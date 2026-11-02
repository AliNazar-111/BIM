using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The analytical model over the plan, for the storey shown: each wall's plate as a blue line where
/// its plane is, each floor's as a green outline, each column a ringed node and each beam an orange
/// line, each named; the nodes as dots, and the supports in green - a triangle where a foundation
/// holds a node, a toothed line along the foot of a wall on a strip footing.
/// </summary>
public partial class PlanView
{
    private AnalyticalModel? _analytical;

    /// <summary>The analytical model shown over the plan, or none.</summary>
    public AnalyticalModel? Analytical
    {
        get => _analytical;
        set
        {
            _analytical = value;
            InvalidateVisual();
        }
    }

    private static readonly Brush AnalyticalMemberBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x59, 0x0C)));
    private static readonly Brush AnalyticalWallBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x19, 0x71, 0xC2)));
    private static readonly Brush AnalyticalPlateBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x9E, 0x44)));
    private static readonly Brush AnalyticalPlateFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x2F, 0x9E, 0x44)));
    private static readonly Brush AnalyticalSupportBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x8A, 0x3E)));
    private static readonly Pen AnalyticalMemberPen = Frozen(new Pen(AnalyticalMemberBrush, 2.4));
    private static readonly Pen AnalyticalWallPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0x19, 0x71, 0xC2)), 3.2) { DashStyle = new DashStyle(new double[] { 4, 1.5 }, 0) });
    private static readonly Pen AnalyticalPlatePen = Frozen(new Pen(AnalyticalPlateBrush, 1.4) { DashStyle = new DashStyle(new double[] { 6, 3 }, 0) });
    private static readonly Pen AnalyticalSupportPen = Frozen(new Pen(AnalyticalSupportBrush, 1.4));

    private void DrawAnalytical(DrawingContext dc)
    {
        if (_analytical is not { } model || Document is null || Document.FindLevel(ActiveLevelId) is not { } level) return;
        var low = level.Elevation - 1;
        var high = Document.Levels.Where(other => other.Elevation > level.Elevation + 1).Select(other => other.Elevation - 1).DefaultIfEmpty(double.MaxValue).Min();
        bool OnThisStorey(double z) => z >= low && z < high;
        Point At(Point3D point) => ModelToScreen(new Point2D(point.X, point.Y));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ink = new SolidColorBrush(AppTheme.Pick(Color.FromRgb(0x22, 0x26, 0x2E), Color.FromRgb(0xE4, 0xE8, 0xEE)));
        ink.Freeze();
        void Label(string text, Point at, Brush brush)
        {
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 10, brush, dpi);
            dc.DrawText(label, new Point(at.X + 4, at.Y - label.Height - 2));
        }

        // The plates: floors as outlines, walls as the line of their plane.
        foreach (var panel in model.Panels.Where(panel => panel.Outline.Count >= 3 && OnThisStorey(panel.Outline.Min(node => node.At.Z))))
        {
            if (panel.Kind == AnalyticalPanelKind.Wall)
            {
                var along = panel.Outline.Select(node => panel.Flat(node.At).X).ToList();
                var (from, to) = (Point3DOn(panel, along.Min()), Point3DOn(panel, along.Max()));
                dc.DrawLine(AnalyticalWallPen, At(from), At(to));
                var middle = new Point((At(from).X + At(to).X) / 2, (At(from).Y + At(to).Y) / 2);
                Label(panel.Name, middle, AnalyticalWallBrush);
                continue;
            }

            var outline = new StreamGeometry();
            using (var context = outline.Open())
            {
                context.BeginFigure(At(panel.Outline[0].At), true, true);
                foreach (var node in panel.Outline.Skip(1)) context.LineTo(At(node.At), true, false);
                foreach (var opening in panel.Openings.Where(opening => opening.Count >= 3))
                {
                    context.BeginFigure(At(opening[0]), true, true);
                    foreach (var point in opening.Skip(1)) context.LineTo(At(point), true, false);
                }
            }

            outline.FillRule = FillRule.EvenOdd;
            outline.Freeze();
            dc.DrawGeometry(AnalyticalPlateFill, AnalyticalPlatePen, outline);
            Label(panel.Name, At(panel.Outline.OrderBy(node => node.At.X + node.At.Y).First().At) + new Vector(4, -4), AnalyticalPlateBrush);
        }

        // The members: beams as lines, columns as ringed nodes.
        foreach (var member in model.Members.Where(member => OnThisStorey(Math.Min(member.Start.At.Z, member.End.At.Z))))
        {
            if (member.Kind == AnalyticalMemberKind.Column)
            {
                var at = At(member.Start.At);
                dc.DrawEllipse(null, AnalyticalMemberPen, at, 7, 7);
                dc.DrawLine(AnalyticalMemberPen, at + new Vector(-5, -5), at + new Vector(5, 5));
                dc.DrawLine(AnalyticalMemberPen, at + new Vector(-5, 5), at + new Vector(5, -5));
                Label(member.Name, at + new Vector(6, -4), AnalyticalMemberBrush);
                continue;
            }

            var (a, b) = (At(member.Start.At), At(member.End.At));
            dc.DrawLine(AnalyticalMemberPen, a, b);
            if (member.Kind != AnalyticalMemberKind.Joist) Label(member.Name, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), AnalyticalMemberBrush);
        }

        // The supports.
        foreach (var support in model.Supports)
        {
            if (support.Node is { } node && OnThisStorey(node.At.Z))
            {
                var at = At(node.At);
                var triangle = new StreamGeometry();
                using (var context = triangle.Open())
                {
                    context.BeginFigure(at, true, true);
                    context.LineTo(at + new Vector(-7, 11), true, false);
                    context.LineTo(at + new Vector(7, 11), true, false);
                }

                triangle.Freeze();
                dc.DrawGeometry(support.Fixity == SupportFixity.Fixed ? AnalyticalSupportBrush : null, AnalyticalSupportPen, triangle);
            }
            else if (support is { IsLine: true, From: { } from, To: { } to } && OnThisStorey(from.At.Z))
            {
                var (a, b) = (At(from.At), At(to.At));
                var way = b - a;
                if (way.Length < 1) continue;
                way.Normalize();
                var across = new Vector(-way.Y, way.X) * 5;
                dc.DrawLine(AnalyticalSupportPen, a + across, b + across);
                var length = (b - a).Length;
                for (var s = 6.0; s < length; s += 10)
                    dc.DrawLine(AnalyticalSupportPen, a + way * s + across, a + way * (s - 4) + across * 2);
            }
        }

        // The nodes.
        foreach (var node in model.Nodes.Where(node => OnThisStorey(node.At.Z)))
            dc.DrawEllipse(ink, null, At(node.At), 2.6, 2.6);
    }

    /// <summary>A point along a wall's plate in plan, so far along its line.</summary>
    private static Point3D Point3DOn(AnalyticalPanel panel, double along) =>
        new(panel.Origin.X + panel.U.X * along, panel.Origin.Y + panel.U.Y * along, 0);
}
