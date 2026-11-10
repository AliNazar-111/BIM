using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a building is used for, as far as how long it has to stand in a fire and how robust it has to be go by it.</summary>
public enum BuildingUse
{
    House,
    Flats,
    Office,
    Other
}

/// <summary>How long a building has to stand in a fire, minutes - none where its rules ask for none or are not worked out here - and why.</summary>
public sealed record FirePeriod(int Minutes, string Said);

/// <summary>An element in a fire: what it was checked by, what it wants if it falls short, and what the check leaves out. No checks means it could not be judged, which its limits say.</summary>
public sealed record FireCheck(string Name, string Kind, int Minutes, IReadOnlyList<StrengthCheck> Checks, string Wants, string Limits)
{
    public StrengthCheck? Worst => Checks.Count == 0 ? null : Checks.MaxBy(check => check.Ratio);

    public double Ratio => Worst?.Ratio ?? 0;

    public bool Enough => Ratio <= 1 + 1e-9;
}

/// <summary>
/// Steel in a fire by BS EN 1993-1-2: how hot it gets under the standard fire of BS EN 1991-1-2 3.2.1 by 4.2.5.1, and how hot it can get
/// before what it carries is more than it can, its strength and stiffness falling by Table 3.1 and buckling by 4.2.3.
/// </summary>
public static class SteelFire
{
    private const double E = 210000;

    // Table 3.1: temperature, effective yield strength, slope of the elastic range.
    private static readonly (double At, double Yield, double Stiffness)[] Reductions =
    {
        (20, 1, 1), (100, 1, 1), (200, 1, 0.9), (300, 1, 0.8), (400, 1, 0.7), (500, 0.78, 0.6), (600, 0.47, 0.31), (700, 0.23, 0.13),
        (800, 0.11, 0.09), (900, 0.06, 0.0675), (1000, 0.04, 0.045), (1100, 0.02, 0.0225), (1200, 0, 0)
    };

    private static double Between(double at, Func<(double At, double Yield, double Stiffness), double> read)
    {
        if (at <= Reductions[0].At) return read(Reductions[0]);
        for (var i = 1; i < Reductions.Length; i++)
            if (at <= Reductions[i].At)
                return read(Reductions[i - 1]) + (read(Reductions[i]) - read(Reductions[i - 1])) * (at - Reductions[i - 1].At) / (Reductions[i].At - Reductions[i - 1].At);
        return 0;
    }

    /// <summary>How much of its yield strength steel keeps at a temperature.</summary>
    public static double Yield(double temperature) => Between(temperature, row => row.Yield);

    /// <summary>How much of its elastic modulus steel keeps at a temperature.</summary>
    public static double Stiffness(double temperature) => Between(temperature, row => row.Stiffness);

    /// <summary>The standard fire's gas temperature after so many minutes, BS EN 1991-1-2 (3.4).</summary>
    public static double Gas(double minutes) => 20 + 345 * Math.Log10(8 * minutes + 1);

    /// <summary>Steel's specific heat at a temperature, J/kg K, 3.4.1.2.</summary>
    public static double SpecificHeat(double temperature) => temperature switch
    {
        < 600 => 425 + 0.773 * temperature - 1.69e-3 * temperature * temperature + 2.22e-6 * temperature * temperature * temperature,
        < 735 => 666 + 13002 / (738 - temperature),
        < 900 => 545 + 17820 / (temperature - 731),
        _ => 650
    };

    /// <summary>
    /// Bare steel's temperature after so many minutes of the standard fire, by 4.2.5.1 in steps of 5 seconds: heat by convection at 25 W/m²K and radiation at an emissivity of 0.7,
    /// <paramref name="factor"/> being the shadow factor times the section factor, /m.
    /// </summary>
    public static double Temperature(double factor, double minutes)
    {
        const double step = 5, density = 7850, convection = 25, emissivity = 0.7, boltzmann = 5.67e-8;
        var steel = 20.0;
        for (var seconds = step; seconds <= minutes * 60 + 1e-9; seconds += step)
        {
            var gas = Gas(seconds / 60);
            var flux = convection * (gas - steel) + emissivity * boltzmann * (Math.Pow(gas + 273, 4) - Math.Pow(steel + 273, 4));
            steel += factor / (SpecificHeat(steel) * density) * flux * step;
        }

        return steel;
    }

    /// <summary>The critical temperature of a member that cannot buckle, °C, by its degree of utilisation: (4.22).</summary>
    public static double Critical(double utilisation) =>
        39.19 * Math.Log(1 / (0.9674 * Math.Pow(Math.Max(utilisation, 0.013), 3.833)) - 1) + 482;

    /// <summary>
    /// A section's factors, /m: as the fire reaches it, its surface on what is exposed over its area, and as a box round it, which is what a board sees. An I's shadow is 0.9 of the one over the other, (4.26a).
    /// </summary>
    public static (double Profile, double Box, double Shadow) Factors(SteelSection section, bool threeSides)
    {
        var (h, b) = (section.H, section.B);
        var box = (threeSides ? b + 2 * h : 2 * (b + h)) * 1000 / section.Area;
        if (!section.IsI) return (box, box, 1);
        var profile = (4 * b + 2 * h - 2 * section.Tw - (threeSides ? b : 0)) * 1000 / section.Area;
        return (profile, box, 0.9 * box / profile);
    }

    /// <summary>What it is carrying over what it can at a temperature, buckling as it heats up: 4.2.3.2, 4.2.3.3 and (4.21), a moment taken as falling to none at one end, βM 1.8. Lengths as cold.</summary>
    public static double Utilisation(SteelSection s, MemberLengths lengths, MemberForces f, double temperature, double kappa = 1)
    {
        var fy = SteelDesign.YieldStrength(Math.Max(s.Tf, s.Tw));
        var (ky, ke) = (Yield(temperature), Stiffness(temperature));
        if (ky <= 0) return double.PositiveInfinity;
        var squash = s.Area * ky * fy;
        var (bendY, bendZ) = (s.Wply * ky * fy, s.Wplz * ky * fy);
        var (my, mz) = (Math.Abs(f.My) * 1e6, Math.Abs(f.Mz) * 1e6);
        var n = Math.Abs(f.N) * 1e3;
        var alpha = 0.65 * Math.Sqrt(235 / fy);
        double Hot(double slender) => slender * Math.Sqrt(ky / ke);
        double Reduced(double slender)
        {
            var phi = 0.5 * (1 + alpha * slender + slender * slender);
            return Math.Min(1, 1 / (phi + Math.Sqrt(phi * phi - slender * slender)));
        }

        double Cold(double length, double second) => length <= 0 ? 0 : Math.Sqrt(s.Area * fy / (Math.PI * Math.PI * E * second / (length * length)));
        var lateral = f.TopInTension ? lengths.LateralHogging : lengths.Lateral;
        var buckling = s.IsI && lateral > 0 && my > 0 ? Reduced(Hot(Math.Sqrt(s.Wply * fy / SteelDesign.CriticalMoment(s, lateral)))) : 1;
        var moment = my / (buckling < 1 ? bendY * buckling : bendY / kappa) + mz / bendZ;
        if (f.N >= 0) return n / squash + moment;

        const double beta = 1.8;
        var (coldY, coldZ) = (Cold(lengths.Strong, s.Iy), Cold(lengths.Weak, s.Iz));
        var (hotY, hotZ) = (Hot(coldY), Hot(coldZ));
        var (chiY, chiZ) = (Reduced(hotY), Reduced(hotZ));
        var least = Math.Min(chiY, chiZ);
        var muY = Math.Min(0.8, (2 * beta - 5) * Hot(Math.Min(coldY, 1.1)) + 0.44 * beta + 0.29);
        var muZ = Math.Min(0.8, (1.2 * beta - 3) * hotZ + 0.71 * beta - 0.29);
        var kY = Math.Min(3, 1 - muY * n / (chiY * squash));
        var kZ = Math.Min(3, 1 - muZ * n / (chiZ * squash));
        return n / (least * squash) + kY * my / bendY / buckling + kZ * mz / bendZ;
    }

    /// <summary>The temperature at which it can carry what it carries no longer: a member buckling or not, by bisection of <see cref="Utilisation"/>.</summary>
    public static double CriticalOf(SteelSection s, MemberLengths lengths, MemberForces f, double kappa = 1)
    {
        if (Utilisation(s, lengths, f, 20, kappa) > 1) return 20;
        var (low, high) = (20.0, 1200.0);
        for (var i = 0; i < 40; i++)
        {
            var middle = (low + high) / 2;
            if (Utilisation(s, lengths, f, middle, kappa) <= 1) low = middle;
            else high = middle;
        }

        return low;
    }
}

/// <summary>Timber in a fire by BS EN 1995-1-2: burning away at its notional charring rate of Table 3.1, a further layer of no strength beside the char by 4.2.2, and what is left taken at its 20th percentile strength.</summary>
public static class TimberFire
{
    /// <summary>How deep a face has gone after so many minutes, mm: (4.1), the zero strength layer growing to 7 mm over the first 20 minutes by Table 4.1.</summary>
    public static double Effective(double minutes, bool glulam) => (glulam ? 0.7 : 0.8) * minutes + Math.Min(1, minutes / 20) * 7;

    /// <summary>What boards are counted of a lining, mm: (3.4.3.3): one layer, or the outer with half the inner, each of gypsum plasterboard, in the order they are in the build-up, outermost last.</summary>
    public static double Counted(IReadOnlyList<double> boards) => boards.Count == 0 ? 0 : boards[^1] + (boards.Count > 1 ? 0.5 * boards[^2] : 0);

    /// <summary>When plasterboard of type A or H lets the fire reach what is behind it, minutes: (3.11), 2.8 hp - 14. It falls off then, (3.15), so this is its failure time as well.</summary>
    public static double StartOfCharring(double lining) => Math.Max(0, 2.8 * lining - 14);

    /// <summary>
    /// How deep a face behind a lining has gone, mm: nothing until the board falls off, then at twice the rate until the charring has caught up with what it would have been or gone 25 mm, then at the rate of Table 3.1: (3.8), the zero strength layer
    /// growing over the time to the start of charring where that is more than 20 minutes, 4.2.2(3). With no lining, <see cref="Effective"/>.
    /// </summary>
    public static double Behind(double minutes, double lining, bool glulam)
    {
        var start = StartOfCharring(lining);
        if (start <= 0) return Effective(minutes, glulam);
        var rate = glulam ? 0.7 : 0.8;
        const double doubled = 2;
        var until = Math.Min(2 * start, 25 / (doubled * rate) + start);
        var charred = minutes <= start ? 0 : minutes <= until ? doubled * rate * (minutes - start) : doubled * rate * (until - start) + rate * (minutes - until);
        return charred + (start > 20 ? Math.Min(1, minutes / start) : Math.Min(1, minutes / 20)) * 7;
    }

    private static double Between((double At, double Value)[] table, double at)
    {
        if (at <= table[0].At) return table[0].Value;
        for (var i = 1; i < table.Length; i++)
            if (at <= table[i].At) return table[i - 1].Value + (table[i].Value - table[i - 1].Value) * (at - table[i - 1].At) / (table[i].At - table[i - 1].At);
        return table[^1].Value;
    }

    /// <summary>
    /// A stud in a wall with its cavity full of rock fibre, by Annex C, a fire on one side and at most 60 minutes: how deep it has charred from the fire's side, mm, (C.2) with β0 0.65, kn 1.5, ks by Table C1 and k3 0.036 tf + 1;
    /// how much of its strength is left, (C.13) and Table C2; and of its stiffness across the wall, (C.14) and Table C4. Null past where the annex goes.
    /// </summary>
    public static (double Charred, double Strength, double Stiffness)? Stud(double minutes, double lining, double depth, double width)
    {
        if (minutes > 60) return null;
        var failed = StartOfCharring(lining);
        var charred = Math.Max(0, minutes - failed) * Between(new[] { (38.0, 1.4), (45.0, 1.3), (60.0, 1.1) }, width) * (0.036 * failed + 1) * 1.5 * 0.65;
        var strength = Between(new[] { (95.0, 0.46), (145.0, 0.55), (195.0, 0.65), (220.0, 0.67) }, depth) - Between(new[] { (95.0, 0.37), (145.0, 0.40), (195.0, 0.48), (220.0, 0.47) }, depth) * charred / depth;
        var stiffness = Between(new[] { (95.0, 0.50), (145.0, 0.60), (195.0, 0.68) }, depth) - Between(new[] { (95.0, 0.79), (145.0, 0.84), (195.0, 0.77) }, depth) * charred / depth;
        return (charred, Math.Max(0, strength), Math.Max(0, stiffness));
    }

    /// <summary>The strength class as a fire finds it: kfi of Table 2.1 on its strengths and stiffnesses, its partial factor undone, for γM,fi is 1.0 and kmod,fi is 1.0, so the checks at normal temperature can be used.</summary>
    public static TimberGrade Hot(TimberGrade grade)
    {
        var factor = (grade.Glulam ? 1.15 : 1.25) * grade.GammaM;
        return grade with { Fm = grade.Fm * factor, Ft0 = grade.Ft0 * factor, Fc0 = grade.Fc0 * factor, Fv = grade.Fv * factor, E0Mean = grade.E0Mean * factor, E05 = grade.E05 * factor };
    }

    /// <summary>A section's depth and width once a fire has been at it for so long: from the sides and beneath, or all round a post.</summary>
    public static (double Depth, double Width) Left(double depth, double width, double minutes, bool glulam, bool allRound, double lining = 0)
    {
        var gone = Behind(minutes, lining, glulam);
        return (depth - (allRound ? 2 : 1) * gone, width - 2 * gone);
    }
}

/// <summary>Concrete in a fire by the tables of BS EN 1992-1-2: the least size of a section and how far its bars must sit from the face, each table's rows at 30, 60, 90, 120, 180 and 240 minutes.</summary>
public static class ConcreteFire
{
    private static readonly int[] Periods = { 30, 60, 90, 120, 180, 240 };

    private static int Row(int minutes)
    {
        for (var i = 0; i < Periods.Length; i++)
            if (minutes <= Periods[i]) return i;
        return Periods.Length - 1;
    }

    private static readonly (double Width, double Axis)[][] Beams =
    {
        new[] { (80.0, 25.0), (120, 20), (160, 15), (200, 15) }, new[] { (120.0, 40.0), (160, 35), (200, 30), (300, 25) }, new[] { (150.0, 55.0), (200, 45), (300, 40), (400, 35) },
        new[] { (200.0, 65.0), (240, 60), (300, 55), (500, 50) }, new[] { (240.0, 80.0), (300, 70), (400, 65), (600, 60) }, new[] { (280.0, 90.0), (350, 80), (500, 75), (700, 70) }
    };

    // Table 5.2a, exposed on more than one side, by the load in fire: 0.2, 0.5 and 0.7 of what it carries cold.
    private static readonly (double Width, double Axis)[][][] Columns =
    {
        new[] { new[] { (200.0, 25.0) }, new[] { (200.0, 25.0) }, new[] { (200.0, 32.0), (300, 27) } },
        new[] { new[] { (200.0, 25.0) }, new[] { (200.0, 36.0), (300, 31) }, new[] { (250.0, 46.0), (350, 40) } },
        new[] { new[] { (200.0, 31.0), (300, 25) }, new[] { (300.0, 45.0), (400, 38) }, new[] { (350.0, 53.0), (450, 40) } },
        new[] { new[] { (250.0, 40.0), (350, 35) }, new[] { (350.0, 45.0), (450, 40) }, new[] { (350.0, 57.0), (450, 51) } },
        new[] { new[] { (350.0, 45.0) }, new[] { (350.0, 63.0) }, new[] { (450.0, 70.0) } },
        new[] { new[] { (350.0, 61.0) }, new[] { (450.0, 75.0) }, Array.Empty<(double, double)>() }
    };

    // Table 5.4: by the load in fire, 0.35 and 0.7, and by being exposed on one side or two.
    private static readonly (double Thickness, double Axis)[][] Walls =
    {
        new[] { (100.0, 10.0), (120, 10), (120, 10), (120, 10) }, new[] { (110.0, 10.0), (120, 10), (130, 10), (140, 10) }, new[] { (120.0, 20.0), (140, 10), (140, 25), (170, 25) },
        new[] { (150.0, 25.0), (160, 25), (160, 35), (220, 35) }, new[] { (180.0, 40.0), (200, 45), (210, 50), (270, 55) }, new[] { (230.0, 55.0), (250, 55), (270, 60), (350, 60) }
    };

    // Tables 5.8 and 5.9: thickness and axis distance of a slab spanning one way, a slab spanning both ways on four edges up to 1.5 long, and a flat slab.
    private static readonly (double Thickness, double OneWay, double TwoWay)[] Slabs = { (60, 10, 10), (80, 20, 10), (100, 30, 15), (120, 40, 20), (150, 55, 30), (175, 65, 40) };

    private static readonly (double Thickness, double Axis)[] FlatSlabs = { (150, 10), (180, 15), (200, 25), (200, 35), (200, 45), (200, 50) };

    /// <summary>The least the table lets a section be, and the axis distance it wants at the size it is: a wider section may have its bars nearer the face.</summary>
    private static (double Least, double Axis) Pick(IReadOnlyList<(double Width, double Axis)> options, double width)
    {
        var fits = options.Where(option => option.Width <= width + 1e-6).ToList();
        return (options[0].Width, fits.Count > 0 ? fits[^1].Axis : options[0].Axis);
    }

    /// <summary>A simply supported beam's least width and the axis distance for the width it has, Table 5.5.</summary>
    public static (double Width, double Axis) Beam(int minutes, double width) => Pick(Beams[Row(minutes)], width);

    /// <summary>A column's least width and axis distance for how much of what it carries cold it carries in fire, Table 5.2a; null past 0.7, or where the table has nothing.</summary>
    public static (double Width, double Axis)? Column(int minutes, double load, double width)
    {
        if (load > 0.7) return null;
        var options = Columns[Row(minutes)][load <= 0.2 ? 0 : load <= 0.5 ? 1 : 2];
        return options.Length == 0 ? null : Pick(options, width);
    }

    /// <summary>A loadbearing wall's least thickness and axis distance, Table 5.4.</summary>
    public static (double Thickness, double Axis) Wall(int minutes, bool heavilyLoaded, bool bothSides) => Walls[Row(minutes)][(heavilyLoaded ? 2 : 0) + (bothSides ? 1 : 0)];

    /// <summary>A solid slab's least thickness and axis distance: one way, Table 5.8, which is also what a two-way slab comes to if it is not held on four edges; or flat, Table 5.9.</summary>
    public static (double Thickness, double Axis) Slab(int minutes, bool flat)
    {
        var row = Row(minutes);
        return flat ? FlatSlabs[row] : (Slabs[row].Thickness, Slabs[row].OneWay);
    }

    /// <summary>What a column carries cold, kN: its concrete at fcd and steel at 1% of it, as Table 5.2a's load ratio wants.</summary>
    public static double Squash(double area, double fck) => (area * fck / 1.5 + 0.01 * area * 500 / 1.15) / 1000;
}

/// <summary>
/// How long a building has to stand in a fire, and whether what holds it up will. In England the period is Approved Document B's Table B4, by what the building is used for, how high its top storey's floor is and
/// whether it has sprinklers, for what 5.3 calls an element of structure: not a structure that carries only a roof, not the lowest floor. An assembly stands by its type's Fire Rating, tested as it is built; steel and timber
/// without one are worked out bare by BS EN 1993-1-2 and BS EN 1995-1-2 under BS EN 1990's accidental combination; concrete is sized by the tables of BS EN 1992-1-2. Masonry is left to its type's Fire Rating, the tables of
/// BS EN 1996-1-2 not having been read.
/// </summary>
public static class FireDesign
{
    /// <summary>What the project says the building is. Nothing set is a house.</summary>
    public static BuildingUse UseOf(BimDocument document)
    {
        var type = document.ProjectInformation.BuildingType;
        var words = type.ToLowerInvariant().Split(new[] { ' ', ',', '-', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries);
        bool Is(params string[] names) => words.Any(word => names.Any(name => word.StartsWith(name, StringComparison.Ordinal)));
        if (Is("office")) return BuildingUse.Office;
        if (Is("flat", "apartment")) return BuildingUse.Flats;
        return type.Trim().Length == 0 || Is("house", "home", "dwelling", "bungalow", "extension") ? BuildingUse.House : BuildingUse.Other;
    }

    /// <summary>How many storeys stand on or above the ground, and how high the floor of the top one is, m: a floor makes a storey as much as the walls on it, and a level with neither does not.</summary>
    public static (int Storeys, double TopFloor) HeightOf(BimDocument document)
    {
        var ground = Wind.Ground(document);
        var feet = document.Walls.Select(wall => wall.GetBaseElevation(document))
            .Concat(document.All<Column>().Select(column => document.FindLevel(column.LevelId)?.Elevation ?? ground))
            .Concat(document.All<Floor>().Select(floor => floor.GetTopElevation(document)))
            .Where(foot => foot >= ground - 1).Select(foot => Math.Round(foot / 100) * 100).Distinct().ToList();
        return feet.Count == 0 ? (0, 0) : (feet.Count, (feet.Max() - ground) / 1000);
    }

    /// <summary>Table B4's period for a building, with note 5's 30 minutes for a three storey house.</summary>
    public static FirePeriod Period(BuildingUse use, int storeys, double topFloor, bool sprinklered)
    {
        const string table = "Approved Document B Table B4";
        var height = $"{storeys} storey{(storeys == 1 ? "" : "s")}, its top floor {topFloor:0.0} m up";
        switch (use)
        {
            case BuildingUse.House:
                if (topFloor > 18) return new FirePeriod(0, $"A house with a floor over 18 m is outside {table}, note 4.");
                return new FirePeriod(topFloor <= 5 || storeys <= 3 ? 30 : 60, $"{(topFloor <= 5 || storeys <= 3 ? 30 : 60)} minutes: a house of {height}, {table}");
            case BuildingUse.Flats:
                if (topFloor > 30 && !sprinklered) return new FirePeriod(120, $"120 minutes: flats of {height}, and over 30 m they want sprinklers, {table} note 2");
                var flats = topFloor <= 5 ? 30 : topFloor <= 18 ? 60 : topFloor <= 30 ? 90 : 120;
                return new FirePeriod(flats, $"{flats} minutes: flats of {height}, {table}");
            case BuildingUse.Office:
                if (topFloor > 30 && !sprinklered) return new FirePeriod(120, $"120 minutes: an office of {height}, and over 30 m it wants sprinklers, {table} note 6");
                var office = topFloor <= 5 ? 30 : topFloor <= 18 ? (sprinklered ? 30 : 60) : topFloor <= 30 ? (sprinklered ? 60 : 90) : 120;
                return new FirePeriod(office, $"{office} minutes: an office of {height}{(sprinklered ? ", with sprinklers" : "")}, {table}");
            default:
                return new FirePeriod(0, "Set the Building Type in the plan's Properties: only houses, flats and offices are worked out here.");
        }
    }

    /// <summary>
    /// IBC Table 601's hours for a type of construction, as remembered: the primary structural frame, exterior and interior bearing walls, and floors with their secondary members; Type IV's
    /// heavy timber taken as none, its sizes being IBC 2304.11's, not worked out here. Its footnotes - a roof's, sprinklers in place of a rating - are not applied.
    /// </summary>
    public static (double Frame, double Exterior, double Interior, double Floor) Ibc601(ConstructionType type) => type switch
    {
        ConstructionType.IA => (3, 3, 3, 2),
        ConstructionType.IB => (2, 2, 2, 2),
        ConstructionType.IIA or ConstructionType.VA => (1, 1, 1, 1),
        ConstructionType.IIIA => (1, 2, 1, 1),
        ConstructionType.IIIB => (0, 2, 0, 0),
        ConstructionType.IV => (0, 2, 0, 0),
        _ => (0, 0, 0, 0)
    };

    /// <summary>
    /// The period the building as modelled wants. In the US: a one- or two-family dwelling's structure needs none under the IRC; any other building goes by its type of construction and IBC
    /// Table 601, each element its own hours, this the most of them.
    /// </summary>
    public static FirePeriod PeriodOf(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            if (UseOf(document) == BuildingUse.House)
                return new FirePeriod(0, "A one- or two-family dwelling's structure needs no rating under the IRC, beyond what separates it from a garage or another dwelling (R302).");
            var type = document.ProjectInformation.ConstructionType;
            if (type == ConstructionType.NotSet) return new FirePeriod(0, "IBC Table 601 goes by a type of construction: set the Construction Type in the plan's Properties.");
            var hours = Ibc601(type);
            var most = Math.Max(Math.Max(hours.Frame, hours.Exterior), Math.Max(hours.Interior, hours.Floor));
            return new FirePeriod((int)Math.Round(60 * most), $"Type {type}, IBC Table 601: {hours.Frame:0.#} h for its frame, {hours.Exterior:0.#} and {hours.Interior:0.#} h for its exterior and interior bearing walls, {hours.Floor:0.#} h for its floors{(type == ConstructionType.IV ? "; heavy timber (HT) by its sizes, not worked out" : string.Empty)}");
        }

        var (storeys, top) = HeightOf(document);
        return Period(UseOf(document), storeys, top, document.ProjectInformation.Sprinklered);
    }

    /// <summary>What a building carries in a fire: its weight and the frequent value of what is on it, BS EN 1990 (6.11b) with ψ1, the larger of the two BS EN 1991-1-2 4.3.1 lets a National Annex choose between; wind and snow left to their ψ.</summary>
    public static LoadCombination Combination(BimDocument document, StructuralLoading loading) =>
        new("Accidental", "BS EN 1990 6.11b", LimitState.Ultimate, loading.Cases.Select(each => (each, each.Nature is LoadNature.SelfWeight or LoadNature.Dead ? 1.0 : each.Nature is LoadNature.Imposed ? StructuralLoading.Psi(document, each.Nature).Frequent : 0.0))
            .Where(term => term.Item2 > 0).ToList());

    /// <summary>What a member carries in a US fire, as NDS Chapter 16 takes it with allowable stress design's capacities: D + L.</summary>
    public static LoadCombination CombinationUs(StructuralLoading loading) =>
        new("Fire, D + L", "NDS 16.2, allowable stress design", LimitState.Ultimate, loading.Cases.Where(each => each.Nature is LoadNature.SelfWeight or LoadNature.Dead or LoadNature.Imposed).Select(each => (each, 1.0)).ToList());

    /// <summary>
    /// Every element of structure the building has, those short first; nothing if no period applies. Each element its own minutes: in the UK all of them Table B4's, a separating wall at least 60;
    /// in the US by IBC Table 601 for its type of construction. With the joints, each steel one by BS EN 1993-1-2 4.2.1(6) against the members it joins.
    /// </summary>
    public static IReadOnlyList<FireCheck> Of(BimDocument document, SecondOrderAnalysis second, IReadOnlyList<MemberCheck> members, IReadOnlyList<JointCheck>? joints = null)
    {
        var period = PeriodOf(document);
        if (period.Minutes == 0) return Array.Empty<FireCheck>();
        var analysis = second.Analysis;
        var model = analysis.Model;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var hours = Ibc601(document.ProjectInformation.ConstructionType);
        var fire = us ? CombinationUs(analysis.Loading) : Combination(document, analysis.Loading);
        int ForMember(AnalyticalMemberKind kind) => us ? (int)Math.Round(60 * (kind == AnalyticalMemberKind.Joist ? hours.Floor : hours.Frame)) : period.Minutes;
        int ForWall(bool exterior, bool separating) => us ? (int)Math.Round(60 * (exterior ? hours.Exterior : hours.Interior)) : separating ? Math.Max(60, period.Minutes) : period.Minutes;
        var forFloor = us ? (int)Math.Round(60 * hours.Floor) : period.Minutes;
        var heated = new HashSet<AnalyticalMember>();
        var ground = Wind.Ground(document);
        var floors = document.All<Floor>().Where(floor => floor.GetTopElevation(document) > ground + 300).ToList();
        var results = new List<FireCheck>();

        bool CarriesFloor(double foot) => floors.Any(floor => floor.GetTopElevation(document) > foot + 300);
        foreach (var check in members.Where(each => each.Member.Kind != AnalyticalMemberKind.GroundBeam))
        {
            var member = check.Member;
            var (start, end) = (member.Start.At, member.End.At);
            var (foot, middle) = (Math.Min(start.Z, end.Z), new Point2D((start.X + end.X) / 2, (start.Y + end.Y) / 2));
            var holding = member.Kind == AnalyticalMemberKind.Column
                ? CarriesFloor(foot)
                : floors.Any(floor => Math.Abs(floor.GetTopElevation(document) - foot) <= 600);
            if (!holding || ForMember(member.Kind) == 0) continue;
            heated.Add(member);
            results.Add(Member(document, analysis, fire, check, ForMember(member.Kind), floors, middle, foot));
        }

        // Steel joints, BS EN 1993-1-2 4.2.1(6): one whose use at room temperature is no more than the most of the members it joins, protected as they are, stands as long as they do.
        if (!us && joints is not null)
        {
            var used = members.ToDictionary(each => each.Member, each => each.Ratio);
            foreach (var joint in joints.Where(joint => joint.Joined is { Count: > 0 } && joint.Checks.Count > 0 && joint.Joined.All(each => each.Section.Material.Kind == AnalyticalMaterialKind.Steel) && joint.Joined.Any(heated.Contains)))
            {
                var most = joint.Joined.Max(each => used.GetValueOrDefault(each));
                if (most <= 1e-6) continue;
                results.Add(new FireCheck(joint.Name, "Joint", period.Minutes, new[] { new StrengthCheck("Used no more than its members", joint.Ratio, most, "", "BS EN 1993-1-2 4.2.1(6)", $"{joint.Kind}, {joint.Ratio:P0} against {most:P0} at room temperature") },
                    joint.Ratio <= most + 1e-9 ? string.Empty : "a joint no more used than what it joins, or its resistance in fire by Annex D",
                    "Protected as the members it joins are, or bare where they are bare: at least the protection of the least protected of them, 4.2.1(6)."));
            }
        }

        foreach (var panel in model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Element is Wall))
        {
            var wall = (Wall)panel.Element;
            var type = document.GetWallType(wall);
            var separating = type is { Separating: true };
            if (!separating && !CarriesFloor(wall.GetBaseElevation(document))) continue;
            var minutes = ForWall(type is { Function: WallFunction.Exterior }, separating);
            if (minutes > 0) results.Add(WallOf(document, second, panel, wall, minutes));
        }

        var bearing = model.Panels.Select(panel => panel.Element).OfType<Wall>().ToHashSet();
        if (!us)
            foreach (var wall in document.Walls.Where(wall => !bearing.Contains(wall) && document.GetWallType(wall) is { Separating: true }))
                results.Add(ByRating(document.GetWallType(wall)!.Name, "Wall", Math.Max(60, period.Minutes), Doors.FireMinutes(document.GetWallType(wall)!.FireRating), "Give its type a Fire Rating from its maker's tested data."));

        if (forFloor > 0)
            foreach (var floor in floors)
            {
                var panel = model.Panels.FirstOrDefault(each => ReferenceEquals(each.Element, floor) && each.Kind == AnalyticalPanelKind.Floor && each.Material.Kind == AnalyticalMaterialKind.Concrete);
                results.Add(panel is null ? FloorByRating(document, floor, forFloor) : us ? ConcreteUs(panel.Name, "Floor", panel.Thickness, forFloor) : ConcreteFloor(model, panel, forFloor));
            }

        return results.OrderByDescending(each => each.Checks.Count == 0 ? double.MaxValue : each.Ratio).ToList();
    }

    /// <summary>
    /// The least equivalent thickness of a concrete wall or floor for its hours, inches, ACI 216.1 Table 4.2 as IBC Table 722.2.1.1 has it, as remembered, for siliceous aggregate, the most asked:
    /// 3.5 in for 1 h, 4.3 for 1.5, 5.0 for 2, 6.2 for 3, 7.0 for 4.
    /// </summary>
    public static double AciThickness(double hours) => hours <= 1 ? 3.5 : hours <= 1.5 ? 4.3 : hours <= 2 ? 5.0 : hours <= 3 ? 6.2 : 7.0;

    /// <summary>A concrete column's least dimension for its hours, inches, ACI 216.1 as IBC Table 722.2.4 has it, as remembered, for siliceous aggregate: 8 in for 1 h, 9, 10, 12 and 14 for 1.5 to 4.</summary>
    public static double AciColumn(double hours) => hours <= 1 ? 8 : hours <= 1.5 ? 9 : hours <= 2 ? 10 : hours <= 3 ? 12 : 14;

    private static FireCheck ConcreteUs(string name, string kind, double thickness, int minutes)
    {
        var hours = minutes / 60.0;
        var column = kind == "Column";
        var least = (column ? AciColumn(hours) : AciThickness(hours)) * 25.4;
        return new FireCheck(name, kind, minutes, new[] { new StrengthCheck(column ? "Least dimension" : "Thickness", least, thickness, "mm", column ? "ACI 216.1, IBC Table 722.2.4" : "ACI 216.1, IBC Table 722.2.1.1", $"{hours:0.#} h, siliceous aggregate") },
            string.Empty, "Siliceous aggregate taken, which asks the most; carbonate and lightweight aggregate ask less. Cover to the bars by ACI 216.1 is for whoever details them.");
    }

    private static FireCheck ByRating(string name, string kind, int minutes, int rating, string otherwise, string clause = "Table B4") => rating == 0
        ? new FireCheck(name, kind, minutes, Array.Empty<StrengthCheck>(), $"{minutes} minutes' fire resistance", $"Not checked. {otherwise}")
        : new FireCheck(name, kind, minutes, new[] { Rated(minutes, rating, clause) }, rating >= minutes ? string.Empty : $"{minutes} minutes' fire resistance, its type giving {rating}", "As its type's Fire Rating says, to be tested as it is built.");

    private static StrengthCheck Rated(int minutes, int rating, string clause = "Table B4") => new("Fire Rating", minutes, rating, "min", clause, "by its type's Fire Rating");

    private static FireCheck FloorByRating(BimDocument document, Floor floor, int minutes) =>
        ByRating($"{document.FindType<SlabType>(floor.TypeId)?.Name ?? "Floor"} on {document.FindLevel(floor.LevelId)?.Name}", "Floor", minutes, Doors.FireMinutes(document.FindType<SlabType>(floor.TypeId)?.FireRating ?? string.Empty),
            "Give its type a Fire Rating from its lining maker's tested data, or make it concrete.");

    private static FireCheck ConcreteFloor(AnalyticalModel model, AnalyticalPanel panel, int minutes)
    {
        // Flat if a column comes up to it with no beam to stand between them.
        var nodes = panel.Outline.Concat(panel.Inside).ToHashSet();
        var flat = model.Members.Any(member => member.Kind == AnalyticalMemberKind.Column && member.Nodes.Any(nodes.Contains)
            && !model.Members.Any(beam => beam.Kind == AnalyticalMemberKind.Beam && beam.Nodes.Any(node => member.Nodes.Contains(node))));
        var (thickness, axis) = ConcreteFire.Slab(minutes, flat);
        return new FireCheck(panel.Name, "Floor", minutes, new[] { new StrengthCheck("Thickness", thickness, panel.Thickness, "mm", flat ? "Table 5.9" : "Table 5.8", flat ? "a flat slab" : "spanning one way") },
            $"its bars {axis:0} mm to their centres from the soffit", "A floor finish of non-combustible material may count towards the thickness, 5.7.1, and is not counted here. A two-way slab held on four edges may want less axis distance.");
    }

    /// <summary>
    /// A wall of studs with no Fire Rating, its cavity full of insulation that stays up behind plasterboard, by Annex C of BS EN 1995-1-2, from its inner face: its stud as a column of what is left, at 20th percentile strength.
    /// Null where the annex does not reach: an empty cavity, or glass fibre past the time the board falls off, or past 60 minutes.
    /// </summary>
    private static FireCheck? Stud(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel, WallType type, int minutes)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        if (us || TimberWallDesign.FrameOf(document, panel, us) is not { } frame) return null;
        var fill = type.Structure.Layers.Where(layer => layer.Function == BIMDesigner.Core.Materials.LayerFunction.Structure).Select(layer => document.FindMaterial(layer.MaterialId)?.Name ?? string.Empty).FirstOrDefault() ?? string.Empty;
        var glass = fill.Contains("glass", StringComparison.OrdinalIgnoreCase);
        if (!glass && !fill.Contains("wool", StringComparison.OrdinalIgnoreCase) && !fill.Contains("rock", StringComparison.OrdinalIgnoreCase)) return null;
        var lining = TimberFire.Counted(BoardsAfter(document, type.Structure));
        if (lining <= 0 || (glass && minutes > TimberFire.StartOfCharring(lining))) return null;
        var (width, depth) = (frame.Section.Thickness, frame.Section.Depth);
        if (TimberFire.Stud(minutes, lining, depth, width) is not { } left) return null;

        var analysis = second.Analysis;
        var height = panel.FlatOutline.Max(p => p.Y) - panel.FlatOutline.Min(p => p.Y);
        var length = (panel.FlatOutline.Max(p => p.X) - panel.FlatOutline.Min(p => p.X)) / 1000;
        var fire = Combination(document, analysis.Loading);
        var line = analysis.Model.Supports.Where(support => ReferenceEquals(support.Panel, panel)).Sum(support => analysis.ReactionOf(support, fire).Fz) / Math.Max(length, 1e-3);
        var load = Math.Max(0, line) * frame.Spacing / 1000;
        var remaining = depth - left.Charred;
        var grade = frame.Section.Name.Contains("C16", StringComparison.Ordinal) ? TimberDesign.C16 : TimberDesign.C24;
        var strength = left.Strength * 1.25 * grade.Fc0;
        var stiffness = left.Stiffness * 1.25 * grade.E05;
        if (remaining <= 0 || stiffness <= 0)
            return new FireCheck(panel.Name, "Wall", minutes, new[] { new StrengthCheck("Burnt through", left.Charred, depth, "mm", "Annex C", "from its inner face") }, $"a thicker lining, or a Fire Rating for {minutes} minutes", string.Empty);

        var slender = height / (0.2887 * remaining) / Math.PI * Math.Sqrt(strength / stiffness);
        var k = 0.5 * (1 + 0.2 * (slender - 0.3) + slender * slender);
        var buckling = Math.Min(1, 1 / (k + Math.Sqrt(k * k - slender * slender)));
        var capacity = buckling * strength * width * remaining / 1000;
        return new FireCheck(panel.Name, "Wall", minutes, new[] { new StrengthCheck("In fire: stud in compression", load, capacity, "kN", "Annex C", $"{remaining:0} left of {depth:0}, {left.Strength:0.00} of its strength, kc {buckling:0.00}") },
            load <= capacity ? string.Empty : $"a thicker lining, or a Fire Rating for {minutes} minutes",
            $"Behind {lining:0.#} mm of plasterboard of type A or H counted, its cavity full of rock fibre taken to stay up, a fire from its inner face. Annex C is informative.");
    }

    private static FireCheck WallOf(BimDocument document, SecondOrderAnalysis second, AnalyticalPanel panel, Wall wall, int minutes)
    {
        var type = document.GetWallType(wall);
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return panel.Material.Kind == AnalyticalMaterialKind.Concrete
                ? ConcreteUs(panel.Name, "Wall", panel.Thickness, minutes)
                : ByRating(panel.Name, "Wall", minutes, Doors.FireMinutes(type?.FireRating ?? string.Empty), "Give its type a Fire Rating from a listed assembly, IBC 703.3: masonry by ACI 216.1's equivalent thickness and stud walls by tested assemblies are not worked out here.", "IBC Table 601");
        if (panel.Material.Kind == AnalyticalMaterialKind.Timber && type is not null && Doors.FireMinutes(type.FireRating) == 0 && Stud(document, second, panel, type, minutes) is { } stud) return stud;

        if (panel.Material.Kind != AnalyticalMaterialKind.Concrete)
            return ByRating(panel.Name, "Wall", minutes, Doors.FireMinutes(type?.FireRating ?? string.Empty), panel.Material.Kind == AnalyticalMaterialKind.Masonry
                ? "The tables of BS EN 1996-1-2 have not been read here: give its type a Fire Rating from its block maker's data."
                : "Give its type a Fire Rating from its lining maker's tested data.");

        // Table 5.4's load ratio is what it carries in fire over what it carries cold; the worst it carries at the ultimate, at 0.7 of it as the standard's own 2.4.2 takes on the safe side.
        var height = panel.FlatOutline.Max(p => p.Y) - panel.FlatOutline.Min(p => p.Y);
        var fck = ConcreteDesign.StrengthOf(panel.Material.Quality);
        var worst = second.WallStretches(panel, height).SelectMany(entry => entry.Bands).Select(band => Math.Max(0, -band.Vertical)).DefaultIfEmpty(0).Max();
        var loaded = 0.7 * worst / (panel.Thickness * fck / 1.5) > 0.35;
        var both = type is not { Function: WallFunction.Exterior };
        var (thickness, axis) = ConcreteFire.Wall(minutes, loaded, both);
        return new FireCheck(panel.Name, "Wall", minutes, new[] { new StrengthCheck("Thickness", thickness, panel.Thickness, "mm", "Table 5.4", $"{(both ? "two sides" : "one side")} exposed, load ratio {(loaded ? "0.7" : "0.35")}") },
            $"its bars {axis:0} mm to their centres from the face", string.Empty);
    }

    private static FireCheck Member(BimDocument document, StructuralAnalysis analysis, LoadCombination fire, MemberCheck check, int minutes, IReadOnlyList<Floor> floors, Point2D middle, double level)
    {
        var member = check.Member;
        var kind = member.Kind.ToString();
        var rating = member.Element switch
        {
            Column column => Doors.FireMinutes(document.FindType<ColumnType>(column.TypeId)?.FireRating ?? string.Empty),
            Beam beam => Doors.FireMinutes(document.FindType<BeamType>(beam.TypeId)?.FireRating ?? string.Empty),
            _ => 0
        };

        // A joist stands inside its floor, whose rating is the floor's and all that is in it.
        var lining = 0.0;
        if (member.Kind == AnalyticalMemberKind.Joist)
            foreach (var floor in floors.Where(floor => Math.Abs(floor.GetTopElevation(document) - level) <= 600 && Polygon2D.Contains(floor.Boundary, middle)))
            {
                var type = document.FindType<SlabType>(floor.TypeId);
                rating = Math.Max(rating, Doors.FireMinutes(type?.FireRating ?? string.Empty));
                if (type is not null) lining = Math.Max(lining, TimberFire.Counted(BoardsAfter(document, type.Structure)));
            }

        if (rating >= minutes && member.Section.Material.Kind != AnalyticalMaterialKind.Concrete)
            return new FireCheck(member.Name, kind, minutes, new[] { Rated(minutes, rating) }, string.Empty, member.Kind == AnalyticalMemberKind.Joist ? "As its floor's Fire Rating says, tested with the joists it has." : "As its type's Fire Rating says, the protection on it to be what was tested.");

        var result = analysis.MemberOf(member, fire);
        var forces = new MemberForces(result.Axial, result.ShearY, result.ShearZ, result.Torsion, result.MomentY, result.MomentZ, false);
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return member.Section.Material.Kind switch
            {
                AnalyticalMaterialKind.Timber when member.Section.Dimensions.Count >= 2 => TimberUs(check, forces, minutes),
                AnalyticalMaterialKind.Concrete when member.Section.Dimensions.Count >= 2 && member.Kind == AnalyticalMemberKind.Column => ConcreteUs(member.Name, "Column", Math.Min(member.Section.Dimensions[0], member.Section.Dimensions[1]), minutes),
                _ => ByRating(member.Name, kind, minutes, rating, member.Section.Material.Kind == AnalyticalMaterialKind.Steel
                    ? "Give its type a Fire Rating from a listed assembly or its protection's maker, IBC 703.3: AISC 360 Appendix 4 is not worked out here."
                    : "A concrete beam's cover by ACI 216.1 is for whoever details it; give its type a Fire Rating from a listed assembly.", "IBC Table 601")
            };

        return member.Section.Material.Kind switch
        {
            AnalyticalMaterialKind.Steel when SteelSection.Of(member.Section) is { } section => Steel(document, analysis.Model, check, section, forces, minutes),
            AnalyticalMaterialKind.Timber when member.Section.Dimensions.Count >= 2 => Timber(check, forces, minutes, lining),
            AnalyticalMaterialKind.Concrete when member.Section.Dimensions.Count >= 2 => Concrete(check, forces, minutes),
            _ => new FireCheck(member.Name, kind, minutes, Array.Empty<StrengthCheck>(), $"{minutes} minutes' fire resistance", "Not checked: a member of this kind is not worked out in fire.")
        };
    }

    /// <summary>
    /// A steel column's buckling length in fire, BS EN 1993-1-2 4.2.3.2(5) and its Figure 4.1: in a braced frame whose storeys are each a fire compartment, 0.5 L where it runs on both above and
    /// below, 0.7 L where it runs on at one end only, the cooler column beyond holding it; otherwise as cold. Braced: walls stand in the building both ways. Compartments: flats and offices, whose
    /// floors Approved Document B makes compartment floors.
    /// </summary>
    public static (MemberLengths Lengths, double Factor) LengthsInFire(BimDocument document, AnalyticalModel model, AnalyticalMember member, MemberLengths cold)
    {
        if (member.Kind != AnalyticalMemberKind.Column || UseOf(document) is not (BuildingUse.Flats or BuildingUse.Office)) return (cold, 1);
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();
        var braced = walls.Any(wall => Math.Abs(wall.U.X) > 0.7) && walls.Any(wall => Math.Abs(wall.U.Y) > 0.7);
        if (!braced) return (cold, 1);
        AnalyticalNode Bottom(AnalyticalMember column) => column.Start.At.Z <= column.End.At.Z ? column.Start : column.End;
        AnalyticalNode Top(AnalyticalMember column) => column.Start.At.Z <= column.End.At.Z ? column.End : column.Start;
        var columns = model.Members.Where(each => each.Kind == AnalyticalMemberKind.Column && !ReferenceEquals(each, member)).ToList();
        var above = columns.Any(each => ReferenceEquals(Bottom(each), Top(member)));
        var below = columns.Any(each => ReferenceEquals(Top(each), Bottom(member)));
        var factor = above && below ? 0.5 : above || below ? 0.7 : 1;
        if (factor >= 1) return (cold, 1);
        var length = member.Length;
        return (cold with { Strong = Math.Min(cold.Strong, factor * length), Weak = Math.Min(cold.Weak, factor * length) }, factor);
    }

    private static FireCheck Steel(BimDocument document, AnalyticalModel model, MemberCheck check, SteelSection section, MemberForces forces, int minutes)
    {
        var member = check.Member;
        var column = member.Kind == AnalyticalMemberKind.Column;
        var onConcrete = !column && member.Nodes.Any(node => model.Panels.Any(panel => panel.Kind == AnalyticalPanelKind.Floor && panel.Material.Kind == AnalyticalMaterialKind.Concrete && panel.Outline.Concat(panel.Inside).Contains(node)));
        var (profile, box, shadow) = SteelFire.Factors(section, onConcrete);
        var hot = SteelFire.Temperature(shadow * profile, minutes);
        var (lengths, factor) = LengthsInFire(document, model, member, check.Lengths);
        var critical = SteelFire.CriticalOf(section, lengths, forces, onConcrete ? 0.7 : 1);
        return new FireCheck(member.Name, member.Kind.ToString(), minutes,
            new[] { new StrengthCheck($"Bare, at {minutes} minutes", hot, critical, "°C", "4.2.3, 4.2.5.1", $"Am/V {profile:0} /m, {forces.N:0.0} kN and {Math.Abs(forces.My):0.0} kNm in fire") },
            hot <= critical ? string.Empty : $"protection to hold it under {critical:0} °C for {minutes} minutes: Ap/V {box:0} /m boxed, {profile:0} /m following its shape",
            $"Bare, by the standard fire, {(onConcrete ? "three sides exposed under a concrete floor" : "all round")}. A protected member takes a Fire Rating on its type. " +
            (factor < 1 ? $"Its buckling length in fire {factor:0.0} L, its storey a compartment of its own in a braced frame, the column beyond it cooler (4.2.3.2(5))." : "Its lengths as cold."));
    }

    /// <summary>The plasterboard after a build-up's structure, on a floor's underside or a wall's inner face, mm each, in the order they are in it.</summary>
    public static IReadOnlyList<double> BoardsAfter(BimDocument document, BIMDesigner.Core.Materials.CompoundStructure structure)
    {
        var layers = structure.Layers.ToList();
        var last = layers.FindLastIndex(layer => layer.Function == BIMDesigner.Core.Materials.LayerFunction.Structure);
        return layers.Skip(last + 1).Where(layer => document.FindMaterial(layer.MaterialId)?.Name is { } name && (name.Contains("plasterboard", StringComparison.OrdinalIgnoreCase) || name.Contains("gypsum", StringComparison.OrdinalIgnoreCase))).Select(layer => layer.Thickness).ToList();
    }

    private static FireCheck Timber(MemberCheck check, MemberForces forces, int minutes, double lining = 0)
    {
        var member = check.Member;
        var post = member.Kind == AnalyticalMemberKind.Column;
        var (depth, width) = (member.Section.Dimensions[0], member.Section.Dimensions[1]);
        var glulam = member.Section.Material.Quality.StartsWith("GL", StringComparison.Ordinal);
        var (leftDepth, leftWidth) = TimberFire.Left(depth, width, minutes, glulam, post, lining);
        var said = $"{leftWidth:0} by {leftDepth:0} left of {width:0} by {depth:0}";
        var wants = lining > 0 ? $"a thicker lining, or a Fire Rating for {minutes} minutes" : $"a lining or a Fire Rating for {minutes} minutes";
        var limits = lining > 0
            ? $"Behind {lining:0.#} mm of plasterboard of type A or H counted, which falls away at {TimberFire.StartOfCharring(lining):0} minutes, then the reduced section of 4.2.2 with the charring of 3.4.3. Type F's failure time wants its maker's tests, and a tested floor takes a Fire Rating on its type."
            : "Bare, the reduced section of 4.2.2, three sides burning or all round a post. A lining delays this, and a tested floor or wall takes a Fire Rating on its type.";
        if (leftDepth <= 0 || leftWidth <= 0)
            return new FireCheck(member.Name, member.Kind.ToString(), minutes, new[] { new StrengthCheck("Burnt through", 2 * TimberFire.Behind(minutes, lining, glulam), width, "mm", "4.2.2", $"two sides burning {TimberFire.Behind(minutes, lining, glulam):0} mm each") }, wants, limits);

        var grade = TimberFire.Hot(glulam ? TimberDesign.Gl24h : TimberDesign.C24);
        var checks = TimberDesign.Eurocode(grade, leftDepth, leftWidth, member.Kind == AnalyticalMemberKind.Joist, check.Lengths, forces, 1)
            .Select(each => each with { Name = $"In fire: {each.Name.ToLowerInvariant()}", Note = said }).ToList();
        return new FireCheck(member.Name, member.Kind.ToString(), minutes, checks, checks.All(each => each.Ratio <= 1) ? string.Empty : wants, limits);
    }

    /// <summary>NDS Chapter 16's effective char depth, mm, after so many minutes: aeff = 1.2 βn t^0.813 with βn 1.5 in/h, (16.2-1) and Table 16.2.1A as remembered.</summary>
    public static double NdsChar(double minutes) => 1.2 * 1.5 * Math.Pow(minutes / 60, 0.813) * 25.4;

    /// <summary>
    /// A timber member in a US fire by NDS Chapter 16: the section left once each exposed face has charred aeff - three faces for a beam or joist under its floor, four for a post - its
    /// allowable stress design capacity raised to the average ultimate by Table 16.2.2's factors, 2.85 in bending, 2.58 in compression, 2.03 for buckling, against D + L. No. 2 sawn lumber or
    /// 24F glulam as the NDS checks take them, the size factor of the whole section.
    /// </summary>
    private static FireCheck TimberUs(MemberCheck check, MemberForces forces, int minutes)
    {
        const double psi = 0.00689476;
        var member = check.Member;
        var post = member.Kind == AnalyticalMemberKind.Column;
        var (depth, width) = (member.Section.Dimensions[0], member.Section.Dimensions[1]);
        var glulam = member.Section.Material.Quality.StartsWith("GL", StringComparison.Ordinal);
        var charred = NdsChar(minutes);
        var (leftDepth, leftWidth) = post ? (depth - 2 * charred, width - 2 * charred) : (depth - charred, width - 2 * charred);
        var said = $"{leftWidth:0} by {leftDepth:0} left of {width:0} by {depth:0}, aeff {charred:0} mm";
        if (leftDepth <= 0 || leftWidth <= 0)
            return new FireCheck(member.Name, member.Kind.ToString(), minutes, new[] { new StrengthCheck("Burnt through", 2 * charred, width, "mm", "NDS 16.2", said) }, $"a larger section, or a Fire Rating for {minutes} minutes", string.Empty);

        var (cb, _, cc) = glulam ? (1.0, 1.0, 1.0) : TimberDesign.SizeFactors(depth, width);
        var (fb, fc, emin) = glulam ? (2400.0, 1600.0, 950000.0) : (900.0, 1350.0, 580000.0);
        var repetitive = member.Kind == AnalyticalMemberKind.Joist && !glulam ? 1.15 : 1.0;
        var area = leftDepth * leftWidth / (25.4 * 25.4);
        var modulus = leftWidth * leftDepth * leftDepth / 6 / (25.4 * 25.4 * 25.4);
        var bending = Math.Abs(forces.My) * 1e6 / psi / (25.4 * 25.4 * 25.4) / modulus;
        var fbFire = 2.85 * fb * cb * repetitive;
        var checks = new List<StrengthCheck> { new("In fire: bending", bending, fbFire, "psi", "NDS 16.2.2", $"{said}, 2.85 Fb") };
        var compression = Math.Max(0, -forces.N) * 1e3 / psi / (leftDepth * leftWidth);
        if (compression > 0)
        {
            var slender = Math.Max(check.Lengths.Strong / leftDepth, check.Lengths.Weak / leftWidth);
            var euler = 0.822 * 2.03 * emin / (slender * slender);
            var fcStar = 2.58 * fc * cc;
            var cp = TimberDesign.Stability(euler / fcStar, glulam ? 0.9 : 0.8);
            checks.Add(new StrengthCheck("In fire: compression", compression, cp * fcStar, "psi", "NDS 16.2.2, 3.7", $"{said}, 2.58 Fc, 2.03 for buckling, Cp {cp:0.00}"));
            if (bending > 0)
                checks.Add(new StrengthCheck("In fire: compression with bending", Math.Pow(compression / (cp * fcStar), 2) + bending / (fbFire * Math.Max(1e-6, 1 - compression / euler)), 1, "", "NDS 3.9.2, 16.2.2", said));
        }

        return new FireCheck(member.Name, member.Kind.ToString(), minutes, checks, checks.All(each => each.Ratio <= 1) ? string.Empty : $"a larger section, or a lining or Fire Rating for {minutes} minutes",
            $"Bare, NDS Chapter 16 under D + L, {(post ? "four faces" : "three faces, its top under the floor")} charring. A lining delays this and a listed assembly takes a Fire Rating on its type.");
    }

    private static FireCheck Concrete(MemberCheck check, MemberForces forces, int minutes)
    {
        var member = check.Member;
        var (depth, width) = (member.Section.Dimensions[0], member.Section.Dimensions[1]);
        if (member.Kind == AnalyticalMemberKind.Column)
        {
            var share = Math.Abs(forces.N) / ConcreteFire.Squash(depth * width, ConcreteDesign.StrengthOf(member.Section.Material.Quality));
            var least = Math.Min(depth, width);
            if (ConcreteFire.Column(minutes, share, least) is not { } column)
                return new FireCheck(member.Name, "Column", minutes, new[] { new StrengthCheck("Load in fire", share, 0.7, "", "Table 5.2a", "of what it carries cold") }, "a larger section", string.Empty);
            return new FireCheck(member.Name, "Column", minutes, new[] { new StrengthCheck("Width", column.Width, least, "mm", "Table 5.2a", $"load in fire {share:0.00} of what it carries cold") }, $"its bars {column.Axis:0} mm to their centres from the face", string.Empty);
        }

        var (needs, axis) = ConcreteFire.Beam(minutes, width);
        return new FireCheck(member.Name, member.Kind.ToString(), minutes, new[] { new StrengthCheck("Width", needs, width, "mm", "Table 5.5", "as simply supported") }, $"its bars {axis:0} mm to their centres from the soffit and sides", string.Empty);
    }
}

/// <summary>A building's type of construction by the International Building Code, Chapter 6: what Table 601 rates its elements by. None set, none is worked out.</summary>
public enum ConstructionType
{
    NotSet,
    IA,
    IB,
    IIA,
    IIB,
    IIIA,
    IIIB,
    IV,
    VA,
    VB
}
