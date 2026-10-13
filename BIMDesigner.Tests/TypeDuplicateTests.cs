using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.UI;

namespace BIMDesigner.Tests;

/// <summary>
/// Making your own types: Duplicate copies a type whole, under a new name, and gives it to what
/// is selected and to nothing else - so one table can be made bigger, or one sofa another
/// material, without changing every other of the same type.
/// </summary>
[Collection("Wpf")]
public class TypeDuplicateTests
{
    [Fact]
    public void EveryTypeThatCanBeDuplicatedKeepsEverySetting()
    {
        var document = BimDocument.CreateDefault();
        var dropped = new List<string>();
        foreach (var type in document.ElementTypes.Where(TypeDuplicates.CanDuplicate).GroupBy(type => type.GetType()).Select(group => group.First()))
        {
            (type.TypeMark, type.AssemblyCode, type.Keynote, type.Manufacturer, type.Url, type.Description, type.Cost) = ("M1", "Pr_40", "K9", "Maker", "https://example.com", "Said", 123m);
            var copy = TypeDuplicates.Duplicate(type, type.Name + " copy")!;
            Assert.NotEqual(type.Id, copy.Id);
            Assert.Equal(type.Name + " copy", copy.Name);

            foreach (var property in type.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.GetSetMethod() is not null && p.Name is not ("Id" or "Name")))
            {
                var (before, after) = (property.GetValue(type), property.GetValue(copy));
                if (before is System.Collections.IEnumerable and not string) continue;
                if (!Equals(before, after)) dropped.Add($"{type.GetType().Name}.{property.Name}");
            }
        }

        Assert.True(dropped.Count == 0, "Duplicates drop: " + string.Join(", ", dropped));
    }

    [Fact]
    public void ADuplicateIsGivenToTheSelectedTableOnlyAndChangedOnItsOwn()
    {
        var document = BimDocument.CreateDefault();
        var tableType = document.TypesOf<ComponentType>().First(t => t.Form == ComponentForm.Table);
        Component Table(double x)
        {
            var table = new Component { TypeId = tableType.Id, TypeKind = tableType.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, 0) };
            document.Add(table);
            return table;
        }

        var (picked, other) = (Table(0), Table(4000));
        var history = new UndoStack();
        var (command, copy) = TypeDuplicates.For(document, tableType, "Table - Dining 2400 x 1000", new[] { picked })!.Value;
        history.Execute(command);

        var bigger = (ComponentType)copy;
        bigger.Width = 2400;
        bigger.Depth = 1000;
        Assert.Equal(copy.Id, picked.TypeId);
        Assert.Equal(tableType.Id, other.TypeId);
        Assert.Equal(1800, tableType.Width, 6);

        // Its own size in 3D; the other stays as it was.
        double Wide(Component table) => ModelMeshBuilder.BuildComponent(document, table).SelectMany(mesh => mesh.Positions).Max(p => p.X) -
                                        ModelMeshBuilder.BuildComponent(document, table).SelectMany(mesh => mesh.Positions).Min(p => p.X);
        Assert.Equal(2400, Wide(picked), 0);
        Assert.Equal(1800, Wide(other), 0);

        // Taken back: the table is the old type again, and the new one is gone.
        history.Undo();
        Assert.Equal(tableType.Id, picked.TypeId);
        Assert.DoesNotContain(document.ElementTypes, type => type.Id == copy.Id);
    }

    [Fact]
    public void ATypeTakesAMaterialItIsBuiltInIn3D()
    {
        var document = BimDocument.CreateDefault();
        var sofa = document.TypesOf<ComponentType>().First(t => t.Form == ComponentForm.Sofa);
        var piece = new Component { TypeId = sofa.Id, TypeKind = sofa.Kind, LevelId = document.Levels[0].Id };
        document.Add(piece);

        var material = document.Materials.First(m => m.Name.StartsWith("Brick"));
        Assert.True(sofa.GetTypeParameters(document).Single(p => p.Name == "Material").TrySet(material.Name));
        Assert.Equal(material.Id, sofa.MaterialId);
        Assert.All(ModelMeshBuilder.BuildComponent(document, piece), mesh => Assert.Equal(material.SurfaceColour, mesh.Colour));
    }

    [Fact]
    public void TheWindowSaysWhatIsSharedAndGivesAPickedTypeToTheSelection()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var chairType = document.TypesOf<ComponentType>().First(t => t.Form == ComponentForm.Chair);
                var chairs = Enumerable.Range(0, 3).Select(i => new Component { TypeId = chairType.Id, TypeKind = chairType.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(i * 1000, 0) }).ToList();
                chairs.ForEach(document.Add);
                var other = document.TypesOf<ComponentType>().First(t => t.Kind == chairType.Kind && t != chairType);

                var window = new TypePropertiesWindow(document, new UndoStack(), chairType, new Core.Elements.Element[] { chairs[0] });
                T Field<T>(string name) => (T)typeof(TypePropertiesWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.Contains("3 pieces", Field<System.Windows.Controls.TextBlock>("Hint").Text);
                Assert.False(Field<System.Windows.Controls.Button>("DeleteButton").IsEnabled);

                Field<System.Windows.Controls.ComboBox>("TypePicker").SelectedItem = other;
                Assert.Equal(other.Id, chairs[0].TypeId);
                Assert.Equal(chairType.Id, chairs[1].TypeId);
                Assert.Same(other, window.Type);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The window failed.", failure);
    }
}
