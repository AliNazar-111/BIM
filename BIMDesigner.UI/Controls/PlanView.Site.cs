using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The site on the plan: the ground put round the whole building at a click; pads graded into it
/// and the property line round it, by their corners; spot elevations reading heights off it; a
/// spot level taken out of it; and the ground coloured by its slope, height, or cut and fill,
/// with a legend.
/// </summary>
public partial class PlanView
{
    /// <summary>How high above its level the next pad is, mm - below, for minus: 150 under the floor keeps the damp-proof course clear.</summary>
    public double ActivePadOffset { get; set; } = -150;

    /// <summary>The side slope the next pad is battered back to the ground at, so many to one; none for a sheer edge.</summary>
    public double ActivePadBatter { get; set; } = 2;

    /// <summary>What the next pad is laid as.</summary>
    public GroundSurface ActivePadSurface { get; set; } = GroundSurface.Paving;

    /// <summary>The setback the next property line keeps the building behind, mm; none for none.</summary>
    public double ActiveSetback { get; set; }

    /// <summary>How far round the building Around Building puts the ground, mm.</summary>
    public static double GroundMargin => Core.Units.Imperial ? 50 * Core.Units.MillimetresPerFoot : 15000;

    private GroundColouring _groundColouring;

    /// <summary>How the ground is coloured on this plan: plainly, by slope, by height, or by cut and fill.</summary>
    public GroundColouring GroundColouring
    {
        get => _groundColouring;
        set
        {
            _groundColouring = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Ground put round the whole of the building on this storey - its walls, floors, and what
    /// stands outside them - so far out on every side, level at the elevation on the option bar:
    /// with none modelled, the quickest way to set the building in the ground. None where there is
    /// ground on this storey already, or nothing to put it round. Public so it can be driven from tests.
    /// </summary>
    public bool GroundAroundBuilding()
    {
        if (Document?.FindLevel(ActiveLevelId) is not { } level) return false;
        if (GroundOnLevel is { } existing)
        {
            HintChanged?.Invoke(this, $"There is ground on {level.Name} already: pick it and set its Lowest Point to move it up or down, or click spot levels onto it.");
            return false;
        }

        var extent = new List<Point2D>();
        foreach (var wall in Document.Walls.Where(wall => wall.LevelId == ActiveLevelId)) extent.AddRange(new[] { wall.Start, wall.End });
        foreach (var slab in Document.All<Core.Architecture.Slab>().Where(slab => slab.LevelId == ActiveLevelId && slab is not Core.Architecture.Ceiling)) extent.AddRange(slab.Boundary);
        foreach (var ramp in Document.All<Ramp>().Where(ramp => ramp.LevelId == ActiveLevelId)) extent.AddRange(Ramps.Layout(Document, ramp).Footprint);
        if (extent.Count == 0)
        {
            HintChanged?.Invoke(this, $"Nothing on {level.Name} to put the ground round: draw the building first, or click spot levels with Toposurface.");
            return false;
        }

        var (minX, maxX, minY, maxY) = (extent.Min(p => p.X) - GroundMargin, extent.Max(p => p.X) + GroundMargin, extent.Min(p => p.Y) - GroundMargin, extent.Max(p => p.Y) + GroundMargin);
        var height = level.Elevation + ActiveGroundElevation;
        var made = new Toposurface { LevelId = ActiveLevelId };
        made.SetPoints(new[] { new Point3D(minX, minY, height), new Point3D(maxX, minY, height), new Point3D(maxX, maxY, height), new Point3D(minX, maxY, height) });
        Apply(new AddElementCommand(Document, made, "Ground Around Building"));
        HintChanged?.Invoke(this, $"Ground put round the building, {Core.Units.Metres(GroundMargin, "0")} out, at {Units.FormatLength(ActiveGroundElevation)} from {level.Name}. {Terrains.Verdict(Document, made)}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A spot elevation where clicked, reading the height of what is there. Public so it can be driven from tests.</summary>
    public bool PlaceSpotElevation(Point2D at)
    {
        if (Document?.FindLevel(ActiveLevelId) is null) return false;
        var spot = new SpotElevation { LevelId = ActiveLevelId, Location = at };
        Apply(new AddElementCommand(Document, spot, "Spot Elevation"));
        HintChanged?.Invoke(this, $"{SpotElevations.Label(Document, spot)}: the {SpotElevations.Reading(Document, spot).What.ToLowerInvariant()} here. Click for another; Esc to stop.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A spot level taken out of the ground - three are kept, the least that make it. Public so it can be driven from tests.</summary>
    public bool DeleteGroundPoint(Toposurface ground, int index)
    {
        if (Document is null || index < 0 || index >= ground.Points.Count) return false;
        if (ground.Points.Count <= 3)
        {
            HintChanged?.Invoke(this, "Three spot levels make the ground: delete the ground itself to take it away.");
            return false;
        }

        Apply(new SetToposurfacePointsCommand(ground, ground.Points.Where((_, i) => i != index), "Delete Spot Level"));
        HintChanged?.Invoke(this, $"Spot level taken out: {ground.Points.Count} left.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The menu for a handle on the selected ground: a spot level to take out.</summary>
    private bool GroundPointMenu(Point2D raw)
    {
        if (OnlyHandled is not Toposurface ground || HandleAt(raw) is not { } handle || !handle.Key.StartsWith("point:", StringComparison.Ordinal)) return false;
        if (!int.TryParse(handle.Key[6..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index >= ground.Points.Count) return false;

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var height = new System.Windows.Controls.MenuItem { Header = $"Spot level at {Units.FormatLength(ground.Points[index].Z)}", IsEnabled = false };
        var delete = new System.Windows.Controls.MenuItem { Header = "Delete Spot Level" };
        delete.Click += (_, _) => DeleteGroundPoint(ground, index);
        menu.Items.Add(height);
        menu.Items.Add(delete);
        menu.IsOpen = true;
        return true;
    }

    /// <summary>The pads graded into the ground under a point of this plan.</summary>
    private IEnumerable<SitePad> PadsAt(Point2D model) =>
        OnActiveLevel<SitePad>().Where(pad => pad.Boundary.Count >= 3 && Polygon2D.Contains(pad.Boundary, model));

    /// <summary>The property lines a point of this plan is on, or near.</summary>
    private IEnumerable<PropertyLine> PropertyLinesAt(Point2D model)
    {
        var reach = 6 / PixelsPerMm;
        return OnActiveLevel<PropertyLine>().Where(line => line.Boundary.Count >= 2
            && Enumerable.Range(0, line.Boundary.Count).Any(i => Line2D.DistanceFromSegment(model, line.Boundary[i], line.Boundary[(i + 1) % line.Boundary.Count]) <= reach));
    }

    /// <summary>The spot elevations at a point of this plan: on their mark, or their label.</summary>
    private IEnumerable<SpotElevation> SpotElevationsAt(Point2D model)
    {
        var reach = 10 / PixelsPerMm;
        return OnActiveLevel<SpotElevation>().Where(spot => spot.Location.DistanceTo(model) <= reach
                                                             || (model - spot.Location) is var off && off.X >= 0 && off.X <= 70 / PixelsPerMm && off.Y >= 0 && off.Y <= 20 / PixelsPerMm);
    }

    /// <summary>The legend of the ground's colours, in the plan's corner, while it is coloured.</summary>
    private void DrawGroundLegend(DrawingContext dc)
    {
        if (Document is null || GroundColouring == GroundColouring.None || GroundOnLevel is not { } ground) return;
        var bands = GroundColours.Bands(Document, ground, GroundColouring);
        if (bands.Count == 0) return;

        var title = GroundColouring switch
        {
            GroundColouring.Slope => "Ground by slope",
            GroundColouring.Height => "Ground by height",
            _ => ground.ExistingPoints.Count >= 3 ? "Cut and fill, from the ground as recorded" : "Cut and fill, from its spot levels"
        };
        var typeface = new Typeface("Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Text(string text, double size, bool bold = false) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? new Typeface(typeface.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : typeface, size, Brushes.Black, dpi);

        var labels = bands.Select(band => Text(band.Label, 11)).ToList();
        var heading = Text(title, 11.5, bold: true);
        var width = Math.Max(heading.Width, labels.Max(label => label.Width) + 22) + 16;
        var height = 26 + bands.Count * 17 + 6;
        var corner = new Point(12, ActualHeight - height - 12);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)), 1), new Rect(corner, new Size(width, height)), 4, 4);
        dc.DrawText(heading, new Point(corner.X + 8, corner.Y + 6));
        for (var i = 0; i < bands.Count; i++)
        {
            var row = corner.Y + 26 + i * 17;
            var colour = bands[i].Colour;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B)), new Pen(Brushes.Gray, 0.5), new Rect(corner.X + 8, row + 2, 14, 11));
            dc.DrawText(labels[i], new Point(corner.X + 28, row));
        }
    }
}
