using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Outlines drawn on the plan: a region of the ground - a lawn, paving, gravel, planting - by
/// its corners, or a path along its middle so wide; and a joist layout, by the outline its
/// joists fill. Click each corner; click the first again, or double-click, to close it.
/// </summary>
public partial class PlanView
{
    /// <summary>What the Region tool lays.</summary>
    public GroundSurface ActiveGroundSurface { get; set; } = GroundSurface.Lawn;

    /// <summary>Whether the Region tool draws a path along its middle, so wide, rather than an outline.</summary>
    public bool DrawingPath { get; set; }

    /// <summary>How wide a path the Region tool draws, mm: 900 is the least an approach to a door asks.</summary>
    public double ActivePathWidth { get; set; } = 1200;

    /// <summary>The joist type and spacing the Joists tool lays.</summary>
    public Guid ActiveJoistTypeId { get; set; }

    public double ActiveJoistSpacing { get; set; } = 400;

    private readonly List<Point2D> _outlinePoints = new();
    private Point2D? _outlineHover;

    private void OutlineHover(Point2D at)
    {
        _outlineHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Region or Joists tool: the next corner - or point along a path - and
    /// clicking the first again, or the last, closes or finishes it. Public so it can be driven from tests.
    /// </summary>
    public bool ClickOutline(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null && !Drafting) return false;

        var path = ActiveTool == PlanTool.Region && DrawingPath;
        if (!path && _outlinePoints.Count >= 3 && (at - _outlinePoints[0]).Length * PixelsPerMm < 8) return FinishOutline();
        if (_outlinePoints.Count >= (path ? 2 : 3) && (at - _outlinePoints[^1]).Length * PixelsPerMm < 8) return FinishOutline();

        _outlinePoints.Add(at);
        HintChanged?.Invoke(this, path
            ? "Click along the middle of the path, and double-click where it ends. Esc to stop."
            : _outlinePoints.Count < 3 ? "Click its next corner." : "Click its next corner, or its first to close it - or double-click. Esc to stop.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The outline clicked, made into a region of the ground or a joist layout. False with too few points.</summary>
    public bool FinishOutline()
    {
        if (Document is null) return false;
        var path = ActiveTool == PlanTool.Region && DrawingPath;
        if (_outlinePoints.Count < (path ? 2 : 3))
        {
            _outlinePoints.Clear();
            InvalidateVisual();
            return false;
        }

        var outline = path ? GroundRegions.Band(_outlinePoints, Math.Max(300, ActivePathWidth)).ToList() : _outlinePoints.ToList();
        var pathLine = _outlinePoints.ToList();
        _outlinePoints.Clear();
        if (ActiveTool == PlanTool.FilledRegion)
        {
            MakeFilledRegion(outline);
            InvalidateVisual();
            return true;
        }

        if (Polygon2D.Area(outline) < 10000)
        {
            HintChanged?.Invoke(this, "Too small to lay: draw it larger.");
            InvalidateVisual();
            return false;
        }

        if (ActiveTool == PlanTool.Mass)
        {
            if (MakeMass(outline) is null) HintChanged?.Invoke(this, "Too small for a mass: draw it bigger.");
        }
        else if (ActiveTool == PlanTool.Pad)
        {
            var pad = new SitePad { LevelId = ActiveLevelId, HeightOffset = ActivePadOffset, Batter = Math.Max(0, ActivePadBatter), Surface = ActivePadSurface };
            pad.SetBoundary(outline);
            Apply(new AddElementCommand(Document, pad, "Pad"));
            var (cut, fill) = GroundOnLevel is { } graded ? Terrains.CutAndFill(Document, graded) : (0, 0);
            HintChanged?.Invoke(this, GroundOnLevel is null
                ? "Pad drawn - but there is no ground on this level for it to be graded into: draw the ground with Toposurface, or Around Building."
                : $"Pad graded in: {Core.Units.SquareMetres(Polygon2D.Area(pad.Boundary), "0.0")} at {Core.Units.FormatLength(pad.HeightOffset)}; the ground's cut now {Core.Units.CubicMetres(cut, "0.0")}, fill {Core.Units.CubicMetres(fill, "0.0")}. {SitePads.Verdict(Document, pad)}. Draw another; Esc to stop.");
        }
        else if (ActiveTool == PlanTool.PropertyLine)
        {
            var line = new PropertyLine { LevelId = ActiveLevelId, Setback = Math.Max(0, ActiveSetback) };
            line.SetBoundary(outline);
            Apply(new AddElementCommand(Document, line, "Property Line"));
            HintChanged?.Invoke(this, $"Property line: {Core.Units.SquareMetres(Polygon2D.Area(line.Boundary), "0.0")}, {Core.Units.Metres(Polygon2D.Perimeter(line.Boundary), "0.00")} round; the building covers {PropertyLines.Coverage(Document, line) * 100:0.#}% of it. {PropertyLines.Verdict(Document, line)}.");
        }
        else if (ActiveTool == PlanTool.Joists)
        {
            var type = Document.FindType<BeamType>(ActiveJoistTypeId)
                       ?? Document.TypesOf<BeamType>().FirstOrDefault(candidate => candidate.Name.Contains("47x200", StringComparison.Ordinal))
                       ?? Document.TypesOf<BeamType>().FirstOrDefault();
            var system = new BeamSystem { LevelId = ActiveLevelId, TypeId = type?.Id ?? Guid.Empty, Spacing = Math.Max(100, ActiveJoistSpacing) };
            system.SetBoundary(outline);
            Apply(new AddElementCommand(Document, system, "Joist Layout"));
            HintChanged?.Invoke(this, $"Joists in: {BeamSystems.Joists(Document, system).Count} of {BeamSystems.TypeOf(Document, system).Name} at {(Core.Units.Imperial ? Core.Units.FeetAndInches(system.Spacing) : $"{system.Spacing:0}")}, spanning {Core.Units.Metres(BeamSystems.Joists(Document, system).Select(joist => joist.From.DistanceTo(joist.To)).DefaultIfEmpty(0).Max(), "0.00")} at most. Draw another; Esc to stop.");
        }
        else
        {
            var region = new GroundRegion { LevelId = ActiveLevelId, Surface = path && ActiveGroundSurface == GroundSurface.Lawn ? GroundSurface.Path : ActiveGroundSurface };
            if (path) region.SetPath(pathLine, Math.Max(300, ActivePathWidth));
            region.SetBoundary(outline);
            Apply(new AddElementCommand(Document, region, "Ground Region"));
            HintChanged?.Invoke(this, GroundRegions.Host(Document, region) is null
                ? "Region laid - but not on any ground on this level: draw the ground with Toposurface first."
                : $"{Core.Parameters.EnumText.Humanise(region.Surface)} laid: {Core.Units.SquareMetres(Polygon2D.Area(region.Boundary), "0.0")}, at its steepest {(GroundRegions.Steepest(Document, region) is { } steepest && steepest > 1e-6 ? $"1:{1 / steepest:0.#}" : "level")}. Draw another; Esc to stop.");
        }

        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with an outline begun: forgotten.</summary>
    private bool CancelOutline()
    {
        if (_outlinePoints.Count == 0) return false;
        _outlinePoints.Clear();
        InvalidateVisual();
        return true;
    }

    /// <summary>The outline or path being drawn, on to the cursor.</summary>
    private void DrawOutlinePreview(DrawingContext dc)
    {
        if (ActiveTool is not (PlanTool.Region or PlanTool.Joists or PlanTool.Pad or PlanTool.PropertyLine or PlanTool.Mass or PlanTool.FilledRegion)) return;
        if (ActiveTool == PlanTool.Mass && ActiveMassFootprint != MassFootprint.Outline) return;

        var points = _outlineHover is { } next ? _outlinePoints.Append(next).ToList() : _outlinePoints.ToList();
        foreach (var (a, b) in points.Zip(points.Skip(1))) dc.DrawLine(SketchPreviewPen, ModelToScreen(a), ModelToScreen(b));
        if (ActiveTool == PlanTool.Region && DrawingPath && points.Count >= 2)
            dc.DrawGeometry(null, SketchPreviewPen, Outline(GroundRegions.Band(points, Math.Max(300, ActivePathWidth))));
        else if (_outlinePoints.Count >= 3)
            dc.DrawEllipse(null, SketchPreviewPen, ModelToScreen(_outlinePoints[0]), 6, 6);
    }

    /// <summary>The regions of the ground under a point of this plan.</summary>
    private IEnumerable<GroundRegion> RegionsAt(Point2D model) =>
        OnActiveLevel<GroundRegion>().Where(region => region.Boundary.Count >= 3 && Polygon2D.Contains(region.Boundary, model));

    /// <summary>The joist layouts a point of this plan is on: on their outline, or one of their joists.</summary>
    private IEnumerable<BeamSystem> JoistLayoutsAt(Point2D model)
    {
        if (Document is null) yield break;
        var reach = 5 / PixelsPerMm;
        foreach (var system in OnActiveLevel<BeamSystem>())
        {
            // Nowhere near it: its joists not worked out.
            var outline = system.Boundary;
            if (outline.Count < 3 || model.X < outline.Min(p => p.X) - reach || model.X > outline.Max(p => p.X) + reach
                || model.Y < outline.Min(p => p.Y) - reach || model.Y > outline.Max(p => p.Y) + reach) continue;
            var onEdge = Enumerable.Range(0, outline.Count).Any(i => Line2D.DistanceFromSegment(model, outline[i], outline[(i + 1) % outline.Count]) <= reach);
            if (onEdge || BeamSystems.Joists(Document, system).Any(joist => Line2D.DistanceFromSegment(model, joist.From, joist.To) <= reach)) yield return system;
        }
    }
}
