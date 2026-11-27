using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What a door gives and what the rules ask of it: its clear opening width - measured, as
/// Approved Document M and the IRC measure it, from the stop on one jamb to the face of the leaf
/// opened to 90 degrees on the other - and whether it is wide enough, fire-resisting enough,
/// glazed safely enough and set clear of the next door's swing.
/// </summary>
public static class Doors
{
    /// <summary>How wide a door's frame is on each jamb, mm - its lining and stop.</summary>
    public const double Frame = 50;

    /// <summary>How thick a leaf is, mm: what stands in the opening when it is open at 90 degrees.</summary>
    public const double Leaf = 40;

    /// <summary>Whether a door's leaves are glazed.</summary>
    public static bool Glazed(DoorType type) =>
        type.LeafDesign is DoorLeafDesign.Glazed or DoorLeafDesign.FrenchGlazed or DoorLeafDesign.HalfGlazed or DoorLeafDesign.FullGlass or DoorLeafDesign.ArchedTopLight;

    /// <summary>
    /// The clear width a door gives when open: between its frame's stops, less the leaf open in the
    /// way - for a pair, its main leaf's; for a slider, the opening its leaf runs clear of - an
    /// automatic pair's both, as they part; for a bi-fold, less its leaves folded back.
    /// </summary>
    public static double ClearWidth(DoorType type)
    {
        var inside = Math.Max(0, type.Width - 2 * Frame);
        return Math.Max(0, type.Operation switch
        {
            DoorOperation.Swing when type.LeafCount < 2 => inside - Leaf,
            DoorOperation.Swing or DoorOperation.DoubleSwing => inside / 2 - Leaf,
            DoorOperation.Sliding when type.LeafCount >= 2 && !type.Automatic => inside / 2,
            DoorOperation.Sliding or DoorOperation.Overhead => inside,
            DoorOperation.Folding => inside - Math.Max(4, type.LeafCount * 2) * Math.Max(20, Math.Min(type.Thickness, 60)) / 2,
            _ => inside - Leaf
        });
    }

    /// <summary>
    /// The clear width a doorway gives with its doors open, as Approved Document B measures it for escape (Volume 2 Appendix D, D4a, Diagram
    /// D1): a pair's leaves both open, from leaf to leaf - not its main leaf alone, as a wheelchair user's width is; otherwise its clear width.
    /// </summary>
    public static double EscapeWidth(DoorType type) =>
        type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing && (type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2)
            ? Math.Max(0, type.Width - 2 * Frame - 2 * Leaf)
            : ClearWidth(type);

    /// <summary>
    /// A rating's minutes, as it is written: "30 min", "FD30", "E 30 Sa" all 30; in hours as the US writes them, "1 hr", "2-hour", "1/2 hour",
    /// "1.5 h" 60, 120, 30 and 90 (round 140); none, 0.
    /// </summary>
    public static int FireMinutes(string rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return 0;
        if (HoursWritten.Match(rating) is { Success: true } hours)
        {
            var whole = double.Parse(hours.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var value = hours.Groups[2].Success ? whole / double.Parse(hours.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : whole;
            if (value > 0 && double.IsFinite(value)) return (int)Math.Round(value * 60);
        }

        var digits = new string(rating.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var minutes) ? minutes : 0;
    }

    /// <summary>A rating in hours: a number or a fraction, then h, hr, hrs, hour or hours.</summary>
    private static readonly System.Text.RegularExpressions.Regex HoursWritten =
        new(@"(\d+(?:\.\d+)?)(?:\s*/\s*(\d+))?\s*-?\s*(?:h|hr|hrs|hour|hours)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a door leads outside: an exterior door's type, or in an outside wall.</summary>
    public static bool External(BimDocument document, Door door, DoorType type) =>
        type.Function == DoorFunction.Exterior
        || document.Find<Wall>(door.HostWallId) is { } wall && document.GetWallType(wall) is { Function: WallFunction.Exterior };

    /// <summary>The rooms either side of a door, where there are rooms there: on its swing side first.</summary>
    public static (Room? Swing, Room? Other) RoomsBeside(BimDocument document, Door door)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } type) return (null, null);

        var reach = type.Structure.TotalWidth / 2 + 150;
        var side = door.FlipFacing ? -1.0 : 1.0;
        return (RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, side * reach)),
            RoomsOf.Containing(document, wall.LevelId, wall.PointAt(type.Structure, door.DistanceAlongWall, -side * reach)));
    }

    /// <summary>Whether a room is a garage: called one, or with a car in it.</summary>
    public static bool Garage(BimDocument document, Room room) =>
        room.Name.Contains("garage", StringComparison.OrdinalIgnoreCase)
        || room.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>()
            .Any(piece => piece.LevelId == room.LevelId && document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Car && Polygon2D.Contains(boundary.Polygon, piece.Location));

    /// <summary>Whether a room is a bathroom or a WC - not a habitable room.</summary>
    private static bool Wet(BimDocument document, Room room) =>
        room.GetBoundary(document) is { IsEnclosed: true } boundary && document.All<Component>()
            .Where(piece => piece.LevelId == room.LevelId && Polygon2D.Contains(boundary.Polygon, piece.Location))
            .Select(piece => document.FindType<ComponentType>(piece.TypeId))
            .Any(type => type is not null && (Bathrooms.IsWc(type) || type.Form is ComponentForm.Bath or ComponentForm.Shower));

    /// <summary>
    /// The rooms a stair rises through - the one at its foot and the one at its head - which in
    /// a house with a storey more than 4.5 m up are its protected stairway.
    /// </summary>
    public static HashSet<Room> StairRooms(BimDocument document)
    {
        var rooms = new HashSet<Room>();
        foreach (var stair in document.All<Stair>())
        {
            var walk = Stairs.Layout(document, stair).WalkingLine;
            if (walk.Count < 2) continue;
            if (RoomsOf.Containing(document, stair.LevelId, walk[0]) is { } foot) rooms.Add(foot);
            if (stair.TopLevelId is { } top && RoomsOf.Containing(document, top, walk[^1]) is { } head) rooms.Add(head);
        }

        return rooms;
    }

    /// <summary>Whether a dwelling has a storey whose floor is more than 4.5 m above the ground: one that wants a protected stair.</summary>
    public static bool HasHighStorey(BimDocument document)
    {
        var withRooms = document.Levels.Where(level => document.All<Room>().Any(room => room.LevelId == level.Id)).ToList();
        if (withRooms.Count == 0) return false;
        var ground = Escape.Ground(document);
        return withRooms.Any(level => level.Elevation - ground > 4500 + 0.5);
    }

    /// <summary>
    /// How much wall there is beside a door's leading edge, on the side it opens to: along the
    /// face from its latch jamb to the wall that returns there; null where none returns within 1 m.
    /// Its following edge's - its hinge jamb's, on the side it opens away from, as M4(3)'s Diagram
    /// 3.4 draws it (round 159) - where asked.
    /// </summary>
    public static double? Nib(BimDocument document, Door door, DoorType type, bool following = false) =>
        Return(document, door, type, following)?.Reach;

    /// <summary>
    /// The wall that returns beside a door's leading edge - or its following edge - on the side the nib is measured: how far along the face
    /// it is, the wall, and the corner, the point on the door's wall face where it meets it; null where none returns within 1 m.
    /// </summary>
    public static (double Reach, Wall Wall, Point2D Corner)? Return(BimDocument document, Door door, DoorType type, bool following = false)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } host || document.GetWallType(host) is not { } hostType) return null;

        var others = document.Walls
            .Where(wall => wall != host && wall.LevelId == host.LevelId && document.GetWallType(wall) is { } t && t.Structure.TotalWidth > 0)
            .Select(wall => (Wall: wall, Band: CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length, document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2)))
            .ToList();

        var (from, to) = door.GetSpan(type);
        var (latch, away) = door.FlipHand ? (from, -1.0) : (to, 1.0);
        if (following) (latch, away) = door.FlipHand ? (to, 1.0) : (from, -1.0);
        var face = (door.FlipFacing ? -1 : 1) * (following ? -1 : 1) * (hostType.Structure.TotalWidth / 2 + 10);
        for (var reach = 0.0; reach <= 1000; reach += 5)
        {
            var at = host.PointAt(hostType.Structure, latch + away * reach, face);
            foreach (var (wall, band) in others)
                if (Polygon2D.Contains(band, at))
                    return (reach, wall, at);
        }

        return null;
    }

    /// <summary>
    /// A wheelchair user dwelling's door opening outwards into a hall close to a corner, another door on the return wall within 800 of that
    /// corner: its leading edge 800 from the corner, unless a 1500 turning circle fits immediately outside it (M4(3) 3.24e, Diagram 3.5,
    /// round 161) - what it falls short of, said; none where it meets it or the rule does not reach it.
    /// </summary>
    public static (string Said, string Rule)? CornerProblem(BimDocument document, Door door, DoorType type)
    {
        var (swing, other) = RoomsBeside(document, door);
        if (swing is null || other is null || !Visitable.IsHall(swing) || Visitable.IsHall(other)) return null;
        if (Return(document, door, type) is not var (reach, wall, corner) || reach >= 800 - 0.5) return null;
        if (document.GetWallType(wall) is not { } wallType || wall.Length < 1) return null;

        // Another door on the return wall within 800 of the corner, measured along that wall to its nearer jamb.
        var way = (wall.End - wall.Start) / wall.Length;
        var near = document.All<Door>()
            .Where(each => each != door && each.HostWallId == wall.Id && document.FindType<DoorType>(each.TypeId) is not null)
            .Select(each =>
            {
                var (a, b) = each.GetSpan(document.FindType<DoorType>(each.TypeId)!);
                var apart = new[] { a, b }.Min(at => Math.Abs((wall.PointAt(wallType.Structure, at, 0) - corner).Dot(way)));
                return (Door: each, Apart: apart);
            })
            .Where(entry => entry.Apart <= 800 + 0.5)
            .OrderBy(entry => entry.Apart)
            .FirstOrDefault();
        if (near.Door is null) return null;

        // A 1500 turning circle immediately outside the door: one put down there, or room in the hall for one in front of it.
        if (TurningRoom(document, door, type, swing)) return null;
        var beyond = RoomsBeside(document, near.Door) is var (s, o) ? (s == swing ? o : s) : null;
        return ($"its leading edge is {reach:0} from the corner, {(beyond is null ? "another door" : $"the {beyond.Name.ToLowerInvariant()}'s door")} {near.Apart:0} from it on the return wall.",
            "Approved Document M (M4(3) 3.24e, Diagram 3.5) asks an outward opening door close to a corner, another door within 800 of it on the return wall, to have its leading edge 800 from the corner - or a 1500 turning circle immediately outside it.");
    }

    /// <summary>
    /// How far out from a door's face the width a nib makes beside its leading edge - or its following edge - stays clear of walls, which
    /// Approved Document M asks kept for 1200, 1500 or 1800 beyond it (2.14f, 2.20f, 3.14g, h, 3.22e, g; round 163): a strip so wide beside
    /// the edge, on the side the nib is measured, sampled every 50 out to so far; null where it is clear all the way.
    /// </summary>
    public static double? NibKept(BimDocument document, Door door, DoorType type, double nib, double length, bool following = false)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } host || document.GetWallType(host) is not { } hostType) return null;
        var bands = document.Walls
            .Where(wall => wall != host && wall.LevelId == host.LevelId && document.GetWallType(wall) is { } t && t.Structure.TotalWidth > 0)
            .Select(wall => CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length, document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2))
            .ToList();

        var (from, to) = door.GetSpan(type);
        var (edge, away) = door.FlipHand ? (from, -1.0) : (to, 1.0);
        if (following) (edge, away) = door.FlipHand ? (to, 1.0) : (from, -1.0);
        var side = (door.FlipFacing ? -1 : 1) * (following ? -1 : 1);
        var half = hostType.Structure.TotalWidth / 2;
        var outs = new List<double>();
        for (var d = 10.0; d < length - 10; d += 50) outs.Add(d);
        outs.Add(length - 10);
        var across = new[] { 10.0, nib / 4, nib / 2, 3 * nib / 4, nib - 10 };
        bool Blocked(double d) => across.Any(r => bands.Any(band => Polygon2D.Contains(band, host.PointAt(hostType.Structure, edge + away * r, side * (half + d)))));
        var clear = 0.0;
        foreach (var d in outs)
        {
            if (!Blocked(d))
            {
                clear = d;
                continue;
            }

            // Where between the last clear sample and this one the wall begins, to the millimetre.
            var (low, high) = (clear, d);
            while (high - low > 1)
            {
                var middle = (low + high) / 2;
                if (Blocked(middle)) high = middle;
                else low = middle;
            }

            return Math.Round(low);
        }

        return null;
    }

    /// <summary>
    /// How deep a door's reveal is on the side it opens to - from its leaf's face closed to the wall's face there - its frame set in the middle
    /// of the wall's thickness and the leaf on that side of it, as it is drawn, mm (Approved Document M 2.14g, 2.20g, 3.14i, 3.22h).
    /// </summary>
    public static double? Reveal(BimDocument document, Door door, DoorType type)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return null;
        var width = wallType.Structure.TotalWidth;
        var frame = Math.Clamp(type.Thickness > 0 ? type.Thickness : 100, 20, Math.Max(width, 20));
        return Math.Max(0, width / 2 - frame / 2);
    }

    /// <summary>
    /// Whether a 1500 turning circle fits immediately outside a door, in the hall it opens into - or inside an entrance, in the room it opens
    /// into (3.14d, 3.22d): one put down there, or room for one.
    /// </summary>
    public static bool TurningRoom(BimDocument document, Door door, DoorType type, Room hall)
    {
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) return false;
        if (hall.GetBoundary(document) is not { IsEnclosed: true } boundary || boundary.Polygon.Count < 3) return false;
        var outline = boundary.Polygon;
        var half = wallType.Structure.TotalWidth / 2;
        var middle = wall.PointAt(wallType.Structure, door.DistanceAlongWall, 0);
        var normal = (wall.PointAt(wallType.Structure, door.DistanceAlongWall, half + 150) - middle).NormalisedOrDefault(Vector2D.UnitX);
        if (!Polygon2D.Contains(outline, middle + normal * (half + 150))) normal = new Vector2D(-normal.X, -normal.Y);
        var along = new Vector2D(-normal.Y, normal.X);
        var width = door.WidthOf(type);

        // One put down: a turning circle whose disc reaches to within 100 of the door's face.
        if (document.All<Component>().Any(piece => piece.LevelId == hall.LevelId && document.FindType<ComponentType>(piece.TypeId) is { Form: ComponentForm.TurningCircle } circle
                && Polygon2D.Contains(outline, piece.Location) && Line2D.DistanceFromSegment(piece.Location, middle + normal * half - along * width / 2, middle + normal * half + along * width / 2) <= circle.Width / 2 + 100))
            return true;

        // Room for one: a centre 750 out from the door's face, somewhere across its width, 750 clear of the hall's sides.
        var edges = Enumerable.Range(0, outline.Count).Select(i => (outline[i], outline[(i + 1) % outline.Count])).ToList();
        for (var t = -width / 2; t <= width / 2 + 1; t += 50)
        {
            var centre = middle + normal * (half + 760) + along * t;
            if (Polygon2D.Contains(outline, centre) && edges.Min(edge => Line2D.DistanceFromSegment(centre, edge.Item1, edge.Item2)) >= 750 - 1) return true;
        }

        return false;
    }

    /// <summary>Whether a door is a cupboard's - an airing, meter or store cupboard's, a riser's - which M4's halls and doors leave out (Note 1).</summary>
    private static bool Cupboarded(BimDocument document, Door door) =>
        new[] { RoomsBeside(document, door).Swing, RoomsBeside(document, door).Other }
            .Any(room => room is not null && new[] { "cupboard", "airing", "meter", "riser", "store" }.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>What a door falls short of, as the project's standard asks: its width, its nib, its fire rating and closer, its glass, its U-value.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Door door, ClearanceStandard standard)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { } type || type.CurtainPanel) return Array.Empty<PlacementProblem>();
        if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType)
            return Array.Empty<PlacementProblem>();

        var (from, to) = door.GetSpan(type);
        var half = wallType.Structure.TotalWidth / 2;
        var outline = CurtainGeometry.Band(wall, wallType, from, to, half + 100, -half - 100);
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.Add(new PlacementProblem(door, said, rule, outline));

        var clear = ClearWidth(type);
        var external = External(document, door, type);
        var swings = type.Operation is DoorOperation.Swing or DoorOperation.DoubleSwing;
        var pair = type.Operation == DoorOperation.DoubleSwing || type.LeafCount >= 2;

        // Wide enough: a dwelling's by M4, a building's other than a dwelling by Approved Document M Volume 2's Table 2 (round 106, below). A
        // block's communal entrance and doors, and a dwelling's principal private entrance, are Entrances' (round 163).
        var homes = !Circulation.Applies(document);
        var own = homes && Entrances.RoleOf(document, door) != Entrances.Role.None;
        if (homes)
        switch (standard)
        {
            case ClearanceStandard.ApprovedDocumentM when own:
                break;
            case ClearanceStandard.ApprovedDocumentM when external && clear < 850 - 0.5:
                Add($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                    "Approved Document M (M4(2) 2.20d-e, 2.21) asks for 850 clear through an external door - a pair's main leaf.");
                break;
            case ClearanceStandard.ApprovedDocumentM when !external && clear < 750 - 0.5:
                Add($"it gives {clear:0} clear.", "Approved Document M (M4(2) Table 2.1) asks for 750 clear through a door at the least - more off a narrow hall.");
                break;

            // Off a hall or landing, as Table 2.1 asks by its width there and how the door is approached; the hall 900 at least (2.22a, c, round
            // 157) - a cupboard's door left out, as Note 1 has it.
            case ClearanceStandard.ApprovedDocumentM when !external && Domestic.Rules(document, door) && !Cupboarded(document, door)
                && Visitable.HallProblem(document, door, type,
                    "Approved Document M (M4(2) 2.22a, c, Table 2.1) asks for a hall or landing 900 clear at least, and a door off it 750 clear approached head on; approached from the side, 750 off a hall 1200 wide, 775 off 1050, 800 off 900 - a radiator or fitting not opposite it (2.22b).") is var (narrowSaid, narrowRule):
                Add(narrowSaid, narrowRule);
                break;
            case ClearanceStandard.ApprovedDocumentM3 when clear < 850 - 0.5 && !own:
                Add($"it gives {clear:0} clear{(pair ? " through its main leaf" : string.Empty)}.",
                    "Approved Document M (M4(3) 3.22, 3.24) asks for 850 clear through every door, for a wheelchair user.");
                break;

            // Off a hall: approached from the side, the hall 1200 there; head on, 1050 (3.24a, b, round 159) - a cupboard's door left out (Note 1).
            case ClearanceStandard.ApprovedDocumentM3 when !external && Domestic.Rules(document, door) && !Cupboarded(document, door)
                && Visitable.HallAt(document, door) is var (wheelHall, wheelHeadOn, wheelWidth) && wheelWidth < (wheelHeadOn ? 1050 : 1200) - 0.5:
                Add($"the {wheelHall.Name.ToLowerInvariant()} in front of it is {wheelWidth:0} clear, {(wheelHeadOn ? "approached head on" : "approached from the side")}.",
                    "Approved Document M (M4(3) 3.24a, b) asks for every hallway, approach and landing 1050 clear, and 1200 where a door off it is approached from the side.");
                break;
            case ClearanceStandard.Ada when clear < 815 - 0.5:
                Add($"it gives {Units.Bare(clear)} clear.", $"ADA 404.2.3 asks for 32 in{Units.MetricAside("815")} clear through a door.");
                break;
        }

        // A visitable dwelling's front door and the doors on the storey visitors use (M4(1) 1.14a, 1.15a); the WC room's door opening outwards
        // (M4(1) 1.17d, M4(2) 2.27d) - round 148.
        if (homes)
            foreach (var (said, rule) in Visitable.DoorProblems(document, door, type, standard)) Add(said, rule);

        // A block's principal communal entrance and its other communal doors, a dwelling's principal private entrance (round 163).
        if (homes)
            foreach (var (said, rule) in Entrances.DoorProblems(document, door, type, standard)) Add(said, rule);

        // The US's egress door: one at least, side-hinged, 32 in by 78 in clear.
        if (standard.IsUs() && external && swings && !Kept(document, "egress door", () => document.All<Door>().Any(other =>
                document.FindType<DoorType>(other.TypeId) is { Operation: DoorOperation.Swing or DoorOperation.DoubleSwing } otherType
                && External(document, other, otherType) && ClearWidth(otherType) >= 813 - 0.5 && otherType.Height - Frame >= 1981 - 0.5)))
            Add($"no door out of the dwelling is 32 in{Units.MetricAside("813")} clear and 78 in{Units.MetricAside("1981")} high.",
                "IRC R311.2 asks for one side-hinged egress door 32 in clear, 78 in high, at the least.");

        // A nib beside its leading edge, on the side it opens to: every external door, and every door on the entrance storey under M4(2); every door under M4(3).
        var entranceStorey = Kept(document, "entrance storey", () => document.All<Door>()
            .Where(other => document.FindType<DoorType>(other.TypeId) is { } otherType && External(document, other, otherType))
            .Select(other => document.Find<Wall>(other.HostWallId)?.LevelId)
            .FirstOrDefault());
        // In a block, a flat's entrance storey is the level of its door off the common parts (round 163).
        var onEntranceStorey = Entrances.Block(document) ? Entrances.OnFlatsEntranceStorey(document, door) : wall.LevelId == entranceStorey;
        var wantsNib = standard == ClearanceStandard.ApprovedDocumentM3 || standard == ClearanceStandard.ApprovedDocumentM && (external || onEntranceStorey);
        if (homes && !own && wantsNib && swings && !pair && Nib(document, door, type) is { } nib && nib < 300 - 0.5)
            Add($"its leading edge is {nib:0} from the wall beside it.",
                standard == ClearanceStandard.ApprovedDocumentM3
                    ? "Approved Document M (M4(3) 3.22, 3.24) asks for a 300 nib beside every door's leading edge, on the side it opens to."
                    : "Approved Document M (M4(2) 2.20f, 2.22) asks for a 300 nib beside the leading edge of an external door, and of every door on the entrance storey, on the side it opens to.");

        // And M4(3)'s 200 beside every door's following edge (3.24g, round 159).
        if (homes && !own && standard == ClearanceStandard.ApprovedDocumentM3 && swings && !pair && Nib(document, door, type, following: true) is { } tail && tail < 200 - 0.5)
            Add($"its following edge is {tail:0} from the wall beside it.",
                "Approved Document M (M4(3) 3.24g) asks for a 200 nib beside every door's following edge - its hinge side, on the side it opens away from.");

        // And clear of a corner with another door near it on the return wall, or a turning circle outside it (3.24e, round 161).
        if (homes && standard == ClearanceStandard.ApprovedDocumentM3 && swings && !pair && CornerProblem(document, door, type) is var (cornerSaid, cornerRule))
            Add(cornerSaid, cornerRule);

        // In a building other than a dwelling: Table 2's widths, the space beside its leading edge, and not standing into a corridor (round 106).
        foreach (var (said, rule) in Circulation.Problems(document, door, type)) Add(said, rule);

        // Fire doors: into an integral garage, and onto the stair of a house with a storey over 4.5 m.
        var (swingRoom, otherRoom) = RoomsBeside(document, door);
        var garage = swingRoom is not null && otherRoom is not null && Garage(document, swingRoom) != Garage(document, otherRoom);
        var minutes = FireMinutes(type.FireRating);
        if (garage && (minutes < (standard.IsUs() ? 20 : 30) || !type.SelfClosing))
            Add($"it opens into the garage, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}{(type.SelfClosing ? string.Empty : " and with no closer")}.",
                standard.IsUs()
                    ? "IRC R302.5.1 asks for a door between a garage and the house to be solid, or rated 20 minutes, and self-closing and self-latching."
                    : "Approved Document B (5.6, Table C1 item 11a, C5) asks for a door between a house and its garage of FD30S - 30 minutes - and self-closing.");

        if (!standard.IsUs() && !garage && swingRoom is not null && otherRoom is not null && Kept(document, "high storey", () => HasHighStorey(document)) && minutes < 20)
        {
            var stair = Kept(document, "stair rooms", () => StairRooms(document));
            var onto = stair.Contains(swingRoom) ? otherRoom : stair.Contains(otherRoom) ? swingRoom : null;
            if (onto is not null && !stair.Contains(onto) && !Wet(document, onto))
                Add($"it opens off the stair, which a storey more than 4.5 m up makes a protected stairway, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}.",
                    "Approved Document B (2.5a, Table C1 item 11b) asks for the doors from habitable rooms onto a house's protected stair to be fire doors, FD20.");
        }

        // In a block of flats: entrance doors, doors into the common stair, across the common corridors and off them (Table C1, round 94).
        if (!standard.IsUs())
            foreach (var (said, rule) in FireSeparation.Problems(document, door, type)) Add(said, rule);

        // Into a place of special fire hazard in a building other than a dwelling: E 30 (Approved Document B Volume 2 Table C1, round 112).
        if (!standard.IsUs() && FireCompartments.Problem(document, door, type) is var (hazard, hazardRule)) Add(hazard, hazardRule);

        // Between separate occupancies, and into a protected shaft (Approved Document B Volume 2 8.30, Table C1 item 2, round 134).
        if (!standard.IsUs())
            foreach (var (said, rule) in FireCompartments.Problems(document, door, type)) Add(said, rule);

        // Onto an escape route over a flat roof (Approved Document B Volume 2 2.30 to 2.32, Volume 1 2.13, 2.14, 3.10, 3.11, 3.34, round 136).
        if (!standard.IsUs())
            foreach (var (said, rule) in RoofEscape.Problems(document, door, type)) Add(said, rule);

        // In a care home: between protected areas, a bedroom's, ancillary accommodation's (Approved Document B Volume 2 2.42, 2.44, Table C1, round 129).
        foreach (var (said, rule) in CareHomes.Problems(document, door, type)) Add(said, rule);

        // On an escape route of a building other than a dwelling: its direction of opening, its fastening, its vision panel, its exit sign
        // (Approved Document B Volume 2 Section 5, round 113).
        if (!standard.IsUs())
            foreach (var (said, rule) in EscapeRoutes.Problems(document, door, type)) Add(said, rule);

        // A flat's entrance door from the common parts: sealed, and Rw 29 dB (Approved Document E 2.26, round 99).
        if (SoundInsulation.Problem(document, door, type) is var (heard, asked)) Add(heard, asked);

        // Its security: a secure doorset, its letter plate, a viewer and limiter on the main door (Approved Document Q, round 97).
        foreach (var (said, rule) in Security.Problems(document, door, type)) Add(said, rule);

        // Its glass, where it does not insulate (Table B5, round 96).
        if (!standard.IsUs())
            foreach (var (said, rule) in FireGlazing.Problems(document, door, type)) Add(said, rule);

        // Its glass, and how much heat it lets out.
        if (Glazed(type) && !type.SafetyGlazing)
            Add("its glass is not safety glass.",
                standard.IsUs()
                    ? "IRC R308.4.1 makes all glass in a door a hazardous location: tempered or laminated safety glazing."
                    : "Approved Document K (5.1, Diagram 5.1) makes glass in a door below 1500 a critical location: safety glass to BS EN 12600, class 3 at least.");

        // A glass door in a slim frame marked so that it is seen, in a building other than a dwelling (Approved Document K K5.2, 7.4, round 155).
        if (!standard.IsUs() && Manifestation.DoorWants(type) && !type.Manifestation && !Domestic.Rules(document, door)
            && Manifestation.Unmarked(new[] { (type.Width - 2 * Frame, 100.0, type.Height - Frame) }) is { } glass)
            Add(Manifestation.Said(glass), Manifestation.DoorRule);

        // A conversion's outside door: retained, replaced over 3.3; new, 1.4 (Approved Document L 11.7, round 145).
        if (!standard.IsUs() && external && Conversion.Applies(document))
        {
            if (type.HeatTransferCoefficient > 0 && Conversion.Energy(document, door, Conversion.Fabric.Door, type.HeatTransferCoefficient) is var (convertedSaid, convertedRule)) Add(convertedSaid, convertedRule);
        }
        else if (!standard.IsUs() && external && UValueLimit(document, door, type) is var (limit, heat) && type.HeatTransferCoefficient > limit + 0.005)
            Add($"its U-value is {type.HeatTransferCoefficient:0.0#}.", heat);

        // The entrance an approach path comes to: its landing outside, level and covered.
        foreach (var (said, rule) in Approaches.EntranceProblems(document, door, standard)) Add(said, rule);

        return problems;
    }

    /// <summary>
    /// The most heat an external door may let out, W/m²K, and the rule: a dwelling's 1.6 (Approved Document L Table 4.1); in a building other than
    /// a dwelling by Volume 2's Table 4.1 - a vehicle access door or one as large 1.3, a high-usage entrance door 3.0, an external fire doorset
    /// 1.8 (note 14), any other pedestrian door 1.6 (round 118). A high-usage entrance door is one many people go through, powered or robust
    /// (Appendix A): taken as a revolving or power-operated one.
    /// </summary>
    public static (double Limit, string Said) UValueLimit(BimDocument document, Door door, DoorType type)
    {
        if (Domestic.Rules(document, door)) return (1.6, "Approved Document L (Table 4.1) limits an external door in a new dwelling to a U-value of 1.6 W/m²K.");
        var (limit, what) = type.Operation == DoorOperation.Overhead ? (1.3, "a vehicle access door, or a similar large door,")
            : type.Operation == DoorOperation.Revolving || type.Automatic ? (3.0, "a high-usage entrance door")
            : FireMinutes(type.FireRating) > 0 ? (1.8, "an external fire doorset (note 14)")
            : (1.6, "a pedestrian door");
        return (limit, $"Approved Document L Volume 2 (Table 4.1) limits {what} of a new building other than a dwelling to a U-value of {limit:0.0} W/m²K.");
    }

    /// <summary>A fact about the whole building, the same for every door: while the model is drawn, found once.</summary>
    private static T Kept<T>(BimDocument document, string what, Func<T> work) =>
        ModelCache.Of(document) is { } cache ? cache.Keep($"doors: {what}", document, null, null, work) : work();

    /// <summary>What a door comes to as its rules see it - its clear width, and what it falls short of.</summary>
    public static string Verdict(BimDocument document, Door door)
    {
        if (document.FindType<DoorType>(door.TypeId) is not { } type) return string.Empty;

        // The storeys walked once for all its rules, not again for each - while nothing changes (round 116).
        using var kept = ModelCache.Begin(document);
        var problems = Problems(document, door, document.ProjectInformation.ClearanceStandard);
        var clear = $"{Units.Bare(ClearWidth(type))} clear";
        return problems.Count == 0 ? $"{clear} - meets {Clearances.Label(document.ProjectInformation.ClearanceStandard)}"
            : $"{clear} - {string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"))}";
    }

    /// <summary>Every door's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Door>()
            .Where(door => document.Find<Wall>(door.HostWallId)?.LevelId == levelId)
            .SelectMany(door => Problems(document, door, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(door, "its place", problem.Outline, problem.Rule), string.Empty,
                    $"{document.FindType<DoorType>(door.TypeId)?.Name ?? "A door"}{(string.IsNullOrWhiteSpace(door.Mark) ? string.Empty : $" ({door.Mark})")}", problem.Said)))
            .ToList();
}
