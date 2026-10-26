using System.Windows;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// The Annotate tab's tools as they are used: an aligned dimension's line put where the third
/// click is; a note typed where it goes, changed by typing into it again, and nothing left by
/// a box closed empty; and a fresh project's types keyed from the starter keynotes, so a keynote
/// placed on one reads something.
/// </summary>
[Collection("Wpf")]
public class AnnotateTabTests
{
    private static void OnUi(Action act)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                act();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    /// <summary>Two parallel walls 6 m apart, west and east, on a plan with an undo history.</summary>
    private static (BimDocument Document, PlanView Plan, UndoStack History) TwoWalls()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Interior"));
        foreach (var x in new[] { 0.0, 6000 })
            document.Add(new Wall { Start = new Point2D(x, 0), End = new Point2D(x, 5000), TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 });

        var history = new UndoStack();
        return (document, new PlanView { Document = document, ActiveLevelId = level, History = history }, history);
    }

    [Fact]
    public void AnAlignedDimensionsLineGoesWhereTheThirdClickIs()
    {
        OnUi(() =>
        {
            var (document, plan, _) = TwoWalls();
            plan.SetTool(PlanTool.Dimension);

            // Two picks measure; nothing is made until the third says where the line goes.
            plan.PlaceDimensionPointAt(new Point2D(0, 2500));
            plan.PlaceDimensionPointAt(new Point2D(6000, 2500));
            Assert.Empty(document.Elements.OfType<Dimension>());

            plan.PlaceDimensionPointAt(new Point2D(3000, 4000));
            var dimension = Assert.Single(document.Elements.OfType<Dimension>());
            Assert.Equal(6000, dimension.Measure(document), precision: 0);
            var (from, to) = dimension.GetDimensionLine(document);
            Assert.Equal(4000, from.Y, precision: 0);
            Assert.Equal(4000, to.Y, precision: 0);

            // Esc after the second pick leaves nothing behind, and the next click starts afresh.
            plan.PlaceDimensionPointAt(new Point2D(0, 1000));
            plan.PlaceDimensionPointAt(new Point2D(6000, 1000));
            Assert.True(plan.CancelPendingOperation());
            Assert.Single(document.Elements.OfType<Dimension>());
        });
    }

    [Fact]
    public void ANoteIsTypedWhereItGoesAndChangedByTypingIntoItAgain()
    {
        OnUi(() =>
        {
            var (document, plan, history) = TwoWalls();
            plan.SetTool(PlanTool.Text);

            // Typed and put down: one note saying it, one step to undo.
            plan.BeginNoteEdit(new Point2D(3000, 2000));
            Assert.True(plan.TypingNote);
            Assert.True(plan.CommitNoteText("Living room\nengineered oak floor  "));
            Assert.False(plan.TypingNote);
            var note = Assert.Single(document.Elements.OfType<TextNote>());
            Assert.Equal("Living room\nengineered oak floor", note.Text);
            Assert.Equal(new Point2D(3000, 2000), note.Position);

            // Typed into again: changed, and undone back to what it said.
            plan.BeginNoteEdit(note.Position, note);
            Assert.True(plan.CommitNoteText("Kitchen"));
            Assert.Equal("Kitchen", note.Text);
            history.Undo();
            Assert.Equal("Living room\nengineered oak floor", note.Text);

            // A box closed with nothing in it makes no note, and empties no note.
            plan.BeginNoteEdit(new Point2D(4000, 4000));
            Assert.False(plan.CommitNoteText("   "));
            plan.BeginNoteEdit(note.Position, note);
            Assert.False(plan.CommitNoteText(""));
            Assert.Single(document.Elements.OfType<TextNote>());
            Assert.Equal("Living room\nengineered oak floor", note.Text);
        });
    }

    [Fact]
    public void AFreshProjectsTypesCarryTheStarterKeynotes()
    {
        var document = BimDocument.CreateDefault();
        string KeyOf<T>(string name) where T : Core.Elements.ElementType => document.TypesOf<T>().First(type => type.Name == name).Keynote;

        Assert.Equal("2.5.01", KeyOf<WallType>("Exterior - Brick on Block 330mm"));
        Assert.Equal("2.7.01", KeyOf<WallType>("Interior - Partition 125mm"));
        Assert.Equal("2.6.01", KeyOf<WindowType>("Casement - 1200 x 1200"));
        Assert.Equal("2.8.01", KeyOf<DoorType>("Single - 900 x 2100"));
        Assert.Equal("2.8.02", KeyOf<DoorType>("Fire Door FD30S - 900 x 2100"));
        Assert.Equal("2.6.02", KeyOf<DoorType>("Front Door - 1000 x 2100"));
        Assert.Equal("2.3.02", KeyOf<RoofType>("Roof - Slate on Timber Rafters 150mm"));
        Assert.Equal("2.1.02", KeyOf<BeamType>("UB 203x133x25"));

        // Where the starter's words do not fit, nothing: a louvred door is not flush and painted,
        // nor plain clay tiles concrete ones.
        Assert.Equal("", KeyOf<DoorType>("Bi-fold Louvred - 1600 x 2100"));
        Assert.Equal("", KeyOf<RoofType>("Roof - Plain Clay Tiles on Insulated Rafters 240mm"));

        // Every key given is one the table has, and a keynote of the brick wall reads it.
        Assert.All(document.ElementTypes.Where(type => type.Keynote.Length > 0), type => Assert.NotNull(document.Keynotes.Find(type.Keynote)));
        Assert.Equal("2.5.01", document.Materials.First(material => material.Name.StartsWith("Brick")).Keynote);
    }
}
