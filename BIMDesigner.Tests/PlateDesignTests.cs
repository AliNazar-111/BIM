using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Concrete floors and rafts designed from their plate moments, and footings and piles checked against what the analysis
/// puts on them (round 32): Wood and Armer's equations, Tables 7.2N and 7.3N, punching by BS EN 1992-1-1 and
/// ACI 318-19 against worked figures, and whole floors, rafts and footings.
/// </summary>
public class PlateDesignTests
{
    private const double Psi = 0.00689476;

    [Fact]
    public void WoodAndArmersEquationsShareOutTheTwist()
    {
        // Bending alone: no change. Sagging goes to the bottom, hogging to the top.
        Assert.Equal((10.0, 5.0, 0.0, 0.0), PlateDesign.WoodArmer(10, 5, 0));
        Assert.Equal((0.0, 0.0, 10.0, 5.0), PlateDesign.WoodArmer(-10, -5, 0));

        // Twist alone asks the same both faces both ways.
        Assert.Equal((5.0, 5.0, 5.0, 5.0), PlateDesign.WoodArmer(0, 0, 5));
        Assert.Equal((5.0, 5.0, 5.0, 5.0), PlateDesign.WoodArmer(0, 0, -5));

        // Where a way needs no steel in a face, all the twist goes to the other: top y would be 5 positive, so none, and top x takes mxy²/|my|.
        var (bottomX, bottomY, topX, topY) = PlateDesign.WoodArmer(-2, 10, 5);
        Assert.Equal((3.0, 15.0), (bottomX, bottomY));
        Assert.Equal(0, topY);
        Assert.Equal(4.5, topX, 6);
    }

    [Theory]
    [InlineData(160, 32, 300)]
    [InlineData(200, 25, 250)]
    [InlineData(240, 16, 200)]
    [InlineData(280, 12, 150)]
    [InlineData(320, 10, 100)]
    [InlineData(360, 8, 50)]
    public void CrackLimitsAreTables7Point2NAnd7Point3N(double stress, double bar, double spacing) =>
        Assert.Equal((bar, spacing), PlateDesign.CrackLimits(stress));

    [Fact]
    public void CrackControlTakesEitherTable()
    {
        Assert.Equal((20.5, 225.0), PlateDesign.CrackLimits(220));

        // At 240 N/mm²: a 12 bar passes on size though at 250 centres; a 20 at 250 fails both.
        Assert.Equal(12.0 / 16, PlateDesign.CrackShare(false, 240, 12, 250, 40), 6);
        Assert.True(PlateDesign.CrackShare(false, 240, 20, 250, 40) > 1);

        // Past the end of Table 7.3N only the bar's size can do it.
        Assert.Equal(6.0 / 6, PlateDesign.CrackShare(false, 400, 6, 150, 40), 6);

        // ACI 24.3.2: 15 in less 2.5 cc, no more than 12 in, at fs 40 ksi.
        Assert.Equal(200 / (15 * 25.4 - 2.5 * 40), PlateDesign.CrackShare(true, 0, 16, 200, 40), 6);
    }

    [Fact]
    public void PunchingIsWorkedOutByTheEurocode()
    {
        // 300 square, d 200, C30/37, 0.5% steel, 400 kN inside a slab: u1 3,713 mm, vEd 0.619; k 2.0, vRd,c 0.592.
        var inner = PlateDesign.PunchingEurocode(400, 300, 300, 200, 0.005, 0, 30, 180).ToList();
        Assert.Equal(1.15 * 400000 / ((1200 + 4 * Math.PI * 200) * 200), inner[0].Demand, 6);
        Assert.Equal(0.12 * 2 * Math.Cbrt(15), inner[0].Capacity, 6);

        // At the face: u0 the column's perimeter, against 0.4 ν fcd.
        Assert.Equal(1.15 * 400000 / (1200.0 * 200), inner[1].Demand, 6);
        Assert.Equal(0.4 * 0.6 * 0.88 * 20, inner[1].Capacity, 6);

        // At an edge and a corner, less perimeter and more β.
        var edge = PlateDesign.PunchingEurocode(400, 300, 300, 200, 0.005, 1, 30, 180).First();
        Assert.Equal(1.4 * 400000 / ((900 + 2 * Math.PI * 200) * 200), edge.Demand, 6);
        var corner = PlateDesign.PunchingEurocode(400, 300, 300, 200, 0.005, 2, 30, 180).First();
        Assert.Equal(1.5 * 400000 / ((600 + Math.PI * 200) * 200), corner.Demand, 6);

        // A little steel: the least, 0.035 k^1.5 √fck.
        var bare = PlateDesign.PunchingEurocode(400, 300, 300, 200, 0.0001, 0, 30, 180).First();
        Assert.Equal(0.035 * Math.Pow(2, 1.5) * Math.Sqrt(30), bare.Capacity, 6);
    }

    [Fact]
    public void PunchingIsWorkedOutByAci()
    {
        // 300 square, d 200, 4,000 psi: bo 2,000 mm, vc 4√f'c, φ 0.75.
        var check = PlateDesign.PunchingAci(400, 300, 300, 200, 0, 4000 * Psi).Single();
        Assert.Equal(400000 / (2000.0 * 200), check.Demand, 6);
        Assert.Equal(0.75 * 4 * Math.Sqrt(4000) * Psi, check.Capacity, 6);

        // A long column: 2 + 4/β governs, β 4 here.
        var wall = PlateDesign.PunchingAci(400, 1200, 300, 200, 0, 4000 * Psi).Single();
        Assert.Equal(0.75 * 3 * Math.Sqrt(4000) * Psi, wall.Capacity, 6);

        // Deep slabs lose some: λs below 1 past 10 in.
        var deep = PlateDesign.PunchingAci(400, 300, 300, 500, 0, 4000 * Psi).Single();
        Assert.Equal(0.75 * 4 * Math.Sqrt(2 / (1 + 500 / 25.4 / 10)) * Math.Sqrt(4000) * Psi, deep.Capacity, 6);
    }

    [Fact]
    public void PunchingLinksComeToThePublishedDesign()
    {
        // Structville's worked example: a 450 by 230 edge column, 250 slab, d 213, C30, 0.582%, 400 kN. β 1.4, u1 2,468, vEd 1.065, vRd,c 0.613.
        var check = PlateDesign.PunchingEurocode(400, 450, 230, 213, 0.00582, 1, 30, 250).First();
        Assert.InRange(check.Demand, 1.06, 1.07);
        Assert.Equal(2 * 0.613, check.Capacity, 2);
        Assert.StartsWith("needs links", check.Note, StringComparison.Ordinal);

        // sr 150, fywd,ef 303: 492 mm² a perimeter, as 7 H10; uout 4,289; reinforced out to 1,005 less 1.5 d, 686 from its face.
        var links = PlateDesign.LinksEurocode(400, check.Demand, 0.613, 1.4, 2468, 213, 2 * 450 + 230, Math.PI, 30);
        Assert.Equal(150, links.Spacing);
        Assert.InRange(links.Area, 490, 496);
        Assert.Equal((7, 10.0), (links.Legs, links.Leg));
        Assert.InRange(links.Outer, 4280, 4300);
        Assert.True(links.Last >= 686, $"the last perimeter is {links.Last:0}");
        Assert.True(links.Last - links.Spacing < 686, "and one fewer would not reach");
    }

    [Fact]
    public void ALinkLegIsNoSmallerThanExpression9Point11()
    {
        // At d 600, sr 450 and st 900 ask 0.053 × 450 × 900 × 5.48 / 500 = 235 mm² of a leg: an H20, not an H10.
        var links = PlateDesign.LinksEurocode(2000, 1.0, 0.5, 1.15, 12000, 600, 3000, 2 * Math.PI, 30);
        Assert.Equal(20, links.Leg);
    }

    [Fact]
    public void AThinSlabCannotTakeLinksAndNoSlabGoesPastTwiceWhatItCarries()
    {
        // Under 200 mm it stays at what the concrete carries, and says why.
        var thin = PlateDesign.PunchingEurocode(400, 300, 300, 150, 0.005, 0, 30, 190).First();
        Assert.False(thin.Ratio <= 1);
        Assert.Contains("200 mm", thin.Note);

        // Over twice vRd,c no links are enough.
        Assert.True(PlateDesign.PunchingEurocode(1500, 300, 300, 200, 0.005, 0, 30, 250).First().Ratio > 1);
    }

    [Fact]
    public void StirrupsAreWorkedOutByAci()
    {
        // 300 square, d 200, 4,000 psi, 700 kN: bo 2,000, vu 1.75. φ vc 1.31 is short; with stirrups vc 2√f'c 0.872 and up to φ 6√f'c 1.96.
        var root = Math.Sqrt(4000) * Psi;
        var check = PlateDesign.PunchingAci(700, 300, 300, 200, 0, 4000 * Psi).Single();
        Assert.Equal(1.75, check.Demand, 6);
        Assert.Equal(0.75 * 6 * root, check.Capacity, 6);

        // Av = (1.75 / 0.75 - 2 √f'c) bo s / fy, s 3 in: 3 in is under d/2.
        Assert.Contains($"{(1.75 / 0.75 - 2 * root) * 2000 * 76.2 / (60000 * Psi):0} mm²", check.Note);
        Assert.Contains("3 in apart", check.Note);

        // Under 6 in of depth there is no room for them.
        Assert.Contains("6 in", PlateDesign.PunchingAci(500, 300, 300, 140, 0, 4000 * Psi).Single().Note);
    }

    [Theory]
    [InlineData(0.005, 1.0, 20)]
    [InlineData(0.015, 1.0, 14)]
    [InlineData(0.005, 1.5, 30)]
    [InlineData(0.015, 1.5, 20)]
    [InlineData(0.005, 1.2, 24)]
    [InlineData(0.015, 1.2, 17)]
    [InlineData(0.005, 0.4, 8)]
    [InlineData(0.015, 0.4, 6)]
    public void SpanToDepthIsTable7Point4N(double rho, double k, double table) =>
        Assert.InRange(PlateDesign.SpanDepthLimit(k, rho, 30, 5000, 1, false), table - 1, table + 1);

    [Fact]
    public void SpanToDepthFollowsLongSpansAndTheSteelGiven()
    {
        var plain = PlateDesign.SpanDepthLimit(1, 0.005, 30, 7000, 1, false);
        Assert.Equal(plain * 7 / 10, PlateDesign.SpanDepthLimit(1, 0.005, 30, 10000, 1, false), 6);
        Assert.Equal(plain, PlateDesign.SpanDepthLimit(1, 0.005, 30, 8000, 1, true), 6);
        Assert.Equal(plain * 1.2, PlateDesign.SpanDepthLimit(1, 0.005, 30, 7000, 1.2, false), 6);
        Assert.Equal(plain * 1.5, PlateDesign.SpanDepthLimit(1, 0.005, 30, 7000, 3, false), 6);
    }

    [Fact]
    public void TheLeastThicknessIsTheTablesOfAci()
    {
        Assert.Equal(6000 * 1.1 / (36 + 9 * 4.0 / 3), PlateDesign.ThicknessAci(4500, 6000, false, false, false).Thickness, 6);
        Assert.Equal(90, PlateDesign.ThicknessAci(2000, 2000, false, false, false).Thickness, 6);
        Assert.Equal(200, PlateDesign.ThicknessAci(6000, 6000, true, false, false).Thickness, 6);
        Assert.Equal(125, PlateDesign.ThicknessAci(3000, 3000, true, false, false).Thickness, 6);
        Assert.Equal(150, PlateDesign.ThicknessAci(3000, 12000, false, false, false).Thickness, 6);
        Assert.Equal(125, PlateDesign.ThicknessAci(3000, 12000, false, true, false).Thickness, 6);
        Assert.Equal(150, PlateDesign.ThicknessAci(1500, 1500, false, false, true).Thickness, 6);
    }

    /// <summary>Points along lines a plate is held by, 600 apart as the mesh has them.</summary>
    private static List<(Point2D At, bool Column)> Along(params (double X0, double Y0, double X1, double Y1)[] lines)
    {
        var held = new List<(Point2D, bool)>();
        foreach (var (x0, y0, x1, y1) in lines)
        {
            var count = (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / 600);
            for (var i = 0; i <= count; i++) held.Add((new Point2D(x0 + (x1 - x0) * i / count, y0 + (y1 - y0) * i / count), false));
        }

        return held;
    }

    private static List<Point2D> Grid(double wide, double deep)
    {
        var points = new List<Point2D>();
        for (var x = 100.0; x < wide; x += 200)
            for (var y = 100.0; y < deep; y += 200)
                points.Add(new Point2D(x, y));
        return points;
    }

    [Fact]
    public void ASlabsSpansComeFromWhatHoldsIt()
    {
        var outline = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = Along((0, 0, 10000, 0), (10000, 0, 10000, 8000), (10000, 8000, 0, 8000), (0, 8000, 0, 0));

        // Walls all round: both ways, not continuous, not flat.
        var four = PlateDesign.SpansOf(walls, outline, Grid(10000, 8000))!.Value;
        Assert.Equal((8000.0, 10000.0), (four.Short, four.Long));
        Assert.False(four.Flat || four.Continuous || four.Cantilever);

        // A wall across it halves the long way and makes it continuous.
        var divided = PlateDesign.SpansOf(walls.Concat(Along((5000, 0, 5000, 8000))).ToList(), outline, Grid(10000, 8000))!.Value;
        Assert.Equal((5000.0, 8000.0), (divided.Short, divided.Long));
        Assert.True(divided.Continuous);

        // Two walls only: one way.
        var two = PlateDesign.SpansOf(Along((0, 0, 10000, 0), (0, 8000, 10000, 8000)), outline, Grid(10000, 8000))!.Value;
        Assert.Equal((8000.0, 8000.0), (two.Short, two.Long));

        // One wall along it: a cantilever of its whole depth.
        var cantilever = PlateDesign.SpansOf(Along((0, 0, 10000, 0)), outline, Grid(10000, 8000))!.Value;
        Assert.True(cantilever.Cantilever);
        Assert.InRange(cantilever.Short, 7800, 8000);

        // A column in the middle: flat, on the longer way from it to what is next, and not the 10 m past it.
        var post = walls.Append((new Point2D(5000, 4000), true)).ToList();
        var flat = PlateDesign.SpansOf(post, outline, Grid(10000, 8000))!.Value;
        Assert.True(flat.Flat);
        Assert.Equal(5000, flat.Long, 0);
    }

    private static BimDocument Box(double wide, double deep, double middleWallAt = 0)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(wide, 0), new Point2D(wide, deep), new Point2D(0, deep) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000, StructuralUsage = StructuralUsage.Bearing });
        if (middleWallAt > 0)
            document.Add(new Wall { Start = new Point2D(middleWallAt, 0), End = new Point2D(middleWallAt, deep), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000, StructuralUsage = StructuralUsage.Bearing });
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var concrete = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var floor = new Floor { TypeId = concrete.Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(wide + 165, -165), new Point2D(wide + 165, deep + 165), new Point2D(-165, deep + 165) });
        document.Add(floor);
        return document;
    }

    private static (AnalyticalPanel Panel, double Q, IReadOnlyList<PlateShear> Shears, Point2D Corner) ShearOf(BimDocument document)
    {
        var (analysis, second) = Analysed(document);
        var panel = analysis.Model.Panels.Single(each => each.Kind == AnalyticalPanelKind.Floor);
        var (combination, shears) = second.PlateShears(panel).First(entry => entry.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var q = combination.Terms.Sum(term => term.Factor * (term.Case.Nature == LoadNature.SelfWeight
            ? panel.SelfWeight
            : -analysis.Loading.SurfaceLoads.Where(load => load.Panel == panel && load.Case == term.Case).Sum(load => load.Force.Z)));
        var outline = panel.FlatOutline;
        return (panel, q, shears, new Point2D(outline.Min(p => p.X) + 165, outline.Min(p => p.Y) + 165));
    }

    [Fact]
    public void ASlabSpanningOneWayHasTheShearStaticsGiveIt()
    {
        // 14 by 3 m: it spans its 3 m way, and q L / 2 reaches each long wall. What is read is that, less the load on the half row of triangles next to the wall.
        var (_, q, shears, corner) = ShearOf(Box(14000, 3000));
        var alongWalls = shears.Where(each => Math.Abs(each.AcrossV) > 0.9 && Math.Abs(each.At.X - corner.X - 7000) < 3000).Select(each => each.Shear).ToList();
        Assert.NotEmpty(alongWalls);
        Assert.InRange(alongWalls.Average(), 0.75 * q * 1.5, q * 1.5);

        // The same all along both walls, as a slab spanning one way is: what differs is the two walls' meshes.
        Assert.InRange(alongWalls.Max() / alongWalls.Min(), 1, 1.1);
    }

    [Fact]
    public void ShearAtAnInnerWallIsNotLostAcrossIt()
    {
        // Two 5 m spans: 0.625 q L on each side of the wall between them spanning one way, somewhat under as these span two ways too; 0.375 q L at the outer walls.
        var (_, q, shears, corner) = ShearOf(Box(10000, 8000, 5000));
        double Worst(Func<PlateShear, bool> side) => shears.Where(side).Max(each => each.Shear);
        bool Beside(PlateShear each) => Math.Abs(each.At.X - corner.X - 5000) < 100 && Math.Abs(each.At.Y - corner.Y - 4000) < 2000;
        var (left, right) = (Worst(each => Beside(each) && each.AcrossU < -0.5), Worst(each => Beside(each) && each.AcrossU > 0.5));
        Assert.InRange(left, 0.7 * 0.625 * q * 5, 0.625 * q * 5);
        Assert.InRange(right, 0.7 * 0.625 * q * 5, 0.625 * q * 5);

        // The two sides are one wall's mirror image, so what is read must not depend on the mesh by much.
        Assert.InRange(left / right, 0.85, 1.15);
    }

    [Fact]
    public void ACornersTwistIsNotTakenForShear()
    {
        // 8 m square, Timoshenko's edge reaction 0.42 q a at most, and a point force at each corner that would read half as much again if it were counted.
        var (_, q, shears, _) = ShearOf(Box(8000, 8000));
        Assert.InRange(shears.Max(each => each.Shear), 0.34 * q * 8, 0.46 * q * 8);
    }

    [Fact]
    public void AFloorHasItsShearAndItsSpanChecked()
    {
        var document = Walled();
        FirstFloor(document);
        var (_, second) = Analysed(document);
        var check = PlateDesign.Of(document, second).Single();

        var shear = check.Checks.Single(each => each.Name == "One-way shear");
        Assert.InRange(shear.Demand, 5, 40);
        Assert.Equal(PlateDesign.OneWayEurocode(58, 0.0013, 30) * 58, shear.Capacity, 3);

        // 8.16 m, the walls taken at its edge, on a d of 58: far past anything Table 7.4N allows, which a slab's uncracked deflection alone did not show.
        var span = check.Checks.Single(each => each.Name == "Span to depth");
        Assert.Equal(8160.0 / 58, span.Demand, 3);
        Assert.True(span.Ratio > 2);
    }

    [Fact]
    public void WhatALinkedColumnWantsIsSaidOfTheSlabItIn()
    {
        // A raft under a column hard enough on it to want links, and the model not holding them.
        var check = PlateDesign.PunchingEurocode(400, 300, 300, 150, 0.003, 0, 30, 200).First();
        Assert.StartsWith("needs links", check.Note, StringComparison.Ordinal);
        Assert.True(check.Ratio <= 1);
    }

    private static BimDocument Walled()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        return document;
    }

    private static Floor FirstFloor(BimDocument document)
    {
        var concrete = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var floor = new Floor { TypeId = concrete.Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 8165), new Point2D(-165, 8165) });
        document.Add(floor);
        return floor;
    }

    private static (StructuralAnalysis Analysis, SecondOrderAnalysis Second) Analysed(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        return (analysis, SecondOrderAnalysis.Run(document, analysis));
    }

    [Fact]
    public void AFloorOnItsWallsWantsItsSteelAtTheBottomAndMostAcrossItsShortWay()
    {
        var document = Walled();
        FirstFloor(document);
        var (_, second) = Analysed(document);
        var check = PlateDesign.Of(document, second).Single();
        Assert.Equal(AnalyticalPanelKind.Floor, check.Panel.Kind);
        Assert.Contains("BS EN 1992-1-1", check.By);

        // A 10 by 8 m slab sags, spanning most its 8 m way: y steel more than x, bottom more than top.
        double Moment(string name) => check.Checks.Single(each => each.Name == name).Demand;
        Assert.True(Moment("Bottom steel, y way") > Moment("Bottom steel, x way"));
        Assert.True(Moment("Bottom steel, y way") > Moment("Top steel, y way"));
        Assert.Contains(check.Checks, each => each.Name == "Deflection");

        // 100 mm over 8 m is too thin for any steel, and is said to be: not a small amount of steel.
        Assert.True(double.IsInfinity(check.BottomY));
        Assert.Contains("make it deeper", check.Checks.Single(each => each.Name == "Bottom steel, y way").Note);
        Assert.False(check.Enough);
    }

    [Fact]
    public void AFloorsMomentComesToTheSizeThePlateTablesGive()
    {
        var document = Walled();
        FirstFloor(document);
        var (analysis, second) = Analysed(document);
        var panel = analysis.Model.Panels.Single(each => each.Kind == AnalyticalPanelKind.Floor);

        // Its load under a combination, its own weight and what is on it.
        var (combination, stretches) = second.PlateStretches(panel).First(entry => entry.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var q = combination.Terms.Sum(term => term.Factor * (term.Case.Nature == LoadNature.SelfWeight
            ? panel.SelfWeight
            : -analysis.Loading.SurfaceLoads.Where(load => load.Panel == panel && load.Case == term.Case).Sum(load => load.Force.Z)));
        var outline = panel.FlatOutline;
        var (wide, across) = (outline.Max(p => p.X) - outline.Min(p => p.X), outline.Max(p => p.Y) - outline.Min(p => p.Y));
        Assert.InRange(wide / across, 1.2, 1.3);

        // Timoshenko's table, simply supported at b/a 1.25: My about 0.066 q a² across the short way. The walls hold its edges, so it comes in under, not over: about two thirds, block walls
        // at E = 1000 fk with Table NA.4's K of 0.75 holding them more than at 0.55.
        var table = 0.066 * q * Math.Pow(across / 1000, 2);
        var moment = stretches.Max(stretch => stretch.MomentV) / 1000;
        Assert.True(moment >= 0.6 * table && moment <= 1.05 * table, $"{moment / table:0.000} of the table");
    }

    [Fact]
    public void AColumnOnAFloorIsCheckedForPunching()
    {
        var document = Walled();
        FirstFloor(document);
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        document.Add(Footings.Fit(document, column));
        var (_, second) = Analysed(document);
        var check = PlateDesign.Of(document, second).Single();
        var punching = check.Checks.Single(each => each.Name.StartsWith("Punching at 2d", StringComparison.Ordinal));
        Assert.Contains("C1", punching.Name);
        Assert.Contains("β 1.15", punching.Note);
        Assert.True(punching.Demand > 0);
    }

    [Fact]
    public void ARaftIsCheckedAgainstItsOwnBars()
    {
        var document = EaveTests.House(hip: false).Document;
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        document.Add(raft);
        var (_, second) = Analysed(document);
        var check = PlateDesign.Of(document, second).Single();
        Assert.Equal(AnalyticalPanelKind.Raft, check.Panel.Kind);

        // A393 both ways, each face.
        var bottom = check.Checks.Single(each => each.Name == "Bottom steel, x way");
        Assert.Equal(393, bottom.Capacity);
        Assert.Equal("mm²/m", bottom.Unit);
        Assert.Contains(check.Checks, each => each.Name == "Cracks, bottom x");

        // The ground holds it up: no deflection limit for a plate on it.
        Assert.DoesNotContain(check.Checks, each => each.Name == "Deflection");
    }

    [Fact]
    public void AFootingsPressureIsWhatComesDownOverItsArea()
    {
        var document = Walled();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var (analysis, _) = Analysed(document);
        var checks = FoundationDesign.Of(document, analysis);
        Assert.Equal(4, checks.Count);
        foreach (var check in checks)
        {
            var bearing = check.Checks.Single(each => each.Name == "Bearing");
            Assert.Equal(75, bearing.Capacity);
            Assert.Equal("BS 8004:1986 Table 1", bearing.Clause);

            // From what the analysis puts on it at its heaviest metre, and its own weight: no less than over its whole area. A slab spanning both ways comes down harder at the middle of a wall, here about 1.3 times its average.
            var strip = (WallFooting)check.Foundation;
            var area = Footings.RunOutlines(document, strip).Sum(run => Polygon2D.Area(run.Outline)) / 1e6;
            var down = analysis.Model.Supports.Where(support => support.Foundation == strip).Sum(support => analysis.ReactionOf(support, analysis.Total).Fz);
            var thickness = document.FindType<FootingType>(strip.TypeId)!.Thickness;
            var even = (down + area * thickness / 1000 * 24) / area;
            Assert.InRange(bearing.Demand, even - 1e-6, even * 1.4);
        }

        // Where the ground has no presumed value its bearing is said, with the pressure, not judged; its concrete still is.
        document.ProjectInformation.Ground = GroundType.SoftClay;
        var soft = FoundationDesign.Of(document, analysis);
        Assert.All(soft, check =>
        {
            Assert.DoesNotContain(check.Checks, each => each.Name == "Bearing");
            Assert.Contains("site investigation", check.Limits);
        });
    }

    [Fact]
    public void APileIsCheckedAgainstWhatItCanCarryInClayAndLeftToItsEngineerElsewhere()
    {
        var document = EaveTests.House(hip: false).Document;
        document.ProjectInformation.Ground = GroundType.FirmClay;
        foreach (var wall in document.Walls.ToList())
        {
            wall.StructuralUsage = StructuralUsage.Bearing;
            var (beam, piles) = Piles.Fit(document, wall, 300, 3000);
            document.Add(beam);
            foreach (var pile in piles) document.Add(pile);
        }

        var (analysis, _) = Analysed(document);
        var checks = FoundationDesign.Of(document, analysis);
        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.Equal("Load on the pile", check.Checks.Single().Name));

        document.ProjectInformation.Ground = GroundType.Gravel;
        Assert.All(FoundationDesign.Of(document, analysis), check =>
        {
            Assert.Empty(check.Checks);
            Assert.Contains("piling engineer", check.Limits);
        });
    }

    [Fact]
    public void AGroundBeamsCracksAreJudgedFromItsBars()
    {
        var document = EaveTests.House(hip: false).Document;
        document.ProjectInformation.Ground = GroundType.FirmClay;
        foreach (var wall in document.Walls.ToList())
        {
            wall.StructuralUsage = StructuralUsage.Bearing;
            var (beam, piles) = Piles.Fit(document, wall, 300, 3000);
            document.Add(beam);
            foreach (var pile in piles) document.Add(pile);
        }

        var (_, second) = Analysed(document);
        var checks = MemberStrength.Of(document, second.Analysis, second);
        Assert.All(checks, check => Assert.Contains(check.Checks, each => each.Name.StartsWith("Cracks", StringComparison.Ordinal)));
    }
}
