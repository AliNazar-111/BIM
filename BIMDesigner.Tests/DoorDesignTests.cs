using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Door designs, trim, marks, flipping and moving a door to another wall.</summary>
public class DoorDesignTests
{
    private static (BimDocument Document, Wall Wall, Door Door) WallWith(DoorType type)
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

        var door = new Door { TypeId = type.Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        return (document, wall, door);
    }

    private static List<Mesh3D> MeshesOf(BimDocument document, Wall wall, Door door) =>
        ModelMeshBuilder.BuildWall(document, wall).Where(m => m.ElementId == door.Id).ToList();

    private static int Triangles(IEnumerable<Mesh3D> meshes, MeshKind kind) =>
        meshes.Where(m => m.Kind == kind).Sum(m => m.TriangleCount);

    [Theory]
    [InlineData(DoorLeafDesign.Flush, false)]
    [InlineData(DoorLeafDesign.Panelled, false)]
    [InlineData(DoorLeafDesign.Glazed, true)]
    [InlineData(DoorLeafDesign.FrenchGlazed, true)]
    [InlineData(DoorLeafDesign.HalfGlazed, true)]
    [InlineData(DoorLeafDesign.Louvred, false)]
    [InlineData(DoorLeafDesign.ArchedTopLight, true)]
    public void EachDesignIsBuiltAndHasGlassOnlyWhereItShould(DoorLeafDesign design, bool glazed)
    {
        var (document, wall, door) = WallWith(new DoorType("Test", 900, 2100) { LeafDesign = design });
        var meshes = MeshesOf(document, wall, door);

        Assert.True(Triangles(meshes, MeshKind.DoorLeaf) > 0);
        Assert.Equal(glazed, Triangles(meshes, MeshKind.Glazing) > 0);

        // Whatever the design, the leaf stays in the doorway, floor to head.
        var leaf = meshes.Single(m => m.Description == "Door leaf").Bounds()!.Value;
        Assert.True(leaf.Min.X >= 2550 - 1e-6 && leaf.Max.X <= 3450 + 1e-6, $"{design} leaf runs {leaf.Min.X:0}..{leaf.Max.X:0}");
        Assert.True(leaf.Max.Z <= 2100 + 1e-6);
    }

    [Fact]
    public void DesignsDifferInDetail()
    {
        int LeafTriangles(DoorLeafDesign design)
        {
            var (document, wall, door) = WallWith(new DoorType("Test", 900, 2100) { LeafDesign = design });
            return Triangles(MeshesOf(document, wall, door), MeshKind.DoorLeaf);
        }

        // Panels and bars and slats are there: each design has more to it than a flush slab.
        var flush = LeafTriangles(DoorLeafDesign.Flush);
        Assert.True(LeafTriangles(DoorLeafDesign.Panelled) > flush);
        Assert.True(LeafTriangles(DoorLeafDesign.FrenchGlazed) > LeafTriangles(DoorLeafDesign.Glazed));
        Assert.True(LeafTriangles(DoorLeafDesign.Louvred) > LeafTriangles(DoorLeafDesign.Panelled));
    }

    [Fact]
    public void TrimStandsOffEachFaceByItsProjection()
    {
        var (document, wall, door) = WallWith(new DoorType("Test", 900, 2100) { TrimWidth = 80, TrimProjectionExterior = 30, TrimProjectionInterior = 15 });
        var half = document.GetWallType(wall)!.Width / 2;
        var frame = MeshesOf(document, wall, door).Single(m => m.Description == "Door frame").Bounds()!.Value;

        Assert.Equal(half + 30, frame.Max.Y, precision: 6);
        Assert.Equal(-half - 15, frame.Min.Y, precision: 6);
        Assert.Equal(2550 - 80, frame.Min.X, precision: 6);
        Assert.Equal(2100 + 80, frame.Max.Z, precision: 6);
    }

    [Fact]
    public void DesignsAreSavedAndOldProjectsTakeTheirsFromTheMaterial()
    {
        var (document, _, door) = WallWith(new DoorType("Entrance", 1000, 2300)
        {
            LeafDesign = DoorLeafDesign.ArchedTopLight, GlazingRows = 3, Function = DoorFunction.Exterior, TrimWidth = 95
        });
        door.FrameType = "Timber lining";
        door.Finish = "Oiled";

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var loaded = ProjectFile.Load(path);
            var type = loaded.TypesOf<DoorType>().Single(t => t.Name == "Entrance");
            Assert.Equal(DoorLeafDesign.ArchedTopLight, type.LeafDesign);
            Assert.Equal(3, type.GlazingRows);
            Assert.Equal(DoorFunction.Exterior, type.Function);
            Assert.Equal(95, type.TrimWidth);
            var copy = loaded.Elements.OfType<Door>().Single(d => d.Id == door.Id);
            Assert.Equal("Timber lining", copy.FrameType);
            Assert.Equal("Oiled", copy.Finish);

            // A project from before designs: the design is left out, and glass in the material says glazed.
            var json = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path), "\"leafDesign\"", "\"unused\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            File.WriteAllText(path, json);
            var old = ProjectFile.Load(path);
            Assert.Equal(DoorLeafDesign.Glazed, old.TypesOf<DoorType>().Single(t => t.PanelMaterial.Contains("Glazed") && t.Name.StartsWith("Sliding Twin")).LeafDesign);
            Assert.Equal(DoorLeafDesign.Panelled, old.TypesOf<DoorType>().Single(t => t.Name == "Entrance").LeafDesign);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MarksCountUpAndHeadHeightMovesTheSill()
    {
        var (document, _, door) = WallWith(new DoorType("Test", 900, 2100));
        Assert.Equal("1", OpeningMarks.Next<Door>(document));

        door.Mark = "7";
        Assert.Equal("8", OpeningMarks.Next<Door>(document));

        var head = door.GetInstanceParameters(document).Single(p => p.Name == "Head Height");
        Assert.True(head.TrySet(2400.0));
        Assert.Equal(300, door.SillHeight, precision: 6);
    }

    [Fact]
    public void FlippingAndPickingANewHostAreUndoable()
    {
        var (document, wall, door) = WallWith(new DoorType("Test", 900, 2100));

        var flip = new FlipOpeningCommand(door, facing: true);
        flip.Redo();
        Assert.True(door.FlipFacing);
        flip.Undo();
        Assert.False(door.FlipFacing);

        var other = new Wall { Start = new Point2D(0, 4000), End = new Point2D(5000, 4000), TypeId = wall.TypeId, LevelId = wall.LevelId, UnconnectedHeight = 3000 };
        document.Add(other);

        var rehost = new RehostOpeningCommand(door, other, 1200);
        rehost.Redo();
        Assert.Equal(other.Id, door.HostWallId);
        Assert.Equal(1200, door.DistanceAlongWall);
        Assert.Contains(door, WallOpenings.Of(document, other));

        rehost.Undo();
        Assert.Equal(wall.Id, door.HostWallId);
        Assert.Equal(3000, door.DistanceAlongWall);
    }
}
