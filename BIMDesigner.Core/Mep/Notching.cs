using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Where a pipe or duct crosses a timber joist within its depth: a notch cut down from its top, or a
/// hole drilled through it - how big, how high, how far along its span from its nearer support, and
/// the joist's depth and span.
/// </summary>
public sealed record JoistCut(Element Service, BeamSystem Joists, Point3D At, double Size, bool Notch, double FromSupport, double Span, double Depth, double Top)
{
    /// <summary>A hole's middle over the joist's underside, mm.</summary>
    public double Centre => At.Z - (Top - Depth);

    /// <summary>Whether a notch is cut up from the joist's underside rather than down from its top.</summary>
    public bool FromBelow { get; init; }

    /// <summary>How wide a hole must be to take it: the wider of its height and its width - a flat duct needs a hole as wide as it is.</summary>
    public double Across { get; init; }

    /// <summary>The hole's size: what it must take every way.</summary>
    public double Hole => Math.Max(Size, Across);
}

/// <summary>
/// Services through timber floor joists: notched or drilled, and kept to where and how much a joist
/// may be cut without weakening it.
///
/// In the UK as the NHBC Standards (2025, 6.4.18, Table 6) give it for solid timber joists up to 250 mm
/// deep: a notch in the top edge only, no deeper than 0.15 of the joist's depth, between 0.1 and 0.2 of
/// the span from a support; a hole no wider than 0.25 of the depth, drilled on its centre line, between
/// 0.25 and 0.4 of the span; notches and holes 100 mm apart at least; a deeper joist cut only as an
/// engineer designs it. In the US the IRC (R502.8.1): a notch no deeper than a sixth of the depth and
/// not in the middle third of the span - a quarter at the end, over a support; a hole no larger than a
/// third of the depth, 2 in from the top and bottom edges and from other holes and notches.
/// </summary>
public static class Notching
{
    private static (Point3D Start, Point3D End, double Size, double Width)? Run(BimDocument document, Element service) => service switch
    {
        Pipe pipe when !pipe.Upright => pipe.Ends(document) is var (start, end) ? (start, end, pipe.Diameter + 2 * pipe.Insulation, pipe.Diameter + 2 * pipe.Insulation) : null,
        Duct duct when !duct.Upright => duct.Ends(document) is var (start, end) ? (start, end, duct.Height + 2 * duct.Insulation, duct.Width + 2 * duct.Insulation) : null,
        _ => null
    };

    private static bool Cross(Point2D a, Point2D b, Point2D c, Point2D d, out Point2D at, out double t, out double u)
    {
        at = default;
        (t, u) = (0, 0);
        var (rx, ry, sx, sy) = (b.X - a.X, b.Y - a.Y, d.X - c.X, d.Y - c.Y);
        var denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-9) return false;
        t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / denominator;
        u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        at = new Point2D(a.X + rx * t, a.Y + ry * t);
        return true;
    }

    /// <summary>Every notch and hole the services ask of the timber joists they cross.</summary>
    public static IReadOnlyList<JoistCut> Cuts(BimDocument document)
    {
        var cuts = new List<JoistCut>();
        var services = document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()).Select(service => (Service: service, Run: Run(document, service)))
            .Where(entry => entry.Run is not null).ToList();
        if (services.Count == 0) return cuts;
        foreach (var system in document.All<BeamSystem>())
        {
            if (BeamSystems.TypeOf(document, system) is not { Material: BeamMaterial.Timber } type) continue;
            var top = BeamSystems.Top(document, system);
            var bottom = top - type.Depth;
            foreach (var (from, to) in BeamSystems.Joists(document, system))
            {
                var span = from.DistanceTo(to);
                if (span < 1) continue;
                foreach (var (service, run) in services)
                {
                    var (start, end, size, width) = run!.Value;
                    if (!Cross(new Point2D(start.X, start.Y), new Point2D(end.X, end.Y), from, to, out var at, out var t, out var u)) continue;
                    var z = start.Z + (end.Z - start.Z) * t;
                    if (z + size / 2 <= bottom || z - size / 2 >= top) continue;
                    var fromTop = z + size / 2 >= top - 1;
                    var fromBelow = !fromTop && z - size / 2 <= bottom + 1;
                    var notch = fromTop || fromBelow;
                    cuts.Add(new JoistCut(service, system, new Point3D(at.X, at.Y, z), fromTop ? top - (z - size / 2) : fromBelow ? z + size / 2 - bottom : size, notch,
                        Math.Min(u, 1 - u) * span, span, type.Depth, top) { FromBelow = fromBelow, Across = width });
                }
            }
        }

        return cuts;
    }

    /// <summary>What is wrong with a notch or hole, as the project's standard has it; empty where nothing is.</summary>
    public static IReadOnlyList<string> Faults(JoistCut cut, bool us, IReadOnlyList<JoistCut>? others = null)
    {
        var faults = new List<string>();
        var share = cut.FromSupport / cut.Span;
        if (us)
        {
            const double inch = 25.4;
            // 2 in between their edges.
            var close = others?.Where(other => !ReferenceEquals(other, cut) && other.Joists == cut.Joists
                                               && new Point2D(other.At.X, other.At.Y).DistanceTo(new Point2D(cut.At.X, cut.At.Y)) < (other.Hole + cut.Hole) / 2 + 2 * inch - 0.5
                                               && Math.Abs(other.FromSupport - cut.FromSupport) < (other.Hole + cut.Hole) / 2 + 2 * inch).ToList();
            if (close is { Count: > 0 })
                faults.Add("cuts less than 2 in apart in one joist - IRC R502.8.1 keeps a hole 2 in from other holes and notches");
            if (cut.Notch)
            {
                var end = cut.FromSupport < cut.Depth;
                var most = cut.Depth / (end ? 4 : 6);
                if (cut.Size > most + 0.5)
                    faults.Add($"a notch {cut.Size:0} mm deep in a joist {cut.Depth:0} deep - IRC R502.8.1 notches no deeper than {(end ? "a quarter of the depth at the end" : "a sixth of it")}, {most:0} mm");
                if (share > 1.0 / 3 + 1e-6)
                    faults.Add("a notch in the middle third of the joist's span - IRC R502.8.1 allows none there");
            }
            else
            {
                if (cut.Hole > cut.Depth / 3 + 0.5)
                    faults.Add($"a hole {cut.Hole:0} mm across in a joist {cut.Depth:0} deep - IRC R502.8.1 bores no larger than a third of the depth, {cut.Depth / 3:0} mm");
                if (cut.Centre - cut.Size / 2 < 2 * inch - 0.5 || cut.Depth - (cut.Centre + cut.Size / 2) < 2 * inch - 0.5)
                    faults.Add("a hole within 2 in (51 mm) of the joist's top or bottom edge - IRC R502.8.1 keeps holes 2 in from them");
            }

            return faults;
        }

        const string nhbc = "NHBC Standards 6.4.18, Table 6";
        if (cut.Depth > 250 + 0.5)
        {
            faults.Add($"a {(cut.Notch ? "notch" : "hole")} in a joist {cut.Depth:0} mm deep - {nhbc} has an engineer design the cuts in a joist deeper than 250 mm");
            return faults;
        }

        if (cut.Notch)
        {
            if (cut.FromBelow)
                faults.Add($"a notch in the joist's underside - notches are cut in the top edge only ({nhbc})");
            if (cut.Size > 0.15 * cut.Depth + 0.5)
                faults.Add($"a notch {cut.Size:0} mm deep in a joist {cut.Depth:0} deep - a notch is no deeper than 0.15 of the depth, {0.15 * cut.Depth:0} mm ({nhbc})");
            if (share < 0.1 - 1e-6 || share > 0.2 + 1e-6)
                faults.Add($"a notch {share:0.00} of the span from the support - notches go between 0.1 and 0.2 of it ({nhbc})");
        }
        else
        {
            if (cut.Hole > 0.25 * cut.Depth + 0.5)
                faults.Add($"a hole {cut.Hole:0} mm across in a joist {cut.Depth:0} deep - a hole is no wider than 0.25 of the depth, {0.25 * cut.Depth:0} mm ({nhbc})");
            if (Math.Abs(cut.Centre - cut.Depth / 2) > Math.Max(10, cut.Depth * 0.05))
                faults.Add($"a hole off the joist's centre line - holes are drilled on it ({nhbc})");
            if (share < 0.25 - 1e-6 || share > 0.4 + 1e-6)
                faults.Add($"a hole {share:0.00} of the span from the support - holes go between 0.25 and 0.4 of it ({nhbc})");
        }

        var near = others?.Where(other => !ReferenceEquals(other, cut) && other.Joists == cut.Joists
                                           && new Point2D(other.At.X, other.At.Y).DistanceTo(new Point2D(cut.At.X, cut.At.Y)) < (other.Hole + cut.Hole) / 2 + 100 - 0.5
                                           && Math.Abs(other.FromSupport - cut.FromSupport) < (other.Hole + cut.Hole) / 2 + 100).ToList();
        if (near is { Count: > 0 })
            faults.Add($"cuts less than 100 mm apart in one joist ({nhbc})");

        return faults;
    }

    /// <summary>What is wanting where the services cross timber joists.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var cuts = Cuts(document);
        foreach (var group in cuts.GroupBy(cut => cut.Service))
        {
            var said = group.SelectMany(cut => Faults(cut, us, cuts)).Distinct().ToList();
            if (said.Count == 0) continue;
            var count = group.Count(cut => Faults(cut, us, cuts).Count > 0);
            yield return new MepProblem(group.Key, $"it crosses {count} joist{(count == 1 ? string.Empty : "s")} where {(count == 1 ? "it" : "they")} may not be cut so: {string.Join("; ", said)}");
        }
    }
}
