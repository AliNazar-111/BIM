using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>One length of a drain along its long section: where it starts and ends along the run, its inverts there, its bore and its fall.</summary>
public sealed record SectionPipe(Pipe Pipe, double From, double To, double UpperInvert, double LowerInvert, double Bore, double Fall);

/// <summary>An access point along a long section: how far along, its cover and the invert under it.</summary>
public sealed record SectionAccess(Component Access, string Name, double At, double Cover, double Invert);

/// <summary>A drain run's long section, from its head down to its outfall.</summary>
public sealed record DrainSection(MepSystem System, IReadOnlyList<SectionPipe> Pipes, IReadOnlyList<SectionAccess> Accesses, double Ground)
{
    public double Length => Pipes.Count == 0 ? 0 : Pipes[^1].To;
}

/// <summary>
/// A drain's long section, as a drainage engineer draws one: the run from its head to its outfall
/// laid out flat along its length - the ground over it, the pipe's invert falling along it, its size
/// and gradient length by length, and each access point with its cover level, invert and depth.
/// </summary>
public static class DrainSections
{
    /// <summary>The long section through a drain: the longest run through it, from as far up as it goes to its outfall. Null for a pipe that is not a traced drain.</summary>
    public static DrainSection? Through(BimDocument document, Pipe pipe)
    {
        if (!Drainage.IsDrainage(pipe.Kind) || document.Find<MepSystem>(pipe.SystemId) is not { } system) return null;
        var network = PipeNetworks.Trace(document, system);
        if (!network.Loads.ContainsKey(pipe)) return null;
        var drains = network.Loads.Where(entry => Drainage.BelowGround(document, entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
        if (!drains.ContainsKey(pipe)) return null;

        // Up from the pipe, by whichever drain coming in reaches furthest up; then down to the outfall.
        static bool Same(Point3D a, Point3D b) => Math.Abs(a.X - b.X) <= PipeNetworks.Touch && Math.Abs(a.Y - b.Y) <= PipeNetworks.Touch && Math.Abs(a.Z - b.Z) <= PipeNetworks.Touch;
        var head = pipe;
        while (drains.Where(entry => Same(entry.Value.Near, drains[head].Far)).OrderByDescending(entry => entry.Value.Downstream).Select(entry => entry.Key).FirstOrDefault() is { } further)
            head = further;

        var run = new List<SectionPipe>();
        var along = 0.0;
        for (Pipe? each = head; each is not null && drains.TryGetValue(each, out var load); each = load.Toward)
        {
            var bore = Drainage.Bore(document, each);
            var plan = new Point2D(load.Far.X, load.Far.Y).DistanceTo(new Point2D(load.Near.X, load.Near.Y));
            run.Add(new SectionPipe(each, along, along + plan, load.Far.Z - bore / 2, load.Near.Z - bore / 2, bore, Drainage.Fall(load)));
            along += plan;
        }

        // The access points along it, by where they stand over it.
        var accesses = new List<SectionAccess>();
        foreach (var access in document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form is
                     ComponentForm.DrainAccess or ComponentForm.FoulOutfall or ComponentForm.SurfaceWaterOutfall or ComponentForm.Soakaway))
        {
            foreach (var length in run)
            {
                var load = drains[length.Pipe];
                var (upper, lower) = (new Point2D(load.Far.X, load.Far.Y), new Point2D(load.Near.X, load.Near.Y));
                var closest = Line2D.ClosestPointOnSegment(access.Location, upper, lower);
                if (closest.DistanceTo(access.Location) > PipeNetworks.Reach) continue;
                var t = upper.DistanceTo(lower) < 1 ? 0 : upper.DistanceTo(closest) / upper.DistanceTo(lower);
                var type = document.FindType<ComponentType>(access.TypeId)!;
                accesses.Add(new SectionAccess(access, string.IsNullOrEmpty(access.Mark) ? type.Name : access.Mark, length.From + (length.To - length.From) * t,
                    ComponentHosting.BaseElevation(document, access, type), length.UpperInvert + (length.LowerInvert - length.UpperInvert) * t));
                break;
            }
        }

        var ground = document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);
        return new DrainSection(system, run, accesses.OrderBy(access => access.At).ToList(), ground);
    }
}
