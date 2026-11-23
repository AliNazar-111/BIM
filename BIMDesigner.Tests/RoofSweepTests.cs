using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// Roofs by footprint swept over the shapes that broke them (the geometry audit of November 2026): every face together covering the footprint
/// once, each a simple polygon, nowhere below the eaves, faces meeting at their corners at one height but where a gable stands, and nowhere
/// far lower than its nearest sloping eave allows - a roof diving toward an eave that is not there.
///
/// Which side of the line between two planes at a corner an eave's roof is on was once asked at a point in from the edge, and that point lay
/// beyond the line wherever it left the corner at under 26.6 degrees - any corner sharper than 53 degrees, or 25 against 45 degrees of
/// pitch - so a triangle's roof lost a third of itself and a rectangle's ends were built ten metres up. And an eave at an inside corner beside
/// a gable had nothing to stop it, so it ran on under the other wing down to its own gutter height.
/// </summary>
public class RoofSweepTests
{
    private static Point2D P(double x, double y) => new(x, y);

    private static List<string> Faults(Point2D[] outline, Func<int, double?> pitch)
    {
        var roof = new Roof();
        roof.SetBoundary(outline);
        for (var i = 0; i < roof.Edges.Count; i++)
            if (pitch(i) is { } degrees)
            {
                roof.Edges[i].DefinesSlope = true;
                roof.Edges[i].SlopeDegrees = degrees;
            }

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 3000);
        var faults = new List<string>();
        var area = Polygon2D.Area(outline);
        if (Math.Abs(surface.Facets.Sum(facet => facet.Area) - area) > Math.Max(1000, area * 1e-6)) faults.Add($"faces cover {surface.Facets.Sum(facet => facet.Area) / 1e6:0.000} of {area / 1e6:0.000} m²");
        foreach (var facet in surface.Facets)
        {
            if (Polygon2D.Triangulate(facet.Outline).Count != facet.Outline.Count - 2) faults.Add("a face is not a simple polygon");
            if (facet.Points3D.Any(point => point.Z < 3000 - 1)) faults.Add("a face dips below the eaves");
        }

        var sloped = Enumerable.Range(0, outline.Length).Where(k => roof.Edges[k].DefinesSlope).ToList();
        var least = sloped.Min(k => Math.Tan(roof.Edges[k].SlopeDegrees * Math.PI / 180));
        var dives = BIMDesigner.Core.Energy.Daylight.Grid(outline, 250).Count(point =>
            surface.HeightAt(point) - 3000 < 0.5 * least * sloped.Min(k => Line2D.DistanceFromSegment(point, outline[k], outline[(k + 1) % outline.Length])) - 50);
        if (dives > 0) faults.Add($"{dives} points diving toward an eave that is not there");
        return faults;
    }

    public static IEnumerable<object[]> Triangles => new[]
    {
        new object[] { "acute", new[] { P(0, 0), P(10000, 0), P(3000, 8000) } },
        new object[] { "right", new[] { P(0, 0), P(8000, 0), P(0, 6000) } },
        new object[] { "obtuse", new[] { P(0, 0), P(12000, 0), P(9000, 3000) } },
        new object[] { "equilateral", new[] { P(0, 0), P(10000, 0), P(5000, 8660) } }
    };

    [Theory]
    [MemberData(nameof(Triangles))]
    public void ATriangleIsRoofedWhole(string name, Point2D[] outline)
    {
        foreach (var pitch in new[] { 20.0, 30, 45, 60 })
            Assert.True(Faults(outline, _ => pitch) is { Count: 0 }, $"{name} at {pitch}°: {string.Join("; ", Faults(outline, _ => pitch))}");

        // Its three planes meeting at one apex: at 30 degrees an acute triangle's ridge is its inradius times tan 30 over its eaves.
        if (name != "acute") return;
        var roof = new Roof();
        roof.SetBoundary(outline);
        foreach (var edge in roof.Edges)
        {
            edge.DefinesSlope = true;
            edge.SlopeDegrees = 30;
        }

        var sides = Enumerable.Range(0, 3).Select(i => outline[i].DistanceTo(outline[(i + 1) % 3])).ToList();
        var inradius = 2 * Polygon2D.Area(outline) / sides.Sum();
        Assert.Equal(inradius * Math.Tan(Math.PI / 6), RoofShape.Build(roof.Boundary, roof.Edges, 0).RidgeElevation, precision: 3);
    }

    public static IEnumerable<object[]> Quadrilaterals => new[]
    {
        new object[] { "rectangle", new[] { P(0, 0), P(10000, 0), P(10000, 6000), P(0, 6000) } },
        new object[] { "skewed quad", new[] { P(0, 0), P(11000, 1000), P(9000, 8000), P(1000, 6000) } },
        new object[] { "trapezoid", new[] { P(0, 0), P(12000, 0), P(9000, 6000), P(3000, 6000) } },
        new object[] { "kite", new[] { P(0, 0), P(6000, -3000), P(12000, 0), P(6000, 8000) } }
    };

    [Theory]
    [MemberData(nameof(Quadrilaterals))]
    public void HipsOfDifferentPitchesMeet(string name, Point2D[] outline)
    {
        foreach (var (low, high) in new[] { (25.0, 40.0), (25.0, 45.0), (20.0, 50.0), (40.0, 25.0), (30.0, 60.0) })
            Assert.True(Faults(outline, i => i % 2 == 0 ? low : high) is { Count: 0 }, $"{name} at {low}/{high}°: {string.Join("; ", Faults(outline, i => i % 2 == 0 ? low : high))}");
    }

    [Fact]
    public void ARectangleAt25And45DegreesHasItsEavesAtTheEaves()
    {
        // Long sides at 25 degrees, ends at 45: the ridge 3 m in at tan 25, the hips 1.4 m in from each end - every eave at the base.
        var roof = new Roof();
        roof.SetBoundary(new[] { P(0, 0), P(10000, 0), P(10000, 6000), P(0, 6000) });
        for (var i = 0; i < 4; i++)
        {
            roof.Edges[i].DefinesSlope = true;
            roof.Edges[i].SlopeDegrees = i % 2 == 0 ? 25 : 45;
        }

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);
        Assert.Equal(3000 * Math.Tan(25 * Math.PI / 180), surface.RidgeElevation, precision: 3);
        Assert.Equal(0, surface.HeightAt(P(0, 3000)), precision: 3);
        Assert.Equal(0, surface.HeightAt(P(10000, 3000)), precision: 3);
        Assert.Equal(500, surface.HeightAt(P(500, 3000)), precision: 3);
    }

    public static IEnumerable<object[]> Wings => new[]
    {
        new object[] { "L", new[] { P(0, 0), P(10000, 0), P(10000, 4000), P(4000, 4000), P(4000, 9000), P(0, 9000) } },
        new object[] { "T", new[] { P(0, 6000), P(4000, 6000), P(4000, 0), P(8000, 0), P(8000, 6000), P(12000, 6000), P(12000, 10000), P(0, 10000) } },
        new object[] { "U", new[] { P(0, 0), P(12000, 0), P(12000, 9000), P(8000, 9000), P(8000, 4000), P(4000, 4000), P(4000, 9000), P(0, 9000) } },
        new object[] { "H", new[] { P(0, 0), P(4000, 0), P(4000, 4000), P(8000, 4000), P(8000, 0), P(12000, 0), P(12000, 12000), P(8000, 12000), P(8000, 8000), P(4000, 8000), P(4000, 12000), P(0, 12000) } },
        new object[] { "cross", new[] { P(4000, 0), P(8000, 0), P(8000, 4000), P(12000, 4000), P(12000, 8000), P(8000, 8000), P(8000, 12000), P(4000, 12000), P(4000, 8000), P(0, 8000), P(0, 4000), P(4000, 4000) } },
        new object[] { "bay", new[] { P(0, 0), P(10000, 0), P(10000, 7000), P(7000, 7000), P(6000, 8500), P(4000, 8500), P(3000, 7000), P(0, 7000) } }
    };

    [Theory]
    [MemberData(nameof(Wings))]
    public void AnEaveBesideAGableAtAnInsideCornerStopsThere(string name, Point2D[] outline)
    {
        // Gables on every other edge, the rest at 35 degrees; then every edge hipped; then hipped at two pitches.
        Assert.True(Faults(outline, i => i % 2 == 0 ? 35 : null) is { Count: 0 }, $"{name} with gables: {string.Join("; ", Faults(outline, i => i % 2 == 0 ? 35 : null))}");
        Assert.True(Faults(outline, _ => 30) is { Count: 0 }, $"{name} hipped: {string.Join("; ", Faults(outline, _ => 30))}");
        Assert.True(Faults(outline, i => i % 2 == 0 ? 25 : 40) is { Count: 0 }, $"{name} at 25/40°: {string.Join("; ", Faults(outline, i => i % 2 == 0 ? 25 : 40))}");
    }

    [Fact]
    public void AnLsLowerRangeStopsAtTheGableOfItsWing()
    {
        // The L's lower range gabled at its east end, its north eave stopping at the wing's east gable at x 4 m; west of it the main
        // range's south slope runs on up to the wing's ridge, 4.5 m in.
        var roof = new Roof();
        roof.SetBoundary(new[] { P(0, 0), P(10000, 0), P(10000, 4000), P(4000, 4000), P(4000, 9000), P(0, 9000) });
        for (var i = 0; i < 6; i += 2)
        {
            roof.Edges[i].DefinesSlope = true;
            roof.Edges[i].SlopeDegrees = 35;
        }

        var surface = RoofShape.Build(roof.Boundary, roof.Edges, 0);
        var rise = Math.Tan(35 * Math.PI / 180);
        Assert.Equal(3900 * rise, surface.HeightAt(P(2000, 3900)), precision: 3);
        Assert.Equal(1000 * rise, surface.HeightAt(P(7000, 3000)), precision: 3);
        Assert.Equal(4500 * rise, surface.RidgeElevation, precision: 3);
    }
}
