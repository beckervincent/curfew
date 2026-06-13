using System.Runtime.InteropServices;

namespace Curfew.Overlay;

/// <summary>
/// Win32 / GDI P/Invoke surface for the overlay and lock windows.
/// <para>
/// The overlay process is deliberately plain Win32 (no WinUI) so it starts
/// reliably when spawned by the service in any session, including the locked /
/// RDP / disconnected cases where the modern UI stack may not be available.
/// </para>
/// <para>
/// Everything here is grouped by area: window styles, messages, the structs the
/// message loop needs, then user32 (windowing/painting) and gdi32 (drawing)
/// imports. The gdi32 section intentionally exposes a complete little drawing
/// toolkit — solid brushes, pens, stock objects, <c>RoundRect</c> and an
/// offscreen (double-buffer) DC — so the visible lock screen and mini overlay
/// can be drawn cleanly, with rounded panels and no flicker.
/// </para>
/// <para>
/// Cleanup contract for callers: every <see cref="CreateSolidBrush"/>,
/// <see cref="CreatePen"/>, <see cref="CreateFontW"/> and
/// <see cref="CreateCompatibleBitmap"/> must be released with
/// <see cref="DeleteObject"/>; every <see cref="CreateCompatibleDC"/> with
/// <see cref="DeleteDC"/>; and the value returned by <see cref="SelectObject"/>
/// must be selected back before the DC is freed. Objects from
/// <see cref="GetStockObject"/> are owned by the system and must NOT be deleted.
/// </para>
/// </summary>
internal static class Native
{
    // ── Window styles (WS_*) ────────────────────────────────────────────────
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_CHILD = 0x40000000;

    // ── Extended window styles (WS_EX_*) ────────────────────────────────────
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ── Layered-window attribute flags ──────────────────────────────────────
    public const uint LWA_ALPHA = 0x2;

    // ── ShowWindow commands (SW_*) ──────────────────────────────────────────
    public const int SW_SHOW = 5;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_HIDE = 0;

    // ── SetWindowPos flags (SWP_*) ──────────────────────────────────────────
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>hWndInsertAfter value that pins a window above all topmost peers.</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // ── Window messages (WM_*) ──────────────────────────────────────────────
    public const uint WM_CREATE = 0x0001;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_SETFONT = 0x0030;
    // Owner-draw + control-colour messages used to give the lock's child controls
    // (buttons, passcode field) the same dark, rounded look as the WinUI app.
    public const uint WM_DRAWITEM = 0x002B;
    public const uint WM_CTLCOLOREDIT = 0x0133;

    // ── GetSystemMetrics indices (SM_*) ─────────────────────────────────────
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    // ── DrawText formatting flags (DT_*) ────────────────────────────────────
    public const int DT_CENTER = 0x1;
    public const int DT_VCENTER = 0x4;
    public const int DT_WORDBREAK = 0x10;
    public const int DT_SINGLELINE = 0x20;
    public const int DT_NOPREFIX = 0x0800;

    // ── Background mix modes (SetBkMode) ────────────────────────────────────
    public const int TRANSPARENT = 1;

    // ── CreateFontW: weights, quality, charset, precision, pitch ────────────
    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;
    public const int FW_BOLD = 700;

    public const uint DEFAULT_CHARSET = 1;
    public const uint CLIP_DEFAULT_PRECIS = 0;

    /// <summary>ClearType — best for opaque text on the panel; preferred default.</summary>
    public const uint CLEARTYPE_QUALITY = 5;

    public const uint DEFAULT_PITCH = 0;
    public const uint FF_DONTCARE = 0;

    // ── CreatePen styles ────────────────────────────────────────────────────
    public const int PS_SOLID = 0;

    // ── GetStockObject indices ──────────────────────────────────────────────
    public const int NULL_BRUSH = 5;

    // ── BitBlt raster operation ─────────────────────────────────────────────
    public const uint SRCCOPY = 0x00CC0020;

    // ════════════════════════════════════════════════════════════════════════
    //  Structs
    // ════════════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;

        public readonly int Width => right - left;
        public readonly int Height => bottom - top;
    }

    /// <summary>
    /// WM_DRAWITEM payload for an owner-drawn control. Sequential layout lets the
    /// marshaller insert the correct padding before the pointer-sized fields on
    /// 64-bit, so this matches the native struct without explicit offsets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public IntPtr hwndItem;
        public IntPtr hDC;
        public RECT rcItem;
        public IntPtr itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    /// <summary>
    /// Window procedure delegate. Keep a managed reference alive (e.g. a static
    /// field) for the whole window lifetime; if it is collected the native code
    /// will call back into freed memory and crash the process.
    /// </summary>
    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ════════════════════════════════════════════════════════════════════════
    //  user32 — windowing, message loop, painting
    // ════════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        int exStyle, string className, string? windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapseMs, IntPtr func);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, int format);

    /// <summary>Mapped index brush owned by the system; never DeleteObject it.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetSysColorBrush(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // ════════════════════════════════════════════════════════════════════════
    //  gdi32 — drawing primitives
    //
    //  Colours passed to these are COLORREF (0x00BBGGRR — note blue is the high
    //  byte, the reverse of HTML #RRGGBB). The overlay's colour constants are
    //  already written in this BGR order.
    // ════════════════════════════════════════════════════════════════════════

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreatePen(int style, int width, uint color);

    /// <summary>Returns a shared stock object (brush/pen/font). Do not delete it.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern int SetTextColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    public static extern int SetBkColor(IntPtr hdc, uint color);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(IntPtr hdc, int mode);

    /// <summary>
    /// Filled, outlined rounded rectangle using the DC's currently-selected
    /// brush (fill) and pen (border). Select a brush and pen first; pass the
    /// corner diameters as <paramref name="ellipseW"/>/<paramref name="ellipseH"/>.
    /// </summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RoundRect(
        IntPtr hdc, int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    // ── Offscreen / double-buffered drawing ─────────────────────────────────
    //
    // To eliminate flicker: create a memory DC compatible with the window DC,
    // back it with a compatible bitmap, render the whole frame there, then BitBlt
    // it to the window in one shot. Restore the original bitmap and free the DC
    // and bitmap when done.

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
        string face);
}
