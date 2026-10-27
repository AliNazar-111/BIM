using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Room separators on the plan: click where one starts and where it ends - a line rooms are
/// bounded by where no wall is - and the next starts where it ended, a chain of them, until Esc.
/// </summary>
public partial class PlanView
{
    private Point2D? _separatorStart;
    private Point2D? _separatorHover;

    private void RoomSeparatorHover(Point2D at)
    {
        _separatorHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Room Separator tool: where a line starts, then where it ends - and the next
    /// starts there. Public so it can be driven from tests.
    /// </summary>
    public bool ClickRoomSeparator(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_separatorStart is not { } start)
        {
            _separatorStart = at;
            HintChanged?.Invoke(this, "Now click where it ends: the next starts there. Esc to stop.");
            InvalidateVisual();
            return true;
        }

        if ((at - start).Length < 10) return false;

        var separator = new RoomSeparator { LevelId = ActiveLevelId, Start = start, End = at };
        Apply(new AddElementCommand(Document, separator, "Room Separator"));
        _separatorStart = at;
        HintChanged?.Invoke(this, $"Room separator in, {Core.Units.Metres(separator.Length, "0.00")}: the rooms either side are bounded by it. Click where the next ends; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Esc with a room separator begun: its start forgotten.</summary>
    private bool CancelRoomSeparator()
    {
        if (_separatorStart is null) return false;
        _separatorStart = null;
        InvalidateVisual();
        return true;
    }

    /// <summary>The room separator the next click would put in, from where it starts to the cursor.</summary>
    private void DrawRoomSeparatorPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.RoomSeparator || _separatorStart is not { } start || _separatorHover is not { } hover || (hover - start).Length < 1) return;
        dc.DrawLine(SketchPreviewPen, ModelToScreen(start), ModelToScreen(hover));
    }

    /// <summary>The room separators on this level under a point.</summary>
    private IEnumerable<RoomSeparator> RoomSeparatorsAt(Point2D model) =>
        OnActiveLevel<RoomSeparator>().Where(separator => Line2D.DistanceFromSegment(model, separator.Start, separator.End) <= 5 / PixelsPerMm);
}
