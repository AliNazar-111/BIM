using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Coordination;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A construction programme as a bar chart: each task a bar from its start to its finish - red on
/// the critical path, blue off it - filled as far as it is done, its baseline a thin grey line
/// under it, a milestone a diamond; the weeks along the top, the status date and the date being
/// simulated as lines down it.
/// </summary>
public sealed class GanttChart : FrameworkElement
{
    private const double RowHeight = 22;
    private const double NameWidth = 260;
    private const double HeaderHeight = 34;

    private static readonly Brush Critical = Frozen(new SolidColorBrush(Color.FromRgb(0xD0, 0x45, 0x3A)));
    private static readonly Brush Normal = Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0x7D, 0xD9)));
    private static readonly Brush Done = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x30, 0x10)));
    private static readonly Brush Baseline = Frozen(new SolidColorBrush(Color.FromRgb(0x90, 0x94, 0x9A)));
    private static readonly Brush Text = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Pen Grid = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x88, 0x90))), 1));
    private static readonly Pen StatusPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xB8, 0x00))), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen DatePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43))), 2));

    private IReadOnlyList<TaskTimes> _times = Array.Empty<TaskTimes>();
    private DateOnly _first;
    private DateOnly _last;
    private DateOnly? _status;
    private DateOnly? _shown;

    /// <summary>The programme to draw, with the date progress is measured to and the date being shown.</summary>
    public void Show(IReadOnlyList<TaskTimes> times, DateOnly? status, DateOnly? shown)
    {
        _times = times;
        _status = status;
        _shown = shown;
        var dates = times.SelectMany(timed => new[] { timed.Start, timed.Finish }
                .Concat(timed.Task.BaselineStart is { } baseline ? new[] { baseline } : Array.Empty<DateOnly>())
                .Concat(timed.Task.BaselineFinish is { } baseFinish ? new[] { baseFinish } : Array.Empty<DateOnly>()))
            .ToList();
        _first = dates.Count == 0 ? DateOnly.FromDateTime(DateTime.Today) : dates.Min();
        _last = dates.Count == 0 ? _first.AddDays(30) : dates.Max().AddDays(1);
        Height = HeaderHeight + Math.Max(1, times.Count) * RowHeight + 8;
        InvalidateVisual();
    }

    private double DayWidth => Math.Max(1.5, (ActualWidth - NameWidth - 12) / Math.Max(1, _last.DayNumber - _first.DayNumber + 1));

    private double X(DateOnly date) => NameWidth + (date.DayNumber - _first.DayNumber) * DayWidth;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_times.Count == 0 || ActualWidth <= NameWidth + 20) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Words(string text, double size = 11) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Text, dpi);

        // The weeks, by their Mondays; the months above them.
        var monday = _first.AddDays(-(((int)_first.DayOfWeek + 6) % 7));
        for (var week = monday; week <= _last; week = week.AddDays(7))
        {
            var x = X(week);
            if (x >= NameWidth) dc.DrawLine(Grid, new Point(x, HeaderHeight - 12), new Point(x, ActualHeight));
            if (DayWidth * 7 >= 34 && x >= NameWidth) dc.DrawText(Words(week.ToString("d MMM", CultureInfo.CurrentCulture), 10), new Point(x + 2, HeaderHeight - 14));
        }

        for (var month = new DateOnly(_first.Year, _first.Month, 1); month <= _last; month = month.AddMonths(1))
            if (X(month) >= NameWidth - 1) dc.DrawText(Words(month.ToString("MMMM yyyy", CultureInfo.CurrentCulture)), new Point(X(month) + 2, 2));

        for (var i = 0; i < _times.Count; i++)
        {
            var timed = _times[i];
            var task = timed.Task;
            var top = HeaderHeight + i * RowHeight;
            dc.DrawLine(Grid, new Point(0, top + RowHeight), new Point(ActualWidth, top + RowHeight));

            var name = Words($"{i + 1}  {task.Name}");
            name.MaxTextWidth = NameWidth - 8;
            name.MaxLineCount = 1;
            name.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(name, new Point(4, top + 4));

            var fill = timed.Critical ? Critical : Normal;
            if (timed.IsMilestone)
            {
                var x = X(timed.Start);
                var diamond = new StreamGeometry();
                using (var context = diamond.Open())
                {
                    context.BeginFigure(new Point(x, top + 4), true, true);
                    context.LineTo(new Point(x + 7, top + 11), true, false);
                    context.LineTo(new Point(x, top + 18), true, false);
                    context.LineTo(new Point(x - 7, top + 11), true, false);
                }

                diamond.Freeze();
                dc.DrawGeometry(fill, null, diamond);
                continue;
            }

            var (left, right) = (X(timed.Start), X(timed.Finish.AddDays(1)));
            if (task.BaselineStart is { } baseStart && task.BaselineFinish is { } baseFinish)
                dc.DrawRectangle(Baseline, null, new Rect(X(baseStart), top + RowHeight - 6, Math.Max(2, X(baseFinish.AddDays(1)) - X(baseStart)), 3));
            dc.DrawRoundedRectangle(fill, null, new Rect(left, top + 4, Math.Max(3, right - left), RowHeight - 11), 2, 2);
            if (task.PercentComplete > 0)
                dc.DrawRectangle(Done, null, new Rect(left, top + 9, Math.Max(1, (right - left) * task.PercentComplete / 100), 4));
        }

        if (_status is { } status) dc.DrawLine(StatusPen, new Point(X(status), HeaderHeight - 12), new Point(X(status), ActualHeight));
        if (_shown is { } shown) dc.DrawLine(DatePen, new Point(X(shown) + DayWidth / 2, HeaderHeight - 12), new Point(X(shown) + DayWidth / 2, ActualHeight));
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// The cost over the programme: the planned spend as a running total - the S-curve - with, at the
/// status date, the value earned and the cost so far.
/// </summary>
public sealed class CostCurve : FrameworkElement
{
    private static readonly Pen Planned = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3C, 0x7D, 0xD9))), 2));
    private static readonly Brush Earned = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
    private static readonly Brush Actual = Frozen(new SolidColorBrush(Color.FromRgb(0xD0, 0x45, 0x3A)));
    private static readonly Brush Text = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD4)));
    private static readonly Pen Axis = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x88, 0x90))), 1));

    private IReadOnlyList<(DateOnly Week, decimal Spend, decimal Cumulative)> _flow = Array.Empty<(DateOnly, decimal, decimal)>();
    private EarnedValue? _value;
    private string _currency = "£";

    public void Show(IReadOnlyList<(DateOnly Week, decimal Spend, decimal Cumulative)> flow, EarnedValue? value, string currency)
    {
        (_flow, _value, _currency) = (flow, value, currency);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Words(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Text, dpi);
        if (_flow.Count == 0)
        {
            dc.DrawText(Words("No costed tasks in the programme yet."), new Point(8, 8));
            return;
        }

        var (left, bottom, top, right) = (70.0, ActualHeight - 24, 12.0, ActualWidth - 12);
        var first = _flow[0].Week.DayNumber;
        var last = Math.Max(first + 7, _flow[^1].Week.DayNumber + 7);
        var most = (double)Math.Max(1, Math.Max(_flow[^1].Cumulative, Math.Max(_value?.Actual ?? 0, _value?.Earned ?? 0)));
        double X(int day) => left + (right - left) * (day - first) / (last - first);
        double Y(decimal amount) => bottom - (bottom - top) * (double)amount / most;

        dc.DrawLine(Axis, new Point(left, top), new Point(left, bottom));
        dc.DrawLine(Axis, new Point(left, bottom), new Point(right, bottom));
        dc.DrawText(Words($"{_currency}{most:N0}"), new Point(4, top - 6));
        dc.DrawText(Words(_flow[0].Week.ToString("d MMM yy")), new Point(left, bottom + 4));
        dc.DrawText(Words(_flow[^1].Week.AddDays(7).ToString("d MMM yy")), new Point(right - 60, bottom + 4));

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(new Point(X(first), bottom), false, false);
            foreach (var (week, _, cumulative) in _flow) context.LineTo(new Point(X(week.DayNumber + 7), Y(cumulative)), true, false);
        }

        curve.Freeze();
        dc.DrawGeometry(null, Planned, curve);

        if (_value is { } value)
        {
            var at = X(value.StatusDate.DayNumber);
            dc.DrawEllipse(Earned, null, new Point(at, Y(value.Earned)), 5, 5);
            dc.DrawEllipse(Actual, null, new Point(at, Y(value.Actual)), 5, 5);
            dc.DrawText(Words("planned"), new Point(right - 150, top));
            dc.DrawText(Words("earned"), new Point(right - 150, top + 14));
            dc.DrawText(Words("actual cost"), new Point(right - 150, top + 28));
            dc.DrawLine(Planned, new Point(right - 170, top + 7), new Point(right - 155, top + 7));
            dc.DrawEllipse(Earned, null, new Point(right - 162, top + 21), 4, 4);
            dc.DrawEllipse(Actual, null, new Point(right - 162, top + 35), 4, 4);
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
