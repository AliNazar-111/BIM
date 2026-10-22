using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

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
    /// <summary>
    /// What a part is finished in: the type's own colour or material - a unit's carcass and
    /// worktop, a fridge's cabinet - or the white enamel, black and stainless steel an
    /// appliance's front, a hob and a sink are made of, whatever the type is made of; or the
    /// clear glass of a shower's enclosure, seen through.
    /// </summary>
    public enum PartFinish { Body, White, Dark, Metal, Glass, Bark, Skin }

    /// <summary>
    /// How a part fills its box: the box itself, a cylinder standing up in it - a burner, a tap -
    /// a disc facing forward in it, as a washing machine's door does; or, for a family of your
    /// own, a ball, a cone standing on its base, or a wedge sloping down to the front.
    /// </summary>
    public enum PartShape { Box, Upright, Facing, Sphere, Cone, Wedge }

    /// <summary>A box in the component's own frame: across, front to back, and up - what it is finished in, and how it fills the box.</summary>
    public readonly record struct Part(
        double From, double To, double Back, double Front, double Bottom, double Top,
        PartFinish Finish = PartFinish.Body, PartShape Shape = PartShape.Box);

    /// <summary>A rectangle in plan in the component's own frame: across it, and front to back.</summary>
    public readonly record struct Area(double From, double To, double Back, double Front)
    {
        public double Across => (From + To) / 2;

        public double Forward => (Back + Front) / 2;
    }

    /// <summary>The colour a finish reads as and what it is called, for one that is not the type's own.</summary>
    public static (ColourRgb Colour, string Name) Look(PartFinish finish) => finish switch
    {
        PartFinish.White => (new ColourRgb(0xF3, 0xF4, 0xF5), "Enamel, White"),
        PartFinish.Dark => (new ColourRgb(0x2B, 0x2D, 0x31), "Black"),
        PartFinish.Metal => (new ColourRgb(0xBF, 0xC4, 0xC9), "Stainless Steel"),
        PartFinish.Glass => (new ColourRgb(0xB4, 0xD8, 0xE4), "Glass"),
        PartFinish.Bark => (new ColourRgb(0x6B, 0x4E, 0x37), "Bark"),
        PartFinish.Skin => (new ColourRgb(0xD9, 0xAD, 0x8A), "Skin"),
        _ => (new ColourRgb(0xB4, 0x9A, 0x74), "Body")
    };

    /// <summary>
    /// Where a component stands: the middle of its footprint, the way it faces, and how big it
    /// is. Distances across it are measured from its middle, front to back from its middle, and
    /// heights from the floor it stands on.
    /// </summary>
    public readonly record struct Frame(Point2D Centre, Vector2D Across, double Base)
    {
        /// <summary>Square to the width, toward the front: the way the component faces.</summary>
        public Vector2D Facing => new(Across.Y, -Across.X);

        /// <summary>Whether it stands as the mirror image of its family, left for right.</summary>
        public bool Mirrored { get; init; }

        /// <summary>The same frame the right way round: for what is the same either way, and for what is mirrored before it is placed.</summary>
        public Frame Unmirrored => this with { Mirrored = false };

        public Point2D Plan(double across, double forward) => Centre + Across * (Mirrored ? -across : across) + Facing * forward;

        /// <summary>The outline of one part, as it sits in plan.</summary>
        public IReadOnlyList<Point2D> Outline(double from, double to, double back, double front) => new[]
        {
            Plan(from, back), Plan(to, back), Plan(to, front), Plan(from, front)
        };

        /// <summary>The ellipse filling a part's box in plan - a burner, a tap, a waste - as a ring of short sides.</summary>
        public IReadOnlyList<Point2D> Ellipse(double from, double to, double back, double front, int sides = 24)
        {
            var (across, forward) = ((from + to) / 2, (back + front) / 2);
            var (radiusAcross, radiusForward) = ((to - from) / 2, (front - back) / 2);
            var points = new Point2D[sides];
            for (var i = 0; i < sides; i++)
            {
                var angle = 2 * Math.PI * i / sides;
                points[i] = Plan(across + radiusAcross * Math.Cos(angle), forward + radiusForward * Math.Sin(angle));
            }

            return points;
        }

        /// <summary>A part's box in plan with its corners rounded: a sink's rim, its bowl.</summary>
        public IReadOnlyList<Point2D> Rounded(double from, double to, double back, double front, double radius)
        {
            radius = Math.Min(radius, Math.Min(to - from, front - back) / 2);
            if (radius < 1) return Outline(from, to, back, front);

            var points = new List<Point2D>();
            foreach (var (across, forward, start) in new[]
                     {
                         (to - radius, front - radius, 0.0), (from + radius, front - radius, 90.0),
                         (from + radius, back + radius, 180.0), (to - radius, back + radius, 270.0)
                     })
                for (var step = 0; step <= 4; step++)
                {
                    var angle = (start + step * 22.5) * Math.PI / 180;
                    points.Add(Plan(across + radius * Math.Cos(angle), forward + radius * Math.Sin(angle)));
                }

            return points;
        }

        /// <summary>A line in plan, from one point in the frame to another: a rib of a drainer.</summary>
        public IReadOnlyList<Point2D> Line(double fromAcross, double fromForward, double toAcross, double toForward) =>
            new[] { Plan(fromAcross, fromForward), Plan(toAcross, toForward) };
    }

    /// <summary>
    /// The frame a component stands in: where it is in plan, which way round it is, and the
    /// height its own base sits at - which its host decides, so it is passed in rather than
    /// worked out here. A plan does not care about the height and passes nothing.
    /// </summary>
    public static Frame FrameOf(Component component, double baseElevation) =>
        new(component.Location, component.Across, baseElevation) { Mirrored = component.FlipHand };

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

        void Add(double from, double to, double back, double front, double bottom, double top,
            PartFinish finish = PartFinish.Body, PartShape shape = PartShape.Box)
        {
            if (to - from < 0.5 || front - back < 0.5 || top - bottom < 0.5) return;
            parts.Add(new Part(from, to, back, front, bottom, top, finish, shape));
        }

        // A box with the bowls of a sink through it: the strips behind and in front of them, and
        // either side of each - how a worktop, a sink's rim and the unit under it are cut for them.
        void AroundBowls(Area box, IReadOnlyList<Area> bowls, double bottom, double top, PartFinish finish)
        {
            var (back, front) = (bowls.Min(bowl => bowl.Back), bowls.Max(bowl => bowl.Front));
            Add(box.From, box.To, box.Back, back, bottom, top, finish);
            Add(box.From, box.To, front, box.Front, bottom, top, finish);

            var at = box.From;
            foreach (var bowl in bowls.OrderBy(bowl => bowl.From))
            {
                Add(at, bowl.From, back, front, bottom, top, finish);
                at = bowl.To;
            }

            Add(at, box.To, back, front, bottom, top, finish);
        }

        // A handle standing proud of a front: a bar on two short posts.
        void Handle(double from, double to, double face, double bottom, double top, bool upright)
        {
            var standOff = Math.Max(0, halfD - face - 14);
            Add(from, to, face + standOff, halfD, bottom, top, PartFinish.Metal);
            if (upright)
            {
                Add(from, to, face, face + standOff, bottom + 20, bottom + 40, PartFinish.Metal);
                Add(from, to, face, face + standOff, top - 40, top - 20, PartFinish.Metal);
            }
            else
            {
                Add(from + 20, from + 40, face, face + standOff, bottom, top, PartFinish.Metal);
                Add(to - 40, to - 20, face, face + standOff, bottom, top, PartFinish.Metal);
            }
        }

        // A unit's doors on the front of its carcass, one to every 600 or less of its width, each
        // with a bar handle across the top of it.
        void Doors(double front, double bottom, double top)
        {
            var count = Math.Max(1, (int)Math.Ceiling(w / 600 - 0.01));
            var each = w / count;
            for (var i = 0; i < count; i++)
            {
                var from = -halfW + each * i;
                Add(from + 2, from + each - 2, front, front + 18, bottom + 3, top - 3);

                var middle = from + each / 2;
                var bar = Math.Min(64, each * 0.2);
                Handle(middle - bar, middle + bar, front + 18, top - 60, top - 48, upright: false);
            }
        }

        // What a kitchen run has in common: a worktop 40 thick on top.
        var worktop = Math.Min(40, h * 0.1);

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
                // Its seat at 450 or so, as a dining chair's is, a little higher on a tall one.
                var seat = Math.Min(h * 0.5, 460);
                var thickness = Math.Min(60, h * 0.08);
                Add(-halfW, halfW, -halfD, halfD, seat, seat + thickness);
                Add(-halfW, halfW, -halfD, -halfD + thickness, seat, h);
                Legs(40, seat);
                break;
            }

            case ComponentForm.Sofa:
            {
                var (arm, back, seat, armTop) = SofaOf(type, d);

                Add(-halfW, halfW, -halfD, halfD, 0, seat);                              // the base
                Add(-halfW, halfW, -halfD, -halfD + back, seat, h);                      // the back
                Add(-halfW, -halfW + arm, -halfD + back, halfD, seat, armTop);           // the arms
                Add(halfW - arm, halfW, -halfD + back, halfD, seat, armTop);
                break;
            }

            case ComponentForm.CornerSofa:
            {
                // An L: seats along the back turning the corner down the right-hand side to the
                // front, backed along both; an arm at the left end and at the front of the return.
                var (run, turn) = CornerOf(type);
                var (arm, back, seat, armTop) = SofaOf(type, run);

                Add(-halfW, halfW, -halfD, -halfD + run, 0, seat);                                       // the base along the back
                Add(halfW - turn, halfW, -halfD + run, halfD, 0, seat);                                  // and down the return
                Add(-halfW, halfW, -halfD, -halfD + back, seat, h);                                      // the back
                Add(halfW - back, halfW, -halfD + back, halfD, seat, h);                                 // turning the corner
                Add(-halfW, -halfW + arm, -halfD + back, -halfD + run, seat, armTop);                    // the arm at the end
                Add(halfW - turn, halfW - back, halfD - arm, halfD, seat, armTop);                       // and at the front of the return
                break;
            }

            case ComponentForm.RoundTable:
            {
                // A round top on a pedestal, its foot spread under it.
                var slab = Math.Min(40, h * 0.12);
                var column = Math.Min(Math.Min(w, d) * 0.12, 120);
                var foot = Math.Min(Math.Min(w, d) * 0.5, 600);
                Add(-halfW, halfW, -halfD, halfD, h - slab, h, shape: PartShape.Upright);
                Add(-column / 2, column / 2, -column / 2, column / 2, 40, h - slab, shape: PartShape.Upright);
                Add(-foot / 2, foot / 2, -foot / 2, foot / 2, 0, 40, shape: PartShape.Upright);
                break;
            }

            case ComponentForm.Drawers:
            {
                // A chest on a plinth set back in shadow, its top over it, and a drawer to every
                // 200 or so of its height - two side by side where it is a metre or more wide -
                // each with its handle across the middle of it.
                var plinth = Math.Min(60, h * 0.08);
                var top = Math.Min(25, h * 0.05);
                var face = halfD - 22;

                Add(-halfW + 20, halfW - 20, -halfD + 20, face - 38, 0, plinth, PartFinish.Dark);
                Add(-halfW, halfW, -halfD, face - 18, plinth, h - top);
                Add(-halfW, halfW, -halfD, face, h - top, h);

                var (rows, columns) = DrawersOf(type);
                var (tall, wide) = ((h - plinth - top) / rows, w / columns);
                for (var row = 0; row < rows; row++)
                for (var column = 0; column < columns; column++)
                {
                    var (from, bottom) = (-halfW + wide * column, plinth + tall * row);
                    Add(from + 2, from + wide - 2, face - 18, face, bottom + 2, bottom + tall - 2);

                    var middle = from + wide / 2;
                    var bar = Math.Min(64, wide * 0.15);
                    Handle(middle - bar, middle + bar, face, bottom + tall / 2 - 6, bottom + tall / 2 + 6, upright: false);
                }

                break;
            }

            case ComponentForm.Bed:
            {
                // The divan, the mattress on it - its top at 550 or so, as a bed is got into - the
                // headboard up the back, and the pillows at the head: two, unless it is a single.
                var (head, top, mattress) = BedOf(type);

                Add(-halfW, halfW, -halfD + head, halfD, 0, top - mattress);                     // the divan
                Add(-halfW + 15, halfW - 15, -halfD + head, halfD - 15, top - mattress, top);    // the mattress
                Add(-halfW, halfW, -halfD, -halfD + head, 0, h);                                 // the headboard

                foreach (var (from, to) in Pillows(type))
                    Add(from, to, -halfD + head + 50, -halfD + head + 400, top, top + Math.Min(120, (h - top) * 0.4));
                break;
            }

            case ComponentForm.BunkBed:
            {
                // Two beds, one over the other, on four posts: the lower near the floor, the upper
                // guarded all round - to the posts' tops, well over the 260 above its base EN 747
                // asks for - but for the way in at the foot of its right-hand side, where the ladder is.
                var (lower, upper, opening) = BunkOf(type);
                const double post = 60, rail = 25, thick = 150;

                foreach (var across in new[] { -halfW, halfW - post })
                foreach (var forward in new[] { -halfD, halfD - post })
                    Add(across, across + post, forward, forward + post, 0, h);

                foreach (var bottom in new[] { lower, upper })
                {
                    Add(-halfW + post, halfW - post, -halfD + post, halfD - post, bottom - 40, bottom);                        // the base
                    Add(-halfW + post + 10, halfW - post - 10, -halfD + post + 10, halfD - post - 10, bottom, bottom + thick);  // the mattress
                    Add(-halfW, -halfW + rail, -halfD + post, halfD - post, bottom - 120, bottom - 40);                        // the rails it lies on
                    Add(halfW - rail, halfW, -halfD + post, halfD - post, bottom - 120, bottom - 40);
                }

                // The upper bed guarded: its head and foot, its far side all along, its near side
                // as far as the way in at its foot.
                Add(-halfW + post, halfW - post, -halfD, -halfD + rail, upper + 100, h);
                Add(-halfW + post, halfW - post, halfD - rail, halfD, upper + 100, h);
                Add(-halfW, -halfW + rail, -halfD + post, halfD - post, upper + 100, h);
                Add(halfW - rail, halfW, -halfD + post, halfD - post - opening, upper + 100, h);

                // The ladder in the way in: its two stiles and a rung every 250.
                var (from, to) = (halfD - post - opening + 20, halfD - post - 20);
                Add(halfW - rail, halfW, from, from + 30, 0, h);
                Add(halfW - rail, halfW, to - 30, to, 0, h);
                for (var rung = 250.0; rung < upper; rung += 250)
                    Add(halfW - rail + 3, halfW - 3, from + 30, to - 30, rung - 15, rung + 15, PartFinish.Metal);
                break;
            }

            case ComponentForm.Cot:
            {
                // Four posts, top and bottom rails all round, slats between them 45 to 65 apart
                // as BS EN 716 sets them, and the mattress low in it, its top 500 or more under the rails.
                const double post = 40, rail = 40, slat = 20, gap = 50;
                var mattress = Math.Max(150, h - rail - 600);

                foreach (var across in new[] { -halfW, halfW - post })
                foreach (var forward in new[] { -halfD, halfD - post })
                    Add(across, across + post, forward, forward + post, 0, h);

                Add(-halfW + post, halfW - post, -halfD + post, halfD - post, mattress - 100, mattress - 80);                // its base
                Add(-halfW + post + 5, halfW - post - 5, -halfD + post + 5, halfD - post - 5, mattress - 80, mattress);    // the mattress

                // The rails and slats along each side and across each end.
                foreach (var (fromA, toA, fromF, toF, alongWidth) in new[]
                         {
                             (-halfW + post, halfW - post, -halfD, -halfD + post, true), (-halfW + post, halfW - post, halfD - post, halfD, true),
                             (-halfW, -halfW + post, -halfD + post, halfD - post, false), (halfW - post, halfW, -halfD + post, halfD - post, false)
                         })
                {
                    Add(fromA, toA, fromF, toF, mattress - 100, mattress - 100 + rail);
                    Add(fromA, toA, fromF, toF, h - rail, h);

                    var length = alongWidth ? toA - fromA : toF - fromF;
                    var count = Math.Max(1, (int)Math.Floor((length - gap) / (slat + gap)));
                    var spare = (length - count * slat) / (count + 1);
                    for (var i = 0; i < count; i++)
                    {
                        var at = spare + i * (slat + spare);
                        if (alongWidth) Add(fromA + at, fromA + at + slat, fromF + 10, toF - 10, mattress - 100 + rail, h - rail);
                        else Add(fromA + 10, toA - 10, fromF + at, fromF + at + slat, mattress - 100 + rail, h - rail);
                    }
                }

                break;
            }

            case ComponentForm.Counter:
            {
                var top = Math.Min(40, h * 0.1);
                var plinth = Math.Min(120, h * 0.18);

                Add(-halfW, halfW, -halfD, halfD - 40, plinth, h - top);                 // the carcass
                Doors(halfD - 40, plinth, h - top);                                      // its doors
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, plinth);                  // the plinth, set back
                Add(-halfW, halfW, -halfD, halfD, h - top, h);                           // the worktop, overhanging
                break;
            }

            case ComponentForm.Fridge:
            {
                var (door, face) = FridgeFront(type);
                var foot = Math.Min(80, h * 0.05);
                const double gap = 6;

                if (UnderCounter(type))
                {
                    // Under the worktop, as an under-counter one stands: white, one door the height of it.
                    var machine = h - worktop - 10;
                    Add(-halfW, halfW, -halfD, halfD, h - worktop, h);
                    Add(-halfW + 2, halfW - 2, -halfD, face - door, 0, machine, PartFinish.White);
                    Add(-halfW + 12, halfW - 12, face - door, face - 15, 0, foot, PartFinish.Dark);
                    Add(-halfW + 2, halfW - 2, face - door, face, foot, machine, PartFinish.White);
                    Handle(-halfW + 33, -halfW + 57, face, machine - 300, machine - 40, upright: true);
                    break;
                }

                Add(-halfW, halfW, -halfD, face - door, 0, h);                                  // the cabinet
                Add(-halfW + 10, halfW - 10, face - door, face - 15, 0, foot, PartFinish.Dark);   // the grille under the doors

                // A door, and its handle up the side it opens from.
                void Door(double from, double to, double bottom, double top, double handle, double handleFrom, double handleTo)
                {
                    Add(from, to, face - door, face, bottom, top);
                    Handle(handle - 12, handle + 12, face, Math.Max(bottom + 20, handleFrom), Math.Min(top - 20, handleTo), upright: true);
                }

                if (w > 800)
                {
                    // Side by side, as an American one is: the freezer on the left, the narrower,
                    // with its water and ice dispenser at chest height.
                    var split = -halfW + w * 0.42;
                    Door(-halfW, split - gap / 2, foot, h, split - gap / 2 - 40, h * 0.35, h * 0.8);
                    Door(split + gap / 2, halfW, foot, h, split + gap / 2 + 40, h * 0.35, h * 0.8);

                    var middle = (-halfW + split) / 2;
                    var reach = Math.Min(110, (split + halfW) * 0.35);
                    Add(middle - reach, middle + reach, face, face + 3, h * 0.5, h * 0.68, PartFinish.Dark);
                }
                else if (h > 1200)
                {
                    // The fridge above, the freezer under it: about 60 : 40.
                    var split = foot + (h - foot) * 0.4;
                    Door(-halfW, halfW, foot, split - gap / 2, -halfW + 45, split - 300, split - 40);
                    Door(-halfW, halfW, split + gap / 2, h, -halfW + 45, split + 40, split + 500);
                }
                else
                {
                    Door(-halfW, halfW, foot, h, -halfW + 45, h - 300, h - 40);
                }

                break;
            }

            case ComponentForm.Cooker:
            {
                var under = h - worktop;
                var low = Math.Min(100, h * 0.12);
                var face = halfD - 25;
                var panel = Math.Min(110, (under - low) * 0.16);
                var (hob, burners) = Hob(type);

                Add(-halfW, halfW, -halfD, halfD, under, h);                                  // the top - the worktop the hob is set into, or a range's own
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, low);                          // the plinth, set back
                Add(-halfW, halfW, -halfD, face, low, under, PartFinish.Metal);               // the oven, its front stainless
                Add(hob.From, hob.To, hob.Back, hob.Front, h, h + 5, PartFinish.Dark);         // the hob's glass, on the top

                foreach (var (across, forward, radius) in burners)
                {
                    Add(across - radius, across + radius, forward - radius, forward + radius, h + 5, h + 20, PartFinish.Metal, PartShape.Upright);
                    var cap = radius * 0.45;
                    Add(across - cap, across + cap, forward - cap, forward + cap, h + 20, h + 26, PartFinish.Dark, PartShape.Upright);
                }

                // A knob for each burner along the panel at the top of the front.
                var knob = Math.Min(18, panel * 0.2);
                for (var i = 0; i < burners.Count; i++)
                {
                    var across = -halfW + w * (i + 1) / (burners.Count + 1);
                    Add(across - knob, across + knob, face, face + 20, under - panel / 2 - knob, under - panel / 2 + knob, PartFinish.Dark, PartShape.Facing);
                }

                // The oven's door under the panel, with its window and handle - two side by side on a range.
                var doorTop = under - panel - 5;
                var ovens = w >= 900
                    ? new[] { (-halfW, -halfW + w * 0.6), (-halfW + w * 0.6, halfW) }
                    : new[] { (-halfW, halfW) };
                foreach (var (from, to) in ovens)
                {
                    Add(from + 4, to - 4, face, face + 6, low + 4, doorTop, PartFinish.Metal);
                    Add(from + 70, to - 70, face + 6, face + 8, low + 90, doorTop - 110, PartFinish.Dark);
                    Handle(from + 60, to - 60, face + 6, doorTop - 60, doorTop - 40, upright: false);
                }

                break;
            }

            case ComponentForm.Dishwasher:
            {
                var machine = h - worktop - 10;
                var kick = Math.Min(100, machine * 0.12);
                var face = halfD - 25;

                Add(-halfW, halfW, -halfD, halfD, h - worktop, h);                             // the worktop over it
                Add(-halfW + 2, halfW - 2, -halfD, face - 50, 0, kick, PartFinish.White);       // the kick plate, set back
                Add(-halfW + 2, halfW - 2, -halfD, face, kick, machine, PartFinish.White);      // the tub and its door
                Add(-halfW + 2, halfW - 2, face, face + 2, machine - 60, machine, PartFinish.Dark); // the controls along the top of the door
                Handle(-w * 0.3, w * 0.3, face, machine - 110, machine - 90, upright: false);
                break;
            }

            case ComponentForm.WallUnit:
            {
                // A cupboard hung on the wall: its carcass, a door to every 600 or less of its
                // width, and a bar handle along the bottom of each, where a hand reaches up to it.
                var count = Math.Max(1, (int)Math.Ceiling(w / 600 - 0.01));
                var each = w / count;
                Add(-halfW, halfW, -halfD, halfD - 40, 0, h);
                for (var i = 0; i < count; i++)
                {
                    var from = -halfW + each * i;
                    Add(from + 2, from + each - 2, halfD - 40, halfD - 22, 3, h - 3);
                    var middle = from + each / 2;
                    var bar = Math.Min(64, each * 0.2);
                    Handle(middle - bar, middle + bar, halfD - 22, 48, 60, upright: false);
                }

                break;
            }

            case ComponentForm.TallUnit:
            {
                // A larder: on its plinth, a tall door below and a short one over it, its handles
                // up the edge they open from, at the height a hand is.
                var plinth = Math.Min(120, h * 0.06);
                var split = plinth + (h - plinth) * 0.7;
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, plinth);
                Add(-halfW, halfW, -halfD, halfD - 40, plinth, h);
                foreach (var (bottom, top, handleFrom, handleTo) in new[] { (plinth, split, 900.0, 1300.0), (split, h, split + 40, split + 300) })
                {
                    Add(-halfW + 2, halfW - 2, halfD - 40, halfD - 22, bottom + 3, top - 3);
                    Handle(halfW - 50, halfW - 34, halfD - 22, Math.Max(bottom + 30, Math.Min(handleFrom, top - 60)), Math.Min(top - 30, handleTo), upright: true);
                }

                break;
            }

            case ComponentForm.OvenHousing:
            {
                // A tall housing: a drawer under, the oven at eye level - its stainless front, its
                // door's window and handle - the microwave over it, and a door to the top.
                var plinth = Math.Min(120, h * 0.06);
                var (ovenBottom, ovenTop, microwaveTop) = OvenHousingOf(type);
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, plinth);
                Add(-halfW, halfW, -halfD, halfD - 40, plinth, h);

                Add(-halfW + 2, halfW - 2, halfD - 40, halfD - 22, plinth + 3, ovenBottom - 3);                         // the drawer under it
                Handle(-w * 0.2, w * 0.2, halfD - 22, ovenBottom - 70, ovenBottom - 58, upright: false);
                Add(-halfW + 2, halfW - 2, halfD - 40, halfD - 20, ovenBottom, ovenTop, PartFinish.Metal);              // the oven
                Add(-halfW + 70, halfW - 70, halfD - 20, halfD - 18, ovenBottom + 80, ovenTop - 110, PartFinish.Dark);
                Handle(-halfW + 60, halfW - 60, halfD - 22, ovenTop - 60, ovenTop - 40, upright: false);
                Add(-halfW + 2, halfW - 2, halfD - 40, halfD - 20, ovenTop + 5, microwaveTop, PartFinish.Metal);        // the microwave
                Add(-halfW + 30, halfW - 160, halfD - 20, halfD - 18, ovenTop + 40, microwaveTop - 40, PartFinish.Dark);
                Add(-halfW + 2, halfW - 2, halfD - 40, halfD - 22, microwaveTop + 5, h - 3);                            // the door over them
                Handle(-w * 0.2, w * 0.2, halfD - 22, microwaveTop + 40, microwaveTop + 52, upright: false);
                break;
            }

            case ComponentForm.CookerHood:
            {
                // A chimney hood: its canopy over the hob - stainless, as its type is - its filters
                // under it, and the chimney up from its back to the ceiling, the duct in it.
                var canopy = Math.Min(90, h * 0.15);
                var chimney = ChimneyOf(type);
                Add(-halfW, halfW, -halfD, halfD, 0, canopy);
                Add(-halfW + 30, halfW - 30, -halfD + 30, halfD - 30, 0, 2, PartFinish.Dark);
                Add(-chimney.Width / 2, chimney.Width / 2, -halfD, -halfD + chimney.Depth, canopy, h);
                break;
            }

            case ComponentForm.TumbleDryer:
            {
                // Under the worktop as a washing machine is: its cabinet, the control panel along
                // the top with its dial, the water tank's drawer and the round door.
                var machine = h - worktop - 10;
                var face = halfD - 40;
                var panel = Math.Min(130, machine * 0.16);

                Add(-halfW, halfW, -halfD, halfD, h - worktop, h);
                Add(-halfW + 2, halfW - 2, -halfD, face, 0, machine, PartFinish.White);
                Add(-halfW + 2, halfW - 2, face, face + 4, machine - panel, machine, PartFinish.White);
                Add(-halfW + 15, -halfW + 15 + w * 0.25, face + 4, face + 10, machine - panel + 20, machine - 20, PartFinish.White);   // the tank's drawer
                var dial = Math.Min(24, panel * 0.2);
                Add(halfW - w * 0.14 - dial, halfW - w * 0.14 + dial, face + 4, face + 20, machine - panel / 2 - dial, machine - panel / 2 + dial, PartFinish.Metal, PartShape.Facing);

                var radius = Math.Min(w * 0.4, (machine - panel) * 0.42);
                var middle = (machine - panel) * 0.5;
                Add(-radius, radius, face, face + 20, middle - radius, middle + radius, PartFinish.White, PartShape.Facing);
                Add(-radius * 0.75, radius * 0.75, face + 20, halfD, middle - radius * 0.75, middle + radius * 0.75, PartFinish.Dark, PartShape.Facing);
                break;
            }

            case ComponentForm.WashingMachine:
            {
                var machine = h - worktop - 10;
                var face = halfD - 40;
                var panel = Math.Min(130, machine * 0.16);

                Add(-halfW, halfW, -halfD, halfD, h - worktop, h);                                          // the worktop over it
                Add(-halfW + 2, halfW - 2, -halfD, face, 0, machine, PartFinish.White);                      // the cabinet
                Add(-halfW + 2, halfW - 2, face, face + 4, machine - panel, machine, PartFinish.White);        // the control panel
                Add(-halfW + 15, -halfW + 15 + w * 0.3, face + 4, face + 10, machine - panel + 20, machine - 20, PartFinish.White); // the soap drawer
                Add(-halfW + w * 0.42, halfW - w * 0.26, face + 4, face + 6, machine - panel + 35, machine - 35, PartFinish.Dark);  // the display

                var dial = Math.Min(24, panel * 0.2);
                var dialAt = halfW - w * 0.14;
                Add(dialAt - dial, dialAt + dial, face + 4, face + 20, machine - panel / 2 - dial, machine - panel / 2 + dial, PartFinish.Metal, PartShape.Facing);

                // The door: a stainless ring with the glass domed out of it.
                var radius = Math.Min(w * 0.38, (machine - panel) * 0.4);
                var middle = (machine - panel) * 0.52;
                Add(-radius, radius, face, face + 25, middle - radius, middle + radius, PartFinish.Metal, PartShape.Facing);
                var glass = radius * 0.7;
                Add(-glass, glass, face + 25, halfD, middle - glass, middle + glass, PartFinish.Dark, PartShape.Facing);
                break;
            }

            case ComponentForm.Sink:
            {
                var (rim, bowls, drainer, tap) = Sink(type);
                var under = h - worktop;
                var plinth = Math.Min(120, h * 0.18);
                var floor = h - Math.Min(200, (under - plinth) * 0.3);

                // The unit under it, cut round the bowls where they sink into it; the worktop cut
                // for them; and the sink's rim lying on the worktop round them.
                Add(-halfW, halfW, -halfD + 60, halfD - 60, 0, plinth);
                Add(-halfW, halfW, -halfD, halfD - 40, plinth, floor - 3);
                AroundBowls(new Area(-halfW, halfW, -halfD, halfD - 40), bowls, floor - 3, under, PartFinish.Body);
                Doors(halfD - 40, plinth, under);
                AroundBowls(new Area(-halfW, halfW, -halfD, halfD), bowls, under, h, PartFinish.Body);
                AroundBowls(rim, bowls, h, h + 3, PartFinish.Metal);

                // Each bowl: its floor, its four sides and its waste.
                const double steel = 3;
                foreach (var bowl in bowls)
                {
                    Add(bowl.From, bowl.To, bowl.Back, bowl.Front, floor - steel, floor, PartFinish.Metal);
                    Add(bowl.From, bowl.From + steel, bowl.Back, bowl.Front, floor, h + 3, PartFinish.Metal);
                    Add(bowl.To - steel, bowl.To, bowl.Back, bowl.Front, floor, h + 3, PartFinish.Metal);
                    Add(bowl.From + steel, bowl.To - steel, bowl.Back, bowl.Back + steel, floor, h + 3, PartFinish.Metal);
                    Add(bowl.From + steel, bowl.To - steel, bowl.Front - steel, bowl.Front, floor, h + 3, PartFinish.Metal);
                    Add(bowl.Across - 30, bowl.Across + 30, bowl.Forward - 30, bowl.Forward + 30, floor, floor + 2, PartFinish.Dark, PartShape.Upright);
                }

                // The drainer's ribs, running to the bowl.
                if (drainer is { } board)
                    foreach (var across in DrainerRibs(board))
                        Add(across - 4, across + 4, board.Back + 30, board.Front - 30, h + 3, h + 6, PartFinish.Metal);

                // The tap on the deck behind the bowl, its spout reaching over it.
                var (tapAcross, tapForward) = tap;
                Add(tapAcross - 22, tapAcross + 22, tapForward - 22, tapForward + 22, h + 3, h + 300, PartFinish.Metal, PartShape.Upright);
                Add(tapAcross - 14, tapAcross + 14, tapForward, tapForward + 220, h + 270, h + 292, PartFinish.Metal);
                break;
            }

            case ComponentForm.Wardrobe:
            {
                var plinth = Math.Min(80, h * 0.05);
                var top = Math.Min(30, h * 0.02);
                var face = halfD - 18;

                Add(-halfW, halfW, -halfD, face - 18, plinth, h - top);                  // the carcass
                Add(-halfW, halfW, -halfD + 40, face - 58, 0, plinth);                   // the plinth, set back
                Add(-halfW, halfW, -halfD, face, h - top, h);                            // the top, over the doors

                // Hinged doors, one to every 500 or so, in pairs with their handles up the edges they meet at.
                var count = Math.Max(1, (int)Math.Round(w / 500));
                var each = w / count;
                for (var i = 0; i < count; i++)
                {
                    var from = -halfW + each * i;
                    Add(from + 2, from + each - 2, face - 18, face, plinth + 3, h - top - 3);

                    var right = count == 1 || (i % 2 == 0 && i < count - 1);
                    var handle = right ? from + each - 40 : from + 40;
                    Handle(handle - 8, handle + 8, face, h * 0.4, h * 0.6, upright: true);
                }

                break;
            }

            case ComponentForm.Armchair:
            {
                var (arm, back, seat) = ArmchairOf(type);
                var leg = Math.Min(110, h * 0.12);
                var cushion = Math.Min(130, (seat - leg) * 0.5);
                var frame = seat - cushion;

                // Short dark legs at the corners, the frame on them, its arms and back, and the cushions.
                foreach (var across in new[] { -halfW + 30, halfW - 70 })
                foreach (var forward in new[] { -halfD + 30, halfD - 70 })
                    Add(across, across + 40, forward, forward + 40, 0, leg, PartFinish.Dark);

                Add(-halfW, halfW, -halfD, halfD, leg, frame);
                Add(-halfW, -halfW + arm, -halfD, halfD, frame, h * 0.68);
                Add(halfW - arm, halfW, -halfD, halfD, frame, h * 0.68);
                Add(-halfW + arm, halfW - arm, -halfD, -halfD + back, frame, h);
                Add(-halfW + arm + 5, halfW - arm - 5, -halfD + back + 5, halfD - 5, frame, seat);
                Add(-halfW + arm + 5, halfW - arm - 5, -halfD + back, -halfD + back + Math.Min(120, d * 0.14), seat, h - 50);
                break;
            }

            case ComponentForm.CoffeeTable:
            {
                var top = Math.Min(30, h * 0.08);
                var leg = Math.Min(50, Math.Min(w, d) * 0.1);
                var inset = Math.Min(30, Math.Min(w, d) * 0.06);
                var shelf = h * 0.25;

                Add(-halfW, halfW, -halfD, halfD, h - top, h);                           // the top
                foreach (var across in new[] { -halfW + inset, halfW - inset - leg })
                foreach (var forward in new[] { -halfD + inset, halfD - inset - leg })
                    Add(across, across + leg, forward, forward + leg, 0, h - top);       // the legs
                Add(-halfW + inset, halfW - inset, -halfD + inset, halfD - inset, shelf, shelf + Math.Min(18, h * 0.05)); // the shelf under it
                break;
            }

            case ComponentForm.TelevisionUnit:
            {
                var plinth = Math.Min(60, h * 0.12);
                var face = halfD - 18;

                Add(-halfW, halfW, -halfD, face, plinth, h);                                        // the cabinet
                Add(-halfW + 30, halfW - 30, -halfD + 30, face - 30, 0, plinth, PartFinish.Dark);  // its plinth, set back in shadow

                // Doors at the ends and an open shelf between them, or two doors on a short one: push to open.
                if (w >= 1000)
                {
                    var door = Math.Min(w * 0.3, 500);
                    Add(-halfW + 2, -halfW + door - 2, face, halfD, plinth + 3, h - 3);
                    Add(halfW - door + 2, halfW - 2, face, halfD, plinth + 3, h - 3);
                    Add(-halfW + door + 20, halfW - door - 20, face, face + 1, plinth + 20, h - 20, PartFinish.Dark);
                }
                else
                {
                    Add(-halfW + 2, -2, face, halfD, plinth + 3, h - 3);
                    Add(2, halfW - 2, face, halfD, plinth + 3, h - 3);
                }

                // The television standing on it: a 16:9 screen most of its width, on a foot.
                var (screen, forward) = Television(type);
                Add(-screen * 0.15, screen * 0.15, forward - 90, forward + 90, h, h + 12, PartFinish.Dark);
                Add(-30, 30, forward - 15, forward + 15, h + 12, h + 70, PartFinish.Dark);
                Add(-screen / 2, screen / 2, forward - 20, forward + 20, h + 70, h + 70 + screen * 9 / 16, PartFinish.Dark);
                break;
            }

            case ComponentForm.Shower:
            {
                var tray = ShowerTray(type);
                var front = halfD - ShowerInset;
                var panes = ShowerGlass(type);

                Add(-halfW, halfW, -halfD, halfD, 0, tray);                                         // the tray
                Add(-45, 45, -45, 45, tray, tray + 2, PartFinish.Metal, PartShape.Upright);          // its waste
                foreach (var pane in panes)
                    Add(pane.From, pane.To, pane.Back, pane.Front, tray, h, PartFinish.Glass);

                // Where the glass meets a wall, a channel up the wall; at a corner, a post; and a handle on the door.
                switch (type.Enclosure)
                {
                    case ShowerEnclosure.Alcove when panes.Count == 2:
                    {
                        Add(-halfW, -halfW + 15, front - 40, front + 5, tray, h, PartFinish.Metal);
                        Add(halfW - 15, halfW, front - 40, front + 5, tray, h, PartFinish.Metal);
                        var handle = halfW * 0.6;
                        Add(handle - 8, handle + 8, panes[1].Front, panes[1].Front + 30, tray + 900, tray + 1300, PartFinish.Metal);
                        break;
                    }

                    case ShowerEnclosure.Corner when panes.Count == 2:
                    {
                        Add(-halfW, -halfW + 15, front - 30, front + 5, tray, h, PartFinish.Metal);
                        Add(halfW - ShowerInset - 30, halfW - ShowerInset + 5, -halfD, -halfD + 15, tray, h, PartFinish.Metal);
                        Add(halfW - ShowerInset - 20, halfW - ShowerInset + 5, front - 20, front + 5, tray, h, PartFinish.Metal);
                        var handle = halfW - 150;
                        Add(handle - 8, handle + 8, front, front + 22, tray + 900, tray + 1300, PartFinish.Metal);
                        break;
                    }

                    case ShowerEnclosure.WalkIn when panes.Count == 1:
                    {
                        // Its channel at the wall, and the stay from the top of its free end back to the wall behind.
                        Add(-halfW, -halfW + 15, front - 30, front + 5, tray, h, PartFinish.Metal);
                        Add(panes[0].To - 40, panes[0].To - 25, -halfD, panes[0].Back, h - 40, h - 25, PartFinish.Metal);
                        break;
                    }
                }

                break;
            }

            case ComponentForm.Custom:
                // Its own solids, at its size; with nothing drawn yet, and no model, a plain box to put down.
                parts.AddRange(type.Solids.Select(solid => solid.Part(w, d, h)));
                if (type.Solids.Count == 0 && type.Mesh is null) Add(-halfW, halfW, -halfD, halfD, 0, h);
                break;

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

            case ComponentForm.Basin when type.IsHosted:
            {
                // Hung on the wall: the bowl, and its bottle trap under it down to the wall.
                var bowl = Math.Min(h, 180);
                Add(-halfW, halfW, -halfD, halfD, h - bowl, h);                                      // the bowl
                Add(-30, 30, -halfD + 30, -halfD + 90, 0, h - bowl, PartFinish.Metal, PartShape.Upright); // the trap
                Add(-40, 40, -halfD + 20, -halfD + 120, h, h + 140);                                 // the tap
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
                // The cistern at the back, the pan on its foot in front of it, its seat at 420 or
                // so - as a WC's is, a little lower on a small one.
                var cistern = Math.Min(d * 0.3, 200);
                var seat = SeatOf(type);
                Add(-halfW * 0.8, halfW * 0.8, -halfD + cistern, halfD, seat * 0.3, seat);        // the pan
                Add(-halfW * 0.5, halfW * 0.5, -halfD + cistern - 20, halfD - 40, 0, seat * 0.3);  // its foot
                Add(-halfW, halfW, -halfD, -halfD + cistern, 0, h);                                // the cistern
                break;
            }

            case ComponentForm.WallHungToilet:
            {
                // The pan off the wall, clear of the floor, its seat at 420; its cistern in the wall,
                // and the flush plate on the wall over it.
                var seat = SeatOf(type);
                Add(-halfW * 0.85, halfW * 0.85, -halfD + 15, halfD, seat - 220, seat);
                Add(-halfW, halfW, -halfD, -halfD + 15, seat - 200, seat + 30);
                Add(-120, 120, -halfD, -halfD + 12, Math.Max(seat + 200, h - 260), Math.Max(seat + 300, h - 100), PartFinish.White);
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
                var trunk = Math.Min(Math.Min(w, d) * 0.08, 450);
                Add(-trunk / 2, trunk / 2, -trunk / 2, trunk / 2, 0, h * 0.45, PartFinish.Bark, PartShape.Upright);

                // The canopy, as round slices of a ball from 0.38 of its height to the top.
                const double low = 0.38, slices = 7;
                for (var i = 0; i < slices; i++)
                {
                    var bottom = low + (1 - low) * i / slices;
                    var top = low + (1 - low) * (i + 1) / slices;
                    var off = ((bottom + top) / 2 - (1 + low) / 2) / ((1 - low) / 2);
                    var spread = Math.Max(0.35, Math.Sqrt(1 - off * off));
                    Add(-halfW * spread, halfW * spread, -halfD * spread, halfD * spread, h * bottom, h * top, PartFinish.Body, PartShape.Upright);
                }

                break;
            }

            case ComponentForm.Conifer:
            {
                var trunk = Math.Min(Math.Min(w, d) * 0.08, 120);
                Add(-trunk / 2, trunk / 2, -trunk / 2, trunk / 2, 0, h * 0.15, PartFinish.Bark, PartShape.Upright);

                // Tiers narrowing to its tip, as a cone is built of discs.
                const int tiers = 7;
                for (var i = 0; i < tiers; i++)
                {
                    var spread = 1 - (double)i / tiers;
                    var bottom = h * (0.1 + 0.9 * i / tiers);
                    var top = Math.Min(h, h * (0.1 + 0.9 * (i + 1) / tiers) + h * 0.03);
                    Add(-halfW * spread, halfW * spread, -halfD * spread, halfD * spread, bottom, top, PartFinish.Body, PartShape.Upright);
                }

                break;
            }

            case ComponentForm.Shrub:
                Add(-halfW * 0.8, halfW * 0.8, -halfD * 0.8, halfD * 0.8, 0, h * 0.45, PartFinish.Body, PartShape.Upright);
                Add(-halfW, halfW, -halfD, halfD, h * 0.25, h * 0.8, PartFinish.Body, PartShape.Upright);
                Add(-halfW * 0.65, halfW * 0.65, -halfD * 0.65, halfD * 0.65, h * 0.7, h, PartFinish.Body, PartShape.Upright);
                break;

            case ComponentForm.Radiator:
            {
                var valve = Math.Min(50, h * 0.1);
                var standOff = Math.Min(25, d * 0.25);

                // On its valves at either end, off the wall on its brackets: the panels, and the
                // ribs pressed into the front one every 50.
                foreach (var across in new[] { -halfW + 40, halfW - 40 })
                    Add(across - 15, across + 15, -halfD + standOff, -halfD + standOff + 30, 0, valve + 5, PartFinish.Metal, PartShape.Upright);
                Add(-halfW, halfW, -halfD + standOff, halfD - 3, valve, h);
                for (var across = -halfW + 25; across < halfW - 10; across += 50)
                    Add(across - 8, across + 8, halfD - 3, halfD, valve + 15, h - 15);
                break;
            }

            case ComponentForm.TowelRail:
            {
                // A ladder rail: its two uprights off the wall on their brackets, and bars across
                // between them every 80 or so.
                var off = Math.Min(40, d * 0.4);
                Add(-halfW, -halfW + 30, -halfD + off, halfD, 0, h, shape: PartShape.Upright);
                Add(halfW - 30, halfW, -halfD + off, halfD, 0, h, shape: PartShape.Upright);
                foreach (var z in new[] { h * 0.15, h * 0.85 })
                foreach (var across in new[] { -halfW + 5, halfW - 25 })
                    Add(across, across + 20, -halfD, -halfD + off, z, z + 20, PartFinish.Metal);
                for (var z = 60.0; z < h - 40; z += 80)
                    Add(-halfW + 30, halfW - 30, -halfD + off + 4, halfD - 4, z, z + 22);
                break;
            }

            case ComponentForm.ExtractFan:
            {
                // Its grille on the wall, slatted across.
                Add(-halfW, halfW, -halfD, halfD - 4, 0, h);
                for (var z = h * 0.2; z < h * 0.8; z += Math.Max(12, h * 0.12))
                    Add(-halfW * 0.7, halfW * 0.7, halfD - 4, halfD, z, z + 4, PartFinish.Dark);
                break;
            }

            case ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm:
            {
                // A disc on the ceiling, and its light.
                Add(-halfW, halfW, -halfD, halfD, h * 0.2, h, shape: PartShape.Upright);
                Add(-halfW * 0.7, halfW * 0.7, -halfD * 0.7, halfD * 0.7, 0, h * 0.2, shape: PartShape.Upright);
                Add(-6, 6, halfD * 0.4, halfD * 0.4 + 12, 0, 2, type.Form == ComponentForm.SmokeAlarm ? PartFinish.Dark : PartFinish.Metal, PartShape.Upright);
                break;
            }

            case ComponentForm.ShaverSocket:
            {
                // Its plate, and the slots for two-pin and three-pin plugs.
                Add(-halfW, halfW, -halfD, halfD - 3, 0, h);
                foreach (var across in new[] { -w * 0.2, w * 0.2 })
                {
                    Add(across - 4, across + 4, halfD - 3, halfD, h * 0.45, h * 0.6, PartFinish.Dark);
                    Add(across + 12, across + 20, halfD - 3, halfD, h * 0.45, h * 0.6, PartFinish.Dark);
                }

                break;
            }

            case ComponentForm.ConsumerUnit:
            {
                // Its box, the cover over its breakers, and the window they are seen through.
                Add(-halfW, halfW, -halfD, halfD - 15, 0, h);
                Add(-halfW + 5, halfW - 5, halfD - 15, halfD, 5, h - 5);
                Add(-halfW + 30, halfW - 30, halfD - 1, halfD, h * 0.45, h * 0.7, PartFinish.Dark);
                break;
            }

            case ComponentForm.ParkingBay:
            {
                // Its lines painted on the ground, 100 wide, down both sides and across the back - open
                // at the front, where it is driven into.
                Add(-halfW, halfW, -halfD, halfD, 0, Math.Min(1, h));
                Add(-halfW, -halfW + 100, -halfD, halfD, Math.Min(1, h), h, PartFinish.White);
                Add(halfW - 100, halfW, -halfD, halfD, Math.Min(1, h), h, PartFinish.White);
                Add(-halfW + 100, halfW - 100, -halfD, -halfD + 100, Math.Min(1, h), h, PartFinish.White);
                break;
            }

            case ComponentForm.AccessibleParkingBay:
            {
                // The bay's lines, and its zones beside it and behind it to get in and out of the car,
                // striped across.
                var (side, rear) = AccessibleBayOf(type);
                var paint = Math.Min(1, h);
                Add(-halfW, halfW, -halfD, halfD, 0, paint);
                Add(-halfW, -halfW + 100, -halfD, halfD, paint, h, PartFinish.White);
                Add(halfW - 100, halfW, -halfD, halfD, paint, h, PartFinish.White);
                Add(-halfW + 100, halfW - 100, -halfD, -halfD + 100, paint, h, PartFinish.White);
                if (side > 0)
                {
                    Add(halfW - side - 50, halfW - side + 50, -halfD + rear, halfD, paint, h, PartFinish.White);
                    for (var forward = -halfD + rear + 300; forward < halfD - 200; forward += 600)
                        Add(halfW - side + 150, halfW - 150, forward, forward + 150, paint, h, PartFinish.White);
                }

                if (rear > 0)
                {
                    Add(-halfW + 100, halfW - side, -halfD + rear - 50, -halfD + rear + 50, paint, h, PartFinish.White);
                    for (var across = -halfW + 300; across < halfW - side - 200; across += 600)
                        Add(across, across + 150, -halfD + 250, -halfD + rear - 150, paint, h, PartFinish.White);
                }

                break;
            }

            case ComponentForm.WheelchairUser:
            {
                // The chair - its big wheels at the back, the seat at 500 between them, its back and
                // footplates - and the person sitting in it.
                var wheel = Math.Min(610, h * 0.46);
                foreach (var across in new[] { -halfW, halfW - 40 })
                    Add(across, across + 40, -halfD + 60, -halfD + 60 + wheel, 0, wheel, PartFinish.Dark);
                Add(-halfW + 60, halfW - 60, -halfD + 140, -halfD + 600, 470, 510, PartFinish.Dark);
                Add(-halfW + 60, halfW - 60, -halfD + 100, -halfD + 140, 470, 900, PartFinish.Dark);
                Add(-halfW + 120, halfW - 120, halfD - 160, halfD, 80, 100, PartFinish.Metal);
                Add(-180, 180, -halfD + 140, -halfD + 390, 510, h * 0.8);
                Add(-170, 170, -halfD + 390, halfD - 230, 510, 640);
                Add(-160, 160, halfD - 290, halfD - 150, 100, 510);
                Add(-95, 95, -halfD + 170, -halfD + 360, h * 0.8, h, PartFinish.Skin, PartShape.Upright);
                break;
            }

            case ComponentForm.TurningCircle:
                // A disc on the floor, the size of the turning a wheelchair user needs.
                Add(-halfW, halfW, -halfD, halfD, 0, h, PartFinish.Body, PartShape.Upright);
                break;

            case ComponentForm.Boiler:
            {
                // Its white case, the controls along the bottom of its front, and its flue out of the top.
                Add(-halfW, halfW, -halfD, halfD, 0, h);
                Add(-halfW + 40, halfW - 40, halfD - 1, halfD, h * 0.04, h * 0.14, PartFinish.Dark);
                Add(-50, 50, -halfD + 40, -halfD + 140, h, h + 150, PartFinish.White, PartShape.Upright);
                break;
            }

            case ComponentForm.HotWaterCylinder:
            {
                // A round copper or steel cylinder on its base, its domed top, and the pipe out of the top.
                Add(-halfW, halfW, -halfD, halfD, 0, h * 0.92, PartFinish.Body, PartShape.Upright);
                Add(-halfW * 0.8, halfW * 0.8, -halfD * 0.8, halfD * 0.8, h * 0.92, h, PartFinish.Body, PartShape.Upright);
                Add(-14, 14, -14, 14, h, h + 60, PartFinish.Metal, PartShape.Upright);
                break;
            }

            case ComponentForm.StopValve:
            {
                // The valve's brass body on its pipe, and the handle on top.
                Add(-halfW, halfW, -halfD, halfD, h * 0.35, h * 0.65);
                Add(-15, 15, -15, 15, 0, h, PartFinish.Metal, PartShape.Upright);
                Add(-halfW * 0.8, halfW * 0.8, -12, 12, h * 0.75, h * 0.85, PartFinish.Dark);
                break;
            }

            case ComponentForm.GasMeter:
            {
                // The meter box on the wall, and the meter behind its door.
                Add(-halfW, halfW, -halfD, halfD - 10, 0, h);
                Add(-halfW + 10, halfW - 10, halfD - 10, halfD, 10, h - 10, PartFinish.White);
                break;
            }

            case ComponentForm.Thermostat:
            {
                // Its plate and the dial on it.
                Add(-halfW, halfW, -halfD, halfD - 8, 0, h);
                Add(-w * 0.3, w * 0.3, halfD - 8, halfD, h * 0.2, h * 0.8, PartFinish.Metal, PartShape.Facing);
                break;
            }

            case ComponentForm.CeilingLight:
            {
                if (h > 250)
                {
                    // A pendant: the rose on the ceiling, the flex, and the shade at the bottom of it.
                    var rose = Math.Min(60, halfW * 0.4);
                    var shade = Math.Min(260, h * 0.4);
                    Add(-rose, rose, -rose, rose, h - 25, h, PartFinish.White, PartShape.Upright);
                    Add(-4, 4, -4, 4, shade, h - 25, PartFinish.Dark, PartShape.Upright);
                    Add(-halfW, halfW, -halfD, halfD, 0, shade * 0.55, PartFinish.Body, PartShape.Upright);
                    Add(-halfW * 0.7, halfW * 0.7, -halfD * 0.7, halfD * 0.7, shade * 0.55, shade * 0.85, PartFinish.Body, PartShape.Upright);
                    Add(-halfW * 0.3, halfW * 0.3, -halfD * 0.3, halfD * 0.3, shade * 0.85, shade, PartFinish.Body, PartShape.Upright);
                }
                else
                {
                    // Flush to the ceiling: its base, and the diffuser domed under it.
                    Add(-halfW, halfW, -halfD, halfD, h * 0.7, h, PartFinish.Body, PartShape.Upright);
                    Add(-halfW * 0.85, halfW * 0.85, -halfD * 0.85, halfD * 0.85, h * 0.3, h * 0.7, PartFinish.White, PartShape.Upright);
                    Add(-halfW * 0.55, halfW * 0.55, -halfD * 0.55, halfD * 0.55, 0, h * 0.3, PartFinish.White, PartShape.Upright);
                }

                break;
            }

            case ComponentForm.Socket:
            {
                // The plate, and an outlet and its switch for each socket on it - two on a double.
                Add(-halfW, halfW, -halfD, halfD - 3, 0, h);
                var outlets = w > 110 ? 2 : 1;
                var each = w / outlets;
                for (var i = 0; i < outlets; i++)
                {
                    var from = -halfW + each * i;
                    var middle = from + each * 0.36;
                    Add(from + 10, from + each * 0.62, halfD - 3, halfD - 1, h * 0.2, h * 0.8);
                    Add(middle - 2, middle + 2, halfD - 1, halfD, h * 0.58, h * 0.72, PartFinish.Dark);           // earth
                    Add(middle - 18, middle - 8, halfD - 1, halfD, h * 0.3, h * 0.36, PartFinish.Dark);          // neutral
                    Add(middle + 8, middle + 18, halfD - 1, halfD, h * 0.3, h * 0.36, PartFinish.Dark);          // live
                    Add(from + each * 0.7, from + each - 10, halfD - 3, halfD, h * 0.38, h * 0.62);              // its switch
                }

                break;
            }

            case ComponentForm.Switch:
            {
                Add(-halfW, halfW, -halfD, halfD - 4, 0, h);
                var gangs = Math.Max(1, (int)Math.Round(w / 86));
                var each = w / gangs;
                for (var i = 0; i < gangs; i++)
                    Add(-halfW + each * (i + 0.32), -halfW + each * (i + 0.68), halfD - 4, halfD, h * 0.28, h * 0.72);
                break;
            }

            case ComponentForm.Car:
            {
                // Its length runs across it, its front to the right; its width is front to back.
                var wheel = Math.Min(330, h * 0.22);
                var axle = Math.Max(650, w * 0.17);
                var sill = wheel * 0.55;
                var waist = h * 0.55;

                foreach (var across in new[] { -halfW + axle, halfW - axle })
                {
                    Add(across - wheel, across + wheel, -halfD + 15, -halfD + 210, 0, 2 * wheel, PartFinish.Dark, PartShape.Facing);
                    Add(across - wheel, across + wheel, halfD - 210, halfD - 15, 0, 2 * wheel, PartFinish.Dark, PartShape.Facing);
                    Add(across - wheel * 0.55, across + wheel * 0.55, -halfD, -halfD + 15, wheel * 0.45, wheel * 1.55, PartFinish.Metal, PartShape.Facing);
                    Add(across - wheel * 0.55, across + wheel * 0.55, halfD - 15, halfD, wheel * 0.45, wheel * 1.55, PartFinish.Metal, PartShape.Facing);
                }

                Add(-halfW + 30, halfW - 30, -halfD + 40, halfD - 40, sill, waist);                          // the body
                Add(-halfW, -halfW + 30, -halfD + 80, halfD - 80, sill, sill + 200, PartFinish.Dark);          // the bumpers
                Add(halfW - 30, halfW, -halfD + 80, halfD - 80, sill, sill + 200, PartFinish.Dark);
                Add(-halfW * 0.5, halfW * 0.22, -halfD + 110, halfD - 110, waist, h - 70, PartFinish.Dark);   // its windows
                Add(-halfW * 0.45, halfW * 0.15, -halfD + 130, halfD - 130, h - 70, h);                       // the roof
                foreach (var (back, front) in new[] { (-halfD + 120, -halfD + 420), (halfD - 420, halfD - 120) })
                {
                    Add(halfW - 30, halfW, back, front, waist - 150, waist - 60, PartFinish.White);           // headlights
                    Add(-halfW, -halfW + 30, back, front, waist - 150, waist - 60, PartFinish.Dark);           // tail lights
                }

                break;
            }

            case ComponentForm.Person:
            {
                var (torso, arm) = PersonOf(type);
                var hip = h * 0.47;
                var shoulder = h * 0.81;
                var chin = h * 0.86;
                var leg = w * 0.16;

                Add(-leg - 10, -10, -halfD * 0.4, halfD * 0.4, 0, hip, PartFinish.Dark);                        // the legs
                Add(10, leg + 10, -halfD * 0.4, halfD * 0.4, 0, hip, PartFinish.Dark);
                Add(-torso, torso, -halfD * 0.6, halfD * 0.6, hip, shoulder);                                   // the body, in its clothes
                Add(-torso - arm, -torso, -halfD * 0.35, halfD * 0.35, hip - h * 0.02, shoulder - 10);         // the arms
                Add(torso, torso + arm, -halfD * 0.35, halfD * 0.35, hip - h * 0.02, shoulder - 10);
                Add(-w * 0.06, w * 0.06, -w * 0.06, w * 0.06, shoulder, chin, PartFinish.Skin, PartShape.Upright);
                Add(-w * 0.2, w * 0.2, -halfD * 0.75, halfD * 0.75, chin, h, PartFinish.Skin, PartShape.Upright);
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

        // Its footprint - round for what is round from above: a tree, a light, a person, a round
        // table; a car's rounded at its corners; a corner sofa's an L.
        var shapes = new List<IReadOnlyList<Point2D>>
        {
            type.Form switch
            {
                ComponentForm.Tree or ComponentForm.Conifer or ComponentForm.Shrub or ComponentForm.CeilingLight or ComponentForm.RoundTable
                    or ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm or ComponentForm.TurningCircle
                    or ComponentForm.HotWaterCylinder =>
                    frame.Ellipse(-halfW, halfW, -halfD, halfD, 32),
                ComponentForm.CornerSofa => CornerOf(type) is var (run, turn)
                    ? new[] { frame.Plan(-halfW, -halfD), frame.Plan(halfW, -halfD), frame.Plan(halfW, halfD), frame.Plan(halfW - turn, halfD), frame.Plan(halfW - turn, -halfD + run), frame.Plan(-halfW, -halfD + run) }
                    : frame.Outline(-halfW, halfW, -halfD, halfD),
                ComponentForm.Person => frame.Ellipse(-PersonOf(type).Torso - PersonOf(type).Arm, PersonOf(type).Torso + PersonOf(type).Arm, -halfD * 0.6, halfD * 0.6),
                ComponentForm.Car => frame.Rounded(-halfW, halfW, -halfD, halfD, Math.Min(300, d * 0.2)),
                _ => frame.Outline(-halfW, halfW, -halfD, halfD)
            }
        };

        void Inside(double from, double to, double back, double front) =>
            shapes.Add(frame.Outline(from, to, back, front));

        switch (type.Form)
        {
            case ComponentForm.Chair:
                Inside(-halfW, halfW, -halfD, -halfD + Math.Min(60, d * 0.18));
                break;

            case ComponentForm.Sofa:
            {
                var (arm, back, _, _) = SofaOf(type, d);
                Inside(-halfW, halfW, -halfD, -halfD + back);
                Inside(-halfW, -halfW + arm, -halfD + back, halfD);
                Inside(halfW - arm, halfW, -halfD + back, halfD);
                break;
            }

            case ComponentForm.CornerSofa:
            {
                var (run, turn) = CornerOf(type);
                var (arm, back, _, _) = SofaOf(type, run);
                Inside(-halfW, halfW, -halfD, -halfD + back);
                Inside(halfW - back, halfW, -halfD + back, halfD);
                Inside(-halfW, -halfW + arm, -halfD + back, -halfD + run);
                Inside(halfW - turn, halfW - back, halfD - arm, halfD);
                break;
            }

            case ComponentForm.RoundTable:
            {
                // Its pedestal's foot, seen under the top.
                var foot = Math.Min(Math.Min(w, d) * 0.5, 600);
                shapes.Add(frame.Ellipse(-foot / 2, foot / 2, -foot / 2, foot / 2, 24));
                break;
            }

            case ComponentForm.Drawers:
            {
                // Its drawer fronts, and where two columns of them meet.
                Inside(-halfW, halfW, halfD - 40, halfD - 22);
                if (DrawersOf(type).Columns == 2) shapes.Add(frame.Line(0, halfD - 40, 0, halfD - 22));
                break;
            }

            case ComponentForm.Bed:
            {
                // The headboard, the mattress, the pillows, and the cover turned down at a corner.
                var (head, _, _) = BedOf(type);
                Inside(-halfW, halfW, -halfD, -halfD + head);
                Inside(-halfW + 15, halfW - 15, -halfD + head, halfD - 15);
                foreach (var (from, to) in Pillows(type)) shapes.Add(frame.Rounded(from, to, -halfD + head + 50, -halfD + head + 400, 60));

                var turned = -halfD + head + 480;
                var fold = Math.Min(300, (w - 30) * 0.3);
                shapes.Add(frame.Line(-halfW + 15, turned, halfW - 15, turned));
                shapes.Add(frame.Line(halfW - 15 - fold, turned, halfW - 15, turned + fold));
                break;
            }

            case ComponentForm.BunkBed:
            {
                // Seen from above: the upper bed in its rails, the posts, and the ladder's rungs in the way in.
                var (_, _, opening) = BunkOf(type);
                Inside(-halfW + 60, halfW - 60, -halfD + 60, halfD - 60);
                foreach (var across in new[] { -halfW, halfW - 60 })
                foreach (var forward in new[] { -halfD, halfD - 60 })
                    Inside(across, across + 60, forward, forward + 60);
                shapes.Add(frame.Line(halfW - 12, halfD - 60 - opening, halfW - 12, halfD - 60));
                break;
            }

            case ComponentForm.Cot:
                Inside(-halfW + 40, halfW - 40, -halfD + 40, halfD - 40);
                break;

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

            case ComponentForm.WallHungToilet:
                // Its pan, round at the front, and the flush plate on the wall.
                shapes.Add(frame.Rounded(-halfW * 0.85, halfW * 0.85, -halfD + 15, halfD, halfW * 0.8));
                Inside(-120, 120, -halfD, -halfD + 12);
                break;

            case ComponentForm.Bath:
            {
                var wall = Math.Min(80, Math.Min(w, d) * 0.12);
                Inside(-halfW + wall, halfW - wall, -halfD + wall, halfD - wall);
                break;
            }

            case ComponentForm.Tree:
                shapes.Add(frame.Ellipse(-halfW * 0.6, halfW * 0.6, -halfD * 0.6, halfD * 0.6, 24));
                shapes.Add(frame.Ellipse(-halfW * 0.06, halfW * 0.06, -halfD * 0.06, halfD * 0.06, 12));
                break;

            case ComponentForm.Conifer:
                // Its branches, radiating from the trunk.
                for (var i = 0; i < 8; i++)
                {
                    var angle = Math.PI * i / 4;
                    shapes.Add(frame.Line(0, 0, halfW * 0.9 * Math.Cos(angle), halfD * 0.9 * Math.Sin(angle)));
                }

                break;

            case ComponentForm.Shrub:
                shapes.Add(frame.Ellipse(-halfW * 0.6, halfW * 0.6, -halfD * 0.6, halfD * 0.6, 24));
                break;

            case ComponentForm.TowelRail:
                // Its bars, off the wall.
                Inside(-halfW, halfW, -halfD + Math.Min(40, d * 0.4), halfD);
                break;

            case ComponentForm.ExtractFan:
                // A fan: its grille, crossed.
                shapes.Add(frame.Line(-halfW, -halfD, halfW, halfD));
                shapes.Add(frame.Line(-halfW, halfD, halfW, -halfD));
                break;

            case ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm:
                shapes.Add(frame.Ellipse(-halfW * 0.6, halfW * 0.6, -halfD * 0.6, halfD * 0.6, 16));
                break;

            case ComponentForm.ShaverSocket:
            case ComponentForm.ConsumerUnit:
                Inside(-halfW + Math.Min(10, w * 0.1), halfW - Math.Min(10, w * 0.1), halfD - Math.Min(15, d * 0.4), halfD);
                break;

            case ComponentForm.Thermostat:
                shapes.Add(frame.Ellipse(-w * 0.3, w * 0.3, halfD - 8, halfD + 40, 12));
                break;

            case ComponentForm.Boiler:
                // Its flue, seen from above.
                shapes.Add(frame.Ellipse(-50, 50, -halfD + 40, -halfD + 140, 16));
                break;

            case ComponentForm.HotWaterCylinder:
                // The pipe out of its top.
                shapes.Add(frame.Ellipse(-14, 14, -14, 14, 12));
                break;

            case ComponentForm.StopValve:
                shapes.Add(frame.Ellipse(-15, 15, -15, 15, 12));
                break;

            case ComponentForm.GasMeter:
                // The door of its box.
                Inside(-halfW + 10, halfW - 10, halfD - 10, halfD);
                break;

            case ComponentForm.ParkingBay:
                // The inside of its lines.
                Inside(-halfW + 100, halfW - 100, -halfD + 100, halfD);
                break;

            case ComponentForm.AccessibleParkingBay:
            {
                // The bay, and its zones hatched.
                var (side, rear) = AccessibleBayOf(type);
                Inside(-halfW, halfW - side, -halfD + rear, halfD);
                // Hatched at 45 degrees, each line run on until it meets the zone's edge.
                void Hatch(double fromAcross, double fromForward, double toAcross, double toForward)
                {
                    var run = Math.Min(toAcross - fromAcross, toForward - fromForward);
                    if (run > 1) shapes.Add(frame.Line(fromAcross, fromForward, fromAcross + run, fromForward + run));
                }

                if (side > 0)
                    for (var forward = -halfD + rear; forward < halfD; forward += 400) Hatch(halfW - side, forward, halfW, halfD);
                if (rear > 0)
                    for (var across = -halfW; across < halfW - side; across += 400) Hatch(across, -halfD, halfW - side, -halfD + rear);
                break;
            }

            case ComponentForm.WheelchairUser:
                // The chair's wheels either side, and the person in it.
                Inside(-halfW, -halfW + 40, -halfD + 60, -halfD + 60 + Math.Min(610, type.Height * 0.46));
                Inside(halfW - 40, halfW, -halfD + 60, -halfD + 60 + Math.Min(610, type.Height * 0.46));
                shapes.Add(frame.Ellipse(-95, 95, -halfD + 170, -halfD + 360, 16));
                break;

            case ComponentForm.TurningCircle:
                shapes.Add(frame.Line(-halfW, 0, halfW, 0));
                shapes.Add(frame.Line(0, -halfD, 0, halfD));
                break;

            case ComponentForm.CeilingLight:
                // A lighting point: a circle crossed.
                shapes.Add(frame.Line(-halfW * 0.7, -halfD * 0.7, halfW * 0.7, halfD * 0.7));
                shapes.Add(frame.Line(-halfW * 0.7, halfD * 0.7, halfW * 0.7, -halfD * 0.7));
                break;

            case ComponentForm.Radiator:
                Inside(-halfW, halfW, -halfD + Math.Min(25, d * 0.25), halfD - 3);
                break;

            case ComponentForm.Socket:
            {
                // A socket outlet: a half circle off the wall, a stroke out of it for each socket.
                var radius = 60.0;
                var arc = Enumerable.Range(0, 13)
                    .Select(i => Math.PI * i / 12)
                    .Select(angle => frame.Plan(radius * Math.Cos(angle), halfD + radius * Math.Sin(angle)))
                    .ToList();
                shapes.Add(arc);
                foreach (var across in w > 110 ? new[] { -25.0, 25.0 } : new[] { 0.0 })
                    shapes.Add(frame.Line(across, halfD + Math.Sqrt(radius * radius - across * across), across, halfD + radius + 45));
                break;
            }

            case ComponentForm.Switch:
                // A switch: a small circle off the wall, a stroke from it.
                shapes.Add(frame.Ellipse(-25, 25, halfD + 40, halfD + 90, 16));
                shapes.Add(frame.Line(18, halfD + 83, 60, halfD + 125));
                break;

            case ComponentForm.Car:
            {
                // Its cabin, and the windscreen across the front of it.
                shapes.Add(frame.Rounded(-halfW * 0.5, halfW * 0.22, -halfD + 110, halfD - 110, 120));
                shapes.Add(frame.Line(halfW * 0.08, -halfD + 130, halfW * 0.08, halfD - 130));
                break;
            }

            case ComponentForm.Person:
                shapes.Add(frame.Ellipse(-w * 0.2, w * 0.2, -halfD * 0.75, halfD * 0.75, 16));
                break;

            case ComponentForm.Custom:
            {
                // Each solid as it is seen from above - round ones round - and a model's outline.
                foreach (var part in type.Solids.Select(solid => solid.Part(w, d, type.Height)))
                    shapes.Add(part.Shape is PartShape.Upright or PartShape.Sphere or PartShape.Cone
                        ? frame.Ellipse(part.From, part.To, part.Back, part.Front, 24)
                        : frame.Outline(part.From, part.To, part.Back, part.Front));

                if (type.Mesh?.PlanHull is { Count: >= 3 } hull)
                    shapes.Add(hull.Select(point => frame.Plan(point.X * w, point.Y * d)).ToList());
                break;
            }

            case ComponentForm.Fridge:
            {
                var (door, face) = FridgeFront(type);
                Inside(-halfW, halfW, face - door, face);
                if (w > 800) shapes.Add(frame.Line(-halfW + w * 0.42, face - door, -halfW + w * 0.42, face));
                break;
            }

            case ComponentForm.Cooker:
            {
                var (hob, burners) = Hob(type);
                Inside(hob.From, hob.To, hob.Back, hob.Front);
                foreach (var (across, forward, radius) in burners)
                    shapes.Add(frame.Ellipse(across - radius, across + radius, forward - radius, forward + radius));
                break;
            }

            case ComponentForm.Dishwasher:
            case ComponentForm.WashingMachine:
            case ComponentForm.TumbleDryer:
                Inside(-halfW, halfW, -halfD, -halfD + Math.Min(80, d * 0.25));
                break;

            case ComponentForm.WallUnit:
            case ComponentForm.TallUnit:
            case ComponentForm.OvenHousing:
                // Its doors' faces, and - a tall one, standing through the plan's cut - a cross
                // corner to corner, as a plan draws a tall unit.
                Inside(-halfW, halfW, halfD - 40, halfD - 22);
                if (type.Form != ComponentForm.WallUnit)
                {
                    shapes.Add(frame.Line(-halfW, -halfD, halfW, halfD - 40));
                    shapes.Add(frame.Line(-halfW, halfD - 40, halfW, -halfD));
                }

                break;

            case ComponentForm.CookerHood:
            {
                // Its canopy, and the chimney at its back.
                var chimney = ChimneyOf(type);
                Inside(-chimney.Width / 2, chimney.Width / 2, -halfD, -halfD + chimney.Depth);
                break;
            }

            case ComponentForm.Sink:
            {
                var (rim, bowls, drainer, (tapAcross, tapForward)) = Sink(type);
                shapes.Add(frame.Rounded(rim.From, rim.To, rim.Back, rim.Front, 30));
                foreach (var bowl in bowls)
                {
                    shapes.Add(frame.Rounded(bowl.From, bowl.To, bowl.Back, bowl.Front, 40));
                    shapes.Add(frame.Ellipse(bowl.Across - 25, bowl.Across + 25, bowl.Forward - 25, bowl.Forward + 25, 16));
                }

                if (drainer is { } board)
                    foreach (var across in DrainerRibs(board))
                        shapes.Add(frame.Line(across, board.Back + 30, across, board.Front - 30));

                shapes.Add(frame.Ellipse(tapAcross - 22, tapAcross + 22, tapForward - 22, tapForward + 22, 16));
                break;
            }

            case ComponentForm.Wardrobe:
                Inside(-halfW, halfW, halfD - 36, halfD);                                // its doors
                shapes.Add(frame.Line(-halfW + 50, -18, halfW - 50, -18));               // the hanging rail
                break;

            case ComponentForm.Armchair:
            {
                var (arm, back, _) = ArmchairOf(type);
                Inside(-halfW, halfW, -halfD, -halfD + back);
                Inside(-halfW, -halfW + arm, -halfD + back, halfD);
                Inside(halfW - arm, halfW, -halfD + back, halfD);
                Inside(-halfW + arm + 5, halfW - arm - 5, -halfD + back + 5, halfD - 5);
                break;
            }

            case ComponentForm.CoffeeTable:
            {
                var inset = Math.Min(30, Math.Min(w, d) * 0.06);
                Inside(-halfW + inset, halfW - inset, -halfD + inset, halfD - inset);
                break;
            }

            case ComponentForm.TelevisionUnit:
            {
                var (screen, forward) = Television(type);
                Inside(-halfW, halfW, halfD - 18, halfD);
                Inside(-screen / 2, screen / 2, forward - 20, forward + 20);
                break;
            }

            case ComponentForm.Shower:
                // The tray crossed corner to corner, as a plan draws one, its waste, and its glass.
                shapes.Add(frame.Line(-halfW, -halfD, halfW, halfD));
                shapes.Add(frame.Line(-halfW, halfD, halfW, -halfD));
                shapes.Add(frame.Ellipse(-45, 45, -45, 45, 16));
                foreach (var pane in ShowerGlass(type)) Inside(pane.From, pane.To, pane.Back, pane.Front);
                break;
        }

        return shapes;
    }

    /// <summary>What an appliance is lettered with on a plan, as drawings do: F, FF, DW, WM - or nothing.</summary>
    public static string? PlanLabel(ComponentType type) => type.Form switch
    {
        ComponentForm.Fridge => type.Height > 1200 ? "FF" : "F",
        ComponentForm.Dishwasher => "DW",
        ComponentForm.WashingMachine => "WM",
        ComponentForm.TumbleDryer => "TD",
        ComponentForm.OvenHousing => "OV",
        ComponentForm.SmokeAlarm => "S",
        ComponentForm.HeatAlarm => "H",
        ComponentForm.CarbonMonoxideAlarm => "CO",
        ComponentForm.ExtractFan => "EF",
        ComponentForm.ShaverSocket => "SH",
        ComponentForm.ConsumerUnit => "CU",
        ComponentForm.Thermostat => "T",
        ComponentForm.Boiler => "B",
        ComponentForm.HotWaterCylinder => "HWC",
        ComponentForm.StopValve => "SV",
        ComponentForm.GasMeter => "GM",
        ComponentForm.ParkingBay or ComponentForm.AccessibleParkingBay => "P",
        ComponentForm.TelevisionUnit => "TV",
        _ => null
    };

    /// <summary>
    /// Whether one hung above where a plan cuts is drawn dashed, as overhead: a wall unit, a
    /// shelf. A light, a socket and a switch are drawn as their symbols wherever they are.
    /// </summary>
    public static bool DrawnOverhead(ComponentType type) =>
        type.Form is not (ComponentForm.CeilingLight or ComponentForm.WallLight or ComponentForm.Socket or ComponentForm.Switch
            or ComponentForm.ExtractFan or ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm
            or ComponentForm.ShaverSocket or ComponentForm.ConsumerUnit or ComponentForm.Thermostat or ComponentForm.Boiler
            or ComponentForm.StopValve or ComponentForm.GasMeter);

    /// <summary>How high a WC's seat is: 420 or so, as a WC's is - the ADA's 17 to 19 in, 430 to 485, on an accessible one made taller.</summary>
    public static double SeatOf(ComponentType type) => Math.Min(type.Height * 0.55, 420);

    /// <summary>
    /// A bed's headboard, how thick; the height of the top of its mattress - 550 or so, as the
    /// UK's beds are, the US's 600 - and how thick the mattress is.
    /// </summary>
    private static (double Head, double Top, double Mattress) BedOf(ComponentType type)
    {
        var top = Math.Min(type.Height * 0.55, 600);
        return (Math.Min(type.Depth * 0.05, 80), top, Math.Min(250, top * 0.45));
    }

    /// <summary>Where a bed's pillows lie across its head: two side by side, or one on a single.</summary>
    private static IEnumerable<(double From, double To)> Pillows(ComponentType type)
    {
        var count = type.Width > 1100 ? 2 : 1;
        var span = (type.Width - 200) / count;
        for (var i = 0; i < count; i++)
        {
            var from = -type.Width / 2 + 100 + i * span;
            yield return (from + 30, from + span - 30);
        }
    }

    /// <summary>
    /// A bunk bed's two beds - the tops of their bases - and how wide the way in to the upper one
    /// is: its base 1000 or so up, well over EN 747's 600, its rails to the posts' tops; the way
    /// in 400, the most EN 747 lets it be.
    /// </summary>
    public static (double Lower, double Upper, double Opening) BunkOf(ComponentType type) =>
        (Math.Min(250, type.Height * 0.15), Math.Max(type.Height * 0.4, type.Height - 600), Math.Min(400, type.Depth * 0.3));

    /// <summary>
    /// A sofa's arms and back, how thick, the height of its seat and of its arms: the seat at
    /// 450 or so, as a sofa is sat on, a little lower on a low one; the arms at two thirds of its
    /// height. <paramref name="deep"/> is how deep its seats are from the back.
    /// </summary>
    private static (double Arm, double Back, double Seat, double ArmTop) SofaOf(ComponentType type, double deep) =>
        (Math.Min(type.Width * 0.14, 220), Math.Min(deep * 0.28, 260), Math.Min(type.Height * 0.55, 450), type.Height * 0.72);

    /// <summary>
    /// A corner sofa's L: how deep its run along the back is, to the front of its seats - 950,
    /// or less in a shallow one - and how wide the return down its right-hand side is, as deep.
    /// </summary>
    public static (double Run, double Turn) CornerOf(ComponentType type)
    {
        var run = Math.Min(950, type.Depth * 0.6);
        return (run, Math.Min(run, type.Width * 0.45));
    }

    /// <summary>A chest's drawers: a row to every 200 or so of its height, and two columns of them where it is a metre or more wide.</summary>
    public static (int Rows, int Columns) DrawersOf(ComponentType type) =>
        (Math.Max(1, (int)Math.Round((type.Height - Math.Min(60, type.Height * 0.08) - Math.Min(25, type.Height * 0.05)) / 200)), type.Width >= 1000 ? 2 : 1);

    /// <summary>How wide a person is across the body either side of the middle, and how thick an arm is.</summary>
    private static (double Torso, double Arm) PersonOf(ComponentType type) => (type.Width * 0.36, type.Width * 0.1);

    /// <summary>Whether one hangs from the ceiling over where it is put: a ceiling light, or a smoke, heat or carbon monoxide alarm.</summary>
    public static bool HangsFromCeiling(ComponentType type) =>
        type.Form is ComponentForm.CeilingLight or ComponentForm.SmokeAlarm or ComponentForm.HeatAlarm or ComponentForm.CarbonMonoxideAlarm;

    /// <summary>An armchair's arms and back, how thick, and the height of its seat - 450 or so on a 950 chair.</summary>
    private static (double Arm, double Back, double Seat) ArmchairOf(ComponentType type) =>
        (Math.Min(type.Width * 0.17, 170), Math.Min(type.Depth * 0.2, 190), type.Height * 0.47);

    /// <summary>The television on a TV unit: its screen's width - most of the unit's, up to a 65 inch one - and how far forward it stands.</summary>
    private static (double Screen, double Forward) Television(ComponentType type) =>
        (Math.Min(type.Width * 0.8, 1450), -type.Depth / 2 + type.Depth * 0.35);

    /// <summary>How high a shower's tray is: a low stone-resin one, 45.</summary>
    public static double ShowerTray(ComponentType type) => Math.Min(45, type.Height);

    /// <summary>How far in from the tray's edge its glass stands.</summary>
    private const double ShowerInset = 25;

    /// <summary>
    /// A shower's panes of glass, 8 thick, standing on its tray set in from the edge: a sliding
    /// door across an alcove's front in two overlapping panes; the front and the right side in a
    /// corner; or a fixed panel along the front from the left for a walk-in - 700, or half the
    /// tray, leaving at least 500 to step in at the right. None for a tray on its own.
    /// </summary>
    public static IReadOnlyList<Area> ShowerGlass(ComponentType type)
    {
        if (type.Form != ComponentForm.Shower || type.Height <= ShowerTray(type) + 100) return Array.Empty<Area>();

        var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
        const double glass = 8;
        var front = halfD - ShowerInset;
        return type.Enclosure switch
        {
            ShowerEnclosure.Alcove => new[]
            {
                new Area(-halfW + 15, type.Width * 0.05, front - glass, front),
                new Area(-type.Width * 0.05, halfW - 15, front - 2 * glass - 10, front - glass - 10)
            },
            ShowerEnclosure.Corner => new[]
            {
                new Area(-halfW + 15, halfW - ShowerInset, front - glass, front),
                new Area(halfW - ShowerInset - glass, halfW - ShowerInset, -halfD + 15, front - glass)
            },
            ShowerEnclosure.WalkIn => new[]
            {
                new Area(-halfW + 15, -halfW + 15 + Math.Min(Math.Max(700, type.Width * 0.5), type.Width - 530), front - glass, front)
            },
            _ => Array.Empty<Area>()
        };
    }

    /// <summary>
    /// Whether a shape is open on its right as its family is drawn, and wants its wall on the
    /// left: a corner shower's glass side, a walk-in's way in. Put into a corner the other way
    /// round, it is flipped to suit.
    /// </summary>
    public static bool OpenOnTheRight(ComponentType type) =>
        type.Form == ComponentForm.Shower && type.Enclosure is ShowerEnclosure.Corner or ShowerEnclosure.WalkIn && ShowerGlass(type).Count > 0;

    /// <summary>
    /// Where a tall housing's oven and microwave are: the oven's underside at 850, as an oven at
    /// eye level is set - its middle about 1150, a little over M4(3)'s 800 to 900 for a wheelchair
    /// user's - 600 of oven, and a 380 microwave over it.
    /// </summary>
    public static (double OvenBottom, double OvenTop, double MicrowaveTop) OvenHousingOf(ComponentType type)
    {
        var bottom = Math.Min(850, type.Height * 0.45);
        var top = Math.Min(bottom + 600, type.Height * 0.7);
        return (bottom, top, Math.Min(top + 385, type.Height - 100));
    }

    /// <summary>
    /// An accessible parking bay's zones: how wide the one beside the bay is - the UK's 1200, or
    /// the ADA's 60 in aisle - and how deep the one behind it is, where it has one: the UK's 1200.
    /// </summary>
    public static (double Side, double Rear) AccessibleBayOf(ComponentType type) =>
        (Math.Clamp(type.Width - (type.Width >= 3900 ? 2438 : 2400), 0, 1524), type.Depth >= 5900 ? Math.Min(1200, type.Depth - 4800) : 0);

    /// <summary>A chimney hood's chimney: 40% of the hood's width, 250 deep, at its back.</summary>
    public static (double Width, double Depth) ChimneyOf(ComponentType type) =>
        (Math.Max(150, type.Width * 0.4), Math.Min(250, type.Depth * 0.6));

    /// <summary>Whether a fridge is one that stands under the worktop: as high as one, a metre or less.</summary>
    public static bool UnderCounter(ComponentType type) => type.Form == ComponentForm.Fridge && type.Height <= 1000;

    /// <summary>A fridge's doors: how thick, and where their fronts are - its handles stand proud of them.</summary>
    private static (double Door, double Face) FridgeFront(ComponentType type) =>
        (Math.Min(50, type.Depth * 0.08), type.Depth / 2 - 30);

    /// <summary>
    /// A hob as it is set into the top: a glass panel nearly the cooker's width, its burners in
    /// two rows - two across on a 600 hob, three on a range of 900 or more - the front left and
    /// back right ones the big ones.
    /// </summary>
    public static (Area Panel, IReadOnlyList<(double Across, double Forward, double Radius)> Burners) Hob(ComponentType type)
    {
        var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
        var margin = Math.Min(20, type.Width * 0.04);
        var depth = Math.Clamp(type.Depth - 80, type.Depth * 0.6, 520);
        var panel = new Area(-halfW + margin, halfW - margin, -depth / 2, depth / 2);

        var columns = type.Width >= 900 ? 3 : 2;
        var cellWidth = (panel.To - panel.From) / columns;
        var cellDepth = depth / 2;
        var burners = new List<(double, double, double)>();
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < columns; column++)
        {
            var big = (row + column) % 2 == 1;
            burners.Add((panel.From + cellWidth * (column + 0.5), panel.Back + cellDepth * (row + 0.5),
                Math.Min(cellWidth, cellDepth) * (big ? 0.34 : 0.25)));
        }

        return (panel, burners);
    }

    /// <summary>
    /// An inset sink as it lies in the worktop of its unit: its rim 20 in from either end and
    /// set back from the front edge; a bowl on the left with the drainer beside it, where the
    /// unit is wide enough for one (800 or more) - with a half bowl between them on one of 1100
    /// or more - or one bowl in the middle of a narrower unit; and the tap on the deck behind
    /// the main bowl.
    /// </summary>
    public static (Area Rim, IReadOnlyList<Area> Bowls, Area? Drainer, (double Across, double Forward) Tap) Sink(ComponentType type)
    {
        var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
        var end = Math.Min(20, type.Width * 0.04);
        var front = halfD - Math.Min(70, type.Depth * 0.12);
        var rim = new Area(-halfW + end, halfW - end, Math.Max(-halfD + 20, front - 500), front);

        var lip = Math.Min(40, (rim.Front - rim.Back) * 0.1);
        var deck = Math.Min(90, (rim.Front - rim.Back) * 0.2);
        var (back, near) = (rim.Back + deck, rim.Front - lip);
        var width = rim.To - rim.From;

        var bowls = new List<Area>();
        Area? drainer = null;
        if (width < 750)
        {
            bowls.Add(new Area(rim.From + lip, rim.To - lip, back, near));
        }
        else
        {
            var main = new Area(rim.From + lip, rim.From + lip + Math.Min(450, width * 0.42), back, near);
            bowls.Add(main);
            if (width >= 1050) bowls.Add(new Area(main.To + lip, main.To + lip + Math.Min(200, width * 0.17), back, near));
            drainer = new Area(bowls[^1].To + lip, rim.To - lip, back, near);
        }

        return (rim, bowls, drainer, (bowls[0].Across, (rim.Back + back) / 2));
    }

    /// <summary>Where a drainer's ribs are across it: every 40, clear of its ends.</summary>
    private static IEnumerable<double> DrainerRibs(Area drainer)
    {
        for (var across = drainer.From + 30; across <= drainer.To - 30; across += 40) yield return across;
    }

    /// <summary>The footprint on its own, for picking one out of a plan and for a room's area.</summary>
    public static IReadOnlyList<Point2D> Footprint(ComponentType type, Frame frame) =>
        frame.Unmirrored.Outline(-type.Width / 2, type.Width / 2, -type.Depth / 2, type.Depth / 2);
}
