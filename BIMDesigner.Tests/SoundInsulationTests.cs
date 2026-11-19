using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Sound between dwellings by Approved Document E (round 99): walls read as Section 2's types by their mass from Annex A, a party wall's
/// junctions with the outside walls, internal walls and floors, a separating floor by Section 3 and its outside walls, a flat's entrance door
/// (2.26) and the common parts' absorption by Method A (E3, Section 7).
/// </summary>
[Collection("Wpf")]
public class SoundInsulationTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y, string dwelling = "")
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name, Dwelling = dwelling };
        document.Add(room);
        return room;
    }

    private static T Slab<T>(BimDocument document, Guid level, string type, double x0, double y0, double x1, double y1, double offset = 0) where T : Slab, new()
    {
        var slab = new T { LevelId = level, TypeId = document.TypesOf<SlabType>().First(each => each.Name == type).Id, HeightOffset = offset };
        slab.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(slab);
        return slab;
    }

    private static Material Material(BimDocument document, string name, double density, double length = 0, double height = 0, double joint = 0)
    {
        var material = new Material(name) { Density = density, UnitLength = length, UnitHeight = height, JointWidth = joint };
        document.AddMaterial(material);
        return material;
    }

    private static Material Named(BimDocument document, string name) => document.Materials.First(material => material.Name == name);

    private static WallType Type(string name, params (LayerFunction Function, Material Material, double Thickness)[] layers) =>
        new(name, new CompoundStructure(layers.Select(layer => new MaterialLayer(layer.Function, layer.Material.Id, layer.Thickness)).ToArray())) { Separating = true };

    private static List<PlacementProblem> E(BimDocument document, Wall wall) =>
        WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (", StringComparison.Ordinal) && !problem.Rule.Contains("(E2")).ToList();

    private static List<PlacementProblem> E(BimDocument document, Floor floor) =>
        SlabRules.Problems(document, floor, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (", StringComparison.Ordinal) && !problem.Rule.Contains("Table 0.2")).ToList();

    [Fact]
    public void WallsAreReadAsSection2DescribesThem()
    {
        var document = BimDocument.CreateDefault();

        // Annex A, Table A.1: blocks laid flat, a 440 by 100 face, 10 joints of mortar at 1800 kg/m³, 215 thick - a 16 kg block 366 kg/m², a 6 kg one 164.
        var heavy = Material(document, "Concrete Block, Table A.1 16 kg", 16 / (0.44 * 0.1 * 0.215), 440, 100, 10);
        var light = Material(document, "Concrete Block, Table A.1 6 kg", 6 / (0.44 * 0.1 * 0.215), 440, 100, 10);
        Assert.Equal(366, SoundInsulation.Mass(document, CompoundStructure.Single(heavy.Id, 215)), 0);
        Assert.Equal(164, SoundInsulation.Mass(document, CompoundStructure.Single(light.Id, 215)), 0);

        // The library's party walls: lightweight block at 1400 in 440 x 215 units with 10 joints at 1800, 142.6 kg/m² a leaf, 2 x 24.7 of plaster and the cavity's air - 334.7 kg/m², a 75 cavity - type 2.2; dense block at 1990 likewise, 445.0 - type 2.1.
        var lightweight = SoundInsulation.Wall(document, document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Cavity Blockwork 300mm"));
        Assert.Equal(("2.2", true), (lightweight.Type, lightweight.Met));
        Assert.Equal(334.7, lightweight.Mass, 1);
        var dense = SoundInsulation.Wall(document, document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Dense Cavity Blockwork 300mm"));
        Assert.Equal(("2.1", true), (dense.Type, dense.Met));
        Assert.Equal(445.0, dense.Mass, 1);

        // Wall type 1.1, as 2.33's example: 215 of block at 1840 kg/m³ and 13 of lightweight plaster at 10 kg/m² a face - 415.6; 190 of it, 369.6, short.
        var plaster = Material(document, "Lightweight Plaster", 770);
        var block = Material(document, "Concrete Block, Dense 1840", 1840);
        var solid = SoundInsulation.Wall(document, Type("Solid 215", (LayerFunction.Finish2, plaster, 13), (LayerFunction.Structure, block, 215), (LayerFunction.Finish2, plaster, 13)));
        Assert.Equal(("1.1", true), (solid.Type, solid.Met));
        var thin = SoundInsulation.Wall(document, Type("Solid 190", (LayerFunction.Finish2, plaster, 13), (LayerFunction.Structure, block, 190), (LayerFunction.Finish2, plaster, 13)));
        Assert.Equal("415 kg/m² at least with its plaster, not 370 kg/m² (2.33)", Assert.Single(thin.Short));

        // Brick, 1.3; a solid wall of aircrete, none of them.
        var brick = SoundInsulation.Wall(document, Type("Brick 215", (LayerFunction.Finish2, plaster, 13), (LayerFunction.Structure, Named(document, "Brick, Common"), 215), (LayerFunction.Finish2, plaster, 13)));
        Assert.Equal(("1.3", true), (brick.Type, brick.Met));
        Assert.Equal(0, SoundInsulation.Wall(document, Type("Aircrete 215", (LayerFunction.Finish2, plaster, 13), (LayerFunction.Structure, Named(document, "Aircrete Block"), 215), (LayerFunction.Finish2, plaster, 13))).Group);

        // Wall type 2.4, 2.72's example: aircrete at 650, 100 leaves, a 75 cavity, plasterboard - 150 kg/m², met only staggered, and with no separating floor.
        var aircrete = Material(document, "Aircrete Block 650", 650);
        var board = Named(document, "Gypsum Plasterboard");
        var air = Named(document, "Air Cavity");
        var cavityAircrete = Type("Aircrete Cavity", (LayerFunction.Finish2, board, 12.5), (LayerFunction.Structure, aircrete, 100), (LayerFunction.ThermalAir, air, 75), (LayerFunction.Structure, aircrete, 100), (LayerFunction.Finish2, board, 12.5));
        Assert.True(SoundInsulation.Wall(document, cavityAircrete, staggered: true).Met);
        Assert.Equal("2.4", SoundInsulation.Wall(document, cavityAircrete).Type);
        Assert.Contains(SoundInsulation.Wall(document, cavityAircrete, staggered: false).Short, said => said.StartsWith("a step or stagger of 300 at least", StringComparison.Ordinal));
        Assert.Contains("no separating floor in the building (2.72)", SoundInsulation.Wall(document, cavityAircrete, separatingFloor: true, staggered: true).Short);

        // Wall type 4.1: two frames, 228 between the linings, two sheets of plasterboard of 10 kg/m² each side, mineral wool in the frames.
        var wool = Named(document, "Mineral Wool Insulation");
        var framed = SoundInsulation.Wall(document, Type("Twin Frame", (LayerFunction.Finish2, board, 12.5), (LayerFunction.Finish2, board, 12.5), (LayerFunction.Structure, wool, 89),
            (LayerFunction.ThermalAir, air, 50), (LayerFunction.Structure, wool, 89), (LayerFunction.Finish2, board, 12.5), (LayerFunction.Finish2, board, 12.5)));
        Assert.Equal(("4.1", true), (framed.Type, framed.Met));

        // Wall type 3.1: 140 of block at 2200, 308 kg/m², between independent panels of two sheets 35 off it.
        var core = Material(document, "Concrete Block, Dense 2200", 2200);
        var panelled = SoundInsulation.Wall(document, Type("Panelled", (LayerFunction.Finish2, board, 12.5), (LayerFunction.Finish2, board, 12.5), (LayerFunction.ThermalAir, air, 35),
            (LayerFunction.Structure, core, 140), (LayerFunction.ThermalAir, air, 35), (LayerFunction.Finish2, board, 12.5), (LayerFunction.Finish2, board, 12.5)));
        Assert.Equal(("3.1", true), (panelled.Type, panelled.Met));
    }

    [Fact]
    public void APartyWallsJunctionsKeepTheSoundFromGoingRoundIt()
    {
        // A pair of houses 12 m by 8 m, the library's lightweight party wall between them at x 6 m, the library's outside walls round them.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++) Wall(document, level, corners[i], corners[(i + 1) % 4], Brick);
        var party = Wall(document, level, new Point2D(6000, 0), new Point2D(6000, 8000), "Party Wall - Cavity Blockwork 300mm");
        Room(document, level, "Living Room", 3000, 4000, "House 1");
        Room(document, level, "Kitchen", 9000, 2000, "House 2");
        Room(document, level, "Living Room", 9000, 6000, "House 2");

        // Its outside walls' aircrete inner leaf, 140 of 440 x 215 units at 600 kg/m³ with 10 joints at 1800: 95 kg/m², under the 120 a type 2.2 asks of it (2.75).
        var said = Assert.Single(E(document, party));
        Assert.Equal("it meets the outside wall Exterior - Brick on Block 330mm, whose masonry inner leaf is 95 kg/m² without its finish.", said.Said);
        Assert.Contains("(2.75)", said.Rule);

        // One ground slab poured under both houses: it bridges the cavity (2.88). Two slabs, one each side: not.
        var slab = Slab<Floor>(document, level, "Floor - Screed on Slab 250mm", 0, 0, 12000, 8000);
        Assert.Contains(E(document, party), problem => problem.Said == "Floor - Screed on Slab 250mm, a slab on the ground, runs on under it, bridging its cavity." && problem.Rule.Contains("(2.88)"));
        document.Remove(slab);
        Slab<Floor>(document, level, "Floor - Screed on Slab 250mm", 0, 0, 6000, 8000);
        Slab<Floor>(document, level, "Floor - Screed on Slab 250mm", 6000, 0, 12000, 8000);
        Assert.Single(E(document, party));

        // Dense block, type 2.1: no least mass asked of the inner leaf (2.75) - met.
        party.TypeId = document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Dense Cavity Blockwork 300mm").Id;
        Assert.Empty(E(document, party));

        // An aircrete partition in House 2 abutting it, 68 kg/m² without its plaster: under the 120 asked of it (2.81).
        var partition = new WallType("Interior - Aircrete 100mm", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, Named(document, "Cement Plaster").Id, 13),
            new MaterialLayer(LayerFunction.Structure, Named(document, "Aircrete Block").Id, 100), new MaterialLayer(LayerFunction.Finish2, Named(document, "Cement Plaster").Id, 13)));
        document.AddType(partition);
        Wall(document, level, new Point2D(6000, 4000), new Point2D(12000, 4000), partition.Name);
        said = Assert.Single(E(document, party));
        Assert.Equal("an internal masonry wall abutting it weighs less than 120 kg/m² without its finish: Interior - Aircrete 100mm, 68 kg/m².", said.Said);
        Assert.Contains("(2.81, 2.83)", said.Rule);

        // Built to a Robust Detail: taken as that, not checked here.
        document.FindType<WallType>(party.TypeId)!.RobustDetail = "E-WM-1";
        Assert.Empty(E(document, party));
    }

    /// <summary>A storey of round 94's block: its level, the wall between the corridor and the flat, the flat's door, and its rooms.</summary>
    private sealed record Storey(Level Level, Wall FlatWall, Door FlatDoor, Room Stair, Room Corridor, Room Flat);

    /// <summary>
    /// A block of flats 12 m by 6 m on two storeys 3 m apart, each a stair at the west end (x 0 to 3 m), a corridor along the south side (y 0 to
    /// 2 m), and a flat north of it entered at x 8 m through an FD30S door; the stair rising north to the next storey.
    /// </summary>
    private static (BimDocument Document, List<Storey> Storeys) Block()
    {
        var document = BimDocument.CreateDefault();
        var built = new List<Storey>();
        foreach (var level in new[] { document.Levels[0], document.Levels[1] })
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
            for (var i = 0; i < 4; i++) Wall(document, level.Id, corners[i], corners[(i + 1) % 4], Brick);
            var stairWall = Wall(document, level.Id, new Point2D(3000, 0), new Point2D(3000, 6000));
            var flatWall = Wall(document, level.Id, new Point2D(3000, 2000), new Point2D(12000, 2000));
            document.Add(new Door { HostWallId = stairWall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100").Id, LevelId = level.Id, DistanceAlongWall = 1000 });
            var flatDoor = new Door { HostWallId = flatWall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100").Id, LevelId = level.Id, DistanceAlongWall = 5000 };
            document.Add(flatDoor);
            built.Add(new Storey(level, flatWall, flatDoor, Room(document, level.Id, "Stair", 1500, 3000), Room(document, level.Id, "Corridor", 7500, 1000),
                Room(document, level.Id, "Living Room", 7500, 4000, $"Flat {built.Count + 1}")));
        }

        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = built[0].Level.Id, TopLevelId = built[1].Level.Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        return (document, built);
    }

    [Fact]
    public void AFloorBetweenFlatsIsOneOfSection3s()
    {
        // The library's ground floor laid between the flats: its 100 slab 240 kg/m², its screed floating on PIR, nothing under it - floor type 2.1C
        // short, and only that said until it is one of Section 3's floors.
        var (document, storeys) = Block();
        var floor = Slab<Floor>(document, storeys[1].Level.Id, "Floor - Screed on Slab 250mm", 3000, 2000, 12000, 6000);
        var said = Assert.Single(E(document, floor));
        Assert.Equal("it separates Flat 2 from Flat 1: a floor of concrete cast in situ, its base 240 kg/m² with any screed bonded to it.", said.Said);
        Assert.Contains("floor type 2.1C", said.Rule);
        Assert.Contains("300 kg/m² at least of its base with any bonded screed, not 240 kg/m²", said.Rule);
        Assert.Contains("a resilient layer of mineral wool 25 thick at least, of 36 kg/m³, not Rigid Insulation", said.Rule);
        Assert.Contains("ceiling treatment C or better under it, not none", said.Rule);

        // The library's separating floor - 360 kg/m², 65 of screed on 25 of mineral wool at 36, plasterboard on battens: met, but the outside walls' aircrete inner leaf, 95 kg/m², is not (3.70).
        floor.TypeId = document.TypesOf<FloorType>().Single(type => type.Name == "Floor - Separating Slab, Floating 278mm").Id;
        said = Assert.Single(E(document, floor));
        Assert.Equal("an outside wall round it has a masonry inner leaf of less than 120 kg/m² without its finish: Exterior - Brick on Block 330mm, 95 kg/m².", said.Said);
        Assert.Contains("(3.70)", said.Rule);
        Assert.Equal("2.1C", SoundInsulation.Floor(document, document.FindType<FloorType>(floor.TypeId)!, floor).Type);

        // A timber frame outside wall: one sheet of plasterboard on its frame, where two are asked (3.74c).
        var frame = document.TypesOf<WallType>().Single(type => type.Name == "Timber Frame - Brick Clad 315mm").Id;
        foreach (var wall in document.Walls.Where(wall => document.GetWallType(wall)?.Name == Brick)) wall.TypeId = frame;
        said = Assert.Single(E(document, floor));
        Assert.Equal("an outside wall round it has a timber frame inner leaf with less than two layers of plasterboard: Timber Frame - Brick Clad 315mm.", said.Said);
        Assert.Contains("(3.74c)", said.Rule);
    }

    [Fact]
    public void AFlatsDoorIsSealedAndTheCommonPartsAbsorbSound()
    {
        var (document, storeys) = Block();

        // A flat's entrance door from the corridor, its Rw not given (2.26); Rw 30 met.
        var door = storeys[0].FlatDoor;
        static List<PlacementProblem> Heard(BimDocument document, Door door) =>
            Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (2.25, 2.26)", StringComparison.Ordinal)).ToList();
        Assert.Equal("it is Flat 1's entrance door from the Corridor, its sound reduction not given.", Assert.Single(Heard(document, door)).Said);
        document.FindType<DoorType>(door.TypeId)!.AcousticRating = 30;
        Assert.Empty(Heard(document, door));

        // Nothing absorbing in the common parts: each corridor, and the stair, asked for absorbers (7.10, 7.11).
        List<string> Reverberant() => SoundInsulation.Reverberation(document).Select(problem => $"{problem.Room.Name}: {problem.Said}").ToList();
        var said = Reverberant();
        Assert.Equal(3, said.Count);
        Assert.Equal(2, said.Count(each => each.StartsWith("Corridor: it is a common corridor of a block of flats,", StringComparison.Ordinal)));
        Assert.Single(said, each => each.StartsWith("Stair: it is the head of a common stair", StringComparison.Ordinal));

        // A plasterboard ceiling over the corridors absorbs too little to be classed; acoustic tiles of Class C do.
        var ceilings = storeys.Select(storey => Slab<Ceiling>(document, storey.Level.Id, "Ceiling - Plasterboard 25mm", 3000, 0, 12000, 2000, 2400)).ToList();
        Assert.Equal(3, Reverberant().Count);
        var tiles = document.TypesOf<SlabType>().Single(type => type.Name == "Ceiling - Acoustic Mineral Fibre Tile 15mm").Id;
        foreach (var ceiling in ceilings) ceiling.TypeId = tiles;
        Assert.Single(Reverberant());

        // Tiles over the top of the stair: half its treads, landings and top ceiling in Class C - met (7.11).
        Slab<Ceiling>(document, storeys[1].Level.Id, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 0, 0, 3000, 6000, 2400);
        Assert.Empty(Reverberant());
    }

    [Fact]
    public void AbsorbersAndRobustDetailsAreKept()
    {
        // A class read as written, or from αw.
        Assert.True(SoundAbsorption.TryParse("class c", out var written) && written == AbsorptionClass.C);
        Assert.True(SoundAbsorption.TryParse("0.7", out var seventy) && seventy == AbsorptionClass.C);
        Assert.True(SoundAbsorption.TryParse("αw 0.55", out var half) && half == AbsorptionClass.D);
        Assert.False(SoundAbsorption.TryParse("0.10", out _));

        // In Edit Type, the party wall's mass and its type; saved and opened again with its Robust Detail, the floor's, and the tiles' class.
        var document = BimDocument.CreateDefault();
        var party = document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Cavity Blockwork 300mm");
        var rows = party.GetTypeParameters(document).ToList();
        Assert.Equal(335d, rows.Single(row => row.Name == "Mass per Unit Area (kg/m²)").Value);
        Assert.Equal("2.2, two leaves of lightweight aggregate block with a 75 cavity", rows.Single(row => row.Name == "Separating Wall Type (Approved Document E)").Value);
        party.RobustDetail = "E-WM-1";
        document.TypesOf<FloorType>().Single(type => type.Name == "Floor - Separating Slab, Floating 278mm").RobustDetail = "E-FC-1";
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal("E-WM-1", opened.TypesOf<WallType>().Single(type => type.Name == party.Name).RobustDetail);
        Assert.Equal("E-FC-1", opened.TypesOf<FloorType>().Single(type => type.Name == "Floor - Separating Slab, Floating 278mm").RobustDetail);
        Assert.Equal("Class C", opened.Materials.Single(material => material.Name == "Mineral Fibre Ceiling Tile").SoundAbsorption);
        Assert.Equal(AbsorptionClass.C, SoundAbsorption.Of(opened.Materials.Single(material => material.Name == "Mineral Fibre Ceiling Tile")));
    }
}
