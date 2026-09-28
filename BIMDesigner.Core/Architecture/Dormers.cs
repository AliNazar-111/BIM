using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>The roof a dormer is given.</summary>
public enum DormerShape
{
    /// <summary>Two slopes to a ridge running up the main roof, a gable end at the front.</summary>
    Gable,

    /// <summary>One slope, falling to the front at a shallower pitch than the main roof.</summary>
    Shed,

    /// <summary>Slopes on the front and both sides, meeting at a short ridge.</summary>
    Hip
}

/// <summary>What a dormer is to be: its shape and size.</summary>
public sealed record DormerSettings(DormerShape Shape, double Width, double Height, double Slope, double Overhang)
{
    public static DormerSettings Default { get; } = new(DormerShape.Gable, 2400, 1400, 35, 200);
}

/// <summary>
/// A whole dormer in one go, clicked on a roof's slope: what Revit takes five steps to build.
///
/// A front wall across the slope where it was clicked and two side walls running up it, all
/// standing on the roof (their bases follow its slope) and carrying the dormer's roof (their
/// tops follow its underside); the dormer's roof picked off the walls, so it follows them; that
/// roof joined into the main roof, so the two meet in valleys; and the main roof opened under
/// it, between the walls. Every part is an ordinary element afterwards - a wall, a roof, a join,
/// an opening - to be changed like any other.
/// </summary>
public static class Dormers
{
    /// <summary>The least a dormer's front wall stands above the roof and still makes a dormer.</summary>
    public const double MinimumHeight = 500;

    /// <summary>
    /// How high a dormer's front wall can be at a point on a roof: low enough that its roof
    /// still runs back into the main roof below the ridge. Null where the roof does not slope.
    /// </summary>
    public static double? HeightThatFits(BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness)
    {
        if (Frame(document, main, at) is not var (uphill, across, _)) return null;

        var thickness = document.FindType<SlabType>(main.TypeId)?.Thickness ?? 0;
        var pitch = Math.Tan(Math.Clamp(settings.Slope, 1, 80) * Math.PI / 180);
        var halfSpan = settings.Width / 2 + wallThickness / 2 + settings.Overhang;
        var front = main.TopAt(document, at);
        var fits = double.MaxValue;

        // Up the slope along lines across the dormer's whole width - its middle and its sides,
        // and between - since a roof narrowing to a hip is lower at the sides than the middle,
        // and the dormer must be buried in it all the way across.
        foreach (var share in new[] { -1.0, -0.5, 0, 0.5, 1.0 })
        {
            var offset = share * halfSpan;
            var start = at + across * offset;
            if (!main.Contains(start)) return null;

            // The highest the main roof's top gets going up the slope along this line, and how far up.
            var highest = main.TopAt(document, start);
            var peak = 0.0;
            for (var along = 100.0; along < 50000; along += 100)
            {
                var point = start + uphill * along;
                if (!main.Contains(point)) break;

                var top = main.TopAt(document, point);
                if (top <= highest) continue;

                highest = top;
                peak = along;
            }

            // How far the dormer's own roof climbs above its eaves along this line before it must
            // be under the main roof: to its ridge over the middle, less toward the sides - or for
            // a shed, rising all the way back, as far as the main roof's peak, where they must meet.
            var climb = settings.Shape == DormerShape.Shed
                ? (peak + wallThickness / 2 + settings.Overhang) * pitch
                : (halfSpan - Math.Abs(offset)) * pitch;

            fits = Math.Min(fits, highest - front - climb - thickness / Math.Cos(Math.Atan(pitch)) - 150);
        }

        return fits;
    }

    /// <summary>
    /// What would fit at a point when the dormer asked for does not: a shed - no ridge of its
    /// own to rise above the main one - at a gentle pitch, as high as fits. Null when not even
    /// that does, or where the roof does not slope.
    /// </summary>
    public static DormerSettings? InsteadAt(BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness)
    {
        // The gentler the slope, the lower the shed's roof stays as it runs back to meet the
        // main roof, and the narrower it is, the less a roof narrowing to a hip is lower at its
        // sides - so the widest, then steepest, of these that fits.
        foreach (var width in new[] { settings.Width, 1800.0, 1200.0 }.Where(width => width <= settings.Width).Distinct())
        foreach (var slope in new[] { 15.0, 10.0, 5.0 })
        {
            var shed = settings with { Shape = DormerShape.Shed, Slope = Math.Min(settings.Slope, slope), Width = width };
            if (HeightThatFits(document, main, at, shed, wallThickness) is { } fits && fits >= MinimumHeight)
                return shed with { Height = Math.Floor(Math.Min(settings.Height, fits) / 10) * 10 };
        }

        return null;
    }

    /// <summary>
    /// The dormer an element is part of - its roof, or one of the walls carrying it - or null.
    /// One made by the Dormer tool knows its walls; one built by hand, as Revit builds them, is
    /// a roof joined into another with walls standing on that one and carrying it.
    /// </summary>
    public static Roof? Of(BimDocument document, Element? element)
    {
        switch (element)
        {
            case Roof roof:
                return WallsOf(document, roof).Any() ? roof : null;

            case Wall wall:
                var roofs = document.Elements.OfType<Roof>().ToList();
                return roofs.FirstOrDefault(roof => roof.Dormer is not null && roof.DormerWalls.Contains(wall.Id))
                       ?? (roofs.FirstOrDefault(roof => roof.Id == wall.TopAttachedTo) is { JoinedTo: { } main } carried &&
                           wall.BaseAttachedTo == main
                           ? carried
                           : null);

            default:
                return null;
        }
    }

    /// <summary>What a dormer is made of that is still there: its roof, then its walls.</summary>
    public static IReadOnlyList<Element> Parts(BimDocument document, Roof dormer) =>
        WallsOf(document, dormer).Cast<Element>().Prepend(dormer).ToList();

    private static IEnumerable<Wall> WallsOf(BimDocument document, Roof roof) => roof.Dormer is not null
        ? document.Walls.Where(wall => roof.DormerWalls.Contains(wall.Id))
        : roof.JoinedTo is { } main
            ? document.Walls.Where(wall => wall.TopAttachedTo == roof.Id && wall.BaseAttachedTo == main)
            : Enumerable.Empty<Wall>();

    /// <summary>"Gable dormer, 2400 x 1400" - what it is, for the status bar.</summary>
    public static string Describe(Roof dormer) => dormer.Dormer is { } made
        ? $"{made.Shape} dormer, {Units.FormatLength(made.Width)} wide and {Units.FormatLength(made.Height)} high"
        : "Dormer";

    /// <summary>The way up the roof's slope at a point, square across it, and how steep it is - or null where it is flat.</summary>
    private static (Vector2D Uphill, Vector2D Across, double Rise)? Frame(BimDocument document, Roof main, Point2D at)
    {
        if (!main.Contains(at) || main.Surface(document).FaceAt(at) is not { } face) return null;

        var rise = face.Plane.Rise;
        if (rise < 0.05) return null;

        var uphill = new Vector2D(face.Plane.A / rise, face.Plane.B / rise);
        return (uphill, uphill.PerpendicularLeft(), rise);
    }

    /// <summary>
    /// Builds a dormer on a roof at a point - the middle of its front wall - and returns what was
    /// done, already done, as one step to undo; or null, with the reason, leaving the model as
    /// it was. The height is brought down to what fits where it would not.
    /// </summary>
    public static IUndoableCommand? Add(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, Guid wallTypeId,
        out string? problem, out DormerSettings used)
    {
        problem = null;
        used = settings;

        if (main.IsExtrusion)
        {
            problem = "Dormers go on roofs drawn by footprint. This one is a roof by extrusion.";
            return null;
        }

        if (Frame(document, main, at) is not var (uphill, across, rise))
        {
            problem = "Click on a sloping part of the roof - a dormer rises out of a slope, not a flat.";
            return null;
        }

        if (document.FindType<WallType>(wallTypeId) is not { } wallType)
        {
            problem = "There is no wall type to build the dormer's walls from.";
            return null;
        }

        var thickness = wallType.Structure.TotalWidth;

        // No higher than fits under the ridge; a shed no steeper than the roof it sits in, or it
        // would never meet it.
        var fits = HeightThatFits(document, main, at, settings, thickness) ?? 0;
        if (fits < MinimumHeight)
        {
            problem = $"The roof is too low here for a dormer: even {Units.FormatLength(MinimumHeight)} high, its roof would come out " +
                      $"above the ridge, {Units.FormatLength(MinimumHeight - fits)} short of fitting. Click nearer the eave, make it " +
                      "narrower or its slope shallower, try a Shed dormer - or give the main roof a steeper slope.";
            return null;
        }

        var mainPitch = Math.Atan(rise) * 180 / Math.PI;
        var size = settings with
        {
            Height = Math.Min(settings.Height, fits),
            Slope = settings.Shape == DormerShape.Shed ? Math.Min(settings.Slope, mainPitch - 5) : settings.Slope
        };

        used = size;
        if (size.Slope < 2)
        {
            problem = "The roof is too shallow for a shed dormer to fall to the front below it.";
            return null;
        }

        var level = document.FindLevel(main.LevelId)?.Elevation ?? 0;
        var front = main.TopAt(document, at);
        var eave = front + size.Height;

        // The side walls run up the slope until the main roof has risen to meet the dormer's
        // roof - at its eaves, or for a shed, rising too, as far back as it takes the main roof
        // to catch it up - and a little past, which the walls' outlines end at anyway.
        var gaining = size.Shape == DormerShape.Shed ? rise - Math.Tan(size.Slope * Math.PI / 180) : rise;
        var run = size.Height / Math.Max(gaining, 0.02) + thickness + 300;

        var left = at - across * (size.Width / 2);
        var right = at + across * (size.Width / 2);

        // Each drawn so its outside - its left - faces out of the dormer.
        var walls = new[]
        {
            new Wall { Start = left + uphill * run, End = left },
            new Wall { Start = left, End = right },
            new Wall { Start = right, End = right + uphill * run }
        };

        var dormer = new Roof
        {
            TypeId = main.TypeId,
            LevelId = main.LevelId,
            HeightOffset = eave - level,
            Bearing = RoofBearing.Truss
        };

        foreach (var wall in walls)
        {
            wall.TypeId = wallType.Id;
            wall.LevelId = main.LevelId;
            wall.UnconnectedHeight = eave - level + 500;
            wall.BaseAttachedTo = main.Id;
            wall.TopAttachedTo = dormer.Id;
        }

        // The dormer's roof, picked off the outside of its walls with the overhang, and closed
        // at the back with a line to be carried back into the main roof.
        var sides = walls
            .Select(wall => RoofSketch.LineOnWall(document, wall, !wall.Flipped, size.Overhang, false) is var (a, b)
                ? new RoofSketchLine(a, b, new RoofEdge
                {
                    WallId = wall.Id, OnLeftOfWall = !wall.Flipped, Overhang = size.Overhang, SlopeDegrees = size.Slope
                })
                : null)
            .ToList();

        if (sides.Any(line => line is null))
        {
            problem = "The dormer's walls could not carry a roof.";
            return null;
        }

        var (leftSide, frontSide, rightSide) = (sides[0]!, sides[1]!, sides[2]!);
        leftSide.Edge.DefinesSlope = settings.Shape is DormerShape.Gable or DormerShape.Hip;
        rightSide.Edge.DefinesSlope = settings.Shape is DormerShape.Gable or DormerShape.Hip;
        frontSide.Edge.DefinesSlope = settings.Shape is DormerShape.Shed or DormerShape.Hip;

        var back = new RoofSketchLine(leftSide.Start, rightSide.End, new RoofEdge { DefinesSlope = false });
        var sketch = new List<RoofSketchLine>();
        foreach (var line in new[] { leftSide, frontSide, rightSide, back })
        {
            sketch.Add(line);
            RoofSketch.CloseCorners(sketch, line, 2 * (thickness + size.Overhang) + 200);
        }

        var outline = RoofSketch.Check(sketch);
        if (!outline.IsValid)
        {
            problem = "The dormer's roof would not close: " + outline.Problem;
            return null;
        }

        dormer.SetBoundary(outline.Boundary);
        dormer.SetEdges(outline.Edges);

        // One dormer, picked as one: the roof knows the walls that are part of it.
        dormer.Dormer = size;
        dormer.DormerWalls.AddRange(walls.Select(wall => wall.Id));

        // Made, then joined and opened - the join works out how far back to carry the roof from
        // the walls and roof being there.
        var done = new List<IUndoableCommand>();
        foreach (var element in walls.Cast<Element>().Append(dormer))
        {
            var add = new AddElementCommand(document, element, "Add Dormer");
            add.Redo();
            done.Add(add);
        }

        var backEdge = Enumerable.Range(0, dormer.Boundary.Count)
            .OrderByDescending(i => (dormer.Boundary[i].MidpointTo(dormer.Boundary[(i + 1) % dormer.Boundary.Count]) - at).Dot(uphill))
            .First();

        if (RoofJoin.Join(document, dormer, backEdge, main, out var joinProblem) is not { } join)
        {
            for (var i = done.Count - 1; i >= 0; i--) done[i].Undo();
            problem = "The roof is too small here for a dormer this size: its roof would come out above the main roof " +
                      "instead of running back into it. Make it narrower or lower, click nearer the eave, or give the roof a steeper slope.";
            return null;
        }

        join.Redo();
        done.Add(join);

        var opening = new SetDormerOpeningCommand(main, dormer.Id, open: true);
        opening.Redo();
        done.Add(opening);

        return new CompositeCommand("Add Dormer", done);
    }
}
