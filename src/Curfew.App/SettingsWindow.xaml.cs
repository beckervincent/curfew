using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using QRCoder;
using Windows.Storage.Streams;
using Windows.UI;

namespace Curfew.App;

/// <summary>settings editor (Fluent + Mica). daily limits edited in hours, stored in minutes; budget and schedule toggle independently. save validates passcode change first, aborts whole save on fail</summary>
public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth = 680;
    private const int WindowHeight = 880;

    /// <summary>daily limit when row blank or stored value missing</summary>
    private const int DefaultDailyMinutes = 120;

    /// <summary>editable weekdays (Mon..Sun)</summary>
    private const int DayCount = 7;

    /// <summary>min passcode length; any chars (PIN or password)</summary>
    private const int PasscodeLength = PasscodeHash.MinLength;

    /// <summary>width at/above which cards reflow into 2 columns</summary>
    private const double TwoColumnWidth = 1040;

    /// <summary>width at/above which cards reflow into 3 columns</summary>
    private const double ThreeColumnWidth = 1480;

    /// <summary>cap on update download (installer ~95MB); guards hostile asset</summary>
    private const long MaxInstallerBytes = 150_000_000;

    private readonly SettingsStore _settings;

    /// <summary>seven per-day hour spinners, built in <see cref="LoadDailyLimits"/></summary>
    private readonly NumberBox[] _dailyLimits = new NumberBox[DayCount];

    /// <summary>section cards in display order, spread across columns by <see cref="Relayout"/></summary>
    private FrameworkElement[] _cards = System.Array.Empty<FrameworkElement>();

    /// <summary>column count from last <see cref="Relayout"/>, to skip redundant work</summary>
    private int _columns;

    /// <summary>newer release from last check, enables "Update now"</summary>
    private ReleaseInfo? _pendingUpdate;

    /// <summary>running build version, stamped at publish (e.g. "1.5.0")</summary>
    private static string CurrentVersion =>
        typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public SettingsWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        // config writes go through SYSTEM service (config.db read-only here)
        ConfigBridge.Attach(_settings);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
        WindowEffects.Apply(this, Loc.T("settings.title"), TitleBar);

        InitCards();
        Load();

        // start maximised so cards reflow to full 3 columns; restore down collapses to 2 or 1
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    /// <summary>detach section cards from authoring host, watch window size to reflow across 1-3 columns</summary>
    private void InitCards()
    {
        _cards = new FrameworkElement[]
        {
            UsageExpander, ActivityExpander, DailyLimitsExpander, ScheduleExpander, WarningsExpander,
            LockExpander, PauseExpander, FilterExpander, ProtectionExpander, UnlockExpander, PasscodeExpander,
        };
        CardSource.Children.Clear();

        RootGrid.SizeChanged += (_, e) => Relayout(e.NewSize.Width);
        Relayout(WindowWidth);
    }

    /// <summary>re-parent cards into 1/2/3 columns by width, widen centred area to match; cards keep order, fill round-robin</summary>
    private void Relayout(double width)
    {
        var columns = width >= ThreeColumnWidth ? 3 : width >= TwoColumnWidth ? 2 : 1;
        if (columns == _columns) return;
        _columns = columns;

        ColumnA.Children.Clear();
        ColumnB.Children.Clear();
        ColumnC.Children.Clear();
        var lanes = new[] { ColumnA, ColumnB, ColumnC };

        for (var i = 0; i < _cards.Length; i++)
            lanes[i % columns].Children.Add(_cards[i]);

        for (var c = 0; c < 3; c++)
            CardsHost.ColumnDefinitions[c].Width = c < columns
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

        ContentRoot.MaxWidth = columns == 3 ? 1700 : columns == 2 ? 1160 : 680;
    }

    /// <summary>fill every control from persisted settings</summary>
    private void Load()
    {
        // First: pick the active user and set the scope, so every per-user control
        // below loads (and later saves) that user's values rather than the device
        // default — see PopulateUserPicker for why the default matters.
        PopulateUserPicker();

        LimitEnabled.IsOn = _settings.GetBool("limit_enabled", true);
        ScheduleEnabled.IsOn = _settings.GetBool("schedule_enabled", false);
        Schedule.Load(Curfew.Core.Schedule.Parse(_settings.Get("schedule")));

        LoadDailyLimits();
        LoadWarnings();
        LoadLockScreen();
        LoadPause();
        LoadContentFilter();
        LoadProtection();
        LoadUnlock();
        LoadUsageHistory();
        LoadAppUsage();
        LoadActivity();
        UpdateStatus.Text = Loc.T("settings.update.current", CurrentVersion);
    }

    /// <summary>guards user-picker handler during programmatic populate</summary>
    private bool _loadingUser;

    /// <summary>fill picker with "All users" plus each Windows user with recorded usage</summary>
    private void PopulateUserPicker()
    {
        _loadingUser = true;
        UserPicker.Items.Clear();
        UserPicker.Items.Add(new ComboBoxItem { Content = Loc.T("settings.user.all"), Tag = string.Empty });

        var sids = _settings.UsersWithHistory();
        foreach (var sid in sids)
            UserPicker.Items.Add(new ComboBoxItem { Content = ResolveUserName(sid), Tag = sid });

        // Default to the user whose session this is — almost always the child whose PC
        // this is (Settings is opened from their tray, behind the PIN). Editing that
        // user writes their per-user limit keys, which the overlay running in their
        // session actually reads. The "All users" entry edits only the device-wide
        // defaults, and every provisioned user has per-user limits (written at setup)
        // that shadow those defaults — so leaving "All users" selected made a time
        // change silently fail to affect the live session. Falls back to "All users"
        // when the current user has no per-user row yet (e.g. Settings opened from a
        // separate admin account).
        var current = CurrentSessionSid();
        var match = -1;
        if (!string.IsNullOrEmpty(current))
            for (var i = 0; i < sids.Count; i++)
                if (string.Equals(sids[i], current, StringComparison.OrdinalIgnoreCase)) { match = i; break; }

        UserPicker.SelectedIndex = match >= 0 ? match + 1 : 0; // +1: "All users" is slot 0
        var tag = (UserPicker.SelectedItem as ComboBoxItem)?.Tag as string;
        _settings.UserSid = string.IsNullOrEmpty(tag) ? null : tag;

        _loadingUser = false;
    }

    /// <summary>SID of the Windows session Settings runs in, or empty on failure</summary>
    private static string CurrentSessionSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>resolve SID to display name, fall back to raw SID</summary>
    private static string ResolveUserName(string sid)
    {
        try
        {
            var name = new System.Security.Principal.SecurityIdentifier(sid)
                .Translate(typeof(System.Security.Principal.NTAccount)).Value;
            var slash = name.LastIndexOf('\\');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
        catch
        {
            return sid;
        }
    }

    /// <summary>re-scope per-user controls to selected user</summary>
    private void OnUserChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUser) return;

        var sid = (UserPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        _settings.UserSid = string.IsNullOrEmpty(sid) ? null : sid;

        // reload only per-user controls; device-wide (passcode, device code, allow-list, updates) unaffected
        LimitEnabled.IsOn = _settings.GetBool("limit_enabled", true);
        ScheduleEnabled.IsOn = _settings.GetBool("schedule_enabled", false);
        Schedule.Load(Curfew.Core.Schedule.Parse(_settings.Get("schedule")));
        LoadDailyLimits();
        LoadWarnings();
        LoadLockScreen();
        LoadPause();
        LoadContentFilter();
        LoadUsageHistory();
        LoadAppUsage();
    }

    /// <summary>draw 7-day bar chart of active screen time from usage history</summary>
    private void LoadUsageHistory()
    {
        var history = _settings.GetUsageHistory(7);
        var max = Math.Max(1, history.Count == 0 ? 1 : history.Max(h => h.Minutes));
        const double maxBarHeight = 104;

        var accent = new SolidColorBrush(Color.FromArgb(0xFF, 0x4C, 0xA0, 0xF0));
        var muted = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));

        UsageChart.ColumnDefinitions.Clear();
        UsageChart.Children.Clear();

        for (var i = 0; i < history.Count; i++)
        {
            UsageChart.ColumnDefinitions.Add(new ColumnDefinition());
            var day = history[i];

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition());                            // bar area
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // day label
            Grid.SetColumn(column, i);

            var bars = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 4,
            };
            bars.Children.Add(new TextBlock
            {
                Text = FormatUsage(day.Minutes),
                FontSize = 11,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            bars.Children.Add(new Rectangle
            {
                Width = 28,
                Height = Math.Max(2, day.Minutes / (double)max * maxBarHeight),
                RadiusX = 4,
                RadiusY = 4,
                Fill = accent,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            Grid.SetRow(bars, 0);

            var name = Loc.T($"day.{TimeMath.MondayBasedWeekday(day.Date)}");
            var label = new TextBlock
            {
                Text = name.Length >= 2 ? name[..2] : name,
                FontSize = 11,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            Grid.SetRow(label, 1);

            column.Children.Add(bars);
            column.Children.Add(label);
            UsageChart.Children.Add(column);
        }
    }

    private static string FormatUsage(int minutes)
    {
        if (minutes <= 0) return "0";
        return minutes < 60
            ? Loc.T("settings.history.minutes", minutes)
            : Loc.T("settings.history.hours", minutes / 60, minutes % 60);
    }

    /// <summary>list the apps that used the most screen time this week (per the picked user)</summary>
    private void LoadAppUsage()
    {
        var apps = _settings.AppUsageThisWeek(DateOnly.FromDateTime(DateTime.Now));
        AppUsageList.Items.Clear();
        AppUsageEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var muted = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));
        foreach (var (name, minutes) in apps.Take(10))
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 0);
            var value = new TextBlock { Text = FormatUsage(minutes), Foreground = muted };
            Grid.SetColumn(value, 1);

            row.Children.Add(label);
            row.Children.Add(value);
            AppUsageList.Items.Add(row);
        }
    }

    /// <summary>fill activity list from most recent event-log entries</summary>
    private void LoadActivity()
    {
        var events = EventLog.ReadRecent(CurfewPaths.EventLogFile, 25);
        ActivityList.Items.Clear();
        ActivityEmpty.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var alert = new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0x6C, 0x6C));
        var muted = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88));

        foreach (var ev in events)
        {
            var isAlert = ev.Kind is CurfewEventKind.ClockTamper
                or CurfewEventKind.FailedUnlock or CurfewEventKind.FilterFailure;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Padding = new Thickness(0, 2, 0, 2),
            };
            row.Children.Add(new TextBlock
            {
                Text = ev.Time.ToLocalTime().ToString("dd.MM HH:mm"),
                FontSize = 12,
                Foreground = muted,
                Width = 96,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var label = Loc.T($"event.{ev.Kind}");
            var line = new TextBlock
            {
                Text = string.IsNullOrEmpty(ev.Detail) ? label : $"{label} — {ev.Detail}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (isAlert) line.Foreground = alert;
            row.Children.Add(line);

            ActivityList.Items.Add(row);
        }
    }

    private void LoadUnlock()
    {
        var secret = _settings.Get("unlock_secret");
        if (string.IsNullOrEmpty(secret))
        {
            // seed routes through SYSTEM service. if write never lands, device has no secret — don't show a generated one the parent would enrol but lock screen rejects
            ConfigBridge.ResetWriteStatus();
            secret = UnlockCode.GenerateSecret();
            _settings.Set("unlock_secret", secret);
            if (!ConfigBridge.LastWriteOk)
            {
                ShowError(Loc.T("settings.err.savefailed"));
                return;
            }
        }
        ShowUnlockSecret(secret);
        UnlockBonus.Value = _settings.GetInt("unlock_bonus_minutes", 30);
    }

    private void ShowUnlockSecret(string secret)
    {
        var uri = $"otpauth://totp/Curfew:Device?secret={secret}&issuer=Curfew&digits=6&period=30";
        UnlockSecret.Text = secret;
        UnlockUri.Text = uri;
        RenderQr(uri);
    }

    /// <summary>render enrolment URI as QR bitmap. best-effort: failure leaves image blank</summary>
    private async void RenderQr(string uri)
    {
        // clear first so a failed rerender (e.g. after regen) blanks instead of showing previous secret's QR
        UnlockQr.Source = null;
        try
        {
            var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(10);

            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            UnlockQr.Source = bitmap;
        }
        catch
        {
            // QR render fail is cosmetic — secret/URI still shown under Configure
        }
    }

    /// <summary>toggle advanced unlock-code details (bonus, secret, regenerate). button flips to accent "Done" while open so second press visibly undoes first</summary>
    private void OnToggleUnlockAdvanced(object sender, RoutedEventArgs e)
    {
        var show = UnlockAdvanced.Visibility != Visibility.Visible;
        UnlockAdvanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UnlockConfigureButton.Content = Loc.T(show ? "settings.unlock.close" : "settings.unlock.configure");
        UnlockConfigureButton.Style = (Style)Application.Current.Resources[
            show ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    /// <summary>issue fresh secret, reset replay counter so old codes stop working</summary>
    private void OnRegenerateUnlock(object sender, RoutedEventArgs e)
    {
        ClearError();

        // write goes through SYSTEM service. on fail the stored secret has NOT rotated — don't show the new one (parent would enrol a secret device never accepted) and don't zero replay counter against an unchanged secret
        ConfigBridge.ResetWriteStatus();
        var secret = UnlockCode.GenerateSecret();
        _settings.Set("unlock_secret", secret);
        if (!ConfigBridge.LastWriteOk)
        {
            ShowError(Loc.T("settings.err.savefailed"));
            return;
        }

        _settings.Set("unlock_last_counter", string.Empty);
        ShowUnlockSecret(secret);
    }

    /// <summary>build seven per-day hour spinners, append to panel</summary>
    private void LoadDailyLimits()
    {
        DailyLimitsPanel.Children.Clear();   // re-runnable when picked user changes
        for (var i = 0; i < DayCount; i++)
        {
            var minutes = _settings.GetInt(SettingsStore.WeekdayKeys[i], DefaultDailyMinutes);
            var box = new NumberBox
            {
                Header = Loc.T($"day.{i}"),
                Minimum = 0,
                Maximum = 24,
                SmallChange = 0.25,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Value = MinutesToHours(minutes),
            };
            _dailyLimits[i] = box;
            DailyLimitsPanel.Children.Add(box);
        }

        AppAllowlistBox.Text = _settings.Get("app_allowlist") ?? string.Empty;
        BlockedAppsBox.Text = _settings.Get("blocked_apps") ?? string.Empty;
        AppTimeLimitsBox.Text = _settings.Get("app_time_limits") ?? string.Empty;
        AppWeeklyLimitsBox.Text = _settings.Get("app_weekly_limits") ?? string.Empty;

        // weekly cap: stored in minutes, edited in hours
        WeeklyLimitEnabled.IsOn = _settings.GetBool("weekly_limit_enabled", false);
        WeeklyLimit.Value = MinutesToHours(_settings.GetInt("weekly_limit_minutes", 0));
    }

    private void LoadWarnings()
    {
        Warn1Min.Value = _settings.GetInt("warning1_minutes", 10);
        Warn1Msg.Text = _settings.Get("warning1_message") ?? "";
        Warn2Min.Value = _settings.GetInt("warning2_minutes", 5);
        Warn2Msg.Text = _settings.Get("warning2_message") ?? "";
        BlockingMsg.Text = _settings.Get("blocking_message") ?? "";
    }

    private void LoadLockScreen()
    {
        // stored in seconds, edited in minutes
        LockTimeout.Value = _settings.GetInt("lock_screen_timeout", 600) / 60;
        IdleEnabled.IsOn = _settings.GetBool("idle_enabled", true);
        IdleTimeout.Value = _settings.GetInt("idle_timeout_minutes", 5);
        WindDown.Value = _settings.GetInt("wind_down_minutes", 10);
        EyeStrainEnabled.IsOn = _settings.GetBool("eyestrain_enabled", false);
        EyeStrainInterval.Value = _settings.GetInt("eyestrain_interval_minutes", 20);
        NewAppAlerts.IsOn = _settings.GetBool("newapp_alerts_enabled", false);
    }

    private void LoadPause()
    {
        // all stored and edited in minutes
        PauseEnabled.IsOn = _settings.GetBool("pause_enabled", true);
        PauseDailyBudget.Value = _settings.GetInt("pause_daily_budget", 45);
        PauseMaxDuration.Value = _settings.GetInt("pause_max_duration", 20);
        PauseCooldown.Value = _settings.GetInt("pause_cooldown", 15);
        PauseMinActive.Value = _settings.GetInt("pause_min_active_time", 10);
    }

    private void LoadContentFilter()
    {
        switch (ContentFilter.Parse(_settings.Get("dns_filter_mode")))
        {
            case FilterMode.Malware: FilterMalware.IsChecked = true; break;
            case FilterMode.Family: FilterFamily.IsChecked = true; break;
            default: FilterOff.IsChecked = true; break;
        }
        BlockDoh.IsOn = _settings.GetBool("block_doh_bypass", true);
        SafeSearch.IsOn = _settings.GetBool("safesearch_enabled", false);
        var cats = BlockCategories.Parse(_settings.Get("blocked_categories"));
        CatSocial.IsChecked = cats.Contains(BlockCategories.Social);
        CatGaming.IsChecked = cats.Contains(BlockCategories.Gaming);
        CatStreaming.IsChecked = cats.Contains(BlockCategories.Streaming);
        CatAdult.IsChecked = cats.Contains(BlockCategories.Adult);
        BlockedDomains.Text = _settings.Get("blocked_domains") ?? string.Empty;
    }

    private void LoadProtection()
    {
        TimeGuard.IsOn = _settings.GetBool("time_guard_enabled", true);
        AutoUpdate.IsOn = _settings.GetBool("auto_update_enabled", true);
        // default stable channel; pre-releases opt-in
        UpdateChannel.SelectedIndex = _settings.Get("update_channel") == "prerelease" ? 1 : 0;
    }

    /// <summary>chosen update channel tag ("stable"/"prerelease")</summary>
    private string SelectedChannel() =>
        (UpdateChannel.SelectedItem as ComboBoxItem)?.Tag as string ?? "stable";

    /// <summary>query GitHub for newer release, report inline. on success remembers release so <see cref="OnUpdateNow"/> can install. (service still auto-checks on boot + every 6h)</summary>
    private async void OnCheckForUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
        UpdateStatus.Text = Loc.T("settings.update.checking");
        try
        {
            var release = await Updater.CheckForUpdateAsync(
                CurrentVersion, Updater.HttpFetchAsync, includePrereleases: SelectedChannel() == "prerelease");
            _pendingUpdate = release;
            if (release is null)
            {
                UpdateStatus.Text = Loc.T("settings.update.uptodate", CurrentVersion);
            }
            else
            {
                UpdateNowButton.IsEnabled = true;
                UpdateStatus.Text = Loc.T("settings.update.available", release.Value.Tag.TrimStart('v', 'V'));
            }
        }
        catch
        {
            UpdateStatus.Text = Loc.T("settings.update.failed");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>download pending installer, launch elevated (silent), close Settings. installer stops + restarts Curfew service</summary>
    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;

        CheckUpdateButton.IsEnabled = false;
        UpdateNowButton.IsEnabled = false;
        UpdateStatus.Text = Loc.T("settings.update.downloading");
        try
        {
            var installer = await DownloadInstallerAsync(_pendingUpdate.Value.InstallerUrl);
            if (installer is null)
            {
                UpdateStatus.Text = Loc.T("settings.update.failed");
                CheckUpdateButton.IsEnabled = true;
                UpdateNowButton.IsEnabled = true;
                return;
            }

            // UseShellExecute + runas raises the UAC prompt installer needs
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/SILENT /SUPPRESSMSGBOXES",
                UseShellExecute = true,
                Verb = "runas",
            });
            Close();
        }
        catch
        {
            // download error or user dismissed UAC prompt
            UpdateStatus.Text = Loc.T("settings.update.failed");
            CheckUpdateButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = true;
        }
    }

    /// <summary>stream installer to temp, reject untrusted host / too big / too small / not a Windows exe. returns path, null on fail (partial deleted)</summary>
    /// <remarks>asset fetched over HTTPS from GitHub (authenticates source). not Authenticode-signed, no published hash, so <see cref="IsTrustedInstallerUrl"/> + size + PE-header checks are the available defences</remarks>
    private static async Task<string?> DownloadInstallerAsync(string url)
    {
        // initial URL must be THIS repo's pinned HTTPS release path, not just any github.com address — else another account's asset named curfew-setup*.exe gets launched elevated. post-redirect URL re-checked host-only below (lands on asset CDN)
        if (!ReleaseInfo.IsInstallerUrl(url)) return null;

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");

        // stage under app-specific dir with unguessable name so a same-user process can't pre-create or swap the file between download and elevated launch (TOCTOU). CreateNew fails on name clash
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Curfew");
        Directory.CreateDirectory(tempDir);
        var path = System.IO.Path.Combine(tempDir, $"curfew-update-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // HttpClient follows redirects (github.com -> *.githubusercontent.com) — re-validate the URL actually fetched, not just input
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsTrustedInstallerUrl(finalUri.ToString()))
                return null;

            if (response.Content.Headers.ContentLength is long advertised && advertised > MaxInstallerBytes)
                return null;

            // stream straight to disk (capped), don't buffer ~95MB in memory
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var chunk = new byte[81_920];

                // validate "MZ" PE signature on first chunk before writing more
                var first = await source.ReadAsync(chunk);
                if (first < 2 || chunk[0] != 0x4D || chunk[1] != 0x5A)
                    throw new InvalidDataException("not a Windows executable");
                await file.WriteAsync(chunk.AsMemory(0, first));

                long total = first;
                int read;
                while ((read = await source.ReadAsync(chunk)) > 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes)
                        throw new InvalidDataException("installer exceeds size cap");
                    await file.WriteAsync(chunk.AsMemory(0, read));
                }

                if (total < 500_000)
                    throw new InvalidDataException("download too small to be the installer");
            }

            // refuse elevated launch unless Authenticode-signed by Curfew's own key. URL/host pinning guards where it came from; this guards what it is
            if (!Curfew.Core.Security.InstallerSignature.Verify(path))
                throw new InvalidDataException("installer is not signed by Curfew's key");

            return path;
        }
        catch
        {
            // network/HTTP error, oversize, or failed validation: drop partial file
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>whether installer URL is an HTTPS GitHub address, so elevated launch only runs something from the release host</summary>
    private static bool IsTrustedInstallerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Save handler. validate optional passcode change first; on fail dialog stays open, nothing persisted</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        ClearError();
        ConfigBridge.ResetWriteStatus();
        if (!TrySavePasscode()) return;

        SaveToggles();
        SaveDailyLimits();
        SaveWarnings();
        SaveLockScreen();
        SavePause();
        SaveContentFilter();
        SaveProtection();
        _settings.Set("unlock_bonus_minutes", Clamp(UnlockBonus, 1, 600, 30).ToString());

        // any config write that didn't reach the service: keep dialog open + tell parent, don't silently lose the change
        if (!ConfigBridge.LastWriteOk)
        {
            ShowError(Loc.T("settings.err.savefailed"));
            return;
        }

        Close();
    }

    private void SaveToggles()
    {
        _settings.Set("limit_enabled", ToFlag(LimitEnabled.IsOn));
        _settings.Set("schedule_enabled", ToFlag(ScheduleEnabled.IsOn));
        _settings.Set("schedule", Schedule.ToSchedule().Serialize());
    }

    private void SaveDailyLimits()
    {
        for (var i = 0; i < DayCount; i++)
        {
            var hours = double.IsNaN(_dailyLimits[i].Value)
                ? MinutesToHours(DefaultDailyMinutes)
                : _dailyLimits[i].Value;
            var minutes = (int)Math.Round(Math.Clamp(hours, 0, 24) * 60);
            _settings.Set(SettingsStore.WeekdayKeys[i], minutes.ToString());
        }

        // apps whose foreground time is exempt from budget. stored raw; overlay parses (AppAllowlist.Parse) when enforcing
        _settings.Set("app_allowlist", AppAllowlistBox.Text ?? string.Empty);
        _settings.Set("blocked_apps", BlockedAppsBox.Text ?? string.Empty);
        // per-app daily limits ("name=minutes" lines); overlay parses (AppTimeLimits.Parse) when enforcing
        _settings.Set("app_time_limits", AppTimeLimitsBox.Text ?? string.Empty);
        // per-app weekly limits ("name=minutes/week" lines)
        _settings.Set("app_weekly_limits", AppWeeklyLimitsBox.Text ?? string.Empty);

        // weekly cap: edited in hours, stored in minutes
        _settings.Set("weekly_limit_enabled", ToFlag(WeeklyLimitEnabled.IsOn));
        var weeklyHours = double.IsNaN(WeeklyLimit.Value) ? 0 : WeeklyLimit.Value;
        _settings.Set("weekly_limit_minutes", ((int)Math.Round(Math.Clamp(weeklyHours, 0, 168) * 60)).ToString());
    }

    private void SaveWarnings()
    {
        _settings.Set("warning1_minutes", Clamp(Warn1Min, 0, 600, 10).ToString());
        _settings.Set("warning1_message", TrimmedText(Warn1Msg));
        _settings.Set("warning2_minutes", Clamp(Warn2Min, 0, 600, 5).ToString());
        _settings.Set("warning2_message", TrimmedText(Warn2Msg));
        _settings.Set("blocking_message", TrimmedText(BlockingMsg));
    }

    private void SaveLockScreen()
    {
        // edited in minutes, stored in seconds
        _settings.Set("lock_screen_timeout", (Clamp(LockTimeout, 1, 720, 10) * 60).ToString());
        _settings.Set("idle_enabled", ToFlag(IdleEnabled.IsOn));
        _settings.Set("idle_timeout_minutes", Clamp(IdleTimeout, 1, 600, 5).ToString());
        _settings.Set("wind_down_minutes", Clamp(WindDown, 0, 120, 10).ToString());
        _settings.Set("eyestrain_enabled", ToFlag(EyeStrainEnabled.IsOn));
        _settings.Set("eyestrain_interval_minutes", Clamp(EyeStrainInterval, 5, 120, 20).ToString());
        _settings.Set("newapp_alerts_enabled", ToFlag(NewAppAlerts.IsOn));
    }

    private void SavePause()
    {
        _settings.Set("pause_enabled", ToFlag(PauseEnabled.IsOn));
        _settings.Set("pause_daily_budget", Clamp(PauseDailyBudget, 0, 600, 45).ToString());
        _settings.Set("pause_max_duration", Clamp(PauseMaxDuration, 1, 240, 20).ToString());
        _settings.Set("pause_cooldown", Clamp(PauseCooldown, 0, 240, 15).ToString());
        _settings.Set("pause_min_active_time", Clamp(PauseMinActive, 0, 240, 10).ToString());
    }

    private void SaveContentFilter()
    {
        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;
        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", ToFlag(BlockDoh.IsOn));
        _settings.Set("safesearch_enabled", ToFlag(SafeSearch.IsOn));
        var cats = new List<string>();
        if (CatSocial.IsChecked == true) cats.Add(BlockCategories.Social);
        if (CatGaming.IsChecked == true) cats.Add(BlockCategories.Gaming);
        if (CatStreaming.IsChecked == true) cats.Add(BlockCategories.Streaming);
        if (CatAdult.IsChecked == true) cats.Add(BlockCategories.Adult);
        _settings.Set("blocked_categories", string.Join(',', cats));
        // normalize to a clean newline-joined list so the stored value round-trips predictably
        _settings.Set("blocked_domains", string.Join('\n', HostsBlocklist.Parse(BlockedDomains.Text)));
    }

    private void SaveProtection()
    {
        _settings.Set("time_guard_enabled", ToFlag(TimeGuard.IsOn));
        _settings.Set("auto_update_enabled", ToFlag(AutoUpdate.IsOn));
        _settings.Set("update_channel", SelectedChannel());
    }

    /// <summary>validate + persist passcode change. only attempted when New/Confirm non-empty; all-blank = keep current, succeeds without touching settings</summary>
    /// <returns><c>true</c> when nothing to change, or change valid and saved</returns>
    private bool TrySavePasscode()
    {
        var newPin = NewPin.Password;
        var confirm = ConfirmPin.Password;
        if (newPin.Length == 0 && confirm.Length == 0) return true;

        if (!PasscodeHash.Verify(CurrentPin.Password, _settings.Get("passcode")))
        {
            ShowError(Loc.T("settings.err.currentwrong"));
            return false;
        }
        if (newPin.Length < PasscodeLength)
        {
            ShowError(Loc.T("settings.err.newlen", PasscodeLength));
            return false;
        }
        if (newPin != confirm)
        {
            ShowError(Loc.T("settings.err.newmatch"));
            return false;
        }

        _settings.Set("passcode", PasscodeHash.Hash(newPin));
        return true;
    }

    /// <summary>Cancel handler; discards all edits</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>read <see cref="NumberBox"/> as clamped int, using <paramref name="fallback"/> when empty (NaN)</summary>
    private static int Clamp(NumberBox box, int min, int max, int fallback)
    {
        var value = double.IsNaN(box.Value) ? fallback : (int)box.Value;
        return Math.Clamp(value, min, max);
    }

    /// <summary>whole minutes to hours, rounded to spinner precision</summary>
    private static double MinutesToHours(int minutes) => Math.Round(minutes / 60.0, 2);

    /// <summary>toggle state to "1"/"0" persisted flag</summary>
    private static string ToFlag(bool on) => on ? "1" : "0";

    /// <summary>null-safe trimmed text for a <see cref="TextBox"/></summary>
    private static string TrimmedText(TextBox box) => (box.Text ?? "").Trim();

    /// <summary>show validation error beneath form</summary>
    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>hide any shown validation error</summary>
    private void ClearError()
    {
        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
    }
}
