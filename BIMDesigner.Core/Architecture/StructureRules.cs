using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a US house's walls are built, as the IRC's footing tables tell them apart.</summary>
public enum IrcWallConstruction
{
    /// <summary>Light frame: timber or steel studs (Table R403.1(1)).</summary>
    LightFrame,

    /// <summary>Light frame faced with brick veneer, or lath and plaster (Table R403.1(2)).</summary>
    BrickVeneer,

    /// <summary>Cast-in-place concrete or masonry (Table R403.1(3)).</summary>
    Masonry
}

/// <summary>What a US house stands on, as the IRC's footing tables tell them apart.</summary>
public enum IrcFoundation
{
    SlabOnGrade,
    CrawlSpace,
    Basement
}

/// <summary>
/// What the structure answers to. In England, Approved Document A's foundations of plain concrete
/// (Section 2E): strip footings as wide as Table 10 asks for the ground and the load on them,
/// 150 thick at the least and no thinner than they project, central under their walls, and 0.45 m
/// down out of the frost - 0.75, 0.9 or 1.0 m in a shrinkable clay (2E4); and Approved Document B's
/// 30 minutes for a column holding up a floor (5.1, Table B2). In the US, the IRC: footings as
/// wide and thick as Tables R403.1(1) to (3) ask for the walls, storeys, snow, foundation and soil
/// (R403.1.1), 12 by 6 in at the least, projecting 2 in to their thickness, 12 in below the ground
/// and below the frost line (R403.1.4); a column's footing sized by its load and the soil
/// (R403.1.1, Table R401.4.1); and its columns - a 4 by 4 at the least in wood, 3 in Schedule 40
/// pipe in steel (R407.3), steel painted against rust (R407.2), wood protected from decay (R317.1).
/// </summary>
public static class StructureRules
{
    // ---- the ground ------------------------------------------------------------------

    /// <summary>The IRC's presumptive load-bearing value of the ground, psf (Table R401.4.1); null where it takes a soils investigation.</summary>
    public static double? Bearing(GroundType ground) => ground switch
    {
        GroundType.Rock => 4000,
        GroundType.Gravel => 3000,
        GroundType.Sand or GroundType.LooseSand => 2000,
        GroundType.StiffClay or GroundType.FirmClay => 1500,
        _ => null
    };

    /// <summary>Approved Document A's type of ground (Table 10).</summary>
    public static string AdaType(GroundType ground) => ground switch
    {
        GroundType.Rock => "I",
        GroundType.Gravel or GroundType.Sand => "II",
        GroundType.StiffClay => "III",
        GroundType.FirmClay => "IV",
        GroundType.LooseSand => "V",
        GroundType.SoftClay => "VI",
        _ => "VII"
    };

    /// <summary>The ground as it reads in a sentence.</summary>
    public static string Describe(GroundType ground) => ground switch
    {
        GroundType.Rock => "rock",
        GroundType.Gravel => "medium dense gravel",
        GroundType.Sand => "medium dense sand",
        GroundType.StiffClay => "stiff clay",
        GroundType.FirmClay => "firm clay",
        GroundType.LooseSand => "loose sand",
        GroundType.SoftClay => "soft clay or silt",
        _ => "very soft clay or silt"
    };

    /// <summary>Table 10's loads, kN a metre of load-bearing walling, and the widths it asks for each type of ground, mm.</summary>
    private static readonly double[] Table10Loads = { 20, 30, 40, 50, 60, 70 };

    private static double[]? Table10Widths(GroundType ground) => ground switch
    {
        GroundType.Gravel or GroundType.Sand or GroundType.StiffClay => new double[] { 250, 300, 400, 500, 600, 650 },
        GroundType.FirmClay => new double[] { 300, 350, 450, 600, 750, 850 },
        GroundType.LooseSand => new double[] { 400, 600 },
        GroundType.SoftClay => new double[] { 450, 650 },
        _ => null
    };

    /// <summary>
    /// The width Table 10 asks of a strip footing on a ground carrying a load, mm - the wall's own
    /// thickness on rock - or why it gives none: past 70 kN/m, past 30 on loose or soft ground, or
    /// very soft ground that wants specialist advice.
    /// </summary>
    public static (double? Width, string? Outside) Table10(GroundType ground, double load, double wallThickness)
    {
        if (ground == GroundType.Rock) return (wallThickness, null);
        if (Table10Widths(ground) is not { } widths) return (null, "on very soft ground (type VII) Table 10 refers the footing to specialist advice");
        var column = Array.FindIndex(Table10Loads, limit => load <= limit + 1e-6);
        if (column < 0) return (null, $"Table 10 goes no further than 70 kN/m - a footing carrying more is an engineer's design");
        if (column >= widths.Length) return (null, $"on {Describe(ground)} (type {AdaType(ground)}) Section 2E does not apply once the load is over 30 kN/m");
        return (widths[column], null);
    }

    /// <summary>The bearing pressure Table 10 works strip footings to on a ground, kN/m²: the most load it takes over the width it asks for it. None on rock, nor on very soft ground.</summary>
    public static double? Table10Pressure(GroundType ground) => ground switch
    {
        GroundType.Gravel or GroundType.Sand or GroundType.StiffClay => 70 / 0.65,
        GroundType.FirmClay => 70 / 0.85,
        GroundType.LooseSand => 30 / 0.6,
        GroundType.SoftClay => 30 / 0.65,
        _ => null
    };

    /// <summary>How far down Approved Document A takes a strip footing's underside, mm (2E4): 0.45 m out of the frost; 0.75, 0.9 or 1.0 m in a clay that shrinks little, more or much; nothing on rock.</summary>
    public static double AdaDepth(GroundType ground, ClayShrinkage shrinkage) => ground == GroundType.Rock ? 0 : shrinkage switch
    {
        ClayShrinkage.Low => 750,
        ClayShrinkage.Medium => 900,
        ClayShrinkage.High => 1000,
        _ => 450
    };

    // ---- the IRC's footing tables (2021) --------------------------------------------------

    /// <summary>The soil columns of Tables R403.1(1) to (3), psf.</summary>
    private static readonly double[] Soils = { 1500, 2000, 2500, 3000, 3500, 4000 };

    /// <summary>
    /// Width by thickness, in, for each snow load - 20 psf roof live load or 25 psf ground snow,
    /// 30, 50, 70 - and storey and foundation in turn (1 story slab, crawl space, basement; 2
    /// story; 3 story), at 1,500 to 4,000 psf.
    /// </summary>
    private static readonly string[] LightFrame =
    {
        "12x6 12x6 12x6 12x6 12x6 12x6",
        "12x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "15x6 12x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "18x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "12x6 12x6 12x6 12x6 12x6 12x6",
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "16x6 14x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "12x6 12x6 12x6 12x6 12x6 12x6",
        "14x6 12x6 12x6 12x6 12x6 12x6",
        "18x6 13x6 12x6 12x6 12x6 12x6",
        "15x6 13x6 12x6 12x6 12x6 12x6",
        "17x6 13x6 12x6 12x6 12x6 12x6",
        "21x7 15x6 12x6 12x6 12x6 12x6",
        "18x6 13x6 12x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "24x8 18x6 14x6 12x6 12x6 12x6",
        "14x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "17x6 12x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 17x6 13x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "24x8 19x6 15x6 13x6 12x6 12x6",
    };
    private static readonly string[] BrickVeneer =
    {
        "12x6 12x6 12x6 12x6 12x6 12x6",
        "15x6 12x6 12x6 12x6 12x6 12x6",
        "18x6 14x6 12x6 12x6 12x6 12x6",
        "18x6 13x6 12x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "23x8 17x6 14x6 12x6 12x6 12x6",
        "23x8 17x6 14x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 13x6 12x6 12x6",
        "29x11 21x7 17x6 14x6 12x6 12x6",
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "15x6 12x6 12x6 12x6 12x6 12x6",
        "18x6 14x6 12x6 12x6 12x6 12x6",
        "18x6 14x6 12x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "24x8 18x6 14x6 12x6 12x6 12x6",
        "23x8 18x6 14x6 12x6 12x6 12x6",
        "26x9 19x6 15x6 13x6 12x6 12x6",
        "29x11 22x7 17x6 14x6 12x6 12x6",
        "14x6 12x6 12x6 12x6 12x6 12x6",
        "17x6 13x6 12x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "20x6 15x6 12x6 12x6 12x6 12x6",
        "22x7 17x6 13x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 13x6 12x6 12x6",
        "25x9 19x6 15x6 13x6 12x6 12x6",
        "27x10 21x7 16x6 14x6 12x6 12x6",
        "31x12 23x8 18x6 15x6 13x6 12x6",
        "14x6 12x6 12x6 12x6 12x6 12x6",
        "17x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "21x7 16x6 13x6 12x6 12x6 12x6",
        "24x8 18x6 14x6 12x6 12x6 12x6",
        "27x10 20x6 16x6 13x6 12x6 12x6",
        "27x10 20x6 16x6 13x6 12x6 12x6",
        "29x11 22x7 17x6 15x6 12x6 12x6",
        "32x12 24x8 19x6 16x6 14x6 12x6",
    };
    private static readonly string[] Masonry =
    {
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 13x6 12x6 12x6",
        "28x10 21x7 17x6 14x6 12x6 12x6",
        "31x12 23x8 18x6 15x6 13x6 12x6",
        "13x6 12x6 12x6 12x6 12x6 12x6",
        "16x6 12x6 12x6 12x6 12x6 12x6",
        "19x7 14x6 12x6 12x6 12x6 12x6",
        "19x6 15x6 12x6 12x6 12x6 12x6",
        "22x7 16x6 13x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 13x6 12x6 12x6",
        "26x9 19x6 15x6 13x6 12x6 12x6",
        "28x10 21x7 17x6 14x6 12x6 12x6",
        "31x12 23x8 19x6 16x6 13x6 12x6",
        "15x6 12x6 12x6 12x6 12x6 12x6",
        "18x6 13x6 12x6 12x6 12x6 12x6",
        "21x7 16x6 12x6 12x6 12x6 12x6",
        "21x7 16x6 13x6 12x6 12x6 12x6",
        "24x8 18x6 14x6 12x6 12x6 12x6",
        "27x10 20x6 16x6 13x6 12x6 12x6",
        "27x10 20x6 16x6 14x6 12x6 12x6",
        "30x11 22x7 18x6 15x6 13x6 12x6",
        "33x13 25x9 20x6 16x6 14x6 12x6",
        "17x6 13x6 12x6 12x6 12x6 12x6",
        "19x6 14x6 12x6 12x6 12x6 12x6",
        "22x7 17x6 13x6 12x6 12x6 12x6",
        "23x8 17x6 14x6 12x6 12x6 12x6",
        "25x9 19x6 15x6 12x6 12x6 12x6",
        "28x10 21x7 17x6 14x6 12x6 12x6",
        "29x11 22x7 17x6 14x6 12x6 12x6",
        "31x12 23x8 19x6 16x6 13x6 12x6",
        "34x13 26x9 21x7 17x6 15x6 13x6",
    };

    private static string[] Table(IrcWallConstruction wall) => wall switch
    {
        IrcWallConstruction.BrickVeneer => BrickVeneer,
        IrcWallConstruction.Masonry => Masonry,
        _ => LightFrame
    };

    /// <summary>The table's number, for what is said about it.</summary>
    public static string TableName(IrcWallConstruction wall) => wall switch
    {
        IrcWallConstruction.BrickVeneer => "Table R403.1(2)",
        IrcWallConstruction.Masonry => "Table R403.1(3)",
        _ => "Table R403.1(1)"
    };

    /// <summary>
    /// The footing a US house asks under a wall, in: width and thickness from the table for its
    /// walls, by its snow load, storeys, foundation and soil - the width interpolated between the
    /// soils as note a allows - or null beyond the tables: over 70 psf of snow, over three storeys,
    /// on soil softer than 1,500 psf.
    /// </summary>
    public static (double Width, double Thickness)? IrcFooting(IrcWallConstruction wall, double snowPsf, int stories, IrcFoundation foundation, double soilPsf)
    {
        var snow = snowPsf <= 25 ? 0 : snowPsf <= 30 ? 1 : snowPsf <= 50 ? 2 : snowPsf <= 70 ? 3 : -1;
        if (snow < 0 || stories is < 1 or > 3 || soilPsf < Soils[0]) return null;

        var row = Table(wall)[snow * 9 + (stories - 1) * 3 + (int)foundation]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(cell => cell.Split('x'))
            .Select(sizes => (Width: double.Parse(sizes[0]), Thickness: double.Parse(sizes[1])))
            .ToArray();
        if (soilPsf >= Soils[^1]) return row[^1];

        var at = Array.FindLastIndex(Soils, soil => soil <= soilPsf);
        var share = (soilPsf - Soils[at]) / (Soils[at + 1] - Soils[at]);
        return (row[at].Width + (row[at + 1].Width - row[at].Width) * share, Math.Max(row[at].Thickness, row[at + 1].Thickness));
    }

    /// <summary>
    /// The tables' footing made to suit the house's width square to the wall (notes c and d): 2 in
    /// wider and 1 in thicker for every 4 ft over 32 ft, and as much narrower and thinner for every
    /// whole 4 ft under it, down to 12 by 6 in.
    /// </summary>
    public static (double Width, double Thickness) ForWidth((double Width, double Thickness) footing, double buildingWidthFeet)
    {
        var over = buildingWidthFeet - 32;
        var steps = over > 0 ? over / 4 : -Math.Floor(-over / 4);
        return (Math.Max(12, footing.Width + 2 * steps), Math.Max(6, footing.Thickness + steps));
    }

    /// <summary>How a wall is built, as the tables tell it: concrete or masonry by its structure's weight; brick-faced; or a light frame.</summary>
    public static IrcWallConstruction ConstructionOf(BimDocument document, Wall wall)
    {
        if (document.GetWallType(wall) is not { } type) return IrcWallConstruction.LightFrame;
        var layers = type.Structure.Layers;
        if (layers.Where(layer => layer.Function == Materials.LayerFunction.Structure).Any(layer => (document.FindMaterial(layer.MaterialId)?.Density ?? 0) >= 1400))
            return IrcWallConstruction.Masonry;
        return layers.Any(layer => document.FindMaterial(layer.MaterialId)?.Name.Contains("brick", StringComparison.OrdinalIgnoreCase) == true)
            ? IrcWallConstruction.BrickVeneer
            : IrcWallConstruction.LightFrame;
    }

    /// <summary>How many storeys stand on a wall's footing: the wall and those on top of it, a basement's not counted.</summary>
    public static int Stories(BimDocument document, Wall wall) =>
        Math.Max(1, Loads.Stack(document, wall).Count(part => part.GetBaseElevation(document) >= -300));

    /// <summary>What the house stands on at a wall: a basement where the wall is below the ground; a slab cast on the ground beside it; otherwise a crawl space.</summary>
    public static IrcFoundation FoundationOf(BimDocument document, Wall wall)
    {
        var bottom = wall.GetBaseElevation(document);
        if (bottom < -1000) return IrcFoundation.Basement;
        var middle = wall.Start.MidpointTo(wall.End);
        var across = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var slab = document.All<Floor>().Any(floor => Math.Abs(floor.GetTopElevation(document) - bottom) < 600 &&
            (floor.Contains(middle + across * 400) || floor.Contains(middle - across * 400)) &&
            document.FindType<SlabType>(floor.TypeId) is { } type &&
            type.Structure.Layers.Any(layer => layer.Function == Materials.LayerFunction.Structure && (document.FindMaterial(layer.MaterialId)?.Density ?? 0) >= 1800));
        return slab ? IrcFoundation.SlabOnGrade : IrcFoundation.CrawlSpace;
    }

    /// <summary>How wide the house is square to a wall, mm: across its outside walls on that storey.</summary>
    public static double BuildingWidth(BimDocument document, Wall wall)
    {
        var across = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        var outside = document.Walls.Where(other => other.LevelId == wall.LevelId && document.GetWallType(other) is { Function: WallFunction.Exterior }).ToList();
        if (outside.Count == 0) outside = new List<Wall> { wall };
        var reach = outside.SelectMany(other => new[] { other.Start, other.End }).Select(point => (point - wall.Start).Dot(across)).ToList();
        return reach.Max() - reach.Min();
    }

    private static string Inches(double millimetres) => $"{millimetres / 25.4:0.#} in";

    // ---- what falls short ----------------------------------------------------------------

    /// <summary>What a footing or a structural column falls short of, as the project's standard asks: said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Element element, ClearanceStandard standard) => element switch
    {
        WallFooting strip => StripProblems(document, strip, standard),
        PadFooting pad => PadProblems(document, pad, standard),
        Column { Structural: true } column => ColumnProblems(document, column, standard),
        _ => Array.Empty<(string, string)>()
    };

    private static IReadOnlyList<(string Said, string Rule)> StripProblems(BimDocument document, WallFooting footing, ClearanceStandard standard)
    {
        var problems = new List<(string, string)>();
        if (document.Find<Wall>(footing.WallId) is not { } wall || document.FindType<FootingType>(footing.TypeId) is not { } type) return problems;
        var project = document.ProjectInformation;
        var thickness = Footings.WallThickness(document, wall);
        var projection = (type.Width - thickness) / 2;
        var depth = Footings.DepthBelowGround(document, footing) ?? footing.Depth;

        if (!standard.IsUs())
        {
            var load = Loads.OnWall(document, wall);
            var (asked, outside) = Table10(project.Ground, load, thickness);
            if (outside is not null)
                problems.Add(($"it carries {load:0.0} kN/m on {Describe(project.Ground)}.", $"Approved Document A (2E3, Table 10): {outside}."));
            else if (asked is { } width && type.Width < Math.Max(width, thickness) - 0.5)
                problems.Add(type.Width < thickness - 0.5
                    ? ($"it is {type.Width:0} wide under a wall {thickness:0} thick.", "Approved Document A (2E2a, Diagram 23) has a strip footing project past its wall each side, central under it.")
                    : ($"it is {type.Width:0} wide, carrying {load:0.0} kN/m on {Describe(project.Ground)} (type {AdaType(project.Ground)}).",
                        $"Approved Document A (2E3, Table 10) asks a strip footing there to be {width:0} wide at the least."));
            if (type.Thickness < Math.Max(150, projection) - 0.5)
                problems.Add(($"it is {type.Thickness:0} thick, standing {Math.Max(0, projection):0} out past its wall.",
                    "Approved Document A (2E2c) asks a strip footing to be 150 thick at the least, and no thinner than it projects past its wall."));
            if (Math.Abs(footing.Eccentricity) > 5)
                problems.Add(($"its middle is {Math.Abs(footing.Eccentricity):0} off its wall's.", "Approved Document A (2E2a) asks a strip footing to be central under its wall."));
            var down = AdaDepth(project.Ground, project.ClayShrinkage);
            if (depth < down - 0.5)
                problems.Add(($"its underside is {depth:0} below the ground.",
                    project.ClayShrinkage == ClayShrinkage.None
                        ? "Approved Document A (2E4) takes a strip footing's underside 0.45 m down at the least, out of the frost."
                        : $"Approved Document A (2E4) takes a strip footing's underside {Units.Metres(down, "0.00")} down at the least in a clay of {project.ClayShrinkage.ToString().ToLowerInvariant()} shrinkage."));
            return problems;
        }

        var wallKind = ConstructionOf(document, wall);
        var stories = Stories(document, wall);
        if (Bearing(project.Ground) is not { } soil)
            problems.Add(($"it stands on {Describe(project.Ground)}.", "The IRC (Table R401.4.1, note b) takes ground softer than 1,500 psf to a soils investigation, to size the footing on it."));
        else if (project.GroundSnowLoad > 70)
            problems.Add(($"the ground snow load is {project.GroundSnowLoad:0} psf.", "The IRC's footing tables go no further than 70 psf (R301.2.3): the footing is to be designed."));
        else if (stories > 3)
            problems.Add(($"{stories} storeys stand on it.", "The IRC's footing tables go no higher than three storeys: the footing is to be designed."));
        else if (IrcFooting(wallKind, project.GroundSnowLoad, stories, FoundationOf(document, wall), soil) is { } table)
        {
            var (width, thick) = ForWidth(table, BuildingWidth(document, wall) / 304.8);
            var said = $"{stories} story {EnumText(FoundationOf(document, wall))} at {soil:#,0} psf";
            if (type.Width < width * 25.4 - 1 || type.Thickness < thick * 25.4 - 1)
                problems.Add(($"it is {Inches(type.Width)} wide and {Inches(type.Thickness)} thick.",
                    $"The IRC ({TableName(wallKind)}, R403.1.1) asks {width:0.#} by {thick:0} in for {said}, the house {BuildingWidth(document, wall) / 304.8:0} ft wide."));
        }

        if (projection < 51 - 0.5 || projection > type.Thickness + 0.5)
            problems.Add(($"it projects {Inches(Math.Max(0, projection))} past its wall, {Inches(type.Thickness)} thick.",
                "The IRC (R403.1.1) asks a footing to project 2 in past its wall at the least, and no more than it is thick."));
        AddIrcDepth(problems, depth, project);
        return problems;
    }

    private static string EnumText(IrcFoundation foundation) => foundation switch
    {
        IrcFoundation.SlabOnGrade => "slab-on-grade",
        IrcFoundation.CrawlSpace => "with crawl space",
        _ => "plus basement"
    };

    private static void AddIrcDepth(List<(string, string)> problems, double depth, ProjectInformation project)
    {
        if (depth < 305 - 0.5)
            problems.Add(($"its underside is {Inches(depth)} below the ground.", "The IRC (R403.1.4) puts an exterior footing 12 in below the undisturbed ground at the least."));
        else if (project.FrostDepth > 0 && depth < project.FrostDepth - 0.5)
            problems.Add(($"its underside is {Inches(depth)} below the ground, the frost line {Inches(project.FrostDepth)}.", "The IRC (R403.1.4.1) takes a footing below the frost line, unless it is protected from frost another way (R403.3)."));
    }

    /// <summary>The load on a pad, kN: its column's, and the pad's and its pier's own weight.</summary>
    public static double PadLoad(BimDocument document, PadFooting pad)
    {
        var load = pad.ColumnId is { } id && document.FindElement(id) is Column column ? Loads.OnColumn(document, column) : 0;
        return load + Footings.Volume(document, pad) / 1e9 * 2400 * 9.81 / 1000;
    }

    private static IReadOnlyList<(string Said, string Rule)> PadProblems(BimDocument document, PadFooting pad, ClearanceStandard standard)
    {
        var problems = new List<(string, string)>();
        if (document.FindType<FootingType>(pad.TypeId) is not { } type) return problems;
        var project = document.ProjectInformation;
        var area = type.Width * type.Length / 1e6;
        var load = PadLoad(document, pad);
        var depth = Footings.DepthBelowGround(document, pad) ?? pad.Depth;
        var pier = Footings.PierOutline(document, pad) is { } outline ? (outline[0].DistanceTo(outline[1]), outline[1].DistanceTo(outline[2])) : (0.0, 0.0);
        var projection = Math.Max((type.Width - Math.Min(pier.Item1, pier.Item2)) / 2, (type.Length - Math.Max(pier.Item1, pier.Item2)) / 2);

        if (!standard.IsUs())
        {
            if (project.Ground == GroundType.VerySoft)
                problems.Add(($"it stands on {Describe(project.Ground)}.", "Approved Document A (Table 10) refers footings on very soft ground (type VII) to specialist advice."));
            else if (pad.ColumnId is not null && Table10Pressure(project.Ground) is { } allowed && load / area > allowed + 0.05)
                problems.Add(($"it carries {load:0.0} kN on {Units.OfSquareMetres(area, "0.00")} - {load / area:0} kN/m² on {Describe(project.Ground)}.",
                    $"Approved Document A gives no table for pads: it is checked at the pressure its Table 10 bears strip footings at on this ground, {allowed:0} kN/m² - a pad {Math.Sqrt(load / allowed) * 1000:0} square at the least."));
            if (type.Thickness < Math.Max(150, projection) - 0.5)
                problems.Add(($"it is {type.Thickness:0} thick, standing {Math.Max(0, projection):0} out past its pier.",
                    "Approved Document A (2E2c) asks plain concrete footings to be 150 thick at the least, and no thinner than they project."));
            var down = AdaDepth(project.Ground, project.ClayShrinkage);
            if (depth < down - 0.5)
                problems.Add(($"its underside is {depth:0} below the ground.", $"Approved Document A (2E4) takes a footing's underside {Units.Metres(down, "0.00")} down at the least - out of the frost{(project.ClayShrinkage == ClayShrinkage.None ? "" : ", and below the clay's shrinkage")}."));
            return problems;
        }

        if (Bearing(project.Ground) is not { } soil)
            problems.Add(($"it stands on {Describe(project.Ground)}.", "The IRC (Table R401.4.1, note b) takes ground softer than 1,500 psf to a soils investigation, to size the footing on it."));
        else if (pad.ColumnId is not null && load / area > soil * Loads.Psf + 0.05)
            problems.Add(($"it carries {load:0.0} kN on {Units.OfSquareMetres(area, "0.00")} - {load / area / Loads.Psf:#,0} psf on soil good for {soil:#,0}.",
                $"The IRC (R403.1.1, Table R401.4.1) sizes a column's footing by its load and the soil: {Units.OfSquareMetres(load / (soil * Loads.Psf), "0.00")} at the least."));
        if (Math.Min(type.Width, type.Length) < 305 - 0.5 || type.Thickness < 152 - 0.5)
            problems.Add(($"it is {Inches(type.Width)} by {Inches(type.Length)}, {Inches(type.Thickness)} thick.", "The IRC (R403.1.1) asks a footing 12 in wide and 6 in deep at the least."));
        if (pad.ColumnId is not null && (projection < 51 - 0.5 || projection > type.Thickness + 0.5))
            problems.Add(($"it projects {Inches(Math.Max(0, projection))} past its pier, {Inches(type.Thickness)} thick.", "The IRC (R403.1.1) asks a footing to project 2 in at the least, and no more than it is thick."));
        AddIrcDepth(problems, depth, project);
        return problems;
    }

    /// <summary>The least the IRC asks of a steel column (R407.3): 3 in Schedule 40 pipe - 88.9 across, 5.49 thick - its area, mm², and least radius of gyration, mm.</summary>
    public static readonly (double Area, double Radius) SchedulePipe = Pipe(88.9, 5.49);

    private static (double Area, double Radius) Pipe(double outside, double wall)
    {
        var inside = outside - 2 * wall;
        var area = Math.PI / 4 * (outside * outside - inside * inside);
        var second = Math.PI / 64 * (Math.Pow(outside, 4) - Math.Pow(inside, 4));
        return (area, Math.Sqrt(second / area));
    }

    /// <summary>Whether a column holds up a floor - an element of structure (Approved Document B 5.1) - rather than only a roof (5.3a).</summary>
    public static bool HoldsFloor(BimDocument document, Column column)
    {
        var top = column.GetTopElevation(document);
        return document.All<Floor>().Any(floor => floor.Contains(column.Location) && Math.Abs(floor.GetBottomElevation(document) - top) < 600)
               || document.All<Column>().Any(upper => upper.Structural && !ReferenceEquals(upper, column) &&
                                                                  upper.Location.DistanceTo(column.Location) < 100 && Math.Abs(upper.GetBaseElevation(document) - top) < 600);
    }

    /// <summary>Whether something is over a point to keep the weather off what stands there: a floor or a roof above it.</summary>
    private static bool Covered(BimDocument document, Point2D at, double above) =>
        document.All<Slab>().Any(slab => slab is Floor or Roof && slab.Boundary.Count >= 3 && slab.Contains(at) && slab.GetBottomElevation(document) > above + 100);

    /// <summary>Whether a column stands at the bottom of the building, on the ground or a floor cast on it, rather than on a floor or beam over a storey.</summary>
    private static bool OnTheGround(BimDocument document, Column column)
    {
        if (column.BaseAttachedTo is not null) return false;
        var lowest = document.Levels.Select(level => level.Elevation).DefaultIfEmpty(0).Min();
        return Math.Abs((document.FindLevel(column.LevelId)?.Elevation ?? 0) - lowest) < 1;
    }

    private static IReadOnlyList<(string Said, string Rule)> ColumnProblems(BimDocument document, Column column, ClearanceStandard standard)
    {
        var problems = new List<(string, string)>();
        if (document.FindType<ColumnType>(column.TypeId) is not { } type) return problems;

        if (OnTheGround(document, column) && Footings.Under(document, column) is null)
            problems.Add(("it stands on the ground floor with no footing under it.", standard.IsUs()
                ? "The IRC (R403.1, R403.1.1) puts a footing under a column, sized by the load it brings down - Pad Footing."
                : "Approved Document A (A1) asks for the load to be carried down to the ground - a pad footing under it (Pad Footing)."));

        if (!standard.IsUs())
        {
            var minutes = Doors.FireMinutes(type.FireRating);
            if (HoldsFloor(document, column) && minutes < 30)
                problems.Add(($"it holds up a floor, {(minutes > 0 ? $"rated {minutes} minutes" : "with no fire rating")}.",
                    "Approved Document B (5.1, Table B2) asks 30 minutes of an element of structure in a house - intumescent paint or boarding on steel; a column holding up only a roof is excluded (5.3a)."));
            return problems;
        }

        var section = type.Profile.Properties();
        var (width, depth) = type.Profile.Extent;
        switch (type.StructuralMaterial)
        {
            case BeamMaterial.Timber or BeamMaterial.Glulam:
                if (Math.Min(width, depth) < 88.9 - 0.5)
                    problems.Add(($"it is {Inches(width)} by {Inches(depth)}.", "The IRC (R407.3) asks a wood column to be a 4 by 4 at the least - 3½ by 3½ in."));
                var basement = (document.FindLevel(column.LevelId)?.Elevation ?? 0) < -500 && column.BaseOffset < 25;
                var exposed = !Covered(document, column.Location, column.GetBaseElevation(document));
                if (!type.PreservativeTreated && (basement || exposed))
                    problems.Add((basement ? "it stands on a basement floor slab, not preservative-treated." : "it stands out in the weather, not preservative-treated.",
                        "The IRC (R407.1, R317.1) asks a wood column on a basement slab, or out in the weather, to be naturally durable or preservative-treated - or, on the slab, on a pier or pedestal 1 in above it."));
                break;

            case BeamMaterial.Steel:
                // A round section is drawn as a polygon, a little inside the circle: within 2% is the pipe itself.
                if (section.Area < SchedulePipe.Area * 0.98 || section.LeastRadiusOfGyration < SchedulePipe.Radius * 0.98)
                    problems.Add(($"its section is {Units.SquareMillimetres(section.Area, "0")}, its least radius of gyration {Units.Bare(section.LeastRadiusOfGyration, "0.0")}.",
                        $"The IRC (R407.3) asks a steel column to be 3 in Schedule 40 pipe or its equal - {Units.SquareMillimetres(SchedulePipe.Area, "0")} and {Units.Bare(SchedulePipe.Radius, "0.0")} least radius of gyration."));
                if (!type.RustProtected)
                    problems.Add(("it has no rust-inhibitive paint.", "The IRC (R407.2) asks every surface of a steel column, inside and out, for a shop coat of rust-inhibitive paint, unless it is a corrosion-resistant steel."));
                break;
        }

        return problems;
    }

    /// <summary>What a footing or structural column comes to as its rules see it.</summary>
    public static string Verdict(BimDocument document, Element element)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var problems = Problems(document, element, standard);
        if (problems.Count > 0)
            return string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"));

        var project = document.ProjectInformation;
        switch (element)
        {
            case WallFooting strip when document.Find<Wall>(strip.WallId) is { } wall:
                if (!standard.IsUs())
                    return Table10(project.Ground, Loads.OnWall(document, wall), Footings.WallThickness(document, wall)).Width is { } width
                        ? $"Carries {Loads.OnWall(document, wall):0.0} kN/m on {Describe(project.Ground)}: Table 10 asks {width:0} wide - it is wide, thick and deep enough"
                        : "Meets Approved Document A";
                return $"Meets the IRC's {TableName(ConstructionOf(document, wall))} for {Stories(document, wall)} story {EnumText(FoundationOf(document, wall))}";
            case PadFooting pad:
                return pad.ColumnId is null ? "Carries no column" : $"Carries {PadLoad(document, pad):0.0} kN - big, thick and deep enough";
            case Column column:
                return $"Carries {Loads.OnColumn(document, column):0.0} kN - meets the rules";
            default:
                return "Nothing asked";
        }
    }

    /// <summary>Every footing's and structural column's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard)
    {
        var found = new List<BlockedClearance>();
        foreach (var element in document.Elements.Where(element => element is WallFooting or PadFooting or Column { Structural: true }))
        {
            var level = element is Column ? element.LevelId : Footings.LevelOf(document, element);
            if (level != levelId) continue;
            var outline = element switch
            {
                Column column when document.FindType<ColumnType>(column.TypeId) is { } type => column.Outline(type),
                _ => Footings.Footprint(document, element)
            };
            if (outline is not { Count: >= 3 }) continue;
            var name = document.FindType<ElementType>(element.TypeId)?.Name ?? (element is Column ? "A column" : "A footing");
            foreach (var (said, rule) in Problems(document, element, standard))
                found.Add(new BlockedClearance(new ClearanceZone(element, element is Column ? "the column" : "the footing", outline, rule), string.Empty, name, said));
        }

        return found;
    }
}
