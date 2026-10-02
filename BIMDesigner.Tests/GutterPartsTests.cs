using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Gutters by their section - K-style, half round, box, fascia gutter - and what they are made
/// of; the parts along one - end caps, hangers, a leaf screen; and the downpipes taking the
/// water down from it: a drop outlet, a swan neck back to the wall, the pipe down it, a shoe.
/// </summary>
public class GutterPartsTests
{
    [Theory]
    [InlineData(GutterShape.HalfRound)]
    [InlineData(GutterShape.Box)]
    [InlineData(GutterShape.KStyle)]
    [InlineData(GutterShape.Fascia)]
    public void EverySectionIsASheetRoundItsChannel(GutterShape shape)
    {
        var type = new GutterType("Gutter") { Shape = shape, Width = 125, Depth = 100, WallThickness = 1 };
        var form = Gutters.Form(type);

        // A closed sheet, as thick as the type says, all round a channel open at the top.
        Assert.True(Polygon2D.SignedArea(form.Sheet) > 0);
        var line = form.Channel.Zip(form.Channel.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
        Assert.InRange(Polygon2D.Area(form.Sheet), line * 0.8, line * 1.4);
        Assert.True(Polygon2D.Area(form.Channel) > 125 * 40);

        // Out from the roof's edge, hanging below its top, its outlet in the bottom.
        Assert.Equal(0, form.Back);
        Assert.Equal(125, form.Front);
        Assert.Equal(shape == GutterShape.HalfRound ? -62.5 : -100, form.Bottom, 6);
        Assert.InRange(form.Outlet, 10, 115);
        Assert.All(form.Sheet, point => Assert.InRange(point.Y, form.Bottom - 1e-6, form.Rim + 1e-6));
    }

    [Theory]
    [InlineData("Gutter - K-Style 125, Aluminium White", GutterShape.KStyle, "Aluminium, Powder Coated White", DownpipeShape.Rectangular)]
    [InlineData("Gutter - K-Style 125, Vinyl White", GutterShape.KStyle, "PVC-U, White", DownpipeShape.Rectangular)]
    [InlineData("Gutter - K-Style 125, Galvanised Steel", GutterShape.KStyle, "Steel, Galvanised", DownpipeShape.Rectangular)]
    [InlineData("Gutter - Half Round 150, Copper", GutterShape.HalfRound, "Copper", DownpipeShape.Round)]
    [InlineData("Gutter - Half Round 150, Copper Patina", GutterShape.HalfRound, "Copper, Patinated", DownpipeShape.Round)]
    [InlineData("Gutter - Half Round 125, Zinc", GutterShape.HalfRound, "Zinc, Pre-Weathered", DownpipeShape.Round)]
    [InlineData("Gutter - Box 200 x 150, Galvanised Steel", GutterShape.Box, "Steel, Galvanised", DownpipeShape.Square)]
    [InlineData("Gutter - Fascia 120 x 180, Aluminium Anthracite", GutterShape.Fascia, "Aluminium, Powder Coated Anthracite", DownpipeShape.Rectangular)]
    public void EveryStyleAndMaterialIsReadyToUse(string name, GutterShape shape, string material, DownpipeShape pipe)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<GutterType>().Single(candidate => candidate.Name == name);

        Assert.Equal(shape, type.Shape);
        Assert.Equal(material, document.FindMaterial(type.MaterialId)!.Name);
        Assert.Equal(pipe, type.DownpipeShape);
    }

    /// <summary>The house with a hip roof overhanging its walls by 400 mm, and a gutter all round it of a type.</summary>
    private static (BimDocument Document, Roof Roof, Gutter Gutter, GutterType Type) Guttered(string typeName = "Gutter - K-Style 125, Aluminium White", bool dormer = false)
    {
        var house = new HouseEditAuditTests.House(dormer);
        var document = house.Document;
        var roof = house.Dormer ?? house.Roof;
        var type = document.TypesOf<GutterType>().Single(candidate => candidate.Name == typeName);
        var gutter = new Gutter { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = type.Id, HorizontalOffset = 25, VerticalOffset = -40 };
        gutter.EdgeIds.AddRange(RoofEdgeSweeps.GutterEdges(document, roof));
        document.Add(gutter);
        return (document, roof, gutter, type);
    }

    [Fact]
    public void HangersHoldItEverySoOftenAndALeafScreenCoversIt()
    {
        var (document, _, gutter, type) = Guttered();
        int Triangles() => RoofEdgeSweeps.Mesh(document, gutter, gutter.LevelId).Indices.Count / 3;

        var withHangers = Triangles();
        type.HangerSpacing = 0;
        var without = Triangles();

        // Twelve triangles a strap, one every 600 mm or so round the eaves.
        var hangers = (withHangers - without) / 12;
        Assert.Equal(0, (withHangers - without) % 12);
        var length = gutter.Length(document);
        Assert.InRange(hangers, length / 600 - 4, length / 600 + 4);

        type.LeafGuard = true;
        Assert.True(Triangles() > without);
    }

    [Fact]
    public void AnOpenRunIsCappedAtBothEnds()
    {
        var (document, roof, gutter, _) = Guttered();

        // Along one eave only: an open run, with a cap across the channel at each end.
        var eave = RoofEdgeSweeps.GutterEdges(document, roof)[0];
        gutter.EdgeIds.Clear();
        gutter.EdgeIds.Add(eave);
        var run = RoofEdgeSweeps.Runs(document, gutter).Single();
        Assert.False(run.Closed);

        var mesh = RoofEdgeSweeps.Mesh(document, gutter, gutter.LevelId);
        var start = run.Segments[0].From;
        var along = run.Segments[0].Along;

        // Triangles lying square across the run at its start, the channel's own size.
        var capped = Enumerable.Range(0, mesh.Indices.Count / 3)
            .Select(t => new[] { mesh.Positions[mesh.Indices[3 * t]], mesh.Positions[mesh.Indices[3 * t + 1]], mesh.Positions[mesh.Indices[3 * t + 2]] })
            .Count(corners => corners.All(p => Math.Abs((Vector3.Of(p) - start).Dot(along)) < 0.5));
        Assert.True(capped >= 4, $"{capped} triangles across the start of the run");
    }

    [Fact]
    public void ADownpipeRunsFromTheOutletBackToTheWallAndDownToTheGround()
    {
        var (document, _, gutter, type) = Guttered();
        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) };
        document.Add(pipe);

        var path = Downpipes.Path(document, pipe)!;
        Assert.False(path.OntoRoof);

        // From the gutter's bottom, down to a shoe just off the ground at the foot of the wall.
        Assert.Equal(0, path.Bottom, 6);
        Assert.Equal(Downpipes.ShoeRise - Downpipes.ShoeReach, path.Points[^1].Z, 3);
        Assert.True(path.Points[0].Z > 2500);

        // Down the front wall's outside face, standing off it; the outlet out under the gutter.
        var wall = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Structure.TotalWidth;
        Assert.Equal(-(wall / 2 + Downpipes.Clearance + type.DownpipeDepth / 2), path.Foot.Y, 1);
        Assert.True(path.Points[0].Y < path.Foot.Y - 300);
        Assert.Equal(5000, path.Foot.X, 1);

        var mesh = Downpipes.Mesh(document, pipe)!;
        Assert.NotEmpty(mesh.Positions);
        Assert.Equal("Rectangular", pipe.GetInstanceParameters(document).Single(p => p.Name == "Shape").Value);
    }

    [Fact]
    public void ADormersDownpipeLetsItsWaterOntoTheRoofBelow()
    {
        var (document, dormer, gutter, _) = Guttered(dormer: true);
        var main = document.Elements.OfType<Roof>().Single(roof => roof.Id == dormer.JoinedTo);
        var run = RoofEdgeSweeps.Runs(document, gutter).First();
        var middle = run.Segments[0].From + run.Segments[0].Along * (run.Segments[0].Length / 2);

        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = middle.Plan };
        document.Add(pipe);

        var path = Downpipes.Path(document, pipe)!;
        Assert.True(path.OntoRoof);
        Assert.Equal(main.TopAt(document, path.Foot), path.Bottom, 3);
    }

    [Fact]
    public void AtEachEndOfAnOpenGutterAndEachCornerOfOneAllRound()
    {
        var (document, roof, gutter, _) = Guttered();
        Assert.True(RoofEdgeSweeps.Runs(document, gutter).Single().Closed);
        Assert.Equal(4, Downpipes.AtEnds(document, gutter).Count);

        gutter.EdgeIds.Clear();
        gutter.EdgeIds.Add(RoofEdgeSweeps.GutterEdges(document, roof)[0]);
        Assert.Equal(2, Downpipes.AtEnds(document, gutter).Count);
    }

    [Fact]
    public void ItGoesWithItsRoofMovedCopiedDeletedSavedAndOpened()
    {
        var (document, roof, gutter, _) = Guttered();
        var pipe = new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) };
        document.Add(pipe);

        var move = new MoveElementsCommand(new[] { roof }, new Vector2D(2000, 0), document: document);
        move.Redo();
        Assert.Equal(7000, pipe.Location.X, 6);
        move.Undo();

        var copies = ElementCopy.Duplicate(document, new[] { roof });
        var copiedGutter = copies.OfType<Gutter>().Single();
        Assert.Contains(copies.OfType<Downpipe>(), copy => copy.GutterId == copiedGutter.Id);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reopened.Elements.OfType<Downpipe>().Single();
        Assert.Equal(pipe.Location, again.Location);
        Assert.Equal(DownpipeShape.Rectangular, reopened.FindType<GutterType>(gutter.TypeId)!.DownpipeShape);

        var delete = new DeleteElementCommand(document, roof);
        delete.Redo();
        Assert.DoesNotContain(pipe, document.Elements);
        Assert.DoesNotContain(gutter, document.Elements);
        delete.Undo();
        Assert.Contains(pipe, document.Elements);
    }

    [Fact]
    public void InIfcItIsADownpipe()
    {
        var (document, _, gutter, _) = Guttered();
        document.Add(new Downpipe { GutterId = gutter.Id, LevelId = gutter.LevelId, Location = new Point2D(5000, -400) });

        using var model = IfcExport.Build(document);

        Assert.Single(model.Instances.OfType<IfcBuildingElementProxy>(), proxy => proxy.ObjectType == "Downpipe");
    }

    [Fact]
    public void TheToolPutsOneOnTheGutterClickedAndTheButtonOneAtEachCorner()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, roof, gutter, _) = Guttered();
                var plan = new BIMDesigner.UI.Controls.PlanView
                {
                    Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack()
                };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Downpipe);

                Assert.False(plan.PlaceDownpipeAt(new Point2D(5000, 3500)));
                Assert.True(plan.PlaceDownpipeAt(new Point2D(5000, -450)));
                Assert.Equal(gutter.Id, document.Elements.OfType<Downpipe>().Single().GutterId);

                Assert.Equal(4, plan.AddDownpipesAtEnds(new[] { gutter }));
                Assert.Equal(0, plan.AddDownpipesAtEnds(new[] { gutter }));

                plan.History!.Undo();
                Assert.Single(document.Elements.OfType<Downpipe>());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
