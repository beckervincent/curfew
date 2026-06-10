using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>
/// Passcode prompt shown before any protected action (for example, opening
/// Settings). The window raises <see cref="Result"/> exactly once: <c>true</c>
/// when the correct PIN is entered, or <c>false</c> when the prompt is
/// cancelled or dismissed (including via the title-bar close button).
/// </summary>
public sealed partial class PasscodeWindow : Window
{
    /// <summary>Settings key holding the parent's PIN. Must match the value used elsewhere.</summary>
    private const string PasscodeKey = "passcode";

    /// <summary>Initial window size in device-independent pixels.</summary>
    private static readonly Windows.Graphics.SizeInt32 WindowSize = new(420, 320);

    private readonly SettingsStore _settings;

    /// <summary>Guards against raising <see cref="Result"/> more than once.</summary>
    private bool _resultRaised;

    /// <summary>
    /// Raised once when the prompt closes: <c>true</c> if the PIN was verified,
    /// <c>false</c> if the user cancelled or closed the window without verifying.
    /// </summary>
    public event Action<bool>? Result;

    /// <param name="settings">Store used to read the configured passcode.</param>
    public PasscodeWindow(SettingsStore settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        InitializeComponent();

        AppWindow.Resize(WindowSize);
        WindowEffects.RoundCorners(this);

        // If the window is dismissed by any means other than OK/Cancel (for
        // example the title-bar X), treat it as a cancellation so the caller is
        // never left waiting for a result that will not arrive.
        Closed += OnClosed;

        PinBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Verifies the entered PIN; on success closes with a positive result.</summary>
    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (IsPasscodeCorrect(PinBox.Password))
        {
            RaiseResult(true);
            Close();
        }
        else
        {
            ShowError();
        }
    }

    /// <summary>Closes the prompt with a negative (cancelled) result.</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>Allows submitting the PIN by pressing Enter inside the password box.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnOk(sender, e);
        }
    }

    /// <summary>Fallback cancellation when the window closes without an explicit result.</summary>
    private void OnClosed(object sender, WindowEventArgs e) => RaiseResult(false);

    /// <summary>
    /// Constant-time-agnostic comparison of the entry against the stored PIN.
    /// Returns <c>false</c> when no passcode is configured so an empty PIN can
    /// never satisfy an empty stored value.
    /// </summary>
    private bool IsPasscodeCorrect(string? entered)
    {
        var stored = _settings.Get(PasscodeKey);
        return !string.IsNullOrEmpty(stored) && entered == stored;
    }

    /// <summary>Reveals the error message and resets the input for another attempt.</summary>
    private void ShowError()
    {
        ErrorText.Visibility = Visibility.Visible;
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Raises <see cref="Result"/> at most once for the lifetime of the window.</summary>
    private void RaiseResult(bool verified)
    {
        if (_resultRaised) return;
        _resultRaised = true;
        Result?.Invoke(verified);
    }
}
