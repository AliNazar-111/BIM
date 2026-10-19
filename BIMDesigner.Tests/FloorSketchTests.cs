using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.ProductExtension;

namespace BIMDesigner.Tests;

/// <summary>
/// Floors and ceilings sketched as roofs are: the Floor tool opens a sketch, walls are picked or
/// a room clicked, a loop drawn inside is a hole through it, a line picked is the way it spans -
/// and the holes and the span are the floor's own, wherever it goes.
/// </summary>
[Collection("Wpf")]
public class FloorSketchTests
{
    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    private static readonly Point2D[] Outline = { new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000) };
    private static readonly Point2D[] Void = { new(4000, 2000), new(6000, 2000), new(6000, 4000), new(4000, 4000) };

    private static (BimDocument Document, Floor Floor) Holed()
    {
        var document = BimDocument.CreateDefault();
        var floor = new Floor { LevelId = document.Levels[1].Id, TypeId = document.TypesOf<FloorType>().First().Id, SpanDirection = 90 };
        floor.SetBoundary(Outline);
        floor.SetHoles(new[] { Void.Reverse() });
        document.Add(floor);
        return (document, floor);
    }

    [Fact]
    public void AHoleInAFloorIsCutFromItsAreaItsShapeAndWhatStandsOnIt()
    {
        var (document, floor) = Holed();

        // Wound anticlockwise whichever way it was given; 60 m² less the 4 m² hole.
        Assert.True(Polygon2D.SignedArea(floor.Holes[0]) > 0);
        Assert.Equal(56e6, floor.Area, 0);
        Assert.Equal(56e6, Shafts.NetArea(document, floor), 0);
        Assert.False(floor.Contains(new Point2D(5000, 3000)));
        Assert.True(floor.Contains(new Point2D(2000, 3000)));

        // Built with the hole through it, as the plan, 3D and sections draw it.
        Assert.True(SlabEdges.IsCut(document, floor));
        var region = Assert.Single(SlabEdges.Regions(document, floor));
        Assert.Single(region.Holes);
        Assert.Equal(56e6 * (document.FindType<SlabType>(floor.TypeId)!.Thickness), floor.GetVolume(document), 0);
        Assert.Equal("90°", floor.GetInstanceParameters(document).Single(parameter => parameter.Name == "Span Direction").Value);
    }

    [Fact]
    public void ItsHolesAndSpanGoWithItSavedCopiedTurnedAndExported()
    {
        var (document, floor) = Holed();

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Floor>().Single();
        Assert.Equal(floor.Holes[0], reopened.Holes[0]);
        Assert.Equal(90, reopened.SpanDirection);

        var copy = (Floor)ElementCopy.Clone(floor)!;
        Assert.Equal(floor.Holes[0], copy.Holes[0]);
        Assert.Equal(90, copy.SpanDirection);

        // Turned a quarter, the hole goes with it and the span turns; mirrored, the span reflects.
        ElementTransforms.Rotate(floor, new Point2D(0, 0), 90);
        Assert.Contains(floor.Holes[0], point => point.DistanceTo(new Point2D(-2000, 4000)) < 1e-6);
        Assert.Equal(0, floor.SpanDirection!.Value, 6);
        floor.SpanDirection = 30;
        ElementTransforms.Mirror(floor, new Line2D(new Point2D(0, 0), new Vector2D(0, 1)));
        Assert.Equal(150, floor.SpanDirection!.Value, 6);

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IfcOpeningElement>(), opening => opening.Name == "Opening");
    }

    [Fact]
    public void TheFloorToolSketchesAFloorWithAHoleAndASpan()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var ground = document.Levels[0];
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = Outline[i], End = Outline[(i + 1) % 4], TypeId = type.Id, LevelId = ground.Id, TopLevelId = document.Levels[1].Id });
            var plan = new PlanView { Document = document, ActiveLevelId = ground.Id, History = new UndoStack() };

            // The Floor tool opens a sketch rather than laying a floor.
            plan.SetTool(PlanTool.Floor);
            Assert.True(plan.IsSketching);
            Assert.Equal(SketchTarget.Floor, plan.SketchTarget);
            Assert.Equal(RoofSketchTool.PickWalls, plan.SketchTool);

            // A click in the middle of the room takes all of it, at the walls' inside faces; level lines.
            Assert.True(plan.SketchClick(new Point2D(5000, 3000)));
            Assert.Equal(4, plan.SketchLines.Count);
            Assert.All(plan.SketchLines, line => Assert.False(line.Edge.DefinesSlope));

            // A rectangle inside it is a hole; a line picked is the way it spans.
            plan.SketchTool = RoofSketchTool.Rectangle;
            plan.SketchClick(new Point2D(4000, 2000));
            plan.SketchClick(new Point2D(6000, 4000));
            plan.SketchTool = RoofSketchTool.SpanDirection;
            Assert.True(plan.SketchClick(plan.SketchLines[0].Start.MidpointTo(plan.SketchLines[0].End)));
            Assert.NotNull(plan.SketchSpan);

            Assert.True(plan.FinishSketch());
            Assert.False(plan.IsSketching);
            var floor = Assert.Single(document.Elements.OfType<Floor>());
            Assert.Single(floor.Holes);
            Assert.Equal(4e6, Polygon2D.Area(floor.Holes[0]), 0);
            Assert.NotNull(floor.SpanDirection);
            var inside = 10000 - 2 * (type.Structure.TotalWidth / 2);
            Assert.Equal(inside * (6000 - type.Structure.TotalWidth) - 4e6, floor.Area, 0);

            // Edit Boundary opens it again with its hole, and finishing it unchanged is one step to undo.
            plan.Select(floor);
            Assert.True(plan.EditFootprint());
            Assert.Equal(SketchTarget.Floor, plan.SketchTarget);
            Assert.Same(floor, plan.SketchedSlab);
            Assert.Equal(8, plan.SketchLines.Count);
            plan.CancelSketch();
            Assert.Single(floor.Holes);

            // A ceiling is sketched the same way.
            plan.SetTool(PlanTool.Ceiling);
            Assert.Equal(SketchTarget.Ceiling, plan.SketchTarget);
            plan.SketchClick(new Point2D(5000, 3000));
            Assert.True(plan.FinishSketch());
            Assert.Single(document.Elements.OfType<Ceiling>());
        });
    }
}
