using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>Doors and windows in 3D are built as what their type says they are.</summary>
public class OpeningModelTests
{
    /// <summary>A 6 m exterior wall along X, exterior toward +Y, and one opening of the given type in its middle.</summary>
    private static (BimDocument Document, Wall Wall, Opening Opening, double WallHalf) WallWith(OpeningType type)
    {
        var document = BimDocument.CreateDefault();
        var wallType = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        document.AddType(type);

        Opening opening = type is DoorType ? new Door() : new BimWindow { SillHeight = 900 };
        opening.TypeId = type.Id;
        opening.LevelId = wall.LevelId;
        opening.HostWallId = wall.Id;
        opening.DistanceAlongWall = 3000;
        document.Add(opening);

        return (document, wall, opening, wallType.Width / 2);
    }

    private static List<Mesh3D> MeshesOf(BimDocument document, Wall wall, Opening opening) =>
        ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == opening.Id).ToList();

    private static (Point3D Min, Point3D Max) Bounds(IEnumerable<Mesh3D> meshes)
    {
        var points = meshes.SelectMany(m => m.Positions).ToList();
        return (new Point3D(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
                new Point3D(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    [Fact]
    public void ASwingDoorHasAFrameALeafAndHandles()
    {
        var (document, wall, door, _) = WallWith(new DoorType("Swing", 900, 2100) { Operation = DoorOperation.Swing, PanelMaterial = "Timber" });
        var meshes = MeshesOf(document, wall, door);

        Assert.Contains(meshes, m => m.Description == "Door frame");
        Assert.Contains(meshes, m => m.Description == "Door leaf");
        Assert.Contains(meshes, m => m.Description == "Hardware");

        // The leaf stands in the opening, floor to just under the head.
        var leaf = meshes.Single(m => m.Description == "Door leaf").Bounds()!.Value;
        Assert.True(leaf.Min.X >= 2550 - 1e-6 && leaf.Max.X <= 3450 + 1e-6);
        Assert.Equal(0, leaf.Min.Z, precision: 6);
    }

    [Fact]
    public void AGlazedDoorHasGlassInIt()
    {
        var (document, wall, door, _) = WallWith(new DoorType("Glazed", 900, 2100) { PanelMaterial = "Oak, Glazed", LeafDesign = DoorLeafDesign.Glazed });
        Assert.Contains(MeshesOf(document, wall, door), m => m.Kind == MeshKind.Glazing);
    }

    [Fact]
    public void ATwinSliderHasTwoLeavesOnTwoTracks()
    {
        var (document, wall, door, _) = WallWith(new DoorType("Twin", 1800, 2100) { Operation = DoorOperation.Sliding, LeafCount = 2 });
        var leaf = MeshesOf(document, wall, door).Single(m => m.Description == "Door leaf").Bounds()!.Value;

        // One track each side of the centreline, so the leaves can pass: 25 off it, 40 thick.
        Assert.Equal(-45, leaf.Min.Y, precision: 6);
        Assert.Equal(45, leaf.Max.Y, precision: 6);
    }

    [Fact]
    public void ASingleSliderHangsOnTheFaceOfTheWall()
    {
        var (document, wall, door, half) = WallWith(new DoorType("Slider", 900, 2100) { Operation = DoorOperation.Sliding, LeafCount = 1 });
        var meshes = MeshesOf(document, wall, door);

        var leaf = meshes.Single(m => m.Description == "Door leaf").Bounds()!.Value;
        Assert.True(leaf.Max.Y < -half, "The leaf is on the room side of the wall, clear of it.");

        // Its track runs on past the opening, over the wall it parks against.
        var track = meshes.Single(m => m.Description == "Hardware").Bounds()!.Value;
        Assert.True(track.Max.X > 3450 + 700, $"The track stops at {track.Max.X:0}.");
    }

    [Fact]
    public void AFoldingDoorZigZags()
    {
        var (document, wall, door, _) = WallWith(new DoorType("Folding", 1800, 2100) { Operation = DoorOperation.Folding, LeafCount = 2 });
        var leaf = MeshesOf(document, wall, door).Single(m => m.Description == "Door leaf").Bounds()!.Value;
        Assert.True(leaf.Max.Y - leaf.Min.Y > 100, "The panels fold either side of the line.");
    }

    [Fact]
    public void ARevolvingDoorStandsOutOfTheWallBothWays()
    {
        var (document, wall, door, half) = WallWith(new DoorType("Revolving", 2000, 2200) { Operation = DoorOperation.Revolving });
        var glass = MeshesOf(document, wall, door).Single(m => m.Kind == MeshKind.Glazing).Bounds()!.Value;

        Assert.True(glass.Max.Y > half && glass.Min.Y < -half);
    }

    [Fact]
    public void AnOverheadDoorIsHorizontalSections()
    {
        var (document, wall, door, _) = WallWith(new DoorType("Garage", 2400, 2100) { Operation = DoorOperation.Overhead });
        var leaf = MeshesOf(document, wall, door).Single(m => m.Description == "Door leaf");

        // Four or more boxes stacked: every section adds its own top and bottom.
        var levels = leaf.Positions.Select(p => Math.Round(p.Z)).Distinct().Count();
        Assert.True(levels >= 8, $"Only {levels} heights: the sections are not separate.");
    }

    [Theory]
    [InlineData(WindowOperation.Fixed, false)]
    [InlineData(WindowOperation.Casement, true)]
    [InlineData(WindowOperation.Awning, true)]
    [InlineData(WindowOperation.TiltAndTurn, true)]
    [InlineData(WindowOperation.Sliding, true)]
    public void AWindowHasSashesOnlyIfItOpens(WindowOperation operation, bool opens)
    {
        var (document, wall, window, _) = WallWith(new WindowType("W", 1200, 1200) { Operation = operation, FrameMaterial = "uPVC" });
        var meshes = MeshesOf(document, wall, window);

        Assert.Contains(meshes, m => m.Description == "Window frame");
        Assert.Contains(meshes, m => m.Kind == MeshKind.Glazing);
        Assert.Equal(opens, meshes.Any(m => m.Description == "Sash"));
        Assert.Equal(opens, meshes.Any(m => m.Description == "Hardware"));
    }

    [Fact]
    public void ASlidingWindowsSashesSitOnTwoTracks()
    {
        var (document, wall, window, _) = WallWith(new WindowType("Slider", 1800, 1200) { Operation = WindowOperation.Sliding });
        var sash = MeshesOf(document, wall, window).Single(m => m.Description == "Sash").Bounds()!.Value;

        Assert.Equal(-52, sash.Min.Y, precision: 6);
        Assert.Equal(52, sash.Max.Y, precision: 6);
    }

    [Fact]
    public void ABayWindowStandsOutFromTheWall()
    {
        var (document, wall, window, half) = WallWith(new WindowType("Bay", 2400, 1500) { Operation = WindowOperation.Bay });
        var glass = MeshesOf(document, wall, window).Single(m => m.Kind == MeshKind.Glazing).Bounds()!.Value;

        Assert.True(glass.Max.Y > half + 300, "The bay projects outside the wall.");
    }

    [Fact]
    public void FramesTakeTheColourOfWhatTheyAreMadeOf()
    {
        Assert.Equal(new BIMDesigner.Core.Materials.ColourRgb(0xEE, 0xEE, 0xEA),
            WallWithFrame("uPVC"));
        Assert.Equal(new BIMDesigner.Core.Materials.ColourRgb(0xB4, 0xB8, 0xBE),
            WallWithFrame("Aluminium, thermally broken"));

        static BIMDesigner.Core.Materials.ColourRgb WallWithFrame(string material)
        {
            var (document, wall, window, _) = WallWith(new WindowType("W", 1200, 1200) { FrameMaterial = material });
            return MeshesOf(document, wall, window).Single(m => m.Description == "Window frame").Colour;
        }
    }
}
