using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Strip footings stepped up rising ground, rafts, ground beams on piles, and the steel in them:
/// Approved Document A's steps, BS EN 1992-1-1's and ACI 318's least steel and cover, BS 8666's
/// bar schedule and BS 4483's fabric, NHBC's void under a ground beam in a shrinkable clay.
/// </summary>
public class DeepFoundationTests
{
    /// <summary>The 10 by 8 m house of outside walls 330 thick, its ground rising toward +x at a slope.</summary>
    private static (BimDocument Document, List<Wall> Walls) House(double slope = 0, double at0 = 0)
    {
        var (document, _) = EaveTests.House(hip: false);
        if (slope != 0)
        {
            var surface = new Toposurface { LevelId = document.Levels[0].Id };
            var points = new List<Point3D>();
            for (var x = -8000.0; x <= 18000; x += 2000)
            for (var y = -8000.0; y <= 16000; y += 2000)
                points.Add(new Point3D(x, y, x * slope + at0));
            surface.SetPoints(points);
            document.Add(surface);
        }

        return (document, document.Walls.ToList());
    }

    [Fact]
    public void BarsAreReadAsDrawingsWriteThem()
    {
        Assert.Equal((3, "H12", 12.0), BarSpec.Parse("3 H12") is { } counted ? (counted.Count!.Value, counted.Size, counted.Diameter) : default);
        Assert.Equal(200, BarSpec.Parse("H12 @ 200")!.Spacing);
        Assert.Equal(304.8, BarSpec.Parse("#4 @ 12 in")!.Spacing!.Value, 3);
        Assert.Equal(12.7, BarSpec.Parse("#4 @ 12 in")!.Diameter, 3);
        Assert.Equal(15.875, BarSpec.Parse("2 #5")!.Diameter, 3);
        Assert.Equal((393.0, 393.0), BarSpec.Parse("a393")!.FabricArea!.Value);
        Assert.Null(BarSpec.Parse("H13"));
        Assert.Null(BarSpec.Parse("H12 @ 20"));
        Assert.Null(BarSpec.Parse("#12"));

        // BS 8666: 0.00617 d² kg a metre - 0.888 for an H12, 1.579 for an H16.
        Assert.InRange(Reinforcement.KilogramsPerMetre(12), 0.887, 0.889);
        Assert.InRange(Reinforcement.KilogramsPerMetre(16), 1.578, 1.580);
        Assert.Equal(2025, Reinforcement.Scheduled(2001));
    }

    [Fact]
    public void AStripFootingStepsUpRisingGroundAsApprovedDocumentAAsks()
    {
        var (document, walls) = House(slope: 0.1, at0: -1000);
        var south = walls[0];
        var footing = Footings.Fit(document, south);
        document.Add(footing);
        var type = document.FindType<FootingType>(footing.TypeId)!;

        var runs = Footings.Runs(document, footing);
        Assert.True(runs.Count >= 3, $"{runs.Count} runs");
        for (var i = 1; i < runs.Count; i++)
        {
            var step = runs[i].Bottom - runs[i - 1].Bottom;
            Assert.True(step > 0, "the ground rises, so the runs step up");
            Assert.True(step <= type.Thickness + 1e-6, "no step higher than the footing is thick");
            Assert.Equal(0, step % Footings.StepIncrement(document), 6);
        }

        // Each lower run laps under the higher by twice the step, the thickness or 300, whichever is greater.
        var outlines = Footings.RunOutlines(document, footing);
        var start = Footings.Outline(document, footing)!.Min(point => point.X);
        for (var i = 1; i < outlines.Count; i++)
        {
            var lap = Footings.Lap(runs[i].Bottom - runs[i - 1].Bottom, type.Thickness);
            Assert.True(outlines[i - 1].Outline.Max(point => point.X) - start >= runs[i].From + lap - 1, "the lower run goes on under the higher");
        }

        // Every run as far under the ground over it as the rules ask, and none deeper than a footing not stepped.
        Assert.True(Footings.DepthBelowGround(document, footing)!.Value >= Footings.DepthAsked(document, type) - 1);
        Assert.DoesNotContain(StructureRules.Problems(document, footing, document.ProjectInformation.ClearanceStandard), problem => problem.Said.Contains("below the ground"));
        Assert.Contains("up at", Footings.StepsSaid(document, footing));

        footing.Stepped = false;
        Assert.Single(Footings.Runs(document, footing));
        var level = runs[0].Bottom;
        Assert.Equal(level, Footings.Runs(document, footing)[0].Bottom, 3);

        // A house dug into the hill: the ground outside is above its floor all round, so its footings go down from the floor.
        var (cut, cutWalls) = House(slope: 0.1, at0: 500);
        var dug = Footings.Fit(cut, cutWalls[0]);
        cut.Add(dug);
        var asked = Footings.DepthAsked(cut, cut.FindType<FootingType>(dug.TypeId)!);
        Assert.Single(Footings.Runs(cut, dug));
        Assert.True(Footings.Runs(cut, dug)[0].Bottom <= -asked + 1, "no shallower under the floor than the rules ask");
        Assert.Equal(asked, Footings.DepthBelowGround(cut, dug)!.Value, 0);
    }

    [Fact]
    public void ARaftGoesUnderTheBuildingToTheOutsideOfItsWalls()
    {
        var (document, _) = House();
        var level = document.Levels[0].Id;
        Assert.Null(Rafts.Under(document, level, new Point2D(30000, 30000)));
        var raft = Rafts.Under(document, level, new Point2D(5000, 4000))!;
        document.Add(raft);

        // The walls' outside faces, 165 out, and 150 beyond: 10.63 by 8.63 m.
        Assert.InRange(Polygon2D.Area(raft.Outline), 10630.0 * 8630 - 1000, 10630.0 * 8630 + 1000);
        Assert.Empty(Rafts.Thickenings(document, raft));
        var band = Polygon2D.Area(raft.Outline) - 9730.0 * 7730;
        Assert.InRange(Rafts.Volume(document, raft) / (Polygon2D.Area(raft.Outline) * 200 + band * 400), 0.999, 1.001);
        Assert.Equal(5, Rafts.Meshes(document, raft).Count);
        Assert.Equal(600, Rafts.EdgeBelowGround(document, raft), 3);
        Assert.Empty(StructureRules.Problems(document, raft, document.ProjectInformation.ClearanceStandard));

        // A142 is too little in a 200 slab; an edge 400 down is in the frost; no steel at all is no raft.
        raft.BottomReinforcement = "A142";
        raft.EdgeDepth = 400;
        var problems = StructureRules.Problems(document, raft, document.ProjectInformation.ClearanceStandard);
        Assert.Contains(problems, problem => problem.Rule.Contains("9.2.1.1") && problem.Said.Contains("bottom"));
        Assert.Contains(problems, problem => problem.Rule.Contains("2E4"));
        raft.TopReinforcement = raft.BottomReinforcement = string.Empty;
        Assert.Contains(StructureRules.Problems(document, raft, document.ProjectInformation.ClearanceStandard), problem => problem.Said.Contains("no steel"));

        // In the US: a 4 in slab with a turned-down edge 12 in wide, below the frost line.
        var (us, _) = House();
        us.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        us.ProjectInformation.FrostDepth = 762;
        var american = Rafts.Under(us, us.Levels[0].Id, new Point2D(5000, 4000))!;
        us.Add(american);
        Assert.Equal((101.6, 304.8, 762.0), (american.Thickness, american.EdgeWidth, american.EdgeDepth));
        Assert.Empty(StructureRules.Problems(us, american, ClearanceStandard.Irc));
        american.Thickness = 76.2;
        Assert.Contains(StructureRules.Problems(us, american, ClearanceStandard.Irc), problem => problem.Rule.Contains("R506.1"));
    }

    [Fact]
    public void AGroundBeamStandsOnPilesSizedForTheirShareOfTheWall()
    {
        var (document, walls) = House();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.Medium;
        var (south, east) = (walls[0], walls[1]);

        var (beam, piles) = Piles.Fit(document, south, 300, 3000);
        Assert.Equal(5, piles.Count);
        Assert.Equal(100, beam.Void);
        document.Add(beam);
        foreach (var pile in piles) document.Add(pile);

        // Each pile as long as its share asks in firm clay - the middle ones carry twice the end ones' span.
        foreach (var pile in piles)
        {
            Assert.True(Piles.Capacity(document, pile) >= Piles.Load(document, pile) - 0.5, $"{Piles.CapacitySaid(document, pile)}");
            Assert.Equal(0, pile.Length % 250, 6);
        }

        Assert.True(piles[2].Length >= piles[0].Length);
        Assert.Equal(piles[2].Along, south.Length / 2, 3);

        // The east wall's beam takes the corner pile the south one stands on, rather than another.
        var (eastBeam, eastPiles) = Piles.Fit(document, east, 300, 3000);
        Assert.Equal(3, eastPiles.Count);
        document.Add(eastBeam);
        foreach (var pile in eastPiles) document.Add(pile);
        Assert.DoesNotContain(StructureRules.Problems(document, eastBeam, ClearanceStandard.ApprovedDocumentM), problem => problem.Said.Contains("no pile"));
        Assert.Empty(StructureRules.Problems(document, beam, ClearanceStandard.ApprovedDocumentM));

        // Too little void, links too far apart, a pile too short.
        beam.Void = 50;
        beam.Links = "H8 @ 450";
        piles[2].Length = 1000;
        var problems = StructureRules.Problems(document, beam, ClearanceStandard.ApprovedDocumentM);
        Assert.Contains(problems, problem => problem.Rule.Contains("NHBC"));
        Assert.Contains(problems, problem => problem.Rule.Contains("9.2.2(6)"));
        Assert.Contains(StructureRules.Problems(document, piles[2], ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("alpha method"));

        // Not clay: not worked out.
        document.ProjectInformation.Ground = GroundType.Gravel;
        Assert.Null(Piles.Capacity(document, piles[0]));
        Assert.Contains("piling engineer", Piles.CapacitySaid(document, piles[0]));
    }

    [Fact]
    public void TheBarScheduleListsEachMembersBarsAsBs8666SetsThemOut()
    {
        var (document, walls) = House();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var (beam, piles) = Piles.Fit(document, walls[0], 300, 3000);
        document.Add(beam);
        foreach (var pile in piles) document.Add(pile);

        var lines = Reinforcement.LinesOf(document, beam, () => "01");
        var length = Piles.Outline(document, beam)![0].DistanceTo(Piles.Outline(document, beam)![1]);
        var top = lines.Single(line => line.Size == "H16" && line.Each == 2);
        Assert.Equal(("00", Reinforcement.Scheduled(length - 150)), (top.Shape, top.Length));
        var links = lines.Single(line => line.Shape == "51");
        Assert.Equal((300.0, 450.0, Reinforcement.AcrossAt(length, 200, 75)), (links.A, links.B, links.Each));

        // A pile's cage: its bars 2 m down.
        Assert.Equal((4, 2000.0), Reinforcement.LinesOf(document, piles[0], () => "02").Single() is var cage ? (cage.Each, cage.Length) : default);

        // A raft's thickening bars run round it, lapped where they are longer than a stock bar.
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        document.Add(raft);
        var edge = Reinforcement.LinesOf(document, raft, () => "03").Single(line => line.Size == "H12" && line.Shape == "00");
        Assert.True(edge.Length <= Reinforcement.StockLength);
        Assert.True(edge.Each >= 4 * 3, "four bars round, each in three or more lengths");

        // The fabric: A393 top and bottom, in 4.8 by 2.4 m sheets lapped 200.
        var fabric = Reinforcement.FabricOf(document);
        Assert.Equal(2, fabric.Count);
        Assert.Equal((int)Math.Ceiling(Polygon2D.Area(raft.Outline) / (4600.0 * 2200)), fabric[0].Sheets);

        // Marked one after another, member by member, weighed and put on a sheet.
        var schedule = Reinforcement.Schedule(document);
        Assert.Equal(schedule.Select((line, i) => (i + 1).ToString("00")), schedule.Select(line => line.Mark));
        Assert.True(Reinforcement.Mass(schedule) > 100);
        var table = Reinforcement.Table(document);
        Assert.Equal("Bar Schedule - Foundations", table.Title);
        Assert.Contains(table.Lines, line => line.Cells.Count > 0 && line.Cells[0] == "H16 in all");
        var viewport = new Viewport { View = Reinforcement.View() };
        Assert.True(viewport.IsTable);
        Assert.NotNull(viewport.Table(document));
        Assert.True(Reinforcement.View().ExistsIn(document));
    }

    [Fact]
    public void SectionsCutTheBarsAndTheCostPlanWeighsThem()
    {
        var (document, walls) = House();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var (beam, piles) = Piles.Fit(document, walls[0], 300, 3000);
        document.Add(beam);
        foreach (var pile in piles) document.Add(pile);

        // Across the south beam, between two links: its two top bars and three bottom ones, each a dot.
        var marker = new SectionMarker { LevelId = document.Levels[0].Id, Start = new Point2D(4090, -1500), End = new Point2D(4090, 1500) };
        document.Add(marker);
        var dots = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.Description == "Reinforcement" && piece.ElementId == beam.Id).ToList();
        Assert.True(dots.Count >= 5, $"{dots.Count} bars cut");
        Assert.All(dots.Where(dot => dot.Bounds.Width < 20), dot => Assert.InRange(dot.Bounds.Height, 15, 17));
        Assert.Contains(SectionProjection.Build(document, marker).Pieces, piece => piece.ElementId == beam.Id && piece.Description != "Reinforcement");

        // The beam's concrete by the cubic metre, the piles by the metre, the steel by the kilogram.
        var costs = CostPlanning.Lines(document);
        Assert.Contains(costs, line => line.Element == beam && line.Unit == CostUnit.Volume && line.Rate == 190m);
        Assert.Contains(costs, line => line.Element == piles[0] && line.Unit == CostUnit.Length && line.Rate == 85m);
        var steel = costs.Single(line => line.Element == beam && line.Unit == CostUnit.Mass);
        Assert.Equal(Math.Round(Reinforcement.Mass(Reinforcement.LinesOf(document, beam, () => string.Empty)), 1), steel.Quantity, 1);
        Assert.Equal("1.1", steel.Code);
    }

    [Fact]
    public void RaftsBeamsAndPilesAreSavedCopiedMovedAndExported()
    {
        var (document, walls) = House();
        document.ProjectInformation.Ground = GroundType.StiffClay;
        document.ProjectInformation.ClayShrinkage = ClayShrinkage.High;
        var south = walls[0];
        var (beam, piles) = Piles.Fit(document, south, 300, 3000);
        beam.TopBars = "2 H20";
        document.Add(beam);
        foreach (var pile in piles) document.Add(pile);
        var lone = Piles.Single(document, document.Levels[0].Id, new Point2D(5000, 4000), 450, 6000);
        document.Add(lone);
        var raft = Rafts.Under(document, document.Levels[0].Id, new Point2D(5000, 4000))!;
        raft.EdgeBars = "6 H16";
        document.Add(raft);
        var footing = Footings.Fit(document, walls[2]);
        footing.Reinforced = true;
        footing.Bars = "4 H12";
        document.Add(footing);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        var ifc = Path.ChangeExtension(path, ".ifc");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var back = opened.All<GroundBeam>().Single();
            Assert.Equal((south.Id, "2 H20", 150.0), (back.WallId, back.TopBars, back.Void));
            Assert.Equal(piles.Select(pile => (pile.Along, pile.Length)), opened.All<Pile>().Where(pile => pile.BeamId == back.Id).Select(pile => (pile.Along, pile.Length)));
            Assert.Equal((450.0, 6000.0, (Guid?)null), opened.All<Pile>().Single(pile => pile.Id == lone.Id) is var single ? (single.Diameter, single.Length, single.BeamId) : default);
            var raftBack = opened.All<RaftFoundation>().Single();
            Assert.Equal((Polygon2D.Area(raft.Outline), "6 H16", "A393"), (Polygon2D.Area(raftBack.Outline), raftBack.EdgeBars, raftBack.TopReinforcement));
            Assert.Equal((true, "4 H12"), opened.All<WallFooting>().Single() is var strip ? (strip.Reinforced, strip.Bars) : default);

            IfcExport.Save(document, ifc);
            var text = File.ReadAllText(ifc);
            Assert.Contains("IFCPILE", text);
            Assert.Contains(".BORED.", text);
            Assert.Contains(".FOOTING_BEAM.", text);
            Assert.Contains(".BASESLAB.", text);
        }
        finally
        {
            File.Delete(path);
            File.Delete(ifc);
        }

        // A wall copied takes its beam and the piles under it; moved, they go with it; deleted, they go too.
        var copies = ElementCopy.Duplicate(document, new Element[] { south });
        var copiedBeam = copies.OfType<GroundBeam>().Single();
        Assert.Equal(copies.OfType<Wall>().Single().Id, copiedBeam.WallId);
        Assert.All(copies.OfType<Pile>(), pile => Assert.Equal(copiedBeam.Id, pile.BeamId));
        Assert.Equal(piles.Count, copies.OfType<Pile>().Count());

        var before = Piles.Position(document, piles[2]);
        var shift = new Vector2D(0, -500);
        new MoveWallCommand(south, south.Start, south.End, south.Start + shift, south.End + shift, "Move").Redo();
        Assert.Equal(before + shift, Piles.Position(document, piles[2]));

        new DeleteElementCommand(document, south).Redo();
        Assert.Empty(document.All<GroundBeam>());
        Assert.Single(document.All<Pile>());
    }
}
