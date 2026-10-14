using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The handles on a selected stair, in plan and in 3D: arrows on its sides to widen it, a dot
/// where its first flight ends to turn it a riser sooner or later, an arrow on a half turn's
/// second flight to widen the well, arrows on a spiral's or curve's inside and outside, a
/// handle in front of its foot to turn it round, and one beside it to flip it - and in 3D,
/// its top and bottom to drag up and down. Each drag is one step to undo.
/// </summary>
public partial class PlanView
{
    private Stair? _gripStair;
    private StairGripKind _stairGrip;
    private int _stairGripIndex;
    private StairState? _stairBefore;

    /// <summary>The selection when it is exactly one stair, which is what stair handles act on - in plan, with the Select tool.</summary>
    private Stair? SelectedStair => ActiveTool == PlanTool.Select ? OnlySelectedStair : null;

    /// <summary>The selection when it is exactly one stair, whatever tool the plan has: what 3D shows handles on, a stair clicked there being selected whatever the tool.</summary>
    private Stair? OnlySelectedStair => _selection.Count == 1 && _selection[0] is Stair stair && Document is not null ? stair : null;

    /// <summary>The handles a selected stair shows in plan: all but its top and bottom, which are dragged in 3D.</summary>
    private IEnumerable<StairGrip> PlanStairGrips() =>
        SelectedStair is { } stair && stair.LevelId == ActiveLevelId
            ? StairGrips.Of(Document!, stair).Where(grip => !grip.Vertical)
            : Enumerable.Empty<StairGrip>();

    private StairGrip? StairGripAt(Point2D model)
    {
        var reach = (GripPixelRadius + 2) / PixelsPerMm;
        return PlanStairGrips().Where(grip => grip.At.DistanceTo(model) <= reach).OrderBy(grip => grip.At.DistanceTo(model)).FirstOrDefault();
    }

    /// <summary>A press on a handle of the selected stair: flips it, or picks the handle up to drag.</summary>
    private bool BeginStairGrip(Point2D raw)
    {
        if (SelectedStair is not { } stair || StairGripAt(raw) is not { } grip) return false;

        if (grip.Clicked)
        {
            FlipStair(stair);
            return true;
        }

        (_gripStair, _stairGrip, _stairGripIndex, _stairBefore) = (stair, grip.Kind, grip.Index, StairState.Of(stair));
        _dragging = GripKind.Stair;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        return true;
    }

    /// <summary>
    /// A whole drag of one of the selected stair's handles, to a point - or a click on its flip
    /// button - as the mouse does it, in one go. Public so it can be driven from tests.
    /// </summary>
    public bool DragStairGrip(StairGripKind kind, Point2D to, int index = 0)
    {
        if (Document is null || SelectedStair is not { } stair) return false;
        if (PlanStairGrips().FirstOrDefault(grip => grip.Kind == kind && grip.Index == index) is not { } grip) return false;

        if (grip.Clicked)
        {
            FlipStair(stair);
            return true;
        }

        (_gripStair, _stairGrip, _stairGripIndex, _stairBefore) = (stair, kind, index, StairState.Of(stair));
        DragStairGrip(to);
        EndStairGrip();
        return true;
    }

    /// <summary>The handle being dragged, followed to the cursor: the stair re-planned as it goes.</summary>
    private void DragStairGrip(Point2D raw)
    {
        if (Document is null || _gripStair is not { } stair) return;
        if (StairGrips.Of(Document, stair).FirstOrDefault(grip => grip.Kind == _stairGrip && grip.Index == _stairGripIndex) is not { } grip) return;

        // Its regulations are checked when it is let go: worked out on every move, they would make the drag lag.
        HintChanged?.Invoke(this, StairGrips.Drag(Document, stair, grip, raw, 0, check: false));
        InvalidateVisual();
    }

    /// <summary>The end of a handle's drag: what it did, as one step to undo.</summary>
    private void EndStairGrip()
    {
        if (_gripStair is not { } stair || _stairBefore is not { } before) return;
        (_gripStair, _stairBefore) = (null, null);

        var after = StairState.Of(stair);
        if (after == before) return;

        // Put back, and done again by the command, so what is recorded is what was done.
        before.ApplyTo(stair);
        Apply(new StairEditCommand(stair, before, after, StairGrips.Name(_stairGrip)));
        if (Document is not null) HintChanged?.Invoke(this, StairGrips.Summary(Document, stair));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void FlipStair(Stair stair)
    {
        if (Document is null) return;

        var before = StairState.Of(stair);
        var said = StairGrips.Flip(Document, stair);
        var after = StairState.Of(stair);
        before.ApplyTo(stair);
        Apply(new StairEditCommand(stair, before, after, StairGrips.Name(StairGripKind.Flip)));

        HintChanged?.Invoke(this, $"{said} {Stairs.Verdict(Document, stair)}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// The selected stair's handles: an arrow for each that is dragged one way, a dot where it
    /// turns, and round buttons to turn it and to flip it.
    /// </summary>
    private void DrawStairGrips(DrawingContext dc)
    {
        if (_isPanning) return;

        foreach (var grip in PlanStairGrips())
        {
            var at = ModelToScreen(grip.At);
            switch (grip.Kind)
            {
                case StairGripKind.Turn or StairGripKind.PathPoint:
                    dc.DrawEllipse(_gripBrush, _gripPen, at, 5, 5);
                    break;

                case StairGripKind.Rotate or StairGripKind.Flip:
                    dc.DrawEllipse(_gripBrush, _gripPen, at, 9, 9);
                    var glyph = new FormattedText(grip.Kind == StairGripKind.Rotate ? "↻" : "⇄", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Symbol"), 11, _gripPen.Brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(glyph, new Point(at.X - glyph.Width / 2, at.Y - glyph.Height / 2));
                    break;

                default:
                    dc.DrawGeometry(_gripBrush, _gripPen, Arrowhead(at, ModelToScreen(grip.At + grip.Axis * 100) - at));
                    break;
            }
        }
    }

    /// <summary>A small arrowhead at a point, pointing a way on the screen.</summary>
    private static Geometry Arrowhead(Point at, Vector way)
    {
        if (way.Length < 1e-9) way = new Vector(1, 0);
        way.Normalize();
        var across = new Vector(-way.Y, way.X);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(at + way * 8, true, true);
            ctx.PolyLineTo(new[] { at - way * 4 + across * 6, at - way * 4 - across * 6 }, true, false);
        }

        head.Freeze();
        return head;
    }

    // ---- in 3D ------------------------------------------------------------------

    /// <summary>The selected stair's handles where the 3D view shows them: every one, top and bottom too.</summary>
    public IReadOnlyList<ModelGrip> StairGrips3D() =>
        OnlySelectedStair is { } stair
            ? StairGrips.Of(Document!, stair)
                .Select(grip => new ModelGrip(grip.Kind == StairGripKind.PathPoint ? $"stair:{grip.Kind}:{grip.Index}" : $"stair:{grip.Kind}", stair.Id, new CorePoint3D(grip.At.X, grip.At.Y, grip.Height), grip.Vertical,
                    grip.Kind switch
                    {
                        StairGripKind.Rotate => "↻",
                        StairGripKind.Flip => "⇄",
                        StairGripKind.Top => "▲",
                        StairGripKind.Bottom => "▼",
                        StairGripKind.Turn or StairGripKind.PathPoint => "●",
                        _ => "◆"
                    }))
                .ToList()
            : Array.Empty<ModelGrip>();

    /// <summary>
    /// A stair's handle dragged in 3D. Each handle slides along its own line - across the stair
    /// for its sides, up it for its turn, straight up for its top and foot - to where that line
    /// comes nearest the line of sight through the cursor, so it follows the mouse steadily from
    /// any angle; the one that turns it moves over the floor at its foot. True when the stair
    /// changed. Public so it can be driven from tests.
    /// </summary>
    public bool DragStairGripIn3D(string key, CorePoint3D from, CorePoint3D through)
    {
        if (Document is null || OnlySelectedStair is not { } stair || !key.StartsWith("stair:", StringComparison.Ordinal)) return false;
        var parts = key.Split(':');
        if (!Enum.TryParse<StairGripKind>(parts[1], out var kind)) return false;
        var index = parts.Length > 2 && int.TryParse(parts[2], out var at) ? at : 0;
        if (StairGrips.Of(Document, stair).FirstOrDefault(grip => grip.Kind == kind && grip.Index == index) is not { } grip || grip.Clicked) return false;

        var (dx, dy, dz) = (through.X - from.X, through.Y - from.Y, through.Z - from.Z);
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-9) return false;
        (dx, dy, dz) = (dx / length, dy / length, dz / length);

        Point2D to;
        var height = grip.Height;
        if (kind is StairGripKind.Rotate or StairGripKind.PathPoint)
        {
            // Over the floor at its foot, or the height of the turn.
            if (Math.Abs(dz) < 1e-6) return false;
            var along = (grip.Height - from.Z) / dz;
            if (along <= 0) return false;
            to = new Point2D(from.X + dx * along, from.Y + dy * along);
        }
        else
        {
            // The handle's line, and where it comes nearest the line of sight.
            var (ux, uy, uz) = grip.Vertical ? (0.0, 0.0, 1.0) : (grip.Axis.X, grip.Axis.Y, 0.0);
            var size = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (size < 1e-9) return false;
            (ux, uy, uz) = (ux / size, uy / size, uz / size);

            var (wx, wy, wz) = (grip.At.X - from.X, grip.At.Y - from.Y, grip.Height - from.Z);
            var b = ux * dx + uy * dy + uz * dz;
            var (d, e) = (ux * wx + uy * wy + uz * wz, dx * wx + dy * wy + dz * wz);
            var parallel = 1 - b * b;
            if (parallel < 1e-4) return false;
            var slide = (b * e - d) / parallel;
            if ((e - b * d) / parallel <= 0) return false;
            (to, height) = (grip.At + new Vector2D(ux, uy) * slide, grip.Height + uz * slide);
        }

        if (_gripStair is null) (_gripStair, _stairGrip, _stairGripIndex, _stairBefore) = (stair, kind, index, StairState.Of(stair));
        var before = StairState.Of(stair);
        HintChanged?.Invoke(this, StairGrips.Drag(Document, stair, grip, to, height, check: false));
        return StairState.Of(stair) != before;
    }

    /// <summary>The end of a stair handle's drag in 3D: one step to undo.</summary>
    public void EndStairGripIn3D() => EndStairGrip();

    /// <summary>A stair's flip handle clicked in 3D. True when it was one.</summary>
    public bool ClickStairGripIn3D(string key)
    {
        if (OnlySelectedStair is not { } stair || key != $"stair:{StairGripKind.Flip}") return false;
        FlipStair(stair);
        return true;
    }
}
