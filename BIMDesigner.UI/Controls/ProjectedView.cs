using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// A view drawn flat from the model - an interior elevation - in hidden line, with its levels and
/// the crop round its room. Zoomed with the wheel, panned with a drag. The same drawing a sheet
/// shows, so the view on screen and on paper cannot disagree.
/// </summary>
public class ProjectedView : FrameworkElement
{
    private readonly ProjectionRenderer _renderer = new() { ShowCrop = true };
    private readonly Brush _background = RenderPens.Fill(AppTheme.Pick(Colors.White, Color.FromRgb(0x14, 0x17, 0x1D)));
    private readonly Brush _titleBrush = RenderPens.Fill(AppTheme.Pick(Color.FromRgb(0x8A, 0x42, 0x16), Color.FromRgb(0xF2, 0xC0, 0x9E)));

    private BimDocument? _document;
    private ViewReference? _view;
    private ProjectedDrawing? _drawing;

    private Point _centre;
    private double _pixelsPerMm = 0.05;
    private bool _panning;
    private Point _panFrom;
    private Point _centreFrom;

    public ProjectedView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public BimDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            _document = value;
            Rebuild();
        }
    }

    /// <summary>The view shown: an interior elevation.</summary>
    public ViewReference? View
    {
        get => _view;
        set
        {
            _view = value;
            Rebuild();
            ZoomToFit();
        }
    }

    /// <summary>Projects the view again, and repaints.</summary>
    public void Rebuild()
    {
        _drawing = _document is not null && _view is { } view && view.ExistsIn(_document) ? Projections.Of(_document, view) : null;
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_drawing is not { Extent.IsEmpty: false } drawing || ActualWidth <= 0 || ActualHeight <= 0) return;
        var extent = drawing.Extent.Expand(600);
        _centre = new Point(extent.Centre.X, extent.Centre.Y);
        _pixelsPerMm = Math.Clamp(Math.Min(ActualWidth / extent.Width, (ActualHeight - 30) / extent.Height), 0.002, 2);
        InvalidateVisual();
    }

    private Point ToScreen(double x, double y) =>
        new(ActualWidth / 2 + (x - _centre.X) * _pixelsPerMm, ActualHeight / 2 - (y - _centre.Y) * _pixelsPerMm);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.PreviousSize.Width <= 0) ZoomToFit();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(_background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_drawing is null || _document is null || _view is not { } view) return;

        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.SetTransform(ToScreen);
        _renderer.DrawContent(dc, _drawing);

        var title = new FormattedText(view.TitleIn(_document), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 13, _titleBrush, _renderer.PixelsPerDip);
        dc.DrawText(title, new Point(12, 8));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var at = e.GetPosition(this);
        var before = new Point(_centre.X + (at.X - ActualWidth / 2) / _pixelsPerMm, _centre.Y - (at.Y - ActualHeight / 2) / _pixelsPerMm);
        _pixelsPerMm = Math.Clamp(_pixelsPerMm * Math.Pow(1.0015, e.Delta), 0.002, 2);

        // The point under the cursor stays under it.
        _centre = new Point(before.X - (at.X - ActualWidth / 2) / _pixelsPerMm, before.Y + (at.Y - ActualHeight / 2) / _pixelsPerMm);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        (_panning, _panFrom, _centreFrom) = (true, e.GetPosition(this), _centre);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_panning) return;
        var moved = e.GetPosition(this) - _panFrom;
        _centre = new Point(_centreFrom.X - moved.X / _pixelsPerMm, _centreFrom.Y + moved.Y / _pixelsPerMm);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        _panning = false;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.F) return;
        ZoomToFit();
        e.Handled = true;
    }
}
