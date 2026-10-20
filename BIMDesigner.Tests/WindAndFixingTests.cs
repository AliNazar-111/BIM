using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;
using Xbim.Ifc4.SharedComponentElements;

namespace BIMDesigner.Tests;

/// <summary>
/// The wind on cladding worked out for the site - BS EN 1991-1-4 in England, ASCE 7 in the US - and
/// a curtain wall's mullions anchored to the floors and spliced up its height.
/// </summary>
public class WindAndFixingTests
{
    /// <summary>A box 10 m by 8 m, 3 m high: outside walls, its south side a storefront.</summary>
    private static (BimDocument Document, Wall Front) Box(double height = 3000, string curtain = "Storefront")
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var outside = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior && type.Log is null).Id;
        var glass = document.TypesOf<CurtainWallType>().Single(type => type.Name.Contains(curtain)).Id;
        Point2D[] corners = { new(0, 0), new(10000, 0), new(10000, 8000), new(0, 8000) };
        Wall? front = null;
        for (var i = 0; i < 4; i++)
        {
            var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], LevelId = level, UnconnectedHeight = height, TypeId = i == 0 ? glass : outside };
            document.Add(wall);
            front ??= wall;
        }

        return (document, front!);
    }

    [Fact]
    public void TheUkPeakPressureIsTheEurocodesForTheTerrainRaisedForAltitude()
    {
        // 22 m/s at 10 m over open country: an exposure factor of 2.35 on ½ρvb² (BS EN 1991-1-4 Figure 4.2).
        var basic = 0.5 * 1.226 * 22 * 22 / 1000;
        Assert.Equal(2.352, Wind.PeakPressure(22, 0, WindTerrain.OpenCountry, 10) / basic, precision: 3);

        // 100 m up, the speed is a tenth more; above 10 m less so (UK National Annex NA.2.5).
        Assert.Equal(1.21, Wind.PeakPressure(22, 100, WindTerrain.OpenCountry, 10) / Wind.PeakPressure(22, 0, WindTerrain.OpenCountry, 10), precision: 6);
        var raised = 1 + 0.1 * Math.Pow(0.5, 0.2);
        Assert.Equal(raised * raised, Wind.PeakPressure(22, 100, WindTerrain.OpenCountry, 20) / Wind.PeakPressure(22, 0, WindTerrain.OpenCountry, 20), precision: 6);

        // Rougher ground slows it near the ground: sea, country, suburb, town.
        var byTerrain = new[] { WindTerrain.Sea, WindTerrain.OpenCountry, WindTerrain.Suburban, WindTerrain.Town }
            .Select(terrain => Wind.PeakPressure(22, 0, terrain, 10)).ToList();
        Assert.Equal(byTerrain.OrderByDescending(q => q), byTerrain);
    }

    [Fact]
    public void TheUsVelocityPressureIsAsce7s()
    {
        // 115 mph, exposure C, at 15 ft: Kz 0.85, Kd 0.85 - 24.4 psf.
        Assert.Equal(24.43, Wind.VelocityPressure(115, 0, WindTerrain.OpenCountry, 4.572) / 0.04788, precision: 1);

        // Exposure B there: Kz 0.57.
        Assert.Equal(0.575 / 0.849, Wind.VelocityPressure(115, 0, WindTerrain.Suburban, 4.572) / Wind.VelocityPressure(115, 0, WindTerrain.OpenCountry, 4.572), precision: 2);
    }

    [Fact]
    public void InEnglandACornerIsSuckedHardestAndTheMiddlePushedOn()
    {
        var (document, front) = Box();
        document.ProjectInformation.BasicWindSpeed = 22;
        var qp = Wind.PeakPressure(22, 0, WindTerrain.OpenCountry, 3);

        // 3 m by 1.5 m - 4.5 m² - near a corner: zone A, -1.27 less +0.2 inside.
        var corner = Wind.OnMember(document, front, 300, 3000, 1500);
        Assert.Contains("zone A", corner.Said);
        Assert.Equal((1.4 - 0.2 * Math.Log10(4.5) + 0.2) * qp, corner.Pressure, precision: 6);

        // In the middle, the wind against the face is worse than the side suction: zone D, +0.81 and -0.3 inside.
        var middle = Wind.OnMember(document, front, 5000, 3000, 1500);
        Assert.Contains("zone D", middle.Said);
        var windward = 0.7 + 0.1 * (3.0 / 8 - 0.25) / 0.75;
        Assert.Equal((1.0 - (1.0 - windward) * Math.Log10(4.5) + 0.3) * qp, middle.Pressure, precision: 6);

        // No wind speed: the pressure as set.
        document.ProjectInformation.BasicWindSpeed = 0;
        Assert.Equal(1.0, Wind.OnMember(document, front, 300, 3000, 1500).Pressure);
    }

    [Fact]
    public void InTheUsACornerIsZoneFiveAndDeflectionIsCheckedAtSixTenths()
    {
        var (document, front) = Box();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BasicWindSpeed = 115;
        var qh = Wind.VelocityPressure(115, 0, WindTerrain.OpenCountry, 3);

        // 4.5 m² is 48 sq ft: GCp -1.16 at a corner, -0.98 in the field; GCpi ±0.18.
        var fraction = Math.Log10(4.5 * 10.7639 / 10) / Math.Log10(50);
        var corner = Wind.OnMember(document, front, 300, 3000, 1500);
        Assert.Contains("zone 5", corner.Said);
        Assert.Equal(0.6 * (1.4 - 0.6 * fraction + 0.18) * qh, corner.Pressure, precision: 6);
        var field = Wind.OnMember(document, front, 5000, 3000, 1500);
        Assert.Contains("zone 4", field.Said);
        Assert.Equal(0.6 * (1.1 - 0.3 * fraction + 0.18) * qh, field.Pressure, precision: 6);
    }

    [Fact]
    public void TheMullionsAreCheckedUnderTheWindWhereTheyStand()
    {
        var (document, front) = Box();
        document.ProjectInformation.BasicWindSpeed = 22;
        var bending = MullionRules.Of(document, front).Where(b => b.What == "mullion").ToList();

        // The upright nearest the corner takes more than one in the middle, each as its own wind says.
        var nearest = bending.Where(b => !b.Mullion.IsBorder).MinBy(b => b.Mullion.From)!;
        var middle = bending.Where(b => !b.Mullion.IsBorder).MinBy(b => Math.Abs(b.Mullion.From - 5000))!;
        Assert.Contains("zone", nearest.Wind);
        Assert.True(nearest.Pressure > 0);
        Assert.Equal(Wind.OnMember(document, front, (middle.Mullion.From + middle.Mullion.To) / 2, middle.Span, 1500).Pressure, middle.Pressure, precision: 9);
        Assert.Contains("qp", Wind.Verdict(document));
    }

    [Fact]
    public void TheStickSystemIsAnchoredAtEachFloorAndSplicedAboveIt()
    {
        // Two storeys of it: held at the ground, the first floor and its head; spliced 300 over the first floor.
        var (document, front) = Box(6000, "Stick System");
        var (anchors, splices, lengths) = MullionFixings.Of(document, front);
        var uprights = CurtainLayout.Of(document, front)!.Mullions.Count(m => m.IsVertical);
        Assert.Equal(uprights * 3, anchors.Count);
        Assert.Equal(uprights, splices.Count);
        Assert.All(splices, splice => Assert.Equal(3300, splice.Height, precision: 6));
        Assert.Equal(3300, lengths.Max(piece => piece.To - piece.From), precision: 6);

        // An upright between panels 1.5 m apart: the wind on 3 m of it at the first floor - 1 kPa x 1.5 m x 3 m -
        // and the weight of the length over the floor, its glass and itself, on the anchor there.
        var middle = CurtainLayout.Of(document, front)!.Mullions.Where(m => m.IsVertical && !m.IsBorder).MinBy(m => Math.Abs(m.From - 5000))!;
        var atFloor = anchors.Single(anchor => anchor.Mullion == middle && Math.Abs(anchor.Height - 3000) < 1);
        Assert.Equal(4.5, atFloor.Wind, precision: 6);
        var section = middle.Type!.Section().Properties().Area;
        Assert.Equal((0.0003 * 1500 * 2700 + section * 2700 * 2700 * 9.81e-9) / 1000, atFloor.Dead, precision: 6);
        Assert.True(anchors.Single(anchor => anchor.Mullion == middle && anchor.Height > 5000).Dead == 0);
        Assert.Empty(MullionFixings.Problems(document, front));
        Assert.StartsWith($"{uprights * 3} anchors", MullionFixings.Verdict(document, front));
    }

    [Fact]
    public void AStackJointTooNarrowAnAnchorTooWeakAndALengthTooLongAreSaid()
    {
        var (document, front) = Box(6000, "Stick System");
        var type = document.FindType<CurtainWallType>(front.TypeId)!;

        // 3.3 m of aluminium moves 7.5 mm over 99 K; with the floor's 15.9, 23 mm wanted.
        type.StackJoint = 20;
        Assert.Contains(MullionFixings.Problems(document, front), problem => problem.Said.Contains("23 mm wanted"));
        Assert.Equal(23e-6 * 3300 * 99 + 15.9, MullionFixings.JointNeeded(MullionMaterial.Aluminium, 3300), precision: 6);
        type.StackJoint = 25;

        type.AnchorCapacity = 3;
        Assert.Contains(MullionFixings.Problems(document, front), problem => problem.Said.Contains("over the 3.00 kN"));
        type.AnchorCapacity = 0;

        // Unspliced, 8 m of aluminium is longer than it is sold in.
        type.Splices = MullionSplices.None;
        front.UnconnectedHeight = 8000;
        Assert.Contains(MullionFixings.Problems(document, front), problem => problem.Rule.Contains("24 ft"));
        Assert.Contains(WallRules.Problems(document, front, ClearanceStandard.ApprovedDocumentM), problem => problem.Rule.Contains("24 ft"));
    }

    [Fact]
    public void In3DTheSplicesAreGapsAndTheAnchorsBrackets()
    {
        var (document, front) = Box(6000, "Stick System");
        var meshes = ModelMeshBuilder.BuildWall(document, front);
        var frame = meshes.Where(m => m.Kind == MeshKind.Mullion && !m.Description.Contains("Galvanised")).SelectMany(m => m.Positions).ToList();
        Assert.Contains(frame, p => Math.Abs(p.Z - 3287.5) < 0.5);
        Assert.Contains(frame, p => Math.Abs(p.Z - 3312.5) < 0.5);

        var brackets = meshes.Single(m => m.Description.Contains("Galvanised"));
        Assert.Contains(brackets.Positions, p => Math.Abs(p.Z - 3100) < 0.5);

        // Built apart, each upright and transom is its own, and each anchor.
        var parts = ModelMeshBuilder.CurtainWallParts(document, front)!;
        Assert.Equal(CurtainLayout.Of(document, front)!.Mullions.Count, parts.Mullions.Count);
        Assert.Equal(MullionFixings.Of(document, front).Anchors.Count, parts.Anchors.Count);
    }

    [Fact]
    public void InIfcEachMullionIsAMemberAndEachAnchorABracket()
    {
        var (document, front) = Box(6000, "Stick System");
        using var model = IfcExport.Build(document);
        Assert.Equal(CurtainLayout.Of(document, front)!.Mullions.Count, model.Instances.OfType<IfcMember>().Count());
        var brackets = model.Instances.OfType<IfcDiscreteAccessory>().ToList();
        Assert.Equal(MullionFixings.Of(document, front).Anchors.Count, brackets.Count);
        Assert.All(brackets, bracket => Assert.Equal(Xbim.Ifc4.Interfaces.IfcDiscreteAccessoryTypeEnum.BRACKET, bracket.PredefinedType));
        Assert.Contains(model.Instances.OfType<IfcMember>(), member => member.Name.ToString()!.Contains("Capped"));
    }

    [Fact]
    public void TheSiteAndTheFixingsAreSaved()
    {
        var (document, front) = Box(6000, "Stick System");
        document.ProjectInformation.BasicWindSpeed = 24.5;
        document.ProjectInformation.SiteAltitude = 85;
        document.ProjectInformation.Terrain = WindTerrain.Suburban;
        var type = document.FindType<CurtainWallType>(front.TypeId)!;
        type.Splices = MullionSplices.EveryOtherFloor;
        type.SpliceAbove = 450;
        type.AnchorCapacity = 12;

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var again = ProjectFile.Load(path);
            Assert.Equal((24.5, 85.0, WindTerrain.Suburban), (again.ProjectInformation.BasicWindSpeed, again.ProjectInformation.SiteAltitude, again.ProjectInformation.Terrain));
            var curtain = again.FindType<CurtainWallType>(type.Id)!;
            Assert.Equal((true, MullionSplices.EveryOtherFloor, 450.0, 25.0, 12.0), (curtain.MullionAnchors, curtain.Splices, curtain.SpliceAbove, curtain.StackJoint, curtain.AnchorCapacity));
        }
        finally
        {
            File.Delete(path);
        }

        // The type's own settings in Type Properties, as the others are.
        var names = type.GetTypeParameters(document).Select(p => p.Definition.Name).ToList();
        Assert.Contains("Mullion Splices", names);
        Assert.Contains("Stack Joint", names);
        Assert.Equal(EnumText.Humanise(MullionSplices.EveryOtherFloor), type.GetTypeParameters(document).Single(p => p.Definition.Name == "Mullion Splices").Value);
    }
}
