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

    /// <summary>The deepest roof build-up a dormer is given as it is; past it, a lighter one is found for it.</summary>
    public const double MaximumRoofThickness = 200;

    /// <summary>
    /// The roof build-up a new dormer is given: the main roof's own where that is light enough -
    /// a dormer roofed as the house is - or else the lightest pitched build-up the project has. A
    /// dormer is a small thing: a roof as deep as a flat roof's concrete slab sits on its walls
    /// like a lid, and every millimetre of it comes off how high the dormer can be under the ridge.
    /// </summary>
    public static Guid RoofTypeFor(BimDocument document, Roof main)
    {
        if (document.FindType<SlabType>(main.TypeId) is { Thickness: <= MaximumRoofThickness }) return main.TypeId;

        return document.TypesOf<RoofType>()
            .Where(type => type.Thickness is >= 100 and <= MaximumRoofThickness)
            .OrderBy(type => type.Thickness)
            .FirstOrDefault()?.Id ?? main.TypeId;
    }

    /// <summary>
    /// How high a dormer's front wall can be at a point on a roof: low enough that its roof
    /// still runs back into the main roof below the ridge. Null where the roof does not slope.
    /// The dormer's roof is as thick as the build-up a new one is given, unless said otherwise.
    /// </summary>
    public static double? HeightThatFits(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness, double? roofThickness = null)
    {
        if (Frame(document, main, at) is not var (uphill, across, _)) return null;

        var thickness = roofThickness ?? document.FindType<SlabType>(RoofTypeFor(document, main))?.Thickness ?? 0;
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
    public static DormerSettings? InsteadAt(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness, double? roofThickness = null)
    {
        // The gentler the slope, the lower the shed's roof stays as it runs back to meet the
        // main roof, and the narrower it is, the less a roof narrowing to a hip is lower at its
        // sides - so the widest, then steepest, of these that fits.
        foreach (var width in new[] { settings.Width, 1800.0, 1200.0 }.Where(width => width <= settings.Width).Distinct())
        foreach (var slope in new[] { 15.0, 10.0, 5.0 })
        {
            var shed = settings with { Shape = DormerShape.Shed, Slope = Math.Min(settings.Slope, slope), Width = width };
            if (HeightThatFits(document, main, at, shed, wallThickness, roofThickness) is { } fits && fits >= MinimumHeight)
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

    /// <summary>
    /// Adding copies, with the roof under each copied dormer opened for it as it was for the
    /// dormer copied - a copy of a dormer is a dormer, not a roof sitting on top of another -
    /// as one step. A copy moved off the roof it was joined to is left as it is.
    /// </summary>
    public static IUndoableCommand AddCopies(BimDocument document, IReadOnlyList<Element> copies, string name)
    {
        var add = new AddElementsCommand(document, copies, name);
        var openings = copies.OfType<Roof>()
            .Where(copy => copy.Dormer is not null)
            .Select(copy => (Copy: copy, Main: document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == copy.JoinedTo)))
            .Where(pair => pair.Main is not null && pair.Main.LevelId == pair.Copy.LevelId &&
                           pair.Main.Contains(Polygon2D.Centroid(pair.Copy.Boundary, pair.Copy.Boundary[0])))
            .Select(pair => (IUndoableCommand)new SetDormerOpeningCommand(pair.Main!, pair.Copy.Id, open: true))
            .ToList();

        return openings.Count == 0 ? add : new CompositeCommand(name, openings.Prepend(add));
    }

    /// <summary>What a dormer is made of that is still there: its roof, then its walls.</summary>
    public static IReadOnlyList<Element> Parts(BimDocument document, Roof dormer) =>
        WallsOf(document, dormer).Cast<Element>().Prepend(dormer).ToList();

    private static IEnumerable<Wall> WallsOf(BimDocument document, Roof roof) => roof.Dormer is not null
        ? document.Walls.Where(wall => roof.DormerWalls.Contains(wall.Id))
        : roof.JoinedTo is { } main
            ? document.Walls.Where(wall => wall.TopAttachedTo == roof.Id && wall.BaseAttachedTo == main)
            : Enumerable.Empty<Wall>();

    /// <summary>"Gable dormer, 2.4 m wide and 1.4 m high" - what it is, for the status bar: how high it actually is.</summary>
    public static string Describe(BimDocument document, Roof dormer)
    {
        if (dormer.Dormer is not { } made) return "Dormer";

        return $"{made.Shape} dormer, {Units.FormatLength(made.Width)} wide and {Units.FormatLength(HeightOf(document, dormer))} high";
    }

    /// <summary>
    /// How high a dormer made by the Dormer tool actually is - its eaves above the main roof at
    /// the middle of its front wall - which is less than it was asked to be where the roof has
    /// not room for that.
    /// </summary>
    public static double HeightOf(BimDocument document, Roof dormer)
    {
        var front = dormer.DormerWalls.Count == 3 ? document.Walls.FirstOrDefault(wall => wall.Id == dormer.DormerWalls[1]) : null;
        var main = document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == dormer.JoinedTo);

        return front is not null && main is not null
            ? dormer.BaseElevation(document) - main.TopAt(document, front.LocationCurve.PointAt(front.Length / 2))
            : dormer.Dormer?.Height ?? 0;
    }

    /// <summary>
    /// Puts every dormer made by the Dormer tool back on its roof as that roof now is: as high as
    /// it was asked to be, or lower where the roof no longer has room for that under its ridge,
    /// and its own roof carried back into the main roof again. After the main roof's slope
    /// changes, or the walls under it move, a dormer would otherwise stand as it was built - its
    /// roof coming out above the ridge, or stopping short of the roof it ran into. Worked out
    /// afresh from the model each time, as roofs following their walls are, so an undo puts the
    /// dormer back too. Returns whether any dormer changed.
    /// </summary>
    public static bool FollowRoofs(BimDocument document)
    {
        var changed = false;
        foreach (var dormer in document.Elements.OfType<Roof>().Where(roof => roof.Dormer is not null && roof.JoinedTo is not null).ToList())
            changed |= Follow(document, dormer);

        return changed;
    }

    private static bool Follow(BimDocument document, Roof dormer)
    {
        if (document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == dormer.JoinedTo) is not { } main) return false;
        if (dormer.DormerWalls.Count != 3 || dormer.Boundary.Count != 4 || dormer.Edges.Count != 4) return false;

        var walls = dormer.DormerWalls.Select(id => document.Walls.FirstOrDefault(wall => wall.Id == id)).ToList();
        if (walls.Any(wall => wall is null)) return false;
        var (left, front, right) = (walls[0]!, walls[1]!, walls[2]!);

        var settings = dormer.Dormer!;
        var at = front.LocationCurve.PointAt(front.Length / 2);
        var thickness = document.GetWallType(front)?.Structure.TotalWidth ?? 0;
        if (Frame(document, main, at) is not var (uphill, _, rise)) return false;

        // As high as asked, or as high as fits - lower than a new one would be allowed, if that is
        // all the roof has room for now, rather than left standing through it; a shed no steeper
        // than the roof it sits in.
        var roofThickness = document.FindType<SlabType>(dormer.TypeId)?.Thickness;
        if (HeightThatFits(document, main, at, settings, thickness, roofThickness) is not { } fits || fits <= 0) return false;
        var height = Math.Min(settings.Height, fits);
        var level = document.FindLevel(dormer.LevelId)?.Elevation ?? 0;
        var offset = main.TopAt(document, at) + height - level;
        var slope = settings.Shape == DormerShape.Shed ? Math.Min(settings.Slope, Math.Atan(rise) * 180 / Math.PI - 5) : settings.Slope;

        var before = (dormer.HeightOffset, Slopes: dormer.Edges.Select(edge => edge.SlopeDegrees).ToList(), Boundary: dormer.Boundary.ToList(),
            Heights: walls.Select(wall => wall!.UnconnectedHeight).ToList());
        dormer.HeightOffset = offset;
        if (settings.Shape == DormerShape.Shed)
        {
            var edges = dormer.Edges.Select(edge => edge.Copy()).ToList();
            foreach (var edge in edges.Where(edge => edge.DefinesSlope)) edge.SlopeDegrees = slope;
            dormer.SetEdges(edges);
        }

        // Its side walls run back as far as its eaves stand above the main roof - further the
        // higher it stands - so that a dormer grown taller, its roof having more room, does not
        // have its roof run on back over nothing where walls laid out for a lower one stop short.
        // They carry it: their tops follow its underside, so they reach it too.
        var gaining = settings.Shape == DormerShape.Shed ? rise - Math.Tan(slope * Math.PI / 180) : rise;
        var run = height / Math.Max(gaining, 0.02) + thickness + 300;
        var relaid = new List<MoveWallCommand>();
        foreach (var side in new[] { left, right }.Where(side => !side.IsCurved))
        {
            var startIsBack = (side.Start - at).Dot(uphill) > (side.End - at).Dot(uphill);
            var (back, frontEnd) = startIsBack ? (side.Start, side.End) : (side.End, side.Start);
            var reaching = frontEnd + (back - frontEnd).NormalisedOrDefault(uphill) * run;
            if (reaching.DistanceTo(back) < 1e-6) continue;

            var move = startIsBack
                ? new MoveWallCommand(side, side.Start, side.End, reaching, side.End, "Follow Roof")
                : new MoveWallCommand(side, side.Start, side.End, side.Start, reaching, "Follow Roof");
            move.KeepingOpenings(document).Redo();
            relaid.Add(move);
        }

        foreach (var wall in walls) wall!.UnconnectedHeight = offset + 500;

        // Its back edge from where the walls' back ends put it, carried back into the main roof.
        var backEdge = Enumerable.Range(0, 4)
            .OrderByDescending(i => (dormer.Boundary[i].MidpointTo(dormer.Boundary[(i + 1) % 4]) - at).Dot(uphill))
            .First();
        var overhang = dormer.Edges.Where(edge => edge.WallId is not null).Select(edge => edge.Overhang).DefaultIfEmpty(settings.Overhang).First();
        if (RoofSketch.LineOnWall(document, left, !left.Flipped, overhang, false) is not var (leftBack, _) ||
            RoofSketch.LineOnWall(document, right, !right.Flipped, overhang, false) is not var (_, rightBack))
            return Restore();

        var backMiddle = leftBack.MidpointTo(rightBack);
        var edgeMiddle = dormer.Boundary[backEdge].MidpointTo(dormer.Boundary[(backEdge + 1) % 4]);
        var based = RoofJoin.MovedEdge(dormer.Boundary, backEdge, uphill * (backMiddle - edgeMiddle).Dot(uphill));
        if (RoofJoin.CarriedBack(document, dormer, based, backEdge, main) is not { } joined) return Restore();

        var moved = joined.Where((corner, i) => corner.DistanceTo(before.Boundary[i]) > 1e-6).Any();
        if (moved) dormer.SetBoundary(joined);

        // Grown or brought down, the windows in it are made to suit it again, as when it is
        // changed in Properties: one made small for a low dormer is not left small in a tall one.
        if (Math.Abs(before.HeightOffset - offset) > 1 || relaid.Count > 0)
            foreach (var wall in walls)
            foreach (var window in document.Elements.OfType<Window>().Where(window => window.HostWallId == wall!.Id).ToList())
                SuitTo(document, wall!, window);

        return moved || relaid.Count > 0 || Math.Abs(before.HeightOffset - offset) > 1e-6 ||
               !before.Slopes.SequenceEqual(dormer.Edges.Select(edge => edge.SlopeDegrees)) ||
               !before.Heights.SequenceEqual(walls.Select(wall => wall!.UnconnectedHeight));

        // Where it cannot be followed - the roof never meets it - it stays as it was.
        bool Restore()
        {
            for (var i = relaid.Count - 1; i >= 0; i--) relaid[i].Undo();
            for (var i = 0; i < walls.Count; i++) walls[i]!.UnconnectedHeight = before.Heights[i];
            dormer.HeightOffset = before.HeightOffset;
            if (!before.Slopes.SequenceEqual(dormer.Edges.Select(edge => edge.SlopeDegrees)))
            {
                var edges = dormer.Edges.Select(edge => edge.Copy()).ToList();
                for (var i = 0; i < edges.Count; i++) edges[i].SlopeDegrees = before.Slopes[i];
                dormer.SetEdges(edges);
            }

            return false;
        }
    }

    /// <summary>
    /// The nearest place level with a point, along the slope, where the dormer asked for fits -
    /// away from a hipped end, where the roof slopes down at the side - or null when it fits
    /// nowhere along it.
    /// </summary>
    public static Point2D? NearestThatFits(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness, double? roofThickness = null)
    {
        if (Frame(document, main, at) is not var (_, across, _)) return null;

        for (var step = 250.0; step <= 30000; step += 250)
        foreach (var side in new[] { 1.0, -1.0 })
        {
            var point = at + across * (side * step);
            if (main.Contains(point) && HeightThatFits(document, main, point, settings, wallThickness, roofThickness) is { } fits && fits >= MinimumHeight)
                return point;
        }

        return null;
    }

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
    /// The dormer asked for, made to fit at a point on a roof - no higher than fits under the
    /// ridge, a shed no steeper than the roof it sits in, or it would never meet it - with the way
    /// up the slope there, the way across it, and how steep it is. Null, with the reason, where not
    /// even a low one fits.
    /// </summary>
    private static (DormerSettings Size, Vector2D Uphill, Vector2D Across, double Rise)? Fitted(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, double thickness, double? roofThickness, out string? problem)
    {
        problem = null;

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

        // A shed no steeper than the roof it sits in, or it would never meet it - and it is
        // at that slope that it has to fit.
        var mainPitch = Math.Atan(rise) * 180 / Math.PI;
        var built = settings.Shape == DormerShape.Shed ? settings with { Slope = Math.Min(settings.Slope, mainPitch - 5) } : settings;
        if (built.Slope < 2)
        {
            problem = "The roof is too shallow for a shed dormer to fall to the front below it.";
            return null;
        }

        var fitsHere = HeightThatFits(document, main, at, built, thickness, roofThickness);
        var fits = fitsHere ?? 0;
        if (fits < MinimumHeight)
        {
            // Near a hipped end the roof slopes away at the side, and that is what stops it:
            // said so, with how far along it would fit.
            var along = NearestThatFits(document, main, at, built, thickness, roofThickness) is { } fitsAt
                ? $" It fits {Units.FormatLength(at.DistanceTo(fitsAt))} further along the roof, away from its end. "
                : " ";
            var shed = settings.Shape == DormerShape.Shed ? "" : "try a Shed dormer, ";
            problem = fitsHere is null
                ? $"The dormer would hang off the roof here: {Units.FormatLength(settings.Width)} wide, its side runs past the roof's edge.{along}" +
                  "Click further in from the edge, or make it narrower."
                : $"The roof is too low here for a dormer: even {Units.FormatLength(MinimumHeight)} high, its roof would come out " +
                  $"above the main roof, {Units.FormatLength(MinimumHeight - fits)} short of fitting.{along}Click nearer the eave or " +
                  $"further from a hipped end, make it narrower or its slope shallower, {shed}or give the main roof a steeper slope.";
            return null;
        }

        return (built with { Height = Math.Min(settings.Height, fits) }, uphill, across, rise);
    }

    /// <summary>
    /// The dormer the Dormer tool would make at a point: as asked, or lower where the roof has
    /// not room for it. Null where none fits, with why.
    /// </summary>
    public static DormerSettings? SizeAt(BimDocument document, Roof main, Point2D at, DormerSettings settings, double wallThickness, out string? problem) =>
        Sized(document, main, at, settings, wallThickness, null, out problem)?.Size;

    /// <summary>
    /// As <see cref="Fitted"/>, for a new dormer, whose slope gives way before its height does.
    /// A dormer's roof climbs from its eaves to its ridge - or for a shed, all the way back to the
    /// main roof - and the steeper it is, the lower it has to start: on a shallow roof, a squat
    /// box under a tall hat. Where it would be made lower than asked, or not at all, its slope is
    /// brought down first - a gable or hip toward the roof's own pitch, a shed toward 10° - as far
    /// as it takes to be as high as asked, or all the way, and as high as that fits.
    /// </summary>
    private static (DormerSettings Size, Vector2D Uphill, Vector2D Across, double Rise)? Sized(
        BimDocument document, Roof main, Point2D at, DormerSettings settings, double thickness, double? roofThickness, out string? problem)
    {
        var fitted = Fitted(document, main, at, settings, thickness, roofThickness, out problem);
        if (fitted is { } whole && whole.Size.Height >= settings.Height - 1) return fitted;
        if (main.IsExtrusion || Frame(document, main, at) is not var (_, _, rise)) return fitted;

        var pitch = Math.Atan(rise) * 180 / Math.PI;
        var (shallowest, steepest) = settings.Shape == DormerShape.Shed
            ? (10.0, Math.Min(settings.Slope, pitch - 5))
            : (pitch, settings.Slope);
        if (steepest <= shallowest) return fitted;

        double Fits(double slope) => HeightThatFits(document, main, at, settings with { Slope = slope }, thickness, roofThickness) ?? 0;
        var most = Fits(shallowest);
        if (most < MinimumHeight || most <= (fitted?.Size.Height ?? 0) + 1) return fitted;

        // Steeper is lower: the steepest that is still as high as asked, found by halving.
        var slope = shallowest;
        if (most >= settings.Height)
        {
            var (fitsAt, failsAt) = (shallowest, steepest);
            for (var i = 0; i < 30; i++)
            {
                var middle = (fitsAt + failsAt) / 2;
                if (Fits(middle) >= settings.Height) fitsAt = middle;
                else failsAt = middle;
            }

            slope = fitsAt;
        }

        // To the half degree below, as a pitch is written down.
        if (Fitted(document, main, at, settings with { Slope = Math.Floor(slope * 2) / 2 }, thickness, roofThickness, out var eased) is not { } shallower)
            return fitted;

        problem = eased;
        return shallower;
    }

    /// <summary>
    /// Where a dormer's three walls go - left side, front, right side - for a front wall centred
    /// on a point. Each is drawn so its outside, its left, faces out of the dormer. The side walls
    /// run up the slope until the main roof has risen to meet the dormer's roof - at its eaves,
    /// or for a shed, rising too, as far back as it takes the main roof to catch it up - and a
    /// little past, which the walls' outlines end at anyway.
    /// </summary>
    private static (Point2D Start, Point2D End)[] WallLines(
        Point2D at, Vector2D uphill, Vector2D across, double rise, DormerSettings size, double thickness)
    {
        var gaining = size.Shape == DormerShape.Shed ? rise - Math.Tan(size.Slope * Math.PI / 180) : rise;
        var run = size.Height / Math.Max(gaining, 0.02) + thickness + 300;

        var left = at - across * (size.Width / 2);
        var right = at + across * (size.Width / 2);

        return new[] { (left + uphill * run, left), (left, right), (right, right + uphill * run) };
    }

    /// <summary>
    /// A dormer's roof picked off the outside of its walls with the overhang, and closed at the
    /// back with a line to be carried back into the main roof: its outline and edges. An edge
    /// keeps the identity of the one it replaces - found by the wall it is picked off, or for the
    /// back, by having none - so a fascia or gutter along it stays on it. Null, with the reason,
    /// when it does not close.
    /// </summary>
    private static (List<Point2D> Boundary, List<RoofEdge> Edges)? Sketch(
        BimDocument document, IReadOnlyList<Wall> walls, DormerSettings size, double thickness,
        IReadOnlyList<RoofEdge> was, out string? problem)
    {
        problem = null;
        Guid IdFor(Guid? wallId) => was.FirstOrDefault(edge => edge.WallId == wallId)?.Id ?? Guid.NewGuid();

        var sides = walls
            .Select(wall => RoofSketch.LineOnWall(document, wall, !wall.Flipped, size.Overhang, false) is var (a, b)
                ? new RoofSketchLine(a, b, new RoofEdge
                {
                    Id = IdFor(wall.Id), WallId = wall.Id, OnLeftOfWall = !wall.Flipped, Overhang = size.Overhang, SlopeDegrees = size.Slope
                })
                : null)
            .ToList();

        if (sides.Any(line => line is null))
        {
            problem = "The dormer's walls could not carry a roof.";
            return null;
        }

        var (leftSide, frontSide, rightSide) = (sides[0]!, sides[1]!, sides[2]!);
        leftSide.Edge.DefinesSlope = size.Shape is DormerShape.Gable or DormerShape.Hip;
        rightSide.Edge.DefinesSlope = size.Shape is DormerShape.Gable or DormerShape.Hip;
        frontSide.Edge.DefinesSlope = size.Shape is DormerShape.Shed or DormerShape.Hip;

        var back = new RoofSketchLine(leftSide.Start, rightSide.End, new RoofEdge { Id = IdFor(null), DefinesSlope = false });
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

        return (outline.Boundary.ToList(), outline.Edges.ToList());
    }

    /// <summary>
    /// A dormer made by the Dormer tool made again as another - another shape, wider, higher,
    /// steeper, a longer overhang - where it stands: its walls moved and its roof picked off them
    /// afresh and carried back into the main roof. It is the same dormer afterwards, not a new
    /// one: the windows in its walls stay in them, sized again to suit it as a new one is, and a
    /// fascia, gutter or soffit that ran all round it runs all round it again - along the front,
    /// say, when a gable becomes a shed. As high as asked, or as high as fits under the ridge.
    ///
    /// Returns the change, not yet made, as one step; or null, with the reason, when that dormer
    /// does not fit there. The message says too when it was made other than asked.
    /// </summary>
    public static IUndoableCommand? Change(BimDocument document, Roof dormer, DormerSettings settings, out string? message)
    {
        message = null;

        var main = document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == dormer.JoinedTo);
        var found = dormer.DormerWalls.Select(id => document.Walls.FirstOrDefault(wall => wall.Id == id)).ToList();
        if (dormer.Dormer is null || main is null || found.Count != 3 || found.Any(wall => wall is null || wall.IsCurved))
        {
            message = "Only a dormer made by the Dormer tool, with its three walls still standing, changes as one. Change its walls and roof one at a time.";
            return null;
        }

        if (settings.Width < 600 || settings.Height < MinimumHeight || settings.Slope is < 2 or > 75 || settings.Overhang is < 0 or > 2000)
        {
            message = $"A dormer is at least {Units.FormatLength(600)} wide and {Units.FormatLength(MinimumHeight)} high, " +
                      $"with a slope between 2° and 75° and an overhang of no more than {Units.FormatLength(2000)}.";
            return null;
        }

        var walls = found.Select(wall => wall!).ToList();
        var front = walls[1];
        var at = front.LocationCurve.PointAt(front.Length / 2);
        var thickness = document.GetWallType(front)?.Structure.TotalWidth ?? 0;
        // Its slope gives way before its height, as a new one's does - unless it is the slope
        // that was asked for.
        var roofThickness = document.FindType<SlabType>(dormer.TypeId)?.Thickness;
        var slopeAsked = Math.Abs(settings.Slope - dormer.Dormer.Slope) > 1e-9 && settings.Shape == dormer.Dormer.Shape;
        var sized = slopeAsked
            ? Fitted(document, main, at, settings, thickness, roofThickness, out message)
            : Sized(document, main, at, settings, thickness, roofThickness, out message);
        if (sized is not var (size, uphill, across, rise)) return null;

        // Kept at the slope it is made, so it goes on being made at it as the roof changes.
        var asked = settings;
        if (size.Slope < settings.Slope) settings = settings with { Slope = size.Slope };

        var before = Snapshot(document, dormer, walls);
        var shapeBefore = Geometry(dormer, walls);

        // What ran all round it runs all round it again, along the eaves it has now.
        var allRound = RoofEdgeSweeps.Of(document, dormer)
            .Where(sweep => sweep.EdgeIds.ToHashSet().SetEquals(AllRound(document, dormer, sweep)))
            .ToList();

        var level = document.FindLevel(dormer.LevelId)?.Elevation ?? 0;
        var eave = main.TopAt(document, at) + size.Height;
        var lines = WallLines(at, uphill, across, rise, size, thickness);
        for (var i = 0; i < walls.Count; i++)
        {
            var wall = walls[i];
            new MoveWallCommand(wall, wall.Start, wall.End, lines[i].Start, lines[i].End, "Change Dormer").KeepingOpenings(document).Redo();
            wall.UnconnectedHeight = eave - level + 500;
        }

        dormer.HeightOffset = eave - level;
        dormer.Dormer = settings;
        if (Sketch(document, walls, size, thickness, dormer.Edges, out message) is not var (boundary, edges))
        {
            before();
            return null;
        }

        dormer.SetBoundary(boundary);
        dormer.SetEdges(edges);

        var backEdge = Enumerable.Range(0, dormer.Boundary.Count)
            .OrderByDescending(i => (dormer.Boundary[i].MidpointTo(dormer.Boundary[(i + 1) % dormer.Boundary.Count]) - at).Dot(uphill))
            .First();
        if (RoofJoin.CarriedBack(document, dormer, dormer.Boundary, backEdge, main) is not { } joined)
        {
            before();
            message = "Its roof would come out above the main roof instead of running back into it. Make it narrower or lower.";
            return null;
        }

        dormer.SetBoundary(joined);

        foreach (var sweep in allRound)
        {
            var now = AllRound(document, dormer, sweep);
            if (now.Count == 0) continue;

            sweep.EdgeIds.Clear();
            sweep.EdgeIds.AddRange(now);
        }

        foreach (var wall in walls)
        foreach (var window in document.Elements.OfType<Window>().Where(window => window.HostWallId == wall.Id).ToList())
            SuitTo(document, wall, window);

        // Asked for more than there is room for, and already as much as there is.
        var lowered = size.Height < asked.Height - 1;
        var flattened = size.Slope < asked.Slope - 1e-6;
        if (Same(shapeBefore, Geometry(dormer, walls)))
        {
            before();
            message = lowered
                ? $"It is as high as a dormer fits under the ridge here already: {Units.FormatLength(size.Height)}."
                : flattened
                    ? $"A shed dormer falls at least 5° less steeply than the roof it sits in, or it would never meet it: {size.Slope:0.#}° is as steep as it goes here."
                    : null;
            return null;
        }

        message = (lowered ? $"Made {Units.FormatLength(size.Height)} high - as high as a dormer fits under the ridge here. " : "") +
                  (flattened
                      ? slopeAsked
                          ? $"Its slope is {size.Slope:0.#}° - a shed dormer falls less steeply than the roof it sits in, or it would never meet it."
                          : $"Its slope is {size.Slope:0.#}° - shallower, so it stands higher under the ridge."
                      : lowered ? "" : $"{Describe(document, dormer)} - its walls, its roof and what is in them changed together.");
        message = message.Trim();

        var after = Snapshot(document, dormer, walls);
        before();
        return new ChangeDormerCommand(before, after);
    }

    /// <summary>The edges a fascia, gutter or soffit takes on a roof when put all round it.</summary>
    private static IReadOnlyList<Guid> AllRound(BimDocument document, Roof roof, RoofEdgeSweep sweep) => sweep switch
    {
        Gutter => RoofEdgeSweeps.GutterEdges(document, roof),
        Soffit => RoofEdgeSweeps.SoffitEdges(document, roof),
        _ => RoofEdgeSweeps.FasciaEdges(document, roof)
    };

    /// <summary>
    /// A window in a dormer's wall made to suit it again, as a new one is: as big as its own size
    /// where there is room, smaller - keeping its proportions - where there is not. One given a
    /// size of its own, not its type's scaled down, is kept to that, only made smaller if it must.
    /// </summary>
    private static void SuitTo(BimDocument document, Wall wall, Window window)
    {
        if (document.FindType<OpeningType>(window.TypeId) is not { } type || type.Width <= 0 || type.Height <= 0) return;

        var (width, height) = (window.WidthOf(type), window.HeightOf(type));
        if (width <= 0 || height <= 0) return;
        // Scaled down to whole centimetres its proportions are not quite its type's: near enough.
        var fitted = Math.Abs(width / Math.Max(height, 1) / (type.Width / type.Height) - 1) < 0.03 && width <= type.Width + 1e-6;
        var (wantWidth, wantHeight) = fitted ? (type.Width, type.Height) : (width, height);

        if (WallOpenings.SizeToFit(document, wall, window.DistanceAlongWall, window.SillHeight, wantWidth, wantHeight) is not { } size) return;

        window.DistanceAlongWall = size.Along;
        (window.WidthOverride, window.HeightOverride) = Math.Abs(size.Width - type.Width) < 1e-6 && Math.Abs(size.Height - type.Height) < 1e-6
            ? ((double?)null, (double?)null)
            : (size.Width, size.Height);
    }

    /// <summary>The numbers a dormer's shape comes down to, to tell whether a change changed it.</summary>
    private static List<double> Geometry(Roof dormer, IReadOnlyList<Wall> walls) =>
        walls.SelectMany(wall => new[] { wall.Start.X, wall.Start.Y, wall.End.X, wall.End.Y })
            .Concat(dormer.Boundary.SelectMany(corner => new[] { corner.X, corner.Y }))
            .Concat(dormer.Edges.SelectMany(edge => new[] { edge.DefinesSlope ? 1 : 0, edge.SlopeDegrees, edge.Overhang }))
            .Append(dormer.HeightOffset)
            .ToList();

    private static bool Same(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);

    /// <summary>
    /// Everything about a dormer a change can touch - its roof, its walls, the doors, windows and
    /// openings in them, and the edges its fascias, gutters and soffits run along - kept, to be
    /// put back as it is now.
    /// </summary>
    private static Action Snapshot(BimDocument document, Roof dormer, IReadOnlyList<Wall> walls)
    {
        var boundary = dormer.Boundary.ToList();
        var edges = dormer.Edges.Select(edge => edge.Copy()).ToList();
        var (offset, settings) = (dormer.HeightOffset, dormer.Dormer);
        var wallsWere = walls.Select(wall => (Wall: wall, wall.Start, wall.End, wall.UnconnectedHeight)).ToList();

        var hosts = walls.Select(wall => wall.Id).ToHashSet();
        var openings = document.Elements.OfType<Opening>()
            .Where(opening => hosts.Contains(opening.HostWallId))
            .Select(opening => (Opening: opening, opening.DistanceAlongWall, opening.SillHeight, opening.WidthOverride, opening.HeightOverride))
            .ToList();
        var cuts = document.Elements.OfType<WallOpening>()
            .Where(cut => hosts.Contains(cut.HostWallId))
            .Select(cut => (Cut: cut, cut.DistanceAlongWall))
            .ToList();
        var sweeps = RoofEdgeSweeps.Of(document, dormer).Select(sweep => (Sweep: sweep, Edges: sweep.EdgeIds.ToList())).ToList();

        return () =>
        {
            foreach (var (wall, start, end, height) in wallsWere) (wall.Start, wall.End, wall.UnconnectedHeight) = (start, end, height);

            dormer.HeightOffset = offset;
            dormer.Dormer = settings;
            dormer.SetBoundary(boundary);
            dormer.SetEdges(edges.Select(edge => edge.Copy()));

            foreach (var (opening, along, sill, width, height) in openings)
                (opening.DistanceAlongWall, opening.SillHeight, opening.WidthOverride, opening.HeightOverride) = (along, sill, width, height);
            foreach (var (cut, along) in cuts) cut.DistanceAlongWall = along;
            foreach (var (sweep, ids) in sweeps)
            {
                sweep.EdgeIds.Clear();
                sweep.EdgeIds.AddRange(ids);
            }
        };
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
        used = settings;

        if (document.FindType<WallType>(wallTypeId) is not { } wallType)
        {
            problem = "There is no wall type to build the dormer's walls from.";
            return null;
        }

        var thickness = wallType.Structure.TotalWidth;
        if (Sized(document, main, at, settings, thickness, null, out problem) is not var (size, uphill, across, rise)) return null;
        used = size;

        var level = document.FindLevel(main.LevelId)?.Elevation ?? 0;
        var eave = main.TopAt(document, at) + size.Height;

        var walls = WallLines(at, uphill, across, rise, size, thickness)
            .Select(line => new Wall { Start = line.Start, End = line.End })
            .ToArray();

        var dormer = new Roof
        {
            TypeId = RoofTypeFor(document, main),
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

        if (Sketch(document, walls, size, thickness, Array.Empty<RoofEdge>(), out problem) is not var (boundary, edges)) return null;
        dormer.SetBoundary(boundary);
        dormer.SetEdges(edges);

        // One dormer, picked as one: the roof knows the walls that are part of it - and the
        // dormer asked for, which it is kept as, as far as the roof has room for it. A gable or
        // hip made shallower to keep its height is kept at the slope it was made.
        dormer.Dormer = size.Slope < settings.Slope ? settings with { Slope = size.Slope } : settings;
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

/// <summary>
/// A dormer changed as a whole - see <see cref="Dormers.Change"/> - as one step: all of it as it
/// was, or all of it as it became.
/// </summary>
public sealed class ChangeDormerCommand : IUndoableCommand
{
    private readonly Action _before;
    private readonly Action _after;

    internal ChangeDormerCommand(Action before, Action after)
    {
        _before = before;
        _after = after;
    }

    public string Name => "Change Dormer";

    public void Redo() => _after();

    public void Undo() => _before();
}
