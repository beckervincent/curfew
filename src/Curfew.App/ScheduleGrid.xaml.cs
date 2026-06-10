using Curfew.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Curfew.App;

/// <summary>
/// Interactive weekly allowed-time grid (7 days × 96 fifteen-minute slots).
/// Click and drag to paint allowed (blue) or blocked windows.
/// </summary>
public sealed partial class ScheduleGrid : UserControl
{
    private static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private readonly bool[][] _slots = NewGrid();
    private bool _painting;

    private static readonly Brush AllowedBrush = new SolidColorBrush(Color.FromArgb(255, 0x54, 0xAD, 0xF2));
    private static readonly Brush BlockedBrush = new SolidColorBrush(Color.FromArgb(40, 0x80, 0x80, 0x80));
    private static readonly Brush LineBrush = new SolidColorBrush(Color.FromArgb(60, 0x80, 0x80, 0x80));

    public ScheduleGrid()
    {
        InitializeComponent();
        GridCanvas.SizeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
    }

    private static bool[][] NewGrid()
    {
        var g = new bool[7][];
        for (var d = 0; d < 7; d++) { g[d] = new bool[Schedule.SlotsPerDay]; Array.Fill(g[d], true); }
        return g;
    }

    /// <summary>Load an existing schedule into the grid.</summary>
    public void Load(Schedule schedule)
    {
        for (var d = 0; d < 7; d++)
            for (var s = 0; s < Schedule.SlotsPerDay; s++)
                _slots[d][s] = schedule.GetSlot(d, s);
        Render();
    }

    /// <summary>The current grid as a Schedule.</summary>
    public Schedule ToSchedule()
    {
        var clone = new bool[7][];
        for (var d = 0; d < 7; d++) clone[d] = (bool[])_slots[d].Clone();
        return new Schedule(clone);
    }

    private void Render()
    {
        GridCanvas.Children.Clear();
        LabelCanvas.Children.Clear();

        double w = GridCanvas.ActualWidth, h = GridCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var rowH = h / 7;
        var slotW = w / Schedule.SlotsPerDay;

        for (var d = 0; d < 7; d++)
        {
            var y = d * rowH;
            AddRect(0, y, w, rowH, BlockedBrush);

            var s = 0;
            while (s < Schedule.SlotsPerDay)
            {
                if (_slots[d][s])
                {
                    var e = s;
                    while (e < Schedule.SlotsPerDay && _slots[d][e]) e++;
                    AddRect(s * slotW, y, (e - s) * slotW, rowH, AllowedBrush);
                    s = e;
                }
                else s++;
            }

            AddLabel(Days[d], y, rowH);
        }

        // Hour gridlines every 2 hours (8 slots).
        for (var hour = 0; hour <= 24; hour += 2)
        {
            var x = hour * 4 * slotW;
            var line = new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h, Stroke = LineBrush, StrokeThickness = 1 };
            GridCanvas.Children.Add(line);
        }
    }

    private void AddRect(double x, double y, double w, double h, Brush fill)
    {
        var rect = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        GridCanvas.Children.Add(rect);
    }

    private void AddLabel(string text, double y, double rowH)
    {
        var label = new TextBlock { Text = text, FontSize = 12 };
        Canvas.SetLeft(label, 2);
        Canvas.SetTop(label, y + rowH / 2 - 9);
        LabelCanvas.Children.Add(label);
    }

    private void PaintAt(Point p)
    {
        double w = GridCanvas.ActualWidth, h = GridCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var day = (int)(p.Y / (h / 7));
        var slot = (int)(p.X / (w / Schedule.SlotsPerDay));
        if (day is < 0 or > 6 || slot < 0 || slot >= Schedule.SlotsPerDay) return;

        _slots[day][slot] = ToolAllow.IsChecked == true;
        Render();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _painting = true;
        GridCanvas.CapturePointer(e.Pointer);
        PaintAt(e.GetCurrentPoint(GridCanvas).Position);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_painting) PaintAt(e.GetCurrentPoint(GridCanvas).Position);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _painting = false;
        GridCanvas.ReleasePointerCapture(e.Pointer);
    }
}
