using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>primary-monitor WinUI lock surface. collects input, verifies parent passcode (or offline unlock code) in process, enforces brute-force lockout, then raises <see cref="ActionConfirmed"/>; <see cref="LockController"/> records action for overlay + tears lock down. no title bar, shown full-screen by controller</summary>
public sealed partial class LockWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly string _reason;          // "budget" | "schedule" | "newuser"
    private readonly bool _budgetMode;
    private readonly bool _newUser;

    /// <summary>daily limit (minutes) parent chose on new-user setup lock, read by <see cref="LockController"/> when it records a "provision" action. only meaningful while <see cref="_newUser"/></summary>
    public int SetupLimitMinutes { get; private set; }

    /// <summary>fail-closed cooldown deadline (Unix seconds, UTC) used when SYSTEM service couldnt record a failure. persisted counter lives in config.db + is the only thing driving <see cref="LockoutPolicy"/> backoff; WinUI lock can only advance it via best-effort config pipe, which returns false (never throws) when service down/restarting/pipe busy. re-accepting input in that window would make lock an unthrottled oracle, so failed RecordFailure() arms this local floor instead — re-checked by <see cref="IsLockedOut"/> every attempt + cleared once wall clock passes it</summary>
    private long _localCooldownUntilUnix;

    /// <summary>true while an action is with the overlay awaiting its verdict; blocks further submissions</summary>
    private bool _pending;

    /// <summary>raised on confirmed action (extend15/30/60 / unlock / ignore_schedule / redeem / provision / logoff). second arg = entered code for redeem/provision, otherwise null</summary>
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
            // first-time setup for this Windows user: parent picks daily limit + enters PIN, then "Save & unlock" sets user up
            TitleText.Text = Loc.T("lock.title.newuser");
            MessageText.Text = Loc.T("lock.newuser.message");
            UnlockButton.Content = Loc.T("lock.activate");
            AddTimePanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            SetupLimitHours.Value = DefaultSetupHours();
            // subscribed after the initial value is seeded, so the warning reflects parent edits only
            SetupLimitHours.ValueChanged += OnSetupLimitChanged;
            OnSetupLimitChanged(SetupLimitHours, null!);   // reflect a device default that is itself zero
        }
        else
        {
            TitleText.Text = _budgetMode ? Loc.T("lock.title.budget") : Loc.T("lock.title.schedule");
            MessageText.Text = _budgetMode ? BudgetMessage() : Loc.T("lock.schedule.message");
            UnlockButton.Content = _budgetMode ? Loc.T("lock.unlock") : Loc.T("lock.schedule.ignore");
        }
    }

    /// <summary>device's current daily limit (hours) for today, shown as default when setting up new user</summary>
    private double DefaultSetupHours()
    {
        var weekday = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(DateTime.Now));
        return Math.Round(_settings.GetDailyLimit(weekday) / 60.0, 2);
    }

    /// <summary>update logoff-countdown line (driven by controller's timer)</summary>
    public void SetCountdown(string text) => CountdownText.Text = text;

    /// <summary>focus passcode field (once window shown)</summary>
    public void FocusInput() => PinBox.Focus(FocusState.Programmatic);

    /// <summary>Show or hide the child self-service break button based on the break minutes the overlay
    /// currently offers (published in lock_break_minutes). Zero or less hides it.</summary>
    public void SetBreakOffer(int minutes)
    {
        if (minutes > 0)
        {
            BreakButton.Content = Loc.T("lock.break", minutes);
            BreakButton.Visibility = Visibility.Visible;
        }
        else
        {
            BreakButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Child takes a self-service break: ungated (no passcode), bounded by the parent's pause
    /// policy in the overlay. Records the action for the overlay to grant and tears the lock down.</summary>
    private void OnBreak(object sender, RoutedEventArgs e)
    {
        if (_pending) return;
        ActionConfirmed?.Invoke("break", null);
    }

    /// <summary>Action handed to the overlay: disable the form and show the working indicator so the parent
    /// gets feedback and cannot fire a second action (or a second setup pipe call) while the first is in
    /// flight. Cleared by <see cref="EndPending"/>, or the window is closed outright once the overlay lifts
    /// the lock.</summary>
    public void BeginPending()
    {
        _pending = true;
        ErrorBar.IsOpen = false;
        BusyPanel.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        SetFormEnabled(false);
    }

    /// <summary>Overlay refused the action (or never answered): re-enable the form, show why, put the cursor
    /// back in the passcode box so the parent can retry immediately.</summary>
    public void EndPending(string message)
    {
        _pending = false;
        BusyRing.IsActive = false;
        BusyPanel.Visibility = Visibility.Collapsed;
        SetFormEnabled(true);
        ShowError(message);
    }

    /// <summary>enable/disable every input while an action is in flight</summary>
    private void SetFormEnabled(bool enabled)
    {
        PinBox.IsEnabled = enabled;
        UnlockButton.IsEnabled = enabled;
        LogoffButton.IsEnabled = enabled;
        BreakButton.IsEnabled = enabled;
        Add15.IsEnabled = enabled;
        Add30.IsEnabled = enabled;
        Add60.IsEnabled = enabled;
        SetupLimitHours.IsEnabled = enabled;
    }

    private string BudgetMessage()
    {
        var configured = _settings.Get("blocking_message");
        return string.IsNullOrWhiteSpace(configured) ? Loc.T("lock.default.message") : configured;
    }

    /// <summary>daily limit (minutes, clamped 0..24h) parent entered for new user.
    /// <para>An empty or unparseable box reads back as NaN, which used to fold to a 0-minute limit: setup
    /// "succeeded", the user was provisioned with no time at all, and the very next enforcement tick locked
    /// them straight back out with the setup screen gone. Blank now means the prefilled device default —
    /// matching the first-run wizard, which has always resolved NaN to its default rather than to zero. A
    /// deliberately typed 0 is still honoured.</para></summary>
    private int ChosenSetupMinutes()
    {
        var hours = double.IsNaN(SetupLimitHours.Value) ? DefaultSetupHours() : SetupLimitHours.Value;
        return Math.Clamp((int)Math.Round(hours * 60), 0, 24 * 60);
    }

    /// <summary>Flag a zero daily limit as the parent types it. Zero is a legitimate choice (a day this user
    /// gets no screen time), but it is also what a mistyped entry lands on, and the consequence — the user is
    /// re-locked the moment setup completes — is severe enough to warrant saying so up front.</summary>
    private void OnSetupLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        SetupZeroWarning.Visibility = ChosenSetupMinutes() == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnAdd15(object sender, RoutedEventArgs e) => TryAction("extend15");
    private void OnAdd30(object sender, RoutedEventArgs e) => TryAction("extend30");
    private void OnAdd60(object sender, RoutedEventArgs e) => TryAction("extend60");

    private void OnUnlock(object sender, RoutedEventArgs e) =>
        TryAction(_newUser ? "provision" : _budgetMode ? "unlock" : "ignore_schedule");

    private void OnLogoff(object sender, RoutedEventArgs e)
    {
        if (_pending) return;
        ActionConfirmed?.Invoke("logoff", null);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnUnlock(sender, e);
        }
    }

    /// <summary>enforce lockout, verify entry, on success raise action (reset failed-attempt counter); on failure record attempt</summary>
    private void TryAction(string action)
    {
        if (_pending) return;   // one action at a time; the overlay has not ruled on the last one yet

        if (IsLockedOut(out var wait))
        {
            ShowError(Loc.T("lock.lockedout", wait));
            return;
        }

        var entered = PinBox.Password;

        // parent passcode authorizes everything. for new-user setup it also carries chosen daily limit (captured here) + PIN itself through to service, which re-verifies before writing limit + setting up
        if (PasscodeHash.Verify(entered, _settings.Get("passcode")))
        {
            ConfigClient.ResetFailures(entered);
            if (_newUser)
            {
                SetupLimitMinutes = ChosenSetupMinutes();
                ActionConfirmed?.Invoke(action, entered);
            }
            else
            {
                ActionConfirmed?.Invoke(action, null);
            }
            return;
        }

        // offline unlock code grants bonus time on ordinary lock, but cant skip new user's setup
        if (!_newUser && IsValidUnlockCode(entered))
        {
            // do NOT touch the failed-attempt counter here. the service only resets it on a
            // verified PASSCODE, so calling ResetFailures with the unlock code makes the service
            // verify the code as the PIN, fail, and RECORD A FAILURE — every valid redemption
            // would then climb toward a lockout that eventually refuses all input. leave the
            // counter as-is; a later correct passcode clears it.
            ActionConfirmed?.Invoke("redeem", entered);
            return;
        }

        EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FailedUnlock, _reason);

        // persisted counter throttles guessing; advancing it is best-effort pipe round-trip to SYSTEM service that returns false (never throws) when service stopped/restarting or pipe busy. if we cant advance it, re-prompting would let child grind freely for whole outage window — including brief restart windows child can provoke. fail closed: arm local cooldown (policy's first throttle step) so IsLockedOut keeps refusing input until server counter advances or local floor expires
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

    /// <summary>surface controller-side failure (e.g. lock-handshake write to state.db couldnt land) on still-open lock window so parent can retry, instead of action silently dropped. called by <see cref="LockController"/> after verified action fails to persist</summary>
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

        // local floor armed when service couldnt advance persisted counter (see TryAction): hold input shut until it expires so service outage cant turn lock into unthrottled oracle
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
