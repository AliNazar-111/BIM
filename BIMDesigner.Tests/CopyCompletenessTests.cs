using System.Reflection;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;

namespace BIMDesigner.Tests;

/// <summary>
/// A copy is the same thing again: every setting an element has comes with it, and a dormer
/// copied is a dormer - carried by its copied walls, and opening the roof under it.
/// </summary>
public class CopyCompletenessTests
{
    /// <summary>A value for a property of this type different from a new element's, or null for one this test cannot make up.</summary>
    private static object? Different(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => !(bool)current!,
        _ when type == typeof(bool?) => current is true ? false : true,
        _ when type == typeof(double) || type == typeof(double?) => (current as double? ?? 0) + 123.0,
        _ when type == typeof(int) => (int)current! + 7,
        _ when type == typeof(string) => current + "x",
        _ when type == typeof(Guid) || type == typeof(Guid?) => Guid.NewGuid(),
        _ when type.IsEnum => Enum.GetValues(type).Cast<object>().FirstOrDefault(value => !value.Equals(current)),
        _ => null
    };

    [Fact]
    public void EverySettingOfEveryElementComesWithItsCopy()
    {
        var elementTypes = typeof(BimDocument).Assembly.GetTypes()
            .Where(type => typeof(Element).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
            .Where(type => type != typeof(Sheet));

        var dropped = new List<string>();
        foreach (var type in elementTypes)
        {
            var original = (Element)Activator.CreateInstance(type)!;

            // A mark is a number to be given out again, not a setting: copies are numbered afresh.
            var settings = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetSetMethod() is not null && p.GetIndexParameters().Length == 0)
                .Where(p => p.Name is not ("Id" or "Mark"));

            foreach (var setting in settings)
            {
                if (Different(setting.PropertyType, setting.GetValue(original)) is not { } value) continue;
                try { setting.SetValue(original, value); }
                catch (TargetInvocationException) { continue; }

                var copy = ElementCopy.Clone(original);
                Assert.NotNull(copy);
                if (!Equals(setting.GetValue(copy), setting.GetValue(original))) dropped.Add($"{type.Name}.{setting.Name}");
            }
        }

        Assert.True(dropped.Count == 0, "Copies drop: " + string.Join(", ", dropped));
    }

    private static (BimDocument Document, Roof Main, Roof Dormer) HouseWithDormer()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var corners = new[] { new Point2D(0, 0), new Point2D(12000, 0), new Point2D(12000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 3000 });

        var main = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
        main.SetBoundary(corners);
        main.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = i % 2 == 0, SlopeDegrees = 40 }));
        document.Add(main);

        Assert.NotNull(Dormers.Add(document, main, new Point2D(3000, 800), DormerSettings.Default, wallType.Id, out _, out _));
        return (document, main, document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, main)));
    }

    [Fact]
    public void ACopiedDormerIsCarriedByItsCopiedWallsAndOpensTheRoofUnderIt()
    {
        var (document, main, dormer) = HouseWithDormer();

        var copies = ElementCopy.Duplicate(document, Dormers.Parts(document, dormer));
        foreach (var copy in copies) ElementTransforms.Move(copy, new Vector2D(5000, 0));
        var add = Dormers.AddCopies(document, copies, "Paste");
        add.Redo();

        var copied = copies.OfType<Roof>().Single();
        Assert.Equal(4, Dormers.Parts(document, copied).Count);
        Assert.All(copies.OfType<Wall>(), wall => Assert.Equal(copied.Id, wall.TopAttachedTo));
        Assert.All(copies.OfType<Wall>(), wall => Assert.Equal(main.Id, wall.BaseAttachedTo));
        Assert.Equal(2, RoofJoin.Openings(document, main).Count);

        // One step: undone, the copy and its opening both go.
        add.Undo();
        Assert.Single(RoofJoin.Openings(document, main));
        Assert.DoesNotContain(copied, document.Elements);
    }

    [Fact]
    public void DeletingARoofTakesTheDormersOnItWithIt()
    {
        var (document, main, dormer) = HouseWithDormer();
        var parts = Dormers.Parts(document, dormer);
        var before = document.Elements.Count;

        var delete = new DeleteElementsCommand(document, new[] { main });
        delete.Redo();

        Assert.All(parts, part => Assert.DoesNotContain(part, document.Elements));
        Assert.Equal(before - 1 - parts.Count, document.Elements.Count);

        delete.Undo();
        Assert.Equal(before, document.Elements.Count);
        Assert.Single(RoofJoin.Openings(document, main));
    }
}
