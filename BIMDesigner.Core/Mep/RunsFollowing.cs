using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// The pipes and ducts that meet what is moving - a fitting's connector, or the end of a pipe or duct moved with it - and follow it.
/// The end that meets it goes where the connector or the end goes, the run stretching to it; an upright drop goes whole, keeping
/// upright, and what meets its other end follows that in turn. Each end keeps the little it stood off what it met, so moving there
/// and back puts it back where it was.
///
/// Worked out from where things stand when it is made: <see cref="Follow"/> then sets each end to follow wherever what it met now
/// is - while a drag goes on, after a move, and after the move is undone, the fitting back where it was.
/// </summary>
public sealed class RunsFollowing
{
    private readonly BimDocument _document;
    private readonly List<Follower> _followers;

    private sealed record Follower(Element Run, bool AtStart, Func<Point3D> Source, double Dx, double Dy, double Dz);

    private RunsFollowing(BimDocument document, List<Follower> followers) => (_document, _followers) = (document, followers);

    /// <summary>Whether nothing follows.</summary>
    public bool IsEmpty => _followers.Count == 0;

    /// <summary>The pipes and ducts that follow, each once.</summary>
    public IReadOnlyList<Element> Runs => _followers.Select(follower => follower.Run).Distinct().ToList();

    /// <summary>The pipes and ducts meeting what is moving, as they stand now.</summary>
    public static RunsFollowing For(BimDocument document, IEnumerable<Element> moving)
    {
        var set = moving.ToHashSet();
        var sources = new Queue<(Point3D At, ConnectorDomain Domain, Func<Point3D> Now)>();
        foreach (var fitting in set.OfType<Component>())
        {
            var placed = Connectors.Placed(document, fitting);
            for (var i = 0; i < placed.Count; i++)
            {
                if (placed[i].Connector.Domain == ConnectorDomain.Electrical) continue;
                var index = i;
                sources.Enqueue((placed[i].At, placed[i].Connector.Domain, () => Connectors.Placed(document, fitting)[index].At));
            }
        }

        foreach (var run in set.Where(element => element is Pipe or Duct))
        {
            var (start, end) = EndsOf(document, run);
            sources.Enqueue((start, DomainOf(run), () => EndsOf(document, run).Start));
            sources.Enqueue((end, DomainOf(run), () => EndsOf(document, run).End));
        }

        var followers = new List<Follower>();
        if (sources.Count == 0) return new RunsFollowing(document, followers);

        var runs = document.All<Pipe>().Cast<Element>().Concat(document.All<Duct>()).Where(run => !set.Contains(run))
            .Select(run => (Run: run, Ends: EndsOf(document, run), Touch: run is Pipe ? PipeNetworks.Touch : DuctNetworks.Touch)).ToList();
        var taken = new HashSet<(Element, bool)>();
        while (sources.Count > 0)
        {
            var source = sources.Dequeue();
            foreach (var (run, (start, end), touch) in runs)
            {
                if (DomainOf(run) != source.Domain) continue;
                foreach (var atStart in new[] { true, false })
                {
                    var at = atStart ? start : end;
                    if (Distance(at, source.At) > touch || !taken.Add((run, atStart))) continue;
                    followers.Add(Off(run, atStart, source.Now, at, source.At));

                    // An upright drop goes whole: its other end with the same source, and what meets that end follows it.
                    if (!Upright(run) || !taken.Add((run, !atStart))) continue;
                    var other = atStart ? end : start;
                    followers.Add(Off(run, !atStart, source.Now, other, source.At));
                    var otherAtStart = !atStart;
                    sources.Enqueue((other, source.Domain, () => otherAtStart ? EndsOf(document, run).Start : EndsOf(document, run).End));
                }
            }
        }

        return new RunsFollowing(document, followers);
    }

    /// <summary>Sets each following end to where what it met now is, off it as it stood.</summary>
    public void Follow()
    {
        foreach (var follower in _followers)
        {
            var source = follower.Source();
            Set(_document, follower.Run, follower.AtStart, new Point3D(source.X + follower.Dx, source.Y + follower.Dy, source.Z + follower.Dz));
        }
    }

    private static Follower Off(Element run, bool atStart, Func<Point3D> source, Point3D at, Point3D from) =>
        new(run, atStart, source, at.X - from.X, at.Y - from.Y, at.Z - from.Z);

    private static double Distance(Point3D a, Point3D b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static ConnectorDomain DomainOf(Element run) => run is Pipe ? ConnectorDomain.Pipe : ConnectorDomain.Duct;

    private static bool Upright(Element run) => run switch
    {
        Pipe pipe => pipe.Upright,
        Duct duct => duct.Upright,
        _ => false
    };

    private static (Point3D Start, Point3D End) EndsOf(BimDocument document, Element run) => run switch
    {
        Pipe pipe => pipe.Ends(document),
        Duct duct => duct.Ends(document),
        _ => default
    };

    private static void Set(BimDocument document, Element run, bool atStart, Point3D at)
    {
        var level = document.FindLevel(run.LevelId)?.Elevation ?? 0;
        var plan = new Point2D(at.X, at.Y);
        switch (run)
        {
            case Pipe pipe when atStart:
                (pipe.Start, pipe.StartOffset) = (plan, at.Z - level);
                break;
            case Pipe pipe:
                (pipe.End, pipe.EndOffset) = (plan, at.Z - level);
                break;
            case Duct duct when atStart:
                (duct.Start, duct.StartOffset) = (plan, at.Z - level);
                break;
            case Duct duct:
                (duct.End, duct.EndOffset) = (plan, at.Z - level);
                break;
        }
    }
}

/// <summary>
/// A move made already - a drag, its pieces then set against a wall - with the pipes and ducts that met them following, both when it is
/// done again and when it is undone: whatever its steps did, the runs end where the fittings end.
/// </summary>
public sealed class FollowingRunsCommand : IUndoableCommand
{
    private readonly IUndoableCommand _moved;
    private readonly RunsFollowing _runs;

    public FollowingRunsCommand(IUndoableCommand moved, RunsFollowing runs) => (_moved, _runs) = (moved, runs);

    public string Name => _moved.Name;

    public void Redo()
    {
        _moved.Redo();
        _runs.Follow();
    }

    public void Undo()
    {
        _moved.Undo();
        _runs.Follow();
    }
}
