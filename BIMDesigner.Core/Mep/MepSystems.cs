using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Mep;

/// <summary>Something wanting in the services: a fitting not connected, a system with nothing feeding it - and the rule that asks for it.</summary>
public sealed record MepProblem(Element Element, string Text);

/// <summary>
/// The building's services as systems: which fittings are on which, what feeds each, how a
/// new one is made of the fittings picked - and what is wanting.
///
/// A fitting is on a system by the connector it has of the system's kind - a basin on a cold
/// water system by its cold supply - and on one system of a kind at most, as IFC4 has it (an
/// element is assigned to only one IfcDistributionSystem of a PredefinedType). Equipment with a
/// connector that feeds the kind - a boiler's flow, a cylinder's hot water, the stop valve, the
/// consumer unit - is a system's source rather than a member of it.
///
/// What is checked against the rules: Approved Document G asks for cold wholesome water (G1) and
/// heated wholesome water (G3) at a washbasin, bath, shower and kitchen sink; Approved Document H
/// (H1) for the foul water of appliances carried to an outfall; BS 5839-6, which Approved Document
/// B (1.1) follows, for smoke and heat alarms powered from the mains and linked so that one
/// sounding sets off the rest - the IRC's R314.6 and R314.4 in the US; and Approved Document L
/// (2021) for heating zones, each with its own time and temperature control, two of them in a new
/// dwelling of 150 m² or more.
/// </summary>
public static class MepSystems
{
    /// <summary>A dwelling of this many square metres or more has its heating in two zones at least (Approved Document L, 2021).</summary>
    public const double TwoZoneArea = 150;

    public static IReadOnlyList<Connector> ConnectorsOf(BimDocument document, Component component) =>
        document.FindType<ComponentType>(component.TypeId) is { } type ? Connectors.Of(type) : Array.Empty<Connector>();

    /// <summary>Whether something is a fitting of the services: a component with connectors.</summary>
    public static bool IsFitting(BimDocument document, Element element) => element is Component component && ConnectorsOf(document, component).Count > 0;

    /// <summary>The disciplines a fitting is drawn in: those of the kinds of its connectors.</summary>
    public static IReadOnlySet<Discipline> DisciplinesOf(BimDocument document, Component component) =>
        ConnectorsOf(document, component).Select(connector => connector.Kind.Discipline()).ToHashSet();

    public static IReadOnlyList<Component> MembersOf(BimDocument document, MepSystem system) =>
        system.Members.Select(id => document.Find<Component>(id)).OfType<Component>().Distinct().ToList();

    public static Component? SourceOf(BimDocument document, MepSystem system) => document.Find<Component>(system.SourceId);

    /// <summary>The systems a fitting is on or feeds.</summary>
    public static IReadOnlyList<MepSystem> SystemsOf(BimDocument document, Component component) =>
        document.All<MepSystem>().Where(system => system.Members.Contains(component.Id) || system.SourceId == component.Id).ToList();

    /// <summary>The system of a kind a fitting is on, or null.</summary>
    public static MepSystem? MemberOf(BimDocument document, Component component, SystemKind kind) =>
        document.All<MepSystem>().FirstOrDefault(system => system.Kind == kind && system.Members.Contains(component.Id));

    /// <summary>The systems of a kind a piece of equipment feeds.</summary>
    public static IReadOnlyList<MepSystem> FedBy(BimDocument document, Component component, SystemKind kind) =>
        document.All<MepSystem>().Where(system => system.Kind == kind && system.SourceId == component.Id).ToList();

    /// <summary>How a fitting is named in a list: its type, and the room it is in or the storey it is on.</summary>
    public static string? Describe(BimDocument document, Component? component)
    {
        if (component is null) return null;
        var type = document.FindType<ComponentType>(component.TypeId)?.Name ?? "Fitting";
        return RoomsOf.Of(document, component) is { } room
            ? $"{type} in {RoomsOf.Label(room)}"
            : $"{type} on {document.FindLevel(component.LevelId)?.Name ?? "no storey"}";
    }

    /// <summary>Why a fitting cannot go on a system - or null, where it can.</summary>
    public static string? WhyNotMember(BimDocument document, Component component, MepSystem system)
    {
        var connectors = ConnectorsOf(document, component).Where(connector => connector.Kind == system.Kind).ToList();
        var name = document.FindType<ComponentType>(component.TypeId)?.Name ?? "It";
        if (connectors.Count == 0) return $"{name} has no {system.Kind.Title().ToLowerInvariant()} connection";

        // Equipment feeding the kind - a boiler, with its return as well as its flow - is a system's source, not on it.
        if (connectors.Any(connector => connector.Feeds)) return $"{name} feeds {system.Kind.Title().ToLowerInvariant()} - make it the system's source";
        if (MemberOf(document, component, system.Kind) is { } other && !ReferenceEquals(other, system)) return $"{name} is on {other.Name} already";
        return null;
    }

    /// <summary>Why a piece of equipment cannot feed a system - or null, where it can.</summary>
    public static string? WhyNotSource(BimDocument document, Component component, MepSystem system)
    {
        var name = document.FindType<ComponentType>(component.TypeId)?.Name ?? "It";
        var feeding = ConnectorsOf(document, component).FirstOrDefault(connector => connector.Kind == system.Kind && connector.Feeds);
        if (feeding is null) return $"{name} cannot feed {system.Kind.Title().ToLowerInvariant()}{(system.Kind.Fed() is { } fed ? $" - {fed} does" : string.Empty)}";
        if (!feeding.FeedsMany && FedBy(document, component, system.Kind).FirstOrDefault(other => !ReferenceEquals(other, system)) is { } taken)
            return $"{name} feeds {taken.Name} already";
        return null;
    }

    /// <summary>The equipment in the model that could feed a system of a kind.</summary>
    public static IReadOnlyList<Component> SourcesFor(BimDocument document, SystemKind kind) =>
        document.All<Component>().Where(component => ConnectorsOf(document, component).Any(connector => connector.Kind == kind && connector.Feeds)).ToList();

    /// <summary>The next free name for a system of a kind: "Cold Water 1", "Heating Zone 2", "Circuit 3".</summary>
    public static string NextName(BimDocument document, SystemKind kind)
    {
        var prefix = kind.NamePrefix();
        var used = document.All<MepSystem>()
            .Select(system => system.Name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase) && int.TryParse(system.Name[(prefix.Length + 1)..], out var number) ? number : 0)
            .DefaultIfEmpty(0).Max();
        return $"{prefix} {used + 1}";
    }

    /// <summary>
    /// The kinds of system the fittings picked can make: those any of them has a connection of
    /// that is not on a system yet - or, for equipment picked alone, the kinds it feeds.
    /// </summary>
    public static IReadOnlyList<SystemKind> KindsFor(BimDocument document, IReadOnlyList<Component> picked)
    {
        var kinds = new SortedSet<SystemKind>();
        foreach (var component in picked)
            foreach (var connector in ConnectorsOf(document, component))
            {
                if (!connector.Feeds && MemberOf(document, component, connector.Kind) is null) kinds.Add(connector.Kind);
                else if (connector.Feeds && (connector.FeedsMany || FedBy(document, component, connector.Kind).Count == 0) && picked.Count == 1) kinds.Add(connector.Kind);
            }

        return kinds.ToList();
    }

    /// <summary>
    /// A new system of a kind made of the fittings picked: those of them with a free connection of
    /// the kind go on it, and equipment among them able to feed it is its source - or, where none
    /// was picked and the model has only one, that one. Null, with why, where nothing picked can go on it.
    /// </summary>
    public static (MepSystem? System, string? Refusal) Make(BimDocument document, SystemKind kind, IReadOnlyList<Component> picked)
    {
        var system = new MepSystem { Kind = kind, Name = NextName(document, kind) };
        var sources = picked.Where(component => WhyNotSource(document, component, system) is null).ToList();
        var members = picked.Where(component => WhyNotMember(document, component, system) is null).ToList();
        if (members.Count == 0 && sources.Count == 0)
            return (null, picked.Count == 1
                ? WhyNotMember(document, picked[0], system)
                : $"None of these has a {kind.Title().ToLowerInvariant()} connection free to go on a new system");

        system.Members.AddRange(members.Select(member => member.Id));
        var source = sources.Count >= 1 ? sources[0] : null;
        if (source is null && kind.Fed() is not null && SourcesFor(document, kind) is { Count: 1 } only && WhyNotSource(document, only[0], system) is null)
            source = only[0];
        system.SourceId = source?.Id;
        system.Use = UseOf(document, kind, members);

        // In the US a circuit is given a GFCI and an AFCI where the NEC asks them, not everywhere.
        if (kind == SystemKind.Power && document.ProjectInformation.ClearanceStandard.IsUs())
            (system.Rcd, system.ArcFault) = Circuits.UsProtection(document, system);
        return (system, null);
    }

    /// <summary>What a new circuit is for, by what is on it: lights and switches the lighting - the alarms with them - sockets a ring, the cooker and the shower their own.</summary>
    public static CircuitUse UseOf(BimDocument document, SystemKind kind, IReadOnlyList<Component> members)
    {
        if (kind != SystemKind.Power || members.Count == 0) return CircuitUse.Other;
        var forms = members.Select(member => document.FindType<ComponentType>(member.TypeId)?.Form).ToHashSet();
        bool Only(params ComponentForm[] allowed) => forms.All(form => form is { } f && allowed.Contains(f));

        if (Only(ComponentForm.CeilingLight, ComponentForm.WallLight, ComponentForm.Switch, ComponentForm.ExtractFan)) return CircuitUse.Lighting;
        if (forms.Any(form => form is ComponentForm.CeilingLight or ComponentForm.WallLight)
            && Only(ComponentForm.CeilingLight, ComponentForm.WallLight, ComponentForm.Switch, ComponentForm.ExtractFan, ComponentForm.SmokeAlarm, ComponentForm.HeatAlarm, ComponentForm.CarbonMonoxideAlarm))
            return CircuitUse.Lighting;
        if (Only(ComponentForm.Socket, ComponentForm.ShaverSocket)) return CircuitUse.RingFinal;
        if (Only(ComponentForm.Cooker, ComponentForm.OvenHousing, ComponentForm.CookerHood)) return CircuitUse.Cooker;
        if (Only(ComponentForm.Shower)) return CircuitUse.Shower;
        if (Only(ComponentForm.SmokeAlarm, ComponentForm.HeatAlarm, ComponentForm.CarbonMonoxideAlarm)) return CircuitUse.Alarms;
        if (Only(ComponentForm.Boiler, ComponentForm.Thermostat)) return CircuitUse.Heating;
        if (Only(ComponentForm.HotWaterCylinder)) return CircuitUse.ImmersionHeater;
        return CircuitUse.Other;
    }

    /// <summary>
    /// The pipes or ducts of a system's kind on no system that join it: drawn to or from its fittings
    /// before it was made, end to end from their connectors or from its own runs - those to be put on
    /// it when it is made or a fitting is put on it. Fittings to be put on it may be named as well.
    /// </summary>
    public static (IReadOnlyList<Pipe> Pipes, IReadOnlyList<Duct> Ducts) Strays(BimDocument document, MepSystem system, params Component[] also)
    {
        var domain = system.Kind.Domain();
        if (domain is not (ConnectorDomain.Pipe or ConnectorDomain.Duct)) return (Array.Empty<Pipe>(), Array.Empty<Duct>());
        var touch = domain == ConnectorDomain.Pipe ? PipeNetworks.Touch : DuctNetworks.Touch;

        var runs = domain == ConnectorDomain.Pipe
            ? document.All<Pipe>().Where(pipe => pipe.Kind == system.Kind).Select(pipe => (Run: (Element)pipe, pipe.SystemId, Ends: pipe.Ends(document))).ToList()
            : document.All<Duct>().Where(duct => duct.Kind == system.Kind).Select(duct => (Run: (Element)duct, duct.SystemId, Ends: duct.Ends(document))).ToList();
        var fittings = MembersOf(document, system).Concat(also);
        if (SourceOf(document, system) is { } source) fittings = fittings.Append(source);
        var reached = fittings.SelectMany(fitting => Connectors.Placed(document, fitting))
            .Where(placed => placed.Connector.Kind == system.Kind && placed.Connector.Domain == domain).Select(placed => placed.At)
            .Concat(runs.Where(run => run.SystemId == system.Id).SelectMany(run => new[] { run.Ends.Start, run.Ends.End }))
            .ToList();

        // Out from what is on it, run by run, each loose one touching what is reached at an end.
        static double Apart(Geometry.Point3D a, Geometry.Point3D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
        var loose = runs.Where(run => run.SystemId is null).ToList();
        var taken = new List<Element>();
        var frontier = reached;
        while (frontier.Count > 0 && loose.Count > 0)
        {
            var next = new List<Geometry.Point3D>();
            foreach (var run in loose.ToList())
            {
                if (!frontier.Any(point => Apart(point, run.Ends.Start) <= touch || Apart(point, run.Ends.End) <= touch)) continue;
                loose.Remove(run);
                taken.Add(run.Run);
                next.Add(run.Ends.Start);
                next.Add(run.Ends.End);
            }

            frontier = next;
        }

        return (taken.OfType<Pipe>().ToList(), taken.OfType<Duct>().ToList());
    }

    /// <summary>The rooms a system's fittings are in, in the order they come.</summary>
    public static IReadOnlyList<string> RoomsServed(BimDocument document, MepSystem system)
    {
        using var outlines = new RoomsOf.Outlines();
        return MembersOf(document, system)
            .Select(member => RoomsOf.Containing(document, member.LevelId, member.Location))
            .OfType<Room>().Distinct().Select(RoomsOf.Label).ToList();
    }

    /// <summary>
    /// The kinds a fitting has connections of that are not connected: on no system of the kind,
    /// or - for equipment - feeding none. Those that may be left are left out.
    /// </summary>
    public static IReadOnlyList<SystemKind> OpenKinds(BimDocument document, Component component)
    {
        var open = new List<SystemKind>();
        var connectors = ConnectorsOf(document, component);
        foreach (var kind in connectors.Where(connector => !connector.Optional).Select(connector => connector.Kind).Distinct())
        {
            // What feeds a kind is connected by feeding a system of it; anything else by being on one.
            var connected = connectors.Any(connector => connector.Kind == kind && connector.Feeds)
                ? FedBy(document, component, kind).Count > 0
                : MemberOf(document, component, kind) is not null;
            if (!connected) open.Add(kind);
        }

        // A lone smoke alarm has nothing to be linked to.
        if (open.Contains(SystemKind.FireAlarm) && Alarms(document).Count < 2) open.Remove(SystemKind.FireAlarm);
        return open;
    }

    /// <summary>The smoke and heat alarms in the model: what are linked to each other.</summary>
    private static IReadOnlyList<Component> Alarms(BimDocument document) =>
        document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm).ToList();

    /// <summary>What is wanting in the services, fitting by fitting and system by system, with the rules asking for it.</summary>
    public static IReadOnlyList<MepProblem> Problems(BimDocument document) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("services problems", document, null, null, () => Work(document)) : Work(document);

    private static IReadOnlyList<MepProblem> Work(BimDocument document)
    {
        var problems = new List<MepProblem>();
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        using var outlines = new RoomsOf.Outlines();
        using var cache = ModelCache.Begin(document);

        foreach (var component in document.All<Component>())
        {
            if (document.FindType<ComponentType>(component.TypeId) is not { } type || Connectors.Of(type).Count == 0) continue;
            foreach (var kind in OpenKinds(document, component))
                problems.Add(new MepProblem(component, Unconnected(type, kind, feeds: Connectors.Of(type).Any(connector => connector.Kind == kind && connector.Feeds && !connector.Optional), us)));
        }

        problems.AddRange(SystemChecks(document, null));

        // The pipes: too small, not joined, not reaching; and the water the dwelling uses.
        problems.AddRange(PipeNetworks.Problems(document));
        problems.AddRange(Drainage.Problems(document));
        problems.AddRange(WaterUse.Problems(document));
        problems.AddRange(Heating.Problems(document));
        problems.AddRange(Ventilation.Problems(document));
        problems.AddRange(DuctNetworks.Problems(document));
        problems.AddRange(Circuits.Problems(document));
        problems.AddRange(Lighting.Problems(document));
        problems.AddRange(AlarmSiting.Problems(document));
        problems.AddRange(Penetrations.Problems(document));
        problems.AddRange(Notching.Problems(document));
        problems.AddRange(ServiceClearances.Problems(document));
        return problems;
    }

    /// <summary>
    /// What is wanting in one system itself - what its Properties say - with only the checks of its
    /// kind run, and only on it: the same as the whole model's problems said of it, without the rest.
    /// </summary>
    public static IReadOnlyList<MepProblem> ProblemsOf(BimDocument document, MepSystem system)
    {
        using var outlines = new RoomsOf.Outlines();
        using var cache = ModelCache.Begin(document);
        var found = new List<MepProblem>(SystemChecks(document, system));
        switch (system.Kind)
        {
            case SystemKind.Heating:
                found.AddRange(Heating.ZoneProblems(document, system));
                break;
            case SystemKind.ColdWater:
                found.AddRange(WaterUse.Problems(document));
                break;
            case SystemKind.Power:
                found.AddRange(Circuits.Problems(document, system));
                break;
            case SystemKind.Switching:
                found.AddRange(Lighting.SwitchingProblems(document, system));
                break;
        }

        if (system.Kind.Domain() == ConnectorDomain.Duct) found.AddRange(DuctNetworks.Problems(document, system));
        return found.Where(problem => ReferenceEquals(problem.Element, system)).ToList();
    }

    /// <summary>What is wanting in the systems themselves - all of them, or one: empty, unfed, the alarms not linked as one, heating zones without their controls.</summary>
    private static IEnumerable<MepProblem> SystemChecks(BimDocument document, MepSystem? only)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var systems = document.All<MepSystem>().ToList();
        foreach (var system in systems.Where(system => only is null || ReferenceEquals(system, only)))
        {
            var members = MembersOf(document, system);
            if (members.Count == 0) yield return new MepProblem(system, "it has no fittings on it");
            if (system.Kind.Fed() is { } fed && SourceOf(document, system) is null)
                yield return new MepProblem(system, Drainage.IsDrainage(system.Kind) ? $"it drains nowhere yet - {fed} takes it" : $"nothing feeds it - {fed} does");
        }

        // The alarms linked as one: BS 5839-6, or the IRC's R314.4.
        if (only is null or { Kind: SystemKind.FireAlarm })
        {
            var interlinks = systems.Where(system => system.Kind == SystemKind.FireAlarm && MembersOf(document, system).Count > 0).ToList();
            if (interlinks.Count > 1)
                foreach (var system in interlinks.Where(system => only is null || ReferenceEquals(system, only)))
                    yield return new MepProblem(system, $"the alarms are split between {interlinks.Count} interlinks - " + (us
                        ? "IRC R314.4 interconnects the smoke alarms of a dwelling so that one sounding sounds them all"
                        : "BS 5839-6, which Approved Document B (1.1) follows, links the alarms so that one sounding sounds them all"));
        }

        // Heating zones, each with its own time and temperature control: Approved Document L (2021).
        if (!us && only is null or { Kind: SystemKind.Heating })
        {
            var zones = systems.Where(system => system.Kind == SystemKind.Heating && MembersOf(document, system).Any(member => Heats(document, member))).ToList();
            foreach (var zone in zones.Where(zone => (only is null || ReferenceEquals(zone, only))
                                                     && !MembersOf(document, zone).Any(member => document.FindType<ComponentType>(member.TypeId)?.Form == ComponentForm.Thermostat)))
                yield return new MepProblem(zone, "it has no room thermostat - Approved Document L (2021) gives each heating zone its own time and temperature control");

            if (zones.Count == 1 && (only is null || ReferenceEquals(zones[0], only)) && DwellingArea(document) is var area and >= TwoZoneArea)
                yield return new MepProblem(zones[0],
                    $"the rooms come to {Units.OfSquareMetres(area, "0")} - Approved Document L (2021) asks a new dwelling of {Units.OfSquareMetres(TwoZoneArea, "0")} or more for two heating zones at least, each with its own time and temperature control");
        }
    }

    /// <summary>Whether a fitting heats a room: a radiator, a towel rail or an underfloor loop, not a cylinder's coil.</summary>
    private static bool Heats(BimDocument document, Component component) =>
        document.FindType<ComponentType>(component.TypeId)?.Form is ComponentForm.Radiator or ComponentForm.TowelRail or ComponentForm.UnderfloorHeating;

    /// <summary>The dwelling's floor area, as its enclosed rooms add up, m².</summary>
    public static double DwellingArea(BimDocument document) =>
        document.All<Room>().Select(room => room.GetBoundary(document)).Where(boundary => boundary.IsEnclosed).Sum(boundary => boundary.Area) / 1e6;

    /// <summary>What a fitting not connected to a kind is wanting, and the rule asking for it.</summary>
    private static string Unconnected(ComponentType type, SystemKind kind, bool feeds, bool us)
    {
        var what = kind.Title().ToLowerInvariant();
        if (feeds) return $"it feeds no {what} system";

        var sanitary = type.Form is ComponentForm.Basin or ComponentForm.Bath or ComponentForm.Shower or ComponentForm.Sink;
        var alarm = type.Form is ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm;
        return kind switch
        {
            SystemKind.HotWater when sanitary && !us => "not on a hot water system - Approved Document G (G3) asks for heated wholesome water at a washbasin, bath, shower or kitchen sink",
            SystemKind.ColdWater when sanitary && !us => "not on a cold water system - Approved Document G (G1) asks for wholesome water at a washbasin, bath, shower or kitchen sink",
            SystemKind.Foul when !us => "its foul water goes nowhere - Approved Document H (H1) carries the foul water of appliances to an outfall",
            SystemKind.Power when alarm => us
                ? "not on a circuit - IRC R314.6 has a smoke alarm take its power from the building's wiring, with a battery behind it"
                : "not on a circuit - BS 5839-6, which Approved Document B (1.1) follows, has the alarms mains powered, with a standby supply",
            SystemKind.FireAlarm => us
                ? "not interconnected - IRC R314.4 interconnects the smoke alarms of a dwelling so that one sounding sounds them all"
                : "not linked to the others - BS 5839-6, which Approved Document B (1.1) follows, links the alarms so that one sounding sounds them all",
            SystemKind.Heating when type.Form == ComponentForm.Thermostat => "it controls no heating zone",
            SystemKind.Switching when type.Form == ComponentForm.Switch => "it switches no lights - select it with the lights it turns on and make a switching system",
            SystemKind.Switching => us
                ? "no wall switch controls it - NEC 210.70 asks for lighting outlets controlled by a listed wall-mounted control"
                : "no switch controls it - select it with its switch and make a switching system",
            SystemKind.Power => "not on a circuit",
            _ => $"not on {("aeiou".Contains(what[0]) ? "an" : "a")} {what} system"
        };
    }
}
