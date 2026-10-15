namespace BIMDesigner.UI.Rendering;

/// <summary>
/// What has been worked out of the model while it has not changed - a downpipe's run down from
/// its gutter, whether a stair meets its rules, a floor as it is built - kept between drawings,
/// and shared by the drawings that draw the same model: a plan and the storey under it. Emptied
/// by whoever keeps it whenever the model may have changed; a drawing given none works it all
/// out each time it is drawn.
/// </summary>
public sealed class ModelMemo
{
    private readonly Dictionary<string, object?> _known = new();

    /// <summary>What is kept under a name, worked out the first time it is asked for.</summary>
    public T Get<T>(string name, Func<T> work)
    {
        if (_known.TryGetValue(name, out var known)) return (T)known!;
        var made = work();
        _known[name] = made;
        return made;
    }

    /// <summary>Everything forgotten: the model may have changed.</summary>
    public void Clear() => _known.Clear();

    /// <summary>What is kept of some elements forgotten - those being dragged - the rest kept as it was.</summary>
    public void Forget(IEnumerable<Guid> ids)
    {
        var names = ids.Select(id => id.ToString()).ToList();
        foreach (var name in _known.Keys.Where(key => names.Any(key.Contains)).ToList()) _known.Remove(name);
    }
}
