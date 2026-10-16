using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The services - sockets, switches, controls, lights, radiators, fans - checked as the
/// regulations check them.
///
/// Where they are on the wall: Approved Document M puts switches and sockets with their middles
/// 450 to 1200 off the floor and 300 or more from an inside corner (M4(2) 2.30b), heating
/// controls 900 to 1200 (2.30e), the consumer unit's switches 1350 to 1450 (1.18b) - for a
/// wheelchair user, 700 to 1000 and 700 from a corner (M4(3) 3.44b); the ADA within its reach
/// ranges, 15 to 48 in (381 to 1219; 308). A radiator 50 or more under the sill over it, as its
/// makers ask; an extract fan within 400 of the ceiling (Approved Document F 1.20b); lamps of 75
/// lumens a watt at least (Approved Document L 6.58).
///
/// And room by room: the extract a kitchen, a bathroom, a WC or a utility room needs (Approved
/// Document F Table 1.1: 30 l/s with a cooker hood or 60 without, 15, 6 or a window to open, 30;
/// the IRC's M1505.4.4: 100 cfm in a kitchen, 50 in a bathroom or a window of 3 sq ft, R303.3);
/// and in the US, a light in every room (NEC 210.70) and a receptacle within 6 ft of every point
/// along its walls (NEC 210.52(A)).
/// </summary>
public static class Services
{
    /// <summary>
    /// The heights a fitting's middle may be at, least and most, above the floor it is on, and
    /// how far it must be from an inside corner - with the rule asking it - or null where the
    /// standard asks nothing of it.
    /// </summary>
    public static (double Least, double Most, double Corner, string Rule)? Reach(ComponentType type, ClearanceStandard standard)
    {
        var accessory = type.Form is ComponentForm.Socket or ComponentForm.Switch or ComponentForm.ShaverSocket;
        var control = type.Form == ComponentForm.Thermostat;
        var board = type.Form == ComponentForm.ConsumerUnit;
        if (!accessory && !control && !board) return null;

        return standard switch
        {
            ClearanceStandard.Irc => null,
            ClearanceStandard.Ada when board => null,
            ClearanceStandard.Ada => (381, 1219, 0, "ADA 308 keeps what is reached for within 15 to 48 in (381 to 1219) of the floor."),
            _ when board => (1350, 1450, 0, "Approved Document M (1.18b) puts a consumer unit's switches 1350 to 1450 above the floor."),
            ClearanceStandard.ApprovedDocumentM3 => (700, 1000, 700, "Approved Document M (M4(3) 3.44b) puts switches, sockets and controls 700 to 1000 above the floor, 700 from an inside corner."),
            _ when control => (900, 1200, 300, "Approved Document M (M4(2) 2.30e) puts heating controls 900 to 1200 above the floor, 300 from an inside corner."),
            _ => (450, 1200, 300, "Approved Document M (M4(2) 2.30b) puts switches and sockets 450 to 1200 above the floor, 300 from an inside corner.")
        };
    }

    /// <summary>
    /// How far a fitting on a wall is from an inside corner: along the wall's face either way from
    /// its edge to where another wall stands in front of it. Null for one not on a wall, or with
    /// no corner within a metre.
    /// </summary>
    public static double? FromCorner(BimDocument document, Component piece, ComponentType type)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == piece.HostId) is not { } host || document.GetWallType(host) is not { } hostType) return null;

        var others = document.Walls
            .Where(wall => wall != host && wall.LevelId == host.LevelId && document.GetWallType(wall) is { } t && t.Structure.TotalWidth > 0)
            .Select(wall =>
            {
                var t = document.GetWallType(wall)!;
                var half = t.Structure.TotalWidth / 2;
                return CurtainGeometry.Band(wall, t, 0, wall.Length, half, -half);
            })
            .ToList();

        var (along, _) = host.Locate(hostType.Structure, piece.Location);
        var face = (piece.FlipFacing ? -1 : 1) * (hostType.Structure.TotalWidth / 2 + 10);
        double? nearest = null;
        foreach (var way in new[] { -1, 1 })
            for (var reach = type.Width / 2; reach <= type.Width / 2 + 1000; reach += 5)
            {
                var at = host.PointAt(hostType.Structure, along + way * reach, face);
                if (!others.Any(band => Polygon2D.Contains(band, at))) continue;
                nearest = Math.Min(nearest ?? double.MaxValue, reach - type.Width / 2);
                break;
            }

        return nearest;
    }

    /// <summary>
    /// The height of the ceiling over a point on a storey: a ceiling's underside, or the floor's
    /// on the storey above; null where neither is over it.
    /// </summary>
    public static double? CeilingOver(BimDocument document, Guid levelId, Point2D at)
    {
        if (document.Elements.OfType<Ceiling>().Where(ceiling => ceiling.LevelId == levelId && ceiling.Contains(at))
                .Select(ceiling => (double?)ceiling.GetBottomElevation(document)).Min() is { } ceiling)
            return ceiling;

        var level = document.FindLevel(levelId);
        var above = document.Levels.Where(other => level is not null && other.Elevation > level.Elevation + 1).OrderBy(other => other.Elevation).FirstOrDefault();
        return above is null ? null : document.Elements.OfType<Floor>()
            .Where(floor => floor.LevelId == above.Id && floor.Contains(at))
            .Select(floor => (double?)floor.GetBottomElevation(document))
            .Min();
    }

    /// <summary>What is wrong with where one service fitting is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type) return Array.Empty<PlacementProblem>();

        var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
        var problems = new List<PlacementProblem>();

        if (Reach(type, standard) is var (least, most, corner, rule))
        {
            var middle = piece.Elevation + type.Height / 2;
            if (middle < least - 0.5 || middle > most + 0.5)
                problems.Add(new PlacementProblem(piece, $"its middle is {middle:0} above the floor.", rule, footprint));
            if (corner > 0 && FromCorner(document, piece, type) is { } apart && apart < corner - 0.5)
                problems.Add(new PlacementProblem(piece, $"it is {apart:0} from an inside corner.", rule, footprint));
        }

        switch (type.Form)
        {
            case ComponentForm.Radiator or ComponentForm.TowelRail when UnderWindow(document, piece, type) is { } sill:
            {
                var top = ComponentHosting.BaseElevation(document, piece, type) + type.Height;
                if (top > sill - 50 + 0.5)
                    problems.Add(new PlacementProblem(piece, $"its top is {sill - top:0} under the sill over it.",
                        "Radiator makers ask for 50 at least between a radiator's top and a sill or shelf over it, for its warm air to rise.", footprint));
                break;
            }

            case ComponentForm.ExtractFan when !standard.IsUs() && CeilingOver(document, piece.LevelId, piece.Location) is { } ceiling:
            {
                var top = ComponentHosting.BaseElevation(document, piece, type) + type.Height;
                if (ceiling - top > 400 + 0.5)
                    problems.Add(new PlacementProblem(piece, $"its top is {ceiling - top:0} under the ceiling.",
                        "Approved Document F (1.20b) puts an extract fan no more than 400 under the ceiling, where the moist air gathers.", footprint));
                break;
            }
        }

        if (!standard.IsUs() && type.Kind == BuiltInCategory.LightingFixtures && type.Efficacy > 0 && type.Efficacy < 75 - 0.05)
            problems.Add(new PlacementProblem(piece, $"its lamps give {type.Efficacy:0} lumens a watt.",
                "Approved Document L (6.58) asks for lamps of at least 75 light source lumens per circuit-watt.", footprint));

        return problems;
    }

    /// <summary>The height of the sill of a window in the wall a radiator is on, over it; null where there is none.</summary>
    private static double? UnderWindow(BimDocument document, Component piece, ComponentType type)
    {
        if (document.Walls.FirstOrDefault(wall => wall.Id == piece.HostId) is not { } wall || document.GetWallType(wall) is not { } wallType) return null;

        var (along, _) = wall.Locate(wallType.Structure, piece.Location);
        var (from, to) = (along - type.Width / 2, along + type.Width / 2);
        var bottom = wall.GetBaseElevation(document);
        return document.Elements.OfType<Window>()
            .Where(window => window.HostWallId == wall.Id && document.FindType<WindowType>(window.TypeId) is not null)
            .Select(window => (Window: window, Span: window.GetSpan(document.FindType<WindowType>(window.TypeId)!)))
            .Where(entry => entry.Span.From < to - 1 && entry.Span.To > from + 1)
            .Select(entry => (double?)(bottom + entry.Window.Placed(document, document.FindType<WindowType>(entry.Window.TypeId), wall).Sill))
            .Min();
    }

    /// <summary>Every service fitting's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "A fitting", problem.Said)))
            .ToList();

    /// <summary>What a room has in it, by type, and where.</summary>
    private static List<(Component Piece, ComponentType Type)> In(BimDocument document, Room room, IReadOnlyList<Point2D> outline) =>
        document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location))
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)))
            .Where(entry => entry.Type is not null)
            .Select(entry => (entry.Piece, entry.Type!))
            .ToList();

    /// <summary>The windows in a room's walls that open, and their areas, m².</summary>
    private static IReadOnlyList<double> OpeningWindows(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var areas = new List<double>();
        foreach (var window in document.Elements.OfType<Window>())
        {
            if (document.FindType<WindowType>(window.TypeId) is not { } type || type.Operation == WindowOperation.Fixed) continue;
            if (document.Walls.FirstOrDefault(wall => wall.Id == window.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } wallType) continue;

            var reach = wallType.Structure.TotalWidth / 2 + 150;
            if (!new[] { reach, -reach }.Any(side => Polygon2D.Contains(outline, wall.PointAt(wallType.Structure, window.DistanceAlongWall, side)))) continue;
            areas.Add(type.Width * window.Placed(document, type, wall).Height / 1e6);
        }

        return areas;
    }

    /// <summary>
    /// What a room comes to for its services - the extract it has against what it needs, and in the
    /// US its lights and its wall receptacles - or null for a room asking nothing of them.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var pieces = In(document, room, boundary.Polygon);
        bool Has(params ComponentForm[] forms) => pieces.Any(entry => forms.Contains(entry.Type.Form));

        var kitchen = Has(ComponentForm.Sink, ComponentForm.Cooker);
        var bathroom = Has(ComponentForm.Bath, ComponentForm.Shower);
        var wc = pieces.Any(entry => Bathrooms.IsWc(entry.Type));
        var utility = !kitchen && Has(ComponentForm.WashingMachine, ComponentForm.TumbleDryer);
        var hood = pieces.Where(entry => entry.Type.Form == ComponentForm.CookerHood).Sum(entry => entry.Type.ExtractRate);
        var fans = pieces.Where(entry => entry.Type.Form == ComponentForm.ExtractFan).Sum(entry => entry.Type.ExtractRate);
        var windows = OpeningWindows(document, room, boundary.Polygon);

        var said = new List<string>();
        string? Extract(double wanted, string what, string rule, bool window = false)
        {
            var has = what == "kitchen" ? fans + hood : fans;
            if (has >= wanted - 0.05) return $"extract {has:0} l/s";
            if (window) return "a window to open";
            return $"extract {has:0} l/s - {rule}";
        }

        if (us)
        {
            // The IRC: a bathroom's 50 cfm, or a window of 3 sq ft half of which opens; a kitchen's 100 cfm.
            if (kitchen) said.Add(Extract(47.2, "kitchen", "the IRC (M1505.4.4) asks for 100 cfm (47 l/s) from a kitchen")!);
            else if (bathroom || wc) said.Add(Extract(23.6, "bathroom", "the IRC (M1505.4.4) asks for 50 cfm (24 l/s) from a bathroom, or a window of 3 sq ft (R303.3)", windows.Any(area => area >= 0.28))!);
        }
        else if (kitchen)
        {
            said.Add(hood > 0
                ? Extract(30, "kitchen", "Approved Document F (Table 1.1) asks for 30 l/s from a kitchen with a cooker hood")!
                : Extract(60, "kitchen", "Approved Document F (Table 1.1) asks for 60 l/s from a kitchen with no cooker hood")!);
        }
        else if (bathroom) said.Add(Extract(15, "bathroom", "Approved Document F (Table 1.1) asks for 15 l/s from a bathroom")!);
        else if (wc) said.Add(Extract(6, "WC", "Approved Document F (Table 1.1) asks for 6 l/s from a WC, or a window to open", windows.Count > 0)!);
        else if (utility) said.Add(Extract(30, "utility room", "Approved Document F (Table 1.1) asks for 30 l/s from a utility room")!);

        if (us)
        {
            if (!pieces.Any(entry => entry.Type.Kind == BuiltInCategory.LightingFixtures))
                said.Add("no light - NEC 210.70 asks for a lighting outlet in every habitable room, kitchen and bathroom");

            if (!bathroom && !wc && ShortWallSpaces(document, room, boundary.Polygon, pieces) is > 0 and var spaces)
                said.Add($"{spaces} wall {(spaces == 1 ? "space" : "spaces")} with a point more than 6 ft (1829) from a receptacle - NEC 210.52(A) asks for none");
        }

        return said.Count == 0 ? null : $"Services: {string.Join("; ", said)}";
    }

    /// <summary>
    /// How many of a room's wall spaces - its walls between doorways, 610 (2 ft) long or more -
    /// have a point along them more than 1829 (6 ft) from a receptacle in that space, measured
    /// along the wall as NEC 210.52(A) measures, round its corners.
    /// </summary>
    public static int ShortWallSpaces(BimDocument document, Room room, IReadOnlyList<Point2D> outline, IReadOnlyList<(Component Piece, ComponentType Type)>? pieces = null)
    {
        // The room's outline as one line round it, and how far along it each point is.
        var edges = Enumerable.Range(0, outline.Count).Select(i => (A: outline[i], B: outline[(i + 1) % outline.Count])).ToList();
        var starts = new double[edges.Count];
        for (var i = 1; i < edges.Count; i++) starts[i] = starts[i - 1] + edges[i - 1].A.DistanceTo(edges[i - 1].B);
        var perimeter = starts[^1] + edges[^1].A.DistanceTo(edges[^1].B);
        if (perimeter < 1) return 0;

        // How far along the line round the room a point is, where it is within reach of it.
        double? Along(Point2D point, double within)
        {
            var best = (Distance: double.MaxValue, At: 0.0);
            for (var i = 0; i < edges.Count; i++)
            {
                var (a, b) = edges[i];
                var length = a.DistanceTo(b);
                if (length < 1e-6) continue;
                var t = Math.Clamp((point - a).Dot(b - a) / (length * length), 0, 1);
                var distance = point.DistanceTo(a + (b - a) * t);
                if (distance < best.Distance) best = (distance, starts[i] + t * length);
            }

            return best.Distance <= within ? best.At : null;
        }

        // The doorways in it break it into wall spaces.
        var breaks = new List<(double From, double To)>();
        foreach (var door in document.Elements.OfType<Door>())
        {
            if (document.Walls.FirstOrDefault(wall => wall.Id == door.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } wallType || document.FindType<DoorType>(door.TypeId) is not { } doorType) continue;

            var (from, to) = door.GetSpan(doorType);
            var reach = wallType.Structure.TotalWidth / 2 + 50;
            var ends = new[] { from, to }
                .Select(at => new[] { reach, -reach }.Select(side => Along(wall.PointAt(wallType.Structure, at, side), 100)).FirstOrDefault(found => found is not null))
                .ToList();
            if (ends[0] is { } one && ends[1] is { } other)
                breaks.Add(Math.Abs(other - one) <= perimeter / 2 ? (Math.Min(one, other), Math.Max(one, other)) : (Math.Max(one, other), Math.Min(one, other) + perimeter));
        }

        pieces ??= In(document, room, outline);
        var receptacles = pieces.Where(entry => entry.Type.Form == ComponentForm.Socket)
            .Select(entry => Along(entry.Piece.Location, 300))
            .OfType<double>()
            .ToList();

        // Walk round the room in steps; a point counts where it is not in a doorway.
        bool InDoorway(double at) => breaks.Any(gap => (at >= gap.From && at <= gap.To) || (at + perimeter >= gap.From && at + perimeter <= gap.To));
        // Each space's points as how far along it they are, so a distance is measured along it and
        // never across a doorway.
        const double step = 50;
        var spaces = new List<List<(double At, double Walked)>>();
        List<(double At, double Walked)>? current = null;
        var startAt = breaks.Count == 0 ? 0 : breaks[0].To % perimeter + 1;
        for (var walked = 0.0; walked < perimeter; walked += step)
        {
            var at = (startAt + walked) % perimeter;
            if (InDoorway(at)) { current = null; continue; }
            if (current is null) spaces.Add(current = new List<(double, double)>());
            current.Add((at, walked));
        }

        double Between(double a, double b) => Math.Min(Math.Abs(a - b), perimeter - Math.Abs(a - b));
        var shortSpaces = 0;
        foreach (var space in spaces.Where(space => space.Count * step >= 610))
        {
            var positions = receptacles
                .Select(position => space.Where(point => Between(point.At, position) <= step).Select(point => (double?)point.Walked).FirstOrDefault())
                .OfType<double>()
                .ToList();
            if (space.Any(point => positions.Count == 0 || positions.Min(position => Math.Abs(point.Walked - position)) > 1829 + step)) shortSpaces++;
        }

        return shortSpaces;
    }
}
