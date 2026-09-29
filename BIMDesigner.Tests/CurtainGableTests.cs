using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// A curtain wall under the end of a gable roof, its top attached to the roof: its glass runs
/// up to the ridge, as Revit's Attach Top takes one - the grid set out to the full height and
/// cut off along the rake, the panes shaped to it and a mullion run up it.
/// </summary>
public class CurtainGableTests
{
    /// <summary>An 8 m by 6 m house under a 40° roof, gable ends east and west; the west end a storefront attached to the roof.</summary>
    private static (BimDocument Document, Roof Roof, Wall Gable) House()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(8000, 0), new Point2D(8000, 6000), new Point2D(0, 6000) });
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge()
        });
        document.Add(roof);

        var gable = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(0, 6000), LevelId = level, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront")).Id,
            TopAttachedTo = roof.Id,
            CurtainGrid = new CurtainGrid(new[] { 1500.0, 3000, 4500 }, new[] { 2500.0, 4500 })
        };
        document.Add(gable);

        return (document, roof, gable);
    }

    [Fact]
    public void AttachedToAGableRoofItRisesToTheRidge()
    {
        var (document, roof, gable) = House();
        var layout = CurtainLayout.Of(document, gable)!;

        Assert.NotNull(layout.TopLine);
        Assert.True(layout.TopAt(3000) > 5000, $"only {layout.TopAt(3000):0} high under the ridge");
        Assert.True(layout.TopAt(0) < 3500 && layout.TopAt(6000) < 3500);
        Assert.True(layout.Height > 5000);

        // Glass up in the gable, above the eaves.
        Assert.Contains(layout.Cells, cell => layout.ClearPieces(cell).Any(piece => Math.Max(piece.TopFrom, piece.TopTo) > 4800));

        // Detached, it is its own height again.
        gable.TopAttachedTo = null;
        Assert.Null(CurtainLayout.Of(document, gable)!.TopLine);
    }

    [Fact]
    public void CellsWhollyAboveTheRakeAreNotThere()
    {
        var (document, _, gable) = House();
        var layout = CurtainLayout.Of(document, gable)!;

        // The top row starts at 4.5 m: over the corner bays the rake is lower than that; over the
        // middle ones, higher.
        Assert.DoesNotContain(layout.Cells, cell => cell.Row == 2 && cell.Column is 0 or 3);
        Assert.Contains(layout.Cells, cell => cell.Row == 2 && cell.Column is 1);
        Assert.Contains(layout.Mullions, mullion => !mullion.IsVertical && mullion.IsBorder && mullion.IsSloped);
    }

    [Fact]
    public void NothingOfItStandsUpThroughTheRoof()
    {
        var (document, roof, gable) = House();
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == gable.Id || mesh.OwnerId == gable.Id).ToList();
        Assert.NotEmpty(meshes);

        var surface = roof.Surface(document);
        var highest = 0.0;
        foreach (var point in meshes.SelectMany(mesh => mesh.Positions))
        {
            var at = new Point2D(Math.Max(point.X, 1), point.Y);
            if (!roof.Contains(at)) continue;

            Assert.True(point.Z <= surface.HeightAt(at) + 1, $"({point.X:0}, {point.Y:0}, {point.Z:0}) is above the roof's underside");
            highest = Math.Max(highest, point.Z);
        }

        Assert.True(highest > 5000);
    }

    [Fact]
    public void ItsGlassIsOneSheetNotTwoHalvesMeetingUnderTheRidge()
    {
        var (document, _, gable) = House();
        gable.TypeId = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Glazed")).Id;
        gable.CurtainGrid = null;

        // One pane, the shape of the gable: five corners, three triangles a face.
        var glass = ModelMeshBuilder.Build(document).Single(mesh => mesh.OwnerId == gable.Id && mesh.Kind == MeshKind.Glazing);
        Assert.Equal(6, glass.TriangleCount);

        // And the frame along its bottom is one bar, not two meeting under the ridge.
        var layout = CurtainLayout.Of(document, gable)!;
        Assert.Single(layout.Mullions, mullion => !mullion.IsVertical && mullion.Bottom < 1);
    }

    [Fact]
    public void ASectionThroughItShowsTheGlassUpToTheRidge()
    {
        var (document, _, gable) = House();
        var marker = new SectionMarker
        {
            Name = "A", Start = new Point2D(-2000, 3000), End = new Point2D(4000, 3000), LevelId = document.Levels[0].Id, ViewDepth = 5000
        };
        document.Add(marker);

        var cut = SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == gable.Id && piece.Depth == SectionDepth.Cut)
            .ToList();

        Assert.NotEmpty(cut);
        Assert.True(cut.Max(piece => piece.Bounds.Top) > 5000);
    }
}
