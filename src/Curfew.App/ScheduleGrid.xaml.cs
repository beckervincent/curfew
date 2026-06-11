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
/// Interactive weekly allowed-time grid. The user paints in 30-minute cells
/// (7 days × 48 cells), but the schedule is stored at the underlying 15-minute
/// resolution; each painted cell sets the two 15-minute slots it covers.
/// Click and drag with the mouse to paint using the currently selected tool:
/// <c>Allow</c> (blue) or <c>Block</c> (grey). Days run Monday (top) to Sunday
/// (bottom); within each row time runs midnight (left) to midnight (right).
/// Quick-fill preset buttons set common patterns in one click.
/// </summary>
public sealed partial class ScheduleGrid : UserControl
{
    private const int DayCount = 7;
    private const int SlotsPerHour = 4; // 60 min / 15 min

    /// <summary>15-minute storage slots covered by one paintable 30-minute cell.</summary>
    private const int SlotsPerCell = 2;

    /// <summary>Paintable cells per day (48 thirty-minute cells over a 24-hour day).</summary>
    private static readonly int CellsPerDay = Schedule.SlotsPerDay / SlotsPerCell;

    private static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary><c>_slots[day][slot]</c> — <c>true</c> means usage is allowed.</summary>
    private readonly bool[][] _slots = NewGrid();

    /// <summary>True while a press-drag paint gesture is in progress.</summary>
    private bool _painting;

    private static readonly Brush AllowedBrush = new SolidColorBrush(Color.FromArgb(255, 0x4C, 0xA0, 0xF0));
    private static readonly Brush BlockedBrush = new SolidColorBrush(Color.FromArgb(28, 0x80, 0x80, 0x80));
    private static readonly Brush HourLineBrush = new SolidColorBrush(Color.FromArgb(38, 0x80, 0x80, 0x80));
    private static readonly Brush MajorLineBrush = new SolidColorBrush(Color.FromArgb(80, 0x80, 0x80, 0x80));

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

    // ---- Quick-fill presets ------------------------------------------------

    private void FillAll(bool allowed)
    {
        for (var d = 0; d < DayCount; d++)
            Array.Fill(_slots[d], allowed);
        Render();
    }

    /// <summary>Sets every slot of the given day to <paramref name="allowed"/>.</summary>
    private void SetDay(int day, bool allowed) => Array.Fill(_slots[day], allowed);

    /// <summary>Allows a contiguous hour window [fromHour, toHour) on the given day.</summary>
    private void AllowWindow(int day, int fromHour, int toHour)
    {
        for (var s = fromHour * SlotsPerHour; s < toHour * SlotsPerHour && s < Schedule.SlotsPerDay; s++)
            _slots[day][s] = true;
    }

    private void OnPresetAllowAll(object sender, RoutedEventArgs e) => FillAll(true);

    private void OnPresetBlockAll(object sender, RoutedEventArgs e) => FillAll(false);

    private void OnPresetWeeknights(object sender, RoutedEventArgs e)
    {
        for (var d = 0; d < DayCount; d++)
        {
            SetDay(d, false);
            if (d <= 4) AllowWindow(d, 16, 20); // Mon–Fri 4 PM–8 PM
        }
        Render();
    }

    private void OnPresetWeekends(object sender, RoutedEventArgs e)
    {
        for (var d = 0; d < DayCount; d++)
            SetDay(d, d >= 5); // Sat/Sun allowed, weekdays blocked
        Render();
    }

    // ---- Rendering ---------------------------------------------------------

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
            AddRect(0, y, w, rowH, BlockedBrush, 0);
            RenderAllowedRuns(d, y, rowH, slotW);
            AddLabel(Days[d], y, rowH);
        }

        RenderGridlines(slotW, rowH, h);
    }

    /// <summary>
    /// Draws the allowed (blue) blocks for one day, coalescing contiguous allowed
    /// slots into a single rounded rectangle so the visual tree stays small.
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
            // Inset within the row so day rows read as distinct bands.
            AddRect(start * slotW, y + 1, (end - start) * slotW, rowH - 2, AllowedBrush, 3);
            start = end;
        }
    }

    /// <summary>
    /// Faint vertical lines every hour, stronger lines every six hours, plus a
    /// separator between day rows.
    /// </summary>
    private void RenderGridlines(double slotW, double rowH, double h)
    {
        for (var hour = 0; hour <= 24; hour++)
        {
            var x = hour * SlotsPerHour * slotW;
            var major = hour % 6 == 0;
            GridCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = major ? MajorLineBrush : HourLineBrush,
                StrokeThickness = 1,
            });
        }

        var w = GridCanvas.ActualWidth;
        for (var d = 1; d < DayCount; d++)
        {
            var y = d * rowH;
            GridCanvas.Children.Add(new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = HourLineBrush, StrokeThickness = 1,
            });
        }
    }

    private void AddRect(double x, double y, double w, double h, Brush fill, double radius)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(0, w),
            Height = Math.Max(0, h),
            Fill = fill,
            RadiusX = radius,
            RadiusY = radius,
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        GridCanvas.Children.Add(rect);
    }

    private void AddLabel(string text, double y, double rowH)
    {
        const double labelHalfHeight = 9;
        var label = new TextBlock { Text = text, FontSize = 13 };
        Canvas.SetLeft(label, 2);
        Canvas.SetTop(label, y + rowH / 2 - labelHalfHeight);
        LabelCanvas.Children.Add(label);
    }

    // ---- Paint gestures ----------------------------------------------------

    /// <summary>
    /// Maps a pointer position to a (day, 30-minute cell), applies the active tool
    /// to both 15-minute slots the cell covers, and redraws only when something
    /// actually changes — keeping drag-paint cheap.
    /// </summary>
    private void PaintAt(Point p)
    {
        double w = GridCanvas.ActualWidth, h = GridCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var day = (int)(p.Y / (h / DayCount));
        var cell = (int)(p.X / (w / CellsPerDay));
        if (day < 0 || day >= DayCount || cell < 0 || cell >= CellsPerDay) return;

        var allow = ToolAllow.IsChecked == true;
        var first = cell * SlotsPerCell;
        var changed = false;
        for (var s = first; s < first + SlotsPerCell && s < Schedule.SlotsPerDay; s++)
        {
            if (_slots[day][s] == allow) continue;
            _slots[day][s] = allow;
            changed = true;
        }
        if (changed) Render();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _painting = true;
        GridCanvas.CapturePointer(e.Pointer);
        PaintAt(e.GetCurrentPoint(GridCanvas).Position);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_painting) return;

        // If the primary button was released while we missed the event (e.g. capture
        // was stolen), stop painting rather than smearing the grid on a hover.
        if (!e.GetCurrentPoint(GridCanvas).Properties.IsLeftButtonPressed)
        {
            _painting = false;
            return;
        }

        PaintAt(e.GetCurrentPoint(GridCanvas).Position);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _painting = false;
        GridCanvas.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// Pointer capture can be lost without a Released event (window deactivation, a
    /// system gesture, a touch being cancelled). Without resetting state the grid
    /// would keep painting on the next hover. Stop painting when capture is lost.
    /// </summary>
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        _painting = false;
}
