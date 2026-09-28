using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Where a roof by extrusion is to go, from the three clicks that place it: the line its
/// profile is drawn on, how wide that is, how far the roof runs back from it, and the base
/// it would sit at.
/// </summary>
public sealed record RoofExtrusionRequest(Point2D Origin, Vector2D Direction, double Width, double Start, double End, double BaseOffset);

/// <summary>
/// Placing a roof by extrusion in plan (specification section 3.3; Revit's Roof by Extrusion).
///
/// Revit sets the work plane and draws the profile in an elevation. Here the plan says the
/// same three things with three clicks - where the profile's line starts, where it ends, and
/// how far the roof runs back from it - and the profile is then drawn square-on in a window of
/// its own, over the width just clicked.
/// </summary>
public partial class PlanView
{
    private Point2D? _extrusionFrom;
    private Point2D? _extrusionTo;
    private Point2D _extrusionCursor;

    /// <summary>Raised on the third click: where the roof goes, for its profile to be drawn.</summary>
    public event EventHandler<RoofExtrusionRequest>? ExtrusionPlaced;

    /// <summary>Raised when a roof by extrusion is double-clicked: its profile wants opening.</summary>
    public event EventHandler<Roof>? EditProfileRequested;

    /// <summary>What a click does with the Roof by Extrusion tool. Public so it can be driven from tests.</summary>
    public bool ExtrusionClick(Point2D raw)
    {
        if (Document is null) return false;

        var point = SnapPoint(raw, null, out _);

        if (_extrusionFrom is not { } from)
        {
            _extrusionFrom = point;
            HintChanged?.Invoke(this, "Click the other end of the profile's line - the width the roof spans.");
            InvalidateVisual();
            return true;
        }

        if (_extrusionTo is not { } to)
        {
            if (from.DistanceTo(point) < SnapStepMm)
            {
                HintChanged?.Invoke(this, "Too short to span anything. Click further away.");
                return false;
            }

            _extrusionTo = point;
            HintChanged?.Invoke(this, "Click how far the roof runs back from that line.");
            InvalidateVisual();
            return true;
        }

        var direction = (to - from).NormalisedOrDefault(Vector2D.UnitX);
        var depth = Units.SnapToGrid((point - from).Dot(direction.PerpendicularLeft()), SnapStepMm);

        if (Math.Abs(depth) < SnapStepMm)
        {
            HintChanged?.Invoke(this, "The roof needs some depth. Click further from the line.");
            return false;
        }

        var request = new RoofExtrusionRequest(
            from, direction, from.DistanceTo(to), Math.Min(0, depth), Math.Max(0, depth),
            ExtrusionBaseOffset(from, direction, from.DistanceTo(to), Math.Min(0, depth), Math.Max(0, depth)));

        _extrusionFrom = null;
        _extrusionTo = null;
        InvalidateVisual();

        ExtrusionPlaced?.Invoke(this, request);
        return true;
    }

    /// <summary>
    /// Where an extruded roof over this rectangle should sit: on the tops of the walls under
    /// it, as a footprint roof picked from them does, or on the storey above where there are
    /// none.
    /// </summary>
    private double ExtrusionBaseOffset(Point2D origin, Vector2D direction, double width, double start, double end)
    {
        if (Document is null) return 3000;

        var level = Document.FindLevel(ActiveLevelId)?.Elevation ?? 0;
        var across = direction.PerpendicularLeft();
        var outline = new[]
        {
            origin + across * start, origin + direction * width + across * start,
            origin + direction * width + across * end, origin + across * end
        };

        var tops = WallsUnder(outline, OnActiveLevel<Wall>())
            .Select(wall => wall.GetTopElevation(Document))
            .ToList();

        if (tops.Count > 0) return tops.Max() - level;

        var above = Document.Levels.Where(other => other.Elevation > level + 1).OrderBy(other => other.Elevation).FirstOrDefault();
        return above is not null ? above.Elevation - level : 3000;
    }

    /// <summary>The walls an outline covers: those standing inside it or along its edge.</summary>
    private static IEnumerable<Wall> WallsUnder(IReadOnlyList<Point2D> outline, IEnumerable<Wall> walls)
    {
        var ring = Polygon2D.SignedArea(outline) >= 0 ? outline : outline.Reverse().ToArray();

        return walls.Where(wall =>
        {
            var middle = wall.LocationCurve.PointAt(wall.Length / 2);
            return Polygon2D.Contains(ring, middle) ||
                   ring.Select((corner, i) => Line2D.DistanceFromSegment(middle, corner, ring[(i + 1) % ring.Count])).Min() < 400;
        });
    }

    /// <summary>
    /// Where the building under an extruded roof starts along its profile's line, and how
    /// wide it is: from the walls the roof covers, so its profile is redrawn against what it
    /// spans. The profile's own width where it covers none.
    /// </summary>
    public (double Start, double Width) SpanUnder(Roof roof)
    {
        if (roof.Extrusion is not { Profile.Count: >= 2 } extrusion) return (0, 0);

        var profile = (Start: extrusion.Profile[0].X, Width: extrusion.Profile[^1].X - extrusion.Profile[0].X);
        if (Document is null) return profile;

        var along = WallsUnder(extrusion.Footprint(), Document.Elements.OfType<Wall>().Where(wall => wall.LevelId == roof.LevelId))
            .SelectMany(wall => new[] { wall.Start, wall.End })
            .Select(point => (point - extrusion.Origin).Dot(extrusion.Direction))
            .ToList();

        return along.Count > 0 && along.Max() - along.Min() > 1
            ? (along.Min(), along.Max() - along.Min())
            : profile;
    }

    /// <summary>
    /// Makes the roof once its profile is drawn: one undoable step, on this storey at the base
    /// given, and selected.
    /// </summary>
    public Roof? PlaceExtrusionRoof(RoofExtrusion extrusion, double baseOffset)
    {
        if (Document is null) return null;

        if (extrusion.Problem() is { } problem)
        {
            HintChanged?.Invoke(this, problem);
            return null;
        }

        var type = Document.FindType<SlabType>(ActiveRoofTypeId) as RoofType
                   ?? Document.TypesOf<RoofType>().FirstOrDefault();
        if (type is null)
        {
            HintChanged?.Invoke(this, "This project has no roof types to build the roof from.");
            return null;
        }

        var roof = new Roof { TypeId = type.Id, LevelId = ActiveLevelId, HeightOffset = baseOffset };
        roof.SetExtrusion(extrusion);

        Apply(new AddElementCommand(Document, roof, "Create Roof by Extrusion"));
        Select(roof);

        HintChanged?.Invoke(this,
            $"{EnumText.Humanise(roof.Form)} roof by extrusion: {Units.FormatArea(roof.SlopingArea(Document))} of covering. " +
            "Edit Profile, or double-click it, to change its section.");

        ModelChanged?.Invoke(this, EventArgs.Empty);
        return roof;
    }

    /// <summary>Changes an extruded roof's profile, as one undoable step.</summary>
    public bool ChangeExtrusion(Roof roof, RoofExtrusion extrusion, double baseOffset)
    {
        if (Document is null || extrusion.Problem() is not null) return false;

        var commands = new List<IUndoableCommand> { new SetRoofExtrusionCommand(roof, extrusion) };

        if (Math.Abs(roof.HeightOffset - baseOffset) > 1e-6)
            commands.Add(new SetRoofBaseCommand(roof, baseOffset));

        Apply(commands.Count == 1 ? commands[0] : new CompositeCommand("Edit Profile", commands));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    private void ExtrusionHover(Point2D raw)
    {
        _extrusionCursor = SnapPoint(raw, null, out _);
        if (_extrusionFrom is not null) InvalidateVisual();
    }

    private bool CancelExtrusion()
    {
        if (_extrusionFrom is null) return false;

        _extrusionFrom = null;
        _extrusionTo = null;
        return true;
    }

    /// <summary>What the next click would make: the profile's line, then the roof's rectangle behind it.</summary>
    private void DrawExtrusionPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.RoofExtrusion || _extrusionFrom is not { } from) return;

        if (_extrusionTo is not { } to)
        {
            dc.DrawLine(SketchPreviewPen, ModelToScreen(from), ModelToScreen(_extrusionCursor));
            return;
        }

        var direction = (to - from).NormalisedOrDefault(Vector2D.UnitX);
        var across = direction.PerpendicularLeft();
        var depth = Units.SnapToGrid((_extrusionCursor - from).Dot(across), SnapStepMm);

        var corners = new[] { from, to, to + across * depth, from + across * depth };
        for (var i = 0; i < corners.Length; i++)
            dc.DrawLine(i == 0 ? SketchPen : SketchPreviewPen, ModelToScreen(corners[i]), ModelToScreen(corners[(i + 1) % corners.Length]));
    }
}

/// <summary>Changes a roof's base offset, as one undoable step.</summary>
public sealed class SetRoofBaseCommand : IUndoableCommand
{
    private readonly Roof _roof;
    private readonly double _old;
    private readonly double _new;

    public SetRoofBaseCommand(Roof roof, double offset)
    {
        _roof = roof;
        _old = roof.HeightOffset;
        _new = offset;
    }

    public string Name => "Change Roof Base";

    public void Redo() => _roof.HeightOffset = _new;

    public void Undo() => _roof.HeightOffset = _old;
}
