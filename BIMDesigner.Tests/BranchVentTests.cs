using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Branch ventilating pipes (round 81), as Approved Document H (2015) has them: a branch longer than Diagram 3 allows ventilated (1.20)
/// by a pipe joined to it within 750 mm of the trap, rising all the way to the stack above the appliances' spillover level (1.22), or
/// open to the air 900 mm over any opening within 3 m (1.23), or closed with an air admittance valve; 25 mm at least (1.24). A
/// ventilating pipe carries air, so it is no way for the water to go.
/// </summary>
[Collection("Wpf")]
public class BranchVentTests
{
    private static Component Put(BimDocument document, string type, double x, double y, double elevation = double.NaN)
    {
        var family = document.TypesOf<ComponentType>().First(each => each.Name == type);
        var component = new Component
        {
            TypeId = family.Id, TypeKind = family.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y),
            Elevation = double.IsNaN(elevation) ? family.DefaultElevation : elevation
        };
        document.Add(component);
        return component;
    }

    private static Pipe Run(BimDocument document, Point3D from, Point3D to, MepSystem system, double bore, bool vent = false)
    {
        var type = document.TypesOf<PipeType>().First(each => each.Material == PipeMaterial.PvcU);
        var pipe = new Pipe
        {
            TypeId = type.Id, LevelId = document.Levels[0].Id, Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y), StartOffset = from.Z, EndOffset = to.Z,
            Diameter = PipeSizes.Of(PipeMaterial.PvcU).First(size => size.Inside == bore).Outside, Kind = system.Kind, SystemId = system.Id, Vent = vent
        };
        document.Add(pipe);
        return pipe;
    }

    /// <summary>A stack cut where something joins it at a height, so what joins meets an end of it.</summary>
    private static Point3D Tee(BimDocument document, double z)
    {
        var piece = document.All<Pipe>().First(pipe => pipe.Upright && !pipe.Vent && pipe.Start == new Point2D(0, 0) && Math.Min(pipe.StartOffset, pipe.EndOffset) < z && z < Math.Max(pipe.StartOffset, pipe.EndOffset));
        new SplitPipeCommand(document, piece, piece.Start, z).Redo();
        return new Point3D(0, 0, z);
    }

    /// <summary>
    /// A stack at the origin from a drain 600 below the ground floor to 5 m, the drain out 12 m at 1:40 to the sewer; a basin 2 m from
    /// the stack, its 32 waste dropping 50 then running 1.95 m across into the stack at 20 mm a metre - past Diagram 3's 1.7 m.
    /// </summary>
    private static (BimDocument Document, MepSystem Foul, Component Basin, Pipe Across, Point3D Waste) Basin()
    {
        var document = BimDocument.CreateDefault();
        var outfall = Put(document, "Outfall - Foul Sewer", 12000, 0, 0);
        var basin = Put(document, "Basin - Pedestal", 0, 2000);
        var (foul, refusal) = MepSystems.Make(document, SystemKind.Foul, new[] { outfall, basin });
        Assert.True(foul is not null, refusal);
        document.Add(foul!);
        var foot = new Point3D(0, 0, -600 + 12000 * 0.025);
        Run(document, foot, new Point3D(12000, 0, -600), foul!, 100);
        Run(document, foot, new Point3D(0, 0, 5000), foul!, 100);
        var waste = Connectors.Placed(document, basin).First(placed => placed.Connector.Kind == SystemKind.Foul).At;
        var bend = new Point3D(waste.X, waste.Y, waste.Z - 50);
        Run(document, waste, bend, foul!, 32);
        var plan = new Point2D(bend.X, bend.Y).DistanceTo(new Point2D(0, 0));
        var across = Run(document, bend, Tee(document, bend.Z - plan * 0.02), foul!, 32);
        return (document, foul!, basin, across, waste);
    }

    /// <summary>A vent from the branch a distance along it from the bend, up 1.5 m, then across rising to the stack - or to wherever it is sent.</summary>
    private static List<Pipe> Vent(BimDocument document, MepSystem foul, Pipe across, double along, double bore = 32, double rise = 50, double? joins = null)
    {
        var (start, end) = across.Ends(document);
        var t = along / across.Length;
        var at = new Point3D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
        new SplitPipeCommand(document, across, new Point2D(at.X, at.Y)).Redo();
        var up = new Point3D(at.X, at.Y, at.Z + 1500);
        var top = joins is { } z ? Tee(document, z) : Tee(document, up.Z + rise);
        return new List<Pipe> { Run(document, at, up, foul, bore, vent: true), Run(document, up, top, foul, bore, vent: true) };
    }

    private static List<MepProblem> Problems(BimDocument document) => MepSystems.Problems(document).ToList();

    [Fact]
    public void AVentWithin750OfTheTrapLetsALongBranchRun()
    {
        var (document, foul, basin, across, _) = Basin();
        Assert.Contains(Problems(document), problem => problem.Element == basin && problem.Text.Contains("unventilated, Approved Document H (Diagram 3, Table 2)"));

        // A 32 vent off the branch 500 along from the bend - 550 mm from the trap - up 1.5 m and across rising 50 mm into the stack, over the basin's top.
        var vent = Vent(document, foul, across, 500);
        var problems = Problems(document);
        Assert.DoesNotContain(problems, problem => problem.Element == basin && problem.Text.Contains("Diagram 3"));
        Assert.DoesNotContain(problems, problem => problem.Element is Pipe pipe && vent.Contains(pipe));

        // It carries no water: the drainage is traced without it, and it is not left unreached.
        var network = PipeNetworks.Trace(document, foul);
        Assert.All(vent, pipe => Assert.False(network.Loads.ContainsKey(pipe)));
        Assert.DoesNotContain(network.Unreached, pipe => pipe.Vent);
        Assert.Equal(1, network.Loads[network.FirstPipe[basin]].Fittings);
    }

    [Fact]
    public void AVentIsJoinedNearTheTrapRisingAllTheWayAboveTheSpilloverAndLargeEnough()
    {
        // Joined 1.2 m along: 1.25 m from the trap, past 1.22's 750 mm - and the branch is not ventilated by it.
        var (document, foul, basin, across, _) = Basin();
        var vent = Vent(document, foul, across, 1200);
        var problems = Problems(document);
        Assert.Contains(problems, problem => problem.Element == vent[0] && problem.Text == "it joins the discharge pipe 1.25 m from the trap of Basin - Pedestal - Approved Document H (1.22) joins a branch ventilating pipe within 750 mm of the trap");
        Assert.Contains(problems, problem => problem.Element == basin && problem.Text.Contains("Diagram 3"));

        // Across to the stack level: not on a continuous incline.
        (document, foul, basin, across, _) = Basin();
        vent = Vent(document, foul, across, 500, rise: 0);
        Assert.Contains(Problems(document), problem => problem.Element == vent[1] && problem.Text.Contains("runs level") && problem.Text.Contains("(1.22)"));

        // Joined to the stack below the basin's top.
        (document, foul, basin, across, var waste) = Basin();
        vent = Vent(document, foul, across, 500, joins: waste.Z + 100);
        Assert.Contains(Problems(document), problem => problem.Element == vent[0] && problem.Text.Contains("below the spillover level") && problem.Text.Contains("(1.22)"));

        // In 22 mm copper - 20 bore - smaller than 1.24's 25.
        (document, foul, basin, across, _) = Basin();
        vent = Vent(document, foul, across, 500);
        var copper = document.TypesOf<PipeType>().First(type => type.Material == PipeMaterial.Copper);
        foreach (var pipe in vent) (pipe.TypeId, pipe.Diameter) = (copper.Id, 22);
        Assert.Contains(Problems(document), problem => problem.Element == vent[0] && problem.Text == "a branch ventilating pipe of 20 - Approved Document H (1.24) asks 25 at least");
    }

    [Fact]
    public void AVentEndsAtAnAirAdmittanceValveOrInTheOpenNotInsideOpen()
    {
        // A vent rising 1.5 m from the branch and stopping, under a roof: open inside the building - wanting.
        var (document, foul, basin, across, _) = Basin();
        var (start, end) = across.Ends(document);
        var t = 500 / across.Length;
        var at = new Point3D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
        new SplitPipeCommand(document, across, new Point2D(at.X, at.Y)).Redo();
        var vent = Run(document, at, new Point3D(at.X, at.Y, at.Z + 1500), foul, 32, vent: true);
        var roof = new Roof { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<RoofType>().First().Id };
        roof.SetBoundary(new[] { new Point2D(-3000, -3000), new Point2D(3000, -3000), new Point2D(3000, 5000), new Point2D(-3000, 5000) });
        document.Add(roof);
        Assert.Contains(Problems(document), problem => problem.Element == vent && problem.Text.StartsWith("it ends inside the building, open"));
        Assert.DoesNotContain(Problems(document), problem => problem.Element == basin && problem.Text.Contains("Diagram 3"));

        // Closed with an air admittance valve at its top: ventilated inside (1.20).
        Put(document, "Air Admittance Valve - 110", at.X, at.Y, at.Z + 1500);
        Assert.DoesNotContain(Problems(document), problem => problem.Element == vent);
    }

    [Fact]
    public void AVentIsAPropertyOfAFoulPipeKeptCopiedAndSplit()
    {
        var (document, foul, _, across, _) = Basin();
        var vent = Vent(document, foul, across, 500);
        Assert.True(vent[0].GetInstanceParameters(document).Single(parameter => parameter.Name == "Ventilating Pipe").Value is true);
        Assert.True(ProjectFile.FromJson(ProjectFile.ToJson(document)).Find<Pipe>(vent[0].Id)!.Vent);
        Assert.True(((Pipe)ElementCopy.Clone(vent[0])!).Vent);
        var split = new SplitPipeCommand(document, vent[1], new Point2D((vent[1].Start.X + vent[1].End.X) / 2, (vent[1].Start.Y + vent[1].End.Y) / 2));
        Assert.True(split.Second.Vent);

        // Not offered on a water pipe.
        var cold = new Pipe { Kind = SystemKind.ColdWater, LevelId = document.Levels[0].Id };
        Assert.DoesNotContain(cold.GetInstanceParameters(document), parameter => parameter.Name == "Ventilating Pipe");
    }
}
