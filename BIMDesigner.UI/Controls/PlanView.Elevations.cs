using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The elevation markers round the building on every plan, one off each side, its arrow
/// pointing at the face it looks at, as Revit puts them round a new project. Clicking one
/// opens that elevation.
/// </summary>
public partial class PlanView
{
    /// <summary>How big a marker is drawn, in screen pixels: it is a symbol, the same size at every zoom.</summary>
    private const double ElevationMarkerRadius = 10;

    /// <summary>Raised when an elevation marker is clicked, with the side it looks at.</summary>
    public event EventHandler<ElevationSide>? ElevationPicked;

    private static readonly Brush ElevationMarkerFill = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x3A)));
    private static readonly Brush ElevationArrowFill = Frozen(new SolidColorBrush(Color.FromRgb(0xD7, 0xDD, 0xE5)));
    private static readonly Pen ElevationMarkerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xD7, 0xDD, 0xE5))), 1.3));

    /// <summary>The marker of the elevation under a point of the screen, if any.</summary>
    private ElevationSide? ElevationMarkerAt(Point screen)
    {
        if (Document is null) return null;

        foreach (var side in Elevations.All)
        {
            if (Elevations.MarkerAt(Document, side) is not { } at) continue;
            if ((ModelToScreen(at) - screen).Length <= ElevationMarkerRadius + 3) return side;
        }

        return null;
    }

    private void DrawElevationMarkers(DrawingContext dc)
    {
        if (Document is null) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var side in Elevations.All)
        {
            if (Elevations.MarkerAt(Document, side) is not { } at) continue;

            var centre = ModelToScreen(at);
            var outward = Elevations.Outward(side);

            // On screen, Y runs down: the arrow points back at the building.
            var toward = new Vector(-outward.X, outward.Y);
            var across = new Vector(-toward.Y, toward.X);
            var r = ElevationMarkerRadius;

            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                ctx.BeginFigure(centre + toward * (r * 1.9), true, true);
                ctx.LineTo(centre + across * r, true, false);
                ctx.LineTo(centre - across * r, true, false);
            }

            arrow.Freeze();
            dc.DrawGeometry(ElevationArrowFill, ElevationMarkerPen, arrow);
            dc.DrawEllipse(ElevationMarkerFill, ElevationMarkerPen, centre, r, r);

            var letter = new FormattedText(side.ToString()[..1], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 11, ElevationArrowFill, dpi);
            dc.DrawText(letter, new Point(centre.X - letter.Width / 2, centre.Y - letter.Height / 2));
        }
    }
}
