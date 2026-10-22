using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>A beam's cross-section: a rectangle - timber, glulam, concrete - or a steel I.</summary>
public enum BeamSection
{
    Rectangular,
    IBeam
}

/// <summary>What a beam is made of, for its weight and how it reads.</summary>
public enum BeamMaterial
{
    Steel,
    Timber,
    Glulam,
    Concrete
}

/// <summary>What a beam does: carries others, carries a floor, spans an opening, carries a roof.</summary>
public enum BeamUsage
{
    Beam,
    Joist,
    Lintel,
    Purlin
}

/// <summary>Where a beam hangs from the line it is drawn on: its top there - under a floor - its middle, or its underside.</summary>
public enum BeamJustification
{
    Top,
    Centre,
    Bottom
}

/// <summary>
/// A beam type: its section - a steel universal beam or wide flange, or a rectangle of timber,
/// glulam or concrete - its sizes, and what it is made of.
/// </summary>
public sealed class BeamType : ElementType
{
    public BeamType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.StructuralFraming;

    public BeamSection Section { get; set; } = BeamSection.Rectangular;

    public BeamMaterial Material { get; set; } = BeamMaterial.Timber;

    /// <summary>How deep it is, top to bottom, mm.</summary>
    public double Depth { get; set; } = 225;

    /// <summary>How wide it is, or its flanges are, mm.</summary>
    public double Width { get; set; } = 75;

    /// <summary>An I-section's web, mm.</summary>
    public double WebThickness { get; set; }

    /// <summary>An I-section's flanges, mm.</summary>
    public double FlangeThickness { get; set; }

    /// <summary>A material of the project's for it, where it has one; none for its usual colour.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>The area of its section, mm²: an I's flanges and web, root radii left out.</summary>
    public double SectionArea => Section == BeamSection.IBeam
        ? 2 * Width * FlangeThickness + Math.Max(0, Depth - 2 * FlangeThickness) * WebThickness
        : Width * Depth;

    /// <summary>What it weighs, kg a metre: its section by what it is made of - steel 7850 kg/m³, timber and glulam 420, concrete 2400.</summary>
    public double MassPerMetre => SectionArea / 1e6 * Density(Material);

    public static double Density(BeamMaterial material) => material switch
    {
        BeamMaterial.Steel => 7850,
        BeamMaterial.Concrete => 2400,
        _ => 420
    };

    /// <summary>How it reads with no material of its own.</summary>
    public static ColourRgb Colour(BeamMaterial material) => material switch
    {
        BeamMaterial.Steel => new ColourRgb(0x8B, 0x3A, 0x2F),
        BeamMaterial.Concrete => new ColourRgb(0xB5, 0xB3, 0xAE),
        BeamMaterial.Glulam => new ColourRgb(0xD8, 0xB8, 0x86),
        _ => new ColourRgb(0xC8, 0xA8, 0x7A)
    };

    public BeamType Duplicate(string name) => WithCommonOf(new BeamType(name)
    {
        Section = Section, Material = Material, Depth = Depth, Width = Width, WebThickness = WebThickness,
        FlangeThickness = FlangeThickness, MaterialId = MaterialId
    });

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        yield return ParameterValue.BindChoice(BeamTypeParameters.Section,
            () => EnumText.Humanise(Section),
            v => { if (EnumText.TryParse<BeamSection>(v, out var section)) Section = section; },
            EnumText.Choices<BeamSection>());
        yield return ParameterValue.BindChoice(BeamTypeParameters.Material,
            () => EnumText.Humanise(Material),
            v => { if (EnumText.TryParse<BeamMaterial>(v, out var material)) Material = material; },
            EnumText.Choices<BeamMaterial>());
        yield return Size(BeamTypeParameters.Depth, () => Depth, v => Depth = v, 50, 2000);
        yield return Size(BeamTypeParameters.Width, () => Width, v => Width = v, 20, 1000);
        yield return Size(BeamTypeParameters.WebThickness, () => WebThickness, v => WebThickness = v, 0, 100);
        yield return Size(BeamTypeParameters.FlangeThickness, () => FlangeThickness, v => FlangeThickness = v, 0, 100);
        yield return ParameterValue.ReadOnly(BeamTypeParameters.SectionArea, () => SectionArea);
        yield return ParameterValue.ReadOnly(BeamTypeParameters.Mass, () => Math.Round(MassPerMetre, 1));

        if (document is not null)
        {
            var names = document.Materials.Select(material => material.Name).OrderBy(name => name).ToArray();
            yield return ParameterValue.BindChoice(BeamTypeParameters.MaterialName,
                () => document.FindMaterial(MaterialId)?.Name ?? ComponentType.ByColour,
                v => MaterialId = document.Materials.FirstOrDefault(material => material.Name == v)?.Id ?? Guid.Empty,
                names.Prepend(ComponentType.ByColour).ToArray());
        }

        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
    }

    private static ParameterValue Size(ParameterDefinition definition, Func<double> get, Action<double> set, double least, double most) =>
        ParameterValue.BindValidated(definition, get, (double v) =>
        {
            if (v < least || v > most) return false;
            set(v);
            return true;
        });

    private static BeamType Steel(string name, double depth, double width, double web, double flange, string mark) =>
        new(name) { Section = BeamSection.IBeam, Material = BeamMaterial.Steel, Depth = depth, Width = width, WebThickness = web, FlangeThickness = flange, TypeMark = mark };

    private static BeamType Rectangle(string name, BeamMaterial material, double width, double depth, string mark) =>
        new(name) { Material = material, Width = width, Depth = depth, TypeMark = mark };

    /// <summary>
    /// The beams a project starts with: UK universal beams (British Steel's sizes) and US wide
    /// flanges; C24 timber joists and beams; GL24h glulam; and reinforced concrete.
    /// </summary>
    public static IReadOnlyList<BeamType> Library() => new[]
    {
        Steel("UB 152x89x16", 152.4, 88.7, 4.5, 7.7, "B1"),
        Steel("UB 203x133x25", 203.2, 133.2, 5.7, 7.8, "B2"),
        Steel("UB 254x146x31", 251.4, 146.1, 6.0, 8.6, "B3"),
        Steel("UB 305x165x40", 303.4, 165.0, 6.0, 10.2, "B4"),
        Steel("UB 356x171x51", 355.0, 171.5, 7.4, 11.5, "B5"),
        Steel("UB 457x191x67", 453.4, 189.9, 8.5, 12.7, "B6"),
        Steel("W8x10", 200.4, 100.1, 4.3, 5.2, "W1"),
        Steel("W10x12", 250.7, 100.6, 4.8, 5.3, "W2"),
        Steel("W12x26", 310.4, 164.8, 5.8, 9.7, "W3"),
        Steel("W16x31", 403.4, 140.5, 7.0, 11.2, "W4"),
        Rectangle("Timber C24 47x150 Joist", BeamMaterial.Timber, 47, 150, "T1"),
        Rectangle("Timber C24 47x200 Joist", BeamMaterial.Timber, 47, 200, "T2"),
        Rectangle("Timber C24 47x225 Joist", BeamMaterial.Timber, 47, 225, "T3"),
        Rectangle("Timber C24 75x225", BeamMaterial.Timber, 75, 225, "T4"),
        Rectangle("Glulam GL24h 90x270", BeamMaterial.Glulam, 90, 270, "G1"),
        Rectangle("Glulam GL24h 115x315", BeamMaterial.Glulam, 115, 315, "G2"),
        Rectangle("Glulam GL24h 140x360", BeamMaterial.Glulam, 140, 360, "G3"),
        Rectangle("Concrete 300x500", BeamMaterial.Concrete, 300, 500, "C1"),
        Rectangle("Concrete 300x600", BeamMaterial.Concrete, 300, 600, "C2")
    };
}

public static class BeamTypeParameters
{
    public static readonly ParameterDefinition Section = new("Section", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Material = new("Structural Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition MaterialName = new("Material", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition WebThickness = new("Web Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FlangeThickness = new("Flange Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition SectionArea = new("Section Area", ParameterDataType.Area, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Mass = new("Mass per Metre", ParameterDataType.Number, ParameterBinding.Type, ParameterGroup.Dimensions);
}

/// <summary>
/// A beam: a member spanning between two points in plan, hung from its level - its top there,
/// as under a floor, unless it is told otherwise - and each end raised or lowered from it, so it
/// can slope. What it passes over is measured against its underside: a stair's headroom, a
/// door's head.
/// </summary>
public sealed class Beam : Element
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFraming;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    /// <summary>How far its line is above its level at its start, and at its end, mm.</summary>
    public double StartOffset { get; set; }

    public double EndOffset { get; set; }

    public BeamJustification Justification { get; set; } = BeamJustification.Top;

    public BeamUsage Usage { get; set; } = BeamUsage.Beam;

    public double Length => Start.DistanceTo(End);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(BeamParameters.ReferenceLevel,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(BeamParameters.StartOffset, () => StartOffset, (double v) => Offset(v, value => StartOffset = value));
        yield return ParameterValue.BindValidated(BeamParameters.EndOffset, () => EndOffset, (double v) => Offset(v, value => EndOffset = value));
        yield return ParameterValue.BindChoice(BeamParameters.Justification,
            () => EnumText.Humanise(Justification),
            v => { if (EnumText.TryParse<BeamJustification>(v, out var justification)) Justification = justification; },
            EnumText.Choices<BeamJustification>());
        yield return ParameterValue.BindChoice(BeamParameters.Usage,
            () => EnumText.Humanise(Usage),
            v => { if (EnumText.TryParse<BeamUsage>(v, out var usage)) Usage = usage; },
            EnumText.Choices<BeamUsage>());
        yield return ParameterValue.ReadOnly(BeamParameters.Length, () => Length);
        yield return ParameterValue.ReadOnly(BeamParameters.TopElevation, () => Beams.Top(document, this, 0));
        yield return ParameterValue.ReadOnly(BeamParameters.BottomElevation, () => Math.Min(Beams.Bottom(document, this, 0), Beams.Bottom(document, this, 1)));
        yield return ParameterValue.ReadOnly(BeamParameters.Mass, () => Math.Round(Beams.TypeOf(document, this).MassPerMetre * Length / 1000, 1));
        yield return ParameterValue.ReadOnly(BeamParameters.Volume, () => Beams.TypeOf(document, this).SectionArea * Length);
        yield return ParameterValue.ReadOnly(BeamParameters.Rule, () => Beams.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static bool Offset(double value, Action<double> set)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 20000) return false;
        set(value);
        return true;
    }
}

public static class BeamParameters
{
    public static readonly ParameterDefinition ReferenceLevel = new("Reference Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition StartOffset = new("Start Level Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition EndOffset = new("End Level Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Justification = new("z-Justification", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Usage = new("Structural Usage", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TopElevation = new("Elevation at Top", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BottomElevation = new("Elevation at Bottom", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Mass = new("Mass", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Volume = new("Volume", ParameterDataType.Volume, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rule = new("Clearances", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
}

/// <summary>
/// Where a beam is: its footprint, how high its top and its underside are along it, what it
/// passes over too low; and its shape in 3D.
/// </summary>
public static class Beams
{
    private static readonly BeamType Fallback = BeamType.Library()[1];

    public static BeamType TypeOf(BimDocument document, Beam beam) => document.FindType<BeamType>(beam.TypeId) ?? Fallback;

    /// <summary>Its line's height at a point along it, from its start (0) to its end (1).</summary>
    public static double Line(BimDocument document, Beam beam, double t) =>
        (document.FindLevel(beam.LevelId)?.Elevation ?? 0) + beam.StartOffset + (beam.EndOffset - beam.StartOffset) * Math.Clamp(t, 0, 1);

    /// <summary>Its top at a point along it.</summary>
    public static double Top(BimDocument document, Beam beam, double t)
    {
        var depth = TypeOf(document, beam).Depth;
        return Line(document, beam, t) + beam.Justification switch
        {
            BeamJustification.Centre => depth / 2,
            BeamJustification.Bottom => depth,
            _ => 0
        };
    }

    /// <summary>Its underside at a point along it.</summary>
    public static double Bottom(BimDocument document, Beam beam, double t) => Top(document, beam, t) - TypeOf(document, beam).Depth;

    /// <summary>How far along it a point in plan is, its start 0 and its end 1.</summary>
    public static double Along(Beam beam, Point2D at)
    {
        var way = beam.End - beam.Start;
        var length = way.Dot(way);
        return length < 1e-9 ? 0 : Math.Clamp((at - beam.Start).Dot(way) / length, 0, 1);
    }

    /// <summary>Its underside over a point in plan.</summary>
    public static double UndersideAt(BimDocument document, Beam beam, Point2D at) => Bottom(document, beam, Along(beam, at));

    /// <summary>Its footprint in plan: as wide as it is, from end to end - each end cut back to the face of a column, or of a beam it frames into.</summary>
    public static IReadOnlyList<Point2D> Footprint(BimDocument document, Beam beam)
    {
        var (start, end) = Ends(document, beam);
        return Band(start, end, TypeOf(document, beam).Width / 2);
    }

    private static IReadOnlyList<Point2D> Band(Point2D start, Point2D end, double half)
    {
        var across = (end - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * half;
        return new[] { start - across, end - across, end + across, start + across };
    }

    /// <summary>
    /// Its ends as it is built: each cut back to the face of the column it lands on, or to the
    /// side of the beam it frames into - a joist into a beam, a beam into a girder - where its
    /// heights meet. Its line runs on to where it was drawn; what is built stops at the face.
    /// </summary>
    public static (Point2D Start, Point2D End) Ends(BimDocument document, Beam beam)
    {
        if (beam.Length < 1) return (beam.Start, beam.End);
        var (top, bottom) = (Math.Max(Top(document, beam, 0), Top(document, beam, 1)), Math.Min(Bottom(document, beam, 0), Bottom(document, beam, 1)));
        return (Trimmed(document, beam, beam.Start, beam.End, top, bottom), Trimmed(document, beam, beam.End, beam.Start, top, bottom));
    }

    private static Point2D Trimmed(BimDocument document, Beam beam, Point2D end, Point2D other, double top, double bottom)
    {
        foreach (var column in document.All<Column>())
        {
            if (document.FindType<ColumnType>(column.TypeId) is not { } type) continue;
            if (column.GetBaseElevation(document) >= top || column.GetTopElevation(document) <= bottom) continue;
            var outline = column.Outline(type);
            if (outline.Count >= 3 && Polygon2D.Contains(outline, end)) return Exit(outline, end, other) ?? end;
        }

        foreach (var carrier in document.All<Beam>())
        {
            if (ReferenceEquals(carrier, beam) || FramesInto(document, end, other, carrier, top, bottom) is not { } face) continue;
            return Exit(face, end, other) ?? end;
        }

        return end;
    }

    /// <summary>
    /// The footprint of a beam another's end frames into: where the end lies in it, along its
    /// side - not at its own end, where two beams meet at a corner, nor running along it, as one
    /// laid over another in line does - and their heights meet.
    /// </summary>
    private static IReadOnlyList<Point2D>? FramesInto(BimDocument document, Point2D end, Point2D other, Beam carrier, double top, double bottom)
    {
        if (carrier.Length < 1 || Top(document, carrier, 0.5) <= bottom || Bottom(document, carrier, 0.5) >= top) return null;
        var half = TypeOf(document, carrier).Width / 2;
        var way = carrier.End - carrier.Start;
        if (Math.Abs((end - other).NormalisedOrDefault(Vector2D.UnitX).Cross(way.NormalisedOrDefault(Vector2D.UnitX))) < Math.Sin(Math.PI / 18)) return null;
        var along = (end - carrier.Start).Dot(way) / way.Dot(way);
        var margin = (half + 1) / carrier.Length;
        if (along <= margin || along >= 1 - margin) return null;
        var footprint = Band(carrier.Start, carrier.End, half);
        return Polygon2D.Contains(footprint, end) ? footprint : null;
    }

    /// <summary>Where a line from inside an outline toward a point leaves it: its first crossing of the outline's edges.</summary>
    private static Point2D? Exit(IReadOnlyList<Point2D> outline, Point2D from, Point2D toward)
    {
        var way = toward - from;
        double? nearest = null;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var edge = b - a;
            var denominator = way.Cross(edge);
            if (Math.Abs(denominator) < 1e-12) continue;
            var t = (a - from).Cross(edge) / denominator;
            var u = (a - from).Cross(way) / denominator;
            if (t <= 1e-9 || t > 1 || u < -1e-9 || u > 1 + 1e-9) continue;
            if (nearest is not { } known || t < known) nearest = t;
        }

        return nearest is { } at ? from + way * at : null;
    }

    /// <summary>
    /// What carries an end of a beam: a column it lands on, a wall it bears on or is built into, a
    /// beam it frames into - whichever stands under it, up to its underside. None where nothing is.
    /// </summary>
    public static string? CarriedBy(BimDocument document, Beam beam, Point2D end)
    {
        var bottom = Bottom(document, beam, Along(beam, end));
        foreach (var column in document.All<Column>())
        {
            if (document.FindType<ColumnType>(column.TypeId) is not { } type) continue;
            var outline = column.Outline(type);
            if (outline.Count >= 3 && Polygon2D.Contains(outline, end) && column.GetTopElevation(document) >= bottom - 50 && column.GetBaseElevation(document) < bottom) return "a column";
        }

        // On a wall - its end on the wall's end or face counted, as a beam snapped to a corner is.
        bool On(StairModel.WallBand wall) => wall.Holds(end)
            || Enumerable.Range(0, wall.Band.Count).Any(i => Line2D.DistanceFromSegment(end, wall.Band[i], wall.Band[(i + 1) % wall.Band.Count]) <= 5);
        if (StairModel.WallBands(document).Any(wall => On(wall) && wall.Top >= bottom - 50 && wall.Base < bottom)) return "a wall";

        // On a beam under it - up to its underside, not one far below in another storey.
        foreach (var carrier in document.All<Beam>().Where(other => !ReferenceEquals(other, beam) && other.Length >= 1))
            if (Polygon2D.Contains(Band(carrier.Start, carrier.End, TypeOf(document, carrier).Width / 2 + 1), end)
                && UndersideAt(document, carrier, end) < bottom + TypeOf(document, beam).Depth
                && Top(document, carrier, Along(carrier, end)) >= bottom - 50)
                return "a beam";

        return null;
    }

    /// <summary>
    /// The lowest its underside comes over a region in plan - a step, a doorway - where it passes
    /// over it, measured where they overlap; none where it does not.
    /// </summary>
    public static double? LowestOver(BimDocument document, Beam beam, IReadOnlyList<Point2D> region)
    {
        var over = PolygonBoolean.Combine(Footprint(document, beam), region, BooleanOperation.Intersection);
        if (over.Sum(piece => piece.Area) < 100) return null;
        return over.SelectMany(piece => piece.Outer).Min(point => UndersideAt(document, beam, point));
    }

    /// <summary>
    /// What a beam comes down too low over: a doorway it crosses below the door's head. Each
    /// said, with how low it comes and how high the head is.
    /// </summary>
    public static IReadOnlyList<string> Problems(BimDocument document, Beam beam)
    {
        var problems = new List<string>();
        if (beam.Length < 1)
        {
            problems.Add("It has no length.");
            return problems;
        }

        // Each end carried by something: a column, a wall, a beam.
        if (CarriedBy(document, beam, beam.Start) is null) problems.Add("Its start rests on nothing: no column, wall or beam carries it there.");
        if (CarriedBy(document, beam, beam.End) is null) problems.Add("Its end rests on nothing: no column, wall or beam carries it there.");

        // Deep enough for its span, as a floor's beams are first sized: a span of about 19 times
        // its depth for solid timber (as TRADA's span tables come out for joists at 400 centres),
        // 18 for glulam, 20 for steel and concrete. A rule of thumb, not a calculation.
        var type = TypeOf(document, beam);
        var ratio = type.Material switch { BeamMaterial.Timber => 19, BeamMaterial.Glulam => 18, _ => 20 };
        if (type.Depth > 0 && beam.Length > ratio * type.Depth + 0.5)
            problems.Add($"It spans {beam.Length / 1000:0.0} m on a section {type.Depth:0} deep - more than the {ratio} times its depth a floor's {EnumText.Humanise(type.Material).ToLowerInvariant()} beam is first sized at: have an engineer check it, or take a deeper section.");

        foreach (var door in document.All<Door>())
        {
            if (document.Find<Wall>(door.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType) continue;
            if (document.FindType<DoorType>(door.TypeId) is not { } doorType) continue;
            var (from, to) = door.GetSpan(doorType);
            var half = wallType.Structure.TotalWidth / 2 + 1;
            var doorway = new[] { wall.PointAt(wallType.Structure, from, -half), wall.PointAt(wallType.Structure, to, -half), wall.PointAt(wallType.Structure, to, half), wall.PointAt(wallType.Structure, from, half) };
            var head = wall.GetBaseElevation(document) + door.SillHeight + doorType.Height;
            if (LowestOver(document, beam, doorway) is { } low && low < head - 0.5 && low > wall.GetBaseElevation(document) + door.SillHeight)
                problems.Add($"Its underside comes down to {low:0} across the doorway{(string.IsNullOrEmpty(door.Mark) ? "" : $" of door {door.Mark}")}, below the door's head at {head:0}: raise it, or carry it over the door as a lintel.");
        }

        return problems;
    }

    public static string Verdict(BimDocument document, Beam beam)
    {
        var problems = Problems(document, beam);
        return problems.Count == 0 ? "Clear" : string.Join(" ", problems);
    }

    /// <summary>
    /// A beam in 3D: its section carried from end to end - an I's flanges and web, or a
    /// rectangle - its top stopping under the floor or flat roof it is set into, where it is.
    /// </summary>
    public static Mesh3D? Mesh(BimDocument document, Beam beam) => Mesh(document, beam, beam.Id);

    /// <summary>A beam in 3D as another element's - a joist of a joist layout, picked as the layout.</summary>
    public static Mesh3D? Mesh(BimDocument document, Beam beam, Guid elementId)
    {
        if (beam.Length < 1) return null;
        var type = TypeOf(document, beam);
        var material = document.FindMaterial(type.MaterialId);
        var mesh = new Mesh3D(elementId, beam.LevelId, MeshKind.Beam, material?.SurfaceColour ?? BeamType.Colour(type.Material), material?.Name ?? EnumText.Humanise(type.Material));
        var (start, end) = Ends(document, beam);

        var along = (beam.End - beam.Start).NormalisedOrDefault(Vector2D.UnitX);
        var across = along.PerpendicularLeft();
        double Top(Point2D at) => Beams.Top(document, beam, Along(beam, at));
        double Bottom(Point2D at) => Beams.Bottom(document, beam, Along(beam, at));
        double Clip(Point2D at) => Embedding(document, beam, at);
        IReadOnlyList<Point2D> Strip(double half) => Band(start, end, half);

        // Each part of its section from so low to so high, cut off where the slab it is set into is: nothing of a part wholly in it.
        void Part(double half, Func<Point2D, double> low, Func<Point2D, double> high)
        {
            double High(Point2D at) => Math.Min(high(at), Clip(at));
            if (High(start) <= low(start) + 0.5 && High(end) <= low(end) + 0.5) return;
            mesh.AddExtrusion(Strip(half), low, at => Math.Max(low(at), High(at)));
        }

        if (type.Section == BeamSection.IBeam && type.FlangeThickness > 0 && type.WebThickness > 0)
        {
            var flange = type.FlangeThickness;
            Part(type.Width / 2, Bottom, at => Bottom(at) + flange);
            Part(type.WebThickness / 2, at => Bottom(at) + flange, at => Top(at) - flange);
            Part(type.Width / 2, at => Top(at) - flange, Top);
        }
        else Part(type.Width / 2, Bottom, Top);

        return mesh.IsEmpty ? null : mesh;
    }

    /// <summary>
    /// The underside of the floor or flat roof a beam's top is set into, over a point - where the
    /// slab sits on it, the beam shows below the slab, not through it; infinity where none is.
    /// </summary>
    private static double Embedding(BimDocument document, Beam beam, Point2D at)
    {
        var top = Beams.Top(document, beam, Along(beam, at));
        var middle = beam.Start.MidpointTo(beam.End);
        foreach (var slab in document.All<Slab>())
        {
            if (slab.Boundary.Count < 3 || !slab.Contains(middle) || slab is Roof { IsExtrusion: true }) continue;
            if (slab is Roof roof && !roof.Surface(document).IsFlat) continue;
            var (bottom, above) = (slab.GetBottomElevation(document), slab.GetTopElevation(document));
            if (bottom < top - 0.5 && above >= top - 0.5) return bottom;
        }

        return double.PositiveInfinity;
    }
}
