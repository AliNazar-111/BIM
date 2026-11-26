using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Tests;

/// <summary>
/// A US modern farmhouse, built in the real window (round 154): a standing-seam steel roof and the IRC's least slopes for metal - standing seam
/// 1/4 in 12, lapped panels 3 or 1/2 with lap sealant (R905.10.2), metal shingles 3 (R905.4.2) - metal shingles Class A on a noncombustible deck
/// (R902.1 exception 2); the laundry's receptacle within 6 ft of the washer (NEC 210.52(F), 210.50(C)); a closet asked no window; a US loft lived in.
/// </summary>
[Collection("Wpf")]
public class FarmhouseTests
{
    private const string Metal = "Roof - Standing Seam Steel on Plywood, 2x8 Rafters";

    private static BimDocument Us()
    {
        var document = BimDocument.CreateDefault();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        document.ProjectInformation.BuildingType = "Detached house";
        return document;
    }

    private static Guid TypeOf<T>(BimDocument document, string name) where T : ElementType => document.TypesOf<T>().First(each => each.Name == name).Id;

    /// <summary>A roof type of one covering material on plywood and rafters, the material made for it.</summary>
    private static RoofType Covered(BimDocument document, string covering)
    {
        var material = new Material(covering) { Density = 7850, ThermalConductivity = 50 };
        document.AddMaterial(material);
        var plywood = document.Materials.First(each => each.Name.Contains("Plywood", StringComparison.OrdinalIgnoreCase));
        var type = new RoofType($"Roof - {covering}", new CompoundStructure(new MaterialLayer(LayerFunction.Finish1, material.Id, 1), new MaterialLayer(LayerFunction.Substrate, plywood.Id, 15)));
        document.AddType(type);
        return type;
    }

    [Fact]
    public void MetalRoofsArePitchedAsTheIrcAsks()
    {
        var document = Us();
        Assert.Equal((0.25, "standing-seam metal panels", "R905.10.2"), SlabRules.IrcCoveringSlope(document, document.TypesOf<RoofType>().Single(type => type.Name == Metal)));
        Assert.Equal((3.0, "lapped metal panels", "R905.10.2"), SlabRules.IrcCoveringSlope(document, Covered(document, "Steel, Corrugated Sheet")));
        Assert.Equal((0.5, "lapped metal panels with lap sealant", "R905.10.2"), SlabRules.IrcCoveringSlope(document, Covered(document, "Steel, Lapped Panels with Lap Sealant")));
        Assert.Equal((3.0, "metal roof shingles", "R905.4.2"), SlabRules.IrcCoveringSlope(document, Covered(document, "Steel Shingles, Stone Coated")));
        Assert.Equal((2.0, "asphalt shingles", "R905.2.2"), SlabRules.IrcCoveringSlope(document, document.TypesOf<RoofType>().Single(type => type.Name == "Roof - Asphalt Shingles on Plywood, 2x8 Rafters")));

        // A porch's standing-seam roof at 1/8 in 12 is too flat; at 3 in 12, not.
        var roof = new Roof { LevelId = document.Levels[0].Id, TypeId = TypeOf<RoofType>(document, Metal), HeightOffset = 2743 };
        roof.SetBoundary(new[] { new Point2D(0, -2400), new Point2D(7000, -2400), new Point2D(7000, 0), new Point2D(0, 0) });
        roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i == 0, SlopeDegrees = Math.Atan(0.125 / 12) * 180 / Math.PI }));
        document.Add(roof);
        var said = Assert.Single(SlabRules.Problems(document, roof, ClearanceStandard.Irc), problem => problem.Rule.Contains("R905.10.2"));
        Assert.Equal("The IRC (R905.10.2) lays standing-seam metal panels at 1/4 in 12 at the least.", said.Rule);
        roof.SlopeDegrees = Math.Atan(3.0 / 12) * 180 / Math.PI;
        Assert.DoesNotContain(SlabRules.Problems(document, roof, ClearanceStandard.Irc), problem => problem.Rule.Contains("R905"));

        // The library's roof is offered in a US project only.
        var england = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.EnglandHouse });
        Assert.False(ProjectTemplates.Fits(england, england.TypesOf<RoofType>().Single(type => type.Name == Metal)));
        Assert.True(ProjectTemplates.Fits(document, document.TypesOf<RoofType>().Single(type => type.Name == Metal)));
    }

    [Fact]
    public void MetalShinglesAreMetalForTheirRoofClass()
    {
        // R902.1 exception 2: ferrous or copper shingles and metal shingles on a noncombustible deck are Class A; on a wood deck, as listed.
        Assert.Equal(UsRoofClass.A, UsRoofClass.Typical("Steel Shingles, Stone Coated", true)!.Value.Class);
        Assert.Equal(UsRoofClass.A, UsRoofClass.Typical("Copper Shingles", true)!.Value.Class);
        Assert.Null(UsRoofClass.Typical("Steel Shingles, Stone Coated", false));
        Assert.Null(UsRoofClass.Typical("Asphalt Shingles", true));
        Assert.Null(UsRoofClass.Typical("Cedar Shingles", true));
        Assert.Equal(UsRoofClass.A, UsRoofClass.Typical("Steel, Standing Seam Roof Panels, Prepainted", true)!.Value.Class);
    }

    /// <summary>Four partitions round a rectangle on the ground storey, and a room named in it.</summary>
    private static Room Box(BimDocument document, string name, double x0, double y0, double x1, double y1)
    {
        var level = document.Levels.OrderBy(each => each.Elevation).First();
        var type = TypeOf<WallType>(document, "Interior - Partition 125mm");
        var corners = new[] { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        for (var i = 0; i < 4; i++) document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type, LevelId = level.Id, UnconnectedHeight = 2400 });
        var room = new Room { LevelId = level.Id, Location = new Point2D((x0 + x1) / 2, (y0 + y1) / 2), Name = name };
        document.Add(room);
        return room;
    }

    private static Component Put(BimDocument document, string name, double x, double y, double elevation = 0)
    {
        var type = document.TypesOf<ComponentType>().First(each => each.Name == name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels.OrderBy(each => each.Elevation).First().Id, Location = new Point2D(x, y), Elevation = elevation };
        ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void TheLaundryHasAReceptacleWithin6FtOfTheWasher()
    {
        // A mudroom 3 m by 4 m, the washer at its north end: no receptacle, then one 8 ft off, then one 2 ft off (NEC 210.52(F), 210.50(C)).
        var document = Us();
        var mudroom = Box(document, "Mudroom", 0, 0, 3000, 4000);
        Put(document, "Washing Machine - 600", 1500, 3600);
        Assert.Contains("NEC 210.52(F)", Services.Verdict(document, mudroom));

        var far = Put(document, "Receptacle - Duplex 15 A", 1500, 100, 300);
        Assert.True(far.Location.DistanceTo(new Point2D(1500, 3600)) > 1829);
        Assert.Contains("NEC 210.52(F)", Services.Verdict(document, mudroom));
        Put(document, "Receptacle - Duplex 15 A", 2900, 3300, 1100);
        Assert.DoesNotContain("210.52(F)", Services.Verdict(document, mudroom) ?? string.Empty);

        // In England none of it.
        var england = BimDocument.CreateDefault();
        var utility = Box(england, "Utility", 0, 0, 3000, 4000);
        Put(england, "Washing Machine - 600", 1500, 3600);
        Assert.DoesNotContain("NEC", Services.Verdict(england, utility) ?? string.Empty);
    }

    [Fact]
    public void AClosetAsksNoWindowAndAUsLoftIsLivedIn()
    {
        // A walk-in closet 2 m by 4 m: no glass asked of it by R303.1 - it is no habitable room (R202); a bedroom of the same size is asked.
        var document = Us();
        var closet = Box(document, "Closet", 0, 0, 2000, 4000);
        Assert.DoesNotContain("R303.1", WindowRules.Verdict(document, closet) ?? string.Empty);
        closet.Name = "Bedroom";
        Assert.Contains("R303.1", WindowRules.Verdict(document, closet) ?? string.Empty);

        // A loft: lived in, in a US home - asked its light and receptacles; England's roof space, not.
        closet.Name = "Loft";
        var outline = closet.GetBoundary(document).Polygon;
        Assert.Equal(RoomUse.Habitable, Ventilation.UseOf(document, closet, outline));
        Assert.Contains("R303.1", WindowRules.Verdict(document, closet) ?? string.Empty);
        Assert.Contains("NEC 210.70(A)(1)", Services.Verdict(document, closet) ?? string.Empty);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Equal(RoomUse.Other, Ventilation.UseOf(document, closet, outline));
    }
}
