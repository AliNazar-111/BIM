using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>
/// Curtain walls back from IFC as curtain walls: an IfcCurtainWall made of its panels and its mullions and transoms, back as a
/// curtain wall on the line its parts run along, with the grid its members stand on and the type it was of.
/// </summary>
public class IfcCurtainWallsBackTests
{
    private static (BimDocument Document, Wall Wall) Storefront()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<CurtainWallType>().First(each => each.Name == "Curtain Wall - Storefront 1500");
        var wall = new Wall { Start = new Point2D(1000, 2000), End = new Point2D(7000, 2000), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000, Mark = "CW1" };
        document.Add(wall);
        return (document, wall);
    }

    [Fact]
    public void AStorefrontComesBackOnItsLineWithItsGrid()
    {
        var (document, original) = Storefront();
        var laid = CurtainLayout.Of(document, original)!;
        Assert.NotEmpty(laid.Verticals);

        using var model = IfcExport.Build(document);
        var result = IfcImport.Read(model, "Storefront");
        var back = result.Document;
        Assert.Equal(1, result["Curtain walls"]);

        // One wall, a curtain wall of the type it was, nothing of it left over as shapes.
        var wall = Assert.Single(back.Walls);
        Assert.True(back.IsCurtainWall(wall));
        Assert.Equal("Curtain Wall - Storefront 1500", back.FindType<CurtainWallType>(wall.TypeId)!.Name);
        Assert.Empty(back.All<Component>());

        // On its line - the middle of its depth - its length and height; drawn the way it was.
        Assert.Equal(6000, wall.Length, 0);
        Assert.Equal(3000, wall.UnconnectedHeight, 0);
        Assert.Equal(original.Start.Y, wall.Start.Y, 0);
        Assert.True(wall.End.X > wall.Start.X);
        Assert.Equal("CW1", wall.Mark);

        // Its grid where its mullions and transoms were: each line to the millimetre.
        // The layout's lines run edge to edge; a wall's own grid holds those between its edges.
        var grid = wall.CurtainGrid!;
        var verticals = laid.Verticals.Where(line => line > 1 && line < laid.Length - 1).ToList();
        var horizontals = laid.Horizontals.Where(line => line > 1 && line < laid.Height - 1).ToList();
        Assert.Equal(verticals.Count, grid.Verticals.Count);
        Assert.All(verticals.Zip(grid.Verticals), pair => Assert.Equal(pair.First, pair.Second, 0));
        Assert.Equal(horizontals.Count, grid.Horizontals.Count);
        Assert.All(horizontals.Zip(grid.Horizontals), pair => Assert.Equal(pair.First, pair.Second, 0));
    }
}
