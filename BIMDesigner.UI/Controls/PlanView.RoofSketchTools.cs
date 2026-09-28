using System.Windows.Input;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The roof sketch's other draw and modify tools (Revit's Start-End-Radius Arc, Circle, Pick
/// Lines, Offset and Trim/Extend to Corner): arcs and circles for round roofs, lines taken off
/// what is already drawn, and the two edits that put lines where they belong once drawn.
/// </summary>
public partial class PlanView
{
    /// <summary>An arc's second click - its other end - while its bow is still being chosen.</summary>
    private Point2D? _sketchArcEnd;

    /// <summary>The first line clicked for Trim/Extend, and where on it: the part clicked is the part kept.</summary>
    private (RoofSketchLine Line, Point2D At)? _trimFirst;

    /// <summary>How far Pick Lines and Offset set a line off - Revit's Offset on the options bar.</summary>
    public double SketchOffset { get; set; }

    /// <summary>Whether Offset makes a copy of the line rather than moving it.</summary>
    public bool SketchOffsetCopy { get; set; }

    // ---- arcs and circles ---------------------------------------------------------------

    /// <summary>
    /// Draws an arc: its start, its end, then a third click the arc passes through - Revit's
    /// Start-End-Radius Arc. A sloping arc makes a rounded roof, a cone if it goes all round.
    /// </summary>
    private bool DrawArcTo(Point2D point)
    {
        if (_sketch is null) return false;

        if (_sketchStart is not { } from)
        {
            _sketchStart = point;
            HintChanged?.Invoke(this, "Click the other end of the arc.");
            InvalidateVisual();
            return true;
        }

        if (_sketchArcEnd is not { } to)
        {
            if (from.DistanceTo(point) <= RoofSketch.JoinTolerance) return false;

            _sketchArcEnd = point;
            HintChanged?.Invoke(this, "Click a point the arc passes through - how far it bows.");
            InvalidateVisual();
            return true;
        }

        var bulge = ArcBulge(from, to, point);
        if (Math.Abs(bulge) < WallCurve.StraightBulge)
        {
            HintChanged?.Invoke(this, "That is on the straight line between the ends. Click off it to bow the arc.");
            return false;
        }

        Remember();
        _sketch.Add(new RoofSketchLine(from, to, NewDrawnEdge(), bulge));
        _sketchStart = null;
        _sketchArcEnd = null;

        AfterSketchEdit(RoofSketch.Check(_sketch).IsValid
            ? "The outline closes. Finish ✓ to make the roof - a sloping arc rises as a cone."
            : "Arc added.");
        return true;
    }

    /// <summary>The bulge of the arc between two ends through a point - kept short of a whole circle.</summary>
    private static double ArcBulge(Point2D from, Point2D to, Point2D through) =>
        Math.Clamp(WallCurve.BulgeThrough(from, to, through), -MaxBulge, MaxBulge);

    // ---- pick lines ---------------------------------------------------------------------

    /// <summary>
    /// The line Pick Lines would take here: the nearest edge of something already drawn - a
    /// floor, ceiling or other roof, a wall's face, a grid line - set off by the offset toward
    /// the cursor. Null when nothing is near.
    /// </summary>
    private (Point2D Start, Point2D End)? PickLineAt(Point2D raw)
    {
        if (Document is null) return null;

        var reach = 10 / PixelsPerMm;
        (Point2D Start, Point2D End)? best = null;

        void Consider(Point2D a, Point2D b)
        {
            if (a.DistanceTo(b) <= RoofSketch.JoinTolerance) return;

            var distance = Line2D.DistanceFromSegment(raw, a, b);
            if (distance >= reach) return;

            reach = distance;
            best = (a, b);
        }

        foreach (var slab in OnActiveLevel<Slab>())
        {
            if (ReferenceEquals(slab, _sketchRoof)) continue;

            for (var i = 0; i < slab.Boundary.Count; i++)
                Consider(slab.Boundary[i], slab.Boundary[(i + 1) % slab.Boundary.Count]);
        }

        foreach (var wall in OnActiveLevel<Wall>())
        {
            if (wall.IsCurved || Document.GetWallType(wall) is not { } type) continue;

            var half = type.Structure.TotalWidth / 2;
            foreach (var face in new[] { half, -half })
                Consider(wall.PointAt(type.Structure, 0, face), wall.PointAt(type.Structure, wall.Length, face));
        }

        foreach (var grid in OnActiveLevel<Grid>())
            Consider(grid.Start, grid.End);

        if (best is not var (start, end)) return null;
        if (Math.Abs(SketchOffset) < 1e-9) return best;

        // Set off toward the side the cursor is on.
        var across = (end - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var side = (raw - start).Dot(across) >= 0 ? 1 : -1;
        var shift = across * (side * SketchOffset);
        return (start + shift, end + shift);
    }

    private bool PickLinesAt(Point2D raw)
    {
        if (_sketch is null) return false;

        if (PickLineAt(raw) is not var (start, end))
        {
            HintChanged?.Invoke(this, "No line there. Hover the edge of a floor or roof, a wall's face or a grid line.");
            return false;
        }

        Remember();
        var line = new RoofSketchLine(start, end, NewDrawnEdge());
        _sketch.Add(line);
        RoofSketch.CloseCorners(_sketch, line, CornerReach());

        AfterSketchEdit(RoofSketch.Check(_sketch).IsValid
            ? "The outline closes. Finish ✓ to make the roof."
            : "Line picked. Pick the next one; corners close by themselves.");
        return true;
    }

    // ---- offset -------------------------------------------------------------------------

    /// <summary>
    /// A sketch line set off by the offset toward the cursor - straight lines sideways, arcs
    /// round the same centre. Null when there is no line under the cursor or no offset.
    /// </summary>
    private (RoofSketchLine Line, RoofSketchLine Moved)? SketchOffsetAt(Point2D raw)
    {
        if (SketchLineAt(raw, 20) is not { } line || Math.Abs(SketchOffset) < 1e-9) return null;

        var (_, left) = line.Curve.Locate(raw);
        var by = left >= 0 ? SketchOffset : -SketchOffset;
        var moved = line.Curve.Offset(by);

        if (moved.Length <= RoofSketch.JoinTolerance) return null;
        return (line, new RoofSketchLine(moved.Start, moved.End, line.Edge.Copy(), line.Bulge));
    }

    /// <summary>
    /// Offset: moves the line clicked sideways by the offset, toward the cursor - or copies it
    /// there. A moved line's neighbours are carried to meet it at its new corners, so the
    /// outline stays closed.
    /// </summary>
    private bool OffsetLineAt(Point2D raw)
    {
        if (_sketch is null) return false;

        if (Math.Abs(SketchOffset) < 1e-9)
        {
            HintChanged?.Invoke(this, "Type the offset on the options bar first - how far to move the line.");
            return false;
        }

        if (SketchOffsetAt(raw) is not var (line, moved))
        {
            HintChanged?.Invoke(this, "Click a sketch line, on the side it should move toward.");
            return false;
        }

        Remember();

        if (SketchOffsetCopy)
        {
            _sketch.Add(moved);
            AfterSketchEdit($"Copied {Units.FormatLength(SketchOffset)} across.");
            return true;
        }

        var (oldStart, oldEnd) = (line.Start, line.End);
        line.Start = moved.Start;
        line.End = moved.End;

        foreach (var (old, atStart) in new[] { (oldStart, true), (oldEnd, false) })
        {
            foreach (var other in _sketch.Where(other => !ReferenceEquals(other, line)).ToList())
            {
                var otherAtStart = other.Start.DistanceTo(old) <= RoofSketch.JoinTolerance;
                if (!otherAtStart && other.End.DistanceTo(old) > RoofSketch.JoinTolerance) continue;

                // A straight neighbour runs on, or is cut back, to meet the moved line where the
                // two cross; an arc neighbour, or one running parallel, just follows the corner.
                var corner = atStart ? line.Start : line.End;
                if (!other.IsArc && !line.IsArc &&
                    Crossing(other.Start, other.End, line.Start, line.End) is { } crossing)
                {
                    corner = crossing;
                    if (atStart) line.Start = crossing;
                    else line.End = crossing;
                }

                if (otherAtStart) other.Start = corner;
                else other.End = corner;
            }
        }

        AfterSketchEdit($"Moved {Units.FormatLength(SketchOffset)} across; the lines either side followed it.");
        return true;
    }

    // ---- trim / extend ------------------------------------------------------------------

    /// <summary>
    /// Trim/Extend to Corner: click two lines, and each is cut back or run on to where they
    /// cross, keeping the part of each that was clicked - how two lines that do not quite meet,
    /// or run past each other, are made a corner.
    /// </summary>
    private bool SketchTrimAt(Point2D raw)
    {
        if (_sketch is null) return false;

        if (SketchLineAt(raw) is not { } line)
        {
            HintChanged?.Invoke(this, "Click a sketch line - on the part of it to keep.");
            return false;
        }

        if (line.IsArc)
        {
            HintChanged?.Invoke(this, "Trim/Extend works on straight lines. Move an arc's ends with Offset, or redraw it.");
            return false;
        }

        if (_trimFirst is not var (first, firstAt) || ReferenceEquals(first, line))
        {
            _trimFirst = (line, raw);
            HintChanged?.Invoke(this, "Now click the line it should meet - on the part of it to keep.");
            InvalidateVisual();
            return true;
        }

        if (Crossing(first.Start, first.End, line.Start, line.End) is not { } corner)
        {
            HintChanged?.Invoke(this, "Those lines run parallel and never meet. Pick one that crosses the first.");
            return false;
        }

        Remember();
        KeepUpTo(first, firstAt, corner);
        KeepUpTo(line, raw, corner);
        _trimFirst = null;

        AfterSketchEdit(RoofSketch.Check(_sketch).IsValid
            ? "Cornered. The outline closes: Finish ✓ to make the roof."
            : "Cornered. Click the next pair.");
        return true;
    }

    /// <summary>Ends a line at a corner, keeping the side of the corner that was clicked.</summary>
    private static void KeepUpTo(RoofSketchLine line, Point2D clicked, Point2D corner)
    {
        var along = line.End - line.Start;
        var length = along.Length;
        if (length <= 0) return;

        var at = (corner - line.Start).Dot(along) / length;
        var kept = (clicked - line.Start).Dot(along) / length;

        if (kept < at) line.End = corner;
        else line.Start = corner;
    }

    // ---- align eaves --------------------------------------------------------------------

    /// <summary>The eave the others are being brought to, once Align Eaves has been given one.</summary>
    private RoofSketchLine? _alignReference;

    /// <summary>
    /// Whether Align Eaves moves a picked eave's overhang to bring it to height - Revit's
    /// Adjust Overhang - rather than its plate - Adjust Height. Drawn lines have no overhang,
    /// so they are always brought by their height.
    /// </summary>
    public bool SketchAlignByOverhang { get; set; }

    /// <summary>How far in from a sketch line the roof would bear: for the roof being edited, as it says; for a new one, a truss.</summary>
    private double SketchBearingInset(RoofSketchLine line) =>
        _sketchRoof is { } roof && Document is not null ? roof.BearingInset(Document, line.Edge) : line.Edge.BearingInset;

    /// <summary>How high above the roof's base a sketch line's eave is.</summary>
    private double SketchEaveHeight(RoofSketchLine line) => Roof.EaveHeight(line.Edge, SketchBearingInset(line));

    /// <summary>
    /// Align Eaves: the first sloping line clicked is the one to match; every one clicked after
    /// it is brought to the same eave height - by raising or lowering its plate, or, for a line
    /// on a wall with Adjust Overhang, by running its overhang out or in until its edge lands
    /// there at its own pitch.
    /// </summary>
    private bool AlignEaveAt(Point2D raw)
    {
        if (_sketch is null || Document is null) return false;

        if (SketchLineAt(raw) is not { } line || !line.Edge.DefinesSlope)
        {
            HintChanged?.Invoke(this, "Click a sloping line - an eave. Gable ends have no eave to align.");
            return false;
        }

        if (_alignReference is null || !_sketch.Contains(_alignReference))
        {
            _alignReference = line;
            HintChanged?.Invoke(this,
                $"Eaves will be brought to {Units.FormatLength(SketchEaveHeight(line))} above the base. Click the eaves to align; Esc to pick another.");
            InvalidateVisual();
            return true;
        }

        if (ReferenceEquals(line, _alignReference)) return false;

        var target = SketchEaveHeight(_alignReference);
        var rise = Math.Tan(Math.Clamp(line.Edge.SlopeDegrees, 0.05, 89.95) * Math.PI / 180);
        var inset = SketchBearingInset(line);

        if (SketchAlignByOverhang && line.Edge.WallId is { } wallId &&
            Document.Walls.FirstOrDefault(wall => wall.Id == wallId) is { } wall)
        {
            // The plate stays; the edge runs out until, falling at its pitch, it reaches the target.
            var reach = (line.Edge.PlateOffset - target) / rise;
            var overhang = line.Edge.Overhang + (reach - inset);
            if (overhang < -1e-6)
            {
                HintChanged?.Invoke(this, "That eave is already lower than the one it should match; it would need an overhang back inside its wall. Use Adjust Height instead.");
                return false;
            }

            Remember();
            line.Edge.Overhang = Math.Max(0, overhang);

            if (RoofSketch.LineOnWall(Document, wall, line.Edge.OnLeftOfWall, line.Edge.Overhang, line.Edge.ExtendToCore) is var (a, b))
            {
                line.Start = OntoLine(line.Start, a, b);
                line.End = OntoLine(line.End, a, b);
                RoofSketch.CloseCorners(_sketch, line, CornerReach());
            }

            AfterSketchEdit($"Overhang now {Units.FormatLength(line.Edge.Overhang)}: its eave is at {Units.FormatLength(target)}, with the one it was matched to.");
            return true;
        }

        Remember();
        line.Edge.PlateOffset = target + rise * inset;
        AfterSketchEdit($"Plate raised to {Units.FormatLength(line.Edge.PlateOffset)}: its eave is at {Units.FormatLength(target)}, with the one it was matched to.");
        return true;
    }

    /// <summary>While Align Eaves is on, each eave's height beside it - the one being matched picked out.</summary>
    private void DrawEaveHeights(System.Windows.Media.DrawingContext dc)
    {
        if (_sketch is null || _sketchTool != RoofSketchTool.AlignEaves) return;

        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var line in _sketch.Where(line => line.Edge.DefinesSlope))
        {
            var text = new System.Windows.Media.FormattedText(
                $"eave {Units.FormatLength(SketchEaveHeight(line))}",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"),
                11,
                ReferenceEquals(line, _alignReference) ? SketchSelectedPen.Brush : SketchSlopeBrush,
                dpi);

            var at = MarkerPoint(line);
            dc.DrawText(text, new System.Windows.Point(at.X - text.Width / 2, at.Y + 9));
        }
    }

    // ---- typing a pitch -----------------------------------------------------------------

    private System.Windows.Controls.Primitives.Popup? _pitchEditor;
    private RoofSketchLine? _pitchLine;

    /// <summary>The pitch shown beside a sloping line, as a rectangle on screen - clicked, it is typed into.</summary>
    private System.Windows.Rect PitchLabelBounds(RoofSketchLine line)
    {
        var text = new System.Windows.Media.FormattedText(
            $"{line.Edge.SlopeDegrees:0.##}°",
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface("Segoe UI"),
            11,
            SketchSlopeBrush,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Further in from the marker, on the roof's side of it, clear of the line whichever way it runs.
        var (at, inward) = MarkerPlace(line);
        var clear = 12 + Math.Abs(inward.X) * text.Width / 2 + Math.Abs(inward.Y) * text.Height / 2;
        var centre = at + inward * clear;
        return new System.Windows.Rect(centre.X - text.Width / 2, centre.Y - text.Height / 2, text.Width, text.Height);
    }

    /// <summary>The sloping line whose pitch label is under the cursor.</summary>
    private RoofSketchLine? PitchLabelAt(Point2D raw)
    {
        if (_sketch is null) return null;

        var screen = ModelToScreen(raw);
        return _sketch.FirstOrDefault(line =>
            line.Edge.DefinesSlope &&
            line.Length * PixelsPerMm > 3 * MarkerRadius &&
            PitchLabelBounds(line) is var bounds &&
            new System.Windows.Rect(bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6).Contains(screen));
    }

    /// <summary>
    /// Opens a box over a line's pitch to type a new one in - Revit's slope beside the line,
    /// clicked and typed into. Enter keeps it; Esc leaves it as it was.
    /// </summary>
    public bool BeginPitchEdit(RoofSketchLine line)
    {
        if (_sketch is null || !_sketch.Contains(line) || !line.Edge.DefinesSlope) return false;

        ClosePitchEditor();

        var bounds = PitchLabelBounds(line);
        var box = new System.Windows.Controls.TextBox
        {
            Text = $"{line.Edge.SlopeDegrees:0.##}°",
            MinWidth = 64,
            Padding = new System.Windows.Thickness(2, 0, 2, 0)
        };

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitPitchEdit(box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClosePitchEditor();
                e.Handled = true;
            }
        };

        box.LostKeyboardFocus += (_, _) =>
        {
            if (_pitchEditor is not null) CommitPitchEdit(box.Text);
        };

        _pitchLine = line;
        _pitchEditor = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
            HorizontalOffset = bounds.X - 4,
            VerticalOffset = bounds.Y - 3,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = box
        };

        _pitchEditor.Closed += (_, _) =>
        {
            _pitchEditor = null;
            _pitchLine = null;
            Focus();
        };

        _pitchEditor.IsOpen = true;
        box.Focus();
        box.SelectAll();

        HintChanged?.Invoke(this, "Type the pitch - 35, or 35° - and press Enter. Esc leaves it as it was.");
        return true;
    }

    /// <summary>Takes a typed pitch for the line being edited. Public so it can be driven from tests.</summary>
    public bool CommitPitchEdit(string text)
    {
        if (_pitchLine is not { } line || _sketch is null) return false;

        var typed = text.Trim().TrimEnd('°').Trim();
        if (!double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var degrees) &&
            !double.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out degrees))
        {
            HintChanged?.Invoke(this, "Type the pitch in degrees, such as 35.");
            ClosePitchEditor();
            return false;
        }

        if (degrees <= 0 || degrees >= 90)
        {
            HintChanged?.Invoke(this, "A pitch is more than 0° and less than 90°. Turn the slope off with its △ for a gable end.");
            ClosePitchEditor();
            return false;
        }

        ClosePitchEditor();
        if (Math.Abs(degrees - line.Edge.SlopeDegrees) < 1e-9) return true;

        Remember();
        line.Edge.SlopeDegrees = degrees;
        AfterSketchEdit($"The roof now rises at {degrees:0.##}° from this line.");
        return true;
    }

    private void ClosePitchEditor()
    {
        var editor = _pitchEditor;
        _pitchEditor = null;
        if (editor is not null) editor.IsOpen = false;
    }

    /// <summary>Where two lines, taken as running on for ever, cross; null when they are parallel.</summary>
    private static Point2D? Crossing(Point2D a0, Point2D a1, Point2D b0, Point2D b1)
    {
        var a = a1 - a0;
        var b = b1 - b0;
        var denominator = a.Cross(b);
        if (Math.Abs(denominator) <= a.Length * b.Length * 1e-9) return null;

        return a0 + a * ((b0 - a0).Cross(b) / denominator);
    }
}
