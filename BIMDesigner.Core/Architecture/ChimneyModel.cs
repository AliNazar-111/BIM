using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>Chimneys in 3D, each built the way its type is built.</summary>
public static partial class Chimneys
{
    private static readonly ColourRgb Brick = ColourRgb.FromHex("8E4B34"), Cap = ColourRgb.FromHex("A7A49C"), Pot = ColourRgb.FromHex("B5653F"),
        Mortar = ColourRgb.FromHex("9C9A92"), Lead = ColourRgb.FromHex("6F767D"), Soot = ColourRgb.FromHex("1A1A1A"),
        Sooted = ColourRgb.FromHex("2E2622"), Pumice = ColourRgb.FromHex("B9B4AA"), Stainless = ColourRgb.FromHex("C3C8CD"),
        Galvanised = ColourRgb.FromHex("A9AFB4"), Steel = ColourRgb.FromHex("6B7177"), Iron = ColourRgb.FromHex("26282B"),
        Timber = ColourRgb.FromHex("6E4A2C"), Slate = ColourRgb.FromHex("3C4045"), Fire = ColourRgb.FromHex("FF7A1A"),
        Glass = ColourRgb.FromHex("8CC4E0"), Cladding = ColourRgb.FromHex("D6CFBF"), Absorber = ColourRgb.FromHex("1E2124"),
        Warning = ColourRgb.FromHex("C8382F"), Grating = ColourRgb.FromHex("3A3D40");

    /// <summary>The flue liner in each flue of a masonry stack: its outside, and its wall, mm; and how far down it is drawn.</summary>
    public const double LinerSize = 225, LinerWall = 20, LinerDepth = 1200;

    /// <summary>The oversailing course near the top of a masonry stack: how far it stands out, how deep it is, and how far below the cap.</summary>
    public const double Oversail = 40, CourseDepth = 75, CourseBelowCap = 150;

    /// <summary>The flaunching round each pot: how far out over the cap it spreads, and how high up the pot it comes.</summary>
    public const double FlaunchingSpread = 70, FlaunchingRise = 70;

    /// <summary>The lead flashing where the stack meets the roof: how far up the stack it is let in, and how far out over the roof its apron lies.</summary>
    public const double FlashingUp = 150, FlashingApron = 150;

    /// <summary>How far above its foot a stack without a fireplace is solid, before its flues open: nothing open underneath.</summary>
    public const double SolidFoot = 600;

    /// <summary>The flue liners' outside in a masonry stack, kept inside the brickwork: a half brick at least each side.</summary>
    public static double LinerWidth(Chimney chimney) =>
        Math.Max(100, Math.Min(LinerSize, Math.Min(chimney.Width / Math.Max(1, chimney.Flues), chimney.Depth) - 2 * 100));

    /// <summary>Where a guyed stack's three wires are anchored in the ground, in plan: well out from it, a third of the way round from each other.</summary>
    public static IReadOnlyList<Point2D> Anchors(BimDocument document, Chimney chimney)
    {
        var reach = Math.Max((Top(document, chimney) - Foot(document, chimney)) * 0.45, chimney.Width * 5);
        return Enumerable.Range(0, 3)
            .Select(i => 2 * Math.PI * i / 3 + (chimney.Angle + 90) * Math.PI / 180)
            .Select(angle => chimney.Location + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * reach)
            .ToList();
    }

    private static Material? Named(BimDocument document, string start) =>
        document.Materials.FirstOrDefault(material => material.Name.StartsWith(start, StringComparison.OrdinalIgnoreCase));

    /// <summary>A chimney as the 3D view draws it, built as its type is built.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Chimney chimney)
    {
        var (foot, top) = (Foot(document, chimney), Top(document, chimney));
        if (top - foot < 300) return Array.Empty<Mesh3D>();

        var type = TypeOf(document, chimney);
        var meshes = type.Construction switch
        {
            ChimneyConstruction.Masonry or ChimneyConstruction.PrecastBlock => Stack(document, chimney, type, foot, top),
            ChimneyConstruction.TwinWallSteel or ChimneyConstruction.AirCooledMetal or ChimneyConstruction.TypeBVent or ChimneyConstruction.ConcentricFlue
                => Pipe(document, chimney, type, foot, top),
            ChimneyConstruction.SteelStack or ChimneyConstruction.GuyedStack => SteelStack(document, chimney, type, foot, top),
            ChimneyConstruction.IndustrialMultiFlue => Industrial(document, chimney, type, foot, top),
            ChimneyConstruction.SolarChimney => Solar(document, chimney, foot, top),
            _ => Chase(document, chimney, type, foot, top)
        };

        return meshes.Where(mesh => !mesh.IsEmpty).ToList();
    }

    private static Mesh3D New(Chimney chimney, MeshKind kind, ColourRgb colour, string name) =>
        new(chimney.Id, chimney.LevelId, kind, colour, name);

    /// <summary>
    /// A masonry stack - or a block system chimney - as it is built: a breast in the room with the
    /// fireplace opening in its front, the flues starting above it; the brickwork open down each
    /// lined flue; an oversailing course; the cap with the flues through it; the flaunching and
    /// hollow pots - or a block chimney's steel terminals; and lead flashing at the roof.
    /// </summary>
    private static List<Mesh3D> Stack(BimDocument document, Chimney chimney, ChimneyType type, double foot, double top)
    {
        var masonry = type.Construction == ChimneyConstruction.Masonry;
        var brick = masonry ? Named(document, "Brick") : null;
        var concrete = Named(document, "Concrete, Cast");
        var stack = New(chimney, MeshKind.Wall, brick?.SurfaceColour ?? (masonry ? Brick : Pumice), brick?.Name ?? (masonry ? "Brick" : "Pumice Block"));
        var meshes = new List<Mesh3D> { stack };

        var breast = Breast(document, chimney);
        var flues = Flues(document, chimney);
        var liner = masonry ? LinerWidth(chimney) : Math.Min(type.FlueDiameter + 40, Math.Min(chimney.Width / Math.Max(1, chimney.Flues), chimney.Depth) - 120);
        var holes = flues.Select(flue => masonry ? Rectangle(chimney, liner, liner, flue) : Circle(flue, liner / 2)).ToList();

        // Solid up to where the flues open: over the fireplace, or above a solid foot.
        var opens = breast?.Top ?? Math.Min(foot + SolidFoot, top - 300);
        if (breast is not null)
        {
            foreach (var part in PolygonBoolean.Combine(breast.Outline, breast.Opening, BooleanOperation.Difference))
                stack.AddExtrusion(part.Outer, foot, foot + breast.OpeningHeight);
            stack.AddExtrusion(breast.Outline, foot + breast.OpeningHeight, breast.Top);
            meshes.AddRange(Fireplace(document, chimney, breast, foot));
        }
        else
        {
            stack.AddExtrusion(Outline(document, chimney), foot, opens);
        }

        // Up to the cap, open down the flues: brick with an oversailing course near the top.
        var course = top - CourseBelowCap;
        if (masonry && course - CourseDepth > opens)
        {
            stack.AddExtrusion(Outline(document, chimney), holes, opens, course - CourseDepth);
            stack.AddExtrusion(Rectangle(chimney, chimney.Width + 2 * Oversail, chimney.Depth + 2 * Oversail), holes, course - CourseDepth, course);
            stack.AddExtrusion(Outline(document, chimney), holes, course, top);
        }
        else
        {
            stack.AddExtrusion(Outline(document, chimney), holes, opens, top);
        }

        // The liners, blackened by the smoke, down each flue to where it goes dark.
        var liners = New(chimney, MeshKind.Wall, Sooted, "Flue Liner");
        var soot = New(chimney, MeshKind.Wall, Soot, "Flue");
        var linerFoot = Math.Max(opens, top - LinerDepth);
        foreach (var flue in flues)
        {
            var outside = masonry ? Rectangle(chimney, liner, liner, flue) : Circle(flue, liner / 2);
            var inside = masonry ? Rectangle(chimney, liner - 2 * LinerWall, liner - 2 * LinerWall, flue) : Circle(flue, liner / 2 - LinerWall);
            liners.AddExtrusion(outside, new[] { inside }, linerFoot, top + CapThickness);
            soot.AddExtrusion(inside, linerFoot, linerFoot + 5);
        }

        meshes.AddRange(new[] { liners, soot });

        // The cap, the flues through it.
        var cap = New(chimney, MeshKind.Wall, concrete?.SurfaceColour ?? Cap, concrete?.Name ?? "Chimney Cap");
        cap.AddExtrusion(Rectangle(chimney, chimney.Width + 2 * CapOversail, chimney.Depth + 2 * CapOversail), holes, top, top + CapThickness);
        meshes.Add(cap);

        var capTop = top + CapThickness;
        if (masonry) meshes.AddRange(Pots(chimney, flues, liner, capTop));
        else meshes.AddRange(flues.Select(flue => Cowl(chimney, flue, liner / 2 - LinerWall + 15, capTop)));

        meshes.AddRange(Flashing(document, chimney, margin => Grown(document, chimney, margin)));
        return meshes;
    }

    /// <summary>Hollow clay pots in their flaunching, blackened inside a hand's breadth down from the rim.</summary>
    private static IEnumerable<Mesh3D> Pots(Chimney chimney, IReadOnlyList<Point2D> flues, double liner, double capTop)
    {
        var potOuter = Math.Min(PotDiameter / 2, liner / 2 + 10);
        var flaunching = New(chimney, MeshKind.Wall, Mortar, "Flaunching");
        var pots = New(chimney, MeshKind.Wall, Pot, "Chimney Pot");
        var insides = New(chimney, MeshKind.Wall, Sooted, "Flue");

        foreach (var flue in flues)
        {
            flaunching.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
            {
                (Circle(flue, potOuter + FlaunchingSpread), new[] { Circle(flue, potOuter) }, capTop),
                (Circle(flue, potOuter + 8), new[] { Circle(flue, potOuter) }, capTop + FlaunchingRise)
            });

            var bore = potOuter - LinerWall;
            var neck = capTop + PotHeight - 50;
            pots.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
            {
                (Circle(flue, potOuter), new[] { Circle(flue, bore) }, capTop),
                (Circle(flue, potOuter * 0.88), new[] { Circle(flue, bore * 0.86) }, neck),
                (Circle(flue, potOuter * 0.88 + 14), new[] { Circle(flue, bore * 0.86) }, neck + 15),
                (Circle(flue, potOuter * 0.88 + 14), new[] { Circle(flue, bore * 0.86) }, capTop + PotHeight)
            });
            insides.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
            {
                (Circle(flue, bore - 0.5), new[] { Circle(flue, bore - 3) }, capTop),
                (Circle(flue, bore * 0.86 - 0.5), new[] { Circle(flue, bore * 0.86 - 3) }, neck - 40)
            });
        }

        return new[] { flaunching, pots, insides };
    }

    /// <summary>A steel terminal on a flue: a short open tube with a cone hat over it on legs.</summary>
    private static Mesh3D Cowl(Chimney chimney, Point2D flue, double radius, double from)
    {
        var mesh = New(chimney, MeshKind.Sweep, Stainless, "Terminal");
        mesh.AddExtrusion(Circle(flue, radius), new[] { Circle(flue, radius - 2) }, from, from + 250);
        RainCap(mesh, flue, radius, from + 250);
        return mesh;
    }

    /// <summary>A cone hat held over a pipe's open top on three legs, to keep the rain out.</summary>
    private static void RainCap(Mesh3D mesh, Point2D centre, double radius, double top)
    {
        for (var i = 0; i < 3; i++)
        {
            var angle = 2 * Math.PI * i / 3 + Math.PI / 6;
            var at = centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (radius + 4);
            mesh.AddExtrusion(Circle(at, 5, 6), top - 30, top + 140);
        }

        mesh.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
        {
            (Circle(centre, radius + 55), Array.Empty<IReadOnlyList<Point2D>>(), top + 140),
            (Circle(centre, radius + 55), Array.Empty<IReadOnlyList<Point2D>>(), top + 152),
            (Circle(centre, 12), Array.Empty<IReadOnlyList<Point2D>>(), top + 150 + radius * 0.6)
        });
    }

    /// <summary>
    /// The fireplace in a breast's front: the opening blackened inside, a hearth before it, a
    /// timber mantel beam over it, and an open fire on its grate - or a stove standing in it.
    /// </summary>
    private static IEnumerable<Mesh3D> Fireplace(BimDocument document, Chimney chimney, ChimneyBreast breast, double foot)
    {
        var front = Front(chimney);
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var width = breast.Opening[0].DistanceTo(breast.Opening[1]);
        var depth = breast.Opening[1].DistanceTo(breast.Opening[2]) - 2;
        var face = new Point2D(breast.Opening.Average(p => p.X), breast.Opening.Average(p => p.Y)) + front * (depth / 2 - 1);
        var height = breast.OpeningHeight;

        // Blackened inside: its back, its sides and its throat.
        var soot = New(chimney, MeshKind.Wall, Soot, "Firebox");
        soot.AddExtrusion(Rectangle(chimney, width, 8, face - front * (depth - 4)), foot, foot + height);
        foreach (var side in new[] { -1, 1 })
            soot.AddExtrusion(Rectangle(chimney, 8, depth - 8, face - front * (depth / 2 + 4) + along * (side * (width / 2 - 4))), foot, foot + height);
        soot.AddExtrusion(Rectangle(chimney, width, depth, face - front * (depth / 2)), foot + height - 4, foot + height);

        var hearth = New(chimney, MeshKind.Floor, Slate, "Hearth");
        hearth.AddExtrusion(breast.Hearth, foot, foot + 50);

        var mantel = New(chimney, MeshKind.Wall, Timber, "Mantel Beam");
        mantel.AddExtrusion(Rectangle(chimney, width + 400, 160, face + front * 70), foot + height + 30, foot + height + 210);

        var meshes = new List<Mesh3D> { soot, hearth, mantel };
        var middle = face - front * (depth / 2);

        if (chimney.Fireplace == ChimneyFireplace.Stove)
        {
            // A stove on the hearth, its glass glowing, its pipe up into the throat.
            var stove = New(chimney, MeshKind.Wall, Iron, "Stove");
            stove.AddExtrusion(Rectangle(chimney, 520, 400, middle + front * 30), foot + 50, foot + 650);
            stove.AddExtrusion(Circle(middle + front * 30, 65, 16), foot + 650, foot + height);
            var glass = New(chimney, MeshKind.Wall, Fire, "Fire");
            glass.AddExtrusion(Rectangle(chimney, 330, 6, middle + front * 233), foot + 260, foot + 530);
            meshes.AddRange(new[] { stove, glass });
        }
        else
        {
            // An open fire: logs on a grate, glowing.
            var grate = New(chimney, MeshKind.Wall, Iron, "Fire Grate");
            grate.AddExtrusion(Rectangle(chimney, 440, 240, middle), foot + 50, foot + 90);
            var fire = New(chimney, MeshKind.Wall, Fire, "Fire");
            fire.AddExtrusion(Rectangle(chimney, 380, 190, middle), foot + 90, foot + 120);
            foreach (var shift in new[] { -60.0, 60.0 })
                Tube(fire, middle + front * shift - along * 180, middle + front * shift + along * 180, foot + 150, 45);
            meshes.AddRange(new[] { grate, fire });
        }

        return meshes;
    }

    /// <summary>A round bar lying level between two points, at a height.</summary>
    private static void Tube(Mesh3D mesh, Point2D from, Point2D to, double height, double radius) =>
        Strut(mesh, new Vector3(from.X, from.Y, height), new Vector3(to.X, to.Y, height), radius);

    /// <summary>A round bar between two points in space - a log, a guy wire, a leg.</summary>
    private static void Strut(Mesh3D mesh, Vector3 from, Vector3 to, double radius)
    {
        var along = (to - from).Normalised;
        var side = Math.Abs(along.Z) < 0.9 ? along.Cross(new Vector3(0, 0, 1)).Normalised : new Vector3(1, 0, 0);
        var section = Enumerable.Range(0, 10).Select(i => 2 * Math.PI * i / 10)
            .Select(a => new Point2D(radius * Math.Cos(a), radius * Math.Sin(a))).ToList();
        RoofEdgeSweeps.AddRun(mesh, new RoofEdgeRun(new[] { new RoofEdgeSegment(from, to, side, along.Cross(side).Normalised, section) }, false));
    }

    /// <summary>
    /// A factory-built metal flue: twin-wall, air-cooled, a gas vent or a concentric flue - from a
    /// stove at its foot or straight up from it, its joints banded, a storm collar where it comes
    /// through the roof over the flashing, and its own terminal.
    /// </summary>
    private static List<Mesh3D> Pipe(BimDocument document, Chimney chimney, ChimneyType type, double foot, double top)
    {
        var meshes = new List<Mesh3D>();
        var at = chimney.Location;
        var radius = chimney.Width / 2;
        var bore = Math.Min(type.FlueDiameter / 2, radius - 3);
        var colour = type.Construction is ChimneyConstruction.TypeBVent or ChimneyConstruction.ConcentricFlue ? Galvanised : Stainless;
        var start = foot;

        if (chimney.Fireplace == ChimneyFireplace.Stove)
        {
            var stove = New(chimney, MeshKind.Wall, Iron, "Stove");
            stove.AddExtrusion(Rectangle(chimney, 520, 420, at), foot + 12, foot + 650);
            var plate = New(chimney, MeshKind.Floor, Slate, "Hearth");
            plate.AddExtrusion(Rectangle(chimney, StovePlate, StovePlate, at + Front(chimney) * StovePlateForward), foot, foot + 12);
            var glass = New(chimney, MeshKind.Wall, Fire, "Fire");
            glass.AddExtrusion(Rectangle(chimney, 330, 6, at + Front(chimney) * 213), foot + 260, foot + 530);
            meshes.AddRange(new[] { stove, plate, glass });
            start = foot + 650;
        }

        var pipe = New(chimney, MeshKind.Sweep, colour, EnumText.Humanise(type.Construction));
        if (top - start > 50) pipe.AddExtrusion(Circle(at, radius), new[] { Circle(at, bore) }, start, top);

        // Locking bands at each joint of a twin-wall or air-cooled flue.
        if (type.Construction is ChimneyConstruction.TwinWallSteel or ChimneyConstruction.AirCooledMetal)
            for (var z = start + 1000; z < top - 100; z += 1000)
                pipe.AddExtrusion(Circle(at, radius + 3), new[] { Circle(at, radius) }, z, z + 40);

        // The storm collar over the flashing, where it comes through the roof.
        if (HighestContact(document, chimney) is { } contact && contact < top - 200)
            pipe.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
            {
                (Circle(at, radius + 45), new[] { Circle(at, radius) }, contact + 40),
                (Circle(at, radius + 4), new[] { Circle(at, radius) }, contact + 120)
            });

        // The terminal.
        switch (type.Construction)
        {
            case ChimneyConstruction.TypeBVent:
                pipe.AddExtrusion(Circle(at, radius + 25), new[] { Circle(at, bore) }, top, top + 90);
                pipe.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
                {
                    (Circle(at, radius + 40), Array.Empty<IReadOnlyList<Point2D>>(), top + 90),
                    (Circle(at, 10), Array.Empty<IReadOnlyList<Point2D>>(), top + 170)
                });
                break;

            case ChimneyConstruction.ConcentricFlue:
                // The exhaust core standing out of the air intake, capped.
                pipe.AddExtrusion(Circle(at, bore), new[] { Circle(at, bore - 3) }, top, top + 180);
                pipe.AddExtrusion(Circle(at, bore + 25), top + 180, top + 195);
                break;

            default:
                RainCap(pipe, at, radius, top);
                break;
        }

        meshes.Add(pipe);
        meshes.AddRange(Flashing(document, chimney, margin => Circle(at, radius + margin)));
        return meshes;
    }

    /// <summary>
    /// A steel stack: on a concrete plinth, its shell open at the top with a stiffening band, a
    /// platform with a rail near the top - and, guyed, three wires down to anchors in the ground.
    /// </summary>
    private static List<Mesh3D> SteelStack(BimDocument document, Chimney chimney, ChimneyType type, double foot, double top)
    {
        var at = chimney.Location;
        var radius = chimney.Width / 2;
        var concrete = Named(document, "Concrete, Cast");

        var plinth = New(chimney, MeshKind.Wall, concrete?.SurfaceColour ?? Cap, concrete?.Name ?? "Plinth");
        plinth.AddExtrusion(Rectangle(chimney, chimney.Width * 1.6, chimney.Width * 1.6), foot, foot + 300);

        var shell = New(chimney, MeshKind.Wall, Steel, "Steel Stack");
        shell.AddExtrusion(Circle(at, radius + 60), new[] { Circle(at, radius) }, foot + 300, foot + 330);
        shell.AddExtrusion(Circle(at, radius), new[] { Circle(at, radius - 10) }, foot + 300, top);
        shell.AddExtrusion(Circle(at, radius + 20), new[] { Circle(at, radius) }, top - 250, top);

        var inside = New(chimney, MeshKind.Wall, Soot, "Flue");
        inside.AddExtrusion(Circle(at, radius - 10.5), new[] { Circle(at, radius - 13) }, top - 1500, top - 5);

        var platform = New(chimney, MeshKind.Wall, Grating, "Platform");
        var level = top - 1800;
        platform.AddExtrusion(Circle(at, radius + 700), new[] { Circle(at, radius) }, level, level + 50);
        platform.AddExtrusion(Circle(at, radius + 700), new[] { Circle(at, radius + 690) }, level + 1050, level + 1100);
        for (var i = 0; i < 12; i++)
        {
            var angle = 2 * Math.PI * i / 12;
            platform.AddExtrusion(Circle(at + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (radius + 695), 12, 6), level + 50, level + 1050);
        }

        var meshes = new List<Mesh3D> { plinth, shell, inside, platform };

        if (type.Construction == ChimneyConstruction.GuyedStack)
        {
            var guys = New(chimney, MeshKind.Sweep, Galvanised, "Guy Wires");
            var from = foot + (top - foot) * 0.7;
            foreach (var anchor in Anchors(document, chimney))
            {
                var collar = at + (anchor - at).NormalisedOrDefault(Vector2D.UnitX) * radius;
                Strut(guys, new Vector3(collar.X, collar.Y, from), new Vector3(anchor.X, anchor.Y, foot + 200), 10);
                plinth.AddExtrusion(Rectangle(chimney, 600, 600, anchor), foot, foot + 200);
            }

            meshes.Add(guys);
        }

        return meshes;
    }

    /// <summary>
    /// An industrial stack: a concrete windshield, open inside, its roof slab carrying several
    /// steel flues that stand out of its top; a red band near the top for aircraft.
    /// </summary>
    private static List<Mesh3D> Industrial(BimDocument document, Chimney chimney, ChimneyType type, double foot, double top)
    {
        var at = chimney.Location;
        var radius = chimney.Width / 2;
        var concrete = Named(document, "Concrete, Cast");
        var flues = Flues(document, chimney);
        var flueRadius = type.FlueDiameter / 2;

        var shield = New(chimney, MeshKind.Wall, concrete?.SurfaceColour ?? Cap, concrete?.Name ?? "Windshield");
        shield.AddExtrusion(Circle(at, radius, 32), new[] { Circle(at, radius - 300, 32) }, foot, top);
        shield.AddExtrusion(Circle(at, radius - 300, 32), flues.Select(flue => Circle(flue, flueRadius + 50)).ToList(), top - 600, top - 400);

        var band = New(chimney, MeshKind.Wall, Warning, "Aviation Marking");
        band.AddExtrusion(Circle(at, radius + 5, 32), new[] { Circle(at, radius, 32) }, top - 3000, top - 1500);

        var steel = New(chimney, MeshKind.Wall, Steel, "Steel Flue");
        var soot = New(chimney, MeshKind.Wall, Soot, "Flue");
        foreach (var flue in flues)
        {
            steel.AddExtrusion(Circle(flue, flueRadius), new[] { Circle(flue, flueRadius - 12) }, foot + 3000, top + 1500);
            soot.AddExtrusion(Circle(flue, flueRadius - 12.5), new[] { Circle(flue, flueRadius - 15) }, top, top + 1495);
        }

        return new List<Mesh3D> { shield, band, steel, soot };
    }

    /// <summary>
    /// A solar chimney: three solid walls - the back one a dark absorber - and a glazed front the
    /// sun heats the air behind; open between corner posts at the top to let the warm air out,
    /// under a cap.
    /// </summary>
    private static List<Mesh3D> Solar(BimDocument document, Chimney chimney, double foot, double top)
    {
        const double wall = 150, vent = 600;
        var front = Front(chimney);
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var (width, depth) = (chimney.Width, chimney.Depth);
        var at = chimney.Location;
        var concrete = Named(document, "Concrete, Cast");
        var glassFrom = HighestContact(document, chimney) ?? foot;

        var walls = New(chimney, MeshKind.Wall, concrete?.SurfaceColour ?? Cap, concrete?.Name ?? "Walls");
        var absorber = New(chimney, MeshKind.Wall, Absorber, "Absorber");
        absorber.AddExtrusion(Rectangle(chimney, width, wall, at - front * (depth / 2 - wall / 2)), foot, top - vent);
        foreach (var side in new[] { -1, 1 })
            walls.AddExtrusion(Rectangle(chimney, wall, depth - 2 * wall, at + along * (side * (width / 2 - wall / 2))), foot, top - vent);
        walls.AddExtrusion(Rectangle(chimney, width, wall, at + front * (depth / 2 - wall / 2)), foot, glassFrom);

        var glass = new Mesh3D(chimney.Id, chimney.LevelId, MeshKind.Glazing, Glass, "Glazing") { Opacity = 0.4 };
        glass.AddExtrusion(Rectangle(chimney, width - 2 * wall, 20, at + front * (depth / 2 - 30)), glassFrom, top - vent);

        // Open at the top between its corner posts, under the cap.
        foreach (var x in new[] { -1, 1 })
        foreach (var y in new[] { -1, 1 })
            walls.AddExtrusion(Rectangle(chimney, wall, wall, at + along * (x * (width / 2 - wall / 2)) + front * (y * (depth / 2 - wall / 2))), top - vent, top);
        walls.AddExtrusion(Rectangle(chimney, width + 200, depth + 200), top, top + 100);

        var meshes = new List<Mesh3D> { walls, absorber, glass };
        meshes.AddRange(Flashing(document, chimney, margin => Grown(document, chimney, margin)));
        return meshes;
    }

    /// <summary>
    /// A framed chase: a clad stud box round a metal flue, capped with a metal chase cover the
    /// flue comes up through - with its storm collar and rain cap.
    /// </summary>
    private static List<Mesh3D> Chase(BimDocument document, Chimney chimney, ChimneyType type, double foot, double top)
    {
        var at = chimney.Location;
        var chase = New(chimney, MeshKind.Wall, Cladding, "Chase Cladding");
        chase.AddExtrusion(Outline(document, chimney), new[] { Rectangle(chimney, chimney.Width - 240, chimney.Depth - 240) }, foot, top);

        var pipeRadius = type.FlueDiameter / 2 + 25;
        var cover = New(chimney, MeshKind.Sweep, Galvanised, "Chase Cover");
        cover.AddExtrusion(Rectangle(chimney, chimney.Width + 80, chimney.Depth + 80), new[] { Circle(at, pipeRadius + 5) }, top, top + 8);

        var pipe = New(chimney, MeshKind.Sweep, Stainless, "Twin Wall Steel");
        pipe.AddExtrusion(Circle(at, pipeRadius), new[] { Circle(at, type.FlueDiameter / 2) }, top - 300, top + 600);
        pipe.AddLoft(new (IReadOnlyList<Point2D>, IReadOnlyList<IReadOnlyList<Point2D>>, double)[]
        {
            (Circle(at, pipeRadius + 40), new[] { Circle(at, pipeRadius) }, top + 10),
            (Circle(at, pipeRadius + 4), new[] { Circle(at, pipeRadius) }, top + 80)
        });
        RainCap(pipe, at, pipeRadius, top + 600);

        var meshes = new List<Mesh3D> { chase, cover, pipe };
        meshes.AddRange(Flashing(document, chimney, margin => Grown(document, chimney, margin)));
        return meshes;
    }

    /// <summary>
    /// The lead flashing where a chimney comes through each roof: a collar let into it, up its
    /// sides, and an apron dressed out over the roof round it.
    /// </summary>
    private static IEnumerable<Mesh3D> Flashing(BimDocument document, Chimney chimney, Func<double, IReadOnlyList<Point2D>> grown)
    {
        var lead = New(chimney, MeshKind.Sweep, Lead, "Lead Flashing");
        foreach (var roof in Roofs(document, chimney))
        {
            var collar = PolygonBoolean.Combine(grown(6), grown(0), BooleanOperation.Difference);
            var apron = PolygonBoolean.Combine(grown(FlashingApron), grown(6), BooleanOperation.Difference);

            foreach (var (regions, low, high) in new[] { (collar, -2.0, FlashingUp), (apron, 1.0, 5.0) })
            foreach (var region in regions)
            foreach (var onRoof in PolygonBoolean.Combine(Shafts.Loops(region), new[] { roof.Boundary }, BooleanOperation.Intersection))
            foreach (var piece in PolygonBoolean.WithoutHoles(onRoof))
                lead.AddExtrusion(piece, point => roof.TopAt(document, point) + low, point => roof.TopAt(document, point) + high);
        }

        if (!lead.IsEmpty) yield return lead;
    }
}
