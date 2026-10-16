using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a window gives and what the rules ask of it - and of the rooms it lights.
///
/// Its clear opening: through the frame, 60 each side, and by how it opens - a casement or a
/// tilt-and-turn its sash's whole light, a slider or a sash window half of it, a top- or
/// bottom-hung light only a gap, a fixed light nothing. An escape window, Approved Document B
/// asks (2.10), opens 0.33 m² clear, 450 by 450 at least, its bottom 1100 or less above the floor:
/// one in every habitable room on a storey up to 4.5 m above the ground with no protected stair
/// (2.2). The IRC asks for one in every bedroom (R310): 5.7 sq ft (0.53 m²) clear - 5.0 on the
/// ground floor - 24 in high, 20 in wide, its sill 44 in or less. Approved Document F asks a room
/// to open a twentieth of its floor area to purge its air - a tenth through windows opening only
/// 15 to 30 degrees (Table 1.4); the IRC its glass to be 8% of its floor area and an opening 4%
/// (R303.1).
///
/// Its glass: safety glass below 800 from the floor and within 300 of a door's edge below 1500
/// (Approved Document K, Diagram 5.1); the IRC's hazardous locations - within 24 in of a door's
/// edge below 60 in, a pane over 9 sq ft low and tall, within 60 in of a bath (R308.4). And its
/// U-value, 1.6 at most in a new dwelling (Approved Document L, Table 4.1).
/// </summary>
public static class WindowRules
{
    /// <summary>How wide a window's frame is all round, mm.</summary>
    public const double Frame = 60;

    /// <summary>
    /// The clear opening a window gives open - its width and height - and how far it opens: fully
    /// (a casement, a tilt-and-turn, a slider or sash), only part way (top- or bottom-hung, louvres),
    /// or not at all.
    /// </summary>
    public static (double Width, double Height, bool Fully, bool Opens) Opening(WindowType type)
    {
        var (width, height) = (Math.Max(0, type.Width - 2 * Frame), Math.Max(0, type.Height - 2 * Frame));
        return type.Operation switch
        {
            WindowOperation.Fixed => (0, 0, false, false),
            WindowOperation.Casement or WindowOperation.Bay when type.Width > 1300 => (width / 2 - 30, height, true, true),
            WindowOperation.Casement or WindowOperation.Bay or WindowOperation.TiltAndTurn => (width, height, true, true),
            WindowOperation.Sliding => (width / 2 - 30, height, true, true),
            WindowOperation.DoubleHung => (width, height / 2 - 30, true, true),
            _ => (width, height, false, true)
        };
    }

    /// <summary>
    /// The area a window opens, m², for a room's air: every sash of a casement or bay, half a slider
    /// or sash window, the whole of a top- or bottom-hung light or louvres.
    /// </summary>
    public static double OpenArea(WindowType type)
    {
        var (width, height) = (Math.Max(0, type.Width - 2 * Frame), Math.Max(0, type.Height - 2 * Frame));
        return type.Operation switch
        {
            WindowOperation.Fixed => 0,
            WindowOperation.Sliding or WindowOperation.DoubleHung => width * height / 2 / 1e6,
            _ => width * height / 1e6
        };
    }

    /// <summary>Its glass's area, m²: the light inside its frame.</summary>
    public static double GlassArea(WindowType type) => Math.Max(0, type.Width - 2 * Frame) * Math.Max(0, type.Height - 2 * Frame) / 1e6;

    /// <summary>Whether a window opens far enough to escape through and gives the clear opening asked, its bottom low enough.</summary>
    public static bool Escapes(WindowType type, double sill, bool us, bool ground)
    {
        var (width, height, fully, _) = Opening(type);
        if (!fully) return false;
        var bottom = sill + Frame;
        return us
            ? width * height >= (ground ? 464500 : 529500) && height >= 610 - 0.5 && width >= 508 - 0.5 && bottom <= 1118 + 0.5
            : width * height >= 330000 && width >= 450 - 0.5 && height >= 450 - 0.5 && bottom <= 1100 + 0.5;
    }

    /// <summary>
    /// Whether a roof window gives an escape: top-hung, so it stays open on its own, its opening
    /// inside its frame as big as a window's must be, and its bottom as low over the floor.
    /// </summary>
    public static bool Escapes(RoofWindowFrame frame, double floor, bool us, bool ground)
    {
        if (frame.Type.Operation != RoofWindowOperation.TopHung) return false;
        var width = frame.Type.Width - 2 * frame.Type.FrameWidth;
        var height = frame.Type.Height - 2 * frame.Type.FrameWidth;
        var bottom = frame.Corners.Min(frame.Top) - floor;
        return us
            ? width * height >= (ground ? 464500 : 529500) && height >= 610 - 0.5 && width >= 508 - 0.5 && bottom <= 1118 + 0.5
            : width * height >= 330000 && width >= 450 - 0.5 && height >= 450 - 0.5 && bottom <= 1100 + 0.5;
    }

    /// <summary>The roof windows over a room: their middles over it, in the roof over its storey.</summary>
    public static IReadOnlyList<RoofWindowFrame> RoofWindowsOver(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var floor = document.FindLevel(room.LevelId)?.Elevation ?? 0;
        var next = document.Levels.Where(level => level.Elevation > floor + 1).Select(level => level.Elevation).DefaultIfEmpty(floor + 6000).Min();
        return document.Elements.OfType<RoofWindow>()
            .Select(window => RoofWindows.Frame(document, window))
            .OfType<RoofWindowFrame>()
            .Where(frame => Polygon2D.Contains(outline, frame.Centre) && frame.Corners.Min(frame.Top) is var low && low > floor + 1 && low < next)
            .ToList();
    }

    /// <summary>The windows of a room - in its walls - with their types and sills, and the room's external doors.</summary>
    public static (List<(Window Window, WindowType Type, double Sill)> Windows, List<(Door Door, DoorType Type)> Doors) Of(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var windows = new List<(Window, WindowType, double)>();
        var doors = new List<(Door, DoorType)>();
        foreach (var opening in document.Elements.OfType<Opening>())
        {
            if (document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } wall || wall.LevelId != room.LevelId) continue;
            if (document.GetWallType(wall) is not { } wallType) continue;

            var reach = wallType.Structure.TotalWidth / 2 + 150;
            if (!new[] { reach, -reach }.Any(side => Polygon2D.Contains(outline, wall.PointAt(wallType.Structure, opening.DistanceAlongWall, side)))) continue;

            if (opening is Window window && document.FindType<WindowType>(window.TypeId) is { } type)
                windows.Add((window, type, window.Placed(document, type, wall).Sill));
            else if (opening is Door door && document.FindType<DoorType>(door.TypeId) is { } doorType && Architecture.Doors.External(document, door, doorType))
                doors.Add((door, doorType));
        }

        return (windows, doors);
    }

    /// <summary>What a window falls short of, as the project's standard asks: its glass, its U-value.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Window window, ClearanceStandard standard)
    {
        if (document.FindType<WindowType>(window.TypeId) is not { } type) return Array.Empty<PlacementProblem>();
        if (document.Walls.FirstOrDefault(wall => wall.Id == window.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType)
            return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var (from, to) = window.GetSpan(type);
        var (sill, height) = window.Placed(document, type, wall);
        var half = wallType.Structure.TotalWidth / 2;
        var outline = CurtainGeometry.Band(wall, wallType, from, to, half + 100, -half - 100);
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.Add(new PlacementProblem(window, said, rule, outline));

        // Its glass in a critical - a hazardous - location.
        if (!type.SafetyGlazing && GlassArea(type) > 0)
        {
            var glassBottom = sill + Frame;
            var nearDoor = document.Elements.OfType<Door>()
                .Where(door => door.HostWallId == wall.Id && document.FindType<DoorType>(door.TypeId) is not null)
                .Select(door => door.GetSpan(document.FindType<DoorType>(door.TypeId)!))
                .Select(span => Math.Max(0, Math.Max(span.From - to, from - span.To)))
                .DefaultIfEmpty(double.MaxValue)
                .Min();

            if (us)
            {
                var pane = GlassArea(type);
                var wet = document.Elements.OfType<Component>()
                    .Where(piece => piece.LevelId == wall.LevelId && document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.Bath or ComponentForm.Shower)
                    .Select(piece => Kitchens.Apart(ComponentModel.Footprint(document.FindType<ComponentType>(piece.TypeId)!, ComponentModel.FrameOf(piece, 0)), outline))
                    .DefaultIfEmpty(double.MaxValue)
                    .Min();
                if (nearDoor <= 610 && glassBottom < 1524)
                    Add($"its glass is {nearDoor:0} from a door's edge, {glassBottom:0} off the floor.", "IRC R308.4.2 makes glass within 24 in (610) of a door's edge and under 60 in (1524) up a hazardous location: safety glazing.");
                else if (pane > 0.836 && glassBottom < 457 && sill + height - Frame > 914)
                    Add($"its pane of {pane:0.0} m² comes down to {glassBottom:0} off the floor.", "IRC R308.4.3 makes a pane over 9 sq ft whose bottom is under 18 in (457) and top over 36 in a hazardous location: safety glazing.");
                else if (wet <= 1524 && glassBottom < 1524)
                    Add($"its glass is {wet:0} from a bath or shower, {glassBottom:0} off the floor.", "IRC R308.4.5 makes glass within 60 in (1524) of a bath or shower and under 60 in up a hazardous location: safety glazing.");
            }
            else if (glassBottom < 800)
                Add($"its glass comes down to {glassBottom:0} off the floor.", "Approved Document K (Diagram 5.1) makes glass below 800 a critical location: safety glass to BS EN 12600, or small panes.");
            else if (nearDoor <= 300 && glassBottom < 1500)
                Add($"its glass is {nearDoor:0} from a door's edge, {glassBottom:0} off the floor.", "Approved Document K (Diagram 5.1) makes glass within 300 of a door's edge and below 1500 a critical location: safety glass to BS EN 12600.");
        }

        if (!us && type.HeatTransferCoefficient > 1.6 + 0.005 && document.GetWallType(wall) is { Function: WallFunction.Exterior })
            Add($"its U-value is {type.HeatTransferCoefficient:0.0#}.", "Approved Document L (Table 4.1) limits a window in a new dwelling to a U-value of 1.6 W/m²K.");

        return problems;
    }

    /// <summary>What a window comes to as its rules see it - what it opens, and what it falls short of.</summary>
    public static string Verdict(BimDocument document, Window window)
    {
        if (document.FindType<WindowType>(window.TypeId) is not { } type) return string.Empty;
        var problems = Problems(document, window, document.ProjectInformation.ClearanceStandard);
        var (width, height, fully, opens) = Opening(type);
        var opening = !opens ? "Fixed" : fully ? $"Opens {width:0} x {height:0} clear ({OpenArea(type):0.00} m²)" : $"Opens part way ({OpenArea(type):0.00} m²)";
        return problems.Count == 0 ? opening : $"{opening} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }

    /// <summary>Every window's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Elements.OfType<Window>()
            .Where(window => document.Walls.FirstOrDefault(wall => wall.Id == window.HostWallId)?.LevelId == levelId)
            .SelectMany(window => Problems(document, window, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(window, "its place", problem.Outline, problem.Rule), string.Empty,
                    $"{document.FindType<WindowType>(window.TypeId)?.Name ?? "A window"}{(string.IsNullOrWhiteSpace(window.Mark) ? string.Empty : $" ({window.Mark})")}", problem.Said)))
            .ToList();

    /// <summary>
    /// What a room's windows come to - an escape window where one is asked for, the air it can be
    /// purged with, in the US the light it has - or null where it asks nothing of them.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var pieces = document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .OfType<ComponentType>()
            .ToList();

        // Not a habitable room: a bathroom, a WC, a garage.
        if (pieces.Any(type => Bathrooms.IsWc(type) || type.Form is ComponentForm.Bath or ComponentForm.Shower) || Architecture.Doors.Garage(document, room)) return null;

        var (windows, doors) = Of(document, room, boundary.Polygon);
        var floor = boundary.Area / 1e6;
        var level = document.FindLevel(room.LevelId);
        var ground = document.Levels.Count == 0 ? 0 : document.Levels.Min(other => other.Elevation);
        var above = (level?.Elevation ?? 0) - ground;
        var bedroom = pieces.Any(type => type.Form is ComponentForm.Bed or ComponentForm.BunkBed);
        var said = new List<string>();

        // An escape window.
        if (us ? bedroom : above > 1 && above <= 4500 + 0.5 && !Architecture.Doors.HasHighStorey(document))
        {
            var escapes = windows.Any(entry => Escapes(entry.Type, entry.Sill, us, above <= 1))
                || RoofWindowsOver(document, room, boundary.Polygon).Any(frame => Escapes(frame, level?.Elevation ?? 0, us, above <= 1));
            said.Add(escapes
                ? "an escape window"
                : us
                    ? "no escape window - IRC R310.2 asks for one in every bedroom: 5.7 sq ft (0.53 m²) clear, 24 in high, 20 in wide, its sill 44 in (1118) up at most"
                    : "no escape window - Approved Document B (2.2, 2.10) asks for one in every habitable room up here: 0.33 m² clear, 450 by 450, its bottom 1100 up at most");
        }

        // Its air - and, in the US, its light.
        var open = windows.Sum(entry => OpenArea(entry.Type)) + doors.Sum(entry => entry.Type.Width * entry.Type.Height / 1e6);
        var wide = windows.Where(entry => Opening(entry.Type).Fully).Sum(entry => OpenArea(entry.Type)) + doors.Sum(entry => entry.Type.Width * entry.Type.Height / 1e6);
        if (us)
        {
            var glass = windows.Sum(entry => GlassArea(entry.Type)) + doors.Where(entry => Architecture.Doors.Glazed(entry.Type)).Sum(entry => Math.Max(0, entry.Type.Width - 200) * Math.Max(0, entry.Type.Height - 200) / 1e6 * 0.6);
            if (glass < floor * 0.08 - 1e-6) said.Add($"glass {glass:0.00} m² - IRC R303.1 asks for 8% of its floor, {floor * 0.08:0.00} m²");
            if (open < floor * 0.04 - 1e-6) said.Add($"opens {open:0.00} m² - IRC R303.1 asks for 4% of its floor, {floor * 0.04:0.00} m²");
        }
        else if (wide < floor / 20 - 1e-6 && open < floor / 10 - 1e-6)
            said.Add($"opens {open:0.00} m² - Approved Document F (Table 1.4) asks for a twentieth of its floor, {floor / 20:0.00} m², to purge its air (a tenth through windows opening only part way)");

        return said.Count == 0 ? null : $"Windows: {string.Join("; ", said)}";
    }
}
