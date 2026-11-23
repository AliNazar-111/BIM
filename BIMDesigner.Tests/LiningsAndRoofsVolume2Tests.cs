using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Linings, roof coverings and plastic rooflights in a building other than a home (round 120), by Approved Document B Volume 2: Table 6.1's
/// classes and 6.4's lower parts, Table 14.1 note 2, and Table 6.2, Diagram 6.3 and Diagram 14.1 for rooflights - every figure by hand.
/// </summary>
[Collection("Wpf")]
public class LiningsAndRoofsVolume2Tests
{
    private const string Partition = "Interior - Partition 125mm";

    /// <summary>A room of partitions so big inside their faces, its walls 3 m high, in a building of a use; named so.</summary>
    private static (BimDocument Document, Room Room, List<Wall> Walls) Room(string use, double width, double depth, string name = "Store", string type = Partition)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var level = document.Levels.Where(each => each.Elevation >= 0).OrderBy(each => each.Elevation).First().Id;
        var half = document.TypesOf<WallType>().First(each => each.Name == type).Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(-half, depth + half), new Point2D(width + half, depth + half), new Point2D(width + half, -half) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        var room = new Room { LevelId = level, Location = new Point2D(width / 2, depth / 2), Name = name };
        document.Add(room);
        return (document, room, walls);
    }

    /// <summary>A partition faced both sides in a material - or, as a wallcovering 1 mm thick, laid over its plasterboard.</summary>
    private static WallType Lined(BimDocument document, string material, bool covering = false)
    {
        var copy = (WallType)document.TypesOf<WallType>().First(each => each.Name == Partition).Duplicate($"Partition - {material}{(covering ? " over board" : "")}");
        var id = document.Materials.First(each => each.Name == material).Id;
        var layers = copy.Structure.Layers.ToList();
        if (covering)
        {
            layers.Insert(0, new MaterialLayer(LayerFunction.Finish1, id, 1));
            layers.Add(new MaterialLayer(LayerFunction.Finish2, id, 1));
        }
        else
            (layers[0].MaterialId, layers[^1].MaterialId) = (id, id);
        copy.Structure.ReplaceLayers(layers);
        document.AddType(copy);
        return copy;
    }

    private static Material Declared(BimDocument document, string name, string fire)
    {
        var material = new Material(name) { Density = 500, ReactionToFire = fire };
        document.AddMaterial(material);
        return material;
    }

    [Fact]
    public void Table61AsksLessOfASmallRoomInAnOfficeThanInAHotelOrAHome()
    {
        // A store 5 m by 5 m, 25 m², lined in softwood, D-s2, d0 by Commission Decision 2005/610/EC.
        foreach (var (use, said) in new[]
                 {
                     // An office: 30 m² or less in non-residential accommodation, D-s3, d2 - met.
                     ("Office", "Linings: D-s3, d2 asked of a room of 30 m² or less in non-residential accommodation, met - walls of Timber, Softwood D-s2, d0"),
                     // A hotel's small room is 4 m² at most: C-s3, d2, its walls - 20 m by 3 m, 60 m² - past half its floor, 12.5 m².
                     ("Hotel", "Linings: 60.0 m² of its walls - the Timber, Softwood - is below C-s3, d2, Approved Document B Volume 2 (6.4) asks less than 12.5 m² of a room's walls below Table 6.1's class - half its floor, 20 m² at most in residential accommodation"),
                     // A house: Volume 1's Table 4.1, 4 m².
                     ("House", "Linings: 60.0 m² of its walls - the Timber, Softwood - is below C-s3, d2, Approved Document B (4.4) asks less than 12.5 m² of a room's walls below Table 4.1's class - half its floor, 20 m² at most")
                 })
        {
            var (document, room, walls) = Room(use, 5000, 5000);
            var softwood = Lined(document, "Timber, Softwood");
            foreach (var wall in walls) wall.TypeId = softwood.Id;
            Assert.Equal(said, Combustibility.LiningVerdict(document, room));
        }

        // A flat over a shop: its room named into the flat is a home's, by Volume 1.
        var (shop, flatRoom, flatWalls) = Room("Shop", 5000, 5000, "Living Room");
        flatRoom.Dwelling = "Flat 1";
        var lined = Lined(shop, "Timber, Softwood");
        foreach (var wall in flatWalls) wall.TypeId = lined.Id;
        Assert.Contains("Approved Document B (4.4) asks", Combustibility.LiningVerdict(shop, flatRoom));
    }

    [Fact]
    public void AnOfficesRoomMayHave60SquareMetresOfLowerWalls()
    {
        // An office 15 m by 10 m, 150 m²: one long wall in softwood, 15 m by 3 m - 45 m² - against the less of half its floor, 75, and 60 (6.4b).
        var (office, room, walls) = Room("Office", 15000, 10000, "Office");
        walls[1].TypeId = Lined(office, "Timber, Softwood").Id;
        Assert.Equal("Linings: C-s3, d2 asked of a room, met - walls of Gypsum Plasterboard A2-s1, d0 and Timber, Softwood D-s2, d0", Combustibility.LiningVerdict(office, room));

        // A hotel's room: 20 m² at most (6.4a).
        office.ProjectInformation.BuildingType = "Hotel";
        Assert.Contains("45.0 m² of its walls - the Timber, Softwood - is below C-s3, d2, Approved Document B Volume 2 (6.4) asks less than 20.0 m²", Combustibility.LiningVerdict(office, room));

        // Foam of class E anywhere: worse than D-s3, d2, which no part may be.
        office.ProjectInformation.BuildingType = "Office";
        Declared(office, "Acoustic Panel, Foam", "E");
        walls[0].TypeId = Lined(office, "Acoustic Panel, Foam").Id;
        Assert.Contains("the Acoustic Panel, Foam on its walls is E - Approved Document B Volume 2 (Table 6.1, 6.4) asks C-s3, d2 of a room's linings, and no part of them worse than D-s3, d2", Combustibility.LiningVerdict(office, room));
        Assert.Contains(Warnings.RoomShortfalls(office, room), shortfall => shortfall.Contains("Table 6.1, 6.4"));
    }

    [Fact]
    public void ACirculationSpaceIsBs3d2OrAWallcoveringOnBoardAsNote1Allows()
    {
        // A corridor 2 m by 10 m in an office, plasterboard A2-s1, d0: B-s3, d2 asked, met.
        var (document, corridor, walls) = Room("Office", 2000, 10000, "Corridor");
        Assert.Equal("Linings: B-s3, d2 asked of a circulation space, met - walls of Gypsum Plasterboard A2-s1, d0", Combustibility.LiningVerdict(document, corridor));

        // A vinyl wallcovering of C-s3, d2 bonded to the plasterboard (BS EN 15102, note 1): met.
        Declared(document, "Vinyl Wallcovering", "C-s3, d2");
        var covered = Lined(document, "Vinyl Wallcovering", covering: true);
        foreach (var wall in walls) wall.TypeId = covered.Id;
        Assert.Equal("Linings: B-s3, d2 asked of a circulation space, met - walls of Vinyl Wallcovering C-s3, d2 on Gypsum Plasterboard, a wallcovering as note 1 allows", Combustibility.LiningVerdict(document, corridor));

        // C-s3, d2 boarding that is not a wallcovering: below B-s3, d2 on all 24 m by 3 m, 72 m², against half the floor, 10 m².
        Declared(document, "Plywood, Fire Retardant", "C-s3, d2");
        var plywood = Lined(document, "Plywood, Fire Retardant");
        foreach (var wall in walls) wall.TypeId = plywood.Id;
        Assert.Contains("72.0 m² of its walls - the Plywood, Fire Retardant - is below B-s3, d2, Approved Document B Volume 2 (6.4) asks less than 10.0 m²", Combustibility.LiningVerdict(document, corridor));
    }

    /// <summary>A warehouse 8 m by 6 m - 144 m³ of room - under a felt roof of DROOF(t4) 13 m from the boundary.</summary>
    [Fact]
    public void Table141Note2KeepsTheLowerCoveringsOffAnyIndustrialOrStorageBuilding()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = ground.Id, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = ground.Id, Location = new Point2D(4000, 3000), Name = "Store" });
        var felt = new Material("Roof Felt, Torch-On") { Density = 1100, RoofCovering = "DROOF(t4)" };
        document.AddMaterial(felt);
        var type = new RoofType("Roof - Felt on Rafters", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, felt.Id, 5), new MaterialLayer(LayerFunction.Structure, document.Materials.First(each => each.Name == "Timber, Softwood").Id, 150)));
        document.AddType(type);
        var roof = new Roof { LevelId = first.Id, TypeId = type.Id };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(8300, -300), new Point2D(8300, 6300), new Point2D(-300, 6300) });
        document.Add(roof);
        var line = new PropertyLine { LevelId = ground.Id };
        line.SetBoundary(new[] { new Point2D(-30000, -13300), new Point2D(40000, -13300), new Point2D(40000, 40000), new Point2D(-30000, 40000) });
        document.Add(line);

        // A house of 144 m³: DROOF(t4) 12 m or more off is allowed, note 2 reaching only buildings over 1500 m³.
        Assert.Empty(RoofCoverings.Problems(document, roof));

        // A warehouse: note 2 keeps it off an industrial or storage building of any size.
        document.ProjectInformation.BuildingType = "Warehouse";
        var problem = Assert.Single(RoofCoverings.Problems(document, roof));
        Assert.Equal("Approved Document B Volume 2 (Table 14.1, note 2) does not allow a DROOF(t4) covering 12 m to 20 m from the relevant boundary on storage, purpose group 7, any size.", problem.Rule);

        // An office of the same size: allowed.
        document.ProjectInformation.BuildingType = "Office";
        Assert.Empty(RoofCoverings.Problems(document, roof));
    }

    /// <summary>The house of the rooflight tests, 10 m by 8 m pitched 40°, its room so named, in a building of a use - rooflights of a material at plan points.</summary>
    private static (BimDocument Document, Roof Roof, RoofWindowType Type, Material Glass) Rooflit(string use, string glass, string? fire, double width, double height, params (double X, double Y)[] at)
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        document.ProjectInformation.BuildingType = use;
        var line = new PropertyLine { LevelId = document.Levels[0].Id };
        line.SetBoundary(new[] { new Point2D(-30000, -30000), new Point2D(40000, -30000), new Point2D(40000, 40000), new Point2D(-30000, 40000) });
        document.Add(line);
        document.Add(new Room { LevelId = document.Levels[0].Id, Location = new Point2D(5000, 4000), Name = "Office" });
        var material = new Material(glass) { ReactionToFire = fire ?? string.Empty };
        document.AddMaterial(material);
        var type = document.TypesOf<RoofWindowType>().First(each => each.Name == "Rooflight - Fixed 1000 x 1000").Duplicate($"Rooflight - {glass}");
        (type.Width, type.Height, type.GlassMaterialId) = (width, height, material.Id);
        document.AddType(type);
        foreach (var (x, y) in at)
            document.Add(new RoofWindow { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, Location = new Point2D(x, y) });
        return (document, roof, type, material);
    }

    private static List<string> Said(BimDocument document, Roof roof) => Rooflights.Problems(document, roof).Select(each => $"{each.Said} {each.Rule}").ToList();

    [Fact]
    public void RooflightsOf1SquareMetreMayStandAsFarApartAsTheirLongestSide()
    {
        // Eight TP(b) rooflights 1 m square on the south slope, two rows of four - 1.2 m apart along the slope's foot, and in plan 1.8 m
        // between row centres less 0.77 m of each, 1.03 m, across it: 8 m² within 3 m of each other.
        var points = new[] { 1000.0, 3200, 5400, 7600 }.SelectMany(x => new[] { (x, 1200.0), (x, 3000.0) }).ToArray();
        var (house, houseRoof, _, _) = Rooflit("House", "Polycarbonate, Multiwall 16mm", null, 1000, 1000, points);
        Assert.Contains(Said(house, houseRoof), said => said.Contains("8.0 m² together") && said.Contains("Approved Document B (Diagram 12.1, Table 4.2)"));

        // In an office, each 1 m² at most, each as far from the next as its longest side, 1 m in plan (Table 6.2, Diagram 6.3): met.
        var (office, officeRoof, _, _) = Rooflit("Office", "Polycarbonate, Multiwall 16mm", null, 1000, 1000, points);
        Assert.Empty(Said(office, officeRoof));

        // Rooflights 1.2 m square are past 1 m²: grouped by 3 m again, as Volume 2's Diagram 14.1 has it.
        var (bigger, biggerRoof, _, _) = Rooflit("Office", "Polycarbonate, Multiwall 16mm", null, 1200, 1200, (1000, 1200), (3600, 1200), (6200, 1200), (8800, 1200));
        Assert.Contains(Said(bigger, biggerRoof), said => said.Contains("5.8 m² together") && said.Contains("Approved Document B Volume 2 (Diagram 14.1, Table 6.2)"));
    }

    [Fact]
    public void ClassDRooflightsOverAnOfficeRoomMayBe1800Apart()
    {
        // Two GRP rooflights of class D-s3, d2, 2 m by 1.5 m - 3 m² each, 6 m² of the 80 m² floor, 7.5% - 2 m apart: within 3 m of each other.
        var points = new[] { (2500.0, 2000.0), (6500.0, 2000.0) };
        var (house, houseRoof, _, _) = Rooflit("House", "GRP Rooflight Sheet", "D-s3, d2", 2000, 1500, points);
        Assert.Contains(Said(house, houseRoof), said => said.Contains("6.0 m² together"));

        // An office, purpose group 3: 20% of the floor at most, 1800 apart (Table 6.2 note 6) - met.
        var (office, officeRoof, _, _) = Rooflit("Office", "GRP Rooflight Sheet", "D-s3, d2", 2000, 1500, points);
        Assert.Empty(Said(office, officeRoof));

        // A hotel is residential, purpose group 2(b): note 6 is not for it.
        var (hotel, hotelRoof, _, _) = Rooflit("Hotel", "GRP Rooflight Sheet", "D-s3, d2", 2000, 1500, points);
        Assert.Contains(Said(hotel, hotelRoof), said => said.Contains("6.0 m² together") && said.Contains("Volume 2 (Diagram 14.1, Table 6.2)"));

        // 1.5 m apart in the office: closer than note 6's 1800, 6 m² together.
        var (close, closeRoof, _, _) = Rooflit("Office", "GRP Rooflight Sheet", "D-s3, d2", 2000, 1500, (2500, 2000), (6000, 2000));
        Assert.Contains(Said(close, closeRoof), said => said.Contains("within 1.8 m of each other, 6.0 m² together") && said.Contains("Table 6.2 note 6"));
    }

    /// <summary>
    /// The plan's Building Type offers the uses Volume 2's rules know - before round 120 only homes, an office, an extension and "Other" could be
    /// picked, so a hotel, a shop or a warehouse could not be set from the window at all.
    /// </summary>
    [Fact]
    public void EveryBuildingTypeOfferedIsReadAsItsPurposeGroup()
    {
        var expected = new Dictionary<string, PurposeGroup?>
        {
            ["Detached house"] = null, ["Semi-detached house"] = null, ["Terraced house"] = null, ["Bungalow"] = null, ["Flat"] = null, ["Extension"] = null,
            ["Office"] = PurposeGroup.Office, ["Shop"] = PurposeGroup.ShopAndCommercial, ["Restaurant"] = PurposeGroup.ShopAndCommercial,
            ["Hotel"] = PurposeGroup.ResidentialOther, ["Hostel"] = PurposeGroup.ResidentialOther, ["Care home"] = PurposeGroup.ResidentialInstitutional,
            ["Hospital"] = PurposeGroup.ResidentialInstitutional, ["School"] = PurposeGroup.AssemblyAndRecreation, ["Library"] = PurposeGroup.AssemblyAndRecreation,
            ["Theatre"] = PurposeGroup.AssemblyAndRecreation, ["Sports centre"] = PurposeGroup.AssemblyAndRecreation, ["Factory"] = PurposeGroup.Industrial,
            ["Warehouse"] = PurposeGroup.Storage, ["Other"] = PurposeGroup.Office
        };
        Assert.Equal(expected.Keys.Order(), BIMDesigner.UI.StartUp.NewProjectSteps.BuildingTypes.Order());

        var document = BimDocument.CreateDefault();
        foreach (var (type, group) in expected)
        {
            document.ProjectInformation.BuildingType = type;
            Assert.Equal(group is not null, HorizontalEscape.Applies(document));
            if (group is { } known) Assert.Equal(known, HorizontalEscape.Purpose(document));
        }
    }
}
