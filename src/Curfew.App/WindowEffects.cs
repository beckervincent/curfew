using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Curfew.App;

/// <summary>Win11 window chrome helpers (rounded corners) via DWM.</summary>
internal static class WindowEffects
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Give a window Win11 rounded corners.</summary>
    public static void RoundCorners(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // Older Windows without the attribute — ignore.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
