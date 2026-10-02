namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A band of one layer of a wall type made of another material between two heights - a tile
/// band at the foot of plaster, a plinth of brick under render: Revit's vertically compound wall,
/// a region split off a layer and another layer assigned to it. Heights are up from the bottom
/// of the wall; an open top runs to the top of it, however tall the wall is.
/// </summary>
public sealed record WallBand(int Layer, double Bottom, double? Top, Guid MaterialId)
{
    public bool Covers(double height) => height >= Bottom - 1e-6 && height <= (Top ?? double.PositiveInfinity) + 1e-6;
}

/// <summary>What a layer of a wall type is made of at each height, with its bands.</summary>
public static class WallBands
{
    /// <summary>What a layer is made of at a height above the bottom of the wall: the last band covering it, or the layer's own material.</summary>
    public static Guid MaterialAt(WallType type, int layer, double height)
    {
        for (var i = type.Bands.Count - 1; i >= 0; i--)
            if (type.Bands[i].Layer == layer && type.Bands[i].Covers(height)) return type.Bands[i].MaterialId;

        return layer >= 0 && layer < type.Structure.Layers.Count ? type.Structure.Layers[layer].MaterialId : Guid.Empty;
    }

    /// <summary>Every height a band starts or stops at, above a wall's bottom, as elevations.</summary>
    public static IEnumerable<double> Edges(WallType type, double bottom) =>
        type.Bands.SelectMany(band => band.Top is { } top ? new[] { band.Bottom, top } : new[] { band.Bottom }).Select(height => bottom + height);

    /// <summary>
    /// A layer between two elevations as the runs of one material each it is built in, cut at
    /// its bands' edges - the wall's bottom at the elevation given.
    /// </summary>
    public static IEnumerable<(double Bottom, double Top, Guid MaterialId)> Runs(WallType type, int layer, double from, double to, double bottom)
    {
        var cuts = Edges(type, bottom).Where(z => z > from + 1e-6 && z < to - 1e-6).Append(from).Append(to).Distinct().OrderBy(z => z).ToList();
        (double Bottom, double Top, Guid MaterialId)? run = null;

        for (var i = 0; i + 1 < cuts.Count; i++)
        {
            var material = MaterialAt(type, layer, (cuts[i] + cuts[i + 1]) / 2 - bottom);
            if (run is { } open && open.MaterialId == material)
            {
                run = (open.Bottom, cuts[i + 1], material);
                continue;
            }

            if (run is { } done) yield return done;
            run = (cuts[i], cuts[i + 1], material);
        }

        if (run is { } last) yield return last;
    }

    /// <summary>Why a set of bands cannot be, or null: a band on a layer the type has not got, or upside down.</summary>
    public static string? Problem(IReadOnlyList<WallBand> bands, int layers)
    {
        foreach (var band in bands)
        {
            if (band.Layer < 0 || band.Layer >= layers) return "A band is on a layer the wall has not got.";
            if (band.Bottom < 0 || !double.IsFinite(band.Bottom)) return "A band starts below the bottom of the wall.";
            if (band.Top is { } top && (!double.IsFinite(top) || top <= band.Bottom + 1)) return "A band's top is below its bottom.";
        }

        return null;
    }
}
