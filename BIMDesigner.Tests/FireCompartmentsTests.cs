using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Fire separation in a building other than a dwelling by Approved Document B Volume 2 (round 112): Table B2's periods, floors and compartment
/// floors, Table 8.1's compartment sizes, places of special fire hazard, sprinklers.
/// </summary>
[Collection("Wpf")]
public class FireCompartmentsTests
{
    private const string Brick = "Exterior - Brick on Block 330mm";

    private static Wall Wall(BimDocument document, Guid level, Point2D from, Point2D to, string type = "Interior - Partition 125mm")
    {
        var wall = new Wall { Start = from, End = to, TypeId = document.TypesOf<WallType>().First(each => each.Name == type).Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(wall);
        return wall;
    }

    private static Room Box(BimDocument document, Guid level, string name, double width, double depth)
    {
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        for (var i = 0; i < 4; i++) Wall(document, level, corners[i], corners[(i + 1) % 4], Brick);
        var room = new Room { LevelId = level, Location = new Point2D(width / 2, depth / 2), Name = name };
        document.Add(room);
        return room;
    }

    [Fact]
    public void Table2GivesEachPurposeGroupItsPeriod()
    {
        int Minutes(PurposeGroup group, double top, bool sprinklered = false) => FireDesign.Period(group, 2, top, sprinklered).Minutes;
        Assert.Equal(30, Minutes(PurposeGroup.Office, 4));
        Assert.Equal(60, Minutes(PurposeGroup.Office, 12));
        Assert.Equal(30, Minutes(PurposeGroup.Office, 12, true));
        Assert.Equal(60, Minutes(PurposeGroup.ShopAndCommercial, 4));
        Assert.Equal(30, Minutes(PurposeGroup.ShopAndCommercial, 4, true));
        Assert.Equal(90, Minutes(PurposeGroup.AssemblyAndRecreation, 25));
        Assert.Equal(90, Minutes(PurposeGroup.Industrial, 10));
        Assert.Equal(120, Minutes(PurposeGroup.Storage, 25));
        Assert.Equal(90, Minutes(PurposeGroup.ResidentialOther, 20));
        Assert.Contains("not allowed without sprinklers", FireDesign.Period(PurposeGroup.Office, 12, 35, false).Said);
        Assert.Equal(120, Minutes(PurposeGroup.Office, 35, true));
        Assert.Equal(new[] { 60, 90, 60, 120, 90 }, new[]
        {
            FireDesign.BasementPeriod(PurposeGroup.Office, 4, false), FireDesign.BasementPeriod(PurposeGroup.Office, 12, false), FireDesign.BasementPeriod(PurposeGroup.Office, 12, true),
            FireDesign.BasementPeriod(PurposeGroup.Industrial, 12, false), FireDesign.BasementPeriod(PurposeGroup.Industrial, 4, false)
        });

        // The structure's period for a building other than a dwelling: no longer "set the building type".
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        Box(document, document.Levels[0].Id, "Shop", 10000, 8000);
        Assert.StartsWith("60 minutes: shop and commercial, purpose group 4", FireDesign.PeriodOf(document).Said);
    }

    [Fact]
    public void AFloorStandsTheBuildingsPeriodAndIsACompartmentFloorWhereAsked()
    {
        // A two storey shop: its first floor of timber, 30 minutes, where Table B2 asks 60.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        foreach (var level in document.Levels.OrderBy(level => level.Elevation).Take(2)) Box(document, level.Id, "Shop", 10000, 8000);
        var upper = document.Levels.OrderBy(level => level.Elevation).Skip(1).First();
        var floor = new Floor { LevelId = upper.Id, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Timber 220mm").Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);
        var standard = document.ProjectInformation.ClearanceStandard;
        var said = Assert.Single(SlabRules.Problems(document, floor, standard), problem => problem.Rule.Contains("Volume 2 (Tables B1, B2"));
        Assert.Equal("it resists fire for 30 minutes.", said.Said);
        Assert.Equal("Approved Document B Volume 2 (Tables B1, B2) asks REI 60 of a floor of shop and commercial, purpose group 4.", said.Rule);

        // A hotel: every floor a compartment floor (8.10), its period 30 minutes to 5 m - met by the timber floor.
        document.ProjectInformation.BuildingType = "Hotel";
        Assert.Equal("every floor of an other residential building a compartment floor (8.10)", FireCompartments.CompartmentFloor(document, floor));
        Assert.DoesNotContain(SlabRules.Problems(document, floor, standard), problem => problem.Rule.Contains("Volume 2 (Tables B1, B2"));
    }

    [Fact]
    public void AShopsCompartmentIsNoBiggerThanTable81Allows()
    {
        // A single storey shop 51 m by 50 m - about 2500 m² in one compartment: 2000 asked without sprinklers.
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Shop";
        var level = document.Levels[0].Id;
        Box(document, level, "Sales", 51000, 50000);
        var said = Assert.Single(FireCompartments.Problems(document));
        Assert.Matches(@"is 2\d{3} m² in one compartment - 1 room joined through walls, doors or room separators of less than 60 minutes - Approved Document B Volume 2 \(8\.11a, Table 8\.1\) allows 2000 m²", said.Said);

        // Divided at x 30 m by a 60 minute compartment wall: two compartments under 2000 - met. (Not through the room marker at x 25.5 m.)
        Wall(document, level, new Point2D(30000, 0), new Point2D(30000, 50000), "Party Wall - Dense Cavity Blockwork 300mm");
        document.Add(new Room { LevelId = level, Location = new Point2D(40000, 25000), Name = "Sales East" });
        Assert.Empty(FireCompartments.Problems(document));
        Assert.Equal(2, FireCompartments.Compartments(document, level, 60).Count);

        // A room separator in place of the wall: one compartment again.
        document.Remove(document.Walls.Single(wall => wall.Start == new Point2D(30000, 0)));
        document.Add(new RoomSeparator { LevelId = level, Start = new Point2D(30000, 0), End = new Point2D(30000, 50000) });
        Assert.Single(FireCompartments.Problems(document));

        // Sprinklered, a single storey shop: no limit.
        document.ProjectInformation.Sprinklered = true;
        Assert.Empty(FireCompartments.Problems(document));
    }

    [Fact]
    public void APlaceOfSpecialFireHazardIsEnclosedAndATallBuildingSprinklered()
    {
        // An office with a boiler room behind an unrated partition and door: REI 30 and E 30 asked (8.7, Table C1).
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        var level = document.Levels[0].Id;
        Box(document, level, "Office", 12000, 8000);
        var plain = new WallType("Interior - Unrated Partition 100mm", new CompoundStructure(new MaterialLayer(LayerFunction.Finish2, document.Materials.First(material => material.Name == "Gypsum Plasterboard").Id, 100))) { Function = WallFunction.Interior };
        document.AddType(plain);
        var partition = Wall(document, level, new Point2D(9000, 0), new Point2D(9000, 8000), plain.Name);
        document.Add(new Room { LevelId = level, Location = new Point2D(10500, 4000), Name = "Boiler Room" });
        var unrated = new DoorType("Unrated Door - 900 x 2100", 900, 2100);
        document.AddType(unrated);
        var door = new Door { HostWallId = partition.Id, TypeId = unrated.Id, LevelId = level, DistanceAlongWall = 4000 };
        document.Add(door);
        var standard = document.ProjectInformation.ClearanceStandard;
        Assert.Contains(WallRules.Problems(document, partition, standard), problem => problem.Said == "it encloses the Boiler Room, a place of special fire hazard, not fire-rated." && problem.Rule.StartsWith("Approved Document B Volume 2 (8.7, Table B1)", StringComparison.Ordinal));
        Assert.Contains(Doors.Problems(document, door, standard), problem => problem.Said == "it opens into the Boiler Room, a place of special fire hazard, not fire-rated." && problem.Rule.StartsWith("Approved Document B Volume 2 (Table C1 item 10a)", StringComparison.Ordinal));
        plain.FireRating = "30 min";
        unrated.FireRating = "30 min";
        Assert.DoesNotContain(WallRules.Problems(document, partition, standard), problem => problem.Rule.Contains("(8.7"));
        Assert.DoesNotContain(Doors.Problems(document, door, standard), problem => problem.Rule.Contains("item 10a"));

        // A storey 33 m up and no sprinklers: sprinklers asked (Table B2 note 6); a care home likewise (2.46).
        var high = new Level { Name = "Eleventh Floor", Elevation = 33000 };
        document.AddLevel(high);
        Box(document, high.Id, "Office 11", 12000, 8000);
        Assert.Contains(FireCompartments.Problems(document), problem => problem.Said.StartsWith("its top storey is 33.0 m up and the building has no sprinklers - Approved Document B Volume 2 (Table B2 note 6)", StringComparison.Ordinal));
        document.ProjectInformation.Sprinklered = true;
        Assert.DoesNotContain(FireCompartments.Problems(document), problem => problem.Said.Contains("sprinklers"));
        document.ProjectInformation.Sprinklered = false;
        document.ProjectInformation.BuildingType = "Care Home";
        Assert.Contains(FireCompartments.Problems(document), problem => problem.Said.StartsWith("it is a care home without sprinklers - Approved Document B Volume 2 (2.46)", StringComparison.Ordinal));
    }
}
