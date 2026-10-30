using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Documents;

/// <summary>What a warning is about: the model itself, the building regulations, or the services.</summary>
public enum WarningKind
{
    /// <summary>The model drawn wrong: walls one over another, the same thing twice in one place, a room nothing encloses.</summary>
    Model,

    /// <summary>What the project's standard asks that the building falls short of.</summary>
    Regulations,

    /// <summary>What a system of pipes, ducts or wiring falls short of.</summary>
    Services
}

/// <summary>One thing wrong: what kind, its heading among the others like it, what is said of it, and what it is about.</summary>
public sealed record Warning(WarningKind Kind, string Heading, string Said, IReadOnlyList<Element> Elements);

/// <summary>
/// Everything wrong with the model in one list, as the warnings a drawing program keeps: walls
/// drawn over one another, the same thing twice in one place - counted twice in schedules - a room
/// that nothing encloses or that shares its space with another, marks used twice, a door with no
/// wall, an element whose type is gone; then every rule of the project's standard the building
/// falls short of, storey by storey - each fitting's clear space, each door, window, wall, floor,
/// roof edge, chimney, footing and column, each stair, ramp, railing and beam, each room as a
/// bedroom, a kitchen, a bathroom, for its windows, its extract and its alarms, the site - and
/// what each system of services falls short of. Each is what that element's own Properties say.
/// </summary>
public static class Warnings
{
    /// <summary>Walls whose centre lines lie within this of each other, along this much of both, are drawn over one another.</summary>
    private const double Overlapping = 10;

    /// <summary>Every warning in the model: its own, then the regulations', then the services'.</summary>
    public static IReadOnlyList<Warning> Of(BimDocument document)
    {
        using var kept = ModelCache.Begin(document);
        var found = new List<Warning>();
        found.AddRange(ModelProblems(document));
        found.AddRange(Regulations(document));
        found.AddRange(MepSystems.Problems(document)
            .Select(problem => new Warning(WarningKind.Services, Heading(problem.Element), problem.Text, new[] { problem.Element })));
        return found;
    }

    /// <summary>What a warning about an element goes under: its kind of element.</summary>
    private static string Heading(Element element) => EnumText.Humanise(element.Category);

    private static string NameOf(BimDocument document, Element element) => Coordination.Comparisons.NameOf(document, element);

    // ---- the model --------------------------------------------------------------------

    /// <summary>The model drawn wrong.</summary>
    public static IEnumerable<Warning> ModelProblems(BimDocument document)
    {
        foreach (var warning in Duplicates(document)) yield return warning;
        foreach (var warning in OverlappingWalls(document)) yield return warning;
        foreach (var warning in RoomProblems(document)) yield return warning;
        foreach (var warning in DuplicateMarks(document)) yield return warning;

        foreach (var opening in document.All<Opening>().Where(opening => document.Find<Wall>(opening.HostWallId) is null))
            yield return new Warning(WarningKind.Model, "Doors and windows with no wall", $"{NameOf(document, opening)} is in no wall: the wall it was in has gone. Delete it, or Pick New Host.", new Element[] { opening });

        foreach (var element in document.Elements.Where(element => element.TypeId != Guid.Empty && document.FindType<ElementType>(element.TypeId) is null && element is not Group))
            yield return new Warning(WarningKind.Model, "Elements with no type", $"{NameOf(document, element)} on {LevelName(document, element)} is of a type the project no longer has: give it one in Properties.", new[] { element });
    }

    private static string LevelName(BimDocument document, Element element) => document.FindLevel(element.LevelId)?.Name ?? "no storey";

    /// <summary>
    /// The same thing twice in one place: of one kind and one type, on one storey, in one design
    /// option, set out by the same points - a door at the same place along a wall where that wall,
    /// or one over it, runs - at the same height. Schedules and quantities count it twice.
    /// </summary>
    private static IEnumerable<Warning> Duplicates(BimDocument document)
    {
        static long Round(double value) => (long)Math.Round(value);

        string? Place(Element element)
        {
            if (Phasing.IsAnnotation(element.Category) || element is Group or MepSystem or Coordination.ModelLink) return null;
            if (element is Opening opening)
            {
                var along = $"at {Round(opening.DistanceAlongWall)}, {Round(opening.SillHeight)} up";
                return document.Find<Wall>(opening.HostWallId) is { } host
                    ? $"in {Round(host.Start.X)},{Round(host.Start.Y)}-{Round(host.End.X)},{Round(host.End.Y)} {along}"
                    : $"in {opening.HostWallId} {along}";
            }

            var points = ElementTransforms.PlanPoints(element);
            if (points.Count == 0) return null;
            var height = element switch
            {
                Wall wall => $"{Round(wall.GetBaseElevation(document))}-{Round(wall.GetHeight(document))}",
                _ => string.Empty
            };
            return string.Join(";", points.Select(point => $"{Round(point.X)},{Round(point.Y)}").Order(StringComparer.Ordinal)) + "|" + height;
        }

        var alike = document.Elements
            .Select(element => (Element: element, Place: Place(element)))
            .Where(entry => entry.Place is not null)
            .GroupBy(entry => (entry.Element.GetType(), entry.Element.TypeId, entry.Element.LevelId, entry.Element.DesignOptionId, entry.Place));
        foreach (var group in alike.Where(group => group.Count() > 1))
        {
            var elements = group.Select(entry => entry.Element).ToList();
            var what = document.FindType<ElementType>(elements[0].TypeId)?.Name ?? EnumText.Humanise(elements[0].Category);
            yield return new Warning(WarningKind.Model, "Identical instances in the same place",
                $"{elements.Count} {what} in the same place on {LevelName(document, elements[0])}: schedules and quantities count each one. Delete all but one.", elements);
        }
    }

    /// <summary>
    /// Straight walls drawn over one another on a storey: their centre lines within
    /// <see cref="Overlapping"/> of each other along more than that of both, at heights that
    /// meet. A room is bounded by one of them only, and both are counted. Not a curtain wall
    /// set in a wall, which is meant to be.
    /// </summary>
    private static IEnumerable<Warning> OverlappingWalls(BimDocument document)
    {
        foreach (var storey in document.All<Wall>()
                     .Where(wall => wall.Bulge == 0 && wall.Ellipse is null && wall.Length > Overlapping && !document.IsCurtainWall(wall))
                     .GroupBy(wall => (wall.LevelId, wall.DesignOptionId)))
        {
            var walls = storey.Select(wall => (Wall: wall, Bottom: wall.GetBaseElevation(document), Top: wall.GetBaseElevation(document) + wall.GetHeight(document))).ToList();
            for (var i = 0; i < walls.Count; i++)
            for (var j = i + 1; j < walls.Count; j++)
            {
                var (a, b) = (walls[i], walls[j]);
                if (a.Top <= b.Bottom + 1 || b.Top <= a.Bottom + 1) continue;

                var along = a.Wall.End - a.Wall.Start;
                var length = along.Length;
                var unit = along * (1 / length);
                var other = b.Wall.End - b.Wall.Start;
                if (Math.Abs(unit.X * other.Y - unit.Y * other.X) / other.Length > 0.002) continue;

                // How far off a's line b lies, and how much of a's length it runs along.
                double Off(Point2D point) => Math.Abs(unit.X * (point.Y - a.Wall.Start.Y) - unit.Y * (point.X - a.Wall.Start.X));
                if (Off(b.Wall.Start) > Overlapping || Off(b.Wall.End) > Overlapping) continue;
                double At(Point2D point) => unit.X * (point.X - a.Wall.Start.X) + unit.Y * (point.Y - a.Wall.Start.Y);
                var (from, to) = (Math.Min(At(b.Wall.Start), At(b.Wall.End)), Math.Max(At(b.Wall.Start), At(b.Wall.End)));
                var shared = Math.Min(length, to) - Math.Max(0, from);
                if (shared <= Overlapping) continue;

                yield return new Warning(WarningKind.Model, "Walls overlap",
                    $"{NameOf(document, a.Wall)} and {NameOf(document, b.Wall)} on {LevelName(document, a.Wall)} are drawn over one another for {Units.FormatLength(shared)}: rooms are bounded by one of them only, and both are counted. Delete or shorten one.",
                    new Element[] { a.Wall, b.Wall });
            }
        }
    }

    /// <summary>A room that nothing encloses, and two rooms in one enclosed space.</summary>
    private static IEnumerable<Warning> RoomProblems(BimDocument document)
    {
        var rooms = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).ToList();
        foreach (var (room, _) in rooms.Where(entry => !entry.Boundary.IsEnclosed))
            yield return new Warning(WarningKind.Model, "Rooms not enclosed",
                $"{RoomsOf.Label(room)} on {LevelName(document, room)} is not in a properly enclosed space: it has no area. Close the walls round it, or add a room separator, or move it.",
                new Element[] { room });

        foreach (var storey in rooms.Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).GroupBy(entry => (entry.Room.LevelId, entry.Room.DesignOptionId)))
        {
            var placed = storey.ToList();
            var said = new HashSet<Guid>();
            foreach (var (room, boundary) in placed)
            {
                if (said.Contains(room.Id)) continue;
                var sharing = placed.Where(other => !ReferenceEquals(other.Room, room) && !said.Contains(other.Room.Id)
                                                    && Polygon2D.Contains(boundary.Polygon, other.Room.Location)
                                                    && Polygon2D.Contains(other.Boundary.Polygon, room.Location)).Select(other => other.Room).ToList();
                if (sharing.Count == 0) continue;

                var all = sharing.Prepend(room).ToList();
                foreach (var each in all) said.Add(each.Id);
                yield return new Warning(WarningKind.Model, "Rooms in the same space",
                    $"{string.Join(", ", all.Select(RoomsOf.Label))} on {LevelName(document, room)} are in the same enclosed space: its area is counted {all.Count} times. Delete all but one, or divide the space with a wall or a room separator.",
                    all);
            }
        }
    }

    /// <summary>Marks used twice among elements of one kind, and room numbers used twice on the project.</summary>
    private static IEnumerable<Warning> DuplicateMarks(BimDocument document)
    {
        foreach (var group in document.Elements
                     .Where(element => !string.IsNullOrWhiteSpace(element.Mark) && !Phasing.IsAnnotation(element.Category) && element is not Group)
                     .GroupBy(element => (element.Category, Mark: element.Mark.Trim().ToUpperInvariant()))
                     .Where(group => group.Count() > 1))
            yield return new Warning(WarningKind.Model, "Marks used twice",
                $"{group.Count()} {EnumText.Humanise(group.Key.Category).ToLowerInvariant()} are marked {group.First().Mark.Trim()}: a schedule cannot tell them apart. Give each its own mark.",
                group.ToList());

        foreach (var group in document.All<Room>()
                     .Where(room => !string.IsNullOrWhiteSpace(room.Number))
                     .GroupBy(room => (room.DesignOptionId, Number: room.Number.Trim().ToUpperInvariant()))
                     .Where(group => group.Count() > 1))
            yield return new Warning(WarningKind.Model, "Room numbers used twice",
                $"{group.Count()} rooms are numbered {group.First().Number.Trim()}: {string.Join(", ", group.Select(room => $"{room.Name} on {LevelName(document, room)}"))}. Give each its own number.",
                group.ToList<Element>());
    }

    // ---- the regulations ---------------------------------------------------------------

    /// <summary>Every rule of the project's standard the building falls short of, as each element's Properties say it.</summary>
    public static IEnumerable<Warning> Regulations(BimDocument document)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        Warning For(Element element, string said) => new(WarningKind.Regulations, Heading(element), said, new[] { element });
        string Named(Element element, string said) => $"{NameOf(document, element)}: {said}";

        // Storey by storey: each fitting's clear space and placing, and each door, window, wall, floor, roof edge, chimney, footing and column.
        foreach (var level in document.Levels)
        foreach (var blocked in Clearances.Blocked(document, level.Id, standard))
            yield return For(blocked.Zone.Element, blocked.Message);

        foreach (var stair in document.All<Stair>())
        foreach (var problem in Stairs.Problems(document, stair))
            yield return For(stair, Named(stair, problem));

        foreach (var ramp in document.All<Ramp>())
        foreach (var problem in Ramps.Problems(document, ramp))
            yield return For(ramp, Named(ramp, problem));

        foreach (var railing in document.All<Railing>())
        foreach (var problem in Railings.Problems(document, railing))
            yield return For(railing, Named(railing, problem));

        foreach (var beam in document.All<Beam>())
        foreach (var problem in Beams.Problems(document, beam))
            yield return For(beam, Named(beam, problem));

        // Openings over a drop, their guarding; a curtain wall's the same.
        foreach (var opening in document.All<Opening>())
        foreach (var problem in OpeningGuarding.Problems(document, opening))
            yield return For(opening, Named(opening, problem));

        foreach (var wall in document.All<Wall>().Where(document.IsCurtainWall))
        foreach (var problem in OpeningGuarding.CurtainProblems(document, wall))
            yield return For(wall, Named(wall, problem));

        // The site.
        foreach (var region in document.All<GroundRegion>())
        foreach (var problem in GroundRegions.Problems(document, region))
            yield return For(region, Named(region, problem));

        foreach (var pad in document.All<SitePad>())
        foreach (var problem in SitePads.Problems(document, pad))
            yield return For(pad, Named(pad, problem));

        foreach (var line in document.All<PropertyLine>())
        foreach (var problem in PropertyLines.Problems(document, line))
            yield return For(line, Named(line, problem));

        foreach (var surface in document.All<Toposurface>())
        foreach (var problem in Terrains.Problems(document, surface))
            yield return For(surface, Named(surface, problem));

        // Each room: as a bedroom, a kitchen, a bathroom, for its windows, its extract and its alarms.
        foreach (var room in document.All<Room>())
        foreach (var problem in RoomShortfalls(document, room))
            yield return For(room, $"{RoomsOf.Label(room)}: {problem}");
    }

    /// <summary>
    /// What a room falls short of as each of its Properties says it - the clauses of each verdict
    /// that cite what a rule asks; not those that say it is met.
    /// </summary>
    public static IEnumerable<string> RoomShortfalls(BimDocument document, Room room)
    {
        if (Bedrooms.Verdict(document, room) is { } bedroom && !bedroom.EndsWith("big enough", StringComparison.Ordinal))
            yield return bedroom;

        foreach (var verdict in new[] { Kitchens.Verdict(document, room), Bathrooms.Verdict(document, room), Architecture.Services.Verdict(document, room), Architecture.Alarms.Verdict(document, room), WindowRules.Verdict(document, room) })
        {
            if (verdict is null) continue;

            // "Kitchen: a; b": what it is taken as, then what is said of it, clause by clause.
            var colon = verdict.IndexOf(": ", StringComparison.Ordinal);
            var (what, said) = colon is > 0 and < 24 ? (verdict[..colon], verdict[(colon + 2)..]) : (string.Empty, verdict);
            foreach (var clause in said.Split("; ").Where(Asks))
                yield return what.Length > 0 ? $"{what} - {clause}" : clause;
        }
    }

    /// <summary>Whether a clause cites what a rule asks: how every shortfall is said, and nothing that is met.</summary>
    private static bool Asks(string clause) => clause.Contains(" asks", StringComparison.Ordinal);
}
