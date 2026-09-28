using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Works out where a wall is solid and where it has been opened up by its doors and windows
/// (specification section 2.5, "automatic opening creation in host").
///
/// The opening is not stored on the wall. A wall's holes are derived from whatever is hosted
/// in it, exactly as its joins are derived from its neighbours - so a door can be added,
/// moved or deleted without anything having to remember to go and patch the wall.
/// </summary>
public static class WallOpenings
{
    /// <summary>Every opening hosted in this wall, in order along it.</summary>
    public static IReadOnlyList<Opening> Of(BimDocument document, Wall wall) =>
        document.Elements
            .OfType<Opening>()
            .Where(opening => opening.HostWallId == wall.Id)
            .OrderBy(opening => opening.DistanceAlongWall)
            .ToList();

    /// <summary>
    /// The stretches of wall that remain solid, as distances along it. A wall with no
    /// openings gives one run covering its whole length.
    ///
    /// Overlapping openings are merged rather than rejected, so a bad model draws sensibly
    /// instead of producing a negative-length run that renders inside out.
    /// </summary>
    public static IReadOnlyList<(double From, double To)> GetSolidRuns(BimDocument document, Wall wall)
    {
        var length = wall.Length;
        var holes = new List<(double From, double To)>();

        foreach (var opening in Of(document, wall))
        {
            var type = document.FindType<OpeningType>(opening.TypeId);
            if (type is null) continue;

            var (from, to) = opening.GetSpan(type);
            from = Math.Max(0, from);
            to = Math.Min(length, to);

            if (to > from) holes.Add((from, to));
        }

        // A curtain wall embedded in this one opens it up the same way, as do the doors and
        // windows of the walls joined to its faces.
        holes.AddRange(WallHoles.Of(document, wall).Select(hole => (hole.From, hole.To)));

        if (holes.Count == 0) return new[] { (0.0, length) };

        holes.Sort((a, b) => a.From.CompareTo(b.From));

        var runs = new List<(double From, double To)>();
        var cursor = 0.0;

        foreach (var (from, to) in holes)
        {
            if (from > cursor) runs.Add((cursor, from));
            cursor = Math.Max(cursor, to);
        }

        if (cursor < length) runs.Add((cursor, length));
        return runs;
    }

    /// <summary>
    /// How much unbroken wall lies immediately beside an opening, before the next opening or
    /// the end of the wall.
    ///
    /// This is what a sliding panel has to park over. Measuring only to the end of the wall
    /// is not enough: a panel cannot slide across a window, so the run stops at whatever it
    /// meets first.
    /// </summary>
    public static double ClearRunBeside(
        BimDocument document, Wall wall, double from, double to, bool towardEnd)
    {
        foreach (var (runFrom, runTo) in GetSolidRuns(document, wall))
        {
            if (towardEnd && Math.Abs(runFrom - to) <= WallJoins.JoinTolerance) return runTo - runFrom;
            if (!towardEnd && Math.Abs(runTo - from) <= WallJoins.JoinTolerance) return runTo - runFrom;
        }

        return 0;
    }

    /// <summary>
    /// Whether an opening of this width can sit at this distance along the wall: it has to
    /// fit inside the wall and must not overlap an opening already there.
    /// </summary>
    public static bool CanPlace(
        BimDocument document, Wall wall, double width, double distanceAlongWall, Opening? ignore = null)
    {
        var from = distanceAlongWall - width / 2;
        var to = distanceAlongWall + width / 2;

        if (from < -WallJoins.JoinTolerance || to > wall.Length + WallJoins.JoinTolerance) return false;

        foreach (var existing in Of(document, wall))
        {
            if (ReferenceEquals(existing, ignore)) continue;

            var type = document.FindType<OpeningType>(existing.TypeId);
            if (type is null) continue;

            var (otherFrom, otherTo) = existing.GetSpan(type);
            if (from < otherTo && to > otherFrom) return false;
        }

        return true;
    }

    /// <summary>
    /// Nudges a distance so an opening of this width sits fully inside the wall. Returns
    /// null when the wall is simply too short to take it.
    /// </summary>
    public static double? ClampToWall(Wall wall, double width, double distanceAlongWall)
    {
        if (width > wall.Length) return null;

        return Math.Clamp(distanceAlongWall, width / 2, wall.Length - width / 2);
    }

    /// <summary>The least room kept between an opening moved to fit and the edge of its wall.</summary>
    public const double FitMargin = 100;

    /// <summary>
    /// A door's or window's sill and height brought inside the wall it is put in, or null when
    /// there is not room for one there at all.
    ///
    /// A wall is not always a rectangle: a dormer's front wall is its gable, a wall under a
    /// roof follows the slope, one drawn with a profile is whatever was drawn. An opening that
    /// fits is left alone. One that would come out of the top is brought down under it - a
    /// window no lower than a little above the wall's foot - and made shorter if it still
    /// does not fit, keeping a little wall above its head, as it would have to be built.
    /// </summary>
    public static (double Sill, double Height)? Fit(
        BimDocument document, Wall wall, double from, double to, double sill, double height, bool isDoor,
        IReadOnlyList<Point2D>? outline = null, double sillMargin = 0, double headMargin = 0)
    {
        var length = wall.Length;
        outline ??= WallProfile.Of(document, wall) ?? WallProfile.Rectangle(length, wall.GetHeight(document));

        // Across its whole width - its sides, every corner of the outline between them and the
        // middle - the highest the wall's foot comes and the lowest its top does.
        var (left, right) = (Math.Clamp(from, 1, length - 1), Math.Clamp(to, 1, length - 1));
        var samples = outline.Select(point => point.X)
            .Where(x => x > left && x < right)
            .Concat(new[] { left, right, (left + right) / 2 });

        var (floor, ceiling) = (double.MinValue, double.MaxValue);
        foreach (var x in samples)
        {
            // Where the wall has no height here, there is nothing to cut the opening in.
            if (WallProfile.HeightsAt(outline, x) is not [var (bottom, top), ..]) return null;

            floor = Math.Max(floor, bottom);
            ceiling = Math.Min(ceiling, top);
        }

        sill = Math.Max(sill, floor + sillMargin);
        if (sill + height <= ceiling - headMargin + WallJoins.JoinTolerance) return (sill, height);

        // Down under the top, then shorter - a door stays standing where it is.
        if (!isDoor) sill = Math.Max(Math.Min(sill, ceiling - FitMargin - height), floor + FitMargin);
        height = Math.Min(height, ceiling - FitMargin - sill);

        return height >= (isDoor ? MinimumDoorHeight : MinimumWindowHeight) ? (sill, height) : null;
    }

    /// <summary>The least a window is made to fit a wall, and a door - no shorter than a person can get through bent.</summary>
    public const double MinimumWindowHeight = 300, MinimumDoorHeight = 1500;

    /// <summary>
    /// Where a new window goes in a wall and how big it is made: its type's size where it fits,
    /// and otherwise smaller - width and height together, so it keeps its proportions - until it
    /// sits inside the wall with a little wall all round it: clear of the walls joined at its
    /// ends, and under the top of a dormer's gable or a wall under a slope. It is moved along
    /// if it would run into a corner. Null when not even a small one fits.
    /// </summary>
    public static (double Along, double Sill, double Width, double Height)? SizeToFit(
        BimDocument document, Wall wall, double along, double sill, double width, double height)
    {
        var outline = WallProfile.Of(document, wall) ?? WallProfile.Rectangle(wall.Length, wall.GetHeight(document));
        var (low, high) = ClearRun(document, wall);

        // Standing on a roof, as a dormer's wall does, it keeps a little wall under its sill
        // for the roof to be flashed up to.
        var sillMargin = document.Elements.OfType<Roof>().Any(roof => roof.Id == wall.BaseAttachedTo) ? FitMargin : 0;

        (double Along, double Sill, double Width, double Height)? At(double scale) => Sized(width * scale, height * scale);

        (double Along, double Sill, double Width, double Height)? Sized(double w, double h)
        {
            if (w > high - low) return null;

            var middle = Math.Clamp(along, low + w / 2, high - w / 2);
            return Fit(document, wall, middle - w / 2, middle + w / 2, sill, h, isDoor: false, outline, sillMargin, headMargin: FitMargin) is { } fit &&
                   fit.Height >= h - 1e-6
                ? (middle, fit.Sill, w, h)
                : null;
        }

        if (At(1) is { } whole) return whole;

        // The biggest that fits, found by halving: smaller always fits at least as well.
        var smallest = Math.Max(MinimumWindowHeight / Math.Max(height, 1), 300 / Math.Max(width, 1));
        if (smallest >= 1 || At(smallest) is null) return null;

        var (fits, fails) = (smallest, 1.0);
        for (var i = 0; i < 30; i++)
        {
            var mid = (fits + fails) / 2;
            if (At(mid) is null) fails = mid;
            else fits = mid;
        }

        // Whole centimetres, as a size is written down - smaller, so it still fits.
        var scaled = At(fits)!.Value;
        return Sized(Math.Floor(scaled.Width / 10) * 10, Math.Floor(scaled.Height / 10) * 10) ?? scaled;
    }

    /// <summary>
    /// The stretch along a wall a window can take without running into the walls joined at its
    /// ends: inside their faces, with a little wall to spare. A free end, or one where the wall
    /// carries straight on, is the end of the wall.
    /// </summary>
    public static (double From, double To) ClearRun(BimDocument document, Wall wall)
    {
        double Inset(Point2D end)
        {
            if (WallJoins.FindPartnerAt(document, wall, end) is not { } partner || document.GetWallType(partner) is not { } type) return 0;

            // Square across it reaches half its thickness in; at a slant, further.
            var sine = Math.Abs(wall.Direction.X * partner.Direction.Y - wall.Direction.Y * partner.Direction.X);
            return sine < 0.2 ? 0 : type.Structure.TotalWidth / 2 / sine + FitMargin;
        }

        var (from, to) = (Inset(wall.Start), wall.Length - Inset(wall.End));
        return to > from ? (from, to) : (0, wall.Length);
    }
}

/// <summary>
/// Marks for doors and windows as they are placed (specification section 3.5, "door tags"):
/// numbered from 1 up, one more than the highest already in the project, whatever the type.
/// </summary>
public static class OpeningMarks
{
    public static string Next<T>(BimDocument document) where T : Opening =>
        (document.Elements.OfType<T>()
            .Select(o => int.TryParse(o.Mark, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
