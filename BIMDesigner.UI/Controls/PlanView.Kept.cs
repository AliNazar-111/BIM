using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The drawing of the model kept between repaints. Hovering, picking and panning change nothing
/// in the model, so the storey below and this one are drawn again only when something that is
/// drawn has changed: an edit made or undone, an element added or taken away, a refresh asked
/// for, the zoom, the storey, the selection, how the plan is shown. Panned, the drawing kept is
/// moved with the view; while something is dragged it is made afresh each time, as it changes.
/// </summary>
public partial class PlanView
{
    /// <summary>Counted up whenever the model may have changed, so the drawing kept of it is made again.</summary>
    private int _modelVersion;

    /// <summary>The drawing of the model last made, what it was made for, and where the model's origin was on the screen then.</summary>
    private (string Key, Point Origin, DrawingGroup Drawing)? _kept;

    private UndoStackListener? _historyListener;

    /// <summary>Whether something has been dragged since all was last worked out - what was kept of the rest then is to go.</summary>
    private bool _dragDirty;

    /// <summary>What has been worked out of the model while it has not changed, shared by this storey's drawing and the one under it.</summary>
    private readonly Rendering.ModelMemo _memo = new();

    /// <summary>That the model may have changed: its drawing is made again at the next repaint, and all worked out of it again.</summary>
    private void ModelMayHaveChanged()
    {
        _modelVersion++;
        _kept = null;
        _memo.Clear();
    }

    /// <summary>What the drawing of the model depends on, besides where the view is panned to.</summary>
    private string ContentKey()
    {
        var document = Document!;
        var view = CurrentView;
        return string.Join("|",
            _modelVersion, document.Elements.Count, document.Levels.Count, ActiveLevelId, PixelsPerMm.ToString("R"), DetailLevel, ShowUnderlay,
            _renderer.ShowWallLabels, _sketchRoof?.Id, VisualTreeHelper.GetDpi(this).PixelsPerDip, document.ViewSettings.Version,
            document.ProjectInformation.ClearanceStandard, GroundColouring, string.Join(",", _selection.Select(element => element.Id)));
    }

    /// <summary>
    /// The model - the storey below, faintly, then this one - drawn as it was last time where
    /// nothing drawn has changed since, moved to where the view is panned to; otherwise drawn again.
    /// </summary>
    private void DrawModel(DrawingContext dc)
    {
        var key = ContentKey();
        var origin = ModelToScreen(new Point2D(0, 0));

        // Something being dragged - a grip, a handle, a stair - changes the model as it goes: drawn
        // as it is, each time - what is worked out of what is dragged worked out again, the rest
        // kept till it is let go, when all is.
        var dragged = IsMouseCaptured && !_isPanning;
        if (dragged)
        {
            _memo.Forget(_selection.Select(element => element.Id).Concat(_handleElement is { } handled ? new[] { handled.Id } : Array.Empty<Guid>()));
            _dragDirty = true;
        }
        else if (_dragDirty)
        {
            // Let go: whatever it moved, all of it worked out again.
            _dragDirty = false;
            _memo.Clear();
            _kept = null;
        }
        (_renderer.Memo, _underlayRenderer.Memo) = (_memo, _memo);
        PrepareRenderer();
        if (!dragged && _kept is { } kept && kept.Key == key)
        {
            dc.PushTransform(new TranslateTransform(origin.X - kept.Origin.X, origin.Y - kept.Origin.Y));
            dc.DrawDrawing(kept.Drawing);
            dc.Pop();
            return;
        }

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            DrawUnderlay(context);
            PrepareRenderer();
            _renderer.DrawContent(context);
        }

        if (drawing.CanFreeze) drawing.Freeze();
        dc.DrawDrawing(drawing);
        _kept = dragged ? null : (key, origin, drawing);
    }

    /// <summary>Listens to an edit history for the model changing through it: done, undone, redone.</summary>
    private sealed class UndoStackListener
    {
        private readonly Core.Documents.Commands.UndoStack _history;
        private readonly EventHandler _changed;

        public UndoStackListener(Core.Documents.Commands.UndoStack history, Action changed)
        {
            _history = history;
            _changed = (_, _) => changed();
            _history.Changed += _changed;
        }

        public void Stop() => _history.Changed -= _changed;
    }
}
