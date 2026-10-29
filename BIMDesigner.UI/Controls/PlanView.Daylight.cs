using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Energy;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The daylight factor over each habitable room's floor, as the Daylight window last worked it out: a square
/// of colour at each point of its grid, under the walls - dark blue where there is little daylight, through
/// blue at the UK annex's 0.7% and green at BS EN 17037's 2.1% to yellow and orange above 5%.
/// </summary>
public partial class PlanView
{
    private DaylightResult? _daylight;

    /// <summary>The daylight drawn over the plan, or none.</summary>
    public DaylightResult? DaylightMap
    {
        get => _daylight;
        set
        {
            _daylight = value;
            InvalidateVisual();
        }
    }

    /// <summary>The bands of the map: the factor each starts at, %, and its colour.</summary>
    internal static readonly (double From, Color Colour)[] DaylightBands =
    {
        (0, Color.FromRgb(0x1B, 0x26, 0x6B)), (0.7, Color.FromRgb(0x2F, 0x6F, 0xD0)), (1.4, Color.FromRgb(0x2B, 0xB3, 0xC8)),
        (2.1, Color.FromRgb(0x4C, 0xB8, 0x4A)), (3.5, Color.FromRgb(0xE6, 0xD2, 0x3A)), (5, Color.FromRgb(0xF0, 0x8A, 0x24))
    };

    private static readonly Brush[] DaylightBrushes = DaylightBands.Select(band => Frozen(new SolidColorBrush(Color.FromArgb(0x90, band.Colour.R, band.Colour.G, band.Colour.B)))).ToArray();

    private void DrawDaylight(DrawingContext dc)
    {
        if (_daylight is not { } result || Document is null) return;
        var half = result.Spacing / 2;
        foreach (var room in result.Rooms.Where(room => room.Room.LevelId == ActiveLevelId))
            foreach (var point in room.Points)
            {
                var band = 0;
                while (band + 1 < DaylightBands.Length && point.Factor >= DaylightBands[band + 1].From) band++;
                var a = ModelToScreen(new Core.Geometry.Point2D(point.At.X - half, point.At.Y - half));
                var b = ModelToScreen(new Core.Geometry.Point2D(point.At.X + half, point.At.Y + half));
                dc.DrawRectangle(DaylightBrushes[band], null, new Rect(a, b));
            }
    }
}
