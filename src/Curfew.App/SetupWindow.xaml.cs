using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curfew.App;

/// <summary>first-run setup wizard (Fluent + Mica). parent picks: admin PIN, daily hour limit on/off, weekly schedule on/off, content-filter level, DNS-over-HTTPS blocking, Time Manipulation Guarding</summary>
/// <remarks><c>Continue</c> validates PIN, writes every choice to <see cref="SettingsStore"/>, marks setup complete, closes. nothing persisted until validation passes, so cancelling leaves store untouched</remarks>
public sealed partial class SetupWindow : Window
{
    /// <summary>min passcode length; any chars (PIN or password)</summary>
    private const int PinLength = PasscodeHash.MinLength;

    /// <summary>hours/day when <c>NumberBox</c> blank/NaN</summary>
    private const double DefaultHoursPerDay = 2.0;

    /// <summary>inclusive bounds for daily hour budget (matches XAML NumberBox)</summary>
    private const double MinHoursPerDay = 0.0;
    private const double MaxHoursPerDay = 24.0;

    private const int MinutesPerHour = 60;

    /// <summary>initial wizard client size, in DIPs</summary>
    private static readonly Windows.Graphics.SizeInt32 WindowSize = new(560, 720);

    /// <summary>editable weekdays (Mon..Sun)</summary>
    private const int DayCount = 7;

    private readonly SettingsStore _settings;

    /// <summary>per-day hour spinners under Advanced; built in <see cref="BuildPerDayLimits"/></summary>
    private readonly NumberBox[] _perDay = new NumberBox[DayCount];

    /// <summary>true once Advanced revealed, so per-day values override the single figure</summary>
    private bool _advanced;

    /// <summary>wizard bound to the store its choices write to</summary>
    /// <param name="settings">destination for every configured value. never <c>null</c></param>
    public SetupWindow(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();
        ApplyWindowChrome();
        BuildPerDayLimits();
    }

    /// <summary>build seven per-day hour spinners under Advanced</summary>
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

    /// <summary>toggle advanced options. on first reveal per-day spinners inherit the single hours/day figure for a sane baseline</summary>
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

    /// <summary>one-tap preset: enable daily limit + bedtime schedule, set hours, block overnight [blockFromHour..blockToHour), reveal advanced panel for review/tweak</summary>
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

    /// <summary>apply title, Mica backdrop, custom title bar, rounded corners</summary>
    private void ApplyWindowChrome()
    {
        AppWindow.Resize(WindowSize);
        WindowEffects.Apply(this, Loc.T("setup.title"), TitleBar);
    }

    /// <summary><c>Continue</c> button: validate input, commit all settings, close. bound from XAML (<c>Click="OnContinue"</c>)</summary>
    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (!TryReadValidatedPin(out var pin))
            return;

        ClearError();
        ConfigBridge.ResetWriteStatus();
        PersistConfiguration(pin);

        // every first-run value (passcode hash + setup_complete) written through service over config pipe. if any didn't reach it, device is left unprotected (HasPasscode==false) while wizard claimed success — keep window open, tell parent to retry once service reachable
        if (!ConfigBridge.LastWriteOk)
        {
            ShowError(Loc.T("settings.err.savefailed"));
            return;
        }

        Close();
    }

    /// <summary>read + validate PIN pair. on fail surface inline error, return <c>false</c>; on success <paramref name="pin"/> holds confirmed 4-digit value</summary>
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

    /// <summary>write every wizard choice to store, mark setup complete</summary>
    private void PersistConfiguration(string pin)
    {
        // first run writes config through service too (config.db read-only). new PIN authorises writes; service lets the very first passcode through before any exists (bootstrap)
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

        // seed offline unlock-code secret so feature is ready to enrol in an authenticator from Settings
        if (string.IsNullOrEmpty(_settings.Get("unlock_secret")))
            _settings.Set("unlock_secret", Curfew.Core.Security.UnlockCode.GenerateSecret());

        _settings.Set("setup_complete", "1");
    }

    /// <summary>write daily budget. simple mode: single hours/day applies to every weekday; once Advanced used, each day keeps its own value</summary>
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

    /// <summary>NumberBox hours clamped to range, rounded to whole minutes</summary>
    private static int HoursToMinutes(double hours)
    {
        if (double.IsNaN(hours))
            hours = DefaultHoursPerDay;
        hours = Math.Clamp(hours, MinHoursPerDay, MaxHoursPerDay);
        return (int)Math.Round(hours * MinutesPerHour);
    }

    /// <summary>daily budget from single NumberBox, clamped + rounded to whole minutes</summary>
    private int ChosenDailyMinutes() => HoursToMinutes(HoursPerDay.Value);

    /// <summary>map content-filter radio group to <see cref="FilterMode"/></summary>
    private FilterMode SelectedFilterMode()
    {
        if (FilterMalware.IsChecked == true) return FilterMode.Malware;
        if (FilterFamily.IsChecked == true) return FilterMode.Family;
        return FilterMode.Off;
    }

    /// <summary>bool as store's "1"/"0" flag</summary>
    private static string ToFlag(bool on) => on ? "1" : "0";

    /// <summary>show inline validation message beneath form</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>hide any shown validation message</summary>
    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
