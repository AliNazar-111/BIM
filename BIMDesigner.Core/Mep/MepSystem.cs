using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// A system of the building's services (specification section 5.4): the fittings joined into
/// one network, and the equipment it runs from - a cold water system from the stop valve to the
/// taps, a heating zone from the boiler to its radiators and its thermostat, a circuit from the
/// consumer unit to its sockets. It has no shape of its own; its fittings are its members, each
/// joined by the connector it has of the system's kind.
/// </summary>
public sealed class MepSystem : Element
{
    public override BuiltInCategory Category => Kind.Category();

    public SystemKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The equipment feeding it - a boiler, a cylinder, a stop valve, a consumer unit - or none yet.</summary>
    public Guid? SourceId { get; set; }

    /// <summary>The fittings on it, in the order they were added.</summary>
    public List<Guid> Members { get; } = new();

    /// <summary>For a circuit, what it is for: a ring of sockets, the lights, the cooker.</summary>
    public CircuitUse Use { get; set; } = CircuitUse.Other;

    /// <summary>For a heating zone, the water's temperature leaving its heat source and coming back, °C - none set at 0, its source's usual taken.</summary>
    public double FlowTemperature { get; set; }

    public double ReturnTemperature { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(MepSystemParameters.SystemName, () => Name, v => { if (!string.IsNullOrWhiteSpace(v)) Name = v.Trim(); });
        yield return ParameterValue.ReadOnly(MepSystemParameters.SystemType, () => Kind.Title());
        yield return ParameterValue.ReadOnly(MepSystemParameters.Discipline, () => Kind.Discipline().Title());
        if (Kind == SystemKind.Power)
            yield return ParameterValue.BindChoice(MepSystemParameters.Use, () => Use.Title(),
                v => { foreach (var use in Enum.GetValues<CircuitUse>()) if (use.Title() == v) Use = use; },
                Enum.GetValues<CircuitUse>().Select(use => use.Title()).ToArray());
        if (Kind == SystemKind.Heating)
        {
            yield return ParameterValue.BindValidated(MepSystemParameters.FlowTemperature, () => Heating.Design(document, this).Flow, v =>
            {
                if (v is < 25 or > 95 || v <= Heating.Design(document, this).Return) return false;
                FlowTemperature = v;
                return true;
            });
            yield return ParameterValue.BindValidated(MepSystemParameters.ReturnTemperature, () => Heating.Design(document, this).Return, v =>
            {
                if (v is < 15 or > 90 || v >= Heating.Design(document, this).Flow) return false;
                ReturnTemperature = v;
                return true;
            });
            yield return ParameterValue.ReadOnly(MepSystemParameters.HeatLoad, () => Heating.Describe(document, this));
        }
        yield return ParameterValue.ReadOnly(MepSystemParameters.Source, () => MepSystems.Describe(document, MepSystems.SourceOf(document, this)) ?? "<none>");
        yield return ParameterValue.ReadOnly(MepSystemParameters.Fittings, () => (double)MepSystems.MembersOf(document, this).Count);
        yield return ParameterValue.ReadOnly(MepSystemParameters.RoomsServed, () => string.Join(", ", MepSystems.RoomsServed(document, this)));
        yield return ParameterValue.ReadOnly(MepSystemParameters.Problems, () =>
            MepSystems.Problems(document).Where(problem => problem.Element == this).Select(problem => problem.Text).ToList() is { Count: > 0 } said
                ? string.Join("; ", said)
                : "None");

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>What it is, all of it - for a change to be undone by putting it back.</summary>
    public sealed record State(string Name, SystemKind Kind, Guid? SourceId, IReadOnlyList<Guid> Members, CircuitUse Use, double Flow = 0, double Return = 0);

    public State Capture() => new(Name, Kind, SourceId, Members.ToList(), Use, FlowTemperature, ReturnTemperature);

    public void Restore(State state)
    {
        (Name, Kind, SourceId, Use, FlowTemperature, ReturnTemperature) = (state.Name, state.Kind, state.SourceId, state.Use, state.Flow, state.Return);
        Members.Clear();
        Members.AddRange(state.Members);
    }
}

public static class MepSystemParameters
{
    public static readonly ParameterDefinition SystemName = new("System Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition SystemType = new("System Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Discipline = new("Discipline", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Use = new("Circuit Use", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Source = new("Source", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Fittings = new("Fittings", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition RoomsServed = new("Rooms Served", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Problems = new("Wanting", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition FlowTemperature = new("Flow Temperature (°C)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ReturnTemperature = new("Return Temperature (°C)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition HeatLoad = new("Heat Load", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    /// <summary>On a radiator or an underfloor loop: what it gives at its zone's temperatures.</summary>
    public static readonly ParameterDefinition DesignOutput = new("Output at Design", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    /// <summary>On a fitting: the systems it is on, and what it is not connected to yet.</summary>
    public static readonly ParameterDefinition Systems = new("Systems", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition NotConnected = new("Not Connected", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition HotWaterRun = new("Hot Water Run", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
}

/// <summary>
/// A change to a system - its fittings, its source, its name - as one step to undo: what it was
/// before is put back whole.
/// </summary>
public sealed class EditSystemCommand : IUndoableCommand
{
    private readonly MepSystem _system;
    private readonly MepSystem.State _before;
    private readonly MepSystem.State _after;

    public EditSystemCommand(MepSystem system, string name, Action<MepSystem> change)
    {
        _system = system;
        Name = name;
        _before = system.Capture();
        change(system);
        _after = system.Capture();
        system.Restore(_before);
    }

    public string Name { get; }

    /// <summary>Whether it changes anything.</summary>
    public bool Changes => _before.Name != _after.Name || _before.Kind != _after.Kind || _before.SourceId != _after.SourceId
                           || _before.Use != _after.Use || _before.Flow != _after.Flow || _before.Return != _after.Return
                           || !_before.Members.SequenceEqual(_after.Members);

    public void Redo() => _system.Restore(_after);

    public void Undo() => _system.Restore(_before);
}
