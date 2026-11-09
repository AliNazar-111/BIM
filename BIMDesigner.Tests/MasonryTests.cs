using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Masonry walls (round 29): timber floors and roofs as diaphragms holding the wall tops, and BS EN 1996-1-1 with
/// its UK National Annex against worked figures: fk, Φ, effective height and thickness, Table NA.6's flexural
/// strengths, and whole walls checked from what the analysis finds in them.
/// </summary>
public class MasonryTests
{
    private static MasonryUnit Aircrete => MasonryDesign.UnitOf("Aircrete Block");

    private static MasonryUnit Block => MasonryDesign.UnitOf("Concrete Block");

    private static MasonryUnit Brick => MasonryDesign.UnitOf("Brick, Common");

    [Fact]
    public void TheCharacteristicStrengthIsTheNationalAnnexs()
    {
        // K 0.55 for concrete and aircrete units of Group 1, 0.50 for clay (Table NA.4), fk = K fb^0.7 fm^0.3 in M4.
        Assert.Equal((0.55, false), (Aircrete.K, Aircrete.Clay));
        Assert.Equal((0.50, true), (Brick.K, Brick.Clay));
        Assert.Equal(2.04, MasonryDesign.Characteristic(Aircrete), 2);
        Assert.Equal(3.35, MasonryDesign.Characteristic(Block), 2);
        Assert.Equal(6.17, MasonryDesign.Characteristic(Brick), 2);
    }

    [Fact]
    public void FlexuralAndShearStrengthsAreTablesNa5AndNa6()
    {
        Assert.Equal((0.4, 1.1), MasonryDesign.FlexuralStrengths(Brick, 102));
        Assert.Equal((0.25, 0.45), MasonryDesign.FlexuralStrengths(Aircrete, 100));
        Assert.Equal((0.25, 0.6), MasonryDesign.FlexuralStrengths(Block, 100));
        var thick = MasonryDesign.FlexuralStrengths(Aircrete, 250);
        Assert.Equal((0.15, 0.25), (thick.Fxk1, Math.Round(thick.Fxk2, 6)));
        Assert.Equal(0.35, MasonryDesign.FlexuralStrengths(Block, 300).Fxk2, 6);

        // Between 100 and 250 mm by a straight line (Note 3).
        Assert.Equal(0.25 - 0.10 * 40 / 150, MasonryDesign.FlexuralStrengths(Aircrete, 140).Fxk1, 6);
        Assert.Equal(0.20, MasonryDesign.InitialShear(Brick));
        Assert.Equal(0.15, MasonryDesign.InitialShear(Block));
    }

    [Fact]
    public void EffectiveHeightAndThicknessAreClause5Point5()
    {
        // ρ4 = ρ2 / (1 + (ρ2 h / l)²) to h = 1.15 l, then 0.5 l / h.
        Assert.Equal(0.5, MasonryDesign.Rho(1.0, 3000, 3000, 215, 2), 6);
        Assert.Equal(0.5 * 2000 / 3000, MasonryDesign.Rho(1.0, 3000, 2000, 215, 2), 6);

        // ρ3 = ρ2 / (1 + (ρ2 h / 3 l)²) to h = 3.5 l, then 1.5 l / h, 0.3 at the least.
        Assert.Equal(1 / (1 + Math.Pow(3000.0 / 4500, 2)), MasonryDesign.Rho(1.0, 3000, 1500, 215, 1), 6);
        Assert.Equal(0.375, MasonryDesign.Rho(1.0, 3000, 750, 215, 1), 6);

        // Long for its thickness, or nothing at its edges, a wall stays at ρ2 (5.5.1.2(7)): 30 t, or 15 t with one edge.
        Assert.Equal(1.0, MasonryDesign.Rho(1.0, 3000, 10000, 140, 2));
        Assert.Equal(0.75, MasonryDesign.Rho(0.75, 3000, 3000, 140, 1));
        Assert.Equal(0.75, MasonryDesign.Rho(0.75, 3000, 3000, 140, 0));

        // A cavity wall: the cube root of the sum of the cubes, its unloaded leaf no thicker than the loaded.
        Assert.Equal(155.3, MasonryDesign.EffectiveThickness(100, 140), 1);
        Assert.Equal(126.0, MasonryDesign.EffectiveThickness(140, 100), 1);
        Assert.Equal(140, MasonryDesign.EffectiveThickness(0, 140));
    }

    [Fact]
    public void TheReductionFactorsAreEquation6Point4AndAnnexG()
    {
        Assert.Equal(0.8, MasonryDesign.PhiEnd(10, 100), 6);
        Assert.Equal(0, MasonryDesign.PhiEnd(60, 100));

        // hef/tef 20 at emk 0.05 t: A1 0.90, u = 18 / 21.15 = 0.851, Φm 0.627.
        Assert.Equal(0.627, MasonryDesign.PhiMiddle(5, 100, 20), 3);

        // Stockier, or with less eccentricity, it carries more; past half the thickness, nothing.
        Assert.True(MasonryDesign.PhiMiddle(5, 100, 10) > MasonryDesign.PhiMiddle(5, 100, 20));
        Assert.True(MasonryDesign.PhiMiddle(5, 100, 20) > MasonryDesign.PhiMiddle(20, 100, 20));
        Assert.Equal(0, MasonryDesign.PhiMiddle(55, 100, 10));
    }

    [Fact]
    public void AStretchOfWallIsCheckedAsTheClausesHaveIt()
    {
        // 100 kN/m on 140 mm of 7.3 block, ends and middle.
        var (t, fd) = (140.0, MasonryDesign.Characteristic(Block) / 3.0);
        var end = new WallStretch(true, -100, 5, 0, 0, 0);
        var checks = MasonryDesign.Checks(Block, t, null, 0, 2800, 140, end).ToList();
        var foot = checks.Single(check => check.Name == "Load at its top or foot");
        Assert.Equal(100, foot.Demand, 6);
        Assert.Equal((1 - 2 * Math.Max(2800.0 / 450, 0.05 * t) / t) * t * fd, foot.Capacity, 3);

        // At mid height, Annex G; a moment shifts the load, wind left out of it.
        var middle = new WallStretch(false, -100, 0, 500, 0, 700);
        var mid = MasonryDesign.Checks(Block, t, null, 0, 2800, 140, middle).Single(check => check.Name == "Load at mid height");
        var eccentric = 700.0 / 100 + 2800.0 / 450;
        Assert.Equal(MasonryDesign.PhiMiddle(eccentric, t, 20) * t * fd, mid.Capacity, 3);

        // The load on it helps it bend up its height, to 0.2 fd (6.16).
        var (fxk1, _) = MasonryDesign.FlexuralStrengths(Block, t);
        var bending = checks.Single(check => check.Name == "Bending up its height");
        Assert.Equal((fxk1 / 2.7 + Math.Min(100 / t, 0.2 * fd)) * t * t / 6 / 1000, bending.Capacity, 6);

        // Shear is the wall's: all of it against fvd t lc, fvk = 0.15 + 0.4 σd, no more than 0.065 fb, over 2.5, on the length in compression.
        var shear = MasonryDesign.WholeShear(Block, t, new[] { new WallStretch(true, -100, 5, 0, 0, 0, 0, 1000), new WallStretch(true, -100, 5, 0, 0, 0, 0, 1000), new WallStretch(true, 50, 5, 0, 0, 0, 0, 1000) });
        Assert.Equal(15, shear.Demand, 6);
        Assert.Equal(Math.Min(0.15 + 0.4 * 100 / t, 0.065 * 7.3) / 2.5 * t * 2000 / 1000, shear.Capacity, 6);

        // A second leaf adds its own bending strength, though no load.
        var outer = MasonryDesign.Checks(Block, t, Brick, 100, 2800, 155, end).Single(check => check.Name == "Bending up its height");
        Assert.Equal(bending.Capacity + 0.4 / 2.7 * 100 * 100 / 6 / 1000, outer.Capacity, 6);
    }

    [Fact]
    public void PastHalfADeepEccentricityTheLoadIsTakenOnAStressBlock()
    {
        // A rigid joint would put the load off the wall altogether: Annex C(4) and (5) take it on 0.1 t at the face instead, so what it can carry is 0.1 t fd and not nothing.
        var (t, fd) = (140.0, MasonryDesign.Characteristic(Block) / 3.0);
        var foot = MasonryDesign.Checks(Block, t, null, 0, 2800, 140, new WallStretch(true, -10, 0, 0, 0, 5000)).Single(check => check.Name == "Load at its top or foot");
        Assert.Equal(0.1 * t * fd, foot.Capacity, 3);
        var middle = MasonryDesign.Checks(Block, t, null, 0, 2800, 140, new WallStretch(false, -10, 0, 0, 0, 5000)).Single(check => check.Name == "Load at mid height");
        Assert.True(middle.Capacity > 0);
    }

    /// <summary>The 10 by 8 m cavity-walled house with its roof, on strip footings.</summary>
    private static BimDocument House()
    {
        var (document, _) = EaveTests.House(hip: false);
        foreach (var wall in document.Walls) wall.StructuralUsage = StructuralUsage.Bearing;
        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        return document;
    }

    private static (StructuralAnalysis Analysis, SecondOrderAnalysis Second, IReadOnlyList<WallCheck> Checks) Checked(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var analysis = StructuralAnalysis.Run(document, model, StructuralLoading.Of(document, model));
        Assert.Empty(analysis.Problems);
        var second = SecondOrderAnalysis.Run(document, analysis);
        return (analysis, second, MasonryDesign.Of(document, second));
    }

    [Fact]
    public void ARoofHoldsTheWallTopsAndCarriesTheWindToTheWallsThatRunItsWay()
    {
        var document = House();
        var model = AnalyticalModel.Of(document);
        var diaphragm = Assert.Single(model.Diaphragms);
        Assert.Equal(AnalyticalPanelKind.Diaphragm, diaphragm.Kind);
        Assert.Equal(model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).Max(panel => panel.Outline.Max(node => node.At.Z)), diaphragm.Origin.Z, 3);

        // It is the analysis's: not a plate of the structure, nor handed on.
        Assert.DoesNotContain(model.Panels, panel => panel.Kind == AnalyticalPanelKind.Diaphragm);
        Assert.DoesNotContain(SafExport.Build(document).Single(sheet => sheet.Name == "StructuralSurfaceMember").Rows, row => row[0]?.ToString()?.StartsWith('D') == true);

        var (analysis, second, _) = Checked(document);
        Assert.Contains(analysis.Notes, note => note.Contains("diaphragm", StringComparison.Ordinal));

        // Held at the top, the walls no longer lean out of their plane: first order is enough.
        Assert.True(second.Worst!.Alpha > 20, $"αcr {second.Worst.Alpha:0.0}");
        Assert.DoesNotContain(second.Results, result => result.SecondOrder);

        // Its loads and reactions still balance: it adds nothing to what is carried.
        foreach (var loadCase in analysis.Loading.Cases)
        {
            var (load, reaction) = (analysis.TotalLoad(loadCase), analysis.TotalReaction(loadCase));
            Assert.True((load + reaction).Length < 0.01 * Math.Max(1, load.Length), $"{loadCase.Name}: {load} against {reaction}");
        }
    }

    [Fact]
    public void ADiaphragmSitsWhereTheWallsEndsReachIt()
    {
        // Walls attached to a hip: the slope cuts their tops lower at each end, and its corners have to be on them.
        var (document, roof) = EaveTests.House(hip: true);
        foreach (var wall in document.Walls)
        {
            wall.StructuralUsage = StructuralUsage.Bearing;
            wall.TopAttachedTo = roof.Id;
        }

        foreach (var wall in document.Walls.ToList()) document.Add(Footings.Fit(document, wall));
        var model = AnalyticalModel.Of(document);
        var diaphragm = Assert.Single(model.Diaphragms);
        var ends = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).SelectMany(panel => panel.Outline.Where(node => Math.Abs(node.At.Z - diaphragm.Origin.Z) < 1)).ToList();
        Assert.All(diaphragm.Outline.Take(4), corner => Assert.Contains(corner, ends));

        // Joined at every corner, so nothing is left free and nothing leans.
        var (_, second, _) = Checked(document);
        Assert.DoesNotContain(second.Results, result => result.SecondOrder);
    }

    [Fact]
    public void AHousesWallsAreCheckedFromWhatTheAnalysisFinds()
    {
        var (_, _, checks) = Checked(House());
        Assert.Equal(4, checks.Count);
        foreach (var check in checks)
        {
            Assert.Contains("BS EN 1996-1-1", check.By);
            Assert.Contains("Aircrete block", check.Grade);

            // A 140 aircrete leaf and a 100 brick one, 3 m high, held top and foot.
            Assert.Equal(155.3, check.EffectiveThickness, 1);
            Assert.InRange(check.EffectiveHeight / 3000, 0.5, 1);
            foreach (var name in new[] { "Slenderness", "Load at its top or foot", "Load at mid height", "Bending up its height", "Bending along its length", "Shear in its plane" })
                Assert.Contains(check.Checks, each => each.Name == name);
            Assert.All(check.Checks, each => Assert.False(double.IsNaN(each.Ratio), each.Name));

            // The load down it is well within what it carries; shear too.
            Assert.True(check.Checks.Single(each => each.Name == "Load at mid height").Ratio < 0.6);
            Assert.True(check.Checks.Single(each => each.Name == "Shear in its plane").Ratio < 1);
        }

        // Long walls with no cross wall to span to bend up their height under the wind, about wh²/8: near what they can carry, and past it where a side wall is drawn off by zones A to C,
        // about 0.8 qp along it, where the wall into the wind is pressed at 0.85 x 0.7.
        var bending = checks.Max(check => check.Checks.Single(each => each.Name == "Bending up its height").Ratio);
        Assert.InRange(bending, 0.6, 2.0);
    }

    [Fact]
    public void AWallWithNothingHoldingItsTopStandsAsACantilever()
    {
        var document = House();
        foreach (var roof in document.All<Roof>().ToList()) document.Elements.Remove(roof);
        var model = AnalyticalModel.Of(document);
        Assert.Empty(model.Diaphragms);
        var wall = model.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall);
        Assert.False(MasonryDesign.Holding(model, wall).Held);

        var (_, _, checks) = Checked(document);
        var slender = checks[0].Checks.Single(each => each.Name == "Slenderness");
        Assert.Equal(2 * 3000 / MasonryDesign.EffectiveThickness(100, 140), slender.Demand, 3);
        Assert.True(slender.Ratio > 1, $"{slender.Ratio:P0}");
        Assert.Contains("cantilever", slender.Note);
        Assert.All(checks, check => Assert.False(check.Enough));
    }

    [Fact]
    public void AConcreteFloorHoldsAWallBetterThanATimberOne()
    {
        // Under a concrete floor the wall is ρ2 0.75; under a roof it is 1.0.
        var document = House();
        var roofed = AnalyticalModel.Of(document);
        var first = document.Levels[1];
        var concrete = document.TypesOf<FloorType>().First(type => type.Name.StartsWith("Floor - Screed", StringComparison.Ordinal));
        var floor = new Floor { TypeId = concrete.Id, LevelId = first.Id };
        floor.SetBoundary(new[] { new Point2D(-165, -165), new Point2D(10165, -165), new Point2D(10165, 8165), new Point2D(-165, 8165) });
        document.Add(floor);
        var model = AnalyticalModel.Of(document);
        var wall = model.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall);
        Assert.False(MasonryDesign.Holding(roofed, roofed.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall)).Concrete);
        Assert.True(MasonryDesign.Holding(model, wall).Concrete);
    }

    [Fact]
    public void AMaterialsOwnStrengthAndMortarStandInForItsName()
    {
        var document = BimDocument.CreateDefault();
        var block = document.Materials.First(material => material.Name == "Concrete Block");
        Assert.Equal((7.3, MortarClass.M4), (MasonryDesign.UnitOf(block).Fb, MasonryDesign.UnitOf(block).Mortar));

        // 10.4 in M6: fk = 0.55 x 10.4^0.7 x 6^0.3.
        var command = new SetMaterialCommand(block, MaterialLook.Of(block) with { UnitStrength = 10.4, Mortar = MortarClass.M6 });
        command.Redo();
        Assert.Equal(0.55 * Math.Pow(10.4, 0.7) * Math.Pow(6, 0.3), MasonryDesign.Characteristic(MasonryDesign.UnitOf(block)), 6);

        // One step to undo; a copy keeps it; the project keeps it.
        var copy = MaterialCopies.Copy(block, "Block 2");
        Assert.Equal((10.4, MortarClass.M6), (copy.UnitStrength, copy.Mortar));
        var brick = document.Materials.First(material => material.Name == "Brick, Common");
        brick.WaterAbsorption = 5;
        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var back = opened.Materials.First(material => material.Name == "Concrete Block");
            Assert.Equal((10.4, MortarClass.M6), (back.UnitStrength, back.Mortar));
            Assert.Equal(5, opened.Materials.First(material => material.Name == "Brick, Common").WaterAbsorption);
        }
        finally
        {
            File.Delete(path);
        }

        command.Undo();
        Assert.Equal((0, MortarClass.M4), (block.UnitStrength, block.Mortar));

        // Mortar's strength is held to twice the unit's (NA.2.4).
        Assert.Equal(2, MasonryDesign.MortarStrength(MasonryDesign.UnitOf("Concrete Block", 1, MortarClass.M12)));
    }

    [Theory]
    [InlineData(5.0, MortarClass.M12, 0.7, 2.0)]
    [InlineData(5.0, MortarClass.M4, 0.5, 1.5)]
    [InlineData(5.0, MortarClass.M2, 0.4, 1.2)]
    [InlineData(10.0, MortarClass.M6, 0.4, 1.1)]
    [InlineData(10.0, MortarClass.M2, 0.35, 1.0)]
    [InlineData(15.0, MortarClass.M12, 0.4, 1.1)]
    [InlineData(15.0, MortarClass.M4, 0.3, 0.9)]
    [InlineData(15.0, MortarClass.M2, 0.25, 0.8)]
    public void ClayBendsAsTableNa6Has(double absorption, MortarClass mortar, double fxk1, double fxk2) =>
        Assert.Equal((fxk1, fxk2), MasonryDesign.FlexuralStrengths(MasonryDesign.UnitOf("Brick", 20, mortar, absorption), 102));

    [Theory]
    [InlineData(2.9, MortarClass.M4, 100, 0.25, 0.4)]
    [InlineData(3.6, MortarClass.M2, 100, 0.2, 0.4)]
    [InlineData(7.3, MortarClass.M2, 100, 0.2, 0.5)]
    [InlineData(3.6, MortarClass.M2, 250, 0.1, 0.2)]
    [InlineData(7.3, MortarClass.M12, 250, 0.15, 0.35)]
    [InlineData(10.4, MortarClass.M4, 215, 0.25, 0.75)]
    [InlineData(17.5, MortarClass.M4, 100, 0.25, 0.9)]
    [InlineData(17.5, MortarClass.M2, 100, 0.2, 0.7)]
    public void BlocksBendAsTableNa6Has(double strength, MortarClass mortar, double thickness, double fxk1, double fxk2)
    {
        var (first, second) = MasonryDesign.FlexuralStrengths(MasonryDesign.UnitOf("Concrete Block", strength, mortar), thickness);
        Assert.Equal(fxk1, first, 6);
        Assert.Equal(fxk2, second, 6);
    }

    [Fact]
    public void ShearAndInBetweenStrengthsAreAsTheAnnexHasThem()
    {
        // Between 3.6 and 7.3 by a straight line (Note 3); past 7.3 the row below, there being none between.
        Assert.Equal(0.525, MasonryDesign.FlexuralStrengths(MasonryDesign.UnitOf("Block", 5.45), 100).Fxk2, 6);
        Assert.Equal(0.6, MasonryDesign.FlexuralStrengths(MasonryDesign.UnitOf("Block", 9), 100).Fxk2, 6);

        Assert.Equal(0.30, MasonryDesign.InitialShear(MasonryDesign.UnitOf("Brick", 0, MortarClass.M12)));
        Assert.Equal(0.20, MasonryDesign.InitialShear(MasonryDesign.UnitOf("Block", 0, MortarClass.M12)));
        Assert.Equal(0.10, MasonryDesign.InitialShear(MasonryDesign.UnitOf("Brick", 0, MortarClass.M2)));
        Assert.Equal(0.10, MasonryDesign.InitialShear(MasonryDesign.UnitOf("Block", 0, MortarClass.M2)));
    }

    /// <summary>The house with a partition across its middle, of the thickness and material given.</summary>
    private static (BimDocument Document, Wall Cross) Crossed(string material, double thickness, double length = 8000)
    {
        var document = House();
        var type = new WallType($"Cross {material} {thickness}", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, document.Materials.First(each => each.Name == material).Id, thickness))) { Function = WallFunction.Interior };
        document.AddType(type);
        var cross = new Wall { Start = new Point2D(5000, 0), End = new Point2D(5000, length), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(cross);
        return (document, cross);
    }

    [Fact]
    public void ABlockCrossWallStiffensTheWallsItMeets()
    {
        var (document, cross) = Crossed("Concrete Block", 100);
        var model = AnalyticalModel.Of(document);

        // It meets both long walls, which are split there, each piece stiffened at that edge; it is no plate, and goes nowhere else.
        Assert.Equal(2, model.Stiffeners.Count);
        Assert.All(model.Stiffeners, stiffener => Assert.Equal(cross, stiffener.Cross));
        Assert.Equal(6, model.Panels.Count(panel => panel.Kind == AnalyticalPanelKind.Wall));
        Assert.DoesNotContain(model.Panels, panel => panel.Element == cross);
        var south = model.Panels.First(panel => panel.Element == model.Stiffeners[0].Host);
        Assert.Equal(2, MasonryDesign.Holding(model, south).Edges);

        // As a cantilever: h³/3EI + h/GAv, shared between the two walls it joins.
        var (e, t, l, h) = (1000 * MasonryDesign.Characteristic(Block), 100.0, 8000.0, 3000.0);
        var flexibility = h * h * h / (3 * e * t * l * l * l / 12) + h / (0.4 * e * 5 / 6 * t * l);
        Assert.Equal(1, model.Stiffeners[0].Stiffness * flexibility * 2, 3);

        // What it pushes back with counts: everything still balances.
        var (analysis, _, _) = Checked(document);
        foreach (var loadCase in analysis.Loading.Cases)
        {
            var (load, reaction) = (analysis.TotalLoad(loadCase), analysis.TotalReaction(loadCase));
            Assert.True((load + reaction).Length < 0.01 * Math.Max(1, load.Length), $"{loadCase.Name}: {load} against {reaction}");
        }

        Assert.Contains(analysis.Notes, note => note.Contains("5.5.1.2(4)", StringComparison.Ordinal));
        var windward = analysis.Loading.Cases.First(loadCase => loadCase.Nature == LoadNature.Wind && Math.Abs(loadCase.Wind!.Value.Y) > 0.5);
        Assert.True(analysis.CrossWallsOf(windward).Length > 1, "it takes some of the wind");
    }

    [Fact]
    public void ACrossWallTakesTheWindOffALongWall()
    {
        double Worst(BimDocument document, bool meets) => Checked(document).Checks
            .Where(check => check.Panel.Element is Wall wall && Math.Abs(wall.End.Y - wall.Start.Y) < 1 == meets)
            .Max(check => check.Checks.Single(each => each.Name == "Bending up its height").Ratio);

        // Studs have no such stiffness; block across the same line eases the long walls it meets, each now a shorter panel.
        var (stud, _) = Crossed("Timber, Softwood", 100);
        var (block, _) = Crossed("Concrete Block", 100);
        var (without, with) = (Worst(stud, true), Worst(block, true));
        Assert.True(with < 0.9 * without, $"{with:P0} with it, {without:P0} without");

        // The walls it runs beside it does nothing for.
        Assert.Equal(Worst(stud, false), Worst(block, false), 1);
    }

    [Theory]
    [InlineData("Timber, Softwood", 100, 8000, 0)]
    [InlineData("Concrete Block", 20, 8000, 0)]
    [InlineData("Concrete Block", 100, 400, 0)]
    [InlineData("Concrete Block", 100, 4000, 1)]
    public void OnlyAStiffeningWallStiffens(string material, double thickness, double length, int meetings) =>
        Assert.Equal(meetings, AnalyticalModel.Of(Crossed(material, thickness, length).Document).Stiffeners.Count);

    [Fact]
    public void AUsProjectsMasonryIsSaidNotToBeChecked()
    {
        var document = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var (_, _, checks) = Checked(document);
        Assert.All(checks, check =>
        {
            Assert.Empty(check.Checks);
            Assert.Contains("TMS 402", check.Limits);
        });
    }
}
