using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Energy;

/// <summary>Where the home is for Approved Document O: found from its postcode, or set.</summary>
public enum OverheatingLocation
{
    ByPostcode,
    Moderate,
    High
}

/// <summary>
/// An opening of the home as Part O counts it: its glazing, m² - its glass, not its frame - and the
/// equivalent area it opens to by day and at night, m², the way it faces (degrees clockwise from true
/// north; none for a rooflight on the level), and what limits it.
/// </summary>
public sealed record OverheatingOpening(Element Element, Room Room, string Name, double Glazing, double Equivalent, double NightEquivalent, double? Bearing, string Note);

/// <summary>A room as Part O counts it: its floor area - no deeper than 4.5 m from its glazing where it serves more than one use - its glazing and the way most of it faces, and what it opens to at night.</summary>
public sealed record OverheatingRoom(Room Room, string Name, double FloorArea, double Glazing, string Facade, bool Bedroom, double NightFree)
{
    public double Share => FloorArea > 0 ? Glazing / FloorArea : 0;
}

/// <summary>
/// The home checked by Approved Document O's simplified method: where it is and whether it is
/// cross-ventilated, its glazing against Table 1.1 or 1.2, its shading where the risk is high, and
/// what it opens to against Table 1.3 or 1.4 - each of its rooms and openings as counted.
/// </summary>
public sealed record OverheatingAssessment(
    bool HighRisk, string Location, bool CrossVentilated, string CrossVentilation, double FloorArea, double Glazing, string Facade,
    double TotalFree, double TotalFreeRequired, IReadOnlyList<OverheatingRoom> Rooms, IReadOnlyList<OverheatingOpening> Openings, IReadOnlyList<FhsCheck> Checks)
{
    public bool Complies => Checks.All(check => check.Met);
}

/// <summary>
/// Requirement O1 by Approved Document O's simplified method (2021 edition, in force from 15 June
/// 2022): the home's risk category by its location - the high risk part of London by its postcode
/// (Table C1), central Manchester taken with it as paragraph C3 advises, England else moderate - and
/// whether it can be cross-ventilated, through openings on opposite façades (1.4).
///
/// Its glazing - the glass, not the frames - as a share of its floor area, and the most glazed room's
/// of its own, against Table 1.1 or 1.2 for the façade with the most glazing (1.6): a room serving more
/// than one use taken no deeper than 4.5 m from its glazing (Appendix A). In the high risk location,
/// glazing facing from north-east to north-west through south shaded (1.9): by shutters, by glass of
/// g 0.4 at most with a light transmittance of 0.7 at least, or - facing due south - by an overhang
/// cutting off the sun above 50°.
///
/// What it opens to, by the equivalent area of each opening light (Appendix D, Tables D1 to D9: a
/// side-hung light by its height and width, a top- or bottom-hung one the other way round, a sash or a
/// slider half opened at 90°, a pivot roof window as its two halves), against Table 1.3 or 1.4's total
/// and each bedroom's. A light opening more than 100 mm above the ground floor counts only where it is
/// guarded to 1.1 m - its opening at least 1.1 m up, or a railing across it - and is taken at 100 mm
/// otherwise (3.9, Table 3.1); a ground floor bedroom's only as far as it can be left open securely,
/// 100 mm on a lockable restrictor, unless it has lockable shutters or grilles (3.6, 3.7). Where noise
/// will keep the windows shut at night, the simplified method cannot be used (3.1, 3.2).
///
/// Taken where the model does not say: side-hung lights open to 90°, top-hung to 30°, bottom-hung to
/// 15°, a pivot roof window to 20°; glazed doors' glass a share of the door by their design.
/// </summary>
public static class Overheating
{
    /// <summary>The high risk part of London by postcode district (Table C1).</summary>
    public static readonly IReadOnlySet<string> HighRiskPostcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CR4", "CR7", "E1", "E10", "E11", "E12", "E13", "E14", "E15", "E16", "E17", "E18", "E1W", "E2", 
        "E20", "E3", "E4", "E5", "E6", "E7", "E8", "E9", "EC1A", "EC1M", "EC1N", "EC1R", "EC1V", "EC1Y", 
        "EC2A", "EC2M", "EC2N", "EC2P", "EC2R", "EC2V", "EC2Y", "EC3A", "EC3M", "EC3N", "EC3R", "EC3V", "EC4A", "EC4M", 
        "EC4N", "EC4R", "EC4V", "EC4Y", "HA0", "HA9", "IG1", "IG11", "IG2", "IG3", "IG4", "KT1", "KT2", "KT3", 
        "KT4", "KT5", "KT6", "KT7", "KT8", "N1", "N13", "N15", "N16", "N17", "N18", "N19", "N1C", "N2", 
        "N22", "N4", "N5", "N6", "N7", "N8", "N9", "NW1", "NW10", "NW11", "NW2", "NW3", "NW5", "NW6", 
        "NW8", "RM10", "RM8", "RM9", "SE1", "SE10", "SE11", "SE12", "SE13", "SE14", "SE15", "SE16", "SE17", "SE18", 
        "SE19", "SE2", "SE20", "SE21", "SE22", "SE23", "SE24", "SE25", "SE26", "SE27", "SE28", "SE3", "SE4", "SE5", 
        "SE6", "SE7", "SE8", "SE9", "SW10", "SW11", "SW12", "SW13", "SW14", "SW15", "SW16", "SW17", "SW18", "SW19", 
        "SW1A", "SW1E", "SW1H", "SW1P", "SW1V", "SW1W", "SW1X", "SW1Y", "SW2", "SW20", "SW3", "SW4", "SW5", "SW6", 
        "SW7", "SW8", "SW9", "TW1", "TW10", "TW11", "TW12", "TW13", "TW14", "TW15", "TW19", "TW2", "TW3", "TW4", 
        "TW5", "TW6", "TW7", "TW8", "TW9", "UB1", "UB11", "UB2", "UB3", "UB4", "UB5", "UB6", "UB7", "UB8", 
        "W10", "W11", "W12", "W13", "W14", "W1B", "W1C", "W1D", "W1F", "W1G", "W1H", "W1J", "W1K", "W1S", 
        "W1T", "W1U", "W1W", "W2", "W3", "W4", "W5", "W6", "W7", "W8", "W9", "WC1A", "WC1B", "WC1E", 
        "WC1H", "WC1N", "WC1R", "WC1V", "WC1X", "WC2A", "WC2B", "WC2E", "WC2H", "WC2N", "WC2R", 
    };

    /// <summary>Central Manchester, where paragraph C3 advises following the high risk location's guidance.</summary>
    public static readonly IReadOnlySet<string> ManchesterPostcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "M1", "M2", "M3", "M5", "M15", "M16", "M50" };

    // Tables 1.1 and 1.2: the most glazing, % of the floor area and % of the most glazed room's, by the façade with the most glazing - north, east, south, west.
    private static readonly (double Home, double Room)[] CrossHigh = { (15, 37), (18, 37), (15, 22), (18, 37) };
    private static readonly (double Home, double Room)[] CrossModerate = { (18, 37), (18, 37), (15, 30), (11, 22) };
    private static readonly (double Home, double Room)[] ClosedHigh = { (15, 26), (11, 18), (11, 11), (11, 18) };
    private static readonly (double Home, double Room)[] ClosedModerate = { (18, 26), (18, 26), (15, 15), (11, 11) };

    private static readonly string[] Facades = { "north", "east", "south", "west" };

    /// <summary>The most glazing, % of the floor area and of the most glazed room's, for a façade (Tables 1.1 and 1.2).</summary>
    public static (double Home, double Room) GlazingLimit(bool high, bool cross, string facade)
    {
        var table = (high, cross) switch { (true, true) => CrossHigh, (false, true) => CrossModerate, (true, false) => ClosedHigh, _ => ClosedModerate };
        return table[Math.Max(0, Array.IndexOf(Facades, facade))];
    }

    /// <summary>The least free area to remove heat (Tables 1.3 and 1.4): % of the floor area, % of the glazing, the greater; and % of a bedroom's floor.</summary>
    public static (double Floor, double Glazing, double Bedroom) FreeAreaLimit(bool high, bool cross) => (high, cross) switch
    {
        (true, true) => (6, 70, 13),
        (false, true) => (9, 55, 4),
        (true, false) => (10, 95, 13),
        _ => (12, 80, 4)
    };

    // Tables D1 to D9: the equivalent area of a window, m², opened 10° to 90° in tens; by its opening height h from 0.5 to 3 m in quarters, and width w from 0.5 to 1.5 m in quarters.
    private static readonly double[][][] Equivalents =
    {
        new[]
        {
            new[] { 0.08, 0.14, 0.18, 0.28, 0.34 },
            new[] { 0.11, 0.17, 0.27, 0.34, 0.41 },
            new[] { 0.14, 0.23, 0.30, 0.45, 0.54 },
            new[] { 0.18, 0.28, 0.38, 0.47, 0.68 },
            new[] { 0.21, 0.32, 0.45, 0.57, 0.68 },
            new[] { 0.25, 0.37, 0.53, 0.66, 0.79 },
            new[] { 0.28, 0.42, 0.56, 0.75, 0.91 },
            new[] { 0.32, 0.47, 0.63, 0.85, 1.02 },
            new[] { 0.35, 0.53, 0.70, 0.88, 1.13 },
            new[] { 0.39, 0.58, 0.77, 0.96, 1.25 },
            new[] { 0.42, 0.63, 0.84, 1.05, 1.26 },
        },
        new[]
        {
            new[] { 0.13, 0.22, 0.29, 0.43, 0.52 },
            new[] { 0.19, 0.28, 0.44, 0.55, 0.66 },
            new[] { 0.24, 0.38, 0.50, 0.73, 0.88 },
            new[] { 0.29, 0.47, 0.63, 0.79, 1.10 },
            new[] { 0.35, 0.53, 0.76, 0.94, 1.13 },
            new[] { 0.41, 0.62, 0.88, 1.10, 1.32 },
            new[] { 0.47, 0.71, 0.94, 1.26, 1.51 },
            new[] { 0.53, 0.80, 1.06, 1.42, 1.70 },
            new[] { 0.59, 0.88, 1.18, 1.47, 1.89 },
            new[] { 0.65, 0.97, 1.30, 1.62, 2.08 },
            new[] { 0.71, 1.06, 1.42, 1.77, 2.12 },
        },
        new[]
        {
            new[] { 0.16, 0.27, 0.36, 0.52, 0.62 },
            new[] { 0.24, 0.36, 0.54, 0.68, 0.81 },
            new[] { 0.30, 0.48, 0.64, 0.90, 1.08 },
            new[] { 0.38, 0.60, 0.80, 1.00, 1.36 },
            new[] { 0.45, 0.68, 0.96, 1.20, 1.43 },
            new[] { 0.53, 0.79, 1.12, 1.40, 1.67 },
            new[] { 0.60, 0.90, 1.21, 1.59, 1.91 },
            new[] { 0.68, 1.02, 1.36, 1.79, 2.15 },
            new[] { 0.75, 1.13, 1.51, 1.88, 2.39 },
            new[] { 0.83, 1.24, 1.66, 2.07, 2.63 },
            new[] { 0.90, 1.36, 1.81, 2.26, 2.71 },
        },
        new[]
        {
            new[] { 0.18, 0.30, 0.40, 0.56, 0.67 },
            new[] { 0.27, 0.41, 0.61, 0.76, 0.91 },
            new[] { 0.35, 0.55, 0.73, 1.01, 1.21 },
            new[] { 0.43, 0.68, 0.91, 1.14, 1.52 },
            new[] { 0.52, 0.78, 1.09, 1.36, 1.64 },
            new[] { 0.61, 0.91, 1.27, 1.59, 1.91 },
            new[] { 0.69, 1.04, 1.38, 1.82, 2.18 },
            new[] { 0.78, 1.17, 1.56, 2.05, 2.46 },
            new[] { 0.86, 1.30, 1.73, 2.16, 2.73 },
            new[] { 0.95, 1.43, 1.90, 2.38, 3.00 },
            new[] { 1.04, 1.56, 2.08, 2.59, 3.11 },
        },
        new[]
        {
            new[] { 0.20, 0.32, 0.43, 0.59, 0.70 },
            new[] { 0.30, 0.44, 0.65, 0.81, 0.97 },
            new[] { 0.38, 0.59, 0.79, 1.08, 1.29 },
            new[] { 0.47, 0.74, 0.98, 1.23, 1.62 },
            new[] { 0.56, 0.85, 1.18, 1.48, 1.77 },
            new[] { 0.66, 0.99, 1.38, 1.72, 2.07 },
            new[] { 0.75, 1.13, 1.51, 1.97, 2.36 },
            new[] { 0.85, 1.27, 1.69, 2.21, 2.66 },
            new[] { 0.94, 1.41, 1.88, 2.35, 2.95 },
            new[] { 1.04, 1.55, 2.07, 2.59, 3.25 },
            new[] { 1.13, 1.69, 2.26, 2.82, 3.39 },
        },
        new[]
        {
            new[] { 0.21, 0.34, 0.45, 0.60, 0.72 },
            new[] { 0.31, 0.47, 0.67, 0.84, 1.01 },
            new[] { 0.40, 0.62, 0.83, 1.12, 1.34 },
            new[] { 0.50, 0.78, 1.03, 1.29, 1.68 },
            new[] { 0.60, 0.89, 1.24, 1.55, 1.86 },
            new[] { 0.70, 1.04, 1.45, 1.81, 2.17 },
            new[] { 0.79, 1.19, 1.59, 2.07, 2.48 },
            new[] { 0.89, 1.34, 1.79, 2.33, 2.79 },
            new[] { 0.99, 1.49, 1.99, 2.48, 3.10 },
            new[] { 1.09, 1.64, 2.19, 2.73, 3.41 },
            new[] { 1.19, 1.79, 2.38, 2.98, 3.58 },
        },
        new[]
        {
            new[] { 0.21, 0.34, 0.46, 0.61, 0.73 },
            new[] { 0.32, 0.48, 0.69, 0.86, 1.03 },
            new[] { 0.41, 0.64, 0.85, 1.15, 1.37 },
            new[] { 0.51, 0.80, 1.07, 1.33, 1.72 },
            new[] { 0.62, 0.93, 1.28, 1.60, 1.92 },
            new[] { 0.72, 1.08, 1.49, 1.87, 2.24 },
            new[] { 0.82, 1.23, 1.65, 2.13, 2.56 },
            new[] { 0.93, 1.39, 1.85, 2.40, 2.88 },
            new[] { 1.03, 1.54, 2.06, 2.57, 3.20 },
            new[] { 1.13, 1.70, 2.26, 2.83, 3.52 },
            new[] { 1.23, 1.85, 2.47, 3.09, 3.70 },
        },
        new[]
        {
            new[] { 0.22, 0.35, 0.46, 0.61, 0.73 },
            new[] { 0.33, 0.49, 0.70, 0.87, 1.04 },
            new[] { 0.42, 0.65, 0.87, 1.16, 1.39 },
            new[] { 0.53, 0.82, 1.09, 1.36, 1.74 },
            new[] { 0.63, 0.95, 1.31, 1.63, 1.96 },
            new[] { 0.74, 1.11, 1.53, 1.91, 2.29 },
            new[] { 0.84, 1.26, 1.68, 2.18, 2.61 },
            new[] { 0.95, 1.42, 1.90, 2.45, 2.94 },
            new[] { 1.05, 1.58, 2.11, 2.63, 3.27 },
            new[] { 1.16, 1.74, 2.32, 2.90, 3.60 },
            new[] { 1.26, 1.90, 2.53, 3.16, 3.79 },
        },
        new[]
        {
            new[] { 0.22, 0.35, 0.47, 0.61, 0.74 },
            new[] { 0.33, 0.50, 0.70, 0.88, 1.05 },
            new[] { 0.43, 0.66, 0.88, 1.17, 1.40 },
            new[] { 0.53, 0.83, 1.10, 1.38, 1.76 },
            new[] { 0.64, 0.96, 1.33, 1.66, 1.99 },
            new[] { 0.75, 1.12, 1.55, 1.93, 2.32 },
            new[] { 0.86, 1.28, 1.71, 2.21, 2.65 },
            new[] { 0.96, 1.44, 1.93, 2.48, 2.98 },
            new[] { 1.07, 1.60, 2.14, 2.67, 3.31 },
            new[] { 1.18, 1.76, 2.35, 2.94, 3.64 },
            new[] { 1.28, 1.93, 2.57, 3.21, 3.85 },
        }
    };

    /// <summary>
    /// The equivalent area of an opening light, m² (Appendix D): its hinge's length and its width
    /// across, m, opened so many degrees - read off Tables D1 to D9 between their rows, columns and
    /// angles, and beyond them in proportion; under 10° in proportion to the angle.
    /// </summary>
    public static double EquivalentArea(double hinge, double across, double angle)
    {
        if (hinge <= 0 || across <= 0 || angle <= 0) return 0;
        var h = Math.Clamp(hinge, 0.5, 3);
        var w = Math.Clamp(across, 0.5, 1.5);
        var scale = hinge / h * (across / w);

        double At(int table)
        {
            var (row, column) = ((h - 0.5) / 0.25, (w - 0.5) / 0.25);
            var (r, c) = (Math.Min((int)row, 9), Math.Min((int)column, 3));
            var (tr, tc) = (row - r, column - c);
            var values = Equivalents[table];
            var low = values[r][c] + (values[r][c + 1] - values[r][c]) * tc;
            var high = values[r + 1][c] + (values[r + 1][c + 1] - values[r + 1][c]) * tc;
            return low + (high - low) * tr;
        }

        var a = Math.Min(angle, 90);
        if (a <= 10) return At(0) * a / 10 * scale;
        var below = Math.Min((int)(a / 10) - 1, 7);
        return (At(below) + (At(below + 1) - At(below)) * (a - (below + 1) * 10) / 10) * scale;
    }

    /// <summary>How far a light hung on one edge opens for its far edge to move 100 mm, degrees: a window on a restrictor.</summary>
    public static double RestrictedAngle(double across) => 2 * Math.Asin(Math.Min(1, 0.1 / (2 * across))) * 180 / Math.PI;

    /// <summary>The district of a UK postcode in a piece of text - "SW1A" of "10 Downing Street, London SW1A 2AA" - or a district on its own.</summary>
    public static string? District(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var full = System.Text.RegularExpressions.Regex.Match(text, @"\b([A-Z]{1,2}[0-9][A-Z0-9]?)\s*[0-9][A-Z]{2}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (full.Success) return full.Groups[1].Value.ToUpperInvariant();
        var alone = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^([A-Z]{1,2}[0-9][A-Z0-9]?)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return alone.Success ? alone.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Whether the home is in the high risk location (1.3), and how that was found.</summary>
    public static (bool High, string Said) LocationOf(BimDocument document)
    {
        var settings = document.ProjectInformation.Energy;
        if (settings.OverheatingLocation == OverheatingLocation.High) return (true, "in the high risk location, as set");
        if (settings.OverheatingLocation == OverheatingLocation.Moderate) return (false, "in a moderate risk location, as set");
        var district = District(settings.Postcode) ?? District(document.ProjectInformation.Address);
        if (district is null) return (false, "taken as in a moderate risk location: no postcode is given, here or in the project's address");
        if (HighRiskPostcodes.Contains(district)) return (true, $"in the high risk location: {district} is in Table C1");
        if (ManchesterPostcodes.Contains(district)) return (true, $"taken as in the high risk location: {district} is central Manchester, where paragraph C3 advises it");
        return (false, $"in a moderate risk location: {district} is not in Table C1");
    }

    /// <summary>The façade a way of facing is on: north from north-west to north-east, and so on round.</summary>
    private static string FacadeOf(double? bearing) => bearing is { } way ? Facades[(int)Math.Floor(((way + 45) % 360 + 360) % 360 / 90) % 4] : "south";

    /// <summary>Whether a way of facing is between north-east and north-west via the south, where the high risk location wants its glazing shaded (1.9).</summary>
    private static bool Sunny(double? bearing) => bearing is not { } way || way is >= 45 and <= 315;

    /// <summary>An opening's lights as Appendix D takes them: each light's hinge length and width across, m, how far it opens, and whether it slides - up, or across.</summary>
    private static IEnumerable<(double Hinge, double Across, double Angle, int Slides)> Lights(BimDocument document, Element element)
    {
        switch (element)
        {
            case Window window when document.FindType<WindowType>(window.TypeId) is { } type:
            {
                var (sx, sy) = (window.WidthOf(type) / Math.Max(type.Width, 1), window.HeightOf(type) / Math.Max(type.Height, 1));
                foreach (var light in WindowLights.Of(type).Where(light => light.Opens))
                {
                    var (w, h) = (light.Width * sx / 1000, light.Height * sy / 1000);
                    var operation = light.Fanlight ? WindowOperation.Awning : light.Operation;
                    switch (operation)
                    {
                        case WindowOperation.Casement or WindowOperation.TiltAndTurn or WindowOperation.Bay or WindowOperation.Louvred:
                            yield return (h, w, 90, 0);
                            break;
                        case WindowOperation.Awning:
                            yield return (w, h, 30, 0);
                            break;
                        case WindowOperation.Hopper:
                            yield return (w, h, 15, 0);
                            break;
                        case WindowOperation.DoubleHung:
                            yield return (h / 2, w, 90, 1);
                            break;
                        case WindowOperation.Sliding:
                            yield return (h, w / 2, 90, 2);
                            break;
                    }
                }

                break;
            }

            case Door door when document.FindType<DoorType>(door.TypeId) is { } type && GlazedShare(type) > 0:
            {
                var (w, h) = (door.WidthOf(type) / 1000, door.HeightOf(type) / 1000);
                switch (type.Operation)
                {
                    case DoorOperation.Swing or DoorOperation.DoubleSwing:
                        var leaves = Math.Max(1, type.LeafCount);
                        for (var i = 0; i < leaves; i++) yield return (h, w / leaves, 90, 0);
                        break;
                    case DoorOperation.Sliding:
                        yield return (h, w / 2, 90, 2);
                        break;
                    case DoorOperation.Folding:
                        yield return (h, w, 90, 2);
                        break;
                }

                break;
            }

            case RoofWindow roofWindow when document.FindType<RoofWindowType>(roofWindow.TypeId) is { } type:
            {
                var built = roofWindow.Built(type);
                var (w, h) = ((built.Width - 2 * built.FrameWidth) / 1000, (built.Height - 2 * built.FrameWidth) / 1000);
                if (built.Operation == RoofWindowOperation.CentrePivot)
                {
                    // Each half as a window hung along the pivot (D3 b).
                    yield return (w, h / 2, 20, 0);
                    yield return (w, h / 2, 20, 0);
                }
                else if (built.Operation == RoofWindowOperation.TopHung) yield return (w, h, 30, 0);

                break;
            }
        }
    }

    /// <summary>How much of a door is glass, by its design: none for a door under 30% glazed, as SAP and Part O count doors.</summary>
    private static double GlazedShare(DoorType type) => type.LeafDesign switch
    {
        DoorLeafDesign.FullGlass => 0.8,
        DoorLeafDesign.FrenchGlazed => 0.65,
        DoorLeafDesign.Glazed => 0.6,
        DoorLeafDesign.HalfGlazed => 0.35,
        _ => 0
    };

    /// <summary>An opening's glass, m²: a window's lights inside their frames, a door's glazed share, a roof window's pane, curtain walling less its framing.</summary>
    private static double GlassOf(BimDocument document, HeatLossLine line) => line.Through switch
    {
        Window window when document.FindType<WindowType>(window.TypeId) is { } type =>
            WindowLights.Of(type).Sum(light => light.Area) / 1e6 * (window.WidthOf(type) / Math.Max(type.Width, 1)) * (window.HeightOf(type) / Math.Max(type.Height, 1)),
        Door door when document.FindType<DoorType>(door.TypeId) is { } type => line.Area * GlazedShare(type),
        RoofWindow roofWindow when document.FindType<RoofWindowType>(roofWindow.TypeId) is { } type && roofWindow.Built(type) is var built =>
            (built.Width - 2 * built.FrameWidth) * (built.Height - 2 * built.FrameWidth) / 1e6,
        _ => line.Area * 0.85
    };

    /// <summary>Whether an opening light is guarded so it may open more than 100 mm above the ground floor (3.9, Table 3.1): its lowest opening 1.1 m up, or a railing of 1.1 m across it.</summary>
    private static bool Guarded(BimDocument document, Element element)
    {
        if (element is RoofWindow) return true;
        if (element is not Opening opening || document.Find<Wall>(opening.HostWallId) is not { } wall || document.FindType<OpeningType>(opening.TypeId) is not { } type) return false;
        var lowest = type is WindowType window ? WindowLights.LowestOpening(window) ?? 0 : 0;
        if (opening.SillHeight + lowest >= 1100) return true;
        var centre = opening.GetCentre(wall);
        return document.All<Railing>().Any(railing => railing.LevelId == wall.LevelId
            && document.FindType<RailingType>(railing.TypeId) is { Height: >= 1100 }
            && railing.Path.Zip(railing.Path.Skip(1), (a, b) => Line2D.DistanceFromSegment(centre, a, b)).DefaultIfEmpty(double.MaxValue).Min() <= 600);
    }

    private static bool Bedroom(BimDocument document, Room room, RoomBoundaryResult boundary) =>
        room.Name.Contains("bed", StringComparison.OrdinalIgnoreCase) || Bedrooms.Of(document, room, boundary.Polygon) is not null;

    /// <summary>Whether a room serves more than one use - an open-plan kitchen and living room - so it is taken no deeper than 4.5 m from its glazing (Appendix A).</summary>
    private static bool OpenPlan(Room room)
    {
        var uses = new[] { "kitchen", "living", "dining", "lounge", "family", "study", "sitting" };
        return room.Name.Contains("open plan", StringComparison.OrdinalIgnoreCase) || uses.Count(use => room.Name.Contains(use, StringComparison.OrdinalIgnoreCase)) >= 2;
    }

    /// <summary>The length of a room's outside walls on each façade, m.</summary>
    private static Dictionary<string, double> OutsideWalls(BimDocument document, Room room, RoomBoundaryResult boundary, double north)
    {
        var lengths = new Dictionary<string, double>();
        var polygon = boundary.Polygon;
        var turn = Polygon2D.SignedArea(polygon) >= 0 ? 1 : -1;
        for (var i = 0; i < polygon.Count && i < boundary.Sides.Count; i++)
        {
            if (boundary.Sides[i] is not { } wall) continue;
            var outside = document.FindType<CurtainWallType>(wall.TypeId) is not null || document.GetWallType(wall)?.Function == WallFunction.Exterior;
            var (from, to) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            var length = from.DistanceTo(to);
            if (!outside || length < 1) continue;
            var along = (to - from) * (1 / length);
            var outward = new Vector2D(along.Y, -along.X) * turn;
            var facade = FacadeOf(((Math.Atan2(outward.X, outward.Y) * 180 / Math.PI - north) % 360 + 360) % 360);
            lengths[facade] = lengths.GetValueOrDefault(facade) + length / 1000;
        }

        return lengths;
    }

    /// <summary>The home checked by the simplified method; null for a US project, or where no heated room is enclosed.</summary>
    public static OverheatingAssessment? Of(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) return null;
        var building = HeatLoss.Surveyed(document);
        var heated = building?.Rooms.Where(room => room.Heated && room.Area > 0).ToList() ?? new List<RoomHeatLoss>();
        if (heated.Count == 0) return null;

        using var cache = ModelCache.Begin(document);
        var settings = document.ProjectInformation.Energy;
        var north = document.ProjectInformation.TrueNorth;
        var (high, location) = LocationOf(document);
        var lowest = document.Levels.Where(level => heated.Any(room => room.Room.LevelId == level.Id)).OrderBy(level => level.Elevation).First().Id;

        // Each opening: its glass, and what it opens to by day and at night.
        var openings = new List<OverheatingOpening>();
        var rooms = new List<(RoomHeatLoss Room, RoomBoundaryResult Boundary, bool Bedroom, Dictionary<string, double> Glazing)>();
        foreach (var room in heated)
        {
            var boundary = room.Room.GetBoundary(document);
            var ground = room.Room.LevelId == lowest;
            var bedroom = Bedroom(document, room.Room, boundary);
            var glazing = new Dictionary<string, double>();
            foreach (var line in room.Lines.Where(line => line.Path == HeatPath.Outside && line.Kind is HeatSurface.Window or HeatSurface.Door && line.Through is not null))
            {
                double? bearing = line.Facing is { } facing && line.Tilt is not < 1 ? ((facing - north) % 360 + 360) % 360 : null;
                var glass = line.Kind == HeatSurface.Window ? GlassOf(document, line) : 0;
                if (glass > 0) glazing[FacadeOf(bearing)] = glazing.GetValueOrDefault(FacadeOf(bearing)) + glass;

                var lights = Lights(document, line.Through!).ToList();
                if (lights.Count == 0)
                {
                    if (glass > 0) openings.Add(new OverheatingOpening(line.Through!, room.Room, line.What, glass, 0, 0, bearing, "does not open"));
                    continue;
                }

                // Above the ground floor, opening further than 100 mm only where guarded (3.9); a ground floor bedroom at night only as far as is secure (3.6).
                var guarded = ground || Guarded(document, line.Through!);
                var door = line.Through is Door;
                double Full((double Hinge, double Across, double Angle, int Slides) light) => EquivalentArea(light.Hinge, light.Across, light.Angle);
                double Restricted((double Hinge, double Across, double Angle, int Slides) light) => light.Slides switch
                {
                    1 => EquivalentArea(0.1, light.Across, 90),
                    2 => EquivalentArea(light.Hinge, 0.1, 90),
                    _ => EquivalentArea(light.Hinge, light.Across, Math.Min(light.Angle, RestrictedAngle(light.Across)))
                };
                var day = lights.Sum(light => guarded ? Full(light) : Restricted(light));
                var night = bedroom && ground && !settings.Shutters
                    ? door ? 0 : lights.Sum(Restricted)
                    : day;
                var note = !guarded ? "taken open 100 mm: no guarding to 1.1 m in front of it (3.9, Table 3.1)"
                    : bedroom && ground && !settings.Shutters ? door ? "open by day; shut at night, a ground floor bedroom's door not secure open (3.6)" : "open 100 mm at night on a lockable restrictor, a ground floor bedroom's (3.6)"
                    : "opens fully";
                openings.Add(new OverheatingOpening(line.Through!, room.Room, line.What, glass, day, night, bearing, note));
            }

            rooms.Add((room, boundary, bedroom, glazing));
        }

        // The floor area to the inside of its outside walls (Appendix A), and its glass by façade.
        var floorArea = heated.Sum(room => room.Area) + SapSurvey.Partitions(document, heated);
        var byFacade = Facades.ToDictionary(facade => facade, facade => rooms.Sum(room => room.Glazing.GetValueOrDefault(facade)));
        var glazingTotal = byFacade.Values.Sum();

        // Cross-ventilated: opening lights on opposite façades (1.4).
        var opens = openings.Where(opening => opening.Equivalent > 0 && opening.Bearing is not null).Select(opening => opening.Bearing!.Value).ToList();
        var cross = opens.Any(a => opens.Any(b => Math.Abs(((a - b) % 360 + 540) % 360 - 180) <= 45));

        // The façade with the most glazing - of two with as much, the one allowed less.
        string MostGlazed(IReadOnlyDictionary<string, double> glazing, bool room) => glazing.Count == 0 ? "north"
            : glazing.OrderByDescending(pair => Math.Round(pair.Value, 2)).ThenBy(pair => room ? GlazingLimit(high, cross, pair.Key).Room : GlazingLimit(high, cross, pair.Key).Home).First().Key;
        var homeFacade = MostGlazed(byFacade.Where(pair => pair.Value > 0).ToDictionary(), false);
        var crossSaid = cross ? "it opens on opposite façades, so it can be cross-ventilated (1.4)" : "it does not open on opposite façades, so it is taken as not cross-ventilated (1.4)";

        // Each room: its glazing on its most glazed façade, and its floor - an open-plan room no deeper than 4.5 m from that glazing.
        var counted = rooms.Select(entry =>
        {
            var facade = MostGlazed(entry.Glazing, true);
            var area = entry.Room.Area;
            if (OpenPlan(entry.Room.Room) && OutsideWalls(document, entry.Room.Room, entry.Boundary, north).GetValueOrDefault(facade) is > 0 and var wall)
                area = Math.Min(area, wall * 4.5);
            var night = openings.Where(opening => ReferenceEquals(opening.Room, entry.Room.Room)).Sum(opening => opening.NightEquivalent);
            return new OverheatingRoom(entry.Room.Room, entry.Room.Name, area, entry.Glazing.Values.Sum(), facade, entry.Bedroom, night);
        }).ToList();

        var checks = new List<FhsCheck>();
        if (settings.NoisyAtNight)
            checks.Add(new FhsCheck("Noise at night (3.1 to 3.3)", false,
                "noise will keep the windows shut at night, so the simplified method cannot show compliance: dynamic thermal modelling to CIBSE TM59 is wanted (Section 2)"));

        // Table 1.1 or 1.2: the home's glazing, and the most glazed room's.
        var (homeLimit, _) = GlazingLimit(high, cross, homeFacade);
        var share = floorArea > 0 ? glazingTotal / floorArea * 100 : 0;
        var table = cross ? "Table 1.1" : "Table 1.2";
        checks.Add(new FhsCheck($"Glazing of the home ({table})", Math.Round(share, 1) <= homeLimit,
            $"{glazingTotal:0.00} m² of glass, {share:0.#}% of its {floorArea:0.0} m² floor; most of it facing {homeFacade}, where {homeLimit:0}% is the most"));
        if (counted.Where(room => room.Glazing > 0).MaxBy(room => room.Share) is { } most)
        {
            var (_, roomLimit) = GlazingLimit(high, cross, most.Facade);
            checks.Add(new FhsCheck($"Glazing of the most glazed room ({table})", Math.Round(most.Share * 100, 1) <= roomLimit,
                $"{most.Name}: {most.Glazing:0.00} m² of glass, {most.Share * 100:0.#}% of its {most.FloorArea:0.0} m² floor; most of it facing {most.Facade}, where {roomLimit:0}% is the most")
            {
                Element = most.Room
            });
        }

        // 1.9: in the high risk location, glazing between north-east and north-west via the south shaded.
        if (high)
        {
            var unshaded = new List<OverheatingOpening>();
            foreach (var opening in openings.Where(opening => opening.Glazing > 0 && Sunny(opening.Bearing)))
            {
                if (settings.Shutters) break;
                var shaded = opening.Element switch
                {
                    Window window when document.FindType<WindowType>(window.TypeId) is { } type =>
                        type.SolarHeatGainCoefficient <= 0.4 && type.LightTransmittance >= 0.7 || DueSouthOverhang(document, window, opening.Bearing, north),
                    RoofWindow roofWindow => document.FindType<RoofWindowType>(roofWindow.TypeId) is { SolarHeatGainCoefficient: <= 0.4 },
                    Opening door => DueSouthOverhang(document, door, opening.Bearing, north),
                    _ => false
                };
                if (!shaded) unshaded.Add(opening);
            }

            checks.Add(new FhsCheck("Shading in the high risk location (1.9)", unshaded.Count == 0, unshaded.Count == 0
                ? settings.Shutters ? "external shutters with means of ventilation on the glazing" : "the glazing between north-east and north-west via the south is shaded or of g 0.4 or less"
                : $"{unshaded.Count} glazed opening{(unshaded.Count == 1 ? string.Empty : "s")} facing between north-east and north-west via the south unshaded - {string.Join(", ", unshaded.Take(4).Select(opening => opening.Name))}: shutters, glass of g 0.4 or less and light transmittance 0.7 or more, or on a due south face an overhang cutting off the sun above 50°")
            {
                Element = unshaded.FirstOrDefault()?.Element
            });
        }

        // Table 1.3 or 1.4: what the home opens to, and each bedroom at night.
        var (floorShare, glazingShare, bedroomShare) = FreeAreaLimit(high, cross);
        var free = openings.Sum(opening => opening.Equivalent);
        var required = Math.Max(floorShare / 100 * floorArea, glazingShare / 100 * glazingTotal);
        var freeTable = cross ? "Table 1.3" : "Table 1.4";
        checks.Add(new FhsCheck($"Free area to remove heat ({freeTable})", Math.Round(free, 2) >= Math.Round(required, 2),
            $"{free:0.00} m² of openings against {required:0.00} - the greater of {floorShare:0}% of the floor and {glazingShare:0}% of the glazing"));
        foreach (var bedroom in counted.Where(room => room.Bedroom))
        {
            var needed = bedroomShare / 100 * bedroom.FloorArea;
            checks.Add(new FhsCheck($"{bedroom.Name}'s free area at night ({freeTable})", Math.Round(bedroom.NightFree, 2) >= Math.Round(needed, 2),
                $"{bedroom.NightFree:0.00} m² against {needed:0.00} - {bedroomShare:0}% of its {bedroom.FloorArea:0.0} m² floor")
            {
                Element = bedroom.Room
            });
        }

        return new OverheatingAssessment(high, location, cross, crossSaid, floorArea, glazingTotal, homeFacade, free, required, counted, openings, checks);
    }

    /// <summary>Whether an opening facing due south has an overhang over it that cuts off the sun above 50° from the whole of it (1.9 c).</summary>
    private static bool DueSouthOverhang(BimDocument document, Opening opening, double? bearing, double north)
    {
        if (bearing is not { } way || Math.Abs(way - 180) > 22.5 || document.FindType<OpeningType>(opening.TypeId) is not { } type) return false;
        var plan = ((way + north) % 360 + 360) % 360;
        return CoolingLoads.Overhang(document, opening, plan) is { } overhang
               && overhang.Depth >= (overhang.Gap + opening.HeightOf(type)) / Math.Tan(50 * Math.PI / 180);
    }
}
