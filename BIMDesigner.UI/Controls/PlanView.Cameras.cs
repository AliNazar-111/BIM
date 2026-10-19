using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Cameras and walkthroughs on the plan. The Camera tool takes where the eye stands and where
/// it looks, and makes a 3D view in perspective from there at eye height. The Walkthrough tool
/// takes the points of a path, double-clicked where it ends, for a camera to follow.
/// </summary>
public partial class PlanView
{
    private Point2D? _cameraEye;
    private readonly List<Point2D> _walkPoints = new();
    private Point2D? _cameraHover;

    private static readonly Pen WalkPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57))), 1.4) { DashStyle = DashStyles.Dash });
    private static readonly Brush WalkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)));

    /// <summary>Raised with the 3D view a camera has just made, to be opened.</summary>
    public event EventHandler<View3D>? CameraPlaced;

    /// <summary>Raised with a walkthrough just drawn, to be opened.</summary>
    public event EventHandler<Walkthrough>? WalkthroughPlaced;

    private void CameraHover(Point2D at)
    {
        _cameraHover = at;
        InvalidateVisual();
    }

    /// <summary>
    /// A click with the Camera tool: first where the eye stands, then where it looks - a 3D view
    /// in perspective, at a standing eye's height above this storey. Public so it can be driven from tests.
    /// </summary>
    public bool ClickCamera(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        if (_cameraEye is not { } eye)
        {
            _cameraEye = at;
            HintChanged?.Invoke(this, "Now click where the camera looks.");
            InvalidateVisual();
            return true;
        }

        if (eye.DistanceTo(at) * PixelsPerMm < 8) return false;
        _cameraEye = null;

        var view = Views3D.Camera(Document, ActiveLevelId, eye, at);
        Apply(new AddElementCommand(Document, view, "Camera"));
        HintChanged?.Invoke(this, $"{view.Name}: a perspective from {Views3D.EyeHeight:0} mm above the floor. It is in the Project Browser under 3D Views.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        CameraPlaced?.Invoke(this, view);
        return true;
    }

    /// <summary>A click with the Walkthrough tool: the next point of its path. Public so it can be driven from tests.</summary>
    public bool ClickWalkthrough(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;
        if (_walkPoints.Count > 0 && _walkPoints[^1].DistanceTo(at) * PixelsPerMm < 8) return FinishWalkthrough();

        _walkPoints.Add(at);
        HintChanged?.Invoke(this, _walkPoints.Count == 1
            ? "Click along the way the camera goes, and double-click where it ends."
            : $"{_walkPoints.Count} points. Click the next, double-click where it ends; Esc to stop.");
        InvalidateVisual();
        return true;
    }

    /// <summary>The path clicked along made a walkthrough. False with fewer than two points.</summary>
    public bool FinishWalkthrough()
    {
        if (Document is null || _walkPoints.Count < 2)
        {
            _walkPoints.Clear();
            return false;
        }

        var names = Document.Elements.OfType<Walkthrough>().Select(walk => walk.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (names.Contains($"Walkthrough {number}")) number++;

        var made = new Walkthrough { LevelId = ActiveLevelId, Name = $"Walkthrough {number}" };
        made.SetPath(_walkPoints);
        _walkPoints.Clear();
        Apply(new AddElementCommand(Document, made, "Walkthrough"));
        HintChanged?.Invoke(this, $"{made.Name}: {made.Length / 1000:0.0} m, {made.Frames} frames at {made.FramesPerSecond:0} a second. It is in the Project Browser under Walkthroughs.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        WalkthroughPlaced?.Invoke(this, made);
        return true;
    }

    private bool CancelCamera()
    {
        var was = _cameraEye is not null || _walkPoints.Count > 0;
        _cameraEye = null;
        _walkPoints.Clear();
        if (was) InvalidateVisual();
        return was;
    }

    /// <summary>The walkthroughs on this storey whose path passes near a point.</summary>
    private IEnumerable<Walkthrough> WalkthroughsAt(Point2D model)
    {
        var reach = 5 / PixelsPerMm;
        return OnActiveLevel<Walkthrough>().Where(walk =>
            Enumerable.Range(0, Math.Max(walk.Path.Count - 1, 0)).Any(i => DistanceToSegment(model, walk.Path[i], walk.Path[i + 1]) <= reach));
    }

    /// <summary>The walkthroughs on this storey, their paths dashed with an arrow at the end; and the camera or path being drawn.</summary>
    private void DrawCameras(DrawingContext dc)
    {
        foreach (var walk in OnActiveLevel<Walkthrough>())
        {
            var pen = _selection.Contains(walk) ? SketchSelectedPen : WalkPen;
            for (var i = 0; i + 1 < walk.Path.Count; i++)
                dc.DrawLine(pen, ModelToScreen(walk.Path[i]), ModelToScreen(walk.Path[i + 1]));
            if (walk.Path.Count >= 2) DrawCameraMark(dc, walk.Path[^2], walk.Path[^1], WalkBrush);
            if (walk.Path.Count >= 1) dc.DrawEllipse(WalkBrush, null, ModelToScreen(walk.Path[0]), 3.5, 3.5);
        }

        if (ActiveTool == PlanTool.Camera && _cameraEye is { } eye && _cameraHover is { } looking)
            DrawCameraMark(dc, eye, looking, WalkBrush);

        if (ActiveTool == PlanTool.Walkthrough && _walkPoints.Count > 0)
        {
            var points = _walkPoints.ToList();
            if (_cameraHover is { } next) points.Add(next);
            for (var i = 0; i + 1 < points.Count; i++)
                dc.DrawLine(SketchPreviewPen, ModelToScreen(points[i]), ModelToScreen(points[i + 1]));
        }
    }

    /// <summary>A camera: where it stands, and the spread of what it sees toward where it looks.</summary>
    private void DrawCameraMark(DrawingContext dc, Point2D eye, Point2D toward, Brush brush)
    {
        var at = ModelToScreen(eye);
        var to = ModelToScreen(toward);
        var look = to - at;
        if (look.Length < 1) return;
        look.Normalize();

        var spread = Math.Tan(25 * Math.PI / 180);
        var side = new Vector(-look.Y, look.X) * spread * 28;
        var pen = Frozen(new Pen(brush, 1.2));
        dc.DrawLine(pen, at, at + look * 28 + side);
        dc.DrawLine(pen, at, at + look * 28 - side);
        dc.DrawLine(SketchPreviewPen, at, to);
        dc.DrawEllipse(brush, null, at, 4, 4);
    }
}
