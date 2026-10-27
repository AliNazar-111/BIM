namespace BIMDesigner.Core.Energy;

/// <summary>
/// What a run of the worksheet is for: the SAP and environmental impact ratings (typical gains);
/// the emission and primary energy rates Approved Document L checks (reduced gains for the heating,
/// Table 5 note 3); or the fabric energy efficiency, under section 11's special conditions.
/// </summary>
public enum SapPurpose
{
    Rating,
    Compliance,
    FabricEfficiency
}

/// <summary>A fuel as SAP 10.3's Table 12 prices it: its standing charge, £/year, its price, p/kWh, and its emissions, kgCO2e/kWh, and primary energy factor.</summary>
public sealed record SapFuel(string Name, double StandingCharge, double Price, double Emissions, double PrimaryEnergy)
{
    public static readonly SapFuel MainsGas = new("Mains gas", 92, 3.64, 0.214, 1.12);
    public static readonly SapFuel Electricity = new("Electricity, standard tariff", 81, 16.49, 0.086, 1.969);
    public static readonly SapFuel Exported = new("Electricity sold to the grid, PV", 0, 5.59, 0.086, 0.969);

    /// <summary>The energy cost deflator (Table 12).</summary>
    public const double Deflator = 0.36;
}

/// <summary>One line of the worksheet: its box, what it is, its value and its unit.</summary>
public sealed record SapLine(string Box, string What, double Value, string Unit);

/// <summary>
/// The worksheet worked through: the home's heat losses and gains, its space heating and cooling, its
/// hot water, the fuel each takes, its lighting, its pumps and fans and its solar electricity - and the
/// cost, emissions and primary energy of it all, with the ratings and rates they give.
/// </summary>
public sealed record SapResult(SapPurpose Purpose, IReadOnlyList<SapLine> Lines)
{
    public double FloorArea { get; init; }

    /// <summary>The heat loss coefficient, W/K, and heat loss parameter, W/m²K, averaged over the year (boxes 39 and 40).</summary>
    public double HeatLossCoefficient { get; init; }
    public double HeatLossParameter { get; init; }

    /// <summary>The year's space heating and cooling, kWh (boxes 98 and 107), and the fabric energy efficiency, kWh/m² (box 109).</summary>
    public double SpaceHeating { get; init; }
    public double SpaceCooling { get; init; }
    public double FabricEnergyEfficiency { get; init; }

    /// <summary>The heat for the hot water, kWh (box 64), and the fuel each use takes, kWh (boxes 211 to 232).</summary>
    public double WaterHeating { get; init; }
    public double SpaceFuel { get; init; }
    public double WaterFuel { get; init; }
    public double ShowerElectricity { get; init; }
    public double CoolingElectricity { get; init; }
    public double PumpsAndFans { get; init; }
    public double Lighting { get; init; }

    /// <summary>The solar electricity used in the home and sold, kWh.</summary>
    public double PhotovoltaicUsed { get; init; }
    public double PhotovoltaicExported { get; init; }

    /// <summary>The year's fuel cost, £ (box 255), the energy cost factor (257) and the SAP rating (258).</summary>
    public double Cost { get; init; }
    public double CostFactor { get; init; }
    public int SapRating { get; init; }

    /// <summary>Its emissions, kgCO2e (272), the emission rate, kgCO2e/m² (273), and the environmental impact rating (274).</summary>
    public double Emissions { get; init; }
    public double EmissionRate { get; init; }
    public int EnvironmentalImpact { get; init; }

    /// <summary>Its primary energy, kWh (286), and primary energy rate, kWh/m² (287).</summary>
    public double PrimaryEnergy { get; init; }
    public double PrimaryEnergyRate { get; init; }

    /// <summary>The solar arrays' output over the year, kWh.</summary>
    public double PhotovoltaicGenerated => PhotovoltaicUsed + PhotovoltaicExported;
}

/// <summary>
/// The SAP 10.3 worksheet (12 June 2026), month by month: its ventilation (section 2), heat losses (3),
/// hot water (4 and Appendix J), internal and solar gains (5, 6, Appendix L), the mean internal
/// temperature and the space heating (7, 8, Tables 9 to 9c), the cooling (8c, Tables 10a and 10b),
/// the fuel each takes (9a), and their costs, emissions and primary energy (10a, 12a, 13a), with the
/// solar electricity used in the home and sold (Appendix M) - in the UK average climate.
///
/// What is taken in place of the Product Characteristics Database: a boiler's or a heat pump's
/// efficiencies as given, a heat pump's with the in-use factors of N3.6 and Table N8; fan powers and a
/// heat recovery unit's efficiency as in use. A heat pump's heating is run for longer as Appendix N's
/// Table N5 has it for its plant size ratio - its output over the design heat loss, taken as 1 where
/// its output is not given.
/// </summary>
public static class SapCalculation
{
    private static readonly double[] ColdWater = { 8.0, 8.2, 9.3, 12.7, 14.6, 16.7, 18.4, 17.6, 16.6, 14.3, 11.1, 8.5 };
    private static readonly double[] HotWaterUse = { 1.10, 1.06, 1.02, 0.98, 0.94, 0.90, 0.90, 0.94, 0.98, 1.02, 1.06, 1.10 };
    private static readonly double[] Behaviour = { 1.035, 1.021, 1.007, 0.993, 0.979, 0.965, 0.965, 0.979, 0.993, 1.007, 1.021, 1.035 };

    // Table N5: the days a heat pump heats for 24 hours and for 16, by its plant size ratio.
    private static readonly double[] PlantSize = { 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2 };
    private static readonly double[] Days24 = { 218, 191, 168, 128, 94, 50, 26, 14, 8, 4, 3 };
    private static readonly double[] Days16 = { 6, 22, 29, 56, 74, 95, 103, 92, 77, 55, 38 };

    /// <summary>The months heated, January first: October to May.</summary>
    public static bool Heated(int month) => month is <= 4 or >= 9;

    /// <summary>The fixed lighting the notional home and the fabric energy efficiency take, lm (Table R1, section 11).</summary>
    public static double ReferenceLighting(double floorArea, double occupancy) => 1139 * Math.Pow(floorArea * occupancy, 0.39);

    /// <summary>The hot water used a day in each month, litres (box 44) - what a notional cylinder is sized to.</summary>
    public static double[] HotWaterDemand(SapDwelling home) => Water(home, false).Daily;

    /// <summary>The SAP rating for an energy cost factor (section 13), not yet rounded.</summary>
    public static double Rating(double costFactor) =>
        costFactor >= 3.5 ? 108.8 - 120.5 * Math.Log10(costFactor) : 100 - 16.21 * costFactor;

    /// <summary>The environmental impact rating for a carbon factor (section 14), not yet rounded.</summary>
    public static double Impact(double carbonFactor) =>
        carbonFactor >= 28.3 ? 200 - 95 * Math.Log10(carbonFactor) : 100 - 1.34 * carbonFactor;

    /// <summary>A rating's band, A to G (Table 14).</summary>
    public static char Band(int rating) => rating switch
    {
        >= 92 => 'A',
        >= 81 => 'B',
        >= 69 => 'C',
        >= 55 => 'D',
        >= 39 => 'E',
        >= 21 => 'F',
        _ => 'G'
    };

    // The hot water: each month's litres a day (44), the heat in it (45), the warm water each mixer shower drains (for its heat recovery) and the electric showers' electricity (64a).
    private static (double[] Daily, double[] Energy, double[] Warm, double[] Electric, double Showers, int Outlets) Water(SapDwelling home, bool fabric)
    {
        var occupants = home.Occupancy;
        var showers = fabric ? new[] { new SapShower(0, 9.3) } : home.Showers.ToArray();
        var baths = fabric ? Math.Max(1, home.Baths) : home.Baths;
        var target = fabric || home.WaterTarget;
        var outlets = showers.Length;
        var perDay = outlets == 0 ? 0 : baths > 0 ? 0.45 * occupants + 0.65 : 0.58 * occupants + 0.83;
        var bathsPerDay = baths == 0 ? outlets > 0 ? 0 : 0.35 * occupants + 0.50 : outlets > 0 ? 0.13 * occupants + 0.19 : 0.35 * occupants + 0.50;
        var other = (9.8 * occupants + 14) * (target ? 0.95 : 1);

        var daily = new double[12];
        var energy = new double[12];
        var warm = new double[12];
        var electric = new double[12];
        for (var m = 0; m < 12; m++)
        {
            var cold = ColdWater[m];
            double shower = 0;
            foreach (var each in showers)
            {
                var uses = perDay / outlets;
                if (each.Electric)
                {
                    electric[m] += uses * Behaviour[m] * Math.Max(9, each.ElectricPower) * 0.1 * SapClimate.Days[m];
                    continue;
                }

                // A new home's showers at 8 l/min at the least (Table J4).
                var litres = Math.Max(8, each.FlowRate) * 6 * Behaviour[m] * uses;
                warm[m] += litres;
                shower += litres * (41.0 - cold) / (52.0 - cold);
            }

            var bath = bathsPerDay * 73 * Behaviour[m] * (target ? 0.95 : 1) * (42.0 - cold) / (52.0 - cold);
            daily[m] = shower + bath + other * HotWaterUse[m];
            energy[m] = 4.18 * daily[m] * SapClimate.Days[m] * (52.0 - cold) / 3600;
        }

        return (daily, energy, warm, electric, perDay, outlets);
    }

    // Table 9a and 10a: how much of the gains go to heating, or of the losses to cooling.
    private static double Utilisation(double gains, double loss, double a, bool cooling = false)
    {
        var gamma = loss == 0 ? 1e6 : Math.Round(gains / loss, 8);
        if (gamma <= 0) return 1;
        if (gamma == 1) return a / (a + 1);
        return cooling ? (1 - Math.Pow(gamma, -a)) / (1 - Math.Pow(gamma, -(a + 1))) : (1 - Math.Pow(gamma, a)) / (1 - Math.Pow(gamma, a + 1));
    }

    /// <summary>Works the worksheet through for a home, for one purpose.</summary>
    public static SapResult Run(SapDwelling home, SapPurpose purpose)
    {
        var lines = new List<SapLine>();
        void Line(string box, string what, double value, string unit) => lines.Add(new SapLine(box, what, value, unit));

        var fabric = purpose == SapPurpose.FabricEfficiency;
        var reduced = purpose != SapPurpose.Rating;
        var area = Math.Max(home.FloorArea, 1);
        var volume = Math.Max(home.Volume, 1);
        var occupants = home.Occupancy;
        Line("4", "Total floor area", area, "m²");
        Line("5", "Volume", volume, "m³");
        Line("42", "Assumed occupancy", occupants, "people");

        // 2. Ventilation: what the openings let through, the envelope's leakage sheltered and with the wind, and the system's.
        // Checked against the regulations, a home ventilated naturally is taken no tighter than 3 m³/h·m² (2.3).
        var system = fabric ? SapVentilation.Extract : home.Ventilation;
        var permeability = purpose == SapPurpose.Compliance && system == SapVentilation.Natural ? Math.Max(3, home.AirPermeability) : home.AirPermeability;
        var openings = fabric ? 0 : home.OpenChimneys * 80 + home.OpenFlues * 20 + home.IntermittentFans * 10 + home.PassiveVents * 10 + home.FluelessFires * 40;
        var infiltration = permeability / 20 + openings / volume;
        var sheltered = infiltration * (1 - 0.075 * Math.Clamp(home.ShelteredSides, 0, 4));
        var fanPower = fabric ? 0.15 : home.SpecificFanPower > 0 ? home.SpecificFanPower : system == SapVentilation.Extract ? 0.8 : 2.0;
        var recovery = home.HeatRecovery > 0 ? home.HeatRecovery : 0.66;
        Line("8", "Air changes through chimneys, flues, fans and vents", openings / volume, "ach");
        Line("18", "Infiltration rate", infiltration, "ach");
        Line("21", "Infiltration rate with the sheltered sides", sheltered, "ach");

        var airChanges = new double[12];
        var ventilationLoss = new double[12];
        for (var m = 0; m < 12; m++)
        {
            var leak = sheltered * SapClimate.Wind[m] / 4;
            airChanges[m] = system switch
            {
                SapVentilation.HeatRecovery => leak + 0.5 * (1 - recovery),
                SapVentilation.Balanced => leak + 0.5,
                SapVentilation.Extract => leak < 0.25 ? 0.5 : leak + 0.25,
                _ => leak >= 1 ? leak : 0.5 + leak * leak * 0.5
            };
            ventilationLoss[m] = 0.33 * airChanges[m] * volume;
        }

        Line("25", "Effective air change rate, the year's mean", airChanges.Average(), "ach");

        // 3. Heat losses: each surface's area by its U-value - glazing with curtains allowed for - and the thermal bridges.
        double exposed = 0, transmission = 0;
        foreach (var element in home.Elements)
        {
            var u = element.Glazed ? 1 / (1 / element.U + 0.04) : element.U;
            transmission += element.Area * u;
            if (element.Kind != SapSurface.PartyWall) exposed += element.Area;
        }

        var bridging = home.Junctions.Count > 0 ? home.Junctions.Sum(junction => junction.Length * junction.Psi) : home.YValue * exposed;
        var fabricLoss = transmission + bridging;
        Line("31", "Area of the elements facing outside", exposed, "m²");
        Line("33", "Fabric heat loss, ΣAU", transmission, "W/K");
        Line("35", "Thermal mass parameter", home.ThermalMass, "kJ/m²K");
        Line("36", "Thermal bridges", bridging, "W/K");
        Line("37", "Total fabric heat loss", fabricLoss, "W/K");
        var coefficient = new double[12];
        for (var m = 0; m < 12; m++) coefficient[m] = fabricLoss + ventilationLoss[m];
        Line("38", "Ventilation heat loss, the year's mean", ventilationLoss.Average(), "W/K");
        Line("39", "Heat transfer coefficient, the year's mean", coefficient.Average(), "W/K");
        Line("40", "Heat loss parameter", coefficient.Average() / area, "W/m²K");

        // 4. Hot water: what is used and the heat in it, what is lost on the way and in store, and what is recovered from the showers.
        var (daily, waterEnergy, warm, showerElectric, _, _) = Water(home, fabric);
        var water = home.HotWater;
        var pointOfUse = water.Heater == SapWaterHeater.PointOfUse;
        var cylinder = !fabric && !pointOfUse && water.CylinderVolume > 0;
        double storeLoss = 0;
        if (cylinder)
        {
            var factor = 0.6 * (water.Cylinderstat ? 1 : 1.3) * (water.SeparateTiming && water.Heater == SapWaterHeater.MainSystem ? 0.9 : 1);
            storeLoss = water.DeclaredLoss is { } declared
                ? declared * factor
                : (0.005 + 0.55 / (water.Insulation + 4)) * water.CylinderVolume * Math.Pow(120 / water.CylinderVolume, 1.0 / 3) * factor;
        }

        var heats = home.Heating.Source;
        var primaryCircuit = cylinder && water.Heater == SapWaterHeater.MainSystem && (heats == SapHeatSource.HeatPump || heats == SapHeatSource.GasBoiler && !home.Heating.Combi);
        var combi = !fabric && water.Heater == SapWaterHeater.MainSystem && heats == SapHeatSource.GasBoiler && home.Heating.Combi && water.CylinderVolume <= 0;
        var output = new double[12];
        var waterGains = new double[12];
        double distributionTotal = 0, storageTotal = 0, primaryTotal = 0, combiTotal = 0, recoveredTotal = 0;
        for (var m = 0; m < 12; m++)
        {
            var days = SapClimate.Days[m];
            var distribution = fabric || pointOfUse ? 0 : 0.15 * waterEnergy[m];
            var stored = storeLoss * days;
            var hours = m is >= 5 and <= 8 ? 3 : !water.Cylinderstat ? 11 : water.SeparateTiming ? 3 : 5;
            var insulated = water.InsulatedPrimary ? 1.0 : 0.0;
            var circuit = primaryCircuit ? days * 14 * ((0.0091 * insulated + 0.0245 * (1 - insulated)) * hours + 0.0263) : 0;
            var combiLoss = combi ? 600 * Math.Min(1, daily[m] / 100) * days / 365 : 0;
            var recovered = !fabric && home.WasteWaterRecovery > 0
                ? warm[m] * days * (35 - ColdWater[m]) * 4.18 / 3600 * home.WasteWaterRecovery * home.WasteWaterUtilisation
                : 0;
            output[m] = Math.Max(0, 0.85 * waterEnergy[m] + distribution + stored + circuit + combiLoss - recovered);
            waterGains[m] = 0.25 * (0.85 * waterEnergy[m] + combiLoss + showerElectric[m]) + 0.8 * (distribution + stored + circuit);
            distributionTotal += distribution;
            storageTotal += stored;
            primaryTotal += circuit;
            combiTotal += combiLoss;
            recoveredTotal += recovered;
        }

        Line("43", "Hot water used a day, the year's mean", daily.Select((litres, m) => litres * SapClimate.Days[m]).Sum() / 365, "litres");
        Line("45", "Heat in the hot water used", waterEnergy.Sum(), "kWh");
        Line("46", "Distribution loss", distributionTotal, "kWh");
        Line("57", "Storage loss", storageTotal, "kWh");
        Line("59", "Primary circuit loss", primaryTotal, "kWh");
        Line("61", "Combi loss", combiTotal, "kWh");
        Line("63a", "Recovered from the showers' waste water", -recoveredTotal, "kWh");
        Line("64", "Output of the water heater", output.Sum(), "kWh");
        Line("64a", "Electric showers", showerElectric.Sum(), "kWh");

        // 5. Internal gains: people, lights, appliances, cooking, pumps and fans, the water's, less evaporation.
        var baseLight = 11.2 * 59.73 * Math.Pow(area * occupants, 0.4714);
        var overshading = reduced && home.Overshading == SapOvershading.VeryLittle ? SapOvershading.AverageOrUnknown : home.Overshading;
        var (winter, summer, light) = overshading switch
        {
            SapOvershading.Heavy => (0.3, 0.5, 0.5),
            SapOvershading.MoreThanAverage => (0.54, 0.7, 0.67),
            SapOvershading.VeryLittle => (1.0, 1.0, 1.0),
            _ => (0.77, 0.9, 0.83)
        };
        var glazing = home.Elements.Where(element => element.Glazed).ToList();
        var daylight = glazing.Sum(element => 0.9 * element.Area * element.LightTransmittance * element.FrameFactor * (element.Kind == SapSurface.Window ? light : 1.0)) / area;
        var daylightFactor = daylight <= 0.095 ? 52.2 * daylight * daylight - 9.94 * daylight + 1.433 : 0.96;
        var required = 2.0 / 3 * baseLight * daylightFactor;
        var lumens = fabric ? ReferenceLighting(area, occupants) : home.LightingLumens;
        var watts = fabric ? lumens / 120 : home.LightingWatts;
        var capacity = lumens > 0 ? lumens : 185 * area;
        var efficacy = lumens > 0 && watts > 0 ? lumens / watts : 21.3;
        var provided = required * capacity / (330 * area);
        var topUp = provided < required / 3 ? required / 3 - provided : 0;
        var lighting = Math.Max(required, provided) / efficacy + topUp / 21.3 + baseLight * daylightFactor / 3 / 21.3;
        var appliances = 207.8 * Math.Pow(area * occupants, 0.4714);
        var cookingElectric = 138 + 28 * occupants;

        var lightingMonthly = new double[12];
        var appliancesMonthly = new double[12];
        var gains = new double[12];
        var coolingGains = new double[12];
        var pumpGains = new double[12];
        var heatsByPump = heats == SapHeatSource.GasBoiler;
        for (var m = 0; m < 12; m++)
        {
            var days = SapClimate.Days[m];
            var hoursIn = 24.0 * days;
            var lightShape = (1 + 0.5 * Math.Cos(2 * Math.PI * (m + 1 - 0.2) / 12)) * days / 365;
            lightingMonthly[m] = lighting * lightShape;
            appliancesMonthly[m] = appliances * (1 + 0.157 * Math.Cos(2 * Math.PI * (m + 1 - 1.78) / 12)) * days / 365;

            var lightTypical = lightingMonthly[m] * 0.85 * 1000 / hoursIn;
            var lightReduced = baseLight * daylightFactor / 80 * lightShape * 0.85 * 1000 / hoursIn;
            var applianceTypical = appliancesMonthly[m] * 1000 / hoursIn;
            var waterHeat = 1000 * waterGains[m] / hoursIn;
            pumpGains[m] = fabric ? 0
                : (heatsByPump && Heated(m) ? 3 : 0) + (home.Ventilation == SapVentilation.Balanced ? fanPower * 0.06 * volume : 0);

            gains[m] = (reduced ? 50 : 60) * occupants + (reduced ? lightReduced : lightTypical) + (reduced ? 0.67 : 1) * applianceTypical
                       + (reduced ? 23 + 5 * occupants : 35 + 7 * occupants) + pumpGains[m] - 40 * occupants + waterHeat;
            coolingGains[m] = 60 * occupants + lightTypical + applianceTypical + 35 + 7 * occupants - 40 * occupants + waterHeat;
        }

        Line("73", "Internal gains, the year's mean", gains.Average(), "W");

        // 6. Solar gains through the glazing: a roof window's at its pitch, a rooflight's on the level, and nothing shading either.
        double SolarGain(int m, double access) => glazing.Sum(element =>
        {
            var (orientation, tilt) = element.Kind switch
            {
                SapSurface.Rooflight => (180.0, 0.0),
                SapSurface.RoofWindow when element.Tilt < 70 => (element.Orientation, element.Tilt),
                _ => (element.Orientation, 90.0)
            };
            var factor = element.Kind == SapSurface.Window ? access : 1.0;
            return 0.9 * element.Area * SapClimate.Flux(orientation, tilt, m) * element.SolarTransmittance * element.FrameFactor * factor;
        });

        var solar = new double[12];
        for (var m = 0; m < 12; m++) solar[m] = SolarGain(m, winter);
        Line("83", "Solar gains, the year's mean", solar.Average(), "W");

        // 7. The mean internal temperature: the living area and the rest, each heated to Table 9's times, cooling off in between.
        var heating = home.Heating;
        var control = fabric ? 2 : heating.ControlType;
        var responsiveness = fabric ? 1 : heating.Responsiveness;
        var adjustment = fabric ? 0 : heating.TemperatureAdjustment;
        var living = Math.Clamp(home.LivingArea / area, 0, 1);
        var mass = home.ThermalMass;

        // A heat pump's longer heating (Appendix N, Table N5), the days given to the coldest months first.
        var long24 = new double[12];
        var long16 = new double[12];
        if (!fabric && heats == SapHeatSource.HeatPump)
        {
            var design = coefficient.Average() * 24.2;
            var ratio = heating.Output > 0 && design > 0 ? heating.Output / design : 1;
            double Interpolate(double[] values)
            {
                if (ratio <= PlantSize[0]) return values[0];
                for (var i = 1; i < PlantSize.Length; i++)
                    if (ratio <= PlantSize[i]) return values[i - 1] + (values[i] - values[i - 1]) * (ratio - PlantSize[i - 1]) / (PlantSize[i] - PlantSize[i - 1]);
                return values[^1];
            }

            var left24 = Math.Round(Interpolate(Days24));
            var left16 = Math.Round(Interpolate(Days16));
            foreach (var m in new[] { 0, 11, 1, 2, 10, 3, 9, 4 })
            {
                long24[m] = Math.Min(left24, SapClimate.Days[m]);
                left24 -= long24[m];
                long16[m] = Math.Min(left16, SapClimate.Days[m] - long24[m]);
                left16 -= long16[m];
            }

            Line("N5", "Plant size ratio", ratio, string.Empty);
        }

        var meanTemperature = new double[12];
        var spaceHeating = new double[12];
        for (var m = 0; m < 12; m++)
        {
            var h = coefficient[m];
            var parameter = h / area;
            var tau = mass / (3.6 * parameter);
            var a = 1 + tau / 15;
            var outside = SapClimate.External[m];
            var total = gains[m] + solar[m];
            var capped = Math.Min(parameter, 6);
            var rest = control == 1 ? 21 - 0.5 * capped : 21 - capped + capped * capped / 12;

            // Table 9b: how far it falls while the heating is off, and Table 9c: the mean over the day.
            double Zone(double heated, double[] off)
            {
                var useful = Utilisation(total, h * (heated - outside), a);
                var timeConstant = 4 + 0.25 * tau;
                var unheated = (1 - responsiveness) * (heated - 2) + responsiveness * (outside + useful * total / h);
                double Fall(double hours) => hours <= timeConstant
                    ? 0.5 * hours * hours * (heated - unheated) / (24 * timeConstant)
                    : (heated - unheated) * (hours - 0.5 * timeConstant) / 24;
                var bimodal = heated - off.Sum(Fall);
                var unimodal = heated - Fall(8);
                var days = SapClimate.Days[m];
                return (long24[m] * heated + long16[m] * unimodal + (days - long24[m] - long16[m]) * bimodal) / days;
            }

            var first = Zone(21, new[] { 7.0, 8.0 });
            var second = Zone(rest, control == 3 ? new[] { 9.0, 8.0 } : new[] { 7.0, 8.0 });
            meanTemperature[m] = living * first + (1 - living) * second + adjustment;
            var loss = h * (meanTemperature[m] - outside);
            var useful = Utilisation(total, loss, a) * total;
            var need = 0.024 * (loss - useful) * SapClimate.Days[m];
            spaceHeating[m] = Heated(m) && need >= 1 ? need : 0;
        }

        Line("91", "Living area fraction", living, string.Empty);
        Line("93", "Mean internal temperature, January", meanTemperature[0], "°C");
        var heatingTotal = spaceHeating.Sum();
        Line("98", "Space heating requirement", heatingTotal, "kWh");
        Line("99", "Space heating requirement a square metre", heatingTotal / area, "kWh/m²");

        // 8c. Cooling, June to August, to 24 °C: the whole home for the fabric energy efficiency, otherwise what is cooled.
        var cooled = fabric ? 1 : home.Seer > 0 ? Math.Clamp(home.CooledFraction, 0, 1) : 0;
        double coolingTotal = 0;
        if (cooled > 0)
            for (var m = 5; m <= 7; m++)
            {
                var h = coefficient[m];
                var tau = mass / (3.6 * h / area);
                var a = 1 + tau / 15;
                var total = coolingGains[m] + SolarGain(m, summer);
                var loss = h * (24 - SapClimate.External[m]);
                var need = 0.024 * (total - Utilisation(total, loss, a, cooling: true) * loss) * SapClimate.Days[m] * cooled * 0.25;
                if (need >= 1) coolingTotal += need;
            }

        Line("107", "Space cooling requirement", coolingTotal, "kWh");
        var fabricEfficiency = heatingTotal / area + coolingTotal / area;
        if (fabric)
        {
            Line("109", "Fabric energy efficiency", fabricEfficiency, "kWh/m²");
            return new SapResult(purpose, lines)
            {
                FloorArea = area, HeatLossCoefficient = coefficient.Average(), HeatLossParameter = coefficient.Average() / area,
                SpaceHeating = heatingTotal, SpaceCooling = coolingTotal, FabricEnergyEfficiency = fabricEfficiency
            };
        }

        // 9a. The fuel: the space heating at the main system's efficiency, the hot water at the water heater's.
        var spaceEfficiency = heats switch
        {
            SapHeatSource.GasBoiler => heating.SpaceEfficiency - (heating.RoomThermostat ? 0 : 5),
            SapHeatSource.HeatPump => heating.SpaceEfficiency * 0.95,
            _ => 100
        };
        var spaceFuel = new double[12];
        var waterFuel = new double[12];
        for (var m = 0; m < 12; m++)
        {
            spaceFuel[m] = spaceHeating[m] * 100 / spaceEfficiency;
            double efficiency = 100;
            if (water.Heater == SapWaterHeater.MainSystem && heats == SapHeatSource.GasBoiler)
            {
                var (cold, warmer) = (heating.WaterEfficiency, heating.SpaceEfficiency);
                efficiency = cold >= warmer || spaceHeating[m] + output[m] <= 0
                    ? cold
                    : (spaceHeating[m] + output[m]) / (spaceHeating[m] / warmer + output[m] / cold);
            }
            else if (water.Heater == SapWaterHeater.MainSystem && heats == SapHeatSource.HeatPump)
                efficiency = Math.Max(100, heating.WaterEfficiency * water.HeatPumpInUse);
            waterFuel[m] = output[m] * 100 / efficiency;
        }

        var coolingFuel = cooled > 0 ? coolingTotal / home.Seer : 0;
        var fans = system switch
        {
            SapVentilation.Extract => fanPower * 1.22 * volume,
            SapVentilation.Balanced or SapVentilation.HeatRecovery => fanPower * 2.44 * 0.5 * volume,
            _ => 0
        };
        var pump = heats == SapHeatSource.GasBoiler ? 41 * (heating.RoomThermostat ? 1 : 1.3) : 0;
        var flueFan = heats == SapHeatSource.GasBoiler ? 45 : 0;
        var auxiliary = fans + pump + flueFan;
        var lightingTotal = lightingMonthly.Sum();
        Line("206", "Efficiency of the main heating", spaceEfficiency, "%");
        Line("211", "Space heating fuel", spaceFuel.Sum(), "kWh");
        Line("217", "Efficiency of the water heater, the year's", output.Sum() > 0 ? output.Sum() / waterFuel.Sum() * 100 : 100, "%");
        Line("219", "Water heating fuel", waterFuel.Sum(), "kWh");
        Line("221", "Space cooling electricity", coolingFuel, "kWh");
        Line("230a", "Ventilation fans", fans, "kWh");
        Line("230c", "Heating circulation pump", pump, "kWh");
        Line("230e", "Boiler flue fan", flueFan, "kWh");
        Line("232", "Lighting", lightingTotal, "kWh");

        // Appendix M: the solar electricity, month by month, and the share used in the home - more with a battery.
        var electricSpace = heats != SapHeatSource.GasBoiler;
        var electricWater = water.Heater != SapWaterHeater.MainSystem || heats != SapHeatSource.GasBoiler;
        double used = 0, sold = 0;
        var battery = Math.Min(Math.Max(home.Battery, 0), 15);
        var (c1, c2, c3) = (1.610 - 0.0973 * battery, 0.415 - 0.00776 * battery, 0.511 + 0.0866 * battery);
        for (var m = 0; m < 12; m++)
        {
            var generated = home.Photovoltaics.Sum(array =>
            {
                var yearly = SapClimate.Annual(array.Orientation, array.Tilt);
                return yearly <= 0 ? 0 : 0.8 * array.PeakPower * yearly * array.Shading * (0.024 * SapClimate.Days[m] * SapClimate.Flux(array.Orientation, array.Tilt, m) / yearly);
            });
            if (generated <= 0) continue;
            var demand = lightingMonthly[m] + appliancesMonthly[m] + cookingElectric * SapClimate.Days[m] / 365 + showerElectric[m] + auxiliary * SapClimate.Days[m] / 365
                         + (electricSpace ? spaceFuel[m] : 0) + (electricWater ? waterFuel[m] : 0);
            var share = demand > 0 ? Math.Min(Math.Exp(-c1 * Math.Pow(generated / demand * c2, c3)), demand / generated) : 0;
            used += generated * share;
            if (home.Export) sold += generated * (1 - share);
        }

        Line("233", "Solar electricity used in the home", -used, "kWh");
        Line("233b", "Solar electricity sold to the grid", -sold, "kWh");

        // 10a-13a. The costs, emissions and primary energy - regulated uses only, so no electricity standing charge.
        var spaceFuelKind = heats == SapHeatSource.GasBoiler ? SapFuel.MainsGas : SapFuel.Electricity;
        var waterFuelKind = water.Heater == SapWaterHeater.MainSystem && heats == SapHeatSource.GasBoiler ? SapFuel.MainsGas : SapFuel.Electricity;
        var electricity = SapFuel.Electricity;
        var electricUses = showerElectric.Sum() + coolingFuel + auxiliary + lightingTotal;
        double Sum(Func<SapFuel, double> of) =>
            spaceFuel.Sum() * of(spaceFuelKind) + waterFuel.Sum() * of(waterFuelKind) + electricUses * of(electricity) - used * of(electricity) - sold * of(SapFuel.Exported);
        var standing = spaceFuelKind == SapFuel.MainsGas || waterFuelKind == SapFuel.MainsGas ? SapFuel.MainsGas.StandingCharge : 0;
        var cost = Sum(fuel => fuel.Price) * 0.01 + standing;
        var costFactor = cost * SapFuel.Deflator / (area + 45);
        var rating = Math.Max(1, (int)Math.Round(Rating(costFactor), MidpointRounding.AwayFromZero));
        var emissions = Sum(fuel => fuel.Emissions);
        var impact = Math.Max(1, (int)Math.Round(Impact(emissions / (area + 45)), MidpointRounding.AwayFromZero));
        var primary = Sum(fuel => fuel.PrimaryEnergy);
        Line("255", "Total energy cost", cost, "£");
        Line("257", "Energy cost factor", costFactor, string.Empty);
        Line("258", "SAP rating", rating, string.Empty);
        Line("272", "Total emissions", emissions, "kgCO2e");
        Line("273", "Emission rate", emissions / area, "kgCO2e/m²");
        Line("274", "Environmental impact rating", impact, string.Empty);
        Line("286", "Total primary energy", primary, "kWh");
        Line("287", "Primary energy rate", primary / area, "kWh/m²");

        return new SapResult(purpose, lines)
        {
            FloorArea = area, HeatLossCoefficient = coefficient.Average(), HeatLossParameter = coefficient.Average() / area,
            SpaceHeating = heatingTotal, SpaceCooling = coolingTotal, FabricEnergyEfficiency = fabricEfficiency,
            WaterHeating = output.Sum(), SpaceFuel = spaceFuel.Sum(), WaterFuel = waterFuel.Sum(), ShowerElectricity = showerElectric.Sum(),
            CoolingElectricity = coolingFuel, PumpsAndFans = auxiliary, Lighting = lightingTotal, PhotovoltaicUsed = used, PhotovoltaicExported = sold,
            Cost = cost, CostFactor = costFactor, SapRating = rating, Emissions = emissions, EmissionRate = emissions / area, EnvironmentalImpact = impact,
            PrimaryEnergy = primary, PrimaryEnergyRate = primary / area
        };
    }
}
