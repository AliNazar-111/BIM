using System.IO;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// What Phase 1's roofs had left (round 24): tangent and centre-ends arcs in the sketch, lines picked
/// with Lock following their wall or gridline, a roof by extrusion joined to another, a flat roof's
/// shape edited by hand for its falls, and rooms bounded by roofs by extrusion.
/// </summary>
[Collection("Wpf")]
public class RoofsFinishedTests
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

    private static (BimDocument Document, PlanView Plan) Sketching()
    {
        var document = BimDocument.CreateDefault();
        var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack(), ActiveRoofTypeId = document.TypesOf<RoofType>().First().Id };
        plan.SetTool(PlanTool.Roof);
        return (document, plan);
    }

    [Fact]
    public void ATangentArcLeavesTheLineTheWayItRuns()
    {
        // A quarter turn: leaving along x, ending up and across by the same - and the centre-ends arc round a centre.
        Assert.Equal(Math.Tan(Math.PI / 8), PlanView.TangentBulge(new Point2D(0, 0), new Vector2D(1, 0), new Point2D(2000, 2000)), 9);
        Assert.Equal(-Math.Tan(Math.PI / 8), PlanView.TangentBulge(new Point2D(0, 0), new Vector2D(1, 0), new Point2D(2000, -2000)), 9);
        var (bulge, end) = PlanView.CentreBulge(new Point2D(0, 0), new Point2D(1000, 0), new Point2D(0, 3000));
        Assert.Equal(Math.Tan(Math.PI / 8), bulge, 9);
        Assert.Equal(0, end.DistanceTo(new Point2D(0, 1000)), 6);

        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Line;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(6000, 0));

            // From the line's end, a quarter circle round to (8000, 2000), then on round the rest.
            plan.SketchTool = RoofSketchTool.TangentArc;
            Assert.True(plan.SketchClick(new Point2D(6000, 0)));
            Assert.True(plan.SketchClick(new Point2D(8000, 2000)));
            var arc = plan.SketchLines.Last();
            Assert.True(arc.IsArc);
            Assert.Equal(Math.PI * 1000, arc.Length, 0);

            plan.SketchTool = RoofSketchTool.Line;
            foreach (var point in new[] { new Point2D(8000, 2000), new Point2D(8000, 4000), new Point2D(0, 4000), new Point2D(0, 0) })
                plan.SketchClick(point);
            Assert.True(plan.FinishSketch());
            var roof = document.Elements.OfType<Roof>().Single();
            Assert.Equal(6000 * 4000 + 2000 * 2000 + Math.PI * 2000 * 2000 / 4, roof.Area, 0.01 * roof.Area);
        });
    }

    [Fact]
    public void LockedLinesFollowTheirGridAndWall()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            var level = document.Levels[0].Id;
            var wallType = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
            var half = wallType.Structure.TotalWidth / 2;
            var grids = new[]
            {
                new Grid { Name = "A", LevelId = level, Start = new Point2D(half, 0), End = new Point2D(10000, 0) },
                new Grid { Name = "B", LevelId = level, Start = new Point2D(10000, 0), End = new Point2D(10000, 6000) },
                new Grid { Name = "C", LevelId = level, Start = new Point2D(10000, 6000), End = new Point2D(half, 6000) }
            };
            foreach (var grid in grids) document.Add(grid);
            var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(0, 6000), TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000 };
            document.Add(wall);

            // Each picked with Lock: the grids' lines, and the wall's face toward them.
            plan.SketchTool = RoofSketchTool.PickLines;
            plan.SketchLock = true;
            foreach (var point in new[] { new Point2D(5000, 20), new Point2D(9980, 3000), new Point2D(5000, 5980) })
                Assert.True(plan.SketchClick(point));
            Assert.True(plan.SketchClick(new Point2D(half + 20, 3000)));
            Assert.True(plan.FinishSketch());

            var roof = document.Elements.OfType<Roof>().Single();
            Assert.Equal(3, roof.Edges.Count(edge => edge.GridId is not null));
            Assert.Single(roof.Edges, edge => edge.WallId == wall.Id);
            var width = 10000 - half;
            Assert.Equal(width * 6000, roof.Area, 1000.0);

            // Grid B moved a metre on, the wall a metre back: the roof follows both.
            grids[1].Start += new Vector2D(1000, 0);
            grids[1].End += new Vector2D(1000, 0);
            wall.Start += new Vector2D(-1000, 0);
            wall.End += new Vector2D(-1000, 0);
            Assert.True(RoofSketch.FollowWalls(document));
            Assert.Equal((width + 2000) * 6000, roof.Area, 1000.0);

            // And it is saved with the grid it follows.
            var path = Path.Combine(Path.GetTempPath(), $"locked-{Guid.NewGuid():N}.bimx");
            try
            {
                ProjectFile.Save(document, path);
                var opened = ProjectFile.Load(path).Elements.OfType<Roof>().Single();
                Assert.Equal(3, opened.Edges.Count(edge => edge.GridId is not null));
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void ARoofByExtrusionJoinsTheRoofItRunsInto()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var roofType = document.TypesOf<RoofType>().First();
        var main = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        main.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge() });
        document.Add(main);

        // A gabled roof by extrusion across the south slope, its profile 2.4 m wide, its far end short of the main roof.
        var gable = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 3600 };
        gable.SetExtrusion(new RoofExtrusion(new Point2D(3800, -500), new Vector2D(1, 0), new[] { new Point2D(0, 0), new Point2D(1200, 900), new Point2D(2400, 0) }, -1000, 0));
        document.Add(gable);
        var before = gable.Extrusion!;

        var history = new UndoStack();
        Assert.Null(RoofJoin.JoinExtrusion(document, gable, new Point2D(5000, 3000), gable, out var refused));
        Assert.NotNull(refused);
        var join = RoofJoin.JoinExtrusion(document, gable, gable.Extrusion!.At(1200, gable.Extrusion.End), main, out var problem);
        Assert.Null(problem);
        history.Execute(join!);

        // Carried on into the main roof - north, from its end facing it - and joined, so trimmed where it runs in.
        Assert.Equal(main.Id, gable.JoinedTo);
        Assert.True(gable.Extrusion!.End > before.End + 1500);
        Assert.Equal(before.Start, gable.Extrusion.Start);
        var far = gable.Extrusion.At(1200, gable.Extrusion.End - 50);
        Assert.True(main.TopAt(document, far) > gable.BaseElevation(document) + 900);
        Assert.NotEmpty(RoofSolid.Pieces(document, gable));

        history.Undo();
        Assert.Null(gable.JoinedTo);
        Assert.Equal(before.End, gable.Extrusion!.End);
    }

    /// <summary>A flat roof 10 by 6 m of the template's flat build-up.</summary>
    private static (BimDocument Document, Roof Roof) FlatRoof()
    {
        var document = BimDocument.CreateDefault();
        var flat = document.TypesOf<RoofType>().First(type => type.Name.Contains("Flat", StringComparison.OrdinalIgnoreCase));
        var roof = new Roof { TypeId = flat.Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) });
        document.Add(roof);
        return (document, roof);
    }

    [Fact]
    public void AFlatRoofsShapeIsEditedForItsFalls()
    {
        var (document, roof) = FlatRoof();
        Assert.True(RoofShapeEditing.CanShape(document, roof));
        var built = roof.TopAt(document, new Point2D(5000, 3000));
        var history = new UndoStack();

        // The north side raised 120: the roof falls to the south, its insulation thicker to the north.
        history.Execute(RoofShapeEditing.Raise(roof, new Point2D(0, 6000), 120)!);
        history.Execute(RoofShapeEditing.Raise(roof, new Point2D(10000, 6000), 120)!);
        Assert.True(RoofShapeEditing.Shaped(roof));
        Assert.Equal(built + 120, roof.TopAt(document, new Point2D(5000, 5999)), 1.0);
        Assert.Equal(built + 60, roof.TopAt(document, new Point2D(5000, 3000)), 1.0);
        Assert.Equal(built, roof.TopAt(document, new Point2D(5000, 1)), 1.0);
        Assert.Equal(2, RoofShapeEditing.Faces(roof).Count);
        Assert.Equal(new Vector2D(0, -1), RoofShapeEditing.Faces(roof)[0].Downhill);
        Assert.All(RoofDrainage.Regions(document, roof), region => Assert.Null(region.Drain));
        Assert.Contains(roof.GetInstanceParameters(document), parameter => parameter.Name == "Shape Edited" && parameter.DisplayValue.StartsWith("2 points raised", StringComparison.Ordinal));

        // A point added in the middle, raised higher: a peak, the faces meeting at it.
        history.Execute(RoofShapeEditing.Raise(roof, new Point2D(5000, 3000), 200)!);
        Assert.Equal(built + 200, roof.TopAt(document, new Point2D(5000, 3000)), 1.0);
        Assert.Equal(4, RoofShapeEditing.Faces(roof).Count);

        // A split line from the middle to a corner is an edge of the faces.
        history.Execute(RoofShapeEditing.Split(roof, new Point2D(5000, 3000), new Point2D(0, 0))!);
        Assert.Contains(RoofShapeEditing.Faces(roof), face => face.Outline.Any(point => point.DistanceTo(new Point2D(0, 0)) < 1) && face.Outline.Any(point => point.DistanceTo(new Point2D(5000, 3000)) < 1));
        Assert.Null(RoofShapeEditing.Split(roof, new Point2D(5000, 3000), new Point2D(0, 0)));

        // Moved, its shape goes with it; copied, the copy has it; saved, it is kept.
        ElementTransforms.Move(roof, new Vector2D(1000, 0));
        Assert.Equal(built + 200, roof.TopAt(document, new Point2D(6000, 3000)), 1.0);
        var copy = (Roof)ElementCopy.Clone(roof)!;
        Assert.Equal(roof.ShapePoints.Count, copy.ShapePoints.Count);
        var path = Path.Combine(Path.GetTempPath(), $"shaped-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var saved = opened.Elements.OfType<Roof>().Single();
            Assert.Equal(roof.ShapePoints.Count, saved.ShapePoints.Count);
            Assert.Single(saved.SplitLines);
            Assert.Equal(roof.TopAt(document, new Point2D(6000, 3000)), saved.TopAt(opened, new Point2D(6000, 3000)), 1.0);
        }
        finally
        {
            File.Delete(path);
        }

        // A point on an edge, between corners, is one too: the outline is broken at it.
        history.Execute(RoofShapeEditing.Raise(roof, new Point2D(6000, 6000), 300)!);
        Assert.Equal(built + 300, roof.TopAt(document, new Point2D(6000, 5999)), 2.0);
        Assert.Null(RoofShapeEditing.Raise(roof, new Point2D(6000, 7000), 300));

        // Reset, it is as built; undone, shaped again.
        history.Execute(RoofShapeEditing.Reset(roof)!);
        Assert.False(RoofShapeEditing.Shaped(roof));
        Assert.Equal(built, roof.TopAt(document, new Point2D(6000, 3000)), 1.0);
        history.Undo();
        Assert.True(RoofShapeEditing.Shaped(roof));
    }

    [Fact]
    public void ARoomIsBoundedByAVaultOverIt()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 });
        var room = new Room { LevelId = level, Location = new Point2D(3000, 2500), Name = "Studio", UpperLimitOffset = 6000 };
        document.Add(room);
        var open = RoomHeadroom.Measure(document, room)!;

        // A gabled vault over it, its eaves at 2.4 m, its ridge 1.5 m higher.
        var vault = new Roof { LevelId = level, TypeId = document.TypesOf<RoofType>().First().Id, HeightOffset = 2400 };
        vault.SetExtrusion(new RoofExtrusion(new Point2D(-300, 0), new Vector2D(0, 1), new[] { new Point2D(0, 0), new Point2D(2800, 1500), new Point2D(5600, 0) }, -6300, 300));
        document.Add(vault);
        var under = RoomHeadroom.Measure(document, room)!;
        Assert.True(under.UnderRoof);
        Assert.True(under.Volume < open.Volume);
        Assert.InRange(under.Highest, 2400 + 1200, 2400 + 1600);
        Assert.InRange(under.Lowest, 2000, 2700);
    }
}
