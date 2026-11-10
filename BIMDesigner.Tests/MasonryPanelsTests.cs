using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Masonry panels under the wind by BS EN 1996-1-1 Annex E, the UK National Annex's K and γM by group, category and execution class, and masonry's smaller rules.</summary>
public class MasonryPanelsTests
{
    [Fact]
    public void AnnexEsCoefficientsAreTheGuides()
    {
        // At μ 1 and h/l 1 each case as BS 5628-1's Table 9 and the guide print it: A 0.071 down to I 0.021.
        var expected = new Dictionary<char, double> { ['A'] = 0.071, ['B'] = 0.053, ['C'] = 0.042, ['D'] = 0.035, ['E'] = 0.042, ['F'] = 0.034, ['G'] = 0.028, ['H'] = 0.024, ['I'] = 0.021 };
        foreach (var (letter, value) in expected) Assert.Equal(value, LateralPanels.Coefficient(letter, 1, 1), 6);

        // Straight lines between: E at μ 0.95 halfway between 0.042 and 0.044; J at μ 0.05 and h/l 2, 0.959.
        Assert.Equal(0.043, LateralPanels.Coefficient('E', 0.95, 1), 6);
        Assert.Equal(0.959, LateralPanels.Coefficient('J', 0.05, 2), 6);

        // J is A on its side: J(1, h/l) = A(1, l/h) (h/l)².
        Assert.InRange(LateralPanels.Coefficient('J', 1, 1.5) - LateralPanels.Coefficient('A', 1, 1 / 1.5) * 2.25, -0.002, 0.002);
        Assert.InRange(LateralPanels.Coefficient('J', 1, 2.0) - LateralPanels.Coefficient('A', 1, 0.5) * 4, -0.002, 0.002);
    }

    [Fact]
    public void APanelsEdgesChooseItsCase()
    {
        Assert.Equal('E', LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, 0.5, 1));
        Assert.Equal('F', LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Continuous, 0.5, 1));
        Assert.Equal('G', LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Continuous, PanelEdge.Continuous, 0.5, 1));
        Assert.Equal('A', LateralPanels.CaseFor(PanelEdge.Free, PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, 0.5, 1));
        Assert.Equal('J', LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Free, PanelEdge.Simple, 0.5, 1));
        Assert.Equal('K', LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Continuous, PanelEdge.Free, 0.5, 1));
        Assert.Null(LateralPanels.CaseFor(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Free, PanelEdge.Free, 0.5, 1));

        // Both vertical edges free, it spans up its height: M1 = W h² / 8, so α2 = h² / (8 μ l²); its top free too, a cantilever, h² / 2.
        var (spans, _) = LateralPanels.Alpha(PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Free, PanelEdge.Free, 0.5, 2500, 4000);
        Assert.Equal(2500.0 * 2500 / (8 * 0.5 * 4000 * 4000), spans, 9);
        var (stands, _) = LateralPanels.Alpha(PanelEdge.Free, PanelEdge.Simple, PanelEdge.Free, PanelEdge.Free, 0.5, 2500, 4000);
        Assert.Equal(2500.0 * 2500 / (2 * 0.5 * 4000 * 4000), stands, 9);
    }

    [Fact]
    public void APanelUnderTheWindIsCheckedByAnnexE()
    {
        // 140 of 7.3 block in M4, 5 m between its returns and 2.5 m high under a floor: fxk1 0.223 and fxk2 0.533 by thickness (Table NA.6), μ 0.419, case E at h/l 0.5. 1 kN/m² of wind:
        // MEd2 = α2 W l², MEd1 = μ α2 W l²; MRd2 = fxk2 / 2.7 Z, MRd1 = (fxk1 / 2.7 + σd) Z with σd 0.1 N/mm² under 0.2 fd.
        var block = MasonryDesign.UnitOf("Concrete Block");
        var (fxk1, fxk2) = MasonryDesign.FlexuralStrengths(block, 140);
        Assert.Equal(0.25 + (40.0 / 150) * (0.15 - 0.25), fxk1, 6);
        var mu = fxk1 / fxk2;
        var gammas = MasonryDesign.Gammas(UnitCategory.CategoryII, 2);
        var checks = MasonryDesign.Panel(block, 140, null, 0, 2500, 5000, PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, 1.0, 0.1, gammas).ToList();
        var alpha = LateralPanels.Coefficient('E', mu, 0.5);
        var z = 140.0 * 140 / 6 * 1000;
        var across = checks.Single(check => check.Name == "Wind, plane across the bed joints");
        var along = checks.Single(check => check.Name == "Wind, plane along the bed joints");
        Assert.Equal(alpha * 1.0 * 25, across.Demand, 9);
        Assert.Equal(fxk2 / 2.7 * z / 1e6, across.Capacity, 9);
        Assert.Equal(mu * alpha * 25, along.Demand, 9);
        Assert.Equal((fxk1 / 2.7 + 0.1) * z / 1e6, along.Capacity, 9);
        Assert.Contains("case E", along.Note);

        // A cavity wall's leaves together: a 100 brick leaf adds its own strengths over its own modulus.
        var brick = MasonryDesign.UnitOf("Brick, Common");
        var cavity = MasonryDesign.Panel(block, 140, brick, 100, 2500, 5000, PanelEdge.Simple, PanelEdge.Simple, PanelEdge.Simple, 1.0, 0.1, gammas).Single(check => check.Name == "Wind, plane across the bed joints");
        Assert.Equal(across.Capacity + MasonryDesign.FlexuralStrengths(brick, 100).Fxk2 / 2.7 * 100 * 100 / 6 * 1000 / 1e6, cavity.Capacity, 9);
    }

    [Fact]
    public void ASmallSectionIsWeakerByTheNationalAnnexsFactor()
    {
        // Under 0.1 m² of plan, fd by (0.7 + 3A): 0.05 m² by 0.85.
        var block = MasonryDesign.UnitOf("Concrete Block");
        var band = new WallStretch(true, -50, 0, 0, 0, 0);
        var whole = MasonryDesign.Checks(block, 140, null, 0, 2800, 140, band).Single(check => check.Name == "Load at its top or foot");
        var small = MasonryDesign.Checks(block, 140, null, 0, 2800, 140, band, area: 0.05).Single(check => check.Name == "Load at its top or foot");
        Assert.Equal(whole.Capacity * 0.85, small.Capacity, 9);

        // γM for Category I units in class 1: 2.3, fd up by 3.0 / 2.3.
        var better = MasonryDesign.Checks(block, 140, null, 0, 2800, 140, band, MasonryDesign.Gammas(UnitCategory.CategoryI, 1)).Single(check => check.Name == "Load at its top or foot");
        Assert.Equal(whole.Capacity * 3.0 / 2.3, better.Capacity, 9);
    }

    [Fact]
    public void GroupCategoryAndExecutionClassAreKept()
    {
        var document = BimDocument.CreateDefault();
        var block = document.Materials.First(material => material.Name == "Concrete Block");
        new SetMaterialCommand(block, MaterialLook.Of(block) with { Group = MasonryGroup.Group2, Category = UnitCategory.CategoryI }).Redo();
        document.ProjectInformation.MasonryExecutionClass = 1;
        var copy = MaterialCopies.Copy(block, "Block 2");
        Assert.Equal((MasonryGroup.Group2, UnitCategory.CategoryI), (copy.Group, copy.Category));
        var path = Path.Combine(Path.GetTempPath(), $"masonry-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var read = opened.Materials.First(material => material.Name == "Concrete Block");
            Assert.Equal((MasonryGroup.Group2, UnitCategory.CategoryI), (read.Group, read.Category));
            Assert.Equal(1, opened.ProjectInformation.MasonryExecutionClass);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
