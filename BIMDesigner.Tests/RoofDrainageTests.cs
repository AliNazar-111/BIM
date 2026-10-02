using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Flat roofs that drain: drains in the roof, and the insulation tapered so the roof falls to
/// them - faces falling straight at each drain, valleys from its corners, ridges between drains.
/// </summary>
public class RoofDrainageTests
{
    /// <summary>A 10 m by 8 m warm flat roof over the ground floor, its base at 3 m.</summary>
    private static (BimDocument Document, Roof Roof, double Base, double Total) FlatRoof()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<RoofType>().First(candidate => candidate.Name.Contains("Flat"));
        var roof = new Roof { TypeId = type.Id, LevelId = document.Levels[0].Id, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(roof);
        return (document, roof, 3000, type.Structure.TotalWidth);
    }

    private static RoofDrain Drain(BimDocument document, Roof roof, double x, double y)
    {
        var drain = new RoofDrain { RoofId = roof.Id, LevelId = roof.LevelId, Location = new Point2D(x, y) };
        document.Add(drain);
        return drain;
    }

    [Fact]
    public void ItFallsToItsDrainAtItsFall()
    {
        var (document, roof, bottom, total) = FlatRoof();
        Drain(document, roof, 5000, 4000);

        // Lowest at the drain; rising at 1 in 40, measured square to the roof - 5 m out to the corner.
        Assert.Equal(bottom + total, roof.TopAt(document, new Point2D(5000, 4000)), 6);
        Assert.Equal(bottom + total + 4000 / 40.0, roof.TopAt(document, new Point2D(5000, 0)), 6);
        Assert.Equal(bottom + total + 5000 / 40.0, roof.TopAt(document, new Point2D(0, 0)), 6);

        // Four faces, the whole roof between them, each falling at the drain.
        var regions = RoofDrainage.Regions(document, roof);
        Assert.Equal(4, regions.Count);
        Assert.Equal(10000 * 8000, regions.Sum(region => Polygon2D.Area(region.Outline)), 0);
        Assert.All(regions, region => Assert.All(region.Outline, point =>
            Assert.Equal(RoofDrainage.Rise(document, roof, point), region.Rise(point), 6)));
    }

    [Fact]
    public void TheInsulationIsTaperedAndTheDeckStaysFlat()
    {
        var (document, roof, bottom, total) = FlatRoof();
        Drain(document, roof, 5000, 4000);
        var type = document.FindType<SlabType>(roof.TypeId)!;
        var corner = new Point2D(100, 100);
        var rise = RoofDrainage.Rise(document, roof, corner);

        var at = RoofSolid.Pieces(document, roof).Where(piece => Polygon2D.Contains(piece.Outline, corner)).ToList();
        var deck = at.Single(piece => piece.Layer.Function == LayerFunction.Structure);
        var insulation = at.Single(piece => piece.Layer.Function == LayerFunction.ThermalAir);
        var finish = at.Single(piece => piece.Layer.Function == LayerFunction.Finish1);

        Assert.Equal(bottom + 150, deck.Top.HeightAt(corner), 6);
        Assert.Equal(bottom + 150, insulation.Bottom.HeightAt(corner), 6);
        Assert.Equal(bottom + 150 + 160 + rise, insulation.Top.HeightAt(corner), 6);
        Assert.Equal(bottom + total + rise, finish.Top.HeightAt(corner), 6);

        // Built and cut: the top of the 3D roof is the falls' top, nothing below the deck moved.
        var roofMeshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == roof.Id).ToList();
        Assert.Equal(bottom + total + 5000 / 40.0, roofMeshes.SelectMany(mesh => mesh.Positions).Max(point => point.Z), 3);
        Assert.Equal(bottom, roofMeshes.SelectMany(mesh => mesh.Positions).Min(point => point.Z), 3);
        Assert.Equal(160 + 5000 / 40.0, (double)roof.GetInstanceParameters(document).Single(p => p.Name == "Thickest Tapered Insulation").Value!, 3);
        Assert.NotNull(type);
    }

    [Fact]
    public void BetweenTwoDrainsThereIsARidgeAndEachTakesItsHalf()
    {
        var (document, roof, bottom, total) = FlatRoof();
        var west = Drain(document, roof, 2500, 4000);
        var east = Drain(document, roof, 7500, 4000);

        // Highest midway between them, falling both ways.
        Assert.Equal(bottom + total + 2500 / 40.0, roof.TopAt(document, new Point2D(5000, 4000)), 6);
        Assert.True(roof.TopAt(document, new Point2D(4000, 4000)) < roof.TopAt(document, new Point2D(5000, 4000)));
        Assert.True(roof.TopAt(document, new Point2D(6000, 4000)) < roof.TopAt(document, new Point2D(5000, 4000)));

        Assert.Equal(40e6, RoofDrainage.CatchmentArea(document, roof, west), tolerance: 1000);
        Assert.Equal(40e6, RoofDrainage.CatchmentArea(document, roof, east), tolerance: 1000);
        Assert.Equal(80e6, RoofDrainage.Regions(document, roof).Sum(region => Polygon2D.Area(region.Outline)), tolerance: 1000);
    }

    [Fact]
    public void TheValleysFallLessSteeplyAndItSaysWhenTheyAreTooShallow()
    {
        var (document, roof, _, _) = FlatRoof();
        Drain(document, roof, 5000, 4000);
        string Check() => (string)roof.GetInstanceParameters(document).Single(p => p.Name == "Finished Fall Check (1 in 80)").Value!;

        Assert.Equal(40 * Math.Sqrt(2), RoofDrainage.ValleyFall(roof), 6);
        Assert.StartsWith("Falls at 1 in 80 or steeper", Check());

        Assert.True(roof.GetInstanceParameters(document).Single(p => p.Name == "Drainage Fall (1 in)").TrySet(80.0));
        Assert.StartsWith("Valleys fall at only", Check());
    }

    [Theory]
    [InlineData(5000, 4000, null)]
    [InlineData(50, 4000, "edge")]
    [InlineData(20000, 4000, "Click on the roof")]
    [InlineData(5200, 4100, "already")]
    public void ADrainGoesWellInsideTheRoofAndNotOnAnother(double x, double y, string? why)
    {
        var (document, roof, _, _) = FlatRoof();
        if (why == "already") Drain(document, roof, 5000, 4000);

        var problem = RoofDrainage.Problem(document, roof, new Point2D(x, y));
        if (why is null) Assert.Null(problem);
        else Assert.Contains(why, problem);
    }

    [Fact]
    public void NotInAPitchedRoof()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        Assert.Contains("flat roof", RoofDrainage.Problem(document, roof, new Point2D(5000, 2000)));
    }

    [Fact]
    public void AFasciaAlongItsEdgeFollowsTheFalls()
    {
        var (document, roof, _, _) = FlatRoof();
        Drain(document, roof, 5000, 4000);
        var fascia = new Fascia { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
        fascia.EdgeIds.Add(roof.Edges[0].Id);
        document.Add(fascia);

        var segments = RoofEdgeSweeps.Runs(document, fascia).Single().Segments;
        Assert.True(segments.Count >= 2);
        Assert.All(segments, segment =>
        {
            Assert.Equal(roof.TopAt(document, segment.From.Plan), segment.From.Z, 3);
            Assert.Equal(roof.TopAt(document, segment.To.Plan), segment.To.Z, 3);
        });
    }

    [Fact]
    public void ItGoesWithItsRoofMovedCopiedDeletedSavedAndOpened()
    {
        var (document, roof, _, _) = FlatRoof();
        var drain = Drain(document, roof, 5000, 4000);
        roof.DrainageFall = 60;

        var move = new MoveElementsCommand(new[] { roof }, new Vector2D(1000, 0), document: document);
        move.Redo();
        Assert.Equal(6000, drain.Location.X, 6);
        move.Undo();

        var copies = ElementCopy.Duplicate(document, new[] { roof });
        Assert.Contains(copies.OfType<RoofDrain>(), copy => copy.RoofId == copies.OfType<Roof>().Single().Id);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(drain.Location, reopened.Elements.OfType<RoofDrain>().Single().Location);
        Assert.Equal(60, reopened.Elements.OfType<Roof>().Single().DrainageFall);

        using (var model = IfcExport.Build(document))
            Assert.Single(model.Instances.OfType<IfcBuildingElementProxy>(), proxy => proxy.ObjectType == "Roof Drain");

        new DeleteElementCommand(document, roof).Redo();
        Assert.DoesNotContain(drain, document.Elements);
    }

    [Fact]
    public void TheToolPutsOneInTheFlatRoofClicked()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, roof, _, _) = FlatRoof();
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack() };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.RoofDrain);

                Assert.False(plan.PlaceRoofDrainAt(new Point2D(20000, 4000)));
                Assert.True(plan.PlaceRoofDrainAt(new Point2D(5000, 4000)));
                Assert.False(plan.PlaceRoofDrainAt(new Point2D(5100, 4000)));
                Assert.Single(document.Elements.OfType<RoofDrain>());

                plan.History!.Undo();
                Assert.Empty(document.Elements.OfType<RoofDrain>());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
