using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Where a pipe or duct passes through a wall or a floor: what it passes, where, how big a hole it
/// needs - round or square, the service's size and the gap round it - whether the wall or floor
/// separates compartments and how long it resists fire, and the opening cut for it, if any.
/// </summary>
public sealed record Penetration(Element Service, Element Through, Point3D At, double Width, double Height, bool Round, bool Separating, int Minutes)
{
    /// <summary>The opening cut for it - a wall opening or a shaft opening - or none yet.</summary>
    public Element? Opening { get; init; }

    /// <summary>Whether the opening cut takes it - the service's own size within it; true where none is cut.</summary>
    public bool Fits { get; init; } = true;
}

/// <summary>
/// The builder's work for the services: where pipes and ducts pass through walls and floors, the
/// openings cut for them, and keeping fire separation where they pass through compartment walls and
/// floors. Cut Openings makes a wall opening or a shaft opening wherever one is missing, the service's
/// size and a gap all round it for its fire-stopping - and no larger, as Approved Document B asks.
/// </summary>
public static class Penetrations
{
    /// <summary>The gap left all round a pipe or duct in the opening cut for it, for its fire-stopping or sealing, mm.</summary>
    public const double Gap = 25;

    private static (Point3D Start, Point3D End, double Width, double Height, bool Round, Guid LevelId)? Run(BimDocument document, Element service) => service switch
    {
        Pipe pipe when pipe.Length >= 1 => pipe.Ends(document) is var (start, end) ? (start, end, pipe.Diameter, pipe.Diameter, true, pipe.LevelId) : null,
        Duct duct when duct.Length >= 1 => duct.Ends(document) is var (start, end) ? (start, end, duct.Width, duct.Height, duct.Size(document).Shape == DuctShape.Round, duct.LevelId) : null,
        _ => null
    };

    /// <summary>Where one pipe or duct passes through walls on its storey and floors anywhere.</summary>
    public static IEnumerable<Penetration> Of(BimDocument document, Element service)
    {
        if (Run(document, service) is not var (start, end, width, height, round, levelId)) yield break;
        var (planStart, planEnd) = (new Point2D(start.X, start.Y), new Point2D(end.X, end.Y));

        // Through walls, running along.
        if (planStart.DistanceTo(planEnd) >= 1)
            foreach (var wall in document.Walls.Where(wall => wall.LevelId == levelId && !wall.IsCurved))
            {
                if (!Cross(planStart, planEnd, wall.Start, wall.End, out var at)) continue;
                var t = planStart.DistanceTo(at) / planStart.DistanceTo(planEnd);
                var z = start.Z + (end.Z - start.Z) * t;
                if (z < wall.GetBaseElevation(document) || z > wall.GetTopElevation(document)) continue;
                var type = document.GetWallType(wall);

                // Square across the wall the hole is the service's width; at a slant, longer along the wall by its angle and the wall's thickness.
                var (run, face) = ((planEnd - planStart).NormalisedOrDefault(Vector2D.UnitX), (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX));
                var slant = Math.Max(0.2, Math.Abs(run.X * face.Y - run.Y * face.X));
                var along = (width + 2 * Gap) / slant + (type?.Width ?? 0) * Math.Sqrt(1 - slant * slant) / slant;
                var hole = new Penetration(service, wall, new Point3D(at.X, at.Y, z), along, height + 2 * Gap, round && slant > 0.99,
                    type?.Separating == true, Doors.FireMinutes(type?.FireRating ?? string.Empty));
                yield return Cut(document, hole);
            }

        // Through floors, rising or dropping.
        if (Math.Abs(end.Z - start.Z) >= 1)
            foreach (var floor in document.All<Floor>())
            {
                var middle = (floor.GetTopElevation(document) + floor.GetBottomElevation(document)) / 2;
                if (middle < Math.Min(start.Z, end.Z) || middle > Math.Max(start.Z, end.Z)) continue;
                var t = (middle - start.Z) / (end.Z - start.Z);
                var at = new Point2D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
                if (!floor.Contains(at)) continue;
                var type = document.FindType<SlabType>(floor.TypeId);
                var hole = new Penetration(service, floor, new Point3D(at.X, at.Y, middle), width + 2 * Gap, height + 2 * Gap, round,
                    type?.Separating == true, Doors.FireMinutes(type?.FireRating ?? string.Empty));
                yield return Cut(document, hole);
            }
    }

    /// <summary>A penetration with the opening cut there, if any, and whether it takes the service.</summary>
    private static Penetration Cut(BimDocument document, Penetration hole) =>
        OpeningFor(document, hole) is { } opening ? hole with { Opening = opening, Fits = Takes(document, opening, hole) } : hole;

    /// <summary>Whether an opening takes a service through it: the service's own size - the hole less its gap - within it.</summary>
    private static bool Takes(BimDocument document, Element opening, Penetration hole)
    {
        var (width, height) = (hole.Width - 2 * Gap, hole.Height - 2 * Gap);
        switch (opening)
        {
            case WallOpening cut when hole.Through is Wall wall:
                var (along, up) = Along(document, wall, hole.At);
                return Math.Abs(cut.DistanceAlongWall - along) + width / 2 <= cut.Width / 2 + 1
                       && up - height / 2 >= cut.SillHeight - 1 && up + height / 2 <= cut.SillHeight + cut.Height + 1;
            case ShaftOpening shaft:
                var plan = new Point2D(hole.At.X, hole.At.Y);
                return Enumerable.Range(0, 8).Select(i => i * Math.PI / 4)
                    .All(angle => Polygon2D.Contains(shaft.Outline, new Point2D(plan.X + Math.Cos(angle) * width / 2 * 0.99, plan.Y + Math.Sin(angle) * height / 2 * 0.99)));
            default:
                return true;
        }
    }

    /// <summary>Where two segments cross in plan.</summary>
    private static bool Cross(Point2D a, Point2D b, Point2D c, Point2D d, out Point2D at)
    {
        at = default;
        var (rx, ry, sx, sy) = (b.X - a.X, b.Y - a.Y, d.X - c.X, d.Y - c.Y);
        var denominator = rx * sy - ry * sx;
        if (Math.Abs(denominator) < 1e-9) return false;
        var t = ((c.X - a.X) * sy - (c.Y - a.Y) * sx) / denominator;
        var u = ((c.X - a.X) * ry - (c.Y - a.Y) * rx) / denominator;
        if (t < 0 || t > 1 || u < 0 || u > 1) return false;
        at = new Point2D(a.X + rx * t, a.Y + ry * t);
        return true;
    }

    /// <summary>Every pipe and duct's penetrations.</summary>
    public static IReadOnlyList<Penetration> All(BimDocument document) =>
        document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()).SelectMany(service => Of(document, service)).ToList();

    /// <summary>The opening already cut where a service passes: a wall opening in the wall round the point, or a shaft opening through the floor there.</summary>
    public static Element? OpeningFor(BimDocument document, Penetration hole)
    {
        if (hole.Through is Wall wall)
        {
            var (along, height) = Along(document, wall, hole.At);
            return document.All<WallOpening>().FirstOrDefault(opening => opening.HostWallId == wall.Id
                && Math.Abs(opening.DistanceAlongWall - along) <= opening.Width / 2 + 1 && height >= opening.SillHeight - 1 && height <= opening.SillHeight + opening.Height + 1);
        }

        var plan = new Point2D(hole.At.X, hole.At.Y);
        return document.All<ShaftOpening>().FirstOrDefault(shaft => shaft.BaseElevation(document) <= hole.At.Z + 1 && shaft.TopElevation(document) >= hole.At.Z - 1
                                                                     && Polygon2D.Contains(shaft.Outline, plan));
    }

    /// <summary>How far along a straight wall from its start a point is, and how high over its base, mm.</summary>
    private static (double Along, double Height) Along(BimDocument document, Wall wall, Point3D at)
    {
        var direction = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        return ((new Point2D(at.X, at.Y) - wall.Start).Dot(direction), at.Z - wall.GetBaseElevation(document));
    }

    /// <summary>The openings Cut Openings makes: one for each penetration with none, sized to the service and the gap round it.</summary>
    public static IReadOnlyList<Element> Openings(BimDocument document)
    {
        var made = new List<Element>();
        foreach (var hole in All(document).Where(hole => hole.Opening is null))
        {
            var name = hole.Service switch
            {
                Pipe pipe => $"{pipe.Kind.Title()} pipe {pipe.Diameter:0}",
                Duct duct => $"{duct.Kind.Title()} duct {duct.Size(document).Name}",
                _ => "service"
            };

            // One opening for services that pass at the same place, the larger kept.
            if (made.Any(opening => Covers(document, opening, hole))) continue;
            if (hole.Through is Wall wall)
            {
                var (along, height) = Along(document, wall, hole.At);
                made.Add(new WallOpening
                {
                    LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = along, Width = hole.Width, Height = hole.Height,
                    SillHeight = height - hole.Height / 2, ForServices = true, Comments = $"For {name}"
                });
            }
            else if (hole.Through is Floor floor)
            {
                var level = document.FindLevel(floor.LevelId)?.Elevation ?? 0;
                var bottom = floor.GetBottomElevation(document);
                made.Add(new ShaftOpening
                {
                    LevelId = floor.LevelId, Location = new Point2D(hole.At.X, hole.At.Y), Width = hole.Width, Depth = hole.Height,
                    Shape = hole.Round ? ShaftShape.Round : ShaftShape.Rectangle, BaseOffset = bottom - 10 - level,
                    UnconnectedHeight = floor.GetTopElevation(document) - bottom + 20, ForServices = true, Comments = $"For {name}"
                });
            }
        }

        return made;
    }

    private static bool Covers(BimDocument document, Element opening, Penetration hole) => opening switch
    {
        WallOpening cut when hole.Through is Wall wall && cut.HostWallId == wall.Id => Along(document, wall, hole.At) is var (along, height)
                                                                                      && Math.Abs(cut.DistanceAlongWall - along) <= cut.Width / 2 && height >= cut.SillHeight && height <= cut.SillHeight + cut.Height,
        ShaftOpening shaft when hole.Through is Floor => Polygon2D.Contains(shaft.Outline, new Point2D(hole.At.X, hole.At.Y)) && Math.Abs(shaft.BaseElevation(document) + shaft.UnconnectedHeight / 2 - hole.At.Z) < 200,
        _ => false
    };

    /// <summary>
    /// The largest nominal internal diameter of pipe that may pass through a compartment wall or floor
    /// with fire-stopping alone, mm - Approved Document B (Volume 1, Table 9.1): metal 160; PVC-U 160
    /// for a soil stack and 110 for a branch where drainage is boxed in as Diagram 9.1 has it, 40
    /// otherwise; any other material 40.
    /// </summary>
    public static double FireStoppedLimit(BimDocument document, Pipe pipe)
    {
        var material = document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper;
        return material switch
        {
            PipeMaterial.Copper or PipeMaterial.StainlessSteel or PipeMaterial.GalvanisedSteel => 160,
            PipeMaterial.PvcU when Drainage.IsDrainage(pipe.Kind) => pipe.Upright ? 160 : 110,
            _ => 40
        };
    }

    /// <summary>A pipe's nominal internal diameter, mm: its size's bore.</summary>
    private static double Bore(BimDocument document, Pipe pipe) =>
        document.FindType<PipeType>(pipe.TypeId) is { } type ? PipeSizes.Nearest(type.Material, pipe.Diameter).Inside : pipe.Diameter;

    /// <summary>
    /// What is wanting where services pass through walls and floors: no opening cut for them; and a pipe
    /// through a compartment wall or floor not kept fire-separating as Approved Document B has it - in
    /// the UK a proprietary seal, fire-stopping round a pipe no larger than Table 9.1 allows, or a sleeve
    /// of PVC-U, lead, aluminium or fibre-cement pipe up to 160 mm in metal pipe 1 m either side; in the
    /// US a listed firestop system (IRC R302.4.1.2).
    /// </summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        foreach (var hole in All(document))
        {
            if (hole.Opening is null)
                yield return new MepProblem(hole.Service, $"it passes through {Describe(document, hole.Through)} with no opening cut for it - Cut Openings makes one, its size and {Gap:0} mm round it for fire-stopping");
            else if (!hole.Fits)
                yield return new MepProblem(hole.Service, $"the opening where it passes through {Describe(document, hole.Through)} is too small for it - make it {hole.Width:0} x {hole.Height:0} mm, its size and {Gap:0} mm round it");
            if (!hole.Separating || hole.Service is not Pipe pipe) continue;

            var what = hole.Through is Floor ? "compartment floor" : "compartment wall";
            if (us)
            {
                if (pipe.Seal == PipeSeal.None)
                    yield return new MepProblem(pipe, $"it passes through the dwelling unit separation unsealed - IRC R302.4.1.2 protects a penetration with a listed firestop system (ASTM E814 or UL 1479)");
                continue;
            }

            var material = document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper;
            switch (pipe.Seal)
            {
                case PipeSeal.None:
                    yield return new MepProblem(pipe, $"it passes through a {what} with nothing keeping it fire-separating - Approved Document B (Volume 1, section 9) asks a proprietary seal, fire-stopping round a pipe no larger than Table 9.1 allows, or a sleeve");
                    break;
                case PipeSeal.FireStopped when Bore(document, pipe) > FireStoppedLimit(document, pipe) + 0.5:
                    yield return new MepProblem(pipe, $"a pipe of {Bore(document, pipe):0} mm bore fire-stopped through a {what} - Approved Document B (Table 9.1) fire-stops a pipe of {PipeType.Title(material)} up to {FireStoppedLimit(document, pipe):0} mm alone; a proprietary seal or a sleeve for it");
                    break;
                case PipeSeal.Sleeved when material != PipeMaterial.PvcU || pipe.Diameter > 160 + 0.5:
                    yield return new MepProblem(pipe, $"a pipe of {PipeType.Title(material)} sleeved through a {what} - Approved Document B (Volume 1, section 9) sleeves PVC-U, lead, aluminium or fibre-cement pipe up to 160 mm, in metal pipe 1 m either side");
                    break;
            }
        }
    }

    private static string Describe(BimDocument document, Element element) => element switch
    {
        Wall wall => $"the wall {document.GetWallType(wall)?.Name ?? string.Empty}".TrimEnd(),
        Floor floor => $"the floor {document.FindType<SlabType>(floor.TypeId)?.Name ?? string.Empty}".TrimEnd(),
        _ => "a wall"
    };
}
