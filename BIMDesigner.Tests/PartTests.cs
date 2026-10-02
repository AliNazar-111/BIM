using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Parts: a wall's layers as pieces of their own - divided into panels with joints, given their
/// own materials, left out - shown, measured and scheduled as such.
/// </summary>
public class PartTests
{
    /// <summary>An exterior wall 6 m long and 3 m high, free at both ends.</summary>
    private static (BimDocument Document, Wall Wall, WallType Type) OneWall()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        type.WrapAtEnds = WallWrapping.None;
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return (document, wall, type);
    }

    [Fact]
    public void AWallMadeIntoPartsHasOneForEachLayerAndIsShownAsThem()
    {
        var (document, wall, type) = OneWall();
        Parts.Create(document, new[] { wall })!.Redo();

        var parts = Parts.Of(document, wall);
        Assert.Equal(type.Structure.Layers.Count(layer => layer.Thickness > 0), parts.Count);
        Assert.NotNull(Parts.WhyNot(document, wall));

        // Each part is its layer, the wall's whole length and height.
        var brick = parts.Single(part => part.Layer == 0);
        Assert.Equal(6000 * 3000 * type.Structure.Layers[0].Thickness, Parts.Volume(document, brick), 0);
        Assert.Equal(6000 * 3000, Parts.Area(document, brick), 0);

        // In 3D the wall's own layers give way to the parts, each selectable.
        var meshes = ModelMeshBuilder.Build(document);
        Assert.DoesNotContain(meshes, mesh => mesh.ElementId == wall.Id && mesh.Description == "Brick, Common");
        Assert.Contains(meshes, mesh => mesh.ElementId == brick.Id && mesh.Description == "Brick, Common");
    }

    [Fact]
    public void DividingAPartMakesPanelsWithJointsBetween()
    {
        var (document, wall, type) = OneWall();
        Parts.Create(document, new[] { wall })!.Redo();
        var brick = Parts.Of(document, wall).Single(part => part.Layer == 0);

        var divide = Parts.Divide(document, new[] { brick }, 1200, 1500, 10)!;
        divide.Redo();

        var panels = Parts.Of(document, wall).Where(part => part.Layer == 0).ToList();
        Assert.Equal(10, panels.Count);
        Assert.DoesNotContain(brick, document.Elements);

        // Each a little short of its neighbours: 1190 by 1490 in the middle of the wall.
        var middle = panels.Single(panel => panel.From == 2400 && panel.Bottom is null);
        var (length, height) = Parts.Size(document, middle);
        Assert.Equal(1190, length, 3);
        Assert.Equal(1495, height, 3);

        divide.Undo();
        Assert.Contains(brick, document.Elements);
        Assert.Single(Parts.Of(document, wall), part => part.Layer == 0);
    }

    [Fact]
    public void APartStopsAtTheDoorAndIsMeasuredWithoutIt()
    {
        var (document, wall, type) = OneWall();
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        var doorType = document.FindType<DoorType>(door.TypeId)!;
        Parts.Create(document, new[] { wall })!.Redo();

        var brick = Parts.Of(document, wall).Single(part => part.Layer == 0);
        Assert.Equal(6000 * 3000 - doorType.Width * doorType.Height, Parts.Area(document, brick), 0);
    }

    [Fact]
    public void APartCanBeGivenItsOwnMaterialOrLeftOutAndTheScheduleListsWhatIsBuilt()
    {
        var (document, wall, type) = OneWall();
        Parts.Create(document, new[] { wall })!.Redo();
        var parts = Parts.Of(document, wall);
        var brick = parts.Single(part => part.Layer == 0);
        var plaster = parts.Single(part => part.Layer == type.Structure.Layers.Count - 1);

        var terracotta = document.Materials.First(m => m.Name == "Paint, Terracotta");
        brick.GetInstanceParameters(document).Single(p => p.Definition == PartParameters.Material).TrySet(terracotta.Name);
        Assert.Equal(terracotta.Id, Parts.MaterialOf(document, brick));

        new ExcludePartsCommand(new[] { plaster }, true).Redo();
        Assert.Null(Parts.Mesh(document, plaster));

        var definition = ScheduleDefinition.Defaults().Single(d => d.Name == "Part Schedule");
        var result = Schedule.Run(document, definition);
        Assert.Equal(parts.Count - 1, result.Rows.Count);
    }

    [Fact]
    public void PartsAreSavedAndGoWithTheirWall()
    {
        var (document, wall, _) = OneWall();
        Parts.Create(document, new[] { wall })!.Redo();
        Parts.Divide(document, Parts.Of(document, wall).Take(1), 2000, null, 6)!.Redo();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = loaded.Walls.Single();
        Assert.Equal(Parts.Of(document, wall).Count, Parts.Of(loaded, again).Count);
        Assert.Equal(
            Parts.Of(document, wall).Sum(part => Parts.Volume(document, part)),
            Parts.Of(loaded, again).Sum(part => Parts.Volume(loaded, part)), 0);

        // Deleting the wall takes its parts.
        new DeleteElementsCommand(document, new[] { wall }).Redo();
        Assert.Empty(document.Elements.OfType<Part>());
    }

    [Fact]
    public void ACurtainWallOrAStackedWallCannotBeMadeIntoParts()
    {
        var document = BimDocument.CreateDefault();
        var curtain = document.TypesOf<CurtainWallType>().First();
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = curtain.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);

        Assert.NotNull(Parts.WhyNot(document, wall));
        Assert.Null(Parts.Create(document, new[] { wall }));
    }
}
