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

        foreach (var column in document.Elements.OfType<Column>().Where(c => shows(c)))
            AddColumn(document, column, meshes);

        foreach (var component in document.Elements.OfType<Component>().Where(c => shows(c)))
            AddComponent(document, component, meshes);

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

    /// <summary>One component's solids on their own: what its family is shaped like, where it stands.</summary>
    public static IReadOnlyList<Mesh3D> BuildComponent(BimDocument document, Component component)
    {
        var meshes = new List<Mesh3D>();
        AddComponent(document, component, meshes);
        return Finished(meshes);
    }

    /// <summary>
    /// A component as the boxes its form is made of, turned to face the way it faces and stood
    /// on its level - or at its fixing height on the wall carrying it.
    /// </summary>
    private static void AddComponent(BimDocument document, Component component, List<Mesh3D> meshes)
    {
        if (document.FindType<ComponentType>(component.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0 || type.Height <= 0) return;

        var frame = ComponentModel.FrameOf(component, ComponentHosting.BaseElevation(document, component, type));

        var material = document.FindMaterial(type.MaterialId);
        var mesh = new Mesh3D(component.Id, component.LevelId, MeshKind.Wall,
            material?.SurfaceColour ?? type.Colour, material?.Name ?? type.Name);

        foreach (var part in ComponentModel.Parts(type))
            mesh.AddExtrusion(
                frame.Outline(part.From, part.To, part.Back, part.Front),
                frame.Base + part.Bottom, frame.Base + part.Top);

        meshes.Add(mesh);
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
            var head = Math.Clamp(bottom + opening.SillHeight + opening.HeightOf(openingType), lowest, top);
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
        foreach (var (type, tierBottom, tierTop) in document.GetWallTiers(wall).Where(_ => profile is null))
        {
            var first = meshes.Count;
            AddTier(document, wall, type, tierBottom, tierTop, openings, meshes);
            Lean(wall, type, bottom, meshes, first);
        }

        if (document.GetWallType(wall) is not { } planType) return;

        // Sweeps placed on this wall on their own, built like the type's and carrying their own
        // id, so clicking one selects it rather than the wall. Placed reveals are cut into the wall above.
        foreach (var (sweep, id) in WallSweeps.Placed(document, wall).Where(p => p.Sweep.Kind == SweepKind.Sweep))
            AddSweep(document, wall, planType, sweep, id, bottom, top, meshes);

        foreach (var (opening, openingType, from, to, sill, head) in openings)
        {
            if (opening is null || openingType is null) continue;

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
        var reveals = WallSweeps.Reveals(document, wall, type, bottom, top);
        var breaks = reveals.SelectMany(r => new[] { r.Bottom, r.Top }).ToList();

        // Upright reveals split each piece along the wall, the part in the groove built thinner.
        var verticals = WallSweeps.VerticalReveals(document, wall);
        var split = pieces
            .SelectMany(piece => WallSweeps.SplitAtVerticalReveals(wall, type, piece.Slice, verticals)
                .Select(part => (part.Slice, piece.Bottom, piece.Top, Reveals: part.Bands.Count == 0 ? reveals : reveals.Concat(part.Bands).ToList())))
            .ToList();

        foreach (var (layer, start, end) in structure.GetLayerOffsets())
        {
            // A membrane is a line with no volume: nothing to build or cut.
            if (layer.Thickness <= 0) continue;

            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                wall.Id, wall.LevelId, MeshKind.Wall,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

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
        var frame = NewMesh(MeshKind.Mullion, type.MullionMaterialId, DefaultSurface);

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

                if (cell.Kind == CurtainPanelKind.Glazed)
                    AddPane(mesh, wall, body, a, b, pane, bottom + low, bottom + high);
                else
                    mesh.AddExtrusion(CurtainGeometry.Band(wall, body, a, b, pane, -pane), bottom + low, bottom + high);
            }
        }

        // The windows themselves, built where they were put and carrying their own ids, so one
        // can be clicked, sized and moved like a window in any other wall.
        foreach (var (opening, openingType, from, to, sill, head) in cut)
            OpeningModel.Add(wall, body, opening, openingType, from, to, bottom + sill, bottom + head, meshes);

        var radius = type.MullionWidth / 2;
        var depth = type.MullionDepth / 2;

        foreach (var mullion in layout.Mullions)
        {
            // A window cut into the wall interrupts the mullions it crosses, rather than a bar
            // running through the middle of its glass.
            var crossing = holes
                .Where(hole => mullion.IsVertical
                    ? hole.From < mullion.To && hole.To > mullion.From
                    : hole.Sill < mullion.Top && hole.Head > mullion.Bottom)
                .Select(hole => mullion.IsVertical ? (hole.Sill, hole.Head) : (hole.From, hole.To))
                .ToList();

            // A vertical mullion at either end of the grid is the edge of the frame, so it moves
            // with the trim to stand at the end of the wall as built; every other one stays
            // where the grid put it and is simply cut off if the wall no longer reaches it.
            var (span, spanTo) = mullion.IsVertical && mullion.IsBorder
                ? mullion.From <= 1
                    ? (trimFrom, trimFrom + (mullion.To - mullion.From))
                    : (trimTo - (mullion.To - mullion.From), trimTo)
                : (mullion.From, mullion.To);

            if (spanTo <= trimFrom + 1 || span >= trimTo - 1) continue;

            var run = mullion.IsVertical
                ? (mullion.Bottom, mullion.Top)
                : (Math.Max(span, trimFrom), Math.Min(spanTo, trimTo));

            foreach (var (low, high) in CurtainOpening.Gaps(run.Item1, run.Item2, crossing))
            {
                var (from, to) = mullion.IsVertical
                    ? (Math.Max(span, trimFrom), Math.Min(spanTo, trimTo))
                    : (low, high);
                var (base_, top) = mullion.IsVertical ? (low, high) : (mullion.Bottom, mullion.Top);

                if (type.MullionProfile == MullionProfile.Rectangular)
                    frame.AddExtrusion(CurtainGeometry.Band(wall, body, from, to, depth, -depth), bottom + base_, bottom + top);
                else if (mullion.IsVertical)
                    frame.AddExtrusion(CurtainGeometry.Circle(wall, body, (from + to) / 2, radius), bottom + base_, bottom + top);
                else
                    AddTube(frame, CurtainGeometry.Path(wall, body, from, to), bottom + (base_ + top) / 2, radius);
            }
        }

        meshes.AddRange(panes);
        meshes.Add(frame);
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
        Mesh3D mesh, Wall wall, WallType body, double from, double to, double half, double z0, double z1)
    {
        if (to - from <= 1e-6 || z1 - z0 <= 1e-6) return;

        var path = CurtainGeometry.Path(wall, body, from, to);
        if (path.Count < 2) return;

        foreach (var side in new[] { half, -half })
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var a = path[i].Point + path[i].Across * side;
            var b = path[i + 1].Point + path[i + 1].Across * side;

            mesh.AddQuad(
                new Point3D(a.X, a.Y, z0), new Point3D(b.X, b.Y, z0),
                new Point3D(b.X, b.Y, z1), new Point3D(a.X, a.Y, z1));
        }

        // The outline, so the pane still reads as a pane where it meets its frame.
        var (start, end) = (path[0].Point, path[^1].Point);
        mesh.AddEdge(new Point3D(start.X, start.Y, z0), new Point3D(end.X, end.Y, z0));
        mesh.AddEdge(new Point3D(start.X, start.Y, z1), new Point3D(end.X, end.Y, z1));
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

    // ---- slabs -----------------------------------------------------------------

    /// <summary>
    /// A floor, ceiling or roof as its layers, hanging down from its upper surface - the same
    /// convention the section uses, so the two views cannot disagree about where it sits.
    /// </summary>
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
        var surface = roof.Surface(document);
        var total = type.Structure.TotalWidth;

        foreach (var (layer, start, end) in type.Structure.GetLayerOffsets())
        {
            if (layer.Thickness <= 0) continue;

            var material = document.FindMaterial(layer.MaterialId);
            var mesh = new Mesh3D(
                roof.Id, roof.LevelId, MeshKind.Roof,
                material?.SurfaceColour ?? DefaultSurface,
                material?.Name ?? layer.Function.ToString());

            foreach (var facet in surface.Facets)
            {
                var plane = facet.Plane;
                var stretch = plane.VerticalStretch;

                // The planes are the underside; the layers are counted from the top down, so
                // each one sits the rest of the build-up above the underside.
                mesh.AddExtrusion(
                    facet.Outline,
                    point => plane.HeightAt(point) + (total - end) * stretch,
                    point => plane.HeightAt(point) + (total - start) * stretch);
            }

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
