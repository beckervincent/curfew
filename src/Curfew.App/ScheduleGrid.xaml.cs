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
/// Click and drag with the mouse to paint slots using the currently selected
/// tool: <c>Allow</c> (blue) or <c>Block</c> (grey). Days run Monday (top) to
/// Sunday (bottom); within each row time runs midnight (left) to midnight (right).
/// </summary>
public sealed partial class ScheduleGrid : UserControl
{
    private const int DayCount = 7;

    /// <summary>Hour interval between vertical gridlines.</summary>
    private const int GridlineHours = 2;

    private static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary><c>_slots[day][slot]</c> — <c>true</c> means usage is allowed.</summary>
    private readonly bool[][] _slots = NewGrid();

    /// <summary>True while a press-drag paint gesture is in progress.</summary>
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

    /// <summary>Creates a fresh, fully-allowed backing grid.</summary>
    private static bool[][] NewGrid()
    {
        var grid = new bool[DayCount][];
        for (var d = 0; d < DayCount; d++)
        {
            grid[d] = new bool[Schedule.SlotsPerDay];
            Array.Fill(grid[d], true);
        }
        return grid;
    }

    /// <summary>Loads an existing schedule into the grid and redraws.</summary>
    /// <param name="schedule">The schedule to display. Null is treated as fully allowed.</param>
    public void Load(Schedule? schedule)
    {
        for (var d = 0; d < DayCount; d++)
            for (var s = 0; s < Schedule.SlotsPerDay; s++)
                _slots[d][s] = schedule?.GetSlot(d, s) ?? true;
        Render();
    }

    /// <summary>Captures the current grid state as a new <see cref="Schedule"/>.</summary>
    public Schedule ToSchedule()
    {
        var clone = new bool[DayCount][];
        for (var d = 0; d < DayCount; d++)
            clone[d] = (bool[])_slots[d].Clone();
        return new Schedule(clone);
    }

    /// <summary>Redraws the day rows, allowed-time blocks, labels and gridlines.</summary>
    private void Render()
    {
        GridCanvas.Children.Clear();
        LabelCanvas.Children.Clear();

        double w = GridCanvas.ActualWidth, h = GridCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var rowH = h / DayCount;
        var slotW = w / Schedule.SlotsPerDay;

        for (var d = 0; d < DayCount; d++)
        {
            var y = d * rowH;
            AddRect(0, y, w, rowH, BlockedBrush);
            RenderAllowedRuns(d, y, rowH, slotW);
            AddLabel(Days[d], y, rowH);
        }

        RenderGridlines(slotW, h);
    }

    /// <summary>
    /// Draws the allowed (blue) blocks for one day, coalescing contiguous allowed
    /// slots into a single rectangle so the visual tree stays small.
    /// </summary>
    private void RenderAllowedRuns(int day, double y, double rowH, double slotW)
    {
        var row = _slots[day];
        var start = 0;
        while (start < Schedule.SlotsPerDay)
        {
            if (!row[start]) { start++; continue; }

            var end = start;
            while (end < Schedule.SlotsPerDay && row[end]) end++;
            AddRect(start * slotW, y, (end - start) * slotW, rowH, AllowedBrush);
            start = end;
        }
    }

    /// <summary>Draws faint vertical gridlines at fixed hour intervals.</summary>
    private void RenderGridlines(double slotW, double h)
    {
        const int slotsPerHour = 4; // 60min / 15min
        for (var hour = 0; hour <= 24; hour += GridlineHours)
        {
            var x = hour * slotsPerHour * slotW;
            GridCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = LineBrush, StrokeThickness = 1
            });
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
        const double labelHalfHeight = 9;
        var label = new TextBlock { Text = text, FontSize = 12 };
        Canvas.SetLeft(label, 2);
        Canvas.SetTop(label, y + rowH / 2 - labelHalfHeight);
        LabelCanvas.Children.Add(label);
    }

    /// <summary>
    /// Maps a pointer position to a (day, slot), applies the active tool, and
    /// redraws only when the slot actually changes — keeping drag-paint cheap.
    /// </summary>
    private void PaintAt(Point p)
    {
        double w = GridCanvas.ActualWidth, h = GridCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var day = (int)(p.Y / (h / DayCount));
        var slot = (int)(p.X / (w / Schedule.SlotsPerDay));
        if (day < 0 || day >= DayCount || slot < 0 || slot >= Schedule.SlotsPerDay) return;

        var allow = ToolAllow.IsChecked == true;
        if (_slots[day][slot] == allow) return;

        _slots[day][slot] = allow;
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
