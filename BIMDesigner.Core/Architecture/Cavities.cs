using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a cavity wall's cavity is closed at its edges (Approved Document B 5.18, 8.3, 9.3 and Diagrams 5.3, 8.2, 9.2).</summary>
public enum CavityClosing
{
    /// <summary>Nothing said to close it: open at its top and round its openings.</summary>
    Open,

    /// <summary>Closed at its top and round its openings in any material - all a wall of two leaves of brick or concrete 75 mm thick needs (Diagram 5.3).</summary>
    Closers,

    /// <summary>Cavity barriers, E 30 and I 15: round its openings, at its top and edges, and where it meets a compartment floor or wall (5.18, 5.20).</summary>
    Barriers
}

/// <summary>
/// A cavity barrier (round 127): a line on a storey, in the concealed space over its ceilings - a ceiling void, a roof space - closing it from
/// the top of the ceiling, or of the wall under it, up to the floor or roof over it. It divides a cavity, and closes one off over a wall that
/// stops short of the floor or roof above (Approved Document B 5.17, 8.2, 9.2, Diagram 9.1). Tested from each side, 30 minutes' integrity and
/// 15 minutes' insulation (5.20).
/// </summary>
public sealed class CavityBarrier : Element
{
    public override BuiltInCategory Category => BuiltInCategory.CavityBarriers;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    public double Length => Start.DistanceTo(End);

    /// <summary>What it is made of, as 5.21 lists what may form one: mineral wool sleeved in polythene or a slab of it, timber, steel, board.</summary>
    public string Construction { get; set; } = Cavities.Constructions[0];

    /// <summary>Whether it is tested from each side separately to E 30 and I 15 (5.20, 8.8, 9.13) - a proprietary barrier's, or a construction so tested.</summary>
    public bool Tested { get; set; } = true;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(CavityBarrierParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.ReadOnly(CavityBarrierParameters.Length, () => Length);
        yield return ParameterValue.ReadOnly(CavityBarrierParameters.Height, () => Cavities.Span(document, this) is var (bottom, top) ? top - bottom : 0);
        yield return ParameterValue.BindChoice(CavityBarrierParameters.Construction, () => Construction, v => Construction = v, Cavities.Constructions.ToArray());
        yield return ParameterValue.Bind(CavityBarrierParameters.Tested, () => Tested, v => Tested = v);
        yield return ParameterValue.ReadOnly(CavityBarrierParameters.Regulations, () => Cavities.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class CavityBarrierParameters
{
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height (the cavity it closes)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Construction = new("Construction", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Tested = new("Tested E 30 / I 15", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Regulations = new("Regulations", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// Cavities and cavity barriers (round 127), as Approved Document B has them - Volume 1 Section 5 for a house, Section 8 for flats, Volume 2
/// Section 9 for any other building - a cavity being any concealed space (5.16).
///
/// <b>Cavity walls</b> (5.18, 8.3, 9.3): a wall whose build-up has an air cavity between two leaves is closed round its openings and at its top
/// and edges, and where it meets a compartment floor or wall, by cavity barriers E 30 and I 15 (5.20) - its type's Cavity Closed; one of two
/// leaves of brick, block or concrete each 75 mm thick or more is left out of that, its cavity only closed at the top and round its openings in
/// any material (Diagram 5.3).
///
/// <b>Walls that stop short</b> of the floor or roof over them: a compartment wall - in a building other than a home, one typed separating - is
/// not completed by a cavity barrier over it, but runs up to the floor or roof (5.19, 8.4, 9.4, 8.20); the enclosure of a protected escape route - a house's protected stairway where a storey is more than
/// 4.5 m up (2.5, Diagram 2.3), a block of flats' common stair and corridors (8.5), a protected stairway or corridor of a building other than a
/// home (9.5), a partition between bedrooms of a hotel, hostel or care home (9.7) - has a cavity barrier on its line in the cavity over it, or a
/// fire-resisting ceiling EI 30 under the cavity throughout the storey (Diagram 8.3).
///
/// <b>Extensive cavities</b> in a building other than a home (9.9, Table 9.1): the void over a storey's ceilings, between the walls running up
/// to the floor or roof over it and the cavity barriers drawn in it, measured along the storey's two directions - its length and width between
/// them, as cavity barriers are set out - no more than 20 m between a roof and a ceiling; elsewhere 20 m where its surfaces are class C-s3, d2
/// or better and 10 m where worse; 30 m over a fire-resisting ceiling throughout the storey (9.10c); 40 m over a single room, its barriers on
/// the line of its walls, class C-s3, d2 or better (9.11).
///
/// <b>Corridors</b> (round 128): a corridor of a building other than a home has its partitions up to the soffit or a suspended ceiling (2.25 a); a
/// wall across a corridor dividing it, stopping short, has a cavity barrier over it or a fire-resisting ceiling throughout (2.27, 8.6), and a divided
/// corridor's enclosures barriers on their line too unless the storey is divided on the division's line (Methods 1 to 3, Diagram 2.9).
///
/// <b>Openings in cavity barriers</b> (round 128, 5.24, 9.17): a pipe through one sealed, fire-stopped or sleeved as through any fire-separating
/// element, in Table 9.1's and 10.1's other situation; a duct dampered or fire-resisting; over a partition between bedrooms not fire-resisting,
/// only sealed against smoke (9.18).
///
/// Not done: compartment walls in a building other than a home found only as round 112 finds compartments, not typed separating - a 30 minute
/// partition is one there wherever 30 minutes is the period, and would be told to run full height; stud partitions' voids and their junctions
/// with fire-resisting walls (5.18c); floor voids, raised floors and the cavity under a
/// ground floor (9.10f); 9.12's undivided cavities over 40 m, with their detection, linings and wiring; double-skinned roof sheeting (9.8); the
/// cavities of external cladding (9.10d); doors through cavity barriers (5.24a, 9.17a); fixings (5.22, 5.23); 2.25 b's openings without doors.
/// </summary>
public static class Cavities
{
    /// <summary>How thick each leaf of a cavity wall of brick or concrete is to leave it out of the cavity barrier provisions, mm (Diagram 5.3).</summary>
    public const double Leaf = 75;

    /// <summary>Table 9.1's largest undivided cavity, mm: between a roof and a ceiling, or with its surfaces class C-s3, d2 or better; and with them worse.</summary>
    public const double Limit = 20000, WorseLimit = 10000;

    /// <summary>Over a fire-resisting ceiling throughout (9.10c), and over a single room with barriers on its walls (9.11), mm.</summary>
    public const double FireCeilingLimit = 30000, RoomLimit = 40000;

    /// <summary>The squares a storey's cavity is laid out in to find where it runs, mm.</summary>
    public const double Cell = 250;

    /// <summary>What 5.21 lets a cavity barrier be made of; the last, a proprietary barrier tested as one.</summary>
    public static readonly IReadOnlyList<string> Constructions = new[]
    {
        "Mineral wool, polythene-sleeved", "Mineral wool slab", "Timber, 38 mm", "Steel, 0.5 mm", "Board, 12 mm", "Proprietary cavity barrier"
    };

    /// <summary>Which part of Approved Document B a place is judged by, and its paragraphs on cavities.</summary>
    private sealed record Part(string Doc, string Edges, string Barrier, string Diagram, string Compartment, string Openings, string Pipes, string Table);

    private static readonly Part House = new("Approved Document B", "5.18", "5.20", "Diagram 5.3", "5.19", "5.24", "9.2 to 9.5", "Table 9.1");
    private static readonly Part Flats = new("Approved Document B", "8.3", "8.8", "Diagram 8.2", "8.4", "8.9, 5.24", "9.2 to 9.5", "Table 9.1");
    private static readonly Part Other = new("Approved Document B Volume 2", "9.3", "9.13", "Diagram 9.2", "8.20, 9.4", "9.17", "10.2 to 10.5", "Table 10.1");

    private static Part PartOf(BimDocument document, bool home) =>
        !home ? Other : FireSeparation.Of(document) is not null || FireDesign.UseOf(document) == BuildingUse.Flats ? Flats : House;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool Hard(string name) =>
        new[] { "brick", "block", "concrete", "aircrete", "stone", "masonry" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A wall type's cavity: an air layer among its thermal layers with a leaf either side - how wide, and whether both leaves are brick, block or
    /// concrete 75 mm thick or more (Diagram 5.3). Null for a wall with none - a solid wall, a stud partition's void, a log wall.
    /// </summary>
    public static (double Width, bool Masonry)? CavityOf(BimDocument document, WallType type)
    {
        if (type.Log is not null) return null;
        var layers = type.Structure.Layers;
        for (var i = 1; i < layers.Count - 1; i++)
        {
            var layer = layers[i];
            if (layer.Function != LayerFunction.ThermalAir || layer.Thickness <= 0) continue;
            var name = document.FindMaterial(layer.MaterialId)?.Name ?? string.Empty;
            if (!name.Contains("cavity", StringComparison.OrdinalIgnoreCase) && !name.Contains("air", StringComparison.OrdinalIgnoreCase)) continue;
            bool Masonry(IEnumerable<MaterialLayer> leaf) =>
                leaf.Where(each => Hard(document.FindMaterial(each.MaterialId)?.Name ?? string.Empty)).Sum(each => each.Thickness) >= Leaf - 0.5;
            if (!layers.Take(i).Any(each => each.Thickness > 0) || !layers.Skip(i + 1).Any(each => each.Thickness > 0)) continue;
            return (layer.Thickness, Masonry(layers.Take(i)) && Masonry(layers.Skip(i + 1)));
        }

        return null;
    }

    /// <summary>The cavity closing a wall type is taken to have where a project saved before it was asked: what its build-up would be built with.</summary>
    public static CavityClosing Assumed(BimDocument document, WallType type) =>
        CavityOf(document, type) is { Masonry: false } ? CavityClosing.Barriers : CavityClosing.Closers;

    // ---- walls ---------------------------------------------------------------------------

    /// <summary>What a wall falls short of for its cavity, and for the cavity over it where it stops short of the floor or roof above.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || document.IsCurtainWall(wall)) yield break;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        if (one is null && other is null) yield break;
        var home = Domestic.Rules(document, one, other);
        var part = PartOf(document, home);

        // Its own cavity: closed at its edges (5.18, Diagram 5.3).
        if (CavityOf(document, type) is var (width, masonry))
        {
            if (type.Cavity == CavityClosing.Open)
                yield return masonry
                    ? ($"its {width:0} cavity is left open at its top and round its openings.",
                        $"{part.Doc} ({part.Diagram}) asks a cavity wall of two leaves of brick or concrete, each 75 mm thick or more, its cavity closed at the top of the wall - unless it is filled with insulation - and round its openings, in any material: set Cavity Closed in Edit Type.")
                    : ($"its {width:0} cavity is left open, and its leaves are not both brick or concrete 75 mm thick.",
                        $"{part.Doc} ({part.Edges}, {part.Barrier}) asks cavity barriers, E 30 and I 15, round its openings, at its top and edges and where it meets a compartment floor or wall: set Cavity Closed to cavity barriers in Edit Type.");
            else if (type.Cavity == CavityClosing.Closers && !masonry)
                yield return ($"its {width:0} cavity is closed by closers, and its leaves are not both brick or concrete 75 mm thick.",
                    $"{part.Doc} ({part.Edges}, {part.Barrier}) asks this wall cavity barriers, E 30 and I 15, round its openings, at its top and edges and where it meets a compartment floor or wall - {part.Diagram} leaves closers of any material only to two leaves of brick or concrete.");
        }

        // The cavity over it, where it stops short of the floor or roof above.
        if (FireSeparation.Reaches(document, wall) is not { } reach) yield break;
        var over = reach.Roof ? "the roof over it" : "the floor over it";
        if (Route(document, wall, one, other, home) is not { } route)
        {
            // A wall typed separating, in a building other than a home - a home's are said by 5.11 and 7.10 already.
            if (!home && type.Separating)
                yield return ($"it is a separating wall and stops {reach.Gap:0} below {over}.",
                    $"{part.Doc} ({part.Compartment}) asks a compartment wall to run the full height of its storey, up to the underside of the floor or roof above - a line of them is not completed by cavity barriers over it.");
            else
                foreach (var problem in Corridor(document, wall, one, other, home, reach.Gap, over)) yield return problem;
            yield break;
        }

        if (Along(document, wall) || FireCeiling(document, wall.LevelId)) yield break;
        yield return ($"it encloses {route.What} and stops {reach.Gap:0} below {over}, with no cavity barrier over it.",
            $"{route.Rule} asks the cavity over a protected escape route's fire-resisting enclosure, where it is not carried up to the {(reach.Roof ? "roof covering" : "floor above")}, closed by a cavity barrier on its line, or by a fire-resisting ceiling, EI 30, under the cavity throughout the storey: draw a Cavity Barrier along it.");
    }

    /// <summary>
    /// What a wall encloses as a protected escape route, and the paragraph asking the cavity over it closed: a house's protected stairway with a
    /// storey more than 4.5 m up (2.5); a block of flats' common stair or corridor (8.5); a building other than a home's protected stairway or
    /// protected corridor (9.5), or in a hotel, hostel or care home a partition between bedrooms (9.7). Null for any other wall.
    /// </summary>
    private static (string What, string Rule)? Route(BimDocument document, Wall wall, Room? one, Room? other, bool home)
    {
        if (one is null || other is null || one == other) return null;
        if (home)
        {
            if (FireSeparation.Of(document) is { } block)
            {
                // A flat's own compartment wall is to run up to the floor or roof whatever is over it (7.10, 8.4): said there.
                if (!block.Outlines.ContainsKey(one) || !block.Outlines.ContainsKey(other) || FireSeparation.Compartment(document, wall)) return null;
                bool Common(Room room) => block.Stairs.Contains(room) || block.Common.Contains(room) && FireSeparation.Circulation(room);
                if (Common(one) == Common(other)) return null;
                return ($"the common {(Common(one) ? one : other).Name.ToLowerInvariant()}, a protected escape route", "Approved Document B (8.5, Diagram 8.3)");
            }

            if (FireDesign.UseOf(document) != BuildingUse.House || !HighHouse(document)) return null;
            var stairs = Doors.StairRooms(document);
            bool Stairway(Room room) => stairs.Contains(room) || Named(room, "stair", "hall", "landing");
            return Stairway(one) != Stairway(other) ? ("the protected stairway", "Approved Document B (2.5, Diagram 2.3)") : null;
        }

        if (FireSeparating.Of(document, wall) is { What: "enclosure of a protected stairway" })
            return ("the protected stairway", "Approved Document B Volume 2 (9.5, Diagram 9.3)");
        var (corridorOne, corridorOther) = (HorizontalEscape.ProtectedCorridor(document, one), HorizontalEscape.ProtectedCorridor(document, other));
        if (corridorOne != corridorOther)
            return ($"the {(corridorOne ? one : other).Name.ToLowerInvariant()}, a protected corridor", "Approved Document B Volume 2 (9.5, 2.24, Diagram 9.3)");
        if (HorizontalEscape.Purpose(document) is PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther && Named(one, "bedroom") && Named(other, "bedroom"))
            return ("a partition between bedrooms", "Approved Document B Volume 2 (9.7, 9.5, Diagram 9.3)");
        return null;
    }

    /// <summary>Whether a house has a storey more than 4.5 m above the ground (2.5).</summary>
    private static bool HighHouse(BimDocument document)
    {
        var ground = Escape.Ground(document);
        return document.All<Room>().Any(room => (document.FindLevel(room.LevelId)?.Elevation ?? 0) - ground > Escape.High + 0.5);
    }

    // ---- corridors (Volume 2 2.25 to 2.27, Volume 1 8.6) ----------------------------------------

    /// <summary>Whether a room is a corridor whose division and enclosures are asked about: a common corridor of a block of flats, or any corridor of a building other than a home.</summary>
    private static bool Corridor(BimDocument document, Room room, bool home) =>
        home
            ? FireSeparation.Of(document) is { } block && block.Common.Contains(room) && !block.Stairs.Contains(room) && FireSeparation.Circulation(room) && !Named(room, "stair", "lobby")
            : room.GetBoundary(document) is { IsEnclosed: true } boundary && Circulation.IsCorridor(room, boundary.Polygon);

    /// <summary>
    /// What a wall stopping short of the floor or roof over it falls short of about a corridor. Across a corridor, dividing it (2.26): the cavity
    /// over it closed - a cavity barrier on its line, or a fire-resisting ceiling throughout (2.27, Diagram 2.9; 8.6 in a block of flats). Along a
    /// corridor of a building other than a home: a partition up to the soffit or to a suspended ceiling (2.25 a); a divided corridor's
    /// enclosure closed over too (2.27 Method 1), unless the storey is divided on the division's line by fire-resisting construction (Method 2) or
    /// a fire-resisting ceiling runs throughout (Method 3).
    /// </summary>
    private static IEnumerable<(string Said, string Rule)> Corridor(BimDocument document, Wall wall, Room? one, Room? other, bool home, double gap, string over)
    {
        if (one is null || other is null || one == other || home && FireSeparation.Of(document) is null) yield break;
        var (a, b) = (Corridor(document, one, home), Corridor(document, other, home));
        if (!a && !b) yield break;
        var fireCeiling = FireCeiling(document, wall.LevelId);

        if (a && b)
        {
            if (Along(document, wall) || fireCeiling) yield break;
            yield return home
                ? ($"it divides the common corridor and stops {gap:0} below {over}, with no cavity barrier over it.",
                    "Approved Document B (8.6) asks fire-stopping where a divided corridor has a cavity, so that fire and smoke do not pass over the division into the alternative escape route: draw a Cavity Barrier along it.")
                : ($"it divides the corridor and stops {gap:0} below {over}, with no cavity barrier over it.",
                    "Approved Document B Volume 2 (2.27, Diagram 2.9) asks the cavity over a corridor's division closed against smoke passing over it - cavity barriers on its line and on the line of the corridor's enclosures (Method 1), the storey divided on its line by fire-resisting construction with barriers over it (Method 2), or a fire-resisting ceiling throughout (Method 3): draw a Cavity Barrier along it.");
            yield break;
        }

        if (home) yield break;
        var corridor = a ? one : other;
        if (!HorizontalEscape.ProtectedCorridor(document, corridor) && !MeetsCeiling(document, wall, corridor))
        {
            yield return ($"it encloses the {corridor.Name.ToLowerInvariant()}, an escape route, and stops {gap:0} below {over} with no suspended ceiling to meet.",
                "Approved Document B Volume 2 (2.25 a) asks the partitions of a corridor used for means of escape that is not a protected corridor, fire-resisting or not, to continue to the soffit of the floor above or to a suspended ceiling, to hold smoke back.");
            yield break;
        }

        if (Along(document, wall) || fireCeiling || Divisions(document, wall.LevelId).FirstOrDefault(division => division.One == corridor || division.Other == corridor) is not { Wall: { } across }
            || StoreyDivided(document, across)) yield break;
        yield return ($"it encloses the {corridor.Name.ToLowerInvariant()}, divided across, and stops {gap:0} below {over}, with no cavity barrier over it.",
            "Approved Document B Volume 2 (2.27 a, Diagram 2.9) asks cavity barriers on the line of a divided corridor's enclosures as well as across it (Method 1), so that smoke does not pass round the division - unless the storey is divided on the division's line by fire-resisting construction (Method 2) or a fire-resisting ceiling runs throughout (Method 3): draw a Cavity Barrier along it.");
    }

    /// <summary>The walls dividing a storey's corridors: a wall with a corridor either side, kept while the model does not change.</summary>
    private static IReadOnlyList<(Wall Wall, Room One, Room Other)> Divisions(BimDocument document, Guid level)
    {
        IReadOnlyList<(Wall Wall, Room One, Room Other)> Work()
        {
            var home = Domestic.Rules(document);
            var found = new List<(Wall, Room, Room)>();
            foreach (var wall in document.Walls.Where(wall => wall.LevelId == level && wall.Start.DistanceTo(wall.End) > 1))
                if (WallRules.RoomsBeside(document, wall) is ({ } one, { } other) && one != other && Corridor(document, one, home) && Corridor(document, other, home))
                    found.Add((wall, one, other));
            return found;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("cavities: divisions", document, level, null, Work) : Work();
    }

    /// <summary>
    /// Whether a storey is divided on the line of a corridor's division by fire-resisting construction (2.27 Method 2): walls of 30 minutes or more
    /// on that line, each up to the floor or roof over it or with a cavity barrier over it, across the storey's rooms from side to side but 600 mm -
    /// barriers alone in a void over open rooms not dividing it.
    /// </summary>
    private static bool StoreyDivided(BimDocument document, Wall across)
    {
        var length = across.Start.DistanceTo(across.End);
        if (length < 1) return false;
        var way = (across.End - across.Start) * (1 / length);
        double Off(Point2D p) => Math.Abs(way.Cross(p - across.Start));
        double On(Point2D p) => way.Dot(p - across.Start);
        var covered = new List<(double From, double To)>();
        foreach (var wall in document.Walls.Where(each => each.LevelId == across.LevelId && each.Start.DistanceTo(each.End) > 1))
        {
            var along = (wall.End - wall.Start).NormalisedOrDefault(way);
            if (Math.Abs(way.Cross(along)) > 0.05 || Off(wall.Start) > 150 || Off(wall.End) > 150) continue;
            if (Doors.FireMinutes(document.GetWallType(wall)?.FireRating ?? string.Empty) < 30) continue;
            if (FireSeparation.Reaches(document, wall) is not null && !Along(document, wall)) continue;
            covered.Add((Math.Min(On(wall.Start), On(wall.End)), Math.Max(On(wall.Start), On(wall.End))));
        }

        // How far the storey's rooms reach along the line: where their outlines cross it, and their corners beside it - the rooms either side of
        // walls on it coming up to it without crossing.
        var reach = new List<double>();
        foreach (var room in document.All<Room>().Where(room => room.LevelId == across.LevelId && room.GetBoundary(document) is { IsEnclosed: true }))
        {
            var outline = room.GetBoundary(document).Polygon;
            for (var i = 0; i < outline.Count; i++)
            {
                var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
                if (Off(p) <= 500) reach.Add(On(p));
                var (dp, dq) = (way.Cross(p - across.Start), way.Cross(q - across.Start));
                if (dp * dq > 0 || Math.Abs(dp - dq) < 1e-9) continue;
                var t = dp / (dp - dq);
                reach.Add(On(p + (q - p) * t));
            }
        }

        if (reach.Count < 2) return false;
        var (from, to) = (reach.Min(), reach.Max());
        var total = 0.0;
        var reached = from;
        foreach (var (start, end) in covered.OrderBy(each => each.From))
        {
            if (end <= reached) continue;
            total += Math.Min(end, to) - Math.Max(start, reached);
            reached = Math.Max(reached, end);
        }

        return total >= to - from - 600;
    }

    /// <summary>Whether a wall along a corridor meets a suspended ceiling over the corridor: one whose underside is no higher than the wall's top.</summary>
    private static bool MeetsCeiling(BimDocument document, Wall wall, Room corridor)
    {
        if (corridor.GetBoundary(document) is not { IsEnclosed: true } boundary) return false;
        var length = wall.Start.DistanceTo(wall.End);
        if (length < 1) return false;
        var middle = wall.Start.MidpointTo(wall.End);
        var side = new Vector2D(-(wall.End.Y - wall.Start.Y) / length, (wall.End.X - wall.Start.X) / length) * ((document.GetWallType(wall)?.Width ?? 0) / 2 + 300);
        var inside = Polygon2D.Contains(boundary.Polygon, middle + side) ? middle + side : middle + side * -1;
        var top = wall.GetTopElevation(document);
        return document.All<Ceiling>().Any(ceiling => ceiling.LevelId == wall.LevelId && ceiling.Boundary.Count >= 3 && ceiling.Contains(inside)
                                                     && ceiling.GetBottomElevation(document) <= top + 25);
    }

    // ---- services through cavity barriers (5.24, 9.17, 9.18) ---------------------------------

    /// <summary>Where a pipe or duct passes through a cavity barrier: the barrier and the point, at a height within the cavity it closes.</summary>
    public static IEnumerable<(CavityBarrier Barrier, Point3D At)> Through(BimDocument document, Element service)
    {
        var (levelId, ends) = service switch
        {
            Pipe pipe when pipe.Length >= 1 => (pipe.LevelId, pipe.Ends(document)),
            Duct duct when duct.Length >= 1 => (duct.LevelId, duct.Ends(document)),
            _ => (Guid.Empty, default((Point3D Start, Point3D End)))
        };
        if (levelId == Guid.Empty) yield break;
        var (start, end) = ends;
        var (from, to) = (new Point2D(start.X, start.Y), new Point2D(end.X, end.Y));
        if (from.DistanceTo(to) < 1) yield break;
        foreach (var barrier in document.All<CavityBarrier>().Where(each => each.LevelId == levelId && each.Length > 1))
        {
            var (rx, ry, sx, sy) = (to.X - from.X, to.Y - from.Y, barrier.End.X - barrier.Start.X, barrier.End.Y - barrier.Start.Y);
            var denominator = rx * sy - ry * sx;
            if (Math.Abs(denominator) < 1e-9) continue;
            var t = ((barrier.Start.X - from.X) * sy - (barrier.Start.Y - from.Y) * sx) / denominator;
            var u = ((barrier.Start.X - from.X) * ry - (barrier.Start.Y - from.Y) * rx) / denominator;
            if (t < 0 || t > 1 || u < 0 || u > 1) continue;
            var z = start.Z + (end.Z - start.Z) * t;
            if (Span(document, barrier) is not var (bottom, top) || z < bottom - 1 || z > top + 1) continue;
            yield return (barrier, new Point3D(from.X + rx * t, from.Y + ry * t, z));
        }
    }

    /// <summary>
    /// Whether a cavity barrier stands over a partition between bedrooms of a hotel, hostel or care home that is not fire-resisting, where 9.17 does
    /// not apply but its openings are kept few and sealed against smoke (9.18).
    /// </summary>
    private static bool OverBedroomPartition(BimDocument document, CavityBarrier barrier)
    {
        if (HorizontalEscape.Purpose(document) is not (PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther) || Domestic.Rules(document)) return false;
        var way = (barrier.End - barrier.Start).NormalisedOrDefault(Vector2D.UnitX);
        return document.Walls.Any(wall => wall.LevelId == barrier.LevelId && document.GetWallType(wall) is { } type && Doors.FireMinutes(type.FireRating) < 30
                                          && Math.Abs(way.Cross((wall.End - wall.Start).NormalisedOrDefault(way))) < 0.05
                                          && Line2D.DistanceFromSegment(barrier.Start.MidpointTo(barrier.End), wall.Start, wall.End) <= type.Width / 2 + 150
                                          && WallRules.RoomsBeside(document, wall) is ({ } one, { } other) && one != other && Named(one, "bedroom") && Named(other, "bedroom"));
    }

    /// <summary>
    /// What pipes and ducts through cavity barriers fall short of (5.24, 9.17): a pipe kept as Section 9 or 10 keeps one through a fire-separating
    /// element - a proprietary seal, fire-stopping round a pipe no larger than Table 9.1 or 10.1 allows in any other situation (high melting point
    /// metal 160, anything else 40), or a sleeve; a duct with a fire damper where it passes, or of fire-resisting duct (E 30). Over a partition
    /// between bedrooms that is not fire-resisting, only sealed against smoke (9.18).
    /// </summary>
    public static IEnumerable<MepProblem> ServiceProblems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !document.All<CavityBarrier>().Any()) yield break;
        foreach (var service in document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()))
        foreach (var (barrier, at) in Through(document, service))
        {
            var part = PartOf(document, Domestic.Rules(document, RoomsOf.Containing(document, barrier.LevelId, new Point2D(at.X, at.Y))));
            if (OverBedroomPartition(document, barrier))
            {
                if (service is Pipe { Seal: PipeSeal.None } unsealed)
                    yield return new MepProblem(unsealed, "it passes unsealed through the cavity barrier over a partition between bedrooms - Approved Document B Volume 2 (9.18) asks the openings in it kept few and every penetration sealed against smoke with fire-stopping");
                continue;
            }

            if (service is Duct duct)
            {
                if (duct.FireProtection == FireProtection.None)
                    yield return new MepProblem(duct, $"it passes through a cavity barrier unprotected - {part.Doc} ({part.Openings} d, e) asks a duct through a cavity barrier a fire damper where it passes, or fire-resisting duct, E 30");
                continue;
            }

            var pipe = (Pipe)service;
            var material = (document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper).Like();
            var limit = material is PipeMaterial.Copper or PipeMaterial.StainlessSteel or PipeMaterial.GalvanisedSteel ? 160.0 : 40.0;
            var bore = document.FindType<PipeType>(pipe.TypeId) is { } type ? PipeSizes.Nearest(type.Material, pipe.Diameter).Inside : pipe.Diameter;
            switch (pipe.Seal)
            {
                case PipeSeal.None:
                    yield return new MepProblem(pipe, $"it passes through a cavity barrier with nothing closing round it - {part.Doc} ({part.Openings} b, {part.Pipes}) asks a pipe through a cavity barrier a proprietary seal, fire-stopping round a pipe no larger than {part.Table} allows, or a sleeve");
                    break;
                case PipeSeal.FireStopped when bore > limit + 0.5:
                    yield return new MepProblem(pipe, $"a pipe of {Units.Millimetres(bore, "0")} bore fire-stopped through a cavity barrier - {part.Doc} ({part.Table}, any other situation) fire-stops a pipe of {PipeType.Title(material)} up to {Units.Millimetres(limit, "0")} alone; a proprietary seal or a sleeve for it");
                    break;
                case PipeSeal.Sleeved when material != PipeMaterial.PvcU || pipe.Diameter > 160 + 0.5:
                    yield return new MepProblem(pipe, $"a pipe of {PipeType.Title(material)} sleeved through a cavity barrier - {part.Doc} ({part.Pipes}) sleeves lead, aluminium, uPVC or fibre-cement pipe up to 160 mm, in high melting point metal 1 m either side");
                    break;
            }
        }
    }

    /// <summary>Whether cavity barriers drawn on a wall's storey run along its line, over its whole length but half a metre.</summary>
    private static bool Along(BimDocument document, Wall wall)
    {
        var length = wall.Start.DistanceTo(wall.End);
        if (length < 1) return true;
        var way = (wall.End - wall.Start) * (1 / length);
        var reach = (document.GetWallType(wall)?.Width ?? 0) / 2 + 150;
        var covered = new List<(double From, double To)>();
        foreach (var barrier in document.All<CavityBarrier>().Where(each => each.LevelId == wall.LevelId && each.Length > 1))
        {
            var along = (barrier.End - barrier.Start) * (1 / barrier.Length);
            if (Math.Abs(way.Cross(along)) > 0.05) continue;
            if (Math.Abs(way.Cross(barrier.Start - wall.Start)) > reach || Math.Abs(way.Cross(barrier.End - wall.Start)) > reach) continue;
            var (a, b) = (way.Dot(barrier.Start - wall.Start), way.Dot(barrier.End - wall.Start));
            var (from, to) = (Math.Max(0, Math.Min(a, b)), Math.Min(length, Math.Max(a, b)));
            if (to > from) covered.Add((from, to));
        }

        var total = 0.0;
        var reached = 0.0;
        foreach (var (from, to) in covered.OrderBy(each => each.From))
        {
            if (to <= reached) continue;
            total += to - Math.Max(from, reached);
            reached = to;
        }

        return total >= length - 500;
    }

    /// <summary>
    /// Whether a storey has a fire-resisting ceiling under its cavity throughout (Diagram 8.3, 9.10c): a ceiling rated 30 minutes or more over
    /// every enclosed room on it.
    /// </summary>
    public static bool FireCeiling(BimDocument document, Guid level)
    {
        bool Work()
        {
            var ceilings = document.All<Ceiling>().Where(ceiling => ceiling.LevelId == level && ceiling.Boundary.Count >= 3
                                                                    && Doors.FireMinutes(document.FindType<SlabType>(ceiling.TypeId)?.FireRating ?? string.Empty) >= 30).ToList();
            if (ceilings.Count == 0) return false;
            var rooms = document.All<Room>().Where(room => room.LevelId == level && room.GetBoundary(document) is { IsEnclosed: true }).ToList();
            return rooms.Count > 0 && rooms.All(room => ceilings.Any(ceiling => ceiling.Contains(room.Location)));
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("cavities: fire ceiling", document, level, null, Work) : Work();
    }

    // ---- what is over a point ---------------------------------------------------------------

    /// <summary>
    /// The underside of what is over a point at a height: the lowest floor whose underside is above it, within 6 m, else the roof over it -
    /// with whether it is the roof. Null where there is neither.
    /// </summary>
    public static (double Elevation, bool Roof, Slab Over)? Soffit(BimDocument document, Point2D point, double from) =>
        Soffit(document, document.All<Floor>().ToList(), document.All<Roof>().ToList(), point, from);

    private static (double Elevation, bool Roof, Slab Over)? Soffit(BimDocument document, IReadOnlyList<Floor> floors, IReadOnlyList<Roof> roofs, Point2D point, double from)
    {
        Slab? over = null;
        var lowest = double.PositiveInfinity;
        foreach (var floor in floors)
        {
            if (floor.Boundary.Count < 3 || !floor.Contains(point)) continue;
            var bottom = floor.GetBottomElevation(document);
            if (bottom > from - 1 && bottom < from + 6000 && bottom < lowest) (lowest, over) = (bottom, floor);
        }

        var roof = false;
        foreach (var each in roofs)
        {
            if (each.IsExtrusion || each.Boundary.Count < 3 || !each.Contains(point)) continue;
            var underside = each.UndersideAt(document, point);
            if (underside > from - 1 && underside < lowest) (lowest, over, roof) = (underside, each, true);
        }

        return over is null ? null : (lowest, roof, over);
    }

    /// <summary>
    /// The heights a cavity barrier closes, mm: from the top of the ceiling under its middle - or of the wall it stands on, or its storey's floor -
    /// up to the floor or roof over it. Null where nothing is over it.
    /// </summary>
    public static (double Bottom, double Top)? Span(BimDocument document, CavityBarrier barrier)
    {
        var middle = barrier.Start.MidpointTo(barrier.End);
        var level = document.FindLevel(barrier.LevelId)?.Elevation ?? 0;
        var bottom = document.All<Ceiling>().Where(ceiling => ceiling.LevelId == barrier.LevelId && ceiling.Boundary.Count >= 3 && ceiling.Contains(middle))
            .Select(ceiling => ceiling.GetTopElevation(document)).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(bottom))
            bottom = document.Walls.Where(wall => wall.LevelId == barrier.LevelId && Line2D.DistanceFromSegment(middle, wall.Start, wall.End) <= (document.GetWallType(wall)?.Width ?? 0) / 2 + 150)
                .Select(wall => wall.GetTopElevation(document)).DefaultIfEmpty(level).Max();
        return Soffit(document, middle, bottom) is var (top, _, _) && top > bottom + 1 ? (bottom, top) : null;
    }

    /// <summary>A cavity barrier's body: a 40 mm thick sheet along its line, from what it stands on to what is over it.</summary>
    public static Mesh3D? Mesh(BimDocument document, CavityBarrier barrier)
    {
        if (barrier.Length < 1 || Span(document, barrier) is not var (bottom, top)) return null;
        var way = (barrier.End - barrier.Start) * (1 / barrier.Length);
        var side = new Vector2D(-way.Y, way.X) * 20;
        var mesh = new Mesh3D(barrier.Id, barrier.LevelId, MeshKind.Sweep, new ColourRgb(0xD9, 0x7A, 0x2B), $"Cavity Barrier - {barrier.Construction}");
        mesh.AddExtrusion(new[] { barrier.Start + side, barrier.End + side, barrier.End + side * -1, barrier.Start + side * -1 }, bottom, top);
        return mesh;
    }

    // ---- cavity barriers ------------------------------------------------------------------

    /// <summary>What a cavity barrier falls short of: tested to E 30 and I 15 (5.20, 8.8, 9.13).</summary>
    public static IEnumerable<string> Problems(BimDocument document, CavityBarrier barrier)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || barrier.Tested) yield break;
        var room = RoomsOf.Containing(document, barrier.LevelId, barrier.Start.MidpointTo(barrier.End));
        var part = PartOf(document, Domestic.Rules(document, room));
        yield return $"it is {barrier.Construction.ToLowerInvariant()}, not tested to E 30 and I 15 - {part.Doc} ({part.Barrier}) asks a cavity barrier, tested from each side separately, for 30 minutes' integrity and 15 minutes' insulation; what 5.21 lists need not reach it";
    }

    /// <summary>What a cavity barrier comes to: the cavity it closes, and what it falls short of.</summary>
    public static string Verdict(BimDocument document, CavityBarrier barrier)
    {
        var said = Span(document, barrier) is var (bottom, top) ? $"Closes {top - bottom:0} from {bottom:0} up to {top:0}" : "Nothing over it to close the cavity up to";
        var problems = Problems(document, barrier).ToList();
        return problems.Count == 0 ? said : $"{said} - {string.Join("; ", problems)}";
    }

    // ---- extensive cavities (Volume 2, Table 9.1) -------------------------------------------

    /// <summary>
    /// A cavity over a storey's ceilings, between the walls running up to the floor or roof over it and the cavity barriers drawn in it: its
    /// length and width along the storey's two directions, what Table 9.1 allows it and why, and the ceiling it is said on - the largest under it.
    /// </summary>
    public sealed record Void(Guid Level, Ceiling Ceiling, bool Roof, double Length, double Width, double Allowed, string Why, IReadOnlyList<Ceiling> Ceilings);

    /// <summary>The cavities over the ceilings of a building other than a home, kept while the model does not change.</summary>
    public static IReadOnlyList<Void> Voids(BimDocument document) =>
        ModelCache.Of(document) is { } cache ? cache.Keep("cavities: voids", document, null, null, () => WorkVoids(document)) : WorkVoids(document);

    private static IReadOnlyList<Void> WorkVoids(BimDocument document)
    {
        var found = new List<Void>();
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !HorizontalEscape.Applies(document)) return found;
        foreach (var group in document.All<Ceiling>().Where(ceiling => ceiling.Boundary.Count >= 3 && document.FindType<SlabType>(ceiling.TypeId) is not null).GroupBy(ceiling => ceiling.LevelId))
            found.AddRange(VoidsOn(document, group.Key, group.ToList()));
        return found;
    }

    private static IEnumerable<Void> VoidsOn(BimDocument document, Guid level, List<Ceiling> ceilings)
    {
        var points = ceilings.SelectMany(ceiling => ceiling.Boundary).ToList();
        var (minX, minY) = (points.Min(p => p.X), points.Min(p => p.Y));
        var (maxX, maxY) = (points.Max(p => p.X), points.Max(p => p.Y));
        var cell = Cell;
        while ((maxX - minX) / cell * ((maxY - minY) / cell) > 250_000) cell *= 1.5;
        var (nx, ny) = ((int)Math.Ceiling((maxX - minX) / cell) + 1, (int)Math.Ceiling((maxY - minY) / cell) + 1);
        Point2D Centre(int i, int j) => new(minX + (i + 0.5) * cell, minY + (j + 0.5) * cell);

        // The homes of a building of mixed use are Volume 1's: no cavity of theirs counted.
        var homes = document.All<Room>().Where(room => room.LevelId == level && MixedUse.Home(document, room) && room.GetBoundary(document) is { IsEnclosed: true })
            .Select(room => room.GetBoundary(document).Polygon).ToList();

        var floors = document.All<Floor>().Where(floor => floor.Boundary.Count >= 3).ToList();
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3).ToList();
        var ceilingAt = new Ceiling?[nx, ny];
        var bottom = new double[nx, ny];
        var top = new double[nx, ny];
        var roofAt = new bool[nx, ny];
        var overAt = new Slab?[nx, ny];
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < ny; j++)
        {
            var p = Centre(i, j);
            var ceiling = ceilings.Where(each => each.Contains(p)).OrderByDescending(each => each.GetTopElevation(document)).FirstOrDefault();
            if (ceiling is null || homes.Any(home => Polygon2D.Contains(home, p))) continue;
            var from = ceiling.GetTopElevation(document);
            if (Soffit(document, floors, roofs, p, from) is not var (soffit, isRoof, slab) || soffit - from < 50) continue;
            (ceilingAt[i, j], bottom[i, j], top[i, j], roofAt[i, j], overAt[i, j]) = (ceiling, from, soffit, isRoof, slab);
        }

        // What closes it: walls running from below the ceiling up to what is over it, and the cavity barriers drawn on the storey.
        var closed = new bool[nx, ny];
        void Mark(Point2D a, Point2D b, double half, Func<int, int, bool> closes)
        {
            var reach = Math.Max(half, cell * 0.55);
            var (i0, i1) = ((int)Math.Floor((Math.Min(a.X, b.X) - reach - minX) / cell), (int)Math.Ceiling((Math.Max(a.X, b.X) + reach - minX) / cell));
            var (j0, j1) = ((int)Math.Floor((Math.Min(a.Y, b.Y) - reach - minY) / cell), (int)Math.Ceiling((Math.Max(a.Y, b.Y) + reach - minY) / cell));
            for (var i = Math.Max(0, i0); i <= Math.Min(nx - 1, i1); i++)
            for (var j = Math.Max(0, j0); j <= Math.Min(ny - 1, j1); j++)
                if (ceilingAt[i, j] is not null && Line2D.DistanceFromSegment(Centre(i, j), a, b) <= reach && closes(i, j))
                    closed[i, j] = true;
        }

        foreach (var wall in document.Walls)
        {
            var length = wall.Start.DistanceTo(wall.End);
            if (length < 1 || document.GetWallType(wall) is not { } type) continue;
            var (foot, head) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
            var stretch = (wall.End - wall.Start) * (type.Width / 2 / length);
            Mark(wall.Start + stretch * -1, wall.End + stretch, type.Width / 2, (i, j) => foot <= bottom[i, j] + 1 && head >= top[i, j] - 50);
        }

        foreach (var barrier in document.All<CavityBarrier>().Where(each => each.LevelId == level && each.Length > 1))
            Mark(barrier.Start, barrier.End, 0, (_, _) => true);

        // The storey's two directions: its walls' most common, and square to it.
        var degrees = document.Walls.Where(wall => wall.LevelId == level && wall.Start.DistanceTo(wall.End) > 1)
            .GroupBy(wall => ((int)Math.Round(Math.Atan2(wall.End.Y - wall.Start.Y, wall.End.X - wall.Start.X) * 180 / Math.PI) % 90 + 90) % 90)
            .Select(group => (Angle: group.Key, Length: group.Sum(wall => wall.Start.DistanceTo(wall.End)))).OrderByDescending(entry => entry.Length)
            .Select(entry => entry.Angle).DefaultIfEmpty(0).First() * Math.PI / 180;
        var (u, v) = (new Vector2D(Math.Cos(degrees), Math.Sin(degrees)), new Vector2D(-Math.Sin(degrees), Math.Cos(degrees)));
        var origin = new Point2D(0, 0);
        var fireCeiling = FireCeiling(document, level);
        var rooms = document.All<Room>().Where(room => room.LevelId == level && room.GetBoundary(document) is { IsEnclosed: true })
            .Select(room => room.GetBoundary(document).Polygon).ToList();

        var seen = new bool[nx, ny];
        for (var i = 0; i < nx; i++)
        for (var j = 0; j < ny; j++)
        {
            if (seen[i, j] || ceilingAt[i, j] is null || closed[i, j]) continue;
            var cells = new List<(int I, int J)>();
            var queue = new Queue<(int I, int J)>();
            queue.Enqueue((i, j));
            seen[i, j] = true;
            while (queue.Count > 0)
            {
                var (ci, cj) = queue.Dequeue();
                cells.Add((ci, cj));
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    var (ni, nj) = (ci + di, cj + dj);
                    if (ni < 0 || nj < 0 || ni >= nx || nj >= ny || seen[ni, nj] || ceilingAt[ni, nj] is null || closed[ni, nj]) continue;
                    seen[ni, nj] = true;
                    queue.Enqueue((ni, nj));
                }
            }

            var centres = cells.Select(each => Centre(each.I, each.J)).ToList();
            var along = centres.Select(p => u.Dot(p - origin)).ToList();
            var across = centres.Select(p => v.Dot(p - origin)).ToList();
            var (length, width) = (along.Max() - along.Min() + cell, across.Max() - across.Min() + cell);
            if (width > length) (length, width) = (width, length);
            var underRoof = cells.Count(each => roofAt[each.I, each.J]) * 2 > cells.Count;
            var under = cells.GroupBy(each => ceilingAt[each.I, each.J]!).OrderByDescending(group => group.Count()).Select(group => group.Key).ToList();
            var overs = cells.Select(each => overAt[each.I, each.J]!).Distinct().ToList();

            // The surfaces exposed in it: the ceilings' upper faces and the undersides of what is over them (Table 9.1).
            var faces = under.Select(ceiling => Face(document, ceiling, upper: true)).Concat(overs.Select(slab => Face(document, slab, upper: false)))
                .Where(face => face is not null).Select(face => face!.Value).ToList();
            var good = faces.All(face => face.Class is { } each && each.AtLeast(Euroclass.Cs3d2));
            var worst = faces.FirstOrDefault(face => face.Class is not { } each || !each.AtLeast(Euroclass.Cs3d2));
            double allowed;
            string why;
            if (underRoof) (allowed, why) = (Limit, "between a roof and a ceiling, of any class of surface, 20 m (Table 9.1)");
            else if (good) (allowed, why) = (Limit, "with its surfaces class C-s3, d2 or better, 20 m (Table 9.1)");
            else (allowed, why) = (WorseLimit, worst.Class is { } bad
                ? $"with a surface worse than class C-s3, d2 - {worst.Name}, {bad} - 10 m (Table 9.1)"
                : $"with a surface whose class is not given - {worst.Name}, taken as worse than C-s3, d2 until its material's Reaction to Fire is set - 10 m (Table 9.1)");
            if (fireCeiling && allowed < FireCeilingLimit && under.All(ceiling => Doors.FireMinutes(document.FindType<SlabType>(ceiling.TypeId)?.FireRating ?? string.Empty) >= 30))
                (allowed, why) = (FireCeilingLimit, "over a fire-resisting ceiling, EI 30, throughout the storey, 30 m (9.10c)");
            if (good && allowed < RoomLimit && rooms.FirstOrDefault(room => Polygon2D.Contains(room, centres[0])) is { } single
                && centres.Count(p => Polygon2D.Contains(single, p)) >= centres.Count * 0.98)
                (allowed, why) = (RoomLimit, "over a single room, its cavity barriers on the line of the walls round it and its surfaces class C-s3, d2 or better, 40 m (9.11)");
            yield return new Void(level, under[0], underRoof, length, width, allowed, why, under);
        }
    }

    /// <summary>The face of a ceiling, floor or roof exposed in a cavity - its top layer, or its bottom - and that layer's class; null where it has no layers.</summary>
    private static (string Name, Euroclass? Class)? Face(BimDocument document, Slab slab, bool upper)
    {
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return null;
        var layers = type.Structure.Layers.Where(layer => layer.Thickness > 0).ToList();
        if (layers.Count == 0 || document.FindMaterial((upper ? layers[0] : layers[^1]).MaterialId) is not { } material) return null;
        return ($"the {material.Name.ToLowerInvariant()} {(upper ? "on top of the ceiling" : slab is Roof ? "under the roof" : "under the floor over it")}", Euroclass.Of(material)?.Class);
    }

    /// <summary>What a ceiling falls short of: the cavity over it larger than Table 9.1 allows.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Ceiling ceiling)
    {
        foreach (var each in Voids(document).Where(each => each.Ceiling == ceiling && each.Length > each.Allowed + 0.5))
            yield return ($"the cavity over it runs {each.Length / 1000:0.0} m by {each.Width / 1000:0.0} m between the walls up to the {(each.Roof ? "roof" : "floor")} over it and the cavity barriers drawn.",
                $"Approved Document B Volume 2 (9.9) asks cavities divided by cavity barriers - this one {each.Why} at most in any direction{(each.Allowed >= RoomLimit ? "; 9.12 lets a cavity over an undivided area more than 40 m be larger, the room and cavity a compartment with BS 5839-1 detection, B-s3, d2 surfaces and A1 fixings - not checked" : string.Empty)}: draw Cavity Barriers across it.");
    }

    /// <summary>What a ceiling comes to for the cavity over it.</summary>
    public static string Verdict(BimDocument document, Ceiling ceiling)
    {
        var voids = Voids(document).Where(each => each.Ceilings.Contains(ceiling)).ToList();
        var said = voids.Count == 0 ? "No cavity over it measured" : string.Join("; ", voids.Select(each => $"The cavity over it {each.Length / 1000:0.0} m by {each.Width / 1000:0.0} m, {each.Allowed / 1000:0} m allowed"));
        var problems = Problems(document, ceiling).ToList();
        return problems.Count == 0 ? said : $"{said} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }
}
