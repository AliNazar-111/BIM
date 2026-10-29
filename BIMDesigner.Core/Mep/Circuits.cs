using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>A breaker's tripping curve: how many times its rating trips it at once - type B 5, C 10, D 20 (BS EN 60898).</summary>
public enum BreakerCurve
{
    B,
    C,
    D
}

/// <summary>How a circuit's flat twin and earth cable is installed, as BS 7671's Table 4D5 rates it.</summary>
public enum CableMethod
{
    /// <summary>Reference method C: clipped direct to a surface, or buried in plaster.</summary>
    ClippedDirect,

    /// <summary>Reference method A: in conduit in a thermally insulated wall.</summary>
    InsulatedWallConduit,

    /// <summary>Method 100: over a plasterboard ceiling, under thermal insulation not over 100 mm.</summary>
    UnderInsulation,

    /// <summary>Method 101: over a plasterboard ceiling, under more than 100 mm of insulation.</summary>
    UnderDeepInsulation,

    /// <summary>Method 102: in a stud wall filled with insulation, touching its inner lining.</summary>
    StudWallTouching,

    /// <summary>Method 103: in a stud wall filled with insulation, not touching its lining.</summary>
    StudWall
}

/// <summary>
/// A size of cable a circuit may be wired in: in the UK flat twin and earth (6242Y) - its line
/// and protective conductors, mm², its voltage drop, mV/A/m, and what it carries by how it is
/// installed (BS 7671 Table 4D5); in the US, NM-B copper - its circular mils, what it carries
/// at 60 °C (Table 310.16, 334.80) and at 90 °C, which bunching is taken from, and the most a
/// breaker may protect it at (240.4(D)).
/// </summary>
public sealed record CableSize(string Name, double Line, double Cpc, double MilliVolts, double[] Ratings, double CircularMils = 0, double MostBreaker = 0, double Ninety = 0)
{
    public double Rating(CableMethod method) => Ratings.Length == 1 ? Ratings[0] : Ratings[(int)method];
}

/// <summary>
/// A circuit worked out: its use and voltage, the load connected and the current it is designed
/// for (Ib), its breaker (In) and cable and what that carries as installed (Iz), how long it is
/// and the fittings along it, the voltage it drops and may, its earth fault loop impedance (Zs)
/// and the most its breaker allows - and for a standard socket circuit, the floor it serves and
/// how far the On-Site Guide takes it.
/// </summary>
public sealed record CircuitDesign(
    MepSystem Circuit, CircuitUse Use, double Voltage, double Connected, double Current, double Breaker, BreakerCurve Curve, CableSize Cable,
    CableMethod Method, double Capacity, double Length, bool Measured, double Drop, double DropLimit, double Impedance, double ImpedanceLimit,
    IReadOnlyList<(Component Fitting, double Distance)> Stops)
{
    /// <summary>For a standard socket circuit, the floor its sockets serve, m², and the most the On-Site Guide takes it to; and how long it may be, m.</summary>
    public double Area { get; init; }

    public double AreaLimit { get; init; }

    public double LengthLimit { get; init; }

    /// <summary>Whether it is one of the On-Site Guide's standard socket circuits - a ring or radial of the breaker and cable it sets out.</summary>
    public bool Standard => LengthLimit > 0;

    /// <summary>Whether its breaker and cable are what Size Circuits chose rather than set.</summary>
    public bool BreakerChosen { get; init; }

    public bool CableChosen { get; init; }
}

/// <summary>What a consumer unit's circuits come to: its ways, its supply, and the most it is drawn after diversity, A.</summary>
public sealed record BoardDemand(Component Board, IReadOnlyList<CircuitDesign> Circuits, int Ways, double Supply, double Demand, IReadOnlyList<string> Workings);

/// <summary>
/// A home's circuits designed and checked as BS 7671 (18th edition, A4:2026) and the IET's On-Site
/// Guide have them, and in the US the NEC (2023).
///
/// Each circuit runs from its consumer unit up into the void over its storey's ceiling and down to
/// each of its fittings in turn, the nearest next - and a ring back to the board. Its design current
/// (Ib): a light's circuit-watts, a cooker's 10 A and 30% of the rest (On-Site Guide Table A2), an
/// electric shower's or immersion heater's whole load; a standard socket circuit its breaker's rating.
/// Its breaker (In) is no less (433.1.1); its cable carries no less as installed (Iz, Table 4D5 by the
/// installation method, times Table 4C1's factor for circuits bunched with it), a ring's 20 A at least
/// (433.1.204). Its voltage drops no more than 3% to its furthest light, 5% for anything else
/// (Appendix 4) - worked from each fitting's current and how far along it is. Its earth fault loop
/// impedance - Ze and the line and protective conductors' resistance (BS EN 60228) at 70 °C, 1.20
/// times as the On-Site Guide takes it - is within Table 41.3's for its breaker to disconnect in 0.4 s,
/// where no RCD gives that protection. The On-Site Guide's standard circuits (Appendix H, Table 7.1): a
/// 32 A ring of 2.5/1.5 mm² serving 100 m² and up to 106 m (96 on TN-S with no RCD), a 20 A radial of
/// 2.5 mm² 50 m² and 42 m, a 32 A radial of 4 mm² 75 m² and 43 m. A 30 mA RCD on sockets up to 32 A
/// (411.3.3), on a home's lighting (411.3.4) and on every circuit in a bathroom (701.411.3.3).
///
/// In the US: 120 V circuits, 240 V for a range or a water heater; NM-B at its 60 °C ampacity (334.80,
/// Table 310.16), a 14 AWG cable on 15 A, 12 AWG on 20, 10 AWG on 30 at most (240.4(D)); 3% voltage
/// drop (210.19 Informational Note); two 20 A small-appliance circuits for the kitchen's receptacles,
/// serving nothing else, a 20 A laundry circuit, 20 A for the bathrooms and the garage (210.11(C));
/// GFCI where 210.8 asks and AFCI where 210.12(B) does; fixed equipment no more than half a circuit
/// that has lights or receptacles on it (210.23(B)(2)).
/// </summary>
public static class Circuits
{
    /// <summary>The UK's supply voltage, and the US's two.</summary>
    public const double Uk = 230, Us = 120, UsTwo = 240;

    /// <summary>How high over its storey's level a circuit's cable runs between its fittings: in the void over the ceiling, mm.</summary>
    public const double RunHeight = 2500;

    /// <summary>A cable's resistance at 70 °C over its resistance at 20 °C, as the On-Site Guide takes it.</summary>
    public const double Hot = 1.20;

    private static readonly double[] UkRatings = { 6, 10, 16, 20, 25, 32, 40, 45, 50, 63 };
    private static readonly double[] UsRatings = { 15, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100 };

    /// <summary>The breakers a circuit may have, A: BS EN 60898's ratings, or the NEC's standard ones (240.6(A)).</summary>
    public static IReadOnlyList<double> Ratings(bool us) => us ? UsRatings : UkRatings;

    private static readonly CableSize[] UkSizes =
    {
        new("1.0 mm²", 1.0, 1.0, 44, new double[] { 16, 11.5, 13, 10.5, 13, 8 }),
        new("1.5 mm²", 1.5, 1.0, 29, new double[] { 20, 14.5, 16, 13, 16, 10 }),
        new("2.5 mm²", 2.5, 1.5, 18, new double[] { 27, 20, 21, 17, 21, 13.5 }),
        new("4 mm²", 4, 1.5, 11, new double[] { 37, 26, 27, 22, 27, 17.5 }),
        new("6 mm²", 6, 2.5, 7.3, new double[] { 47, 32, 34, 27, 35, 23.5 }),
        new("10 mm²", 10, 4, 4.4, new double[] { 64, 44, 45, 36, 47, 32 }),
        new("16 mm²", 16, 6, 2.8, new double[] { 85, 57, 57, 46, 63, 42.5 })
    };

    private static readonly CableSize[] UsSizes =
    {
        new("14 AWG", 0, 0, 0, new double[] { 15 }, 4110, 15, 25),
        new("12 AWG", 0, 0, 0, new double[] { 20 }, 6530, 20, 30),
        new("10 AWG", 0, 0, 0, new double[] { 30 }, 10380, 30, 40),
        new("8 AWG", 0, 0, 0, new double[] { 40 }, 16510, 0, 55),
        new("6 AWG", 0, 0, 0, new double[] { 55 }, 26240, 0, 75),
        new("4 AWG", 0, 0, 0, new double[] { 70 }, 41740, 0, 95)
    };

    /// <summary>The cables a circuit may be wired in.</summary>
    public static IReadOnlyList<CableSize> Sizes(bool us) => us ? UsSizes : UkSizes;

    /// <summary>A copper conductor's resistance at 20 °C, mΩ/m, by its size (BS EN 60228).</summary>
    private static double Resistance(double size) => size switch
    {
        <= 1.0 => 18.1,
        <= 1.5 => 12.1,
        <= 2.5 => 7.41,
        <= 4 => 4.61,
        <= 6 => 3.08,
        <= 10 => 1.83,
        _ => 1.15
    };

    /// <summary>Table 4C1's factor for cables bunched with others, by how many circuits there are - between its columns, the next more.</summary>
    public static double GroupingFactor(int circuits) => circuits switch
    {
        <= 1 => 1.0,
        2 => 0.80,
        3 => 0.70,
        4 => 0.65,
        5 => 0.60,
        6 => 0.57,
        7 => 0.54,
        8 => 0.52,
        9 => 0.50,
        <= 12 => 0.45,
        <= 16 => 0.41,
        _ => 0.38
    };

    /// <summary>In the US, Table 310.15(C)(1)'s adjustment for more than three current-carrying conductors together - two to each cable.</summary>
    private static double UsAdjustment(int cables) => (cables * 2) switch
    {
        <= 3 => 1.0,
        <= 6 => 0.80,
        <= 9 => 0.70,
        <= 20 => 0.50,
        <= 30 => 0.45,
        <= 40 => 0.40,
        _ => 0.35
    };

    public static string Title(this CableMethod method) => method switch
    {
        CableMethod.ClippedDirect => "Clipped direct (C)",
        CableMethod.InsulatedWallConduit => "In conduit in an insulated wall (A)",
        CableMethod.UnderInsulation => "Under up to 100 mm of loft insulation (100)",
        CableMethod.UnderDeepInsulation => "Under more than 100 mm of loft insulation (101)",
        CableMethod.StudWallTouching => "In an insulated stud wall, touching its lining (102)",
        _ => "In an insulated stud wall, not touching its lining (103)"
    };

    // ---- what fittings draw -------------------------------------------------------

    /// <summary>Whether a fitting of this type draws from a circuit by its own rating: not a socket, a switch or the board - and a shower, which may be electric.</summary>
    public static bool TakesLoad(ComponentType type) =>
        type.Form == ComponentForm.Shower
        || type.Form is not (ComponentForm.Socket or ComponentForm.Switch or ComponentForm.ConsumerUnit)
        && Connectors.Of(type).Any(connector => connector.Kind == SystemKind.Power && !connector.Feeds);

    /// <summary>What a fitting draws from its circuit, W: its maker's rating, or what one of its kind typically does.</summary>
    public static double LoadOf(ComponentType type) => type.Load > 0 ? type.Load : type.Form switch
    {
        ComponentForm.CeilingLight or ComponentForm.WallLight => type.Lumens > 0 && type.Efficacy > 0 ? type.Lumens / type.Efficacy : 10,
        ComponentForm.ShaverSocket => 20,
        ComponentForm.ExtractFan => 20,
        ComponentForm.CookerHood => 150,
        // A gas cooker's ignition and its oven's light and fan; an electric one's rings and oven.
        ComponentForm.Cooker => type.GasInput > 0 ? 100 : 10000,
        ComponentForm.OvenHousing => 3000,
        ComponentForm.Boiler => 120,
        ComponentForm.HeatPump => 3000,
        ComponentForm.HeatRecoveryUnit => 60,
        ComponentForm.ExtractUnit => 40,
        ComponentForm.HotWaterCylinder => 3000,
        ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm => 1,
        ComponentForm.Dishwasher => 1200,
        ComponentForm.TumbleDryer => 5000,
        _ => 0
    };

    private static ComponentType? TypeOf(BimDocument document, Component component) => document.FindType<ComponentType>(component.TypeId);

    private static bool IsSocket(BimDocument document, Component component) => TypeOf(document, component)?.Form is ComponentForm.Socket;

    /// <summary>The power circuits in the model.</summary>
    public static IReadOnlyList<MepSystem> Of(BimDocument document) => document.All<MepSystem>().Where(system => system.Kind == SystemKind.Power).ToList();

    // ---- the run from the board ---------------------------------------------------

    /// <summary>
    /// A circuit's run from its board: up into the void over the ceiling, along it - square to the
    /// building - and down to each fitting, the nearest next; a ring back to the board. Its length, m,
    /// and how far along it each fitting is.
    /// </summary>
    public static (double Length, IReadOnlyList<(Component Fitting, double Distance)> Stops) Route(BimDocument document, MepSystem circuit) =>
        ModelCache.Of(document) is { } kept ? kept.Keep("circuit route", circuit, null, null, () => Walk(document, circuit)) : Walk(document, circuit);

    private static (double Length, IReadOnlyList<(Component Fitting, double Distance)> Stops) Walk(BimDocument document, MepSystem circuit)
    {
        Point3D? Port(Component component) =>
            Connectors.Placed(document, component).Where(placed => placed.Connector.Kind == SystemKind.Power).Select(placed => (Point3D?)placed.At).FirstOrDefault();
        double Void(Component component) => (document.FindLevel(component.LevelId)?.Elevation ?? 0) + RunHeight;

        var left = MepSystems.MembersOf(document, circuit).Select(member => (Fitting: member, At: Port(member))).Where(entry => entry.At is not null)
            .Select(entry => (entry.Fitting, At: entry.At!.Value, Void: Void(entry.Fitting))).ToList();
        if (left.Count == 0) return (0, Array.Empty<(Component, double)>());

        var board = MepSystems.SourceOf(document, circuit);
        var start = board is not null && Port(board) is { } feed ? (At: feed, Void: Void(board)) : (At: left[0].At, Void: left[0].Void);
        var (here, hereVoid) = start;
        var travelled = 0.0;
        var stops = new List<(Component, double)>();
        while (left.Count > 0)
        {
            var next = left.OrderBy(entry => Math.Abs(entry.At.X - here.X) + Math.Abs(entry.At.Y - here.Y) + Math.Abs(entry.Void - hereVoid)).First();
            left.Remove(next);
            travelled += Math.Abs(hereVoid - here.Z) + Math.Abs(next.At.X - here.X) + Math.Abs(next.At.Y - here.Y) + Math.Abs(next.Void - hereVoid) + Math.Abs(next.Void - next.At.Z);
            stops.Add((next.Fitting, travelled / 1000));
            (here, hereVoid) = (next.At, next.Void);
        }

        // A ring comes back to the board.
        if (circuit.Use == CircuitUse.RingFinal && board is not null && !document.ProjectInformation.ClearanceStandard.IsUs())
            travelled += Math.Abs(hereVoid - here.Z) + Math.Abs(start.At.X - here.X) + Math.Abs(start.At.Y - here.Y) + Math.Abs(start.Void - hereVoid) + Math.Abs(start.Void - start.At.Z);
        return (travelled / 1000, stops);
    }

    // ---- where fittings are -------------------------------------------------------

    /// <summary>What a fitting's room is, as the NEC's circuit rules sort rooms.</summary>
    private enum Place
    {
        Kitchen,
        Dining,
        Bathroom,
        Laundry,
        Garage,
        Basement,
        Hall,
        Closet,
        Habitable,
        Other,
        Outdoors
    }

    private static Place PlaceOf(BimDocument document, Component fitting)
    {
        if (RoomsOf.Of(document, fitting) is not { } room) return Place.Outdoors;
        var name = room.Name.ToLowerInvariant();
        if (name.Contains("garage")) return Place.Garage;
        if (name.Contains("basement") || name.Contains("cellar")) return Place.Basement;
        var use = Ventilation.UseOf(document, room, room.GetBoundary(document).Polygon);
        if (use == RoomUse.Kitchen) return Place.Kitchen;
        if (use is RoomUse.Bathroom or RoomUse.Wc) return Place.Bathroom;
        if (use == RoomUse.Utility || name.Contains("laundry")) return Place.Laundry;
        if (name.Contains("dining") || name.Contains("pantry") || name.Contains("breakfast")) return Place.Dining;
        if (name.Contains("closet") || name.Contains("wardrobe") || name.Contains("dressing")) return Place.Closet;
        if (new[] { "hall", "landing", "corridor", "lobby", "foyer", "entry" }.Any(name.Contains)) return Place.Hall;
        return use == RoomUse.Habitable ? Place.Habitable : Place.Other;
    }

    /// <summary>Whether a fitting is in a room with a bath or shower - a location BS 7671's section 701 covers.</summary>
    private static bool InBathroom(BimDocument document, Component fitting) =>
        RoomsOf.Of(document, fitting) is { } room && Ventilation.UseOf(document, room, room.GetBoundary(document).Polygon) == RoomUse.Bathroom;

    // ---- the design ---------------------------------------------------------------

    private static double Smallest(IEnumerable<double> ratings, double atLeast) => ratings.Where(rating => rating >= atLeast - 1e-9).DefaultIfEmpty(ratings.Max()).Min();

    /// <summary>The voltage a circuit runs at: 230 V in the UK; in the US 240 V for a range, a water heater or a heat pump, 120 V otherwise.</summary>
    public static double VoltageOf(BimDocument document, MepSystem circuit)
    {
        if (!document.ProjectInformation.ClearanceStandard.IsUs()) return Uk;
        return circuit.Use is CircuitUse.Cooker or CircuitUse.ImmersionHeater
               || MepSystems.MembersOf(document, circuit).Any(member => TypeOf(document, member)?.Form is ComponentForm.HeatPump or ComponentForm.Shower)
            ? UsTwo
            : Us;
    }

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>Whether a circuit is a socket circuit: sockets on it, whatever else - fused spurs, an alarm - is too.</summary>
    private static bool OfSockets(BimDocument document, IReadOnlyList<Component> members) =>
        members.Any(member => IsSocket(document, member));

    /// <summary>The breaker a circuit's use usually has, given the current it carries, A.</summary>
    private static double UsualBreaker(BimDocument document, MepSystem circuit, IReadOnlyList<Component> members, double load, double area)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var ratings = Ratings(us);
        var voltage = VoltageOf(document, circuit);
        if (us)
        {
            if (OfSockets(document, members))
                return members.Any(member => PlaceOf(document, member) is Place.Kitchen or Place.Dining or Place.Bathroom or Place.Laundry or Place.Garage) ? 20 : 15;
            return circuit.Use switch
            {
                // A range of 8¾ kW or more on 40 A at least (210.19(A)(3)).
                CircuitUse.Cooker => Smallest(ratings, Math.Max(RangeDemand(load / 1000) / UsTwo, load >= 8750 ? 40 : 0)),
                CircuitUse.ImmersionHeater => Smallest(ratings, load / voltage * 1.25),
                _ => Smallest(ratings, Math.Max(load / voltage, 0))
            };
        }

        if (OfSockets(document, members))
            return circuit.Use == CircuitUse.RingFinal || area > 50 ? 32 : 20;
        return circuit.Use switch
        {
            CircuitUse.Cooker => Smallest(ratings, Math.Max(32, CookerCurrent(load / voltage))),
            CircuitUse.ImmersionHeater => Smallest(ratings, Math.Max(16, load / voltage)),
            _ => Smallest(ratings, Math.Max(6, load / voltage))
        };
    }

    /// <summary>A cooker's design current, A: 10 A and 30% of the rest (the On-Site Guide's Table A2).</summary>
    public static double CookerCurrent(double current) => current <= 10 ? current : 10 + 0.3 * (current - 10);

    /// <summary>The demand a range of so many kW is taken at, W: Table 220.55's Column C, 8 kW for one up to 12 kW and 5% more for each kW over or major fraction of one (Note 1).</summary>
    public static double RangeDemand(double kw)
    {
        if (kw <= 12) return Math.Min(8000, kw * 1000);
        var over = kw - 12;
        var whole = Math.Floor(over + 1e-9);
        return 8000 * (1 + 0.05 * (whole + (over - whole > 0.5 + 1e-9 ? 1 : 0)));
    }

    /// <summary>A circuit as it is designed: its breaker and cable as set, or as Size Circuits would choose them where not; null for a circuit with nothing on it.</summary>
    public static CircuitDesign? Design(BimDocument document, MepSystem circuit)
    {
        if (ModelCache.Of(document) is { } kept) return kept.Keep("circuit design", circuit, null, null, () => Designed(document, circuit));

        // Choosing tries each cable on the same run and rooms: worked out once for all of them.
        using var cache = ModelCache.Begin(document);
        return Design(document, circuit);
    }

    private static CircuitDesign? Designed(BimDocument document, MepSystem circuit)
    {
        if (circuit.Kind != SystemKind.Power || MepSystems.MembersOf(document, circuit).Count == 0) return null;
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var set = Sizes(us).FirstOrDefault(size => size.Name == circuit.Cable);
        if (circuit.Breaker > 0 && set is not null) return Evaluate(document, circuit, circuit.Breaker, set);
        var chosen = Choose(document, circuit, circuit.Breaker > 0 ? circuit.Breaker : null, set);
        return chosen with { BreakerChosen = circuit.Breaker <= 0, CableChosen = set is null };
    }

    /// <summary>
    /// The breaker and cable Size Circuits chooses: the breaker the circuit's use usually has, or the
    /// smallest that carries it; the smallest cable that carries the breaker as installed, keeps its
    /// voltage drop and earth loop within the rules - a ring's 2.5 mm² at least, a 20 A radial's 2.5 and a
    /// 32 A radial's 4 as the On-Site Guide has them.
    /// </summary>
    public static CircuitDesign Choose(BimDocument document, MepSystem circuit, double? breaker = null, CableSize? cable = null)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var members = MepSystems.MembersOf(document, circuit);
        var load = members.Sum(member => TypeOf(document, member) is { } type ? LoadOf(type) : 0);
        var rating = breaker ?? UsualBreaker(document, circuit, members, load, AreaServed(document, members));
        CircuitDesign? last = null;
        foreach (var size in cable is not null ? new[] { cable } : Sizes(us))
        {
            if (!us && OfSockets(document, members) && size.Line < (rating > 20 && circuit.Use != CircuitUse.RingFinal ? 4 : 2.5) - 1e-9) continue;
            if (us && OfSockets(document, members) && size.MostBreaker > 0 && size.MostBreaker < rating - 1e-9) continue;
            last = Evaluate(document, circuit, rating, size);
            if (Faults(document, last).Count == 0) return last;
        }

        return last ?? Evaluate(document, circuit, rating, cable ?? Sizes(us)[^1]);
    }

    /// <summary>The floor a circuit's sockets serve: the rooms they are in, m².</summary>
    private static double AreaServed(BimDocument document, IReadOnlyList<Component> members) =>
        members.Where(member => IsSocket(document, member)).Select(member => RoomsOf.Of(document, member)).OfType<Room>().Distinct()
            .Sum(room => room.GetBoundary(document).Area) / 1e6;

    /// <summary>A circuit worked out on a breaker and a cable.</summary>
    public static CircuitDesign Evaluate(BimDocument document, MepSystem circuit, double breaker, CableSize cable)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var members = MepSystems.MembersOf(document, circuit);
        var voltage = VoltageOf(document, circuit);
        var loads = members.ToDictionary(member => member, member => TypeOf(document, member) is { } type ? LoadOf(type) : 0);
        var connected = loads.Values.Sum();
        var sockets = OfSockets(document, members);
        var use = us && circuit.Use == CircuitUse.RingFinal ? CircuitUse.Radial : circuit.Use;

        // The current it is designed for.
        var full = connected / voltage;
        var current = us
            ? sockets ? 0.8 * breaker
              : use == CircuitUse.Cooker ? RangeDemand(connected / 1000) / voltage
              : use == CircuitUse.ImmersionHeater ? full * 1.25
              : full
            : sockets ? breaker
              : use == CircuitUse.Cooker ? CookerCurrent(full)
              : full;

        // Its run, measured or as set.
        var (measured, stops) = Route(document, circuit);
        var length = circuit.CableLength > 0 ? circuit.CableLength : measured;
        var scale = measured > 0 ? length / measured : 1;
        var along = stops.Select(stop => (stop.Fitting, Distance: stop.Distance * scale)).ToList();

        // What it carries as installed: in the US, NM-B bunched is taken down from its 90 °C rating and kept to its 60 °C (334.80).
        var capacity = us
            ? cable.Ninety > 0 ? Math.Min(cable.Rating(circuit.Method), cable.Ninety * UsAdjustment(circuit.Grouped)) : cable.Rating(circuit.Method) * UsAdjustment(circuit.Grouped)
            : cable.Rating(circuit.Method) * GroupingFactor(circuit.Grouped);

        // Its voltage drop: each fitting's share of the current over its distance - a ring's load at its middle, a socket circuit's at its end.
        var ring = !us && use == CircuitUse.RingFinal;
        double moment;
        if (ring) moment = current * length / 4;
        else if (connected > 0 && !sockets)
            moment = along.Sum(stop => loads[stop.Fitting] / connected * current * stop.Distance);
        else moment = current * (along.Count > 0 ? along[^1].Distance : length);
        var drop = us
            ? 2 * 12.9 * moment * 3.28084 / cable.CircularMils
            : cable.MilliVolts * moment / 1000;
        var lighting = use == CircuitUse.Lighting || members.Count > 0 && members.All(member => TypeOf(document, member)?.Kind is BuiltInCategory.LightingFixtures or BuiltInCategory.LightingDevices);
        var dropLimit = voltage * (us || lighting ? 0.03 : 0.05);

        // Its earth fault loop impedance, and the most its breaker allows.
        double impedance = 0, impedanceLimit = 0;
        if (!us)
        {
            var external = MepSystems.SourceOf(document, circuit) is { } board && TypeOf(document, board) is { ExternalImpedance: > 0 } boardType ? boardType.ExternalImpedance : 0.35;
            var conductors = (Resistance(cable.Line) + Resistance(cable.Cpc)) * Hot * length / 1000;
            impedance = external + (ring ? conductors / 4 : conductors);
            impedanceLimit = 0.95 * Uk / (breaker * circuit.Curve switch { BreakerCurve.B => 5, BreakerCurve.C => 10, _ => 20 });
        }

        var design = new CircuitDesign(circuit, use, voltage, connected, current, breaker, circuit.Curve, cable, circuit.Method, capacity, length,
            circuit.CableLength <= 0, drop, dropLimit, impedance, impedanceLimit, along);

        // The On-Site Guide's standard socket circuits: the floor they serve and how far they go.
        if (!us && sockets && circuit.Curve == BreakerCurve.B && circuit.Method == CableMethod.ClippedDirect)
        {
            var tns = impedance > 0 && MepSystems.SourceOf(document, circuit) is { } unit && TypeOf(document, unit) is { ExternalImpedance: > 0.35 + 1e-9 };
            var standard = (ring, breaker, cable.Line) switch
            {
                (true, 32, 2.5) => (Area: 100.0, Length: tns && !circuit.Rcd ? 96.0 : 106.0),
                (false, 20, 2.5) => (Area: 50.0, Length: 42.0),
                (false, 32, 4) => (Area: 75.0, Length: 43.0),
                _ => (Area: 0.0, Length: 0.0)
            };
            if (standard.Length > 0)
                design = design with { Area = AreaServed(document, members), AreaLimit = standard.Area, LengthLimit = standard.Length };
        }

        return design;
    }

    // ---- what is wrong with it ----------------------------------------------------

    /// <summary>What is wrong with a circuit's breaker and cable: overloaded, unprotected, too long.</summary>
    public static IReadOnlyList<string> Faults(BimDocument document, CircuitDesign design)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var faults = new List<string>();
        var name = $"{(us ? string.Empty : design.Curve.ToString())}{design.Breaker:0}";
        if (design.Current > design.Breaker + 1e-6)
            faults.Add(us
                ? $"its {design.Breaker:0} A breaker is less than the {design.Current:0.#} A it carries"
                : $"its {name} breaker is less than the {design.Current:0.#} A it is designed for - BS 7671 (433.1.1) asks Ib ≤ In");

        if (us)
        {
            var bunched = design.Circuit.Grouped > 1 ? $", bunched with {design.Circuit.Grouped - 1} more" : string.Empty;
            if (design.Capacity < design.Current - 1e-6)
                faults.Add($"its {design.Cable.Name} carries {design.Capacity:0.#} A as installed{bunched} - less than the {design.Current:0.#} A it is to carry; NEC 210.19(A)(1) asks no less");

            // The next breaker up from what the cable carries (240.4(B)) - not for a circuit of receptacles - and 240.4(D)'s limit for the small sizes.
            var receptacles = MepSystems.MembersOf(document, design.Circuit).Count(member => IsSocket(document, member)) > 1;
            var rounded = receptacles ? UsRatings.Where(rating => rating <= design.Capacity + 1e-9).DefaultIfEmpty(0).Max() : Smallest(UsRatings, design.Capacity);
            var most = design.Cable.MostBreaker > 0 ? Math.Min(design.Cable.MostBreaker, rounded) : rounded;
            if (design.Breaker > most + 1e-6)
                faults.Add(design.Cable.MostBreaker > 0 && most >= design.Cable.MostBreaker - 1e-6
                    ? $"its {design.Cable.Name} is protected at {design.Breaker:0} A - NEC 240.4(D) allows {most:0} A at most"
                    : $"its {design.Cable.Name} is protected at {design.Breaker:0} A - it carries {design.Capacity:0.#} A as installed{bunched}, and NEC 240.4(B) allows {(most > 0 ? $"{most:0} A" : "no standard breaker")} at most{(receptacles ? " on a circuit of receptacles" : string.Empty)}");
        }
        else
        {
            // A ring's two legs share its current: 433.1.204 asks 20 A of its cable rather than its breaker's rating.
            if (design.Use != CircuitUse.RingFinal && design.Capacity < design.Breaker - 1e-6)
                faults.Add($"its {design.Cable.Name} cable carries {design.Capacity:0.#} A {Lower(design.Method.Title())}{(design.Circuit.Grouped > 1 ? $", grouped with {design.Circuit.Grouped - 1} more" : string.Empty)} - less than its {name} breaker; BS 7671 (433.1.1, Table 4D5) asks In ≤ Iz");
            if (design.Use == CircuitUse.RingFinal && (design.Capacity < 20 - 1e-6 || design.Cable.Line < 2.5 - 1e-9))
                faults.Add($"a ring final's cable carries 20 A at least and is 2.5 mm² at least (433.1.204) - its {design.Cable.Name} carries {design.Capacity:0.#} A as installed");
        }

        if (design.Standard)
        {
            if (design.Length > design.LengthLimit + 0.05)
                faults.Add($"it runs {design.Length:0.#} m - the On-Site Guide (Table 7.1) takes a {(design.Use == CircuitUse.RingFinal ? "ring" : "radial")} of {design.Cable.Name} on a {name} to {design.LengthLimit:0} m");
            if (design.Area > design.AreaLimit + 0.05)
                faults.Add($"its sockets serve {Units.OfSquareMetres(design.Area, "0")} - the On-Site Guide (Appendix H) takes a {(design.Use == CircuitUse.RingFinal ? "ring final" : "radial")} of {design.Cable.Name} on a {name} to {Units.OfSquareMetres(design.AreaLimit, "0")}");
        }
        else
        {
            if (design.Drop > design.DropLimit + 1e-6)
                faults.Add(us
                    ? $"its voltage drops {design.Drop:0.0} V ({design.Drop / design.Voltage:P1}) to its furthest outlet - the NEC (210.19, Informational Note) suggests 3% at most"
                    : $"its voltage drops {design.Drop:0.0} V ({design.Drop / design.Voltage:P1}) - BS 7671 (Appendix 4) keeps it to {design.DropLimit / design.Voltage:P0} {(design.DropLimit / design.Voltage < 0.04 ? "for lighting" : "for anything but lighting")}");
        }

        // The earth loop, standard circuit or not: the On-Site Guide's lengths take the usual Ze, not a board's higher one.
        if (!us && !design.Circuit.Rcd && design.Impedance > design.ImpedanceLimit + 1e-6)
            faults.Add($"its earth fault loop impedance is {design.Impedance:0.00} Ω - Table 41.3 allows {design.ImpedanceLimit:0.00} Ω for a {name} to disconnect in 0.4 s; a larger cable, a shorter run, or an RCD");

        return faults;
    }

    /// <summary>A circuit's design as its Properties say it.</summary>
    public static string Describe(BimDocument document, MepSystem circuit)
    {
        if (Design(document, circuit) is not { } design) return "Nothing on it yet";
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var protection = us
            ? string.Join(" ", new[] { circuit.Rcd ? "GFCI" : null, circuit.ArcFault ? "AFCI" : null }.OfType<string>())
            : circuit.Rcd ? circuit.ArcFault ? "RCBO and AFDD" : "RCBO" : circuit.ArcFault ? "MCB and AFDD" : "MCB";
        var breaker = us ? $"{design.Breaker:0} A {protection}".Trim() : $"{design.Curve}{design.Breaker:0} {protection}";
        var cable = us
            ? $"{design.Cable.Name} NM-B, {design.Capacity:0} A"
            : $"{design.Cable.Name.Replace(" mm²", string.Empty)}/{design.Cable.Cpc:0.0#} mm² twin and earth {Lower(design.Method.Title())}, carrying {design.Capacity:0.#} A";
        var current = design.Standard
            ? $"designed for {design.Current:0} A as a standard {(design.Use == CircuitUse.RingFinal ? "ring" : "radial")}"
            : us && OfSockets(document, MepSystems.MembersOf(document, circuit))
                ? $"designed for {design.Current:0.#} A, 80% of it (210.23(B)(1))"
                : $"designed for {design.Current:0.#} A{(design.Connected > 0 ? $" ({design.Connected:#,0} W at {design.Voltage:0} V)" : string.Empty)}";
        var run = us ? $"{design.Length * 3.28084:0} ft" : $"{design.Length:0.#} m";
        var rest = design.Standard
            ? $"{run} of {design.LengthLimit:0}, serving {Units.OfSquareMetres(design.Area, "0")} of {design.AreaLimit:0} (On-Site Guide)"
            : us
                ? $"{run}, dropping {design.Drop:0.0} V ({design.Drop / design.Voltage:P1}) of 3%"
                : $"{run}, dropping {design.Drop:0.0} V ({design.Drop / design.Voltage:P1}) of {design.DropLimit / design.Voltage:P0}; Zs {design.Impedance:0.00} Ω{(circuit.Rcd ? string.Empty : $" of {design.ImpedanceLimit:0.00}")}";
        var chosen = design.BreakerChosen || design.CableChosen ? " - as Size Circuits chooses" : string.Empty;
        return $"{breaker}, {cable}; {current}; {rest}{chosen}";
    }

    // ---- the board ----------------------------------------------------------------

    /// <summary>
    /// What a consumer unit is drawn at most, A: in the UK its circuits after the On-Site Guide's
    /// diversity (Table A2) - lighting 66%; heating and power 10 A and half the rest; cooking 10 A and
    /// 30% of the rest; showers the largest and the next whole and a quarter of the rest; immersion
    /// heaters whole; standard socket circuits the largest and 40% of every other. In the US, the
    /// dwelling's load by NEC Article 220 - the lower of the standard and optional calculations.
    /// </summary>
    public static BoardDemand? Demand(BimDocument document, Component board)
    {
        if (TypeOf(document, board) is not { Form: ComponentForm.ConsumerUnit } type) return null;
        using var cache = ModelCache.Begin(document);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var designs = MepSystems.FedBy(document, board, SystemKind.Power).Select(circuit => Design(document, circuit)).OfType<CircuitDesign>().ToList();
        var ways = type.Ways > 0 ? type.Ways : 12;
        var supply = type.SupplyRating > 0 ? type.SupplyRating : 100;
        if (us)
        {
            var (standard, optional, workings) = UsLoad(document);
            return new BoardDemand(board, designs, ways, supply, Math.Min(standard, optional) / UsTwo, workings);
        }

        var lines = new List<string>();
        double Sum(IEnumerable<CircuitDesign> of) => of.Sum(design => design.Current);
        var sockets = designs.Where(design => OfSockets(document, MepSystems.MembersOf(document, design.Circuit))).OrderByDescending(design => design.Breaker).ToList();
        var lighting = designs.Where(design => design.Use == CircuitUse.Lighting && !sockets.Contains(design)).ToList();
        var cooking = designs.Where(design => design.Use == CircuitUse.Cooker && !sockets.Contains(design)).ToList();
        var showers = designs.Where(design => design.Use == CircuitUse.Shower && !sockets.Contains(design)).OrderByDescending(design => design.Current).ToList();
        var immersion = designs.Where(design => design.Use == CircuitUse.ImmersionHeater && !sockets.Contains(design)).ToList();
        var rest = designs.Except(sockets).Except(lighting).Except(cooking).Except(showers).Except(immersion).ToList();

        var demand = 0.0;
        void Add(string what, double amps)
        {
            if (amps <= 0) return;
            demand += amps;
            lines.Add($"{what} {amps:0.#} A");
        }

        Add("lighting at 66%", Sum(lighting) * 0.66);
        var power = Sum(rest);
        Add("heating and power - 10 A and half the rest", Math.Min(power, 10) + Math.Max(power - 10, 0) * 0.5);
        var cookers = cooking.Sum(design => design.Connected / design.Voltage);
        Add("cooking - 10 A and 30% of the rest", CookerCurrent(cookers));
        Add("showers - the largest two and a quarter of the rest", showers.Select((design, i) => design.Current * (i < 2 ? 1 : 0.25)).Sum());
        Add("immersion heaters", Sum(immersion));
        Add("socket circuits - the largest and 40% of the others", sockets.Select((design, i) => design.Breaker * (i == 0 ? 1 : 0.4)).Sum());
        return new BoardDemand(board, designs, ways, supply, demand, lines);
    }

    /// <summary>
    /// A US dwelling's load by NEC Article 220, VA: by the standard method - 3 VA a square foot of floor,
    /// garages and all (220.41, 220.5(C)), 1500 for each small-appliance circuit (two at least) and the
    /// laundry's (220.52), Table 220.45's demand factors; fastened appliances, three-quarters of four or
    /// more of 500 W and over (220.53); dryers at 5000 W or more (220.54); ranges by Table 220.55's
    /// Column C; heating (220.60) - and by the optional method of 220.82: the first 10 kVA of the general
    /// loads whole and 40% of the rest, and the heating whole.
    /// </summary>
    public static (double Standard, double Optional, IReadOnlyList<string> Workings) UsLoad(BimDocument document)
    {
        var lines = new List<string>();
        var feet = MepSystems.DwellingArea(document) * 10.7639;
        var receptacleCircuits = Of(document).Where(circuit => MepSystems.MembersOf(document, circuit).Any(member => IsSocket(document, member))).ToList();
        int Serving(params Place[] places) => receptacleCircuits.Count(circuit =>
            MepSystems.MembersOf(document, circuit).Any(member => IsSocket(document, member) && places.Contains(PlaceOf(document, member))));
        var smallAppliance = Math.Max(2, Serving(Place.Kitchen, Place.Dining));
        var laundry = Math.Max(1, Serving(Place.Laundry));

        var pieces = document.All<Component>().Select(component => (Component: component, Type: TypeOf(document, component))).Where(entry => entry.Type is not null)
            .Select(entry => (entry.Component, Type: entry.Type!)).ToList();
        var ranges = pieces.Where(entry => entry.Type.Form == ComponentForm.Cooker && !entry.Type.BurnsGas).Select(entry => LoadOf(entry.Type)).ToList();
        var ovens = pieces.Where(entry => entry.Type.Form == ComponentForm.OvenHousing).Select(entry => LoadOf(entry.Type)).ToList();
        var dryers = pieces.Where(entry => entry.Type.Form == ComponentForm.TumbleDryer && !entry.Type.BurnsGas).Select(entry => Math.Max(5000, LoadOf(entry.Type))).ToList();
        var heating = pieces.Where(entry => entry.Type.Form == ComponentForm.HeatPump).Sum(entry => LoadOf(entry.Type));
        var fastened = pieces.Where(entry => entry.Type.Form is ComponentForm.HotWaterCylinder or ComponentForm.Dishwasher or ComponentForm.HeatRecoveryUnit
                or ComponentForm.ExtractUnit or ComponentForm.Boiler or ComponentForm.CookerHood or ComponentForm.ExtractFan && !(entry.Type.Form == ComponentForm.HotWaterCylinder && entry.Type.BurnsGas))
            .Select(entry => LoadOf(entry.Type)).ToList();

        // The standard method.
        var general = 3 * feet + 1500.0 * (smallAppliance + laundry);
        var generalDemand = Math.Min(general, 3000) + Math.Clamp(general - 3000, 0, 117000) * 0.35 + Math.Max(general - 120000, 0) * 0.25;
        lines.Add($"lighting {3 * feet:#,0} VA ({feet:#,0} sq ft at 3 VA), {smallAppliance} small-appliance and {laundry} laundry circuit{(laundry == 1 ? string.Empty : "s")} at 1500 VA: {general:#,0} VA, {generalDemand:#,0} after Table 220.45");
        var large = fastened.Where(watts => watts >= 500).ToList();
        var fixedDemand = large.Sum() * (large.Count >= 4 ? 0.75 : 1) + fastened.Where(watts => watts < 500).Sum();
        if (fastened.Count > 0) lines.Add($"fastened appliances {fixedDemand:#,0} VA{(large.Count >= 4 ? " (75% of four or more, 220.53)" : string.Empty)}");
        var rangeDemand = ranges.Count switch
        {
            0 => 0,
            1 => RangeDemand(ranges[0] / 1000),
            _ => new[] { 8, 11, 14, 17, 20, 21, 22, 23, 24, 25 }[Math.Min(ranges.Count, 10) - 1] * 1000.0
        } + ovens.Sum();
        if (rangeDemand > 0) lines.Add($"cooking {rangeDemand:#,0} VA (Table 220.55)");
        if (dryers.Count > 0) lines.Add($"dryer{(dryers.Count == 1 ? string.Empty : "s")} {dryers.Sum():#,0} VA (220.54)");
        if (heating > 0) lines.Add($"heating {heating:#,0} VA (220.60)");
        var standard = generalDemand + fixedDemand + rangeDemand + dryers.Sum() + heating;

        // The optional method.
        var nameplate = general + fastened.Sum() + ranges.Sum() + ovens.Sum() + dryers.Sum();
        var optional = Math.Min(nameplate, 10000) + Math.Max(nameplate - 10000, 0) * 0.4 + heating;
        lines.Add($"standard method {standard:#,0} VA, {standard / UsTwo:0} A; optional method (220.82) {optional:#,0} VA, {optional / UsTwo:0} A");
        return (standard, optional, lines);
    }

    // ---- what is wanting ----------------------------------------------------------

    /// <summary>What is wanting in the circuits and the boards.</summary>
    public static IReadOnlyList<MepProblem> Problems(BimDocument document)
    {
        using var cache = ModelCache.Begin(document);
        return Found(document, null).ToList();
    }

    /// <summary>What is wanting in one circuit itself, without checking the rest.</summary>
    public static IReadOnlyList<MepProblem> Problems(BimDocument document, MepSystem circuit)
    {
        using var cache = ModelCache.Begin(document);
        return Found(document, circuit).Where(problem => ReferenceEquals(problem.Element, circuit)).ToList();
    }

    private static IEnumerable<MepProblem> Found(BimDocument document, MepSystem? only)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var circuits = Of(document);
        foreach (var circuit in circuits.Where(circuit => only is null || ReferenceEquals(circuit, only)))
        {
            if (Design(document, circuit) is not { } design) continue;
            foreach (var fault in Faults(document, design)) yield return new MepProblem(circuit, fault);

            var members = MepSystems.MembersOf(document, circuit);
            if (!us)
            {
                // A 30 mA RCD: on sockets, on a home's lighting, on anything in a bathroom.
                if (!circuit.Rcd)
                {
                    if (members.Any(member => IsSocket(document, member)) && design.Breaker <= 32)
                        yield return new MepProblem(circuit, "its sockets have no RCD - BS 7671 (411.3.3) protects sockets up to 32 A with a 30 mA RCD");
                    else if (members.Any(member => TypeOf(document, member)?.Kind == BuiltInCategory.LightingFixtures))
                        yield return new MepProblem(circuit, "its lights have no RCD - BS 7671 (411.3.4) protects a home's lighting circuits with a 30 mA RCD");
                    if (members.Any(member => InBathroom(document, member)))
                        yield return new MepProblem(circuit, "it serves a bathroom with no RCD - BS 7671 (701.411.3.3) protects every circuit of a room with a bath or shower with a 30 mA RCD");
                }

                continue;
            }

            // The NEC: GFCI, AFCI, and circuits kept to their rooms.
            var receptacles = members.Where(member => IsSocket(document, member)).ToList();
            var places = members.Select(member => PlaceOf(document, member)).ToHashSet();
            if (!circuit.Rcd)
            {
                if (receptacles.Any(member => Wet(document, member)))
                    yield return new MepProblem(circuit, "its receptacles in a kitchen, bathroom, laundry, garage, basement, outdoors or within 6 ft of a sink, tub or shower have no GFCI - NEC 210.8(A)");
                else if (design.Use == CircuitUse.Cooker || members.Any(member => TypeOf(document, member)?.Form is ComponentForm.Cooker or ComponentForm.OvenHousing))
                    yield return new MepProblem(circuit, "its range or oven has no GFCI - NEC 210.8(D) (2023) protects the outlets of ranges, wall ovens and cooktops");
            }

            if (!circuit.ArcFault && design.Voltage < UsTwo && design.Breaker <= 20
                && places.Any(place => place is Place.Kitchen or Place.Dining or Place.Habitable or Place.Hall or Place.Closet or Place.Laundry))
                yield return new MepProblem(circuit, "it has no AFCI - NEC 210.12(B) protects the 120 V, 15 and 20 A circuits of kitchens, living, dining and family rooms, bedrooms, closets, hallways and laundry areas");

            var kitchen = receptacles.Any(member => PlaceOf(document, member) is Place.Kitchen or Place.Dining);
            if (kitchen)
            {
                if (design.Breaker < 20 - 1e-6)
                    yield return new MepProblem(circuit, "its kitchen receptacles are on less than 20 A - NEC 210.11(C)(1) gives them 20 A small-appliance circuits");
                if (members.Any(member => !IsSocket(document, member) || PlaceOf(document, member) is not (Place.Kitchen or Place.Dining)))
                    yield return new MepProblem(circuit, "its small-appliance receptacles share it with other outlets - NEC 210.52(B)(2) has those circuits serve the kitchen's, pantry's and dining room's receptacles alone");
            }

            if (receptacles.Any(member => PlaceOf(document, member) == Place.Laundry))
            {
                if (design.Breaker < 20 - 1e-6) yield return new MepProblem(circuit, "its laundry receptacle is on less than 20 A - NEC 210.11(C)(2) gives the laundry a 20 A circuit");
                if (members.Any(member => PlaceOf(document, member) != Place.Laundry))
                    yield return new MepProblem(circuit, "the laundry's circuit serves other outlets - NEC 210.11(C)(2) gives it no other outlets");
            }

            if (receptacles.Any(member => PlaceOf(document, member) == Place.Bathroom))
            {
                if (design.Breaker < 20 - 1e-6) yield return new MepProblem(circuit, "its bathroom receptacles are on less than 20 A - NEC 210.11(C)(3) gives them a 20 A circuit");
                var rooms = members.Select(member => RoomsOf.Of(document, member)).Distinct().ToList();
                if (places.Any(place => place != Place.Bathroom) || rooms.Count > 1 && members.Any(member => !IsSocket(document, member)))
                    yield return new MepProblem(circuit, "its bathroom circuit serves other outlets - NEC 210.11(C)(3) has it serve bathroom receptacles alone, or one bathroom's outlets");
            }

            if (receptacles.Any(member => PlaceOf(document, member) == Place.Garage) && design.Breaker < 20 - 1e-6)
                yield return new MepProblem(circuit, "its garage receptacles are on less than 20 A - NEC 210.11(C)(4) gives the garage a 20 A circuit");

            // Fixed equipment on a circuit with lights or receptacles: half of it at most.
            var fixedLoad = members.Where(member => TypeOf(document, member) is { } type && type.Kind != BuiltInCategory.LightingFixtures && type.Form is not (ComponentForm.Socket or ComponentForm.Switch))
                .Sum(member => LoadOf(TypeOf(document, member)!));
            if (fixedLoad > 0 && fixedLoad / design.Voltage > design.Breaker * 0.5 + 1e-6
                && members.Any(member => IsSocket(document, member) || TypeOf(document, member)?.Kind == BuiltInCategory.LightingFixtures))
                yield return new MepProblem(circuit, $"its fixed equipment draws {fixedLoad / design.Voltage:0.#} A - NEC 210.23(B)(2) keeps it to half a {design.Breaker:0} A circuit that also has lights or receptacles");
        }

        if (only is not null) yield break;

        if (us)
        {
            // Two small-appliance circuits for the kitchen at least.
            var kitchenCircuits = circuits.Count(circuit => MepSystems.MembersOf(document, circuit).Any(member => IsSocket(document, member) && PlaceOf(document, member) == Place.Kitchen));
            var kitchenReceptacles = document.All<Component>().Where(component => IsSocket(document, component) && PlaceOf(document, component) == Place.Kitchen).ToList();
            if (kitchenReceptacles.Count >= 2 && kitchenCircuits == 1)
                yield return new MepProblem(kitchenReceptacles[0], "the kitchen's receptacles are on one circuit - NEC 210.11(C)(1) and 210.52(B)(3) serve its countertop by two or more 20 A small-appliance circuits");
        }

        // The boards: their ways and their supply.
        foreach (var board in document.All<Component>().Where(component => TypeOf(document, component)?.Form == ComponentForm.ConsumerUnit))
        {
            if (Demand(document, board) is not { } demand || demand.Circuits.Count == 0) continue;
            if (demand.Circuits.Count > demand.Ways)
                yield return new MepProblem(board, $"it has {demand.Circuits.Count} circuits on its {demand.Ways} ways");
            if (us)
            {
                if (demand.Supply < 100 - 1e-6)
                    yield return new MepProblem(board, $"its service is {demand.Supply:0} A - NEC 230.79(C) gives a one-family dwelling 100 A at least");
                if (demand.Demand > demand.Supply + 1e-6)
                    yield return new MepProblem(board, $"the dwelling's load is {demand.Demand:0} A at 240 V by NEC Article 220 - more than its {demand.Supply:0} A service");
            }
            else if (demand.Demand > demand.Supply + 1e-6)
                yield return new MepProblem(board, $"its maximum demand after diversity is {demand.Demand:0} A - more than its {demand.Supply:0} A supply (the On-Site Guide's Table A2)");
        }
    }

    /// <summary>Whether NEC 210.8(A) asks a GFCI of a receptacle: in a kitchen, bathroom, laundry, garage, basement or outdoors, or by water.</summary>
    private static bool Wet(BimDocument document, Component receptacle) =>
        PlaceOf(document, receptacle) is Place.Bathroom or Place.Garage or Place.Outdoors or Place.Basement or Place.Kitchen or Place.Laundry || NearWater(document, receptacle);

    /// <summary>
    /// In the US, the protection a new circuit is given: a GFCI where NEC 210.8 asks one - receptacles
    /// where there is water, a range or oven - and an AFCI on a 120 V circuit of the rooms 210.12(B) names.
    /// </summary>
    public static (bool Gfci, bool Afci) UsProtection(BimDocument document, MepSystem circuit)
    {
        var members = MepSystems.MembersOf(document, circuit);
        var gfci = members.Any(member => IsSocket(document, member) && Wet(document, member))
                   || circuit.Use == CircuitUse.Cooker || members.Any(member => TypeOf(document, member)?.Form is ComponentForm.Cooker or ComponentForm.OvenHousing);
        var afci = VoltageOf(document, circuit) < UsTwo
                   && members.Any(member => PlaceOf(document, member) is Place.Kitchen or Place.Dining or Place.Habitable or Place.Hall or Place.Closet or Place.Laundry);
        return (gfci, afci);
    }

    /// <summary>Whether a receptacle is within 6 ft of a sink, basin, bath or shower (NEC 210.8(A)(8), (10)).</summary>
    private static bool NearWater(BimDocument document, Component receptacle) =>
        document.All<Component>().Any(other => other.LevelId == receptacle.LevelId && TypeOf(document, other) is { Form: ComponentForm.Sink or ComponentForm.Basin or ComponentForm.Bath or ComponentForm.Shower } type
                                               && Views.ComponentModel.Footprint(type, Views.ComponentModel.FrameOf(other, 0)) is { Count: >= 3 } outline
                                               && (Polygon2D.Contains(outline, receptacle.Location)
                                                   || outline.Select((point, i) => Line2D.DistanceFromSegment(receptacle.Location, point, outline[(i + 1) % outline.Count])).Min() <= 1829));

    // ---- what it costs --------------------------------------------------------------

    /// <summary>
    /// What a circuit's cable costs a metre and its breaker each - indicative trade prices: twin and
    /// earth or NM-B by its size; an MCB or an RCBO, an AFDD on it; in the US a breaker, GFCI, AFCI or both.
    /// </summary>
    public static (decimal Cable, decimal Breaker) Rates(bool us, CircuitDesign design)
    {
        var sizes = Sizes(us);
        var index = Math.Max(0, sizes.ToList().FindIndex(size => size.Name == design.Cable.Name));
        var cable = us
            ? new[] { 2.10m, 3.10m, 5.25m, 8.50m, 13.80m, 19.70m }[Math.Min(index, 5)]
            : new[] { 0.90m, 1.10m, 1.60m, 2.60m, 3.80m, 6.40m, 9.80m }[Math.Min(index, 6)];
        var breaker = us
            ? design.Circuit.Rcd && design.Circuit.ArcFault ? 55m : design.Circuit.Rcd || design.Circuit.ArcFault ? 45m : 12m
            : (design.Circuit.Rcd ? 32m : 8m) + (design.Circuit.ArcFault ? 60m : 0m);
        return (cable, breaker);
    }

    // ---- Size Circuits -------------------------------------------------------------

    /// <summary>Each circuit's breaker and cable as Size Circuits chooses them, where that differs from what it has set.</summary>
    public static IReadOnlyList<(MepSystem Circuit, double Breaker, string Cable)> Sized(BimDocument document, IEnumerable<MepSystem> circuits)
    {
        var sized = new List<(MepSystem, double, string)>();
        foreach (var circuit in circuits.Where(circuit => circuit.Kind == SystemKind.Power && MepSystems.MembersOf(document, circuit).Count > 0))
        {
            var chosen = Choose(document, circuit);
            if (Math.Abs(chosen.Breaker - circuit.Breaker) > 1e-6 || chosen.Cable.Name != circuit.Cable)
                sized.Add((circuit, chosen.Breaker, chosen.Cable.Name));
        }

        return sized;
    }
}

/// <summary>Circuits' breakers and cables set as Size Circuits chooses them, as one step to undo.</summary>
public sealed class SetCircuitsCommand : IUndoableCommand
{
    private readonly IReadOnlyList<(MepSystem Circuit, double Breaker, string Cable, double NewBreaker, string NewCable)> _changes;

    public SetCircuitsCommand(IEnumerable<(MepSystem Circuit, double Breaker, string Cable)> sized)
    {
        _changes = sized.Select(entry => (entry.Circuit, entry.Circuit.Breaker, entry.Circuit.Cable, entry.Breaker, entry.Cable)).ToList();
    }

    public int Count => _changes.Count;

    public string Name => "Size Circuits";

    public void Redo()
    {
        foreach (var (circuit, _, _, breaker, cable) in _changes) (circuit.Breaker, circuit.Cable) = (breaker, cable);
    }

    public void Undo()
    {
        foreach (var (circuit, breaker, cable, _, _) in _changes) (circuit.Breaker, circuit.Cable) = (breaker, cable);
    }
}
