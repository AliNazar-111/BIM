using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// The gaps the audit left (round 36): stud walls and concrete walls, a beam's web where it bears, a concrete beam under a wall, and wind on roofs, each from figures worked by hand.
/// </summary>
public class WallDesignTests
{
    private const double Psi = 0.00689476;

    [Fact]
    public void ANailThroughABoardComesToJohansen()
    {
        // 3.1 by 50 through 9 OSB into 41 of C24: fh,1 36.7, fh,2 20.4, My 3,400 N mm. Ways (a) 1,024, (b) 2,593, (c) 938, (d) 589, (e) 1,082, (f) 854 N: (d).
        Assert.InRange(TimberWallDesign.NailInBoard(3.1, 9, 41), 0.58, 0.60);
    }

    [Fact]
    public void RackingIsItsFastenersAlongTheLengthsThatCount()
    {
        // 3 m of wall 2.4 high, nails 0.5 kN at 150: 1.2 × 0.5 × 3000 / 150 = 12 kN.
        Assert.Equal(12, TimberWallDesign.RackingEurocode(new[] { 3000.0 }, 2400, 0.5, 150), 6);

        // A length under half the height counts in proportion: 600 of a 2.4 m wall, half of it.
        Assert.Equal(1.2 * 0.5 * 600 / 150 * 0.5, TimberWallDesign.RackingEurocode(new[] { 600.0 }, 2400, 0.5, 150), 6);
    }

    [Fact]
    public void RackingByTheSdpwsIsItsUnitShearAndAspect()
    {
        // 670 plf is 9.78 kN/m; φ 0.8 is 7.82.
        Assert.InRange(TimberWallDesign.RackingSdpws(new[] { 3000.0 }, 2400), 3 * 7.75, 3 * 7.9);

        // Taller than 3.5 of its length it does not count; at 2.5 of it, 0.9375.
        Assert.Equal(0, TimberWallDesign.RackingSdpws(new[] { 600.0 }, 2400));
        Assert.InRange(TimberWallDesign.RackingSdpws(new[] { 1000.0 }, 2500), 0.9375 * 7.75, 0.9375 * 7.9);
    }

    [Fact]
    public void AStudsFootSpreadsAlongItsPlate()
    {
        // 38 stud on its plate, 98 of it effective, 89 deep, 5 kN; C24, kmod 0.8: 1.25 × 2.5 × 0.8 / 1.3.
        var check = TimberWallDesign.FootEurocode(5, 38, 89, TimberDesign.C24, 0.8);
        Assert.Equal(5000.0 / (89 * 98), check.Demand, 6);
        Assert.Equal(1.25 * 2.5 * 0.8 / 1.3, check.Capacity, 6);

        // By the NDS, 1.5 in of bearing has a Cb of 1.25.
        Assert.Equal(625 * Psi * 1.67 * 0.9 * (1.5 + 0.375) / 1.5, TimberWallDesign.FootNds(5, 38.1, 89).Capacity, 3);
    }

    [Fact]
    public void AConcreteWallBySimplifiedAciIsItsFormula()
    {
        // 3,000 psi, 200 thick, 3 m, k 1: 0.65 × 0.55 × 20.68 × 200 × (1 - (3000 / 6400)²) = 1,153 kN/m.
        Assert.InRange(ConcreteWallDesign.SimplifiedAci(3000 * Psi, 200, 3000, 1), 1148, 1158);
    }

    private static readonly SteelSection Ub254 = new("I Section", 251.4, 146.1, 8.6, 6.0, 3960, 4410e4, 448e4, 353e3, 61.3e3, 393e3, 94.1e3, 8.55e4, 0.0659e12, 7.6);

    [Fact]
    public void AWebOverABearingComesToEurocode3Part1To5()
    {
        // 254 UB on 200 of stiff bearing, along it, S355: hw 234, kF 6, Fcr 1,046 kN; m1 24.4, m2 14.8, ly 325; λF 0.81, χF 0.61: 426 kN.
        Assert.InRange(JointDesign.WebBearingEurocode(Ub254, 200, 6000, false), 420, 432);

        // At an end, type (c): le 200, ly by (6.13) 254, λF 0.72, χF 0.70: 376 kN.
        Assert.InRange(JointDesign.WebBearingEurocode(Ub254, 200, 6000, true), 372, 380);

        // More bearing carries more.
        Assert.True(JointDesign.WebBearingEurocode(Ub254, 300, 6000, false) > JointDesign.WebBearingEurocode(Ub254, 200, 6000, false));
    }

    [Fact]
    public void AWebOverABearingComesToAisc()
    {
        // k 16.2, lb 200, in A992: yielding 345 × 6 × (5k + 200) = 595 kN; crippling 0.75 × 0.80 × 36 (1 + 3 × 0.796 × 0.583) √(2e5 × 345 × 8.6 / 6) = 513 kN.
        Assert.InRange(JointDesign.WebBearingAisc(Ub254, 200, false), 505, 520);
    }

    private static BimDocument House(string wall, ClearanceStandard? standard = null)
    {
        var document = BimDocument.CreateDefault();
        if (standard is { } each) document.ProjectInformation.ClearanceStandard = each;
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing }).ToList();
        foreach (var each2 in walls) document.Add(each2);
        foreach (var each2 in walls) document.Add(Footings.Fit(document, each2));
        const double over = 465;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name.StartsWith("Roof - Slate on Timber", StringComparison.Ordinal)).Id, LevelId = first };
        roof.SetBoundary(new[] { new Point2D(-over, -over), new Point2D(10000 + over, -over), new Point2D(10000 + over, 8000 + over), new Point2D(-over, 8000 + over) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35, WallId = walls[i].Id, OnLeftOfWall = false, Overhang = 300 }));
        document.Add(roof);
        return document;
    }

    private static (StructuralLoading Loading, StructuralAnalysis Analysis, SecondOrderAnalysis Second) Analysed(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var analysis = StructuralAnalysis.Run(document, model, loading);
        return (loading, analysis, SecondOrderAnalysis.Run(document, analysis));
    }

    [Fact]
    public void ATimberFrameHouseHasItsStudsAndRackingChecked()
    {
        var document = House("Timber Frame");
        var (_, _, second) = Analysed(document);
        var walls = MasonryDesign.Of(document, second);
        Assert.All(walls, wall =>
        {
            Assert.Contains(wall.Checks, check => check.Name.StartsWith("Stud:", StringComparison.Ordinal));
            Assert.Contains(wall.Checks, check => check.Name == "Stud on its plate");
            Assert.Contains(wall.Checks, check => check.Name == "Racking");
            Assert.Contains("No frame is drawn", wall.Limits);
            Assert.StartsWith("BS EN 1995-1-1", wall.By, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ADrawnFrameIsTheOneChecked()
    {
        var document = House("Timber Frame");
        foreach (var wall in document.Walls.ToList())
        {
            var framing = WallFramings.New(document, wall);
            framing.Section = "47 x 150 C24";
            framing.Spacing = 600;
            document.Add(framing);
        }

        var (_, _, second) = Analysed(document);
        var check = MasonryDesign.Of(document, second).First();
        Assert.Equal("47 x 150 C24 at 600 centres", check.Grade);
        Assert.DoesNotContain("No frame is drawn", check.Limits);
    }

    [Fact]
    public void AUsTimberFrameIsCheckedByTheNdsAndTheSdpws()
    {
        var document = House("Timber Frame", ClearanceStandard.Irc);
        var (_, _, second) = Analysed(document);
        Assert.All(MasonryDesign.Of(document, second), wall =>
        {
            Assert.Contains("NDS", wall.By);
            Assert.Contains(wall.Checks, check => check.Clause == "SDPWS 4.3");
        });
    }

    [Fact]
    public void AWallWithNoOpeningRacksOnAllOfIt()
    {
        var document = House("Timber Frame");
        var panel = AnalyticalModel.Of(document).Panels.First(p => p.Kind == AnalyticalPanelKind.Wall);
        Assert.Equal(panel.FlatOutline.Max(p => p.X) - panel.FlatOutline.Min(p => p.X), TimberWallDesign.SolidLengths(panel).Sum(), 1);
    }

    [Fact]
    public void AnOpeningInAStudWallHasItsHeader()
    {
        var document = House("Timber Frame");
        var wall = document.Walls.First();
        document.Add(new Window { HostWallId = wall.Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = wall.LevelId, DistanceAlongWall = 5000 });
        var (_, _, second) = Analysed(document);
        var header = JointDesign.Of(document, second).Single(joint => joint.Kind == "Header");
        Assert.Contains("on a jack each side", header.Wants);
        Assert.Contains(header.Checks, check => check.Name.StartsWith("Bending", StringComparison.Ordinal));
    }

    [Fact]
    public void ADoorIsAnOpeningThoughItIsNoHole()
    {
        // A door stands on the foot of its wall: a notch in the outline the mesh takes without a hole, and it still wants carrying over and still takes the wall's length away.
        var document = House("Timber Frame");
        var wall = document.Walls.First();
        document.Add(new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, DistanceAlongWall = 5000 });
        var panel = AnalyticalModel.Of(document).Panels.Single(p => ReferenceEquals(p.Element, wall));
        Assert.Empty(panel.Openings);
        Assert.Single(panel.Cuts);

        var whole = panel.FlatOutline.Max(p => p.X) - panel.FlatOutline.Min(p => p.X);
        var cut = panel.Cuts[0].Select(panel.Flat).ToList();
        Assert.Equal(whole - (cut.Max(p => p.X) - cut.Min(p => p.X)), TimberWallDesign.SolidLengths(panel).Sum(), 1);
        Assert.Equal(2, TimberWallDesign.SolidLengths(panel).Count);

        var (_, _, second) = Analysed(document);
        Assert.Contains(JointDesign.Of(document, second), joint => joint.Kind == "Header");
    }

    [Fact]
    public void ADoorInABlockWallHasItsLintel()
    {
        var document = House("Exterior");
        var wall = document.Walls.First();
        document.Add(new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, DistanceAlongWall = 5000 });
        var (_, _, second) = Analysed(document);
        Assert.Contains(JointDesign.Of(document, second), joint => joint.Kind == "Lintel");
    }

    [Fact]
    public void RackingIsTheWindAndNotTheLoadArchingOverADoor()
    {
        // Wind across the ridge of a 10 by 8 house: half of the 27.2 kN on its long walls goes straight down to their sole plates, the rest and the roof's 27.3 through its end walls: 1.5 × 40.9 / 2 = 30.7 kN each.
        var document = House("Timber Frame");
        var wall = document.Walls.First();
        document.Add(new Door { HostWallId = wall.Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, DistanceAlongWall = 5000 });
        var (_, analysis, second) = Analysed(document);
        var checks = MasonryDesign.Of(document, second).ToList();
        var ends = checks.Where(check => Math.Abs(check.Panel.Normal.X) > 0.5).ToList();
        Assert.Equal(2, ends.Count);
        Assert.All(ends, check => Assert.InRange(check.Checks.Single(each => each.Name == "Racking").Demand, 27, 34));

        // No wind, no racking, however the plate arches over the door.
        var long_ = analysis.Model.Panels.Single(panel => ReferenceEquals(panel.Element, wall));
        var height = long_.FlatOutline.Max(p => p.Y) - long_.FlatOutline.Min(p => p.Y);
        foreach (var (combination, bands) in second.WallStretches(long_, height).Where(entry => entry.Combination.Terms.All(term => term.Case.Nature != LoadNature.Wind)))
            Assert.All(bands, band => Assert.Equal(0, band.Lateral));
    }

    [Fact]
    public void ASolidTimberWallIsSaidToBeLeftOut()
    {
        var document = House("Log Wall");
        var (_, _, second) = Analysed(document);
        Assert.All(MasonryDesign.Of(document, second), wall => Assert.Contains("Not checked", wall.Limits));
    }

    [Fact]
    public void AConcreteWallIsWorkedOutAsAColumnAMetreWide()
    {
        var document = House("Generic");
        var (_, _, second) = Analysed(document);
        Assert.All(MasonryDesign.Of(document, second), wall =>
        {
            Assert.Contains(wall.Checks, check => check.Name == "Load and bending");
            Assert.Contains(wall.Checks, check => check.Name == "Shear in its plane" && check.Unit == "kN");
            Assert.Contains(wall.Checks, check => check.Name == "Load and bending" && check.Note.Contains("needs") && check.Unit == "mm²/m" && check.Capacity == 8000);
            Assert.Contains("No bars are in the model", wall.Limits);
        });
    }

    [Fact]
    public void AUsConcreteWallTakesTheSimplifiedMethodWithItsLoadInItsMiddleThird()
    {
        var document = House("Generic", ClearanceStandard.Irc);
        var (_, _, second) = Analysed(document);
        var checks = MasonryDesign.Of(document, second).SelectMany(wall => wall.Checks).ToList();
        Assert.Contains(checks, check => check.Clause == "11.5.3.1" && check.Name == "Load and bending");

        // A 3 m wall wants 3000 / 25 = 120 by Table 11.3.1.1.
        Assert.Contains(checks, check => check.Name == "Least thickness" && Math.Abs(check.Demand - 120) < 0.1);
    }

    [Fact]
    public void WindGoesOnARoofAsWellAsItsWalls()
    {
        // Wind across the ridge on slopes of 48.8 m² on plan at 35°, 4.88 m of plan to each metre of eave: H and I of Table 7.4a, +0.467 and -0.333, with F and G +0.7 along the windward eave
        // and J -0.433 along the leeward side of the ridge, each strip e/10 deep, e the lesser of the 10 m across and 2h; sideways by tan 35°. The 300 overhang windward lifted at zone D's +0.717.
        var document = House("Exterior");
        var (loading, _, _) = Analysed(document);
        var wind = loading.Cases.First(c => c.Nature == LoadNature.Wind && Math.Abs(c.Wind!.Value.Y) > 0.5);
        var lines = loading.LineLoads.Where(load => load.Case == wind).ToList();
        Assert.NotEmpty(lines);
        var qp = WindPressure(loading, wind);
        var h = Wind.Height(document) / 1000;
        var strip = Math.Min(Math.Min(10, 2 * h) / 10, 4.88) / 4.88;
        var (windward, leeward) = (0.7 * strip + 0.4667 * (1 - strip), -0.4333 * strip - 0.3333 * (1 - strip));
        var sideways = Math.Abs(lines.Sum(load => load.Force.Y * (load.End - load.Start) / 1000));
        Assert.InRange(sideways, 0.97 * (windward - leeward) * qp * 48.8 * Math.Tan(35 * Math.PI / 180), 1.03 * (windward - leeward) * qp * 48.8 * Math.Tan(35 * Math.PI / 180));

        // Down on the windward, up off the leeward and under the overhang.
        var zoneD = 0.7 + 0.1 * (h / 8 - 0.25) / 0.75;
        var down = -lines.Sum(load => load.Force.Z * (load.End - load.Start) / 1000);
        var expected = (windward + leeward) * qp * 48.8 - zoneD * qp * 0.3 * 10;
        Assert.InRange(down, expected - 0.03 * Math.Abs(expected), expected + 0.03 * Math.Abs(expected));
    }

    [Fact]
    public void WindAlongARidgeLiftsARoofOnly()
    {
        // Table 7.4b at 35°: within e/10 of the windward gable F -1.1 for e/4 in from the eave and G -1.4 between, H -0.833 to e/2, I -0.5 past it, along each 10 m eave, both slopes up,
        // nothing sideways: they cancel.
        var document = House("Exterior");
        var (loading, _, _) = Analysed(document);
        var wind = loading.Cases.First(c => c.Nature == LoadNature.Wind && Math.Abs(c.Wind!.Value.X) > 0.5);
        var lines = loading.LineLoads.Where(load => load.Case == wind).ToList();
        var qp = WindPressure(loading, wind);
        var e = Math.Min(8, 2 * Wind.Height(document) / 1000);
        var near = (-1.1 * Math.Min(e / 4, 4.88) - 1.4 * Math.Max(0, 4.88 - e / 4)) / 4.88;
        var up = -2 * 4.88 * qp * (near * e / 10 - 0.8333 * (e / 2 - e / 10) - 0.5 * (10 - e / 2));
        Assert.InRange(lines.Sum(load => load.Force.X * (load.End - load.Start) / 1000), -0.5, 0.5);
        Assert.InRange(lines.Sum(load => load.Force.Y * (load.End - load.Start) / 1000), -0.5, 0.5);
        Assert.InRange(lines.Sum(load => load.Force.Z * (load.End - load.Start) / 1000), 0.97 * up, 1.03 * up);
    }

    /// <summary>The peak pressure a case was worked from, read back from a wall into the wind: 0.85 × 0.7 or so of it, by its own note.</summary>
    private static double WindPressure(StructuralLoading loading, LoadCase wind)
    {
        var said = loading.SurfaceLoads.First(load => load.Case == wind && load.Said.Contains("qp ", StringComparison.Ordinal)).Said;
        var at = said.IndexOf("qp ", StringComparison.Ordinal) + 3;
        return double.Parse(said[at..said.IndexOf(" kPa", at, StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void WhatIsDrawnAlongABeamIsWhatWasChecked()
    {
        // A timber beam between two walls with a floor on it, its load evenly along it: moment wL²/8 is a quarter of its end shear wL/2 times L, and it runs from none at each end to a peak in the middle.
        var document = House("Exterior");
        var ground = document.Levels[0].Id;
        var first = document.Levels[1].Id;
        var type = new BeamType("Timber 75 x 225") { Material = BeamMaterial.Timber, Width = 75, Depth = 225 };
        document.AddType(type);
        for (var x = 1000; x <= 9000; x += 2000)
            document.Add(new Beam { TypeId = type.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 8000), Usage = BeamUsage.Joist });
        var boards = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal));
        var floor = new Floor { TypeId = boards.Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);

        var (_, analysis, second) = Analysed(document);
        var member = analysis.Model.Members.First(each => each.Kind == AnalyticalMemberKind.Joist && Math.Abs(each.Start.At.X - 5000) < 200);
        var check = MemberStrength.Of(document, analysis, second).Single(each => ReferenceEquals(each.Member, member));
        var under = check.Worst!.Under;
        var along = second.DiagramOf(member, under);
        Assert.True(along.Count >= 8);
        Assert.Equal(0, along[0].At, 3);
        Assert.Equal(member.Length, along[^1].At, 0);

        var peak = along.MaxBy(station => Math.Abs(station.Forces.My));
        Assert.InRange(peak.At, 0.35 * member.Length, 0.65 * member.Length);
        Assert.True(Math.Abs(along[0].Forces.My) < 0.05 * Math.Abs(peak.Forces.My));
        Assert.InRange(Math.Abs(peak.Forces.My) / (Math.Abs(along[0].Forces.Vz) * member.Length / 1000), 0.23, 0.27);
        Assert.Empty(second.DiagramOf(member, "no such combination"));
    }

    [Fact]
    public void AConcreteBeamUnderAWallHasItsSteelSaid()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var ground = document.Levels[0].Id;
        var first = document.Levels[1].Id;
        var level = new BIMDesigner.Core.Datums.Level { Name = "Roof", Elevation = 6000 };
        document.AddLevel(level);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 6000), TypeId = type.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing });
        document.Add(new Wall { Start = new Point2D(6000, 0), End = new Point2D(6000, 6000), TypeId = type.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing });
        document.Add(new Wall { Start = new Point2D(6000, 6000), End = new Point2D(0, 6000), TypeId = type.Id, LevelId = ground, TopLevelId = first, StructuralUsage = StructuralUsage.Bearing });
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var concrete = new BeamType("Concrete 300 x 500") { Material = BeamMaterial.Concrete, Width = 300, Depth = 500 };
        document.AddType(concrete);
        document.Add(new Beam { TypeId = concrete.Id, LevelId = first, Start = new Point2D(0, 0), End = new Point2D(6000, 0) });
        document.Add(new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = first, TopLevelId = level.Id, StructuralUsage = StructuralUsage.Bearing });
        var slab = new FloorType("Slab 200", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal)).Id, 200)));
        document.AddType(slab);
        var floor = new Floor { TypeId = slab.Id, LevelId = first };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 6000), new Point2D(0, 6000) });
        document.Add(floor);
        var (_, _, second) = Analysed(document);
        var joint = JointDesign.Of(document, second).Single(each => each.Kind == "Beam under a wall");
        Assert.Contains(joint.Checks, check => check.Name == "Bending" && check.Note.Contains("mm²"));
        Assert.Contains(joint.Checks, check => check.Name == "Shear" && check.Note.Contains("links"));
    }
}
