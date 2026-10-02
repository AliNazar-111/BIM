using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>Which rule sets how far a chimney stands above the roof.</summary>
public enum ChimneyRule
{
    /// <summary>
    /// England and Wales, Approved Document J: the outlet at least 1 m above the highest point
    /// where the chimney meets the roof, and at least 600 mm above the ridge when within 600 mm
    /// of it; and no lower than the roof within 2.3 m of it.
    /// </summary>
    ApprovedDocumentJ,

    /// <summary>The US 3-2-10 rule: 3 ft above where it passes through the roof, and 2 ft above anything within 10 ft.</summary>
    ThreeTwoTen
}

/// <summary>
/// A chimney: from its level up through the roof, standing far enough above the roof to draw -
/// set by the rule it follows, worked out from the roof each time - and built the way its type
/// says: a masonry stack with pots, a block system chimney, a twin-wall steel flue, an
/// industrial stack. A fireplace or stove at its foot opens into the room through the front of
/// its breast. It cuts its way through the roofs, floors and ceilings it passes, kept clear of
/// them by its clearance to combustibles.
/// </summary>
public sealed class Chimney : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Chimneys;

    /// <summary>Its middle, in plan.</summary>
    public Point2D Location { get; set; }

    /// <summary>How wide the stack is along its angle, mm - a round one's outside diameter.</summary>
    public double Width { get; set; } = 450;

    /// <summary>How deep the stack is across its angle, mm.</summary>
    public double Depth { get; set; } = 450;

    /// <summary>Which way its width runs in plan, degrees anticlockwise from the project's X axis. Its front - where a fireplace opens - faces 90° round from it.</summary>
    public double Angle { get; set; }

    /// <summary>How far its foot is above its level, mm.</summary>
    public double BaseOffset { get; set; }

    /// <summary>How many flues it carries.</summary>
    public int Flues { get; set; } = 1;

    public ChimneyRule Rule { get; set; } = ChimneyRule.ApprovedDocumentJ;

    /// <summary>How far above the least height its rule asks it stands, mm.</summary>
    public double ExtraHeight { get; set; }

    /// <summary>What there is at its foot, in the room: an open fireplace, a stove, or nothing.</summary>
    public ChimneyFireplace Fireplace { get; set; } = ChimneyFireplace.None;

    public Point2D Along => new Point2D(0, 0) + new Vector2D(Math.Cos(Angle * Math.PI / 180), Math.Sin(Angle * Math.PI / 180));

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = Chimneys.TypeOf(document, this);

        yield return ParameterValue.BindChoice(ChimneyParameters.BaseLevel,
            () => document.FindLevel(LevelId)?.Name ?? string.Empty,
            name => { if (document.Levels.FirstOrDefault(level => level.Name == name) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.Bind(ChimneyParameters.BaseOffset, () => BaseOffset, value => BaseOffset = value);
        yield return ParameterValue.BindChoice(ChimneyParameters.Rule,
            () => Rule == ChimneyRule.ThreeTwoTen ? "US 3-2-10" : "Approved Document J",
            value => Rule = value == "US 3-2-10" ? ChimneyRule.ThreeTwoTen : ChimneyRule.ApprovedDocumentJ,
            new[] { "Approved Document J", "US 3-2-10" });
        yield return ParameterValue.BindValidated(ChimneyParameters.ExtraHeight, () => ExtraHeight, (double value) =>
        {
            if (value < 0 || value > 50000) return false;
            ExtraHeight = value;
            return true;
        });
        yield return ParameterValue.ReadOnly(ChimneyParameters.LeastTop, () => Chimneys.LeastTop(document, this) - (document.FindLevel(LevelId)?.Elevation ?? 0));
        yield return ParameterValue.ReadOnly(ChimneyParameters.Height, () => Chimneys.Top(document, this) - Chimneys.Foot(document, this));
        yield return ParameterValue.ReadOnly(ChimneyParameters.AboveRoof, () => Chimneys.Top(document, this) - Chimneys.HighestContact(document, this) ?? 0);

        yield return ParameterValue.BindChoice(ChimneyParameters.Fireplace,
            () => EnumText.Humanise(Fireplace),
            value => { if (EnumText.TryParse<ChimneyFireplace>(value, out var fireplace)) Fireplace = fireplace; },
            EnumText.Choices<ChimneyFireplace>());

        if (type.IsRound)
        {
            yield return Positive(ChimneyParameters.Diameter, () => Width, value => Width = value);
        }
        else
        {
            yield return Positive(ChimneyParameters.Width, () => Width, value => Width = value);
            yield return Positive(ChimneyParameters.Depth, () => Depth, value => Depth = value);
        }

        yield return ParameterValue.BindCommand(ChimneyParameters.Rotation, () => Angle,
            (double value, out string? message) => FitChimneysCommand.Turn(document, this, value, out message));
        if (type.HasFlue)
            yield return ParameterValue.BindValidated(ChimneyParameters.Flues, () => Flues, (int value) =>
            {
                if (value < 1 || value > 8) return false;
                Flues = value;
                return true;
            });
        yield return ParameterValue.ReadOnly(ChimneyParameters.Designation, () => type.Designation);

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static ParameterValue Positive(ParameterDefinition definition, Func<double> get, Action<double> set) =>
        ParameterValue.BindValidated(definition, get, (double value) =>
        {
            if (value < 60 || value > 20000) return false;
            set(value);
            return true;
        });
}

public static partial class Chimneys
{
    /// <summary>How tall a chimney is with no roof to stand above, mm.</summary>
    public const double Freestanding = 4000;

    /// <summary>Approved Document J: above the highest contact, above a ridge it is near, and the reach of the roof that counts, mm.</summary>
    public const double AboveContact = 1000, AboveRidge = 600, NearRidge = 600, RoofReach = 2300;

    /// <summary>The US 3-2-10 rule, in mm: 3 ft above the roof, 2 ft above anything within 10 ft.</summary>
    public const double ThreeFeet = 914.4, TwoFeet = 609.6, TenFeet = 3048;

    /// <summary>The cap over a masonry stack: how far it oversails, and how thick; and each flue's pot.</summary>
    public const double CapOversail = 50, CapThickness = 75, PotDiameter = 225, PotHeight = 450;

    private static readonly ChimneyType Fallback = ChimneyType.Library()[0];

    /// <summary>A chimney's type; a chimney from before there were types is a masonry one.</summary>
    public static ChimneyType TypeOf(BimDocument document, Chimney chimney) =>
        document.FindType<ChimneyType>(chimney.TypeId) ?? Fallback;

    /// <summary>The way a chimney's front faces: where a fireplace in it opens.</summary>
    public static Vector2D Front(Chimney chimney) => new Vector2D(chimney.Along.X, chimney.Along.Y).PerpendicularLeft();

    /// <summary>A rectangle centred on the chimney - or on a point of it - turned with it.</summary>
    public static IReadOnlyList<Point2D> Rectangle(Chimney chimney, double width, double depth, Point2D? centre = null)
    {
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var across = along.PerpendicularLeft();
        var (w, d) = (along * (width / 2), across * (depth / 2));
        var at = centre ?? chimney.Location;
        return new[] { at - w - d, at + w - d, at + w + d, at - w + d };
    }

    /// <summary>A circle round a point, as many short sides.</summary>
    public static IReadOnlyList<Point2D> Circle(Point2D centre, double radius, int sides = 24) =>
        Enumerable.Range(0, sides).Select(i => 2 * Math.PI * i / sides)
            .Select(a => centre + new Vector2D(Math.Cos(a), Math.Sin(a)) * radius).ToList();

    /// <summary>The stack's outline in plan: square, or round.</summary>
    public static IReadOnlyList<Point2D> Outline(BimDocument document, Chimney chimney) => Grown(document, chimney, 0);

    /// <summary>The stack's outline grown by a margin all round.</summary>
    public static IReadOnlyList<Point2D> Grown(BimDocument document, Chimney chimney, double margin) =>
        TypeOf(document, chimney).IsRound
            ? Circle(chimney.Location, chimney.Width / 2 + margin)
            : Rectangle(chimney, chimney.Width + 2 * margin, chimney.Depth + 2 * margin);

    public static double Foot(BimDocument document, Chimney chimney) =>
        (document.FindLevel(chimney.LevelId)?.Elevation ?? 0) + chimney.BaseOffset;

    /// <summary>The roofs a chimney rises through: drawn by footprint, over its outline, and above its foot.</summary>
    public static IReadOnlyList<Roof> Roofs(BimDocument document, Chimney chimney)
    {
        var outline = Outline(document, chimney);
        var foot = Foot(document, chimney);
        return document.Elements.OfType<Roof>()
            .Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3)
            .Where(roof => PolygonBoolean.Combine(roof.Boundary, outline, BooleanOperation.Intersection).Sum(region => region.Area) > 100)
            .Where(roof => Contacts(document, roof, outline).Any(z => z > foot))
            .ToList();
    }

    /// <summary>The heights of a roof's top where it meets a chimney's sides.</summary>
    private static IEnumerable<double> Contacts(BimDocument document, Roof roof, IReadOnlyList<Point2D> outline)
    {
        for (var i = 0; i < outline.Count; i++)
        {
            var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
            var steps = outline.Count > 4 ? 1 : 8;
            for (var s = 0; s <= steps; s++)
            {
                var at = a + (b - a) * (s / (double)steps);
                if (roof.Contains(at)) yield return roof.TopAt(document, at);
            }
        }
    }

    /// <summary>The highest point where a chimney meets a roof it rises through, or null where it meets none.</summary>
    public static double? HighestContact(BimDocument document, Chimney chimney)
    {
        var outline = Outline(document, chimney);
        var contacts = Roofs(document, chimney).SelectMany(roof => Contacts(document, roof, outline)).ToList();
        return contacts.Count == 0 ? null : contacts.Max();
    }

    /// <summary>
    /// The lowest a chimney's top may be under its rule, in project elevation: above where it meets
    /// the roof, above a ridge it is near, and above the roof round it - and no lower than its
    /// type's own least height, which is what sets an industrial stack's.
    /// </summary>
    public static double LeastTop(BimDocument document, Chimney chimney)
    {
        var foot = Foot(document, chimney);
        var own = foot + TypeOf(document, chimney).MinimumHeight;
        var roofs = Roofs(document, chimney);
        if (HighestContact(document, chimney) is not { } contact) return Math.Max(own, foot + Freestanding);

        var outline = Outline(document, chimney);
        var top = contact + (chimney.Rule == ChimneyRule.ThreeTwoTen ? ThreeFeet : AboveContact);

        foreach (var roof in roofs)
        {
            var reach = chimney.Rule == ChimneyRule.ThreeTwoTen ? TenFeet : RoofReach;
            var round = Around(outline, reach).Where(roof.Contains).Select(near => roof.TopAt(document, near)).DefaultIfEmpty(double.MinValue).Max();

            // The roof round it: under 3-2-10, 2 ft above anything within 10 ft; under Approved
            // Document J, no lower than the roof within 2.3 m - but never past the ridge for that.
            top = chimney.Rule == ChimneyRule.ThreeTwoTen
                ? Math.Max(top, round + TwoFeet)
                : Math.Max(top, Math.Min(round, roof.RidgeElevation(document)));

            // A ridge within 600 mm: 600 mm above it.
            if (chimney.Rule == ChimneyRule.ApprovedDocumentJ)
            {
                foreach (var (from, to) in roof.Surface(document).BreakLines.Where(line => IsRidge(document, roof, line)))
                {
                    var gap = outline.Min(corner => Line2D.DistanceFromSegment(corner, from, to));
                    if (gap <= NearRidge || Polygon2D.Contains(outline, from.MidpointTo(to)))
                        top = Math.Max(top, Math.Max(roof.TopAt(document, from), roof.TopAt(document, to)) + AboveRidge);
                }
            }
        }

        return Math.Max(top, own);
    }

    /// <summary>A level line along the top of a roof: a ridge, rather than a hip or valley.</summary>
    private static bool IsRidge(BimDocument document, Roof roof, (Point2D From, Point2D To) line) =>
        Math.Abs(roof.TopAt(document, line.From) - roof.TopAt(document, line.To)) < 1;

    /// <summary>Points round an outline out to a reach: what counts as near it.</summary>
    private static IEnumerable<Point2D> Around(IReadOnlyList<Point2D> outline, double reach)
    {
        var centre = new Point2D(outline.Average(point => point.X), outline.Average(point => point.Y));
        var radius = outline.Max(point => point.DistanceTo(centre)) + reach;
        for (var ring = 1; ring <= 4; ring++)
        for (var i = 0; i < 24; i++)
        {
            var angle = 2 * Math.PI * i / 24;
            yield return centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * (radius * ring / 4);
        }
    }

    /// <summary>The top of a chimney's stack, under its cap or terminal: the least its rule allows, and any more it is given.</summary>
    public static double Top(BimDocument document, Chimney chimney) => LeastTop(document, chimney) + chimney.ExtraHeight;

    /// <summary>
    /// Whether a chimney passes through a slab: over it, and reaching into it - a roof anywhere
    /// over its outline below its top, a floor or ceiling between its foot and its top.
    /// </summary>
    public static bool Cuts(BimDocument document, Chimney chimney, Slab slab)
    {
        if (slab.Boundary.Count < 3 || slab is Roof { IsExtrusion: true }) return false;

        var outline = Outline(document, chimney);
        if (PolygonBoolean.Combine(slab.Boundary, outline, BooleanOperation.Intersection).Sum(region => region.Area) <= 100) return false;

        var (foot, top) = (Foot(document, chimney), Top(document, chimney));
        return slab is Roof roof
            ? Contacts(document, roof, outline).Any(z => z > foot && z - (document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0) * 2 < top)
            : slab.GetTopElevation(document) > foot + 1 && slab.GetBottomElevation(document) < top - 1;
    }

    /// <summary>The holes chimneys cut in a slab: each one's outline, opened up by its clearance to combustibles.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Holes(BimDocument document, Slab slab) =>
        document.Elements.OfType<Chimney>()
            .Where(chimney => Cuts(document, chimney, slab))
            .Select(chimney => Grown(document, chimney, TypeOf(document, chimney).ClearanceToCombustibles));

    /// <summary>Whether a chimney is drawn on a level's plan: on its own, and on every one it rises through.</summary>
    public static bool ShownOn(BimDocument document, Chimney chimney, Guid levelId)
    {
        if (chimney.LevelId == levelId) return true;
        if (document.FindLevel(levelId) is not { } level) return false;
        return level.Elevation >= Foot(document, chimney) - 1 && level.Elevation < Top(document, chimney) - 1;
    }

    /// <summary>Where each flue is, in plan: in a row along a square stack, round the middle of an industrial one, at the centre of a pipe.</summary>
    public static IReadOnlyList<Point2D> Flues(BimDocument document, Chimney chimney)
    {
        var type = TypeOf(document, chimney);
        var count = Math.Max(1, chimney.Flues);
        if (type.Construction == ChimneyConstruction.IndustrialMultiFlue)
        {
            var ring = Math.Max(0, chimney.Width / 2 - 300 - type.FlueDiameter / 2 - 150) * 0.7;
            return count == 1
                ? new[] { chimney.Location }
                : Enumerable.Range(0, count).Select(i => 2 * Math.PI * i / count)
                    .Select(a => chimney.Location + new Vector2D(Math.Cos(a), Math.Sin(a)) * ring).ToList();
        }

        if (type.IsRound || !type.HasFlue) return new[] { chimney.Location };

        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        return Enumerable.Range(0, count)
            .Select(i => chimney.Location + along * (chimney.Width * ((i + 0.5) / count - 0.5)))
            .ToList();
    }

    /// <summary>A chimney breast: its outline, its top, the fireplace opening in its front and how high, and the hearth before it, in plan.</summary>
    public sealed record ChimneyBreast(IReadOnlyList<Point2D> Outline, double Top, IReadOnlyList<Point2D> Opening, double OpeningHeight, IReadOnlyList<Point2D> Hearth);

    /// <summary>How far up the breast goes before it narrows to the stack: up to near the ceiling, below the roof.</summary>
    private static double BreastTop(BimDocument document, Chimney chimney)
    {
        var foot = Foot(document, chimney);
        var under = HighestContact(document, chimney) is { } contact ? contact - 600 : foot + 2400;
        return Math.Min(foot + 2400, under);
    }

    /// <summary>Whether a chimney has a breast in the room with a fireplace opening in its front: a masonry or block one with a fire at its foot.</summary>
    public static bool HasBreast(BimDocument document, Chimney chimney) =>
        chimney.Fireplace != ChimneyFireplace.None &&
        TypeOf(document, chimney).Construction is ChimneyConstruction.Masonry or ChimneyConstruction.PrecastBlock &&
        BreastTop(document, chimney) - Foot(document, chimney) >= 1500;

    /// <summary>
    /// The breast of a chimney with a fire at its foot: wider and deeper than the stack, its back
    /// on the stack's back, with the fireplace opening in its front at the floor and a hearth
    /// before it - the opening the room sees, not a hole underneath.
    /// </summary>
    public static ChimneyBreast? Breast(BimDocument document, Chimney chimney)
    {
        if (!HasBreast(document, chimney)) return null;

        var front = Front(chimney);
        var width = Math.Max(chimney.Width, 1200);
        var depth = Math.Max(chimney.Depth + 150, 600);
        var centre = chimney.Location + front * ((depth - chimney.Depth) / 2);
        var outline = Rectangle(chimney, width, depth, centre);

        var stove = chimney.Fireplace == ChimneyFireplace.Stove;
        var openingWidth = Math.Max(500, Math.Min(width - 450, stove ? 900 : 800));
        var openingDepth = Math.Min(depth - 115, stove ? 500 : 400);
        var face = centre + front * (depth / 2);
        var opening = Rectangle(chimney, openingWidth, openingDepth + 2, face - front * (openingDepth / 2 - 1));
        var hearth = Rectangle(chimney, openingWidth + 300, openingDepth + 500, face + front * ((500 - openingDepth) / 2));

        return new ChimneyBreast(outline, BreastTop(document, chimney), opening, stove ? 750 : 650, hearth);
    }

    /// <summary>
    /// What a chimney covers in plan on a level: on its own, its breast where it has one, or the
    /// hearth plate a metal flue's stove stands on; its stack elsewhere.
    /// </summary>
    public static IReadOnlyList<Point2D> Footprint(BimDocument document, Chimney chimney, Guid levelId)
    {
        if (levelId != chimney.LevelId) return Outline(document, chimney);
        if (Breast(document, chimney) is { } breast) return breast.Outline;
        return TypeOf(document, chimney).IsRound && chimney.Fireplace == ChimneyFireplace.Stove
            ? Rectangle(chimney, Math.Max(StovePlate, chimney.Width), StovePlate, chimney.Location + Front(chimney) * StovePlateForward)
            : Outline(document, chimney);
    }

    /// <summary>The plate a stove at the foot of a metal flue stands on: how big, and how far forward of the flue its middle is.</summary>
    public const double StovePlate = 900, StovePlateForward = 150;

    /// <summary>How far from a wall's face a chimney put down near it is drawn in against it, mm.</summary>
    public const double WallSnap = 600;

    /// <summary>How far away the nearest wall may be and still turn a chimney's front away from it, mm.</summary>
    public const double WallFacing = 3000;

    /// <summary>A wall's body in plan, about its body centreline: where it starts and ends, its left normal and half its thickness.</summary>
    private readonly record struct WallBody(Wall Wall, Point2D Start, Point2D End, Vector2D Normal, double Half)
    {
        public double Across(Point2D point) => (point - Start).Dot(Normal);

        public IReadOnlyList<Point2D> Outline => new[] { Start - Normal * Half, End - Normal * Half, End + Normal * Half, Start + Normal * Half };
    }

    /// <summary>The straight walls standing round a chimney's foot - on its storey, whatever level they are drawn from.</summary>
    private static List<WallBody> WallsAround(BimDocument document, Chimney chimney)
    {
        var foot = Foot(document, chimney);
        var bodies = new List<WallBody>();
        foreach (var wall in document.Walls.Where(wall => !wall.IsCurved))
        {
            if (document.GetWallType(wall) is not { } type || type.Structure.TotalWidth <= 0) continue;
            if (wall.GetBaseElevation(document) > foot + 1 || wall.GetTopElevation(document) <= foot + 1) continue;

            var (start, end) = wall.GetBodyCentreline(type.Structure);
            if (start.DistanceTo(end) < 1) continue;
            bodies.Add(new WallBody(wall, start, end, (end - start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft(), type.Structure.TotalWidth / 2));
        }

        return bodies;
    }

    /// <summary>The walls a chimney stands in, any part of it: what it should be moved out of.</summary>
    public static IReadOnlyList<Wall> WallsItIsIn(BimDocument document, Chimney chimney)
    {
        var footprint = Footprint(document, chimney, chimney.LevelId);
        return WallsAround(document, chimney)
            .Where(body => PolygonBoolean.Combine(body.Outline, footprint, BooleanOperation.Intersection).Sum(region => region.Area) > 100)
            .Select(body => body.Wall)
            .ToList();
    }

    /// <summary>
    /// Which side of a wall the room is: the side a point is clearly on; for a point in the wall
    /// itself, the side the rest of the walls are on - the inside of the building - or, for a wall
    /// standing alone, its interior face.
    /// </summary>
    private static double RoomSide(IReadOnlyList<WallBody> walls, WallBody wall, Point2D at)
    {
        var across = wall.Across(at);
        if (Math.Abs(across) > wall.Half + 1) return Math.Sign(across);

        var others = walls.Where(other => other.Wall != wall.Wall).ToList();
        if (others.Count > 0)
        {
            var length = others.Sum(other => other.Start.DistanceTo(other.End));
            var middle = new Point2D(
                others.Sum(other => (other.Start.X + other.End.X) / 2 * other.Start.DistanceTo(other.End)) / length,
                others.Sum(other => (other.Start.Y + other.End.Y) / 2 * other.Start.DistanceTo(other.End)) / length);
            if (Math.Abs(wall.Across(middle)) > 1) return Math.Sign(wall.Across(middle));
        }

        return wall.Wall.ExteriorNormalAt(0).Dot(wall.Normal) > 0 ? -1 : 1;
    }

    /// <summary>
    /// Sets a chimney into the room it is put down in, never into a wall: its back to the nearest
    /// wall and its front - where its fireplace opens - to the room; drawn in against the wall's
    /// face when put down near it, or on it; and slid along that wall clear of any other it would
    /// run into, as at a corner. A chimney standing well out in a room stays where it is, turned
    /// away from the nearest wall.
    /// </summary>
    public static void FitToWalls(BimDocument document, Chimney chimney)
    {
        var walls = WallsAround(document, chimney);
        if (walls.Count == 0) return;

        var back = walls.MinBy(body => Line2D.DistanceFromSegment(chimney.Location, body.Start, body.End));
        if (Line2D.DistanceFromSegment(chimney.Location, back.Start, back.End) > WallFacing) return;

        BackOnto(document, chimney, walls, back, WallSnap);
    }

    /// <summary>How far in front of a chimney there must be no wall: the reach of a hearth, mm.</summary>
    public const double FrontClearance = 500;

    /// <summary>
    /// The wall a chimney's front - its fireplace - faces, close enough that a hearth before it
    /// would run into it; null when it faces the room.
    /// </summary>
    public static Wall? WallInFront(BimDocument document, Chimney chimney) => FacedWall(document, chimney, WallsAround(document, chimney))?.Wall;

    private static WallBody? FacedWall(BimDocument document, Chimney chimney, IReadOnlyList<WallBody> walls)
    {
        var front = Front(chimney);
        var along = new Vector2D(chimney.Along.X, chimney.Along.Y);
        var footprint = Footprint(document, chimney, chimney.LevelId);

        // The strip of floor before its front face, a little narrower than it so a wall it is
        // only beside, as in a corner, is not taken for one it faces.
        var ahead = footprint.Max(point => (point - chimney.Location).Dot(front));
        var (left, right) = (footprint.Min(point => (point - chimney.Location).Dot(along)), footprint.Max(point => (point - chimney.Location).Dot(along)));
        var middle = chimney.Location + front * (ahead + 1 + FrontClearance / 2) + along * ((left + right) / 2);
        var strip = Rectangle(chimney, Math.Max(right - left - 20, 10), FrontClearance, middle);

        return walls
            .Where(wall => PolygonBoolean.Combine(wall.Outline, strip, BooleanOperation.Intersection).Sum(region => region.Area) > 100)
            .Select(wall => (WallBody?)wall)
            .MinBy(wall => Line2D.DistanceFromSegment(chimney.Location, wall!.Value.Start, wall.Value.End));
    }

    /// <summary>
    /// Turns a chimney whose front faces a wall round to face the room, its back against that
    /// wall where its front was. False, and left as it was, when it faces no wall - or when
    /// turned round it would face another, as in a passage too narrow for it.
    /// </summary>
    public static bool TurnFromWallInFront(BimDocument document, Chimney chimney)
    {
        var walls = WallsAround(document, chimney);
        if (FacedWall(document, chimney, walls) is not { } faced) return false;

        var (at, angle) = (chimney.Location, chimney.Angle);
        BackOnto(document, chimney, walls, faced, double.MaxValue);
        if (FacedWall(document, chimney, walls) is null) return true;

        (chimney.Location, chimney.Angle) = (at, angle);
        return false;
    }

    /// <summary>
    /// Sets a chimney's back to a wall, its front to the room, drawn in against the wall's face
    /// when it is no further from it than a distance; and slid along it clear of the others.
    /// </summary>
    private static void BackOnto(BimDocument document, Chimney chimney, IReadOnlyList<WallBody> walls, WallBody back, double snap)
    {
        // The front square to the wall, to the room; the width along the wall.
        var side = RoomSide(walls, back, chimney.Location);
        var front = back.Normal * side;
        chimney.Angle = Math.Atan2(-front.X, front.Y) * 180 / Math.PI;

        // How far its back - breast, stack or stove plate - is behind its middle.
        double Behind() => Footprint(document, chimney, chimney.LevelId).Max(point => (chimney.Location - point).Dot(front));

        var gap = back.Across(chimney.Location) * side - back.Half - Behind();
        if (gap > snap) return;
        chimney.Location += front * -gap;

        // Clear of the walls either side, sliding along the one it backs onto.
        var along = (back.End - back.Start).NormalisedOrDefault(Vector2D.UnitX);
        for (var pass = 0; pass < 3; pass++)
        {
            var moved = false;
            var footprint = Footprint(document, chimney, chimney.LevelId);
            foreach (var wall in walls.Where(wall => wall.Wall != back.Wall))
            {
                if (PolygonBoolean.Combine(wall.Outline, footprint, BooleanOperation.Intersection).Sum(region => region.Area) <= 100) continue;

                var away = RoomSide(walls, wall, chimney.Location);
                var slide = away * along.Dot(wall.Normal);
                if (Math.Abs(slide) < 0.2) continue;

                var shortfall = footprint.Max(point => wall.Half - away * wall.Across(point));
                chimney.Location += along * (shortfall / slide);
                footprint = Footprint(document, chimney, chimney.LevelId);
                moved = true;
            }

            if (!moved) break;
        }
    }
}

/// <summary>
/// Chimneys set right against the walls: moved out of a wall they were left in, back into the
/// room against its face, and turned round where their fireplace faced a wall. One step, so Undo
/// puts them back where the move left them.
/// </summary>
public sealed class FitChimneysCommand : IUndoableCommand
{
    private readonly List<(Chimney Chimney, Point2D FromAt, double FromAngle, Point2D ToAt, double ToAngle)> _moves;

    private FitChimneysCommand(List<(Chimney, Point2D, double, Point2D, double)> moves, string name)
    {
        _moves = moves;
        Name = name;
    }

    /// <summary>
    /// Fits those of the chimneys that stand in a wall or face one; null when none does. The
    /// chimneys are left fitted.
    /// </summary>
    public static FitChimneysCommand? For(BimDocument document, IEnumerable<Chimney> chimneys)
    {
        var moves = new List<(Chimney, Point2D, double, Point2D, double)>();
        foreach (var chimney in chimneys.Distinct())
        {
            var (at, angle) = (chimney.Location, chimney.Angle);
            if (Chimneys.WallsItIsIn(document, chimney).Count > 0) Chimneys.FitToWalls(document, chimney);
            Chimneys.TurnFromWallInFront(document, chimney);
            if (chimney.Location != at || chimney.Angle != angle) moves.Add((chimney, at, angle, chimney.Location, chimney.Angle));
        }

        return moves.Count == 0 ? null : new FitChimneysCommand(moves, moves.Count == 1 ? "Chimney Set Against the Wall" : "Chimneys Set Against the Walls");
    }

    /// <summary>
    /// A chimney turned to an angle - and, where that would face its fireplace into a wall,
    /// turned round to face the room instead, its back against the wall, saying so. The chimney
    /// is left as it was, for the command to make the change.
    /// </summary>
    public static FitChimneysCommand Turn(BimDocument document, Chimney chimney, double angle, out string? message)
    {
        var (fromAt, fromAngle) = (chimney.Location, chimney.Angle);
        chimney.Angle = angle;
        message = Chimneys.TurnFromWallInFront(document, chimney)
            ? "Its fireplace would have faced the wall, so it is turned round to face the room, its back against the wall."
            : null;

        var (toAt, toAngle) = (chimney.Location, chimney.Angle);
        (chimney.Location, chimney.Angle) = (fromAt, fromAngle);
        return new FitChimneysCommand(new() { (chimney, fromAt, fromAngle, toAt, toAngle) }, "Rotate Chimney");
    }

    public string Name { get; }

    public void Redo()
    {
        foreach (var (chimney, _, _, at, angle) in _moves) (chimney.Location, chimney.Angle) = (at, angle);
    }

    public void Undo()
    {
        foreach (var (chimney, at, angle, _, _) in _moves) (chimney.Location, chimney.Angle) = (at, angle);
    }
}

public static class ChimneyParameters
{
    public static readonly ParameterDefinition BaseLevel = new("Base Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition BaseOffset = new("Base Offset", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Rule = new("Height Rule", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition ExtraHeight = new("Extra Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition LeastTop = new("Least Top (above level)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition AboveRoof = new("Above Where It Meets the Roof", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Fireplace = new("At Its Foot", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Diameter = new("Diameter", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Flues = new("Flues", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Designation = new("EN 1443 Designation", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);
}
