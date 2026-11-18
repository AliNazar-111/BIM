using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How readily a home's linings and outside walls burn, as Approved Document B Volume 1 has it, by each material's reaction to fire class
/// (BS EN 13501-1) - its maker's, or the one its name has it by where it is classed without testing (<see cref="Euroclass.Typical"/>):
///
/// Linings (Section 4, Table 4.1): the face of each wall round a room, and the ceiling over it, class C-s3, d2 or better - D-s3, d2 in a
/// room of 4 m² or less or an integral garage of 40 m² or less, B-s3, d2 in a circulation space of the common parts of a block of flats;
/// parts of the walls may be lower, but no worse than D-s3, d2 and less than half the floor's area, 20 m² at most, all told (4.4).
///
/// Outside walls (Section 10): the outside surface by Table 10.1 - a home's building more than 11 m high A2-s1, d0, 11 m or less B-s3, d2
/// within 1 m of the boundary; another building's B-s3, d2 within 1 m, C-s3, d2 further off where it is more than 18 m high, B-s3, d2 above
/// 18 m - its notes' coated steel sheet and timber cladding accepted; insulation and filling A2-s3, d2 with a storey 18 m up (10.6), A2-s1, d0
/// in a home's building with a storey 11 m up (10.7), unless the wall is masonry cavity wall (Diagram 8.2); and in a relevant building - a
/// storey 18 m or more up, homes in it - every material A2-s1, d0 or A1, membranes excepted (Regulation 7(2), 10.14). Not under the US's codes.
/// </summary>
public static class Combustibility
{
    /// <summary>Table 4.1's small room, and its integral garage, m².</summary>
    public const double SmallRoom = 4, SmallGarage = 40;

    /// <summary>4.4: the most of a room's walls below Table 4.1's class, m², besides half its floor.</summary>
    public const double LowerArea = 20;

    /// <summary>Table 10.1: within this of the relevant boundary, mm; a residential building's height, and a relevant building's storey, mm.</summary>
    public const double NearBoundary = 1000, Residential = 11000, Relevant = 18000;

    // --- Linings -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A room's linings as its Properties say them: "Linings: C-s3, d2 asked of a room, met - walls Cement Plaster A1, ceiling Gypsum Plasterboard
    /// A2-s1, d0", or what is wanting, clause by clause. Null where they are not asked - not a home, or under the US's codes.
    /// </summary>
    public static string? LiningVerdict(BimDocument document, Room room)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;
        var named = document.All<Room>().Any(each => Dwellings.Of(each).Length > 0);
        if (!named && FireDesign.UseOf(document) is not (BuildingUse.House or BuildingUse.Flats)) return null;
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;

        var area = boundary.Area / 1e6;
        var circulation = Lighting.KindOf(document, room, boundary.Polygon) is LitRoom.Hall or LitRoom.Stairs;
        var common = named && Dwellings.Of(room).Length == 0;
        var (asked, what) = common && circulation ? (Euroclass.Bs3d2, "a circulation space of the common parts")
            : area <= SmallRoom + 1e-6 ? (Euroclass.Ds3d2, "a room of 4 m² or less")
            : Doors.Garage(document, room) && area <= SmallGarage + 1e-6 ? (Euroclass.Ds3d2, "a garage of 40 m² or less")
            : (Euroclass.Cs3d2, circulation ? "a circulation space in a home" : "a room");

        // Its walls' faces toward it, by material, and how much of each.
        var faces = new Dictionary<Material, double>();
        var polygon = boundary.Polygon;
        for (var i = 0; i < boundary.Sides.Count && i < polygon.Count; i++)
        {
            if (boundary.Sides[i] is not { } wall || document.IsCurtainWall(wall) || document.GetWallType(wall) is not { } type) continue;
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var middle = new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var outside = (middle - Line2D.ClosestPointOnSegment(middle, wall.Start, wall.End)).Dot(wall.ExteriorNormal) > 0;
            if (Face(document, type.Structure, outside) is { } material)
                faces[material] = faces.GetValueOrDefault(material) + a.DistanceTo(b) * room.Height / 1e6;
        }

        // The ceiling: the underside of the lowest ceiling, floor or roof over it.
        var floor = (document.FindLevel(room.LevelId)?.Elevation ?? 0) + room.BaseOffset;
        var over = document.All<Slab>().Where(slab => slab.Boundary.Count >= 3 && slab is not Roof { IsExtrusion: true } && slab.Contains(room.Location))
            .Select(slab => (Slab: slab, Under: slab is Roof roof ? roof.UndersideAt(document, room.Location) : slab.GetBottomElevation(document)))
            .Where(entry => entry.Under > floor + 500).OrderBy(entry => entry.Under).Select(entry => entry.Slab).FirstOrDefault();
        var ceiling = over is not null && document.FindType<SlabType>(over.TypeId) is { } slabType ? Face(document, slabType.Structure, false) : null;
        if (faces.Count == 0 && ceiling is null) return null;

        var wanting = new List<string>();
        var said = new List<string>();
        var (lower, lowerNames) = (0.0, new List<string>());
        string? ceilingSaid = null;
        foreach (var (material, faceArea) in faces.OrderByDescending(entry => entry.Value))
        {
            if (Euroclass.Of(material) is not { } fire)
            {
                wanting.Add($"the {material.Name} on its walls has no reaction to fire class given - Approved Document B (Table 4.1) asks {asked} of {what}'s linings; give it the class its maker declares");
                continue;
            }

            said.Add($"{material.Name} {fire.Class}");
            if (fire.Class.AtLeast(asked)) continue;
            if (!fire.Class.AtLeast(Euroclass.Ds3d2))
            {
                wanting.Add($"the {material.Name} on its walls is {fire.Class} - Approved Document B (Table 4.1, 4.4) asks {asked} of {what}'s linings, and no part of them worse than D-s3, d2");
                continue;
            }

            lower += faceArea;
            lowerNames.Add(material.Name);
        }

        var allowed = Math.Min(area / 2, LowerArea);
        if (lowerNames.Count > 0 && lower >= allowed - 1e-6)
            wanting.Add($"{Units.OfSquareMetres(lower, "0.0")} of its walls - the {string.Join(" and ", lowerNames)} - is below {asked}, Approved Document B (4.4) asks less than {Units.OfSquareMetres(allowed, "0.0")} of a room's walls below Table 4.1's class - half its floor, 20 m² at most");

        if (ceiling is not null)
        {
            if (Euroclass.Of(ceiling) is not { } fire)
                wanting.Add($"the {ceiling.Name} of its ceiling has no reaction to fire class given - Approved Document B (Table 4.1) asks {asked} of {what}'s linings; give it the class its maker declares");
            else
            {
                ceilingSaid = $"ceiling of {ceiling.Name} {fire.Class}";
                if (!fire.Class.AtLeast(asked))
                    wanting.Add($"its ceiling's {ceiling.Name} is {fire.Class} - Approved Document B (Table 4.1) asks {asked} of {what}'s linings, its ceiling as much as its walls");
            }
        }

        var met = new[] { said.Count > 0 ? $"walls of {string.Join(" and ", said)}" : null, ceilingSaid }.OfType<string>();
        return wanting.Count > 0 ? $"Linings: {string.Join("; ", wanting)}" : $"Linings: {asked} asked of {what}, met - {string.Join(", ", met)}";
    }

    /// <summary>The material of a build-up's face, outside or in: its first layer that is not a membrane, nor air.</summary>
    private static Material? Face(BimDocument document, CompoundStructure structure, bool outside)
    {
        foreach (var layer in outside ? structure.Layers : structure.Layers.Reverse())
            if (layer.Function != LayerFunction.Membrane && layer.Thickness > 0 && document.FindMaterial(layer.MaterialId) is { } material && !Air(material))
                return material;
        return null;
    }

    private static bool Air(Material material) => material.Density < 2 || material.Name.Contains("air cavity", StringComparison.OrdinalIgnoreCase);

    // --- Outside walls -------------------------------------------------------------------------------------------------------------------

    /// <summary>A building's height (Diagram D4: to its roofs' mean level, or its outside walls' tops, whichever is higher) and its top storey's (Diagram D6: the floor of the highest with rooms), above the ground, mm.</summary>
    public static (double Building, double TopStorey) Heights(BimDocument document)
    {
        var ground = Escape.Ground(document);
        var roofs = document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3).Select(roof => (roof.BaseElevation(document) + roof.RidgeElevation(document)) / 2);
        var walls = document.Walls.Where(wall => document.GetWallType(wall)?.Function == WallFunction.Exterior).Select(wall => wall.GetTopElevation(document));
        var building = roofs.Concat(walls).DefaultIfEmpty(ground).Max() - ground;
        var storey = document.All<Room>().Select(room => document.FindLevel(room.LevelId)?.Elevation ?? ground).DefaultIfEmpty(ground).Max() - ground;
        return (Math.Max(0, building), Math.Max(0, storey));
    }

    /// <summary>What an outside wall's materials fall short of by Section 10 and Regulation 7(2): what, and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (standard.IsUs() || document.IsCurtainWall(wall) || document.GetWallType(wall) is not { Function: WallFunction.Exterior } type) yield break;
        if (!BoundsARoom(document, wall)) yield break;
        var layers = type.Structure.Layers
            .Select(layer => (Layer: layer, Material: layer.Function == LayerFunction.Membrane || layer.Thickness <= 0 ? null : document.FindMaterial(layer.MaterialId)))
            .Where(entry => entry.Material is not null && !Air(entry.Material)).Select(entry => (entry.Layer, Material: entry.Material!)).ToList();
        if (layers.Count == 0) yield break;

        var (building, storey) = Heights(document);
        var residential = document.All<Room>().Any(room => Dwellings.Of(room).Length > 0) || FireDesign.UseOf(document) is BuildingUse.House or BuildingUse.Flats;
        static string Said(Material material) => Euroclass.Of(material) is { } fire ? $"{material.Name}, {fire.Class}" : $"{material.Name}, of no class given";

        // A relevant building: every material A2-s1, d0 or A1 (Regulation 7(2)), which prevails over the rest.
        if (residential && storey >= Relevant - 0.5)
        {
            var short_ = layers.Select(entry => entry.Material).Distinct().Where(material => Euroclass.Of(material) is not { } fire || !fire.Class.AtLeast(Euroclass.A2s1d0)).ToList();
            if (short_.Count > 0)
                yield return ($"its {string.Join("; ", short_.Select(Said))}.",
                    "Regulation 7(2) asks every material of an outside wall of a building holding homes with a storey 18 m or more above the ground - a relevant building (Approved Document B 10.14) - to be class A2-s1, d0 or A1, membranes excepted (regulation 7(3)).");
            yield break;
        }

        // Its outside surface (Table 10.1).
        var (face, faceMaterial) = layers[0];
        var distance = SpaceSeparation.DistanceToBoundary(document, wall);
        var near = distance is < NearBoundary - 0.5;
        var top = wall.GetTopElevation(document) - Escape.Ground(document);
        (Euroclass Class, string Of, bool Steel, bool Timber)? asked = residential
            ? building > Residential + 0.5 ? (Euroclass.A2s1d0, "a home's building more than 11 m high", true, false)
              : near ? (Euroclass.Bs3d2, "a home's building 11 m high or less, within 1 m of the boundary", true, false) : null
            : near ? (Euroclass.Bs3d2, "a building within 1 m of the boundary", true, false)
            : building > Relevant + 0.5 ? top > Relevant + 0.5 ? (Euroclass.Bs3d2, "a building more than 18 m high, above 18 m", true, false) : (Euroclass.Cs3d2, "a building more than 18 m high, up to 18 m", false, true)
            : null;
        if (asked is { } want)
        {
            var name = faceMaterial.Name;
            var accepted = Euroclass.Of(faceMaterial) is { } fire && fire.Class.AtLeast(want.Class)
                           || want.Steel && name.Contains("steel", StringComparison.OrdinalIgnoreCase) && face.Thickness >= 0.5 - 1e-6
                           || want.Timber && (name.Contains("timber", StringComparison.OrdinalIgnoreCase) || name.Contains("wood", StringComparison.OrdinalIgnoreCase)
                                              || name.Contains("cedar", StringComparison.OrdinalIgnoreCase) || name.Contains("larch", StringComparison.OrdinalIgnoreCase)) && face.Thickness >= 9 - 1e-6;
            if (!accepted)
                yield return ($"its outside face is {Said(faceMaterial)}{(distance is { } d && near ? $", {Units.Metres(d, "0.0")} from the boundary" : string.Empty)}.",
                    $"Approved Document B (10.5, Table 10.1) asks the outside surface of {want.Of} to be class {want.Class} or better{(want.Steel ? " - or steel sheet 0.5 mm thick with an organic coating 0.2 mm at most" : want.Timber ? " - or timber cladding 9 mm thick" : string.Empty)}.");
        }

        // Its insulation and filling (10.6, 10.7) - not in a masonry cavity wall (Diagram 8.2).
        var (insulationClass, clause, height) = residential && storey >= Residential - 0.5 ? (Euroclass.A2s1d0, "10.7", "a home's building with a storey 11 m")
            : storey >= Relevant - 0.5 ? (Euroclass.A2s3d2, "10.6", "a building with a storey 18 m") : (default(Euroclass), string.Empty, string.Empty);
        if (clause.Length == 0 || MasonryCavity(document, type.Structure)) yield break;
        var filling = layers.Where(entry => entry.Layer.Function == LayerFunction.ThermalAir).Select(entry => entry.Material).Distinct()
            .Where(material => Euroclass.Of(material) is not { } fire || !fire.Class.AtLeast(insulationClass)).ToList();
        if (filling.Count > 0)
            yield return ($"its insulation is {string.Join("; ", filling.Select(Said))}.",
                $"Approved Document B ({clause}) asks the insulation and filling of an outside wall of {height} or more above the ground to be class {insulationClass} or better - unless it is a masonry cavity wall, two leaves of brick or concrete 75 mm thick at least (Diagram 8.2).");
    }

    /// <summary>Whether a wall is the building's - some room's outline runs along it - not a wall round the garden.</summary>
    public static bool BoundsARoom(BimDocument document, Wall wall) => document.All<Room>().Any(room => room.GetBoundary(document).BoundingWalls.Contains(wall));

    /// <summary>Whether a build-up is a masonry cavity wall as Diagram 8.2 draws it: a leaf of brick or concrete 75 mm thick at least either side of its insulation and cavity.</summary>
    public static bool MasonryCavity(BimDocument document, CompoundStructure structure)
    {
        bool Leaf(MaterialLayer layer) => layer.Thickness >= 75 - 0.5 && document.FindMaterial(layer.MaterialId)?.Name is { } name
                                          && new[] { "brick", "block", "concrete", "aircrete", "stone" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        var layers = structure.Layers.ToList();
        var first = layers.FindIndex(layer => layer.Function == LayerFunction.ThermalAir);
        var last = layers.FindLastIndex(layer => layer.Function == LayerFunction.ThermalAir);
        return first >= 0 && layers.Take(first).Any(Leaf) && layers.Skip(last + 1).Any(Leaf);
    }
}
