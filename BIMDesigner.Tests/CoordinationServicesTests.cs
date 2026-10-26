using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using static BIMDesigner.Tests.VentilationTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Coordination and handover of the services (round M8): clash tests with their insulation, joists
/// notched and drilled within the rules, openings cut where they pass walls and floors and fire-stopped
/// as Approved Document B's Table 9.1 has it, supports at their spacings, clearances round the
/// electrics, the programme's first and second fix, builder's work costed, and pipes and ducts read back
/// from IFC on their systems.
/// </summary>
[Collection("Wpf")]
public class CoordinationServicesTests
{
    private static PipeType PipeOf(BimDocument document, PipeMaterial material) => document.TypesOf<PipeType>().First(type => type.Material == material);

    private static Pipe Run(BimDocument document, PipeMaterial material, double diameter, Point2D from, Point2D to, double height, SystemKind kind = SystemKind.ColdWater, Guid? level = null)
    {
        var pipe = new Pipe
        {
            TypeId = PipeOf(document, material).Id, LevelId = level ?? document.Levels[0].Id, Start = from, End = to, StartOffset = height, EndOffset = height, Diameter = diameter, Kind = kind
        };
        document.Add(pipe);
        return pipe;
    }

    [Fact]
    public void PipesAndDuctsAreClashTestedWithTheirInsulationAndNotAtTheirJoints()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var beam = new Beam
        {
            TypeId = document.TypesOf<BeamType>().First(type => type.Material == BeamMaterial.Steel).Id, LevelId = document.Levels[1].Id,
            Start = new Point2D(2000, 0), End = new Point2D(2000, 3000)
        };
        document.Add(beam);
        var bottom = Beams.Bottom(document, beam, 0.5) - document.Levels[0].Elevation;

        // A pipe running under the beam, 20 mm clear of it - but its 25 mm insulation runs into it.
        var pipe = Run(document, PipeMaterial.Copper, 22, new Point2D(500, 1500), new Point2D(3500, 1500), bottom - 20 - 11);
        pipe.Insulation = 25;
        var test = ClashDetective.Defaults().Single(each => each.Name == "Pipes and ducts into structure");
        Assert.True(test.AllowInsulation);
        var found = Assert.Single(ClashDetective.Run(document, test, DateTime.Now));
        Assert.Contains("insulation", found.Description);
        Assert.InRange(-found.Distance, 4, 5.5);
        pipe.Insulation = 10;
        Assert.Empty(ClashDetective.Run(document, test, DateTime.Now));

        // Two pipes of one system meeting at their ends do not clash; a pipe of another system crossing does.
        var system = new MepSystem { Kind = SystemKind.ColdWater, Name = "Cold Water 1" };
        document.Add(system);
        var next = Run(document, PipeMaterial.Copper, 22, new Point2D(3500, 1500), new Point2D(3500, 2500), pipe.StartOffset);
        (pipe.SystemId, next.SystemId) = (system.Id, system.Id);
        var each = ClashDetective.Defaults().Single(one => one.Name == "Pipes and ducts into each other");
        Assert.Empty(ClashDetective.Run(document, each, DateTime.Now));
        Run(document, PipeMaterial.Copper, 22, new Point2D(3000, 500), new Point2D(3000, 2500), pipe.StartOffset, SystemKind.HotWater);
        Assert.NotEmpty(ClashDetective.Run(document, each, DateTime.Now));
    }

    [Fact]
    public void PipesMeetingAtTheirEndsAreJoinedWhateverTheirSystemAndOnesCrossingClash()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var each = ClashDetective.Defaults().Single(one => one.Name == "Pipes and ducts into each other");

        // Two pipes on no system at an elbow, and a branch teed into the first's side: joined, not clashes.
        var first = Run(document, PipeMaterial.Copper, 22, new Point2D(500, 1500), new Point2D(3500, 1500), 2400);
        var second = Run(document, PipeMaterial.Copper, 22, new Point2D(3500, 1500), new Point2D(3500, 2500), 2400);
        var branch = Run(document, PipeMaterial.Copper, 15, new Point2D(2000, 1500), new Point2D(2000, 500), 2400);
        Assert.Empty(ClashDetective.Run(document, each, DateTime.Now));

        // All on one system, and another of it crossing the first away from any joint: that clashes.
        var system = new MepSystem { Kind = SystemKind.ColdWater, Name = "Cold Water 1" };
        document.Add(system);
        var crossing = Run(document, PipeMaterial.Copper, 22, new Point2D(1000, 500), new Point2D(1000, 2500), 2400);
        foreach (var pipe in new[] { first, second, branch, crossing }) pipe.SystemId = system.Id;
        Assert.Single(ClashDetective.Run(document, each, DateTime.Now));

        // A hot drop whose top ends inside the cold run is no joint.
        Run(document, PipeMaterial.Copper, 15, new Point2D(3000, 1500), new Point2D(3000, 1500), 2400, SystemKind.HotWater).EndOffset = 1000;
        Assert.Equal(2, ClashDetective.Run(document, each, DateTime.Now).Count);
    }

    [Fact]
    public void SteelJoistsClashWithAPipeThroughThemAndAFlatDuctWantsAHoleAsWideAsItIs()
    {
        var (document, joists) = Floor(225);
        var top = BeamSystems.Top(document, joists);
        var level = document.Levels[0].Elevation;
        var through = Run(document, PipeMaterial.Copper, 22, new Point2D(100, 1000), new Point2D(3900, 1000), top - 225.0 / 2 - level);
        var structure = ClashDetective.Defaults().Single(one => one.Name == "Pipes and ducts into structure");

        // Timber joists are drilled, and checked by their own rules; steel ones are not.
        Assert.Empty(ClashDetective.Run(document, structure, DateTime.Now));
        joists.TypeId = document.TypesOf<BeamType>().First(type => type.Material == BeamMaterial.Steel).Id;
        Assert.NotEmpty(ClashDetective.Run(document, structure, DateTime.Now));
        Assert.DoesNotContain(Notching.Cuts(document), cut => cut.Service == through);

        // A flat duct 220 wide and 54 high through 225 mm timber joists: its hole 220 across, past a quarter of the depth.
        var (house, timber) = Floor(225);
        var height = BeamSystems.Top(house, timber) - 225.0 / 2 - house.Levels[0].Elevation;
        var flat = new Duct
        {
            TypeId = house.TypesOf<DuctType>().First(type => type.Shape == DuctShape.Rectangular).Id, LevelId = house.Levels[0].Id,
            Start = new Point2D(100, 1000), End = new Point2D(3900, 1000), StartOffset = height, EndOffset = height, Width = 220, Height = 54, Kind = SystemKind.ExtractAir
        };
        house.Add(flat);
        var cut = Notching.Cuts(house).First(each => each.Service == flat);
        Assert.Equal((54.0, 220.0), (cut.Size, cut.Hole));
        Assert.Contains(Notching.Faults(cut, us: false), fault => fault.Contains("a hole 220 mm across"));
    }

    [Fact]
    public void AnOpeningIsCutLongerForAPipeAtASlantAndOneTooSmallIsSaid()
    {
        // At 45° to a 200 mm wall: the service and its gap over sin 45°, and the wall's thickness over tan 45°.
        var (document, _, partitions) = Row("Generic - 200mm", "Living Room", "Kitchen");
        var slanted = Run(document, PipeMaterial.Copper, 22, new Point2D(3000, 500), new Point2D(5000, 2500), 300);
        var hole = Assert.Single(Penetrations.Of(document, slanted));
        var thickness = document.GetWallType(partitions[0])!.Width;
        Assert.Equal((22 + 2 * Penetrations.Gap) / Math.Sqrt(0.5) + thickness, hole.Width, 3);

        // An opening there too small for it: said, and left for its size to be set.
        var (house, _, walls) = Row("Generic - 200mm", "Living Room", "Kitchen");
        var straight = Run(house, PipeMaterial.Copper, 22, new Point2D(2000, 1500), new Point2D(6000, 1500), 300);
        var small = new WallOpening { LevelId = walls[0].LevelId, HostWallId = walls[0].Id, DistanceAlongWall = 1500 - walls[0].Start.Y, Width = 15, Height = 15, SillHeight = 300 - 7.5 };
        house.Add(small);
        var through = Assert.Single(Penetrations.Of(house, straight));
        Assert.Same(small, through.Opening);
        Assert.False(through.Fits);
        Assert.Contains(MepSystems.Problems(house), problem => problem.Element == straight && problem.Text.Contains("too small for it - make it 72 x 72 mm"));
        Assert.Empty(Penetrations.Openings(house));
        (small.Width, small.Height, small.SillHeight) = (80, 80, 260);
        Assert.True(Penetrations.Of(house, straight).Single().Fits);
    }

    [Fact]
    public void APipeThroughACurvedWallIsFoundWhereverItCrossesAndGetsItsOpenings()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(each => each.Name == "Generic - 200mm");
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(4000, 0), Bulge = 0.5, TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        var crown = wall.LocationCurve.PointAt(wall.Length / 2);
        var side = Math.Sign(crown.Y);

        // Square across its crown: through it once, the hole the pipe and its gap.
        var across = Run(document, PipeMaterial.Copper, 22, new Point2D(crown.X, crown.Y - side * 1500), new Point2D(crown.X, crown.Y + side * 1500), 300);
        var hole = Assert.Single(Penetrations.Of(document, across));
        Assert.Same(wall, hole.Through);
        Assert.Equal(22 + 2 * Penetrations.Gap, hole.Width, 3);
        Assert.Equal(crown.X, hole.At.X, 1);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == across && problem.Text.Contains("no opening cut for it"));

        // Along inside its bow: in through one side and out through the other.
        var along = Run(document, PipeMaterial.Copper, 15, new Point2D(-1000, crown.Y / 2), new Point2D(5000, crown.Y / 2), 300);
        Assert.Equal(2, Penetrations.Of(document, along).Count());

        // Cut Openings: a wall opening in the curved wall at each, and each then has one that takes it.
        var openings = Penetrations.Openings(document);
        Assert.Equal(3, openings.Count);
        Assert.All(openings, opening => Assert.Equal(wall.Id, Assert.IsType<WallOpening>(opening).HostWallId));
        new Core.Documents.Commands.AddElementsCommand(document, openings, "Cut Openings").Redo();
        Assert.All(Penetrations.All(document), each => Assert.True(each.Opening is not null && each.Fits));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Text.Contains("opening"));
    }

    private static (BimDocument Document, BeamSystem Joists) Floor(double depth)
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        var type = document.TypesOf<BeamType>().First(each => each.Material == BeamMaterial.Timber).Duplicate($"Joist {depth}");
        type.Depth = depth;
        document.AddType(type);
        var joists = new BeamSystem { TypeId = type.Id, LevelId = document.Levels[1].Id, Direction = 90 };
        joists.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000), new Point2D(0, 3000) });
        document.Add(joists);
        return (document, joists);
    }

    [Fact]
    public void ServicesThroughTimberJoistsAreKeptToTheNhbcsAndTheIrcsCuts()
    {
        // 225 mm joists spanning 3 m north-south; a pipe across them 1 m from the south support - a third of the span.
        var (document, joists) = Floor(225);
        var top = BeamSystems.Top(document, joists);
        var span = BeamSystems.Joists(document, joists)[0];
        var y = 1000.0;
        var level = document.Levels[0].Elevation;

        // On the joists' centre line, 22 mm: a hole, 0.33 of the span - between 0.25 and 0.4, and under 0.25 of the depth.
        var drilled = Run(document, PipeMaterial.Copper, 22, new Point2D(100, y), new Point2D(3900, y), top - 225.0 / 2 - level);
        var cuts = Notching.Cuts(document).Where(cut => cut.Service == drilled).ToList();
        Assert.NotEmpty(cuts);
        Assert.All(cuts, cut => Assert.False(cut.Notch));
        Assert.Equal(span.From.DistanceTo(span.To), cuts[0].Span, 0);
        Assert.Empty(Notching.Faults(cuts[0], us: false));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == drilled && problem.Text.Contains("joist"));

        // Notched into the top: 22 mm deep is within 0.15 of 225 - but a third of the span is past 0.2.
        drilled.StartOffset = drilled.EndOffset = top - 11 - level;
        var notch = Notching.Cuts(document).First(cut => cut.Service == drilled);
        Assert.True(notch.Notch);
        Assert.Contains(Notching.Faults(notch, us: false), fault => fault.Contains("between 0.1 and 0.2"));

        // A 60 mm duct drilled through the middle: more than a quarter of the depth.
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.Flexible);
        var duct = new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(100, y), End = new Point2D(3900, y),
            StartOffset = top - 225.0 / 2 - level, EndOffset = top - 225.0 / 2 - level, Width = 100, Height = 100, Kind = SystemKind.SupplyAir
        };
        document.Add(duct);
        Assert.Contains(Notching.Faults(Notching.Cuts(document).First(cut => cut.Service == duct), us: false), fault => fault.Contains("0.25 of the depth"));

        // In the US: a hole of a third of the depth at most.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Contains(Notching.Faults(Notching.Cuts(document).First(cut => cut.Service == duct), us: true), fault => fault.Contains("a third of the depth"));
        Assert.Contains("R502.8.1", string.Join(" | ", MepSystems.Problems(document).Where(problem => problem.Element == duct).Select(problem => problem.Text)));

        // A joist deeper than 250 mm: an engineer designs its cuts.
        var (deep, _) = Floor(300);
        var deepTop = BeamSystems.Top(deep, deep.All<BeamSystem>().Single());
        var through = Run(deep, PipeMaterial.Copper, 15, new Point2D(100, y), new Point2D(3900, y), deepTop - 150);
        Assert.Contains(MepSystems.Problems(deep), problem => problem.Element == through && problem.Text.Contains("deeper than 250 mm"));
    }

    [Fact]
    public void OpeningsAreCutWherePipesAndDuctsPassWallsAndFloorsAndFireStoppedAsTable91Asks()
    {
        var (document, _, partitions) = Row("Party Wall - Cavity Blockwork 300mm", "Living Room", "Kitchen");
        var pipe = Run(document, PipeMaterial.PeX, 22, new Point2D(2000, 1500), new Point2D(6000, 1500), 300);
        var hole = Assert.Single(Penetrations.Of(document, pipe));
        Assert.Equal(partitions[0], hole.Through);
        Assert.True(hole.Separating);
        Assert.Equal(22 + 2 * Penetrations.Gap, hole.Width);
        var problems = MepSystems.Problems(document);
        Assert.Contains(problems, problem => problem.Element == pipe && problem.Text.Contains("no opening cut for it"));
        Assert.Contains(problems, problem => problem.Element == pipe && problem.Text.Contains("nothing keeping it fire-separating"));

        // Cut Openings: a wall opening the pipe's size and the gap round it, for the services; costed as builder's work.
        var openings = Penetrations.Openings(document);
        var opening = Assert.IsType<WallOpening>(Assert.Single(openings));
        Assert.True(opening.ForServices);
        Assert.Equal((72.0, 72.0), (opening.Width, opening.Height));
        Assert.Equal(300 - 36, opening.SillHeight, 6);
        new Core.Documents.Commands.AddElementsCommand(document, openings, "Cut Openings").Redo();
        Assert.Same(opening, Penetrations.Of(document, pipe).Single().Opening);
        Assert.Contains(CostPlanning.Lines(document), line => line.Element == opening && line.Code == "5.14");

        // Fire-stopped alone, PE-X is kept to 40 mm bore through a wall between houses; 22 mm is well within it.
        pipe.Seal = PipeSeal.FireStopped;
        Assert.Equal(40, Penetrations.FireStoppedLimit(document, pipe));
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == pipe && problem.Text.Contains("Approved Document B"));
        pipe.Seal = PipeSeal.Sleeved;
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == pipe && problem.Text.Contains("sleeves PVC-U"));
        pipe.Seal = PipeSeal.ProprietarySeal;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == pipe && problem.Text.Contains("Approved Document B"));

        // A 160 mm PVC-U drain branch fire-stopped through it: Table 9.1 takes a branch to 110 mm; sleeved, it is kept.
        var drain = Run(document, PipeMaterial.PvcU, 160, new Point2D(2000, 2500), new Point2D(6000, 2500), 200, SystemKind.Foul);
        drain.Seal = PipeSeal.FireStopped;
        Assert.Equal(110, Penetrations.FireStoppedLimit(document, drain));
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("Table 9.1"));
        drain.Seal = PipeSeal.Sleeved;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == drain && problem.Text.Contains("Approved Document B"));

        // Copper to 160 mm; a PVC-U soil stack 160, a branch 110.
        Assert.Equal(160, Penetrations.FireStoppedLimit(document, Run(document, PipeMaterial.Copper, 22, new Point2D(500, 500), new Point2D(900, 500), 300)));
        var stack = Run(document, PipeMaterial.PvcU, 110, new Point2D(500, 2500), new Point2D(500, 2500), 0, SystemKind.Foul);
        stack.EndOffset = 2500;
        Assert.Equal(160, Penetrations.FireStoppedLimit(document, stack));

        // Through a floor: a round shaft opening through it.
        var floor = document.All<Floor>().Single();
        var riser = Run(document, PipeMaterial.Copper, 22, new Point2D(1000, 1000), new Point2D(1000, 1000), -500);
        riser.EndOffset = 500;
        var shaft = Assert.IsType<ShaftOpening>(Penetrations.Openings(document).Single(each => each is ShaftOpening));
        Assert.Equal((ShaftShape.Round, 72.0), (shaft.Shape, shaft.Width));
        Assert.True(shaft.BaseElevation(document) < floor.GetBottomElevation(document) && shaft.TopElevation(document) > floor.GetTopElevation(document));
    }

    [Fact]
    public void PipesAndDuctsAreSupportedAtTheirSpacingsAndCostedWithThem()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");

        // 15 mm copper along: 1.2 m apart at most (BS 6700 Table 14) - four clips for 3.8 m, evenly spread.
        var copper = Run(document, PipeMaterial.Copper, 15, new Point2D(100, 1500), new Point2D(3900, 1500), 2400);
        Assert.Equal((1200.0, 1800.0), Supports.Spacing(document, copper) is var (along, up, _) ? (along, up) : default);
        var clips = Supports.Of(document, copper);
        Assert.Equal(4, clips.Count);
        Assert.Equal(100 + 3800 / 8.0, clips[0].At.X, 6);
        Assert.Contains("4 clips, 1.2 m apart at most along it (BS 6700 Table 14)", Supports.Describe(document, copper));

        // PE-X 22 mm: 0.5 m; PVC-U 110: 1.0 m along and 2.0 up; gas copper 15 mm: 1.5 m (BS 6891 Table 4).
        Assert.Equal(500, Supports.Spacing(document, Run(document, PipeMaterial.PeX, 22, new Point2D(100, 500), new Point2D(900, 500), 300))!.Value.Along);
        Assert.Equal((1000.0, 2000.0), Supports.Spacing(document, Run(document, PipeMaterial.PvcU, 110, new Point2D(100, 600), new Point2D(900, 600), 100, SystemKind.Foul)) is var (a, u, _) ? (a, u) : default);
        Assert.Equal(1500, Supports.Spacing(document, Run(document, PipeMaterial.Copper, 15, new Point2D(100, 700), new Point2D(900, 700), 300, SystemKind.Gas))!.Value.Along);

        // A home's plastic ducting at 750 mm; steel ductwork at 3 m (DW/144).
        var pvc = new Duct { TypeId = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.Pvc).Id, LevelId = document.Levels[0].Id, Start = new Point2D(100, 2000), End = new Point2D(3100, 2000), StartOffset = 2400, EndOffset = 2400, Width = 125, Height = 125, Kind = SystemKind.SupplyAir };
        document.Add(pvc);
        Assert.Equal(4, Supports.Of(document, pvc).Count);

        // Costed with the pipe or duct: four clips at the clip's rate.
        Assert.Contains(CostPlanning.Lines(document), line => line.Element == copper && line.Quantity == 4 && line.Rate == Supports.Rate(copper, false));

        // In the US: copper tubing to 1¼ in 6 ft along (IRC Table P2605.1).
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Equal(6 * 304.8, Supports.Spacing(document, copper)!.Value.Along, 6);
    }

    [Fact]
    public void GasPipeIsKeptFromTheElectricsAndThePanelsSpaceKeptClear()
    {
        var (document, _, _) = Row("Generic - 200mm", "Kitchen");
        var board = Put(document, "Consumer Unit - 12 Way", 300, 1500, 90);
        var gas = Run(document, PipeMaterial.Copper, 22, new Point2D(300, 500), new Point2D(300, 2500), 1400, SystemKind.Gas);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == gas && problem.Text.Contains("BS 6891 (8.4.2)"));
        gas.StartOffset = gas.EndOffset = 100;
        Assert.DoesNotContain(MepSystems.Problems(document), problem => problem.Element == gas && problem.Text.Contains("BS 6891"));

        // In the US, a water pipe over the panel within 6 ft of its top.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var water = Run(document, PipeMaterial.Copper, 15, new Point2D(300, 500), new Point2D(300, 2500), 2200);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == water && problem.Text.Contains("E3405.3"));

        // Its dedicated space runs down to the floor: a pipe passing under it as well.
        var under = Run(document, PipeMaterial.Copper, 15, new Point2D(300, 500), new Point2D(300, 2500), 300);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == under && problem.Text.Contains("from the floor to 6 ft above it"));
        _ = board;
    }

    [Fact]
    public void PipesAndDuctsAreReadBackFromIfcOnTheirSystems()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room", "Kitchen");
        var (_, supply, _, _) = Ventilate(document, rooms);
        var spiral = document.TypesOf<DuctType>().First(type => type.Material == DuctMaterial.GalvanisedSteel && type.Shape == DuctShape.Round);
        document.Add(new Duct
        {
            TypeId = spiral.Id, LevelId = document.Levels[0].Id, Start = new Point2D(1000, 1500), End = new Point2D(3500, 1500), StartOffset = 2700, EndOffset = 2700,
            Width = 160, Height = 160, Kind = SystemKind.SupplyAir, SystemId = supply.Id
        });
        var cold = new MepSystem { Kind = SystemKind.ColdWater, Name = "Cold Water 9" };
        document.Add(cold);
        var pipe = Run(document, PipeMaterial.Copper, 22, new Point2D(500, 500), new Point2D(500, 2500), 1200);
        pipe.SystemId = cold.Id;

        using var model = IfcExport.Build(document);
        var back = IfcImport.Read(model).Document;
        var duct = Assert.Single(back.All<Duct>());
        Assert.Equal((160.0, 160.0), (Math.Round(duct.Width), Math.Round(duct.Height)));
        Assert.Equal(2500, duct.Length, tolerance: 10);
        Assert.Equal(2700, duct.StartOffset, tolerance: 10);
        Assert.Equal(SystemKind.SupplyAir, duct.Kind);
        var again = Assert.Single(back.All<Pipe>());
        Assert.Equal(22, again.Diameter, 0);
        Assert.Equal(2000, again.Length, tolerance: 10);
        Assert.Equal(SystemKind.ColdWater, again.Kind);
        Assert.Equal("Cold Water 9", back.Find<MepSystem>(again.SystemId)?.Name);
    }

    [Fact]
    public void TheProgrammeRunsTheServicesFirstFixBeforeTheCeilingsAndTheSecondAfter()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room");
        var ceiling = new Ceiling { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2400 };
        ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000), new Point2D(0, 3000) });
        document.Add(ceiling);
        Run(document, PipeMaterial.Copper, 15, new Point2D(100, 1500), new Point2D(3900, 1500), 2500);
        Put(document, "Ceiling Light - Flush", 2000, 1500);
        ConstructionPlanning.Generate(document, new DateOnly(2026, 11, 2));
        var times = ConstructionPlanning.Time(document.Construction).ToDictionary(timed => timed.Task.Name);
        var ground = document.Levels[0].Name;
        Assert.True(times[$"{ground} - Services first fix"].EarlyFinish <= times[$"{ground} - Ceilings"].EarlyStart);
        Assert.True(times[$"{ground} - Ceilings"].EarlyFinish <= times[$"{ground} - Services second fix"].EarlyStart);
        _ = rooms;
    }

    [Fact]
    public void CobieListsEachSystemsComponentsAndHowTheyConnect()
    {
        var (document, rooms, _) = Row("Generic - 200mm", "Living Room", "Kitchen");
        var (unit, supply, _, valves) = Ventilate(document, rooms);
        var sheets = CobieExport.Build(document, new CobieOptions { ContactEmail = "design@example.co.uk", CreatedOn = new DateTime(2026, 10, 26, 9, 0, 0) });
        string Cell(XlsxSheet sheet, object?[] row, string column) => row[sheet.Columns.ToList().FindIndex(each => each.Heading.TrimStart('*', '>', '#', ' ') == column)]?.ToString() ?? string.Empty;

        // One row for each component on each system - its unit and its valves - classified by Uniclass's Elements/functions.
        var system = sheets.Single(sheet => sheet.Name == "System");
        var rows = system.Rows.Where(row => Cell(system, row, "Name") == supply.Name).ToList();
        Assert.Equal(1 + valves.Count(valve => supply.Members.Contains(valve.Id)), rows.Count);
        Assert.All(rows, row => Assert.StartsWith("EF_65", Cell(system, row, "Category")));
        Assert.All(rows, row => Assert.DoesNotContain(",", Cell(system, row, "ComponentNames")));

        // Connections from the unit to each supply valve, upstream first, through their connectors; no Category column on the sheet.
        var connection = sheets.Single(sheet => sheet.Name == "Connection");
        Assert.DoesNotContain(connection.Columns, column => column.Heading.Contains("Category"));
        var supplied = connection.Rows.Where(row => Cell(connection, row, "Name").StartsWith(supply.Name + ": ", StringComparison.Ordinal)).ToList();
        Assert.Equal(supply.Members.Count, supplied.Count);

        // Nothing COBie lists realizes them - the ducts are left out of it.
        Assert.All(supplied, row => Assert.Equal("n/a", Cell(connection, row, "RealizingElement")));
        Assert.All(supplied, row => Assert.Equal(("Supply", "Supply"), (Cell(connection, row, "ConnectionType"), Cell(connection, row, "PortName1"))));

        // The components' classes as IFC has them.
        var component = sheets.Single(sheet => sheet.Name == "Component");
        Assert.Contains(component.Rows, row => Cell(component, row, "ExternalObject") == "IfcAirToAirHeatRecovery");
        Assert.Contains(component.Rows, row => Cell(component, row, "ExternalObject") == "IfcAirTerminal");
        _ = unit;
    }

    [Fact]
    public void ClashTestInsulationAndPipeFireStoppingAreSaved()
    {
        var (document, _, _) = Row("Generic - 200mm", "Living Room");
        if (!document.ClashTests.Any(test => test.Name == "Pipes and ducts into structure")) document.ClashTests.AddRange(ClashDetective.Defaults());
        var pipe = Run(document, PipeMaterial.Copper, 15, new Point2D(100, 1500), new Point2D(3900, 1500), 2400);
        (pipe.Insulation, pipe.Seal) = (13, PipeSeal.ProprietarySeal);
        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(loaded.ClashTests.First(test => test.Name == "Pipes and ducts into structure").AllowInsulation);
        var again = loaded.All<Pipe>().Single();
        Assert.Equal((13.0, PipeSeal.ProprietarySeal), (again.Insulation, again.Seal));
    }
}
