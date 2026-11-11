using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A wall's earthquake out of its plane, ASCE 7-16 12.11, at a level: the pressure the wall is to be designed for across its face by 12.11.1, 0.4 SDS Ie of its weight and no less than a tenth of it,
/// kPa; and where a concrete or masonry wall is held by a flexible floor or roof in categories C to F, the force its anchorage to it takes by (12.11-1), kN/m, ka by the diaphragm's span.
/// </summary>
public sealed record SeismicAnchorage(string Wall, string Level, double Pressure, double Anchor, double Ka, double Span);

/// <summary>
/// The rules of ASCE 7-16 Chapter 12 that the building's shape decides: the irregularities of Tables 12.3-1 and 12.3-2 the drawing shows, the redundancy factor of 12.3.4.2, which analysis Table 12.6-1
/// permits, and the forces of 12.11 on walls out of their plane. As remembered, the copies got having these tables as images or not at all.
/// </summary>
public static class SeismicRules
{
    private const double Feet = 304.8;

    /// <summary>
    /// Whether Table 12.6-1 permits the equivalent lateral force procedure: in categories B and C always; in D to F for Risk Category I or II buildings of no more than two storeys, light-frame
    /// construction, a building with no irregularity no more than 160 ft high or past it with T under 3.5 Ts, and one no more than 160 ft high whose only irregularities are horizontal 2 to 5 and
    /// vertical 4 and 5. Else the modal response spectrum analysis of 12.9.1 is asked.
    /// </summary>
    public static (bool Permitted, string Said) EquivalentPermitted(SeismicDesign quake, IReadOnlyCollection<string> types, int storeys)
    {
        if (quake.Category is not ("D" or "E" or "F")) return (true, $"Table 12.6-1 permits the equivalent lateral force procedure in category {quake.Category}");
        if (quake.Risk <= 2 && storeys <= 2) return (true, "Table 12.6-1 permits the equivalent lateral force procedure: Risk Category I or II, no more than two storeys");
        if (ReferenceEquals(quake.System, Seismic.LightFrame)) return (true, "Table 12.6-1 permits the equivalent lateral force procedure: light-frame construction");
        var tall = quake.Height > 160 * Feet;
        var ts = quake.Sds > 0 ? quake.Sd1 / quake.Sds : 0;
        if (types.Count == 0 && !tall) return (true, "Table 12.6-1 permits the equivalent lateral force procedure: no irregularity, no more than 160 ft high");
        if (types.Count == 0 && quake.Period < 3.5 * ts) return (true, $"Table 12.6-1 permits the equivalent lateral force procedure: no irregularity, T {quake.Period:0.00} s under 3.5 Ts");
        if (!tall && types.All(type => type is "H2" or "H3" or "H4" or "H5" or "V4" or "V5a" or "V5b"))
            return (true, $"Table 12.6-1 permits the equivalent lateral force procedure: no more than 160 ft high, its irregularities {string.Join(", ", types.OrderBy(type => type))} only");
        return (false, types.Count > 0
            ? $"Table 12.6-1 does not permit the equivalent lateral force procedure in category {quake.Category} with irregularities {string.Join(", ", types.OrderBy(type => type))}"
            : $"Table 12.6-1 does not permit the equivalent lateral force procedure in category {quake.Category} past 160 ft with T {quake.Period:0.00} s at 3.5 Ts or more");
    }

    private static bool Level(AnalyticalPanel plate, double elevation) =>
        Math.Abs(plate.Origin.Z - elevation) <= 150 && Math.Abs(plate.Normal.Z) > 0.95;

    private static IEnumerable<AnalyticalPanel> Plates(AnalyticalModel model, double elevation) =>
        model.Panels.Where(panel => panel.Kind is AnalyticalPanelKind.Floor or AnalyticalPanelKind.Roof).Concat(model.Diaphragms).Where(plate => Level(plate, elevation));

    private static List<Point2D> Plan(AnalyticalPanel plate) => plate.Outline.Select(node => new Point2D(node.At.X, node.At.Y)).ToList();

    private static (double Low, double High) Extent(AnalyticalPanel wall)
    {
        var along = wall.Outline.Select(node => (node.At.X - wall.Origin.X) * wall.U.X + (node.At.Y - wall.Origin.Y) * wall.U.Y).ToList();
        return (along.Min(), along.Max());
    }

    private static (double Bottom, double Top) Heights(AnalyticalPanel wall) => (wall.Outline.Min(node => node.At.Z), wall.Outline.Max(node => node.At.Z));

    /// <summary>
    /// The irregularities the drawing shows, each with its type and what makes it. Horizontal: 2, a re-entrant corner both of whose projections are more than 15% of the plan's dimension that way; 3,
    /// a floor more than half open, or one whose stiffness changes by more than half from the storey's next to it, timber against concrete; 4, a wall standing on no wall in its plane under it, its
    /// path out of plane; 5, a wall more than 5° off both of the building's axes. Vertical: 3, a storey whose walls and columns stand out more than 130% as far as the storey's next to it, either way;
    /// 4, a wall over a wall in its plane but past its end.
    /// </summary>
    public static IReadOnlyList<(string Type, string Said)> Geometric(AnalyticalModel model, IReadOnlyList<(string Name, double Elevation)> floors, double ground)
    {
        var found = new List<(string, string)>();
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();

        foreach (var (name, elevation) in floors)
        {
            var plates = Plates(model, elevation).ToList();
            if (plates.Count == 0) continue;
            var largest = plates.MaxBy(plate => Polygon2D.Area(Plan(plate)))!;
            if (ReEntrant(Plan(largest)) is { } corner) found.Add(("H2", $"{name}: horizontal irregularity 2, re-entrant corner, its plan standing out {corner} past it (Table 12.3-1, as remembered)."));
            var gross = plates.Sum(plate => Polygon2D.Area(Plan(plate)));
            var open = plates.Sum(plate => plate.Openings.Sum(opening => Polygon2D.Area(opening.Select(point => new Point2D(point.X, point.Y)).ToList())));
            if (gross > 0 && open > 0.5 * gross) found.Add(("H3", $"{name}: horizontal irregularity 3, diaphragm discontinuity, {open / gross:P0} of its floor open (Table 12.3-1, as remembered)."));
        }

        // A diaphragm of timber next to one of concrete: its stiffness changes far more than by half.
        for (var i = 0; i + 1 < floors.Count; i++)
        {
            bool Rigid(double elevation) => Plates(model, elevation).Any(plate => plate.Kind is not AnalyticalPanelKind.Diaphragm && plate.Material.Kind == AnalyticalMaterialKind.Concrete);
            bool Any(double elevation) => Plates(model, elevation).Any();
            if (Any(floors[i].Elevation) && Any(floors[i + 1].Elevation) && Rigid(floors[i].Elevation) != Rigid(floors[i + 1].Elevation))
                found.Add(("H3", $"{floors[i + 1].Name}: horizontal irregularity 3, its diaphragm {(Rigid(floors[i + 1].Elevation) ? "concrete" : "timber")} over {(Rigid(floors[i].Elevation) ? "concrete" : "timber")}, its stiffness changing by more than half (Table 12.3-1, as remembered)."));
        }

        // Walls standing on walls: in their plane and along them, or not.
        foreach (var wall in walls)
        {
            var (bottom, _) = Heights(wall);
            if (bottom <= ground + 300) continue;
            var (low, high) = Extent(wall);
            if (high - low < 300) continue;
            var across = new Point2D(-wall.U.Y, wall.U.X);
            double Off(Point3D point) => (point.X - wall.Origin.X) * across.X + (point.Y - wall.Origin.Y) * across.Y;
            double Along(Point3D point) => (point.X - wall.Origin.X) * wall.U.X + (point.Y - wall.Origin.Y) * wall.U.Y;
            var under = walls.Where(other => other != wall && Math.Abs(Heights(other).Top - bottom) <= 150 && Math.Abs(other.U.X * wall.U.X + other.U.Y * wall.U.Y) > 0.99).ToList();
            var inPlane = under.Where(other => other.Outline.All(node => Math.Abs(Off(node.At)) <= (wall.Thickness + other.Thickness) / 2 + 50)).ToList();
            var covered = inPlane.Select(other => (Low: other.Outline.Min(node => Along(node.At)), High: other.Outline.Max(node => Along(node.At))))
                .Sum(span => Math.Max(0, Math.Min(span.High, high) - Math.Max(span.Low, low)));
            if (covered >= 0.9 * (high - low)) continue;
            found.Add(inPlane.Count > 0
                ? ("V4", $"{wall.Name}: vertical irregularity 4, in-plane discontinuity, standing past the end of the wall under it, {covered / (high - low):P0} of it over a wall (Table 12.3-2, as remembered).")
                : ("H4", $"{wall.Name}: horizontal irregularity 4, out-of-plane offset, no wall under it in its plane (Table 12.3-1, as remembered)."));
        }

        foreach (var wall in walls.Where(wall => Math.Abs(wall.U.X) > Math.Sin(5 * Math.PI / 180) && Math.Abs(wall.U.Y) > Math.Sin(5 * Math.PI / 180)))
            found.Add(("H5", $"{wall.Name}: horizontal irregularity 5, nonparallel system, {Math.Atan2(Math.Abs(wall.U.Y), Math.Abs(wall.U.X)) * 180 / Math.PI:0}° off the building's axes (Table 12.3-1, as remembered)."));

        // How far each storey's walls and columns stand out each way, against the storeys next to it.
        var spans = new List<(double X, double Y)>();
        var under_ = ground;
        foreach (var (_, elevation) in floors)
        {
            var points = walls.Where(wall => Heights(wall).Bottom >= under_ - 150 && Heights(wall).Top <= elevation + 150).SelectMany(wall => wall.Outline.Select(node => node.At))
                .Concat(model.Members.Where(member => member.Kind == AnalyticalMemberKind.Column && Math.Min(member.Start.At.Z, member.End.At.Z) >= under_ - 150 && Math.Max(member.Start.At.Z, member.End.At.Z) <= elevation + 150)
                    .SelectMany(member => new[] { member.Start.At, member.End.At })).ToList();
            spans.Add(points.Count == 0 ? (0, 0) : (points.Max(point => point.X) - points.Min(point => point.X), points.Max(point => point.Y) - points.Min(point => point.Y)));
            under_ = elevation;
        }

        for (var i = 0; i < floors.Count; i++)
            foreach (var (axis, way) in new[] { (0, "x"), (1, "y") })
            {
                double Of(int k) => axis == 0 ? spans[k].X : spans[k].Y;
                var mine = Of(i);
                var next = new[] { i - 1, i + 1 }.Where(k => k >= 0 && k < floors.Count && Of(k) > 0).Select(Of).ToList();
                if (mine > 0 && next.Any(other => mine > 1.3 * other))
                    found.Add(("V3", $"{floors[i].Name}: vertical irregularity 3, vertical geometric, the storey under it standing {Units.Metres(mine, "0.0")} along {way} against {Units.Metres(next.Min(), "0.0")} next to it (Table 12.3-2, as remembered)."));
            }

        return found.Distinct().ToList();
    }

    /// <summary>A plan's re-entrant corner both of whose projections are more than 15% of the plan's dimension that way, said; none where there is none.</summary>
    public static string? ReEntrant(IReadOnlyList<Point2D> plan)
    {
        if (plan.Count < 5) return null;
        var (minX, maxX, minY, maxY) = (plan.Min(p => p.X), plan.Max(p => p.X), plan.Min(p => p.Y), plan.Max(p => p.Y));
        var (lx, ly) = (maxX - minX, maxY - minY);
        var turning = Polygon2D.SignedArea(plan) >= 0 ? 1 : -1;
        for (var i = 0; i < plan.Count; i++)
        {
            var (a, v, b) = (plan[(i + plan.Count - 1) % plan.Count], plan[i], plan[(i + 1) % plan.Count]);
            var cross = (v.X - a.X) * (b.Y - v.Y) - (v.Y - a.Y) * (b.X - v.X);
            if (cross * turning > -1e-3 * Math.Max(1, v.DistanceTo(a) * v.DistanceTo(b))) continue;
            foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
            {
                if (Polygon2D.Contains(plan, new Point2D(v.X + dx * 10, v.Y + dy * 10))) continue;
                var (px, py) = (dx > 0 ? maxX - v.X : v.X - minX, dy > 0 ? maxY - v.Y : v.Y - minY);
                if (px > 0.15 * lx && py > 0.15 * ly) return $"{px / lx:P0} of its {Units.Metres(lx, "0.0")} and {py / ly:P0} of its {Units.Metres(ly, "0.0")}";
            }
        }

        return null;
    }

    /// <summary>
    /// The redundancy factor, 12.3.4.2: 1.0 in categories B and C; in D to F 1.3 unless, by (b), the building is regular in plan at every level and each storey taking more than 35% of the base
    /// shear has at least two bays of walls along its perimeter on each side each way - a wall's length over the storey's height, twice that for light-frame. The walls on a side are those within a
    /// tenth of the building's width of it, or 1.5 m. Frames' bays are not counted here, nor (a)'s removal of an element.
    /// </summary>
    public static (double Rho, string Said) Redundancy(SeismicDesign quake, AnalyticalModel model, IReadOnlyList<(string Name, double Elevation)> floors, double ground, IReadOnlyList<double> storeyShares,
        IReadOnlyCollection<string> types)
    {
        if (quake.Category is not ("D" or "E" or "F")) return (1.0, $"ρ 1.0 in category {quake.Category}, 12.3.4.1");
        var plan = types.Where(type => type.StartsWith('H')).ToList();
        if (plan.Count > 0) return (1.3, $"ρ 1.3: not regular in plan, {string.Join(", ", plan.Distinct().OrderBy(type => type))} (12.3.4.2)");
        var walls = model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall).ToList();
        var light = ReferenceEquals(quake.System, Seismic.LightFrame);
        var under = ground;
        for (var i = 0; i < floors.Count; i++)
        {
            var (name, elevation) = floors[i];
            var height = elevation - under;
            var mine = walls.Where(wall => Heights(wall).Bottom >= under - 150 && Heights(wall).Top <= elevation + 150).ToList();
            var from = under;
            under = elevation;
            if (i < storeyShares.Count && storeyShares[i] <= 0.35) continue;
            if (mine.Count == 0 || height <= 0) return (1.3, $"ρ 1.3: {name}'s storey has no walls; frames' bays are not counted here (12.3.4.2)");
            var points = mine.SelectMany(wall => wall.Outline.Select(node => node.At)).ToList();
            foreach (var (axis, way) in new[] { (0, "x"), (1, "y") })
            {
                // Walls along this way, on the two sides across it.
                var along = mine.Where(wall => Math.Abs(axis == 0 ? wall.U.X : wall.U.Y) > 0.99).ToList();
                double Across(Point3D point) => axis == 0 ? point.Y : point.X;
                var (low, high) = (points.Min(Across), points.Max(Across));
                var near = Math.Max(1500, 0.1 * (high - low));
                foreach (var (side, edge) in new[] { ("one", low), ("the other", high) })
                {
                    var bays = along.Where(wall => Math.Abs(wall.Outline.Average(node => Across(node.At)) - edge) <= near)
                        .Sum(wall => (Extent(wall).High - Extent(wall).Low) / height * (light ? 2 : 1));
                    if (bays < 2) return (1.3, $"ρ 1.3: {name}'s storey has {bays:0.0} bays of wall along {way} on {side} side, two asked (12.3.4.2(b))");
                }
            }
        }

        return (1.0, "ρ 1.0 by 12.3.4.2(b): regular in plan, two bays of wall or more on each side each way in each storey taking more than 35% of the base shear");
    }

    /// <summary>
    /// Each wall's earthquake out of its plane at each level it reaches, 12.11: 0.4 SDS Ie of its weight a square metre, no less than 0.1 of it, across its face by 12.11.1; and where it is concrete or
    /// masonry, held by a flexible floor or roof in categories C to F, its anchorage by (12.11-1), 0.4 SDS ka Ie Wp and no less than 0.2 ka Ie Wp, ka 1 + Lf / 100 to 2, Lf the diaphragm's span in ft
    /// along the wall between what holds it, Wp the wall from halfway down the storey under to halfway up the one over, the wall standing on it in its plane where it stops at the floor.
    /// </summary>
    public static IReadOnlyList<SeismicAnchorage> Anchorage(SeismicDesign quake, AnalyticalModel model, IReadOnlyList<(string Name, double Elevation)> floors)
    {
        var rows = new List<SeismicAnchorage>();
        if (quake.Category == "A") return rows;
        foreach (var wall in model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
        {
            var (bottom, top) = Heights(wall);
            var weight = wall.SelfWeight;
            var pressure = Math.Max(0.4 * quake.Sds * quake.Importance, 0.1) * weight;
            var heavy = wall.Material.Kind is AnalyticalMaterialKind.Concrete or AnalyticalMaterialKind.Masonry;
            foreach (var (name, elevation) in floors.Where(floor => floor.Elevation > bottom + 300 && floor.Elevation <= top + 150))
            {
                var flexible = model.Diaphragms.FirstOrDefault(plate => Level(plate, elevation) && Reaches(plate, wall));
                double anchor = 0, ka = 0, span = 0;
                if (heavy && flexible is not null && quake.Category is "C" or "D" or "E" or "F")
                {
                    var along = flexible.Outline.Select(node => (node.At.X - wall.Origin.X) * wall.U.X + (node.At.Y - wall.Origin.Y) * wall.U.Y).ToList();
                    span = along.Max() - along.Min();
                    ka = Math.Min(2, 1 + span / Feet / 100);
                    // Wp: the wall from halfway down the storey under the floor to halfway up the one over it - this wall where it runs on past the floor, else the wall standing on it in its plane.
                    var below = floors.Where(floor => floor.Elevation < elevation - 150).Select(floor => floor.Elevation).DefaultIfEmpty(bottom).Max();
                    var next = floors.Where(floor => floor.Elevation > elevation + 150).Select(floor => floor.Elevation).DefaultIfEmpty(double.PositiveInfinity).Min();
                    var under = Math.Min(elevation, top) - Math.Max(below, bottom);
                    var over = Math.Max(0, Math.Min(next, top) - elevation) * weight;
                    if (over <= 0)
                    {
                        var across = new Point2D(-wall.U.Y, wall.U.X);
                        var upper = model.Panels.Where(other => other.Kind == AnalyticalPanelKind.Wall && other != wall && Math.Abs(Heights(other).Bottom - elevation) <= 150
                                                                && Math.Abs(other.U.X * wall.U.X + other.U.Y * wall.U.Y) > 0.99
                                                                && other.Outline.All(node => Math.Abs((node.At.X - wall.Origin.X) * across.X + (node.At.Y - wall.Origin.Y) * across.Y) <= (wall.Thickness + other.Thickness) / 2 + 50))
                            .Select(other => Math.Max(0, Math.Min(next, Heights(other).Top) - elevation) * other.SelfWeight).DefaultIfEmpty(0).Max();
                        over = upper;
                    }

                    var wp = (weight * under / 2 + over / 2) / 1000;
                    anchor = Math.Max(0.4 * quake.Sds * ka * quake.Importance, 0.2 * ka * quake.Importance) * wp;
                }

                rows.Add(new SeismicAnchorage(wall.Name, name, pressure, anchor, ka, span));
            }
        }

        return rows;
    }

    private static bool Reaches(AnalyticalPanel plate, AnalyticalPanel wall)
    {
        var plan = Plan(plate);
        var (low, high) = Extent(wall);
        var middle = new Point2D(wall.Origin.X + wall.U.X * (low + high) / 2, wall.Origin.Y + wall.U.Y * (low + high) / 2);
        return Polygon2D.Contains(plan, middle) || Enumerable.Range(0, plan.Count).Any(i => Line2D.DistanceFromSegment(middle, plan[i], plan[(i + 1) % plan.Count]) <= wall.Thickness / 2 + 300);
    }
}
