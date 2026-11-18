using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Reaction to fire (round 89), as Approved Document B Volume 1 has it: a room's linings by Table 4.1 and 4.4; an outside wall's surface by
/// Table 10.1, its insulation by 10.6 and 10.7, and in a relevant building every material by Regulation 7(2) - each material's class by
/// BS EN 13501-1, its maker's or the one its name has without testing.
/// </summary>
[Collection("Wpf")]
public class CombustibilityTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    /// <summary>Four walls round a rectangle, drawn clockwise so their outsides - layer 0 - face out, as a house drawn in the plan is.</summary>
    private static List<Wall> Box(BimDocument document, Guid level, double x0, double y0, double x1, double y1, string type = Brick)
    {
        var corners = new[] { new Point2D(x0, y0), new Point2D(x0, y1), new Point2D(x1, y1), new Point2D(x1, y0) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        return walls;
    }

    /// <summary>A copy of a wall type with its outside or inside face of another material.</summary>
    private static WallType Faced(BimDocument document, string type, string material, bool outside)
    {
        var copy = (WallType)document.TypesOf<WallType>().First(each => each.Name == type).Duplicate($"{type} - {material}{(outside ? " out" : " in")}");
        var layers = copy.Structure.Layers;
        (outside ? layers[0] : layers[^1]).MaterialId = document.Materials.First(each => each.Name == material).Id;
        document.AddType(copy);
        return copy;
    }

    private static Material Declared(BimDocument document, string name, string fire)
    {
        var material = new Material(name) { Density = 30, ReactionToFire = fire };
        document.AddMaterial(material);
        return material;
    }

    [Fact]
    public void EuroclassesAreReadAndRankedAndSomeAreKnownByName()
    {
        Assert.True(Euroclass.TryParse("a2-s1,d0", out var a2));
        Assert.Equal("A2-s1, d0", a2.ToString());
        Assert.True(Euroclass.TryParse("B-s3, d2", out var b));
        Assert.True(b.AtLeast(Euroclass.Cs3d2));
        Assert.False(Euroclass.Cs3d2.AtLeast(b));
        Assert.True(Euroclass.TryParse("C-s1, d0", out var c));
        Assert.True(c.AtLeast(Euroclass.Cs3d2));
        Assert.False(c.AtLeast(Euroclass.Bs3d2));
        Assert.True(Euroclass.A1.AtLeast(Euroclass.A2s1d0));
        Assert.True(Euroclass.TryParse("E", out var e));
        Assert.False(e.AtLeast(Euroclass.Ds3d2));
        Assert.Equal("E-d2", Euroclass.TryParse("E-d2", out var ed2) ? ed2.ToString() : null);
        Assert.False(Euroclass.TryParse("G", out _));
        Assert.False(Euroclass.TryParse("B-s4, d0", out _));

        // Known by name, without testing; not where the maker's class is wanted.
        Assert.Equal(Euroclass.A2s1d0, Euroclass.Typical("Gypsum Plasterboard")!.Value.Class);
        Assert.Equal(Euroclass.A1, Euroclass.Typical("Brick, Common")!.Value.Class);
        Assert.Equal("D-s2, d2", Euroclass.Typical("Timber, Western Red Cedar")!.Value.Class.ToString());
        Assert.Equal("D-s2, d0", Euroclass.Typical("Timber, Softwood")!.Value.Class.ToString());
        Assert.Null(Euroclass.Typical("Rigid Insulation"));
        Assert.Null(Euroclass.Typical("Steel, Colour Coated"));
        Assert.Equal("E", Euroclass.Of(new Material("Rigid Insulation") { ReactionToFire = "E" })!.Value.Class.ToString());
    }

    [Fact]
    public void ARoomsLiningsAreCs3d2WithSomeOfItsWallsLower()
    {
        // A bedroom 4 m square in brick on block walls, plastered: 3.67 m inside, 13.5 m².
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var walls = Box(document, level, 0, 0, 4000, 4000);
        var room = new Room { LevelId = level, Location = new Point2D(2000, 2000), Name = "Bedroom" };
        document.Add(room);
        Assert.Equal("Linings: C-s3, d2 asked of a room, met - walls of Cement Plaster A1", Combustibility.LiningVerdict(document, room));

        // One wall lined with softwood, D-s2, d0: 3.67 m by 3 m, 11.0 m², past 4.4's half the floor - 6.7 m².
        walls[0].TypeId = Faced(document, Brick, "Timber, Softwood", outside: false).Id;
        var lined = Combustibility.LiningVerdict(document, room)!;
        Assert.StartsWith("Linings: 11.0 m² of its walls - the Timber, Softwood - is below C-s3, d2, Approved Document B (4.4) asks less than 6.7 m² of a room's walls", lined);
        Assert.Contains(Warnings.RoomShortfalls(document, room), said => said.StartsWith("Linings - 11.0 m² of its walls", StringComparison.Ordinal));

        // A bigger room, 8 m by 6 m - 7.67 by 5.67 inside, 43.5 m²: the same 6 m wall's 17.0 m² under 4.4's 20 m².
        var big = BimDocument.CreateDefault();
        var bigWalls = Box(big, big.Levels[0].Id, 0, 0, 8000, 6000);
        bigWalls[0].TypeId = Faced(big, Brick, "Timber, Softwood", outside: false).Id;
        var living = new Room { LevelId = big.Levels[0].Id, Location = new Point2D(4000, 3000), Name = "Living Room" };
        big.Add(living);
        Assert.StartsWith("Linings: C-s3, d2 asked of a room, met", Combustibility.LiningVerdict(big, living));

        // A foam panel of class E on a wall: worse than D-s3, d2, which no part may be.
        Declared(document, "Acoustic Panel, Foam", "E");
        walls[1].TypeId = Faced(document, Brick, "Acoustic Panel, Foam", outside: false).Id;
        Assert.Contains("the Acoustic Panel, Foam on its walls is E - Approved Document B (Table 4.1, 4.4) asks C-s3, d2 of a room's linings, and no part of them worse than D-s3, d2", Combustibility.LiningVerdict(document, room));
    }

    [Fact]
    public void ASmallRoomTheCommonPartsAndTheCeilingAskTheirOwn()
    {
        // A cupboard 2 m square, 1.67 m inside - 2.8 m², 4 m² or less: D-s3, d2, so softwood all round will do.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var timber = Faced(document, Brick, "Timber, Softwood", outside: false);
        foreach (var wall in Box(document, level, 0, 0, 2000, 2000)) wall.TypeId = timber.Id;
        var store = new Room { LevelId = level, Location = new Point2D(1000, 1000), Name = "Store" };
        document.Add(store);
        Assert.Equal("Linings: D-s3, d2 asked of a room of 4 m² or less, met - walls of Timber, Softwood D-s2, d0", Combustibility.LiningVerdict(document, store));

        // A ceiling of polystyrene tiles, class E, over it: wanting.
        var tiles = Declared(document, "Polystyrene Ceiling Tile", "E");
        var tiled = new CeilingType("Ceiling - Polystyrene Tiles", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, tiles.Id, 12)));
        document.AddType(tiled);
        var ceiling = new Ceiling { LevelId = level, TypeId = tiled.Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 2000), new Point2D(0, 2000) });
        document.Add(ceiling);
        Assert.Contains("its ceiling's Polystyrene Ceiling Tile is E - Approved Document B (Table 4.1) asks D-s3, d2", Combustibility.LiningVerdict(document, store));

        // A corridor of a block of flats, 10 m by 2 m, plastered: B-s3, d2 asked, A1 met; lined with softwood all round, not.
        var block = BimDocument.CreateDefault();
        var floor = block.Levels[0].Id;
        var corridorWalls = Box(block, floor, 0, 0, 10000, 2000);
        var corridor = new Room { LevelId = floor, Location = new Point2D(5000, 1000), Name = "Corridor" };
        block.Add(corridor);
        Box(block, floor, 0, 3000, 6000, 8000);
        block.Add(new Room { LevelId = floor, Location = new Point2D(3000, 5500), Name = "Living Room", Dwelling = "Flat 1" });
        Assert.StartsWith("Linings: B-s3, d2 asked of a circulation space of the common parts, met", Combustibility.LiningVerdict(block, corridor));
        var softwood = Faced(block, Brick, "Timber, Softwood", outside: false);
        foreach (var wall in corridorWalls) wall.TypeId = softwood.Id;
        Assert.Contains("is below B-s3, d2, Approved Document B (4.4)", Combustibility.LiningVerdict(block, corridor));
    }

    /// <summary>
    /// A house 8 m by 6 m of brick on block, a partition across its middle - where the south wall's own middle meets it - a room either side,
    /// and the site's boundary so far out from its south wall's outside face.
    /// </summary>
    private static (BimDocument Document, List<Wall> Walls) House(double boundary)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var walls = Box(document, level, 0, 0, 8000, 6000);
        document.Add(new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 6000), TypeId = document.TypesOf<WallType>().First(each => each.Name == "Interior - Partition 125mm").Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(2000, 3000), Name = "Living Room" });
        document.Add(new Room { LevelId = level, Location = new Point2D(6000, 3000), Name = "Kitchen" });
        var line = new PropertyLine { LevelId = level };
        line.SetBoundary(new[] { new Point2D(-5000, -165 - boundary), new Point2D(13000, -165 - boundary), new Point2D(13000, 20000), new Point2D(-5000, 20000) });
        document.Add(line);
        return (document, walls);
    }

    [Fact]
    public void AnOutsideFaceWithin1MetreOfTheBoundaryIsBs3d2()
    {
        // Brick, A1, 0.5 m from the boundary: met. Clad in cedar, D-s2, d2: not.
        var (document, walls) = House(500);
        var south = walls.Single(wall => Math.Abs(wall.Start.Y) < 1 && Math.Abs(wall.End.Y) < 1);
        Assert.Empty(Combustibility.Problems(document, south, ClearanceStandard.ApprovedDocumentM));
        south.TypeId = Faced(document, Brick, "Timber, Western Red Cedar", outside: true).Id;
        var (said, rule) = Assert.Single(Combustibility.Problems(document, south, ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("its outside face is Timber, Western Red Cedar, D-s2, d2, 0.5 m from the boundary.", said);
        Assert.Equal("Approved Document B (10.5, Table 10.1) asks the outside surface of a home's building 11 m high or less, within 1 m of the boundary to be class B-s3, d2 or better - or steel sheet 0.5 mm thick with an organic coating 0.2 mm at most.", rule);
        Assert.Contains(WallRules.Problems(document, south, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == said);

        // 2 m from the boundary: Table 10.1 asks nothing of a house's outside face.
        (document, walls) = House(2000);
        south = walls.Single(wall => Math.Abs(wall.Start.Y) < 1 && Math.Abs(wall.End.Y) < 1);
        south.TypeId = Faced(document, Brick, "Timber, Western Red Cedar", outside: true).Id;
        Assert.Empty(Combustibility.Problems(document, south, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void ARelevantBuildingsOutsideWallsAreA2s1d0ThroughAndTallerHomesInsulationToo()
    {
        // A flat 18 m up over the house: a relevant building. Its walls' brick, block and plaster are A1; the rigid insulation's class not given.
        var (document, walls) = House(5000);
        var top = new Level { Name = "Sixth Floor", Elevation = 18000 };
        document.AddLevel(top);
        Box(document, top.Id, 0, 0, 8000, 6000);
        document.Add(new Room { LevelId = top.Id, Location = new Point2D(4000, 3000), Name = "Living Room", Dwelling = "Flat 7" });
        var (said, rule) = Assert.Single(Combustibility.Problems(document, walls[0], ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("its Rigid Insulation, of no class given.", said);
        Assert.StartsWith("Regulation 7(2) asks every material of an outside wall of a building holding homes with a storey 18 m or more above the ground", rule);

        // Its insulation declared A2-s1, d0: met - and the class kept when the project is saved and opened again.
        document.Materials.First(material => material.Name == "Rigid Insulation").ReactionToFire = "A2-s1, d0";
        Assert.Empty(Combustibility.Problems(document, walls[0], ClearanceStandard.ApprovedDocumentM));
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal("A2-s1, d0", opened.Materials.First(material => material.Name == "Rigid Insulation").ReactionToFire);
        Assert.Equal(string.Empty, opened.Materials.First(material => material.Name == "Brick, Common").ReactionToFire);

        // A home 12 m up: 10.7 asks the insulation A2-s1, d0 - but not of a masonry cavity wall, which brick on block is.
        (document, walls) = House(5000);
        var fourth = new Level { Name = "Fourth Floor", Elevation = 12000 };
        document.AddLevel(fourth);
        document.Add(new Room { LevelId = fourth.Id, Location = new Point2D(4000, 3000), Name = "Bedroom" });
        Assert.True(Combustibility.MasonryCavity(document, document.GetWallType(walls[0])!.Structure));
        Assert.Empty(Combustibility.Problems(document, walls[0], ClearanceStandard.ApprovedDocumentM));

        // Its inner leaf plasterboard instead of block: no masonry cavity wall, and the insulation's class not given.
        var framed = (WallType)document.GetWallType(walls[0])!.Duplicate("Exterior - Brick on Board");
        var leaf = framed.Structure.Layers.First(layer => document.FindMaterial(layer.MaterialId)?.Name == "Aircrete Block");
        leaf.MaterialId = document.Materials.First(material => material.Name == "Gypsum Plasterboard").Id;
        document.AddType(framed);
        walls[0].TypeId = framed.Id;
        Assert.False(Combustibility.MasonryCavity(document, framed.Structure));
        (said, rule) = Assert.Single(Combustibility.Problems(document, walls[0], ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("its insulation is Rigid Insulation, of no class given.", said);
        Assert.StartsWith("Approved Document B (10.7) asks the insulation and filling of an outside wall of a home's building with a storey 11 m or more above the ground to be class A2-s1, d0 or better", rule);
    }
}
