namespace BIMDesigner.Core.Mep;

/// <summary>
/// A US house's water pipes sized as the IRC's Appendix AP sizes them (AP201.1 - the IPC's
/// Appendix E the same), which P2903.7 points to: the water's static pressure where it comes in,
/// less half a psi for each foot the highest outlet stands above it, picks the pressure range; the
/// pipe from the source to the furthest fixture - through the water heater to a hot one - times
/// 1.2 for its fittings picks the developed length column of Table AP201.1. Down that column, the
/// first row taking all the fixtures' combined water-supply fixture units gives the meter and
/// service and the main distribution pipe; each segment after it takes the smallest distribution
/// size of the same or the next smaller meter's rows carrying the hot or cold fixture units
/// beyond it - and is never larger than the main.
/// </summary>
public static class IrcWaterSizing
{
    /// <summary>Table AP201.1's maximum developed lengths, feet: its columns.</summary>
    public static readonly double[] Lengths = { 40, 60, 80, 100, 150, 200, 250, 300, 400, 500 };

    /// <summary>The fittings a developed length is taken up by for, as AP201.1 step 3 counts them.</summary>
    public const double FittingsAllowance = 1.2;

    /// <summary>What the water loses for each foot it rises, psi (AP201.1 step 2.1).</summary>
    public const double PsiPerFoot = 0.5;

    /// <summary>The most static pressure P2903.3.2 lets a house's water have without a pressure-reducing valve, psi.</summary>
    public const double MostPressure = 80;

    /// <summary>One row of Table AP201.1: its meter and service size and its distribution size, inches, and the most fixture units at each length.</summary>
    public sealed record Row(double Meter, double Distribution, IReadOnlyList<double> Most);

    private static Row R(double meter, double distribution, params double[] most) => new(meter, distribution, most);

    // Table AP201.1 (2021 IRC Appendix AP), by pressure range, its rows in the code's order. A
    // 1/2" distribution pipe is not the building's supply: that is 3/4" at least (its note a).
    private static readonly Row[][] Table =
    {
        // 30 to 39 psi
        new[]
        {
            R(0.75, 0.5, 2.5, 2, 1.5, 1.5, 1, 1, 0.5, 0.5, 0, 0),
            R(0.75, 0.75, 9.5, 7.5, 6, 5.5, 4, 3.5, 3, 2.5, 2, 1.5),
            R(0.75, 1, 32, 25, 20, 16.5, 11, 9, 7.8, 6.5, 5.5, 4.5),
            R(1, 1, 32, 32, 27, 21, 13.5, 10, 8, 7, 5.5, 5),
            R(0.75, 1.25, 32, 32, 32, 32, 30, 24, 20, 17, 13, 10.5),
            R(1, 1.25, 80, 80, 70, 61, 45, 34, 27, 22, 16, 12),
            R(1.5, 1.25, 80, 80, 80, 75, 54, 40, 31, 25, 17.5, 13),
            R(1, 1.5, 87, 87, 87, 87, 84, 73, 64, 56, 45, 36),
            R(1.5, 1.5, 151, 151, 151, 151, 117, 92, 79, 69, 54, 43),
            R(2, 1.5, 151, 151, 151, 151, 128, 99, 83, 72, 56, 45),
            R(1, 2, 87, 87, 87, 87, 87, 87, 87, 87, 87, 86),
            R(1.5, 2, 275, 275, 275, 275, 258, 223, 196, 174, 144, 122),
            R(2, 2, 365, 365, 365, 365, 318, 266, 229, 201, 160, 134),
            R(2, 2.5, 533, 533, 533, 533, 533, 495, 448, 409, 353, 311)
        },

        // 40 to 49 psi
        new[]
        {
            R(0.75, 0.5, 3, 2.5, 2, 1.5, 1.5, 1, 1, 0.5, 0.5, 0.5),
            R(0.75, 0.75, 9.5, 9.5, 8.5, 7, 5.5, 4.5, 3.5, 3, 2.5, 2),
            R(0.75, 1, 32, 32, 32, 26, 18, 13.5, 10.5, 9, 7.5, 6),
            R(1, 1, 32, 32, 32, 32, 21, 15, 11.5, 9.5, 7.5, 6.5),
            R(0.75, 1.25, 32, 32, 32, 32, 32, 32, 32, 27, 21, 16.5),
            R(1, 1.25, 80, 80, 80, 80, 65, 52, 42, 35, 26, 20),
            R(1.5, 1.25, 80, 80, 80, 80, 75, 59, 48, 39, 28, 21),
            R(1, 1.5, 87, 87, 87, 87, 87, 87, 87, 78, 65, 55),
            R(1.5, 1.5, 151, 151, 151, 151, 151, 130, 109, 93, 75, 63),
            R(2, 1.5, 151, 151, 151, 151, 151, 139, 115, 98, 77, 64),
            R(1, 2, 87, 87, 87, 87, 87, 87, 87, 87, 87, 87),
            R(1.5, 2, 275, 275, 275, 275, 275, 275, 264, 238, 198, 169),
            R(2, 2, 365, 365, 365, 365, 365, 349, 304, 270, 220, 185),
            R(2, 2.5, 533, 533, 533, 533, 533, 533, 533, 528, 456, 403)
        },

        // 50 to 60 psi
        new[]
        {
            R(0.75, 0.5, 3, 3, 2.5, 2, 1.5, 1, 1, 1, 0.5, 0.5),
            R(0.75, 0.75, 9.5, 9.5, 9.5, 8.5, 6.5, 5, 4.5, 4, 3, 2.5),
            R(0.75, 1, 32, 32, 32, 32, 25, 18.5, 14.5, 12, 9.5, 8),
            R(1, 1, 32, 32, 32, 32, 30, 22, 16.5, 13, 10, 8),
            R(0.75, 1.25, 32, 32, 32, 32, 32, 32, 32, 32, 29, 24),
            R(1, 1.25, 80, 80, 80, 80, 80, 68, 57, 48, 35, 28),
            R(1.5, 1.25, 80, 80, 80, 80, 80, 75, 63, 53, 39, 29),
            R(1, 1.5, 87, 87, 87, 87, 87, 87, 87, 87, 82, 70),
            R(1.5, 1.5, 151, 151, 151, 151, 151, 151, 139, 120, 94, 79),
            R(2, 1.5, 151, 151, 151, 151, 151, 151, 146, 126, 97, 81),
            R(1, 2, 87, 87, 87, 87, 87, 87, 87, 87, 87, 87),
            R(1.5, 2, 275, 275, 275, 275, 275, 275, 275, 275, 247, 213),
            R(2, 2, 365, 365, 365, 365, 365, 365, 365, 329, 272, 232),
            R(2, 2.5, 533, 533, 533, 533, 533, 533, 533, 533, 533, 486)
        },

        // Over 60 psi
        new[]
        {
            R(0.75, 0.5, 3, 3, 3, 2.5, 2, 1.5, 1.5, 1, 1, 0.5),
            R(0.75, 0.75, 9.5, 9.5, 9.5, 9.5, 7.5, 6, 5, 4.5, 3.5, 3),
            R(0.75, 1, 32, 32, 32, 32, 32, 24, 19.5, 15.5, 11.5, 9.5),
            R(1, 1, 32, 32, 32, 32, 32, 28, 28, 17, 12, 9.5),
            R(0.75, 1.25, 32, 32, 32, 32, 32, 32, 32, 32, 32, 30),
            R(1, 1.25, 80, 80, 80, 80, 80, 80, 69, 60, 46, 36),
            R(1.5, 1.25, 80, 80, 80, 80, 80, 80, 76, 65, 50, 38),
            R(1, 1.5, 87, 87, 87, 87, 87, 87, 87, 87, 87, 84),
            R(1.5, 1.5, 151, 151, 151, 151, 151, 151, 151, 144, 114, 94),
            R(2, 1.5, 151, 151, 151, 151, 151, 151, 151, 151, 118, 97),
            R(1, 2, 87, 87, 87, 87, 87, 87, 87, 87, 87, 87),
            R(1.5, 2, 275, 275, 275, 275, 275, 275, 275, 275, 275, 252),
            R(2, 2, 365, 368, 368, 368, 368, 368, 368, 368, 318, 273),
            R(2, 2.5, 533, 533, 533, 533, 533, 533, 533, 533, 533, 533)
        }
    };

    /// <summary>Table AP201.1's pressure range for a pressure, psi: 0 for 30 to 39, 1 for 40 to 49, 2 for 50 to 60, 3 over 60 - none under 30, which it does not go to.</summary>
    public static int? RangeOf(double psi) => psi < 30 ? null : psi < 40 ? 0 : psi < 50 ? 1 : psi <= 60 ? 2 : 3;

    public static string RangeTitle(int range) => range switch
    {
        0 => "30 to 39 psi",
        1 => "40 to 49 psi",
        2 => "50 to 60 psi",
        _ => "over 60 psi"
    };

    /// <summary>The column for a developed length, feet: the first as long or longer - none past 500 ft.</summary>
    public static int? ColumnOf(double feet)
    {
        for (var i = 0; i < Lengths.Length; i++)
            if (feet <= Lengths[i] + 1e-9) return i;
        return null;
    }

    /// <summary>
    /// The meter and service and the main distribution pipe for a house's whole demand, combined
    /// fixture units (step 4): the first row down the column taking it - not the 1/2" row, the
    /// supply being 3/4" at least. None where the table goes no further.
    /// </summary>
    public static Row? Main(int range, int column, double units) =>
        Table[range].FirstOrDefault(row => row.Distribution >= 0.75 && row.Most[column] >= units - 1e-9);

    /// <summary>
    /// A segment's size, inches, for the hot or cold fixture units through it (step 5): the
    /// smallest distribution pipe of the main's meter's rows, or the next smaller meter's, taking
    /// them - and no larger than the main.
    /// </summary>
    public static double Segment(int range, int column, double units, Row main)
    {
        var smaller = Table[range].Select(row => row.Meter).Where(meter => meter < main.Meter).DefaultIfEmpty(main.Meter).Max();
        var size = Table[range].Where(row => (row.Meter == main.Meter || row.Meter == smaller) && row.Most[column] >= units - 1e-9)
            .Select(row => row.Distribution).DefaultIfEmpty(main.Distribution).Min();
        return Math.Min(size, main.Distribution);
    }

    /// <summary>A nominal size as US drawings write it: 1/2", 3/4", 1", 1-1/4", 2-1/2".</summary>
    public static string Name(double inches)
    {
        var whole = Math.Floor(inches + 1e-9);
        var part = inches - whole;
        var fraction = Math.Abs(part - 0.25) < 1e-6 ? "1/4" : Math.Abs(part - 0.5) < 1e-6 ? "1/2" : Math.Abs(part - 0.75) < 1e-6 ? "3/4" : null;
        return fraction is null ? $"{whole:0}\"" : whole > 0 ? $"{whole:0}-{fraction}\"" : $"{fraction}\"";
    }

    /// <summary>
    /// The size of a material a nominal size is: by its name where it comes in the US's copper tube
    /// sizes; otherwise the one nearest a copper tube of that size outside, an eighth over its nominal.
    /// </summary>
    public static PipeSize SizeOf(PipeMaterial material, double inches) =>
        PipeSizes.Of(material).FirstOrDefault(size => material.IsCts() && size.Name == Name(inches))
        ?? PipeSizes.Nearest(material, (inches + 0.125) * Units.MillimetresPerInch);
}
