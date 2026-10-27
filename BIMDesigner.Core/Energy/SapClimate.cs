namespace BIMDesigner.Core.Energy;

/// <summary>
/// The UK average climate SAP 10.3 rates a home and checks it against Approved Document L in
/// (Appendix U, region 0): each month's outside temperature (Table U1), wind (U2), the sun on the
/// level (U3) and its declination, at latitude 53.5°N (U4); and the sun on a surface facing any
/// way at any tilt, by equations U1 to U3 with Table U5's constants.
/// </summary>
public static class SapClimate
{
    /// <summary>The days in each month, January first (Table 1a).</summary>
    public static readonly int[] Days = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    /// <summary>The mean outside temperature, °C (Table U1).</summary>
    public static readonly double[] External = { 4.3, 4.9, 6.5, 8.9, 11.7, 14.6, 16.6, 16.4, 14.1, 10.6, 7.1, 4.2 };

    /// <summary>The wind speed, m/s (Table U2).</summary>
    public static readonly double[] Wind = { 5.1, 5.0, 4.9, 4.4, 4.3, 3.8, 3.8, 3.7, 4.0, 4.3, 4.5, 4.7 };

    /// <summary>The mean sun on a level surface, W/m² (Table U3).</summary>
    public static readonly double[] Horizontal = { 26, 54, 96, 150, 192, 200, 189, 157, 115, 66, 33, 21 };

    /// <summary>The sun's declination, degrees (Table U3).</summary>
    public static readonly double[] Declination = { -20.7, -12.8, -1.8, 9.8, 18.8, 23.1, 21.2, 13.7, 2.9, -8.7, -18.4, -23.0 };

    /// <summary>The UK average's latitude, °N (Table U4).</summary>
    public const double Latitude = 53.5;

    // Table U5: k1 to k9 for a surface facing north, north-east or north-west, east or west, south-east or south-west, and south.
    private static readonly double[][] K =
    {
        new[] { 26.3, 0.165, 1.44, -2.95, -0.66 },
        new[] { -38.5, -3.68, -2.36, 2.89, -0.106 },
        new[] { 14.8, 3.0, 1.07, 1.17, 2.93 },
        new[] { -16.5, 6.38, -0.514, 5.67, 3.63 },
        new[] { 27.3, -4.53, 1.89, -3.54, -0.374 },
        new[] { -11.9, -0.405, -1.64, -4.28, -7.4 },
        new[] { -1.06, -4.38, -0.542, -2.72, -2.71 },
        new[] { 0.0872, 4.89, -0.757, -0.25, -0.991 },
        new[] { -0.191, -1.99, 0.604, 3.07, 4.59 }
    };

    /// <summary>One of Table U5's constants for a surface facing so many degrees clockwise from north, between its columns as the table says.</summary>
    private static double KOf(int row, double orientation)
    {
        var from = ((orientation % 360) + 360) % 360;
        if (from > 180) from = 360 - from;
        var at = Math.Min(from / 45, 3.999999);
        var column = (int)at;
        return K[row][column] + (K[row][column + 1] - K[row][column]) * (at - column);
    }

    /// <summary>
    /// The sun on a surface in a month, W/m²: facing so many degrees clockwise from true north, tilted
    /// so many degrees from the level - 90 for a wall, 0 for a rooflight (equations U1 to U3).
    /// </summary>
    public static double Flux(double orientation, double tilt, int month)
    {
        var half = Math.Sin(tilt / 2 * Math.PI / 180);
        double Term(int first) => KOf(first, orientation) * half * half * half + KOf(first + 1, orientation) * half * half + KOf(first + 2, orientation) * half;
        var a = Term(0);
        var b = Term(3);
        var c = Term(6) + 1;
        var cos = Math.Cos((Latitude - Declination[month]) * Math.PI / 180);
        return Horizontal[month] * (a * cos * cos + b * cos + c);
    }

    /// <summary>The sun on such a surface over a year, kWh/m² (equation U4).</summary>
    public static double Annual(double orientation, double tilt) =>
        Enumerable.Range(0, 12).Sum(month => 0.024 * Days[month] * Flux(orientation, tilt, month));
}
