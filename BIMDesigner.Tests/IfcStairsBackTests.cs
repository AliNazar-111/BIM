using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;

namespace BIMDesigner.Tests;

/// <summary>A straight stair back from IFC as a stair: from the middle of its first riser, up its flight, as wide, between the same storeys.</summary>
public class IfcStairsBackTests
{
    private static (BimDocument Document, Stair Stair) Straight(StairShape shape = StairShape.Straight)
    {
        var document = BimDocument.CreateDefault();
        var stair = new Stair
        {
            TypeId = document.TypesOf<StairType>().First().Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id,
            Start = new Point2D(2000, 1000), Angle = 90, Width = 900, Shape = shape, Mark = "ST1"
        };
        document.Add(stair);
        return (document, stair);
    }

    private static IfcImportResult Back(BimDocument document)
    {
        using var model = IfcExport.Build(document);
        return IfcImport.Read(model, "Stair");
    }

    [Fact]
    public void AStraightStairComesBackAsAStair()
    {
        var (document, original) = Straight();
        var result = Back(document);
        Assert.Equal(1, result["Stairs"]);
        var stair = Assert.Single(result.Document.All<Stair>());

        // Up the same way from the same first riser, as wide, between the same storeys - so with the same risers.
        Assert.Equal(StairShape.Straight, stair.Shape);
        Assert.Equal(90, stair.Angle, 1);
        Assert.Equal(original.Start.X, stair.Start.X, 0);
        Assert.InRange(stair.Start.Y - original.Start.Y, -30, 30);
        Assert.InRange(stair.Width, 880, 1000);
        Assert.Equal(document.FindLevel(original.TopLevelId!.Value)!.Name, result.Document.FindLevel(stair.TopLevelId!.Value)!.Name);
        Assert.Equal("ST1", stair.Mark);
        Assert.Equal(Stairs.Layout(document, original).Risers, Stairs.Layout(result.Document, stair).Risers);
        Assert.Empty(result.Document.All<Component>());
    }

    [Fact]
    public void AStairThatTurnsComesInAsItsShape()
    {
        var (document, _) = Straight(StairShape.QuarterTurn);
        var result = Back(document);
        Assert.Empty(result.Document.All<Stair>());
        Assert.NotEmpty(result.Document.All<Component>());
    }
}
