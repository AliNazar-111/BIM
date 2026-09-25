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

    /// <summary>Where a hand is: the height a door handle sits at on an ordinary door.</summary>
    private const double HandleHeight = 1000;

    /// <summary>As high as a handle is ever set, however tall the leaf carrying it.</summary>
    private const double TallestHandle = 1400;

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
        Wall wall, WallType body, DoorType type, Guid panelId, Guid levelId,
        double from, double to, double sill, double head, List<Mesh3D> meshes,
        bool flipHand = false, bool flipFacing = false, CurtainGlass glazing = CurtainGlass.Clear,
        bool isOpen = false)
    {
        if (head - sill <= 1 || to - from <= 1) return;

        // The door or window is the panel, so clicking it picks the panel out; it still belongs
        // to the wall, so selecting the wall lights it up with everything else.
        var panel = new Door
        {
            Id = panelId, LevelId = levelId, TypeId = type.Id, HostWallId = wall.Id,
            FlipHand = flipHand, FlipFacing = flipFacing, IsOpen = isOpen
        };

        var builder = new Builder(wall, body, panel, type, from, to, sill, head, wall.Id, glazing) { Trimmed = false };
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
        private readonly Mesh3D _hinges;

        public Builder(
            Wall wall, WallType wallType, Opening opening, OpeningType type,
            double from, double to, double sill, double head, Guid? partOf = null, CurtainGlass? glazing = null)
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

            // A door can be framed in something other than its type says.
            if (opening is Door instance && instance.FrameMaterial.Length > 0)
                frameColour = ColourOf(instance.FrameMaterial, frameColour);

            // What the door is part of, when it is part of something larger: a curtain wall.
            var owner = partOf ?? opening.Id;

            _frame = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, frameColour, isDoor ? "Door frame" : "Window frame") { OwnerId = owner };
            _leaf = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, leafColour, isDoor ? "Door leaf" : "Sash") { OwnerId = owner };
            // A door in a curtain wall is glazed with whatever the panel is glazed with: tinted,
            // frosted, laminated, or an opaque spandrel panel.
            _glass = glazing is { } pane
                ? new Mesh3D(opening.Id, opening.LevelId, MeshKind.Glazing, CurtainGlassLook.ColourOf(pane, Glass),
                        pane == CurtainGlass.Clear ? "Glazing" : CurtainGlassLook.NameOf(pane))
                    { OwnerId = owner, Opacity = CurtainGlassLook.OpacityOf(pane) }
                : new Mesh3D(opening.Id, opening.LevelId, MeshKind.Glazing, Glass, "Glazing") { OwnerId = owner };
            _metal = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, Metal, "Hardware") { OwnerId = owner };
            _hinges = new Mesh3D(opening.Id, opening.LevelId, MeshKind.DoorLeaf, Metal, "Hinges") { OwnerId = owner };
        }

        public double Width { get; }

        /// <summary>Whether the architrave is built: a door in a curtain panel has none.</summary>
        public bool Trimmed { get; init; } = true;

        private double Height => _head - _sill;

        /// <summary>Half the wall's thickness: where its faces are, across from its centreline.</summary>
        private double WallHalf => _wallType.Width / 2;

        /// <summary>How deep the frame is through the wall: the type's frame, no deeper than the wall.</summary>
        private double FrameDepth => Math.Clamp(_type.Thickness > 0 ? _type.Thickness : 100, 20, Math.Max(_wallType.Width, 20));

        /// <summary>
        /// Which side of the wall the opening faces - the side it swings out to. This is what
        /// facing means, and it is what flipping it changes, shut as well as open.
        /// </summary>
        private double Facing => _opening.FlipFacing ? -1 : 1;

        /// <summary>
        /// How far across the wall a shut leaf or sash sits from the centreline. A door is hung
        /// in a rebate with its face flush on the side it opens to and the stop behind it, so
        /// which face the leaf lies in is the visible half of what facing means: flip it and the
        /// leaf moves across the reveal to the other side, hinges and all.
        /// </summary>
        private double SetIn(double leafThickness) =>
            Facing * Math.Clamp(FrameDepth / 2 - leafThickness / 2, 0, Math.Max(0, WallHalf - leafThickness / 2));

        /// <summary>
        /// Which side of a leaf, measured in its own frame, it opens toward. A leaf's frame runs
        /// from its hinge, so one hung on its far edge is turned back to front and its swing with it.
        /// </summary>
        private double SwingSide(bool hingeAtStart) => Facing * (hingeAtStart ? 1 : -1);

        public IEnumerable<Mesh3D> Meshes => new[] { _frame, _leaf, _glass, _metal, _hinges }.Where(m => !m.IsEmpty);

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
                    // The leaf where it stands, and the handle on the leaf itself, on the edge
                    // away from the hinges - so it goes with the leaf when the door is open.
                    var hingeAtStart = !_opening.FlipHand;
                    var frame = Hung(clearFrom, clearTo, SetIn(LeafThickness), hingeAtStart);

                    DrawLeaf(frame, _sill, top);
                    Handles(frame, frame.Width - 70);
                    Hinges(frame, _sill, top, SwingSide(hingeAtStart), LeafThickness);
                    break;
                }

                case DoorOperation.Sliding when door.LeafCount >= 2:
                {
                    // Two leaves on two tracks, overlapping in the middle - and when it stands
                    // open, one run across the other.
                    var middle = (clearFrom + clearTo) / 2;
                    var slide = _opening.IsOpen ? middle - clearFrom : 0;
                    Leaf(clearFrom + slide, middle + 30 + slide, top, 25);
                    Leaf(middle - 30, clearTo, top, -25);

                    // The pull on the leaf that runs goes with it.
                    PullBar(clearFrom + 60 + slide, 25 + LeafThickness / 2 + 10);
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

                    // Open, it has run along its track to park over the wall beside the hole.
                    var parked = _opening.IsOpen ? (_opening.FlipHand ? -1 : 1) * (clearTo - clearFrom) : 0;
                    Leaf(clearFrom - 40 + parked, clearTo + 40 + parked, top + 20, across);
                    var trackAcross = side * (WallHalf + 25);
                    Box(_metal, clearFrom - 40, clearTo + (clearTo - clearFrom) + 40,
                        Math.Min(trackAcross - 15, trackAcross + 15), Math.Max(trackAcross - 15, trackAcross + 15), top + 20, top + 60);
                    PullBar((_opening.FlipHand ? clearTo - 60 : clearFrom + 60) + parked, across + side * (LeafThickness / 2 + 10));
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
            // A pair is hung one leaf on each jamb, so they swing apart rather than together.
            var middle = (clearFrom + clearTo) / 2;
            var set = SetIn(LeafThickness);
            var left = Hung(clearFrom, middle - 2, set, hingeAtStart: true);
            var right = Hung(middle + 2, clearTo, set, hingeAtStart: false);

            DrawLeaf(left, _sill, top);
            DrawLeaf(right, _sill, top);

            // A handle on each leaf, on the meeting stile it is latched at, and hinges on the
            // jamb each is hung on.
            Handles(left, left.Width - 70);
            Handles(right, right.Width - 70);
            Hinges(left, _sill, top, SwingSide(hingeAtStart: true), LeafThickness);
            Hinges(right, _sill, top, SwingSide(hingeAtStart: false), LeafThickness);
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
            DrawLeaf(Hung(u0, u1, across, hingeAtStart: !_opening.FlipHand), _sill, top);

        /// <summary>
        /// Where a leaf actually is: across its opening when the door is shut, and turned about
        /// its hinge when it is open. A door drawn open shows the space its leaf takes and where
        /// it lands, which is the thing a plan gets checked for.
        /// </summary>
        private LeafFrame Hung(double u0, double u1, double across, bool hingeAtStart, double? swing = null)
        {
            // Measured from the hinge outward whichever way the leaf is hung, so anything set
            // on the leaf - a handle, a pull - is in the same place on it open or shut.
            var shut = hingeAtStart
                ? LeafFrame.Between(P(u0, across), P(u1, across))
                : LeafFrame.Between(P(u1, across), P(u0, across));

            if (!_opening.IsOpen) return shut;
            if (swing is null && _type is not DoorType { Operation: DoorOperation.Swing or DoorOperation.DoubleSwing }) return shut;

            var turned = swing ?? (_opening as Door)?.SwingAngle ?? 90;
            var hinge = P(hingeAtStart ? u0 : u1, across);

            // Turned toward the side it opens to, from the jamb it is hung on.
            var closed = (P(hingeAtStart ? u1 : u0, across) - hinge).NormalisedOrDefault(Vector2D.UnitX);
            var turn = (_opening.FlipFacing ? -1 : 1) * (hingeAtStart ? 1 : -1) * turned * Math.PI / 180;

            var open = new Vector2D(
                closed.X * Math.Cos(turn) - closed.Y * Math.Sin(turn),
                closed.X * Math.Sin(turn) + closed.Y * Math.Cos(turn));

            return new LeafFrame(hinge, open, open.PerpendicularLeft(), u1 - u0);
        }

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
        /// <summary>
        /// How high the handle sits. A door handle is at about a metre whatever the door,
        /// because that is where a hand is - but a tall leaf carries it higher, the way the pull
        /// on a shopfront door is set, and on a low one it never goes past halfway up.
        /// </summary>
        private double HandleAt => _sill + Math.Min(Math.Clamp(Height * 0.45, HandleHeight, TallestHandle), Height * 0.5);

        private void Handles(double at, double across)
        {
            var z = HandleAt;
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

        /// <summary>
        /// The same pair of handles, but screwed to a leaf rather than set in the doorway: this
        /// far along the leaf from its hinge, so they travel with it as it swings open.
        /// </summary>
        private void Handles(LeafFrame f, double s)
        {
            var z = HandleAt;
            var face = LeafThickness / 2;
            foreach (var side in new[] { 1.0, -1.0 })
            {
                var near = side * face;
                var far = side * (face + 55);

                // The lever lies back toward the hinge, as a lever does.
                var tail = s - 130;
                Block(_metal, f, s - 12, s + 12, Math.Min(near, far), Math.Max(near, far), z - 12, z + 12);
                Block(_metal, f, Math.Min(s, tail), Math.Max(s, tail),
                    Math.Min(far - side * 18, far), Math.Max(far - side * 18, far), z - 9, z + 9);
            }
        }

        /// <summary>
        /// The hinges a leaf is hung on: knuckles up its hanging edge, standing proud on the side
        /// it opens to. They are what shows at a glance which way a shut door faces, and being on
        /// the leaf they go round with it when it opens.
        /// </summary>
        private void Hinges(LeafFrame f, double bottom, double top, double side, double thickness)
        {
            var height = top - bottom;
            if (height <= 400 || f.Width <= 200) return;

            var face = thickness / 2;
            var (t0, t1) = (Math.Min(side * face, side * (face + 14)), Math.Max(side * face, side * (face + 14)));

            foreach (var at in new[] { bottom + height * 0.13, bottom + height * 0.5, bottom + height * 0.87 })
                Block(_hinges, f, 0, 32, t0, t1, at - Math.Min(55, height * 0.05), at + Math.Min(55, height * 0.05));
        }

        /// <summary>A long vertical pull on one face, as sliding doors have.</summary>
        private void PullBar(double at, double across)
        {
            // The bar grows with the leaf: a long pull on a tall shopfront door, a short one on
            // a domestic slider.
            var z = HandleAt;
            var half = Math.Clamp(Height * 0.28, 500, 1400) / 2;
            Box(_metal, at - 10, at + 10, across - 10, across + 10, z - half, z + half);
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

            // A sash's panes are its own: six over six is six panes in each sash, not six in
            // the hole. So the bars are drawn with the sash that carries them.
            var rows = Math.Max(1, window.GlazingRows);
            var columns = Math.Max(1, window.GlazingColumns);

            void Glazed(SashPlace place, double bottom, double top)
            {
                var inset = place.Width <= 2 * SashFace || top - bottom <= 2 * SashFace ? 0 : SashFace;
                GlazingBars(place, inset, place.Width - inset, bottom + inset, top - inset, rows, columns);
            }

            switch (window.Operation)
            {
                case WindowOperation.Fixed:
                {
                    var light = Placed(u0, u1, 0);
                    Pane(light, 0, light.Width, z0, z1);
                    GlazingBars(light, 0, light.Width, z0, z1, rows, columns);
                    break;
                }

                case WindowOperation.Casement:
                {
                    // A pair is hung one sash on each jamb, so they swing apart and their
                    // catches meet in the middle, as a French casement's do.
                    var sashes = Width > 1000 ? 2 : 1;
                    var width = (u1 - u0) / sashes;
                    for (var i = 0; i < sashes; i++)
                    {
                        var atStart = sashes == 1 ? !_opening.FlipHand : i == 0 != _opening.FlipHand;
                        var sash = Placed(u0 + i * width, u0 + (i + 1) * width, SetIn(SashDepth), atStart);

                        Sash(sash, z0, z1);
                        Glazed(sash, z0, z1);
                        WindowHandle(sash, atStart ? sash.Width - 60 : 60, (z0 + z1) / 2, vertical: true);
                        SashHinges(sash, z0, z1, atStart);
                    }

                    break;
                }

                case WindowOperation.Awning:
                {
                    // A fixed light above a top-hung sash: the sash opens out from its head.
                    var split = z0 + (z1 - z0) * 0.55;
                    Box(_frame, u0, u1, -depth, depth, split - SashFace / 2, split + SashFace / 2);

                    var light = Placed(u0, u1, 0);
                    Pane(light, 0, light.Width, split + SashFace / 2, z1);
                    GlazingBars(light, 0, light.Width, split + SashFace / 2, z1, rows, columns);

                    var sash = Placed(u0, u1, 0);
                    Sash(sash, z0, split - SashFace / 2);
                    Glazed(sash, z0, split - SashFace / 2);
                    WindowHandle(sash, sash.Width / 2, z0 + 70, vertical: false);
                    break;
                }

                case WindowOperation.Sliding:
                {
                    // Two sashes on their own tracks, overlapping in the middle. Open, the one
                    // with the handle has run along its track across the fixed one, leaving
                    // half the window clear - which is the whole of what a slider does.
                    var middle = (u0 + u1) / 2;
                    var run = _opening.IsOpen ? middle - u0 : 0;

                    var fixedSash = Placed(u0, middle + SashFace / 2, 22);
                    Sash(fixedSash, z0, z1);
                    Glazed(fixedSash, z0, z1);

                    var runner = Placed(middle - SashFace / 2 - run, u1 - run, -22);
                    Sash(runner, z0, z1);
                    Glazed(runner, z0, z1);
                    WindowHandle(runner, SashFace * 1.5, (z0 + z1) / 2, vertical: true);
                    break;
                }

                case WindowOperation.TiltAndTurn:
                {
                    var sashes = Width > 1400 ? 2 : 1;
                    var width = (u1 - u0) / sashes;
                    for (var i = 0; i < sashes; i++)
                    {
                        var sash = Placed(u0 + i * width, u0 + (i + 1) * width, SetIn(SashDepth));

                        Sash(sash, z0, z1);
                        Glazed(sash, z0, z1);
                        WindowHandle(sash, _opening.FlipHand ? 60 : sash.Width - 60, (z0 + z1) / 2, vertical: true);
                        SashHinges(sash, z0, z1, !_opening.FlipHand);
                    }

                    break;
                }

                case WindowOperation.DoubleHung:
                {
                    // Two sashes one above the other on their own planes, the lower one inside,
                    // so they pass each other as they slide.
                    // Open, the lower one has been pushed up behind the upper, as a sash is.
                    var middle = (z0 + z1) / 2;
                    var lift = _opening.IsOpen ? middle - z0 : 0;

                    var upper = Placed(u0, u1, 20);
                    Sash(upper, middle - SashFace / 2, z1);
                    Glazed(upper, middle - SashFace / 2, z1);

                    var lower = Placed(u0, u1, -20);
                    Sash(lower, z0 + lift, middle + SashFace / 2 + lift);
                    Glazed(lower, z0 + lift, middle + SashFace / 2 + lift);
                    WindowHandle(lower, lower.Width / 2, middle + SashFace + lift, vertical: false);
                    break;
                }

                case WindowOperation.Hopper:
                {
                    // Hinged along the bottom and opening in, so the catch is at the head.
                    var sash = Placed(u0, u1, 0);
                    Sash(sash, z0, z1);
                    Glazed(sash, z0, z1);
                    WindowHandle(sash, sash.Width / 2, z1 - 70, vertical: false);
                    break;
                }

                case WindowOperation.Louvred:
                {
                    // Glass slats turning together in the frame, sloped to throw the rain out.
                    var count = Math.Max(3, (int)((z1 - z0) / 160));
                    var pitch = (z1 - z0) / count;
                    for (var i = 0; i < count; i++)
                    {
                        var bottom = z0 + i * pitch;
                        Box(_glass, u0, u1, -18, 18, bottom + pitch * 0.15, bottom + pitch * 0.85);
                    }

                    // The two stiles the slats are carried on.
                    Box(_frame, u0, u0 + 30, -depth / 2, depth / 2, z0, z1);
                    Box(_frame, u1 - 30, u1, -depth / 2, depth / 2, z0, z1);
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

        /// <summary>
        /// Where one sash or fixed light of a window is: the stretch of the opening it fills, how
        /// far it is set across the wall, and - when it has swung open - the line it has swung to.
        /// Everything on the sash is set out along it from its first edge, so a glazing bar or a
        /// catch keeps its place on the sash whether the window is shut, swung open, or run along
        /// its track.
        /// </summary>
        private readonly record struct SashPlace(double From, double Width, double Across, LeafFrame? Swung);

        /// <summary>Whether this window's sashes swing out of their opening when it stands open.</summary>
        private bool SashSwingsOpen =>
            _opening.IsOpen && _type is WindowType { Operation: WindowOperation.Casement or WindowOperation.TiltAndTurn };

        /// <summary>
        /// A sash filling this stretch of the opening, hinged on the edge given - which is the low
        /// edge unless it is hung the other way. A sash that swings is put on the line it has swung
        /// to, still measured from its low edge, so nothing set out on it has to know how it is hung.
        /// </summary>
        private SashPlace Placed(double u0, double u1, double across, bool? hingeAtStart = null)
        {
            if (!SashSwingsOpen) return new SashPlace(u0, u1 - u0, across, null);

            var atStart = hingeAtStart ?? !_opening.FlipHand;
            var hung = Hung(u0, u1, across, atStart, swing: 55);

            // Hung gives the leaf from its hinge outward; turned back to front when the hinge is
            // on the high edge, so that along the sash always means from the low edge up.
            var frame = atStart ? hung : LeafFrame.Between(hung.Plan(hung.Width, 0), hung.Origin);
            return new SashPlace(u0, u1 - u0, across, frame);
        }

        /// <summary>
        /// A piece of a sash: along it from its first edge, across its face, and heights. On the
        /// line it has swung to when it stands open, and following the wall when it has not, so a
        /// window in a curved wall still curves with it.
        /// </summary>
        private void OnSash(Mesh3D mesh, SashPlace p, double s0, double s1, double t0, double t1, double z0, double z1)
        {
            if (p.Swung is { } f)
                Block(mesh, f, Math.Min(s0, s1), Math.Max(s0, s1), Math.Min(t0, t1), Math.Max(t0, t1), z0, z1);
            else
                Box(mesh, p.From + Math.Min(s0, s1), p.From + Math.Max(s0, s1), p.Across + t0, p.Across + t1, z0, z1);
        }

        /// <summary>An opening sash: a frame of its own round a pane, set a little proud of the fixed frame.</summary>
        private void Sash(double u0, double u1, double z0, double z1, double across) =>
            Sash(Placed(u0, u1, across), z0, z1);

        private void Sash(SashPlace p, double z0, double z1)
        {
            var s = SashFace;
            var d = SashDepth / 2;
            var w = p.Width;

            if (w <= 2 * s || z1 - z0 <= 2 * s)
            {
                Pane(p, 0, w, z0, z1);
                return;
            }

            OnSash(_leaf, p, 0, s, -d, d, z0, z1);
            OnSash(_leaf, p, w - s, w, -d, d, z0, z1);
            OnSash(_leaf, p, s, w - s, -d, d, z0, z0 + s);
            OnSash(_leaf, p, s, w - s, -d, d, z1 - s, z1);
            Pane(p, s, w - s, z0 + s, z1 - s);
        }

        /// <summary>The glass in a stretch of a sash, on the sash rather than in the hole.</summary>
        private void Pane(SashPlace p, double s0, double s1, double z0, double z1)
        {
            var t = PaneThickness / 2;
            OnSash(_glass, p, s0, s1, -t, t, z0, z1);
        }

        /// <summary>
        /// The bars dividing one sash's glass into panes, which is most of what tells a Georgian
        /// sash from a picture window. A sash's rows and columns are its own - six over six means
        /// six panes in each sash - and they are set on the sash, so they go with it when it opens.
        /// </summary>
        private void GlazingBars(SashPlace p, double s0, double s1, double z0, double z1, int rows, int columns)
        {
            if (rows <= 1 && columns <= 1) return;

            const double bar = 28;

            for (var r = 1; r < rows; r++)
            {
                var z = z0 + (z1 - z0) * r / rows;
                OnSash(_frame, p, s0, s1, -16, 16, z - bar / 2, z + bar / 2);
            }

            for (var c = 1; c < columns; c++)
            {
                var s = s0 + (s1 - s0) * c / columns;
                OnSash(_frame, p, s - bar / 2, s + bar / 2, -16, 16, z0, z1);
            }
        }

        /// <summary>
        /// The hinges a sash is hung on, up the jamb it swings from and standing proud on the side
        /// it opens to. A sash is set out from its low edge whichever edge is hinged, so the side
        /// it opens to is simply the side the window faces.
        /// </summary>
        private void SashHinges(SashPlace p, double z0, double z1, bool hingeAtStart)
        {
            var height = z1 - z0;
            if (height <= 400 || p.Width <= 200) return;

            var face = SashDepth / 2;
            var (t0, t1) = (Math.Min(Facing * face, Facing * (face + 12)), Math.Max(Facing * face, Facing * (face + 12)));
            var s = hingeAtStart ? 0 : p.Width - 28;

            foreach (var at in new[] { z0 + height * 0.16, z0 + height * 0.84 })
                OnSash(_hinges, p, s, s + 28, t0, t1, at - Math.Min(45, height * 0.05), at + Math.Min(45, height * 0.05));
        }

        /// <summary>A window handle on the room side of the sash, this far along the sash.</summary>
        private void WindowHandle(SashPlace p, double at, double z, bool vertical)
        {
            var face = -SashDepth / 2;
            OnSash(_metal, p, at - 10, at + 10, face - 30, face, z - 10, z + 10);
            if (vertical) OnSash(_metal, p, at - 8, at + 8, face - 38, face - 24, z - 90, z + 10);
            else OnSash(_metal, p, at - 70, at + 10, face - 38, face - 24, z - 8, z + 8);
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
