using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>
/// Valves on pipes (round 80): servicing valves, stopvalves, double check valves and drain-off taps put on a pipe and staying on it -
/// split, moved and deleted with it, saved and sent out as IFC - and checked by the Water Supply (Water Fittings) Regulations 1999,
/// Schedule 2: a servicing valve on the inlet pipe adjacent to a WC's cistern (16(2)), and a stopvalve for each of separate premises (10(1)).
/// </summary>
[Collection("Wpf")]
public class PipeValveTests
{
    private static Component Put(BimDocument document, string type, double x, double y, double rotation = 0)
    {
        var family = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation, Elevation = family.DefaultElevation
        };
        document.Add(component);
        return component;
    }

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem? system = null)
    {
        var pipe = new Pipe
        {
            TypeId = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper).Id, LevelId = document.Levels[0].Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = 15, Kind = SystemKind.ColdWater, SystemId = system?.Id
        };
        document.Add(pipe);
        return pipe;
    }

    private static void Near(Point3D expected, Point3D actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    /// <summary>
    /// Two flats side by side, rooms of separators named Flat 1 and Flat 2, a WC in each; the incoming main's stopvalve at the origin,
    /// a main from it to a tee at x = 4 m, and a branch from the tee to each WC's cistern.
    /// </summary>
    private static (BimDocument Document, MepSystem Cold, Pipe Main, Pipe ToFirst, Pipe ToSecond, Component First, Component Second) Flats()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        foreach (var (x1, y1, x2, y2) in new[] { (0.0, 1000.0, 8000.0, 1000.0), (0.0, 5000.0, 8000.0, 5000.0), (0.0, 1000.0, 0.0, 5000.0), (4000.0, 1000.0, 4000.0, 5000.0), (8000.0, 1000.0, 8000.0, 5000.0) })
            document.Add(new RoomSeparator { LevelId = level, Start = new Point2D(x1, y1), End = new Point2D(x2, y2) });
        document.Add(new Room { LevelId = level, Location = new Point2D(1000, 4500), Name = "Bathroom", Number = "1", Dwelling = "Flat 1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(5000, 4500), Name = "Bathroom", Number = "2", Dwelling = "Flat 2" });

        var stop = Put(document, "Stop Valve - Incoming Main", 0, 0);
        var first = Put(document, "WC - Close Coupled", 2000, 3000);
        var second = Put(document, "WC - Close Coupled", 6000, 3000);
        var (cold, refusal) = MepSystems.Make(document, SystemKind.ColdWater, new[] { stop, first, second });
        Assert.True(cold is not null, refusal);
        document.Add(cold!);
        var start = Connectors.Placed(document, stop).First(placed => placed.Connector.Kind == SystemKind.ColdWater).At;
        var tee = new Point3D(4000, 0, start.Z);
        var main = Run(document, start, tee, cold);
        var toFirst = Run(document, tee, Connectors.Placed(document, first).First(placed => placed.Connector.Kind == SystemKind.ColdWater).At, cold);
        var toSecond = Run(document, tee, Connectors.Placed(document, second).First(placed => placed.Connector.Kind == SystemKind.ColdWater).At, cold);
        return (document, cold!, main, toFirst, toSecond, first, second);
    }

    private static PipeValve Valve(BimDocument document, Pipe pipe, double along, ValveKind kind)
    {
        var valve = new PipeValve { PipeId = pipe.Id, LevelId = pipe.LevelId, Along = along, Kind = kind };
        new AddPipeValveCommand(document, valve).Redo();
        return valve;
    }

    [Fact]
    public void AValveStaysOnItsPipeSplitMovedAndDeleted()
    {
        var document = BimDocument.CreateDefault();
        var pipe = Run(document, new Point3D(0, 0, 300), new Point3D(2000, 0, 300));

        // Put where the pipe passes nearest the click: 1.5 m along; on an upright pipe, half way up it.
        var valve = PipeValves.For(pipe, new Point2D(1500, 120), ValveKind.Stop);
        new AddPipeValveCommand(document, valve).Redo();
        Assert.Equal(1500, valve.Along, 6);
        Near(new Point3D(1500, 0, 300), PipeValves.At(document, valve)!.Value);
        var riser = Run(document, new Point3D(0, 0, 0), new Point3D(0, 0, 2400));
        Assert.Equal(1200, PipeValves.For(riser, new Point2D(30, 30), ValveKind.Servicing).Along, 6);

        // Cut at 1 m, it goes onto the second part 0.5 m along it; undone, back on the first.
        var split = new SplitPipeCommand(document, pipe, new Point2D(1000, 0));
        split.Redo();
        Assert.Equal((split.Second.Id, 500.0), (valve.PipeId, Math.Round(valve.Along, 6)));
        Near(new Point3D(1500, 0, 300), PipeValves.At(document, valve)!.Value);
        split.Undo();
        Assert.Equal((pipe.Id, 1500.0), (valve.PipeId, Math.Round(valve.Along, 6)));

        // The pipe moved, it goes with it; deleted, it goes too, and comes back with it.
        new MoveElementsCommand(new[] { pipe }, new Vector2D(0, 500), document: document).Redo();
        Near(new Point3D(1500, 500, 300), PipeValves.At(document, valve)!.Value);
        Assert.False(ElementTransforms.CanMove(valve));
        var delete = new DeleteElementsCommand(document, new[] { pipe });
        delete.Redo();
        Assert.Null(document.Find<PipeValve>(valve.Id));
        delete.Undo();
        Assert.NotNull(document.Find<PipeValve>(valve.Id));

        // The pipe copied, its valve comes with it, on the copy.
        var copies = ElementCopy.Duplicate(document, new[] { pipe });
        var copiedPipe = copies.OfType<Pipe>().Single();
        Assert.Equal(copiedPipe.Id, copies.OfType<PipeValve>().Single().PipeId);

        // In 3D, a barrel on the pipe; its kind in Properties.
        Assert.NotNull(PipeValves.Mesh(document, valve));
        Assert.True(valve.GetInstanceParameters(document).Single(parameter => parameter.Name == "Valve").TrySet("Drain-off tap"));
        Assert.Equal(ValveKind.DrainOff, valve.Kind);
    }

    [Fact]
    public void AWcsCisternHasAServicingValveOnItsInletPipe()
    {
        // 16(2): the branch to the first WC with no valve on it - wanting; one 300 mm from the cistern - adjacent; one 1.5 m from it - not.
        var (document, _, _, toFirst, toSecond, first, second) = Flats();
        bool Wants(Component wc) => PipeValves.Problems(document).Any(problem => problem.Element == wc && problem.Text.Contains("(Schedule 2, 16(2))"));
        Assert.True(Wants(first));
        var valve = Valve(document, toFirst, toFirst.Length - 300, ValveKind.Servicing);
        Assert.False(Wants(first));
        Assert.True(Wants(second));
        valve.Along = toFirst.Length - 1500;
        Assert.True(Wants(first));

        // What it shuts off: the first WC, in Flat 1.
        Assert.Equal(new[] { first }, PipeValves.ShutsOff(document, valve));
        Assert.Equal("WC - Close Coupled - Flat 1", PipeValves.Describe(document, valve));
        _ = second;
        _ = toSecond;
    }

    [Fact]
    public void EachFlatCanBeShutOffOnItsOwn()
    {
        // 10(1): a stopvalve on the main shuts off both flats, not either alone; one on the branch to Flat 1 shuts off Flat 1 alone.
        var (document, cold, main, toFirst, toSecond, _, _) = Flats();
        IEnumerable<string> Wanting() => PipeValves.Problems(document).Where(problem => problem.Element == cold).Select(problem => problem.Text[..problem.Text.IndexOf(' ', 5)]);
        Assert.Equal(new[] { "Flat 1", "Flat 2" }, Wanting());
        Valve(document, main, 500, ValveKind.Stop);
        Assert.Equal(new[] { "Flat 1", "Flat 2" }, Wanting());
        Valve(document, toFirst, 200, ValveKind.Stop);
        Assert.Equal(new[] { "Flat 2" }, Wanting());
        const string said = "Flat 2 cannot be shut off on its own - the Water Supply (Water Fittings) Regulations 1999 (Schedule 2, 10(1)) fit a stopvalve on the pipe to each of separate premises, shutting off its supply without shutting off the others'";
        Assert.Contains(PipeValves.Problems(document), problem => problem.Text == said);

        // In the system's own problems, as in the whole model's.
        Assert.Contains(MepSystems.ProblemsOf(document, cold), problem => problem.Text == said);
        Assert.Contains(MepSystems.Problems(document), problem => problem.Element == cold && problem.Text == said);

        // A servicing valve there is not a stopvalve.
        Valve(document, toSecond, 200, ValveKind.Servicing);
        Assert.Equal(new[] { "Flat 2" }, Wanting());
        Valve(document, toSecond, 300, ValveKind.Stop);
        Assert.Empty(Wanting());
        Assert.DoesNotContain(MepSystems.ProblemsOf(document, cold), problem => problem.Text.Contains("(Schedule 2, 10(1))"));
    }

    [Fact]
    public void AValveIsSavedAndSentOutAsAnIfcValve()
    {
        var (document, _, main, toFirst, _, _, _) = Flats();
        var stop = Valve(document, main, 500, ValveKind.Stop);
        Valve(document, toFirst, toFirst.Length - 300, ValveKind.Servicing);
        stop.Mark = "SV1";

        var opened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var back = opened.Find<PipeValve>(stop.Id)!;
        Assert.Equal((main.Id, 500.0, ValveKind.Stop, "SV1"), (back.PipeId, back.Along, back.Kind, back.Mark));
        Assert.Equal(2, opened.All<PipeValve>().Count());

        using var model = IfcExport.Build(document);
        // The incoming main's stopvalve goes out as one too, a fitting of its own; the valves on the pipes by their names.
        var valves = model.Instances.OfType<IIfcValve>().Where(valve => valve.Name.ToString()!.StartsWith("Stopvalve ") || valve.Name.ToString()!.StartsWith("Servicing valve ")).ToList();
        Assert.Equal(new[] { IfcValveTypeEnum.ISOLATING, IfcValveTypeEnum.STOPCOCK }, valves.Select(valve => valve.PredefinedType!.Value).Order());
        Assert.Contains(valves, valve => valve.Name == "Stopvalve 15 x 1.0" && valve.Tag == "SV1");
        Assert.All(valves, valve => Assert.Contains(valve.HasAssignments.OfType<IIfcRelAssignsToGroup>(), relation => relation.RelatingGroup is IIfcDistributionSystem));
    }
}
