using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Footings as concrete (round 33): plain by BS EN 1992-1-1 12.9.3 against the Concrete Centre's table, reinforced against its worked pad, and by ACI 318
/// against StructurePoint's worked spread footing and Chapter 14 by hand; then whole footings from the analysis, bars across or along.
/// </summary>
public class FootingConcreteTests
{
    private const double Psi = 0.00689476;
    private const double Kip = 4.44822;
    private const double Inch = 25.4;

    [Theory]
    [InlineData(70, 16, 0.65)]
    [InlineData(140, 16, 0.92)]
    [InlineData(210, 16, 1.12)]
    [InlineData(280, 16, 1.29)]
    [InlineData(350, 16, 1.45)]
    [InlineData(70, 20, 0.60)]
    [InlineData(140, 20, 0.85)]
    [InlineData(210, 20, 1.04)]
    [InlineData(280, 20, 1.21)]
    [InlineData(350, 20, 1.35)]
    [InlineData(70, 25, 0.55)]
    [InlineData(140, 25, 0.78)]
    [InlineData(210, 25, 0.95)]
    [InlineData(280, 25, 1.10)]
    [InlineData(350, 25, 1.23)]
    [InlineData(70, 30, 0.52)]
    [InlineData(140, 30, 0.74)]
    [InlineData(210, 30, 0.90)]
    [InlineData(280, 30, 1.04)]
    [InlineData(350, 30, 1.17)]
    public void APlainFootingsDepthIsTheConcreteCentresTable(double pressure, double fck, double table) =>
        Assert.InRange(FoundationDesign.PlainDepthEurocode(pressure / 1000, 1000, fck) / 1000, table - 0.011, table + 0.011);

    [Fact]
    public void APlainStripComesToTheConcreteCentresExample()
    {
        // A 300 cavity wall on 800, a of 250, 140 kN/m² ultimate, C20/25: 0.85 × 250 = 213, say 225.
        var check = FoundationDesign.StripEurocode(0.14 * 800, 800, 250, 225, 0, 0, 20).Single();
        Assert.Equal("Depth as plain concrete", check.Name);
        Assert.InRange(check.Demand, 211, 214);
        Assert.True(check.Ratio <= 1);
    }

    [Fact]
    public void ThePlainRuleNeverAsksMoreThanTwiceTheProjection() =>
        Assert.Equal(200, FoundationDesign.PlainDepthEurocode(5, 100, 16), 6);

    /// <summary>The Concrete Centre's worked pad: 2.4 m square, 500 deep, 350 column, C30/37, 1,567.5 kN at the ultimate, H16 @ 250 at d 434.</summary>
    private static List<StrengthCheck> WorkedPad() => FoundationDesign.PadEurocode(1567.5, 0, 350, 350, 2400, 2400, 500, 434, 804, 30).ToList();

    [Fact]
    public void APadsBendingComesToTheWorkedExample()
    {
        // MEd 343 kNm over 2.4 m, 143 kNm/m; As 1,914 mm²: 797 mm²/m against H16 @ 250's 804. K 0.025, z 0.95 d.
        var bending = WorkedPad().Single(check => check.Name == "Bottom steel, width way");
        Assert.InRange(bending.Demand, 790, 805);
        Assert.Equal(804, bending.Capacity);
        Assert.Contains("143.0 kNm/m", bending.Note);
    }

    [Fact]
    public void APadsShearAtDComesToTheWorkedExample()
    {
        // 272 × (1.025 - 0.434) = 161 kN/m; ρ 0.19%, vRd,c 0.42: 0.42 × 434 = 182.
        var shear = WorkedPad().Single(check => check.Name == "Shear at d, length way");
        Assert.InRange(shear.Demand, 160, 162);
        Assert.InRange(shear.Capacity, 178, 184);
    }

    [Fact]
    public void APadsPunchingComesToTheWorkedExample()
    {
        // At 2d: u 6,854, 560 kN left of the ground's push, vEd 0.188.
        var (stress, perimeter, left) = FoundationDesign.PadPunchingAt(1567.5, 0, 350, 350, 5.76, 434, 868, 0.6);
        Assert.InRange(perimeter, 6850, 6860);
        Assert.InRange(left, 555, 565);
        Assert.InRange(stress, 0.185, 0.191);

        // 6.4.4(2) asks every perimeter within 2d, and closer in the ground takes off less: the worst is well inside 2d, against 2d/a on what the concrete carries.
        var worst = WorkedPad().Single(check => check.Name == "Punching within 2d");
        Assert.True(worst.Ratio > 0.188 / 0.42, $"{worst.Ratio:0.00}");
        Assert.True(worst.Ratio <= 1);
    }

    [Fact]
    public void AMomentAtAPadsFootAddsToItsPunching()
    {
        var (axial, _, _) = FoundationDesign.PadPunchingAt(1000, 0, 350, 350, 5.76, 434, 434, 0.6);
        var (bent, _, _) = FoundationDesign.PadPunchingAt(1000, 100, 350, 350, 5.76, 434, 434, 0.6);
        Assert.True(bent > axial);
    }

    /// <summary>StructurePoint's worked spread footing: 18 in column, 11 ft 2 in square, 32 in thick, d 28 in, 3,000 psi, 912 kips factored, 7.76 in² across its width.</summary>
    private static List<StrengthCheck> SpreadFooting()
    {
        var width = 11.17 * 12 * Inch;
        return FoundationDesign.PadAci(912 * Kip, 0, 18 * Inch, 18 * Inch, width, width, 32 * Inch, 28 * Inch, 7.76 * 645.16 / (width / 1000), 3000 * Psi).ToList();
    }

    [Fact]
    public void ASpreadFootingsBendingComesToStructurePoints()
    {
        // Mu 954 kip-ft over 11.17 ft, As 7.76 in², or the least 0.0018 b h, 7.72 in², a hair under.
        var width = 11.17 * 12 * Inch / 1000;
        var check = SpreadFooting().Single(each => each.Name == "Bottom steel, width way");
        Assert.InRange(check.Demand, 0.98 * 7.76 * 645.16 / width, 1.02 * 7.76 * 645.16 / width);
        Assert.Contains($"{954 * 1.35582 / width:0}", check.Note);
    }

    [Fact]
    public void ASpreadFootingsTwoWayShearComesToStructurePoints()
    {
        // vu 156 psi on bo 4 × 46 in, Vu 805 kips; φ vc 0.75 × 4 √3,000 = 164 psi.
        var check = SpreadFooting().Single(each => each.Name == "Two-way shear");
        Assert.InRange(check.Demand / Psi, 154, 158);
        Assert.InRange(check.Capacity / Psi, 163, 165.5);
    }

    [Fact]
    public void ASpreadFootingsOneWayShearTakesThe2019Rule()
    {
        // Vu 204 kips over its width as StructurePoint has it. 318-14's 2√f'c gave 308 kips; 318-19's 8 ρ^⅓ √f'c, λs 1 for a footing, gives less.
        var width = 11.17 * 12 * Inch / 1000;
        var check = SpreadFooting().Single(each => each.Name == "Shear at d, width way");
        Assert.InRange(check.Demand * width / Kip, 200, 208);
        var rho = 7.76 * 645.16 / (width * 1000 * 28 * Inch);
        Assert.Equal(0.75 * 8 * Math.Cbrt(rho) * Math.Sqrt(3000) * Psi * 28 * Inch, check.Capacity, 3);
        Assert.True(check.Capacity * width / Kip < 308);
    }

    [Fact]
    public void APlainFootingByAciIsChapter14()
    {
        // 2,500 psi, 300 kN/m ... h 200 less 50 is 150 in strength, φ 0.6: 5√f'c Sm is 0.6 × 5 × 50 psi × 1000 × 150² / 6.
        var checks = FoundationDesign.StripAci(120, 800, 250, 200, 0, 0, 2500 * Psi).ToList();
        var bending = checks.Single(each => each.Name == "Bending");
        Assert.Equal(0.6 * 5 * 50 * Psi * 1000 * 150 * 150 / 6 / 1e6, bending.Capacity, 6);
        Assert.Equal(150 * 0.25 * 0.25 / 2, bending.Demand, 6);

        // 4/3 √f'c at h from its critical section: 150 × (0.25 - 0.15).
        var shear = checks.Single(each => each.Name == "Shear at h");
        Assert.Equal(150 * 0.1, shear.Demand, 6);
        Assert.Equal(0.6 * 4.0 / 3 * 50 * Psi * 150, shear.Capacity, 6);

        // At least 200 mm (14.3.2.1).
        Assert.True(FoundationDesign.StripAci(120, 800, 250, 190, 0, 0, 2500 * Psi).Single(each => each.Name == "Thickness").Ratio > 1);
    }

    private static BimDocument Walled()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        return document;
    }

    private static IReadOnlyList<FoundationCheck> Checked(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        return FoundationDesign.Of(document, StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model)));
    }

    [Fact]
    public void AMassStripIsCheckedAsPlainConcreteUnderItsUltimateLoad()
    {
        var document = Walled();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var check = Checked(document).First(each => each.Foundation is WallFooting);
        Assert.Contains(check.Checks, each => each.Name == "Bearing" && each.Under == "All but wind");
        var plain = check.Checks.Single(each => each.Name == "Depth as plain concrete");
        Assert.StartsWith("ULS", plain.Under, StringComparison.Ordinal);
        Assert.Contains("C16/20", plain.Note);
    }

    [Fact]
    public void BarsAcrossAStripCountAndBarsAlongItDoNot()
    {
        var document = Walled();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        foreach (var strip in document.Elements.OfType<WallFooting>())
        {
            strip.Reinforced = true;
            strip.Bars = "3 H12";
        }

        Assert.Contains(Checked(document).First(each => each.Foundation is WallFooting).Checks, each => each.Name == "Depth as plain concrete");

        foreach (var strip in document.Elements.OfType<WallFooting>()) strip.Bars = "H12 @ 200";
        var across = Checked(document).First(each => each.Foundation is WallFooting);
        var steel = across.Checks.Single(each => each.Name == "Bottom steel");
        Assert.Equal(565.5, steel.Capacity, 1);
        Assert.DoesNotContain(across.Checks, each => each.Name == "Depth as plain concrete");
    }

    [Fact]
    public void ASectionOffTheFootingIsNotChecked()
    {
        // Standing out 100 past the wall on a d of 150: d is off the strip's edge. Standing out 500, it is on it.
        Assert.DoesNotContain(FoundationDesign.StripEurocode(100, 1000, 100, 225, 150, 565, 28), check => check.Name == "Shear at d");
        Assert.Contains(FoundationDesign.StripEurocode(100, 1000, 500, 225, 150, 565, 28), check => check.Name == "Shear at d");

        // A pad too small to have d/2 on it each way has no two-way section by ACI, not a negative shear.
        Assert.DoesNotContain(FoundationDesign.PadAci(300, 0, 450, 450, 600, 600, 300, 200, 565, 3000 * Psi), check => check.Name == "Two-way shear");
        Assert.All(FoundationDesign.PadAci(300, 0, 450, 450, 600, 600, 300, 200, 565, 3000 * Psi), check => Assert.True(check.Demand >= 0));
    }

    [Fact]
    public void AColumnTooHeavyForEveryPadGetsTheLargestNotTheLastListed()
    {
        var document = Walled();
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);

        // A concrete floor over 80 m² on it alone is more than any pad will bear on firm clay; the US types run after a UK project's own, and the smallest of all is the last.
        var concrete = document.Materials.First(material => material.Name.StartsWith("Concrete", StringComparison.Ordinal) && !material.Name.Contains("Block", StringComparison.Ordinal));
        var slab = new FloorType("Slab 400", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 400)));
        document.AddType(slab);
        var floor = new Floor { TypeId = slab.Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) });
        document.Add(floor);

        var pad = Footings.Fit(document, column);
        var largest = document.TypesOf<FootingType>().Where(type => type.Kind == FootingKind.Pad).Max(type => type.Width * type.Length);
        var fitted = document.FindType<FootingType>(pad.TypeId)!;
        Assert.Equal(largest, fitted.Width * fitted.Length);
    }

    [Fact]
    public void APadIsCheckedAtItsPierAndWithoutPresumedBearingItsConcreteIsStillChecked()
    {
        var document = Walled();
        var post = document.TypesOf<ColumnType>().First(type => type.Structural && type.Shape == ColumnShape.IBeam);
        var column = new Column { TypeId = post.Id, LevelId = document.Levels[0].Id, TopLevelId = document.Levels[1].Id, Location = new Point2D(5000, 4000), Structural = true };
        document.Add(column);
        var pad = Footings.Fit(document, column);
        pad.Reinforced = true;
        pad.Bars = "H12 @ 200";
        document.Add(pad);

        // Rock's presumed value wants a site investigation, and that does not stop the concrete being worked out.
        document.ProjectInformation.Ground = GroundType.Rock;
        var check = Checked(document).Single(each => each.Foundation is PadFooting);
        Assert.DoesNotContain(check.Checks, each => each.Name == "Bearing");
        Assert.NotEqual(string.Empty, check.Limits);
        Assert.Contains(check.Checks, each => each.Name == "Bottom steel, width way");
        Assert.Contains(check.Checks, each => each.Name == "Punching at the pier's face");
    }

    [Fact]
    public void AUsStripIsWorkedOutByAci()
    {
        var document = Walled();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.Ground = GroundType.FirmClay;
        var check = Checked(document).First(each => each.Foundation is WallFooting);
        Assert.Contains(check.Checks, each => each.Name == "Bending");
        Assert.Contains(check.Checks, each => each.Clause == "14.5.2.1");
    }
}
