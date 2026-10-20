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

    /// <summary>A 3D view: the {3D} view, a camera, a copy - drawn on a sheet in its own style.</summary>
    Model3D,

    /// <summary>An elevation: one side of the building, or one arrow of an interior elevation marker.</summary>
    Elevation,

    /// <summary>A callout: part of a plan or section, larger, cropped to its box.</summary>
    Callout,

    /// <summary>A keynote legend: the keys tagged, and what they say - in the whole project, or (Index 1) in the views on its sheet.</summary>
    KeynoteLegend
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
    /// <summary>Which of a target's views: an elevation's side, an interior marker's arrow.</summary>
    public int Index { get; init; }

    /// <summary>A callout's own view.</summary>
    public static ViewReference Callout(Guid calloutId) => new(ViewKind.Callout, calloutId, string.Empty);

    /// <summary>One of the building's four elevations.</summary>
    public static ViewReference Elevation(ElevationSide side) => new(ViewKind.Elevation, Guid.Empty, string.Empty) { Index = (int)side };

    /// <summary>The view one arrow of an interior elevation marker looks at.</summary>
    public static ViewReference InteriorElevation(Guid markerId, int arrow) => new(ViewKind.Elevation, markerId, string.Empty) { Index = arrow };

    public static ViewReference FloorPlan(Guid levelId) => new(ViewKind.FloorPlan, levelId, string.Empty);

    public static ViewReference Section(Guid markerId) => new(ViewKind.Section, markerId, string.Empty);

    /// <summary>A schedule of the project's, by its id - and its name, for files that knew it only by that.</summary>
    public static ViewReference Schedule(Schedules.ScheduleDefinition schedule) => new(ViewKind.Schedule, schedule.Id, schedule.Name);

    /// <summary>The schedule a reference is to: by its id, or by its name where it was placed before schedules had ids.</summary>
    public Schedules.ScheduleDefinition? ScheduleIn(BimDocument document)
    {
        if (Kind != ViewKind.Schedule) return null;
        var (id, name) = (TargetId, ScheduleName);
        return id != Guid.Empty
            ? document.FindSchedule(id)
            : document.Schedules.FirstOrDefault(schedule => schedule.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static ViewReference Model3D => new(ViewKind.Model3D, Guid.Empty, string.Empty);

    /// <summary>A keynote legend: of the keys tagged in the views on its sheet, or in the whole project.</summary>
    public static ViewReference KeynoteLegend(bool bySheet) => new(ViewKind.KeynoteLegend, Guid.Empty, string.Empty) { Index = bySheet ? 1 : 0 };

    /// <summary>A 3D view kept by name: a camera's, or a copy of {3D}.</summary>
    public static ViewReference View3D(Guid viewId) => new(ViewKind.Model3D, viewId, string.Empty);

    /// <summary>Whether the view this refers to still exists in the project.</summary>
    public bool ExistsIn(BimDocument document)
    {
        // A readonly struct cannot be captured by a lambda, so the id is copied out first.
        var targetId = TargetId;

        return Kind switch
        {
            ViewKind.FloorPlan => document.FindLevel(targetId) is not null,
            ViewKind.Section => document.Elements.OfType<SectionMarker>().Any(s => s.Id == targetId),
            ViewKind.Schedule => ScheduleIn(document) is not null,
            ViewKind.Model3D => Views3D.Find(document, this) is not null,
            ViewKind.Elevation => targetId == Guid.Empty
                ? Index is >= 0 and <= 3
                : document.FindElement(targetId) is InteriorElevationMarker marker && marker.Looks(Index),
            ViewKind.Callout => document.FindElement(targetId) is Views.Callout callout && callout.Parent.Kind != ViewKind.Callout && callout.Parent.ExistsIn(document),
            ViewKind.KeynoteLegend => true,
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

            ViewKind.Schedule => ScheduleIn(document)?.Name ?? (string.IsNullOrWhiteSpace(ScheduleName) ? "Schedule - missing" : $"{ScheduleName} - missing"),
            ViewKind.Model3D => Views3D.Find(document, this)?.Name ?? "3D - missing view",
            ViewKind.Elevation => targetId == Guid.Empty
                ? Elevations.Name((ElevationSide)Math.Clamp(Index, 0, 3))
                : document.FindElement(targetId) is InteriorElevationMarker marker
                    ? marker.ViewName(Index)
                    : "Elevation - missing marker",
            ViewKind.Callout => document.FindElement(targetId) is Views.Callout callout ? callout.Name : "Callout - missing",
            ViewKind.KeynoteLegend => Index == 1 ? "Keynote Legend (by sheet)" : "Keynote Legend",
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
        ViewKind.Model3D or ViewKind.Elevation => Projections.Of(document, reference).Extent is { IsEmpty: false } drawn ? drawn.Expand(Margin) : BoundingBox2D.Empty,

        // A callout is cropped to its box: the box is its extent.
        ViewKind.Callout => document.FindElement(reference.TargetId) is Callout callout ? callout.Region : BoundingBox2D.Empty,
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

        // And the notes on the storey: a cloud, a keynote or a label clipped by its own viewport
        // would say nothing.
        foreach (var element in document.Elements.Where(element => element.LevelId == levelId))
            switch (element)
            {
                case Annotation.RevisionCloud cloud:
                    foreach (var point in cloud.Outline) box = box.Include(point);
                    if (cloud.TagAt is { } tagAt) box = box.Include(tagAt);
                    break;

                case Annotation.KeynoteTag keynote:
                    box = box.Include(keynote.Position);
                    if (keynote.LeaderEnd is { } end) box = box.Include(end);
                    break;

                case Annotation.Tag tag:
                    box = box.Include(tag.Position);
                    break;

                case Annotation.TextNote note:
                    box = box.Include(note.Position);
                    if (note.LeaderEnd is { } leader) box = box.Include(leader);
                    break;
            }

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
