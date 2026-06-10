using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Curfew.App;

/// <summary>
/// Applies consistent Windows 11 window chrome to every Curfew dialog: a real
/// title (instead of the default "WinUI Desktop"), a Mica backdrop, a custom
/// drag-region title bar so the caption area is no longer a flat white strip,
/// theme-aware caption buttons that follow the Windows light/dark setting, and
/// rounded corners.
/// </summary>
internal static class WindowEffects
{
    /// <summary>
    /// Sets the title, applies a Mica (or Acrylic) backdrop, extends the content
    /// into the title bar using <paramref name="titleBar"/> as the drag region,
    /// keeps the caption buttons in sync with the current theme, and rounds the
    /// corners.
    /// </summary>
    public static void Apply(Window window, string title, UIElement? titleBar)
    {
        if (window is null) return;

        window.Title = title;
        TrySetBackdrop(window);

        if (titleBar is not null)
        {
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);
        }

        if (window.Content is FrameworkElement root)
        {
            UpdateCaptionColors(window, root.ActualTheme);
            // Re-tint the caption glyphs whenever Windows switches light/dark.
            root.ActualThemeChanged += (sender, _) => UpdateCaptionColors(window, sender.ActualTheme);
        }

        RoundCorners(window);
    }

    /// <summary>Prefer Mica; fall back to Acrylic; otherwise leave the default.</summary>
    private static void TrySetBackdrop(Window window)
    {
        if (MicaController.IsSupported())
            window.SystemBackdrop = new MicaBackdrop();
        else if (DesktopAcrylicController.IsSupported())
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    /// <summary>
    /// Makes the caption-button backgrounds transparent (so Mica shows through)
    /// and tints the min/restore/close glyphs to contrast with the active theme.
    /// </summary>
    private static void UpdateCaptionColors(Window window, ElementTheme theme)
    {
        var bar = window.AppWindow.TitleBar;
        var dark = theme == ElementTheme.Dark;
        var glyph = dark ? Colors.White : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A);

        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = glyph;
        bar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A)
            : Color.FromArgb(0xFF, 0x6A, 0x6A, 0x6A);
        bar.ButtonHoverForegroundColor = glyph;
        bar.ButtonPressedForegroundColor = glyph;
        bar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        bar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x28, 0x00, 0x00, 0x00);
    }

    /// <summary>
    /// Requests Windows 11 rounded corners. Failures are swallowed: rounding is
    /// cosmetic and unsupported on Windows 10, where the DWM call simply fails.
    /// </summary>
    public static void RoundCorners(Window? window)
    {
        if (window is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch
        {
            // Older Windows or an unresolvable handle — square corners are fine.
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
