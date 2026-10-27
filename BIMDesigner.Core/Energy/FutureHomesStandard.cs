using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Energy;

/// <summary>One thing Approved Document L asks: what it is, whether the home meets it, and how it stands.</summary>
public sealed record FhsCheck(string Requirement, bool Met, string Detail)
{
    /// <summary>What falls short, to pick in the model.</summary>
    public Element? Element { get; init; }
}

/// <summary>
/// A home checked against the Future Homes Standard: its SAP and environmental impact ratings, its
/// emission, primary energy and fabric energy efficiency rates against the notional home's targets,
/// and each other thing Approved Document L 2026 asks of it.
/// </summary>
public sealed record FhsAssessment(SapModel Model, SapDwelling Notional, SapResult Rating, SapResult Dwelling, SapResult Target,
    SapResult DwellingFabric, SapResult TargetFabric, IReadOnlyList<FhsCheck> Checks)
{
    /// <summary>Whether it meets all of them.</summary>
    public bool Complies => Checks.All(check => check.Met);

    public double DwellingEmissionRate => Dwelling.EmissionRate;
    public double TargetEmissionRate => Target.EmissionRate;
    public double DwellingPrimaryEnergyRate => Dwelling.PrimaryEnergyRate;
    public double TargetPrimaryEnergyRate => Target.PrimaryEnergyRate;
    public double DwellingFabricEnergyEfficiency => DwellingFabric.FabricEnergyEfficiency;
    public double TargetFabricEnergyEfficiency => TargetFabric.FabricEnergyEfficiency;
}

/// <summary>
/// The Future Homes Standard for a new home in England - The Building Regulations etc. (Amendment)
/// (England) Regulations 2026 (S.I. 2026/335), in force from 24 March 2027, with Approved Document L
/// Volume 1, 2026 edition - worked by SAP 10.3, its only approved method so far.
///
/// The home's dwelling emission rate, primary energy rate and fabric energy efficiency against the
/// targets its notional twin sets (SAP 10.3 Appendix R): the same size and shape, its walls at 0.18,
/// floors 0.13, roofs 0.11, windows 1.2 and doors 1.0 W/m²K, its glazing at most a quarter of the
/// floor area, its junctions at Table R2's ψ, 4 m³/h·m² at 50 Pa with decentralised extract, an air
/// source heat pump at 250% heating and hot water, waste water heat recovery on two or more storeys,
/// lighting at 120 lm/W, and its solar array as the home's up to 40% of the ground floor over 4.5.
/// And the limiting U-values and air permeability (Table 3.1), heating and hot water only from fuels
/// at or below 0.086 kgCO2e and 1.969 kWh of primary energy a kWh (4.4), the fans' specific fan power
/// and heat recovery (Table 5.6, 5.55), lighting at 105 lm/W (5.57), and requirement L3's solar output:
/// at least that of 0.22 kWp a m² over 40% of a house's ground floor, facing south-east to south-west
/// at 45° and not shaded (5.73) - taken at south-east, the least that range gives.
/// </summary>
public static class FutureHomesStandard
{
    /// <summary>Table 3.1's limiting U-values, area-weighted, W/m²K, and air permeability, m³/h·m² at 50 Pa.</summary>
    public const double RoofLimit = 0.16, WallLimit = 0.26, FloorLimit = 0.18, PartyWallLimit = 0.20, WindowLimit = 1.6, RooflightLimit = 2.2, DoorLimit = 1.6;
    public const double AirPermeabilityLimit = 8.0;

    /// <summary>Table 5.6's largest specific fan power for a new home, W/(l/s), and the least heat recovery (5.55).</summary>
    public const double DecentralisedExtractLimit = 0.3, CentralExtractLimit = 0.5, BalancedLimit = 1.4, LeastHeatRecovery = 0.73;

    /// <summary>The least efficacy of a fixed light, lumens a circuit-watt (5.57).</summary>
    public const double LeastEfficacy = 105;

    /// <summary>Requirement L3's panels: 0.22 kWp a m² over 40% of the ground floor (5.73, equation 5.1).</summary>
    public const double PanelPower = 0.22, PanelShare = 0.4;

    /// <summary>The fuels heating and hot water may use, at most, kgCO2e/kWh and kWh of primary energy a kWh (4.4).</summary>
    public const double FuelEmissionsLimit = 0.086, FuelPrimaryLimit = 1.969;

    /// <summary>The home in the model checked; null where no heated room is enclosed.</summary>
    public static FhsAssessment? Of(BimDocument document)
    {
        var model = SapSurvey.Of(document);
        return model.Empty ? null : Assess(model);
    }

    /// <summary>A home checked.</summary>
    public static FhsAssessment Assess(SapModel model)
    {
        var home = model.Home;
        var notional = Notional(home);
        var rating = SapCalculation.Run(home, SapPurpose.Rating);
        var dwelling = SapCalculation.Run(home, SapPurpose.Compliance);
        var target = SapCalculation.Run(notional, SapPurpose.Compliance);
        var dwellingFabric = SapCalculation.Run(home, SapPurpose.FabricEfficiency);
        var targetFabric = SapCalculation.Run(notional, SapPurpose.FabricEfficiency);

        var checks = new List<FhsCheck>();
        void Target(string what, double ours, double theirs, string unit)
        {
            var (a, b) = (Math.Round(ours, 2), Math.Round(theirs, 2));
            checks.Add(new FhsCheck(what, a <= b, $"{a:0.00} against a target of {b:0.00} {unit}{(a <= b ? string.Empty : $" - {a - b:0.00} over")}"));
        }

        Target("Dwelling emission rate", dwelling.EmissionRate, target.EmissionRate, "kgCO2e/m²");
        Target("Dwelling primary energy rate", dwelling.PrimaryEnergyRate, target.PrimaryEnergyRate, "kWh/m²");
        Target("Dwelling fabric energy efficiency", dwellingFabric.FabricEnergyEfficiency, targetFabric.FabricEnergyEfficiency, "kWh/m²");

        // Table 3.1: each kind of element's U-value, averaged over its area.
        void Limit(string what, Func<SapElement, bool> kind, double limit)
        {
            var these = home.Elements.Where(kind).Where(element => element.Area > 0).ToList();
            if (these.Count == 0) return;
            var average = these.Sum(element => element.Area * element.U) / these.Sum(element => element.Area);
            var worst = these.MaxBy(element => element.U)!;
            checks.Add(new FhsCheck($"{what}, limiting U-value", Math.Round(average, 2) <= limit,
                $"{average:0.00} W/m²K over {these.Sum(element => element.Area):0.0} m², at most {limit:0.00}" + (worst.U > limit ? $"; the worst {worst.Name} at {worst.U:0.00}" : string.Empty))
            {
                Element = worst.U > limit ? worst.Source : null
            });
        }

        Limit("Roofs", element => element.Kind == SapSurface.Roof, RoofLimit);
        Limit("Walls", element => element.Kind == SapSurface.Wall, WallLimit);
        Limit("Floors", element => element.Kind == SapSurface.Floor, FloorLimit);
        Limit("Party walls", element => element.Kind == SapSurface.PartyWall, PartyWallLimit);
        Limit("Windows, roof windows and curtain walling", element => element.Kind is SapSurface.Window or SapSurface.RoofWindow, WindowLimit);
        Limit("Rooflights", element => element.Kind == SapSurface.Rooflight, RooflightLimit);
        Limit("Doors", element => element.Kind == SapSurface.Door, DoorLimit);
        checks.Add(new FhsCheck("Air permeability", home.AirPermeability <= AirPermeabilityLimit,
            $"{home.AirPermeability:0.0} m³/h·m² at 50 Pa, at most {AirPermeabilityLimit:0.0} - to be shown by a test of every home, as built"));

        // 4.4: heating and hot water from low-carbon fuels only.
        var gas = home.Heating.Source == SapHeatSource.GasBoiler;
        checks.Add(new FhsCheck("Heating and hot water fuel (4.4)", !gas, gas
            ? $"Mains gas, {SapFuel.MainsGas.Emissions:0.000} kgCO2e/kWh and a primary energy factor of {SapFuel.MainsGas.PrimaryEnergy:0.00}: over {FuelEmissionsLimit} and {FuelPrimaryLimit} - a heat pump, direct electric heating or a heat network is wanted"
            : $"{home.Heating.Name}: electricity, {SapFuel.Electricity.Emissions:0.000} kgCO2e/kWh and {SapFuel.Electricity.PrimaryEnergy:0.000}"));

        // Table 5.6 and 5.55: the fans' power and the heat recovered.
        switch (home.Ventilation)
        {
            case SapVentilation.Extract:
                var limit = home.CentralExtract ? CentralExtractLimit : DecentralisedExtractLimit;
                checks.Add(new FhsCheck("Extract ventilation's specific fan power (Table 5.6)", home.SpecificFanPower is > 0 && home.SpecificFanPower <= limit,
                    home.SpecificFanPower > 0 ? $"{home.SpecificFanPower:0.00} W/(l/s), at most {limit:0.0}" : $"not given - at most {limit:0.0} W/(l/s)"));
                break;
            case SapVentilation.HeatRecovery or SapVentilation.Balanced:
                checks.Add(new FhsCheck("Supply and extract ventilation's specific fan power (Table 5.6)", home.SpecificFanPower is > 0 && home.SpecificFanPower <= BalancedLimit,
                    home.SpecificFanPower > 0 ? $"{home.SpecificFanPower:0.00} W/(l/s), at most {BalancedLimit:0.0}" : $"not given - at most {BalancedLimit:0.0} W/(l/s)"));
                checks.Add(new FhsCheck("Heat recovery (5.55)", home.HeatRecovery >= LeastHeatRecovery,
                    $"{home.HeatRecovery:P0}, at least {LeastHeatRecovery:P0}"));
                break;
        }

        if (home.LightingWatts > 0)
        {
            var efficacy = home.LightingLumens / home.LightingWatts;
            checks.Add(new FhsCheck("Fixed lighting's efficacy (5.57)", efficacy >= LeastEfficacy, $"{efficacy:0} lm a circuit-watt on average, each at least {LeastEfficacy:0}"));
        }

        checks.Add(Photovoltaics(home));
        return new FhsAssessment(model, notional, rating, dwelling, target, dwellingFabric, targetFabric, checks);
    }

    /// <summary>
    /// Requirement L3 (5.73): the home's solar arrays' output over a year against that of 0.22 kWp a m²
    /// over 40% of its ground floor, facing south-east at 45° and not shaded; a flat's is the block's to meet (5.74).
    /// </summary>
    public static FhsCheck Photovoltaics(SapDwelling home)
    {
        var output = home.Photovoltaics.Sum(array => 0.8 * array.PeakPower * SapClimate.Annual(array.Orientation, array.Tilt) * array.Shading);
        if (home.Flat)
            return new FhsCheck("On-site electricity (L3, 5.74)", true,
                $"{output:#,0} kWh a year from the arrays on it; a block of flats meets L3 together, its arrays sized to 40% of its ground floor - checked for the block");
        var peak = PanelPower * PanelShare * home.GroundFloorArea;
        var benchmark = 0.8 * peak * SapClimate.Annual(135, 45);
        var met = output >= benchmark - 0.5;
        return new FhsCheck("On-site electricity (L3, 5.73)", met,
            home.Photovoltaics.Count == 0
                ? $"No solar array: L3 asks for {benchmark:#,0} kWh a year - {peak:0.00} kWp facing south-east to south-west at 45° - from a {home.GroundFloorArea:0.0} m² ground floor. Set a roof's Solar PV."
                : $"{output:#,0} kWh a year against {benchmark:#,0} - {peak:0.00} kWp at 45° facing south-east to south-west" +
                  (met ? string.Empty : "; short of it, the arrays have to cover the roof as far as is reasonably practicable (5.73b, Appendix B)"));
    }

    /// <summary>The notional home SAP 10.3's Appendix R sets the targets by, for a home.</summary>
    public static SapDwelling Notional(SapDwelling home)
    {
        // Its openings at most a quarter of the floor area: the doors kept, the glazing cut down in proportion and walled or roofed instead.
        var doors = home.Elements.Where(element => element.Kind == SapSurface.Door).Sum(element => element.Area);
        var glazing = home.Elements.Where(element => element.Glazed).Sum(element => element.Area);
        var most = 0.25 * home.FloorArea;
        var scale = doors + glazing > most && glazing > 0 ? Math.Max(0, most - doors) / glazing : 1;
        var elements = new List<SapElement>();
        double walled = 0, roofed = 0;
        foreach (var element in home.Elements)
        {
            switch (element.Kind)
            {
                case SapSurface.Wall:
                    elements.Add(element with { U = 0.18 });
                    break;
                case SapSurface.PartyWall:
                    elements.Add(element with { U = 0 });
                    break;
                case SapSurface.Floor:
                    elements.Add(element with { U = 0.13 });
                    break;
                case SapSurface.Roof:
                    elements.Add(element with { U = 0.11 });
                    break;
                case SapSurface.Door:
                    elements.Add(element with { U = 1.0 });
                    break;
                case SapSurface.Window:
                    elements.Add(element with { Area = element.Area * scale, U = element.CurtainWall ? 1.3 : 1.2 });
                    walled += element.Area * (1 - scale);
                    break;
                case SapSurface.RoofWindow:
                    elements.Add(element with { Area = element.Area * scale, U = 1.2 + (element.Triple ? 0.2 : 0.3) });
                    roofed += element.Area * (1 - scale);
                    break;
                case SapSurface.Rooflight:
                    elements.Add(element with { Area = element.Area * scale, U = 1.7 });
                    roofed += element.Area * (1 - scale);
                    break;
            }
        }

        if (walled > 0) elements.Add(new SapElement("Wall where the glazing is over a quarter of the floor area", SapSurface.Wall, walled, 0.18));
        if (roofed > 0) elements.Add(new SapElement("Roof where the glazing is over a quarter of the floor area", SapSurface.Roof, roofed, 0.11));

        // A heat pump at 250%, its controls as Table R1 has them; its hot water in a cylinder as the home's, or sized to its demand.
        var heatPump = home.Heating.Source == SapHeatSource.HeatPump;
        var open = home.Storeys == 1 && home.LivingArea > 0.7 * home.FloorArea;
        var notional = home with
        {
            Elements = elements,
            Junctions = home.Junctions.Select(junction => junction with { Psi = SapJunctions.Reference(junction.Code) }).ToList(),
            YValue = 0.05,
            AirPermeability = 4,
            Ventilation = SapVentilation.Extract,
            CentralExtract = false,
            SpecificFanPower = 0.15,
            HeatRecovery = 0,
            OpenChimneys = 0, OpenFlues = 0, IntermittentFans = 0, PassiveVents = 0, FluelessFires = 0,
            Heating = new SapHeating(SapHeatSource.HeatPump, "Air source heat pump, SCOP 2.5")
            {
                SpaceEfficiency = 250, WaterEfficiency = 250, Output = heatPump ? home.Heating.Output : 0,
                Responsiveness = heatPump ? home.Heating.Responsiveness : 1, ControlType = open ? 1 : 3,
                Controls = open ? "Programmer and room thermostat" : "Time and temperature zone control, TRVs"
            },
            Showers = home.Showers.Select(_ => new SapShower(8)).ToList(),
            WaterTarget = true,
            WasteWaterRecovery = home.Storeys > 1 ? 0.5 : 0,
            WasteWaterUtilisation = 0.98,
            LightingLumens = SapCalculation.ReferenceLighting(home.FloorArea, home.Occupancy),
            LightingWatts = SapCalculation.ReferenceLighting(home.FloorArea, home.Occupancy) / 120,
            CooledFraction = 0,
            Seer = 0,
            Battery = 0
        };

        var volume = home.HotWater.CylinderVolume > 0 && home.HotWater.Heater != SapWaterHeater.PointOfUse
            ? home.HotWater.CylinderVolume
            : SapCalculation.HotWaterDemand(notional).OrderBy(litres => litres).ElementAt(8);
        notional = notional with
        {
            HotWater = new SapHotWater(SapWaterHeater.MainSystem, volume) { DeclaredLoss = 0.85 * (0.2 + 0.051 * Math.Pow(volume, 2.0 / 3)) }
        };

        // Its solar array as the home's, up to 40% of the ground floor over 4.5 - a flat's share of it by its storeys.
        var cap = PanelShare * (home.Flat ? home.FloorArea / Math.Max(1, home.Storeys) : home.GroundFloorArea) / 4.5;
        var total = home.Photovoltaics.Sum(array => array.PeakPower);
        if (total > cap && total > 0)
            notional = notional with { Photovoltaics = home.Photovoltaics.Select(array => array with { PeakPower = array.PeakPower * cap / total }).ToList() };
        return notional;
    }
}
