using System.Globalization;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// A light's photometric data, as its maker measures it: how much light it gives in each direction,
/// candelas, on C-planes round it (0° along its length, turning to 360°) at angles γ from straight
/// down - read from an IES LM-63 or a EULUMDAT file, spread to every plane by its symmetry - with the
/// light it gives all told and the watts it draws.
/// </summary>
public sealed class Photometry
{
    public string Source { get; init; } = string.Empty;

    public double Lumens { get; init; }

    public double Watts { get; init; }

    /// <summary>The C-planes, ascending from 0 to 360.</summary>
    public IReadOnlyList<double> Planes { get; init; } = Array.Empty<double>();

    /// <summary>The angles from straight down, ascending.</summary>
    public IReadOnlyList<double> Angles { get; init; } = Array.Empty<double>();

    /// <summary>Candelas on each plane at each angle.</summary>
    public IReadOnlyList<IReadOnlyList<double>> Candela { get; init; } = Array.Empty<IReadOnlyList<double>>();

    public string Describe() => $"{Source}: {Lumens:#,0} lm{(Watts > 0 ? $", {Watts:0.#} W, {Lumens / Watts:0} lm/W" : string.Empty)}, {Candela.SelectMany(plane => plane).DefaultIfEmpty(0).Max():#,0} cd at most";

    /// <summary>Its intensity on a plane at an angle from straight down, cd.</summary>
    public double Intensity(double plane, double angle)
    {
        if (Planes.Count == 0 || Angles.Count == 0) return 0;
        if (angle < Angles[0] - 1e-9 || angle > Angles[^1] + 1e-9) return 0;
        plane = ((plane % 360) + 360) % 360;
        var (c0, c1, ct) = Bracket(Planes, plane);
        var (g0, g1, gt) = Bracket(Angles, angle);
        double At(int c) => Candela[c][g0] + (Candela[c][g1] - Candela[c][g0]) * gt;
        return At(c0) + (At(c1) - At(c0)) * ct;
    }

    private static (int Low, int High, double T) Bracket(IReadOnlyList<double> values, double x)
    {
        if (values.Count == 1 || x <= values[0]) return (0, 0, 0);
        for (var i = 1; i < values.Count; i++)
            if (x <= values[i] + 1e-9)
                return (i - 1, i, values[i] - values[i - 1] < 1e-9 ? 0 : (x - values[i - 1]) / (values[i] - values[i - 1]));
        return (values.Count - 1, values.Count - 1, 0);
    }

    /// <summary>Reads an IES LM-63 file or a EULUMDAT file - which one, from what it holds.</summary>
    public static Photometry Read(string text, string source) =>
        text.TrimStart().StartsWith("IES", StringComparison.OrdinalIgnoreCase) || text.Contains("TILT=", StringComparison.OrdinalIgnoreCase)
            ? ReadIes(text, source)
            : ReadEulumdat(text, source);

    private static double Number(string token) => double.Parse(token.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// An IES LM-63 file: keyword lines, TILT=, then its numbers - lamps, lumens a lamp, the candela
    /// multiplier, how many vertical and horizontal angles, the photometric type (C, 1, only), units and
    /// size, the ballast factor, the input watts - its angles and its candelas plane by plane.
    /// </summary>
    public static Photometry ReadIes(string text, string source)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n');
        var tilt = Array.FindIndex(lines, line => line.TrimStart().StartsWith("TILT=", StringComparison.OrdinalIgnoreCase));
        if (tilt < 0) throw new FormatException("no TILT= line - not an IES file");
        var numbers = new Queue<double>(lines.Skip(tilt + 1).SelectMany(line => line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)).Select(Number));
        if (lines[tilt].Contains("INCLUDE", StringComparison.OrdinalIgnoreCase))
        {
            numbers.Dequeue();
            var pairs = (int)numbers.Dequeue();
            for (var i = 0; i < pairs * 2; i++) numbers.Dequeue();
        }

        var lamps = numbers.Dequeue();
        var perLamp = numbers.Dequeue();
        var multiplier = numbers.Dequeue();
        var vertical = (int)numbers.Dequeue();
        var horizontal = (int)numbers.Dequeue();
        var type = (int)numbers.Dequeue();
        if (type != 1) throw new FormatException("only type C photometry is read - its photometric type is " + (type == 2 ? "B" : "A"));
        for (var i = 0; i < 4; i++) numbers.Dequeue();
        var ballast = numbers.Dequeue();
        numbers.Dequeue();
        var watts = numbers.Dequeue();
        var angles = Enumerable.Range(0, vertical).Select(_ => numbers.Dequeue()).ToList();
        var planes = Enumerable.Range(0, horizontal).Select(_ => numbers.Dequeue()).ToList();
        var candela = planes.Select(_ => (IReadOnlyList<double>)Enumerable.Range(0, vertical).Select(_ => numbers.Dequeue() * multiplier * (ballast > 0 ? ballast : 1)).ToList()).ToList();
        _ = lamps;
        _ = perLamp;
        // LM-63's horizontal angles: 0 alone round, 0 to 90 a quadrant, 0 to 180 halves either side of the 0-180 plane, 90 to 270 either side of the 90-270 plane.
        return Build(source, watts, planes, angles, candela, (planes[0], planes[^1]) switch
        {
            (_, 0) => Symmetry.Round,
            (0, 90) => Symmetry.Quadrant,
            (0, 180) => Symmetry.Halves,
            (90, 270) => Symmetry.HalvesAcross,
            _ => Symmetry.None
        });
    }

    /// <summary>
    /// A EULUMDAT file, line by line: its maker and type, its symmetry, how many C-planes and angles,
    /// its names and sizes, its light output ratio and the lamps in it - their light and watts - its
    /// direct ratios, its angles and its intensities, cd for each 1000 lm of its lamps.
    /// </summary>
    public static Photometry ReadEulumdat(string text, string source)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(line => line.Trim()).ToList();
        if (lines.Count < 30) throw new FormatException("too short to be a EULUMDAT file");
        var symmetry = (int)Number(lines[2]);
        var planeCount = (int)Number(lines[3]);
        var angleCount = (int)Number(lines[5]);
        var outputRatio = Number(lines[22]) / 100;
        var sets = (int)Number(lines[25]);
        var at = 26;
        double flux = 0, watts = 0;
        for (var i = 0; i < sets; i++)
        {
            var count = Number(lines[at + i]);
            flux += Number(lines[at + 2 * sets + i]) * (count < 0 ? 1 : 1);
            watts += Number(lines[at + 5 * sets + i]);
        }

        at += 6 * sets + 10;
        var allPlanes = Enumerable.Range(0, planeCount).Select(i => Number(lines[at + i])).ToList();
        at += planeCount;
        var angles = Enumerable.Range(0, angleCount).Select(i => Number(lines[at + i])).ToList();
        at += angleCount;
        // The planes given: one; C0 to C180; C270 round through C0 to C90 for symmetry about the C90-C270 plane (Mc1 = 3Mc/4 + 1); C0 to C90; all.
        var (first, used, kind) = symmetry switch
        {
            1 => (0, 1, Symmetry.Round),
            2 => (0, planeCount / 2 + 1, Symmetry.Halves),
            3 => (3 * planeCount / 4, planeCount / 2 + 1, Symmetry.HalvesAcross),
            4 => (0, planeCount / 4 + 1, Symmetry.Quadrant),
            _ => (0, planeCount, Symmetry.None)
        };
        var planes = Enumerable.Range(0, used).Select(i => allPlanes[(first + i) % planeCount]).ToList();
        var candela = new List<IReadOnlyList<double>>();
        for (var p = 0; p < used; p++)
        {
            candela.Add(Enumerable.Range(0, angleCount).Select(g => Number(lines[at + p * angleCount + g]) * flux / 1000).ToList());
        }

        var built = Build(source, watts, planes, angles, candela, kind);
        return new Photometry
        {
            Source = built.Source, Watts = built.Watts, Planes = built.Planes, Angles = built.Angles, Candela = built.Candela,
            Lumens = flux * outputRatio > 0 ? flux * outputRatio : built.Lumens
        };
    }

    private enum Symmetry
    {
        None,
        Round,
        Halves,
        HalvesAcross,
        Quadrant
    }

    /// <summary>The data spread to every plane from 0 to 360 by its symmetry, and the light it gives worked out from it.</summary>
    private static Photometry Build(string source, double watts, IReadOnlyList<double> planes, IReadOnlyList<double> angles, IReadOnlyList<IReadOnlyList<double>> candela, Symmetry symmetry)
    {
        var map = new SortedDictionary<double, IReadOnlyList<double>>();
        void Put(double plane, IReadOnlyList<double> values)
        {
            plane = Math.Round(((plane % 360) + 360) % 360, 6);
            map.TryAdd(plane, values);
        }

        for (var i = 0; i < planes.Count; i++)
        {
            var (c, values) = (planes[i], candela[i]);
            Put(c, values);
            switch (symmetry)
            {
                case Symmetry.Halves:
                    Put(360 - c, values);
                    break;
                case Symmetry.HalvesAcross:
                    Put(180 - c, values);
                    break;
                case Symmetry.Quadrant:
                    Put(180 - c, values);
                    Put(180 + c, values);
                    Put(360 - c, values);
                    break;
            }
        }

        if (symmetry == Symmetry.Round) map[0] = candela[0];
        var full = map.Select(entry => (Plane: entry.Key, Values: entry.Value)).ToList();
        if (full.Count == 0) throw new FormatException("no intensities in it");
        full.Add((360, full[0].Values));
        if (symmetry == Symmetry.Round) full = new List<(double, IReadOnlyList<double>)> { (0, candela[0]), (360, candela[0]) };

        var built = new Photometry
        {
            Source = source, Watts = watts, Planes = full.Select(entry => entry.Plane).ToList(), Angles = angles.ToList(), Candela = full.Select(entry => entry.Values).ToList()
        };

        // Its light all told: its intensity over the whole sphere round it.
        double lumens = 0;
        const int steps = 72;
        for (var g = 0; g < 180; g++)
        {
            var angle = g + 0.5;
            var band = 2 * Math.PI / steps * (Math.Cos(g * Math.PI / 180) - Math.Cos((g + 1) * Math.PI / 180));
            for (var c = 0; c < steps; c++) lumens += built.Intensity(c * 360.0 / steps, angle) * band;
        }

        return new Photometry { Source = source, Watts = watts, Planes = built.Planes, Angles = built.Angles, Candela = built.Candela, Lumens = lumens };
    }
}

/// <summary>What a room is, as the light it is lit to goes.</summary>
public enum LitRoom
{
    Living,
    Family,
    Dining,
    Kitchen,
    Utility,
    Study,
    Bedroom,
    Bathroom,
    Hall,
    Stairs,
    Garage,
    Other
}

/// <summary>The light a room is lit to, lux, on what plane - its height over the floor, mm - and whose figure it is.</summary>
public sealed record LightTarget(double Lux, double Plane, string Where, string Source);

/// <summary>
/// A room's light: what it is, its target, the average its lights give on the working plane by the
/// lumen method, and - from the lights with photometric data - the direct light at its darkest and
/// brightest point; how many lights, and the room index.
/// </summary>
public sealed record RoomLight(Room Room, LitRoom Kind, LightTarget? Target, double Average, double? Darkest, double? Brightest, IReadOnlyList<Component> Lights, double RoomIndex);

/// <summary>
/// A home's lighting: each room's light worked out, and what is wanting.
///
/// The average on the working plane by the lumen method - E = Σ Φ × UF × MF / A, its room index
/// K = 2A / (Hm × P) from its area, perimeter and the lights' height over the plane - its utilisation
/// factor read for surfaces of 70/50/20 from a real fitting's tables: a downlight's from Signify's
/// Philips DN027B, a diffuse fitting's from Cooper's batten as CIBSE TM5 tables it; a maintenance factor
/// of 0.8 - clean rooms, LEDs at L90, as the SLL Code for Lighting's factors come to (not a figure for
/// homes published as such). Where a light has its maker's photometric data, the direct light at each
/// point of the plane, I cos γ / d², at its darkest and brightest.
///
/// Its target: in the UK the light BRE took for each room in SAP 10 (Technical Paper S10TP-06, Appendix
/// A) - 150 lux in a living room and on a dining table, 200 on a kitchen's worktop and a study's desk,
/// 150 in a bathroom, 100 in a bedroom, hall, utility room and on stairs; in the US, the IES's (Lighting
/// Handbook, 10th edition, Table 33.2, as the US Department of Energy gives it) - 32 lux in a living room
/// and hall, 108 in a family room and bathroom, 54 in a bedroom, kitchen, dining room, on stairs and in a
/// garage, 215 on a home office's desk and a laundry's work surface. Recommendations, not regulations.
///
/// And switching: each light switched (in the US a listed wall control, NEC 210.70), a stair of six risers
/// or more switched at each floor (210.70(A)(2)(c)).
/// </summary>
public static class Lighting
{
    /// <summary>The maintenance factor taken: clean rooms, LEDs at L90.</summary>
    public const double MaintenanceFactor = 0.8;

    private static readonly double[] DownlightIndex = { 0.6, 0.8, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
    private static readonly double[] DownlightFactor = { 0.46, 0.56, 0.64, 0.72, 0.77, 0.86, 0.92, 0.95, 1.00, 1.03 };
    private static readonly double[] DiffuseIndex = { 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
    private static readonly double[] DiffuseFactor = { 0.42, 0.50, 0.55, 0.59, 0.64, 0.67, 0.69, 0.72, 0.74 };

    /// <summary>The share of a light's lumens reaching the working plane at a room index, for surfaces of 70/50/20.</summary>
    public static double UtilisationFactor(LightDistribution distribution, double index)
    {
        var (at, values) = distribution == LightDistribution.Downlight ? (DownlightIndex, DownlightFactor) : (DiffuseIndex, DiffuseFactor);
        if (index <= at[0]) return Math.Max(0.1, values[0] - (values[1] - values[0]) / (at[1] - at[0]) * (at[0] - index));
        for (var i = 1; i < at.Length; i++)
            if (index <= at[i]) return values[i - 1] + (values[i] - values[i - 1]) * (index - at[i - 1]) / (at[i] - at[i - 1]);
        return values[^1];
    }

    /// <summary>The light a fitting gives, lm: its photometric data's, its maker's figure, or its watts at its efficacy.</summary>
    public static double LumensOf(ComponentType type) =>
        type.Photometry is { Lumens: > 0 } photometry ? photometry.Lumens
        : type.Lumens > 0 ? type.Lumens
        : type.Efficacy > 0 ? Circuits.LoadOf(type) * type.Efficacy : 0;

    /// <summary>What kind of room it is, for the light it is lit to.</summary>
    public static LitRoom KindOf(BimDocument document, Room room, IReadOnlyList<Point2D> outline)
    {
        var name = room.Name.ToLowerInvariant();
        if (name.Contains("garage")) return LitRoom.Garage;
        if (name.Contains("stair")) return LitRoom.Stairs;
        if (new[] { "hall", "landing", "corridor", "lobby", "foyer", "entry", "porch" }.Any(name.Contains)) return LitRoom.Hall;
        switch (Ventilation.UseOf(document, room, outline))
        {
            case RoomUse.Kitchen: return LitRoom.Kitchen;
            case RoomUse.Utility: return LitRoom.Utility;
            case RoomUse.Bathroom or RoomUse.Wc: return LitRoom.Bathroom;
        }

        if (name.Contains("laundry")) return LitRoom.Utility;
        if (name.Contains("dining")) return LitRoom.Dining;
        if (name.Contains("study") || name.Contains("office")) return LitRoom.Study;
        if (name.Contains("bed") || Bedrooms.Of(document, room, outline) is not null) return LitRoom.Bedroom;
        if (new[] { "family", "play", "recreation", "den" }.Any(name.Contains)) return LitRoom.Family;
        return Ventilation.UseOf(document, room, outline) == RoomUse.Habitable ? LitRoom.Living : LitRoom.Other;
    }

    /// <summary>The light a room of a kind is lit to: BRE's for SAP 10 in the UK, the IES's in the US; none for a room neither gives.</summary>
    public static LightTarget? TargetOf(LitRoom kind, bool us)
    {
        const string bre = "BRE's for SAP 10 (S10TP-06)";
        const string ies = "the IES's (Lighting Handbook, 10th edition)";
        return us
            ? kind switch
            {
                LitRoom.Living => new LightTarget(32, 0, "floor", ies),
                LitRoom.Family => new LightTarget(108, 0, "floor", ies),
                LitRoom.Dining => new LightTarget(54, 0, "floor", ies),
                LitRoom.Kitchen => new LightTarget(54, 0, "floor", ies),
                LitRoom.Utility => new LightTarget(215, 900, "work surface", ies),
                LitRoom.Study => new LightTarget(215, 760, "desk", ies),
                LitRoom.Bedroom => new LightTarget(54, 0, "floor", ies),
                LitRoom.Bathroom => new LightTarget(108, 0, "floor", ies),
                LitRoom.Hall => new LightTarget(32, 0, "floor", ies),
                LitRoom.Stairs => new LightTarget(54, 0, "treads", ies),
                LitRoom.Garage => new LightTarget(54, 0, "floor", ies),
                _ => null
            }
            : kind switch
            {
                LitRoom.Living or LitRoom.Family => new LightTarget(150, 0, "floor", bre),
                LitRoom.Dining => new LightTarget(150, 800, "table", bre),
                LitRoom.Kitchen => new LightTarget(200, 900, "worktop", bre),
                LitRoom.Utility => new LightTarget(100, 0, "floor", bre),
                LitRoom.Study => new LightTarget(200, 800, "desk", bre),
                LitRoom.Bedroom => new LightTarget(100, 0, "floor", bre),
                LitRoom.Bathroom => new LightTarget(150, 0, "floor", bre),
                LitRoom.Hall => new LightTarget(100, 0, "floor", bre),
                LitRoom.Stairs => new LightTarget(100, 0, "treads", bre),
                _ => null
            };
    }

    /// <summary>Where a light gives its light from: its underside, or a wall light's middle - over the datum, mm.</summary>
    private static double SourceHeight(BimDocument document, Component light, ComponentType type) =>
        ComponentHosting.BaseElevation(document, light, type) + (type.Form == ComponentForm.WallLight ? type.Height / 2 : 0);

    /// <summary>A room's light, as its lights now give it; null for a room not enclosed.</summary>
    public static RoomLight? Of(BimDocument document, Room room)
    {
        var boundary = room.GetBoundary(document);
        if (!boundary.IsEnclosed || boundary.Polygon.Count < 3) return null;
        var outline = boundary.Polygon;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var kind = KindOf(document, room, outline);
        var target = TargetOf(kind, us);
        var lights = document.All<Component>()
            .Where(component => component.LevelId == room.LevelId && document.FindType<ComponentType>(component.TypeId)?.Kind == BuiltInCategory.LightingFixtures
                                && RoomsOf.Holds(document, room, outline, component))
            .ToList();
        var floor = RoomHeadroom.Floor(document, room);
        var plane = floor + (target?.Plane ?? 0);
        var area = boundary.Area / 1e6;
        var perimeter = Polygon2D.Perimeter(outline) / 1000;
        if (lights.Count == 0 || area <= 0) return new RoomLight(room, kind, target, 0, null, null, lights, 0);

        // The lumen method, light by light.
        double average = 0, indexSum = 0;
        foreach (var light in lights)
        {
            var type = document.FindType<ComponentType>(light.TypeId)!;
            var above = Math.Max((SourceHeight(document, light, type) - plane) / 1000, 0.3);
            var index = 2 * area / (above * perimeter);
            indexSum += index;
            average += LumensOf(type) * UtilisationFactor(type.Distribution, index) * MaintenanceFactor / area;
        }

        // The direct light, point by point, from the lights with photometric data.
        double? darkest = null, brightest = null;
        var measured = lights.Select(light => (Light: light, Type: document.FindType<ComponentType>(light.TypeId)!)).Where(entry => entry.Type.Photometry is not null).ToList();
        if (measured.Count > 0)
        {
            var box = BoundingBox2D.Around(outline);
            var step = Math.Clamp(Math.Min(box.Width, box.Height) / 8, 250, 500);
            var values = new List<double>();
            for (var x = box.MinX + step / 2; x < box.MaxX; x += step)
            for (var y = box.MinY + step / 2; y < box.MaxY; y += step)
            {
                var point = new Point2D(x, y);
                if (!Polygon2D.Contains(outline, point)) continue;
                double lux = 0;
                foreach (var (light, type) in measured)
                {
                    var (dx, dy) = ((point.X - light.Location.X) / 1000, (point.Y - light.Location.Y) / 1000);
                    var h = (SourceHeight(document, light, type) - plane) / 1000;
                    if (h <= 0) continue;
                    var turn = -light.Rotation * Math.PI / 180;
                    var (lx, ly) = (dx * Math.Cos(turn) - dy * Math.Sin(turn), dx * Math.Sin(turn) + dy * Math.Cos(turn));
                    var squared = dx * dx + dy * dy + h * h;
                    var gamma = Math.Acos(h / Math.Sqrt(squared)) * 180 / Math.PI;
                    var c = Math.Atan2(ly, lx) * 180 / Math.PI;
                    lux += type.Photometry!.Intensity(c, gamma) * (h / Math.Sqrt(squared)) / squared;
                }

                values.Add(lux);
            }

            if (values.Count > 0) (darkest, brightest) = (values.Min(), values.Max());
        }

        return new RoomLight(room, kind, target, average, darkest, brightest, lights, indexSum / lights.Count);
    }

    /// <summary>Every enclosed room's light.</summary>
    public static IReadOnlyList<RoomLight> Rooms(BimDocument document) =>
        document.All<Room>().Select(room => Of(document, room)).OfType<RoomLight>().ToList();

    /// <summary>A room's light, as its Services say it - or null where it has no lights.</summary>
    public static string? Describe(BimDocument document, RoomLight light)
    {
        if (light.Lights.Count == 0) return null;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        string Lux(double lux) => us ? $"{lux / 10.764:0.#} fc ({lux:0} lux)" : $"{lux:0} lux";
        var said = $"light {Lux(light.Average)} on the {light.Target?.Where ?? "floor"}";
        if (light.Target is { } target) said += $" of {Lux(target.Lux)}";
        if (light.Darkest is { } dark && light.Brightest is { } bright) said += $", direct {Lux(dark)} to {Lux(bright)}";
        return said;
    }

    /// <summary>Switching - all of it, or one: a switch and a light on each.</summary>
    public static IEnumerable<MepProblem> SwitchingProblems(BimDocument document, MepSystem? only)
    {
        foreach (var system in document.All<MepSystem>().Where(system => system.Kind == SystemKind.Switching && (only is null || ReferenceEquals(system, only))))
        {
            var members = MepSystems.MembersOf(document, system);
            if (members.Count == 0) continue;
            if (!members.Any(member => document.FindType<ComponentType>(member.TypeId)?.Form == ComponentForm.Switch))
                yield return new MepProblem(system, "no switch on it - select its lights with the switch that turns them on");
            else if (!members.Any(member => document.FindType<ComponentType>(member.TypeId)?.Kind == BuiltInCategory.LightingFixtures))
                yield return new MepProblem(system, "no lights on it");
        }
    }

    /// <summary>What is wanting in the lighting: rooms short of their light, lights not switched, a stair switched from one floor.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        foreach (var light in Rooms(document))
        {
            if (light.Lights.Count == 0 || light.Target is not { } target || light.Average >= target.Lux - 0.5) continue;
            var kind = light.Kind.ToString().ToLowerInvariant();
            yield return new MepProblem(light.Room, us
                ? $"its lights give {light.Average / 10.764:0.#} fc ({light.Average:0} lux) on the {target.Where} - the IES recommends {target.Lux / 10.764:0.#} fc for a {kind} (Lighting Handbook, 10th edition)"
                : $"its lights give {light.Average:0} lux on the {target.Where} - BRE took {target.Lux:0} lux for a {kind} in SAP 10 (S10TP-06)");
        }

        foreach (var problem in SwitchingProblems(document, null)) yield return problem;

        if (!us) yield break;

        // A stair of six risers or more: a light, switched at each floor (NEC 210.70(A)(2)).
        foreach (var stair in document.All<Stair>().Where(stair => stair.TopLevelId is not null))
        {
            StairLayout layout;
            try { layout = Stairs.Layout(document, stair); }
            catch (Exception) { continue; }
            if (layout.Risers < 6) continue;
            var footprint = Stairs.Footprint(layout);
            if (footprint.Count < 3) continue;
            var levels = new[] { stair.LevelId, stair.TopLevelId!.Value };
            var lights = document.All<Component>().Where(component => levels.Contains(component.LevelId)
                                                                      && document.FindType<ComponentType>(component.TypeId)?.Kind == BuiltInCategory.LightingFixtures
                                                                      && (Polygon2D.Contains(footprint, component.Location)
                                                                          || footprint.Select((point, i) => Line2D.DistanceFromSegment(component.Location, point, footprint[(i + 1) % footprint.Count])).Min() <= 1500))
                .ToList();
            if (lights.Count == 0)
            {
                yield return new MepProblem(stair, "no light over it - NEC 210.70(A)(2) asks for a lighting outlet at stairways");
                continue;
            }

            var switches = document.All<MepSystem>().Where(system => system.Kind == SystemKind.Switching && lights.Any(light => system.Members.Contains(light.Id)))
                .SelectMany(system => MepSystems.MembersOf(document, system)).Where(member => document.FindType<ComponentType>(member.TypeId)?.Form == ComponentForm.Switch)
                .Select(member => member.LevelId).ToHashSet();
            if (!levels.All(switches.Contains))
                yield return new MepProblem(stair, $"its light is not switched at each floor - NEC 210.70(A)(2)(c) puts a switch at each floor level of a stair of six risers or more; it has {layout.Risers}");
        }
    }
}

/// <summary>A light's photometric data loaded into its type - its lumens and watts taken from it - as one step to undo.</summary>
public sealed class SetPhotometryCommand : Documents.Commands.IUndoableCommand
{
    private readonly ComponentType _type;
    private readonly (Photometry? Photometry, double Lumens, double Load) _before;
    private readonly (Photometry? Photometry, double Lumens, double Load) _after;

    public SetPhotometryCommand(ComponentType type, Photometry photometry)
    {
        _type = type;
        _before = (type.Photometry, type.Lumens, type.Load);
        _after = (photometry, photometry.Lumens, photometry.Watts > 0 ? photometry.Watts : type.Load);
    }

    public string Name => "Load Light Data";

    public void Redo() => (_type.Photometry, _type.Lumens, _type.Load) = _after;

    public void Undo() => (_type.Photometry, _type.Lumens, _type.Load) = _before;
}
