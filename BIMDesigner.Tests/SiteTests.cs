using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.ProductExtension;

namespace BIMDesigner.Tests;

/// <summary>
/// The site: pads graded into the ground, battered back to it; regions raised or sunk; trees and
/// cars standing on the ground; a basement's dig; the ground simplified; property lines with
/// their bearings, area, coverage and setback; spot elevations; and the rules for the ground
/// against the building - its fall, timber near it, slopes near it, lawns too steep to mow.
/// </summary>
public class SiteTests
{
    /// <summary>Flat ground at a height, 40 m square round the origin, a spot level every 2 m.</summary>
    private static Toposurface Flat(BimDocument document, double height = 0, Func<double, double, double>? shape = null)
    {
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        var points = new List<Point3D>();
        for (var x = -20000.0; x <= 20000; x += 2000)
        for (var y = -20000.0; y <= 20000; y += 2000)
            points.Add(new Point3D(x, y, shape?.Invoke(x, y) ?? height));
        ground.SetPoints(points);
        document.Add(ground);
        return ground;
    }

    /// <summary>The stair tests' house - a room 6 m by 5 m - its walls outside walls, and its ground floor in.</summary>
    private static BimDocument House()
    {
        var (document, _) = StairTests.House();
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        foreach (var wall in document.Walls) wall.TypeId = outside.Id;
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        return document;
    }

    private static SitePad Pad(BimDocument document, double offset, double batter = 2, double size = 10000, double x = 0, double y = 0)
    {
        var pad = new SitePad { LevelId = document.Levels[0].Id, HeightOffset = offset, Batter = batter };
        pad.SetBoundary(new[] { new Point2D(x - size / 2, y - size / 2), new Point2D(x + size / 2, y - size / 2), new Point2D(x + size / 2, y + size / 2), new Point2D(x - size / 2, y + size / 2) });
        document.Add(pad);
        return pad;
    }

    [Fact]
    public void APadIsGradedIntoTheGroundAndBatteredBackToIt()
    {
        var document = BimDocument.CreateDefault();
        var ground = Flat(document);
        Pad(document, -600);

        // Level at its own height inside; cut down 600, battered back at 1 in 2: halfway up 600 out, meeting the ground 1200 out.
        Assert.Equal(-600, Terrains.HeightAt(document, ground, new Point2D(0, 0))!.Value, 3);
        Assert.Equal(-600, Terrains.HeightAt(document, ground, new Point2D(4900, 0))!.Value, 3);
        Assert.Equal(-300, Terrains.HeightAt(document, ground, new Point2D(5600, 0))!.Value, 30.0);
        Assert.Equal(0, Terrains.HeightAt(document, ground, new Point2D(7000, 0))!.Value, 3);

        // What is dug out for it: its 10 m square 600 deep, and the batter round it.
        var (cut, fill) = Terrains.CutAndFill(document, ground);
        Assert.InRange(cut / 1e9, 10 * 10 * 0.6, 10 * 10 * 0.6 + 4 * 10 * 1.2 * 0.6 / 2 + 2);
        Assert.InRange(fill / 1e9, 0, 0.5);

        // Made up instead: filled, battered down to the ground.
        document.Elements.OfType<SitePad>().Single().HeightOffset = 600;
        Assert.Equal(300, Terrains.HeightAt(document, ground, new Point2D(5600, 0))!.Value, 30.0);
        Assert.True(Terrains.CutAndFill(document, ground).Fill / 1e9 > 60);

        // In 3D, in its own surface.
        Assert.Contains(Terrains.Meshes(document, ground), mesh => mesh.ElementId == document.Elements.OfType<SitePad>().Single().Id);
    }

    [Fact]
    public void APadOnGroundOfFewSpotLevelsFillsOnlyWhatIsUnderItAndRoundIt()
    {
        // Ground of five spot levels: four corners at -150 and one far down the garden at -1200,
        // so the ground under the patio falls away - and its triangles span the whole site.
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var ground = new Toposurface { LevelId = level };
        ground.SetPoints(new[] { new Point3D(-15000, -15000, -150), new Point3D(27000, -15000, -150), new Point3D(27000, 24000, -150), new Point3D(-15000, 24000, -150), new Point3D(6000, -14000, -1200) });
        document.Add(ground);
        var patio = Pad(document, -150, batter: 2, size: 4000, x: 14500, y: 3500);

        // What the patio stands over, filled up to it; and the most its batter can add round it.
        var under = 0.0;
        var deepest = 0.0;
        for (var x = 12550.0; x < 16500; x += 100)
        for (var y = 1550.0; y < 5500; y += 100)
        {
            var depth = -150 - Terrains.HeightAt(ground, new Point2D(x, y))!.Value;
            under += Math.Max(0, depth) * 100 * 100;
            deepest = Math.Max(deepest, depth);
        }

        var batter = 4 * 4000 * deepest * deepest * patio.Batter / 2 * 1.5;
        var (_, fill) = Terrains.CutAndFill(document, ground);
        Assert.InRange(fill, under * 0.9, under + batter);

        // And the ground well away from it is where it was, to within what reading it off a grid loses.
        foreach (var far in new[] { new Point2D(-10000, 15000), new Point2D(0, -10000), new Point2D(22000, 20000) })
            Assert.Equal(Terrains.HeightAt(ground, far)!.Value, Terrains.HeightAt(document, ground, far)!.Value, 25.0);
    }

    [Fact]
    public void AHouseDrawnWithoutItsFloorStillCoversThePlotItsWallsStandRound()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 9000), new Point2D(0, 9000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 9000), TypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior")).Id, LevelId = level, UnconnectedHeight = 3000 });

        // Its outside: 12 m by 9 m to the walls' middles, out by half a wall all round - the corners' squares aside.
        var outside = (12000 + type.Structure.TotalWidth) * (9000 + type.Structure.TotalWidth);
        Assert.InRange(PropertyLines.Footprint(document), outside - 4 * 330 * 330, outside + 1);

        var plot = new PropertyLine { LevelId = level };
        plot.SetBoundary(new[] { new Point2D(-9000, -10500), new Point2D(21000, -10500), new Point2D(21000, 19500), new Point2D(-9000, 19500) });
        Assert.InRange(PropertyLines.Coverage(document, plot), 0.12, 0.13);
    }

    [Fact]
    public void ASheerPadLeavesTheGroundRoundItAndOneFallingFallsTheWayItIsTold()
    {
        var document = BimDocument.CreateDefault();
        var ground = Flat(document);
        var pad = Pad(document, -600, batter: 0);
        Assert.Equal(0, Terrains.HeightAt(document, ground, new Point2D(5600, 0))!.Value, 3);

        // Falling 1 in 50 to the south: higher at its north edge than its south.
        (pad.Fall, pad.FallDirection) = (50, 270);
        Assert.Equal(-600 + 4000 / 50.0, Terrains.HeightAt(document, ground, new Point2D(0, 4000))!.Value, 3);
        Assert.Equal(-600 - 4000 / 50.0, Terrains.HeightAt(document, ground, new Point2D(0, -4000))!.Value, 3);
    }

    [Fact]
    public void AgainstTheHouseAPadIsKeptBelowTheDampProofCourseAndFallsAwayFromIt()
    {
        var document = House();
        Flat(document, -450);

        // A patio along the south wall, level with the floor: over the damp-proof course, and level against the wall.
        var patio = new SitePad { LevelId = document.Levels[0].Id, HeightOffset = 0, Surface = GroundSurface.Paving };
        patio.SetBoundary(new[] { new Point2D(0, -3000), new Point2D(6000, -3000), new Point2D(6000, 0), new Point2D(0, 0) });
        document.Add(patio);
        var problems = SitePads.Problems(document, patio);
        Assert.Contains(problems, problem => problem.Contains("damp-proof course"));
        Assert.Contains(problems, problem => problem.Contains("level"));

        // 150 down, falling away to the south: right. Falling toward the house: wrong.
        (patio.HeightOffset, patio.Fall, patio.FallDirection) = (-200, 60, 270);
        Assert.Empty(SitePads.Problems(document, patio));
        patio.FallDirection = 90;
        Assert.Contains(SitePads.Problems(document, patio), problem => problem.Contains("falls toward the building"));
    }

    [Fact]
    public void ARegionRaisedOrSunkIsSoHighAndWhatStandsOutsideStandsOnTheGround()
    {
        var document = BimDocument.CreateDefault();
        var ground = Flat(document, -450);
        var bed = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Planting, Offset = 300 };
        bed.SetBoundary(new[] { new Point2D(-3000, -3000), new Point2D(3000, -3000), new Point2D(3000, 3000), new Point2D(-3000, 3000) });
        document.Add(bed);

        Assert.Equal(-150, Terrains.GroundAt(document, new Point2D(0, 0))!.Value, 3);
        Assert.Equal(-450, Terrains.GroundAt(document, new Point2D(8000, 0))!.Value, 3);
        var raised = Terrains.Meshes(document, ground).Single(mesh => mesh.ElementId == bed.Id);
        Assert.Equal(-150, raised.Positions.Max(point => point.Z), 3);
        Assert.Equal(-450, raised.Positions.Min(point => point.Z), 3);

        // A tree in the bed, and a car on the lawn: on the ground there, not at the level.
        var family = new ComponentType("Tree", BuiltInCategory.Planting, ComponentForm.Box, 1000, 1000, 5000);
        document.AddType(family);
        var tree = new Component { LevelId = document.Levels[0].Id, TypeId = family.Id, Location = new Point2D(0, 0) };
        var car = new Component { LevelId = document.Levels[0].Id, TypeId = family.Id, Location = new Point2D(10000, 10000) };
        document.Add(tree);
        document.Add(car);
        Assert.Equal(-150, ComponentHosting.BaseElevation(document, tree, family), 3);
        Assert.Equal(-450, ComponentHosting.BaseElevation(document, car, family), 3);
    }

    [Fact]
    public void ABasementIsDugOutToItsFloorsUnderside()
    {
        var document = BimDocument.CreateDefault();
        var ground = Flat(document, -150);
        var groundFloor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        groundFloor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(groundFloor);
        var (shallow, _) = Terrains.CutAndFill(document, ground);

        var basement = new Core.Datums.Level { Name = "Basement", Elevation = -3000 };
        document.InsertLevel(basement);
        var below = new Floor { LevelId = basement.Id, TypeId = document.TypesOf<FloorType>().First().Id };
        below.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(below);
        var (deep, _) = Terrains.CutAndFill(document, ground);

        // Its 6 by 5 m dug down from the ground floor's underside to the basement floor's.
        var extra = (groundFloor.GetBottomElevation(document) - below.GetBottomElevation(document)) * 6000 * 5000;
        Assert.Equal(shallow + extra, deep, extra * 0.02);
    }

    [Fact]
    public void ADenseSurveyIsSimplifiedKeepingItsShape()
    {
        var points = new List<Point3D>();
        for (var x = 0.0; x <= 20000; x += 500)
        for (var y = 0.0; y <= 20000; y += 500)
            points.Add(new Point3D(x, y, x / 10 + (x > 10000 ? (x - 10000) / 5 : 0)));
        var kept = Terrains.Simplified(points, Terrains.SimplifyWithin);
        Assert.True(kept.Count < points.Count / 3, $"{kept.Count} of {points.Count} kept");

        var document = BimDocument.CreateDefault();
        var ground = new Toposurface { LevelId = document.Levels[0].Id };
        ground.SetPoints(kept);
        document.Add(ground);
        foreach (var at in new[] { new Point2D(5250, 7750), new Point2D(14750, 3250), new Point2D(10250, 18250) })
        {
            var asWas = at.X / 10 + (at.X > 10000 ? (at.X - 10000) / 5 : 0);
            Assert.Equal(asWas, Terrains.HeightAt(document, ground, at)!.Value, 0.5 + Terrains.SimplifyWithin);
        }
    }

    [Fact]
    public void APropertyLineHasItsBearingsAreaAndCoverageAndKeepsTheBuildingBehindItsSetback()
    {
        Assert.Equal("N 45°00' E", PropertyLines.Bearing(new Point2D(0, 0), new Point2D(1000, 1000)));
        Assert.Equal("S 30°00' W", PropertyLines.Bearing(new Point2D(0, 0), new Point2D(-500, -500 * Math.Sqrt(3))));
        Assert.Equal("E", PropertyLines.Bearing(new Point2D(0, 0), new Point2D(1000, 0)));

        var (document, _) = StairTests.House();
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        var line = new PropertyLine { LevelId = document.Levels[0].Id, Setback = 2000 };
        line.SetBoundary(new[] { new Point2D(-3000, -3000), new Point2D(12000, -3000), new Point2D(12000, 10000), new Point2D(-3000, 10000) });
        document.Add(line);

        Assert.Equal(15000.0 * 13000, Polygon2D.Area(line.Boundary), 0);
        // Covered to the outside of its walls - its gross external area - as planning measures it.
        var wall = document.GetWallType(document.Walls.First())!.Structure.TotalWidth;
        var outside = (6000 + wall) * (5000 + wall);
        Assert.InRange(PropertyLines.Coverage(document, line), (outside - wall * wall) / (15000 * 13000), outside / (15000.0 * 13000));
        Assert.Equal("Within it, behind its setback", PropertyLines.Verdict(document, line));

        line.Setback = 4000;
        Assert.Contains(PropertyLines.Problems(document, line), problem => problem.Contains("its setback is 4.00 m"));
        line.SetBoundary(new[] { new Point2D(1000, -3000), new Point2D(12000, -3000), new Point2D(12000, 10000), new Point2D(1000, 10000) });
        Assert.Contains(PropertyLines.Problems(document, line), problem => problem.Contains("over the property line"));
    }

    [Fact]
    public void ASpotElevationReadsWhatIsUnderIt()
    {
        var (document, _) = StairTests.House();
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        Flat(document, -450);

        var inside = new SpotElevation { LevelId = document.Levels[0].Id, Location = new Point2D(3000, 2500) };
        var outside = new SpotElevation { LevelId = document.Levels[0].Id, Location = new Point2D(-5000, -5000) };
        document.Add(inside);
        document.Add(outside);
        Assert.Equal("FFL +0.000", SpotElevations.Label(document, inside));
        Assert.Equal("GL -0.450", SpotElevations.Label(document, outside));
    }

    [Fact]
    public void UnderTheUsCodesHardSurfacesFallTwoPercentTimberIsKeptOffTheGroundAndTheBuildingBackFromSlopes()
    {
        var document = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;

        // Falling 1 in 40 all round: too little for soil, enough for paving.
        double Away(double x, double y)
        {
            var dx = x < 0 ? -x : x > 6000 ? x - 6000 : 0;
            var dy = y < 0 ? -y : y > 5000 ? y - 5000 : 0;
            return -300 - Math.Max(dx, dy) / 40;
        }

        var ground = Flat(document, shape: Away);
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("6 in (152)"));
        var paving = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Paving };
        paving.SetBoundary(new[] { new Point2D(-5000, -5000), new Point2D(11000, -5000), new Point2D(11000, 10000), new Point2D(-5000, 10000) });
        document.Add(paving);
        Assert.DoesNotContain(Terrains.Problems(document, ground), problem => problem.Contains("6 in (152), or 5%"));

        // Timber framing in a wall 100 off the ground: under the IRC's 8 in.
        var south = document.Walls.First();
        document.Add(new WallFraming { HostId = south.Id, LevelId = south.LevelId });
        ground.SetPoints(ground.Points.Select(point => point with { Z = point.Z + 200 }).ToList());
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("R317.1"));
    }

    [Fact]
    public void AHouseNearTheTopOfASteepSlopeIsSetBackFromIt()
    {
        var document = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;

        // Level round the house, then just east of it falling 3 m at 1 in 1.
        var ground = Flat(document, shape: (x, _) => x <= 6500 ? -200 : Math.Max(-3200, -200 - (x - 6500)));
        Assert.Contains(Terrains.Problems(document, ground), problem => problem.Contains("R403.1.7"));
    }

    [Fact]
    public void ALawnTooSteepToMowIsSaidToBe()
    {
        var document = BimDocument.CreateDefault();
        Flat(document, shape: (x, _) => x / 2);
        var lawn = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Lawn };
        lawn.SetBoundary(new[] { new Point2D(-5000, -5000), new Point2D(5000, -5000), new Point2D(5000, 5000), new Point2D(-5000, 5000) });
        document.Add(lawn);
        Assert.Contains(GroundRegions.Problems(document, lawn), problem => problem.Contains("not mown safely"));
    }

    [Fact]
    public void ItIsSavedScheduledAndExported()
    {
        var document = BimDocument.CreateDefault();
        Flat(document);
        var pad = Pad(document, -300);
        (pad.Fall, pad.FallDirection, pad.Surface, pad.Mark) = (60, 180, GroundSurface.Gravel, "P1");
        var line = new PropertyLine { LevelId = document.Levels[0].Id, Setback = 3000 };
        line.SetBoundary(new[] { new Point2D(-15000, -15000), new Point2D(15000, -15000), new Point2D(15000, 15000) });
        document.Add(line);
        document.Add(new SpotElevation { LevelId = document.Levels[0].Id, Location = new Point2D(1000, 2000) });
        var bed = new GroundRegion { LevelId = document.Levels[0].Id, Surface = GroundSurface.Planting, Offset = 450 };
        bed.SetBoundary(new[] { new Point2D(8000, 8000), new Point2D(10000, 8000), new Point2D(10000, 10000) });
        document.Add(bed);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var padAgain = Assert.Single(loaded.Elements.OfType<SitePad>());
        Assert.Equal((pad.HeightOffset, pad.Fall, pad.FallDirection, pad.Batter, pad.Surface, pad.Boundary.Count), (padAgain.HeightOffset, padAgain.Fall, padAgain.FallDirection, padAgain.Batter, padAgain.Surface, padAgain.Boundary.Count));
        Assert.Equal(3000, Assert.Single(loaded.Elements.OfType<PropertyLine>()).Setback);
        Assert.Equal(new Point2D(1000, 2000), Assert.Single(loaded.Elements.OfType<SpotElevation>()).Location);
        Assert.Equal(450, Assert.Single(loaded.Elements.OfType<GroundRegion>()).Offset);

        var pads = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Pad Schedule"));
        Assert.Equal("P1", Assert.Single(pads.Rows).Text("Mark"));
        var landscape = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Landscape Schedule"));
        Assert.Equal("Planting", Assert.Single(landscape.Rows).Text("Surface"));

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IfcGeographicElement>(), element => element.ObjectType == "PAD");
    }
}
