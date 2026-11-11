using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Tests;

/// <summary>Robustness's leftovers: walls and beams as key elements of 34 kN/m², and timber at its accidental partial factors, against figures worked by hand.</summary>
public class KeyElementsTests
{
    [Fact]
    public void TimberInAnAccidentIsAtItsAccidentalFactors()
    {
        // BS EN 1995-1-1 Table 2.3's γM 1.0 for an accident and Table 3.1's kmod 1.1 for an instantaneous load, against 1.3 and its own weight's 0.6: (1.1 / 1.0) / (0.6 / 1.3) = 2.38 times as strong.
        var d = BimDocument.CreateDefault();
        var timber = d.TypesOf<BeamType>().Where(each => each.Material == BeamMaterial.Timber).OrderBy(each => Math.Abs(each.Depth - 220)).First();
        d.Add(new Beam { TypeId = timber.Id, LevelId = d.Levels[1].Id, Start = new Point2D(0, 0), End = new Point2D(4000, 0) });
        var model = AnalyticalModel.Of(d);
        var member = model.Members.Single();
        var lengths = MemberStrength.LengthsOf(model, member);
        var own = new LoadCase("G1", "Its own weight", LoadNature.SelfWeight);
        var persistent = new LoadCombination("ULS", "BS EN 1990 6.10", LimitState.Ultimate, new[] { (own, 1.35) });
        var accident = new LoadCombination("Accidental", "BS EN 1990 6.11b", LimitState.Ultimate, new[] { (own, 1.0) });
        var forces = new MemberForces(0, 0, 1, 0, 2, 0, false);
        var usual = MemberStrength.ChecksOf(d, member, lengths, new[] { (persistent, forces) }).Single(each => each.Name == "Bending, strong axis");
        var accidental = MemberStrength.ChecksOf(d, member, lengths, new[] { (accident, forces) }).Single(each => each.Name == "Bending, strong axis");
        Assert.Equal(usual.Capacity * (1.1 / 1.0) / (0.6 / 1.3), accidental.Capacity, 6);
        Assert.True(TimberDesign.Accidental(accident));
        Assert.False(TimberDesign.Accidental(persistent));
    }

    [Fact]
    public void ABeamAsAKeyElementTakes34OnItsTopAndThenItsSide()
    {
        // A UB 254 x 146 6 m long: on its top 34 × 0.146 = 4.97 kN/m, w L² / 8 = 22.4 kNm and w L / 2 = 14.9 kN more; on its side 34 × 0.251 = 8.55 kN/m, 38.5 kNm about its weak axis.
        var d = BimDocument.CreateDefault();
        var steel = d.TypesOf<BeamType>().First(each => each.Name.StartsWith("UB 254", StringComparison.Ordinal));
        d.Add(new Beam { TypeId = steel.Id, LevelId = d.Levels[1].Id, Start = new Point2D(0, 0), End = new Point2D(6000, 0) });
        var beam = AnalyticalModel.Of(d).Members.Single();
        var (depth, width) = (beam.Section.Dimensions[0], beam.Section.Dimensions[1]);
        var accident = new LoadCombination("Accidental", "BS EN 1990 6.11b", LimitState.Ultimate, Array.Empty<(LoadCase, double)>());
        var carried = new[] { (accident, new MemberForces(0, 0, 10, 0, 20, 0, false)) };
        var forces = NotionalRemoval.KeyBeamForces(accident, beam, carried);
        Assert.Equal(2, forces.Count);
        var (top, side) = (forces[0].Item2, forces[1].Item2);
        Assert.Equal(20 + 34 * width / 1000 * 36 / 8, top.My, 6);
        Assert.Equal(10 + 34 * width / 1000 * 6 / 2, top.Vz, 6);
        Assert.Equal(0, top.Mz, 9);
        Assert.Equal(34 * depth / 1000 * 36 / 8, side.Mz, 6);
        Assert.Equal(20, side.My, 9);
        Assert.Equal("BS EN 1990 6.11b, Ad 34 kN/m²", forces[0].Item1.Description);
    }

    private static (BimDocument Document, AnalyticalPanel Wall) Concrete()
    {
        var d = BimDocument.CreateDefault();
        var concrete = d.Materials.First(m => m.Name.StartsWith("Concrete, Cast", StringComparison.Ordinal));
        var type = new WallType("Concrete 200mm", new CompoundStructure(new MaterialLayer(LayerFunction.Structure, concrete.Id, 200))) { Function = WallFunction.Interior };
        d.AddType(type);
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = d.Levels[0].Id, TopLevelId = d.Levels[1].Id, StructuralUsage = StructuralUsage.Bearing };
        d.Add(wall);
        d.Add(Footings.Fit(d, wall));
        return (d, AnalyticalModel.Of(d).Panels.Single(panel => panel.Kind == AnalyticalPanelKind.Wall));
    }

    [Fact]
    public void AConcreteWallAsAKeyElementSpansBetweenItsFloors()
    {
        // 34 kN/m² over its 3 m: 34 × 3² / 8 = 38.25 kNm/m and 51 kN/m; a 200 wall, d 150, takes 0.167 fck b d².
        var (document, wall) = Concrete();
        var height = (wall.Outline.Max(node => node.At.Z) - wall.Outline.Min(node => node.At.Z)) / 1000;
        var checks = NotionalRemoval.KeyWall(document, wall, 50);
        var bending = checks.Single(each => each.Name == "Bending across its face");
        Assert.Equal(34 * height * height / 8, bending.Demand, 6);
        var fck = ConcreteDesign.StrengthOf(wall.Material.Quality);
        Assert.Equal(0.167 * 1000 * 150 * 150 * fck / 1e6, bending.Capacity, 6);
        Assert.Contains("mm²/m in each face", bending.Note);
        Assert.Equal(34 * height / 2, checks.Single(each => each.Name == "Shear across its face").Demand, 6);
    }

    [Fact]
    public void AMasonryWallAsAKeyElementLeansOnWhatBearsDownOnIt()
    {
        // The flats' blockwork: its flexural strength over γM 2.7, and what bears down on it at 0.9 of it, to 0.2 fd, by its section modulus a metre long. 40 kN/m on a 100 leaf is 0.36 N/mm².
        var d = NotionalRemovalTests.Flats(2);
        var model = AnalyticalModel.Of(d);
        var wall = model.Panels.First(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Material.Kind == AnalyticalMaterialKind.Masonry);
        var unit = MasonryDesign.LoadedUnitOf(d, wall);
        var t = wall.Thickness;
        var fxk1 = MasonryDesign.FlexuralStrengths(unit, t).Fxk1;
        var fd = MasonryDesign.Characteristic(unit) / MasonryDesign.GammaCompression;
        var bending = NotionalRemoval.KeyWall(d, wall, 40).Single();
        Assert.Equal((fxk1 / 2.7 + Math.Min(0.9 * 40 / t, 0.2 * fd)) * t * t / 6 / 1000, bending.Capacity, 6);
        Assert.Contains("no arching counted", bending.Note);
    }

    [Fact]
    public void TheFlatsEndWallAsAKeyElementIsShort()
    {
        // A whole end wall of the masonry flats taken away does not stand; as a key element its 100 blockwork leaf cannot take 34 kN/m² over 3 m, 38 kNm/m, with no arching counted.
        var d = NotionalRemovalTests.Flats(5);
        foreach (var block in d.Materials.Where(material => material.Name.Contains("Block", StringComparison.Ordinal))) block.UnitStrength = 3.6;
        var model = AnalyticalModel.Of(d);
        var analysis = StructuralAnalysis.Run(d, model, StructuralLoading.Of(d, model));
        var walls = MasonryDesign.Of(d, SecondOrderAnalysis.Run(d, analysis));
        var result = NotionalRemoval.Of(d, analysis, walls)!;
        var end = result.Removals.First(each => each.Name.StartsWith("W4,", StringComparison.Ordinal));
        Assert.False(end.Stands);
        var key = Assert.Single(end.Key);
        Assert.Equal("Bending across its face", key.Name);
        Assert.Equal(34 * 3.0 * 3.0 / 8, key.Demand, 1);
        Assert.True(key.Ratio > 1);
        Assert.False(end.Enough);
        Assert.Contains("A key element carries 34 kN/m² on its faces", result.Limits);
    }
}
