using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a load case is: the structure's own weight, the rest of what is built, what is put on its floors and roofs, snow, wind.</summary>
public enum LoadNature
{
    SelfWeight,
    Dead,
    Imposed,
    RoofImposed,
    Snow,
    Wind
}

/// <summary>A load case: its name - G1, Q1, W3 - what it is, and for wind, the way it blows.</summary>
public sealed record LoadCase(string Name, string Description, LoadNature Nature, Vector2D? Wind = null)
{
    public bool Permanent => Nature is LoadNature.SelfWeight or LoadNature.Dead;
}

/// <summary>Whether a combination is for strength or for how the structure behaves in use.</summary>
public enum LimitState
{
    Ultimate,
    Serviceability
}

/// <summary>A combination of load cases, each by its factor, and the expression of the standard it is.</summary>
public sealed record LoadCombination(string Name, string Description, LimitState State, IReadOnlyList<(LoadCase Case, double Factor)> Terms)
{
    /// <summary>Whether it is a factored combination to check strength under - not ASCE 7's allowable stress combinations of 2.4.1, kept for working in that way.</summary>
    public bool Factored => State == LimitState.Ultimate && !Name.StartsWith("ASD", StringComparison.Ordinal);

    /// <summary>Its terms said: "1.35 G1 + 1.35 G2 + 1.5 Q1".</summary>
    public string Said => string.Join(" + ", Terms.Select(term => $"{term.Factor:0.###} {term.Case.Name}"));
}

/// <summary>A load spread over a panel: its case, kN/m² of its area as a force in the building's directions - down for weight, across for wind - and what it is.</summary>
public sealed record SurfaceLoad(LoadCase Case, AnalyticalPanel Panel, Direction3D Force, string Said);

/// <summary>
/// A load along a line, kN/m as a force in the building's directions: on a member, or along a
/// panel's edge from one of its nodes to the next, from so far to so far along it, mm - and what it is.
/// </summary>
public sealed record LineLoad(LoadCase Case, AnalyticalMember? Member, AnalyticalPanel? Panel, AnalyticalNode? From, AnalyticalNode? To, double Start, double End, Direction3D Force, string Said)
{
    public double Length => End - Start;
}

/// <summary>
/// The loads on the analytical model, case by case, and the combinations they are designed for.
///
/// In England, by the Eurocodes and their UK National Annexes. The structure's own weight is a case
/// of its own, G1, left to the analysis program to work from its members' and plates' sections and
/// materials; G2 is the rest of what is built - the layers of a wall or floor beyond its structure,
/// and the whole of a timber floor or pitched roof, which are not plates. Imposed loads by BS EN
/// 1991-1-1 and its National Annex: 1.5 kN/m² on a home's floors (category A), 2.5 on an office's
/// (B1); movable partitions as the uniform load 6.3.1.2(8) adds to them - 0.5, 0.8 or 1.2 kN/m² for
/// partitions of up to 1, 2 or 3 kN/m - and a heavier one said to be taken where it stands; on a
/// roof reached only to be kept up, category H, 0.6 kN/m² below 30° falling to nothing at 60°
/// (Table NA.7), not taken with snow or wind (3.3.2(1)). Snow by BS EN 1991-1-3 and its National
/// Annex: on the ground sk = 0.15 + (0.1Z + 0.05) + (A − 100)/525 for the site's zone and altitude
/// (Equation NA.1) - its altitude taken as no less than 100 m, the map's - and on a roof
/// s = μ1·Ce·Ct·sk, Ce and Ct 1.0 (NA.2.15, NA.2.16), μ1 0.8 up to 30°, 0.8(60 − α)/30 to 60°
/// (Table 5.2), the snow lying evenly. Wind by BS EN 1991-1-4: the peak velocity pressure at the
/// building's height - as its cladding is worked out - on the faces into and out of the wind,
/// zones D and E of Table 7.1 for its height over its depth, by the lack of correlation between
/// them (7.2.2(3)), the wind four ways along the building's axes. Combined by BS EN 1990 and its
/// National Annex, Set B (Table NA.A1.2(B)): expressions 6.10a and 6.10b - 1.35 Gk and 1.5 ψ0 Qk,
/// and 0.925 × 1.35 Gk with 1.5 Qk leading - each variable action leading in turn, with 1.0 Gk
/// and 1.5 Wk where the weight helps; and for use, the characteristic combination 6.14b and the
/// quasi-permanent 6.16b. ψ by Table NA.A1.1: imposed 0.7, 0.5, 0.3; roofs 0.7, 0, 0; snow 0.5,
/// 0.2, 0 up to 1000 m; wind 0.5, 0.2, 0.
///
/// In the US, by ASCE 7-16: dead loads D1 and D2 as above; live loads by Table 4.3-1 - 40 psf in a
/// one- or two-family dwelling, 30 in its sleeping areas, 60 on a balcony or deck, 50 in an office,
/// with 15 psf for partitions in an office (4.3.2) and a home's partitions taken at their weight;
/// roof live load Lr = 20 R1 R2 psf, R1 taken as 1 and R2 by the roof's rise, at least 12 (4.8.2);
/// snow on a roof ps = Cs·0.7·Ce·Ct·Is·pg (7.3-1, 7.4-1), Ce 1.0 partially exposed - 0.9 by the sea -
/// Ct and Is 1.0, Cs 1.0 up to 30° falling to nothing at 70° for a warm roof, and at least pg - 20
/// psf at most - on a roof under 15° (7.3.4); wind on the main structure by the directional
/// procedure, p = qh·G·Cp with G 0.85, Cp 0.8 into the wind and −0.5, −0.3 or −0.2 out of it by
/// its depth over its breadth (Figure 27.3-1), qh as the cladding's, the internal pressures
/// cancelling over the building. Combined by 2.3.1 for strength - the load factor on L taken as 0.5
/// in combinations 3 and 4, as its exception 1 lets for live loads of 100 psf and less - and by
/// 2.4.1 for allowable stress design.
///
/// Left out, and said so: drifted and unbalanced snow (the UK's Table NA.1, ASCE 7's 7.6), the wind
/// on side walls and roofs, internal pressure, the wind on walls that are not bearing, seismic loads,
/// and imposed loads reduced for the area carried.
/// </summary>
public sealed class StructuralLoading
{
    private StructuralLoading()
    {
    }

    public List<LoadCase> Cases { get; } = new();

    public List<LoadCombination> Combinations { get; } = new();

    public List<SurfaceLoad> SurfaceLoads { get; } = new();

    public List<LineLoad> LineLoads { get; } = new();

    /// <summary>What the loads rest on, and what is left out.</summary>
    public List<string> Notes { get; } = new();

    public List<AnalyticalProblem> Problems { get; } = new();

    /// <summary>A case's loads added up, kN down: the weight of it.</summary>
    public double Total(LoadCase loadCase) =>
        -SurfaceLoads.Where(load => load.Case == loadCase).Sum(load => load.Force.Z * load.Panel.Area / 1e6)
        - LineLoads.Where(load => load.Case == loadCase).Sum(load => load.Force.Z * load.Length / 1000);

    /// <summary>A case's loads across, kN: the push of the wind.</summary>
    public double Across(LoadCase loadCase) =>
        SurfaceLoads.Where(load => load.Case == loadCase).Sum(load => Math.Sqrt(load.Force.X * load.Force.X + load.Force.Y * load.Force.Y) * load.Panel.Area / 1e6);

    public static StructuralLoading Of(BimDocument document, AnalyticalModel model) => new Builder(document, model).Build();

    // ---- the standards' figures ------------------------------------------------------

    /// <summary>The UK's characteristic snow load on the ground, kN/m², for the site's zone and altitude: Equation NA.1, the altitude no less than the map's 100 m.</summary>
    public static double GroundSnow(double zone, double altitude) => 0.15 + (0.1 * zone + 0.05) + (Math.Max(altitude, 100) - 100) / 525;

    /// <summary>BS EN 1991-1-3 Table 5.2's μ1 for a roof's pitch, degrees: the snow lying evenly.</summary>
    public static double ShapeCoefficient(double pitch) => pitch <= 30 ? 0.8 : pitch < 60 ? 0.8 * (60 - pitch) / 30 : 0;

    /// <summary>ASCE 7-16 Figure 7.4-1's Cs for a warm roof that is not slippery: 1 up to 30°, falling to nothing at 70°.</summary>
    public static double SlopeFactor(double pitch) => pitch <= 30 ? 1 : pitch < 70 ? (70 - pitch) / 40 : 0;

    /// <summary>A US roof's snow load, psf, for its pitch: Cs·pf, pf = 0.7·Ce·Ct·Is·pg - and at least the least of 7.3.4 under 15°.</summary>
    public static double RoofSnowPsf(BimDocument document, double pitch)
    {
        var project = document.ProjectInformation;
        var exposure = project.Terrain == WindTerrain.Sea ? 0.9 : 1.0;
        var flat = 0.7 * exposure * 1.0 * 1.0 * project.GroundSnowLoad;
        var sloped = SlopeFactor(pitch) * flat;
        return pitch < 15 ? Math.Max(sloped, Math.Min(project.GroundSnowLoad, 20)) : sloped;
    }

    /// <summary>The snow on a roof of a pitch, kN/m² of its plan.</summary>
    public static double RoofSnow(BimDocument document, double pitch) =>
        document.ProjectInformation.ClearanceStandard.IsUs()
            ? RoofSnowPsf(document, pitch) * Loads.Psf
            : ShapeCoefficient(pitch) * GroundSnow(document.ProjectInformation.SnowZone, document.ProjectInformation.SiteAltitude);

    /// <summary>What is put on a roof to keep it up, kN/m² of its plan, for its pitch: category H (Table NA.7), or Lr (ASCE 7-16 4.8.2).</summary>
    public static double RoofImposed(BimDocument document, double pitch)
    {
        if (!document.ProjectInformation.ClearanceStandard.IsUs()) return pitch < 30 ? 0.6 : pitch < 60 ? 0.6 * (60 - pitch) / 30 : 0;
        var rise = 12 * Math.Tan(pitch * Math.PI / 180);
        var r2 = rise <= 4 ? 1 : rise < 12 ? 1.2 - 0.05 * rise : 0.6;
        return Math.Max(12, 20 * r2) * Loads.Psf;
    }

    /// <summary>What is put on a floor, kN/m², by its rooms' use - the most of them - and what that is.</summary>
    public static (double Load, string Said) FloorImposed(BimDocument document, Slab slab)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var office = document.ProjectInformation.BuildingType.Contains("office", StringComparison.OrdinalIgnoreCase);
        var rooms = document.All<Room>().Where(room => room.LevelId == slab.LevelId && slab.Contains(room.Location)).ToList();
        (double, string) Of(Room? room)
        {
            var name = $"{room?.Name} {room?.Occupancy}".ToLowerInvariant();
            var isOffice = office || name.Contains("office");
            if (!us) return isOffice ? (2.5, "office, B1") : (1.5, "home, A1");
            if (isOffice) return (50 * Loads.Psf, "office, 50 psf");
            if (name.Contains("balcony") || name.Contains("deck")) return (60 * Loads.Psf, "balcony, 1.5 x 40 psf");
            if (name.Contains("bed")) return (30 * Loads.Psf, "sleeping area, 30 psf");
            return (40 * Loads.Psf, "dwelling, 40 psf");
        }

        return rooms.Count == 0 ? Of(null) : rooms.Select(Of).OrderByDescending(found => found.Item1).First();
    }

    /// <summary>The ψ0, ψ1 and ψ2 of a case: BS EN 1990 Table NA.A1.1.</summary>
    public static (double Combination, double Frequent, double QuasiPermanent) Psi(BimDocument document, LoadNature nature) => nature switch
    {
        LoadNature.Imposed => (0.7, 0.5, 0.3),
        LoadNature.RoofImposed => (0.7, 0, 0),
        LoadNature.Snow => document.ProjectInformation.SiteAltitude > 1000 ? (0.7, 0.5, 0.2) : (0.5, 0.2, 0),
        LoadNature.Wind => (0.5, 0.2, 0),
        _ => (1, 1, 1)
    };

    private sealed class Builder
    {
        private readonly BimDocument _document;
        private readonly AnalyticalModel _model;
        private readonly StructuralLoading _loading = new();
        private readonly bool _us;
        private LoadCase _self = null!, _dead = null!, _imposed = null!, _roof = null!, _snow = null!;
        private readonly List<LoadCase> _wind = new();

        public Builder(BimDocument document, AnalyticalModel model)
        {
            _document = document;
            _model = model;
            _us = document.ProjectInformation.ClearanceStandard.IsUs();
        }

        public StructuralLoading Build()
        {
            _self = new LoadCase(_us ? "D1" : "G1", "Self weight of the members and plates, worked by the analysis program", LoadNature.SelfWeight);
            _dead = new LoadCase(_us ? "D2" : "G2", "The rest of what is built: finishes, timber floors and pitched roofs", LoadNature.Dead);
            _imposed = new LoadCase(_us ? "L1" : "Q1", _us ? "Live load on the floors (ASCE 7-16 Table 4.3-1)" : "Imposed load on the floors (BS EN 1991-1-1)", LoadNature.Imposed);
            _roof = new LoadCase(_us ? "Lr1" : "Q2", _us ? "Roof live load (ASCE 7-16 4.8)" : "Imposed load on the roofs, category H", LoadNature.RoofImposed);
            _snow = new LoadCase("S1", _us ? "Snow (ASCE 7-16 Chapter 7)" : "Snow (BS EN 1991-1-3)", LoadNature.Snow);
            foreach (var (way, name) in new[] { (Vector2D.UnitX, "+X"), (-Vector2D.UnitX, "-X"), (Vector2D.UnitY, "+Y"), (-Vector2D.UnitY, "-Y") })
                _wind.Add(new LoadCase($"W{_wind.Count + 1}", $"Wind {name}", LoadNature.Wind, way));

            Panels();
            SpanningFloors();
            PitchedRoofs();
            Wind();

            // Only the cases something is put in - and the structure's own weight, always.
            var used = _loading.SurfaceLoads.Select(load => load.Case).Concat(_loading.LineLoads.Select(load => load.Case)).ToHashSet();
            _loading.Cases.Add(_self);
            foreach (var each in new[] { _dead, _imposed, _roof, _snow }.Concat(_wind))
                if (used.Contains(each)) _loading.Cases.Add(each);
            if (_us) UsCombinations();
            else UkCombinations();
            Notes();
            return _loading;
        }

        private static readonly Direction3D Down = new(0, 0, -1);

        private static Direction3D Times(Direction3D way, double by) => new(way.X * by, way.Y * by, way.Z * by);

        private void Surface(LoadCase loadCase, AnalyticalPanel panel, double down, string said)
        {
            if (Math.Abs(down) < 1e-6) return;
            _loading.SurfaceLoads.Add(new SurfaceLoad(loadCase, panel, Times(Down, down), said));
        }

        // ---- the plates ------------------------------------------------------------

        private void Panels()
        {
            foreach (var panel in _model.Panels)
            {
                // What the analysis program's self weight is to be made up to.
                var rest = panel.Weight - panel.SelfWeight;
                if (panel.Kind == AnalyticalPanelKind.Raft)
                    rest += _document.All<Floor>().Where(floor => floor.LevelId == ((RaftFoundation)panel.Element).LevelId && Polygon2D.Contains(((RaftFoundation)panel.Element).Outline, floor.Centroid) && _document.FindType<SlabType>(floor.TypeId) is not null)
                        .Select(floor => Loads.Weight(_document, _document.FindType<SlabType>(floor.TypeId)!.Structure)).DefaultIfEmpty(0).Max();
                Surface(_dead, panel, rest, panel.Kind == AnalyticalPanelKind.Wall ? "its layers beyond its structure" : "its build-up beyond its structure");

                switch (panel.Kind)
                {
                    case AnalyticalPanelKind.Floor or AnalyticalPanelKind.GroundSlab or AnalyticalPanelKind.Raft:
                    {
                        var slab = panel.Element as Slab ?? _document.All<Floor>().FirstOrDefault(floor => panel.Element is RaftFoundation raft && floor.LevelId == raft.LevelId && Polygon2D.Contains(raft.Outline, floor.Centroid));
                        var (load, said) = slab is null ? (_us ? 40 * Loads.Psf : 1.5, _us ? "dwelling, 40 psf" : "home, A1") : FloorImposed(_document, slab);
                        var (partition, dead, why) = slab is null ? (0.0, 0.0, string.Empty) : Partitions(slab);
                        Surface(_imposed, panel, load + partition, partition > 0 ? $"{said}, with {why}" : said);
                        Surface(_dead, panel, dead, why);
                        break;
                    }
                    case AnalyticalPanelKind.Roof:
                        Surface(_roof, panel, RoofImposed(_document, 0), _us ? "Lr, 20 psf" : "category H, 0.6 kN/m²");
                        Surface(_snow, panel, RoofSnow(_document, 0), SnowSaid(0));
                        break;
                }
            }
        }

        /// <summary>
        /// The partitions on a floor - its walls that are not bearing - as a load spread over it: in
        /// England the allowance 6.3.1.2(8) adds to the imposed load for the heaviest, kN/m²; in the US
        /// in an office 15 psf live (4.3.2), in a home their weight spread over it, dead.
        /// </summary>
        private (double Imposed, double Dead, string Said) Partitions(Slab slab)
        {
            var walls = _document.Walls.Where(wall => wall.LevelId == slab.LevelId && wall.StructuralUsage == StructuralUsage.NonBearing && !_document.IsCurtainWall(wall)
                                                     && slab.Contains(wall.Start.MidpointTo(wall.End)) && _document.GetWallType(wall) is not null).ToList();
            if (walls.Count == 0) return (0, 0, string.Empty);
            var perMetre = walls.Select(wall => (Wall: wall, Load: Loads.Weight(_document, _document.GetWallType(wall)!.Structure) * wall.GetHeight(_document) / 1000)).ToList();

            if (_us)
            {
                var office = FloorImposed(_document, slab).Said.StartsWith("office", StringComparison.Ordinal);
                if (office) return (15 * Loads.Psf, 0, "partitions, 15 psf");
                var spread = perMetre.Sum(each => each.Load * each.Wall.Length / 1000) / Math.Max(slab.Area / 1e6, 1);
                return (0, spread, $"partitions' weight spread over it, {Units.Kilopascals(spread, "0.00")}");
            }

            var heaviest = perMetre.Max(each => each.Load);
            if (heaviest > 3)
            {
                _loading.Problems.Add(new AnalyticalProblem($"A partition of {heaviest:0.0} kN/m stands on it - heavier than the 3 kN/m BS EN 1991-1-1 6.3.1.2(8) spreads: it is to be taken where it stands (6.3.1.2(9)), as a bearing wall or a load of its own.",
                    new Element[] { slab, perMetre.First(each => each.Load == heaviest).Wall }));
                return (1.2, 0, "partitions, 1.2 kN/m² for those up to 3 kN/m");
            }

            var allowance = heaviest <= 1 ? 0.5 : heaviest <= 2 ? 0.8 : 1.2;
            return (allowance, 0, $"partitions of up to {heaviest:0.0} kN/m, {allowance:0.0} kN/m² (6.3.1.2(8))");
        }

        private string SnowSaid(double pitch) => _us
            ? $"Cs {SlopeFactor(pitch):0.00} x 0.7 Ce Ct Is pg, pg {_document.ProjectInformation.GroundSnowLoad:0} psf"
            : $"μ1 {ShapeCoefficient(pitch):0.00} x sk {GroundSnow(_document.ProjectInformation.SnowZone, _document.ProjectInformation.SiteAltitude):0.00} kN/m², zone {_document.ProjectInformation.SnowZone:0.#}";

        // ---- what spans onto the frame ------------------------------------------------

        /// <summary>A line something spanning bears on: a wall's top edge, or a beam - its ends in plan, and its height.</summary>
        private sealed record Bearing(AnalyticalPanel? Panel, AnalyticalNode? From, AnalyticalNode? To, AnalyticalMember? Member, Point2D A, Point2D B);

        /// <summary>The lines at a height things span onto: the bearing walls' tops, the beams and joists.</summary>
        private List<Bearing> BearingsAt(double height)
        {
            var found = new List<Bearing>();
            foreach (var panel in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall))
            foreach (var (from, to) in panel.Edges)
            {
                if (Math.Abs(from.At.Z - to.At.Z) > 1 || Math.Abs(from.At.Z - height) > 600) continue;
                var top = panel.Outline.Max(node => node.At.Z);
                if (from.At.Z < top - 1 && !TopAt(panel, from, to)) continue;
                found.Add(new Bearing(panel, from, to, null, new Point2D(from.At.X, from.At.Y), new Point2D(to.At.X, to.At.Y)));
            }

            foreach (var member in _model.Members.Where(member => member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist))
                if (Math.Abs((member.Start.At.Z + member.End.At.Z) / 2 - height) <= 600)
                    found.Add(new Bearing(null, null, null, member, new Point2D(member.Start.At.X, member.Start.At.Y), new Point2D(member.End.At.X, member.End.At.Y)));
            return found;
        }

        /// <summary>Whether a level edge of a wall's plate is along its top: nothing of the plate over it.</summary>
        private static bool TopAt(AnalyticalPanel panel, AnalyticalNode from, AnalyticalNode to)
        {
            var middle = new Point3D((from.At.X + to.At.X) / 2, (from.At.Y + to.At.Y) / 2, (from.At.Z + to.At.Z) / 2 + 20);
            return !Polygon2D.Contains(panel.FlatOutline, panel.Flat(middle));
        }

        /// <summary>
        /// Each floor that is not a plate, its weight and what is put on it spread one way - the way it
        /// spans, as drawn, or across its short way - onto the walls and beams it bears on, each
        /// taking half the way to the next on either side, as a uniform load along the stretch of it
        /// under the floor.
        /// </summary>
        private void SpanningFloors()
        {
            foreach (var floor in _document.All<Floor>())
            {
                if (floor.Boundary.Count < 3 || AnalyticalModel.IsPlate(_document, floor) || _document.FindType<SlabType>(floor.TypeId) is not { } type) continue;
                if (_document.All<RaftFoundation>().Any(raft => raft.LevelId == floor.LevelId && Polygon2D.Contains(raft.Outline, floor.Centroid))) continue;
                var height = floor.GetTopElevation(_document);
                var (imposed, said) = FloorImposed(_document, floor);
                var (partition, partitionDead, why) = Partitions(floor);
                var weight = Loads.Weight(_document, type.Structure) + partitionDead;
                var spread = Spread(floor, height, out var carried);
                JoistsLeftOut(floor, height);
                if (spread.Count == 0)
                {
                    // On the ground, onto nothing: it is carried by the ground under it.
                    if (Math.Abs((_document.FindLevel(floor.LevelId)?.Elevation ?? 0) - Lowest) < 1) continue;
                    _loading.Problems.Add(new AnalyticalProblem("It spans onto nothing in the analytical model: no bearing wall or beam is under it, across the way it spans.", new Element[] { floor }));
                    continue;
                }

                if (carried < 0.9 * floor.Area / 1e6)
                    _loading.Problems.Add(new AnalyticalProblem($"Only {carried / (floor.Area / 1e6):0%} of it reaches what carries it in the analytical model, spanning the way it does: the rest has no bearing wall or beam across it.", new Element[] { floor }));

                foreach (var (bearing, start, end, width) in spread)
                {
                    Line(_dead, bearing, start, end, weight * width / 1000, $"{width:0} mm of the floor at {weight:0.00} kN/m²");
                    Line(_imposed, bearing, start, end, (imposed + partition) * width / 1000, $"{width:0} mm of the floor, {said}{(partition > 0 ? $", with {why}" : "")}");
                }
            }
        }

        private double Lowest => _document.Levels.Count == 0 ? 0 : _document.Levels.Min(level => level.Elevation);

        private void Line(LoadCase loadCase, Bearing bearing, double start, double end, double down, string said)
        {
            if (Math.Abs(down) < 1e-6 || end - start < 1) return;
            _loading.LineLoads.Add(new LineLoad(loadCase, bearing.Member, bearing.Panel, bearing.From, bearing.To, start, end, Times(Down, down), said));
        }

        /// <summary>
        /// How a floor's load is spread onto what it bears on: each bearing, the stretch of it under the
        /// floor - from so far to so far along it - and the width of floor it takes, mm; and the area that
        /// reaches them all, m².
        /// </summary>
        private List<(Bearing Bearing, double Start, double End, double Width)> Spread(Slab floor, double height, out double carried)
        {
            var way = SpanOf(floor);
            var bearings = BearingsAt(height).Where(bearing => Math.Abs((bearing.B - bearing.A).NormalisedOrDefault(Vector2D.UnitX).Dot(way)) <= 0.7).ToList();
            var spread = new List<(Bearing, double, double, double)>();
            carried = 0;
            const double step = 250;
            foreach (var bearing in bearings)
            {
                var along = bearing.B - bearing.A;
                var length = along.Length;
                if (length < 1) continue;
                var unit = along / length;
                var (first, last, sum, count) = (double.NaN, double.NaN, 0.0, 0);
                for (var at = step / 2; at < length; at += step)
                {
                    var point = bearing.A + unit * at;
                    var width = 0.0;
                    foreach (var side in new[] { way, -way })
                        width += Reach(floor, point, side, bearings, bearing) / 2;
                    if (width <= 0) continue;
                    if (double.IsNaN(first)) first = at;
                    last = at;
                    sum += width;
                    count++;
                }

                if (count == 0) continue;
                var (start, end) = (Math.Max(0, first - step / 2), Math.Min(length, last + step / 2));
                var mean = sum / count;
                spread.Add((bearing, start, end, mean));
                carried += mean * (end - start) / 1e6;
            }

            return spread;
        }

        /// <summary>
        /// Joists run the way a floor spans carry none of it, and their checks then read as if all were well. It is said, and left as it is: which way a floor spans is for whoever draws it.
        /// </summary>
        private void JoistsLeftOut(Slab floor, double height)
        {
            var way = SpanOf(floor);
            var idle = _model.Members.Where(member => member.Kind == AnalyticalMemberKind.Joist && Math.Abs(member.Start.At.Z - height) < 600 && floor.Contains(new Point2D((member.Start.At.X + member.End.At.X) / 2, (member.Start.At.Y + member.End.At.Y) / 2))
                && Math.Abs(((member.End.At.X - member.Start.At.X) * way.X + (member.End.At.Y - member.Start.At.Y) * way.Y) / Math.Max(1, member.Length)) > 0.7).ToList();
            if (idle.Count > 0)
                _loading.Problems.Add(new AnalyticalProblem($"It spans the way {idle.Count} of the joists under it run, so they carry none of it: set its span direction across them.", new Element[] { floor }));
        }

        /// <summary>The way a floor spans: as drawn, or across its short way.</summary>
        private static Vector2D SpanOf(Slab floor)
        {
            if (floor is Floor { SpanDirection: { } degrees }) return new Vector2D(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));
            var (minX, maxX, minY, maxY) = (floor.Boundary.Min(p => p.X), floor.Boundary.Max(p => p.X), floor.Boundary.Min(p => p.Y), floor.Boundary.Max(p => p.Y));
            return maxX - minX <= maxY - minY ? Vector2D.UnitX : Vector2D.UnitY;
        }

        /// <summary>How far a floor runs from a point one way before another bearing holds it, or it ends; nothing where it does not go that way.</summary>
        private static double Reach(Slab floor, Point2D from, Vector2D way, IReadOnlyList<Bearing> bearings, Bearing own)
        {
            if (!floor.Contains(from + way * 60)) return 0;
            const double far = 30000;
            var to = from + way * far;
            var nearest = far;
            var boundary = floor.Boundary;
            for (var i = 0; i < boundary.Count; i++)
                if (Crossing(from, to, boundary[i], boundary[(i + 1) % boundary.Count]) is { } t && t * far > 50) nearest = Math.Min(nearest, t * far);
            foreach (var hole in floor.Holes)
                for (var i = 0; i < hole.Count; i++)
                    if (Crossing(from, to, hole[i], hole[(i + 1) % hole.Count]) is { } t && t * far > 50) nearest = Math.Min(nearest, t * far);
            foreach (var other in bearings)
                if (!ReferenceEquals(other, own) && Crossing(from, to, other.A, other.B) is { } t && t * far > 50) nearest = Math.Min(nearest, t * far);
            return nearest >= far ? 0 : nearest;
        }

        private static double? Crossing(Point2D a, Point2D b, Point2D c, Point2D d)
        {
            var (r, s) = (b - a, d - c);
            var denominator = r.Cross(s);
            if (Math.Abs(denominator) < 1e-9) return null;
            var t = (c - a).Cross(s) / denominator;
            var u = (c - a).Cross(r) / denominator;
            return t is >= 0 and <= 1 && u is >= -1e-6 and <= 1 + 1e-6 ? t : null;
        }

        /// <summary>
        /// Each pitched roof - one not a plate - on the walls its eaves run along: each slope's weight
        /// up the slope, what is put on it and its snow, its plan area shared along its eave, onto the
        /// top of the bearing wall under the eave, as trussed rafters bear on the walls at their feet.
        /// </summary>
        private void PitchedRoofs()
        {
            foreach (var roof in _document.All<Roof>())
            {
                if (AnalyticalModel.IsPlate(_document, roof) || roof.IsExtrusion || roof.Boundary.Count < 3 || roof.Edges.Count != roof.Boundary.Count) continue;
                if (_document.FindType<SlabType>(roof.TypeId) is not { } type) continue;
                var surface = roof.Surface(_document);
                if (surface.IsFlat)
                {
                    FlatRoof(roof, type);
                    continue;
                }

                var bearings = BearingsAt(roof.BaseElevation(_document)).Where(bearing => bearing.Panel is not null).ToList();
                var count = roof.Boundary.Count;
                var weight = Loads.Weight(_document, type.Structure);
                for (var i = 0; i < count; i++)
                {
                    var edge = roof.Edges[i];
                    var (a, b) = (roof.Boundary[i], roof.Boundary[(i + 1) % count]);
                    var eave = a.DistanceTo(b);
                    if (eave < 1) continue;
                    var along = (b - a) / eave;
                    var inward = along.PerpendicularLeft() * (Polygon2D.SignedArea(roof.Boundary) >= 0 ? 1 : -1);
                    var face = edge.DefinesSlope ? surface.FaceAt(a.MidpointTo(b) + inward * 1) : null;
                    var pitch = face?.Plane.SlopeDegrees ?? 0;
                    var area = face is null ? 0 : face.Area / 1e6;
                    if (area <= 0) continue;

                    // The walls under this eave: along it, within its overhang of it.
                    var under = bearings.Where(bearing =>
                    {
                        var way = (bearing.B - bearing.A).NormalisedOrDefault(Vector2D.UnitX);
                        return Math.Abs(way.Cross(along)) < 0.05 && Line2D.DistanceFromSegment(bearing.A.MidpointTo(bearing.B), a - along * 1e5, b + along * 1e5) <= edge.Overhang + 600;
                    }).ToList();
                    if (under.Count == 0)
                    {
                        _loading.Problems.Add(new AnalyticalProblem($"Its slope over the eave from ({a.X:0}, {a.Y:0}) bears on nothing in the analytical model: no bearing wall runs under it.", new Element[] { roof }));
                        continue;
                    }

                    // The part of each wall's top under this eave - the whole slope shared along them, its overhang past their ends with it.
                    var stretches = under.Select(bearing =>
                    {
                        var length = bearing.A.DistanceTo(bearing.B);
                        var unit = (bearing.B - bearing.A) / Math.Max(length, 1e-9);
                        var (s0, s1) = ((a - bearing.A).Dot(unit), (b - bearing.A).Dot(unit));
                        return (Bearing: bearing, Start: Math.Max(0, Math.Min(s0, s1)), End: Math.Min(length, Math.Max(s0, s1)));
                    }).Where(each => each.End - each.Start >= 1).ToList();
                    var covered = stretches.Sum(each => each.End - each.Start);
                    if (covered < 1) continue;
                    var perMetre = area / (covered / 1000);
                    var stretch = face!.Plane.VerticalStretch;
                    foreach (var (bearing, start, end) in stretches)
                    {
                        Line(_dead, bearing, start, end, perMetre * weight * stretch, $"{perMetre:0.00} m² a metre of the roof at {pitch:0}°, {weight * stretch:0.00} kN/m² on plan");
                        Line(_roof, bearing, start, end, perMetre * RoofImposed(_document, pitch), _us ? $"Lr at {pitch:0}°" : $"category H at {pitch:0}°");
                        Line(_snow, bearing, start, end, perMetre * RoofSnow(_document, pitch), SnowSaid(pitch));
                    }
                }
            }
        }

        /// <summary>A flat roof that is not a plate - joists, a deck - spread one way onto what carries it, as a floor is.</summary>
        private void FlatRoof(Roof roof, SlabType type)
        {
            var spread = Spread(roof, roof.BaseElevation(_document), out _);
            if (spread.Count == 0)
            {
                _loading.Problems.Add(new AnalyticalProblem("It spans onto nothing in the analytical model: no bearing wall or beam is under it, across its short way.", new Element[] { roof }));
                return;
            }

            var weight = Loads.Weight(_document, type.Structure);
            foreach (var (bearing, start, end, width) in spread)
            {
                Line(_dead, bearing, start, end, weight * width / 1000, $"{width:0} mm of the roof at {weight:0.00} kN/m²");
                Line(_roof, bearing, start, end, RoofImposed(_document, 0) * width / 1000, $"{width:0} mm of the roof, {(_us ? "Lr" : "category H")}");
                Line(_snow, bearing, start, end, RoofSnow(_document, 0) * width / 1000, $"{width:0} mm of the roof, {SnowSaid(0)}");
            }
        }

        // ---- wind -------------------------------------------------------------------------

        /// <summary>The wind on the bearing outside walls into and out of it, four ways along the building's axes.</summary>
        private void Wind()
        {
            var project = _document.ProjectInformation;
            var outside = _document.Walls.Where(wall => !wall.IsCurved && (_document.IsCurtainWall(wall) || _document.GetWallType(wall) is { Function: WallFunction.Exterior })).ToList();
            if (outside.Count == 0) return;
            var corners = outside.SelectMany(wall => new[] { wall.Start, wall.End }).ToList();
            var heightMm = Architecture.Wind.Height(_document);
            var height = heightMm / 1000;

            foreach (var wind in _wind)
            {
                var way = wind.Wind!.Value;
                var depth = Math.Max(1, (corners.Max(p => (p - default(Point2D)).Dot(way)) - corners.Min(p => (p - default(Point2D)).Dot(way))) / 1000);
                var breadth = Math.Max(1, (corners.Max(p => (p - default(Point2D)).Cross(way)) - corners.Min(p => (p - default(Point2D)).Cross(way))) / 1000);
                double into, outOf;
                string said;
                if (_us)
                {
                    var q = Architecture.Wind.FromSite(_document) ? Architecture.Wind.VelocityPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height) : project.WindPressure;
                    var ratio = depth / breadth;
                    var leeward = ratio <= 1 ? 0.5 : ratio <= 2 ? 0.5 - 0.2 * (ratio - 1) : ratio <= 4 ? 0.3 - 0.05 * (ratio - 2) : 0.2;
                    (into, outOf) = (q * 0.85 * 0.8, q * 0.85 * leeward);
                    said = $"qh {Units.Kilopascals(q, "0.00")} x G 0.85 x Cp 0.8 and -{leeward:0.00}, L/B {ratio:0.00} (ASCE 7-16 Figure 27.3-1)";
                }
                else
                {
                    var qp = Architecture.Wind.FromSite(_document) ? Architecture.Wind.PeakPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height) : project.WindPressure;
                    var ratio = height / depth;
                    double Between(double low, double one, double five) => ratio <= 0.25 ? low : ratio <= 1 ? low + (one - low) * (ratio - 0.25) / 0.75 : ratio < 5 ? one + (five - one) * (ratio - 1) / 4 : five;
                    var (d, e) = (Between(0.7, 0.8, 0.8), Between(0.3, 0.5, 0.7));
                    var correlation = ratio <= 1 ? 0.85 : ratio >= 5 ? 1 : 0.85 + 0.15 * (ratio - 1) / 4;
                    (into, outOf) = (correlation * d * qp, correlation * e * qp);
                    said = $"qp {qp:0.00} kPa x {correlation:0.00} x cpe +{d:0.00} (D) and -{e:0.00} (E), h/d {ratio:0.00} (BS EN 1991-1-4 Table 7.1, 7.2.2(3))";
                }

                foreach (var panel in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Element is Wall wall && _document.GetWallType(wall) is { Function: WallFunction.Exterior }))
                {
                    var facing = ((Wall)panel.Element).ExteriorNormal.Dot(way);
                    var pressure = facing < -0.7 ? into : facing > 0.7 ? outOf : 0;
                    if (pressure <= 0) continue;
                    _loading.SurfaceLoads.Add(new SurfaceLoad(wind, panel, new Direction3D(way.X * pressure, way.Y * pressure, 0), $"{(facing < 0 ? "into the wind" : "out of the wind")}: {said}"));
                }
            }
        }

        // ---- combinations ---------------------------------------------------------------

        private bool Has(LoadCase loadCase) => _loading.Cases.Contains(loadCase);

        private void Add(string description, LimitState state, IEnumerable<(LoadCase Case, double Factor)> terms)
        {
            var list = terms.Where(term => Has(term.Case) && term.Factor > 0).ToList();
            if (list.Count == 0) return;
            if (_loading.Combinations.Any(each => each.State == state && each.Terms.Count == list.Count && each.Terms.Zip(list).All(pair => pair.First.Case == pair.Second.Case && Math.Abs(pair.First.Factor - pair.Second.Factor) < 1e-9))) return;
            var prefix = state == LimitState.Ultimate ? (_us && description.Contains("2.4.1", StringComparison.Ordinal) ? "ASD" : "ULS") : "SLS";
            var number = _loading.Combinations.Count(each => each.Name.StartsWith(prefix, StringComparison.Ordinal)) + 1;
            _loading.Combinations.Add(new LoadCombination($"{prefix}{number}", description, state, list));
        }

        private IEnumerable<(LoadCase, double)> Permanent(double factor) => new[] { (_self, factor), (_dead, factor) };

        /// <summary>BS EN 1990 with the UK National Annex: 6.10a and 6.10b with each variable action leading, weight helping the wind, then 6.14b and 6.16b.</summary>
        private void UkCombinations()
        {
            var sets = new List<List<LoadCase>> { new() { _imposed, _roof }, new() { _imposed, _snow } };
            foreach (var wind in _wind) sets.Add(new List<LoadCase> { _imposed, _snow, wind });
            sets = sets.Select(set => set.Where(Has).ToList()).Where(set => set.Count > 0).ToList();
            double Psi0(LoadCase each) => Psi(_document, each.Nature).Combination;

            foreach (var set in sets)
                Add("BS EN 1990 6.10a", LimitState.Ultimate, Permanent(1.35).Concat(set.Select(each => (each, 1.5 * Psi0(each)))));
            foreach (var set in sets)
            foreach (var leading in set)
                Add($"BS EN 1990 6.10b, {leading.Name} leading", LimitState.Ultimate, Permanent(1.25).Append((leading, 1.5)).Concat(set.Where(each => each != leading).Select(each => (each, 1.5 * Psi0(each)))));
            foreach (var wind in _wind)
                Add($"BS EN 1990 6.10b, {wind.Name} leading, the weight helping", LimitState.Ultimate, Permanent(1.0).Append((wind, 1.5)));

            foreach (var set in sets)
            foreach (var leading in set)
                Add($"BS EN 1990 6.14b characteristic, {leading.Name} leading", LimitState.Serviceability, Permanent(1.0).Append((leading, 1.0)).Concat(set.Where(each => each != leading).Select(each => (each, Psi0(each)))));
            Add("BS EN 1990 6.16b quasi-permanent", LimitState.Serviceability, Permanent(1.0).Concat(new[] { _imposed, _roof, _snow }.Concat(_wind).Select(each => (each, Psi(_document, each.Nature).QuasiPermanent))));
        }

        /// <summary>ASCE 7-16: 2.3.1 for strength, 2.4.1 for allowable stress.</summary>
        private void UsCombinations()
        {
            var roofs = new[] { _roof, _snow }.Where(Has).ToList();
            if (roofs.Count == 0) roofs.Add(_roof);

            Add("ASCE 7-16 2.3.1 (1)", LimitState.Ultimate, Permanent(1.4));
            foreach (var roof in roofs)
                Add("ASCE 7-16 2.3.1 (2)", LimitState.Ultimate, Permanent(1.2).Append((_imposed, 1.6)).Append((roof, 0.5)));
            foreach (var roof in roofs)
            {
                Add("ASCE 7-16 2.3.1 (3), 0.5L", LimitState.Ultimate, Permanent(1.2).Append((roof, 1.6)).Append((_imposed, 0.5)));
                foreach (var wind in _wind)
                    Add("ASCE 7-16 2.3.1 (3), 0.5W", LimitState.Ultimate, Permanent(1.2).Append((roof, 1.6)).Append((wind, 0.5)));
            }

            foreach (var wind in _wind)
            foreach (var roof in roofs)
                Add("ASCE 7-16 2.3.1 (4), 0.5L", LimitState.Ultimate, Permanent(1.2).Append((wind, 1.0)).Append((_imposed, 0.5)).Append((roof, 0.5)));
            foreach (var wind in _wind)
                Add("ASCE 7-16 2.3.1 (5)", LimitState.Ultimate, Permanent(0.9).Append((wind, 1.0)));

            Add("ASCE 7-16 2.4.1 (1)", LimitState.Ultimate, Permanent(1.0));
            Add("ASCE 7-16 2.4.1 (2)", LimitState.Ultimate, Permanent(1.0).Append((_imposed, 1.0)));
            foreach (var roof in roofs)
                Add("ASCE 7-16 2.4.1 (3)", LimitState.Ultimate, Permanent(1.0).Append((roof, 1.0)));
            foreach (var roof in roofs)
                Add("ASCE 7-16 2.4.1 (4)", LimitState.Ultimate, Permanent(1.0).Append((_imposed, 0.75)).Append((roof, 0.75)));
            foreach (var wind in _wind)
                Add("ASCE 7-16 2.4.1 (5)", LimitState.Ultimate, Permanent(1.0).Append((wind, 0.6)));
            foreach (var wind in _wind)
            foreach (var roof in roofs)
                Add("ASCE 7-16 2.4.1 (6a)", LimitState.Ultimate, Permanent(1.0).Append((_imposed, 0.75)).Append((wind, 0.45)).Append((roof, 0.75)));
            foreach (var wind in _wind)
                Add("ASCE 7-16 2.4.1 (7)", LimitState.Ultimate, Permanent(0.6).Append((wind, 0.6)));
        }

        private void Notes()
        {
            var project = _document.ProjectInformation;
            if (_us)
            {
                _loading.Notes.Add($"Snow: pg {project.GroundSnowLoad:0} psf, Ce {(project.Terrain == WindTerrain.Sea ? 0.9 : 1.0):0.0}, Ct 1.0, Is 1.0 - the snow lying evenly; drifts and unbalanced loads (7.6 to 7.8) left out.");
                _loading.Notes.Add(Architecture.Wind.FromSite(_document)
                    ? $"Wind: {project.BasicWindSpeed:0} mph, exposure {(project.Terrain switch { WindTerrain.Sea => "D", WindTerrain.OpenCountry => "C", _ => "B" })}, on the bearing outside walls into and out of it; side walls, roofs and the walls not bearing left out."
                    : $"Wind: no basic wind speed set, the {Units.Kilopascals(project.WindPressure, "0.00")} set taken as qh.");
                _loading.Notes.Add("Live loads are not reduced for the area carried (4.7); seismic loads are not worked out.");
            }
            else
            {
                _loading.Notes.Add($"Snow: zone {project.SnowZone:0.#} at {project.SiteAltitude:0} m{(project.SiteAltitude < 100 ? ", taken as the map's 100 m" : "")}, sk {GroundSnow(project.SnowZone, project.SiteAltitude):0.00} kN/m² - the snow lying evenly; the drifted load of Table NA.1 and the exceptional drifts of Annex B left out.");
                _loading.Notes.Add(Architecture.Wind.FromSite(_document)
                    ? $"Wind: {project.BasicWindSpeed:0.0} m/s at {project.SiteAltitude:0} m, on the bearing outside walls into and out of it; side walls, roofs, internal pressure and the walls not bearing left out."
                    : $"Wind: no basic wind speed set, the {project.WindPressure:0.00} kPa set taken as qp.");
                _loading.Notes.Add("Imposed loads are not reduced for the area carried (6.3.1.2(10)); accidental actions are not combined.");
            }
        }
    }
}
