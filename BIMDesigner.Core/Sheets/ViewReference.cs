using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Sheets;

/// <summary>What kind of view a viewport shows.</summary>
public enum ViewKind
{
    FloorPlan,
    Section,
    Schedule,

    /// <summary>The 3D view. It cannot be placed on a sheet yet, but it has its own settings.</summary>
    Model3D
}

/// <summary>
/// Which view a viewport shows, by reference rather than by copy.
///
/// A viewport holds one of these and nothing else about its contents. Placing a plan on a
/// sheet does not take a picture of the plan: the sheet asks the model for that view every
/// time it is drawn, so a sheet is as current as the building is. That is the whole point of
/// a drawing set produced from a model rather than drawn again by hand. (§6.5)
/// </summary>
public readonly record struct ViewReference(ViewKind Kind, Guid TargetId, string ScheduleName)
{
    public static ViewReference FloorPlan(Guid levelId) => new(ViewKind.FloorPlan, levelId, string.Empty);

    public static ViewReference Section(Guid markerId) => new(ViewKind.Section, markerId, string.Empty);

    public static ViewReference Schedule(string name) => new(ViewKind.Schedule, Guid.Empty, name);

    public static ViewReference Model3D => new(ViewKind.Model3D, Guid.Empty, string.Empty);

    /// <summary>Whether the view this refers to still exists in the project.</summary>
    public bool ExistsIn(BimDocument document)
    {
        // A readonly struct cannot be captured by a lambda, so the id is copied out first.
        var targetId = TargetId;

        return Kind switch
        {
            ViewKind.FloorPlan => document.FindLevel(targetId) is not null,
            ViewKind.Section => document.Elements.OfType<SectionMarker>().Any(s => s.Id == targetId),
            ViewKind.Schedule => !string.IsNullOrWhiteSpace(ScheduleName),
            ViewKind.Model3D => true,
            _ => false
        };
    }

    /// <summary>The name printed under the viewport, read from the model rather than typed.</summary>
    public string TitleIn(BimDocument document)
    {
        var targetId = TargetId;

        return Kind switch
        {
            ViewKind.FloorPlan => document.FindLevel(targetId) is { } level
                ? $"{level.Name} Plan"
                : "Plan - missing level",

            ViewKind.Section => document.Elements.OfType<SectionMarker>()
                .FirstOrDefault(s => s.Id == targetId) is { } marker
                ? $"Section {marker.Name}"
                : "Section - missing marker",

            ViewKind.Schedule => string.IsNullOrWhiteSpace(ScheduleName) ? "Schedule" : ScheduleName,
            ViewKind.Model3D => "3D",
            _ => "View"
        };
    }
}

/// <summary>
/// How big a view's contents are, in that view's own coordinates: model millimetres for a
/// plan, distance-along and elevation for a section.
///
/// Both are "X right, Y up, millimetres", which is what lets one viewport place either of
/// them without knowing which it has.
/// </summary>
public static class ViewExtent
{
    /// <summary>Breathing space around the drawing, in model millimetres before scaling.</summary>
    public const double Margin = 800;

    public static BoundingBox2D Of(BimDocument document, ViewReference reference) => reference.Kind switch
    {
        ViewKind.FloorPlan => OfFloorPlan(document, reference.TargetId),
        ViewKind.Section => OfSection(document, reference.TargetId),
        _ => BoundingBox2D.Empty
    };

    private static BoundingBox2D OfFloorPlan(BimDocument document, Guid levelId)
    {
        var box = BoundingBox2D.Empty;

        foreach (var wall in document.Walls.Where(wall => wall.LevelId == levelId))
        {
            // To the faces, not the location line: a wall's outer leaf is part of the drawing.
            var half = (document.GetWallType(wall)?.Width ?? 0) / 2;
            var offset = wall.ExteriorNormal * half;

            box = box
                .Include(wall.Start + offset).Include(wall.Start - offset)
                .Include(wall.End + offset).Include(wall.End - offset);
        }

        foreach (var slab in document.Elements.OfType<Slab>().Where(slab => slab.LevelId == levelId))
            box = box.Include(BoundingBox2D.Around(slab.Boundary));

        // Grids belong to the whole building, so they appear on every plan and can reach past
        // the walls - a gridline that ran off the edge of its own viewport would be useless.
        foreach (var grid in document.Elements.OfType<Grid>())
            box = box.Include(grid.Start).Include(grid.End);

        return box.IsEmpty ? box : box.Expand(Margin);
    }

    private static BoundingBox2D OfSection(BimDocument document, Guid markerId)
    {
        var marker = document.Elements.OfType<SectionMarker>().FirstOrDefault(s => s.Id == markerId);
        if (marker is null) return BoundingBox2D.Empty;

        var drawing = SectionProjection.Build(document, marker);
        if (drawing.IsEmpty && drawing.Levels.Count == 0) return BoundingBox2D.Empty;

        return new BoundingBox2D(0, drawing.MinElevation, drawing.Width, drawing.MaxElevation)
            .Expand(Margin);
    }
}
