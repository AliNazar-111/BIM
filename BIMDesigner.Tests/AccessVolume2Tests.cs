using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// The way into a building other than a dwelling (round 123), by Approved Document M Volume 2 Sections 1 and 2: the approach (1.13), parking
/// (1.18), external steps (1.33), ramps and railings judged as the building's, steps as the only way in (1.6, 2.1), and the landing and cover
/// at the entrance (2.7d, g) - every figure by hand.
/// </summary>
public class AccessVolume2Tests
{
    /// <summary>An office of outside walls 8 m by 6 m, its front door in its south wall 4 m along, on ground of a shape; the ground drawn 100 m by 100 m.</summary>
    private static (BimDocument Document, Door Door) Office(Func<double, double, double> ground, string use = "Office")
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = use;
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(candidate => candidate.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);
            walls.Add(wall);
        }

        var door = new Door { TypeId = document.TypesOf<DoorType>().First(candidate => candidate.Function == DoorFunction.Exterior).Id, LevelId = level, HostWallId = walls[0].Id, DistanceAlongWall = 4000, Mark = "D1" };
        document.Add(door);
        document.Add(new Room { LevelId = level, Location = new Point2D(4000, 3000), Name = "Office" });

        var surface = new Toposurface { LevelId = level };
        var points = new List<Point3D>();
        for (var x = -70000.0; x <= 30000; x += 5000)
        for (var y = -70000.0; y <= 30000; y += 5000)
            points.Add(new Point3D(x, y, ground(x, y)));
        surface.SetPoints(points);
        document.Add(surface);
        return (document, door);
    }

    /// <summary>An approach path so wide from so far south up to the front door.</summary>
    private static GroundRegion Path(BimDocument document, double width, double length = 6000)
    {
        var path = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Path, Approach = true };
        path.SetPath(new[] { new Point2D(4000, -length - 300), new Point2D(4000, -300) }, width);
        document.Add(path);
        return path;
    }

    [Fact]
    public void AnApproachIs15MetresWideFallsLittleAcrossAndHasPassingPlaces()
    {
        var (document, _) = Office((_, _) => 0);
        var path = Path(document, 1200);
        Assert.Contains(GroundRegions.Problems(document, path), said => said.StartsWith("It is 1200 wide: Approved Document M Volume 2 (1.13a) asks an approach to a building other than a dwelling for 1.5 m", StringComparison.Ordinal));
        path.SetPath(path.PathLine.ToList(), 1500);
        Assert.Empty(GroundRegions.Problems(document, path));

        // 60 m long and 1.5 m wide: one passing place 1.8 by 2 m asked, 50 m apart at most.
        var (longer, _) = Office((_, _) => 0);
        var far = Path(longer, 1500, 60000);
        Assert.Contains(GroundRegions.Problems(longer, far), said => said.StartsWith("It runs 60.0 m at 1500 wide with no passing places: Approved Document M Volume 2 (1.13b)", StringComparison.Ordinal));
        var place = new GroundRegion { LevelId = longer.Levels[0].Id, Surface = GroundSurface.Paving };
        place.SetBoundary(new[] { new Point2D(4750, -30000), new Point2D(6550, -30000), new Point2D(6550, -28000), new Point2D(4750, -28000) });
        longer.Add(place);
        Assert.DoesNotContain(GroundRegions.Problems(longer, far), said => said.Contains("(1.13b)"));

        // Ground falling 1 in 30 to the east, across it: 1 in 40 at most (1.13c).
        var (tilted, _) = Office((x, _) => -x / 30);
        Assert.Contains(GroundRegions.Problems(tilted, Path(tilted, 1500)), said => said == "It falls 1:30 across: Approved Document M Volume 2 (1.13c) allows 1 in 40 across an approach.");

        // Laid in gravel: no firm surface (1.13d).
        var gravel = Path(document, 1500);
        gravel.Surface = GroundSurface.Gravel;
        Assert.Contains(GroundRegions.Problems(document, gravel), said => said.Contains("(1.13d)"));
    }

    [Fact]
    public void AnApproachSteeperThan1In60HasALevelLandingEach500MillimetresItRises()
    {
        // Rising 1 in 30 toward the door over 20 m: 667 mm with no level landing - one asked each 500 mm (1.13c).
        var (document, _) = Office((_, y) => y / 30);
        var path = Path(document, 1500, 20000);
        Assert.Equal(20000 / 30.0, Approaches.Climb(document, path)!.Value, 0);
        Assert.Contains(GroundRegions.Problems(document, path), said => said.StartsWith("It climbs 667 between level landings: Approved Document M Volume 2 (1.13c)", StringComparison.Ordinal));

        // 1 in 45: 444 mm, within 500.
        var (gentle, _) = Office((_, y) => y / 45);
        Assert.DoesNotContain(GroundRegions.Problems(gentle, Path(gentle, 1500, 20000)), said => said.Contains("level landing"));

        // 1 in 15: steeper than 1 in 20, ramped access (1.8, 1.19).
        var (steep, _) = Office((_, y) => y / 15);
        Assert.Contains(GroundRegions.Problems(steep, Path(steep, 1500, 6000)), said => said.StartsWith("It falls 1:15 at its steepest: Approved Document M Volume 2 (1.8, 1.19)", StringComparison.Ordinal));
    }

    private static Component Bay(BimDocument document, ComponentForm form, double x, double y)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Form == form);
        var bay = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y) };
        document.Add(bay);
        return bay;
    }

    [Fact]
    public void ParkingHasABayForDisabledPeopleOnLevelGround()
    {
        var (document, _) = Office((_, _) => 0);
        var standard = document.ProjectInformation.ClearanceStandard;
        var bays = new[] { Bay(document, ComponentForm.ParkingBay, -10000, -10000), Bay(document, ComponentForm.ParkingBay, -7000, -10000) };
        var said = bays.SelectMany(bay => Outside.Problems(document, bay, standard)).ToList();
        var none = Assert.Single(said);
        Assert.Equal("none of the 2 parking bays is designated for disabled people.", none.Said);
        Assert.StartsWith("Approved Document M Volume 2 (1.18a)", none.Rule, StringComparison.Ordinal);

        // One designated, 3.6 x 6.0 m on level ground: met.
        var accessible = Bay(document, ComponentForm.AccessibleParkingBay, -3000, -10000);
        Assert.Empty(bays.Append(accessible).SelectMany(bay => Outside.Problems(document, bay, standard)));

        // On ground falling 1 in 40: not level, 1 in 60 at most.
        var (sloping, _) = Office((x, _) => x / 40);
        var tilted = Bay(sloping, ComponentForm.AccessibleParkingBay, -10000, -10000);
        Assert.Contains(Outside.Problems(sloping, tilted, standard), problem => problem.Said == "the ground under it falls 1 in 40." && problem.Rule.Contains("(1.18a)"));
    }

    [Fact]
    public void ExternalStepsToAnOfficeAreApprovedDocumentMs()
    {
        // The library's first stair outside, ground floor to first: 15 risers of 200, goings of 225, 1000 wide.
        var (document, _) = Office((_, _) => 0);
        var stair = new Stair { TypeId = document.TypesOf<StairType>().First().Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Start = new Point2D(-5000, -8000), Angle = 90, Width = 1000, Outside = true };
        document.Add(stair);
        var layout = Stairs.Layout(document, stair);
        Assert.Equal(15, layout.Risers);
        var said = Stairs.Problems(document, stair);
        Assert.Contains($"Each riser is {layout.Rise:0}: Approved Document M Volume 2 (1.33l, m) allows 170 at most on external steps to a building other than a dwelling.", said);
        Assert.Contains(said, each => each.StartsWith($"Each going is {layout.Going:0}: Approved Document M Volume 2 (1.33l, m) asks for 280 at least", StringComparison.Ordinal));
        Assert.Contains("It climbs 15 risers in a flight: Approved Document M Volume 2 (1.33h, Diagram 5) allows external steps 12 a flight with a going under 350.", said);
        Assert.Contains("It is 1000 wide: Approved Document M Volume 2 (1.33f) asks external steps 1.2 m wide between walls, strings or upstands.", said);
        Assert.DoesNotContain(said, each => each.Contains("Approved Document K allows 12"));

        // 2 m wide: handrails dividing it into channels 1 to 1.8 m (1.33p).
        stair.Width = 2000;
        Assert.Contains(Stairs.Problems(document, stair), each => each.Contains("(1.33p)"));

        // A house's stair outside is Approved Document K's, as before.
        var (house, _) = Office((_, _) => 0, "House");
        var homeStair = new Stair { TypeId = house.TypesOf<StairType>().First().Id, LevelId = house.Levels[0].Id, TopLevelId = house.Levels[1].Id, Start = new Point2D(-5000, -8000), Angle = 90, Width = 1000, Outside = true };
        house.Add(homeStair);
        Assert.DoesNotContain(Stairs.Problems(house, homeStair), each => each.Contains("Volume 2"));
    }

    [Fact]
    public void RampsAndRailingsLeftADwellingsAreJudgedAsTheBuildingsOwn()
    {
        var (office, _) = Office((_, _) => 0);
        var ramp = new Ramp { LevelId = office.Levels[0].Id, Start = new Point2D(-5000, -5000) };
        office.Add(ramp);
        Assert.Equal(RailingBuilding.Dwelling, ramp.Building);
        Assert.Equal(RailingBuilding.Other, Ramps.BuildingOf(office, ramp));
        Assert.Equal(1500, Ramps.LeastWidth(office, ramp).Width);

        var railing = new Railing { LevelId = office.Levels[0].Id };
        railing.SetPath(new[] { new Point2D(-5000, -9000), new Point2D(-2000, -9000) });
        office.Add(railing);
        Assert.Equal(RailingBuilding.Other, Railings.BuildingOf(office, railing));
        Assert.Equal(1100, Railings.Required(office, railing).Height);

        var (house, _) = Office((_, _) => 0, "House");
        var homeRamp = new Ramp { LevelId = house.Levels[0].Id, Start = new Point2D(-5000, -5000) };
        house.Add(homeRamp);
        Assert.Equal(RailingBuilding.Dwelling, Ramps.BuildingOf(house, homeRamp));
    }

    [Fact]
    public void AnAccessibleEntranceHasALevelLandingAndWeatherProtection()
    {
        var (document, door) = Office((_, _) => 0);
        var standard = document.ProjectInformation.ClearanceStandard;
        Path(document, 1500);
        Assert.True(Approaches.IsApproachedEntrance(document, door));

        // Level ground, nothing over the door: weather protection asked (2.7g).
        var said = Approaches.EntranceProblems(document, door, standard);
        var cover = Assert.Single(said);
        Assert.Equal("nothing covers it, a door not powered.", cover.Said);
        Assert.StartsWith("Approved Document M Volume 2 (2.7g)", cover.Rule, StringComparison.Ordinal);

        // A canopy over it, 1.2 m deep: met.
        var canopy = new Roof { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat")).Id, HeightOffset = 2500 };
        canopy.SetBoundary(new[] { new Point2D(3000, -1200), new Point2D(5000, -1200), new Point2D(5000, 0), new Point2D(3000, 0) });
        document.Add(canopy);
        Assert.Empty(Approaches.EntranceProblems(document, door, standard));

        // Ground falling 1 in 30 from the door: no level landing 1500 square (2.7d).
        var (sloping, entrance) = Office((_, y) => y / 30);
        Path(sloping, 1500);
        Assert.Contains(Approaches.EntranceProblems(sloping, entrance, standard), problem => problem.Said.StartsWith("the ground outside it falls 1:30", StringComparison.Ordinal) && problem.Rule.Contains("(2.7d)"));
    }
}
