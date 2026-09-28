using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The roof sketch's round and editing tools, clicked as a user clicks them: Circle and Arc,
/// Pick Lines, Offset and Trim/Extend.
/// </summary>
[Collection("Wpf")]
public class RoofSketchToolsPlanTests
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

    /// <summary>An empty plan with a roof sketch open.</summary>
    private static (BimDocument Document, PlanView Plan) Sketching()
    {
        var document = BimDocument.CreateDefault();
        var plan = new PlanView
        {
            Document = document,
            ActiveLevelId = document.Levels[0].Id,
            History = new UndoStack(),
            ActiveRoofTypeId = document.TypesOf<RoofType>().First().Id
        };

        plan.SetTool(PlanTool.Roof);
        return (document, plan);
    }

    [Fact]
    public void TheCircleToolMakesAConicalRoof()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Circle;

            Assert.True(plan.SketchClick(new Point2D(0, 0)));
            Assert.True(plan.SketchClick(new Point2D(4000, 0)));
            Assert.Equal(2, plan.SketchLines.Count);
            Assert.All(plan.SketchLines, line => Assert.True(line.IsArc));

            Assert.True(plan.FinishSketch());
            var roof = Assert.Single(document.Elements.OfType<Roof>());
            Assert.Equal(RoofForm.Conical, roof.Form);
            Assert.Equal(Math.PI * 4000 * 4000, roof.Area, 4000 * 4000 * 0.02);

            // Opened again, it is two arcs, not dozens of short lines.
            plan.Select(roof);
            Assert.True(plan.EditFootprint());
            Assert.Equal(2, plan.SketchLines.Count);
        });
    }

    [Fact]
    public void TheArcToolRoundsOffOneEnd()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();

            plan.SketchTool = RoofSketchTool.Line;
            plan.SketchClick(new Point2D(0, 6000));
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(8000, 0));
            plan.SketchEscape();

            // Start, end, then a point three metres out that the arc passes through.
            plan.SketchTool = RoofSketchTool.Arc;
            Assert.True(plan.SketchClick(new Point2D(8000, 0)));
            Assert.True(plan.SketchClick(new Point2D(8000, 6000)));
            Assert.True(plan.SketchClick(new Point2D(11000, 3000)));

            plan.SketchTool = RoofSketchTool.Line;
            plan.SketchClick(new Point2D(8000, 6000));
            plan.SketchClick(new Point2D(0, 6000));

            Assert.True(plan.FinishSketch(), "The outline with a round end should close.");
            var roof = Assert.Single(document.Elements.OfType<Roof>());

            // A half circle on the 6 m end: the square part and half a disc.
            Assert.Equal(8000 * 6000 + Math.PI * 3000 * 3000 / 2, roof.Area, 3000 * 3000 * 0.03);
            Assert.Single(RoofSketch.LinesOf(roof), line => line.IsArc);
        });
    }

    [Fact]
    public void PickLinesTakesTheEdgesOfAFloorSetOff()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            var floor = new Floor { TypeId = document.TypesOf<FloorType>().First().Id, LevelId = document.Levels[0].Id };
            floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) });
            document.Add(floor);

            plan.SketchTool = RoofSketchTool.PickLines;
            plan.SketchOffset = 500;

            // Just outside the middle of each edge: the line goes out toward the cursor.
            foreach (var point in new[] { new Point2D(5000, -20), new Point2D(10020, 3000), new Point2D(5000, 6020), new Point2D(-20, 3000) })
                Assert.True(plan.SketchClick(point));

            Assert.True(plan.FinishSketch());
            var roof = document.Elements.OfType<Roof>().Single();
            Assert.Equal(11000 * 7000, roof.Area, precision: 0);
        });
    }

    [Fact]
    public void OffsetMovesALineAndItsNeighboursFollow()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Rectangle;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(10000, 6000));

            // The right-hand line, clicked on its outer side, goes a metre further out.
            plan.SketchTool = RoofSketchTool.Offset;
            plan.SketchOffset = 1000;
            Assert.True(plan.SketchClick(new Point2D(10020, 3000)));

            Assert.True(plan.FinishSketch());
            Assert.Equal(11000 * 6000, document.Elements.OfType<Roof>().Single().Area, precision: 0);
        });
    }

    [Fact]
    public void OffsetWithCopyLeavesTheLineAndAddsAnother()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Rectangle;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(10000, 6000));

            plan.SketchTool = RoofSketchTool.Offset;
            plan.SketchOffset = 1000;
            plan.SketchOffsetCopy = true;
            Assert.True(plan.SketchClick(new Point2D(5000, 20)));

            Assert.Equal(5, plan.SketchLines.Count);
            Assert.Contains(plan.SketchLines, line => Math.Abs(line.Start.Y - 1000) < 1e-6 && Math.Abs(line.End.Y - 1000) < 1e-6);
        });
    }

    [Fact]
    public void TrimExtendMakesTwoLinesMeetAtACorner()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Sketching();

            // One line running past where it should stop, one stopping short of it.
            plan.SketchTool = RoofSketchTool.Line;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(12000, 0));
            plan.SketchEscape();
            plan.SketchClick(new Point2D(10000, 1000));
            plan.SketchClick(new Point2D(10000, 6000));
            plan.SketchEscape();

            plan.SketchTool = RoofSketchTool.TrimExtend;
            Assert.True(plan.SketchClick(new Point2D(5000, 0)));
            Assert.True(plan.SketchClick(new Point2D(10000, 4000)));

            var bottom = plan.SketchLines.Single(line => Math.Abs(line.Start.Y) < 1e-6 && Math.Abs(line.End.Y) < 1e-6);
            var side = plan.SketchLines.Single(line => Math.Abs(line.Start.X - 10000) < 1e-6 && Math.Abs(line.End.X - 10000) < 1e-6);

            Assert.Equal(new Point2D(10000, 0), bottom.End);
            Assert.Equal(new Point2D(10000, 0), side.Start);
        });
    }

    /// <summary>A box of walls, picked with a 500 mm overhang on the long sides and none on the ends.</summary>
    private static (BimDocument Document, PlanView Plan) PickedWithTwoOverhangs()
    {
        var (document, plan) = Sketching();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 6000), new Point2D(0, 6000) };

        for (var i = 0; i < 4; i++)
        {
            document.Add(new Wall
            {
                Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id,
                LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
            });
        }

        plan.SketchTool = RoofSketchTool.PickWalls;
        plan.SketchOverhang = 500;
        plan.SketchClick(new Point2D(5000, -250));
        plan.SketchClick(new Point2D(5000, 6250));
        plan.SketchOverhang = 0;
        plan.SketchClick(new Point2D(10250, 3000));
        plan.SketchClick(new Point2D(-250, 3000));

        Assert.Equal(4, plan.SketchLines.Count);
        return (document, plan);
    }

    [Fact]
    public void AlignEavesRaisesOrLowersAnEaveToMatchAnother()
    {
        OnUiThread(() =>
        {
            var (_, plan) = PickedWithTwoOverhangs();
            var south = plan.SketchLines.Single(line => line.Edge.Overhang > 0 && line.Start.Y < 0);
            var east = plan.SketchLines.Single(line => line.Edge.Overhang == 0 && line.Start.X > 5000);

            // The overhanging eave is lower: 500 mm out at 30 degrees.
            plan.SketchTool = RoofSketchTool.AlignEaves;
            Assert.True(plan.SketchClick(south.Middle));
            Assert.True(plan.SketchClick(east.Middle));

            // The east eave's plate comes down until its edge is at the south eave's height.
            Assert.Equal(-500 * Math.Tan(Math.PI / 6), east.Edge.PlateOffset, precision: 3);
            Assert.Equal(0, east.Edge.Overhang, precision: 6);
        });
    }

    [Fact]
    public void AlignEavesByOverhangRunsAnEaveOutToTheHeight()
    {
        OnUiThread(() =>
        {
            var (document, plan) = PickedWithTwoOverhangs();
            var south = plan.SketchLines.Single(line => line.Edge.Overhang > 0 && line.Start.Y < 0);
            var east = plan.SketchLines.Single(line => line.Edge.Overhang == 0 && line.Start.X > 5000);
            var eastWallFace = east.Start.X;

            plan.SketchTool = RoofSketchTool.AlignEaves;
            plan.SketchAlignByOverhang = true;
            plan.SketchClick(south.Middle);
            Assert.True(plan.SketchClick(east.Middle));

            // Same pitch, same plate: to reach the same height it needs the same overhang - and
            // the line moves out by it, its neighbours running on to meet it.
            Assert.Equal(500, east.Edge.Overhang, precision: 3);
            Assert.Equal(0, east.Edge.PlateOffset, precision: 6);
            Assert.Equal(eastWallFace + 500, east.Start.X, precision: 3);

            // The west end too, and every eave of the roof is level.
            var west = plan.SketchLines.Single(line => line.Edge.Overhang == 0 && line.Start.X < 5000);
            Assert.True(plan.SketchClick(west.Middle));

            Assert.True(plan.FinishSketch());
            var roof = document.Elements.OfType<Roof>().Single();
            Assert.All(roof.Edges, edge => Assert.Equal(-500 * Math.Tan(Math.PI / 6), roof.EaveHeight(document, edge), precision: 3));
        });
    }

    [Fact]
    public void APitchCanBeTypedBesideItsLine()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Rectangle;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(10000, 6000));

            var line = plan.SketchLines[0];
            Assert.True(plan.BeginPitchEdit(line));
            Assert.True(plan.CommitPitchEdit("45°"));
            Assert.Equal(45, line.Edge.SlopeDegrees, precision: 6);

            // Nonsense or an upright pitch leaves it alone.
            plan.BeginPitchEdit(line);
            Assert.False(plan.CommitPitchEdit("steep"));
            plan.BeginPitchEdit(line);
            Assert.False(plan.CommitPitchEdit("95"));
            Assert.Equal(45, line.Edge.SlopeDegrees, precision: 6);

            // And the change is one step of the sketch's own undo.
            Assert.True(plan.UndoSketch());
            Assert.Equal(30, plan.SketchLines[0].Edge.SlopeDegrees, precision: 6);
        });
    }

    [Fact]
    public void ADormerIsJoinedAndOpenedByClicks()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var level = document.Levels[0].Id;
            var roofType = document.TypesOf<RoofType>().First();

            // A gable roof with a small gable dormer roof sitting on its south slope.
            var main = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 3000 };
            main.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
            main.SetEdges(new[]
            {
                new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
                new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
            });
            document.Add(main);

            var dormer = new Roof { TypeId = roofType.Id, LevelId = level, HeightOffset = 5200 };
            dormer.SetBoundary(new[] { new Point2D(3700, 900), new Point2D(6300, 900), new Point2D(6300, 3000), new Point2D(3700, 3000) });
            dormer.SetEdges(new[]
            {
                new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 },
                new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }
            });
            document.Add(dormer);

            var plan = new PlanView { Document = document, ActiveLevelId = level, History = new UndoStack() };

            // Join: the dormer's back edge, then the main roof.
            plan.SetTool(PlanTool.JoinRoof);
            Assert.True(plan.JoinRoofAt(new Point2D(5000, 3020)));
            Assert.True(plan.JoinRoofAt(new Point2D(5000, 6500)));
            Assert.Equal(main.Id, dormer.JoinedTo);

            // Dormer Opening: the main roof selected, then the dormer clicked.
            plan.SetTool(PlanTool.Select);
            plan.Select(main);
            Assert.True(plan.BeginDormerOpening());
            Assert.True(plan.DormerOpeningAt(new Point2D(5000, 2000)));
            Assert.Contains(dormer.Id, main.DormerOpenings);

            // Clicking an edge of the joined dormer with the tool unjoins it; each is one undo.
            plan.SetTool(PlanTool.JoinRoof);
            Assert.True(plan.JoinRoofAt(new Point2D(3710, 2000)));
            Assert.Null(dormer.JoinedTo);
            plan.History!.Undo();
            Assert.Equal(main.Id, dormer.JoinedTo);
        });
    }

    [Fact]
    public void AShapeDrawnInsideTheOutlineBecomesAnOpening()
    {
        OnUiThread(() =>
        {
            var (document, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Rectangle;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(10000, 6000));

            // A square inside it, and a circle.
            plan.SketchClick(new Point2D(2000, 2000));
            plan.SketchClick(new Point2D(3000, 3000));
            plan.SketchTool = RoofSketchTool.Circle;
            plan.SketchClick(new Point2D(7000, 3000));
            plan.SketchClick(new Point2D(7500, 3000));

            // Their lines do not slope: they are the edges of holes.
            Assert.Equal(4, plan.SketchLines.Count(line => line.Edge.DefinesSlope));

            Assert.True(plan.FinishSketch());
            var roof = document.Elements.OfType<Roof>().Single();
            Assert.Equal(2, roof.Openings.Count);
            Assert.Equal(RoofForm.Hip, roof.Form);
        });
    }

    [Fact]
    public void TheDormerToolBuildsADormerWithOneClick()
    {
        OnUiThread(() =>
        {
            var document = BimDocument.CreateDefault();
            var level = document.Levels[0].Id;

            var main = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
            main.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 10000), new Point2D(0, 10000) });
            main.SetEdges(new[]
            {
                new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
                new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
            });
            document.Add(main);

            var plan = new PlanView { Document = document, ActiveLevelId = level, History = new UndoStack() };
            plan.SetTool(PlanTool.Dormer);
            plan.Dormer = DormerSettings.Default with { Shape = DormerShape.Hip };

            Assert.True(plan.DormerAt(new Point2D(5000, 800)));
            Assert.Equal(2, document.Elements.OfType<Roof>().Count());
            Assert.Equal(3, document.Walls.Count());
            Assert.Single(main.DormerOpenings);

            // One click, one step to undo.
            plan.History!.Undo();
            Assert.Single(document.Elements.OfType<Roof>());
            Assert.Empty(document.Walls);
            Assert.Empty(main.DormerOpenings);
        });
    }

    [Fact]
    public void OnALowRoofTheDormerToolSaysWhyAndOffersAShed()
    {
        OnUiThread(() =>
        {
            // A 6 m x 5 m house, drawn clockwise, under a 30 degree gable roof picked off its walls.
            var document = BimDocument.CreateDefault();
            var level = document.Levels[0].Id;
            var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var corners = new[] { new Point2D(0, 0), new Point2D(0, 5000), new Point2D(6000, 5000), new Point2D(6000, 0) };
            for (var i = 0; i < 4; i++)
                document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 });

            var plan = new PlanView
            {
                Document = document, ActiveLevelId = level, History = new UndoStack(),
                ActiveRoofTypeId = document.TypesOf<RoofType>().First().Id, ActiveWallTypeId = type.Id
            };

            plan.SetTool(PlanTool.Roof);
            foreach (var point in new[] { new Point2D(3000, -250), new Point2D(6250, 2500), new Point2D(3000, 5250), new Point2D(-250, 2500) })
                plan.SketchClick(point);
            foreach (var line in plan.SketchLines.Where(line => Math.Abs(line.Start.X - line.End.X) < 1))
                line.Edge.DefinesSlope = false;
            Assert.True(plan.FinishSketch());

            DormerRefusedEventArgs? refused = null;
            plan.DormerRefused += (_, args) => refused = args;

            // A gable dormer clicked just inside the north eave: the roof rises only 1.5 m to its
            // ridge, not enough for a 2.4 m gable dormer - refused, with a shed offered instead.
            plan.SetTool(PlanTool.Dormer);
            Assert.False(plan.DormerAt(new Point2D(3000, 5140)));
            Assert.NotNull(refused);
            Assert.Contains("too low", refused!.Problem);
            var shed = refused.Instead;
            var clicked = refused.At;
            Assert.NotNull(shed);
            Assert.Equal(DormerShape.Shed, shed!.Shape);

            // Taking the shed, it goes on.
            plan.Dormer = shed;
            refused = null;
            var added = plan.DormerAt(clicked);
            Assert.True(added, refused?.Problem);
            Assert.Equal(2, document.Elements.OfType<Roof>().Count());

            // Steepened to 45 degrees, the roof takes the gable dormer asked for at first - made as
            // high as fits under the ridge.
            plan.History!.Undo();
            var main = document.Elements.OfType<Roof>().Single();
            foreach (var edge in main.Edges.Where(edge => edge.DefinesSlope)) edge.SlopeDegrees = 45;
            plan.Dormer = DormerSettings.Default;
            refused = null;
            Assert.True(plan.DormerAt(clicked), refused?.Problem);
            var gable = document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main));
            Assert.Equal(RoofForm.Gable, gable.Form);
        });
    }

    [Fact]
    public void SplittingAnArcLeavesTwoArcsOfTheSameCircle()
    {
        OnUiThread(() =>
        {
            var (_, plan) = Sketching();
            plan.SketchTool = RoofSketchTool.Circle;
            plan.SketchClick(new Point2D(0, 0));
            plan.SketchClick(new Point2D(4000, 0));

            plan.SketchTool = RoofSketchTool.Split;
            var top = plan.SketchLines.First(line => line.Middle.Y > 0);
            Assert.True(plan.SketchClick(top.Middle));

            Assert.Equal(3, plan.SketchLines.Count);
            Assert.All(plan.SketchLines, line => Assert.True(line.IsArc));
            Assert.All(plan.SketchLines.SelectMany(line => line.Facets), point =>
                Assert.Equal(4000, point.DistanceTo(new Point2D(0, 0)), precision: 3));
        });
    }
}
