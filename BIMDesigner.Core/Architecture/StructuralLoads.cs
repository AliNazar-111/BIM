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
    Wind,

    /// <summary>The earthquake's equivalent lateral force, ASCE 7-16 12.8, the way its case's direction gives.</summary>
    Seismic
}

/// <summary>A load case: its name - G1, Q1, W3 - what it is, and for wind or an earthquake, the way it pushes. An earthquake's accidental torsion is a case of its own, about the way its forces go.</summary>
public sealed record LoadCase(string Name, string Description, LoadNature Nature, Vector2D? Wind = null, bool Torsion = false, bool Drifted = false, bool Accidental = false)
{
    // Drifted: a drifted arrangement of snow, taken instead of the snow lying evenly and never with it. Accidental: an action of the accidental design situation, combined by BS EN 1990 6.11b alone.
    public bool Permanent => Nature is LoadNature.SelfWeight or LoadNature.Dead;

    /// <summary>Whether it pushes sideways: the wind, or an earthquake. What reads racking and shear takes it; what reads weight leaves it out.</summary>
    public bool Lateral => Nature is LoadNature.Wind or LoadNature.Seismic;
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
    public string Said => string.Concat(Terms.Select((term, i) => $"{(i == 0 ? (term.Factor < 0 ? "-" : string.Empty) : term.Factor < 0 ? " - " : " + ")}{Math.Abs(term.Factor):0.###} {term.Case.Name}"));
}

/// <summary>
/// A load spread over a panel: its case, kN/m² of its area as a force in the building's directions - down for weight, across for wind - and what it is; with a ramp, that force where the ramp is
/// whole, less in proportion where it falls away, and none past it.
/// </summary>
public sealed record SurfaceLoad(LoadCase Case, AnalyticalPanel Panel, Direction3D Force, string Said, LoadRamp? Ramp = null)
{
    /// <summary>The area its force acts over at its full value, mm²: the plate's, or what of it a ramp covers, each part at its share.</summary>
    public double LoadedArea => Ramp is not { } ramp
        ? Panel.Area
        : ramp.Over(Panel.Outline.Select(node => new Point2D(node.At.X, node.At.Y)).ToList()) - Panel.Openings.Sum(opening => ramp.Over(opening.Select(point => new Point2D(point.X, point.Y)).ToList()));
}

/// <summary>
/// How a load that is not even lies on plan, mm: whole along a line through At, running Along it from From to To, falling straight to nothing Length further Away from it, and none behind
/// the line or past its ends - snow drifted against a wall or a parapet.
/// </summary>
public sealed record LoadRamp(Point2D At, Vector2D Away, Vector2D Along, double Length, double From, double To)
{
    /// <summary>The share of the load's whole value at a point.</summary>
    public double Share(Point2D point)
    {
        var (out_, along) = ((point - At).Dot(Away), (point - At).Dot(Along));
        return out_ < 0 || out_ > Length || along < From || along > To ? 0 : 1 - out_ / Length;
    }

    /// <summary>The ramp over an outline as an area at its whole value, mm²: the outline cut to the ramp's band, by its area and how far its middle lies out.</summary>
    public double Over(IReadOnlyList<Point2D> outline)
    {
        var cut = Clip(Clip(Clip(Clip(outline, Away, 0), -Away, -Length), Along, From), -Along, -To);
        if (cut.Count < 3) return 0;
        var (twice, cx, cy) = (0.0, 0.0, 0.0);
        for (var i = 0; i < cut.Count; i++)
        {
            var (p, q) = (cut[i], cut[(i + 1) % cut.Count]);
            var cross = p.X * q.Y - q.X * p.Y;
            twice += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }

        if (Math.Abs(twice) < 1e-9) return 0;
        var middle = new Point2D(cx / (3 * twice), cy / (3 * twice));
        return Math.Abs(twice) / 2 * (1 - (middle - At).Dot(Away) / Length);
    }

    /// <summary>What of an outline lies where (p - At) · normal is at least so much.</summary>
    private List<Point2D> Clip(IReadOnlyList<Point2D> outline, Vector2D normal, double least)
    {
        var kept = new List<Point2D>();
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var (sp, sq) = ((p - At).Dot(normal) - least, (q - At).Dot(normal) - least);
            if (sp >= 0) kept.Add(p);
            if (sp >= 0 != sq >= 0) kept.Add(p + (q - p) * (sp / (sp - sq)));
        }

        return kept;
    }
}

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

    /// <summary>The seismic design its earthquake cases are worked from, where there is one.</summary>
    public SeismicDesign? Seismic { get; private set; }

    private readonly Dictionary<LoadCase, double> _amplified = new();

    /// <summary>How much each accidental torsion case is amplified by in the combinations, Ax of 12.8.4.3: 1 until the analysis finds the building torsionally irregular.</summary>
    public double AmplificationOf(LoadCase torsion) => _amplified.GetValueOrDefault(torsion, 1);

    /// <summary>Amplifies an accidental torsion case in every combination that holds it, to Ax from what it was.</summary>
    public void Amplify(LoadCase torsion, double ax)
    {
        var was = AmplificationOf(torsion);
        if (Math.Abs(was - ax) < 1e-9) return;
        for (var i = 0; i < Combinations.Count; i++)
            if (Combinations[i].Terms.Any(term => term.Case == torsion))
                Combinations[i] = Combinations[i] with { Terms = Combinations[i].Terms.Select(term => term.Case == torsion ? (term.Case, term.Factor * ax / was) : term).ToList() };
        _amplified[torsion] = ax;
    }

    /// <summary>A case's loads added up, kN down: the weight of it.</summary>
    public double Total(LoadCase loadCase) =>
        -SurfaceLoads.Where(load => load.Case == loadCase).Sum(load => load.Force.Z * load.LoadedArea / 1e6)
        - LineLoads.Where(load => load.Case == loadCase).Sum(load => load.Force.Z * load.Length / 1000);

    /// <summary>A case's loads across, kN: the push of the wind, the way it blows - a side wall drawn off square to it adding nothing.</summary>
    public double Across(LoadCase loadCase) => loadCase.Wind is { } way
        ? Math.Abs(SurfaceLoads.Where(load => load.Case == loadCase).Sum(load => (load.Force.X * way.X + load.Force.Y * way.Y) * load.LoadedArea / 1e6))
        : SurfaceLoads.Where(load => load.Case == loadCase).Sum(load => Math.Sqrt(load.Force.X * load.Force.X + load.Force.Y * load.Force.Y) * load.LoadedArea / 1e6);

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
        private readonly List<LoadCase> _drifted = new();
        private bool _drifts;
        private readonly List<LoadCase> _wind = new();
        private readonly List<LoadCase> _drawn = new();
        private readonly List<LoadCase> _seismic = new();
        private readonly List<LoadCase> _torsion = new();

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
            _drifted.Add(new LoadCase("S2", _us ? "Snow unbalanced, each roof's first way leeward (ASCE 7-16 7.6.1)" : "Snow drifted, each roof's first way at half (BS EN 1991-1-3 5.3.3 case (ii))", LoadNature.Snow, null, false, true));
            _drifted.Add(new LoadCase("S3", _us ? "Snow unbalanced, each roof's other way leeward (ASCE 7-16 7.6.1)" : "Snow drifted, each roof's other way at half (BS EN 1991-1-3 5.3.3 case (iii))", LoadNature.Snow, null, false, true));
            foreach (var (way, name) in new[] { (Vector2D.UnitX, "+X"), (-Vector2D.UnitX, "-X"), (Vector2D.UnitY, "+Y"), (-Vector2D.UnitY, "-Y") })
                _wind.Add(new LoadCase($"W{_wind.Count + 1}", $"Wind {name}", LoadNature.Wind, way));

            Panels();
            SpanningFloors();
            PitchedRoofs();
            Wind();
            SnowAtSteps();
            _loading.Seismic = Architecture.Seismic.Of(_document, _model);
            if (_loading.Seismic is { Applies: true } shaken)
            {
                foreach (var (way, name) in new[] { (Vector2D.UnitX, "+X"), (-Vector2D.UnitX, "-X"), (Vector2D.UnitY, "+Y"), (-Vector2D.UnitY, "-Y") })
                    _seismic.Add(new LoadCase($"E{_seismic.Count + 1}", $"Earthquake {name} (ASCE 7-16 12.8)", LoadNature.Seismic, way));

                // Where the floors are not flexible, each level's mass is taken 5% of the building's width across the forces off where it is, both ways, 12.8.4.2.
                if (!shaken.Flexible)
                {
                    _torsion.Add(new LoadCase("T1", "Accidental torsion of the earthquake along X, 5% of the width across it (ASCE 7-16 12.8.4.2)", LoadNature.Seismic, Vector2D.UnitX, true));
                    _torsion.Add(new LoadCase("T2", "Accidental torsion of the earthquake along Y, 5% of the width across it (ASCE 7-16 12.8.4.2)", LoadNature.Seismic, Vector2D.UnitY, true));
                }
            }

            // Only the cases something is put in - and the structure's own weight, always. Snow drifted nowhere is the snow lying evenly again, and left out.
            if (!_drifts)
            {
                _loading.LineLoads.RemoveAll(load => _drifted.Contains(load.Case));
                _loading.SurfaceLoads.RemoveAll(load => _drifted.Contains(load.Case));
            }

            var used = _loading.SurfaceLoads.Select(load => load.Case).Concat(_loading.LineLoads.Select(load => load.Case)).ToHashSet();
            _loading.Cases.Add(_self);
            foreach (var each in new[] { _dead, _imposed, _roof, _snow }.Concat(_drifted).Concat(_stepped is null ? Array.Empty<LoadCase>() : new[] { _stepped }).Concat(_wind).Concat(_drawn))
                if (used.Contains(each)) _loading.Cases.Add(each);
            _loading.Cases.AddRange(_seismic);
            _loading.Cases.AddRange(_torsion);
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
                        foreach (var drifted in _drifted) Surface(drifted, panel, RoofSnow(_document, 0), SnowSaid(0));
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
        private readonly List<(Vector2D Outward, double Pitch, double PerMetre, List<(Bearing Bearing, double Start, double End)> Stretches, double Overhang, bool Hipped, Roof Roof, RoofFacet Face)> _slopes = new();

        /// <summary>
        /// A roof's drifted snow on one of its slopes, kN a metre of the wall under its eave, the slope the one the wind comes over (side 1), the one it drifts onto (-1) or neither (0). BS EN 1991-1-3
        /// 5.3.3: half μ1 on the slope the wind comes over, all of it on the other. ASCE 7-16 7.6.1 for 1/2 on 12 to 7 on 12, W the windward slope's eave to ridge on plan: of 20 ft or less, nothing
        /// windward and Is pg leeward; past that, 0.3 ps windward and ps leeward with the surcharge hd γ / √S over 8 hd √S / 3 from the ridge, 8 hd² γ / 3 a foot of eave in all, hd by (7.6-1) with lu
        /// W, γ = 0.13 pg + 14 no more than 30 pcf. The plan the slope covers is perMetre a metre of the wall; its eave is spread times as long as the wall.
        /// </summary>
        private double DriftedSnow(double pitch, double perMetre, double windwardDeep, double spread, int side)
        {
            if (side == 0) return perMetre * RoofSnow(_document, pitch);
            if (!_us) return perMetre * RoofSnow(_document, pitch) * (side > 0 ? 0.5 : 1);
            var slope = Math.Tan(pitch * Math.PI / 180);
            if (slope < 0.5 / 12 - 1e-9 || slope > 7.0 / 12 + 1e-9) return perMetre * RoofSnow(_document, pitch);
            var pg = _document.ProjectInformation.GroundSnowLoad;
            var w = windwardDeep / 0.3048;
            if (w <= 20) return side > 0 ? 0 : perMetre * pg * Loads.Psf;
            if (side > 0) return 0.3 * perMetre * RoofSnow(_document, pitch);
            var hd = Math.Max(0, 0.43 * Math.Pow(w, 1.0 / 3) * Math.Pow(pg + 10, 0.25) - 1.5);
            var gamma = Math.Min(0.13 * pg + 14, 30);
            return perMetre * RoofSnow(_document, pitch) + 8 * hd * hd * gamma / 3 * 4.44822 / 0.3048 / 1000 * spread;
        }

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
                var hipped = Enumerable.Range(0, count).Any(i => roof.Edges[i].DefinesSlope && roof.Edges[(i + 1) % count].DefinesSlope
                    && Math.Abs((roof.Boundary[(i + 1) % count] - roof.Boundary[i]).NormalisedOrDefault(Vector2D.UnitX).Cross((roof.Boundary[(i + 2) % count] - roof.Boundary[(i + 1) % count]).NormalisedOrDefault(Vector2D.UnitX))) > 0.3);
                var slopes = new List<(Vector2D Outward, double Pitch, double PerMetre, List<(Bearing Bearing, double Start, double End)> Stretches, double Deep, double Spread)>();
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
                    var placed = stretches.Select(each => (each.Bearing, each.Start, each.End)).ToList();
                    _slopes.Add((new Vector2D(-inward.X, -inward.Y), pitch, perMetre, placed, edge.Overhang, hipped, roof, face));
                    var deep = face.Outline.Max(point => (point - a).Dot(inward)) / 1000;
                    slopes.Add((new Vector2D(-inward.X, -inward.Y), pitch, perMetre, placed, deep, eave / covered));
                    foreach (var (bearing, start, end) in stretches)
                    {
                        Line(_dead, bearing, start, end, perMetre * weight * stretch, $"{perMetre:0.00} m² a metre of the roof at {pitch:0}°, {weight * stretch:0.00} kN/m² on plan");
                        Line(_roof, bearing, start, end, perMetre * RoofImposed(_document, pitch), _us ? $"Lr at {pitch:0}°" : $"category H at {pitch:0}°");
                        Line(_snow, bearing, start, end, perMetre * RoofSnow(_document, pitch), SnowSaid(pitch));
                    }
                }

                // Its snow drifted from one way and the other: the way its biggest slope faces is its first.
                if (slopes.Count == 0) continue;
                var first = slopes.MaxBy(each => each.PerMetre * each.Stretches.Sum(s => s.End - s.Start)).Outward;
                foreach (var (outward, pitch, perMetre, stretches, deep, spread) in slopes)
                {
                    var facing = outward.Dot(first);
                    var side = facing > 0.5 ? 1 : facing < -0.5 ? -1 : 0;
                    var opposite = slopes.Where(other => other.Outward.Dot(outward) < -0.5).Select(other => other.Deep).DefaultIfEmpty(deep).Max();
                    for (var arrangement = 0; arrangement < 2; arrangement++)
                    {
                        var drift = arrangement == 0 ? side : -side;
                        var load = DriftedSnow(pitch, perMetre, drift > 0 ? deep : opposite, spread, drift);
                        if (Math.Abs(load - perMetre * RoofSnow(_document, pitch)) > 1e-9) _drifts = true;
                        foreach (var (bearing, start, end) in stretches)
                            Line(_drifted[arrangement], bearing, start, end, load, $"{(side == 0 ? "across the drift" : drift > 0 ? "the side the wind comes over" : "the side it drifts onto")} at {pitch:0}°");
                    }
                }
            }
        }

        private readonly List<(Roof Roof, List<(Bearing Bearing, double Start, double End, double Width)> Spread)> _flats = new();

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
            _flats.Add((roof, spread));
            foreach (var (bearing, start, end, width) in spread)
            {
                Line(_drifted[0], bearing, start, end, RoofSnow(_document, 0) * width / 1000, $"{width:0} mm of a flat roof, {SnowSaid(0)}");
                Line(_drifted[1], bearing, start, end, RoofSnow(_document, 0) * width / 1000, $"{width:0} mm of a flat roof, {SnowSaid(0)}");
                Line(_dead, bearing, start, end, weight * width / 1000, $"{width:0} mm of the roof at {weight:0.00} kN/m²");
                Line(_roof, bearing, start, end, RoofImposed(_document, 0) * width / 1000, $"{width:0} mm of the roof, {(_us ? "Lr" : "category H")}");
                Line(_snow, bearing, start, end, RoofSnow(_document, 0) * width / 1000, $"{width:0} mm of the roof, {SnowSaid(0)}");
            }
        }

        // ---- snow drifted at steps and parapets ------------------------------------------

        private LoadCase? _stepped;

        /// <summary>
        /// Snow drifted against what rises above a flat roof's edge: a taller part of the building it abuts - a roof or floor beyond the wall as high as it or higher - or a parapet, nothing beyond. In the UK
        /// by BS EN 1991-1-3 Annex B, which its National Annex has used in place of 5.3.6 and 6.2: an exceptional drift, an accidental action with no snow elsewhere on the roof; against a taller part
        /// ls the least of 5h, b1 and 15 m and μ the least of 2h/sk, 2b/ls and 8, b the larger of b1 and b2, falling away with the pitch of the roof it lies on past 30° to nothing at 60° (Table B1's μ2,
        /// the larger of its two); behind a parapet the same, b the roof's own depth (B4(4)). In the US by ASCE 7-16 7.7.1 and 7.8 on top of the snow lying evenly: hb = ps / γ, hc the height clear above it, none
        /// where hc/hb is under 0.2; against a taller part the larger of the leeward drift, hd by (7.6-1) with lu the upper roof's length, and the windward, three quarters of it with the lower roof's;
        /// behind a parapet three quarters of hd with the roof's length, no less than 25 ft; w = 4 hd, or where hd is more than hc, hd = hc and w = 4 hd² / hc no more than 8 hc; pd = hd γ. Each falls
        /// straight from the wall to nothing at its length, cut off where the roof ends; all at once, as one arrangement.
        /// </summary>
        private void SnowAtSteps()
        {
            var project = _document.ProjectInformation;
            var sk = _us ? project.GroundSnowLoad * Loads.Psf : GroundSnow(project.SnowZone, project.SiteAltitude);
            if (sk <= 0) return;
            var drifts = new List<(Roof Roof, LoadRamp Ramp, double Peak, string Said)>();
            foreach (var roof in _document.All<Roof>())
            {
                if (roof.IsExtrusion || roof.Boundary.Count < 3 || _document.FindType<SlabType>(roof.TypeId) is not { } type) continue;
                var surface = roof.Surface(_document);
                var flat = surface.IsFlat;
                if (!flat && (roof.Edges.Count != roof.Boundary.Count || !_slopes.Any(slope => ReferenceEquals(slope.Roof, roof)))) continue;
                var outline = roof.Boundary;
                var turn = Polygon2D.SignedArea(outline) >= 0 ? 1 : -1;
                for (var i = 0; i < outline.Count; i++)
                {
                    // A pitched roof's eaves drift nothing against a wall; its other edges - a lean-to's top, a gable end - may, the roof's height taken where the edge is.
                    if (!flat && roof.Edges[i].DefinesSlope) continue;
                    var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                    var length = a.DistanceTo(b);
                    if (length < 300) continue;
                    var along = (b - a) / length;
                    var inward = along.PerpendicularLeft() * turn;
                    var top = flat ? roof.BaseElevation(_document) + type.Structure.TotalWidth : surface.HeightAt(a.MidpointTo(b) + inward * 100);

                    // The walls along this edge, no further off it than half their width and 300, whose tops are above the roof.
                    var rising = _document.Walls.Where(wall => !wall.IsCurved && Math.Abs(wall.Direction.Cross(along)) < 0.05 && wall.GetTopElevation(_document) > top + 150
                                                               && _document.GetWallType(wall) is { } wallType && Math.Abs((wall.Start - a).Dot(inward)) <= wallType.Structure.TotalWidth / 2 + 300)
                        .Select(wall => (Wall: wall, From: Math.Max(0, Math.Min((wall.Start - a).Dot(along), (wall.End - a).Dot(along))), To: Math.Min(length, Math.Max((wall.Start - a).Dot(along), (wall.End - a).Dot(along)))))
                        .Where(each => each.To - each.From >= 300).ToList();
                    if (rising.Count == 0) continue;
                    var (from, to) = (rising.Min(each => each.From), rising.Max(each => each.To));
                    if (!flat) top = surface.HeightAt(a + along * ((from + to) / 2) + inward * 100);
                    var h = (rising.Max(each => each.Wall.GetTopElevation(_document)) - top) / 1000;
                    if (h <= 0.15) continue;
                    var thick = rising.Max(each => _document.GetWallType(each.Wall)!.Structure.TotalWidth);
                    var middle = a + along * ((from + to) / 2);
                    var behind = middle - inward * (thick / 2 + 600);
                    var taller = _document.All<Slab>().Where(slab => slab is Roof or Floor && !ReferenceEquals(slab, roof) && slab.Boundary.Count >= 3 && Polygon2D.Contains(slab.Boundary, behind)
                                                                     && slab.GetBottomElevation(_document) >= top - 300).OrderByDescending(slab => slab.GetBottomElevation(_document)).FirstOrDefault();
                    var b2 = outline.Max(p => (p - middle).Dot(inward)) / 1000;
                    var b1 = taller is null ? b2 : taller.Boundary.Max(p => (p - middle).Dot(-inward)) / 1000;
                    var pitch = flat ? 0 : surface.FaceAt(middle + inward * 100)?.Plane.SlopeDegrees ?? 0;
                    var (peak, ls, said) = _us ? UsDrift(h, b1, b2, taller is null, pitch) : UkDrift(h, b1, b2, sk, pitch, taller is null);
                    if (peak <= 0 || ls <= 0) continue;
                    drifts.Add((roof, new LoadRamp(a, inward, along, ls * 1000, from, to), peak, said));
                }
            }

            if (drifts.Count == 0) return;
            _stepped = new LoadCase(_drifts ? "S4" : "S2", _us ? "Snow drifted at steps and parapets (ASCE 7-16 7.7, 7.8)" : "Snow drifted at steps and parapets, accidental (BS EN 1991-1-3 Annex B)",
                LoadNature.Snow, null, false, true, !_us);
            if (_us)
            {
                foreach (var load in _loading.SurfaceLoads.Where(load => load.Case == _snow).ToList()) _loading.SurfaceLoads.Add(load with { Case = _stepped });
                foreach (var load in _loading.LineLoads.Where(load => load.Case == _snow).ToList()) _loading.LineLoads.Add(load with { Case = _stepped });
            }

            foreach (var (roof, ramp, peak, said) in drifts)
            {
                if (_model.Panels.FirstOrDefault(panel => panel.Kind == AnalyticalPanelKind.Roof && ReferenceEquals(panel.Element, roof)) is { } plate)
                    _loading.SurfaceLoads.Add(new SurfaceLoad(_stepped, plate, Times(Down, peak), said, ramp));
                else if (_flats.Any(flat => ReferenceEquals(flat.Roof, roof)))
                    foreach (var (bearing, start, end, perMetre) in SpreadDrift(roof, roof.BaseElevation(_document), ramp, peak))
                        Line(_stepped, bearing, start, end, perMetre, said);
                else
                    foreach (var slope in _slopes.Where(slope => ReferenceEquals(slope.Roof, roof)))
                    foreach (var (bearing, start, end, perMetre) in SlopeDrift(slope.Stretches, -slope.Outward, slope.Overhang, slope.Face, ramp, peak))
                        Line(_stepped, bearing, start, end, perMetre, said);
            }
        }

        /// <summary>
        /// BS EN 1991-1-3 Annex B: the drift's greatest load, kN/m², and its length, m, against a taller part h m higher (B3, Table B1) or behind a parapet h m high (B4(4)). Table B1 by the pitch of the
        /// roof the drift lies on, its μ2, the larger of its two: a drift does not lie on a steep slope, none from 60°.
        /// </summary>
        private static (double Peak, double Length, string Said) UkDrift(double h, double b1, double b2, double sk, double pitch, bool parapet)
        {
            var ls = Math.Min(Math.Min(5 * h, parapet ? b2 : b1), 15);
            if (ls <= 0) return (0, 0, string.Empty);
            var b = parapet ? b2 : Math.Max(b1, b2);
            var most = Math.Min(Math.Min(2 * h / sk, 2 * b / ls), 8);
            var mu = parapet || pitch <= 30 ? most : pitch < 60 ? most * (60 - pitch) / 30 : 0;
            return (mu * sk, ls, parapet
                ? $"behind a parapet {h * 1000:0} high: μ {mu:0.00} x sk {sk:0.00} kN/m², to nothing at {ls:0.0} m (BS EN 1991-1-3 B4(4))"
                : $"against a part {h:0.00} m higher: μ {mu:0.00} x sk {sk:0.00} kN/m², to nothing at {ls:0.0} m, b1 {b1:0.0} m, b2 {b2:0.0} m{(pitch > 0 ? $", on a slope of {pitch:0}°" : "")} (BS EN 1991-1-3 B3, Table B1)");
        }

        /// <summary>ASCE 7-16 7.7.1 and 7.8: the drift's surcharge at the wall, kN/m², and its width, m, against a taller part h m higher or behind a parapet h m high; nothing where hc/hb is under 0.2.</summary>
        private (double Peak, double Length, string Said) UsDrift(double h, double b1, double b2, bool parapet, double pitch = 0)
        {
            var pg = _document.ProjectInformation.GroundSnowLoad;
            var gamma = Math.Min(0.13 * pg + 14, 30);
            var hb = RoofSnowPsf(_document, pitch) / gamma;
            var hc = h / 0.3048 - hb;
            if (hb <= 0 || hc / hb < 0.2) return (0, 0, string.Empty);
            double Height(double lu) => Math.Max(0, 0.43 * Math.Cbrt(lu) * Math.Pow(pg + 10, 0.25) - 1.5);
            var (upper, lower) = (b1 / 0.3048, b2 / 0.3048);
            var (hd, how) = parapet
                ? (0.75 * Height(Math.Max(lower, 25)), $"0.75 hd for lu {Math.Max(lower, 25):0} ft (7.8)")
                : Height(upper) >= 0.75 * Height(lower) ? (Height(upper), $"leeward, hd for lu {upper:0} ft (7.7.1)") : (0.75 * Height(lower), $"windward, 0.75 hd for lu {lower:0} ft (7.7.1)");
            var w = 4 * hd;
            if (hd > hc) (w, hd) = (Math.Min(4 * hd * hd / hc, 8 * hc), hc);
            if (hd <= 0) return (0, 0, string.Empty);
            return (hd * gamma * Loads.Psf, w * 0.3048, $"{(parapet ? "behind a parapet" : "against a part")} {h / 0.3048:0.0} ft high, hb {hb:0.00} ft, hc {hc:0.00} ft: {how}, hd {hd:0.00} ft, pd {hd * gamma:0} psf, w {w:0.0} ft (ASCE 7-16)");
        }

        /// <summary>
        /// A drift on a pitched roof's slope, onto the walls under its eave as the slope's other loads go: every 250 mm along the slope, its overhangs past the walls' ends too, the drift on its plan
        /// straight in from the eave to as far as it goes, given to the wall under that point or the nearest end of one; kN a metre of each wall, in runs where it hardly changes.
        /// </summary>
        private static List<(Bearing Bearing, double Start, double End, double PerMetre)> SlopeDrift(List<(Bearing Bearing, double Start, double End)> stretches, Vector2D inward, double overhang, RoofFacet face, LoadRamp ramp, double peak)
        {
            var pieces = new List<(Bearing, double, double, double)>();
            if (stretches.Count == 0) return pieces;
            const double step = 250;
            const int parts = 40;
            var origin = stretches[0].Bearing.A;
            var along = new Vector2D(-inward.Y, inward.X);
            var units = stretches.Select(each => (each.Bearing.B - each.Bearing.A) / Math.Max(each.Bearing.A.DistanceTo(each.Bearing.B), 1e-9)).ToList();
            var ranges = stretches.Select((each, i) =>
            {
                var (u0, u1) = ((each.Bearing.A + units[i] * each.Start - origin).Dot(along), (each.Bearing.A + units[i] * each.End - origin).Dot(along));
                return (Low: Math.Min(u0, u1), High: Math.Max(u0, u1));
            }).ToList();
            var bins = stretches.Select(each => new double[Math.Max(1, (int)Math.Ceiling((each.End - each.Start) / step))]).ToList();
            var (lowest, highest) = (face.Outline.Min(corner => (corner - origin).Dot(along)), face.Outline.Max(corner => (corner - origin).Dot(along)));
            for (var u = lowest + step / 2; u < highest; u += step)
            {
                // The wall this strip of slope comes down onto, or the nearest end of one.
                var nearest = Enumerable.Range(0, stretches.Count).MinBy(i => u < ranges[i].Low ? ranges[i].Low - u : u > ranges[i].High ? u - ranges[i].High : 0);
                var (bearing, first, last) = stretches[nearest];
                // Along its wall's line, past the wall's end where the slope runs on; and where on the wall it lands.
                var dot = units[nearest].Dot(along);
                double T(double value) => (value - (bearing.A - origin).Dot(along)) / dot;
                var line = bearing.A + units[nearest] * T(u);
                var reach = face.Outline.Max(corner => (corner - line).Dot(inward));
                var span = reach + overhang + 200;
                var value = 0.0;
                for (var k = 0; k < parts; k++)
                {
                    var point = line + inward * (-overhang - 200 + (k + 0.5) / parts * span);
                    if (Polygon2D.Contains(face.Outline, point)) value += peak * ramp.Share(point) * span / parts / 1000;
                }

                var at = T(Math.Clamp(u, ranges[nearest].Low, ranges[nearest].High)) - first;
                var bin = Math.Clamp((int)(at / step), 0, bins[nearest].Length - 1);
                bins[nearest][bin] += value;
            }

            for (var i = 0; i < stretches.Count; i++)
            {
                var (bearing, first, last) = stretches[i];
                var (runStart, runEnd, runSum, runCount) = (0.0, 0.0, 0.0, 0);
                for (var bin = 0; bin < bins[i].Length; bin++)
                {
                    var (start, end) = (first + bin * step, Math.Min(last, first + (bin + 1) * step));
                    var value = bins[i][bin] * step / Math.Max(end - start, 1);
                    if (value < 1e-6 || (runCount > 0 && Math.Abs(value - runSum / runCount) > 0.02 * Math.Max(value, runSum / runCount)))
                    {
                        if (runCount > 0 && runSum > 1e-9) pieces.Add((bearing, runStart, runEnd, runSum / runCount));
                        (runSum, runCount) = (0, 0);
                        if (value < 1e-6) continue;
                    }

                    if (runCount == 0) runStart = start;
                    runEnd = end;
                    runSum += value;
                    runCount++;
                }

                if (runCount > 0 && runSum > 1e-9) pieces.Add((bearing, runStart, runEnd, runSum / runCount));
            }

            return pieces;
        }

        /// <summary>
        /// A drift on a roof spanning one way onto its bearings, as Spread shares a floor's load: at each point along each bearing, each way it spans, a strip a metre wide to what holds it there or the
        /// roof's end, its reaction here by statics; kN a metre of each bearing, in runs where it hardly changes.
        /// </summary>
        private List<(Bearing Bearing, double Start, double End, double PerMetre)> SpreadDrift(Slab roof, double height, LoadRamp ramp, double peak)
        {
            var way = SpanOf(roof);
            var bearings = BearingsAt(height).Where(bearing => Math.Abs((bearing.B - bearing.A).NormalisedOrDefault(Vector2D.UnitX).Dot(way)) <= 0.7).ToList();
            var pieces = new List<(Bearing, double, double, double)>();
            const double step = 250;
            const int parts = 20;
            foreach (var bearing in bearings)
            {
                var length = bearing.A.DistanceTo(bearing.B);
                if (length < 1) continue;
                var unit = (bearing.B - bearing.A) / length;
                var (runStart, runEnd, runSum, runCount) = (0.0, 0.0, 0.0, 0);
                void Close()
                {
                    if (runCount > 0 && runSum > 1e-9) pieces.Add((bearing, runStart, runEnd, runSum / runCount));
                    (runSum, runCount) = (0, 0);
                }

                for (var at = step / 2; at < length; at += step)
                {
                    var point = bearing.A + unit * at;
                    var value = 0.0;
                    foreach (var side in new[] { way, -way })
                    {
                        var reach = Reach(roof, point, side, bearings, bearing);
                        for (var k = 0; k < parts && reach > 0; k++)
                        {
                            var s = (k + 0.5) / parts * reach;
                            value += peak * ramp.Share(point + side * s) * (reach - s) / reach * reach / parts / 1000;
                        }
                    }

                    if (value < 1e-6)
                    {
                        Close();
                        continue;
                    }

                    if (runCount > 0 && Math.Abs(value - runSum / runCount) > 0.02 * Math.Max(value, runSum / runCount)) Close();
                    if (runCount == 0) runStart = Math.Max(0, at - step / 2);
                    runEnd = Math.Min(length, at + step / 2);
                    runSum += value;
                    runCount++;
                }

                Close();
            }

            return pieces;
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

            // BS EN 1991-1-4 Table 7.4a's and 7.5's lesser values on a windward slope of 5° to 45°, or ASCE 7-16 Figure 27.3-1's from 10°, which draw it off where the greater press it: a case of their
            // own each way some slope faces, the rest as the wind's, taken where the weight helps the wind - where a roof is lifted.
            foreach (var wind in _wind)
                if (_slopes.Any(slope => slope.Outward.Dot(wind.Wind!.Value) < -0.7 && slope.Pitch >= (_us ? 10 : 5) && slope.Pitch <= 45))
                    _drawn.Add(new LoadCase($"W{_wind.Count + _drawn.Count + 1}", $"{wind.Description}, windward slopes drawn off", LoadNature.Wind, wind.Wind));
            foreach (var (wind, lesser) in _wind.Select(each => (each, false)).Concat(_drawn.Select(each => (each, true))).ToList())
            {
                var way = wind.Wind!.Value;
                var depth = Math.Max(1, (corners.Max(p => (p - default(Point2D)).Dot(way)) - corners.Min(p => (p - default(Point2D)).Dot(way))) / 1000);
                var breadth = Math.Max(1, (corners.Max(p => (p - default(Point2D)).Cross(way)) - corners.Min(p => (p - default(Point2D)).Cross(way))) / 1000);
                double into, outOf, velocity, underside;
                string said;
                if (_us)
                {
                    var q = Architecture.Wind.FromSite(_document) ? Architecture.Wind.VelocityPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height) : project.WindPressure;
                    var ratio = depth / breadth;
                    var leeward = ratio <= 1 ? 0.5 : ratio <= 2 ? 0.5 - 0.2 * (ratio - 1) : ratio <= 4 ? 0.3 - 0.05 * (ratio - 2) : 0.2;
                    (into, outOf, velocity, underside) = (q * 0.85 * 0.8, q * 0.85 * leeward, q * 0.85, 0.8);
                    said = $"qh {Units.Kilopascals(q, "0.00")} x G 0.85 x Cp 0.8 and -{leeward:0.00}, L/B {ratio:0.00} (ASCE 7-16 Figure 27.3-1)";
                }
                else
                {
                    var qp = Architecture.Wind.FromSite(_document) ? Architecture.Wind.PeakPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height) : project.WindPressure;
                    var ratio = height / depth;
                    double Between(double low, double one, double five) => ratio <= 0.25 ? low : ratio <= 1 ? low + (one - low) * (ratio - 0.25) / 0.75 : ratio < 5 ? one + (five - one) * (ratio - 1) / 4 : five;
                    var (cD, cE) = (Between(0.7, 0.8, 0.8), Between(0.3, 0.5, 0.7));
                    var correlation = ratio <= 1 ? 0.85 : ratio >= 5 ? 1 : 0.85 + 0.15 * (ratio - 1) / 4;
                    (into, outOf, velocity, underside) = (correlation * cD * qp, correlation * cE * qp, qp, cD);
                    said = $"qp {qp:0.00} kPa x {correlation:0.00} x cpe +{cD:0.00} (D) and -{cE:0.00} (E), h/d {ratio:0.00} (BS EN 1991-1-4 Table 7.1, 7.2.2(3))";
                }

                // The side walls: drawn off outward. BS EN 1991-1-4 Table 7.1's zones A, B and C, -1.2 for e/5 from the windward edge, -0.8 to e, -0.5 past it, e the lesser of b and 2h, each wall at the
                // average over its length; ASCE 7-16 Figure 27.3-1's -0.7.
                var windward = corners.Min(p => (p - default(Point2D)).Dot(way));
                var e = Math.Min(breadth, 2 * height) * 1000;
                foreach (var panel in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Wall && panel.Element is Wall wall && _document.GetWallType(wall) is { Function: WallFunction.Exterior }))
                {
                    var normal = Outward((Wall)panel.Element, outside);
                    var facing = normal.Dot(way);
                    if (Math.Abs(facing) <= 0.7)
                    {
                        var flat = panel.FlatOutline;
                        var ends = new[] { flat.Min(p => p.X), flat.Max(p => p.X) }.Select(x => (panel.Origin.X + panel.U.X * x) * way.X + (panel.Origin.Y + panel.U.Y * x) * way.Y - windward).ToArray();
                        var (from, to) = (Math.Max(0, ends.Min()), Math.Max(0, ends.Max()));
                        double Zone(double s) => s < e / 5 ? -1.2 : s < e ? -0.8 : -0.5;
                        double Averaged()
                        {
                            if (to - from < 1) return Zone(from);
                            var cuts = new[] { from, Math.Clamp(e / 5, from, to), Math.Clamp(e, from, to), to };
                            var sum = 0.0;
                            for (var i = 0; i + 1 < cuts.Length; i++) sum += (cuts[i + 1] - cuts[i]) * Zone((cuts[i] + cuts[i + 1]) / 2);
                            return sum / (to - from);
                        }

                        var (coefficient, side) = _us ? (-0.7, "side wall: qh x G 0.85 x Cp -0.7 (ASCE 7-16 Figure 27.3-1)") : (Averaged(), $"side wall: cpe {Averaged():0.00} over zones A, B and C, e {e / 1000:0.0} m (BS EN 1991-1-4 Table 7.1)");
                        var suction = -coefficient * velocity;
                        if (suction > 0) _loading.SurfaceLoads.Add(new SurfaceLoad(wind, panel, new Direction3D(normal.X * suction, normal.Y * suction, 0), side));
                        continue;
                    }

                    var pressure = facing < -0.7 ? into : facing > 0.7 ? outOf : 0;
                    if (pressure <= 0) continue;
                    _loading.SurfaceLoads.Add(new SurfaceLoad(wind, panel, new Direction3D(way.X * pressure, way.Y * pressure, 0), $"{(facing < 0 ? "into the wind" : "out of the wind")}: {said}"));
                }

                // Its roof: each slope by which way it faces, what presses on it across the slope, so sideways and down; each bearing on the wall under its eave, as its weight does.
                foreach (var (outward, pitch, perMetre, stretches, overhang, hipped, _, _) in _slopes)
                {
                    if (pitch < 5) continue;
                    var facing = outward.Dot(way);

                    // With the wind along its ridge, by how far along the wind each part of it is: BS EN 1991-1-4 Table 7.4b, F for e/4 in from its eave and G between, to e/10 from the windward
                    // end, H to e/2 and I past it; ASCE 7-16 Figure 27.3-1 parallel to the ridge as on a flat roof, -0.9 to h, -0.5 to 2h and -0.3 past it for h/L to 0.5, -1.3 to h/2 and -0.7
                    // past it for 1 or more. Each stretch cut where its zone changes.
                    if (Math.Abs(facing) <= 0.7)
                    {
                        var eMetres = e / 1000;
                        var nearEnd = (Interpolated(pitch, new[] { (5.0, -1.6), (15.0, -1.3), (30.0, -1.1), (45.0, -1.1), (60.0, -1.1), (75.0, -1.1) }) * Math.Min(eMetres / 4, perMetre)
                                       + Interpolated(pitch, new[] { (5.0, -1.3), (15.0, -1.3), (30.0, -1.4), (45.0, -1.4), (60.0, -1.2), (75.0, -1.2) }) * Math.Max(0, perMetre - eMetres / 4)) / Math.Max(perMetre, 1e-9);
                        var (zoneH, zoneI) = (Interpolated(pitch, new[] { (5.0, -0.7), (15.0, -0.6), (30.0, -0.8), (45.0, -0.9), (60.0, -0.8), (75.0, -0.8) }), Interpolated(pitch, new[] { (5.0, -0.6), (15.0, -0.5), (30.0, -0.5) }));
                        var slender = height / depth;
                        double AlongIt(double s)
                        {
                            if (!_us) return s < eMetres / 10 ? nearEnd : s < eMetres / 2 ? zoneH : zoneI;
                            var (low, high) = (s < height ? -0.9 : s < 2 * height ? -0.5 : -0.3, s < height / 2 ? -1.3 : -0.7);
                            return slender <= 0.5 ? low : slender >= 1 ? high : low + (high - low) * (slender - 0.5) / 0.5;
                        }

                        var changes = _us ? new[] { height / 2, height, 2 * height } : new[] { eMetres / 10, eMetres / 2 };
                        foreach (var (bearing, start, end) in stretches)
                        {
                            var length = bearing.A.DistanceTo(bearing.B);
                            var unit = (bearing.B - bearing.A) / Math.Max(length, 1e-9);
                            var (at0, rate) = ((bearing.A - default(Point2D)).Dot(way) - windward, unit.Dot(way));
                            var cuts = new List<double> { start, end };
                            if (Math.Abs(rate) > 1e-9)
                                cuts.AddRange(changes.Select(change => (change * 1000 - at0) / rate).Where(t => t > start && t < end));
                            cuts.Sort();
                            for (var i = 0; i + 1 < cuts.Count; i++)
                            {
                                if (cuts[i + 1] - cuts[i] < 1) continue;
                                var c = AlongIt(Math.Max(0, at0 + rate * (cuts[i] + cuts[i + 1]) / 2) / 1000);
                                var (pressing, sideways) = (c * velocity * perMetre, c * velocity * perMetre * Math.Tan(pitch * Math.PI / 180));
                                _loading.LineLoads.Add(new LineLoad(wind, bearing.Member, bearing.Panel, bearing.From, bearing.To, cuts[i], cuts[i + 1], new Direction3D(-outward.X * sideways, -outward.Y * sideways, -pressing),
                                    $"slope along the wind at {pitch:0}°, c {c:0.00} {(_us ? "(ASCE 7-16 Figure 27.3-1)" : "(BS EN 1991-1-4 Table 7.4b)")}"));
                            }
                        }

                        continue;
                    }

                    var drawnOff = lesser && facing < -0.7 && pitch >= (_us ? 10 : 5) && pitch <= 45;
                    var (coefficient, zone) = facing < -0.7 ? (drawnOff ? DrawnOff(pitch) : RoofPressure(pitch, true), drawnOff ? "windward slope, drawn off," : "windward slope") : (RoofPressure(pitch, false, hipped), "leeward slope");
                    var edgeSaid = string.Empty;

                    // In the UK the strip e/10 deep along its eave windward, F for e/4 in from each end and G between, and along its ridge leeward, J, at their own coefficients, the slope's averaged over
                    // its depth; a hipped roof's by Table 7.5.
                    if (!_us)
                    {
                        var strip = Math.Min(e / 10 / 1000, perMetre) / Math.Max(perMetre, 1e-9);
                        double edge;
                        if (facing < 0)
                        {
                            var eave = stretches.Sum(each => each.End - each.Start) / 1000;
                            var atEnds = Math.Min(e / 2 / 1000, eave) / Math.Max(eave, 1e-9);
                            var (f, g) = drawnOff
                                ? (Interpolated(pitch, new[] { (5.0, -1.7), (15.0, -0.9), (30.0, -0.5), (45.0, 0.0) }), Interpolated(pitch, new[] { (5.0, -1.2), (15.0, -0.8), (30.0, -0.5), (45.0, 0.0) }))
                                : (Interpolated(pitch, hipped ? new[] { (5.0, 0.0), (15.0, 0.2), (30.0, 0.5), (45.0, 0.7), (60.0, 0.7), (75.0, 0.8) } : new[] { (5.0, 0.0), (15.0, 0.2), (30.0, 0.7), (45.0, 0.7), (60.0, 0.7), (75.0, 0.8) }),
                                    Interpolated(pitch, new[] { (5.0, 0.0), (15.0, 0.2), (30.0, 0.7), (45.0, 0.7), (60.0, 0.7), (75.0, 0.8) }));
                            edge = f * atEnds + g * (1 - atEnds);
                        }
                        else
                            edge = Interpolated(pitch, hipped ? new[] { (5.0, -0.6), (15.0, -1.0), (30.0, -0.7), (45.0, -0.6), (60.0, -0.6), (75.0, -0.6) } : new[] { (5.0, -0.6), (15.0, -1.0), (30.0, -0.5), (45.0, -0.3), (60.0, -0.3), (75.0, -0.3) });
                        coefficient = edge * strip + coefficient * (1 - strip);
                        edgeSaid = facing < 0 ? $" with F and G along its eave, {strip * 100:0}% of its depth" : $" with J along its ridge, {strip * 100:0}% of its depth";
                    }

                    // Its overhang windward lifted from under by the pressure on the wall below it, BS EN 1991-1-4 7.2.1(2)'s zone D, or ASCE 7-16 27.3.3's Cp 0.8.
                    if (facing < -0.7 && overhang > 0)
                    {
                        var lift = underside * velocity * overhang / 1000;
                        foreach (var (bearing, start, end) in stretches)
                            if (end - start >= 1)
                                _loading.LineLoads.Add(new LineLoad(wind, bearing.Member, bearing.Panel, bearing.From, bearing.To, start, end, new Direction3D(0, 0, lift), $"its overhang of {overhang:0} lifted from under, c +{underside:0.00}"));
                    }
                    var (down, across) = (coefficient * velocity * perMetre, coefficient * velocity * perMetre * Math.Tan(pitch * Math.PI / 180));
                    var force = new Direction3D(-outward.X * across, -outward.Y * across, -down);
                    foreach (var (bearing, start, end) in stretches)
                        if (end - start >= 1)
                            _loading.LineLoads.Add(new LineLoad(wind, bearing.Member, bearing.Panel, bearing.From, bearing.To, start, end, force, $"{zone} at {pitch:0}°{edgeSaid}{(hipped ? ", a hipped roof's (Table 7.5)" : "")}, c {coefficient:0.00}"));
                }

                // A flat roof drawn off: BS EN 1991-1-4 Table 7.2's sharp eaves, F -1.8 over e/4 at each end of the strip e/10 deep at its windward edge and G -1.2 between, H -0.7 to e/2, I -0.2 past it;
                // ASCE 7-16 Figure 27.3-1, G already in the velocity: for h/L to 0.5 -0.9 to h, -0.5 to 2h and -0.3 past it, for h/L of 1 or more -1.3 to h/2 and -0.7 past it, between them straight -
                // each averaged over the roof's depth along the wind. On a plate as on joists.
                double Lifted(IEnumerable<Point2D> outline)
                {
                    var points = outline.ToList();
                    var (along, across) = (points.Select(p => (p - default(Point2D)).Dot(way)).ToList(), points.Select(p => (p - default(Point2D)).Cross(way)).ToList());
                    var (deep, wide) = ((along.Max() - along.Min()) / 1000, (across.Max() - across.Min()) / 1000);
                    if (deep <= 0 || wide <= 0) return 0;
                    double Average((double Upto, double Value)[] zones)
                    {
                        var (sum, at) = (0.0, 0.0);
                        foreach (var (upto, value) in zones)
                        {
                            var next = Math.Min(upto, deep);
                            if (next > at) sum += (next - at) * value;
                            at = Math.Max(at, next);
                        }

                        return sum / deep;
                    }

                    var eMetres = e / 1000;
                    if (!_us)
                    {
                        var atCorners = Math.Min(eMetres / 2, wide);
                        var strip = (-1.8 * atCorners - 1.2 * (wide - atCorners)) / wide;
                        return Average(new[] { (eMetres / 10, strip), (eMetres / 2, -0.7), (double.MaxValue, -0.2) });
                    }

                    var (low, high) = (Average(new[] { (height, -0.9), (2 * height, -0.5), (double.MaxValue, -0.3) }), Average(new[] { (height / 2, -1.3), (double.MaxValue, -0.7) }));
                    var slender = height / deep;
                    return slender <= 0.5 ? low : slender >= 1 ? high : low + (high - low) * (slender - 0.5) / 0.5;
                }

                foreach (var (roof, spread) in _flats)
                {
                    var lifted = Lifted(roof.Boundary);
                    if (lifted >= 0) continue;
                    foreach (var (bearing, start, end, width) in spread)
                        if (end - start >= 1)
                            _loading.LineLoads.Add(new LineLoad(wind, bearing.Member, bearing.Panel, bearing.From, bearing.To, start, end, new Direction3D(0, 0, -lifted * velocity * width / 1000), $"flat roof drawn off, c {lifted:0.00} over its depth"));
                }

                foreach (var panel in _model.Panels.Where(panel => panel.Kind == AnalyticalPanelKind.Roof && panel.Element is Roof))
                {
                    var lifted = Lifted(((Roof)panel.Element).Boundary);
                    if (lifted < 0) _loading.SurfaceLoads.Add(new SurfaceLoad(wind, panel, new Direction3D(0, 0, -lifted * velocity), $"flat roof drawn off, c {lifted:0.00} over its depth"));
                }
            }
        }

        private readonly Dictionary<Wall, Vector2D> _outward = new();

        /// <summary>
        /// The side of an outside wall the weather is on. Its exterior side as drawn, unless a line from its middle that way crosses the other outside walls of its level an odd number of times,
        /// and so goes into the building: a wall drawn anticlockwise round a building and never turned has its exterior side in.
        /// </summary>
        private Vector2D Outward(Wall wall, IReadOnlyList<Wall> outside)
        {
            if (_outward.TryGetValue(wall, out var known)) return known;
            var normal = wall.ExteriorNormal;
            var from = wall.Start.MidpointTo(wall.End) + normal * 50;
            var crossings = outside.Count(other =>
            {
                if (ReferenceEquals(other, wall) || other.LevelId != wall.LevelId) return false;
                var run = other.End - other.Start;
                var square = normal.Cross(run);
                if (Math.Abs(square) < 1e-9) return false;
                var toStart = other.Start - from;
                var (ahead, along) = (toStart.Cross(run) / square, toStart.Cross(normal) / square);
                return ahead > 0 && along >= 0 && along < 1;
            });
            var outward = crossings % 2 == 1 ? -normal : normal;
            _outward[wall] = outward;
            return outward;
        }

        /// <summary>A figure read off a table by pitch, straight between its rows and held at its first and last.</summary>
        private static double Interpolated(double pitch, (double Pitch, double Value)[] table)
        {
            if (pitch <= table[0].Pitch) return table[0].Value;
            for (var i = 0; i + 1 < table.Length; i++)
                if (pitch <= table[i + 1].Pitch)
                    return table[i].Value + (table[i + 1].Value - table[i].Value) * (pitch - table[i].Pitch) / (table[i + 1].Pitch - table[i].Pitch);
            return table[^1].Value;
        }

        /// <summary>
        /// A slope's pressure coefficient by its pitch, with the wind across its ridge, pressing in as a positive: its windward or leeward face taken as a whole by zones H and I of BS EN 1991-1-4 Table 7.4a, windward its pressing
        /// values, which sway a building most; or ASCE 7-16 Figure 27.3-1 for h/L to 0.25, as remembered, with G already in what it is multiplied by.
        /// </summary>

        private double RoofPressure(double pitch, bool windward, bool hipped = false)
        {
            (double Pitch, double Value)[] table = _us
                ? windward ? new[] { (15.0, 0.0), (20.0, 0.2), (25.0, 0.3), (30.0, 0.3), (35.0, 0.4), (45.0, 0.4), (60.0, 0.6) } : new[] { (10.0, -0.3), (15.0, -0.5), (20.0, -0.6), (60.0, -0.6) }
                : windward ? new[] { (5.0, 0.0), (15.0, 0.2), (30.0, 0.4), (45.0, 0.6), (60.0, 0.7), (75.0, 0.8) }
                : hipped ? new[] { (5.0, -0.3), (15.0, -0.5), (30.0, -0.4), (45.0, -0.3), (60.0, -0.3), (75.0, -0.3) } : new[] { (5.0, -0.6), (15.0, -0.4), (30.0, -0.4), (45.0, -0.2), (60.0, -0.2), (75.0, -0.2) };
            return Interpolated(pitch, table);
        }

        /// <summary>
        /// A windward slope's lesser coefficient, drawing it off: zone H of BS EN 1991-1-4 Table 7.4a, the same in Table 7.5, -0.6 at 5° to nothing at 45°; or ASCE 7-16 Figure 27.3-1's for h/L to 0.25,
        /// -0.7 at 10° to nothing at 35°, as remembered.
        /// </summary>
        private double DrawnOff(double pitch) => Interpolated(pitch, _us
            ? new[] { (10.0, -0.7), (15.0, -0.5), (20.0, -0.3), (25.0, -0.2), (30.0, -0.2), (35.0, 0.0), (45.0, 0.0) }
            : new[] { (5.0, -0.6), (15.0, -0.3), (30.0, -0.2), (45.0, 0.0) });

        // ---- combinations ---------------------------------------------------------------

        private bool Has(LoadCase loadCase) => _loading.Cases.Contains(loadCase);

        private void Add(string description, LimitState state, IEnumerable<(LoadCase Case, double Factor)> terms)
        {
            var list = terms.Where(term => Has(term.Case) && (term.Factor > 0 || (term.Case.Torsion && term.Factor != 0))).ToList();
            if (list.Count == 0) return;
            if (_loading.Combinations.Any(each => each.State == state && each.Terms.Count == list.Count && each.Terms.Zip(list).All(pair => pair.First.Case == pair.Second.Case && Math.Abs(pair.First.Factor - pair.Second.Factor) < 1e-9))) return;
            var prefix = state == LimitState.Ultimate ? (_us && description.Contains("2.4.", StringComparison.Ordinal) ? "ASD" : "ULS") : "SLS";
            var number = _loading.Combinations.Count(each => each.Name.StartsWith(prefix, StringComparison.Ordinal)) + 1;
            _loading.Combinations.Add(new LoadCombination($"{prefix}{number}", description, state, list));
        }

        private IEnumerable<(LoadCase, double)> Permanent(double factor) => new[] { (_self, factor), (_dead, factor) };

        /// <summary>BS EN 1990 with the UK National Annex: 6.10a and 6.10b with each variable action leading, weight helping the wind, then 6.14b and 6.16b.</summary>
        private void UkCombinations()
        {
            var snows = new[] { _snow }.Concat(_drifted).ToList();
            var sets = new List<List<LoadCase>> { new() { _imposed, _roof } };
            foreach (var snow in snows) sets.Add(new List<LoadCase> { _imposed, snow });
            foreach (var wind in _wind)
            foreach (var snow in snows)
                sets.Add(new List<LoadCase> { _imposed, snow, wind });
            sets = sets.Select(set => set.Where(Has).ToList()).Where(set => set.Count > 0).ToList();
            double Psi0(LoadCase each) => Psi(_document, each.Nature).Combination;

            foreach (var set in sets)
                Add("BS EN 1990 6.10a", LimitState.Ultimate, Permanent(1.35).Concat(set.Select(each => (each, 1.5 * Psi0(each)))));
            foreach (var set in sets)
            foreach (var leading in set)
                Add($"BS EN 1990 6.10b, {leading.Name} leading", LimitState.Ultimate, Permanent(1.25).Append((leading, 1.5)).Concat(set.Where(each => each != leading).Select(each => (each, 1.5 * Psi0(each)))));
            foreach (var wind in _wind.Concat(_drawn))
                Add($"BS EN 1990 6.10b, {wind.Name} leading, the weight helping", LimitState.Ultimate, Permanent(1.0).Append((wind, 1.5)));

            foreach (var set in sets)
            foreach (var leading in set)
                Add($"BS EN 1990 6.14b characteristic, {leading.Name} leading", LimitState.Serviceability, Permanent(1.0).Append((leading, 1.0)).Concat(set.Where(each => each != leading).Select(each => (each, Psi0(each)))));
            Add("BS EN 1990 6.16b quasi-permanent", LimitState.Serviceability, Permanent(1.0).Concat(new[] { _imposed, _roof, _snow }.Concat(_wind).Select(each => (each, Psi(_document, each.Nature).QuasiPermanent))));

            // The exceptional drift by 6.11b, the accidental design situation: the weight and the drift whole, the floors' imposed load at ψ1 with it, or a wind at its ψ1 and the floors at ψ2.
            if (_stepped is { Accidental: true } accident && Has(accident))
            {
                Add("BS EN 1990 6.11b, the exceptional drift of Annex B", LimitState.Ultimate, Permanent(1.0).Append((accident, 1.0)).Append((_imposed, Psi(_document, LoadNature.Imposed).Frequent)));
                foreach (var wind in _wind)
                    Add($"BS EN 1990 6.11b, the exceptional drift of Annex B, {wind.Name} with it", LimitState.Ultimate,
                        Permanent(1.0).Append((accident, 1.0)).Append((wind, Psi(_document, LoadNature.Wind).Frequent)).Append((_imposed, Psi(_document, LoadNature.Imposed).QuasiPermanent)));
            }
        }

        /// <summary>ASCE 7-16: 2.3.1 for strength, 2.4.1 for allowable stress.</summary>
        private void UsCombinations()
        {
            var roofs = new[] { _roof, _snow }.Concat(_drifted).Concat(_stepped is null ? Array.Empty<LoadCase>() : new[] { _stepped }).Where(Has).ToList();
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
            foreach (var wind in _wind.Concat(_drawn))
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
            foreach (var wind in _wind.Concat(_drawn))
                Add("ASCE 7-16 2.4.1 (7)", LimitState.Ultimate, Permanent(0.6).Append((wind, 0.6)));

            // The earthquake, ρ QE with Ev as 0.2 SDS D on the dead load (12.4.2.2), none in category B, as remembered; L at 0.5 where it is 100 psf or less (2.3.6, exception 1).
            if (_loading.Seismic is not { Applies: true } quake) return;
            var (rho, ev) = (quake.Redundancy, quake.Category == "B" ? 0 : 0.2 * quake.Sds);
            var snow = Has(_snow);
            foreach (var earthquake in _seismic)
            {
                // With its accidental torsion one way and the other, where there is one: the mass off its place by +5% and by -5%.
                var torsion = _torsion.FirstOrDefault(each => Math.Abs(each.Wind!.Value.Dot(earthquake.Wind!.Value)) > 0.5);
                foreach (var sign in torsion is null ? new[] { 0.0 } : new[] { 1.0, -1.0 })
                {
                    IEnumerable<(LoadCase, double)> Shaken(IEnumerable<(LoadCase, double)> terms, double factor) =>
                        torsion is null ? terms.Append((earthquake, factor)) : terms.Append((earthquake, factor)).Append((torsion, sign * factor));
                    var six = Shaken(Permanent(1.2 + ev), rho).Append((_imposed, 0.5));
                    Add("ASCE 7-16 2.3.6 (6)", LimitState.Ultimate, snow ? six.Append((_snow, 0.2)) : six);
                    Add("ASCE 7-16 2.3.6 (7)", LimitState.Ultimate, Shaken(Permanent(0.9 - ev), rho));
                    Add("ASCE 7-16 2.4.5 (8)", LimitState.Ultimate, Shaken(Permanent(1.0 + 0.7 * ev), 0.7 * rho));
                    var nine = Shaken(Permanent(1.0 + 0.525 * ev), 0.525 * rho).Append((_imposed, 0.75));
                    Add("ASCE 7-16 2.4.5 (9)", LimitState.Ultimate, snow ? nine.Append((_snow, 0.75)) : nine);
                    Add("ASCE 7-16 2.4.5 (10)", LimitState.Ultimate, Shaken(Permanent(0.6 - 0.7 * ev), 0.7 * rho));
                }
            }
        }

        private void Notes()
        {
            var project = _document.ProjectInformation;
            if (_us)
            {
                _loading.Notes.Add($"Snow: pg {project.GroundSnowLoad:0} psf, Ce {(project.Terrain == WindTerrain.Sea ? 0.9 : 1.0):0.0}, Ct 1.0, Is 1.0 - S1 the snow lying evenly{(_drifts ? "; a gable roof from 1/2 on 12 to 7 on 12 unbalanced by 7.6.1 one way and the other as S2 and S3" : "; no roof here unbalanced by 7.6.1, none being a gable from 1/2 on 12 to 7 on 12")}{(_stepped is null ? "; no flat roof here has a taller part or a parapet to drift against (7.7, 7.8)" : $"; drifted against taller parts and parapets by 7.7.1 and 7.8 on top of it as {_stepped.Name}")}; sliding snow (7.9) left out.");
                const string usWhere = "into the bearing outside walls and out of them, the side walls drawn off at Cp -0.7, each roof's slopes by Figure 27.3-1, a windward slope of 10° to 45° also drawn off by its lesser values as a case of its own in 2.3.1 (5) and 2.4.1 (7), along a ridge or over a flat roof by how far along the wind, a windward overhang lifted at Cp 0.8; internal pressure and the walls not bearing left out.";
                _loading.Notes.Add(Architecture.Wind.FromSite(_document)
                    ? $"Wind: {project.BasicWindSpeed:0} mph, exposure {(project.Terrain switch { WindTerrain.Sea => "D", WindTerrain.OpenCountry => "C", _ => "B" })}, {usWhere}"
                    : $"Wind: no basic wind speed set, the {Units.Kilopascals(project.WindPressure, "0.00")} set taken as qh, {usWhere}");
                _loading.Notes.Add(project.ReduceImposedLoads
                    ? "Live loads on beams and columns reduced for the area they carry by 4.7, as Imposed Loads Reduced by Area in the plan's Properties asks; walls and footings take them whole."
                    : "Live loads are not reduced for the area carried (4.7): Imposed Loads Reduced by Area in the plan's Properties reduces them on beams and columns.");
                _loading.Notes.Add(_loading.Seismic is { } quake
                    ? $"Seismic: Ss {quake.Ss:0.00}, S1 {quake.S1:0.00}, Site Class {quake.Site}, Risk Category {quake.Risk}: {quake.Said}{(quake.Applies ? $" SDS {quake.Sds:0.000}, SD1 {quake.Sd1:0.000}; {quake.System.Name}, R {quake.System.R:0.#}, Cd {quake.System.Cd:0.#}; T {quake.Period:0.000} s, Cs {quake.Coefficient:0.0000} by {quake.CoefficientSaid}, ρ {quake.Redundancy:0.0}. The effective seismic weight its dead load; accidental torsion, snow and storage in it, and vertical irregularities, not worked out." : string.Empty)}"
                    : "Seismic: no Ss and S1 set in the plan's Properties, so none is worked out.");
            }
            else
            {
                _loading.Notes.Add($"Snow: zone {project.SnowZone:0.#} at {project.SiteAltitude:0} m{(project.SiteAltitude < 100 ? ", taken as the map's 100 m" : "")}, sk {GroundSnow(project.SnowZone, project.SiteAltitude):0.00} kN/m² - S1 the snow lying evenly{(_drifts ? "; a pitched roof's drifted by 5.3.3 as S2 and S3, half on the slope the wind comes over one way and the other" : "")}{(_stepped is null ? "; no flat roof here has a taller part or a parapet to drift against" : $"; drifted against taller parts and parapets by Annex B, as the UK National Annex has it in place of 5.3.6 and 6.2, as {_stepped.Name}: exceptional, an accidental action with no snow elsewhere, combined by BS EN 1990 6.11b; all at once, where the Annex has them as alternatives")}; drifts in valleys and sliding snow left out.");
                const string ukWhere = "into the bearing outside walls and out of them by Table 7.1's zones D and E, the side walls drawn off by its zones A to C, each roof's slopes by Table 7.4a with F and G along a windward eave and J along a leeward ridge, a hipped roof's by Table 7.5, a windward slope of 5° to 45° also drawn off by their lesser values as a case of its own where the weight helps the wind, along a ridge by Table 7.4b, a flat roof by Table 7.2, a windward overhang lifted at zone D's pressure (7.2.1(2)); internal pressure and the walls not bearing left out.";
                _loading.Notes.Add(Architecture.Wind.FromSite(_document)
                    ? $"Wind: {project.BasicWindSpeed:0.0} m/s at {project.SiteAltitude:0} m, {ukWhere}"
                    : $"Wind: no basic wind speed set, the {project.WindPressure:0.00} kPa set taken as qp, {ukWhere}");
                _loading.Notes.Add((project.ReduceImposedLoads
                    ? "Imposed loads on beams and columns reduced by 6.3.1.2(10) and (11) with the UK National Annex, as Imposed Loads Reduced by Area in the plan's Properties asks; walls and footings take them whole"
                    : "Imposed loads are not reduced for the area carried (6.3.1.2(10)): Imposed Loads Reduced by Area in the plan's Properties reduces them on beams and columns") + "; accidental actions are not combined.");
            }
        }
    }
}
