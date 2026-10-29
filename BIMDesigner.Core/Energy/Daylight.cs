using System.Numerics;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using Vector3 = System.Numerics.Vector3;

namespace BIMDesigner.Core.Energy;

/// <summary>How finely the daylight is worked out: the grid's spacing over each room's floor and the rays from each point of it.</summary>
public enum DaylightQuality
{
    Draft,
    Standard,
    Fine
}

/// <summary>A level of BS EN 17037's recommendations a room reaches: below its minimum, or its minimum, medium or high.</summary>
public enum DaylightLevel
{
    Below,
    Minimum,
    Medium,
    High
}

/// <summary>What a home's daylight is worked out with that the model does not say.</summary>
public sealed class DaylightSettings
{
    /// <summary>The day sunlight is counted on: 21 March, as BRE 209 takes it - any from 1 February to 21 March BS EN 17037 allows.</summary>
    public DateOnly SunlightDate { get; set; } = new(2026, 3, 21);

    /// <summary>The median diffuse illuminance outside over the daylight hours, lux; none where the location's is taken.</summary>
    public double OutsideLight { get; set; }

    public DaylightQuality Quality { get; set; } = DaylightQuality.Standard;

    public DaylightSettings Copy() => new() { SunlightDate = SunlightDate, OutsideLight = OutsideLight, Quality = Quality };

    public bool SameAs(DaylightSettings other) => SunlightDate == other.SunlightDate && OutsideLight.Equals(other.OutsideLight) && Quality == other.Quality;

    public void CopyFrom(DaylightSettings other) => (SunlightDate, OutsideLight, Quality) = (other.SunlightDate, other.OutsideLight, other.Quality);
}

/// <summary>A point of a room's reference plane and its daylight factor, %.</summary>
public readonly record struct DaylightPoint(Point2D At, double Factor);

/// <summary>
/// A room's daylight: its kind and the UK annex's target for it, lux; its daylight factors over its reference
/// plane; the factor half of it reaches and the one 95% of it does; the level of BS EN 17037 that reaches;
/// whether it meets the UK annex's target; and the hours of sun its windows let in on the day, and their level.
/// </summary>
public sealed record RoomDaylight(
    Room Room,
    LitRoom Kind,
    double? Target,
    IReadOnlyList<DaylightPoint> Points,
    double Median,
    double Least,
    DaylightLevel Level,
    bool? MeetsAnnex,
    double SunHours,
    DaylightLevel SunLevel,
    int Windows);

/// <summary>A home's daylight, room by room, and what it was worked out with: the light outside and where it comes from, the day sunlight was counted on, the least altitude it counted from at the latitude, and the grid's spacing, mm.</summary>
public sealed record DaylightResult(
    IReadOnlyList<RoomDaylight> Rooms,
    double OutsideLight,
    string OutsideFrom,
    DateOnly Date,
    double LeastAltitude,
    double Latitude,
    double Spacing)
{
    /// <summary>Whether a habitable room - the main living room, preferably - has the 1.5 hours of sun the UK annex and BRE 209 ask one room of a home to have.</summary>
    public bool Sunlit => Rooms.Any(room => room.SunHours >= Daylight.SunHours(DaylightLevel.Minimum) - 1e-9);
}

/// <summary>
/// A home's daylight as BS EN 17037 assesses it (its daylight factor method) and its sunlight exposure.
///
/// Each habitable room's reference plane is a grid 0.85 m over its floor, 0.3 m in from its walls, and each
/// point's daylight factor is worked out by tracing the CIE standard overcast sky through the model: rays from
/// the point over the sky's half, out through the glass - its light transmittance 0.68, low-e double glazing's
/// - and round the room off its walls, floor and ceiling at their own colours' reflectance, the ground outside
/// and whatever stands in the way. The furniture and the services are left out. The factor half the plane
/// reaches and the one 95% of it reaches are set against the targets the standard gives - 300 and 100 lux at
/// its minimum level, 500 and 300 at medium, 750 and 500 at high - over the median diffuse illuminance outside:
/// London's 14,100 lux (Table A.3) in the UK, which makes them 2.1% and 0.7%. A home in England is asked the UK
/// annex's lower targets over half its plane: 100 lux in a bedroom, 150 in a living room, 200 in a kitchen or a
/// room with one (0.7%, 1.1% and 1.4%), as BRE 209 (2022) recommends them. Table A.3 has no American city: a US
/// project is worked with London's figure unless its own is set.
///
/// Sunlight is counted on the day set - 21 March, as BRE 209 takes it - every five minutes, at the middle of each
/// window on the inside of its glass, 1.2 m over the floor or 0.3 m over its sill, whichever is higher: the sun
/// counted where it comes in through the glass and nothing stands in its way, and only above the least altitude
/// the standard lets it count from (Table D.1) - the line its values for Nicosia (23°), Madrid (19°), Budapest
/// (14°) and Amsterdam (11°) fall on, taken at the site's latitude. 1.5 hours is its minimum, 3 medium, 4 high;
/// at least one habitable room of a home - the main living room, preferably - is to have the minimum.
/// </summary>
public static class Daylight
{
    /// <summary>How high over the floor the reference plane is, mm, and how far in from the walls its grid keeps, mm.</summary>
    public const double PlaneHeight = 850, Margin = 300;

    /// <summary>Low-e double glazing's light transmittance.</summary>
    public const double Transmittance = 0.68;

    /// <summary>London's median diffuse illuminance outside, lux: BS EN 17037 Table A.3.</summary>
    public const double London = 14_100;

    /// <summary>The targets of a level, lux: what half the reference plane reaches, and 95% of it.</summary>
    public static (double Half, double Most) Targets(DaylightLevel level) => level switch
    {
        DaylightLevel.High => (750, 500),
        DaylightLevel.Medium => (500, 300),
        _ => (300, 100)
    };

    /// <summary>The hours of sun a level asks.</summary>
    public static double SunHours(DaylightLevel level) => level switch
    {
        DaylightLevel.High => 4,
        DaylightLevel.Medium => 3,
        _ => 1.5
    };

    /// <summary>The UK annex's target for a room of a kind, lux - a kitchen or a room with one 200, a living room 150, a bedroom 100; none for a room it does not cover.</summary>
    public static double? AnnexTarget(LitRoom kind) => kind switch
    {
        LitRoom.Kitchen => 200,
        LitRoom.Living or LitRoom.Family or LitRoom.Dining or LitRoom.Study => 150,
        LitRoom.Bedroom => 100,
        _ => null
    };

    /// <summary>Whether a room is a habitable one, whose daylight and sunlight are assessed.</summary>
    public static bool Habitable(LitRoom kind) => AnnexTarget(kind) is not null;

    /// <summary>The least altitude the sun counts from at a latitude, °: Table D.1's reported values' line.</summary>
    public static double LeastAltitude(double latitude) => Math.Clamp(16.75 - 0.699 * (Math.Abs(latitude) - 43.875), 5, 30);

    /// <summary>The median diffuse illuminance outside a project is worked with, lux, and where it comes from.</summary>
    public static (double Lux, string From) Outside(BimDocument document, DaylightSettings settings) =>
        settings.OutsideLight > 0 ? (settings.OutsideLight, "as set")
        : document.ProjectInformation.ClearanceStandard.IsUs() ? (London, "London's (BS EN 17037 Table A.3 has no American city) - set the site's own from its climate data")
        : (London, "London's, BS EN 17037 Table A.3");

    /// <summary>The level a room's daylight factors reach - the one half its plane reaches and the one 95% does - against an illuminance outside.</summary>
    public static DaylightLevel LevelOf(double median, double least, double outside)
    {
        foreach (var level in new[] { DaylightLevel.High, DaylightLevel.Medium, DaylightLevel.Minimum })
        {
            var (half, most) = Targets(level);
            if (median >= half / outside * 100 - 1e-9 && least >= most / outside * 100 - 1e-9) return level;
        }

        return DaylightLevel.Below;
    }

    /// <summary>The level hours of sun reach.</summary>
    public static DaylightLevel SunLevelOf(double hours) =>
        hours >= SunHours(DaylightLevel.High) - 1e-9 ? DaylightLevel.High
        : hours >= SunHours(DaylightLevel.Medium) - 1e-9 ? DaylightLevel.Medium
        : hours >= SunHours(DaylightLevel.Minimum) - 1e-9 ? DaylightLevel.Minimum
        : DaylightLevel.Below;

    /// <summary>The value a share of a room's points reach: the median at a half, the least of most of them at 0.95.</summary>
    public static double Reached(IReadOnlyList<double> factors, double share)
    {
        if (factors.Count == 0) return 0;
        var sorted = factors.OrderByDescending(value => value).ToList();
        var index = (int)Math.Ceiling(share * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>The points of a room's reference plane, in plan: a grid of the spacing over its floor, the margin in from its walls.</summary>
    public static IReadOnlyList<Point2D> Grid(IReadOnlyList<Point2D> outline, double spacing)
    {
        var points = new List<Point2D>();
        if (outline.Count < 3) return points;
        var (left, right) = (outline.Min(point => point.X), outline.Max(point => point.X));
        var (bottom, top) = (outline.Min(point => point.Y), outline.Max(point => point.Y));
        var across = right - left - 2 * Margin;
        var along = top - bottom - 2 * Margin;
        if (across <= 0 || along <= 0) return points;
        var (nx, ny) = (Math.Max(1, (int)Math.Round(across / spacing)), Math.Max(1, (int)Math.Round(along / spacing)));
        for (var i = 0; i < nx; i++)
            for (var j = 0; j < ny; j++)
            {
                var at = new Point2D(left + Margin + (i + 0.5) * across / nx, bottom + Margin + (j + 0.5) * along / ny);
                if (Polygon2D.Contains(outline, at) && Edge(at, outline) >= Margin - 1) points.Add(at);
            }

        return points;
    }

    private static double Edge(Point2D point, IReadOnlyList<Point2D> outline)
    {
        var nearest = double.MaxValue;
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var along = b - a;
            var t = along.Length < 1e-9 ? 0 : Math.Clamp(((point.X - a.X) * along.X + (point.Y - a.Y) * along.Y) / (along.Length * along.Length), 0, 1);
            nearest = Math.Min(nearest, point.DistanceTo(new Point2D(a.X + along.X * t, a.Y + along.Y * t)));
        }

        return nearest;
    }

    /// <summary>The model as daylight sees it: what is built, without the furniture and the services; and the ground.</summary>
    public static RenderScene SceneOf(BimDocument document) =>
        RenderScene.Build(ModelMeshBuilder.Build(document, element => element is not (Component or Pipe or Duct)), SurfacePatterns.ByName(document), ground: true);

    /// <summary>
    /// The daylight factor at a point of the model, %: the light the CIE overcast sky gives it, upward over a level
    /// plane, through the glass and off the surfaces, over what the same sky gives the ground with nothing in the way.
    /// </summary>
    public static double FactorAt(RenderScene scene, Point3D at, int rays, uint seed)
    {
        var origin = scene.ToScene(at);
        var random = new Sampler(seed);
        var side = Math.Max(1, (int)Math.Sqrt(rays));
        var sum = 0.0;
        for (var i = 0; i < side; i++)
            for (var j = 0; j < side; j++)
                sum += Radiance(scene, origin, Cosine(Vector3.UnitZ, (i + random.Next()) / side, (j + random.Next()) / side), ref random);

        // Under the overcast sky L(θ) = Lz (1 + 2 sin θ) / 3, the ground outside gets 7π/9 Lz; the point π times the mean it sees.
        return sum / (side * side) * 9 / 7 * 100;
    }

    /// <summary>The light along a ray, as a share of the sky's at its zenith: through glass, off what it meets, out to the sky.</summary>
    private static double Radiance(RenderScene scene, Vector3 origin, Vector3 direction, ref Sampler random)
    {
        var carried = 1.0;
        var through = Math.Sqrt(Transmittance);
        for (var bounce = 0; bounce < 12; bounce++)
        {
            if (!scene.Intersect(origin, direction, float.MaxValue, out var hit))
                return direction.Z > 0 ? carried * (1 + 2 * direction.Z) / 3 : 0;

            var material = scene.Materials[scene.MaterialOf[hit.Triangle]];
            var point = origin + direction * hit.T;
            if (material.Glass || material.Opacity < 1)
            {
                // A pane is a thin block: each of its faces passes the root of what the glass does.
                carried *= material.Glass ? through : 1 - material.Opacity;
                origin = point + direction * 1e-4f;
                continue;
            }

            var albedo = material.Albedo;
            var reflectance = 0.2126 * albedo.X + 0.7152 * albedo.Y + 0.0722 * albedo.Z;
            carried *= reflectance;
            if (bounce >= 3)
            {
                // Past the third reflection, carried on by chance in proportion to what is left.
                var keep = Math.Clamp(carried * 4, 0.05, 1);
                if (random.Next() > keep) return 0;
                carried /= keep;
            }

            if (carried < 1e-6) return 0;
            var normal = scene.NormalAt(hit);
            if (Vector3.Dot(normal, direction) > 0) normal = -normal;
            origin = point + normal * 1e-4f;
            direction = Cosine(normal, random.Next(), random.Next());
        }

        return 0;
    }

    /// <summary>A direction over a surface, cosine-weighted, from two numbers between 0 and 1.</summary>
    private static Vector3 Cosine(Vector3 normal, double u, double v)
    {
        var r = Math.Sqrt(u);
        var phi = 2 * Math.PI * v;
        var (x, y, z) = (r * Math.Cos(phi), r * Math.Sin(phi), Math.Sqrt(Math.Max(0, 1 - u)));
        var helper = Math.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(helper, normal));
        var bitangent = Vector3.Cross(normal, tangent);
        return Vector3.Normalize(tangent * (float)x + bitangent * (float)y + normal * (float)z);
    }

    /// <summary>A small fast generator of numbers between 0 and 1, its own for each point so the same model gives the same result.</summary>
    private struct Sampler
    {
        private uint _state;

        public Sampler(uint seed) => _state = seed == 0 ? 0x9E3779B9u : seed;

        public double Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / 4294967296.0;
        }
    }

    /// <summary>The floor of a room at a point: the top of the floor over its level there, or its level.</summary>
    private static double FloorAt(BimDocument document, Room room, Point2D at) =>
        FloorSurfaces.FloorUnder(document, room.LevelId, at) is { } slab ? slab.GetTopElevation(document) : document.FindLevel(room.LevelId)?.Elevation ?? 0;

    /// <summary>
    /// The points sunlight is counted at in a room: one at the middle of each window in the walls round it, just
    /// inside its glass, 1.2 m over the floor or 0.3 m over its sill, whichever is higher - no higher than its head.
    /// </summary>
    public static IReadOnlyList<Point3D> SunPoints(BimDocument document, Room room, RoomBoundaryResult boundary)
    {
        var walls = boundary.BoundingWalls.Select(wall => wall.Id).ToHashSet();
        var points = new List<Point3D>();
        foreach (var pane in ViewOut.Panes(document, room.LevelId).Where(pane => walls.Contains(pane.Host)))
        {
            var middle = pane.A.MidpointTo(pane.B);
            var half = document.Find<Wall>(pane.Host) is { } wall && document.GetWallType(wall) is { } type ? type.Structure.TotalWidth / 2 : 100;
            // Just inside the glass on the room's side, whichever way the wall was drawn.
            var inside = middle - pane.Out * (half + 20);
            if (!Polygon2D.Contains(boundary.Polygon, inside)) inside = middle + pane.Out * (half + 20);
            if (!Polygon2D.Contains(boundary.Polygon, inside)) continue;
            var floor = FloorAt(document, room, inside);
            var height = Math.Min(Math.Max(floor + 1200, pane.Sill + 300), pane.Head - 50);
            points.Add(new Point3D(inside.X, inside.Y, height));
        }

        return points;
    }

    /// <summary>The hours of sun that reach any of a room's points on a day, counted every five minutes above the least altitude.</summary>
    public static double SunHoursAt(BimDocument document, RenderScene scene, IReadOnlyList<Point3D> points, DateOnly date)
    {
        if (points.Count == 0) return 0;
        var (latitude, longitude, offset, summer) = document.ProjectInformation.Render.Place(document);
        var least = LeastAltitude(latitude);
        var origins = points.Select(scene.ToScene).ToList();
        var steps = 0;
        for (var minute = 0; minute < 24 * 60; minute += 5)
        {
            var utc = SunPosition.ToUtc(date, new TimeOnly(minute / 60, minute % 60), offset, summer);
            var (altitude, azimuth) = SunPosition.Of(latitude, longitude, utc);
            if (altitude < least) continue;
            var (x, y, z) = SunPosition.Direction(altitude, azimuth, document.ProjectInformation.TrueNorth);
            var toward = Vector3.Normalize(new Vector3((float)x, (float)y, (float)z));
            if (origins.Any(origin => scene.Transmission(origin, toward) > 0)) steps++;
        }

        return steps * 5 / 60.0;
    }

    /// <summary>
    /// The home's daylight and sunlight, room by room - every habitable room's, or those of the rooms given -
    /// saying how far along it is as it goes.
    /// </summary>
    public static DaylightResult Assess(BimDocument document, DaylightSettings settings, IReadOnlyCollection<Room>? only = null, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        var (outside, from) = Outside(document, settings);
        var latitude = document.ProjectInformation.Render.Place(document).Latitude;
        var (spacing, rays) = settings.Quality switch
        {
            DaylightQuality.Draft => (600.0, 128),
            DaylightQuality.Fine => (300.0, 1024),
            _ => (400.0, 512)
        };

        var rooms = new List<(Room Room, RoomBoundaryResult Boundary, LitRoom Kind)>();
        using (var cache = ModelCache.Begin(document))
            foreach (var room in document.All<Room>().Where(room => only is null || only.Contains(room)))
                if (room.GetBoundary(document) is { IsEnclosed: true } boundary && boundary.Polygon.Count >= 3
                    && Lighting.KindOf(document, room, boundary.Polygon) is var kind && Habitable(kind))
                    rooms.Add((room, boundary, kind));

        var scene = SceneOf(document);
        var grids = rooms.Select(entry => Grid(entry.Boundary.Polygon, spacing)).ToList();
        var total = Math.Max(1, grids.Sum(grid => grid.Count));
        var done = 0;
        var results = new List<RoomDaylight>();
        for (var r = 0; r < rooms.Count; r++)
        {
            cancel.ThrowIfCancellationRequested();
            var (room, boundary, kind) = rooms[r];
            var grid = grids[r];
            var factors = new double[grid.Count];
            var heights = grid.Select(at => FloorAt(document, room, at) + PlaneHeight).ToArray();
            Parallel.For(0, grid.Count, new ParallelOptions { CancellationToken = cancel }, i =>
            {
                var at = new Point3D(grid[i].X, grid[i].Y, heights[i]);
                factors[i] = FactorAt(scene, at, rays, (uint)(i * 2654435761u + (uint)r * 40503u + 1));
                var now = Interlocked.Increment(ref done);
                if (now % 16 == 0) progress?.Report((double)now / total);
            });

            var median = Reached(factors, 0.5);
            var least = Reached(factors, 0.95);
            var target = AnnexTarget(kind);
            var sun = SunPoints(document, room, boundary);
            var hours = SunHoursAt(document, scene, sun, settings.SunlightDate);
            results.Add(new RoomDaylight(room, kind, target, grid.Select((at, i) => new DaylightPoint(at, factors[i])).ToList(),
                median, least, LevelOf(median, least, outside),
                target is { } lux && grid.Count > 0 ? median >= lux / outside * 100 - 1e-9 : null,
                hours, SunLevelOf(hours), sun.Count));
        }

        progress?.Report(1);
        return new DaylightResult(results, outside, from, settings.SunlightDate, LeastAltitude(latitude), latitude, spacing);
    }
}

/// <summary>The project's daylight settings changed, as one step to undo.</summary>
public sealed class SetDaylightSettingsCommand : Documents.Commands.IUndoableCommand
{
    private readonly DaylightSettings _settings;
    private readonly DaylightSettings _before;
    private readonly DaylightSettings _after;

    public SetDaylightSettingsCommand(DaylightSettings settings, DaylightSettings after)
    {
        _settings = settings;
        _before = settings.Copy();
        _after = after.Copy();
    }

    public string Name => "Daylight Settings";

    public void Redo() => _settings.CopyFrom(_after);

    public void Undo() => _settings.CopyFrom(_before);
}
