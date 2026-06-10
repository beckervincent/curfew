using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Curfew.App;

/// <summary>Settings editor. Loads current values, validates and clamps numeric
/// input, and writes everything back on Save.</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly NumberBox[] _dailyLimits = new NumberBox[7];

    public SettingsWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(580, 820));
        Load();
    }

    private void Load()
    {
        for (var i = 0; i < 7; i++)
        {
            var box = new NumberBox
            {
                Header = SettingsStore.WeekdayNames[i],
                Minimum = 0,
                Maximum = 1440,
                Value = _settings.GetInt(SettingsStore.WeekdayKeys[i], 120),
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
        IdleEnabled.IsChecked = _settings.GetBool("idle_enabled", true);
        IdleTimeout.Value = _settings.GetInt("idle_timeout_minutes", 5);

        switch (ContentFilter.Parse(_settings.Get("dns_filter_mode")))
        {
            case FilterMode.Malware: FilterMalware.IsChecked = true; break;
            case FilterMode.Family: FilterFamily.IsChecked = true; break;
            default: FilterOff.IsChecked = true; break;
        }
        BlockDoh.IsChecked = _settings.GetBool("block_doh_bypass", true);
        TimeGuard.IsChecked = _settings.GetBool("time_guard_enabled", true);
        AutoUpdate.IsChecked = _settings.GetBool("auto_update_enabled", true);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TrySavePasscode()) return;

        for (var i = 0; i < 7; i++)
        {
            _settings.Set(SettingsStore.WeekdayKeys[i], Clamp(_dailyLimits[i], 0, 1440, 120).ToString());
        }

        _settings.Set("warning1_minutes", Clamp(Warn1Min, 0, 600, 10).ToString());
        _settings.Set("warning1_message", Warn1Msg.Text.Trim());
        _settings.Set("warning2_minutes", Clamp(Warn2Min, 0, 600, 5).ToString());
        _settings.Set("warning2_message", Warn2Msg.Text.Trim());
        _settings.Set("blocking_message", BlockingMsg.Text.Trim());

        _settings.Set("lock_screen_timeout", (Clamp(LockTimeout, 1, 720, 10) * 60).ToString());
        _settings.Set("idle_enabled", IdleEnabled.IsChecked == true ? "1" : "0");
        _settings.Set("idle_timeout_minutes", Clamp(IdleTimeout, 1, 600, 5).ToString());

        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;
        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", BlockDoh.IsChecked == true ? "1" : "0");
        _settings.Set("time_guard_enabled", TimeGuard.IsChecked == true ? "1" : "0");
        _settings.Set("auto_update_enabled", AutoUpdate.IsChecked == true ? "1" : "0");

        Close();
    }

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
        if (newPin.Length != 4 || !newPin.All(char.IsAsciiDigit))
        {
            ShowError("New passcode must be exactly 4 digits.");
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
