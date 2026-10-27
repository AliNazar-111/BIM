using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Mep;

/// <summary>A duct's shape in section.</summary>
public enum DuctShape
{
    Round,
    Rectangular
}

/// <summary>What a duct is made of: galvanised steel - spiral wound - rigid PVC, or flexible duct.</summary>
public enum DuctMaterial
{
    GalvanisedSteel,
    Pvc,
    Flexible
}

/// <summary>
/// How a duct passing through a fire-separating wall keeps it fire-separating, as Approved Document
/// B (9.12) has it: a fire damper closing on the heat (method 1), a fire and smoke damper closing on
/// smoke detected (method 4), or fire-resisting ductwork or an enclosure round it (methods 2 and 3).
/// </summary>
public enum FireProtection
{
    None,
    FireDamper,
    FireAndSmokeDamper,
    FireResisting
}

/// <summary>A size a duct is made in: round by its diameter, rectangular by its width and height, mm.</summary>
public sealed record DuctSize(string Name, double Width, double Height, DuctShape Shape)
{
    /// <summary>Its open area across, mm².</summary>
    public double Area => Shape == DuctShape.Round ? Math.PI * Width * Width / 4 : Width * Height;

    /// <summary>The diameter a round duct of the same friction would have: its hydraulic diameter, mm.</summary>
    public double Hydraulic => Shape == DuctShape.Round ? Width : 2 * Width * Height / (Width + Height);
}

/// <summary>
/// The sizes ducts come in: round galvanised spiral duct in BS EN 1506's diameters; a home's rigid
/// PVC round duct and its flat channel - 110 x 54 for a 100 round, 204 x 60 for a 125, 220 x 90 for
/// a 150 - and flexible duct for the last connection to a valve. In the US, whole inches: round
/// galvanized duct and flexible duct 4 to 16 in, rectangular sheet metal in 2 in steps.
/// </summary>
public static class DuctSizes
{
    private static DuctSize Round(double diameter) => new($"{diameter:0}", diameter, diameter, DuctShape.Round);

    private static DuctSize Flat(double width, double height) => new($"{width:0} x {height:0}", width, height, DuctShape.Rectangular);

    private static readonly DuctSize[] Spiral = { Round(100), Round(125), Round(150), Round(160), Round(200), Round(250), Round(315) };
    private static readonly DuctSize[] PvcRound = { Round(100), Round(125), Round(150) };
    private static readonly DuctSize[] FlatChannel = { Flat(110, 54), Flat(204, 60), Flat(220, 90) };
    private static readonly DuctSize[] Flexible = { Round(100), Round(125), Round(150), Round(160), Round(200) };

    /// <summary>Rectangular sheet steel ductwork, as it is made to any size: the usual steps, no deeper than wide.</summary>
    private static readonly DuctSize[] SheetSteel =
        (from width in new[] { 150, 200, 250, 300, 400, 500, 600, 800, 1000 }
         from height in new[] { 100, 150, 200, 250, 300, 400, 500 }
         where height <= width
         select Flat(width, height)).ToArray();

    // The US's sizes, named in inches and kept in mm.
    private static DuctSize RoundInches(int inches) => new($"{inches}\"", inches * Units.MillimetresPerInch, inches * Units.MillimetresPerInch, DuctShape.Round);

    private static DuctSize FlatInches(int width, int height) => new($"{width} x {height}\"", width * Units.MillimetresPerInch, height * Units.MillimetresPerInch, DuctShape.Rectangular);

    private static readonly DuctSize[] UsRound = new[] { 4, 5, 6, 7, 8, 9, 10, 12, 14, 16 }.Select(RoundInches).ToArray();
    private static readonly DuctSize[] UsFlexible = new[] { 4, 5, 6, 7, 8, 10, 12, 14 }.Select(RoundInches).ToArray();
    private static readonly DuctSize[] UsSheetMetal =
        (from width in new[] { 6, 8, 10, 12, 14, 16, 20, 24, 30 }
         from height in new[] { 4, 6, 8, 10, 12, 14 }
         where height <= width
         select FlatInches(width, height)).ToArray();

    public static IReadOnlyList<DuctSize> Of(DuctType type) => type.Inches
        ? type.Material == DuctMaterial.Flexible ? UsFlexible : type.Shape == DuctShape.Rectangular ? UsSheetMetal : UsRound
        : type.Material switch
    {
        DuctMaterial.Pvc => type.Shape == DuctShape.Rectangular ? FlatChannel : PvcRound,
        DuctMaterial.Flexible => Flexible,
        _ => type.Shape == DuctShape.Rectangular ? SheetSteel : Spiral
    };

    /// <summary>The size of a type nearest a width and height.</summary>
    public static DuctSize Nearest(DuctType type, double width, double height) =>
        Of(type).OrderBy(size => Math.Abs(size.Width - width) + Math.Abs(size.Height - height)).First();

    /// <summary>A duct's absolute roughness, mm: galvanised steel 0.15, rigid PVC 0.007, flexible duct pulled taut 3.</summary>
    public static double Roughness(DuctMaterial material) => material switch
    {
        DuctMaterial.Pvc => 0.007,
        DuctMaterial.Flexible => 3.0,
        _ => 0.15
    };
}

/// <summary>A duct type: what it is made of and its shape, and so the sizes it comes in. Its cost is by the metre.</summary>
public sealed class DuctType : ElementType
{
    public DuctType(string name, DuctMaterial material, DuctShape shape) : base(name)
    {
        Material = material;
        Shape = shape;
    }

    public override BuiltInCategory Category => BuiltInCategory.Ducts;

    public DuctMaterial Material { get; set; }

    public DuctShape Shape { get; set; }

    /// <summary>Whether it comes in the US's whole-inch sizes rather than metric ones.</summary>
    public bool Inches { get; set; }

    /// <summary>The duct types a project starts with, priced by the metre.</summary>
    public static IReadOnlyList<DuctType> Library() => new[]
    {
        new DuctType("Round - Galvanised Spiral", DuctMaterial.GalvanisedSteel, DuctShape.Round) { TypeMark = "DU1", AssemblyCode = "D3040", Cost = 24m },
        new DuctType("Round - PVC Rigid", DuctMaterial.Pvc, DuctShape.Round) { TypeMark = "DU2", AssemblyCode = "D3040", Cost = 9m },
        new DuctType("Flat Channel - PVC", DuctMaterial.Pvc, DuctShape.Rectangular) { TypeMark = "DU3", AssemblyCode = "D3040", Cost = 11m },
        new DuctType("Flexible - Insulated", DuctMaterial.Flexible, DuctShape.Round) { TypeMark = "DU4", AssemblyCode = "D3040", Cost = 7m },
        new DuctType("Rectangular - Galvanised Steel", DuctMaterial.GalvanisedSteel, DuctShape.Rectangular) { TypeMark = "DU5", AssemblyCode = "D3040", Cost = 45m },
        new DuctType("Round - Galvanized Steel (in)", DuctMaterial.GalvanisedSteel, DuctShape.Round) { TypeMark = "DU6", AssemblyCode = "D3040", Cost = 20m, Inches = true },
        new DuctType("Flexible - Insulated (in)", DuctMaterial.Flexible, DuctShape.Round) { TypeMark = "DU7", AssemblyCode = "D3040", Cost = 7m, Inches = true },
        new DuctType("Rectangular - Galvanized Steel (in)", DuctMaterial.GalvanisedSteel, DuctShape.Rectangular) { TypeMark = "DU8", AssemblyCode = "D3040", Cost = 40m, Inches = true }
    };

    public static string Title(DuctMaterial material) => material switch
    {
        DuctMaterial.Pvc => "PVC",
        DuctMaterial.Flexible => "Flexible",
        _ => "Galvanised Steel"
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(DuctParameters.Material, () => Title(Material),
            v => { foreach (var material in Enum.GetValues<DuctMaterial>()) if (Title(material) == v) Material = material; },
            Enum.GetValues<DuctMaterial>().Select(Title).ToArray());
        yield return ParameterValue.BindChoice(DuctParameters.Shape, () => Shape.ToString(),
            v => { if (Enum.TryParse<DuctShape>(v, out var shape)) Shape = shape; },
            Enum.GetNames<DuctShape>());
        yield return ParameterValue.ReadOnly(DuctParameters.Sizes, () => string.Join(", ", DuctSizes.Of(this).Select(size => size.Name)));
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }
}

/// <summary>
/// A length of duct on a storey, as a pipe is: straight from one end to the other, each end so high
/// above the storey's level, carrying what its system carries in its colour. Round, or rectangular
/// with its width across and its height up; joined to what its ends meet.
/// </summary>
public sealed class Duct : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Ducts;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>How high its start is above its level, to its centre line.</summary>
    public double StartOffset { get; set; }

    public double EndOffset { get; set; }

    /// <summary>Its diameter, or a rectangular one's width, mm.</summary>
    public double Width { get; set; } = 125;

    /// <summary>A rectangular one's height, mm; a round one's diameter again.</summary>
    public double Height { get; set; } = 125;

    /// <summary>What it carries: air supplied, air extracted, or air to and from outside.</summary>
    public SystemKind Kind { get; set; } = SystemKind.SupplyAir;

    public Guid? SystemId { get; set; }

    /// <summary>Where it passes through a fire-separating wall or floor, what keeps it fire-separating.</summary>
    public FireProtection FireProtection { get; set; }

    /// <summary>A damper's fire resistance, minutes - its E (or ES) classification: 60 at least, or the element's (Approved Document B 9.20, 9.21).</summary>
    public int DamperRating { get; set; } = 60;

    /// <summary>The insulation round it, mm - of a material as good as one of 0.04 W/mK; none at 0.</summary>
    public double Insulation { get; set; }

    public bool Upright => Start.DistanceTo(End) < 1;

    public (Point3D Start, Point3D End) Ends(BimDocument document)
    {
        var level = document.FindLevel(LevelId)?.Elevation ?? 0;
        return (Point3D.On(Start, level + StartOffset), Point3D.On(End, level + EndOffset));
    }

    public double Length
    {
        get
        {
            var rise = EndOffset - StartOffset;
            return Math.Sqrt(Math.Pow(Start.DistanceTo(End), 2) + rise * rise);
        }
    }

    /// <summary>Its size as its type has it.</summary>
    public DuctSize Size(BimDocument document) =>
        document.FindType<DuctType>(TypeId) is { } type ? DuctSizes.Nearest(type, Width, Height) : new DuctSize($"{Width:0}", Width, Height, DuctShape.Round);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<DuctType>(TypeId);
        yield return ParameterValue.ReadOnly(PipeParameters.SystemName, () => document.Find<MepSystem>(SystemId)?.Name ?? "<none>");
        yield return ParameterValue.ReadOnly(PipeParameters.SystemType, () => Kind.Title());
        yield return ParameterValue.BindChoice(PipeParameters.Size, () => Size(document).Name,
            v => { if (type is not null && DuctSizes.Of(type).FirstOrDefault(size => size.Name == v) is { } size) (Width, Height) = (size.Width, size.Height); },
            type is null ? Array.Empty<string>() : DuctSizes.Of(type).Select(size => size.Name).ToArray());
        yield return ParameterValue.BindChoice(PipeParameters.Level, () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(PipeParameters.StartOffset, () => StartOffset, v => { StartOffset = v; return true; });
        yield return ParameterValue.BindValidated(PipeParameters.EndOffset, () => EndOffset, v => { EndOffset = v; return true; });
        yield return ParameterValue.ReadOnly(PipeParameters.Length, () => Length);
        yield return ParameterValue.ReadOnly(PipeParameters.Supports, () => Supports.Describe(document, this));
        yield return ParameterValue.BindChoice(DuctParameters.FireProtection, () => Describe(FireProtection),
            v => { foreach (var protection in Enum.GetValues<FireProtection>()) if (Describe(protection) == v) FireProtection = protection; },
            Enum.GetValues<FireProtection>().Select(Describe).ToArray());
        if (FireProtection is FireProtection.FireDamper or FireProtection.FireAndSmokeDamper)
            yield return ParameterValue.BindValidated(DuctParameters.DamperRating, () => (double)DamperRating, v => { if (v is < 0 or > 240) return false; DamperRating = (int)Math.Round(v); return true; });
        yield return ParameterValue.BindValidated(Units.Imperial ? DuctParameters.InsulationInches : DuctParameters.Insulation, () => Insulation, v => { if (v is < 0 or > 300) return false; Insulation = v; return true; });
        yield return ParameterValue.ReadOnly(PipeParameters.Carries, () => DuctNetworks.Describe(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public static string Describe(FireProtection protection) => protection switch
    {
        FireProtection.FireDamper => "Fire damper",
        FireProtection.FireAndSmokeDamper => "Fire and smoke damper",
        FireProtection.FireResisting => "Fire-resisting duct or enclosure",
        _ => "None"
    };
}

/// <summary>A duct in 3D: a round bar, or a box along it, as big as it is, in its system's colour.</summary>
public static class DuctModel
{
    public static Mesh3D? Mesh(BimDocument document, Duct duct)
    {
        if (duct.Length < 1) return null;
        var type = document.FindType<DuctType>(duct.TypeId);
        var size = duct.Size(document);
        var (start, end) = duct.Ends(document);
        var mesh = new Mesh3D(duct.Id, duct.LevelId, MeshKind.Duct, duct.Kind.Colour(),
            $"{(type is null ? "Duct" : DuctType.Title(type.Material))} {size.Name}");
        if (size.Shape == DuctShape.Round) mesh.AddTube(new[] { start, end }, Math.Max(size.Width, 20) / 2, 16);
        else Box(mesh, start, end, size.Width, size.Height);
        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>A rectangular duct: its four sides and ends along it, its width level across it and its height up - or, upright, its width along x.</summary>
    private static void Box(Mesh3D mesh, Point3D start, Point3D end, double width, double height)
    {
        var (dx, dy, dz) = (end.X - start.X, end.Y - start.Y, end.Z - start.Z);
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var along = (X: dx / length, Y: dy / length, Z: dz / length);
        var flat = Math.Sqrt(along.X * along.X + along.Y * along.Y);
        var across = flat > 1e-6 ? (X: -along.Y / flat, Y: along.X / flat, Z: 0.0) : (X: 1.0, Y: 0.0, Z: 0.0);
        var up = (X: across.Y * along.Z - across.Z * along.Y, Y: across.Z * along.X - across.X * along.Z, Z: across.X * along.Y - across.Y * along.X);
        if (flat <= 1e-6) (across, up) = ((1, 0, 0), (0, 1, 0));

        Point3D Corner(Point3D at, double a, double u) =>
            new(at.X + across.X * a + up.X * u, at.Y + across.Y * a + up.Y * u, at.Z + across.Z * a + up.Z * u);
        var (w, h) = (width / 2, height / 2);
        var near = new[] { Corner(start, -w, -h), Corner(start, w, -h), Corner(start, w, h), Corner(start, -w, h) };
        var far = new[] { Corner(end, -w, -h), Corner(end, w, -h), Corner(end, w, h), Corner(end, -w, h) };
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            mesh.AddQuad(near[i], near[j], far[j], far[i]);
        }

        mesh.AddQuad(near[3], near[2], near[1], near[0]);
        mesh.AddQuad(far[0], far[1], far[2], far[3]);
    }
}

public static class DuctParameters
{
    public static readonly ParameterDefinition Material = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Shape = new("Shape", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Sizes = new("Sizes", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FireProtection = new("Fire Protection", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition DamperRating = new("Damper Rating (minutes)", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Insulation = new("Insulation (mm at 0.04 W/mK)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition InsulationInches = new("Insulation (thickness, about R-3.6 an inch)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);
}
