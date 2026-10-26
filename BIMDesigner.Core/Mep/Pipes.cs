using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Mep;

/// <summary>What a pipe is made of - each with its own sizes and its own sizing table in BS EN 806-3.</summary>
public enum PipeMaterial
{
    Copper,
    StainlessSteel,
    PeX,
    GalvanisedSteel,

    /// <summary>PVC-U soil, waste and drain pipe (BS EN 1329 above the ground, BS EN 1401 below it): for drainage, not water.</summary>
    PvcU
}

/// <summary>A size a pipe is made in: its name, its outside diameter and wall, and its bore - what the water flows through.</summary>
public sealed record PipeSize(string Name, double Outside, double Inside);

/// <summary>
/// One column of BS EN 806-3's Table 3: a pipe size, the most loading units it carries, the most
/// any one draw-off on it may have, and how long it may be - where the table limits them.
/// </summary>
public sealed record PipeSizingColumn(PipeSize Size, int MaxLoad, int? Highest, double? MaxLength);

/// <summary>
/// Pipe sizes and how they are chosen, by BS EN 806-3's simplified method: each draw-off's
/// loading units (Table 2: a washbasin, a WC cistern 1; a kitchen sink, a shower, a washing
/// machine or dishwasher 2; a domestic bath 4; one unit is 0.1 l/s), added up from the last
/// draw-off back toward the supply, and the pipe each section needs read off Table 3 for the
/// material - which takes in how many draw-offs run at once, and keeps the water to 2 m/s in the
/// mains and risers and 4 m/s in the pipes to single fittings.
/// </summary>
public static class PipeSizes
{
    private static PipeSize Size(string name, double outside, double inside) => new(name, outside, inside);

    private static readonly PipeSize[] CopperSizes =
    {
        Size("12 x 1.0", 12, 10.0), Size("15 x 1.0", 15, 13.0), Size("18 x 1.0", 18, 16.0), Size("22 x 1.0", 22, 20.0), Size("28 x 1.5", 28, 25),
        Size("35 x 1.5", 35, 32), Size("42 x 1.5", 42, 39), Size("54 x 2.0", 54, 50), Size("76.1 x 2.0", 76.1, 72.1)
    };

    private static readonly PipeSize[] StainlessSizes =
    {
        Size("15 x 1.0", 15, 13.0), Size("18 x 1.0", 18, 16.0), Size("22 x 1.0", 22, 19.6), Size("28 x 1.2", 28, 25.6), Size("35 x 1.5", 35, 32),
        Size("42 x 1.5", 42, 39), Size("54 x 1.5", 54, 51), Size("76.1 x 2.0", 76.1, 72.1)
    };

    private static readonly PipeSize[] PeXSizes =
    {
        Size("12 x 1.7", 12, 8.4), Size("16 x 2.2", 16, 11.6), Size("20 x 2.8", 20, 14.4), Size("25 x 3.5", 25, 18.0), Size("32 x 4.4", 32, 23.2),
        Size("40 x 5.5", 40, 29), Size("50 x 6.9", 50, 36.2), Size("63 x 8.6", 63, 45.6)
    };

    private static readonly PipeSize[] SteelSizes =
    {
        Size("DN 15", 21.3, 16.0), Size("DN 20", 26.9, 21.6), Size("DN 25", 33.7, 27.2), Size("DN 32", 42.4, 35.9), Size("DN 40", 48.3, 41.8),
        Size("DN 50", 60.3, 53), Size("DN 65", 76.1, 68.8)
    };

    /// <summary>PVC-U's sizes by their nominal bore - what Approved Document H sizes traps, branches, stacks and drains by - with their outside diameters.</summary>
    private static readonly PipeSize[] PvcSizes =
    {
        Size("32", 36, 32), Size("40", 43, 40), Size("50", 56, 50), Size("75", 82, 75), Size("100", 110, 100), Size("150", 160, 150), Size("225", 250, 225)
    };

    public static IReadOnlyList<PipeSize> Of(PipeMaterial material) => material switch
    {
        PipeMaterial.Copper => CopperSizes,
        PipeMaterial.StainlessSteel => StainlessSizes,
        PipeMaterial.PeX => PeXSizes,
        PipeMaterial.PvcU => PvcSizes,
        _ => SteelSizes
    };

    /// <summary>Which part of BS EN 806-3's Table 3 a material is sized by.</summary>
    public static string TableOf(PipeMaterial material) => material switch
    {
        PipeMaterial.GalvanisedSteel => "Table 3.1",
        PipeMaterial.Copper => "Table 3.2",
        PipeMaterial.StainlessSteel => "Table 3.3",
        _ => "Table 3.4"
    };

    /// <summary>
    /// BS EN 806-3 Table 3 for a material, column by column: the small sizes take more loading
    /// units over shorter lengths, and the next column only where no one draw-off has more than its highest value.
    /// </summary>
    public static IReadOnlyList<PipeSizingColumn> Table(PipeMaterial material)
    {
        var sizes = Of(material);
        PipeSizingColumn Column(int size, int load, int? highest = null, double? length = null) => new(sizes[size], load, highest, length);
        return material switch
        {
            PipeMaterial.PvcU => Array.Empty<PipeSizingColumn>(),
            PipeMaterial.GalvanisedSteel => new[]
            {
                Column(0, 6, 4, 10), Column(1, 16, 15, 6), Column(2, 40), Column(3, 160), Column(4, 300), Column(5, 600), Column(6, 1600)
            },
            PipeMaterial.Copper => new[]
            {
                Column(0, 1, length: 20), Column(0, 2, length: 7), Column(0, 3, 2, 5),
                Column(1, 3, length: 15), Column(1, 4, length: 9), Column(1, 6, 4, 7),
                Column(2, 10, 5), Column(3, 20, 8), Column(4, 50), Column(5, 165), Column(6, 430), Column(7, 1050), Column(8, 2100)
            },
            PipeMaterial.StainlessSteel => new[]
            {
                Column(0, 3, length: 15), Column(0, 4, length: 9), Column(0, 6, 4, 7),
                Column(1, 10, 5), Column(2, 20, 8), Column(3, 50), Column(4, 165), Column(5, 430), Column(6, 1050), Column(7, 2100)
            },
            _ => new[]
            {
                Column(0, 1, length: 13), Column(0, 2, length: 4),
                Column(1, 3, length: 9), Column(1, 4, length: 5), Column(1, 5, 4, 4),
                Column(2, 8, 5), Column(3, 16, 8), Column(4, 35), Column(5, 100), Column(6, 350), Column(7, 700)
            }
        };
    }

    /// <summary>
    /// The smallest pipe of a material a section needs: carrying so many loading units, the most of
    /// any one draw-off on it so many, so far to the last draw-off - or null past the table's end.
    /// </summary>
    public static PipeSizingColumn? Required(PipeMaterial material, double load, double highest, double length) =>
        Table(material).FirstOrDefault(column =>
            load <= column.MaxLoad + 1e-9 &&
            (column.Highest is not { } most || highest <= most + 1e-9) &&
            (column.MaxLength is not { } longest || length <= longest * 1000 + 1e-6));

    /// <summary>The size of a material nearest an outside diameter.</summary>
    public static PipeSize Nearest(PipeMaterial material, double outside) =>
        Of(material).OrderBy(size => Math.Abs(size.Outside - outside)).First();

    /// <summary>
    /// A draw-off's loading units, BS EN 806-3 Table 2: a washbasin, a WC cistern 1; a domestic
    /// kitchen sink, a shower, a washing machine or a dishwasher 2; a domestic bath 4.
    /// </summary>
    public static int LoadingUnits(Architecture.ComponentForm form) => form switch
    {
        Architecture.ComponentForm.Basin or Architecture.ComponentForm.Toilet or Architecture.ComponentForm.WallHungToilet => 1,
        Architecture.ComponentForm.Sink or Architecture.ComponentForm.Shower or Architecture.ComponentForm.WashingMachine or Architecture.ComponentForm.Dishwasher => 2,
        Architecture.ComponentForm.Bath => 4,
        _ => 0
    };

    /// <summary>
    /// A fixture's water-supply fixture units in a dwelling, IRC Table P2903.6 (combined hot and
    /// cold): a lavatory 0.7, a tank WC 2.2, a bathtub, shower, kitchen sink, dishwasher or clothes washer 1.4.
    /// </summary>
    public static double FixtureUnits(Architecture.ComponentForm form) => form switch
    {
        Architecture.ComponentForm.Basin => 0.7,
        Architecture.ComponentForm.Toilet or Architecture.ComponentForm.WallHungToilet => 2.2,
        Architecture.ComponentForm.Bath or Architecture.ComponentForm.Shower or Architecture.ComponentForm.Sink
            or Architecture.ComponentForm.Dishwasher or Architecture.ComponentForm.WashingMachine => 1.4,
        _ => 0
    };
}

/// <summary>A pipe type: what it is made of, and so the sizes it comes in. Its cost is by the metre.</summary>
public sealed class PipeType : ElementType
{
    public PipeType(string name, PipeMaterial material) : base(name) => Material = material;

    public override BuiltInCategory Category => BuiltInCategory.Pipes;

    public PipeMaterial Material { get; set; }

    /// <summary>
    /// The pipe types a project starts with, priced by the metre: copper to BS EN 1057 for a home's
    /// water and heating, PE-X for its runs under floors, stainless and galvanised steel.
    /// </summary>
    public static IReadOnlyList<PipeType> Library() => new[]
    {
        new PipeType("Copper - BS EN 1057", PipeMaterial.Copper) { TypeMark = "PI1", AssemblyCode = "D2020", Cost = 22m },
        new PipeType("PE-X", PipeMaterial.PeX) { TypeMark = "PI2", AssemblyCode = "D2020", Cost = 12m },
        new PipeType("Stainless Steel", PipeMaterial.StainlessSteel) { TypeMark = "PI3", AssemblyCode = "D2020", Cost = 38m },
        new PipeType("Galvanised Steel", PipeMaterial.GalvanisedSteel) { TypeMark = "PI4", AssemblyCode = "D2020", Cost = 30m },
        new PipeType("PVC-U Soil and Waste - BS EN 1329", PipeMaterial.PvcU) { TypeMark = "PI5", AssemblyCode = "D2030", Cost = 16m },
        new PipeType("PVC-U Drain - BS EN 1401", PipeMaterial.PvcU) { TypeMark = "PI6", AssemblyCode = "G3020", Cost = 45m }
    };

    public static string Title(PipeMaterial material) => material switch
    {
        PipeMaterial.StainlessSteel => "Stainless Steel",
        PipeMaterial.PeX => "PE-X",
        PipeMaterial.GalvanisedSteel => "Galvanised Steel",
        PipeMaterial.PvcU => "PVC-U",
        _ => "Copper"
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(PipeParameters.Material, () => Title(Material),
            v => { foreach (var material in Enum.GetValues<PipeMaterial>()) if (Title(material) == v) Material = material; },
            Enum.GetValues<PipeMaterial>().Select(Title).ToArray());
        yield return ParameterValue.ReadOnly(PipeParameters.Sizes, () => string.Join(", ", PipeSizes.Of(Material).Select(size => size.Name)));
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }
}

/// <summary>
/// A length of pipe on a storey: straight, from one end to the other, each end so high above the
/// storey's level - level along a run, or upright where it rises or drops to a fitting. It carries
/// what its system carries, and is drawn in its colour. Pipes meeting end to end, or at a fitting's
/// connector, are joined: an elbow where two meet, a tee where three do.
/// </summary>
/// <summary>
/// How a pipe keeps a fire-separating wall or floor fire-separating where it passes through it, as
/// Approved Document B has it: a proprietary seal; the gap round it fire-stopped, for a pipe no larger
/// than its material allows; or sleeved in non-combustible pipe either side.
/// </summary>
public enum PipeSeal
{
    None,
    FireStopped,
    ProprietarySeal,
    Sleeved
}

public static class PipeSeals
{
    public static string Title(this PipeSeal seal) => seal switch
    {
        PipeSeal.FireStopped => "Fire-stopped",
        PipeSeal.ProprietarySeal => "Proprietary seal",
        PipeSeal.Sleeved => "Sleeved",
        _ => "None"
    };
}

public sealed class Pipe : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Pipes;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>How high its start is above its level, to its centre line.</summary>
    public double StartOffset { get; set; }

    public double EndOffset { get; set; }

    /// <summary>Its outside diameter, mm: one of its type's sizes.</summary>
    public double Diameter { get; set; } = 15;

    /// <summary>What it carries: cold or hot water, the heating's water, foul water, gas.</summary>
    public SystemKind Kind { get; set; } = SystemKind.ColdWater;

    /// <summary>The system it is part of, or none yet.</summary>
    public Guid? SystemId { get; set; }

    /// <summary>
    /// On a heating zone, whether it brings the water back to the heat source rather than out to
    /// the radiators - the return, not the flow. The two never join.
    /// </summary>
    public bool Return { get; set; }

    /// <summary>The insulation round it, mm thick - what it takes up beside it, and what clash tests allow for.</summary>
    public double Insulation { get; set; }

    /// <summary>How it keeps a fire-separating wall or floor it passes through fire-separating.</summary>
    public PipeSeal Seal { get; set; }

    /// <summary>Whether it runs up or down rather than along: its ends one over the other.</summary>
    public bool Upright => Start.DistanceTo(End) < 1;

    /// <summary>Where its ends are in the building, its level's height added.</summary>
    public (Point3D Start, Point3D End) Ends(BimDocument document)
    {
        var level = document.FindLevel(LevelId)?.Elevation ?? 0;
        return (Point3D.On(Start, level + StartOffset), Point3D.On(End, level + EndOffset));
    }

    /// <summary>Its length along it, mm, rising and falling included.</summary>
    public double Length
    {
        get
        {
            var rise = EndOffset - StartOffset;
            return Math.Sqrt(Math.Pow(Start.DistanceTo(End), 2) + rise * rise);
        }
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<PipeType>(TypeId);
        yield return ParameterValue.ReadOnly(PipeParameters.SystemName, () => document.Find<MepSystem>(SystemId)?.Name ?? "<none>");
        yield return ParameterValue.ReadOnly(PipeParameters.SystemType, () => Kind.Title());
        if (Kind == SystemKind.Heating)
            yield return ParameterValue.BindChoice(PipeParameters.Circuit, () => Return ? "Return" : "Flow", v => Return = v == "Return", new[] { "Flow", "Return" });
        yield return ParameterValue.BindChoice(PipeParameters.Size, () => SizeName(type),
            v => { if (type is not null && PipeSizes.Of(type.Material).FirstOrDefault(size => size.Name == v) is { } size) Diameter = size.Outside; },
            type is null ? Array.Empty<string>() : PipeSizes.Of(type.Material).Select(size => size.Name).ToArray());
        yield return ParameterValue.BindChoice(PipeParameters.Level, () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(PipeParameters.StartOffset, () => StartOffset, v => { StartOffset = v; return true; });
        yield return ParameterValue.BindValidated(PipeParameters.EndOffset, () => EndOffset, v => { EndOffset = v; return true; });
        yield return ParameterValue.ReadOnly(PipeParameters.Length, () => Length);
        yield return ParameterValue.BindValidated(PipeParameters.Insulation, () => Insulation, v => { if (v is < 0 or > 200) return false; Insulation = v; return true; });
        yield return ParameterValue.BindChoice(PipeParameters.Seal, () => Seal.Title(),
            v => { foreach (var seal in Enum.GetValues<PipeSeal>()) if (seal.Title() == v) Seal = seal; },
            Enum.GetValues<PipeSeal>().Select(seal => seal.Title()).ToArray());
        yield return ParameterValue.ReadOnly(PipeParameters.Carries, () => PipeNetworks.Describe(document, this));
        yield return ParameterValue.ReadOnly(PipeParameters.Supports, () => Mep.Supports.Describe(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>Its size as its type names it: "15 x 1.0".</summary>
    public string SizeName(PipeType? type) =>
        type is null ? $"{Diameter:0.#}" : PipeSizes.Nearest(type.Material, Diameter).Name;
}

/// <summary>A pipe in 3D: a round bar along it, as thick as it is, in its system's colour.</summary>
public static class PipeModel
{
    public static Mesh3D? Mesh(BimDocument document, Pipe pipe)
    {
        if (pipe.Length < 1) return null;
        var type = document.FindType<PipeType>(pipe.TypeId);
        var (start, end) = pipe.Ends(document);
        var mesh = new Mesh3D(pipe.Id, pipe.LevelId, MeshKind.Pipe, pipe.Kind.Colour(),
            $"{(type is null ? "Pipe" : PipeType.Title(type.Material))} {pipe.SizeName(type)}");
        mesh.AddTube(new[] { start, end }, Math.Max(pipe.Diameter, 6) / 2, 10);
        return mesh.IsEmpty ? null : mesh;
    }
}

public static class PipeParameters
{
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Sizes = new("Sizes", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition SystemName = new("System", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition SystemType = new("System Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Size = new("Size", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Level = new("Reference Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition StartOffset = new("Start Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition EndOffset = new("End Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Carries = new("Carries", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition Circuit = new("Flow or Return", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Insulation = new("Insulation (mm)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Seal = new("Fire Stopping", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Supports = new("Supports", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
}
