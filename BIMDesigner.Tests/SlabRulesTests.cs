using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Floors and roofs as the rules see them: the heat a ground floor lets into the ground and a roof
/// lets out - with a cold loft's ceiling counted - the sound and fire a floor between storeys holds
/// back, and the pitch a slate roof is laid at.
/// </summary>
public class SlabRulesTests
{
    private static readonly Point2D[] House = { new(0, 0), new(6000, 0), new(6000, 5000), new(0, 5000) };

    private static T Type<T>(BimDocument document, string name) where T : SlabType =>
        document.TypesOf<T>().Single(type => type.Name == name);

    [Fact]
    public void AGroundFloorsUValueCountsTheGroundUnderIt()
    {
        var document = BimDocument.CreateDefault();
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = Type<FloorType>(document, "Floor - Screed on Slab 250mm").Id };
        floor.SetBoundary(House);
        document.Add(floor);

        // 50 of screed on 100 of PIR on the slab, 6 by 5 m on clay: 0.17, inside the 0.18 asked.
        Assert.Equal(0.170, SlabRules.UValue(document, floor)!.Value, 3);
        Assert.Empty(SlabRules.Problems(document, floor, ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("U 0.17 W/m²K", SlabRules.Verdict(document, floor));
        Assert.Contains(floor.GetInstanceParameters(document), parameter => parameter.Name == "Regulations");

        // With 40 of PIR, as it had before, it lets 0.32 into the ground.
        var type = Type<FloorType>(document, "Floor - Screed on Slab 250mm");
        type.Structure.Layers[1].Thickness = 40;
        Assert.Equal(0.32, SlabRules.UValue(document, floor)!.Value, 2);
        Assert.Contains("Table 4.1", Assert.Single(SlabRules.Problems(document, floor, ClearanceStandard.ApprovedDocumentM)).Rule);
    }

    [Fact]
    public void ARoofKeepsTheHeatInByItsOwnInsulationOrItsLoftsCeiling()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var roof = new Roof { TypeId = Type<RoofType>(document, "Roof - Slate on Timber Rafters 150mm").Id, LevelId = level, HeightOffset = 2700 };
        roof.SetBoundary(House);
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 40 }, new RoofEdge() });
        document.Add(roof);

        // Slate on bare rafters lets out 0.89: no insulation in it.
        Assert.Equal(0.89, SlabRules.UValue(document, roof)!.Value, 2);
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("0.16"));

        // A cold loft: 270 of mineral wool on the ceiling under it.
        var ceiling = new Ceiling { LevelId = level, TypeId = Type<CeilingType>(document, "Ceiling - Plasterboard under 270mm Mineral Wool").Id, HeightOffset = 2400 };
        ceiling.SetBoundary(House);
        document.Add(ceiling);
        Assert.InRange(SlabRules.UValue(document, roof)!.Value, 0.10, 0.13);
        Assert.Empty(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM));

        // A warm roof needs no ceiling: its PIR between and under its rafters gives 0.16.
        document.Remove(ceiling);
        roof.TypeId = Type<RoofType>(document, "Roof - Insulated Slate on Rafters 230mm").Id;
        Assert.InRange(SlabRules.UValue(document, roof)!.Value, 0.15, 0.16);
    }

    [Fact]
    public void ASlateRoofIsPitchedTwentyDegreesAtLeast()
    {
        var document = BimDocument.CreateDefault();
        var roof = new Roof { TypeId = Type<RoofType>(document, "Roof - Insulated Slate on Rafters 230mm").Id, LevelId = document.Levels[0].Id, HeightOffset = 2700 };
        roof.SetBoundary(House);
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 15 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 15 }, new RoofEdge() });
        document.Add(roof);

        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it is pitched 15°, too flat for natural slate.");
        roof.SlopeDegrees = 30;
        Assert.DoesNotContain(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("BS 5534"));
    }

    [Fact]
    public void AFloorBetweenStoreysKeepsSoundInAndResistsFire()
    {
        var document = BimDocument.CreateDefault();
        var floor = new Floor { LevelId = document.Levels[1].Id, TypeId = Type<FloorType>(document, "Floor - Timber 220mm").Id };
        floor.SetBoundary(House);
        document.Add(floor);
        Assert.Empty(SlabRules.Problems(document, floor, ClearanceStandard.ApprovedDocumentM));
        Assert.Equal("Between storeys", SlabRules.Verdict(document, floor));

        var thin = Type<FloorType>(document, "Floor - Timber 220mm");
        thin.AcousticRating = 34;
        thin.FireRating = string.Empty;
        var problems = SlabRules.Problems(document, floor, ClearanceStandard.ApprovedDocumentM);
        Assert.Contains(problems, problem => problem.Rule.Contains("Rw 40"));
        Assert.Contains(problems, problem => problem.Rule.Contains("Table B2"));
    }

    [Fact]
    public void PlainTilesWantThirtyFiveDegreesAndInterlockingTilesFifteen()
    {
        var document = BimDocument.CreateDefault();
        var plain = Type<RoofType>(document, "Roof - Plain Clay Tiles on Insulated Rafters 240mm");
        var interlocking = Type<RoofType>(document, "Roof - Concrete Interlocking Tiles on Insulated Rafters 240mm");
        Assert.Equal((35.0, "plain tiles"), SlabRules.CoveringPitch(document, plain));
        Assert.Equal((15.0, "interlocking tiles"), SlabRules.CoveringPitch(document, interlocking));

        var roof = new Roof { TypeId = plain.Id, LevelId = document.Levels[0].Id, HeightOffset = 2700 };
        roof.SetBoundary(House);
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 30 }, new RoofEdge() });
        document.Add(roof);
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM), problem => problem.Said == "it is pitched 30°, too flat for plain tiles.");

        roof.TypeId = interlocking.Id;
        Assert.Empty(SlabRules.Problems(document, roof, ClearanceStandard.ApprovedDocumentM));

        // The IRC lays tile at 2½ in 12 and slate at 4: 10° is 2.1 in 12, too flat for either.
        Assert.Equal((2.5, "clay or concrete tile", "R905.3.2"), SlabRules.IrcCoveringSlope(document, interlocking));
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 10 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 10 }, new RoofEdge() });
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.Irc), problem => problem.Said == "it is pitched 2.1 in 12, too flat for clay or concrete tile.");
        roof.SetEdges(new[] { new RoofEdge { DefinesSlope = true, SlopeDegrees = 15 }, new RoofEdge(), new RoofEdge { DefinesSlope = true, SlopeDegrees = 15 }, new RoofEdge() });
        Assert.Empty(SlabRules.Problems(document, roof, ClearanceStandard.Irc));
        roof.TypeId = Type<RoofType>(document, "Roof - Insulated Slate on Rafters 230mm").Id;
        Assert.Contains(SlabRules.Problems(document, roof, ClearanceStandard.Irc), problem => problem.Rule.Contains("R905.6.2"));
    }
}
