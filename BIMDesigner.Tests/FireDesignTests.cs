using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>A building in a fire and after an accident: how long it must stand, each material against its own standard's own figures, and the ties Annex A of BS EN 1991-1-7 asks for.</summary>
public class FireDesignTests
{
    private static readonly SteelSection Ub254 = new("I Section", 251.4, 146.1, 8.6, 6.0, 3960, 4410e4, 448e4, 353e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.0659e12, 7.6);

    [Theory]
    [InlineData(BuildingUse.House, 2, 3, false, 30)]
    [InlineData(BuildingUse.House, 3, 6, false, 30)]
    [InlineData(BuildingUse.House, 4, 9, false, 60)]
    [InlineData(BuildingUse.Flats, 2, 3, false, 30)]
    [InlineData(BuildingUse.Flats, 5, 12, false, 60)]
    [InlineData(BuildingUse.Flats, 8, 24, false, 90)]
    [InlineData(BuildingUse.Office, 2, 4, false, 30)]
    [InlineData(BuildingUse.Office, 5, 12, false, 60)]
    [InlineData(BuildingUse.Office, 5, 12, true, 30)]
    [InlineData(BuildingUse.Office, 8, 24, false, 90)]
    [InlineData(BuildingUse.Office, 8, 24, true, 60)]
    [InlineData(BuildingUse.Office, 12, 36, true, 120)]
    public void ThePeriodIsTableB4(BuildingUse use, int storeys, double top, bool sprinklers, int minutes) =>
        Assert.Equal(minutes, FireDesign.Period(use, storeys, top, sprinklers).Minutes);

    [Fact]
    public void AnyOtherUseIsAskedFor() => Assert.Contains("Building Type", FireDesign.Period(BuildingUse.Other, 2, 3, false).Said);

    [Fact]
    public void TheStandardFireIsItsCurve()
    {
        // 20 + 345 log10(8t + 1): 842 at 30 minutes, 945 at 60.
        Assert.Equal(842, SteelFire.Gas(30), 0);
        Assert.Equal(945, SteelFire.Gas(60), 0);
    }

    [Theory]
    [InlineData(0.22, 711)]
    [InlineData(0.40, 620)]
    [InlineData(0.60, 554)]
    [InlineData(0.70, 526)]
    public void TheCriticalTemperatureComesToTable41(double utilisation, double critical) => Assert.Equal(critical, SteelFire.Critical(utilisation), 0);

    [Fact]
    public void SteelLosesStrengthByTable31()
    {
        Assert.Equal(1, SteelFire.Yield(400));
        Assert.Equal(0.78, SteelFire.Yield(500));
        Assert.Equal(0.47, SteelFire.Yield(600));
        Assert.Equal(0.625, SteelFire.Yield(550), 6);
        Assert.Equal(0.31, SteelFire.Stiffness(600));
    }

    [Fact]
    public void BareSteelFollowsTheFire()
    {
        // At Am/V 200 /m there is little between it and the gas by half an hour; heavier steel lags well behind; it never passes the gas.
        Assert.InRange(SteelFire.Temperature(200, 30), 800, 842);
        Assert.True(SteelFire.Temperature(50, 30) < SteelFire.Temperature(200, 30) - 100);
    }

    [Fact]
    public void AnISectionsFactorsAreItsSurfaceOverItsArea()
    {
        // 4 × 146.1 + 2 × 251.4 - 2 × 6 = 1075 round it, 795 boxed; over 3960 mm², 271.5 and 200.8 /m; shadow 0.9 × 200.8 / 271.5 = 0.665.
        var (profile, box, shadow) = SteelFire.Factors(Ub254, false);
        Assert.Equal(271.5, profile, 0);
        Assert.Equal(200.8, box, 0);
        Assert.Equal(0.665, shadow, 3);

        // Under a floor the top of its top flange and the box's lid go: 929 and 649 over 3960.
        Assert.Equal(234.6, SteelFire.Factors(Ub254, true).Profile, 0);
        Assert.Equal(163.9, SteelFire.Factors(Ub254, true).Box, 0);
    }

    [Fact]
    public void ABeamHeldAlongItsTopReachesItsCriticalTemperatureWithNoBuckling()
    {
        // 355 × 393e3 = 140 kNm cold; carrying 0.4 of it, the yield factor falls to 0.4 at 629 °C by straight lines through Table 3.1: 600 + 100 × 0.07 / 0.24. (4.22), a curve through the same, says 620.
        var moment = 0.4 * Ub254.Wply * 355 / 1e6;
        var forces = new MemberForces(0, 0, 0, 0, moment, 0, false);
        var critical = SteelFire.CriticalOf(Ub254, new MemberLengths(5000, 5000, 0, 0), forces);
        Assert.InRange(critical, 628, 630);

        // An adaptation factor of 0.7 under a concrete floor lets it go hotter.
        Assert.True(SteelFire.CriticalOf(Ub254, new MemberLengths(5000, 5000, 0, 0), forces, 0.7) > critical + 30);
    }

    [Fact]
    public void AColumnBucklesLongBeforeItYields()
    {
        // In compression alone, the same load fails the slender column well below the stocky one's temperature.
        var forces = new MemberForces(-300, 0, 0, 0, 0, 0, false);
        var stocky = SteelFire.CriticalOf(Ub254, new MemberLengths(500, 500, 0, 0), forces);
        var slender = SteelFire.CriticalOf(Ub254, new MemberLengths(3000, 3000, 0, 0), forces);
        Assert.True(slender < stocky - 50);
    }

    [Fact]
    public void TimberBurnsAtItsNotionalRate()
    {
        // 0.8 × 30 + 7 = 31 at 30 minutes; 0.7 × 30 + 7 = 28 for glulam; 0.8 × 10 + 3.5 = 11.5 at 10, the zero strength layer not yet whole.
        Assert.Equal(31, TimberFire.Effective(30, false), 6);
        Assert.Equal(28, TimberFire.Effective(30, true), 6);
        Assert.Equal(11.5, TimberFire.Effective(10, false), 6);

        var (depth, width) = TimberFire.Left(225, 100, 30, false, false);
        Assert.Equal(194, depth, 6);
        Assert.Equal(38, width, 6);
        Assert.Equal(163, TimberFire.Left(225, 100, 30, false, true).Depth, 6);
    }

    [Fact]
    public void TimberIsTakenAtItsTwentiethPercentileWithNoPartialFactor()
    {
        // fd,fi = kmod,fi × kfi × fk / γM,fi = 1.0 × 1.25 × 24 / 1.0: with kmod 1 the check at normal temperature divides the 1.3 back out.
        var hot = TimberFire.Hot(TimberDesign.C24);
        Assert.Equal(1.25 * 24, 1.0 * hot.Fm / hot.GammaM, 6);
        Assert.Equal(1.15 * 24, TimberFire.Hot(TimberDesign.Gl24h).Fm / TimberDesign.Gl24h.GammaM, 6);

        // Its slenderness does not change: strength and stiffness rise together.
        Assert.Equal(TimberDesign.C24.Fc0 / TimberDesign.C24.E05, hot.Fc0 / hot.E05, 9);
    }

    [Theory]
    [InlineData(30, 300, 80, 15)]
    [InlineData(60, 300, 120, 25)]
    [InlineData(60, 200, 120, 30)]
    [InlineData(90, 200, 150, 45)]
    [InlineData(120, 240, 200, 60)]
    [InlineData(120, 150, 200, 65)]
    public void ABeamIsTable55(int minutes, double width, double needs, double axis)
    {
        var (least, distance) = ConcreteFire.Beam(minutes, width);
        Assert.Equal(needs, least);
        Assert.Equal(axis, distance);
    }

    [Fact]
    public void AColumnIsTable52a()
    {
        // The least it may be, and how far in its bars want to be at the size it is.
        Assert.Equal((200, 25), ConcreteFire.Column(30, 0.2, 300));
        Assert.Equal((200, 36), ConcreteFire.Column(60, 0.5, 250));
        Assert.Equal((200, 31), ConcreteFire.Column(60, 0.5, 300));
        Assert.Equal((250, 40), ConcreteFire.Column(60, 0.7, 400));
        Assert.Null(ConcreteFire.Column(60, 0.8, 400));
        Assert.Null(ConcreteFire.Column(240, 0.7, 500));
    }

    [Fact]
    public void AWallIsTable54()
    {
        Assert.Equal((100, 10), ConcreteFire.Wall(30, false, false));
        Assert.Equal((140, 10), ConcreteFire.Wall(60, true, true));
        Assert.Equal((170, 25), ConcreteFire.Wall(90, true, true));
        Assert.Equal((160, 35), ConcreteFire.Wall(120, true, false));
    }

    [Fact]
    public void ASlabIsTable58OrTable59()
    {
        Assert.Equal((80, 20), ConcreteFire.Slab(60, false));
        Assert.Equal((180, 15), ConcreteFire.Slab(60, true));
        Assert.Equal((200, 25), ConcreteFire.Slab(90, true));
    }

    [Theory]
    [InlineData(BuildingUse.House, 2, "1")]
    [InlineData(BuildingUse.House, 4, "1")]
    [InlineData(BuildingUse.House, 5, "2a")]
    [InlineData(BuildingUse.Flats, 4, "2a")]
    [InlineData(BuildingUse.Flats, 5, "2b")]
    [InlineData(BuildingUse.Office, 4, "2a")]
    [InlineData(BuildingUse.Office, 15, "2b")]
    [InlineData(BuildingUse.Office, 16, "3")]
    [InlineData(BuildingUse.Other, 2, "")]
    public void TheClassIsTable11(BuildingUse use, int storeys, string expected) => Assert.Equal(expected, Robustness.ClassOf(use, storeys));

    [Fact]
    public void TiesComeToAnnexA()
    {
        // The standard's own example: (3 + 0.5 × 5) × 2.5 × 6 = 82.5, 0.8 of it 66 kN, under the least of 75.
        Assert.Equal(75, Robustness.FramedTie(82.5, false));
        Assert.Equal(160, Robustness.FramedTie(200, false));
        Assert.Equal(80, Robustness.FramedTie(200, true));
        Assert.Equal(75, Robustness.FramedTie(100, true));

        // 20 + 4 × 6 = 44; at 11 storeys 64 would pass the 60 it stops at.
        Assert.Equal(44, Robustness.BasicTie(6));
        Assert.Equal(60, Robustness.BasicTie(11));

        // 6 storeys, 7.5 kN/m² and z of 5 m: just Ft; 15 kN/m² is twice it.
        Assert.Equal(44, Robustness.WallTie(6, 7.5, 5), 6);
        Assert.Equal(88, Robustness.WallTie(6, 15, 5), 6);

        // A metre of 200 wall 3 m high: 34 × 200000 / 8000 × 225 = 191 kN; of 300, 127.5; of 400, 96, under the 100 kN/m.
        Assert.Equal(191.25, Robustness.VerticalTie(200000, 3000, 200, 1000), 6);
        Assert.Equal(127.5, Robustness.VerticalTie(300000, 3000, 300, 1000), 6);
        Assert.Equal(100, Robustness.VerticalTie(400000, 3000, 400, 1000), 6);
    }

    /// <summary>A rectangle of bearing walls on strips, a floor on joists at the first level and a roof, of a wall type by the start of its name.</summary>
    private static BimDocument House(string wall, string building = "", bool concreteFloor = false)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = building;
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var level = new BIMDesigner.Core.Datums.Level { Name = "Roof", Elevation = 6000 };
        document.AddLevel(level);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        foreach (var (from, to) in new[] { (ground, first), (first, level.Id) })
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = from, TopLevelId = to, StructuralUsage = StructuralUsage.Bearing });
        foreach (var each in document.Walls.Where(each => each.LevelId == ground).ToList()) document.Add(Footings.Fit(document, each));

        Floor Floored(FloorType floorType)
        {
            var floor = new Floor { TypeId = floorType.Id, LevelId = first };
            floor.SetBoundary(corners);
            return floor;
        }

        if (concreteFloor)
        {
            var slab = new FloorType("Slab 200", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal)).Id, 200)));
            document.AddType(slab);
            document.Add(Floored(slab));
        }
        else
        {
            var joist = new BeamType("Timber 75 x 225") { Material = BeamMaterial.Timber, Width = 75, Depth = 225 };
            document.AddType(joist);
            for (var x = 1000; x <= 9000; x += 2000)
                document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 8000), Usage = BeamUsage.Joist });
            var floor = Floored(document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)));
            floor.SpanDirection = 0;
            document.Add(floor);
        }

        return document;
    }

    private static (SecondOrderAnalysis Second, IReadOnlyList<MemberCheck> Members) Analysed(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var second = SecondOrderAnalysis.Run(document, analysis);
        return (second, MemberStrength.Of(document, analysis, second));
    }

    [Fact]
    public void ATwoStoreyTimberHouseStandsByItsRatings()
    {
        var document = House("Timber Frame");
        var (second, members) = Analysed(document);
        Assert.Equal(30, FireDesign.PeriodOf(document).Minutes);
        var checks = FireDesign.Of(document, second, members);

        // The walls under the floor, the joists in it and the floor; not the upper walls, which carry only a roof, nor the lowest floor.
        Assert.Equal(4, checks.Count(check => check.Kind == "Wall"));
        Assert.All(checks.Where(check => check.Kind == "Joist"), check => Assert.Equal("Fire Rating", check.Worst!.Name));
        Assert.Contains(checks, check => check.Kind == "Floor" && check.Enough);
        Assert.All(checks, check => Assert.True(check.Enough, check.Name));
    }

    [Fact]
    public void ABlockWallWithNoRatingIsSaidNotToBeChecked()
    {
        var document = House("Exterior");
        var (second, members) = Analysed(document);
        foreach (var type in document.TypesOf<WallType>()) type.FireRating = string.Empty;
        var wall = FireDesign.Of(document, second, members).First(check => check.Kind == "Wall");
        Assert.Empty(wall.Checks);
        Assert.Contains("BS EN 1996-1-2", wall.Limits);
    }

    [Fact]
    public void ARatingTooShortIsOver()
    {
        var document = House("Exterior");
        document.ProjectInformation.BuildingType = "Flats";
        var (second, members) = Analysed(document);
        foreach (var type in document.TypesOf<WallType>()) type.FireRating = "15 min";
        var wall = FireDesign.Of(document, second, members).First(check => check.Kind == "Wall");
        Assert.False(wall.Enough);
        Assert.Contains("giving 15", wall.Wants);
    }

    [Fact]
    public void AJoistInAFloorWithNoRatingIsWorkedOutBare()
    {
        var document = House("Exterior");
        var (second, members) = Analysed(document);
        var boards = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var bare = new FloorType("Boards alone", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, boards.Structure.Layers[1].MaterialId, 180)));
        document.AddType(bare);
        foreach (var floor in document.All<Floor>()) floor.TypeId = bare.Id;
        var joist = FireDesign.Of(document, second, members).First(check => check.Kind == "Joist");

        // 75 wide, 31 gone each side: 13 by 194 left, far too slender to stand.
        Assert.Contains(joist.Checks, check => check.Note.Contains("13 by 194"));
        Assert.False(joist.Enough);
        Assert.Contains("a lining or a Fire Rating", joist.Wants);
    }

    [Fact]
    public void AConcreteFloorIsSizedByItsTable()
    {
        var document = House("Exterior", concreteFloor: true);
        var (second, members) = Analysed(document);
        var floor = FireDesign.Of(document, second, members).Single(check => check.Kind == "Floor");
        Assert.Equal(60, floor.Worst!.Demand);
        Assert.Equal(200, floor.Worst!.Capacity);
        Assert.Contains("10 mm", floor.Wants);
    }

    [Fact]
    public void ASteelBeamUnderAFloorIsBareUntilItIsRated()
    {
        // Small enough for its share of a light floor to tell: a heavy beam on so little could stand bare for half an hour, and does.
        var document = House("Exterior");
        var first = document.Levels[1].Id;
        var ground = document.Levels[0].Id;
        var steel = new BeamType("UB 127") { Section = BeamSection.IBeam, Material = BeamMaterial.Steel, Depth = 127, Width = 76, WebThickness = 4, FlangeThickness = 7.6 };
        document.AddType(steel);
        document.Add(new Beam { TypeId = steel.Id, LevelId = first, Start = new Point2D(0, 4000), End = new Point2D(10000, 4000) });
        var post = document.TypesOf<ColumnType>().First(each => each.Structural && each.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = ground, TopLevelId = first, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        var (second, members) = Analysed(document);

        var beam = FireDesign.Of(document, second, members).First(check => check.Kind == "Beam");
        Assert.False(beam.Enough);
        Assert.Contains("hold it under", beam.Wants);
        Assert.Contains("Ap/V", beam.Wants);

        steel.FireRating = "60 min";
        var rated = FireDesign.Of(document, second, members).First(check => check.Kind == "Beam");
        Assert.True(rated.Enough);
        Assert.Equal("Fire Rating", rated.Worst!.Name);
    }

    [Fact]
    public void ABeamsRatingIsKeptWithTheProjectAndInACopy()
    {
        var document = BimDocument.CreateDefault();
        var steel = new BeamType("UB 254") { Section = BeamSection.IBeam, Material = BeamMaterial.Steel, Depth = 254, Width = 146, WebThickness = 6, FlangeThickness = 8.6, FireRating = "60 min" };
        document.AddType(steel);
        Assert.Equal("60 min", steel.Duplicate("Copy").FireRating);
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<BeamType>(steel.Id);
        Assert.Equal("60 min", reopened!.FireRating);
    }

    [Fact]
    public void AUsProjectSaysWhyNoneIsWorkedOut()
    {
        var document = House("Exterior");
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (second, members) = Analysed(document);
        Assert.Empty(FireDesign.Of(document, second, members));
        Assert.Contains("IRC", FireDesign.PeriodOf(document).Said);
        document.ProjectInformation.BuildingType = "Office";
        Assert.Contains("Table 601", FireDesign.PeriodOf(document).Said);
    }

    [Fact]
    public void AFloorMakesAStoreyWhetherOrNotWallsAreDrawnOnIt()
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var type = document.TypesOf<WallType>().First();
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = ground, TopLevelId = first });
        Assert.Equal(1, FireDesign.HeightOf(document).Storeys);

        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = first };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(5000, 0), new Point2D(5000, 5000), new Point2D(0, 5000) });
        document.Add(floor);
        var (storeys, top) = FireDesign.HeightOf(document);
        Assert.Equal(2, storeys);
        Assert.True(top > 2.5);
    }

    /// <summary>Five storeys of 250 slab on 300 square columns on a 6 m grid, 3.5 m floor to floor.</summary>
    private static BimDocument Offices()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = "Office";
        document.ProjectInformation.Ground = GroundType.StiffClay;
        for (var i = 2; i <= 5; i++)
            if (document.Levels.Count <= i) document.AddLevel(new BIMDesigner.Core.Datums.Level { Name = $"Level {i}", Elevation = i * 3500 });
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        levels[1].Elevation = 3500;
        var slab = new FloorType("Slab 250", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal)).Id, 250)));
        document.AddType(slab);
        var square = new ColumnType("Concrete 300", ColumnShape.Rectangular, 300, 300) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        document.AddType(square);
        for (var x = 0; x <= 12000; x += 6000)
        for (var y = 0; y <= 12000; y += 6000)
        for (var level = 0; level < 5; level++)
        {
            var column = new Column { TypeId = square.Id, LevelId = levels[level].Id, TopLevelId = levels[level + 1].Id, Location = new Point2D(x, y), Structural = true };
            document.Add(column);
            if (level == 0) document.Add(Footings.Fit(document, column));
        }

        for (var level = 1; level <= 5; level++)
        {
            var floor = new Floor { TypeId = slab.Id, LevelId = levels[level].Id };
            floor.SetBoundary(new[] { new Point2D(-600, -600), new Point2D(12600, -600), new Point2D(12600, 12600), new Point2D(-600, 12600) });
            document.Add(floor);
        }

        return document;
    }

    [Fact]
    public void AnOfficeOfFiveStoreysIsClass2bAtAnHour()
    {
        var document = Offices();
        var (second, members) = Analysed(document);
        Assert.Equal(60, FireDesign.PeriodOf(document).Minutes);
        var fires = FireDesign.Of(document, second, members);
        Assert.All(fires.Where(check => check.Kind == "Column"), check => Assert.Equal("Table 5.2a", check.Worst!.Clause));
        Assert.All(fires.Where(check => check.Kind == "Floor"), check =>
        {
            Assert.Equal("Table 5.9", check.Worst!.Clause);
            Assert.Equal(180, check.Worst.Demand);
        });

        // The centre column at the bottom: five floors of 6 m by 6 m at 0.25 × 25 + 0.5 × (2.5 + 0.8) = 7.9 kN/m², 1,420 kN and its own weight: 0.65 or so of what it carries cold, so the 0.7 column of the table.
        var centre = fires.Where(check => check.Kind == "Column").OrderByDescending(check => check.Worst!.Demand).First();
        Assert.Equal(250, centre.Worst!.Demand);

        var result = Robustness.Of(document, second);
        Assert.Equal("2b", result.Class);

        // With no beams its slabs tie it: (6.1 + 0.5 × 2.5) × 6 × 6 = 265 kN on the grid, 0.8 of it 212 kN and 424 mm² of bars at 500; 0.4 of it 106 kN round the edge.
        var slab = result.Ties.First(tie => tie.Kind == "Floor ties");
        var along = double.Parse(slab.Wants.Split(" for ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(along, 205, 220);
        Assert.Contains("along each line of columns", slab.Wants);

        // A vertical tie is the most from any one storey, A.6(2), not the whole stack: 6 × 6 × 7.9 = 284 kN at a centre column on a slab not far from it, a little more over the middle of a continuous slab.
        var ties = result.Ties.Where(tie => tie.Kind == "Vertical tie").ToList();
        Assert.Equal(45, ties.Count);
        var forces = ties.Select(tie => double.Parse(tie.Wants.Split(" for ")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.InRange(forces.Max(), 260, 400);
    }

    [Fact]
    public void AHouseWantsNoTies()
    {
        var document = House("Exterior");
        var (second, _) = Analysed(document);
        var result = Robustness.Of(document, second);
        Assert.Equal("1", result.Class);
        Assert.Empty(result.Ties);
        Assert.Contains("No additional measures", result.Asks);
    }

    [Fact]
    public void FlatsAnchorTheirFloorsAndTieTheirBeams()
    {
        var document = House("Exterior", "Flats");
        var steel = new BeamType("UB 254") { Section = BeamSection.IBeam, Material = BeamMaterial.Steel, Depth = 254, Width = 146, WebThickness = 6, FlangeThickness = 8.6 };
        document.AddType(steel);
        document.Add(new Beam { TypeId = steel.Id, LevelId = document.Levels[1].Id, Start = new Point2D(0, 4000), End = new Point2D(10000, 4000) });
        var (second, _) = Analysed(document);
        var result = Robustness.Of(document, second);
        Assert.Equal("2a", result.Class);
        Assert.Contains(result.Ties, tie => tie.Kind == "Floor anchorage");
        var beam = result.Ties.Single(tie => tie.Kind == "Internal tie");

        // Never under 75 kN; 3960 mm² at 345 or so carries 1,366 kN or so.
        Assert.Equal(75, beam.Checks.Single().Demand, 6);
        Assert.InRange(beam.Checks.Single().Capacity, 1200, 1500);
    }

    [Theory]
    [InlineData("Warehouse", BuildingUse.Other)]
    [InlineData("", BuildingUse.House)]
    [InlineData("Detached house", BuildingUse.House)]
    [InlineData("Block of flats", BuildingUse.Flats)]
    [InlineData("Flat", BuildingUse.Flats)]
    [InlineData("Extension", BuildingUse.House)]
    [InlineData("Other", BuildingUse.Other)]
    [InlineData("Bungalow", BuildingUse.House)]
    [InlineData("Head Office", BuildingUse.Office)]
    public void WhatABuildingIsCalledSaysWhatItIs(string name, BuildingUse use)
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.BuildingType = name;
        Assert.Equal(use, FireDesign.UseOf(document));
    }

    [Fact]
    public void AllElseIsAsked()
    {
        var document = House("Exterior", "Warehouse");
        var (second, _) = Analysed(document);
        Assert.Contains("Building Type", Robustness.Of(document, second).Said);
    }
}
