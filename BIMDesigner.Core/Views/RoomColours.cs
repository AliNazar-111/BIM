using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Core.Views;

/// <summary>What a plan's rooms are coloured by: nothing, their name, department, occupancy, or how big they are.</summary>
public enum RoomColourBy
{
    None,
    Name,
    Department,
    Occupancy,
    Area
}

/// <summary>One colour of a scheme: what it stands for, the colour, and how many rooms and how much floor have it on the view.</summary>
public sealed record RoomColourEntry(string Value, ColourRgb Colour, int Rooms, double Area);

/// <summary>
/// Rooms coloured on a plan by what they are - by name, department, occupancy, or by size in bands
/// - each value its own colour, the same on every storey, and a legend of them to put on a sheet:
/// a swatch, what it stands for, how many rooms and how much floor.
/// </summary>
public static class RoomColours
{
    /// <summary>Colours told apart easily, light enough to read a room's name over.</summary>
    private static readonly ColourRgb[] Palette =
    {
        ColourRgb.FromHex("8DD3C7"), ColourRgb.FromHex("FFFFB3"), ColourRgb.FromHex("BEBADA"), ColourRgb.FromHex("FB8072"),
        ColourRgb.FromHex("80B1D3"), ColourRgb.FromHex("FDB462"), ColourRgb.FromHex("B3DE69"), ColourRgb.FromHex("FCCDE5"),
        ColourRgb.FromHex("D9D9D9"), ColourRgb.FromHex("BC80BD"), ColourRgb.FromHex("CCEBC5"), ColourRgb.FromHex("FFED6F")
    };

    /// <summary>The size bands rooms are coloured in by area, m² - in a US project the same bands in square feet.</summary>
    private static readonly double[] MetricBands = { 5, 10, 15, 20, 30 };
    private static readonly double[] ImperialBands = { 50, 100, 150, 200, 300 };

    public static string Name(RoomColourBy by) => by switch
    {
        RoomColourBy.None => "None",
        RoomColourBy.Name => "Name",
        RoomColourBy.Department => "Department",
        RoomColourBy.Occupancy => "Occupancy",
        _ => "Area"
    };

    /// <summary>What a room counts as in a scheme: its name, department or occupancy - "(none)" where blank - or its size band.</summary>
    public static string ValueOf(BimDocument document, Room room, RoomColourBy by) => by switch
    {
        RoomColourBy.Name => Blank(room.Name),
        RoomColourBy.Department => Blank(room.Department),
        RoomColourBy.Occupancy => Blank(room.Occupancy),
        RoomColourBy.Area => Band(room.GetBoundary(document).Area),
        _ => string.Empty
    };

    private static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? "(none)" : text.Trim();

    /// <summary>The size bands as said, smallest first: "Under 5 m²", "5 to 10 m²" ... "30 m² and over".</summary>
    private static List<string> Bands()
    {
        var (bands, unit) = Units.Imperial ? (ImperialBands, "sq ft") : (MetricBands, "m²");
        var said = new List<string> { $"Under {bands[0]:0} {unit}" };
        for (var i = 1; i < bands.Length; i++) said.Add($"{bands[i - 1]:0} to {bands[i]:0} {unit}");
        said.Add($"{bands[^1]:0} {unit} and over");
        return said;
    }

    /// <summary>The band a floor area falls in, said in the project's units.</summary>
    private static string Band(double squareMillimetres)
    {
        var (bands, area) = Units.Imperial ? (ImperialBands, squareMillimetres / 92903.04) : (MetricBands, squareMillimetres / 1e6);
        var index = bands.TakeWhile(limit => area >= limit).Count();
        return Bands()[index];
    }

    /// <summary>The order a scheme's values come in: by size for the bands, by name for the rest.</summary>
    private static IEnumerable<string> Ordered(IEnumerable<string> values, RoomColourBy by)
    {
        var distinct = values.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (by != RoomColourBy.Area) return distinct.OrderBy(value => value == "(none)").ThenBy(value => value, StringComparer.CurrentCultureIgnoreCase);
        var order = Bands();
        return distinct.OrderBy(value => order.IndexOf(value) is var at && at >= 0 ? at : int.MaxValue);
    }

    /// <summary>
    /// Each value's colour: by its place among every room's in the project, so one department is
    /// the same colour on every storey.
    /// </summary>
    public static IReadOnlyDictionary<string, ColourRgb> Colours(BimDocument document, RoomColourBy by)
    {
        var values = Ordered(document.All<Room>().Select(room => ValueOf(document, room, by)), by).ToList();
        var colours = new Dictionary<string, ColourRgb>(StringComparer.CurrentCultureIgnoreCase);
        for (var i = 0; i < values.Count; i++) colours[values[i]] = Palette[i % Palette.Length];
        return colours;
    }

    /// <summary>A room's colour on a view, or null where the view colours none.</summary>
    public static ColourRgb? ColourOf(BimDocument document, ViewReference view, Room room)
    {
        var by = document.ViewSettings.ColourByOf(view);
        if (by == RoomColourBy.None) return null;
        var colours = ModelCache.Of(document) is { } cache
            ? cache.Keep($"room colours {by}", document, null, null, () => Colours(document, by))
            : Colours(document, by);
        return colours.TryGetValue(ValueOf(document, room, by), out var colour) ? colour : null;
    }

    /// <summary>The storey a plan or plan callout shows, or null.</summary>
    private static Guid? LevelOf(BimDocument document, ViewReference view) => view.Kind switch
    {
        ViewKind.FloorPlan => view.TargetId,
        ViewKind.Callout when document.Find<Callout>(view.TargetId) is { Parent.Kind: ViewKind.FloorPlan } callout => callout.Parent.TargetId,
        _ => null
    };

    /// <summary>A view's colours as its legend lists them: those of the rooms on it, each with how many and how much floor.</summary>
    public static IReadOnlyList<RoomColourEntry> Legend(BimDocument document, ViewReference view)
    {
        var by = document.ViewSettings.ColourByOf(view);
        if (by == RoomColourBy.None || LevelOf(document, view) is not { } level) return Array.Empty<RoomColourEntry>();

        var colours = Colours(document, by);
        var rooms = document.All<Room>().Where(room => room.LevelId == level)
            .Select(room => (Room: room, Value: ValueOf(document, room, by), Area: room.GetBoundary(document) is { IsEnclosed: true } boundary ? boundary.Area : 0))
            .ToList();
        return Ordered(rooms.Select(entry => entry.Value), by)
            .Select(value => new RoomColourEntry(value, colours[value], rooms.Count(entry => string.Equals(entry.Value, value, StringComparison.CurrentCultureIgnoreCase)),
                rooms.Where(entry => string.Equals(entry.Value, value, StringComparison.CurrentCultureIgnoreCase)).Sum(entry => entry.Area)))
            .ToList();
    }

    /// <summary>The legend for a view, as a viewport refers to it: the view's kind in its name, its target and index kept.</summary>
    public static ViewReference LegendOf(ViewReference view) => new(ViewKind.ColourLegend, view.TargetId, view.Kind.ToString()) { Index = view.Index };

    /// <summary>The view a legend is of.</summary>
    public static ViewReference ViewOf(ViewReference legend) =>
        new(Enum.TryParse<ViewKind>(legend.ScheduleName, out var kind) ? kind : ViewKind.FloorPlan, legend.TargetId, string.Empty) { Index = legend.Index };

    /// <summary>A legend as a table on a sheet: a swatch, what it stands for, how many rooms and how much floor.</summary>
    public static ScheduleTable Table(BimDocument document, ViewReference legend)
    {
        var view = ViewOf(legend);
        var by = document.ViewSettings.ColourByOf(view);
        var entries = Legend(document, view);
        var headings = new[] { string.Empty, Name(by), "Rooms", "Area" };
        var table = ScheduleTable.Of(Title(document, legend), headings,
            entries.Select(entry => (IReadOnlyList<string>)new[] { "      ", entry.Value, $"{entry.Rooms}", Units.SquareMetres(entry.Area, "0.0") }));
        var lines = table.Lines.Select((line, index) => index < 2 || index - 2 >= entries.Count
            ? line
            : line with { Backgrounds = new ColourRgb?[] { entries[index - 2].Colour, null, null, null } }).ToList();
        var widths = table.ColumnWidths.ToList();
        widths[0] = Math.Max(widths[0], 10);
        return new ScheduleTable { Title = table.Title, ColumnWidths = widths, Numeric = new[] { false, false, true, true }, Lines = lines };
    }

    /// <summary>A legend's title: "Colour Legend - Ground Floor Plan, by Department".</summary>
    public static string Title(BimDocument document, ViewReference legend)
    {
        var view = ViewOf(legend);
        return $"Colour Legend - {view.TitleIn(document)}, by {Name(document.ViewSettings.ColourByOf(view))}";
    }

    /// <summary>A view's rooms coloured by something else, or by nothing. One step to undo.</summary>
    public static IUndoableCommand Set(BimDocument document, ViewReference view, RoomColourBy by)
    {
        var before = document.ViewSettings.ColourByOf(view);
        return new DelegateCommand("Colour Scheme", () => document.ViewSettings.SetColourBy(view, by), () => document.ViewSettings.SetColourBy(view, before));
    }
}
