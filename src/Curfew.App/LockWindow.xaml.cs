using Curfew.Core;
using Curfew.Core.Localization;
using Curfew.Core.Security;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Curfew.App;

/// <summary>
/// The primary-monitor WinUI lock surface. It only collects input and verifies
/// the parent passcode (or a valid offline unlock code) in-process — it never
/// writes enforcement state itself. On a confirmed action it raises
/// <see cref="ActionConfirmed"/>; <see cref="LockController"/> is responsible for
/// recording the action for the overlay and tearing the lock down. The window has
/// no title bar and is shown full-screen by the controller.
/// </summary>
public sealed partial class LockWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly bool _budgetMode;

    /// <summary>
    /// Raised when the user confirms an action with a valid passcode/code. The
    /// first argument is the action name (<c>extend15</c>/<c>extend30</c>/
    /// <c>extend60</c>/<c>unlock</c>/<c>ignore_schedule</c>/<c>redeem</c>/
    /// <c>logoff</c>); the second is the entered unlock code for <c>redeem</c>,
    /// otherwise <see langword="null"/>.
    /// </summary>
    public event Action<string, string?>? ActionConfirmed;

    public LockWindow(SettingsStore settings, bool budgetMode)
    {
        _settings = settings;
        _budgetMode = budgetMode;
        InitializeComponent();

        Add15.Content = Loc.T("lock.extend.minutes", 15);
        Add30.Content = Loc.T("lock.extend.minutes", 30);
        Add60.Content = Loc.T("lock.extend.hour");

        TitleText.Text = budgetMode ? Loc.T("lock.title.budget") : Loc.T("lock.title.schedule");
        MessageText.Text = budgetMode ? BudgetMessage() : Loc.T("lock.schedule.message");
        UnlockButton.Content = budgetMode ? Loc.T("lock.unlock") : Loc.T("lock.schedule.ignore");
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
        // Logging off needs no passcode — it ends the child's own session and
        // enforcement continues on next sign-in.
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
    /// Verifies the entered passcode and, on success, raises the action. Falls back
    /// to validating an offline unlock code (read-only here — the overlay performs
    /// the authoritative single-use redemption). Anything else shows the error.
    /// </summary>
    private void TryAction(string action)
    {
        var entered = PinBox.Password;

        if (PasscodeHash.Verify(entered, _settings.Get("passcode")))
        {
            ActionConfirmed?.Invoke(action, null);
            return;
        }

        if (IsValidUnlockCode(entered))
        {
            ActionConfirmed?.Invoke("redeem", entered);
            return;
        }

        ErrorBar.IsOpen = true;
        PinBox.Password = string.Empty;
        PinBox.Focus(FocusState.Programmatic);
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
