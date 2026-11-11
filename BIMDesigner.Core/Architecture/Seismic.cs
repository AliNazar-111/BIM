using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Architecture;

/// <summary>A site's class by what it stands on, ASCE 7-16 Chapter 20: hard rock to soft clay, and F for ground wanting its own study. D where nothing is known of it (11.4.3).</summary>
public enum SiteClass
{
    A,
    B,
    C,
    D,
    E,
    F
}

/// <summary>A seismic force-resisting system of ASCE 7-16 Table 12.2-1: its R, Ω0 and Cd, and the seismic design categories it is not permitted in.</summary>
public sealed record SeismicSystem(string Name, double R, double Overstrength, double Cd, string NotPermittedFrom);

/// <summary>
/// A building's seismic design by the equivalent lateral force procedure of ASCE 7-16 12.8: its site's figures and the design spectrum's, its category, its system, its period and Cs, and whether it applies.
/// The base shear is Cs times the effective seismic weight, which the analysis takes from the model's own dead load. Drift is worked from Cs without (12.8-5)'s least, 12.8.6.1. Floors and roofs of
/// timber are flexible diaphragms, 12.3.1.1, and take no accidental torsion; concrete ones are not, and do.
/// </summary>
public sealed record SeismicDesign(
    double Ss, double S1, SiteClass Site, int Risk, double Fa, double Fv, double Sds, double Sd1, string Category, string IrcCategory,
    SeismicSystem System, double Importance, double Height, double Period, double Exponent, double Coefficient, string CoefficientSaid, double Redundancy,
    bool Applies, string Said, double DriftCoefficient = 0, bool Flexible = true, string RedundancySaid = "")
{
    /// <summary>Whether its accidental torsion is amplified where it is torsionally irregular, 12.8.4.3: categories C to F.</summary>
    public bool Amplifies => Category is "C" or "D" or "E" or "F";
}

/// <summary>
/// Seismic loads for a US project by ASCE 7-16, Supplement 1's site coefficients taken in. In England Approved Document A 5.5 does not usually ask for seismic design for consequence classes 1, 2a and 2b, so none is made.
/// </summary>
public static class Seismic
{
    private static readonly double[] SsAt = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5 };
    private static readonly double[] S1At = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6 };

    // ASCE 7-16 Tables 11.4-1 and 11.4-2 as Supplement 1 has them. Site Class E past its first columns, and F, want Section 11.4.8.
    private static readonly Dictionary<SiteClass, double[]> FaTable = new()
    {
        [SiteClass.A] = new[] { 0.8, 0.8, 0.8, 0.8, 0.8, 0.8 },
        [SiteClass.B] = new[] { 0.9, 0.9, 0.9, 0.9, 0.9, 0.9 },
        [SiteClass.C] = new[] { 1.3, 1.3, 1.2, 1.2, 1.2, 1.2 },
        [SiteClass.D] = new[] { 1.6, 1.4, 1.2, 1.1, 1.0, 1.0 },
        [SiteClass.E] = new[] { 2.4, 1.7, 1.3, double.NaN, double.NaN, double.NaN }
    };

    private static readonly Dictionary<SiteClass, double[]> FvTable = new()
    {
        [SiteClass.A] = new[] { 0.8, 0.8, 0.8, 0.8, 0.8, 0.8 },
        [SiteClass.B] = new[] { 0.8, 0.8, 0.8, 0.8, 0.8, 0.8 },
        [SiteClass.C] = new[] { 1.5, 1.5, 1.5, 1.5, 1.5, 1.4 },
        [SiteClass.D] = new[] { 2.4, 2.2, 2.0, 1.9, 1.8, 1.7 },
        [SiteClass.E] = new[] { 4.2, 3.3, 2.8, 2.4, 2.2, 2.0 }
    };

    private static double Between(double[] at, double[] values, double x)
    {
        if (x <= at[0]) return values[0];
        for (var i = 1; i < at.Length; i++)
            if (x <= at[i]) return values[i - 1] + (values[i] - values[i - 1]) * (x - at[i - 1]) / (at[i] - at[i - 1]);
        return values[^1];
    }

    /// <summary>Fa by Table 11.4-1, straight lines between columns. Site Class E at Ss of 1.0 or more takes Site Class C's, by 11.4.8's first exception; F has none.</summary>
    public static double? Fa(SiteClass site, double ss)
    {
        if (site == SiteClass.F) return null;
        if (site == SiteClass.E && ss >= 1.0) return Between(SsAt, FaTable[SiteClass.C], ss);
        var value = Between(SsAt, FaTable[site], ss);
        return double.IsNaN(value) ? null : value;
    }

    /// <summary>Fv by Table 11.4-2, straight lines between columns; none for Site Class F.</summary>
    public static double? Fv(SiteClass site, double s1) => site == SiteClass.F ? null : Between(S1At, FvTable[site], s1);

    /// <summary>The seismic design category by Tables 11.6-1 and 11.6-2, the more severe of the two, and E or F where S1 is 0.75 or more (11.6), as remembered.</summary>
    public static string Category(double sds, double sd1, double s1, int risk)
    {
        if (s1 >= 0.75) return risk == 4 ? "F" : "E";
        var critical = risk == 4;
        var bySds = sds < 0.167 ? "A" : sds < 0.33 ? (critical ? "C" : "B") : sds < 0.5 ? (critical ? "D" : "C") : "D";
        var bySd1 = sd1 < 0.067 ? "A" : sd1 < 0.133 ? (critical ? "C" : "B") : sd1 < 0.2 ? (critical ? "D" : "C") : "D";
        return string.CompareOrdinal(bySds, bySd1) >= 0 ? bySds : bySd1;
    }

    /// <summary>The IRC's category by SDS, Table R301.2.2.1.1: D split into D0, D1 and D2.</summary>
    public static string IrcCategory(double sds) => sds <= 0.17 ? "A" : sds <= 0.33 ? "B" : sds <= 0.50 ? "C" : sds <= 0.67 ? "D0" : sds <= 0.83 ? "D1" : sds <= 1.25 ? "D2" : "E";

    /// <summary>The importance factor by risk category, Table 1.5-2.</summary>
    public static double Importance(int risk) => risk >= 4 ? 1.5 : risk == 3 ? 1.25 : 1.0;

    public static readonly SeismicSystem LightFrame = new("Light-frame (wood) walls sheathed with wood structural panels rated for shear resistance", 6.5, 3, 4, string.Empty);
    public static readonly SeismicSystem ConcreteWalls = new("Ordinary reinforced concrete shear walls", 4, 2.5, 4, "D");
    public static readonly SeismicSystem PlainMasonry = new("Ordinary plain masonry shear walls", 1.5, 2.5, 1.25, "C");
    public static readonly SeismicSystem SteelFrame = new("Steel systems not specifically detailed for seismic resistance", 3, 3, 3, "D");
    public static readonly SeismicSystem ConcreteFrame = new("Ordinary reinforced concrete moment frames", 3, 3, 2.5, "C");

    /// <summary>
    /// The system the model's walls and frames make: of those it has, the least R, as 12.2.3.3 asks of systems combined in a direction. Walls of studs, concrete or masonry, and its columns' frame of
    /// steel or concrete where walls do not stand both ways, the frame being all there is the other way.
    /// With its period's Ct and x of Table 12.8-2: 0.02 and 0.75 for all but moment frames, 0.028 and 0.8 for steel's, 0.016 and 0.9 for concrete's.
    /// </summary>
    public static (SeismicSystem System, double Ct, double X) SystemOf(AnalyticalModel model)
    {
        var systems = new List<SeismicSystem>();
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();
        foreach (var panel in walls)
            systems.Add(panel.Material.Kind switch
            {
                AnalyticalMaterialKind.Timber => LightFrame,
                AnalyticalMaterialKind.Concrete => ConcreteWalls,
                _ => PlainMasonry
            });

        // Where walls stand only one way, or none do, the columns are what is left the other way: their frame is a system of the building's too.
        var (alongX, alongY) = (walls.Any(panel => Math.Abs(panel.U.X) >= 0.7), walls.Any(panel => Math.Abs(panel.U.Y) >= 0.7));
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).ToList();
        var frame = columns.Count == 0 || (alongX && alongY) ? null
            : columns.Any(member => member.Section.Material.Kind == AnalyticalMaterialKind.Concrete) ? ConcreteFrame : SteelFrame;
        if (frame is not null) systems.Add(frame);
        if (systems.Count == 0) return (SteelFrame, 0.028, 0.8);
        var least = systems.MinBy(system => system.R)!;
        return ReferenceEquals(least, ConcreteFrame) ? (least, 0.016, 0.9) : ReferenceEquals(least, SteelFrame) ? (least, 0.028, 0.8) : (least, 0.02, 0.75);
    }

    /// <summary>The approximate fundamental period, s, (12.8-7): Ct hn^x, hn in feet.</summary>
    public static double Period(double height, double ct, double x) => ct * Math.Pow(height / 304.8, x);

    /// <summary>The exponent the forces go up a building by, 12.8.3: 1 to half a second, 2 from 2.5, straight between.</summary>
    public static double Exponent(double period) => period <= 0.5 ? 1 : period >= 2.5 ? 2 : 1 + (period - 0.5) / 2;

    /// <summary>
    /// The seismic response coefficient, 12.8.1.1: SDS / (R / Ie), (12.8-2); no more than SD1 / (T R / Ie) to TL, (12.8-3), or SD1 TL / (T² R / Ie) past it, (12.8-4); no less than 0.044 SDS Ie nor 0.01, (12.8-5), and where S1 is 0.6 or
    /// more no less than 0.5 S1 / (R / Ie), (12.8-6), its coefficients as remembered. On a Site Class D site with S1 of 0.2 or more, 11.4.8's second exception: (12.8-2) to 1.5 Ts and 1.5 times (12.8-3) or (12.8-4) past it.
    /// </summary>
    public static (double Value, string Said) Coefficient(double sds, double sd1, double s1, double r, double importance, double period, double transition, bool siteD, bool forDrift = false)
    {
        var plateau = sds / (r / importance);
        var corner = sds > 0 ? sd1 / sds : 0;
        var falling = period <= transition ? sd1 / (period * r / importance) : sd1 * transition / (period * period * r / importance);
        double value;
        string said;
        if (siteD && period <= 1.5 * corner)
            (value, said) = (plateau, "(12.8-2), T within 1.5 Ts on Site Class D, 11.4.8");
        else if (siteD)
            (value, said) = (Math.Min(plateau, 1.5 * falling), "1.5 times (12.8-3) on Site Class D, 11.4.8");
        else
            (value, said) = plateau <= falling ? (plateau, "(12.8-2)") : (falling, period <= transition ? "(12.8-3)" : "(12.8-4)");

        // For drift, 12.8.6.1: (12.8-5) need not be met, (12.8-6) still must.
        var least = forDrift ? 0 : Math.Max(0.044 * sds * importance, 0.01);
        if (s1 >= 0.6) least = Math.Max(least, 0.5 * s1 / (r / importance));
        return value >= least ? (value, said) : (least, s1 >= 0.6 && (forDrift || 0.5 * s1 / (r / importance) >= Math.Max(0.044 * sds * importance, 0.01)) ? "(12.8-6)" : "(12.8-5)");
    }

    /// <summary>The allowable story drift as a share of the storey's height, Table 12.12-1.</summary>
    /// <summary>The amplification of accidental torsion, (12.8-14), as remembered: (δmax / 1.2 δavg)², no less than 1 nor more than 3.</summary>
    public static double TorsionAmplification(double most, double average) => average <= 0 ? 1 : Math.Clamp(Math.Pow(most / (1.2 * average), 2), 1, 3);

    /// <summary>Table 12.3-1's torsional irregularity by the most a storey drifts at an edge over the average of its two edges, as remembered: 1a past 1.2, 1b past 1.4.</summary>
    public static string TorsionalIrregularity(double ratio) => ratio > 1.4 ? "1b" : ratio > 1.2 ? "1a" : string.Empty;

    /// <summary>Where P-delta has to be taken in, 12.8.7, θ over 0.10: the first-order drift increased by 1 / (1 - θ).</summary>
    public static double DriftIncrease(double theta) => theta > 0.1 && theta < 1 ? 1 / (1 - theta) : 1;

    /// <summary>
    /// A diaphragm's force at its level, (12.10-1) to (12.10-3): the forces from it up over the weights from it up, times its own weight, no less than 0.2 SDS Ie of its weight nor more than 0.4. kN.
    /// </summary>
    public static double DiaphragmForce(double forcesAbove, double weightsAbove, double weight, double sds, double importance) =>
        weightsAbove <= 0 ? 0 : Math.Clamp(forcesAbove / weightsAbove * weight, 0.2 * sds * importance * weight, 0.4 * sds * importance * weight);

    public static double DriftLimit(SeismicSystem system, int storeys, int risk)
    {
        if (ReferenceEquals(system, PlainMasonry)) return 0.007;
        if (storeys <= 4) return risk >= 4 ? 0.015 : risk == 3 ? 0.020 : 0.025;
        return risk >= 4 ? 0.010 : risk == 3 ? 0.015 : 0.020;
    }

    /// <summary>The project's seismic design, for a US project whose Ss and S1 are set; null otherwise.</summary>
    public static SeismicDesign? Of(BimDocument document, AnalyticalModel model)
    {
        var project = document.ProjectInformation;
        if (!project.ClearanceStandard.IsUs() || project.SeismicSs <= 0 || project.SeismicS1 <= 0) return null;
        var (ss, s1, site, risk) = (project.SeismicSs, project.SeismicS1, project.SiteClass, Math.Clamp(project.RiskCategory, 1, 4));
        var (system, ct, x) = SystemOf(model);
        var nodes = model.Nodes.Select(node => node.At.Z).DefaultIfEmpty(0).ToList();
        var height = Math.Max(0, nodes.Max() - Wind.Ground(document));
        var importance = Importance(risk);

        if (Fa(site, ss) is not { } fa || Fv(site, s1) is not { } fv)
            return new SeismicDesign(ss, s1, site, risk, double.NaN, double.NaN, 0, 0, "-", "-", system, importance, height, 0, 1, 0, string.Empty, 1, false,
                site == SiteClass.F ? "Site Class F wants a site response analysis, 11.4.8 and 21.1: not worked out." : "Site Class E with Ss between 0.75 and 1.0 is past what Table 11.4-1 gives, and wants a site response analysis by 11.4.8: not worked out.");

        var (sds, sd1) = (2.0 / 3 * fa * ss, 2.0 / 3 * fv * s1);
        var category = Category(sds, sd1, s1, risk);
        var irc = IrcCategory(sds);
        var period = Period(height, ct, x);
        var (cs, said) = Coefficient(sds, sd1, s1, system.R, importance, period, 8, site == SiteClass.D && s1 >= 0.2);
        var redundancy = category is "A" or "B" or "C" ? 1.0 : 1.3;
        var house = FireDesign.UseOf(document) == BuildingUse.House;

        string why;
        var applies = true;
        if (category == "A")
            (applies, why) = (false, "Seismic Design Category A: 11.7 asks no more than the structural integrity of 1.4.");
        else if (house && (category is "B" or "C" || ss < 0.4))
            (applies, why) = (false, $"Seismic Design Category {category}, IRC {irc}: a detached one- or two-family dwelling {(ss < 0.4 ? "where Ss is under 0.4" : "in category B or C")} is exempt from seismic design by IBC 1613.1, as read secondhand.");
        else if (system.NotPermittedFrom.Length > 0 && string.CompareOrdinal(category, system.NotPermittedFrom) >= 0)
            why = $"Seismic Design Category {category}: {system.Name.ToLowerInvariant()} are not permitted from {system.NotPermittedFrom} by Table 12.2-1. Worked out as if they were, to show what they would carry.";
        else
            why = $"Seismic Design Category {category}{(house ? $", IRC {irc}" : string.Empty)}.";

        var drift = Coefficient(sds, sd1, s1, system.R, importance, period, 8, site == SiteClass.D && s1 >= 0.2, true).Value;
        var flexible = !model.Panels.Any(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof && panel.Material.Kind == AnalyticalMaterialKind.Concrete);
        return new SeismicDesign(ss, s1, site, risk, fa, fv, sds, sd1, category, irc, system, importance, height, period, Exponent(period), cs, said, redundancy, applies, why, drift, flexible);
    }
}
