using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// The electricity escape and firefighting rely on (round 138), by Approved Document B Volume 2 5.26 and 5.30: an escape stair's lights on a
/// circuit of their own, and a protected circuit to equipment working in a fire in fire-resisting cable and feeding nothing else.
/// </summary>
[Collection("Wpf")]
public class FireCircuitTests
{
    /// <summary>An office 14 m by 10 m on two storeys: a stair at the west end entered from a corridor along the south, an office north of it.</summary>
    private static (BimDocument Document, List<Room> Stairs, List<Room> Corridors, List<Room> Offices) Office()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var levels = document.Levels.OrderBy(level => level.Elevation).Take(2).ToList();
        var (stairs, corridors, offices) = (new List<Room>(), new List<Room>(), new List<Room>());
        Wall Wall(Guid level, double x0, double y0, double x1, double y1, string type)
        {
            var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            return wall;
        }

        foreach (var level in levels)
        {
            const string brick = "Exterior - Brick on Block 330mm", partition = "Interior - Partition 125mm";
            Wall(level.Id, 0, 0, 14000, 0, brick);
            var east = Wall(level.Id, 14000, 0, 14000, 10000, brick);
            Wall(level.Id, 14000, 10000, 0, 10000, brick);
            Wall(level.Id, 0, 10000, 0, 0, brick);
            var stairWall = Wall(level.Id, 3000, 0, 3000, 10000, partition);
            var corridorWall = Wall(level.Id, 3000, 2000, 14000, 2000, partition);
            document.Add(new Door { HostWallId = stairWall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Fire Door FD30S - 900 x 2100").Id, LevelId = level.Id, DistanceAlongWall = 1000 });
            document.Add(new Door { HostWallId = corridorWall.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 1000 x 2100").Id, LevelId = level.Id, DistanceAlongWall = 5500 });
            if (level == levels[0]) document.Add(new Door { HostWallId = east.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 1000 x 2100").Id, LevelId = level.Id, DistanceAlongWall = 1000 });
            foreach (var (name, x, y, list) in new[] { ("Stair", 1500.0, 8000.0, stairs), ("Corridor", 8500.0, 1000.0, corridors), ("Office", 8500.0, 6000.0, offices) })
            {
                var room = new Room { LevelId = level.Id, Location = new Point2D(x, y), Name = name };
                document.Add(room);
                list.Add(room);
            }
        }

        document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = levels[0].Id, TopLevelId = levels[1].Id, Start = new Point2D(1500, 2600), Angle = 90, Width = 900 });
        return (document, stairs, corridors, offices);
    }

    private static Component Put(BimDocument document, Room room, string type, double x, double y)
    {
        var kind = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var piece = new Component { TypeId = kind.Id, TypeKind = kind.Kind, LevelId = room.LevelId, Location = new Point2D(x, y), Elevation = kind.DefaultElevation };
        document.Add(piece);
        return piece;
    }

    private static MepSystem Circuit(BimDocument document, string name, CircuitUse use, params Component[] members)
    {
        var circuit = new MepSystem { Kind = SystemKind.Power, Name = name, Use = use };
        foreach (var member in members) circuit.Members.Add(member.Id);
        document.Add(circuit);
        return circuit;
    }

    [Fact]
    public void AnEscapeStairsLightsAreOnACircuitOfTheirOwn()
    {
        // One lighting circuit for the ground floor's stair and its corridor (5.26); the stair's lights on their own: met.
        var (document, stairs, corridors, offices) = Office();
        var stairLight = Put(document, stairs[0], "Ceiling Light - Flush", 1500, 8000);
        var corridorLight = Put(document, corridors[0], "Ceiling Light - Flush", 8500, 1000);
        var officeLight = Put(document, offices[0], "Ceiling Light - Flush", 8500, 6000);
        var circuit = Circuit(document, "Lighting 1", CircuitUse.Lighting, stairLight, corridorLight, officeLight);
        Assert.Contains(FireCircuits.Problems(document), problem => problem.Element == circuit && problem.Text.StartsWith("it lights the Stair, an escape stair, and the Corridor - Approved Document B Volume 2 (5.26)", StringComparison.Ordinal));

        circuit.Members.Remove(stairLight.Id);
        Circuit(document, "Stair Lighting", CircuitUse.Lighting, stairLight);
        Assert.DoesNotContain(FireCircuits.Problems(document), problem => problem.Text.Contains("(5.26)"));

        // A stair's light with an office's only - not another part of the escape routes: met.
        circuit.Members.Remove(corridorLight.Id);
        circuit.Members.Add(stairLight.Id);
        Assert.DoesNotContain(FireCircuits.Problems(document), problem => problem.Text.Contains("(5.26)"));
    }

    [Fact]
    public void AFireAlarmPanelsCircuitIsFireResistingAndFeedsNothingElse()
    {
        // The fire alarm panel on a circuit with the office's sockets, in ordinary cable (5.30a, c).
        var (document, _, corridors, offices) = Office();
        var panel = Put(document, corridors[0], "Fire Alarm Panel - BS EN 54-2", 4000, 300);
        var socket = Put(document, offices[0], "Socket - Double", 5000, 9800);

        // The panel goes on a power circuit from the board and on the alarm's loop.
        Assert.Equal(new[] { SystemKind.Power, SystemKind.FireAlarm }, MepSystems.KindsFor(document, new[] { panel }).OrderBy(kind => kind == SystemKind.FireAlarm));
        var circuit = Circuit(document, "Circuit 1", CircuitUse.Radial, panel, socket);
        var said = FireCircuits.Problems(document).Where(problem => problem.Element == circuit).Select(problem => problem.Text).ToList();
        Assert.Contains(said, text => text.StartsWith("it feeds Fire Alarm Panel - BS EN 54-2, equipment working in a fire, and its cable is not fire-resisting - Approved Document B Volume 2 (5.30a)", StringComparison.Ordinal));
        Assert.Contains(said, text => text.StartsWith("it feeds Fire Alarm Panel - BS EN 54-2 and also Socket - Double - Approved Document B Volume 2 (5.30c)", StringComparison.Ordinal));

        // Its own circuit, in fire-resisting cable: met.
        circuit.Members.Remove(socket.Id);
        circuit.FireResisting = true;
        Assert.DoesNotContain(FireCircuits.Problems(document), problem => problem.Element == circuit);

        // The fire alarm's own wiring - a call point and a sounder - the same (5.30a).
        var alarm = new MepSystem { Kind = SystemKind.FireAlarm, Name = "Alarm Loop 1" };
        alarm.Members.Add(Put(document, corridors[0], "Manual Call Point - BS EN 54-11 Type A", 13500, 1000).Id);
        document.Add(alarm);
        Assert.Contains(FireCircuits.Problems(document), problem => problem.Element == alarm && problem.Text.Contains("(5.30a)"));
        alarm.FireResisting = true;
        Assert.Empty(FireCircuits.Problems(document));

        // Saved and opened again, still fire-resisting.
        var opened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(opened.Find<MepSystem>(circuit.Id)!.FireResisting);
        Assert.True(opened.Find<MepSystem>(alarm.Id)!.FireResisting);
    }
}
