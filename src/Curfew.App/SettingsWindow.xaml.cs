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

/// <summary>
/// Settings editor (Fluent + Mica). Daily limits are edited in hours but stored
/// in minutes; the time budget and the weekly schedule are independently
/// toggleable. Saving validates the passcode change first and aborts the whole
/// save if it fails, so the dialog stays open for correction.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const int WindowWidth = 680;
    private const int WindowHeight = 880;

    /// <summary>Daily limit used when a row is blank or a stored value is missing.</summary>
    private const int DefaultDailyMinutes = 120;

    /// <summary>Number of editable weekdays (Monday … Sunday).</summary>
    private const int DayCount = 7;

    /// <summary>Minimum passcode length; any characters (PIN or password) are allowed.</summary>
    private const int PasscodeLength = PasscodeHash.MinLength;

    /// <summary>Window width at or above which cards reflow into two columns.</summary>
    private const double TwoColumnWidth = 1040;

    /// <summary>Window width at or above which cards reflow into three columns.</summary>
    private const double ThreeColumnWidth = 1480;

    /// <summary>Upper bound on the update download (installer is ~95 MB); guards against a hostile asset.</summary>
    private const long MaxInstallerBytes = 150_000_000;

    private readonly SettingsStore _settings;

    /// <summary>The seven per-day hour spinners, created in <see cref="LoadDailyLimits"/>.</summary>
    private readonly NumberBox[] _dailyLimits = new NumberBox[DayCount];

    /// <summary>Section cards in display order, distributed across columns by <see cref="Relayout"/>.</summary>
    private FrameworkElement[] _cards = System.Array.Empty<FrameworkElement>();

    /// <summary>Column count applied by the last <see cref="Relayout"/>, to skip redundant work.</summary>
    private int _columns;

    /// <summary>The newer release found by the last successful check, enabling "Update now".</summary>
    private ReleaseInfo? _pendingUpdate;

    /// <summary>The running build's version, stamped at publish time (e.g. "1.5.0").</summary>
    private static string CurrentVersion =>
        typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public SettingsWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
        WindowEffects.Apply(this, Loc.T("settings.title"), TitleBar);

        InitCards();
        Load();

        // Default to a maximised window so the cards reflow into the full 3 columns;
        // the user can restore it down to collapse back to 2 or 1 column.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    /// <summary>
    /// Detaches the declaratively-defined section cards from their authoring host
    /// and starts watching the window size so they can reflow across 1–3 columns.
    /// </summary>
    private void InitCards()
    {
        _cards = new FrameworkElement[]
        {
            UsageExpander, DailyLimitsExpander, ScheduleExpander, WarningsExpander,
            LockExpander, FilterExpander, ProtectionExpander, UnlockExpander, PasscodeExpander,
        };
        CardSource.Children.Clear();

        RootGrid.SizeChanged += (_, e) => Relayout(e.NewSize.Width);
        Relayout(WindowWidth);
    }

    /// <summary>
    /// Re-parents the cards into one, two or three columns depending on the window
    /// width, and widens the centred content area to match. Cards keep their
    /// natural display order, filling columns round-robin.
    /// </summary>
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

    /// <summary>Populates every control from the persisted settings.</summary>
    private void Load()
    {
        LimitEnabled.IsOn = _settings.GetBool("limit_enabled", true);
        ScheduleEnabled.IsOn = _settings.GetBool("schedule_enabled", false);
        Schedule.Load(Curfew.Core.Schedule.Parse(_settings.Get("schedule")));

        LoadDailyLimits();
        LoadWarnings();
        LoadLockScreen();
        LoadContentFilter();
        LoadProtection();
        LoadUnlock();
        LoadUsageHistory();
        UpdateStatus.Text = Loc.T("settings.update.current", CurrentVersion);
    }

    /// <summary>Draws a 7-day bar chart of active screen time from usage history.</summary>
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

    private void LoadUnlock()
    {
        var secret = _settings.Get("unlock_secret");
        if (string.IsNullOrEmpty(secret))
        {
            secret = UnlockCode.GenerateSecret();
            _settings.Set("unlock_secret", secret);
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

    /// <summary>Renders the enrolment URI as a QR bitmap. Best-effort: failures leave the image blank.</summary>
    private async void RenderQr(string uri)
    {
        // Clear first so a failed rerender (e.g. after regenerating the secret)
        // leaves the image blank instead of showing the previous secret's QR.
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
            // A QR rendering failure is cosmetic — the secret/URI are still shown under Configure.
        }
    }

    /// <summary>
    /// Reveals or hides the advanced unlock-code details (bonus, secret, regenerate).
    /// The button itself flips to an accent "Done" state while open and back again,
    /// so a second press visibly undoes the first.
    /// </summary>
    private void OnToggleUnlockAdvanced(object sender, RoutedEventArgs e)
    {
        var show = UnlockAdvanced.Visibility != Visibility.Visible;
        UnlockAdvanced.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UnlockConfigureButton.Content = Loc.T(show ? "settings.unlock.close" : "settings.unlock.configure");
        UnlockConfigureButton.Style = (Style)Application.Current.Resources[
            show ? "AccentButtonStyle" : "DefaultButtonStyle"];
    }

    /// <summary>Issues a fresh secret and resets the replay counter so old codes stop working.</summary>
    private void OnRegenerateUnlock(object sender, RoutedEventArgs e)
    {
        var secret = UnlockCode.GenerateSecret();
        _settings.Set("unlock_secret", secret);
        _settings.Set("unlock_last_counter", string.Empty);
        ShowUnlockSecret(secret);
    }

    /// <summary>Builds the seven per-day hour spinners and appends them to the panel.</summary>
    private void LoadDailyLimits()
    {
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
        // Stored in seconds, edited in minutes.
        LockTimeout.Value = _settings.GetInt("lock_screen_timeout", 600) / 60;
        IdleEnabled.IsOn = _settings.GetBool("idle_enabled", true);
        IdleTimeout.Value = _settings.GetInt("idle_timeout_minutes", 5);
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
    }

    private void LoadProtection()
    {
        TimeGuard.IsOn = _settings.GetBool("time_guard_enabled", true);
        AutoUpdate.IsOn = _settings.GetBool("auto_update_enabled", true);
        // Default to the stable channel; pre-releases are opt-in.
        UpdateChannel.SelectedIndex = _settings.Get("update_channel") == "prerelease" ? 1 : 0;
    }

    /// <summary>The chosen update channel's tag ("stable"/"prerelease").</summary>
    private string SelectedChannel() =>
        (UpdateChannel.SelectedItem as ComboBoxItem)?.Tag as string ?? "stable";

    /// <summary>
    /// Queries GitHub for a newer release and reports the result inline. On success
    /// it remembers the release so <see cref="OnUpdateNow"/> can install it.
    /// (Automatic checks still run in the service on boot and every six hours.)
    /// </summary>
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

    /// <summary>
    /// Downloads the pending release's installer and launches it elevated (silent),
    /// then closes Settings. The installer stops and restarts the Curfew service.
    /// </summary>
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

            // UseShellExecute + runas raises the UAC prompt the installer needs.
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
            // Download error or the user dismissed the UAC prompt.
            UpdateStatus.Text = Loc.T("settings.update.failed");
            CheckUpdateButton.IsEnabled = true;
            UpdateNowButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Streams the installer to the temp folder, rejecting anything from an
    /// untrusted host, too large, too small, or not a Windows executable. Returns
    /// the path, or null on failure (any partial download is deleted).
    /// </summary>
    /// <remarks>
    /// The asset is fetched over HTTPS from GitHub, which authenticates the source.
    /// The installer is not Authenticode-signed and no hash is published, so signer
    /// or hash-pin verification is not yet possible; <see cref="IsTrustedInstallerUrl"/>
    /// plus the size and PE-header checks are the available defences.
    /// </remarks>
    private static async Task<string?> DownloadInstallerAsync(string url)
    {
        // The initial URL must be THIS repo's pinned HTTPS release path, not merely
        // some github.com address: otherwise any other account's release asset named
        // curfew-setup*.exe would be accepted and then launched elevated. The
        // post-redirect URL is re-checked host-only below (it lands on the asset CDN).
        if (!ReleaseInfo.IsInstallerUrl(url)) return null;

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("curfew-updater");

        // Stage under an app-specific dir with an unguessable name so another
        // same-user process cannot pre-create or swap the file between the download
        // and the elevated launch (TOCTOU). CreateNew fails if the name ever clashes.
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Curfew");
        Directory.CreateDirectory(tempDir);
        var path = System.IO.Path.Combine(tempDir, $"curfew-update-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // HttpClient follows redirects (github.com -> *.githubusercontent.com),
            // so re-validate the URL actually fetched, not just the input.
            if (response.RequestMessage?.RequestUri is { } finalUri && !IsTrustedInstallerUrl(finalUri.ToString()))
                return null;

            if (response.Content.Headers.ContentLength is long advertised && advertised > MaxInstallerBytes)
                return null;

            // Stream straight to disk (capped) rather than buffering ~95 MB in memory.
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var chunk = new byte[81_920];

                // Validate the "MZ" PE signature on the first chunk before writing more.
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

            // Refuse to hand an installer to the elevated launch unless it is
            // Authenticode-signed by Curfew's own key. URL/host pinning guards where
            // it came from; this guards what it actually is.
            if (!Curfew.Core.Security.InstallerSignature.Verify(path))
                throw new InvalidDataException("installer is not signed by Curfew's key");

            return path;
        }
        catch
        {
            // Network/HTTP error, oversize, or a failed validation: drop the partial file.
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>
    /// Whether the installer URL is an HTTPS GitHub address, so the elevated launch
    /// can only ever run something fetched from the release host.
    /// </summary>
    private static bool IsTrustedInstallerUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Save handler wired to the Save button. Validates the optional passcode
    /// change first; if it fails the dialog stays open and nothing is persisted.
    /// </summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        ClearError();
        if (!TrySavePasscode()) return;

        SaveToggles();
        SaveDailyLimits();
        SaveWarnings();
        SaveLockScreen();
        SaveContentFilter();
        SaveProtection();
        _settings.Set("unlock_bonus_minutes", Clamp(UnlockBonus, 1, 600, 30).ToString());

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
        // Edited in minutes, stored in seconds.
        _settings.Set("lock_screen_timeout", (Clamp(LockTimeout, 1, 720, 10) * 60).ToString());
        _settings.Set("idle_enabled", ToFlag(IdleEnabled.IsOn));
        _settings.Set("idle_timeout_minutes", Clamp(IdleTimeout, 1, 600, 5).ToString());
    }

    private void SaveContentFilter()
    {
        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;
        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", ToFlag(BlockDoh.IsOn));
    }

    private void SaveProtection()
    {
        _settings.Set("time_guard_enabled", ToFlag(TimeGuard.IsOn));
        _settings.Set("auto_update_enabled", ToFlag(AutoUpdate.IsOn));
        _settings.Set("update_channel", SelectedChannel());
    }

    /// <summary>
    /// Validates and persists a passcode change. A change is only attempted when
    /// at least one of the New/Confirm boxes is non-empty; an all-blank trio means
    /// "keep the current passcode" and succeeds without touching settings.
    /// </summary>
    /// <returns><c>true</c> when nothing needs changing or the change is valid and saved.</returns>
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

    /// <summary>Cancel handler wired to the Cancel button; discards all edits.</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>Reads a <see cref="NumberBox"/> as a clamped integer, substituting
    /// <paramref name="fallback"/> when the box is empty (its value is NaN).</summary>
    private static int Clamp(NumberBox box, int min, int max, int fallback)
    {
        var value = double.IsNaN(box.Value) ? fallback : (int)box.Value;
        return Math.Clamp(value, min, max);
    }

    /// <summary>Converts whole minutes to hours, rounded to the spinner's precision.</summary>
    private static double MinutesToHours(int minutes) => Math.Round(minutes / 60.0, 2);

    /// <summary>Serializes a toggle state to the "1"/"0" persisted flag.</summary>
    private static string ToFlag(bool on) => on ? "1" : "0";

    /// <summary>Null-safe trimmed text for a <see cref="TextBox"/>.</summary>
    private static string TrimmedText(TextBox box) => (box.Text ?? "").Trim();

    /// <summary>Shows a validation error beneath the form.</summary>
    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Hides any previously shown validation error.</summary>
    private void ClearError()
    {
        StatusText.Text = "";
        StatusText.Visibility = Visibility.Collapsed;
    }
}
