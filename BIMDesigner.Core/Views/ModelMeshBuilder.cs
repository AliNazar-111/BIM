using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Views;

/// <summary>
/// Builds the 3D form of the model (specification section 6.1).
///
/// Like the section cut, nothing here is stored: the meshes are produced from the same walls,
/// levels and slabs every other view reads, whenever the 3D view asks. So the 3D view cannot
/// show a building the plan does not, and every height it draws is the height a schedule
/// would report.
///
/// It lives in Core, with no reference to any 3D library, so the same geometry can back an
/// IFC export or a web viewer without being worked out a second time.
/// </summary>
public static class ModelMeshBuilder
{
    private static readonly ColourRgb DefaultSurface = new(0xB0, 0xB0, 0xB0);
    private static readonly ColourRgb GlazingColour = new(0x8C, 0xC4, 0xE0);
    private static readonly ColourRgb LeafColour = new(0xA8, 0x84, 0x5C);

    /// <summary>Thickness of a door leaf and a pane of glass, in millimetres.</summary>
    private const double LeafThickness = 40;
    private const double PaneThickness = 12;

    public static IReadOnlyList<Mesh3D> Build(BimDocument document, Func<Element, bool>? shows = null)
    {
        shows ??= _ => true;

        var meshes = new List<Mesh3D>();

        foreach (var wall in document.Walls.Where(wall => shows(wall))) AddWall(document, wall, meshes);
        foreach (var slab in document.Elements.OfType<Slab>()) AddSlab(document, slab, meshes);

        return meshes.Where(mesh => !mesh.IsEmpty).ToList();
    }

    // ---- walls -----------------------------------------------------------------

    /// <summary>One wall's solids on their own: its layers, tiers and what fills its openings.</summary>
    public static IReadOnlyList<Mesh3D> BuildWall(BimDocument document, Wall wall)
    {
        var meshes = new List<Mesh3D>();
        AddWall(document, wall, meshes);
        return meshes.Where(mesh => !mesh.IsEmpty).ToList();
    }

    /// <summary>
    /// A wall as the constructions it is built of, each between its own heights, with the
    /// holes its doors and windows leave and what fills them.
    /// </summary>
    private static void AddWall(BimDocument document, Wall wall, List<Mesh3D> meshes)
    {
        if (wall.Length <= WallJoins.JoinTolerance) return;

        var bottom = wall.GetBaseElevation(document);
        var top = bottom + wall.GetHeight(document);
        if (top <= bottom) return;

        // Doors and windows are placed from the wall's base, whatever it is built of, and
        // cut through every tier they reach.
        var openings = new List<(Opening Opening, OpeningType Type, double From, double To, double Sill, double Head)>();

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

            var (from, to) = opening.GetSpan(openingType);
            from = Math.Max(0, from);
            to = Math.Min(wall.Length, to);
            if (to - from <= WallJoins.JoinTolerance) continue;

            var sill = Math.Clamp(bottom + opening.SillHeight, bottom, top);
            var head = Math.Clamp(bottom + opening.SillHeight + openingType.Height, bottom, top);
            openings.Add((opening, openingType, from, to, sill, head));
        }

        // Each construction the wall is made of, between its own heights: one for an ordinary
        // wall, one per tier for a stacked one.
        foreach (var (type, tierBottom, tierTop) in document.GetWallTiers(wall))
        {
            var first = meshes.Count;
            AddTier(document, wall, type, tierBottom, tierTop, openings, meshes);
            Lean(wall, type, bottom, meshes, first);
        }

        if (document.GetWallType(wall) is not { } planType) return;

        var infill = meshes.Count;
        foreach (var (opening, _, from, to, sill, head) in openings)
            AddOpeningInfill(wall, planType, opening, from, to, sill, head, meshes);

        // Doors and windows lean with the wall they are in.
        Lean(wall, planType, bottom, meshes, infill);
    }

    /// <summary>
    /// Leans or tapers what was just built of a wall: each point moved across the wall by its
    /// height above the base. Nothing happens to a vertical wall.
    /// </summary>
    private static void Lean(Wall wall, WallType type, double bottom, List<Mesh3D> meshes, int first)
    {
        if (!WallLean.Leans(wall, type)) return;

        for (var i = first; i < meshes.Count; i++)
        {
            meshes[i].Transform(point =>
            {
                var moved = WallLean.Move(wall, type, new Point2D(point.X, point.Y), point.Z - bottom);
                return new Point3D(moved.X, moved.Y, point.Z);
            });
        }
    }

    /// <summary>
    /// One construction of a wall between two heights, as its layers, with the holes its doors
    /// and windows leave.
    ///
    /// Each layer is its own mesh in its own material, for the same reason the plan draws
    /// them separately: an exterior wall is brick outside and plaster inside, and a 3D view
    /// that showed it as one grey block would be hiding the most useful thing about it.
    /// </summary>
    private static void AddTier(
        BimDocument document, Wall wall, WallType type, double bottom, double top,
        IReadOnlyList<(Opening Opening, OpeningType Type, double From, double To, double Sill, double Head)> openings,
        List<Mesh3D> meshes)
    {
        var structure = type.Structure;
        if (structure.TotalWidth <= 0 || top <= bottom) return;

        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var half = structure.TotalWidth / 2;

        // The pieces of wall, with the cut at each end: full height where it is solid, and
        // below the sill and above the head wherever something is hosted in it. The cutting is
        // worked out in one place so the plan and the 3D model cannot disagree about where a
        // wall starts and stops.
        var pieces = new List<(WallSlice Slice, double Bottom, double Top)>();

        foreach (var slice in WallSlices.Solid(document, wall, type))
            pieces.Add((slice, bottom, top));

        foreach (var (_, _, from, to, sill, head) in openings)
        {
            if (WallSlices.Between(document, wall, type, from, to, startCut, endCut) is not { } over) continue;

            var below = Math.Min(sill, top);
            var above = Math.Max(head, bottom);
            if (below > bottom) pieces.Add((over, bottom, below));
            if (above < top) pieces.Add((over, above, top));
        }

        // Reveals take the wall back from its face between their heights, so every piece is
        // built in bands: full thickness outside a reveal, thinner within one.
        var reveals = WallSweeps.Reveals(type, bottom, top);
        var breaks = reveals.SelectMany(r => new[] { r.Bottom, r.Top }).ToList();

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            // A membrane is a line with no volume: nothing to build or cut.
            if (layer.Thickness <= 0) continue;

            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                wall.Id, wall.LevelId, MeshKind.Wall,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            foreach (var (slice, pieceBottom, pieceTop) in pieces)
            {
                var heights = breaks.Where(z => z > pieceBottom && z < pieceTop)
                    .Append(pieceBottom).Append(pieceTop)
                    .Distinct().OrderBy(z => z).ToList();

                for (var i = 0; i + 1 < heights.Count; i++)
                {
                    if (WallSweeps.Recess(half - start, half - end, half, reveals, (heights[i] + heights[i + 1]) / 2)
                        is not var (outer, inner))
                        continue;

                    var outline = WallJoins.GetBandOutline(
                        wall, type, outer, inner, Unwrapped(slice.CutFrom), Unwrapped(slice.CutTo));

                    mesh.AddExtrusion(outline, heights[i], heights[i + 1]);
                }
            }

            meshes.Add(mesh);
        }

        AddSweeps(document, wall, type, bottom, top, meshes);
    }

    /// <summary>
    /// The sweeps of a wall type on this wall: each profile run along the face it belongs to,
    /// following the wall round a curve, stopping where a door or window reaches it.
    /// </summary>
    private static void AddSweeps(BimDocument document, Wall wall, WallType type, double bottom, double top, List<Mesh3D> meshes)
    {
        var structure = type.Structure;
        var half = type.Width / 2;
        var curve = wall.LocationCurve;

        foreach (var sweep in type.Sweeps.Where(s => s.Kind == SweepKind.Sweep))
        {
            var (sweepBottom, sweepTop) = sweep.Span(bottom, top);
            var sign = sweep.Side == WallSide.Exterior ? 1.0 : -1.0;
            var shape = sweep.Shape();

            Point3D At(double along, (double Out, double Up) corner)
            {
                var plan = wall.PointAt(structure, along, sign * (half + corner.Out * sweep.Depth));
                return new Point3D(plan.X, plan.Y, sweepBottom + corner.Up * sweep.Height);
            }

            var material = document.FindMaterial(sweep.MaterialId);
            var mesh = new Mesh3D(
                wall.Id, wall.LevelId, MeshKind.Sweep,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? "Sweep");

            foreach (var (from, to) in WallSweeps.Runs(document, wall, type, sweep.Side, sweepBottom, sweepTop))
            {
                var stations = new List<double> { from };
                stations.AddRange(curve.Between(from, to, wall.LeftOf(structure, sign * half)));
                stations.Add(to);

                for (var s = 0; s + 1 < stations.Count; s++)
                for (var k = 0; k < shape.Count; k++)
                {
                    var a = shape[k];
                    var b = shape[(k + 1) % shape.Count];
                    mesh.AddQuad(At(stations[s], a), At(stations[s + 1], a), At(stations[s + 1], b), At(stations[s], b));
                    mesh.AddEdge(At(stations[s], a), At(stations[s + 1], a));
                }

                // The profile itself, closing each end.
                var profile = shape.Select(p => new Point2D(p.Out, p.Up)).ToList();
                foreach (var along in new[] { from, to })
                {
                    foreach (var (i, j, k) in Polygon2D.Triangulate(profile))
                        mesh.AddTriangle(At(along, shape[i]), At(along, shape[j]), At(along, shape[k]));

                    for (var k = 0; k < shape.Count; k++)
                        mesh.AddEdge(At(along, shape[k]), At(along, shape[(k + 1) % shape.Count]));
                }
            }

            meshes.Add(mesh);
        }
    }

    /// <summary>
    /// A jamb without its wrapping. The finishes return into a reveal only as high as the
    /// opening; above it the wall carries on unbroken, and a solid built from the plan
    /// cut would run the return the full height of the wall.
    /// </summary>
    private static WallCut Unwrapped(WallCut cut) =>
        cut.Condition == WallEndCondition.Jamb ? new WallCut(cut.Points, cut.Condition) : cut;

    /// <summary>
    /// What fills a hole: a pane of glass for a window, a closed leaf for a door. Both sit in
    /// the middle of the wall, which is where a frame is fixed.
    /// </summary>
    private static void AddOpeningInfill(
        Wall wall, WallType type, Opening opening, double from, double to, double sill, double head,
        List<Mesh3D> meshes)
    {
        if (head <= sill) return;

        var structure = type.Structure;
        var isWindow = opening is Window;
        var half = (isWindow ? PaneThickness : LeafThickness) / 2;

        // Built in the wall's own frame, so in a curved wall the glass or leaf follows the
        // curve and fills the hole exactly.
        var curve = wall.LocationCurve;
        var stations = new List<double> { from };
        stations.AddRange(curve.Between(from, to));
        stations.Add(to);

        var outline = stations.Select(along => wall.PointAt(structure, along, half))
            .Concat(stations.AsEnumerable().Reverse().Select(along => wall.PointAt(structure, along, -half)))
            .ToArray();

        var mesh = new Mesh3D(
            opening.Id, opening.LevelId,
            isWindow ? MeshKind.Glazing : MeshKind.DoorLeaf,
            isWindow ? GlazingColour : LeafColour,
            isWindow ? "Glazing" : "Door leaf");

        mesh.AddExtrusion(outline, sill, head);
        meshes.Add(mesh);
    }

    // ---- slabs -----------------------------------------------------------------

    /// <summary>
    /// A floor, ceiling or roof as its layers, hanging down from its upper surface - the same
    /// convention the section uses, so the two views cannot disagree about where it sits.
    /// </summary>
    private static void AddSlab(BimDocument document, Slab slab, List<Mesh3D> meshes)
    {
        if (slab.Boundary.Count < 3) return;
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return;

        var surface = (document.FindLevel(slab.LevelId)?.Elevation ?? 0) + slab.HeightOffset;

        foreach (var (layer, start, end) in type.Structure.GetLayerOffsets())
        {
            // A membrane is a line with no volume: nothing to build or cut.
            if (layer.Thickness <= 0) continue;

            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                slab.Id, slab.LevelId,

                // Kept apart so a viewer can lift the roof off without losing the floors.
                slab switch
                {
                    Roof => MeshKind.Roof,
                    Ceiling => MeshKind.Ceiling,
                    _ => MeshKind.Floor
                },
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            mesh.AddExtrusion(slab.Boundary, surface - end, surface - start);
            meshes.Add(mesh);
        }
    }

    /// <summary>The box around the whole model, or null when there is nothing to draw.</summary>
    public static (Point3D Min, Point3D Max)? Bounds(IEnumerable<Mesh3D> meshes)
    {
        (Point3D Min, Point3D Max)? total = null;

        foreach (var bounds in meshes.Select(mesh => mesh.Bounds()))
        {
            if (bounds is not { } b) continue;

            total = total is not { } t
                ? b
                : (new Point3D(Math.Min(t.Min.X, b.Min.X), Math.Min(t.Min.Y, b.Min.Y), Math.Min(t.Min.Z, b.Min.Z)),
                   new Point3D(Math.Max(t.Max.X, b.Max.X), Math.Max(t.Max.Y, b.Max.Y), Math.Max(t.Max.Z, b.Max.Z)));
        }

        return total;
    }
}
