using System.Text.RegularExpressions;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Toilets in buildings other than dwellings (round 100), as Approved Document T (2024) sets them out for requirement T1 in England.
///
/// Each room with a WC in it is read as one of its four types (1.9) by what is in it, where its doors open and its name. A fully enclosed room
/// with one WC and its own washbasin is self-contained: a universal toilet - Type B, or Type A where its name says it is ambulant - or, named for
/// men or women, a self-contained single-sex toilet laid out as one (1.11, 3.2, 4.2). A room with one WC and no basin whose doors open only into
/// rooms named for one sex is a single-sex cubicle - Type D, or Type C ambulant. A room named accessible, disabled or wheelchair is the
/// wheelchair-accessible unisex toilet Approved Document M Volume 2 sets out, not checked here.
///
/// What is checked: single-sex toilets for both men and women, or universal toilets alone - taken as where space precludes single-sex ones
/// (T1(1)); a wheelchair-accessible unisex toilet in the building (1.14); each type's least overall size and its door's least effective clear
/// width (Tables 3.1, 4.1, 5.1, 6.1); a self-contained toilet's washbasin and hand-drying within it (T1(2), Tables 3.1, 4.1); a universal toilet
/// opening onto a corridor or circulation space (1.18, 2.8), not through single-sex toilets (1.21), with no shared hand-washing outside it (1.16);
/// a cubicle's door opening only into single-sex toilet accommodation (1.22) with hand-washing in it (Tables 5.1, 6.1); WCs each in a cubicle or
/// room of their own (2.3, Sections 5 and 6); urinals only in men's toilets (2.2).
///
/// Not under the US's codes; not in a house or flats, a school, a nursery or a prison (1.2), nor an en-suite reached only from a bedroom. Not
/// checked: grab rails, hooks, shelves, mirrors, bins and the spaces in front of the fittings (Diagrams 3.1 to 6.1, 2.3), doors' bolts, closers
/// and opening force (2.4 to 2.7), signs (2.9, 2.10), controls, contrast and lighting (2.11 to 2.13), and how many toilets there are (1.6).
/// </summary>
public static partial class Toilets
{
    public enum Sex
    {
        None,
        Male,
        Female
    }

    /// <summary>A type's least overall width and depth and its door's least effective clear width, mm (Tables 3.1, 4.1, 5.1, 6.1).</summary>
    public static (double Width, double Depth, double Door, string Table) Least(char type) => type switch
    {
        'A' => (985, 2125, 750, "Table 3.1"),
        'B' => (1050, 1685, 650, "Table 4.1"),
        'C' => (920, 1465, 750, "Table 5.1"),
        _ => (910, 1740, 650, "Table 6.1")
    };

    /// <summary>A room as Approved Document T reads it: what is in it, whom its name says it is for, and its doors with the room beyond each.</summary>
    internal sealed record Fitted(Room Room, IReadOnlyList<Point2D> Outline, int Wcs, int Basins, int Urinals, bool Drying, bool Beds, Sex Sex, bool Accessible, bool Ambulant,
        List<(Door Door, Room? Other)> Doors, List<(Component Piece, ComponentType Type)> Pieces)
    {
        public int Showers => Pieces.Count(each => each.Type.Form == ComponentForm.Shower);

        public int Baths => Pieces.Count(each => each.Type.Form == ComponentForm.Bath);

        /// <summary>A changing room or a locker room by its name - not a changing places toilet.</summary>
        public bool Changing => (Room.Name.Contains("changing", StringComparison.OrdinalIgnoreCase) || Room.Name.Contains("locker", StringComparison.OrdinalIgnoreCase))
                                && !Room.Name.Contains("changing places", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"[a-z]+")]
    private static partial Regex Words();

    private static HashSet<string> WordsOf(string name) => Words().Matches(name.ToLowerInvariant()).Select(match => match.Value).ToHashSet();

    /// <summary>Whom a room's name says it is for: women - female, women, ladies, girls - or men - male, men, gents, boys - or neither.</summary>
    public static Sex SexOf(Room room)
    {
        var words = WordsOf(room.Name);
        if (words.Overlaps(new[] { "female", "women", "woman", "womens", "ladies", "lady", "girls" })) return Sex.Female;
        if (words.Overlaps(new[] { "male", "men", "mens", "gents", "gentlemen", "gentleman", "boys" })) return Sex.Male;
        return Sex.None;
    }

    private static string Said(Sex sex) => sex == Sex.Female ? "women" : "men";

    /// <summary>Whether Approved Document T applies to the model: a building other than a dwelling, not a school, a nursery or a prison (1.1, 1.2), not under the US's codes.</summary>
    public static bool Applies(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats) return false;
        var words = WordsOf(document.ProjectInformation.BuildingType);
        return !words.Overlaps(new[] { "school", "academy", "nursery", "creche", "preschool", "prison", "custodial", "jail", "gaol" });
    }

    /// <summary>The model's rooms as Approved Document T reads them, kept while the model does not change; empty where it does not apply.</summary>
    internal static IReadOnlyDictionary<Room, Fitted> Plan(BimDocument document)
    {
        IReadOnlyDictionary<Room, Fitted> Work()
        {
            if (!Applies(document)) return new Dictionary<Room, Fitted>();
            var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
            var doors = outlines.Keys.ToDictionary(room => room, _ => new List<(Door, Room?)>());
            foreach (var door in document.All<Door>())
            {
                var (a, b) = Doors.RoomsBeside(document, door);
                if (a is not null && a != b && doors.TryGetValue(a, out var fromA)) fromA.Add((door, b));
                if (b is not null && a != b && doors.TryGetValue(b, out var fromB)) fromB.Add((door, a));
            }

            var pieces = document.All<Component>().Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId))).Where(entry => entry.Type is not null).ToList();
            return outlines.ToDictionary(entry => entry.Key, entry =>
            {
                var (room, outline) = (entry.Key, entry.Value);
                var held = pieces.Where(each => each.Piece.LevelId == room.LevelId && Polygon2D.Contains(outline, each.Piece.Location)).Select(each => (each.Piece, Type: each.Type!)).ToList();
                var inside = held.Select(each => each.Type).ToList();
                var words = WordsOf(room.Name);
                return new Fitted(room, outline, inside.Count(Bathrooms.IsWc), inside.Count(type => type.Form == ComponentForm.Basin), inside.Count(type => type.Form == ComponentForm.Urinal),
                    inside.Any(type => type.Form == ComponentForm.HandDryer), inside.Any(type => type.Form is ComponentForm.Bed or ComponentForm.BunkBed) || words.Contains("bedroom"),
                    SexOf(room), words.Overlaps(new[] { "accessible", "disabled", "wheelchair" }) || room.Name.Contains("changing places", StringComparison.OrdinalIgnoreCase),
                    words.Contains("ambulant"), doors[room], held);
            });
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("toilets: plan", document, null, null, Work) : Work();
    }

    /// <summary>A room's least and greatest dimensions in plan, mm: its outline squared to its longest side.</summary>
    public static (double Short, double Long) Size(IReadOnlyList<Point2D> outline)
    {
        var longest = Enumerable.Range(0, outline.Count).OrderByDescending(i => outline[i].DistanceTo(outline[(i + 1) % outline.Count])).First();
        var way = (outline[(longest + 1) % outline.Count] - outline[longest]).NormalisedOrDefault(Vector2D.UnitX);
        var across = way.PerpendicularLeft();
        var length = outline.Max(p => way.Dot(p - outline[0])) - outline.Min(p => way.Dot(p - outline[0]));
        var width = outline.Max(p => across.Dot(p - outline[0])) - outline.Min(p => across.Dot(p - outline[0]));
        return (Math.Min(length, width), Math.Max(length, width));
    }

    /// <summary>The single-sex toilet accommodation a room's doors all open into, and whom for; none where any opens elsewhere.</summary>
    private static Sex OpensInto(Fitted room) =>
        room.Doors.Count > 0 && room.Doors.All(door => door.Other is { } other && SexOf(other) != Sex.None) && room.Doors.Select(door => SexOf(door.Other!)).Distinct().Count() == 1
            ? SexOf(room.Doors[0].Other!) : Sex.None;

    /// <summary>Whether a WC room is an en-suite, reached only from a bedroom - which T1 does not reach (1.2).</summary>
    internal static bool EnSuite(IReadOnlyDictionary<Room, Fitted> plan, Fitted room) =>
        room.Wcs + room.Showers + room.Baths + room.Basins > 0 && room.Doors.Count > 0 && room.Doors.All(door => door.Other is { } other && plan.TryGetValue(other, out var beyond) && beyond.Beds);

    /// <summary>Whom a WC room is for, as single-sex toilet accommodation: its own name, or the toilets a cubicle opens into; none for a universal toilet.</summary>
    private static Sex For(Fitted room) => room.Sex != Sex.None ? room.Sex : room.Wcs == 1 && room.Basins == 0 ? OpensInto(room) : Sex.None;

    /// <summary>
    /// What a room comes to as toilet accommodation - "Type B universal toilet: 1 WC, its basin and hand-drying, 1.10 x 1.75 m" - with what
    /// Approved Document T asks of it that it falls short of; null for a room with no WC or urinal, or where the Approved Document does not apply.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var plan = Plan(document);
        if (!plan.TryGetValue(room, out var fitted)) return null;
        var ensuite = EnSuite(plan, fitted);
        if (ensuite)
        {
            // A wheelchair-accessible bedroom's en-suite is checked as the shower room or bathroom it is (Approved Document M Volume 2 4.24m,
            // round 104); any other bedroom's is no concern of Approved Document T's (1.2).
            if (!fitted.Doors.Any(door => door.Other is { } owner && plan.TryGetValue(owner, out var bedroom) && bedroom.Accessible)) return null;
            fitted = fitted with { Accessible = true };
        }

        if (fitted.Wcs == 0 && fitted.Urinals == 0 && fitted.Showers == 0 && !fitted.Changing && !(fitted.Accessible && fitted.Baths > 0)) return null;

        var wanting = new List<string>();
        var (narrow, deep) = Size(fitted.Outline);
        var size = $"{narrow / 1000:0.00} x {deep / 1000:0.00} m";
        string what;
        var facts = new List<string>();
        if (fitted.Wcs > 0 || fitted.Urinals > 0) facts.Add(fitted.Wcs == 1 ? "1 WC" : $"{fitted.Wcs} WCs");
        if (fitted.Showers > 0) facts.Add(fitted.Showers == 1 ? "a shower" : $"{fitted.Showers} showers");
        if (fitted.Baths > 0) facts.Add(fitted.Baths == 1 ? "a bath" : $"{fitted.Baths} baths");
        if (fitted.Urinals > 0) facts.Add(fitted.Urinals == 1 ? "a urinal" : $"{fitted.Urinals} urinals");
        if (fitted.Basins > 0) facts.Add(fitted.Basins == 1 ? "its basin" : $"{fitted.Basins} basins");
        if (fitted.Drying) facts.Add("hand-drying");
        facts.Add(size);

        void Sized(char type)
        {
            var (width, depth, door, table) = Least(type);
            if (narrow < width - 0.5 || deep < depth - 0.5)
                wanting.Add($"{size} - Approved Document T ({table}) asks a Type {type} {width:0} x {depth:0} at least");
            foreach (var (each, _) in fitted.Doors)
                if (document.FindType<DoorType>(each.TypeId) is { } kind && Doors.ClearWidth(kind) < door - 0.5)
                    wanting.Add($"its door gives {Doors.ClearWidth(kind):0} clear - Approved Document T ({table}) asks {door:0} at least of a Type {type}'s");
        }

        // The building's toilets, storey by storey: each said of on its first.
        var toilets = plan.Values.Where(each => each.Wcs > 0 && !EnSuite(plan, each)).OrderBy(each => document.FindLevel(each.Room.LevelId)?.Elevation ?? 0).ThenBy(each => each.Room.Number, StringComparer.Ordinal).ToList();
        var sex = For(fitted);
        if (fitted.Accessible && Diagram(fitted) is var (number, side, length, paragraph, kind) && number != 18)
        {
            // A wheelchair user's bathroom, shower room with a WC, shower room or changing room (5.18h, k, r; 5.21a; Diagrams 22 to 25).
            what = $"Wheelchair {kind}";
            if (narrow < side - 0.5 || deep < length - 0.5)
                wanting.Add($"{size} - Approved Document M Volume 2 ({paragraph}, Diagram {number}) asks a wheelchair-accessible {kind} {side:0} x {length:0} at least");
            wanting.AddRange(Wheelchair(document, plan, fitted));

            // One with a WC is not to be the building's only wheelchair-accessible toilet (5.16).
            if (fitted.Wcs > 0 && !ensuite && !plan.Values.Any(each => each.Accessible && each.Wcs > 0 && Diagram(each).Number == 18 && !EnSuite(plan, each)))
                wanting.Add($"it is the building's only wheelchair-accessible WC - Approved Document M Volume 2 (5.16) asks a {kind} with a WC not to be the only wheelchair-accessible toilet: a unisex toilet of its own as well (Diagram 18)");
        }
        else if (fitted.Accessible)
        {
            // A wheelchair-accessible unisex toilet: 1500 by 2200 (Diagram 18) - 2000 wide, with a standing height basin besides the finger rinse
            // basin, where it is the only toilet in the building (5.7a, 5.10e).
            what = "Wheelchair toilet";
            var only = toilets.Count == 1;
            var least = only ? 2000.0 : 1500;
            if (narrow < least - 0.5 || deep < 2200 - 0.5)
                wanting.Add($"{size} - Approved Document M Volume 2 ({(only ? "5.7a, 5.10e, " : string.Empty)}5.10i, Diagram 18) asks a wheelchair-accessible unisex toilet {least:0} x 2200 at least{(only ? ", 2000 wide where it is the only toilet in the building" : string.Empty)}");
            if (only && fitted.Basins < 2)
                wanting.Add("one basin - Approved Document M Volume 2 (5.7a, 5.10e) asks a standing height washbasin besides the finger rinse basin at the WC, where it is the only toilet in the building");
            wanting.AddRange(Wheelchair(document, plan, fitted));
        }
        else if (fitted.Wcs >= 2)
        {
            what = sex == Sex.None ? "Toilets" : $"Toilets, {Said(sex)}";
            wanting.Add(sex == Sex.None
                ? $"{fitted.Wcs} WCs in one room, for no one sex - Approved Document T (T1(2)) asks single-sex toilets, for men or for women, or universal toilets, each a fully enclosed room of its own with a WC, washbasin and hand-drying"
                : $"its {fitted.Wcs} WCs are not each in a cubicle or room of their own - Approved Document T (2.3, Sections 5 and 6) asks each WC in a single-sex toilet in a cubicle, Type C or D, or a room laid out as Type A or B");
        }
        else if (fitted.Wcs == 1 && fitted.Basins == 0 && sex != Sex.None)
        {
            // A single-sex cubicle, Type C or D, opening into the single-sex toilets with their basins.
            var type = fitted.Ambulant ? 'C' : 'D';
            what = $"Type {type} cubicle, {Said(sex)}";
            Sized(type);
            if (OpensInto(fitted) == Sex.None)
                wanting.Add("a door opening elsewhere than into single-sex toilet accommodation - Approved Document T (1.22) asks a cubicle's doors to open only into single-sex toilet accommodation");
            else if (!fitted.Doors.Any(door => door.Other is { } other && plan.TryGetValue(other, out var beyond) && beyond.Basins > 0))
                wanting.Add($"no basin in the toilets it opens into - Approved Document T ({Least(type).Table}) asks hand-washing in a single-sex communal space for those using a Type {type}");
        }
        else if (fitted.Wcs == 1 && fitted.Basins > 0)
        {
            // Self-contained: a universal toilet, or a single-sex toilet laid out as one (1.11).
            var type = fitted.Ambulant ? 'A' : 'B';
            what = sex == Sex.None ? $"Type {type} universal toilet" : $"Type {type} toilet, {Said(sex)}";
            Sized(type);
            if (!fitted.Drying)
                wanting.Add($"no hand-drying in it - Approved Document T (T1(2), {Least(type).Table}) asks a washbasin, mirror and hand-drying within it: a hand dryer or towel dispenser");
            if (sex == Sex.None)
                foreach (var (_, other) in fitted.Doors)
                {
                    if (other is null) continue;
                    if (SexOf(other) != Sex.None)
                        wanting.Add($"it is reached through the {other.Name}, single-sex toilets - Approved Document T (1.21) asks a universal toilet reached from a circulation route outside single-sex toilet accommodation");
                    else if (!FireSeparation.Circulation(other))
                        wanting.Add($"it opens into the {other.Name} - Approved Document T (1.18, 2.8) asks a universal toilet to open onto a well-lit corridor or circulation space");
                    if (plan.TryGetValue(other, out var beyond) && beyond.Basins > 0 && beyond.Wcs == 0)
                        wanting.Add($"there is a basin in the {other.Name} outside it - Approved Document T (1.16) asks no shared hand-washing in corridors or spaces outside a universal toilet");
                }
        }
        else if (fitted.Wcs == 1)
        {
            what = "Toilet";
            wanting.Add("a WC with no basin in its room, opening elsewhere than into single-sex toilets - Approved Document T (T1(2)) asks a universal toilet's washbasin and hand-drying in its own fully enclosed room, and a cubicle's in the single-sex toilets it opens into");
        }
        else if (fitted.Urinals > 0) what = sex == Sex.None ? "Urinals" : $"Urinals, {Said(sex)}";
        else if (fitted.Showers > 0) what = sex == Sex.None ? "Showers" : $"Showers, {Said(sex)}";
        else what = sex == Sex.None ? "Changing room" : $"Changing room, {Said(sex)}";

        // Showers for a building's staff, or anyone: one wheelchair-accessible shower room among them (5.18l) - said on the first room with showers.
        var showering = plan.Values.Where(each => each.Showers > 0 && !EnSuite(plan, each)).OrderBy(each => document.FindLevel(each.Room.LevelId)?.Elevation ?? 0)
            .ThenBy(each => each.Room.Number, StringComparer.Ordinal).ToList();
        if (showering.Count > 0 && !showering.Any(each => each.Accessible) && showering[0] == fitted)
            wanting.Add("no wheelchair-accessible shower room among the building's showers - Approved Document M Volume 2 (5.18l) asks at least one shower compartment as Diagram 23 lays it out where showers are given for staff in a commercial development - name its room accessible");

        // A sports building's communal changing: an individual self-contained changing or shower room as well (5.18d) - said on its first changing room.
        var changing = plan.Values.Where(each => each.Changing && !each.Accessible).OrderBy(each => document.FindLevel(each.Room.LevelId)?.Elevation ?? 0)
            .ThenBy(each => each.Room.Number, StringComparer.Ordinal).ToList();
        if (changing.Count > 0 && changing[0] == fitted && Sporting(document) && !plan.Values.Any(each => each.Accessible && (each.Changing || each.Showers > 0)))
            wanting.Add("no individual self-contained wheelchair-accessible changing or shower room - Approved Document M Volume 2 (5.18d) asks them in sports facilities as well as communal separate-sex ones (Diagrams 22, 23)");

        // A choice of left- and right-hand transfer where there is more than one of a kind (5.10c, 5.10d, 5.18s, 5.21b).
        if (fitted.Accessible && Hand(fitted) is var hand && hand != 0 && Transfer(document, plan, fitted, hand) is { } choice) wanting.Add(choice);

        // A hand dryer 800 to 1000 above the floor (Approved Document M Volume 2 Diagram 20).
        foreach (var (piece, type) in fitted.Pieces.Where(each => each.Type.Form == ComponentForm.HandDryer && each.Type.Load > 0))
            if (Above(document, piece, type) is var at && (at < 800 - 0.5 || at > 1000 + 0.5))
                wanting.Add($"its hand dryer {at:0} above the floor - Approved Document M Volume 2 (Diagram 20) puts an automatic hand dryer 800 to 1000 above it");

        // Urinals in men's toilets only (2.2).
        if (fitted.Urinals > 0 && (sex != Sex.Male || fitted.Accessible))
            wanting.Add($"{(fitted.Urinals == 1 ? "a urinal" : "urinals")} in toilets not for men - Approved Document T (2.2) asks urinals only in male single-sex toilets");

        // The building's: single-sex toilets for both men and women (T1(1)), said on the first toilet of the one sex there is.
        var sexes = toilets.Where(each => !each.Accessible).Select(For).Where(each => each != Sex.None).Distinct().ToList();
        if (sexes.Count == 1 && toilets.FirstOrDefault(each => For(each) == sexes[0]) == fitted)
            wanting.Add($"there are single-sex toilets for {Said(sexes[0])} and none for {Said(sexes[0] == Sex.Male ? Sex.Female : Sex.Male)} - Approved Document T (T1(1)) asks reasonable provision for male and female single-sex toilets");

        // A wheelchair-accessible unisex toilet wherever there are toilets - taken as on each storey with them (Approved Document M Volume 2 5.7b;
        // Approved Document T 1.14) - said on the storey's first toilet.
        var storey = toilets.Where(each => each.Room.LevelId == room.LevelId).ToList();
        if (storey.Count > 0 && storey[0] == fitted && !storey.Any(each => each.Accessible))
            wanting.Add($"no wheelchair-accessible unisex toilet {(toilets.Any(each => each.Accessible) ? "on this storey" : "in the building")} - Approved Document M Volume 2 (5.7b) asks one at each location where there are toilets, taken as each storey with them, and Approved Document T (1.14) one in every building other than dwellings - name its room accessible");

        // Each sex's toilets: a cubicle for ambulant disabled people (5.7c), and with four cubicles or more an enlarged one 1200 wide (5.7d, 5.14d).
        foreach (var each in sexes)
        {
            var theirs = toilets.Where(toilet => !toilet.Accessible && For(toilet) == each).ToList();
            if (theirs.Count == 0 || theirs[0] != fitted) continue;
            if (!theirs.Any(toilet => toilet.Ambulant))
                wanting.Add($"no ambulant cubicle in the {Said(each)}'s toilets - Approved Document M Volume 2 (5.7c, 5.14b) asks one for ambulant disabled people in each separate-sex toilet accommodation, Type C or A by Approved Document T (1.15) - name its room ambulant");
            var cubicles = theirs.Where(toilet => toilet.Wcs == 1 && toilet.Basins == 0).ToList();
            if (cubicles.Count >= 4 && !cubicles.Any(toilet => Size(toilet.Outline).Short >= 1200 - 0.5))
                wanting.Add($"{cubicles.Count} cubicles in the {Said(each)}'s toilets, none 1200 wide - Approved Document M Volume 2 (5.7d, 5.14d) asks an enlarged cubicle 1200 wide where there are four or more");
        }
        // A changing places toilet in the buildings 5.7e names - said on the building's first toilet.
        if (toilets.Count > 0 && toilets[0] == fitted && ChangingPlacesAsked(document, plan) is { } why
            && !plan.Keys.Any(each => each.Name.Contains("changing places", StringComparison.OrdinalIgnoreCase)))
            wanting.Add($"no changing places toilet in the building, {why} - Approved Document M Volume 2 (5.6, 5.7e) asks one, besides its wheelchair-accessible toilets, as BS 8300-2 18.6 lays it out - name its room Changing Places");

        if (sexes.Count == 0 && toilets.Count > 0 && toilets.FirstOrDefault(each => !each.Accessible && each.Wcs == 1 && each.Basins > 0) == fitted)
            facts.Add("universal toilets only, as Approved Document T (T1(1)) allows where space precludes single-sex toilets");

        var said = $"{what}: {string.Join(", ", facts)}";
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }

    /// <summary>How high a fitting's underside is above its storey's floor, mm.</summary>
    internal static double Above(BimDocument document, Component piece, ComponentType type) =>
        ComponentHosting.BaseElevation(document, piece, type) - (document.FindLevel(piece.LevelId)?.Elevation ?? 0);

    /// <summary>How far a ray from a point goes before it meets a room's outline, mm; null where it meets none.</summary>
    internal static double? Ray(IReadOnlyList<Point2D> outline, Point2D from, Vector2D way)
    {
        double? best = null;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, edge) = (outline[i], outline[(i + 1) % outline.Count] - outline[i]);
            var across = way.Cross(edge);
            if (Math.Abs(across) < 1e-9) continue;
            var start = a - from;
            var (along, on) = (start.Cross(edge) / across, start.Cross(way) / across);
            if (along >= 0 && on is >= 0 and <= 1 && (best is null || along < best)) best = along;
        }

        return best;
    }

    /// <summary>
    /// Whether a wheelchair-accessible toilet has its 1500 by 1500 turning space (Diagram 18): a square of it, squared to the room, clear of what
    /// stands on its floor - its WC or bath, not a basin or the alarm's cord on the wall, nor a level shower its turning space runs over (Diagram 23) -
    /// and of its doors' swings (5.3).
    /// </summary>
    internal static bool Turns(BimDocument document, Fitted room)
    {
        const double side = 1500;
        var outline = room.Outline;
        var longest = Enumerable.Range(0, outline.Count).OrderByDescending(i => outline[i].DistanceTo(outline[(i + 1) % outline.Count])).First();
        var way = (outline[(longest + 1) % outline.Count] - outline[longest]).NormalisedOrDefault(Vector2D.UnitX);
        var across = way.PerpendicularLeft();
        var origin = outline[0];
        var (minU, maxU) = (outline.Min(p => way.Dot(p - origin)), outline.Max(p => way.Dot(p - origin)));
        var (minV, maxV) = (outline.Min(p => across.Dot(p - origin)), outline.Max(p => across.Dot(p - origin)));
        if (maxU - minU < side - 0.5 || maxV - minV < side - 0.5) return false;

        var standing = room.Pieces.Where(each => Above(document, each.Piece, each.Type) <= Clearances.Underneath
                                                 && !(each.Type.Form is ComponentForm.Basin or ComponentForm.AssistanceAlarm or ComponentForm.HandDryer && each.Type.IsHosted)
                                                 && each.Type.Form != ComponentForm.Shower)
            .Select(each => Views.ComponentModel.Footprint(each.Type, Views.ComponentModel.FrameOf(each.Piece, 0)))
            .Concat(room.Doors.SelectMany(door => DoorSwing.Sweeps(document, door.Door))).ToList();
        var step = Math.Max(50, Math.Max(maxU - minU, maxV - minV) / 60);
        for (var u = minU; u <= maxU - side + 0.5; u += step)
        for (var v = minV; v <= maxV - side + 0.5; v += step)
        {
            Point2D At(double a, double b) => origin + way * (u + a) + across * (v + b);
            var square = new[] { At(1, 1), At(side - 1, 1), At(side - 1, side - 1), At(1, side - 1) };
            if (!square.Concat(new[] { At(side / 2, 1), At(side - 1, side / 2), At(side / 2, side - 1), At(1, side / 2) }).All(point => Polygon2D.Contains(outline, point))) continue;
            if (standing.All(obstacle => PolygonBoolean.Combine(square, obstacle, BooleanOperation.Intersection).Sum(region => region.Area) <= 50 * 50)) return true;
        }

        return false;
    }

    /// <summary>
    /// What a wheelchair-accessible unisex toilet's fittings fall short of (Approved Document M Volume 2 5.10, Diagrams 18 to 20): its turning
    /// space; its WC's centreline 500 from the wall beside it and its seat 480 high; the finger rinse basin nearest the WC its rim 720 to 740
    /// high, any other 780 to 800; its emergency assistance alarm; and no point on its storey more than 40 m from it, walked (5.10h).
    /// </summary>
    private static IEnumerable<string> Wheelchair(BimDocument document, IReadOnlyDictionary<Room, Fitted> plan, Fitted room)
    {
        if (!Turns(document, room))
            yield return $"no 1500 x 1500 turning space in it clear of {(room.Wcs > 0 ? "the WC" : "what stands on its floor")} and the door's swing - Approved Document M Volume 2 (5.3, Diagram {Diagram(room).Number}) asks one, the door opening out where it would sweep it";

        var wc = room.Pieces.FirstOrDefault(each => Bathrooms.IsWc(each.Type));
        if (wc.Piece is { } pan)
        {
            var frame = Views.ComponentModel.FrameOf(pan, 0);
            var side = (frame.Plan(1, 0) - frame.Plan(0, 0)).NormalisedOrDefault(Vector2D.UnitX);
            var near = new[] { Ray(room.Outline, pan.Location, side), Ray(room.Outline, pan.Location, -side) }.OfType<double>().DefaultIfEmpty(0).Min();
            if (Math.Abs(near - 500) > 50 + 0.5)
                yield return $"its WC's centreline {near:0} from the wall beside it - Approved Document M Volume 2 (Diagram 18) sets it 500 from that wall, with 1000 to the other side for the transfer";
            var seat = Views.ComponentModel.SeatOf(wc.Type);
            if (Math.Abs(seat - 480) > 10 + 0.5)
                yield return $"its WC's seat {seat:0} high - Approved Document M Volume 2 (Diagram 19) asks 480, a pan to BS EN 997 taking a seat riser (5.10q): its Seat Height in Edit Type";

            var basins = room.Pieces.Where(each => each.Type.Form == ComponentForm.Basin).OrderBy(each => each.Piece.Location.DistanceTo(pan.Location)).ToList();
            for (var i = 0; i < basins.Count; i++)
            {
                var rim = Above(document, basins[i].Piece, basins[i].Type) + basins[i].Type.Height;
                var (low, high, what) = i == 0 ? (720.0, 740.0, "the finger rinse basin by the WC") : (780.0, 800.0, "a basin for people standing");
                if (rim < low - 0.5 || rim > high + 0.5)
                    yield return $"{what} has its rim {rim:0} high - Approved Document M Volume 2 (Diagrams 19, 20) asks {low:0} to {high:0}";
            }
        }

        if (!room.Pieces.Any(each => each.Type.Form == ComponentForm.AssistanceAlarm))
            yield return $"no emergency assistance alarm - Approved Document M Volume 2 ({Diagram(room).Number switch { 18 => "5.4h, 5.10m to o", 25 => "5.4h, 5.21f, 5.21g", _ => "5.4h, 5.18e, 5.18f" }}) asks one, its pull cord reachable from {(room.Baths > 0 ? "the bath" : room.Wcs > 0 ? "the WC" : "the tip-up seat")} and the floor, its call signal seen and heard outside";

        // How far a wheelchair user goes to a unisex toilet on its storey, walked through the doors (5.10h(i)).
        if (Diagram(room).Number != 18) yield break;
        var storey = plan.Where(entry => entry.Key.LevelId == room.Room.LevelId).ToDictionary(entry => entry.Key, entry => entry.Value.Outline);
        var links = Escape.Links(document, storey.Keys.ToHashSet());
        var doors = links.Where(link => link.Room == room.Room && link.Other is not null).Select(link => (link.Room, link.Inside, 0.0)).ToList();
        if (doors.Count == 0) yield break;
        var from = new Escape.Walk(storey, links.Where(link => link.Other is { } other && storey.ContainsKey(other))).From(doors);
        (Room Room, double Distance)? furthest = null;
        foreach (var (other, outline) in storey.Where(entry => entry.Key != room.Room))
        foreach (var point in Escape.Points(outline))
            if (from(other, point) is { } distance && (furthest is null || distance > furthest.Value.Distance))
                furthest = (other, distance);
        if (furthest is { } far && far.Distance > 40000 + 0.5)
            yield return $"the furthest point of the {far.Room.Name} is {far.Distance / 1000:0.0} m from it, walked - Approved Document M Volume 2 (5.10h) keeps a wheelchair user within 40 m of one on the same floor";
    }

    /// <summary>
    /// Whether a building is one Approved Document M Volume 2 asks a changing places toilet in (5.7e), and why: a hospital or primary care
    /// centre; a crematorium or cemetery building; a shopping centre or retail park of 30,000 m², a shop of 2500 m², a sport or leisure building
    /// over 5000 m² - its rooms' areas together taken for its gross floor area, a little under it; a place of assembly, recreation or
    /// entertainment its rooms' occupant counts putting at 350 people or more.
    /// </summary>
    private static string? ChangingPlacesAsked(BimDocument document, IReadOnlyDictionary<Room, Fitted> plan)
    {
        var type = document.ProjectInformation.BuildingType.ToLowerInvariant();
        bool Is(params string[] names) => names.Any(name => type.Contains(name, StringComparison.Ordinal));
        var area = plan.Values.Sum(each => Polygon2D.Area(each.Outline)) / 1e6;
        var people = plan.Keys.Sum(room => room.OccupantCount);
        if (Is("hospital", "primary care", "health centre", "surgery", "clinic")) return "a hospital or primary care centre (v)";
        if (Is("crematori", "cemetery")) return "a crematorium or cemetery building (vi)";
        if (Is("shopping centre", "shopping mall", "mall", "retail park") && area >= 30000 - 0.5) return $"a shopping centre or retail park of {area:0} m² (ii)";
        if (Is("shop", "store", "retail", "supermarket") && !Is("shopping centre", "mall", "retail park") && area >= 2500 - 0.5) return $"retail premises of {area:0} m² (iii)";
        if (Is("sport", "leisure", "gym", "swimming") && area > 5000 + 0.5) return $"a sport and leisure building of {area:0} m² (iv)";
        if (Is("cinema", "theatre", "concert", "conference", "museum", "gallery", "library", "church", "chapel", "mosque", "temple", "synagogue", "worship", "arcade",
                "college", "university", "stadium", "arena", "exhibition", "zoo", "motorway service") && people >= 350)
            return $"a place of assembly, recreation or entertainment for {people} people (i)";
        return null;
    }

    /// <summary>
    /// Which of Approved Document M Volume 2's diagrams a wheelchair-accessible room is laid out by, by what is in it: a bathroom with a corner WC,
    /// Diagram 25, 2500 by 2700 (5.21a); a shower room with a corner WC, Diagram 24, 2400 by 2500 (5.18r); a shower room, Diagram 23, 2000 by
    /// 2200 (5.18k); a changing room, Diagram 22, 2000 by 2200 (5.18h); else a unisex toilet, Diagram 18.
    /// </summary>
    private static (int Number, double Side, double Length, string Paragraph, string Kind) Diagram(Fitted room) =>
        room.Baths > 0 ? (25, 2500, 2700, "5.21a", "bathroom")
        : room.Showers > 0 && room.Wcs > 0 ? (24, 2400, 2500, "5.18r", "shower room")
        : room.Showers > 0 ? (23, 2000, 2200, "5.18k", "shower room")
        : room.Wcs == 0 && room.Changing ? (22, 2000, 2200, "5.18h", "changing room")
        : (18, 1500, 2200, "5.10i", "toilet");

    /// <summary>Whether the building is a sports facility, by its type: sport, leisure, gym, swimming, a stadium or arena.</summary>
    private static bool Sporting(BimDocument document) =>
        WordsOf(document.ProjectInformation.BuildingType).Any(word => word.StartsWith("sport", StringComparison.Ordinal) || word is "leisure" or "gym" or "swimming" or "pool" or "stadium" or "arena");

    /// <summary>Which hand a wheelchair-accessible room's WC is transferred to from: +1 with the wall beside it on its right, -1 on its left; 0 with no WC.</summary>
    private static int Hand(Fitted room)
    {
        if (room.Pieces.FirstOrDefault(each => Bathrooms.IsWc(each.Type)).Piece is not { } pan) return 0;
        var frame = Views.ComponentModel.FrameOf(pan, 0);
        var side = (frame.Plan(1, 0) - frame.Plan(0, 0)).NormalisedOrDefault(Vector2D.UnitX);
        var (right, left) = (Ray(room.Outline, pan.Location, side), Ray(room.Outline, pan.Location, -side));
        return right is null && left is null ? 0 : (right ?? double.MaxValue) <= (left ?? double.MaxValue) ? 1 : -1;
    }

    /// <summary>
    /// A choice of left- and right-hand transfer, where a building has more than one of a kind of wheelchair-accessible room with a WC: in a
    /// building of one storey, not all of one hand (5.10d, 5.18s, 5.21b) - said on the second; up a multi-storey building, a unisex toilet's on
    /// alternate floors (5.10c) - said where one is the same hand as the one on the storey under it.
    /// </summary>
    private static string? Transfer(BimDocument document, IReadOnlyDictionary<Room, Fitted> plan, Fitted room, int hand)
    {
        var number = Diagram(room).Number;
        double Height(Fitted each) => document.FindLevel(each.Room.LevelId)?.Elevation ?? 0;
        var alike = plan.Values.Where(each => each.Accessible && each.Wcs > 0 && Diagram(each).Number == number && !EnSuite(plan, each) && Hand(each) != 0)
            .OrderBy(Height).ThenBy(each => each.Room.Number, StringComparer.Ordinal).ToList();
        if (alike.Count < 2) return null;
        var paragraph = number switch { 25 => "5.21b", 24 => "5.18s", _ => "5.10d" };
        var storeys = alike.Select(each => each.Room.LevelId).Distinct().Count();
        if (storeys == 1 || number != 18)
            return alike.All(each => Hand(each) == hand) && alike[1] == room
                ? $"every one of the building's {alike.Count} wheelchair-accessible {Diagram(room).Kind}s is laid out for {(hand > 0 ? "left" : "right")}-hand transfer - Approved Document M Volume 2 ({paragraph}) asks a choice of layouts for left- and right-hand transfer where there is more than one"
                : null;
        var under = alike.Where(each => Height(each) < Height(room) - 1).OrderByDescending(Height).FirstOrDefault();
        return under is not null && Hand(under) == hand && alike.First(each => each.Room.LevelId == room.Room.LevelId) == room
            ? $"it is laid out for {(hand > 0 ? "left" : "right")}-hand transfer as the one on the storey under it is - Approved Document M Volume 2 (5.10c) asks left- and right-hand transfer on alternate floors of a multi-storey building"
            : null;
    }

    /// <summary>What a room is as toilet accommodation, for the clear spaces its fittings need: none where Approved Document T does not apply.</summary>
    public enum Place
    {
        None,

        /// <summary>A room with a WC: a universal toilet, a cubicle, a single-sex toilet - Approved Document T's activity spaces.</summary>
        Toilet,

        /// <summary>A wheelchair-accessible unisex toilet: Approved Document M Volume 2's Diagram 18.</summary>
        Accessible,

        /// <summary>Single-sex toilets' washroom, with their basins and urinals and no WC.</summary>
        Washroom,

        /// <summary>A changing room or locker room: 1500 clear in front of its lockers (Approved Document M Volume 2 5.18j).</summary>
        Changing,

        /// <summary>A bedroom of a building other than a dwelling: no dwelling's spaces round its furniture (round 104).</summary>
        Bedroom
    }

    /// <summary>What a room is as toilet accommodation - a fitting in no room of a building the Approved Document applies to taken as in a toilet.</summary>
    public static Place PlaceOf(BimDocument document, Room? room)
    {
        if (!Applies(document)) return Place.None;
        var plan = Plan(document);
        if (room is null || !plan.TryGetValue(room, out var fitted)) return Place.Toilet;
        return fitted.Accessible || AccessibleSuite(plan, fitted) ? Place.Accessible : fitted.Wcs > 0 ? Place.Toilet : fitted.Changing ? Place.Changing : fitted.Basins + fitted.Urinals > 0 ? Place.Washroom : fitted.Beds ? Place.Bedroom : Place.None;
    }

    /// <summary>Whether a room is the en-suite of a wheelchair-accessible bedroom - checked as a wheelchair user's shower room or bathroom (4.24m).</summary>
    internal static bool AccessibleSuite(IReadOnlyDictionary<Room, Fitted> plan, Fitted room) =>
        EnSuite(plan, room) && room.Doors.Any(door => door.Other is { } owner && plan.TryGetValue(owner, out var bedroom) && bedroom.Accessible);

    /// <summary>The rooms of a model with what Approved Document T asks of them that they fall short of.</summary>
    public static IEnumerable<(Room Room, string Verdict)> Shortfalls(BimDocument document) =>
        Plan(document).Keys.Select(room => (Room: room, Verdict: Verdict(document, room))).Where(entry => entry.Verdict is not null && entry.Verdict.Contains(" asks", StringComparison.Ordinal))
            .Select(entry => (entry.Room, entry.Verdict!));
}
