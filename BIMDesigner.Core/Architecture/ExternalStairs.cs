using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// External escape stairs (round 133), by Approved Document B Volume 2 3.24d and 3.29 to 3.33, and Volume 1 3.66 to 3.72 and 3.85d for flats.
///
/// An external stair: a stair from one storey to another, put down outside the walls (its Outside ticked) or with neither its foot nor its head
/// in a room; stairs one above another, the head of one by the foot of the next, one stair. The doors onto it: doors in outside walls on the
/// storeys it passes, opening within 1.2 m of its steps at their floor's height - not those where it meets the ground, which are final exits.
///
/// Asked of one in a building other than a dwelling: an internal escape stair from every part of each storey it serves but plant (3.31a); in an
/// assembly building none of its doors from a part the public use (3.31b), and in a residential (institutional) building only from offices
/// and staff accommodation (3.31c); its doors E 30 with closers, but a single door onto the top landing of a stair leading down (3.32a); the
/// outside walls within 1800 of its flights and landings - beside and above them, from 9 m below up to 1100 over its top landing - REI 30, their
/// doors E 30 and their windows fixed shut and E 30 (3.32b, e); over 6 m high, a roof over it against snow and ice (3.32d); its flights A2-s3, d2
/// but between the ground and a floor 6 m up or down at most (3.24d); and 800 wide at least (Table 3.1). Of one serving flats: the same, its
/// floors 6 m above the ground at most (3.69a, 3.70b), and where another stair serves them, an internal stair from every part (3.70a).
///
/// And of a protected stairway projecting from the building, recessed into it or in an internal angle of its outside wall: 1800 between an
/// unprotected area of its enclosure and one of the building's that face each other (Volume 2 3.29, 3.30, Diagram 3.3; Volume 1 3.66, 3.67,
/// Diagram 3.10) - an unprotected area taken as a window or door of less than 30 minutes.
///
/// Not done: the escape route from the stair's foot to a place of safety and the building within 1800 of it (3.32c, 3.71c), the way to a place
/// of safety being the ground at its foot, read as open; a flat roof as the way onto it (3.33, 3.72); a podium served by its own stair (3.70b);
/// a house's external escape stairs (Volume 1 2.17), a stair outside a house being a garden's or a balcony's as often as an escape stair.
/// </summary>
public static class ExternalStairs
{
    /// <summary>The zone round an external stair's flights and landings, below them and over its top landing; the height past which it is roofed over (3.32b, d), mm.</summary>
    public const double Zone = 1800, Below = 9000, OverTop = 1100, Weather = 6000;

    /// <summary>How far from its steps a door onto an external stair opens, mm; how near the ground a storey is to be at a door for it to be a final exit, mm.</summary>
    public const double Reach = 1200, AtGround = 600;

    /// <summary>A door onto an external stair: the door, the room it leads out of, its storey and where it comes out.</summary>
    public sealed record Onto(Door Door, Room? Room, Level Level, Point2D Out);

    /// <summary>An external stair: its stair elements, its steps, its bottom and top, its foot and head, and the doors onto it.</summary>
    public sealed record Placed(IReadOnlyList<Stair> Stairs, IReadOnlyList<StairStep> Steps, double Bottom, double Top, Point2D Foot, Point2D Head, double Width, IReadOnlyList<Onto> Doors);

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the external stair rules reach a building: one other than a dwelling, or a block of flats, in England.</summary>
    private static (bool Volume2, bool Flats) Rules(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? (false, false) : (HorizontalEscape.Applies(document), FireDesign.UseOf(document) == BuildingUse.Flats);

    /// <summary>Where a door comes out on the side away from a room, mm - null for one between rooms.</summary>
    private static Point2D? Out(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) return null;
        var reach = type.Structure.TotalWidth / 2 + 150;
        var (a, b) = (wall.PointAt(type.Structure, door.DistanceAlongWall, reach), wall.PointAt(type.Structure, door.DistanceAlongWall, -reach));
        var (inA, inB) = (RoomsOf.Containing(document, wall.LevelId, a), RoomsOf.Containing(document, wall.LevelId, b));
        return inA is null && inB is not null ? a : inB is null && inA is not null ? b : inA is null && inB is null ? a : null;
    }

    /// <summary>The ground outside a door: the terrain where there is one, else the ground storey's floor, mm.</summary>
    private static double GroundOutside(BimDocument document, Point2D at) => Terrains.GroundAt(document, at) ?? Escape.Ground(document);

    /// <summary>The least distance from a point to a polygon's edges, none inside it, mm.</summary>
    private static double DistanceTo(IReadOnlyList<Point2D> outline, Point2D point)
    {
        if (outline.Count >= 3 && Polygon2D.Contains(outline, point)) return 0;
        var least = double.PositiveInfinity;
        for (var i = 0; i < outline.Count; i++) least = Math.Min(least, Line2D.DistanceFromSegment(point, outline[i], outline[(i + 1) % outline.Count]));
        return least;
    }

    /// <summary>The least distance between two segments that do not cross, mm.</summary>
    private static double Between(Point2D a0, Point2D a1, Point2D b0, Point2D b1) =>
        new[] { Line2D.DistanceFromSegment(a0, b0, b1), Line2D.DistanceFromSegment(a1, b0, b1), Line2D.DistanceFromSegment(b0, a0, a1), Line2D.DistanceFromSegment(b1, a0, a1) }.Min();

    /// <summary>The building's external stairs, kept while the model does not change.</summary>
    public static IReadOnlyList<Placed> Of(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("external stairs", document, null, null, () => Find(document)) : Find(document);

    private static IReadOnlyList<Placed> Find(BimDocument document)
    {
        var withRooms = document.All<Room>().Where(room => room.GetBoundary(document) is { IsEnclosed: true }).Select(room => room.LevelId).ToHashSet();
        var single = new List<(Stair Stair, List<StairStep> Steps, double Bottom, double Top, Point2D Foot, Point2D Head, double Width, Guid Base, Guid Upper)>();
        foreach (var stair in document.All<Stair>())
        {
            if (stair.TopLevelId is null) continue;
            var flights = Stairs.Flights(document, stair);
            var layouts = flights.Select(flight => flight.Layout).Where(layout => layout.Steps.Count > 0).ToList();
            if (layouts.Count == 0) continue;
            var (first, last) = (layouts.MinBy(layout => layout.Bottom)!, layouts.MaxBy(layout => layout.Top)!);
            if (last.Top - first.Bottom < 2000) continue;
            Point2D Middle(IReadOnlyList<Point2D> outline) => new(outline.Average(point => point.X), outline.Average(point => point.Y));
            var foot = first.WalkingLine.Count > 0 ? first.WalkingLine[0] : Middle(first.Steps[0].Outline);
            var head = last.WalkingLine.Count > 0 ? last.WalkingLine[^1] : Middle(last.Steps[^1].Outline);
            var (lowest, highest) = (flights[0].LevelId, flights[^1].TopLevelId ?? flights[^1].LevelId);
            var outside = stair.Outside
                          || withRooms.Contains(lowest) && withRooms.Contains(highest) && RoomsOf.Containing(document, lowest, foot) is null && RoomsOf.Containing(document, highest, head) is null;
            if (!outside) continue;
            single.Add((stair, layouts.SelectMany(layout => layout.Steps).ToList(), first.Bottom, last.Top, foot, head, layouts.Min(layout => layout.Width), lowest, highest));
        }

        // One stair above another, the head of the lower by the foot of the upper, is one stair.
        var groups = single.Select(each => new List<int> { single.IndexOf(each) }).ToList();
        bool Merged()
        {
            for (var i = 0; i < groups.Count; i++)
            for (var j = 0; j < groups.Count; j++)
            {
                if (i == j) continue;
                var lower = single[groups[i].MaxBy(k => single[k].Top)];
                var upper = single[groups[j].MinBy(k => single[k].Bottom)];
                if (lower.Upper != upper.Base || lower.Head.DistanceTo(upper.Foot) > 3000) continue;
                groups[i].AddRange(groups[j]);
                groups.RemoveAt(j);
                return true;
            }

            return false;
        }

        while (Merged()) { }

        var ground = Escape.Ground(document);
        var placed = new List<Placed>();
        foreach (var group in groups)
        {
            var parts = group.Select(k => single[k]).OrderBy(part => part.Bottom).ToList();
            var steps = parts.SelectMany(part => part.Steps).ToList();
            var (bottom, top) = (parts[0].Bottom, parts[^1].Top);
            var (foot, head) = (parts[0].Foot, parts[^1].Head);
            var doors = new List<Onto>();
            foreach (var door in document.All<Door>())
            {
                if (document.Find<Wall>(door.HostWallId) is not { } wall || document.FindLevel(wall.LevelId) is not { } level || document.FindType<DoorType>(door.TypeId) is not { } type
                    || !Doors.External(document, door, type)) continue;
                var floor = level.Elevation;
                if (floor < bottom - AtGround || floor > top + AtGround || Out(document, door) is not { } at) continue;

                // Where it meets the ground, a door is a final exit, not onto the stair.
                var grade = GroundOutside(document, at);
                if (Math.Abs(floor - grade) <= AtGround && (Math.Abs(bottom - grade) <= AtGround || Math.Abs(top - grade) <= AtGround)) continue;
                var near = steps.Any(step => Math.Abs(step.Top - floor) <= AtGround && DistanceTo(step.Outline, at) <= Reach)
                           || Math.Abs(top - floor) <= AtGround && at.DistanceTo(head) <= Reach + 300
                           || Math.Abs(bottom - floor) <= AtGround && at.DistanceTo(foot) <= Reach + 300;
                if (!near) continue;
                var (one, other) = Doors.RoomsBeside(document, door);
                doors.Add(new Onto(door, one ?? other, level, at));
            }

            placed.Add(new Placed(parts.Select(part => part.Stair).ToList(), steps, bottom, top, foot, head, parts.Min(part => part.Width), doors));
        }

        return placed;
    }

    /// <summary>Whether a stair is an external escape stair only - doors from the storeys onto it, none of them a main entrance - and so no approach to the building.</summary>
    public static bool EscapeOnly(BimDocument document, Stair stair) =>
        Of(document).FirstOrDefault(each => each.Stairs.Contains(stair)) is { Doors.Count: > 0 } placed && placed.Doors.All(onto => !EscapeRoutes.MainEntrances(document).Contains(onto.Door));

    /// <summary>The doors onto the building's external stairs.</summary>
    public static IReadOnlySet<Door> DoorsOnto(BimDocument document) => Of(document).SelectMany(stair => stair.Doors).Select(onto => onto.Door).ToHashSet();

    /// <summary>Whether a door to the outside comes out at the ground - its storey within 600 of the ground outside it, or below it - and so is a final exit.</summary>
    public static bool AtGrade(BimDocument document, Door door) =>
        document.Find<Wall>(door.HostWallId) is not { } wall || document.FindLevel(wall.LevelId) is not { } level || Out(document, door) is not { } at
        || level.Elevation <= GroundOutside(document, at) + AtGround;

    /// <summary>Whether a door to the outside is a way out of its storey: at the ground, or onto an external stair.</summary>
    public static bool WayOut(BimDocument document, Door door, IReadOnlySet<Door> onto) => onto.Contains(door) || AtGrade(document, door);

    /// <summary>What an external stair falls short of, said on the first of its stair elements; nothing for the others, or a stair that is not one.</summary>
    public static IReadOnlyList<string> Wanting(BimDocument document, Stair stair)
    {
        var (volume2, flats) = Rules(document);
        if (!volume2 && !flats) return Array.Empty<string>();
        return Of(document).FirstOrDefault(each => each.Stairs.Count > 0 && ReferenceEquals(each.Stairs[0], stair)) is { } placed ? Wanting(document, placed, volume2) : Array.Empty<string>();
    }

    private static IReadOnlyList<string> Wanting(BimDocument document, Placed stair, bool volume2)
    {
        var wanting = new List<string>();
        var book = volume2 ? "Approved Document B Volume 2" : "Approved Document B Volume 1";
        string Cite(string two, string one) => volume2 ? two : one;
        var ground = Escape.Ground(document);
        var group = volume2 ? HorizontalEscape.Purpose(document) : PurposeGroup.ResidentialOther;
        var levels = stair.Doors.Select(onto => onto.Level).DistinctBy(level => level.Id).OrderBy(level => level.Elevation).ToList();
        var links = Escape.Links(document, document.All<Room>().ToHashSet());
        if (stair.Doors.Count == 0)
        {
            wanting.Add($"it is an external stair with no door onto it from the building - {book} counts it no escape stair until one opens onto its flights or landings");
            return wanting;
        }

        // An internal escape stair from every part of each storey it serves, plant left out (3.31a, 3.70a); a flat's floors 6 m up at most (3.69a, 3.70b).
        var internalStairs = Doors.StairRooms(document);
        var anyInternal = false;
        foreach (var level in levels)
        {
            var rooms = document.All<Room>().Where(room => room.LevelId == level.Id && room.GetBoundary(document) is { IsEnclosed: true }).ToList();
            var reached = rooms.Where(internalStairs.Contains).ToHashSet();
            anyInternal |= reached.Count > 0;
            var queue = new Queue<Room>(reached);
            while (queue.Count > 0)
            {
                var room = queue.Dequeue();
                foreach (var link in links.Where(link => link.Room == room && link.Other is { } next && next.LevelId == level.Id && !reached.Contains(next)))
                {
                    reached.Add(link.Other!);
                    queue.Enqueue(link.Other!);
                }
            }

            var cut = rooms.Where(room => !reached.Contains(room) && !Named(room, "plant", "boiler", "tank", "switch", "riser", "duct", "lift")).Select(room => room.Name).Distinct().ToList();
            if (cut.Count > 0 && (volume2 || reached.Count > 0))
                wanting.Add($"on the {level.Name} the {string.Join(", the ", cut)} {(cut.Count == 1 ? "has" : "have")} no internal escape stair, only this external one - {book} ({Cite("3.31a", "3.70a")}) allows an external stair as one of the escape routes only where an internal escape stair serves every part of each storey, plant left out");
        }

        if (!volume2)
        {
            var high = levels.Where(level => level.Elevation - ground > Weather + 0.5).Select(level => $"the {level.Name} {(level.Elevation - ground) / 1000:0.0} m up").ToList();
            if (high.Count > 0)
                wanting.Add($"it serves {string.Join(", ", high)} - Approved Document B Volume 1 ({(anyInternal ? "3.70b" : "3.69a")}) allows an external escape stair to flats serving a floor 6 m above the ground at most");
        }

        // Who may use it: in an assembly building not the public (3.31b); in a residential (institutional) one only offices and staff accommodation (3.31c).
        if (volume2 && group == PurposeGroup.AssemblyAndRecreation)
        {
            var public_ = stair.Doors.Where(onto => onto.Room is { } room && !Named(room, "office", "staff", "kitchen", "store", "plant", "workshop", "dressing", "back")).Select(onto => onto.Room!.Name).Distinct().ToList();
            if (public_.Count > 0)
                wanting.Add($"its door from the {string.Join(", the ", public_)} - a part the public use, as read by its name - {book} (3.31b) allows an external escape stair in an assembly and recreation building only for a route the public are not to use");
        }
        else if (volume2 && group == PurposeGroup.ResidentialInstitutional)
        {
            var others = stair.Doors.Where(onto => onto.Room is { } room && !Named(room, "office", "staff")).Select(onto => onto.Room!.Name).Distinct().ToList();
            if (others.Count > 0)
                wanting.Add($"its door from the {string.Join(", the ", others)} - {book} (3.31c) allows an external escape stair in a residential (institutional) building only from offices or staff accommodation");
        }

        // Its doors E 30 with closers, but a single door onto the top landing of a stair leading down from it (3.32a, 3.71a).
        var topLevel = levels[^1];
        var downward = stair.Top > ground + 100 && Math.Abs(topLevel.Elevation - stair.Top) <= AtGround;
        var exempt = downward && stair.Doors.Count(onto => onto.Level.Id == topLevel.Id) == 1 ? stair.Doors.First(onto => onto.Level.Id == topLevel.Id).Door : null;
        var poorList = stair.Doors.Where(onto => onto.Door != exempt && document.FindType<DoorType>(onto.Door.TypeId) is { } type && (Doors.FireMinutes(type.FireRating) < 30 || !type.SelfClosing)).ToList();
        var poorDoors = poorList.Select(onto => $"the {onto.Level.Name}'s, {document.FindType<DoorType>(onto.Door.TypeId)!.Name}").Distinct().ToList();
        if (poorDoors.Count > 0)
            wanting.Add($"its door{(poorList.Count == 1 ? string.Empty : "s")} - {string.Join("; ", poorDoors)} - not E 30 with a closer - {book} ({Cite("3.32a", "3.71a")}) asks the doors to an external escape stair E 30 and self-closing, but a single door onto the top landing of a stair leading down, the only door onto it");

        // The building's outside walls within 1800 of its flights and landings - beside and over them, 9 m below, 1100 over the top landing but for a
        // stair up from a basement to the ground - REI 30, their doors E 30 and their windows fixed shut and E 30 (3.32b, e; 3.71b, d).
        var fromBasement = stair.Bottom < ground - Escape.Basement - 0.5 && Math.Abs(stair.Top - ground) <= AtGround;
        var (low, high_) = (stair.Bottom - Below, stair.Top + (fromBasement ? 0 : OverTop));
        var walls = new List<string>();
        var openings = new List<string>();
        foreach (var wall in document.Walls)
        {
            if (document.GetWallType(wall) is not { Function: WallFunction.Exterior } type || document.FindLevel(wall.LevelId) is not { } level) continue;
            var (from, to) = (level.Elevation, level.Elevation + wall.UnconnectedHeight);
            if (to <= low + 1 || from >= high_ - 1 || to <= ground + 100) continue;
            var half = type.Structure.TotalWidth / 2;
            double Gap(Point2D a, Point2D b) => stair.Steps.Min(step => Enumerable.Range(0, step.Outline.Count).Min(i => Between(a, b, step.Outline[i], step.Outline[(i + 1) % step.Outline.Count]))) - half;
            if (Gap(wall.Start, wall.End) > Zone + 0.5) continue;
            if (Doors.FireMinutes(type.FireRating) < 30) walls.Add($"{type.Name} on the {level.Name}");
            foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id && door != exempt))
            {
                if (document.FindType<DoorType>(door.TypeId) is not { } doorType || Doors.FireMinutes(doorType.FireRating) >= 30) continue;
                var (a, b) = (wall.PointAt(type.Structure, door.DistanceAlongWall - doorType.Width / 2, 0), wall.PointAt(type.Structure, door.DistanceAlongWall + doorType.Width / 2, 0));
                if (Gap(a, b) <= Zone + 0.5) openings.Add($"the {doorType.Name} door on the {level.Name}, not E 30");
            }

            foreach (var window in document.All<Window>().Where(window => window.HostWallId == wall.Id))
            {
                if (document.FindType<WindowType>(window.TypeId) is not { } windowType) continue;
                var fixed_ = windowType.Operation == WindowOperation.Fixed;
                var rated = Doors.FireMinutes(windowType.FireRating) >= 30;
                if (fixed_ && rated) continue;
                var (a, b) = (wall.PointAt(type.Structure, window.DistanceAlongWall - windowType.Width / 2, 0), wall.PointAt(type.Structure, window.DistanceAlongWall + windowType.Width / 2, 0));
                if (Gap(a, b) <= Zone + 0.5)
                    openings.Add($"the {windowType.Name} window on the {level.Name}, {(rated ? "opening" : fixed_ ? "not E 30" : "opening and not E 30")}");
            }
        }

        if (walls.Count > 0 || openings.Count > 0)
            wanting.Add($"the building beside it is not fire-resisting: {string.Join("; ", new[] { walls.Count > 0 ? $"its walls {string.Join(", ", walls.Distinct())} less than REI 30" : null, openings.Count > 0 ? string.Join("; ", openings.Distinct()) : null }.OfType<string>())} - {book} ({Cite("3.32b, e, Diagram 3.4", "3.71b, d, Diagram 3.11")}) asks the building's envelope within 1800 of an external escape stair's flights and landings{(volume2 ? ", over them," : string.Empty)} from 9 m below up to 1100 over its top landing REI 30, its doors E 30 fire doorsets and its glazing fixed shut and E 30");

        // Over 6 m high, protected from the weather (3.32d, 3.71e).
        var height = volume2 ? stair.Top - stair.Bottom : stair.Top - Math.Max(stair.Bottom, ground);
        var centre = new Point2D(stair.Steps.Average(step => step.Outline.Average(point => point.X)), stair.Steps.Average(step => step.Outline.Average(point => point.Y)));
        if (height > Weather + 0.5 && Cavities.Soffit(document, centre, stair.Top) is null && Cavities.Soffit(document, stair.Head, stair.Top) is null)
            wanting.Add($"it is {height / 1000:0.0} m high with nothing over it - {book} ({Cite("3.32d", "3.71e")}) asks an external escape stair more than 6 m high protected from the weather so that snow and ice cannot build up on it - a roof or canopy over it, not full enclosure");

        // Its flights and landings A2-s3, d2 but between the ground and a floor or flat roof 6 m up or down at most (3.24d, 3.85d).
        var short_ = Math.Abs(stair.Bottom - ground) <= AtGround && stair.Top - ground <= Weather + 0.5 || Math.Abs(stair.Top - ground) <= AtGround && ground - stair.Bottom <= Weather + 0.5;
        if (!short_)
        {
            var poor = stair.Stairs.Select(each => document.FindType<StairType>(each.TypeId)).OfType<StairType>().Distinct()
                .Select(type => (Type: type, Class: VerticalEscape.ClassOf(document, type)))
                .Where(entry => entry.Class.Class is not { } known || !known.AtLeast(Euroclass.A2s3d2))
                .Select(entry => $"{entry.Type.Name}, {entry.Class.Of}{(entry.Class.Class is { } known ? $" {known}" : " of no class given")}").ToList();
            if (poor.Count > 0)
                wanting.Add($"its flights are {string.Join("; ", poor)} - {book} ({Cite("3.24d", "3.85d")}) asks an external escape stair's flights and landings of class A2-s3, d2 or better, but one between the ground and a floor or flat roof 6 m up or down at most");
        }

        // As wide as Table 3.1 asks of any escape stair (Volume 2).
        if (volume2 && stair.Width < 800 - 0.5)
            wanting.Add($"it is {stair.Width:0} wide - {book} (3.10b, Table 3.1) asks an escape stair 800 wide at least");

        return wanting;
    }

    // --- Protected stairways at an angle in the outside wall (Volume 2 3.29, 3.30; Volume 1 3.66, 3.67) -------------------------------

    /// <summary>An opening in an outside wall: what it is, its room, its storey, its ends on the wall's line, its middle and which way is out.</summary>
    private sealed record Opening(string What, Room Room, Guid Level, Wall Wall, Point2D A, Point2D B, Point2D Middle, Vector2D Outward);

    /// <summary>The unprotected areas of the outside walls: windows and doors of less than 30 minutes, each with the room inside it.</summary>
    private static List<Opening> Unprotected(BimDocument document)
    {
        var found = new List<Opening>();
        void Add(Wall wall, WallType type, double along, double width, string rating, string what)
        {
            if (Doors.FireMinutes(rating) >= 30) return;
            var reach = type.Structure.TotalWidth / 2 + 150;
            var (a, b) = (wall.PointAt(type.Structure, along, reach), wall.PointAt(type.Structure, along, -reach));
            var (inA, inB) = (RoomsOf.Containing(document, wall.LevelId, a), RoomsOf.Containing(document, wall.LevelId, b));
            if (inA is not null == (inB is not null)) return;
            var (room, inside, outside) = inA is not null ? (inA, a, b) : (inB!, b, a);
            var outward = new Vector2D(outside.X - inside.X, outside.Y - inside.Y);
            var length = Math.Sqrt(outward.X * outward.X + outward.Y * outward.Y);
            found.Add(new Opening(what, room, wall.LevelId, wall, wall.PointAt(type.Structure, along - width / 2, 0), wall.PointAt(type.Structure, along + width / 2, 0),
                wall.PointAt(type.Structure, along, 0), new Vector2D(outward.X / length, outward.Y / length)));
        }

        foreach (var wall in document.Walls)
        {
            if (document.GetWallType(wall) is not { Function: WallFunction.Exterior } type) continue;
            foreach (var window in document.All<Window>().Where(window => window.HostWallId == wall.Id))
                if (document.FindType<WindowType>(window.TypeId) is { } windowType)
                    Add(wall, type, window.DistanceAlongWall, windowType.Width, windowType.FireRating, $"{windowType.Name} window");
            foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id))
                if (document.FindType<DoorType>(door.TypeId) is { } doorType)
                    Add(wall, type, door.DistanceAlongWall, doorType.Width, doorType.FireRating, $"{doorType.Name} door");
        }

        return found;
    }

    /// <summary>Whether two walls lie on one line: parallel within 5 degrees, the one's middle within 100 of the other's line.</summary>
    private static bool OneLine(Wall one, Wall other)
    {
        var (u, v) = (one.End - one.Start, other.End - other.Start);
        var (lu, lv) = (Math.Sqrt(u.X * u.X + u.Y * u.Y), Math.Sqrt(v.X * v.X + v.Y * v.Y));
        if (lu < 1 || lv < 1) return false;
        var sine = Math.Abs(u.X * v.Y - u.Y * v.X) / (lu * lv);
        return sine < Math.Sin(5 * Math.PI / 180) && Line2D.DistanceFromSegment(other.Start.MidpointTo(other.End), one.Start - u * 1000, one.End + u * 1000) < 100;
    }

    /// <summary>
    /// A protected stairway's unprotected areas within 1800 of the building's where its outside wall projects, is recessed or meets the building's
    /// at an internal angle - the two facing each other across the angle - said on the stair's room (Volume 2 3.30, Volume 1 3.67).
    /// </summary>
    public static IEnumerable<EscapeProblem> Faces(BimDocument document)
    {
        var (volume2, flats) = Rules(document);
        if (!volume2 && !flats) yield break;
        var stairs = Doors.StairRooms(document).Where(room => Dwellings.Of(room).Length == 0 && !HorizontalEscape.OpenStairRoom(document, room)).ToHashSet();
        if (stairs.Count == 0) yield break;
        var openings = Unprotected(document);
        var said = new Dictionary<Room, List<string>>();
        foreach (var own in openings.Where(opening => stairs.Contains(opening.Room)))
        foreach (var other in openings.Where(opening => opening.Level == own.Level && !stairs.Contains(opening.Room) && !OneLine(own.Wall, opening.Wall)))
        {
            var across = new Vector2D(other.Middle.X - own.Middle.X, other.Middle.Y - own.Middle.Y);
            if (across.X * own.Outward.X + across.Y * own.Outward.Y <= 0 || -(across.X * other.Outward.X + across.Y * other.Outward.Y) <= 0) continue;
            var gap = Between(own.A, own.B, other.A, other.B);
            if (gap >= Zone - 0.5) continue;
            if (!said.TryGetValue(own.Room, out var list)) said[own.Room] = list = new List<string>();
            list.Add($"its {own.What} on the {document.FindLevel(own.Level)?.Name} {gap / 1000:0.0} m from the {other.Room.Name}'s {other.What}");
        }

        foreach (var (room, list) in said)
            yield return new EscapeProblem(room, $"{string.Join("; ", list.Distinct())}, facing each other across an angle in the outside wall - Approved Document B {(volume2 ? "Volume 2 (3.29, 3.30, Diagram 3.3)" : "Volume 1 (3.66, 3.67, Diagram 3.10)")} asks 1800 between an unprotected area of a protected stairway's enclosure and one of the building's where the stairway projects, is recessed or stands in an internal angle: make one fire-resisting, or move it");
    }
}
