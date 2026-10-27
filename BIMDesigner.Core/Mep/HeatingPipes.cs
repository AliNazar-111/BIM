using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Mep;

/// <summary>What a heating pipe carries at the design conditions: the heat beyond it, W; the water, kg/s; how fast it goes, m/s; and what it loses in pressure a metre, Pa/m.</summary>
public sealed record HeatingFlow(double Watts, double MassFlow, double Velocity, double Gradient);

/// <summary>
/// Heating pipes sized for flow and pressure drop.
///
/// What each pipe carries is the output of the radiators, towel rails, floors and cylinder coils
/// beyond it at its zone's temperatures, and the water it takes is that heat over the water's
/// specific heat and the zone's drop from flow to return. The pressure it loses a metre is
/// Darcy-Weisbach's, its friction factor Swamee and Jain's fit to Colebrook - with the water's
/// density and viscosity at the zone's mean temperature and the pipe's own roughness: drawn copper,
/// stainless steel and PVC-U 0.0015 mm, PE-X 0.007, galvanised steel 0.15. Each is sized the smallest
/// of its material - 15 mm or more - that keeps the water to 1.0 m/s, 1.5 over 50 mm, and the loss
/// to 300 Pa a metre: the limits CIBSE's guidance on heating pipework is quoted at, for quiet pipes
/// and a pump of reasonable size (not seen at first hand). In the US, as ASHRAE's Fundamentals
/// sizes hydronic pipes: 4 ft/s at most to 2 in, and 4 ft of head lost a 100 ft. The circuit
/// through the most pipe resistance - the index circuit - says what the pump has to overcome
/// along its pipes.
/// </summary>
public static class HeatingPipes
{
    /// <summary>Water's specific heat, J/kgK.</summary>
    public const double SpecificHeat = 4180;

    /// <summary>The fastest water in a pipe to 50 mm, and over it, m/s; the most it loses a metre, Pa/m.</summary>
    public const double VelocityLimit = 1.0, LargeVelocityLimit = 1.5, GradientLimit = 300;

    /// <summary>The same in the US, as ASHRAE's Fundamentals has them: 4 ft/s in a pipe to 2 in, m/s; 4 ft of head lost a 100 ft, Pa/m.</summary>
    public const double UsVelocityLimit = 4 * 0.3048, UsGradientLimit = 0.04 * 9806.65;

    /// <summary>The largest pipe the US velocity limit holds for, outside diameter in mm: 2 in copper tube.</summary>
    private const double UsSmallPipe = 2.125 * Units.MillimetresPerInch;

    /// <summary>A pipe's absolute roughness, mm.</summary>
    public static double Roughness(PipeMaterial material) => material.Like() switch
    {
        PipeMaterial.PeX => 0.007,
        PipeMaterial.GalvanisedSteel => 0.15,
        _ => 0.0015
    };

    /// <summary>Water's density (kg/m³) and dynamic viscosity (Pa·s) at a temperature, °C.</summary>
    public static (double Density, double Viscosity) Water(double celsius) =>
        (1000 - 0.0178 * Math.Pow(Math.Abs(celsius - 4), 1.7), 2.414e-5 * Math.Pow(10, 247.8 / (celsius + 273.15 - 140)));

    /// <summary>How fast water goes in a bore (mm) carrying so much (kg/s), m/s, and the pressure it loses a metre, Pa/m.</summary>
    public static (double Velocity, double Gradient) Friction(double massFlow, double bore, PipeMaterial material, double celsius)
    {
        if (massFlow <= 0 || bore <= 0) return (0, 0);
        var (density, viscosity) = Water(celsius);
        var diameter = bore / 1000;
        var velocity = massFlow / density / (Math.PI * diameter * diameter / 4);
        var reynolds = density * velocity * diameter / viscosity;
        var friction = reynolds < 2300
            ? 64 / reynolds
            : 0.25 / Math.Pow(Math.Log10(Roughness(material) / 1000 / (3.7 * diameter) + 5.74 / Math.Pow(reynolds, 0.9)), 2);
        return (velocity, friction * density * velocity * velocity / 2 / diameter);
    }

    /// <summary>The fastest water a pipe of this outside diameter may carry, m/s.</summary>
    public static double FastestIn(double outside, bool us = false) =>
        us ? outside > UsSmallPipe + 0.01 ? double.PositiveInfinity : UsVelocityLimit
        : outside > 50 + 0.01 ? LargeVelocityLimit : VelocityLimit;

    /// <summary>The most a pipe's water may lose a metre, Pa/m.</summary>
    public static double SteepestLoss(bool us) => us ? UsGradientLimit : GradientLimit;

    /// <summary>The limits a pipe is sized within, as its Properties and problems say them.</summary>
    private static string Within(BimDocument document) => document.ProjectInformation.ClearanceStandard.IsUs()
        ? "within 4 ft/s and 4 ft of head a 100 ft (ASHRAE Fundamentals)"
        : "within 1.0 m/s and 300 Pa/m";

    /// <summary>What a pipe of a heating zone carries; null for one not traced or carrying nothing yet.</summary>
    public static HeatingFlow? Of(BimDocument document, Pipe pipe, PipeNetwork network)
    {
        if (pipe.Kind != SystemKind.Heating || !network.Loads.TryGetValue(pipe, out var carried) || carried.Heat <= 0) return null;
        var (flow, back) = Heating.Design(document, network.System);
        var mass = carried.Heat / (SpecificHeat * Math.Max(1, flow - back));
        var bore = document.FindType<PipeType>(pipe.TypeId) is { } type ? PipeSizes.Of(type.Material).FirstOrDefault(size => Math.Abs(size.Outside - pipe.Diameter) < 0.01)?.Inside ?? pipe.Diameter * 0.85 : pipe.Diameter * 0.85;
        var (velocity, gradient) = Friction(mass, bore, document.FindType<PipeType>(pipe.TypeId)?.Material ?? PipeMaterial.Copper, (flow + back) / 2);
        return new HeatingFlow(carried.Heat, mass, velocity, gradient);
    }

    /// <summary>The smallest pipe of a material that carries this much water within the limits; null where none of its sizes does.</summary>
    public static PipeSize? Required(PipeMaterial material, double massFlow, double celsius, bool us = false) =>
        PipeSizes.Of(material).Where(size => size.Outside >= 15 - 0.01).FirstOrDefault(size =>
            Friction(massFlow, size.Inside, material, celsius) is var (velocity, gradient) && velocity <= FastestIn(size.Outside, us) + 1e-9 && gradient <= SteepestLoss(us) + 1e-9);

    /// <summary>The size a heating pipe needs, or null for one not traced, carrying nothing, or past its material's sizes.</summary>
    public static PipeSize? Required(BimDocument document, Pipe pipe, PipeNetwork network) =>
        Of(document, pipe, network) is { } carried && document.FindType<PipeType>(pipe.TypeId) is { } type
            ? Required(type.Material, carried.MassFlow, Heating.Design(document, network.System) is var (flow, back) ? (flow + back) / 2 : 50, document.ProjectInformation.ClearanceStandard.IsUs())
            : null;

    /// <summary>
    /// The index circuit: of everything on a zone, the one whose water loses the most pressure along
    /// the pipes out to it and back - and how much, Pa, and its water, kg/s, at the source.
    /// </summary>
    public static (Component? Fitting, double Pressure, double MassFlow) IndexCircuit(BimDocument document, PipeNetwork network)
    {
        if (network.System.Kind != SystemKind.Heating) return (null, 0, 0);
        var flows = network.Loads.Keys.ToDictionary(pipe => pipe, pipe => Of(document, pipe, network));
        double Along(Pipe? pipe)
        {
            var total = 0.0;
            for (var guard = 0; pipe is not null && guard < 10_000; guard++)
            {
                total += (flows.GetValueOrDefault(pipe)?.Gradient ?? 0) * pipe.Length / 1000;
                pipe = network.Loads[pipe].Toward;
            }

            return total;
        }

        Component? worst = null;
        var most = 0.0;
        foreach (var member in MepSystems.MembersOf(document, network.System))
        {
            var ends = Connectors.Placed(document, member).Where(placed => placed.Connector.Kind == SystemKind.Heating && !placed.Connector.Feeds)
                .Select(placed => network.Loads.FirstOrDefault(entry => Distance(entry.Value.Far, placed.At) <= PipeNetworks.Touch).Key)
                .OfType<Pipe>().Distinct();
            var pressure = ends.Sum(Along);
            if (pressure > most) (worst, most) = (member, pressure);
        }

        var source = network.Loads.Where(entry => entry.Value.Toward is null).Sum(entry => flows.GetValueOrDefault(entry.Key)?.MassFlow ?? 0) / 2;
        return (worst, most, source);
    }

    private static double Distance(Geometry.Point3D a, Geometry.Point3D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>What a heating pipe carries, as its Properties say it.</summary>
    public static string Describe(BimDocument document, Pipe pipe, PipeNetwork network)
    {
        if (Of(document, pipe, network) is not { } carried) return $"{Units.Metres(network.Loads[pipe].FromSource, "0.0")} from what feeds {network.System.Name} - nothing beyond it gives heat yet";
        var (flow, back) = Heating.Design(document, network.System);
        var litres = carried.MassFlow / Water((flow + back) / 2).Density * 1000 * 3600;
        var said = $"{Units.Watts(carried.Watts, "#,0")} beyond it: {Units.LitresPerHour(litres, "0")} at {Units.MetresPerSecond(carried.Velocity, "0.00")}, losing {Units.PascalsPerMetre(carried.Gradient, "0")}";
        var type = document.FindType<PipeType>(pipe.TypeId);
        return Required(document, pipe, network) is { } size && type is not null
            ? $"{said} - {size.Name} keeps it {Within(document)}"
            : $"{said} - more than its material's sizes carry {Within(document)}";
    }

    /// <summary>Heating pipes too small for what they carry.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document, PipeNetwork network)
    {
        foreach (var pipe in network.Loads.Keys)
        {
            if (Of(document, pipe, network) is not { } carried || document.FindType<PipeType>(pipe.TypeId) is not { } type) continue;
            var required = Required(document, pipe, network);
            if (required is null)
                yield return new MepProblem(pipe, $"it carries {Units.Watts(carried.Watts, "#,0")} - more than a {type.Name} pipe carries {Within(document)}");
            else if (pipe.Diameter < required.Outside - 0.01)
                yield return new MepProblem(pipe,
                    $"too small at {pipe.SizeName(type)}: it carries {Units.Watts(carried.Watts, "#,0")}, its water at {Units.MetresPerSecond(carried.Velocity, "0.00")} losing {Units.PascalsPerMetre(carried.Gradient, "0")} - {required.Name} keeps it {Within(document)}");
        }
    }
}
