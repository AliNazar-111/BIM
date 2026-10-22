using System.Windows.Media;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Elements drawn as their view's phasing and filters have them: faded where existing or a filter
/// fades them, in a filter's colour, or - demolished - in red, dashed and unfilled. Each is drawn as
/// it always is, into a drawing of its own, and that drawing restyled: so every kind of element
/// takes the overrides without each drawing them for itself.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>How the view draws each element beyond how it is drawn anyway; plain unless set.</summary>
    public Func<Element, ElementStyle> Style { get; set; } = _ => default;

    /// <summary>How far a faded element shows: a little under half.</summary>
    private const double HalftoneOpacity = 0.4;

    private void Styled(DrawingContext dc, Element element, Action<DrawingContext> draw)
    {
        // Drawn on its own first: one that cannot be drawn is left out - and said so - with
        // whatever it had got as far as, rather than the rest of the plan with it.
        var drawn = new DrawingGroup();
        bool done;
        using (var inner = drawn.Open()) done = DrawFailures.Guard(element, () => draw(inner));
        if (!done) return;

        var style = Style(element);
        if (style.IsPlain)
        {
            dc.DrawDrawing(drawn);
            return;
        }

        // A copy to restyle: what is drawn can be a drawing kept from before, frozen.
        var group = drawn.CloneCurrentValue();
        Restyle(group, style);
        if (style.Halftone) group.Opacity = HalftoneOpacity;
        dc.DrawDrawing(group);
    }

    private static void Restyle(Drawing drawing, ElementStyle style)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                foreach (var child in group.Children) Restyle(child, style);
                break;

            case GeometryDrawing shape:
                if (shape.Pen is { } pen)
                {
                    var restyled = pen.CloneCurrentValue();
                    if (style.Colour is { } colour) restyled.Brush = Brush(colour);
                    if (style.Dashed) restyled.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
                    shape.Pen = restyled;
                }

                // Taken down: an outline, dashed, nothing filled. Coloured: its fill tinted to the colour.
                if (style.Dashed) shape.Brush = null;
                else if (style.Colour is { } tint && shape.Brush is SolidColorBrush fill)
                    shape.Brush = Brush(new Core.Materials.ColourRgb(
                        (byte)((fill.Color.R + tint.R * 2) / 3), (byte)((fill.Color.G + tint.G * 2) / 3), (byte)((fill.Color.B + tint.B * 2) / 3)), fill.Color.A);
                break;

            case GlyphRunDrawing text when style.Colour is { } colour:
                text.ForegroundBrush = Brush(colour);
                break;
        }
    }

    private static SolidColorBrush Brush(Core.Materials.ColourRgb colour, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
