using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Modelling and drawing leftovers: Purge Unused offering schedules on no sheet and filters no view uses; area boundaries picked from walls following them.</summary>
public class ViewsPurgeAndAreasTests
{
    [Fact]
    public void PurgeUnusedOffersSchedulesOnNoSheetAndFiltersNoViewUses()
    {
        var document = BimDocument.CreateDefault();
        var before = Purging.FindViews(document);
        Assert.Equal(document.Schedules.Count(schedule => !schedule.IsKeySchedule), before.Schedules.Count);

        // A schedule placed on a sheet is used; a key schedule never offered.
        var placed = document.Schedules[0];
        var sheet = new Sheet { Number = "A101", Name = "Schedules" };
        sheet.Add(new Viewport { View = ViewReference.Schedule(placed) });
        document.Add(sheet);
        var key = new ScheduleDefinition("Room Style Key", Core.Elements.BuiltInCategory.Rooms, "Name") { KeyName = "Room Style" };
        document.Schedules.Add(key);

        // Filters: one used by a view, one by a view template, one by nothing.
        var (used, templated, idle) = (new ParameterFilter { Name = "Fire Walls" }, new ParameterFilter { Name = "Structure" }, new ParameterFilter { Name = "Old Filter" });
        document.ParameterFilters.AddRange(new[] { used, templated, idle });
        document.ViewSettings.SetFilters(ViewReference.FloorPlan(document.Levels[0].Id), new[] { new FilterOverride(used.Id) });
        var template = new ViewTemplate { Name = "Structural Plan" };
        template.Filters.Add(new FilterOverride(templated.Id));
        document.Add(template);

        var views = Purging.FindViews(document);
        Assert.DoesNotContain(placed, views.Schedules);
        Assert.DoesNotContain(key, views.Schedules);
        Assert.Equal(new[] { idle }, views.Filters);

        // Purged as one step; undone, back in their places.
        var order = document.Schedules.ToList();
        var command = Purging.PurgeViews(document, views.Schedules.Take(1), views.Filters);
        command.Redo();
        Assert.DoesNotContain(views.Schedules[0], document.Schedules);
        Assert.DoesNotContain(idle, document.ParameterFilters);
        command.Undo();
        Assert.Equal(order, document.Schedules);
        Assert.Contains(idle, document.ParameterFilters);
    }

    private static (BimDocument Document, Wall Wall, AreaBoundary Boundary) Picked()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(each => each.Name.StartsWith("Generic", StringComparison.Ordinal));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id };
        document.Add(wall);
        var (from, to) = wall.GetBodyCentreline(type.Structure);
        var boundary = new AreaBoundary { LevelId = wall.LevelId, Scheme = AreaScheme.GrossInternal, Start = from, End = to, WallId = wall.Id };
        document.Add(boundary);
        return (document, wall, boundary);
    }

    [Fact]
    public void AnAreaBoundaryPickedFromAWallFollowsIt()
    {
        // The wall moved 1 m and lengthened 2 m: the boundary along its middle goes with it, as it is measured and drawn.
        var (document, wall, boundary) = Picked();
        (wall.Start, wall.End) = (new Point2D(0, 1000), new Point2D(8000, 1000));
        var shown = Areas.Boundaries(document, wall.LevelId, AreaScheme.GrossInternal).Single();
        Assert.Same(boundary, shown);
        var (from, to) = wall.GetBodyCentreline(document.GetWallType(wall)!.Structure);
        Assert.Equal(from, boundary.Start);
        Assert.Equal(to, boundary.End);
        Assert.Equal(8000, boundary.Length, 0);

        // Its end dragged by hand: it stays where it was put, and undo puts it back on the wall, following it again.
        var handle = ElementHandles.Of(document, boundary).First(each => each.Key == "end");
        var snapshot = ElementHandles.Snapshot(boundary);
        ElementHandles.Drag(document, boundary, handle, new Point2D(5000, 3000), 0);
        Assert.Null(boundary.WallId);
        wall.End = new Point2D(9000, 1000);
        Areas.FollowWalls(document);
        Assert.Equal(new Point2D(5000, 3000), boundary.End);
        ElementHandles.Restore(boundary, snapshot);
        Assert.Equal(wall.Id, boundary.WallId);
    }

    [Fact]
    public void ABoundarysWallIsKeptWithTheProject()
    {
        var (document, wall, boundary) = Picked();
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).All<AreaBoundary>().Single();
        Assert.Equal(wall.Id, reopened.WallId);
        Assert.Equal(boundary.Start, reopened.Start);
    }
}
