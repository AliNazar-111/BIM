using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// The loads coming down a house onto its foundations, estimated from the model as a building
/// control officer's first check would: each wall's own weight and the weight of the walls on
/// top of it, from what their layers are made of; the floors bearing on them, half their span
/// either side, their own weight from their layers and what is put on them; and the roof's
/// slopes on the walls their eaves run along. Characteristic loads, dead and imposed together -
/// what Approved Document A's Table 10 is read against. Imposed: 1.5 kN/m² on a floor (BS EN
/// 1991-1-1, a home's) and 0.75 on a roof for snow and access; under the IRC, 40 psf on a floor
/// and the greater of its 20 psf roof live load and the ground snow load on a roof.
/// </summary>
public static class Loads
{
    /// <summary>A pound a square foot, in kN/m².</summary>
    public const double Psf = 0.04788;

    /// <summary>What is put on a floor, kN/m².</summary>
    public static double FloorImposed(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? 40 * Psf : 1.5;

    /// <summary>What is put on a roof - snow, people working on it - kN/m² of its plan.</summary>
    public static double RoofImposed(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? Math.Max(20, document.ProjectInformation.GroundSnowLoad) * Psf : 0.75;

    /// <summary>What a build-up weighs a square metre of its face, kN/m²: each layer's thickness by its material's density.</summary>
    public static double Weight(BimDocument document, CompoundStructure structure) =>
        structure.Layers.Sum(layer => layer.Thickness / 1000 * (document.FindMaterial(layer.MaterialId)?.Density ?? 0)) * 9.81 / 1000;

    /// <summary>Whether two walls stand one on the other: along the same line, overlapping, one higher.</summary>
    private static bool Over(BimDocument document, Wall lower, Wall upper)
    {
        if (ReferenceEquals(lower, upper) || lower.IsCurved || upper.IsCurved) return false;
        var way = (lower.End - lower.Start).NormalisedOrDefault(Vector2D.UnitX);
        var other = (upper.End - upper.Start).NormalisedOrDefault(Vector2D.UnitX);
        if (Math.Abs(way.Cross(other)) > 0.03) return false;
        var reach = Math.Max(Footings.WallThickness(document, lower), Footings.WallThickness(document, upper)) / 2 + 50;
        if (Line2D.DistanceFromSegment(upper.Start.MidpointTo(upper.End), lower.Start - way * 1e5, lower.End + way * 1e5) > reach) return false;
        var (a, b) = ((upper.Start - lower.Start).Dot(way), (upper.End - lower.Start).Dot(way));
        if (Math.Max(a, b) <= 1 || Math.Min(a, b) >= lower.Length - 1) return false;
        return upper.GetBaseElevation(document) >= lower.GetBaseElevation(document) + 500;
    }

    /// <summary>The wall and the walls standing on top of it, bottom up.</summary>
    public static IReadOnlyList<Wall> Stack(BimDocument document, Wall wall)
    {
        var stack = new List<Wall> { wall };
        foreach (var upper in document.Walls.Where(upper => Over(document, wall, upper)).OrderBy(upper => upper.GetBaseElevation(document)))
            stack.Add(upper);
        return stack;
    }

    /// <summary>How much of a wall's length another along the same line covers, as a share of it.</summary>
    private static double Share(Wall wall, Wall other)
    {
        if (wall.Length < 1) return 0;
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var (a, b) = ((other.Start - wall.Start).Dot(way), (other.End - wall.Start).Dot(way));
        return Math.Clamp((Math.Min(Math.Max(a, b), wall.Length) - Math.Max(Math.Min(a, b), 0)) / wall.Length, 0, 1);
    }

    /// <summary>
    /// The load a wall brings down onto its footing, kN a metre of it: its weight and the walls',
    /// floors' and roof's on top of it.
    /// </summary>
    public static double OnWall(BimDocument document, Wall wall)
    {
        if (wall.IsCurved || wall.Length < 1) return 0;
        var total = 0.0;
        var stack = Stack(document, wall);
        foreach (var part in stack)
        {
            var share = ReferenceEquals(part, wall) ? 1 : Share(wall, part);
            if (document.GetWallType(part) is { } type)
                total += Weight(document, type.Structure) * part.GetHeight(document) / 1000 * share;
            total += FloorsOn(document, part) * share;
        }

        // The roof, on whichever of them its eaves run along.
        total += stack.Sum(part => RoofOn(document, part) * (ReferenceEquals(part, wall) ? 1 : Share(wall, part)));

        // A suspended ground floor bears on the walls round it too; one cast on the ground does not.
        total += FloorsOn(document, wall, atBase: true);
        return total;
    }

    /// <summary>Whether a floor is cast on the ground: its structure concrete, at the lowest level, on the walls' base.</summary>
    private static bool GroundBearing(BimDocument document, Floor floor) =>
        document.FindType<SlabType>(floor.TypeId) is { } type &&
        type.Structure.Layers.Where(layer => layer.Function == LayerFunction.Structure)
            .Any(layer => (document.FindMaterial(layer.MaterialId)?.Density ?? 0) >= 1800);

    /// <summary>
    /// The floors bearing on a wall's top - or, asked, a suspended floor at its base - kN a metre:
    /// each spanning onto it, the half of its span either side, its weight and what is put on it.
    /// </summary>
    private static double FloorsOn(BimDocument document, Wall wall, bool atBase = false)
    {
        var height = atBase ? wall.GetBaseElevation(document) : wall.GetTopElevation(document);
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var middle = wall.Start.MidpointTo(wall.End);
        var total = 0.0;
        foreach (var floor in document.Elements.OfType<Floor>())
        {
            if (floor.Boundary.Count < 3 || document.FindType<SlabType>(floor.TypeId) is not { } type) continue;
            var top = floor.GetTopElevation(document);
            if (Math.Abs(top - height) > 600) continue;
            if (atBase && GroundBearing(document, floor)) continue;

            var span = SpanOf(floor);
            if (Math.Abs(span.Dot(way)) > 0.7) continue;

            // Half the way to the next support each side, inside the floor.
            var tributary = 0.0;
            foreach (var side in new[] { 1.0, -1.0 })
                tributary += Reach(document, floor, middle, span * side, height, wall) / 2;
            if (tributary <= 0) continue;

            total += tributary / 1000 * (Weight(document, type.Structure) + FloorImposed(document));
        }

        return total;
    }

    /// <summary>The way a floor spans: as drawn, or the short way across it.</summary>
    private static Vector2D SpanOf(Slab floor)
    {
        if (floor is Floor { SpanDirection: { } degrees }) return new Vector2D(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));
        var (minX, maxX, minY, maxY) = (floor.Boundary.Min(p => p.X), floor.Boundary.Max(p => p.X), floor.Boundary.Min(p => p.Y), floor.Boundary.Max(p => p.Y));
        return maxX - minX <= maxY - minY ? Vector2D.UnitX : Vector2D.UnitY;
    }

    /// <summary>
    /// How far a floor runs from a point one way before something holds it up again - a wall under
    /// it, square across the way, at its height - or it ends; nothing where it does not go that way.
    /// </summary>
    private static double Reach(BimDocument document, Slab floor, Point2D from, Vector2D way, double height, Wall? own = null, Column? column = null)
    {
        if (!floor.Contains(from + way * 50)) return 0;
        const double far = 30000;
        var to = from + way * far;
        var nearest = far;

        // Its edge.
        var boundary = floor.Boundary;
        for (var i = 0; i < boundary.Count; i++)
            if (Cross(from, to, boundary[i], boundary[(i + 1) % boundary.Count]) is { } t && t * far > 50) nearest = Math.Min(nearest, t * far);

        // The walls holding it up.
        foreach (var wall in document.Walls)
        {
            if (ReferenceEquals(wall, own) || wall.IsCurved || Math.Abs(wall.GetTopElevation(document) - height) > 600) continue;
            if (Math.Abs((wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).Dot(way)) > 0.7) continue;
            if (Cross(from, to, wall.Start, wall.End) is { } t && t * far > 50) nearest = Math.Min(nearest, t * far);
        }

        // The columns holding it up, on its line.
        foreach (var other in document.Elements.OfType<Column>().Where(other => other.Structural && !ReferenceEquals(other, column)))
        {
            var ahead = (other.Location - from).Dot(way);
            if (ahead <= 50 || Math.Abs((other.Location - from).Cross(way)) > 600) continue;
            if (Math.Abs(other.GetTopElevation(document) - height) > 600) continue;
            nearest = Math.Min(nearest, ahead);
        }

        return nearest >= far ? 0 : nearest;
    }

    /// <summary>Where along a to b another line crosses it, as a share of it; null where they do not cross.</summary>
    private static double? Cross(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        var r = b - a;
        var s = d - c;
        var denominator = r.Cross(s);
        if (Math.Abs(denominator) < 1e-9) return null;
        var t = (c - a).Cross(s) / denominator;
        var u = (c - a).Cross(r) / denominator;
        return t is >= 0 and <= 1 && u is >= -1e-6 and <= 1 + 1e-6 ? t : null;
    }

    /// <summary>
    /// The roof on a wall, kN a metre: each slope whose eave runs along it - picked off it, or over
    /// it - its plan area, its weight up the slope and what is put on it, shared along the eave.
    /// </summary>
    private static double RoofOn(BimDocument document, Wall wall)
    {
        var total = 0.0;
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var top = wall.GetTopElevation(document);
        foreach (var roof in document.Elements.OfType<Roof>())
        {
            if (roof.IsExtrusion || roof.Boundary.Count < 3 || roof.Edges.Count != roof.Boundary.Count) continue;
            if (document.FindType<SlabType>(roof.TypeId) is not { } type) continue;
            if (Math.Abs(roof.BaseElevation(document) - top) > 1500) continue;

            var surface = roof.Surface(document);
            var count = roof.Boundary.Count;
            for (var i = 0; i < count; i++)
            {
                var edge = roof.Edges[i];
                if (!edge.DefinesSlope) continue;
                var (a, b) = (roof.Boundary[i], roof.Boundary[(i + 1) % count]);
                var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
                var on = edge.WallId == wall.Id ||
                         Math.Abs(along.Cross(way)) < 0.03 && Line2D.DistanceFromSegment(wall.Start.MidpointTo(wall.End), a - along * 1e5, b + along * 1e5) <= edge.Overhang + Footings.WallThickness(document, wall) + 200;
                if (!on) continue;

                var inward = along.PerpendicularLeft() * (Polygon2D.SignedArea(roof.Boundary) >= 0 ? 1 : -1);
                if (surface.FaceAt(a.MidpointTo(b) + inward * 1) is not { } face) continue;
                var eave = a.DistanceTo(b) / 1000;
                if (eave <= 0) continue;
                total += face.Area / 1e6 * (Weight(document, type.Structure) * face.Plane.VerticalStretch + RoofImposed(document)) / eave;
            }
        }

        return total;
    }

    /// <summary>
    /// The load a structural column brings down onto its pad, kN: its own weight, and the floors and
    /// roof bearing on it - each the area half way to the next support every way round it, its
    /// weight and what is put on it - with what the columns standing on it bring down.
    /// </summary>
    public static double OnColumn(BimDocument document, Column column) => OnColumn(document, column, 0);

    private static double OnColumn(BimDocument document, Column column, int depth)
    {
        if (depth > 6 || document.FindType<ColumnType>(column.TypeId) is not { } type) return 0;
        var height = column.GetHeight(document);
        var total = type.SectionArea / 1e6 * height / 1000 * BeamType.Density(type.StructuralMaterial) * 9.81 / 1000;
        var top = column.GetTopElevation(document);
        var across = column.Across;
        var along = across.PerpendicularLeft();

        foreach (var slab in document.Elements.OfType<Slab>().Where(slab => slab is Floor or Roof))
        {
            if (slab.Boundary.Count < 3 || !slab.Contains(column.Location)) continue;
            if (document.FindType<SlabType>(slab.TypeId) is not { } slabType) continue;
            var under = slab is Roof roof ? roof.UndersideAt(document, column.Location) : slab.GetBottomElevation(document);
            if (Math.Abs(under - top) > 600) continue;

            double Half(Vector2D way) => Reach(document, slab, column.Location, way, top, column: column) / 2;
            var area = (Half(across) + Half(-across)) * (Half(along) + Half(-along)) / 1e6;
            if (area <= 0) continue;

            total += slab is Roof pitched
                ? area * (Weight(document, slabType.Structure) * (pitched.Surface(document).FaceAt(column.Location)?.Plane.VerticalStretch ?? 1) + RoofImposed(document))
                : area * (Weight(document, slabType.Structure) + FloorImposed(document));
        }

        // A column standing on this one brings its load down through it.
        foreach (var upper in document.Elements.OfType<Column>().Where(upper => upper.Structural && !ReferenceEquals(upper, column) &&
                     upper.Location.DistanceTo(column.Location) < 100 && Math.Abs(upper.GetBaseElevation(document) - top) < 600))
            total += OnColumn(document, upper, depth + 1);

        return total;
    }
}
