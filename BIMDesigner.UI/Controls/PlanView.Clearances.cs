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

    /// <summary>Whether the clear space round WCs, basins, baths, showers, beds, tables and kitchens is drawn.</summary>
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

        // A kitchen or bathroom piece, a socket or a switch, where the rules do not want it.
        foreach (var problem in Document.Elements.OfType<Component>().Where(component => component.LevelId == ActiveLevelId && _shows(component))
                     .SelectMany(component => Kitchens.Problems(Document, component, standard).Concat(Bathrooms.Problems(Document, component, standard))
                         .Concat(Services.Problems(Document, component, standard)).Concat(Alarms.Problems(Document, component, standard))
                         .Concat(Outside.Problems(Document, component, standard))))
            dc.DrawGeometry(BlockedFill, BlockedPen, Outline(problem.Outline));

        // A floor or roof short of its rules, outlined: its U-value, its sound, its fire, its pitch.
        foreach (var problem in Document.Elements.OfType<Slab>().Where(slab => slab.LevelId == ActiveLevelId && slab is Floor or Roof && _shows(slab))
                     .SelectMany(slab => SlabRules.Problems(Document, slab, standard)))
            dc.DrawGeometry(null, BlockedPen, Outline(problem.Outline));

        // A wall short of its rules: its U-value, its sound, its fire resistance by a garage.
        foreach (var problem in Document.Walls.Where(wall => wall.LevelId == ActiveLevelId && _shows(wall))
                     .SelectMany(wall => WallRules.Problems(Document, wall, standard)))
            dc.DrawGeometry(BlockedFill, BlockedPen, Outline(problem.Outline));

        // A door or window short of its rules: its width, its nib, its fire rating, its glass.
        foreach (var problem in Document.Elements.OfType<Door>()
                     .Where(door => _shows(door) && Document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId)?.LevelId == ActiveLevelId)
                     .SelectMany(door => Doors.Problems(Document, door, standard))
                     .Concat(Document.Elements.OfType<Core.Architecture.Window>()
                         .Where(window => _shows(window) && Document.Walls.FirstOrDefault(wall => wall.Id == window.HostWallId)?.LevelId == ActiveLevelId)
                         .SelectMany(window => WindowRules.Problems(Document, window, standard))))
            dc.DrawGeometry(BlockedFill, BlockedPen, Outline(problem.Outline));

        // A door's swing only where something stands in it: the swing itself is drawn already.
        foreach (var door in Document.Elements.OfType<Door>().Where(door => _shows(door) && Document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId)?.LevelId == ActiveLevelId))
        foreach (var (zone, inTheWay) in Clearances.Swings(Document, door))
            if (inTheWay.Count > 0)
                dc.DrawGeometry(BlockedFill, BlockedPen, Outline(zone.Outline));
    }
}
