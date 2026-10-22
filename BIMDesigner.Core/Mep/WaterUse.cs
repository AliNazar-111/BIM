using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Mep;

/// <summary>One line of the water efficiency calculator: a kind of fitting, what of it is measured, and the litres a person a day it comes to.</summary>
public sealed record WaterUseLine(string Fitting, string Measure, double Value, double UseFactor, double FixedUse)
{
    public double Litres => Math.Round(Value * UseFactor + FixedUse, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The water efficiency calculation for a dwelling, and how it stands against its target.</summary>
public sealed record WaterUseResult(IReadOnlyList<WaterUseLine> Lines, double Total, double Consumption, double Target, bool WithinFittingsLimits, bool HasShower)
{
    /// <summary>Whether it is within its target: by the calculation, or by the fittings approach.</summary>
    public bool Meets => Consumption <= Target + 1e-9;

    public string Said => $"{Consumption:0.0} litres a person a day - Approved Document G (G2) asks {Target:0} at most" +
                          (WithinFittingsLimits ? $"; every fitting is within Table {(Target <= 110 ? "2.2" : "2.1")}'s limits (the fittings approach)" : string.Empty);
}

/// <summary>
/// How much wholesome water a new dwelling uses, a person a day, by Approved Document G's water
/// efficiency calculator (Appendix A, Table A1): each kind of fitting's flow, flush or capacity times
/// its use factor, plus its fixed use - WCs 4.42 uses a day (a dual flush 1.46 full and 2.96 part);
/// basin taps 1.58 and 1.58; a bath 0.11 and a shower 4.37 where there are both, 0.50 or 5.60 where
/// there is only one; kitchen sink taps 0.44 and 10.36; a washing machine 2.1 a kilo of dry load, a
/// dishwasher 3.6 a place setting (8.17 l/kg and 1.25 l/place assumed where none is fitted) - the
/// sum normalised by 0.91, and 5 litres added for use outside. Regulation 36 asks 125 litres at
/// most, or 110 where planning makes the optional requirement a condition. Where there are several
/// of a kind with different figures, their average is taken - but no less than 70 per cent of the
/// highest (A8 to A10); for WCs the average effective flush, a dual flush's being 0.33 of its full
/// and 0.67 of its part flush (A11). Bidets and bath taps are not counted (A2).
/// </summary>
public static class WaterUse
{
    public const double Target = 125, OptionalTarget = 110, Normalisation = 0.91, External = 5.0;

    /// <summary>The figures it counts, where nothing sets them: the limits of Approved Document G's Table 2.1.</summary>
    public const double DefaultDishwasher = 1.25, DefaultWashingMachine = 8.17;

    /// <summary>The figure for several fittings of a kind: their average, or 70 per cent of the highest where that is more (A8 to A10).</summary>
    private static double Proportionate(IReadOnlyList<double> values) =>
        Math.Max(values.Average(), values.Max() * 0.7);

    public static WaterUseResult Assess(BimDocument document)
    {
        var fittings = document.All<Component>().Select(component => document.FindType<ComponentType>(component.TypeId)).OfType<ComponentType>().ToList();
        List<ComponentType> Of(params ComponentForm[] forms) => fittings.Where(type => forms.Contains(type.Form)).ToList();
        var lines = new List<WaterUseLine>();

        // WCs: one kind, single or dual flush, as it is; several kinds, their average effective flush.
        var wcs = Of(ComponentForm.Toilet, ComponentForm.WallHungToilet);
        var wcKinds = wcs.Select(type => (type.FlushVolume, type.PartFlushVolume)).Distinct().ToList();
        if (wcKinds.Count == 1 && wcKinds[0].PartFlushVolume > 0)
        {
            lines.Add(new WaterUseLine("WC (dual flush)", "Full flush volume (litres)", wcKinds[0].FlushVolume, 1.46, 0));
            lines.Add(new WaterUseLine("WC (dual flush)", "Part flush volume (litres)", wcKinds[0].PartFlushVolume, 2.96, 0));
        }
        else if (wcKinds.Count == 1)
            lines.Add(new WaterUseLine("WC (single flush)", "Flush volume (litres)", wcKinds[0].FlushVolume, 4.42, 0));
        else if (wcKinds.Count > 1)
            lines.Add(new WaterUseLine("WCs (multiple fittings)", "Average effective flushing volume (litres)",
                wcs.Average(type => type.PartFlushVolume > 0 ? type.FlushVolume * 0.33 + type.PartFlushVolume * 0.67 : type.FlushVolume), 4.42, 0));

        var basins = Of(ComponentForm.Basin);
        if (basins.Count > 0)
            lines.Add(new WaterUseLine("Taps (excluding kitchen/utility room taps)", "Flow rate (litres/minute)", Proportionate(basins.Select(type => type.FlowRate).ToList()), 1.58, 1.58));

        var baths = Of(ComponentForm.Bath);
        var showers = Of(ComponentForm.Shower);
        if (baths.Count > 0)
            lines.Add(new WaterUseLine(showers.Count > 0 ? "Bath (where shower also present)" : "Bath only", "Capacity to overflow (litres)",
                Proportionate(baths.Select(type => type.Capacity).ToList()), showers.Count > 0 ? 0.11 : 0.50, 0));
        if (showers.Count > 0)
            lines.Add(new WaterUseLine(baths.Count > 0 ? "Shower (where bath also present)" : "Shower only", "Flow rate (litres/minute)",
                Proportionate(showers.Select(type => type.FlowRate).ToList()), baths.Count > 0 ? 4.37 : 5.60, 0));

        var sinks = Of(ComponentForm.Sink);
        if (sinks.Count > 0)
            lines.Add(new WaterUseLine("Kitchen/utility room sink taps", "Flow rate (litres/minute)", Proportionate(sinks.Select(type => type.FlowRate).ToList()), 0.44, 10.36));

        var washers = Of(ComponentForm.WashingMachine);
        lines.Add(new WaterUseLine("Washing machine", "Litres/kg dry load", washers.Count > 0 ? Proportionate(washers.Select(type => type.WaterPerUse).ToList()) : DefaultWashingMachine, 2.1, 0));
        var dishwashers = Of(ComponentForm.Dishwasher);
        lines.Add(new WaterUseLine("Dishwasher", "Litres/place setting", dishwashers.Count > 0 ? Proportionate(dishwashers.Select(type => type.WaterPerUse).ToList()) : DefaultDishwasher, 3.6, 0));

        var total = Math.Round(lines.Sum(line => line.Litres), 2, MidpointRounding.AwayFromZero);
        var consumption = Math.Round(total * Normalisation + External, 1, MidpointRounding.AwayFromZero);
        var target = document.ProjectInformation.WaterOptionalRequirement ? OptionalTarget : Target;
        return new WaterUseResult(lines, total, consumption, target, WithinLimits(fittings, target), showers.Count > 0);
    }

    /// <summary>
    /// Whether every fitting is within Approved Document G's limits for the fittings approach -
    /// Table 2.1 for 125 litres, Table 2.2 for 110 - and there is a shower, without which the
    /// calculator must be used.
    /// </summary>
    public static bool WithinLimits(IReadOnlyList<ComponentType> fittings, double target)
    {
        var optional = target <= OptionalTarget;
        var (wcFull, wcPart, wcSingle, shower, bath, basin, sink) = optional ? (4.0, 2.6, 0.0, 8.0, 170.0, 5.0, 6.0) : (6.0, 4.0, 4.5, 10.0, 185.0, 6.0, 8.0);
        if (!fittings.Any(type => type.Form == ComponentForm.Shower)) return false;
        return fittings.All(type => type.Form switch
        {
            ComponentForm.Toilet or ComponentForm.WallHungToilet => type.PartFlushVolume > 0
                ? type.FlushVolume <= wcFull + 1e-9 && type.PartFlushVolume <= wcPart + 1e-9
                : wcSingle > 0 && type.FlushVolume <= wcSingle + 1e-9,
            ComponentForm.Shower => type.FlowRate <= shower + 1e-9,
            ComponentForm.Bath => type.Capacity <= bath + 1e-9,
            ComponentForm.Basin => type.FlowRate <= basin + 1e-9,
            ComponentForm.Sink => type.FlowRate <= sink + 1e-9,
            ComponentForm.Dishwasher => type.WaterPerUse <= 1.25 + 1e-9,
            ComponentForm.WashingMachine => type.WaterPerUse <= 8.17 + 1e-9,
            _ => true
        });
    }

    /// <summary>What is wanting in the dwelling's water use: over its target. Nothing where it has no sanitary fittings, or is built to the US's codes.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        var sanitary = document.All<Component>().FirstOrDefault(component => document.FindType<ComponentType>(component.TypeId)?.Form is
            ComponentForm.Basin or ComponentForm.Bath or ComponentForm.Shower or ComponentForm.Toilet or ComponentForm.WallHungToilet or ComponentForm.Sink);
        if (sanitary is null) yield break;

        var result = Assess(document);
        if (!result.Meets)
            yield return new MepProblem(document.All<MepSystem>().FirstOrDefault(system => system.Kind == SystemKind.ColdWater) ?? (Elements.Element)sanitary,
                $"the dwelling uses {result.Consumption:0.0} litres a person a day - Approved Document G (G2, regulation 36) asks {result.Target:0} at most");
    }
}
