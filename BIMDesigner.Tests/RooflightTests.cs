using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Plastic rooflights (round 95), by Approved Document B Volume 1: thermoplastics' classes by B13, Tables 4.2, 12.2 and 12.3 and Diagram 12.1
/// read off the pages; and in a block of flats, sprinklers over 11 m (7.4) and compartment walls to the floor or roof over them (7.10, 7.11).
/// </summary>
[Collection("Wpf")]
public class RooflightTests
{
    [Fact]
    public void AThermoplasticsClassIsReadOrTakenFromItsName()
    {
        Assert.True(Thermoplastic.TryParse("TP(a) rigid", out var rigid) && rigid == ThermoplasticClass.TpaRigid);
        Assert.True(Thermoplastic.TryParse("tpa flexible", out var flexible) && flexible == ThermoplasticClass.TpaFlexible);
        Assert.True(Thermoplastic.TryParse("TP(b)", out var b) && b == ThermoplasticClass.Tpb);
        Assert.True(Thermoplastic.TryParse("TP(a)", out var a) && a == ThermoplasticClass.TpaRigid);
        Assert.False(Thermoplastic.TryParse("TP(c)", out _));
        Assert.False(Thermoplastic.TryParse("TP(b) rigid", out _));

        Assert.Equal(ThermoplasticClass.Tpb, Thermoplastic.Typical("Polycarbonate, Multiwall 16mm")!.Value.Class);
        Assert.Equal(ThermoplasticClass.TpaRigid, Thermoplastic.Typical("Polycarbonate, Solid 4mm")!.Value.Class);
        Assert.Equal(ThermoplasticClass.Tpb, Thermoplastic.Typical("Polycarbonate, Solid 2mm")!.Value.Class);
        Assert.Equal(ThermoplasticClass.Tpb, Thermoplastic.Typical("Polycarbonate")!.Value.Class);
        Assert.Equal(ThermoplasticClass.TpaRigid, Thermoplastic.Typical("uPVC Sheet, Rigid")!.Value.Class);
        Assert.Null(Thermoplastic.Typical("Glass, Clear"));

        // Given in the project: kept, and saved.
        var document = BimDocument.CreateDefault();
        var acrylic = new Material("Acrylic Dome") { Thermoplastic = "TP(a) rigid" };
        document.AddMaterial(acrylic);
        Assert.Equal((ThermoplasticClass.TpaRigid, "as given"), Thermoplastic.Of(acrylic)!.Value);
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal("TP(a) rigid", opened.Materials.Single(material => material.Name == "Acrylic Dome").Thermoplastic);
    }

    /// <summary>
    /// A house 10 m by 8 m under a roof pitched 40° from its south and north eaves, a room under it - so named - and the site's boundary so
    /// far east of it; a rooflight type fixed and so big, glazed with a material of that name, put on the south slope at x 3 m, y 2.5 m.
    /// </summary>
    private static (BimDocument Document, Roof Roof, Room Room, RoofWindowType Type, Material Glass, PropertyLine Line) House(string room, double boundary, string glass, double size = 1000)
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var line = new PropertyLine { LevelId = document.Levels[0].Id };
        line.SetBoundary(new[] { new Point2D(-30000, -30000), new Point2D(10000 + boundary, -30000), new Point2D(10000 + boundary, 30000), new Point2D(-30000, 30000) });
        document.Add(line);
        var under = new Room { LevelId = document.Levels[0].Id, Location = new Point2D(5000, 4000), Name = room };
        document.Add(under);
        var material = new Material(glass);
        document.AddMaterial(material);
        var type = document.TypesOf<RoofWindowType>().First(each => each.Name == "Rooflight - Fixed 1000 x 1000").Duplicate($"Rooflight - {glass}");
        (type.Width, type.Height, type.GlassMaterialId) = (size, size, material.Id);
        document.AddType(type);
        document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(3000, 2500) });
        return (document, roof, under, type, material, line);
    }

    private static List<string> Said(BimDocument document, Roof roof) => Rooflights.Problems(document, roof).Select(each => $"{each.Said} {each.Rule}").ToList();

    [Fact]
    public void ATpbRooflightIsKept6mFromTheBoundaryAnd5SquareMetres()
    {
        // Multiwall polycarbonate, TP(b), over a living room, the boundary 2 m east of the house: about 9 m from the rooflight - met.
        var (document, roof, room, type, glass, line) = House("Living Room", 2000, "Polycarbonate, Multiwall 16mm");
        Assert.Empty(Said(document, roof));

        // The boundary 2 m west of the house instead: the rooflight about 4.5 m from it - Table 12.3 asks 6 m.
        line.SetBoundary(new[] { new Point2D(-2000, -30000), new Point2D(30000, -30000), new Point2D(30000, 30000), new Point2D(-2000, 30000) });
        var near = Assert.Single(Said(document, roof));
        Assert.StartsWith("its rooflight, the Rooflight - Polycarbonate, Multiwall 16mm glazed with Polycarbonate, Multiwall 16mm (TP(b)) is 4.", near, StringComparison.Ordinal);
        Assert.EndsWith("Approved Document B (Table 12.3) keeps a TP(b) rooflight 6 m from the relevant boundary.", near, StringComparison.Ordinal);

        // Class C-s3, d2 by test: taken as BROOF(t4), no distance asked (12.7).
        glass.ReactionToFire = "C-s3, d2";
        Assert.Empty(Said(document, roof));
        glass.ReactionToFire = string.Empty;
        line.SetBoundary(new[] { new Point2D(-30000, -30000), new Point2D(30000, -30000), new Point2D(30000, 30000), new Point2D(-30000, 30000) });

        // 2.5 m square, 6.25 m² up the slope: over 5 m² (Diagram 12.1).
        (type.Width, type.Height) = (2500, 2500);
        Assert.Contains(Said(document, roof), said => said.Contains("is 6.3 m². Approved Document B (Diagram 12.1, Table 4.2)", StringComparison.Ordinal));
        (type.Width, type.Height) = (1000, 1000);

        // Five more along the slope, 0.5 m apart: six within 3 m of each other, 6 m² together - over 5 m² (Diagram 12.1).
        foreach (var x in new[] { 1500.0, 4500, 6000, 7500, 9000 }) document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(x, 2500) });
        Assert.Contains(Said(document, roof), said => said.StartsWith("its rooflights Rooflight - Polycarbonate, Multiwall 16mm stand within 3 m of each other, 6.0 m² together.", StringComparison.Ordinal));

        // Over a hall - a circulation space - Table 4.2's 15% of its floor: 6 m² over its 72 m² is 8% - met; the share is said where over.
        room.Name = "Hall";
        Assert.DoesNotContain(Said(document, roof), said => said.Contains("(Table 4.2) allows"));
    }

    [Fact]
    public void ClassDAndTpaRigidRooflightsByTheirOwnTables()
    {
        // An acrylic dome of class D-s3, d2, no external designation given: taken as FROOF(t4), 20 m (Table 12.2); the boundary about 11 m off.
        var (document, roof, room, _, glass, line) = House("Living Room", 4000, "Acrylic Dome");
        glass.ReactionToFire = "D-s3, d2";
        Assert.Contains(Said(document, roof), said => said.Contains(", no external designation given - taken as FROOF(t4). Approved Document B (Table 12.2)"));

        // DROOF(t4): 6 m - met.
        glass.RoofCovering = "DROOF(t4)";
        Assert.Empty(Said(document, roof));

        // Of no class at all: a plastic wanting one (4.7b).
        (glass.ReactionToFire, glass.RoofCovering) = (string.Empty, string.Empty);
        Assert.Contains(Said(document, roof), said => said.Contains("- a plastic of no class given. Approved Document B (4.7b, 4.14a)"));

        // TP(a) rigid by its class: 6 m from the boundary, the boundary 2 m west - but over a garage of 40 m² at most, no limit.
        glass.Thermoplastic = "TP(a) rigid";
        line.SetBoundary(new[] { new Point2D(-2000, -30000), new Point2D(30000, -30000), new Point2D(30000, 30000), new Point2D(-2000, 30000) });
        Assert.Contains(Said(document, roof), said => said.Contains("Approved Document B (Table 12.3) keeps a TP(a) rigid rooflight 6 m"));
        var wall = document.Walls.First();
        var partition = new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = document.TypesOf<WallType>().First(type => type.Name == "Interior - Partition 125mm").Id, LevelId = wall.LevelId, UnconnectedHeight = 3000 };
        document.Add(partition);
        room.Location = new Point2D(2500, 4000);
        room.Name = "Garage";
        Assert.Empty(Said(document, roof));

        // TP(a) flexible: not a rooflight's (4.14a).
        glass.Thermoplastic = "TP(a) flexible";
        Assert.Contains(Said(document, roof), said => said.Contains("has a TP(a) flexible lower surface."));
    }

    [Fact]
    public void APlasticRooflightIsKept15mFromACompartmentWall()
    {
        // A party wall under the roof at x 3.8 m, 0.3 m thick: the rooflight's edge at 3.5 m - 0.15 m off its face.
        var (document, roof, _, _, _, _) = House("Living Room", 20000, "Polycarbonate, Multiwall 16mm");
        var party = document.TypesOf<WallType>().First(type => type.Separating);
        document.Add(new Wall { Start = new Point2D(3800, 0), End = new Point2D(3800, 8000), TypeId = party.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 });
        Assert.Contains(Said(document, roof), said => said.Contains("from a compartment wall under the roof. Approved Document B (Diagram 5.2"));
    }

    /// <summary>A block of flats as the fire separation tests build it: 12 m by 6 m on so many storeys, a stair, a corridor and a flat on each.</summary>
    private static (BimDocument Document, List<Level> Levels) Block(int storeys, double flatWallHeight = 3000)
    {
        var document = BimDocument.CreateDefault();
        var levels = new List<Level> { document.Levels[0], document.Levels[1] };
        for (var i = 2; i < storeys; i++)
        {
            var level = new Level { Name = $"Level {i}", Elevation = 3000 * i };
            document.AddLevel(level);
            levels.Add(level);
        }

        WallType Type(string name) => document.TypesOf<WallType>().First(type => type.Name == name);
        var doors = document.TypesOf<DoorType>().First(type => type.Name == "Fire Door FD30S - 900 x 2100");
        for (var i = 0; i < storeys; i++)
        {
            var id = levels[i].Id;
            var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) };
            for (var k = 0; k < 4; k++) document.Add(new Wall { Start = corners[k], End = corners[(k + 1) % 4], TypeId = Type("Exterior - Brick on Block 330mm").Id, LevelId = id, UnconnectedHeight = 3000 });
            var west = new Wall { Start = new Point2D(3000, 0), End = new Point2D(3000, 6000), TypeId = Type("Interior - Partition 125mm").Id, LevelId = id, UnconnectedHeight = 3000 };
            var north = new Wall { Start = new Point2D(3000, 2000), End = new Point2D(12000, 2000), TypeId = Type("Interior - Partition 125mm").Id, LevelId = id, UnconnectedHeight = flatWallHeight };
            document.Add(west);
            document.Add(north);
            document.Add(new Door { HostWallId = west.Id, TypeId = doors.Id, LevelId = id, DistanceAlongWall = 1000 });
            document.Add(new Door { HostWallId = north.Id, TypeId = doors.Id, LevelId = id, DistanceAlongWall = 5000 });
            document.Add(new Room { LevelId = id, Location = new Point2D(1500, 3000), Name = "Stair" });
            document.Add(new Room { LevelId = id, Location = new Point2D(7500, 1000), Name = "Corridor" });
            document.Add(new Room { LevelId = id, Location = new Point2D(7500, 4000), Name = "Living Room", Dwelling = $"Flat {i + 1}" });
            if (i + 1 < storeys)
                document.Add(new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = id, TopLevelId = levels[i + 1].Id, Start = new Point2D(1500, 600), Angle = 90, Width = 900 });
        }

        return (document, levels);
    }

    [Fact]
    public void ABlockOver11mIsSprinkleredAndItsCompartmentWallsReachWhatIsOverThem()
    {
        // Five storeys, 12 m up, not sprinklered (7.4).
        var (document, levels) = Block(5);
        var sprinklers = Escape.Problems(document).Where(problem => problem.Said.Contains("(7.4")).ToList();
        Assert.Equal("the block's top storey is 12.0 m up and no flat has sprinklers - Approved Document B (7.4, Table B4 note 2) asks a block of flats with a top storey over 11 m a sprinkler system throughout, in each flat (Appendix E): tick Sprinklers Throughout, or put sprinklers in every flat",
            Assert.Single(sprinklers).Said);
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(Escape.Problems(document), problem => problem.Said.Contains("(7.4"));

        // Three storeys, the flats' walls 2.4 m high: under a timber floor 220 mm thick, 380 mm short of it (7.10); in the top storey under a
        // flat roof 3 m up, 600 mm short of it (7.11).
        (document, levels) = Block(3, 2400);
        var timber = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Timber 220mm");
        var floor = new Floor { LevelId = levels[1].Id, TypeId = timber.Id };
        floor.SetBoundary(new[] { new Point2D(165, 165), new Point2D(11835, 165), new Point2D(11835, 5835), new Point2D(165, 5835) });
        document.Add(floor);
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = levels[2].Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 6000), new Point2D(0, 6000) });
        document.Add(roof);
        List<string> Reach(Guid level) => document.Walls.Where(wall => wall.LevelId == level && wall.Start == new Point2D(3000, 2000))
            .SelectMany(wall => WallRules.Problems(document, wall, document.ProjectInformation.ClearanceStandard)).Where(problem => problem.Rule.Contains("(7.10)") || problem.Rule.Contains("(7.11")).Select(problem => $"{problem.Said} {problem.Rule}").ToList();
        Assert.StartsWith("it separates a flat from the rest of the building and stops 380 below the floor over it. Approved Document B (7.10)", Assert.Single(Reach(levels[0].Id)));
        Assert.Empty(Reach(levels[1].Id));
        Assert.StartsWith("it separates a flat from the rest of the building in the top storey and stops 600 below the roof. Approved Document B (7.11, 5.11)", Assert.Single(Reach(levels[2].Id)));
    }
}
