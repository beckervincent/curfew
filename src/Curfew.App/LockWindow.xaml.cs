using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>
/// The primary-monitor WinUI lock surface. It collects input and verifies the
/// parent passcode (or an offline unlock code) in process, enforces the brute-force
/// lockout, then raises <see cref="ActionConfirmed"/>;
/// <see cref="LockController"/> records the action for the overlay and tears the
/// lock down. The window has no title bar and is shown full-screen by the controller.
/// </summary>
public sealed partial class LockWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly string _reason;          // "budget" | "schedule"
    private readonly bool _budgetMode;

    /// <summary>
    /// Fail-closed cooldown deadline (Unix seconds, UTC) used when the SYSTEM
    /// service could not record a failure. The persisted counter lives in
    /// config.db and is the only thing that drives <see cref="LockoutPolicy"/>'s
    /// backoff; the WinUI lock can only advance it via the best-effort config
    /// pipe, which returns false (never throws) whenever the service is down,
    /// restarting, or the pipe is busy. If we re-accepted input in that window the
    /// lock would become an unthrottled oracle, so a failed RecordFailure() arms
    /// this local floor instead — re-checked by <see cref="IsLockedOut"/> on every
    /// attempt and cleared automatically once the wall clock passes it.
    /// </summary>
    private long _localCooldownUntilUnix;

    /// <summary>
    /// Raised on a confirmed action (extend15/30/60 / unlock / ignore_schedule /
    /// redeem / logoff). The second argument is the entered code for redeem,
    /// otherwise null.
    /// </summary>
    public event Action<string, string?>? ActionConfirmed;

    public LockWindow(SettingsStore settings, string reason)
    {
        _settings = settings;
        _reason = reason;
        _budgetMode = reason == "budget";
        InitializeComponent();

        Add15.Content = Loc.T("lock.extend.minutes", 15);
        Add30.Content = Loc.T("lock.extend.minutes", 30);
        Add60.Content = Loc.T("lock.extend.hour");

        TitleText.Text = _budgetMode ? Loc.T("lock.title.budget") : Loc.T("lock.title.schedule");
        MessageText.Text = _budgetMode ? BudgetMessage() : Loc.T("lock.schedule.message");
        UnlockButton.Content = _budgetMode ? Loc.T("lock.unlock") : Loc.T("lock.schedule.ignore");
    }

    /// <summary>Updates the logoff-countdown line (driven by the controller's timer).</summary>
    public void SetCountdown(string text) => CountdownText.Text = text;

    /// <summary>Focuses the passcode field (called once the window is shown).</summary>
    public void FocusInput() => PinBox.Focus(FocusState.Programmatic);

    private string BudgetMessage()
    {
        var configured = _settings.Get("blocking_message");
        return string.IsNullOrWhiteSpace(configured) ? Loc.T("lock.default.message") : configured;
    }

    private void OnAdd15(object sender, RoutedEventArgs e) => TryAction("extend15");
    private void OnAdd30(object sender, RoutedEventArgs e) => TryAction("extend30");
    private void OnAdd60(object sender, RoutedEventArgs e) => TryAction("extend60");

    private void OnUnlock(object sender, RoutedEventArgs e) =>
        TryAction(_budgetMode ? "unlock" : "ignore_schedule");

    private void OnLogoff(object sender, RoutedEventArgs e) =>
        ActionConfirmed?.Invoke("logoff", null);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnUnlock(sender, e);
        }
    }

    /// <summary>
    /// Enforces the lockout, verifies the entry, and on success raises the action
    /// (resetting the failed-attempt counter); on failure records the attempt.
    /// </summary>
    private void TryAction(string action)
    {
        if (IsLockedOut(out var wait))
        {
            ShowError(Loc.T("lock.lockedout", wait));
            return;
        }

        var entered = PinBox.Password;

        // The lock accepts the parent passcode or an offline unlock code.
        if (PasscodeHash.Verify(entered, _settings.Get("passcode")))
        {
            ConfigClient.ResetFailures(entered);
            ActionConfirmed?.Invoke(action, null);
            return;
        }

        if (IsValidUnlockCode(entered))
        {
            // An offline unlock code cannot authenticate the reset (the service
            // only verifies passcode/device code), so the counter simply keeps its
            // value until the next passcode success — fail-closed and harmless.
            ConfigClient.ResetFailures(entered);
            ActionConfirmed?.Invoke("redeem", entered);
            return;
        }

        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FailedUnlock, _reason);

        // The persisted counter is what throttles guessing; advancing it is a
        // best-effort pipe round-trip to the SYSTEM service that returns false
        // (never throws) when the service is stopped/restarting or the pipe is
        // busy. If we cannot advance it, re-prompting would let the child grind
        // freely for the whole outage window — including the brief restart windows
        // the child can provoke. Fail closed: arm a local cooldown (the policy's
        // first throttle step) so IsLockedOut keeps refusing input until either the
        // server counter advances or the local floor expires.
        if (ConfigClient.RecordFailure())
        {
            ShowError(Loc.T("lock.incorrect"));
        }
        else
        {
            _localCooldownUntilUnix =
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + LockoutPolicy.BaseBackoffSeconds;
            ShowError(Loc.T("lock.lockedout", LockoutPolicy.BaseBackoffSeconds));
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Surfaces a controller-side failure (e.g. the lock-handshake write to state.db
    /// could not land) on the still-open lock window so the parent can retry, rather
    /// than the action being silently dropped. Called by <see cref="LockController"/>
    /// after a verified action fails to persist.
    /// </summary>
    public void ShowActionError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        PinBox.Focus(FocusState.Programmatic);
    }

    private bool IsLockedOut(out int retryAfterSeconds)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var state = new LockoutState(
            _settings.GetInt("failed_attempts", 0),
            long.TryParse(_settings.Get("failed_attempt_at"), out var at) ? at : 0);
        if (LockoutPolicy.IsLockedOut(state, now, out retryAfterSeconds))
            return true;

        // Local floor armed when the service could not advance the persisted
        // counter (see TryAction): hold input shut until it expires so a service
        // outage cannot turn the lock into an unthrottled oracle.
        var localRemaining = (int)(_localCooldownUntilUnix - now);
        if (localRemaining > 0)
        {
            retryAfterSeconds = localRemaining;
            return true;
        }

        retryAfterSeconds = 0;
        return false;
    }

    private bool IsValidUnlockCode(string entered)
    {
        var secret = _settings.Get("unlock_secret");
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var minCounter = long.TryParse(_settings.Get("unlock_last_counter"), out var last)
            ? last
            : long.MinValue;

        return UnlockCode.Verify(secret, entered, now, 10, minCounter, out _);
    }
}
