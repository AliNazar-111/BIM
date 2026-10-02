using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Fascias by what they are made of - timber, uPVC, aluminium, composite, fibre cement - and by
/// the shape of their face: square, a capping board over an old fascia, ogee, round.
/// </summary>
public class FasciaTypeTests
{
    [Theory]
    [InlineData("Fascia - Timber 25 mm, Pine", FasciaProfile.Square, "Timber, Softwood")]
    [InlineData("Fascia - Timber 25 mm, Cedar", FasciaProfile.Square, "Timber, Western Red Cedar")]
    [InlineData("Fascia - uPVC Square 18 mm, White", FasciaProfile.Square, "PVC-U, White")]
    [InlineData("Fascia - uPVC Capping 9 mm, White", FasciaProfile.Capping, "PVC-U, White")]
    [InlineData("Fascia - uPVC Ogee 18 mm, White", FasciaProfile.Ogee, "PVC-U, White")]
    [InlineData("Fascia - uPVC Round 18 mm, White", FasciaProfile.Round, "PVC-U, White")]
    [InlineData("Fascia - Aluminium Square, Anthracite", FasciaProfile.Square, "Aluminium, Powder Coated Anthracite")]
    [InlineData("Fascia - Composite 25 mm, Grey", FasciaProfile.Square, "Composite, Wood-Plastic Grey")]
    [InlineData("Fascia - Fibre Cement 12 mm", FasciaProfile.Square, "Fibre Cement Board")]
    [InlineData("Fascia - 25 mm Board, White", FasciaProfile.Moulded, "Timber Fascia, Painted White")]
    public void EveryMaterialAndProfileIsReadyToUse(string name, FasciaProfile profile, string material)
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<FasciaType>().Single(candidate => candidate.Name == name);

        Assert.Equal(profile, type.Profile);
        Assert.Equal(material, document.FindMaterial(type.MaterialId)!.Name);
    }

    [Theory]
    [InlineData(FasciaProfile.Moulded)]
    [InlineData(FasciaProfile.Square)]
    [InlineData(FasciaProfile.Capping)]
    [InlineData(FasciaProfile.Ogee)]
    [InlineData(FasciaProfile.Round)]
    public void EveryProfileRunsFromTheRoofsTopDownPastItsEdge(FasciaProfile profile)
    {
        var section = RoofEdgeSweeps.FasciaSection(profile, 25, 300);

        // Wound anticlockwise, from the roof's edge out, and from its top down to the drip.
        Assert.True(Polygon2D.SignedArea(section) > 0);
        Assert.Equal(0, section.Min(point => point.X), 6);
        Assert.Equal(0, section.Max(point => point.Y), 6);
        Assert.Equal(-300, section.Min(point => point.Y), 6);
        Assert.True(section.Max(point => point.X) >= 25 - 1e-6);
    }

    [Fact]
    public void EachProfileHasTheShapeItIsNamedFor()
    {
        double Area(FasciaProfile profile) => Polygon2D.Area(RoofEdgeSweeps.FasciaSection(profile, 25, 300));
        var square = Area(FasciaProfile.Square);

        Assert.Equal(25 * 300, square, 6);

        // The ogee's S stands out beyond the square board; the round one is cut away at its foot.
        Assert.True(Area(FasciaProfile.Ogee) > square);
        Assert.True(Area(FasciaProfile.Round) < square);

        // A capping board is a thin face over the old board and a leg over its top: far less than a solid board.
        Assert.True(Area(FasciaProfile.Capping) < square / 2);
    }

    [Fact]
    public void ChangingTheProfileIsATypeChangeAndSavedWithIt()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<FasciaType>().Single(candidate => candidate.Name == "Fascia - 25 mm Board, White");

        Assert.True(type.GetTypeParameters(document).Single(parameter => parameter.Name == "Profile").TrySet("Ogee"));
        Assert.Equal(FasciaProfile.Ogee, type.Profile);

        var again = ProjectFile.FromJson(ProjectFile.ToJson(document)).FindType<FasciaType>(type.Id)!;
        Assert.Equal(FasciaProfile.Ogee, again.Profile);
        Assert.Equal(FasciaProfile.Ogee, type.Duplicate("Copy").Profile);
    }

    [Fact]
    public void LeftToItselfFasciaAllRoundStillPicksAPlainBoard()
    {
        var house = new HouseEditAuditTests.House(dormer: false);
        Assert.Equal(FasciaProfile.Moulded, RoofEdgeSweeps.ContrastingFascia(house.Document, house.Roof)!.Profile);
    }

    [Fact]
    public void ARoundFasciaIsBuiltAllRoundTheRoof()
    {
        var house = new HouseEditAuditTests.House(dormer: false);
        var (document, roof) = (house.Document, house.Roof);
        var fascia = new Fascia
        {
            RoofId = roof.Id, LevelId = roof.LevelId,
            TypeId = document.TypesOf<FasciaType>().Single(type => type.Profile == FasciaProfile.Round).Id
        };
        fascia.EdgeIds.AddRange(RoofEdgeSweeps.FasciaEdges(document, roof));
        document.Add(fascia);

        var mesh = RoofEdgeSweeps.Mesh(document, fascia, fascia.LevelId);
        Assert.NotEmpty(mesh.Positions);
        Assert.True(RoofEdgeSweeps.Runs(document, fascia).Single().Closed);
    }
}
