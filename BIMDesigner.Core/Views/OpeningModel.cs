using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Views;

/// <summary>
/// A door or window in 3D as what it actually is (specification sections 3.5 and 6.1): a frame
/// in the hole, and inside it the leaves or sashes its operation calls for - one leaf or two,
/// panels side by side on two tracks for a slider, a zig-zag of panels for a folding door, a
/// glass drum and wings for a revolving one, horizontal sections for an overhead door; a
/// single pane for a fixed window, sashes for a casement, two overlapping sashes for a
/// slider, three panes standing out from the wall for a bay.
///
/// Everything is laid out in the wall's own frame - distance along the hole, distance across
/// from the wall's centreline toward the exterior, height - so a door in a curved wall follows
/// the curve like the wall does.
/// </summary>
internal static class OpeningModel
{
    private const double FrameFace = 50;
    private const double WindowFrameFace = 60;
    private const double LeafThickness = 40;
    private const double SashDepth = 60;
    private const double SashFace = 50;
    private const double PaneThickness = 12;
    private const double HandleHeight = 1000;

    private static readonly ColourRgb Glass = new(0x8C, 0xC4, 0xE0);
    private static readonly ColourRgb Metal = new(0x3A, 0x3D, 0x42);

    public static void Add(
        Wall wall, WallType wallType, Opening opening, OpeningType type,
        double from, double to, double sill, double head, List<Mesh3D> meshes)
    {
        if (head - sill <= 1 || to - from <= 1) return;

        var builder = new Builder(wall, wallType, opening, type, from, to, sill, head);

        switch (type)
        {
            case DoorType door:
                builder.Door(door);
                break;
            case WindowType window:
                builder.Window(window);
                break;
            default:
                builder.Pane(0, to - from, sill, head, 0);
                break;
        }

        meshes.AddRange(builder.Meshes);
    }

    /// <summary>
    /// A curtain wall's door panel: a door of the chosen type, built to fill the panel from
    /// mullion to mullion and from the floor to the head, with every leaf design and operation
    /// an ordinary door has. It belongs to the wall rather than to a door element of its own,
    /// and it has no architrave - the mullions round it are its frame.
    /// </summary>
    public static void AddPanelDoor(
        Wall wall, WallType body, DoorType type, Guid ownerId, Guid levelId,
        double from, double to, double sill, double head, List<Mesh3D> meshes)
    {
        if (head - sill <= 1 || to - from <= 1) return;

        var panel = new Door { Id = ownerId, LevelId = levelId, TypeId = type.Id, HostWallId = wall.Id };
        var builder = new Builder(wall, body, panel, type, from, to, sill, head) { Trimmed = false };
        builder.Door(type);
        meshes.AddRange(builder.Meshes);
    }

    /// <summary>What a frame or door panel made of this reads as.</summary>
    public static ColourRgb ColourOf(string material, ColourRgb fallback)
    {
        var name = material.ToLowerInvariant();
        if (name.Contains("oak")) return new ColourRgb(0xB0, 0x84, 0x52);
        if (name.Contains("walnut")) return new ColourRgb(0x6E, 0x4B, 0x33);
        if (name.Contains("pine")) return new ColourRgb(0xD6, 0x9E, 0x3E);
        if (name.Contains("mahogany")) return new ColourRgb(0x5E, 0x33, 0x22);
        if (name.Contains("paint") || name.Contains("upvc") || name.Contains("pvc") || name.Contains("white"))
            return new ColourRgb(0xEE, 0xEE, 0xEA);
        if (name.Contains("alumin")) return new ColourRgb(0xB4, 0xB8, 0xBE);
        if (name.Contains("steel") || name.Contains("metal")) return new ColourRgb(0x6A, 0x70, 0x78);
        if (name.Contains("timber") || name.Contains("wood")) return new ColourRgb(0xA8, 0x7A, 0x4E);
        return fallback;
    }

    private sealed class Builder
    {
        private readonly Wall _wall;
        private readonly WallType _wallType;
        private readonly OpeningType _type;
        private readonly Opening _opening;
        private readonly double _from;
        private readonly double _sill;
        private readonly double _head;

        private readonly Mesh3D _frame;
        private readonly Mesh3D _leaf;
        private readonly Mesh3D _glass;
        private readonly Mesh3D _metal;

        public Builder(Wall wall, WallType wallType, Opening opening, OpeningType type, double from, double to, double sill, double head)
        {
            _wall = wall;
            _wallType = wallType;
            _opening = opening;
            _type = type;
            _from = from;
            Width = to - from;
            _sill = sill;
            _head = head;

            var isDoor = type is DoorType;
            var frameColour = ColourOf(type.FrameMaterial, isDoor ? new ColourRgb(0xA8, 0x7A, 0x4E) : new ColourRgb(0xEE, 0xEE, 0xEA));
            var leafColour = type is DoorType door ? ColourOf(door.PanelMaterial, new ColourRgb(0xA8, 0x7A, 0x4E)) : frameColour;

            _frame = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, frameColour, isDoor ? "Door frame" : "Window frame");
            _leaf = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, leafColour, isDoor ? "Door leaf" : "Sash");
            _glass = new Mesh3D(opening.Id, opening.LevelId, MeshKind.Glazing, Glass, "Glazing");
            _metal = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, Metal, "Hardware");
        }

        public double Width { get; }

        /// <summary>Whether the architrave is built: a door in a curtain panel has none.</summary>
        public bool Trimmed { get; init; } = true;

        private double Height => _head - _sill;

        /// <summary>Half the wall's thickness: where its faces are, across from its centreline.</summary>
        private double WallHalf => _wallType.Width / 2;

        /// <summary>How deep the frame is through the wall: the type's frame, no deeper than the wall.</summary>
        private double FrameDepth => Math.Clamp(_type.Thickness > 0 ? _type.Thickness : 100, 20, Math.Max(_wallType.Width, 20));

        public IEnumerable<Mesh3D> Meshes => new[] { _frame, _leaf, _glass, _metal }.Where(m => !m.IsEmpty);

        // ---- doors -------------------------------------------------------------------

        public void Door(DoorType door)
        {
            // A lining round the jambs and head; nothing across the threshold.
            var f = FrameFace;
            var depth = FrameDepth / 2;
            Box(_frame, 0, f, -depth, depth, _sill, _head);
            Box(_frame, Width - f, Width, -depth, depth, _sill, _head);
            Box(_frame, f, Width - f, -depth, depth, _head - f, _head);

            var clearFrom = f;
            var clearTo = Width - f;
            var top = _head - f;
            _design = door.LeafDesign;
            _glazingRows = door.GlazingRows;
            _glazingColumns = door.GlazingColumns;

            if (Trimmed) Trim(door);

            switch (door.Operation)
            {
                case DoorOperation.DoubleSwing:
                    TwoLeaves(clearFrom, clearTo, top);
                    break;

                case DoorOperation.Swing when door.LeafCount >= 2:
                    TwoLeaves(clearFrom, clearTo, top);
                    break;

                case DoorOperation.Swing:
                {
                    Leaf(clearFrom, clearTo, top, 0);

                    // The handle on the side away from the hinges.
                    var latch = _opening.FlipHand ? clearFrom + 70 : clearTo - 70;
                    Handles(latch, 0);
                    break;
                }

                case DoorOperation.Sliding when door.LeafCount >= 2:
                {
                    // Two leaves on two tracks, overlapping in the middle.
                    var middle = (clearFrom + clearTo) / 2;
                    Leaf(clearFrom, middle + 30, top, 25);
                    Leaf(middle - 30, clearTo, top, -25);
                    PullBar(clearFrom + 60, 25 + LeafThickness / 2 + 10);
                    PullBar(clearTo - 60, -25 - LeafThickness / 2 - 10);
                    Box(_metal, clearFrom, clearTo, -45, 45, top - 25, top);
                    break;
                }

                case DoorOperation.Sliding:
                {
                    // One leaf hung on the face of the wall, and the track it runs along to
                    // park over the wall beside the opening.
                    var side = _opening.FlipFacing ? 1.0 : -1.0;
                    var across = side * (WallHalf + LeafThickness / 2 + 10);
                    Leaf(clearFrom - 40, clearTo + 40, top + 20, across);
                    var trackAcross = side * (WallHalf + 25);
                    Box(_metal, clearFrom - 40, clearTo + (clearTo - clearFrom) + 40,
                        Math.Min(trackAcross - 15, trackAcross + 15), Math.Max(trackAcross - 15, trackAcross + 15), top + 20, top + 60);
                    PullBar(_opening.FlipHand ? clearTo - 60 : clearFrom + 60, across + side * (LeafThickness / 2 + 10));
                    break;
                }

                case DoorOperation.Folding:
                    Folding(clearFrom, clearTo, top, Math.Max(4, door.LeafCount * 2));
                    break;

                case DoorOperation.Revolving:
                    Revolving(top);
                    break;

                case DoorOperation.Overhead:
                    Overhead(clearFrom, clearTo, top);
                    break;
            }
        }

        /// <summary>
        /// The architrave round the opening on each face of the wall: up both jambs and across the
        /// head, as wide as the type says and standing off the face by its projection.
        /// </summary>
        private void Trim(DoorType door)
        {
            var width = door.TrimWidth;
            if (width <= 0) return;

            foreach (var (face, projection) in new[] { (WallHalf, door.TrimProjectionExterior), (-WallHalf, -door.TrimProjectionInterior) })
            {
                if (Math.Abs(projection) <= 0.5) continue;
                var (a0, a1) = (Math.Min(face, face + projection), Math.Max(face, face + projection));
                Box(_frame, -width, 0, a0, a1, _sill, _head + width);
                Box(_frame, Width, Width + width, a0, a1, _sill, _head + width);
                Box(_frame, 0, Width, a0, a1, _head, _head + width);
            }
        }

        private void TwoLeaves(double clearFrom, double clearTo, double top)
        {
            var middle = (clearFrom + clearTo) / 2;
            Leaf(clearFrom, middle - 2, top, 0);
            Leaf(middle + 2, clearTo, top, 0);
            Handles(middle - 70, 0);
            Handles(middle + 70, 0);
        }

        // ---- door leaves, as their design says ------------------------------------------

        private DoorLeafDesign _design = DoorLeafDesign.Panelled;
        private int _glazingRows = 4;
        private int _glazingColumns = 1;

        /// <summary>
        /// A flat leaf standing on the straight line from one plan point to another: distances
        /// along it (s), across it (t) and heights (z) are what a leaf's design is drawn in, so the
        /// same design serves a leaf in the wall and a panel of a folding door.
        /// </summary>
        private readonly record struct LeafFrame(Point2D Origin, Vector2D Along, Vector2D Across, double Width)
        {
            public static LeafFrame Between(Point2D a, Point2D b)
            {
                var run = b - a;
                var along = run.NormalisedOrDefault(Vector2D.UnitX);
                return new LeafFrame(a, along, along.PerpendicularLeft(), run.Length);
            }

            public Point3D At(double s, double t, double z)
            {
                var plan = Origin + Along * s + Across * t;
                return new Point3D(plan.X, plan.Y, z);
            }

            public Point2D Plan(double s, double t) => Origin + Along * s + Across * t;
        }

        /// <summary>A door leaf in the plane across from the centreline, floor to top, built as the type's design.</summary>
        private void Leaf(double u0, double u1, double top, double across) =>
            DrawLeaf(LeafFrame.Between(P(u0, across), P(u1, across)), _sill, top);

        /// <summary>
        /// A leaf of the type's design: a flush slab; stiles and rails round raised panels; round
        /// one pane; round panes divided by glazing bars over a panel (French); glass over panels;
        /// louvres over a panel; or panels under an arched top light with sunburst bars.
        /// </summary>
        private void DrawLeaf(LeafFrame f, double bottom, double top)
        {
            var w = f.Width;
            var h = top - bottom;
            if (w <= 40 || h <= 200) return;

            var t = LeafThickness / 2;
            if (_design == DoorLeafDesign.Flush)
            {
                Block(_leaf, f, 0, w, -t, t, bottom, top);
                return;
            }

            if (_design == DoorLeafDesign.FullGlass)
            {
                // A shopfront leaf: one clear pane in a slim frame - narrow stiles, a shallow
                // top rail and a kicking rail at the foot - which is what a curtain wall door is.
                var slim = Math.Min(90, w * 0.14);
                var head = Math.Min(100, h * 0.06);
                var kick = Math.Min(200, h * 0.09);
                Block(_leaf, f, 0, slim, -t, t, bottom, top);
                Block(_leaf, f, w - slim, w, -t, t, bottom, top);
                Block(_leaf, f, slim, w - slim, -t, t, top - head, top);
                Block(_leaf, f, slim, w - slim, -t, t, bottom, bottom + kick);
                GlassIn(f, slim, w - slim, bottom + kick, top - head);
                return;
            }

            // The frame every other design is built in: two stiles, a top rail and a deep bottom rail.
            var stile = Math.Min(110, w * 0.16);
            var topRail = Math.Min(110, h * 0.06);
            var bottomRail = Math.Min(220, h * 0.11);
            Block(_leaf, f, 0, stile, -t, t, bottom, top);
            Block(_leaf, f, w - stile, w, -t, t, bottom, top);
            Block(_leaf, f, stile, w - stile, -t, t, top - topRail, top);
            Block(_leaf, f, stile, w - stile, -t, t, bottom, bottom + bottomRail);

            var (s0, s1) = (stile, w - stile);
            var (z0, z1) = (bottom + bottomRail, top - topRail);
            const double rail = 100;

            switch (_design)
            {
                case DoorLeafDesign.Panelled:
                {
                    // A lock rail a little under halfway, and a panel either side of it.
                    var lockRail = bottom + h * 0.42;
                    Block(_leaf, f, s0, s1, -t, t, lockRail, lockRail + rail);
                    Panels(f, s0, s1, z0, lockRail);
                    Panels(f, s0, s1, lockRail + rail, z1);
                    break;
                }

                case DoorLeafDesign.Glazed:
                    GlassIn(f, s0, s1, z0, z1);
                    break;

                case DoorLeafDesign.FrenchGlazed:
                {
                    // A panel at the foot, then glass divided into panes by glazing bars.
                    var lockRail = z0 + h * 0.18;
                    Panels(f, s0, s1, z0, lockRail, columns: 1);
                    Block(_leaf, f, s0, s1, -t, t, lockRail, lockRail + rail);
                    GlassIn(f, s0, s1, lockRail + rail, z1);
                    GlazingBars(f, s0, s1, lockRail + rail, z1, _glazingRows, _glazingColumns);
                    break;
                }

                case DoorLeafDesign.HalfGlazed:
                {
                    var lockRail = bottom + h * 0.42;
                    Panels(f, s0, s1, z0, lockRail);
                    Block(_leaf, f, s0, s1, -t, t, lockRail, lockRail + rail);
                    GlassIn(f, s0, s1, lockRail + rail, z1);
                    break;
                }

                case DoorLeafDesign.Louvred:
                {
                    // A panel at the foot and slatted louvres above it.
                    var lockRail = z0 + h * 0.28;
                    Panels(f, s0, s1, z0, lockRail, columns: 1);
                    Block(_leaf, f, s0, s1, -t, t, lockRail, lockRail + rail);
                    Louvres(f, s0, s1, lockRail + rail, z1);
                    break;
                }

                case DoorLeafDesign.ArchedTopLight:
                {
                    // Panels below, a transom rail, and over it an arched light with sunburst bars.
                    var archRise = Math.Min((s1 - s0) / 2, h * 0.2);
                    var springing = z1 - archRise - 20;
                    var lockRail = bottom + h * 0.42;
                    Panels(f, s0, s1, z0, lockRail);
                    Block(_leaf, f, s0, s1, -t, t, lockRail, lockRail + rail);
                    Panels(f, s0, s1, lockRail + rail, springing - rail);
                    Block(_leaf, f, s0, s1, -t, t, springing - rail, springing);
                    ArchedLight(f, s0, s1, springing, z1);
                    break;
                }

                default:
                    GlassIn(f, s0, s1, z0, z1);
                    break;
            }
        }

        /// <summary>Raised panels filling a stretch of the leaf, side by side with a muntin between on a wide leaf.</summary>
        private void Panels(LeafFrame f, double s0, double s1, double z0, double z1, int columns = 0)
        {
            if (z1 - z0 < 60 || s1 - s0 < 60) return;

            const double muntin = 90;
            if (columns == 0) columns = s1 - s0 > 560 ? 2 : 1;
            var width = (s1 - s0 - muntin * (columns - 1)) / columns;

            for (var c = 0; c < columns; c++)
            {
                var a = s0 + c * (width + muntin);
                var b = a + width;
                if (c > 0) Block(_leaf, f, a - muntin, a, -LeafThickness / 2, LeafThickness / 2, z0, z1);

                // A thin panel set back in the frame, and a raised field proud of it on both faces.
                Block(_leaf, f, a, b, -8, 8, z0, z1);
                var inset = Math.Min(45, Math.Min(b - a, z1 - z0) / 4);
                Block(_leaf, f, a + inset, b - inset, -14, 14, z0 + inset, z1 - inset);
            }
        }

        private void GlassIn(LeafFrame f, double s0, double s1, double z0, double z1) =>
            Block(_glass, f, s0, s1, -PaneThickness / 2, PaneThickness / 2, z0, z1);

        /// <summary>Bars dividing a pane into rows and columns of smaller ones.</summary>
        private void GlazingBars(LeafFrame f, double s0, double s1, double z0, double z1, int rows, int columns)
        {
            const double bar = 30;
            for (var r = 1; r < rows; r++)
            {
                var z = z0 + (z1 - z0) * r / rows;
                Block(_leaf, f, s0, s1, -14, 14, z - bar / 2, z + bar / 2);
            }

            for (var c = 1; c < columns; c++)
            {
                var s = s0 + (s1 - s0) * c / columns;
                Block(_leaf, f, s - bar / 2, s + bar / 2, -14, 14, z0, z1);
            }
        }

        /// <summary>Slats tilted down toward the outside, one above the other, as a louvred door has.</summary>
        private void Louvres(LeafFrame f, double s0, double s1, double z0, double z1)
        {
            const double pitch = 45;
            const double slat = 8;
            var reach = LeafThickness * 0.42;

            for (var zc = z0 + pitch / 2; zc < z1 - pitch / 3; zc += pitch)
            {
                // In section: a thin slat running from high at the back to low at the front.
                var drop = reach * 0.9;
                Prism(_leaf, f, s0, s1, new[]
                {
                    (-reach, zc + drop / 2 + slat / 2), (-reach, zc + drop / 2 - slat / 2),
                    (reach, zc - drop / 2 - slat / 2), (reach, zc - drop / 2 + slat / 2)
                });
            }
        }

        /// <summary>
        /// An arched light: glass under a half ellipse rising from the springing to the top rail,
        /// timber spandrels either side of it, and sunburst bars fanning out from a hub.
        /// </summary>
        private void ArchedLight(LeafFrame f, double s0, double s1, double springing, double top)
        {
            var t = LeafThickness / 2;
            var middle = (s0 + s1) / 2;
            var halfWidth = (s1 - s0) / 2;
            var rise = top - springing - 10;
            if (rise < 40) return;

            const int steps = 24;
            var arc = Enumerable.Range(0, steps + 1)
                .Select(i => Math.PI - Math.PI * i / steps)
                .Select(a => (S: middle + halfWidth * Math.Cos(a), Z: springing + rise * Math.Sin(a)))
                .ToList();

            // The glass under the arch.
            Plate(_glass, f, arc, -PaneThickness / 2, PaneThickness / 2);

            // The timber either side of it, up to the top rail.
            var half = steps / 2;
            var left = new List<(double S, double Z)> { (s0, springing) };
            left.AddRange(arc.Take(half + 1));
            left.Add((middle, top));
            left.Add((s0, top));
            Plate(_leaf, f, left, -t, t);

            var right = new List<(double S, double Z)> { (middle, top) };
            right.AddRange(arc.Skip(half));
            right.Add((s1, springing));
            right.Add((s1, top));
            Plate(_leaf, f, right, -t, t);

            // Sunburst bars from the hub to the arch, and the hub itself.
            const double bar = 24;
            foreach (var angle in new[] { Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4 })
            {
                var (dx, dz) = (Math.Cos(angle), Math.Sin(angle));
                var (nx, nz) = (-dz * bar / 2, dx * bar / 2);
                var (ex, ez) = (middle + halfWidth * dx, springing + rise * dz);
                Plate(_leaf, f, new[]
                {
                    (middle + nx, springing + nz), (ex + nx, ez + nz), (ex - nx, ez - nz), (middle - nx, springing - nz)
                }, -12, 12);
            }

            var hubRadius = Math.Min(halfWidth, rise) * 0.22;
            var hub = Enumerable.Range(0, 13)
                .Select(i => Math.PI - Math.PI * i / 12)
                .Select(a => (middle + hubRadius * Math.Cos(a), springing + hubRadius * Math.Sin(a)))
                .ToList();
            Plate(_leaf, f, hub, -14, 14);
        }

        // ---- building blocks in a leaf's own frame ------------------------------------------

        /// <summary>A block in a leaf frame: between two distances along it, two across it and two heights.</summary>
        private static void Block(Mesh3D mesh, LeafFrame f, double s0, double s1, double t0, double t1, double z0, double z1)
        {
            if (s1 - s0 <= 0.5 || t1 - t0 <= 0.5 || z1 - z0 <= 0.5) return;
            mesh.AddExtrusion(new[] { f.Plan(s0, t0), f.Plan(s1, t0), f.Plan(s1, t1), f.Plan(s0, t1) }, z0, z1);
        }

        /// <summary>
        /// A flat piece of any outline drawn on the leaf's face - along and up - given a thickness
        /// across it. Every face is put in both ways round, so it shows from either side whichever
        /// way round the outline was drawn.
        /// </summary>
        private static void Plate(Mesh3D mesh, LeafFrame f, IReadOnlyList<(double S, double Z)> outline, double t0, double t1)
        {
            if (outline.Count < 3) return;

            var flat = outline.Select(p => new Point2D(p.S, p.Z)).ToList();
            foreach (var (a, b, c) in Polygon2D.Triangulate(flat))
            foreach (var t in new[] { t0, t1 })
            {
                var (pa, pb, pc) = (f.At(outline[a].S, t, outline[a].Z), f.At(outline[b].S, t, outline[b].Z), f.At(outline[c].S, t, outline[c].Z));
                mesh.AddTriangle(pa, pb, pc);
                mesh.AddTriangle(pa, pc, pb);
            }

            for (var i = 0; i < outline.Count; i++)
            {
                var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
                var (p0, q0, q1, p1) = (f.At(p.S, t0, p.Z), f.At(q.S, t0, q.Z), f.At(q.S, t1, q.Z), f.At(p.S, t1, p.Z));
                mesh.AddQuad(p0, q0, q1, p1);
                mesh.AddQuad(p0, p1, q1, q0);
                mesh.AddEdge(p0, q0);
                mesh.AddEdge(p1, q1);
            }
        }

        /// <summary>A section - across and up - run along the leaf between two distances: a louvre slat.</summary>
        private static void Prism(Mesh3D mesh, LeafFrame f, double s0, double s1, IReadOnlyList<(double T, double Z)> section)
        {
            for (var i = 0; i < section.Count; i++)
            {
                var (p, q) = (section[i], section[(i + 1) % section.Count]);
                var (a, b, c, d) = (f.At(s0, p.T, p.Z), f.At(s1, p.T, p.Z), f.At(s1, q.T, q.Z), f.At(s0, q.T, q.Z));
                mesh.AddQuad(a, b, c, d);
                mesh.AddQuad(a, d, c, b);
            }

            foreach (var s in new[] { s0, s1 })
            {
                var cap = section.Select(p => f.At(s, p.T, p.Z)).ToList();
                for (var i = 1; i + 1 < cap.Count; i++)
                {
                    mesh.AddTriangle(cap[0], cap[i], cap[i + 1]);
                    mesh.AddTriangle(cap[0], cap[i + 1], cap[i]);
                }
            }

            mesh.AddEdge(f.At(s0, section[0].T, section[0].Z), f.At(s1, section[0].T, section[0].Z));
        }

        /// <summary>A lever handle on each face of a leaf at the usual height.</summary>
        private void Handles(double at, double across)
        {
            var z = _sill + Math.Min(HandleHeight, (_head - _sill) * 0.5);
            var face = LeafThickness / 2;
            foreach (var side in new[] { 1.0, -1.0 })
            {
                var near = across + side * face;
                var far = across + side * (face + 55);
                Box(_metal, at - 12, at + 12, Math.Min(near, far), Math.Max(near, far), z - 12, z + 12);
                Box(_metal, Math.Min(at, at + (at > Width / 2 ? -130 : 130)), Math.Max(at, at + (at > Width / 2 ? -130 : 130)),
                    Math.Min(far - side * 18, far), Math.Max(far - side * 18, far), z - 9, z + 9);
            }
        }

        /// <summary>A long vertical pull on one face, as sliding doors have.</summary>
        private void PullBar(double at, double across)
        {
            var z = _sill + Math.Min(HandleHeight, (_head - _sill) * 0.5);
            Box(_metal, at - 10, at + 10, across - 10, across + 10, z - 300, z + 300);
        }

        /// <summary>Panels folded concertina-fashion, alternately either side of the centreline.</summary>
        private void Folding(double clearFrom, double clearTo, double top, int panels)
        {
            var width = (clearTo - clearFrom) / panels;
            var swing = Math.Min(width * 0.35, 90);
            var points = Enumerable.Range(0, panels + 1)
                .Select(i => P(clearFrom + i * width, i % 2 == 0 ? -swing / 2 : swing / 2))
                .ToList();

            // Each panel in the door's design - louvred, glazed, panelled - as a bi-fold closet door's are.
            for (var i = 0; i < panels; i++)
                DrawLeaf(LeafFrame.Between(points[i], points[i + 1]), _sill, top);
        }

        /// <summary>A glass drum either side, four wings turning in it, and a canopy over.</summary>
        private void Revolving(double top)
        {
            var radius = Width / 2 - FrameFace;
            var centreAlong = Width / 2;

            // Plan points in the wall's frame, at an angle round the drum from along the wall.
            Point2D Round(double angle, double r) =>
                P(centreAlong + r * Math.Cos(angle), r * Math.Sin(angle));

            const int steps = 10;
            foreach (var middle in new[] { 0.0, Math.PI })
            {
                for (var i = 0; i < steps; i++)
                {
                    var a0 = middle - 0.95 + 1.9 * i / steps;
                    var a1 = middle - 0.95 + 1.9 * (i + 1) / steps;
                    Slab(_glass, Round(a0, radius), Round(a1, radius), PaneThickness, _sill, top);
                }
            }

            for (var k = 0; k < 4; k++)
            {
                var angle = Math.PI / 4 + k * Math.PI / 2;
                Slab(_glass, Round(angle, 30), Round(angle, radius - 30), PaneThickness, _sill + 20, top - 20);
                Slab(_metal, Round(angle, radius - 70), Round(angle, radius - 30), 40, _sill + 20, top - 20);
            }

            _metal.AddExtrusion(Enumerable.Range(0, 4).Select(k => Round(k * Math.PI / 2 + Math.PI / 4, 40)).ToList(), _sill, top);

            var canopy = Enumerable.Range(0, 24).Select(i => Round(2 * Math.PI * i / 24, radius + 60)).ToList();
            _frame.AddExtrusion(canopy, top, Math.Min(_head, top + 150) + 150);
        }

        /// <summary>A sectional overhead door: horizontal panels one above the other.</summary>
        private void Overhead(double clearFrom, double clearTo, double top)
        {
            var sections = Math.Max(4, (int)Math.Round((top - _sill) / 550));
            var height = (top - _sill) / sections;
            for (var i = 0; i < sections; i++)
                Box(_leaf, clearFrom, clearTo, -LeafThickness / 2, LeafThickness / 2, _sill + i * height + 6, _sill + (i + 1) * height - 6);

            Handles((clearFrom + clearTo) / 2, 0);
        }

        // ---- windows -----------------------------------------------------------------

        public void Window(WindowType window)
        {
            if (window.Operation == WindowOperation.Bay)
            {
                Bay();
                return;
            }

            // The frame all round, and a sill board standing out from the outside face.
            var f = WindowFrameFace;
            var depth = FrameDepth / 2;
            Box(_frame, 0, f, -depth, depth, _sill, _head);
            Box(_frame, Width - f, Width, -depth, depth, _sill, _head);
            Box(_frame, f, Width - f, -depth, depth, _head - f, _head);
            Box(_frame, f, Width - f, -depth, depth, _sill, _sill + f);
            Box(_frame, -40, Width + 40, 0, WallHalf + 45, _sill - 35, _sill);

            var (u0, u1, z0, z1) = (f, Width - f, _sill + f, _head - f);

            switch (window.Operation)
            {
                case WindowOperation.Fixed:
                    Pane(u0, u1, z0, z1, 0);
                    break;

                case WindowOperation.Casement:
                {
                    var sashes = Width > 1000 ? 2 : 1;
                    var width = (u1 - u0) / sashes;
                    for (var i = 0; i < sashes; i++)
                    {
                        Sash(u0 + i * width, u0 + (i + 1) * width, z0, z1, 0);
                        var latch = sashes == 1 ? u1 - 60 : i == 0 ? u0 + width - 60 : u0 + width + 60;
                        WindowHandle(latch, (z0 + z1) / 2, vertical: true);
                    }

                    break;
                }

                case WindowOperation.Awning:
                {
                    // A fixed light above a top-hung sash: the sash opens out from its head.
                    var split = z0 + (z1 - z0) * 0.55;
                    Box(_frame, u0, u1, -depth, depth, split - SashFace / 2, split + SashFace / 2);
                    Pane(u0, u1, split + SashFace / 2, z1, 0);
                    Sash(u0, u1, z0, split - SashFace / 2, 0);
                    WindowHandle((u0 + u1) / 2, z0 + 70, vertical: false);
                    break;
                }

                case WindowOperation.Sliding:
                {
                    var middle = (u0 + u1) / 2;
                    Sash(u0, middle + SashFace / 2, z0, z1, 22);
                    Sash(middle - SashFace / 2, u1, z0, z1, -22);
                    WindowHandle(middle + SashFace, (z0 + z1) / 2, vertical: true, across: -22);
                    break;
                }

                case WindowOperation.TiltAndTurn:
                {
                    var sashes = Width > 1400 ? 2 : 1;
                    var width = (u1 - u0) / sashes;
                    for (var i = 0; i < sashes; i++)
                    {
                        Sash(u0 + i * width, u0 + (i + 1) * width, z0, z1, 0);
                        WindowHandle(i == sashes - 1 ? u1 - 60 : u0 + width - 60, (z0 + z1) / 2, vertical: true);
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// A bay window: three glazed faces standing out from the wall - one parallel to it, two
        /// splayed back to it - on a seat board, under a cap.
        /// </summary>
        private void Bay()
        {
            var out1 = WallHalf;
            var depth = Math.Clamp(Width * 0.3, 300, 800);
            var corners = new[]
            {
                P(0, out1), P(Width * 0.22, out1 + depth), P(Width * 0.78, out1 + depth), P(Width, out1)
            };

            var outline = corners.Concat(new[] { P(Width, 0), P(0, 0) }).ToList();
            _frame.AddExtrusion(outline, _sill - 60, _sill);
            _frame.AddExtrusion(outline, _head, _head + 100);

            for (var i = 0; i < 3; i++)
            {
                Slab(_glass, corners[i], corners[i + 1], PaneThickness, _sill, _head);
                Slab(_frame, corners[i], corners[i + 1], SashDepth, _sill, _sill + WindowFrameFace);
                Slab(_frame, corners[i], corners[i + 1], SashDepth, _head - WindowFrameFace, _head);
            }

            foreach (var corner in corners)
            {
                var square = new[]
                {
                    new Point2D(corner.X - 35, corner.Y - 35), new Point2D(corner.X + 35, corner.Y - 35),
                    new Point2D(corner.X + 35, corner.Y + 35), new Point2D(corner.X - 35, corner.Y + 35)
                };
                _frame.AddExtrusion(square, _sill, _head);
            }
        }

        /// <summary>An opening sash: a frame of its own round a pane, set a little proud of the fixed frame.</summary>
        private void Sash(double u0, double u1, double z0, double z1, double across)
        {
            var s = SashFace;
            var d = SashDepth / 2;
            if (u1 - u0 <= 2 * s || z1 - z0 <= 2 * s)
            {
                Pane(u0, u1, z0, z1, across);
                return;
            }

            Box(_leaf, u0, u0 + s, across - d, across + d, z0, z1);
            Box(_leaf, u1 - s, u1, across - d, across + d, z0, z1);
            Box(_leaf, u0 + s, u1 - s, across - d, across + d, z0, z0 + s);
            Box(_leaf, u0 + s, u1 - s, across - d, across + d, z1 - s, z1);
            Pane(u0 + s, u1 - s, z0 + s, z1 - s, across);
        }

        /// <summary>A window handle on the room side of the sash.</summary>
        private void WindowHandle(double at, double z, bool vertical, double across = 0)
        {
            var face = across - SashDepth / 2;
            Box(_metal, at - 10, at + 10, face - 30, face, z - 10, z + 10);
            if (vertical) Box(_metal, at - 8, at + 8, face - 38, face - 24, z - 90, z + 10);
            else Box(_metal, at - 70, at + 10, face - 38, face - 24, z - 8, z + 8);
        }

        /// <summary>A pane of glass filling a rectangle of the opening, centred at an offset across the wall.</summary>
        public void Pane(double u0, double u1, double z0, double z1, double across)
        {
            var t = PaneThickness / 2;
            Box(_glass, u0, u1, across - t, across + t, z0, z1);
        }

        // ---- building blocks -----------------------------------------------------------

        /// <summary>A plan point in the wall's frame: this far along the hole, this far across from the centreline.</summary>
        private Point2D P(double along, double across) => _wall.PointAt(_wallType.Structure, _from + along, across);

        /// <summary>
        /// A block between two distances along the hole, two offsets across the wall and two
        /// heights, following the wall if it curves.
        /// </summary>
        private void Box(Mesh3D mesh, double u0, double u1, double a0, double a1, double z0, double z1)
        {
            if (u1 - u0 <= 0.5 || Math.Abs(a1 - a0) <= 0.5 || z1 - z0 <= 0.5) return;
            mesh.AddExtrusion(CurtainGeometry.Band(_wall, _wallType, _from + u0, _from + u1, Math.Max(a0, a1), Math.Min(a0, a1)), z0, z1);
        }

        /// <summary>A flat panel standing on the straight line between two plan points, this thick.</summary>
        private static void Slab(Mesh3D mesh, Point2D a, Point2D b, double thickness, double z0, double z1)
        {
            var along = b - a;
            if (along.Length <= 0.5 || z1 - z0 <= 0.5) return;

            var side = along.NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (thickness / 2);
            mesh.AddExtrusion(new[] { a + side, b + side, b - side, a - side }, z0, z1);
        }
    }
}
