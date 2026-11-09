using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Snow drifted against what rises above a flat roof: a taller part of the building and a parapet. In the UK by BS EN 1991-1-3 Annex B, as its National Annex has it, an accidental action
/// with no snow elsewhere, combined by BS EN 1990 6.11b; in the US by ASCE 7-16 7.7.1 and 7.8 on top of the snow lying evenly.
/// </summary>
public class SnowDriftTests
{
    /// <summary>
    /// A house 10 by 8 m of walls 6 m high under a flat concrete roof - or a gable roof at a pitch - with a single storey extension on its south side, 3 m high under its own flat roof, which runs up
    /// to the house wall's face: 6 m along it and 4 out, or 4 along and 6 out on timber joists spanning across.
    /// </summary>
    private static (BimDocument Document, Roof Lower, Roof Upper, double Half) Extension(bool us = false, bool timber = false, double upperPitch = 0, double leanTo = 0, double houseHeight = 6000)
    {
        var document = BimDocument.CreateDefault();
        if (us)
        {
            document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
            document.ProjectInformation.GroundSnowLoad = 30;
        }

        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var half = wallType.Structure.TotalWidth / 2;
        var flat = document.TypesOf<RoofType>().First(t => t.Name == "Roof - Warm Flat 320mm");
        var walls = new List<Wall>();
        void Loop(Point2D[] corners, double height, bool closed)
        {
            for (var i = 0; i < corners.Length - (closed ? 0 : 1); i++)
            {
                var wall = new Wall { Start = corners[i], End = corners[(i + 1) % corners.Length], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = height, StructuralUsage = StructuralUsage.Bearing };
                document.Add(wall);
                walls.Add(wall);
            }
        }

        var house = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        Loop(house, houseHeight, true);
        var pitched = document.TypesOf<RoofType>().First(t => t.Name == "Roof - Slate on Timber Rafters 150mm");
        var upper = new Roof { TypeId = upperPitch > 0 ? pitched.Id : flat.Id, LevelId = level, HeightOffset = houseHeight };
        upper.SetBoundary(new[] { new Point2D(-half, -half), new Point2D(10000 + half, -half), new Point2D(10000 + half, 8000 + half), new Point2D(-half, 8000 + half) });
        upper.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = upperPitch > 0 && i % 2 == 0, SlopeDegrees = upperPitch, WallId = walls[i].Id, OnLeftOfWall = false }));
        document.Add(upper);

        var (wide, deep) = timber ? (4000.0, 6000.0) : (6000.0, 4000.0);
        var (x0, x1) = (5000 - wide / 2, 5000 + wide / 2);
        Loop(new[] { new Point2D(x0, -half), new Point2D(x0, -half - deep), new Point2D(x1, -half - deep), new Point2D(x1, -half) }, 3000, false);
        var lowerType = leanTo > 0 ? pitched : flat;
        if (timber)
        {
            var wood = document.Materials.First(material => material.Name.Contains("Softwood", StringComparison.OrdinalIgnoreCase));
            lowerType = new RoofType("Roof - Flat Timber Joists", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, wood.Id, 200))) { Function = SlabFunction.Structural };
            document.AddType(lowerType);
        }

        var lower = new Roof { TypeId = lowerType.Id, LevelId = level, HeightOffset = 3000 };
        lower.SetBoundary(new[] { new Point2D(x0 - half, -2 * half - deep), new Point2D(x1 + half, -2 * half - deep), new Point2D(x1 + half, -half), new Point2D(x0 - half, -half) });
        // A lean-to: its eave over the extension's far wall, rising to the house.
        lower.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = leanTo > 0 && i == 0, SlopeDegrees = leanTo, WallId = i == 0 ? walls[^2].Id : null, OnLeftOfWall = false }));
        document.Add(lower);
        foreach (var wall in walls) document.Add(Footings.Fit(document, wall));
        return (document, lower, upper, half);
    }

    /// <summary>A box 10 by 8 m under a flat concrete roof at 3 m, its walls running on above it as a parapet so high.</summary>
    private static (BimDocument Document, Roof Roof) Parapet(double above, bool us = false, bool faces = false)
    {
        var document = BimDocument.CreateDefault();
        if (us)
        {
            document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
            document.ProjectInformation.GroundSnowLoad = 30;
        }

        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var flat = document.TypesOf<RoofType>().First(t => t.Name == "Roof - Warm Flat 320mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = Enumerable.Range(0, 4).Select(i => new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3320 + above, StructuralUsage = StructuralUsage.Bearing }).ToList();
        foreach (var wall in walls) document.Add(wall);
        var roof = new Roof { TypeId = flat.Id, LevelId = level, HeightOffset = 3000 };
        var half = faces ? wallType.Structure.TotalWidth / 2 : 0;
        roof.SetBoundary(new[] { new Point2D(-half, -half), new Point2D(10000 + half, -half), new Point2D(10000 + half, 8000 + half), new Point2D(-half, 8000 + half) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(_ => new RoofEdge { DefinesSlope = false }));
        document.Add(roof);
        foreach (var wall in walls) document.Add(Footings.Fit(document, wall));
        return (document, roof);
    }

    private static StructuralLoading Loaded(BimDocument document) => StructuralLoading.Of(document, AnalyticalModel.Of(document));

    /// <summary>A ramp's load in all over a rectangle it covers from its wall out, kN: whole at the wall, to nothing at ls, cut off where the roof ends.</summary>
    private static double Triangle(double peak, double along, double deep, double ls) => peak * along * (Math.Min(deep, ls) - Math.Min(deep, ls) * Math.Min(deep, ls) / (2 * ls));

    /// <summary>A drift whole along y = line, mm, falling south to nothing at ls, m, over a rectangular plate as the analysis has it, kN.</summary>
    private static double OnPlate(AnalyticalPanel plate, double peak, double line, double ls)
    {
        var (xs, ys) = (plate.Outline.Select(node => node.At.X).ToList(), plate.Outline.Select(node => node.At.Y).ToList());
        return Triangle(peak, (xs.Max() - xs.Min()) / 1000, (line - ys.Min()) / 1000, ls);
    }

    [Fact]
    public void AnExtensionsRoofTakesTheExceptionalDriftAgainstTheHouse()
    {
        // BS EN 1991-1-3 B3 and Table B1, a flat upper roof: h 6000 - 3320 = 2.68 m, b1 the house beyond the wall, b2 the extension's roof; ls the least of 5h, b1 and 15 m; μ the least of
        // 2h/sk, 2b/ls and 8, b the larger of b1 and b2 - here 2b/ls with ls = b1, 2. Falling to nothing at ls past the extension's 4 m, so cut off at its end.
        var (document, lower, _, half) = Extension();
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var drift = loading.Cases.Single(each => each.Name == "S2");
        Assert.True(drift.Accidental && drift.Drifted);
        var load = Assert.Single(loading.SurfaceLoads.Where(each => each.Case == drift));
        Assert.Same(lower, load.Panel.Element);
        Assert.NotNull(load.Ramp);

        var sk = StructuralLoading.GroundSnow(document.ProjectInformation.SnowZone, document.ProjectInformation.SiteAltitude);
        var h = (6000 - 3320) / 1000.0;
        var (b1, b2) = ((8000 + 2 * half) / 1000, (4000 + half) / 1000);
        var ls = Math.Min(Math.Min(5 * h, b1), 15);
        var mu = Math.Min(Math.Min(2 * h / sk, 2 * Math.Max(b1, b2) / ls), 8);
        Assert.Equal(2, mu, 6);
        Assert.Equal(mu * sk, -load.Force.Z, 6);

        // On the plate as the analysis has it, between the lines of the walls it stands on - a few per cent short of the roof as drawn, as every load on it is.
        var total = OnPlate(load.Panel, mu * sk, -half, ls);
        Assert.Equal(total, loading.Total(drift), 3);
        Assert.InRange(total, 0.9 * Triangle(mu * sk, (6000 + 2 * half) / 1000, b2, ls), Triangle(mu * sk, (6000 + 2 * half) / 1000, b2, ls));

        // No snow elsewhere: alone with the weight, by 6.11b, the floors' imposed load at ψ1 had there been any, a wind at ψ1 0.2; never with the snow lying evenly.
        var said = loading.Combinations.Where(each => each.Terms.Any(term => term.Case == drift)).Select(each => each.Said).ToList();
        Assert.Contains("1 G1 + 1 G2 + 1 S2", said);
        Assert.Contains("1 G1 + 1 G2 + 1 S2 + 0.2 W1", said);
        Assert.All(loading.Combinations.Where(each => each.Terms.Any(term => term.Case == drift)), each =>
        {
            Assert.Contains("6.11b", each.Description);
            Assert.DoesNotContain(each.Terms, term => term.Case.Nature == LoadNature.Snow && term.Case != drift);
        });
        Assert.Contains(loading.Notes, note => note.Contains("Annex B", StringComparison.Ordinal) && note.Contains("6.11b", StringComparison.Ordinal));

        // The analysis puts it on the plate's triangles, each by what of it the drift covers: the same in all, the plate now running on to the house wall's line behind where the drift starts.
        var analysis = StructuralAnalysis.Run(document, model, loading);
        Assert.Equal(loading.Total(drift), -analysis.TotalLoad(drift).Z, 2);
        Assert.Equal(loading.Total(drift), analysis.TotalReaction(drift).Z, 2);
        Assert.DoesNotContain(analysis.Variable, each => each.Item1 == drift);
    }

    [Fact]
    public void AStepRoofsDriftIsNotLessForASteepRoofOverIt()
    {
        // Table B1 by the pitch of the roof the drift lies on: a flat roof under a house roofed at 45° takes μ3 as under a flat one.
        var (flat, _, _, _) = Extension();
        var (steep, _, _, _) = Extension(upperPitch: 45);
        double Peak(BimDocument document)
        {
            var loading = Loaded(document);
            return -loading.SurfaceLoads.Single(each => each.Case.Accidental).Force.Z;
        }

        var sk = StructuralLoading.GroundSnow(flat.ProjectInformation.SnowZone, flat.ProjectInformation.SiteAltitude);
        Assert.Equal(2 * sk, Peak(flat), 6);
        Assert.Equal(Peak(flat), Peak(steep), 6);
    }

    [Theory]
    [InlineData(20.0, 6000.0)]
    [InlineData(45.0, 9000.0)]
    public void ALeanToTakesTheDriftByItsOwnPitch(double pitch, double houseHeight)
    {
        // A lean-to rising to the house wall: Table B1's μ2 by its own pitch, μ3 to 30° and less past it to nothing at 60°; h from its height at the wall. On the wall under its eave, as its other
        // loads go, its overhangs past the walls' ends with it.
        var (document, lower, _, half) = Extension(leanTo: pitch, houseHeight: houseHeight);
        var loading = Loaded(document);
        var drift = loading.Cases.Single(each => each.Accidental);
        var lines = loading.LineLoads.Where(each => each.Case == drift).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, each => Assert.True(each.Panel?.Element is Wall wall && Math.Abs(wall.Start.Y - wall.End.Y) < 1 && wall.Start.Y < -1000, "the eave's wall"));

        var sk = StructuralLoading.GroundSnow(document.ProjectInformation.SnowZone, document.ProjectInformation.SiteAltitude);
        var h = (houseHeight - lower.Surface(document).HeightAt(new Point2D(5000, -half - 100))) / 1000;
        var (b1, b2) = ((8000 + 2 * half) / 1000, (4000 + half) / 1000);
        var ls = Math.Min(Math.Min(5 * h, b1), 15);
        var most = Math.Min(Math.Min(2 * h / sk, 2 * Math.Max(b1, b2) / ls), 8);
        var mu = pitch <= 30 ? most : most * (60 - pitch) / 30;
        Assert.Contains($"μ {mu:0.00}", lines[0].Said);
        var total = Triangle(mu * sk, (6000 + 2 * half) / 1000, b2, ls);
        Assert.InRange(loading.Total(drift), 0.97 * total, 1.03 * total);
    }

    [Fact]
    public void AUsExtensionsRoofTakesTheLeewardDriftOnTopOfItsSnow()
    {
        // ASCE 7-16 7.7.1: pg 30 psf, ps 21 psf on the flat roofs, γ 0.13 x 30 + 14 = 17.9 pcf, hb 1.17 ft, hc 8.79 - 1.17 ft. The leeward drift, hd by (7.6-1) with lu the house's 27 ft, is more
        // than the windward, 0.75 hd with the extension's 13 ft; under hc, so w = 4 hd and pd = hd γ, on top of the snow lying evenly.
        var (document, lower, _, half) = Extension(us: true);
        var loading = Loaded(document);
        var drift = loading.Cases.Single(each => each.Name == "S2");
        Assert.False(drift.Accidental);
        Assert.True(drift.Drifted);
        var surcharge = Assert.Single(loading.SurfaceLoads.Where(each => each.Case == drift && each.Ramp is not null));

        double Height(double lu) => 0.43 * Math.Cbrt(lu) * Math.Pow(30 + 10, 0.25) - 1.5;
        var gamma = 0.13 * 30 + 14;
        var hb = 21 / gamma;
        var (b1, b2) = ((8000 + 2 * half) / 304.8, (4000 + half) / 304.8);
        var hd = Height(b1);
        Assert.True(hd > 0.75 * Height(b2));
        Assert.True(hd < (6000 - 3320) / 304.8 - hb);
        var (pd, w) = (hd * gamma * Loads.Psf, 4 * hd * 0.3048);
        Assert.Equal(pd, -surcharge.Force.Z, 6);
        Assert.Equal(w * 1000, surcharge.Ramp!.Length, 3);

        var s1 = loading.Cases.Single(each => each.Name == "S1");
        Assert.Equal(loading.Total(s1) + OnPlate(surcharge.Panel, pd, -half, w), loading.Total(drift), 3);
        Assert.Contains(loading.Combinations, each => each.Said == "1.2 D1 + 1.2 D2 + 1.6 S2 + 0.5 W1");
        Assert.Contains(loading.Notes, note => note.Contains("7.7.1", StringComparison.Ordinal));
        Assert.Same(lower, surcharge.Panel.Element);
    }

    [Fact]
    public void ARoofOnJoistsSharesItsDriftOntoWhatItSpansOnto()
    {
        // The extension 4 m along the house and 6 m out on timber joists across its short way, onto its side walls: each takes its half of each strip, the drift along them as it falls away.
        var (document, lower, _, half) = Extension(timber: true);
        var loading = Loaded(document);
        var drift = loading.Cases.Single(each => each.Accidental);
        Assert.DoesNotContain(loading.SurfaceLoads, each => each.Case == drift);
        var lines = loading.LineLoads.Where(each => each.Case == drift).ToList();
        Assert.NotEmpty(lines);

        var sk = StructuralLoading.GroundSnow(document.ProjectInformation.SnowZone, document.ProjectInformation.SiteAltitude);
        var h = (6000 - 3200) / 1000.0;
        var (b1, b2) = ((8000 + 2 * half) / 1000, (6000 + half) / 1000);
        var ls = Math.Min(Math.Min(5 * h, b1), 15);
        var mu = Math.Min(Math.Min(2 * h / sk, 2 * Math.Max(b1, b2) / ls), 8);
        var total = Triangle(mu * sk, (4000 + 2 * half) / 1000, b2, ls);
        Assert.InRange(loading.Total(drift), 0.97 * total, 1.03 * total);

        // On the side walls, half each, heaviest by the house.
        var walls = lines.GroupBy(each => each.Panel?.Element).ToList();
        Assert.Equal(2, walls.Count);
        Assert.All(walls, wall => Assert.InRange(wall.Sum(each => -each.Force.Z * each.Length / 1000), 0.45 * total, 0.55 * total));
        Assert.All(walls, wall => Assert.True(wall.Count() > 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnowDriftsBehindAParapet(bool us)
    {
        // A metre of parapet round a 10 by 8 m roof. UK, B4(4): μ the least of 2h/sk, 2b/ls and 8, ls the least of 5h, b1 and 15 m, b the roof's depth from the parapet - 2h/sk, to nothing at
        // 5 m. US, 7.8: 0.75 hd with lu the roof's depth, no less than 25 ft, hc the parapet's height over hb, w = 4 hd.
        var (document, roof) = Parapet(1000, us);
        var loading = Loaded(document);
        var drift = loading.Cases.Single(each => each.Drifted);
        var ramps = loading.SurfaceLoads.Where(each => each.Case == drift && each.Ramp is not null).ToList();
        Assert.Equal(4, ramps.Count);
        Assert.All(ramps, each => Assert.Same(roof, each.Panel.Element));
        foreach (var ramp in ramps)
        {
            var deep = Math.Abs(ramp.Ramp!.Away.X) > 0.5 ? 10.0 : 8.0;
            if (us)
            {
                var gamma = 0.13 * 30 + 14;
                var hd = 0.75 * (0.43 * Math.Cbrt(Math.Max(deep / 0.3048, 25)) * Math.Pow(40, 0.25) - 1.5);
                Assert.Equal(hd * gamma * Loads.Psf, -ramp.Force.Z, 6);
                Assert.Equal(4 * hd * 304.8, ramp.Ramp.Length, 3);
            }
            else
            {
                var sk = StructuralLoading.GroundSnow(document.ProjectInformation.SnowZone, document.ProjectInformation.SiteAltitude);
                var ls = Math.Min(Math.Min(5 * 1.0, deep), 15);
                Assert.Equal(Math.Min(Math.Min(2 * 1.0 / sk, 2 * deep / ls), 8) * sk, -ramp.Force.Z, 6);
                Assert.Equal(ls * 1000, ramp.Ramp.Length, 6);
            }
        }

        // A low parapet in the US, hc under 0.2 hb: no drift.
        if (us) Assert.DoesNotContain(Loaded(Parapet(250, true).Document).Cases, each => each.Drifted);
    }

    [Fact]
    public void ARoofInsideItsParapetsRestsOnTheWallsThatRunPastIt()
    {
        // The roof drawn out to the walls' outer faces, the walls running a metre past it: its edges are taken to the walls' lines and joined there, as on walls stopping under it - it read as
        // resting on nothing. Its snow comes back through the walls, and it bends as the same roof on the walls' tops does.
        double Sag(BimDocument document, Roof roof)
        {
            var model = AnalyticalModel.Of(document);
            Assert.DoesNotContain(model.Problems, problem => problem.Elements.Contains(roof));
            var loading = StructuralLoading.Of(document, model);
            var analysis = StructuralAnalysis.Run(document, model, loading);
            var snow = loading.Cases.Single(each => each.Name == "S1");
            Assert.Equal(-analysis.TotalLoad(snow).Z, analysis.TotalReaction(snow).Z, 1);
            return Math.Abs(analysis.PanelOf(model.Panels.Single(panel => panel.Element == roof), snow).Deflection);
        }

        var (parapets, inside) = Parapet(1000, faces: true);
        var (none, under) = Parapet(-320, faces: true);
        var (sag, onTops) = (Sag(parapets, inside), Sag(none, under));
        Assert.True(onTops > 0.1, $"{onTops:0.00} mm");
        Assert.InRange(sag, 0.5 * onTops, 1.1 * onTops);
    }

    [Fact]
    public void ALeanToIsADiaphragmOnItsOwnWallsHeldByTheHouseWall()
    {
        // A timber lean-to's diaphragm sits on the walls under it, at 3 m, its edge against the house taken to the house wall and held there. It had been put at the top of the house wall, the
        // highest wall near it, 6 m up over the lean-to and held by nothing but that wall's top - free to move at seven nodes.
        var (document, lower, _, _) = Extension(leanTo: 20);
        var model = AnalyticalModel.Of(document);
        var diaphragm = Assert.Single(model.Diaphragms, panel => panel.Element == lower);
        Assert.Equal(3000, diaphragm.Origin.Z, 0);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        Assert.DoesNotContain(analysis.Problems, problem => problem.Contains("free to move", StringComparison.Ordinal));
    }

    [Fact]
    public void ARoofWithNothingAboveItHasNoDrift()
    {
        // Walls up to the roof and no further, and nothing beside it: no case of drifted snow.
        var (document, _) = Parapet(-320);
        Assert.DoesNotContain(Loaded(document).Cases, each => each.Drifted);
    }

    [Fact]
    public void ARampIsIntegratedOverAnOutline()
    {
        // Whole along y = 0, falling to nothing at 2 m out along +y, from x 0 to 4 m: over a 4 by 1 m strip from the line, 4 x (1 - 1/4) = 3 m² at its whole value; over 4 by 3, the 2 m it reaches, 4 m².
        var ramp = new LoadRamp(new Point2D(0, 0), Vector2D.UnitY, Vector2D.UnitX, 2000, 0, 4000);
        Assert.Equal(3e6, ramp.Over(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 1000), new Point2D(0, 1000) }), 3);
        Assert.Equal(4e6, ramp.Over(new[] { new Point2D(0, 0), new Point2D(0, 3000), new Point2D(4000, 3000), new Point2D(4000, 0) }), 3);
        Assert.Equal(2e6, ramp.Over(new[] { new Point2D(-2000, 0), new Point2D(2000, 0), new Point2D(2000, 3000), new Point2D(-2000, 3000) }), 3);
        Assert.Equal(0.75, ramp.Share(new Point2D(1000, 500)), 9);
        Assert.Equal(0, ramp.Share(new Point2D(5000, 500)), 9);
        Assert.Equal(0, ramp.Share(new Point2D(1000, -1)), 9);
    }
}
