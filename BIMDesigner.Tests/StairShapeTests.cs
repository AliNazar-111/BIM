using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// The stairs that wind, curve or split: a spiral round its column, a curve round an open well,
/// a half turn on six winders round two newels, and a split stair going off both ways from its
/// landing - each laid out, checked against its rules, cutting its floor and guarded round it,
/// built, drawn, saved and exported.
/// </summary>
[Collection("Wpf")]
public class StairShapeTests
{
    /// <summary>The angle between two points seen from a centre, radians.</summary>
    private static double Between(Point2D centre, Point2D a, Point2D b)
    {
        var (from, to) = (a - centre, b - centre);
        return Math.Acos(Math.Clamp(from.Dot(to) / (from.Length * to.Length), -1, 1));
    }

    [Fact]
    public void ASpiralStairWindsRoundItsColumnUpToALandingAtItsHead()
    {
        var (document, floor) = House();
        var stair = Put(document, 3000, 1500, shape: StairShape.Spiral, width: 800);
        var layout = Stairs.Layout(document, stair);

        // Turning right going up, round a 120 column to its right: each tread turning as far as
        // makes its going along the middle of the stair the type's 225.
        var centre = layout.Centre!.Value;
        Assert.Equal(0, (centre - new Point2D(3460, 1500)).Length, 6);
        var column = Assert.Single(layout.Newels);
        Assert.Equal(60, column.Outline.Max(point => (point - centre).Length), 6);
        Assert.Equal(layout.Risers - 1, layout.Steps.Count(step => step.Kind == StairStepKind.Tread));
        Assert.Equal(225, Between(centre, layout.Steps[0].RiserTo, layout.Steps[1].RiserTo) * 460, 6);
        Assert.Equal(2 * Math.PI * 460 / 225, layout.TreadsPerTurn!.Value, 6);

        // At its head a quarter turn of landing, level with the floor it reaches.
        var landing = Assert.Single(layout.Steps, step => step.Kind == StairStepKind.Landing);
        Assert.Equal((layout.Risers, layout.Top), (landing.Number, landing.Top));
        Assert.InRange(Polygon2D.Area(landing.Outline), Math.PI / 4 * (860 * 860 - 60 * 60) * 0.995, Math.PI / 4 * (860 * 860 - 60 * 60));

        // The column is its handhold on the inside; a balustrade goes up the outside, and along the
        // landing's far edge, where it drops to the stair below.
        Assert.True(layout.Sides.Single(side => side.Role == StairSideRole.Right).AgainstPole);
        var railings = StairModel.Railings(document, stair, layout);
        Assert.Equal(2, railings.Count);
        var outside = Assert.Single(railings, railing => railing.Points.Count > 2);
        Assert.True(outside.LeftSide);
        Assert.All(outside.Points, point => Assert.Equal(860, (point.At - centre).Length, 6));
        Assert.All(railings.Single(railing => railing.Points.Count == 2).Points, point => Assert.Equal(layout.Top, point.Height, 6));

        // BS 5395-2 for a house's main stair, and the IRC's spiral stair rules: met by both.
        Assert.Empty(Stairs.Problems(document, stair));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Empty(Stairs.Problems(document, stair));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;

        // It comes up through a round hole its own size, guarded round on the floor but for the
        // quarter its landing is stepped off from.
        var hole = Assert.Single(Stairs.Holes(document, floor));
        Assert.InRange(Polygon2D.Area(hole), Math.PI * 860 * 860 * 0.995, Math.PI * 860 * 860);
        var guarding = Assert.Single(StairModel.Guarding(document, stair, layout));
        var guarded = guarding.Points.Zip(guarding.Points.Skip(1), (a, b) => (b.At - a.At).Length).Sum();
        Assert.InRange(guarded, 2 * Math.PI * 860 * 0.75 - 100, 2 * Math.PI * 860 * 0.75 + 100);
        Assert.DoesNotContain(guarding.Points.Zip(guarding.Points.Skip(1)),
            pair => Polygon2D.Contains(landing.Outline, pair.First.At.MidpointTo(pair.Second.At) + (centre - pair.First.At.MidpointTo(pair.Second.At)) * 0.01));

        // Its column stands from its foot to a handrail's height over the floor it reaches.
        Assert.Equal(layout.Top + 900, column.Top, 6);
        var (body, rails) = StairModel.Build(document, stair);
        Assert.Equal(layout.Bottom, body.Concat(rails).Min(mesh => mesh.Bounds()!.Value.Min.Z), 0);
        Assert.True(rails.Max(mesh => mesh.Bounds()!.Value.Max.Z) >= layout.Top + 900 - 0.5);
    }

    [Fact]
    public void ASpiralThatWindsOverItselfHasBalustersAllTheWayUp()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 2000, shape: StairShape.Spiral);
        var type = document.TypesOf<StairType>().First(candidate => candidate.Construction == StairConstruction.Concrete);
        stair.TypeId = type.Id;
        var layout = Stairs.Layout(document, stair);
        Assert.True(layout.Risers - 1 > layout.TreadsPerTurn, "It goes more than once round.");

        // Each stands on the tread under the handrail, not the one a turn above it, up to the rail.
        var outside = StairModel.Railings(document, stair, layout).Single(railing => railing.Points.Count > 2);
        var balusters = outside.Points.Zip(outside.Points.Skip(1))
            .SelectMany(pair => StairModel.BalustersAlong(pair.First, pair.Second, outside.LeftSide, type, layout)).ToList();
        Assert.True(balusters.Count > 60);
        Assert.All(balusters, baluster => Assert.InRange(baluster.Top - baluster.Foot, type.HandrailHeight - type.HandrailWidth, type.HandrailHeight - type.HandrailWidth + layout.Rise + 1));
    }

    [Fact]
    public void ASpiralTooNarrowOrTooTightComesShortOfItsRules()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 1500, shape: StairShape.Spiral, width: 600);

        // 600 wide: too narrow for a house's main stair, and only ten treads to a turn - 1981 clear over the tread a turn below.
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("BS 5395-2 asks for 800"));
        Assert.Contains(problems, problem => problem.Contains("comes round over itself with 1981 headroom"));

        // Round a wide column, the IRC's walkline is too far out.
        stair.Width = 800;
        stair.InnerRadius = 400;
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("622 (24½ in)"));
    }

    [Fact]
    public void ACurvedStairTapersItsTreadsAndIsMeasuredWhereTheRulesSay()
    {
        var (document, _) = House();
        var stair = Put(document, 4500, 600, shape: StairShape.Curved, left: true);
        stair.InnerRadius = 1200;
        var layout = Stairs.Layout(document, stair);

        // Turning left, round a centre 1650 to its left: its treads tapering from 164 at the narrow end.
        Assert.Equal(0, (layout.Centre!.Value - new Point2D(4500 - 1650, 600)).Length, 6);
        Assert.Equal((1200.0, 2100.0), (layout.InnerRadius, layout.OuterRadius));
        Assert.Equal(225 * 1200 / 1650.0, layout.GoingAt!(0), 6);
        Assert.Empty(layout.Newels);
        Assert.Equal(2, StairModel.Railings(document, stair, layout).Count);

        // Under 1000 wide, Approved Document K measures it along the middle: 225, as the type says.
        Assert.Empty(Stairs.Problems(document, stair));

        // 1200 wide, it is measured 270 in from each side: 184 on the inside is too short, and too steep.
        stair.Width = 1200;
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("going 270 in from the inside is 184"));
        Assert.Contains(problems, problem => problem.Contains("270 in from the inside: Approved Document K allows 42°"));

        // The IRC measures at its walkline, 305 in from the narrow side.
        stair.Width = 900;
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("at the walkline 305 (12 in)"));
    }

    [Fact]
    public void AHalfTurnOnWindersTurnsOnSixWindersRoundTwoNewels()
    {
        var (document, floor) = House();
        var stair = Put(document, 1500, 600, shape: StairShape.HalfTurn, corner: StairCorner.Winders);
        var layout = Stairs.Layout(document, stair);

        // Six winders, three fanned round each of two newels at the ends of the well.
        var winders = layout.Steps.Where(step => step.Kind == StairStepKind.Winder).ToList();
        Assert.Equal(6, winders.Count);
        Assert.Equal(2, layout.Newels.Count);
        Assert.All(winders, winder => Assert.Contains(layout.Newels, newel => newel.Outline.Contains(winder.RiserFrom)));
        Assert.Equal((57.7, 235.6), (Math.Round(layout.WinderNarrowEnd!.Value, 1), Math.Round(layout.WinderGoing!.Value, 1)));

        // Turning right, it comes back down the plan beside its first flight, the 100 well between.
        Assert.Equal(-1, layout.Steps[^1].Up.Y, 6);
        Assert.Equal(900 + 100, layout.WalkingLine[^1].X - layout.WalkingLine[0].X, 6);
        Assert.Equal(layout.Top - layout.Rise, layout.Steps[^1].Top, 6);

        // It meets Approved Document K, cuts one opening, and its inner handrail crosses the well from newel to newel.
        Assert.Empty(Stairs.Problems(document, stair));
        Assert.Single(Stairs.Holes(document, floor));
        var inner = StairModel.Railings(document, stair, layout).Single(railing => !railing.LeftSide);
        Assert.Contains(inner.Points, point => layout.Newels[1].Outline.Contains(point.At));
    }

    [Fact]
    public void ASplitStairGoesUpOneWideFlightAndOffBothWays()
    {
        var (document, floor) = House();
        var stair = Put(document, 3000, 400, shape: StairShape.Bifurcated);
        var layout = Stairs.Layout(document, stair);

        // A first flight half as wide again, a landing, then a flight the stair's width off each way.
        Assert.Equal(1350, layout.WidestFlight, 6);
        Assert.Single(layout.Steps, step => step.Kind == StairStepKind.Landing);
        Assert.Equal(6 + 1 + 2 * 7, layout.Steps.Count);
        Assert.Equal(2, layout.Walks.Count);
        Assert.Equal(2, layout.Arrivals.Count);
        Assert.Equal(new[] { -1.0, 1.0 }, layout.Steps.Where(step => Math.Abs(step.Top - (layout.Top - layout.Rise)) < 1e-6).Select(step => Math.Round(step.Up.X, 6)).OrderBy(x => x));

        // Handrails up both sides and round the back of its landing, along the outsides of both ways up.
        Assert.Contains(layout.Sides, side => side.Role == StairSideRole.Other);
        Assert.Equal(3, StairModel.Railings(document, stair, layout).Count);

        // Its first flight is 1350 wide, so Approved Document K wants handrails both sides of it.
        Assert.Empty(Stairs.Problems(document, stair));
        stair.Railings = StairRailings.LeftSide;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("Its first flight is 1000 wide or more"));
        stair.Railings = StairRailings.Auto;

        // Its landing high enough to want the floor over it opened, one opening; low enough to keep it, one over each way up.
        Assert.Single(Stairs.Holes(document, floor));
        stair.RisersBeforeTurn = 3;
        layout = Stairs.Layout(document, stair);
        Assert.True(layout.Steps.Single(step => step.Kind == StairStepKind.Landing).Top + 2000 < floor.GetBottomElevation(document));
        Assert.Equal(2, Stairs.Holes(document, floor).Count());

        // Each guarded but where it comes up.
        var guarding = StairModel.Guarding(document, stair, layout);
        Assert.True(guarding.Count >= 2);
        foreach (var arrival in layout.Arrivals)
            Assert.DoesNotContain(guarding.SelectMany(run => run.Points.Zip(run.Points.Skip(1))),
                pair => Line2D.DistanceFromSegment(pair.First.At.MidpointTo(pair.Second.At), arrival.From, arrival.To) < 5);
    }

    [Fact]
    public void TheNewShapesAreBuiltCutSavedCopiedAndExportedAsTheirKinds()
    {
        var (document, _) = House();
        var stairs = new[]
        {
            (Stair: Put(document, 1500, 1500, shape: StairShape.Spiral, width: 800), Kind: IfcStairTypeEnum.SPIRAL_STAIR),
            (Stair: Put(document, 4500, 600, shape: StairShape.Curved, left: true), Kind: IfcStairTypeEnum.CURVED_RUN_STAIR),
            (Stair: Put(document, 1500, 600, shape: StairShape.HalfTurn, corner: StairCorner.Winders), Kind: IfcStairTypeEnum.HALF_WINDING_STAIR),
            (Stair: Put(document, 3000, 400, shape: StairShape.Bifurcated), Kind: IfcStairTypeEnum.DOUBLE_RETURN_STAIR)
        };
        for (var i = 0; i < stairs.Length; i++) stairs[i].Stair.Mark = $"S{i + 1}";
        stairs[0].Stair.InnerRadius = 100;

        // Built in 3D, and cut in a section through each.
        foreach (var (stair, _) in stairs)
        {
            var layout = Stairs.Layout(document, stair);
            var meshes = StairModel.Meshes(document, stair);
            Assert.Equal(layout.Bottom, meshes.Min(mesh => mesh.Bounds()!.Value.Min.Z), 0);
            Assert.True(meshes.Max(mesh => mesh.Bounds()!.Value.Max.Z) > layout.Top + 800);
            var middle = Polygon2D.Centroid(layout.Steps[layout.Steps.Count / 2].Outline, stair.Start);
            var marker = new SectionMarker { Start = middle + new Vector2D(-3000, 0.5), End = middle + new Vector2D(3000, 0.5), LevelId = stair.LevelId };
            Assert.Contains(SectionProjection.Build(document, marker).Pieces, piece => piece.ElementId == stair.Id);
        }

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var spiral = loaded.Elements.OfType<Stair>().Single(stair => stair.Mark == "S1");
        Assert.Equal((StairShape.Spiral, 100.0), (spiral.Shape, spiral.InnerRadius));
        Assert.Equal(100, ((Stair)ElementCopy.Clone(spiral)!).InnerRadius);

        using var model = IfcExport.Build(document);
        var exported = model.Instances.OfType<IfcStair>().ToList();
        foreach (var (stair, kind) in stairs)
            Assert.Equal(kind, exported.Single(product => $"{product.Tag}" == stair.Mark).PredefinedType);
    }

    [Fact]
    public void TheStairToolPutsInASpiralRoundTheColumnAskedForAndDrawsEveryShape()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, NewStairShape = StairShape.Spiral, NewStairInnerRadius = 100, NewStairWidth = 800 };
                plan.SetTool(PlanTool.Stair);
                Assert.True(plan.ClickStair(new Point2D(3000, 1500)));
                Assert.True(plan.ClickStair(new Point2D(3000, 2500)));
                var stair = Assert.Single(document.Elements.OfType<Stair>());
                Assert.Equal((StairShape.Spiral, 100.0, 800.0), (stair.Shape, stair.InnerRadius, stair.Width));

                Put(document, 4500, 600, shape: StairShape.Curved, left: true);
                Put(document, 1000, 600, shape: StairShape.HalfTurn, corner: StairCorner.Winders, width: 800);
                Put(document, 3000, 300, shape: StairShape.Bifurcated, width: 700);

                // Drawn on the storey they go up from and the one they reach.
                foreach (var level in document.Levels.Take(2))
                {
                    plan.ActiveLevelId = level.Id;
                    var border = new Border { Width = 600, Height = 500, Child = plan };
                    border.Measure(new Size(600, 500));
                    border.Arrange(new Rect(0, 0, 600, 500));
                    plan.ZoomToFit();
                    var bitmap = new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(border);
                    border.Child = null;
                }
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
