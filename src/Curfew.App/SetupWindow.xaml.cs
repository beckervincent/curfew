using Curfew.Core;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// First-run wizard. Always presents choices: PIN, content-filter mode, DoH
/// blocking and Time Manipulation Guarding. Writes them to settings and marks
/// setup complete.
/// </summary>
public sealed partial class SetupWindow : Window
{
    private readonly SettingsStore _settings;

    public SetupWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 640));
        WindowEffects.RoundCorners(this);
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        var confirm = ConfirmBox.Password;

        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            ShowError("PIN must be exactly 4 digits.");
            return;
        }
        if (pin != confirm)
        {
            ShowError("PINs do not match.");
            return;
        }

        var mode = FilterMalware.IsChecked == true ? FilterMode.Malware
                 : FilterFamily.IsChecked == true ? FilterMode.Family
                 : FilterMode.Off;

        _settings.Set("passcode", pin);
        _settings.Set("dns_filter_mode", ContentFilter.ToSetting(mode));
        _settings.Set("block_doh_bypass", BlockDoh.IsChecked == true ? "1" : "0");
        _settings.Set("time_guard_enabled", TimeGuard.IsChecked == true ? "1" : "0");
        _settings.Set("setup_complete", "1");

        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
