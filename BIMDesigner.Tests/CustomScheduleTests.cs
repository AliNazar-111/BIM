using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;

namespace BIMDesigner.Tests;

/// <summary>
/// Schedules made in the project: formulas worked out for each row, shares of a total, cells
/// coloured by a rule, kept with the project and laid out as a table on a sheet.
/// </summary>
public class CustomScheduleTests
{
    private static (BimDocument Document, List<Wall> Walls) Walls(params (double Length, string Mark, double Height)[] walls)
    {
        var document = BimDocument.CreateDefault();
        var exterior = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior);
        var interior = document.TypesOf<WallType>().First(t => t.Function == WallFunction.Interior);
        var placed = new List<Wall>();
        for (var i = 0; i < walls.Length; i++)
        {
            var wall = new Wall
            {
                Start = new Point2D(0, i * 5000), End = new Point2D(walls[i].Length, i * 5000), Mark = walls[i].Mark,
                TypeId = i % 2 == 0 ? exterior.Id : interior.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = walls[i].Height
            };
            document.Add(wall);
            placed.Add(wall);
        }

        return (document, placed);
    }

    private static double Evaluate(string formula, params (string Name, double Value)[] fields) =>
        ScheduleFormula.Evaluate(formula, fields.Select(f => f.Name).ToList(), name => fields.First(f => f.Name == name).Value);

    [Fact]
    public void AFormulaIsWorkedOutAsWritten()
    {
        Assert.Equal(14, Evaluate("2 + 3 * 4"));
        Assert.Equal(20, Evaluate("(2 + 3) * 4"));
        Assert.Equal(-8, Evaluate("-2 ^ 3"));
        Assert.Equal(1, Evaluate("3 >= 3"));
        Assert.Equal(0, Evaluate("3 <> 3"));
        Assert.Equal(7, Evaluate("if(2 < 1, 5, 7)"));
        Assert.Equal(1, Evaluate("and(1, not(0), or(0, 2))"));
        Assert.Equal(3, Evaluate("round(2.5)"));
        Assert.Equal(3, Evaluate("roundup(2.1)"));
        Assert.Equal(2, Evaluate("rounddown(2.9)"));
        Assert.Equal(4, Evaluate("max(1, abs(-4), sqrt(9))"));

        // Fields by name, spaces and all - the longest name first - or in square brackets.
        Assert.Equal(1800, Evaluate("Clear Width * 2", ("Width", 1000), ("Clear Width", 900)));
        Assert.Equal(1900, Evaluate("[Clear Width] + Width", ("Width", 1000), ("Clear Width", 900)));
        Assert.Equal(1, Evaluate("if(Width < 1200, 1, 0)", ("Width", 1000)));

        // And why where it cannot be.
        Assert.Contains("not a field", ScheduleFormula.Check("Depth * 2", new[] { "Width" }));
        Assert.Contains("bracket", ScheduleFormula.Check("(Width * 2", new[] { "Width" }));
        Assert.Contains("nought", Assert.Throws<FormulaException>(() => Evaluate("Width / 0", ("Width", 1))).Message);
        Assert.Contains("takes 3", ScheduleFormula.Check("if(1, 2)", Array.Empty<string>()));
        Assert.Null(ScheduleFormula.Check("Width * Height", new[] { "Width", "Height" }));
    }

    [Fact]
    public void ACalculatedFieldIsWorkedOutForEachRowAndCanBeSortedAndFilteredOn()
    {
        var (document, _) = Walls((5000, "W1", 3000), (3000, "W2", 2400), (8000, "W3", 3000));
        var schedule = new ScheduleDefinition("Wall faces", BuiltInCategory.Walls, "Mark", "Length", "Face", "Tall") { SortBy = "Face" };
        schedule.Calculated.Add(new CalculatedField { Name = "Face", Kind = CalculatedKind.Area, Formula = "Length * Unconnected Height" });
        schedule.Calculated.Add(new CalculatedField { Name = "Tall", Kind = CalculatedKind.YesNo, Formula = "Unconnected Height >= 3000" });

        var result = Schedule.Run(document, schedule);
        Assert.Equal(new[] { "W2", "W1", "W3" }, result.Rows.Select(row => row.Text("Mark")));
        Assert.Equal(new[] { "7.20 m²", "15.00 m²", "24.00 m²" }, result.Rows.Select(row => row.Text("Face")));
        Assert.Equal(new[] { "No", "Yes", "Yes" }, result.Rows.Select(row => row.Text("Tall")));

        // Totalled as an area; read-only, as nothing can be typed into what is worked out.
        var face = result.Columns.Single(column => column.Field == "Face");
        Assert.Equal((ParameterDataType.Area, true, true), (face.DataType, face.IsTotalled, face.IsReadOnly));
        Assert.Equal(46.2e6, result.Totals["Face"], 3);
        Assert.Empty(result.Problems);

        // A filter on what is worked out.
        schedule.Filters.Add(new ScheduleFilter("Tall", ScheduleOperator.Equals, "Yes"));
        Assert.Equal(new[] { "W1", "W3" }, Schedule.Run(document, schedule).Rows.Select(row => row.Text("Mark")));

        // A formula that cannot be worked out leaves its cells empty, and says why.
        schedule.Calculated.Add(new CalculatedField { Name = "Odd", Formula = "Length / (Unconnected Height - 3000)" });
        schedule.Fields.Add("Odd");
        schedule.Filters.Clear();
        var odd = Schedule.Run(document, schedule);
        Assert.Contains("nought", odd.Problems["Odd"]);
        Assert.Equal(ParameterFormatter.NoValue, odd.Rows.First(row => row.Text("Mark") == "W1").Text("Odd"));
        Assert.Equal("-5", odd.Rows.First(row => row.Text("Mark") == "W2").Text("Odd"));
    }

    [Fact]
    public void APercentageIsAShareOfTheWholeOrOfItsGroup()
    {
        var (document, _) = Walls((6000, "W1", 3000), (2000, "W2", 3000), (2000, "W3", 3000));
        var schedule = new ScheduleDefinition("Shares", BuiltInCategory.Walls, "Mark", "Function", "Length", "Share", "Of Group") { SortBy = "Mark", GroupBy = "Function" };
        schedule.Calculated.Add(new CalculatedField { Name = "Share", Kind = CalculatedKind.Percentage, PercentageOf = "Length" });
        schedule.Calculated.Add(new CalculatedField { Name = "Of Group", Kind = CalculatedKind.Percentage, PercentageOf = "Length", ByGroup = true });

        var result = Schedule.Run(document, schedule);
        Assert.Equal(new[] { "60%", "20%", "20%" }, result.Rows.Select(row => row.Text("Share")));

        // W1 and W3 are outside walls, W2 inside: 75% and 25% of the outside walls, all of the inside.
        Assert.Equal(new[] { "75%", "100%", "25%" }, result.Rows.Select(row => row.Text("Of Group")));
        Assert.Equal(100, result.Totals["Share"], 6);
        Assert.False(result.Columns.Single(column => column.Field == "Of Group").IsTotalled);
    }

    [Fact]
    public void CellsAreColouredWhereTheyPassARule()
    {
        var (document, _) = Walls((5000, "W1", 3000), (3000, "W2", 2400), (8000, "W3", 3000));
        var red = new ColourRgb(0xF8, 0xC8, 0xC8);
        var green = new ColourRgb(0xCD, 0xEB, 0xC8);
        var schedule = new ScheduleDefinition("Walls", BuiltInCategory.Walls, "Mark", "Length", "Area") { SortBy = "Mark" };
        schedule.Formats.Add(new ConditionalFormat { Field = "Length", Test = FormatTest.LessThan, Value = "4000", Background = red });

        // An area is compared in square metres, as it reads; and a bare number to a length is millimetres.
        schedule.Formats.Add(new ConditionalFormat { Field = "Area", Test = FormatTest.Between, Value = "10", Value2 = "20", Background = green });
        schedule.Formats.Add(new ConditionalFormat { Field = "Mark", Test = FormatTest.Contains, Value = "3", Background = green });

        var result = Schedule.Run(document, schedule);
        var rows = result.Rows.ToDictionary(row => row.Text("Mark"));
        Assert.Equal(red, result.BackgroundOf(rows["W2"], "Length"));
        Assert.Null(result.BackgroundOf(rows["W1"], "Length"));
        Assert.Equal(green, result.BackgroundOf(rows["W3"], "Mark"));
        Assert.Null(result.BackgroundOf(rows["W1"], "Mark"));

        var areas = rows.Values.Where(row => result.BackgroundOf(row, "Area") is not null).Select(row => row.Text("Mark")).ToList();
        Assert.Equal(rows.Values.Where(row => Schedule.Numeric(row["Area"]) is >= 10e6 and <= 20e6).Select(row => row.Text("Mark")), areas);
        Assert.NotEmpty(areas);

        Assert.Equal(7e6, ConditionalFormat.Threshold(ParameterDataType.Area, "7"));
        Assert.Equal(7e6, ConditionalFormat.Threshold(ParameterDataType.Area, "7 m²"));
        Assert.Equal(775, ConditionalFormat.Threshold(ParameterDataType.Length, "0.775 m"));
        Assert.Equal(25, ConditionalFormat.Threshold(ParameterDataType.Percentage, "25%"));
    }

    [Fact]
    public void SchedulesAreKeptWithTheProjectAndPlacedOnSheetsByTheirId()
    {
        var (document, _) = Walls((5000, "W1", 3000), (3000, "W2", 2400));
        Assert.Contains(document.Schedules, schedule => schedule.Name == "Sheet List");

        var schedule = document.Schedules.First(s => s.Name == "Wall Schedule").Copy("Wall Faces");
        schedule.Fields.Add("Face");
        schedule.Calculated.Add(new CalculatedField { Name = "Face", Kind = CalculatedKind.Area, Formula = "Length * [Unconnected Height]" });
        schedule.Formats.Add(new ConditionalFormat { Field = "Face", Test = FormatTest.GreaterThan, Value = "10", Background = new ColourRgb(1, 2, 3) });
        schedule.ShowFormatsOnSheets = true;
        new UndoStack().Execute(ScheduleCommands.Add(document, schedule));

        var sheet = new Sheet();
        document.Add(sheet);
        sheet.Add(new Viewport { View = ViewReference.Schedule(schedule), Centre = new Point2D(200, 300), ShowTitle = false });
        Assert.Equal(string.Empty, sheet.Viewports[0].DetailNumber);

        // Laid out as a table: a title, its headings, a row a wall, and its total; the viewport the table's size.
        var table = ScheduleTable.Of(document, schedule);
        Assert.Equal(ScheduleLineKind.Title, table.Lines[0].Kind);
        Assert.Equal(schedule.Fields, table.Lines[1].Cells);
        Assert.Contains(table.Lines, line => line.Kind == ScheduleLineKind.Total);
        Assert.Contains(table.Lines, line => line.Backgrounds.Any(colour => colour == new ColourRgb(1, 2, 3)));
        var bounds = sheet.Viewports[0].PaperBounds(document);
        Assert.Equal(table.Width, bounds.Width, 6);
        Assert.Equal(table.Height, bounds.Height, 6);
        Assert.True(table.ColumnWidths.All(width => width > ScheduleTable.Padding * 2));

        // Renamed, it is still the one on the sheet.
        schedule.Name = "Faces of Walls";
        Assert.Equal("Faces of Walls", sheet.Viewports[0].TitleIn(document));

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var kept = reopened.Schedules.Single(s => s.Id == schedule.Id);
        Assert.Equal(("Faces of Walls", schedule.Fields.Count, "Length * [Unconnected Height]", CalculatedKind.Area), (kept.Name, kept.Fields.Count, kept.Calculated[0].Formula, kept.Calculated[0].Kind));
        Assert.Equal((FormatTest.GreaterThan, new ColourRgb(1, 2, 3), true), (kept.Formats[0].Test, kept.Formats[0].Background, kept.ShowFormatsOnSheets));
        Assert.Equal(document.Schedules.Count, reopened.Schedules.Count);
        Assert.Same(kept, reopened.Elements.OfType<Sheet>().Single().Viewports[0].View.ScheduleIn(reopened));

        // Deleted, it comes off the sheet too; undone, it is back on it.
        var history = new UndoStack();
        history.Execute(ScheduleCommands.Delete(document, schedule));
        Assert.DoesNotContain(schedule, document.Schedules);
        Assert.Empty(sheet.Viewports);
        history.Undo();
        Assert.Contains(schedule, document.Schedules);
        Assert.Single(sheet.Viewports);
    }

    [Fact]
    public void AnOlderFileOpensWithTheSchedulesAProjectStartsWith()
    {
        var json = ProjectFile.ToJson(BimDocument.CreateDefault());
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove("schedules"));
        var reopened = ProjectFile.FromJson(node.ToJsonString());
        Assert.Equal(ScheduleDefinition.Defaults().Select(s => s.Name), reopened.Schedules.Select(s => s.Name));

        // A schedule placed by its name, before schedules had ids, is found by it.
        var reference = new ViewReference(ViewKind.Schedule, Guid.Empty, "Door Schedule");
        Assert.Same(reopened.Schedules.Single(s => s.Name == "Door Schedule"), reference.ScheduleIn(reopened));
    }

    [Fact]
    public void AnEditIsOneStepToUndo()
    {
        var document = BimDocument.CreateDefault();
        var schedule = document.Schedules.First();
        var edited = schedule.Copy(schedule.Name);
        edited.Fields.Remove("Cost");
        edited.Calculated.Add(new CalculatedField { Name = "Twice", Formula = "Length * 2", Kind = CalculatedKind.Length });
        edited.Fields.Add("Twice");
        var history = new UndoStack();
        history.Execute(ScheduleCommands.Change(schedule, edited));
        Assert.DoesNotContain("Cost", schedule.Fields);
        Assert.Single(schedule.Calculated);

        // The copy edited is not the schedule: changing it now does nothing to it.
        edited.Fields.Clear();
        Assert.Contains("Twice", schedule.Fields);

        history.Undo();
        Assert.Contains("Cost", schedule.Fields);
        Assert.Empty(schedule.Calculated);
        history.Redo();
        Assert.Contains("Twice", schedule.Fields);
    }
}

/// <summary>Schedules in the window: made, edited, duplicated, deleted, and drawn on a sheet.</summary>
[Collection("Wpf")]
public class ScheduleWindowTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethods(Hidden).First(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(window, arguments);

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

    [Fact]
    public void AScheduleIsMadeEditedAndPlacedOnASheet()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _, _) = DormerToolTests.House(10000, 8000, 40);
                var window = new MainWindow();
                Call(window, "LoadDocument", document, null, false);
                var history = Field<UndoStack>(window, "_history");

                // Made in the editor, checked there first: a formula that does not read is refused.
                var schedule = new ScheduleDefinition("Wall Areas", BuiltInCategory.Walls, "Mark", "Length");
                Func<ScheduleEditorWindow, bool> accept = editor =>
                {
                    editor.Schedule.Calculated.Add(new CalculatedField { Name = "Face", Kind = CalculatedKind.Area, Formula = "Length * Nothing" });
                    editor.Schedule.Fields.Add("Face");
                    Assert.Contains("Nothing", editor.Problems());
                    editor.Schedule.Calculated[0].Formula = "Length * Unconnected Height";
                    Assert.Null(editor.Problems());
                    return true;
                };
                Assert.True((bool)Call(window, "EditSchedule", schedule, true, accept)!);
                Assert.Contains(schedule, document.Schedules);
                Assert.Same(schedule, Field<System.Windows.Controls.ComboBox>(window, "SchedulePicker").SelectedItem);

                // A name another schedule has is refused.
                Assert.False((bool)Call(window, "EditSchedule", schedule, false, (Func<ScheduleEditorWindow, bool>)(editor =>
                {
                    editor.Schedule.Name = "Door Schedule";
                    return editor.Problems() is null;
                }))!);

                // Duplicated, then the copy deleted; each a step to undo.
                var copy = (ScheduleDefinition)Call(window, "DuplicateSchedule", schedule)!;
                Assert.Equal("Wall Areas 2", copy.Name);
                Call(window, "DeleteSchedule", copy);
                Assert.DoesNotContain(copy, document.Schedules);
                history.Undo();
                Assert.Contains(copy, document.Schedules);

                // On a sheet: drawn as a table, its rows read off the walls.
                var sheet = new Sheet();
                document.Add(sheet);
                var viewport = new Viewport { View = ViewReference.Schedule(schedule), Centre = new Point2D(300, 400), ShowTitle = false };
                sheet.Add(viewport);
                var renderer = new BIMDesigner.UI.Rendering.SheetRenderer { Document = document, Sheet = sheet, ShowGuides = false };
                renderer.SetTransform(p => new System.Windows.Point(p.X * 3, (sheet.Height - p.Y) * 3), 3);
                var visual = new System.Windows.Media.DrawingVisual();
                using (var dc = visual.RenderOpen()) renderer.DrawContent(dc);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(sheet.Width * 3), (int)(sheet.Height * 3), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(visual);

                // Inked inside the table's box.
                var box = viewport.PaperBounds(document);
                var pixels = new byte[(int)(box.Width * 3) * 4 * (int)(box.Height * 3)];
                bitmap.CopyPixels(new Int32Rect((int)(box.MinX * 3), (int)((sheet.Height - box.MaxY) * 3), (int)(box.Width * 3), (int)(box.Height * 3)), pixels, (int)(box.Width * 3) * 4, 0);
                Assert.True(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4] < 100) > 200);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }
}
