using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curfew.App;

/// <summary>
/// First-run setup wizard (Fluent + Mica). The parent always makes explicit
/// choices here: an administrator PIN, whether a daily hour limit is enforced,
/// whether a weekly schedule is enforced, the content-filter level, DNS-over-HTTPS
/// blocking and Time Manipulation Guarding.
/// </summary>
/// <remarks>
/// Pressing <c>Continue</c> validates the PIN, writes every choice to the
/// <see cref="SettingsStore"/>, marks setup as complete and closes the window.
/// Nothing is persisted until validation passes, so cancelling (closing the
/// window) leaves the store untouched.
/// </remarks>
public sealed partial class SetupWindow : Window
{
    /// <summary>Minimum passcode length; any characters (PIN or password) are allowed.</summary>
    private const int PinLength = PasscodeHash.MinLength;

    /// <summary>Hours-per-day applied when the <c>NumberBox</c> value is blank/NaN.</summary>
    private const double DefaultHoursPerDay = 2.0;

    /// <summary>Inclusive bounds for the daily hour budget (matches the XAML NumberBox).</summary>
    private const double MinHoursPerDay = 0.0;
    private const double MaxHoursPerDay = 24.0;

    private const int MinutesPerHour = 60;

    /// <summary>Initial client size of the wizard, in DIPs.</summary>
    private static readonly Windows.Graphics.SizeInt32 WindowSize = new(560, 720);

    /// <summary>Number of editable weekdays (Monday … Sunday).</summary>
    private const int DayCount = 7;

    private readonly SettingsStore _settings;

    /// <summary>Per-day hour spinners shown under Advanced; built in <see cref="BuildPerDayLimits"/>.</summary>
    private readonly NumberBox[] _perDay = new NumberBox[DayCount];

    /// <summary>True once Advanced has been revealed, so per-day values take over from the single figure.</summary>
    private bool _advanced;

    /// <summary>Creates the wizard bound to the store its choices are written to.</summary>
    /// <param name="settings">Destination for every configured value. Never <c>null</c>.</param>
    public SetupWindow(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();
        ApplyWindowChrome();
        BuildPerDayLimits();
    }

    /// <summary>Creates the seven per-day hour spinners shown under Advanced.</summary>
    private void BuildPerDayLimits()
    {
        for (var i = 0; i < DayCount; i++)
        {
            var box = new NumberBox
            {
                Header = Loc.T($"day.{i}"),
                Minimum = MinHoursPerDay,
                Maximum = MaxHoursPerDay,
                SmallChange = 0.25,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Value = DefaultHoursPerDay,
            };
            _perDay[i] = box;
            DailyPerDayPanel.Children.Add(box);
        }
    }

    /// <summary>
    /// Reveals (or hides) the advanced options. On first reveal the per-day spinners
    /// inherit the single hours/day figure so they start from a sensible baseline.
    /// </summary>
    private void OnToggleAdvanced(object sender, RoutedEventArgs e)
    {
        if (!_advanced)
        {
            _advanced = true;
            var hours = double.IsNaN(HoursPerDay.Value) ? DefaultHoursPerDay : HoursPerDay.Value;
            foreach (var box in _perDay)
                box.Value = hours;
            AdvancedPanel.Visibility = Visibility.Visible;
        }
        else
        {
            AdvancedPanel.Visibility = AdvancedPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnPresetChild(object sender, RoutedEventArgs e) => ApplyPreset(1, blockFromHour: 19, blockToHour: 7);
    private void OnPresetTeen(object sender, RoutedEventArgs e) => ApplyPreset(3, blockFromHour: 22, blockToHour: 6);

    /// <summary>
    /// One-tap preset: enables the daily limit + a bedtime schedule, sets the hours,
    /// blocks the overnight window [blockFromHour..blockToHour) and reveals the
    /// advanced panel so the parent can review/tweak the applied schedule.
    /// </summary>
    private void ApplyPreset(double hours, int blockFromHour, int blockToHour)
    {
        LimitEnabled.IsOn = true;
        HoursPerDay.Value = hours;
        ScheduleEnabled.IsOn = true;

        var schedule = Schedule.AllAllowed();
        for (var d = 0; d < Schedule.Days; d++)
            for (var s = 0; s < Schedule.SlotsPerDay; s++)
            {
                var hour = s * Schedule.SlotMinutes / 60.0;
                var blocked = hour >= blockFromHour || hour < blockToHour;
                schedule.SetSlot(d, s, !blocked);
            }
        ScheduleGridControl.Load(schedule);

        _advanced = true;
        foreach (var box in _perDay) box.Value = hours;
        AdvancedPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Applies the title, Mica backdrop, custom title bar and rounded corners.</summary>
    private void ApplyWindowChrome()
    {
        AppWindow.Resize(WindowSize);
        WindowEffects.Apply(this, Loc.T("setup.title"), TitleBar);
    }

    /// <summary>
    /// Handles the <c>Continue</c> button: validates input, then commits all
    /// settings and closes. Bound from XAML (<c>Click="OnContinue"</c>).
    /// </summary>
    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!TryReadValidatedPin(out var pin))
            return;

        ClearError();
        ConfigBridge.ResetWriteStatus();
        PersistConfiguration(pin);

        // Every first-run value (the passcode hash and setup_complete included) is
        // written through the service over the config pipe. If any of those writes
        // could not reach the service, the device would be left unprotected
        // (HasPasscode==false) while the wizard claimed success — so keep the window
        // open and tell the parent to retry once the service is reachable.
        if (!ConfigBridge.LastWriteOk)
        {
            ShowError(Loc.T("settings.err.savefailed"));
            return;
        }

        Close();
    }

    /// <summary>
    /// Reads and validates the PIN pair. On failure it surfaces an inline error
    /// and returns <c>false</c>; on success <paramref name="pin"/> holds the
    /// confirmed 4-digit value.
    /// </summary>
    private bool TryReadValidatedPin(out string pin)
    {
        pin = PinBox.Password;

        if (pin.Length < PinLength)
        {
            ShowError(Loc.T("setup.err.pinlen", PinLength));
            return false;
        }

        if (pin != ConfirmBox.Password)
        {
            ShowError(Loc.T("setup.err.pinmatch"));
            return false;
        }

        return true;
    }

    /// <summary>Writes every wizard choice to the store and marks setup complete.</summary>
    private void PersistConfiguration(string pin)
    {
        // First run writes config through the service too (config.db is read-only).
        // The new PIN authorises the writes; the service lets the very first
        // passcode through before any passcode exists (bootstrap).
        ConfigBridge.Passcode = pin;
        ConfigBridge.Attach(_settings);

        _settings.Set("passcode", PasscodeHash.Hash(pin));
        _settings.Set("limit_enabled", ToFlag(LimitEnabled.IsOn));
        _settings.Set("schedule_enabled", ToFlag(ScheduleEnabled.IsOn));
        _settings.Set("schedule", ScheduleGridControl.ToSchedule().Serialize());

        SaveDailyLimits();

        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(SelectedFilterMode()));
        _settings.Set("block_doh_bypass", ToFlag(BlockDoh.IsOn));
        _settings.Set("time_guard_enabled", ToFlag(TimeGuard.IsOn));

        // Seed the offline unlock-code secret so the feature is ready to enrol
        // in an authenticator app from Settings.
        if (string.IsNullOrEmpty(_settings.Get("unlock_secret")))
            _settings.Set("unlock_secret", Curfew.Core.Security.UnlockCode.GenerateSecret());

        _settings.Set("setup_complete", "1");
    }

    /// <summary>
    /// Writes the daily budget. In simple mode the single hours/day figure applies
    /// to every weekday; once Advanced has been used, each day keeps its own value.
    /// </summary>
    private void SaveDailyLimits()
    {
        if (_advanced)
        {
            for (var i = 0; i < DayCount; i++)
                _settings.Set(SettingsStore.WeekdayKeys[i], HoursToMinutes(_perDay[i].Value).ToString());
            return;
        }

        var value = ChosenDailyMinutes().ToString();
        foreach (var key in SettingsStore.WeekdayKeys)
            _settings.Set(key, value);
    }

    /// <summary>A NumberBox hours value clamped to range and rounded to whole minutes.</summary>
    private static int HoursToMinutes(double hours)
    {
        if (double.IsNaN(hours))
            hours = DefaultHoursPerDay;
        hours = Math.Clamp(hours, MinHoursPerDay, MaxHoursPerDay);
        return (int)Math.Round(hours * MinutesPerHour);
    }

    /// <summary>The daily budget from the single NumberBox, clamped and rounded to whole minutes.</summary>
    private int ChosenDailyMinutes() => HoursToMinutes(HoursPerDay.Value);

    /// <summary>Maps the content-filter radio group to a <see cref="FilterMode"/>.</summary>
    private FilterMode SelectedFilterMode()
    {
        if (FilterMalware.IsChecked == true) return FilterMode.Malware;
        if (FilterFamily.IsChecked == true) return FilterMode.Family;
        return FilterMode.Off;
    }

    /// <summary>Renders a boolean as the store's "1"/"0" flag convention.</summary>
    private static string ToFlag(bool on) => on ? "1" : "0";

    /// <summary>Shows an inline validation message beneath the form.</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Hides any previously shown validation message.</summary>
    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
