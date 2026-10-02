using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Log walls: built of logs in courses, crossing at the corners, stopping at doors and windows.</summary>
public class LogWallTests
{
    /// <summary>Two round-log walls meeting at a corner: one along X from the origin, one up Y from its end.</summary>
    private static (BimDocument Document, Wall AlongX, Wall AlongY, WallType Type) Corner()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name == "Log Wall - Round 220");
        var level = document.Levels[0].Id;
        var alongX = new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2850 };
        var alongY = new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 4000), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2850 };
        document.Add(alongX);
        document.Add(alongY);
        return (document, alongX, alongY, type);
    }

    [Fact]
    public void ItIsBuiltOfLogsCourseOnCourseInsteadOfLayers()
    {
        var (document, alongX, _, type) = Corner();
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == alongX.Id).ToList();
        var logs = Assert.Single(meshes);
        Assert.Equal("Timber, Softwood", logs.Description);

        // 2850 high in 190 courses: fifteen of them, from the floor.
        var heights = logs.Positions.Select(point => Math.Round(point.Z)).Distinct().ToList();
        Assert.Equal(0, heights.Min());
        Assert.Equal(2850, heights.Max());
        Assert.Equal(type.Width / 2, logs.Positions.Max(point => point.Y), 3);
    }

    [Fact]
    public void AtTheCornerTheLogsRunOnPastItAndTheOtherWallsSitHalfACourseUp()
    {
        var (document, alongX, alongY, type) = Corner();
        var log = type.Log!;
        var meshes = ModelMeshBuilder.Build(document);
        var x = meshes.Single(mesh => mesh.ElementId == alongX.Id);
        var y = meshes.Single(mesh => mesh.ElementId == alongY.Id);

        // Past the corner by half the wall and the overhang, each way.
        Assert.Equal(5000 + type.Width / 2 + log.Overhang, x.Positions.Max(point => point.X), 3);
        Assert.Equal(-(type.Width / 2 + log.Overhang), y.Positions.Min(point => point.Y), 3);

        // The second wall's courses are half a course up: its first is a half log.
        Assert.False(LogWalls.Raised(alongX));
        Assert.True(LogWalls.Raised(alongY));
        Assert.Contains(y.Positions, point => Math.Abs(point.Z - log.CourseHeight / 4) < 1e-6);
        Assert.DoesNotContain(x.Positions, point => Math.Abs(point.Z - log.CourseHeight / 4) < 1e-6);

        // In plan the log ends show past the corner.
        Assert.Single(LogWalls.CornerEnds(document, alongX, type));
    }

    [Fact]
    public void TheLogsStopAtAWindow()
    {
        var (document, alongX, _, _) = Corner();
        var window = new Window { TypeId = document.TypesOf<WindowType>().First().Id, LevelId = alongX.LevelId, HostWallId = alongX.Id, DistanceAlongWall = 2500, SillHeight = 900 };
        document.Add(window);
        var windowType = document.FindType<WindowType>(window.TypeId)!;
        var (from, to) = window.GetSpan(windowType);

        var logs = ModelMeshBuilder.Build(document).Single(mesh => mesh.ElementId == alongX.Id && mesh.Description == "Timber, Softwood");
        var through = logs.Positions.Where(point => point.X > from + 1 && point.X < to - 1 && point.Z > 900 + 200 && point.Z < 900 + windowType.Height - 200);
        Assert.Empty(through);
    }

    [Fact]
    public void ATypeCanBeBuiltAsLogsAndItIsSaved()
    {
        var document = BimDocument.CreateDefault();
        var generic = document.TypesOf<WallType>().First(t => t.Log is null);
        var builtAs = generic.GetTypeParameters(document).Single(p => p.Definition == WallTypeParameters.BuiltAs);
        Assert.True(builtAs.TrySet("Logs"));
        Assert.Equal(LogWall.Default, generic.Log);

        var shape = generic.GetTypeParameters(document).Single(p => p.Definition == WallTypeParameters.LogShape);
        Assert.True(shape.TrySet("DLog"));
        Assert.Equal(LogShape.DLog, generic.Log!.Shape);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<WallType>(generic.Id)!;
        Assert.Equal(generic.Log, loaded.Log);
        Assert.Equal(generic.Log, generic.Duplicate("Copy").Log);
    }
}
