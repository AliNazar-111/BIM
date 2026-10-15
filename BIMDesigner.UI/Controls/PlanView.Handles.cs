using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The handles on a selected railing, beam, piece of ground, region or joist layout, in plan and
/// in 3D: a railing's corners and a "+" midway along each length to add one; a beam's ends, and
/// in 3D arrows over them to raise each; the ground's spot levels, moved in plan and raised in
/// 3D; a region's or a joist layout's corners and "+"s. A corner dropped on the next goes; each
/// drag is one step to undo.
/// </summary>
public partial class PlanView
{
    private Element? _handleElement;
    private string? _handleKey;
    private object? _handleBefore;

    /// <summary>What is being dragged by a handle, whose own points are no snap for it.</summary>
    private Element? _snapIgnore;

    /// <summary>The selection when it is exactly one element with handles of this kind, whatever tool the plan has.</summary>
    private Element? OnlyHandled => _selection.Count == 1 && ElementHandles.Has(_selection[0]) && Document is not null ? _selection[0] : null;

    /// <summary>The handles shown in plan: on the selected element with the Select tool, on this level, those dragged across.</summary>
    private IEnumerable<ElementHandle> PlanHandles() =>
        ActiveTool == PlanTool.Select && OnlyHandled is { } element && element.LevelId == ActiveLevelId
            ? ElementHandles.Of(Document!, element).Where(handle => !handle.Vertical)
            : Enumerable.Empty<ElementHandle>();

    private ElementHandle? HandleAt(Point2D model)
    {
        var reach = (GripPixelRadius + 2) / PixelsPerMm;
        return PlanHandles().Where(handle => handle.At.DistanceTo(model) <= reach)
            .OrderBy(handle => handle.Shape == HandleShape.Insert ? 1 : 0).ThenBy(handle => handle.At.DistanceTo(model)).FirstOrDefault();
    }

    /// <summary>A press on a handle of the selected element: picked up to drag.</summary>
    private bool BeginHandle(Point2D raw)
    {
        if (OnlyHandled is not { } element || HandleAt(raw) is not { } handle) return false;
        if (RefusePinned(new[] { element }, "reshaped")) return true;

        (_handleElement, _handleKey, _handleBefore) = (element, handle.Key, ElementHandles.Snapshot(element));
        _dragging = GripKind.Handle;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        return true;
    }

    /// <summary>
    /// A whole drag of one of the selected element's handles to a point, as the mouse does it,
    /// in one go. Public so it can be driven from tests.
    /// </summary>
    public bool DragHandle(string key, Point2D to)
    {
        if (Document is null || OnlyHandled is not { } element) return false;
        if (ElementHandles.Of(Document, element).FirstOrDefault(handle => handle.Key == key) is null) return false;

        (_handleElement, _handleKey, _handleBefore) = (element, key, ElementHandles.Snapshot(element));
        FollowHandle(to);
        EndHandle();
        return true;
    }

    /// <summary>The handle being dragged, followed to the cursor - snapped to what corners and ends snap to.</summary>
    private void FollowHandle(Point2D raw)
    {
        if (Document is null || _handleElement is not { } element || _handleKey is not { } key) return;
        if (ElementHandles.Of(Document, element).FirstOrDefault(handle => handle.Key == key) is not { } handle) return;

        _snapIgnore = element;
        var to = element is Toposurface ? SnapToGrid(raw) : SnapPoint(raw, null, out _);
        _snapIgnore = null;
        var (said, follow) = ElementHandles.Drag(Document, element, handle, to, handle.Height);
        _handleKey = follow;
        HintChanged?.Invoke(this, said);
        InvalidateVisual();
    }

    /// <summary>The end of a handle's drag: what it left tidied, and what it did, as one step to undo.</summary>
    private void EndHandle()
    {
        if (_handleElement is not { } element || _handleBefore is not { } before || _handleKey is not { } key) return;
        (_handleElement, _handleBefore, _handleKey) = (null, null, null);

        // Not moved - pressed and let go: nothing to tidy, nothing done.
        if (ElementHandles.Same(before, ElementHandles.Snapshot(element))) return;

        // A beam's end dropped on its other end: no beam left - put back as it was.
        if (element is Beam { Length: < 1 })
        {
            ElementHandles.Restore(element, before);
            HintChanged?.Invoke(this, "A beam runs between two ends apart: its end is put back.");
            InvalidateVisual();
            return;
        }

        ElementHandles.Settle(element, key);
        var after = ElementHandles.Snapshot(element);
        if (ElementHandles.Same(before, after)) return;

        // Put back, and done again by the command, so what is recorded is what was done.
        ElementHandles.Restore(element, before);
        Apply(new HandleEditCommand(element, before, after, ElementHandles.Name(element, key)));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>The selected element's handles in plan: a square for a corner or an end, a "+" to add a corner.</summary>
    private void DrawHandles(DrawingContext dc)
    {
        if (_isPanning) return;

        foreach (var handle in PlanHandles())
        {
            var at = ModelToScreen(handle.At);
            if (handle.Shape == HandleShape.Insert)
            {
                dc.DrawEllipse(_gripBrush, _gripPen, at, 5, 5);
                dc.DrawLine(_gripPen, new System.Windows.Point(at.X - 3, at.Y), new System.Windows.Point(at.X + 3, at.Y));
                dc.DrawLine(_gripPen, new System.Windows.Point(at.X, at.Y - 3), new System.Windows.Point(at.X, at.Y + 3));
            }
            else if (handle.Shape == HandleShape.Bend) dc.DrawEllipse(_gripBrush, _gripPen, at, 5, 5);
            else dc.DrawRectangle(_gripBrush, _gripPen, new System.Windows.Rect(at.X - 4, at.Y - 4, 8, 8));
        }
    }

    // ---- in 3D ------------------------------------------------------------------

    /// <summary>The selected element's handles where the 3D view shows them: every one, those raised up and down too.</summary>
    public IReadOnlyList<ModelGrip> Handles3D() =>
        OnlyHandled is { } element
            ? ElementHandles.Of(Document!, element)
                .Select(handle => new ModelGrip($"handle:{handle.Key}", element.Id, new CorePoint3D(handle.At.X, handle.At.Y, handle.Height), handle.Vertical,
                    handle.Shape switch { HandleShape.Insert => "+", HandleShape.Raise => "▲", HandleShape.Bend => "●", _ => "■" }))
                .ToList()
            : Array.Empty<ModelGrip>();

    /// <summary>Every handle the 3D view shows: a stair's, or a railing's, a beam's, the ground's, a region's or a joist layout's.</summary>
    public IReadOnlyList<ModelGrip> Grips3D() => StairGrips3D().Concat(Handles3D()).ToList();

    /// <summary>
    /// A handle dragged in 3D: one moved across, over the plane at its height, to where the line
    /// of sight through the cursor meets it; one raised, up its own line to where that comes
    /// nearest the line of sight. True when what it is on changed. Public so it can be driven from tests.
    /// </summary>
    public bool DragHandleIn3D(string key, CorePoint3D from, CorePoint3D through)
    {
        ModelMayHaveChanged();
        if (Document is null || OnlyHandled is not { } element || !key.StartsWith("handle:", StringComparison.Ordinal)) return false;
        var own = key["handle:".Length..];
        if (ElementHandles.Of(Document, element).FirstOrDefault(handle => handle.Key == (_handleElement == element && _handleKey is { } following ? following : own)) is not { } handle) return false;

        var (dx, dy, dz) = (through.X - from.X, through.Y - from.Y, through.Z - from.Z);
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-9) return false;
        (dx, dy, dz) = (dx / length, dy / length, dz / length);

        Point2D to;
        var height = handle.Height;
        if (!handle.Vertical)
        {
            if (Math.Abs(dz) < 1e-6) return false;
            var along = (handle.Height - from.Z) / dz;
            if (along <= 0) return false;
            to = new Point2D(from.X + dx * along, from.Y + dy * along);
        }
        else
        {
            // Straight up through it, and where that comes nearest the line of sight.
            var (wx, wy, wz) = (handle.At.X - from.X, handle.At.Y - from.Y, handle.Height - from.Z);
            var b = dz;
            var (d, e) = (wz, dx * wx + dy * wy + dz * wz);
            var parallel = 1 - b * b;
            if (parallel < 1e-4) return false;
            var slide = (b * e - d) / parallel;
            (to, height) = (handle.At, handle.Height + slide);
        }

        if (_handleElement is null)
        {
            if (RefusePinned(new[] { element }, "reshaped")) return false;
            (_handleElement, _handleKey, _handleBefore) = (element, handle.Key, ElementHandles.Snapshot(element));
        }

        var before = ElementHandles.Snapshot(element);
        var (said, follow) = ElementHandles.Drag(Document, element, handle, to, height);
        _handleKey = follow;
        HintChanged?.Invoke(this, said);
        return !ElementHandles.Same(before, ElementHandles.Snapshot(element));
    }

    /// <summary>The end of a handle's drag in 3D: one step to undo.</summary>
    public void EndHandleIn3D() => EndHandle();
}
