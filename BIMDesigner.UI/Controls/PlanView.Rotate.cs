using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Rotate, Scale and Pin. Rotate turns the selection about a centre - the middle of the
/// selection, or a point placed - through an angle picked with two clicks or typed; Scale
/// grows or shrinks its setting out about a point clicked; Pin holds elements where they are.
/// </summary>
public partial class PlanView
{
    /// <summary>Whether Rotate turns copies and leaves the selection where it is.</summary>
    public bool RotateCopies { get; set; }

    /// <summary>Whether the next click with Rotate places its centre, rather than picking the angle.</summary>
    public bool PlacingRotateCentre
    {
        get => _placingRotateCentre;
        set
        {
            _placingRotateCentre = value;
            if (value) _rotateFrom = null;
            HintChanged?.Invoke(this, value ? "Click where the centre of rotation goes." : RotateHint());
            InvalidateVisual();
        }
    }

    /// <summary>Which line of a wall the Dimension tool measures to when a wall is clicked.</summary>
    public DimensionPreference DimensionPrefer { get; set; } = DimensionPreference.WallCentrelines;

    /// <summary>How much Scale grows what it scales: 2 doubles it, 0.5 halves it.</summary>
    public double ScaleFactor { get; set; } = 2;

    private bool _placingRotateCentre;
    private Point2D? _rotateCentre;
    private Point2D? _rotateFrom;

    /// <summary>Raised when a pinned element stops something being done, with why.</summary>
    public event EventHandler<string>? PinnedRefused;

    /// <summary>
    /// Raised when Rotate or Scale has done its turn or scale: the tool is finished with, and
    /// the window goes back to Modify with what it moved still selected, as Revit does.
    /// </summary>
    public event EventHandler? ToolFinished;

    /// <summary>Where Rotate turns about: the point placed, or the middle of what is selected.</summary>
    public Point2D? RotateCentre => _rotateCentre ?? SelectionCentre();

    /// <summary>The middle of the box round what is selected.</summary>
    private Point2D? SelectionCentre()
    {
        var points = _selection.Where(ElementTransforms.CanMove).SelectMany(Ghost).SelectMany(line => line).ToList();
        if (points.Count == 0) return null;

        return new Point2D((points.Min(p => p.X) + points.Max(p => p.X)) / 2, (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
    }

    private void ResetRotate()
    {
        _rotateCentre = null;
        _rotateFrom = null;
        _placingRotateCentre = false;
    }

    private string RotateHint() => _rotateFrom is null
        ? "Click where the angle starts from, or type an Angle and press Enter. Place Centre moves the centre."
        : "Click where the angle ends. Esc cancels.";

    /// <summary>A click with Rotate: the centre when placing it, then the start of the angle, then its end.</summary>
    private void PlaceRotatePoint(Point2D point, bool snapped)
    {
        if (Document is null) return;

        if (!_selection.Any(ElementTransforms.CanMove))
        {
            HintChanged?.Invoke(this, "Nothing to rotate. Select something with the Select tool first.");
            return;
        }

        if (_placingRotateCentre)
        {
            _rotateCentre = point;
            _placingRotateCentre = false;
            _rotateFrom = null;
            HintChanged?.Invoke(this, RotateHint());
            InvalidateVisual();
            return;
        }

        if (RotateCentre is not { } centre) return;

        if (_rotateFrom is null)
        {
            if (point.DistanceTo(centre) < SnapStepMm)
            {
                HintChanged?.Invoke(this, "Click further from the centre, so the angle has a line to start from.");
                return;
            }

            _rotateFrom = point;
            HintChanged?.Invoke(this, RotateHint());
            InvalidateVisual();
            return;
        }

        var angle = PickedAngle(centre, _rotateFrom.Value, point, snapped);
        _rotateFrom = null;
        if (angle is { } degrees) RotateBy(degrees);
        InvalidateVisual();
    }

    /// <summary>
    /// The angle between two clicks about the centre, anticlockwise. A point snapped to
    /// something is taken exactly; one clicked freely goes to the whole degree, and to the
    /// nearest 15° within 3° of it.
    /// </summary>
    private static double? PickedAngle(Point2D centre, Point2D from, Point2D to, bool exact)
    {
        if (to.DistanceTo(centre) < 1) return null;

        var (a, b) = (from - centre, to - centre);
        var degrees = Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y) * 180 / Math.PI;
        if (exact) return degrees;

        var fifteen = Math.Round(degrees / 15) * 15;
        return Math.Abs(degrees - fifteen) <= 3 ? fifteen : Math.Round(degrees);
    }

    /// <summary>
    /// Turns the selection - or copies of it - through an angle, anticlockwise in degrees, about
    /// the centre. Refused while any of it is pinned, unless it is copies being turned. Public
    /// so the options bar's Angle and tests can drive it.
    /// </summary>
    public bool RotateBy(double degrees)
    {
        if (Document is null || RotateCentre is not { } centre) return false;
        if (Math.Abs(degrees % 360) < 1e-9)
        {
            HintChanged?.Invoke(this, "A whole turn leaves it where it is.");
            return false;
        }

        var selection = _selection.ToList();
        if (!RotateCopies && RefusePinned(selection, "rotated")) return false;

        if (RotateCopies)
        {
            var copies = ElementCopy.Duplicate(Document, selection);
            foreach (var copy in copies.Where(ElementTransforms.CanMove)) ElementTransforms.Rotate(copy, centre, degrees);
            FitLoneChimneys(copies);
            Apply(Dormers.AddCopies(Document, copies, "Rotate Copy"));
            SelectMany(copies.Where(ElementTransforms.CanMove));
        }
        else
        {
            var command = new RotateElementsCommand(selection, centre, degrees, Document);
            if (command.IsEmpty) return false;
            Apply(command);
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        ToolFinished?.Invoke(this, EventArgs.Empty);
        HintChanged?.Invoke(this, $"Rotated {degrees.ToString("0.##", CultureInfo.CurrentCulture)}°{(RotateCopies ? " as copies" : "")}. Still selected: Q to rotate again.");
        return true;
    }

    /// <summary>A click with Scale: the point the selection is scaled about, by the factor on the options bar.</summary>
    private void PlaceScalePoint(Point2D point)
    {
        if (!_selection.Any(ElementTransforms.CanScale))
        {
            HintChanged?.Invoke(this, _selection.Count == 0
                ? "Nothing to scale. Select something with the Select tool first."
                : "That cannot be scaled: walls, floors, ceilings, roofs drawn by footprint, grids, sections, rooms and annotation can.");
            return;
        }

        ScaleAbout(point, ScaleFactor);
    }

    /// <summary>
    /// Scales the selection's setting out about a point - walls longer, floors larger - with
    /// thicknesses and heights kept. Public so tests can drive it.
    /// </summary>
    public bool ScaleAbout(Point2D origin, double factor)
    {
        if (Document is null) return false;
        if (factor <= 0 || !double.IsFinite(factor) || Math.Abs(factor - 1) < 1e-9)
        {
            HintChanged?.Invoke(this, "Give a scale other than 1: 2 doubles the size, 0.5 halves it.");
            return false;
        }

        var selection = _selection.ToList();
        if (RefusePinned(selection, "scaled")) return false;

        var command = new ScaleElementsCommand(selection, origin, factor, Document);
        if (command.IsEmpty) return false;

        Apply(command);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        ToolFinished?.Invoke(this, EventArgs.Empty);
        HintChanged?.Invoke(this, $"Scaled by {factor.ToString("0.###", CultureInfo.CurrentCulture)}. Still selected: K to scale again.");
        return true;
    }

    /// <summary>Pins the selection where it is, or unpins it.</summary>
    public bool PinSelected(bool pinned)
    {
        if (Document is null) return false;

        var command = new PinElementsCommand(_selection, pinned);
        if (command.IsEmpty)
        {
            HintChanged?.Invoke(this, _selection.Count == 0
                ? $"Select what to {(pinned ? "pin" : "unpin")} first."
                : pinned ? "Already pinned." : "Nothing selected is pinned.");
            return false;
        }

        Apply(command);
        HintChanged?.Invoke(this, pinned
            ? $"Pinned: {Plural(_selection.Count, "element")} cannot be moved, rotated, scaled or deleted until unpinned."
            : "Unpinned: free to move again.");
        InvalidateVisual();
        return true;
    }

    /// <summary>Whether any of these is pinned - and, if so, says it cannot be done and why.</summary>
    private bool RefusePinned(IEnumerable<Element> elements, string doing)
    {
        var pinned = elements.Where(element => element.Pinned).ToList();
        if (pinned.Count == 0) return false;

        var what = pinned.Count == 1 ? $"A pinned {Describe(pinned[0])} cannot be {doing}" : $"{pinned.Count} pinned elements cannot be {doing}";
        var why = $"{what}. Unpin it first: Modify → Unpin, or P.";
        HintChanged?.Invoke(this, why);
        PinnedRefused?.Invoke(this, why);
        return true;
    }

    private static string Describe(Element element) => element switch
    {
        Wall => "wall",
        Grid => "grid line",
        Roof => "roof",
        Floor => "floor",
        Column => "column",
        Chimney => "chimney",
        _ => element.Category.ToString().ToLowerInvariant().TrimEnd('s')
    };

    /// <summary>The lines an element is drawn by in a preview: a wall's line, a floor's outline, a cross where something stands.</summary>
    private IEnumerable<IReadOnlyList<Point2D>> Ghost(Element element)
    {
        IReadOnlyList<Point2D> Cross(Point2D at) => new[] { at + new Vector2D(-200, 0), at + new Vector2D(200, 0), at, at + new Vector2D(0, -200), at + new Vector2D(0, 200) };

        switch (element)
        {
            case Wall wall:
            {
                var curve = wall.LocationCurve;
                var steps = wall.IsCurved ? 24 : 1;
                yield return Enumerable.Range(0, steps + 1).Select(i => curve.PointAt(curve.Length * i / steps)).ToList();
                break;
            }

            case Slab slab when slab.Boundary.Count >= 3:
                yield return slab.Boundary.Append(slab.Boundary[0]).ToList();
                break;

            case Grid grid:
                yield return new[] { grid.Start, grid.End };
                break;

            case SectionMarker section:
                yield return new[] { section.Start, section.End };
                break;

            case Chimney chimney when Document is not null:
            {
                var outline = Chimneys.Footprint(Document, chimney, chimney.LevelId);
                yield return outline.Append(outline[0]).ToList();
                break;
            }

            case Room room: yield return Cross(room.Location); break;
            case Column column: yield return Cross(column.Location); break;
            case Component component: yield return Cross(component.Location); break;
            case ShaftOpening shaft: yield return Cross(shaft.Location); break;
            case RoofWindow roofWindow: yield return Cross(roofWindow.Location); break;
            case RoofDrain drain: yield return Cross(drain.Location); break;
            case Downpipe pipe: yield return Cross(pipe.Location); break;
            case Tag tag: yield return Cross(tag.Position); break;
            case TextNote note: yield return Cross(note.Position); break;
            case Dimension dimension: yield return new[] { dimension.Start.FallbackPoint, dimension.End.FallbackPoint }; break;
        }
    }

    /// <summary>Rotate's centre, the angle being picked, and the selection turned through it.</summary>
    private void DrawRotatePreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Rotate || Document is null || !_selection.Any(ElementTransforms.CanMove)) return;
        if (_placingRotateCentre)
        {
            DrawCentreMarker(dc, _cursorModel);
            return;
        }

        if (RotateCentre is not { } centre) return;
        DrawCentreMarker(dc, centre);

        if (_rotateFrom is not { } from)
        {
            dc.DrawLine(SketchPreviewPen, ModelToScreen(centre), ModelToScreen(_cursorModel));
            return;
        }

        dc.DrawLine(SketchPreviewPen, ModelToScreen(centre), ModelToScreen(from));
        dc.DrawLine(SketchPreviewPen, ModelToScreen(centre), ModelToScreen(_cursorModel));
        if (PickedAngle(centre, from, _cursorModel, _cursorIsSnapped) is not { } degrees) return;

        // The selection turned through it, as it will be: walls at their thickness, the doors
        // and windows in them, floors and footprints.
        var radians = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(radians), Math.Sin(radians));
        Point2D Turn(Point2D p) => new(centre.X + (p.X - centre.X) * cos - (p.Y - centre.Y) * sin, centre.Y + (p.X - centre.X) * sin + (p.Y - centre.Y) * cos);
        foreach (var line in PreviewOutlines())
            DrawModelPolyline(dc, RotatePreviewPen, line.Select(Turn).ToList());

        // The angle swept, as an arc between the two lines.
        var radius = Math.Min(from.DistanceTo(centre), _cursorModel.DistanceTo(centre)) * 0.6;
        var start = Math.Atan2(from.Y - centre.Y, from.X - centre.X);
        var arc = Enumerable.Range(0, 33).Select(i => start + radians * i / 32)
            .Select(a => new Point2D(centre.X + radius * Math.Cos(a), centre.Y + radius * Math.Sin(a))).ToList();
        DrawModelPolyline(dc, SketchPreviewPen, arc);

        DrawPreviewLabel(dc, $"{degrees.ToString("0.##", CultureInfo.CurrentCulture)}°");
    }

    /// <summary>Scale's point under the cursor, and the selection scaled about it, as it will be.</summary>
    private void DrawScalePreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Scale || Document is null || !IsMouseOver) return;

        var scalable = _selection.Where(ElementTransforms.CanScale).ToList();
        if (scalable.Count == 0) return;

        var origin = _cursorModel;
        var factor = ScaleFactor;
        DrawCentreMarker(dc, origin);
        if (factor <= 0 || !double.IsFinite(factor)) return;

        Point2D Scale(Point2D p) => new(origin.X + (p.X - origin.X) * factor, origin.Y + (p.Y - origin.Y) * factor);
        foreach (var element in scalable)
        {
            // A wall grows longer, not thicker: its faces either side of its line, scaled.
            if (element is Wall { IsCurved: false } wall && Document.GetWallType(wall) is { } type)
            {
                var (from, to) = wall.GetBodyCentreline(type.Structure);
                var (a, b) = (Scale(from), Scale(to));
                var across = (b - a).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (type.Width / 2);
                DrawModelPolyline(dc, RotatePreviewPen, new[] { a + across, b + across, b - across, a - across, a + across });
                continue;
            }

            foreach (var line in Ghost(element))
                DrawModelPolyline(dc, RotatePreviewPen, line.Select(Scale).ToList());
        }

        DrawPreviewLabel(dc, $"× {factor.ToString("0.###", CultureInfo.CurrentCulture)}");
    }

    private static readonly Pen RotatePreviewPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x8F, 0xE0))), 1.4));

    private void DrawPreviewLabel(DrawingContext dc, string label)
    {
        var text = new FormattedText(label, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, SketchPreviewPen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var at = ModelToScreen(_cursorModel);
        dc.DrawText(text, new Point(at.X + 12, at.Y - 18));
    }

    /// <summary>
    /// The outlines of what Rotate moves, as the plan draws them: a wall's faces, the doors and
    /// windows in it, a floor's edge, a component's or column's footprint - or its line or a
    /// cross, where that is all it has.
    /// </summary>
    private IEnumerable<IReadOnlyList<Point2D>> PreviewOutlines()
    {
        if (Document is null) yield break;

        var moving = _selection.Where(ElementTransforms.CanMove).ToList();
        var walls = moving.OfType<Wall>().Select(wall => wall.Id).ToHashSet();
        var hosted = Document.Elements.OfType<Opening>().Where(opening => walls.Contains(opening.HostWallId) && !moving.Contains(opening));

        foreach (var element in moving.Concat(hosted))
        {
            IReadOnlyList<Point2D>? ring = element switch
            {
                Wall wall when Document.GetWallType(wall) is { } type => WallJoins.GetBandOutline(Document, wall, type, type.Width / 2, -type.Width / 2),
                Opening opening => OpeningRing(opening),
                PolygonWall shaped => shaped.Outline,
                Component component when Document.FindType<ComponentType>(component.TypeId) is { } type =>
                    ComponentModel.Footprint(type, ComponentModel.FrameOf(component, 0)),
                Column column when Document.FindType<ColumnType>(column.TypeId) is { } type => ColumnRing(column, type),
                _ => null
            };

            if (ring is { Count: > 2 })
            {
                yield return ring.Append(ring[0]).ToList();
                continue;
            }

            foreach (var line in Ghost(element)) yield return line;
        }
    }

    /// <summary>A door's or window's opening through its wall, in plan.</summary>
    private IReadOnlyList<Point2D>? OpeningRing(Opening opening)
    {
        if (Document is null) return null;
        if (Document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } host || Document.GetWallType(host) is not { } hostType) return null;
        if (Document.FindType<OpeningType>(opening.TypeId) is not { } type) return null;

        var (from, to) = opening.GetSpan(type);
        var half = hostType.Width / 2;
        return new[]
        {
            host.PointAt(hostType.Structure, from, half), host.PointAt(hostType.Structure, to, half),
            host.PointAt(hostType.Structure, to, -half), host.PointAt(hostType.Structure, from, -half)
        };
    }

    private static IReadOnlyList<Point2D> ColumnRing(Column column, ColumnType type)
    {
        var radians = column.Rotation * Math.PI / 180;
        var (along, across) = (new Vector2D(Math.Cos(radians), Math.Sin(radians)), new Vector2D(-Math.Sin(radians), Math.Cos(radians)));
        var (w, d) = (type.Width / 2, type.Depth / 2);
        return new[]
        {
            column.Location - along * w - across * d, column.Location + along * w - across * d,
            column.Location + along * w + across * d, column.Location - along * w + across * d
        };
    }

    private void DrawCentreMarker(DrawingContext dc, Point2D at)
    {
        var centre = ModelToScreen(at);
        dc.DrawEllipse(null, SketchPreviewPen, centre, 6, 6);
        dc.DrawLine(SketchPreviewPen, new Point(centre.X - 10, centre.Y), new Point(centre.X + 10, centre.Y));
        dc.DrawLine(SketchPreviewPen, new Point(centre.X, centre.Y - 10), new Point(centre.X, centre.Y + 10));
    }

    private static readonly Brush PinBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD9, 0x3A, 0x2E)));
    private static readonly Pen PinPen = Frozen(new Pen(PinBrush, 1.6));

    /// <summary>A pin on each selected element that is pinned.</summary>
    private void DrawPins(DrawingContext dc)
    {
        foreach (var element in _selection.Where(element => element.Pinned))
        {
            if (Ghost(element).SelectMany(line => line).ToList() is not { Count: > 0 } points) continue;

            var at = ModelToScreen(new Point2D(points.Average(p => p.X), points.Average(p => p.Y)));
            var head = new Point(at.X + 6, at.Y - 14);
            dc.DrawLine(PinPen, at, head);
            dc.DrawEllipse(PinBrush, null, head, 5, 5);
        }
    }
}
