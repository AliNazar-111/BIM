using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>How a roof window opens.</summary>
public enum RoofWindowOperation
{
    /// <summary>Turning on a bar across its middle: the usual roof window.</summary>
    CentrePivot,

    /// <summary>Hinged along its top, swinging out at the bottom: an escape window.</summary>
    TopHung,

    /// <summary>Not opening: a rooflight.</summary>
    Fixed
}

/// <summary>
/// A roof window's type: how big it is - across the slope and up it - how wide its frame is and
/// how far it stands up out of the roof, how it opens, and what it is framed and glazed with.
/// </summary>
public sealed class RoofWindowType : ElementType
{
    public RoofWindowType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.RoofWindows;

    /// <summary>Across the slope, outside the frame.</summary>
    public double Width { get; set; } = 780;

    /// <summary>Up the slope, outside the frame - measured along the roof, not in plan.</summary>
    public double Height { get; set; } = 980;

    /// <summary>How wide the frame round the glass is.</summary>
    public double FrameWidth { get; set; } = 70;

    /// <summary>How far the frame stands up out of the roof's surface, square to it.</summary>
    public double Upstand { get; set; } = 90;

    public RoofWindowOperation Operation { get; set; } = RoofWindowOperation.CentrePivot;

    public Guid FrameMaterialId { get; set; }

    public Guid GlassMaterialId { get; set; }

    public RoofWindowType Duplicate(string name) => new(name)
    {
        Width = Width, Height = Height, FrameWidth = FrameWidth, Upstand = Upstand, Operation = Operation,
        FrameMaterialId = FrameMaterialId, GlassMaterialId = GlassMaterialId, Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindValidated(RoofWindowParameters.Width, () => Width, (double v) => Set(Unbuildable(v, Height, FrameWidth, Upstand) is null, () => Width = v));
        yield return ParameterValue.BindValidated(RoofWindowParameters.Height, () => Height, (double v) => Set(Unbuildable(Width, v, FrameWidth, Upstand) is null, () => Height = v));
        yield return ParameterValue.BindValidated(RoofWindowParameters.FrameWidth, () => FrameWidth,
            (double v) => Set(Unbuildable(Width, Height, v, Upstand) is null, () => FrameWidth = v));
        yield return ParameterValue.BindValidated(RoofWindowParameters.Upstand, () => Upstand, (double v) => Set(Unbuildable(Width, Height, FrameWidth, v) is null, () => Upstand = v));
        yield return ParameterValue.BindChoice(
            RoofWindowParameters.Operation,
            () => EnumText.Humanise(Operation),
            v => { if (EnumText.TryParse<RoofWindowOperation>(v, out var operation)) Operation = operation; },
            EnumText.Choices<RoofWindowOperation>());

        if (document is not null)
        {
            yield return MaterialOf(document, RoofWindowParameters.FrameMaterial, () => FrameMaterialId, id => FrameMaterialId = id);
            yield return MaterialOf(document, RoofWindowParameters.GlassMaterial, () => GlassMaterialId, id => GlassMaterialId = id);
        }
    }

    /// <summary>Why a roof window of these sizes could not be made - too small, all frame - or null where it can.</summary>
    public static string? Unbuildable(double width, double height, double frameWidth, double upstand) =>
        width < 300 || height < 300 ? "A roof window is at least 300 mm across and up the slope."
        : frameWidth <= 10 ? "Its frame has to be more than 10 mm wide."
        : 2 * frameWidth + 50 >= Math.Min(width, height) ? "Its frame would leave no glass: make the frame narrower, or the window bigger."
        : upstand is < 0 or > 500 ? "Its upstand has to be between 0 and 500 mm."
        : null;

    private static bool Set(bool valid, Action apply)
    {
        if (valid) apply();
        return valid;
    }

    private static ParameterValue MaterialOf(BimDocument document, ParameterDefinition definition, Func<Guid> get, Action<Guid> set) =>
        ParameterValue.BindChoice(
            definition,
            () => document.FindMaterial(get())?.Name ?? "<by category>",
            v => set(document.Materials.FirstOrDefault(m => m.Name == v)?.Id ?? Guid.Empty),
            new[] { "<by category>" }.Concat(document.Materials.Select(m => m.Name).OrderBy(name => name)).ToArray());
}

/// <summary>
/// A window in a roof: a roof window or rooflight lying in the slope, lined up with it, the roof
/// cut away under it. Hosted by its roof - deleted and copied with it, and carried when it moves -
/// but set where it is in plan, so it stays put when the roof's outline changes round it.
/// </summary>
public sealed class RoofWindow : Element, IHostedElement
{
    public override BuiltInCategory Category => BuiltInCategory.RoofWindows;

    /// <summary>The roof it is in.</summary>
    public Guid RoofId { get; set; }

    public Guid HostId => RoofId;

    /// <summary>Its middle, in plan.</summary>
    public Point2D Location { get; set; }

    // This window's own size, frame, upstand and materials, where it has been given them: null
    // takes its type's. Changing one window changes that window, and neither its type nor the
    // windows put in after it.

    /// <summary>Across the slope, outside the frame; null for its type's.</summary>
    public double? Width { get; set; }

    /// <summary>Up the slope, outside the frame; null for its type's.</summary>
    public double? Height { get; set; }

    /// <summary>How wide its frame is; null for its type's.</summary>
    public double? FrameWidth { get; set; }

    /// <summary>How far it stands up out of the roof; null for its type's.</summary>
    public double? Upstand { get; set; }

    /// <summary>What its frame is made of; null for its type's.</summary>
    public Guid? FrameMaterialId { get; set; }

    /// <summary>What it is glazed with; null for its type's.</summary>
    public Guid? GlassMaterialId { get; set; }

    /// <summary>All of its own values at once, so a change can be kept and undone whole.</summary>
    public RoofWindowOwn Own
    {
        get => new(Width, Height, FrameWidth, Upstand, FrameMaterialId, GlassMaterialId);
        set => (Width, Height, FrameWidth, Upstand, FrameMaterialId, GlassMaterialId) =
            (value.Width, value.Height, value.FrameWidth, value.Upstand, value.FrameMaterialId, value.GlassMaterialId);
    }

    /// <summary>
    /// Its type as this window is built: the type's size, frame, upstand and materials, with this
    /// window's own wherever it has them. The type itself when it has none.
    /// </summary>
    public RoofWindowType Built(RoofWindowType type) =>
        Own == default
            ? type
            : new RoofWindowType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Description = type.Description, Cost = type.Cost, Operation = type.Operation,
                Width = Width ?? type.Width, Height = Height ?? type.Height,
                FrameWidth = FrameWidth ?? type.FrameWidth, Upstand = Upstand ?? type.Upstand,
                FrameMaterialId = FrameMaterialId ?? type.FrameMaterialId, GlassMaterialId = GlassMaterialId ?? type.GlassMaterialId
            };

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var roof = document.Elements.OfType<Roof>().FirstOrDefault(candidate => candidate.Id == RoofId);
        var type = document.FindType<RoofWindowType>(TypeId);

        yield return ParameterValue.ReadOnly(RoofWindowParameters.Roof,
            () => roof is null ? "<none>" : document.FindType<SlabType>(roof.TypeId)?.Name ?? "Roof");
        yield return ParameterValue.ReadOnly(RoofWindowParameters.Slope, () => RoofWindows.Frame(document, this)?.Plane.SlopeDegrees ?? 0);
        yield return ParameterValue.ReadOnly(RoofWindowParameters.SillHeight,
            () => RoofWindows.Frame(document, this) is { } frame
                ? frame.Top(frame.Corners[0]) - (document.FindLevel(LevelId)?.Elevation ?? 0)
                : 0);

        if (type is not null)
        {
            // This window's size and make-up, changed for it alone.
            yield return ParameterValue.BindCommand<double>(RoofWindowParameters.InstanceWidth, () => Built(type).Width,
                (double value, out string? message) => Change(document, type, own => own with { Width = value }, out message));
            yield return ParameterValue.BindCommand<double>(RoofWindowParameters.InstanceHeight, () => Built(type).Height,
                (double value, out string? message) => Change(document, type, own => own with { Height = value }, out message));
            yield return ParameterValue.BindCommand<double>(RoofWindowParameters.InstanceFrameWidth, () => Built(type).FrameWidth,
                (double value, out string? message) => Change(document, type, own => own with { FrameWidth = value }, out message));
            yield return ParameterValue.BindCommand<double>(RoofWindowParameters.InstanceUpstand, () => Built(type).Upstand,
                (double value, out string? message) => Change(document, type, own => own with { Upstand = value }, out message));

            var materials = new[] { RoofWindowOwn.ByType }
                .Concat(document.Materials.Select(material => material.Name).OrderBy(name => name))
                .ToArray();
            yield return ParameterValue.BindChoiceCommand(RoofWindowParameters.InstanceFrameMaterial,
                () => MaterialName(document, FrameMaterialId),
                (string name, out string? message) => Change(document, type, own => own with { FrameMaterialId = MaterialNamed(document, name) }, out message),
                materials);
            yield return ParameterValue.BindChoiceCommand(RoofWindowParameters.InstanceGlassMaterial,
                () => MaterialName(document, GlassMaterialId),
                (string name, out string? message) => Change(document, type, own => own with { GlassMaterialId = MaterialNamed(document, name) }, out message),
                materials);
        }

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static string MaterialName(BimDocument document, Guid? id) =>
        id is { } own ? document.FindMaterial(own)?.Name ?? RoofWindowOwn.ByType : RoofWindowOwn.ByType;

    private static Guid? MaterialNamed(BimDocument document, string name) =>
        name == RoofWindowOwn.ByType ? null : document.Materials.FirstOrDefault(material => material.Name == name)?.Id;

    /// <summary>
    /// The change that gives this window its own values, or null - with why - where they would
    /// not make a window, or the window would no longer fit where it is on its roof.
    /// </summary>
    private IUndoableCommand? Change(BimDocument document, RoofWindowType type, Func<RoofWindowOwn, RoofWindowOwn> change, out string? message)
    {
        var before = Own;
        var after = change(before);

        Own = after;
        try
        {
            var built = Built(type);
            message = RoofWindowType.Unbuildable(built.Width, built.Height, built.FrameWidth, built.Upstand) ??
                      (RoofWindows.Frame(document, this) is { } frame
                          ? RoofWindows.Problem(document, frame, this) is { } problem ? $"At that size it would not fit where it is. {problem}" : null
                          : "It would no longer be on its roof.");
        }
        finally
        {
            Own = before;
        }

        return message is null ? new SetRoofWindowOwnCommand(this, before, after) : null;
    }
}

/// <summary>A roof window's own size, frame, upstand and materials; a null takes its type's.</summary>
public readonly record struct RoofWindowOwn(
    double? Width, double? Height, double? FrameWidth, double? Upstand, Guid? FrameMaterialId, Guid? GlassMaterialId)
{
    /// <summary>What a material choice shows while the window takes its type's.</summary>
    public const string ByType = "<by type>";
}

/// <summary>Gives one roof window its own size, frame, upstand or materials.</summary>
public sealed class SetRoofWindowOwnCommand : IUndoableCommand
{
    private readonly RoofWindow _window;
    private readonly RoofWindowOwn _before;
    private readonly RoofWindowOwn _after;

    public SetRoofWindowOwnCommand(RoofWindow window, RoofWindowOwn before, RoofWindowOwn after)
    {
        _window = window;
        _before = before;
        _after = after;
    }

    public string Name => "Roof Window";

    public void Redo() => _window.Own = _after;

    public void Undo() => _window.Own = _before;
}

/// <summary>
/// Where a roof window is on its roof: the face it lies in, and its outline there - lined up with
/// the slope, as wide across it as the window and as long up it as the window, seen in plan.
/// </summary>
public sealed record RoofWindowFrame(Roof Roof, RoofWindowType Type, RoofPlane Plane, double Thickness, Point2D Centre, Vector2D Uphill, Vector2D Across)
{
    /// <summary>How long it is up the slope, seen in plan.</summary>
    public double Run => Type.Height / Plane.VerticalStretch;

    /// <summary>Its outline in plan, anticlockwise from the bottom corner on its left.</summary>
    public IReadOnlyList<Point2D> Corners => Rectangle(Type.Width, Run);

    /// <summary>The glass inside the frame, in plan.</summary>
    public IReadOnlyList<Point2D> Glass => Rectangle(Type.Width - 2 * Type.FrameWidth, Run - 2 * Type.FrameWidth / Plane.VerticalStretch);

    /// <summary>The underside of the roof at a point.</summary>
    public double Bottom(Point2D point) => Plane.HeightAt(point);

    /// <summary>The top of the roof at a point.</summary>
    public double Top(Point2D point) => Plane.HeightAt(point) + Thickness * Plane.VerticalStretch;

    /// <summary>The top of the frame at a point: standing up out of the roof by the upstand, square to it.</summary>
    public double FrameTop(Point2D point) => Top(point) + Type.Upstand * Plane.VerticalStretch;

    private IReadOnlyList<Point2D> Rectangle(double width, double run)
    {
        var (a, u) = (Across * (width / 2), Uphill * (run / 2));
        var corners = new[] { Centre - a - u, Centre + a - u, Centre + a + u, Centre - a + u };
        return Polygon2D.SignedArea(corners) >= 0 ? corners : corners.Reverse().ToArray();
    }
}

/// <summary>
/// Roof windows: where one goes on a roof and whether it fits there, the hole it cuts, and the
/// window itself - a frame standing up out of the roof round a pane lying in the slope.
/// </summary>
public static class RoofWindows
{
    /// <summary>How near an edge of its face a roof window may come, mm: flashing needs somewhere to go.</summary>
    public const double Margin = 100;

    public static IEnumerable<RoofWindow> On(BimDocument document, Roof roof) =>
        document.Elements.OfType<RoofWindow>().Where(window => window.RoofId == roof.Id);

    /// <summary>Where a roof window is on its roof now, or null where it is not on a face of one.</summary>
    public static RoofWindowFrame? Frame(BimDocument document, RoofWindow window) =>
        document.Elements.OfType<Roof>().FirstOrDefault(roof => roof.Id == window.RoofId) is { } roof &&
        document.FindType<RoofWindowType>(window.TypeId) is { } type
            ? FrameOn(document, roof, window.Built(type), window.Location)
            : null;

    /// <summary>A roof window of a type on a roof, with its middle at a point: lined up with the face under that point. Null off the roof.</summary>
    public static RoofWindowFrame? FrameOn(BimDocument document, Roof roof, RoofWindowType type, Point2D at)
    {
        if (roof.IsExtrusion || !roof.Contains(at) || roof.Surface(document).FaceAt(at) is not { } face) return null;

        // Up the slope, and across it; on a flat roof, square to the project.
        var plane = face.Plane;
        var uphill = plane.Rise < 0.01 ? Vector2D.UnitY : new Vector2D(plane.A / plane.Rise, plane.B / plane.Rise);
        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;
        return new RoofWindowFrame(roof, type, plane, thickness, at, uphill, uphill.PerpendicularLeft());
    }

    /// <summary>
    /// Why a roof window cannot go where this frame puts it - off the roof, over a ridge, hip or
    /// valley, too near the edge, over a hole already there - or null where it fits.
    /// </summary>
    public static string? Problem(BimDocument document, RoofWindowFrame frame, RoofWindow? except = null)
    {
        var surface = frame.Roof.Surface(document);

        // All of it, and a margin round it, on the one face.
        var outline = frame.Corners;
        var centre = frame.Centre;
        foreach (var corner in outline)
        {
            var reach = corner + (corner - centre).NormalisedOrDefault(Vector2D.UnitX) * Margin;
            foreach (var point in new[] { corner, reach })
            {
                if (!frame.Roof.Contains(point)) return "It would run off the roof there: keep it further in from the edge.";
                if (surface.FaceAt(point) is not { } face || !SamePlane(face.Plane, frame.Plane))
                    return "It would cross a ridge, hip or valley there: keep it where it lies in one slope.";
            }
        }

        // Clear of every hole already in the roof: sketched ones, dormer openings, other windows.
        var holes = RoofJoin.Openings(document, frame.Roof, except).ToList();
        foreach (var hole in holes)
            if (outline.Any(corner => Polygon2D.Contains(hole, corner)) || hole.Any(corner => Polygon2D.Contains(outline, corner)) ||
                Polygon2D.Contains(hole, centre))
                return "There is already an opening there.";

        return null;
    }

    /// <summary>
    /// Why roof windows moved without their roof cannot stay where they have been put - off it,
    /// over a ridge, over another opening - or null where every one still fits. One whose roof
    /// moved with it has gone along with it and fits as it did.
    /// </summary>
    public static string? Misplaced(BimDocument document, IEnumerable<Element> moved)
    {
        var moving = moved.ToList();
        var roofs = moving.OfType<Roof>().Select(roof => roof.Id).ToHashSet();

        foreach (var window in moving.OfType<RoofWindow>().Where(window => !roofs.Contains(window.RoofId)))
        {
            if (Frame(document, window) is not { } frame) return "A roof window has to stay on its roof.";
            if (Problem(document, frame, window) is { } problem) return problem;
        }

        return null;
    }

    /// <summary>
    /// Where a line of sight - from one point, through another - first meets a roof's top: the
    /// place on the roof under the cursor while a roof window is dragged in 3D. Worked out face
    /// by face from the roof's shape, its holes ignored, so a window dragged over the hole it
    /// cut itself still finds the roof. Null where the line misses the roof.
    /// </summary>
    public static Point2D? Along(BimDocument document, Roof roof, Point3D from, Point3D through)
    {
        if (roof.IsExtrusion) return null;

        var thickness = document.FindType<SlabType>(roof.TypeId)?.Thickness ?? 0;
        var (dx, dy, dz) = (through.X - from.X, through.Y - from.Y, through.Z - from.Z);

        Point2D? nearest = null;
        var best = double.MaxValue;
        foreach (var facet in roof.Surface(document).Facets)
        {
            // The face's top: its underside plane lifted by the build-up, stretched by the pitch.
            var plane = facet.Plane;
            var top = plane.C + thickness * plane.VerticalStretch;

            // Running along the face never meets it.
            var closing = dz - plane.A * dx - plane.B * dy;
            if (Math.Abs(closing) < 1e-12) continue;

            var t = (plane.A * from.X + plane.B * from.Y + top - from.Z) / closing;
            if (t <= 0 || t >= best) continue;

            var at = new Point2D(from.X + t * dx, from.Y + t * dy);
            if (!Polygon2D.Contains(facet.Outline, at)) continue;

            (best, nearest) = (t, at);
        }

        return nearest;
    }

    private static bool SamePlane(RoofPlane a, RoofPlane b) =>
        Math.Abs(a.A - b.A) < 1e-6 && Math.Abs(a.B - b.B) < 1e-6 && Math.Abs(a.C - b.C) < 1e-3;

    /// <summary>The holes the roof windows in a roof cut in it: each one's outline in plan, straight down through it.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Holes(BimDocument document, Roof roof, RoofWindow? except = null) =>
        On(document, roof)
            .Where(window => !ReferenceEquals(window, except))
            .Select(window => Frame(document, window))
            .Where(frame => frame is not null)
            .Select(frame => frame!.Corners);

    /// <summary>
    /// The roof windows in the roofs being moved that are not moving already: a window goes with
    /// its roof, as a door goes with its wall.
    /// </summary>
    public static IReadOnlyList<Element> Following(BimDocument document, IReadOnlyList<Element> moving)
    {
        var roofs = moving.OfType<Roof>().Select(roof => roof.Id).ToHashSet();
        return roofs.Count == 0
            ? Array.Empty<Element>()
            : document.Elements.OfType<RoofWindow>().Where(window => roofs.Contains(window.RoofId) && !moving.Contains(window)).ToList();
    }

    private static readonly ColourRgb FrameColour = new(0x3A, 0x3F, 0x46);
    private static readonly ColourRgb GlassColour = new(0x8C, 0xC4, 0xE0);

    /// <summary>
    /// The window: a frame round the hole, from the roof's underside up to the upstand above its
    /// top - its sides straight up, as the hole's are - and the pane in it, lying in the slope.
    /// </summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, RoofWindow window)
    {
        if (Frame(document, window) is not { } frame) return Array.Empty<Mesh3D>();

        var type = frame.Type;
        var frameMaterial = document.FindMaterial(type.FrameMaterialId);
        var glassMaterial = document.FindMaterial(type.GlassMaterialId);

        var bars = new Mesh3D(window.Id, frame.Roof.LevelId, MeshKind.Roof, frameMaterial?.SurfaceColour ?? FrameColour, frameMaterial?.Name ?? "Roof Window Frame");
        var outer = frame.Corners;
        var inner = frame.Glass;
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            bars.AddExtrusion(new[] { outer[i], outer[j], inner[j], inner[i] }, frame.Bottom, frame.FrameTop);
        }

        // The pane, just down from the top of the frame, and a thin line of its frame over the glass edge.
        var glass = new Mesh3D(window.Id, frame.Roof.LevelId, MeshKind.Glazing, glassMaterial?.SurfaceColour ?? GlassColour, glassMaterial?.Name ?? "Glazing")
        {
            Opacity = 0.45
        };
        var stretch = frame.Plane.VerticalStretch;
        glass.AddExtrusion(inner, point => frame.FrameTop(point) - 30 * stretch, point => frame.FrameTop(point) - 20 * stretch);

        var meshes = new List<Mesh3D>();
        if (!bars.IsEmpty) meshes.Add(bars);
        if (!glass.IsEmpty) meshes.Add(glass);
        return meshes;
    }
}

public static class RoofWindowParameters
{
    public static readonly ParameterDefinition Roof = new("Roof", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Slope = new("Slope", ParameterDataType.Angle, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition SillHeight = new("Sill Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition InstanceWidth = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition InstanceHeight = new("Height (up the slope)", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition InstanceFrameWidth = new("Frame Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition InstanceUpstand = new("Upstand", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition InstanceFrameMaterial = new("Frame Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition InstanceGlassMaterial = new("Glass Material", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition Width = new("Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height (up the slope)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FrameWidth = new("Frame Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Upstand = new("Upstand", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Operation = new("Operation", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition FrameMaterial = new("Frame Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition GlassMaterial = new("Glass Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
}
