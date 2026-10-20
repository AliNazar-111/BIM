using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Annotation;

/// <summary>One keynote: its key, what it says, and the heading it comes under - none for a heading.</summary>
public sealed record KeynoteEntry(string Key, string Text, string Parent);

/// <summary>
/// The project's keynotes (specification section 6.3): keys and what they say, under headings.
/// Read from a tab-separated file - key, text, and the key it comes under - as offices keep them,
/// or begun from a starter set. A drawing carries the key; the legend on the sheet says it in full.
/// </summary>
public sealed class KeynoteTable
{
    public List<KeynoteEntry> Entries { get; } = new();

    /// <summary>The file it was read from, to read again; empty where it was not.</summary>
    public string Source { get; set; } = string.Empty;

    public KeynoteEntry? Find(string key) =>
        string.IsNullOrWhiteSpace(key) ? null : Entries.FirstOrDefault(entry => entry.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>What a key says, or nothing where the table has no such key.</summary>
    public string TextOf(string key) => Find(key)?.Text ?? string.Empty;

    /// <summary>The keys that are notes rather than headings: what is put on a drawing.</summary>
    public IEnumerable<KeynoteEntry> Notes => Entries.Where(entry => !Entries.Any(other => other.Parent.Equals(entry.Key, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A keynote file read: a line a keynote - its key, a tab, its text, and a tab and the key it
    /// comes under where it comes under one. Blank lines, and those begun with # or //, are not
    /// keynotes. Text in double quotes keeps its quotes off.
    /// </summary>
    public static KeynoteTable Parse(string text, out IReadOnlyList<string> problems)
    {
        var table = new KeynoteTable();
        var found = new List<string>();
        var number = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            number++;
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#') || line.TrimStart().StartsWith("//")) continue;
            var parts = line.Split('\t').Select(part => part.Trim().Trim('"')).ToArray();
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                found.Add($"Line {number} has no key and text separated by a tab.");
                continue;
            }

            if (table.Find(parts[0]) is not null)
            {
                found.Add($"Line {number}: {parts[0]} is there already.");
                continue;
            }

            table.Entries.Add(new KeynoteEntry(parts[0], parts[1], parts.Length > 2 ? parts[2] : string.Empty));
        }

        foreach (var entry in table.Entries.Where(entry => entry.Parent.Length > 0 && table.Find(entry.Parent) is null))
            found.Add($"{entry.Key} comes under {entry.Parent}, which is not a key.");

        problems = found;
        return table;
    }

    /// <summary>The table as a keynote file is written.</summary>
    public string Write() => string.Join(Environment.NewLine, Entries.Select(entry =>
        entry.Parent.Length == 0 ? $"{entry.Key}\t{entry.Text}" : $"{entry.Key}\t{entry.Text}\t{entry.Parent}")) + Environment.NewLine;

    public KeynoteTable Copy()
    {
        var copy = new KeynoteTable { Source = Source };
        copy.Entries.AddRange(Entries);
        return copy;
    }

    public void CopyFrom(KeynoteTable other)
    {
        Source = other.Source;
        Entries.Clear();
        Entries.AddRange(other.Entries);
    }

    /// <summary>
    /// A starter set, headed by the group elements and elements of the RICS New Rules of
    /// Measurement (NRM1) a cost plan is set out by; the notes under them are this set's own,
    /// for the walls, floors, roofs and fittings a house is drawn with - to be replaced, or added
    /// to, by an office's own file.
    /// </summary>
    public static KeynoteTable Starter()
    {
        const string source = """
            1	Substructure
            1.1	Substructure	1
            1.1.01	Concrete strip foundation, to the engineer's design	1.1
            1.1.02	Trench fill foundation, to the engineer's design	1.1
            1.1.03	Ground bearing concrete slab on damp proof membrane on compacted hardcore	1.1
            1.1.04	Rigid insulation under the slab	1.1
            2	Superstructure
            2.1	Frame	2
            2.1.01	Steel column, to the engineer's design	2.1
            2.1.02	Steel beam, to the engineer's design	2.1
            2.1.03	Timber post	2.1
            2.2	Upper floors	2
            2.2.01	Timber joists, to the engineer's design	2.2
            2.2.02	Precast concrete beam and block floor	2.2
            2.2.03	18 mm tongued and grooved chipboard deck	2.2
            2.3	Roof	2
            2.3.01	Interlocking concrete tiles on battens and breathable membrane	2.3
            2.3.02	Natural slates on battens and breathable membrane	2.3
            2.3.03	Single ply membrane on insulation on deck	2.3
            2.3.04	Timber rafters, to the engineer's design	2.3
            2.3.05	Mineral wool insulation between and over ceiling joists	2.3
            2.3.06	Fascia and ventilated soffit	2.3
            2.3.07	Gutter and downpipe	2.3
            2.4	Stairs and ramps	2
            2.4.01	Timber staircase	2.4
            2.4.02	Handrail at 900 mm above the pitch line	2.4
            2.5	External walls	2
            2.5.01	Facing brick outer leaf, 102.5 mm	2.5
            2.5.02	Cavity, fully filled with insulation	2.5
            2.5.03	Aircrete block inner leaf	2.5
            2.5.04	Render on blockwork	2.5
            2.5.05	Stainless steel wall ties	2.5
            2.5.06	Cavity tray with weep holes over the opening	2.5
            2.5.07	Insulated cavity closer round the opening	2.5
            2.6	Windows and external doors	2
            2.6.01	Double glazed window	2.6
            2.6.02	External door	2.6
            2.6.03	Safety glazing in a critical location	2.6
            2.7	Internal walls and partitions	2
            2.7.01	Timber stud partition, 12.5 mm plasterboard each side	2.7
            2.7.02	Blockwork partition	2.7
            2.7.03	Mineral wool sound insulation in the partition	2.7
            2.8	Internal doors	2
            2.8.01	Internal door, flush, painted	2.8
            2.8.02	Fire door FD30S, self-closing	2.8
            3	Internal finishes
            3.1	Wall finishes	3
            3.1.01	Plaster skim on plasterboard, painted	3.1
            3.1.02	Ceramic wall tiles	3.1
            3.2	Floor finishes	3
            3.2.01	Sand and cement screed	3.2
            3.2.02	Carpet on underlay	3.2
            3.2.03	Ceramic floor tiles	3.2
            3.2.04	Sheet vinyl	3.2
            3.3	Ceiling finishes	3
            3.3.01	12.5 mm plasterboard, skimmed and painted	3.3
            4	Fittings, furnishings and equipment
            4.1	Fittings, furnishings and equipment	4
            4.1.01	Kitchen units and worktop	4.1
            4.1.02	Fitted wardrobe	4.1
            5	Services
            5.1	Sanitary installations	5
            5.1.01	WC	5.1
            5.1.02	Wash basin	5.1
            5.1.03	Bath or shower	5.1
            5.6	Space heating and air conditioning	5
            5.6.01	Radiator with thermostatic valve	5.6
            5.7	Ventilation	5
            5.7.01	Extract fan, ducted to outside	5.7
            5.8	Electrical installations	5
            5.8.01	Socket outlet	5.8
            5.8.02	Light fitting	5.8
            5.11	Fire and lightning protection	5
            5.11.01	Mains smoke alarm, interlinked	5.11
            8	External works
            8.2	Roads, paths, pavings and surfacings	8
            8.2.01	Block paving on sand on sub-base	8.2
            8.2.02	Concrete paving slabs	8.2
            8.3	Soft landscaping, planting and irrigation systems	8
            8.3.01	Turf on topsoil	8.3
            8.4	Fencing, railings and walls	8
            8.4.01	Close boarded timber fence	8.4
            8.6	External drainage	8
            8.6.01	Gully	8.6
            """;

        var table = Parse(source, out _);
        table.Source = string.Empty;
        return table;
    }
}

/// <summary>Whose keynote a keynote tag reads.</summary>
public enum KeynoteSource
{
    /// <summary>The keynote of the element's type.</summary>
    Element,

    /// <summary>The keynote of the material where it points: a wall's layer, a floor's or roof's top.</summary>
    Material,

    /// <summary>A key picked from the table: a note of the drawing's own.</summary>
    User
}

/// <summary>What a keynote tag shows of its keynote.</summary>
public enum KeynoteDisplay
{
    Key,
    Text,
    KeyAndText
}

/// <summary>
/// A keynote tag (specification section 6.3): a key in a box, with a leader to what it is the
/// note of - read off the element's type, or the material it points at, or picked from the table.
/// The key is read when it is drawn, so a type given another keynote changes every tag of it.
/// </summary>
public sealed class KeynoteTag : Element
{
    public override BuiltInCategory Category => BuiltInCategory.KeynoteTags;

    public KeynoteSource Source { get; set; } = KeynoteSource.Element;

    /// <summary>The element it is the note of, for an element's or a material's keynote.</summary>
    public Guid TargetId { get; set; }

    /// <summary>The material it is the note of, for a material's keynote.</summary>
    public Guid MaterialId { get; set; }

    /// <summary>The key, for a keynote of its own.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Where the box sits.</summary>
    public Point2D Position { get; set; }

    /// <summary>Where its leader points: on what it is the note of. Null for no leader.</summary>
    public Point2D? LeaderEnd { get; set; }

    public KeynoteDisplay Display { get; set; } = KeynoteDisplay.Key;

    /// <summary>The key it carries now: off the type, off the material, or its own.</summary>
    public string KeyIn(BimDocument document) => Source switch
    {
        KeynoteSource.Element => document.FindElement(TargetId) is { } target
            ? document.ElementTypes.FirstOrDefault(type => type.Id == target.TypeId)?.Keynote ?? string.Empty
            : string.Empty,
        KeynoteSource.Material => document.FindMaterial(MaterialId)?.Keynote ?? string.Empty,
        _ => Key
    };

    /// <summary>What it says on the drawing: its key, what the key says, or both - "?" where it has no key.</summary>
    public string Label(BimDocument document)
    {
        var key = KeyIn(document);
        if (string.IsNullOrWhiteSpace(key)) return "?";
        var text = document.Keynotes.TextOf(key);
        return Display switch
        {
            KeynoteDisplay.Text => text.Length > 0 ? text : key,
            KeynoteDisplay.KeyAndText => text.Length > 0 ? $"{key}  {text}" : key,
            _ => key
        };
    }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.ReadOnly(KeynoteTagParameters.KeyValue, () => KeyIn(document));
        yield return ParameterValue.ReadOnly(KeynoteTagParameters.Text, () => document.Keynotes.TextOf(KeyIn(document)));
        yield return ParameterValue.ReadOnly(KeynoteTagParameters.Source, () => EnumText.Humanise(Source));
        if (Source == KeynoteSource.User)
            yield return ParameterValue.BindChoice(KeynoteTagParameters.Key, () => Key, v => Key = v,
                document.Keynotes.Notes.Select(entry => entry.Key).Append(Key).Distinct().ToList());
        yield return ParameterValue.BindChoice(KeynoteTagParameters.Display, () => EnumText.Humanise(Display),
            v => { if (EnumText.TryParse<KeynoteDisplay>(v, out var display)) Display = display; }, EnumText.Choices<KeynoteDisplay>());
        yield return ParameterValue.Bind(KeynoteTagParameters.Leader, () => LeaderEnd is not null, v =>
        {
            if (!v) LeaderEnd = null;
            else LeaderEnd ??= Position + new Vector2D(-600, -600);
        });

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => "Keynote";
}

public static class KeynoteTagParameters
{
    public static readonly ParameterDefinition KeyValue =
        new("Key Value", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Text =
        new("Keynote Text", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Source =
        new("Keynote Of", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Key =
        new("Key", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);

    public static readonly ParameterDefinition Display =
        new("Shows", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Graphics);

    public static readonly ParameterDefinition Leader =
        new("Leader", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Graphics);
}

/// <summary>Keynotes found on the model, and the legend listing those a sheet - or the project - uses.</summary>
public static class Keynotes
{
    /// <summary>
    /// The material a keynote points at, where it points on an element: a wall's layer under the
    /// point; the top layer of a floor, roof or ceiling. Null where it is none of those.
    /// </summary>
    public static Material? MaterialAt(BimDocument document, Element element, Point2D point)
    {
        switch (element)
        {
            case Wall wall when document.GetWallType(wall) is { } type:
            {
                // Across the wall from its exterior face, layer by layer.
                var (_, across) = wall.Locate(type.Structure, point);
                var depth = type.Structure.TotalWidth / 2 - across;
                foreach (var layer in type.Structure.Layers)
                {
                    if (depth <= layer.Thickness) return document.FindMaterial(layer.MaterialId);
                    depth -= layer.Thickness;
                }

                return type.Structure.Layers.Count > 0 ? document.FindMaterial(type.Structure.Layers[^1].MaterialId) : null;
            }

            default:
                var structure = document.ElementTypes.FirstOrDefault(type => type.Id == element.TypeId) switch
                {
                    SlabType slab => slab.Structure,
                    _ => null
                };
                return structure is { Layers.Count: > 0 } ? document.FindMaterial(structure.Layers[0].MaterialId) : null;
        }
    }

    /// <summary>Whether a view shows a keynote tag: the plan of its storey, or a callout of that plan round it.</summary>
    public static bool Shows(BimDocument document, ViewReference view, KeynoteTag tag) => view.Kind switch
    {
        ViewKind.FloorPlan => view.TargetId == tag.LevelId,
        ViewKind.Callout => document.FindElement(view.TargetId) is Callout callout
                            && callout.Parent.Kind == ViewKind.FloorPlan && callout.Parent.TargetId == tag.LevelId
                            && callout.Region is var region
                            && tag.Position.X >= region.MinX && tag.Position.X <= region.MaxX && tag.Position.Y >= region.MinY && tag.Position.Y <= region.MaxY,
        _ => false
    };

    /// <summary>The keys a legend lists: those tagged anywhere, or in the views on a sheet - in the table's order.</summary>
    public static IReadOnlyList<KeynoteEntry> Used(BimDocument document, Sheet? sheet)
    {
        var tags = document.Elements.OfType<KeynoteTag>()
            .Where(tag => sheet is null || sheet.Viewports.Any(viewport => Shows(document, viewport.View, tag)));
        var keys = tags.Select(tag => tag.KeyIn(document)).Where(key => key.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Keys tagged that the table does not have are listed too, saying so, after those it does.
        var listed = document.Keynotes.Entries.Where(entry => keys.Contains(entry.Key)).ToList();
        listed.AddRange(keys.Where(key => document.Keynotes.Find(key) is null).OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => new KeynoteEntry(key, "(not in the keynote table)", string.Empty)));
        return listed;
    }

    /// <summary>The legend laid out as a table: by sheet, the keys tagged in its views; else every key tagged.</summary>
    public static ScheduleTable Legend(BimDocument document, Sheet? sheet, bool bySheet) =>
        ScheduleTable.Of("Keynote Legend", new[] { "Key", "Keynote" }, Used(document, bySheet ? sheet : null).Select(entry => (IReadOnlyList<string>)new[] { entry.Key, entry.Text }));

    /// <summary>A legend on a sheet: the sheet it is on, whether it lists that sheet's keys alone, and its table.</summary>
    public static ScheduleTable LegendFor(BimDocument document, Viewport viewport)
    {
        var sheet = document.Elements.OfType<Sheet>().FirstOrDefault(candidate => candidate.Viewports.Contains(viewport));
        return Legend(document, sheet, viewport.View.Index == 1);
    }
}
