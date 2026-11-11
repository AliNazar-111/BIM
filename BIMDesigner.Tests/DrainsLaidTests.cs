using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Drains as Approved Document H (2015) lays them, from its text: their cover by Table 10, the floor over them (2.23), their
/// trenches beside footings (2.25, Diagram 8), and gullies - wastewater only (H1 1.8), and every rainwater pipe through a trap
/// where surface water joins a combined sewer (H3 1.8, 3.7).
/// </summary>
public class DrainsLaidTests
{
    private static Component Put(BimDocument document, string type, double x, double y, double elevation = double.NaN, double rotation = 0)
    {
        var family = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation,
            Elevation = double.IsNaN(elevation) ? family.DefaultElevation : elevation
        };
        document.Add(component);
        return component;
    }

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double bore = 100)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside, Kind = system.Kind, SystemId = system.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static MepSystem Make(BimDocument document, SystemKind kind, params Component[] picked)
    {
        var (system, refusal) = MepSystems.Make(document, kind, picked);
        Assert.True(system is not null, refusal);
        document.Add(system!);
        return system!;
    }

    /// <summary>A foul drain 12 m long to the sewer's outfall, from one depth at its head to another at the outfall - heights from the ground floor's level 0.</summary>
    private static (BimDocument Document, MepSystem Foul, Pipe Drain) Drain(double head, double tail)
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var foul = Make(document, SystemKind.Foul, outfall);
        var drain = Run(document, new Point3D(0, 0, head), new Point3D(12000, 0, tail), foul);
        return (document, foul, drain);
    }

    private static IEnumerable<string> ProblemsWith(BimDocument document, Core.Elements.Element element) =>
        MepSystems.Problems(document).Where(problem => problem.Element == element).Select(problem => problem.Text);

    [Fact]
    public void ADrainHasTable10sCoverOrASlabOverIt()
    {
        // No floor and no surveyed ground: the ground 150 mm below the ground floor. A 100 drain, 110 outside, its middle 705 down at
        // its head: its crown 650 down, 500 of cover - under the 0.6 m Table 10 asks over a plastic drain in a garden.
        var (document, _, drain) = Drain(-705, -1000);
        Assert.Contains(ProblemsWith(document, drain), text => text.Contains("0.50 m of cover") && text.Contains("Table 10") && text.Contains("0.6 m"));

        // Level 1005 down, 1005 - 55 - 150 = 800 of cover: enough in a garden; under a parking bay near its tail, short of a drive's 0.9 m.
        var (driven, _, drivenDrain) = Drain(-1005, -1005);
        Assert.DoesNotContain(ProblemsWith(driven, drivenDrain), text => text.Contains("of cover"));
        Put(driven, driven.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.ParkingBay).Name, 10000, 0, 0);
        Assert.Contains(ProblemsWith(driven, drivenDrain), text => text.Contains("0.80 m of cover under the drive") && text.Contains("0.9 m"));

        // A concrete slab over it answers both (2.44).
        drain.Protection = DrainProtection.CoverSlab;
        drivenDrain.Protection = DrainProtection.CoverSlab;
        Assert.DoesNotContain(ProblemsWith(document, drain), text => text.Contains("of cover"));
        Assert.DoesNotContain(ProblemsWith(driven, drivenDrain), text => text.Contains("of cover"));
    }

    [Fact]
    public void ADrainUnderTheFloorIsProtectedWithin300mmOfTheSlab()
    {
        // A 250 slab, its underside 250 down; the drain's middle 450 down under it, its crown 395 down: 145 under the slab.
        var (document, _, drain) = Drain(-450, -800);
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First(type => type.Name == "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(new[] { new Point2D(-1000, -1000), new Point2D(3000, -1000), new Point2D(3000, 1000), new Point2D(-1000, 1000) });
        document.Add(floor);
        Assert.Equal(-250, floor.GetBottomElevation(document), 6);
        Assert.Contains(ProblemsWith(document, drain), text => text.Contains("145 mm below the floor slab") && text.Contains("2.23"));

        drain.Protection = DrainProtection.CoverSlab;
        Assert.DoesNotContain(ProblemsWith(document, drain), text => text.Contains("floor slab"));
    }

    /// <summary>A wall along y, from x 0 to 10 m, on a strip footing 1 m down.</summary>
    private static (WallFooting Footing, double Half) Footing(BimDocument document, double y)
    {
        var wallType = document.TypesOf<WallType>().First(type => type.Name.StartsWith("Exterior", StringComparison.Ordinal));
        var wall = new Wall { Start = new Point2D(0, y), End = new Point2D(10000, y), TypeId = wallType.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        var type = document.TypesOf<FootingType>().First();
        var footing = new WallFooting { WallId = wall.Id, TypeId = type.Id, LevelId = wall.LevelId, Depth = 1000 };
        document.Add(footing);
        return (footing, type.Width / 2);
    }

    [Fact]
    public void ADrainsTrenchBelowAFootingNearItIsFilledWithConcrete()
    {
        // The drain's middle 1500 down, its trench its 50 radius and 100 of bedding further: 1650 down, 650 below a footing's 1000.
        // Laid level, so every place of it is as deep; the footing's edge 0.5 m from it.
        var (document, _, drain) = Drain(-1500, -1500);
        var half = document.TypesOf<FootingType>().First().Width / 2;
        var (footing, _) = Footing(document, 500 + half);
        Assert.Contains(ProblemsWith(document, drain), text => text.Contains("0.65 m below a footing") && text.Contains("within 1 m") && text.Contains("2.25"));

        // Further off - its footing's edge 1.7 m from the drain - the trench may go to 1.7 - 0.15 = 1.55 m below the footing's
        // underside, 2550 down, before it needs concrete; at 2950 down it needs 400 of it.
        var (far, _, deep) = Drain(-2800, -2800);
        Footing(far, 1700 + half);
        Assert.Contains(ProblemsWith(far, deep), text => text.Contains("1.55 m below the footing") && text.Contains("0.40 m of it"));

        // Its trench filled with concrete, it is answered.
        drain.Protection = DrainProtection.ConcreteFill;
        Assert.DoesNotContain(ProblemsWith(document, drain), text => text.Contains("footing"));
        Assert.NotNull(footing);
    }

    [Fact]
    public void AGullyTakesWastewaterOnly()
    {
        // A WC's branch and a basin's, each down to the head of a drain at a gully.
        var (document, foul, _) = Drain(-600, -900);
        var wc = Put(document, "WC - Close Coupled", 0, 1500, rotation: 180);
        new EditSystemCommand(foul, "Add", system => system.Members.Add(wc.Id)).Redo();
        var soil = Connectors.Placed(document, wc).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        Run(document, soil, new Point3D(0, 0, -600), foul);
        var gully = Put(document, "Gully - Back Inlet, Trapped 100", 0, 0, 0);
        Assert.Contains(ProblemsWith(document, gully), text => text.Contains("a WC discharges into it") && text.Contains("H1 1.8"));
    }

    [Fact]
    public void RainwaterJoiningACombinedSewerGoesThroughATrap()
    {
        // The house's gutter down a downpipe into a surface water drain to a sewer that is combined.
        var house = new HouseEditAuditTests.House(false);
        var document = house.Document;
        var gutter = new Gutter { RoofId = house.Roof.Id, LevelId = house.Roof.LevelId, TypeId = document.TypesOf<GutterType>().First().Id };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, house.Roof));
        document.Add(gutter);
        var downpipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) };
        document.Add(downpipe);
        var foot = Downpipes.Path(document, downpipe)!.Foot;
        var outfall = Put(document, "Outfall - Surface Water Sewer", foot.X, foot.Y - 8000, 0);
        var rain = Make(document, SystemKind.Rainwater, outfall);
        Run(document, new Point3D(foot.X, foot.Y, -700), new Point3D(foot.X, foot.Y - 8000, -800), rain);
        Assert.DoesNotContain(ProblemsWith(document, downpipe), text => text.Contains("combined sewer"));

        // The sewer combined: its foot wants a trap; a gully there answers it.
        document.FindType<ComponentType>(outfall.TypeId)!.CombinedSewer = true;
        Assert.Contains(ProblemsWith(document, downpipe), text => text.Contains("combined sewer") && text.Contains("H3 1.8, 3.7"));
        Put(document, "Gully - Back Inlet, Trapped 100", foot.X, foot.Y, 0);
        Assert.DoesNotContain(ProblemsWith(document, downpipe), text => text.Contains("combined sewer"));
    }

    [Fact]
    public void ADrainsProtectionAndACombinedSewerAreKeptWithTheProject()
    {
        var (document, _, drain) = Drain(-700, -1000);
        drain.Protection = DrainProtection.ConcreteFill;
        var outfall = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.SurfaceWaterOutfall);
        outfall.CombinedSewer = true;
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(DrainProtection.ConcreteFill, reopened.Find<Pipe>(drain.Id)!.Protection);
        Assert.True(reopened.FindType<ComponentType>(outfall.Id)!.CombinedSewer);
        Assert.Contains(reopened.TypesOf<ComponentType>(), type => type.Form == ComponentForm.Gully);
    }
}
