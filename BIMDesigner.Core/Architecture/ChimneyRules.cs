using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Architecture;

public static partial class Chimneys
{
    /// <summary>A fireplace opening of 6 sq ft or more, mm²: the IRC asks a bigger hearth before it (R1001.10).</summary>
    public const double LargeOpening = 557_418;
}

/// <summary>
/// What the rules ask of a chimney's flues and stack.
///
/// Its flue's size (Approved Document J, Table 2): 200 across for an open fire's opening up to 500
/// by 550, a flue of 15 per cent of a bigger opening's face (2.7), and 125 at the least for a
/// stove. The IRC: a round flue a twelfth of the fireplace opening (R1003.15). Its height: 4.5 m
/// from where the air goes into the fire to the top, which Approved Document J takes as enough to
/// draw (2.8, 2.9). And a masonry stack standing free above the roof no taller, its pot with it,
/// than 4.5 times its least width (Approved Document A, 2D1).
/// </summary>
public static class ChimneyRules
{
    /// <summary>The height of flue Approved Document J takes as enough to draw, mm.</summary>
    public const double FlueHeight = 4500;

    /// <summary>How far above its hearth a stove's flue starts, mm: its outlet.</summary>
    private const double StoveOutlet = 750;

    /// <summary>Each fire a chimney has: what it is, its opening - none for a stove standing out from the flue - and where its flue starts drawing air.</summary>
    public static IReadOnlyList<(ChimneyFireplace Kind, Guid LevelId, double Width, double Height, double Entry)> Fires(BimDocument document, Chimney chimney)
    {
        var fires = new List<(ChimneyFireplace, Guid, double, double, double)>();
        var hearths = Chimneys.Hearths(document, chimney);
        foreach (var hearth in hearths)
        {
            if (Chimneys.BreastFor(document, chimney, hearth) is { } breast)
                fires.Add((hearth.Kind, hearth.LevelId, breast.Opening[0].DistanceTo(breast.Opening[1]), breast.OpeningHeight, hearth.Floor + breast.OpeningHeight));
            else fires.Add((hearth.Kind, hearth.LevelId, 0, 0, hearth.Floor + StoveOutlet));
        }

        if (hearths.Count == 0 && chimney.Fireplace != ChimneyFireplace.None)
            fires.Add((chimney.Fireplace, chimney.LevelId, 0, 0, Chimneys.Foot(document, chimney) + StoveOutlet));
        return fires;
    }

    /// <summary>What a chimney falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Chimney chimney, ClearanceStandard standard)
    {
        var type = Chimneys.TypeOf(document, chimney);
        // An industrial stack is sized by its own engineer; a gas appliance's flue by its terminal's siting, not a solid fuel fire's rules.
        if (type.Construction is ChimneyConstruction.SteelStack or ChimneyConstruction.GuyedStack or ChimneyConstruction.IndustrialMultiFlue or ChimneyConstruction.SolarChimney
            or ChimneyConstruction.TypeBVent or ChimneyConstruction.ConcentricFlue)
            return Array.Empty<PlacementProblem>();

        var problems = new List<PlacementProblem>();
        var outline = Chimneys.Footprint(document, chimney, chimney.LevelId);
        void Add(string said, string rule) => problems.Add(new PlacementProblem(chimney, said, rule, outline));

        var us = standard.IsUs();
        var flue = type.FlueDiameter;
        var flueArea = Math.PI * flue * flue / 4;
        static double Across(double area) => Math.Sqrt(4 * area / Math.PI);
        var top = Chimneys.Top(document, chimney);

        foreach (var (kind, levelId, width, height, entry) in Fires(document, chimney))
        {
            var floor = document.FindLevel(levelId)?.Name ?? "its";
            if (kind == ChimneyFireplace.Open && width > 0)
            {
                if (us)
                {
                    if (flueArea < width * height / 12 - 1)
                        Add($"its flue is {flue:0} across for the {floor} fire's {width:0} by {height:0} opening.",
                            $"The IRC asks for a round flue a twelfth of the fireplace opening's area (R1003.15): {Across(width * height / 12):0} across.");
                }
                else if (width <= 500 + 0.5 && height <= 550 + 0.5)
                {
                    if (flue < 200 - 0.5)
                        Add($"its flue is {flue:0} across for the {floor} open fire.",
                            "Approved Document J (Table 2) asks for a flue 200 across for an open fire's opening up to 500 by 550.");
                }
                else if (flueArea < 0.15 * width * height - 1)
                    Add($"its flue is {flue:0} across for the {floor} fire's {width:0} by {height:0} opening.",
                        $"Approved Document J (2.7) asks for a flue of 15 per cent of a bigger opening's face: {Across(0.15 * width * height):0} across.");
            }
            else if (kind == ChimneyFireplace.Stove && !us && flue < 125 - 0.5)
                Add($"its flue is {flue:0} across for the {floor} stove.",
                    "Approved Document J (Table 2) asks for 125 across at the least for a closed appliance - 150 for one burning any fuel.");

            if (!us && top - entry < FlueHeight - 0.5)
                Add($"the flue from the {floor} {(kind == ChimneyFireplace.Stove ? "stove" : "fire")} rises {(top - entry) / 1000:0.0} m to the top.",
                    "Approved Document J (2.8) takes a 4.5 m flue as enough to draw - stand the stack higher (Extra Height), or have its draught worked out to BS EN 13384-1.");
        }

        // A masonry stack standing free above the roof, its pot with it.
        if (!us && type.Construction is ChimneyConstruction.Masonry or ChimneyConstruction.PrecastBlock
            && Chimneys.HighestContact(document, chimney) is { } contact)
        {
            var above = top + Chimneys.CapThickness + Chimneys.PotHeight - contact;
            var least = Math.Min(chimney.Width, chimney.Depth);
            if (above > 4.5 * least + 0.5)
                Add($"it stands {above:0} above the roof, its pot with it - more than 4.5 times its least width, {4.5 * least:0}.",
                    "Approved Document A (2D1) holds a masonry chimney standing free above the roof to 4.5 times its least width - tie it back, or make it wider.");
        }

        return problems;
    }

    /// <summary>What a chimney comes to as its rules see it.</summary>
    public static string Verdict(BimDocument document, Chimney chimney)
    {
        var problems = Problems(document, chimney, document.ProjectInformation.ClearanceStandard);
        return problems.Count == 0
            ? "Its flues and stack as the rules ask"
            : string.Join(" ", problems.Select(problem => $"{char.ToUpperInvariant(problem.Said[0])}{problem.Said[1..]} {problem.Rule}"));
    }

    /// <summary>Every chimney's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Chimney>()
            .Where(chimney => chimney.LevelId == levelId)
            .SelectMany(chimney => Problems(document, chimney, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(chimney, "its flue", problem.Outline, problem.Rule), string.Empty,
                    Chimneys.TypeOf(document, chimney).Name, problem.Said)))
            .ToList();
}
