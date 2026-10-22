using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// A pipe cut in two at a point along it - where a branch tees into it: the first part keeps its
/// identity, the second is a new pipe the same as it, so the branch's end meets an end of each.
/// </summary>
public sealed class SplitPipeCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Pipe _pipe;
    private readonly Point2D _end;
    private readonly double _endOffset;
    private readonly Point2D _at;
    private readonly double _atOffset;

    /// <param name="height">For an upright pipe - a stack - the height above its level it is cut at; a pipe along the storey is cut where it passes the point.</param>
    public SplitPipeCommand(BimDocument document, Pipe pipe, Point2D at, double? height = null)
    {
        _document = document;
        _pipe = pipe;
        (_end, _endOffset) = (pipe.End, pipe.EndOffset);
        var length = pipe.Start.DistanceTo(pipe.End);
        var rise = pipe.EndOffset - pipe.StartOffset;
        var t = pipe.Upright && height is { } z && Math.Abs(rise) > 1e-9
            ? Math.Clamp((z - pipe.StartOffset) / rise, 0, 1)
            : length < 1e-9 ? 0 : Math.Clamp(pipe.Start.DistanceTo(at) / length, 0, 1);
        _at = pipe.Start + (pipe.End - pipe.Start) * t;
        _atOffset = pipe.StartOffset + (pipe.EndOffset - pipe.StartOffset) * t;
        Second = new Pipe
        {
            TypeId = pipe.TypeId, LevelId = pipe.LevelId, Start = _at, End = _end, StartOffset = _atOffset, EndOffset = _endOffset,
            Diameter = pipe.Diameter, Kind = pipe.Kind, SystemId = pipe.SystemId, Return = pipe.Return, PhaseCreated = pipe.PhaseCreated, Workset = pipe.Workset
        };
    }

    /// <summary>The part from the cut to where the pipe ended.</summary>
    public Pipe Second { get; }

    /// <summary>Where it is cut, its height included.</summary>
    public Point3D At => Point3D.On(_at, (_document.FindLevel(_pipe.LevelId)?.Elevation ?? 0) + _atOffset);

    public string Name => "Split Pipe";

    public void Redo()
    {
        (_pipe.End, _pipe.EndOffset) = (_at, _atOffset);
        _document.Add(Second);
    }

    public void Undo()
    {
        _document.Remove(Second);
        (_pipe.End, _pipe.EndOffset) = (_end, _endOffset);
    }
}

/// <summary>Pipes given new sizes, as one step to undo: what Size Pipes does.</summary>
public sealed class SetPipeSizesCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Pipe Pipe, double Old, double New)> _changes;

    public SetPipeSizesCommand(IEnumerable<(Pipe Pipe, double Diameter)> sizes)
    {
        _changes = sizes.Select(entry => (entry.Pipe, entry.Pipe.Diameter, entry.Diameter)).ToList();
    }

    public int Count => _changes.Count;

    public string Name => "Size Pipes";

    public void Redo()
    {
        foreach (var (pipe, _, size) in _changes) pipe.Diameter = size;
    }

    public void Undo()
    {
        foreach (var (pipe, old, _) in _changes) pipe.Diameter = old;
    }
}

/// <summary>Pipes put on a system - a run joined to a fitting on one - as one step to undo.</summary>
public sealed class SetPipeSystemCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Pipe Pipe, Guid? Old)> _pipes;
    private readonly Guid? _system;

    public SetPipeSystemCommand(IEnumerable<Pipe> pipes, Guid? system)
    {
        _pipes = pipes.Select(pipe => (pipe, pipe.SystemId)).ToList();
        _system = system;
    }

    public string Name => "Pipe System";

    public void Redo()
    {
        foreach (var (pipe, _) in _pipes) pipe.SystemId = _system;
    }

    public void Undo()
    {
        foreach (var (pipe, old) in _pipes) pipe.SystemId = old;
    }
}
