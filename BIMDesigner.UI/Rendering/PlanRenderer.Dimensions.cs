using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Dimensions in their style: witness lines standing off what is measured and running past the
/// line, the line closed by ticks, arrows or dots, the text along it - read from below or from
/// the right, as ISO 129 has it - or level. A string carries a value on each stretch. Angles are
/// drawn as an arc between their lines, radii and diameters through the centre, arc lengths as
/// an arc beside the wall. Every size is on the paper.
/// </summary>
public sealed partial class PlanRenderer
{
    private static readonly DimensionType Arrowed = new("Arrowed") { Tick = DimensionTick.Arrow };

    /// <summary>Paper millimetres to this surface's pixels.</summary>
    private double Paper(double millimetres) => Page(millimetres * ScreenPixelsPerPaperMm);

    private void DrawStyledDimension(DrawingContext dc, Dimension dimension)
    {
        if (Document is null) return;

        var type = DimensionTypes.Of(Document, dimension);
        var stations = dimension.Stations(Document);
        if (stations[0].DistanceTo(stations[^1]) < 1) return;

        var (lineFrom, _) = dimension.GetDimensionLine(Document);
        var offset = lineFrom - dimension.StartPoint(Document);
        var attached = dimension.IsAssociative(Document);
        var pen = IsSelected(dimension) ? _selectedPen : attached ? _dimensionPen : _looseDimensionPen;
        var brush = attached ? _dimensionTextBrush : _looseDimensionBrush;

        var labels = !string.IsNullOrWhiteSpace(dimension.Override)
            ? new[] { dimension.Override }
            : dimension.Segments(Document).Select(type.Format).ToArray();
        DrawLinear(dc, type, stations, offset, pen, brush, labels);
    }

    /// <summary>The dimension being drawn, in the style it will have.</summary>
    public void DrawPendingDimension(DrawingContext dc, IReadOnlyList<Point2D> stations, double offset, DimensionType type)
    {
        if (stations.Count < 2 || stations[0].DistanceTo(stations[^1]) < 1) return;
        var normal = (stations[^1] - stations[0]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var labels = Enumerable.Range(0, stations.Count - 1).Select(i => type.Format(stations[i].DistanceTo(stations[i + 1]))).ToArray();
        DrawLinear(dc, type, stations, normal * offset, _previewPen, _dimensionTextBrush, labels);
    }

    /// <summary>A run of dimensions along one line: witness lines at each station, the line, its marks and a value on each stretch - or one over all.</summary>
    private void DrawLinear(DrawingContext dc, DimensionType type, IReadOnlyList<Point2D> stations, Vector2D offset, Pen pen, Brush brush, IReadOnlyList<string> labels)
    {
        var onLine = stations.Select(station => station + offset).ToList();
        foreach (var (station, on) in stations.Zip(onLine))
            DrawWitnessLine(dc, pen, station, on, Paper(type.WitnessGap), Paper(type.WitnessExtension));

        var (a, b) = (ModelToScreen(onLine[0]), ModelToScreen(onLine[^1]));
        var along = b - a;
        if (along.Length < 1e-6) return;
        along.Normalize();

        var extension = type.Tick == DimensionTick.Arrow ? 0 : Paper(type.LineExtension);
        dc.DrawLine(pen, a - along * extension, b + along * extension);

        var points = onLine.Select(ModelToScreen).ToList();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            DrawMark(dc, type, pen, points[i], along);
            DrawMark(dc, type, pen, points[i + 1], -along);
        }

        if (labels.Count == 1 && points.Count > 2)
            DrawLabel(dc, labels[0], new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), along, type, brush);
        else
            for (var i = 0; i + 1 < points.Count && i < labels.Count; i++)
                DrawLabel(dc, labels[i], new Point((points[i].X + points[i + 1].X) / 2, (points[i].Y + points[i + 1].Y) / 2), along, type, brush);
    }

    /// <summary>The mark closing a line at a point: a stroke at 45°, an arrowhead pointing at the point from along the line, or a dot.</summary>
    private void DrawMark(DrawingContext dc, DimensionType type, Pen pen, Point at, Vector inward)
    {
        switch (type.Tick)
        {
            case DimensionTick.Arrow:
            {
                var (length, half) = (Paper(2.5), Paper(0.45));
                var across = new Vector(-inward.Y, inward.X);
                var head = new StreamGeometry();
                using (var context = head.Open())
                {
                    context.BeginFigure(at, true, true);
                    context.LineTo(at + inward * length + across * half, true, false);
                    context.LineTo(at + inward * length - across * half, true, false);
                }

                head.Freeze();
                dc.DrawGeometry(pen.Brush, null, head);
                break;
            }

            case DimensionTick.Dot:
                dc.DrawEllipse(pen.Brush, null, at, Paper(0.6), Paper(0.6));
                break;

            default:
            {
                // The stroke leans the same way whichever way the line runs.
                var slash = new Vector(inward.X - inward.Y, inward.Y + inward.X);
                if (slash.X < 0 || (Math.Abs(slash.X) < 1e-9 && slash.Y > 0)) slash = -slash;
                slash.Normalize();
                dc.DrawLine(new Pen(pen.Brush, pen.Thickness * 1.6), at - slash * Paper(1.25), at + slash * Paper(1.25));
                break;
            }
        }
    }

    /// <summary>
    /// A dimension's value: along the line, above it, turned to read from below or from the right
    /// - or level, on a backdrop, for a style that keeps text level.
    /// </summary>
    private void DrawLabel(DrawingContext dc, string label, Point middle, Vector along, DimensionType type, Brush brush)
    {
        // The size asked for is the height of a capital; type is set a little larger than that.
        var text = Text(label, Paper(type.TextSize / 0.72), brush);
        if (!type.TextAlongLine)
        {
            var origin = new Point(middle.X - text.Width / 2, middle.Y - text.Height / 2);
            dc.DrawRoundedRectangle(_labelBackdrop, null, new Rect(origin.X - Page(4), origin.Y - Page(1), text.Width + Page(8), text.Height + Page(2)), Page(2), Page(2));
            dc.DrawText(text, origin);
            return;
        }

        var angle = Math.Atan2(along.Y, along.X) * 180 / Math.PI;
        if (angle > 90.01 || angle <= -90)
        {
            angle += 180;
            along = -along;
        }

        // Above the line as the text reads.
        var up = new Vector(along.Y, -along.X);
        var centre = middle + up * (Paper(type.TextOffset) + text.Height / 2);
        dc.PushTransform(new RotateTransform(angle, centre.X, centre.Y));
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
        dc.Pop();
    }

    /// <summary>An angular or radial dimension being placed, drawn as it will be.</summary>
    public void DrawPreview(DrawingContext dc, BIMDesigner.Core.Elements.Element dimension)
    {
        if (dimension is AngularDimension angular) DrawAngularDimension(dc, angular);
        else if (dimension is RadialDimension radial) DrawRadialDimension(dc, radial);
    }

    /// <summary>An angle: the arc between its two lines, at the radius it was placed at, extension lines out to it, its value along the arc.</summary>
    private void DrawAngularDimension(DrawingContext dc, AngularDimension dimension)
    {
        if (Document is null || dimension.Geometry(Document) is not { } geometry) return;

        var type = DimensionTypes.Of(Document, dimension);
        var pen = IsSelected(dimension) ? _selectedPen : _dimensionPen;
        var (centre, from, to, _, radius) = geometry;

        // Out from where each line was picked, to the arc.
        foreach (var (reference, direction) in new[] { (dimension.First, from), (dimension.Second, to) })
        {
            var reach = (reference.Resolve(Document) - centre).Dot(direction);
            if (radius > reach + 1)
                DrawWitnessLine(dc, pen, centre + direction * Math.Max(reach, 0), centre + direction * radius, Paper(type.WitnessGap), Paper(type.WitnessExtension));
        }

        var start = Math.Atan2(from.Y, from.X);
        var sweep = Math.Atan2(from.Cross(to), from.Dot(to));
        DrawArc(dc, pen, type, centre, radius, start, sweep, dimension.DisplayText(Document), _dimensionTextBrush);
    }

    /// <summary>A radius or diameter through the centre to where it was placed; an arc length beside the wall, out at the radius it was placed at.</summary>
    private void DrawRadialDimension(DrawingContext dc, RadialDimension dimension)
    {
        if (Document is null || dimension.Arc(Document) is not { } arc) return;

        var type = DimensionTypes.Of(Document, dimension);
        var pen = IsSelected(dimension) ? _selectedPen : _dimensionPen;
        var label = dimension.DisplayText(Document);

        if (dimension.Kind == RadialKind.ArcLength)
        {
            var outward = Math.Max(dimension.Through.DistanceTo(arc.Centre), 1);
            foreach (var angle in new[] { arc.StartAngle, arc.StartAngle + arc.Sweep })
            {
                var direction = new Vector2D(Math.Cos(angle), Math.Sin(angle));
                DrawWitnessLine(dc, pen, arc.Centre + direction * arc.Radius, arc.Centre + direction * outward, Paper(type.WitnessGap), Paper(type.WitnessExtension));
            }

            DrawArc(dc, pen, type, arc.Centre, outward, arc.StartAngle, arc.Sweep, label, _dimensionTextBrush);
            return;
        }

        // The way it is drawn: toward where it was placed; a diameter right across.
        var toward = (dimension.Through - arc.Centre).NormalisedOrDefault(Vector2D.UnitX);
        var onArc = ModelToScreen(arc.Centre + toward * arc.Radius);
        var from = ModelToScreen(dimension.Kind == RadialKind.Diameter ? arc.Centre - toward * arc.Radius : arc.Centre);
        dc.DrawLine(pen, from, onArc);

        var along = onArc - from;
        if (along.Length < 1e-6) return;
        along.Normalize();
        // A radius and a diameter end in arrows on the arc, whatever the style closes a straight line with.
        var arrow = type.Tick == DimensionTick.Tick ? Arrowed : type;
        DrawMark(dc, arrow, pen, onArc, -along);
        if (dimension.Kind == RadialKind.Diameter) DrawMark(dc, arrow, pen, from, along);
        else
        {
            // The centre marked with a small cross.
            var mark = Paper(1.2);
            dc.DrawLine(pen, from - new Vector(mark, 0), from + new Vector(mark, 0));
            dc.DrawLine(pen, from - new Vector(0, mark), from + new Vector(0, mark));
        }

        DrawLabel(dc, label, new Point((from.X + onArc.X) / 2, (from.Y + onArc.Y) / 2), along, type, _dimensionTextBrush);
    }

    /// <summary>An arc about a centre, from an angle through a sweep (radians, anticlockwise positive), closed at both ends, its value at its middle along it.</summary>
    private void DrawArc(DrawingContext dc, Pen pen, DimensionType type, Point2D centre, double radius, double start, double sweep, string label, Brush brush)
    {
        Point2D At(double angle) => centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * radius;

        var pieces = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 90)), 2, 360);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ModelToScreen(At(start)), false, false);
            for (var i = 1; i <= pieces; i++) context.LineTo(ModelToScreen(At(start + sweep * i / pieces)), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // The ends closed along the arc's tangent, pointing at its ends.
        Vector Tangent(double angle, double way)
        {
            var a = ModelToScreen(At(angle));
            var b = ModelToScreen(At(angle + way * 0.001 * Math.Sign(sweep)));
            var tangent = b - a;
            if (tangent.Length < 1e-12) return new Vector(1, 0);
            tangent.Normalize();
            return tangent;
        }

        DrawMark(dc, type, pen, ModelToScreen(At(start)), Tangent(start, 1));
        DrawMark(dc, type, pen, ModelToScreen(At(start + sweep)), Tangent(start + sweep, -1));

        var middle = start + sweep / 2;
        DrawLabel(dc, label, ModelToScreen(At(middle)), Tangent(middle, 1), type, brush);
    }
}
