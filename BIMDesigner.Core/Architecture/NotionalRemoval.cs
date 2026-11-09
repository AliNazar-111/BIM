using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One notional removal: what was taken away and from which storey; whether what is left stands, by its load paths or with damage no greater than the limit; the worst of what is left and where it is;
/// the floor at risk at each level it reaches and what that level allows; and, where it does not stand, its checks as a key element.
/// </summary>
public sealed record RemovalCheck(
    string Name, string Storey, bool Stable, int Failed, double Worst, string WorstWhere, IReadOnlyList<(string Level, double Area, double Limit)> AtRisk, bool Stands,
    IReadOnlyList<StrengthCheck> Key, string Said)
{
    /// <summary>Whether it is enough: it stands without it, or as a key element it carries its accidental load.</summary>
    public bool Enough => Stands || (Key.Count > 0 && Key.All(check => check.Ratio <= 1 + 1e-9));
}

/// <summary>A Consequence Class 2b building's notional removals.</summary>
public sealed record RemovalResult(string Said, IReadOnlyList<RemovalCheck> Removals, string Limits);

/// <summary>
/// Approved Document A 5.1d's alternative to vertical ties for a Consequence Class 2b building, as Annex A.4 of BS EN 1991-1-7 has it: each supporting column, one storey at a time, and each beam a column
/// stands on, taken away in turn, the building to stand, and the floor at risk of collapse in each of two adjacent storeys to be no more than 15% of it or 100 m², whichever is less; what does not do
/// that to be a key element, carrying 34 kN/m² on its faces, A.8. The damaged structure is solved under the accidental combination, (6.11b), by the intact stiffness updated for what is taken away -
/// Woodbury's identity over the nodes it shared with the rest - so each removal costs a dozen or so solutions and not a factorisation. What is left is checked as it is: steel and timber by their own
/// checks; concrete taken to hold what its intact design asks of it, or its least steel, and no more, at the accidental partial factors of steel. Where anything fails, the floor that leaned on what was
/// taken away is at risk at each level it supported.
/// </summary>
public static class NotionalRemoval
{
    /// <summary>The accidental design action on a key element, kN/m², A.8 and Approved Document A 5.3.</summary>
    public const double KeyLoad = 34;

    /// <summary>The floor that may be at risk at a level, mm²: 15% of it or 100 m², whichever is less.</summary>
    public static double Limit(double floor) => Math.Min(0.15 * floor, 100e6);

    /// <summary>
    /// The building's notional removals, for a UK building in Consequence Class 2b; null otherwise. Load-bearing walls are not taken away here, their nominal lengths of A.7 being left to the engineer.
    /// </summary>
    public static RemovalResult? Of(BimDocument document, StructuralAnalysis analysis, IReadOnlyList<WallCheck>? wallChecks = null)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;
        var (storeys, _) = FireDesign.HeightOf(document);
        if (Robustness.ClassOf(FireDesign.UseOf(document), storeys) != "2b") return null;

        var (solver, model) = (analysis.Solver, analysis.Model);
        var accidental = FireDesign.Combination(document, analysis.Loading);
        var length = solver.NodeCount * 6;
        double[] Load(LoadCombination combination)
        {
            var b = new double[length];
            foreach (var (loadCase, factor) in combination.Terms)
            {
                var each = analysis.LoadOf(loadCase);
                for (var i = 0; i < length; i++) b[i] += factor * each[i];
            }

            return b;
        }

        var load = Load(accidental);
        var factored = solver.Factorise();

        // What meets each node.
        var beamsAt = Enumerable.Range(0, solver.NodeCount).Select(_ => new List<int>()).ToArray();
        for (var b = 0; b < solver.Beams.Count; b++)
        {
            beamsAt[solver.Beams[b].I].Add(b);
            beamsAt[solver.Beams[b].J].Add(b);
        }

        var shelled = new bool[solver.NodeCount];
        foreach (var shell in solver.Shells) shelled[shell.I] = shelled[shell.J] = shelled[shell.K] = true;
        var shellsAt = Enumerable.Range(0, solver.NodeCount).Select(_ => new List<int>()).ToArray();
        for (var s = 0; s < solver.Shells.Count; s++)
            foreach (var node in new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K })
                shellsAt[node].Add(s);
        var held = new bool[solver.NodeCount];
        for (var n = 0; n < solver.NodeCount; n++)
            for (var f = 0; f < 6; f++)
                held[n] |= solver.IsRestrained(n, f);
        foreach (var (node, _, _) in solver.Springs) held[node] = true;

        // Levels, a level a floor, levels at one height one floor.
        var ground = Wind.Ground(document);
        var levels = new List<(string Name, double Elevation)>();
        foreach (var level in document.Levels.Where(level => level.Elevation > ground + 100).OrderBy(level => level.Elevation))
            if (levels.Count > 0 && level.Elevation - levels[^1].Elevation < 50) levels[^1] = ($"{levels[^1].Name}, {level.Name}", levels[^1].Elevation);
            else levels.Add((level.Name, level.Elevation));
        var bases = document.Levels.OrderBy(level => level.Elevation).ToList();
        if (levels.Count == 0) return null;
        int LevelOf(double z) => levels.Select((level, i) => (Distance: Math.Abs(level.Elevation - z), Index: i)).MinBy(each => each.Distance).Index;
        string StoreyUnder(double top) => bases.LastOrDefault(level => level.Elevation < top - 300)?.Name ?? "the lowest";

        // The floors at each level, their triangles' areas and where they are.
        var floors = model.Panels.Where(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof).Concat(model.Diaphragms).ToList();
        var floorShells = floors.SelectMany(panel => analysis.ShellsOf(panel)).Distinct().Select(s =>
        {
            var shell = solver.Shells[s];
            var (a, b, c) = (solver.NodeAt(shell.I), solver.NodeAt(shell.J), solver.NodeAt(shell.K));
            var (ux, uy, uz, vx, vy, vz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z, c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var area = Math.Sqrt(Math.Pow(uy * vz - uz * vy, 2) + Math.Pow(uz * vx - ux * vz, 2) + Math.Pow(ux * vy - uy * vx, 2)) / 2;
            return (Shell: s, X: (a.X + b.X + c.X) / 3, Y: (a.Y + b.Y + c.Y) / 3, Level: LevelOf((a.Z + b.Z + c.Z) / 3), Area: area);
        }).ToList();
        var floorArea = Enumerable.Range(0, levels.Count).Select(level => floorShells.Where(each => each.Level == level).Sum(each => each.Area)).ToArray();
        var shellAt = floorShells.ToDictionary(each => each.Shell, each => (each.Level, each.Area));

        // What stands under each level: columns, as points, and walls, as lengths on plan.
        var pieces = model.Members.ToDictionary(member => member, member => analysis.BeamsOf(member).ToList());
        var columns = model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column).ToList();
        var under = Enumerable.Range(0, levels.Count).Select(_ => new List<(double X0, double Y0, double X1, double Y1)>()).ToArray();
        foreach (var column in columns)
            foreach (var (beam, _, _, _) in pieces[column])
            {
                var (i, j) = (solver.NodeAt(solver.Beams[beam].I), solver.NodeAt(solver.Beams[beam].J));
                var top = i.Z > j.Z ? i : j;
                var level = LevelOf(top.Z);
                if (Math.Abs(levels[level].Elevation - top.Z) < 400 && !under[level].Any(each => Math.Abs(each.X0 - top.X) < 1 && Math.Abs(each.Y0 - top.Y) < 1))
                    under[level].Add((top.X, top.Y, top.X, top.Y));
            }

        foreach (var wall in model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var top = wall.Outline.Max(node => node.At.Z);
            var level = LevelOf(top);
            if (Math.Abs(levels[level].Elevation - top) >= 400) continue;
            var flat = wall.FlatOutline;
            var (from, to) = (flat.Min(p => p.X), flat.Max(p => p.X));
            under[level].Add((wall.Origin.X + wall.U.X * from, wall.Origin.Y + wall.U.Y * from, wall.Origin.X + wall.U.X * to, wall.Origin.Y + wall.U.Y * to));
        }

        static double Distance(double x, double y, (double X0, double Y0, double X1, double Y1) s)
        {
            var (dx, dy) = (s.X1 - s.X0, s.Y1 - s.Y0);
            var t = dx * dx + dy * dy < 1 ? 0 : Math.Clamp(((x - s.X0) * dx + (y - s.Y0) * dy) / (dx * dx + dy * dy), 0, 1);
            return Math.Sqrt(Math.Pow(x - s.X0 - t * dx, 2) + Math.Pow(y - s.Y0 - t * dy, 2));
        }

        // The floor at a level that leans on what was taken away: nearer it than anything else standing under that level.
        double Tributary(int level, (double X0, double Y0, double X1, double Y1) taken) =>
            floorShells.Where(each => each.Level == level)
                .Where(each => Distance(each.X, each.Y, taken) < under[level].Where(other => Distance((other.X0 + other.X1) / 2, (other.Y0 + other.Y1) / 2, taken) > 1)
                    .Select(other => Distance(each.X, each.Y, other)).DefaultIfEmpty(double.PositiveInfinity).Min())
                .Sum(each => each.Area);

        // A piece's forces under a combination, from a movement, its own load along it taken off.
        double[] PieceForces(int beam, double[] u, LoadCombination combination)
        {
            var (matrix, dofs) = solver.BeamForceMatrix(solver.Beams[beam]);
            var f = new double[12];
            for (var i = 0; i < 12; i++)
            for (var j = 0; j < 12; j++)
                f[i] += matrix[i, j] * u[dofs[j]];
            foreach (var (loadCase, factor) in combination.Terms)
                if (analysis.LoadAlong(loadCase, beam) is { } along)
                {
                    var condensed = solver.CondensedLoad(solver.Beams[beam], along);
                    for (var i = 0; i < 12; i++) f[i] -= factor * condensed[i];
                }

            return f;
        }

        List<(LoadCombination, MemberForces)> Stations(IEnumerable<int> beams, double[] u, LoadCombination combination)
        {
            var stations = new List<(LoadCombination, MemberForces)>();
            foreach (var beam in beams)
            {
                var up = solver.Axes(solver.Beams[beam]).Z.Z;
                bool Top(double my) => Math.Abs(up) > 0.5 && my * up > 0;
                var f = PieceForces(beam, u, combination);
                stations.Add((combination, new MemberForces(-f[0] / 1e3, f[1] / 1e3, f[2] / 1e3, -f[3] / 1e6, -f[4] / 1e6, -f[5] / 1e6, Top(-f[4]))));
                stations.Add((combination, new MemberForces(f[6] / 1e3, -f[7] / 1e3, -f[8] / 1e3, f[9] / 1e6, f[10] / 1e6, f[11] / 1e6, Top(f[10]))));
            }

            return stations;
        }

        // What concrete was designed for: the most of each force over the factored combinations, at first order.
        var factoredCombinations = analysis.Loading.Combinations.Where(combination => combination.Factored).ToList();
        var designed = factoredCombinations.Select(combination => (Combination: combination, Moved: analysis.DisplacementsOf(combination))).ToList();
        var lengths = model.Members.ToDictionary(member => member, member => MemberStrength.LengthsOf(model, member));
        var concreteMembers = model.Members.Where(member => member.Section.Material.Kind == AnalyticalMaterialKind.Concrete).ToList();
        var envelope = concreteMembers.ToDictionary(member => member, member =>
        {
            var all = designed.SelectMany(each => Stations(pieces[member].Select(piece => piece.Beam), each.Moved, each.Combination)).Select(station => station.Item2).ToList();
            return (Compression: all.Select(f => -f.N).DefaultIfEmpty(0).Max(), Tension: all.Select(f => f.N).DefaultIfEmpty(0).Max(), My: all.Select(f => Math.Abs(f.My)).DefaultIfEmpty(0).Max(),
                Mz: all.Select(f => Math.Abs(f.Mz)).DefaultIfEmpty(0).Max(), V: all.Select(f => Math.Max(Math.Abs(f.Vy), Math.Abs(f.Vz))).DefaultIfEmpty(0).Max());
        });

        double ConcreteRatio(AnalyticalMember member, IReadOnlyList<(LoadCombination, MemberForces)> stations)
        {
            var dims = member.Section.Dimensions;
            var (depth, width) = dims.Count >= 2 ? (dims[0], dims[1]) : (Math.Sqrt(member.Section.Area), Math.Sqrt(member.Section.Area));
            var fck = ConcreteDesign.StrengthOf(member.Section.Material.Quality);
            var (d, dWeak) = (Math.Max(depth - 50, 0.5 * depth), Math.Max(width - 50, 0.5 * width));
            var column = member.Kind == AnalyticalMemberKind.Column;
            var e = envelope[member];
            var least = column ? Math.Max(0.002 * depth * width, 0.1 * e.Compression * 1e3 / (500 / 1.15)) : ConcreteDesign.LeastEurocode(width, d, fck);
            var (momentY, momentZ) = (Math.Max(e.My, (column ? 0.5 : 1) * least * 500 * 0.9 * d / 1e6), Math.Max(e.Mz, 0.5 * least * 500 * 0.9 * dWeak / 1e6));
            var k = Math.Min(2, 1 + Math.Sqrt(200 / d));
            var shear = Math.Max(e.V, 0.035 * Math.Pow(k, 1.5) * Math.Sqrt(fck) * width * d / 1e3);
            var pull = Math.Max(e.Tension * 1.15, (column ? least : 2 * least) * 500 / 1e3);
            var push = Math.Max(e.Compression, 0.1 * fck * depth * width / 1e3) * 1.15;
            var forces = stations.Select(station => station.Item2).ToList();
            return new[]
            {
                forces.Max(f => Math.Abs(f.My)) / (momentY * 1.15), forces.Max(f => Math.Abs(f.Mz)) / (momentZ * 1.15), forces.Max(f => Math.Max(Math.Abs(f.Vy), Math.Abs(f.Vz))) / (shear * 1.25),
                forces.Max(f => Math.Max(0, f.N)) / pull, forces.Max(f => Math.Max(0, -f.N)) / push
            }.Max();
        }

        // Concrete floors and roofs: each triangle's Wood-Armer moments, as designed and its least steel, kNm/m.
        var slabs = model.Panels.Where(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof && panel.Material.Kind == AnalyticalMaterialKind.Concrete).ToList();
        var slabShells = slabs.SelectMany(panel => analysis.ShellsOf(panel).Select(shell => (Shell: shell, Panel: panel))).ToList();
        var momentMatrices = slabShells.ToDictionary(each => each.Shell, each => solver.ShellMomentMatrix(solver.Shells[each.Shell]));
        (double, double, double, double) WoodArmerOf(int shell, double[] u)
        {
            var (matrix, dofs) = momentMatrices[shell];
            var m = new double[3];
            for (var r = 0; r < 3; r++)
            for (var j = 0; j < 18; j++)
                m[r] += matrix[r, j] * u[dofs[j]];
            return PlateDesign.WoodArmer(m[0] / 1e3, m[1] / 1e3, m[2] / 1e3);
        }

        var slabCapacity = slabShells.ToDictionary(each => each.Shell, each =>
        {
            var fck = ConcreteDesign.StrengthOf(each.Panel.Material.Quality);
            var d = Math.Max(each.Panel.Thickness - 40, 0.6 * each.Panel.Thickness);
            var least = ConcreteDesign.LeastEurocode(1000, d, fck) * 500 * 0.9 * d / 1e6;
            var most = new double[4];
            foreach (var (_, moved) in designed)
            {
                var (bx, by, tx, ty) = WoodArmerOf(each.Shell, moved);
                most[0] = Math.Max(most[0], bx);
                most[1] = Math.Max(most[1], by);
                most[2] = Math.Max(most[2], -tx);
                most[3] = Math.Max(most[3], -ty);
            }

            return most.Select(m => Math.Max(m, least) * 1.15).ToArray();
        });

        // Walls: each triangle at a wall's foot, the fifth of its height there, in stretches of a metre along it, with how its axes turn up the wall.
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();
        var feet = walls.ToDictionary(wall => wall, wall =>
        {
            var flat = wall.FlatOutline;
            var (first, bottom, height) = (flat.Min(p => p.X), flat.Min(p => p.Y), flat.Max(p => p.Y) - flat.Min(p => p.Y));
            var up = new StructuralSolver.Vector3(wall.V.X, wall.V.Y, wall.V.Z);
            return analysis.ShellsOf(wall).Select(s =>
            {
                var shell = solver.Shells[s];
                var (a, b, c) = (solver.NodeAt(shell.I), solver.NodeAt(shell.J), solver.NodeAt(shell.K));
                var at = wall.Flat(new Geometry.Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3));
                var (x, y, _) = solver.Axes(shell);
                var area = shellAt.TryGetValue(s, out var known) ? known.Area : 0.0;
                var (ux, uy, uz, vx, vy, vz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z, c.X - a.X, c.Y - a.Y, c.Z - a.Z);
                area = Math.Sqrt(Math.Pow(uy * vz - uz * vy, 2) + Math.Pow(uz * vx - ux * vz, 2) + Math.Pow(ux * vy - uy * vx, 2)) / 2;
                return (Shell: s, Along: at.X, Stretch: (int)Math.Floor((at.X - first) / 1000), Foot: at.Y - bottom < height / 5, Area: area, XV: x.Dot(up), YV: y.Dot(up));
            }).ToList();
        });

        // What a wall's foot carries, kN/m, compression positive, each stretch the average over its triangles; taken away triangles left out.
        Dictionary<int, double> Carried(AnalyticalPanel wall, StructuralSolver.Prestress stress, HashSet<int>? gone)
        {
            var sums = new Dictionary<int, (double Force, double Area)>();
            foreach (var each in feet[wall].Where(each => each.Foot && (gone is null || !gone.Contains(each.Shell))))
            {
                var vertical = stress.Nx[each.Shell] * each.XV * each.XV + stress.Ny[each.Shell] * each.YV * each.YV + 2 * stress.Nxy[each.Shell] * each.XV * each.YV;
                var was = sums.GetValueOrDefault(each.Stretch);
                sums[each.Stretch] = (was.Force - vertical * each.Area, was.Area + each.Area);
            }

            return sums.Where(entry => entry.Value.Area > 5e4).ToDictionary(entry => entry.Key, entry => entry.Value.Force / entry.Value.Area);
        }

        // What each can carry: a masonry wall its own checks' least, at γM 1.5 for an accident rather than 3.0, as remembered; any other what it carried as designed, at the accidental factor of
        // its material.
        var designedStress = designed.Select(each => solver.PrestressOf(factored, each.Moved)).ToList();
        var wallCapacity = walls.ToDictionary(wall => wall, wall =>
        {
            var check = wallChecks?.FirstOrDefault(each => ReferenceEquals(each.Panel, wall));
            var masonry = check?.Checks.Where(each => each.Name is "Load at its top or foot" or "Load at mid height").Select(each => each.Capacity).DefaultIfEmpty(0).Min() ?? 0;
            if (wall.Material.Kind == AnalyticalMaterialKind.Masonry && masonry > 0)
                return (Each: (Dictionary<int, double>?)null, Whole: masonry * MasonryDesign.GammaCompression / 1.5);
            var most = new Dictionary<int, double>();
            foreach (var stress in designedStress)
                foreach (var (stretch, force) in Carried(wall, stress, null))
                    most[stretch] = Math.Max(most.GetValueOrDefault(stretch), force);
            var factor = wall.Material.Kind switch { AnalyticalMaterialKind.Concrete => 1.25, AnalyticalMaterialKind.Timber => 1.3, _ => 1.5 };
            var floor = most.Values.DefaultIfEmpty(0).Average();
            return (Each: most.ToDictionary(entry => entry.Key, entry => Math.Max(entry.Value, floor) * factor), Whole: floor * factor);
        });

        // Each removal stands alone, so they are worked side by side: what the solver works out once and keeps, worked before they start.
        var intact = factored.Solve(load);
        solver.PrestressOf(factored, intact);
        var jobs = new List<Func<RemovalCheck>>();
        RemovalCheck Remove(string name, string storey, IReadOnlyList<int> beams, IReadOnlyList<int> shells, (double X0, double Y0, double X1, double Y1) where, int level, AnalyticalMember? keyColumn)
        {
            var removed = beams.ToHashSet();
            var gone = shells.ToHashSet();
            var nodes = beams.SelectMany(b => new[] { solver.Beams[b].I, solver.Beams[b].J }).Concat(shells.SelectMany(s => new[] { solver.Shells[s].I, solver.Shells[s].J, solver.Shells[s].K })).Distinct().ToList();
            bool Left(int n) => beamsAt[n].Any(b => !removed.Contains(b)) || shellsAt[n].Any(s => !gone.Contains(s));
            var inner = nodes.Where(n => !held[n] && !Left(n)).ToList();
            var boundary = nodes.Where(n => held[n] || Left(n)).ToList();
            // A node held only by what was taken away and its support carries no load of its own now: the foot of a column taken from the lowest storey.
            var bare = boundary.Where(n => !Left(n)).ToList();
            double[]? u;
            StructuralSolver.Factored standing;
            if (shells.Count == 0)
            {
                // A column or a beam: the intact factorisation updated over the nodes it shared.
                u = Damaged(solver, factored, load, beams, inner, boundary, bare);
                standing = factored;
            }
            else
            {
                // A length of wall shares too many nodes for that: the stiffness factorised again without it, what it kept a billionth.
                standing = solver.Factorise(null, Enumerable.Range(0, solver.Shells.Count).Select(s => gone.Contains(s) ? 1 - Kept : 1.0).ToArray());
                var b0 = (double[])load.Clone();
                foreach (var node in inner.Concat(bare))
                    for (var f = 0; f < 6; f++)
                        b0[node * 6 + f] = 0;
                var free = inner.Concat(bare).ToHashSet();
                u = standing.Unstable.Any(each => !free.Contains(each.Node)) ? null : standing.Solve(b0);
            }

            var stable = u is not null && u.All(double.IsFinite) && boundary.Except(bare).All(n => Math.Sqrt(Math.Pow(u[n * 6], 2) + Math.Pow(u[n * 6 + 1], 2) + Math.Pow(u[n * 6 + 2], 2)) < 2000);

            // What is left, as it is checked.
            var failed = new List<(string Where, double Ratio, int Level)>();
            var failedArea = new double[levels.Count];
            if (stable)
            {
                foreach (var member in model.Members)
                {
                    var left = pieces[member].Where(piece => !removed.Contains(piece.Beam)).Select(piece => piece.Beam).ToList();
                    if (left.Count == 0) continue;
                    var stations = Stations(left, u!, accidental);
                    var ratio = member.Section.Material.Kind == AnalyticalMaterialKind.Concrete
                        ? ConcreteRatio(member, stations)
                        : MemberStrength.ChecksOf(document, member, lengths[member], stations).Select(check => check.Ratio).DefaultIfEmpty(0).Max();
                    if (ratio > 1)
                    {
                        var zs = left.SelectMany(b => new[] { solver.NodeAt(solver.Beams[b].I).Z, solver.NodeAt(solver.Beams[b].J).Z }).ToList();
                        failed.Add((member.Name, ratio, LevelOf(member.Kind == AnalyticalMemberKind.Column ? zs.Max() : zs.Average())));
                    }
                }

                // Walls, at their foot a metre at a time.
                var stress = solver.PrestressOf(standing, u!);
                foreach (var wall in walls)
                {
                    var (each, whole) = wallCapacity[wall];
                    var carried = Carried(wall, stress, gone);
                    if (carried.Count == 0 || whole <= 0) continue;
                    var ratio = carried.Max(entry => entry.Value / (each?.GetValueOrDefault(entry.Key, whole) ?? whole));
                    if (ratio > 1) failed.Add((wall.Name, ratio, LevelOf(wall.Outline.Max(node => node.At.Z))));
                }

                foreach (var (shell, panel) in slabShells.Where(each => !gone.Contains(each.Shell)))
                {
                    var (bx, by, tx, ty) = WoodArmerOf(shell, u!);
                    var capacity = slabCapacity[shell];
                    var ratio = new[] { bx / capacity[0], by / capacity[1], -tx / capacity[2], -ty / capacity[3] }.Max();
                    if (ratio > 1 && shellAt.TryGetValue(shell, out var at))
                    {
                        failed.Add((panel.Name, ratio, at.Level));
                        failedArea[at.Level] += at.Area;
                    }
                }
            }

            // The floor at risk: where anything fails, what leaned on what was taken away, at each level from the one it held up.
            var atRisk = new List<(string Level, double Area, double Limit)>();
            if (!stable || failed.Count > 0)
                for (var at = level; at < levels.Count; at++)
                {
                    var leaning = Tributary(at, where);
                    if (leaning + failedArea[at] > 0 || failed.Any(f => f.Level == at)) atRisk.Add((levels[at].Name, Math.Max(leaning, failedArea[at]), Limit(floorArea[at])));
                }

            var stands = stable && (failed.Count == 0 || (atRisk.Count <= 2 && atRisk.All(each => each.Area <= each.Limit)
                && atRisk.All(each => levels.FindIndex(l => l.Name == each.Level) <= level + 1)));
            var worst = failed.Count == 0 ? ("-", 0.0, -1) : failed.MaxBy(each => each.Ratio);
            var key = !stands && keyColumn is not null ? KeyColumn(document, analysis, keyColumn, beams, lengths[keyColumn], intact) : Array.Empty<StrengthCheck>();
            var said = !stable ? $"Nothing is left to carry what it held: the building does not stand without it{(shells.Count > 0 ? "; a wall as a key element is not worked out here" : string.Empty)}."
                : !stands && keyColumn is null && shells.Count > 0 ? $"{failed.Count} part{(failed.Count == 1 ? "" : "s")} of what is left fail{(failed.Count == 1 ? "s" : "")}, the floor at risk past the limit: to be a key element, which for a wall is not worked out here."
                : failed.Count == 0 ? "What is left carries what it held, nothing over its strength."
                : stands ? $"{failed.Count} part{(failed.Count == 1 ? "" : "s")} of what is left fail{(failed.Count == 1 ? "s" : "")}, the floor at risk within the limit."
                : $"{failed.Count} part{(failed.Count == 1 ? "" : "s")} of what is left fail{(failed.Count == 1 ? "s" : "")}, the floor at risk past the limit: to be a key element.";
            return new RemovalCheck(name, storey, stable, failed.Count, worst.Item2, worst.Item1, atRisk, stands, key, said);
        }

        foreach (var column in columns)
        {
            // Its pieces, a storey at a time.
            var byStorey = pieces[column].GroupBy(piece =>
            {
                var top = Math.Max(solver.NodeAt(solver.Beams[piece.Beam].I).Z, solver.NodeAt(solver.Beams[piece.Beam].J).Z);
                return levels.FindIndex(level => level.Elevation >= top - 400);
            }).Where(group => group.Key >= 0);
            foreach (var storey in byStorey)
            {
                var beams = storey.Select(piece => piece.Beam).ToList();
                var top = beams.Select(b => solver.NodeAt(solver.Beams[b].I)).Concat(beams.Select(b => solver.NodeAt(solver.Beams[b].J))).MaxBy(p => p.Z);
                var under0 = StoreyUnder(levels[storey.Key].Elevation);
                var at = storey.Key;
                jobs.Add(() => Remove($"{column.Name}, {under0} storey", under0, beams, Array.Empty<int>(), (top.X, top.Y, top.X, top.Y), at, column));
            }
        }

        // Each beam a column stands on.
        foreach (var beam in model.Members.Where(member => member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist))
        {
            var carried = columns.Where(column => column.Nodes.MinBy(node => node.At.Z) is { } foot && beam.Nodes.Any(node => ReferenceEquals(node, foot))).ToList();
            if (carried.Count == 0) continue;
            var (first, last) = (beam.Nodes[0].At, beam.Nodes[^1].At);
            var level = LevelOf((first.Z + last.Z) / 2);
            jobs.Add(() => Remove($"{beam.Name}, carrying {string.Join(", ", carried.Select(column => column.Name))}", levels[level].Name, pieces[beam].Select(piece => piece.Beam).ToList(), Array.Empty<int>(), (first.X, first.Y, last.X, last.Y), level, null));
        }

        // Each nominal length of load-bearing wall, A.7: a concrete wall's no more than 2.25 times its storey's height; an outside wall of masonry or studs between what holds it - the walls
        // that meet it, columns against it; an inside one no more than 2.25 times its height.
        foreach (var wall in walls)
        {
            var flat = wall.FlatOutline;
            var (from, to) = (flat.Min(p => p.X), flat.Max(p => p.X));
            var (bottom, top) = (wall.Outline.Min(node => node.At.Z), wall.Outline.Max(node => node.At.Z));
            var height = top - bottom;
            if (height < 500 || to - from < 300) continue;
            var outside = wall.Element is Wall element && document.GetWallType(element)?.Function == WallFunction.Exterior;
            var nominal = NominalLengths(wall, walls, columns, from, to, bottom, top, outside && wall.Material.Kind != AnalyticalMaterialKind.Concrete, height);
            var level = LevelOf(top);
            var under0 = StoreyUnder(levels[level].Elevation);
            for (var i = 0; i < nominal.Count; i++)
            {
                var (start, end) = nominal[i];
                var shells = feet[wall].Where(each => each.Along >= start - 1 && each.Along <= end + 1).Select(each => each.Shell).ToList();
                if (shells.Count == 0) continue;
                var (p, q) = ((wall.Origin.X + wall.U.X * start, wall.Origin.Y + wall.U.Y * start), (wall.Origin.X + wall.U.X * end, wall.Origin.Y + wall.U.Y * end));
                jobs.Add(() => Remove($"{wall.Name}{(nominal.Count > 1 ? $" {Units.Metres(start - from, "0.0")} to {Units.Metres(end - from, "0.0")}" : string.Empty)}, {under0} storey", under0, Array.Empty<int>(), shells,
                    (p.Item1, p.Item2, q.Item1, q.Item2), level, null));
            }
        }

        var worked = new RemovalCheck[jobs.Count];
        Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i => worked[i] = jobs[i]());
        var removals = worked.ToList();
        var stands = removals.Count(each => each.Stands);
        return new RemovalResult(
            $"Consequence Class 2b: vertical ties, or each support taken away in turn, Approved Document A 5.1d. {removals.Count} taken away: {stands} stand without it, {removals.Count - stands} would be key elements.",
            removals,
            "Each load-bearing wall taken away a nominal length at a time, Annex A.7; a masonry wall left standing carries what its own checks give it at γM 1.5 for an accident, as remembered, and a wall as a key element is not worked out. Steel and timber at their persistent partial factors, on the safe side; concrete taken to hold what " +
            "its intact design asks of it at first order, or its least steel, with the accidental factor of steel. Linear and elastic: no catenary or membrane action is counted. A key element beam is not worked out.");
    }

    /// <summary>
    /// A wall's nominal lengths, Annex A.7, along it in its own terms: between what holds it where that is asked - other walls meeting it, columns against it, within 400 mm of its line - or no more
    /// than 2.25 times its height, in equal lengths.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> NominalLengths(AnalyticalPanel wall, IReadOnlyList<AnalyticalPanel> walls, IReadOnlyList<AnalyticalMember> columns, double from, double to,
        double bottom, double top, bool betweenSupports, double height)
    {
        var cuts = new List<double>();
        if (betweenSupports)
        {
            double Along(Geometry.Point3D at) => wall.Flat(at).X;
            double Off(Geometry.Point3D at) => Math.Abs((at.X - wall.Origin.X) * wall.Normal.X + (at.Y - wall.Origin.Y) * wall.Normal.Y);
            foreach (var other in walls.Where(other => !ReferenceEquals(other, wall)))
            {
                var ends = other.Outline.Where(node => node.At.Z <= top + 1 && node.At.Z >= bottom - 1).Select(node => node.At).Where(at => Off(at) <= 400).ToList();
                foreach (var at in ends)
                {
                    var along = Along(at);
                    if (along > from + 300 && along < to - 300) cuts.Add(along);
                }
            }

            foreach (var column in columns)
                foreach (var node in column.Nodes.Where(node => node.At.Z <= top + 1 && node.At.Z >= bottom - 1 && Off(node.At) <= 400))
                {
                    var along = Along(node.At);
                    if (along > from + 300 && along < to - 300) cuts.Add(along);
                }

            var points = new[] { from }.Concat(cuts.OrderBy(cut => cut)).Append(to).ToList();
            var lengths = new List<(double, double)>();
            for (var i = 0; i + 1 < points.Count; i++)
                if (points[i + 1] - points[i] > 300) lengths.Add((points[i], points[i + 1]));
            return lengths;
        }

        var most = 2.25 * height;
        var count = Math.Max(1, (int)Math.Ceiling((to - from) / most - 1e-9));
        return Enumerable.Range(0, count).Select(i => (from + (to - from) * i / count, from + (to - from) * (i + 1) / count)).ToList();
    }

    /// <summary>
    /// The movement of the structure with these beams taken away, from its intact factorisation: Woodbury's identity over the freedoms of the nodes they share with the rest, their own nodes condensed out
    /// and their loads taken off. Null where what is left cannot be solved.
    /// </summary>
    public static double[]? Damaged(StructuralSolver solver, StructuralSolver.Factored factored, double[] load, IReadOnlyList<int> beams, IReadOnlyList<int> inner, IReadOnlyList<int> boundary,
        IReadOnlyList<int>? bare = null)
    {
        var all = boundary.Concat(inner).ToList();
        var local = all.Select((node, i) => (node, i)).ToDictionary(each => each.node, each => each.i);
        var m = all.Count * 6;
        var stiffness = new double[m, m];
        foreach (var beam in beams)
        {
            var (k, _, _) = solver.BeamGlobal(solver.Beams[beam]);
            var ends = new[] { local[solver.Beams[beam].I], local[solver.Beams[beam].J] };
            for (var a = 0; a < 12; a++)
            for (var b = 0; b < 12; b++)
                stiffness[ends[a / 6] * 6 + a % 6, ends[b / 6] * 6 + b % 6] += Kept * k[a, b];
        }

        // Condense the nodes only they met: D = Dbb - Dbi Dii⁻¹ Dib.
        var (nb, ni) = (boundary.Count * 6, inner.Count * 6);
        var d = new double[nb, nb];
        for (var i = 0; i < nb; i++)
        for (var j = 0; j < nb; j++)
            d[i, j] = stiffness[i, j];
        if (ni > 0)
        {
            var dii = new double[ni, ni];
            var dib = new double[ni, nb];
            for (var i = 0; i < ni; i++)
            {
                for (var j = 0; j < ni; j++) dii[i, j] = stiffness[nb + i, nb + j];
                for (var j = 0; j < nb; j++) dib[i, j] = stiffness[nb + i, j];
            }

            var x = SolveMany(dii, dib);
            if (x is null) return null;
            for (var i = 0; i < nb; i++)
            for (var j = 0; j < nb; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < ni; k++) sum += stiffness[i, nb + k] * x[k, j];
                d[i, j] -= sum;
            }
        }

        // The freedoms of the shared nodes that are free, each one's unit load put through the intact structure.
        var free = Enumerable.Range(0, nb).Where(i => !solver.IsRestrained(boundary[i / 6], i % 6)).ToList();
        int Dof(int i) => boundary[i / 6] * 6 + i % 6;
        var z = free.Select(i =>
        {
            var e = new double[load.Length];
            e[Dof(i)] = 1;
            return factored.Solve(e);
        }).ToList();
        var r = free.Count;
        var b0 = (double[])load.Clone();
        foreach (var node in inner.Concat(bare ?? Array.Empty<int>()))
            for (var f = 0; f < 6; f++)
                b0[node * 6 + f] = 0;
        var moved = factored.Solve(b0);
        if (r == 0) return moved;

        // u = x + Z D (I - G D)⁻¹ y, G = Pᵀ K⁻¹ P, y = Pᵀ x.
        var dr = new double[r, r];
        for (var i = 0; i < r; i++)
        for (var j = 0; j < r; j++)
            dr[i, j] = d[free[i], free[j]];
        var system = new double[r, r];
        for (var i = 0; i < r; i++)
        for (var j = 0; j < r; j++)
        {
            var sum = 0.0;
            for (var k = 0; k < r; k++) sum += z[k][Dof(free[i])] * dr[k, j];
            system[i, j] = (i == j ? 1 : 0) - sum;
        }

        var y = new double[r, 1];
        for (var i = 0; i < r; i++) y[i, 0] = moved[Dof(free[i])];
        var w = SolveMany(system, y);
        if (w is null) return null;
        var dw = new double[r];
        for (var i = 0; i < r; i++)
        for (var j = 0; j < r; j++)
            dw[i] += dr[i, j] * w[j, 0];
        var u = (double[])moved.Clone();
        for (var k = 0; k < r; k++)
            if (dw[k] != 0)
                for (var n = 0; n < u.Length; n++)
                    u[n] += z[k][n] * dw[k];
        return u.All(double.IsFinite) ? u : null;
    }

    // What is taken away keeps a billionth of its stiffness: a node it leaves on its supports' springs alone, the foot of a column taken from the lowest storey, is not left free to move.
    private const double Kept = 1 - 1e-9;

    /// <summary>A X = B by Gaussian elimination with partial pivoting; null where A is singular.</summary>
    private static double[,]? SolveMany(double[,] a, double[,] b)
    {
        var n = a.GetLength(0);
        var columns = b.GetLength(1);
        var (m, x) = ((double[,])a.Clone(), (double[,])b.Clone());
        var scale = 0.0;
        for (var i = 0; i < n; i++) scale = Math.Max(scale, Math.Abs(m[i, i]));
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
                if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
            if (Math.Abs(m[pivot, c]) < 1e-14 * Math.Max(scale, 1)) return null;
            if (pivot != c)
            {
                for (var k = 0; k < n; k++) (m[c, k], m[pivot, k]) = (m[pivot, k], m[c, k]);
                for (var k = 0; k < columns; k++) (x[c, k], x[pivot, k]) = (x[pivot, k], x[c, k]);
            }

            for (var r = c + 1; r < n; r++)
            {
                var factor = m[r, c] / m[c, c];
                if (factor == 0) continue;
                for (var k = c; k < n; k++) m[r, k] -= factor * m[c, k];
                for (var k = 0; k < columns; k++) x[r, k] -= factor * x[c, k];
            }
        }

        for (var c = n - 1; c >= 0; c--)
            for (var k = 0; k < columns; k++)
            {
                var sum = x[c, k];
                for (var j = c + 1; j < n; j++) sum -= m[c, j] * x[j, k];
                x[c, k] = sum / m[c, c];
            }

        return x;
    }

    /// <summary>
    /// A column as a key element, A.8: 34 kN/m² on each of its faces in turn over its storey, as a beam held at its ends, with what it carries under the accidental combination; checked as any member of its
    /// material is.
    /// </summary>
    private static IReadOnlyList<StrengthCheck> KeyColumn(BimDocument document, StructuralAnalysis analysis, AnalyticalMember column, IReadOnlyList<int> beams, MemberLengths lengths, double[] u)
    {
        var solver = analysis.Solver;
        var accidental = FireDesign.Combination(document, analysis.Loading);
        var height = beams.Sum(b => solver.Axes(solver.Beams[b]).Length);
        var dims = column.Section.Dimensions;
        var (depth, width) = dims.Count >= 2 ? (dims[0], dims[1]) : (Math.Sqrt(column.Section.Area), Math.Sqrt(column.Section.Area));
        var carried = beams.Select(b =>
        {
            var (matrix, dofs) = solver.BeamForceMatrix(solver.Beams[b]);
            var f0 = 0.0;
            for (var j = 0; j < 12; j++) f0 += matrix[0, j] * u[dofs[j]];
            return -f0 / 1e3;
        }).DefaultIfEmpty(0).Min();
        var key = new LoadCombination("Key element", "BS EN 1990 6.11b, Ad 34 kN/m²", LimitState.Ultimate, accidental.Terms);
        var stations = new List<(LoadCombination, MemberForces)>();
        foreach (var (face, strong) in new[] { (width, true), (depth, false) })
        {
            var w = KeyLoad * face / 1000;
            var (m, v) = (w * Math.Pow(height / 1000, 2) / 8, w * height / 1000 / 2);
            stations.Add((key, strong ? new MemberForces(carried, 0, v, 0, 0, 0, false) : new MemberForces(carried, v, 0, 0, 0, 0, false)));
            stations.Add((key, strong ? new MemberForces(carried, 0, 0, 0, m, 0, false) : new MemberForces(carried, 0, 0, 0, 0, m, false)));
        }

        return column.Section.Material.Kind == AnalyticalMaterialKind.Concrete
            ? ConcreteDesign.Checks(document, analysis, column, false, lengths, stations).Checks
            : MemberStrength.ChecksOf(document, column, lengths, stations);
    }
}
