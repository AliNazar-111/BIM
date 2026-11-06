using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A metre of cantilever retaining wall: the stem's height above its base, its thickness and the base standing out behind it, the heel, and before it, the toe, all mm; what the stem weighs a
/// square metre of its face, kN/m²; what stands on the retained ground, kN/m²; the retained soil's angle of friction, degrees, and weight, kN/m³; the angle of friction under the base, degrees; and how far its underside is below the ground in front, mm.
/// </summary>
public sealed record RetainedSection(double Height, double Stem, double Heel, double Toe, double Thickness, double StemWeight, double Surcharge, double Friction, double Density, double BaseFriction, double Embedded = 0)
{
    public double Base => Heel + Stem + Toe;

    /// <summary>The depth the earth pushes over, from the retained surface to the underside of the base, m.</summary>
    public double Pushed => (Height + Thickness) / 1000;
}

/// <summary>
/// Retaining walls: whether each stands, and what it wants. The earth on a plane behind the heel, level and dry, with nothing taken for the ground in front. In England by BS EN 1997-1's Design Approach 1, as its UK National Annex
/// has it: sliding under Combination 1 (A1, M1) and Combination 2 (A2, M2), overturning as the static equilibrium of EQU, the load kept to the middle third of the base and the ground by its presumed bearing value. In the US by
/// IRC R404.4 and IBC 1807.2: a safety factor of 1.5 against sliding and overturning on nominal loads, the earth as the equivalent fluid of Table 1610.1. The stem and base as concrete, or a stem of unreinforced masonry by its flexural strength.
/// </summary>
public static class RetainingDesign
{
    private const double Concrete = 24, Psf = 0.0478803, PerFoot = Psf / 0.3048;

    /// <summary>The active pressure coefficient of a level surface against a vertical plane with no friction on it: Rankine's.</summary>
    public static double Active(double friction)
    {
        var sine = Math.Sin(friction * Math.PI / 180);
        return (1 - sine) / (1 + sine);
    }

    private static double Factored(double angle, double factor) => Math.Atan(Math.Tan(angle * Math.PI / 180) / factor) * 180 / Math.PI;

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

    /// <summary>IBC Table 1610.1's active pressure for the ground, kN/m³ of depth: 30 psf/ft for clean gravel, 45 for silty sand, 60 for lean clay. Null where it is no fit backfill.</summary>
    public static double? FluidPressure(GroundType ground) => ground switch
    {
        GroundType.Rock or GroundType.Gravel => 30 * PerFoot,
        GroundType.Sand or GroundType.LooseSand => 45 * PerFoot,
        GroundType.StiffClay or GroundType.FirmClay => 60 * PerFoot,
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

    /// <summary>What stands on the base, kN/m, and its moment about the toe, kNm/m: the stem, the base and the soil on the heel. The surcharge stops behind the heel, where it is no help.</summary>
    public static (double Weight, double Moment) Stands(RetainedSection s)
    {
        var (height, stem, heel, toe, thick, width) = (s.Height / 1000, s.Stem / 1000, s.Heel / 1000, s.Toe / 1000, s.Thickness / 1000, s.Base / 1000);
        var (ofStem, ofBase, ofSoil) = (s.StemWeight * height, Concrete * width * thick, s.Density * heel * height);
        return (ofStem + ofBase + ofSoil, ofStem * (toe + stem / 2) + ofBase * width / 2 + ofSoil * (toe + stem + heel / 2));
    }

    /// <summary>The load off the middle of the base and the most the ground takes, on characteristic loads: V/B (1 + 6e/B) in the middle third, 2V/3x past it.</summary>
    public static (double Eccentricity, double Pressure) OnTheGround(RetainedSection s, double overturning)
    {
        var (weight, moment) = Stands(s);
        var width = s.Base / 1000;
        var from = (moment - overturning) / weight;
        var off = width / 2 - from;
        return (off * 1000, from <= 0 ? double.PositiveInfinity : off <= width / 6 ? weight / width * (1 + 6 * off / width) : 2 * weight / (3 * from));
    }

    private static IEnumerable<StrengthCheck> GroundChecks(RetainedSection s, double overturning, double? allowable, string clause, string bearingClause)
    {
        var (off, pressure) = OnTheGround(s, overturning);
        yield return new StrengthCheck("Load off its middle", Math.Abs(off), s.Base / 6, "mm", clause, "kept to the middle third of its base");
        if (allowable is { } bears && !double.IsInfinity(pressure)) yield return new StrengthCheck("Ground under its toe", pressure, bears, "kPa", bearingClause);
    }

    /// <summary>Whether it stands by BS EN 1997-1's Design Approach 1. Nothing taken for the ground in front, so no unplanned excavation has to be allowed for.</summary>
    public static IReadOnlyList<StrengthCheck> StabilityEurocode(RetainedSection s, double? allowable)
    {
        var (weight, moment) = Stands(s);
        var pushed = s.Pushed;
        double Earth(double active) => 0.5 * active * s.Density * pushed * pushed;
        double Over(double active) => s.Surcharge * active * pushed;
        StrengthCheck Sliding(string name, double earth, double surcharge, double materials)
        {
            var active = Active(Factored(s.Friction, materials));
            var friction = Math.Tan(Factored(s.BaseFriction, materials) * Math.PI / 180);
            return new StrengthCheck("Sliding", earth * Earth(active) + surcharge * Over(active), weight * friction, "kN/m", "EN 1997-1 6.5.3, (6.3a)", $"{name}: Ka {active:0.00}, tan δ {friction:0.00}, nothing in front", name);
        }

        var first = Sliding("DA1-1", 1.35, 1.5, 1);
        var second = Sliding("DA1-2", 1.0, 1.3, 1.25);
        var checks = new List<StrengthCheck> { first.Ratio >= second.Ratio ? first : second };

        var design = Active(Factored(s.Friction, 1.25));
        var tipping = 1.1 * Earth(design) * pushed / 3 + 1.5 * Over(design) * pushed / 2;
        checks.Add(new StrengthCheck("Overturning", tipping, 0.9 * moment, "kNm/m", "BS EN 1990 EQU", $"about its toe, Ka {design:0.00}", "EQU"));

        var active = Active(s.Friction);
        checks.AddRange(GroundChecks(s, Earth(active) * pushed / 3 + Over(active) * pushed / 2, allowable, "BS 8002 practice", "BS 8004:1986 Table 1"));
        return checks;
    }

    /// <summary>Whether it stands by IRC R404.4 and IBC 1807.2.3: 1.5 against sliding and overturning, on nominal loads. The surcharge as added depth of earth; the base's friction or cohesion by Table 1806.2, with lateral bearing below grade in front, which 1806.3.1 lets them be combined with.</summary>
    public static IReadOnlyList<StrengthCheck> StabilityUs(RetainedSection s, double fluid, double friction, double cohesion, double? allowable, double lateral = 0)
    {
        var (weight, moment) = Stands(s);
        var pushed = s.Pushed;
        var (earth, over) = (0.5 * fluid * pushed * pushed, fluid * s.Surcharge / s.Density * pushed);
        var slides = earth + over;
        var bearing = 0.5 * lateral * Math.Pow(s.Embedded / 1000, 2);
        var holds = (friction > 0 ? friction * weight : Math.Min(cohesion * s.Base / 1000, weight / 2)) + bearing;
        var tips = earth * pushed / 3 + over * pushed / 2;
        var checks = new List<StrengthCheck>
        {
            new("Sliding, at 1.5", 1.5 * slides, holds, "kN/m", "IRC R404.4, IBC 1807.2.3", $"{(friction > 0 ? $"friction {friction:0.00} of its weight" : "130 psf over its base or half its weight")}, {bearing:0.0} of lateral bearing, Table 1806.2"),
            new("Overturning, at 1.5", 1.5 * tips, moment, "kNm/m", "IRC R404.4, IBC 1807.2.3", "about its toe")
        };
        checks.AddRange(GroundChecks(s, tips, allowable, "IBC 1807.2.1", "IRC Table R401.4.1"));
        return checks;
    }

    /// <summary>The moment and shear at the foot of the stem, kNm/m and kN/m, from the earth and the surcharge against it alone.</summary>
    public static (double Moment, double Shear) AtStemFoot(RetainedSection s, double earth, double surcharge)
    {
        var height = s.Height / 1000;
        var (triangle, block) = (0.5 * earth * height * height, surcharge * height);
        return (triangle * height / 3 + block * height / 2, triangle + block);
    }

    /// <summary>The worst of the design approach's two combinations at the foot of the stem.</summary>
    public static (double Moment, double Shear) StemForcesEurocode(RetainedSection s)
    {
        (double, double) Under(double earth, double surcharge, double materials)
        {
            var active = Active(Factored(s.Friction, materials));
            return AtStemFoot(s, earth * active * s.Density, surcharge * active * s.Surcharge);
        }

        var (first, second) = (Under(1.35, 1.5, 1), Under(1, 1.3, 1.25));
        return (Math.Max(first.Item1, second.Item1), Math.Max(first.Item2, second.Item2));
    }

    /// <summary>The stem as concrete, or by ACI 318-19, saying the bars each metre comes to; and the base in bending at the stem's two faces: the toe under the most the ground gives, the heel under what stands on it with nothing from below.</summary>
    public static IEnumerable<StrengthCheck> ConcreteParts(RetainedSection s, double moment, double shear, double groundPressure, double fc, bool us)
    {
        var d = s.Stem - 50;
        (double Needed, string? Problem) Steel(double m, double depth) => us
            ? ConcreteDesign.Aci(m, 1000, depth, fc, ConcreteDesign.LeastAci(1000, depth, fc))
            : ConcreteDesign.Eurocode(m, 1000, depth, fc, ConcreteDesign.LeastEurocode(1000, depth, fc));
        double Most(double depth) => us
            ? 0.9 * 0.85 * fc * 1000 * 0.375 * ConcreteDesign.Beta1(fc) * depth * (depth - 0.375 * ConcreteDesign.Beta1(fc) * depth / 2) / 1e6
            : 0.167 * 1000 * depth * depth * fc / 1e6;
        var (needed, problem) = Steel(moment, d);
        yield return new StrengthCheck("Stem in bending", moment, Most(d), "kNm/m", us ? "22.2" : "6.1", problem ?? $"needs {needed:0} mm²/m up its retained face");
        var share = Math.Min(0.02, needed / (1000 * d));
        var stress = us ? PlateDesign.OneWayAci(d, share, fc) : PlateDesign.OneWayEurocode(d, share, fc);
        yield return new StrengthCheck("Stem in shear", shear, stress * d, "kN/m", us ? "22.5" : "6.2.2");

        // Ground under the toe at the ultimate as its characteristic pressure by the permanent factor: the base's own weight, which would help, left out.
        var factor = us ? 1.6 : 1.35;
        var (toe, heel) = (s.Toe / 1000, s.Heel / 1000);
        var atToe = factor * (double.IsInfinity(groundPressure) ? 0 : groundPressure) * toe * toe / 2;
        var atHeel = (factor * (s.Density * s.Height / 1000 + Concrete * s.Thickness / 1000) + (us ? 1.6 : 1.5) * s.Surcharge) * heel * heel / 2;
        var worst = Math.Max(atToe, atHeel);
        var depth = s.Thickness - Reinforcement.CoverAgainstGround;
        var (forBase, tooThin) = Steel(worst, depth);
        yield return new StrengthCheck("Base in bending", worst, Most(depth), "kNm/m", us ? "22.2" : "6.1", tooThin ?? $"needs {forBase:0} mm²/m, {(atToe >= atHeel ? "underneath, in its toe" : "on top, in its heel")}");
    }

    /// <summary>The stem as unreinforced masonry, BS EN 1996-1-1 6.3.1: its flexural strength, with what the wall's own weight adds to it.</summary>
    public static StrengthCheck MasonryStem(RetainedSection s, double moment, double thickness, double fxk1, double design)
    {
        var modulus = 1000 * thickness * thickness / 6;
        var compression = 0.9 * s.StemWeight * s.Height / 1000 / thickness;
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

    /// <summary>Which way a wall holds its earth back: toward its exterior or not. The ground says, where there is a surface under it; else its exterior, which is what its Flip turns.</summary>
    public static bool RetainsExterior(BimDocument document, Wall wall, WallType type)
    {
        var surface = document.All<Toposurface>().FirstOrDefault(each => each.LevelId == wall.LevelId && Terrains.RetainingWalls(document, each).Any(pair => ReferenceEquals(pair.Wall, wall)));
        if (surface is null) return true;
        double Side(double side) => Enumerable.Range(0, 5).Select(k => Terrains.HeightAt(surface, wall.PointAt(type.Structure, wall.Length * (k + 0.5) / 5, side * (type.Structure.TotalWidth / 2 + 1000))))
            .Where(height => height is not null).Select(height => height!.Value).DefaultIfEmpty(double.NegativeInfinity).Average();
        return Side(1) >= Side(-1);
    }

    /// <summary>A retaining wall as a section: from its height over its footing's top, its thickness, and which side of it the footing stands out further.</summary>
    public static RetainedSection? SectionOf(BimDocument document, Wall wall, WallFooting footing, double friction, double density, double baseFriction)
    {
        if (document.GetWallType(wall) is not { } type || document.FindType<FootingType>(footing.TypeId) is not { } base_) return null;
        var stem = type.Structure.TotalWidth;
        var height = wall.GetTopElevation(document) - wall.GetBaseElevation(document) + Math.Max(0, footing.Depth - base_.Thickness);
        var offset = (RetainsExterior(document, wall, type) ? 1 : -1) * footing.Eccentricity;
        var (heel, toe) = (Math.Max(0, base_.Width / 2 + offset - stem / 2), Math.Max(0, base_.Width / 2 - offset - stem / 2));
        return new RetainedSection(height, stem, heel, toe, base_.Thickness, Loads.Weight(document, type.Structure), document.ProjectInformation.RetainedSurcharge, friction, density, baseFriction, footing.Depth);
    }

    /// <summary>Every retaining wall in the model, each called RW and a number in the order they are in it.</summary>
    public static IReadOnlyList<FoundationCheck> Of(BimDocument document)
    {
        var results = new List<FoundationCheck>();
        var walls = document.Walls.Where(wall => wall.Length > 1 && document.GetWallType(wall) is { Function: WallFunction.Retaining }).ToList();
        for (var i = 0; i < walls.Count; i++) results.Add(Check(document, walls[i], $"RW{i + 1}"));
        return results;
    }

    /// <summary>One retaining wall: whether it stands, and its stem and base.</summary>
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
        var (allowable, said) = StructuralAnalysis.AllowableBearing(document);
        var fluid = FluidPressure(project.Ground);
        if (us && fluid is null) return new FoundationCheck(wall, name, Array.Empty<StrengthCheck>(), "Not checked: IBC Table 1610.1 gives no load for this ground as backfill: a site investigation to say.");
        var (sliding, cohesion) = SlidingOf(project.Ground);
        IReadOnlyList<StrengthCheck> Stable(RetainedSection each) => us ? StabilityUs(each, fluid!.Value, sliding, cohesion, allowable, LateralBearing(project.Ground)) : StabilityEurocode(each, allowable);

        var checks = Stable(section).ToList();
        var pressure = checks.FirstOrDefault(check => check.Name == "Ground under its toe")?.Demand ?? OnTheGround(section, 0).Pressure;
        var (moment, shear) = us
            ? (AtStemFoot(section, 1.6 * fluid!.Value, 1.6 * fluid.Value * section.Surcharge / section.Density).Moment, AtStemFoot(section, 1.6 * fluid.Value, 1.6 * fluid.Value * section.Surcharge / section.Density).Shear)
            : StemForcesEurocode(section);

        var limits = new List<string>();
        var core = type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).OrderByDescending(layer => layer.Thickness).FirstOrDefault();
        var material = core is null ? null : document.FindMaterial(core.MaterialId);
        if (AnalyticalModel.IsConcreteWall(document, wall))
        {
            var strength = material is null ? 28 : ConcreteDesign.StrengthOf(material.Name);
            checks.AddRange(ConcreteParts(section with { Stem = core?.Thickness ?? section.Stem }, moment, shear, pressure, strength, us));
            limits.Add("No bars are in the model: what each metre comes to is said.");
        }
        else if (!us && material is not null && core is not null && material.Name.Contains("block", StringComparison.OrdinalIgnoreCase) | material.Name.Contains("brick", StringComparison.OrdinalIgnoreCase) | material.Name.Contains("masonry", StringComparison.OrdinalIgnoreCase))
        {
            var unit = MasonryDesign.UnitOf(material);
            checks.Add(MasonryStem(section, moment, core!.Thickness, MasonryDesign.FlexuralStrengths(unit, core.Thickness).Fxk1, MasonryDesign.Characteristic(unit) / MasonryDesign.GammaCompression));
        }
        else limits.Add(us ? "Its stem as masonry is not worked out: TMS 402 could not be confirmed." : "Its stem is not worked out: it is neither concrete nor masonry.");

        var wanted = BaseWanted(section, Stable);
        var stands = Stable(section).All(check => check.Ratio <= 1);
        if (!stands) limits.Insert(0, wanted is { } width ? $"It wants a base about {Math.Ceiling(width / 50) * 50:0} wide. The extra goes behind the stem under the earth; a key under it would do, and is not worked out here." : "No base under 8 m stands it: hold it some other way.");
        if (!stands && us) limits.Add("Table 1806.2's presumptive values are low on clay and silt: a soils report's friction will usually do better.");
        if (allowable is null) limits.Add($"{said}.");
        var drawn = project.RetainedFriction > 0 ? $"{friction:0}° as set" : $"{friction:0}°, typical for the ground";
        limits.Add($"{Units.Metres(section.Pushed * 1000, "0.00")} of earth from its top to under its base, φ' {drawn}, with {section.Surcharge:0.#} kN/m² on it; dry behind it, the ground in front not counted, a site investigation to say. {(us ? "IRC R404.4 asks for this past 48 in of unbalanced fill." : string.Empty)}".TrimEnd());
        return new FoundationCheck(wall, name, checks, string.Join(" ", limits));
    }
}
