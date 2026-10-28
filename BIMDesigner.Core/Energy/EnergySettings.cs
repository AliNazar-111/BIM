namespace BIMDesigner.Core.Energy;

/// <summary>
/// How the junctions' ψ-values are taken: SAP's defaults (Table K1); the notional home's (Table R2) -
/// what junctions detailed and calculated to BR 497 commonly come to, to be shown when they are; or a
/// y-value over the area facing outside, with no junctions measured.
/// </summary>
public enum ThermalBridging
{
    ReferenceDetails,
    Defaults,
    YValue
}

/// <summary>
/// What the energy rating asks of a project that its model does not say: how sheltered it is and how
/// shaded its windows are, how its thermal bridges are taken, any waste water heat recovery on its
/// showers, a battery for its solar electricity, and whether it is a flat.
/// </summary>
public sealed class EnergySettings
{
    public SapOvershading Overshading { get; set; } = SapOvershading.AverageOrUnknown;

    /// <summary>Its sides sheltered by buildings or trees as tall as it within ten times its height, 0 to 4 (box 19).</summary>
    public int ShelteredSides { get; set; } = 2;

    public ThermalBridging Bridging { get; set; } = ThermalBridging.ReferenceDetails;

    /// <summary>The y-value taken where the bridging is a y-value, W/m²K.</summary>
    public double YValue { get; set; } = 0.15;

    /// <summary>Each junction's own ψ, W/m·K, by its reference in Table K1 - from calculated details; what is not here is taken by <see cref="Bridging"/>.</summary>
    public Dictionary<string, double> Psi { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instantaneous waste water heat recovery on the showers: its efficiency, 0 to 1, and utilisation; none at 0.</summary>
    public double WasteWaterRecovery { get; set; }
    public double WasteWaterUtilisation { get; set; } = 0.98;

    /// <summary>A battery's usable capacity for the solar electricity, kWh; none at 0.</summary>
    public double Battery { get; set; }

    /// <summary>Whether the home is a flat - its solar array sized to its share of the block's ground floor.</summary>
    public bool Flat { get; set; }

    /// <summary>Where the home is for Approved Document O - by its postcode, here or in the project's address, or as set.</summary>
    public OverheatingLocation OverheatingLocation { get; set; }

    public string Postcode { get; set; } = string.Empty;

    /// <summary>Whether its glazing has external shutters with means of ventilation, lockable - shading it and keeping it secure open at night (Approved Document O 1.9, 3.7).</summary>
    public bool Shutters { get; set; }

    /// <summary>Whether noise at night will keep its windows shut - as the planning authority found - so only dynamic thermal modelling can show it does not overheat (3.1 to 3.3).</summary>
    public bool NoisyAtNight { get; set; }

    /// <summary>A copy, to change and put back as one step.</summary>
    public EnergySettings Copy()
    {
        var copy = new EnergySettings
        {
            Overshading = Overshading, ShelteredSides = ShelteredSides, Bridging = Bridging, YValue = YValue,
            WasteWaterRecovery = WasteWaterRecovery, WasteWaterUtilisation = WasteWaterUtilisation, Battery = Battery, Flat = Flat,
            OverheatingLocation = OverheatingLocation, Postcode = Postcode, Shutters = Shutters, NoisyAtNight = NoisyAtNight
        };
        foreach (var (code, psi) in Psi) copy.Psi[code] = psi;
        return copy;
    }

    /// <summary>Whether another's settings are the same as these.</summary>
    public bool SameAs(EnergySettings other) =>
        Overshading == other.Overshading && ShelteredSides == other.ShelteredSides && Bridging == other.Bridging && YValue.Equals(other.YValue)
        && WasteWaterRecovery.Equals(other.WasteWaterRecovery) && WasteWaterUtilisation.Equals(other.WasteWaterUtilisation) && Battery.Equals(other.Battery)
        && Flat == other.Flat && Psi.Count == other.Psi.Count && Psi.All(entry => other.Psi.TryGetValue(entry.Key, out var psi) && psi.Equals(entry.Value))
        && OverheatingLocation == other.OverheatingLocation && Postcode == other.Postcode && Shutters == other.Shutters && NoisyAtNight == other.NoisyAtNight;

    /// <summary>Takes on another's settings.</summary>
    public void CopyFrom(EnergySettings other)
    {
        Overshading = other.Overshading;
        ShelteredSides = other.ShelteredSides;
        Bridging = other.Bridging;
        YValue = other.YValue;
        WasteWaterRecovery = other.WasteWaterRecovery;
        WasteWaterUtilisation = other.WasteWaterUtilisation;
        Battery = other.Battery;
        Flat = other.Flat;
        OverheatingLocation = other.OverheatingLocation;
        Postcode = other.Postcode;
        Shutters = other.Shutters;
        NoisyAtNight = other.NoisyAtNight;
        Psi.Clear();
        foreach (var (code, psi) in other.Psi) Psi[code] = psi;
    }
}

/// <summary>A project's energy settings changed, as one step to undo.</summary>
public sealed class SetEnergySettingsCommand : Documents.Commands.IUndoableCommand
{
    private readonly EnergySettings _settings;
    private readonly EnergySettings _before;
    private readonly EnergySettings _after;

    public SetEnergySettingsCommand(EnergySettings settings, EnergySettings after)
    {
        _settings = settings;
        _before = settings.Copy();
        _after = after.Copy();
    }

    public string Name => "Energy Settings";

    public void Redo() => _settings.CopyFrom(_after);

    public void Undo() => _settings.CopyFrom(_before);
}
