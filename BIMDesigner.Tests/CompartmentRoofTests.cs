using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Where a compartment wall meets the roof (round 135), by Approved Document B Volume 1 5.11 to 5.15 and Volume 2 8.18 to 8.21 and 8.25 to
/// 8.29: the roof 1500 either side BROOF(t4) on a non-combustible deck, a combustible one only in a low building of some uses, or the wall
/// carried up through the roof; and a compartment wall running up to the roof.
/// </summary>
[Collection("Wpf")]
public class CompartmentRoofTests
{
    private const string Brick = "Exterior - Brick on Block 330mm", Party = "Party Wall - Cavity Blockwork 300mm", Slate = "Roof - Slate on Timber Rafters 150mm", Flat = "Roof - Warm Flat 320mm";

    private static Wall Wall(BimDocument document, double x0, double y0, double x1, double y1, string type, double height = 3000)
    {
        var wall = new Wall { Start = new Point2D(x0, y0), End = new Point2D(x1, y1), TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = document.Levels[0].Id, UnconnectedHeight = height };
        document.Add(wall);
        return wall;
    }

    /// <summary>Two units 8 m by 8 m side by side, a wall between them at x 8 m of a type, a flat roof of a type over both 2.7 m up, a door out of each.</summary>
    private static (BimDocument Document, Wall Between, Roof Roof, Room Left, Room Right) Pair(string use, string between, string roofType)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var south = Wall(document, 0, 0, 16000, 0, Brick);
        Wall(document, 16000, 0, 16000, 8000, Brick);
        Wall(document, 16000, 8000, 0, 8000, Brick);
        Wall(document, 0, 8000, 0, 0, Brick);
        var wall = Wall(document, 8000, 0, 8000, 8000, between);
        foreach (var along in new[] { 4000.0, 12000 })
            document.Add(new Door { HostWallId = south.Id, TypeId = document.TypesOf<DoorType>().First(each => each.Name == "Single - 1000 x 2100").Id, LevelId = document.Levels[0].Id, DistanceAlongWall = along });
        var left = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(4000, 4000), Name = "Unit 1" };
        var right = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(12000, 4000), Name = "Unit 2" };
        document.Add(left);
        document.Add(right);
        var roof = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(each => each.Name == roofType).Id, HeightOffset = 2700 };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(16300, -300), new Point2D(16300, 8300), new Point2D(-300, 8300) });
        document.Add(roof);
        return (document, wall, roof, left, right);
    }

    private static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Wall wall) => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard);

    [Fact]
    public void APartyWallsRoofIsBroofOrTheWallIsCarriedUpThroughIt()
    {
        // A pair of semi-detached houses under one slate roof: slate is BROOF(t4) without testing, and its timber under it allowed in a house
        // 15 m high at most (5.12, 5.13).
        var (document, party, roof, _, _) = Pair("Semi-detached House", Party, Slate);
        Assert.InRange(CompartmentRoofs.Height(document), 2700, 3100);
        Assert.DoesNotContain(Problems(document, party), problem => problem.Rule.Contains("(5.12"));

        // A warm flat roof whose covering has no designation given: said (5.12).
        roof.TypeId = document.TypesOf<RoofType>().First(each => each.Name == Flat).Id;
        var said = Assert.Single(Problems(document, party), problem => problem.Rule.Contains("(5.12"));
        Assert.StartsWith("it is a wall common to two buildings under the roof, and its covering, the ", said.Said);
        Assert.EndsWith("has no external fire designation given.", said.Said);
        Assert.StartsWith("Approved Document B (5.12 to 5.15, Diagram 5.2)", said.Rule);

        // The party wall carried up 3.5 m, 480 over the roof's top at 3.02 m: met (5.15).
        party.UnconnectedHeight = 3500;
        Assert.DoesNotContain(Problems(document, party), problem => problem.Rule.Contains("(5.12"));

        // As the Wall tool leaves it: the wall stopping at the roof's underside, a piece on its line standing on the roof's top 480 high: met.
        party.UnconnectedHeight = 2700;
        var parapet = new Wall { Start = party.Start, End = party.End, TypeId = party.TypeId, LevelId = party.LevelId, BaseOffset = 3020, UnconnectedHeight = 480 };
        document.Add(parapet);
        Assert.DoesNotContain(Problems(document, party), problem => problem.Rule.Contains("(5.12"));
        parapet.UnconnectedHeight = 150;
        Assert.Contains(Problems(document, party), problem => problem.Rule.Contains("(5.12"));
    }

    [Fact]
    public void AShopsCompartmentWallAsksANonCombustibleDeckWhereAnOfficesNeedNot()
    {
        // Two offices side by side, a wall common to two buildings between, under a slate roof on timber: a combustible substrate allowed in an
        // office 15 m high at most (8.27).
        var (office, wall, _, _, _) = Pair("Office", Party, Slate);
        Assert.DoesNotContain(Problems(office, wall), problem => problem.Rule.Contains("(8.26"));

        // Two shops: not allowed it - the roof's timber under the slate said (8.26).
        var (shop, between, _, _, _) = Pair("Shop", Party, Slate);
        var said = Assert.Single(Problems(shop, between), problem => problem.Rule.Contains("(8.26"));
        Assert.StartsWith("it is a wall common to two buildings under the roof, and the ", said.Said);
        Assert.Contains("under the covering is class D-s2, d0, shop and commercial, purpose group 4 not being allowed a combustible substrate.", said.Said);
        Assert.StartsWith("Approved Document B Volume 2 (8.26 to 8.29, Diagram 8.2)", said.Rule);

        // The wall carried up 400 over the roof: met (8.29).
        between.UnconnectedHeight = 3270;
        Assert.DoesNotContain(Problems(shop, between), problem => problem.Rule.Contains("(8.26"));
    }

    [Fact]
    public void AWallBetweenSeparateOccupanciesRunsUpToTheRoof()
    {
        // A shop let as two units, A Ltd's and B Ltd's, a 60 minute separating wall between them 2.4 m high under a roof 2.7 m up (8.21, 8.25a).
        var (document, wall, _, left, right) = Pair("Shop", "Separating Wall - Dense Blockwork 215mm", Slate);
        wall.UnconnectedHeight = 2400;
        left.Occupancy = "A Ltd";
        right.Occupancy = "B Ltd";
        Assert.Equal("a compartment wall between separate occupancies", CompartmentRoofs.Why(document, wall));
        Assert.Contains(Problems(document, wall), problem => problem.Said == "it is a compartment wall between separate occupancies in the top storey and stops 300 below the roof."
                                                             && problem.Rule.StartsWith("Approved Document B Volume 2 (8.21, 8.25a)", StringComparison.Ordinal));

        // Up to the roof: met - though its timber deck is still said (8.26).
        wall.UnconnectedHeight = 3000;
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("(8.21"));
        Assert.Contains(Problems(document, wall), problem => problem.Rule.Contains("(8.26"));

        // One tenant: no compartment wall, nothing asked of it or the roof.
        right.Occupancy = "A Ltd";
        Assert.Null(CompartmentRoofs.Why(document, wall));
        Assert.DoesNotContain(Problems(document, wall), problem => problem.Rule.Contains("(8.26"));
    }
}
