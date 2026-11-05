using System.Globalization;
using System.Text.RegularExpressions;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Steel set in concrete as it is written on a drawing: a number of bars - "3 H12", "2 #5" - bars at
/// centres - "H12 @ 200", "#4 @ 12 in" - or a fabric - "A393" - with each bar's diameter and area.
/// UK bars are BS 4449 grade B500 ("H" in BS 8666), diameters 6 to 40 mm; US bars ASTM A615 grade 60,
/// #3 to #11, an eighth of an inch to the number; fabric BS 4483.
/// </summary>
public sealed record BarSpec(int? Count, string Size, double Diameter, double? Spacing, string? Fabric)
{
    /// <summary>The bars' or fabric's main wires' area, mm² each.</summary>
    public double BarArea => Math.PI * Diameter * Diameter / 4;

    /// <summary>A fabric's area per metre width, each way: its main wires, then its cross wires, mm²/m.</summary>
    public (double Main, double Cross)? FabricArea => Fabric is { } name && Reinforcement.Fabrics.TryGetValue(name, out var fabric) ? (fabric.Main, fabric.Cross) : null;

    public bool IsFabric => Fabric is not null;

    public override string ToString() =>
        Fabric ?? (Count is { } count ? $"{count} {Size}" : $"{Size} @ {(Size.StartsWith('#') ? $"{Spacing / 25.4:0.#} in" : $"{Spacing:0}")}");

    private static readonly Regex Counted = new(@"^\s*(\d+)\s*(H|T|B|#)\s*(\d+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex Spaced = new(@"^\s*(H|T|B|#)\s*(\d+)\s*@\s*(\d+(?:\.\d+)?)\s*(in|mm)?\s*(?:c/c|crs|o\.?c\.?)?\s*$", RegexOptions.IgnoreCase);

    /// <summary>A spec read from what is written; null where it is none of them.</summary>
    public static BarSpec? Parse(string text)
    {
        var trimmed = text.Trim();
        if (Reinforcement.Fabrics.TryGetValue(trimmed.ToUpperInvariant(), out var fabric))
            return new BarSpec(null, trimmed.ToUpperInvariant(), fabric.Wire, fabric.Pitch, trimmed.ToUpperInvariant());

        if (Counted.Match(trimmed) is { Success: true } counted && Bar(counted.Groups[2].Value, counted.Groups[3].Value) is var (size, diameter))
            return new BarSpec(int.Parse(counted.Groups[1].Value, CultureInfo.InvariantCulture), size, diameter, null, null);

        if (Spaced.Match(trimmed) is { Success: true } spaced && Bar(spaced.Groups[1].Value, spaced.Groups[2].Value) is var (barSize, barDiameter))
        {
            var spacing = double.Parse(spaced.Groups[3].Value, CultureInfo.InvariantCulture);
            var inches = spaced.Groups[4].Value.Equals("in", StringComparison.OrdinalIgnoreCase) || barSize.StartsWith('#');
            if (inches) spacing *= 25.4;
            if (spacing < 50 || spacing > 1000) return null;
            return new BarSpec(null, barSize, barDiameter, spacing, null);
        }

        return null;
    }

    /// <summary>A bar size as written and its diameter, mm; null for one not made.</summary>
    private static (string Size, double Diameter)? Bar(string prefix, string number)
    {
        if (!int.TryParse(number, out var n)) return null;
        if (prefix == "#") return n is >= 3 and <= 11 ? ($"#{n}", Reinforcement.UsDiameter(n)) : null;
        return Reinforcement.UkDiameters.Contains(n) ? ($"H{n}", n) : null;
    }
}

/// <summary>One line of a bar schedule (BS 8666 Table 2): the member, its bar mark, type and size, how many members, bars in each and in all, each bar's length, its shape code and dimensions.</summary>
public sealed record BarLine(string Member, string Mark, string Size, int Members, int Each, double Length, string Shape, double A, double B = 0, double C = 0, double D = 0)
{
    public int Total => Members * Each;
}

/// <summary>
/// The steel in the foundations: what each footing, raft, ground beam and pile holds, as bars and
/// fabric, scheduled as BS 8666 sets a bar schedule out - lengths rounded up to the next 25 mm, shape
/// codes 00 straight and 51 closed link - and weighed; cover to the bars as BS EN 1992-1-1 (4.4.1.3(4))
/// and ACI 318-19 (Table 20.5.1.3.1) ask of concrete cast against the ground.
/// </summary>
public static class Reinforcement
{
    /// <summary>The bar diameters made to BS 4449, mm.</summary>
    public static readonly IReadOnlyList<int> UkDiameters = new[] { 6, 8, 10, 12, 16, 20, 25, 32, 40 };

    /// <summary>An ASTM A615 bar's nominal diameter, mm: #3 to #8 an eighth of an inch to the number; #9 to #11 as the standard tabulates.</summary>
    public static double UsDiameter(int number) => number switch
    {
        9 => 1.128 * 25.4,
        10 => 1.270 * 25.4,
        11 => 1.410 * 25.4,
        _ => number / 8.0 * 25.4
    };

    /// <summary>BS 4483 fabric: its wire and pitch each way, and its area per metre each way, mm and mm²/m.</summary>
    public static readonly IReadOnlyDictionary<string, (double Wire, double Pitch, double Main, double Cross)> Fabrics =
        new Dictionary<string, (double, double, double, double)>
        {
            ["A142"] = (6, 200, 142, 142),
            ["A193"] = (7, 200, 193, 193),
            ["A252"] = (8, 200, 252, 252),
            ["A393"] = (10, 200, 393, 393),
            ["B385"] = (7, 100, 385, 193),
            ["B503"] = (8, 100, 503, 252),
            ["B785"] = (10, 100, 785, 252),
            ["B1131"] = (12, 100, 1131, 252)
        };

    /// <summary>The cover to the bars in concrete cast against the ground, mm: 75 (BS EN 1992-1-1 4.4.1.3(4); ACI 318 3 in).</summary>
    public const double CoverAgainstGround = 75;

    /// <summary>The cover in concrete cast on blinding, mm: 40 at the least, 50 as is usual (BS EN 1992-1-1 4.4.1.3(4)).</summary>
    public const double CoverOnBlinding = 50;

    /// <summary>How long bars are made, mm: what longer runs are lapped from.</summary>
    public const double StockLength = 12000;

    /// <summary>A lap between bars, in their diameters: 40, as is usual for a tension lap in footings.</summary>
    public const double LapDiameters = 40;

    /// <summary>A length as BS 8666 schedules it: rounded up to the next 25 mm.</summary>
    public static double Scheduled(double length) => Math.Ceiling(length / 25) * 25;

    /// <summary>A bar's mass per metre, kg: its section at 7,850 kg/m³ - 0.00617 d², d in mm, as BS 8666 Table 1 has it to the gram.</summary>
    public static double KilogramsPerMetre(double diameter) => Math.PI / 4 * diameter * diameter * 7850 / 1e6;

    /// <summary>A run of bars of a length: how many bars, each how long, lapped where it is longer than stock.</summary>
    private static (int Bars, double Each) Lapped(double length, double diameter)
    {
        if (length <= StockLength) return (1, length);
        var lap = LapDiameters * diameter;
        var bars = (int)Math.Ceiling((length - lap) / (StockLength - lap));
        return (bars, (length + (bars - 1) * lap) / bars);
    }

    /// <summary>How many bars at centres across a width: the first and last at the cover, one at least.</summary>
    public static int AcrossAt(double width, double spacing, double cover) => Math.Max(1, (int)Math.Floor((width - 2 * cover) / spacing) + 1);

    /// <summary>Every bar line of the foundations, member by member, marked 01 on.</summary>
    public static IReadOnlyList<BarLine> Schedule(BimDocument document)
    {
        var lines = new List<BarLine>();
        var mark = 0;
        string Next() => (++mark).ToString("00");

        foreach (var element in document.Elements)
            lines.AddRange(LinesOf(document, element, Next));
        return lines;
    }

    /// <summary>A member's bar lines.</summary>
    public static IReadOnlyList<BarLine> LinesOf(BimDocument document, Element element, Func<string> mark)
    {
        var lines = new List<BarLine>();
        var cover = CoverAgainstGround;
        var member = Footings.MemberName(document, element);
        void Straight(BarSpec spec, int count, double length)
        {
            var (bars, each) = Lapped(Math.Max(0, length - 2 * cover), spec.Diameter);
            lines.Add(new BarLine(member, mark(), spec.Size, 1, count * bars, Scheduled(each), "00", Scheduled(each)));
        }

        void Links(BarSpec spec, double length, double width, double depth)
        {
            if (spec.Spacing is not { } spacing) return;
            var (a, b) = (width - 2 * cover, depth - 2 * cover);
            if (a <= 0 || b <= 0) return;
            var count = AcrossAt(length, spacing, cover);
            // Shape code 51: a closed link, its two sides and the hooks that close it.
            var each = 2 * (a + b) + 20 * spec.Diameter;
            lines.Add(new BarLine(member, mark(), spec.Size, 1, count, Scheduled(each), "51", Scheduled(a), Scheduled(b)));
        }

        void Mesh(BarSpec spec, double length, double width)
        {
            if (spec.IsFabric || spec.Spacing is not { } spacing) return;
            Straight(spec, AcrossAt(width, spacing, cover), length);
            Straight(spec, AcrossAt(length, spacing, cover), width);
        }

        switch (element)
        {
            case WallFooting strip when strip.Reinforced && BarSpec.Parse(strip.Bars) is { } bars && Footings.Outline(document, strip) is { } outline
                                        && document.FindType<FootingType>(strip.TypeId) is { } type:
            {
                var run = outline[0].DistanceTo(outline[1]);
                if (bars.Count is { } n) Straight(bars, n, run);
                else if (bars.Spacing is not null) Mesh(bars, run, type.Width);
                break;
            }

            case PadFooting pad when pad.Reinforced && BarSpec.Parse(pad.Bars) is { } bars && document.FindType<FootingType>(pad.TypeId) is { } type:
                if (bars.Spacing is { } spacing)
                {
                    Straight(bars, AcrossAt(type.Length, spacing, cover), type.Width);
                    Straight(bars, AcrossAt(type.Width, spacing, cover), type.Length);
                }
                else if (bars.Count is { } count)
                {
                    Straight(bars, count, type.Width);
                    Straight(bars, count, type.Length);
                }

                break;

            case GroundBeam beam when Piles.Outline(document, beam) is { } outline:
            {
                var length = outline[0].DistanceTo(outline[1]);
                if (BarSpec.Parse(beam.TopBars) is { Count: { } top } topBars) Straight(topBars, top, length);
                if (BarSpec.Parse(beam.BottomBars) is { Count: { } bottom } bottomBars) Straight(bottomBars, bottom, length);
                if (BarSpec.Parse(beam.Links) is { } links) Links(links, length, beam.Width, beam.Depth);
                break;
            }

            case Pile pile when BarSpec.Parse(pile.Bars) is { Count: { } count } bars && pile.CageLength > 0:
                lines.Add(new BarLine(member, mark(), bars.Size, 1, count, Scheduled(Math.Min(pile.CageLength, pile.Length - cover)), "00", Scheduled(Math.Min(pile.CageLength, pile.Length - cover))));
                break;

            case RaftFoundation raft:
            {
                var perimeter = Polygon2D.Perimeter(raft.Outline);
                if (BarSpec.Parse(raft.EdgeBars) is { Count: { } count } edge) Straight(edge, count, perimeter + 2 * cover);
                if (BarSpec.Parse(raft.EdgeLinks) is { } links) Links(links, perimeter, raft.EdgeWidth, raft.EdgeDepth);
                var box = BoundingBox2D.Around(raft.Outline);
                foreach (var face in new[] { raft.TopReinforcement, raft.BottomReinforcement })
                    if (BarSpec.Parse(face) is { Spacing: not null, IsFabric: false } bars) Mesh(bars, box.Width, box.Height);
                break;
            }
        }

        return lines;
    }

    /// <summary>The fabric in the foundations: each raft face's, its area, and the sheets of 4.8 by 2.4 m it takes lapped 200 each way.</summary>
    public static IReadOnlyList<(string Member, string Fabric, double Area, int Sheets)> FabricOf(BimDocument document)
    {
        var fabric = new List<(string, string, double, int)>();
        foreach (var raft in document.All<RaftFoundation>())
            foreach (var (face, where) in new[] { (raft.TopReinforcement, "top"), (raft.BottomReinforcement, "bottom") })
                if (BarSpec.Parse(face) is { IsFabric: true } spec)
                {
                    var area = Polygon2D.Area(raft.Outline);
                    var sheet = (4800 - 200) * (2400 - 200);
                    fabric.Add(($"{Footings.MemberName(document, raft)}, {where}", spec.Fabric!, area, (int)Math.Ceiling(area / sheet)));
                }

        return fabric;
    }

    /// <summary>The steel in a member as a drawing notes it - "A393 top; A393 bottom; 4 H12 thickenings" - empty where it has none.</summary>
    public static string Said(Element element)
    {
        static string Join(params (string Bars, string Where)[] faces) =>
            string.Join("; ", faces.Where(face => face.Bars.Length > 0).Select(face => $"{face.Bars} {face.Where}"));
        return element switch
        {
            WallFooting { Reinforced: true } strip => strip.Bars,
            PadFooting { Reinforced: true } pad => $"{pad.Bars} each way",
            RaftFoundation raft => Join((raft.TopReinforcement, "top"), (raft.BottomReinforcement, "bottom"), (raft.EdgeBars, "thickenings"), (raft.EdgeLinks, "links")),
            GroundBeam beam => Join((beam.TopBars, "top"), (beam.BottomBars, "bottom"), (beam.Links, "links")),
            Pile { Bars.Length: > 0 } pile => $"{pile.Bars}, {Units.FormatMm(pile.CageLength)} down",
            _ => string.Empty
        };
    }

    /// <summary>The fabric in a member, kg: each face's sheets, lapped, at the fabric's mass - its wires' area each way at 7,850 kg/m³.</summary>
    public static double FabricMass(BimDocument document, Element element)
    {
        if (element is not RaftFoundation raft) return 0;
        var sheet = 4.8 * 2.4;
        return FabricOf(document).Where(entry => entry.Member.StartsWith(Footings.MemberName(document, raft) + ",", StringComparison.Ordinal))
            .Sum(entry => Fabrics.TryGetValue(entry.Fabric, out var fabric) ? entry.Sheets * sheet * (fabric.Main + fabric.Cross) * 7850 / 1e6 : 0);
    }

    /// <summary>
    /// A rough rate to start a foundation with no type of its own from - its concrete, placed with its
    /// formwork, by the cubic metre; a pile by the metre bored and concreted - to be priced properly.
    /// </summary>
    public static decimal ConcreteRate(Element element, bool us) => element switch
    {
        RaftFoundation or GroundBeam => us ? 300m : 190m,
        Pile => us ? 110m : 85m,
        _ => 0m
    };

    /// <summary>A rough rate for steel in foundations, cut, bent and fixed, by the kilogram.</summary>
    public static decimal SteelRate(bool us) => us ? 2.2m : 1.9m;

    // ---- the schedule ---------------------------------------------------------------------

    /// <summary>The bar schedule, as a viewport refers to it.</summary>
    public static Sheets.ViewReference View() => new(Sheets.ViewKind.BarSchedule, Guid.Empty, string.Empty);

    public static string Title(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() ? "Bar List - Foundations" : "Bar Schedule - Foundations";

    /// <summary>A bar's shape as a schedule names it: BS 8666's code, or in the US the bend in words.</summary>
    public static string ShapeSaid(string code, bool us) => !us ? code : code switch
    {
        "00" => "Straight",
        "51" => "Closed tie",
        _ => code
    };

    /// <summary>
    /// The bar schedule as a table for a sheet: member by member, each bar mark's type and size, how
    /// many, its length and shape code and dimensions, set out as BS 8666 Table 2 sets a schedule out
    /// (in the US as a bar list, lengths in feet and inches) - then each size's mass and the fabric.
    /// </summary>
    public static Schedules.ScheduleTable Table(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        string Length(double mm) => mm <= 0 ? string.Empty : us ? Units.FormatLength(mm) : mm.ToString("0", CultureInfo.InvariantCulture);
        var lines = Schedule(document);
        var rows = new List<(Schedules.ScheduleLineKind Kind, IReadOnlyList<string> Cells)>();
        foreach (var line in lines)
            rows.Add((Schedules.ScheduleLineKind.Row, new[]
            {
                line.Member, line.Mark, line.Size, line.Members.ToString(CultureInfo.InvariantCulture), line.Each.ToString(CultureInfo.InvariantCulture),
                line.Total.ToString(CultureInfo.InvariantCulture), Length(line.Length), ShapeSaid(line.Shape, us), Length(line.A), Length(line.B)
            }));

        foreach (var size in lines.GroupBy(line => line.Size).OrderBy(group => BarSpec.Parse($"1 {group.Key}")?.Diameter ?? 0))
            rows.Add((Schedules.ScheduleLineKind.Total, new[]
            {
                $"{size.Key} in all", string.Empty, size.Key, string.Empty, string.Empty, size.Sum(line => line.Total).ToString(CultureInfo.InvariantCulture),
                us ? $"{Mass(size) * 2.20462:#,0} lb" : $"{Mass(size):#,0.0} kg", string.Empty, string.Empty, string.Empty
            }));

        foreach (var (member, fabric, area, sheets) in FabricOf(document))
            rows.Add((Schedules.ScheduleLineKind.Row, new[]
            {
                member, string.Empty, fabric, string.Empty, string.Empty, $"{sheets} sheets", Units.OfSquareMetres(area / 1e6, "0.0"), "fabric", string.Empty, string.Empty
            }));

        if (rows.Count == 0)
            rows.Add((Schedules.ScheduleLineKind.Row, new[] { "No reinforced foundations", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty }));
        rows.Add((Schedules.ScheduleLineKind.GroupHeading, new[]
        {
            us ? $"Cover {CoverAgainstGround / 25.4:0} in against the ground (ACI 318-19 Table 20.5.1.3.1). Laps {LapDiameters:0} bar diameters; bars {StockLength / 304.8:0} ft long at most."
               : $"Cover {CoverAgainstGround:0} against the ground, {CoverOnBlinding:0} on blinding (BS EN 1992-1-1 4.4.1.3(4)). Laps {LapDiameters:0} bar diameters; bars {StockLength / 1000:0} m long at most; fabric BS 4483 in 4.8 by 2.4 m sheets lapped 200."
        }));

        var headings = us
            ? new[] { "Member", "Mark", "Size", "No. of members", "No. in each", "Total no.", "Length", "Bend", "A", "B" }
            : new[] { "Member", "Bar mark", "Type and size", "No. of mbrs", "No. of bars in each", "Total no.", "Length of each bar (mm)", "Shape code", "A", "B" };
        return Views.AreaPlans.Build(Title(document), headings, rows, new[] { false, false, false, true, true, true, true, false, true, true },
            us ? "Set out as a bar list after ACI 315; ASTM A615 Grade 60 bars." : "Set out as BS 8666:2020 Table 2 asks; bars BS 4449 grade B500, H to BS 8666.");
    }

    /// <summary>The steel in a set of bar lines, kg.</summary>
    public static double Mass(IEnumerable<BarLine> lines) =>
        lines.Sum(line => line.Total * line.Length / 1000 * KilogramsPerMetre(BarSpec.Parse($"1 {line.Size}")?.Diameter ?? 0));

    // ---- the bars as solids, for a section to cut ----------------------------------------

    /// <summary>The cover to the bars in a raft's top face, inside the building, mm.</summary>
    public const double CoverTop = 40;

    /// <summary>
    /// A bar's centreline where it lies in a member, mm in the project: two points for a straight bar,
    /// a link's four corners round it closed. What IFC sweeps a bar's circle along.
    /// </summary>
    public sealed record BarPath(IReadOnlyList<Point3D> Points, double Diameter, string Size, bool Link);

    /// <summary>The bars being laid in a member: their solids and each one's centreline.</summary>
    private sealed class Cage(Mesh3D mesh)
    {
        public Mesh3D Mesh { get; } = mesh;
        public List<BarPath> Paths { get; } = new();
    }

    /// <summary>
    /// A member's bars as thin solids where they lie in it, each its bar's diameter square: what a
    /// section cuts to show a bar across it as a dot and one along it as a line.
    /// </summary>
    public static IReadOnlyList<Mesh3D> Rods(BimDocument document, Element element) =>
        Lay(document, element).Mesh is { TriangleCount: > 0 } steel ? new[] { steel } : Array.Empty<Mesh3D>();

    /// <summary>A member's bars one by one, each its centreline, size and whether it is a link.</summary>
    public static IReadOnlyList<BarPath> Bars(BimDocument document, Element element) => Lay(document, element).Paths;

    private static Cage Lay(BimDocument document, Element element)
    {
        var steel = new Cage(new Mesh3D(element.Id, element.LevelId, MeshKind.Beam, ColourRgb.FromHex("2B2B2B"), "Reinforcement"));
        var cover = CoverAgainstGround;
        switch (element)
        {
            case WallFooting strip when strip.Reinforced && BarSpec.Parse(strip.Bars) is { } bars:
                foreach (var (outline, bottom) in Footings.RunOutlines(document, strip))
                    Grid(steel, outline, bars, bottom + cover + bars.Diameter / 2, cover);
                break;

            case PadFooting pad when pad.Reinforced && BarSpec.Parse(pad.Bars) is { } bars && Footings.Outline(document, pad) is { } outline
                                     && Footings.Heights(document, pad) is var (bottom, _):
                Grid(steel, outline, bars, bottom + cover + bars.Diameter / 2, cover, bothWays: true);
                break;

            case GroundBeam beam when Piles.Outline(document, beam) is { } outline:
            {
                var (bottom, top) = Piles.Heights(document, beam);
                if (BarSpec.Parse(beam.TopBars) is { Count: not null } topBars) Grid(steel, outline, topBars, top - cover - topBars.Diameter / 2, cover);
                if (BarSpec.Parse(beam.BottomBars) is { Count: not null } bottomBars) Grid(steel, outline, bottomBars, bottom + cover + bottomBars.Diameter / 2, cover);
                if (BarSpec.Parse(beam.Links) is { Spacing: { } spacing } links) Links(steel, outline, links, spacing, bottom, top, cover);
                break;
            }

            case Pile pile when BarSpec.Parse(pile.Bars) is { Count: { } count } bars && pile.CageLength > 0:
            {
                var head = Piles.Head(document, pile);
                var centre = Piles.Position(document, pile);
                var radius = pile.Diameter / 2 - cover - bars.Diameter / 2;
                if (radius <= 0) break;
                for (var i = 0; i < count; i++)
                {
                    var angle = 2 * Math.PI * i / count;
                    Post(steel, centre + new Vector2D(Math.Cos(angle), Math.Sin(angle)) * radius, head - Math.Min(pile.CageLength, pile.Length - cover), head, bars);
                }

                break;
            }

            case RaftFoundation { Outline.Count: >= 3 } raft:
            {
                var top = Rafts.Top(document, raft);
                if (BarSpec.Parse(raft.TopReinforcement) is { Spacing: { } topPitch } topFace)
                    Mesh(steel, SlabEdgeRuns.Inset(raft.Outline, closed: true, cover), topFace, topPitch, top - CoverTop - topFace.Diameter / 2);
                if (BarSpec.Parse(raft.BottomReinforcement) is { Spacing: { } bottomPitch } bottomFace)
                    Mesh(steel, SlabEdgeRuns.Inset(raft.Outline, closed: true, cover), bottomFace, bottomPitch, top - raft.Thickness + CoverOnBlinding + bottomFace.Diameter / 2);
                if (raft.EdgeDepth > raft.Thickness + 1 && BarSpec.Parse(raft.EdgeBars) is { Count: { } count } edge)
                {
                    var z = top - raft.EdgeDepth + cover + edge.Diameter / 2;
                    for (var i = 0; i < count; i++)
                    {
                        var inset = count == 1 ? raft.EdgeWidth / 2 : cover + edge.Diameter / 2 + i * (raft.EdgeWidth - 2 * cover - edge.Diameter) / (count - 1);
                        var ring = SlabEdgeRuns.Inset(raft.Outline, closed: true, inset);
                        for (var j = 0; j < ring.Count; j++) Rod(steel, ring[j], ring[(j + 1) % ring.Count], z, edge);
                    }

                    foreach (var strip in Rafts.Thickenings(document, raft))
                        Grid(steel, strip, edge, z, cover);
                }

                if (raft.EdgeDepth > raft.Thickness + 1 && BarSpec.Parse(raft.EdgeLinks) is { Spacing: { } spacing } links)
                {
                    var inner = SlabEdgeRuns.Inset(raft.Outline, closed: true, raft.EdgeWidth);
                    for (var j = 0; j < raft.Outline.Count; j++)
                    {
                        var k = (j + 1) % raft.Outline.Count;
                        Links(steel, new[] { raft.Outline[j], raft.Outline[k], inner[k], inner[j] }, links, spacing, top - raft.EdgeDepth, top, cover);
                    }
                }

                break;
            }
        }

        return steel;
    }

    /// <summary>A horizontal bar from one point to another at a height; part of a link when not alone.</summary>
    private static void Rod(Cage cage, Point2D from, Point2D to, double z, BarSpec bars, bool alone = true)
    {
        var (length, diameter) = (from.DistanceTo(to), bars.Diameter);
        if (length < diameter) return;
        var side = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (diameter / 2);
        cage.Mesh.AddExtrusion(new[] { from - side, to - side, to + side, from + side }, z - diameter / 2, z + diameter / 2);
        if (alone) cage.Paths.Add(new BarPath(new[] { new Point3D(from.X, from.Y, z), new Point3D(to.X, to.Y, z) }, diameter, bars.Size, Link: false));
    }

    /// <summary>An upright bar at a point, from one height to another; part of a link when not alone.</summary>
    private static void Post(Cage cage, Point2D at, double bottom, double top, BarSpec bars, bool alone = true)
    {
        var diameter = bars.Diameter;
        if (top - bottom < diameter) return;
        var h = diameter / 2;
        cage.Mesh.AddExtrusion(new[] { at + new Vector2D(-h, -h), at + new Vector2D(h, -h), at + new Vector2D(h, h), at + new Vector2D(-h, h) }, bottom, top);
        if (alone) cage.Paths.Add(new BarPath(new[] { new Point3D(at.X, at.Y, bottom), new Point3D(at.X, at.Y, top) }, diameter, bars.Size, Link: false));
    }

    /// <summary>
    /// Bars in a long box - two corners at its start, two at its end - at a height: so many along it
    /// spread across it, or at centres along it and across it; at centres each way under a pad.
    /// </summary>
    private static void Grid(Cage cage, IReadOnlyList<Point2D> box, BarSpec bars, double z, double cover, bool bothWays = false)
    {
        if (box.Count < 4) return;
        var (a, b, d) = (box[0], box[1], box[3]);
        var (length, width) = (a.DistanceTo(b), a.DistanceTo(d));
        if (length < 2 * cover || width < 2 * cover) return;
        var along = (b - a) / length;
        var across = (d - a) / width;
        var r = bars.Diameter / 2;

        IEnumerable<double> Spread(double extent, int count) =>
            count == 1 ? new[] { extent / 2 } : Enumerable.Range(0, count).Select(i => cover + r + i * (extent - 2 * (cover + r)) / (count - 1));
        IEnumerable<double> AtCentres(double extent, double spacing) =>
            Enumerable.Range(0, AcrossAt(extent, spacing, cover + r)).Select(i => cover + r + i * spacing);

        var lengthwise = bars.Count is { } n ? Spread(width, n) : AtCentres(width, bars.Spacing ?? width);
        foreach (var offset in lengthwise)
            Rod(cage, a + across * offset + along * cover, b + across * offset - along * cover, z, bars);
        if (bars.Count is not null && !bothWays) return;

        // The bars across lie on those along it.
        var crosswise = bars.Count is { } m ? Spread(length, m) : AtCentres(length, bars.Spacing ?? length);
        foreach (var offset in crosswise)
            Rod(cage, a + along * offset + across * cover, d + along * offset - across * cover, z + bars.Diameter, bars);
    }

    /// <summary>Closed links round a long box at centres along it, from its underside to its top, each the cover in from its faces.</summary>
    private static void Links(Cage cage, IReadOnlyList<Point2D> box, BarSpec links, double spacing, double bottom, double top, double cover)
    {
        var diameter = links.Diameter;
        var (a, b, d) = (box[0], box[1], box[3]);
        var (length, width) = (a.DistanceTo(b), a.DistanceTo(d));
        if (length < 2 * cover || width < 2 * cover + diameter || top - bottom < 2 * cover + diameter) return;
        var along = (b - a) / length;
        var across = (d - a) / width;
        var r = diameter / 2;
        var (low, high) = (bottom + cover + r, top - cover - r);
        for (var i = 0; i < AcrossAt(length, spacing, cover); i++)
        {
            var at = a + along * (cover + i * spacing);
            var (near, far) = (at + across * (cover + r), at + across * (width - cover - r));
            Post(cage, near, bottom + cover, top - cover, links, alone: false);
            Post(cage, far, bottom + cover, top - cover, links, alone: false);
            Rod(cage, near, far, low, links, alone: false);
            Rod(cage, near, far, high, links, alone: false);
            cage.Paths.Add(new BarPath(new[]
            {
                new Point3D(near.X, near.Y, low), new Point3D(far.X, far.Y, low), new Point3D(far.X, far.Y, high), new Point3D(near.X, near.Y, high)
            }, diameter, links.Size, Link: true));
        }
    }

    /// <summary>Bars at centres each way over an outline, at a height, those across lying on those along, each cut to the outline.</summary>
    private static void Mesh(Cage cage, IReadOnlyList<Point2D> outline, BarSpec bars, double pitch, double z)
    {
        if (outline.Count < 3) return;
        var (minX, maxX) = (outline.Min(point => point.X), outline.Max(point => point.X));
        var (minY, maxY) = (outline.Min(point => point.Y), outline.Max(point => point.Y));
        for (var y = minY + pitch / 2; y < maxY; y += pitch)
            foreach (var (from, to) in Spans(outline, new Point2D(minX - 1, y), Vector2D.UnitX))
                Rod(cage, from, to, z, bars);
        for (var x = minX + pitch / 2; x < maxX; x += pitch)
            foreach (var (from, to) in Spans(outline, new Point2D(x, minY - 1), Vector2D.UnitY))
                Rod(cage, from, to, z + bars.Diameter, bars);
    }

    /// <summary>Where a line from a point along a direction lies inside an outline: the stretches between its crossings of the edges.</summary>
    private static IEnumerable<(Point2D From, Point2D To)> Spans(IReadOnlyList<Point2D> outline, Point2D origin, Vector2D direction)
    {
        var crossings = new List<double>();
        for (var i = 0; i < outline.Count; i++)
        {
            var (p, q) = (outline[i], outline[(i + 1) % outline.Count]);
            var edge = q - p;
            var denominator = direction.X * edge.Y - direction.Y * edge.X;
            if (Math.Abs(denominator) < 1e-12) continue;
            var offset = p - origin;
            var s = (offset.X * direction.Y - offset.Y * direction.X) / denominator;
            if (s < 0 || s >= 1) continue;
            crossings.Add((offset.X * edge.Y - offset.Y * edge.X) / denominator);
        }

        crossings.Sort();
        for (var i = 0; i + 1 < crossings.Count; i += 2)
            yield return (origin + direction * crossings[i], origin + direction * crossings[i + 1]);
    }
}
