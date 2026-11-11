using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a glazing bar runs along: up a face, across it, or round its edge.</summary>
public enum GlazingBarRun
{
    /// <summary>Up the slope, from the eave toward the ridge: the bars the panes' long edges sit in.</summary>
    UpSlope,

    /// <summary>Across the slope, level: the transoms the panes' ends sit in.</summary>
    AcrossSlope,

    /// <summary>Round the face's edge: an eave, a verge, a hip, a ridge or a valley.</summary>
    Edge
}

/// <summary>A glazing bar on a face of a glazed roof: where it runs in plan, and the plane of the glass it holds.</summary>
public sealed record GlazingBar(Point2D From, Point2D To, GlazingBarRun Run, RoofPlane Glass)
{
    /// <summary>Its length on the face, mm - longer than in plan by the slope it runs up.</summary>
    public double Length
    {
        get
        {
            var rise = Glass.HeightAt(To) - Glass.HeightAt(From);
            return Math.Sqrt(From.DistanceTo(To) * From.DistanceTo(To) + rise * rise);
        }
    }
}

/// <summary>One face of a glazed roof: its region in plan, the plane of its glass's top, and the bars laid on it.</summary>
public sealed record GlazedFace(PolygonBoolean.Region Region, RoofPlane Glass, IReadOnlyList<GlazingBar> Bars)
{
    /// <summary>The face's glass, m² of the face itself - more than its plan by the slope.</summary>
    public double Area => (Polygon2D.Area(Region.Outer) - Region.Holes.Sum(Polygon2D.Area)) * Glass.VerticalStretch / 1e6;

    /// <summary>Up the slope, in plan: square to the eave the face rises from; up the plan for a flat one.</summary>
    public Vector2D Up => Glass.Rise < 1e-6 ? Vector2D.UnitY : new Vector2D(Glass.A / Glass.Rise, Glass.B / Glass.Rise);
}

/// <summary>
/// Sloped glazing (Revit's Sloped Glazing roof): a roof whose type is glass laid between glazing bars,
/// as a curtain wall is on a wall - on each face of the roof as its footprint and slopes make it.
///
/// Each face gets its own grid, set out in its own plane: bars running up the slope, spaced along
/// the eave at the type's spacing and centred on the face, so a symmetrical face is glazed
/// symmetrically; and bars running across it, the first a whole spacing up the face from its
/// lowest edge - so the bottom row of panes is whole where the water runs off - and on up it,
/// measured on the face, not in plan. Every edge of a face is a bar too: an eave, a verge, a hip,
/// a ridge, a valley, a hole's edge - one bar where two faces meet. The panes are what lies between.
/// </summary>
public static class GlazedRoofs
{
    /// <summary>How far a glazing bar's cap stands proud of the glass, mm.</summary>
    public const double CapHeight = 20;

    /// <summary>The type of a glazed roof, or null for a roof that is not one.</summary>
    public static RoofType? TypeOf(BimDocument document, Roof roof) =>
        document.FindType<SlabType>(roof.TypeId) is RoofType { SlopedGlazing: true } type ? type : null;

    public static bool IsGlazed(BimDocument document, Roof roof) => TypeOf(document, roof) is not null;

    /// <summary>
    /// The roof's faces, each with the bars laid on it: the pieces the roof is built from gathered by the
    /// plane of their glass - cut round dormers and the roofs it is joined to as it is built - and the
    /// grid set out on each. Empty for a roof that is not glazed.
    /// </summary>
    public static IReadOnlyList<GlazedFace> Faces(BimDocument document, Roof roof)
    {
        if (TypeOf(document, roof) is not { } type) return Array.Empty<GlazedFace>();

        var faces = new List<(PolygonBoolean.Region Region, RoofPlane Glass)>();
        foreach (var plane in RoofSolid.Pieces(document, roof).GroupBy(piece => (Math.Round(piece.Top.A, 6), Math.Round(piece.Top.B, 6), Math.Round(piece.Top.C, 1))))
        {
            // The pieces of one face, sliced at its eave, are one region again: each joined to those it meets.
            var regions = new List<PolygonBoolean.Region>();
            foreach (var piece in plane)
            {
                var current = new PolygonBoolean.Region(piece.Outline, Array.Empty<IReadOnlyList<Point2D>>());
                var apart = new List<PolygonBoolean.Region>();
                foreach (var region in regions)
                {
                    var joined = PolygonBoolean.Combine(Loops(region), Loops(current), BooleanOperation.Union);
                    if (joined.Count == 1) current = joined[0];
                    else apart.Add(region);
                }

                apart.Add(current);
                regions = apart;
            }

            var glass = plane.First().Top;
            faces.AddRange(regions.Where(region => Polygon2D.Area(region.Outer) > 1).Select(region => (region, glass)));
        }

        // Each edge once: where two faces meet - a hip, a ridge, a valley - one bar holds both.
        var edges = new HashSet<(long, long, long, long)>();
        bool First(Point2D a, Point2D b)
        {
            (long, long) Key(Point2D p) => ((long)Math.Round(p.X), (long)Math.Round(p.Y));
            var (ka, kb) = (Key(a), Key(b));
            var key = ka.CompareTo(kb) <= 0 ? (ka.Item1, ka.Item2, kb.Item1, kb.Item2) : (kb.Item1, kb.Item2, ka.Item1, ka.Item2);
            return edges.Add(key);
        }

        var laid = new List<GlazedFace>();
        foreach (var (region, glass) in faces)
        {
            var bars = new List<GlazingBar>();
            foreach (var loop in Loops(region))
                for (var i = 0; i < loop.Count; i++)
                {
                    var (a, b) = (loop[i], loop[(i + 1) % loop.Count]);
                    if (a.DistanceTo(b) > 1 && First(a, b)) bars.Add(new GlazingBar(a, b, GlazingBarRun.Edge, glass));
                }

            var face = new GlazedFace(region, glass, bars);
            var (up, across) = (face.Up, new Vector2D(face.Up.Y, -face.Up.X));
            var stretch = glass.VerticalStretch;
            var (sMin, sMax) = (region.Outer.Min(p => Along(p, across)), region.Outer.Max(p => Along(p, across)));
            var (tMin, tMax) = (region.Outer.Min(p => Along(p, up)), region.Outer.Max(p => Along(p, up)));
            var clear = type.GlazingBarWidth;

            // Up the slope: centred along the eave, none on top of an edge's.
            var spacing = Math.Max(1, type.UpBarSpacing);
            var count = (int)Math.Floor((sMax - sMin) / spacing);
            var first = sMin + (sMax - sMin - count * spacing) / 2;
            for (var k = 0; k <= count; k++)
            {
                var s = first + k * spacing;
                if (s - sMin < clear || sMax - s < clear) continue;
                Lay(bars, region, Point(across, s, up, tMin - 1), Point(across, s, up, tMax + 1), GlazingBarRun.UpSlope, glass);
            }

            // Across the slope: from the lowest edge up, the spacing measured on the face.
            var step = Math.Max(1, type.CrossBarSpacing) / stretch;
            for (var t = tMin + step; tMax - t >= clear / stretch; t += step)
                Lay(bars, region, Point(up, t, across, sMin - 1), Point(up, t, across, sMax + 1), GlazingBarRun.AcrossSlope, glass);

            laid.Add(face);
        }

        return laid;
    }

    private static IReadOnlyList<IReadOnlyList<Point2D>> Loops(PolygonBoolean.Region region) => new[] { region.Outer }.Concat(region.Holes).ToList();

    private static double Along(Point2D point, Vector2D way) => point.X * way.X + point.Y * way.Y;

    private static Point2D Point(Vector2D a, double alongA, Vector2D b, double alongB) =>
        new(a.X * alongA + b.X * alongB, a.Y * alongA + b.Y * alongB);

    /// <summary>A bar along a line, where the line is on the face: cut where it leaves it, or crosses a hole.</summary>
    private static void Lay(List<GlazingBar> bars, PolygonBoolean.Region region, Point2D from, Point2D to, GlazingBarRun run, RoofPlane glass)
    {
        IEnumerable<(Point2D From, Point2D To)> pieces = Polygon2D.Clip(from, to, region.Outer, inside: true);
        foreach (var hole in region.Holes)
            pieces = pieces.SelectMany(piece => Polygon2D.Clip(piece.From, piece.To, hole, inside: false)).ToList();
        foreach (var (a, b) in pieces)
            if (a.DistanceTo(b) > 1) bars.Add(new GlazingBar(a, b, run, glass));
    }

    /// <summary>How wide a bar is in plan, square to it, for a width on the face: narrower where it lies across the slope.</summary>
    public static double PlanWidth(GlazingBar bar, double width)
    {
        var way = (bar.To - bar.From).NormalisedOrDefault(Vector2D.UnitX);
        var square = way.PerpendicularLeft();
        var climb = bar.Glass.A * square.X + bar.Glass.B * square.Y;
        return width / Math.Sqrt(1 + climb * climb);
    }

    /// <summary>The bar's footprint in plan, its width on the face either side of its line.</summary>
    public static IReadOnlyList<Point2D> Footprint(GlazingBar bar, double width)
    {
        var square = (bar.To - bar.From).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (PlanWidth(bar, width) / 2);
        return new[] { bar.From - square, bar.To - square, bar.To + square, bar.From + square };
    }

    /// <summary>All the bars on a glazed roof, m on the faces.</summary>
    public static double BarLength(BimDocument document, Roof roof) => Faces(document, roof).Sum(face => face.Bars.Sum(bar => bar.Length)) / 1000;

    /// <summary>The roof's glass, m² of its faces.</summary>
    public static double Area(BimDocument document, Roof roof) => Faces(document, roof).Sum(face => face.Area);

    /// <summary>
    /// The share of a glazed roof that is glass and not bar - its frame factor: one less the bars' width
    /// times their length over the faces' area. Edge bars count half, the other half over what the
    /// face meets.
    /// </summary>
    public static double GlassShare(BimDocument document, Roof roof)
    {
        if (TypeOf(document, roof) is not { } type) return 1;
        var faces = Faces(document, roof);
        var area = faces.Sum(face => face.Area) * 1e6;
        if (area <= 0) return 1;
        var covered = faces.Sum(face => face.Bars.Sum(bar => bar.Length * type.GlazingBarWidth * (bar.Run == GlazingBarRun.Edge ? 0.5 : 1)));
        return Math.Clamp(1 - covered / area, 0, 1);
    }

    /// <summary>
    /// The panes: each face cut along its bars into the pieces of glass between them - whole panes
    /// in the middle, raked ones at a hip or a verge. How many, and the biggest, m² on the face.
    /// </summary>
    public static (int Count, double Largest) Panes(BimDocument document, Roof roof)
    {
        if (TypeOf(document, roof) is not { } type) return (0, 0);
        var (count, largest) = (0, 0.0);
        foreach (var face in Faces(document, roof))
        {
            var (up, across) = (face.Up, new Vector2D(face.Up.Y, -face.Up.X));
            var cuts = face.Bars.Where(bar => bar.Run != GlazingBarRun.Edge).ToList();
            var ss = cuts.Where(bar => bar.Run == GlazingBarRun.UpSlope).Select(bar => Math.Round(Along(bar.From, across), 3)).Distinct().OrderBy(s => s).ToList();
            var ts = cuts.Where(bar => bar.Run == GlazingBarRun.AcrossSlope).Select(bar => Math.Round(Along(bar.From, up), 3)).Distinct().OrderBy(t => t).ToList();
            var outer = face.Region.Outer;
            var (sMin, sMax) = (outer.Min(p => Along(p, across)) - 1, outer.Max(p => Along(p, across)) + 1);
            var (tMin, tMax) = (outer.Min(p => Along(p, up)) - 1, outer.Max(p => Along(p, up)) + 1);
            var sCuts = new[] { sMin }.Concat(ss).Append(sMax).ToList();
            var tCuts = new[] { tMin }.Concat(ts).Append(tMax).ToList();
            for (var i = 0; i + 1 < sCuts.Count; i++)
            for (var j = 0; j + 1 < tCuts.Count; j++)
            {
                var cell = new[]
                {
                    Point(across, sCuts[i], up, tCuts[j]), Point(across, sCuts[i + 1], up, tCuts[j]),
                    Point(across, sCuts[i + 1], up, tCuts[j + 1]), Point(across, sCuts[i], up, tCuts[j + 1])
                };
                if (Polygon2D.SignedArea(cell) < 0) Array.Reverse(cell);
                foreach (var pane in PolygonBoolean.Combine(Loops(face.Region), new[] { (IReadOnlyList<Point2D>)cell }, BooleanOperation.Intersection))
                {
                    var area = (Polygon2D.Area(pane.Outer) - pane.Holes.Sum(Polygon2D.Area)) * face.Glass.VerticalStretch / 1e6;
                    if (area < 0.01) continue;
                    count++;
                    largest = Math.Max(largest, area);
                }
            }
        }

        return (count, largest);
    }

    /// <summary>
    /// The bars as built: each a box its width on the face, its cap standing proud of the glass and the
    /// rest of its depth under it, square to the face - for the 3D model.
    /// </summary>
    public static Mesh3D? BarMesh(BimDocument document, Roof roof)
    {
        if (TypeOf(document, roof) is not { } type) return null;
        var material = document.FindMaterial(type.GlazingBarMaterialId);
        var mesh = new Mesh3D(roof.Id, roof.LevelId, MeshKind.Mullion, material?.SurfaceColour ?? BarColour, material?.Name ?? "Glazing Bars");
        var below = Math.Max(0, type.GlazingBarDepth - CapHeight);
        foreach (var face in Faces(document, roof))
        {
            var stretch = face.Glass.VerticalStretch;
            foreach (var bar in face.Bars)
                mesh.AddExtrusion(Footprint(bar, type.GlazingBarWidth), point => face.Glass.HeightAt(point) - below * stretch, point => face.Glass.HeightAt(point) + CapHeight * stretch);
        }

        return mesh.IsEmpty ? null : mesh;
    }

    private static readonly Materials.ColourRgb BarColour = Materials.ColourRgb.FromHex("B8BCC2");
}
