using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
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
    private void OnBreak(object sender, RoutedEventArgs e) => ActionConfirmed?.Invoke("break", null);

    private string BudgetMessage() =>
        _settings.CustomMessage("blocking_message") ?? Loc.T("lock.default.message");

    /// <summary>daily limit (minutes, clamped 0..24h) parent entered for new user</summary>
    private int ChosenSetupMinutes()
    {
        var hours = double.IsNaN(SetupLimitHours.Value) ? 0 : SetupLimitHours.Value;
        return Math.Clamp((int)Math.Round(hours * 60), 0, 24 * 60);
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

    /// <summary>send entry to the SYSTEM service, which verifies the passcode (or an offline unlock code for time
    /// actions), enforces lockout, and publishes a grant bound to THIS session that the overlay consumes. nothing
    /// secret is read or stored client-side</summary>
    private void TryAction(string action)
    {
        var entered = PinBox.Password;
        string? limit = null;
        if (_newUser)
        {
            SetupLimitMinutes = ChosenSetupMinutes();
            limit = SetupLimitMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var response = ConfigClient.Authorize(action, entered, limit);
        if (response.Ok)
        {
            ActionConfirmed?.Invoke(response.Value ?? action, null);
            return;
        }

        var error = response.Error ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(error, @"locked out, retry in (\d+)s");
        if (match.Success)
        {
            ShowError(Loc.T("lock.lockedout", int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            return;
        }
        if (error == "wrong code")
        {
            EventLog.Append(CurfewPaths.EventLogFile, CurfewEventKind.FailedUnlock, _reason);
            ShowError(Loc.T("lock.incorrect"));
            return;
        }

        // service unreachable/refused: fail closed, nothing granted
        ShowError(Loc.T("lock.action.failed"));
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


}
