using Curfew.Core;
using Curfew.Core.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Curfew.App;

/// <summary>
/// Drives the WinUI lock surface for the <c>--lock</c> activation: a full-screen
/// interactive card on the primary monitor and a cover on every other monitor.
/// </summary>
/// <remarks>
/// <para>
/// This is only the visual + input layer. The robust enforcement (the instant
/// black cover, the low-level keyboard hook, the watchdog) lives in the Win32
/// overlay, which launches this app while a session is blocked and relaunches it
/// if it is killed. The two coordinate through settings keys:
/// </para>
/// <list type="bullet">
/// <item>read: <c>lock_reason</c> (budget|schedule), <c>lock_deadline_unix</c>
/// (logoff countdown), <c>lock_active</c> (1 while the overlay still wants the
/// lock up — this app exits when it clears).</item>
/// <item>write: <c>lock_action</c> + <c>lock_action_at</c> (+ <c>lock_code</c> for
/// an unlock-code redemption), which the overlay consumes and applies.</item>
/// </list>
/// </remarks>
internal sealed class LockController
{
    private readonly SettingsStore _settings;
    private readonly List<LockCoverWindow> _covers = new();
    private LockWindow? _primary;
    private DispatcherTimer? _timer;
    private bool _closing;

    public LockController(SettingsStore settings) => _settings = settings;

    /// <summary>Builds and shows the lock windows; returns the primary window.</summary>
    public LockWindow Start()
    {
        var reason = _settings.Get("lock_reason") ?? "budget";
        _primary = new LockWindow(_settings, reason);
        _primary.ActionConfirmed += OnAction;

        var displays = DisplayArea.FindAll();
        var primaryArea = PrimaryDisplay(displays);

        if (primaryArea is not null) PlaceFullScreen(_primary, primaryArea);
        _primary.Activate();
        _primary.FocusInput();

        foreach (var display in displays)
        {
            if (primaryArea is not null && display.DisplayId.Value == primaryArea.DisplayId.Value) continue;

            var cover = new LockCoverWindow();
            var target = display;
            cover.MoveHereRequested += () => MovePrimaryTo(target);
            PlaceFullScreen(cover, display);
            cover.Activate();
            _covers.Add(cover);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        UpdateCountdown();

        return _primary;
    }

    private static DisplayArea? PrimaryDisplay(IReadOnlyList<DisplayArea> displays)
    {
        foreach (var d in displays)
            if (d.IsPrimary) return d;
        return displays.Count > 0 ? displays[0] : null;
    }

    private static void PlaceFullScreen(Window window, DisplayArea area)
    {
        var appWindow = window.AppWindow;
        var bounds = area.OuterBounds;
        // Move onto the target monitor first, then go full-screen so the presenter
        // fills that monitor. Hide from Alt+Tab / the taskbar so the lock cannot be
        // switched away from.
        appWindow.Move(new PointInt32(bounds.X, bounds.Y));
        appWindow.IsShownInSwitchers = false;
        appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private void MovePrimaryTo(DisplayArea area)
    {
        if (_primary is null) return;
        PlaceFullScreen(_primary, area);
        _primary.Activate();
        _primary.FocusInput();
    }

    private void OnTick(object? sender, object e)
    {
        // The overlay clears lock_active when it decides the session is no longer
        // blocked; exit so it can drop its black cover. The read hits state.db,
        // which the child can hold locked: a transient SqliteException here must
        // not kill the tick (that would freeze the lock and stop us ever noticing
        // lock_active clearing). Treat a failed read as "still active" and retry
        // next tick — the overlay's GDI cover stays up regardless.
        string? active;
        try { active = _settings.Get("lock_active"); }
        catch { return; }

        if (active != "1")
        {
            Close();
            return;
        }

        // Keep every lock window pinned to the top so nothing can cover it (the
        // overlay's black cover deliberately does NOT fight us for the top spot).
        ReassertTopmost(_primary);
        foreach (var cover in _covers) ReassertTopmost(cover);

        UpdateCountdown();
    }

    private static void ReassertTopmost(Window? window)
    {
        if (window is null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            // SWP_NOACTIVATE so reasserting never steals focus from the passcode field.
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch
        {
            // Best effort: the FullScreen presenter already keeps the window topmost.
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private void UpdateCountdown()
    {
        if (_primary is null) return;

        // The deadline read also hits state.db; a transient lock must not throw out
        // of the tick. Leave the last-shown countdown in place and refresh next tick.
        string? rawDeadline;
        try { rawDeadline = _settings.Get("lock_deadline_unix"); }
        catch { return; }

        if (!long.TryParse(rawDeadline, out var deadline))
        {
            _primary.SetCountdown(string.Empty);
            return;
        }

        var remaining = (int)(deadline - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (remaining <= 0)
        {
            _primary.SetCountdown(Loc.T("lock.exceeded"));
            return;
        }

        _primary.SetCountdown(remaining <= 60
            ? Loc.T("lock.shutdown.in.short", remaining)
            : Loc.T("lock.shutdown.in.long", TimeMath.FormatDuration(remaining)));
    }

    private void OnAction(string action, string? code)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // These writes land in state.db, which the child can hold locked: a write
        // throwing SqliteException here would otherwise escape the ActionConfirmed
        // handler unhandled (crashing the lock process) AND silently discard the
        // parent's authenticated unlock/extend/redeem. Catch it, keep the lock
        // window up, and surface a retry prompt instead of acting on a half-written
        // handshake. lock_action is still written last, so a failure before it is
        // set leaves the overlay nothing to consume (no fresh action paired with a
        // stale timestamp).
        try
        {
            _settings.Set("lock_action_at", now.ToString());
            if (action == "redeem" && code is not null) _settings.Set("lock_code", code);
            _settings.Set("lock_action", action);
        }
        catch
        {
            // A momentary state.db lock (typically the child contending for it) must
            // not drop the action — let the parent retry on the still-open window.
            _primary?.ShowActionError(Loc.T("lock.action.failed"));
            return;
        }
        Close();
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;

        _timer?.Stop();
        try { foreach (var cover in _covers) cover.Close(); } catch { /* tearing down */ }
        try { _primary?.Close(); } catch { /* tearing down */ }

        Application.Current.Exit();
    }
}
