using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a wall is framed in.</summary>
public enum FramingMaterial
{
    /// <summary>Sawn timber studs and plates.</summary>
    Timber,

    /// <summary>Light-gauge steel: C-studs standing in U-tracks.</summary>
    Steel
}

/// <summary>What a piece of a wall's frame does.</summary>
public enum FramingMemberKind
{
    /// <summary>The sole plate the studs stand on - a bottom track in steel.</summary>
    BottomPlate,

    /// <summary>The head plate over the studs, often doubled - a top track in steel.</summary>
    TopPlate,

    /// <summary>A common stud, at the spacing.</summary>
    Stud,

    /// <summary>A second stud at a corner, for the boards of the other wall to fix to.</summary>
    CornerStud,

    /// <summary>The full-height stud each side of an opening.</summary>
    KingStud,

    /// <summary>The stud each side of an opening, inside the king, carrying the header - a trimmer.</summary>
    JackStud,

    /// <summary>The beam over an opening, carrying what is above it to the jacks - a lintel.</summary>
    Header,

    /// <summary>The flat piece under a window opening.</summary>
    Sill,

    /// <summary>A short stud over a header, or under a sill, at the spacing.</summary>
    CrippleStud,

    /// <summary>A short horizontal piece between studs - a nogging, or blocking.</summary>
    Nogging
}

/// <summary>
/// A section framing comes in: its name as it is ordered, and its size - its thickness along the
/// wall and its depth through it.
/// </summary>
public sealed record FramingSection(string Name, FramingMaterial Material, double Thickness, double Depth)
{
    /// <summary>The sections a wall can be framed in: sawn timber, and light-gauge steel by its universal designator.</summary>
    public static IReadOnlyList<FramingSection> Catalogue { get; } = new[]
    {
        new FramingSection("38 x 89 (2x4)", FramingMaterial.Timber, 38, 89),
        new FramingSection("38 x 140 (2x6)", FramingMaterial.Timber, 38, 140),
        new FramingSection("47 x 75 C16", FramingMaterial.Timber, 47, 75),
        new FramingSection("47 x 100 C24", FramingMaterial.Timber, 47, 100),
        new FramingSection("47 x 150 C24", FramingMaterial.Timber, 47, 150),
        new FramingSection("250S162-33 (64 x 41)", FramingMaterial.Steel, 41, 64),
        new FramingSection("362S162-54 (92 x 41)", FramingMaterial.Steel, 41, 92),
        new FramingSection("600S162-54 (152 x 41)", FramingMaterial.Steel, 41, 152)
    };

    public static FramingSection Named(string? name) => Catalogue.FirstOrDefault(section => section.Name == name) ?? Catalogue[0];

    /// <summary>The section of a material that best fits a depth: the deepest no deeper, or the shallowest.</summary>
    public static FramingSection Fitting(FramingMaterial material, double depth)
    {
        var ofMaterial = Catalogue.Where(section => section.Material == material).OrderBy(section => section.Depth).ToList();
        return ofMaterial.LastOrDefault(section => section.Depth <= depth + 1) ?? ofMaterial[0];
    }
}

/// <summary>One piece of a wall's frame: what it does, and the box it fills - along the wall, and up.</summary>
public sealed record FramingMember(FramingMemberKind Kind, double From, double To, double Bottom, double Top)
{
    /// <summary>Whether it stands upright: studs of every kind.</summary>
    public bool IsUpright => Kind is FramingMemberKind.Stud or FramingMemberKind.CornerStud or FramingMemberKind.KingStud
        or FramingMemberKind.JackStud or FramingMemberKind.CrippleStud;

    /// <summary>How long it is cut, mm.</summary>
    public double Length => IsUpright ? Top - Bottom : To - From;
}

/// <summary>
/// The frame inside a wall (what the framing add-ins do for Revit): its studs at their spacing,
/// on a sole plate and under head plates; each opening between king studs, its header carried on
/// jacks, a sill under a window, cripples above and below; corners with a second stud; noggings
/// between the studs. Laid out from the wall each time, so it follows the wall and its openings.
/// </summary>
public sealed class WallFraming : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.StructuralFraming;

    /// <summary>The wall it frames.</summary>
    public Guid HostId { get; set; }

    public FramingMaterial Material { get; set; } = FramingMaterial.Timber;

    /// <summary>The section the studs and plates are, by name.</summary>
    public string Section { get; set; } = FramingSection.Catalogue[0].Name;

    /// <summary>The studs' spacing, centre to centre, mm.</summary>
    public double Spacing { get; set; } = 600;

    /// <summary>How many head plates: two in a timber bearing wall, one track in steel.</summary>
    public int TopPlates { get; set; } = 2;

    /// <summary>How many rows of noggings between the studs, spaced evenly up them.</summary>
    public int NoggingRows { get; set; } = 1;

    /// <summary>Whether the spacing is set out from the wall's end rather than its start.</summary>
    public bool FromEnd { get; set; }

    public FramingSection SectionSize => FramingSection.Named(Section);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(WallFramingParameters.Material,
            () => EnumText.Humanise(Material),
            value =>
            {
                if (!EnumText.TryParse<FramingMaterial>(value, out var material) || material == Material) return;
                Material = material;
                Section = FramingSection.Fitting(material, SectionSize.Depth).Name;
                TopPlates = material == FramingMaterial.Steel ? 1 : 2;
            },
            EnumText.Choices<FramingMaterial>());
        yield return ParameterValue.BindChoice(WallFramingParameters.Section, () => Section,
            value => { if (FramingSection.Catalogue.Any(s => s.Name == value)) Section = value; },
            FramingSection.Catalogue.Where(s => s.Material == Material).Select(s => s.Name).ToArray());
        yield return ParameterValue.BindValidated(WallFramingParameters.Spacing, () => Spacing, (double value) =>
        {
            if (value < 200 || value > 1200) return false;
            Spacing = value;
            return true;
        });
        yield return ParameterValue.BindValidated(WallFramingParameters.TopPlates, () => TopPlates, (int value) =>
        {
            if (value is < 1 or > 2) return false;
            TopPlates = value;
            return true;
        });
        yield return ParameterValue.BindValidated(WallFramingParameters.NoggingRows, () => NoggingRows, (int value) =>
        {
            if (value is < 0 or > 4) return false;
            NoggingRows = value;
            return true;
        });
        yield return ParameterValue.Bind(WallFramingParameters.FromEnd, () => FromEnd, value => FromEnd = value);
        yield return ParameterValue.ReadOnly(WallFramingParameters.StudCount, () => WallFramings.Layout(document, this).Count(member => member.IsUpright));
        yield return ParameterValue.ReadOnly(WallFramingParameters.TotalLength, () => WallFramings.Layout(document, this).Sum(member => member.Length));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class WallFramingParameters
{
    public static readonly ParameterDefinition Material = new("Framing Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Section = new("Section", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Spacing = new("Stud Spacing", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition TopPlates = new("Top Plates", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition NoggingRows = new("Nogging Rows", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition FromEnd = new("Set Out From End", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition StudCount = new("Studs", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition TotalLength = new("Total Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

/// <summary>Laying out, building and listing the frames of walls.</summary>
public static class WallFramings
{
    /// <summary>The depths a header comes in: 2x6 to 2x12 in timber, 6" to 12" box headers in steel.</summary>
    private static readonly double[] TimberHeaders = { 140, 184, 235, 286 };
    private static readonly double[] SteelHeaders = { 152, 203, 254, 305 };

    public static WallFraming? Of(BimDocument document, Wall wall) =>
        document.Elements.OfType<WallFraming>().FirstOrDefault(framing => framing.HostId == wall.Id);

    public static Wall? Host(BimDocument document, WallFraming framing) => document.Walls.FirstOrDefault(wall => wall.Id == framing.HostId);

    /// <summary>Why a wall cannot be framed, or null: a straight wall of one layered type, with a core to frame and no edited outline.</summary>
    public static string? WhyNot(BimDocument document, Wall wall)
    {
        if (wall.IsCurved) return "Only a straight wall can be framed.";
        if (CurtainLayout.Of(document, wall) is not null) return "A curtain wall is framed by its mullions.";
        if (document.FindType<StackedWallType>(wall.TypeId) is not null) return "A stacked wall is made of other walls: frame a wall of one type.";
        if (document.GetWallType(wall) is not { } type || type.Structure.CoreWidth <= 0) return "That wall has no core to frame.";
        if (WallProfile.Of(document, wall) is not null) return "A wall with an edited outline cannot be framed yet.";
        if (Of(document, wall) is not null) return "That wall is framed already.";
        return null;
    }

    /// <summary>
    /// A frame for a wall, in the material its core suggests - steel for a metal stud partition,
    /// timber otherwise - its section fitted to the core's thickness.
    /// </summary>
    public static WallFraming New(BimDocument document, Wall wall)
    {
        var type = document.GetWallType(wall)!;
        var coreMaterial = type.Structure.CoreStartIndex >= 0 ? document.FindMaterial(type.Structure.Layers[type.Structure.CoreStartIndex].MaterialId) : null;
        var material = coreMaterial?.Name.Contains("Steel", StringComparison.OrdinalIgnoreCase) == true || type.Name.Contains("Metal", StringComparison.OrdinalIgnoreCase)
            ? FramingMaterial.Steel
            : FramingMaterial.Timber;

        return new WallFraming
        {
            HostId = wall.Id, LevelId = wall.LevelId, Material = material,
            Section = FramingSection.Fitting(material, type.Structure.CoreWidth).Name,
            TopPlates = material == FramingMaterial.Steel ? 1 : 2
        };
    }

    /// <summary>How deep a header is over a span: an inch for every foot, at least the smallest made.</summary>
    public static double HeaderDepth(FramingMaterial material, double span)
    {
        var sizes = material == FramingMaterial.Steel ? SteelHeaders : TimberHeaders;
        return sizes.FirstOrDefault(depth => depth >= span / 12) is var found and > 0 ? found : sizes[^1];
    }

    /// <summary>
    /// The frame laid out: plates, studs at the spacing, each opening framed between king studs
    /// with its header on jacks, a sill under a window and cripples above and below it, a second
    /// stud at a corner, and noggings between the studs. Empty for a wall that cannot be framed.
    /// </summary>
    public static IReadOnlyList<FramingMember> Layout(BimDocument document, WallFraming framing)
    {
        if (Host(document, framing) is not { IsCurved: false } wall || document.GetWallType(wall) is not { } type) return Array.Empty<FramingMember>();

        var section = framing.SectionSize;
        var t = section.Thickness;
        var length = wall.Length;
        var height = wall.GetHeight(document);
        var plates = Math.Clamp(framing.TopPlates, 1, 2);
        var (studBottom, studTop) = (t, height - plates * t);
        if (length < 3 * t || studTop - studBottom < 3 * t) return Array.Empty<FramingMember>();

        var members = new List<FramingMember>();
        var openings = WallPaint.Openings(document, wall)
            .Where(o => o.To > o.From && o.From > -1 && o.To < length + 1)
            .OrderBy(o => o.From)
            .ToList();

        // The sole plate, stopping at doors; the head plates the whole length.
        var plateRuns = new List<(double From, double To)> { (0, length) };
        foreach (var (from, to, sill, _) in openings.Where(o => o.Sill <= t))
            plateRuns = plateRuns.SelectMany(run => to <= run.From || from >= run.To ? new[] { run } : new[] { (run.From, from), (to, run.To) })
                .Where(run => run.Item2 - run.Item1 > 1).ToList();
        members.AddRange(plateRuns.Select(run => new FramingMember(FramingMemberKind.BottomPlate, run.From, run.To, 0, t)));
        for (var p = 0; p < plates; p++) members.Add(new FramingMember(FramingMemberKind.TopPlate, 0, length, height - (p + 1) * t, height - p * t));

        // Each opening: kings full height, jacks to the header, the header, a sill under a window.
        var zones = new List<(double From, double To)>();
        foreach (var (from, to, sill, head) in openings)
        {
            var headerDepth = Math.Min(HeaderDepth(framing.Material, to - from + 2 * t), Math.Max(0, studTop - head));
            members.Add(new FramingMember(FramingMemberKind.KingStud, from - 2 * t, from - t, studBottom, studTop));
            members.Add(new FramingMember(FramingMemberKind.KingStud, to + t, to + 2 * t, studBottom, studTop));
            members.Add(new FramingMember(FramingMemberKind.JackStud, from - t, from, studBottom, Math.Min(head, studTop)));
            members.Add(new FramingMember(FramingMemberKind.JackStud, to, to + t, studBottom, Math.Min(head, studTop)));
            if (headerDepth > 1) members.Add(new FramingMember(FramingMemberKind.Header, from - t, to + t, head, head + headerDepth));
            if (sill > t + 1) members.Add(new FramingMember(FramingMemberKind.Sill, from, to, sill - t, sill));
            zones.Add((from - 2 * t, to + 2 * t));
        }

        // The common studs at the spacing, from the chosen end; under and over an opening they are cripples.
        var spacing = Math.Max(framing.Spacing, 2 * t);
        var centres = new List<double>();
        for (var c = 0.0; c <= length - t / 2 + 1e-6; c += spacing) centres.Add(framing.FromEnd ? length - c : c);
        centres.Add(framing.FromEnd ? 0 : length);

        foreach (var centre in centres.Select(c => Math.Clamp(c, t / 2, length - t / 2)).Distinct())
        {
            var (from, to) = (centre - t / 2, centre + t / 2);
            if (members.Any(m => m.Kind == FramingMemberKind.Stud && Math.Abs((m.From + m.To) / 2 - centre) < t)) continue;

            var inside = openings.FirstOrDefault(o => from >= o.From - 1e-6 && to <= o.To + 1e-6);
            if (inside != default)
            {
                var headerTop = members.Where(m => m.Kind == FramingMemberKind.Header && m.From <= from && m.To >= to).Select(m => m.Top).DefaultIfEmpty(inside.Head).Max();
                if (studTop - headerTop > t) members.Add(new FramingMember(FramingMemberKind.CrippleStud, from, to, headerTop, studTop));
                if (inside.Sill - t - studBottom > t) members.Add(new FramingMember(FramingMemberKind.CrippleStud, from, to, studBottom, inside.Sill - t));
                continue;
            }

            if (zones.Any(zone => to > zone.From + 1e-6 && from < zone.To - 1e-6)) continue;
            members.Add(new FramingMember(FramingMemberKind.Stud, from, to, studBottom, studTop));
        }

        // A second stud at a corner, inside the end one.
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        if (startCut.IsJoined) members.Add(new FramingMember(FramingMemberKind.CornerStud, t, 2 * t, studBottom, studTop));
        if (endCut.IsJoined) members.Add(new FramingMember(FramingMemberKind.CornerStud, length - 2 * t, length - t, studBottom, studTop));

        // Noggings between whatever stands at their height, clear of the openings.
        for (var row = 1; row <= framing.NoggingRows; row++)
        {
            var middle = studBottom + (studTop - studBottom) * row / (framing.NoggingRows + 1);
            var standing = members.Where(m => m.IsUpright && m.Bottom <= middle && m.Top >= middle).OrderBy(m => m.From).ToList();
            for (var i = 0; i + 1 < standing.Count; i++)
            {
                var (from, to) = (standing[i].To, standing[i + 1].From);
                if (to - from <= 1) continue;
                if (openings.Any(o => from < o.To && to > o.From && middle > o.Sill && middle < o.Head)) continue;
                members.Add(new FramingMember(FramingMemberKind.Nogging, from, to, middle - t / 2, middle + t / 2));
            }
        }

        return members;
    }

    /// <summary>Where a frame lies through the wall: centred on its core, as deep as its section, from the wall's middle toward the outside.</summary>
    private static (double Outer, double Inner) Across(WallType type, FramingSection section)
    {
        var structure = type.Structure;
        var coreMiddle = structure.TotalWidth / 2 - structure.ExteriorWidth - structure.CoreWidth / 2;
        return (coreMiddle + section.Depth / 2, coreMiddle - section.Depth / 2);
    }

    /// <summary>
    /// The frame as the 3D view draws it: timber as sawn boxes; steel studs as C-sections and the
    /// plates as tracks, the headers and sills as boxes.
    /// </summary>
    public static Mesh3D? Mesh(BimDocument document, WallFraming framing)
    {
        if (Host(document, framing) is not { } wall || document.GetWallType(wall) is not { } type) return null;

        var members = Layout(document, framing);
        if (members.Count == 0) return null;

        var section = framing.SectionSize;
        var steel = framing.Material == FramingMaterial.Steel;
        var colour = steel ? new ColourRgb(0xA9, 0xAF, 0xB4) : new ColourRgb(0xD9, 0xB8, 0x86);
        var mesh = new Mesh3D(framing.Id, wall.LevelId, MeshKind.Framing, colour, steel ? $"Steel Framing {section.Name}" : $"Timber Framing {section.Name}");
        var structure = type.Structure;
        var bottom = wall.GetBaseElevation(document);
        var (outer, inner) = Across(type, section);

        Point2D At(double along, double across) => wall.PointAt(structure, along, across);

        foreach (var member in members)
        {
            if (steel && member.IsUpright)
            {
                // A C-stud standing: its web across the wall at its middle, its flanges along it.
                var middle = (member.From + member.To) / 2;
                var c = CSection(section.Thickness, section.Depth, 1.5, 12)
                    .Select(p => At(middle + p.X, (outer + inner) / 2 + p.Y)).ToList();
                mesh.AddExtrusion(c, bottom + member.Bottom, bottom + member.Top);
                continue;
            }

            var box = new[] { At(member.From, outer), At(member.To, outer), At(member.To, inner), At(member.From, inner) };
            mesh.AddExtrusion(box, bottom + member.Bottom, bottom + member.Top);
        }

        return mesh;
    }

    /// <summary>A lipped C, in plan: its flanges this wide along the wall, its web this deep through it, of steel this thick, lips this long.</summary>
    private static IReadOnlyList<Point2D> CSection(double flange, double web, double gauge, double lip)
    {
        var (w, d) = (flange / 2, web / 2);
        return new[]
        {
            new Point2D(-w, d), new Point2D(w, d), new Point2D(w, d - lip), new Point2D(w - gauge, d - lip), new Point2D(w - gauge, d - gauge),
            new Point2D(-w + gauge, d - gauge), new Point2D(-w + gauge, -d + gauge), new Point2D(w - gauge, -d + gauge),
            new Point2D(w - gauge, -d + lip), new Point2D(w, -d + lip), new Point2D(w, -d), new Point2D(-w, -d)
        };
    }

    /// <summary>The studs where a plan cuts the wall, at a height above its base: their boxes in plan.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> PlanAt(BimDocument document, WallFraming framing, double height)
    {
        if (Host(document, framing) is not { } wall || document.GetWallType(wall) is not { } type) yield break;

        var (outer, inner) = Across(type, framing.SectionSize);
        foreach (var member in Layout(document, framing).Where(m => m.IsUpright && m.Bottom <= height && m.Top >= height))
            yield return new[]
            {
                wall.PointAt(type.Structure, member.From, outer), wall.PointAt(type.Structure, member.To, outer),
                wall.PointAt(type.Structure, member.To, inner), wall.PointAt(type.Structure, member.From, inner)
            };
    }

    /// <summary>
    /// The cut list: every member of the frames, by what it is, its section and the length it is
    /// cut to, with how many - and the walls they are for.
    /// </summary>
    public static IReadOnlyList<CutListRow> CutList(BimDocument document, IEnumerable<WallFraming> framings)
    {
        var rows = new List<(string Wall, string Kind, string Section, double Length)>();
        foreach (var framing in framings)
        {
            var wall = Host(document, framing);
            var name = string.IsNullOrWhiteSpace(wall?.Mark) ? string.IsNullOrWhiteSpace(framing.Mark) ? "Wall" : framing.Mark : wall!.Mark;
            var section = framing.SectionSize;
            foreach (var member in Layout(document, framing))
            {
                // A header is two plies on edge; everything else is the stud section.
                var size = member.Kind == FramingMemberKind.Header
                    ? $"2 x {section.Thickness:0} x {member.Top - member.Bottom:0}"
                    : section.Name;
                rows.Add((name, EnumText.Humanise(member.Kind), size, Math.Round(member.Length)));
            }
        }

        return rows
            .GroupBy(row => (row.Kind, row.Section, row.Length))
            .Select(group => new CutListRow(
                string.Join(", ", group.Select(row => row.Wall).Distinct()), group.Key.Kind, group.Key.Section, group.Key.Length, group.Count()))
            .OrderBy(row => row.Section).ThenBy(row => row.Member).ThenByDescending(row => row.Length)
            .ToList();
    }
}

/// <summary>One line of a cut list: members of one kind, section and length, how many, and for which walls.</summary>
public sealed record CutListRow(string Walls, string Member, string Section, double Length, int Quantity)
{
    public double TotalLength => Length * Quantity;
}
