using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIMDesigner.Core.Documents;
using BIMDesigner.Infrastructure.Projects;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Keys of one's own for any command on the ribbon - single letters, runs of letters, keys held
/// with Ctrl, Alt or Shift - kept for the person and handed round as a file; and the views open
/// as tabs over the drawing area, with the 3D view in a window of its own for a second screen.
/// </summary>
[Collection("Wpf")]
public class ViewsAndKeysTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static void OnUiThread(Action<MainWindow> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var window = new MainWindow();
                typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(window, new object?[] { BimDocument.CreateDefault(), null, false });
                action(window);
            }
            catch (Exception exception) { failure = exception; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }

    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Hidden)!.GetValue(window)!;

    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetProperty(name, Hidden)!.GetValue(window)!;

    private static object? Call(MainWindow window, string name, params object?[] arguments) =>
        typeof(MainWindow).GetMethods(Hidden).First(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(window, arguments);

    private static bool Press(MainWindow window, Key key, ModifierKeys held = ModifierKeys.None) => (bool)Call(window, "TakeShortcut", key, held, false)!;

    private static PlanTool ToolOf(MainWindow window) => Field<PlanView>(window, "Plan").ActiveTool;

    [Fact]
    public void KeysAreWrittenOneWayAndTheProgramsOwnAreNeverACommands()
    {
        Assert.Equal("Ctrl+Shift+V", KeyChords.Tidy("shift + ctrl+v"));
        Assert.Equal("WA", KeyChords.Tidy("wa"));
        Assert.Equal("F12", KeyChords.Tidy("f12"));
        Assert.Equal("Alt+3", KeyChords.Tidy("ALT+3"));
        Assert.Null(KeyChords.Tidy("W1"));
        Assert.Null(KeyChords.Tidy("ABCDEF"));
        Assert.Null(KeyChords.Tidy("Ctrl+Ctrl+A"));
        Assert.NotNull(KeyChords.Refusal("Ctrl+Z"));
        Assert.NotNull(KeyChords.Refusal("Ctrl+S"));
        Assert.Null(KeyChords.Refusal("Ctrl+W"));
        Assert.Equal("Ctrl+Shift+V", KeyChords.Of(Key.V, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(KeyChords.Of(Key.OemComma, ModifierKeys.None));
    }

    [Fact]
    public void EveryCommandOnTheRibbonCanHaveKeysAndTheOldOnesAreItsDefaults()
    {
        OnUiThread(window =>
        {
            var commands = Get<IReadOnlyList<ShortcutCommand>>(window, "ShortcutCommands");
            Assert.True(commands.Count > 150, $"{commands.Count} commands");
            Assert.Equal(commands.Count, commands.Select(command => command.Id).Distinct().Count());
            foreach (var id in ShortcutMap.Defaults.Keys) Assert.Contains(commands, command => command.Id == id);
            Assert.Contains(commands, command => command.Name == "Purge Unused" && command.Path == "Manage > Project Data");

            // As before: a letter picks its tool, a function key the floor, Ctrl+3 the 3D view - which the tools then wait for.
            var map = Get<ShortcutMap>(window, "Shortcuts");
            Assert.Equal("FloorTool", map.Owner("F2"));
            Assert.True(Press(window, Key.W));
            Assert.Equal(PlanTool.Wall, ToolOf(window));
            map.Assign("WindowTool", "F6");
            Assert.True(Press(window, Key.F6));
            Assert.Equal(PlanTool.Window, ToolOf(window));
            Assert.True(Press(window, Key.D3, ModifierKeys.Control));
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "ModelPanel").Visibility);
            Assert.True(Press(window, Key.D));
            Assert.Equal(PlanTool.Window, ToolOf(window));
            Assert.Contains("close the 3D view first", Field<TextBlock>(window, "StatusHint").Text);
            Assert.True(Press(window, Key.D3, ModifierKeys.Control));
            Assert.True(Press(window, Key.D));
            Assert.Equal(PlanTool.Door, ToolOf(window));

            // A figure alone is a length being typed, not a shortcut; a key nothing has is let go.
            Assert.False(Press(window, Key.D4));
            Assert.False(Press(window, Key.J));
        });
    }

    [Fact]
    public void ARunOfLettersWaitsForTheRestWhereItBeginsALongerOne()
    {
        OnUiThread(window =>
        {
            var map = Get<ShortcutMap>(window, "Shortcuts");
            var taken = map.Assign("WindowTool", "WA");
            Assert.Null(taken);
            Assert.True(map.Begins("W"));

            // W then A: the window, not the wall.
            Press(window, Key.W);
            Assert.NotEqual(PlanTool.Wall, ToolOf(window));
            Assert.StartsWith("W…", Field<TextBlock>(window, "StatusHint").Text);
            Press(window, Key.A);
            Assert.Equal(PlanTool.Window, ToolOf(window));

            // W and a moment: the wall.
            Press(window, Key.W);
            Call(window, "FinishTypedKeys");
            Assert.Equal(PlanTool.Wall, ToolOf(window));

            // W then a letter that goes no further: the wall, then that letter's own.
            Press(window, Key.S);
            Press(window, Key.W);
            Press(window, Key.D);
            Assert.Equal(PlanTool.Door, ToolOf(window));

            // Keys given to another command are taken off the one that had them.
            Assert.Equal("WallTool", map.Assign("DoorTool", "W"));
            Assert.Empty(map.KeysOf("WallTool"));
        });
    }

    [Fact]
    public void ShortcutsAreKeptForThePersonAndReadFromAnotherProgramsFileByName()
    {
        OnUiThread(window =>
        {
            var commands = Get<IReadOnlyList<ShortcutCommand>>(window, "ShortcutCommands");
            var map = Get<ShortcutMap>(window, "Shortcuts").Copy();
            map.Assign("WallTool", "WW");
            map.Remove("TrimTool", "T");
            Call(window, "UseShortcuts", map);
            Assert.True(File.Exists(ShortcutFile.Kept));

            // The next window - the next time the program starts - has them.
            var next = new MainWindow();
            var kept = Get<ShortcutMap>(next, "Shortcuts");
            Assert.Contains("WW", kept.KeysOf("WallTool"));
            Assert.Contains("W", kept.KeysOf("WallTool"));
            Assert.Empty(kept.KeysOf("TrimTool"));

            // Another program's file: its commands matched to these by name.
            var file = Path.Combine(Path.GetTempPath(), $"shortcuts-{Guid.NewGuid():N}.xml");
            File.WriteAllText(file,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><Shortcuts>"
                + "<ShortcutItem CommandName=\"Wall\" CommandId=\"ID_OBJECTS_WALL\" Shortcuts=\"WA\" Paths=\"Architecture&gt;Build\" />"
                + "<ShortcutItem CommandName=\"Door\" CommandId=\"ID_OBJECTS_DOOR\" Shortcuts=\"DR#Ctrl+Z\" Paths=\"Architecture&gt;Build\" />"
                + "<ShortcutItem CommandName=\"Somewhere Else\" CommandId=\"ID_ELSEWHERE\" Shortcuts=\"QQ\" Paths=\"Nowhere\" />"
                + "</Shortcuts>");
            try
            {
                var read = ShortcutFile.Read(file)!;
                var imported = ShortcutMap.Starting(commands);
                Assert.Equal(1, imported.Take(read, commands));
                Assert.Equal(new[] { "WA" }, imported.KeysOf("WallTool"));
                Assert.Equal(new[] { "DR" }, imported.KeysOf("DoorTool"));
            }
            finally
            {
                File.Delete(file);
                File.Delete(ShortcutFile.Kept);
            }
        });
    }

    [Fact]
    public void TheViewsOpenAreTabsAndThe3DViewCanHaveAWindowOfItsOwn()
    {
        OnUiThread(window =>
        {
            var document = Field<PlanView>(window, "Plan").Document!;
            IReadOnlyList<(string Title, bool Shown)> Open() => (IReadOnlyList<(string, bool)>)Call(window, "OpenViews")!;

            Assert.Equal(new[] { ("Floor Plan: Ground Floor", true) }, Open());

            // Another storey, then 3D over the plan: each a tab, only what is in sight lit.
            Field<ComboBox>(window, "LevelPicker").SelectedItem = document.Levels[1];
            Assert.Contains(("Floor Plan: First Floor", true), Open());
            Call(window, "OnToggle3D", window, new RoutedEventArgs());
            Assert.Equal(new[] { ("Floor Plan: Ground Floor", false), ("Floor Plan: First Floor", false), ("{3D}", true) }, Open());

            // A tab picked: its view in front again.
            Assert.True((bool)Call(window, "PickViewTab", "Floor Plan: Ground Floor")!);
            Assert.Equal(document.Levels[0].Id, Field<PlanView>(window, "Plan").ActiveLevelId);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "ModelPanel").Visibility);
            Assert.Contains(("Floor Plan: Ground Floor", true), Open());

            // Closed: the 3D view's tab goes; Close Inactive leaves what is in sight.
            Assert.True((bool)Call(window, "CloseViewTab", "{3D}")!);
            Assert.DoesNotContain(Open(), tab => tab.Title == "{3D}");
            Call(window, "OnCloseInactiveViews", window, new RoutedEventArgs());
            Assert.Equal(new[] { ("Floor Plan: Ground Floor", true) }, Open());

            // The plan in sight, its tab closed with no other: it stays.
            Assert.True((bool)Call(window, "CloseViewTab", "Floor Plan: Ground Floor")!);
            Assert.Single(Open());

            // The 3D view in a window of its own: the plan stays in sight beside it, and both are lit.
            var own = (Window)Call(window, "FloatModel")!;
            Assert.Same(Field<FrameworkElement>(window, "ModelPanel"), own.Content);
            Assert.Contains(("Floor Plan: Ground Floor", true), Open());
            Assert.Contains(("{3D}", true), Open());

            // Closed there: back in the main window, and closed.
            own.Close();
            var grid = Field<Grid>(window, "ViewArea");
            Assert.Contains(Field<FrameworkElement>(window, "ModelPanel"), grid.Children.Cast<UIElement>());
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "ModelPanel").Visibility);

            // Docked from its own button: back, and still open.
            Call(window, "FloatModel");
            Call(window, "DockModel");
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "ModelPanel").Visibility);
            Assert.Contains(Field<FrameworkElement>(window, "ModelPanel"), grid.Children.Cast<UIElement>());
        });
    }
}
