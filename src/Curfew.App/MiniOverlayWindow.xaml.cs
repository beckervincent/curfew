using Curfew.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace Curfew.App;

/// <summary>
/// Small always-on-top countdown shown in the top-right corner. Drives the
/// per-second tick via <see cref="TimeKeeper"/>. The blocking lock screen is
/// wired up in a later milestone.
/// </summary>
public sealed partial class MiniOverlayWindow : Window
{
    private const int Width = 160;
    private const int Height = 44;
    private const int Margin = 12;

    private readonly DispatcherQueueTimer _timer;
    private readonly SettingsStore _settings;
    private int _remaining;

    public MiniOverlayWindow(SettingsStore settings, int initialRemaining)
    {
        InitializeComponent();
        _settings = settings;
        _remaining = initialRemaining;

        ConfigurePresenter();
        PositionTopRight();
        Render();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => OnSecond();
        _timer.Start();
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

    private void OnSecond()
    {
        _remaining = TimeKeeper.Tick(_remaining);
        if (TimeKeeper.ShouldPersist(_remaining))
        {
            _settings.Set($"remaining_time_{DateTime.Now:yyyy-MM-dd}", _remaining.ToString());
        }
        Render();
    }

    private void Render()
    {
        TimeText.Text = TimeMath.FormatCompact(_remaining);
        TimeText.Foreground = new SolidColorBrush(ColorForRemaining(_remaining));
    }

    private static Color ColorForRemaining(int seconds)
    {
        if (seconds <= 60) return Color.FromArgb(255, 0xFF, 0x44, 0x44);   // red
        if (seconds <= 300) return Color.FromArgb(255, 0xE0, 0x6C, 0x75);  // accent
        return Colors.White;
    }
}
