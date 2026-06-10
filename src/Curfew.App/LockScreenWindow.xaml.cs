using Curfew.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>
/// Full-screen passcode-protected lock screen shown when time runs out. A
/// keyboard blocker suppresses escape shortcuts while it is visible.
/// </summary>
public sealed partial class LockScreenWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly KeyboardBlocker _blocker = new();

    /// <summary>Raised with the granted extension (minutes) when unlocked via
    /// extend, or 0 for a plain unlock.</summary>
    public event Action<int>? Unlocked;

    public LockScreenWindow(SettingsStore settings, string message)
    {
        InitializeComponent();
        _settings = settings;
        MessageText.Text = message;

        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        AppWindow.IsShownInSwitchers = false;
        Closed += (_, _) => _blocker.Dispose();
    }

    private bool PasscodeMatches()
    {
        var stored = _settings.Get("passcode");
        return !string.IsNullOrEmpty(stored) && PasscodeBox.Password == stored;
    }

    private void RejectPasscode()
    {
        ErrorText.Visibility = Visibility.Visible;
        PasscodeBox.Password = string.Empty;
        PasscodeBox.Focus(FocusState.Programmatic);
    }

    private void Unlock(int extendMinutes)
    {
        _blocker.Dispose();
        Unlocked?.Invoke(extendMinutes);
        Close();
    }

    private void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (PasscodeMatches()) Unlock(0);
        else RejectPasscode();
    }

    private void OnPasscodeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) OnUnlock(sender, e);
    }

    private void Extend(int minutes)
    {
        if (PasscodeMatches()) Unlock(minutes);
        else RejectPasscode();
    }

    private void OnExtend15(object sender, RoutedEventArgs e) => Extend(15);
    private void OnExtend30(object sender, RoutedEventArgs e) => Extend(30);
    private void OnExtend60(object sender, RoutedEventArgs e) => Extend(60);

    private void OnShutdown(object sender, RoutedEventArgs e) => SystemPower.Shutdown();
}
