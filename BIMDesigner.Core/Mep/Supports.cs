using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>One support along a pipe or duct: where it is, and whether the run it holds goes up rather than along.</summary>
public readonly record struct Support(Element Service, Point3D At, bool Upright);

/// <summary>
/// The clips, brackets and hangers holding the pipes and ducts up, at the spacings their material and
/// size ask - along a run and up one - evenly spread, none further apart than that and the ends within
/// half of it.
///
/// In the UK, water pipes as BS 6700 (Table 14) set them - withdrawn, its successor BS 8558's table not
/// published - copper and stainless 15 mm 1.2 m along and 1.8 m up, 22 and 28 mm 1.8 and 2.4, 35 and
/// 42 mm 2.4 and 3.0, 54 mm 2.7 and 3.0; PE-X to 16 mm 0.3 and 0.5, to 25 mm 0.5 and 0.8, 28 mm 0.8 and
/// 1.0 (as its makers give it); gas pipe as BS 6891 (Table 4); PVC-U soil and waste as its makers -
/// 32 to 40 mm 0.5 and 1.2, 50 mm 0.6 and 1.2, 110 mm 1.0 and 2.0; ductwork as DW/144 (Tables 16 and
/// 17) - 3 m, 2.5 m for a rectangular duct over 1 m across - a home's plastic ducting at 750 mm and
/// flexible duct at 600 mm (Domus). In the US the IRC's Table P2605.1 - copper tubing to 1¼ in 6 ft
/// along and 10 ft up, PEX to 1 in 32 in and 10 ft, PVC 4 ft and 10 ft, steel 12 ft and 15 ft - and
/// ducts as SMACNA - rectangular 10 ft, round 12 ft - flexible duct 4 ft (the ADC's standards).
/// </summary>
public static class Supports
{
    private const double Foot = 304.8;

    /// <summary>The most a pipe's or duct's supports may be apart, along a run and up one, mm, and the source.</summary>
    public static (double Along, double Up, string Source)? Spacing(BimDocument document, Element service)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        switch (service)
        {
            case Pipe pipe:
            {
                var material = (document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper).Like();
                var d = pipe.Diameter;
                if (us)
                    return material switch
                    {
                        // 1-1/4" and under, 1" and under - a CTS tube an eighth over its nominal outside.
                        PipeMaterial.Copper => d <= 35 ? (6 * Foot, 10 * Foot, "IRC Table P2605.1") : (10 * Foot, 10 * Foot, "IRC Table P2605.1"),
                        PipeMaterial.PeX => d <= 29 ? (32 * 25.4, 10 * Foot, "IRC Table P2605.1") : (4 * Foot, 10 * Foot, "IRC Table P2605.1"),
                        PipeMaterial.PvcU => (4 * Foot, 10 * Foot, "IRC Table P2605.1"),
                        PipeMaterial.GalvanisedSteel => (12 * Foot, 15 * Foot, "IRC Table P2605.1"),
                        _ => (10 * Foot, 10 * Foot, "IRC Table P2605.1")
                    };

                if (pipe.Kind == SystemKind.Gas)
                    return material == PipeMaterial.GalvanisedSteel
                        ? d <= 22 ? (2000, 2500, "BS 6891 Table 4") : d <= 34 ? (2500, 3000, "BS 6891 Table 4") : (2700, 3300, "BS 6891 Table 4")
                        : d <= 15 ? (1500, 2000, "BS 6891 Table 4") : d <= 28 ? (2000, 2500, "BS 6891 Table 4") : (2500, 2500, "BS 6891 Table 4");
                return material switch
                {
                    PipeMaterial.PeX => d <= 16 ? (300, 500, "its makers' figures, as BS 6700 Table 14") : d <= 25 ? (500, 800, "its makers' figures, as BS 6700 Table 14")
                        : d <= 28 ? (800, 1000, "its makers' figures, as BS 6700 Table 14") : (900, 1200, "BS 6700 Table 14"),
                    PipeMaterial.PvcU => d <= 40 ? (500, 1200, "its makers' figures") : d <= 50 ? (600, 1200, "its makers' figures")
                        : d <= 110 ? (1000, 2000, "its makers' figures") : (900, 1800, "its makers' figures"),
                    PipeMaterial.GalvanisedSteel => d <= 22 ? (1800, 2000, "BS 6700 Table 14") : d <= 34 ? (2400, 3000, "BS 6700 Table 14")
                        : d <= 43 ? (2700, 3000, "BS 6700 Table 14") : (3000, 3600, "BS 6700 Table 14"),
                    _ => d <= 15 ? (1200, 1800, "BS 6700 Table 14") : d <= 28 ? (1800, 2400, "BS 6700 Table 14") : d <= 42 ? (2400, 3000, "BS 6700 Table 14")
                        : d <= 54 ? (2700, 3000, "BS 6700 Table 14") : (3000, 3600, "BS 6700 Table 14")
                };
            }

            case Duct duct:
            {
                var type = document.FindType<DuctType>(duct.TypeId);
                var round = duct.Size(document).Shape == DuctShape.Round;
                if (us)
                    return type?.Material == DuctMaterial.Flexible ? (4 * Foot, 6 * Foot, "the ADC's flexible duct standards (4.6)")
                        : round ? (12 * Foot, 12 * Foot, "SMACNA's duct construction standards") : (10 * Foot, 10 * Foot, "SMACNA's duct construction standards");
                return type?.Material switch
                {
                    DuctMaterial.Flexible => (600, 600, "Domus's figures for flexible duct"),
                    DuctMaterial.Pvc => (750, 750, "Domus's figures for rigid plastic duct"),
                    _ => Math.Max(duct.Width, duct.Height) > 1000 && !round ? (2500, 4000, "DW/144 Table 16") : (3000, 4000, round ? "DW/144 Table 17" : "DW/144 Table 16")
                };
            }

            default:
                return null;
        }
    }

    /// <summary>The shortest piece of pipe or duct with a support of its own, mm: shorter, it is held by what it joins.</summary>
    public const double ShortestHeld = 300;

    /// <summary>A pipe's or duct's supports, spread evenly along it at no more than its spacing.</summary>
    public static IReadOnlyList<Support> Of(BimDocument document, Element service)
    {
        if (Spacing(document, service) is not var (along, up, _)) return Array.Empty<Support>();
        var (start, end, upright) = service switch
        {
            Pipe pipe => (pipe.Ends(document).Start, pipe.Ends(document).End, pipe.Upright),
            Duct duct => (duct.Ends(document).Start, duct.Ends(document).End, duct.Upright),
            _ => (default(Point3D), default(Point3D), false)
        };
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2));

        // A drain below ground is bedded in its trench, not clipped; a short piece between fittings is held by them.
        if (length < ShortestHeld || service is Pipe buried && Drainage.BelowGround(document, buried)) return Array.Empty<Support>();
        var count = (int)Math.Ceiling(length / (upright ? up : along) - 1e-9);
        count = Math.Max(1, count);
        return Enumerable.Range(0, count).Select(i =>
        {
            var t = (i + 0.5) / count;
            return new Support(service, new Point3D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t), upright);
        }).ToList();
    }

    /// <summary>Every pipe's and duct's supports.</summary>
    public static IReadOnlyList<Support> All(BimDocument document) =>
        document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()).SelectMany(service => Of(document, service)).ToList();

    /// <summary>A pipe's or duct's supports as its Properties say them.</summary>
    public static string Describe(BimDocument document, Element service)
    {
        if (Spacing(document, service) is not var (along, up, source)) return "None";
        var count = Of(document, service).Count;
        var upright = service is Pipe { Upright: true } or Duct { Upright: true };
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var spacing = upright ? up : along;
        return $"{count} {(service is Duct ? "hanger" : "clip")}{(count == 1 ? string.Empty : "s")}, {(us ? $"{spacing / Foot:0.#} ft" : $"{Units.Metres(spacing, "0.0#")}")} apart at most {(upright ? "up it" : "along it")} ({source})";
    }

    /// <summary>What one support costs: a pipe clip or a duct hanger and its fixing.</summary>
    public static decimal Rate(Element service, bool us) => service is Duct ? (us ? 12m : 9m) : (us ? 1.50m : 1.20m);
}

/// <summary>
/// Where services keep clear of each other: in the US the space over a panelboard to 6 ft above it,
/// its width and depth, kept clear of pipes and ducts (IRC E3405.3, after NEC 110.26(E)); in the UK
/// gas pipe 150 mm at least from a consumer unit or an electricity meter, 25 mm from cables, switches
/// and sockets (BS 6891, 8.4.2).
/// </summary>
public static class ServiceClearances
{
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var boards = document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.ConsumerUnit).ToList();
        if (us)
        {
            foreach (var board in boards)
            {
                var type = document.FindType<ComponentType>(board.TypeId)!;
                var outline = Views.ComponentModel.Footprint(type, Views.ComponentModel.FrameOf(board, 0));
                if (outline.Count < 3) continue;
                var top = ComponentHosting.BaseElevation(document, board, type) + type.Height;
                var floor = document.FindLevel(board.LevelId)?.Elevation ?? 0;
                foreach (var service in document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()))
                {
                    var (start, end) = service is Pipe pipe ? pipe.Ends(document) : ((Duct)service).Ends(document);
                    if (Overhead(outline, start, end, floor, top + 6 * 304.8))
                        yield return new MepProblem(service, "it runs through a panelboard's dedicated space - IRC E3405.3 (NEC 110.26(E)) keeps its width and depth, from the floor to 6 ft above it, clear of pipes and ducts");
                }
            }

            yield break;
        }

        // Gas pipe kept from the electrics.
        var electrics = document.All<Component>()
            .Select(component => (Component: component, Type: document.FindType<ComponentType>(component.TypeId)))
            .Where(entry => entry.Type?.Form is ComponentForm.ConsumerUnit or ComponentForm.Socket or ComponentForm.Switch)
            .ToList();
        foreach (var pipe in document.All<Pipe>().Where(pipe => pipe.Kind == SystemKind.Gas))
        {
            var (start, end) = pipe.Ends(document);
            foreach (var (component, type) in electrics)
            {
                var least = type!.Form == ComponentForm.ConsumerUnit ? 150 : 25;
                var box = Box(document, component, type);
                var apart = Distance(start, end, box) - pipe.Diameter / 2;
                if (apart < least - 0.5)
                {
                    yield return new MepProblem(pipe, $"it runs {Units.Millimetres(Math.Max(apart, 0), "0")} from a {(type.Form == ComponentForm.ConsumerUnit ? "consumer unit" : type.Form == ComponentForm.Socket ? "socket" : "switch")} - BS 6891 (8.4.2) keeps gas pipe {least} mm from {(least == 150 ? "consumer units and electricity meters" : "cables, switches and sockets")}");
                    break;
                }
            }
        }
    }

    private static (Point3D Min, Point3D Max) Box(BimDocument document, Component component, ComponentType type)
    {
        var outline = Views.ComponentModel.Footprint(type, Views.ComponentModel.FrameOf(component, 0));
        var bottom = ComponentHosting.BaseElevation(document, component, type);
        if (outline.Count == 0) return (Point3D.On(component.Location, bottom), Point3D.On(component.Location, bottom + type.Height));
        return (new Point3D(outline.Min(p => p.X), outline.Min(p => p.Y), bottom), new Point3D(outline.Max(p => p.X), outline.Max(p => p.Y), bottom + type.Height));
    }

    /// <summary>How many points along a run it is sampled at: every 10 mm or so.</summary>
    private static int Steps(Point3D start, Point3D end) =>
        Math.Clamp((int)Math.Ceiling(Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2) + Math.Pow(end.Z - start.Z, 2)) / 10), 8, 5000);

    /// <summary>How near a straight run comes to a box, sampled finely along it.</summary>
    private static double Distance(Point3D start, Point3D end, (Point3D Min, Point3D Max) box)
    {
        var best = double.MaxValue;
        var steps = Steps(start, end);
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var (x, y, z) = (start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
            var dx = Math.Max(Math.Max(box.Min.X - x, 0), x - box.Max.X);
            var dy = Math.Max(Math.Max(box.Min.Y - y, 0), y - box.Max.Y);
            var dz = Math.Max(Math.Max(box.Min.Z - z, 0), z - box.Max.Z);
            best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }

        return best;
    }

    /// <summary>Whether a run passes over an outline between two heights.</summary>
    private static bool Overhead(IReadOnlyList<Point2D> outline, Point3D start, Point3D end, double low, double high)
    {
        var steps = Steps(start, end);
        for (var i = 0; i <= steps; i++)
        {
            var t = (double)i / steps;
            var z = start.Z + (end.Z - start.Z) * t;
            if (z < low || z > high) continue;
            if (Polygon2D.Contains(outline, new Point2D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t))) return true;
        }

        return false;
    }
}
