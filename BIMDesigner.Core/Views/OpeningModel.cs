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

    /// <summary>What a frame or door panel made of this reads as.</summary>
    public static ColourRgb ColourOf(string material, ColourRgb fallback)
    {
        var name = material.ToLowerInvariant();
        if (name.Contains("oak")) return new ColourRgb(0xB0, 0x84, 0x52);
        if (name.Contains("walnut")) return new ColourRgb(0x6E, 0x4B, 0x33);
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
            var glazed = door.PanelMaterial.Contains("glaz", StringComparison.OrdinalIgnoreCase);

            switch (door.Operation)
            {
                case DoorOperation.DoubleSwing:
                    TwoLeaves(clearFrom, clearTo, top, glazed);
                    break;

                case DoorOperation.Swing when door.LeafCount >= 2:
                    TwoLeaves(clearFrom, clearTo, top, glazed);
                    break;

                case DoorOperation.Swing:
                {
                    Leaf(clearFrom, clearTo, top, 0, glazed);

                    // The handle on the side away from the hinges.
                    var latch = _opening.FlipHand ? clearFrom + 70 : clearTo - 70;
                    Handles(latch, 0);
                    break;
                }

                case DoorOperation.Sliding when door.LeafCount >= 2:
                {
                    // Two leaves on two tracks, overlapping in the middle.
                    var middle = (clearFrom + clearTo) / 2;
                    Leaf(clearFrom, middle + 30, top, 25, glazed);
                    Leaf(middle - 30, clearTo, top, -25, glazed);
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
                    Leaf(clearFrom - 40, clearTo + 40, top + 20, across, glazed);
                    var trackAcross = side * (WallHalf + 25);
                    Box(_metal, clearFrom - 40, clearTo + (clearTo - clearFrom) + 40,
                        Math.Min(trackAcross - 15, trackAcross + 15), Math.Max(trackAcross - 15, trackAcross + 15), top + 20, top + 60);
                    PullBar(_opening.FlipHand ? clearTo - 60 : clearFrom + 60, across + side * (LeafThickness / 2 + 10));
                    break;
                }

                case DoorOperation.Folding:
                    Folding(clearFrom, clearTo, top, Math.Max(4, door.LeafCount * 2), glazed);
                    break;

                case DoorOperation.Revolving:
                    Revolving(top);
                    break;

                case DoorOperation.Overhead:
                    Overhead(clearFrom, clearTo, top);
                    break;
            }
        }

        private void TwoLeaves(double clearFrom, double clearTo, double top, bool glazed)
        {
            var middle = (clearFrom + clearTo) / 2;
            Leaf(clearFrom, middle - 2, top, 0, glazed);
            Leaf(middle + 2, clearTo, top, 0, glazed);
            Handles(middle - 70, 0);
            Handles(middle + 70, 0);
        }

        /// <summary>
        /// A door leaf in the plane across from the centreline, floor to top. A glazed one is a
        /// frame of stiles and rails round a pane.
        /// </summary>
        private void Leaf(double u0, double u1, double top, double across, bool glazed)
        {
            var t = LeafThickness / 2;
            if (!glazed)
            {
                Box(_leaf, u0, u1, across - t, across + t, _sill, top);

                // A shallow panel line on each face, so it reads as a door rather than a slab.
                var inset = Math.Min(120, (u1 - u0) / 5);
                foreach (var face in new[] { across + t, across - t })
                {
                    var (a0, a1) = (Math.Min(face, face + Math.Sign(face - across) * 3), Math.Max(face, face + Math.Sign(face - across) * 3));
                    Box(_leaf, u0 + inset, u1 - inset, a0, a1, _sill + 150, _sill + (top - _sill) * 0.45);
                    Box(_leaf, u0 + inset, u1 - inset, a0, a1, _sill + (top - _sill) * 0.52, top - 150);
                }

                return;
            }

            const double stile = 100;
            Box(_leaf, u0, u0 + stile, across - t, across + t, _sill, top);
            Box(_leaf, u1 - stile, u1, across - t, across + t, _sill, top);
            Box(_leaf, u0 + stile, u1 - stile, across - t, across + t, top - stile, top);
            Box(_leaf, u0 + stile, u1 - stile, across - t, across + t, _sill, _sill + 200);
            Pane(u0 + stile, u1 - stile, _sill + 200, top - stile, across);
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
        private void Folding(double clearFrom, double clearTo, double top, int panels, bool glazed)
        {
            var width = (clearTo - clearFrom) / panels;
            var swing = Math.Min(width * 0.35, 90);
            var points = Enumerable.Range(0, panels + 1)
                .Select(i => P(clearFrom + i * width, i % 2 == 0 ? -swing / 2 : swing / 2))
                .ToList();

            for (var i = 0; i < panels; i++)
            {
                Slab(glazed ? _glass : _leaf, points[i], points[i + 1], glazed ? PaneThickness : 30, _sill, top);
                if (glazed) Slab(_leaf, points[i], points[i + 1], 34, top - 80, top);
            }
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
