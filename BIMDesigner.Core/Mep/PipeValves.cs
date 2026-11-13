using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// What a valve on a pipe does, as the Water Supply (Water Fittings) Regulations 1999 name them: a servicing valve, shutting off the
/// flow to a fitting to maintain it; a stopvalve, shutting off the flow in a pipe - to premises, or to part of the pipework; a
/// double check valve against backflow; a drain-off tap to empty the pipework.
/// </summary>
public enum ValveKind
{
    Servicing,
    Stop,
    DoubleCheck,
    DrainOff
}

/// <summary>
/// A valve on a pipe, so far along it from its start: carried by it - moved, turned and split with it, deleted with it - drawn on
/// it in plan and in 3D, the pipe's size.
/// </summary>
public sealed class PipeValve : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.PipeAccessories;

    /// <summary>The pipe it is on.</summary>
    public Guid PipeId { get; set; }

    public Guid HostId => PipeId;

    /// <summary>How far along the pipe from its start, mm, rising and falling included.</summary>
    public double Along { get; set; }

    public ValveKind Kind { get; set; } = ValveKind.Servicing;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var pipe = document.Find<Pipe>(PipeId);
        yield return ParameterValue.BindChoice(PipeValveParameters.Kind, () => Kind.Title(),
            v => { foreach (var kind in Enum.GetValues<ValveKind>()) if (kind.Title() == v) Kind = kind; },
            Enum.GetValues<ValveKind>().Select(kind => kind.Title()).ToArray());
        yield return ParameterValue.BindValidated(PipeValveParameters.Along, () => Along, v =>
        {
            if (pipe is null || v < 0 || v > pipe.Length) return false;
            Along = v;
            return true;
        });
        yield return ParameterValue.ReadOnly(PipeValveParameters.Size, () => pipe is null ? "<none>" : pipe.SizeName(document.FindType<PipeType>(pipe.TypeId)));
        yield return ParameterValue.ReadOnly(PipeValveParameters.SystemName, () => pipe is null ? "<none>" : document.Find<MepSystem>(pipe.SystemId)?.Name ?? "<none>");
        yield return ParameterValue.ReadOnly(PipeValveParameters.ShutsOff, () => PipeValves.Describe(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class PipeValveParameters
{
    public static readonly ParameterDefinition Kind = new("Valve", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Along = new("Distance Along Pipe", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Size = new("Size", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition SystemName = new("System", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition ShutsOff = new("Shuts Off", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
}

/// <summary>
/// Valves on pipes, and what the Water Supply (Water Fittings) Regulations 1999 ask of them (Schedule 2): a servicing valve on the
/// inlet pipe adjacent to every WC flushing cistern (16(2)) - taken here as within 1 m of pipework from its inlet - and, where one
/// supply serves separate premises, a stopvalve on the pipe to each, shutting off its supply without shutting off the others' (10(1)):
/// each dwelling named in the rooms, its cold water fittings all beyond a stopvalve of its own that no other dwelling's are beyond.
/// </summary>
public static class PipeValves
{
    /// <summary>How near its inlet a cistern's servicing valve is taken to be adjacent to it, mm of pipework.</summary>
    public const double Adjacent = 1000;

    public static string Title(this ValveKind kind) => kind switch
    {
        ValveKind.Stop => "Stopvalve",
        ValveKind.DoubleCheck => "Double check valve",
        ValveKind.DrainOff => "Drain-off tap",
        _ => "Servicing valve"
    };

    /// <summary>The valves on a pipe, from its start.</summary>
    public static IReadOnlyList<PipeValve> On(BimDocument document, Pipe pipe) =>
        document.All<PipeValve>().Where(valve => valve.PipeId == pipe.Id).OrderBy(valve => valve.Along).ToList();

    /// <summary>Where a valve is in the building, on its pipe's centre line - or null for one whose pipe is gone.</summary>
    public static Point3D? At(BimDocument document, PipeValve valve)
    {
        if (document.Find<Pipe>(valve.PipeId) is not { } pipe) return null;
        var (start, end) = pipe.Ends(document);
        var t = pipe.Length < 1e-9 ? 0 : Math.Clamp(valve.Along / pipe.Length, 0, 1);
        return new Point3D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
    }

    /// <summary>A valve put on a pipe where it passes nearest a point in plan - for an upright pipe, half way up it.</summary>
    public static PipeValve For(Pipe pipe, Point2D near, ValveKind kind)
    {
        var plan = pipe.Start.DistanceTo(pipe.End);
        var t = plan < 1 ? 0.5 : Math.Clamp((near - pipe.Start).Dot(pipe.End - pipe.Start) / (plan * plan), 0, 1);
        return new PipeValve { PipeId = pipe.Id, LevelId = pipe.LevelId, Along = pipe.Length * t, Kind = kind, Workset = pipe.Workset, PhaseCreated = pipe.PhaseCreated };
    }

    /// <summary>A valve's body in 3D: a short barrel on its pipe, half as large again, in the pipe's colour.</summary>
    public static Mesh3D? Mesh(BimDocument document, PipeValve valve)
    {
        if (document.Find<Pipe>(valve.PipeId) is not { } pipe || At(document, valve) is not { } at || pipe.Length < 1) return null;
        var (start, end) = pipe.Ends(document);
        var radius = Math.Max(pipe.Diameter, 10) * 0.8;
        var half = Math.Min(Math.Max(pipe.Diameter, 10) * 1.2, pipe.Length / 2);
        var (dx, dy, dz) = ((end.X - start.X) / pipe.Length, (end.Y - start.Y) / pipe.Length, (end.Z - start.Z) / pipe.Length);
        var mesh = new Mesh3D(valve.Id, valve.LevelId, MeshKind.Pipe, pipe.Kind.Colour(), $"{valve.Kind.Title()} {pipe.SizeName(document.FindType<PipeType>(pipe.TypeId))}");
        mesh.AddTube(new[] { new Point3D(at.X - dx * half, at.Y - dy * half, at.Z - dz * half), new Point3D(at.X + dx * half, at.Y + dy * half, at.Z + dz * half) }, radius, 10);
        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>
    /// The valves on a pipe cut in two moved to the part they are on: those past the cut onto the second, so far along it.
    /// Returns how to put them back.
    /// </summary>
    public static Action Split(BimDocument document, Pipe first, Pipe second, double cut)
    {
        var moved = document.All<PipeValve>().Where(valve => valve.PipeId == first.Id && valve.Along > cut + 1e-9).Select(valve => (Valve: valve, valve.Along)).ToList();
        foreach (var (valve, along) in moved) (valve.PipeId, valve.Along) = (second.Id, along - cut);
        return () =>
        {
            foreach (var (valve, along) in moved) (valve.PipeId, valve.Along) = (first.Id, along);
        };
    }

    /// <summary>The pipework from a fitting back to its system's source, each pipe with how far along the pipework from the fitting it begins.</summary>
    private static List<(Pipe Pipe, double From)> PathOf(PipeNetwork network, Component fitting)
    {
        var path = new List<(Pipe, double)>();
        var from = 0.0;
        for (Pipe? pipe = network.FirstPipe.GetValueOrDefault(fitting); pipe is not null && network.Loads.TryGetValue(pipe, out var load) && path.Count < 10_000; pipe = load.Toward)
        {
            path.Add((pipe, from));
            from += pipe.Length;
        }

        return path;
    }

    /// <summary>The fittings a valve shuts off: those whose pipework back to the system's source passes through its pipe.</summary>
    public static IReadOnlyList<Component> ShutsOff(BimDocument document, PipeValve valve)
    {
        if (document.Find<Pipe>(valve.PipeId) is not { } pipe || document.Find<MepSystem>(pipe.SystemId) is not { } system) return Array.Empty<Component>();
        var network = PipeNetworks.Trace(document, system);
        if (!network.Loads.ContainsKey(pipe)) return Array.Empty<Component>();
        return network.FirstPipe.Keys.Where(fitting => PathOf(network, fitting).Any(step => step.Pipe == pipe)).ToList();
    }

    /// <summary>What a valve shuts off, as its Properties say it.</summary>
    public static string Describe(BimDocument document, PipeValve valve)
    {
        if (document.Find<Pipe>(valve.PipeId) is not { } pipe || document.Find<MepSystem>(pipe.SystemId) is not { } system || !PipeNetworks.Trace(document, system).Loads.ContainsKey(pipe))
            return "Its pipe is not traced from a source yet";
        var off = ShutsOff(document, valve);
        if (off.Count == 0) return "Nothing beyond it";
        var names = off.GroupBy(fitting => document.FindType<ComponentType>(fitting.TypeId)?.Name ?? "Fitting").Select(group => group.Count() == 1 ? group.Key : $"{group.Count()} × {group.Key}");
        var dwellings = off.Select(fitting => Dwellings.Of(document, fitting)).Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(NaturalOrder.Instance).ToList();
        return $"{string.Join(", ", names)}{(dwellings.Count > 0 ? $" - {string.Join(", ", dwellings)}" : string.Empty)}";
    }

    /// <summary>What the Water Fittings Regulations ask that the valves on the cold water pipes do not give.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var valves = document.All<PipeValve>().ToList();
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        foreach (var system in document.All<MepSystem>().Where(system => system.Kind == SystemKind.ColdWater))
        {
            var pipes = PipeNetworks.PipesOf(document, system);
            if (pipes.Count == 0) continue;
            var network = PipeNetworks.Trace(document, system);
            var on = valves.Where(valve => document.Find<Pipe>(valve.PipeId) is { } pipe && network.Loads.ContainsKey(pipe))
                .GroupBy(valve => valve.PipeId).ToDictionary(group => group.Key, group => group.ToList());
            List<(Pipe Pipe, double From)> Path(Component fitting) => PathOf(network, fitting);

            double FromFitting(Pipe pipe, PipeValve valve, double from)
            {
                // The pipe's far end is the one nearer the fitting.
                var far = network.Loads[pipe].Far;
                var (start, _) = pipe.Ends(document);
                var startFar = Math.Abs(start.X - far.X) + Math.Abs(start.Y - far.Y) + Math.Abs(start.Z - far.Z) < 1;
                return from + (startFar ? valve.Along : pipe.Length - valve.Along);
            }

            // 16(2): a servicing valve on the inlet pipe adjacent to every WC flushing cistern.
            var fittings = MepSystems.MembersOf(document, system).Where(member => network.FirstPipe.ContainsKey(member)).ToList();
            foreach (var wc in fittings.Where(member => document.FindType<ComponentType>(member.TypeId)?.Form is ComponentForm.Toilet or ComponentForm.WallHungToilet))
            {
                var near = Path(wc).Any(step => on.TryGetValue(step.Pipe.Id, out var here)
                                               && here.Any(valve => valve.Kind is ValveKind.Servicing or ValveKind.Stop && FromFitting(step.Pipe, valve, step.From) <= Adjacent + 1e-6));
                if (!near)
                    yield return new MepProblem(wc, $"no servicing valve on its cistern's inlet pipe - the Water Supply (Water Fittings) Regulations 1999 (Schedule 2, 16(2)) fit one adjacent to every WC flushing cistern; within {Units.Metres(Adjacent, "0.0")} of it");
            }

            // 10(1): each of separate premises shut off on its own.
            var dwellings = fittings.GroupBy(fitting => Dwellings.Of(document, fitting), StringComparer.OrdinalIgnoreCase).Where(group => group.Key.Length > 0).ToList();
            if (dwellings.Count < 2) continue;
            var paths = fittings.ToDictionary(fitting => fitting, fitting => Path(fitting).Select(step => step.Pipe).ToHashSet());
            foreach (var dwelling in dwellings.OrderBy(group => group.Key, NaturalOrder.Instance))
            {
                var own = on.Where(entry => entry.Value.Any(valve => valve.Kind == ValveKind.Stop)).Select(entry => document.Find<Pipe>(entry.Key)!).Any(pipe =>
                    dwelling.All(fitting => paths[fitting].Contains(pipe))
                    && fittings.Where(fitting => paths[fitting].Contains(pipe)).All(fitting => string.Equals(Dwellings.Of(document, fitting), dwelling.Key, StringComparison.OrdinalIgnoreCase)));
                if (!own)
                    yield return new MepProblem(system, $"{dwelling.Key} cannot be shut off on its own - the Water Supply (Water Fittings) Regulations 1999 (Schedule 2, 10(1)) fit a stopvalve on the pipe to each of separate premises, shutting off its supply without shutting off the others'");
            }
        }
    }
}

/// <summary>A valve put on a pipe, as one step to undo.</summary>
public sealed class AddPipeValveCommand : IUndoableCommand
{
    private readonly BimDocument _document;

    public AddPipeValveCommand(BimDocument document, PipeValve valve) => (_document, Valve) = (document, valve);

    public PipeValve Valve { get; }

    public string Name => $"Place {Valve.Kind.Title()}";

    public void Redo() => _document.Add(Valve);

    public void Undo() => _document.Remove(Valve);
}
