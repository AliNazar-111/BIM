using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>How a detail line is drawn: thin, medium or wide, or dashed as hidden, chained as a centre line, or dashed long as overhead.</summary>
public enum DetailLineStyle
{
    Thin,
    Medium,
    Wide,
    Hidden,
    Centre,
    Overhead
}

/// <summary>What a filled region is filled with: solid black or grey, or hatched - diagonal, close, crossed, level or upright.</summary>
public enum FillPattern
{
    SolidBlack,
    SolidGrey,
    Diagonal,
    DiagonalClose,
    Crosshatch,
    Horizontal,
    Vertical
}

/// <summary>
/// A line drawn in one view and nowhere else - on a storey's plan, or in a drafting view - the
/// linework of a detail, or a mark on a plan no model element makes: never in 3D, a section, a
/// schedule or IFC.
/// </summary>
public sealed class DetailLine : Element
{
    public override BuiltInCategory Category => BuiltInCategory.DetailLines;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    public DetailLineStyle Style { get; set; } = DetailLineStyle.Medium;

    public double Length => Start.DistanceTo(End);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(DetailingParameters.LineStyle, () => Detailing.Name(Style),
            value => Style = Enum.GetValues<DetailLineStyle>().FirstOrDefault(style => Detailing.Name(style) == value), Detailing.LineStyles);
        yield return ParameterValue.ReadOnly(DetailingParameters.Length, () => Length);
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

/// <summary>
/// An area filled with a pattern in one view - the cut materials of a detail, a zone hatched on a
/// plan - its hatching kept the same on the paper whatever the scale the view is drawn at.
/// </summary>
public sealed class FilledRegion : Element
{
    private readonly List<Point2D> _boundary = new();

    public override BuiltInCategory Category => BuiltInCategory.FilledRegions;

    /// <summary>Its corners, anticlockwise.</summary>
    public IReadOnlyList<Point2D> Boundary => _boundary;

    public void SetBoundary(IEnumerable<Point2D> points)
    {
        var replacement = points.ToList();
        _boundary.Clear();
        _boundary.AddRange(replacement);
        if (Polygon2D.SignedArea(_boundary) < 0) _boundary.Reverse();
    }

    public FillPattern Pattern { get; set; } = FillPattern.Diagonal;

    /// <summary>A fill pattern of the project's own it is filled with instead, where it is.</summary>
    public Guid? CustomPatternId { get; set; }

    /// <summary>Whether its edge is drawn round it, or the pattern stands alone.</summary>
    public bool Outlined { get; set; } = true;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(DetailingParameters.Pattern, () => FillPatterns.Find(document, CustomPatternId)?.Name ?? Detailing.Name(Pattern),
            value =>
            {
                if (document.FillPatterns.FirstOrDefault(custom => custom.Name == value) is { } custom) CustomPatternId = custom.Id;
                else (CustomPatternId, Pattern) = (null, Enum.GetValues<FillPattern>().FirstOrDefault(pattern => Detailing.Name(pattern) == value));
            },
            Detailing.Patterns.Concat(document.FillPatterns.Select(custom => custom.Name)).ToList());
        yield return ParameterValue.Bind(DetailingParameters.Outlined, () => Outlined, (bool value) => Outlined = value);
        yield return ParameterValue.ReadOnly(DetailingParameters.Area, () => Polygon2D.Area(Boundary));
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

/// <summary>
/// A drafting view: a sheet of 2D detail of its own, drawn at its own scale with detail lines,
/// filled regions, text and dimensions - a standard detail, a junction drawn larger than the model
/// shows it - placed on a sheet as any view is. What is drawn in it belongs to it, as a plan's
/// notes belong to its storey.
/// </summary>
public sealed class DraftingView : Element
{
    public override BuiltInCategory Category => BuiltInCategory.Views;

    public string Name { get; set; } = "Drafting View 1";

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.Bind(DetailingParameters.ViewName, () => Name, value => { if (!string.IsNullOrWhiteSpace(value)) Name = value.Trim(); });
        yield return ParameterValue.ReadOnly(DetailingParameters.Sheet, () => Callouts.PlacedOn(document, Detailing.Reference(this)) is { } placed ? $"{placed.Detail} / {placed.Sheet}" : "Not placed");
        foreach (var common in GetCommonParameters(document)) yield return common;
    }
}

/// <summary>What a repeating detail draws over and over: brick or block courses, batt or rigid insulation, or timbers at centres.</summary>
public enum RepeatingKind
{
    Brick,
    Block,
    BattInsulation,
    RigidInsulation,
    Timber
}

/// <summary>
/// A detail drawn over and over along a line, in one view and nowhere else - brick courses up a
/// wall in section, a run of blockwork, batt insulation in a stud wall, rigid boards, timbers at
/// their centres - each piece at its true size: so deep across the line, so long along it, one
/// every so far. It is drawn to the left of the line from its start to its end, or to the right
/// where flipped.
/// </summary>
public sealed class RepeatingDetail : Element
{
    public override BuiltInCategory Category => BuiltInCategory.DetailComponents;

    public Point2D Start { get; set; }

    public Point2D End { get; set; }

    public RepeatingKind Kind { get; set; } = RepeatingKind.Brick;

    /// <summary>How deep each piece is across the line, mm.</summary>
    public double Width { get; set; } = 102.5;

    /// <summary>How long each piece is along the line, mm: a brick's height in a course, a timber's thickness.</summary>
    public double Unit { get; set; } = 65;

    /// <summary>One piece every so far along the line, mm: the piece and its joint, or the centres.</summary>
    public double Spacing { get; set; } = 75;

    /// <summary>Drawn to the right of the line rather than the left.</summary>
    public bool Flipped { get; set; }

    public double Length => Start.DistanceTo(End);

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindChoice(DetailingParameters.RepeatingKind, () => Detailing.Name(Kind),
            value => Kind = Enum.GetValues<RepeatingKind>().FirstOrDefault(kind => Detailing.Name(kind) == value), Detailing.RepeatingKinds);
        yield return ParameterValue.BindValidated(DetailingParameters.RepeatingWidth, () => Width, (double value) => Positive(value, v => Width = v));
        if (Kind != RepeatingKind.BattInsulation && Kind != RepeatingKind.RigidInsulation)
            yield return ParameterValue.BindValidated(DetailingParameters.RepeatingUnit, () => Unit, (double value) => Positive(value, v => Unit = Math.Min(v, Spacing)));
        yield return ParameterValue.BindValidated(DetailingParameters.RepeatingSpacing, () => Spacing, (double value) => Positive(value, v => (Spacing, Unit) = (v, Math.Min(Unit, v))));
        yield return ParameterValue.Bind(DetailingParameters.Flipped, () => Flipped, (bool value) => Flipped = value);
        yield return ParameterValue.ReadOnly(DetailingParameters.Length, () => Length);
        yield return ParameterValue.ReadOnly(DetailingParameters.Pieces, () => Detailing.Pieces(this));
        foreach (var common in GetCommonParameters(document)) yield return common;
    }

    private static bool Positive(double value, Action<double> set)
    {
        if (!double.IsFinite(value) || value < 1 || value > 100000) return false;
        set(value);
        return true;
    }
}

public static class DetailingParameters
{
    public static readonly ParameterDefinition LineStyle = new("Line Style", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Length = new("Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Pattern = new("Fill Pattern", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Outlined = new("Outline", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Area = new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition ViewName = new("View Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Sheet = new("Detail / Sheet Number", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition RepeatingKind = new("Detail", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition RepeatingWidth = new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RepeatingUnit = new("Piece Length", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RepeatingSpacing = new("Spacing", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Flipped = new("Flipped", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
    public static readonly ParameterDefinition Pieces = new("Pieces", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Dimensions);
}

/// <summary>Detail lines, filled regions and drafting views: their names, their hatching, and a drafting view made.</summary>
public static class Detailing
{
    /// <summary>The scale a drafting view starts at: a detail, 1:5.</summary>
    public const double DraftingScale = 5;

    public static string Name(DetailLineStyle style) => style switch
    {
        DetailLineStyle.Thin => "Thin",
        DetailLineStyle.Medium => "Medium",
        DetailLineStyle.Wide => "Wide",
        DetailLineStyle.Hidden => "Hidden (dashed)",
        DetailLineStyle.Centre => "Centre (chain)",
        _ => "Overhead (long dash)"
    };

    public static string Name(FillPattern pattern) => pattern switch
    {
        FillPattern.SolidBlack => "Solid black",
        FillPattern.SolidGrey => "Solid grey",
        FillPattern.Diagonal => "Diagonal",
        FillPattern.DiagonalClose => "Diagonal, close",
        FillPattern.Crosshatch => "Crosshatch",
        FillPattern.Horizontal => "Horizontal",
        _ => "Vertical"
    };

    public static readonly IReadOnlyList<string> LineStyles = Enum.GetValues<DetailLineStyle>().Select(Name).ToList();

    public static readonly IReadOnlyList<string> Patterns = Enum.GetValues<FillPattern>().Select(Name).ToList();

    public static string Name(RepeatingKind kind) => kind switch
    {
        RepeatingKind.Brick => "Brick courses",
        RepeatingKind.Block => "Block courses",
        RepeatingKind.BattInsulation => "Batt insulation",
        RepeatingKind.RigidInsulation => "Rigid insulation",
        _ => "Timbers at centres"
    };

    public static readonly IReadOnlyList<string> RepeatingKinds = Enum.GetValues<RepeatingKind>().Select(Name).ToList();

    /// <summary>
    /// A repeating detail's sizes to start from - width across, piece along, spacing - mm: a UK
    /// brick's 102.5 mm leaf in 65 mm courses at 75 (BS EN 771-1's 215 x 102.5 x 65 with a 10 mm
    /// joint), 100 mm blockwork in 215 mm courses at 225; in the US a modular brick's 3-5/8 in leaf,
    /// three courses to 8 in, and an 8 in block in 8 in courses. Batts 100 mm (4 in) thick, rigid
    /// board 50 mm (2 in), timbers 47 x 97 mm (2x4) at 400 mm (16 in) centres.
    /// </summary>
    public static (double Width, double Unit, double Spacing) Defaults(RepeatingKind kind, bool us) => (kind, us) switch
    {
        (RepeatingKind.Brick, false) => (102.5, 65, 75),
        (RepeatingKind.Brick, true) => (92.1, 57.2, 203.2 / 3),
        (RepeatingKind.Block, false) => (100, 215, 225),
        (RepeatingKind.Block, true) => (193.7, 193.7, 203.2),
        (RepeatingKind.BattInsulation, false) => (100, 50, 50),
        (RepeatingKind.BattInsulation, true) => (101.6, 50.8, 50.8),
        (RepeatingKind.RigidInsulation, false) => (50, 50, 50),
        (RepeatingKind.RigidInsulation, true) => (50.8, 50.8, 50.8),
        (_, false) => (97, 47, 400),
        _ => (88.9, 38.1, 406.4)
    };

    /// <summary>How many pieces a repeating detail draws: as many as fit whole along it.</summary>
    public static int Pieces(RepeatingDetail detail) =>
        detail.Spacing <= 0 || detail.Length < Math.Min(detail.Unit, detail.Spacing) ? 0
        : detail.Kind is RepeatingKind.BattInsulation or RepeatingKind.RigidInsulation ? (int)Math.Floor(detail.Length / detail.Spacing + 1e-9)
        : Math.Min(MostPieces, (int)Math.Floor((detail.Length - detail.Unit) / detail.Spacing + 1e-9) + 1);

    /// <summary>The most pieces one repeating detail draws.</summary>
    public const int MostPieces = 5000;

    /// <summary>
    /// What a repeating detail draws, as strokes in the model - each a run of points - so the view,
    /// a PDF and a DWG draw it alike: each brick, block or timber its outline along the line, a
    /// timber crossed corner to corner as cut timber is; rigid board its two faces and a cross in each
    /// board's length; batt insulation the looped line it is drawn in, a loop every spacing, between
    /// its faces.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Strokes(RepeatingDetail detail)
    {
        var strokes = new List<IReadOnlyList<Point2D>>();
        var length = detail.Length;
        if (length < 1 || detail.Width <= 0 || detail.Spacing <= 0) return strokes;

        var along = (detail.End - detail.Start).NormalisedOrDefault(Vector2D.UnitX);
        var across = along.PerpendicularLeft() * (detail.Flipped ? -1 : 1);
        Point2D At(double a, double c) => detail.Start + along * a + across * c;
        var (width, count) = (detail.Width, Pieces(detail));

        switch (detail.Kind)
        {
            case RepeatingKind.BattInsulation:
            {
                // A prolate cycloid: rolling along, a point beyond the wheel's rim traces a loop each turn.
                var pitch = detail.Spacing;
                var (r, d) = (pitch / (2 * Math.PI), width / 2 * 0.92);
                var turns = Math.Min(MostPieces, length / pitch);
                var points = new List<Point2D>();
                const int perTurn = 28;
                for (var i = 0; i <= (int)Math.Ceiling(turns * perTurn); i++)
                {
                    var t = Math.Min(turns, i / (double)perTurn) * 2 * Math.PI;
                    var a = Math.Clamp(r * t - d * Math.Sin(t) + d * 0.35, 0, length);
                    points.Add(At(a, width / 2 - d * Math.Cos(t)));
                }

                strokes.Add(points);
                break;
            }

            case RepeatingKind.RigidInsulation:
                strokes.Add(new[] { At(0, 0), At(length, 0) });
                strokes.Add(new[] { At(0, width), At(length, width) });
                for (var i = 0; i < count; i++)
                {
                    var (a, b) = (i * detail.Spacing, (i + 1) * detail.Spacing);
                    strokes.Add(new[] { At(a, 0), At(b, width) });
                    strokes.Add(new[] { At(a, width), At(b, 0) });
                    strokes.Add(new[] { At(b, 0), At(b, width) });
                }

                strokes.Add(new[] { At(0, 0), At(0, width) });
                break;

            default:
                for (var i = 0; i < count; i++)
                {
                    var (a, b) = (i * detail.Spacing, i * detail.Spacing + detail.Unit);
                    strokes.Add(new[] { At(a, 0), At(b, 0), At(b, width), At(a, width), At(a, 0) });
                    if (detail.Kind == RepeatingKind.Timber)
                    {
                        strokes.Add(new[] { At(a, 0), At(b, width) });
                        strokes.Add(new[] { At(a, width), At(b, 0) });
                    }
                }

                break;
        }

        return strokes;
    }

    public static ViewReference Reference(DraftingView view) => ViewReference.Drafting(view.Id);

    public static string NextName(BimDocument document)
    {
        var taken = document.All<DraftingView>().Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; ; i++)
            if (!taken.Contains($"Drafting View {i}")) return $"Drafting View {i}";
    }

    /// <summary>A drafting view made, at a detail's scale. One step to undo.</summary>
    public static (DraftingView View, IUndoableCommand Command) Make(BimDocument document, string? name = null)
    {
        var view = new DraftingView { Name = string.IsNullOrWhiteSpace(name) ? NextName(document) : name.Trim() };
        return (view, new CompositeCommand("Drafting View", new IUndoableCommand[]
        {
            new AddElementCommand(document, view, "Drafting View"),
            new SetViewScaleCommand(document, Reference(view), DraftingScale)
        }));
    }

    /// <summary>
    /// A drafting view copied with everything drawn in it, at its scale, named "... Copy" - to
    /// change into another detail. One step to undo.
    /// </summary>
    public static (DraftingView View, IUndoableCommand Command) Duplicate(BimDocument document, DraftingView view)
    {
        var taken = document.All<DraftingView>().Select(each => each.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = $"{view.Name} Copy";
        for (var i = 2; taken.Contains(name); i++) name = $"{view.Name} Copy {i}";

        var copy = new DraftingView { Name = name, Comments = view.Comments };
        var drawn = ElementCopy.Duplicate(document, document.Elements.Where(element => element.LevelId == view.Id).ToList());
        foreach (var element in drawn) element.LevelId = copy.Id;

        var steps = new List<IUndoableCommand>
        {
            new AddElementCommand(document, copy, "Drafting View"),
            new SetViewScaleCommand(document, Reference(copy), document.ViewSettings.ScaleOf(Reference(view)))
        };
        steps.AddRange(drawn.Select(element => new AddElementCommand(document, element, "Drafting View")));
        return (copy, new CompositeCommand("Duplicate Drafting View", steps));
    }

    /// <summary>A drafting view deleted with everything drawn in it. One step to undo; a sheet it is on keeps its place, empty.</summary>
    public static IUndoableCommand Delete(BimDocument document, DraftingView view)
    {
        var steps = document.Elements.Where(element => element.LevelId == view.Id)
            .Select(element => (IUndoableCommand)new DeleteElementCommand(document, element)).ToList();
        steps.Add(new DeleteElementCommand(document, view));
        return new CompositeCommand("Delete Drafting View", steps);
    }

    /// <summary>The lines of a pattern, how far apart on the paper and at what angles: none for a solid fill.</summary>
    public static IReadOnlyList<(double Degrees, double Spacing)> Lines(FillPattern pattern) => pattern switch
    {
        FillPattern.Diagonal => new[] { (45.0, 2.0) },
        FillPattern.DiagonalClose => new[] { (45.0, 1.0) },
        FillPattern.Crosshatch => new[] { (45.0, 2.0), (-45.0, 2.0) },
        FillPattern.Horizontal => new[] { (0.0, 2.0) },
        FillPattern.Vertical => new[] { (90.0, 2.0) },
        _ => Array.Empty<(double, double)>()
    };

    /// <summary>
    /// The hatching of an outline as lines cut to it: each set of parallel lines at its angle and
    /// spacing - in model millimetres, the paper's spacing times the scale - laid across the outline
    /// and kept where they are inside it, whatever its shape. Drawn as lines, it prints, plots and
    /// goes to a DWG the same.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Hatch(IReadOnlyList<Point2D> outline, double degrees, double spacing)
    {
        var segments = new List<(Point2D, Point2D)>();
        if (outline.Count < 3 || spacing <= 0) return segments;

        var turn = degrees * Math.PI / 180;
        var along = new Vector2D(Math.Cos(turn), Math.Sin(turn));
        var across = new Vector2D(-along.Y, along.X);
        double Across(Point2D point) => point.X * across.X + point.Y * across.Y;
        double Along(Point2D point) => point.X * along.X + point.Y * along.Y;

        var (low, high) = (outline.Min(Across), outline.Max(Across));
        if ((high - low) / spacing > 20000) return segments;

        for (var offset = Math.Ceiling(low / spacing) * spacing; offset <= high; offset += spacing)
        {
            var crossings = new List<double>();
            for (var i = 0; i < outline.Count; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                var (sa, sb) = (Across(a) - offset, Across(b) - offset);
                if (sa <= 0 == sb <= 0) continue;
                var at = a + (b - a) * (sa / (sa - sb));
                crossings.Add(Along(at));
            }

            crossings.Sort();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                Point2D Point(double t) => new(across.X * offset + along.X * t, across.Y * offset + along.Y * t);
                segments.Add((Point(crossings[i]), Point(crossings[i + 1])));
            }
        }

        return segments;
    }
}
