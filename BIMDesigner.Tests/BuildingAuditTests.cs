using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Whole buildings, end to end (round 35): every load case balanced, what goes on them against a hand take-down, each plate's mesh against its area, and nothing left carrying
/// nothing. Each test here stands for a fault that a load balance alone would never have shown: a balance closes however wrong the load is.
/// </summary>
public class BuildingAuditTests
{
    private static WallType WallNamed(BimDocument d, string start) => d.TypesOf<WallType>().First(t => t.Name.StartsWith(start, StringComparison.Ordinal));

    private static Guid Roofed(BimDocument d)
    {
        var level = new Level { Name = "Roof", Elevation = 6000 };
        d.AddLevel(level);
        return level.Id;
    }

    private static Wall AddWall(BimDocument d, Point2D a, Point2D b, WallType type, Guid level, Guid top)
    {
        var wall = new Wall { Start = a, End = b, TypeId = type.Id, LevelId = level, TopLevelId = top, StructuralUsage = StructuralUsage.Bearing };
        d.Add(wall);
        return wall;
    }

    private static List<Wall> Box(BimDocument d, double w, double h, WallType type, Guid level, Guid top)
    {
        var c = new[] { new Point2D(0, 0), new Point2D(w, 0), new Point2D(w, h), new Point2D(0, h) };
        return Enumerable.Range(0, 4).Select(i => AddWall(d, c[i], c[(i + 1) % 4], type, level, top)).ToList();
    }

    private static void AddRoof(BimDocument d, double w, double h, List<Wall> walls, Guid level)
    {
        const double over = 465;
        var roof = new Roof { TypeId = d.TypesOf<RoofType>().First(t => t.Name.StartsWith("Roof - Slate on Timber", StringComparison.Ordinal)).Id, LevelId = level };
        roof.SetBoundary(new[] { new Point2D(-over, -over), new Point2D(w + over, -over), new Point2D(w + over, h + over), new Point2D(-over, h + over) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 35, WallId = walls[i].Id, OnLeftOfWall = false, Overhang = 300 }));
        d.Add(roof);
    }

    private static FloorType Slab(BimDocument d, double thick)
    {
        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var type = new FloorType($"Slab {thick:0}", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, thick)));
        d.AddType(type);
        return type;
    }

    private static Floor AddFloor(BimDocument d, FloorType type, Guid level, double x0, double y0, double x1, double y1, double? span = null)
    {
        var floor = new Floor { TypeId = type.Id, LevelId = level, SpanDirection = span };
        floor.SetBoundary(new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) });
        d.Add(floor);
        return floor;
    }

    private sealed record Audited(AnalyticalModel Model, StructuralLoading Loading, StructuralAnalysis Analysis, SecondOrderAnalysis Second)
    {
        public double Total(string name) => -Analysis.TotalLoad(Loading.Cases.Single(c => c.Name == name)).Z;
    }

    private static Audited Audit(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var analysis = StructuralAnalysis.Run(document, model, loading);
        var audited = new Audited(model, loading, analysis, SecondOrderAnalysis.Run(document, analysis));

        // Asked of every building: nothing free to move, nothing it could not mesh, every case held up.
        Assert.Empty(analysis.Problems);
        foreach (var loadCase in loading.Cases)
        {
            var (load, held) = (analysis.TotalLoad(loadCase), analysis.TotalReaction(loadCase));
            Assert.InRange(load.Z + held.Z, -0.5, 0.5);
            Assert.InRange(load.X + held.X, -0.5, 0.5);
            Assert.InRange(load.Y + held.Y, -0.5, 0.5);
        }

        return audited;
    }

    private static BimDocument JoistedHouse()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f, r) = (d.Levels[0].Id, d.Levels[1].Id, Roofed(d));
        var ext = WallNamed(d, "Exterior");
        var lower = Box(d, 10000, 8000, ext, g, f);
        var upper = Box(d, 10000, 8000, ext, f, r);
        d.Add(new Window { HostWallId = lower[0].Id, TypeId = d.TypesOf<WindowType>().First().Id, LevelId = g, DistanceAlongWall = 2500 });
        d.Add(new Window { HostWallId = upper[0].Id, TypeId = d.TypesOf<WindowType>().First().Id, LevelId = f, DistanceAlongWall = 7500 });
        AddWall(d, new Point2D(5000, 0), new Point2D(5000, 8000), WallNamed(d, "Generic"), g, f);
        foreach (var wall in d.Walls.Where(w => w.LevelId == g).ToList()) d.Add(Footings.Fit(d, wall));
        var timber = d.TypesOf<BeamType>().Where(t => t.Material == BeamMaterial.Timber).OrderBy(t => Math.Abs(t.Depth - 220)).First();
        for (var i = 1; i < 20; i++)
        {
            d.Add(new Beam { TypeId = timber.Id, LevelId = f, Start = new Point2D(0, i * 400), End = new Point2D(5000, i * 400), Usage = BeamUsage.Joist });
            d.Add(new Beam { TypeId = timber.Id, LevelId = f, Start = new Point2D(5000, i * 400), End = new Point2D(10000, i * 400), Usage = BeamUsage.Joist });
        }

        AddFloor(d, d.TypesOf<FloorType>().First(t => t.Name.StartsWith("Floor - Timber", StringComparison.Ordinal)), f, -165, -165, 10165, 8165, 90);
        AddRoof(d, 10000, 8000, upper, r);
        return d;
    }

    [Fact]
    public void ATwoStoreyJoistedHouseComesToItsHandTakeDown()
    {
        var audit = Audit(JoistedHouse());

        // Its floor, wall to wall, at 1.5 kN/m².
        Assert.InRange(audit.Total("Q1"), 0.97 * 1.5 * 82.9, 1.03 * 1.5 * 82.9);

        // Everything of its own weight: 216 m² of 140 aircrete less windows, 24 m² of 200 concrete, 38 joists.
        var walls = audit.Model.Panels.Where(p => p.Kind == AnalyticalPanelKind.Wall).Sum(p => p.Area / 1e6 * p.SelfWeight);
        var joists = audit.Model.Members.Sum(m => m.Section.Area / 1e6 * m.Length / 1000 * m.Section.Material.Density * 9.81 / 1000);
        Assert.InRange(audit.Total("G1"), 0.98 * (walls + joists), 1.02 * (walls + joists));
    }

    [Fact]
    public void EveryJoistCarriesItsShareOfTheFloor()
    {
        var audit = Audit(JoistedHouse());
        var joists = audit.Model.Members.Where(m => m.Kind == AnalyticalMemberKind.Joist).ToList();
        Assert.Equal(38, joists.Count);

        // A joist in from the ends, 400 of the floor: all on it reaches its ends, by statics, whatever else it is joined to.
        var joist = joists[5];
        var (combination, down) = audit.Second.LandingsOf(joist).First(e => e.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var along = combination.Terms.Sum(t => t.Factor * audit.Loading.LineLoads.Where(l => ReferenceEquals(l.Member, joist) && l.Case == t.Case).Sum(l => -l.Force.Z));
        var own = 1.35 * joist.Section.Area / 1e6 * joist.Section.Material.Density * 9.81 / 1000;
        var total = (along + own) * joist.Length / 1000;
        Assert.InRange(down.GetValueOrDefault(joist.Start) + down.GetValueOrDefault(joist.End), 0.97 * total, 1.03 * total);

        // 5 w L⁴ / 384 E I under all but wind, as the deflection check reads it, over its span and not a stretch of it.
        var check = audit.Analysis.MemberOf(joist, audit.Analysis.Total);
        Assert.Equal(joist.Length, check.Span, 0);
    }

    [Fact]
    public void AJoistedHouseHasNoWallFailingOnAPeakBesideAWindow()
    {
        var d = JoistedHouse();
        var audit = Audit(d);
        var walls = MasonryDesign.Of(d, audit.Second).Where(check => check.Checks.Count > 0).ToList();
        Assert.NotEmpty(walls);
        Assert.All(walls, wall => Assert.True(wall.Checks.Single(each => each.Name == "Shear in its plane").Ratio < 1, wall.Panel.Name));
    }

    private static BimDocument SteelFrame(double past)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f, r) = (d.Levels[0].Id, d.Levels[1].Id, Roofed(d));
        var post = d.TypesOf<ColumnType>().First(t => t.Structural && t.Shape == ColumnShape.IBeam);
        var ub = d.TypesOf<BeamType>().First(t => t.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var slab = Slab(d, 200);
        foreach (var x in new[] { 0.0, 6000 })
            foreach (var y in new[] { 0.0, 6000 })
                d.Add(new Column { TypeId = post.Id, LevelId = g, TopLevelId = r, Location = new Point2D(x, y), Structural = true });
        foreach (var level in new[] { f, r })
        {
            foreach (var y in new[] { 0.0, 6000 }) d.Add(new Beam { TypeId = ub.Id, LevelId = level, Start = new Point2D(0, y), End = new Point2D(6000, y), StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
            foreach (var x in new[] { 0.0, 6000 }) d.Add(new Beam { TypeId = ub.Id, LevelId = level, Start = new Point2D(x, 0), End = new Point2D(x, 6000), StartRelease = MemberRelease.Fixed, EndRelease = MemberRelease.Fixed });
            AddFloor(d, slab, level, -past, -past, 6000 + past, 6000 + past);
        }

        foreach (var column in d.All<Column>().ToList())
        {
            var pad = Footings.Fit(d, column);
            pad.Fixity = SupportFixity.Fixed;
            d.Add(pad);
        }

        return d;
    }

    [Fact]
    public void AFloorWithBeamsInsideItsEdgeMeshesToItsOwnArea()
    {
        // This one took 315 m² of triangles to mesh 41 m² of floor, and so eight times the load, every case still balancing.
        var audit = Audit(SteelFrame(200));
        foreach (var panel in audit.Model.Panels.Where(p => p.Kind == AnalyticalPanelKind.Floor))
        {
            var solver = audit.Analysis.Solver;
            var area = audit.Analysis.ShellsOf(panel).Sum(s =>
            {
                var shell = solver.Shells[s];
                return Polygon2D.Area(new[] { panel.Flat(solver.NodeAt(shell.I)), panel.Flat(solver.NodeAt(shell.J)), panel.Flat(solver.NodeAt(shell.K)) });
            });
            Assert.InRange(area / panel.Area, 0.99, 1.01);
        }

        Assert.InRange(audit.Total("Q1"), 0.98 * 1.5 * 41 * 2, 1.02 * 1.5 * 41 * 2);
    }

    [Theory]
    [InlineData(600, 600)]
    [InlineData(300, 600)]
    [InlineData(600, 300)]
    public void ATriangulationOfARegularGridHasNoOverlaps(double stepX, double stepY)
    {
        // A grid is all points on one another's circles, with a closed loop of edges inside it: the case rounding cannot answer.
        var points = new List<Point2D>();
        for (var x = 0; x <= 6000; x += (int)stepX)
            for (var y = 0; y <= 6000; y += (int)stepY)
                points.Add(new Point2D(x, y));
        foreach (var (x, y) in new[] { (-200.0, -200.0), (6200.0, -200.0), (6200.0, 6200.0), (-200.0, 6200.0) }) points.Add(new Point2D(x, y));

        int At(double x, double y) => points.FindIndex(p => p.X == x && p.Y == y);
        var constraints = new List<(int, int)>();
        for (var x = 0; x + stepX <= 6000; x += (int)stepX)
        {
            constraints.Add((At(x, 0), At(x + stepX, 0)));
            constraints.Add((At(x, 6000), At(x + stepX, 6000)));
        }

        for (var y = 0; y + stepY <= 6000; y += (int)stepY)
        {
            constraints.Add((At(0, y), At(0, y + stepY)));
            constraints.Add((At(6000, y), At(6000, y + stepY)));
        }

        var area = Triangulation.Of(points, constraints).Sum(t => Polygon2D.Area(new[] { points[t.A], points[t.B], points[t.C] }));
        Assert.InRange(area / (6400.0 * 6400), 0.999, 1.001);
    }

    [Fact]
    public void ASlabOnBeamsDoesNotPunch()
    {
        var d = SteelFrame(200);
        var audit = Audit(d);
        Assert.All(PlateDesign.Of(d, audit.Second), plate => Assert.DoesNotContain(plate.Checks, check => check.Name.StartsWith("Punching", StringComparison.Ordinal)));
    }

    [Fact]
    public void ABeamFixedAtAColumnSaysItsEndPlateIsForShearAlone()
    {
        var d = SteelFrame(200);
        var audit = Audit(d);
        var plates = JointDesign.Of(d, audit.Second).Where(joint => joint.Kind == "Steel end plate").ToList();
        Assert.NotEmpty(plates);
        Assert.All(plates, joint => Assert.Contains("shear alone", joint.Limits));
    }

    private static BimDocument FlatSlabs(int storeys)
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var levels = storeys == 2 ? new[] { d.Levels[0].Id, d.Levels[1].Id, Roofed(d) } : new[] { d.Levels[0].Id, d.Levels[1].Id };
        var post = new ColumnType("Concrete 400", ColumnShape.Rectangular, 400, 400) { Structural = true, StructuralMaterial = BeamMaterial.Concrete };
        d.AddType(post);
        var slab = Slab(d, 300);
        foreach (var x in new[] { 0.0, 7000, 14000 })
            foreach (var y in new[] { 0.0, 7000 })
                d.Add(new Column { TypeId = post.Id, LevelId = levels[0], TopLevelId = levels[^1], Location = new Point2D(x, y), Structural = true });
        foreach (var level in levels.Skip(1)) AddFloor(d, slab, level, -300, -300, 14300, 7300);
        foreach (var column in d.All<Column>().ToList()) d.Add(Footings.Fit(d, column));
        return d;
    }

    [Fact]
    public void AConcreteFrameComesToItsHandTakeDown()
    {
        var audit = Audit(FlatSlabs(1));

        // 14.6 by 7.6 m of 300 slab, and six 400 square columns 3 m tall.
        var density = audit.Model.Panels.Single(p => p.Kind == AnalyticalPanelKind.Floor).Material.Density * 9.81 / 1000;
        var by = (14.6 * 7.6 * 0.3 + 6 * 0.16 * 3) * density;
        Assert.InRange(audit.Total("G1"), 0.98 * by, 1.02 * by);
        Assert.InRange(audit.Total("Q1"), 0.98 * 1.5 * 14.6 * 7.6, 1.02 * 1.5 * 14.6 * 7.6);

        // Its two middle columns carry about twice its corners.
        var reactions = audit.Model.Supports.Select(s => audit.Analysis.ReactionOf(s, audit.Analysis.Total).Fz).OrderBy(r => r).ToList();
        Assert.InRange(reactions[^1] / reactions[0], 1.8, 2.7);
    }

    [Fact]
    public void PunchingIsWhatTheSlabPutsOnAColumnNotWhatThatColumnCarries()
    {
        // Through two floors the lower column carries both, and only one comes through this slab: the same punching force at each.
        var d = FlatSlabs(2);
        var audit = Audit(d);
        var column = audit.Model.Members.Where(m => m.Kind == AnalyticalMemberKind.Column).MaxBy(m => audit.Analysis.ReactionOf(audit.Model.Supports.First(s => ReferenceEquals(s.Node, m.Start)), audit.Analysis.Total).Fz)!;
        var lower = audit.Model.Panels.Where(p => p.Kind == AnalyticalPanelKind.Floor).MinBy(p => p.Origin.Z)!;
        var node = column.Nodes.Single(n => Math.Abs(n.At.Z - lower.Origin.Z) < 5);
        var through = audit.Second.PlateForceOn(lower, node).Max(e => e.Force);
        var inColumn = audit.Second.StationsOf(column).Max(s => -s.Forces.N);
        Assert.True(through < 0.65 * inColumn, $"{through:0} kN through the slab, {inColumn:0} kN in the column");
    }

    [Fact]
    public void AColumnThroughAFloorIsJoinedToIt()
    {
        // Nothing of either ends at the other, and without a node there the floor hung on nothing.
        var d = FlatSlabs(2);
        var model = AnalyticalModel.Of(d);
        Assert.All(model.Members.Where(m => m.Kind == AnalyticalMemberKind.Column), column => Assert.Contains(column.Nodes, node => Math.Abs(node.At.Z - 3000) < 5));
        Assert.All(model.Panels.Where(p => p.Kind == AnalyticalPanelKind.Floor), floor => Assert.Equal(6, floor.Outline.Concat(floor.Inside).Distinct().Count(node => model.Members.Any(m => m.Nodes.Contains(node)))));

        // So it holds the column there too, each storey its own length.
        var column = model.Members.First(m => m.Kind == AnalyticalMemberKind.Column);
        Assert.InRange(MemberStrength.LengthsOf(model, column).Weak, 2900, 3100);
    }

    [Fact]
    public void AColumnThroughTwoFloorsIsSizedForBoth()
    {
        var d = FlatSlabs(2);
        var column = d.All<Column>().First(c => c.Location.X == 7000);
        var one = FlatSlabs(1);
        Assert.True(Loads.OnColumn(d, column) > 1.6 * Loads.OnColumn(one, one.All<Column>().First(c => c.Location.X == 7000)));
    }

    private static (BimDocument Document, Beam Beam) OverAnOpening()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f, r) = (d.Levels[0].Id, d.Levels[1].Id, Roofed(d));
        var ext = WallNamed(d, "Exterior");

        // A garage: the front of the ground floor open under a steel beam, the first floor's wall standing on it.
        AddWall(d, new Point2D(0, 0), new Point2D(0, 6000), ext, g, f);
        AddWall(d, new Point2D(6000, 0), new Point2D(6000, 6000), ext, g, f);
        AddWall(d, new Point2D(6000, 6000), new Point2D(0, 6000), ext, g, f);
        foreach (var wall in d.Walls.ToList()) d.Add(Footings.Fit(d, wall));
        var ub = d.TypesOf<BeamType>().First(t => t.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var beam = new Beam { TypeId = ub.Id, LevelId = f, Start = new Point2D(0, 0), End = new Point2D(6000, 0) };
        d.Add(beam);
        Box(d, 6000, 6000, ext, f, r);
        AddFloor(d, Slab(d, 200), f, 0, 0, 6000, 6000);
        return (d, beam);
    }

    [Fact]
    public void ABeamUnderAWallCarriesIt()
    {
        // The wall's line is its structure's, a little off the one the beam is drawn on: unless it is taken there the wall stands on the slab's edge, and the beam holds up only itself.
        var (d, beam) = OverAnOpening();
        var audit = Audit(d);
        var member = audit.Model.Members.Single(m => m.Element == beam);
        var (combination, down) = audit.Second.LandingsOf(member).First(e => e.Combination.Name.StartsWith("ULS1", StringComparison.Ordinal));
        var wall = audit.Model.Panels.Where(p => p.Kind == AnalyticalPanelKind.Wall && p.Outline.Min(n => n.At.Z) > 2900).MinBy(p => Math.Abs(p.Origin.Y - member.Start.At.Y) + (Math.Abs(p.U.X) > 0.5 ? 0 : 1e6))!;

        // It is joined along the wall, so elastically the wall spans as a deep beam and it reads little. That wants tension masonry does not have.
        Assert.True(member.Nodes.Count > 2 || wall.Outline.Contains(member.Start));

        // So it is also made to carry all the wall brings down, by statics: at least the wall's own weight at 1.35 over 6 m, wL²/8 of that.
        var joint = JointDesign.Of(d, audit.Second).Single(each => each.Kind == "Beam under a wall");
        var line = 1.35 * wall.SelfWeight * wall.Area / 1e6 / 6.16;
        var bending = joint.Checks.First(check => check.Name.StartsWith("Bending", StringComparison.Ordinal));
        Assert.True(bending.Ratio > line * 6 * 6 / 8 / (393e3 * 355 / 1e6) * 0.9, $"{bending.Ratio:P0} of it");
        Assert.Contains("kN/m of the wall", joint.Wants);
    }

    [Fact]
    public void ABeamBesideNoWallStaysWhereItWasDrawn()
    {
        var d = JoistedHouse();
        var ub = d.TypesOf<BeamType>().First(t => t.Name.StartsWith("UB 254", StringComparison.Ordinal));
        var beam = new Beam { TypeId = ub.Id, LevelId = d.Levels[1].Id, Start = new Point2D(0, 4100), End = new Point2D(5000, 4100) };
        d.Add(beam);
        Assert.All(AnalyticalModel.Of(d).Members.Single(m => m.Element == beam).Nodes, node => Assert.Equal(4100, node.At.Y, 0));
    }

    [Fact]
    public void AnLShapedFloorIsLoadedOverItsOwnShape()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f) = (d.Levels[0].Id, d.Levels[1].Id);
        var c = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 5000), new Point2D(7000, 5000), new Point2D(7000, 9000), new Point2D(0, 9000) };
        foreach (var i in Enumerable.Range(0, c.Length)) AddWall(d, c[i], c[(i + 1) % c.Length], WallNamed(d, "Exterior"), g, f);
        foreach (var wall in d.Walls.ToList()) d.Add(Footings.Fit(d, wall));
        var floor = new Floor { TypeId = Slab(d, 200).Id, LevelId = f };
        floor.SetBoundary(c);
        d.Add(floor);
        var audit = Audit(d);
        var plate = audit.Model.Panels.Single(p => p.Kind == AnalyticalPanelKind.Floor);
        Assert.InRange(plate.Area / 1e6, 86, 96);
        Assert.InRange(audit.Total("Q1"), 0.99 * 1.5 * plate.Area / 1e6, 1.01 * 1.5 * plate.Area / 1e6);
    }

    [Fact]
    public void AnOpeningTakesItsAreaOffTheLoad()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f) = (d.Levels[0].Id, d.Levels[1].Id);
        Box(d, 10000, 8000, WallNamed(d, "Exterior"), g, f);
        foreach (var wall in d.Walls.ToList()) d.Add(Footings.Fit(d, wall));
        var floor = AddFloor(d, Slab(d, 200), f, 0, 0, 10000, 8000);
        floor.SetHoles(new[] { new[] { new Point2D(4000, 3000), new Point2D(7000, 3000), new Point2D(7000, 5000), new Point2D(4000, 5000) } });
        var audit = Audit(d);
        var plate = audit.Model.Panels.Single(p => p.Kind == AnalyticalPanelKind.Floor);
        Assert.Single(plate.Openings);
        Assert.InRange(audit.Total("Q1"), 1.5 * (10.16 * 8.16 - 6) - 2, 1.5 * (10.16 * 8.16 - 6) + 2);
    }

    [Fact]
    public void AHippedRoofsImposedLoadFallsWithItsPitch()
    {
        // 0.6 kN/m² up to 30°, falling to nothing at 60° (UK National Annex to BS EN 1991-1-1 NA.7): 0.5 at 35°, on 97.6 m².
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f) = (d.Levels[0].Id, d.Levels[1].Id);
        var walls = Box(d, 10000, 8000, WallNamed(d, "Exterior"), g, f);
        foreach (var wall in d.Walls.ToList()) d.Add(Footings.Fit(d, wall));
        const double over = 465;
        var roof = new Roof { TypeId = d.TypesOf<RoofType>().First(t => t.Name.StartsWith("Roof - Slate on Timber", StringComparison.Ordinal)).Id, LevelId = f };
        roof.SetBoundary(new[] { new Point2D(-over, -over), new Point2D(10000 + over, -over), new Point2D(10000 + over, 8000 + over), new Point2D(-over, 8000 + over) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = true, SlopeDegrees = 35, WallId = walls[i].Id, OnLeftOfWall = false, Overhang = 300 }));
        d.Add(roof);
        var audit = Audit(d);
        Assert.InRange(audit.Total("Q2"), 0.99 * 0.5 * 97.6, 1.01 * 0.5 * 97.6);
    }

    [Fact]
    public void AWallOffTheOneBelowStandsOnTheSlabAndReachesTheGround()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f, r) = (d.Levels[0].Id, d.Levels[1].Id, Roofed(d));
        Box(d, 10000, 8000, WallNamed(d, "Exterior"), g, f);
        Box(d, 10000, 8000, WallNamed(d, "Exterior"), f, r);
        AddWall(d, new Point2D(5000, 0), new Point2D(5000, 8000), WallNamed(d, "Generic"), g, f);
        AddWall(d, new Point2D(6000, 0), new Point2D(6000, 8000), WallNamed(d, "Generic"), f, r);
        foreach (var wall in d.Walls.Where(w => w.LevelId == g).ToList()) d.Add(Footings.Fit(d, wall));
        AddFloor(d, Slab(d, 200), f, 0, 0, 10000, 8000);
        AddFloor(d, Slab(d, 150), r, 0, 0, 10000, 8000);
        var audit = Audit(d);
        Assert.InRange(audit.Total("Q1"), 0.99 * 1.5 * 2 * 82.9, 1.01 * 1.5 * 2 * 82.9);
    }

    private static BimDocument OnARaft()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f, r) = (d.Levels[0].Id, d.Levels[1].Id, Roofed(d));
        var ext = WallNamed(d, "Exterior");
        Box(d, 10000, 8000, ext, g, f);
        var upper = Box(d, 10000, 8000, ext, f, r);
        AddWall(d, new Point2D(5000, 0), new Point2D(5000, 8000), WallNamed(d, "Generic"), g, f);
        d.Add(Rafts.Under(d, g, new Point2D(2500, 4000))!);
        AddFloor(d, Slab(d, 200), f, -165, -165, 10165, 8165);
        AddRoof(d, 10000, 8000, upper, r);
        return d;
    }

    [Fact]
    public void AHouseOnARaftStandsOnTheGroundAlone()
    {
        var d = OnARaft();
        var audit = Audit(d);
        Assert.Empty(audit.Model.Supports);
        var raft = audit.Model.Panels.Single(p => p.Kind == AnalyticalPanelKind.Raft);
        var (held, load) = (audit.Analysis.GroundOf(raft, audit.Analysis.Total).Fz, -audit.Analysis.TotalLoad(audit.Analysis.Total).Z);
        Assert.InRange(held, 0.99 * load, 1.01 * load);
        Assert.All(PlateDesign.Of(d, audit.Second).Where(p => p.Panel.Kind == AnalyticalPanelKind.Floor), plate => Assert.True(plate.Enough, plate.Worst?.Name));
    }

    [Fact]
    public void NoWallReadsAsHoldingNothing()
    {
        // A rigid joint no longer takes a wall to no strength at all: what it can carry is small and said.
        var d = OnARaft();
        var audit = Audit(d);
        Assert.All(MasonryDesign.Of(d, audit.Second).Where(check => check.Checks.Count > 0), wall => Assert.True(double.IsFinite(wall.Ratio), wall.Panel.Name));
    }

    [Fact]
    public void AUsTimberFrameHouseIsHeldUpAndSaysWhatItTakes()
    {
        var d = BimDocument.CreateDefault();
        d.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        d.ProjectInformation.Ground = GroundType.StiffClay;
        var (g, f) = (d.Levels[0].Id, d.Levels[1].Id);
        var lower = Box(d, 10000, 8000, WallNamed(d, "Timber Frame"), g, f);
        foreach (var wall in d.Walls.ToList()) d.Add(Footings.Fit(d, wall));
        AddRoof(d, 10000, 8000, lower, f);
        var audit = Audit(d);
        Assert.All(MasonryDesign.Of(d, audit.Second), wall => Assert.Contains("No frame is drawn", wall.Limits));
    }
}
