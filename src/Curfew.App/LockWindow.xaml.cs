using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>
/// The primary-monitor WinUI lock surface. It collects input and verifies the
/// parent passcode (or an unlock code, or the device code for a new user) in
/// process, enforces the brute-force lockout, then raises <see cref="ActionConfirmed"/>;
/// <see cref="LockController"/> records the action for the overlay and tears the
/// lock down. The window has no title bar and is shown full-screen by the controller.
/// </summary>
public sealed partial class LockWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly string _reason;          // "budget" | "schedule" | "newuser"
    private readonly bool _budgetMode;
    private readonly bool _newUser;

    /// <summary>
    /// Raised on a confirmed action (extend15/30/60 / unlock / ignore_schedule /
    /// redeem / provision / logoff). The second argument is the entered code for
    /// redeem/provision, otherwise null.
    /// </summary>
    public event Action<string, string?>? ActionConfirmed;

    public LockWindow(SettingsStore settings, string reason)
    {
        _settings = settings;
        _reason = reason;
        _budgetMode = reason == "budget";
        _newUser = reason == "newuser";
        InitializeComponent();

        Add15.Content = Loc.T("lock.extend.minutes", 15);
        Add30.Content = Loc.T("lock.extend.minutes", 30);
        Add60.Content = Loc.T("lock.extend.hour");

        if (_newUser)
        {
            TitleText.Text = Loc.T("lock.title.newuser");
            MessageText.Text = Loc.T("lock.newuser.message");
            UnlockButton.Content = Loc.T("lock.activate");
            AddTimePanel.Visibility = Visibility.Collapsed;   // no time to add for a new user
        }
        else
        {
            TitleText.Text = _budgetMode ? Loc.T("lock.title.budget") : Loc.T("lock.title.schedule");
            MessageText.Text = _budgetMode ? BudgetMessage() : Loc.T("lock.schedule.message");
            UnlockButton.Content = _budgetMode ? Loc.T("lock.unlock") : Loc.T("lock.schedule.ignore");
        }
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
        TryAction(_newUser ? "provision" : _budgetMode ? "unlock" : "ignore_schedule");

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

        // A new user activates with the device code OR the parent passcode; an
        // ordinary lock accepts the parent passcode or an offline unlock code.
        var passcodeOk = PasscodeHash.Verify(entered, _settings.Get("passcode"));
        var deviceOk = _newUser && PasscodeHash.Verify(entered, _settings.Get("device_code"));

        if (passcodeOk || deviceOk)
        {
            ConfigClient.ResetFailures();
            ActionConfirmed?.Invoke(action, _newUser ? entered : null);
            return;
        }

        if (!_newUser && IsValidUnlockCode(entered))
        {
            ConfigClient.ResetFailures();
            ActionConfirmed?.Invoke("redeem", entered);
            return;
        }

        ConfigClient.RecordFailure();
        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FailedUnlock, _reason);
        ShowError(Loc.T("lock.incorrect"));
    }

    private void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Programmatic);
    }

    private bool IsLockedOut(out int retryAfterSeconds)
    {
        var state = new LockoutState(
            _settings.GetInt("failed_attempts", 0),
            long.TryParse(_settings.Get("failed_attempt_at"), out var at) ? at : 0);
        return LockoutPolicy.IsLockedOut(state, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out retryAfterSeconds);
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
