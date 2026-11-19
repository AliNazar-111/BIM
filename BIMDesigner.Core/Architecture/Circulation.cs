using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Getting round a building other than a dwelling (round 106), as Approved Document M Volume 2 sets it out in Sections 2 and 3 - in place of a
/// dwelling's M4 doors there.
///
/// Its doors (3.10, 2.13, 2.17): each the effective clear width Table 2 asks by how it is approached - straight on 800; at right angles off an
/// access route 1500 wide or more 800, narrower 825; an outside door of a building the general public use 1000 - and 300 clear beside its
/// leading edge on its pull side to the return wall. A door opening into a corridor recessed so that, fully open, it does not stand into it
/// (3.14g) - a small store's or a duct cupboard's excepted, an accessible unisex toilet's where the corridor is 1800 wide (3.14h).
///
/// Its corridors (3.14): 1200 clear at their narrowest between their doors, columns, radiators and fittings standing in them taken off (a, b);
/// narrower than 1800, a passing place 1800 long and 1800 wide in them or in a corridor or hall their doors open into (c). Its lobbies (3.16,
/// 2.29, Diagram 10): with two single swing doors in line, long enough for each door that swings into it and 1570 between - a wheelchair with a
/// companion pushing - a side door's leaf beyond, 100 off for every 100 of space beside the leading edges past 300, up to 600; with double doors
/// DP1 + DP2 + 1570; 1200 wide, or the wider leaf and 300, or 1800 with double doors.
///
/// A WC's, a shower's or a changing room's doors are Approved Document T's and Section 5's to say, a hotel bedroom's 4.24's; a cupboard's, a
/// duct's, a riser's and a meter cupboard's are left alone. Not done: opening forces, door furniture, visual contrast, vision panels and glass
/// (3.10a, d to m), gradients and ramps along a corridor (3.14d to f), unequal pairs' wider leaves on the same side (3.14i), floor finishes,
/// existing buildings' narrower widths (Table 2's second column) and reception points (3.6). A lift's landing doors are 3.34e's (Lifts).
/// </summary>
public static class Circulation
{
    /// <summary>Table 2's and 3.14's widths, mm.</summary>
    public const double Corridor = 1200, Passing = 1800, Wheelchair = 1570, Edge = 300;

    /// <summary>Whether Approved Document M Volume 2 reaches the building: one that is not a dwelling, nor a block of flats, under England's rules.</summary>
    public static bool Applies(BimDocument document) =>
        !document.ProjectInformation.ClearanceStandard.IsUs() && FireDesign.UseOf(document) is BuildingUse.Office or BuildingUse.Other;

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>A cupboard, a duct, a riser, a meter cupboard: left alone.</summary>
    private static bool Cupboard(Room room) => Named(room, "cupboard", "duct", "riser", "meter");

    /// <summary>A minor utility facility whose door may open into a corridor (3.14g): a small store, a cupboard, a duct.</summary>
    private static bool Utility(Room room) => Cupboard(room) || Named(room, "store", "storage", "cleaner");

    /// <summary>Whether a building is one the general public use - a shop, a hotel, a library, a hospital and the like - whose outside doors give 1000 (Table 2).</summary>
    public static bool Public(BimDocument document)
    {
        var words = Toilets.WordsOf(document.ProjectInformation.BuildingType);
        return words.Overlaps(new[]
        {
            "shop", "shops", "retail", "supermarket", "restaurant", "cafe", "pub", "bar", "hotel", "motel", "hostel", "library", "museum", "gallery", "theatre",
            "cinema", "church", "chapel", "mosque", "temple", "synagogue", "community", "village", "leisure", "sport", "sports", "gym", "pool", "swimming",
            "hospital", "clinic", "surgery", "health", "station", "bank", "council", "civic", "venue", "stadium", "arena", "public", "visitor", "market"
        });
    }

    /// <summary>The enclosed rooms' outlines and the doors from each, with the room beyond, kept while the model does not change.</summary>
    private static (IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Outlines, IReadOnlyDictionary<Room, List<(Door Door, Room? Other)>> Doors) Plan(BimDocument document)
    {
        (IReadOnlyDictionary<Room, IReadOnlyList<Point2D>>, IReadOnlyDictionary<Room, List<(Door, Room?)>>) Work()
        {
            var outlines = document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
                .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
            var doors = outlines.Keys.ToDictionary(room => room, _ => new List<(Door, Room?)>());
            foreach (var door in document.All<Door>())
            {
                var (a, b) = Architecture.Doors.RoomsBeside(document, door);
                if (a == b) continue;
                if (a is not null && doors.TryGetValue(a, out var fromA)) fromA.Add((door, b));
                if (b is not null && doors.TryGetValue(b, out var fromB)) fromB.Add((door, a));
            }

            return (outlines, doors);
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("circulation: plan", document, null, null, Work) : Work();
    }

    /// <summary>Whether a room is a corridor or passageway: called one, or a hall or landing more than three times as long as it is wide.</summary>
    public static bool IsCorridor(Room room, IReadOnlyList<Point2D> outline) =>
        Named(room, "corridor", "passage", "hallway")
        || FireSeparation.Circulation(room) && !Named(room, "stair", "lobby", "porch", "vestibule") && Toilets.Size(outline) is var (narrow, deep) && narrow > 0 && deep / narrow > 3;

    /// <summary>Whether a room is a lobby: called one, or a vestibule.</summary>
    public static bool IsLobby(Room room) => Named(room, "lobby", "vestibule");

    /// <summary>Whether a door's two sides are left to other rules: a WC's, a shower's, a changing room's (Approved Document T, Section 5), a hotel bedroom's or within one (4.24), a cupboard's.</summary>
    private static bool LeftAlone(BimDocument document, Room? room)
    {
        if (room is null) return false;
        if (Cupboard(room) || Lodging.Suites(document).ContainsKey(room)) return true;
        var plan = Toilets.Plan(document);
        return plan.TryGetValue(room, out var fitted) && (fitted.Wcs + fitted.Urinals + fitted.Showers + fitted.Baths > 0 || fitted.Changing);
    }

    /// <summary>
    /// A door's place in its wall: its middle, the way along the wall, the way into the room it swings into, its wall's half thickness, its jambs.
    /// </summary>
    private static (Point2D Middle, Vector2D Along, Vector2D Into, double Half, Point2D From, Point2D To)? Place(BimDocument document, Door door, DoorType type)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType || wall.Length < 1) return null;
        var structure = wallType.Structure;
        var side = door.FlipFacing ? -1.0 : 1.0;
        var middle = wall.PointAt(structure, door.DistanceAlongWall, 0);
        var into = (wall.PointAt(structure, door.DistanceAlongWall, side * 100) - middle).NormalisedOrDefault(Vector2D.UnitY);
        var along = into.PerpendicularLeft();
        var (from, to) = door.GetSpan(type);
        return (middle, along, into, structure.TotalWidth / 2, wall.PointAt(structure, from, 0), wall.PointAt(structure, to, 0));
    }

    /// <summary>How wide a room is straight across from a door into it, mm: from the door's face to the far side.</summary>
    private static double? Across(IReadOnlyList<Point2D> outline, Point2D middle, Vector2D into, double half) =>
        Toilets.Ray(outline, middle + into * (half + 1), into) is { } far ? far + 1 : null;

    /// <summary>
    /// What a door of a building other than a dwelling falls short of: Table 2's effective clear width, 300 beside its leading edge (3.10b, c,
    /// 2.13b, 2.17b), and standing into a corridor when open (3.14g, h).
    /// </summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Door door, DoorType type)
    {
        if (!Applies(document) || Lifts.IsLanding(document, door) || Place(document, door, type) is not (var middle, var along, var into, var half, var jambA, var jambB)) yield break;
        var (outlines, _) = Plan(document);
        var (swing, other) = Architecture.Doors.RoomsBeside(document, door);
        var external = Architecture.Doors.External(document, door, type);
        var swings = type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;
        var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;

        if (!LeftAlone(document, swing) && !LeftAlone(document, other))
        {
            // Its width by how it is come at: off a corridor at right angles, by the corridor's width across from it; else straight on (Table 2).
            var clear = Architecture.Doors.ClearWidth(type);
            var (asked, how) = (800.0, "straight on");
            if (external && Public(document)) (asked, how) = (1000, "as an outside door of a building the general public use");
            else
                foreach (var (room, way) in new[] { (swing, into), (other, -into) })
                    if (room is not null && outlines.TryGetValue(room, out var outline) && IsCorridor(room, outline) && Across(outline, middle, way, half) is { } wide)
                    {
                        (asked, how) = (wide >= 1500 - 0.5 ? 800 : 825, $"at right angles off the {room.Name}, {wide:0} wide");
                        break;
                    }

            if (clear < asked - 0.5)
                yield return ($"it gives {clear:0} clear{(pair ? " through its wider leaf" : string.Empty)}, approached {how}.",
                    $"Approved Document M Volume 2 ({(external ? "2.13b" : "3.10b")}, Table 2, Diagram 9) asks {asked:0} effective clear width through a door of a new building approached {how}{(pair ? ", through one of its leaves" : string.Empty)}.");

            // Room beside its leading edge to reach the handle and pull it open (3.10c, 2.17b).
            if (swings && !pair && Architecture.Doors.Nib(document, door, type) is { } nib && nib < Edge - 0.5)
                yield return ($"its leading edge is {nib:0} from the wall beside it, on the side it opens to.",
                    $"Approved Document M Volume 2 ({(external ? "2.17b" : "3.10c")}, Diagram 9) asks {Edge:0} clear on a door's pull side between its leading edge and a return wall, unless it opens by power.");
        }

        // Open into a corridor: recessed so that it does not stand into it (3.14g).
        if (!swings || swing is null || !outlines.TryGetValue(swing, out var corridor) || !IsCorridor(swing, corridor) || other is not null && Utility(other)) yield break;
        var leaf = (type.Width - 2 * Architecture.Doors.Frame) / (pair ? 2 : 1);
        double Recess(Point2D jamb)
        {
            var away = (jamb - middle).NormalisedOrDefault(along);
            return Toilets.Ray(corridor, jamb + away * 300, into) is { } face ? Math.Max(0, face - half) : 0;
        }

        var stands = leaf - Math.Min(Recess(jambA), Recess(jambB));
        if (stands <= 25) yield break;
        var width = Across(corridor, middle, into, half) ?? 0;
        var toilet = other is not null && Toilets.Plan(document).TryGetValue(other, out var fitted) && fitted.Accessible && fitted.Wcs > 0;
        if (toilet && width >= Passing - 0.5) yield break;
        yield return ($"it opens into the {swing.Name}, standing {stands:0} into it when fully open{(toilet ? $", the corridor {width:0} wide there" : string.Empty)}.",
            "Approved Document M Volume 2 (3.14g) asks a door opening towards a corridor that is a major access route or an escape route to be recessed so that, fully open, it does not stand into the corridor - a small store's or a locked duct cupboard's excepted; an accessible unisex toilet's door may open into a corridor 1800 wide at least that is neither (3.14h).");
    }

    // --- Corridors (3.14) ------------------------------------------------------------

    /// <summary>A corridor measured: its least clear width between its doors, its length, and whether a passing place 1800 by 1800 fits in it, mm.</summary>
    public sealed record Measured(double Width, double Length, bool Passing);

    /// <summary>
    /// A corridor measured on a grid of 50 squares laid along its longest side: each square's clear distance to its walls and to the columns and
    /// fittings standing in it; its least width the widest way's narrowest point between its doors, measured there across; a passing place where
    /// 1800 by 1800 of squares are all clear.
    /// </summary>
    public static Measured? Measure(BimDocument document, Room room)
    {
        var (outlines, doors) = Plan(document);
        if (!outlines.TryGetValue(room, out var outline)) return null;

        Measured? Work()
        {
            const double cell = 50;
            var longest = Enumerable.Range(0, outline.Count).OrderByDescending(i => outline[i].DistanceTo(outline[(i + 1) % outline.Count])).First();
            var way = (outline[(longest + 1) % outline.Count] - outline[longest]).NormalisedOrDefault(Vector2D.UnitX);
            var across = way.PerpendicularLeft();
            var origin = outline[0];
            var (s0, s1) = (outline.Min(p => way.Dot(p - origin)), outline.Max(p => way.Dot(p - origin)));
            var (t0, t1) = (outline.Min(p => across.Dot(p - origin)), outline.Max(p => across.Dot(p - origin)));
            var (nx, ny) = ((int)Math.Ceiling((s1 - s0) / cell), (int)Math.Ceiling((t1 - t0) / cell));
            if (nx < 1 || ny < 1 || (long)nx * ny > 2_000_000) return null;
            Point2D At(int i, int j) => origin + way * (s0 + (i + 0.5) * cell) + across * (t0 + (j + 0.5) * cell);

            // What stands in it: columns, and fittings low enough to be in the way.
            var obstacles = new List<IReadOnlyList<Point2D>>();
            foreach (var column in document.All<Column>().Where(column => column.LevelId == room.LevelId))
                if (document.FindType<ColumnType>(column.TypeId) is { } columnType && column.Outline(columnType) is { Count: >= 3 } shape && shape.Any(point => Polygon2D.Contains(outline, point)))
                    obstacles.Add(shape);
            foreach (var piece in document.All<Component>().Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(outline, piece.Location)))
                if (document.FindType<ComponentType>(piece.TypeId) is { Depth: >= 50 } pieceType && ComponentHosting.BaseElevation(document, piece, pieceType) - (document.FindLevel(room.LevelId)?.Elevation ?? 0) < 2000)
                    obstacles.Add(ComponentModel.Footprint(pieceType, ComponentModel.FrameOf(piece, 0)));

            var edges = Enumerable.Range(0, outline.Count).Select(i => (outline[i], outline[(i + 1) % outline.Count]))
                .Concat(obstacles.SelectMany(shape => Enumerable.Range(0, shape.Count).Select(i => (shape[i], shape[(i + 1) % shape.Count])))).ToList();
            var clear = new double[nx, ny];
            for (var i = 0; i < nx; i++)
            for (var j = 0; j < ny; j++)
            {
                var point = At(i, j);
                if (!Polygon2D.Contains(outline, point) || obstacles.Any(shape => Polygon2D.Contains(shape, point))) continue;
                clear[i, j] = edges.Min(edge => Line2D.DistanceFromSegment(point, edge.Item1, edge.Item2));
            }

            // A passing place: 1800 by 1800 of clear squares.
            var span = (int)Math.Ceiling(Passing / cell);
            var sum = new int[nx + 1, ny + 1];
            for (var i = 0; i < nx; i++)
            for (var j = 0; j < ny; j++)
                sum[i + 1, j + 1] = (clear[i, j] > 0 ? 1 : 0) + sum[i, j + 1] + sum[i + 1, j] - sum[i, j];
            var passing = false;
            for (var i = 0; i + span <= nx && !passing; i++)
            for (var j = 0; j + span <= ny && !passing; j++)
                passing = sum[i + span, j + span] - sum[i, j + span] - sum[i + span, j] + sum[i, j] == span * span;

            // Its doors' ways in: the clearest square within 1200 of each door's middle.
            (int, int)? Entry(Door door)
            {
                if (document.FindType<DoorType>(door.TypeId) is not { } doorType || Place(document, door, doorType) is not (var middle, _, _, _, _, _)) return null;
                (int, int)? best = null;
                for (var i = 0; i < nx; i++)
                for (var j = 0; j < ny; j++)
                    if (clear[i, j] > 0 && At(i, j).DistanceTo(middle) <= 1200 && (best is null || clear[i, j] > clear[best.Value.Item1, best.Value.Item2]))
                        best = (i, j);
                return best;
            }

            var entries = doors[room].Select(each => Entry(each.Door)).OfType<(int, int)>().Distinct().ToList();
            var length = s1 - s0;

            // With fewer than two doors, from end to end: the clearest square within 1200 of each end along its length.
            var reach = (int)Math.Ceiling(1200 / cell);
            foreach (var columns in new[] { Enumerable.Range(0, nx), Enumerable.Range(0, nx).Reverse() })
            {
                if (entries.Count >= 2) break;
                var near = columns.SkipWhile(i => Enumerable.Range(0, ny).All(j => clear[i, j] <= 0)).Take(reach)
                    .SelectMany(i => Enumerable.Range(0, ny).Select(j => (i, j))).Where(cellAt => clear[cellAt.i, cellAt.j] > 0).ToList();
                if (near.Count == 0) return new Measured(0, length, passing);
                var clearest = near.OrderByDescending(cellAt => clear[cellAt.i, cellAt.j]).First();
                if (!entries.Contains(clearest)) entries.Add(clearest);
            }

            if (entries.Count < 2) return new Measured(Chord(outline, obstacles, At(entries[0].Item1, entries[0].Item2)), length, passing);

            // The widest way from the first door to every square: its narrowest point on the way to each other door.
            var best = new double[nx, ny];
            var from = new (int, int)?[nx, ny];
            var queue = new PriorityQueue<(int, int), double>();
            var (i0, j0) = entries[0];
            best[i0, j0] = clear[i0, j0];
            queue.Enqueue((i0, j0), -best[i0, j0]);
            while (queue.TryDequeue(out var here, out var priority))
            {
                var (i, j) = here;
                if (-priority < best[i, j] - 1e-9) continue;
                for (var di = -1; di <= 1; di++)
                for (var dj = -1; dj <= 1; dj++)
                {
                    var (a, b) = (i + di, j + dj);
                    if ((di, dj) == (0, 0) || a < 0 || b < 0 || a >= nx || b >= ny || clear[a, b] <= 0) continue;
                    var through = Math.Min(best[i, j], clear[a, b]);
                    if (through <= best[a, b] + 1e-9) continue;
                    best[a, b] = through;
                    from[a, b] = (i, j);
                    queue.Enqueue((a, b), -through);
                }
            }

            // The narrowest point on the worst of those ways, measured across there - the least of its chords through it.
            var at = entries.Skip(1).OrderBy(entry => best[entry.Item1, entry.Item2]).First();
            if (best[at.Item1, at.Item2] <= 0) return new Measured(0, length, passing);
            var path = new List<(int, int)> { at };
            while (from[at.Item1, at.Item2] is { } back) path.Add(at = back);

            // Every square of the way within 30 of its narrowest: the least chord through any of them - a square just past a radiator's end is
            // as near it as one beside it, but only the one beside it is across from it.
            var narrowest = path.Min(cellAt => clear[cellAt.Item1, cellAt.Item2]);
            return new Measured(path.Where(cellAt => clear[cellAt.Item1, cellAt.Item2] <= narrowest + 30).Min(cellAt => Chord(outline, obstacles, At(cellAt.Item1, cellAt.Item2))), length, passing);
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("circulation: corridor", document, room, null, Work) : Work();
    }

    /// <summary>
    /// What a corridor or a lobby of a building other than a dwelling comes to - "Corridor: 1.62 m clear at its narrowest, 14.8 m long" - with
    /// what Approved Document M Volume 2 asks of it that it falls short of; null for any other room.
    /// </summary>
    public static string? Verdict(BimDocument document, Room room)
    {
        if (!Applies(document)) return null;
        var (outlines, doors) = Plan(document);
        if (!outlines.TryGetValue(room, out var outline)) return null;
        if (IsLobby(room)) return Lobby(document, room, outline, doors[room]);
        if (!IsCorridor(room, outline) || Measure(document, room) is not { } measured) return null;

        var wanting = new List<string>();
        if (measured.Width < Corridor - 0.5)
            wanting.Add($"{measured.Width:0} clear at its narrowest - Approved Document M Volume 2 (3.14a, b) asks {Corridor:0} unobstructed along a corridor's length, columns, radiators and the like not standing into it");
        bool Passes(Room other) => outlines.TryGetValue(other, out var beyond) && (IsCorridor(other, beyond) || FireSeparation.Circulation(other)) && Measure(document, other) is { Passing: true };
        if (measured.Width < Passing - 0.5 && !measured.Passing && !doors[room].Any(each => each.Other is { } other && Passes(other)))
            wanting.Add($"no passing place 1800 long and 1800 wide in it, nor in a corridor or hall its doors open into - Approved Document M Volume 2 (3.14c) asks them where a corridor is narrower than {Passing:0}, at junctions or reasonable intervals, for wheelchair users to pass");

        var said = $"Corridor: {measured.Width / 1000:0.00} m clear at its narrowest, {measured.Length / 1000:0.0} m long{(measured.Passing ? ", a passing place in it" : string.Empty)}";
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }

    // --- Lobbies (3.16, 2.29, Diagram 10) ----------------------------------------------

    /// <summary>
    /// A lobby with two doors by Diagram 10: in line - its length between them each swinging door's leaf (DP1, DP2) and 1570; one at its side -
    /// the end door's leaf and 1570 before the side door, and that door's leaf before the far end; 100 off for each 100 of space beside the
    /// leading edges past 300, up to 600; its width 1200, or the wider leaf and 300, or 1800 with double doors (3.16, 2.29).
    /// </summary>
    private static string? Lobby(BimDocument document, Room room, IReadOnlyList<Point2D> outline, List<(Door Door, Room? Other)> doors)
    {
        if (doors.Count != 2) return null;
        var placed = new List<(Door Door, DoorType Type, Point2D Middle, Vector2D Along, Vector2D In, double Half, Point2D Latch, double Leaf, double Projects, bool Pair, bool External)>();
        foreach (var (door, _) in doors)
        {
            if (document.FindType<DoorType>(door.TypeId) is not { } type || Place(document, door, type) is not (var middle, var along, var into, var half, var jambA, var jambB)) return null;
            var inward = Polygon2D.Contains(outline, middle + into * (half + 150)) ? into : -into;
            var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;
            var leaf = (type.Width - 2 * Architecture.Doors.Frame) / (pair ? 2 : 1);
            var swingsIn = inward.Dot(into) > 0 && type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;
            placed.Add((door, type, middle, along, inward, half, door.FlipHand ? jambA : jambB, leaf, swingsIn ? leaf : 0, pair, Architecture.Doors.External(document, door, type)));
        }

        var paragraph = placed.Any(each => each.External) ? "2.29" : "3.16";
        var doubled = placed.Any(each => each.Pair);

        // The space beside each leading edge on the lobby's side: past 300, 100 off the length for every 100, up to 600.
        double Beside(int k)
        {
            var each = placed[k];
            var away = (each.Latch - each.Middle).NormalisedOrDefault(each.Along);
            return Toilets.Ray(outline, each.Latch + each.In * (each.Half + 10), away) ?? 0;
        }

        var space = Math.Min(Beside(0), Beside(1));
        var off = doubled ? 0 : Math.Min(600, Math.Max(0, Math.Floor((space - Edge + 0.5) / 100) * 100));
        var wanting = new List<string>();
        string said;
        double width;
        if (placed[0].In.Dot(placed[1].In) < -0.9)
        {
            // In line: the length between the two door faces.
            var length = placed[0].In.Dot(placed[1].Middle - placed[0].Middle) - placed[0].Half - placed[1].Half;
            var asked = placed[0].Projects + placed[1].Projects + Wheelchair - off;
            width = outline.Max(p => placed[0].Along.Dot(p - placed[0].Middle)) - outline.Min(p => placed[0].Along.Dot(p - placed[0].Middle));
            said = $"Lobby: {length / 1000:0.00} m long between its doors in line, {width / 1000:0.00} m wide";
            if (length < asked - 0.5)
                wanting.Add($"{length:0} long between its doors - Approved Document M Volume 2 ({paragraph}{(doubled ? "b" : "a, Diagram 10")}) asks {asked:0}: {(placed[0].Projects + placed[1].Projects > 0 ? $"the leaves swinging into it, {placed[0].Projects + placed[1].Projects:0}, and " : string.Empty)}{Wheelchair:0} for a wheelchair with a companion pushing{(off > 0 ? $", less {off:0} for the {space:0} beside the leading edges" : string.Empty)}");
        }
        else if (Math.Abs(placed[0].In.Dot(placed[1].In)) < 0.2)
        {
            // One door at its end, the other in its side: whichever way round leaves more room.
            (double Before, double To, double AskedBefore, double AskedTo) Way(int end, int side)
            {
                var (e, s) = (placed[end], placed[side]);
                var jambs = new[] { s.Middle + s.Along * (s.Type.Width / 2), s.Middle - s.Along * (s.Type.Width / 2) };
                var before = jambs.Min(jamb => e.In.Dot(jamb - e.Middle)) - e.Half;
                var to = Toilets.Ray(outline, e.Middle + e.In * (e.Half + 1), e.In) ?? 0;
                return (before, to, e.Projects + Wheelchair - off, e.Projects + Wheelchair + s.Leaf - off);
            }

            var (before, to, askedBefore, askedTo) = new[] { Way(0, 1), Way(1, 0) }.OrderByDescending(way => Math.Min(way.Before - way.AskedBefore, way.To - way.AskedTo)).First();
            var end = Way(0, 1) == (before, to, askedBefore, askedTo) ? placed[0] : placed[1];
            width = outline.Max(p => end.Along.Dot(p - end.Middle)) - outline.Min(p => end.Along.Dot(p - end.Middle));
            said = $"Lobby: {to / 1000:0.00} m long, entered at its end and its side, {width / 1000:0.00} m wide";
            if (before < askedBefore - 0.5 || to < askedTo - 0.5)
                wanting.Add($"{before:0} from its end door to its side door and {to:0} to its far end - Approved Document M Volume 2 ({paragraph}a, Diagram 10) asks {askedBefore:0} and {askedTo:0}: the end door's leaf if it swings in, {Wheelchair:0} for a wheelchair with a companion pushing, and the side door's leaf{(off > 0 ? $", less {off:0} for the {space:0} beside the leading edges" : string.Empty)}");
        }
        else return null;

        var widthAsked = doubled ? Passing : Math.Max(Corridor, Math.Max(placed[0].Leaf, placed[1].Leaf) + Edge);
        if (width < widthAsked - 0.5)
            wanting.Add($"{width:0} wide - Approved Document M Volume 2 ({paragraph}c) asks {widthAsked:0}: {(doubled ? "1800 with double leaf doors" : "1200, or the wider door leaf and 300, whichever is more")}");
        return wanting.Count == 0 ? said : $"{said}; {string.Join("; ", wanting)}";
    }

    /// <summary>The least chord through a point, mm: from wall to wall, or to what stands in the way, every 5 degrees and square to the room's sides.</summary>
    private static double Chord(IReadOnlyList<Point2D> outline, IReadOnlyList<IReadOnlyList<Point2D>> obstacles, Point2D point)
    {
        double Reach(Vector2D way) => new[] { Toilets.Ray(outline, point, way) }.Concat(obstacles.Select(shape => Toilets.Ray(shape, point, way))).OfType<double>().DefaultIfEmpty(0).Min();
        var ways = Enumerable.Range(0, 36).Select(k => new Vector2D(Math.Cos(k * Math.PI / 36), Math.Sin(k * Math.PI / 36)))
            .Concat(Enumerable.Range(0, outline.Count).Select(i => (outline[(i + 1) % outline.Count] - outline[i]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft()));
        return ways.Min(way => Reach(way) + Reach(-way));
    }
}
