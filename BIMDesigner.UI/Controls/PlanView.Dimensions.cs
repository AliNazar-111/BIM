using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The dimension tools beyond a single aligned dimension: a string through many walls and
/// gridlines, clicked one after another and double-clicked where its line goes; an angle between
/// two walls or gridlines; a radius, diameter or arc length on a curved wall. Each drawn in the
/// style picked on the options bar.
/// </summary>
public partial class PlanView
{
    /// <summary>The style new dimensions are drawn in; the project's first where none is picked.</summary>
    public Guid ActiveDimensionTypeId { get; set; }

    /// <summary>Whether the Aligned tool draws a string: clicks run on until a double-click places its line.</summary>
    public bool DimensionString { get; set; }

    /// <summary>What the Radial tool measures.</summary>
    public RadialKind ActiveRadialKind { get; set; } = RadialKind.Radius;

    private readonly List<DimensionReference> _dimensionString = new();
    private DimensionReference? _angularFirst;
    private DimensionReference? _angularSecond;
    private DimensionReference? _radialTarget;

    private Guid ActiveDimensionType() =>
        Document?.FindType<DimensionType>(ActiveDimensionTypeId)?.Id ?? (Document is null ? null : DimensionTypes.Starting(Document)?.Id) ?? Guid.Empty;

    /// <summary>A line to measure an angle or a radius to: a wall's line, by the options bar's preference, or a gridline - not a wall's end.</summary>
    private DimensionReference? LineReferenceAt(Point2D point)
    {
        if (Document is null) return null;
        var tolerance = Math.Max(WallJoins.JoinTolerance, 4 / PixelsPerMm);

        foreach (var wall in OnActiveLevel<Wall>())
        {
            if (Document.GetWallType(wall) is not { } type) continue;
            var (along, across) = wall.Locate(type.Structure, point);
            if (along >= 0 && along <= wall.Length && Math.Abs(across) <= type.Structure.TotalWidth / 2 + tolerance)
                return DimensionReference.ToWall(Document, wall, point, DimensionPrefer);
        }

        foreach (var grid in OnActiveLevel<Grid>())
            if (grid.Line.ClosestPointTo(point).DistanceTo(point) <= tolerance)
                return DimensionReference.ToGrid(grid, point);

        return null;
    }

    /// <summary>A click with the Aligned tool drawing a string: the next thing it measures to. Public so it can be driven from tests.</summary>
    public bool ClickDimensionString(Point2D raw)
    {
        if (Document is null) return false;
        var reference = ReferenceAt(SnapPoint(raw, null, out _));
        _dimensionString.Add(reference);
        HintChanged?.Invoke(this, _dimensionString.Count < 2
            ? "Click the next wall, face or gridline the string measures to."
            : $"{_dimensionString.Count} measured to. Click the next, or double-click where the dimension line goes. Esc cancels.");
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// The string clicked along made a dimension, its line through a point: its first and last
    /// references its ends, the rest between them. Public so it can be driven from tests.
    /// </summary>
    public bool FinishDimensionString(Point2D at)
    {
        if (Document is null || _dimensionString.Count < 2)
        {
            HintChanged?.Invoke(this, "A string measures to two things at least: click another.");
            return false;
        }

        var dimension = new Dimension { Start = _dimensionString[0], End = _dimensionString[^1], LevelId = ActiveLevelId, TypeId = ActiveDimensionType() };
        dimension.Between.AddRange(_dimensionString.Skip(1).Take(_dimensionString.Count - 2));
        _dimensionString.Clear();
        if (dimension.Measure(Document) < 1)
        {
            HintChanged?.Invoke(this, "Its ends are the same place. Start the string again.");
            InvalidateVisual();
            return false;
        }

        // The dimension line through the point placed: the offset, square to what is measured.
        var (from, to) = dimension.Ends(Document);
        var left = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var offset = (at - from).Dot(left);
        dimension.Offset = Math.Abs(offset) < 1 ? 600 : offset;

        Apply(new AddElementCommand(Document, dimension, "Dimension String"));
        Select(dimension);
        HintChanged?.Invoke(this, $"A string of {dimension.Segments(Document).Count}: {string.Join(" + ", dimension.Segments(Document).Select(DimensionTypes.Of(Document, dimension).Format))}.");
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click with the Angular tool: the first wall or gridline, the second, then where the arc
    /// goes - which of the angles between them it shows, and how far out. Public so it can be driven from tests.
    /// </summary>
    public bool ClickAngular(Point2D raw)
    {
        if (Document is null) return false;

        if (_angularFirst is null || _angularSecond is null)
        {
            if (LineReferenceAt(raw) is not { } line)
            {
                HintChanged?.Invoke(this, "Click a wall or a gridline: an angle is measured between two lines.");
                return false;
            }

            if (_angularFirst is null)
            {
                _angularFirst = line;
                HintChanged?.Invoke(this, "Now click the other wall or gridline.");
            }
            else
            {
                _angularSecond = line;
                if (new AngularDimension { First = _angularFirst, Second = line, Through = raw }.Geometry(Document) is null)
                {
                    _angularSecond = null;
                    HintChanged?.Invoke(this, "Those two are parallel: there is no angle between them. Click another.");
                    return false;
                }

                HintChanged?.Invoke(this, "Now click where the arc goes: in the angle to measure, as far out as it is to be drawn.");
            }

            InvalidateVisual();
            return true;
        }

        var dimension = new AngularDimension { First = _angularFirst, Second = _angularSecond, Through = raw, LevelId = ActiveLevelId, TypeId = ActiveDimensionType() };
        (_angularFirst, _angularSecond) = (null, null);
        Apply(new AddElementCommand(Document, dimension, "Angular Dimension"));
        Select(dimension);
        HintChanged?.Invoke(this, $"{dimension.DisplayText(Document)} between them - it follows them as they move.");
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click with the Radial tool: a curved wall - its centreline, or the face or core face the
    /// options bar prefers - then where the dimension goes. Public so it can be driven from tests.
    /// </summary>
    public bool ClickRadial(Point2D raw)
    {
        if (Document is null) return false;

        if (_radialTarget is null)
        {
            if (LineReferenceAt(raw) is not { } line || new RadialDimension { Target = line }.Arc(Document) is null)
            {
                HintChanged?.Invoke(this, "Click a curved wall: its radius, diameter or length along it is what is measured.");
                return false;
            }

            _radialTarget = line;
            HintChanged?.Invoke(this, ActiveRadialKind == RadialKind.ArcLength
                ? "Now click where the dimension goes: how far out from the wall it is drawn."
                : "Now click where the dimension goes: the side of the arc it is drawn to.");
            InvalidateVisual();
            return true;
        }

        var dimension = new RadialDimension { Kind = ActiveRadialKind, Target = _radialTarget, Through = raw, LevelId = ActiveLevelId, TypeId = ActiveDimensionType() };
        _radialTarget = null;
        Apply(new AddElementCommand(Document, dimension, dimension.ToString()));
        Select(dimension);
        HintChanged?.Invoke(this, $"{dimension.DisplayText(Document)} - it follows the wall as it changes.");
        InvalidateVisual();
        return true;
    }

    private bool CancelDimensionTools()
    {
        var was = _dimensionString.Count > 0 || _angularFirst is not null || _radialTarget is not null;
        _dimensionString.Clear();
        (_angularFirst, _angularSecond, _radialTarget) = (null, null, null);
        if (was) InvalidateVisual();
        return was;
    }

    /// <summary>The string, angle or radius being drawn, as it will be.</summary>
    private void DrawPendingDimensionTools(DrawingContext dc)
    {
        if (Document is null) return;
        var type = Document.FindType<DimensionType>(ActiveDimensionType()) ?? DimensionTypes.Defaults().First();

        if (ActiveTool == PlanTool.Dimension && _dimensionString.Count > 0)
        {
            var references = _dimensionString.Append(ReferenceAt(_cursorModel)).ToList();
            var preview = new Dimension { Start = references[0], End = references[^1] };
            preview.Between.AddRange(references.Skip(1).Take(references.Count - 2));
            _renderer.DrawPendingDimension(dc, preview.Stations(Document), 600, type);
        }

        if (ActiveTool == PlanTool.AngularDimension && _angularFirst is { } first)
        {
            if (first.WallLineSpan(Document) is { } span) dc.DrawLine(_selectedPen, ModelToScreen(span.From), ModelToScreen(span.To));
            if (_angularSecond is { } second)
                _renderer.DrawPreview(dc, new AngularDimension { First = first, Second = second, Through = _cursorModel, TypeId = type.Id });
        }

        if (ActiveTool == PlanTool.RadialDimension && _radialTarget is { } target)
            _renderer.DrawPreview(dc, new RadialDimension { Kind = ActiveRadialKind, Target = target, Through = _cursorModel, TypeId = type.Id });
    }

    /// <summary>The angular and radial dimensions on this storey near a point: on their arc or line.</summary>
    private IEnumerable<Element> OtherDimensionsAt(Point2D model)
    {
        if (Document is null) yield break;
        var reach = 6 / PixelsPerMm;
        foreach (var angular in OnActiveLevel<AngularDimension>())
            if (angular.Geometry(Document) is { } g && Math.Abs(model.DistanceTo(g.Centre) - g.Radius) <= reach) yield return angular;
        foreach (var radial in OnActiveLevel<RadialDimension>())
        {
            if (radial.Arc(Document) is not { } arc) continue;
            var toward = (radial.Through - arc.Centre).NormalisedOrDefault(Vector2D.UnitX);
            var near = radial.Kind switch
            {
                RadialKind.ArcLength => Math.Abs(model.DistanceTo(arc.Centre) - radial.Through.DistanceTo(arc.Centre)) <= reach,
                RadialKind.Diameter => DistanceToSegment(model, arc.Centre - toward * arc.Radius, arc.Centre + toward * arc.Radius) <= reach,
                _ => DistanceToSegment(model, arc.Centre, arc.Centre + toward * arc.Radius) <= reach
            };
            if (near) yield return radial;
        }
    }
}
