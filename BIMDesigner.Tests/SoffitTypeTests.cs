using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Kernel;

namespace BIMDesigner.Tests;

/// <summary>
/// Soffits by the air they let into the roof - solid, vented all over, vented down the middle,
/// hollow - and by what they are made of: uPVC, aluminium, timber, fibre cement, steel.
/// </summary>
public class SoffitTypeTests
{
    private static (BimDocument Document, Soffit Soffit) Under(string typeName)
    {
        var house = new HouseEditAuditTests.House(dormer: false);
        var (document, roof) = (house.Document, house.Roof);
        var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<SoffitType>().Single(type => type.Name == typeName).Id };
        soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, roof));
        document.Add(soffit);
        return (document, soffit);
    }

    [Theory]
    [InlineData("Soffit - uPVC Solid 10 mm", SoffitBoard.Solid, "PVC-U, White", 0)]
    [InlineData("Soffit - uPVC Vented 10 mm", SoffitBoard.Vented, "PVC-U, White", 25000)]
    [InlineData("Soffit - uPVC Centre Vented 10 mm", SoffitBoard.CentreVented, "PVC-U, White", 10000)]
    [InlineData("Soffit - uPVC Hollow 9 mm", SoffitBoard.Hollow, "PVC-U, White", 0)]
    [InlineData("Soffit - Aluminium Vented 1 mm", SoffitBoard.Vented, "Aluminium, Anodised", 25000)]
    [InlineData("Soffit - Timber 18 mm", SoffitBoard.Solid, "Timber, Softwood", 0)]
    [InlineData("Soffit - Fibre Cement 9 mm", SoffitBoard.Solid, "Fibre Cement Board", 0)]
    [InlineData("Soffit - Steel 0.7 mm, Colour Coated", SoffitBoard.Solid, "Steel, Colour Coated", 0)]
    public void EveryKindIsReadyToUse(string name, SoffitBoard board, string material, double freeAir)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<SoffitType>().Single(candidate => candidate.Name == name);

        Assert.Equal(board, type.Board);
        Assert.Equal(material, document.FindMaterial(type.MaterialId)!.Name);
        Assert.Equal(freeAir, type.FreeAirArea);
    }

    [Fact]
    public void AVentedBoardShowsItsSlotsACentreVentedOneABandOfThemAHollowOneItsPlanksAndASolidOneNothing()
    {
        int Marks(string name)
        {
            var (document, soffit) = Under(name);
            return RoofEdgeSweeps.Mesh(document, soffit, soffit.LevelId).Edges.Count;
        }

        var solid = Marks("Soffit - uPVC Solid 10 mm");
        var vented = Marks("Soffit - uPVC Vented 10 mm");
        var centre = Marks("Soffit - uPVC Centre Vented 10 mm");
        var hollow = Marks("Soffit - uPVC Hollow 9 mm");

        Assert.True(vented > centre, $"vented {vented}, centre vented {centre}");
        Assert.True(centre > solid, $"centre vented {centre}, solid {solid}");
        Assert.True(hollow > solid, $"hollow {hollow}, solid {solid}");

        // Every mark is on the board's underside, inside its outline.
        var (ventedDocument, ventedSoffit) = Under("Soffit - uPVC Vented 10 mm");
        var pieces = RoofEdgeSweeps.SoffitPieces(ventedDocument, ventedSoffit);
        var outlines = pieces.Select(piece => piece.Outline).ToList();
        var edges = RoofEdgeSweeps.Mesh(ventedDocument, ventedSoffit, ventedSoffit.LevelId).Edges;
        var underside = pieces.Min(piece => piece.Bottom) - 0.25;
        Assert.All(edges.Where(edge => edge.From.Z < underside), edge =>
            Assert.Contains(outlines, outline => Core.Geometry.Polygon2D.Contains(outline, new Core.Geometry.Point2D(edge.From.X, edge.From.Y))));
    }

    [Fact]
    public void ItsAirIsWhatItLetsThroughAMetreAllAlongIt()
    {
        var (document, soffit) = Under("Soffit - uPVC Vented 10 mm");

        var air = soffit.GetInstanceParameters(document).Single(parameter => parameter.Name == "Free Air Area");
        Assert.Equal(25000 * soffit.Length(document) / 1000, (double)air.Value!, 3);
    }

    [Fact]
    public void ChangingTheBoardBringsItsAirWithItAndASolidOneLetsNoneThrough()
    {
        var type = new SoffitType("Board");
        var parameters = type.GetTypeParameters().ToList();

        Assert.True(parameters.Single(parameter => parameter.Name == "Board").TrySet("Vented"));
        Assert.Equal(SoffitBoard.Vented, type.Board);
        Assert.Equal(25000, type.FreeAirArea);

        Assert.True(parameters.Single(parameter => parameter.Name == "Free Air Area (mm²/m)").TrySet(15000.0));
        Assert.Equal(15000, type.FreeAirArea);

        Assert.True(parameters.Single(parameter => parameter.Name == "Board").TrySet("Solid"));
        Assert.Equal(0, type.FreeAirArea);
        Assert.False(parameters.Single(parameter => parameter.Name == "Free Air Area (mm²/m)").TrySet(5000.0));
    }

    [Fact]
    public void SavedAndOpenedItIsTheSameBoard()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<SoffitType>().Single(candidate => candidate.Name == "Soffit - uPVC Centre Vented 10 mm");
        type.FreeAirArea = 12000;

        var again = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<SoffitType>(type.Id)!;

        Assert.Equal(SoffitBoard.CentreVented, again.Board);
        Assert.Equal(12000, again.FreeAirArea);
    }

    [Fact]
    public void AProjectFromBeforeGainsThemWithoutASecondCopyOfItsMaterials()
    {
        var document = BimDocument.CreateDefault();
        foreach (var type in document.TypesOf<SoffitType>().Where(type => type.Name != "Soffit - 12 mm Board").ToList())
            document.RemoveType(type);

        document.EnsureDefaultTypes();

        Assert.Equal(9, document.TypesOf<SoffitType>().Count());
        var names = document.Materials.Select(material => material.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(document.TypesOf<SoffitType>(), type => Assert.NotNull(document.FindMaterial(type.MaterialId)));
    }

    [Fact]
    public void InIfcASoffitSaysWhatBoardItIsAndItsAir()
    {
        var (document, _) = Under("Soffit - uPVC Vented 10 mm");

        using var model = IfcExport.Build(document);

        var set = model.Instances.OfType<IfcPropertySet>().Single(candidate => candidate.Name == "Soffit");
        Assert.Contains(set.HasProperties, property => property.Name == "Board");
        Assert.Contains(set.HasProperties, property => property.Name == "FreeAirArea");
    }
}
