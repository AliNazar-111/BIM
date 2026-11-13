namespace BIMDesigner.Core;

/// <summary>
/// Names in the order a person reads them: the numbers in them by their value, the rest as the culture orders it, case aside -
/// "Flat 2" before "Flat 10", "Block A" before "Block B".
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var (i, j) = (0, 0);
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (a, b) = (Run(x, ref i), Run(y, ref j));
                var (left, right) = (a.TrimStart('0'), b.TrimStart('0'));
                var by = left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
                if (by != 0) return by;
                continue;
            }

            var (p, q) = (Text(x, ref i), Text(y, ref j));
            var told = string.Compare(p, q, StringComparison.CurrentCultureIgnoreCase);
            if (told != 0) return told;
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.Compare(x, y, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string Run(string text, ref int at)
    {
        var start = at;
        while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
        return text[start..at];
    }

    private static string Text(string text, ref int at)
    {
        var start = at;
        while (at < text.Length && !char.IsAsciiDigit(text[at])) at++;
        return text[start..at];
    }
}
