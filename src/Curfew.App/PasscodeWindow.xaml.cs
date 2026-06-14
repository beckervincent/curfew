using Curfew.Core;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>passcode prompt shown before any protected action (e.g. opening Settings). raises <see cref="Result"/> exactly once: <c>true</c> on correct PIN, <c>false</c> when cancelled/dismissed (including title-bar close button)</summary>
public sealed partial class PasscodeWindow : Window
{
    /// <summary>settings key holding parent's PIN. must match value used elsewhere</summary>
    private const string PasscodeKey = "passcode";

    /// <summary>initial window size in device-independent pixels</summary>
    private static readonly Windows.Graphics.SizeInt32 WindowSize = new(440, 380);

    private readonly SettingsStore _settings;

    /// <summary>guard against raising <see cref="Result"/> more than once</summary>
    private bool _resultRaised;

    /// <summary>raised once when prompt closes: <c>true</c> if PIN verified, <c>false</c> if cancelled/closed without verifying</summary>
    public event Action<bool>? Result;

    /// <param name="settings">store used to read configured passcode</param>
    public PasscodeWindow(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();

        AppWindow.Resize(WindowSize);
        WindowEffects.Apply(this, "Curfew", TitleBar);

        // dismissed by anything other than OK/Cancel (e.g. title-bar X) -> treat as cancellation so caller never waits for a result that wont arrive
        Closed += OnClosed;

        PinBox.Focus(FocusState.Programmatic);
    }

    /// <summary>verify entered PIN; on success close with positive result</summary>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (IsPasscodeCorrect(PinBox.Password))
        {
            // remember verified passcode so config writes can be authorised by service (config.db read-only for app)
            ConfigBridge.Passcode = PinBox.Password;
            RaiseResult(true);
            Close();
        }
        else
        {
            ShowError();
        }
    }

    /// <summary>close prompt with negative (cancelled) result</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>submit PIN by pressing Enter inside password box</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnOk(sender, e);
        }
    }

    /// <summary>fallback cancellation when window closes without explicit result</summary>
    private void OnClosed(object sender, WindowEventArgs e) => RaiseResult(false);

    /// <summary>constant-time-agnostic comparison of entry against stored PIN. <c>false</c> when no passcode configured so empty PIN never satisfies empty stored value</summary>
    private bool IsPasscodeCorrect(string? entered)
    {
        return PasscodeHash.Verify(entered, _settings.Get(PasscodeKey));
    }

    /// <summary>reveal error message + reset input for another attempt</summary>
    private void ShowError()
    {
        ErrorText.Visibility = Visibility.Visible;
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Programmatic);
    }

    /// <summary>raise <see cref="Result"/> at most once for window lifetime</summary>
    private void RaiseResult(bool verified)
    {
        if (_resultRaised) return;
        _resultRaised = true;
        Result?.Invoke(verified);
    }
}
