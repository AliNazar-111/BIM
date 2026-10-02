using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Chimneys on the plan: click where the stack goes, and it rises from this storey up through
/// the roof over it, as far above the roof as the height rule asks, built as its type is built.
/// Put by a wall, its back goes to the wall and its fireplace opens into the room.
/// </summary>
public partial class PlanView
{
    /// <summary>The chimney type the Chimney tool puts up.</summary>
    public Guid ActiveChimneyTypeId { get; set; }

    /// <summary>What the Chimney tool puts at a new chimney's foot; its type's own when not set.</summary>
    public ChimneyFireplace? ActiveChimneyFireplace { get; set; }

    private Point2D? _chimneyHover;

    private void ChimneyHover(Point2D at)
    {
        _chimneyHover = at;
        InvalidateVisual();
    }

    private Chimney NewChimney(Guid levelId, Point2D at)
    {
        var type = Document?.FindType<ChimneyType>(ActiveChimneyTypeId) ?? Document?.TypesOf<ChimneyType>().FirstOrDefault()
            ?? ChimneyType.Library()[0];
        var chimney = new Chimney
        {
            LevelId = levelId, Location = at, TypeId = Document?.FindType<ChimneyType>(type.Id)?.Id ?? Guid.Empty,
            Width = type.Width, Depth = type.IsRound ? type.Width : type.Depth, Flues = type.Flues,
            Fireplace = ActiveChimneyFireplace ?? type.Fireplace
        };
        if (Document is not null) Chimneys.FitToWalls(Document, chimney);
        return chimney;
    }

    /// <summary>A click with the Chimney tool: a stack from this storey up through the roof. Public so it can be driven from tests.</summary>
    public bool PlaceChimneyAt(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;
        return PlaceChimney(NewChimney(ActiveLevelId, at));
    }

    /// <summary>A click on a roof in 3D with the Chimney tool: a stack there, from the roof's storey up through it.</summary>
    public bool PlaceChimneyIn3D(Guid roofId, Point3D at) =>
        Document?.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == roofId) is { IsExtrusion: false } roof &&
        PlaceChimney(NewChimney(roof.LevelId, new Point2D(at.X, at.Y)));

    private bool PlaceChimney(Chimney chimney)
    {
        if (Document is null) return false;

        Apply(new AddElementCommand(Document, chimney, "Chimney"));

        var above = Chimneys.HighestContact(Document, chimney) is { } contact
            ? $"{Core.Units.FormatLength(Chimneys.Top(Document, chimney) - contact)} above where it meets the roof"
            : "standing free: there is no roof over it";
        HintChanged?.Invoke(this, $"Chimney up through the roof, {above}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The stack the next click would put up, following the cursor.</summary>
    private void DrawChimneyPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Chimney || _chimneyHover is not { } hover || Document is null) return;

        var chimney = NewChimney(ActiveLevelId, hover);
        dc.DrawGeometry(null, SketchPreviewPen, Outline(Chimneys.Footprint(Document, chimney, ActiveLevelId)));
        if (Chimneys.Breast(Document, chimney) is { } breast)
        {
            dc.DrawGeometry(null, SketchPreviewPen, Outline(breast.Opening));
            dc.DrawGeometry(null, SketchPreviewPen, Outline(breast.Hearth));
        }

        var radius = Math.Max(20, Chimneys.TypeOf(Document, chimney).FlueDiameter / 2) * PixelsPerMm;
        foreach (var flue in Chimneys.Flues(Document, chimney))
            dc.DrawEllipse(null, SketchPreviewPen, ModelToScreen(flue), radius, radius);
    }

    /// <summary>
    /// Why a nudge cannot move a chimney - into a wall it was clear of, when the walls are not
    /// moving with it - or null when it can.
    /// </summary>
    private string? ChimneyStepProblem(IEnumerable<Core.Elements.Element> moving, Vector2D step)
    {
        if (Document is null) return null;

        var set = moving.ToList();
        if (set.OfType<Wall>().Any()) return null;

        foreach (var chimney in set.OfType<Chimney>())
        {
            var before = Chimneys.WallsItIsIn(Document, chimney).Count;
            chimney.Location += step;
            var after = Chimneys.WallsItIsIn(Document, chimney).Count;
            chimney.Location -= step;
            if (after > before) return "A chimney stops at the wall's face: it stands in the room, not in the wall.";
        }

        return null;
    }

    /// <summary>
    /// Copies about to be put down - pasted, arrayed, mirrored - set right against the walls
    /// that are there: out of a wall, and turned round where their fireplace faces one. Not when
    /// walls are among them: a chimney copied with its walls keeps its place among them.
    /// </summary>
    private void FitLoneChimneys(IReadOnlyList<Core.Elements.Element> copies)
    {
        if (Document is null || copies.OfType<Wall>().Any()) return;
        FitChimneysCommand.For(Document, copies.OfType<Chimney>());
    }

    /// <summary>The chimneys under a point of this plan.</summary>
    private IEnumerable<Chimney> ChimneysAt(Point2D model) =>
        OnActiveLevel<Chimney>().Where(chimney => Document is not null && Polygon2D.Contains(Chimneys.Footprint(Document, chimney, ActiveLevelId), model));
}
