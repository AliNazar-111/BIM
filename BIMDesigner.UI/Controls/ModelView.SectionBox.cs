using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The section box in the 3D view: drawn round the part of the model shown, with a handle on
/// each face to pull it in or push it out - the model cut and closed again as it goes - and
/// the drag recorded as one step to undo when it is let go.
/// </summary>
public partial class ModelView
{
    private const string BoxGrip = "box:";
    private View3DState? _boxBefore;
    private bool _boxPending;

    private static readonly Pen BoxPen = FrozenPen(Color.FromRgb(0x1F, 0x6F, 0xD1), 1.2);

    private static Pen FrozenPen(Color colour, double width)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var pen = new Pen(brush, width);
        pen.Freeze();
        return pen;
    }

    /// <summary>Raised when the view has been changed here - its section box dragged - with the step to record.</summary>
    public event EventHandler<SetView3DCommand>? ViewEdited;

    /// <summary>The box round everything shown, with room to spare: where a section box starts.</summary>
    public Box3D? ModelBox => SectionBoxes.Around(_allMeshes.Where(_view.Shows));

    /// <summary>The box round some elements, with room to spare: Revit's Selection Box.</summary>
    public Box3D? BoxAround(IReadOnlyCollection<Guid> elements) =>
        SectionBoxes.Around(_allMeshes.Where(mesh => elements.Contains(mesh.ElementId) || elements.Contains(mesh.OwnerId)));

    /// <summary>A handle in the middle of each face of the section box, while it is on.</summary>
    private IEnumerable<ModelGrip> BoxGrips()
    {
        if (_elevation is not null || _view.ActiveBox is not { } box) yield break;

        for (var face = 0; face < 6; face++)
        {
            var axis = face / 2;
            var at = Box3D.With(box.Centre, axis, Box3D.Along(face % 2 == 1 ? box.Max : box.Min, axis));
            yield return new ModelGrip($"{BoxGrip}{face}", Guid.Empty, at, axis == 2, axis == 2 ? "↕" : "↔");
        }
    }

    private static bool IsBoxGrip(string? key) => key is not null && key.StartsWith(BoxGrip, StringComparison.Ordinal);

    /// <summary>A face of the box pulled to where the line of sight through the cursor passes closest to the line it moves along.</summary>
    private void DragBoxFace(string key, CorePoint3D from, CorePoint3D through)
    {
        if (_view.SectionBox is not { } box || !int.TryParse(key[BoxGrip.Length..], out var face)) return;
        _boxBefore ??= View3DState.Of(_view);

        var axis = face / 2;
        var high = face % 2 == 1;
        var grip = Box3D.With(box.Centre, axis, Box3D.Along(high ? box.Max : box.Min, axis));

        // The nearest points of two lines: the face's own line, and the line of sight.
        var u = Box3D.With(new CorePoint3D(0, 0, 0), axis, 1);
        var v = new CorePoint3D(through.X - from.X, through.Y - from.Y, through.Z - from.Z);
        var w = new CorePoint3D(grip.X - from.X, grip.Y - from.Y, grip.Z - from.Z);
        static double Dot(CorePoint3D a, CorePoint3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        var (b, c, d, e) = (Dot(u, v), Dot(v, v), Dot(u, w), Dot(v, w));
        var denominator = c - b * b;
        if (Math.Abs(denominator) < 1e-9 * c) return;

        var along = Box3D.Along(grip, axis) + (b * e - c * d) / denominator;
        _view.SectionBox = high
            ? box with { Max = Box3D.With(box.Max, axis, Math.Max(along, Box3D.Along(box.Min, axis) + SectionBoxes.Smallest)) }
            : box with { Min = Box3D.With(box.Min, axis, Math.Min(along, Box3D.Along(box.Max, axis) - SectionBoxes.Smallest)) };

        // Cut again once a frame, not on every move of the mouse.
        if (_boxPending) return;
        _boxPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _boxPending = false;
            ApplySectionBox();
            Compose();
        });
    }

    /// <summary>The drag of a face let go: recorded, as one step.</summary>
    private void EndBoxDrag()
    {
        if (_boxBefore is not { } before) return;
        _boxBefore = null;
        var command = new SetView3DCommand(_view, before, "Section Box");
        if (command.Changes) ViewEdited?.Invoke(this, command);
    }

    /// <summary>The box's twelve edges, over the model.</summary>
    private void DrawSectionBox(DrawingContext dc)
    {
        if (_elevation is not null || _walking || _playing is not null || _view.ActiveBox is not { } box) return;

        var corners = new CorePoint3D[8];
        for (var i = 0; i < 8; i++)
            corners[i] = new CorePoint3D((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);

        for (var i = 0; i < 8; i++)
            foreach (var bit in new[] { 1, 2, 4 })
            {
                if ((i & bit) != 0) continue;
                if (ScreenOf(corners[i]) is { } a && ScreenOf(corners[i | bit]) is { } b) dc.DrawLine(BoxPen, a, b);
            }
    }
}
