using System.Globalization;
using System.Text;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>
/// One family of parallel lines in a fill pattern, as a .pat file gives it: the lines' angle in
/// degrees, a point one of them passes through, the step from one line to the next - along the
/// lines, then square to them - and the dashes each is drawn in, in turn: a positive length drawn,
/// a negative one left, nought a dot. No dashes, the lines are drawn whole. In millimetres.
/// </summary>
public sealed record PatternLine(double Angle, double OriginX, double OriginY, double Shift, double Spacing, IReadOnlyList<double> Dashes);

/// <summary>
/// A fill pattern of the project's own: made in it, or read from a .pat file. A drafting pattern
/// is sized on the paper, so it reads alike at every scale; a model pattern is sized in the
/// building - a brick course - so it grows and shrinks with the scale as the building does.
/// </summary>
public sealed class CustomFillPattern
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Pattern";

    public string Description { get; set; } = string.Empty;

    /// <summary>Sized in the building rather than on the paper.</summary>
    public bool Model { get; set; }

    public List<PatternLine> Lines { get; } = new();
}

/// <summary>
/// Fill patterns of one's own: a simple one made - lines at an angle and a spacing, crossed or not -
/// and patterns read from and written to .pat files, the hatch pattern files drawing programs share:
/// "*NAME, description" and under it one line family a line, "angle, x-origin, y-origin, delta-x,
/// delta-y, dash, dash ...", with ";%UNITS=INCH" or "MM" and ";%TYPE=MODEL" or "DRAFTING" saying what
/// they are sized in and against.
/// </summary>
public static class FillPatterns
{
    /// <summary>The most lines a region's hatching is drawn with: past that, the pattern is too fine to draw at this scale.</summary>
    public const int MostSegments = 200_000;

    public static CustomFillPattern? Find(BimDocument document, Guid? id) =>
        id is { } wanted ? document.FillPatterns.FirstOrDefault(pattern => pattern.Id == wanted) : null;

    /// <summary>A simple pattern: lines at an angle and a spacing, and as many again square to them where crossed.</summary>
    public static CustomFillPattern Simple(string name, double degrees, double spacing, bool crossed, bool model)
    {
        var pattern = new CustomFillPattern { Name = name.Trim(), Model = model, Description = crossed ? "Crossed lines" : "Parallel lines" };
        pattern.Lines.Add(new PatternLine(degrees, 0, 0, 0, spacing, Array.Empty<double>()));
        if (crossed) pattern.Lines.Add(new PatternLine(degrees + 90, 0, 0, 0, spacing, Array.Empty<double>()));
        return pattern;
    }

    /// <summary>Patterns added to the project, as one step to undo; a name already used is numbered on.</summary>
    public static IUndoableCommand Add(BimDocument document, IReadOnlyList<CustomFillPattern> patterns)
    {
        foreach (var pattern in patterns)
        {
            var taken = document.FillPatterns.Select(each => each.Name).Concat(Detailing.Patterns).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = pattern.Name;
            for (var i = 2; taken.Contains(name); i++) name = $"{pattern.Name} {i}";
            pattern.Name = name;
        }

        return new DelegateCommand(patterns.Count == 1 ? "Fill Pattern" : $"{patterns.Count} Fill Patterns",
            () => document.FillPatterns.AddRange(patterns),
            () => document.FillPatterns.RemoveAll(patterns.Contains));
    }

    /// <summary>A pattern taken out of the project, as one step to undo: the regions filled with it go back to their own pattern.</summary>
    public static IUndoableCommand Remove(BimDocument document, CustomFillPattern pattern)
    {
        var filled = document.All<FilledRegion>().Where(region => region.CustomPatternId == pattern.Id).ToList();
        var at = document.FillPatterns.IndexOf(pattern);
        return new DelegateCommand("Delete Fill Pattern",
            () =>
            {
                document.FillPatterns.Remove(pattern);
                foreach (var region in filled) region.CustomPatternId = null;
            },
            () =>
            {
                document.FillPatterns.Insert(Math.Clamp(at, 0, document.FillPatterns.Count), pattern);
                foreach (var region in filled) region.CustomPatternId = pattern.Id;
            });
    }

    // ---- .pat files ---------------------------------------------------------------------

    /// <summary>
    /// The patterns in a .pat file, in millimetres - from inches where it says so, or where told the
    /// file is in inches - and the lines it could not read, each said.
    /// </summary>
    public static IReadOnlyList<CustomFillPattern> Read(string text, out IReadOnlyList<string> problems, bool inches = false, bool model = false)
    {
        var patterns = new List<CustomFillPattern>();
        var said = new List<string>();
        CustomFillPattern? current = null;
        var number = 0;

        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith(';'))
            {
                var setting = line.TrimStart(';').Trim().Replace(" ", string.Empty).ToUpperInvariant();
                if (setting == "%UNITS=INCH") inches = true;
                else if (setting == "%UNITS=MM") inches = false;
                else if (setting.StartsWith("%TYPE=", StringComparison.Ordinal))
                {
                    var isModel = setting == "%TYPE=MODEL";
                    if (current is not null && current.Lines.Count == 0) current.Model = isModel;
                    else model = isModel;
                }

                continue;
            }

            if (line.StartsWith('*'))
            {
                var comma = line.IndexOf(',');
                var name = (comma < 0 ? line[1..] : line[1..comma]).Trim();
                current = new CustomFillPattern { Name = name.Length > 0 ? name : $"Pattern {patterns.Count + 1}", Description = comma < 0 ? string.Empty : line[(comma + 1)..].Trim(), Model = model };
                patterns.Add(current);
                continue;
            }

            if (current is null)
            {
                said.Add($"Line {number}: a line family before any *NAME line.");
                continue;
            }

            // Anything after a comment mark on the line is not the pattern's.
            var data = line.Split(';')[0];
            var values = data.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
            var numbers = new List<double>();
            foreach (var value in values)
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)) numbers.Add(parsed);
                else break;

            if (numbers.Count < 5 || numbers.Count < values.Count)
            {
                said.Add($"Line {number} of {current.Name}: not \"angle, x, y, delta-x, delta-y\" and its dashes.");
                continue;
            }

            var factor = inches ? 25.4 : 1;
            if (Math.Abs(numbers[4]) * factor < 1e-6)
            {
                said.Add($"Line {number} of {current.Name}: its lines are no distance apart.");
                continue;
            }

            current.Lines.Add(new PatternLine(numbers[0], numbers[1] * factor, numbers[2] * factor, numbers[3] * factor, numbers[4] * factor,
                numbers.Skip(5).Select(dash => dash * factor).ToList()));
        }

        foreach (var empty in patterns.Where(pattern => pattern.Lines.Count == 0).ToList())
        {
            said.Add($"{empty.Name}: no line families - left out.");
            patterns.Remove(empty);
        }

        problems = said;
        return patterns;
    }

    /// <summary>Patterns written as a .pat file, in millimetres, each saying whether it is a model or a drafting pattern.</summary>
    public static string Write(IEnumerable<CustomFillPattern> patterns)
    {
        static string Number(double value) => Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.AppendLine(";%UNITS=MM");
        foreach (var pattern in patterns)
        {
            text.AppendLine($"*{pattern.Name.Replace(",", " ")}{(pattern.Description.Length > 0 ? $", {pattern.Description}" : string.Empty)}");
            text.AppendLine($";%TYPE={(pattern.Model ? "MODEL" : "DRAFTING")}");
            foreach (var line in pattern.Lines)
                text.AppendLine(string.Join(", ", new[] { line.Angle, line.OriginX, line.OriginY, line.Shift, line.Spacing }.Concat(line.Dashes).Select(Number)));
        }

        return text.ToString();
    }

    // ---- hatching -------------------------------------------------------------------------

    /// <summary>
    /// A pattern's hatching of an outline as lines cut to it, each family laid across it at its
    /// angle and step and drawn in its dashes - a dot as a short stroke - at a scale: model
    /// millimetres to the pattern's, the view's scale for a drafting pattern, 1 for a model one.
    /// </summary>
    public static IReadOnlyList<(Point2D From, Point2D To)> Hatch(IReadOnlyList<Point2D> outline, CustomFillPattern pattern, double scale, double dot)
    {
        var segments = new List<(Point2D, Point2D)>();
        foreach (var line in pattern.Lines)
        {
            Hatch(segments, outline, line, scale, dot);
            if (segments.Count > MostSegments) return Array.Empty<(Point2D, Point2D)>();
        }

        return segments;
    }

    private static void Hatch(List<(Point2D, Point2D)> segments, IReadOnlyList<Point2D> outline, PatternLine family, double scale, double dot)
    {
        if (outline.Count < 3 || scale <= 0) return;

        // Square to the lines, each the same step further on; a step back across is the same lines the other way.
        var (shift, spacing) = family.Spacing < 0 ? (-family.Shift * scale, -family.Spacing * scale) : (family.Shift * scale, family.Spacing * scale);
        var turn = family.Angle * Math.PI / 180;
        var along = new Vector2D(Math.Cos(turn), Math.Sin(turn));
        var across = new Vector2D(-along.Y, along.X);
        var origin = new Point2D(family.OriginX * scale, family.OriginY * scale);
        double Across(Point2D point) => point.X * across.X + point.Y * across.Y;
        double Along(Point2D point) => point.X * along.X + point.Y * along.Y;

        var (low, high) = (outline.Min(Across), outline.Max(Across));
        var first = Math.Ceiling((low - Across(origin)) / spacing);
        var last = Math.Floor((high - Across(origin)) / spacing);
        if (last - first > 20000) return;

        var dashes = family.Dashes.Select(dash => dash * scale).ToList();
        var period = dashes.Sum(Math.Abs);
        for (var k = first; k <= last; k++)
        {
            // This line, and where along it its dashes start.
            var start = origin + along * (shift * k) + across * (spacing * k);
            var offset = Across(start);
            var crossings = new List<double>();
            for (var i = 0; i < outline.Count; i++)
            {
                var (a, b) = (outline[i], outline[(i + 1) % outline.Count]);
                var (sa, sb) = (Across(a) - offset, Across(b) - offset);
                if (sa <= 0 == sb <= 0) continue;
                crossings.Add(Along(a + (b - a) * (sa / (sa - sb))));
            }

            crossings.Sort();
            Point2D Point(double t) => new(across.X * offset + along.X * t, across.Y * offset + along.Y * t);
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var (from, to) = (crossings[i], crossings[i + 1]);
                if (dashes.Count == 0 || period < 1e-9)
                {
                    segments.Add((Point(from), Point(to)));
                    continue;
                }

                // The dashes in turn from the repeat before this stretch begins, kept where they fall in it.
                var at = Along(start) + Math.Floor((from - Along(start)) / period) * period;
                if ((to - at) / period * dashes.Count > 50000) continue;
                while (at < to)
                {
                    foreach (var dash in dashes)
                    {
                        var length = Math.Abs(dash);
                        var (a, b) = dash > 0 ? (at, at + length) : dash == 0 ? (at, at + dot) : (0.0, 0.0);
                        if (b > a && b > from && a < to) segments.Add((Point(Math.Max(a, from)), Point(Math.Min(b, to))));
                        at += length;
                        if (at >= to) break;
                    }
                }

                if (segments.Count > MostSegments) return;
            }
        }
    }
}
