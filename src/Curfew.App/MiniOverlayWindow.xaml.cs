using Curfew.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Curfew.App;

/// <summary>
/// Small always-on-top countdown in the top-right corner. A view only — the
/// <see cref="AppController"/> owns the timer and calls <see cref="ShowTime"/>.
/// </summary>
public sealed partial class MiniOverlayWindow : Window
{
    private const int Width = 160;
    private const int Height = 44;
    private const int Margin = 12;

    public MiniOverlayWindow()
    {
        InitializeComponent();
        ConfigurePresenter();
        PositionTopRight();
    }

    private void ConfigurePresenter()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
    }

    private void PositionTopRight()
    {
        AppWindow.Resize(new SizeInt32(Width, Height));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        AppWindow.Move(new PointInt32(work.X + work.Width - Width - Margin, work.Y + Margin));
    }

    public void ShowTime(int remaining)
    {
        TimeText.Text = TimeMath.FormatCompact(remaining);
        TimeText.Foreground = new SolidColorBrush(ColorForRemaining(remaining));
    }

    private static Color ColorForRemaining(int seconds)
    {
        if (seconds <= 60) return Color.FromArgb(255, 0xFF, 0x44, 0x44);   // red
        if (seconds <= 300) return Color.FromArgb(255, 0xE0, 0x6C, 0x75);  // accent
        return Microsoft.UI.Colors.White;
    }
}
