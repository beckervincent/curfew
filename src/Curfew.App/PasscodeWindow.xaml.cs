using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>Passcode prompt shown before any protected action (e.g. opening
/// Settings). Raises <see cref="Result"/> with true on the correct PIN.</summary>
public sealed partial class PasscodeWindow : Window
{
    private readonly SettingsStore _settings;

    /// <summary>True = verified, false = cancelled/closed.</summary>
    public event Action<bool>? Result;

    public PasscodeWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 320));
        WindowEffects.RoundCorners(this);
        PinBox.Focus(FocusState.Programmatic);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var stored = _settings.Get("passcode") ?? "";
        if (!string.IsNullOrEmpty(stored) && PinBox.Password == stored)
        {
            Result?.Invoke(true);
            Close();
        }
        else
        {
            ErrorText.Visibility = Visibility.Visible;
            PinBox.Password = string.Empty;
            PinBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result?.Invoke(false);
        Close();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) OnOk(sender, e);
    }
}
