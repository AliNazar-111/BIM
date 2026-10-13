using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The clear space fittings need, drawn over the plan: the zone in front of a WC, beside a bath,
/// round a bed, in front of a kitchen - green where it is clear, red where something stands in
/// it. A design aid, drawn here in the editor and never on a sheet.
/// </summary>
public partial class PlanView
{
    private static readonly Brush ClearFill = RenderPens.Fill(Color.FromArgb(0x2A, 0x3C, 0xB3, 0x71));
    private static readonly Brush BlockedFill = RenderPens.Fill(Color.FromArgb(0x48, 0xE0, 0x4B, 0x3A));
    private static readonly Pen ClearPen = RenderPens.Dashed(Color.FromRgb(0x3C, 0xB3, 0x71), 1.2, 5, 3);
    private static readonly Pen BlockedPen = RenderPens.Dashed(Color.FromRgb(0xE0, 0x4B, 0x3A), 1.6, 5, 3);

    private bool _showClearances;

    /// <summary>Whether the clear space round WCs, basins, baths, showers, beds and kitchens is drawn.</summary>
    public bool ShowClearances
    {
        get => _showClearances;
        set
        {
            if (_showClearances == value) return;

            _showClearances = value;
            InvalidateVisual();
        }
    }

    /// <summary>Each fitting's zones on this storey, as the project's standard sets them out.</summary>
    private void DrawClearances(DrawingContext dc)
    {
        if (!ShowClearances || Document is null) return;

        var standard = Document.ProjectInformation.ClearanceStandard;
        using var rooms = new RoomsOf.Outlines();
        foreach (var component in Document.Elements.OfType<Component>().Where(component => component.LevelId == ActiveLevelId && _shows(component)))
        foreach (var (zone, inTheWay) in Clearances.Of(Document, component, standard))
        {
            var clear = inTheWay.Count == 0;
            dc.DrawGeometry(clear ? ClearFill : BlockedFill, clear ? ClearPen : BlockedPen, Outline(zone.Outline));
        }
    }
}
