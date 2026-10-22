using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>
/// An interior elevation marker (specification section 6.1): placed in a room, with up to four
/// arrows, each a view of the wall it points at - cropped to the room, its floor and its ceiling,
/// as Revit crops an interior elevation to the walls, floor and ceiling bounding its room.
/// </summary>
public sealed class InteriorElevationMarker : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Elevations;

    /// <summary>Its number, which names its views: "1 - North".</summary>
    public string Name { get; set; } = "1";

    public Point2D Location { get; set; }

    /// <summary>Turned anticlockwise from straight, degrees: its first arrow points up the plan at 0.</summary>
    public double Rotation { get; set; }

    /// <summary>Whether each arrow is a view: up the marker, to its right, down, to its left.</summary>
    public bool LooksUp { get; set; } = true;

    public bool LooksRight { get; set; } = true;

    public bool LooksDown { get; set; } = true;

    public bool LooksLeft { get; set; } = true;

    public bool Looks(int arrow) => arrow switch { 0 => LooksUp, 1 => LooksRight, 2 => LooksDown, 3 => LooksLeft, _ => false };

    /// <summary>The arrows that are views, in order round the marker.</summary>
    public IEnumerable<int> Arrows => Enumerable.Range(0, 4).Where(Looks);

    /// <summary>The way an arrow looks, in plan: up, right, down, left of the marker, turned with it.</summary>
    public Vector2D Look(int arrow)
    {
        var angle = (Rotation + 90 - 90 * arrow) * Math.PI / 180;
        return new Vector2D(Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>An arrow's view by name: by the compass where it looks square to it, otherwise a, b, c, d.</summary>
    public string ViewName(int arrow)
    {
        var look = Look(arrow);
        var compass = Math.Abs(look.Y) > 0.999 ? (look.Y > 0 ? "North" : "South") : Math.Abs(look.X) > 0.999 ? (look.X > 0 ? "East" : "West") : null;
        return $"{Name} - {compass ?? ((char)('a' + arrow)).ToString()}";
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(InteriorElevationParameters.Name, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.Bind(InteriorElevationParameters.Rotation, () => Rotation, value => { if (double.IsFinite(value)) Rotation = value; });
        yield return ParameterValue.Bind(InteriorElevationParameters.Up, () => LooksUp, value => LooksUp = value);
        yield return ParameterValue.Bind(InteriorElevationParameters.Right, () => LooksRight, value => LooksRight = value);
        yield return ParameterValue.Bind(InteriorElevationParameters.Down, () => LooksDown, value => LooksDown = value);
        yield return ParameterValue.Bind(InteriorElevationParameters.Left, () => LooksLeft, value => LooksLeft = value);
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

public static class InteriorElevationParameters
{
    public static readonly ParameterDefinition Name = new("Marker Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Rotation = new("Rotation", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Up = new("View Up", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Right = new("View Right", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Down = new("View Down", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Left = new("View Left", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
}

/// <summary>Where an interior elevation looks from, and the box it is cropped to.</summary>
public sealed record InteriorFrame(Camera3D Camera, Box3D Crop, bool InRoom);

public static class InteriorElevations
{
    /// <summary>How far past the wall looked at an interior elevation sees, mm: enough for the wall's face, not what is beyond it.</summary>
    private const double PastWall = 30;

    /// <summary>The next free marker number.</summary>
    public static string NextName(BimDocument document)
    {
        var names = document.All<InteriorElevationMarker>().Select(marker => marker.Name).ToHashSet();
        for (var i = 1; ; i++)
            if (!names.Contains(i.ToString())) return i.ToString();
    }

    /// <summary>
    /// Where an arrow's view looks from - the marker, at the floor - and the box round what it
    /// shows, in the view's own frame (across, up from the floor, away from the marker): between
    /// the room's walls either side, from its floor to its ceiling, out to the wall it looks at.
    /// With no room round it, six metres either way and up to the storey above.
    /// </summary>
    public static InteriorFrame Frame(BimDocument document, InteriorElevationMarker marker, int arrow)
    {
        var floor = document.FindLevel(marker.LevelId)?.Elevation ?? 0;
        var look = marker.Look(arrow);
        var right = new Vector2D(look.Y, -look.X);
        var eye = Point3D.On(marker.Location, floor);
        var camera = new Camera3D(eye, Point3D.On(marker.Location + look, floor), Perspective: false);

        var traced = RoomBoundary.Trace(document, marker.LevelId, marker.Location);
        var room = document.All<Room>().FirstOrDefault(r => r.LevelId == marker.LevelId &&
                                                                      r.GetBoundary(document) is { IsEnclosed: true } b && Polygon2D.Contains(b.Polygon, marker.Location));
        var above = document.Levels.Where(level => level.Elevation > floor + 1).Select(level => level.Elevation - floor).DefaultIfEmpty(3000).Min();
        var height = room?.UpperLimitOffset ?? CeilingOver(document, marker) ?? above;

        if (!traced.IsEnclosed || traced.Polygon.Count < 3)
            return new InteriorFrame(camera, new Box3D(new Point3D(-6000, -150, 0), new Point3D(6000, height + 150, 6000)), false);

        var across = traced.Polygon.Select(point => (point - marker.Location).Dot(right)).ToList();
        var away = traced.Polygon.Select(point => (point - marker.Location).Dot(look)).ToList();
        return new InteriorFrame(camera,
            new Box3D(new Point3D(across.Min(), -150, 0), new Point3D(across.Max(), height + 150, Math.Max(away.Max(), 100) + PastWall)), true);
    }

    /// <summary>The underside of the ceiling over a marker, above its floor; null where none is.</summary>
    private static double? CeilingOver(BimDocument document, InteriorElevationMarker marker)
    {
        var floor = document.FindLevel(marker.LevelId)?.Elevation ?? 0;
        var ceilings = document.All<Ceiling>()
            .Where(ceiling => ceiling.LevelId == marker.LevelId && ceiling.Contains(marker.Location))
            .Select(ceiling => ceiling.GetBottomElevation(document) - floor)
            .Where(height => height > 0)
            .ToList();
        return ceilings.Count > 0 ? ceilings.Min() : null;
    }
}
