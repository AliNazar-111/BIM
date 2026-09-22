using BIMDesigner.Core.Documents;

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
