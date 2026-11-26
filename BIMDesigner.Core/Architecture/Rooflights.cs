using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Plastic rooflights (round 95), as Approved Document B Volume 1 sets them out - 4.7, 4.12 to 4.14, Table 4.2, 12.5 to 12.7, Tables 12.2 and
/// 12.3 and Diagram 12.1, read off the pages. A roof window glazed with a plastic - its glass material a thermoplastic by B13, or a plastic by
/// its name - by its lower surface's class:
///
/// TP(a) rigid: 6 m from the relevant boundary but over a balcony, carport, garage, conservatory or outbuilding of 40 m² at most (Table 12.3).
/// TP(b), and a plastic of class D-s3, d2: 6 m from the boundary - a D-s3, d2 one's by its own external designation, EROOF(t4) or DROOF(t4)
/// 6 m, FROOF(t4) 20 m (Table 12.2) - and over a room or circulation space each 5 m² at most, or a group of them within 3 m of each other 5 m²
/// at most, the roof covering round them class A2-s3, d2 or better (Diagram 12.1), and no more of them over a room than half its floor, over a
/// circulation space 15% (Table 4.2) - no limit on class D-s3, d2 over a small room, 4 m² at most (note 4). Polycarbonate or uPVC of class
/// C-s3, d2 by test is taken as BROOF(t4) (12.7), with no distance from the boundary. TP(a) flexible not as a rooflight (4.14a); a plastic of
/// no class given wanting one (4.7b). None over a protected stairway (4.14); none within 1.5 m of a compartment wall the roof passes over
/// (Diagram 5.2, the tables' notes).
///
/// In a building other than a home (round 120), Volume 2's own: 6.8, 6.15, Table 6.2, Tables 14.2 and 14.3, Diagram 14.1 - the same limits,
/// but class D-s3, d2 unlimited over a small room by Table 6.1, 4 m² in residential accommodation and 30 m² in any other (note 4); over a room
/// rooflights of 1 m² at most each as far apart as the largest one's longest side in plan (Table 6.2, Diagram 6.3); class D-s3, d2 ones over
/// a room in purpose groups 3 to 7, 20% of its floor at most, 1800 apart (note 6) - their being evenly spread not judged; and the covering
/// round them B-s3, d2 (Diagram 14.1 note 2).
/// </summary>
public static class Rooflights
{
    /// <summary>The boundary distances (Tables 12.2, 12.3), the most of a rooflight or group (m²) and the least between them (Diagram 12.1, Table 4.2), mm.</summary>
    public const double Near = 6000, Far = 20000, MostArea = 5, Apart = 3000, FromCompartmentWall = 1500;

    /// <summary>A small room, where class D-s3, d2 has no limit (Table 4.1 for a dwelling, Table 4.2 note 4), m².</summary>
    public const double SmallRoom = 4;

    private enum Lower { TpaRigid, TpaFlexible, Tpb, ClassD, Unclassed }

    private sealed record Light(RoofWindowType Type, RoofWindowFrame Frame, Material Material, Lower Lower, string Class, double Area, Room? Under, double UnderArea, bool Broof);

    /// <summary>What a roof's plastic rooflights fall short of: what, and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Roof roof)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || roof.Boundary.Count < 3) yield break;
        var lights = Plastic(document, roof);
        if (lights.Count == 0) yield break;

        var volume2 = HorizontalEscape.Applies(document);
        var book = volume2 ? "Approved Document B Volume 2" : "Approved Document B";
        string C(string one, string two) => volume2 ? two : one;
        var residential = volume2 && HorizontalEscape.Purpose(document) is PurposeGroup.ResidentialInstitutional or PurposeGroup.ResidentialOther;
        double Small(Room room) => volume2 && Dwellings.Of(room).Length == 0 ? residential ? Combustibility.SmallResidentialRoom : Combustibility.SmallOtherRoom : SmallRoom;

        var lines = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2).ToList();
        var stairs = Doors.StairRooms(document);
        var protectedStairs = stairs.Count > 0 && (FireSeparation.Covers(document) || Doors.HasHighStorey(document));
        string Name(Light light) => $"its rooflight, the {light.Type.Name} glazed with {light.Material.Name} ({light.Class})";
        bool Open(Room room) => Named(room, "balcony", "verandah", "veranda", "carport", "car port", "covered way", "loading bay", "swimming pool")
                                || Named(room, "conservatory", "garage", "outbuilding", "shed", "summerhouse") && Area(document, room) <= 40 + 1e-6;

        foreach (var light in lights)
        {
            var name = Name(light);
            if (light.Under is { } stair && protectedStairs && stairs.Contains(stair))
            {
                yield return ($"{name} is over the {stair.Name}, a protected stairway.", $"{book} ({C("4.14, Tables 12.2 and 12.3", "6.15, Tables 14.2 and 14.3")}) allows no plastic rooflight over a protected stairway.");
                continue;
            }

            switch (light.Lower)
            {
                case Lower.TpaFlexible:
                    yield return ($"{name} has a TP(a) flexible lower surface.", $"{book} ({C("4.14a", "6.15a")}) asks a thermoplastic rooflight's lower surface to be TP(a) rigid or TP(b); TP(a) flexible goes only in ceiling panels of 5 m² at most ({C("4.17", "6.18")}).");
                    continue;
                case Lower.Unclassed:
                    yield return ($"{name} - a plastic of no class given.", $"{book} ({C("4.7b, 4.14a", "6.8b, 6.15a")}) asks a plastic rooflight to be TP(a) rigid or TP(b), or class D-s3, d2 at least within {C("Tables 4.2 and 12.2", "Tables 6.2 and 14.2")} - else {C("Table 4.1", "Table 6.1")}'s class: give its Thermoplastic class or its Reaction to Fire in the Materials window.");
                    continue;
            }

            // How near the boundary (Tables 12.2, 12.3): polycarbonate or uPVC of class C-s3, d2 taken as BROOF(t4) (12.7) is not limited.
            var open = light.Under is { } over && Open(over);
            if (lines.Count > 0 && !light.Broof)
            {
                var distance = lines.Min(line => RoofCoverings.Distance(light.Frame.Corners, line.Boundary));
                var at = Units.Metres(distance, "0.0");
                switch (light.Lower)
                {
                    case Lower.TpaRigid when !open && distance < Near - 0.5:
                        yield return ($"{name} is {at} from the boundary.", $"{book} ({C("Table 12.3", "Table 14.3")}) keeps a TP(a) rigid rooflight 6 m from the relevant boundary, but over a balcony, carport, garage, conservatory or outbuilding of 40 m² at most.");
                        break;
                    case Lower.Tpb when distance < Near - 0.5:
                        yield return ($"{name} is {at} from the boundary.", $"{book} ({C("Table 12.3", "Table 14.3")}) keeps a TP(b) rooflight 6 m from the relevant boundary.");
                        break;
                    case Lower.ClassD:
                        var designation = RoofDesignation.Of(light.Material, 10)?.Designation;
                        if (designation is { Rank: <= 1 }) break;
                        var far = designation is null || designation.Value.Rank >= 4;
                        if (distance < (far ? Far : Near) - 0.5)
                            yield return ($"{name} is {at} from the boundary{(designation is null ? ", no external designation given - taken as FROOF(t4)" : $", {designation}")}.",
                                $"{book} ({C("Table 12.2", "Table 14.2")}) keeps a class D-s3, d2 plastic rooflight 6 m from the relevant boundary as DROOF(t4) or EROOF(t4), 20 m as FROOF(t4).");
                        break;
                }
            }
        }

        // Over rooms and circulation spaces, TP(b) and class D-s3, d2 rooflights: each, and each group within 3 m of one another, 5 m² at most
        // (Diagram 12.1, Diagram 4.2); no more over a space than Table 4.2's share of its floor.
        var limited = lights.Where(light => light.Lower is Lower.Tpb or Lower.ClassD && !(light.Under is { } room && (Open(room) || light.Lower == Lower.ClassD && light.UnderArea <= Small(room) + 1e-6))).ToList();

        // Volume 2's other ways over a room: rooflights of 1 m² at most as far apart as the largest one's longest side (Table 6.2, Diagram 6.3);
        // class D-s3, d2 ones in purpose groups 3 to 7, 20% of the floor at most, 1800 apart (note 6).
        var grouped = limited;
        if (volume2)
        {
            var otherwise = new HashSet<Light>();
            foreach (var over in limited.Where(light => light.Under is { } room && !Circulation(room)).GroupBy(light => light.Under!))
            {
                var each = over.ToList();
                var longest = each.Max(light => Longest(light.Frame.Corners));
                if (each.All(light => light.Area <= 1 + 1e-6)
                    && each.SelectMany((light, i) => each.Skip(i + 1).Select(other => Apartness(light.Frame.Corners, other.Frame.Corners))).All(gap => gap >= longest - 0.5))
                {
                    otherwise.UnionWith(each);
                    continue;
                }

                if (residential || each.Any(light => light.Lower != Lower.ClassD) || each.Sum(light => light.Area) > 0.2 * each[0].UnderArea + 1e-6) continue;
                otherwise.UnionWith(each);
                foreach (var group in Groups(each, NoteSix))
                {
                    var total = group.Sum(light => light.Area);
                    if (total <= MostArea + 1e-6) continue;
                    yield return (group.Count == 1
                            ? $"{Name(group[0])} is {Units.OfSquareMetres(group[0].Area, "0.0")}."
                            : $"its rooflights {string.Join(", ", group.Select(light => light.Type.Name).Distinct())} over the {over.Key.Name} stand within 1.8 m of each other, {Units.OfSquareMetres(total, "0.0")} together.",
                        $"{book} (Table 6.2 note 6, Diagram 14.1) lets class D-s3, d2 rooflights over a room of a building in purpose groups 3 to 7, 20% of its floor at most, stand 1800 apart, evenly spread - each, or each group closer than that, 5 m² at most.");
                }
            }

            grouped = limited.Where(light => !otherwise.Contains(light)).ToList();
        }

        foreach (var group in Groups(grouped))
        {
            var total = group.Sum(light => light.Area);
            if (total <= MostArea + 1e-6) continue;
            yield return (group.Count == 1
                    ? $"{Name(group[0])} is {Units.OfSquareMetres(group[0].Area, "0.0")}."
                    : $"its rooflights {string.Join(", ", group.Select(light => light.Type.Name).Distinct())} stand within 3 m of each other, {Units.OfSquareMetres(total, "0.0")} together.",
                volume2
                    ? $"{book} (Diagram 14.1, Table 6.2) keeps a TP(b) or class D-s3, d2 plastic rooflight over a room or circulation space to 5 m², or a group of them within 3 m of each other to 5 m² together - each group 3 m from the next; over a room, ones of 1 m² at most may stand as far apart as the largest one's longest side (Diagram 6.3)."
                    : "Approved Document B (Diagram 12.1, Table 4.2) keeps a TP(b) or class D-s3, d2 plastic rooflight over a room or circulation space to 5 m², or a group of them within 3 m of each other to 5 m² together - each group 3 m from the next.");
        }

        foreach (var under in limited.Where(light => light.Under is not null).GroupBy(light => light.Under!))
        {
            var share = Circulation(under.Key) ? 0.15 : 0.5;
            var floor = under.First().UnderArea;
            var total = under.Sum(light => light.Area);
            if (floor > 0 && total > share * floor + 1e-6)
                yield return ($"its TP(b) or class D-s3, d2 rooflights over the {under.Key.Name} are {Units.OfSquareMetres(total, "0.0")}, {100 * total / floor:0}% of its {Units.OfSquareMetres(floor, "0.0")} floor.",
                    $"{book} ({C("Table 4.2", "Table 6.2")}) allows them over {(share < 0.5 ? "a circulation space 15%" : "a room 50%")} of its floor at most.");
        }

        if (limited.Count > 0 && document.FindType<SlabType>(roof.TypeId) is { } type
            && type.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0).Select(layer => document.FindMaterial(layer.MaterialId)).FirstOrDefault(material => material is not null) is { } covering
            && !(Euroclass.Of(covering) is { } fire && fire.Class.AtLeast(volume2 ? Euroclass.Bs3d2 : Euroclass.A2s3d2)))
            yield return ($"its covering round its plastic rooflights, the {covering.Name}, is {(Euroclass.Of(covering) is { } known ? $"class {known.Class}" : "of no class given")}.",
                volume2
                    ? $"{book} (Diagram 14.1, note 2) asks the roof covering for 3 m round TP(b) and class D-s3, d2 plastic rooflights to be class B-s3, d2 or better."
                    : "Approved Document B (Diagram 12.1, note 2) asks the roof covering for 3 m round TP(b) and class D-s3, d2 plastic rooflights to be class A2-s3, d2 or better.");

        // Within 1.5 m of a compartment wall the roof passes over (Diagram 5.2, the notes to Tables 12.2 and 12.3; 5.12).
        var walls = document.Walls.Where(wall => roof.Contains(wall.Start.MidpointTo(wall.End)) && CompartmentRoofs.Why(document, wall) is not null).ToList();
        foreach (var light in lights)
        foreach (var wall in walls)
        {
            var half = (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2;
            var gap = light.Frame.Corners.Min(corner => Line2D.DistanceFromSegment(corner, wall.Start, wall.End)) - half;
            if (Polygon2D.Contains(light.Frame.Corners, wall.Start.MidpointTo(wall.End))) gap = 0;
            if (gap >= FromCompartmentWall - 0.5) continue;
            yield return ($"{Name(light)} is {Math.Max(0, gap):0} mm from a compartment wall under the roof.",
                $"{book} ({C("Diagram 5.2, the notes to Tables 12.2 and 12.3, 5.12", "Diagram 8.2, the notes to Tables 14.2 and 14.3")}) keeps a plastic rooflight 1.5 m from a compartment wall the roof passes over.");
            break;
        }
    }

    /// <summary>A roof's rooflights glazed with a plastic: each with its class, its area, and the room under it.</summary>
    private static List<Light> Plastic(BimDocument document, Roof roof)
    {
        var lights = new List<Light>();
        List<(Room Room, IReadOnlyList<Point2D> Outline, double Floor)>? rooms = null;
        foreach (var window in RoofWindows.On(document, roof))
        {
            if (document.FindType<RoofWindowType>(window.TypeId) is not { } type || RoofWindows.Frame(document, window) is not { } frame) continue;
            var built = window.Built(type);
            if (document.FindMaterial(built.GlassMaterialId) is not { } material) continue;
            var thermoplastic = Thermoplastic.Of(material);
            if (thermoplastic is null && !Thermoplastic.Plastic(material.Name)) continue;
            var fire = Euroclass.Of(material);
            var (lower, said) = thermoplastic switch
            {
                { Class: ThermoplasticClass.TpaRigid } => (Lower.TpaRigid, "TP(a) rigid"),
                { Class: ThermoplasticClass.TpaFlexible } => (Lower.TpaFlexible, "TP(a) flexible"),
                { Class: ThermoplasticClass.Tpb } => (Lower.Tpb, "TP(b)"),
                _ when fire is { } known && known.Class.AtLeast(Euroclass.Ds3d2) => (Lower.ClassD, $"class {known.Class}"),
                _ => (Lower.Unclassed, "no class given")
            };
            var broof = fire is { } tested && tested.Class.AtLeast(Euroclass.Cs3d2)
                        && new[] { "polycarbonate", "pvc" }.Any(word => material.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

            rooms ??= document.All<Room>().Select(room => (Room: room, Boundary: room.GetBoundary(document))).Where(entry => entry.Boundary.IsEnclosed && entry.Boundary.Polygon.Count >= 3)
                .Select(entry => (entry.Room, (IReadOnlyList<Point2D>)entry.Boundary.Polygon, document.FindLevel(entry.Room.LevelId)?.Elevation ?? 0)).ToList();
            var low = frame.Corners.Min(frame.Top);
            var under = rooms.Where(entry => entry.Floor < low - 1 && Polygon2D.Contains(entry.Outline, frame.Centre)).OrderByDescending(entry => entry.Floor).Select(entry => ((Room Room, IReadOnlyList<Point2D> Outline, double Floor)?)entry).FirstOrDefault();
            lights.Add(new Light(type, frame, material, lower, said, built.Width * built.Height / 1e6, under?.Room, under is { } found ? Math.Abs(Polygon2D.SignedArea(found.Outline)) / 1e6 : 0, broof));
        }

        return lights;
    }

    /// <summary>Table 6.2 note 6's spacing of class D-s3, d2 rooflights over a room in purpose groups 3 to 7, mm.</summary>
    public const double NoteSix = 1800;

    /// <summary>An outline's longest side in plan, mm: a rooflight's largest plan dimension (Table 6.2, Diagram 6.3).</summary>
    private static double Longest(IReadOnlyList<Point2D> corners) =>
        corners.Select((corner, i) => corner.DistanceTo(corners[(i + 1) % corners.Count])).DefaultIfEmpty(0).Max();

    /// <summary>Rooflights grouped where they stand within so far of one another - 3 m unless said - edge to edge in plan.</summary>
    private static List<List<Light>> Groups(List<Light> lights, double apart = Apart)
    {
        var groups = new List<List<Light>>();
        var left = new List<Light>(lights);
        while (left.Count > 0)
        {
            var group = new List<Light> { left[0] };
            left.RemoveAt(0);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var light in left.ToList().Where(light => group.Any(member => Apartness(member.Frame.Corners, light.Frame.Corners) < apart - 0.5)))
                {
                    group.Add(light);
                    left.Remove(light);
                    grew = true;
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    /// <summary>The least distance in plan between two outlines, mm: none where one's corner is in the other.</summary>
    private static double Apartness(IReadOnlyList<Point2D> a, IReadOnlyList<Point2D> b)
    {
        if (a.Any(corner => Polygon2D.Contains(b, corner)) || b.Any(corner => Polygon2D.Contains(a, corner))) return 0;
        double Edges(IReadOnlyList<Point2D> from, IReadOnlyList<Point2D> to) =>
            from.Min(corner => Enumerable.Range(0, to.Count).Min(i => Line2D.DistanceFromSegment(corner, to[i], to[(i + 1) % to.Count])));
        return Math.Min(Edges(a, b), Edges(b, a));
    }

    private static bool Named(Room room, params string[] words) => words.Any(word => room.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static bool Circulation(Room room) => Named(room, "hall", "landing", "corridor", "lobby", "stair", "entrance", "porch") && !Mep.Ventilation.WetByName(room.Name);

    private static double Area(BimDocument document, Room room) => room.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Area / 1e6 : 0;
}
