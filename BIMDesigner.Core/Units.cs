namespace BIMDesigner.Core;

/// <summary>
/// Every length inside the model is millimetres, every area mm² and every volume mm³,
/// stored as a double. Conversions live here only - never convert ad-hoc elsewhere.
///
/// Specification section 2.3 asks for metric/imperial and per-discipline unit formats; this
/// is the single place that will grow to support them.
/// </summary>
public static class Units
{
    public const double MillimetresPerMetre = 1000.0;
    public const double SquareMillimetresPerSquareMetre = 1_000_000.0;
    public const double CubicMillimetresPerCubicMetre = 1_000_000_000.0;

    public static double MetresToMm(double metres) => metres * MillimetresPerMetre;

    public static double MmToMetres(double mm) => mm / MillimetresPerMetre;

    public static double SquareMmToSquareMetres(double mm2) => mm2 / SquareMillimetresPerSquareMetre;

    public static double CubicMmToCubicMetres(double mm3) => mm3 / CubicMillimetresPerCubicMetre;

    /// <summary>Formats a stored mm value, e.g. 5250 -> "5.25 m", 200 -> "200 mm".</summary>
    public static string FormatLength(double mm) =>
        Math.Abs(mm) >= MillimetresPerMetre
            ? $"{MmToMetres(mm):0.00} m"
            : $"{mm:0.##} mm";

    /// <summary>Formats a stored mm² value as square metres, the unit used on schedules.</summary>
    public static string FormatArea(double mm2) => $"{SquareMmToSquareMetres(mm2):0.00} m²";

    /// <summary>Formats a stored mm³ value as cubic metres, the unit used for takeoff.</summary>
    public static string FormatVolume(double mm3) => $"{CubicMmToCubicMetres(mm3):0.000} m³";

    /// <summary>Rounds a mm value onto the nearest grid step, used for snapping.</summary>
    public static double SnapToGrid(double mm, double stepMm) =>
        stepMm <= 0 ? mm : Math.Round(mm / stepMm) * stepMm;
}
