using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using V3 = BIMDesigner.Core.Architecture.StructuralSolver.Vector3;

namespace BIMDesigner.Core.Architecture;

/// <summary>One member's results under a combination: its greatest forces, kN and kNm, and its deflections against their limits.</summary>
public sealed record MemberResult(
    AnalyticalMember Member, double Axial, double ShearY, double ShearZ, double Torsion, double MomentY, double MomentZ,
    double Deflection, double Span, double? Limit, string LimitSaid)
{
    /// <summary>How much of its limit it deflects: 1 is the limit; null where it has none.</summary>
    public double? Ratio => Limit is { } limit && Span > 0 ? Deflection / (Span / limit) : null;
}

/// <summary>A support's reactions under a combination, kN and kNm, in the building's axes, up positive.</summary>
public sealed record SupportResult(AnalyticalSupport? Support, AnalyticalPanel? Ground, double Fx, double Fy, double Fz, double Mx, double My, double Mz);

/// <summary>A plate's results under a combination: how far it moves at most, mm, and its greatest bending moments, kNm per m, across its two ways.</summary>
public sealed record PanelResult(AnalyticalPanel Panel, double Deflection, double MomentU, double MomentV, double LeastMomentU, double LeastMomentV);

/// <summary>
/// The ground's pressure under a plate on it, under every load but the wind at its characteristic value,
/// kPa: the greatest at any node, the average over it, how far it goes down at most, mm, and whether any
/// of it lifts; against the presumed allowable bearing pressure of the ground, where there is one.
/// </summary>
public sealed record BearingResult(AnalyticalPanel Panel, double Greatest, double Average, double Settlement, bool Lifts, double? Allowable, string AllowableSaid)
{
    /// <summary>How much of the allowable pressure the greatest is: 1 is all of it; null where the ground has no presumed value.</summary>
    public double? Ratio => Allowable is > 0 ? Greatest / Allowable : null;
}

/// <summary>
/// A ground beam's bars worked out from what the analysis finds in it: its greatest sagging and hogging
/// moments over the ultimate combinations, kNm, its effective depths, mm, and the steel each face needs, has
/// and is to have at the least, mm² - with what it was worked out by, and what stops it where the section cannot carry it.
/// </summary>
public sealed record GroundBeamDesign(
    AnalyticalMember Member, GroundBeam Beam, double Sagging, double Hogging, double BottomDepth, double TopDepth,
    double BottomNeeded, double BottomGiven, double TopNeeded, double TopGiven, string By, string? Problem, double BottomLeast, double TopLeast)
{
    /// <summary>Whether the bars it has are enough: the section carries its moments, and each face has what it needs.</summary>
    public bool Enough => Problem is null && BottomGiven >= BottomNeeded - 0.5 && TopGiven >= TopNeeded - 0.5;
}

/// <summary>
/// The structure analysed: the analytical model meshed - each member into beams no longer than the mesh
/// size, each plate into shell triangles of about that size, sharing their nodes where they meet - held
/// at its supports and by the ground under a raft, loaded by each load case - its self weight worked out
/// here from its sections, plates and densities - and solved, linear and elastic, once for each case;
/// each combination the sum of its cases, factored. What it comes to: each node's movement, each member's
/// forces and deflection against the limits for its material and standard, each support's reactions,
/// each plate's deflection and moments, and how well the loads and reactions balance.
/// </summary>
public sealed class StructuralAnalysis
{
    /// <summary>The mesh size the plates are cut into and the members divided by, mm, unless asked otherwise.</summary>
    public const double DefaultMeshSize = 600;

    private const double Gravity = 9.81;

    public AnalyticalModel Model { get; }

    public StructuralLoading Loading { get; }

    public StructuralSolver Solver { get; } = new();

    public double MeshSize { get; }

    /// <summary>Each case's movements, six to a node: mm and radians.</summary>
    public IReadOnlyDictionary<LoadCase, double[]> Displacements => _displacements;

    /// <summary>What went wrong: a structure free to move, loads with nothing to go on.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>What was taken: the mesh, the ground's friction, the limits.</summary>
    public List<string> Notes { get; } = new();

    private readonly Dictionary<LoadCase, double[]> _displacements = new();
    private readonly Dictionary<LoadCase, double[]> _loads = new();
    private readonly Dictionary<(LoadCase, int), double[]> _beamLoads = new();
    private readonly Dictionary<AnalyticalNode, int> _nodeOf = new();
    private readonly Dictionary<long, List<int>> _grid = new();
    private readonly List<(AnalyticalMember Member, int Beam, double From, double To, int Span)> _memberBeams = new();
    private readonly List<(AnalyticalPanel Panel, int Shell)> _panelShells = new();
    private readonly List<(AnalyticalSupport Support, IReadOnlyList<int> Nodes)> _supportNodes = new();
    private readonly List<(AnalyticalPanel Panel, IReadOnlyList<(int Node, double Area)> Nodes)> _groundNodes = new();
    private readonly HashSet<int> _crossNodes = new();
    private readonly Dictionary<LoadCase, double[]> _internal = new();

    private StructuralAnalysis(AnalyticalModel model, StructuralLoading loading, double meshSize)
    {
        Model = model;
        Loading = loading;
        MeshSize = meshSize;
    }

    /// <summary>The model analysed for every load case of its loading.</summary>
    public static StructuralAnalysis Run(BimDocument document, AnalyticalModel model, StructuralLoading loading, double meshSize = DefaultMeshSize)
    {
        var analysis = new StructuralAnalysis(model, loading, Math.Max(100, meshSize));
        analysis.Build(document);
        return analysis;
    }

    /// <summary>The mesh node at a node of the model.</summary>
    public int NodeOf(AnalyticalNode node) => _nodeOf[node];

    /// <summary>A load case as forces on the nodes, N and N mm, six to a node.</summary>
    public double[] LoadOf(LoadCase loadCase) => _loads[loadCase];

    /// <summary>What a load case puts along a beam of the mesh, in its axes, where it does.</summary>
    public double[]? LoadAlong(LoadCase loadCase, int beam) => _beamLoads.GetValueOrDefault((loadCase, beam));

    // ---- the mesh ---------------------------------------------------------------------------

    /// <summary>The mesh node at a point - one already there within 2 mm, or a new one.</summary>
    private int Node(Point3D at)
    {
        long Cell(double v) => (long)Math.Floor(v / 10);
        var (cx, cy, cz) = (Cell(at.X), Cell(at.Y), Cell(at.Z));
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
            if (_grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var nodes))
                foreach (var node in nodes)
                    if (AnalyticalModel.Distance(Solver.NodeAt(node), at) <= 2) return node;

        var added = Solver.AddNode(at);
        var key = Key(cx, cy, cz);
        if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
        list.Add(added);
        return added;
    }

    private static long Key(long x, long y, long z) => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);

    /// <summary>A line between two points cut into equal pieces no longer than the mesh size - the same pieces from either end.</summary>
    private IReadOnlyList<Point3D> Divide(Point3D a, Point3D b)
    {
        var length = AnalyticalModel.Distance(a, b);
        var pieces = Math.Max(1, (int)Math.Ceiling(length / MeshSize - 1e-9));
        return Enumerable.Range(0, pieces + 1).Select(i => new Point3D(a.X + (b.X - a.X) * i / pieces, a.Y + (b.Y - a.Y) * i / pieces, a.Z + (b.Z - a.Z) * i / pieces)).ToList();
    }

    private void Build(BimDocument document)
    {
        foreach (var node in Model.Nodes) _nodeOf[node] = Node(node.At);
        Notes.Add($"Members divided, and plates meshed in triangles, about {MeshSize:0} mm apart; each node six ways free but where it is held.");

        // Members: beams between their nodes, pinned ends where they are released.
        foreach (var member in Model.Members)
        {
            var section = member.Section;
            var material = section.Material;
            var shear = material.ShearModulus > 0 ? material.ShearModulus : material.Modulus / (2 * (1 + PoissonOf(material)));
            var along = 0.0;
            for (var span = 0; span + 1 < member.Nodes.Count; span++)
            {
                var points = Divide(member.Nodes[span].At, member.Nodes[span + 1].At);
                for (var i = 0; i + 1 < points.Count; i++)
                {
                    var (a, b) = (Node(points[i]), Node(points[i + 1]));
                    if (a == b) continue;
                    var length = AnalyticalModel.Distance(points[i], points[i + 1]);
                    var first = span == 0 && i == 0;
                    var last = span + 2 == member.Nodes.Count && i + 2 == points.Count;
                    var beam = Solver.AddBeam(new StructuralSolver.Beam(a, b, material.Modulus, shear, section.Area, section.Iy, section.Iz, TorsionConstant(section),
                        new V3(member.LocalY.X, member.LocalY.Y, member.LocalY.Z),
                        first && member.StartRelease == MemberRelease.Pinned, last && member.EndRelease == MemberRelease.Pinned));
                    _memberBeams.Add((member, beam, along, along + length, span));
                    along += length;
                }
            }
        }

        // Plates: meshed in their own plane.
        foreach (var panel in Model.Panels.Concat(Model.Diaphragms)) Mesh(panel, StudShare(document, panel));

        // Supports.
        foreach (var support in Model.Supports)
        {
            var nodes = support.IsLine && support.Panel is { } panel && support.From is { } from && support.To is { } to
                ? Divide(from.At, to.At).Select(Node).Distinct().ToList()
                : support.Node is { } node ? new List<int> { _nodeOf[node] } : new List<int>();
            foreach (var n in nodes)
                Solver.Restrain(n, support.Fixity == SupportFixity.Fixed ? new[] { true, true, true, true, true, true } : new[] { true, true, true, false, false, false });
            _supportNodes.Add((support, nodes));
        }

        // The ground under a raft or slab: a spring under each node, its share of the area at the ground's stiffness.
        foreach (var panel in Model.Panels.Where(panel => panel.Subgrade is not null))
        {
            var areas = new Dictionary<int, double>();
            foreach (var (_, shell) in _panelShells.Where(entry => ReferenceEquals(entry.Panel, panel)))
            {
                var s = Solver.Shells[shell];
                var area = TriangleArea(s);
                foreach (var n in new[] { s.I, s.J, s.K }) areas[n] = areas.GetValueOrDefault(n) + area / 3;
            }

            var k = panel.Subgrade!.Value * 1e-3;
            foreach (var (n, area) in areas)
            {
                Solver.AddSpring(n, 2, k * area);
                Solver.AddSpring(n, 0, 0.1 * k * area);
                Solver.AddSpring(n, 1, 0.1 * k * area);
            }

            _groundNodes.Add((panel, areas.Select(entry => (entry.Key, entry.Value)).ToList()));
        }

        // Cross walls: a line of support up the wall each meets, the way it runs, its stiffness shared along it.
        foreach (var cross in Model.Stiffeners)
        {
            var nodes = Enumerable.Range(0, Solver.NodeCount).Where(n =>
            {
                var at = Solver.NodeAt(n);
                return Math.Abs(at.X - cross.At.X) <= 2 && Math.Abs(at.Y - cross.At.Y) <= 2 && at.Z >= cross.Bottom - 2 && at.Z <= cross.Top + 2;
            }).ToList();
            foreach (var n in nodes)
            {
                Solver.AddSpring(n, 0, cross.Stiffness / nodes.Count * cross.Way.X * cross.Way.X);
                Solver.AddSpring(n, 1, cross.Stiffness / nodes.Count * cross.Way.Y * cross.Way.Y);
                _crossNodes.Add(n);
            }
        }

        if (Model.Stiffeners.Count > 0)
            Notes.Add($"{Model.Stiffeners.Count} meeting{(Model.Stiffeners.Count == 1 ? "" : "s")} of a cross wall that bears nothing with a wall it stiffens (BS EN 1996-1-1 5.5.1.2(4)): a line of support up the wall, taken as tied to it, as stiff as the cross wall is as a cantilever from its foot. The cross wall's own strength is not checked.");
        if (_groundNodes.Count > 0)
            Notes.Add("The ground under a raft or slab as springs at each node, its stiffness times the node's share of the area; held sideways by springs a tenth as stiff, for its friction.");
        if (Model.Diaphragms.Count > 0)
            Notes.Add($"{Model.Diaphragms.Count} timber floor{(Model.Diaphragms.Count == 1 ? " or roof as a thin diaphragm" : "s and roofs as thin diaphragms")} across the wall tops: 18 mm at 3 GPa, taken as strapped to the walls, weighing nothing here as their weight is already on the walls. They hold the tops and carry the wind to the walls that run its way.");
        if (Model.Members.Any(member => member.Kind == AnalyticalMemberKind.GroundBeam) && Model.Panels.Any(panel => panel.Kind == AnalyticalPanelKind.Wall))
            Notes.Add("A wall on a ground beam is a plate joined to it along its foot, so the two act as one composite deep beam - the masonry arching over the piles, " +
                      "the beam its tie - and the beam's moments are a fraction of wL²/8. Until the masonry has hardened the beam carries it alone; " +
                      "the rough check of 1.4 times its load and wL²/10 in Warnings stays for that, and where the wall's openings break the arch.");
        if (Model.Supports.Count == 0 && _groundNodes.Count == 0)
            Problems.Add("Nothing holds the structure up: no footing, pile or raft is under it, so it cannot be analysed.");

        // Loads, case by case.
        foreach (var loadCase in Loading.Cases.Where(loadCase => loadCase.Nature != LoadNature.Seismic)) _loads[loadCase] = Load(loadCase);
        var levels = SeismicLoads(document);
        var solution = Solver.Solve(Loading.Cases.Select(loadCase => _loads[loadCase]).ToList());
        Problems.AddRange(solution.Problems);
        for (var i = 0; i < Loading.Cases.Count; i++)
        {
            _displacements[Loading.Cases[i]] = solution.Displacements[i];
            _internal[Loading.Cases[i]] = Solver.InternalForces(solution.Displacements[i]);
        }

        if (levels is not null && Loading.Seismic is { } quake) Seismic = Drifts(document, quake, levels);
    }

    private double TriangleArea(StructuralSolver.Shell shell)
    {
        var (a, b, c) = (V3.Of(Solver.NodeAt(shell.I)), V3.Of(Solver.NodeAt(shell.J)), V3.Of(Solver.NodeAt(shell.K)));
        return (b - a).Cross(c - a).Length / 2;
    }

    /// <summary>A plate cut into shell triangles: its edges and openings divided, the lines other plates and members meet it along kept as edges, points about the mesh size apart filling it.</summary>
    /// <summary>How stiff a wall of studs is up them, as a share of the solid plate standing for it: a stud's second moment of area for each mm along the wall over the plate's. Null for anything but studs.</summary>
    private static StructuralSolver.Studs? StudShare(BimDocument document, AnalyticalPanel panel)
    {
        if (panel.Kind != AnalyticalPanelKind.Wall || panel.Material.Kind != AnalyticalMaterialKind.Timber) return null;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        if (TimberWallDesign.FrameOf(document, panel, us) is not { } frame) return null;
        var nu = PoissonOf(panel.Material);
        var (b, d, t) = (frame.Section.Thickness, frame.Section.Depth, panel.Thickness);
        var solid = panel.Material.Modulus * t / (1 - nu * nu);

        // In its plane: its studs end on, its boards across, and in shear the slip of the nails holding its boards on.
        var board = TimberWallDesign.Board(us);
        return new StructuralSolver.Studs(0, 0, b * Math.Pow(d, 3) / 12 / frame.Spacing / (Math.Pow(t, 3) / (12 * (1 - nu * nu))),
            panel.Material.Modulus * b * d / frame.Spacing / solid, TimberWallDesign.BoardModulus * board / solid, TimberWallDesign.RackingStiffness(us) / solid);
    }

    private void Mesh(AnalyticalPanel panel, StructuralSolver.Studs? studs)
    {
        var points = new List<(Point2D Flat, Point3D At)>();
        var constraints = new List<(int, int)>();
        Point3D At(Point2D p) => new(panel.Origin.X + panel.U.X * p.X + panel.V.X * p.Y, panel.Origin.Y + panel.U.Y * p.X + panel.V.Y * p.Y, panel.Origin.Z + panel.U.Z * p.X + panel.V.Z * p.Y);
        int Point(Point3D at)
        {
            var flat = panel.Flat(at);
            var found = points.FindIndex(point => point.Flat.DistanceTo(flat) <= 2);
            if (found >= 0) return found;
            points.Add((flat, at));
            return points.Count - 1;
        }

        void Line(Point3D a, Point3D b)
        {
            var divided = Divide(a, b);
            for (var i = 0; i + 1 < divided.Count; i++)
            {
                var (p, q) = (Point(divided[i]), Point(divided[i + 1]));
                if (p != q) constraints.Add((p, q));
            }
        }

        var outline = panel.FlatOutline;
        foreach (var (from, to) in panel.Edges) Line(from.At, to.At);
        var openings = panel.Openings.Select(opening => opening.Select(panel.Flat).ToList()).ToList();
        foreach (var opening in panel.Openings)
            for (var i = 0; i < opening.Count; i++)
                Line(opening[i], opening[(i + 1) % opening.Count]);

        bool InPlane(Point3D at) => Math.Abs(AnalyticalModel.Dot(AnalyticalModel.Minus(at, panel.Origin), panel.Normal)) <= 5;
        bool On(Point2D flat) => Polygon2D.Contains(outline, flat) || Enumerable.Range(0, outline.Count).Any(i => Line2D.DistanceFromSegment(flat, outline[i], outline[(i + 1) % outline.Count]) <= 5);
        bool Inside(Point3D at) => InPlane(at) && On(panel.Flat(at));

        // The lines other plates' edges and members run along in it: kept as edges, so they share its nodes.
        foreach (var other in Model.Panels.Concat(Model.Diaphragms).Where(other => !ReferenceEquals(other, panel)))
            foreach (var (from, to) in other.Edges)
                if (Inside(from.At) && Inside(to.At) && Inside(Mid(from.At, to.At))) Line(from.At, to.At);
        foreach (var member in Model.Members)
            for (var i = 0; i + 1 < member.Nodes.Count; i++)
                if (Inside(member.Nodes[i].At) && Inside(member.Nodes[i + 1].At) && Inside(Mid(member.Nodes[i].At, member.Nodes[i + 1].At)))
                    Line(member.Nodes[i].At, member.Nodes[i + 1].At);
        foreach (var node in panel.Inside) Point(node.At);

        // Points filling it, about the mesh size apart, kept off its edges.
        var minX = outline.Min(p => p.X);
        var maxX = outline.Max(p => p.X);
        var minY = outline.Min(p => p.Y);
        var maxY = outline.Max(p => p.Y);
        var fixedPoints = points.Select(point => point.Flat).ToList();
        var edges = constraints.Select(c => (points[c.Item1].Flat, points[c.Item2].Flat)).ToList();

        // A diaphragm is meshed from its edges alone: nothing stands on its middle, and it has no bending to hold points there up. Where joists run through it, it is what
        // ties them in its own plane; it must not hold them up, boards having no such span: a thousandth of a plate's bending, so its nodes stay sound and it carries nothing that way.
        if (panel.Kind != AnalyticalPanelKind.Diaphragm)
        for (var x = minX + MeshSize; x < maxX - MeshSize / 3; x += MeshSize)
        for (var y = minY + MeshSize; y < maxY - MeshSize / 3; y += MeshSize)
        {
            var p = new Point2D(x, y);
            if (!Polygon2D.Contains(outline, p) || openings.Any(opening => Polygon2D.Contains(opening, p))) continue;
            if (edges.Any(edge => Line2D.DistanceFromSegment(p, edge.Item1, edge.Item2) < MeshSize * 0.45)) continue;
            if (fixedPoints.Any(q => q.DistanceTo(p) < MeshSize * 0.45)) continue;
            points.Add((p, At(p)));
        }

        var (shells, meshed) = (0, 0.0);
        foreach (var (a, b, c) in Triangulation.Of(points.Select(point => point.Flat).ToList(), constraints))
        {
            var (pa, pb, pc) = (points[a].Flat, points[b].Flat, points[c].Flat);
            var centre = new Point2D((pa.X + pb.X + pc.X) / 3, (pa.Y + pb.Y + pc.Y) / 3);
            if (!Polygon2D.Contains(outline, centre) || openings.Any(opening => Polygon2D.Contains(opening, centre))) continue;
            if (Math.Abs(Polygon2D.SignedArea(new[] { pa, pb, pc })) < 1) continue;
            var nodes = new[] { Node(points[a].At), Node(points[b].At), Node(points[c].At) };
            if (nodes.Distinct().Count() < 3) continue;
            var made = new StructuralSolver.Shell(nodes[0], nodes[1], nodes[2], panel.Material.Modulus, PoissonOf(panel.Material), panel.Thickness, panel.Kind == AnalyticalPanelKind.Diaphragm ? 1e-3 : 1);
            if (studs is { } frame)
            {
                var (x, y, _) = Solver.Axes(made);
                var up = new StructuralSolver.Vector3(panel.V.X, panel.V.Y, panel.V.Z);
                made = made with { Framed = frame with { Cos = up.Dot(x), Sin = up.Dot(y) } };
            }

            var shell = Solver.AddShell(made);
            _panelShells.Add((panel, shell));
            shells++;
            meshed += Math.Abs(Polygon2D.SignedArea(new[] { pa, pb, pc }));
        }

        // Every load by area goes on these triangles, so they had better come to the plate: they are the one thing a load balance cannot catch.
        if (panel.Area > 1 && Math.Abs(meshed / panel.Area - 1) > 0.02)
            Problems.Add($"{panel.Name} could not be meshed: its triangles come to {meshed / 1e6:0.0} m² of its {panel.Area / 1e6:0.0} m², and all that is worked out from it is wrong.");

        if (shells == 0) Problems.Add($"{panel.Name} could not be meshed: it has no area left once its openings are cut out.");
    }

    /// <summary>A material's Poisson's ratio - where none is given, 0.2 for masonry and concrete, 0.3 for steel, timber and the rest.</summary>
    public static double PoissonOf(AnalyticalMaterial material) =>
        double.IsFinite(material.Poisson) && material.Poisson is >= 0 and < 0.5 ? material.Poisson
            : material.Kind is AnalyticalMaterialKind.Masonry or AnalyticalMaterialKind.Concrete ? 0.2 : 0.3;

    private static Point3D Mid(Point3D a, Point3D b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

    /// <summary>A section's torsion constant, mm⁴: a rectangle's by Roark, an I's and a channel's as their plates', a tube's by Bredt, a circle's polar.</summary>
    public static double TorsionConstant(AnalyticalSection section)
    {
        var d = section.Dimensions;
        switch (section.Shape)
        {
            case "Rectangle" when d.Count >= 2:
            {
                var (a, b) = (Math.Max(d[0], d[1]), Math.Min(d[0], d[1]));
                return a * Math.Pow(b, 3) * (1.0 / 3 - 0.21 * b / a * (1 - Math.Pow(b, 4) / (12 * Math.Pow(a, 4))));
            }
            case "I Section" when d.Count >= 6:
                return (d[1] * Math.Pow(d[3], 3) + d[2] * Math.Pow(d[4], 3) + Math.Max(0, d[0] - d[3] - d[4]) * Math.Pow(d[5], 3)) / 3;
            case "Circle" when d.Count >= 1:
                return Math.PI * Math.Pow(d[0], 4) / 32;
            case "Pipe" when d.Count >= 2:
                return Math.PI * (Math.Pow(d[0], 4) - Math.Pow(Math.Max(0, d[0] - 2 * d[1]), 4)) / 32;
            case "Tube" when d.Count >= 3:
            {
                var (h, b, t) = (d[0] - d[2], d[1] - d[2], d[2]);
                return 4 * Math.Pow(h * b, 2) * t / (2 * (h + b));
            }
            default:
                return section.Iy + section.Iz;
        }
    }

    // ---- loads --------------------------------------------------------------------------------

    /// <summary>A load case as forces on the nodes, N and N mm, six to a node.</summary>
    private double[] Load(LoadCase loadCase)
    {
        var load = new double[Solver.NodeCount * 6];

        void OnBeam(int beam, V3 perMm, double share)
        {
            if (share <= 0) return;
            var (local, global) = Solver.BeamUniformLoad(Solver.Beams[beam], perMm * share);
            var b = Solver.Beams[beam];
            for (var i = 0; i < 6; i++)
            {
                load[b.I * 6 + i] += global[i];
                load[b.J * 6 + i] += global[6 + i];
            }

            if (_beamLoads.TryGetValue((loadCase, beam), out var kept))
                for (var i = 0; i < 12; i++) kept[i] += local[i];
            else _beamLoads[(loadCase, beam)] = local;
        }

        void OnPanel(AnalyticalPanel panel, V3 perMm2)
        {
            foreach (var (_, shell) in _panelShells.Where(entry => ReferenceEquals(entry.Panel, panel)))
            {
                var s = Solver.Shells[shell];
                var force = perMm2 * (TriangleArea(s) / 3);
                foreach (var n in new[] { s.I, s.J, s.K })
                {
                    load[n * 6] += force.X;
                    load[n * 6 + 1] += force.Y;
                    load[n * 6 + 2] += force.Z;
                }
            }
        }

        // Its own weight: the members' sections and the plates' thickness, at their densities.
        if (loadCase.Nature == LoadNature.SelfWeight)
        {
            foreach (var (member, beam, _, _, _) in _memberBeams)
                OnBeam(beam, new V3(0, 0, -member.Section.Area * member.Section.Material.Density * Gravity * 1e-9), 1);
            foreach (var panel in Model.Panels)
                OnPanel(panel, new V3(0, 0, -panel.SelfWeight * 1e-3));
        }

        foreach (var surface in Loading.SurfaceLoads.Where(entry => entry.Case == loadCase))
            OnPanel(surface.Panel, new V3(surface.Force.X, surface.Force.Y, surface.Force.Z) * 1e-3);

        foreach (var line in Loading.LineLoads.Where(entry => entry.Case == loadCase))
        {
            var perMm = new V3(line.Force.X, line.Force.Y, line.Force.Z);
            if (line.Member is { } member)
            {
                foreach (var (owner, beam, from, to, _) in _memberBeams.Where(entry => ReferenceEquals(entry.Member, member)))
                {
                    var overlap = Math.Min(to, line.End) - Math.Max(from, line.Start);
                    OnBeam(beam, perMm, overlap / Math.Max(1e-9, to - from));
                }
            }
            else if (line.From is { } start && line.To is { } end)
            {
                // Along a plate's edge: each piece's share to the nodes at its ends.
                var divided = Divide(start.At, end.At);
                var along = 0.0;
                for (var i = 0; i + 1 < divided.Count; i++)
                {
                    var length = AnalyticalModel.Distance(divided[i], divided[i + 1]);
                    var overlap = Math.Min(along + length, line.End) - Math.Max(along, line.Start);
                    along += length;
                    if (overlap <= 0) continue;
                    var half = perMm * (overlap / 2);
                    foreach (var n in new[] { Node(divided[i]), Node(divided[i + 1]) })
                    {
                        load[n * 6] += half.X;
                        load[n * 6 + 1] += half.Y;
                        load[n * 6 + 2] += half.Z;
                    }
                }
            }
        }

        return load;
    }

    // ---- results ------------------------------------------------------------------------------

    /// <summary>The cases of a combination and their factors - a case on its own its own factor of one.</summary>
    private static IReadOnlyList<(LoadCase Case, double Factor)> Terms(object loading) => loading switch
    {
        LoadCombination combination => combination.Terms,
        LoadCase loadCase => new[] { (loadCase, 1.0) },
        IReadOnlyList<(LoadCase, double)> terms => terms,
        _ => Array.Empty<(LoadCase, double)>()
    };

    private double[] Sum(IReadOnlyList<(LoadCase Case, double Factor)> terms, Func<LoadCase, double[]?> of, int length)
    {
        var sum = new double[length];
        foreach (var (loadCase, factor) in terms)
            if (of(loadCase) is { } values)
                for (var i = 0; i < length; i++) sum[i] += factor * values[i];
        return sum;
    }

    /// <summary>Every node's movement under a combination or a case, six to a node, mm and radians.</summary>
    public double[] DisplacementsOf(object loading) => Sum(Terms(loading), loadCase => _displacements.GetValueOrDefault(loadCase), Solver.NodeCount * 6);

    /// <summary>How far a node of the model moves, mm, under a combination or a case: its movement in the building's axes.</summary>
    public V3 Movement(AnalyticalNode node, object loading)
    {
        var u = DisplacementsOf(loading);
        var n = _nodeOf[node];
        return new V3(u[n * 6], u[n * 6 + 1], u[n * 6 + 2]);
    }

    /// <summary>The mesh's beams of a member, in order along it, and where each starts and ends along it, mm.</summary>
    public IEnumerable<(int Beam, double From, double To, int Span)> BeamsOf(AnalyticalMember member) =>
        _memberBeams.Where(entry => ReferenceEquals(entry.Member, member)).Select(entry => (entry.Beam, entry.From, entry.To, entry.Span));

    /// <summary>The mesh's triangles of a plate.</summary>
    public IEnumerable<int> ShellsOf(AnalyticalPanel panel) => _panelShells.Where(entry => ReferenceEquals(entry.Panel, panel)).Select(entry => entry.Shell);

    /// <summary>A beam's end forces under a combination or a case, in its own axes, N and N mm.</summary>
    public double[] BeamForces(int beam, object loading)
    {
        var sum = new double[12];
        foreach (var (loadCase, factor) in Terms(loading))
        {
            var forces = Solver.BeamEndForces(Solver.Beams[beam], _displacements[loadCase], _beamLoads.GetValueOrDefault((loadCase, beam)));
            for (var i = 0; i < 12; i++) sum[i] += factor * forces[i];
        }

        return sum;
    }

    /// <summary>
    /// A member under a combination or a case: its greatest axial force, shears, torsion and moments
    /// along it, kN and kNm, and its greatest deflection from the line between the ends of each span.
    /// </summary>
    public MemberResult MemberOf(AnalyticalMember member, object loading, double? limit = null, string limitSaid = "")
    {
        double axial = 0, vy = 0, vz = 0, torsion = 0, my = 0, mz = 0;
        foreach (var (beam, _, _, _) in BeamsOf(member))
        {
            var f = BeamForces(beam, loading);
            double Bigger(double was, double a, double b) => Math.Abs(a) > Math.Abs(was) || Math.Abs(b) > Math.Abs(was) ? (Math.Abs(a) >= Math.Abs(b) ? a : b) : was;
            axial = Bigger(axial, -f[0], f[6]);
            vy = Bigger(vy, f[1], -f[7]);
            vz = Bigger(vz, f[2], -f[8]);
            torsion = Bigger(torsion, -f[3], f[9]);
            my = Bigger(my, -f[4], f[10]);
            mz = Bigger(mz, -f[5], f[11]);
        }

        var (deflection, span) = DeflectionOf(member, loading);
        return new MemberResult(member, axial / 1000, vy / 1000, vz / 1000, torsion / 1e6, my / 1e6, mz / 1e6, deflection, span, limit, limitSaid);
    }

    /// <summary>
    /// A member's greatest deflection from the straight line between the ends of the span it is in, mm, and that span's length. A span runs between what holds the member up: not every
    /// node on it, or a beam with joists landing on it would be measured between its joists, and read as not bending at all.
    /// </summary>
    public (double Deflection, double Span) DeflectionOf(AnalyticalMember member, object loading)
    {
        var u = DisplacementsOf(loading);
        V3 Moved(int node) => new(u[node * 6], u[node * 6 + 1], u[node * 6 + 2]);
        var ends = Enumerable.Range(0, member.Nodes.Count).Where(i => i == 0 || i == member.Nodes.Count - 1 || HoldsUp(member, member.Nodes[i])).ToList();
        var worst = (Deflection: 0.0, Span: 0.0);
        for (var span = 0; span + 1 < ends.Count; span++)
        {
            var (a, b) = (member.Nodes[ends[span]], member.Nodes[ends[span + 1]]);
            var length = AnalyticalModel.Distance(a.At, b.At);
            if (length < 1) continue;
            var (ua, ub) = (Moved(_nodeOf[a]), Moved(_nodeOf[b]));
            var axis = (V3.Of(b.At) - V3.Of(a.At)).Unit;
            for (var stretch = ends[span]; stretch < ends[span + 1]; stretch++)
                foreach (var point in Divide(member.Nodes[stretch].At, member.Nodes[stretch + 1].At))
                {
                    var t = AnalyticalModel.Distance(a.At, point) / length;
                    var relative = Moved(Node(point)) - (ua * (1 - t) + ub * t);
                    var across = relative - axis * relative.Dot(axis);
                    if (across.Length > worst.Deflection) worst = (across.Length, length);
                }
        }

        return worst.Span > 0 ? worst : (0, ends.Count > 2 ? AnalyticalModel.Distance(member.Nodes[ends[0]].At, member.Nodes[ends[1]].At) : member.Length);
    }

    /// <summary>
    /// Whether a node along a member holds it up: a support, a column, a wall going down from it, or a member it crosses. Something ending on it there is what it carries.
    /// </summary>
    private bool HoldsUp(AnalyticalMember member, AnalyticalNode node)
    {
        if (Model.Supports.Any(support => ReferenceEquals(support.Node, node))) return true;
        if (Model.Panels.Any(panel => panel.Kind == AnalyticalPanelKind.Wall && (panel.Outline.Contains(node) || panel.Inside.Contains(node)) && panel.Outline.Min(each => each.At.Z) < node.At.Z - 100)) return true;
        return Model.Members.Any(other => !ReferenceEquals(other, member) && other.Nodes.Contains(node)
            && (other.Kind == AnalyticalMemberKind.Column || (!ReferenceEquals(other.Start, node) && !ReferenceEquals(other.End, node))));
    }

    /// <summary>A support's reactions under a combination or a case, kN and kNm: the forces at its nodes the structure cannot carry itself.</summary>
    /// <summary>
    /// What a line of support takes at each of its nodes under a loading, up positive, kN, with how far along the panel's foot each is, mm, in order: what it comes to in one stretch and not on average.
    /// A node a strip shares with another at a corner is shared with it, as <see cref="ReactionOf"/> shares it.
    /// </summary>
    public IReadOnlyList<(double At, double Down)> LineOf(AnalyticalSupport support, object loading)
    {
        var nodes = _supportNodes.First(entry => ReferenceEquals(entry.Support, support)).Nodes;
        var terms = Terms(loading);
        var line = new List<(double, double)>();
        foreach (var n in nodes)
        {
            var share = 1.0 / _supportNodes.Count(entry => entry.Nodes.Contains(n));
            var down = terms.Sum(term => term.Factor * (_internal[term.Case][n * 6 + 2] - _loads[term.Case][n * 6 + 2]));
            var at = support.Panel?.Flat(Solver.NodeAt(n)).X ?? 0;
            line.Add((at, share * down / 1000));
        }

        return line.OrderBy(entry => entry.Item1).ToList();
    }

    public SupportResult ReactionOf(AnalyticalSupport support, object loading)
    {
        // A node two supports hold - a corner two strip footings meet at - is shared between them.
        var nodes = _supportNodes.First(entry => ReferenceEquals(entry.Support, support)).Nodes;
        var r = new double[6];
        foreach (var (loadCase, factor) in Terms(loading))
        {
            var k = _internal[loadCase];
            var f = _loads[loadCase];
            foreach (var n in nodes)
            {
                var share = 1.0 / _supportNodes.Count(entry => entry.Nodes.Contains(n));
                for (var i = 0; i < 6; i++)
                    if (Solver.IsRestrained(n, i)) r[i] += share * factor * (k[n * 6 + i] - f[n * 6 + i]);
            }
        }

        return new SupportResult(support, null, r[0] / 1000, r[1] / 1000, r[2] / 1000, r[3] / 1e6, r[4] / 1e6, r[5] / 1e6);
    }

    /// <summary>What the ground under a plate pushes back with under a combination or a case, kN.</summary>
    public SupportResult GroundOf(AnalyticalPanel panel, object loading)
    {
        var u = DisplacementsOf(loading);
        var r = new double[3];
        foreach (var (node, freedom, stiffness) in Solver.Springs)
            if (_groundNodes.Any(entry => ReferenceEquals(entry.Panel, panel) && entry.Nodes.Any(n => n.Node == node)))
                r[freedom] -= stiffness * u[node * 6 + freedom];
        return new SupportResult(null, panel, r[0] / 1000, r[1] / 1000, r[2] / 1000, 0, 0, 0);
    }

    /// <summary>The loads of a combination or case added up, kN in each direction - down negative.</summary>
    public V3 TotalLoad(object loading)
    {
        var f = Sum(Terms(loading), loadCase => _loads.GetValueOrDefault(loadCase), Solver.NodeCount * 6);
        var total = new V3(0, 0, 0);
        for (var n = 0; n < Solver.NodeCount; n++) total += new V3(f[n * 6], f[n * 6 + 1], f[n * 6 + 2]);
        return total * 0.001;
    }

    /// <summary>The reactions of a combination or case added up - every support and the ground - kN in each direction.</summary>
    public V3 TotalReaction(object loading)
    {
        var total = new V3(0, 0, 0);
        foreach (var support in Model.Supports)
        {
            var r = ReactionOf(support, loading);
            total += new V3(r.Fx, r.Fy, r.Fz);
        }

        foreach (var (panel, _) in _groundNodes)
        {
            var r = GroundOf(panel, loading);
            total += new V3(r.Fx, r.Fy, r.Fz);
        }

        return total + CrossWallsOf(loading);
    }

    /// <summary>What the cross walls push back with under a combination or a case, kN in each direction.</summary>
    public V3 CrossWallsOf(object loading)
    {
        if (_crossNodes.Count == 0) return new V3(0, 0, 0);
        var u = DisplacementsOf(loading);
        var r = new double[3];
        foreach (var (node, freedom, stiffness) in Solver.Springs)
            if (_crossNodes.Contains(node) && !_groundNodes.Any(entry => entry.Nodes.Any(n => n.Node == node)))
                r[freedom] -= stiffness * u[node * 6 + freedom];
        return new V3(r[0] / 1000, r[1] / 1000, r[2] / 1000);
    }

    /// <summary>
    /// What turns a triangle's moments, by the solver's sign, into a plate's, sagging positive - the side away from its normal in tension. A plate's
    /// normal is up for a floor; the solver's moments are positive the other way, and a triangle's axes follow the order of its corners.
    /// </summary>
    public static double SaggingSign(V3 triangleNormal, AnalyticalPanel panel) =>
        triangleNormal.Dot(new V3(panel.Normal.X, panel.Normal.Y, panel.Normal.Z)) >= 0 ? -1 : 1;

    /// <summary>A plate under a combination or a case: its greatest movement, mm, and its greatest and least moments across each of its ways, kNm per m.</summary>
    public PanelResult PanelOf(AnalyticalPanel panel, object loading)
    {
        var u = DisplacementsOf(loading);
        var shells = ShellsOf(panel).ToList();
        var nodes = shells.SelectMany(s => new[] { Solver.Shells[s].I, Solver.Shells[s].J, Solver.Shells[s].K }).Distinct();
        var deflection = nodes.Select(n => new V3(u[n * 6], u[n * 6 + 1], u[n * 6 + 2]).Dot(new V3(panel.Normal.X, panel.Normal.Y, panel.Normal.Z))).DefaultIfEmpty(0)
            .OrderByDescending(Math.Abs).First();
        double mu = 0, mv = 0, lu = 0, lv = 0;
        var (pu, pv) = (new V3(panel.U.X, panel.U.Y, panel.U.Z), new V3(panel.V.X, panel.V.Y, panel.V.Z));
        foreach (var s in shells)
        {
            var shell = Solver.Shells[s];
            var m = Solver.ShellMoments(shell, u);
            var (x, y, z) = Solver.Axes(shell);
            var sagging = SaggingSign(z, panel);
            double Along(V3 e) => sagging * (m[0] * Math.Pow(e.Dot(x), 2) + m[1] * Math.Pow(e.Dot(y), 2) + 2 * m[2] * e.Dot(x) * e.Dot(y));
            var (alongU, alongV) = (Along(pu), Along(pv));
            mu = Math.Max(mu, alongU);
            mv = Math.Max(mv, alongV);
            lu = Math.Min(lu, alongU);
            lv = Math.Min(lv, alongV);
        }

        return new PanelResult(panel, deflection, mu / 1000, mv / 1000, lu / 1000, lv / 1000);
    }

    // ---- deflection limits -----------------------------------------------------------------

    /// <summary>The earthquake's forces and drifts, where the project has seismic design to do.</summary>
    public SeismicResult? Seismic { get; private set; }

    /// <summary>
    /// Each earthquake case's load: V = Cs W, (12.8-1), W the dead load above the base, at each node in the proportion of its weight by its height above the base to the power k, (12.8-11) and (12.8-12), so what is
    /// lumped at a floor in the standard is here where the mass is. What each level comes to, each node counted at the level nearest it, and its diaphragm's force, (12.10-1) to (12.10-3).
    /// Each accidental torsion case, 12.8.4.2: at each level the torque of its force 5% of its width across the forces off its centre of mass, as remembered, spread over its nodes as their mass would turn.
    /// </summary>
    private SeismicResult? SeismicLoads(BimDocument document)
    {
        if (Loading.Seismic is not { Applies: true } quake) return null;
        var ground = Wind.Ground(document);
        var permanent = Loading.Cases.Where(loadCase => loadCase.Permanent).ToList();
        var count = Solver.NodeCount;
        var (weights, heights) = (new double[count], new double[count]);
        for (var n = 0; n < count; n++)
        {
            weights[n] = Math.Max(0, -permanent.Sum(loadCase => _loads[loadCase][n * 6 + 2])) / 1000;
            heights[n] = Math.Max(0, Solver.NodeAt(n).Z - ground) / 1000;
        }

        var above = Enumerable.Range(0, count).Where(n => heights[n] > 0.01).ToList();
        var weight = above.Sum(n => weights[n]);
        var shear = quake.Coefficient * weight;
        var lifted = above.Sum(n => weights[n] * Math.Pow(heights[n], quake.Exponent));
        var forces = new double[count];
        if (lifted > 0)
            foreach (var n in above)
                forces[n] = shear * weights[n] * Math.Pow(heights[n], quake.Exponent) / lifted;

        // Levels at one height are one floor: a project's Roof and an Eaves drawn at it, say. Each node above the base is counted at the level nearest it.
        var storeys = new List<(string Name, double Elevation)>();
        foreach (var level in document.Levels.Where(level => level.Elevation > ground + 100).OrderBy(level => level.Elevation))
            if (storeys.Count > 0 && level.Elevation - storeys[^1].Elevation < 50) storeys[^1] = ($"{storeys[^1].Name}, {level.Name}", storeys[^1].Elevation);
            else storeys.Add((level.Name, level.Elevation));
        var levelOf = new int[count];
        Array.Fill(levelOf, -1);
        if (storeys.Count > 0)
            foreach (var n in above)
                levelOf[n] = storeys.Select((storey, index) => (Distance: Math.Abs(storey.Elevation - Solver.NodeAt(n).Z), Index: index)).MinBy(each => each.Distance).Index;

        foreach (var loadCase in Loading.Cases.Where(loadCase => loadCase.Nature == LoadNature.Seismic))
        {
            var load = new double[count * 6];
            var way = loadCase.Wind ?? Vector2D.UnitX;
            if (!loadCase.Torsion)
                foreach (var n in above)
                {
                    load[n * 6] = forces[n] * 1000 * way.X;
                    load[n * 6 + 1] = forces[n] * 1000 * way.Y;
                }
            else
                for (var index = 0; index < storeys.Count; index++)
                {
                    var mine = above.Where(n => levelOf[n] == index).ToList();
                    var mass = mine.Sum(n => weights[n]);
                    if (mass <= 0) continue;
                    var (x, y) = (mine.Sum(n => weights[n] * Solver.NodeAt(n).X) / mass, mine.Sum(n => weights[n] * Solver.NodeAt(n).Y) / mass);
                    var across = Math.Abs(way.X) > 0.5 ? mine.Max(n => Solver.NodeAt(n).Y) - mine.Min(n => Solver.NodeAt(n).Y) : mine.Max(n => Solver.NodeAt(n).X) - mine.Min(n => Solver.NodeAt(n).X);
                    var torque = mine.Sum(n => forces[n]) * 1000 * 0.05 * across;
                    var turning = mine.Sum(n => weights[n] * (Math.Pow(Solver.NodeAt(n).X - x, 2) + Math.Pow(Solver.NodeAt(n).Y - y, 2)));
                    if (turning <= 0) continue;
                    foreach (var n in mine)
                    {
                        var share = torque / turning * weights[n];
                        load[n * 6] -= share * (Solver.NodeAt(n).Y - y);
                        load[n * 6 + 1] += share * (Solver.NodeAt(n).X - x);
                    }
                }

            _loads[loadCase] = load;
        }

        var rows = new List<SeismicLevel>();
        for (var index = 0; index < storeys.Count; index++)
        {
            var mine = above.Where(n => levelOf[n] == index).ToList();
            if (mine.Count == 0) continue;
            rows.Add(new SeismicLevel(storeys[index].Name, storeys[index].Elevation, mine.Sum(n => weights[n]), mine.Sum(n => forces[n])));
        }

        for (var i = 0; i < rows.Count; i++)
            rows[i] = rows[i] with
            {
                Diaphragm = Architecture.Seismic.DiaphragmForce(rows.Skip(i).Sum(row => row.Force), rows.Skip(i).Sum(row => row.Weight), rows[i].Weight, quake.Sds, quake.Importance)
            };
        return new SeismicResult(weight, shear, rows, Array.Empty<SeismicDrift>());
    }

    /// <summary>
    /// The earthquake cases' movement for drift. Concrete and masonry cracked, 12.7.3: concrete at ACI 318-19 Table 6.6.3.1.1(a)'s shares, masonry at half, as second order takes them; steel and
    /// timber as they are. Where nothing is concrete or masonry, the movement already solved.
    /// </summary>
    private (Dictionary<LoadCase, double[]> Moved, bool Cracked) ForDrift()
    {
        var cases = Loading.Cases.Where(loadCase => loadCase.Nature == LoadNature.Seismic).ToList();
        static bool Cracks(AnalyticalMaterial material) => material.Kind is AnalyticalMaterialKind.Concrete or AnalyticalMaterialKind.Masonry;
        if (!Model.Members.Any(member => Cracks(member.Section.Material)) && !Model.Panels.Any(panel => Cracks(panel.Material)))
            return (cases.ToDictionary(loadCase => loadCase, loadCase => _displacements[loadCase]), false);

        var (beamShares, shellShares) = (Enumerable.Repeat(1.0, Solver.Beams.Count).ToArray(), Enumerable.Repeat(1.0, Solver.Shells.Count).ToArray());
        foreach (var member in Model.Members.Where(member => Cracks(member.Section.Material)))
            foreach (var (beam, _, _, _) in BeamsOf(member))
                beamShares[beam] = SecondOrderAnalysis.ShareOf(member.Section.Material, true, member.Kind, null);
        foreach (var panel in Model.Panels.Where(panel => Cracks(panel.Material)))
            foreach (var shell in ShellsOf(panel))
                shellShares[shell] = SecondOrderAnalysis.ShareOf(panel.Material, true, null, panel.Kind);
        var cracked = Solver.Factorise(beamShares, shellShares);
        return (cases.ToDictionary(loadCase => loadCase, loadCase => cracked.Solve(_loads[loadCase])), true);
    }

    /// <summary>
    /// Each storey's design drift, 12.8.6: Cd δxe / Ie at the floor over the same at the floor under, δxe the most any node at that floor moves the case's way, against Table 12.12-1 on its height. The forces
    /// for it are Cs without (12.8-5)'s least, 12.8.6.1, and concrete and masonry cracked. Where the floors are not flexible, each storey's drift at the two edges across the forces, the mass off its place
    /// each way: past 1.2 times their average it is torsionally irregular, past 1.4 extremely, Table 12.3-1 as remembered, and in categories C to F the accidental torsion amplified by Ax, (12.8-14),
    /// in its combinations as well as its drift. The stability coefficient θ, (12.8-16), and past 0.10 the drift increased by 1 / (1 - θ), 12.8.7. Stiffness and weight against the storeys round it,
    /// Table 12.3-2's soft storey and weight irregularity, as remembered.
    /// </summary>
    private SeismicResult Drifts(BimDocument document, SeismicDesign quake, SeismicResult levels)
    {
        var ground = Wind.Ground(document);
        var floors = levels.Levels.Select(level => (level.Name, level.Elevation)).ToList();
        var limit = Architecture.Seismic.DriftLimit(quake.System, floors.Count, quake.Risk);
        var thetaMax = Math.Min(0.25, 0.5 / quake.System.Cd);
        // Px is all the vertical load at and above the storey with no factor over 1.0: dead and live.
        var gravity = Loading.Cases.Where(loadCase => loadCase.Permanent || loadCase.Nature == LoadNature.Imposed).ToList();
        var scale = quake.Coefficient > 0 && quake.DriftCoefficient > 0 ? Math.Min(1, quake.DriftCoefficient / quake.Coefficient) : 1;
        var (moved, cracked) = ForDrift();
        var atLevel = floors.Select(floor => Enumerable.Range(0, Solver.NodeCount).Where(n => Math.Abs(Solver.NodeAt(n).Z - floor.Elevation) <= 150).ToList()).ToList();
        var earthquakes = Loading.Cases.Where(loadCase => loadCase.Nature == LoadNature.Seismic && !loadCase.Torsion).ToList();
        LoadCase? TorsionOf(LoadCase earthquake) =>
            Loading.Cases.FirstOrDefault(each => each.Torsion && Math.Abs(each.Wind!.Value.Dot(earthquake.Wind ?? Vector2D.UnitX)) > 0.5);
        double Along(double[] u, int n, Vector2D way) => u[n * 6] * way.X + u[n * 6 + 1] * way.Y;

        // First the torsional irregularity, Ax taken as 1: each storey's drift at its two edges across the forces.
        var ratios = new Dictionary<(LoadCase, int), double>();
        var amplified = new Dictionary<LoadCase, double>();
        foreach (var earthquake in earthquakes)
        {
            if (TorsionOf(earthquake) is not { } torsion) continue;
            var way = earthquake.Wind ?? Vector2D.UnitX;
            var across = new Vector2D(-way.Y, way.X);
            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var (belowA, belowB) = (0.0, 0.0);
                for (var level = 0; level < floors.Count; level++)
                {
                    var nodes = atLevel[level];
                    if (nodes.Count == 0) continue;
                    double Across(int n) => Solver.NodeAt(n).X * across.X + Solver.NodeAt(n).Y * across.Y;
                    var (low, high) = (nodes.Min(Across), nodes.Max(Across));
                    double Edge(IEnumerable<int> edge) => edge.Average(n => Along(moved[earthquake], n, way) + sign * Along(moved[torsion], n, way));
                    var (a, b) = (Edge(nodes.Where(n => Across(n) <= low + 300)), Edge(nodes.Where(n => Across(n) >= high - 300)));
                    var (driftA, driftB) = (Math.Abs(a - belowA), Math.Abs(b - belowB));
                    var ratio = driftA + driftB > 1e-6 ? Math.Max(driftA, driftB) / ((driftA + driftB) / 2) : 1;
                    ratios[(earthquake, level)] = Math.Max(ratios.GetValueOrDefault((earthquake, level), 1), ratio);
                    if (ratio > 1.2)
                        amplified[torsion] = Math.Max(amplified.GetValueOrDefault(torsion, 1), Architecture.Seismic.TorsionAmplification(Math.Max(Math.Abs(a), Math.Abs(b)), (Math.Abs(a) + Math.Abs(b)) / 2));
                    (belowA, belowB) = (a, b);
                }
            }
        }

        foreach (var torsion in Loading.Cases.Where(loadCase => loadCase.Torsion))
            Loading.Amplify(torsion, quake.Amplifies ? amplified.GetValueOrDefault(torsion, 1) : 1);

        // Then each storey's design drift, its accidental torsion amplified, the worse way.
        var drifts = new List<SeismicDrift>();
        var stiffness = new Dictionary<LoadCase, double[]>();
        var leaning = new Dictionary<LoadCase, double[]>();
        foreach (var earthquake in earthquakes)
        {
            var way = earthquake.Wind ?? Vector2D.UnitX;
            var torsion = TorsionOf(earthquake);
            var ax = torsion is null ? 1 : Loading.AmplificationOf(torsion);
            var signs = torsion is null ? new[] { 0.0 } : new[] { 1.0, -1.0 };
            var below = signs.ToDictionary(sign => sign, _ => 0.0);
            var (under, plain) = (ground, 0.0);
            stiffness[earthquake] = new double[floors.Count];
            leaning[earthquake] = new double[floors.Count];
            for (var level = 0; level < floors.Count; level++)
            {
                var (name, elevation) = floors[level];
                var nodes = atLevel[level];
                double At(double sign) => nodes.Select(n => scale * (Along(moved[earthquake], n, way) + (torsion is null ? 0 : sign * ax * Along(moved[torsion], n, way)))).DefaultIfEmpty(0).Max();
                var worst = signs.MaxBy(sign => At(sign) - below[sign]);
                var at = At(worst);
                var height = elevation - under;
                var storey = Enumerable.Range(0, Solver.NodeCount).Where(n => Solver.NodeAt(n).Z > under + 150).ToList();
                var px = storey.Sum(n => -gravity.Sum(each => _loads[each][n * 6 + 2])) / 1000;
                var vx = Math.Abs(storey.Sum(n => Along(_loads[earthquake], n, way))) / 1000;
                var drift = quake.System.Cd * (at - below[worst]) / quake.Importance;
                var theta = vx > 0 && height > 0 ? px * drift * quake.Importance / (scale * vx * height * quake.System.Cd) : 0;
                var increase = Architecture.Seismic.DriftIncrease(theta);
                var ratio = ratios.GetValueOrDefault((earthquake, level), 1);
                var label = torsion is null ? earthquake.Name : $"{earthquake.Name} {(worst > 0 ? "+" : "-")} {(ax > 1 ? $"{ax:0.00} " : string.Empty)}{torsion.Name}";
                drifts.Add(new SeismicDrift(label, name, height, at, drift * increase, limit * height, theta, thetaMax, ratio, Architecture.Seismic.TorsionalIrregularity(ratio), ax, increase));

                // Its stiffness, the storey's shear over how far it moves under the earthquake alone.
                var centre = nodes.Select(n => Along(_displacements[earthquake], n, way)).DefaultIfEmpty(0).Average();
                stiffness[earthquake][level] = centre - plain > 1e-9 ? vx / (centre - plain) : double.PositiveInfinity;
                leaning[earthquake][level] = height > 0 ? (centre - plain) / height : 0;
                foreach (var sign in signs) below[sign] = At(sign);
                (under, plain) = (elevation, centre);
            }
        }

        // 12.3.2.2's exceptions, as remembered: soft storeys and weight are not looked for in one storey, nor two in categories B to D, nor where no storey drifts more than 1.3 times the one over
        // it, the top two not compared.
        var irregular = new List<string>();
        var few = floors.Count == 1 || (floors.Count == 2 && quake.Category is "B" or "C" or "D");
        bool Even(LoadCase earthquake) => !Enumerable.Range(0, Math.Max(0, floors.Count - 2)).Any(level => leaning[earthquake][level] > 1.3 * leaning[earthquake][level + 1]);
        foreach (var (earthquake, k) in stiffness.Where(each => !few && !Even(each.Key)))
            for (var level = 0; level + 1 < floors.Count; level++)
            {
                var upper = k.Skip(level + 1).Take(3).ToList();
                var next = k[level + 1];
                if (!double.IsFinite(k[level]) || upper.Any(each => !double.IsFinite(each))) continue;
                var three = upper.Count == 3 ? upper.Average() : double.NaN;
                var kind = k[level] < 0.6 * next || k[level] < 0.7 * three ? "1b, extreme soft storey" : k[level] < 0.7 * next || k[level] < 0.8 * three ? "1a, soft storey" : null;
                if (kind is not null)
                    irregular.Add($"{floors[level].Name}, {earthquake.Name}: vertical irregularity {kind}, its stiffness {k[level]:0} kN/mm against {next:0} over it (Table 12.3-2, as remembered).");
            }

        for (var level = 0; level < levels.Levels.Count && !few && !stiffness.Keys.All(Even); level++)
        {
            var weight = levels.Levels[level].Weight;
            var neighbours = new List<double>();
            if (level > 0) neighbours.Add(levels.Levels[level - 1].Weight);
            if (level + 1 < levels.Levels.Count && level + 1 < levels.Levels.Count - 1) neighbours.Add(levels.Levels[level + 1].Weight);
            if (neighbours.Any(other => weight > 1.5 * other))
                irregular.Add($"{levels.Levels[level].Name}: vertical irregularity 2, weight, {weight:0} kN against {neighbours.Min():0} beside it (Table 12.3-2, as remembered; a lighter roof is not counted).");
        }

        foreach (var drift in drifts.Where(drift => drift.Irregular.Length > 0).GroupBy(drift => (drift.Level, drift.Irregular)).Select(group => group.First()))
            irregular.Add($"{drift.Level}: horizontal irregularity {drift.Irregular}, {(drift.Irregular == "1b" ? "extreme torsional" : "torsional")}, an edge drifting {drift.Torsion:0.00} times the average of the two (Table 12.3-1, as remembered).");
        return levels with { Drifts = drifts, Irregularities = irregular.Distinct().ToList(), Cracked = cracked, DriftScale = scale };
    }

    /// <summary>The cases loads vary by, for gravity: imposed, roof and snow.</summary>
    public IReadOnlyList<(LoadCase, double)> Variable =>
        Loading.Cases.Where(loadCase => loadCase.Nature is LoadNature.Imposed or LoadNature.RoofImposed or LoadNature.Snow).Select(loadCase => (loadCase, 1.0)).ToList();

    /// <summary>Every load but the wind and an earthquake, at its characteristic value: what a member deflects under in all.</summary>
    public IReadOnlyList<(LoadCase, double)> Total =>
        Loading.Cases.Where(loadCase => !loadCase.Lateral).Select(loadCase => (loadCase, 1.0)).ToList();

    /// <summary>
    /// A beam's deflection limits - under the loads that vary, and under all of them - as its material and
    /// the project's standard set them: span over so much, and where it comes from. A column has none.
    /// </summary>
    public static ((double Limit, string Said)? Variable, (double Limit, string Said)? Total) LimitsOf(BimDocument document, AnalyticalMember member)
    {
        if (member.Kind is AnalyticalMemberKind.Column) return (null, null);
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return ((360, "L/360 under live load, IBC Table 1604.3 (floor members)"), (240, "L/240 under dead and live load, IBC Table 1604.3"));
        return member.Section.Material.Kind switch
        {
            AnalyticalMaterialKind.Steel => ((360, "span/360 under imposed load, for beams carrying plaster (UK National Annex to BS EN 1993-1-1, as quoted)"),
                (200, "span/200 in all (UK National Annex to BS EN 1993-1-1, as quoted)")),
            AnalyticalMaterialKind.Timber => (null, (250, "span/250 net final (UK National Annex to BS EN 1995-1-1, Table NA.5, as quoted)")),
            AnalyticalMaterialKind.Concrete => (null, (250, "span/250 (BS EN 1992-1-1, 7.4.1(4))")),
            _ => (null, (250, "span/250"))
        };
    }

    /// <summary>Each member's results for deflection: under the loads that vary, and under all, against its limits.</summary>
    public IReadOnlyList<MemberResult> DeflectionChecks(BimDocument document)
    {
        var results = new List<MemberResult>();
        foreach (var member in Model.Members.Where(member => member.Kind != AnalyticalMemberKind.Column))
        {
            var (variable, total) = LimitsOf(document, member);
            if (variable is { } v) results.Add(MemberOf(member, Variable, v.Limit, v.Said));
            if (total is { } t) results.Add(MemberOf(member, Total, t.Limit, t.Said));
        }

        return results;
    }

    // ---- the ground under a raft -------------------------------------------------------------

    /// <summary>
    /// The pressure the ground is presumed to bear, kPa, and where that comes from: in the UK the least
    /// of BS 8004:1986 Table 1's range for it - medium dense gravel 200 to 600, medium dense sand 100 to
    /// 300, stiff clay 150 to 300, firm clay 75 to 150 - values for a first sizing, which Eurocode 7
    /// still admits for it (BS EN 1997-1 Annex G). Rock is as strong as its kind and how weathered it
    /// is, and loose sand and soft clay have no least value: those are for a site investigation. In the
    /// US the IRC's presumptive values (Table R401.4.1): sedimentary rock 4,000 psf, gravel 3,000, sand
    /// 2,000, clay and silt 1,500 - below that, a soils investigation (R401.4.1).
    /// </summary>
    public static (double? Allowable, string Said) AllowableBearing(BimDocument document)
    {
        var ground = document.ProjectInformation.Ground;
        if (document.ProjectInformation.ClearanceStandard.IsUs())
        {
            const double psf = 0.0478803;
            return ground switch
            {
                GroundType.Rock => (4000 * psf, "4,000 psf for sedimentary rock, IRC Table R401.4.1"),
                GroundType.Gravel => (3000 * psf, "3,000 psf for gravel and sandy gravel (GW, GP), IRC Table R401.4.1"),
                GroundType.Sand or GroundType.LooseSand => (2000 * psf, "2,000 psf for sand, silty and clayey sand and gravel (SW, SP, SM, SC, GM, GC), IRC Table R401.4.1"),
                GroundType.StiffClay or GroundType.FirmClay => (1500 * psf, "1,500 psf for clay and silt (CL, ML, MH, CH), IRC Table R401.4.1"),
                _ => (null, "Soft ground, under the IRC's least presumptive value of 1,500 psf: R401.4.1 asks a soils investigation for it")
            };
        }

        return ground switch
        {
            GroundType.Gravel => (200, "200 kPa, the least for medium dense gravel, BS 8004:1986 Table 1"),
            GroundType.Sand => (100, "100 kPa, the least for medium dense sand, BS 8004:1986 Table 1"),
            GroundType.StiffClay => (150, "150 kPa, the least for stiff clay, BS 8004:1986 Table 1"),
            GroundType.FirmClay => (75, "75 kPa, the least for firm clay, BS 8004:1986 Table 1"),
            GroundType.Rock => (null, "Rock bears as its kind and weathering let it: BS 8004:1986 Table 1 gives it rock by rock - a site investigation to say"),
            GroundType.LooseSand => (null, "Loose sand bears under 100 kPa, BS 8004:1986 Table 1, with no least value: a site investigation to say"),
            GroundType.SoftClay => (null, "Soft clay bears under 75 kPa, BS 8004:1986 Table 1, with no least value: a site investigation to say"),
            _ => (null, "Very soft clay or silt has no presumed value in BS 8004:1986 Table 1: a site investigation and specialist advice")
        };
    }

    /// <summary>
    /// The pressure under each plate on the ground, under every load but the wind at its characteristic
    /// value - what a presumed bearing value is taken against: at each node the ground's stiffness times
    /// how far it goes down, MN/m³ by mm being kPa.
    /// </summary>
    public IReadOnlyList<BearingResult> BearingChecks(BimDocument document)
    {
        var (allowable, said) = AllowableBearing(document);
        var u = DisplacementsOf(Total);
        var results = new List<BearingResult>();
        foreach (var (panel, nodes) in _groundNodes)
        {
            var k = panel.Subgrade!.Value;
            var pressures = nodes.Select(entry => -k * u[entry.Node * 6 + 2]).ToList();
            var area = nodes.Sum(entry => entry.Area) / 1e6;
            var average = area > 0 ? GroundOf(panel, Total).Fz / area : 0;
            results.Add(new BearingResult(panel, pressures.DefaultIfEmpty(0).Max(), average, pressures.DefaultIfEmpty(0).Max() / k,
                pressures.Any(p => p < -1), allowable, said));
        }

        return results;
    }

    // ---- ground beams ------------------------------------------------------------------------

    /// <summary>
    /// The greatest sagging and hogging moments along a member over the ultimate combinations, kNm, each
    /// positive: sagging the bottom in tension, hogging the top - by which way its own up is.
    /// </summary>
    public (double Sagging, double Hogging) BendingOf(AnalyticalMember member)
    {
        double sagging = 0, hogging = 0;
        foreach (var combination in Loading.Combinations.Where(combination => combination.Factored))
        foreach (var (beam, _, _, _) in BeamsOf(member))
        {
            var f = BeamForces(beam, combination);
            var up = Solver.Axes(Solver.Beams[beam]).Z.Z >= 0 ? 1 : -1;
            foreach (var moment in new[] { -f[4], f[10] })
            {
                // A moment about the member's y axis puts its +z side in tension when positive.
                var topInTension = moment * up / 1e6;
                hogging = Math.Max(hogging, topInTension);
                sagging = Math.Max(sagging, -topInTension);
            }
        }

        return (sagging, hogging);
    }

    /// <summary>
    /// Each ground beam's bars worked out from its moments, a rectangular section of its width and depth,
    /// the bars the cover cast against the ground and its links in from each face. In the UK by BS EN
    /// 1992-1-1's rectangular stress block (3.1.7) as The Concrete Centre's design guides set it out -
    /// K = M / b d² fck, no more than K' 0.167 without compression steel; z = d [0.5 + √(0.25 - K/1.134)],
    /// no more than 0.95d; As = M / 0.87 fyk z - with RC28/35 concrete, as BS 8500 has reinforced
    /// foundations made, B500 bars, and the least steel 9.2.1.1 asks. In the US by ACI 318-19 22.2 -
    /// φMn = 0.9 As fy (d - a/2), a = As fy / 0.85 f'c b, tension-controlled while c/d is 0.375 or less
    /// (21.2.2) - with f'c 3,000 psi and Grade 60 bars, and the least steel of 9.6.1.2.
    /// </summary>
    public IReadOnlyList<GroundBeamDesign> GroundBeamDesigns(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var designs = new List<GroundBeamDesign>();
        foreach (var member in Model.Members.Where(member => member.Kind == AnalyticalMemberKind.GroundBeam && member.Element is GroundBeam))
        {
            var beam = (GroundBeam)member.Element;
            var (sagging, hogging) = BendingOf(member);
            var links = BarSpec.Parse(beam.Links)?.Diameter ?? 0;
            var bottom = BarSpec.Parse(beam.BottomBars);
            var top = BarSpec.Parse(beam.TopBars);
            double Depth(BarSpec? bars) => beam.Depth - Reinforcement.CoverAgainstGround - links - (bars?.Diameter ?? (us ? 15.9 : 16)) / 2;
            var (bottomDepth, topDepth) = (Depth(bottom), Depth(top));
            var (bottomNeeded, bottomProblem) = us ? Aci(sagging, beam.Width, bottomDepth) : Eurocode(sagging, beam.Width, bottomDepth);
            var (topNeeded, topProblem) = us ? Aci(hogging, beam.Width, topDepth) : Eurocode(hogging, beam.Width, topDepth);
            string? problem = (bottomProblem, topProblem) switch
            {
                (null, null) => null,
                ({ } b, null) => $"Sagging: {b}",
                (null, { } t) => $"Hogging: {t}",
                var (b, t) => $"Sagging: {b}; hogging: {t}"
            };
            designs.Add(new GroundBeamDesign(member, beam, sagging, hogging, bottomDepth, topDepth, bottomNeeded, Given(bottom), topNeeded, Given(top),
                us ? "ACI 318-19 22.2, f'c 3,000 psi, Grade 60, φ 0.9" : "BS EN 1992-1-1 3.1.7 and 9.2.1.1, RC28/35, B500", problem,
                Least(us, beam.Width, bottomDepth), Least(us, beam.Width, topDepth)));
        }

        return designs;
    }

    private static double Given(BarSpec? bars) => bars is { Count: { } count } ? count * bars.BarArea : 0;

    /// <summary>The least tension steel a beam has, mm²: BS EN 1992-1-1 9.2.1.1's, or in the US ACI 318-19 9.6.1.2's.</summary>
    public static double Least(bool us, double width, double depth) =>
        Math.Max(0, depth) * width * (us ? StructureRules.UsBeamLeastSteel : Math.Max(StructureRules.UkLeastSteel, 0.0013));

    /// <summary>The tension steel a rectangular section needs for a moment by BS EN 1992-1-1, mm² - its least where it is more - or why it cannot.</summary>
    public static (double Needed, string? Problem) Eurocode(double moment, double width, double depth) =>
        ConcreteDesign.Eurocode(moment, width, depth, 28, Least(false, width, depth));

    /// <summary>The tension steel a rectangular section needs for a moment by ACI 318-19, mm² - its least where it is more - or why it cannot.</summary>
    public static (double Needed, string? Problem) Aci(double moment, double width, double depth) =>
        ConcreteDesign.Aci(moment, width, depth, 3000 * 0.00689476, Least(true, width, depth));
}

/// <summary>What a level comes to in an earthquake: its weight, its share of the base shear and the force its diaphragm is designed for, kN.</summary>
public sealed record SeismicLevel(string Name, double Elevation, double Weight, double Force, double Diaphragm = 0);

/// <summary>
/// A storey's drift in an earthquake case: its height, how far its floor moves elastically, the design drift Cd δ / Ie it comes to over the storey under it, and what Table 12.12-1 allows, mm;
/// and its stability coefficient θ = Px Δ Ie / (Vx hsx Cd), (12.8-16), against 0.10, under which P-delta need not be considered, and θmax = 0.5 / (β Cd), (12.8-17), β taken as 1.
/// Where the floors are not flexible, how much more one edge drifts than the average of the two, the torsional irregularity that makes, the amplification Ax its accidental torsion was taken at,
/// and how much P-delta increased its drift by.
/// </summary>
public sealed record SeismicDrift(string Case, string Level, double Height, double Elastic, double Drift, double Allowed, double Theta = 0, double ThetaMax = 0.25,
    double Torsion = 1, string Irregular = "", double Amplification = 1, double Increase = 1)
{
    public double Ratio => Allowed > 0 ? Math.Abs(Drift) / Allowed : 0;
}

/// <summary>
/// The earthquake as the analysis worked it: the effective seismic weight and base shear, kN, each level's share, and each storey's drift; the irregularities found, whether concrete and masonry were
/// cracked for drift, and the share of the strength forces drift was worked from.
/// </summary>
public sealed record SeismicResult(double Weight, double Shear, IReadOnlyList<SeismicLevel> Levels, IReadOnlyList<SeismicDrift> Drifts, IReadOnlyList<string>? Irregularities = null, bool Cracked = false,
    double DriftScale = 1);
