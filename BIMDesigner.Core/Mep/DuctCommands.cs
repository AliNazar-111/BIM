using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>A duct cut in two where a branch tees into it, as a pipe is: the first part keeps its identity, the second is a new duct the same as it.</summary>
public sealed class SplitDuctCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly Duct _duct;
    private readonly Point2D _end;
    private readonly double _endOffset;
    private readonly Point2D _at;
    private readonly double _atOffset;

    public SplitDuctCommand(BimDocument document, Duct duct, Point2D at, double? height = null)
    {
        _document = document;
        _duct = duct;
        (_end, _endOffset) = (duct.End, duct.EndOffset);
        var length = duct.Start.DistanceTo(duct.End);
        var rise = duct.EndOffset - duct.StartOffset;
        var t = duct.Upright && height is { } z && Math.Abs(rise) > 1e-9
            ? Math.Clamp((z - duct.StartOffset) / rise, 0, 1)
            : length < 1e-9 ? 0 : Math.Clamp(duct.Start.DistanceTo(at) / length, 0, 1);
        _at = duct.Start + (duct.End - duct.Start) * t;
        _atOffset = duct.StartOffset + (duct.EndOffset - duct.StartOffset) * t;
        Second = new Duct
        {
            TypeId = duct.TypeId, LevelId = duct.LevelId, Start = _at, End = _end, StartOffset = _atOffset, EndOffset = _endOffset,
            Width = duct.Width, Height = duct.Height, Kind = duct.Kind, SystemId = duct.SystemId, FireProtection = duct.FireProtection,
            DamperRating = duct.DamperRating, Insulation = duct.Insulation,
            PhaseCreated = duct.PhaseCreated, Workset = duct.Workset
        };
    }

    public Duct Second { get; }

    public Point3D At => Point3D.On(_at, (_document.FindLevel(_duct.LevelId)?.Elevation ?? 0) + _atOffset);

    public string Name => "Split Duct";

    public void Redo()
    {
        (_duct.End, _duct.EndOffset) = (_at, _atOffset);
        _document.Add(Second);
    }

    public void Undo()
    {
        _document.Remove(Second);
        (_duct.End, _duct.EndOffset) = (_end, _endOffset);
    }
}

/// <summary>Ducts given new sizes - Size Ducts - as one step to undo.</summary>
public sealed class SetDuctSizesCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Duct Duct, double Width, double Height, double NewWidth, double NewHeight)> _changes;

    public SetDuctSizesCommand(IEnumerable<(Duct Duct, DuctSize Size)> sizes)
    {
        _changes = sizes.Select(entry => (entry.Duct, entry.Duct.Width, entry.Duct.Height, entry.Size.Width, entry.Size.Height)).ToList();
    }

    public int Count => _changes.Count;

    public string Name => "Size Ducts";

    public void Redo()
    {
        foreach (var (duct, _, _, width, height) in _changes) (duct.Width, duct.Height) = (width, height);
    }

    public void Undo()
    {
        foreach (var (duct, width, height, _, _) in _changes) (duct.Width, duct.Height) = (width, height);
    }
}

/// <summary>Ducts put on a system - a run joined to a fitting on one - as one step to undo.</summary>
public sealed class SetDuctSystemCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(Duct Duct, Guid? Old)> _ducts;
    private readonly Guid? _system;

    public SetDuctSystemCommand(IEnumerable<Duct> ducts, Guid? system)
    {
        _ducts = ducts.Select(duct => (duct, duct.SystemId)).ToList();
        _system = system;
    }

    public string Name => "Set Duct System";

    public void Redo()
    {
        foreach (var (duct, _) in _ducts) duct.SystemId = _system;
    }

    public void Undo()
    {
        foreach (var (duct, old) in _ducts) duct.SystemId = old;
    }
}
