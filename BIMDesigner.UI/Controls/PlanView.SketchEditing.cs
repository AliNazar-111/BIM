using System.Windows.Input;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Changing a sketch's lines the ways Revit's Modify panel does while sketching: a corner dragged
/// to a new place, the lines meeting there following it; the selected lines moved or mirrored -
/// or copied so; a line aligned onto another; two lines rounded off into a fillet arc. A roof's
/// footprint, a floor's or a ceiling's boundary alike.
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
        }

        if (SketchMoveCopy)
        {
            _sketch.AddRange(lines);
            _sketchSelection.Clear();
            _sketchSelection.AddRange(lines);
        }

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
        }

        if (SketchMirrorCopy)
        {
            _sketch.AddRange(lines);
            _sketchSelection.Clear();
            _sketchSelection.AddRange(lines);
        }

        _sketchStart = null;
        AfterSketchEdit(SketchMirrorCopy ? "Mirrored copies made." : "Mirrored.");
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
        _ => null
    };
}
