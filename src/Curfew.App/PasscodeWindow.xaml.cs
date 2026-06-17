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

    /// <summary>fail-closed cooldown deadline (Unix seconds, UTC) armed when the SYSTEM service couldn't record a failure (pipe down/busy). mirrors <see cref="LockWindow"/>: without it a service outage would turn this prompt into an unthrottled guessing oracle</summary>
    private long _localCooldownUntilUnix;

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
        // enforce the same brute-force lockout as LockWindow: this prompt verifies the PIN in-process, so
        // without rate-limiting a child could grind guesses against it freely
        if (IsLockedOut())
        {
            ShowError();
            return;
        }

        var entered = PinBox.Password;
        if (IsPasscodeCorrect(entered))
        {
            // verified PIN clears the persisted failed-attempt counter (service-side, replay-proof)
            ConfigClient.ResetFailures(entered);
            // remember verified passcode so config writes can be authorised by service (config.db read-only for app)
            ConfigBridge.Passcode = entered;
            RaiseResult(true);
            Close();
        }
        else
        {
            // advance persisted counter via SYSTEM service; if the pipe is down/busy it returns false (never
            // throws), so arm a local cooldown floor instead of letting the child guess through the outage
            if (!ConfigClient.RecordFailure())
                _localCooldownUntilUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + LockoutPolicy.BaseBackoffSeconds;
            ShowError();
        }
    }

    /// <summary>whether input is currently throttled by the persisted backoff counter or the local cooldown floor. mirrors <see cref="LockWindow.IsLockedOut"/></summary>
    private bool IsLockedOut()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var state = new LockoutState(
            _settings.GetInt("failed_attempts", 0),
            long.TryParse(_settings.Get("failed_attempt_at"), out var at) ? at : 0);
        if (LockoutPolicy.IsLockedOut(state, now, out _))
            return true;

        return _localCooldownUntilUnix - now > 0;
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
