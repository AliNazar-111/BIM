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
    public void TheHandleRisesWithTheLeafRatherThanStayingLow()
    {
        double HandleHeight(double doorHeight)
        {
            var (document, wall, door) = WallWith(slantDegrees: 0);
            door.HeightOverride = doorHeight;

            var handle = ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == door.Id && m.Description == "Hardware").Bounds()!.Value;

            return (handle.Min.Z + handle.Max.Z) / 2;
        }

        // An ordinary door keeps its handle where a hand is, about a metre up.
        Assert.Equal(1000, HandleHeight(2100), precision: 0);

        // A taller leaf carries it higher, rather than leaving it down by the floor.
        Assert.True(HandleHeight(2800) > 1200, "a 2.8 m door leaves its handle down by the floor");
        Assert.True(HandleHeight(3600) > HandleHeight(2100));

        // And on a low one it never climbs past halfway up.
        Assert.True(HandleHeight(1400) <= 700 + 1e-6);
    }

    [Fact]
    public void FlippingFacingTurnsAShutDoorRoundAsWellAsAnOpenOne()
    {
        var (document, wall, door) = WallWith(slantDegrees: 0);

        (double Leaf, double Hinges) Sides()
        {
            var meshes = ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == door.Id).ToList();
            var leaf = meshes.Single(m => m.Description == "Door leaf").Bounds()!.Value;
            var hinges = meshes.Single(m => m.Description == "Hinges").Bounds()!.Value;
            return ((leaf.Min.Y + leaf.Max.Y) / 2, (hinges.Min.Y + hinges.Max.Y) / 2);
        }

        // Shut, the leaf is hung on one side of the reveal with its hinges proud of that face.
        var facing = Sides();
        Assert.True(facing.Leaf > 0, "the leaf is not set toward the face it opens to");
        Assert.True(facing.Hinges > facing.Leaf, "the hinges are not on the side the door opens to");

        // Flipped, the whole door has gone round: leaf and hinges both on the other side. This
        // is the thing a shut door shows, and without it flipping facing looks like nothing.
        door.FlipFacing = true;
        var flipped = Sides();
        Assert.Equal(-facing.Leaf, flipped.Leaf, precision: 6);
        Assert.Equal(-facing.Hinges, flipped.Hinges, precision: 6);

        // And the hinges are on the jamb it is hung on, which is what the hand says.
        double HingeAlong()
        {
            var hinges = ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == door.Id && m.Description == "Hinges").Bounds()!.Value;
            return (hinges.Min.X + hinges.Max.X) / 2;
        }

        var hung = HingeAlong();
        door.FlipHand = true;
        Assert.True(Math.Abs(HingeAlong() - hung) > 500, "the hinges did not move to the other jamb");
    }

    [Fact]
    public void TheHandleGoesRoundWithTheLeafItIsScrewedTo()
    {
        var (document, wall, door) = WallWith(slantDegrees: 0);

        (double X, double Y) Handle()
        {
            var bounds = ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == door.Id && m.Description == "Hardware").Bounds()!.Value;

            return ((bounds.Min.X + bounds.Max.X) / 2, (bounds.Min.Y + bounds.Max.Y) / 2);
        }

        // Shut, the handle is in the doorway with the leaf.
        var shut = Handle();
        Assert.True(Math.Abs(shut.Y) < 100, "the handle is not in the leaf lying across the doorway");

        // Open, it has turned about the hinge with the leaf rather than staying behind in the
        // empty doorway - which is where a handle left in the wall's own frame would be.
        door.IsOpen = true;
        var open = Handle();
        Assert.True(Math.Abs(open.Y) > 400, $"the handle stayed in the doorway at y={open.Y:F0}");

        // It is on the leaf: within the leaf's own extent, not floating beside it.
        var leaf = ModelMeshBuilder.BuildWall(document, wall)
            .Single(m => m.ElementId == door.Id && m.Description == "Door leaf").Bounds()!.Value;

        Assert.InRange(open.X, leaf.Min.X - 80, leaf.Max.X + 80);
        Assert.InRange(open.Y, leaf.Min.Y - 80, leaf.Max.Y + 80);

        door.IsOpen = false;
        Assert.Equal(shut.X, Handle().X, precision: 6);
    }

    [Fact]
    public void ADoorDrawnOpenSwingsOutOfItsDoorway()
    {
        var (document, wall, door) = WallWith(slantDegrees: 0);
        var type = document.FindType<DoorType>(door.TypeId)!;

        (double Across, double Along) LeafReach()
        {
            var leaf = ModelMeshBuilder.BuildWall(document, wall)
                .Single(m => m.ElementId == door.Id && m.Description == "Door leaf").Bounds()!.Value;

            return (leaf.Max.Y - leaf.Min.Y, leaf.Max.X - leaf.Min.X);
        }

        // Shut, the leaf lies across the doorway and is only as thick as a door.
        var shut = LeafReach();
        Assert.True(shut.Across < 100, "the leaf is not lying in its doorway");
        Assert.True(shut.Along > type.Width - 150);

        // Open, it has swung out of the way: it now reaches across the wall, not along it.
        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Open").TrySet(true));
        var open = LeafReach();
        Assert.True(open.Across > type.Width - 200, "the leaf did not swing out of the doorway");
        Assert.True(open.Along < shut.Along / 2);

        // Shut again, it is back where it was.
        Assert.True(door.GetInstanceParameters(document).Single(p => p.Name == "Open").TrySet(false));
        Assert.Equal(shut.Across, LeafReach().Across, precision: 6);
    }

    [Fact]
    public void APairOfDoorsSwingsApart()
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);

        var pair = document.TypesOf<DoorType>().First(t => t.Operation == DoorOperation.DoubleSwing);
        var door = new Door { TypeId = pair.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, IsOpen = true };
        document.Add(door);

        // Hung one leaf on each jamb, so between them they reach the full width of the hole
        // out from the wall rather than both folding to the same side.
        var leaf = ModelMeshBuilder.BuildWall(document, wall)
            .Single(m => m.ElementId == door.Id && m.Description == "Door leaf").Bounds()!.Value;

        Assert.True(leaf.Max.Y - leaf.Min.Y > pair.Width / 2 - 150, "the leaves did not swing out");
        Assert.True(leaf.Max.X - leaf.Min.X > pair.Width - 250, "the leaves are not hung on opposite jambs");
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
