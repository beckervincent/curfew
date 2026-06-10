using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curfew.App;

/// <summary>
/// First-run wizard (Fluent + Mica). Always presents choices: PIN, daily limit
/// (hours) on/off, weekly schedule on/off, content filter, DoH blocking and
/// Time Manipulation Guarding.
/// </summary>
public sealed partial class SetupWindow : Window
{
    private readonly SettingsStore _settings;

    public SetupWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 720));
        WindowEffects.RoundCorners(this);
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) { ShowError("PIN must be exactly 4 digits."); return; }
        if (pin != ConfirmBox.Password) { ShowError("PINs do not match."); return; }

        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;

        _settings.Set("passcode", pin);
        _settings.Set("limit_enabled", LimitEnabled.IsOn ? "1" : "0");
        _settings.Set("schedule_enabled", ScheduleEnabled.IsOn ? "1" : "0");

        // Apply the chosen hours/day to every weekday (fine-tune later in Settings).
        var hours = double.IsNaN(HoursPerDay.Value) ? 2.0 : HoursPerDay.Value;
        var minutes = (int)Math.Round(Math.Clamp(hours, 0, 24) * 60);
        foreach (var key in SettingsStore.WeekdayKeys)
            _settings.Set(key, minutes.ToString());

        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", BlockDoh.IsOn ? "1" : "0");
        _settings.Set("time_guard_enabled", TimeGuard.IsOn ? "1" : "0");
        _settings.Set("setup_complete", "1");

        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
