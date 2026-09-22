using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>
/// What each view leaves out (specification section 6.2, visibility and filters).
///
/// Settings belong to a view, not to the project: hiding interior partitions on the ground
/// floor plan to study the envelope does not hide them in the section or in 3D. A plan placed
/// on a sheet is the same view, so it shows the same thing there as on screen.
///
/// Only views that differ from the default are stored. A view nobody has filtered has no
/// entry, and shows everything.
/// </summary>
public sealed class ViewSettings
{
    private readonly Dictionary<ViewReference, HashSet<WallFunction>> _hiddenWallFunctions = new();
    private readonly Dictionary<ViewReference, double> _scales = new();

    /// <summary>The scale a view is drawn at when nobody has chosen one.</summary>
    public const double DefaultScale = 100;

    /// <summary>
    /// The scale a view is meant to be drawn at, as the denominator of 1:n. It is what the view
    /// is placed on a sheet at, when it fits.
    /// </summary>
    public double ScaleOf(ViewReference view) => _scales.TryGetValue(view, out var scale) ? scale : DefaultScale;

    public void SetScale(ViewReference view, double denominator)
    {
        if (!double.IsFinite(denominator) || denominator <= 0 || denominator == DefaultScale) _scales.Remove(view);
        else _scales[view] = denominator;
    }

    /// <summary>Every view with a scale of its own, for saving.</summary>
    public IEnumerable<(ViewReference View, double Scale)> Scaled => _scales.Select(entry => (entry.Key, entry.Value));

    /// <summary>The wall functions this view does not draw.</summary>
    public IReadOnlySet<WallFunction> HiddenWallFunctions(ViewReference view) =>
        _hiddenWallFunctions.TryGetValue(view, out var hidden) ? hidden : new HashSet<WallFunction>();

    public bool IsWallFunctionVisible(ViewReference view, WallFunction function) =>
        !HiddenWallFunctions(view).Contains(function);

    public void SetWallFunctionVisible(ViewReference view, WallFunction function, bool visible)
    {
        if (!_hiddenWallFunctions.TryGetValue(view, out var hidden))
        {
            if (visible) return;
            _hiddenWallFunctions[view] = hidden = new HashSet<WallFunction>();
        }

        if (visible) hidden.Remove(function);
        else hidden.Add(function);

        if (hidden.Count == 0) _hiddenWallFunctions.Remove(view);
    }

    /// <summary>Every view with something hidden, for saving.</summary>
    public IEnumerable<(ViewReference View, IReadOnlySet<WallFunction> Hidden)> Filtered =>
        _hiddenWallFunctions.Select(entry => (entry.Key, (IReadOnlySet<WallFunction>)entry.Value));

    /// <summary>
    /// The test a view applies to each element: whether it is drawn at all.
    ///
    /// A hidden wall takes what it hosts with it - a door floating where a wall has been
    /// filtered out is not a door anyone can read - and a tag goes with what it labels.
    /// </summary>
    public Func<Element, bool> FilterFor(BimDocument document, ViewReference view)
    {
        var hidden = HiddenWallFunctions(view);
        if (hidden.Count == 0) return _ => true;

        var hiddenWalls = document.Walls
            .Where(wall => document.GetWallType(wall) is { } type && hidden.Contains(type.Function))
            .Select(wall => wall.Id)
            .ToHashSet();

        var hiddenHosted = document.Elements
            .OfType<Opening>()
            .Where(opening => hiddenWalls.Contains(opening.HostWallId))
            .Select(opening => opening.Id)
            .ToHashSet();

        return element => element switch
        {
            Wall wall => !hiddenWalls.Contains(wall.Id),
            Opening opening => !hiddenHosted.Contains(opening.Id),
            IHostedElement hosted => !hiddenWalls.Contains(hosted.HostId) && !hiddenHosted.Contains(hosted.HostId),
            _ => true
        };
    }
}

/// <summary>Sets the scale a view is drawn at.</summary>
public sealed class SetViewScaleCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly double _old;
    private readonly double _new;

    public SetViewScaleCommand(BimDocument document, ViewReference view, double denominator)
    {
        _document = document;
        _view = view;
        _old = document.ViewSettings.ScaleOf(view);
        _new = denominator;
    }

    public string Name => "View Scale";

    public void Redo() => _document.ViewSettings.SetScale(_view, _new);

    public void Undo() => _document.ViewSettings.SetScale(_view, _old);
}

/// <summary>Shows or hides walls of one function in one view.</summary>
public sealed class SetWallFunctionVisibilityCommand : IUndoableCommand
{
    private readonly BimDocument _document;
    private readonly ViewReference _view;
    private readonly WallFunction _function;
    private readonly bool _visible;
    private readonly bool _wasVisible;

    public SetWallFunctionVisibilityCommand(
        BimDocument document, ViewReference view, WallFunction function, bool visible)
    {
        _document = document;
        _view = view;
        _function = function;
        _visible = visible;
        _wasVisible = document.ViewSettings.IsWallFunctionVisible(view, function);
    }

    public string Name => _visible ? $"Show {EnumText.Humanise(_function)} Walls" : $"Hide {EnumText.Humanise(_function)} Walls";

    public void Redo() => _document.ViewSettings.SetWallFunctionVisible(_view, _function, _visible);

    public void Undo() => _document.ViewSettings.SetWallFunctionVisible(_view, _function, _wasVisible);
}
