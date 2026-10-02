using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using IfcWall = Xbim.Ifc4.SharedBldgElements.IfcWall;

namespace BIMDesigner.Tests;

/// <summary>Walls shaped in plan, as Archicad has them: a trapezoid thicker at one end, a polygon of any outline.</summary>
[Collection("Wpf")]
public class PolygonWallTests
{
    private static void Near(Point2D expected, Point2D actual) =>
        Assert.True(expected.DistanceTo(actual) < 1e-6, $"Expected {expected}, was {actual}.");

    [Fact]
    public void ATrapezoidIsAsThickAsItsStartAtTheStartAndItsEndAtTheEnd()
    {
        var centred = PolygonWalls.Trapezoid(new Point2D(0, 0), new Point2D(4000, 0), 400, 200, TrapezoidSide.Centre)!;
        Assert.Equal(new[] { new Point2D(0, -200), new Point2D(4000, -100), new Point2D(4000, 100), new Point2D(0, 200) }, centred);

        // The left face straight along the line, the right one splaying.
        var left = PolygonWalls.Trapezoid(new Point2D(0, 0), new Point2D(4000, 0), 400, 200, TrapezoidSide.Left)!;
        Assert.Equal(0, left[2].Y, 6);
        Assert.Equal(0, left[3].Y, 6);
        Assert.Equal(-400, left[0].Y, 6);
        Assert.Equal(-200, left[1].Y, 6);
        Assert.Equal(4000 * 300, Polygon2D.Area(left), 3);
    }

    [Fact]
    public void AnOutlineThatCrossesItselfIsRefused()
    {
        Assert.Contains("crosses", PolygonWalls.OutlineProblem(new[] { new Point2D(0, 0), new Point2D(2000, 2000), new Point2D(2000, 0), new Point2D(0, 2000) }));
        Assert.Null(PolygonWalls.OutlineProblem(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2500, 1500), new Point2D(0, 1000) }));
    }

    private static (BimDocument Document, PolygonWall Wall) Trapezoid()
    {
        var document = BimDocument.CreateDefault();
        var wall = new PolygonWall
        {
            Kind = PolygonWallKind.Trapezoid, LevelId = document.Levels[0].Id, Start = new Point2D(0, 0), End = new Point2D(4000, 0),
            StartThickness = 400, EndThickness = 200, StraightSide = TrapezoidSide.Left, UnconnectedHeight = 2500,
            MaterialId = document.Materials.First(m => m.Name == "Concrete Block").Id
        };
        wall.Shape();
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void ItIsBuiltCutCountedAndExported()
    {
        var (document, wall) = Trapezoid();

        var mesh = Assert.Single(ModelMeshBuilder.Build(document), m => m.ElementId == wall.Id);
        Assert.Equal("Concrete Block", mesh.Description);
        Assert.Equal(2500, mesh.Positions.Max(p => p.Z), 6);
        Assert.Equal(4000.0 * 300 * 2500, PolygonWalls.Volume(document, wall), 0);

        var marker = new SectionMarker { Name = "A", Start = new Point2D(1000, -2000), End = new Point2D(1000, 2000), LevelId = wall.LevelId };
        document.Add(marker);
        var cut = Assert.Single(SectionProjection.Build(document, marker).Pieces, piece => piece.ElementId == wall.Id);
        Assert.Equal(350, cut.Bounds.Width, 3);

        Assert.Contains(MaterialTakeoff.Lines(document), line => line.TypeName == "Trapezoid Wall" && Math.Abs(line.Volume - 4000.0 * 300 * 2500) < 1);

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IfcWall>(), product => product.Name == "Trapezoid Wall");
    }

    [Fact]
    public void ItIsSavedCopiedMovedAndMirrored()
    {
        var (document, wall) = Trapezoid();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<PolygonWall>().Single();
        Assert.Equal(wall.Outline, loaded.Outline);
        Assert.Equal((PolygonWallKind.Trapezoid, 400.0, 200.0, TrapezoidSide.Left), (loaded.Kind, loaded.StartThickness, loaded.EndThickness, loaded.StraightSide));

        var copy = (PolygonWall)ElementCopy.Clone(wall)!;
        Assert.Equal(wall.Outline, copy.Outline);

        new MoveElementsCommand(new[] { wall }, new Vector2D(1000, 500)).Redo();
        Near(new Point2D(1000, 500), wall.Start);

        // Mirrored, its straight face is on its other hand - still on the same side of the room.
        ElementTransforms.Mirror(wall, new Line2D(new Point2D(0, 0), new Vector2D(1, 0)));
        Assert.Equal(TrapezoidSide.Right, wall.StraightSide);
        wall.Shape();
        Assert.Equal(-500, wall.Outline.Min(p => p.Y), 6);
        Assert.Equal(-100, wall.Outline.Max(p => p.Y), 6);
    }

    [Fact]
    public void TheWallToolDrawsATrapezoidAndAPolygon()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
                plan.ActiveWallTypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Id;
                plan.SetTool(PlanTool.Wall);

                plan.DrawShape = WallShape.Trapezoid;
                plan.TrapezoidStartThickness = 500;
                plan.TrapezoidEndThickness = 250;
                plan.PlaceWallPointAt(new Point2D(0, 0));
                plan.PlaceWallPointAt(new Point2D(5000, 0));
                var trapezoid = Assert.Single(document.Elements.OfType<PolygonWall>());
                Assert.Equal(PolygonWallKind.Trapezoid, trapezoid.Kind);
                Assert.Equal(5000 * 375, Polygon2D.Area(trapezoid.Outline), 3);

                // Its material is the core of the wall type chosen: the blockwork.
                Assert.Equal("Concrete Block", document.FindMaterial(trapezoid.MaterialId)!.Name);

                plan.DrawShape = WallShape.PolygonOutline;
                foreach (var corner in new[] { new Point2D(0, 2000), new Point2D(3000, 2000), new Point2D(3500, 3500), new Point2D(0, 3000) })
                    plan.PlaceWallPointAt(corner);
                Assert.True(plan.FinishDrawing());

                var polygon = document.Elements.OfType<PolygonWall>().Last();
                Assert.Equal(PolygonWallKind.Polygon, polygon.Kind);
                Assert.Equal(4, polygon.Outline.Count);
                Assert.True(Polygon2D.SignedArea(polygon.Outline) > 0);
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
