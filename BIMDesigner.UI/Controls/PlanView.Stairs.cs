using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Stairs on the plan: click where its first riser goes, then which way is up - it lays itself
/// out from there to the level above, straight or turning as the option bar says, its risers
/// worked out from the storey's height, and cuts its opening in the floor over it.
/// </summary>
public partial class PlanView
{
    /// <summary>The stair type the Stair tool puts in.</summary>
    public Guid ActiveStairTypeId { get; set; }

    /// <summary>How the next stair is laid out.</summary>
    public StairShape NewStairShape { get; set; }

    public StairCorner NewStairCorner { get; set; }

    public bool NewStairTurnsLeft { get; set; }

    public double NewStairWidth { get; set; } = 900;

    private Point2D? _stairStart;
    private Point2D? _stairHover;

    private void StairHover(Point2D at)
    {
        _stairHover = at;
        InvalidateVisual();
    }

    /// <summary>A stair from a foot toward a point: up from this storey to the one above, as the option bar says.</summary>
    private Stair NewStair(Point2D start, Point2D toward)
    {
        var way = toward - start;
        // A house's stair, timber with closed risers, unless another is picked.
        var type = Document?.FindType<StairType>(ActiveStairTypeId)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault(candidate => candidate.Construction == StairConstruction.Timber && !candidate.OpenRisers)
                   ?? Document?.TypesOf<StairType>().FirstOrDefault();
        return new Stair
        {
            LevelId = ActiveLevelId,
            TopLevelId = Document is null ? null : Levels.Above(Document, ActiveLevelId)?.Id,
            TypeId = type?.Id ?? Guid.Empty,
            Start = start,
            Angle = way.Length < 1 ? 90 : Math.Atan2(way.Y, way.X) * 180 / Math.PI,
            Shape = NewStairShape,
            Corner = NewStairCorner,
            TurnsLeft = NewStairTurnsLeft,
            Width = NewStairWidth
        };
    }

    /// <summary>A click with the Stair tool: its foot, then which way is up. Public so it can be driven from tests.</summary>
    public bool ClickStair(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_stairStart is not { } start)
        {
            _stairStart = at;
            HintChanged?.Invoke(this, "Now click which way is up: the stair runs that way from here, up to the level above.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;

        var stair = NewStair(start, at);
        _stairStart = null;
        Apply(new AddElementCommand(Document, stair, "Stair"));

        var layout = Stairs.Layout(Document, stair);
        HintChanged?.Invoke(this, $"Stair in: {layout.Risers} risers of {layout.Rise:0.#}, going {layout.Going:0}, pitch {layout.Pitch:0.#}°. {Stairs.Verdict(Document, stair)}. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a stair begun: its foot forgotten.</summary>
    private bool CancelStair()
    {
        if (_stairStart is null) return false;
        _stairStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The stair the next click would put in, following the cursor: its steps and the line walked up it.</summary>
    private void DrawStairPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Stair || Document is null) return;

        if (_stairStart is not { } start)
        {
            if (_stairHover is { } hover)
            {
                var arm = 8.0;
                var at = ModelToScreen(hover);
                dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X - arm, at.Y), new System.Windows.Point(at.X + arm, at.Y));
                dc.DrawLine(SketchPreviewPen, new System.Windows.Point(at.X, at.Y - arm), new System.Windows.Point(at.X, at.Y + arm));
            }

            return;
        }

        var toward = _stairHover ?? start + new Vector2D(0, 1000);
        var stair = NewStair(start, toward);
        var layout = Stairs.Layout(Document, stair);
        foreach (var step in layout.Steps) dc.DrawGeometry(null, SketchPreviewPen, Outline(step.Outline));
        for (var i = 0; i + 1 < layout.WalkingLine.Count; i++)
            dc.DrawLine(SketchPreviewPen, ModelToScreen(layout.WalkingLine[i]), ModelToScreen(layout.WalkingLine[i + 1]));
    }

    /// <summary>The stairs under a point of this plan: on their own storey, or seen down the stairwell from the one they come up to.</summary>
    private IEnumerable<Stair> StairsAt(Point2D model)
    {
        if (Document is null) yield break;

        foreach (var stair in Document.Elements.OfType<Stair>().Where(stair => (stair.LevelId == ActiveLevelId || stair.TopLevelId == ActiveLevelId) && _shows(stair)))
            if (Stairs.Layout(Document, stair).Steps.Any(step => Polygon2D.Contains(step.Outline, model)))
                yield return stair;
    }
}
