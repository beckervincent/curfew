using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Curfew.App;

/// <summary>consistent Windows 11 chrome for every Curfew dialog: real title (not "WinUI Desktop"), Mica backdrop, custom drag-region title bar, theme-aware caption buttons, rounded corners</summary>
internal static class WindowEffects
{
    /// <summary>set title, apply Mica (or Acrylic) backdrop, extend content into title bar using <paramref name="titleBar"/> as drag region, sync caption buttons to theme, round corners</summary>
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
            // re-tint caption glyphs whenever Windows switches light/dark
            root.ActualThemeChanged += (sender, _) => UpdateCaptionColors(window, sender.ActualTheme);
        }

        RoundCorners(window);
        CenterOnScreen(window);
    }

    /// <summary>centre window on its current display's work area (middle, not top-left). assumes already sized (callers Resize before Apply)</summary>
    public static void CenterOnScreen(Window? window)
    {
        if (window is null) return;

        try
        {
            var appWindow = window.AppWindow;
            var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
            if (area is null) return;

            var work = area.WorkArea;
            var size = appWindow.Size;
            var x = work.X + (work.Width - size.Width) / 2;
            var y = work.Y + (work.Height - size.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // positioning is cosmetic; failure leaves default placement
        }
    }

    /// <summary>prefer Mica; fall back to Acrylic; else leave default</summary>
    private static void TrySetBackdrop(Window window)
    {
        if (MicaController.IsSupported())
            window.SystemBackdrop = new MicaBackdrop();
        else if (DesktopAcrylicController.IsSupported())
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    /// <summary>transparent caption-button backgrounds (Mica shows through), tint min/restore/close glyphs to contrast active theme</summary>
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

    /// <summary>request Windows 11 rounded corners. failures swallowed: cosmetic, unsupported on Windows 10 where the DWM call fails</summary>
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
            // older Windows or unresolvable handle — square corners fine
        }
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
