using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>Tangent, centre-ends and fillet arcs, and straight walls drawn with a radius at every corner.</summary>
[Collection("Wpf")]
public class WallArcToolTests
{
    private static void OnUiThread(Action<BimDocument, PlanView> action)
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
                action(document, plan);
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    private static void Near(Point2D expected, Point2D actual, double tolerance = 1e-6) =>
        Assert.True(expected.DistanceTo(actual) < tolerance, $"Expected {expected}, was {actual}.");

    private static void Near(Vector2D expected, Vector2D actual) =>
        Assert.True((expected - actual).Length < 1e-6, $"Expected {expected}, was {actual}.");

    [Fact]
    public void ATangentArcCarriesOnSmoothlyFromTheWallAndTheNextFromIt()
    {
        OnUiThread((document, plan) =>
        {
            plan.PlaceWallPointAt(new Point2D(0, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 0));

            plan.DrawShape = WallShape.TangentArc;
            plan.PlaceWallPointAt(new Point2D(4000, 0));
            plan.PlaceWallPointAt(new Point2D(6000, 2000));
            var arc = document.Walls.Last();
            Assert.True(arc.IsCurved);
            Near(new Vector2D(1, 0), arc.LocationCurve.TangentAt(0));

            plan.PlaceWallPointAt(new Point2D(6000, 5000));
            var next = document.Walls.Last();
            Assert.NotSame(arc, next);
            Near(arc.LocationCurve.TangentAt(arc.Length), next.LocationCurve.TangentAt(0));
        });
    }

    [Fact]
    public void ACentreEndsArcIsOnTheCircleTheStartSetsTheShorterWayRound()
    {
        OnUiThread((document, plan) =>
        {
            plan.DrawShape = WallShape.CentreEndsArc;
            plan.PlaceWallPointAt(new Point2D(0, 0));
            plan.PlaceWallPointAt(new Point2D(3000, 0));
            plan.PlaceWallPointAt(new Point2D(0, 5000));

            var arc = Assert.Single(document.Walls);
            Near(new Point2D(3000, 0), arc.Start);
            Near(new Point2D(0, 3000), arc.End);
            Near(new Point2D(0, 0), arc.LocationCurve.Centre, 1e-3);
            Assert.Equal(3000, arc.LocationCurve.Radius, 3);
            Assert.Equal(Math.PI * 3000 / 2, arc.Length, 1);
        });
    }

    [Fact]
    public void AFilletArcRoundsOffACornerAndCutsBothWallsBack()
    {
        OnUiThread((document, plan) =>
        {
            plan.PlaceWallPointAt(new Point2D(0, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 3000));
            var (first, second) = (document.Walls.ElementAt(0), document.Walls.ElementAt(1));

            var arc = plan.RoundCorner(first, second, 1000);
            Assert.NotNull(arc);
            Near(new Point2D(3000, 0), first.End);
            Near(new Point2D(4000, 1000), second.Start);
            Near(new Point2D(3000, 1000), arc!.LocationCurve.Centre, 1e-3);
            Assert.Equal(1000, arc.LocationCurve.Radius, 3);
            Assert.Equal(first.Flipped, arc.Flipped);

            // It turns the corner as the walls do: leaving the first wall along it.
            Near(first.Direction, arc.LocationCurve.TangentAt(0));
            Near(second.Direction, arc.LocationCurve.TangentAt(arc.Length));

            plan.History!.Undo();
            Assert.Equal(2, document.Walls.Count());
            Near(new Point2D(4000, 0), first.End);
        });
    }

    [Fact]
    public void ARadiusTooBigForTheWallsIsRefused()
    {
        OnUiThread((document, plan) =>
        {
            plan.PlaceWallPointAt(new Point2D(0, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 3000));
            var (first, second) = (document.Walls.ElementAt(0), document.Walls.ElementAt(1));

            string? hint = null;
            plan.HintChanged += (_, text) => hint = text;
            Assert.Null(plan.RoundCorner(first, second, 5000));
            Assert.Contains("shorter", hint);
            Assert.Equal(2, document.Walls.Count());
        });
    }

    [Fact]
    public void WithARadiusSetEveryCornerOfAChainIsRoundedOff()
    {
        OnUiThread((document, plan) =>
        {
            plan.ChainRadius = true;
            plan.ArcRadius = 500;
            plan.PlaceWallPointAt(new Point2D(0, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 0));
            plan.PlaceWallPointAt(new Point2D(4000, 3000));
            plan.PlaceWallPointAt(new Point2D(0, 3000));

            var walls = document.Walls.ToList();
            Assert.Equal(5, walls.Count);
            Assert.Equal(2, walls.Count(wall => wall.IsCurved));
            Assert.All(walls.Where(wall => wall.IsCurved), arc => Assert.Equal(500, arc.LocationCurve.Radius, 3));
            Near(new Point2D(3500, 0), walls[0].End);

            // One step per wall drawn: undo takes back the last wall and its rounded corner.
            plan.History!.Undo();
            Assert.Equal(3, document.Walls.Count());
            Near(new Point2D(4000, 3000), document.Walls.Last().End);
        });
    }
}
