using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>
/// Helpers for applying Windows 11 window chrome (rounded corners) via the
/// Desktop Window Manager (DWM).
/// </summary>
/// <remarks>
/// Rounded corners are a Windows 11 (build 22000+) feature. On Windows 10 the
/// underlying <c>DwmSetWindowAttribute</c> call returns a failure HRESULT and
/// the window keeps its default square corners; this is treated as a harmless
/// no-op so the same code runs unchanged across OS versions.
/// </remarks>
internal static class WindowEffects
{
    /// <summary>
    /// <c>DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE</c> — selects the
    /// rounded-corner policy for a window. Available on Windows 11 and later.
    /// </summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>
    /// <c>DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND</c> — round the corners if
    /// appropriate (the standard Windows 11 rounding).
    /// </summary>
    private const int DWMWCP_ROUND = 2;

    /// <summary>S_OK — the DWM call succeeded.</summary>
    private const int S_OK = 0;

    /// <summary>
    /// Requests Windows 11 rounded corners for <paramref name="window"/>.
    /// </summary>
    /// <param name="window">
    /// The window to style. Ignored if <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// Failures are intentionally swallowed: rounded corners are purely
    /// cosmetic, and the app must continue to run on Windows 10 (where the
    /// attribute is unsupported) and on locked-down sessions where retrieving
    /// the window handle could fail.
    /// </remarks>
    public static void RoundCorners(Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var preference = DWMWCP_ROUND;
            // The HRESULT is ignored beyond debugging: a non-S_OK result simply
            // means the running OS (e.g. Windows 10) does not support rounding.
            int hr = DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));

            System.Diagnostics.Debug.WriteLineIf(
                hr != S_OK,
                $"DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE) returned 0x{hr:X8}.");
        }
        catch
        {
            // Older Windows without the attribute, or a window whose handle can
            // no longer be resolved — corners stay square, which is acceptable.
        }
    }

    /// <summary>
    /// P/Invoke for <c>dwmapi.dll!DwmSetWindowAttribute</c>. Returns an
    /// HRESULT (<c>S_OK</c> on success).
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
