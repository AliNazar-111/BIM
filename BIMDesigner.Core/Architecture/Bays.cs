using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>A bay's plan: splayed sides, as on a Victorian terrace, or square ones.</summary>
public enum BayShape
{
    /// <summary>No bay: the Window tool puts in a window.</summary>
    None,

    /// <summary>A canted bay: its sides splayed back to the wall at 45 degrees, a front between them.</summary>
    Canted,

    /// <summary>A box bay: its sides square to the wall.</summary>
    Box
}

/// <summary>What a bay is roofed with: a little hipped roof against the wall, a flat one, or none - one carried on up to the main roof's gable.</summary>
public enum BayRoof
{
    Hipped,
    Flat,
    None
}

/// <summary>What a bay is to be: its plan, its width and projection at the wall's face, how many storeys it rises, its roof and that roof's pitch and overhang.</summary>
public sealed record BaySettings(BayShape Shape, double Width, double Projection, int Storeys, BayRoof Roof, double Slope, double Overhang)
{
    public static BaySettings Default { get; } = new(BayShape.None, 2400, 750, 2, BayRoof.Hipped, 30, 150);
}

/// <summary>
/// A bay window rising one storey or more out of an outside wall, built in one go from ordinary elements (round 139): on each storey the wall
/// across the bay's width taken out, three walls of the wall's own type standing out round it - a canted bay's sides splayed at 45 degrees, a
/// box bay's square - a window in its front and, where they are long enough, in its sides; the floor of each storey carried out into it where
/// the storey has one; and over its top storey a little roof against the wall, hipped on its three faces or flat - or none, for a bay carried
/// on up under the main roof's gable. Rooms take the bay in as their walls go round it. Every part is an ordinary wall, window, floor or roof
/// afterwards, to be changed as any other.
/// </summary>
public static class Bays
{
    /// <summary>The least a bay's front is long, between its walls' centres, and the least it stands from the wall's end, mm.</summary>
    public const double ShortestFront = 600, FromEnd = 300;

    /// <summary>The brickwork left each side of a bay face's window, and the narrowest window a face is given, mm.</summary>
    public const double Reveal = 150, NarrowestWindow = 350;

    /// <summary>A bay's plan in its own terms - x along the wall from the bay's middle, y out from the wall's centre line: its walls' centre line and its outside outline.</summary>
    public static (IReadOnlyList<Point2D> Walls, IReadOnlyList<Point2D> Outline) Plan(BaySettings settings, double wallThickness, double bayWallThickness)
    {
        var (half, face, depth) = (settings.Width / 2, wallThickness / 2, settings.Projection);
        var outline = settings.Shape == BayShape.Box
            ? new[] { new Point2D(-half, face), new Point2D(-half, face + depth), new Point2D(half, face + depth), new Point2D(half, face) }
            : new[] { new Point2D(-half, face), new Point2D(-half + depth, face + depth), new Point2D(half - depth, face + depth), new Point2D(half, face) };

        // Each face moved in by half the bay wall's thickness; the sides carried back to the wall's centre line.
        var inside = new Point2D(0, face + depth / 2);
        (Point2D A, Point2D B) Shifted(Point2D a, Point2D b)
        {
            var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var normal = along.PerpendicularLeft();
            if ((inside - a).Dot(normal) < 0) normal = -normal;
            return (a + normal * (bayWallThickness / 2), b + normal * (bayWallThickness / 2));
        }

        Point2D Meet((Point2D A, Point2D B) one, (Point2D A, Point2D B) other)
        {
            var (d1, d2) = (one.B - one.A, other.B - other.A);
            var cross = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(cross) < 1e-9) return one.B;
            var t = ((other.A.X - one.A.X) * d2.Y - (other.A.Y - one.A.Y) * d2.X) / cross;
            return one.A + d1 * t;
        }

        var (left, front, right) = (Shifted(outline[0], outline[1]), Shifted(outline[1], outline[2]), Shifted(outline[2], outline[3]));
        Point2D Back((Point2D A, Point2D B) side)
        {
            var d = side.B - side.A;
            if (Math.Abs(d.Y) < 1e-9) return side.A;
            var t = (0 - side.A.Y) / d.Y;
            return side.A + d * t;
        }

        var walls = new[] { Back(left), Meet(left, front), Meet(front, right), Back(right) };
        return (walls, outline);
    }

    /// <summary>
    /// Builds a bay on an outside wall at a point - its middle - rising so many storeys, and returns what was done, already done, as one step to
    /// undo; or null, with the reason, leaving the model as it was.
    /// </summary>
    public static IUndoableCommand? Add(BimDocument document, Wall clicked, Point2D at, BaySettings settings, Guid windowTypeId, out string? problem)
    {
        problem = null;
        if (settings.Shape == BayShape.None) { problem = "Choose a canted or a box bay first."; return null; }
        if (clicked.IsCurved || document.GetWallType(clicked) is not { } type || document.FindLevel(clicked.LevelId) is not { } first)
        {
            problem = "A bay stands out of a straight wall: click on one.";
            return null;
        }

        if (type.Function != WallFunction.Exterior)
        {
            problem = "A bay stands out of an outside wall: click on one.";
            return null;
        }

        var thickness = type.Structure.TotalWidth;
        var u = (clicked.End - clicked.Start).NormalisedOrDefault(Vector2D.UnitX);
        var along = Math.Clamp((at - clicked.Start).Dot(u), 0, clicked.Length);
        var centre = clicked.Start + u * along;
        var half = settings.Width / 2;
        if (along - half < FromEnd + thickness || along + half > clicked.Length - FromEnd - thickness)
        {
            problem = $"The bay is {settings.Width / 1000:0.00} m wide and too near the wall's end here: move it along, or make it narrower.";
            return null;
        }

        var (walls, outline) = Plan(settings, thickness, thickness);
        if (walls[1].DistanceTo(walls[2]) < ShortestFront - 0.5)
        {
            problem = $"A canted bay {settings.Width / 1000:0.00} m wide standing out {settings.Projection / 1000:0.00} m leaves too short a front: make it wider, shallower, or a box bay.";
            return null;
        }

        // Out: the side with no room on it.
        var normal = u.PerpendicularLeft();
        var reach = thickness / 2 + 300;
        var (left, right) = (RoomsOf.Containing(document, clicked.LevelId, centre + normal * reach), RoomsOf.Containing(document, clicked.LevelId, centre - normal * reach));
        var outward = left is null && right is not null ? normal : right is null && left is not null ? -normal : (at - centre).Dot(normal) >= 0 ? normal : -normal;
        Point2D World(Point2D local) => centre + u * local.X + outward * local.Y;

        var levels = document.Levels.Where(level => level.Elevation >= first.Elevation - 1).OrderBy(level => level.Elevation).Take(Math.Max(1, settings.Storeys)).ToList();
        if (levels.Count < settings.Storeys)
        {
            problem = $"There are only {levels.Count} storeys from here up for a bay of {settings.Storeys}.";
            return null;
        }

        // The wall across the bay on each storey: on the clicked wall's line, long enough.
        var hosts = new List<(Level Level, Wall Wall)>();
        foreach (var level in levels)
        {
            var host = level.Id == clicked.LevelId ? clicked : document.Walls.FirstOrDefault(wall => wall.LevelId == level.Id && !wall.IsCurved
                && Math.Abs(u.Cross((wall.End - wall.Start).NormalisedOrDefault(u))) < 0.02 && Math.Abs(u.Cross(centre - wall.Start)) < 50
                && (centre - wall.Start).Dot((wall.End - wall.Start).NormalisedOrDefault(u)) is var d && d - half >= 0 && d + half <= wall.Length);
            if (host is null)
            {
                problem = $"There is no outside wall on the {level.Name} over the bay for it to rise through: give the bay fewer storeys, or draw the wall above.";
                return null;
            }

            hosts.Add((level, host));
        }

        var done = new List<IUndoableCommand>();
        void Do(IUndoableCommand command)
        {
            command.Redo();
            done.Add(command);
        }

        // The window put in each face: the one chosen where it fits, else a copy of it made to the face's width - as a bay's windows are made
        // for it - kept as a type of its own, "Casement - 870 x 1200 (Bay)", and used again by the next bay of that size.
        var template = document.FindType<WindowType>(windowTypeId) is { Operation: not WindowOperation.Bay } chosen ? chosen
            : document.TypesOf<WindowType>().FirstOrDefault(each => each.Operation == WindowOperation.Casement) ?? document.TypesOf<WindowType>().FirstOrDefault(each => each.Operation != WindowOperation.Bay);
        WindowType? Fit(double length)
        {
            if (template is null) return null;
            var room = Math.Floor((length - 2 * Reveal) / 10) * 10;
            if (room < NarrowestWindow) return null;
            if (template.Width <= room) return template;
            var stem = template.Name.Split(" - ")[0];
            var name = $"{stem} - {room:0} x {template.Height:0} (Bay)";
            if (document.TypesOf<WindowType>().FirstOrDefault(each => each.Name == name) is { } made) return made;
            var copy = template.Duplicate(name);
            copy.Width = room;
            Do(new AddTypeCommand(document, copy));
            return copy;
        }

        Wall? lastFront = null;
        double top = 0;
        foreach (var (level, host) in hosts)
        {
            // The wall across the bay taken out: split at both ends of it, the middle part deleted with whatever it held.
            var hu = (host.End - host.Start).NormalisedOrDefault(u);
            var ends = new[] { World(walls[0]), World(walls[3]) }.OrderByDescending(point => (point - host.Start).Dot(hu)).ToList();
            Do(new SplitWallCommand(document, host, ends[0]));
            var split = new SplitWallCommand(document, host, ends[1]);
            Do(split);
            Do(new DeleteElementCommand(document, split.Remainder));

            // Three walls round the bay, the wall's own type and height.
            var height = host.GetTopElevation(document) - host.GetBaseElevation(document);
            top = host.GetTopElevation(document);
            var made = new List<Wall>();
            for (var i = 0; i < 3; i++)
            {
                var bayWall = new Wall
                {
                    Start = World(walls[i]), End = World(walls[i + 1]), TypeId = type.Id, LevelId = level.Id, BaseOffset = host.BaseOffset,
                    UnconnectedHeight = height
                };
                Do(new AddElementCommand(document, bayWall, "Add Bay"));
                made.Add(bayWall);
            }

            lastFront = made[1];

            // A window in the front, and in each side where it is long enough.
            for (var i = 0; i < 3; i++)
            {
                var length = made[i].Length - (i == 1 ? 0 : thickness);
                if (Fit(length) is not { } fit) continue;
                var middle = i == 1 ? made[i].Length / 2 : i == 0 ? made[i].Length - (length / 2) : length / 2;
                Do(new AddElementCommand(document, new Window { HostWallId = made[i].Id, DistanceAlongWall = middle, LevelId = level.Id, TypeId = fit.Id }, "Add Bay"));
            }

            // The storey's floor carried out into the bay, where it has one.
            var inside = World(new Point2D(0, -thickness / 2 - 500));
            if (document.All<Floor>().FirstOrDefault(floor => floor.LevelId == level.Id && floor.Boundary.Count >= 3 && floor.Contains(inside)) is { } storeyFloor)
            {
                var floor = new Floor { LevelId = level.Id, TypeId = storeyFloor.TypeId, HeightOffset = storeyFloor.HeightOffset };
                floor.SetBoundary(new[] { World(new Point2D(outline[0].X, 0)) }.Concat(outline.Select(World)).Append(World(new Point2D(outline[3].X, 0))).ToList());
                Do(new AddElementCommand(document, floor, "Add Bay"));
            }
        }

        // The roof over its top storey: hipped on its three faces against the wall, or flat.
        if (settings.Roof != BayRoof.None && lastFront is not null)
        {
            var topLevel = hosts[^1].Level;
            var roofType = settings.Roof == BayRoof.Flat
                ? document.TypesOf<RoofType>().FirstOrDefault(each => each.Name.Contains("Flat", StringComparison.OrdinalIgnoreCase)) ?? document.TypesOf<RoofType>().FirstOrDefault()
                : Dormers.LightRoofType(document) ?? document.TypesOf<RoofType>().FirstOrDefault();
            if (roofType is not null)
            {
                var over = settings.Overhang;
                var eaves = settings.Shape == BayShape.Box
                    ? new[] { new Point2D(outline[0].X - over, outline[0].Y), new Point2D(outline[1].X - over, outline[1].Y + over), new Point2D(outline[2].X + over, outline[2].Y + over), new Point2D(outline[3].X + over, outline[3].Y) }
                    : new[] { new Point2D(outline[0].X - over * 1.414, outline[0].Y), new Point2D(outline[1].X - over * 0.414, outline[1].Y + over), new Point2D(outline[2].X + over * 0.414, outline[2].Y + over), new Point2D(outline[3].X + over * 1.414, outline[3].Y) };
                var roof = new Roof { LevelId = topLevel.Id, TypeId = roofType.Id, HeightOffset = top - topLevel.Elevation };
                roof.SetBoundary(eaves.Select(World).ToList());
                var hipped = settings.Roof == BayRoof.Hipped;
                roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = hipped && i < 3, SlopeDegrees = settings.Slope }));
                Do(new AddElementCommand(document, roof, "Add Bay"));
            }
        }

        return new CompositeCommand("Add Bay", done);
    }
}
