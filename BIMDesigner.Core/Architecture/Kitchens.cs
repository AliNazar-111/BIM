using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A kitchen's layout, checked as kitchen planners and the codes check it.
///
/// The hob: not under a window that opens (the NKBA's Kitchen Planning Guideline 20); worktop
/// beside it - 300 each side in the UK, as kitchen planners and housing standards ask; 12 in
/// (305) one side and 15 in (381) the other in the US (Guideline 17); nothing that can burn hung
/// over it lower than 650 (hob makers' instructions) or, in the US, 30 in (762) - 24 in (610)
/// where a hood is over it (IRC M1901.1, G2447.5); its hood 650 to 750 over it and as wide as it
/// (Approved Document F 1.21 and Diagram 1.1) - 24 to 30 in in the US. In the US besides, the
/// NKBA's landing areas: 24 in (610) of worktop one side of a sink and 18 in (457) the other
/// (Guideline 11), 15 in (381) beside a fridge (Guideline 16), and the dishwasher within 36 in
/// (914) of the sink (Guideline 13).
///
/// And the kitchen as a whole, room by room: the work triangle between sink, hob and fridge -
/// 1.2 to 2.7 m a leg (4 to 9 ft) and 7.9 m (26 ft) in all at most (Guideline 3); in a wheelchair
/// user's dwelling, as long a worktop as Approved Document M's Table 3.3 asks for its bedspaces;
/// and with no hood over the hob, the extract Approved Document F (Table 1.1) or the IRC
/// (M1505.4.4) asks of the room instead.
/// </summary>
public static class Kitchens
{
    /// <summary>How far a hood's underside may be over the hob, least and most, mm.</summary>
    public static (double Least, double Most) HoodHeight(ClearanceStandard standard) => standard.IsUs() ? (610, 762) : (650, 750);

    /// <summary>
    /// The worktop beside a piece of a kitchen run on one side - its left (-1) or right (+1) as it
    /// faces you: the widths of the pieces along the run that side with a worktop the counting
    /// lets count, up to a gap, the end of the run, or a piece without one - a tall fridge, a larder.
    /// </summary>
    public static double Worktop(BimDocument document, Component piece, int side, Func<ComponentType, bool>? counts = null)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type) return 0;

        var across = piece.Across * side;
        var back = piece.Location - piece.Facing * (type.Depth / 2);
        var floor = ComponentHosting.BaseElevation(document, piece, type);
        var others = document.Elements.OfType<Component>()
            .Where(other => other != piece && other.LevelId == piece.LevelId && other.Facing.Dot(piece.Facing) > 0.999)
            .Select(other => (Piece: other, Type: document.FindType<ComponentType>(other.TypeId)))
            .Where(other => other.Type is not null && ComponentPlacing.InRun(other.Type)
                && Math.Abs(ComponentHosting.BaseElevation(document, other.Piece, other.Type) - floor) <= 50
                && Math.Abs((other.Piece.Location - other.Piece.Facing * (other.Type.Depth / 2) - back).Dot(piece.Facing)) <= 30)
            .ToList();

        var edge = type.Width / 2;
        var length = 0.0;
        while (true)
        {
            var next = others.FirstOrDefault(other =>
                Math.Abs((other.Piece.Location - piece.Location).Dot(across) - other.Type!.Width / 2 - edge) <= 5);
            if (next.Piece is null || !ComponentPlacing.HasWorktop(next.Type!) || counts?.Invoke(next.Type!) == false) return length;

            length += next.Type!.Width;
            edge += next.Type.Width;
            others.Remove(next);
        }
    }

    /// <summary>What is wrong with where one kitchen piece is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));
        var problems = new List<PlacementProblem>();
        static bool Landing(ComponentType other) => other.Form is not (ComponentForm.Sink or ComponentForm.Cooker);
        var (left, right) = (Worktop(document, piece, -1, Landing), Worktop(document, piece, 1, Landing));

        switch (type.Form)
        {
            case ComponentForm.Cooker:
            {
                if (OpeningWindowOver(document, piece, footprint) is { } window)
                    problems.Add(new PlacementProblem(piece, $"it stands under {window}, which opens.",
                        "The NKBA's Kitchen Planning Guideline 20 asks that a hob not stand under a window that opens: a draught over its flames, a blind or a curtain catching.", footprint));

                if (us ? Math.Min(left, right) < 305 - 0.5 || Math.Max(left, right) < 381 - 0.5 : Math.Min(left, right) < 300 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"the worktop beside it is {left:0} on its left and {right:0} on its right.",
                        us
                            ? "The NKBA's Kitchen Planning Guideline 17 asks for 12 in (305) of worktop one side of a cooktop and 15 in (381) the other."
                            : "Kitchen planners and housing standards ask for 300 of worktop each side of a hob, to set a pan down and keep its handle clear of a wall or a tall unit.",
                        footprint));

                var hob = ComponentHosting.BaseElevation(document, piece, type) + type.Height;
                var hoods = Over(document, piece, footprint).Where(over => over.Type.Form == ComponentForm.CookerHood).ToList();
                var (least, most) = HoodHeight(standard);
                foreach (var (over, overType, bottom) in Over(document, piece, footprint).Where(over => over.Type.Form != ComponentForm.CookerHood))
                {
                    var clear = us ? hoods.Count > 0 ? 610.0 : 762 : 650;
                    if (bottom - hob >= clear - 0.5 || bottom < hob) continue;
                    problems.Add(new PlacementProblem(piece, $"{overType.Name} over it is {bottom - hob:0} above the hob.",
                        us
                            ? $"IRC M1901.1 asks for 30 in (762) from a range to anything that can burn over it - 24 in (610) with a hood over it."
                            : "Hob makers ask that nothing that can burn hang closer than 650 over a hob.",
                        footprint));
                }

                foreach (var (_, hoodType, bottom) in hoods)
                {
                    if (bottom - hob < least - 0.5 || bottom - hob > most + 0.5)
                        problems.Add(new PlacementProblem(piece, $"its hood is {bottom - hob:0} over it.",
                            us
                                ? "Cooker hoods are hung 24 to 30 in (610 to 762) over a cooktop, as the IRC and their makers ask - low enough to catch its fumes."
                                : "Approved Document F (1.21) asks for a hood 650 to 750 over the hob where its maker gives no height.",
                            footprint));
                    if (!us && hoodType.Width < type.Width - 0.5)
                        problems.Add(new PlacementProblem(piece, $"its hood, {hoodType.Width:0} wide, is narrower than it.",
                            "Approved Document F (Diagram 1.1) asks for a cooker hood at least as wide as the hob.", footprint));
                }

                break;
            }

            case ComponentForm.Sink when us:
                if (!(Math.Min(left, right) >= 457 - 0.5 && Math.Max(left, right) >= 610 - 0.5))
                    problems.Add(new PlacementProblem(piece, $"the worktop beside it is {left:0} on its left and {right:0} on its right.",
                        "The NKBA's Kitchen Planning Guideline 11 asks for 24 in (610) of worktop one side of a sink and 18 in (457) the other.", footprint));
                break;

            case ComponentForm.Fridge when us && !ComponentModel.UnderCounter(type):
                if (Math.Max(left, right) < 381 - 0.5)
                    problems.Add(new PlacementProblem(piece, $"there is no worktop beside it - {Math.Max(left, right):0} at most.",
                        "The NKBA's Kitchen Planning Guideline 16 asks for 15 in (381) of worktop beside a fridge, to set things down from it.", footprint));
                break;

            case ComponentForm.Dishwasher when us:
            {
                var sinks = document.Elements.OfType<Component>()
                    .Where(other => other.LevelId == piece.LevelId && document.FindType<ComponentType>(other.TypeId)?.Form == ComponentForm.Sink)
                    .Select(other => ComponentModel.Footprint(document.FindType<ComponentType>(other.TypeId)!, ComponentModel.FrameOf(other, 0)))
                    .ToList();
                var nearest = sinks.Count == 0 ? double.MaxValue : sinks.Min(sink => Apart(sink, footprint));
                if (nearest > 914 + 0.5)
                    problems.Add(new PlacementProblem(piece, sinks.Count == 0 ? "there is no sink for it." : $"it is {nearest:0} from the sink.",
                        "The NKBA's Kitchen Planning Guideline 13 asks for a dishwasher within 36 in (914) of the sink.", footprint));
                break;
            }
        }

        return problems;
    }

    /// <summary>Every kitchen piece's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place in the kitchen", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "A fitting", problem.Said)))
            .ToList();

    /// <summary>What hangs over a piece - its type and the height of its underside - wall units, shelves, a hood.</summary>
    private static IEnumerable<(Component Piece, ComponentType Type, double Bottom)> Over(BimDocument document, Component piece, IReadOnlyList<Point2D> footprint)
    {
        foreach (var other in document.Elements.OfType<Component>().Where(other => other != piece && other.LevelId == piece.LevelId))
        {
            if (document.FindType<ComponentType>(other.TypeId) is not { } type || ComponentPlacing.InRun(type) && !type.IsHosted && type.Form != ComponentForm.WallUnit) continue;
            if (type.Kind is BuiltInCategory.LightingFixtures or BuiltInCategory.ElectricalFixtures or BuiltInCategory.LightingDevices) continue;

            var outline = ComponentModel.Footprint(type, ComponentModel.FrameOf(other, 0));
            if (PolygonBoolean.Combine(outline, footprint, BooleanOperation.Intersection).Sum(region => region.Area) <= 100 * 100) continue;
            yield return (other, type, ComponentHosting.BaseElevation(document, other, type));
        }
    }

    /// <summary>A window that opens over a hob - in a wall within 400 of it - by its type's name, or null.</summary>
    private static string? OpeningWindowOver(BimDocument document, Component piece, IReadOnlyList<Point2D> footprint)
    {
        foreach (var window in document.Elements.OfType<Window>())
        {
            if (document.FindType<WindowType>(window.TypeId) is not { } type || !WindowLights.Opens(type)) continue;
            if (document.Walls.FirstOrDefault(wall => wall.Id == window.HostWallId) is not { } wall || wall.LevelId != piece.LevelId) continue;
            if (document.GetWallType(wall) is not { } wallType) continue;

            var (from, to) = window.GetSpan(type);
            var reach = wallType.Structure.TotalWidth / 2 + 400;
            var band = CurtainGeometry.Band(wall, wallType, from, to, reach, -reach);
            if (PolygonBoolean.Combine(band, footprint, BooleanOperation.Intersection).Sum(region => region.Area) > 100 * 100)
                return string.IsNullOrWhiteSpace(window.Mark) ? $"the window {type.Name}" : $"window {window.Mark}";
        }

        return null;
    }

    /// <summary>How far apart two outlines are, edge to edge; nothing where they touch.</summary>
    internal static double Apart(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b)
    {
        static double ToEdge(IReadOnlyList<Point2D> polygon, Point2D point) =>
            Polygon2D.Contains(polygon, point) ? 0 : Enumerable.Range(0, polygon.Count).Min(i => Line2D.DistanceFromSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]));

        return Math.Min(a.Min(point => ToEdge(b, point)), b.Min(point => ToEdge(a, point)));
    }

    /// <summary>The bedspaces a project has, by its beds: two in a double or a bunk bed, one in a single.</summary>
    public static int Bedspaces(BimDocument document) =>
        document.Elements.OfType<Component>()
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .Sum(type => type?.Form switch
            {
                ComponentForm.BunkBed => 2,
                ComponentForm.Bed => type.Width >= 1200 ? 2 : 1,
                _ => 0
            });

    /// <summary>
    /// The worktop Approved Document M's Table 3.3 asks a wheelchair user's kitchen to have, with
    /// its fittings and appliances, for so many bedspaces.
    /// </summary>
    public static double WorktopFor(int bedspaces) => bedspaces switch
    {
        <= 2 => 4330,
        <= 4 => 4730,
        5 => 5630,
        _ => 6730
    };

    /// <summary>
    /// What a room comes to as a kitchen - its work triangle between sink, hob and fridge; in a
    /// wheelchair user's dwelling, its worktop's length; and its extract where no hood is over the
    /// hob - or null for a room without a sink or a hob in it.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var standard = document.ProjectInformation.ClearanceStandard;
        var pieces = document.Elements.OfType<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
            .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId)!))
            .Where(entry => entry.Type is not null)
            .ToList();

        Point2D? Front(ComponentForm form) => pieces.Where(entry => entry.Type.Form == form)
            .Select(entry => (Point2D?)(entry.Piece.Location + entry.Piece.Facing * (entry.Type.Depth / 2)))
            .FirstOrDefault();

        var (sink, hob, fridge) = (Front(ComponentForm.Sink), Front(ComponentForm.Cooker), Front(ComponentForm.Fridge));
        if (sink is null && hob is null) return null;

        var said = new List<string>();
        if (sink is { } s && hob is { } h && fridge is { } f)
        {
            var legs = new[] { ("sink to hob", s.DistanceTo(h)), ("hob to fridge", h.DistanceTo(f)), ("fridge to sink", f.DistanceTo(s)) };
            var total = legs.Sum(leg => leg.Item2);
            var odd = legs.Where(leg => leg.Item2 < 1200 - 0.5 || leg.Item2 > 2700 + 0.5).ToList();
            var triangle = $"work triangle {total / 1000:0.0} m ({string.Join(", ", legs.Select(leg => $"{leg.Item1} {leg.Item2 / 1000:0.0}"))})";
            said.Add(odd.Count == 0 && total <= 7900 + 0.5
                ? $"{triangle} - within 1.2 to 2.7 m a leg"
                : $"{triangle} - the NKBA's Kitchen Planning Guideline 3 asks for 1.2 to 2.7 m (4 to 9 ft) a leg and 7.9 m (26 ft) in all at most");
        }

        if (standard == ClearanceStandard.ApprovedDocumentM3 && Bedspaces(document) is > 0 and var bedspaces)
        {
            var worktop = pieces
                .Where(entry => ComponentPlacing.InRun(entry.Type) && entry.Type.Form is not (ComponentForm.WallUnit or ComponentForm.Shelving) && !entry.Type.IsHosted)
                .Sum(entry => entry.Type.Width);
            var wanted = WorktopFor(bedspaces);
            said.Add(worktop >= wanted - 0.5
                ? $"worktop {worktop / 1000:0.00} m - long enough for {bedspaces} bedspaces"
                : $"worktop {worktop / 1000:0.00} m - Approved Document M (M4(3)) Table 3.3 asks for {wanted / 1000:0.00} m for {bedspaces} bedspaces");
        }

        if (hob is not null && !pieces.Any(entry => entry.Type.Form == ComponentForm.CookerHood))
            said.Add(standard.IsUs()
                ? "no hood over the hob - the IRC (M1505.4.4) asks for 100 cfm (47 l/s) of kitchen exhaust"
                : "no hood over the hob - Approved Document F (Table 1.1) asks for 60 l/s of extract, 30 with a hood ducted outside");

        return said.Count == 0 ? "Kitchen" : $"Kitchen: {string.Join("; ", said)}";
    }
}
