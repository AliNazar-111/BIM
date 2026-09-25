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
    public void ASlidingWindowRunsOneSashAcrossTheOther()
    {
        var (document, wall, window, _) = WallWith(new WindowType("Slider", 1800, 1200) { Operation = WindowOperation.Sliding });

        (double Min, double Max) Handle()
        {
            var bounds = MeshesOf(document, wall, window).Single(m => m.Description == "Hardware").Bounds()!.Value;
            return (bounds.Min.X, bounds.Max.X);
        }

        // Shut, the two sashes meet in the middle and the catch is there with them.
        var shut = Handle();
        Assert.True(Math.Abs((shut.Min + shut.Max) / 2 - 3000) < 100, "the catch is not on the meeting stile");

        // Open, the sash the handle is on has run along its track toward the far jamb, which
        // is the whole of what a slider does. Half the window is left standing clear.
        window.IsOpen = true;
        var open = Handle();
        Assert.True(open.Max < shut.Min, $"the sash did not slide: the catch went from {shut.Min:F0} to {open.Min:F0}");
        Assert.True(shut.Min - open.Min > 300, "the sash barely moved");

        // And it is still a window in its hole, not something hanging out of the wall.
        var sash = MeshesOf(document, wall, window).Single(m => m.Description == "Sash").Bounds()!.Value;
        Assert.True(sash.Min.X > 3000 - 1800 / 2.0 - 1, "the sash ran out through the jamb");

        window.IsOpen = false;
        Assert.Equal(shut.Min, Handle().Min, precision: 6);
    }

    [Fact]
    public void ADoubleHungWindowPushesItsLowerSashUp()
    {
        var (document, wall, window, _) = WallWith(new WindowType("Sash", 1000, 1400) { Operation = WindowOperation.DoubleHung });

        double Catch() => MeshesOf(document, wall, window).Single(m => m.Description == "Hardware").Bounds()!.Value.Min.Z;

        var shut = Catch();
        window.IsOpen = true;
        Assert.True(Catch() - shut > 300, "the lower sash did not rise");
    }

    [Fact]
    public void ACasementsCatchGoesRoundWithItsSash()
    {
        var (document, wall, window, _) = WallWith(new WindowType("Casement", 900, 1200) { Operation = WindowOperation.Casement });

        double Catch()
        {
            var bounds = MeshesOf(document, wall, window).Single(m => m.Description == "Hardware").Bounds()!.Value;
            return (bounds.Min.Y + bounds.Max.Y) / 2;
        }

        // Shut, the catch is on the sash lying in the opening.
        Assert.True(Math.Abs(Catch()) < 120, "the catch is not on the shut sash");

        // Open, it has swung out with the sash rather than staying in the empty hole.
        window.IsOpen = true;
        var open = Catch();
        Assert.True(Math.Abs(open) > 400, $"the catch stayed in the opening at y={open:F0}");

        var sash = MeshesOf(document, wall, window).Single(m => m.Description == "Sash").Bounds()!.Value;
        Assert.InRange(open, sash.Min.Y - 60, sash.Max.Y + 60);
    }

    [Fact]
    public void GlazingBarsGoOutWithTheSashTheyDivide()
    {
        double FrameReach(int rows, int columns, bool open)
        {
            var (document, wall, window, _) = WallWith(new WindowType("Georgian", 900, 1200)
            {
                Operation = WindowOperation.Casement, GlazingRows = rows, GlazingColumns = columns
            });

            window.IsOpen = open;
            var frame = MeshesOf(document, wall, window).Single(m => m.Description == "Window frame").Bounds()!.Value;
            return frame.Max.Y - frame.Min.Y;
        }

        // Shut, the bars lie in the sash, well inside the frame and the sill board.
        var plain = FrameReach(1, 1, open: false);
        Assert.Equal(plain, FrameReach(3, 2, open: false), precision: 6);

        // Open, a window with no bars is no deeper than before - and one with bars has taken
        // them out with the sash instead of leaving them hanging across the empty hole.
        Assert.Equal(plain, FrameReach(1, 1, open: true), precision: 6);
        Assert.True(FrameReach(3, 2, open: true) > plain + 300, "the bars stayed behind in the opening");
    }

    [Fact]
    public void APairOfCasementsIsHungOnOppositeJambs()
    {
        var (document, wall, window, _) = WallWith(new WindowType("Pair", 1600, 1200) { Operation = WindowOperation.Casement });
        window.IsOpen = true;

        // Hung one on each jamb, the two sashes swing apart: between them they reach almost the
        // whole width of the opening, rather than folding to the same side one behind the other.
        var sash = MeshesOf(document, wall, window).Single(m => m.Description == "Sash").Bounds()!.Value;
        Assert.True(sash.Max.X - sash.Min.X > 1600 - 400, "the sashes are hung the same way round");

        // And a catch on each of them, both out of the wall with their sashes.
        var hardware = MeshesOf(document, wall, window).Single(m => m.Description == "Hardware").Bounds()!.Value;
        Assert.True(hardware.Max.X - hardware.Min.X > 600, "the two catches are not on opposite sashes");
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
