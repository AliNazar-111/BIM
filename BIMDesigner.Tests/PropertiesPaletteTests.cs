using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.ViewModels;

namespace BIMDesigner.Tests;

/// <summary>The Properties palette with several elements selected, and each view's own scale.</summary>
public class PropertiesPaletteTests
{
    private static (BimDocument Document, Wall First, Wall Second) TwoWalls()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var first = new Wall { Start = new Point2D(0, 0), End = new Point2D(5000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        var second = new Wall { Start = new Point2D(0, 3000), End = new Point2D(5000, 3000), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2700 };
        document.Add(first);
        document.Add(second);
        return (document, first, second);
    }

    private static ParameterRow Row(BimDocument document, string name, params Wall[] walls) =>
        new(walls.Select(wall => wall.GetInstanceParameters(document).Single(p => p.Name == name)).ToList());

    [Fact]
    public void AValueTheSelectionDoesNotShareIsLeftBlank()
    {
        var (document, first, second) = TwoWalls();

        var height = Row(document, "Unconnected Height", first, second);
        Assert.True(height.Varies);
        Assert.Equal(string.Empty, height.Text);

        var baseOffset = Row(document, "Base Offset", first, second);
        Assert.False(baseOffset.Varies);
        Assert.NotEqual(string.Empty, baseOffset.Text);
    }

    [Fact]
    public void AnEditGoesToEveryElementAndUndoesAsOneStep()
    {
        var (document, first, second) = TwoWalls();
        var history = new UndoStack();

        var height = Row(document, "Unconnected Height", first, second);
        height.ValueCommitted += (_, e) =>
            history.Record(new CompositeCommand("Change Unconnected Height",
                e.Changes.Select(c => new ParameterChangeCommand(c.Parameter, c.OldValue, c.NewValue)).ToList()));

        height.Text = "3500";

        Assert.Equal(3500, first.UnconnectedHeight);
        Assert.Equal(3500, second.UnconnectedHeight);
        Assert.False(height.Varies);

        history.Undo();
        Assert.Equal(3000, first.UnconnectedHeight);
        Assert.Equal(2700, second.UnconnectedHeight);
    }

    [Fact]
    public void AViewKeepsItsOwnScaleThroughSavingAndUndo()
    {
        var (document, _, _) = TwoWalls();
        var plan = ViewReference.FloorPlan(document.Levels[0].Id);
        var other = ViewReference.FloorPlan(document.Levels[1].Id);

        Assert.Equal(100, document.ViewSettings.ScaleOf(plan));

        var command = new SetViewScaleCommand(document, plan, 50);
        command.Redo();
        Assert.Equal(50, document.ViewSettings.ScaleOf(plan));
        Assert.Equal(100, document.ViewSettings.ScaleOf(other));

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            Assert.Equal(50, ProjectFile.Load(path).ViewSettings.ScaleOf(plan));
        }
        finally
        {
            File.Delete(path);
        }

        command.Undo();
        Assert.Equal(100, document.ViewSettings.ScaleOf(plan));
    }
}
