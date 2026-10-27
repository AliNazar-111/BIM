using BIMDesigner.Core.Architecture;
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

    /// <summary>For a circuit, its breaker's rating, A - none set at 0, the usual for its use taken, as Size Circuits chooses.</summary>
    public double Breaker { get; set; }

    /// <summary>For a circuit, its breaker's tripping curve: type B for a home's circuits, C or D for motors and the like.</summary>
    public BreakerCurve Curve { get; set; } = BreakerCurve.B;

    /// <summary>For a circuit, whether a 30 mA RCD protects it - an RCBO, or an RCD over it in the board; in the US, a GFCI.</summary>
    public bool Rcd { get; set; } = true;

    /// <summary>For a circuit, whether an arc fault detection device protects it; in the US, an AFCI.</summary>
    public bool ArcFault { get; set; }

    /// <summary>For a circuit, its cable's size - "2.5 mm²", "12 AWG" - or empty, the smallest that does chosen.</summary>
    public string Cable { get; set; } = string.Empty;

    /// <summary>For a circuit, how its cable is installed, as BS 7671 rates it.</summary>
    public CableMethod Method { get; set; } = CableMethod.ClippedDirect;

    /// <summary>For a circuit, how many circuits' cables are bunched where it runs, its own counted.</summary>
    public int Grouped { get; set; } = 1;

    /// <summary>For a circuit, its cable's length, m - none set at 0, measured along its run from the board.</summary>
    public double CableLength { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(MepSystemParameters.SystemName, () => Name, v => { if (!string.IsNullOrWhiteSpace(v)) Name = v.Trim(); });
        yield return ParameterValue.ReadOnly(MepSystemParameters.SystemType, () => Kind.Title());
        yield return ParameterValue.ReadOnly(MepSystemParameters.Discipline, () => Kind.Discipline().Title());
        if (Kind == SystemKind.Power)
        {
            var us = document.ProjectInformation.ClearanceStandard.IsUs();
            var uses = Enum.GetValues<CircuitUse>().Where(use => !us || use != CircuitUse.RingFinal).ToList();
            yield return ParameterValue.BindChoice(MepSystemParameters.Use, () => Use.Title(us),
                v => { foreach (var use in uses) if (use.Title(us) == v) Use = use; },
                uses.Select(use => use.Title(us)).ToArray());

            // Its breaker, protection and cable: as set, or as Size Circuits would choose them - worked out again once any of them is changed.
            CircuitDesign? design = null;
            (CircuitUse, double, BreakerCurve, bool, bool, string, CableMethod, int, double)? designedFor = null;
            CircuitDesign? Designed()
            {
                var now = (Use, Breaker, Curve, Rcd, ArcFault, Cable, Method, Grouped, CableLength);
                if (designedFor != now) (design, designedFor) = (Circuits.Design(document, this), now);
                return design;
            }

            yield return ParameterValue.BindValidated(MepSystemParameters.Breaker, () => Designed()?.Breaker ?? Breaker, v =>
            {
                if (v != 0 && !Circuits.Ratings(us).Contains(v)) return false;
                Breaker = v;
                return true;
            });
            if (!us)
                yield return ParameterValue.BindChoice(MepSystemParameters.Curve, () => $"Type {Curve}",
                    v => { if (Enum.TryParse<BreakerCurve>(v.Replace("Type ", string.Empty), out var curve)) Curve = curve; },
                    Enum.GetValues<BreakerCurve>().Select(curve => $"Type {curve}").ToArray());
            yield return ParameterValue.Bind(us ? MepSystemParameters.Gfci : MepSystemParameters.Rcd, () => Rcd, v => Rcd = v);
            yield return ParameterValue.Bind(us ? MepSystemParameters.Afci : MepSystemParameters.Afdd, () => ArcFault, v => ArcFault = v);
            yield return ParameterValue.BindChoice(MepSystemParameters.Cable, () => Designed()?.Cable.Name ?? Cable,
                v => { if (Circuits.Sizes(us).Any(size => size.Name == v)) Cable = v; },
                Circuits.Sizes(us).Select(size => size.Name).ToArray());
            if (!us)
                yield return ParameterValue.BindChoice(MepSystemParameters.Method, () => Method.Title(),
                    v => { foreach (var method in Enum.GetValues<CableMethod>()) if (method.Title() == v) Method = method; },
                    Enum.GetValues<CableMethod>().Select(method => method.Title()).ToArray());
            yield return ParameterValue.BindValidated(MepSystemParameters.Grouped, () => (double)Grouped, v =>
            {
                if (v is < 1 or > 20) return false;
                Grouped = (int)Math.Round(v);
                return true;
            });
            // Kept in metres; given in feet in feet and inches.
            yield return ParameterValue.BindScaled(MepSystemParameters.CableLength, MepSystemParameters.CableLengthFeet, 1000 / Units.MillimetresPerFoot, 0,
                () => Math.Round(Designed()?.Length ?? CableLength, 1), v =>
            {
                if (v < 0) return false;
                CableLength = Designed() is { Measured: true } measured && Math.Abs(v - measured.Length) < (Units.Imperial ? 0.16 : 0.05) ? 0 : v;
                return true;
            });
            yield return ParameterValue.ReadOnly(MepSystemParameters.DesignCurrent, () => Math.Round(Designed()?.Current ?? 0, 1));
            yield return ParameterValue.ReadOnly(MepSystemParameters.Capacity, () => Math.Round(Designed()?.Capacity ?? 0, 1));
            yield return ParameterValue.ReadOnly(MepSystemParameters.VoltageDrop, () => Designed() is { } now ? Math.Round(now.Drop / now.Voltage * 100, 2) : 0);
            yield return ParameterValue.ReadOnly(MepSystemParameters.Design, () => Circuits.Describe(document, this));
        }
        if (Kind == SystemKind.Heating)
        {
            // Kept in °C; given in °F in feet and inches.
            var fahrenheit = Units.Imperial;
            double Shown(double celsius) => fahrenheit ? Math.Round(celsius * 1.8 + 32) : celsius;
            double Kept(double shown) => fahrenheit ? (shown - 32) / 1.8 : shown;
            yield return ParameterValue.BindValidated(fahrenheit ? MepSystemParameters.FlowTemperatureF : MepSystemParameters.FlowTemperature, () => Shown(Heating.Design(document, this).Flow), (double shown) =>
            {
                var v = Kept(shown);
                if (v is < 25 - 0.3 or > 95 + 0.3 || v <= Heating.Design(document, this).Return) return false;
                FlowTemperature = v;
                return true;
            });
            yield return ParameterValue.BindValidated(fahrenheit ? MepSystemParameters.ReturnTemperatureF : MepSystemParameters.ReturnTemperature, () => Shown(Heating.Design(document, this).Return), (double shown) =>
            {
                var v = Kept(shown);
                if (v is < 15 - 0.3 or > 90 + 0.3 || v >= Heating.Design(document, this).Flow) return false;
                ReturnTemperature = v;
                return true;
            });
            yield return ParameterValue.ReadOnly(MepSystemParameters.HeatLoad, () => Heating.Describe(document, this));
        }
        yield return ParameterValue.ReadOnly(MepSystemParameters.Source, () => MepSystems.Describe(document, MepSystems.SourceOf(document, this)) ?? "<none>");
        yield return ParameterValue.ReadOnly(MepSystemParameters.Fittings, () => (double)MepSystems.MembersOf(document, this).Count);
        yield return ParameterValue.ReadOnly(MepSystemParameters.RoomsServed, () => string.Join(", ", MepSystems.RoomsServed(document, this)));
        yield return ParameterValue.ReadOnly(MepSystemParameters.Problems, () =>
            MepSystems.ProblemsOf(document, this).Select(problem => problem.Text).ToList() is { Count: > 0 } said
                ? string.Join("; ", said)
                : "None");

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>What it is, all of it - for a change to be undone by putting it back.</summary>
    public sealed record State(string Name, SystemKind Kind, Guid? SourceId, IReadOnlyList<Guid> Members, CircuitUse Use, double Flow = 0, double Return = 0)
    {
        public CircuitSettings Circuit { get; init; } = new();
    }

    /// <summary>A circuit's breaker, protection and cable, all of them.</summary>
    public sealed record CircuitSettings(double Breaker = 0, BreakerCurve Curve = BreakerCurve.B, bool Rcd = true, bool ArcFault = false, string Cable = "",
        CableMethod Method = CableMethod.ClippedDirect, int Grouped = 1, double CableLength = 0);

    public State Capture() => new(Name, Kind, SourceId, Members.ToList(), Use, FlowTemperature, ReturnTemperature)
    {
        Circuit = new CircuitSettings(Breaker, Curve, Rcd, ArcFault, Cable, Method, Grouped, CableLength)
    };

    public void Restore(State state)
    {
        (Name, Kind, SourceId, Use, FlowTemperature, ReturnTemperature) = (state.Name, state.Kind, state.SourceId, state.Use, state.Flow, state.Return);
        (Breaker, Curve, Rcd, ArcFault, Cable, Method, Grouped, CableLength) =
            (state.Circuit.Breaker, state.Circuit.Curve, state.Circuit.Rcd, state.Circuit.ArcFault, state.Circuit.Cable, state.Circuit.Method, state.Circuit.Grouped, state.Circuit.CableLength);
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
    public static readonly ParameterDefinition FlowTemperatureF = new("Flow Temperature (°F)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ReturnTemperatureF = new("Return Temperature (°F)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition HeatLoad = new("Heat Load", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    // A circuit's breaker, protection and cable, and what they come to.
    public static readonly ParameterDefinition Breaker = new("Breaker (A)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Curve = new("Breaker Curve", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Rcd = new("RCD (30 mA)", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Gfci = new("GFCI", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Afdd = new("AFDD", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Afci = new("AFCI", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Cable = new("Cable", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Method = new("Installation Method", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Grouped = new("Grouped With (circuits)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition CableLength = new("Cable Length (m)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition CableLengthFeet = new("Cable Length (ft)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Design = new("Circuit Design", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition DesignCurrent = new("Design Current (A)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition Capacity = new("Cable Carries (A)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition VoltageDrop = new("Voltage Drop (%)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Analytical);

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
                           || _before.Circuit != _after.Circuit || !_before.Members.SequenceEqual(_after.Members);

    public void Redo() => _system.Restore(_after);

    public void Undo() => _system.Restore(_before);
}
