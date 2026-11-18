using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Where a room-sealed boiler's flue comes out of the wall, and what it is kept from, as Approved Document J has it: a gas one's - a fanned
/// balanced flue, as a modern boiler's is - by the table to Diagram 34; an oil one's - its burner a pressure jet - by the table to Diagram 41.
///
/// A boiler hung on an outside wall, room-sealed, takes its flue straight out through the wall behind it: its outlet is taken on the wall's
/// outside face at the boiler's top. It is checked against the openings in the wall it comes out of and the walls in line with it - windows
/// that open, doors, air vents - below, beside and above them; the run's ends, its corners; the ground under it; the eaves over it; a wall or
/// the boundary in front of it; a downpipe beside it; and the other outlets on the same wall. Not under the US's codes.
/// </summary>
public static class FlueOutlets
{
    /// <summary>A flue's outlet on a wall: its boiler, the wall, where it is in plan on the outside face and how high above its level, the way out, and whether it burns oil.</summary>
    public sealed record Outlet(Component Boiler, ComponentType Type, Wall Wall, Point2D At, double Height, Vector2D Outward, bool Oil);

    /// <summary>The distances of a table, mm: below, above and beside an opening; under eaves; from a corner or the boundary alongside; above the ground; from a surface or the boundary facing it; from a downpipe; from another outlet facing it, above or below it, and beside it on the same wall.</summary>
    private sealed record Distances(string Table, double Below, double Above, double Beside, double Eaves, double Corner, double Ground, double Facing, double Downpipe, double FacingOutlet, double Vertical, double Horizontal);

    /// <summary>The table to Diagram 34, a fanned balanced flue's column: A, B, C, E, H, I, J, G, K, M and N.</summary>
    private static readonly Distances Gas = new("the table to Diagram 34", 300, 300, 300, 200, 300, 300, 600, 150, 1200, 1500, 300);

    /// <summary>The table to Diagram 41, a pressure jet burner's column: A, none above, B, D (unprotected eaves), F, G, H, none for a rainwater pipe, J, K and L.</summary>
    private static readonly Distances Oil = new("the table to Diagram 41", 600, 0, 600, 600, 300, 300, 600, 0, 1200, 1500, 750);

    /// <summary>The outlet of a boiler's flue on the wall it hangs on; null where it is not room-sealed on an outside wall.</summary>
    public static Outlet? Of(BimDocument document, Component boiler)
    {
        if (document.FindType<ComponentType>(boiler.TypeId) is not { Form: ComponentForm.Boiler } type || type.FlueOf != ApplianceFlue.RoomSealed) return null;
        if (!boiler.IsHosted || document.Find<Wall>(boiler.HostId) is not { } wall || document.GetWallType(wall) is not { } wallType || wall.Length < 1) return null;
        var outward = boiler.Facing * -1;
        var centre = Line2D.ClosestPointOnSegment(boiler.Location, wall.Start, wall.End);
        var at = centre + outward * (wallType.Structure.TotalWidth / 2);
        var beyond = at + outward * 300;
        if (document.All<Room>().Any(room => room.LevelId == wall.LevelId && room.GetBoundary(document) is { IsEnclosed: true } boundary && Polygon2D.Contains(boundary.Polygon, beyond))) return null;
        return new Outlet(boiler, type, wall, at, boiler.Elevation + type.Height, outward, type.BurnsOil);
    }

    /// <summary>Every boiler's flue outlet on a wall.</summary>
    public static IReadOnlyList<Outlet> All(BimDocument document) =>
        document.All<Component>().Select(boiler => Of(document, boiler)).OfType<Outlet>().ToList();

    /// <summary>What is wanting in where flues come out.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        var outlets = All(document);
        foreach (var outlet in outlets)
            foreach (var said in Wanting(document, outlet, outlets))
                yield return new MepProblem(outlet.Boiler, said);
    }

    /// <summary>The walls in line with a wall on its level - one run of a face - and where along the run each starts, from the first wall's start.</summary>
    private static List<Wall> Run(BimDocument document, Wall wall)
    {
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var across = new Vector2D(-along.Y, along.X);
        bool OnLine(Point2D point) => Math.Abs((point - wall.Start).Dot(across)) < 100;
        return document.Walls.Where(other => other.LevelId == wall.LevelId && other.Length > 1 && OnLine(other.Start) && OnLine(other.End)).ToList();
    }

    private static IEnumerable<string> Wanting(BimDocument document, Outlet outlet, IReadOnlyList<Outlet> outlets)
    {
        var table = outlet.Oil ? Oil : Gas;
        var kind = outlet.Oil ? "an oil boiler's flue" : "a fanned room-sealed gas flue";
        var rule = $"Approved Document J ({table.Table})";
        var wall = outlet.Wall;
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        double Along(Point2D point) => (point - wall.Start).Dot(along);
        var run = Run(document, wall);
        var here = Along(outlet.At);
        var z = outlet.Height;

        // The openings in the run: windows that open, doors, air vents - each its span along the run and its height.
        var openings = new List<(string Name, double From, double To, double Bottom, double Top)>();
        foreach (var each in run)
        {
            var eachAlong = (each.End - each.Start).NormalisedOrDefault(Vector2D.UnitX);
            double On(double distance) => Along(each.Start + eachAlong * distance);
            foreach (var window in document.All<Window>().Where(window => window.HostWallId == each.Id))
                if (document.FindType<WindowType>(window.TypeId) is { } type && Opens(type) && window.GetSpan(type) is var (from, to))
                    openings.Add(($"the {type.Name} window", Math.Min(On(from), On(to)), Math.Max(On(from), On(to)), window.SillHeight, window.SillHeight + window.HeightOf(type)));
            foreach (var door in document.All<Door>().Where(door => door.HostWallId == each.Id))
                if (document.FindType<DoorType>(door.TypeId) is { } type && door.GetSpan(type) is var (from, to))
                    openings.Add(($"the {type.Name} door", Math.Min(On(from), On(to)), Math.Max(On(from), On(to)), 0, type.Height));
            foreach (var vent in document.All<Component>().Where(piece => piece.IsHosted && piece.HostId == each.Id))
                if (document.FindType<ComponentType>(vent.TypeId) is { Form: ComponentForm.BackgroundVentilator } type)
                    openings.Add(($"the {type.Name}", Along(vent.Location) - type.Width / 2, Along(vent.Location) + type.Width / 2, vent.Elevation, vent.Elevation + type.Height));
        }

        foreach (var (name, from, to, bottom, top) in openings)
        {
            if (here < from - table.Beside + 0.5 || here > to + table.Beside - 0.5 || z < bottom - table.Below + 0.5 || z > top + table.Above - 0.5) continue;
            var (gap, where) = here >= from && here <= to
                ? z <= bottom ? (bottom - z, "below") : z >= top ? (z - top, "above") : (0.0, "in front of")
                : (Math.Min(Math.Abs(here - from), Math.Abs(here - to)), "beside");
            yield return $"its flue's outlet is {gap:0} mm {where} {name} - {rule} keeps {kind}'s outlet {table.Below:0} mm below an opening{(table.Above > 0 ? $", {table.Above:0} mm above one" : string.Empty)} and {table.Beside:0} mm beside one";
        }

        // The run's ends - a corner - and the ground.
        var ends = run.SelectMany(each => new[] { Along(each.Start), Along(each.End) }).ToList();
        var corner = Math.Min(Math.Abs(here - ends.Min()), Math.Abs(ends.Max() - here)) - document.GetWallType(wall)!.Structure.TotalWidth / 2;
        if (corner < table.Corner - 0.5)
            yield return $"its flue's outlet is {Math.Max(0, corner):0} mm from the corner of the wall - {rule} keeps {kind}'s outlet {table.Corner:0} mm from a corner";
        var level = document.FindLevel(wall.LevelId)?.Elevation ?? 0;
        var ground = Escape.Ground(document);
        var above = level + z - ground;
        if (above < table.Ground - 0.5)
            yield return $"its flue's outlet is {above:0} mm above the ground - {rule} keeps {kind}'s outlet {table.Ground:0} mm above the ground";

        // The eaves over it: a roof's underside where it overhangs the outlet.
        var eaves = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && Polygon2D.Contains(roof.Boundary, outlet.At + outlet.Outward * 50))
            .Select(roof => roof.UndersideAt(document, outlet.At + outlet.Outward * 50) - level - z).Where(gap => gap > -0.5).DefaultIfEmpty(double.PositiveInfinity).Min();
        if (eaves < table.Eaves - 0.5)
            yield return $"its flue's outlet is {eaves:0} mm below the eaves - {rule} keeps {kind}'s outlet {table.Eaves:0} mm below eaves{(outlet.Oil ? " - 75 mm where a heat shield 750 mm wide protects them" : string.Empty)}";

        // In front of it: a wall, or the boundary.
        var ray = outlet.At + outlet.Outward * table.Facing;
        var facing = document.Walls.Where(other => other.LevelId == wall.LevelId && !run.Contains(other) && other.Length > 1)
            .Select(other => Crossing(outlet.At, ray, other.Start, other.End)).OfType<double>().DefaultIfEmpty(double.PositiveInfinity).Min();
        if (facing < table.Facing - 0.5)
            yield return $"a wall stands {facing:0} mm in front of its flue's outlet - {rule} keeps {kind}'s outlet {table.Facing:0} mm from a surface facing it";
        var boundary = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2)
            .SelectMany(line => line.Boundary.Select((point, i) => Crossing(outlet.At, ray, point, line.Boundary[(i + 1) % line.Boundary.Count])))
            .OfType<double>().DefaultIfEmpty(double.PositiveInfinity).Min();
        if (boundary < table.Facing - 0.5)
            yield return $"the boundary is {boundary:0} mm in front of its flue's outlet - {rule} keeps {kind}'s outlet {table.Facing:0} mm from the boundary facing it";

        // A downpipe beside it, on the same face.
        if (table.Downpipe > 0)
        {
            var downpipe = document.All<Downpipe>().Where(pipe => Math.Abs((pipe.Location - outlet.At).Dot(outlet.Outward)) < 300)
                .Select(pipe => Math.Abs(Along(pipe.Location) - here)).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (downpipe < table.Downpipe - 0.5)
                yield return $"its flue's outlet is {downpipe:0} mm from a downpipe - {rule} keeps {kind}'s outlet {table.Downpipe:0} mm from a drainpipe or soil pipe";
        }

        // The other outlets on the same wall: above or below it, or beside it.
        foreach (var other in outlets.Where(other => !ReferenceEquals(other, outlet) && run.Contains(other.Wall) && Vector2DSame(other.Outward, outlet.Outward)))
        {
            var (dx, dz) = (Math.Abs(Along(other.At) - here), Math.Abs(other.Height + (document.FindLevel(other.Wall.LevelId)?.Elevation ?? 0) - level - z));
            if (dx < table.Horizontal - 0.5 && dz < table.Vertical - 0.5)
                yield return $"its flue's outlet is {dx:0} mm along and {dz:0} mm up or down from another on the same wall - {rule} keeps {kind}'s outlets {table.Vertical:0} mm apart one over another and {table.Horizontal:0} mm side by side";
        }
    }

    private static bool Vector2DSame(Vector2D a, Vector2D b) => a.Dot(b) > 0.99;

    /// <summary>Whether a window type opens - any of its lights - and so is an opening; a fixed light is not.</summary>
    private static bool Opens(WindowType type) =>
        WindowLights.Divided(type) ? WindowLights.Of(type).Any(light => light.Opens) : type.Operation != WindowOperation.Fixed;

    /// <summary>How far along a ray from its start a segment crosses it, mm; null where it does not.</summary>
    private static double? Crossing(Point2D start, Point2D end, Point2D a, Point2D b)
    {
        var (r, s) = (end - start, b - a);
        var denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-9) return null;
        var t = ((a.X - start.X) * s.Y - (a.Y - start.Y) * s.X) / denominator;
        var u = ((a.X - start.X) * r.Y - (a.Y - start.Y) * r.X) / denominator;
        return t is > 1e-9 and <= 1 && u is >= 0 and <= 1 ? t * r.Length : null;
    }
}
