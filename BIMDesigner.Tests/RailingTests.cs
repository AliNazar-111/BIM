using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
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
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Railings: guarding drawn along a path, standing on the balcony, terrace or floor under it,
/// checked for its height where it is - 1100 round a balcony or a roof, 900 at a floor's edge
/// inside a home, the IRC's 36 in and the IBC's 42 in - and for the gaps a child's head goes
/// through; and Guard Edges, putting it up along every edge of a floor, balcony or terrace that
/// drops too far, leaving the walls, the floors it runs on into and the stairs coming up open.
/// </summary>
[Collection("Wpf")]
public class RailingTests
{
    /// <summary>The house with a balcony 2500 out off its first floor, along its east wall.</summary>
    private static (BimDocument Document, Floor Balcony) Balcony(double offset = 0)
    {
        var (document, _) = House();
        var balcony = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id, HeightOffset = offset };
        balcony.SetBoundary(new[] { new Point2D(6000, 1000), new Point2D(8500, 1000), new Point2D(8500, 4000), new Point2D(6000, 4000) });
        document.Add(balcony);
        return (document, balcony);
    }

    private static Railing Along(BimDocument document, string type, params (double X, double Y)[] points)
    {
        var railing = new Railing { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<RailingType>().Single(candidate => candidate.Name == type).Id };
        railing.SetPath(points.Select(point => new Point2D(point.X, point.Y)));
        railing.Outside = Railings.StandsOutside(document, railing);
        document.Add(railing);
        return railing;
    }

    [Fact]
    public void GuardEdgesPutsUpGuardingRoundABalconyLeavingTheWallAndTheStairOpen()
    {
        // Set down a step from the house, with a stair up its south side from the garden.
        var (document, balcony) = Balcony(-150);
        var stair = Put(document, 7250, 1000 - 14 * 225);
        Assert.Contains("of its edge drops 2850 with no guarding", Railings.EdgeVerdict(document, balcony));

        var made = Railings.GuardEdges(document, balcony, RailingBuilding.Dwelling, null, out var message);
        Assert.Contains("left open 3.0 m where a floor carries on, 0.9 m where a stair comes up", message);

        // Its east side, and its south side either side of the stair: 1100 high, outside, on the balcony.
        Assert.Equal(2, made.Count);
        Assert.All(made, railing => Assert.True(railing.Outside));
        Assert.All(made, railing => Assert.Equal(1100, Railings.TypeOf(document, railing).Height));
        Assert.Equal(800 + 750 + 2900 + 2450 - 4 * 50, made.Sum(Railings.Length), 0);
        Assert.All(made, railing => Assert.Equal(2850, Railings.Base(document, railing, railing.Path[0]), 6));
        Assert.All(made, railing => Assert.Empty(Railings.Problems(document, railing)));

        foreach (var railing in made) document.Add(railing);
        Assert.StartsWith("Its edges are guarded", Railings.EdgeVerdict(document, balcony));
        Assert.Empty(Railings.GuardEdges(document, balcony, RailingBuilding.Dwelling, null, out message));
        Assert.StartsWith("Nothing to guard", message);
        Assert.NotNull(stair);
    }

    [Fact]
    public void ARoofTerraceIsGuardedAllRoundOnItsFalls()
    {
        // A flat roof over a single storey on its own, stood on: nothing beside it but the garden, 3000 and more down.
        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[1].Id };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000), new Point2D(0, 3000) });
        roof.SetShape(RoofForm.Flat);
        document.Add(roof);

        var railing = Assert.Single(Railings.GuardEdges(document, roof, RailingBuilding.Dwelling, null, out _));
        Assert.True(railing.Closed);
        Assert.True(railing.Outside);
        Assert.Equal(2 * (3900 + 2900), Railings.Length(railing), 0);

        // It stands on the roof's top, which rises with its falls.
        var feet = Railings.Posts(railing, Railings.TypeOf(document, railing)).Select(post => Railings.Base(document, railing, post)).ToList();
        Assert.All(feet, foot => Assert.True(foot > 3000));
        Assert.Equal(roof.TopAt(document, railing.Path[0]), Railings.Base(document, railing, railing.Path[0]), 6);
    }

    [Fact]
    public void AFlatRoofReachedOnlyForItsUpkeepIsNotAskedToBeGuarded()
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = document.Levels[1].Id };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(4000, 0), new Point2D(4000, 3000), new Point2D(0, 3000) });
        roof.SetShape(RoofForm.Flat);
        document.Add(roof);
        Assert.False(Railings.WalkedOn(document, roof));
        Assert.StartsWith("A roof reached for its upkeep only", Railings.EdgeVerdict(document, roof));

        // A stair up onto it from the garden: a roof terrace, to be guarded.
        Put(document, 2000, -3500);
        Assert.True(Railings.WalkedOn(document, roof));
        Assert.Contains("of its edge drops", Railings.EdgeVerdict(document, roof));
    }

    [Fact]
    public void ItIsAsHighAsItsRulesAskWhereItIs()
    {
        var (document, _) = Balcony();
        var railing = Along(document, "Guard 900 - Timber Balusters", (8450, 1050), (8450, 3950));
        Assert.True(railing.Outside);
        Assert.Contains(Railings.Problems(document, railing), problem => problem == "It is 900 high: Approved Document K asks for 1100 round a balcony or a roof.");

        // At a floor's edge inside a home, 900 is enough.
        var inside = Along(document, "Guard 900 - Timber Balusters", (1000, 2000), (3000, 2000));
        Assert.False(inside.Outside);
        Assert.Empty(Railings.Problems(document, inside));

        // In a building other than a home, 1100 everywhere.
        inside.Building = RailingBuilding.Other;
        Assert.Contains(Railings.Problems(document, inside), problem => problem.Contains("1100 in a building other than a dwelling"));

        // The IRC's 36 in round a home; the IBC's 42 in elsewhere.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("914 (36 in)"));
        Assert.Contains(Railings.Problems(document, inside), problem => problem.Contains("1067 (42 in)"));
    }

    [Fact]
    public void NothingAChildsHeadGoesThroughAndNothingToClimb()
    {
        var (document, _) = Balcony();
        var type = document.TypesOf<RailingType>().First().Duplicate("Mine");
        document.AddType(type);
        var railing = Along(document, "Guard 1100 - Steel Balusters", (8450, 1050), (8450, 3950));
        railing.TypeId = type.Id;
        Assert.Empty(Railings.Problems(document, railing));

        (type.BalusterSpacing, type.BottomGap) = (140, 120);
        var problems = Railings.Problems(document, railing);
        Assert.Contains(problems, problem => problem.StartsWith("Its balusters are 124 apart: Approved Document K asks for less than 100"));
        Assert.Contains(problems, problem => problem.StartsWith("It stands 120 clear of the floor"));

        // Rails along: a ladder, in a home.
        (type.BalusterSpacing, type.BottomGap, type.Infill) = (110, 50, RailingInfill.HorizontalBars);
        Assert.Contains(Railings.Problems(document, railing), problem => problem.Contains("which a child can climb"));
        railing.Building = RailingBuilding.Other;
        Assert.DoesNotContain(Railings.Problems(document, railing), problem => problem.Contains("climb"));
        Assert.InRange(Railings.BarGap(type), 1, 99.9);
    }

    [Fact]
    public void ItIsBuiltWithPostsRailsAndWhatFillsIt()
    {
        var (document, _) = Balcony();
        var railing = Along(document, "Guard 1100 - Steel Balusters", (8450, 1050), (8450, 3950), (6050, 3950));
        var type = Railings.TypeOf(document, railing);

        // Two lengths, 2900 and 2400: two bays each, so five posts.
        Assert.Equal(4, Railings.Bays(railing, type).Count);
        Assert.Equal(5, Railings.Posts(railing, type).Count);

        var meshes = RailingModel.Meshes(document, railing);
        Assert.Equal(2, meshes.Count);
        Assert.InRange(meshes.Max(mesh => mesh.Positions.Max(point => point.Z)), 3000 + 1100 - 0.5, 3000 + 1100 + 0.5);

        // In glass: panels you see through.
        railing.TypeId = document.TypesOf<RailingType>().Single(candidate => candidate.Name == "Guard 1100 - Glass Panel").Id;
        Assert.Contains(RailingModel.Meshes(document, railing), mesh => mesh.Opacity < 1);
    }

    [Fact]
    public void ItIsSavedCopiedMovedScheduledAndExported()
    {
        var (document, _) = Balcony();
        var railing = Along(document, "Guard 1100 - Glass Panel", (8450, 1050), (8450, 3950), (6050, 3950));
        (railing.Mark, railing.Building) = ("R1", RailingBuilding.Other);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var saved = back.Elements.OfType<Railing>().Single();
        Assert.Equal(railing.Path, saved.Path);
        Assert.Equal((railing.TypeId, true, RailingBuilding.Other, "R1"), (saved.TypeId, saved.Outside, saved.Building, saved.Mark));
        Assert.Equal(6, back.TypesOf<RailingType>().Count());

        var copy = (Railing)ElementCopy.Clone(railing)!;
        Assert.Equal(railing.Path, copy.Path);
        Assert.Equal(railing.Building, copy.Building);
        new MoveElementsCommand(new[] { copy }, new Vector2D(100, 0)).Redo();
        Assert.Equal(new Point2D(8550, 1050), copy.Path[0]);
        new MirrorElementsCommand(new[] { copy }, new Line2D(new Point2D(0, 0), new Vector2D(0, 1))).Redo();
        Assert.Equal(new Point2D(-8550, 1050), copy.Path[0]);

        var schedule = Schedule.Run(document, ScheduleDefinition.Defaults().Single(definition => definition.Name == "Railing Schedule"));
        var row = Assert.Single(schedule.Rows);
        Assert.Equal(5300, Schedule.Numeric(row["Length"])!.Value, 0);
        Assert.Equal("Meets Approved Document K", row.Text("Regulations"));

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcRailing>());
        Assert.Equal(IfcRailingTypeEnum.BALUSTRADE, exported.PredefinedType);
        var external = model.Instances.OfType<IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(exported))
            .Select(relation => relation.RelatingPropertyDefinition).OfType<IfcPropertySet>()
            .Single(set => set.Name == "Pset_RailingCommon")
            .HasProperties.OfType<IfcPropertySingleValue>().Single(property => property.Name == "IsExternal");
        Assert.True((bool)(Xbim.Ifc4.MeasureResource.IfcBoolean)external.NominalValue!);
    }

    [Fact]
    public void AProjectFromBeforeRailingsIsGivenTheUsualTypes()
    {
        var document = BimDocument.CreateDefault();
        foreach (var type in document.TypesOf<RailingType>().ToList()) document.RemoveType(type);
        Assert.Equal(6, ProjectFile.FromJson(ProjectFile.ToJson(document)).TypesOf<RailingType>().Count());
    }

    [Fact]
    public void TheRailingToolPutsOneUpAlongTheCornersClicked()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, balcony) = Balcony();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[1].Id };
                plan.SetTool(PlanTool.Railing);

                Assert.True(plan.ClickRailing(new Point2D(6050, 1050)));
                Assert.True(plan.ClickRailing(new Point2D(8450, 1050)));
                Assert.True(plan.ClickRailing(new Point2D(8450, 3950)));
                Assert.True(plan.FinishRailing());
                var railing = Assert.Single(document.Elements.OfType<Railing>());
                Assert.Equal((3, false, true), (railing.Path.Count, railing.Closed, railing.Outside));

                // Clicking its first point again takes it all the way round.
                foreach (var point in new[] { (6100.0, 1100.0), (8400.0, 1100.0), (8400.0, 3900.0), (6100.0, 1100.0) })
                    plan.ClickRailing(new Point2D(point.Item1, point.Item2));
                Assert.Single(document.Elements.OfType<Railing>(), candidate => candidate.Closed);

                // Guard Edges on the balcony: what is left open guarded, as one step to undo.
                var history = new UndoStack();
                plan.History = history;
                document.Remove(railing);
                document.Remove(document.Elements.OfType<Railing>().Single());
                Assert.True(plan.GuardEdges(balcony));
                Assert.NotEmpty(document.Elements.OfType<Railing>());
                history.Undo();
                Assert.Empty(document.Elements.OfType<Railing>());
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
