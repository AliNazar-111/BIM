using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>
/// Walls as the rules see them: the heat an outside wall lets out, worked out from its layers;
/// the sound a wall round a bedroom keeps in; the fire a wall by a garage holds back.
/// </summary>
public class WallRulesTests
{
    [Fact]
    public void APartyWallResistsFireForAnHourAndRunsUpToTheRoof()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        var type = document.TypesOf<WallType>().Single(candidate => candidate.Name == "Party Wall - Cavity Blockwork 300mm");
        Assert.True(type.Separating);
        var wall = new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, 8000), TypeId = type.Id, LevelId = roof.LevelId, UnconnectedHeight = 3000 };
        document.Add(wall);
        var standard = document.ProjectInformation.ClearanceStandard;

        // Stopped at the ceiling, under the ridge: short of the roof by the height of the loft.
        var underside = roof.UndersideAt(document, new Point2D(5000, 4000));
        Assert.Equal(underside - 3000, WallRules.ShortOfRoof(document, wall)!.Value, 1);
        Assert.Contains(WallRules.Problems(document, wall, standard), problem => problem.Rule.Contains("(5.11)"));

        // Taken up to the roof, it is a compartment wall all the way.
        wall.UnconnectedHeight = underside - wall.GetBaseElevation(document);
        Assert.Null(WallRules.ShortOfRoof(document, wall));
        Assert.DoesNotContain(WallRules.Problems(document, wall, standard), problem => problem.Rule.Contains("(5.11)"));

        // Rated 30 minutes, it falls short of the hour asked.
        type.FireRating = "30 min";
        Assert.Contains(WallRules.Problems(document, wall, standard), problem => problem.Rule.Contains("60 minutes"));
    }

    private static WallType Type(BimDocument document, string name) =>
        document.TypesOf<WallType>().Single(type => type.Name == name);

    /// <summary>
    /// Two rooms side by side - the left 0 to 2000, the right 2000 to 4000, 3000 deep - inside
    /// walls round them, and the wall between of a type; the rooms named.
    /// </summary>
    private static (BimDocument Document, Wall Between) Pair(string between, string left = "Bedroom", string right = "Landing")
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First();
        var half = outside.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(4000 + half, -half), new Point2D(4000 + half, 3000 + half), new Point2D(-half, 3000 + half) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = outside.Id, LevelId = level, UnconnectedHeight = 2700 });

        var type = Type(document, between);
        var wall = new Wall { Start = new Point2D(2000, -half), End = new Point2D(2000, 3000 + half), TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 };
        document.Add(wall);
        document.Add(new Room { LevelId = level, Location = new Point2D(900, 1500), Name = left, Number = "1" });
        document.Add(new Room { LevelId = level, Location = new Point2D(3100, 1500), Name = right, Number = "2" });
        return (document, wall);
    }

    [Fact]
    public void AnOutsideWallsUValueIsWorkedOutFromItsLayers()
    {
        var document = BimDocument.CreateDefault();

        // Brick, a 25 cavity, 50 of PIR, 140 of aircrete, plaster: 0.25 W/m²K, inside Approved Document L's 0.26.
        var cavity = Type(document, "Exterior - Brick on Block 330mm");
        Assert.Equal(0.247, Thermal.UValue(document, cavity.Structure)!.Value, 3);
        Assert.Contains(cavity.GetTypeParameters(document), parameter => parameter.Name == "U-Value from its Layers");
        Assert.Equal(0.22, Thermal.UValue(document, Type(document, "Timber Frame - Brick Clad 315mm").Structure)!.Value, 2);

        // With a dense block in its inner leaf, as it was, it lets out 0.33: too much for a new dwelling.
        var dense = cavity.Duplicate("Exterior - Brick on Dense Block 330mm");
        var block = document.Materials.Single(material => material.Name == "Concrete Block");
        dense.Structure.Layers[3].MaterialId = block.Id;
        document.AddType(dense);
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = dense.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2700 };
        document.Add(wall);
        var problem = Assert.Single(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("its U-value is 0.33.", problem.Said);
        Assert.StartsWith("U 0.33 W/m²K - Its U-value is 0.33.", WallRules.Verdict(document, wall));
        Assert.Contains(wall.GetInstanceParameters(document), parameter => parameter.Name == "Regulations");
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.Irc));
    }

    [Fact]
    public void AWallRoundABedroomKeepsSoundInToRw40()
    {
        // The plain stud partition is Rw 38: short of 40 round a bedroom.
        var (document, wall) = Pair("Interior - Partition 125mm");
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM));
        document.Add(new Component { TypeId = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Bed).Id, LevelId = document.Levels[0].Id, Location = new Point2D(900, 1500) });
        Assert.Contains(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("Rw 40"));

        // The acoustic stud wall, with mineral wool in it, is Rw 43.
        wall.TypeId = Type(document, "Stud Wall - Acoustic 125mm").Id;
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void AWallBetweenTheHouseAndItsGarageHoldsBackFire()
    {
        var (document, wall) = Pair("Interior - Partition 125mm", left: "Garage", right: "Hall");

        // The partition is rated 30 minutes, and has plasterboard on it: both the UK and the US are content.
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM));
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.Irc));

        // Unrated, it is not.
        var plain = Type(document, "Interior - Partition 125mm").Duplicate("Partition - Unrated");
        plain.FireRating = string.Empty;
        document.AddType(plain);
        wall.TypeId = plain.Id;
        Assert.Contains(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("REI 30"));

        // Bare concrete has no gypsum board for the IRC.
        wall.TypeId = Type(document, "Generic - 200mm").Id;
        Assert.Contains(WallRules.Problems(document, wall, ClearanceStandard.Irc), problem => problem.Rule.Contains("R302.6"));
    }

    [Fact]
    public void ACurtainWallLetsOutNoMoreThan16AndIsSafetyGlassLowDown()
    {
        var document = BimDocument.CreateDefault();
        var storefront = document.TypesOf<CurtainWallType>().Single(type => type.Name == "Curtain Wall - Storefront 1500");
        Assert.Equal((1.4, true), (storefront.HeatTransferCoefficient, storefront.SafetyGlazing));

        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = storefront.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        Assert.Empty(WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM));
        Assert.StartsWith("U 1.4 W/m²K, safety glass", WallRules.Verdict(document, wall));

        storefront.HeatTransferCoefficient = 2.0;
        storefront.SafetyGlazing = false;
        var problems = WallRules.Problems(document, wall, ClearanceStandard.ApprovedDocumentM);
        Assert.Contains(problems, problem => problem.Rule.Contains("curtain walling"));
        Assert.Contains(problems, problem => problem.Rule.Contains("Diagram 5.1"));

        // Saved with the project.
        var loaded = Infrastructure.Serialization.ProjectFile.FromJson(Infrastructure.Serialization.ProjectFile.ToJson(document));
        var again = loaded.TypesOf<CurtainWallType>().Single(type => type.Name == "Curtain Wall - Storefront 1500");
        Assert.Equal((2.0, false), (again.HeatTransferCoefficient, again.SafetyGlazing));
    }
}
