using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Tests;

/// <summary>
/// The furniture: at the sizes it is bought in, drawn as it is built, and kept out of a door's
/// swing and out of the room another piece takes.
/// </summary>
public class FurnitureTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>A room 4 m by 3 m, walled on three sides, its open side to the south.</summary>
    private static (BimDocument Document, Wall Back, Wall Left) Room()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        Wall Wall(Point2D start, Point2D end)
        {
            var wall = new Wall { Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            return wall;
        }

        var back = Wall(new Point2D(0, 3000), new Point2D(4000, 3000));
        var left = Wall(new Point2D(0, 0), new Point2D(0, 3000));
        Wall(new Point2D(4000, 0), new Point2D(4000, 3000));
        return (document, back, left);
    }

    private static Component Put(BimDocument document, string name, double x, double y, double rotation = 0)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation };
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void AWardrobeInADoorsSwingBlocksItAndOneClearOfItDoesNot()
    {
        var (document, _, left) = Room();
        var single = document.TypesOf<DoorType>().First(type => type.Name.StartsWith("Single"));
        var door = new Door { TypeId = single.Id, LevelId = left.LevelId, HostWallId = left.Id, DistanceAlongWall = 1500, Mark = "D1" };
        document.Add(door);

        // A quarter circle a leaf wide, off the face of the wall it opens from.
        var sweep = Assert.Single(DoorSwing.Sweeps(document, door));
        var face = document.GetWallType(left)!.Structure.TotalWidth / 2;
        var reach = sweep.Max(point => Math.Abs(point.X));
        Assert.InRange(reach, face + 700, face + 1000);
        Assert.Empty(Clearances.Blocked(document, left.LevelId, ClearanceStandard.ApprovedDocumentM));

        // A wardrobe on the side it opens to, where the leaf sweeps: in its way.
        var side = sweep.Average(point => point.X);
        var wardrobe = Put(document, "Wardrobe - 1000 x 600", side, 1500);
        var blocked = Assert.Single(Clearances.Blocked(document, left.LevelId, ClearanceStandard.ApprovedDocumentM));
        Assert.Equal(door, blocked.Zone.Element);
        Assert.Null(blocked.Zone.Component);
        Assert.Equal("Wardrobe - 1000 x 600", blocked.By);
        Assert.StartsWith("Single", blocked.Message);
        Assert.Contains("(D1): its swing is blocked by Wardrobe", blocked.Message);

        // Moved along the wall clear of the leaf, it is not.
        wardrobe.Location = new Point2D(side, 2400);
        Assert.Empty(Clearances.Blocked(document, left.LevelId, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void TwoPiecesInOnePlaceAreCalledOutButAChairUnderATableIsNot()
    {
        var (document, _, _) = Room();
        var level = document.Levels[0].Id;

        var table = Put(document, "Table - Dining 1800 x 900", 2000, 1500);
        Put(document, "Chair - Dining", 2000, 1500 - 450);
        Assert.Empty(Clearances.Overlaps(document, level));

        // An armchair pushed into the table.
        var armchair = Put(document, "Armchair", 2000 + 900, 1500);
        var overlap = Assert.Single(Clearances.Overlaps(document, level));
        Assert.Equal(armchair, overlap.Zone.Component);
        Assert.Equal(table.TypeId, Type(document, overlap.By).Id);
        Assert.Contains("Armchair: the room it takes is blocked by Table - Dining 1800 x 900", overlap.Message);

        // A wall unit over a base unit is not in its way: it hangs above it.
        armchair.Location = new Point2D(2000 + 1800, 1500);
        Assert.Empty(Clearances.Overlaps(document, level));
        var unit = Put(document, "Base Unit - 600", 600, 2500);
        var over = Put(document, "Wall Unit - 600", 600, 2500 - 125);
        over.Elevation = 1400;
        Assert.Empty(Clearances.Overlaps(document, level));
        Assert.NotNull(unit);
    }

    [Fact]
    public void AWallUnitAboveThePlansCutIsDrawnOverheadButASocketKeepsItsSymbol()
    {
        var document = BimDocument.CreateDefault();
        Assert.True(ComponentModel.DrawnOverhead(Type(document, "Wall Unit - 600")));
        Assert.False(ComponentModel.DrawnOverhead(Type(document, "Socket - Double")));
        Assert.False(ComponentModel.DrawnOverhead(Type(document, "Wall Light")));
        Assert.False(ComponentModel.DrawnOverhead(Type(document, "Ceiling Light - Pendant")));
    }

    /// <summary>A room walled all round, its inside <paramref name="width"/> by <paramref name="depth"/> from (0, 0), and the room in it.</summary>
    private static (BimDocument Document, Room Room) Enclosed(double width, double depth)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(-half, -half), new Point2D(width + half, -half), new Point2D(width + half, depth + half), new Point2D(-half, depth + half) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 });

        var room = new Room { LevelId = level, Location = new Point2D(width / 2, depth / 2), Name = "Bedroom", Number = "1" };
        document.Add(room);
        return (document, room);
    }

    [Fact]
    public void BedsAreTheSizesTheirMattressesAreSoldInAndAnOldDoubleBecomesTheKingItWas()
    {
        var document = BimDocument.CreateDefault();
        foreach (var (name, width, depth) in new[]
                 {
                     ("Bed - Single 900 x 1900", 900.0, 1900.0), ("Bed - Small Double 1200 x 1900", 1200, 1900), ("Bed - Double 1350 x 1900", 1350, 1900),
                     ("Bed - King 1500 x 2000", 1500, 2000), ("Bed - Super King 1800 x 2000", 1800, 2000),
                     ("Bed - Twin 38 x 75 in", 965, 1905), ("Bed - Full 54 x 75 in", 1372, 1905), ("Bed - Queen 60 x 80 in", 1524, 2032),
                     ("Bed - King 76 x 80 in", 1930, 2032), ("Bed - California King 72 x 84 in", 1829, 2134)
                 })
            Assert.Equal((width, depth), (Type(document, name).Width, Type(document, name).Depth));

        // Its mattress's top at 550, as a bed is got into; a dining chair's seat at 450.
        var king = Type(document, "Bed - King 1500 x 2000");
        Assert.Equal(550, ComponentModel.Parts(king).Where(part => part.To - part.From > 1400 && part.Front - part.Back > 1800).Max(part => part.Top), 6);
        var chair = Type(document, "Chair - Dining");
        Assert.Contains(ComponentModel.Parts(chair), part => part.Bottom == 450 && part.To - part.From == chair.Width && part.Front - part.Back == chair.Depth);

        // A project saved when the king was called "Bed - Double" opens with it called a king, and no second one.
        var older = BimDocument.CreateDefault();
        older.RemoveType(Type(older, "Bed - Double 1350 x 1900"));
        Type(older, "Bed - King 1500 x 2000").Name = "Bed - Double";
        older.EnsureDefaultTypes();
        Assert.Single(older.TypesOf<ComponentType>(), type => type.Name == "Bed - King 1500 x 2000");
        Assert.DoesNotContain(older.TypesOf<ComponentType>(), type => type.Name == "Bed - Double");
        Assert.Single(older.TypesOf<ComponentType>(), type => type.Name == "Bed - Double 1350 x 1900");
    }

    [Fact]
    public void OnlyThePrincipalBedWantsBothSidesClearAndAnotherDoubleOneSideAndItsFoot()
    {
        var (document, _, _) = Room();
        var king = Put(document, "Bed - King 1500 x 2000", 1200, 1500);
        var other = Put(document, "Bed - Double 1350 x 1900", 2900, 1500);
        Assert.Equal(king, Clearances.PrincipalBed(document));

        var principal = Clearances.Of(document, king, ClearanceStandard.ApprovedDocumentM);
        Assert.Equal(new[] { "the access zone to the left of the bed", "the access zone to the right of the bed", "the access zone at the foot of the bed" },
            principal.Select(zone => zone.Zone.What));
        Assert.All(principal, zone => Assert.Contains("2.25b", zone.Zone.Rule));

        var another = Clearances.Of(document, other, ClearanceStandard.ApprovedDocumentM);
        Assert.Equal(new[] { "the access zone beside the bed", "the access zone at the foot of the bed" }, another.Select(zone => zone.Zone.What));
        Assert.All(another, zone => Assert.Contains("2.25c", zone.Zone.Rule));

        // Facing down the page, its head is up it; the side zones run from 600 off the head, where a bedside table may stand.
        var head = king.Location.Y + 1000;
        Assert.Equal(head - 600, principal[0].Zone.Outline.Max(p => p.Y), 6);
    }

    [Fact]
    public void ForAWheelchairUserABedWantsAMetreAndRoomToTurnAndAChairMayStandInIt()
    {
        var (document, _, _) = Room();
        var king = Put(document, "Bed - King 1500 x 2000", 2000, 1500);

        var zones = Clearances.Of(document, king, ClearanceStandard.ApprovedDocumentM3);
        Assert.Equal(5, zones.Count);
        Assert.Contains(zones, zone => zone.Zone.What == "the turning space to the left of the bed");
        var left = zones.Single(zone => zone.Zone.What == "the access zone to the left of the bed").Zone.Outline;
        Assert.Equal(1000, left.Max(p => p.X) - left.Min(p => p.X), 6);

        // A chair beside it is let be under M4(3), not under M4(2).
        Put(document, "Chair - Dining", 2000 - 750 - 300, 1500 + 400);
        Assert.DoesNotContain(Clearances.Of(document, king, ClearanceStandard.ApprovedDocumentM3), zone => zone.In.Contains("Chair - Dining"));
        Assert.Contains(Clearances.Of(document, king, ClearanceStandard.ApprovedDocumentM), zone => zone.In.Contains("Chair - Dining"));

        // A wardrobe against the back wall wants a metre clear in front of it.
        var wardrobe = Put(document, "Wardrobe - 1000 x 600", 3300, 2600);
        var front = Assert.Single(Clearances.Of(document, wardrobe, ClearanceStandard.ApprovedDocumentM3));
        Assert.Contains("3.35", front.Zone.Rule);
        Assert.Empty(Clearances.Of(document, wardrobe, ClearanceStandard.ApprovedDocumentM));
    }

    [Fact]
    public void ADiningTableWantsRoomToDrawAChairOutButItsChairsMayStandThere()
    {
        var (document, _, _) = Room();
        var table = Put(document, "Table - Dining 1200 x 800", 2000, 1500);
        foreach (var x in new[] { 1700.0, 2300 })
        {
            Put(document, "Chair - Dining", x, 1500 - 400 - 150, rotation: 180);
            Put(document, "Chair - Dining", x, 1500 + 400 + 150);
        }

        var zones = Clearances.Of(document, table, ClearanceStandard.ApprovedDocumentM);
        Assert.Equal(2, zones.Count);
        Assert.All(zones, zone => Assert.Empty(zone.In));
        Assert.All(zones, zone => Assert.Contains("NKBA", zone.Zone.Rule));

        // Pushed up to the back wall there is no room to sit on that side.
        table.Location = new Point2D(2000, 2900 - 400 - 300);
        Assert.Contains(Clearances.Of(document, table, ClearanceStandard.ApprovedDocumentM), zone => zone.In.Contains("the wall"));

        // A round table's room is all round it: 800 more every way, 915 for a wheelchair user.
        var round = Put(document, "Table - Round 1200", 2000, 900);
        var ring = Assert.Single(Clearances.Of(document, round, ClearanceStandard.ApprovedDocumentM)).Zone.Outline;
        Assert.Equal(1200 + 2 * 800, ring.Max(p => p.X) - ring.Min(p => p.X), 0);
        var wider = Assert.Single(Clearances.Of(document, round, ClearanceStandard.ApprovedDocumentM3)).Zone.Outline;
        Assert.Equal(1200 + 2 * 915, wider.Max(p => p.X) - wider.Min(p => p.X), 0);
    }

    [Fact]
    public void ABedroomIsCheckedForItsSizeByTheBedsInIt()
    {
        // 3.4 m by 3.2 m inside: 10.9 m², short of the 11.5 a double bedroom wants.
        var (document, room) = Enclosed(3400, 3200);
        Assert.Null(Bedrooms.Verdict(document, room));
        Put(document, "Bed - King 1500 x 2000", 1700, 1100);

        var verdict = Bedrooms.Verdict(document, room)!;
        Assert.StartsWith("Principal bedroom: 10.9 m², 3.20 m wide - 0.6 m² too small", verdict);
        Assert.Contains("Nationally Described Space Standard", verdict);
        Assert.Contains(room.GetInstanceParameters(document), parameter => parameter.Name == "Bedroom Size");

        // As a wheelchair user's it wants 13.5 m²; under the IRC, 6.5 m² is enough.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM3;
        Assert.Contains("13.5 m²", Bedrooms.Verdict(document, room));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.EndsWith("big enough", Bedrooms.Verdict(document, room));

        // A single bed alone makes a single bedroom, which this one is big enough for.
        var (single, small) = Enclosed(2400, 3200);
        Put(single, "Bed - Single 900 x 1900", 1000, 1100);
        Assert.Equal("Single bedroom: 7.7 m², 2.40 m wide - big enough", Bedrooms.Verdict(single, small));
    }

    [Fact]
    public void ADwellingIsSizedByItsBedroomsBedspacesAndStoreys()
    {
        Assert.Equal(50, Dwellings.AreaAsked(1, 2, 1));
        Assert.Equal(58, Dwellings.AreaAsked(1, 2, 2));
        Assert.Equal(99, Dwellings.AreaAsked(3, 5, 3));
        Assert.Equal(61, Dwellings.AreaAsked(2, 2, 1));
        Assert.Equal(79, Dwellings.AreaAsked(2, 6, 2));
        Assert.Equal(39, Dwellings.AreaAsked(1, 1, 2));
        Assert.Equal(1.5, Dwellings.StorageAsked(1, 2));
        Assert.Equal(2.5, Dwellings.StorageAsked(3, 5));

        // A bedroom alone, 10.9 m², is far short of the 50 m² a flat for two asks, and has no store.
        var (document, _) = Enclosed(3400, 3200);
        Assert.Null(Dwellings.Verdict(document));
        Put(document, "Bed - King 1500 x 2000", 1700, 1100);
        var verdict = Dwellings.Verdict(document)!;
        Assert.StartsWith("1 bedroom, 2 bedspaces, 1 storey: rooms 10.9 m² - ", verdict);
        Assert.Contains("asks 50 m²", verdict);
        Assert.Contains("it asks 1.5 m²", verdict);

        // 8 m by 7 m, with a store of 1.2 m by 1.5 m: big enough.
        var (flat, _) = Enclosed(8000, 7000);
        Put(flat, "Bed - King 1500 x 2000", 1700, 1100);
        var wallType = flat.TypesOf<WallType>().First();
        var half = wallType.Structure.TotalWidth / 2;
        var corners = new[] { new Point2D(9000 - half, -half), new Point2D(10200 + half, -half), new Point2D(10200 + half, 1500 + half), new Point2D(9000 - half, 1500 + half) };
        for (var i = 0; i < 4; i++)
            flat.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = flat.Levels[0].Id, UnconnectedHeight = 2400 });
        flat.Add(new Room { LevelId = flat.Levels[0].Id, Location = new Point2D(9600, 750), Name = "Store", Number = "2" });
        Assert.Equal("1 bedroom, 2 bedspaces, 1 storey: rooms 57.8 m² - big enough", Dwellings.Verdict(flat));

        flat.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        Assert.Null(Dwellings.Verdict(flat));
    }

    [Fact]
    public void TheNewPiecesAreBuiltAsTheyAre()
    {
        var document = BimDocument.CreateDefault();

        // A chest's drawers: four high in a 750 chest, two columns in a sideboard.
        Assert.Equal((4, 1), ComponentModel.DrawersOf(Type(document, "Chest of Drawers - 750")));
        Assert.Equal(2, ComponentModel.DrawersOf(Type(document, "Sideboard - 1600")).Columns);

        // A corner sofa is an L in plan, a round table round.
        var frame = new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0);
        Assert.Equal(6, ComponentModel.PlanOutlines(Type(document, "Sofa - Corner 2600 x 1700"), frame)[0].Count);
        Assert.Equal(32, ComponentModel.PlanOutlines(Type(document, "Table - Round 1200"), frame)[0].Count);

        // A bunk bed's upper bed is more than 600 up, guarded 260 and more above its base, its way in 300 to 400.
        var bunk = Type(document, "Bunk Bed - 900 x 1900");
        var (_, upper, opening) = ComponentModel.BunkOf(bunk);
        Assert.True(upper >= 600);
        Assert.True(bunk.Height - upper >= 260);
        Assert.InRange(opening, 300, 400);

        // A cot's slats are 45 to 65 apart, as BS EN 716 sets them.
        var slats = ComponentModel.Parts(Type(document, "Cot - 600 x 1200"))
            .Where(part => part.To - part.From == 20 && part.Front < -600 && part.Top - part.Bottom > 400)
            .OrderBy(part => part.From).ToList();
        Assert.True(slats.Count >= 6);
        Assert.InRange(slats[1].From - slats[0].To, 45, 65);
    }

    [Fact]
    public void ACoffeeTableInTheCrookOfACornerSofaIsNotInItsWay()
    {
        var (document, _, _) = Room();

        // Facing down the page: its L along the top, and down the page's right - its own right.
        Put(document, "Sofa - Corner 2600 x 1700", 2000, 1500);
        var table = Put(document, "Coffee Table - 1100 x 600", 1500, 1000);
        Assert.Empty(Clearances.Overlaps(document, document.Levels[0].Id));

        // Moved into the L's seats, it is.
        table.Location = new Point2D(1500, 1700);
        Assert.Single(Clearances.Overlaps(document, document.Levels[0].Id));
    }
}
