using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Sound between dwellings (round 99), as Approved Document E (2003, incorporating the 2004, 2010, 2013 and 2015 amendments) sets it out for new
/// homes in England: requirement E1's separating walls and floors built as Sections 2 and 3 describe them, to reach Table 0.1a's DnT,w + Ctr
/// 45 dB, with the junctions that keep sound from going round them; a flat's entrance door from the corridor (2.26); and requirement E3's
/// absorption in the common parts of a block of flats (Section 7, Method A).
///
/// A wall's or floor's mass per unit area by Annex A: each layer's thickness by its material's density - a brick or block leaf's units with its
/// mortar joints at 1800 kg/m³ (A2.1, Table A.1) where its material gives its units' face and joint - finishes added (A2.4).
///
/// Its layers are read as Approved Document E reads a construction: masonry - brick, block, concrete; a frame - studs or joists, a layer that
/// holds up and weighs little; plasterboard; other boards; wet plaster or render; and gaps - cavities and what fills them. A separating wall is a
/// party wall type, or one with rooms of different dwellings either side, or a flat on one side and another part of a block of two dwellings or
/// more - a corridor (2.25) - on the other; a separating floor likewise, with rooms over it and under it. A type naming the Robust Detail it is
/// built to is taken as that detail - not checked here, nor tested before completion (0.2, Annex E). A separating wall runs up to the roof over
/// it (2.55, 2.91, 2.133, 2.160 - round 105).
///
/// Rooms for residential purposes (round 105, Section 6): a hotel's, a hostel's, a hall of residence's, a residential home's bedroom with the
/// rooms it opens into, not through a corridor - its en-suite, a suite's sitting room - separated from each other and from every other part of
/// the building by Section 6's walls - solid masonry, types 1.1 to 1.3, a solid core between panels, 3.1 and 3.2, cavity masonry and timber
/// frames, types 2 and 4, with care - to Table 0.1b's DnT,w + Ctr 43 dB, and its floors - 1.1C and 1.2B, or 2 and 3 where their floating floors
/// and ceilings do not run on from room to room (6.8); a solid wall stopped at a ceiling under a roof or floor by Diagram 6.1 (6.14); its door from
/// the corridor (6.6); the corridors, halls and stairs giving access to it absorbing as a block of flats' (Section 7). No Robust Details there:
/// they stand in for testing in new houses and flats only (Regulation 41).
///
/// Not done: steps in elevation for wall types 2.3 and 2.4 - only staggers in plan; wall ties, flexible closers, sockets, hangers and blocking;
/// flue blocks and refuse chutes; floating floors (c), resilient layers by their dynamic stiffness and soft coverings by their ΔLw; floor
/// penetrations' enclosures; noisy rooms' lobbies (6.7) and room layout (6.16 to 6.18); a ceiling's perforations (6.14c); material change of
/// use (Section 4, 6.9, 6.10) and schools (E4); Method B.
/// </summary>
public static class SoundInsulation
{
    /// <summary>Annex A's mortar, kg/m³; the least mass of an inner leaf or an internal masonry wall without its finish (2.38, 2.44, 2.75, 2.81, 3.32, 3.40), and of a sheet of plasterboard, kg/m²; a flat's entrance door's least Rw (2.26).</summary>
    public const double MortarDensity = 1800, LeastLeaf = 120, LeastSheet = 10, LeastDoor = 29;

    /// <summary>Table 0.1b's least DnT,w + Ctr of a wall between rooms for residential purposes, dB - their floors' as Table 0.1a's, 45 and L'nT,w 62.</summary>
    public const double LodgingWall = 43;

    /// <summary>What a layer is, as Approved Document E reads a construction.</summary>
    internal enum Part
    {
        Masonry,
        Frame,
        Plasterboard,
        Board,
        Plaster,
        Gap
    }

    /// <summary>What a masonry layer is made of: brick, dense aggregate block, lightweight aggregate block, aircrete or concrete cast in situ.</summary>
    internal enum Unit
    {
        None,
        Aircrete,
        Lightweight,
        Dense,
        Brick,
        Concrete
    }

    internal sealed record Piece(MaterialLayer Layer, Material? Material, Part Part, Unit Unit, double Mass)
    {
        public double Thickness => Layer.Thickness;

        public string Name => Material?.Name ?? string.Empty;

        public double Density => Material?.Density ?? 0;

        /// <summary>Mineral wool - glass or rock - what Approved Document E absorbs with and floats floors on.</summary>
        public bool Wool => Has(Name, "wool", "mineral fibre", "glass fibre", "rock fibre", "rockfibre") && !Has(Name, "tile", "board");

        public bool Heavy => Part is Part.Masonry or Part.Plaster;
    }

    private static bool Has(string name, params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool Light(Unit unit) => unit is Unit.Aircrete or Unit.Lightweight;

    // --- Layers and their mass -------------------------------------------------

    /// <summary>A layer's mass per unit area by Annex A, kg/m²: a brick or block leaf's units and mortar joints over their co-ordinating area (A2.1) where its material gives them, else its thickness by its density.</summary>
    internal static double MassOf(MaterialLayer layer, Material? material)
    {
        if (material is null || layer.Thickness <= 0 || material.Density <= 0) return 0;
        var thick = layer.Thickness / 1000;
        if (material.UnitLength > 0 && material.UnitHeight > 0 && material.JointWidth > 0 && UnitOf(material) is not Unit.None and not Unit.Concrete)
        {
            var (d, length, height) = (material.JointWidth / 1000, (material.UnitLength + material.JointWidth) / 1000, (material.UnitHeight + material.JointWidth) / 1000);
            var units = (length - d) * (height - d);
            return thick * (material.Density * units + MortarDensity * (length * height - units)) / (length * height);
        }

        return thick * material.Density;
    }

    /// <summary>What a masonry material is, by its name and density: brick; aircrete under 1000 kg/m³; a block dense aggregate over 1600, lightweight aggregate up to it; concrete cast in situ.</summary>
    internal static Unit UnitOf(Material? material)
    {
        if (material is null) return Unit.None;
        var (name, density) = (material.Name, material.Density);
        if (Has(name, "brick")) return Unit.Brick;
        if (Has(name, "aircrete", "aerated", "autoclaved") || density < 1000) return Unit.Aircrete;
        if (Has(name, "block")) return density > 1600 ? Unit.Dense : Unit.Lightweight;
        if (Has(name, "concrete")) return Unit.Concrete;
        return density > 1600 ? Unit.Dense : Unit.Lightweight;
    }

    private static Part PartOf(MaterialLayer layer, Material? material)
    {
        var name = material?.Name ?? string.Empty;
        var density = material?.Density ?? 0;
        if (Has(name, "plasterboard", "gypsum", "drywall", "plaster board", "wallboard")) return Part.Plasterboard;
        if (Has(name, "plaster", "render", "skim")) return Part.Plaster;
        if (layer.Function == LayerFunction.ThermalAir) return Part.Gap;
        if (Has(name, "timber", "wood", "ply", "osb", "chipboard", "particle", "mdf", "cement bonded", "fibre cement", "board", "sheathing", "deck"))
            return layer.Function == LayerFunction.Structure && layer.Thickness >= 38 ? Part.Frame : Part.Board;
        if (layer.Function == LayerFunction.Structure && (density < 400 || Has(name, "stud", "joist", "wool", "insulation", "cavity"))) return Part.Frame;
        if (Has(name, "cavity", "air gap", "air space", "airspace", "void") && !Has(name, "aircrete")) return Part.Gap;
        return density >= 400 ? Part.Masonry : Part.Gap;
    }

    /// <summary>A build-up's layers as Approved Document E reads them, outside in for a wall, top down for a floor - membranes left out.</summary>
    internal static List<Piece> Pieces(BimDocument document, CompoundStructure structure) =>
        structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0).Select(layer =>
        {
            var material = document.FindMaterial(layer.MaterialId);
            var part = PartOf(layer, material);
            return new Piece(layer, material, part, part == Part.Masonry ? UnitOf(material) : Unit.None, MassOf(layer, material));
        }).ToList();

    /// <summary>A build-up's mass per unit area by Annex A, its finishes with it (A2.4), kg/m².</summary>
    public static double Mass(BimDocument document, CompoundStructure structure) => Pieces(document, structure).Sum(piece => piece.Mass);

    /// <summary>The runs of layers of one part next to each other: their first and last places.</summary>
    private static List<(int From, int To)> Runs(IReadOnlyList<Piece> pieces, Part part)
    {
        var runs = new List<(int From, int To)>();
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Part != part) continue;
            var to = i;
            while (to + 1 < pieces.Count && pieces[to + 1].Part == part) to++;
            runs.Add((i, to));
            i = to;
        }

        return runs;
    }

    private static List<Piece> Span(IReadOnlyList<Piece> pieces, (int From, int To) run) => pieces.Skip(run.From).Take(run.To - run.From + 1).ToList();

    /// <summary>What a leaf is mostly made of: its thickest masonry layer's.</summary>
    private static Unit UnitOf(IReadOnlyList<Piece> leaf) => leaf.OrderByDescending(piece => piece.Thickness).First().Unit;

    /// <summary>
    /// Whether the layers to one side of a masonry core, from the core out, are an independent panel (2.104): two layers of plasterboard at least,
    /// 20 kg/m² together, 35 mm off the core - or on a frame 10 mm off it.
    /// </summary>
    private static bool Panel(IReadOnlyList<Piece> side)
    {
        var boards = side.Where(piece => piece.Part == Part.Plasterboard).ToList();
        if (boards.Count < 2 || boards.Sum(piece => piece.Mass) < 20 - 0.05) return false;
        var behind = side.TakeWhile(piece => piece.Part != Part.Plasterboard).ToList();
        var gap = behind.TakeWhile(piece => piece.Part != Part.Frame).Where(piece => piece.Part == Part.Gap).Sum(piece => piece.Thickness);
        return behind.Any(piece => piece.Part == Part.Frame) ? gap >= 10 - 0.5 : behind.Where(piece => piece.Part == Part.Gap).Sum(piece => piece.Thickness) >= 35 - 0.5;
    }

    private static bool Finished(IReadOnlyList<Piece> face) => face.Any(piece => piece.Part is Part.Plaster or Part.Plasterboard);

    private static string Kg(double mass) => $"{mass:0} kg/m²";

    // --- Separating walls (Section 2) --------------------------------------------

    /// <summary>
    /// Which of Section 2's separating walls a wall type is - wall type 1 solid masonry, 2 cavity masonry, 3 masonry between independent panels,
    /// 4 framed with absorbent material; none (0) where it is none of them - what it is, its mass, and what it falls short of being that one:
    /// each "what it asks (paragraph)", or for none why. Wall types 2.3 and 2.4 ask a step or stagger of 300 mm at least: null where not known,
    /// taken to have it.
    /// </summary>
    public sealed record WallConstruction(int Group, string Type, string Name, string What, double Mass, IReadOnlyList<string> Short)
    {
        public bool Met => Group > 0 && Short.Count == 0;
    }

    public static WallConstruction Wall(BimDocument document, WallType type, bool separatingFloor = false, bool? staggered = null, bool lodging = false)
    {
        var pieces = Pieces(document, type.Structure);
        var mass = pieces.Sum(piece => piece.Mass);
        if (type.Log is not null || pieces.Count == 0)
            return new(0, string.Empty, string.Empty, "a wall of logs", mass, new[] { "a wall of logs is none of them" });
        var leaves = Runs(pieces, Part.Masonry);
        var frames = Runs(pieces, Part.Frame);

        // Masonry between independent panels (wall type 3).
        if (leaves.Count is 1 or 2)
        {
            var (first, last) = (leaves[0].From, leaves[^1].To);
            var outer = pieces.Take(first).Reverse().ToList();
            var inner = pieces.Skip(last + 1).ToList();
            if (Panel(outer) && Panel(inner))
            {
                var core = leaves.Sum(leaf => Span(pieces, leaf).Sum(piece => piece.Mass));
                if (leaves.Count == 2)
                {
                    var cavity = pieces.Skip(leaves[0].To + 1).Take(leaves[1].From - leaves[0].To - 1).Sum(piece => piece.Thickness);
                    return new(3, "3.3", "a cavity masonry core between independent panels", $"a cavity masonry core with a {cavity:0} cavity between independent panels", mass,
                        cavity >= 50 - 0.5 ? Array.Empty<string>() : new[] { "a cavity of 50 at least in its core (2.107)" });
                }

                if (core >= 300 - 0.5)
                    return new(3, "3.1", "a solid masonry core between independent panels", $"a solid masonry core of {Kg(core)} between independent panels", mass, Array.Empty<string>());
                return new(3, "3.2", "a solid masonry core of lightweight block between independent panels", $"a solid masonry core of {Kg(core)} between independent panels", mass,
                    core >= 150 - 0.5 ? Array.Empty<string>() : new[] { $"{Kg(150)} at least of its core - {Kg(300)} of a dense one, wall type 3.1 (2.105, 2.106)" });
            }
        }

        // Framed walls with absorbent material (wall type 4).
        if (frames.Count >= 2 || leaves.Count == 0 && frames.Count == 1)
        {
            var outer = pieces.Take(frames[0].From).ToList();
            var inner = pieces.Skip(frames[^1].To + 1).ToList();
            var apart = type.Structure.TotalWidth - outer.Sum(piece => piece.Thickness) - inner.Sum(piece => piece.Thickness);
            var wool = pieces.Skip(frames[0].From).Take(frames[^1].To - frames[0].From + 1).Where(piece => piece.Wool && piece.Density >= 10 - 0.5).Sum(piece => piece.Thickness);
            var shortOf = new List<string>();
            if (frames.Count < 2) shortOf.Add("two frames, not one (2.147)");
            if (apart < 200 - 0.5) shortOf.Add($"200 at least between its linings' inside faces, not {apart:0} (2.147)");
            bool Lined(IReadOnlyList<Piece> face) => face.Count(piece => piece.Part == Part.Plasterboard && piece.Mass >= LeastSheet - 0.05) >= 2;
            if (!Lined(outer) || !Lined(inner)) shortOf.Add($"two layers of plasterboard of {Kg(LeastSheet)} each at least on each side (2.147)");
            if (wool < 25 - 0.5) shortOf.Add($"25 of mineral wool at least between the frames, not {wool:0} (2.147)");
            return new(4, "4.1", "double leaf frames with absorbent material", $"{(frames.Count < 2 ? "a timber frame" : "two timber frames")} {apart:0} apart between its linings, {Kg(mass)} with them", mass, shortOf);
        }

        var outerFace = leaves.Count > 0 ? pieces.Take(leaves[0].From).ToList() : new List<Piece>();
        var innerFace = leaves.Count > 0 ? pieces.Skip(leaves[^1].To + 1).ToList() : new List<Piece>();
        var finished = Finished(outerFace) && Finished(innerFace);

        // Solid masonry (wall type 1).
        if (leaves.Count == 1)
        {
            var unit = UnitOf(Span(pieces, leaves[0]));
            if (Light(unit))
                return new(0, string.Empty, string.Empty, $"a solid wall of {(unit == Unit.Aircrete ? "aircrete" : "lightweight aggregate block")}, {Kg(mass)} with its finishes", mass,
                    new[] { "a solid separating wall is of dense aggregate concrete block, concrete cast in situ or brick (2.33 to 2.35)" });
            var (code, name, asked, paragraph) = unit switch
            {
                Unit.Brick => ("1.3", "brick, plastered on both room faces", 375.0, "2.35"),
                Unit.Concrete => ("1.2", "dense aggregate concrete cast in situ, plastered on both room faces", 415.0, "2.34"),
                _ => ("1.1", "dense aggregate concrete block, plastered on both room faces", 415.0, "2.33")
            };
            var shortOf = new List<string>();
            if (mass < asked - 0.5) shortOf.Add($"{Kg(asked)} at least with its plaster, not {Kg(mass)} ({paragraph})");
            if (!finished) shortOf.Add($"plaster on both room faces ({paragraph})");
            // Between rooms for residential purposes plasterboard may finish it in place of plaster, a sheet of 10 kg/m² on each room face (6.4).
            else if (lodging && new[] { outerFace, innerFace }.Any(face => !face.Any(piece => piece.Part == Part.Plaster) && face.Where(piece => piece.Part == Part.Plasterboard).Max(piece => piece.Mass) < LeastSheet - 0.05))
                shortOf.Add($"a sheet of plasterboard of {Kg(LeastSheet)} at least on each room face it boards in place of plaster (6.4)");
            return new(1, code, name, $"a solid wall of {(unit == Unit.Brick ? "brick" : unit == Unit.Concrete ? "concrete" : "dense block")}, {Kg(mass)} with its finishes", mass, shortOf);
        }

        // Cavity masonry (wall type 2).
        if (leaves.Count == 2)
        {
            var (one, two) = (Span(pieces, leaves[0]), Span(pieces, leaves[1]));
            var unit = (Unit)Math.Min((int)UnitOf(one), (int)UnitOf(two));
            var cavity = pieces.Skip(leaves[0].To + 1).Take(leaves[1].From - leaves[0].To - 1).Sum(piece => piece.Thickness);
            var boarded = outerFace.Any(piece => piece.Part == Part.Plasterboard) && innerFace.Any(piece => piece.Part == Part.Plasterboard);
            var banded = one.Concat(two).All(piece => piece.Density is >= 1350 - 0.5 and <= 1600 + 0.5);
            var what = $"two leaves of {unit switch { Unit.Aircrete => "aircrete block", Unit.Lightweight => "lightweight aggregate block", Unit.Brick => "brick", Unit.Concrete => "concrete", _ => "dense aggregate block" }} with a {cavity:0} cavity, {Kg(mass)} with its finishes";

            List<string> Asks(double least, double gap, string paragraph, bool step = false, bool board = false)
            {
                var shortOf = new List<string>();
                if (mass < least - 0.5) shortOf.Add($"{Kg(least)} at least with its finishes, not {Kg(mass)} ({paragraph})");
                if (cavity < gap - 0.5) shortOf.Add($"a cavity of {gap:0} at least, not {cavity:0} ({paragraph})");
                if (board ? !boarded : !finished) shortOf.Add(board ? $"plasterboard on both room faces ({paragraph})" : $"a finish on both room faces ({paragraph})");
                if (step && staggered == false) shortOf.Add($"a step or stagger of 300 at least where it meets the outside walls ({paragraph})");
                return shortOf;
            }

            var candidates = new List<(string Code, string Name, List<string> Short)>();
            if (!Light(unit)) candidates.Add(("2.1", "two leaves of dense aggregate concrete block with a 50 cavity", Asks(415, 50, "2.69")));
            if (unit != Unit.Aircrete) candidates.Add(("2.2", "two leaves of lightweight aggregate block with a 75 cavity", Asks(300, 75, "2.70")));
            if (unit == Unit.Lightweight && banded && boarded) candidates.Add(("2.3", "two leaves of lightweight aggregate block of 1350 to 1600 kg/m³ with a 75 cavity, stepped or staggered", Asks(290, 75, "2.71", step: true, board: true)));
            if (unit == Unit.Aircrete)
            {
                var asks = Asks(150, 75, "2.72", step: true);
                if (separatingFloor) asks.Add("no separating floor in the building (2.72)");
                candidates.Add(("2.4", "two leaves of aircrete block with a 75 cavity, stepped or staggered", asks));
            }

            var chosen = candidates.FirstOrDefault(candidate => candidate.Short.Count == 0);
            if (chosen.Code is null)
                chosen = unit == Unit.Lightweight && banded && boarded && candidates.Any(candidate => candidate.Code == "2.3") ? candidates.First(candidate => candidate.Code == "2.3")
                    : unit == Unit.Lightweight ? candidates.First(candidate => candidate.Code == "2.2") : candidates[0];
            return new(2, chosen.Code, chosen.Name, what, mass, chosen.Short);
        }

        return new(0, string.Empty, string.Empty, leaves.Count > 2 ? $"{leaves.Count} leaves of masonry" : "neither masonry nor a frame", mass,
            new[] { leaves.Count > 2 ? "it has more leaves of masonry than any of them" : "its layers are neither masonry nor a timber frame" });
    }

    // --- Which walls and floors separate ------------------------------------------

    /// <summary>The model's enclosed rooms and their outlines, kept while the model does not change.</summary>
    private static IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Outlines(BimDocument document)
    {
        IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Work() => document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document)))
            .Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3).ToDictionary(entry => entry.Room, entry => (IReadOnlyList<Point2D>)entry.Boundary.Polygon);
        return ModelCache.Of(document) is { } cache ? cache.Keep("sound: rooms", document, null, null, Work) : Work();
    }

    private static Room? RoomAt(IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> outlines, Guid level, Point2D point) =>
        outlines.FirstOrDefault(entry => entry.Key.LevelId == level && Polygon2D.Contains(entry.Value, point)).Key;

    /// <summary>
    /// What a wall or floor between two rooms separates, said - "Flat 1 from Flat 2", "Flat 1 from the Corridor", "Bedroom 101 from Bedroom 102" -
    /// and whether it is rooms for residential purposes it separates (Section 6, round 105), not dwellings; null where it separates no dwelling or
    /// room for residential purposes from another part.
    /// </summary>
    private static (string Said, bool Lodging)? Separates(BimDocument document, Room one, Room other)
    {
        if (one == other) return null;
        var (a, b) = (Dwellings.Of(one), Dwellings.Of(other));
        if (a.Length > 0 || b.Length > 0)
        {
            if (Dwellings.Same(one, other)) return null;
            if (a.Length > 0 && b.Length > 0) return ($"{a} from {b}", false);
            return Dwellings.Named(document).Count >= 2 ? ($"{(a.Length > 0 ? a : b)} from the {(a.Length > 0 ? other : one).Name}", false) : null;
        }

        // A hotel's, a hostel's: a room for residential purposes from another, or from any other part of the building (E1).
        var suites = Lodging.Suites(document);
        var (c, d) = (suites.GetValueOrDefault(one), suites.GetValueOrDefault(other));
        if (c is null && d is null || c == d) return null;
        return c is not null && d is not null ? ($"{c} from {d}", true) : ($"{c ?? d} from the {(c is not null ? other : one).Name}", true);
    }

    /// <summary>
    /// What a wall separates (E1, 2.25): a party wall type's two homes, or the dwellings - or a flat and another part of a block - whose rooms are
    /// either side of it along its length; null where it separates none.
    /// </summary>
    public static string? Between(BimDocument document, Wall wall, WallType type) => Separation(document, wall, type)?.Said;

    /// <summary>
    /// What a wall separates, and whether that is rooms for residential purposes rather than dwellings. A party wall type with nothing either side
    /// to tell by is taken as between two homes - in a building with rooms for residential purposes, only with one of them on one side and no room
    /// drawn on the other: the building next to it.
    /// </summary>
    private static (string Said, bool Lodging)? Separation(BimDocument document, Wall wall, WallType type)
    {
        if (type.Function is WallFunction.Exterior or WallFunction.Retaining or WallFunction.Foundation or WallFunction.Soffit || document.IsCurtainWall(wall) || wall.Length < 1) return null;
        var outlines = Outlines(document);
        var suites = Lodging.Suites(document);
        var reach = type.Structure.TotalWidth / 2 + 150;
        var count = Math.Max(1, (int)Math.Ceiling(wall.Length / 500));
        string? beside = null;
        for (var i = 0; i < count; i++)
        {
            var along = wall.Length * (i + 0.5) / count;
            var (left, right) = (RoomAt(outlines, wall.LevelId, wall.PointAt(type.Structure, along, reach)), RoomAt(outlines, wall.LevelId, wall.PointAt(type.Structure, along, -reach)));
            if (left is not null && right is not null && Separates(document, left, right) is { } said) return said;
            if ((left is null) != (right is null)) beside ??= suites.GetValueOrDefault((left ?? right)!);
        }

        if (!type.Separating) return null;
        return suites.Count == 0 ? ("two homes", false) : beside is null ? null : ($"{beside} from the building next to it", true);
    }

    /// <summary>The rooms over a floor - on its storey, inside it - and under it, on the storey below.</summary>
    private static (List<Room> Over, List<Room> Under) Rooms(BimDocument document, Floor floor)
    {
        var outlines = Outlines(document);
        double Elevation(Room room) => document.FindLevel(room.LevelId)?.Elevation ?? 0;
        var level = document.FindLevel(floor.LevelId)?.Elevation ?? 0;
        var over = outlines.Keys.Where(room => room.LevelId == floor.LevelId && Polygon2D.Contains(floor.Boundary, room.Location)).ToList();
        var below = outlines.Keys.Where(room => Elevation(room) < level - 1).GroupBy(Elevation).OrderByDescending(group => group.Key).FirstOrDefault();
        return (over, below?.Where(room => Polygon2D.Contains(floor.Boundary, room.Location)).ToList() ?? new List<Room>());
    }

    /// <summary>What a floor separates: the dwellings - or a flat and another part of a block - over and under it; a separating floor type's two homes where there are rooms under it; else null.</summary>
    public static string? Between(BimDocument document, Floor floor, FloorType type) => Separation(document, floor, type)?.Said;

    /// <summary>What a floor separates, and whether that is rooms for residential purposes rather than dwellings.</summary>
    private static (string Said, bool Lodging)? Separation(BimDocument document, Floor floor, FloorType type)
    {
        if (floor.Boundary.Count < 3) return null;
        var (over, under) = Rooms(document, floor);
        foreach (var top in over)
        foreach (var bottom in under)
            if (Separates(document, top, bottom) is { } said)
                return said;
        return type.Separating && over.Count > 0 && under.Count > 0 && Lodging.Suites(document).Count == 0 ? ("two homes", false) : null;
    }

    /// <summary>Whether the rooms over a floor and under it are all one room for residential purposes', a suite's over two storeys - where E2, not E1, reaches it.</summary>
    internal static bool WithinSuite(BimDocument document, Floor floor)
    {
        var (over, under) = Rooms(document, floor);
        var suites = Lodging.Suites(document);
        var names = over.Concat(under).Select(room => suites.GetValueOrDefault(room)).Distinct().ToList();
        return over.Count > 0 && under.Count > 0 && names.Count == 1 && names[0] is not null;
    }

    /// <summary>
    /// Whether there is a separating floor where a wall is - one at its foot or its head, over it or along it - which asks more of its junctions
    /// (2.39, 2.76, 2.82, 2.110) and rules out wall type 2.4 (2.72).
    /// </summary>
    public static bool SeparatingFloorAt(BimDocument document, Wall wall, WallType type)
    {
        var middle = wall.Start.MidpointTo(wall.End);
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var at = levels.FindIndex(level => level.Id == wall.LevelId);
        var storeys = new[] { wall.LevelId, at >= 0 && at + 1 < levels.Count ? levels[at + 1].Id : Guid.Empty };
        var reach = type.Structure.TotalWidth / 2 + 300;
        return SeparatingFloors(document).Any(floor => storeys.Contains(floor.LevelId)
            && (floor.Contains(middle) || Enumerable.Range(0, floor.Boundary.Count).Min(i => Line2D.DistanceFromSegment(middle, floor.Boundary[i], floor.Boundary[(i + 1) % floor.Boundary.Count])) <= reach));
    }

    /// <summary>The floors that separate dwellings, kept while the model does not change.</summary>
    private static IReadOnlyList<Floor> SeparatingFloors(BimDocument document)
    {
        IReadOnlyList<Floor> Work() => document.All<Floor>().Where(floor => floor.Boundary.Count >= 3 && document.FindType<FloorType>(floor.TypeId) is { } floorType && Between(document, floor, floorType) is not null).ToList();
        return ModelCache.Of(document) is { } cache ? cache.Keep("sound: separating floors", document, null, null, Work) : Work();
    }

    /// <summary>
    /// Whether a separating wall is staggered in plan where it meets the outside walls (2.63): at one of its ends, the outside walls either side
    /// of it 300 mm apart at least along it.
    /// </summary>
    public static bool Staggered(BimDocument document, Wall wall, WallType type)
    {
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var across = way.PerpendicularLeft();
        foreach (var end in new[] { wall.Start, wall.End })
        {
            var faces = Meeting(document, wall, type, end).Select(other => (Side: Math.Sign(across.Dot(other.Start.MidpointTo(other.End) - end)),
                At: way.Dot(other.Start - end) / 2 + way.Dot(other.End - end) / 2)).Where(face => face.Side != 0).ToList();
            if (faces.Any(left => left.Side > 0 && faces.Any(right => right.Side < 0 && Math.Abs(left.At - right.At) >= 300 - 0.5))) return true;
        }

        return false;
    }

    /// <summary>The outside walls a wall meets at one of its ends, on its storey.</summary>
    private static IEnumerable<Wall> Meeting(BimDocument document, Wall wall, WallType type, Point2D end) =>
        document.Walls.Where(other => other != wall && other.LevelId == wall.LevelId && !document.IsCurtainWall(other) && document.GetWallType(other) is { Function: WallFunction.Exterior } outside
                                      && Line2D.DistanceFromSegment(end, other.Start, other.End) <= outside.Structure.TotalWidth / 2 + type.Structure.TotalWidth / 2 + 100);

    // --- The walls a separating element meets ------------------------------------

    /// <summary>What an outside wall is at a junction: a cavity wall with a masonry inner leaf or a timber frame one, a solid masonry wall, or none of these.</summary>
    internal enum Outside
    {
        Other,
        MasonryInner,
        FrameInner,
        Solid
    }

    /// <summary>An outside wall as a separating wall's or floor's junction reads it: what it is, its inner leaf's mass without its finish, the sheets of plasterboard of 10 kg/m² on its inside, and whether that is an independent panel.</summary>
    internal static (Outside Kind, double Inner, int Sheets, bool Lined) OutsideWall(BimDocument document, WallType type)
    {
        var pieces = Pieces(document, type.Structure);
        var last = pieces.FindLastIndex(piece => piece.Part is Part.Masonry or Part.Frame);
        if (last < 0 || type.Log is not null) return (Outside.Other, 0, 0, false);
        var part = pieces[last].Part;
        var first = last;
        while (first > 0 && pieces[first - 1].Part == part) first--;
        var leaf = pieces.Skip(first).Take(last - first + 1).ToList();
        var lining = pieces.Skip(last + 1).ToList();
        var outward = pieces.Take(first).ToList();
        var cavity = outward.Any(piece => piece.Part == Part.Gap) && outward.Any(piece => piece.Part is Part.Masonry or Part.Board or Part.Plaster);
        var sheets = lining.Count(piece => piece.Part == Part.Plasterboard && piece.Mass >= LeastSheet - 0.05);
        var kind = part == Part.Frame ? Outside.FrameInner : cavity ? Outside.MasonryInner : Outside.Solid;
        return (kind, leaf.Sum(piece => piece.Mass), sheets, Panel(lining));
    }

    /// <summary>A masonry wall's mass without its finish - its masonry leaves - kg/m²; null for a wall with no masonry.</summary>
    private static double? MasonryOf(BimDocument document, WallType type)
    {
        var pieces = Pieces(document, type.Structure);
        return pieces.Any(piece => piece.Part == Part.Masonry) ? pieces.Where(piece => piece.Part == Part.Masonry).Sum(piece => piece.Mass) : null;
    }

    /// <summary>Whether an outside wall has openings 1 m high at least within 700 mm of a separating wall's faces on both sides of it, at a junction (2.38).</summary>
    private static bool OpenedBothSides(BimDocument document, Wall wall, WallType type, Point2D end, IReadOnlyList<Wall> outside)
    {
        var across = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var half = type.Structure.TotalWidth / 2;
        var (left, right) = (false, false);
        foreach (var host in outside)
        {
            var along = (host.End - host.Start).NormalisedOrDefault(Vector2D.UnitX);
            foreach (var opening in document.All<Opening>().Where(each => each.HostWallId == host.Id))
            {
                var kind = document.FindType<OpeningType>(opening.TypeId);
                if (opening.HeightOf(kind) < 1000 - 0.5) continue;
                var middle = opening.GetCentre(host);
                var width = opening.WidthOf(kind);
                var (one, two) = (across.Dot(middle + along * (width / 2) - end), across.Dot(middle - along * (width / 2) - end));
                if (one * two <= 0 || Math.Min(Math.Abs(one), Math.Abs(two)) - half > 700 + 0.5) continue;
                if (one > 0) left = true;
                else right = true;
            }
        }

        return left && right;
    }

    private static IEnumerable<(string Said, string Rule)> Junctions(BimDocument document, Wall wall, WallType type, WallConstruction built, bool floors)
    {
        var (group, code) = (built.Group, built.Type);
        var beside = $"beside a separating wall of type {code}";

        // The outside walls it meets at its ends (2.36 to 2.42, 2.73 to 2.79, 2.108 to 2.114, 2.148 to 2.151).
        var seen = new HashSet<Guid>();
        foreach (var end in new[] { wall.Start, wall.End })
        {
            var outside = Meeting(document, wall, type, end).ToList();
            foreach (var other in outside)
            {
                if (document.GetWallType(other) is not { } otherType || !seen.Add(otherType.Id)) continue;
                var (kind, inner, sheets, lined) = OutsideWall(document, otherType);
                var name = $"the outside wall {otherType.Name}";
                switch (kind)
                {
                    case Outside.Solid:
                        yield return ($"it meets {name}, of solid masonry.", $"Approved Document E ({group switch { 1 => "2.42", 2 => "2.79", 3 => "2.114", _ => "2.151" }}) gives no junction of a separating wall with a solid masonry outside wall - seek specialist advice.");
                        break;
                    case Outside.FrameInner when group == 3:
                        yield return ($"it meets {name}, whose inner leaf is a timber frame.", "Approved Document E (2.113) gives no junction of wall type 3 with an outside wall whose inner leaf is framed - seek specialist advice.");
                        break;
                    case Outside.FrameInner when sheets < (floors ? 2 : 1):
                        yield return ($"it meets {name}, a timber frame inner leaf with {(sheets == 0 ? "no sheet" : $"{sheets} sheet")} of plasterboard of {Kg(LeastSheet)} on it.",
                            $"Approved Document E ({group switch { 1 => "2.41", 2 => "2.78", _ => "2.150" }}) asks the framed inner leaf of an outside wall {beside} to be finished with {(floors ? "two layers" : "one layer")} of plasterboard of {Kg(LeastSheet)} each at least{(floors ? " where there is a separating floor" : string.Empty)}, joints sealed.");
                        break;
                    case Outside.MasonryInner when group == 4:
                        yield return ($"it meets {name}, whose inner leaf is masonry.", "Approved Document E (2.148) gives no junction of a framed separating wall, type 4, with a masonry inner leaf - seek specialist advice.");
                        break;
                    case Outside.MasonryInner when group == 1 && inner < LeastLeaf - 0.5 && (floors || !OpenedBothSides(document, wall, type, end, outside)):
                        yield return ($"it meets {name}, whose masonry inner leaf is {Kg(inner)} without its finish.",
                            floors ? $"Approved Document E (2.39) asks the inner leaf {beside} for {Kg(LeastLeaf)} at least without its finish where there is a separating floor."
                                : $"Approved Document E (2.38) asks the inner leaf {beside} for {Kg(LeastLeaf)} at least without its finish - unless there are openings 1 m high at least in it on both sides of the separating wall at every storey, within 700 of its faces.");
                        break;
                    case Outside.MasonryInner when group == 2 && inner < LeastLeaf - 0.5 && (floors || code is not ("2.1" or "2.3" or "2.4")):
                        yield return ($"it meets {name}, whose masonry inner leaf is {Kg(inner)} without its finish.",
                            floors ? $"Approved Document E (2.76) asks the inner leaf {beside} for {Kg(LeastLeaf)} at least without its finish where there is a separating floor, whatever the wall type."
                                : $"Approved Document E (2.75) asks the inner leaf {beside} for {Kg(LeastLeaf)} at least without its finish - or make the separating wall type 2.1, 2.3 or 2.4, which ask no least mass of it.");
                        break;
                    case Outside.MasonryInner when group == 3:
                        if (floors && inner < LeastLeaf - 0.5)
                            yield return ($"it meets {name}, whose masonry inner leaf is {Kg(inner)} without its finish.", $"Approved Document E (2.110) asks the inner leaf {beside} for {Kg(LeastLeaf)} at least without its finish where there is a separating floor.");
                        if (!lined && !(!floors && code is "3.1" or "3.3" && inner >= LeastLeaf - 0.5))
                            yield return ($"it meets {name}, whose masonry inner leaf is not lined with an independent panel.",
                                "Approved Document E (2.109b, 2.111, 2.112) asks the inner leaf beside wall type 3 to be lined with independent panels as the separating wall is - or, with no separating floor and wall type 3.1 or 3.3, to weigh 120 kg/m² at least without its finish.");
                        break;
                }
            }
        }

        // Internal walls that abut it (2.43, 2.44, 2.80 to 2.83, 2.115 to 2.118, 2.152, 2.153).
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var light = new List<string>();
        var masonry = new List<string>();
        foreach (var other in document.Walls.Where(other => other != wall && other.LevelId == wall.LevelId && !document.IsCurtainWall(other)))
        {
            if (document.GetWallType(other) is not { } otherType || otherType.Function == WallFunction.Exterior || otherType.Separating || Between(document, other, otherType) is not null) continue;
            if (Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) < 0.5) continue;
            var reach = type.Structure.TotalWidth / 2 + otherType.Structure.TotalWidth / 2 + 50;
            if (Line2D.DistanceFromSegment(other.Start, wall.Start, wall.End) > reach && Line2D.DistanceFromSegment(other.End, wall.Start, wall.End) > reach) continue;
            if (MasonryOf(document, otherType) is not { } inner) continue;
            if (group == 3) masonry.Add(otherType.Name);
            else if (group is 1 or 2 && inner < LeastLeaf - 0.5 && !(group == 2 && !floors && code is "2.3" or "2.4")) light.Add($"{otherType.Name}, {Kg(inner)}");
        }

        if (masonry.Count > 0)
            yield return ($"an internal masonry wall abuts it: {string.Join("; ", masonry.Distinct())}.", "Approved Document E (2.118) asks the internal walls abutting a separating wall of type 3 not to be of masonry.");
        if (light.Count > 0)
            yield return ($"an internal masonry wall abutting it weighs less than {Kg(LeastLeaf)} without its finish: {string.Join("; ", light.Distinct())}.",
                $"Approved Document E ({(group == 1 ? "2.44" : floors ? "2.81, 2.82" : "2.81, 2.83")}) asks internal masonry walls abutting a separating wall of type {code} for {Kg(LeastLeaf)} at least without their finish{(group == 2 && !floors ? " - none of type 2.3 or 2.4 with no separating floor" : string.Empty)}.");

        // Concrete floors carried under or through it (2.46 to 2.53, 2.85 to 2.89, 2.121 to 2.131, 2.155, 2.158).
        var half = type.Structure.TotalWidth / 2 + 200;
        var (a, b) = (wall.PointAt(type.Structure, wall.Length / 2, half), wall.PointAt(type.Structure, wall.Length / 2, -half));
        var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        var solidCore = group == 1 || group == 3 && code is "3.1" or "3.2";
        var cavityCore = group == 2 || code == "3.3";
        foreach (var floor in document.All<Floor>().Where(floor => floor.Boundary.Count >= 3 && floor.Contains(a) && floor.Contains(b)))
        {
            if (document.FindType<FloorType>(floor.TypeId) is not { } floorType) continue;
            var surface = floor.GetTopElevation(document);
            var under = Math.Abs(surface - bottom) <= 60;
            var through = !under && surface > bottom + 100 && floor.GetBottomElevation(document) < top + 1 && surface <= top + 60;
            if (!under && !through) continue;
            var (kind, baseMass) = FloorBase(document, floorType);
            if (kind is Base.None or Base.Timber) continue;
            var ground = under && SlabRules.OnTheGround(document, floor);
            var separating = Between(document, floor, floorType) is not null;
            var what = $"{floorType.Name}{(ground ? ", a slab on the ground," : separating ? ", a separating floor," : string.Empty)}";
            if (cavityCore)
                yield return ($"{what} runs on {(under ? "under" : "through")} it, bridging its cavity.",
                    $"Approved Document E ({(group == 2 ? ground ? "2.88" : under ? "2.89" : separating ? "3.47, 3.85" : "2.85" : ground ? "2.130" : under ? "2.131" : separating ? "3.53, 3.91" : "2.122")}) asks a concrete floor not to be continuous under or through a cavity separating wall - built into each leaf, carried to its cavity face, the cavity not bridged: make it two floors, one each side.");
            else if (solidCore && kind == Base.Planks)
                yield return ($"{what} of planks or beams and blocks runs on {(under ? "under" : "through")} it.",
                    $"Approved Document E ({(separating ? "3.45, 3.50, 3.84, 3.88" : group == 1 ? "2.47, 2.53" : "2.129")}) asks hollow-core planks and beam and block floors not to be continuous under or through a solid separating wall.");
            else if (solidCore && !ground && !separating && baseMass < 365 - 0.5)
                yield return ($"{what} runs on {(under ? "under" : "through")} it, its base {Kg(baseMass)}.",
                    $"Approved Document E ({(group == 1 ? under ? "2.52" : "2.46" : under ? "2.128" : "2.121")}) lets a concrete floor be carried {(under ? "under" : "through")} a solid separating wall only where its base weighs {Kg(365)} at least.");
            else if (group == 4 && through)
                yield return ($"{what} runs on through it.", "Approved Document E (2.155) gives no junction of a framed separating wall, type 4, with an internal concrete floor - seek specialist advice.");
            else if (group == 4 && !ground && baseMass < 365 - 0.5)
                yield return ($"{what} runs on under it, its base {Kg(baseMass)}.", $"Approved Document E (2.158) lets a suspended concrete floor pass under a framed separating wall only where it weighs {Kg(365)} at least.");
        }
    }

    /// <summary>What a separating wall falls short of: being one of Section 2's constructions, and its junctions with the outside walls, the internal walls and the floors.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || Separation(document, wall, type) is not (var between, var lodging)) yield break;

        // A Robust Detail stands in for testing in a new house or a block of flats only (Regulation 41, Annex E) - a hotel's walls are tested.
        if (!lodging && type.RobustDetail.Trim().Length > 0) yield break;

        // Retained in a conversion into homes, lined with wall treatment 1: Section 4's way to 43 dB (4.22 to 4.24, round 145).
        if (!lodging && Conversion.Retained(document, wall) && Conversion.TreatmentOne(document, type)) yield break;
        var floors = SeparatingFloorAt(document, wall, type);
        var built = Wall(document, type, floors, Staggered(document, wall, type), lodging);
        var otherwise = lodging
            ? $"or show by pre-completion testing that it reaches DnT,w + Ctr {LodgingWall:0} dB (Table 0.1b, Section 1){(type.RobustDetail.Trim().Length > 0 ? " - a Robust Detail stands for that in a new house or flat only (Regulation 41, Annex E)" : string.Empty)}"
            : Conversion.Applies(document)
                ? "or, formed by a material change of use, line it with wall treatment 1 - independent panels of two layers of plasterboard, 20 kg/m², over 35 of mineral wool (Section 4, 4.22 to 4.24) - or show by pre-completion testing that it reaches DnT,w + Ctr 43 dB (Table 0.1a)"
                : "name the Robust Detail it is built to in Edit Type, or show by pre-completion testing that it reaches DnT,w + Ctr 45 dB (Table 0.1a, Section 1)";
        if (built.Group == 0)
        {
            yield return ($"it separates {between}: {built.What}.", lodging
                ? $"Approved Document E (E1, 6.4) gives, between rooms for residential purposes, solid masonry walls - type 1 - and a solid masonry core between independent panels - types 3.1 and 3.2 - or with care to keep their leaves apart cavity masonry and timber frames, types 2 and 4; this is none of them: {string.Join("; ", built.Short)}. Build it as one, {otherwise}."
                : $"Approved Document E (E1, Section 2, Diagram 2.1) gives four kinds of separating wall - solid masonry, cavity masonry, masonry between independent panels, and timber frames with absorbent material - and this is none of them: {string.Join("; ", built.Short)}. Build it as one, {otherwise}.");
            yield break;
        }

        // Of Section 2's walls, Section 6 gives these between rooms for residential purposes - 2 and 4 with care (6.4).
        if (lodging && built.Type == "3.3")
        {
            yield return ($"it separates {between}: {built.What}, wall type 3.3.",
                $"Approved Document E (6.4) gives, of Section 2's walls, solid masonry - types 1.1, 1.2 and 1.3 - and a solid masonry core between independent panels - types 3.1 and 3.2 - between rooms for residential purposes, and cavity masonry and timber frames - types 2 and 4 - with care to keep their leaves apart; not a cavity core between panels, type 3.3. Build it as one of them, {otherwise}.");
            yield break;
        }

        // Its junctions once it is one of them: until then, what it is made of comes first.
        if (built.Short.Count > 0)
            yield return ($"it separates {between}: {built.What}.",
                $"Approved Document E (E1{(lodging ? ", 6.4" : string.Empty)}) asks wall type {built.Type}, {built.Name}, for {string.Join("; ", built.Short)} - or build it as another of {(lodging ? "Section 6's" : "Section 2's")} walls, {otherwise}.");
        else
        {
            foreach (var problem in Junctions(document, wall, type, built, floors)) yield return problem;
            if (Top(document, wall, type, built, between, lodging) is { } top) yield return top;
        }
    }

    /// <summary>
    /// What a separating wall falls short of at its head: up to the underside of the roof over it (2.55, 2.91, 2.133, 2.160); between rooms for
    /// residential purposes a solid masonry wall may stop at a ceiling under the roof or the floor over it - two layers of plasterboard or more of
    /// 20 kg/m², 200 of mineral wool of 10 kg/m³ over it, neither joists nor boards run on over the wall (6.14, Diagram 6.1). A party wall type's
    /// own gap to the roof is Approved Document B's to say (5.11), not said twice.
    /// </summary>
    private static (string Said, string Rule)? Top(BimDocument document, Wall wall, WallType type, WallConstruction built, string between, bool lodging)
    {
        var middle = wall.Start.MidpointTo(wall.End);
        var top = wall.GetTopElevation(document);

        // A wall on the same line going on up from its head: it is that one's head that counts.
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        if (document.Walls.Any(other => other != wall && Math.Abs(way.Cross(other.Start.MidpointTo(other.End) - wall.Start)) < 50 && Math.Abs(way.Cross((other.End - other.Start).NormalisedOrDefault(way))) < 0.02
                                        && other.GetTopElevation(document) > top + 1 && other.GetBaseElevation(document) <= top + 1 && Line2D.DistanceFromSegment(middle, other.Start, other.End) < 50))
            return null;

        // What is over it: the floor of the storey above, or the roof.
        var levels = document.Levels.OrderBy(level => level.Elevation).ToList();
        var at = levels.FindIndex(level => level.Id == wall.LevelId);
        var next = at >= 0 && at + 1 < levels.Count ? levels[at + 1].Id : Guid.Empty;
        var floors = document.All<Floor>().Where(floor => floor.LevelId == next && floor.Contains(middle)).Select(floor => floor.GetBottomElevation(document)).Where(bottom => bottom > top - 1).ToList();
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Contains(middle)).Select(roof => roof.UndersideAt(document, middle)).Where(underside => underside > top - 1).ToList();
        var (over, what) = floors.Count > 0 ? (floors.Min(), "floor") : roofs.Count > 0 ? (roofs.Min(), "roof") : (double.NaN, string.Empty);

        // Attached to the roof over it, it follows its underside up (round 147).
        if (what == "roof" && wall.AttachedRoof(document) is { IsExtrusion: false } attached && attached.Contains(middle)) return null;
        if (double.IsNaN(over) || over - top <= 50 || !lodging && (what == "floor" || type.Separating)) return null;
        var gap = over - top;
        var paragraph = built.Group switch { 1 => "2.55, 2.56", 2 => "2.91, 2.92", 3 => "2.133, 2.134", _ => "2.160, 2.161" };
        var upTo = $"run it up to the underside of the {what}, its joint with it filled with a flexible closer that also stops fire";
        if (!lodging)
            return ($"it separates {between} and stops {gap:0} below the roof over it.",
                $"Approved Document E ({paragraph}) asks a separating wall {(built.Group == 4 ? "preferably " : string.Empty)}to be continuous to the underside of the roof - {upTo}; a lighter wall over a ceiling of {Kg(LeastSheet)} with sealed joints is drawn as a wall of its own.");
        if (built.Group != 1)
            return ($"it separates {between} and stops {gap:0} below the {what} over it.",
                $"Approved Document E (6.14, {paragraph}) lets only a solid masonry separating wall, type 1, stop at a ceiling between rooms for residential purposes - {upTo}.");

        // A solid wall stopped at a ceiling: the ceilings either side of it, by Diagram 6.1.
        var reach = type.Structure.TotalWidth / 2 + 150;
        var sides = new[] { wall.PointAt(type.Structure, wall.Length / 2, reach), wall.PointAt(type.Structure, wall.Length / 2, -reach) };
        var ceilings = document.All<Ceiling>().Where(ceiling => ceiling.LevelId == wall.LevelId && ceiling.Boundary.Count >= 3).ToList();
        var wanting = new List<string>();
        var under = sides.Select(side => ceilings.Where(ceiling => ceiling.Contains(side)).OrderBy(ceiling => ceiling.GetBottomElevation(document)).FirstOrDefault()).ToList();
        if (under.Any(ceiling => ceiling is null)) wanting.Add("no ceiling drawn on both sides of it");
        else
        {
            foreach (var ceiling in under.Distinct())
            {
                var kind = document.FindType<CeilingType>(ceiling!.TypeId);
                var layers = kind is null ? new List<Piece>() : Pieces(document, kind.Structure);
                var boards = layers.Where(piece => piece.Part == Part.Plasterboard).ToList();
                var wool = layers.Where(piece => piece.Wool && piece.Density >= 10 - 0.5).Sum(piece => piece.Thickness);
                var name = kind?.Name ?? "a ceiling";
                if (boards.Count < 2 || boards.Sum(piece => piece.Mass) < 20 - 0.05) wanting.Add($"{name} is not two layers of plasterboard or more of {Kg(20)} together");
                if (wool < 200 - 0.5) wanting.Add($"{name} has {wool:0} of mineral wool of 10 kg/m³ over it, not 200");
                if (ceiling.GetBottomElevation(document) > top + 50) wanting.Add($"it stops {ceiling.GetBottomElevation(document) - top:0} short of {name}");
            }

            if (under[0] == under[1]) wanting.Add($"{document.FindType<CeilingType>(under[0]!.TypeId)?.Name ?? "the ceiling"} runs on over it from one room to the other");
        }

        return wanting.Count == 0 ? null : ($"it separates {between} and stops {gap:0} below the {what} over it at a ceiling: {string.Join("; ", wanting)}.",
            $"Approved Document E (6.14, Diagram 6.1) lets a solid masonry wall between rooms for residential purposes stop at a ceiling of two layers of plasterboard or more, {Kg(20)} together, not perforated, with a layer of mineral wool 200 thick at least, of 10 kg/m³, over it, and neither the ceiling's joists nor its boards running on from one room to the other - else {upTo} ({paragraph}). Part B must allow it too (6.15).");
    }

    // --- Separating floors (Section 3) -------------------------------------------

    internal enum Base
    {
        None,
        Concrete,
        Planks,
        Timber
    }

    /// <summary>What a floor's base is - concrete cast in situ, concrete planks or beams and blocks, timber joists - and its mass with any screed bonded to it (3.15, 3.16), kg/m².</summary>
    internal static (Base Kind, double Mass) FloorBase(BimDocument document, FloorType type)
    {
        var (pieces, from, to, kind) = Parse(document, type);
        if (kind == Base.None) return (kind, 0);
        var mass = pieces.Skip(from).Take(to - from + 1).Sum(piece => piece.Mass);
        for (var i = from - 1; i >= 0 && pieces[i].Heavy && kind != Base.Timber; i--) mass += pieces[i].Mass;
        return (kind, mass);
    }

    private static (List<Piece> Pieces, int From, int To, Base Kind) Parse(BimDocument document, FloorType type)
    {
        var pieces = Pieces(document, type.Structure);
        var structure = pieces.Select((piece, i) => (Piece: piece, At: i)).Where(entry => entry.Piece.Layer.Function == LayerFunction.Structure).ToList();
        var concrete = structure.Where(entry => entry.Piece.Part == Part.Masonry).ToList();
        if (concrete.Count == 0 && structure.Count == 0)
            concrete = pieces.Select((piece, i) => (Piece: piece, At: i)).Where(entry => entry.Piece.Part == Part.Masonry).OrderByDescending(entry => entry.Piece.Thickness).Take(1).ToList();
        if (concrete.Count > 0)
        {
            var planks = concrete.Any(entry => Has(entry.Piece.Name, "plank", "hollow", "precast", "beam and block", "block and beam", "beam & block"));
            return (pieces, concrete.Min(entry => entry.At), concrete.Max(entry => entry.At), planks ? Base.Planks : Base.Concrete);
        }

        var joists = structure.Where(entry => entry.Piece.Part is Part.Frame or Part.Gap).ToList();
        return joists.Count > 0 ? (pieces, joists.Min(entry => entry.At), joists.Max(entry => entry.At), Base.Timber) : (pieces, 0, -1, Base.None);
    }

    private static bool Soft(string name) => Has(name, "carpet", "cushion", "rubber", "resilient", "underlay", "soft", "cork", "felt");

    /// <summary>
    /// The best ceiling treatment a ceiling's layers - from the base down - give (3.19 to 3.22): A (3), an independent ceiling of two layers of
    /// plasterboard, 20 kg/m², 100 mm of mineral wool over it, 100 mm clear of the base; B (2), plasterboard of 10 kg/m² with mineral wool in its
    /// void - its resilient bars not read; C (1), plasterboard of 10 kg/m² on battens or channels; none (0).
    /// </summary>
    private static int Treatment(IReadOnlyList<Piece> under, double clearance)
    {
        var boards = under.Where(piece => piece.Part == Part.Plasterboard).ToList();
        if (boards.Count == 0 || boards.Sum(piece => piece.Mass) < LeastSheet - 0.05) return 0;
        var wool = under.Where(piece => piece.Wool && piece.Density >= 10 - 0.5).Sum(piece => piece.Thickness);
        if (boards.Count >= 2 && boards.Sum(piece => piece.Mass) >= 20 - 0.05 && wool >= 100 - 0.5 && clearance >= 100 - 0.5) return 3;
        return wool > 0 ? 2 : 1;
    }

    private static string TreatmentSaid(int level) => level switch { 3 => "A", 2 => "B", 1 => "C", _ => "none" };

    /// <summary>The ceiling under a floor: the best treatment its own layers under its base give, or a ceiling drawn in the rooms under it.</summary>
    private static int CeilingUnder(BimDocument document, Floor? floor, List<Piece> pieces, int to)
    {
        var own = pieces.Skip(to + 1).ToList();
        var top = own.FindIndex(piece => piece.Part == Part.Plasterboard);
        var best = Treatment(own, top < 0 ? 0 : own.Take(top).Sum(piece => piece.Thickness));
        if (floor is null) return best;
        var underside = floor.GetBottomElevation(document);
        var (_, under) = Rooms(document, floor);
        foreach (var ceiling in document.All<Ceiling>().Where(ceiling => ceiling.Boundary.Count >= 3 && under.Any(room => room.LevelId == ceiling.LevelId && ceiling.Contains(room.Location))))
        {
            if (document.FindType<CeilingType>(ceiling.TypeId) is not { } kind) continue;
            var layers = Pieces(document, kind.Structure);
            var first = layers.FindIndex(piece => piece.Part == Part.Plasterboard);
            var clear = underside - ceiling.GetTopElevation(document) + (first < 0 ? 0 : layers.Take(first).Sum(piece => piece.Thickness));
            best = Math.Max(best, Treatment(own.Concat(layers).ToList(), clear));
        }

        return best;
    }

    /// <summary>
    /// Which of Section 3's separating floors a floor type is - 1.1C, 1.2B, 2.1C, 2.2B, 3.1A, or none - what it is and what it falls short of
    /// being so. A floor drawn gives the rooms over it, for their soft floor finish, and the ceilings drawn under it.
    /// </summary>
    public static WallConstruction Floor(BimDocument document, FloorType type, Floor? floor = null)
    {
        var (pieces, from, to, kind) = Parse(document, type);
        var mass = pieces.Sum(piece => piece.Mass);
        if (kind == Base.None)
            return new(0, string.Empty, string.Empty, "a floor of neither concrete nor timber joists", mass, new[] { "its base is neither concrete nor timber joists" });
        var ceiling = CeilingUnder(document, floor, pieces, to);
        var over = floor is null ? new List<Room>() : Rooms(document, floor).Over;
        var above = pieces.Take(from).ToList();
        var shortOf = new List<string>();

        if (kind == Base.Timber)
        {
            // A timber frame base with ceiling treatment A and a platform floor (3.100 to 3.102).
            var resilient = above.FindLastIndex(piece => piece.Part == Part.Gap);
            var deck = above.Skip(resilient + 1).Where(piece => piece.Part is Part.Board or Part.Plasterboard).Sum(piece => piece.Mass);
            var floating = resilient < 0 ? new List<Piece>() : above.Take(resilient).Where(piece => piece.Part is Part.Board or Part.Plasterboard).ToList();
            var wool = resilient < 0 ? null : above.Take(resilient + 1).LastOrDefault(piece => piece.Part == Part.Gap);
            if (deck < 20 - 0.05) shortOf.Add($"a deck of {Kg(20)} at least on its joists, not {Kg(deck)} (3.100)");
            if (resilient < 0 || floating.Count < 2 || floating.Any(piece => piece.Thickness < 8 - 0.05) || floating.Sum(piece => piece.Mass) < 25 - 0.05)
                shortOf.Add($"a platform floor of two boards 8 mm thick at least, {Kg(25)} together, laid loose on its resilient layer (3.101)");
            if (wool is null || !wool.Wool || wool.Thickness < 25 - 0.5 || wool.Density is < 60 - 0.5 or > 100 + 0.5)
                shortOf.Add("a resilient layer of mineral wool 25 thick at least, of 60 to 100 kg/m³ (3.102)");
            if (ceiling < 3) shortOf.Add($"ceiling treatment A - an independent ceiling of two layers of plasterboard, {Kg(20)}, 100 of mineral wool over it, 100 clear of the joists - not {TreatmentSaid(ceiling)} (3.19, 3.100)");
            return new(3, "3.1A", "a timber frame base with ceiling treatment A and a platform floor", $"a floor of timber joists, {Kg(mass)}", mass, shortOf);
        }

        // A concrete base: with a soft floor covering (type 1) or a floating floor (type 2).
        var (_, baseMass) = FloorBase(document, type);
        var bonded = from - 1;
        while (bonded >= 0 && above[bonded].Heavy) bonded--;
        var resilientAt = above.Take(bonded + 1).ToList().FindLastIndex(piece => piece.Part == Part.Gap);
        var planks = kind == Base.Planks;
        var what = $"a floor of concrete {(planks ? "planks" : "cast in situ")}, its base {Kg(baseMass)} with any screed bonded to it";
        if (resilientAt >= 0)
        {
            var resilient = above.Take(resilientAt + 1).Reverse().TakeWhile(piece => piece.Part == Part.Gap).ToList();
            var layer = above.Take(resilientAt + 1 - resilient.Count).ToList();
            var screed = layer.Where(piece => piece.Heavy).Sum(piece => piece.Mass);
            var raft = layer.Where(piece => piece.Part is Part.Board or Part.Plasterboard).Sum(piece => piece.Mass);
            var (code, name, least, treatment, paragraph) = planks ? ("2.2B", "concrete planks, a floating floor and ceiling treatment B", 300.0, 2, "3.68") : ("2.1C", "a solid concrete slab, a floating floor and ceiling treatment C", 300.0, 1, "3.67");
            if (baseMass < least - 0.5) shortOf.Add($"{Kg(least)} at least of its base with any bonded screed, not {Kg(baseMass)} - a floating screed not counted ({paragraph}, 3.16)");
            if (!resilient.Any(piece => piece.Wool && piece.Thickness >= 25 - 0.5 && piece.Density >= 36 - 0.5))
                shortOf.Add($"a resilient layer of mineral wool 25 thick at least, of 36 kg/m³, not {string.Join(" and ", resilient.Select(piece => piece.Name.Length > 0 ? piece.Name : "a gap"))} - another only by its dynamic stiffness, 15 MN/m³ at most (3.64, 3.65)");
            if (screed < 80 - 0.5 && raft < 12 - 0.5) shortOf.Add($"a floating layer of {Kg(80)} of screed, or a timber raft of {Kg(12)} on battens, not {Kg(Math.Max(screed, raft))} (3.64, 3.65)");
            if (ceiling < treatment) shortOf.Add($"ceiling treatment {TreatmentSaid(treatment)} or better under it, not {TreatmentSaid(ceiling)} (3.21, 3.22, {paragraph})");
            return new(2, code, name, what, mass, shortOf);
        }

        {
            var (code, name, treatment, paragraph) = planks ? ("1.2B", "concrete planks, a soft floor covering and ceiling treatment B", 2, "3.30") : ("1.1C", "a solid concrete slab, a soft floor covering and ceiling treatment C", 1, "3.29");
            if (baseMass < 365 - 0.5) shortOf.Add($"{Kg(365)} at least of its base with any bonded screed, not {Kg(baseMass)} ({paragraph})");
            var soft = pieces.Count > 0 && Soft(pieces[0].Name) && pieces[0].Thickness >= 4.5 - 0.05 || over.Count > 0 && over.All(room => Soft(room.FloorFinish));
            if (!soft) shortOf.Add($"a soft floor covering 4.5 thick at least - carpet or the like, in its layers or the rooms' floor finish - or a floating floor, floor type 2 ({paragraph}, 3.28)");
            if (ceiling < treatment) shortOf.Add($"ceiling treatment {TreatmentSaid(treatment)} or better under it, not {TreatmentSaid(ceiling)} (3.21, 3.22, {paragraph})");
            return new(1, code, name, what, mass, shortOf);
        }
    }

    /// <summary>
    /// Where a floating floor or its ceiling runs on from one room for residential purposes to the next (6.8): the floor itself over two of them or
    /// more, its own ceiling layers under two or more, a ceiling drawn under it under two or more - each said.
    /// </summary>
    private static List<string> Continuous(BimDocument document, Floor floor, FloorType type)
    {
        var suites = Lodging.Suites(document);
        var (over, under) = Rooms(document, floor);
        string? Names(IEnumerable<Room> rooms)
        {
            var names = rooms.Select(room => suites.GetValueOrDefault(room)).OfType<string>().Distinct().OrderBy(name => name, StringComparer.Ordinal).ToList();
            return names.Count < 2 ? null : names.Count > 3 ? $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more" : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
        }

        var said = new List<string>();
        if (Names(over) is { } floating) said.Add($"its floating floor runs on over {floating}");
        var (pieces, _, to, _) = Parse(document, type);
        if (pieces.Skip(to + 1).Any(piece => piece.Part == Part.Plasterboard) && Names(under) is { } below) said.Add($"its own ceiling runs on under {below}");
        foreach (var ceiling in document.All<Ceiling>().Where(ceiling => ceiling.Boundary.Count >= 3))
            if (Names(under.Where(room => room.LevelId == ceiling.LevelId && ceiling.Contains(room.Location))) is { } spans)
                said.Add($"{document.FindType<CeilingType>(ceiling.TypeId)?.Name ?? "a ceiling"} under it runs on under {spans}");
        return said;
    }

    /// <summary>What a separating floor falls short of: being one of Section 3's floors, and its junctions with the outside walls and the masonry walls under it.</summary>
    internal static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Floor floor, FloorType type)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || Separation(document, floor, type) is not (var between, var lodging)) yield break;

        // A Robust Detail stands in for testing in a new house or a block of flats only (Regulation 41, Annex E) - a hotel's floors are tested.
        if (!lodging && type.RobustDetail.Trim().Length > 0) yield break;
        var built = Floor(document, type, floor);
        var otherwise = lodging
            ? $"or show by pre-completion testing that it reaches DnT,w + Ctr 45 dB and L'nT,w 62 dB (Table 0.1b, Section 1){(type.RobustDetail.Trim().Length > 0 ? " - a Robust Detail stands for that in a new house or flat only (Regulation 41, Annex E)" : string.Empty)}"
            : "name the Robust Detail it is built to in Edit Type, or show by pre-completion testing that it reaches DnT,w + Ctr 45 dB and L'nT,w 62 dB (Table 0.1a, Section 1)";
        if (built.Group == 0)
        {
            yield return ($"it separates {between}: {built.What}.", lodging
                ? $"Approved Document E (E1, 6.8) gives, between rooms for residential purposes, a concrete slab or concrete planks with a soft floor covering and a ceiling - floor types 1.1C and 1.2B - or, their floating floors and ceilings not running on from one room to the next, floor types 2 and 3; this is none of them. Build it as one, {otherwise}."
                : $"Approved Document E (E1, Section 3, Diagram 3.1) gives three kinds of separating floor - a concrete base with a ceiling and a soft floor covering, a concrete base with a ceiling and a floating floor, and a timber frame base with a ceiling and a platform floor - and this is none of them. Build it as one, {otherwise}.");
            yield break;
        }

        // Its junctions once it is one of them: until then, what it is made of comes first.
        if (built.Short.Count > 0)
        {
            yield return ($"it separates {between}: {built.What}.",
                $"Approved Document E (E1{(lodging ? ", 6.8" : string.Empty)}) asks floor type {built.Type}, {built.Name}, for {string.Join("; ", built.Short)} - or build it as another of Section 3's floors, {otherwise}.");
            yield break;
        }

        // Between rooms for residential purposes a floating floor and its ceiling are not to run on from one room to the next (6.8).
        if (lodging && built.Group is 2 or 3 && Continuous(document, floor, type) is { Count: > 0 } running)
            yield return ($"it separates {between}: {string.Join("; ", running)}.",
                $"Approved Document E (6.8) lets floor types 2 and 3 - floating floors - be used between rooms for residential purposes only where their floating floors and ceilings do not run on from one room for residential purposes to the next: draw floor type {built.Type}'s floor and its ceiling room by room, or build floor type 1.1C or 1.2B, a soft covering on concrete. Specialist advice may be needed.");

        // The outside walls round it, on its storey and the one under it (3.31 to 3.37, 3.69 to 3.75, 3.103 to 3.113).
        var timber = built.Group == 3;
        var level = document.FindLevel(floor.LevelId)?.Elevation ?? 0;
        var below = document.Levels.Where(other => other.Elevation < level - 1).OrderByDescending(other => other.Elevation).FirstOrDefault();
        var storeys = new[] { floor.LevelId, below?.Id ?? Guid.Empty };
        bool Round(Wall wall, WallType wallType)
        {
            var middle = wall.Start.MidpointTo(wall.End);
            var edge = Enumerable.Range(0, floor.Boundary.Count).Min(i => Line2D.DistanceFromSegment(middle, floor.Boundary[i], floor.Boundary[(i + 1) % floor.Boundary.Count]));
            return edge <= wallType.Structure.TotalWidth / 2 + 300;
        }

        var heavy = new List<string>();
        var boards = new List<string>();
        var panels = new List<string>();
        var solid = new List<string>();
        foreach (var wall in document.Walls.Where(wall => storeys.Contains(wall.LevelId) && !document.IsCurtainWall(wall)))
        {
            if (document.GetWallType(wall) is not { Function: WallFunction.Exterior } wallType || !Round(wall, wallType)) continue;
            var (kind, inner, sheets, lined) = OutsideWall(document, wallType);
            if (kind == Outside.Solid) solid.Add(wallType.Name);
            else if (kind == Outside.FrameInner && sheets < 2) boards.Add(wallType.Name);
            else if (kind == Outside.MasonryInner && !timber && inner < LeastLeaf - 0.5) heavy.Add($"{wallType.Name}, {Kg(inner)}");
            else if (kind == Outside.MasonryInner && timber && !lined && inner <= 375 + 0.5) panels.Add(wallType.Name);
        }

        if (heavy.Count > 0)
            yield return ($"an outside wall round it has a masonry inner leaf of less than {Kg(LeastLeaf)} without its finish: {string.Join("; ", heavy.Distinct())}.",
                $"Approved Document E ({(built.Group == 1 ? "3.32" : "3.70")}) asks the masonry inner leaf of an outside cavity wall at a separating concrete floor for {Kg(LeastLeaf)} at least without its finish, the floor built into it.");
        if (boards.Count > 0)
            yield return ($"an outside wall round it has a timber frame inner leaf with less than two layers of plasterboard: {string.Join("; ", boards.Distinct())}.",
                $"Approved Document E ({(built.Group == 1 ? "3.36c" : built.Group == 2 ? "3.74c" : "3.110")}) asks the framed inner leaf of an outside wall at a separating floor to be finished with two layers of plasterboard of {Kg(LeastSheet)} each at least, joints sealed.");
        if (panels.Count > 0)
            yield return ($"an outside wall round it has a masonry inner leaf not lined with an independent panel: {string.Join("; ", panels.Distinct())}.",
                "Approved Document E (3.104, 3.107) asks the masonry inner leaf at a timber separating floor to be lined with an independent panel as for wall type 3 - unless it weighs more than 375 kg/m².");
        if (solid.Count > 0)
            yield return ($"an outside wall round it is of solid masonry: {string.Join("; ", solid.Distinct())}.",
                $"Approved Document E ({(built.Group == 1 ? "3.37" : built.Group == 2 ? "3.75" : "3.113")}) gives no junction of a separating floor with a solid masonry outside wall - seek specialist advice.");

        // The internal masonry walls under it, joined to it (3.39, 3.40, 3.77, 3.78, 3.116).
        if (below is null) yield break;
        var underside = floor.GetBottomElevation(document);
        var light = new List<string>();
        var any = false;
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == below.Id && !document.IsCurtainWall(wall) && floor.Contains(wall.Start.MidpointTo(wall.End))))
        {
            if (document.GetWallType(wall) is not { } wallType || wallType.Function == WallFunction.Exterior || wall.GetTopElevation(document) < underside - 50) continue;
            if (MasonryOf(document, wallType) is not { } inner) continue;
            any = true;
            if (inner < LeastLeaf - 0.5) light.Add($"{wallType.Name}, {Kg(inner)}");
        }

        if (timber && any)
            yield return ("a masonry wall under it holds it up.", "Approved Document E (3.116) gives no junction of a timber separating floor with an internal masonry wall - seek specialist advice.");
        else if (!timber && light.Count > 0)
            yield return ($"an internal masonry wall under it weighs less than {Kg(LeastLeaf)} without its finish: {string.Join("; ", light.Distinct())}.",
                $"Approved Document E ({(built.Group == 1 ? "3.40" : "3.78")}) asks an internal wall carrying a separating concrete floor, or joined rigidly to it, for {Kg(LeastLeaf)} at least without its finish.");
    }

    // --- Doors (2.26) --------------------------------------------------------------

    /// <summary>What a flat's entrance door from the common corridor, hall or stair falls short of: good perimeter seals and 25 kg/m² or Rw 29 dB (2.26).</summary>
    internal static (string Said, string Rule)? Problem(BimDocument document, Door door, DoorType type)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || type.AcousticRating >= LeastDoor - 0.05) return null;

        // A room for residential purposes' door from the corridor (6.5, 6.6).
        var suites = Lodging.Suites(document);
        if (suites.Count > 0 && Doors.RoomsBeside(document, door) is ({ } one, { } two) && one != two && suites.ContainsKey(one) != suites.ContainsKey(two))
        {
            var (inside, outside) = suites.ContainsKey(one) ? (one, two) : (two, one);
            return FireSeparation.Circulation(outside)
                ? ($"it is {suites[inside]}'s door from the {outside.Name}, {(type.AcousticRating > 0 ? $"Rw {type.AcousticRating:0} dB" : "its sound reduction not given")}.",
                    $"Approved Document E (6.5, 6.6) asks a door from a corridor into a room for residential purposes for good perimeter sealing - its threshold too, where practical - and 25 kg/m² at least or a doorset of Rw {LeastDoor:0} dB, as well as what Part B asks of it: put its Acoustic Rating (Rw) in Edit Type.")
                : null;
        }

        if (Dwellings.Named(document).Count < 2) return null;
        var (a, b) = Doors.RoomsBeside(document, door);
        if (a is null || b is null || a == b || Dwellings.Of(a).Length > 0 == Dwellings.Of(b).Length > 0) return null;
        var (flat, other) = Dwellings.Of(a).Length > 0 ? (a, b) : (b, a);
        if (!FireSeparation.Circulation(other)) return null;
        return ($"it is {Dwellings.Of(flat)}'s entrance door from the {other.Name}, {(type.AcousticRating > 0 ? $"Rw {type.AcousticRating:0} dB" : "its sound reduction not given")}.",
            $"Approved Document E (2.25, 2.26) asks a door from a corridor into a flat for good perimeter sealing - its threshold too, where practical - and 25 kg/m² at least or Rw {LeastDoor:0} dB: put its Acoustic Rating (Rw) in Edit Type.");
    }

    // --- Reverberation in the common parts (Section 7) ---------------------------

    /// <summary>
    /// How much of a room's plan is under an absorber of Class C or better, and of Class D or better, m²: the ceilings drawn in it, by the class of
    /// their lowest layer's material, and its ceiling finish where that names a material with a class.
    /// </summary>
    private static (double C, double D) Absorbing(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var area = Polygon2D.Area(outline) / 1e6;
        AbsorptionClass? Named(string name) => document.Materials.FirstOrDefault(material => string.Equals(material.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } material
            ? SoundAbsorption.Of(material) : null;
        if (room.CeilingFinish.Trim().Length > 0 && Named(room.CeilingFinish) is { } finish)
            return (SoundAbsorption.AtLeast(finish, AbsorptionClass.C) ? area : 0, SoundAbsorption.AtLeast(finish, AbsorptionClass.D) ? area : 0);

        var ceilings = document.All<Ceiling>().Where(ceiling => ceiling.LevelId == room.LevelId && ceiling.Boundary.Count >= 3)
            .Select(ceiling => (Ceiling: ceiling, Class: document.FindType<CeilingType>(ceiling.TypeId) is { } kind && kind.Structure.Layers.LastOrDefault(layer => layer.Thickness > 0) is { } lowest
                && document.FindMaterial(lowest.MaterialId) is { } material ? SoundAbsorption.Of(material) : null))
            .Where(entry => entry.Class is not null).ToList();
        if (ceilings.Count == 0) return (0, 0);

        // The plan sampled on a grid: each point in the room under the best absorber over it.
        var (minX, maxX, minY, maxY) = (outline.Min(p => p.X), outline.Max(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.Y));
        var step = Math.Clamp(Math.Sqrt(area * 1e6) / 60, 50, 250);
        var (inside, atC, atD) = (0, 0, 0);
        for (var x = minX + step / 2; x < maxX; x += step)
        for (var y = minY + step / 2; y < maxY; y += step)
        {
            var point = new Point2D(x, y);
            if (!Polygon2D.Contains(outline, point)) continue;
            inside++;
            var best = ceilings.Where(entry => entry.Ceiling.Contains(point)).Select(entry => entry.Class!.Value).DefaultIfEmpty(AbsorptionClass.E + 1).Min();
            if (best <= AbsorptionClass.C) atC++;
            if (best <= AbsorptionClass.D) atD++;
        }

        return inside == 0 ? (0, 0) : (area * atC / inside, area * atD / inside);
    }

    /// <summary>Whether a room is a corridor or hallway - its longest floor dimension more than three times its shortest (7.2) - or an entrance hall (7.3), measured along its longest side.</summary>
    private static bool Corridor(IReadOnlyList<Point2D> outline)
    {
        var longest = Enumerable.Range(0, outline.Count).OrderByDescending(i => outline[i].DistanceTo(outline[(i + 1) % outline.Count])).First();
        var way = (outline[(longest + 1) % outline.Count] - outline[longest]).NormalisedOrDefault(Vector2D.UnitX);
        var across = way.PerpendicularLeft();
        var length = outline.Max(p => way.Dot(p - outline[0])) - outline.Min(p => way.Dot(p - outline[0]));
        var width = outline.Max(p => across.Dot(p - outline[0])) - outline.Min(p => across.Dot(p - outline[0]));
        return Math.Min(length, width) > 0 && Math.Max(length, width) / Math.Min(length, width) > 3;
    }

    /// <summary>
    /// The common parts whose absorption E3 asks for (7.1): a block of flats' corridors, halls and stairs; else those of a building with rooms for
    /// residential purposes that give access to them - the corridors, halls, landings and lobbies their doors open into, and those reached from
    /// them, and the stairs joining them (round 105) - with the outlines of the rooms, and what the building is, said.
    /// </summary>
    private static (IReadOnlyDictionary<Room, IReadOnlyList<Point2D>> Outlines, List<Room> Halls, List<List<Room>> Stacks, string What)? CommonParts(BimDocument document)
    {
        if (FireSeparation.Of(document) is { } block)
        {
            var stacked = block.Stacks.SelectMany(stack => stack).ToHashSet();
            return (block.Outlines, block.Common.Where(room => FireSeparation.Circulation(room) && !stacked.Contains(room) && !block.Stairs.Contains(room)).ToList(), block.Stacks, "a block of flats");
        }

        var suites = Lodging.Suites(document);
        if (suites.Count == 0) return null;
        var plan = Toilets.Plan(document);
        var outlines = plan.ToDictionary(entry => entry.Key, entry => entry.Value.Outline);
        var reached = new HashSet<Room>();
        var queue = new Queue<Room>();
        foreach (var room in suites.Keys)
        foreach (var (_, other) in plan[room].Doors)
            if (other is not null && FireSeparation.Circulation(other) && !suites.ContainsKey(other) && reached.Add(other))
                queue.Enqueue(other);
        while (queue.Count > 0)
            foreach (var (_, other) in plan[queue.Dequeue()].Doors)
                if (other is not null && plan.ContainsKey(other) && FireSeparation.Circulation(other) && !suites.ContainsKey(other) && reached.Add(other))
                    queue.Enqueue(other);

        var flights = Escape.Flights(document, outlines).Where(flight => flight.Foot is { } foot && reached.Contains(foot) && flight.Head is { } head && reached.Contains(head)).ToList();
        var stacks = flights.Count == 0 ? new List<List<Room>>() : Escape.Stacks(flights);
        var stairs = stacks.SelectMany(stack => stack).ToHashSet();
        return (outlines, reached.Where(room => !stairs.Contains(room)).OrderBy(room => room.Number, StringComparer.Ordinal).ToList(), stacks, "a building with rooms for residential purposes");
    }

    /// <summary>
    /// What the common parts of a block of flats, or of a building with rooms for residential purposes, fall short of in absorption, by Method A
    /// (E3, 7.10, 7.11): an entrance hall, corridor or hallway covered over its floor area with a Class C absorber or better; a stairwell over its
    /// stair treads, landings above the ground and top ceiling - taken as its floor area on each storey but the lowest and its ceiling on the top -
    /// with Class D, or half of it with Class C.
    /// </summary>
    public static IEnumerable<EscapeProblem> Reverberation(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || CommonParts(document) is not (var outlines, var halls, var stacks, var what)) yield break;
        double Area(Room room) => Polygon2D.Area(outlines[room]) / 1e6;
        foreach (var room in halls)
        {
            var area = Area(room);
            var (atC, _) = Absorbing(document, room, outlines[room]);
            if (atC >= area - 0.05) continue;
            var corridor = Corridor(outlines[room]);
            yield return new EscapeProblem(room,
                $"it is {(corridor ? "a common corridor" : "an entrance hall")} of {what}, {area:0.0} m² of floor with {atC:0.0} m² of a Class C absorber or better over it - Approved Document E (E3, {(corridor ? "7.2" : "7.3")}, 7.10) asks an area as large as its floor covered with one, a ceiling of acoustic tiles or the like: draw a ceiling of a type whose lowest layer's material has its class in the Materials window. Method B (7.13 to 7.21), worked by hand, may ask less.");
        }

        foreach (var stack in stacks.Where(stack => stack.Count > 0))
        {
            var ordered = stack.OrderBy(room => document.FindLevel(room.LevelId)?.Elevation ?? 0).ToList();
            var asked = ordered.Skip(1).Sum(Area) + Area(ordered[^1]);
            var (atC, atD) = ordered.Aggregate((C: 0.0, D: 0.0), (sum, room) => Absorbing(document, room, outlines[room]) is var (c, d) ? (sum.C + c, sum.D + d) : sum);
            if (atD >= asked - 0.05 || atC >= asked / 2 - 0.05) continue;
            yield return new EscapeProblem(ordered[^1],
                $"it is the head of a common stair of {what}, its treads, landings above the ground and top ceiling taken as {asked:0.0} m² - its floor on each storey but the lowest and its ceiling at the top - with {atD:0.0} m² of a Class D absorber or better and {atC:0.0} m² of Class C - Approved Document E (E3, 7.11) asks that area covered with Class D, or half of it with Class C or better, shared between its storeys: under its landings and on its top ceiling.");
        }
    }
}
