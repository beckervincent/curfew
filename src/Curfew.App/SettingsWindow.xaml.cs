using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

    /// <summary>Passcodes are fixed-length, digit-only PINs.</summary>
    private const int PasscodeLength = 4;

    private readonly SettingsStore _settings;

    /// <summary>The seven per-day hour spinners, created in <see cref="LoadDailyLimits"/>.</summary>
    private readonly NumberBox[] _dailyLimits = new NumberBox[DayCount];

    public SettingsWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;

        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(WindowWidth, WindowHeight));
        WindowEffects.RoundCorners(this);

        Load();
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
    }

    /// <summary>Builds the seven per-day hour spinners and appends them to the panel.</summary>
    private void LoadDailyLimits()
    {
        for (var i = 0; i < DayCount; i++)
        {
            var minutes = _settings.GetInt(SettingsStore.WeekdayKeys[i], DefaultDailyMinutes);
            var box = new NumberBox
            {
                Header = SettingsStore.WeekdayNames[i],
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
    }

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

        var stored = _settings.Get("passcode") ?? "";
        if (CurrentPin.Password != stored)
        {
            ShowError("Current passcode is incorrect.");
            return false;
        }
        if (newPin.Length != PasscodeLength || !newPin.All(char.IsAsciiDigit))
        {
            ShowError($"New passcode must be exactly {PasscodeLength} digits.");
            return false;
        }
        if (newPin != confirm)
        {
            ShowError("New passcode and confirmation do not match.");
            return false;
        }

        _settings.Set("passcode", newPin);
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
