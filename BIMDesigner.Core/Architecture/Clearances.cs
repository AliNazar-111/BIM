using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using Area = BIMDesigner.Core.Views.ComponentModel.Area;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Whose rules say how much room a fitting needs round it - and, for the rest of the project,
/// whether it is built to the UK's codes or the US's.
/// </summary>
public enum ClearanceStandard
{
    /// <summary>England's Approved Document M, Volume 1: Category 2 (M4(2)) accessible and adaptable dwellings.</summary>
    ApprovedDocumentM,

    /// <summary>Approved Document M, Volume 1: Category 3 (M4(3)) wheelchair user dwellings.</summary>
    ApprovedDocumentM3,

    /// <summary>The International Residential Code, for a US house: the spaces round fixtures of R307 and P2705.</summary>
    Irc,

    /// <summary>The 2010 ADA Standards for Accessible Design.</summary>
    Ada
}

public static class ClearanceStandards
{
    /// <summary>Whether a standard is one of the US's - the IRC or the ADA - so the project is built to the US codes.</summary>
    public static bool IsUs(this ClearanceStandard standard) => standard is ClearanceStandard.Irc or ClearanceStandard.Ada;
}

/// <summary>
/// The room one fitting needs, in plan - or a door, to open: what it is for, its outline, and
/// the rule that asks for it.
/// </summary>
public sealed record ClearanceZone(Element Element, string What, IReadOnlyList<Point2D> Outline, string Rule)
{
    /// <summary>The fitting it is for; null for a door's.</summary>
    public Component? Component => Element as Component;
}

/// <summary>
/// A zone with something in it: what is in the way - or, for a kitchen piece set where the
/// kitchen's rules do not want it, what is wrong with where it is.
/// </summary>
public sealed record BlockedClearance(ClearanceZone Zone, string By, string FittingName, string? Said = null)
{
    public string Message => Said is null ? $"{FittingName}: {Zone.What} is blocked by {By}. {Zone.Rule}" : $"{FittingName}: {Said} {Zone.Rule}";
}

/// <summary>
/// The clear space a WC, basin, bath, shower, bed, wardrobe, kitchen or dining table needs round
/// it - as Approved Document M asks of an accessible and adaptable dwelling (M4(2)) or a
/// wheelchair user's (M4(3)), as the IRC asks of a US house, or as the ADA asks - and what is
/// standing in it.
///
/// M4(2) (Diagram 2.5, 2.24, 2.25): 1100 in front of a WC from 350 on its wall side to 1000 on
/// its open side, a basin coming into it by 300 at most; 1100 by 700 in front of a basin and
/// beside a bath, and here a shower; 750 to both sides and the foot of the principal bedroom's
/// double bed, to one side and the foot of any other double, to one side of a single; 1200 in
/// front of kitchen units and appliances. M4(3) (Diagram 3.11, 3.32, 3.35): 2200 out from the wall
/// behind a WC, 350 to 1200; 1650 by 800 at a basin; 800 along a bath; a 1500 turning circle at a
/// shower; 1000 round the beds - and 1200 by 1200 to turn beside the principal one - and in
/// front of a wardrobe or a chest; 1500 in front of a kitchen. The IRC (R307.1, P2705.1): 21 in
/// (533) in front of a WC, a basin and a bath and 15 in (381) from a WC's or basin's centreline
/// to anything beside it; 24 in (610) in front of a shower. The ADA: 1525 (60 in) wide from the
/// side wall by 1420 (56 in) deep from the rear wall round a WC (604.3); 760 by 1220 (30 by
/// 48 in) in front of a lavatory (606.2); 760 (30 in) along a bathtub (607.2); 915 by 1220 at a
/// transfer shower, 760 by 1525 at a roll-in one (608.2); 760 by 1220 beside a bed (806.2.3);
/// 1015 (40 in) in front of kitchen units (804.2). And for any of them, room to draw a chair out
/// at a dining table: 800 (the NKBA's 32 in), 915 (36 in) for a wheelchair user.
///
/// A zone is blocked by a wall it runs into, or by anything standing on the floor in it - not
/// by what is fixed high on a wall over it, nor, in a kitchen, by the rest of its own run, nor
/// by what the rule lets stand there: a chair at a table, a chair in a wheelchair user's bedroom.
/// </summary>
public static class Clearances
{
    /// <summary>How high off the floor something can be and still be in the way: knee height.</summary>
    public const double Underneath = 700;

    /// <summary>How much of a zone something has to take before it is in the way, mm²: a 100 square.</summary>
    private const double Touching = 100 * 100;

    public static string Label(ClearanceStandard standard) => standard switch
    {
        ClearanceStandard.ApprovedDocumentM3 => "Approved Document M (M4(3))",
        ClearanceStandard.Irc => "IRC 2021",
        ClearanceStandard.Ada => "ADA 2010",
        _ => "Approved Document M (M4(2))"
    };

    /// <summary>
    /// One zone a fitting needs, in its own frame: what it is for; where it may go - both places,
    /// where it may go either side; whether it is round; the rule asking for it; what may stand in
    /// it all the same - a chair at a table; and how far a basin may come into it - a WC's.
    /// </summary>
    private sealed record Zone(string What, Area[] Either, string Rule, bool Round = false, Func<ComponentType, bool>? MayStand = null, double BasinMay = 0);

    /// <summary>What may stand in a dining table's zone, or a wheelchair user's bedroom: a chair.</summary>
    private static bool AChair(ComponentType type) => type.Form is ComponentForm.Chair or ComponentForm.Armchair;

    /// <summary>
    /// The fitting's zones in its own frame: across it and front to back, the front +. Where one
    /// may go either side, both are offered. <paramref name="sideWall"/> says how far the face of
    /// a wall beside it is, to its left (-1) or right (+1), where there is one near; <paramref name="principal"/>
    /// whether a double bed is the principal bedroom's.
    /// </summary>
    private static IEnumerable<Zone> ZonesOf(ComponentType type, ClearanceStandard standard, Func<int, double?>? sideWall = null, bool principal = true)
    {
        var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
        var ada = standard == ClearanceStandard.Ada;
        var irc = standard == ClearanceStandard.Irc;
        var m43 = standard == ClearanceStandard.ApprovedDocumentM3;

        // Which side a WC's wall is on: the near one, or either where neither is within 600.
        int[] WallSides()
        {
            var near = new[] { -1, 1 }.Where(side => sideWall?.Invoke(side) is <= 600).ToArray();
            return near.Length == 1 ? near : new[] { -1, 1 };
        }

        switch (type.Form)
        {
            case ComponentForm.Toilet or ComponentForm.WallHungToilet when ada:
            {
                // Measured from the side wall beside it - or, with none near, from where one would
                // be with the centreline 455 (18 in) off it, either side.
                var either = new[] { -1, 1 }
                    .Where(side => sideWall?.Invoke(side) is not null)
                    .Select(side => sideWall!(side)!.Value * side)
                    .Select(face => face > 0 ? new Area(face - 1525, face, -halfD, -halfD + 1420) : new Area(face, face + 1525, -halfD, -halfD + 1420))
                    .ToArray();
                if (either.Length == 0) either = new[] { new Area(455 - 1525, 455, -halfD, -halfD + 1420), new Area(-455, 1525 - 455, -halfD, -halfD + 1420) };

                yield return new Zone("the clearance round the WC", either,
                    $"ADA 604.3 asks for {Units.Figure("1525", "60 in")} wide from the side wall by {Units.Figure("1420", "56 in")} deep from the rear wall.");
                break;
            }

            case ComponentForm.Toilet or ComponentForm.WallHungToilet when irc:
                yield return new Zone("the space round the WC", new[] { new Area(-381, 381, -halfD, halfD + 533) },
                    $"IRC R307.1 (P2705.1) asks for 21 in{Units.MetricAside("533")} clear in front of a WC, and 15 in{Units.MetricAside("381")} from its centreline to a wall or anything beside it.");
                break;

            case ComponentForm.Toilet or ComponentForm.WallHungToilet when m43:
                yield return new Zone("the access zone round the WC",
                    WallSides().Select(side => side < 0 ? new Area(-350, 1200, -halfD, -halfD + 2200) : new Area(-1200, 350, -halfD, -halfD + 2200)).ToArray(),
                    "Approved Document M (M4(3)) Diagram 3.11 asks for 2200 clear out from the wall behind a WC, from 350 on its wall side to 1200 on its open side, for a wheelchair user to transfer; a basin may come into it by 200 at most.",
                    BasinMay: 200);
                break;

            case ComponentForm.Toilet or ComponentForm.WallHungToilet:
                yield return new Zone("the access zone in front of the WC",
                    WallSides().Select(side => side < 0 ? new Area(-350, 1000, halfD, halfD + 1100) : new Area(-1000, 350, halfD, halfD + 1100)).ToArray(),
                    "Approved Document M (M4(2)) Diagram 2.5 asks for 1100 clear in front of a WC, from 350 on its wall side to 1000 on its open side; a basin may come into it by 300 at most.",
                    BasinMay: 300);
                break;

            case ComponentForm.Basin when ada:
                yield return new Zone("the clear floor space at the lavatory", new[] { new Area(-380, 380, halfD, halfD + 1220) },
                    $"ADA 606.2 asks for {Units.Figure("760 by 1220", "30 by 48 in")} for a forward approach.");
                break;

            case ComponentForm.Basin when irc:
                yield return new Zone("the space round the basin", new[] { new Area(-381, 381, -halfD, halfD + 533) },
                    $"IRC R307.1 (P2705.1) asks for 21 in{Units.MetricAside("533")} clear in front of a lavatory, and 15 in{Units.MetricAside("381")} from its centreline to a wall or anything beside it.");
                break;

            case ComponentForm.Basin when m43:
                yield return new Zone("the access zone in front of the basin", new[] { new Area(-825, 825, halfD, halfD + 800) },
                    "Approved Document M (M4(3)) Diagram 3.11 asks for 1650 wide by 800 deep clear in front of a basin.");
                break;

            case ComponentForm.Basin:
                yield return new Zone("the access zone in front of the basin", new[] { new Area(-550, 550, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 wide by 700 deep clear in front of a basin.");
                break;

            case ComponentForm.Bath when ada:
                yield return new Zone("the clearance along the bath", new[] { new Area(-halfW, halfW, halfD, halfD + 760) },
                    $"ADA 607.2 asks for {Units.Figure("760", "30 in")} wide along the length of a bathtub.");
                break;

            case ComponentForm.Bath when irc:
                yield return new Zone("the space in front of the bath", new[] { new Area(-halfW, halfW, halfD, halfD + 533) },
                    $"IRC Figure R307.1 asks for 21 in{Units.MetricAside("533")} clear in front of a bathtub.");
                break;

            case ComponentForm.Bath when m43:
                yield return new Zone("the access zone along the bath", new[] { new Area(-halfW, halfW, halfD, halfD + 800) },
                    "Approved Document M (M4(3)) Diagram 3.11 asks for 800 clear along the length of a bath.");
                break;

            case ComponentForm.Bath:
                yield return new Zone("the access zone beside the bath",
                    new[] { new Area(-550, 550, halfD, halfD + 700), new Area(-halfW, -halfW + 1100, halfD, halfD + 700), new Area(halfW - 1100, halfW, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 long by 700 wide clear beside a bath.");
                break;

            case ComponentForm.Shower when ada && type.Width >= 1525:
                yield return new Zone("the clearance at the roll-in shower", new[] { new Area(-halfW, -halfW + 1525, halfD, halfD + 760) },
                    $"ADA 608.2.2 asks for {Units.Figure("760 by 1525", "30 by 60 in")} beside the open face of a roll-in shower.");
                break;

            case ComponentForm.Shower when ada:
                yield return new Zone("the clearance at the shower", new[]
                {
                    new Area(-halfW, -halfW + 1220, halfD, halfD + 915),
                    new Area(halfW - 1220, halfW, halfD, halfD + 915)
                }, $"ADA 608.2.1 asks for {Units.Figure("915 by 1220", "36 by 48 in")} from the control wall of a transfer shower.");
                break;

            case ComponentForm.Shower when irc:
                yield return new Zone("the space in front of the shower", new[] { new Area(-halfW, halfW, halfD, halfD + 610) },
                    $"IRC Figure R307.1 asks for 24 in{Units.MetricAside("610")} clear in front of a shower's opening.");
                break;

            case ComponentForm.Shower when m43:
                yield return new Zone("the turning circle at the shower", new[] { new Area(-750, 750, halfD - 500, halfD + 1000) },
                    "Approved Document M (M4(3)) Diagram 3.11 asks for a 1500 turning circle in the bathroom, which may overlap the shower by 500 at most.",
                    Round: true);
                break;

            case ComponentForm.Shower:
                yield return new Zone("the access zone in front of the shower", new[] { new Area(-550, 550, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 by 700 clear beside a bath or shower.");
                break;

            case ComponentForm.Bed or ComponentForm.BunkBed when ada:
                yield return new Zone("the clear floor space beside the bed", new[]
                {
                    new Area(-halfW - 760, -halfW, -610, 610),
                    new Area(halfW, halfW + 760, -610, 610)
                }, $"ADA 806.2.3 asks for {Units.Figure("760 by 1220", "30 by 48 in")} beside a bed for a parallel approach.");
                break;

            case ComponentForm.Bed or ComponentForm.BunkBed when !irc:
            {
                // Clear of the bedhead's first 600, where bedside furniture may stand.
                var reach = m43 ? 1000.0 : 750;
                var from = Math.Min(-halfD + 600, halfD - 300);
                var (left, right, foot) = (new Area(-halfW - reach, -halfW, from, halfD), new Area(halfW, halfW + reach, from, halfD), new Area(-halfW, halfW, halfD, halfD + reach));
                var mayStand = m43 ? AChair : (Func<ComponentType, bool>?)null;
                var isDouble = type.Form == ComponentForm.Bed && type.Width >= 1200;

                if (isDouble && principal)
                {
                    var rule = m43
                        ? "Approved Document M (M4(3)) 3.35e asks for 1000 clear to both sides and the foot of the principal bedroom's bed, and 1200 by 1200 to turn on both sides of it; furniture may come only into the 600 by its head, a chair anywhere."
                        : "Approved Document M (M4(2)) 2.25b asks for 750 clear to both sides and the foot of the principal bedroom's bed; bedside furniture may stand by its head.";
                    yield return new Zone("the access zone to the left of the bed", new[] { left }, rule, MayStand: mayStand);
                    yield return new Zone("the access zone to the right of the bed", new[] { right }, rule, MayStand: mayStand);
                    yield return new Zone("the access zone at the foot of the bed", new[] { foot }, rule, MayStand: mayStand);

                    if (m43)
                        foreach (var (side, what) in new[] { (-1, "left"), (1, "right") })
                            yield return new Zone($"the turning space to the {what} of the bed",
                                new[] { halfD - 1200, (from + halfD - 1200) / 2, from }
                                    .Select(back => side < 0 ? new Area(-halfW - 1200, -halfW, back, back + 1200) : new Area(halfW, halfW + 1200, back, back + 1200))
                                    .ToArray(), rule, MayStand: mayStand);
                }
                else if (isDouble)
                {
                    var rule = m43
                        ? "Approved Document M (M4(3)) 3.35g asks for 1000 clear to one side and the foot of every other double bed; a chair may stand in it."
                        : "Approved Document M (M4(2)) 2.25c asks for 750 clear to one side and the foot of every double bed but the principal bedroom's.";
                    yield return new Zone("the access zone beside the bed", new[] { left, right }, rule, MayStand: mayStand);
                    yield return new Zone("the access zone at the foot of the bed", new[] { foot }, rule, MayStand: mayStand);
                }
                else
                {
                    yield return new Zone("the access zone beside the bed", new[] { left, right },
                        m43
                            ? "Approved Document M (M4(3)) 3.35h asks for 1000 clear to one side of each single bed; a chair may stand in it."
                            : "Approved Document M (M4(2)) 2.25d asks for 750 clear to one side of each single bed.",
                        MayStand: mayStand);
                }

                break;
            }

            case ComponentForm.Wardrobe or ComponentForm.Drawers when m43:
                yield return new Zone($"the access zone in front of the {(type.Form == ComponentForm.Wardrobe ? "wardrobe" : "drawers")}",
                    new[] { new Area(-halfW, halfW, halfD, halfD + 1000) },
                    "Approved Document M (M4(3)) 3.35 asks for 1000 clear in front of all the furniture in a bedroom; a chair may stand in it.",
                    MayStand: AChair);
                break;

            case ComponentForm.Counter or ComponentForm.Sink or ComponentForm.Cooker or ComponentForm.Dishwasher
                or ComponentForm.WashingMachine or ComponentForm.Fridge or ComponentForm.TumbleDryer or ComponentForm.TallUnit or ComponentForm.OvenHousing when !type.IsHosted:
                yield return standard switch
                {
                    ClearanceStandard.Ada => new Zone("the clearance in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1015) },
                        $"ADA 804.2 asks for {Units.Figure("1015", "40 in")} clear in front of kitchen units, and {Units.Figure("1525", "60 in")} in a U-shaped kitchen."),
                    ClearanceStandard.Irc => new Zone("the work aisle in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1067) },
                        $"The IRC sets none; the NKBA's Kitchen Planning Guideline 6 asks for a work aisle of 42 in{Units.MetricAside("1067")} for one cook, 48 in{Units.MetricAside("1219")} for more."),
                    ClearanceStandard.ApprovedDocumentM3 => new Zone("the clear space in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1500) },
                        "Approved Document M (M4(3)) 3.32b asks for 1500 clear in front of and between all kitchen units and appliances."),
                    _ => new Zone("the clear space in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1200) },
                        "Approved Document M (M4(2)) 2.24b asks for 1200 clear in front of and between all kitchen units and appliances.")
                };
                break;

            case ComponentForm.TurningCircle:
                yield return new Zone("the turning circle", new[] { new Area(-halfW, halfW, -halfD, halfD) },
                    standard.IsUs()
                        ? $"ADA 304.3 asks for a 60 in{Units.MetricAside("1525")} turning space, clear of anything standing in it."
                        : "Approved Document M (M4(3)) asks for a 1500 turning circle in a wheelchair user's living space, bedrooms and bathroom (3.22d, 3.35, 3.43e).",
                    Round: true);
                break;

            case ComponentForm.Table:
            {
                var (reach, rule) = DiningReach(standard);
                yield return new Zone("the room to sit at the front of the table", new[] { new Area(-halfW, halfW, halfD, halfD + reach) }, rule, MayStand: AChair);
                yield return new Zone("the room to sit at the back of the table", new[] { new Area(-halfW, halfW, -halfD - reach, -halfD) }, rule, MayStand: AChair);
                break;
            }

            case ComponentForm.RoundTable:
            {
                var (reach, rule) = DiningReach(standard);
                yield return new Zone("the room to sit round the table", new[] { new Area(-halfW - reach, halfW + reach, -halfD - reach, halfD + reach) }, rule,
                    Round: true, MayStand: AChair);
                break;
            }
        }
    }

    /// <summary>
    /// How far from a dining table's edge to a wall or anything standing, for a chair to be drawn
    /// out and sat on: the NKBA's 32 in - 800 - or, for a wheelchair user, its access standard's 36 in.
    /// </summary>
    private static (double Reach, string Rule) DiningReach(ClearanceStandard standard) => standard is ClearanceStandard.Ada or ClearanceStandard.ApprovedDocumentM3
        ? (915, $"The NKBA's access standard asks for 36 in{Units.MetricAside("915")} from a table's edge to a wall or anything standing where no one passes behind - 60 in{Units.MetricAside("1525")} where a wheelchair passes.")
        : (800, $"The NKBA's Kitchen Planning Guideline 8 asks for 32 in{Units.MetricAside("800")} from a table's edge to a wall or anything standing, to draw a chair out and sit - 36 in to edge past someone seated, 44 in to walk past.");

    /// <summary>Whether a fitting of this type needs room kept clear by it.</summary>
    public static bool Needs(ComponentType type, ClearanceStandard standard) => ZonesOf(type, standard).Any();

    /// <summary>
    /// The double bed taken as the principal bedroom's, where a project has double beds: the
    /// widest - a king where the others are doubles, as Approved Document M's furniture schedule
    /// sizes the principal one - then the one in the biggest room, then the first put down.
    /// </summary>
    public static Component? PrincipalBed(BimDocument document) =>
        document.All<Component>()
            .Select((piece, order) => (Piece: piece, Order: order, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is { Form: ComponentForm.Bed, Width: >= 1200 })
            .OrderByDescending(entry => Math.Round(entry.Type!.Width))
            .ThenByDescending(entry => RoomsOf.Containing(document, entry.Piece.LevelId, entry.Piece.Location) is { } room ? room.GetBoundary(document).Area : 0)
            .ThenBy(entry => entry.Order)
            .Select(entry => entry.Piece)
            .FirstOrDefault();

    /// <summary>
    /// A fitting's zones where it stands - for one that may have its zone either side, the side
    /// with least in the way - with what is in each. None for one fixed up off the floor.
    /// </summary>
    public static IReadOnlyList<(ClearanceZone Zone, IReadOnlyList<string> In)> Of(BimDocument document, Component component, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(component.TypeId) is not { } type) return Array.Empty<(ClearanceZone, IReadOnlyList<string>)>();
        if (OffTheFloor(document, component, type)) return Array.Empty<(ClearanceZone, IReadOnlyList<string>)>();

        var frame = ComponentModel.FrameOf(component, 0);
        var obstacles = Obstacles(document, component);

        // How far the face of a wall beside it is, either side, within a metre.
        var walls = obstacles.Where(obstacle => obstacle.Type is null).Select(obstacle => obstacle.Outline).ToList();
        double? SideWall(int side)
        {
            bool InWall(double distance) => walls.Any(wall => Polygon2D.Contains(wall, frame.Plan(side * distance, 0)));

            for (var distance = 0.0; distance <= 1000; distance += 5)
            {
                if (!InWall(distance)) continue;

                // Found within 5: closed in on to its face.
                var (clear, inside) = (Math.Max(0, distance - 5), distance);
                while (inside - clear > 0.05)
                {
                    var middle = (clear + inside) / 2;
                    if (InWall(middle)) inside = middle;
                    else clear = middle;
                }

                return inside;
            }

            return null;
        }

        var principal = type.Form == ComponentForm.Bed && PrincipalBed(document) == component;
        var zones = new List<(ClearanceZone, IReadOnlyList<string>)>();
        foreach (var zone in ZonesOf(type, standard, SideWall, principal))
        {
            IReadOnlyList<Point2D> Shape(Area area) => zone.Round
                ? frame.Ellipse(area.From, area.To, area.Back, area.Front, 32)
                : frame.Outline(area.From, area.To, area.Back, area.Front);

            // A basin may come into a WC's zone so far - a hand-rinse basin, 300 deep or less, by
            // 200 at most: it is in the way only past that.
            IReadOnlyList<Point2D>? Inset(Area area, double by) =>
                by > 0 && area.To - area.From > 2 * by && area.Front - area.Back > 2 * by
                    ? Shape(new Area(area.From + by, area.To - by, area.Back + by, area.Front - by))
                    : null;

            Func<ComponentType, IReadOnlyList<Point2D>?>? ForBasins(Area area) =>
                zone.BasinMay <= 0 ? null : basin => Inset(area, basin.Depth <= 300 ? Math.Min(zone.BasinMay, 200) : zone.BasinMay);

            var best = zone.Either
                .Select(area => (Outline: Shape(area), In: InTheWay(Shape(area), obstacles, zone.MayStand, ForBasins(area))))
                .OrderBy(candidate => candidate.In.Count)
                .First();
            zones.Add((new ClearanceZone(component, zone.What, best.Outline, zone.Rule), best.In));
        }

        return zones;
    }

    /// <summary>Every zone on a storey that something is standing in, and what - the fittings', then the doors'.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(component => component.LevelId == levelId)
            .SelectMany(component => Of(document, component, standard)
                .Where(zone => zone.In.Count > 0)
                .Select(zone => new BlockedClearance(zone.Zone, string.Join(", ", zone.In), document.FindType<ComponentType>(component.TypeId)?.Name ?? "A fitting")))
            .Concat(document.All<Door>()
                .Where(door => document.Find<Wall>(door.HostWallId)?.LevelId == levelId)
                .SelectMany(door => Swings(document, door)
                    .Where(zone => zone.In.Count > 0)
                    .Select(zone => new BlockedClearance(zone.Zone, string.Join(", ", zone.In), DoorName(document, door)))))
            .Concat(Overlaps(document, levelId))
            .Concat(Kitchens.Blocked(document, levelId, standard))
            .Concat(Bathrooms.Blocked(document, levelId, standard))
            .Concat(Services.Blocked(document, levelId, standard))
            .Concat(Alarms.Blocked(document, levelId, standard))
            .Concat(Outside.Blocked(document, levelId, standard))
            .Concat(Doors.Blocked(document, levelId, standard))
            .Concat(WindowRules.Blocked(document, levelId, standard))
            .Concat(WallRules.Blocked(document, levelId, standard))
            .Concat(SlabRules.Blocked(document, levelId, standard))
            .Concat(RoofEdgeRules.Blocked(document, levelId, standard))
            .Concat(ChimneyRules.Blocked(document, levelId, standard))
            .Concat(StructureRules.Blocked(document, levelId, standard))
            .ToList();

    /// <summary>
    /// Pieces standing in one another's place on a storey: a sofa pushed into an armchair, a
    /// wardrobe through a bed - each pair once, on the one put down later. Not a chair tucked
    /// under a table, a desk or a counter, which is where it goes; not trees' canopies, which
    /// overlap as they grow; not people.
    /// </summary>
    public static IReadOnlyList<BlockedClearance> Overlaps(BimDocument document, Guid levelId)
    {
        var pieces = document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null && entry.Type.Kind is not (BuiltInCategory.Planting or BuiltInCategory.Entourage) || entry.Type?.Form == ComponentForm.Car)
            .Select(entry =>
            {
                var bottom = ComponentHosting.BaseElevation(document, entry.Piece, entry.Type!);
                return (entry.Piece, Type: entry.Type!, Bottom: bottom, Top: bottom + entry.Type!.Height,
                    Outline: ComponentModel.Footprint(entry.Type!, ComponentModel.FrameOf(entry.Piece, 0)));
            })
            .ToList();

        static bool TuckedUnder(ComponentType a, ComponentType b) =>
            a.Form == ComponentForm.Chair && b.Form is ComponentForm.Table or ComponentForm.Desk or ComponentForm.Counter;

        var overlaps = new List<BlockedClearance>();
        for (var i = 1; i < pieces.Count; i++)
        for (var j = 0; j < i; j++)
        {
            var (later, earlier) = (pieces[i], pieces[j]);
            if (TuckedUnder(later.Type, earlier.Type) || TuckedUnder(earlier.Type, later.Type)) continue;
            if (Math.Min(later.Top, earlier.Top) - Math.Max(later.Bottom, earlier.Bottom) <= 20) continue;
            if (PolygonBoolean.Combine(later.Outline, earlier.Outline, BooleanOperation.Intersection).Sum(region => region.Area) <= Touching) continue;

            // Their boxes meet: whether what they are built of does - a coffee table in the
            // crook of a corner sofa's L is inside its box, but clear of the sofa.
            if (!PartsMeet(later.Piece, later.Type, later.Bottom, earlier.Piece, earlier.Type, earlier.Bottom)) continue;

            overlaps.Add(new BlockedClearance(
                new ClearanceZone(later.Piece, "the room it takes", later.Outline, "Two pieces cannot be in the same place: move one of them."),
                earlier.Type.Name, later.Type.Name));
        }

        return overlaps;
    }

    /// <summary>
    /// Whether two pieces' parts take the same room - in plan more than a sliver of it, and
    /// over more than 20 of height; a handle, a tap or a waste is too small to count.
    /// </summary>
    private static bool PartsMeet(Component a, ComponentType aType, double aBottom, Component b, ComponentType bType, double bBottom)
    {
        static List<(IReadOnlyList<Point2D> Outline, double Bottom, double Top, Rect2 Box)> Solid(Component piece, ComponentType type, double bottom)
        {
            var frame = ComponentModel.FrameOf(piece, bottom);
            var parts = ComponentModel.Parts(type).Where(part => (part.To - part.From) * (part.Front - part.Back) >= 50 * 50).ToList();
            if (parts.Count == 0) parts.Add(new ComponentModel.Part(-type.Width / 2, type.Width / 2, -type.Depth / 2, type.Depth / 2, 0, type.Height));

            return parts.Select(part =>
            {
                var outline = frame.Outline(part.From, part.To, part.Back, part.Front);
                return (outline, bottom + part.Bottom, bottom + part.Top,
                    new Rect2(outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X), outline.Max(p => p.Y)));
            }).ToList();
        }

        var those = Solid(b, bType, bBottom);
        foreach (var mine in Solid(a, aType, aBottom))
        foreach (var theirs in those)
        {
            if (Math.Min(mine.Top, theirs.Top) - Math.Max(mine.Bottom, theirs.Bottom) <= 20) continue;
            if (mine.Box.MinX >= theirs.Box.MaxX || theirs.Box.MinX >= mine.Box.MaxX || mine.Box.MinY >= theirs.Box.MaxY || theirs.Box.MinY >= mine.Box.MaxY) continue;
            if (PolygonBoolean.Combine(mine.Outline, theirs.Outline, BooleanOperation.Intersection).Sum(region => region.Area) > Touching / 4) return true;
        }

        return false;
    }

    /// <summary>A box in plan, to tell quickly that two parts are nowhere near each other.</summary>
    private readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>What a door is called in a warning: its type, and its mark where it has one - "Single - 900 x 2100 (D3)".</summary>
    private static string DoorName(BimDocument document, Door door)
    {
        var name = document.FindType<DoorType>(door.TypeId)?.Name ?? "A door";
        return string.IsNullOrWhiteSpace(door.Mark) ? name : $"{name} ({door.Mark})";
    }

    /// <summary>
    /// The floor a door's leaves sweep, with whatever stands on it: a wardrobe, a bed, a sofa put
    /// where the door opens over it - or another door's swing meeting its own. A door has to open
    /// fully - to the width it was made for, and to get out through it - so nothing may stand there.
    /// </summary>
    public static IReadOnlyList<(ClearanceZone Zone, IReadOnlyList<string> In)> Swings(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall) return Array.Empty<(ClearanceZone, IReadOnlyList<string>)>();

        var floor = document.FindLevel(wall.LevelId)?.Elevation ?? 0;
        var standing = new List<Obstacle>();
        foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == wall.LevelId))
        {
            if (document.FindType<ComponentType>(piece.TypeId) is not { } type) continue;

            // What the leaf would hit: what stands on the floor up to where a leaf swings, not
            // a light or a shelf over it, nor a rug's worth of a shower tray let into the floor.
            var bottom = ComponentHosting.BaseElevation(document, piece, type) - floor;
            if (bottom > Underneath || bottom + type.Height < 30) continue;
            standing.Add(new Obstacle(type.Name, ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0)), type));
        }

        // Another door whose leaf sweeps the same floor: the two meet, opening - said of the one put in later.
        foreach (var other in document.All<Door>().TakeWhile(other => other != door))
        {
            if (document.Find<Wall>(other.HostWallId)?.LevelId != wall.LevelId) continue;
            foreach (var sweep in DoorSwing.Sweeps(document, other))
                standing.Add(new Obstacle(string.IsNullOrWhiteSpace(other.Mark) ? "another door's swing" : $"door {other.Mark}'s swing", sweep, null));
        }

        return DoorSwing.Sweeps(document, door)
            .Select(sweep => (new ClearanceZone(door, "its swing", sweep,
                "A door has to open fully, to give its full width and a way out: nothing may stand where its leaf sweeps."), InTheWay(sweep, standing)))
            .ToList();
    }

    /// <summary>Every blocked zone in the project, storey by storey.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, ClearanceStandard standard) =>
        document.Levels.SelectMany(level => Blocked(document, level.Id, standard)).ToList();

    private static bool OffTheFloor(BimDocument document, Component component, ComponentType type) =>
        ComponentHosting.BaseElevation(document, component, type) - (document.FindLevel(component.LevelId)?.Elevation ?? 0) > Underneath;

    /// <summary>Something that could be in a zone's way: what it is called, its outline, and what it is - none for a wall.</summary>
    private sealed record Obstacle(string Name, IReadOnlyList<Point2D> Outline, ComponentType? Type);

    /// <summary>
    /// What could be in a fitting's way, by name and outline: the walls standing on its storey,
    /// and whatever stands on the floor there - not itself, not what is fixed high on a wall,
    /// and for a piece of a kitchen run, not the rest of its run.
    /// </summary>
    private static List<Obstacle> Obstacles(BimDocument document, Component component)
    {
        var obstacles = new List<Obstacle>();
        var floor = document.FindLevel(component.LevelId)?.Elevation ?? 0;

        foreach (var wall in document.Walls.Where(wall => wall.LevelId == component.LevelId || wall.GetBaseElevation(document) <= floor + 1 && wall.GetTopElevation(document) > floor + 1))
        {
            if (document.GetWallType(wall) is not { } wallType || wallType.Structure.TotalWidth <= 0 || wall.Length < 1) continue;
            if (wall.GetBaseElevation(document) > floor + Underneath || wall.GetTopElevation(document) <= floor + 1) continue;

            var half = wallType.Structure.TotalWidth / 2;
            obstacles.Add(new Obstacle("the wall", CurtainGeometry.Band(wall, wallType, 0, wall.Length, half, -half), null));
        }

        var run = document.FindType<ComponentType>(component.TypeId) is { } own && ComponentPlacing.InRun(own)
            ? RunOf(document, component)
            : new HashSet<Component> { component };

        foreach (var other in document.All<Component>().Where(other => other.LevelId == component.LevelId && !run.Contains(other)))
        {
            if (document.FindType<ComponentType>(other.TypeId) is not { } type || OffTheFloor(document, other, type)) continue;

            // A level-access shower's former is the floor: it is rolled over, not in the way. Nor
            // are the people put in for scale.
            if (type.Height < 30 || type.Kind == BuiltInCategory.Entourage && type.Form != ComponentForm.Car) continue;
            obstacles.Add(new Obstacle(type.Name, ComponentModel.Footprint(type, ComponentModel.FrameOf(other, 0)), type));
        }

        return obstacles;
    }

    /// <summary>A kitchen piece and every piece of the run it is in: those touching it, and those touching them.</summary>
    private static HashSet<Component> RunOf(BimDocument document, Component component)
    {
        var pieces = document.All<Component>()
            .Where(other => other.LevelId == component.LevelId)
            .Select(other => (Component: other, Type: document.FindType<ComponentType>(other.TypeId)))
            .Where(other => other.Type is not null && ComponentPlacing.InRun(other.Type) && !OffTheFloor(document, other.Component, other.Type))
            .Select(other => (other.Component, Outline: ComponentModel.Footprint(other.Type!, ComponentModel.FrameOf(other.Component, 0))))
            .ToList();

        var run = new HashSet<Component> { component };
        var reached = new Queue<IReadOnlyList<Point2D>>();
        if (pieces.FirstOrDefault(piece => piece.Component == component) is { Outline: { } start }) reached.Enqueue(start);

        while (reached.Count > 0)
        {
            var from = reached.Dequeue();
            foreach (var (piece, outline) in pieces.Where(piece => !run.Contains(piece.Component)))
            {
                if (!Touch(from, outline)) continue;
                run.Add(piece);
                reached.Enqueue(outline);
            }
        }

        return run;
    }

    /// <summary>Whether two outlines meet or overlap - within 5, as units closed up into a run do.</summary>
    private static bool Touch(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        a.Any(point => Polygon2D.Contains(b, point) || DistanceToEdge(b, point) <= 5)
        || b.Any(point => Polygon2D.Contains(a, point) || DistanceToEdge(a, point) <= 5);

    private static double DistanceToEdge(IReadOnlyList<Point2D> polygon, Point2D point) =>
        Enumerable.Range(0, polygon.Count).Min(i => Line2D.DistanceFromSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]));

    /// <summary>
    /// What takes up more than a sliver of a zone - but for what the rule lets stand there, and a
    /// basin only where it comes further in than it may: past the edge <paramref name="forBasins"/> gives it.
    /// </summary>
    private static IReadOnlyList<string> InTheWay(IReadOnlyList<Point2D> zone, List<Obstacle> obstacles, Func<ComponentType, bool>? mayStand = null, Func<ComponentType, IReadOnlyList<Point2D>?>? forBasins = null) =>
        obstacles
            .Where(obstacle => obstacle.Type is null || mayStand?.Invoke(obstacle.Type) != true)
            .Where(obstacle =>
            {
                var against = obstacle.Type is { Form: ComponentForm.Basin } basin && forBasins?.Invoke(basin) is { } inset ? inset : zone;
                return PolygonBoolean.Combine(against, obstacle.Outline, BooleanOperation.Intersection).Sum(region => region.Area) > Touching;
            })
            .Select(obstacle => obstacle.Name)
            .Distinct()
            .ToList();
}

/// <summary>
/// One thing wrong with where a fitting, a door or a window is, by the rules for it: what, the
/// rule asking otherwise, and where - to be drawn red over the plan.
/// </summary>
public sealed record PlacementProblem(Element Piece, string Said, string Rule, IReadOnlyList<Point2D> Outline);
