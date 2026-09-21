using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The drawing sheet surface (specification section 6.5).
///
/// Millimetres here are millimetres of <em>paper</em>, which is the one place in the
/// application where they are not millimetres of building. A viewport is where the two meet,
/// and the scale is the conversion.
///
/// Nothing on a sheet is a picture. Every viewport asks the model for its view on every
/// repaint, through the same renderers the editors use, so a sheet is exactly as current as
/// the building is and cannot be left un-regenerated.
/// </summary>
public class SheetView : FrameworkElement
{
    private const double MinPixelsPerPaperMm = 0.15;
    private const double MaxPixelsPerPaperMm = 20.0;

    /// <summary>Paper-millimetre grab radius for picking up a viewport by its edge.</summary>
    private const double FramePickMm = 2.0;

    /// <summary>
    /// Draws the sheet. Shared with the exporter, so what is printed is what was checked.
    /// </summary>
    private readonly SheetRenderer _renderer = new();

    private readonly Brush _background;
    private readonly Brush _emptyBrush;

    private BimDocument? _document;
    private Sheet? _sheet;

    private bool _isPanning;
    private Point _panScreenAnchor;
    private Point2D _panCentreAnchor;

    private Viewport? _dragging;
    private Point2D _dragAnchor;
    private Point2D _dragOriginalCentre;

    /// <summary>Centre of the viewport, in paper millimetres from the sheet's bottom-left.</summary>
    private Point2D _centre = new(210, 148);

    private double _pixelsPerPaperMm = 1.6;

    public SheetView()
    {
        Focusable = true;
        ClipToBounds = true;

        _background = RenderPens.Fill(Color.FromRgb(0x10, 0x13, 0x18));
        _emptyBrush = RenderPens.Fill(Color.FromRgb(0x7A, 0x84, 0x92));
    }

    /// <summary>The project the sheet draws from. The same object every other view uses.</summary>
    public BimDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;

            _document = value;
            _renderer.InvalidateBrushes();
            SelectedViewport = null;
            InvalidateVisual();
        }
    }

    public Sheet? Sheet
    {
        get => _sheet;
        set
        {
            if (ReferenceEquals(_sheet, value)) return;

            _sheet = value;
            SelectedViewport = null;
            ZoomToFit();
        }
    }

    /// <summary>The viewport picked on the sheet, if any.</summary>
    public Viewport? SelectedViewport { get; private set; }

    public event EventHandler? SelectionChanged;

    /// <summary>Raised when a viewport has been dragged to a new place, once, on release.</summary>
    public event EventHandler<ViewportMovedEventArgs>? ViewportMoved;

    public void Refresh() => InvalidateVisual();

    public void Select(Viewport? viewport)
    {
        if (ReferenceEquals(SelectedViewport, viewport)) return;

        SelectedViewport = viewport;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_sheet is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _pixelsPerPaperMm = 1.6;
            _centre = new Point2D(210, 148);
            InvalidateVisual();
            return;
        }

        const double margin = 30;

        _pixelsPerPaperMm = Clamp(Math.Min(
            ActualWidth / (_sheet.Width + margin * 2),
            ActualHeight / (_sheet.Height + margin * 2)));

        _centre = new Point2D(_sheet.Width / 2, _sheet.Height / 2);
        InvalidateVisual();
    }

    // ---- coordinates ------------------------------------------------------------

    /// <summary>Paper millimetres to screen pixels. Paper Y is up, so it is flipped.</summary>
    private Point ToScreen(Point2D paper) => new(
        ActualWidth / 2 + (paper.X - _centre.X) * _pixelsPerPaperMm,
        ActualHeight / 2 - (paper.Y - _centre.Y) * _pixelsPerPaperMm);

    private Point2D ToPaper(Point screen) => new(
        _centre.X + (screen.X - ActualWidth / 2) / _pixelsPerPaperMm,
        _centre.Y - (screen.Y - ActualHeight / 2) / _pixelsPerPaperMm);

    private Rect ToScreen(BoundingBox2D paper)
    {
        var topLeft = ToScreen(new Point2D(paper.MinX, paper.MaxY));
        var bottomRight = ToScreen(new Point2D(paper.MaxX, paper.MinY));
        return new Rect(topLeft, bottomRight);
    }

    private static double Clamp(double pixelsPerPaperMm) =>
        Math.Clamp(pixelsPerPaperMm, MinPixelsPerPaperMm, MaxPixelsPerPaperMm);

    // ---- input ------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) return;

        // Zoom about the cursor, so what is under it stays under it.
        var anchor = e.GetPosition(this);
        var before = ToPaper(anchor);

        _pixelsPerPaperMm = Clamp(_pixelsPerPaperMm * Math.Pow(1.0015, e.Delta));

        var after = ToPaper(anchor);
        _centre = new Point2D(_centre.X + (before.X - after.X), _centre.Y + (before.Y - after.Y));

        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;

        _isPanning = true;
        _panScreenAnchor = e.GetPosition(this);
        _panCentreAnchor = _centre;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (_document is null || _sheet is null) return;

        var paper = ToPaper(e.GetPosition(this));
        var hit = HitTest(paper);

        Select(hit);

        if (hit is null) return;

        _dragging = hit;
        _dragAnchor = paper;
        _dragOriginalCentre = hit.Centre;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isPanning)
        {
            var now = e.GetPosition(this);
            _centre = new Point2D(
                _panCentreAnchor.X - (now.X - _panScreenAnchor.X) / _pixelsPerPaperMm,
                _panCentreAnchor.Y + (now.Y - _panScreenAnchor.Y) / _pixelsPerPaperMm);

            InvalidateVisual();
            return;
        }

        if (_dragging is null)
        {
            Cursor = _document is not null && HitTest(ToPaper(e.GetPosition(this))) is not null
                ? Cursors.SizeAll
                : Cursors.Arrow;

            return;
        }

        // Write the move straight to the model so the sheet updates live; the undo entry is
        // recorded once on release, so a drag is one step rather than hundreds.
        var delta = ToPaper(e.GetPosition(this)) - _dragAnchor;
        var desired = new Point2D(
            Math.Round(_dragOriginalCentre.X + delta.X),
            Math.Round(_dragOriginalCentre.Y + delta.Y));

        // The sheet decides where it may actually go: inside the drawing area, and not through
        // another view. Against either it slides rather than stopping dead.
        if (_sheet is not null && _document is not null)
            _dragging.Centre = _sheet.ConstrainMove(_document, _dragging, desired);

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        EndGesture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndGesture();
    }

    private void EndGesture()
    {
        if (_isPanning)
        {
            _isPanning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            return;
        }

        if (_dragging is null) return;

        var moved = _dragging;
        var from = _dragOriginalCentre;
        var to = moved.Centre;

        _dragging = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;

        if (from == to) return;

        ViewportMoved?.Invoke(this, new ViewportMovedEventArgs(moved, from, to));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key != Key.F) return;

        ZoomToFit();
        e.Handled = true;
    }

    /// <summary>
    /// The viewport under a point on the paper, topmost first - the last placed is the one on
    /// top, so it is the one a click means.
    /// </summary>
    private Viewport? HitTest(Point2D paper)
    {
        if (_document is null || _sheet is null) return null;

        for (var i = _sheet.Viewports.Count - 1; i >= 0; i--)
            if (_sheet.Viewports[i].Contains(_document, paper)) return _sheet.Viewports[i];

        return null;
    }

    // ---- rendering --------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(_background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_sheet is null || _document is null)
        {
            DrawMessage(dc, "No sheet open. Create one from the Sheets menu, or pick one in the browser.");
            return;
        }

        // The sheet is drawn by the shared renderer, so what is on screen and what comes out
        // of the printer are the same drawing rather than two renderings of one layout.
        _renderer.Document = _document;
        _renderer.Sheet = _sheet;
        _renderer.SelectedViewport = SelectedViewport;
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.ShowGuides = true;
        _renderer.SetTransform(ToScreen, _pixelsPerPaperMm);
        _renderer.DrawContent(dc);
    }

    private void DrawMessage(DrawingContext dc, string message)
    {
        var text = Text(message, 12, _emptyBrush);
        dc.DrawText(text, new Point(
            Math.Max(12, (ActualWidth - text.Width) / 2),
            ActualHeight / 2 - text.Height / 2));
    }

    private FormattedText Text(string value, double size, Brush brush) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        Math.Max(1, size),
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

public sealed class ViewportMovedEventArgs : EventArgs
{
    public ViewportMovedEventArgs(Viewport viewport, Point2D from, Point2D to)
    {
        Viewport = viewport;
        From = from;
        To = to;
    }

    public Viewport Viewport { get; }
    public Point2D From { get; }
    public Point2D To { get; }
}
