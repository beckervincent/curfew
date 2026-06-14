using Curfew.Core;
using Curfew.Core.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Curfew.App;

/// <summary>drive WinUI lock surface for <c>--lock</c> activation: full-screen interactive card on primary monitor + cover on every other monitor</summary>
/// <remarks>
/// only visual + input layer. robust enforcement (instant black cover, low-level keyboard hook, watchdog) lives in Win32 overlay, which launches this app while session blocked + relaunches if killed. the two coordinate through settings keys:
/// <list type="bullet">
/// <item>read: <c>lock_reason</c> (budget|schedule), <c>lock_deadline_unix</c> (logoff countdown), <c>lock_active</c> (1 while overlay still wants lock up — this app exits when it clears)</item>
/// <item>write: <c>lock_action</c> + <c>lock_action_at</c> (+ <c>lock_code</c> for unlock-code redemption), which overlay consumes + applies</item>
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

    /// <summary>build + show lock windows; returns primary window</summary>
    public LockWindow Start()
    {
        var reason = _settings.Get("lock_reason") ?? "budget";
        _primary = new LockWindow(_settings, reason);
        _primary.ActionConfirmed += OnAction;

        var displays = AllDisplays();
        var primaryArea = PrimaryDisplay(displays);

        // Activate first so content composes, THEN switch to full-screen presenter. full-screen before first Activate() can leave WinUI 3 window showing uncomposed (black) surface
        _primary.Activate();
        if (primaryArea is not null) PlaceFullScreen(_primary, primaryArea);
        _primary.FocusInput();

        foreach (var display in displays)
        {
            if (primaryArea is not null && display.DisplayId.Value == primaryArea.DisplayId.Value) continue;

            var cover = new LockCoverWindow();
            var target = display;
            cover.MoveHereRequested += () => MovePrimaryTo(target);
            cover.Activate();
            PlaceFullScreen(cover, display);
            _covers.Add(cover);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        UpdateCountdown();

        return _primary;
    }

    private static DisplayArea? PrimaryDisplay(List<DisplayArea> displays)
    {
        foreach (var d in displays)
            if (d.IsPrimary) return d;
        return displays.Count > 0 ? displays[0] : null;
    }

    /// <summary>copy <see cref="DisplayArea.FindAll"/> into plain list element by element. FindAll returns projected WinRT <c>IReadOnlyList</c> whose enumerator interface CsWinRT cant resolve: <c>foreach</c>/LINQ over it throws <see cref="InvalidCastException"/> ("interface not supported"). that crash killed whole lock surface on launch + left overlay showing only its black cover (the "inescapable black screen"). indexer access (<c>Count</c> + <c>this[i]</c>) maps to <c>IVectorView.Size</c>/<c>GetAt</c>, which IS supported, so read positionally instead of enumerating</summary>
    private static List<DisplayArea> AllDisplays()
    {
        var found = DisplayArea.FindAll();
        var list = new List<DisplayArea>(found.Count);
        for (var i = 0; i < found.Count; i++) list.Add(found[i]);
        return list;
    }

    private static void PlaceFullScreen(Window window, DisplayArea area)
    {
        var appWindow = window.AppWindow;
        var bounds = area.OuterBounds;
        // move onto target monitor first, then full-screen so presenter fills it. hide from Alt+Tab / taskbar so lock cant be switched away from
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
        // overlay clears lock_active when session no longer blocked; exit so it can drop black cover. read hits state.db, child can hold locked: transient SqliteException must not kill tick (would freeze lock + stop us noticing lock_active clearing). treat failed read as "still active" + retry next tick — overlay GDI cover stays up regardless
        string? active;
        try { active = _settings.Get("lock_active"); }
        catch { return; }

        if (active != "1")
        {
            Close();
            return;
        }

        // keep every lock window pinned to top so nothing covers it (overlay black cover deliberately does NOT fight us for top spot)
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
            // SWP_NOACTIVATE so reasserting never steals focus from passcode field
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch
        {
            // best effort: FullScreen presenter already keeps window topmost
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

        // deadline read also hits state.db; transient lock must not throw out of tick. leave last-shown countdown in place + refresh next tick
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
        // writes land in state.db, child can hold locked: write throwing SqliteException would escape ActionConfirmed handler unhandled (crashing lock process) AND silently discard parent's authenticated unlock/extend/redeem. catch it, keep lock window up, surface retry prompt instead of acting on half-written handshake. lock_action still written last, so failure before it leaves overlay nothing to consume (no fresh action paired with stale timestamp)
        try
        {
            _settings.Set("lock_action_at", now.ToString());
            // redeem carries unlock code; provision carries parent PIN (so service can re-verify) plus chosen per-user daily limit
            if (code is not null && action is "redeem" or "provision") _settings.Set("lock_code", code);
            if (action == "provision" && _primary is not null)
                _settings.Set("lock_setup_limit", _primary.SetupLimitMinutes.ToString());
            _settings.Set("lock_action", action);   // written last: overlay polls this, then reads the rest
        }
        catch
        {
            // momentary state.db lock (typically child contending for it) must not drop action — let parent retry on still-open window
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
