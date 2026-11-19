using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Sleeping accommodation in a building other than a dwelling (round 104) - a hotel's, a motel's, a hall of residence's - as Approved Document M
/// Volume 2 sets it out (4.17 to 4.24, Table 2, Diagram 17): a bedroom is a room with a bed in it, not an en-suite; a wheelchair-accessible one
/// is named accessible or wheelchair.
///
/// Every bedroom's door from the corridor gives the effective clear width Table 2 asks for an approach at right angles to the corridor - 800
/// where the corridor is 1500 wide or more, 825 where it is narrower (4.24a). One bedroom in 20, or part of 20, wheelchair-accessible (4.24g).
/// A wheelchair-accessible bedroom: 300 clear on its corridor door's pull side between its leading edge and a return wall (4.24j, 3.10c); its
/// en-suite's door 800 clear, straight on (4.24k); a 1500 by 1500 turning space and 1500 beside a bed to transfer to it (4.24l, Diagram 17 - the
/// bed's on the plan's clearances); an emergency assistance alarm (4.24q); and its en-suite checked as the shower room or bathroom it is (4.24m,
/// 5.15 to 5.21), at least as many shower rooms as bathrooms among them (4.19).
///
/// Not checked: wardrobes' doors (4.24b), handles (c), windows' controls (d), visual fire alarms (e), embossed numbers (f), routes and choice of
/// location (h, i), door viewers (n), balconies (o, p) and the call signal (r).
/// </summary>
public static class Lodging
{
    /// <summary>
    /// Whether two rooms are within one room for residential purposes - a hotel's bedroom and its own en-suite - where Approved Document E's E2
    /// reaches the wall between them; the walls between such rooms and to the corridor are E1's separating walls (Section 6, Table 0.1b), not
    /// worked out here.
    /// </summary>
    public static bool WithinRoom(BimDocument document, Room one, Room other)
    {
        var plan = Toilets.Plan(document);
        if (!plan.TryGetValue(one, out var a) || !plan.TryGetValue(other, out var b)) return false;
        bool Suite(Toilets.Fitted suite, Room bedroom) => Toilets.EnSuite(plan, suite) && suite.Doors.Any(door => door.Other == bedroom);
        return Suite(a, other) || Suite(b, one);
    }

    /// <summary>A door's least effective clear width at right angles to an access route, by its width (Table 2, new buildings).</summary>
    public static double Asked(double corridor) => corridor >= 1500 - 0.5 ? 800 : 825;

    /// <summary>Whether a room is a bedroom of a building other than a dwelling: a bed in it, no WC, bath or shower, Approved Document T applying.</summary>
    internal static bool IsBedroom(Toilets.Fitted room) =>
        room.Pieces.Any(each => each.Type.Form is ComponentForm.Bed or ComponentForm.BunkBed) && room.Wcs == 0 && room.Showers == 0 && room.Baths == 0;

    /// <summary>
    /// How much clear space there is on a door's pull side between its leading edge - its latch jamb - and the return wall beyond it, mm: from the
    /// jamb, along the wall away from its hinge, to the room's outline on the side it opens into. Null where it cannot be told.
    /// </summary>
    internal static double? LeadingEdge(BimDocument document, IReadOnlyDictionary<Room, Toilets.Fitted> plan, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType || document.FindType<DoorType>(door.TypeId) is not { } type) return null;
        var sweep = DoorSwing.Sweeps(document, door).FirstOrDefault();
        if (sweep is null || sweep.Count < 3) return null;
        var (from, to) = door.GetSpan(type);
        var (a, b) = (wall.LocationCurve.PointAt(from), wall.LocationCurve.PointAt(to));

        // The hinge: the jamb the whole sweep lies within a leaf's width of; the leading edge the other.
        double Reach(Point2D jamb) => sweep.Max(point => point.DistanceTo(jamb));
        var (hinge, latch) = Reach(a) <= Reach(b) ? (a, b) : (b, a);
        var middle = new Point2D(sweep.Average(point => point.X), sweep.Average(point => point.Y));
        var room = plan.FirstOrDefault(entry => entry.Key.LevelId == wall.LevelId && Polygon2D.Contains(entry.Value.Outline, middle)).Value;
        if (room is null) return null;
        var along = (latch - hinge).NormalisedOrDefault(Vector2D.UnitX);
        var pull = along.PerpendicularLeft();
        if (pull.Dot(middle - latch) < 0) pull = -pull;
        return Toilets.Ray(room.Outline, latch + pull * (wallType.Structure.TotalWidth / 2 + 20), along);
    }

    /// <summary>
    /// What a bedroom of a building other than a dwelling comes to - "Wheelchair bedroom: a bed, 4.38 x 6.34 m" - with what Approved Document M
    /// Volume 2 asks of it that it falls short of; null for any other room, or a dwelling's.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var plan = Toilets.Plan(document);
        if (!plan.TryGetValue(room, out var fitted) || !IsBedroom(fitted)) return null;
        var beds = fitted.Pieces.Count(each => each.Type.Form is ComponentForm.Bed or ComponentForm.BunkBed);
        var (narrow, deep) = Toilets.Size(fitted.Outline);
        var wanting = new List<string>();

        // Its doors: to its own en-suite, and to the corridor or anywhere else.
        foreach (var (door, other) in fitted.Doors)
        {
            if (document.FindType<DoorType>(door.TypeId) is not { } type) continue;
            var clear = Doors.ClearWidth(type);
            var ensuite = other is not null && plan.TryGetValue(other, out var beyond) && Toilets.EnSuite(plan, beyond);
            if (ensuite)
            {
                if (fitted.Accessible && clear < 800 - 0.5)
                    wanting.Add($"its door to the {other!.Name} gives {clear:0} clear - Approved Document M Volume 2 (4.24k, Table 2) asks 800 at least of the door to a wheelchair-accessible bedroom's en-suite");
                continue;
            }

            var corridor = other is not null && plan.TryGetValue(other, out var outside) ? Toilets.Size(outside.Outline).Short : 1500;
            var asked = Asked(corridor);
            if (clear < asked - 0.5)
                wanting.Add($"its door from the {other?.Name ?? "outside"} gives {clear:0} clear - Approved Document M Volume 2 (4.24a, Table 2) asks {asked:0} at least off an access route {(asked > 800 ? "under 1500" : "1500 or more")} wide");
            if (fitted.Accessible && LeadingEdge(document, plan, door) is { } edge && edge < 300 - 0.5)
                wanting.Add($"{edge:0} between its door's leading edge and the wall beside it - Approved Document M Volume 2 (4.24j, 3.10c) asks 300 at least on the pull side of a wheelchair-accessible bedroom's door");
        }

        if (fitted.Accessible)
        {
            if (!Toilets.Turns(document, fitted))
                wanting.Add("no 1500 x 1500 turning space in it clear of the bed, the furniture and the doors' swings - Approved Document M Volume 2 (4.24l, Diagram 17) asks one, with 1500 beside the bed to transfer to it");
            if (!fitted.Pieces.Any(each => each.Type.Form == ComponentForm.AssistanceAlarm))
                wanting.Add("no emergency assistance alarm - Approved Document M Volume 2 (4.24q) asks one with its reset, its pull cord reachable from the bed and the floor beside it");
        }

        // The building's: one wheelchair-accessible bedroom for every 20 or part (4.24g), said on its first bedroom.
        var bedrooms = plan.Values.Where(IsBedroom).OrderBy(each => document.FindLevel(each.Room.LevelId)?.Elevation ?? 0).ThenBy(each => each.Room.Number, StringComparer.Ordinal).ToList();
        var accessible = bedrooms.Count(each => each.Accessible);
        var needed = (int)Math.Ceiling(bedrooms.Count / 20.0);
        if (accessible < needed && bedrooms[0] == fitted)
            wanting.Add($"{accessible} of its {bedrooms.Count} bedrooms wheelchair-accessible - Approved Document M Volume 2 (4.24g) asks one for every 20 bedrooms or part of 20, {needed} - name each accessible");

        // Its wheelchair-accessible bedrooms' en-suites: as many shower rooms as bathrooms at least (4.19), said on the first such bedroom.
        var suites = plan.Values.Where(each => Toilets.EnSuite(plan, each) && each.Doors.Any(door => door.Other is { } owner && plan.TryGetValue(owner, out var bedroom) && bedroom.Accessible)).ToList();
        if (suites.Count(each => each.Baths > 0) > suites.Count(each => each.Showers > 0 && each.Baths == 0) && bedrooms.FirstOrDefault(each => each.Accessible) == fitted)
            wanting.Add($"its wheelchair-accessible bedrooms have {suites.Count(each => each.Baths > 0)} en-suite bathrooms and {suites.Count(each => each.Showers > 0 && each.Baths == 0)} shower rooms - Approved Document M Volume 2 (4.19) asks at least as many shower rooms as bathrooms, unless there are compelling reasons");

        var said = $"{(fitted.Accessible ? "Wheelchair bedroom" : "Bedroom")}: {(beds == 1 ? "a bed" : $"{beds} beds")}, {narrow / 1000:0.00} x {deep / 1000:0.00} m";
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }
}
