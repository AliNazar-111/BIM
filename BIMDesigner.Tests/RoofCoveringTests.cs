using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Roof coverings by their external fire designation (round 90), as Approved Document B Volume 1 (12.3, Table 12.1) keeps them from the
/// relevant boundary: BROOF(t4) anywhere, CROOF(t4) 6 m off, the rest further and only in part, by notes 2 and 3.
/// </summary>
[Collection("Wpf")]
public class RoofCoveringTests
{
    /// <summary>
    /// A house 8 m by 6 m, a room in it, a flat roof over it on the first floor 8.6 m by 6.6 m - its eaves 300 mm out - of slate on rafters;
    /// and the site's property line so far south of the roof's edge.
    /// </summary>
    private static (BimDocument Document, Roof Roof, Room Room) House(double boundary)
    {
        var document = BimDocument.CreateDefault();
        var (ground, first) = (document.Levels[0], document.Levels[1]);
        var brick = document.TypesOf<WallType>().First(type => type.Name == "Exterior - Brick on Block 330mm");
        var corners = new[] { new Point2D(0, 0), new Point2D(0, 6000), new Point2D(8000, 6000), new Point2D(8000, 0) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = brick.Id, LevelId = ground.Id, UnconnectedHeight = 3000 });
        var room = new Room { LevelId = ground.Id, Location = new Point2D(4000, 3000), Name = "Living Room" };
        document.Add(room);
        var roof = new Roof { LevelId = first.Id, TypeId = document.TypesOf<RoofType>().First(type => type.Name == "Roof - Slate on Timber Rafters 150mm").Id };
        roof.SetBoundary(new[] { new Point2D(-300, -300), new Point2D(8300, -300), new Point2D(8300, 6300), new Point2D(-300, 6300) });
        document.Add(roof);
        var line = new PropertyLine { LevelId = ground.Id };
        line.SetBoundary(new[] { new Point2D(-30000, -300 - boundary), new Point2D(40000, -300 - boundary), new Point2D(40000, 40000), new Point2D(-30000, 40000) });
        document.Add(line);
        return (document, roof, room);
    }

    /// <summary>A roof type covered in felt - its maker's designation, if one is given.</summary>
    private static RoofType Felt(BimDocument document, string designation)
    {
        var felt = new Material("Roof Felt, Torch-On") { Density = 1100, RoofCovering = designation };
        document.AddMaterial(felt);
        var softwood = document.Materials.First(material => material.Name == "Timber, Softwood");
        var plasterboard = document.Materials.First(material => material.Name == "Gypsum Plasterboard");
        var type = new RoofType("Roof - Felt on Rafters", new CompoundStructure(
            new MaterialLayer(LayerFunction.Finish1, felt.Id, 5), new MaterialLayer(LayerFunction.Structure, softwood.Id, 150), new MaterialLayer(LayerFunction.Finish2, plasterboard.Id, 12.5)));
        document.AddType(type);
        return type;
    }

    [Fact]
    public void DesignationsAreReadAndSomeAreGivenWithoutTesting()
    {
        Assert.True(RoofDesignation.TryParse("Broof (t4)", out var b));
        Assert.Equal("BROOF(t4)", b.ToString());
        Assert.True(RoofDesignation.TryParse("d", out var d));
        Assert.Equal(RoofDesignation.D, d);
        Assert.False(RoofDesignation.TryParse("GROOF(t4)", out _));

        Assert.Equal(RoofDesignation.B, RoofDesignation.Typical("Roof Slate, Blue-Grey", 18)!.Value.Designation);
        Assert.Equal(RoofDesignation.B, RoofDesignation.Typical("Roof Tile, Clay Plain", 28)!.Value.Designation);
        Assert.Equal(RoofDesignation.E, RoofDesignation.Typical("Thatch, Water Reed", 300)!.Value.Designation);
        Assert.Equal(RoofDesignation.B, RoofDesignation.Typical("Glass, Clear", 6)!.Value.Designation);
        Assert.Null(RoofDesignation.Typical("Glass, Clear", 3));
        Assert.Null(RoofDesignation.Typical("Roof Felt, Torch-On", 5));
    }

    [Fact]
    public void ACoveringIsKeptFromTheBoundaryByItsDesignation()
    {
        // Slate, BROOF(t4) without testing, 3 m from the boundary: allowed anywhere.
        var (document, roof, _) = House(3000);
        Assert.Empty(RoofCoverings.Problems(document, roof));

        // Felt of DROOF(t4) 3 m off: only BROOF(t4) within 6 m.
        roof.TypeId = Felt(document, "DROOF(t4)").Id;
        var (said, rule) = Assert.Single(RoofCoverings.Problems(document, roof));
        Assert.Equal("its covering, the Roof Felt, Torch-On, is DROOF(t4), 3.0 m from the boundary.", said);
        Assert.Equal("Approved Document B (12.3, Table 12.1) does not allow a DROOF(t4) covering within 6 m of the relevant boundary - BROOF(t4) only.", rule);
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == said);

        // 8 m off: only in parts of 3 m² at most (note 3) - the whole roof is 8.6 by 6.6, 56.8 m².
        (document, roof, _) = House(8000);
        roof.TypeId = Felt(document, "DROOF(t4)").Id;
        Assert.EndsWith("only in parts of 3 m² at most, 1.5 m apart, the roof between them class A2-s3, d2 or better - this roof is 56.8 m².", Assert.Single(RoofCoverings.Problems(document, roof)).Rule);

        // 15 m off: allowed on a house this small - its room 7.67 by 5.67 m, 3 m high, 130 m³.
        (document, roof, _) = House(15000);
        roof.TypeId = Felt(document, "DROOF(t4)").Id;
        Assert.Equal(130, RoofCoverings.CubicCapacity(document), 0);
        Assert.Empty(RoofCoverings.Problems(document, roof));
    }

    [Fact]
    public void ATerraceOrABigBuildingKeepsTheLowerCoveringsFurtherOff()
    {
        // The house one of a terrace of three: DROOF(t4) 15 m off is not allowed on it (note 2).
        var (document, roof, room) = House(15000);
        roof.TypeId = Felt(document, "DROOF(t4)").Id;
        room.Dwelling = "No. 1";
        foreach (var (name, x) in new[] { ("No. 2", 12000.0), ("No. 3", 20000.0) })
            document.Add(new Room { LevelId = room.LevelId, Location = new Point2D(x, 3000), Name = "Living Room", Dwelling = name });
        Assert.Equal("Approved Document B (Table 12.1, note 2) does not allow a DROOF(t4) covering 12 m to 20 m from the relevant boundary on a terrace of three or more houses - this one 3.",
            Assert.Single(RoofCoverings.Problems(document, roof)).Rule);

        // EROOF(t4) 25 m off on it: note 2 still.
        (document, roof, room) = House(25000);
        roof.TypeId = Felt(document, "EROOF(t4)").Id;
        Assert.Empty(RoofCoverings.Problems(document, roof));
        room.Dwelling = "No. 1";
        foreach (var (name, x) in new[] { ("No. 2", 12000.0), ("No. 3", 20000.0) })
            document.Add(new Room { LevelId = room.LevelId, Location = new Point2D(x, 3000), Name = "Living Room", Dwelling = name });
        Assert.Contains("(Table 12.1, note 2) does not allow an EROOF(t4) covering 20 m or more from", Assert.Single(RoofCoverings.Problems(document, roof)).Rule);
    }

    [Fact]
    public void ACoveringOfNoDesignationGivenIsAskedForOneNearTheBoundary()
    {
        // Felt with no designation given, 3 m off: wanting one; 25 m off, asked nothing; and no property line, nothing at all.
        var (document, roof, _) = House(3000);
        roof.TypeId = Felt(document, string.Empty).Id;
        Assert.Equal("its covering, the Roof Felt, Torch-On, has no external fire designation given, 3.0 m from the boundary.", Assert.Single(RoofCoverings.Problems(document, roof)).Said);

        // Given in the Materials window as BROOF(t4), it is kept when the project is saved and opened again, and nothing is wanting.
        document.Materials.First(material => material.Name == "Roof Felt, Torch-On").RoofCovering = "BROOF(t4)";
        Assert.Empty(RoofCoverings.Problems(document, roof));
        var opened = BIMDesigner.Infrastructure.Serialization.ProjectFile.FromJson(BIMDesigner.Infrastructure.Serialization.ProjectFile.ToJson(document));
        Assert.Equal("BROOF(t4)", opened.Materials.First(material => material.Name == "Roof Felt, Torch-On").RoofCovering);
        (document, roof, _) = House(25000);
        roof.TypeId = Felt(document, string.Empty).Id;
        Assert.Empty(RoofCoverings.Problems(document, roof));
        document.Remove(document.All<PropertyLine>().Single());
        Assert.Empty(RoofCoverings.Problems(document, roof));
    }
}
