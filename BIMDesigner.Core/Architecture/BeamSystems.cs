using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A joist layout - Revit's beam system: joists of one type laid side by side across an outline
/// at a spacing, running the way they are told, or across the short way, which is the way a
/// floor spans. Where the floor they carry is opened - a stairwell, a shaft - they stop short of
/// the opening, and trimmers across its ends carry them.
/// </summary>
public sealed class BeamSystem : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.StructuralFraming;

    /// <summary>Its outline in plan, anticlockwise: the faces of what carries the joists' ends.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    /// <summary>Which way its joists run, degrees anticlockwise from east; none for across the short way.</summary>
    public double? Direction { get; set; }

    /// <summary>How far apart its joists are, centre to centre, mm: 400 is usual, 600 for a roof; 16 in (406) in the US.</summary>
    public double Spacing { get; set; } = 400;

    /// <summary>How far above its level its joists' tops are, mm: 0 for under the floor at the level.</summary>
    public double Offset { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(BeamSystemParameters.Level,
            () => document.FindLevel(LevelId)?.Name ?? "<none>",
            v => { if (document.Levels.FirstOrDefault(level => level.Name == v) is { } level) LevelId = level.Id; },
            document.Levels.Select(level => level.Name).ToArray());
        yield return ParameterValue.BindValidated(BeamSystemParameters.Offset, () => Offset, (double v) =>
        {
            if (!double.IsFinite(v) || Math.Abs(v) > 20000) return false;
            Offset = v;
            return true;
        });
        yield return ParameterValue.BindValidated(BeamSystemParameters.Spacing, () => Spacing, (double v) =>
        {
            if (!double.IsFinite(v) || v < 100 || v > 3000) return false;
            Spacing = v;
            return true;
        });
        // Set as a step that puts back exactly what was there - "across the short way" too, not the angle that came to.
        IUndoableCommand Turned(double? to)
        {
            var was = Direction;
            return new SettingCommand("Change Joist Direction", () => Direction = to, () => Direction = was);
        }

        yield return ParameterValue.BindCommand(BeamSystemParameters.Direction, () => BeamSystems.Angle(this), (double v) =>
            double.IsFinite(v) ? Turned(((v % 180) + 180) % 180) : null);
        yield return ParameterValue.BindCommand(BeamSystemParameters.ShortWay, () => Direction is null, (bool v) =>
            Turned(v ? null : BeamSystems.Angle(this)));
        yield return ParameterValue.ReadOnly(BeamSystemParameters.Joists, () => BeamSystems.Joists(document, this).Count);
        yield return ParameterValue.ReadOnly(BeamSystemParameters.Length, () => BeamSystems.Joists(document, this).Sum(joist => joist.From.DistanceTo(joist.To)));
        yield return ParameterValue.ReadOnly(BeamSystemParameters.Span, () => BeamSystems.Joists(document, this).Select(joist => joist.From.DistanceTo(joist.To)).DefaultIfEmpty(0).Max());
        yield return ParameterValue.ReadOnly(BeamSystemParameters.Mass, () => Math.Round(BeamSystems.TypeOf(document, this).MassPerMetre * BeamSystems.Joists(document, this).Sum(joist => joist.From.DistanceTo(joist.To)) / 1000, 1));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class BeamSystemParameters
{
    public static readonly ParameterDefinition Level = new("Reference Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Offset = new("Elevation", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);
    public static readonly ParameterDefinition Spacing = new("Spacing", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Direction = new("Joist Direction", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition ShortWay = new("Across the Short Way", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Joists = new("Joists", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Span = new("Longest Span", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Mass = new("Mass", ParameterDataType.Number, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

/// <summary>Where a joist layout's joists and trimmers are, and what they are as beams.</summary>
public static class BeamSystems
{
    /// <summary>Its joists' type: its own, or the project's C24 47x200 joist.</summary>
    public static BeamType TypeOf(BimDocument document, BeamSystem system) =>
        document.FindType<BeamType>(system.TypeId)
        ?? document.TypesOf<BeamType>().FirstOrDefault(type => type.Name.Contains("47x200", StringComparison.Ordinal))
        ?? document.TypesOf<BeamType>().FirstOrDefault()
        ?? BeamType.Library().First(type => type.Name.Contains("47x200", StringComparison.Ordinal));

    /// <summary>Its joists' tops.</summary>
    public static double Top(BimDocument document, BeamSystem system) => (document.FindLevel(system.LevelId)?.Elevation ?? 0) + system.Offset;

    /// <summary>
    /// The way its joists run, degrees: as told, or across the short way of its outline - along
    /// its first side, or square to it, whichever it is narrower across.
    /// </summary>
    public static double Angle(BeamSystem system)
    {
        if (system.Direction is { } told) return told;
        var outline = system.Boundary;
        if (outline.Count < 3) return 0;
        var first = (outline[1] - outline[0]).NormalisedOrDefault(Vector2D.UnitX);
        double Extent(Vector2D way) => outline.Max(point => (point - outline[0]).Dot(way)) - outline.Min(point => (point - outline[0]).Dot(way));
        var way = Extent(first) <= Extent(first.PerpendicularLeft()) ? first : first.PerpendicularLeft();
        return ((Math.Atan2(way.Y, way.X) * 180 / Math.PI) % 180 + 180) % 180;
    }

    private static Vector2D Way(BeamSystem system)
    {
        var radians = Angle(system) * Math.PI / 180;
        return new Vector2D(Math.Cos(radians), Math.Sin(radians));
    }

    /// <summary>
    /// The openings in the floor its joists carry - the floor or flat roof over it, its top at or
    /// a little above theirs: the stairwells cut in it and the shafts through it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Openings(BimDocument document, BeamSystem system)
    {
        if (system.Boundary.Count < 3) return Array.Empty<IReadOnlyList<Point2D>>();
        var top = Top(document, system);
        var middle = Polygon2D.Centroid(system.Boundary, system.Boundary[0]);
        return document.Elements.OfType<Slab>()
            .Where(slab => slab.Boundary.Count >= 3 && Stairs.IsFloorLike(document, slab) && slab.Contains(middle)
                           && slab.GetTopElevation(document) >= top - 1 && slab.GetTopElevation(document) <= top + 600)
            .SelectMany(slab => Stairs.Holes(document, slab).Concat(Shafts.Holes(document, slab)))
            .Where(hole => hole.Count >= 3)
            .ToList();
    }

    /// <summary>
    /// Its joists, end to end in plan: lines the way they run, as far apart as its spacing - an
    /// equal margin at each side - across its outline, each cut where the outline is; stopped
    /// short of the openings in the floor they carry, and a trimmer across each end of an
    /// opening, from the uncut joist on one side of it to that on the other.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Joists(BimDocument document, BeamSystem system)
    {
        var outline = system.Boundary;
        var joists = new List<(Point2D, Point2D)>();
        if (outline.Count < 3) return joists;

        var type = TypeOf(document, system);
        var way = Way(system);
        var across = way.PerpendicularLeft();
        var origin = outline[0];
        var (low, high) = (outline.Min(point => (point - origin).Dot(across)), outline.Max(point => (point - origin).Dot(across)));
        var (first, last) = (outline.Min(point => (point - origin).Dot(way)), outline.Max(point => (point - origin).Dot(way)));
        var spacing = Math.Max(100, system.Spacing);
        var count = Math.Max(0, (int)Math.Floor((high - low - type.Width) / spacing + 1e-9));
        var margin = (high - low - count * spacing) / 2;

        var openings = Openings(document, system);
        var lines = new List<(double At, List<(Point2D, Point2D)> Pieces)>();
        for (var k = 0; k <= count && k < 2000; k++)
        {
            var at = low + margin + k * spacing;
            var from = origin + across * at + way * (first - 1);
            var to = origin + across * at + way * (last + 1);
            IReadOnlyList<(Point2D From, Point2D To)> pieces = Polygon2D.Clip(from, to, outline, inside: true);
            foreach (var opening in openings) pieces = pieces.SelectMany(piece => Polygon2D.Clip(piece.From, piece.To, opening, inside: false)).ToList();

            // Cut short at an opening, it stops at the face of the trimmer across it, not running on through it.
            bool AtOpening(Point2D point) => openings.Any(opening => Enumerable.Range(0, opening.Count)
                .Any(i => Line2D.DistanceFromSegment(point, opening[i], opening[(i + 1) % opening.Count]) < 1));
            pieces = pieces.Select(piece =>
            {
                var along = (piece.To - piece.From).NormalisedOrDefault(way);
                return (From: AtOpening(piece.From) ? piece.From + along * type.Width : piece.From, To: AtOpening(piece.To) ? piece.To - along * type.Width : piece.To);
            }).Where(piece => (piece.To - piece.From).Dot(way) > 0).ToList();
            lines.Add((at, pieces.Where(piece => piece.From.DistanceTo(piece.To) >= 50).Select(piece => (piece.From, piece.To)).ToList()));
            joists.AddRange(lines[^1].Pieces);
        }

        // A trimmer across each end of each opening, between the joists either side of it that run past it.
        foreach (var opening in openings)
        {
            var (near, far) = (opening.Min(point => (point - origin).Dot(across)), opening.Max(point => (point - origin).Dot(across)));
            var (start, end) = (opening.Min(point => (point - origin).Dot(way)), opening.Max(point => (point - origin).Dot(way)));
            var below = lines.Where(line => line.At < near).Select(line => line.At).DefaultIfEmpty(low).Max();
            var above = lines.Where(line => line.At > far).Select(line => line.At).DefaultIfEmpty(high).Min();
            foreach (var along in new[] { start - type.Width / 2, end + type.Width / 2 })
            {
                if (along <= first || along >= last) continue;
                var trimmer = Polygon2D.Clip(origin + across * below + way * along, origin + across * above + way * along, outline, inside: true);
                joists.AddRange(trimmer.Where(piece => piece.From.DistanceTo(piece.To) >= 50).Select(piece => (piece.From, piece.To)));
            }
        }

        return joists;
    }


    /// <summary>Its joists and trimmers as beams, of its type, their tops at its height - not in the model, but measured and built as beams are.</summary>
    public static IReadOnlyList<Beam> Members(BimDocument document, BeamSystem system)
    {
        var type = TypeOf(document, system);
        return Joists(document, system).Select(joist => new Beam
        {
            LevelId = system.LevelId, TypeId = type.Id, Start = joist.From, End = joist.To,
            StartOffset = system.Offset, EndOffset = system.Offset, Usage = BeamUsage.Joist
        }).ToList();
    }

    /// <summary>Its joists in 3D, as its own: picking one picks the layout.</summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, BeamSystem system) =>
        Members(document, system).Select(member => Beams.Mesh(document, member, system.Id)).Where(mesh => mesh is not null).Select(mesh => mesh!).ToList();
}
