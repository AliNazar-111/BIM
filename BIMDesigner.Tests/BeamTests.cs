using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Beams: steel universal beams and wide flanges, C24 timber, GL24h glulam and concrete, hung
/// from their level - their top there, under the floor they carry - sloping where their ends
/// are set apart; a stair's headroom measured to their underside, and a doorway they cross
/// below its head said; built in 3D under the floor, scheduled, saved and exported.
/// </summary>
[Collection("Wpf")]
public class BeamTests
{
    private static Beam Hang(BimDocument document, string type, (double X, double Y) from, (double X, double Y) to, int level = 1)
    {
        var beam = new Beam
        {
            LevelId = document.Levels[level].Id, TypeId = document.TypesOf<BeamType>().Single(candidate => candidate.Name == type).Id,
            Start = new Point2D(from.X, from.Y), End = new Point2D(to.X, to.Y)
        };
        document.Add(beam);
        return beam;
    }

    [Fact]
    public void ItsSectionsWeighWhatTheirTablesSay()
    {
        var document = BimDocument.CreateDefault();
        BeamType Type(string name) => document.TypesOf<BeamType>().Single(type => type.Name == name);

        // A universal beam's name ends with its kilograms a metre; root radii left out, a little under.
        Assert.InRange(Type("UB 203x133x25").MassPerMetre, 24, 25.5);
        Assert.InRange(Type("UB 356x171x51").MassPerMetre, 49, 51.5);
        Assert.InRange(Type("W8x10").MassPerMetre, 14, 15.2);
        Assert.Equal(300 * 600 / 1e6 * 2400, Type("Concrete 300x600").MassPerMetre, 6);
        Assert.Equal(19, document.TypesOf<BeamType>().Count());
    }

    [Fact]
    public void ItHangsFromItsLevelAndSlopesBetweenItsEnds()
    {
        var (document, _) = House();
        var beam = Hang(document, "UB 203x133x25", (0, 2500), (6000, 2500));

        // Its top at the first floor, under its floor.
        Assert.Equal((3000.0, 3000 - 203.2), (Beams.Top(document, beam, 0), Beams.Bottom(document, beam, 0)));

        // Its middle on its line; its end raised 300, half that halfway.
        beam.Justification = BeamJustification.Centre;
        Assert.Equal(3000 + 101.6, Beams.Top(document, beam, 0), 6);
        beam.EndOffset = 300;
        Assert.Equal(3000 + 150 - 101.6, Beams.UndersideAt(document, beam, new Point2D(3000, 2500)), 6);
    }

    [Fact]
    public void AStairsHeadroomIsMeasuredToABeamOverIt()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        Assert.Empty(Stairs.Problems(document, stair));

        // A steel beam across the stairwell, under the first floor: 400 over step 12.
        var beam = Hang(document, "UB 203x133x25", (2000, 3000), (4000, 3000));
        var least = Stairs.LeastHeadroom(document, stair, Stairs.Layout(document, stair))!.Value;
        Assert.Same(beam, least.By);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("under a beam") && problem.Contains("the beam raised, or the stair moved clear of it"));

        // Moved off the stair, it is clear.
        (beam.Start, beam.End) = (new Point2D(500, 3000), new Point2D(2000, 3000));
        Assert.Empty(Stairs.Problems(document, stair));
    }

    [Fact]
    public void ItSaysWhenItCrossesADoorwayBelowItsHead()
    {
        var (document, _) = House();
        var wall = document.Walls.Single(candidate => candidate.Start == new Point2D(0, 0));
        var door = new Door { TypeId = document.TypesOf<DoorType>().First(type => type.Name.StartsWith("Single")).Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000, Mark = "D1" };
        document.Add(door);

        // Hung from the ground floor at 2200, in the wall over the doorway: its underside at 1997.
        var beam = Hang(document, "UB 203x133x25", (2000, 0), (4000, 0), level: 0);
        beam.StartOffset = beam.EndOffset = 2200;
        Assert.Contains("comes down to 1997 across the doorway of door D1, below the door's head at 2100", Beams.Verdict(document, beam));

        // Over its head, a lintel: clear.
        beam.StartOffset = beam.EndOffset = 2400;
        Assert.Equal("Clear", Beams.Verdict(document, beam));
    }

    [Fact]
    public void ItIsBuiltAsItsSectionUnderTheFloorItCarries()
    {
        var (document, floor) = House();
        var beam = Hang(document, "UB 457x191x67", (0, 2500), (6000, 2500));
        var mesh = Beams.Mesh(document, beam)!;

        // Below the floor's underside: its bottom flange and its web up to the slab; its top flange in the slab, unseen.
        Assert.Equal(2 * 12, mesh.TriangleCount);
        Assert.Equal(floor.GetBottomElevation(document), mesh.Positions.Max(point => point.Z), 6);
        Assert.Equal(3000 - 453.4, mesh.Positions.Min(point => point.Z), 6);

        // A shallow one wholly within the floor's depth is not seen at all.
        Assert.Null(Beams.Mesh(document, Hang(document, "UB 203x133x25", (0, 1000), (6000, 1000))));

        // Away from any floor, all of it: its flanges and web.
        document.Remove(floor);
        mesh = Beams.Mesh(document, beam)!;
        Assert.Equal((3 * 12, 3000.0), (mesh.TriangleCount, mesh.Positions.Max(point => point.Z)));
    }

    [Fact]
    public void ItIsSavedCopiedMirroredScheduledAndExported()
    {
        var (document, _) = House();
        var beam = Hang(document, "Glulam GL24h 140x360", (0, 2500), (6000, 2500));
        (beam.Usage, beam.Mark, beam.EndOffset) = (BeamUsage.Joist, "J1", -50);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Beam>().Single();
        Assert.Equal((beam.Start, beam.End, beam.EndOffset, BeamUsage.Joist, "J1"), (back.Start, back.End, back.EndOffset, back.Usage, back.Mark));

        var copy = (Beam)ElementCopy.Clone(beam)!;
        new MirrorElementsCommand(new[] { copy }, new Line2D(new Point2D(0, 0), new Vector2D(1, 0))).Redo();
        Assert.Equal((new Point2D(0, -2500), new Point2D(6000, -2500), -50.0), (copy.Start, copy.End, copy.EndOffset));

        var schedule = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Structural Framing Schedule"));
        var row = Assert.Single(schedule.Rows);
        Assert.Equal(6000, Schedule.Numeric(row["Length"])!.Value, 0);
        Assert.Equal("Clear", row.Text("Clearances"));

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcBeam>());
        Assert.Equal(IfcBeamTypeEnum.JOIST, exported.PredefinedType);
        Assert.Equal("J1", exported.Tag?.ToString());
    }

    [Fact]
    public void TheBeamToolPutsInAChainOfThem()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id };
                plan.SetTool(PlanTool.Beam);

                Assert.True(plan.ClickBeam(new Point2D(0, 2500)));
                Assert.True(plan.ClickBeam(new Point2D(3000, 2500)));
                Assert.True(plan.ClickBeam(new Point2D(6000, 2500)));
                var beams = document.Elements.OfType<Beam>().ToList();
                Assert.Equal(2, beams.Count);
                Assert.Equal(beams[0].End, beams[1].Start);
                Assert.All(beams, beam => Assert.Equal(document.Levels[1].Id, beam.LevelId));
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ABeamTooShallowForItsSpanIsCalledOut()
    {
        var (document, _) = House();

        // A 200 deep joist across 6 m: thirty times its depth, where timber is first sized at nineteen.
        var joist = Hang(document, "Timber C24 47x200 Joist", (0, 2500), (6000, 2500));
        Assert.Contains(Beams.Problems(document, joist), problem => problem.StartsWith("It spans 6.0 m on a section 200 deep"));

        // A 600 deep concrete beam across the same 6 m is ten times its depth: well within.
        var deep = Hang(document, "Concrete 300x600", (0, 1500), (6000, 1500));
        Assert.DoesNotContain(Beams.Problems(document, deep), problem => problem.StartsWith("It spans"));
    }
}
