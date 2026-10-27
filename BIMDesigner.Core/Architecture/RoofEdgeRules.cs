using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// What the rules ask of a roof's edges: its gutters, the downpipes from them, and the soffits
/// letting air into a cold roof.
///
/// A gutter is sized as Approved Document H (H3, Section 1) sizes it: the roof's effective area
/// draining into it - its plan area, and half its elevation for its pitch (Table 1: 1.29 times at
/// 30°, 1.50 at 45°, 1.87 at 60°; past 70°, half the elevation alone) - against the largest a
/// level half-round gutter of its size takes to a sharp-edged outlet at one end (Table 2: 18 m² a
/// 75 gutter, 37 m² 100, 53 m² 115, 65 m² 125, 103 m² 150). A gutter of another section takes
/// what a half round of as much section does. An outlet part way along takes the larger side of
/// it (1.5), and one further than 50 times its depth of water from the stop end takes less
/// (1.4) - BS EN 12056-3 takes off for that. Its outlet is no smaller than Table 2's for all the
/// area coming to it, and the downpipe no smaller than the outlet (1.10).
///
/// A cold roof - its insulation on the ceiling under it - is aired at its eaves: BS 5250 asks
/// 10,000 mm² a metre, a 10 mm gap along each side, of a roof pitched 15° or more, 25,000 below
/// that. The IRC (R806.2): 1/300 of the loft's area, half of it at the eaves and half high up.
/// </summary>
public static class RoofEdgeRules
{
    /// <summary>Table 2: the largest effective area, m², a level half-round gutter takes to a sharp-edged outlet at one end, its size and its outlet's, mm.</summary>
    public static readonly IReadOnlyList<(double Area, double Gutter, double Outlet)> Table2 = new[]
    {
        (18.0, 75.0, 50.0), (37.0, 100.0, 63.0), (53.0, 115.0, 63.0), (65.0, 125.0, 75.0), (103.0, 150.0, 89.0)
    };

    /// <summary>Under so much effective area, m², a roof needs no gutter at all (Table 2).</summary>
    public const double NoGutter = 6;

    /// <summary>
    /// What a slope's plan area counts as for the water off it (Table 1): its plan area and half
    /// its elevation - 1.29 times at 30°, 1.50 at 45°, 1.87 at 60° - and past 70°, as a wall,
    /// half its elevation alone.
    /// </summary>
    public static double EffectiveFactor(double rise) =>
        Math.Atan(rise) * 180 / Math.PI > 70 ? rise / 2 : 1 + rise / 2;

    /// <summary>The size of half round with as much section as a gutter type has, mm: its own width for a half round.</summary>
    public static double HalfRoundSize(GutterType type)
    {
        if (type.Shape == GutterShape.HalfRound) return type.Width;
        var channel = Gutters.Form(type).Channel;
        return Math.Sqrt(8 * Math.Abs(Polygon2D.SignedArea(channel)) / Math.PI);
    }

    /// <summary>How deep the water in a gutter type can be, mm: the depth of its channel.</summary>
    public static double WaterDepth(GutterType type) => -Gutters.Form(type).Bottom;

    /// <summary>The effective area, m², a gutter of a half round's size takes to an outlet at one end (Table 2, between its sizes pro rata, and on past its largest).</summary>
    public static double Capacity(double size)
    {
        if (size <= Table2[0].Gutter) return Table2[0].Area * size / Table2[0].Gutter;
        for (var i = 1; i < Table2.Count; i++)
            if (size <= Table2[i].Gutter)
                return Table2[i - 1].Area + (Table2[i].Area - Table2[i - 1].Area) * (size - Table2[i - 1].Gutter) / (Table2[i].Gutter - Table2[i - 1].Gutter);
        var (last, before) = (Table2[^1], Table2[^2]);
        return last.Area + (last.Area - before.Area) * (size - last.Gutter) / (last.Gutter - before.Gutter);
    }

    /// <summary>
    /// What a gutter takes when the water goes further than 50 times its depth to the outlet: BS
    /// EN 12056-3 takes off for it, a little more the further it goes, down to 0.4 at 500 times.
    /// </summary>
    public static double LongFactor(double distance, double depth) =>
        depth <= 0 ? 1 : Math.Clamp(1 - (distance / depth - 50) / 750, 0.4, 1);

    /// <summary>The smallest half round and outlet Table 2 gives for an effective area, mm; null past its largest.</summary>
    public static (double Gutter, double Outlet)? Asked(double area) =>
        Table2.FirstOrDefault(row => row.Area >= area - 1e-6) is { Gutter: > 0 } row ? (row.Gutter, row.Outlet) : null;

    /// <summary>The diameter of round pipe with as much bore as a gutter type's downpipes, mm.</summary>
    public static double DownpipeBore(GutterType type) => type.DownpipeShape == DownpipeShape.Round
        ? type.DownpipeWidth
        : Math.Sqrt(4 * type.DownpipeWidth * (type.DownpipeShape == DownpipeShape.Square ? type.DownpipeWidth : type.DownpipeDepth) / Math.PI);

    /// <summary>One run of a gutter, as a line in plan, with the roof's water coming into it along it.</summary>
    private sealed record Channel(IReadOnlyList<Point2D> Line, IReadOnlyList<(double From, double To, double Area)> Inflow, IReadOnlyList<double> Outlets,
        IReadOnlyList<Downpipe> Pipes)
    {
        public double Length => Line.Zip(Line.Skip(1), (a, b) => a.DistanceTo(b)).Sum();

        public double Area => Inflow.Sum(piece => piece.Area);

        /// <summary>The water, m², coming in between two places along it.</summary>
        public double Between(double from, double to) => Inflow.Sum(piece =>
            piece.To - piece.From < 1e-6
                ? piece.From >= from && piece.From < to ? piece.Area : 0
                : piece.Area * Math.Max(0, Math.Min(to, piece.To) - Math.Max(from, piece.From)) / (piece.To - piece.From));
    }

    /// <summary>How far along a line a point is, and how far off it.</summary>
    private static (double Along, double Off) Project(IReadOnlyList<Point2D> line, Point2D point)
    {
        var (best, along, run) = (double.MaxValue, 0.0, 0.0);
        for (var i = 0; i + 1 < line.Count; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            var length = a.DistanceTo(b);
            var t = length < 1e-9 ? 0 : Math.Clamp((point - a).Dot(b - a) / (length * length), 0, 1);
            var off = point.DistanceTo(a + (b - a) * t);
            if (off < best) (best, along) = (off, run + t * length);
            run += length;
        }

        return (along, best);
    }

    /// <summary>A gutter's runs, each with the water off the roof's slopes into it and its downpipes' outlets along it.</summary>
    private static IReadOnlyList<Channel> Channels(BimDocument document, Gutter gutter, Roof roof)
    {
        var lines = RoofEdgeSweeps.Runs(document, gutter)
            .Select(run => run.Segments.Select(segment => segment.From.Plan).Append(run.Segments[^1].To.Plan).ToList())
            .Where(line => line.Count >= 2)
            .ToList();
        var inflow = lines.Select(_ => new List<(double, double, double)>()).ToList();

        // Each slope's water runs down it to its eave - its lowest edge - and in along it.
        foreach (var facet in roof.Surface(document).Facets)
        {
            if (facet.Plane.Rise < 0.02 || facet.Outline.Count < 3) continue;
            var low = facet.Outline.Min(point => facet.Plane.HeightAt(point));
            var eaves = Enumerable.Range(0, facet.Outline.Count)
                .Select(i => (A: facet.Outline[i], B: facet.Outline[(i + 1) % facet.Outline.Count]))
                .Where(edge => edge.A.DistanceTo(edge.B) > 10
                    && Math.Abs(facet.Plane.HeightAt(edge.A) - low) < 2 && Math.Abs(facet.Plane.HeightAt(edge.B) - low) < 2)
                .ToList();
            var eaveLength = eaves.Sum(edge => edge.A.DistanceTo(edge.B));
            if (eaveLength < 1) continue;

            var effective = facet.Area / 1e6 * EffectiveFactor(facet.Plane.Rise);
            foreach (var (a, b) in eaves)
            for (var k = 0; k < lines.Count; k++)
            {
                var (from, fromOff) = Project(lines[k], a);
                var (to, toOff) = Project(lines[k], b);
                if (fromOff > 50 || toOff > 50) continue;
                inflow[k].Add((Math.Min(from, to), Math.Max(from, to), effective * a.DistanceTo(b) / eaveLength));
                break;
            }
        }

        // Each downpipe's outlet, on the run it is nearest.
        var outlets = lines.Select(_ => new List<(double Along, Downpipe Pipe)>()).ToList();
        foreach (var pipe in Downpipes.On(document, gutter))
        {
            var at = Downpipes.Path(document, pipe) is { } path ? path.Points[0].Plan : pipe.Location;
            var nearest = Enumerable.Range(0, lines.Count).Select(k => (K: k, Place: Project(lines[k], at))).OrderBy(entry => entry.Place.Off).FirstOrDefault();
            if (lines.Count > 0 && nearest.Place.Off < 600) outlets[nearest.K].Add((nearest.Place.Along, pipe));
        }

        return lines.Select((line, k) =>
        {
            var sorted = outlets[k].OrderBy(outlet => outlet.Along).ToList();
            return new Channel(line, inflow[k], sorted.Select(outlet => outlet.Along).ToList(), sorted.Select(outlet => outlet.Pipe).ToList());
        }).ToList();
    }

    /// <summary>A band along a run, as wide as the gutter: what is drawn red in plan.</summary>
    private static IReadOnlyList<Point2D> Band(IReadOnlyList<Point2D> line, double width)
    {
        var left = new List<Point2D>();
        var right = new List<Point2D>();
        for (var i = 0; i < line.Count; i++)
        {
            var direction = (i + 1 < line.Count ? line[i + 1] - line[i] : line[i] - line[i - 1]).NormalisedOrDefault(Vector2D.UnitX);
            var across = direction.PerpendicularLeft() * (width / 2);
            left.Add(line[i] + across);
            right.Add(line[i] - across);
        }

        right.Reverse();
        return left.Concat(right).ToList();
    }

    /// <summary>What a gutter, its downpipes or a soffit falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, RoofEdgeSweep sweep, ClearanceStandard standard)
    {
        if (document.Find<Roof>(sweep.RoofId) is not { } roof) return Array.Empty<PlacementProblem>();
        return sweep switch
        {
            Gutter gutter when !standard.IsUs() && document.FindType<GutterType>(gutter.TypeId) is { } type => GutterProblems(document, gutter, type, roof),
            Soffit soffit when document.FindType<SoffitType>(soffit.TypeId) is { } type => SoffitProblems(document, soffit, type, roof, standard),
            Eave eave => Eaves.Problems(document, eave, standard),
            _ => Array.Empty<PlacementProblem>()
        };
    }

    private static IReadOnlyList<PlacementProblem> GutterProblems(BimDocument document, Gutter gutter, GutterType type, Roof roof)
    {
        var problems = new List<PlacementProblem>();
        var size = HalfRoundSize(type);
        var depth = WaterDepth(type);
        var bore = DownpipeBore(type);
        var said = type.Shape == GutterShape.HalfRound ? $"a {type.Width:0} half round" : $"as much as a {size:0} half round";

        foreach (var channel in Channels(document, gutter, roof))
        {
            var outline = Band(channel.Line, Math.Max(type.Width, 100));
            void Add(string text, string rule) => problems.Add(new PlacementProblem(gutter, text, rule, outline));

            if (channel.Outlets.Count == 0)
            {
                if (channel.Area > NoGutter)
                    Add($"the {Units.OfSquareMetres(channel.Area, "0.0")} of roof draining into it has no downpipe to go down.", "Approved Document H (H3) asks for a gutter's water to be taken to a drain - put a downpipe on it.");
                continue;
            }

            // Each outlet takes the water from halfway to the next, or from the run's end.
            for (var i = 0; i < channel.Outlets.Count; i++)
            {
                var at = channel.Outlets[i];
                var from = i == 0 ? 0 : (channel.Outlets[i - 1] + at) / 2;
                var to = i == channel.Outlets.Count - 1 ? channel.Length : (at + channel.Outlets[i + 1]) / 2;
                var (before, after) = (channel.Between(from - 1e-6, at), channel.Between(at, to + 1e-6));
                var (side, reach) = before >= after ? (before, at - from) : (after, to - at);
                var takes = Capacity(size) * LongFactor(reach, depth);

                if (side > takes + 0.05)
                {
                    var needed = Asked(side / LongFactor(reach, depth));
                    Add($"{Units.OfSquareMetres(side, "0.0")} of roof - its effective area for its pitch - comes to an outlet {Units.Metres(reach, "0.0")} along it, more than {said} takes there ({Units.OfSquareMetres(takes, "0.0")}).",
                        needed is { } row
                            ? $"Approved Document H (Table 2) asks for a {row.Gutter:0} half-round gutter for that - or another downpipe, to share the water."
                            : "Approved Document H (Table 2) gives no half round big enough - put in another downpipe, to share the water.");
                }

                var total = before + after;

                // Onto a lower roof, a shoe - and past 25 m², a distributor to spread the water along its gutter.
                if (total > 25 + 0.05 && !channel.Pipes[i].Distributor && Downpipes.Path(document, channel.Pipes[i]) is { OntoRoof: true })
                    Add($"its downpipe takes {Units.OfSquareMetres(total, "0.0")} of roof down onto the roof below with no distributor on its shoe.",
                        "Approved Document H (1.9) asks for a distributor pipe on the shoe of a downpipe from more than 25 m² onto a lower roof, so its gutter is not over-topped - tick Distributor on its Shoe.");

                if (Asked(total) is { } outlet && bore < outlet.Outlet - 0.5)
                    Add($"its downpipe is {Downpipes.Size(type)} - {bore:0} bore - and the {Units.OfSquareMetres(total, "0.0")} coming to it wants an outlet of {outlet.Outlet:0}.",
                        "Approved Document H asks for a downpipe at least as big as its outlet (1.10), and the outlet Table 2 gives for the area.");
            }
        }

        return problems;
    }

    /// <summary>How much air a cold roof's eaves want, mm² a metre of eave, and whose rule asks it; null where its rule asks none.</summary>
    public static (double PerMetre, string Rule)? EavesAirAsked(BimDocument document, Roof roof, ClearanceStandard standard)
    {
        if (!SlabRules.Cold(document, roof)) return null;
        if (standard.IsUs())
        {
            // 1/300 of the loft's floor, half of it at the eaves, where it is vented high up as well; 1/150 at the eaves where it is not.
            var eaves = RoofEdgeSweeps.Of(document, roof).Where(sweep => sweep is Soffit or Eave).Sum(sweep => sweep.Length(document));
            if (eaves < 1) return null;
            return roof.HighLevelVents
                ? (Polygon2D.Area(roof.Boundary) / 300 / 2 / (eaves / 1000), "The IRC (R806.2) asks for 1/300 of the loft's area in vents, half of them at the eaves and half high up")
                : (Polygon2D.Area(roof.Boundary) / 150 / (eaves / 1000), "The IRC (R806.2) asks for 1/150 of the loft's area in vents where none are high up - or 1/300 with half of them at the ridge");
        }

        return roof.SlopeDegrees >= 15
            ? (10000, "BS 5250 asks a cold roof pitched 15° or more for 10,000 mm² of air a metre along each eave - a 10 mm gap")
            : (25000, "BS 5250 asks a cold roof pitched under 15° for 25,000 mm² of air a metre along each eave - a 25 mm gap");
    }

    private static IReadOnlyList<PlacementProblem> SoffitProblems(BimDocument document, Soffit soffit, SoffitType type, Roof roof, ClearanceStandard standard)
    {
        if (EavesAirAsked(document, roof, standard) is not var (asked, rule) || type.FreeAirArea >= asked - 0.5) return Array.Empty<PlacementProblem>();

        var outlines = RoofEdgeSweeps.SoffitPieces(document, soffit).Select(piece => piece.Outline).ToList();
        if (outlines.Count == 0) return Array.Empty<PlacementProblem>();
        var said = type.FreeAirArea > 0
            ? $"it lets {Units.AreaPerLength(type.FreeAirArea, " of air")} into the cold roof over it, its insulation on the ceiling."
            : "it lets no air into the cold roof over it, its insulation on the ceiling.";
        return outlines.Select(outline => new PlacementProblem(soffit, said, $"{rule} - a vented soffit, or vents over the fascia.", outline)).ToList();
    }

    /// <summary>What a gutter or soffit comes to as its rules see it.</summary>
    public static string Verdict(BimDocument document, RoofEdgeSweep sweep)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var problems = Problems(document, sweep, standard);
        var said = string.Join(" ", problems.Select(problem => problem.Said).Distinct()
            .Select(text => $"{char.ToUpperInvariant(text[0])}{text[1..]}"));
        var rules = string.Join(" ", problems.Select(problem => problem.Rule).Distinct());

        if (sweep is Gutter gutter && document.FindType<GutterType>(gutter.TypeId) is { } type
            && document.Find<Roof>(gutter.RoofId) is { } roof)
        {
            if (standard.IsUs()) return "Not sized by the IRC";
            var area = Channels(document, gutter, roof).Sum(channel => channel.Area);
            var head = $"{Units.OfSquareMetres(area, "0.0")} of roof drains into it; {(type.Shape == GutterShape.HalfRound ? $"a {type.Width:0} half round" : $"as much as a {HalfRoundSize(type):0} half round")} takes {Units.OfSquareMetres(Capacity(HalfRoundSize(type)), "0")} to an outlet at its end";
            return problems.Count == 0 ? $"{head} - big enough" : $"{head} - {said} {rules}";
        }

        if (sweep is Soffit && document.Find<Roof>(sweep.RoofId) is { } over)
            return EavesAirAsked(document, over, standard) is var (asked, _)
                ? problems.Count == 0 ? $"Airs the cold roof over it: {Units.AreaPerLength(asked)} asked" : $"{said} {rules}"
                : "The roof over it is not a cold roof: no air asked";

        return problems.Count == 0 ? "Nothing asked" : $"{said} {rules}";
    }

    /// <summary>Every gutter's and soffit's problems over a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard)
    {
        var roofs = document.All<Roof>().Where(roof => roof.LevelId == levelId).Select(roof => roof.Id).ToHashSet();
        return document.All<RoofEdgeSweep>()
            .Where(sweep => roofs.Contains(sweep.RoofId) && sweep is Gutter or Soffit or Eave)
            .SelectMany(sweep => Problems(document, sweep, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(sweep, sweep switch { Gutter => "its water", Eave => "the eave", _ => "its air" }, problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ElementType>(sweep.TypeId)?.Name ?? (sweep switch { Gutter => "A gutter", Eave => "An eave", _ => "A soffit" }), problem.Said)))
            .ToList();
    }
}
