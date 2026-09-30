using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Roof windows on the plan: click on a roof's slope and a roof window goes in it, lined up with
/// the slope, the roof cut away under it - Revit's skylight hosted by a roof. The outline it
/// would have follows the cursor, in red where it does not fit.
/// </summary>
public partial class PlanView
{
    /// <summary>The roof window type the Roof Window tool puts in.</summary>
    public Guid ActiveRoofWindowTypeId { get; set; }

    private Point2D? _roofWindowHover;

    private RoofWindowType? ActiveRoofWindowType() =>
        Document?.FindType<RoofWindowType>(ActiveRoofWindowTypeId) ?? Document?.TypesOf<RoofWindowType>().FirstOrDefault();

    /// <summary>The roof a roof window goes in at a point: the highest one there, on this storey - a dormer's over the roof it comes out of.</summary>
    private Roof? RoofWindowRoofAt(Point2D raw) =>
        Document is null
            ? null
            : OnActiveLevel<Roof>()
                .Where(candidate => !candidate.IsExtrusion && candidate.Contains(raw))
                .OrderByDescending(candidate => candidate.TopAt(Document, raw))
                .FirstOrDefault();

    private void RoofWindowHover(Point2D raw)
    {
        _roofWindowHover = raw;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Roof Window tool: a roof window in the roof there, its middle where the
    /// click was. Public so it can be driven from tests.
    /// </summary>
    public bool PlaceRoofWindowAt(Point2D raw)
    {
        if (Document is null) return false;

        if (RoofWindowRoofAt(raw) is not { } roof)
        {
            HintChanged?.Invoke(this, "Click on a roof: the window goes in its slope.");
            return false;
        }

        return PlaceRoofWindow(roof, raw);
    }

    /// <summary>A click on a roof in the 3D view with the Roof Window tool: a roof window where it was clicked.</summary>
    public bool PlaceRoofWindowIn3D(Guid roofId, Point3D at) =>
        Document?.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == roofId) is { } roof && PlaceRoofWindow(roof, new Point2D(at.X, at.Y));

    private bool PlaceRoofWindow(Roof roof, Point2D at)
    {
        if (Document is null) return false;

        if (ActiveRoofWindowType() is not { } type)
        {
            HintChanged?.Invoke(this, "There is no roof window type in the project to put in.");
            return false;
        }

        if (RoofWindows.FrameOn(Document, roof, type, at) is not { } frame)
        {
            HintChanged?.Invoke(this, "A roof window goes in a roof drawn by footprint: click on its slope.");
            return false;
        }

        if (RoofWindows.Problem(Document, frame) is { } problem)
        {
            HintChanged?.Invoke(this, problem);
            return false;
        }

        var window = new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = at };
        Apply(new AddElementCommand(Document, window, "Roof Window"));

        HintChanged?.Invoke(this, $"{type.Name} in the roof, lined up with its slope, the roof cut away under it. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Raised with why, when a roof window dragged or moved would not fit where it was left, and has gone back.</summary>
    public event EventHandler<string>? RoofWindowRefused;

    /// <summary>The roof window being dragged in 3D, and where it was when the drag began.</summary>
    private (RoofWindow Window, Point2D From)? _roofWindowDrag;

    /// <summary>Whether an element can be dragged across the model in 3D: a roof window, along its roof.</summary>
    public bool CanDragIn3D(Guid id) => Document?.Elements.OfType<RoofWindow>().Any(window => window.Id == id) == true;

    /// <summary>
    /// A roof window dragged in 3D, to where the line of sight through the cursor meets its roof.
    /// It goes there when it fits; where it would not - over a ridge, off the edge, over another
    /// opening - it waits where it last fitted and the status bar says why. True when it moved.
    /// Public so it can be driven from tests.
    /// </summary>
    public bool DragRoofWindowIn3D(Guid id, Point3D from, Point3D through)
    {
        if (Document?.Elements.OfType<RoofWindow>().FirstOrDefault(window => window.Id == id) is not { } window) return false;
        if (Document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == window.RoofId) is not { } roof) return false;
        if (Document.FindType<RoofWindowType>(window.TypeId) is not { } type) return false;

        if (_roofWindowDrag is not { } drag || !ReferenceEquals(drag.Window, window)) _roofWindowDrag = (window, window.Location);

        if (RoofWindows.Along(Document, roof, from, through) is not { } at ||
            RoofWindows.FrameOn(Document, roof, type, at) is not { } frame)
        {
            HintChanged?.Invoke(this, "Keep the cursor on the roof: a roof window slides along the roof it is in.");
            return false;
        }

        if (RoofWindows.Problem(Document, frame, window) is { } problem)
        {
            HintChanged?.Invoke(this, problem);
            return false;
        }

        if (window.Location == at) return false;

        window.Location = at;
        HintChanged?.Invoke(this, "Release to leave it there: it lines up with the slope it is over.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The end of a roof window's drag in 3D: the whole of it one move to undo.</summary>
    public void EndRoofWindowDrag()
    {
        if (_roofWindowDrag is not var (window, from) || Document is null) return;
        _roofWindowDrag = null;

        var moved = window.Location - from;
        if (moved.Length < 1e-9) return;

        // Put back, and moved again by the command, so what is recorded is what was done.
        window.Location = from;
        Apply(new MoveElementsCommand(new[] { window }, moved, "Move Roof Window", Document));

        HintChanged?.Invoke(this, $"Moved {Units.FormatLength(moved.Length)} along the roof.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Why the roof windows in what is being moved could not go a step further without their
    /// roof - off it, over a ridge, onto another opening - or null where they can. Tried and put
    /// back, so nothing is left moved.
    /// </summary>
    private string? RoofWindowStepProblem(IReadOnlyList<Element> moving, Vector2D step)
    {
        if (Document is null || !moving.OfType<RoofWindow>().Any()) return null;

        var carried = moving.Where(ElementTransforms.CanMove).ToList();
        foreach (var element in carried) ElementTransforms.Move(element, step);
        try
        {
            return RoofWindows.Misplaced(Document, carried);
        }
        finally
        {
            foreach (var element in carried) ElementTransforms.Move(element, -step);
        }
    }

    /// <summary>Where the roof window would go, following the cursor: its frame and glass, in red where it does not fit.</summary>
    private void DrawRoofWindowPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.RoofWindow || _roofWindowHover is not { } hover || Document is null) return;
        if (RoofWindowRoofAt(hover) is not { } roof || ActiveRoofWindowType() is not { } type) return;
        if (RoofWindows.FrameOn(Document, roof, type, hover) is not { } frame) return;

        var pen = RoofWindows.Problem(Document, frame) is null ? SketchPreviewPen : DormerMissPen;
        foreach (var outline in new[] { frame.Corners, frame.Glass })
            dc.DrawGeometry(null, pen, Outline(outline));
    }

    /// <summary>A closed outline in plan, as drawn on screen.</summary>
    private StreamGeometry Outline(IReadOnlyList<Point2D> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ModelToScreen(points[0]), false, true);
            foreach (var point in points.Skip(1)) context.LineTo(ModelToScreen(point), true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
