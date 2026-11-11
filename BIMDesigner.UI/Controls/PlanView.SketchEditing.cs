using System.Windows.Input;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Changing a sketch's lines the ways Revit's Modify panel does while sketching: a corner dragged
/// to a new place, the lines meeting there following it; the selected lines moved, mirrored or
/// rotated - or copied so - arrayed or scaled; a line aligned onto another; two lines rounded off
/// into a fillet arc. A roof's footprint, a floor's or a ceiling's boundary alike. A line moved
/// off the wall, gridline or edge it was picked from no longer follows it: it stays where it is put.
/// </summary>
public partial class PlanView
{
    /// <summary>The corner being dragged, where it was when the drag began, and where it is now.</summary>
    private (Point2D From, Point2D At)? _sketchDrag;

    /// <summary>Whether Mirror makes copies of the lines - as Revit's does to start with - rather than moving them.</summary>
    public bool SketchMirrorCopy { get; set; } = true;

    /// <summary>Whether Move makes copies of the lines rather than moving them.</summary>
    public bool SketchMoveCopy { get; set; }

    /// <summary>The radius Fillet Arc rounds two lines off at, mm.</summary>
    public double SketchFilletRadius { get; set; } = 1000;

    /// <summary>Whether Rotate turns copies of the lines and leaves them where they are.</summary>
    public bool SketchRotateCopy { get; set; }

    /// <summary>The angle Rotate turns the lines by about the centre clicked, degrees anticlockwise; none to pick it with two more clicks.</summary>
    public double SketchRotateAngle { get; set; }

    /// <summary>How many Array makes in all, the lines selected the first of them.</summary>
    public int SketchArrayCount { get; set; } = 3;

    /// <summary>Whether Array repeats the lines round a centre clicked, rather than along a step.</summary>
    public bool SketchArrayRadial { get; set; }

    /// <summary>How far round a radial Array goes, degrees anticlockwise: 360 spaces them evenly all the way round; less puts the last that far round from the first.</summary>
    public double SketchArrayAngle { get; set; } = 360;

    /// <summary>How much Scale makes the lines bigger: 2 doubles them, 0.5 halves them.</summary>
    public double SketchScaleFactor { get; set; } = 2;

    /// <summary>Whether a corner of the sketch is being dragged.</summary>
    public bool IsDraggingSketchCorner => _sketchDrag is not null;

    // ---- dragging a corner ----------------------------------------------------------------

    /// <summary>The line end nearest a point, within a few pixels, or null.</summary>
    private Point2D? SketchCornerAt(Point2D raw)
    {
        if (_sketch is null) return null;
        var reach = 7 / PixelsPerMm;
        Point2D? best = null;
        foreach (var end in _sketch.SelectMany(line => new[] { line.Start, line.End }))
        {
            var distance = end.DistanceTo(raw);
            if (distance >= reach) continue;
            reach = distance;
            best = end;
        }

        return best;
    }

    /// <summary>
    /// Starts dragging the corner under the cursor, while selecting lines: every line end there
    /// goes with it, so a corner stays a corner. Public so it can be driven from tests.
    /// </summary>
    public bool BeginSketchDragAt(Point2D raw)
    {
        if (_sketch is null || _sketchTool != RoofSketchTool.Modify || SketchCornerAt(raw) is not { } corner) return false;

        Remember();
        _sketchDrag = (corner, corner);
        CaptureMouse();
        HintChanged?.Invoke(this, "Drag the corner where it should go; the lines meeting there follow it.");
        return true;
    }

    /// <summary>Moves the corner being dragged to under the cursor, snapped.</summary>
    public bool SketchDragTo(Point2D raw)
    {
        if (_sketch is null || _sketchDrag is not var (from, at)) return false;

        var to = SketchSnapExcept(raw, at);
        MoveSketchCorner(at, to);
        _sketchDrag = (from, to);
        InvalidateVisual();
        return true;
    }

    /// <summary>Lets the corner go where it was dragged to.</summary>
    public bool EndSketchDrag()
    {
        if (_sketchDrag is not var (from, at)) return false;

        _sketchDrag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();

        // A click that did not move the corner changed nothing: there is nothing to undo.
        if (from.DistanceTo(at) <= RoofSketch.JoinTolerance && _sketchUndo.Count > 0) _sketchUndo.RemoveAt(_sketchUndo.Count - 1);
        AfterSketchEdit(from.DistanceTo(at) <= RoofSketch.JoinTolerance ? null : $"Corner moved {Units.FormatLength(from.DistanceTo(at))}.");
        return true;
    }

    /// <summary>Every line end at one point moved to another.</summary>
    private void MoveSketchCorner(Point2D from, Point2D to)
    {
        if (_sketch is null) return;
        foreach (var line in _sketch)
        {
            if (line.Start.DistanceTo(from) <= RoofSketch.JoinTolerance) line.Start = to;
            if (line.End.DistanceTo(from) <= RoofSketch.JoinTolerance) line.End = to;
        }
    }

    /// <summary>Snaps a point the way drawn points snap, but never to the corner being moved itself.</summary>
    private Point2D SketchSnapExcept(Point2D raw, Point2D moving)
    {
        var reach = SnapPixelRadius / PixelsPerMm;
        Point2D? best = null;
        foreach (var end in (_sketch ?? new List<RoofSketchLine>()).SelectMany(line => new[] { line.Start, line.End }))
        {
            if (end.DistanceTo(moving) <= RoofSketch.JoinTolerance) continue;
            var distance = end.DistanceTo(raw);
            if (distance >= reach) continue;
            reach = distance;
            best = end;
        }

        return best ?? SnapPoint(raw, null, out _);
    }

    // ---- move and mirror -----------------------------------------------------------------

    /// <summary>
    /// Move: the first click is the point to move from, the second where it goes - the selected
    /// lines go with it, or are copied there with Copy on.
    /// </summary>
    private bool SketchMoveAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (_sketchSelection.Count == 0)
        {
            HintChanged?.Invoke(this, "Select the lines to move first (Select Lines), then Move: click where from, then where to.");
            return false;
        }

        var point = SketchSnap(raw);
        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, "Click where that point goes.");
            InvalidateVisual();
            return true;
        }

        var by = point - from;
        if (by.Length <= RoofSketch.JoinTolerance) return false;

        Remember();
        var lines = SketchMoveCopy ? _sketchSelection.Select(line => line.Copy()).ToList() : _sketchSelection.ToList();
        foreach (var line in lines)
        {
            line.Start += by;
            line.End += by;
            Loose(line);
        }

        if (SketchMoveCopy)
        {
            _sketch.AddRange(lines);
            _sketchSelection.Clear();
            _sketchSelection.AddRange(lines);
        }

        CarryArrows(SketchMoveCopy, point => point + by);

        _sketchStart = null;
        AfterSketchEdit($"{(SketchMoveCopy ? "Copied" : "Moved")} {Units.FormatLength(by.Length)}.");
        return true;
    }

    /// <summary>
    /// Mirror - Draw Axis: two clicks are the line to mirror about; the selected lines are
    /// mirrored across it, copied unless Copy is off.
    /// </summary>
    private bool SketchMirrorAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (_sketchSelection.Count == 0)
        {
            HintChanged?.Invoke(this, "Select the lines to mirror first (Select Lines), then Mirror: click two points on the line to mirror about.");
            return false;
        }

        var point = SketchSnap(raw);
        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, "Click a second point on the line to mirror about.");
            InvalidateVisual();
            return true;
        }

        if (from.DistanceTo(point) <= RoofSketch.JoinTolerance) return false;

        Remember();
        var axis = (point - from).NormalisedOrDefault(Vector2D.UnitX);
        Point2D Reflect(Point2D p)
        {
            var along = (p - from).Dot(axis);
            var foot = from + axis * along;
            return foot + (foot - p);
        }

        var lines = SketchMirrorCopy ? _sketchSelection.Select(line => line.Copy()).ToList() : _sketchSelection.ToList();
        foreach (var line in lines)
        {
            (line.Start, line.End) = (Reflect(line.Start), Reflect(line.End));

            // A reflection turns left into right: an arc bows the other way.
            line.Bulge = -line.Bulge;
            Loose(line);
        }

        if (SketchMirrorCopy)
        {
            _sketch.AddRange(lines);
            _sketchSelection.Clear();
            _sketchSelection.AddRange(lines);
        }

        CarryArrows(SketchMirrorCopy, Reflect);

        _sketchStart = null;
        AfterSketchEdit(SketchMirrorCopy ? "Mirrored copies made." : "Mirrored.");
        return true;
    }

    // ---- rotate, array and scale ------------------------------------------------------------

    /// <summary>
    /// A line moved off the wall, gridline or floor's or roof's edge it was picked from: it no longer follows it,
    /// but stays where it is put - or, finished, it would be put back on what it was picked from.
    /// </summary>
    private static void Loose(RoofSketchLine line)
    {
        line.Edge.WallId = null;
        line.Edge.GridId = null;
        line.Edge.SlabId = null;
        line.Edge.SlabEdgeId = null;
    }

    /// <summary>The selected slope arrows put through the same change as the lines - or copies of them, added and selected.</summary>
    private void CarryArrows(bool copy, Func<Point2D, Point2D> to)
    {
        var arrows = copy ? _sketchArrowSelection.Select(arrow => arrow.Copy()).ToList() : _sketchArrowSelection.ToList();
        foreach (var arrow in arrows) (arrow.Tail, arrow.Head) = (to(arrow.Tail), to(arrow.Head));
        if (!copy) return;
        _sketchArrows.AddRange(arrows);
        _sketchArrowSelection.Clear();
        _sketchArrowSelection.AddRange(arrows);
    }

    /// <summary>A point turned about a centre, degrees anticlockwise.</summary>
    private static Point2D Turned(Point2D point, Point2D centre, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        var (x, y) = (point.X - centre.X, point.Y - centre.Y);
        return new Point2D(centre.X + x * cos - y * sin, centre.Y + x * sin + y * cos);
    }

    /// <summary>
    /// The angle between two clicks about a centre, anticlockwise: to the whole degree, and to the
    /// nearest 15 degrees within 3 of it, as Rotate picks it for the model.
    /// </summary>
    private static double SketchTurn(Point2D centre, Point2D from, Point2D to)
    {
        var (a, b) = (from - centre, to - centre);
        if (a.Length <= RoofSketch.JoinTolerance || b.Length <= RoofSketch.JoinTolerance) return 0;
        var degrees = Math.Atan2(a.Cross(b), a.Dot(b)) * 180 / Math.PI;
        var fifteen = Math.Round(degrees / 15) * 15;
        return Math.Abs(degrees - fifteen) <= 3 ? fifteen : Math.Round(degrees);
    }

    /// <summary>
    /// Rotate: the first click is the centre the selected lines turn about. With an Angle typed on the
    /// options bar they turn by it at once; without, the next two clicks are where the angle starts and
    /// where it ends. Copy turns copies and leaves the lines where they are.
    /// </summary>
    private bool SketchRotateAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (_sketchSelection.Count == 0)
        {
            HintChanged?.Invoke(this, "Select the lines to rotate first (Select Lines), then Rotate: click the centre, then where the angle starts and ends.");
            return false;
        }

        var point = SketchSnap(raw);
        if (_sketchStart is not { } centre)
        {
            if (Math.Abs(SketchRotateAngle) > 1e-9) return RotateSketchLines(point, SketchRotateAngle);
            _sketchStart = point;
            HintChanged?.Invoke(this, "Click where the angle starts from.");
            InvalidateVisual();
            return true;
        }

        if (_sketchArcEnd is not { } from)
        {
            if (point.DistanceTo(centre) <= RoofSketch.JoinTolerance) return false;
            _sketchArcEnd = point;
            HintChanged?.Invoke(this, "Click where the angle ends. Esc starts again.");
            InvalidateVisual();
            return true;
        }

        var degrees = SketchTurn(centre, from, point);
        if (Math.Abs(degrees) < 1e-9) return false;
        _sketchStart = null;
        _sketchArcEnd = null;
        return RotateSketchLines(centre, degrees);
    }

    /// <summary>The selected lines turned about a centre - or copies of them, added and selected.</summary>
    public bool RotateSketchLines(Point2D centre, double degrees)
    {
        if (_sketch is null || _sketchSelection.Count == 0 || Math.Abs(degrees) < 1e-9) return false;

        Remember();
        var lines = SketchRotateCopy ? _sketchSelection.Select(line => line.Copy()).ToList() : _sketchSelection.ToList();
        foreach (var line in lines)
        {
            (line.Start, line.End) = (Turned(line.Start, centre, degrees), Turned(line.End, centre, degrees));
            Loose(line);
        }

        if (SketchRotateCopy)
        {
            _sketch.AddRange(lines);
            _sketchSelection.Clear();
            _sketchSelection.AddRange(lines);
        }

        CarryArrows(SketchRotateCopy, point => Turned(point, centre, degrees));

        AfterSketchEdit($"{(SketchRotateCopy ? "Copies turned" : "Turned")} {Math.Abs(degrees):0.##}° {(degrees > 0 ? "anticlockwise" : "clockwise")}.");
        return true;
    }

    /// <summary>
    /// Array: click where from, then where the next one goes - the selected lines are copied that far
    /// on again and again, as many in all as the Count on the options bar, the lines themselves the first.
    /// </summary>
    private bool SketchArrayAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (_sketchSelection.Count == 0)
        {
            HintChanged?.Invoke(this, "Select the lines to repeat first (Select Lines), then Array: click where from, then where the next one goes.");
            return false;
        }

        var point = SketchSnap(raw);
        if (SketchArrayRadial) return ArraySketchLinesRound(point, Math.Max(2, SketchArrayCount), SketchArrayAngle);
        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, $"Click where the next one goes: {Math.Max(2, SketchArrayCount)} in all, each that far on from the last.");
            InvalidateVisual();
            return true;
        }

        var by = point - from;
        if (by.Length <= RoofSketch.JoinTolerance) return false;
        _sketchStart = null;
        return ArraySketchLines(by, Math.Max(2, SketchArrayCount));
    }

    /// <summary>The selected lines copied a step on, again and again, as many in all as the count - the copies made selected.</summary>
    public bool ArraySketchLines(Vector2D step, int count)
    {
        if (_sketch is null || _sketchSelection.Count == 0 || count < 2 || step.Length <= RoofSketch.JoinTolerance) return false;

        Remember();
        var originals = _sketchSelection.ToList();
        var arrows = _sketchArrowSelection.ToList();
        var made = new List<RoofSketchLine>();
        var madeArrows = new List<RoofSlopeArrow>();
        for (var k = 1; k < count; k++)
        {
            foreach (var line in originals)
            {
                var copy = line.Copy();
                copy.Start += step * k;
                copy.End += step * k;
                Loose(copy);
                made.Add(copy);
            }

            foreach (var arrow in arrows)
            {
                var copy = arrow.Copy();
                (copy.Tail, copy.Head) = (copy.Tail + step * k, copy.Head + step * k);
                madeArrows.Add(copy);
            }
        }

        _sketch.AddRange(made);
        _sketchSelection.Clear();
        _sketchSelection.AddRange(made);
        _sketchArrows.AddRange(madeArrows);
        _sketchArrowSelection.Clear();
        _sketchArrowSelection.AddRange(madeArrows);
        AfterSketchEdit($"{count - 1} {(count == 2 ? "copy" : "copies")} made, {Units.FormatLength(step.Length)} apart.");
        return true;
    }

    /// <summary>
    /// The selected lines repeated round a centre - as many in all as the count, the lines themselves the first: all the
    /// way round evenly at 360°, or the last so far round from the first at less - the copies made selected.
    /// </summary>
    public bool ArraySketchLinesRound(Point2D centre, int count, double angle)
    {
        if (_sketch is null || _sketchSelection.Count == 0 || count < 2 || Math.Abs(angle) < 1e-9) return false;
        var full = Math.Abs(Math.Abs(angle) - 360) < 1e-9;
        var step = full ? angle / count : angle / (count - 1);

        Remember();
        var originals = _sketchSelection.ToList();
        var arrows = _sketchArrowSelection.ToList();
        var made = new List<RoofSketchLine>();
        var madeArrows = new List<RoofSlopeArrow>();
        for (var k = 1; k < count; k++)
        {
            var turn = step * k;
            foreach (var line in originals)
            {
                var copy = line.Copy();
                (copy.Start, copy.End) = (Turned(copy.Start, centre, turn), Turned(copy.End, centre, turn));
                Loose(copy);
                made.Add(copy);
            }

            foreach (var arrow in arrows)
            {
                var copy = arrow.Copy();
                (copy.Tail, copy.Head) = (Turned(copy.Tail, centre, turn), Turned(copy.Head, centre, turn));
                madeArrows.Add(copy);
            }
        }

        _sketch.AddRange(made);
        _sketchSelection.Clear();
        _sketchSelection.AddRange(made);
        _sketchArrows.AddRange(madeArrows);
        _sketchArrowSelection.Clear();
        _sketchArrowSelection.AddRange(madeArrows);
        AfterSketchEdit($"{count - 1} {(count == 2 ? "copy" : "copies")} made round the centre, {Math.Abs(step):0.##}° apart.");
        return true;
    }

    /// <summary>
    /// Scale: click the point the selected lines grow from or shrink toward, by the Scale on the
    /// options bar - each line that much longer and that much further from it, an arc keeping its shape.
    /// </summary>
    private bool SketchScaleAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (_sketchSelection.Count == 0)
        {
            HintChanged?.Invoke(this, "Select the lines to scale first (Select Lines), then Scale: type the scale, and click the point it is scaled about.");
            return false;
        }

        return ScaleSketchLines(SketchSnap(raw), SketchScaleFactor);
    }

    /// <summary>The selected lines scaled about a point.</summary>
    public bool ScaleSketchLines(Point2D about, double factor)
    {
        if (_sketch is null || _sketchSelection.Count == 0) return false;
        if (factor <= 0 || Math.Abs(factor - 1) < 1e-9)
        {
            HintChanged?.Invoke(this, "Type a scale on the options bar first: 2 doubles the lines, 0.5 halves them.");
            return false;
        }

        Remember();
        foreach (var line in _sketchSelection)
        {
            (line.Start, line.End) = (about + (line.Start - about) * factor, about + (line.End - about) * factor);
            Loose(line);
        }

        CarryArrows(false, point => about + (point - about) * factor);

        AfterSketchEdit($"Scaled by {factor:0.###} about the point clicked.");
        return true;
    }

    // ---- align ----------------------------------------------------------------------------

    /// <summary>
    /// Align: click the line to align to, then a line running the same way - it is brought onto
    /// the first, square across, and the lines meeting its ends follow it.
    /// </summary>
    private bool SketchAlignAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (SketchLineAt(raw) is not { IsArc: false } line)
        {
            HintChanged?.Invoke(this, "Click a straight sketch line.");
            return false;
        }

        if (_trimFirst is not var (reference, _) || ReferenceEquals(reference, line))
        {
            _trimFirst = (line, raw);
            HintChanged?.Invoke(this, "Now click a line running the same way: it is brought into line with this one.");
            InvalidateVisual();
            return true;
        }

        var way = (reference.End - reference.Start).NormalisedOrDefault(Vector2D.UnitX);
        var other = (line.End - line.Start).NormalisedOrDefault(Vector2D.UnitX);
        if (Math.Abs(way.Cross(other)) > Math.Sin(1 * Math.PI / 180))
        {
            HintChanged?.Invoke(this, "Those lines do not run the same way. Align brings a line onto one parallel to it.");
            return false;
        }

        var across = way.PerpendicularLeft();
        var by = across * (reference.Start - line.Start).Dot(across);
        if (by.Length <= RoofSketch.JoinTolerance)
        {
            HintChanged?.Invoke(this, "They are in line already.");
            return false;
        }

        Remember();
        var (start, end) = (line.Start, line.End);
        MoveSketchCorner(start, start + by);
        MoveSketchCorner(end, end + by);
        _trimFirst = null;
        AfterSketchEdit($"Aligned: moved {Units.FormatLength(by.Length)}. Click the next pair.");
        return true;
    }

    // ---- fillet arc -----------------------------------------------------------------------

    /// <summary>
    /// Fillet Arc: click two straight lines, each on the part to keep, and the corner between them
    /// is rounded off at the radius on the options bar - both cut back to where the arc meets
    /// them, square to it, and an arc put in between.
    /// </summary>
    private bool SketchFilletAt(Point2D raw)
    {
        if (_sketch is null) return false;
        if (SketchLineAt(raw) is not { IsArc: false } line)
        {
            HintChanged?.Invoke(this, "Click a straight sketch line - on the part of it to keep.");
            return false;
        }

        if (_trimFirst is not var (first, firstAt) || ReferenceEquals(first, line))
        {
            _trimFirst = (line, raw);
            HintChanged?.Invoke(this, "Now click the line to round it off to - on the part of it to keep.");
            InvalidateVisual();
            return true;
        }

        if (Crossing(first.Start, first.End, line.Start, line.End) is not { } corner)
        {
            HintChanged?.Invoke(this, "Those lines run parallel and never meet: there is no corner to round off.");
            return false;
        }

        // Away from the corner along each line, toward the part that is kept.
        Vector2D Away(RoofSketchLine of, Point2D kept)
        {
            var way = (of.End - of.Start).NormalisedOrDefault(Vector2D.UnitX);
            return (kept - corner).Dot(way) >= 0 ? way : way * -1;
        }

        var (a, b) = (Away(first, firstAt), Away(line, raw));
        var angle = Math.Acos(Math.Clamp(a.Dot(b), -1, 1));
        if (angle < 1e-3 || angle > Math.PI - 1e-3)
        {
            HintChanged?.Invoke(this, "Those lines run straight on from each other: there is no corner to round off.");
            return false;
        }

        var radius = Math.Max(1, SketchFilletRadius);
        var back = radius / Math.Tan(angle / 2);
        double Reach(RoofSketchLine of, Vector2D away) => Math.Max((of.Start - corner).Dot(away), (of.End - corner).Dot(away));
        if (back > Reach(first, a) + 0.5 || back > Reach(line, b) + 0.5)
        {
            HintChanged?.Invoke(this, $"A {Units.FormatLength(radius)} radius needs {Units.FormatLength(back)} of each line from the corner. Make the radius smaller.");
            return false;
        }

        Remember();
        var (t1, t2) = (corner + a * back, corner + b * back);
        KeepUpTo(first, firstAt, t1);
        KeepUpTo(line, raw, t2);

        // The arc from one to the other, bowed away from the corner - its centre on the bisector.
        var centre = corner + (a + b).NormalisedOrDefault(a) * (radius / Math.Sin(angle / 2));
        var turn = Math.PI - angle;
        var anticlockwise = (t1 - centre).Cross(t2 - centre) > 0;
        var edge = first.Edge.Copy();
        edge.WallId = null;
        _sketch.Add(new RoofSketchLine(t1, t2, edge, (anticlockwise ? 1 : -1) * Math.Tan(turn / 4)));
        _trimFirst = null;

        AfterSketchEdit($"Rounded off at {Units.FormatLength(radius)}. Click the next pair.");
        return true;
    }

    /// <summary>What the editing tools say to do.</summary>
    private string? SketchEditingHint() => _sketchTool switch
    {
        RoofSketchTool.Move => "Move: select lines first, then click where from and where to. Tick Copy to leave them and add copies.",
        RoofSketchTool.Mirror => "Mirror: select lines first, then click two points on the line to mirror about. Untick Copy to move them instead.",
        RoofSketchTool.Align => "Align: click the line to align to, then a line running the same way to bring onto it.",
        RoofSketchTool.Fillet => "Fillet Arc: type the radius on the options bar, then click two lines, each on the part to keep.",
        RoofSketchTool.Rotate => "Rotate: select lines first, then click the centre, then where the angle starts and ends - or type an Angle and click the centre. Tick Copy to turn copies.",
        RoofSketchTool.Array => SketchArrayRadial
            ? "Array, radial: select lines first, type the Count and the Angle round, then click the centre."
            : "Array: select lines first, type the Count, then click where from and where the next one goes. Tick Radial to repeat them round a centre.",
        RoofSketchTool.Scale => "Scale: select lines first, type the Scale, then click the point they are scaled about.",
        _ => null
    };
}
