namespace BIMDesigner.Core.Energy;

/// <summary>
/// The junctions SAP counts thermal bridges at, by their reference: what each is, its default ψ when
/// nothing better is known (Table K1), and the notional home's (Table R2), W/m·K.
/// </summary>
public static class SapJunctions
{
    public static readonly IReadOnlyDictionary<string, (string What, double Default, double Reference)> All =
        new Dictionary<string, (string, double, double)>(StringComparer.OrdinalIgnoreCase)
        {
            ["E1"] = ("Steel lintel with perforated steel base plate", 1.00, 0.05),
            ["E2"] = ("Other lintels", 1.00, 0.05),
            ["E3"] = ("Sill", 0.10, 0.05),
            ["E4"] = ("Jamb", 0.10, 0.05),
            ["E5"] = ("Ground floor (normal)", 0.32, 0.16),
            ["E19"] = ("Ground floor (inverted)", 0.10, 0.07),
            ["E20"] = ("Exposed floor (normal)", 0.32, 0.32),
            ["E21"] = ("Exposed floor (inverted)", 0.32, 0.32),
            ["E22"] = ("Basement floor", 0.22, 0.07),
            ["E6"] = ("Intermediate floor within a dwelling", 0.14, 0),
            ["E7"] = ("Party floor between dwellings", 0.28, 0.07),
            ["E8"] = ("Balcony within a dwelling, wall insulation continuous", 0.10, 0),
            ["E9"] = ("Balcony between dwellings, wall insulation continuous", 0.15, 0.02),
            ["E23"] = ("Balcony support penetrating the wall's insulation", 1.00, 0.02),
            ["E10"] = ("Eaves, insulation at ceiling level", 0.12, 0.06),
            ["E24"] = ("Eaves, insulation at ceiling level - inverted", 0.15, 0.24),
            ["E11"] = ("Eaves, insulation at rafter level", 0.15, 0.04),
            ["E12"] = ("Gable, insulation at ceiling level", 0.25, 0.06),
            ["E13"] = ("Gable, insulation at rafter level", 0.25, 0.08),
            ["E14"] = ("Flat roof", 0.16, 0.08),
            ["E15"] = ("Flat roof with parapet", 0.30, 0.56),
            ["E16"] = ("Corner (normal)", 0.18, 0.09),
            ["E17"] = ("Corner (inverted)", 0.00, -0.09),
            ["E18"] = ("Party wall between dwellings", 0.24, 0.06),
            ["E25"] = ("Staggered party wall between dwellings", 0.24, 0.06),
            ["P1"] = ("Party wall at the ground floor", 0.32, 0.08),
            ["P6"] = ("Party wall at the ground floor (inverted)", 0.32, 0.07),
            ["P2"] = ("Party wall at an intermediate floor within a dwelling", 0, 0),
            ["P3"] = ("Party wall at an intermediate floor between dwellings", 0, 0),
            ["P7"] = ("Party wall at an exposed floor (normal)", 0.48, 0.16),
            ["P8"] = ("Party wall at an exposed floor (inverted)", 0.48, 0.24),
            ["P4"] = ("Party wall at the roof, insulation at ceiling level", 0.48, 0.12),
            ["P5"] = ("Party wall at the roof, insulation at rafter level", 0.48, 0.08),
            ["R1"] = ("Head of a roof window", 0.24, 0.08),
            ["R2"] = ("Sill of a roof window", 0.24, 0.06),
            ["R3"] = ("Jamb of a roof window", 0.24, 0.08),
            ["R4"] = ("Ridge (vaulted ceiling)", 0.12, 0.08),
            ["R5"] = ("Ridge (inverted)", 0.12, 0.04),
            ["R6"] = ("Flat ceiling", 0.12, 0.06),
            ["R7"] = ("Flat ceiling (inverted)", 0.12, 0.04),
            ["R8"] = ("Roof to wall (rafter)", 0.12, 0.06),
            ["R9"] = ("Roof to wall (flat ceiling)", 0.32, 0.04),
            ["R10"] = ("All other roof or room-in-roof junctions", 0.32, 0.08),
            ["R11"] = ("Upstand or kerb of a rooflight", 0.24, 0.08)
        };

    /// <summary>A junction's notional ψ (Table R2); none for a reference not in the table.</summary>
    public static double Reference(string code) => All.TryGetValue(code, out var junction) ? junction.Reference : 0;
}
