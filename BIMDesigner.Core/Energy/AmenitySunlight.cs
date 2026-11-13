using System.Numerics;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using Vector3 = System.Numerics.Vector3;

namespace BIMDesigner.Core.Energy;

/// <summary>
/// A garden's or amenity area's sunlight on the day sunlight is counted: the points of a grid over it with the hours of sun each
/// has; the share of it with two hours or more; and, for one that was there before the new work, the share it had then.
/// </summary>
public sealed record AmenitySunlight(GroundRegion Region, IReadOnlyList<(Point2D At, double Hours)> Points, double Share, double? Before)
{
    /// <summary>Whether half of it at least has two hours of sun (BRE 209 3.3).</summary>
    public bool Meets => Share >= AmenitySun.Half - 1e-9;

    /// <summary>Whether, where it was there before, the area with two hours is no less than 0.8 times what it was; true where it was not there before.</summary>
    public bool Kept => Before is not { } before || Share >= AmenitySun.Kept * before - 1e-9;
}

/// <summary>
/// Sunlight in gardens and open spaces as BRE 209 (2022, 3.3) asks of them, as planning assessments quote it: at least half of a garden
/// or amenity area should have at least two hours of sun on 21 March - and one already there should not be left with less than 0.8
/// times the area it had before the new work. The lawns and planting laid on the ground are taken as amenity space, and any other
/// region said to be one; each is gridded every 500 mm, each point 50 mm over its surface, and the sun counted every five minutes on the
/// day sunlight is counted on, from when it is over the horizon - BRE 209 setting no least altitude for open spaces - through the model
/// as daylight sees it. Before the new work is the model with only what is existing in it - its phase.
/// </summary>
public static class AmenitySun
{
    /// <summary>The hours of sun a point is to have, and the share of the area that is to have them.</summary>
    public const double Hours = 2, Half = 0.5;

    /// <summary>The least share of its former sunny area an existing open space may be left with.</summary>
    public const double Kept = 0.8;

    /// <summary>How far apart its points are, mm.</summary>
    public const double Spacing = 500;

    /// <summary>Whether a region of the ground is amenity space: as it is said to be, or else a lawn or planting.</summary>
    public static bool IsAmenity(GroundRegion region) => region.Amenity ?? region.Surface is GroundSurface.Lawn or GroundSurface.Planting;

    /// <summary>The model as the sun on an open space sees it: what is built, standing as a phase has it, the linked models round it, and the ground.</summary>
    private static RenderScene SceneOf(BimDocument document, Func<Element, bool> standing) =>
        RenderScene.Build(Coordination.ModelLinks.WithLinks(document, ModelMeshBuilder.Build(document, element => element is not (Component or Pipe or Duct) && standing(element)), fade: false),
            SurfacePatterns.ByName(document), ground: true);

    /// <summary>The gardens' and amenity areas' sunlight on a day; none where there is no such region.</summary>
    public static IReadOnlyList<AmenitySunlight> Assess(BimDocument document, DateOnly date, CancellationToken cancel = default)
    {
        var regions = document.All<GroundRegion>().Where(region => IsAmenity(region) && region.Boundary.Count >= 3).ToList();
        if (regions.Count == 0) return Array.Empty<AmenitySunlight>();

        // As built - what is standing once the work is done - and, where an open space was there before it, as it stood.
        var after = SceneOf(document, element => element.PhaseDemolished is null);
        var work = document.Elements.Any(element => element.PhaseCreated != DesignPhase.Existing || element.PhaseDemolished is not null);
        var before = work && regions.Any(region => region.PhaseCreated == DesignPhase.Existing)
            ? SceneOf(document, element => element.PhaseCreated == DesignPhase.Existing)
            : null;

        var sun = Directions(document, date);
        var results = new List<AmenitySunlight>();
        foreach (var region in regions)
        {
            cancel.ThrowIfCancellationRequested();
            var grid = Daylight.Grid(region.Boundary, Spacing);
            if (grid.Count == 0) continue;
            var points = grid.Select(at => (At: at, Origin: new Point3D(at.X, at.Y, GroundRegions.HeightAt(document, region, at) + Math.Max(0, region.Offset) + 50))).ToList();
            var hours = points.Select(point => (point.At, Hours: HoursAt(after, point.Origin, sun))).ToList();
            var share = hours.Count(point => point.Hours >= Hours - 1e-9) / (double)hours.Count;
            double? was = null;
            if (before is not null && region.PhaseCreated == DesignPhase.Existing)
                was = points.Count(point => HoursAt(before, point.Origin, sun) >= Hours - 1e-9) / (double)points.Count;
            results.Add(new AmenitySunlight(region, hours, share, was));
        }

        return results;
    }

    /// <summary>The way to the sun every five minutes of a day it is over the horizon, in the model's directions.</summary>
    private static List<Vector3> Directions(BimDocument document, DateOnly date)
    {
        var (latitude, longitude, offset, summer) = document.ProjectInformation.Render.Place(document);
        var ways = new List<Vector3>();
        for (var minute = 0; minute < 24 * 60; minute += 5)
        {
            var utc = SunPosition.ToUtc(date, new TimeOnly(minute / 60, minute % 60), offset, summer);
            var (altitude, azimuth) = SunPosition.Of(latitude, longitude, utc);
            if (altitude <= 0) continue;
            var (x, y, z) = SunPosition.Direction(altitude, azimuth, document.ProjectInformation.TrueNorth);
            ways.Add(Vector3.Normalize(new Vector3((float)x, (float)y, (float)z)));
        }

        return ways;
    }

    /// <summary>The hours of sun a point has: five minutes for each way to the sun nothing blocks.</summary>
    private static double HoursAt(RenderScene scene, Point3D at, IReadOnlyList<Vector3> sun)
    {
        var origin = scene.ToScene(at);
        return sun.Count(toward => scene.Transmission(origin, toward) > 0) * 5 / 60.0;
    }
}
