using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Interior elevation markers on the plan: placed in a room with a click, drawn as a circle with
/// its four arrows - black where the arrow is a view - and its number. Clicking an arrow opens
/// the view it looks at; clicking the circle picks the marker, to turn it or switch arrows off.
/// </summary>
public partial class PlanView
{
    /// <summary>How big an interior marker is drawn, screen pixels: a symbol, the same at every zoom.</summary>
    private const double InteriorMarkerRadius = 9;

    /// <summary>Raised with a marker just placed.</summary>
    public event EventHandler<InteriorElevationMarker>? InteriorElevationPlaced;

    /// <summary>Raised when an arrow of an interior elevation marker is clicked, with the view it looks at.</summary>
    public event EventHandler<ViewReference>? InteriorViewPicked;

    /// <summary>A click with the Interior Elevation tool: a marker there, looking all four ways. Public so it can be driven from tests.</summary>
    public bool PlaceInteriorElevation(Point2D at)
    {
        if (Document is null || Document.FindLevel(ActiveLevelId) is null) return false;

        var marker = new InteriorElevationMarker { LevelId = ActiveLevelId, Location = at, Name = InteriorElevations.NextName(Document) };
        Apply(new AddElementCommand(Document, marker, "Interior Elevation"));
        var inRoom = InteriorElevations.Frame(Document, marker, 0).InRoom;
        HintChanged?.Invoke(this, inRoom
            ? $"Interior elevation {marker.Name}: four views of the room, under Elevations. Click an arrow to open its view; pick the marker to turn it or switch arrows off."
            : $"Interior elevation {marker.Name} - but not in a room enclosed by walls, so its views are cropped six metres either way. Place it inside a room.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        InteriorElevationPlaced?.Invoke(this, marker);
        return true;
    }

    /// <summary>The arrow of an interior marker under a point of the screen: the view it looks at.</summary>
    private ViewReference? InteriorArrowAt(Point screen)
    {
        foreach (var marker in OnActiveLevel<InteriorElevationMarker>())
        {
            var centre = ModelToScreen(marker.Location);
            var off = screen - centre;
            if (off.Length <= InteriorMarkerRadius * 0.6 || off.Length > InteriorMarkerRadius * 2.2) continue;

            foreach (var arrow in marker.Arrows)
            {
                var look = marker.Look(arrow);
                var toward = new Vector(look.X, -look.Y);
                if (Vector.Multiply(off, toward) / off.Length > Math.Cos(Math.PI / 4)) return ViewReference.InteriorElevation(marker.Id, arrow);
            }
        }

        return null;
    }

    /// <summary>The markers on this storey under a point: picked by their circle.</summary>
    private IEnumerable<InteriorElevationMarker> InteriorMarkersAt(Point2D model) =>
        OnActiveLevel<InteriorElevationMarker>().Where(marker => (ModelToScreen(marker.Location) - ModelToScreen(model)).Length <= InteriorMarkerRadius + 2);

    private void DrawInteriorMarkers(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var marker in OnActiveLevel<InteriorElevationMarker>())
        {
            var centre = ModelToScreen(marker.Location);
            var r = InteriorMarkerRadius;
            var selected = _selection.Contains(marker);
            var pen = selected ? SketchSelectedPen : ElevationMarkerPen;

            for (var arrow = 0; arrow < 4; arrow++)
            {
                var look = marker.Look(arrow);
                var toward = new Vector(look.X, -look.Y);
                var across = new Vector(-toward.Y, toward.X);

                var shape = new StreamGeometry();
                using (var ctx = shape.Open())
                {
                    ctx.BeginFigure(centre + toward * (r * 1.9), true, true);
                    ctx.LineTo(centre + (toward + across) * (r * 0.72), true, false);
                    ctx.LineTo(centre + (toward - across) * (r * 0.72), true, false);
                }

                shape.Freeze();
                dc.DrawGeometry(marker.Looks(arrow) ? ElevationArrowFill : null, pen, shape);
            }

            dc.DrawEllipse(ElevationMarkerFill, pen, centre, r, r);
            var number = new FormattedText(marker.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), 10, ElevationArrowFill, dpi);
            dc.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
        }
    }
}
