using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A drawn profile for sweeps (Revit's "profile family"): any closed shape, as points out from
/// the wall face and up from the profile's bottom, in millimetres.
/// </summary>
public sealed class SweepProfileType : ElementType
{
    public SweepProfileType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Profiles;

    /// <summary>The outline: out from the face, up from the bottom. Millimetres.</summary>
    public List<Point2D> Points { get; } = new();

    /// <summary>How far the shape stands out from the face.</summary>
    public double Depth => Points.Count == 0 ? 0 : Points.Max(p => p.X) - Math.Min(0, Points.Min(p => p.X));

    /// <summary>How tall the shape is.</summary>
    public double Height => Points.Count == 0 ? 0 : Points.Max(p => p.Y) - Points.Min(p => p.Y);

    /// <summary>
    /// The shape scaled into a unit square - out from 0 to 1, up from 0 to 1 - and running
    /// anticlockwise, the way the built-in shapes are held. Null if it cannot be a profile.
    /// </summary>
    public IReadOnlyList<(double Out, double Up)>? Normalised()
    {
        if (Points.Count < 3 || Depth <= 0 || Height <= 0) return null;

        var minOut = Math.Min(0, Points.Min(p => p.X));
        var minUp = Points.Min(p => p.Y);
        var shape = Points.Select(p => ((p.X - minOut) / Depth, (p.Y - minUp) / Height)).ToList();
        if (Polygon2D.SignedArea(shape.Select(s => new Point2D(s.Item1, s.Item2)).ToList()) < 0) shape.Reverse();
        return shape;
    }

    /// <summary>What is wrong with this outline, or null if it will do.</summary>
    public static string? Problem(IReadOnlyList<Point2D> points)
    {
        if (points.Count < 3) return "A profile needs at least three corners.";
        if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return "Every corner needs two numbers.";
        if (Math.Abs(Polygon2D.SignedArea(points)) < 1) return "The profile encloses no area.";
        if (points.Max(p => p.X) <= 0) return "A profile has to stand out from the face somewhere.";

        // The same outline checks a wall's profile gets: nothing crossing itself.
        var shifted = points.Select(p => new Point2D(p.X - Math.Min(0, points.Min(q => q.X)), p.Y)).ToList();
        return WallProfile.Problem(shifted, double.MaxValue);
    }

    public SweepProfileType Duplicate(string name)
    {
        var copy = new SweepProfileType(name) { Description = Description };
        copy.Points.AddRange(Points);
        return copy;
    }

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;
        yield return ParameterValue.ReadOnly(SweepParameters.Depth, () => Depth);
        yield return ParameterValue.ReadOnly(SweepParameters.Height, () => Height);
    }
}

/// <summary>
/// A type of sweep or reveal placed on walls one by one (Revit's Wall: Sweep and Wall: Reveal
/// tools): its profile, size and material, whether it cuts into the wall, whether doors and
/// windows break it, and how far it stops short of the walls' ends.
/// </summary>
public sealed class WallSweepType : ElementType
{
    public WallSweepType(string name, SweepKind kind) : base(name)
    {
        Kind = kind;
    }

    public override BuiltInCategory Category => Kind == SweepKind.Reveal ? BuiltInCategory.WallReveals : BuiltInCategory.WallSweeps;

    public SweepKind Kind { get; }

    public SweepProfile Profile { get; set; } = SweepProfile.Rectangle;

    /// <summary>A drawn profile to use instead of <see cref="Profile"/>, if any.</summary>
    public Guid? ProfileId { get; set; }

    public double Depth { get; set; } = 20;

    public double Height { get; set; } = 100;

    public Guid MaterialId { get; set; }

    public bool CutsWall { get; set; }

    /// <summary>Whether doors and windows break it.</summary>
    public bool Cuttable { get; set; } = true;

    public double Setback { get; set; }

    public WallSweepType Duplicate(string name) => new(name, Kind)
    {
        Profile = Profile, ProfileId = ProfileId, Depth = Depth, Height = Height, MaterialId = MaterialId,
        CutsWall = CutsWall, Cuttable = Cuttable, Setback = Setback, Description = Description, Cost = Cost
    };

    public override IEnumerable<ParameterValue> GetTypeParameters()
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(SweepParameters.Profile,
            () => EnumText.Humanise(Profile),
            v => { if (EnumText.TryParse<SweepProfile>(v, out var p)) Profile = p; },
            EnumText.Choices<SweepProfile>());
        yield return ParameterValue.Bind(SweepParameters.Depth, () => Depth, v => { if (v > 0) Depth = v; });
        yield return ParameterValue.Bind(SweepParameters.Height, () => Height, v => { if (v > 0) Height = v; });
        yield return ParameterValue.Bind(SweepParameters.Cuttable, () => Cuttable, v => Cuttable = v);
        yield return ParameterValue.Bind(SweepParameters.CutsWall, () => CutsWall, v => CutsWall = v);
        yield return ParameterValue.Bind(SweepParameters.Setback, () => Setback, v => { if (v >= 0) Setback = v; });
    }
}

/// <summary>
/// A sweep or reveal placed on particular walls rather than coming with their type: a skirting
/// along one room, a string course round a front, a pilaster strip at one point along a wall.
///
/// Horizontal, it runs along every wall it is on at one height, carried round the corners where
/// those walls meet. Vertical, it stands full height on its first wall at a distance along it.
/// Its geometry is its walls': it moves when they do, and is built by the same code as the
/// sweeps a wall type carries.
/// </summary>
public sealed class PlacedSweep : Element
{
    public override BuiltInCategory Category => Kind == SweepKind.Reveal ? BuiltInCategory.WallReveals : BuiltInCategory.WallSweeps;

    /// <summary>Set from the type when placed: whether this is a sweep or a reveal.</summary>
    public SweepKind Kind { get; set; } = SweepKind.Sweep;

    /// <summary>The walls it runs along, in order.</summary>
    public List<Guid> HostWallIds { get; } = new();

    public WallSide Side { get; set; } = WallSide.Exterior;

    public bool Vertical { get; set; }

    /// <summary>Horizontal: the height of its bottom above the first wall's base.</summary>
    public double Elevation { get; set; }

    /// <summary>Vertical: how far along the first wall it stands, to its middle.</summary>
    public double Along { get; set; }

    /// <summary>How far off the face, or into it when negative.</summary>
    public double Offset { get; set; }

    public bool Flip { get; set; }

    /// <summary>Whether it turns round an exposed wall end instead of stopping flat, at each end.</summary>
    public bool ReturnAtStart { get; set; }

    public bool ReturnAtEnd { get; set; }

    /// <summary>
    /// What it amounts to on one of its walls, in the terms the wall-type sweeps use - or null
    /// if that wall is not one of its hosts or its type is gone.
    /// </summary>
    public WallSweep? On(BimDocument document, Wall wall)
    {
        if (!HostWallIds.Contains(wall.Id) || document.FindType<WallSweepType>(TypeId) is not { } type) return null;

        var first = document.Walls.FirstOrDefault(w => w.Id == HostWallIds[0]) ?? wall;
        var elevation = first.GetBaseElevation(document) + Elevation - wall.GetBaseElevation(document);
        var isFirst = HostWallIds[0] == wall.Id;
        var isLast = HostWallIds[^1] == wall.Id;

        return new WallSweep(
            type.Kind, type.Profile, Side, type.Depth, type.Height, Math.Max(0, elevation), false, type.MaterialId,
            Offset, Flip, type.Setback, type.CutsWall, type.Cuttable, type.ProfileId,
            Returns: (isFirst && ReturnAtStart) || (isLast && ReturnAtEnd),
            Vertical: Vertical,
            Along: Along);
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(SweepParameters.Walls, () => HostWallIds.Count);
        yield return ParameterValue.ReadOnly(SweepParameters.Orientation, () => Vertical ? "Vertical" : "Horizontal");
        yield return ParameterValue.BindChoice(SweepParameters.Side,
            () => EnumText.Humanise(Side),
            v => { if (EnumText.TryParse<WallSide>(v, out var s)) Side = s; },
            EnumText.Choices<WallSide>());

        if (Vertical)
            yield return ParameterValue.Bind(SweepParameters.DistanceAlong, () => Along, v => Along = v);
        else
            yield return ParameterValue.Bind(SweepParameters.OffsetFromBase, () => Elevation, v => { if (v >= 0) Elevation = v; });

        yield return ParameterValue.Bind(SweepParameters.WallOffset, () => Offset, v => Offset = v);
        yield return ParameterValue.Bind(SweepParameters.Flip, () => Flip, v => Flip = v);

        if (!Vertical)
        {
            yield return ParameterValue.BindChoice(SweepParameters.StartEnd,
                () => ReturnAtStart ? "Return" : "Straight Cut", v => ReturnAtStart = v == "Return", ReturnChoices);
            yield return ParameterValue.BindChoice(SweepParameters.EndEnd,
                () => ReturnAtEnd ? "Return" : "Straight Cut", v => ReturnAtEnd = v == "Return", ReturnChoices);
        }

        yield return ParameterValue.ReadOnly(SweepParameters.Length, () => Length(document));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    private static readonly IReadOnlyList<string> ReturnChoices = new[] { "Straight Cut", "Return" };

    /// <summary>How long it runs: along its walls, or up a wall when vertical.</summary>
    public double Length(BimDocument document)
    {
        var walls = HostWallIds.Select(id => document.Walls.FirstOrDefault(w => w.Id == id)).OfType<Wall>().ToList();
        if (walls.Count == 0) return 0;
        return Vertical ? walls[0].GetHeight(document) : walls.Sum(w => w.Length);
    }
}

public static class SweepParameters
{
    public static readonly ParameterDefinition Profile = new("Profile", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Depth = new("Depth", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Height = new("Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Cuttable = new("Cut by Inserts", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition CutsWall = new("Cuts Wall", ParameterDataType.YesNo, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition Setback = new("Default Setback", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Walls = new("Walls", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Orientation = new("Orientation", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Side = new("Side", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition OffsetFromBase = new("Offset From Wall Base", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition DistanceAlong = new("Distance Along Wall", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition WallOffset = new("Offset From Wall", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Flip = new("Flip Profile", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition StartEnd = new("Start", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition EndEnd = new("End", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
