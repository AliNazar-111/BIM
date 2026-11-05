using System.IO;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Key schedules (Phase 1, 1.8b): a schedule of keys - room styles - each giving its fields to every
/// room set to it, following the key as it changes, and the room's own values back where it is set to none.
/// </summary>
public class KeyScheduleTests
{
    private static (BimDocument Document, Room Office, Room Toilet, ScheduleDefinition Styles) Rooms()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var office = new Room { LevelId = level, Location = new Point2D(1000, 1000), Name = "Office", Number = "1" };
        var toilet = new Room { LevelId = level, Location = new Point2D(5000, 1000), Name = "WC", Number = "2" };
        document.Add(office);
        document.Add(toilet);

        var styles = new ScheduleDefinition("Room Styles", BuiltInCategory.Rooms, ScheduleDefinition.KeyNameField, "Floor Finish", "Wall Finish") { KeyName = "Room Style" };
        var carpet = new ScheduleKey(styles, "Office");
        carpet.Values["Floor Finish"] = "Carpet tile";
        carpet.Values["Wall Finish"] = "Emulsion";
        var wet = new ScheduleKey(styles, "Wet Room");
        wet.Values["Floor Finish"] = "Vinyl sheet, coved";
        wet.Values["Wall Finish"] = "Ceramic tile";
        styles.Keys.Add(carpet);
        styles.Keys.Add(wet);
        document.Schedules.Add(styles);
        return (document, office, toilet, styles);
    }

    private static ParameterValue Parameter(BimDocument document, Element element, string name) =>
        element.GetInstanceParameters(document).Single(parameter => parameter.Name == name);

    [Fact]
    public void ARoomSetToAKeyTakesItsValuesAndFollowsIt()
    {
        var (document, office, toilet, styles) = Rooms();
        Parameter(document, office, "Floor Finish").TrySetFromText("Screed");
        var history = new UndoStack();

        // Every room has the key as a parameter, picked from the keys.
        var style = Parameter(document, office, "Room Style");
        Assert.Equal(KeySchedules.None, style.DisplayValue);
        Assert.Equal(new[] { KeySchedules.None, "Office", "Wet Room" }, style.AllowedValues);

        // Set to a key, it takes its values - and they are locked while it has it.
        Assert.True(style.TrySet("Office"));
        history.Record(new ParameterChangeCommand(style, KeySchedules.None, "Office"));
        Assert.Equal("Carpet tile", Parameter(document, office, "Floor Finish").DisplayValue);
        Assert.Equal("Emulsion", Parameter(document, office, "Wall Finish").DisplayValue);
        Assert.Contains("Floor Finish", KeySchedules.Locked(document, office));
        Assert.Empty(KeySchedules.Locked(document, toilet));

        // Undone, it is as it was - its own screed back.
        history.Undo();
        Assert.Equal("Screed", Parameter(document, office, "Floor Finish").DisplayValue);
        Assert.Equal(KeySchedules.None, Parameter(document, office, "Room Style").DisplayValue);
        history.Redo();
        Assert.Equal("Carpet tile", Parameter(document, office, "Floor Finish").DisplayValue);

        // The key's value changed in its schedule row: every room with it follows.
        Parameter(document, toilet, "Room Style").TrySet("Office");
        var key = styles.Keys[0];
        var floor = Parameter(document, key, "Floor Finish");
        Assert.True(floor.TrySet("Rubber"));
        Assert.Equal("Rubber", Parameter(document, office, "Floor Finish").DisplayValue);
        Assert.Equal("Rubber", Parameter(document, toilet, "Floor Finish").DisplayValue);

        // Renamed, the rooms keep it; another key's name is refused.
        Assert.False(Parameter(document, key, ScheduleDefinition.KeyNameField).TrySet("Wet Room"));
        Assert.True(Parameter(document, key, ScheduleDefinition.KeyNameField).TrySet("Open Office"));
        Assert.Equal("Open Office", Parameter(document, office, "Room Style").DisplayValue);

        // Deleted, the rooms keep its values with no key; undone, they have it again.
        var delete = KeySchedules.DeleteKey(document, key);
        history.Execute(delete);
        Assert.Equal(KeySchedules.None, Parameter(document, office, "Room Style").DisplayValue);
        Assert.Equal("Rubber", Parameter(document, office, "Floor Finish").DisplayValue);
        history.Undo();
        Assert.Equal("Open Office", Parameter(document, toilet, "Room Style").DisplayValue);
    }

    [Fact]
    public void TheScheduleListsTheKeysAndIsSaved()
    {
        var (document, office, toilet, styles) = Rooms();
        Parameter(document, office, "Room Style").TrySet("Office");
        Parameter(document, toilet, "Room Style").TrySet("Wet Room");

        // Its rows are its keys, each counting the rooms set to it.
        styles.Fields.Add(ScheduleDefinition.CountField);
        var result = Schedule.Run(document, styles);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Wet Room", result.Rows[1].Text(ScheduleDefinition.KeyNameField));
        Assert.Equal("Ceramic tile", result.Rows[1].Text("Wall Finish"));
        Assert.Equal("1", result.Rows[0].Text(ScheduleDefinition.CountField));
        Assert.False(result.Columns.Single(column => column.Field == "Floor Finish").IsReadOnly);

        // A room schedule can show the key; what a key can set is the rooms' own typed fields.
        Assert.Contains("Room Style", Schedule.AvailableFields(document, BuiltInCategory.Rooms));
        var keyable = KeySchedules.KeyableFields(document, BuiltInCategory.Rooms);
        Assert.Contains("Floor Finish", keyable);
        Assert.DoesNotContain("Mark", keyable);
        Assert.DoesNotContain("Room Style", keyable);

        // A copy is its own, with keys of its own.
        var copy = styles.Copy("Room Styles 2");
        Assert.Equal(2, copy.Keys.Count);
        Assert.Same(copy, copy.Keys[0].Schedule);

        var path = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid():N}.bimx");
        try
        {
            ProjectFile.Save(document, path);
            var opened = ProjectFile.Load(path);
            var saved = opened.Schedules.Single(schedule => schedule.Name == "Room Styles");
            Assert.Equal("Room Style", saved.KeyName);
            Assert.Equal("Vinyl sheet, coved", saved.Keys.Single(key => key.Name == "Wet Room").Values["Floor Finish"]);
            var wc = opened.All<Room>().Single(room => room.Name == "WC");
            Assert.Equal("Wet Room", Parameter(opened, wc, "Room Style").DisplayValue);
            Assert.Equal("Ceramic tile", Parameter(opened, wc, "Wall Finish").DisplayValue);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
