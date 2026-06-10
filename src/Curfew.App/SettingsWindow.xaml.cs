using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curfew.App;

/// <summary>Settings editor (Fluent + Mica). Daily limits are edited in hours;
/// budget and schedule are independently toggleable.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly NumberBox[] _dailyLimits = new NumberBox[7];

    public SettingsWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 880));
        WindowEffects.RoundCorners(this);
        Load();
    }

    private void Load()
    {
        LimitEnabled.IsOn = _settings.GetBool("limit_enabled", true);
        ScheduleEnabled.IsOn = _settings.GetBool("schedule_enabled", false);
        Schedule.Load(Curfew.Core.Schedule.Parse(_settings.Get("schedule")));

        for (var i = 0; i < 7; i++)
        {
            var minutes = _settings.GetInt(SettingsStore.WeekdayKeys[i], 120);
            var box = new NumberBox
            {
                Header = SettingsStore.WeekdayNames[i],
                Minimum = 0,
                Maximum = 24,
                SmallChange = 0.25,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                Value = Math.Round(minutes / 60.0, 2),
            };
            _dailyLimits[i] = box;
            DailyLimitsPanel.Children.Add(box);
        }

        Warn1Min.Value = _settings.GetInt("warning1_minutes", 10);
        Warn1Msg.Text = _settings.Get("warning1_message") ?? "";
        Warn2Min.Value = _settings.GetInt("warning2_minutes", 5);
        Warn2Msg.Text = _settings.Get("warning2_message") ?? "";
        BlockingMsg.Text = _settings.Get("blocking_message") ?? "";

        LockTimeout.Value = _settings.GetInt("lock_screen_timeout", 600) / 60;
        IdleEnabled.IsOn = _settings.GetBool("idle_enabled", true);
        IdleTimeout.Value = _settings.GetInt("idle_timeout_minutes", 5);

        switch (ContentFilter.Parse(_settings.Get("dns_filter_mode")))
        {
            case FilterMode.Malware: FilterMalware.IsChecked = true; break;
            case FilterMode.Family: FilterFamily.IsChecked = true; break;
            default: FilterOff.IsChecked = true; break;
        }
        BlockDoh.IsOn = _settings.GetBool("block_doh_bypass", true);
        TimeGuard.IsOn = _settings.GetBool("time_guard_enabled", true);
        AutoUpdate.IsOn = _settings.GetBool("auto_update_enabled", true);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TrySavePasscode()) return;

        _settings.Set("limit_enabled", LimitEnabled.IsOn ? "1" : "0");
        _settings.Set("schedule_enabled", ScheduleEnabled.IsOn ? "1" : "0");
        _settings.Set("schedule", Schedule.ToSchedule().Serialize());

        for (var i = 0; i < 7; i++)
        {
            var hours = double.IsNaN(_dailyLimits[i].Value) ? 2.0 : _dailyLimits[i].Value;
            var minutes = (int)Math.Round(Math.Clamp(hours, 0, 24) * 60);
            _settings.Set(SettingsStore.WeekdayKeys[i], minutes.ToString());
        }

        _settings.Set("warning1_minutes", Clamp(Warn1Min, 0, 600, 10).ToString());
        _settings.Set("warning1_message", Warn1Msg.Text.Trim());
        _settings.Set("warning2_minutes", Clamp(Warn2Min, 0, 600, 5).ToString());
        _settings.Set("warning2_message", Warn2Msg.Text.Trim());
        _settings.Set("blocking_message", BlockingMsg.Text.Trim());

        _settings.Set("lock_screen_timeout", (Clamp(LockTimeout, 1, 720, 10) * 60).ToString());
        _settings.Set("idle_enabled", IdleEnabled.IsOn ? "1" : "0");
        _settings.Set("idle_timeout_minutes", Clamp(IdleTimeout, 1, 600, 5).ToString());

        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;
        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", BlockDoh.IsOn ? "1" : "0");
        _settings.Set("time_guard_enabled", TimeGuard.IsOn ? "1" : "0");
        _settings.Set("auto_update_enabled", AutoUpdate.IsOn ? "1" : "0");

        Close();
    }

    private bool TrySavePasscode()
    {
        var newPin = NewPin.Password;
        var confirm = ConfirmPin.Password;
        if (newPin.Length == 0 && confirm.Length == 0) return true;

        var stored = _settings.Get("passcode") ?? "";
        if (CurrentPin.Password != stored) { ShowError("Current passcode is incorrect."); return false; }
        if (newPin.Length != 4 || !newPin.All(char.IsAsciiDigit)) { ShowError("New passcode must be exactly 4 digits."); return false; }
        if (newPin != confirm) { ShowError("New passcode and confirmation do not match."); return false; }

        _settings.Set("passcode", newPin);
        return true;
    }

    private static int Clamp(NumberBox box, int min, int max, int fallback)
    {
        var value = double.IsNaN(box.Value) ? fallback : (int)box.Value;
        return Math.Clamp(value, min, max);
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
