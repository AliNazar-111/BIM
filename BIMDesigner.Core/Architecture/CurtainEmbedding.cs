using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The hole an embedded curtain wall makes in the wall around it: along the host wall and
/// height above the host's base.
/// </summary>
public sealed record EmbeddedHole(Wall Curtain, double From, double To, double Sill, double Head);

/// <summary>
/// Curtain walls set into other walls (specification section 3.1, "embedded walls"): a
/// shopfront in a brick wall, a glazed screen in a partition.
///
/// A curtain wall whose type says to embed automatically, drawn inside another wall - both
/// straight, running the same way, the curtain wall's line within the other's thickness and
/// its ends within the other's - cuts itself an opening there, its own length and height. Like
/// a door's opening it is never stored: move either wall and the hole follows, or closes up.
/// </summary>
public static class CurtainEmbedding
{
    /// <summary>How nearly parallel the two walls must be: the sine of the angle between them.</summary>
    private const double ParallelSine = 1e-4;

    /// <summary>The wall a curtain wall is embedded in, and the hole it makes there, or null.</summary>
    public static (Wall Host, EmbeddedHole Hole)? Of(BimDocument document, Wall curtain)
    {
        if (document.FindType<CurtainWallType>(curtain.TypeId) is not { AutomaticallyEmbed: true } || curtain.IsCurved)
            return null;

        foreach (var host in document.Walls)
        {
            if (ReferenceEquals(host, curtain) || host.LevelId != curtain.LevelId || host.IsCurved) continue;
            if (document.IsCurtainWall(host) || document.GetWallType(host) is not { } type) continue;
            if (Math.Abs(host.Direction.Cross(curtain.Direction)) > ParallelSine) continue;

            // Its line inside the host's thickness, and both its ends between the host's.
            var structure = type.Structure;
            var (startAlong, startAcross) = host.Locate(structure, curtain.Start);
            var (endAlong, endAcross) = host.Locate(structure, curtain.End);
            var half = type.Width / 2;
            if (Math.Abs(startAcross) > half + WallJoins.JoinTolerance || Math.Abs(endAcross) > half + WallJoins.JoinTolerance) continue;

            var from = Math.Min(startAlong, endAlong);
            var to = Math.Max(startAlong, endAlong);
            if (from < -WallJoins.JoinTolerance || to > host.Length + WallJoins.JoinTolerance || to - from <= WallJoins.JoinTolerance) continue;

            var sill = curtain.GetBaseElevation(document) - host.GetBaseElevation(document);
            var head = sill + curtain.GetHeight(document);
            return (host, new EmbeddedHole(curtain, Math.Max(0, from), Math.Min(host.Length, to), sill, head));
        }

        return null;
    }

    /// <summary>The holes embedded curtain walls make in a wall, in order along it.</summary>
    public static IReadOnlyList<EmbeddedHole> In(BimDocument document, Wall host)
    {
        if (document.IsCurtainWall(host) || host.IsCurved) return Array.Empty<EmbeddedHole>();

        return document.Walls
            .Where(wall => !ReferenceEquals(wall, host) && wall.LevelId == host.LevelId)
            .Select(wall => Of(document, wall))
            .Where(found => found is { } f && ReferenceEquals(f.Host, host))
            .Select(found => found!.Value.Hole)
            .OrderBy(hole => hole.From)
            .ToList();
    }

    /// <summary>Whether a wall sits inside another as an embedded curtain wall - and so is not joined to it.</summary>
    public static bool IsEmbedded(BimDocument document, Wall wall) => Of(document, wall) is not null;
}
