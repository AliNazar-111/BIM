using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Openings by face: cut square to a roof's slope, as a hole for a rooflight is trimmed between
/// rafters, rather than straight down - moving up the slope as they go down through the roof.
/// </summary>
public class RoofFaceOpeningTests
{
    private const double Pitch = 40 * Math.PI / 180;

    [Fact]
    public void ItIsAsLongUpTheFaceAsAskedAndCutSquareToIt()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var opening = RoofOpenings.ByFace(document, roof, new Point2D(5000, 2000), 900, 1200, out _)!;
        Assert.True(opening.Square);

        // Up the south slope, toward the ridge: 1200 on the face, shorter in plan.
        var sill = opening.Points.Min(point => point.Y);
        var head = opening.Points.Max(point => point.Y);
        Assert.Equal(1200 * Math.Cos(Pitch), head - sill, 3);
        Assert.Equal(900, opening.Points.Max(point => point.X) - opening.Points.Min(point => point.X), 3);

        new SetRoofSketchCommand(roof, roof.Boundary, roof.Edges, roof.SlopeArrows, "Opening by Face", roof.Openings.Append(opening)).Redo();
        var pieces = RoofSolid.Pieces(document, roof);
        var thickness = document.FindType<SlabType>(roof.TypeId)!.Thickness;
        var shift = thickness * Math.Sin(Pitch);

        bool Underside(Point2D at) => pieces.Any(piece => Polygon2D.Contains(piece.Outline, at) &&
            Math.Abs(piece.Bottom.HeightAt(at) - roof.UndersideAt(document, at)) < 1 && piece.Top.HeightAt(at) - piece.Bottom.HeightAt(at) > 1);
        bool Top(Point2D at) => pieces.Any(piece => Polygon2D.Contains(piece.Outline, at) &&
            Math.Abs(piece.Top.HeightAt(at) - roof.TopAt(document, at)) < 1);

        // On top, the hole is where it was drawn.
        Assert.False(Top(new Point2D(5000, sill + 5)));
        Assert.False(Top(new Point2D(5000, head - 5)));
        Assert.True(Top(new Point2D(5000, head + 5)));

        // Underneath, it is as far up the slope as the roof is thick times the sine of its pitch:
        // roof still under the drawn sill, none under the roof just past the drawn head.
        Assert.True(Underside(new Point2D(5000, sill + shift * 0.9)));
        Assert.False(Underside(new Point2D(5000, sill + shift * 1.1)));
        Assert.False(Underside(new Point2D(5000, head + shift * 0.9)));
        Assert.True(Underside(new Point2D(5000, head + shift * 1.1)));

        // Its sides are cut straight down.
        Assert.False(Underside(new Point2D(5000 + 440, (sill + head) / 2 + shift)));
        Assert.True(Underside(new Point2D(5000 + 460, (sill + head) / 2 + shift)));
    }

    [Fact]
    public void ItKeepsToOneFaceAndIsKeptThroughTheSketchAndTheFile()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);

        // Over the ridge it would not be square to anything.
        Assert.Null(RoofOpenings.ByFace(document, roof, new Point2D(5000, 3800), 900, 1200, out var why));
        Assert.Contains("ridge", why);

        var opening = RoofOpenings.ByFace(document, roof, new Point2D(5000, 2000), 900, 1200, out _)!;
        new SetRoofSketchCommand(roof, roof.Boundary, roof.Edges, roof.SlopeArrows, "Opening by Face", roof.Openings.Append(opening)).Redo();

        // Not a second one where it is.
        Assert.Null(RoofOpenings.ByFace(document, roof, new Point2D(5300, 2100), 900, 1200, out why));
        Assert.Contains("opening", why);

        // Drawn again in Edit Footprint, the same loop is still square to the face.
        var redrawn = new RoofOpening(opening.Points.Reverse().ToList(), new Guid?[4]);
        new SetRoofSketchCommand(roof, roof.Boundary, roof.Edges, roof.SlopeArrows, "Edit Footprint", new[] { redrawn }).Redo();
        Assert.True(Assert.Single(roof.Openings).Square);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Roof>().Single(saved => saved.Id == roof.Id);
        Assert.True(Assert.Single(reopened.Openings).Square);
    }
}

/// <summary>The Opening by Face tool on the plan, as a user clicks it.</summary>
[Collection("Wpf")]
public class RoofFaceOpeningPlanTests
{
    [Fact]
    public void AClickOnARoofsFaceCutsItAndOneUndoPutsItBack()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();
                var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack() };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.FaceOpening);
                (plan.ActiveFaceOpeningWidth, plan.ActiveFaceOpeningLength) = (600, 900);

                Assert.False(plan.PlaceFaceOpeningAt(new Point2D(5000, 3900)));
                Assert.True(plan.PlaceFaceOpeningAt(new Point2D(5000, 2000)));
                var opening = Assert.Single(roof.Openings);
                Assert.True(opening.Square);
                Assert.Equal(900 * Math.Cos(40 * Math.PI / 180), opening.Points.Max(point => point.Y) - opening.Points.Min(point => point.Y), 3);

                plan.History!.Undo();
                Assert.Empty(roof.Openings);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
