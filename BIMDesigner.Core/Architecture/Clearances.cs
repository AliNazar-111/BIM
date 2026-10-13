using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using Area = BIMDesigner.Core.Views.ComponentModel.Area;

namespace BIMDesigner.Core.Architecture;

/// <summary>Whose rules say how much room a fitting needs in front of it.</summary>
public enum ClearanceStandard
{
    /// <summary>England's Approved Document M, Volume 1: Category 2 (M4(2)) accessible and adaptable dwellings.</summary>
    ApprovedDocumentM,

    /// <summary>The 2010 ADA Standards for Accessible Design.</summary>
    Ada
}

/// <summary>The room one fitting needs, in plan: what it is for, its outline, and the rule that asks for it.</summary>
public sealed record ClearanceZone(Component Component, string What, IReadOnlyList<Point2D> Outline, string Rule);

/// <summary>A zone with something in it: what is in the way.</summary>
public sealed record BlockedClearance(ClearanceZone Zone, string By, string FittingName)
{
    public string Message => $"{FittingName}: {Zone.What} is blocked by {By}. {Zone.Rule}";
}

/// <summary>
/// The clear space a WC, basin, bath, shower, bed or kitchen needs in front of or beside it -
/// as Approved Document M asks of an accessible and adaptable dwelling (M4(2)), or as the ADA
/// asks - and what is standing in it.
///
/// Approved Document M: a 1100 deep by 700 wide zone in front of a WC; 1100 by 700 in front of
/// a basin and beside a bath (and here a shower, as a bath); 750 to both sides and the foot of a
/// double bed - the principal bedroom's - and to one side of a single; 1200 in front of kitchen
/// units and appliances. The ADA: 1525 (60 in) wide from the side wall by 1420 (56 in) deep from
/// the rear wall round a WC (604.3); 760 by 1220 (30 by 48 in) in front of a lavatory for a
/// forward approach (606.2); 760 (30 in) along a bathtub (607.2); 915 by 1220 (36 by 48 in) at a
/// transfer shower, 760 by 1525 (30 by 60 in) at a roll-in one (608.2); 760 by 1220 beside a
/// bed for a parallel approach (806.2.3); 1015 (40 in) in front of kitchen units (804.2).
///
/// A zone is blocked by a wall it runs into, or by anything standing on the floor in it - not
/// by what is fixed high on a wall over it, nor, in a kitchen, by the rest of its own run.
/// </summary>
public static class Clearances
{
    /// <summary>How high off the floor something can be and still be in the way: knee height.</summary>
    public const double Underneath = 700;

    /// <summary>How much of a zone something has to take before it is in the way, mm²: a 100 square.</summary>
    private const double Touching = 100 * 100;

    public static string Label(ClearanceStandard standard) => standard switch
    {
        ClearanceStandard.Ada => "ADA 2010",
        _ => "Approved Document M (M4(2))"
    };

    /// <summary>
    /// The fitting's zones in its own frame: across it and front to back, the front +. Where one
    /// may go either side, both are offered. <paramref name="sideWall"/> says how far the face of
    /// a wall beside it is, to its left (-1) or right (+1), where there is one near.
    /// </summary>
    private static IEnumerable<(string What, Area[] Either, string Rule)> ZonesOf(ComponentType type, ClearanceStandard standard, Func<int, double?>? sideWall = null)
    {
        var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
        var ada = standard == ClearanceStandard.Ada;

        switch (type.Form)
        {
            case ComponentForm.Toilet when ada:
            {
                // Measured from the side wall beside it - or, with none near, from where one would
                // be with the centreline 455 (18 in) off it, either side.
                var either = new[] { -1, 1 }
                    .Where(side => sideWall?.Invoke(side) is not null)
                    .Select(side => sideWall!(side)!.Value * side)
                    .Select(face => face > 0 ? new Area(face - 1525, face, -halfD, -halfD + 1420) : new Area(face, face + 1525, -halfD, -halfD + 1420))
                    .ToArray();
                if (either.Length == 0) either = new[] { new Area(455 - 1525, 455, -halfD, -halfD + 1420), new Area(-455, 1525 - 455, -halfD, -halfD + 1420) };

                yield return ("the clearance round the WC", either,
                    "ADA 604.3 asks for 1525 (60 in) wide from the side wall by 1420 (56 in) deep from the rear wall.");
                break;
            }

            case ComponentForm.Toilet:
                yield return ("the access zone in front of the WC", new[] { new Area(-350, 350, halfD, halfD + 1100) },
                    "Approved Document M (M4(2)) asks for 1100 deep by 700 wide clear in front of a WC.");
                break;

            case ComponentForm.Basin when ada:
                yield return ("the clear floor space at the lavatory", new[] { new Area(-380, 380, halfD, halfD + 1220) },
                    "ADA 606.2 asks for 760 by 1220 (30 by 48 in) for a forward approach.");
                break;

            case ComponentForm.Basin:
                yield return ("the access zone in front of the basin", new[] { new Area(-550, 550, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 wide by 700 deep clear in front of a basin.");
                break;

            case ComponentForm.Bath when ada:
                yield return ("the clearance along the bath", new[] { new Area(-halfW, halfW, halfD, halfD + 760) },
                    "ADA 607.2 asks for 760 (30 in) wide along the length of a bathtub.");
                break;

            case ComponentForm.Bath:
                yield return ("the access zone beside the bath", new[] { new Area(-550, 550, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 long by 700 wide clear beside a bath.");
                break;

            case ComponentForm.Shower when ada && type.Width >= 1525:
                yield return ("the clearance at the roll-in shower", new[] { new Area(-halfW, -halfW + 1525, halfD, halfD + 760) },
                    "ADA 608.2.2 asks for 760 by 1525 (30 by 60 in) beside the open face of a roll-in shower.");
                break;

            case ComponentForm.Shower when ada:
                yield return ("the clearance at the shower", new[]
                {
                    new Area(-halfW, -halfW + 1220, halfD, halfD + 915),
                    new Area(halfW - 1220, halfW, halfD, halfD + 915)
                }, "ADA 608.2.1 asks for 915 by 1220 (36 by 48 in) from the control wall of a transfer shower.");
                break;

            case ComponentForm.Shower:
                yield return ("the access zone in front of the shower", new[] { new Area(-550, 550, halfD, halfD + 700) },
                    "Approved Document M (M4(2)) asks for 1100 by 700 clear beside a bath or shower.");
                break;

            case ComponentForm.Bed when ada:
                yield return ("the clear floor space beside the bed", new[]
                {
                    new Area(-halfW - 760, -halfW, -610, 610),
                    new Area(halfW, halfW + 760, -610, 610)
                }, "ADA 806.2.3 asks for 760 by 1220 (30 by 48 in) beside a bed for a parallel approach.");
                break;

            case ComponentForm.Bed when type.Width >= 1200:
                yield return ("the access zone to the left of the bed", new[] { new Area(-halfW - 750, -halfW, -halfD, halfD) },
                    "Approved Document M (M4(2)) asks for 750 clear to both sides and the foot of the bed in the principal bedroom.");
                yield return ("the access zone to the right of the bed", new[] { new Area(halfW, halfW + 750, -halfD, halfD) },
                    "Approved Document M (M4(2)) asks for 750 clear to both sides and the foot of the bed in the principal bedroom.");
                yield return ("the access zone at the foot of the bed", new[] { new Area(-halfW, halfW, halfD, halfD + 750) },
                    "Approved Document M (M4(2)) asks for 750 clear to both sides and the foot of the bed in the principal bedroom.");
                break;

            case ComponentForm.Bed:
                yield return ("the access zone beside the bed", new[]
                {
                    new Area(-halfW - 750, -halfW, -halfD, halfD),
                    new Area(halfW, halfW + 750, -halfD, halfD)
                }, "Approved Document M (M4(2)) asks for 750 clear to one side of a single bed.");
                break;

            case ComponentForm.Counter or ComponentForm.Sink or ComponentForm.Cooker or ComponentForm.Dishwasher
                or ComponentForm.WashingMachine or ComponentForm.Fridge when !type.IsHosted:
                yield return ada
                    ? ("the clearance in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1015) },
                        "ADA 804.2 asks for 1015 (40 in) clear in front of kitchen units, and 1525 (60 in) in a U-shaped kitchen.")
                    : ("the clear space in front of the kitchen", new[] { new Area(-halfW, halfW, halfD, halfD + 1200) },
                        "Approved Document M (M4(2)) asks for 1200 clear in front of and between kitchen units and appliances.");
                break;
        }
    }

    /// <summary>Whether a fitting of this type needs room kept clear by it.</summary>
    public static bool Needs(ComponentType type, ClearanceStandard standard) => ZonesOf(type, standard).Any();

    /// <summary>
    /// A fitting's zones where it stands - for one that may have its zone either side, the side
    /// with least in the way - with what is in each. None for one fixed up off the floor.
    /// </summary>
    public static IReadOnlyList<(ClearanceZone Zone, IReadOnlyList<string> In)> Of(BimDocument document, Component component, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(component.TypeId) is not { } type) return Array.Empty<(ClearanceZone, IReadOnlyList<string>)>();
        if (OffTheFloor(document, component, type)) return Array.Empty<(ClearanceZone, IReadOnlyList<string>)>();

        var frame = ComponentModel.FrameOf(component, 0);
        var obstacles = Obstacles(document, component);

        // How far the face of a wall beside it is, either side, within a metre.
        var walls = obstacles.Where(obstacle => obstacle.Name == "the wall").Select(obstacle => obstacle.Outline).ToList();
        double? SideWall(int side)
        {
            bool InWall(double distance) => walls.Any(wall => Polygon2D.Contains(wall, frame.Plan(side * distance, 0)));

            for (var distance = 0.0; distance <= 1000; distance += 5)
            {
                if (!InWall(distance)) continue;

                // Found within 5: closed in on to its face.
                var (clear, inside) = (Math.Max(0, distance - 5), distance);
                while (inside - clear > 0.05)
                {
                    var middle = (clear + inside) / 2;
                    if (InWall(middle)) inside = middle;
                    else clear = middle;
                }

                return inside;
            }

            return null;
        }

        var zones = new List<(ClearanceZone, IReadOnlyList<string>)>();
        foreach (var (what, either, rule) in ZonesOf(type, standard, SideWall))
        {
            var best = either
                .Select(area => frame.Outline(area.From, area.To, area.Back, area.Front))
                .Select(outline => (Outline: outline, In: InTheWay(outline, obstacles)))
                .OrderBy(candidate => candidate.In.Count)
                .First();
            zones.Add((new ClearanceZone(component, what, best.Outline, rule), best.In));
        }

        return zones;
    }

    /// <summary>Every zone on a storey that something is standing in, and what.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.Elements.OfType<Component>()
            .Where(component => component.LevelId == levelId)
            .SelectMany(component => Of(document, component, standard)
                .Where(zone => zone.In.Count > 0)
                .Select(zone => new BlockedClearance(zone.Zone, string.Join(", ", zone.In), document.FindType<ComponentType>(component.TypeId)?.Name ?? "A fitting")))
            .ToList();

    /// <summary>Every blocked zone in the project, storey by storey.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, ClearanceStandard standard) =>
        document.Levels.SelectMany(level => Blocked(document, level.Id, standard)).ToList();

    private static bool OffTheFloor(BimDocument document, Component component, ComponentType type) =>
        ComponentHosting.BaseElevation(document, component, type) - (document.FindLevel(component.LevelId)?.Elevation ?? 0) > Underneath;

    /// <summary>
    /// What could be in a fitting's way, by name and outline: the walls standing on its storey,
    /// and whatever stands on the floor there - not itself, not what is fixed high on a wall,
    /// and for a piece of a kitchen run, not the rest of its run.
    /// </summary>
    private static List<(string Name, IReadOnlyList<Point2D> Outline)> Obstacles(BimDocument document, Component component)
    {
        var obstacles = new List<(string, IReadOnlyList<Point2D>)>();
        var floor = document.FindLevel(component.LevelId)?.Elevation ?? 0;

        foreach (var wall in document.Walls.Where(wall => wall.LevelId == component.LevelId || wall.GetBaseElevation(document) <= floor + 1 && wall.GetTopElevation(document) > floor + 1))
        {
            if (document.GetWallType(wall) is not { } wallType || wallType.Structure.TotalWidth <= 0 || wall.Length < 1) continue;
            if (wall.GetBaseElevation(document) > floor + Underneath || wall.GetTopElevation(document) <= floor + 1) continue;

            var half = wallType.Structure.TotalWidth / 2;
            obstacles.Add(("the wall", CurtainGeometry.Band(wall, wallType, 0, wall.Length, half, -half)));
        }

        var run = document.FindType<ComponentType>(component.TypeId) is { } own && ComponentPlacing.InRun(own)
            ? RunOf(document, component)
            : new HashSet<Component> { component };

        foreach (var other in document.Elements.OfType<Component>().Where(other => other.LevelId == component.LevelId && !run.Contains(other)))
        {
            if (document.FindType<ComponentType>(other.TypeId) is not { } type || OffTheFloor(document, other, type)) continue;
            obstacles.Add((type.Name, ComponentModel.Footprint(type, ComponentModel.FrameOf(other, 0))));
        }

        return obstacles;
    }

    /// <summary>A kitchen piece and every piece of the run it is in: those touching it, and those touching them.</summary>
    private static HashSet<Component> RunOf(BimDocument document, Component component)
    {
        var pieces = document.Elements.OfType<Component>()
            .Where(other => other.LevelId == component.LevelId)
            .Select(other => (Component: other, Type: document.FindType<ComponentType>(other.TypeId)))
            .Where(other => other.Type is not null && ComponentPlacing.InRun(other.Type) && !OffTheFloor(document, other.Component, other.Type))
            .Select(other => (other.Component, Outline: ComponentModel.Footprint(other.Type!, ComponentModel.FrameOf(other.Component, 0))))
            .ToList();

        var run = new HashSet<Component> { component };
        var reached = new Queue<IReadOnlyList<Point2D>>();
        if (pieces.FirstOrDefault(piece => piece.Component == component) is { Outline: { } start }) reached.Enqueue(start);

        while (reached.Count > 0)
        {
            var from = reached.Dequeue();
            foreach (var (piece, outline) in pieces.Where(piece => !run.Contains(piece.Component)))
            {
                if (!Touch(from, outline)) continue;
                run.Add(piece);
                reached.Enqueue(outline);
            }
        }

        return run;
    }

    /// <summary>Whether two outlines meet or overlap - within 5, as units closed up into a run do.</summary>
    private static bool Touch(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b) =>
        a.Any(point => Polygon2D.Contains(b, point) || DistanceToEdge(b, point) <= 5)
        || b.Any(point => Polygon2D.Contains(a, point) || DistanceToEdge(a, point) <= 5);

    private static double DistanceToEdge(IReadOnlyList<Point2D> polygon, Point2D point) =>
        Enumerable.Range(0, polygon.Count).Min(i => Line2D.DistanceFromSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]));

    /// <summary>What takes up more than a sliver of a zone.</summary>
    private static IReadOnlyList<string> InTheWay(IReadOnlyList<Point2D> zone, List<(string Name, IReadOnlyList<Point2D> Outline)> obstacles) =>
        obstacles
            .Where(obstacle => PolygonBoolean.Combine(zone, obstacle.Outline, BooleanOperation.Intersection).Sum(region => region.Area) > Touching)
            .Select(obstacle => obstacle.Name)
            .Distinct()
            .ToList();
}
