using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A door's instance properties as Revit lists them: level, sill and head, orientation in a
/// slanted wall, frame type and material, finish, mark, comments and the two phases.
/// </summary>
public class DoorInstancePropertyTests
{
    private static (BimDocument Document, Wall Wall, Door Door) WallWith(double slantDegrees)
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            CrossSection = slantDegrees == 0 ? WallCrossSection.Vertical : WallCrossSection.Slanted,
            SlantAngle = slantDegrees
        };
        document.Add(wall);

        var type = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Single -"));
        var door = new Door { TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        return (document, wall, door);
    }

    private static double LeafYSpan(BimDocument document, Wall wall, Door door)
    {
        var bounds = ModelMeshBuilder.BuildWall(document, wall)
            .Where(m => m.ElementId == door.Id && m.Description == "Door leaf")
            .Select(m => m.Bounds()!.Value)
            .Single();

        return bounds.Max.Y - bounds.Min.Y;
    }

    [Fact]
    public void ADoorStandsUprightOrLeansWithASlantedWall()
    {
        var (document, wall, door) = WallWith(slantDegrees: 15);

        // Upright, as a door already in the wall when the wall was slanted stays: the leaf is
        // no thicker across the wall than the leaf itself.
        Assert.Equal(OpeningOrientation.Vertical, door.Orientation);
        Assert.True(LeafYSpan(document, wall, door) < 100);

        // Leaning with the wall, the top of the leaf is carried well across it.
        door.Orientation = OpeningOrientation.Slanted;
        Assert.True(LeafYSpan(document, wall, door) > 500);
    }

    [Fact]
    public void OrientationIsOnlyAChoiceInASlantedWall()
    {
        var (upright, _, uprightDoor) = WallWith(slantDegrees: 0);
        var straight = uprightDoor.GetInstanceParameters(upright).Single(p => p.Name == "Orientation");
        Assert.True(straight.IsReadOnly);
        Assert.Equal("Vertical", straight.DisplayValue);

        var (document, _, door) = WallWith(slantDegrees: 15);
        var parameter = door.GetInstanceParameters(document).Single(p => p.Name == "Orientation");
        Assert.False(parameter.IsReadOnly);
        Assert.True(parameter.TrySet("Slanted"));
        Assert.Equal(OpeningOrientation.Slanted, door.Orientation);
    }

    [Fact]
    public void AFrameMaterialOnTheDoorOverridesItsTypes()
    {
        var (document, _, door) = WallWith(slantDegrees: 0);
        var type = document.FindType<DoorType>(door.TypeId)!;

        // It reads the type's until it is given one of its own.
        var parameter = door.GetInstanceParameters(document).Single(p => p.Name == "Frame Material");
        Assert.Equal(type.FrameMaterial, parameter.DisplayValue);

        Assert.True(parameter.TrySet("Steel"));
        Assert.Equal("Steel", door.FrameMaterial);

        // Set back to the type's, it follows the type again rather than freezing the value.
        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Frame Material").TrySet(type.FrameMaterial));
        Assert.Equal(string.Empty, door.FrameMaterial);
    }

    [Fact]
    public void APhaseDemolishedIsNoneUntilItIsSet()
    {
        var (document, _, door) = WallWith(slantDegrees: 0);

        var parameter = door.GetInstanceParameters(document).Single(p => p.Name == "Phase Demolished");
        Assert.Equal("None", parameter.DisplayValue);

        Assert.True(parameter.TrySet("Existing"));
        Assert.Equal(DesignPhase.Existing, door.PhaseDemolished);

        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Phase Demolished").TrySet("None"));
        Assert.Null(door.PhaseDemolished);
    }

    [Fact]
    public void ADoorCanBeItsOwnSizeWithoutATypeOfItsOwn()
    {
        var (document, wall, door) = WallWith(slantDegrees: 0);
        var type = document.FindType<DoorType>(door.TypeId)!;

        var width = door.GetInstanceParameters(document).Single(p => p.Name == "Width");
        var area = door.GetInstanceParameters(document).Single(p => p.Name == "Area");
        Assert.Equal(type.Width, (double)width.Value!, precision: 6);
        Assert.Equal(type.Width * type.Height, (double)area.Value!, precision: 6);

        // Made wider, the hole in the wall is wider: the size is the door's, not a label.
        Assert.True(width.TrySet(1400.0));
        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Height").TrySet(2400.0));

        Assert.Equal(1400, door.WidthOf(type), precision: 6);
        var (from, to) = door.GetSpan(type);
        Assert.Equal(1400, to - from, precision: 6);
        Assert.Equal(1400 * 2400.0, (double)door.GetInstanceParameters(document).Single(p => p.Name == "Area").Value!, precision: 6);

        // The door alone: another of the same type is still the type's size.
        var other = new Door { TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 1000 };
        Assert.Equal(type.Width, other.WidthOf(type), precision: 6);

        // Set back to the type's size, it follows the type again.
        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Width").TrySet(type.Width));
        Assert.Null(door.WidthOverride);
    }

    [Fact]
    public void ADoorsOwnSizeCutsTheWallAndIsSaved()
    {
        var (document, wall, door) = WallWith(slantDegrees: 0);
        var type = document.FindType<DoorType>(door.TypeId)!;
        door.WidthOverride = 1400;
        door.HeightOverride = 2400;

        // The leaf fills the wider, taller hole.
        var leaf = ModelMeshBuilder.BuildWall(document, wall)
            .Single(m => m.ElementId == door.Id && m.Description == "Door leaf").Bounds()!.Value;
        Assert.True(leaf.Max.X - leaf.Min.X > type.Width, "the leaf is no wider than the type");
        Assert.True(leaf.Max.Z > type.Height);

        var path = Path.Combine(Path.GetTempPath(), "bimtest-" + Guid.NewGuid().ToString("N") + ProjectFile.Extension);
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path).Elements.OfType<Door>().Single(d => d.Id == door.Id);
            Assert.Equal(1400, copy.WidthOverride);
            Assert.Equal(2400, copy.HeightOverride);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheNewPropertiesAreSaved()
    {
        var (document, _, door) = WallWith(slantDegrees: 15);
        door.Orientation = OpeningOrientation.Slanted;
        door.FrameMaterial = "Steel, Galvanised";
        door.PhaseDemolished = DesignPhase.Existing;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var copy = ProjectFile.Load(path).Elements.OfType<Door>().Single(d => d.Id == door.Id);

            Assert.Equal(OpeningOrientation.Slanted, copy.Orientation);
            Assert.Equal("Steel, Galvanised", copy.FrameMaterial);
            Assert.Equal(DesignPhase.Existing, copy.PhaseDemolished);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
