using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a building other than a dwelling offers in its rooms (round 108), as Approved Document M Volume 2 sets it out in Sections 3 and 4.
///
/// Audience seating (4.12, Table 3): a room with fixed audience seats is asked for wheelchair spaces by its capacity - up to 600 seats 1% of
/// them permanent, rounded up, and as many more removable as make 6; over 600, 1% permanent; 10,000 or more by the accessible stadia guide, not
/// judged (c). Some of them in pairs with a seat beside them (d), each 900 by 1400 (g), and a hearing enhancement system in the room (m, 4.36b).
/// A room for meetings, lectures, classes, performances or films likewise asked for a hearing enhancement system (4.36b).
///
/// Counters (3.6, 4.16): a reception desk or counter - one not in a bar, café, restaurant or canteen - has a section 1500 wide at least, its
/// surface 760 high at most, with a knee recess (3.6e), a hearing enhancement system (3.6f), and 1200 by 1800 clear in front of it with a knee
/// recess 500 deep, 1400 by 2200 without (3.6d, on the plan's clearances); a bar or serving counter part of its surface 850 high at most (4.16b).
///
/// Not done: the routes to wheelchair spaces, 900 clear (4.12a, f), their spread for a range of views (e), level floors (h), seats' arms and
/// assistance dogs (i, j), stepped terraces (k), stages (l); reception points' place and floor (3.6a to c, g); shared tea points' worktops (4.16c);
/// public address, signs and telephones (4.36a, c to f).
/// </summary>
public static class Facilities
{
    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A room for meetings, lectures, classes, performances or films, which 4.36b asks a hearing enhancement system of.</summary>
    private static bool Heard(Room room) =>
        Named(room, "meeting", "conference", "lecture", "seminar", "classroom", "class room", "boardroom", "board room", "training", "auditorium", "theatre", "cinema",
            "council chamber", "assembly hall", "concert");

    /// <summary>A bar, café, restaurant or canteen: its counters serving counters (4.16), not reception counters (3.6).</summary>
    internal static bool Serving(Room room) => Named(room, "bar", "cafe", "café", "restaurant", "canteen", "servery", "dining", "refectory", "pub", "bistro", "coffee");

    /// <summary>The components in a room, by their types.</summary>
    private static List<(Component Piece, ComponentType Type)> In(BimDocument document, Room room, IReadOnlyList<Point2D> outline) =>
        document.All<Component>().Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location))
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null).Select(entry => (entry.Piece, entry.Type!)).ToList();

    /// <summary>Whether a wheelchair space is made by taking out seats: its type's name says removable.</summary>
    public static bool Removable(ComponentType type) => type.Name.Contains("removable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Table 3's wheelchair spaces for a seating capacity: permanent, and in all with removable ones; null at 10,000 or more.</summary>
    public static (int Permanent, int Total)? Asked(int seats)
    {
        if (seats >= 10000) return null;
        var permanent = (int)Math.Ceiling(seats / 100.0);
        return (permanent, seats <= 600 ? Math.Max(6, permanent) : permanent);
    }

    /// <summary>
    /// What a room of a building other than a dwelling offers - "Audience seating: 120 seats, 6 wheelchair spaces, 2 permanent", "Reception
    /// counter: ..." - with what Approved Document M Volume 2 asks of it that it falls short of; null for a room with none of these.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        if (!Circulation.Applies(document) || room.GetBoundary(document) is not { IsEnclosed: true } boundary) return null;
        var pieces = In(document, room, boundary.Polygon);
        var seats = pieces.Where(each => each.Type.Form == ComponentForm.AudienceSeat).ToList();
        var spaces = pieces.Where(each => each.Type.Form == ComponentForm.WheelchairSpace).ToList();
        var counters = pieces.Where(each => each.Type.Form == ComponentForm.ServiceCounter).ToList();
        var loop = pieces.Any(each => each.Type.Form == ComponentForm.HearingLoop);
        var said = new List<string>();
        var wanting = new List<string>();

        if (seats.Count > 0 || spaces.Count > 0)
        {
            var permanent = spaces.Count(each => !Removable(each.Type));
            said.Add($"Audience seating: {seats.Count} seat{(seats.Count == 1 ? string.Empty : "s")}, {spaces.Count} wheelchair space{(spaces.Count == 1 ? string.Empty : "s")}{(spaces.Count > 0 ? $", {permanent} permanent" : string.Empty)}");
            if (Asked(seats.Count) is var (permanentAsked, totalAsked))
            {
                if (permanent < permanentAsked || spaces.Count < totalAsked)
                    wanting.Add($"{spaces.Count} wheelchair spaces, {permanent} permanent, for {seats.Count} seats - Approved Document M Volume 2 (4.12c, Table 3) asks {permanentAsked} permanent, 1% of the seats rounded up{(seats.Count <= 600 ? $", and {totalAsked} in all with removable ones" : string.Empty)}");
            }

            foreach (var (piece, type) in spaces.Where(each => each.Type.Width < 900 - 0.5 || each.Type.Depth < 1400 - 0.5).Take(1))
                wanting.Add($"its wheelchair space {type.Name} is {type.Width:0} by {type.Depth:0} - Approved Document M Volume 2 (4.12g) asks 900 wide and 1400 deep for an occupied wheelchair parked");

            // Some in pairs, a seat beside them (4.12d, Diagram 13).
            if (spaces.Count >= 2)
            {
                bool Beside(Component a, Component b) => a != b && a.Location.DistanceTo(b.Location) <= 1100;
                var paired = spaces.Any(one => spaces.Any(other => Beside(one.Piece, other.Piece))
                                               && seats.Any(seat => seat.Piece.Location.DistanceTo(one.Piece.Location) <= 1100 || spaces.Any(other => Beside(one.Piece, other.Piece) && seat.Piece.Location.DistanceTo(other.Piece.Location) <= 1100)));
                if (!paired)
                    wanting.Add("no two wheelchair spaces side by side with a seat beside them - Approved Document M Volume 2 (4.12d, Diagram 13) asks some in pairs with standard seating on one side at least");
            }
        }

        if (counters.Count > 0)
        {
            var serving = Serving(room);
            var low = counters.Where(each => each.Type.Height <= (serving ? 850 : 760) + 0.5).ToList();
            said.Add($"{(serving ? "Bar or serving counter" : "Reception counter")}: {counters.Count} section{(counters.Count == 1 ? string.Empty : "s")}{(low.Count > 0 ? $", the lowest {low.Min(each => each.Type.Height):0} high" : string.Empty)}");
            if (serving && low.Count == 0)
                wanting.Add($"no part of its counter 850 high or lower, the lowest {counters.Min(each => each.Type.Height):0} - Approved Document M Volume 2 (4.16b) asks part of a bar's or serving counter's surface permanently accessible to wheelchair users, 850 above the floor at most");
            if (!serving && !low.Any(each => each.Type.Width >= 1500 - 0.5 && each.Type.KneeRecess > 0))
                wanting.Add("no section of its counter 1500 wide at least, 760 high at most, with a knee recess under it - Approved Document M Volume 2 (3.6e) asks one, for wheelchair users and seated visitors");
            if (!serving && !loop)
                wanting.Add("no hearing enhancement system at it - Approved Document M Volume 2 (3.6f) asks one at any reception point, an induction loop or the like");
        }

        if (!loop && (seats.Count > 0 || Heard(room)) && !(counters.Count > 0 && !Serving(room)))
        {
            if (said.Count == 0) said.Add($"{(Named(room, "meeting", "conference", "boardroom", "board room") ? "Meeting room" : "Room for lectures, classes or performances")}");
            wanting.Add("no hearing enhancement system in it - Approved Document M Volume 2 (4.12m, 4.36b) asks one in rooms for meetings, lectures, classes, performances, spectator sport or films: an induction loop, infrared or radio system");
        }

        if (said.Count == 0) return null;
        var head = string.Join("; ", said);
        return wanting.Count == 0 ? head : $"{head}; {string.Join("; ", wanting)}";
    }
}
