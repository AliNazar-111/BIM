using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Views;

/// <summary>
/// What a component is shaped like, in 3D and in plan (specification sections 6.1 and 6.5).
///
/// A component's form is worked out here rather than stored, for the same reason a wall's
/// layers are: the type says what it is and how big, and every view builds the same thing from
/// that. So a table widened in its type is wider in the plan, in the 3D view and in the
/// schedule at once, and no drawing can disagree with another about what is in the room.
///
/// Everything is laid out in the component's own frame - across it, front to back, and up -
/// and then turned to face the way the instance faces, so one description of a chair serves a
/// chair at any angle.
/// </summary>
public static class ComponentModel
{
    /// <summary>A box in the component's own frame: across, front to back, and up.</summary>
    public readonly record struct Part(double From, double To, double Back, double Front, double Bottom, double Top);

    /// <summary>
    /// Where a component stands: the middle of its footprint, the way it faces, and how big it
    /// is. Distances across it are measured from its middle, front to back from its middle, and
    /// heights from the floor it stands on.
    /// </summary>
    public readonly record struct Frame(Point2D Centre, Vector2D Across, double Base)
    {
        /// <summary>Square to the width, toward the front: the way the component faces.</summary>
        public Vector2D Facing => new(Across.Y, -Across.X);

        public Point2D Plan(double across, double forward) => Centre + Across * across + Facing * forward;

        /// <summary>The outline of one part, as it sits in plan.</summary>
        public IReadOnlyList<Point2D> Outline(double from, double to, double back, double front) => new[]
        {
            Plan(from, back), Plan(to, back), Plan(to, front), Plan(from, front)
        };
    }

    /// <summary>
    /// The frame a component stands in: where it is in plan, which way round it is, and the
    /// height its own base sits at - which its host decides, so it is passed in rather than
    /// worked out here. A plan does not care about the height and passes nothing.
    /// </summary>
    public static Frame FrameOf(Component component, double baseElevation) =>
        new(component.Location, component.Across, baseElevation);

    /// <summary>
    /// The boxes a component of this type is built from. The whole form is one list, so the
    /// same description draws it in 3D, measures its footprint and answers what it covers.
    /// </summary>
    public static IReadOnlyList<Part> Parts(ComponentType type)
    {
        var w = type.Width;
        var d = type.Depth;
        var h = type.Height;
        var halfW = w / 2;
        var halfD = d / 2;

        var parts = new List<Part>();

        void Add(double from, double to, double back, double front, double bottom, double top) =>
            parts.Add(new Part(from, to, back, front, bottom, top));

        // Four legs under a top, inset from its edges so they read as legs.
        void Legs(double thickness, double top)
        {
            var inset = Math.Min(Math.Min(w, d) * 0.12, 90);
            foreach (var across in new[] { -halfW + inset, halfW - inset - thickness })
            foreach (var forward in new[] { -halfD + inset, halfD - inset - thickness })
                Add(across, across + thickness, forward, forward + thickness, 0, top);
        }

        switch (type.Form)
        {
            case ComponentForm.Table:
            case ComponentForm.Desk:
            {
                var slab = Math.Min(40, h * 0.12);
                Add(-halfW, halfW, -halfD, halfD, h - slab, h);
                Legs(60, h - slab);

                // A desk has a modesty panel across the back of the knee hole.
                if (type.Form == ComponentForm.Desk)
                    Add(-halfW + 60, halfW - 60, -halfD + 40, -halfD + 70, h * 0.25, h - slab);

                break;
            }

            case ComponentForm.Chair:
            {
                var seat = h * 0.45;
                var thickness = Math.Min(60, h * 0.08);
                Add(-halfW, halfW, -halfD, halfD, seat, seat + thickness);
                Add(-halfW, halfW, -halfD, -halfD + thickness, seat, h);
                Legs(40, seat);
                break;
            }

            case ComponentForm.Sofa:
            {
                var arm = Math.Min(w * 0.14, 220);
                var back = Math.Min(d * 0.28, 260);
                var seat = h * 0.42;

                Add(-halfW, halfW, -halfD, halfD, 0, seat);                              // the base
                Add(-halfW, halfW, -halfD, -halfD + back, seat, h);                      // the back
                Add(-halfW, -halfW + arm, -halfD + back, halfD, seat, h * 0.72);         // the arms
                Add(halfW - arm, halfW, -halfD + back, halfD, seat, h * 0.72);
                break;
            }

            case ComponentForm.Bed:
            {
                var head = Math.Min(d * 0.1, 120);
                var mattress = h * 0.45;

                Add(-halfW, halfW, -halfD, halfD, 0, mattress);                          // the divan
                Add(-halfW + 40, halfW - 40, -halfD + head, halfD - 40, mattress, h * 0.78);
                Add(-halfW, halfW, -halfD, -halfD + head, 0, h);                         // the headboard

                // Two pillows at the head, side by side, unless it is a single.
                var pillows = w > 1100 ? 2 : 1;
                var span = (w - 200) / pillows;
                for (var i = 0; i < pillows; i++)
                {
                    var from = -halfW + 100 + i * span;
                    Add(from + 30, from + span - 30, -halfD + head + 60, -halfD + head + 380, h * 0.78, h * 0.9);
                }

                break;
            }

            case ComponentForm.Counter:
            {
                var top = Math.Min(40, h * 0.1);
                var plinth = Math.Min(120, h * 0.18);

                Add(-halfW, halfW, -halfD, halfD - 20, plinth, h - top);                 // the carcass
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, plinth);                  // the plinth, set back
                Add(-halfW, halfW, -halfD, halfD, h - top, h);                           // the worktop, overhanging
                break;
            }

            case ComponentForm.Shelving:
            {
                var upright = Math.Min(40, w * 0.06);
                Add(-halfW, -halfW + upright, -halfD, halfD, 0, h);
                Add(halfW - upright, halfW, -halfD, halfD, 0, h);

                var shelves = Math.Max(2, (int)Math.Round(h / 400));
                for (var i = 0; i <= shelves; i++)
                {
                    var z = h * i / shelves;
                    Add(-halfW + upright, halfW - upright, -halfD, halfD, Math.Max(0, z - 25), Math.Max(25, z));
                }

                break;
            }

            case ComponentForm.Basin:
            {
                var bowl = h * 0.25;
                Add(-halfW, halfW, -halfD, halfD, h - bowl, h);                          // the bowl
                Add(-halfW * 0.35, halfW * 0.35, -halfD * 0.5, halfD * 0.5, 0, h - bowl); // the pedestal
                Add(-40, 40, -halfD + 20, -halfD + 120, h, h + 140);                     // the tap
                break;
            }

            case ComponentForm.Toilet:
            {
                var cistern = Math.Min(d * 0.3, 200);
                Add(-halfW * 0.8, halfW * 0.8, -halfD + cistern, halfD, h * 0.15, h * 0.62);   // the pan
                Add(-halfW * 0.5, halfW * 0.5, -halfD + cistern - 20, halfD - 40, 0, h * 0.15); // its foot
                Add(-halfW, halfW, -halfD, -halfD + cistern, 0, h);                            // the cistern
                break;
            }

            case ComponentForm.Bath:
            {
                var wall = Math.Min(80, Math.Min(w, d) * 0.12);
                Add(-halfW, halfW, -halfD, halfD, 0, h);                                 // the panelled box
                Add(-halfW + wall, halfW - wall, -halfD + wall, halfD - wall, h * 0.3, h + 1); // the tub, sunk in it
                break;
            }

            case ComponentForm.Tree:
            {
                var trunk = Math.Min(Math.Min(w, d) * 0.12, 160);
                Add(-trunk / 2, trunk / 2, -trunk / 2, trunk / 2, 0, h * 0.45);

                // The canopy, as three boxes stepping in - enough to read as a tree in a view.
                for (var i = 0; i < 3; i++)
                {
                    var spreadW = halfW * (1 - i * 0.28);
                    var spreadD = halfD * (1 - i * 0.28);
                    var low = h * (0.4 + i * 0.2);
                    Add(-spreadW, spreadW, -spreadD, spreadD, low, h * (0.62 + i * 0.19));
                }

                break;
            }

            case ComponentForm.WallLight:
            {
                Add(-halfW, halfW, -halfD, -halfD + Math.Min(40, d * 0.3), 0, h);        // the back plate
                Add(-halfW * 0.7, halfW * 0.7, -halfD + Math.Min(40, d * 0.3), halfD, h * 0.15, h * 0.85);
                break;
            }

            default:
                Add(-halfW, halfW, -halfD, halfD, 0, h);
                break;
        }

        return parts;
    }

    /// <summary>
    /// The lines a component is drawn with in plan: its footprint, and enough of what is inside
    /// it to tell one from another - the back of a chair, the bowl of a basin, the spread of a
    /// tree. Each is a closed outline in plan, ready to be stroked.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> PlanOutlines(ComponentType type, Frame frame)
    {
        var w = type.Width;
        var d = type.Depth;
        var halfW = w / 2;
        var halfD = d / 2;

        var shapes = new List<IReadOnlyList<Point2D>> { frame.Outline(-halfW, halfW, -halfD, halfD) };

        void Inside(double from, double to, double back, double front) =>
            shapes.Add(frame.Outline(from, to, back, front));

        switch (type.Form)
        {
            case ComponentForm.Chair:
                Inside(-halfW, halfW, -halfD, -halfD + Math.Min(60, d * 0.18));
                break;

            case ComponentForm.Sofa:
            {
                var arm = Math.Min(w * 0.14, 220);
                var back = Math.Min(d * 0.28, 260);
                Inside(-halfW, halfW, -halfD, -halfD + back);
                Inside(-halfW, -halfW + arm, -halfD + back, halfD);
                Inside(halfW - arm, halfW, -halfD + back, halfD);
                break;
            }

            case ComponentForm.Bed:
            {
                var head = Math.Min(d * 0.1, 120);
                Inside(-halfW, halfW, -halfD, -halfD + head);
                Inside(-halfW + 40, halfW - 40, -halfD + head, halfD - 40);
                break;
            }

            case ComponentForm.Desk:
            case ComponentForm.Counter:
                Inside(-halfW, halfW, -halfD, -halfD + Math.Min(80, d * 0.25));
                break;

            case ComponentForm.Shelving:
                Inside(-halfW, halfW, -halfD, -halfD + Math.Min(40, d * 0.3));
                break;

            case ComponentForm.Basin:
                Inside(-halfW * 0.7, halfW * 0.7, -halfD * 0.55, halfD * 0.7);
                break;

            case ComponentForm.Toilet:
            {
                var cistern = Math.Min(d * 0.3, 200);
                Inside(-halfW, halfW, -halfD, -halfD + cistern);
                Inside(-halfW * 0.8, halfW * 0.8, -halfD + cistern, halfD);
                break;
            }

            case ComponentForm.Bath:
            {
                var wall = Math.Min(80, Math.Min(w, d) * 0.12);
                Inside(-halfW + wall, halfW - wall, -halfD + wall, halfD - wall);
                break;
            }

            case ComponentForm.Tree:
                Inside(-halfW * 0.12, halfW * 0.12, -halfD * 0.12, halfD * 0.12);
                break;
        }

        return shapes;
    }

    /// <summary>The footprint on its own, for picking one out of a plan and for a room's area.</summary>
    public static IReadOnlyList<Point2D> Footprint(ComponentType type, Frame frame) =>
        frame.Outline(-type.Width / 2, type.Width / 2, -type.Depth / 2, type.Depth / 2);
}
