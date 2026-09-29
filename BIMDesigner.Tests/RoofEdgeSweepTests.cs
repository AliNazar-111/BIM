using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Fascias and gutters (specification section 3.3; Revit's Roof: Fascia and Roof: Gutter): a
/// profile run along a roof's edges, round its corners and up its verges, following the roof.
/// </summary>
[Collection("Wpf")]
public class RoofEdgeSweepTests
{
    /// <summary>The audit's house - walls drawn clockwise and a hip roof picked off them - made a gable if asked.</summary>
    private static HouseEditAuditTests.House House(bool gable = false)
    {
        var house = new HouseEditAuditTests.House(dormer: false);
        if (!gable) return house;

        // The ends of the house run north-south: those edges stop sloping, and become verges.
        var roof = house.Roof;
        var edges = roof.Edges.Select(edge => edge.Copy()).ToList();
        for (var i = 0; i < edges.Count; i++)
            if (Math.Abs(roof.Boundary[i].X - roof.Boundary[(i + 1) % edges.Count].X) < 1) edges[i].DefinesSlope = false;
        roof.SetEdges(edges);
        return house;
    }

    private static T Add<T>(BimDocument document, Roof roof, IEnumerable<Guid> edges) where T : RoofEdgeSweep, new()
    {
        var sweep = new T
        {
            RoofId = roof.Id, LevelId = roof.LevelId,
            TypeId = typeof(T) == typeof(Gutter) ? document.TypesOf<GutterType>().First().Id : document.TypesOf<FasciaType>().First().Id
        };
        sweep.EdgeIds.AddRange(edges);
        document.Add(sweep);
        return sweep;
    }

    [Fact]
    public void AFasciaAllRoundAHipRoofIsOneClosedRunCoveringItsEdge()
    {
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));

        var run = Assert.Single(RoofEdgeSweeps.Runs(document, fascia));
        Assert.True(run.Closed);
        Assert.Equal(4, run.Segments.Count);

        // Level all round, at the roof's top outer edge, and as deep as the roof's edge there.
        var type = document.FindType<SlabType>(roof.TypeId)!;
        var stretch = Math.Sqrt(1 + Math.Pow(Math.Tan(42 * Math.PI / 180), 2));
        foreach (var segment in run.Segments)
        {
            Assert.Equal(segment.From.Z, segment.To.Z, 3);
            // Down past the edge by the drip that hides the soffit's front.
            Assert.Equal(type.Thickness * stretch + RoofEdgeSweeps.FasciaDrip, -segment.Profile.Min(point => point.Y), 1);
            Assert.InRange(roof.TopAt(document, segment.From.Plan.MidpointTo(segment.To.Plan) + new Vector2D(-segment.Out.X * 5, -segment.Out.Y * 5)) - segment.From.Z, 0, 6);
        }

        // Round the corners each board meets the next: the end of one is the start of the next.
        var rings = RoofEdgeSweeps.Rings(run);
        for (var s = 0; s < rings.Count; s++)
        {
            var (_, end) = rings[s];
            var (start, _) = rings[(s + 1) % rings.Count];
            for (var k = 0; k < end.Count; k++) Assert.True((end[k] - start[k]).Length < 1, $"A corner does not meet at {s}.");
        }
    }

    [Fact]
    public void GuttersGoAlongTheEavesOfAGableAndTheFasciaUpItsVerges()
    {
        var house = House(gable: true);
        var (document, roof) = (house.Document, house.Roof);

        var gutter = Add<Gutter>(document, roof, RoofEdgeSweeps.GutterEdges(document, roof));
        var eaves = RoofEdgeSweeps.Runs(document, gutter);
        Assert.Equal(2, eaves.Count);
        Assert.All(eaves, run => Assert.False(run.Closed));
        Assert.All(eaves.SelectMany(run => run.Segments), segment => Assert.Equal(segment.From.Z, segment.To.Z, 3));

        // A verge climbs to the ridge and down again.
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));
        var segments = RoofEdgeSweeps.Runs(document, fascia).Single().Segments;
        Assert.Contains(segments, segment => segment.To.Z > segment.From.Z + 100);
        Assert.Contains(segments, segment => segment.To.Z < segment.From.Z - 100);
        Assert.True(Math.Abs(segments.Max(segment => segment.To.Z) - roof.Surface(document).RidgeElevation) < 1000);
    }

    [Fact]
    public void ItStaysOnItsEdgesWhenTheRoofIsEditedAndFollowsItsPitch()
    {
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        var south = roof.Edges.First(edge => edge.DefinesSlope);
        var gutter = Add<Gutter>(document, roof, new[] { south.Id });
        var before = RoofEdgeSweeps.Runs(document, gutter).Single().Segments.Single();

        // A line added before it in the outline, which moves it along the list of edges.
        var boundary = roof.Boundary.ToList();
        var edges = roof.Edges.Select(edge => edge.Copy()).ToList();
        boundary.Insert(1, boundary[0].MidpointTo(boundary[1]));
        edges.Insert(0, new RoofEdge { DefinesSlope = edges[0].DefinesSlope, SlopeDegrees = edges[0].SlopeDegrees });
        new SetRoofSketchCommand(roof, boundary, edges).Redo();

        Assert.Contains(RoofEdgeSweeps.Runs(document, gutter).SelectMany(run => run.Segments),
            segment => Math.Abs(segment.From.Z - before.From.Z) < 1);

        // Steeper, the roof's top edge is where it was - the eave - but deeper.
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));
        var shallow = -RoofEdgeSweeps.Runs(document, fascia).Single().Segments[0].Profile.Min(point => point.Y);
        new SetRoofSketchCommand(roof, roof.Boundary, roof.Edges.Select(edge => { var copy = edge.Copy(); copy.SlopeDegrees = 55; return copy; })).Redo();
        Assert.True(-RoofEdgeSweeps.Runs(document, fascia).Single().Segments[0].Profile.Min(point => point.Y) > shallow + 50);
    }

    [Fact]
    public void ItIsSavedCopiedWithItsRoofAndDeletedWithIt()
    {
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));
        var gutter = Add<Gutter>(document, roof, RoofEdgeSweeps.GutterEdges(document, roof));
        gutter.HorizontalOffset = 25;

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reopened.Elements.OfType<RoofEdgeSweep>().ToList();
        Assert.Equal(2, again.Count);
        Assert.Equal(25, again.OfType<Gutter>().Single().HorizontalOffset);
        Assert.Equal(RoofEdgeSweeps.Runs(document, fascia).Single().Segments.Count,
            RoofEdgeSweeps.Runs(reopened, again.OfType<Fascia>().Single()).Single().Segments.Count);

        // Copied with its roof, it runs along the copy.
        var copies = ElementCopy.Duplicate(document, new[] { roof });
        var copiedRoof = copies.OfType<Roof>().Single();
        Assert.Equal(2, copies.OfType<RoofEdgeSweep>().Count());
        Assert.All(copies.OfType<RoofEdgeSweep>(), copy => Assert.Equal(copiedRoof.Id, copy.RoofId));

        // Deleted with it.
        new DeleteElementsCommand(document, new[] { roof }).Redo();
        Assert.Empty(document.Elements.OfType<RoofEdgeSweep>());
    }

    [Fact]
    public void ItIsBuiltIn3DCutInSectionAndExported()
    {
        var house = House(gable: true);
        var (document, roof) = (house.Document, house.Roof);
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));
        var gutter = Add<Gutter>(document, roof, RoofEdgeSweeps.GutterEdges(document, roof));

        var meshes = ModelMeshBuilder.Build(document);
        Assert.Contains(meshes, mesh => mesh.ElementId == fascia.Id && !mesh.IsEmpty);
        Assert.Contains(meshes, mesh => mesh.ElementId == gutter.Id && !mesh.IsEmpty);

        // A section across the eaves cuts both.
        var marker = new SectionMarker { Start = new Point2D(5000, -2000), End = new Point2D(5000, 9000), LevelId = roof.LevelId };
        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.Depth == SectionDepth.Cut).ToList();
        Assert.Equal(2, cut.Count(piece => piece.ElementId == fascia.Id));
        Assert.Equal(2, cut.Count(piece => piece.ElementId == gutter.Id));

        var path = Path.Combine(Path.GetTempPath(), $"roof-edges-{Guid.NewGuid():N}.ifc");
        try
        {
            IfcExport.Save(document, path);
            var ifc = File.ReadAllText(path);
            Assert.Contains("'Fascia'", ifc);
            Assert.Contains("'Gutter'", ifc);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EdgesArePickedOneAfterAnotherIntoOneFasciaAndAllRoundInOneClick()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new BIMDesigner.UI.App().InitializeComponent();

                var house = House();
                var (document, roof) = (house.Document, house.Roof);
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = roof.LevelId, Width = 1000, Height = 800 };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Fascia);

                var count = roof.Boundary.Count;
                Point2D Middle(int i) => roof.Boundary[i].MidpointTo(roof.Boundary[(i + 1) % count]);

                // Two edges picked: one fascia along both. The first again: taken off it.
                Assert.True(plan.PickRoofEdgeAt(Middle(0)));
                Assert.True(plan.PickRoofEdgeAt(Middle(1)));
                var fascia = Assert.Single(document.Elements.OfType<Fascia>());
                Assert.Equal(2, fascia.EdgeIds.Count);
                plan.PickRoofEdgeAt(Middle(0));
                Assert.Single(fascia.EdgeIds);

                // Esc, and the next pick starts another.
                plan.CancelPendingOperation();
                plan.PickRoofEdgeAt(Middle(2));
                Assert.Equal(2, document.Elements.OfType<Fascia>().Count());

                // Gutters all round in one click: every eave, hung off the fascia's face.
                var gutter = plan.AddAllRound(roof, gutter: true)!;
                Assert.Equal(RoofEdgeSweeps.GutterEdges(document, roof).Count, gutter.EdgeIds.Count);
                Assert.Equal(document.TypesOf<FasciaType>().First().Thickness, gutter.HorizontalOffset);
                Assert.Null(plan.AddAllRound(roof, gutter: true));

                var soffits = plan.AddAllRound(roof, BIMDesigner.UI.Controls.RoofEdgeKind.Soffit)!;
                Assert.IsType<Soffit>(soffits);
                Assert.Equal(4, soffits.EdgeIds.Count);
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    [Fact]
    public void ASoffitClosesTheOverhangBackToTheWallUnderTheFascia()
    {
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        var fascia = Add<Fascia>(document, roof, RoofEdgeSweeps.FasciaEdges(document, roof));
        var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<SoffitType>().First().Id };
        soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, roof));
        document.Add(soffit);

        // Under all four eaves of the hip, level, tucked behind the fascia's drip.
        var pieces = RoofEdgeSweeps.SoffitPieces(document, soffit);
        Assert.Equal(4, pieces.Count);
        var fasciaBottom = RoofEdgeSweeps.Runs(document, fascia).Single().Segments
            .Select(segment => segment.From.Z + segment.Profile.Min(point => point.Y)).First();
        Assert.All(pieces, piece => Assert.Equal(fasciaBottom + RoofEdgeSweeps.FasciaDrip, piece.Top, 1));

        // From the roof's edge in to the walls' outside faces: their area is the overhang's.
        var wallFaces = Polygon2D.Area(new[]
        {
            new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 7165), new Point2D(-165, 7165)
        });
        Assert.Equal(Polygon2D.Area(roof.Boundary) - wallFaces, pieces.Sum(piece => Polygon2D.Area(piece.Outline)), 1000.0);

        // A gable has soffits under its two eaves only.
        var gable = House(gable: true);
        Assert.Equal(2, RoofEdgeSweeps.SoffitEdges(gable.Document, gable.Roof).Count);

        // Saved, cut in section and built.
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Single(reopened.Elements.OfType<Soffit>());
        var marker = new SectionMarker { Start = new Point2D(5000, -2000), End = new Point2D(5000, 9000), LevelId = roof.LevelId };
        Assert.Equal(2, SectionProjection.Build(document, marker).Pieces.Count(piece => piece.ElementId == soffit.Id));
        Assert.Contains(ModelMeshBuilder.Build(document), mesh => mesh.ElementId == soffit.Id && !mesh.IsEmpty);
    }

    [Fact]
    public void ARoofFlushWithItsWallsIsGivenAnOverhangAndThenTakesSoffits()
    {
        // Picked off the walls' outside faces with no overhang: nothing under the eaves to close.
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        var flush = roof.Edges.Select(edge => { var copy = edge.Copy(); copy.Overhang = 0; return copy; }).ToList();
        roof.SetEdges(flush);
        RoofSketch.FollowWalls(document);
        var area = roof.Area;
        Assert.Empty(RoofEdgeSweeps.SoffitEdges(document, roof));

        // Overhang in Properties: every edge picked from a wall stands out 450 mm, and the
        // outline follows.
        var overhang = roof.GetInstanceParameters(document).Single(parameter => parameter.Name == "Overhang");
        Assert.False(overhang.IsReadOnly);
        Assert.True(overhang.TrySet(450d));
        RoofSketch.FollowWalls(document);

        Assert.All(roof.Edges, edge => Assert.Equal(450, edge.Overhang, 6));
        Assert.Equal((10330 + 900) * (7330 + 900), roof.Area, 0);
        Assert.True(roof.Area > area);
        Assert.Equal(4, RoofEdgeSweeps.SoffitEdges(document, roof).Count);
    }

    [Fact]
    public void LeftToItselfAFasciaIsTheOneThatStandsOut()
    {
        // A light roof: the dark board, whatever the walls.
        var house = House();
        var (document, roof) = (house.Document, house.Roof);
        Assert.Contains("Anthracite", RoofEdgeSweeps.ContrastingFascia(document, roof)!.Name);

        // A dark roof on pale walls: the white one.
        var top = document.FindType<SlabType>(roof.TypeId)!.Structure.Layers[0].MaterialId;
        document.FindMaterial(top)!.SurfaceColour = BIMDesigner.Core.Materials.ColourRgb.FromHex("3A3C40");
        foreach (var material in document.Materials.Where(material => material.Name.StartsWith("Brick")))
            material.SurfaceColour = BIMDesigner.Core.Materials.ColourRgb.FromHex("C8C4B8");
        Assert.Contains("White", RoofEdgeSweeps.ContrastingFascia(document, roof)!.Name);
    }
}
