using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Doors, windows and glass in an outside wall over a drop, as guarding (Approved Document K,
/// section 3, and BS 6180). A door opening onto nothing wants a Juliet balcony across it, 1100
/// high from the floor. A window whose sill is under 800 from the floor wants guarding across it
/// to 800 where it opens; where it is fixed, its glass is the guarding, and is to be laminated -
/// as is the glass low in a curtain wall over a drop, and an empty bay in one is guarded like a
/// door. Under the IRC, a window that opens, its sill under 24 in from the floor and 72 in over
/// the ground outside, wants an opening control device (R312.2), and a door onto a drop of more
/// than 30 in a guard 36 in high.
/// </summary>
public static class OpeningGuarding
{
    /// <summary>The height from the floor under which a window over a drop is guarded, mm.</summary>
    public const double WindowGuard = 800;

    /// <summary>
    /// How far it drops beside a wall at a point along it, from the floor it stands on - its foot
    /// - to the balcony, deck, terrace, floor or roof there, or the ground: on whichever side
    /// drops further, as an outside wall drawn either way round, or glass at the edge of a floor
    /// over a void inside, has its drop on one side and its floor on the other.
    /// </summary>
    public static double Drop(BimDocument document, Wall wall, double along) => DropSide(document, wall, along).Drop;

    /// <summary>The drop beside a wall at a point along it, as <see cref="Drop"/>, and which side it is on: across toward its exterior, or away.</summary>
    public static (double Drop, double Side) DropSide(BimDocument document, Wall wall, double along)
    {
        if (document.GetWallType(wall) is not { } body) return (0, 1);

        var floor = wall.GetBaseElevation(document);
        var lowest = document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);
        double DropOn(double side)
        {
            var beside = wall.PointAt(body.Structure, Math.Clamp(along, 0, wall.Length), side * (body.Structure.TotalWidth / 2 + 600));
            var below = document.Elements.OfType<Slab>()
                .Where(slab => slab is not Ceiling && slab is not Roof { IsExtrusion: true } && slab.Boundary.Count >= 3 && slab.Contains(beside))
                .Select(slab => FloorSurfaces.TopAt(document, slab, beside))
                .Where(top => top <= floor + Stairs.FloorReach)
                .Select(top => (double?)top)
                .Max();
            return floor - (below ?? Terrains.GroundAt(document, beside) ?? lowest);
        }

        var (outward, inward) = (DropOn(1), DropOn(-1));
        return outward >= inward ? (outward, 1) : (inward, -1);
    }

    /// <summary>
    /// The railing guarding a stretch of a wall - a Juliet balcony on an opening there, or any
    /// railing running across it outside or in, within 600 of its face, from one side to the other.
    /// </summary>
    public static Railing? GuardAcross(BimDocument document, Wall wall, double from, double to, Opening? opening = null)
    {
        if (document.GetWallType(wall) is not { } body) return null;

        // A railing that guards it runs across it from side to side, within 600 of its face, on the storey it is on.
        var floor = wall.GetBaseElevation(document);
        var (first, last) = (Math.Min(from + 50, (from + to) / 2), Math.Max(to - 50, (from + to) / 2));
        var along = (wall.PointAt(body.Structure, last, 0) - wall.PointAt(body.Structure, first, 0)).NormalisedOrDefault(Vector2D.UnitX);
        bool Spans(Railing railing, Point2D a, Point2D b)
        {
            var lengths = Railings.Lengths(railing);
            bool Covered(Point2D point) => lengths.Any(length =>
                Line2D.DistanceFromSegment(point, length.From, length.To) < 600
                && Math.Abs((Line2D.ClosestPointOnSegment(point, length.From, length.To) - point).Dot(along)) < 60);
            return Covered(a) && Covered(b) && Math.Abs(Railings.GuardedFrom(document, railing, a.MidpointTo(b)) - floor) < Stairs.FloorReach;
        }

        var face = body.Structure.TotalWidth / 2 + 100;
        foreach (var side in new[] { 1.0, -1.0 })
        {
            var (a, b) = (wall.PointAt(body.Structure, first, side * face), wall.PointAt(body.Structure, last, side * face));

            // Its own Juliet balcony first - so long as it is still across it, the opening not moved away from under it.
            var railings = document.Elements.OfType<Railing>().OrderByDescending(railing => opening is not null && railing.HostOpeningId == opening.Id);
            if (railings.FirstOrDefault(railing => Spans(railing, a, b)) is { } found) return found;
        }

        return null;
    }

    /// <summary>What about a door or window over a drop falls short as guarding - or nothing.</summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Opening opening)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } wall || document.FindType<OpeningType>(opening.TypeId) is not { } type)
            return Array.Empty<string>();

        var us = document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada;
        var drop = Drop(document, wall, opening.DistanceAlongWall);
        var (from, to) = (opening.DistanceAlongWall - opening.WidthOf(type) / 2, opening.DistanceAlongWall + opening.WidthOf(type) / 2);
        var guarded = GuardAcross(document, wall, from, to, opening) is not null;

        if (opening is Door)
        {
            if (guarded || opening.SillHeight > 600 || drop <= (us ? 762 : 600)) return Array.Empty<string>();
            return new[]
            {
                us ? $"It opens onto a drop of {drop:0} with nothing outside it: the IRC asks for a guard 914 (36 in) high across it."
                   : $"It opens onto a drop of {drop:0} with nothing outside it: Approved Document K asks for guarding across it 1100 high from the floor - a Juliet balcony."
            };
        }

        var opens = type is WindowType { Operation: not WindowOperation.Fixed };
        var sill = opening.SillHeight;
        if (us)
        {
            if (!opens || guarded || sill >= 610 || drop <= 1829) return Array.Empty<string>();
            return new[] { $"It opens, its sill {sill:0} above the floor and {drop:0} over the ground outside: the IRC asks for a window opening control device, or a guard across it, where a window is less than 610 (24 in) above the floor and more than 1829 (72 in) up (R312.2)." };
        }

        if (guarded || sill >= WindowGuard - 0.5 || drop <= 600) return Array.Empty<string>();
        if (opens)
            return new[] { $"It opens, its sill {sill:0} above the floor over a drop of {drop:0}: Approved Document K asks for guarding across it to 800 from the floor - a Juliet balcony or a rail - or a restrictor holding it all but shut." };
        return type is WindowType window && window.GlazingType.Contains("lamin", StringComparison.OrdinalIgnoreCase)
            ? Array.Empty<string>()
            : new[] { $"Its sill is {sill:0} above the floor over a drop of {drop:0}: its glass below 800 is the guarding, and Approved Document K asks for it to be made as guarding - toughened laminated glass (BS 6180) - or a rail across it." };
    }

    /// <summary>
    /// What about a curtain wall over a drop falls short as guarding: clear glass below 800, an
    /// empty bay, or a door opening onto nothing - or nothing.
    /// </summary>
    public static IReadOnlyList<string> CurtainProblems(BimDocument document, Wall wall)
    {
        if (!document.IsCurtainWall(wall) || CurtainLayout.Of(document, wall) is not { } layout) return Array.Empty<string>();

        var us = document.ProjectInformation.ClearanceStandard == ClearanceStandard.Ada;
        var limit = us ? 762.0 : 600.0;
        var (low, empty, doors, drop) = (0, 0, 0, 0.0);
        foreach (var cell in layout.Cells)
        {
            var here = Drop(document, wall, (cell.From + cell.To) / 2);
            if (here <= limit || GuardAcross(document, wall, cell.From, cell.To) is not null) continue;

            if (cell.Kind == CurtainPanelKind.Door) doors++;
            else if (cell.Kind == CurtainPanelKind.Empty && cell.Bottom < 1100) empty++;
            else if (!us && cell.Kind == CurtainPanelKind.Glazed && cell.Bottom < WindowGuard && cell.Glass is not (CurtainGlass.Laminated or CurtainGlass.Spandrel)) low++;
            else continue;
            drop = Math.Max(drop, here);
        }

        var problems = new List<string>();
        if (low > 0)
            problems.Add($"{Count(low, "pane")} of its glass below 800, over a drop of {drop:0}, {(low == 1 ? "is" : "are")} the guarding: Approved Document K asks for it to be made as guarding - laminated glass (BS 6180) - or a rail across inside.");
        if (empty > 0)
            problems.Add($"{Count(empty, "bay")} of it {(empty == 1 ? "is" : "are")} left open over a drop of {drop:0}: {(us ? "the IRC asks for a guard 914 (36 in) high" : "Approved Document K asks for guarding 1100 high")} across {(empty == 1 ? "it" : "them")}.");
        if (doors > 0)
            problems.Add($"{Count(doors, "door")} in it open{(doors == 1 ? "s" : "")} onto a drop of {drop:0} with nothing outside: {(us ? "the IRC asks for a guard 914 (36 in) high" : "Approved Document K asks for a Juliet balcony 1100 high")} across {(doors == 1 ? "it" : "each")}.");
        return problems;
    }

    private static string Count(int count, string what) => count == 1 ? $"A {what}" : $"{count} {what}s";

    /// <summary>A door's or window's guarding, as its Properties say it.</summary>
    public static string Verdict(BimDocument document, Opening opening)
    {
        var problems = Problems(document, opening);
        return problems.Count > 0 ? string.Join(" ", problems) : Guarded(document, opening);
    }

    private static string Guarded(BimDocument document, Opening opening)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } wall) return "In no wall";
        var drop = Drop(document, wall, opening.DistanceAlongWall);
        if (drop <= 600) return "Not over a drop";
        var type = document.FindType<OpeningType>(opening.TypeId);
        var (from, to) = (opening.DistanceAlongWall - opening.WidthOf(type) / 2, opening.DistanceAlongWall + opening.WidthOf(type) / 2);
        return GuardAcross(document, wall, from, to, opening) is { } railing
            ? $"Guarded by a railing across it: {Railings.Verdict(document, railing)}"
            : $"Over a drop of {drop:0}, high enough not to need guarding";
    }

    /// <summary>A curtain wall's guarding, as its Properties say it.</summary>
    public static string CurtainVerdict(BimDocument document, Wall wall)
    {
        var problems = CurtainProblems(document, wall);
        if (problems.Count > 0) return string.Join(" ", problems);
        return Drop(document, wall, wall.Length / 2) > 600 ? "Over a drop: its glass low down is laminated, or guarded" : "Not over a drop";
    }
}
