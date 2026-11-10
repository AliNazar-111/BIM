using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A metre of cantilever retaining wall: the stem's height above its base, its thickness and the base standing out behind it, the heel, and before it, the toe, all mm; what the stem weighs a
/// square metre of its face, kN/m²; what stands on the retained ground, kN/m²; the retained soil's angle of friction, degrees, and weight, kN/m³; the angle of friction under the base, degrees; how
/// far its underside is below the ground in front, mm; how steeply the ground behind rises away from it, degrees; how high water stands behind it over the underside of its base, mm; and how deep a
/// key stands down under its base, mm.
/// </summary>
public sealed record RetainedSection(double Height, double Stem, double Heel, double Toe, double Thickness, double StemWeight, double Surcharge, double Friction, double Density, double BaseFriction,
    double Embedded = 0, double Slope = 0, double Water = 0, double Key = 0)
{
    public double Base => Heel + Stem + Toe;

    /// <summary>How far the ground behind rises over the heel, m: a sloping surface's, from the top of the stem.</summary>
    public double Rise => Heel / 1000 * Math.Tan(Slope * Math.PI / 180);

    /// <summary>The depth the earth pushes over, from the retained surface to the underside of the base, m: on a plane up from the back of the heel, where a sloping surface has risen over it.</summary>
    public double Pushed => (Height + Thickness) / 1000 + Rise;

    /// <summary>How high water stands up that plane from the underside of the base, m.</summary>
    public double Wet => Math.Clamp(Water / 1000, 0, Pushed);
}

/// <summary>What pushes on a vertical plane, kN/m, each with its height above the plane's foot, m: the earth by its coefficient, the water standing in it, and what stands on the surface.</summary>
public readonly record struct EarthPush(double Earth, double EarthArm, double Water, double WaterArm, double Surcharge, double SurchargeArm);

/// <summary>
/// Retaining walls: whether each stands, and what it wants. The earth on a plane behind the heel, by Rankine's state behind a level or sloping surface; water behind it as its full head with the
/// earth below it weighed less the water, and under the base from that head at the heel to nothing at the toe; the ground in front once what may be dug away by mistake is taken off it, and a key
/// under the base. In England by BS EN 1997-1's Design Approach 1, as its UK National Annex has it: sliding under Combination 1 (A1, M1) and Combination 2 (A2, M2), on clay in the short term
/// too, undrained; overturning as the static equilibrium of EQU, the load kept to the middle third of the base and the ground by its presumed bearing value. In the US by IRC R404.4 and IBC 1807.2:
/// a safety factor of 1.5 against sliding and overturning on nominal loads, the earth as the equivalent fluid of Table 1610.1. The stem and base as concrete, or a stem of unreinforced masonry by
/// its flexural strength. A wall held at its top by a floor is a basement's: the earth at rest against it, the stem spanning from its foot to the floor, a masonry one by BS EN 1996-3's simplified method.
/// </summary>
public static class RetainingDesign
{
    private const double Concrete = 24, Psf = 0.0478803, PerFoot = Psf / 0.3048;

    /// <summary>What water weighs, kN/m³.</summary>
    public const double WaterWeight = 9.81;

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    /// <summary>
    /// Rankine's active coefficient behind a vertical plane with no friction on it: level, (1 - sin φ) / (1 + sin φ); behind a surface rising at β, cos β (cos β - √(cos²β - cos²φ)) /
    /// (cos β + √(cos²β - cos²φ)), the pressure then acting parallel to the surface. A surface as steep as the ground's friction or steeper has no such state: cos β, its limit, and the slope is checked of itself.
    /// </summary>
    public static double Active(double friction, double slope = 0)
    {
        if (slope <= 0)
        {
            var sine = Math.Sin(Radians(friction));
            return (1 - sine) / (1 + sine);
        }

        var (b, f) = (Math.Cos(Radians(slope)), Math.Cos(Radians(friction)));
        var root = Math.Sqrt(Math.Max(0, b * b - f * f));
        return b * (b - root) / (b + root);
    }

    /// <summary>Rankine's passive coefficient of level ground against a vertical face with no friction on it: (1 + sin φ) / (1 - sin φ).</summary>
    public static double Passive(double friction)
    {
        var sine = Math.Sin(Radians(friction));
        return (1 + sine) / (1 - sine);
    }

    /// <summary>The coefficient of ground at rest, where a wall does not let it move, BS EN 1997-1 9.5.2: 1 - sin φ' for ground normally consolidated, by (1 + sin β) where the surface rises behind.</summary>
    public static double AtRest(double friction, double slope = 0) => (1 - Math.Sin(Radians(friction))) * (1 + Math.Sin(Radians(Math.Max(0, slope))));

    /// <summary>How much of the ground in front is taken as dug away by mistake, m: BS EN 1997-1 9.3.2.2, a tenth of what a cantilever wall retains over the ground in front, 0.5 m at most.</summary>
    public static double Unplanned(RetainedSection s) => Math.Min(0.5, 0.1 * Math.Max(0, (s.Height + s.Thickness - s.Embedded) / 1000));

    /// <summary>
    /// The earth, the water and the surcharge on a vertical plane depth m deep from the surface, water standing wet m up from its foot, the earth's coefficient k: the earth's own weight below the
    /// water taken less the water's, the water's full head besides.
    /// </summary>
    public static EarthPush Against(double depth, double wet, double k, double density, double surcharge)
    {
        depth = Math.Max(0, depth);
        wet = Math.Clamp(wet, 0, depth);
        var earth = k * (density * depth * depth - WaterWeight * wet * wet) / 2;
        var turning = k * (density * Math.Pow(depth, 3) - WaterWeight * Math.Pow(wet, 3)) / 6;
        return new EarthPush(earth, earth > 0 ? turning / earth : 0, WaterWeight * wet * wet / 2, wet / 3, k * surcharge * depth, depth / 2);
    }

    private static double Factored(double angle, double factor) => Math.Atan(Math.Tan(Radians(angle)) / factor) * 180 / Math.PI;

    /// <summary>Typical drained friction and weight of the ground as a project names it, degrees and kN/m³; null for what no typical figure will do for.</summary>
    public static (double Friction, double Density)? SoilOf(GroundType ground) => ground switch
    {
        GroundType.Rock or GroundType.Gravel => (35, 20),
        GroundType.Sand => (32, 18),
        GroundType.LooseSand => (28, 17),
        GroundType.StiffClay => (25, 20),
        GroundType.FirmClay => (22, 19),
        GroundType.SoftClay => (18, 18),
        _ => null
    };

    /// <summary>A clay's undrained shear strength, kPa, at the foot of the range its consistency has in BS 5930, as remembered: soft 20, firm 40, stiff 75. None for what is not clay.</summary>
    public static double UndrainedOf(GroundType ground) => ground switch
    {
        GroundType.SoftClay => 20,
        GroundType.FirmClay => 40,
        GroundType.StiffClay => 75,
        _ => 0
    };

    /// <summary>IBC Table 1610.1's active pressure for the ground, kN/m³ of depth: 30 psf/ft for clean gravel, 45 for silty sand, 60 for lean clay. Null where it is no fit backfill.</summary>
    public static double? FluidPressure(GroundType ground) => ground switch
    {
        GroundType.Rock or GroundType.Gravel => 30 * PerFoot,
        GroundType.Sand or GroundType.LooseSand => 45 * PerFoot,
        GroundType.StiffClay or GroundType.FirmClay => 60 * PerFoot,
        _ => null
    };

    /// <summary>IBC Table 1610.1's pressure at rest, kN/m³ of depth, for a wall held at its top: 60 psf/ft for clean gravel and silty sand, 100 for lean clay. Null where it is no fit backfill.</summary>
    public static double? AtRestPressure(GroundType ground) => ground switch
    {
        GroundType.Rock or GroundType.Gravel or GroundType.Sand or GroundType.LooseSand => 60 * PerFoot,
        GroundType.StiffClay or GroundType.FirmClay => 100 * PerFoot,
        _ => null
    };

    /// <summary>IBC Table 1806.2's lateral sliding resistance: a coefficient of friction, or for clay and silt a cohesion of 130 psf over the base, kPa, at most half the dead load.</summary>
    public static (double Friction, double Cohesion) SlidingOf(GroundType ground) => ground switch
    {
        GroundType.Rock or GroundType.Gravel => (0.35, 0),
        GroundType.Sand or GroundType.LooseSand => (0.25, 0),
        _ => (0, 130 * Psf)
    };

    /// <summary>IBC Table 1806.2's lateral bearing, kN/m³ of depth below natural grade: 200 psf/ft in gravel, 150 in sand, 100 in clay and silt, 400 in sedimentary rock.</summary>
    public static double LateralBearing(GroundType ground) => ground switch
    {
        GroundType.Rock => 400 * PerFoot,
        GroundType.Gravel => 200 * PerFoot,
        GroundType.Sand or GroundType.LooseSand => 150 * PerFoot,
        _ => 100 * PerFoot
    };

    /// <summary>How much more Table 1610.1's pressure is behind a sloping surface, which it is not given for: Rankine's horizontal pressure behind the slope over his behind level ground, at the soil's friction.</summary>
    public static double SlopeRatio(RetainedSection s) => s.Slope > 0 ? Active(s.Friction, s.Slope) * Math.Cos(Radians(s.Slope)) / Active(s.Friction) : 1;

    /// <summary>
    /// What stands on the base, kN/m, and its moment about the toe, kNm/m: the stem, the base and the soil on the heel, with the wedge a sloping surface rises over it. The surcharge stops behind the
    /// heel, where it is no help; soil under water weighs as it is, the water under the base taken off it.
    /// </summary>
    public static (double Weight, double Moment) Stands(RetainedSection s)
    {
        var (height, stem, heel, toe, thick, width) = (s.Height / 1000, s.Stem / 1000, s.Heel / 1000, s.Toe / 1000, s.Thickness / 1000, s.Base / 1000);
        var (ofStem, ofBase, ofSoil, ofWedge) = (s.StemWeight * height, Concrete * width * thick, s.Density * heel * height, s.Density * heel * s.Rise / 2);
        return (ofStem + ofBase + ofSoil + ofWedge, ofStem * (toe + stem / 2) + ofBase * width / 2 + ofSoil * (toe + stem + heel / 2) + ofWedge * (toe + stem + 2 * heel / 3));
    }

    /// <summary>The water under the base, kN/m, and its moment about the toe, kNm/m: from its head behind at the heel to nothing at the toe, the ground in front drained.</summary>
    public static (double Force, double Moment) Uplift(RetainedSection s)
    {
        var width = s.Base / 1000;
        var force = WaterWeight * s.Wet * width / 2;
        return (force, force * 2 * width / 3);
    }

    /// <summary>The load off the middle of the base and the most the ground takes, on characteristic loads: V/B (1 + 6e/B) in the middle third, 2V/3x past it.</summary>
    public static (double Eccentricity, double Pressure) OnTheGround(RetainedSection s, double overturning)
    {
        var (weight, moment) = Stands(s);
        return OnTheGround(s.Base, weight, moment, overturning);
    }

    /// <summary>The load off the middle of a base so wide, mm, bearing down kN/m with a moment holding it about its toe against one turning it, kNm/m; toward the toe positive.</summary>
    public static (double Eccentricity, double Pressure) OnTheGround(double base_, double weight, double holding, double overturning)
    {
        var width = base_ / 1000;
        var from = weight <= 0 ? 0 : (holding - overturning) / weight;
        var off = width / 2 - from;
        var edge = width / 2 - Math.Abs(off);
        return (off * 1000, weight <= 0 || edge <= 0 ? double.PositiveInfinity : Math.Abs(off) <= width / 6 ? weight / width * (1 + 6 * Math.Abs(off) / width) : 2 * weight / (3 * edge));
    }

    private static IEnumerable<StrengthCheck> GroundChecks(double base_, double weight, double holding, double overturning, double? allowable, string clause, string bearingClause)
    {
        var (off, pressure) = OnTheGround(base_, weight, holding, overturning);
        yield return new StrengthCheck("Load off its middle", Math.Abs(off), base_ / 6, "mm", clause, "kept to the middle third of its base");
        if (allowable is { } bears && !double.IsInfinity(pressure)) yield return new StrengthCheck("Ground under its toe", pressure, bears, "kPa", bearingClause);
    }

    private static StrengthCheck Worse(StrengthCheck first, StrengthCheck second) => first.Ratio >= second.Ratio ? first : second;

    /// <summary>
    /// Whether it stands by BS EN 1997-1's Design Approach 1: sliding drained, and on clay undrained in the short term as well, by (6.4a) held to 0.4 of what bears down by (6.5), water reaching under
    /// a retaining wall's base; the ground in front taken for it below what 9.3.2.2 has dug away by mistake, at Rankine's passive pressure drained and γ z + 2 cu undrained, down to a key's foot.
    /// </summary>
    public static IReadOnlyList<StrengthCheck> StabilityEurocode(RetainedSection s, double? allowable, double undrained = 0)
    {
        var (weight, moment) = Stands(s);
        var (uplift, lifting) = Uplift(s);
        var (cos, sin) = (Math.Cos(Radians(s.Slope)), Math.Sin(Radians(s.Slope)));
        var width = s.Base / 1000;
        var (key, wet) = (s.Key / 1000, s.Wet);

        // A key holds the base by the ground in front of it, down to its foot; the earth behind pushes to the base's underside as before, as Das has it.
        var dug = Math.Max(0, (s.Embedded + s.Key) / 1000 - Unplanned(s));

        (double Horizontal, double Down) Sliding(double earth, double surcharge, double water, double materials)
        {
            var push = Against(s.Pushed, wet, Active(Factored(s.Friction, materials), s.Slope), s.Density, s.Surcharge);
            return (cos * (earth * push.Earth + surcharge * push.Surcharge) + water * push.Water, weight - uplift + sin * push.Earth);
        }

        StrengthCheck Drained(string name, double earth, double surcharge, double water, double materials)
        {
            var (horizontal, down) = Sliding(earth, surcharge, water, materials);
            var friction = Math.Tan(Radians(Factored(s.BaseFriction, materials)));
            var front = 0.5 * Passive(Factored(s.BaseFriction, materials)) * s.Density * dug * dug;
            var said = $"{name}: Ka {Active(Factored(s.Friction, materials), s.Slope):0.00}, tan δ {friction:0.00}" + (front > 0.05 ? $", {front:0.0} from the ground in front" : ", nothing in front")
                       + (uplift > 0 ? $", {uplift:0.0} of water under it" : string.Empty) + (key > 0 ? ", the ground in front taken to its key's foot" : string.Empty);
            return new StrengthCheck("Sliding", horizontal, down * friction + front, "kN/m", "EN 1997-1 6.5.3, (6.3a)", said, name);
        }

        var checks = new List<StrengthCheck> { Worse(Drained("DA1-1", 1.35, 1.5, 1.35, 1), Drained("DA1-2", 1.0, 1.3, 1.0, 1.25)) };

        // The ground, on characteristic loads.
        var push = Against(s.Pushed, wet, Active(s.Friction, s.Slope), s.Density, s.Surcharge);
        var turning = cos * (push.Earth * push.EarthArm + push.Surcharge * push.SurchargeArm) + push.Water * push.WaterArm + lifting;
        var (bears, holding) = (weight - uplift + sin * push.Earth, moment + sin * push.Earth * width);

        if (undrained > 0)
        {
            var area = Math.Max(0, width - 2 * Math.Abs(OnTheGround(s.Base, bears, holding, turning).Eccentricity) / 1000);
            StrengthCheck Short(string name, double earth, double surcharge, double water, double materials, double gamma)
            {
                var (horizontal, down) = Sliding(earth, surcharge, water, materials);
                var adhesion = area * undrained / gamma;

                // The clay in front in total stress, below what may be dug away: its weight and twice its strength down its depth.
                var front = 0.5 * s.Density * dug * dug + 2 * undrained / gamma * dug;
                var said = $"{name}: undrained, cu {undrained / gamma:0.#} kPa over {area:0.00} m of its base" + (adhesion > 0.4 * down ? ", held to 0.4 of what bears down, water reaching under it" : string.Empty)
                           + (front > 0.05 ? $", {front:0.0} from the clay in front" : string.Empty);
                return new StrengthCheck("Sliding, short term", horizontal, Math.Min(adhesion, 0.4 * Math.Max(0, down)) + front, "kN/m", "EN 1997-1 6.5.3(12), (6.4a), (6.5)", said, name);
            }

            checks.Add(Worse(Short("DA1-1", 1.35, 1.5, 1.35, 1, 1), Short("DA1-2", 1.0, 1.3, 1.0, 1.25, 1.4)));
        }

        var design = Active(Factored(s.Friction, 1.25), s.Slope);
        var tips = Against(s.Pushed, wet, design, s.Density, s.Surcharge);
        var tipping = cos * (1.1 * tips.Earth * tips.EarthArm + 1.5 * tips.Surcharge * tips.SurchargeArm) + 1.1 * (tips.Water * tips.WaterArm + lifting);
        checks.Add(new StrengthCheck("Overturning", tipping, 0.9 * (moment + sin * tips.Earth * width), "kNm/m", "BS EN 1990 EQU", $"about its toe, Ka {design:0.00}", "EQU"));

        checks.AddRange(GroundChecks(s.Base, bears, holding, turning, allowable, "BS 8002 practice", "BS 8004:1986 Table 1"));
        if (s.Slope > 0)
        {
            var friction = Factored(s.Friction, 1.25);
            checks.Add(new StrengthCheck("Slope behind it", Math.Tan(Radians(s.Slope)), Math.Tan(Radians(friction)), "", "EN 1997-1 11.5.1",
                $"tan β against tan φ'd, {friction:0.0}°: ground with no cohesion stands no steeper of itself", "DA1-2"));
        }

        return checks;
    }

    /// <summary>
    /// Whether it stands by IRC R404.4 and IBC 1807.2.3: 1.5 against sliding and overturning, on nominal loads. The surcharge as added depth of earth; under water, by 1610.1, the soil buoyant and
    /// the water's own head; the base's friction or cohesion by Table 1806.2, with lateral bearing below grade in front, which 1806.3.1 lets them be combined with, down to a key's foot.
    /// </summary>
    public static IReadOnlyList<StrengthCheck> StabilityUs(RetainedSection s, double fluid, double friction, double cohesion, double? allowable, double lateral = 0)
    {
        var (weight, moment) = Stands(s);
        var (uplift, lifting) = Uplift(s);
        var k = fluid / s.Density * SlopeRatio(s);
        var tips = Against(s.Pushed, s.Wet, k, s.Density, s.Surcharge);
        var pushes = tips.Earth + tips.Surcharge + tips.Water;
        var turning = tips.Earth * tips.EarthArm + tips.Surcharge * tips.SurchargeArm + tips.Water * tips.WaterArm + lifting;
        var down = weight - uplift;
        var bearing = 0.5 * lateral * Math.Pow((s.Embedded + s.Key) / 1000, 2);
        var holds = (friction > 0 ? friction * down : Math.Min(cohesion * s.Base / 1000, down / 2)) + bearing;
        var checks = new List<StrengthCheck>
        {
            new("Sliding, at 1.5", 1.5 * pushes, holds, "kN/m", "IRC R404.4, IBC 1807.2.3",
                $"{(friction > 0 ? $"friction {friction:0.00} of its weight" : "130 psf over its base or half its weight")}, {bearing:0.0} of lateral bearing, Table 1806.2" + (uplift > 0 ? $", {uplift:0.0} of water under it" : string.Empty)),
            new("Overturning, at 1.5", 1.5 * turning, moment, "kNm/m", "IRC R404.4, IBC 1807.2.3", "about its toe")
        };
        checks.AddRange(GroundChecks(s.Base, down, moment, turning, allowable, "IBC 1807.2.1", "IRC Table R401.4.1"));
        return checks;
    }

    /// <summary>The earth, water and surcharge on the back of the stem, from the surface at its top down to its foot, the earth's coefficient k.</summary>
    private static EarthPush OnStem(RetainedSection s, double k) => Against(s.Height / 1000, (s.Water - s.Thickness) / 1000, k, s.Density, s.Surcharge);

    /// <summary>The worst of the design approach's two combinations at the foot of the stem, kNm/m and kN/m: the earth's and surcharge's horizontal part, and the water's.</summary>
    public static (double Moment, double Shear) StemForcesEurocode(RetainedSection s)
    {
        var cos = Math.Cos(Radians(s.Slope));
        (double Moment, double Shear) Under(double earth, double surcharge, double water, double materials)
        {
            var push = OnStem(s, Active(Factored(s.Friction, materials), s.Slope));
            return (cos * (earth * push.Earth * push.EarthArm + surcharge * push.Surcharge * push.SurchargeArm) + water * push.Water * push.WaterArm,
                cos * (earth * push.Earth + surcharge * push.Surcharge) + water * push.Water);
        }

        var (first, second) = (Under(1.35, 1.5, 1.35, 1), Under(1, 1.3, 1, 1.25));
        return (Math.Max(first.Moment, second.Moment), Math.Max(first.Shear, second.Shear));
    }

    /// <summary>The foot of the stem by ACI 318-19 and ASCE 7's 1.6 H, kNm/m and kN/m: Table 1610.1's fluid, the surcharge as more earth, the water's head.</summary>
    public static (double Moment, double Shear) StemForcesUs(RetainedSection s, double fluid)
    {
        var push = OnStem(s, fluid / s.Density * SlopeRatio(s));
        return (1.6 * (push.Earth * push.EarthArm + push.Surcharge * push.SurchargeArm + push.Water * push.WaterArm), 1.6 * (push.Earth + push.Surcharge + push.Water));
    }

    private static (double Needed, string? Problem) Steel(double moment, double depth, double fc, bool us) => us
        ? ConcreteDesign.Aci(moment, 1000, depth, fc, ConcreteDesign.LeastAci(1000, depth, fc))
        : ConcreteDesign.Eurocode(moment, 1000, depth, fc, ConcreteDesign.LeastEurocode(1000, depth, fc));

    private static double Most(double depth, double fc, bool us) => us
        ? 0.9 * 0.85 * fc * 1000 * 0.375 * ConcreteDesign.Beta1(fc) * depth * (depth - 0.375 * ConcreteDesign.Beta1(fc) * depth / 2) / 1e6
        : 0.167 * 1000 * depth * depth * fc / 1e6;

    private static StrengthCheck Bending(string name, double moment, double depth, double fc, bool us, string face)
    {
        var (needed, problem) = Steel(moment, depth, fc, us);
        return new StrengthCheck(name, moment, Most(depth, fc, us), "kNm/m", us ? "22.2" : "6.1", problem ?? $"needs {needed:0} mm²/m {face}");
    }

    private static StrengthCheck Shear(double shear, double moment, double depth, double fc, bool us)
    {
        var share = Math.Min(0.02, Steel(moment, depth, fc, us).Needed / (1000 * depth));
        var stress = us ? PlateDesign.OneWayAci(depth, share, fc) : PlateDesign.OneWayEurocode(depth, share, fc);
        return new StrengthCheck("Stem in shear", shear, stress * depth, "kN/m", us ? "22.5" : "6.2.2");
    }

    /// <summary>The stem as concrete, or by ACI 318-19, saying the bars each metre comes to; and the base in bending at the stem's two faces: the toe under the most the ground gives, the heel under what stands on it with nothing from below.</summary>
    public static IEnumerable<StrengthCheck> ConcreteParts(RetainedSection s, double moment, double shear, double groundPressure, double fc, bool us)
    {
        var d = s.Stem - 50;
        yield return Bending("Stem in bending", moment, d, fc, us, "up its retained face");
        yield return Shear(shear, moment, d, fc, us);

        // Ground under the toe at the ultimate as its characteristic pressure by the permanent factor: the base's own weight, which would help, left out. The heel carries the wedge a slope rises over it.
        var factor = us ? 1.6 : 1.35;
        var (toe, heel) = (s.Toe / 1000, s.Heel / 1000);
        var atToe = factor * (double.IsInfinity(groundPressure) ? 0 : groundPressure) * toe * toe / 2;
        var atHeel = (factor * (s.Density * s.Height / 1000 + Concrete * s.Thickness / 1000) + (us ? 1.6 : 1.5) * s.Surcharge) * heel * heel / 2 + factor * s.Density * s.Rise * heel * heel / 3;
        var worst = Math.Max(atToe, atHeel);
        yield return Bending("Base in bending", worst, s.Thickness - Reinforcement.CoverAgainstGround, fc, us, atToe >= atHeel ? "underneath, in its toe" : "on top, in its heel");
    }

    /// <summary>The stem as unreinforced masonry, BS EN 1996-1-1 6.3.1: its flexural strength, with what bears down on it adding to it - its own weight at its foot, or a load given, kN/m, characteristic.</summary>
    public static StrengthCheck MasonryStem(RetainedSection s, double moment, double thickness, double fxk1, double design, double? bearing = null)
    {
        var modulus = 1000 * thickness * thickness / 6;
        var compression = 0.9 * (bearing ?? s.StemWeight * s.Height / 1000) / thickness;
        var capacity = (fxk1 / MasonryDesign.GammaFlexure + Math.Min(compression, 0.2 * design)) * modulus / 1e6;
        return new StrengthCheck("Stem in bending", moment, capacity, "kNm/m", "BS EN 1996-1-1 6.3.1", $"fxk1 {fxk1:0.00}, no bars");
    }

    /// <summary>The narrowest base that stands, mm, the heel grown from what it has, its toe left as it is: soil on the heel is what holds a wall down. Null if none under 8 m does.</summary>
    public static double? BaseWanted(RetainedSection s, Func<RetainedSection, IReadOnlyList<StrengthCheck>> stability)
    {
        for (var heel = s.Heel; heel + s.Stem + s.Toe <= 8000; heel += 50)
        {
            var wider = s with { Heel = heel };
            if (stability(wider).All(check => check.Ratio <= 1)) return wider.Base;
        }

        return null;
    }

    /// <summary>
    /// A stem held at its top as well as its foot, height m between them, against a pressure kPa at each height m above its foot: what holds its top back, kN/m; the moment and shear at its foot,
    /// kNm/m and kN/m, hogging, the earth's face in tension; and the most moment the other way between them, kNm/m. Fixed at its foot, as concrete cast with its base is, its top's prop what takes
    /// back the cantilever's deflection there; or pinned there, as masonry on a damp course is.
    /// </summary>
    public static (double Prop, double FootMoment, double FootShear, double SpanMoment) Propped(double height, Func<double, double> pressure, bool fixedFoot = true)
    {
        static double Simpson(Func<double, double> f, double from, double to, int n)
        {
            if (to - from <= 0) return 0;
            var step = (to - from) / n;
            var sum = f(from) + f(to);
            for (var i = 1; i < n; i++) sum += (i % 2 == 1 ? 4 : 2) * f(from + i * step);
            return sum * step / 3;
        }

        const int n = 200;
        var load = Simpson(pressure, 0, height, n);
        var turning = Simpson(a => pressure(a) * a, 0, height, n);
        var prop = fixedFoot ? Simpson(a => pressure(a) * a * a * (3 * height - a), 0, height, n) / (2 * Math.Pow(height, 3)) : turning / height;

        // The moment at each height from what is above it.
        double At(double x) => Simpson(a => pressure(a) * (a - x), x, height, 100) - prop * (height - x);
        var span = Enumerable.Range(1, 99).Select(i => -At(height * i / 100)).DefaultIfEmpty(0).Max();
        return (prop, fixedFoot ? turning - prop * height : 0, load - prop, Math.Max(0, span));
    }

    /// <summary>
    /// A masonry basement wall by BS EN 1996-3's simplified method, 4.5: the wall arching between its foot and the floor over it to hold the earth back, which asks enough load on it at the middle of
    /// the earth's height, ρe b h he² / (βe t), characteristic and permanent, and not too much, b t fd / 3, at the ultimate. βe 20 where what stiffens it is 2h apart or more, 40 where h or less, 60 -
    /// 20 bc / h between. The first said as the earth it lets the wall hold back, √(NEd,min βe t / (ρe b h)), against the earth there. Its height, the earth's and the walls stiffening it apart mm, its
    /// thickness mm, the earth's weight kN/m³, the least and most load on it kN/m, fd N/mm².
    /// </summary>
    public static IReadOnlyList<StrengthCheck> BasementMasonry(double height, double earth, double between, double thickness, double density, double least, double most, double fd)
    {
        var beta = between >= 2 * height ? 20 : between <= height ? 40 : 60 - 20 * between / height;
        var (h, he, t) = (height / 1000, earth / 1000, thickness / 1000);
        var wanted = density * h * he * he / (beta * t);
        return new[]
        {
            new StrengthCheck("Earth it holds back", earth, Math.Sqrt(Math.Max(0, least) * beta * t / (density * h)) * 1000, "mm", "BS EN 1996-3 4.5",
                $"arching on the {least:0.0} kN/m bearing down at the middle of the earth's height, its own weight and what it carries with nothing imposed; βe {beta:0.#}, NEd,min ≥ ρe b h he² / (βe t) wants {wanted:0.0} kN/m"),
            new StrengthCheck("Load on it, the most", most, thickness * fd / 3, "kN/m", "BS EN 1996-3 4.5", $"t fd / 3, fd {fd:0.00} N/mm²")
        };
    }

    /// <summary>Which way a wall holds its earth back: toward its exterior or not. The ground says, where there is a surface under it; else its exterior, which is what its Flip turns.</summary>
    public static bool RetainsExterior(BimDocument document, Wall wall, WallType type)
    {
        var surface = SurfaceOf(document, wall);
        if (surface is null) return true;
        double Side(double side) => Enumerable.Range(0, 5).Select(k => Terrains.HeightAt(surface, wall.PointAt(type.Structure, wall.Length * (k + 0.5) / 5, side * (type.Structure.TotalWidth / 2 + 1000))))
            .Where(height => height is not null).Select(height => height!.Value).DefaultIfEmpty(double.NegativeInfinity).Average();
        return Side(1) >= Side(-1);
    }

    private static Toposurface? SurfaceOf(BimDocument document, Wall wall) =>
        document.All<Toposurface>().FirstOrDefault(each => each.LevelId == wall.LevelId && Terrains.RetainingWalls(document, each).Any(pair => ReferenceEquals(pair.Wall, wall)));

    /// <summary>The ground's height behind a wall, a metre back from it along its length, as the surface was surveyed: the ground as graded rises to its top at its face; none where no surface has it.</summary>
    private static double? GroundBehind(BimDocument document, Wall wall, WallType type, bool exterior)
    {
        if (SurfaceOf(document, wall) is not { } surface) return null;
        var side = exterior ? 1 : -1;
        var heights = Enumerable.Range(0, 5).Select(k => Terrains.HeightAt(surface, wall.PointAt(type.Structure, wall.Length * (k + 0.5) / 5, side * (type.Structure.TotalWidth / 2 + 1000))))
            .Where(height => height is not null).Select(height => height!.Value).ToList();
        return heights.Count == 0 ? null : heights.Average();
    }

    /// <summary>
    /// A retaining wall as a section: from the ground behind it - its top, or lower where a surface says so - down to its footing's top, its thickness, and which side of it the footing stands out
    /// further; the project's slope and water behind it and its footing's key.
    /// </summary>
    public static RetainedSection? SectionOf(BimDocument document, Wall wall, WallFooting footing, double friction, double density, double baseFriction)
    {
        if (document.GetWallType(wall) is not { } type || document.FindType<FootingType>(footing.TypeId) is not { } base_) return null;
        var project = document.ProjectInformation;
        var stem = type.Structure.TotalWidth;
        var exterior = RetainsExterior(document, wall, type);
        var top = wall.GetTopElevation(document);
        var ground = Math.Min(top, GroundBehind(document, wall, type, exterior) ?? top);
        var height = Math.Max(0, ground - wall.GetBaseElevation(document)) + Math.Max(0, footing.Depth - base_.Thickness);
        var offset = (exterior ? 1 : -1) * footing.Eccentricity;
        var (heel, toe) = (Math.Max(0, base_.Width / 2 + offset - stem / 2), Math.Max(0, base_.Width / 2 - offset - stem / 2));
        return new RetainedSection(height, stem, heel, toe, base_.Thickness, Loads.Weight(document, type.Structure), project.RetainedSurcharge, friction, density, baseFriction, footing.Depth,
            project.RetainedSlope, project.RetainedWater, base_.Kind == FootingKind.Strip ? base_.Key : 0);
    }

    /// <summary>
    /// The floor holding a wall's top: one at its top, over it or reaching it from the side it does not hold back, which makes it a basement's wall rather than a cantilever. None for a wall
    /// standing free, nor for a slab lying on the earth it holds back.
    /// </summary>
    public static Floor? HeldBy(BimDocument document, Wall wall)
    {
        if (document.GetWallType(wall) is not { } type || wall.Length < 1) return null;
        var top = wall.GetTopElevation(document);
        var half = type.Structure.TotalWidth / 2;
        var open = RetainsExterior(document, wall, type) ? -1.0 : 1.0;
        var points = new[] { 0.0, open }.SelectMany(side => new[] { 0.25, 0.5, 0.75 }.Select(t => wall.PointAt(type.Structure, wall.Length * t, side * (half + 300)))).ToList();
        return document.All<Floor>().FirstOrDefault(floor => floor.Boundary.Count >= 3 && floor.GetTopElevation(document) is var level && level >= top - 600 && level <= top + 300 && points.Any(floor.Contains));
    }

    /// <summary>Every retaining wall in the model, each called RW and a number in the order they are in it.</summary>
    public static IReadOnlyList<FoundationCheck> Of(BimDocument document)
    {
        var results = new List<FoundationCheck>();
        var walls = document.Walls.Where(wall => wall.Length > 1 && document.GetWallType(wall) is { Function: WallFunction.Retaining }).ToList();
        for (var i = 0; i < walls.Count; i++) results.Add(Check(document, walls[i], $"RW{i + 1}"));
        return results;
    }


    /// <summary>
    /// Whether a basement floats, BS EN 1997-1 2.4.7.4 (UPL) and Table A.15: the water under its floor at 1.0 against 0.9 of the building's own weight as the analysis loads it, nothing taken for the
    /// ground beside it holding it down. A floor at the foot of walls held at their top, water standing over its underside: the project's water behind them, up from their footings' underside.
    /// </summary>
    public static IReadOnlyList<FoundationCheck> Flotation(BimDocument document, StructuralAnalysis analysis)
    {
        var project = document.ProjectInformation;
        if (project.RetainedWater <= 0 || project.ClearanceStandard.IsUs()) return Array.Empty<FoundationCheck>();
        var held = document.Walls.Where(wall => wall.Length > 1 && document.GetWallType(wall) is { Function: WallFunction.Retaining } && HeldBy(document, wall) is not null).ToList();
        if (held.Count == 0) return Array.Empty<FoundationCheck>();

        var weight = -analysis.Loading.Cases.Where(loadCase => loadCase.Permanent).Sum(loadCase => analysis.TotalLoad(loadCase).Z);
        var results = new List<FoundationCheck>();
        foreach (var floor in document.All<Floor>().Where(floor => floor.Boundary.Count >= 3))
        {
            var top = floor.GetTopElevation(document);
            var walls = held.Where(wall => Math.Abs(wall.GetBaseElevation(document) - top) <= 600 && document.GetWallType(wall) is { } type &&
                                           new[] { 0.25, 0.5, 0.75 }.Any(t => new[] { 1.0, -1 }.Any(side => floor.Contains(wall.PointAt(type.Structure, wall.Length * t, side * (type.Structure.TotalWidth / 2 + 300)))))).ToList();
            if (walls.Count == 0) continue;
            var underside = walls.Min(wall => wall.GetBaseElevation(document) - (document.All<WallFooting>().FirstOrDefault(each => each.WallId == wall.Id)?.Depth ?? 0));
            var bottom = top - (document.FindType<SlabType>(floor.TypeId)?.Thickness ?? 0);
            var head = underside + project.RetainedWater - bottom;
            if (head <= 0) continue;
            var area = Polygon2D.Area(floor.Boundary) / 1e6;
            var uplift = WaterWeight * head / 1000 * area;
            var check = new StrengthCheck("Floating", uplift, 0.9 * weight, "kN", "EN 1997-1 2.4.7.4, (2.8)", $"{Units.Metres(head, "0.00")} of water under its {area:0.#} m², against 0.9 of the building's own weight", "UPL");
            results.Add(new FoundationCheck(floor, $"Basement {results.Count + 1}", new[] { check },
                "The whole building's own weight in the analysis taken as on its basement, and nothing for the ground beside it or the walls' friction; what is put on its floors left out, as it may not be there. Its floor's own bending under the water is not worked out here."));
        }

        return results;
    }

    private static bool IsMasonry(Material? material) => material is not null &&
        (material.Name.Contains("block", StringComparison.OrdinalIgnoreCase) || material.Name.Contains("brick", StringComparison.OrdinalIgnoreCase) || material.Name.Contains("masonry", StringComparison.OrdinalIgnoreCase));

    /// <summary>One retaining wall: whether it stands, and its stem and base; a basement's wall, held at its top, against the earth at rest.</summary>
    public static FoundationCheck Check(BimDocument document, Wall wall, string name)
    {
        var project = document.ProjectInformation;
        var us = project.ClearanceStandard.IsUs();
        var footing = document.All<WallFooting>().FirstOrDefault(each => each.WallId == wall.Id);
        if (footing is null) return new FoundationCheck(wall, name, Array.Empty<StrengthCheck>(), "Not checked: it stands on no footing. Give it a strip footing wide enough to stand it, standing out behind it under the earth.");
        if (SoilOf(project.Ground) is not { } soil) return new FoundationCheck(wall, name, Array.Empty<StrengthCheck>(), "Not checked: ground this soft wants a site investigation, not a typical figure.");

        var friction = project.RetainedFriction > 0 ? project.RetainedFriction : soil.Friction;
        var type = document.GetWallType(wall)!;
        if (SectionOf(document, wall, footing, friction, soil.Density, soil.Friction) is not { } section) return new FoundationCheck(wall, name, Array.Empty<StrengthCheck>(), "Not checked: its footing has no type.");
        var fluid = FluidPressure(project.Ground);
        if (us && fluid is null) return new FoundationCheck(wall, name, Array.Empty<StrengthCheck>(), "Not checked: IBC Table 1610.1 gives no load for this ground as backfill: a site investigation to say.");

        var core = type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).OrderByDescending(layer => layer.Thickness).FirstOrDefault();
        var material = core is null ? null : document.FindMaterial(core.MaterialId);
        var drawn = project.RetainedFriction > 0 ? $"{friction:0}° as set" : $"{friction:0}°, typical for the ground";
        if (HeldBy(document, wall) is { } floor) return Basement(document, wall, name, section, floor, drawn, core, material, Math.Max(0, footing.Depth - section.Thickness));

        var (allowable, said) = StructuralAnalysis.AllowableBearing(document);
        var (sliding, cohesion) = SlidingOf(project.Ground);
        var undrained = us ? 0 : UndrainedOf(project.Ground);
        IReadOnlyList<StrengthCheck> Stable(RetainedSection each) => us ? StabilityUs(each, fluid!.Value, sliding, cohesion, allowable, LateralBearing(project.Ground)) : StabilityEurocode(each, allowable, undrained);

        var checks = Stable(section).ToList();
        var pressure = checks.FirstOrDefault(check => check.Name == "Ground under its toe")?.Demand ?? OnTheGround(section, 0).Pressure;
        var (moment, shear) = us ? StemForcesUs(section, fluid!.Value) : StemForcesEurocode(section);

        var limits = new List<string>();
        if (AnalyticalModel.IsConcreteWall(document, wall))
        {
            var strength = material is null ? 28 : ConcreteDesign.StrengthOf(material.Name);
            checks.AddRange(ConcreteParts(section with { Stem = core?.Thickness ?? section.Stem }, moment, shear, pressure, strength, us));
            limits.Add("No bars are in the model: what each metre comes to is said.");
        }
        else if (!us && core is not null && IsMasonry(material))
        {
            var unit = MasonryDesign.UnitOf(material!);
            checks.Add(MasonryStem(section, moment, core.Thickness, MasonryDesign.FlexuralStrengths(unit, core.Thickness).Fxk1, MasonryDesign.Characteristic(unit) / MasonryDesign.GammaCompression));
        }
        else limits.Add(us ? "Its stem as masonry is not worked out: TMS 402 could not be confirmed." : "Its stem is not worked out: it is neither concrete nor masonry.");

        var wanted = BaseWanted(section, Stable);
        var stands = Stable(section).All(check => check.Ratio <= 1);
        if (!stands) limits.Insert(0, wanted is { } width
            ? $"It wants a base about {Math.Ceiling(width / 50) * 50:0} wide. The extra goes behind the stem under the earth; a key under it, set on its footing's type, may do as well."
            : "No base under 8 m stands it: hold it some other way.");
        if (!stands && us) limits.Add("Table 1806.2's presumptive values are low on clay and silt: a soils report's friction will usually do better.");
        if (allowable is null) limits.Add($"{said}.");

        var behind = new List<string> { $"{Units.Metres(section.Pushed * 1000, "0.00")} of earth from its surface to under its base, φ' {drawn}, with {section.Surcharge:0.#} kN/m² on it" };
        if (section.Slope > 0) behind.Add($"the ground behind rising at {section.Slope:0.#}°{(us ? ", Table 1610.1's pressure taken up by Rankine's for the slope" : string.Empty)}");
        behind.Add(section.Water > 0 ? $"water {Units.Metres(section.Water, "0.00")} up from under its base behind it and none in front, {(us ? "the soil under it buoyant as IBC 1610.1 has it" : "its full head")}" : "drained behind it, the drain kept working");
        if (!us)
        {
            var dug = (section.Embedded + section.Key) / 1000 - Unplanned(section);
            behind.Add(dug > 0 ? $"the ground in front taken for it below {Unplanned(section):0.00} m dug away by mistake (EN 1997-1 9.3.2.2)" : $"nothing taken for the ground in front, {Unplanned(section):0.00} m of it allowed to be dug away by mistake (EN 1997-1 9.3.2.2)");
        }

        if (section.Key > 0) behind.Add($"a key {section.Key:0} deep under it, the ground in front of it taken down to its foot");
        if (undrained > 0) behind.Add($"on clay of {undrained:0} kPa undrained in the short term, typical");
        limits.Add($"{string.Join("; ", behind)}: a site investigation to say. {(us ? "IRC R404.4 asks for this past 48 in of unbalanced fill." : string.Empty)}".TrimEnd());
        return new FoundationCheck(wall, name, checks, string.Join(" ", limits));
    }

    /// <summary>
    /// A basement's wall, held at its top by the floor over it: the earth at rest against it, as BS EN 1997-1 9.5.2 has ground a wall does not let move, or IBC 1610.1 a wall held at its top - active
    /// where its exception lets a foundation wall no more than 8 ft below grade held by a flexible floor be. Concrete fixed at its foot and propped at its top, bent both ways; masonry by BS EN 1996-3 4.5
    /// where it may be, else pinned at both and bent by what holds it down. Whether the basement as a whole slides, tips or floats is not its own wall's to say.
    /// </summary>
    private static FoundationCheck Basement(BimDocument document, Wall wall, string name, RetainedSection s, Floor floor, string drawn, MaterialLayer? core, Material? material, double below)
    {
        var project = document.ProjectInformation;
        var us = project.ClearanceStandard.IsUs();
        var checks = new List<StrengthCheck>();
        var limits = new List<string>();
        var span = (wall.GetHeight(document) + below) / 1000;
        var earthTop = s.Height / 1000;
        var waterAt = Math.Clamp((s.Water - s.Thickness) / 1000, 0, earthTop);
        var rigid = document.FindType<SlabType>(floor.TypeId) is { } slab && slab.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).Any(layer => (document.FindMaterial(layer.MaterialId)?.Density ?? 0) >= 1800);

        Func<double, double> Pressure(double k, double earth, double surcharge, double water) => x =>
        {
            if (x > earthTop) return 0;
            var wet = Math.Max(0, waterAt - x);
            return earth * k * (s.Density * (earthTop - x) - WaterWeight * wet) + surcharge * k * s.Surcharge + water * WaterWeight * wet;
        };

        var concrete = AnalyticalModel.IsConcreteWall(document, wall);
        var masonry = !concrete && core is not null && IsMasonry(material);
        (double Prop, double FootMoment, double FootShear, double SpanMoment) forces;
        string pushed;
        if (us)
        {
            var exception = !rigid && s.Height <= 2438;
            var fluid = (exception ? FluidPressure(project.Ground) : AtRestPressure(project.Ground))!.Value;
            forces = Propped(span, Pressure(fluid / s.Density * SlopeRatio(s), 1.6, 1.6, 1.6), concrete);
            pushed = exception
                ? $"active at {fluid / PerFoot:0} psf/ft by IBC 1610.1's exception, no more than 8 ft below grade under a flexible floor"
                : $"at rest at {fluid / PerFoot:0} psf/ft, IBC 1610.1 and Table 1610.1, held at its top by {(rigid ? "a concrete floor" : "its floor")}";
        }
        else
        {
            var first = Propped(span, Pressure(AtRest(s.Friction, s.Slope), 1.35, 1.5, 1.35), concrete);
            var second = Propped(span, Pressure(AtRest(Factored(s.Friction, 1.25), s.Slope), 1, 1.3, 1), concrete);
            forces = (Math.Max(first.Prop, second.Prop), Math.Max(first.FootMoment, second.FootMoment), Math.Max(first.FootShear, second.FootShear), Math.Max(first.SpanMoment, second.SpanMoment));
            pushed = $"at rest, K0 {AtRest(s.Friction, s.Slope):0.00} (BS EN 1997-1 9.5.2), the worse of DA1's two combinations";
        }

        if (concrete)
        {
            var strength = material is null ? 28 : ConcreteDesign.StrengthOf(material.Name);
            var d = (core?.Thickness ?? s.Stem) - 50;
            checks.Add(Bending("Stem in bending at its foot", forces.FootMoment, d, strength, us, "up its earth face"));
            checks.Add(Bending("Stem in bending up its height", forces.SpanMoment, d, strength, us, "up its inside face"));
            checks.Add(Shear(forces.FootShear, forces.FootMoment, d, strength, us));
            checks.Add(Bending("Base in bending", forces.FootMoment, s.Thickness - Reinforcement.CoverAgainstGround, strength, us, "across it, the stem's foot carried into it"));
            limits.Add("No bars are in the model: what each metre comes to is said.");
        }
        else if (masonry && !us)
        {
            var unit = MasonryDesign.UnitOf(material!);
            var gammas = MasonryDesign.Gammas(unit.Category, project.MasonryExecutionClass);
            var fd = MasonryDesign.Characteristic(unit) / gammas.Compression;
            var earth = Math.Max(0, s.Height - below);
            var height = wall.GetHeight(document);
            var dead = Loads.OnWall(document, wall, false) - s.StemWeight * earth / 2000;
            var imposed = Loads.OnWall(document, wall) - Loads.OnWall(document, wall, false);
            var outside = new List<string>();
            if (height > 2600) outside.Add($"it is {Units.Metres(height, "0.00")} high, over 2.6 m");
            if (core!.Thickness < 200) outside.Add($"it is {core.Thickness:0} thick, under 200");
            if (s.Surcharge > 5) outside.Add($"{s.Surcharge:0.#} kN/m² stands behind it, over 5");
            if (s.Slope > 0) outside.Add("the ground behind it rises");
            if (earth > height) outside.Add("the earth is higher than it");
            if (waterAt > below / 1000) outside.Add("water stands against it");
            if (outside.Count == 0)
            {
                checks.AddRange(BasementMasonry(height, earth, wall.Length, core.Thickness, s.Density, dead, 1.35 * dead + 1.5 * imposed, fd));
                limits.Add("By BS EN 1996-3 4.5's simplified method as remembered: no more than 2.6 m high and 200 thick at the least, the floor over it a diaphragm, no more than 5 kN/m² behind it and no load over 15 kN within 1.5 m, the ground level, no water against it; the UK National Annex's view of it not confirmed. Nothing in its bed may let it slide, a damp proof course of a sheet among them.");
            }
            else
            {
                checks.Add(MasonryStem(s, forces.SpanMoment, core.Thickness, MasonryDesign.FlexuralStrengths(unit, core.Thickness).Fxk1, fd, dead));
                limits.Add($"Outside BS EN 1996-3 4.5's simplified method - {string.Join(", ", outside)}: pinned at its foot and at the floor and bent across its height, what bears down on it at the middle of the earth helping it.");
            }
        }
        else limits.Add(us ? "Its stem as masonry is not worked out: IRC R404.1's tables of plain masonry foundation walls and TMS 402 could not be confirmed." : "Its stem is not worked out: it is neither concrete nor masonry.");

        limits.Add($"Held at its top by the floor over it, which takes {forces.Prop:0.0} kN/m at the ultimate across to the walls facing it; whether the basement as a whole slides or tips is not worked out here{(us ? ", nor whether it floats" : string.Empty)}.");
        limits.Add($"{Units.Metres(s.Height, "0.00")} of earth against it {pushed}, φ' {drawn}, with {s.Surcharge:0.#} kN/m² on it; " +
                   (s.Water > 0 ? $"water {Units.Metres(s.Water, "0.00")} up from under its base, its full head" : "drained behind it, the drain kept working") + ": a site investigation to say.");
        return new FoundationCheck(wall, name, checks, string.Join(" ", limits));
    }
}
