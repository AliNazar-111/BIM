using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Revit's other arc walls: a Tangent Arc carrying on smoothly from the wall before it, a
/// Centre-Ends Arc drawn from its centre, a Fillet Arc rounding off the corner between two walls,
/// and a radius set while drawing straight walls, which rounds off every corner of the chain.
/// </summary>
public partial class PlanView
{
    /// <summary>The radius of a fillet arc, and of the corners of a chain drawn with Radius on, mm.</summary>
    public double ArcRadius { get; set; } = 1000;

    /// <summary>Whether straight walls drawn one after another have their corners rounded off at <see cref="ArcRadius"/>.</summary>
    public bool ChainRadius { get; set; }

    /// <summary>A tangent arc started from the end of a wall, not a chain: which way it leaves.</summary>
    private Vector2D? _tangentWay;

    private Point2D? _arcCentre;
    private Point2D? _arcCentreStart;
    private Wall? _filletFirst;

    private bool CancelArcTools()
    {
        var any = _tangentWay is not null || _arcCentre is not null || _filletFirst is not null;
        _tangentWay = null;
        _arcCentre = null;
        _arcCentreStart = null;
        _filletFirst = null;
        return any;
    }

    /// <summary>A wall like the ones being drawn, along a line or an arc.</summary>
    private Wall NewDrawnWall(Point2D start, Point2D end, double bulge) => new()
    {
        Start = start, End = end, Bulge = bulge,
        TypeId = ActiveWallTypeId, LevelId = ActiveLevelId, LocationLine = ActiveLocationLine, Flipped = DrawFlipped
    };

    /// <summary>The end of a wall on this level near a point, and which way is out of the wall there.</summary>
    private (Point2D At, Vector2D Out)? WallEndNear(Point2D point)
    {
        var tolerance = Math.Max(WallJoins.JoinTolerance, 6 / PixelsPerMm);
        foreach (var wall in OnActiveLevel<Wall>())
        {
            var curve = wall.LocationCurve;
            if (wall.Start.DistanceTo(point) <= tolerance) return (wall.Start, -curve.TangentAt(0));
            if (wall.End.DistanceTo(point) <= tolerance) return (wall.End, curve.TangentAt(curve.Length));
        }

        return null;
    }

    /// <summary>Which way a tangent arc leaves its start: along the wall it carries on from.</summary>
    private Vector2D? TangentHere() =>
        _tangentWay ?? (_chainWall is { } previous && Document?.Elements.Contains(previous) == true && _pendingWallStart is { } at &&
                        previous.End.DistanceTo(at) < 1
            ? previous.LocationCurve.TangentAt(previous.LocationCurve.Length)
            : null);

    /// <summary>
    /// A click with Tangent Arc: the first on the end of a wall, to carry on from; each after it
    /// where the arc ends, leaving the last one smoothly. Arcs chain, each tangent to the one before.
    /// </summary>
    private void PlaceTangentArcPoint(Point2D model)
    {
        if (Document is null) return;

        if (_pendingWallStart is null || TangentHere() is null)
        {
            if (WallEndNear(model) is not var (at, way))
            {
                HintChanged?.Invoke(this, "A tangent arc carries on from a wall: click the end of one to start from.");
                return;
            }

            _pendingWallStart = at;
            _tangentWay = way;
            _chainWall = null;
            _chainFirstWall = null;
            HintChanged?.Invoke(this, "Click where the arc ends: it leaves the wall smoothly. Esc stops.");
            InvalidateVisual();
            return;
        }

        var from = _pendingWallStart.Value;
        if (from.DistanceTo(model) < SnapStepMm || TangentHere() is not { } tangent) return;
        if (Math.Abs(Math.Atan2(tangent.Cross(model - from), tangent.Dot(model - from))) > Math.PI * 0.95)
        {
            HintChanged?.Invoke(this, "That is back the way the wall came: click somewhere ahead of it.");
            return;
        }

        var wall = NewDrawnWall(from, model, WallArcs.TangentBulge(tangent, from, model));
        Apply(new AddElementCommand(Document, Configured(wall), "Draw Tangent Arc"));

        _tangentWay = null;
        _pendingWallStart = model;
        _chainWall = wall;
        _chainFirstWall ??= wall;
        Select(wall);
        HintChanged?.Invoke(this, "Tangent arc placed. Click where the next one ends, or Esc to stop.");
    }

    /// <summary>A click with Centre-Ends Arc: the centre, then the start - which sets the radius - then where it ends.</summary>
    private void PlaceCentreArcPoint(Point2D model)
    {
        if (Document is null) return;

        if (_arcCentre is not { } centre)
        {
            _arcCentre = model;
            HintChanged?.Invoke(this, "Click where the arc starts: its distance from the centre is the radius. Esc cancels.");
            InvalidateVisual();
            return;
        }

        if (_arcCentreStart is not { } start)
        {
            if (centre.DistanceTo(model) < SnapStepMm) return;
            _arcCentreStart = model;
            HintChanged?.Invoke(this, $"Radius {Units.FormatLength(centre.DistanceTo(model))}. Click where the arc ends - it goes the shorter way round.");
            InvalidateVisual();
            return;
        }

        if (WallArcs.CentreEnds(centre, start, model) is not var (from, to, bulge))
        {
            HintChanged?.Invoke(this, "Click somewhere round from the start, off the centre.");
            return;
        }

        var wall = NewDrawnWall(from, to, bulge);
        Apply(new AddElementCommand(Document, Configured(wall), "Draw Centre-Ends Arc"));
        _arcCentre = null;
        _arcCentreStart = null;
        Select(wall);
        HintChanged?.Invoke(this, "Arc placed. Click the centre of the next one, or Esc to stop.");
    }

    /// <summary>A click with Fillet Arc: the first wall, then the second - their corner is rounded off at the radius on the options bar.</summary>
    private void PickFilletWall(Point2D model)
    {
        if (Document is null) return;

        if (HitTestWall(model) is not { } wall)
        {
            HintChanged?.Invoke(this, "Click a wall, then the wall it meets at the corner to round off.");
            return;
        }

        if (_filletFirst is null || ReferenceEquals(_filletFirst, wall))
        {
            _filletFirst = wall;
            Select(wall);
            HintChanged?.Invoke(this, $"Now click the other wall of the corner. The radius is {Units.FormatLength(ArcRadius)} - set it on the options bar.");
            return;
        }

        var first = _filletFirst;
        _filletFirst = null;
        if (RoundCorner(first, wall, ArcRadius) is { } arc) Select(arc);
    }

    /// <summary>
    /// Rounds off the corner between two straight walls with an arc wall of a radius, cutting each
    /// back to where the arc leaves it, as one step. Null, saying why, where it cannot be done.
    /// Public so tests can drive it.
    /// </summary>
    public Wall? RoundCorner(Wall first, Wall second, double radius)
    {
        if (Document is null) return null;
        if (RefusePinned(new[] { first, second }, "cut back for a fillet")) return null;

        if (WallArcs.Round(first, second, radius, out var problem) is not { } fillet)
        {
            HintChanged?.Invoke(this, problem ?? "Those walls cannot be rounded off.");
            return null;
        }

        if (OpeningInTheWay(first, fillet.First) || OpeningInTheWay(second, fillet.Second))
        {
            HintChanged?.Invoke(this, "A door or window is in the part of a wall the arc takes away. Use a smaller radius, or move it.");
            return null;
        }

        var arc = FilletWall(first, fillet);
        Apply(new CompositeCommand("Fillet Arc", new IUndoableCommand[]
        {
            new MoveWallCommand(first, first.Start, first.End, fillet.First.Start, fillet.First.End, "Fillet Arc").KeepingOpenings(Document),
            new MoveWallCommand(second, second.Start, second.End, fillet.Second.Start, fillet.Second.End, "Fillet Arc").KeepingOpenings(Document),
            new AddElementCommand(Document, arc, "Fillet Arc")
        }));

        HintChanged?.Invoke(this, $"Corner rounded off, radius {Units.FormatLength(radius)}. Click two more walls, or Esc.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return arc;
    }

    /// <summary>Whether a door or window would be left off a wall cut back to a new start and end.</summary>
    private bool OpeningInTheWay(Wall wall, (Point2D Start, Point2D End) kept)
    {
        if (Document is null) return false;

        var cutAtStart = kept.Start.DistanceTo(wall.Start) > 1e-6 ? kept.Start.DistanceTo(wall.Start) : 0;
        var length = kept.Start.DistanceTo(kept.End);
        foreach (var opening in WallOpenings.Of(Document, wall))
        {
            if (Document.FindType<OpeningType>(opening.TypeId) is not { } type) continue;
            var (from, to) = opening.GetSpan(type);
            if (from - cutAtStart < -1e-6 || to - cutAtStart > length + 1e-6) return true;
        }

        return false;
    }

    /// <summary>The arc wall rounding off a corner: built like the first wall, its outside the same side.</summary>
    private static Wall FilletWall(Wall first, WallArcs.Fillet fillet)
    {
        // The arc leaves the first wall going toward the corner; where the wall itself runs the
        // other way, the arc's exterior is on its other hand.
        var firstToward = (fillet.ArcStart - (fillet.First.Start.DistanceTo(fillet.ArcStart) < 1e-6 ? fillet.First.End : fillet.First.Start))
            .NormalisedOrDefault(Vector2D.UnitX);
        var sameWay = firstToward.Dot(first.Direction) > 0;

        return new Wall
        {
            Start = fillet.ArcStart, End = fillet.ArcEnd, Bulge = fillet.Bulge,
            TypeId = first.TypeId, LevelId = first.LevelId, TopLevelId = first.TopLevelId, TopOffset = first.TopOffset,
            BaseOffset = first.BaseOffset, UnconnectedHeight = first.UnconnectedHeight, LocationLine = first.LocationLine,
            Flipped = sameWay ? first.Flipped : !first.Flipped, StructuralUsage = first.StructuralUsage, RoomBounding = first.RoomBounding
        };
    }

    /// <summary>
    /// Drawing straight walls with Radius on: the corner the new wall makes with the one before it
    /// rounded off. The commands to add to the new wall's own, and the new wall cut back to the
    /// arc; or nothing where the radius does not fit, saying so.
    /// </summary>
    private IReadOnlyList<IUndoableCommand> RoundChainCorner(Wall wall)
    {
        if (!ChainRadius || Document is null || DrawOffset != 0) return Array.Empty<IUndoableCommand>();
        if (_chainWall is not { IsCurved: false } previous || !Document.Elements.Contains(previous)) return Array.Empty<IUndoableCommand>();
        if (previous.End.DistanceTo(wall.Start) > 1) return Array.Empty<IUndoableCommand>();

        if (WallArcs.Round(previous, wall, ArcRadius, out var problem) is not { } fillet || OpeningInTheWay(previous, fillet.First))
        {
            HintChanged?.Invoke(this, problem ?? "A door or window is where the corner would be rounded.");
            return Array.Empty<IUndoableCommand>();
        }

        (wall.Start, wall.End) = fillet.Second;
        return new IUndoableCommand[]
        {
            new MoveWallCommand(previous, previous.Start, previous.End, fillet.First.Start, fillet.First.End, "Draw Wall").KeepingOpenings(Document),
            new AddElementCommand(Document, Configured(FilletWall(previous, fillet)), "Draw Wall")
        };
    }

    /// <summary>What the arc tools are about to place, following the cursor.</summary>
    private void DrawArcToolPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Wall || Document is null) return;
        var type = Document.PlanWallType(ActiveWallTypeId, NewWallHeight);

        void Show(Wall wall)
        {
            DrawModelPolyline(dc, _previewPen, wall.LocationCurve.Points());
            if (type is not null) DrawWallBody(dc, wall, type, _previewPen);
        }

        switch (_drawShape)
        {
            case WallShape.TangentArc when _pendingWallStart is { } from && TangentHere() is { } tangent && from.DistanceTo(_cursorModel) >= SnapStepMm:
                dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(from), 4, 4);
                Show(NewDrawnWall(from, _cursorModel, WallArcs.TangentBulge(tangent, from, _cursorModel)));
                break;

            case WallShape.CentreEndsArc when _arcCentre is { } centre:
                dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(centre), 4, 4);
                if (_arcCentreStart is not { } start)
                {
                    var r = centre.DistanceTo(_cursorModel) * PixelsPerMm;
                    dc.DrawLine(_previewPen, ModelToScreen(centre), ModelToScreen(_cursorModel));
                    dc.DrawEllipse(null, _previewPen, ModelToScreen(centre), r, r);
                }
                else if (WallArcs.CentreEnds(centre, start, _cursorModel) is var (a, b, bulge))
                {
                    dc.DrawLine(_previewPen, ModelToScreen(centre), ModelToScreen(a));
                    dc.DrawLine(_previewPen, ModelToScreen(centre), ModelToScreen(b));
                    Show(NewDrawnWall(a, b, bulge));
                }

                break;

            case WallShape.FilletArc when _filletFirst is { } first && HitTestWall(_cursorModel) is { } second && !ReferenceEquals(first, second) &&
                                          WallArcs.Round(first, second, ArcRadius, out _) is { } fillet:
                Show(FilletWall(first, fillet));
                break;
        }
    }
}
