using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The 3D view as the building stands on a date of its construction programme (4D): what is
/// built shown as it is, what is going up green and faint, what is coming down red, temporary
/// works yellow, what has not started - or has gone - not shown at all; what starts within the
/// look-ahead faint orange; and, by what has actually happened, what is late orange.
/// </summary>
public partial class ModelView
{
    public static readonly ColourRgb BuildingColour = new(0x2E, 0xA0, 0x43);
    public static readonly ColourRgb DemolishingColour = new(0xC0, 0x39, 0x2B);
    public static readonly ColourRgb TemporaryColour = new(0xE6, 0xB8, 0x00);
    public static readonly ColourRgb LookAheadColour = new(0xF0, 0x8A, 0x24);
    public static readonly ColourRgb LateColour = new(0xE0, 0x60, 0x10);

    private DateOnly? _constructionDate;
    private bool _constructionActual;
    private int _lookAheadDays;
    private Dictionary<Guid, (BuildState State, ConstructionTask Task)>? _states;
    private HashSet<Guid> _comingUp = new();
    private HashSet<Guid> _late = new();

    /// <summary>The date the model is shown as built to; none, all of it.</summary>
    public DateOnly? ConstructionDate
    {
        get => _constructionDate;
        set
        {
            if (_constructionDate == value) return;
            _constructionDate = value;
            Rebuild();
        }
    }

    /// <summary>Whether the date shows what has actually happened rather than the plan.</summary>
    public bool ConstructionActual
    {
        get => _constructionActual;
        set
        {
            if (_constructionActual == value) return;
            _constructionActual = value;
            if (_constructionDate is not null) Rebuild();
        }
    }

    /// <summary>How many days ahead of the date what is about to start is shown, faint; none, not shown.</summary>
    public int LookAheadDays
    {
        get => _lookAheadDays;
        set
        {
            if (_lookAheadDays == value) return;
            _lookAheadDays = Math.Max(0, value);
            if (_constructionDate is not null) Rebuild();
        }
    }

    /// <summary>Works out where everything is on the date shown, before the model is built for it.</summary>
    private void PrepareConstruction()
    {
        (_states, _comingUp, _late) = (null, new HashSet<Guid>(), new HashSet<Guid>());
        if (_document is null || _constructionDate is not { } date || _document.Construction.Tasks.Count == 0) return;

        _states = ConstructionPlanning.StateOn(_document, date, _constructionActual);
        var times = ConstructionPlanning.Time(_document.Construction).ToDictionary(timed => timed.Task.Id);
        foreach (var (id, (state, task)) in _states)
        {
            if (state == BuildState.NotStarted && _lookAheadDays > 0 && times.TryGetValue(task.Id, out var timed) &&
                (task.BaselineStart ?? timed.Start) <= date.AddDays(_lookAheadDays))
                _comingUp.Add(id);

            // Going by what has happened: due finished by the plan, not finished yet.
            if (_constructionActual && state is BuildState.NotStarted or BuildState.InProgress && task.ActualFinish is null &&
                times.TryGetValue(task.Id, out var planned) && (task.BaselineFinish ?? planned.Finish) < date)
                _late.Add(id);
        }
    }

    /// <summary>Whether an element stands on the date shown.</summary>
    private bool StandsOn(Element element)
    {
        if (_states is null || !_states.TryGetValue(element.Id, out var entry)) return true;
        return entry.State switch
        {
            BuildState.NotStarted => _comingUp.Contains(element.Id) || _late.Contains(element.Id),
            BuildState.Demolished or BuildState.TemporaryGone => false,
            _ => true
        };
    }

    /// <summary>How an element is drawn on the date shown.</summary>
    private ElementStyle ConstructionStyle(Element element)
    {
        if (_states is null || !_states.TryGetValue(element.Id, out var entry)) return default;
        if (_late.Contains(element.Id)) return new ElementStyle(Colour: LateColour, Halftone: entry.State == BuildState.NotStarted);
        return entry.State switch
        {
            BuildState.InProgress => new ElementStyle(Halftone: true, Colour: BuildingColour),
            BuildState.Demolishing => new ElementStyle(Colour: DemolishingColour, Dashed: true),
            BuildState.Temporary => new ElementStyle(Colour: TemporaryColour),
            BuildState.NotStarted when _comingUp.Contains(element.Id) => new ElementStyle(Colour: LookAheadColour, Dashed: true),
            _ => default
        };
    }
}
