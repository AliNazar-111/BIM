using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Retaining walls finished and basements: a slope and water behind, the ground in front and a key, clay in the short term, and walls held at their top by a floor, against figures worked by hand.</summary>
public class BasementsAndRetainingTests
{
    // As RetainingDesignTests' wall: 2 m of stem 200 thick at 5 kN/m², a base 300 thick standing 1,000 behind and 500 before, 1.7 wide; φ' 30°, 18 kN/m³, 10 kN/m² behind it, δ 30°.
    private static readonly RetainedSection Wall = new(2000, 200, 1000, 500, 300, 5, 10, 30, 18, 30);

    [Fact]
    public void RankineBehindASlopeIsHisFormula()
    {
        // φ 30°, β 15°: cos 15 = 0.96593, √(0.93301 - 0.75) = 0.42780; 0.96593 × (0.96593 - 0.42780) / (0.96593 + 0.42780) = 0.3729.
        Assert.Equal(0.3729, RetainingDesign.Active(30, 15), 4);
        Assert.Equal(1.0 / 3, RetainingDesign.Active(30, 0), 9);

        // As steep as its friction, cos β: the limit.
        Assert.Equal(Math.Cos(Math.PI / 6), RetainingDesign.Active(30, 30), 9);
        Assert.Equal(3, RetainingDesign.Passive(30), 9);

        // At rest, 1 - sin 30 = 0.5, and by 1 + sin 15 behind a slope: 0.6294.
        Assert.Equal(0.5, RetainingDesign.AtRest(30), 9);
        Assert.Equal(0.6294, RetainingDesign.AtRest(30, 15), 4);
    }

    [Fact]
    public void ASlopeBehindPushesHarderAndWeighsOnTheHeel()
    {
        // β 15° rises 1.0 tan 15 = 0.268 m over the heel: the plane is 2.568 m deep. The wedge, 18 × 1.0 × 0.268 / 2 = 2.41 kN/m at 1.367 m from the toe: 60.65 kN/m.
        var sloped = Wall with { Slope = 15 };
        Assert.Equal(2.3 + Math.Tan(Math.PI / 12), sloped.Pushed, 9);
        var (weight, moment) = RetainingDesign.Stands(sloped);
        Assert.Equal(58.24 + 2.4115, weight, 3);
        Assert.Equal(59.604 + 2.4115 * (0.7 + 2.0 / 3), moment, 3);

        // DA1-2: φd 24.79°, Ka(24.79°, 15°) 0.4741; earth 0.4741 × 18 × 2.568² / 2 = 28.14, surcharge 0.4741 × 10 × 2.568 = 12.18 × 1.3; across, cos 15 × 43.97 = 42.47.
        // What bears down, 60.65 + sin 15 × 28.14 = 67.93, by tan δd 0.4619 = 31.38. DA1-1 is 42.74 against 38.33: the second governs.
        var sliding = RetainingDesign.StabilityEurocode(sloped, 150).Single(check => check.Name == "Sliding");
        Assert.Equal("DA1-2", sliding.Under);
        Assert.InRange(sliding.Demand, 42.3, 42.7);
        Assert.InRange(sliding.Capacity, 31.2, 31.6);

        // The slope stands of itself: tan 15 = 0.268 against tan 24.79 = 0.462.
        var slope = RetainingDesign.StabilityEurocode(sloped, 150).Single(check => check.Name == "Slope behind it");
        Assert.Equal(Math.Tan(Math.PI / 12), slope.Demand, 6);
        Assert.Equal(Math.Tan(30 * Math.PI / 180) / 1.25, slope.Capacity, 6);
        Assert.DoesNotContain(RetainingDesign.StabilityEurocode(Wall, 150), check => check.Name == "Slope behind it");

        // Too steep for its friction once factored: 28° against 24.8°.
        Assert.True(RetainingDesign.StabilityEurocode(Wall with { Slope = 28 }, 150).Single(check => check.Name == "Slope behind it").Ratio > 1);
    }

    [Fact]
    public void WaterBehindItPushesAndLiftsIt()
    {
        // Water 1 m up from under the base: the earth k (18 × 2.3² - 9.81 × 1²) / 2 = 42.705 k, the water 9.81 / 2 = 4.905, under the base 9.81 × 1.0 × 1.7 / 2 = 8.34 kN/m.
        var wet = Wall with { Water = 1000 };
        var push = RetainingDesign.Against(2.3, 1.0, 1.0 / 3, 18, 10);
        Assert.Equal(42.705 / 3, push.Earth, 6);
        Assert.Equal(4.905, push.Water, 6);
        Assert.Equal(1.0 / 3, push.WaterArm, 9);
        Assert.Equal(8.3385, RetainingDesign.Uplift(wet).Force, 4);
        Assert.Equal(8.3385 * 2 * 1.7 / 3, RetainingDesign.Uplift(wet).Moment, 4);

        // DA1-2: Ka 0.4091; 17.47 + 1.3 × 9.41 + 4.905 = 34.61 against (58.24 - 8.34) × 0.4619 = 23.05.
        var sliding = RetainingDesign.StabilityEurocode(wet, 150).Single(check => check.Name == "Sliding");
        Assert.Equal("DA1-2", sliding.Under);
        Assert.InRange(sliding.Demand, 34.4, 34.8);
        Assert.InRange(sliding.Capacity, 22.9, 23.2);
        Assert.Contains("8.3 of water under it", sliding.Note);

        // EQU: 1.1 × 14.27 + 1.5 × 10.82 + 1.1 × (1.635 + 9.450) = 44.12 kNm/m.
        var over = RetainingDesign.StabilityEurocode(wet, 150).Single(check => check.Name == "Overturning");
        Assert.InRange(over.Demand, 43.9, 44.4);

        // The stem sees the water above the base's top, 0.7 m: 9.81 × 0.49 / 2 = 2.40 kN/m more in DA1-2's shear, its earth less 9.81 × 0.49 / 2 × Ka.
        var (_, dry) = RetainingDesign.StemForcesEurocode(Wall);
        var (_, wetShear) = RetainingDesign.StemForcesEurocode(wet);
        Assert.True(wetShear > dry);
    }

    [Fact]
    public void TheGroundInFrontCountsBelowWhatMayBeDugAway()
    {
        // 800 down in front: 9.3.2.2 takes 0.1 × (2.3 - 0.8) = 0.15 m off it, leaving 0.65 m. DA1-2: Kp(24.79°) 2.444, ½ × 2.444 × 18 × 0.65² = 9.29 kN/m on 26.90.
        var deep = Wall with { Embedded = 800 };
        Assert.Equal(0.15, RetainingDesign.Unplanned(deep), 9);
        Assert.Equal(0.5, RetainingDesign.Unplanned(Wall with { Height = 8000 }), 9);
        var sliding = RetainingDesign.StabilityEurocode(deep, 150).Single(check => check.Name == "Sliding");
        Assert.Equal("DA1-2", sliding.Under);
        Assert.InRange(sliding.Capacity, 36.0, 36.4);
        Assert.Contains("from the ground in front", sliding.Note);

        // A key 400 deep: the ground in front down to its foot, 1.05 m, 26.90 + ½ × 2.444 × 18 × 1.05² = 51.15, against the 31.71 pushing to the base's underside, as Das has it.
        var keyed = deep with { Key = 400 };
        var held = RetainingDesign.StabilityEurocode(keyed, 150).Single(check => check.Name == "Sliding");
        Assert.InRange(held.Demand, 31.5, 31.9);
        Assert.InRange(held.Capacity, 50.9, 51.4);
        Assert.Contains("to its key's foot", held.Note);
    }

    [Fact]
    public void OnClayItSlidesInTheShortTermToo()
    {
        // Firm clay, cu 40: the load 187 mm off the middle, B' = 1.7 - 0.374 = 1.326 m; 53.1 kN/m of adhesion, but no more than 0.4 × 58.24 = 23.30 with water able to reach under it. DA1-1's 32.92 governs.
        var check = RetainingDesign.StabilityEurocode(Wall, 150, undrained: 40).Single(each => each.Name == "Sliding, short term");
        Assert.Equal("DA1-1", check.Under);
        Assert.Equal(32.92, check.Demand, 2);
        Assert.Equal(0.4 * 58.24, check.Capacity, 3);
        Assert.Contains("0.4 of what bears down", check.Note);
        Assert.DoesNotContain(RetainingDesign.StabilityEurocode(Wall, 150), each => each.Name == "Sliding, short term");

        // 800 down in front with a key 400 deep: 1.05 m of clay below the 0.15 dug away, in total stress ½ × 18 × 1.05² + 2 × 40 / 1.4 × 1.05 = 69.9 kN/m more in DA1-2, against 31.71 pushing.
        var keyed = RetainingDesign.StabilityEurocode(Wall with { Embedded = 800, Key = 400 }, 150, undrained: 40).Single(each => each.Name == "Sliding, short term");
        Assert.Equal("DA1-2", keyed.Under);
        Assert.InRange(keyed.Demand, 31.5, 31.9);
        Assert.Equal(0.4 * 58.24 + 0.5 * 18 * 1.05 * 1.05 + 2 * 40 / 1.4 * 1.05, keyed.Capacity, 3);
        Assert.Contains("from the clay in front", keyed.Note);
        Assert.Equal(40, RetainingDesign.UndrainedOf(GroundType.FirmClay));
        Assert.Equal(0, RetainingDesign.UndrainedOf(GroundType.Sand));
    }

    [Fact]
    public void AUsWallUnderWaterIsBuoyantSoilAndItsHead()
    {
        // 30 psf/ft of gravel is 4.713 kN/m³; buoyant below the water by (18 - 9.81) / 18, and the water's 9.81 besides: (4.713 × 2.3² - 4.713 × 9.81 / 18 × 1²) / 2 + 4.905.
        var fluid = RetainingDesign.FluidPressure(GroundType.Gravel)!.Value;
        var wet = Wall with { Water = 1000 };
        var sliding = RetainingDesign.StabilityUs(wet, fluid, 0.35, 0, 150).Single(check => check.Name == "Sliding, at 1.5");
        var earth = (fluid * 2.3 * 2.3 - fluid * 9.81 / 18) / 2;
        Assert.Equal(1.5 * (earth + fluid * 10 / 18 * 2.3 + 4.905), sliding.Demand, 3);
        Assert.Equal(0.35 * (58.24 - 8.3385), sliding.Capacity, 3);

        // At rest, Table 1610.1: 60 psf/ft for sand, 100 for lean clay.
        Assert.Equal(60 * 0.0478803 / 0.3048, RetainingDesign.AtRestPressure(GroundType.Sand)!.Value, 6);
        Assert.Equal(100 * 0.0478803 / 0.3048, RetainingDesign.AtRestPressure(GroundType.StiffClay)!.Value, 6);
        Assert.Null(RetainingDesign.AtRestPressure(GroundType.SoftClay));
    }

    [Fact]
    public void AStemHeldAtItsTopIsAProppedCantilever()
    {
        // A triangle 30 kPa at its foot over 3 m: the prop p L / 10 = 9, the foot p L² / 15 = 18, its shear 45 - 9 = 36, and p L² / (15 √5) = 8.05 the other way, L / √5 from the top.
        var triangle = RetainingDesign.Propped(3, x => 30 * (1 - x / 3));
        Assert.Equal(9, triangle.Prop, 3);
        Assert.Equal(18, triangle.FootMoment, 3);
        Assert.Equal(36, triangle.FootShear, 3);
        Assert.Equal(270 / (15 * Math.Sqrt(5)), triangle.SpanMoment, 2);

        // Even, 10 kPa over 3 m: 3 w L / 8 = 11.25 at the prop, w L² / 8 = 11.25 at the foot, 9 w L² / 128 = 6.33 between.
        var even = RetainingDesign.Propped(3, _ => 10);
        Assert.Equal(11.25, even.Prop, 3);
        Assert.Equal(11.25, even.FootMoment, 3);
        Assert.Equal(6.328, even.SpanMoment, 2);

        // Pinned at its foot, as masonry on a damp course: w L² / 8 between, nothing at its foot.
        var pinned = RetainingDesign.Propped(3, _ => 10, fixedFoot: false);
        Assert.Equal(15, pinned.Prop, 3);
        Assert.Equal(0, pinned.FootMoment, 9);
        Assert.Equal(11.25, pinned.SpanMoment, 2);
    }

    [Fact]
    public void AMasonryBasementWallArchesByBsEn19963()
    {
        // h 2.4, he 2.0, 300 thick, 18 kN/m³: bc 4 m is 1.67 h, βe 60 - 20 × 1.67 = 26.7: 18 × 2.4 × 2.0² / (26.7 × 0.3) = 21.6 kN/m wanted. t fd / 3 = 300 × 2.0 / 3 = 200.
        var checks = RetainingDesign.BasementMasonry(2400, 2000, 4000, 300, 18, 40, 200, 2.0);
        // Said as the earth 40 kN/m lets it hold: √(40 × 26.7 × 0.3 / (18 × 2.4)) = 2.72 m, against the 2.0 m there.
        var least = checks.Single(check => check.Name == "Earth it holds back");
        Assert.Equal(2000, least.Demand);
        Assert.Equal(Math.Sqrt(40 * (60 - 20 * 4000.0 / 2400) * 0.3 / (18 * 2.4)) * 1000, least.Capacity, 6);
        Assert.Contains("βe 26.7", least.Note);
        Assert.Contains("wants 21.6 kN/m", least.Note);
        var most = checks.Single(check => check.Name == "Load on it, the most");
        Assert.Equal(200, most.Capacity, 6);

        // βe 20 with nothing within 2h, 40 within h.
        Assert.Contains("wants 28.8 kN/m", RetainingDesign.BasementMasonry(2400, 2000, 6000, 300, 18, 40, 200, 2.0)[0].Note);
        Assert.Contains("wants 14.4 kN/m", RetainingDesign.BasementMasonry(2400, 2000, 2000, 300, 18, 40, 200, 2.0)[0].Note);
    }

    private static (BimDocument Document, Wall Wall) Basement(string wall = "Retaining - Concrete 250mm", double depth = 2700, bool timber = false, bool us = false, double thickness = 0)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Ground = GroundType.Sand;
        if (us) document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var ground = document.Levels.OrderBy(level => level.Elevation).First();
        var below = new Level { Name = "Basement", Elevation = ground.Elevation - depth };
        document.AddLevel(below);
        WallType type;
        if (thickness > 0)
        {
            var block = document.Materials.First(material => material.Name == "Concrete Block");
            type = new WallType($"Basement - Block {thickness}", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, block.Id, thickness))) { Function = WallFunction.Retaining };
            document.AddType(type);
        }
        else type = document.TypesOf<WallType>().First(each => each.Name == wall);

        var corners = new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = below.Id, TopLevelId = ground.Id, StructuralUsage = StructuralUsage.Bearing }).ToList();
        foreach (var each in walls) document.Add(each);
        var strip = new FootingType("Base", FootingKind.Strip) { Width = 900, Thickness = 300 };
        document.AddType(strip);
        foreach (var each in walls) document.Add(new WallFooting { WallId = each.Id, LevelId = below.Id, TypeId = strip.Id, Depth = 300 });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(each => each.Name.StartsWith(timber ? "Floor - Timber" : "Floor - Screed", StringComparison.Ordinal)).Id, LevelId = ground.Id };
        floor.SetBoundary(corners);
        document.Add(floor);
        return (document, walls[0]);
    }

    [Fact]
    public void ABasementWallIsHeldAtItsTopAndTheEarthIsAtRest()
    {
        // 2.7 m of sand at rest: K0 = 1 - sin 32° = 0.470. DA1-1: 1.35 × 0.470 × 18 × 2.7 = 30.84 kPa at its foot and 1.5 × 0.470 × 5 = 3.53 all the way.
        // Its foot 30.84 × 2.7² / 15 + 3.53 × 2.7² / 8 = 18.20 kNm/m; its shear 0.4 × 30.84 × 2.7 + 5 / 8 × 3.53 × 2.7 = 39.26; the floor 30.84 × 2.7 / 10 + 3 / 8 × 3.53 × 2.7 = 11.9 kN/m.
        var (document, wall) = Basement();
        Assert.NotNull(RetainingDesign.HeldBy(document, wall));
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.DoesNotContain(check.Checks, each => each.Name.StartsWith("Sliding", StringComparison.Ordinal) || each.Name == "Overturning");
        Assert.Equal(18.20, check.Checks.Single(each => each.Name == "Stem in bending at its foot").Demand, 1);
        Assert.Equal(39.26, check.Checks.Single(each => each.Name == "Stem in shear").Demand, 1);
        Assert.InRange(check.Checks.Single(each => each.Name == "Stem in bending up its height").Demand, 7.5, 8.6);
        Assert.Contains(check.Checks, each => each.Name == "Base in bending");
        Assert.Contains("11.9 kN/m", check.Limits);
        Assert.Contains("K0 0.47", check.Limits);
        Assert.Contains("slides or tips is not worked out", check.Limits);
    }

    [Fact]
    public void ABasementWithWaterUnderItIsCheckedForFloating()
    {
        // Water 1.5 m up from the footings' underside, 3.0 m down: at -1.5 m, 1.45 m over the underside of a 250 floor at -2.7 m. 9.81 × 1.45 × 48 m² = 682.8 kN up, against 0.9 of what the building weighs.
        var (document, wall) = Basement();
        var lower = document.Levels.First(level => level.Name == "Basement");
        var slab = new Floor { TypeId = document.TypesOf<FloorType>().First(each => each.Name.StartsWith("Floor - Screed", StringComparison.Ordinal)).Id, LevelId = lower.Id };
        slab.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) });
        document.Add(slab);
        document.ProjectInformation.RetainedWater = 1500;
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var basement = RetainingDesign.Flotation(document, analysis).Single();
        Assert.Same(slab, basement.Foundation);
        var floating = basement.Checks.Single();
        Assert.Equal("UPL", floating.Under);
        Assert.Equal(9.81 * 1.45 * 48, floating.Demand, 1);
        var weight = -analysis.Loading.Cases.Where(each => each.Permanent).Sum(each => analysis.TotalLoad(each).Z);
        Assert.Equal(0.9 * weight, floating.Capacity, 3);
        Assert.Contains(FoundationDesign.Of(document, analysis), each => each.Name == "Basement 1");

        // Drained, nothing floats.
        document.ProjectInformation.RetainedWater = 0;
        Assert.Empty(RetainingDesign.Flotation(document, analysis));
        Assert.NotNull(RetainingDesign.HeldBy(document, wall));
    }

    [Fact]
    public void AUsBasementWallIsAtRestUnlessItsExceptionLetsIt()
    {
        // Under a concrete floor, Table 1610.1's 60 psf/ft at rest: 1.6 × 9.425 × 2.7 = 40.72 kPa at its foot, 1.6 × 9.425 / 18 × 5 = 4.19 all the way: 40.72 × 0.486 + 4.19 × 0.911 = 23.61 kNm/m.
        var (document, wall) = Basement(us: true);
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Contains("at rest at 60 psf/ft", check.Limits);
        Assert.Equal(23.61, check.Checks.Single(each => each.Name == "Stem in bending at its foot").Demand, 1);

        // Under a timber floor, no more than 8 ft down: active, 45 psf/ft.
        var (light, lightWall) = Basement(us: true, timber: true, depth: 2400);
        Assert.Contains("active at 45 psf/ft", RetainingDesign.Check(light, lightWall, "RW1").Limits);
    }

    [Fact]
    public void AMasonryBasementWallIsCheckedByTheSimplifiedMethod()
    {
        // 215 block, 2.5 m high with 2.5 m of sand against it, 8 m between its corners, more than 2h: βe 20, 18 × 2.5 × 2.5² / (20 × 0.215) = 65.4 kN/m wanted bearing down on it.
        var (document, wall) = Basement(depth: 2500, thickness: 215);
        var check = RetainingDesign.Check(document, wall, "RW1");
        var least = check.Checks.Single(each => each.Name == "Earth it holds back");
        Assert.Equal(2500, least.Demand, 3);
        Assert.Contains($"wants {18 * 2.5 * 6.25 / (20 * 0.215):0.0} kN/m", least.Note);
        Assert.Contains(check.Checks, each => each.Name == "Load on it, the most");
        Assert.Contains("BS EN 1996-3 4.5", check.Limits);

        // Taller than 2.6 m it is outside the method: bent across its height, pinned at both ends.
        var (tall, tallWall) = Basement(depth: 3000, thickness: 215);
        var bent = RetainingDesign.Check(tall, tallWall, "RW1");
        Assert.Contains(bent.Checks, each => each.Name == "Stem in bending");
        Assert.Contains("over 2.6 m", bent.Limits);
    }

    private static (BimDocument Document, Wall Wall) Garden(GroundType ground = GroundType.Sand, double key = 0)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Ground = ground;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Generic", StringComparison.Ordinal)).Duplicate("Retaining");
        type.Function = WallFunction.Retaining;
        document.AddType(type);
        var placed = new Wall { Start = new Point2D(0, 0), End = new Point2D(8000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id };
        document.Add(placed);
        var footing = new FootingType("Base", FootingKind.Strip) { Width = 2000, Thickness = 300, Key = key };
        document.AddType(footing);
        document.Add(new WallFooting { WallId = placed.Id, LevelId = document.Levels[0].Id, TypeId = footing.Id, Depth = 300 });
        return (document, placed);
    }

    [Fact]
    public void AGardenWallTakesItsSlopeWaterAndKeyFromTheModel()
    {
        var (document, wall) = Garden(key: 400);
        document.ProjectInformation.RetainedSlope = 10;
        document.ProjectInformation.RetainedWater = 600;
        var footing = document.All<WallFooting>().Single();
        var section = RetainingDesign.SectionOf(document, wall, footing, 30, 18, 30)!;
        Assert.Equal(10, section.Slope);
        Assert.Equal(600, section.Water);
        Assert.Equal(400, section.Key);

        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Contains(check.Checks, each => each.Name == "Slope behind it");
        Assert.Contains("rising at 10°", check.Limits);
        Assert.Contains("water 0.60 m up from under its base", check.Limits);
        Assert.Contains("a key 400 deep", check.Limits);
        Assert.Null(RetainingDesign.HeldBy(document, wall));
    }

    [Fact]
    public void ASlabOnTheEarthBehindHoldsNothing()
    {
        // A slab at the wall's top lying on the earth it holds back - its exterior - does not prop it; one reaching it from the open side does.
        var (document, wall) = Garden();
        var type = document.GetWallType(wall)!;
        var top = document.Levels[1];
        var exterior = Math.Sign(wall.PointAt(type.Structure, 4000, 1000).Y);
        Floor Slab(double side)
        {
            var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(each => each.Name.StartsWith("Floor - Screed", StringComparison.Ordinal)).Id, LevelId = top.Id };
            var (near, far) = (side * 150, side * 3000);
            floor.SetBoundary(new[] { new Point2D(-1000, near), new Point2D(9000, near), new Point2D(9000, far), new Point2D(-1000, far) });
            return floor;
        }

        var lying = Slab(exterior);
        document.Add(lying);
        Assert.Null(RetainingDesign.HeldBy(document, wall));
        document.Remove(lying);
        document.Add(Slab(-exterior));
        Assert.NotNull(RetainingDesign.HeldBy(document, wall));
    }

    [Fact]
    public void OnClayAGardenWallIsCheckedShortTerm()
    {
        var (document, wall) = Garden(GroundType.StiffClay);
        var check = RetainingDesign.Check(document, wall, "RW1");
        Assert.Contains(check.Checks, each => each.Name == "Sliding, short term" && each.Note.Contains("cu 75"));
        Assert.Contains("75 kPa undrained", check.Limits);
    }

    [Fact]
    public void AKeyIsDrawnAndCountedInItsConcrete()
    {
        // 8 m of key under a 200 wall, 400 deep: 8000 × 200 × 400 mm³ more concrete.
        var (plain, _) = Garden();
        var (keyed, _) = Garden(key: 400);
        var (a, b) = (plain.All<WallFooting>().Single(), keyed.All<WallFooting>().Single());
        Assert.Equal(8000.0 * 200 * 400, Footings.Volume(keyed, b) - Footings.Volume(plain, a), 0);
        Assert.Equal(Footings.Meshes(plain, a).Count + 1, Footings.Meshes(keyed, b).Count);
        Assert.Equal(400, keyed.FindType<FootingType>(b.TypeId)!.Duplicate("Copy").Key);
    }

    [Fact]
    public void TheGroundBehindSaysHowMuchItRetains()
    {
        // The ground 1.5 m up behind it and level with its foot in front: 1.5 m retained, not its whole height.
        var (document, wall) = Garden();
        var surface = new Toposurface { LevelId = document.Levels[0].Id };
        var points = new List<Point3D>();
        for (var x = -4000.0; x <= 12000; x += 2000)
            foreach (var y in new[] { -6000.0, -4000, -2000, -1000, 1000, 2000, 4000, 6000 })
                points.Add(new Point3D(x, y, y > 0 ? 1500 : 0));
        surface.SetPoints(points);
        document.Add(surface);
        var section = RetainingDesign.SectionOf(document, wall, document.All<WallFooting>().Single(), 30, 18, 30)!;
        Assert.Equal(1500, section.Height, 0);
    }

    [Fact]
    public void TheNewSettingsAreKeptWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.RetainedSlope = 12;
        document.ProjectInformation.RetainedWater = 800;
        var footing = new FootingType("Keyed", FootingKind.Strip) { Key = 450 };
        document.AddType(footing);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(12, reopened.ProjectInformation.RetainedSlope);
        Assert.Equal(800, reopened.ProjectInformation.RetainedWater);
        Assert.Equal(450, reopened.FindType<FootingType>(footing.Id)!.Key);
    }
}
