using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The section drawing surface (specification section 6.1).
///
/// Like <see cref="PlanView"/> it draws the model directly rather than from XAML controls,
/// but it draws a <em>vertical</em> slice: X is distance along the cut line, Y is the true
/// elevation above the project base point.
///
/// It owns no geometry. Every repaint asks <see cref="SectionProjection"/> to cut the model
/// again, so the section cannot fall behind the plan - there is no regenerate step to forget
/// and nothing that could be showing last week's building.
/// </summary>
public class SectionView : FrameworkElement
{
    private const double MinPixelsPerMm = 0.002;
    private const double MaxPixelsPerMm = 2.0;

    /// <summary>
    /// Draws the cut. Shared with the sheet view, so a section in a viewport is this drawing
    /// rather than a second rendering of the same cut.
    /// </summary>
    private readonly SectionRenderer _renderer = new();

    private readonly Brush _titleBrush;
    private readonly Brush _emptyBrush;
    private readonly Brush _background;

    private BimDocument? _document;
    private SectionMarker? _marker;
    private SectionDrawing? _drawing;

    private bool _isPanning;
    private Point _panScreenAnchor;
    private Point _panViewAnchor;

    /// <summary>Millimetres at the centre of the viewport: along the cut, and in elevation.</summary>
    private Point _centre = new(0, 1500);

    private double _pixelsPerMm = 0.05;

    public SectionView()
    {
        Focusable = true;
        ClipToBounds = true;

        _background = RenderPens.Fill(AppTheme.Pick(Colors.White, Color.FromRgb(0x14, 0x17, 0x1D)));
        _titleBrush = RenderPens.Fill(AppTheme.Pick(Color.FromRgb(0x8A, 0x42, 0x16), Color.FromRgb(0xF2, 0xC0, 0x9E)));
        _emptyBrush = RenderPens.Fill(Color.FromRgb(0x7A, 0x84, 0x92));
    }

    /// <summary>The project being cut. The same object the plan draws - never a copy.</summary>
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

    /// <summary>Which section is shown. Null means no section has been opened.</summary>
    public SectionMarker? Marker
    {
        get => _marker;
        set
        {
            if (ReferenceEquals(_marker, value)) return;
            _marker = value;
            Rebuild();
            ZoomToFit();
        }
    }

    /// <summary>The current cut. Recut on every <see cref="Rebuild"/>, never cached longer.</summary>
    public SectionDrawing? Drawing => _drawing;

    /// <summary>
    /// The element picked in the section, kept in step with the plan's selection so the two
    /// views are two views of one model rather than two drawings.
    /// </summary>
    public Element? SelectedElement { get; private set; }

    public event EventHandler? SelectionChanged;

    /// <summary>Recuts the model and repaints. Called whenever anything might have changed.</summary>
    public void Rebuild()
    {
        _drawing = _document is not null && _marker is not null
            ? SectionProjection.Build(_document, _marker, _document.ViewSettings.FilterFor(_document, ViewReference.Section(_marker.Id)))
            : null;

        InvalidateVisual();
    }

    /// <summary>Everything highlighted, which follows the plan's whole selection.</summary>
    private IReadOnlyList<Element> _highlighted = Array.Empty<Element>();

    public void Select(Element? element)
    {
        if (ReferenceEquals(SelectedElement, element)) return;

        SelectedElement = element;
        _highlighted = element is null ? Array.Empty<Element>() : new[] { element };

        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Highlights a selection made elsewhere, without raising the event - which is what stops
    /// the plan and the section bouncing a selection back and forth between them.
    /// </summary>
    public void SetSelectionQuietly(IReadOnlyList<Element> elements)
    {
        _highlighted = elements.ToList();
        SelectedElement = elements.Count == 0 ? null : elements[^1];
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_drawing is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _pixelsPerMm = 0.05;
            _centre = new Point(0, 1500);
            InvalidateVisual();
            return;
        }

        var bottom = Math.Min(_drawing.MinElevation, 0);
        var top = Math.Max(_drawing.MaxElevation, bottom + 1000);

        // The level lines run past the building at both ends, and their names are written
        // beyond the right-hand end: all of it in view, the names too.
        const double marginMm = 1200;
        const double labelPixels = 170, edgePixels = 12;
        var overhang = Math.Max(1500, _drawing.Width * 0.04);
        var width = Math.Max(_drawing.Width, 1000) + overhang * 2;
        var height = top - bottom + marginMm * 2;

        _pixelsPerMm = Clamp(Math.Min(Math.Max(ActualWidth - labelPixels - edgePixels * 2, 50) / width, ActualHeight / height));
        _centre = new Point(_drawing.Width / 2 + labelPixels / 2 / _pixelsPerMm, (bottom + top) / 2);

        InvalidateVisual();
    }

    // ---- coordinates ------------------------------------------------------------

    /// <summary>Section millimetres to screen pixels. Elevation is up, so Y is flipped.</summary>
    private Point ToScreen(double along, double elevation) => new(
        ActualWidth / 2 + (along - _centre.X) * _pixelsPerMm,
        ActualHeight / 2 - (elevation - _centre.Y) * _pixelsPerMm);

    private Point ToSection(Point screen) => new(
        _centre.X + (screen.X - ActualWidth / 2) / _pixelsPerMm,
        _centre.Y - (screen.Y - ActualHeight / 2) / _pixelsPerMm);

    private Rect ToScreen(SectionRect bounds)
    {
        var topLeft = ToScreen(bounds.Left, bounds.Top);
        var bottomRight = ToScreen(bounds.Right, bounds.Bottom);
        return new Rect(topLeft, bottomRight);
    }

    private static double Clamp(double pixelsPerMm) =>
        Math.Clamp(pixelsPerMm, MinPixelsPerMm, MaxPixelsPerMm);

    // ---- input ------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0) return;

        // Zoom about the cursor, so what is under it stays under it.
        var anchor = e.GetPosition(this);
        var before = ToSection(anchor);

        _pixelsPerMm = Clamp(_pixelsPerMm * Math.Pow(1.0015, e.Delta));

        var after = ToSection(anchor);
        _centre = new Point(_centre.X + (before.X - after.X), _centre.Y + (before.Y - after.Y));

        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;

        _isPanning = true;
        _panScreenAnchor = e.GetPosition(this);
        _panViewAnchor = _centre;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isPanning) return;

        var now = e.GetPosition(this);
        _centre = new Point(
            _panViewAnchor.X - (now.X - _panScreenAnchor.X) / _pixelsPerMm,
            _panViewAnchor.Y + (now.Y - _panScreenAnchor.Y) / _pixelsPerMm);

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isPanning) return;

        _isPanning = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        if (_drawing is null || _document is null) return;

        var point = ToSection(e.GetPosition(this));
        Select(HitTest(point.X, point.Y));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F)
        {
            ZoomToFit();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Picks the element under a point. Cut pieces win over seen ones: the cut is what the
    /// drawing is about, and it is drawn on top, so it must be picked from on top.
    /// </summary>
    private Element? HitTest(double along, double elevation)
    {
        if (_drawing is null || _document is null) return null;

        for (var i = _drawing.Pieces.Count - 1; i >= 0; i--)
        {
            var bounds = _drawing.Pieces[i].Bounds;

            if (along >= bounds.Left && along <= bounds.Right &&
                elevation >= bounds.Bottom && elevation <= bounds.Top)
            {
                return _document.Elements.FirstOrDefault(element => element.Id == _drawing.Pieces[i].ElementId);
            }
        }

        return null;
    }

    // ---- rendering --------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        dc.DrawRectangle(_background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_marker is null)
        {
            DrawMessage(dc, "No section open. Draw one with the Section tool, or pick a marker on the plan.");
            return;
        }

        if (_drawing is null) return;

        _renderer.SetSelection(_highlighted);
        _renderer.PixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderer.SetTransform(ToScreen);
        _renderer.DrawContent(dc, _drawing);

        if (_drawing.IsEmpty)
            DrawMessage(dc, "Nothing stands on this line. Move the marker so it crosses the building.");

        DrawTitle(dc);
    }

    private void DrawTitle(DrawingContext dc)
    {
        if (_drawing is null || _marker is null) return;

        // The same zoom baseline the plan reports, so the two views' numbers mean the same.
        var label = $"Section {_drawing.Name}    {_pixelsPerMm / 0.05 * 100:0}%";

        var text = Text(label, 13, _titleBrush);
        dc.DrawText(text, new Point(12, ActualHeight - text.Height - 10));
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
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

}
