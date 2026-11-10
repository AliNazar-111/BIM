using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>Approved Document A 5.1d's notional removal for a Consequence Class 2b building, and key elements of 34 kN/m².</summary>
public class NotionalRemovalTests
{
    /// <summary>A flat slab office: 300 square concrete columns on a 6 m grid, two bays each way, 250 slabs 600 past them, so many storeys.</summary>
    internal static BimDocument Office(int storeys)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.BuildingType = "Office";
        d.ProjectInformation.Ground = GroundType.StiffClay;
        for (var i = 2; i <= storeys; i++) d.AddLevel(new Level { Name = $"Level {i}", Elevation = i * 3500 });
        var levels = d.Levels.OrderBy(level => level.Elevation).ToList();
        levels[1].Elevation = 3500;
        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var slab = new FloorType("Slab 250", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 250)));
        d.AddType(slab);
        var square = new ColumnType("Concrete 300", ColumnShape.Rectangular, 300, 300) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        d.AddType(square);
        for (var x = 0; x <= 12000; x += 6000)
        for (var y = 0; y <= 12000; y += 6000)
        for (var level = 0; level < storeys; level++)
            d.Add(new Column { TypeId = square.Id, LevelId = levels[level].Id, TopLevelId = levels[level + 1].Id, Location = new Point2D(x, y), Structural = true });
        for (var level = 1; level <= storeys; level++)
        {
            var floor = new Floor { TypeId = slab.Id, LevelId = levels[level].Id };
            floor.SetBoundary(new[] { new Point2D(-600, -600), new Point2D(12600, -600), new Point2D(12600, 12600), new Point2D(-600, 12600) });
            d.Add(floor);
        }

        foreach (var column in d.All<Column>().Where(c => c.LevelId == levels[0].Id).ToList()) d.Add(Footings.Fit(d, column));
        return d;
    }

    /// <summary>Five storeys of flats, 10 by 8 m, of brick on block walls round them and a cavity blockwork party wall across the middle, on 200 concrete slabs, 3 m floor to floor.</summary>
    internal static BimDocument Flats(int storeys)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.BuildingType = "Flats";
        d.ProjectInformation.Ground = GroundType.StiffClay;
        for (var i = 2; i <= storeys; i++) d.AddLevel(new Level { Name = $"Level {i}", Elevation = i * 3000 });
        var levels = d.Levels.OrderBy(level => level.Elevation).ToList();
        var outer = d.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior - Brick on Block", StringComparison.Ordinal));
        var cross = d.TypesOf<WallType>().First(t => t.Name.StartsWith("Party Wall", StringComparison.Ordinal));
        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var slab = new FloorType("Slab 200", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 200)));
        d.AddType(slab);
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var level = 0; level < storeys; level++)
        {
            for (var i = 0; i < 4; i++)
            {
                var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outer.Id, LevelId = levels[level].Id, TopLevelId = levels[level + 1].Id, StructuralUsage = StructuralUsage.Bearing };
                d.Add(wall);
                if (level == 0) d.Add(Footings.Fit(d, wall));
            }

            var middle = new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = cross.Id, LevelId = levels[level].Id, TopLevelId = levels[level + 1].Id, StructuralUsage = StructuralUsage.Bearing };
            d.Add(middle);
            if (level == 0) d.Add(Footings.Fit(d, middle));
            var floor = new Floor { TypeId = slab.Id, LevelId = levels[level + 1].Id };
            floor.SetBoundary(corners);
            d.Add(floor);
        }

        return d;
    }

    [Fact]
    public void TakingAColumnAwayIsWhatRefactorisingGives()
    {
        // The middle column of the ground storey of a two storey frame, taken away by the update over its two end nodes, and by refactorising with its stiffness a billionth: the same movement.
        var d = Office(2);
        var model = AnalyticalModel.Of(d);
        var analysis = StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model));
        var solver = analysis.Solver;
        var load = analysis.LoadOf(analysis.Loading.Cases[0]);
        var factored = solver.Factorise();
        var column = model.Members.First(m => m.Kind == AnalyticalMemberKind.Column && Math.Abs(m.Nodes[0].At.X - 6000) < 1 && Math.Abs(m.Nodes[0].At.Y - 6000) < 1 && m.Nodes.Min(n => n.At.Z) < 100);
        var beams = analysis.BeamsOf(column).Select(p => p.Beam).ToList();
        var removed = beams.ToHashSet();
        var shelled = solver.Shells.SelectMany(s => new[] { s.I, s.J, s.K }).ToHashSet();
        bool Held(int n) => Enumerable.Range(0, 6).Any(f => solver.IsRestrained(n, f)) || solver.Springs.Any(s => s.Node == n);
        bool Orphan(int n) => !Held(n) && !shelled.Contains(n) && Enumerable.Range(0, solver.Beams.Count).Where(b => solver.Beams[b].I == n || solver.Beams[b].J == n).All(removed.Contains);
        var nodes = beams.SelectMany(b => new[] { solver.Beams[b].I, solver.Beams[b].J }).Distinct().ToList();
        var (inner, boundary) = (nodes.Where(Orphan).ToList(), nodes.Where(n => !Orphan(n)).ToList());
        Assert.Equal(2, boundary.Count);

        var fast = NotionalRemoval.Damaged(solver, factored, load, beams, inner, boundary)!;
        var b0 = (double[])load.Clone();
        foreach (var n in inner) for (var f = 0; f < 6; f++) b0[n * 6 + f] = 0;
        var slow = solver.Factorise(Enumerable.Range(0, solver.Beams.Count).Select(b => removed.Contains(b) ? 1e-9 : 1.0).ToArray(), null).Solve(b0);
        var top = boundary.MaxBy(n => solver.NodeAt(n).Z);
        Assert.True(Math.Abs(slow[top * 6 + 2]) > 5 * Math.Abs(factored.Solve(load)[top * 6 + 2]));
        foreach (var n in Enumerable.Range(0, solver.NodeCount).Where(n => !inner.Contains(n)))
            Assert.InRange(fast[n * 6 + 2] - slow[n * 6 + 2], -1e-4, 1e-4);

        // A shell's moments read through its matrix are what working it out gives.
        foreach (var s in Enumerable.Range(0, solver.Shells.Count).Where(s => s % 97 == 0))
        {
            var (matrix, dofs) = solver.ShellMomentMatrix(solver.Shells[s]);
            var worked = solver.ShellMoments(solver.Shells[s], slow);
            for (var r = 0; r < 3; r++)
                Assert.Equal(worked[r], Enumerable.Range(0, 18).Sum(j => matrix[r, j] * slow[dofs[j]]), 6);
        }
    }

    [Fact]
    public void AMasonryBlockOfFlatsTakesEachWallAwayANominalLengthAtATime()
    {
        // Five storeys of flats is Class 2b. The party wall across the middle splits the front and back walls in two where it meets them; it is 8.2 m long inside the outer walls' faces and an internal
        // wall, so no more than 2.25 × 3 m = 6.75 m at a time: two lengths of 4.1 m. The ends are a length each. Eight a storey, forty in all.
        var d = Flats(5);

        // In lightweight blocks of 3.6 N/mm², K 0.75 by Table NA.4.
        foreach (var block in d.Materials.Where(material => material.Name.Contains("Block", StringComparison.Ordinal))) block.UnitStrength = 3.6;
        var model = AnalyticalModel.Of(d);
        var analysis = StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model));
        var walls = MasonryDesign.Of(d, SecondOrderAnalysis.Run(d, analysis));
        var result = NotionalRemoval.Of(d, analysis, walls)!;
        Assert.Equal(40, result.Removals.Count);
        var party = model.Panels.Single(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Outline.Min(node => node.At.Z) < 100 && Math.Abs(panel.Outline.Average(node => node.At.X) - 5000) < 200);
        var flat = party.FlatOutline;
        var lengths = NotionalRemoval.NominalLengths(party, model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList(), Array.Empty<AnalyticalMember>(),
            flat.Min(p => p.X), flat.Max(p => p.X), 0, 3000, false, 3000);
        Assert.Equal(2, lengths.Count);
        Assert.All(lengths, length => Assert.InRange(length.To - length.From, 4000, 4200));

        // Half the front wall taken from the ground storey: the wall over it spans the gap and the slabs with it, and nothing fails.
        var front = result.Removals.First(each => each.Name.Contains("Ground Floor storey", StringComparison.Ordinal) && each.Name.StartsWith("W1.1", StringComparison.Ordinal));
        Assert.True(front.Stands);
        Assert.Equal(0, front.Failed);
        Assert.Empty(front.AtRisk);

        // A whole end wall from it: the walls beside it take more than they can, and what leaned on it - more than 15% of the floor - is at risk all the way up. A wall as a key element is not
        // worked out, so it is short.
        var end = result.Removals.First(each => each.Name.StartsWith("W4,", StringComparison.Ordinal));
        Assert.False(end.Stands);
        Assert.True(end.Failed > 0);
        Assert.Equal(5, end.AtRisk.Count);
        Assert.All(end.AtRisk, at => Assert.True(at.Area > at.Limit));
        Assert.All(end.AtRisk, at => Assert.InRange(at.Limit / 1e6, 0.15 * 80, 0.15 * 86));
        Assert.Empty(end.Key);
        Assert.False(end.Enough);
    }

    [Fact]
    public void TheLimitIsFifteenPercentOrAHundredSquareMetres()
    {
        Assert.Equal(0.15 * 13.2 * 13.2 * 1e6, NotionalRemoval.Limit(13.2 * 13.2 * 1e6), 3);
        Assert.Equal(100e6, NotionalRemoval.Limit(1000e6), 3);
        Assert.Equal(34, NotionalRemoval.KeyLoad);
    }

    [Fact]
    public void AFiveStoreyFlatSlabOfficeTakesEachColumnAway()
    {
        // Five storeys of offices is Consequence Class 2b: each of 9 columns in each of 5 storeys taken away in turn.
        var d = Office(5);
        var model = AnalyticalModel.Of(d);
        var analysis = StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model));
        var result = NotionalRemoval.Of(d, analysis)!;
        Assert.Equal(45, result.Removals.Count);

        // What leaned on each: a middle column's 6 m bay, an edge column's 6 by 3.6 m with the slab's 600 past it, a corner's 3.6 by 3.6; against 15% of 13.2 by 13.2 m, 26.1 m².
        double AtRisk(string name) => result.Removals.Single(each => each.Name.StartsWith(name + ",", StringComparison.Ordinal)).AtRisk[0].Area / 1e6;
        var top = result.Removals.Where(each => each.Name.Contains("Level 4 storey", StringComparison.Ordinal)).ToList();
        Assert.Equal(9, top.Count);
        Assert.All(top, each => Assert.Single(each.AtRisk));
        var areas = top.Select(each => Math.Round(each.AtRisk[0].Area / 1e6, 1)).Distinct().OrderBy(area => area).ToList();
        Assert.Equal(new[] { 13.0, 21.6, 36.0 }, areas);
        Assert.All(top, each => Assert.Equal(26.1, each.AtRisk[0].Limit / 1e6, 1));

        // Taken from the top storey a corner or edge column's roof is within the limit and the building stands; the middle one's 36 m² is past it, so that column is a key element, and carries
        // its 34 kN/m² on each face with what it holds up.
        Assert.All(top.Where(each => each.AtRisk[0].Area < 26.1e6), each => Assert.True(each.Stands, each.Name));
        var middle = top.Single(each => each.AtRisk[0].Area > 26.1e6);
        Assert.False(middle.Stands);
        Assert.NotEmpty(middle.Key);
        Assert.True(middle.Enough);

        // From the ground storey the slabs over it cannot bridge it: the floor at risk reaches every floor above, past the two adjacent storeys, and every one is a key element.
        var ground = result.Removals.Where(each => each.Name.Contains("Ground Floor storey", StringComparison.Ordinal)).ToList();
        Assert.All(ground, each =>
        {
            Assert.True(each.Stable);
            Assert.False(each.Stands);
            Assert.Equal(5, each.AtRisk.Count);
            Assert.NotEmpty(each.Key);
        });
    }
}
