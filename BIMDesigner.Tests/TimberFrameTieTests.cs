using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>Timber frame finished, in whole houses: what ties stud walls down, their end studs, headers on their jacks, and what a lining does in a fire, each from a hand figure.</summary>
public class TimberFrameTieTests
{
    [Fact]
    public void AWallRunningOnPastItsFloorLeansAtTheFloor()
    {
        // One storey, its walls going on 150 past the floor that holds them. Leaned at the top of that strip, held by nothing, it read 24 and set off second order; at the floor, what is left is the strip's own short length under the roof, far clear of the 10 that would.
        var (_, running) = Analysed(House(pastTheFloor: 150, storeys: 1));
        Assert.True(running.Worst!.Alpha > 10, $"least {running.Worst!.Alpha:0.0}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASwayFigureIsTheBuildingsAndNotAStudsBowing(bool us)
    {
        // A two storey timber frame house's sway is its walls racking on their nails, about 4 kN/mm a wall: its studs' own buckling, about 80 kN Euler against 8 carried, is in their checks and is not what it is measured by. It does not buckle, nor come near.
        var document = House(openings: true, standard: us ? ClearanceStandard.Irc : null);
        var (_, second) = Analysed(document);
        Assert.DoesNotContain(second.Results, result => result.Buckles);
        Assert.True(second.Worst!.Alpha > 10, $"least {second.Worst!.Alpha:0.0}");
    }

    [Fact]
    public void AnEndForceIsTheMomentOverTheLengthsThatCount()
    {
        // 30 kN racking a 3 m wall: 90 kNm, (9.23) on one 6 m length is 15 kN at each end.
        Assert.Equal(15, TimberWallDesign.EndForce(90, new[] { 6000.0 }, 3000), 6);

        // Two lengths of 3 m share it, each taking 15 kN of the shear: 15 × 3 / 3 = 15 kN at each of their ends, the same.
        Assert.Equal(15, TimberWallDesign.EndForce(90, new[] { 3000.0, 3000.0 }, 3000), 6);

        // A length under half the height counts in proportion, c 0.8: 90 / (6 + 1.2 × 0.8) = 13.8.
        Assert.Equal(90 / 6.96, TimberWallDesign.EndForce(90, new[] { 6000.0, 1200.0 }, 3000), 6);
    }

    [Fact]
    public void WhatStandsOnItHoldsItDown()
    {
        // 15 kN at an end against 8 kN/m on 3 m: half of 24 is 12, so 3 kN lifts it.
        Assert.Equal(3, TimberWallDesign.Uplift(15, 8, 3000), 6);

        // Under a heavier wall nothing lifts.
        Assert.True(TimberWallDesign.Uplift(15, 12, 3000) < 0);
    }

    [Fact]
    public void BoardsBehaveAsThe34Rules()
    {
        // Two layers of 12.5: outer and half the inner, 18.75; (3.11) 2.8 × 18.75 - 14 = 38.5 minutes.
        Assert.Equal(18.75, TimberFire.Counted(new[] { 12.5, 12.5 }), 6);
        Assert.Equal(15, TimberFire.Counted(new[] { 15.0 }), 6);
        Assert.Equal(0, TimberFire.Counted(Array.Empty<double>()));
        Assert.Equal(38.5, TimberFire.StartOfCharring(18.75), 6);
        Assert.Equal(21, TimberFire.StartOfCharring(12.5), 6);
        Assert.Equal(0, TimberFire.StartOfCharring(5), 6);
    }

    [Fact]
    public void ALiningHoldsTheFireOffAndThenItCatchesUp()
    {
        // 12.5 board falls at 21 minutes: at 30, 2 × 0.8 × 9 = 14.4 and the whole 7 of zero strength: 21.4.
        Assert.Equal(21.4, TimberFire.Behind(30, 12.5, false), 6);

        // 15 board falls at 28: at 30, 3.2 and, 28 being over 20, the zero strength layer whole by then: 10.2. Before it falls, only that layer growing: at 14, half of 7.
        Assert.Equal(10.2, TimberFire.Behind(30, 15, false), 6);
        Assert.Equal(3.5, TimberFire.Behind(14, 15, false), 6);

        // Doubled to 25 mm or twice its time: 12.5 board at 60: until 2 × 21 = 42, against 25 / 1.6 + 21 = 36.6; 25 by then, and 0.8 × 23.375 more: 43.7 + 7 = 50.7.
        Assert.Equal(50.7, TimberFire.Behind(60, 12.5, false), 6);

        // With no lining, a bare member's.
        Assert.Equal(TimberFire.Effective(30, false), TimberFire.Behind(30, 0, false), 6);
    }

    [Fact]
    public void AnInsulatedStudChars()
    {
        // 38 by 140 behind 15 board: tf 28; ks 1.4, k3 0.036 × 28 + 1 = 2.008, kn 1.5, β0 0.65: 2.74 mm/min. At 30 minutes 5.48 mm.
        var left = TimberFire.Stud(30, 15, 140, 38)!.Value;
        Assert.Equal(5.48, left.Charred, 2);

        // At 140, by straight lines: a0 0.541, a1 0.397: 0.541 - 0.397 × 5.48 / 140 = 0.5255. b0 0.59, b1 0.835: 0.5573.
        Assert.InRange(left.Strength, 0.52, 0.53);
        Assert.InRange(left.Stiffness, 0.55, 0.56);

        // No further than the annex goes.
        Assert.Null(TimberFire.Stud(90, 15, 140, 38));
    }

    /// <summary>A two storey timber frame house, 10 by 8, with a floor on joists and a roof, a window and a door in its front wall if asked.</summary>
    private static BimDocument House(bool openings = false, ClearanceStandard? standard = null, string wall = "Timber Frame", double pastTheFloor = 0, int storeys = 2)
    {
        var document = BimDocument.CreateDefault();
        if (standard is { } each) document.ProjectInformation.ClearanceStandard = each;
        document.ProjectInformation.Ground = GroundType.StiffClay;
        var (ground, first) = (document.Levels[0].Id, document.Levels[1].Id);
        var level = new BIMDesigner.Core.Datums.Level { Name = "Eaves", Elevation = 6000 };
        document.AddLevel(level);
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith(wall, StringComparison.Ordinal));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        var walls = new List<Wall>();
        foreach (var (from, to) in storeys == 2 ? new[] { (ground, first), (first, level.Id) } : new[] { (ground, first) })
            for (var i = 0; i < 4; i++)
            {
                var placed = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = from, TopLevelId = to, StructuralUsage = StructuralUsage.Bearing, TopOffset = from == ground ? pastTheFloor : 0 };
                document.Add(placed);
                walls.Add(placed);
            }

        foreach (var placed in walls.Where(placed => placed.LevelId == ground)) document.Add(Footings.Fit(document, placed));
        var joist = new BeamType("Timber 75 x 225") { Material = BeamMaterial.Timber, Width = 75, Depth = 225 };
        document.AddType(joist);
        for (var x = 1000; x <= 9000; x += 2000)
            document.Add(new Beam { TypeId = joist.Id, LevelId = first, Start = new Point2D(x, 0), End = new Point2D(x, 8000), Usage = BeamUsage.Joist });
        var floor = new Floor { TypeId = document.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)).Id, LevelId = first, SpanDirection = 0 };
        floor.SetBoundary(corners);
        document.Add(floor);

        const double over = 465;
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First(t => t.Name.StartsWith("Roof - Slate on Timber", StringComparison.Ordinal)).Id, LevelId = storeys == 2 ? level.Id : first };
        roof.SetBoundary(new[] { new Point2D(-over, -over), new Point2D(10000 + over, -over), new Point2D(10000 + over, 8000 + over), new Point2D(-over, 8000 + over) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35, WallId = walls[(storeys == 2 ? 4 : 0) + i].Id, OnLeftOfWall = false, Overhang = 300 }));
        document.Add(roof);

        if (openings)
        {
            var front = walls[0];
            document.Add(new Door { HostWallId = front.Id, TypeId = document.TypesOf<DoorType>().First().Id, LevelId = front.LevelId, DistanceAlongWall = 2500 });
            document.Add(new Window { HostWallId = front.Id, TypeId = document.TypesOf<WindowType>().First().Id, LevelId = front.LevelId, DistanceAlongWall = 7500, SillHeight = 900 });
        }

        return document;
    }

    private static (StructuralAnalysis Analysis, SecondOrderAnalysis Second) Analysed(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        return (analysis, SecondOrderAnalysis.Run(document, analysis));
    }

    [Fact]
    public void RackingNowComesToTheHandSum()
    {
        // With one way walls, half of each wall facing the wind goes straight to its own sole plate and the other half to its top: storey by storey, the roof and the upper wall and half the lower come through the ground storey's end walls.
        var document = House();
        var (analysis, second) = Analysed(document);
        var loading = analysis.Loading;
        var wind = loading.Cases.Where(c => c.Nature == LoadNature.Wind && Math.Abs(c.Wind!.Value.Y) > 0.5).First();
        var roof = Math.Abs(loading.LineLoads.Where(load => load.Case == wind).Sum(load => load.Force.Y * load.Length / 1000));
        var walls = loading.Across(wind);
        var lower = walls / 2;

        // 1.5 on the wind, between two end walls: the roof, three quarters of the two storeys' wind on walls, half the lower's being at the foot.
        var hand = 1.5 * (roof + 0.75 * walls) / 2;
        var ground = MasonryDesign.Of(document, second).Where(check => Math.Abs(check.Panel.Normal.X) > 0.5 && check.Panel.Outline.Min(node => node.At.Z) < 1).ToList();
        Assert.Equal(2, ground.Count);
        Assert.All(ground, check => Assert.InRange(check.Checks.Single(each => each.Name == "Racking").Demand, 0.85 * hand, 1.1 * hand));
    }

    [Fact]
    public void EveryStudWallEndIsSaidToBeHeldOrTied()
    {
        var document = House(openings: true);
        var (_, second) = Analysed(document);
        var joints = JointDesign.Of(document, second);
        var ties = joints.Where(joint => joint.Kind == "Tie-down").ToList();
        Assert.Equal(8, ties.Count(joint => joint.Name.EndsWith("ends", StringComparison.Ordinal)));
        Assert.All(ties, joint => Assert.NotEmpty(joint.Checks));
        Assert.All(ties, joint => Assert.Contains(joint.Checks.Single().Name, new[] { "Held down by its load", "End studs in tension" }));
    }

    [Fact]
    public void AWallStoreysUpCarriesTheMomentOfThoseAbove()
    {
        // The ground storey's moment takes the upper storey's racking on its height as well: more than its own shear on its own height.
        var document = House();
        var (analysis, second) = Analysed(document);
        var panel = analysis.Model.Panels.First(each => each.Kind == AnalyticalPanelKind.Wall && Math.Abs(each.Normal.X) > 0.5 && each.Outline.Min(node => node.At.Z) < 1);
        var ends = TimberWallDesign.EndsOf(document, second, panel);
        var worst = ends.MaxBy(each => each.Moment)!;
        var racking = MasonryDesign.Of(document, second).Single(check => ReferenceEquals(check.Panel, panel)).Checks.Single(each => each.Name == "Racking").Demand;
        Assert.True(worst.Moment > 1.2 * racking * 3);
    }

    [Fact]
    public void ALightWallIsTiedDownWhereItsLoadWillNotHoldIt()
    {
        // No floor bearing to speak of and a lot of wind: a lightly loaded wall is left with tension.
        var document = House();
        document.ProjectInformation.WindPressure = 2.5;
        var (_, second) = Analysed(document);
        var tied = JointDesign.Of(document, second).Where(joint => joint.Kind == "Tie-down" && joint.Wants.Contains("a tie-down for")).ToList();
        Assert.NotEmpty(tied);
        Assert.All(tied, joint =>
        {
            Assert.Equal("End studs in tension", joint.Checks.Single().Name);
            Assert.Contains("kN at each end", joint.Wants);
        });
    }

    [Fact]
    public void EndStudsTakeRackingOnTopOfTheirLoad()
    {
        var document = House(openings: true);
        var (_, second) = Analysed(document);
        var wall = MasonryDesign.Of(document, second).First(check => check.Checks.Any(each => each.Name.StartsWith("End studs:", StringComparison.Ordinal)));
        Assert.Contains(wall.Checks, each => each.Name.StartsWith("End studs:", StringComparison.Ordinal) && each.Note.Contains("kN from racking"));
    }

    [Fact]
    public void AHeaderSagsAndStandsOnItsJacks()
    {
        var document = House(openings: true);
        var (_, second) = Analysed(document);
        var header = JointDesign.Of(document, second).First(joint => joint.Kind == "Header");

        // Its sag against span/250; a jack the height of the opening's head; its bearing on the jack.
        var sag = header.Checks.Single(each => each.Name == "Sag");
        Assert.Equal("mm", sag.Unit);
        Assert.Contains(header.Checks, each => each.Name.StartsWith("Jack:", StringComparison.Ordinal));
        Assert.Contains(header.Checks, each => each.Name == "Header on its jack");
        Assert.Contains("on a jack each side", header.Wants);
    }

    [Fact]
    public void AHeadersSagIsItsFormula()
    {
        // 76 by 140 over 1,278: I 17.4e6 mm⁴; with 10 kN/m in use, 5 × 10 × 1278⁴ / (384 × 11000 × 17.4e6) = 1.8 mm, × 1.6 for creep: 2.9.
        var document = House(openings: true);
        var (_, second) = Analysed(document);
        var sag = JointDesign.Of(document, second).First(joint => joint.Kind == "Header").Checks.Single(each => each.Name == "Sag");
        Assert.InRange(sag.Demand, 0.3, 6);
        Assert.InRange(sag.Capacity, 3, 7);
    }

    [Fact]
    public void AJoistBehindALiningIsWorkedOutBehindIt()
    {
        // The timber floor type has 20 of board under it: it falls at 42 minutes, so at 30 nothing has charred, and only the zero strength layer, 30 / 42 of 7: 5 mm. 75 wide, 65 left.
        var document = House();
        foreach (var type in document.TypesOf<FloorType>()) type.FireRating = string.Empty;
        var (analysis, second) = Analysed(document);
        var joist = FireDesign.Of(document, second, MemberStrength.Of(document, analysis, second)).First(check => check.Kind == "Joist");
        Assert.Contains(joist.Checks, check => check.Note.Contains("65 by 220"));
        Assert.Contains("Behind 20 mm", joist.Limits);
    }

    [Fact]
    public void AStudWallWithNoRatingIsWorkedOutBehindItsBoard()
    {
        // The default timber frame: 12.5 of plasterboard in, mineral wool in its cavity: Annex C.
        var document = House();
        foreach (var type in document.TypesOf<WallType>()) type.FireRating = string.Empty;
        var (analysis, second) = Analysed(document);
        var wall = FireDesign.Of(document, second, MemberStrength.Of(document, analysis, second)).First(check => check.Kind == "Wall");
        Assert.Equal("In fire: stud in compression", wall.Worst!.Name);
        Assert.Equal("Annex C", wall.Worst.Clause);
        Assert.Contains("Behind 12.5 mm", wall.Limits);
    }
}
