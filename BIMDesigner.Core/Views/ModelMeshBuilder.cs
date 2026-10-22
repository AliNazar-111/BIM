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
        using var heights = new Chimneys.Heights();
        using var joins = Architecture.WallJoins.TakingPart(shows);
        using var cache = ModelCache.Begin(document);
        var was = _shows;
        _shows = shows;
        try
        {
            return BuildShown(document, shows);
        }
        finally
        {
            _shows = was;
        }
    }

    // What the model is being built to show, for a wall to leave a door or window not shown as the hole it is in.
    [ThreadStatic] private static Func<Element, bool>? _shows;

    private static IReadOnlyList<Mesh3D> BuildShown(BimDocument document, Func<Element, bool> shows)
    {
        var meshes = new List<Mesh3D>();

        // Each element's solids built in turn, and one that cannot be built left out - and said
        // so - with whatever it had got as far as, rather than the whole model with it.
        void Each<T>(IEnumerable<T> elements, Action<T> add) where T : Element
        {
            foreach (var element in elements)
            {
                if (!shows(element)) continue;
                var before = meshes.Count;
                if (!DrawFailures.Guard(element, () => add(element))) meshes.RemoveRange(before, meshes.Count - before);
            }
        }

        Each(document.Walls, wall => AddWall(document, wall, meshes));
        Each(document.All<Slab>(), slab => AddSlab(document, slab, meshes));
        Each(document.All<Column>(), column => AddColumn(document, column, meshes));

        // Footings under the walls and columns, with the walls and piers on them.
        Each(document.Elements.Where(e => e is WallFooting or PadFooting), footing => meshes.AddRange(Footings.Meshes(document, footing)));

        Each(document.All<Component>(), component => AddComponent(document, component, meshes));

        // Roof windows in the roofs, on the storey of their roof.
        Each(document.All<RoofWindow>(), window => meshes.AddRange(RoofWindows.Meshes(document, window)));

        // Fascias and gutters along the roofs' edges, on the storey of the roof they follow.
        Each(document.All<RoofEdgeSweep>(), sweep =>
        {
            var levelId = document.Find<Roof>(sweep.RoofId)?.LevelId ?? sweep.LevelId;

            // An eave is built of several things - rafters, soffit, fire-stopping - each in its own material.
            if (sweep is Eave eave) meshes.AddRange(Eaves.Meshes(document, eave, levelId));
            else if (RoofEdgeSweeps.Mesh(document, sweep, levelId) is { IsEmpty: false } mesh) meshes.Add(mesh);
        });

        // The downpipes taking the water down from them.
        Each(document.All<Downpipe>(), pipe =>
        {
            if (Downpipes.Mesh(document, pipe) is { } pipeMesh) meshes.Add(pipeMesh);
        });
        Each(document.All<SlabEdge>(), slabEdge =>
        {
            if (SlabEdgeRuns.Mesh(document, slabEdge) is { } edgeMesh) meshes.Add(edgeMesh);
        });

        // And the drains in flat roofs, with the pipes taking their water away.
        Each(document.All<RoofDrain>(), drain =>
        {
            if (RoofDrainage.Mesh(document, drain) is { } drainMesh) meshes.Add(drainMesh);
            if (RoofDrainage.PipeMesh(document, drain) is { } pipeMesh) meshes.Add(pipeMesh);
        });

        // Chimneys, through the roofs they rise through.
        Each(document.All<Chimney>(), chimney => meshes.AddRange(Chimneys.Meshes(document, chimney)));

        // Stairs, up through the openings they cut.
        Each(document.All<Stair>(), stair => meshes.AddRange(StairModel.Meshes(document, stair)));

        // Ramps, up to the doors and terraces they serve.
        Each(document.All<Ramp>(), ramp => meshes.AddRange(RampModel.Meshes(document, ramp)));

        // Railings round balconies, terraces and floors' edges.
        Each(document.All<Railing>(), railing => meshes.AddRange(RailingModel.Meshes(document, railing)));

        // The ground, cut away under the building.
        Each(document.All<Toposurface>(), ground =>
            meshes.AddRange(Terrains.Meshes(document, ground).Where(mesh => mesh.ElementId == ground.Id
                || document.All<GroundRegion>().Any(region => region.Id == mesh.ElementId && shows(region))
                || document.All<SitePad>().Any(pad => pad.Id == mesh.ElementId && shows(pad)))));

        // Beams, under the floors and roofs they carry.
        Each(document.All<Beam>(), beam =>
        {
            if (Beams.Mesh(document, beam) is { } beamMesh) meshes.Add(beamMesh);
        });

        // Joist layouts: their joists and trimmers.
        Each(document.All<BeamSystem>(), system => meshes.AddRange(BeamSystems.Meshes(document, system)));

        // Masses, see-through: the shape of the building before it is one.
        Each(document.All<Mass>().Where(m => m.Boundary.Count >= 3), mass => meshes.Add(Masses.Mesh(document, mass)));

        // Walls shaped in plan - trapezoids and polygons - stood up from their outlines.
        Each(document.All<PolygonWall>(), shaped =>
        {
            if (PolygonWalls.Mesh(document, shaped) is { } shapedMesh) meshes.Add(shapedMesh);
        });

        return Finished(meshes);
    }

    /// <summary>One column's solid on its own: its section, run between what it stands on and what it meets.</summary>
    public static IReadOnlyList<Mesh3D> BuildColumn(BimDocument document, Column column)
    {
        var meshes = new List<Mesh3D>();
        AddColumn(document, column, meshes);
        return Finished(meshes);
    }

    /// <summary>
    /// An architectural column: its section extruded from its base to its top, which its
    /// constraints and attachments decide rather than a height it carries. Built into a wall, it
    /// is drawn as that wall's material, because that is what it is made of.
    /// </summary>
    private static void AddColumn(BimDocument document, Column column, List<Mesh3D> meshes)
    {
        if (document.FindType<ColumnType>(column.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0) return;

        var bottom = column.GetBaseElevation(document);
        var top = column.GetTopElevation(document);
        if (top - bottom <= 1e-6) return;

        var material = document.FindMaterial(ColumnJoins.CutMaterial(document, column, type));
        var mesh = new Mesh3D(column.Id, column.LevelId, MeshKind.Wall,
            material?.SurfaceColour ?? DefaultSurface, material?.Name ?? type.Name);

        // Built from the rings its type's shaping asks for - two for a straight prism, a stack
        // for one that tapers, twists or leans, and more again where it has a base and capital.
        // Holes in the section are holes in the solid, so what is cut in plan is cut in 3D.
        var rings = ColumnGeometry.Rings(type, bottom, top)
            .Select(ring =>
            {
                // Where it stands, less whatever wall it is buried in at that height.
                var placed = ring.Profile.PlacedAt(column.Location, column.Across);
                var clear = ColumnJoins.CutByWalls(document, column, type, placed, ring.Elevation);

                return (clear.Outer, clear.Holes, ring.Elevation);
            })
            .Where(ring => ring.Outer.Count >= 3)
            .ToList();

        mesh.AddLoft(rings);
        meshes.Add(mesh);
    }

    /// <summary>The sides a round part of a component is built with: each turning less than a crease does, so its side is one curve.</summary>
    private const int RoundSides = 32;

    /// <summary>One component's solids on their own: what its family is shaped like, where it stands.</summary>
    public static IReadOnlyList<Mesh3D> BuildComponent(BimDocument document, Component component)
    {
        var meshes = new List<Mesh3D>();
        AddComponent(document, component, meshes);
        return Finished(meshes);
    }

    /// <summary>
    /// A component as the parts its form is made of, turned to face the way it faces and stood
    /// on its level - or at its fixing height on the wall carrying it. The parts in its type's
    /// own colour or material are one solid; an appliance's white enamel, black glass and
    /// stainless steel are a solid each, in their own colours.
    /// </summary>
    private static void AddComponent(BimDocument document, Component component, List<Mesh3D> meshes)
    {
        if (document.FindType<ComponentType>(component.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0 || type.Height <= 0) return;

        var frame = ComponentModel.FrameOf(component, ComponentHosting.BaseElevation(document, component, type));

        var material = document.FindMaterial(type.MaterialId);
        var finishes = new Dictionary<ComponentModel.PartFinish, Mesh3D>();
        Mesh3D In(ComponentModel.PartFinish finish)
        {
            if (finishes.TryGetValue(finish, out var known)) return known;

            var (colour, name) = finish == ComponentModel.PartFinish.Body
                ? (material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name)
                : ComponentModel.Look(finish);
            // A shower's glass is glass: seen through, and cut as glazing in a section.
            var kind = finish == ComponentModel.PartFinish.Glass ? MeshKind.Glazing : MeshKind.Wall;
            var mesh = new Mesh3D(component.Id, component.LevelId, kind, colour, name);
            finishes[finish] = mesh;
            meshes.Add(mesh);
            return mesh;
        }

        // Its own colour first: the solid a picked component is known by.
        In(ComponentModel.PartFinish.Body);

        foreach (var part in ComponentModel.Parts(type))
        {
            var mesh = In(part.Finish);
            switch (part.Shape)
            {
                case ComponentModel.PartShape.Upright:
                    // Fine enough that its side reads as one curve, with no line up every facet.
                    mesh.AddExtrusion(frame.Ellipse(part.From, part.To, part.Back, part.Front, RoundSides), frame.Base + part.Bottom, frame.Base + part.Top);
                    break;

                case ComponentModel.PartShape.Facing:
                {
                    // Built lying down - across and up as if they were a plan, front to back as
                    // its height - then stood up facing the way the component faces. A mirrored
                    // one has its disc on the other hand, built there and stood up unmirrored, so
                    // its faces stay facing out.
                    var disc = new Mesh3D(component.Id, component.LevelId, MeshKind.Wall, mesh.Colour, mesh.Description);
                    var across = (part.From + part.To) / 2 * (frame.Mirrored ? -1 : 1);
                    var up = (part.Bottom + part.Top) / 2;
                    var upright = frame.Unmirrored;
                    var (radiusAcross, radiusUp) = ((part.To - part.From) / 2, (part.Top - part.Bottom) / 2);
                    var ring = Enumerable.Range(0, RoundSides)
                        .Select(i => 2 * Math.PI * i / RoundSides)
                        .Select(angle => new Point2D(across + radiusAcross * Math.Cos(angle), up + radiusUp * Math.Sin(angle)))
                        .ToList();
                    disc.AddExtrusion(ring, part.Back, part.Front);
                    disc.Transform(point => Point3D.On(upright.Plan(point.X, point.Z), upright.Base + point.Y));
                    mesh.Append(disc);
                    break;
                }

                case ComponentModel.PartShape.Sphere:
                    AddTurned(mesh, frame, part, Enumerable.Range(0, 13)
                        .Select(k => ((1 - Math.Cos(Math.PI * k / 12)) / 2, Math.Sin(Math.PI * k / 12)))
                        .ToList());
                    break;

                case ComponentModel.PartShape.Cone:
                    AddTurned(mesh, frame, part, new[] { (0.0, 1.0), (1.0, 0.0) });
                    break;

                case ComponentModel.PartShape.Wedge:
                {
                    // Its full height at the back, sloping down to nothing at the front.
                    double Forward(Point2D point) => (point - frame.Centre).Dot(frame.Facing);
                    var rise = part.Top - part.Bottom;
                    mesh.AddExtrusion(frame.Outline(part.From, part.To, part.Back, part.Front),
                        _ => frame.Base + part.Bottom,
                        point => frame.Base + part.Bottom + Math.Max(0.5, rise * Math.Clamp((part.Front - Forward(point)) / (part.Front - part.Back), 0, 1)));
                    break;
                }

                default:
                    mesh.AddExtrusion(frame.Outline(part.From, part.To, part.Back, part.Front), frame.Base + part.Bottom, frame.Base + part.Top);
                    break;
            }
        }

        // A 3D model loaded into it: its triangles at the family's size, the way it faces -
        // wound the other way round for a mirrored one, so its faces still face out - with the
        // creases a drawing shows.
        if (type.Mesh is { } model)
        {
            var mesh = In(ComponentModel.PartFinish.Body);
            var placed = model.Positions
                .Select(p => Point3D.On(frame.Plan(p.X * type.Width, p.Y * type.Depth), frame.Base + p.Z * type.Height))
                .ToArray();

            var triangles = model.Triangles;
            for (var t = 0; t + 2 < triangles.Count; t += 3)
            {
                var (a, b, c) = (placed[triangles[t]], placed[triangles[t + 1]], placed[triangles[t + 2]]);
                if (frame.Mirrored) mesh.AddTriangle(a, c, b);
                else mesh.AddTriangle(a, b, c);
            }

            for (var e = 0; e + 1 < model.Edges.Count; e += 2)
                mesh.AddEdge(placed[model.Edges[e]], placed[model.Edges[e + 1]]);
        }
    }

    /// <summary>
    /// A solid turned about the upright through the middle of a part's box - a ball, a cone - as
    /// rings up its height, each a share of the box's spread: closed flat where an end ring has
    /// any size, coming to a point where it has none.
    /// </summary>
    private static void AddTurned(Mesh3D mesh, ComponentModel.Frame frame, ComponentModel.Part part, IReadOnlyList<(double Up, double Spread)> profile)
    {
        var (across, forward) = ((part.From + part.To) / 2, (part.Back + part.Front) / 2);
        var (radiusAcross, radiusForward) = ((part.To - part.From) / 2, (part.Front - part.Back) / 2);
        var height = part.Top - part.Bottom;
        var middle = Point3D.On(frame.Plan(across, forward), frame.Base + part.Bottom + height / 2);

        Point3D At(int ring, int side)
        {
            var angle = 2 * Math.PI * side / RoundSides;
            var (up, spread) = profile[ring];
            return Point3D.On(
                frame.Plan(across + radiusAcross * spread * Math.Cos(angle), forward + radiusForward * spread * Math.Sin(angle)),
                frame.Base + part.Bottom + height * up);
        }

        // Out from its middle, through the middle of a face.
        Point3D Outward(params Point3D[] corners) => new(
            corners.Average(p => p.X) - middle.X, corners.Average(p => p.Y) - middle.Y, corners.Average(p => p.Z) - middle.Z);

        for (var ring = 0; ring + 1 < profile.Count; ring++)
        for (var side = 0; side < RoundSides; side++)
        {
            var next = (side + 1) % RoundSides;
            var (a, b, c, d) = (At(ring, side), At(ring, next), At(ring + 1, next), At(ring + 1, side));
            if (profile[ring].Spread <= 1e-9) mesh.AddFace(new[] { a, c, d }, new[] { (0, 1, 2) }, Outward(a, c, d));
            else if (profile[ring + 1].Spread <= 1e-9) mesh.AddFace(new[] { a, b, c }, new[] { (0, 1, 2) }, Outward(a, b, c));
            else mesh.AddFace(a, b, c, d, Outward(a, b, c, d));
        }

        foreach (var (ring, down) in new[] { (0, true), (profile.Count - 1, false) })
        {
            if (profile[ring].Spread <= 1e-9) continue;

            var rim = Enumerable.Range(0, RoundSides).Select(side => At(ring, side)).ToList();
            mesh.AddFace(rim, Enumerable.Range(1, RoundSides - 2).Select(i => (0, i, i + 1)).ToList(), new Point3D(0, 0, down ? -1 : 1));
            for (var side = 0; side < RoundSides; side++) mesh.AddEdge(rim[side], rim[(side + 1) % RoundSides]);
        }
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

            var (placedSill, placedHeight) = opening.Placed(document, openingType, wall, profile);

            var sill = Math.Clamp(bottom + placedSill, lowest, top);
            var head = Math.Clamp(bottom + placedSill + placedHeight, lowest, top);
            openings.Add((opening, openingType, from, to, sill, head));
        }

        // Curtain walls set into this one, and the doors and windows of walls joined to its
        // faces, leave a hole with nothing of this wall's in it.
        foreach (var hole in WallHoles.Of(document, wall))
            openings.Add((null, null, hole.From, hole.To, Math.Clamp(bottom + hole.Sill, lowest, top), Math.Clamp(bottom + hole.Head, lowest, top)));

        if (profile is not null && document.GetWallType(wall) is { } profiledType)
        {
            AddProfiled(document, wall, profiledType, bottom, profile, openings, meshes);
            AddSweeps(document, wall, profiledType, bottom, top, meshes);
        }

        // Each construction the wall is made of, between its own heights: one for an ordinary
        // wall, one per tier for a stacked one.
        // A wall in parts is shown as its parts, each its own piece; its doors, windows and
        // sweeps are still its own.
        var parts = Parts.Of(document, wall);

        // A framed wall's core is its frame: the studs stand where the core was, the finishes on them.
        var framing = WallFramings.Of(document, wall);
        foreach (var (type, tierBottom, tierTop) in document.GetWallTiers(wall).Where(_ => profile is null))
        {
            var first = meshes.Count;
            AddTier(document, wall, type, tierBottom, tierTop, openings, meshes, layers: parts.Count == 0, core: framing is null);
            Lean(wall, type, bottom, meshes, first);
        }

        foreach (var part in parts)
            if (Parts.Mesh(document, part) is { } partMesh) meshes.Add(partMesh);

        if (framing is not null && WallFramings.Mesh(document, framing) is { } frame) meshes.Add(frame);

        if (document.GetWallType(wall) is not { } planType) return;

        // Paint on its faces, and the parts split off them, just proud of the face.
        meshes.AddRange(WallPaint.Meshes(document, wall, planType));

        // Sweeps placed on this wall on their own, built like the type's and carrying their own
        // id, so clicking one selects it rather than the wall. Placed reveals are cut into the wall above.
        foreach (var (sweep, id) in WallSweeps.Placed(document, wall).Where(p => p.Sweep.Kind == SweepKind.Sweep))
            AddSweep(document, wall, planType, sweep, id, bottom, top, meshes);

        foreach (var (opening, openingType, from, to, sill, head) in openings)
        {
            if (opening is null || openingType is null || _shows?.Invoke(opening) == false) continue;

            var infill = meshes.Count;
            OpeningModel.Add(wall, planType, opening, openingType, from, to, sill, head, meshes);

            // A door or window leans with the wall it is in, unless it is set to stand upright:
            // then it stays as it was drawn and the gap against the slanted wall is the user's.
            if (opening.Orientation == OpeningOrientation.Slanted) Lean(wall, planType, bottom, meshes, infill);
        }
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
            // A bent wall is folded at its bend, so it needs a line of points there to fold on.
            foreach (var bend in WallLean.Bends(wall, double.PositiveInfinity)) meshes[i].SplitAt(bottom + bend);

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
        List<Mesh3D> meshes, bool layers = true, bool core = true)
    {
        var structure = type.Structure;
        if (structure.TotalWidth <= 0 || top <= bottom) return;

        // A log wall is built as its logs, course on course, instead of its layers.
        if (layers && LogWalls.Mesh(document, wall, type, bottom, top) is { } logs)
        {
            meshes.Add(logs);
            layers = false;
        }

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
        var reveals = WallSweeps.Reveals(document, wall, type, bottom, top);
        var breaks = reveals.SelectMany(r => new[] { r.Bottom, r.Top }).ToList();

        // A band of another material within a layer is built as its own run of it.
        breaks.AddRange(WallBands.Edges(type, bottom));

        // Walls stopping against this one carry their layers on into it, as high as they stand.
        var intrusions = WallJoins.Intrusions(document, wall, type);
        breaks.AddRange(intrusions.SelectMany(intrusion => new[] { intrusion.Bottom, intrusion.Top }));

        // Upright reveals split each piece along the wall, the part in the groove built thinner.
        var verticals = WallSweeps.VerticalReveals(document, wall);
        var split = pieces
            .SelectMany(piece => WallSweeps.SplitAtVerticalReveals(wall, type, piece.Slice, verticals)
                .Select(part => (part.Slice, piece.Bottom, piece.Top, Reveals: part.Bands.Count == 0 ? reveals : reveals.Concat(part.Bands).ToList())))
            .ToList();

        var layerIndex = -1;
        foreach (var (layer, start, end) in structure.GetLayerOffsets().Where(_ => layers))
        {
            layerIndex++;

            // A membrane is a line with no volume: nothing to build or cut.
            if (layer.Thickness <= 0) continue;

            // A framed wall's core is left to its frame.
            if (!core && (structure.CoreStartIndex < 0 || layerIndex >= structure.CoreStartIndex && layerIndex <= structure.CoreEndIndex)) continue;

            // One mesh for each material the layer is built in: its own, and its bands'.
            var byMaterial = new Dictionary<Guid, Mesh3D>();
            Mesh3D MeshFor(Guid materialId)
            {
                if (byMaterial.TryGetValue(materialId, out var found)) return found;

                var material = document.FindMaterial(materialId);
                return byMaterial[materialId] = new Mesh3D(
                    wall.Id, wall.LevelId, MeshKind.Wall,
                    material?.SurfaceColour ?? DefaultSurface,
                    material?.Name ?? layer.Function.ToString());
            }

            MeshFor(layer.MaterialId);

            foreach (var (slice, pieceBottom, pieceTop, pieceReveals) in split)
            {
                var heights = breaks.Where(z => z > pieceBottom && z < pieceTop)
                    .Append(pieceBottom).Append(pieceTop)
                    .Distinct().OrderBy(z => z).ToList();

                for (var i = 0; i + 1 < heights.Count; i++)
                {
                    if (WallSweeps.Recess(half - start, half - end, half, pieceReveals, (heights[i] + heights[i + 1]) / 2)
                        is not var (outer, inner))
                        continue;

                    var middle = (heights[i] + heights[i + 1]) / 2;
                    var here = intrusions.Where(intrusion => intrusion.At(middle)).ToList();
                    var mesh = MeshFor(WallBands.MaterialAt(type, layerIndex, middle - bottom));
                    foreach (var piece in WallJoins.LayerPieces(wall, type, outer, inner, Unwrapped(slice.CutFrom), Unwrapped(slice.CutTo), here))
                        mesh.AddExtrusion(piece, heights[i], heights[i + 1]);
                }
            }

            meshes.AddRange(byMaterial.Values.Where(mesh => !mesh.IsEmpty || byMaterial.Count == 1));
        }

        AddSweeps(document, wall, type, bottom, top, meshes);
    }

    /// <summary>
    /// A curtain wall as what it is made of: a pane in each cell - glass, a solid panel, a door
    /// or nothing - and the mullions framing them, square or round, all following the wall
    /// round a curve.
    /// </summary>
    /// <summary>A curtain wall's mullions and anchors one by one, each its own mesh: what IFC has as members and brackets of their own.</summary>
    public sealed class CurtainParts
    {
        public List<(CurtainMullion Mullion, MullionType Type, Mesh3D Mesh)> Mullions { get; } = new();

        public List<(MullionAnchor Anchor, Mesh3D Mesh)> Anchors { get; } = new();
    }

    /// <summary>A curtain wall's mullions and anchors, each built on its own as the wall builds it; null for a wall that is not one.</summary>
    public static CurtainParts? CurtainWallParts(BimDocument document, Wall wall)
    {
        if (wall.Length <= WallJoins.JoinTolerance || CurtainLayout.Of(document, wall) is not { } layout) return null;
        var parts = new CurtainParts();
        AddCurtainWall(document, wall, layout, new List<Mesh3D>(), parts);
        return parts;
    }

    private static void AddCurtainWall(BimDocument document, Wall wall, CurtainLayout layout, List<Mesh3D> meshes, CurtainParts? parts = null)
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

        // Each panel is its own mesh, carrying its own id, so clicking a pane in the 3D view
        // selects that panel and shows its properties rather than the whole wall. Clear glass
        // takes its material's colour; tinted, frosted, laminated or spandrel glass its own
        // colour and how far it is seen through.
        var panes = new List<Mesh3D>();

        Mesh3D Glass(CurtainCell cell)
        {
            var material = document.FindMaterial(type.GlassMaterialId);
            var clear = material?.SurfaceColour ?? GlazingColour;
            var mesh = new Mesh3D(CurtainPanel.IdOf(wall.Id, cell.Column, cell.Row), wall.LevelId, MeshKind.Glazing,
                CurtainGlassLook.ColourOf(cell.Glass, clear),
                cell.Glass == CurtainGlass.Clear ? material?.Name ?? "Glazing" : CurtainGlassLook.NameOf(cell.Glass))
            {
                Opacity = CurtainGlassLook.OpacityOf(cell.Glass),
                OwnerId = wall.Id
            };

            panes.Add(mesh);
            return mesh;
        }

        Mesh3D Panel(CurtainCell cell, MeshKind kind, Guid materialId, ColourRgb fallback)
        {
            var material = document.FindMaterial(materialId);
            var mesh = new Mesh3D(CurtainPanel.IdOf(wall.Id, cell.Column, cell.Row), wall.LevelId, kind,
                material?.SurfaceColour ?? fallback, material?.Name ?? kind.ToString())
            {
                OwnerId = wall.Id
            };

            panes.Add(mesh);
            return mesh;
        }
        var doors = NewMesh(MeshKind.DoorLeaf, type.MullionMaterialId, LeafColour);

        // Windows cut into the curtain wall itself, as against the panels that are windows.
        var cut = WallOpenings.Of(document, wall)
            .Select(opening => (Opening: opening, Type: document.FindType<OpeningType>(opening.TypeId)))
            .Where(entry => entry.Type is not null)
            .Select(entry =>
            {
                var (from, to) = entry.Opening.GetSpan(entry.Type!);
                return (entry.Opening, Type: entry.Type!, From: Math.Max(0, from), To: Math.Min(wall.Length, to),
                    Sill: entry.Opening.SillHeight, Head: entry.Opening.SillHeight + entry.Opening.HeightOf(entry.Type!));
            })
            .Where(entry => entry.To - entry.From > 1)
            .ToList();

        var holes = cut.Select(entry => (entry.From, entry.To, entry.Sill, entry.Head)).ToList();

        // Where the joins cut its ends. A curtain wall stops against what it meets like any
        // other wall: run out to its full length and its frame would push into the masonry
        // beside it, and the two would be drawn one over the other.
        var (startCut, endCut) = WallJoins.GetEndCuts(document, wall, body);
        var trimFrom = Trim(wall, body, startCut, 0);
        var trimTo = Trim(wall, body, endCut, layout.Length);

        // The frame goes where the wall actually ends, not where the grid was set out. A wall
        // shortened by a join would otherwise lose the mullion at that end - the grid puts it
        // at the wall's own end, which is now inside the masonry - and leave its glass butting
        // the brickwork with nothing holding it. So the border mullion comes in with the trim,
        // and the glass stops short of it as it does at any other edge of the wall.
        var edge = layout.Type.BorderMullions && layout.Type.MullionWidth > 0 ? layout.Type.MullionWidth : 0;
        var paneFrom = trimFrom + (trimTo - trimFrom > 2 * edge ? edge : 0);
        var paneTo = trimTo - (trimTo - trimFrom > 2 * edge ? edge : 0);

        foreach (var cell in layout.Cells)
        {
            if (cell.ClearTo - cell.ClearFrom <= 1e-6 || cell.ClearTop - cell.ClearBottom <= 1e-6) continue;
            if (cell.ClearTo <= paneFrom + 1 || cell.ClearFrom >= paneTo - 1) continue;

            if (cell.Kind == CurtainPanelKind.Door)
            {
                var doorway = cell with
                {
                    ClearFrom = Math.Max(cell.ClearFrom, paneFrom), ClearTo = Math.Min(cell.ClearTo, paneTo)
                };

                AddCurtainDoor(document, wall, body, doorway, bottom, panes,
                    Panel(cell, MeshKind.DoorLeaf, type.MullionMaterialId, LeafColour), Glass(cell));
                continue;
            }

            var mesh = cell.Kind switch
            {
                CurtainPanelKind.Glazed => Glass(cell),
                CurtainPanelKind.Solid => Panel(cell, MeshKind.Wall, type.SolidMaterialId, DefaultSurface),
                _ => null
            };
            if (mesh is null) continue;

            // Laminated glass is two panes and an interlayer, so it is thicker than the rest.
            var pane = cell.Kind == CurtainPanelKind.Glazed ? CurtainGlassLook.ThicknessOf(cell.Glass, half) : half;

            // Whatever is cut into the wall - a window put in with the window tool - is left
            // out of the panel, and the glass comes back round it.
            foreach (var (from, to, low, high) in CurtainOpening.Panes(cell, holes))
            {
                var (a, b) = (Math.Max(from, paneFrom), Math.Min(to, paneTo));
                if (b - a <= 1) continue;

                // Under a sloping top, the pane is shaped to it: cut along the rake. Glass is one
                // sheet to the shape - a gable's pane is not two halves meeting under the ridge,
                // with a line where they meet.
                var pieces = layout.Under(a, b, low, high).Where(piece => piece.To - piece.From > 1).ToList();
                if (cell.Kind == CurtainPanelKind.Glazed && layout.TopLine is not null && !wall.IsCurved)
                {
                    foreach (var sheet in Sheets(pieces))
                        AddPaneShape(mesh, wall, body, sheet.Select(point => new Point2D(point.X, bottom + point.Y)).ToList(), pane);
                    continue;
                }

                foreach (var piece in pieces)
                {
                    if (cell.Kind == CurtainPanelKind.Glazed)
                        AddPane(mesh, wall, body, piece.From, piece.To, pane, bottom + piece.Bottom, bottom + piece.TopFrom, bottom + piece.TopTo);
                    else
                        mesh.AddExtrusion(CurtainGeometry.Band(wall, body, piece.From, piece.To, pane, -pane),
                            _ => bottom + piece.Bottom,
                            point => bottom + Lerp(piece.TopFrom, piece.TopTo, piece.From, piece.To, wall.LocationCurve.Locate(point).Along));
                }
            }
        }

        // The windows themselves, built where they were put and carrying their own ids, so one
        // can be clicked, sized and moved like a window in any other wall.
        foreach (var (opening, openingType, from, to, sill, head) in cut.Where(entry => _shows?.Invoke(entry.Opening) != false))
            OpeningModel.Add(wall, body, opening, openingType, from, to, bottom + sill, bottom + head, meshes);

        // Each mullion in its own type's section and material: a box offset in or out with its
        // cover cap on its face, or a tube - and where two curtain walls meet, the corner mullion
        // between them, built by one of the two.
        var frames = new Dictionary<Guid, Mesh3D>();
        Mesh3D FrameOf(MullionType made) =>
            frames.TryGetValue(made.MaterialId, out var kept) ? kept : frames[made.MaterialId] = NewMesh(MeshKind.Mullion, made.MaterialId, DefaultSurface);
        var corners = new[] { true, false }.ToDictionary(atStart => atStart, atStart => CurtainCorners.At(document, wall, atStart));

        // Built apart - for IFC - each mullion is a mesh of its own.
        Mesh3D Frame(CurtainMullion mullion, MullionType made)
        {
            if (parts is null) return FrameOf(made);
            var own = NewMesh(MeshKind.Mullion, made.MaterialId, DefaultSurface);
            parts.Mullions.Add((mullion, made, own));
            return own;
        }

        // Where its uprights are spliced, a stack joint's gap up each; and where they are anchored.
        var (anchors, splices, _) = MullionFixings.Of(document, wall, layout);
        List<(double, double)> Joints(CurtainMullion mullion) => splices
            .Where(splice => splice.Mullion == mullion)
            .Select(splice => (splice.Height - splice.Joint / 2, splice.Height + splice.Joint / 2))
            .ToList();

        foreach (var mullion in layout.Mullions)
        {
            if (mullion.Type is not { } mullionType) continue;
            var radius = mullionType.FaceWidth / 2;
            var (outer, inner) = (mullionType.Offset + mullionType.Thickness / 2, mullionType.Offset - mullionType.Thickness / 2);
            var round = mullionType.Shape == MullionShape.Circular;
            var cap = round || mullionType.IsCorner ? 0 : mullionType.CapDepth;

            // At a corner with another curtain wall: the corner mullion, or nothing - the other wall builds it.
            if (mullion.IsVertical && mullion.IsBorder && corners[mullion.From <= 1] is { } corner)
            {
                if (corner.Builds && !mullion.IsSloped)
                {
                    var built = Frame(mullion, corner.Type);
                    var outline = CurtainCorners.Outline(document, wall, mullion.From <= 1, corner.Other, corner.OtherAtStart, corner.Type);
                    foreach (var (low, high) in CurtainOpening.Gaps(mullion.Bottom, mullion.Top, Joints(mullion)))
                        built.AddExtrusion(outline, bottom + low, bottom + high);
                }

                continue;
            }

            var frame = Frame(mullion, mullionType);

            // A window cut into the wall interrupts the mullions it crosses, rather than a bar
            // running through the middle of its glass.
            var crossing = holes
                .Where(hole => mullion.IsVertical
                    ? hole.From < mullion.To && hole.To > mullion.From
                    : hole.Sill < mullion.Top && hole.Head > mullion.Bottom)
                .Select(hole => mullion.IsVertical ? (hole.Sill, hole.Head) : (hole.From, hole.To))
                .ToList();
            if (mullion.IsVertical) crossing.AddRange(Joints(mullion));

            // A vertical mullion at either end of the grid is the edge of the frame, so it moves
            // with the trim to stand at the end of the wall as built; every other one stays
            // where the grid put it and is simply cut off if the wall no longer reaches it.
            var (span, spanTo) = mullion.IsVertical && mullion.IsBorder
                ? mullion.From <= 1
                    ? (trimFrom, trimFrom + (mullion.To - mullion.From))
                    : (trimTo - (mullion.To - mullion.From), trimTo)
                : (mullion.From, mullion.To);

            if (spanTo <= trimFrom + 1 || span >= trimTo - 1) continue;

            // Under a sloping top: an upright stopped at the rake, a transom cut off by it, or the
            // rake itself - their bottoms and tops follow it along the wall.
            if (mullion.IsSloped)
            {
                var (from, to) = (Math.Max(span, trimFrom), Math.Min(spanTo, trimTo));
                if (to - from <= 1) continue;

                double Along(Point2D point) => wall.LocationCurve.Locate(point).Along;
                Func<Point2D, double> low = mullion.IsVertical
                    ? _ => bottom + mullion.Bottom
                    : point => bottom + mullion.BottomAt(Along(point));
                Func<Point2D, double> high = mullion.IsVertical
                    ? point => bottom + Math.Max(mullion.Bottom, layout.TopAt(Along(point)) - layout.TopInset)
                    : point => bottom + mullion.TopAt(Along(point));

                frame.AddExtrusion(
                    round && mullion.IsVertical
                        ? CurtainGeometry.Circle(wall, body, (from + to) / 2, radius, mullionType.Offset)
                        : CurtainGeometry.Band(wall, body, from, to, outer, inner),
                    low, high);
                if (cap > 0) frame.AddExtrusion(CurtainGeometry.Band(wall, body, from, to, outer + cap, outer), low, high);
                continue;
            }

            var run = mullion.IsVertical
                ? (mullion.Bottom, mullion.Top)
                : (Math.Max(span, trimFrom), Math.Min(spanTo, trimTo));

            foreach (var (low, high) in CurtainOpening.Gaps(run.Item1, run.Item2, crossing))
            {
                var (from, to) = mullion.IsVertical
                    ? (Math.Max(span, trimFrom), Math.Min(spanTo, trimTo))
                    : (low, high);
                var (base_, top) = mullion.IsVertical ? (low, high) : (mullion.Bottom, mullion.Top);

                if (!round)
                {
                    frame.AddExtrusion(CurtainGeometry.Band(wall, body, from, to, outer, inner), bottom + base_, bottom + top);
                    if (cap > 0) frame.AddExtrusion(CurtainGeometry.Band(wall, body, from, to, outer + cap, outer), bottom + base_, bottom + top);
                }
                else if (mullion.IsVertical)
                    frame.AddExtrusion(CurtainGeometry.Circle(wall, body, (from + to) / 2, radius, mullionType.Offset), bottom + base_, bottom + top);
                else
                    AddTube(frame, CurtainGeometry.Path(wall, body, from, to).Select(step => (step.Point + step.Across * mullionType.Offset, step.Across)).ToList(),
                        bottom + (base_ + top) / 2, radius);
            }
        }

        // The anchors: a bracket behind each upright at each floor it is held at, on the slab - under
        // the one over its head - standing back from its inner face into the building.
        if (anchors.Count > 0)
        {
            var steel = document.Materials.FirstOrDefault(material => material.Name.Contains("Galvanised", StringComparison.OrdinalIgnoreCase))?.Id ?? type.MullionMaterialId;
            var brackets = NewMesh(MeshKind.Mullion, steel, DefaultSurface);
            foreach (var anchor in anchors)
            {
                var made = anchor.Mullion.Type!;
                var middle = Math.Clamp((anchor.Mullion.From + anchor.Mullion.To) / 2, 0, wall.Length);
                var reach = Math.Max(made.FaceWidth, 40) / 2 + 15;
                var face = made.Offset - (made.Shape == MullionShape.Circular ? made.FaceWidth : made.Thickness) / 2;
                var (low, high) = anchor.Height >= layout.Height - 1 ? (anchor.Height - 100, anchor.Height) : (anchor.Height, anchor.Height + 100);
                var bracket = parts is null ? brackets : NewMesh(MeshKind.Mullion, steel, DefaultSurface);
                bracket.AddExtrusion(CurtainGeometry.Band(wall, body, Math.Max(0, middle - reach), Math.Min(wall.Length, middle + reach), face, face - 150),
                    bottom + low, bottom + high);
                parts?.Anchors.Add((anchor, bracket));
            }

            if (!brackets.IsEmpty) meshes.Add(brackets);
        }

        meshes.AddRange(panes);
        meshes.AddRange(frames.Values.Where(mesh => !mesh.IsEmpty));
    }

    /// <summary>
    /// A door in a curtain wall's panel. If the panel says which door type it is, that door is
    /// built here as any other door of that type would be - its leaves, its design, its
    /// operation - filling the panel from mullion to mullion. A panel with no type named falls
    /// back to a glazed storefront leaf: a slim frame round a pane, with a long pull handle on
    /// each face.
    /// </summary>
    private static void AddCurtainDoor(
        BimDocument document, Wall wall, WallType body, CurtainCell cell, double bottom,
        List<Mesh3D> meshes, Mesh3D leaf, Mesh3D glass)
    {
        // A door is the panel: it fills it, mullion to mullion and floor to head, which is what
        // a doorway in a glazed wall is.
        if (cell.OpeningTypeId is { } typeId && document.FindType<DoorType>(typeId) is { } doorType)
        {
            OpeningModel.AddPanelDoor(wall, body, doorType, CurtainPanel.IdOf(wall.Id, cell.Column, cell.Row), wall.LevelId,
                cell.ClearFrom, cell.ClearTo, bottom + cell.ClearBottom, bottom + cell.ClearTop, meshes,
                cell.FlipHand, cell.FlipFacing, cell.Glass, cell.IsOpen);
            return;
        }

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

        // A pull bar on each face, near the leaf's free edge, set and sized to the leaf: a tall
        // shopfront door carries its pull higher and longer than a domestic one.
        var at = u1 - stile / 2;
        var tall = z1 - z0;
        var middle = z0 + Math.Min(Math.Clamp(tall * 0.45, 1000, 1400), tall / 2);
        var reach = Math.Clamp(tall * 0.28, 500, 1400) / 2;
        foreach (var side in new[] { 1.0, -1.0 })
            Part(leaf, at - 12, at + 12, side * (depth + 55), side * (depth + 35), middle - reach, middle + reach);
    }

    /// <summary>
    /// How far along a wall one of its end cuts reaches, so what is built stops there. The cut
    /// runs across the wall, and it is the deepest point of it that everything must clear.
    /// </summary>
    private static double Trim(Wall wall, WallType body, WallCut? cut, double fallback)
    {
        if (cut is null) return fallback;

        var alongs = cut.Points.Select(point => wall.Locate(body.Structure, point).Along).ToList();
        return fallback <= 0 ? Math.Max(0, alongs.Max()) : Math.Min(fallback, alongs.Min());
    }

    /// <summary>
    /// A pane of glass as a sheet: its two faces and nothing round the edges.
    ///
    /// Glass built as a block shows its cut edges through itself, so a pane divided round a
    /// window comes out with a dark seam along every join - the last thing wanted in a wall
    /// whose whole point is that you see through it. The edges of a pane are inside the
    /// mullions holding it in any case, where nobody can see them.
    /// </summary>
    private static void AddPane(
        Mesh3D mesh, Wall wall, WallType body, double from, double to, double half, double z0, double z1, double? z1AtTo = null)
    {
        var topTo = z1AtTo ?? z1;
        if (to - from <= 1e-6 || Math.Max(z1, topTo) - z0 <= 1e-6) return;

        var path = CurtainGeometry.Path(wall, body, from, to);
        if (path.Count < 2) return;

        // A sloping top runs straight from one end of the pane to the other.
        var run = new double[path.Count];
        for (var i = 1; i < path.Count; i++) run[i] = run[i - 1] + path[i].Point.DistanceTo(path[i - 1].Point);
        double TopAt(int i) => run[^1] <= 1e-9 ? z1 : z1 + (topTo - z1) * run[i] / run[^1];

        foreach (var side in new[] { half, -half })
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i].Point + path[i].Across * side;
            var b = path[i + 1].Point + path[i + 1].Across * side;

            mesh.AddQuad(
                new Point3D(a.X, a.Y, z0), new Point3D(b.X, b.Y, z0),
                new Point3D(b.X, b.Y, TopAt(i + 1)), new Point3D(a.X, a.Y, TopAt(i)));
        }

        // The outline, so the pane still reads as a pane where it meets its frame.
        var (start, end) = (path[0].Point, path[^1].Point);
        mesh.AddEdge(new Point3D(start.X, start.Y, z0), new Point3D(end.X, end.Y, z0));
        mesh.AddEdge(new Point3D(start.X, start.Y, z1), new Point3D(end.X, end.Y, topTo));
    }

    /// <summary>
    /// The pieces of a pane cut to a sloping top, joined into the outlines they make - along the
    /// wall and height - one for each stretch where they run on from each other unbroken.
    /// </summary>
    private static IEnumerable<List<Point2D>> Sheets(IReadOnlyList<(double From, double To, double Bottom, double TopFrom, double TopTo)> pieces)
    {
        var run = new List<(double From, double To, double Bottom, double TopFrom, double TopTo)>();
        foreach (var piece in pieces.OrderBy(piece => piece.From))
        {
            if (run.Count > 0 && Math.Abs(piece.From - run[^1].To) > 1e-6)
            {
                yield return Outline(run);
                run.Clear();
            }

            run.Add(piece);
        }

        if (run.Count > 0) yield return Outline(run);

        static List<Point2D> Outline(List<(double From, double To, double Bottom, double TopFrom, double TopTo)> run)
        {
            var points = new List<Point2D> { new(run[0].From, run[0].Bottom), new(run[^1].To, run[^1].Bottom) };
            for (var i = run.Count - 1; i >= 0; i--)
            {
                points.Add(new Point2D(run[i].To, run[i].TopTo));
                points.Add(new Point2D(run[i].From, run[i].TopFrom));
            }

            // Corners met twice where one piece runs on into the next, and points in a straight
            // line along its bottom or top, are one corner or none.
            var outline = new List<Point2D>();
            foreach (var point in points)
                if (outline.Count == 0 || outline[^1].DistanceTo(point) > 1e-6) outline.Add(point);
            if (outline.Count > 1 && outline[^1].DistanceTo(outline[0]) <= 1e-6) outline.RemoveAt(outline.Count - 1);

            for (var i = outline.Count - 1; i >= 0 && outline.Count > 3; i--)
            {
                var (before, at, after) = (outline[(i - 1 + outline.Count) % outline.Count], outline[i], outline[(i + 1) % outline.Count]);
                var cross = (at.X - before.X) * (after.Y - before.Y) - (at.Y - before.Y) * (after.X - before.X);
                if (Math.Abs(cross) <= 1e-6 * Math.Max(1, before.DistanceTo(after))) outline.RemoveAt(i);
            }

            return outline;
        }
    }

    /// <summary>
    /// A flat pane of glass of any outline in a straight wall's elevation - along the wall and
    /// height - its two faces a distance either side of the centreline.
    /// </summary>
    private static void AddPaneShape(Mesh3D mesh, Wall wall, WallType body, IReadOnlyList<Point2D> shape, double half)
    {
        if (shape.Count < 3 || Polygon2D.Area(shape) <= 1) return;

        var ring = Polygon2D.SignedArea(shape) >= 0 ? shape : shape.Reverse().ToList();
        var triangles = Polygon2D.Triangulate(ring);
        var structure = body.Structure;
        var middle = ring.Average(point => point.X);

        foreach (var side in new[] { half, -half })
        {
            var points = ring.Select(point => wall.PointAt(structure, point.X, side) is var at ? new Point3D(at.X, at.Y, point.Y) : default).ToList();
            var outward = wall.PointAt(structure, middle, side) - wall.PointAt(structure, middle, 0);
            mesh.AddFace(points, triangles, new Point3D(outward.X, outward.Y, 0));
        }

        // The outline along its bottom and top, so the pane still reads as a pane where it meets
        // its frame.
        for (var i = 0; i < ring.Count; i++)
        {
            var (a, b) = (ring[i], ring[(i + 1) % ring.Count]);
            if (Math.Abs(a.X - b.X) <= 1e-6) continue;

            var (from, to) = (wall.PointAt(structure, a.X, 0), wall.PointAt(structure, b.X, 0));
            mesh.AddEdge(new Point3D(from.X, from.Y, a.Y), new Point3D(to.X, to.Y, b.Y));
        }
    }

    /// <summary>A height running straight from one end of a stretch of wall to the other.</summary>
    private static double Lerp(double atFrom, double atTo, double from, double to, double along) =>
        to - from <= 1e-9 ? atFrom : atFrom + (atTo - atFrom) * Math.Clamp((along - from) / (to - from), 0, 1);

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
        var length = wall.Length;
        var tolerance = WallJoins.JoinTolerance;

        // A stretch at an end of the wall can be shorter than the join there reaches into it -
        // the first few centimetres of a gable, where its top crosses a window's head. Cut on
        // its own it would be inside out; it is the end of the whole wall, cut back to where the
        // stretch ends, so the corner is built all the same.
        var strips = WallProfile.Strips(profile, length, holes)
            .Select(strip => (Strip: strip, Slice: WallSlices.Between(document, wall, type, strip.From, strip.To, startCut, endCut)))
            .Where(entry => entry.Slice is not null || entry.Strip.From <= tolerance || entry.Strip.To >= length - tolerance)
            .ToList();
        var whole = WallSlices.Between(document, wall, type, 0, length, startCut, endCut);

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
                if ((slice ?? whole) is not { } piece) continue;

                IReadOnlyList<Point2D> outline = WallJoins.GetBandOutline(
                    wall, type, half - start, half - end, Unwrapped(piece.CutFrom), Unwrapped(piece.CutTo));
                if (slice is null)
                    outline = ClipAlong(outline, Along,
                        strip.From <= tolerance ? double.NegativeInfinity : strip.From,
                        strip.To >= length - tolerance ? double.PositiveInfinity : strip.To);
                if (outline.Count < 3) continue;

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
        foreach (var sweep in type.Sweeps.Where(s => s.Kind == SweepKind.Sweep))
            AddSweep(document, wall, type, sweep, wall.Id, bottom, top, meshes);
    }

    /// <summary>
    /// One sweep on one wall, as a solid: the profile carried along each run of the face - its
    /// ends cut along the join where the wall meets another, so two walls' sweeps meet in a mitre,
    /// and turned round an exposed end when it returns - or, upright, stood full height.
    /// </summary>
    private static void AddSweep(
        BimDocument document, Wall wall, WallType type, WallSweep sweep, Guid elementId,
        double wallBottom, double wallTop, List<Mesh3D> meshes)
    {
        var structure = type.Structure;
        var shape = sweep.Shape(document);
        var material = document.FindMaterial(sweep.MaterialId);
        var mesh = new Mesh3D(
            elementId, wall.LevelId, MeshKind.Sweep,
            material?.SurfaceColour ?? DefaultSurface,
            material?.Name ?? "Sweep");

        if (sweep.Vertical)
        {
            // Upright: the profile laid flat in plan, its height along the wall, stood from the
            // wall's base to its top.
            var outline = shape
                .Select(p => wall.PointAt(structure,
                    Math.Clamp(sweep.Along + (p.Up - 0.5) * sweep.Height, 0, wall.Length),
                    WallSweeps.Across(type, sweep, sweep.OutAt(p.Out))))
                .ToList();
            mesh.AddExtrusion(outline, wallBottom, wallTop);
            meshes.Add(mesh);
            return;
        }

        var (sweepBottom, sweepTop) = sweep.Span(wallBottom, wallTop);
        var curve = wall.LocationCurve;
        var sign = sweep.Side == WallSide.Exterior ? 1.0 : -1.0;
        var half = type.Width / 2;

        Point3D At(double along, double across, (double Out, double Up) corner)
        {
            var plan = wall.PointAt(structure, along, across);
            return new Point3D(plan.X, plan.Y, sweepBottom + corner.Up * sweep.Height);
        }

        foreach (var run in WallSweeps.Runs(document, wall, type, sweep, sweepBottom, sweepTop))
        {
            var stations = new List<double> { run.From };
            stations.AddRange(curve.Between(run.From, run.To, wall.LeftOf(structure, sign * half)));
            stations.Add(run.To);

            // Each ring of the loft: every profile point at its own place along the wall, which
            // differs only at the two ends, where the cut is along a join or turns a corner.
            var rings = stations.Select((along, s) => shape.Select(p =>
            {
                var o = sweep.OutAt(p.Out);
                var at = s == 0 ? WallSweeps.EndAlong(wall, type, sweep, run.Start, along, atStart: true, o)
                    : s == stations.Count - 1 ? WallSweeps.EndAlong(wall, type, sweep, run.End, along, atStart: false, o)
                    : along;
                return At(at, WallSweeps.Across(type, sweep, o), p);
            }).ToArray()).ToList();

            for (var s = 0; s + 1 < rings.Count; s++)
            for (var k = 0; k < shape.Count; k++)
            {
                var j = (k + 1) % shape.Count;
                mesh.AddQuad(rings[s][k], rings[s + 1][k], rings[s + 1][j], rings[s][j]);
                mesh.AddEdge(rings[s][k], rings[s + 1][k]);
            }

            foreach (var (ring, end, atStart, along) in new[] { (rings[0], run.Start, true, run.From), (rings[^1], run.End, false, run.To) })
            {
                if (end.Kind == SweepEndKind.Free && sweep.Returns)
                {
                    AddReturn(mesh, wall, type, sweep, shape, ring, along, atStart, sweepBottom);
                    continue;
                }

                Cap(mesh, shape, ring);
            }
        }

        meshes.Add(mesh);
    }

    /// <summary>
    /// The return round an exposed end: the profile turned the corner in a mitre and carried
    /// across the end of the wall to its other face, where it stops flush.
    /// </summary>
    private static void AddReturn(
        Mesh3D mesh, Wall wall, WallType type, WallSweep sweep, IReadOnlyList<(double Out, double Up)> shape,
        Point3D[] corner, double along, bool atStart, double sweepBottom)
    {
        var structure = type.Structure;
        var farFace = -WallSweeps.Across(type, sweep, 0);

        var far = shape.Select(p =>
        {
            var o = sweep.OutAt(p.Out);
            var plan = wall.PointAt(structure, atStart ? along - o : along + o, farFace);
            return new Point3D(plan.X, plan.Y, sweepBottom + p.Up * sweep.Height);
        }).ToArray();

        for (var k = 0; k < shape.Count; k++)
        {
            var j = (k + 1) % shape.Count;
            mesh.AddQuad(corner[k], far[k], far[j], corner[j]);
            mesh.AddEdge(corner[k], far[k]);
        }

        Cap(mesh, shape, far);
    }

    /// <summary>Closes the end of a run with the profile's own shape.</summary>
    private static void Cap(Mesh3D mesh, IReadOnlyList<(double Out, double Up)> shape, Point3D[] ring)
    {
        var profile = shape.Select(p => new Point2D(p.Out, p.Up)).ToList();
        foreach (var (i, j, k) in Polygon2D.Triangulate(profile))
            mesh.AddTriangle(ring[i], ring[j], ring[k]);

        for (var k = 0; k < ring.Length; k++)
            mesh.AddEdge(ring[k], ring[(k + 1) % ring.Length]);
    }

    /// <summary>
    /// A jamb without its wrapping. The finishes return into a reveal only as high as the
    /// opening; above it the wall carries on unbroken, and a solid built from the plan
    /// cut would run the return the full height of the wall.
    /// </summary>
    private static WallCut Unwrapped(WallCut cut) =>
        cut.Condition == WallEndCondition.Jamb ? new WallCut(cut.Points, cut.Condition) : cut;

    /// <summary>The part of a plan outline between two distances along a wall, either of which may be open.</summary>
    internal static IReadOnlyList<Point2D> ClipAlong(IReadOnlyList<Point2D> outline, Func<Point2D, double> along, double from, double to)
    {
        var kept = outline.ToList();
        foreach (var (limit, keepBelow) in new[] { (from, false), (to, true) })
        {
            if (double.IsInfinity(limit) || kept.Count == 0) continue;

            // Sutherland-Hodgman against one line across the wall.
            double Inside(Point2D point) => keepBelow ? limit - along(point) : along(point) - limit;
            var next = new List<Point2D>();
            for (var i = 0; i < kept.Count; i++)
            {
                var (a, b) = (kept[i], kept[(i + 1) % kept.Count]);
                var (da, db) = (Inside(a), Inside(b));
                if (da >= 0) next.Add(a);
                if ((da >= 0) != (db >= 0)) next.Add(a + (b - a) * (da / (da - db)));
            }

            kept = next;
        }

        return kept;
    }

    // ---- slabs -----------------------------------------------------------------

    /// <summary>
    /// A floor, ceiling or roof as its layers, hanging down from its upper surface - the same
    /// convention the section uses, so the two views cannot disagree about where it sits.
    /// </summary>
    /// <summary>What the slot between a floor and a curtain wall is packed with, and its colour where the project has no such material.</summary>
    public const string FireStopMaterial = "Fire Stop, Mineral Wool";

    private static readonly ColourRgb FireStopColour = ColourRgb.FromHex("D8C98A");

    private static void AddSlab(BimDocument document, Slab slab, List<Mesh3D> meshes)
    {
        if (slab.Boundary.Count < 3) return;
        if (document.FindType<SlabType>(slab.TypeId) is not { } type) return;

        // A pitched roof is not a slab, and is not built like one.
        // A roof stands on its base rather than hanging from it, flat or pitched, so it is
        // built by its own rule.
        if (slab is Roof roof)
        {
            AddRoof(document, roof, type, meshes);
            return;
        }

        var surface = (document.FindLevel(slab.LevelId)?.Elevation ?? 0) + slab.HeightOffset;

        // Cut back from the curtain walls in front of it, and less the shafts through it. What
        // is left is built a piece at a time, cut straight across each hole; those cuts are
        // inside the slab, not faces of it.
        IReadOnlyList<IReadOnlyList<Point2D>> pieces = new[] { slab.Boundary };
        Func<Point2D, Point2D, bool>? cut = null;
        if (SlabEdges.IsCut(document, slab))
        {
            var regions = SlabEdges.Regions(document, slab);
            pieces = Shafts.Pieces(regions);
            cut = Shafts.CutAlong(regions);
        }

        // The fire stop packed into the slot between its edge and the curtain wall, its full depth.
        var stops = SlabEdges.FireStops(document, slab);
        if (stops.Count > 0)
        {
            var fireStop = document.Materials.FirstOrDefault(material => material.Name == FireStopMaterial);
            var stopMesh = new Mesh3D(slab.Id, slab.LevelId, MeshKind.Floor, fireStop?.SurfaceColour ?? FireStopColour, fireStop?.Name ?? FireStopMaterial);
            var depth = type.Structure.TotalWidth;
            foreach (var stop in stops) stopMesh.AddExtrusion(stop, surface - depth, surface);
            if (!stopMesh.IsEmpty) meshes.Add(stopMesh);
        }

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

            foreach (var piece in pieces)
                mesh.AddExtrusion(piece, _ => surface - end, _ => surface - start, null, cut);
            meshes.Add(mesh);
        }
    }

    /// <summary>
    /// A roof, flat or pitched: one face per plane, each carrying the whole build-up on top of
    /// the underside the plane describes - a roof sits on what carries it.
    ///
    /// The layers are measured square to the slope, the way a roof is actually built and the
    /// way its tiles and insulation are specified, so each face's vertical depth is its
    /// thickness stretched by its own pitch. Building every face to the same vertical
    /// thickness instead would leave a step in the covering at every ridge and hip.
    /// </summary>
    private static void AddRoof(BimDocument document, Roof roof, SlabType type, List<Mesh3D> meshes)
    {
        // A roof by extrusion is swept from its section, so a vault shades as the curve it is.
        if (roof.Extrusion is { IsValid: true } extrusion && roof.DormerOpenings.Count == 0 && roof.JoinedTo is null)
        {
            AddExtrudedRoof(document, roof, extrusion, type, meshes);
            return;
        }

        // The pieces are the same ones a section cuts, eaves and all, so the two agree. Where one
        // layer lies on the next, the face between them is inside the roof: neither is drawn,
        // or far off the two would flicker through the finish as one.
        var all = RoofSolid.Pieces(document, roof);
        foreach (var group in all.GroupBy(piece => piece.Layer))
        {
            var layer = group.Key;
            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                roof.Id, roof.LevelId, MeshKind.Roof,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            // The faces of a cone are one curved surface built flat, so they are shaded as one
            // - across the join where one arc runs on into the next too. A real crease between
            // them is steeper than smoothing crosses, and stays sharp. Where pieces of the layer
            // meet - a face cut in two round a dormer's opening - the join has no sides.
            var pieces = group.ToList();
            foreach (var piece in pieces)
                mesh.AddExtrusion(piece.Outline, piece.Bottom.HeightAt, piece.Top.HeightAt, piece.SmoothGroup is null ? null : 0,
                    (a, b) => Joined(pieces, piece, a, b),
                    topCap: !all.Any(other => Stacked(other, piece)),
                    bottomCap: !all.Any(other => Stacked(piece, other)));

            // Where two faces of the layer meet at an angle - a ridge, a hip, a valley - the join
            // has no sides, but it is a crease on top and underneath, and is drawn as one.
            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                if (piece.SmoothGroup is not null) continue;
                for (var k = 0; k < piece.Outline.Count; k++)
                {
                    var (a, b) = (piece.Outline[k], piece.Outline[(k + 1) % piece.Outline.Count]);
                    if (CreaseWith(pieces, i, a, b) is not { } other) continue;
                    if (TopShows(all, piece, other)) mesh.AddEdge(Point3D.On(a, piece.Top.HeightAt(a)), Point3D.On(b, piece.Top.HeightAt(b)));
                    if (!all.Any(lower => Stacked(piece, lower) || Stacked(other, lower)))
                        mesh.AddEdge(Point3D.On(a, piece.Bottom.HeightAt(a)), Point3D.On(b, piece.Bottom.HeightAt(b)));
                }
            }

            mesh.SmoothGroups();
            if (!mesh.IsEmpty) meshes.Add(mesh);
        }
    }

    /// <summary>
    /// The piece of the layer, after this one in the list, that an edge of this one runs on into
    /// at an angle: the two meet along the edge but are not one plane. Each crease is found once.
    /// </summary>
    private static RoofPiece? CreaseWith(IReadOnlyList<RoofPiece> pieces, int index, Point2D a, Point2D b)
    {
        var piece = pieces[index];
        for (var j = index + 1; j < pieces.Count; j++)
        {
            var other = pieces[j];
            if (!Joined(new[] { piece, other }, piece, a, b)) continue;
            var flat = Math.Abs(piece.Top.A - other.Top.A) < 1e-9 && Math.Abs(piece.Top.B - other.Top.B) < 1e-9;
            return flat || other.SmoothGroup is not null ? null : other;
        }

        return null;
    }

    /// <summary>Whether the top of a crease is a face of the roof: nothing of another layer lies on either piece there.</summary>
    private static bool TopShows(IReadOnlyList<RoofPiece> all, RoofPiece piece, RoofPiece other) =>
        !all.Any(upper => Stacked(upper, piece) || Stacked(upper, other));

    /// <summary>Whether one piece of roof lies right on another: the same outline, its underside the other's top all round.</summary>
    private static bool Stacked(RoofPiece upper, RoofPiece lower)
    {
        const double tolerance = 0.5;

        if (ReferenceEquals(upper, lower) || upper.Outline.Count != lower.Outline.Count) return false;
        if (!upper.Outline.All(point => lower.Outline.Any(other => other.DistanceTo(point) < tolerance))) return false;

        return upper.Outline.All(point => Math.Abs(upper.Bottom.HeightAt(point) - lower.Top.HeightAt(point)) < tolerance);
    }

    /// <summary>
    /// Whether an edge of a piece of roof is one another piece of the same layer has too, the two
    /// running on into each other at the same heights along it: inside the roof, not a face of it.
    /// </summary>
    private static bool Joined(IReadOnlyList<RoofPiece> pieces, RoofPiece piece, Point2D a, Point2D b)
    {
        const double tolerance = 0.5;

        foreach (var other in pieces)
        {
            if (ReferenceEquals(other, piece)) continue;

            var outline = other.Outline;
            for (var i = 0; i < outline.Count; i++)
            {
                var (c, d) = (outline[i], outline[(i + 1) % outline.Count]);
                var same = (a.DistanceTo(c) < tolerance && b.DistanceTo(d) < tolerance) ||
                           (a.DistanceTo(d) < tolerance && b.DistanceTo(c) < tolerance);
                if (!same) continue;

                return new[] { a, b }.All(point =>
                    Math.Abs(piece.Top.HeightAt(point) - other.Top.HeightAt(point)) < tolerance &&
                    Math.Abs(piece.Bottom.HeightAt(point) - other.Bottom.HeightAt(point)) < tolerance);
            }
        }

        return false;
    }

    /// <summary>
    /// A roof by extrusion, a layer at a time: each layer's section swept back from the
    /// extrusion's start to its end. Its top and underside are surfaces that stay smooth round
    /// an arc and crease only at the profile's real corners; its eaves are cut plumb and its
    /// ends capped square, the section showing on them as it would on a gable.
    /// </summary>
    private static void AddExtrudedRoof(BimDocument document, Roof roof, RoofExtrusion extrusion, SlabType type, List<Mesh3D> meshes)
    {
        var baseElevation = roof.BaseElevation(document);
        var (start, end) = (extrusion.Start, extrusion.End);

        var up = new Point3D(0, 0, 1);
        var down = new Point3D(0, 0, -1);
        var forward = new Point3D(extrusion.Direction.X, extrusion.Direction.Y, 0);
        var back = new Point3D(-forward.X, -forward.Y, 0);
        var across = new Point3D(extrusion.Across.X, extrusion.Across.Y, 0);
        var before = new Point3D(-across.X, -across.Y, 0);

        Point3D At(Point2D section, double run) => Point3D.On(extrusion.At(section.X, run), baseElevation + section.Y);
        List<Point3D> Row(IEnumerable<Point2D> line, double run) => line.Select(point => At(point, run)).ToList();

        foreach (var section in extrusion.Sections(type.Structure))
        {
            var material = document.FindMaterial(section.Layer.MaterialId);
            var mesh = new Mesh3D(
                roof.Id, roof.LevelId, MeshKind.Roof,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? section.Layer.Function.ToString());

            var (lower, upper) = (section.Lower, section.Upper);

            mesh.AddSweep(Row(upper, start), Row(upper, end), section.UpperSmooth, up);
            mesh.AddSweep(Row(lower, start), Row(lower, end), section.LowerSmooth, down);

            // The eaves, cut plumb.
            mesh.AddFace(At(lower[0], start), At(upper[0], start), At(upper[0], end), At(lower[0], end), back);
            mesh.AddFace(At(lower[^1], start), At(upper[^1], start), At(upper[^1], end), At(lower[^1], end), forward);

            // The two ends, the layer's section on each.
            var ring = upper.Concat(lower.Reverse()).ToList();
            var triangles = Polygon2D.Triangulate(ring);
            var first = Row(ring, start);
            var last = Row(ring, end);
            mesh.AddFace(first, triangles, before);
            mesh.AddFace(last, triangles, across);

            // The section's outline at each end, and a line along the roof at each real corner
            // of it - never part way round an arc, which would stripe the curve.
            for (var i = 0; i < ring.Count; i++)
            {
                mesh.AddEdge(first[i], first[(i + 1) % ring.Count]);
                mesh.AddEdge(last[i], last[(i + 1) % ring.Count]);
            }

            for (var i = 0; i < upper.Count; i++)
                if (!section.UpperSmooth[i]) mesh.AddEdge(At(upper[i], start), At(upper[i], end));

            for (var i = 0; i < lower.Count; i++)
                if (!section.LowerSmooth[i]) mesh.AddEdge(At(lower[i], start), At(lower[i], end));

            if (!mesh.IsEmpty) meshes.Add(mesh);
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
