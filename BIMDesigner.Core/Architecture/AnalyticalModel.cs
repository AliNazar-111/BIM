using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a beam's end is held by what it meets: free to turn, as a simple connection is, or held square to it.</summary>
public enum MemberRelease
{
    Pinned,
    Fixed
}

/// <summary>How a foundation holds what stands on it: in place but free to turn, or held from turning as well.</summary>
public enum SupportFixity
{
    Pinned,
    Fixed
}

/// <summary>How a floor goes into the analytical model: as what it is made of says, as a plate, or spanning one way onto what carries it.</summary>
public enum FloorAnalysis
{
    Automatic,
    Plate,
    OneWay
}

/// <summary>What a structural material is, as an analysis program sorts them.</summary>
public enum AnalyticalMaterialKind
{
    Concrete,
    Steel,
    Timber,
    Masonry,
    Other
}

/// <summary>What a member is in the frame.</summary>
public enum AnalyticalMemberKind
{
    Column,
    Beam,
    Joist,
    GroundBeam
}

/// <summary>What a panel is.</summary>
public enum AnalyticalPanelKind
{
    Wall,
    Floor,
    Roof,

    /// <summary>A raft, on the ground.</summary>
    Raft,

    /// <summary>A concrete floor cast on the ground.</summary>
    GroundSlab,

    /// <summary>A timber floor or a roof holding the wall tops it is strapped to: stiff in its plane, nothing else. In the analysis only.</summary>
    Diaphragm
}

/// <summary>A direction in the building, its length 1.</summary>
public readonly record struct Direction3D(double X, double Y, double Z);

/// <summary>
/// A structural material as an analysis program takes it: its grade, its density, kg/m³, its
/// modulus and shear modulus, MPa, Poisson's ratio and its expansion, a kelvin - NaN where none is
/// given - and how they were come by.
/// </summary>
public sealed record AnalyticalMaterial(string Name, AnalyticalMaterialKind Kind, string Quality, double Density, double Modulus, double ShearModulus, double Poisson, double Expansion, string Said);

/// <summary>
/// A member's section as an analysis program takes it: its shape - Rectangle (H; B), I Section
/// (H; Bh; Bs; ts; th; s), Circle (D), Pipe (D; t), Tube (H; B; s; R; r1) - its sizes, mm, and its
/// area, mm², and second moments, mm⁴, about its strong (y) and weak (z) axes.
/// </summary>
public sealed record AnalyticalSection(string Name, AnalyticalMaterial Material, string Shape, IReadOnlyList<double> Dimensions, double Area, double Iy, double Iz);

/// <summary>A point the analytical model's members and panels meet at, or are held at.</summary>
public sealed class AnalyticalNode
{
    internal AnalyticalNode(Point3D at) => At = at;

    public string Name { get; internal set; } = string.Empty;

    public Point3D At { get; internal set; }
}

/// <summary>
/// A column, beam or ground beam as a line: from its first node to its last through those along
/// it where something else meets it, its section, how its ends are held, and where its line is on
/// its section - a beam is drawn by its top, so that is what its line is.
/// </summary>
public sealed class AnalyticalMember
{
    internal AnalyticalMember(Element element, AnalyticalMemberKind kind, AnalyticalSection section, string systemLine)
    {
        Element = element;
        Kind = kind;
        Section = section;
        SystemLine = systemLine;
    }

    public string Name { get; internal set; } = string.Empty;

    /// <summary>What it is in the model: the column or beam - for a joist, its joist layout.</summary>
    public Element Element { get; }

    public AnalyticalMemberKind Kind { get; }

    public AnalyticalSection Section { get; }

    /// <summary>Start to end, with the nodes along it where something meets it.</summary>
    public List<AnalyticalNode> Nodes { get; } = new();

    public AnalyticalNode Start => Nodes[0];

    public AnalyticalNode End => Nodes[^1];

    public MemberRelease StartRelease { get; internal set; } = MemberRelease.Fixed;

    public MemberRelease EndRelease { get; internal set; } = MemberRelease.Fixed;

    /// <summary>Where its line is on its section: Centre, Top or Bottom.</summary>
    public string SystemLine { get; }

    /// <summary>The way its section's y axis lies - along a column's width, across a beam.</summary>
    public Direction3D LocalY { get; internal set; }

    /// <summary>Its length, mm.</summary>
    public double Length => AnalyticalModel.Distance(Start.At, End.At);
}

/// <summary>
/// A wall, floor, roof or raft as a flat plate: its outline as nodes - with those along its edges
/// where something else meets them - the nodes inside it where something stands on it or is joined
/// to it, its openings, how thick it is and what of, where its plane is on its thickness, and for
/// one on the ground, the stiffness of the ground under it.
/// </summary>
public sealed class AnalyticalPanel
{
    internal AnalyticalPanel(Element element, AnalyticalPanelKind kind, AnalyticalMaterial material, double thickness, string systemPlane, Point3D origin, Direction3D u, Direction3D v)
    {
        Element = element;
        Kind = kind;
        Material = material;
        Thickness = thickness;
        SystemPlane = systemPlane;
        Origin = origin;
        U = u;
        V = v;
        Normal = AnalyticalModel.Cross(u, v);
    }

    public string Name { get; internal set; } = string.Empty;

    public Element Element { get; }

    public AnalyticalPanelKind Kind { get; }

    public AnalyticalMaterial Material { get; }

    /// <summary>How thick it is as a plate, mm: its structural layers.</summary>
    public double Thickness { get; }

    /// <summary>Where its plane is on its thickness: Centre, Top or Bottom.</summary>
    public string SystemPlane { get; }

    public List<AnalyticalNode> Outline { get; } = new();

    public List<AnalyticalNode> Inside { get; } = new();

    /// <summary>The holes in it, that a mesh has to go round.</summary>
    public List<IReadOnlyList<Point3D>> Openings { get; } = new();

    /// <summary>
    /// Every door and window cut from a wall, each as its four corners. A door standing on the foot of a wall is no hole in it but a notch in its outline, so it is not among
    /// <see cref="Openings"/>: whatever an opening wants a lintel or a header for, or takes out of a wall's length, reads these.
    /// </summary>
    public List<IReadOnlyList<Point3D>> Cuts { get; } = new();

    /// <summary>The ground's stiffness under it, MN/m³; none for a panel not on the ground.</summary>
    public double? Subgrade { get; internal set; }

    /// <summary>What it weighs, kN/m², all its layers: what the analysis program's self weight is to be made up to.</summary>
    public double Weight { get; internal set; }

    /// <summary>A corner of its plane and the two ways across it: what its outline is drawn in.</summary>
    public Point3D Origin { get; }

    public Direction3D U { get; }

    public Direction3D V { get; }

    public Direction3D Normal { get; }

    /// <summary>A point in its plane's own terms.</summary>
    public Point2D Flat(Point3D point) => new(AnalyticalModel.Dot(AnalyticalModel.Minus(point, Origin), U), AnalyticalModel.Dot(AnalyticalModel.Minus(point, Origin), V));

    /// <summary>Its outline in its plane's own terms.</summary>
    public IReadOnlyList<Point2D> FlatOutline => Outline.Select(node => Flat(node.At)).ToList();

    /// <summary>Its area, mm², less its openings.</summary>
    public double Area => Polygon2D.Area(FlatOutline) - Openings.Sum(opening => Polygon2D.Area(opening.Select(Flat).ToList()));

    /// <summary>What the analysis program takes it to weigh, kN/m²: its thickness at its material's density.</summary>
    public double SelfWeight => Thickness / 1000 * Material.Density * 9.81 / 1000;

    /// <summary>Its edges, each from a node of its outline to the next.</summary>
    public IEnumerable<(AnalyticalNode From, AnalyticalNode To)> Edges =>
        Outline.Select((node, i) => (node, Outline[(i + 1) % Outline.Count]));

    /// <summary>Which edge, counted from 0, runs from one node to another; -1 where none does.</summary>
    public int EdgeIndex(AnalyticalNode from, AnalyticalNode to)
    {
        for (var i = 0; i < Outline.Count; i++)
            if (ReferenceEquals(Outline[i], from) && ReferenceEquals(Outline[(i + 1) % Outline.Count], to)) return i;
        return -1;
    }
}

/// <summary>
/// Where the analytical model is held: at a node - a column on its pad, a ground beam on a pile -
/// or along a panel's edge, a wall on its strip footing; pinned or fixed.
/// </summary>
public sealed class AnalyticalSupport
{
    internal AnalyticalSupport(Element foundation, SupportFixity fixity)
    {
        Foundation = foundation;
        Fixity = fixity;
    }

    public string Name { get; internal set; } = string.Empty;

    public Element Foundation { get; }

    public SupportFixity Fixity { get; }

    /// <summary>The node it holds, for one at a point.</summary>
    public AnalyticalNode? Node { get; internal init; }

    /// <summary>The panel whose edge it runs along, and the edge's ends, for one along a line.</summary>
    public AnalyticalPanel? Panel { get; internal init; }

    public AnalyticalNode? From { get; internal init; }

    public AnalyticalNode? To { get; internal init; }

    public bool IsLine => Panel is not null;
}

/// <summary>
/// A wall that bears nothing but stiffens one it tees into, as BS EN 1996-1-1 5.5.1.2(4) lets it: masonry or concrete, at least a
/// fifth of the clear height long and 0.3 of the wall's effective thickness thick, standing on the ground or a concrete
/// floor. Not a plate of the structure and not handed on: in the analysis a line of support up the wall it meets,
/// as stiff as it is as a cantilever from its foot.
/// </summary>
public sealed record AnalyticalStiffener(Element Cross, Element Host, Point2D At, Direction3D Way, double Bottom, double Top, double Stiffness);

/// <summary>Something the analytical model is not joined up at, and what it is about.</summary>
public sealed record AnalyticalProblem(string Said, IReadOnlyList<Element> Elements);

/// <summary>
/// The structure as an analysis program takes it, worked out from the model as it stands - not
/// kept, so it is never out of step with it.
///
/// Each structural column is a line up its axis from its base to its top; each beam a line along
/// the line it is drawn on - its top, as it is hung from its level - and each joist of a joist
/// layout the same; each ground beam a line along the foot of the wall it carries. Each wall whose
/// Structural Usage is not Non-bearing is a plate in the middle of its structural core, from its
/// base to its top or the outline it is shaped to - a gable to its roof - its doors and windows
/// cut out of it, as thick as its structural layers. Each concrete floor - or one set to - is a
/// plate at its top, its holes and the shafts through it cut out; a flat concrete roof a plate at
/// its underside; a raft, or a concrete floor on the ground, a plate on the ground's stiffness. A
/// timber floor and a pitched roof are not plates: they span onto what carries them, and go in as
/// the loads they put on it.
///
/// Members are joined where they meet, as an analysis program needs them: a beam's end taken to
/// the axis of the column it lands on, or the line of the beam it frames into, or the plane of the
/// wall it bears on; a column's top up to the beam or floor over it, its foot down to the floor,
/// beam or wall under it; walls meeting at a corner taken to where their planes meet, and one
/// teed into another taken to its plane, the other split there; a floor's edges taken to the
/// planes of the walls it bears on. A node that falls on another's edge goes into its outline;
/// one inside it, into it.
///
/// Held where the foundations are: a column on its pad and a ground beam on its piles at a point,
/// a wall on its strip footing along the foot of its plate - pinned unless set fixed - and a raft
/// or a slab on the ground by the ground's stiffness, a modulus of subgrade reaction by Bowles'
/// ranges for the kind of ground (Foundation Analysis and Design, Table 9-1): rock 300 MN/m³,
/// gravel 100, sand 50, stiff clay 40, firm clay 25, loose sand 12, soft clay 12 and very soft 6.
/// </summary>
public sealed class AnalyticalModel
{
    /// <summary>Points this close together are one node, mm.</summary>
    public const double NodeTolerance = 5;

    private AnalyticalModel()
    {
    }

    public List<AnalyticalNode> Nodes { get; } = new();

    public List<AnalyticalMember> Members { get; } = new();

    public List<AnalyticalPanel> Panels { get; } = new();

    public List<AnalyticalSupport> Supports { get; } = new();

    public List<AnalyticalProblem> Problems { get; } = new();

    public IEnumerable<AnalyticalMaterial> Materials =>
        Members.Select(member => member.Section.Material).Concat(Panels.Select(panel => panel.Material)).Distinct();

    public IEnumerable<AnalyticalSection> Sections => Members.Select(member => member.Section).Distinct();

    /// <summary>Whether it is held at all: a support, or a plate on the ground.</summary>
    /// <summary>Timber floors and roofs as diaphragms: not plates of the structure, and not handed on, but in its analysis.</summary>
    public List<AnalyticalPanel> Diaphragms { get; } = new();

    /// <summary>Cross walls that bear nothing but stiffen the walls they meet: in its analysis, not handed on.</summary>
    public List<AnalyticalStiffener> Stiffeners { get; } = new();

    public bool IsHeld => Supports.Count > 0 || Panels.Any(panel => panel.Subgrade is not null);

    /// <summary>The members and panels an element of the model is in the analytical model as.</summary>
    public IEnumerable<object> Of(Element element) =>
        Members.Where(member => member.Element.Id == element.Id).Cast<object>()
            .Concat(Panels.Where(panel => panel.Element.Id == element.Id))
            .Concat(Supports.Where(support => support.Foundation.Id == element.Id));

    /// <summary>The analytical model as the model now stands.</summary>
    public static AnalyticalModel Of(BimDocument document) => new Builder(document).Build();

    // ---- what an element is, said ------------------------------------------------

    /// <summary>What an element is in the analytical model, as its Properties say it.</summary>
    public static string Said(BimDocument document, Element element)
    {
        var model = Of(document);
        var members = model.Members.Where(member => member.Element.Id == element.Id).ToList();
        var panels = model.Panels.Where(panel => panel.Element.Id == element.Id).ToList();
        var supports = model.Supports.Where(support => support.Foundation.Id == element.Id).ToList();
        var problems = model.Problems.Where(problem => problem.Elements.Any(each => each.Id == element.Id)).Select(problem => problem.Said);
        var said = new List<string>();

        if (members.Count == 1 && members[0] is var member)
            said.Add($"{member.Name}: {Kind(member.Kind)} {member.Start.Name} to {member.End.Name}, {member.Section.Name} of {member.Section.Material.Quality}{(member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist ? $", {Release(member.StartRelease)} at its start and {Release(member.EndRelease)} at its end" : "")}.");
        else if (members.Count > 1)
            said.Add($"{members.Count} {Kind(members[0].Kind)}s, {members[0].Name} to {members[^1].Name}, {members[0].Section.Name} of {members[0].Section.Material.Quality}.");

        foreach (var panel in panels)
            said.Add($"{panel.Name}: a plate {Units.Bare(panel.Thickness)} thick of {panel.Material.Quality} at its {panel.SystemPlane.ToLowerInvariant()}, on {panel.Outline.Count} nodes{(panel.Openings.Count > 0 ? $", {panel.Openings.Count} opening{(panel.Openings.Count == 1 ? "" : "s")} cut out" : "")}{(panel.Subgrade is { } k ? $", on ground of {k:0} MN/m³" : "")}.");

        foreach (var support in supports)
            said.Add(support.IsLine
                ? $"{support.Name}: holds {support.Panel!.Name} along its foot, {support.Fixity.ToString().ToLowerInvariant()}."
                : $"{support.Name}: holds {support.Node!.Name}, {support.Fixity.ToString().ToLowerInvariant()}.");

        if (said.Count == 0)
            said.Add(element switch
            {
                Wall { StructuralUsage: StructuralUsage.NonBearing } => "Not in it: its Structural Usage is Non-bearing.",
                Floor or Roof => "Not a plate: it spans onto what carries it, and goes in as the loads it puts on it.",
                Column { Structural: false } => "Not in it: an architectural column.",
                _ => "Not in it."
            });

        return string.Join(" ", said.Concat(problems));
    }

    /// <summary>Whether a floor or roof is a plate: one set to be, or - left to its build-up - a floor or flat roof whose structure is concrete.</summary>
    public static bool IsPlate(BimDocument document, Slab slab)
    {
        if (slab is Floor { AnalyseAs: FloorAnalysis.Plate }) return true;
        if (slab is Floor { AnalyseAs: FloorAnalysis.OneWay } or Ceiling) return false;
        if (slab is Roof roof && (roof.IsExtrusion || !roof.Surface(document).IsFlat)) return false;
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return false;
        var core = type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).OrderByDescending(layer => layer.Thickness).FirstOrDefault();
        return core is not null && MaterialOf(document, document.FindMaterial(core.MaterialId)).Kind == AnalyticalMaterialKind.Concrete;
    }

    private static string Kind(AnalyticalMemberKind kind) => kind switch
    {
        AnalyticalMemberKind.Column => "a column",
        AnalyticalMemberKind.Joist => "a joist",
        AnalyticalMemberKind.GroundBeam => "a ground beam",
        _ => "a beam"
    };

    private static string Release(MemberRelease release) => release == MemberRelease.Pinned ? "pinned" : "fixed";

    // ---- materials and sections -------------------------------------------------

    /// <summary>
    /// The structural grade a project's material is taken at, by what it is called - steel, glulam,
    /// timber, block or brick, concrete - or failing that by how dense it is: in England S355
    /// (BS EN 1993-1-1 3.2.6: E 210 GPa, G 81 GPa), C30/37 (BS EN 1992-1-1 Table 3.1: Ecm 33 GPa),
    /// C24 (BS EN 338 Table 1: E0,mean 11 GPa, Gmean 0.69 GPa), GL24h (BS EN 14080 Table 5:
    /// E0,mean 11.5 GPa, Gmean 0.65 GPa) and masonry at E = 1000 fk (BS EN 1996-1-1 3.7.2), its fk from
    /// the unit and mortar set on it; in the US A992 (AISC 360-16: E 29,000 ksi, G 11,200 ksi), 4,000 psi
    /// concrete (ACI 318-19 19.2.2.1: Ec = 57,000√f'c), Douglas fir-larch No. 2 (NDS Supplement
    /// Table 4A: E 1,600,000 psi), 24F-1.8E glulam and masonry at Em = 900 f'm, 700 f'm for brick
    /// (TMS 402-16 4.2.2), with f'm taken as 1,500 psi. Its density is its own.
    /// </summary>
    public static AnalyticalMaterial MaterialOf(BimDocument document, Material? material, AnalyticalMaterialKind? kind = null, bool glulam = false)
    {
        var name = material?.Name ?? string.Empty;
        var lower = name.ToLowerInvariant();
        var density = material?.Density ?? 0;
        var sorted = kind ?? (lower.Contains("steel") ? AnalyticalMaterialKind.Steel
            : lower.Contains("glulam") ? AnalyticalMaterialKind.Timber
            : lower.Contains("timber") || lower.Contains("wood") || lower.Contains("stud") || lower.Contains("joist") || lower.Contains("plywood") || lower.Contains("osb") || lower.Contains("clt") ? AnalyticalMaterialKind.Timber
            : lower.Contains("block") || lower.Contains("brick") || lower.Contains("masonry") || lower.Contains("stone") ? AnalyticalMaterialKind.Masonry
            : lower.Contains("concrete") ? AnalyticalMaterialKind.Concrete
            : density >= 6000 ? AnalyticalMaterialKind.Steel
            : density >= 2200 ? AnalyticalMaterialKind.Concrete
            : density >= 1000 ? AnalyticalMaterialKind.Masonry
            : density > 0 ? AnalyticalMaterialKind.Timber
            : AnalyticalMaterialKind.Other);
        glulam |= lower.Contains("glulam");
        var brick = lower.Contains("brick");
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        const double psi = 0.00689476;
        var masonry = MasonryDesign.Characteristic(material is null ? MasonryDesign.UnitOf(string.Empty) : MasonryDesign.UnitOf(material));

        var (quality, e, g, nu, alpha, usual, said) = (sorted, us) switch
        {
            (AnalyticalMaterialKind.Steel, false) => ("S355", 210000.0, 81000.0, 0.3, 12e-6, 7850.0, "BS EN 1993-1-1 3.2.6"),
            (AnalyticalMaterialKind.Steel, true) => ("A992", 29000000 * psi, 11200000 * psi, 0.3, 11.7e-6, 7850.0, "AISC 360-16"),
            (AnalyticalMaterialKind.Concrete, false) => ("C30/37", 33000.0, double.NaN, 0.2, 10e-6, 2500.0, "BS EN 1992-1-1 Table 3.1"),
            (AnalyticalMaterialKind.Concrete, true) => ("4000 psi", 57000 * Math.Sqrt(4000) * psi, double.NaN, 0.2, 9.9e-6, 2400.0, "ACI 318-19 19.2.2.1"),
            (AnalyticalMaterialKind.Timber, false) when glulam => ("GL24h", 11500.0, 650.0, double.NaN, double.NaN, 420.0, "BS EN 14080 Table 5"),
            (AnalyticalMaterialKind.Timber, false) => ("C24", 11000.0, 690.0, double.NaN, double.NaN, 420.0, "BS EN 338 Table 1"),
            (AnalyticalMaterialKind.Timber, true) when glulam => ("24F-1.8E", 1800000 * psi, double.NaN, double.NaN, double.NaN, 500.0, "NDS Supplement Table 5A"),
            (AnalyticalMaterialKind.Timber, true) => ("DF-L No. 2", 1600000 * psi, double.NaN, double.NaN, double.NaN, 500.0, "NDS Supplement Table 4A"),
            (AnalyticalMaterialKind.Masonry, false) => ($"Masonry fk {masonry:0.0}", 1000 * masonry, double.NaN, double.NaN, double.NaN, 2000.0, "BS EN 1996-1-1 3.7.2, E = 1000 fk, KE by the UK National Annex"),
            (AnalyticalMaterialKind.Masonry, true) => ($"Masonry f'm 1500", (brick ? 700 : 900) * 1500 * psi, double.NaN, double.NaN, double.NaN, 2000.0, $"TMS 402-16 4.2.2, f'm taken as 1,500 psi"),
            _ => ("Other", 10000.0, double.NaN, double.NaN, double.NaN, 1000.0, "taken as 10 GPa")
        };

        var used = density > 0 ? density : usual;
        return new AnalyticalMaterial(name.Length > 0 ? name : quality, sorted, quality, used, Math.Round(e, 0), Math.Round(g, 0), nu, alpha, said);
    }

    /// <summary>Whether a wall's structure is concrete, its thickest structural layer's.</summary>
    public static bool IsConcreteWall(BimDocument document, Wall wall)
    {
        var core = document.GetWallType(wall)?.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).OrderByDescending(layer => layer.Thickness).FirstOrDefault();
        return core is not null && MaterialOf(document, document.FindMaterial(core.MaterialId)).Kind == AnalyticalMaterialKind.Concrete;
    }

    /// <summary>A beam material's grade.</summary>
    public static AnalyticalMaterial MaterialOf(BimDocument document, BeamMaterial material, Material? own) => MaterialOf(document, own ?? new Material(material switch
    {
        BeamMaterial.Steel => "Steel",
        BeamMaterial.Concrete => "Concrete",
        BeamMaterial.Glulam => "Glulam",
        _ => "Timber"
    }) { Density = BeamType.Density(material) }, material switch
    {
        BeamMaterial.Steel => AnalyticalMaterialKind.Steel,
        BeamMaterial.Concrete => AnalyticalMaterialKind.Concrete,
        _ => AnalyticalMaterialKind.Timber
    }, material == BeamMaterial.Glulam);

    /// <summary>A beam type's section.</summary>
    public static AnalyticalSection SectionOf(BeamType type, AnalyticalMaterial material) =>
        type.Section == BeamSection.IBeam && type.FlangeThickness > 0 && type.WebThickness > 0
            ? ISection(type.Name, material, type.Depth, type.Width, type.FlangeThickness, type.WebThickness)
            : Rectangle(type.Name, material, type.Depth, type.Width);

    /// <summary>A structural column type's section: a rectangle, an I, a round, a pipe or a tube - any other shape as the rectangle round it.</summary>
    public static AnalyticalSection SectionOf(ColumnType type, AnalyticalMaterial material) => type.Shape switch
    {
        ColumnShape.IBeam when type.FlangeThickness > 0 && type.WebThickness > 0 => ISection(type.Name, material, type.Depth, type.Width, type.FlangeThickness, type.WebThickness),
        ColumnShape.Round => new AnalyticalSection(type.Name, material, "Circle", new[] { type.Width }, Math.PI * type.Width * type.Width / 4, Math.PI * Math.Pow(type.Width, 4) / 64, Math.PI * Math.Pow(type.Width, 4) / 64),
        ColumnShape.HollowRound when type.WallThickness > 0 => Pipe(type.Name, material, type.Width, type.WallThickness),
        ColumnShape.HollowRectangular when type.WallThickness > 0 => Tube(type.Name, material, type.Depth, type.Width, type.WallThickness),
        _ => Rectangle(type.Name, material, type.Depth, type.Width)
    };

    public static AnalyticalSection Rectangle(string name, AnalyticalMaterial material, double depth, double width) =>
        new(name, material, "Rectangle", new[] { depth, width }, depth * width, width * Math.Pow(depth, 3) / 12, depth * Math.Pow(width, 3) / 12);

    private static AnalyticalSection ISection(string name, AnalyticalMaterial material, double depth, double width, double flange, double web)
    {
        var inner = Math.Max(0, depth - 2 * flange);
        return new AnalyticalSection(name, material, "I Section", new[] { depth, width, width, flange, flange, web },
            2 * width * flange + inner * web,
            (width * Math.Pow(depth, 3) - (width - web) * Math.Pow(inner, 3)) / 12,
            (2 * flange * Math.Pow(width, 3) + inner * Math.Pow(web, 3)) / 12);
    }

    private static AnalyticalSection Pipe(string name, AnalyticalMaterial material, double diameter, double wall)
    {
        var inner = Math.Max(0, diameter - 2 * wall);
        var i = Math.PI * (Math.Pow(diameter, 4) - Math.Pow(inner, 4)) / 64;
        return new AnalyticalSection(name, material, "Pipe", new[] { diameter, wall }, Math.PI * (diameter * diameter - inner * inner) / 4, i, i);
    }

    /// <summary>A hollow rectangle, its corners as a hot-finished one's: 1.5 times its wall outside, once inside (BS EN 10210-2).</summary>
    private static AnalyticalSection Tube(string name, AnalyticalMaterial material, double depth, double width, double wall)
    {
        var (h, b) = (Math.Max(0, depth - 2 * wall), Math.Max(0, width - 2 * wall));
        return new AnalyticalSection(name, material, "Tube", new[] { depth, width, wall, 1.5 * wall, wall }, depth * width - h * b,
            (width * Math.Pow(depth, 3) - b * Math.Pow(h, 3)) / 12, (depth * Math.Pow(width, 3) - h * Math.Pow(b, 3)) / 12);
    }

    /// <summary>The ground's stiffness, a modulus of subgrade reaction, MN/m³ - the middle of Bowles' range for its kind.</summary>
    public static double Subgrade(GroundType ground) => ground switch
    {
        GroundType.Rock => 300,
        GroundType.Gravel => 100,
        GroundType.Sand => 50,
        GroundType.StiffClay => 40,
        GroundType.FirmClay => 25,
        GroundType.LooseSand => 12,
        GroundType.SoftClay => 12,
        _ => 6
    };

    // ---- arithmetic in space ----------------------------------------------------

    internal static Direction3D Minus(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    internal static double Dot(Direction3D a, Direction3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    internal static Direction3D Cross(Direction3D a, Direction3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    internal static Point3D Plus(Point3D a, Direction3D d, double by) => new(a.X + d.X * by, a.Y + d.Y * by, a.Z + d.Z * by);

    public static double Distance(Point3D a, Point3D b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>How far a point is from a segment, and how far along it its foot is, 0 to 1.</summary>
    internal static (double Off, double Along) FromSegment(Point3D point, Point3D a, Point3D b)
    {
        var ab = Minus(b, a);
        var length = Dot(ab, ab);
        if (length < 1e-9) return (Distance(point, a), 0);
        var t = Dot(Minus(point, a), ab) / length;
        var foot = Plus(a, ab, Math.Clamp(t, 0, 1));
        return (Distance(point, foot), t);
    }

    // ---- building it ------------------------------------------------------------

    /// <summary>One wall as it goes in: its plane's line in plan, its height, and its outline along and up it.</summary>
    private sealed class WallFrame
    {
        public required Wall Wall { get; init; }
        public required Point2D Origin { get; set; }
        public required Vector2D Way { get; set; }
        public required double Length { get; init; }
        public required double Base { get; init; }
        public required double Top { get; init; }
        public required double Width { get; init; }
        public required double Thickness { get; init; }
        public required AnalyticalMaterial Material { get; init; }
        public required double Weight { get; init; }
        public IReadOnlyList<Point2D>? Profile { get; init; }

        /// <summary>Where its ends are taken to, along its line from where its plane starts.</summary>
        public double From { get; set; }

        public double To { get; set; }

        /// <summary>Where another wall tees into it, along its line.</summary>
        public List<double> Splits { get; } = new();

        public Point2D At(double along) => Origin + Way * along;

        /// <summary>Its top at a point along it.</summary>
        public double TopAt(double along)
        {
            if (Profile is not { Count: >= 3 } profile) return Top;
            var heights = WallProfile.HeightsAt(profile, Math.Clamp(along, 0, Length));
            return heights.Count == 0 ? Top : Base + heights.Max(span => span.Top);
        }

        public double Along(Point2D point) => (point - Origin).Dot(Way);

        public double Off(Point2D point) => Math.Abs((point - Origin).Cross(Way));
    }

    private sealed class Builder
    {
        private readonly BimDocument _document;
        private readonly AnalyticalModel _model = new();
        private readonly Dictionary<(long, long, long), List<AnalyticalNode>> _cells = new();
        private readonly List<WallFrame> _walls = new();
        private readonly Dictionary<Guid, AnalyticalMember> _columns = new();
        private readonly Dictionary<Guid, AnalyticalMember> _groundBeams = new();

        /// <summary>The members to name in the end, with the kind of name each takes.</summary>
        private readonly List<(AnalyticalMember Member, string Prefix)> _named = new();

        public Builder(BimDocument document) => _document = document;

        public AnalyticalModel Build()
        {
            WallFrames();
            Columns();
            Beams();
            Plates();
            Stiffeners();
            WallPanels();
            Diaphragms();
            GroundBeams();
            Connect();
            Supports();
            Check();
            Name();
            return _model;
        }

        private AnalyticalNode Node(Point3D at)
        {
            var key = Cell(at);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue((key.Item1 + dx, key.Item2 + dy, key.Item3 + dz), out var near))
                    foreach (var node in near)
                        if (Distance(node.At, at) <= NodeTolerance) return node;

            var made = new AnalyticalNode(at);
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<AnalyticalNode>();
            list.Add(made);
            _model.Nodes.Add(made);
            return made;
        }

        private static (long, long, long) Cell(Point3D at) =>
            ((long)Math.Floor(at.X / 20), (long)Math.Floor(at.Y / 20), (long)Math.Floor(at.Z / 20));

        // ---- walls -----------------------------------------------------------

        private void WallFrames()
        {
            foreach (var wall in _document.Walls)
            {
                if (wall.StructuralUsage == StructuralUsage.NonBearing || _document.IsCurtainWall(wall) || wall.Length < 1) continue;
                if (_document.FindType<StackedWallType>(wall.TypeId) is not null)
                {
                    _model.Problems.Add(new AnalyticalProblem("A bearing wall of stacked types is not in the analytical model yet: give it one type, or model its tiers as walls.", new Element[] { wall }));
                    continue;
                }

                if (_document.GetWallType(wall) is not { } type) continue;
                if (wall.IsCurved)
                {
                    _model.Problems.Add(new AnalyticalProblem("A curved bearing wall is not in the analytical model yet: model it as straight walls to analyse it.", new Element[] { wall }));
                    continue;
                }

                var structure = type.Structure;
                var core = structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).ToList();
                var thickness = core.Count > 0 ? core.Sum(layer => layer.Thickness) : structure.TotalWidth;
                var across = structure.TotalWidth / 2 - structure.ExteriorWidth - structure.CoreWidth / 2;
                var (a, b) = (wall.PointAt(structure, 0, across), wall.PointAt(structure, wall.Length, across));
                var thickest = core.OrderByDescending(layer => layer.Thickness).FirstOrDefault() ?? structure.Layers.OrderByDescending(layer => layer.Thickness).FirstOrDefault();
                var material = MaterialOf(_document, thickest is null ? null : _document.FindMaterial(thickest.MaterialId));
                _walls.Add(new WallFrame
                {
                    Wall = wall, Origin = a, Way = (b - a).NormalisedOrDefault(Vector2D.UnitX), Length = a.DistanceTo(b),
                    Base = wall.GetBaseElevation(_document), Top = wall.GetTopElevation(_document), Width = structure.TotalWidth,
                    Thickness = thickness, Material = material, Weight = Loads.Weight(_document, structure),
                    Profile = WallProfile.Of(_document, wall), From = 0, To = a.DistanceTo(b)
                });
            }

            // A wall standing on one below taken to its plane, so the two are joined all along.
            foreach (var frame in _walls.OrderBy(frame => frame.Base))
            {
                var lower = _walls.FirstOrDefault(other => !ReferenceEquals(other, frame) && Math.Abs(other.Way.Cross(frame.Way)) < 0.03
                    && Math.Abs(other.Top - frame.Base) <= 50 && other.Off(frame.Origin) <= (other.Width + frame.Width) / 2
                    && Math.Max(other.Along(frame.At(0)), other.Along(frame.At(frame.Length))) > 1
                    && Math.Min(other.Along(frame.At(0)), other.Along(frame.At(frame.Length))) < other.Length - 1);
                if (lower is null || lower.Off(frame.Origin) < 0.5) continue;
                frame.Origin = lower.At(lower.Along(frame.Origin));
                frame.Way = lower.Way.Dot(frame.Way) >= 0 ? lower.Way : -lower.Way;
            }

            // Each end taken to the plane of the wall it meets: a corner to where the two planes cross, a tee to the plane of the wall it runs into.
            foreach (var frame in _walls)
            foreach (var atStart in new[] { true, false })
            {
                var end = frame.At(atStart ? frame.From : frame.To);
                foreach (var other in _walls)
                {
                    if (ReferenceEquals(other, frame) || Math.Abs(frame.Way.Cross(other.Way)) < 0.3) continue;
                    if (frame.Top <= other.Base + 1 || other.Top <= frame.Base + 1) continue;
                    var reach = (frame.Width + other.Width) / 2 + 50;
                    if (other.Off(end) > reach) continue;
                    var along = other.Along(end);
                    if (along < other.From - reach || along > other.To + reach) continue;

                    // Where the two lines cross.
                    var denominator = frame.Way.Cross(other.Way);
                    var t = (other.Origin - frame.Origin).Cross(other.Way) / denominator;
                    var crossing = frame.At(t);
                    if (crossing.DistanceTo(end) > reach) continue;
                    if (atStart) frame.From = t;
                    else frame.To = t;

                    var on = other.Along(crossing);
                    if (on > other.From + reach && on < other.To - reach) other.Splits.Add(on);
                    break;
                }
            }
        }

        /// <summary>Each wall's plate: its outline, its doors and windows cut out of it, split where another tees into it.</summary>
        private void WallPanels()
        {
            foreach (var frame in _walls)
            {
                if (frame.To - frame.From < 1) continue;

                // Along and up the wall, in its own plane: along from the start of its plane's line, up from 0.
                IReadOnlyList<Point2D> outline;
                if (frame.Profile is { Count: >= 3 } profile)
                    outline = profile.Select(p => new Point2D(p.X <= 1e-6 ? frame.From : p.X >= frame.Length - 1e-6 ? frame.To : p.X, frame.Base + p.Y)).ToList();
                else
                    outline = new[] { new Point2D(frame.From, frame.Base), new Point2D(frame.To, frame.Base), new Point2D(frame.To, frame.Top), new Point2D(frame.From, frame.Top) };

                var cuts = new List<IReadOnlyList<Point2D>>();
                foreach (var opening in _document.Openings.Where(opening => opening.HostWallId == frame.Wall.Id))
                {
                    if (_document.FindType<OpeningType>(opening.TypeId) is not { } type) continue;
                    var (from, to) = opening.GetSpan(type);
                    var (sill, height) = opening.Placed(_document, type, frame.Wall);
                    if (height <= 1 || to - from <= 1) continue;

                    // Its span is along the wall's location line, which its plane's line runs beside from the same start.
                    var (low, high) = (from, to);
                    var bottom = frame.Base + sill;
                    cuts.Add(new[] { new Point2D(low, bottom), new Point2D(high, bottom), new Point2D(high, bottom + height), new Point2D(low, bottom + height) });
                }

                IReadOnlyList<PolygonBoolean.Region> regions = new[] { new PolygonBoolean.Region(outline, Array.Empty<IReadOnlyList<Point2D>>()) };
                foreach (var cut in cuts)
                    regions = regions.SelectMany(region => PolygonBoolean.Combine(region.Holes.Prepend(region.Outer).ToList(), new[] { cut }, BooleanOperation.Difference)).ToList();

                // Split where another wall tees into it.
                var splits = frame.Splits.Where(at => at > frame.From + 1 && at < frame.To - 1).OrderBy(at => at).ToList();
                if (splits.Count > 0)
                {
                    var edges = splits.Prepend(frame.From - 1).Append(frame.To + 1).ToList();
                    regions = regions.SelectMany(region => Enumerable.Range(0, edges.Count - 1).SelectMany(i =>
                    {
                        var strip = new[] { new Point2D(edges[i], -1e7), new Point2D(edges[i + 1], -1e7), new Point2D(edges[i + 1], 1e7), new Point2D(edges[i], 1e7) };
                        return PolygonBoolean.Combine(region.Holes.Prepend(region.Outer).ToList(), new IReadOnlyList<Point2D>[] { strip }, BooleanOperation.Intersection);
                    })).ToList();
                }

                var way = new Direction3D(frame.Way.X, frame.Way.Y, 0);
                var origin = Point3D.On(frame.Origin, 0);
                foreach (var region in regions.Where(region => region.Area > 1e4))
                {
                    var panel = new AnalyticalPanel(frame.Wall, AnalyticalPanelKind.Wall, frame.Material, frame.Thickness, "Centre", origin, way, new Direction3D(0, 0, 1))
                    {
                        Weight = frame.Weight
                    };
                    Point3D In(Point2D p) => Point3D.On(frame.At(p.X), p.Y);
                    var outer = region.Outer;
                    if (Polygon2D.SignedArea(outer) < 0) outer = outer.Reverse().ToList();
                    foreach (var corner in Simplified(outer)) panel.Outline.Add(Node(In(corner)));
                    foreach (var hole in region.Holes) panel.Openings.Add(hole.Select(In).ToList());
                    var (left, right) = (outer.Min(p => p.X), outer.Max(p => p.X));
                    foreach (var cut in cuts)
                    {
                        var (from, to, low, high) = (Math.Max(left, cut.Min(p => p.X)), Math.Min(right, cut.Max(p => p.X)), cut.Min(p => p.Y), cut.Max(p => p.Y));
                        if (to - from > 1) panel.Cuts.Add(new[] { new Point2D(from, low), new Point2D(to, low), new Point2D(to, high), new Point2D(from, high) }.Select(In).ToList());
                    }

                    _model.Panels.Add(panel);
                }
            }
        }

        /// <summary>An outline without the corners that lie on a straight run.</summary>
        private static IReadOnlyList<Point2D> Simplified(IReadOnlyList<Point2D> outline)
        {
            var kept = new List<Point2D>();
            for (var i = 0; i < outline.Count; i++)
            {
                var (before, at, after) = (outline[(i + outline.Count - 1) % outline.Count], outline[i], outline[(i + 1) % outline.Count]);
                if (at.DistanceTo(before) < 0.5) continue;
                if (Math.Abs((at - before).Cross(after - at)) > 1e-3 * (at - before).Length * (after - at).Length) kept.Add(at);
            }

            return kept.Count >= 3 ? kept : outline;
        }

        /// <summary>The bearing wall whose plane a point in plan is on - within half its thickness and a little - and how far along it, where its height takes in a height.</summary>
        private (WallFrame Frame, double Along)? WallUnder(Point2D at, double height, double above = 600)
        {
            foreach (var frame in _walls)
            {
                if (frame.Off(at) > frame.Width / 2 + 150) continue;
                var along = frame.Along(at);
                if (along < frame.From - 50 || along > frame.To + 50) continue;
                if (height < frame.Base - 1 || height > frame.TopAt(along) + above) continue;
                return (frame, Math.Clamp(along, frame.From, frame.To));
            }

            return null;
        }

        /// <summary>The wall standing along a beam, if one does: running its way within its own width, its foot at the beam's height, over the beam's middle.</summary>
        private WallFrame? WallOn(Point3D start, Point3D end)
        {
            var (a, b) = (new Point2D(start.X, start.Y), new Point2D(end.X, end.Y));
            var way = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var middle = a.MidpointTo(b);
            foreach (var frame in _walls)
            {
                if (Math.Abs(way.Cross(frame.Way)) > 0.05 || frame.Off(middle) > frame.Width / 2 + 150) continue;
                if (Math.Abs(frame.Base - (start.Z + end.Z) / 2) > 600) continue;
                var along = frame.Along(middle);
                if (along > frame.From && along < frame.To) return frame;
            }

            return null;
        }

        // ---- columns and beams ------------------------------------------------

        private void Columns()
        {
            foreach (var column in _document.All<Column>().Where(column => column.Structural))
            {
                if (_document.FindType<ColumnType>(column.TypeId) is not { } type) continue;
                var material = MaterialOf(_document, type.StructuralMaterial, _document.FindMaterial(type.MaterialId));
                var member = new AnalyticalMember(column, AnalyticalMemberKind.Column, SectionOf(type, material), "Centre")
                {
                    LocalY = new Direction3D(column.Across.X, column.Across.Y, 0)
                };
                var (bottom, top) = (column.GetBaseElevation(_document), column.GetTopElevation(_document));
                var outline = column.Outline(type);

                // Its top up to the beam or concrete floor over it, its foot down to what is under it.
                foreach (var beam in AllBeams())
                {
                    if (beam.Beam.Length < 1) continue;
                    var t = Architecture.Beams.Along(beam.Beam, column.Location);
                    var on = beam.Beam.Start + (beam.Beam.End - beam.Beam.Start) * t;
                    if (on.DistanceTo(column.Location) > Math.Max(Architecture.Beams.TypeOf(_document, beam.Beam).Width / 2, 50) + 50 && !(outline.Count >= 3 && Polygon2D.Contains(outline, on))) continue;
                    var line = Architecture.Beams.Line(_document, beam.Beam, t);
                    if (Math.Abs(line - top) <= 600) top = line;
                    else if (Math.Abs(line - bottom) <= 600 && line < top - 600) bottom = line;
                }

                foreach (var slab in PlateSlabs())
                {
                    if (!slab.Slab.Contains(column.Location)) continue;
                    if (Math.Abs(slab.Height - top) <= 600) top = slab.Height;
                    else if (Math.Abs(slab.Height - bottom) <= 600) bottom = slab.Height;
                }

                var foot = column.Location;
                if (WallUnder(column.Location, bottom, 600) is { } under && Math.Abs(under.Frame.TopAt(under.Along) - bottom) <= 600)
                {
                    bottom = under.Frame.TopAt(under.Along);
                    foot = under.Frame.At(under.Along);
                }

                if (top - bottom < 1) continue;
                member.Nodes.Add(Node(Point3D.On(foot, bottom)));
                member.Nodes.Add(Node(Point3D.On(column.Location, top)));
                _model.Members.Add(member);
                _columns[column.Id] = member;
                _named.Add((member, "C"));
            }
        }

        /// <summary>The beams of the model and the joists of its joist layouts.</summary>
        private IEnumerable<(Beam Beam, Element Element, bool Joist)> AllBeams()
        {
            foreach (var beam in _document.All<Beam>()) yield return (beam, beam, false);
            foreach (var system in _document.All<BeamSystem>())
            foreach (var joist in BeamSystems.Members(_document, system))
                yield return (joist, system, true);
        }

        private void Beams()
        {
            var all = AllBeams().Where(beam => beam.Beam.Length >= 1).ToList();
            foreach (var (beam, element, joist) in all)
            {
                var type = Architecture.Beams.TypeOf(_document, beam);
                var material = MaterialOf(_document, type.Material, _document.FindMaterial(type.MaterialId));
                // A beam drawn as a joist is one, as much as one of a layout: held along its top, sharing its load.
                var isJoist = joist || beam.Usage == BeamUsage.Joist;
                var member = new AnalyticalMember(element, isJoist ? AnalyticalMemberKind.Joist : AnalyticalMemberKind.Beam, SectionOf(type, material), beam.Justification switch
                {
                    BeamJustification.Centre => "Centre",
                    BeamJustification.Bottom => "Bottom",
                    _ => "Top"
                })
                {
                    StartRelease = beam.StartRelease,
                    EndRelease = beam.EndRelease
                };
                var across = (beam.End - beam.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
                member.LocalY = new Direction3D(across.X, across.Y, 0);

                Point3D End(Point2D at, Point2D other, double height)
                {
                    // On a column: to its axis, at its top where it is near it.
                    foreach (var (id, column) in _columns)
                    {
                        if (_document.Find<Column>(id) is not { } physical || _document.FindType<ColumnType>(physical.TypeId) is not { } columnType) continue;
                        var outline = physical.Outline(columnType);
                        if (!(outline.Count >= 3 && Polygon2D.Contains(outline, at)) && at.DistanceTo(physical.Location) > 150) continue;
                        var (low, high) = (column.Start.At.Z, column.End.At.Z);
                        if (height < low - 50 || height > high + 600) continue;
                        return Point3D.On(physical.Location, Math.Abs(height - high) <= 600 ? high : height);
                    }

                    // Framing into another beam: to its line, at its height.
                    foreach (var carrier in all)
                    {
                        if (ReferenceEquals(carrier.Beam, beam)) continue;
                        var way = carrier.Beam.End - carrier.Beam.Start;
                        if (Math.Abs((at - other).NormalisedOrDefault(Vector2D.UnitX).Cross(way.NormalisedOrDefault(Vector2D.UnitX))) < Math.Sin(Math.PI / 18)) continue;
                        var t = (at - carrier.Beam.Start).Dot(way) / way.Dot(way);
                        if (t < -0.01 || t > 1.01) continue;
                        var foot = carrier.Beam.Start + way * Math.Clamp(t, 0, 1);
                        if (foot.DistanceTo(at) > Architecture.Beams.TypeOf(_document, carrier.Beam).Width / 2 + 100) continue;
                        var line = Architecture.Beams.Line(_document, carrier.Beam, t);
                        if (Math.Abs(line - height) > 600) continue;
                        return Point3D.On(foot, line);
                    }

                    // Bearing on a wall: to its plane, down onto its top where it stands just over it. Never up to it: a wall going on past a beam, up to a roof, holds it where it
                    // is, and lifted to the top it would part from all that is framed to it at its own height.
                    if (WallUnder(at, height) is { } under)
                    {
                        var top = under.Frame.TopAt(under.Along);
                        return Point3D.On(under.Frame.At(under.Along), height > top && height - top <= 600 ? top : height);
                    }

                    return Point3D.On(at, height);
                }

                var start = End(beam.Start, beam.End, Architecture.Beams.Line(_document, beam, 0));
                var end = End(beam.End, beam.Start, Architecture.Beams.Line(_document, beam, 1));

                // Carrying a wall that stands along it: on that wall's line and at its foot. A wall's line is its structure's, a little off the one drawn, and a beam left beside it would meet it nowhere.
                if (WallOn(start, end) is { } carried)
                {
                    start = Point3D.On(carried.At(carried.Along(new Point2D(start.X, start.Y))), carried.Base);
                    end = Point3D.On(carried.At(carried.Along(new Point2D(end.X, end.Y))), carried.Base);
                }

                if (Distance(start, end) < 1) continue;
                member.Nodes.Add(Node(start));
                member.Nodes.Add(Node(end));
                _model.Members.Add(member);
                _named.Add((member, isJoist ? "J" : "B"));
            }
        }

        // ---- floors, roofs and the ground ------------------------------------------

        /// <summary>The lowest storey walls stand on: what a floor on the ground is at.</summary>
        private double Lowest => _document.Levels.Count == 0 ? 0 : _document.Levels.Min(level => level.Elevation);

        /// <summary>The floors and flat roofs that are plates, and the height of their plane.</summary>
        private IEnumerable<(Slab Slab, double Height)> PlateSlabs() =>
            _document.All<Slab>().Where(slab => slab is Floor or Roof && slab.Boundary.Count >= 3 && IsPlate(_document, slab))
                .Select(slab => (slab, slab is Roof roof ? roof.BaseElevation(_document) : slab.GetTopElevation(_document)));

        private void Plates()
        {
            var rafts = _document.All<RaftFoundation>().Where(raft => raft.Outline.Count >= 3).ToList();
            foreach (var (slab, height) in PlateSlabs())
            {
                var onGround = slab is Floor && Math.Abs((_document.FindLevel(slab.LevelId)?.Elevation ?? 0) - Lowest) < 1;

                // A floor over a raft is the raft's top: the raft is the plate.
                if (onGround && rafts.Any(raft => Polygon2D.Contains(raft.Outline, slab.Centroid))) continue;
                var type = _document.FindType<SlabType>(slab.TypeId)!;
                var core = type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).ToList();
                var thickness = core.Count > 0 ? core.Sum(layer => layer.Thickness) : type.Thickness;
                var material = MaterialOf(_document, _document.FindMaterial(core.OrderByDescending(layer => layer.Thickness).FirstOrDefault()?.MaterialId ?? Guid.Empty), AnalyticalMaterialKind.Concrete);
                var regions = Shafts.Regions(_document, slab);
                if (slab.Holes.Count > 0)
                    regions = regions.SelectMany(region => slab.Holes.Aggregate((IReadOnlyList<PolygonBoolean.Region>)new[] { region },
                        (pieces, hole) => pieces.SelectMany(piece => PolygonBoolean.Combine(piece.Holes.Prepend(piece.Outer).ToList(), new[] { hole }, BooleanOperation.Difference)).ToList())).ToList();

                foreach (var region in regions.Where(region => region.Area > 1e4))
                {
                    var kind = slab is Roof ? AnalyticalPanelKind.Roof : onGround ? AnalyticalPanelKind.GroundSlab : AnalyticalPanelKind.Floor;
                    var panel = new AnalyticalPanel(slab, kind, material, thickness, slab is Roof ? "Bottom" : "Top", new Point3D(0, 0, height), new Direction3D(1, 0, 0), new Direction3D(0, 1, 0))
                    {
                        Weight = Loads.Weight(_document, type.Structure),
                        Subgrade = onGround ? Subgrade(_document.ProjectInformation.Ground) : null
                    };
                    foreach (var corner in Snapped(region.Outer, height, onGround, 100)) panel.Outline.Add(Node(Point3D.On(corner, height)));
                    foreach (var hole in region.Holes) panel.Openings.Add(hole.Select(point => Point3D.On(point, height)).ToList());
                    _model.Panels.Add(panel);
                }
            }

            foreach (var raft in rafts)
            {
                var level = _document.FindLevel(raft.LevelId)?.Elevation ?? 0;
                var top = level - raft.TopDepth;
                var bases = _walls.Where(frame => frame.Wall.LevelId == raft.LevelId && Polygon2D.Contains(raft.Outline, frame.At((frame.From + frame.To) / 2)) && Math.Abs(frame.Base - top) <= 600).Select(frame => frame.Base).ToList();
                var height = bases.Count > 0 ? bases.Min() : top;
                var material = MaterialOf(_document, null, AnalyticalMaterialKind.Concrete);
                var panel = new AnalyticalPanel(raft, AnalyticalPanelKind.Raft, material, raft.Thickness, "Top", new Point3D(0, 0, height), new Direction3D(1, 0, 0), new Direction3D(0, 1, 0))
                {
                    Weight = raft.Thickness / 1000 * material.Density * 9.81 / 1000,
                    Subgrade = Subgrade(_document.ProjectInformation.Ground)
                };
                var outline = raft.Outline.ToList();
                if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
                foreach (var corner in Snapped(outline, height, true, 250)) panel.Outline.Add(Node(Point3D.On(corner, height)));
                _model.Panels.Add(panel);
            }
        }

        /// <summary>
        /// A floor's outline with its corners taken to the planes of the bearing walls it is on - where
        /// two meet, to where they cross; by one, onto it - so its edges are where those walls are.
        /// </summary>
        private IReadOnlyList<Point2D> Snapped(IReadOnlyList<Point2D> outline, double height, bool onTheirBase, double margin, IReadOnlyList<WallFrame>? walls = null)
        {
            var bearing = walls ?? _walls.Where(frame => Math.Abs((onTheirBase ? frame.Base : frame.Top) - height) <= 600).ToList();
            var moved = outline.Select(corner =>
            {
                var near = bearing
                    .Where(frame => frame.Off(corner) <= frame.Width + margin && frame.Along(corner) >= frame.From - frame.Width - margin && frame.Along(corner) <= frame.To + frame.Width + margin)
                    .OrderBy(frame => frame.Off(corner))
                    .ToList();
                if (near.Count == 0) return corner;
                var first = near[0];
                if (near.Skip(1).FirstOrDefault(frame => Math.Abs(frame.Way.Cross(first.Way)) > 0.3) is { } second)
                {
                    var t = (second.Origin - first.Origin).Cross(second.Way) / first.Way.Cross(second.Way);
                    return first.At(t);
                }

                return first.At(first.Along(corner));
            }).ToList();
            return Simplified(moved);
        }

        /// <summary>
        /// Each wall that bears nothing but meets a bearing wall as a stiffening wall (5.5.1.2(4)): each end that does splits that wall
        /// there, and holds it, sharing its stiffness as a cantilever from its foot - bending and shear, its modulus and G 0.4 E -
        /// between the walls it joins. Stud partitions have no such stiffness and do not count (5.5.1.2(6)).
        /// </summary>
        private void Stiffeners()
        {
            var floors = PlateSlabs().Select(entry => entry.Height).ToList();
            foreach (var wall in _document.Walls)
            {
                if (wall.StructuralUsage != StructuralUsage.NonBearing || _document.IsCurtainWall(wall) || wall.IsCurved || wall.Length < 1) continue;
                if (_document.FindType<StackedWallType>(wall.TypeId) is not null || _document.GetWallType(wall) is not { } type) continue;
                var core = type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure).ToList();
                var thickest = core.OrderByDescending(layer => layer.Thickness).FirstOrDefault();
                if (thickest is null) continue;
                var material = MaterialOf(_document, _document.FindMaterial(thickest.MaterialId));
                if (material.Kind is not (AnalyticalMaterialKind.Masonry or AnalyticalMaterialKind.Concrete)) continue;

                var (bottom, top) = (wall.GetBaseElevation(_document), wall.GetTopElevation(_document));
                var height = top - bottom;
                var thickness = core.Sum(layer => layer.Thickness);
                if (height <= 1 || wall.Length < height / 5) continue;
                if (Math.Abs(bottom - Lowest) >= 1 && !floors.Any(floor => Math.Abs(floor - bottom) <= 300)) continue;

                var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
                var meets = new List<(WallFrame Frame, double Along, Point2D At)>();
                foreach (var end in new[] { wall.Start, wall.End })
                {
                    var reach = type.Structure.TotalWidth / 2 + 50;
                    var frame = _walls.FirstOrDefault(each => Math.Abs(each.Way.Cross(way)) > 0.7 && each.Off(end) <= each.Width / 2 + reach && each.Along(end) >= each.From - 1 && each.Along(end) <= each.To + 1
                                                              && each.Top > bottom + 1 && each.Base < top - 1 && thickness >= 0.3 * each.Thickness);
                    if (frame is null) continue;
                    var along = Math.Clamp(frame.Along(end), frame.From, frame.To);
                    meets.Add((frame, along, frame.At(along)));
                }

                if (meets.Count == 0) continue;
                var second = thickness * Math.Pow(wall.Length, 3) / 12;
                var flexibility = Math.Pow(height, 3) / (3 * material.Modulus * second) + height / (0.4 * material.Modulus * 5.0 / 6 * thickness * wall.Length);
                foreach (var (frame, along, at) in meets)
                {
                    frame.Splits.Add(along);
                    _model.Stiffeners.Add(new AnalyticalStiffener(wall, frame.Wall, at, new Direction3D(way.X, way.Y, 0), Math.Max(bottom, frame.Base), Math.Min(top, frame.Top), 1 / flexibility / meets.Count));
                }
            }
        }

        /// <summary>
        /// Each timber floor over a storey and each roof that is not a plate as a diaphragm across the tops of the walls
        /// under it, as they are strapped to it (Approved Document A 2C33 to 2C36; BS EN 1996-1-1 5.5.1.2(2)): a roof at
        /// the eaves. Boarding, stiff in its plane and with next to no bending in it; it weighs nothing here, its weight being on the walls.
        /// </summary>
        private void Diaphragms()
        {
            var made = new List<(double Height, Polygon Area)>();
            foreach (var slab in _document.All<Slab>().Where(slab => slab is Floor or Roof && slab.Boundary.Count >= 3 && !IsPlate(_document, slab)))
            {
                if (slab is Floor && Math.Abs((_document.FindLevel(slab.LevelId)?.Elevation ?? 0) - Lowest) < 1) continue;

                // The walls it rests on: under a floor those whose tops are at it, under a roof the highest within it.
                var outline = slab.Boundary.ToList();
                if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
                var within = _walls.Where(frame => frame.To - frame.From >= 1 && Near(outline, frame.At((frame.From + frame.To) / 2), frame.Width + 1200)).ToList();
                if (slab is Floor)
                {
                    var level = slab.GetTopElevation(_document);
                    within = within.Where(frame => Math.Abs(frame.Top - level) <= 600).ToList();
                }
                else if (within.Count > 0)
                {
                    var highest = within.Max(frame => frame.Top);
                    within = within.Where(frame => frame.Top >= highest - 600).ToList();
                }

                if (within.Count == 0) continue;

                // At the lowest top of their ends - a hip or gable cuts a wall's top lower there - so its corners are on the walls.
                var plates = _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && within.Any(frame => frame.Wall == panel.Element)).ToList();
                if (plates.Count == 0) continue;
                var height = plates.Min(EndTop);
                var corners = Snapped(outline, height, false, 1200, within);
                if (corners.Count < 3 || Math.Abs(Polygon2D.SignedArea(corners)) < 1e5) continue;
                if (made.Any(other => Math.Abs(other.Height - height) < 50 && Polygon2D.Contains(other.Area.Corners, Polygon2D.Centroid(corners, corners[0])))) continue;
                if (_model.Panels.Any(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof && Math.Abs(panel.Origin.Z - height) < 50 && Polygon2D.Contains(panel.FlatOutline, Polygon2D.Centroid(corners, corners[0])))) continue;
                made.Add((height, new Polygon(corners)));

                var panel = new AnalyticalPanel(slab, AnalyticalPanelKind.Diaphragm, Boarding, 18, "Top", new Point3D(0, 0, height), new Direction3D(1, 0, 0), new Direction3D(0, 1, 0));
                foreach (var corner in corners) panel.Outline.Add(Node(Point3D.On(corner, height)));
                _model.Diaphragms.Add(panel);

                // A node at the end of each wall under it, for its edges to break at: a wall split by a cross wall shares them piece by piece.
                foreach (var wall in plates)
                {
                    var flat = wall.FlatOutline;
                    foreach (var along in new[] { flat.Min(point => point.X), flat.Max(point => point.X) })
                        Node(new Point3D(wall.Origin.X + wall.U.X * along, wall.Origin.Y + wall.U.Y * along, height));
                }
            }
        }

        /// <summary>How high a wall's plate stands at the lower of its two ends.</summary>
        private static double EndTop(AnalyticalPanel wall)
        {
            var flat = wall.FlatOutline;
            var (start, end) = (flat.Min(point => point.X), flat.Max(point => point.X));
            return Math.Min(flat.Where(point => Math.Abs(point.X - start) < 1).Max(point => point.Y), flat.Where(point => Math.Abs(point.X - end) < 1).Max(point => point.Y));
        }

        /// <summary>A diaphragm's corners, kept to see that no other covers the same ground.</summary>
        private sealed record Polygon(IReadOnlyList<Point2D> Corners);

        /// <summary>The boarding of a timber floor or roof as a diaphragm: 18 mm at E 3 GPa, about what chipboard or plywood sheathing has in its plane.</summary>
        private static readonly AnalyticalMaterial Boarding = new("Boarding", AnalyticalMaterialKind.Timber, "Boarding", 0, 3000, 1150, 0.3, double.NaN, "taken for a floor or roof strapped to its walls");

        private static bool Near(IReadOnlyList<Point2D> outline, Point2D at, double reach) =>
            Polygon2D.Contains(outline, at) || Enumerable.Range(0, outline.Count).Any(i => Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]) <= reach);

        private void GroundBeams()
        {
            foreach (var beam in _document.All<GroundBeam>())
            {
                if (_walls.FirstOrDefault(frame => frame.Wall.Id == beam.WallId) is not { } frame) continue;
                var material = MaterialOf(_document, null, AnalyticalMaterialKind.Concrete);
                var member = new AnalyticalMember(beam, AnalyticalMemberKind.GroundBeam, Rectangle($"Ground beam {beam.Width:0}x{beam.Depth:0}", material, beam.Depth, beam.Width), "Top")
                {
                    LocalY = new Direction3D(-frame.Way.Y, frame.Way.X, 0)
                };
                member.Nodes.Add(Node(Point3D.On(frame.At(frame.From), frame.Base)));
                member.Nodes.Add(Node(Point3D.On(frame.At(frame.To), frame.Base)));
                _model.Members.Add(member);
                _groundBeams[beam.Id] = member;
                _named.Add((member, "GB"));

                // A node on it over each pile.
                foreach (var pile in _document.All<Pile>().Where(pile => pile.BeamId == beam.Id))
                    Node(Point3D.On(frame.At(Math.Clamp(frame.Along(pile.Location), frame.From, frame.To)), frame.Base));
            }
        }

        // ---- joining it up ------------------------------------------------------

        /// <summary>Each node that falls on another's edge put into its outline, and each inside it into it; each on a member's line, into it.</summary>
        private void Connect()
        {
            // A column run up through a floor is joined to it there, though nothing of either ends at the other: without a node the floor would hang on nothing, and the column be one long length.
            foreach (var column in _model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).ToList())
                foreach (var plate in _model.Panels.Concat(_model.Diaphragms).Where(panel => panel.Kind != AnalyticalPanelKind.Wall))
                {
                    var (a, b) = (column.Start.At, column.End.At);
                    var (fromA, fromB) = (Dot(Minus(a, plate.Origin), plate.Normal), Dot(Minus(b, plate.Origin), plate.Normal));
                    if (Math.Abs(fromA) <= NodeTolerance || Math.Abs(fromB) <= NodeTolerance || fromA * fromB > 0) continue;
                    var t = fromA / (fromA - fromB);
                    var crossing = new Point3D(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
                    var flat = plate.Flat(crossing);
                    if (Polygon2D.Contains(plate.FlatOutline, flat) && !plate.Openings.Any(opening => Polygon2D.Contains(opening.Select(plate.Flat).ToList(), flat))) Node(crossing);
                }

            foreach (var panel in _model.Panels.Concat(_model.Diaphragms))
            {
                var flat = panel.FlatOutline;
                var openings = panel.Openings.Select(opening => (IReadOnlyList<Point2D>)opening.Select(panel.Flat).ToList()).ToList();
                var placed = new HashSet<AnalyticalNode>(panel.Outline);
                var insertions = new List<(int Edge, double Along, AnalyticalNode Node)>();
                foreach (var node in _model.Nodes)
                {
                    if (placed.Contains(node)) continue;
                    var off = Dot(Minus(node.At, panel.Origin), panel.Normal);
                    if (Math.Abs(off) > NodeTolerance) continue;
                    var at = panel.Flat(node.At);

                    var onEdge = false;
                    for (var i = 0; i < flat.Count; i++)
                    {
                        var (a, b) = (flat[i], flat[(i + 1) % flat.Count]);
                        if (Line2D.DistanceFromSegment(at, a, b) > NodeTolerance) continue;
                        var t = (at - a).Dot(b - a) / Math.Max((b - a).Dot(b - a), 1e-9);
                        if (t * a.DistanceTo(b) <= NodeTolerance || (1 - t) * a.DistanceTo(b) <= NodeTolerance) continue;
                        insertions.Add((i, t, node));
                        onEdge = true;
                        break;
                    }

                    if (!onEdge && Polygon2D.Contains(flat, at) && !openings.Any(opening => Polygon2D.Contains(opening, at)))
                        panel.Inside.Add(node);
                }

                foreach (var group in insertions.GroupBy(insertion => insertion.Edge).OrderByDescending(group => group.Key))
                    panel.Outline.InsertRange(group.Key + 1, group.OrderBy(insertion => insertion.Along).Select(insertion => insertion.Node));
            }

            foreach (var member in _model.Members)
            {
                var (a, b) = (member.Start.At, member.End.At);
                var along = _model.Nodes
                    .Where(node => !ReferenceEquals(node, member.Start) && !ReferenceEquals(node, member.End))
                    .Select(node => (Node: node, Foot: FromSegment(node.At, a, b)))
                    .Where(found => found.Foot.Off <= NodeTolerance && found.Foot.Along * member.Length > NodeTolerance && (1 - found.Foot.Along) * member.Length > NodeTolerance)
                    .OrderBy(found => found.Foot.Along)
                    .Select(found => found.Node)
                    .ToList();
                member.Nodes.InsertRange(1, along);
            }
        }

        // ---- holding it ------------------------------------------------------------

        private void Supports()
        {
            foreach (var footing in _document.All<WallFooting>())
            foreach (var panel in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Element.Id == footing.WallId))
            {
                var foot = panel.Outline.Min(node => node.At.Z);
                foreach (var (from, to) in panel.Edges.Where(edge => edge.From.At.Z <= foot + 1 && edge.To.At.Z <= foot + 1).ToList())
                    _model.Supports.Add(new AnalyticalSupport(footing, footing.Fixity) { Panel = panel, From = from, To = to });
            }

            foreach (var pad in _document.All<PadFooting>())
            {
                var node = pad.ColumnId is { } id && _columns.TryGetValue(id, out var column)
                    ? column.Start
                    : Node(Point3D.On(pad.Location, _document.FindLevel(pad.LevelId)?.Elevation ?? 0));
                _model.Supports.Add(new AnalyticalSupport(pad, pad.Fixity) { Node = node });
            }

            foreach (var pile in _document.All<Pile>())
            {
                AnalyticalNode node;
                if (pile.BeamId is { } beamId && _groundBeams.TryGetValue(beamId, out var beam))
                    node = beam.Nodes.OrderBy(each => Distance(each.At, Point3D.On(pile.Location, each.At.Z))).First();
                else
                    node = Node(Point3D.On(pile.Location, (_document.FindLevel(pile.LevelId)?.Elevation ?? 0) - pile.TopDepth));
                _model.Supports.Add(new AnalyticalSupport(pile, pile.Fixity) { Node = node });
            }
        }

        // ---- what is not joined up ---------------------------------------------------

        /// <summary>How many things a node is part of: members, panels and supports.</summary>
        private Dictionary<AnalyticalNode, int> Uses()
        {
            var uses = _model.Nodes.ToDictionary(node => node, _ => 0);
            foreach (var member in _model.Members)
            foreach (var node in member.Nodes.Distinct())
                uses[node]++;
            foreach (var panel in _model.Panels)
            foreach (var node in panel.Outline.Concat(panel.Inside).Distinct())
                uses[node]++;
            foreach (var support in _model.Supports)
            {
                if (support.Node is { } node) uses[node]++;
                if (support.From is { } from) uses[from]++;
                if (support.To is { } to) uses[to]++;
            }

            return uses;
        }

        private void Check()
        {
            var uses = Uses();
            bool Joined(AnalyticalNode node) => uses[node] > 1;

            foreach (var member in _model.Members)
            {
                switch (member.Kind)
                {
                    case AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist:
                        foreach (var (node, which) in new[] { (member.Start, "start"), (member.End, "end") })
                        {
                            if (Joined(node)) continue;
                            var beam = member.Element as Beam;
                            var physical = beam is null ? null : Architecture.Beams.CarriedBy(_document, beam, which == "start" ? beam.Start : beam.End);
                            _model.Problems.Add(new AnalyticalProblem(physical == "a wall"
                                ? $"Its {which} rests on a wall that is not bearing: set the wall's Structural Usage to Bearing to carry it in the analytical model."
                                : $"Its {which} is joined to nothing in the analytical model: no structural column, bearing wall or beam meets it there.", new[] { member.Element }));
                        }

                        break;
                    case AnalyticalMemberKind.Column when !Joined(member.Start):
                        _model.Problems.Add(new AnalyticalProblem("Its foot is joined to nothing in the analytical model: put a pad under it, or stand it on a beam, a bearing wall or a concrete floor.", new[] { member.Element }));
                        break;
                }
            }

            foreach (var group in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).GroupBy(panel => panel.Element.Id))
            {
                var held = group.Any(panel =>
                {
                    var foot = panel.Outline.Min(node => node.At.Z);
                    return panel.Outline.Where(node => node.At.Z <= foot + 1).Any(Joined);
                });
                if (!held)
                    _model.Problems.Add(new AnalyticalProblem("It stands on nothing in the analytical model: put a footing under it, or stand it on a ground beam, a concrete floor or a bearing wall.", new[] { group.First().Element }));
            }

            foreach (var panel in _model.Panels.Where(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof))
                if (!panel.Outline.Concat(panel.Inside).Any(Joined))
                    _model.Problems.Add(new AnalyticalProblem("It rests on nothing in the analytical model: no bearing wall, beam or column meets it.", new[] { panel.Element }));

            foreach (var support in _model.Supports.Where(support => support.Node is { } node && uses[node] <= 1))
                _model.Problems.Add(new AnalyticalProblem("It carries nothing in the analytical model: nothing stands on it.", new[] { support.Foundation }));

            if (!_model.IsHeld && (_model.Members.Count > 0 || _model.Panels.Count > 0))
                _model.Problems.Add(new AnalyticalProblem("The analytical model is held by nothing: put footings under its walls and columns, or a raft under it.", Array.Empty<Element>()));
        }

        // ---- names ---------------------------------------------------------------

        private void Name()
        {
            var nodes = _model.Nodes.OrderBy(node => Math.Round(node.At.Z)).ThenBy(node => Math.Round(node.At.Y)).ThenBy(node => Math.Round(node.At.X)).ToList();
            _model.Nodes.Clear();
            _model.Nodes.AddRange(nodes);
            for (var i = 0; i < nodes.Count; i++) nodes[i].Name = $"N{i + 1}";

            foreach (var group in _named.GroupBy(named => named.Prefix))
            {
                var count = 0;
                foreach (var (member, _) in group.OrderBy(named => Math.Round(named.Member.Start.At.Z)).ThenBy(named => Math.Round(named.Member.Start.At.Y)).ThenBy(named => Math.Round(named.Member.Start.At.X)))
                    member.Name = $"{group.Key}{++count}";
            }

            _model.Members.Sort((a, b) => string.CompareOrdinal(Order(a.Name), Order(b.Name)));

            foreach (var group in _model.Panels.GroupBy(panel => panel.Kind))
            {
                var prefix = group.Key switch
                {
                    AnalyticalPanelKind.Wall => "W",
                    AnalyticalPanelKind.Floor => "F",
                    AnalyticalPanelKind.Roof => "R",
                    AnalyticalPanelKind.Raft => "RF",
                    _ => "GS"
                };
                var count = 0;
                foreach (var element in group.GroupBy(panel => panel.Element.Id).OrderBy(each => each.Min(panel => Math.Round(panel.Outline.Min(node => node.At.Z)))).ThenBy(each => Math.Round(each.First().Outline.Min(node => node.At.Y))).ThenBy(each => Math.Round(each.First().Outline.Min(node => node.At.X))))
                {
                    count++;
                    var pieces = element.ToList();
                    for (var i = 0; i < pieces.Count; i++) pieces[i].Name = pieces.Count == 1 ? $"{prefix}{count}" : $"{prefix}{count}.{i + 1}";
                }
            }

            for (var i = 0; i < _model.Supports.Count; i++) _model.Supports[i].Name = $"S{i + 1}";
        }

        /// <summary>A name sorted by its letters then its number: C2 before C10.</summary>
        private static string Order(string name)
        {
            var digits = new string(name.SkipWhile(char.IsLetter).TakeWhile(char.IsDigit).ToArray());
            return $"{new string(name.TakeWhile(char.IsLetter).ToArray()),-3}{(int.TryParse(digits, out var number) ? number : 0):D6}";
        }
    }
}

/// <summary>The Properties an element's place in the analytical model is shown and set by.</summary>
public static class AnalyticalParameters
{
    public static readonly ParameterDefinition Model = new("Analytical Model", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition StartRelease = new("Start Release", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition EndRelease = new("End Release", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition Fixity = new("Support", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);
    public static readonly ParameterDefinition AnalyseAs = new("Analyse As", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Analytical);

    /// <summary>What it is in the analytical model, worked out when it is read.</summary>
    public static ParameterValue Said(BimDocument document, Element element) =>
        ParameterValue.ReadOnly(Model, () => AnalyticalModel.Said(document, element));

    public static ParameterValue FixityOf(Func<SupportFixity> get, Action<SupportFixity> set) =>
        ParameterValue.BindChoice(Fixity,
            () => EnumText.Humanise(get()),
            v => { if (EnumText.TryParse<SupportFixity>(v, out var fixity)) set(fixity); },
            EnumText.Choices<SupportFixity>());

    public static ParameterValue Release(ParameterDefinition definition, Func<MemberRelease> get, Action<MemberRelease> set) =>
        ParameterValue.BindChoice(definition,
            () => EnumText.Humanise(get()),
            v => { if (EnumText.TryParse<MemberRelease>(v, out var release)) set(release); },
            EnumText.Choices<MemberRelease>());
}
