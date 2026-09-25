using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>What the next shape drawn on the sketch will be.</summary>
public enum SketchTool
{
    Rectangle,
    Circle,
    Polygon,
    Freeform
}

/// <summary>
/// The drawing board of the column editor: a cross-section seen square on, with shapes drawn on
/// it and added to, cut out of, or kept only where they overlap what is already there.
///
/// It edits one thing - a <see cref="ColumnProfile"/> - and every gesture ends in a boolean
/// against it, so there is no separate list of "cuts" to keep in step with the shape. What you
/// see is the profile, and the profile is what the column is made of.
///
/// The view belongs to the person, not to the drawing: it zooms and pans where they put it and
/// stays there. A board that refit itself to the shape after every cut would move the work under
/// the hand doing it, which is the one thing a drawing surface must never do.
/// </summary>
public sealed class ProfileSketch : Control
{
    private readonly Pen _grid;
    private readonly Pen _gridMajor;
    private readonly Pen _axis;
    private readonly Pen _outline;
    private readonly Pen _rubber;
    private readonly Brush _fill;
    private readonly Brush _label;

    private Point2D? _dragFrom;
    private Point2D _cursor;
    private readonly List<Point2D> _sketchPoints = new();

    private Point? _panFrom;
    private Point2D _panCentreAt;

    private readonly Stack<ColumnProfile> _undo = new();
    private readonly Stack<ColumnProfile> _redo = new();

    public ProfileSketch()
    {
        var ink = AppTheme.Drawing;

        _grid = RenderPens.Solid(ink.LayerSeparator, 0.5);
        _gridMajor = RenderPens.Solid(ink.LayerSeparator, 1.1);
        _axis = RenderPens.Solid(ink.LocationLine, 1.0);
        _outline = RenderPens.Solid(ink.WallOutline, 1.6);
        _rubber = RenderPens.Dashed(ink.Preview, 1.4, 4, 3);
        _fill = RenderPens.Fill(ink.ComponentFill);
        _label = RenderPens.Fill(ink.RoomTagText);

        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The section being drawn. Never null; an empty one draws as nothing.</summary>
    public ColumnProfile Profile { get; private set; } = ColumnProfile.Rectangle(400, 400);

    public SketchTool Tool { get; set; } = SketchTool.Rectangle;

    /// <summary>What a drawn shape does to the profile: add to it, cut it, or keep the overlap.</summary>
    public BooleanOperation Operation { get; set; } = BooleanOperation.Difference;

    /// <summary>How many sides the polygon tool draws.</summary>
    public int PolygonSides { get; set; } = 6;

    /// <summary>What the cursor snaps to, in millimetres. Nothing at or below zero.</summary>
    public double SnapStep { get; set; } = 10;

    public bool SnapOn => SnapStep > 0;

    /// <summary>Screen pixels to a millimetre. The person's, and it stays where they put it.</summary>
    public double Scale { get; private set; } = 0.5;

    /// <summary>Where the middle of the board is, in the section's own coordinates.</summary>
    public Point2D Centre { get; private set; } = new(0, 0);

    public double ScalePercent => Scale * 100;

    /// <summary>Raised whenever the profile changes, so the 3D preview and the readouts follow.</summary>
    public event EventHandler? ProfileChanged;

    /// <summary>Raised as the cursor moves and as a shape is dragged out, for the readouts.</summary>
    public event EventHandler<SketchStatus>? StatusChanged;

    /// <summary>Where the cursor is, and how big the shape being dragged is, if one is.</summary>
    public readonly record struct SketchStatus(Point2D At, double Width, double Depth, bool Drawing);

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Load(ColumnProfile profile)
    {
        Profile = profile;
        _undo.Clear();
        _redo.Clear();
        _sketchPoints.Clear();
        _dragFrom = null;

        Fit();
        Raise();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;

        _redo.Push(Profile);
        Profile = _undo.Pop();
        Raise();
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;

        _undo.Push(Profile);
        Profile = _redo.Pop();
        Raise();
    }

    /// <summary>Replaces the section with one of the shapes a column starts from.</summary>
    public void Reset(ColumnProfile profile) => Apply(profile);

    public void Turn(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        Apply(Profile.Transformed(p => new Point2D(p.X * cos - p.Y * sin, p.X * sin + p.Y * cos)));
    }

    public void MirrorAcross() => Apply(Profile.Transformed(p => new Point2D(-p.X, p.Y)));

    public void MirrorUp() => Apply(Profile.Transformed(p => new Point2D(p.X, -p.Y)));

    public void Nudge(double x, double y) => Apply(Profile.Transformed(p => new Point2D(p.X + x, p.Y + y)));

    public void ResizeTo(double width, double depth) => Apply(Profile.ResizedTo(width, depth));

    public void RoundCorners(double size, bool round) =>
        Apply(round ? Profile.Rounded(size) : Profile.Chamfered(size));

    /// <summary>
    /// Centres the section on the column's own centre, which is where a column stands and what
    /// it turns about. Drawing by eye leaves it off to one side.
    /// </summary>
    public void CentreOnOrigin()
    {
        if (Profile.IsEmpty) return;

        var left = Profile.Outer.Min(p => p.X);
        var right = Profile.Outer.Max(p => p.X);
        var bottom = Profile.Outer.Min(p => p.Y);
        var top = Profile.Outer.Max(p => p.Y);

        Nudge(-(left + right) / 2, -(bottom + top) / 2);
    }

    /// <summary>
    /// Adds a shape of an exact size at an exact place - what the boxes on the toolbar do. This
    /// is how a section is drawn to a dimension rather than to wherever the mouse happened to be.
    /// </summary>
    public void AddShape(double width, double depth, Point2D centre)
    {
        if (width <= 0 || depth <= 0) return;

        var shape = Built(width, depth, centre);
        if (shape.Count >= 3) Apply(Profile.Combine(shape, Operation));
    }

    /// <summary>Takes out the hole under a point, for undoing one cut without undoing the rest.</summary>
    public bool RemoveHoleAt(Point2D at)
    {
        var hole = Profile.Holes.FirstOrDefault(loop => Polygon2D.Contains(loop, at));
        if (hole is null) return false;

        Apply(new ColumnProfile(Profile.Outer, Profile.Holes.Where(loop => loop != hole).ToList()));
        return true;
    }

    private void Apply(ColumnProfile profile)
    {
        _undo.Push(Profile);
        _redo.Clear();
        Profile = profile;
        Raise();
    }

    private void Raise()
    {
        ProfileChanged?.Invoke(this, EventArgs.Empty);
        Report();
        InvalidateVisual();
    }

    private void Report()
    {
        var width = _dragFrom is { } from ? Math.Abs(_cursor.X - from.X) : 0;
        var depth = _dragFrom is { } corner ? Math.Abs(_cursor.Y - corner.Y) : 0;

        StatusChanged?.Invoke(this, new SketchStatus(_cursor, width, depth, _dragFrom is not null));
    }

    // ---- the view --------------------------------------------------------------

    /// <summary>Puts the whole section on the board with room round it.</summary>
    public void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var (width, depth) = Profile.IsEmpty ? (600.0, 600.0) : Profile.Extent;
        var span = Math.Max(Math.Max(width, depth), 200) * 1.5;

        Scale = Math.Min(ActualWidth, ActualHeight) / span;
        Centre = Profile.IsEmpty
            ? new Point2D(0, 0)
            : new Point2D(
                (Profile.Outer.Min(p => p.X) + Profile.Outer.Max(p => p.X)) / 2,
                (Profile.Outer.Min(p => p.Y) + Profile.Outer.Max(p => p.Y)) / 2);

        InvalidateVisual();
    }

    /// <summary>Zooms about a point on the board, so what is under the cursor stays under it.</summary>
    public void ZoomAt(Point screen, double factor)
    {
        var before = ToModel(screen);
        Scale = Math.Clamp(Scale * factor, 0.02, 20);

        var after = ToModel(screen);
        Centre = new Point2D(Centre.X + (before.X - after.X), Centre.Y + (before.Y - after.Y));

        InvalidateVisual();
    }

    /// <summary>Zooms about the middle, which is what the buttons on the toolbar do.</summary>
    public void ZoomBy(double factor) => ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);

    private Point ToScreen(Point2D point) => new(
        ActualWidth / 2 + (point.X - Centre.X) * Scale,
        ActualHeight / 2 - (point.Y - Centre.Y) * Scale);

    private Point2D ToModel(Point screen) => new(
        Centre.X + (screen.X - ActualWidth / 2) / Scale,
        Centre.Y + (ActualHeight / 2 - screen.Y) / Scale);

    private Point2D Snap(Point2D point) => SnapOn
        ? new Point2D(Math.Round(point.X / SnapStep) * SnapStep, Math.Round(point.Y / SnapStep) * SnapStep)
        : point;

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // The first time it has a size, put the section on it. After that the view is the
        // person's and resizing the window must not move their work.
        if (info.PreviousSize.Width <= 0 || info.PreviousSize.Height <= 0) Fit();
    }

    // ---- drawing shapes --------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var at = Snap(ToModel(e.GetPosition(this)));

        if (Tool == SketchTool.Freeform)
        {
            // A double click closes the shape. Its first press has already put a point down, so
            // that one comes off again rather than being left doubled up on the corner.
            if (e.ClickCount >= 2)
            {
                if (_sketchPoints.Count > 0) _sketchPoints.RemoveAt(_sketchPoints.Count - 1);
                CommitFreeform();
            }
            else
            {
                _sketchPoints.Add(at);
            }

            InvalidateVisual();
            return;
        }

        _dragFrom = at;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // Middle or right dragging pans, as it does in every other view in the application.
        if (_panFrom is { } start)
        {
            var now = e.GetPosition(this);
            Centre = new Point2D(
                _panCentreAt.X - (now.X - start.X) / Scale,
                _panCentreAt.Y + (now.Y - start.Y) / Scale);

            InvalidateVisual();
            return;
        }

        _cursor = Snap(ToModel(e.GetPosition(this)));
        Report();
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right)) return;

        _panFrom = e.GetPosition(this);
        _panCentreAt = Centre;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right)) return;

        _panFrom = null;
        Cursor = Cursors.Arrow;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        ZoomAt(e.GetPosition(this), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragFrom is not { } from) return;

        ReleaseMouseCapture();
        var to = Snap(ToModel(e.GetPosition(this)));
        _dragFrom = null;

        if (Shape(from, to) is { Count: >= 3 } shape) Apply(Profile.Combine(shape, Operation));

        Report();
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                _sketchPoints.Clear();
                _dragFrom = null;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.Enter or Key.Return when _sketchPoints.Count >= 3:
                CommitFreeform();
                e.Handled = true;
                break;

            case Key.Back when _sketchPoints.Count > 0:
                _sketchPoints.RemoveAt(_sketchPoints.Count - 1);
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    private void CommitFreeform()
    {
        if (_sketchPoints.Count >= 3) Apply(Profile.Combine(_sketchPoints.ToList(), Operation));

        _sketchPoints.Clear();
        InvalidateVisual();
    }

    /// <summary>The shape a drag from one corner to another makes with the tool in hand.</summary>
    private IReadOnlyList<Point2D>? Shape(Point2D from, Point2D to)
    {
        var width = Math.Abs(to.X - from.X);
        var depth = Math.Abs(to.Y - from.Y);
        if (width < 1 || depth < 1) return null;

        return Built(width, depth, new Point2D((from.X + to.X) / 2, (from.Y + to.Y) / 2));
    }

    /// <summary>The tool's shape at a size and a place.</summary>
    private IReadOnlyList<Point2D> Built(double width, double depth, Point2D centre)
    {
        var profile = Tool switch
        {
            SketchTool.Circle => ColumnProfile.Ellipse(width, depth),
            SketchTool.Polygon => ColumnProfile.Polygon(PolygonSides, width, depth),
            _ => ColumnProfile.Rectangle(width, depth)
        };

        return profile.Transformed(p => new Point2D(p.X + centre.X, p.Y + centre.Y)).Outer;
    }

    // ---- what is drawn ---------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(Background ?? Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        DrawGrid(dc);

        // The section: filled, with its holes left out - so a cut reads as a hole here exactly
        // as it will in the plan and in the 3D view.
        if (!Profile.IsEmpty)
        {
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = geometry.Open())
            {
                foreach (var loop in Profile.Loops) Figure(ctx, loop);
            }

            geometry.Freeze();
            dc.DrawGeometry(_fill, _outline, geometry);
        }

        DrawPending(dc);
    }

    private void DrawGrid(DrawingContext dc)
    {
        // A grid in round millimetres, coarse enough to read at whatever the board is zoomed to.
        var step = 10.0;
        while (step * Scale < 7) step *= 10;

        void Lines(double spacing, Pen pen)
        {
            if (spacing * Scale < 4) return;

            var left = ToModel(new Point(0, 0)).X;
            var right = ToModel(new Point(ActualWidth, 0)).X;
            var bottom = ToModel(new Point(0, ActualHeight)).Y;
            var top = ToModel(new Point(0, 0)).Y;

            for (var x = Math.Ceiling(left / spacing) * spacing; x <= right; x += spacing)
            {
                var at = ToScreen(new Point2D(x, 0)).X;
                dc.DrawLine(pen, new Point(at, 0), new Point(at, ActualHeight));
            }

            for (var y = Math.Ceiling(bottom / spacing) * spacing; y <= top; y += spacing)
            {
                var at = ToScreen(new Point2D(0, y)).Y;
                dc.DrawLine(pen, new Point(0, at), new Point(ActualWidth, at));
            }
        }

        Lines(step, _grid);
        Lines(step * 10, _gridMajor);

        // The centre, which is where the column stands.
        var origin = ToScreen(new Point2D(0, 0));
        dc.DrawLine(_axis, new Point(origin.X, 0), new Point(origin.X, ActualHeight));
        dc.DrawLine(_axis, new Point(0, origin.Y), new Point(ActualWidth, origin.Y));
    }

    private void DrawPending(DrawingContext dc)
    {
        if (_dragFrom is { } from && Shape(from, _cursor) is { Count: >= 3 } shape)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open()) Figure(ctx, shape);

            geometry.Freeze();
            dc.DrawGeometry(null, _rubber, geometry);

            // How big it is, beside the cursor: a shape drawn without knowing its size is a
            // shape that has to be measured afterwards.
            var width = Math.Abs(_cursor.X - from.X);
            var depth = Math.Abs(_cursor.Y - from.Y);
            Label(dc, $"{width:0} × {depth:0}", ToScreen(_cursor));
            return;
        }

        if (_sketchPoints.Count == 0) return;

        var trail = new StreamGeometry();
        using (var ctx = trail.Open())
        {
            ctx.BeginFigure(ToScreen(_sketchPoints[0]), false, false);
            ctx.PolyLineTo(_sketchPoints.Skip(1).Append(_cursor).Select(ToScreen).ToArray(), true, false);
        }

        trail.Freeze();
        dc.DrawGeometry(null, _rubber, trail);

        foreach (var point in _sketchPoints) dc.DrawEllipse(null, _rubber, ToScreen(point), 3, 3);

        // How long the piece being drawn is, so a freeform shape can be drawn to a size.
        Label(dc, $"{_sketchPoints[^1].DistanceTo(_cursor):0}", ToScreen(_cursor));
    }

    private void Label(DrawingContext dc, string text, Point at)
    {
        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, _label, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(formatted, new Point(at.X + 14, at.Y - 20));
    }

    private void Figure(StreamGeometryContext ctx, IReadOnlyList<Point2D> loop)
    {
        if (loop.Count < 2) return;

        ctx.BeginFigure(ToScreen(loop[0]), true, true);
        ctx.PolyLineTo(loop.Skip(1).Select(ToScreen).ToArray(), true, false);
    }
}
