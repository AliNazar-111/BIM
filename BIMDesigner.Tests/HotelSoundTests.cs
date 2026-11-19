using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Sound in a hotel by Approved Document E (round 105): its rooms for residential purposes - a bedroom with its en-suite or a suite's sitting
/// room - separated from each other and from the rest of the building by Section 6's walls and floors to Table 0.1b, a solid wall stopped at a
/// ceiling by Diagram 6.1, floating floors not run on from room to room, doors from the corridor, the corridors' absorption, and E2 within a
/// room for residential purposes only - not on a wall with a door in it, nor between an en-suite toilet and its bedroom.
/// </summary>
[Collection("Wpf")]
public class HotelSoundTests
{
    private const string Partition = "Interior - Partition 125mm", Solid = "Separating Wall - Dense Blockwork 215mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = Partition)
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Door Door(BimDocument document, Wall wall, double along)
    {
        var door = new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 900 x 2100").Id, LevelId = wall.LevelId, DistanceAlongWall = along };
        document.Add(door);
        return door;
    }

    private static Room Room(BimDocument document, Guid level, string name, double x, double y)
    {
        var room = new Room { LevelId = level, Location = new Point2D(x, y), Name = name };
        document.Add(room);
        return room;
    }

    private static void Place(BimDocument document, Guid level, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        document.Add(new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = level, Location = new Point2D(x, y), Elevation = kind.DefaultElevation });
    }

    private static T Slab<T>(BimDocument document, Guid level, string type, double x0, double y0, double x1, double y1, double offset = 0) where T : Slab, new()
    {
        var slab = new T { LevelId = level, TypeId = document.TypesOf<SlabType>().First(each => each.Name == type).Id, HeightOffset = offset };
        slab.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        document.Add(slab);
        return slab;
    }

    /// <summary>A storey of the hotel: its walls between bedrooms 1 and 2 and to the corridor, its doors, its rooms.</summary>
    private sealed record Storey(Guid Level, Wall Between, Wall CorridorWall, Wall SuiteWall, Wall SuiteDoorWall, Door FirstDoor, Door SuiteDoor, Room Corridor, Room First, Room EnSuite, Room Second);

    /// <summary>
    /// A hotel 12 m by 8 m on two storeys 3 m apart, each a corridor along the south (y 0 to 2 m) and three bedrooms north of it off 900 doors -
    /// x 0 to 4 m, 4 to 8 m, 8 to 12 m - the first with an en-suite WC in its north west corner (x 0 to 2 m, y 6 to 8 m), its door in its south wall.
    /// </summary>
    private static (BimDocument Document, List<Storey> Storeys) Build()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Hotel";
        var storeys = new List<Storey>();
        foreach (var (level, n) in new[] { (document.Levels[0].Id, 1), (document.Levels[1].Id, 2) })
        {
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
            for (var i = 0; i < 4; i++) Wall(document, level, corners[i], corners[(i + 1) % 4], "Exterior - Brick on Block 330mm");
            var corridorWall = Wall(document, level, new Point2D(0, 2000), new Point2D(12000, 2000));
            var between = Wall(document, level, new Point2D(4000, 2000), new Point2D(4000, 8000));
            Wall(document, level, new Point2D(8000, 2000), new Point2D(8000, 8000));
            var suiteWall = Wall(document, level, new Point2D(2000, 6000), new Point2D(2000, 8000));
            var suiteDoorWall = Wall(document, level, new Point2D(0, 6000), new Point2D(2000, 6000));
            var corridor = Room(document, level, "Corridor", 6000, 1000);
            var first = Room(document, level, $"Bedroom {n}01", 3000, 4000);
            var suite = Room(document, level, $"En-suite {n}01", 1000, 7000);
            var second = Room(document, level, $"Bedroom {n}02", 6000, 5000);
            Room(document, level, $"Bedroom {n}03", 10000, 5000);
            var firstDoor = Door(document, corridorWall, 3000);
            Door(document, corridorWall, 6000);
            Door(document, corridorWall, 10000);
            var suiteDoor = Door(document, suiteDoorWall, 1000);
            Place(document, level, "Bed - Double 1350 x 1900", 3000, 4000);
            Place(document, level, "Bed - Double 1350 x 1900", 6000, 5000);
            Place(document, level, "Bed - Double 1350 x 1900", 10000, 5000);
            Place(document, level, "WC - Close Coupled, 480 Seat", 1000, 7500);
            storeys.Add(new Storey(level, between, corridorWall, suiteWall, suiteDoorWall, firstDoor, suiteDoor, corridor, first, suite, second));
        }

        return (document, storeys);
    }

    private static List<PlacementProblem> E(BimDocument document, Wall wall) =>
        WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (", StringComparison.Ordinal) && !problem.Rule.Contains("(E2")).ToList();

    private static List<PlacementProblem> E(BimDocument document, Floor floor) =>
        SlabRules.Problems(document, floor, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (", StringComparison.Ordinal) && !problem.Rule.Contains("Table 0.2")).ToList();

    private static bool E2(BimDocument document, Wall wall) =>
        WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard).Any(problem => problem.Rule.Contains("(E2, Table 0.2)"));

    [Fact]
    public void AHotelsRoomsForResidentialPurposesAreItsBedroomsWithWhatTheyOpenInto()
    {
        var (document, storeys) = Build();
        var ground = storeys[0];

        // Each bedroom with its en-suite, by the bedroom's name; the corridor none's.
        var suites = Lodging.Suites(document);
        Assert.Equal("Bedroom 101", suites[ground.First]);
        Assert.Equal("Bedroom 101", suites[ground.EnSuite]);
        Assert.Equal("Bedroom 102", suites[ground.Second]);
        Assert.False(suites.ContainsKey(ground.Corridor));
        Assert.Equal("Bedroom 101, with En-suite 101", Lodging.Residence(document, ground.First));
        Assert.Equal("part of Bedroom 101", Lodging.Residence(document, ground.EnSuite));
        Assert.Null(Lodging.Residence(document, ground.Corridor));

        // A hospital's patients' rooms are not rooms for residential purposes (Regulation 2).
        document.ProjectInformation.BuildingType = "Hospital";
        Assert.Empty(Lodging.Suites(document));

        // A care home, a guest house: rooms for residential purposes, not a house's.
        foreach (var (said, use) in new[] { ("Residential care home", BuildingUse.Other), ("Nursing Home", BuildingUse.Other), ("Guest house", BuildingUse.Other), ("Boarding House", BuildingUse.Other), ("House", BuildingUse.House), ("Holiday home", BuildingUse.House) })
        {
            document.ProjectInformation.BuildingType = said;
            Assert.Equal(use, FireDesign.UseOf(document));
        }
    }

    [Fact]
    public void WallsBetweenHotelRoomsAreSection6s()
    {
        var (document, storeys) = Build();
        var wall = storeys[0].Between;

        // The stud partition between bedrooms 101 and 102: one frame, where Section 6's walls are masonry, or two frames with care - its walls
        // tested to DnT,w + Ctr 43 dB (Table 0.1b), a Robust Detail no stand-in for that in a hotel.
        var said = Assert.Single(E(document, wall));
        Assert.Contains("Bedroom 101", said.Said);
        Assert.Contains("Bedroom 102", said.Said);
        Assert.StartsWith("Approved Document E (E1, 6.4) asks wall type 4.1", said.Rule);
        Assert.EndsWith("or show by pre-completion testing that it reaches DnT,w + Ctr 43 dB (Table 0.1b, Section 1).", said.Rule);
        document.FindType<WallType>(wall.TypeId)!.RobustDetail = "E-WT-1";
        Assert.Contains("a Robust Detail stands for that in a new house or flat only (Regulation 41, Annex E)", Assert.Single(E(document, wall)).Rule);
        document.FindType<WallType>(wall.TypeId)!.RobustDetail = string.Empty;

        // The corridor's wall: Bedroom 101 from the corridor (6.5).
        Assert.Contains(E(document, storeys[0].CorridorWall), problem => problem.Said.StartsWith("it separates Bedroom 101 from the Corridor:", StringComparison.Ordinal));

        // The walls round the en-suite are within Bedroom 101's room: no separating walls.
        Assert.Empty(E(document, storeys[0].SuiteWall));

        // Solid dense block, wall type 1.1, and cavity dense block, 2.1: Section 6's - and Section 3's cavity core between panels, 3.3, is not.
        wall.TypeId = document.TypesOf<WallType>().Single(type => type.Name == Solid).Id;
        Assert.DoesNotContain(E(document, wall), problem => problem.Rule.Contains("6.4"));
        wall.TypeId = document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Dense Cavity Blockwork 300mm").Id;
        Assert.DoesNotContain(E(document, wall), problem => problem.Rule.Contains("6.4"));
        Material Named(string name) => document.Materials.First(material => material.Name == name);
        var (board, air, block) = (Named("Gypsum Plasterboard"), Named("Air Cavity"), Named("Concrete Block, Dense Aggregate"));
        var cavityCore = new WallType("Cavity Core Between Panels", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, board.Id, 12.5), new MaterialLayer(LayerFunction.Finish2, board.Id, 12.5),
            new MaterialLayer(LayerFunction.ThermalAir, air.Id, 35), new MaterialLayer(LayerFunction.Structure, block.Id, 100), new MaterialLayer(LayerFunction.ThermalAir, air.Id, 50),
            new MaterialLayer(LayerFunction.Structure, block.Id, 100), new MaterialLayer(LayerFunction.ThermalAir, air.Id, 35), new MaterialLayer(LayerFunction.Finish2, board.Id, 12.5),
            new MaterialLayer(LayerFunction.Finish2, board.Id, 12.5))) { Function = WallFunction.Interior };
        document.AddType(cavityCore);
        Assert.Equal("3.3", SoundInsulation.Wall(document, cavityCore).Type);
        wall.TypeId = cavityCore.Id;
        said = Assert.Single(E(document, wall));
        Assert.EndsWith("wall type 3.3.", said.Said);
        Assert.Contains("not a cavity core between panels, type 3.3", said.Rule);

        // The same partition in a house: no separating wall, but E2's Rw 40 round its bedrooms.
        document.ProjectInformation.BuildingType = "House";
        wall.TypeId = document.TypesOf<WallType>().Single(type => type.Name == Partition).Id;
        Assert.Empty(E(document, wall));
    }

    [Fact]
    public void ASolidWallMayStopAtACeilingBetweenHotelRooms()
    {
        var (document, storeys) = Build();
        var level = storeys[0].Level;
        var wall = storeys[0].Between;
        wall.TypeId = document.TypesOf<WallType>().Single(type => type.Name == Solid).Id;
        wall.UnconnectedHeight = 2400;

        // A concrete floor over the ground storey, 277.5 thick, its underside at 2722.5: the solid wall stops 323 under it, with no ceiling drawn (6.14).
        Slab<Floor>(document, storeys[1].Level, "Floor - Separating Slab, Floating 278mm", 0, 0, 12000, 8000);
        List<PlacementProblem> Head() => E(document, wall).Where(problem => problem.Rule.Contains("6.14")).ToList();
        var said = Assert.Single(Head());
        Assert.Equal("it separates Bedroom 101 from Bedroom 102 and stops 323 below the floor over it at a ceiling: no ceiling drawn on both sides of it.", said.Said);
        Assert.StartsWith("Approved Document E (6.14, Diagram 6.1) lets a solid masonry wall between rooms for residential purposes stop at a ceiling", said.Rule);

        // A ceiling of two layers of plasterboard under 200 of mineral wool in each room, at 2400: met.
        const string twoLayers = "Ceiling - Two Layers Plasterboard under 200mm Mineral Wool";
        var west = Slab<Ceiling>(document, level, twoLayers, 0, 2000, 4000, 8000, 2400);
        var east = Slab<Ceiling>(document, level, twoLayers, 4000, 2000, 8000, 8000, 2400);
        Assert.Empty(Head());

        // One ceiling run on over it from room to room: not.
        document.Remove(east);
        west.SetBoundary(new[] { new Point2D(0, 2000), new Point2D(8000, 2000), new Point2D(8000, 8000), new Point2D(0, 8000) });
        Assert.EndsWith($"at a ceiling: {twoLayers} runs on over it from one room to the other.", Assert.Single(Head()).Said);

        // A single sheet of 25 plasterboard, no wool: not.
        west.TypeId = document.TypesOf<SlabType>().Single(type => type.Name == "Ceiling - Plasterboard 25mm").Id;
        Assert.Contains("Ceiling - Plasterboard 25mm is not two layers of plasterboard or more of 20 kg/m² together; Ceiling - Plasterboard 25mm has 0 of mineral wool of 10 kg/m³ over it, not 200", Assert.Single(Head()).Said);

        // A cavity wall stopped there: only a solid one may be (6.14).
        wall.TypeId = document.TypesOf<WallType>().Single(type => type.Name == "Party Wall - Dense Cavity Blockwork 300mm").Id;
        said = Assert.Single(Head());
        Assert.Equal("it separates Bedroom 101 from Bedroom 102 and stops 323 below the floor over it.", said.Said);
        Assert.StartsWith("Approved Document E (6.14, 2.91, 2.92) lets only a solid masonry separating wall, type 1, stop at a ceiling", said.Rule);

        // Up to the floor: nothing to say.
        wall.UnconnectedHeight = 3000;
        Assert.Empty(Head());
    }

    [Fact]
    public void AFloatingFloorIsNotRunOnFromHotelRoomToRoom()
    {
        var (document, storeys) = Build();

        // The library's floating floor laid over the whole of the first storey: its screed runs on over the three bedrooms over it, and its own
        // ceiling under the three under it (6.8).
        var floor = Slab<Floor>(document, storeys[1].Level, "Floor - Separating Slab, Floating 278mm", 0, 0, 12000, 8000);
        List<PlacementProblem> Running() => E(document, floor).Where(problem => problem.Rule.StartsWith("Approved Document E (6.8)", StringComparison.Ordinal)).ToList();
        var said = Assert.Single(Running());
        Assert.EndsWith(": its floating floor runs on over Bedroom 201, Bedroom 202 and Bedroom 203; its own ceiling runs on under Bedroom 101, Bedroom 102 and Bedroom 103.", said.Said);

        // Laid room by room - Bedroom 201 with its en-suite, over Bedroom 101 with its own: met.
        floor.SetBoundary(new[] { new Point2D(0, 2000), new Point2D(4000, 2000), new Point2D(4000, 8000), new Point2D(0, 8000) });
        Assert.Empty(Running());

        // The corridor's own floor of the separating floor type, corridor over corridor: no room for residential purposes either side - its type
        // marked separating not taken for two homes in a hotel.
        var corridorFloor = Slab<Floor>(document, storeys[1].Level, "Floor - Separating Slab, Floating 278mm", 0, 0, 12000, 2000);
        Assert.Null(SoundInsulation.Between(document, corridorFloor, document.FindType<FloorType>(corridorFloor.TypeId)!));
        Assert.Empty(E(document, corridorFloor));
        document.Remove(corridorFloor);

        // A concrete slab of 480 kg/m² with carpet and plasterboard on battens under it - floor type 1.1C - runs on from room to room as it may.
        Material Named(string name) => document.Materials.First(material => material.Name == name);
        var slab = new FloorType("Floor - Concrete 200mm, Plasterboard on Battens", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, Named("Concrete, Cast In Situ").Id, 200),
            new MaterialLayer(LayerFunction.ThermalAir, Named("Air Cavity").Id, 25), new MaterialLayer(LayerFunction.Finish2, Named("Gypsum Plasterboard").Id, 12.5)));
        document.AddType(slab);
        foreach (var room in document.All<Room>().Where(room => room.LevelId == storeys[1].Level)) room.FloorFinish = "Carpet";
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) });
        floor.TypeId = slab.Id;
        Assert.Equal(("1.1C", true), (SoundInsulation.Floor(document, slab, floor).Type, SoundInsulation.Floor(document, slab, floor).Met));
        Assert.Empty(Running());
    }

    [Fact]
    public void AHotelRoomsDoorIsSealedAndItsCorridorsAbsorbSound()
    {
        var (document, storeys) = Build();
        var ground = storeys[0];

        // Bedroom 101's door from the corridor, the library's single door made Rw 25 (6.6); its en-suite's door, within the room, not asked.
        static List<PlacementProblem> Heard(BimDocument document, Door door) =>
            Doors.Problems(document, door, document.ProjectInformation.ClearanceStandard).Where(problem => problem.Rule.StartsWith("Approved Document E (6.5, 6.6)", StringComparison.Ordinal)).ToList();
        document.FindType<DoorType>(ground.FirstDoor.TypeId)!.AcousticRating = 25;
        Assert.Equal("it is Bedroom 101's door from the Corridor, Rw 25 dB.", Assert.Single(Heard(document, ground.FirstDoor)).Said);
        Assert.Empty(Heard(document, ground.SuiteDoor));
        document.FindType<DoorType>(ground.FirstDoor.TypeId)!.AcousticRating = 30;
        Assert.Empty(Heard(document, ground.FirstDoor));

        // Both corridors give access to rooms for residential purposes: each asked for a Class C absorber over its floor (E3, 7.10).
        List<string> Reverberant() => SoundInsulation.Reverberation(document).Select(problem => $"{problem.Room.Name}: {problem.Said}").ToList();
        var said = Reverberant();
        Assert.Equal(2, said.Count);
        Assert.All(said, each => Assert.StartsWith("Corridor: it is a common corridor of a building with rooms for residential purposes, ", each));
        Assert.All(said, each => Assert.Contains(" m² of floor with 0.0 m² of a Class C absorber or better over it - Approved Document E (E3, 7.2, 7.10)", each));
        foreach (var storey in storeys) Slab<Ceiling>(document, storey.Level, "Ceiling - Acoustic Mineral Fibre Tile 15mm", 0, 0, 12000, 2000, 2400);
        Assert.Empty(Reverberant());
    }

    [Fact]
    public void E2ReachesWithinARoomForResidentialPurposesNotItsEnSuite()
    {
        var (document, storeys) = Build();
        var ground = storeys[0];
        document.FindType<WallType>(ground.Between.TypeId)!.AcousticRating = 35;

        // Bedroom 101's en-suite: its wall with the door in it (E2 a), and its other wall with the bedroom (E2 b), not asked.
        Assert.False(E2(document, ground.SuiteDoorWall));
        Assert.False(E2(document, ground.SuiteWall));

        // Bedroom 103 made a suite: a sitting room off the corridor (y 2 to 5 m), the bedroom beyond it - the wall between them, Rw 35, in two:
        // where its door is, not asked (a); the rest, asked Rw 40 - within the room for residential purposes.
        var level = ground.Level;
        var withDoor = Wall(document, level, new Point2D(8000, 5000), new Point2D(10000, 5000));
        var plain = Wall(document, level, new Point2D(10000, 5000), new Point2D(12000, 5000));
        Door(document, withDoor, 1000);
        var bedroom = document.All<Room>().Single(room => room.Name == "Bedroom 103");
        bedroom.Location = new Point2D(10000, 6500);
        document.All<Component>().Single(piece => piece.LevelId == level && piece.Location == new Point2D(10000, 5000)).Location = new Point2D(10000, 6500);
        var sitting = Room(document, level, "Sitting Room 103", 10000, 3500);
        Assert.Equal("Bedroom 103", Lodging.Suites(document)[sitting]);
        Assert.False(E2(document, withDoor));
        Assert.True(E2(document, plain));

        // Between two rooms for residential purposes it is E1, not E2.
        Assert.False(E2(document, ground.Between));

        // In a house: the en-suite's walls still not asked; the partition between two bedrooms asked Rw 40.
        document.ProjectInformation.BuildingType = "House";
        Assert.False(E2(document, ground.SuiteWall));
        Assert.True(E2(document, ground.Between));
    }
}
