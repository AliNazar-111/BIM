using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Mep;

/// <summary>Where a US house's heating and cooling ducts run: inside what is heated and cooled, losing nothing, or through an attic, a basement or a crawlspace.</summary>
public enum DuctRun
{
    ConditionedSpace,
    Attic,
    Basement,
    Crawlspace
}

/// <summary>
/// A US house's air for its loads, as ASHRAE's residential load factor method takes it (Handbook -
/// Fundamentals 2021, chapter 17): the envelope's effective leakage area at 0.016 in. of water - from
/// its air leakage at 50 Pa, as a blower door measures it, the flow taken to fall as the pressure to
/// the 0.65 power, through an orifice of discharge coefficient 1 - driven by the stack and the wind,
/// IDF = (I0 + I1 H Δt) / 1000 cfm a square inch (eq. 10, which gives Table 5: heating 698 and 0.81
/// at 15 mph, cooling 343 and 0.88 at 7.5); and the ventilation's balanced and unbalanced flows put
/// with it (eqs. 12 to 14), Q_vi = max(Q_unbal, Q_i + Q_unbal / 2), what a heat recovery unit
/// recovers taken off its balanced flow (eq. 15). The air carries 1.1 Btu/h a cfm a °F at sea level,
/// and 4840 Btu/h a cfm for each pound of water a pound of air, less in thinner air up high.
/// </summary>
public static class ResidentialAir
{
    /// <summary>A cfm in m³/h.</summary>
    public const double CubicMetresPerHourPerCfm = 1.699011;

    /// <summary>The air's sensible heat factor at sea level, W a m³/h a kelvin: ASHRAE's 1.1 Btu/h·cfm·°F.</summary>
    public const double Sensible = 1.1 / Units.BtuPerHourPerWatt / CubicMetresPerHourPerCfm * 1.8;

    /// <summary>The air's latent heat factor at sea level, W a m³/h for each kg of water a kg of air: ASHRAE's 4840 Btu/h·cfm.</summary>
    public const double Latent = 4840 / Units.BtuPerHourPerWatt / CubicMetresPerHourPerCfm;

    /// <summary>The effective leakage area at 4 Pa of an envelope leaking so many cfm at 50 Pa, square inches.</summary>
    public static double LeakageArea(double cfm50) =>
        cfm50 * Math.Pow(4.0 / 50, 0.65) * Units.LitresPerSecondPerCfm / 1000 / Math.Sqrt(2 * 4 / 1.2014) * 1550.0031;

    /// <summary>The infiltration driving force, cfm a square inch of leakage, for a stack so high (ft) and a difference to outside so great (°F): eq. 10, flues left out.</summary>
    public static double DrivingForce(double stackFeet, double differenceF, bool cooling) =>
        cooling ? (343 + 0.88 * stackFeet * differenceF) / 1000 : (698 + 0.81 * stackFeet * differenceF) / 1000;

    /// <summary>How much less heat a cfm carries up high than at sea level: the air's pressure there against the sea's.</summary>
    public static double AltitudeFactor(BimDocument document) => Psychrometrics.Pressure(document.ProjectInformation.SiteAltitude) / 101.325;

    /// <summary>
    /// A house's air at the design conditions, m³/h: the infiltration on its own, the infiltration and
    /// the unbalanced ventilation together, the balanced flow through a heat recovery unit and its
    /// efficiency, and any other balanced flow.
    /// </summary>
    public sealed record Flow(double Infiltration, double Combined, double Recovered, double Efficiency, double OtherBalanced, double LeakageArea, double Stack)
    {
        /// <summary>The flow that carries the sensible heat, m³/h: what a heat recovery unit recovers left out.</summary>
        public double SensibleFlow => Combined + (1 - Efficiency) * Recovered + OtherBalanced;

        /// <summary>The flow that carries moisture, m³/h: a heat recovery unit recovers none.</summary>
        public double MoistFlow => Combined + Recovered + OtherBalanced;
    }

    /// <summary>
    /// The air a house takes in at a difference to outside of so many kelvins: its envelope leaking as
    /// its air permeability says, through the surfaces round what is heated and cooled - walls,
    /// windows, roofs, the ceiling under an attic and a floor over a crawlspace, not a slab (Table 4) -
    /// and its ventilation's flows.
    /// </summary>
    public static Flow Of(BimDocument document, IEnumerable<HeatLossLine> envelope, double stackFeet, double difference, bool cooling,
        (double Efficiency, IReadOnlyDictionary<Room, (double Supply, double Extract)> Rooms)? ventilation, Func<Room, bool> conditioned)
    {
        var exposed = envelope.Where(line => line.Path == HeatPath.Outside && line.Kind != HeatSurface.SlabEdge || line.Kind == HeatSurface.SuspendedFloor).Sum(line => line.Area);
        var cfm50 = document.ProjectInformation.AirPermeability * exposed / CubicMetresPerHourPerCfm;
        var area = LeakageArea(cfm50);
        var infiltration = area * DrivingForce(stackFeet, Math.Abs(difference) * 1.8, cooling) * CubicMetresPerHourPerCfm;

        var supply = ventilation?.Rooms.Where(entry => conditioned(entry.Key)).Sum(entry => entry.Value.Supply) ?? 0;
        var extract = ventilation?.Rooms.Where(entry => conditioned(entry.Key)).Sum(entry => entry.Value.Extract) ?? 0;
        var balanced = Math.Min(supply, extract);
        var unbalanced = Math.Max(supply, extract) - balanced;
        var combined = Math.Max(unbalanced, infiltration + unbalanced / 2);
        var efficiency = ventilation?.Efficiency ?? 0;
        return efficiency > 0
            ? new Flow(infiltration, combined, balanced, efficiency, 0, area, stackFeet)
            : new Flow(infiltration, combined, 0, 0, balanced, area, stackFeet);
    }

    /// <summary>The height of the stack, ft: from the lowest floor of what is heated and cooled to its highest ceiling - 8 ft at the least.</summary>
    public static double StackFeet(BimDocument document, IEnumerable<Room> rooms)
    {
        var spans = rooms.Select(room =>
        {
            var floor = RoomHeadroom.Floor(document, room);
            return (Bottom: floor, Top: floor + (RoomHeadroom.Measure(document, room)?.Highest ?? room.Height));
        }).ToList();
        return spans.Count == 0 ? 8 : Math.Max(8, (spans.Max(span => span.Top) - spans.Min(span => span.Bottom)) / Units.MillimetresPerFoot);
    }

    /// <summary>
    /// ASHRAE's Table 6 duct loss and gain factor: of the house's own load, how much more its ducts
    /// lose or gain where they run outside what is conditioned - by where, the house's storeys, their
    /// leakage (5% or 11% of the fan's flow, supply and return), their insulation (R-0, R-4, R-8) and
    /// whether for cooling, a furnace's heating or a heat pump's.
    /// </summary>
    public static double DuctFactor(DuctRun run, bool cooling, bool heatPump, int storeys, bool sealed_, double insulation)
    {
        if (run == DuctRun.ConditionedSpace) return 0;
        var column = insulation >= 8 - 0.01 ? 2 : insulation >= 4 - 0.01 ? 1 : 0;
        var group = (storeys >= 2 ? 2 : 0) + (sealed_ ? 1 : 0);
        double[][] Rows(params double[][] rows) => rows;
        // Each: 1 storey 11%, 1 storey 5%, 2 or more 11%, 2 or more 5%; each R-0, R-4, R-8.
        var (coolingRows, furnace, pump) = run switch
        {
            DuctRun.Basement => (
                Rows(new[] { 0.12, 0.09, 0.09 }, new[] { 0.07, 0.05, 0.04 }, new[] { 0.11, 0.09, 0.09 }, new[] { 0.06, 0.04, 0.04 }),
                Rows(new[] { 0.28, 0.18, 0.16 }, new[] { 0.19, 0.10, 0.08 }, new[] { 0.24, 0.17, 0.15 }, new[] { 0.16, 0.09, 0.08 }),
                Rows(new[] { 0.23, 0.17, 0.16 }, new[] { 0.14, 0.09, 0.08 }, new[] { 0.20, 0.16, 0.15 }, new[] { 0.12, 0.08, 0.07 })),
            DuctRun.Crawlspace => (
                Rows(new[] { 0.16, 0.12, 0.11 }, new[] { 0.10, 0.06, 0.05 }, new[] { 0.14, 0.12, 0.11 }, new[] { 0.08, 0.06, 0.05 }),
                Rows(new[] { 0.49, 0.29, 0.25 }, new[] { 0.34, 0.16, 0.13 }, new[] { 0.41, 0.26, 0.24 }, new[] { 0.27, 0.14, 0.12 }),
                Rows(new[] { 0.56, 0.37, 0.34 }, new[] { 0.34, 0.19, 0.16 }, new[] { 0.49, 0.35, 0.33 }, new[] { 0.28, 0.17, 0.15 })),
            _ => (
                Rows(new[] { 1.26, 0.71, 0.63 }, new[] { 0.68, 0.33, 0.27 }, new[] { 1.02, 0.66, 0.60 }, new[] { 0.53, 0.29, 0.25 }),
                Rows(new[] { 0.49, 0.29, 0.25 }, new[] { 0.34, 0.16, 0.13 }, new[] { 0.41, 0.26, 0.24 }, new[] { 0.27, 0.14, 0.12 }),
                Rows(new[] { 0.56, 0.37, 0.34 }, new[] { 0.34, 0.19, 0.16 }, new[] { 0.49, 0.35, 0.33 }, new[] { 0.28, 0.17, 0.15 }))
        };
        return (cooling ? coolingRows : heatPump ? pump : furnace)[group][column];
    }

    /// <summary>The project's ducts' factor for its loads: where they run, how many storeys the house has, and whether its heat comes from a heat pump.</summary>
    public static double DuctFactor(BimDocument document, bool cooling, int storeys)
    {
        var project = document.ProjectInformation;
        var heatPump = document.All<MepSystem>().Where(system => system.Kind == SystemKind.Heating)
            .Select(zone => MepSystems.SourceOf(document, zone)).OfType<Component>()
            .Any(source => document.FindType<ComponentType>(source.TypeId)?.Form == ComponentForm.HeatPump);
        return DuctFactor(project.DuctRun, cooling, heatPump, storeys, project.DuctsSealed, project.DuctInsulation);
    }
}

/// <summary>One surface of a room and what it lets in at the summer design conditions: its area, its cooling factor (W/m²) and so its gain.</summary>
public sealed record CoolingLine(string What, double Area, double Factor, double Watts, Element? Through, HeatSurface Kind);

/// <summary>One room's sensible cooling load: through each surface, with its share of the air, and its share of what the house's people and appliances give off.</summary>
public sealed record RoomCooling(Room Room, string Name, IReadOnlyList<CoolingLine> Lines, double Volume, double Air, double Internal)
{
    /// <summary>Through its surfaces, W.</summary>
    public double Envelope => Lines.Sum(line => line.Watts);

    /// <summary>What its cooling has to take out, W.</summary>
    public double Sensible => Envelope + Air + Internal;
}

/// <summary>
/// A US house's cooling load at its summer design conditions: room by room, and the whole house's -
/// its surfaces, its air, its people and appliances, its ducts - sensible, and latent with the
/// moisture its air and people bring in.
/// </summary>
public sealed record BuildingCooling(
    double Outside, double? WetBulb, double DailyRange, double MoistureDifference, IReadOnlyList<RoomCooling> Rooms,
    ResidentialAir.Flow Air, double AirSensible, double AirLatent, double Internal, double InternalLatent, double DuctFactor)
{
    public double Envelope => Rooms.Sum(room => room.Envelope);

    /// <summary>The house's own sensible load, W, and what its ducts add to it.</summary>
    public double Subtotal => Envelope + AirSensible + Internal;

    public double Distribution => DuctFactor * Subtotal;

    public double Sensible => Subtotal + Distribution;

    public double Latent => AirLatent + InternalLatent;

    public double Total => Sensible + Latent;

    /// <summary>The total in tons of refrigeration, 12,000 Btu/h each.</summary>
    public double Tons => Total * Units.BtuPerHourPerWatt / 12000;

    public RoomCooling? Of(Room room) => Rooms.FirstOrDefault(each => ReferenceEquals(each.Room, room));
}

/// <summary>
/// The cooling load of a US house by ASHRAE's residential load factor method (Handbook - Fundamentals
/// 2021, chapter 17), which IRC M1401.3 and IECC R403.7 take as another approved method beside ACCA
/// Manual J: inside at 75 °F and 50%, outside at the summer's 1% dry bulb and its mean coincident wet
/// bulb, with the day's range.
///
/// Each surface's gain is its area times its cooling factor. An opaque surface's is
/// CF = U (OF_t Δt + OF_b + OF_r DR) (eq. 21, Table 7): a wall or door 1, 14.8 °F, -0.36; a roof over
/// the room 1, 68.9 α - 12.6, -0.36, and a ceiling under a vented attic 0.62, 25.7 α - 8.1, -0.19, α
/// the roof's solar absorptance (Table 8); a floor over the open air 1, 0, -0.06 and over a
/// crawlspace 0.33, 0, -0.28; a wall to a space not cooled taken shaded, 1, 0, -0.36, as the
/// chapter's example takes a garage's. A slab on the ground takes heat away, CF = 0.59 - 2.5 h_srf,
/// h_srf = 1 / (R_cvr + 0.68) for what covers it (eqs. 22 and 23). A window's is
/// CF = U (Δt - 0.46 DR) + PXI SHGC IAC FF_s (eq. 25): PXI the peak irradiance on its exposure at the
/// site's latitude (Table 10), through an insect screen on one that opens (T_x 0.64, Table 11), less
/// the direct part on the share an overhang shades - (SLF D - X) / h, the shade line factor of
/// Table 12 (eqs. 26 to 28); IAC its interior shading, taken as light closed-weave drapes half
/// closed, as the chapter's example does and as it advises in all but special cases (eq. 29,
/// Table 14); FF_s its exposure's solar load factor for a detached house (Table 13).
///
/// The air, as <see cref="ResidentialAir"/> takes it, shared among the rooms by their volume; the
/// people and appliances 464 + 0.7 A_cf + 75 N_oc Btu/h sensible and 68 + 0.07 A_cf + 41 N_oc latent,
/// N_oc the bedrooms and one more (eqs. 30, 31), the sensible given to the kitchen and living rooms;
/// the ducts' gain as Table 6 has it. Below the ground nothing is gained.
/// </summary>
public static class CoolingLoads
{
    /// <summary>Inside at the cooling design: 75 °F and 50%.</summary>
    public const double InsideF = 75, InsideHumidity = 0.5;

    /// <summary>A W/m² in Btu/h·ft², and a W/m·K in Btu/h·ft·°F.</summary>
    public const double BtuPerSquareFootPerWattPerSquareMetre = 0.3169983, WattsPerMetreKelvinPerBtuFootF = 1.730735;

    /// <summary>The house's cooling load; null for one not in the US, or with no summer design temperature set.</summary>
    public static BuildingCooling? Of(BimDocument document) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("cooling load", document, null, null, () => Work(document)) : Work(document);

    /// <summary>A room's cooling load on its own - the house's worked out for it.</summary>
    public static RoomCooling? OfRoom(BimDocument document, Room room) => Of(document)?.Of(room);

    // ---- the tables ---------------------------------------------------------

    /// <summary>The eight exposures and the horizontal, as the tables have them.</summary>
    public enum Exposure
    {
        North,
        Northeast,
        East,
        Southeast,
        South,
        Southwest,
        West,
        Northwest,
        Horizontal
    }

    /// <summary>The exposure a surface facing so many degrees clockwise from true north has: the nearest of the eight.</summary>
    public static Exposure ExposureOf(double bearing) => (Exposure)((int)Math.Round(((bearing % 360) + 360) % 360 / 45.0) % 8);

    private static readonly double[] Latitudes = { 20, 25, 30, 35, 40, 45, 50, 55, 60 };

    // Table 10: peak direct and diffuse irradiance, Btu/h·ft², by exposure - north, northeast or northwest, east or west,
    // southeast or southwest, south, horizontal - at each latitude.
    private static readonly double[][] Direct =
    {
        new double[] { 38, 34, 31, 31, 33, 37, 44, 53, 65 },
        new double[] { 140, 140, 140, 141, 142, 143, 145, 147, 150 },
        new double[] { 158, 166, 174, 181, 187, 192, 198, 202, 206 },
        new double[] { 76, 95, 112, 129, 144, 158, 171, 183, 193 },
        new double[] { 0, 6, 36, 64, 90, 115, 139, 161, 181 },
        new double[] { 247, 250, 250, 248, 243, 234, 223, 210, 193 }
    };

    private static readonly double[][] Diffuse =
    {
        new double[] { 35, 31, 27, 24, 21, 18, 15, 13, 11 },
        new double[] { 50, 47, 44, 42, 39, 37, 35, 33, 32 },
        new double[] { 59, 56, 54, 52, 50, 49, 47, 46, 45 },
        new double[] { 63, 61, 59, 58, 56, 55, 54, 53, 52 },
        new double[] { 39, 62, 61, 60, 59, 58, 57, 56, 56 },
        new double[] { 54, 54, 54, 54, 54, 54, 54, 54, 54 }
    };

    // Table 12: shade line factors by the same exposures (no horizontal).
    private static readonly double[][] ShadeLine =
    {
        new[] { 2.8, 2.1, 1.4, 1.5, 1.7, 1.0, 0.8, 0.9, 0.8 },
        new[] { 1.4, 1.5, 1.6, 1.2, 1.3, 1.3, 0.9, 0.9, 0.8 },
        new[] { 1.2, 1.2, 1.1, 1.1, 1.1, 1.0, 1.0, 0.9, 0.8 },
        new[] { 2.1, 1.8, 2.0, 1.7, 1.5, 1.6, 1.4, 1.2, 1.1 },
        new[] { 20.0, 14.0, 6.9, 4.7, 3.3, 2.7, 2.1, 1.7, 1.4 }
    };

    private static int Row(Exposure exposure) => exposure switch
    {
        Exposure.North => 0,
        Exposure.Northeast or Exposure.Northwest => 1,
        Exposure.East or Exposure.West => 2,
        Exposure.Southeast or Exposure.Southwest => 3,
        Exposure.South => 4,
        _ => 5
    };

    private static double AtLatitude(double[] row, double latitude)
    {
        var at = Math.Clamp(latitude, 20, 60);
        var i = Math.Min((int)((at - 20) / 5), Latitudes.Length - 2);
        var share = (at - Latitudes[i]) / 5;
        return row[i] + (row[i + 1] - row[i]) * share;
    }

    /// <summary>Peak direct and diffuse irradiance on an exposure at a latitude, Btu/h·ft² (Table 10).</summary>
    public static (double Direct, double Diffuse) Irradiance(Exposure exposure, double latitude) =>
        (AtLatitude(Direct[Row(exposure)], latitude), AtLatitude(Diffuse[Row(exposure)], latitude));

    /// <summary>The shade line factor of an exposure at a latitude (Table 12); none for a horizontal surface.</summary>
    public static double ShadeLineFactor(Exposure exposure, double latitude) =>
        exposure == Exposure.Horizontal ? 0 : AtLatitude(ShadeLine[Row(exposure)], latitude);

    /// <summary>A detached house's fenestration solar load factor by exposure (Table 13).</summary>
    public static double SolarLoadFactor(Exposure exposure) => exposure switch
    {
        Exposure.North => 0.44,
        Exposure.Northeast => 0.21,
        Exposure.East => 0.31,
        Exposure.Southeast => 0.37,
        Exposure.South => 0.47,
        Exposure.Southwest => 0.58,
        Exposure.West => 0.56,
        Exposure.Northwest => 0.46,
        _ => 0.58
    };

    /// <summary>
    /// Light closed-weave drapes' interior attenuation fully closed, by glazing (Table 14): single
    /// clear 0.45, double clear 0.57, double low-e of high solar gain 0.64, of low 0.68.
    /// </summary>
    public static double ClosedDrapes(WindowType? type, double shgc)
    {
        var glazing = type?.GlazingType ?? "Double glazed, low-E";
        if (glazing.Contains("single", StringComparison.OrdinalIgnoreCase)) return 0.45;
        if (!glazing.Contains("low", StringComparison.OrdinalIgnoreCase)) return 0.57;
        return shgc < 0.4 ? 0.68 : 0.64;
    }

    /// <summary>The share of a window an overhang shades: (SLF D - X) / h, between none and all (eq. 28).</summary>
    public static double Shaded(double shadeLine, double depth, double gap, double height) =>
        height <= 0 || depth <= 0 ? 0 : Math.Clamp((shadeLine * depth - gap) / height, 0, 1);

    /// <summary>
    /// A window's cooling factor, Btu/h·ft² (eq. 25), from its U-factor (Btu/h·ft²·°F), the design
    /// difference and daily range (°F), its exposure and latitude, how much of it an overhang shades,
    /// what an attachment outside lets through, its SHGC and its interior shading.
    /// </summary>
    public static double WindowFactor(double u, double difference, double range, Exposure exposure, double latitude, double shaded, double screen, double shgc, double attenuation)
    {
        var (direct, diffuse) = Irradiance(exposure, latitude);
        var peak = screen * (diffuse + (1 - shaded) * direct);
        return u * (difference - 0.46 * range) + peak * shgc * attenuation * SolarLoadFactor(exposure);
    }

    /// <summary>An opaque surface's cooling factor, Btu/h·ft² (eq. 21, Table 7).</summary>
    public static double OpaqueFactor(double u, double difference, double range, double ofT, double ofB, double ofR) => u * (ofT * difference + ofB + ofR * range);

    /// <summary>A slab's cooling factor, Btu/h·ft² (eq. 23): less than nothing - it takes heat away into the ground.</summary>
    public static double SlabFactor(double coveringR) => 0.59 - 2.5 / (coveringR + 0.68);

    /// <summary>A roof's solar absorptance (Table 8), by what it is covered with: tile 0.80, metal 0.70, a white coating 0.30, asphalt shingles 0.85 - and so anything else, the medium shingle the chapter's example has.</summary>
    public static double Absorptance(string? roofName)
    {
        var name = roofName ?? string.Empty;
        bool Says(params string[] words) => words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
        return Says("white", "elastomeric", "cool roof") ? 0.30 : Says("tile") ? 0.80 : Says("metal", "steel") ? 0.70 : 0.85;
    }

    // ---- the house ----------------------------------------------------------

    private static BuildingCooling? Work(BimDocument document)
    {
        var project = document.ProjectInformation;
        if (!project.ClearanceStandard.IsUs() || project.SummerDesignTemperature is not { } outsideF) return null;
        if (HeatLoss.Surveyed(document) is not { } survey) return null;

        var difference = outsideF - InsideF;
        var range = project.DailyRange;
        var latitude = project.Latitude;
        var conditioned = survey.Rooms.Where(room => room.Heated).ToList();
        var areaFeet = conditioned.Sum(room => room.Area) * 1e6 / Units.SquareMillimetresPerSquareFoot;
        var bedrooms = Ventilation.WholeDwelling(document).Bedrooms;
        var occupants = Math.Max(1, bedrooms) + 1;

        // The people and appliances, given to the kitchen and the living rooms - the bedrooms, bathrooms and the rest not.
        var internalSensible = (464 + 0.7 * areaFeet + 75 * occupants) / Units.BtuPerHourPerWatt;
        var internalLatent = (68 + 0.07 * areaFeet + 41 * occupants) / Units.BtuPerHourPerWatt;
        bool Living(RoomHeatLoss room) =>
            Ventilation.UseOf(document, room.Room, room.Room.GetBoundary(document).Polygon) is RoomUse.Kitchen
            || Ventilation.UseOf(document, room.Room, room.Room.GetBoundary(document).Polygon) == RoomUse.Habitable
               && !room.Room.Name.Contains("bed", StringComparison.OrdinalIgnoreCase) && Bedrooms.Of(document, room.Room, room.Room.GetBoundary(document).Polygon) is null;
        var living = conditioned.Where(Living).ToList();
        if (living.Count == 0) living = conditioned;
        var livingArea = living.Sum(room => room.Area);

        // The air: infiltration at the cooling's driving force, and the ventilation with it.
        var stack = ResidentialAir.StackFeet(document, conditioned.Select(room => room.Room));
        var ventilation = Ventilation.ForHeatLoss(document);
        var air = ResidentialAir.Of(document, conditioned.SelectMany(room => room.Lines), stack, difference / 1.8, cooling: true, ventilation,
            room => conditioned.Any(each => ReferenceEquals(each.Room, room)));
        var altitude = ResidentialAir.AltitudeFactor(document);
        var airSensible = ResidentialAir.Sensible * altitude * air.SensibleFlow * difference / 1.8;

        // The moisture: outside at the dry and wet bulbs, inside at 75 °F and 50%.
        var pressure = Psychrometrics.Pressure(project.SiteAltitude);
        var moisture = project.SummerWetBulb is { } wetF
            ? Math.Max(0, Psychrometrics.FromWetBulb((outsideF - 32) / 1.8, (wetF - 32) / 1.8, pressure) - Psychrometrics.HumidityRatio((InsideF - 32) / 1.8, InsideHumidity, pressure))
            : 0;
        var airLatent = ResidentialAir.Latent * altitude * air.MoistFlow * moisture;

        var volume = conditioned.Sum(room => room.Volume);
        var rooms = conditioned.Select(room => new RoomCooling(room.Room, room.Name, Lines(document, room, difference, range, latitude), room.Volume,
                volume > 0 ? airSensible * room.Volume / volume : 0,
                living.Contains(room) && livingArea > 0 ? internalSensible * room.Area / livingArea : 0))
            .ToList();

        var storeys = conditioned.Select(room => room.Room.LevelId).Distinct().Count();
        return new BuildingCooling((outsideF - 32) / 1.8, project.SummerWetBulb is { } wet ? (wet - 32) / 1.8 : null, range / 1.8, moisture, rooms,
            air, airSensible, airLatent, internalSensible, internalLatent, ResidentialAir.DuctFactor(document, cooling: true, storeys));
    }

    /// <summary>What each of a room's surfaces lets in, from its heat loss's surfaces.</summary>
    private static List<CoolingLine> Lines(BimDocument document, RoomHeatLoss room, double difference, double range, double latitude)
    {
        var lines = new List<CoolingLine>();
        var north = document.ProjectInformation.TrueNorth;
        void Add(HeatLossLine line, string what, double factor)
        {
            // Factors in Btu/h·ft²; kept in W/m².
            var watts = factor / BtuPerSquareFootPerWattPerSquareMetre;
            lines.Add(new CoolingLine(what, line.Area, watts, line.Area * watts, line.Through, line.Kind));
        }

        foreach (var line in room.Lines)
        {
            var u = line.U * Units.UFactorPerUValue;
            switch (line.Kind)
            {
                // A slab on the ground: cooled by it, through what covers it.
                case HeatSurface.SlabEdge:
                    var floor = line.Through as Slab;
                    var covering = floor is null ? 0 : CoveringR(document, floor);
                    var slabArea = room.Area;
                    lines.Add(new CoolingLine($"Slab ({(floor is null ? "floor" : document.FindType<SlabType>(floor.TypeId)?.Name)})", slabArea,
                        SlabFactor(covering) / BtuPerSquareFootPerWattPerSquareMetre, slabArea * SlabFactor(covering) / BtuPerSquareFootPerWattPerSquareMetre, line.Through, HeatSurface.GroundFloor));
                    break;
                case HeatSurface.GroundFloor:
                    break;
                case HeatSurface.Window when line.Path == HeatPath.Outside:
                    Add(line, line.What, WindowFactorOf(document, line, u, difference, range, latitude, north));
                    break;
                case HeatSurface.Wall or HeatSurface.Door or HeatSurface.Window when line.Path == HeatPath.Outside:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 1, 14.8, -0.36));
                    break;
                case HeatSurface.Roof or HeatSurface.Ceiling when line.Path == HeatPath.Outside:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 1, 68.9 * Absorptance(RoofName(document, line)) - 12.6, -0.36));
                    break;
                case HeatSurface.CeilingUnderLoft:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 0.62, 25.7 * Absorptance(RoofName(document, line)) - 8.1, -0.19));
                    break;
                case HeatSurface.Floor when line.Path == HeatPath.Outside:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 1, 0, -0.06));
                    break;
                case HeatSurface.SuspendedFloor:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 0.33, 0, -0.28));
                    break;
                case not HeatSurface.Floor when line.Path == HeatPath.Unheated:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 1, 0, -0.36));
                    break;
                case HeatSurface.Floor when line.Path == HeatPath.Unheated:
                    Add(line, line.What, OpaqueFactor(u, difference, range, 0.33, 0, -0.28));
                    break;
            }
        }

        return lines;
    }

    private static string? RoofName(BimDocument document, HeatLossLine line) =>
        line.Through is Slab slab ? document.FindType<SlabType>(slab.TypeId)?.Name : null;

    /// <summary>What covers a slab, h·ft²·°F/Btu: the layers over its structural core.</summary>
    private static double CoveringR(BimDocument document, Slab floor)
    {
        if (document.FindType<SlabType>(floor.TypeId) is not { } type) return 0;
        var layers = type.Structure.Layers;
        var core = layers.Select((layer, i) => (layer, i)).FirstOrDefault(entry => entry.layer.Function == LayerFunction.Structure).i;
        var over = layers.Take(Math.Max(0, core)).Sum(layer => document.FindMaterial(layer.MaterialId)?.ThermalConductivity is > 0 and var k ? layer.Thickness / 1000 / k : 0);
        return over * 5.678263;
    }

    /// <summary>A window's cooling factor, Btu/h·ft²: its exposure from the way it faces, an overhang over it, its screen, its glass.</summary>
    private static double WindowFactorOf(BimDocument document, HeatLossLine line, double u, double difference, double range, double latitude, double north)
    {
        // True north so many degrees clockwise from up the plan: what faces up the plan faces that much west of north.
        // A roof window's sun a skylight's, from the sky overhead.
        var exposure = line.Tilt is not null ? Exposure.Horizontal : line.Facing is { } facing ? ExposureOf(facing - north) : Exposure.South;
        double shgc, screen = 1, height = 0;
        WindowType? windowType = null;
        switch (line.Through)
        {
            case RoofWindow roofWindow when document.FindType<RoofWindowType>(roofWindow.TypeId) is { } type:
                shgc = type.SolarHeatGainCoefficient;
                break;
            case Window window when document.FindType<WindowType>(window.TypeId) is { } type:
                windowType = type;
                shgc = type.SolarHeatGainCoefficient;
                screen = WindowLights.Opens(type) ? 0.64 : 1;
                height = window.HeightOf(type);
                break;
            case Opening opening when document.FindType<OpeningType>(opening.TypeId) is { } type:
                shgc = 0.4;
                height = opening.HeightOf(type);
                break;
            default:
                shgc = 0.4;
                break;
        }

        var shaded = line.Through is Opening shading && line.Facing is { } outward && Overhang(document, shading, outward) is { } overhang
            ? Shaded(ShadeLineFactor(exposure, latitude), overhang.Depth / Units.MillimetresPerFoot, overhang.Gap / Units.MillimetresPerFoot, height / Units.MillimetresPerFoot)
            : 0;
        var attenuation = 1 + 0.5 * (ClosedDrapes(windowType, shgc) - 1);
        return WindowFactor(u, difference, range, exposure, latitude, shaded, screen, shgc, attenuation);
    }

    /// <summary>
    /// The roof overhanging a window in an outside wall, looking out the way it faces on the plan
    /// (degrees clockwise from up the plan): how far the roof's edge stands out past the wall's face,
    /// and how far above the window's head, mm; none where no roof is over the ground before it.
    /// </summary>
    public static (double Depth, double Gap)? Overhang(BimDocument document, Opening opening, double facing)
    {
        if (document.Find<Wall>(opening.HostWallId) is not { } wall || document.GetWallType(wall) is not { } wallType
            || document.FindType<OpeningType>(opening.TypeId) is not { } type) return null;
        var head = (document.FindLevel(wall.LevelId)?.Elevation ?? 0) + wall.BaseOffset + opening.SillHeight + opening.HeightOf(type);
        var outward = new Vector2D(Math.Sin(facing * Math.PI / 180), Math.Cos(facing * Math.PI / 180));
        var face = wall.PointAt(wallType.Structure, opening.DistanceAlongWall, 0) + outward * (wallType.Structure.TotalWidth / 2);
        var roofs = document.All<Roof>().ToList();

        // Out from its face, as far as a roof over the head of the window still covers the ground.
        double depth = 0, edge = 0;
        for (var d = 25.0; d <= 3000; d += 25)
        {
            var at = face + outward * d;
            var over = roofs.Where(roof => roof.Contains(at)).Select(roof => roof.Surface(document).HeightAt(at)).Where(height => height > head - 1).DefaultIfEmpty(double.NaN).Min();
            if (double.IsNaN(over)) break;
            (depth, edge) = (d, over);
        }

        return depth > 0 ? (depth, Math.Max(0, edge - head)) : null;
    }
}
