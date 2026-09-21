using System.Text;
using BIMDesigner.Core;
using BIMDesigner.Core.Schedules;

namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>
/// Writes a schedule out as CSV (specification section 6.4, "export to CSV/XLSX").
///
/// CSV because it is what a quantity surveyor's spreadsheet opens without asking questions.
/// Values are written as they read on screen - "5.25 m", not 5250 - because the export is a
/// document for a person, and a bare number in millimetres invites the reader to guess at
/// its units. A machine-readable export with raw values belongs with the API work later.
/// </summary>
public static class ScheduleCsv
{
    public static string Write(ScheduleResult schedule)
    {
        var text = new StringBuilder();

        text.AppendLine(Escape(schedule.Definition.Name));
        text.AppendLine();

        text.AppendLine(string.Join(",", schedule.Columns.Select(column => Escape(column.Field))));

        if (schedule.Groups.Count > 0)
        {
            foreach (var group in schedule.Groups)
            {
                text.AppendLine(Escape(group.Heading));

                foreach (var row in group.Rows) WriteRow(text, schedule, row);

                if (schedule.Definition.ShowTotals)
                    WriteTotals(text, schedule, group.Subtotals, $"Subtotal ({group.Rows.Count})");
            }
        }
        else
        {
            foreach (var row in schedule.Rows) WriteRow(text, schedule, row);
        }

        if (schedule.Definition.ShowTotals)
            WriteTotals(text, schedule, schedule.Totals, $"Total ({schedule.Count})");

        return text.ToString();
    }

    /// <summary>The material takeoff, rolled up per material.</summary>
    public static string WriteTakeoff(IReadOnlyList<TakeoffTotal> totals)
    {
        var text = new StringBuilder();

        text.AppendLine("Material Takeoff");
        text.AppendLine();
        text.AppendLine("Material,Layers,Area,Volume,Cost");

        foreach (var total in totals)
        {
            text.AppendLine(string.Join(",",
                Escape(total.Material),
                total.Elements.ToString(),
                Escape(Units.FormatArea(total.Area)),
                Escape(Units.FormatVolume(total.Volume)),
                Escape(total.Cost.ToString("0.00"))));
        }

        text.AppendLine(string.Join(",",
            "Total", string.Empty, string.Empty,
            Escape(Units.FormatVolume(totals.Sum(total => total.Volume))),
            Escape(totals.Sum(total => total.Cost).ToString("0.00"))));

        return text.ToString();
    }

    private static void WriteRow(StringBuilder text, ScheduleResult schedule, ScheduleRow row) =>
        text.AppendLine(string.Join(",",
            schedule.Columns.Select(column => Escape(row.Text(column.Field)))));

    private static void WriteTotals(
        StringBuilder text, ScheduleResult schedule,
        IReadOnlyDictionary<string, double> totals, string label)
    {
        var cells = schedule.Columns.Select((column, index) =>
        {
            if (index == 0) return Escape(label);
            if (!column.IsTotalled || !totals.TryGetValue(column.Field, out var value)) return string.Empty;

            return Escape(ParameterTotalText(column, value));
        });

        text.AppendLine(string.Join(",", cells));
    }

    /// <summary>A total formatted in its column's own units.</summary>
    public static string ParameterTotalText(ScheduleColumn column, double value) => column.DataType switch
    {
        Core.Parameters.ParameterDataType.Length => Units.FormatLength(value),
        Core.Parameters.ParameterDataType.Area => Units.FormatArea(value),
        Core.Parameters.ParameterDataType.Volume => Units.FormatVolume(value),
        Core.Parameters.ParameterDataType.Integer => ((long)Math.Round(value)).ToString(),
        Core.Parameters.ParameterDataType.Currency => value.ToString("0.00"),
        _ => value.ToString("0.###")
    };

    /// <summary>
    /// Quotes a field if it contains anything that would otherwise break the row. Areas and
    /// volumes carry a comma in some locales, so this is not optional.
    /// </summary>
    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n')) return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
