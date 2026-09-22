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

    public static IReadOnlyList<Mesh3D> Build(BimDocument document, Func<Element, bool>? shows = null)
    {
        shows ??= _ => true;

        var meshes = new List<Mesh3D>();

        foreach (var wall in document.Walls.Where(wall => shows(wall))) AddWall(document, wall, meshes);
        foreach (var slab in document.Elements.OfType<Slab>()) AddSlab(document, slab, meshes);

        return Finished(meshes);
    }

    /// <summary>The meshes worth drawing, with the seams between their blocks taken out.</summary>
    private static IReadOnlyList<Mesh3D> Finished(List<Mesh3D> meshes)
    {
        var drawn = meshes.Where(mesh => !mesh.IsEmpty).ToList();
        foreach (var mesh in drawn) mesh.RemoveSeams();
        return drawn;
    }

    // ---- walls -----------------------------------------------------------------

    /// <summary>One wall's solids on their own: its layers, tiers and what fills its openings.</summary>
    public static IReadOnlyList<Mesh3D> BuildWall(BimDocument document, Wall wall)
    {
        var meshes = new List<Mesh3D>();
        AddWall(document, wall, meshes);
        return Finished(meshes);
    }

    /// <summary>
    /// A wall as the constructions it is built of, each between its own heights, with the
    /// holes its doors and windows leave and what fills them.
    /// </summary>
    private static void AddWall(BimDocument document, Wall wall, List<Mesh3D> meshes)
    {
        if (wall.Length <= WallJoins.JoinTolerance) return;

        if (CurtainLayout.Of(document, wall) is { } curtain)
        {
            AddCurtainWall(document, wall, curtain, meshes);
            return;
        }

        var bottom = wall.GetBaseElevation(document);

        // An edited profile says how high the wall is, and may dip below its base.
        var profile = WallProfile.Of(document, wall);
        var top = profile is null ? bottom + wall.GetHeight(document) : bottom + WallProfile.Top(profile);
        var lowest = profile is null ? bottom : Math.Min(bottom, bottom + WallProfile.Bottom(profile));
        if (top <= lowest) return;

        // Doors and windows are placed from the wall's base, whatever it is built of, and
        // cut through every tier they reach.
        var openings = new List<(Opening? Opening, OpeningType? Type, double From, double To, double Sill, double Head)>();

        foreach (var opening in WallOpenings.Of(document, wall))
        {
            if (document.FindType<OpeningType>(opening.TypeId) is not { } openingType) continue;

            var (from, to) = opening.GetSpan(openingType);
            from = Math.Max(0, from);
            to = Math.Min(wall.Length, to);
            if (to - from <= WallJoins.JoinTolerance) continue;

            var sill = Math.Clamp(bottom + opening.SillHeight, lowest, top);
            var head = Math.Clamp(bottom + opening.SillHeight + openingType.Height, lowest, top);
            openings.Add((opening, openingType, from, to, sill, head));
        }

        // Curtain walls set into this one leave a hole with nothing of this wall's in it.
        foreach (var hole in CurtainEmbedding.In(document, wall))
            openings.Add((null, null, hole.From, hole.To, Math.Clamp(bottom + hole.Sill, lowest, top), Math.Clamp(bottom + hole.Head, lowest, top)));

        if (profile is not null && document.GetWallType(wall) is { } profiledType)
        {
            AddProfiled(document, wall, profiledType, bottom, profile, openings, meshes);
            AddSweeps(document, wall, profiledType, bottom, top, meshes);
        }

        // Each construction the wall is made of, between its own heights: one for an ordinary
        // wall, one per tier for a stacked one.
        foreach (var (type, tierBottom, tierTop) in document.GetWallTiers(wall).Where(_ => profile is null))
        {
            var first = meshes.Count;
            AddTier(document, wall, type, tierBottom, tierTop, openings, meshes);
            Lean(wall, type, bottom, meshes, first);
        }

        if (document.GetWallType(wall) is not { } planType) return;

        var infill = meshes.Count;
        foreach (var (opening, openingType, from, to, sill, head) in openings)
        {
            if (opening is not null && openingType is not null)
                OpeningModel.Add(wall, planType, opening, openingType, from, to, sill, head, meshes);
        }

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
        IReadOnlyList<(Opening? Opening, OpeningType? Type, double From, double To, double Sill, double Head)> openings,
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
    /// A curtain wall as what it is made of: a pane in each cell - glass, a solid panel, a door
    /// or nothing - and the mullions framing them, square or round, all following the wall
    /// round a curve.
    /// </summary>
    private static void AddCurtainWall(BimDocument document, Wall wall, CurtainLayout layout, List<Mesh3D> meshes)
    {
        var type = layout.Type;
        var body = type.Body;
        var bottom = wall.GetBaseElevation(document);
        var half = type.PanelThickness / 2;

        Mesh3D NewMesh(MeshKind kind, Guid materialId, ColourRgb fallback)
        {
            var material = document.FindMaterial(materialId);
            return new Mesh3D(wall.Id, wall.LevelId, kind, material?.SurfaceColour ?? fallback, material?.Name ?? kind.ToString());
        }

        var glass = NewMesh(MeshKind.Glazing, type.GlassMaterialId, GlazingColour);
        var solid = NewMesh(MeshKind.Wall, type.SolidMaterialId, DefaultSurface);
        var doors = NewMesh(MeshKind.DoorLeaf, type.MullionMaterialId, LeafColour);
        var frame = NewMesh(MeshKind.Mullion, type.MullionMaterialId, DefaultSurface);

        foreach (var cell in layout.Cells)
        {
            if (cell.ClearTo - cell.ClearFrom <= 1e-6 || cell.ClearTop - cell.ClearBottom <= 1e-6) continue;

            var mesh = cell.Kind switch
            {
                CurtainPanelKind.Glazed => glass,
                CurtainPanelKind.Solid => solid,
                CurtainPanelKind.Door => doors,
                _ => null
            };
            if (mesh is null) continue;

            if (cell.Kind == CurtainPanelKind.Door)
            {
                AddCurtainDoor(wall, body, cell, bottom, doors, glass);
                continue;
            }

            mesh.AddExtrusion(CurtainGeometry.Band(wall, body, cell.ClearFrom, cell.ClearTo, half, -half),
                bottom + cell.ClearBottom, bottom + cell.ClearTop);
        }

        var radius = type.MullionWidth / 2;
        var depth = type.MullionDepth / 2;

        foreach (var mullion in layout.Mullions)
        {
            if (type.MullionProfile == MullionProfile.Rectangular)
            {
                frame.AddExtrusion(CurtainGeometry.Band(wall, body, mullion.From, mullion.To, depth, -depth),
                    bottom + mullion.Bottom, bottom + mullion.Top);
            }
            else if (mullion.IsVertical)
            {
                frame.AddExtrusion(CurtainGeometry.Circle(wall, body, (mullion.From + mullion.To) / 2, radius),
                    bottom + mullion.Bottom, bottom + mullion.Top);
            }
            else
            {
                AddTube(frame, CurtainGeometry.Path(wall, body, mullion.From, mullion.To),
                    bottom + (mullion.Bottom + mullion.Top) / 2, radius);
            }
        }

        meshes.AddRange(new[] { glass, solid, doors, frame });
    }

    /// <summary>
    /// A door in a curtain wall's panel: a glazed leaf - a slim frame round a pane - with a
    /// long pull handle on each face, the way storefront doors are made.
    /// </summary>
    private static void AddCurtainDoor(Wall wall, WallType body, CurtainCell cell, double bottom, Mesh3D leaf, Mesh3D glass)
    {
        const double stile = 90;
        const double rail = 110;
        const double depth = 25;

        var (u0, u1, z0, z1) = (cell.ClearFrom, cell.ClearTo, bottom + cell.ClearBottom, bottom + cell.ClearTop);
        if (u1 - u0 <= 2 * stile || z1 - z0 <= 2 * rail)
        {
            leaf.AddExtrusion(CurtainGeometry.Band(wall, body, u0, u1, depth, -depth), z0, z1);
            return;
        }

        void Part(Mesh3D mesh, double a0, double a1, double outer, double inner, double low, double high) =>
            mesh.AddExtrusion(CurtainGeometry.Band(wall, body, a0, a1, outer, inner), low, high);

        Part(leaf, u0, u0 + stile, depth, -depth, z0, z1);
        Part(leaf, u1 - stile, u1, depth, -depth, z0, z1);
        Part(leaf, u0 + stile, u1 - stile, depth, -depth, z1 - rail, z1);
        Part(leaf, u0 + stile, u1 - stile, depth, -depth, z0, z0 + rail * 1.6);
        Part(glass, u0 + stile, u1 - stile, 6, -6, z0 + rail * 1.6, z1 - rail);

        // A pull bar on each face, near the leaf's free edge.
        var at = u1 - stile / 2;
        var middle = z0 + Math.Min(1000, (z1 - z0) / 2);
        foreach (var side in new[] { 1.0, -1.0 })
            Part(leaf, at - 12, at + 12, side * (depth + 55), side * (depth + 35), middle - 350, middle + 350);
    }

    /// <summary>A round bar lying along a path at one height: a round transom.</summary>
    private static void AddTube(Mesh3D mesh, IReadOnlyList<(Point2D Point, Vector2D Across)> path, double height, double radius)
    {
        if (path.Count < 2 || radius <= 0) return;

        const int sides = CurtainGeometry.RoundSides;
        Point3D[] Ring((Point2D Point, Vector2D Across) at) =>
            Enumerable.Range(0, sides)
                .Select(i => 2 * Math.PI * i / sides)
                .Select(angle =>
                {
                    var sideways = at.Across * (radius * Math.Cos(angle));
                    return new Point3D(at.Point.X + sideways.X, at.Point.Y + sideways.Y, height + radius * Math.Sin(angle));
                })
                .ToArray();

        var rings = path.Select(Ring).ToList();
        for (var k = 0; k + 1 < rings.Count; k++)
        for (var i = 0; i < sides; i++)
        {
            var j = (i + 1) % sides;
            mesh.AddQuad(rings[k][i], rings[k + 1][i], rings[k + 1][j], rings[k][j]);
        }

        // Closed at both ends.
        foreach (var (ring, flip) in new[] { (rings[0], true), (rings[^1], false) })
        for (var i = 1; i + 1 < sides; i++)
        {
            if (flip) mesh.AddTriangle(ring[0], ring[i + 1], ring[i]);
            else mesh.AddTriangle(ring[0], ring[i], ring[i + 1]);
        }

        mesh.AddEdge(new Point3D(path[0].Point.X, path[0].Point.Y, height + radius), new Point3D(path[^1].Point.X, path[^1].Point.Y, height + radius));
    }

    /// <summary>
    /// A wall with an edited profile, as its layers: the outline in vertical strips, each built
    /// like an ordinary stretch of wall - joined at the wall's ends, square at a door or window -
    /// but running from the strip's own bottom edge to its own top edge, which may slope.
    /// </summary>
    private static void AddProfiled(
        BimDocument document, Wall wall, WallType type, double baseElevation, IReadOnlyList<Point2D> profile,
        IReadOnlyList<(Opening? Opening, OpeningType? Type, double From, double To, double Sill, double Head)> openings,
        List<Mesh3D> meshes)
    {
        var structure = type.Structure;
        if (structure.TotalWidth <= 0) return;

        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, type);
        var half = structure.TotalWidth / 2;

        var holes = openings.Select(o => (o.From, o.To, o.Sill - baseElevation, o.Head - baseElevation));
        var strips = WallProfile.Strips(profile, wall.Length, holes)
            .Select(strip => (Strip: strip, Slice: WallSlices.Between(document, wall, type, strip.From, strip.To, startCut, endCut)))
            .Where(entry => entry.Slice is not null)
            .ToList();

        double Along(Point2D point) => wall.Locate(structure, point).Along;

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            if (layer.Thickness <= 0) continue;

            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                wall.Id, wall.LevelId, MeshKind.Wall,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            foreach (var (strip, slice) in strips)
            {
                var outline = WallJoins.GetBandOutline(
                    wall, type, half - start, half - end, Unwrapped(slice!.Value.CutFrom), Unwrapped(slice.Value.CutTo));

                // Straight top and bottom edges in elevation are planes across the plan outline,
                // so each corner takes its height from how far along the wall it is.
                mesh.AddExtrusion(outline,
                    point => baseElevation + strip.Bottom(Along(point)),
                    point => baseElevation + strip.Top(Along(point)));
            }

            meshes.Add(mesh);
        }
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
