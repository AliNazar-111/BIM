using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What lies upwind of the site, rough or smooth: BS EN 1991-1-4's terrain categories (Table 4.1)
/// and ASCE 7's exposures (26.7) in one - the sea or a coast, open country, villages and suburbs,
/// a town or city.
/// </summary>
public enum WindTerrain
{
    /// <summary>Open sea or a coast facing it: terrain category 0; exposure D.</summary>
    Sea,

    /// <summary>Open country, farmland with few hedges and buildings: terrain category II; exposure C.</summary>
    OpenCountry,

    /// <summary>Villages and suburbs, houses close together: terrain category III; exposure B.</summary>
    Suburban,

    /// <summary>A town or city centre, at least 15% of it built over at 15 m and more: terrain category IV; exposure B.</summary>
    Town
}

/// <summary>The wind on one member of the cladding: the pressure it is designed to bend under, kPa, and how that was found.</summary>
public readonly record struct WindOnMember(double Pressure, string Said);

/// <summary>
/// The wind's pressure on cladding, worked out for the site rather than given.
///
/// In England, BS EN 1991-1-4 with its UK National Annex: the map's basic wind speed raised for the
/// site's altitude (calt = 1 + 0.001A up to 10 m, times (10/z)^0.2 above - NA.2.5), its direction,
/// season and probability factors taken as 1; the peak velocity pressure at the building's height
/// for its terrain by the Eurocode's own expressions - qp = [1 + 7Iv]·½ρvm², vm = cr·vb,
/// cr = 0.19(z0/0.05)^0.07·ln(z/z0) (4.3.2, 4.4, 4.5) - in place of the National Annex's charts;
/// and on a wall the external pressure coefficients of Table 7.1 - the edges of a side wall in the
/// wind sucked hardest, zone A within e/5 of a corner, B within e, C beyond, e the lesser of the
/// building's breadth and twice its height; the face into the wind D - taken for the area a member
/// carries between the 1 m² and 10 m² values (Figure 7.2), with the internal pressure +0.2 or -0.3
/// against them (7.2.9). Deflection is checked under these characteristic loads.
///
/// In the US, ASCE 7-16 (as the IBC 2021 cites it): qh = 0.00256·Kz·Kzt·Kd·Ke·V² at the mean roof
/// height (26.10-1) - Kz from Table 26.10-1's power law for the exposure, Kzt 1 on flat ground, Kd
/// 0.85 for components and cladding, Ke for the ground's elevation - and the wall coefficients for
/// components and cladding: zone 5 within a of a corner, zone 4 beyond (Figure 30.3-1 up to 60 ft,
/// 30.5-1 above), for the member's effective wind area - its span by its width, the width at
/// least a third of the span - with GCpi ±0.18 for an enclosed building. Framing carrying glass is
/// held to its deflection limits under 0.6 times these (IBC 2403.3, the loads combined as 1605 has them).
/// </summary>
public static class Wind
{
    /// <summary>Whether the wind is worked out from the site: a basic wind speed set for it.</summary>
    public static bool FromSite(BimDocument document) => document.ProjectInformation.BasicWindSpeed > 0;

    /// <summary>The ground: the lowest storey.</summary>
    public static double Ground(BimDocument document) => document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);

    /// <summary>How tall the building is over the ground, mm: the top of its highest wall - at least 3 m.</summary>
    public static double Height(BimDocument document) =>
        Math.Max(3000, document.Walls.Select(wall => wall.GetTopElevation(document)).DefaultIfEmpty(0).Max() - Ground(document));

    /// <summary>
    /// The UK's peak velocity pressure at a height over the ground, kPa: the map's wind speed, m/s,
    /// raised for the site's altitude, m, and the terrain's roughness and gustiness at that height, m.
    /// </summary>
    public static double PeakPressure(double mapSpeed, double altitude, WindTerrain terrain, double height)
    {
        var z = Math.Max(height, 0.1);
        var altitudeFactor = z <= 10 ? 1 + 0.001 * altitude : 1 + 0.001 * altitude * Math.Pow(10 / z, 0.2);
        var speed = mapSpeed * altitudeFactor;
        var (roughness, least) = terrain switch
        {
            WindTerrain.Sea => (0.003, 1.0),
            WindTerrain.Suburban => (0.3, 5.0),
            WindTerrain.Town => (1.0, 10.0),
            _ => (0.05, 2.0)
        };
        var at = Math.Max(z, least);
        var terrainFactor = 0.19 * Math.Pow(roughness / 0.05, 0.07);
        var mean = terrainFactor * Math.Log(at / roughness) * speed;
        var turbulence = 1 / Math.Log(at / roughness);
        return (1 + 7 * turbulence) * 0.5 * 1.226 * mean * mean / 1000;
    }

    /// <summary>The US's velocity pressure at a height over the ground, kPa, at strength level: the basic wind speed, mph, the site's elevation, m, and the exposure.</summary>
    public static double VelocityPressure(double speed, double altitude, WindTerrain terrain, double height)
    {
        var (alpha, gradient) = terrain switch
        {
            WindTerrain.Sea => (11.5, 700.0),
            WindTerrain.OpenCountry => (9.5, 900.0),
            _ => (7.0, 1200.0)
        };
        var feet = Math.Max(15, height / 0.3048);
        var exposure = 2.01 * Math.Pow(Math.Min(feet, gradient) / gradient, 2 / alpha);
        var elevation = Math.Exp(-0.0000362 * Math.Max(0, altitude) / 0.3048);
        return 0.00256 * exposure * 1.0 * 0.85 * elevation * speed * speed * 0.04788;
    }

    /// <summary>The building in plan along and across a wall: how far its outside walls reach either way of a point on it, mm.</summary>
    private static (double Before, double After, double Across) Extent(BimDocument document, Wall wall, double along)
    {
        var at = wall.LocationCurve.PointAt(Math.Clamp(along, 0, wall.Length));
        var way = wall.TangentAt(Math.Clamp(along, 0, wall.Length));
        var normal = way.PerpendicularLeft();
        var outside = document.Walls
            .Where(other => document.IsCurtainWall(other) || document.GetWallType(other) is { Function: WallFunction.Exterior })
            .SelectMany(other => new[] { other.Start, other.End })
            .Append(wall.Start).Append(wall.End)
            .ToList();
        var lengths = outside.Select(point => (point - at).Dot(way)).ToList();
        var depths = outside.Select(point => (point - at).Dot(normal)).ToList();
        return (Math.Max(0, -lengths.Min()), Math.Max(0, lengths.Max()), Math.Max(1000, depths.Max() - depths.Min()));
    }

    /// <summary>
    /// The wind on a member of a wall - so far along it, spanning so far and carrying so wide a
    /// strip - as the project's standard works it out: the pressure its bending is checked under, kPa.
    /// </summary>
    public static WindOnMember OnMember(BimDocument document, Wall wall, double along, double span, double width)
    {
        var project = document.ProjectInformation;
        if (!FromSite(document)) return new WindOnMember(project.WindPressure, $"{Units.Kilopascals(project.WindPressure, "0.00")} as set");

        var heightMm = Height(document);
        var height = heightMm / 1000;
        var (before, after, across) = Extent(document, wall, along);
        var (edge, length, depth) = (Math.Min(before, after) / 1000, (before + after) / 1000, across / 1000);
        var (spanM, widthM) = (Math.Max(span, 1) / 1000, Math.Max(width, 1) / 1000);

        if (project.ClearanceStandard.IsUs())
        {
            var q = VelocityPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height);
            var area = spanM * Math.Max(widthM, spanM / 3) * 10.7639;
            var least = Math.Min(length, depth);
            var low = height <= 18.288;
            var corner = low ? Math.Max(Math.Min(0.1 * least, 0.4 * height), Math.Max(0.04 * least, 0.9144)) : Math.Max(0.1 * least, 0.9144);
            var inCorner = edge < corner;
            static double Between(double small, double large, double from, double to, double area) =>
                area <= from ? small : area >= to ? large : small + (large - small) * Math.Log10(area / from) / Math.Log10(to / from);
            var (suction, push) = low
                ? (Between(inCorner ? -1.4 : -1.1, -0.8, 10, 500, area), Between(1.0, 0.7, 10, 500, area))
                : (Between(inCorner ? -1.8 : -0.9, inCorner ? -1.0 : -0.7, 20, 500, area), Between(0.9, 0.6, 20, 500, area));
            var net = Math.Max(-suction, push) + 0.18;
            return new WindOnMember(0.6 * net * q,
                $"0.6 x {Units.Kilopascals(net * q, "0.00")} - zone {(inCorner ? 5 : 4)}, GCp {(-suction >= push ? suction : push):+0.00;-0.00} and GCpi ±0.18 on {area:0} sq ft, qh {Units.Kilopascals(q, "0.00")} at {Units.Metres(heightMm, "0.0")} (ASCE 7-16 Figure {(low ? "30.3-1" : "30.5-1")})");
        }

        var qp = PeakPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height);
        var loaded = spanM * widthM;
        var e = Math.Min(depth, 2 * height);
        var zone = edge < e / 5 ? 'A' : edge < e ? 'B' : 'C';
        var (local, overall) = zone switch { 'A' => (-1.4, -1.2), 'B' => (-1.1, -0.8), _ => (-0.5, -0.5) };
        var ratio = height / depth;
        var windward = ratio <= 0.25 ? 0.7 : ratio >= 1 ? 0.8 : 0.7 + 0.1 * (ratio - 0.25) / 0.75;
        static double Sized(double one, double ten, double area) => area <= 1 ? one : area >= 10 ? ten : one - (one - ten) * Math.Log10(area);
        var (side, face) = (Sized(local, overall, loaded), Sized(1.0, windward, loaded));
        var (sucked, pushed) = (-side + 0.2, face + 0.3);
        return new WindOnMember(Math.Max(sucked, pushed) * qp,
            sucked >= pushed
                ? $"zone {zone}, cpe {side:0.00} less cpi +0.2 on {Units.OfSquareMetres(loaded, "0.0")}, qp {qp:0.00} kPa at {height:0.0} m (BS EN 1991-1-4 Table 7.1)"
                : $"zone D, cpe {face:+0.00} less cpi -0.3 on {Units.OfSquareMetres(loaded, "0.0")}, qp {qp:0.00} kPa at {height:0.0} m (BS EN 1991-1-4 Table 7.1)");
    }

    /// <summary>The wind at the building's height, as the plan's Properties say it - or what is set in its place.</summary>
    public static string Verdict(BimDocument document)
    {
        var project = document.ProjectInformation;
        if (!FromSite(document)) return $"{Units.Kilopascals(project.WindPressure, "0.00")} on all cladding, as set - give a basic wind speed to work it out";
        var height = Height(document);
        return project.ClearanceStandard.IsUs()
            ? $"qh {Units.Kilopascals(VelocityPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height / 1000), "0.00")} at {Units.Metres(height, "0.0")} - {project.BasicWindSpeed:0} mph, exposure {(project.Terrain switch { WindTerrain.Sea => "D", WindTerrain.OpenCountry => "C", _ => "B" })} (ASCE 7-16 26.10)"
            : $"qp {PeakPressure(project.BasicWindSpeed, project.SiteAltitude, project.Terrain, height / 1000):0.00} kPa at {Units.Metres(height, "0.0")} - {project.BasicWindSpeed:0.0} m/s, {project.SiteAltitude:0} m up, terrain category {(project.Terrain switch { WindTerrain.Sea => "0", WindTerrain.OpenCountry => "II", WindTerrain.Suburban => "III", _ => "IV" })} (BS EN 1991-1-4 4.5)";
    }
}
