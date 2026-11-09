using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// The loads finished: snow drifted on pitched roofs by BS EN 1991-1-3 5.3.3 and unbalanced by ASCE 7-16 7.6.1, the wind on side walls, roof edges, along a ridge, under an
/// overhang and on flat roofs by BS EN 1991-1-4 Tables 7.1, 7.2, 7.4a and 7.4b and ASCE 7-16 Figure 27.3-1, and imposed loads reduced by the area and floors a member carries,
/// BS EN 1991-1-1 6.3.1.2 with the UK National Annex or ASCE 7-16 4.7.
/// </summary>
public class LoadArrangementTests
{
    /// <summary>A house of bearing walls 3 m high, drawn anticlockwise from the origin, under a gable roof whose slopes fall to its first and third walls - or a flat roof - a strip footing under each wall.</summary>
    private static (BimDocument Document, List<Wall> Walls, Roof Roof) House(double width = 10000, double depth = 8000, double pitch = 35, bool us = false, string? roofType = null, double overhang = 450, bool flat = false)
    {
        var document = BimDocument.CreateDefault();
        if (us)
        {
            document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
            document.ProjectInformation.GroundSnowLoad = 30;
        }

        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        var walls = new List<Wall>();
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000, StructuralUsage = StructuralUsage.Bearing };
            document.Add(wall);
            walls.Add(wall);
        }

        var out_ = wallType.Structure.TotalWidth / 2 + overhang;
        var type = document.TypesOf<RoofType>().First(t => t.Name == (roofType ?? (flat ? "Roof - Warm Flat 320mm" : "Roof - Slate on Timber Rafters 150mm")));
        var roof = new Roof { TypeId = type.Id, LevelId = level, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(-out_, -out_), new Point2D(width + out_, -out_), new Point2D(width + out_, depth + out_), new Point2D(-out_, depth + out_) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = !flat && i % 2 == 0, SlopeDegrees = pitch, WallId = walls[i].Id, OnLeftOfWall = false, Overhang = overhang }));
        document.Add(roof);
        foreach (var wall in walls) document.Add(Footings.Fit(document, wall));
        return (document, walls, roof);
    }

    private static StructuralLoading Loaded(BimDocument document) => StructuralLoading.Of(document, AnalyticalModel.Of(document));

    /// <summary>What a case puts down on the top of a wall, kN.</summary>
    private static double On(StructuralLoading loading, string name, Wall wall) =>
        -loading.LineLoads.Where(load => load.Case.Name == name && load.Panel?.Element == wall).Sum(load => load.Force.Z * load.Length / 1000);

    /// <summary>How deep a slope is on plan for each metre of the wall under its eave, m: its snow over the snow a square metre.</summary>
    private static double Deep(BimDocument document, StructuralLoading loading, Wall wall, double pitch)
    {
        var snow = loading.LineLoads.Where(load => load.Case.Name == "S1" && load.Panel?.Element == wall).ToList();
        return -snow.Sum(load => load.Force.Z * load.Length / 1000) / (snow.Sum(load => load.Length) / 1000) / StructuralLoading.RoofSnow(document, pitch);
    }

    [Fact]
    public void AGableRoofsSnowLiesHalfOnTheSideTheWindComesOver()
    {
        // BS EN 1991-1-3 5.3.3 and Figure 5.3: case (ii) 0.5 μ1 on one slope and μ1 on the other, case (iii) the other way round - each an arrangement of its own, taken instead of the snow lying evenly.
        var (document, walls, _) = House();
        var loading = Loaded(document);
        Assert.Equal(new[] { "G1", "G2", "Q2", "S1", "S2", "S3", "W1", "W2", "W3", "W4", "W5", "W6" }, loading.Cases.Select(each => each.Name));
        var (south, north) = (walls[0], walls[2]);
        var even = On(loading, "S1", south);
        Assert.True(even > 1);
        Assert.Equal(even, On(loading, "S1", north), 6);

        var (s2South, s2North) = (On(loading, "S2", south) / even, On(loading, "S2", north) / even);
        Assert.Equal(0.5, Math.Min(s2South, s2North), 6);
        Assert.Equal(1, Math.Max(s2South, s2North), 6);
        Assert.Equal(s2South, On(loading, "S3", north) / even, 6);
        Assert.Equal(s2North, On(loading, "S3", south) / even, 6);
        var (s1, s2) = (loading.Cases.Single(each => each.Name == "S1"), loading.Cases.Single(each => each.Name == "S2"));
        Assert.True(s2.Drifted && !s1.Drifted);
        Assert.Equal(0.75 * loading.Total(s1), loading.Total(s2), 4);

        // Each leads and goes with the wind as the even snow does, and never with it.
        var said = loading.Combinations.Select(each => each.Said).ToList();
        Assert.Contains("1.25 G1 + 1.25 G2 + 1.5 S2 + 0.75 W1", said);
        Assert.Contains("1.25 G1 + 1.25 G2 + 1.5 W3 + 0.75 S3", said);
        Assert.DoesNotContain(loading.Combinations, each => each.Terms.Any(term => term.Case == s1) && each.Terms.Any(term => term.Case.Drifted));

        // What a beam deflects under leaves them out: they are the snow again, laid another way.
        var analysis = StructuralAnalysis.Run(document, AnalyticalModel.Of(document), loading);
        Assert.DoesNotContain(analysis.Variable, each => each.Item1.Drifted);
        Assert.DoesNotContain(analysis.Total, each => each.Item1.Drifted);
        Assert.Contains(analysis.Variable, each => each.Item1 == s1);
    }

    [Fact]
    public void AFlatRoofAloneHasNoDriftedSnow()
    {
        // With nothing to drift it, S2 and S3 would be S1 again, and are left out.
        var (document, _, _) = House(flat: true, overhang: 0);
        Assert.DoesNotContain(Loaded(document).Cases, each => each.Drifted);

        // A US roof steeper than 7 on 12 need not be unbalanced (ASCE 7-16 7.6.1): 35° is 8.4 on 12.
        var (steep, _, _) = House(us: true);
        Assert.DoesNotContain(Loaded(steep).Cases, each => each.Drifted);
    }

    [Theory]
    [InlineData(8000.0)]
    [InlineData(16000.0)]
    public void AUsGableRoofIsUnbalanced(double depth)
    {
        // ASCE 7-16 7.6.1 at 20°, 4.4 on 12, pg 30 psf and ps 0.7 x 30 = 21 psf: eave to ridge W of 20 ft or less, nothing windward and Is pg leeward; past it, 0.3 ps windward and ps leeward
        // with the surcharge hd γ / √S over 8 hd √S / 3, 8 hd² γ / 3 a foot of eave, hd by (7.6-1) from W, γ = 0.13 pg + 14.
        var (document, walls, roof) = House(depth: depth, pitch: 20, us: true);
        var loading = Loaded(document);
        var ps = StructuralLoading.RoofSnowPsf(document, 20);
        Assert.Equal(21, ps, 6);
        var (south, north) = (walls[0], walls[2]);

        // W on plan from the eave to the ridge, the overhang in it: half the depth and what the roof stands out past the wall line.
        var w = depth / 2000 - roof.Boundary.Min(p => p.Y) / 1000;

        // S2 one way, S3 the other.
        var (s2South, s2North) = (On(loading, "S2", south) / On(loading, "S1", south), On(loading, "S2", north) / On(loading, "S1", north));
        Assert.Equal(s2South, On(loading, "S3", north) / On(loading, "S1", north), 9);
        Assert.Equal(s2North, On(loading, "S3", south) / On(loading, "S1", south), 9);
        var (windward, leeward) = (Math.Min(s2South, s2North), Math.Max(s2South, s2North));
        var feet = w / 0.3048;
        if (feet <= 20)
        {
            Assert.Equal(0, windward, 6);
            Assert.Equal(30 / ps, leeward, 4);
        }
        else
        {
            // A metre of eave carries W of plan and the surcharge over it, wherever along the wall it is spread.
            var hd = 0.43 * Math.Cbrt(feet) * Math.Pow(30 + 10, 0.25) - 1.5;
            var gamma = 0.13 * 30 + 14;
            var surcharge = 8 * hd * hd * gamma / 3 * 4.44822 / 0.3048 / 1000;
            Assert.InRange(hd, 1.6, 1.8);
            Assert.Equal(0.3, windward, 6);
            Assert.Equal(1 + surcharge / (w * ps * Loads.Psf), leeward, 4);
        }

        Assert.Contains(loading.Combinations, each => each.Said == "1.2 D1 + 1.2 D2 + 1.6 S2 + 0.5 W1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SideWallsAreDrawnOffAndTheWindwardWallIsTheOneFacingIt(bool us)
    {
        // The wind from the west along the house's 10 m side walls. BS EN 1991-1-4 Table 7.1: e the lesser of the 8 m across it and 2h = 6 m, A -1.2 to e/5, B -0.8 to e, C -0.5 past it,
        // averaged over each wall's length; ASCE 7-16 Figure 27.3-1's -0.7 with G 0.85. The walls are drawn anticlockwise, their exterior sides in: each is drawn off outward all the same.
        var (document, walls, _) = House(us: us);
        Assert.False(Wind.FromSite(document));
        var loading = Loaded(document);
        var wind = loading.Cases.Single(each => each.Name == "W1");
        var q = document.ProjectInformation.WindPressure;
        var h = Wind.Height(document) / 1000;
        Assert.Equal(3, h, 6);
        var e = Math.Min(8, 2 * h);
        var average = (-1.2 * e / 5 - 0.8 * (e - e / 5) - 0.5 * (10 - e)) / 10;
        var suction = us ? 0.7 * 0.85 * q : -average * q;
        foreach (var (wall, outward) in new[] { (walls[0], -1.0), (walls[2], 1.0) })
        {
            var drawn = loading.SurfaceLoads.Where(load => load.Case == wind && load.Panel.Element == wall).ToList();
            Assert.NotEmpty(drawn);
            Assert.All(drawn, load =>
            {
                Assert.Equal(0, load.Force.X, 9);
                Assert.InRange(load.Force.Y * outward, 0.98 * suction, 1.02 * suction);
            });
        }

        // The west wall is pressed and the east one drawn off - by zones D and E for h/d 0.3, by 0.85, or 0.8 and, for L/B 1.25, 0.45 with G - both toward +x.
        var west = loading.SurfaceLoads.Single(load => load.Case == wind && load.Panel.Element == walls[3]);
        var east = loading.SurfaceLoads.Single(load => load.Case == wind && load.Panel.Element == walls[1]);
        var (into, outOf) = us ? (0.85 * 0.8 * q, 0.85 * 0.45 * q) : (0.85 * (0.7 + 0.1 * (0.3 - 0.25) / 0.75) * q, 0.85 * (0.3 + 0.2 * (0.3 - 0.25) / 0.75) * q);
        Assert.Equal(into, west.Force.X, 4);
        Assert.Equal(outOf, east.Force.X, 4);
        Assert.Contains("into the wind", west.Said);

        // What pushes the building along: the end walls only, the side walls drawing off square to it.
        Assert.Equal((west.Force.X * west.Panel.Area + east.Force.X * east.Panel.Area) / 1e6, loading.Across(wind), 6);
    }

    [Fact]
    public void ARoofsEdgesAndOverhangTakeTheirOwnPressures()
    {
        // The wind from the south across the ridge at 35°. Table 7.4a: the strip e/10 deep along the windward eave, F and G +0.7, the rest of the slope H +0.467; along the leeward side
        // of the ridge J -0.433, the rest I -0.333; e the lesser of the 10 m across it and 2h. 7.2.1(2): the 450 overhang lifted from under at the wall's own zone D.
        var (document, walls, _) = House();
        var loading = Loaded(document);
        var wind = loading.Cases.Single(each => each.Name == "W3");
        var q = document.ProjectInformation.WindPressure;
        var e = Math.Min(10, 2 * Wind.Height(document) / 1000);
        var (south, north) = (walls[0], walls[2]);
        var (deepSouth, deepNorth) = (Deep(document, loading, south, 35), Deep(document, loading, north, 35));
        var (stripSouth, stripNorth) = (Math.Min(e / 10, deepSouth) / deepSouth, Math.Min(e / 10, deepNorth) / deepNorth);

        var windward = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == south && load.Said.StartsWith("windward", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(windward);
        Assert.All(windward, load => Assert.Equal(0.7 * stripSouth + (0.4 + 0.2 / 3) * (1 - stripSouth), -load.Force.Z / (q * deepSouth), 4));
        var leeward = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == north && load.Said.StartsWith("leeward", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(leeward);
        Assert.All(leeward, load => Assert.Equal(-(0.5 - 0.2 / 3) * stripNorth - (0.4 - 0.2 / 3) * (1 - stripNorth), -load.Force.Z / (q * deepNorth), 4));

        var ratio = 3.0 / 8;
        var zoneD = 0.7 + 0.1 * (ratio - 0.25) / 0.75;
        var lifted = loading.LineLoads.Where(load => load.Case == wind && load.Said.Contains("overhang", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(lifted);
        Assert.All(lifted, load =>
        {
            Assert.Same(south, load.Panel!.Element);
            Assert.Equal(zoneD * q * 0.45, load.Force.Z, 4);
        });
    }

    [Fact]
    public void AWindwardSlopeIsAlsoDrawnOffInACaseOfItsOwn()
    {
        // Table 7.4a gives a windward slope of 5° to 45° two values, pressing and drawing off. W3 has the first; W5 the second with all else as W3 has it - at 35° H -0.133 and F and G -0.333
        // in the strip e/10 deep along the eave. It lifts a roof, so it is taken where the weight helps the wind, and only there.
        var (document, walls, _) = House();
        var loading = Loaded(document);
        var (w3, w5) = (loading.Cases.Single(each => each.Name == "W3"), loading.Cases.Single(each => each.Name == "W5"));
        Assert.Equal(w3.Wind, w5.Wind);
        Assert.Contains("drawn off", w5.Description);
        var q = document.ProjectInformation.WindPressure;
        var e = Math.Min(10, 2 * Wind.Height(document) / 1000);
        var (south, north) = (walls[0], walls[2]);
        var deep = Deep(document, loading, south, 35);
        var strip = Math.Min(e / 10, deep) / deep;
        var drawn = loading.LineLoads.Where(load => load.Case == w5 && load.Panel?.Element == south && load.Said.StartsWith("windward slope, drawn off", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(drawn);
        Assert.All(drawn, load => Assert.Equal((-0.5 + 0.5 / 3) * strip + (-0.2 + 0.2 / 3) * (1 - strip), -load.Force.Z / (q * deep), 4));

        // Its walls, leeward slope and overhang as W3's.
        string Rest(LoadCase wind) => string.Join("|", loading.SurfaceLoads.Where(load => load.Case == wind).Select(load => $"{load.Panel.Name} {load.Force}")
            .Concat(loading.LineLoads.Where(load => load.Case == wind && !load.Said.StartsWith("windward slope", StringComparison.Ordinal)).Select(load => $"{load.Panel?.Name} {load.Start:0} {load.Force}")));
        Assert.Equal(Rest(w3), Rest(w5));
        Assert.Equal(new[] { "1 G1 + 1 G2 + 1.5 W5" }, loading.Combinations.Where(each => each.Terms.Any(term => term.Case == w5)).Select(each => each.Said));

        // Past 45° there is one value, and no such case.
        var (steep, _, _) = House(pitch: 50);
        Assert.DoesNotContain(Loaded(steep).Cases, each => each.Description.Contains("drawn off", StringComparison.Ordinal));
    }

    [Fact]
    public void AHippedRoofsFacesAreByTable75()
    {
        // The wind across the main ridge of a hipped roof at 35°: its windward face F +0.567 for e/4 from each end of the strip along its eave and G +0.7 between, H +0.467 on the rest; its leeward
        // face J -0.667 along the ridge and I -0.367 on the rest - Table 7.5's, where a duopitch's are +0.7, -0.433 and -0.333.
        var (document, _) = EaveTests.House(hip: true);
        var walls = document.Walls.ToList();
        foreach (var wall in walls)
        {
            wall.StructuralUsage = StructuralUsage.Bearing;
            document.Add(Footings.Fit(document, wall));
        }

        var loading = Loaded(document);
        var wind = loading.Cases.Single(each => each.Name == "W3");
        var q = document.ProjectInformation.WindPressure;
        var e = Math.Min(10, 2 * Wind.Height(document) / 1000);
        var (south, north) = (walls[0], walls[2]);
        var (deepSouth, deepNorth) = (Deep(document, loading, south, 35), Deep(document, loading, north, 35));
        var eave = loading.LineLoads.Where(load => load.Case.Name == "S1" && load.Panel?.Element == south).Sum(load => load.Length) / 1000;
        var atEnds = Math.Min(e / 2, eave) / eave;
        var (stripSouth, stripNorth) = (Math.Min(e / 10, deepSouth) / deepSouth, Math.Min(e / 10, deepNorth) / deepNorth);
        var edge = (0.5 + 0.2 / 3) * atEnds + 0.7 * (1 - atEnds);

        var windward = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == south && load.Said.StartsWith("windward slope", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(windward);
        Assert.All(windward, load =>
        {
            Assert.Contains("Table 7.5", load.Said);
            Assert.Equal(edge * stripSouth + (0.4 + 0.2 / 3) * (1 - stripSouth), -load.Force.Z / (q * deepSouth), 4);
        });
        var leeward = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == north && load.Said.StartsWith("leeward slope", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(leeward);
        Assert.All(leeward, load => Assert.Equal(-(0.7 - 0.1 / 3) * stripNorth - (0.4 - 0.1 / 3) * (1 - stripNorth), -load.Force.Z / (q * deepNorth), 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindAlongARidgeLiftsItMostAtTheGable(bool us)
    {
        // The wind from the west along the ridge at 35°. Table 7.4b: within e/10 of the windward gable F -1.1 for e/4 in from the eave and G -1.4 between, H -0.833 to e/2, I -0.5 past it, e the lesser
        // of the 8 m across and 2h, 6 m. ASCE 7-16 Figure 27.3-1 parallel to the ridge, h/L 3 / 10.9: -0.9 to h, -0.5 to 2h, -0.3 past it.
        var (document, walls, _) = House(us: us);
        var loading = Loaded(document);
        var wind = loading.Cases.Single(each => each.Name == "W1");
        var q = document.ProjectInformation.WindPressure * (us ? 0.85 : 1);
        var h = Wind.Height(document) / 1000;
        var e = Math.Min(8, 2 * h);
        foreach (var wall in new[] { walls[0], walls[2] })
        {
            // The wall's top runs a little past the corners each end, the first part of it in the first zone and the last in the last.
            var deep = Deep(document, loading, wall, 35);
            var past = (loading.LineLoads.Where(load => load.Case.Name == "S1" && load.Panel?.Element == wall).Sum(load => load.Length) / 1000 - 10) / 2;
            Assert.InRange(past, 0, 0.3);
            var near = (-1.1 * Math.Min(e / 4, deep) - 1.4 * Math.Max(0, deep - e / 4)) / deep;
            var expected = us
                ? -0.9 * (h + past) - 0.5 * h - 0.3 * (10 + past - 2 * h)
                : near * (e / 10 + past) - (0.8 + 0.1 / 3) * (e / 2 - e / 10) - 0.5 * (10 + past - e / 2);
            var up = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == wall).Sum(load => load.Force.Z * load.Length / 1000);
            Assert.InRange(up, -0.98 * expected * q * deep, -1.02 * expected * q * deep);
            var pieces = loading.LineLoads.Where(load => load.Case == wind && load.Panel?.Element == wall).Select(load => Math.Round(-load.Force.Z / (q * deep), 3)).Distinct().OrderBy(c => c).ToList();
            Assert.Equal(us ? new[] { -0.9, -0.5, -0.3 } : new[] { Math.Round(near, 3), -0.833, -0.5 }, pieces);
        }

        // The slopes draw the eaves apart, and the building is not pushed across.
        Assert.Equal(0, loading.LineLoads.Where(load => load.Case == wind).Sum(load => load.Force.Y * load.Length / 1000), 6);
    }

    [Fact]
    public void AFlatRoofIsDrawnOffByItsZones()
    {
        // The wind from the west over a flat roof, on a concrete plate and on joists. Table 7.2's sharp eaves: the strip e/10 deep at the windward edge F -1.8 for e/4 in from each corner and
        // G -1.2 between, H -0.7 to e/2 and I -0.2 past it, e the lesser of the 8 m across and 2h = 6 m - averaged over the roof's depth.
        var (document, _, roof) = House(flat: true, overhang: 0);
        var loading = Loaded(document);
        var wind = loading.Cases.Single(each => each.Name == "W1");
        var q = document.ProjectInformation.WindPressure;
        var deep = (roof.Boundary.Max(p => p.X) - roof.Boundary.Min(p => p.X)) / 1000;
        var wide = (roof.Boundary.Max(p => p.Y) - roof.Boundary.Min(p => p.Y)) / 1000;
        var e = Math.Min(8, 2 * Wind.Height(document) / 1000);
        var corners = Math.Min(e / 2, wide);
        var strip = (-1.8 * corners - 1.2 * (wide - corners)) / wide;
        var lifted = (strip * e / 10 - 0.7 * (e / 2 - e / 10) - 0.2 * (deep - e / 2)) / deep;

        var plate = loading.SurfaceLoads.Single(load => load.Case == wind && load.Panel.Element == roof);
        Assert.Equal(-lifted * q, plate.Force.Z, 6);

        // On timber joists, spread onto the walls as its snow is: in all, the same pull on the same area.
        var timber = document.Materials.First(material => material.Name.Contains("Softwood", StringComparison.OrdinalIgnoreCase) || material.Name.Contains("Timber", StringComparison.OrdinalIgnoreCase));
        var joisted = new RoofType("Roof - Flat Timber Joists", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, timber.Id, 200))) { Function = SlabFunction.Structural };
        document.AddType(joisted);
        roof.TypeId = joisted.Id;
        var spread = Loaded(document);
        var w1 = spread.Cases.Single(each => each.Name == "W1");
        Assert.DoesNotContain(spread.SurfaceLoads, load => load.Case == w1 && load.Panel.Element == roof);
        var area = spread.Total(spread.Cases.Single(each => each.Name == "S1")) / StructuralLoading.RoofSnow(document, 0);
        var pulled = spread.LineLoads.Where(load => load.Case == w1 && load.Said.StartsWith("flat roof", StringComparison.Ordinal)).Sum(load => load.Force.Z * load.Length / 1000);
        Assert.Equal(-lifted * q * area, pulled, 3);
    }

    [Fact]
    public void ImposedLoadsOnAColumnAreReducedByTheFloorsOverIt()
    {
        // BS EN 1991-1-1 6.3.1.2(11) with the UK National Annex: αn = 1.1 - n/10 for two to five floors over a column. The middle columns of five storeys of offices, from the ground up.
        var document = NotionalRemovalTests.Office(5);
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));

        // Taken whole where the project does not reduce them, as it does not unless asked - and kept whole by what was worked out so, when it is asked after.
        Assert.False(document.ProjectInformation.ReduceImposedLoads);
        var whole = SecondOrderAnalysis.Run(document, analysis);
        document.ProjectInformation.ReduceImposedLoads = true;
        var reduced = SecondOrderAnalysis.Run(document, analysis);
        var middle = model.Members.Where(member => member.Element is Column { Location: { X: 6000, Y: 6000 } }).OrderBy(member => member.Nodes.Min(node => node.At.Z)).ToList();
        Assert.All(middle, member => Assert.Equal(1, whole.ImposedShare(member)));
        Assert.DoesNotContain(whole.Notes, note => note.Contains("6.3.1.2", StringComparison.Ordinal));

        Assert.Equal(5, middle.Count);
        Assert.Equal(new[] { 0.6, 0.7, 0.8, 0.9, 1.0 }, middle.Select(reduced.ImposedShare).Select(share => Math.Round(share, 9)));
        Assert.Contains(reduced.Notes, note => note.Contains("6.3.1.2", StringComparison.Ordinal));

        // What the ground storey's column is checked for with the imposed load leading is 0.4 of 1.5 Q less; what it puts on its footing is not reduced.
        var imposed = analysis.Loading.Cases.Single(each => each.Name == "Q1");
        var carried = Math.Abs(analysis.MemberOf(middle[0], imposed).Axial);
        var leading = analysis.Loading.Combinations.First(each => each.Factored && each.Terms.Any(term => term.Case == imposed && Math.Abs(term.Factor - 1.5) < 1e-9));
        double Axial(SecondOrderAnalysis second) => second.StationsOf(middle[0]).Where(station => station.Combination.Name == leading.Name || station.Combination.Name.StartsWith(leading.Name + " ", StringComparison.Ordinal)).Max(station => Math.Abs(station.Forces.N));
        Assert.InRange(Axial(whole) - Axial(reduced), 0.95 * 0.4 * 1.5 * carried, 1.05 * 0.4 * 1.5 * carried);
        Assert.Equal(whole.LandingsOf(middle[0]).Sum(each => each.Down.Values.Sum()), reduced.LandingsOf(middle[0]).Sum(each => each.Down.Values.Sum()), 6);
    }

    [Fact]
    public void UsLiveLoadsAreReducedByTheirInfluenceArea()
    {
        // ASCE 7-16 (4.7-1): L = Lo (0.25 + 15 / √(KLL AT)), KLL 4 for a column, no less than 0.4 Lo for one carrying two floors or more and 0.5 for one; AT all the floors' area over it.
        var document = NotionalRemovalTests.Office(5);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.ReduceImposedLoads = true;
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        var reduced = SecondOrderAnalysis.Run(document, analysis);
        var live = analysis.Loading.Cases.Single(each => each.Name == "L1");
        var middle = model.Members.Where(member => member.Element is Column { Location: { X: 6000, Y: 6000 } }).OrderBy(member => member.Nodes.Min(node => node.At.Z)).ToList();
        foreach (var (column, floors) in middle.Select((column, i) => (column, 5 - i)))
        {
            var area = Math.Abs(analysis.MemberOf(column, live).Axial) / (50 * Loads.Psf);
            var share = Math.Min(1, Math.Max(floors >= 2 ? 0.4 : 0.5, 0.25 + 15 / Math.Sqrt(4 * area * 10.7639)));

            // Its area read from the analysis at the stiffnesses second order takes, a little off the first order's.
            Assert.InRange(reduced.ImposedShare(column), share - 0.01, share + 0.01);
        }

        Assert.InRange(reduced.ImposedShare(middle[0]), 0.4, 0.45);
        Assert.Contains(reduced.Notes, note => note.Contains("ASCE 7-16 4.7", StringComparison.Ordinal));
    }

    [Fact]
    public void ReducingImposedLoadsIsSaved()
    {
        var (document, _, _) = House();
        Assert.False(document.ProjectInformation.ReduceImposedLoads);
        document.ProjectInformation.ReduceImposedLoads = true;
        var path = Path.Combine(Path.GetTempPath(), $"reduced-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            Assert.True(ProjectFile.Load(path).ProjectInformation.ReduceImposedLoads);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
