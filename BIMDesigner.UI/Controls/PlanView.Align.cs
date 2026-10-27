using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Align: the line to align to - a wall's face or centreline, a gridline - then what is to
/// come onto it: a wall's face or centreline, or a gridline, running the same way; an edge of
/// a piece of furniture or a fitting; or where a column or anything else standing at a point
/// stands. It moves straight across onto the line, nothing else about it changing; a wall
/// takes the walls joined to it with it. The line stays picked for the next, until Esc.
/// </summary>
public partial class PlanView
{
    private (Point2D Point, Vector2D Direction, Guid Of)? _alignTo;

    /// <summary>The line Align brings things onto, once it is picked.</summary>
    public (Point2D Point, Vector2D Direction)? AlignLine => _alignTo is { } line ? (line.Point, line.Direction) : null;

    /// <summary>
    /// A wall's line or a gridline under a point: a wall's centreline where the point is in the
    /// middle third of it, otherwise its face on that side.
    /// </summary>
    private (Point2D Point, Vector2D Direction, Element Of)? AlignLineAt(Point2D point)
    {
        if (Document is null) return null;
        var tolerance = Math.Max(WallJoins.JoinTolerance, 4 / PixelsPerMm);

        foreach (var wall in OnActiveLevel<Wall>().Where(wall => !wall.IsCurved))
        {
            if (Document.GetWallType(wall) is not { } type) continue;
            var (along, across) = wall.Locate(type.Structure, point);
            var half = type.Structure.TotalWidth / 2;
            if (along < 0 || along > wall.Length || Math.Abs(across) > half + tolerance) continue;

            var anchor = Math.Abs(across) <= half / 3 ? DimensionAnchor.WallCentreline
                : across > 0 ? DimensionAnchor.WallExteriorFace : DimensionAnchor.WallInteriorFace;
            if (DimensionReference.ToWallLine(wall, anchor, point).Line(Document) is { } line) return (line.Point, line.Direction, wall);
        }

        foreach (var grid in OnActiveLevel<Grid>())
            if (grid.Line.ClosestPointTo(point).DistanceTo(point) <= tolerance)
                return (grid.Line.ClosestPointTo(point), (grid.End - grid.Start).NormalisedOrDefault(Vector2D.UnitX), grid);

        return null;
    }

    /// <summary>A click with the Align tool: the line to align to, then each thing to bring onto it. Public so tests can drive it.</summary>
    public bool AlignClick(Point2D raw)
    {
        if (Document is null) return false;

        if (_alignTo is not { } to)
        {
            if (AlignLineAt(raw) is not { } picked)
            {
                HintChanged?.Invoke(this, "Click the line to align to: a wall's face or centreline, or a gridline.");
                return false;
            }

            _alignTo = (picked.Point, picked.Direction, picked.Of.Id);
            HintChanged?.Invoke(this, "Now click what is to come onto it: a wall's face or centreline or a gridline running the same way, an edge of something, or a column.");
            InvalidateVisual();
            return true;
        }

        var across = to.Direction.PerpendicularLeft();

        // A wall or gridline running the same way, by its face, centreline or line.
        Element? moving = null;
        Point2D? from = null;
        if (AlignLineAt(raw) is { } line && line.Of.Id != to.Of)
        {
            if (Math.Abs(line.Direction.Cross(to.Direction)) > 0.01)
            {
                HintChanged?.Invoke(this, "That does not run the same way as the line picked, so it cannot be brought onto it.");
                return false;
            }

            (moving, from) = (line.Of, line.Point);
        }

        // Anything else that moves on its own: its edge running the same way nearest the click, or where it stands.
        if (moving is null && HitTest(raw) is { } element && element.Id != to.Of && ElementTransforms.CanMove(element))
        {
            var edges = Ghost(element).SelectMany(points => points.Zip(points.Skip(1)))
                .Where(edge => edge.First.DistanceTo(edge.Second) > 1 && Math.Abs((edge.Second - edge.First).NormalisedOrDefault(Vector2D.UnitX).Cross(to.Direction)) < 0.01)
                .OrderBy(edge => DistanceToSegment(raw, edge.First, edge.Second))
                .ToList();
            var stands = ElementTransforms.PlanPoints(element) is { Count: 1 } points ? points[0] : (Point2D?)null;
            from = edges.Count > 0 ? edges[0].First : stands;
            moving = from is null ? null : element;
        }

        if (moving is null || from is not { } start)
        {
            HintChanged?.Invoke(this, "Nothing there to bring onto the line. Click a wall, a gridline, or an edge or a column.");
            return false;
        }

        if (RefusePinned(new[] { moving }, "moved")) return false;

        var shift = across * (to.Point - start).Dot(across);
        if (shift.Length < 0.5)
        {
            HintChanged?.Invoke(this, "Already on the line.");
            return true;
        }

        // Walls joined to a wall moved go on meeting it, as when it is dragged.
        Select(moving);
        CollectMoveSet();
        var command = new NudgeElementsCommand(_moveSet, _followers.Select(follower => (follower.Wall, follower.AtStart)), shift, Document);
        if (command.IsEmpty) return false;
        History?.Execute(command);

        HintChanged?.Invoke(this, $"Aligned: moved {Units.FormatLength(shift.Length)}. Click another to bring onto the same line, or Esc to pick a new one.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private bool CancelAlign()
    {
        if (_alignTo is null) return false;
        _alignTo = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The line being aligned to, run right across the view.</summary>
    private void DrawAlignLine(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Align) return;

        if (_alignTo is { } to)
        {
            DrawModelPolyline(dc, _previewPen, new[] { to.Point - to.Direction * 1e6, to.Point + to.Direction * 1e6 });
            return;
        }

        // Before the first pick, the line a click would take lights up.
        if (AlignLineAt(_cursorModel) is { } under)
            DrawModelPolyline(dc, _selectedPen, new[] { under.Point - under.Direction * 2000, under.Point + under.Direction * 2000 });
    }
}
