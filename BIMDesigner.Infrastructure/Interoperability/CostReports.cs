using System.Globalization;
using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The cost plan as a spreadsheet: the plan itself by cost element, with its groups' subtotals
/// and the additions to the total; every element costed, with its IFC GUID to find it by; and,
/// with a programme, the spend week by week.
/// </summary>
public static class CostReports
{
    public static void WriteXlsx(Stream output, BimDocument document)
    {
        var summary = CostPlanning.Summary(document);
        var currency = summary.Currency;

        var plan = new XlsxSheet("Cost plan", new[]
        {
            ("Code", HeadingKind.Plain), ("Element", HeadingKind.Plain), ($"Amount ({currency})", HeadingKind.Plain), ("%", HeadingKind.Plain)
        });
        foreach (var row in summary.Rows)
            plan.Add(row.Code, row.IsTotal && row.Lines.Count == 0 && row.Percent is null ? row.Title.ToUpperInvariant() : row.Title, Math.Round(row.Amount, 2), row.Percent is { } percent ? percent : null);
        plan.Add(string.Empty, "Floor area (m²)", Math.Round(summary.FloorArea, 2), null);
        plan.Add(string.Empty, $"Cost per m² ({currency})", Math.Round(summary.PerSquareMetre, 2), null);

        var lines = new XlsxSheet("Elements", new[]
        {
            ("Code", HeadingKind.Plain), ("Cost element", HeadingKind.Plain), ("Category", HeadingKind.Plain), ("Type", HeadingKind.Plain),
            ("Mark", HeadingKind.Plain), ("Level", HeadingKind.Plain), ("Quantity", HeadingKind.Plain), ("Unit", HeadingKind.Plain),
            ($"Rate ({currency})", HeadingKind.Plain), ($"Total ({currency})", HeadingKind.Plain), ("IFC GUID", HeadingKind.External)
        });
        foreach (var line in CostPlanning.Lines(document).OrderBy(line => line.Code, StringComparer.Ordinal))
            lines.Add(line.Code, line.Title, line.Element.Category.ToString(), line.Type?.Name ?? string.Empty, line.Element.Mark,
                document.FindLevel(line.Element.LevelId)?.Name ?? string.Empty, Math.Round(line.Quantity, 3), CostPlanning.Abbreviation(line.Unit),
                Math.Round(line.Rate, 2), Math.Round(line.Total, 2), line.Element.Id.ToIfc().ToString());

        var sheets = new List<XlsxSheet> { plan, lines };
        if (document.Construction.Tasks.Count > 0)
        {
            var flow = new XlsxSheet("Cash flow", new[] { ("Week beginning", HeadingKind.Plain), ($"Spend ({currency})", HeadingKind.Plain), ($"Cumulative ({currency})", HeadingKind.Plain) });
            foreach (var (week, spend, cumulative) in CostPlanning.CashFlow(document))
                flow.Add(week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Math.Round(spend, 2), Math.Round(cumulative, 2));
            sheets.Add(flow);
        }

        XlsxWriter.Write(output, sheets);
    }
}
