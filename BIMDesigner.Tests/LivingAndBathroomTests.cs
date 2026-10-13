using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.PlumbingFireProtectionDomain;
using Xbim.Ifc4.SharedFacilitiesElements;
using Part = BIMDesigner.Core.Views.ComponentModel.Part;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.Tests;

/// <summary>
/// The bedroom, the living room and the bathroom in the component library: a wardrobe with its
/// doors, an armchair, a coffee table, a TV unit with its television, and showers - a tray, a
/// corner enclosure, an alcove's sliding door, a walk-in panel - with the glass on whichever
/// side the room is.
/// </summary>
public class LivingAndBathroomTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    private static List<Part> In(ComponentType type, PartFinish finish) =>
        ComponentModel.Parts(type).Where(part => part.Finish == finish).ToList();

    [Fact]
    public void TheyAreInTheLibraryAtTheSizesTheyComeIn()
    {
        var document = BimDocument.CreateDefault();

        var armchair = Type(document, "Armchair");
        Assert.Equal((BuiltInCategory.Furniture, ComponentForm.Armchair, 850.0, 950.0), (armchair.Kind, armchair.Form, armchair.Width, armchair.Height));
        Assert.Equal(420, Type(document, "Coffee Table - 1100 x 600").Height);
        Assert.Equal(ComponentForm.TelevisionUnit, Type(document, "TV Unit - 1600").Form);
        Assert.Equal(ComponentForm.Wardrobe, Type(document, "Wardrobe - 1000 x 600").Form);

        var walkIn = Type(document, "Walk-in Shower - 1400 x 900");
        Assert.Equal((BuiltInCategory.PlumbingFixtures, ShowerEnclosure.WalkIn, 1945.0), (walkIn.Kind, walkIn.Enclosure, walkIn.Height));
        Assert.Equal(ShowerEnclosure.None, Type(document, "Shower Tray - 900 x 900").Enclosure);

        // A wardrobe, a TV unit and a shower back onto a wall; an armchair and a coffee table stand where they are put.
        Assert.True(Type(document, "Wardrobe - 1500 x 600").AgainstWall);
        Assert.True(Type(document, "TV Unit - 1600").AgainstWall);
        Assert.True(walkIn.AgainstWall);
        Assert.False(armchair.AgainstWall);
        Assert.False(Type(document, "Coffee Table - 1100 x 600").AgainstWall);
    }

    [Fact]
    public void AWardrobeSavedAsAPlainBoxTakesItsShapeAndAShowersGlassIsSaved()
    {
        var older = BimDocument.CreateDefault();
        Type(older, "Wardrobe - 1000 x 600").Form = ComponentForm.Box;
        var shower = Type(older, "Shower Enclosure - Corner 900 x 900");
        shower.Enclosure = ShowerEnclosure.Alcove;

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(older));
        Assert.Equal(ComponentForm.Wardrobe, Type(loaded, "Wardrobe - 1000 x 600").Form);
        Assert.Equal(ShowerEnclosure.Alcove, Type(loaded, "Shower Enclosure - Corner 900 x 900").Enclosure);

        // One of the project's own that is a box stays one.
        Assert.Equal(ComponentForm.Box, Type(loaded, "Generic - 600 Cube").Form);
    }

    [Fact]
    public void AWardrobeHasAHingedDoorToEvery500WithItsHandles()
    {
        var document = BimDocument.CreateDefault();
        int Doors(ComponentType type) => In(type, PartFinish.Body).Count(part => part.Front == type.Depth / 2 - 18 && part.Top - part.Bottom > 1500);

        Assert.Equal(2, Doors(Type(document, "Wardrobe - 1000 x 600")));
        Assert.Equal(3, Doors(Type(document, "Wardrobe - 1500 x 600")));
        Assert.Equal(3 * 3, In(Type(document, "Wardrobe - 1500 x 600"), PartFinish.Metal).Count);
    }

    [Fact]
    public void AnArmchairACoffeeTableAndATvUnitAreShapedAsTheyAre()
    {
        var document = BimDocument.CreateDefault();

        // The armchair's seat cushion at 450 or so, between its arms, on four dark legs.
        var armchair = Type(document, "Armchair");
        Assert.Contains(In(armchair, PartFinish.Body), part => Math.Abs(part.Top - 446.5) < 1 && part.From > -armchair.Width / 2 && part.To < armchair.Width / 2);
        Assert.Equal(4, In(armchair, PartFinish.Dark).Count);

        // The coffee table's top at its height, its shelf a quarter of the way up.
        var table = Type(document, "Coffee Table - 1100 x 600");
        var parts = ComponentModel.Parts(table);
        Assert.Equal(420, parts.Max(part => part.Top));
        Assert.Contains(parts, part => part.Bottom == 105 && part.To - part.From > 1000);

        // The TV unit's television stands on it, 16:9, 1280 wide on a 1600 unit.
        var unit = Type(document, "TV Unit - 1600");
        var screen = In(unit, PartFinish.Dark).OrderByDescending(part => part.Top).First();
        Assert.Equal(1280, screen.To - screen.From, 6);
        Assert.Equal(1280 * 9.0 / 16, screen.Top - screen.Bottom, 6);
        Assert.Equal(unit.Height, In(unit, PartFinish.Dark).Where(part => part.Bottom >= unit.Height).Min(part => part.Bottom));
        Assert.Equal("TV", ComponentModel.PlanLabel(unit));
    }

    [Fact]
    public void AShowerHasItsGlassWhereItsEnclosureSays()
    {
        var document = BimDocument.CreateDefault();

        Assert.Empty(ComponentModel.ShowerGlass(Type(document, "Shower Tray - 900 x 900")));

        // A corner: the front and the right side.
        var corner = Type(document, "Shower Enclosure - Corner 900 x 900");
        var panes = ComponentModel.ShowerGlass(corner);
        Assert.Equal(2, panes.Count);
        Assert.True(panes[0].To - panes[0].From > 800, "Across the front.");
        Assert.True(panes[1].Front - panes[1].Back > 800 && panes[1].Across > 400, "Down the right side.");

        // An alcove: two panes across the front, one sliding behind the other.
        var alcove = ComponentModel.ShowerGlass(Type(document, "Shower Enclosure - Alcove 1200 x 800"));
        Assert.Equal(2, alcove.Count);
        Assert.True(alcove[0].To > alcove[1].From && alcove[1].Front < alcove[0].Back);

        // A walk-in: a 700 panel from the left, 600 or more open to step in at the right.
        var walkIn = Type(document, "Walk-in Shower - 1400 x 900");
        var panel = Assert.Single(ComponentModel.ShowerGlass(walkIn));
        Assert.Equal(700, panel.To - panel.From, 6);
        Assert.True(walkIn.Width / 2 - panel.To >= 500);

        // On a 45 tray, the glass up to the top, seen through.
        Assert.Equal(45, ComponentModel.ShowerTray(corner));
        Assert.All(In(corner, PartFinish.Glass), part => Assert.Equal((45.0, 1945.0), (part.Bottom, part.Top)));

        var shower = new Component { TypeId = corner.Id, TypeKind = corner.Kind, LevelId = document.Levels[0].Id };
        document.Add(shower);
        var glass = Assert.Single(ModelMeshBuilder.BuildComponent(document, shower), mesh => mesh.Description == "Glass");
        Assert.Equal(MeshKind.Glazing, glass.Kind);
        Assert.True(glass.Opacity < 1);
    }

    /// <summary>A bathroom 4 m wide, its back wall along y = 0 and a wall either end.</summary>
    private static BimDocument Bathroom()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        foreach (var (start, end) in new[] { (new Point2D(0, 0), new Point2D(4000, 0)), (new Point2D(0, 0), new Point2D(0, 3000)), (new Point2D(4000, 0), new Point2D(4000, 3000)) })
            document.Add(new Wall { Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 });
        return document;
    }

    [Fact]
    public void ACornerShowerHasItsGlassOnTheRoomSideOfWhicheverCornerItIsPutIn()
    {
        var document = Bathroom();
        var type = Type(document, "Shower Enclosure - Corner 900 x 900");
        Component Put(double x)
        {
            var shower = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, 480) };
            document.Add(shower);
            return shower;
        }

        // Facing the room, the corner at x = 0 is on its right: turned over, its glass to its left.
        var byLeftWall = Put(600);
        var fit = FitComponentsCommand.For(document, new[] { byLeftWall });
        Assert.NotNull(fit);
        Assert.Equal(1, byLeftWall.Facing.Y, 6);
        Assert.True(byLeftWall.FlipHand);

        // Its side pane is the one away from the wall at x = 0.
        var frame = ComponentModel.FrameOf(byLeftWall, 0);
        var side = ComponentModel.PlanOutlines(type, frame)[^1];
        Assert.True(side.Average(point => point.X) > byLeftWall.Location.X + 300);

        // Undone, it is as it was put down.
        fit!.Undo();
        Assert.False(byLeftWall.FlipHand);
        Assert.Equal(new Point2D(600, 480), byLeftWall.Location);

        // In the other corner it is the right way round.
        var byRightWall = Put(3400);
        Assert.True(ComponentPlacing.FitToWalls(document, byRightWall));
        Assert.False(byRightWall.FlipHand);
    }

    [Fact]
    public void MirroringAComponentTurnsItOverAndItIsSaved()
    {
        var document = Bathroom();
        var type = Type(document, "Walk-in Shower - 1400 x 900");
        var shower = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(2000, 1000) };
        document.Add(shower);

        var mirror = new Core.Documents.Commands.MirrorElementsCommand(new[] { shower }, new Line2D(new Point2D(3000, 0), new Vector2D(0, 1)));
        mirror.Redo();
        Assert.True(shower.FlipHand);
        Assert.Equal(4000, shower.Location.X, 6);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.True(loaded.Elements.OfType<Component>().Single().FlipHand);

        mirror.Undo();
        Assert.False(shower.FlipHand);

        // Flip Hand on its own, and again.
        var flip = new FlipHandCommand(new[] { shower });
        flip.Redo();
        Assert.True(shower.FlipHand);
        flip.Undo();
        Assert.False(shower.FlipHand);
    }

    [Fact]
    public void AShowersEnclosureIsATypeSettingOnlyAShowerHas()
    {
        var document = BimDocument.CreateDefault();
        var tray = Type(document, "Shower Tray - 900 x 900");
        var enclosure = tray.GetTypeParameters(document).Single(parameter => parameter.Name == "Enclosure");
        Assert.True(enclosure.TrySetFromText("Walk In"));
        Assert.Equal(ShowerEnclosure.WalkIn, tray.Enclosure);

        Assert.DoesNotContain(Type(document, "Armchair").GetTypeParameters(document), parameter => parameter.Name == "Enclosure");
    }

    [Fact]
    public void TheyAreExportedAsWhatTheyAreAndAShowersGlassIsCutAsGlass()
    {
        var document = Bathroom();
        var level = document.Levels[0].Id;
        var x = 600.0;
        foreach (var name in new[] { "Shower Enclosure - Corner 900 x 900", "Armchair", "Coffee Table - 1100 x 600", "Wardrobe - 1000 x 600" })
        {
            var type = Type(document, name);
            document.Add(new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = level, Location = new Point2D(x, 1500) });
            x += 1000;
        }

        using (var model = IfcExport.Build(document))
        {
            Assert.Single(model.Instances.OfType<IfcSanitaryTerminal>(), item => item.PredefinedType == IfcSanitaryTerminalTypeEnum.SHOWER);
            var furniture = model.Instances.OfType<IfcFurniture>().ToList();
            Assert.Contains(furniture, item => item.PredefinedType == IfcFurnitureTypeEnum.CHAIR && item.Name == "Armchair");
            Assert.Contains(furniture, item => item.PredefinedType == IfcFurnitureTypeEnum.TABLE && item.Name == "Coffee Table - 1100 x 600");
            Assert.Contains(furniture, item => item.PredefinedType == IfcFurnitureTypeEnum.USERDEFINED && item.ObjectType == "Wardrobe");
        }

        var marker = new SectionMarker { Start = new Point2D(600, 3000), End = new Point2D(600, 0), LevelId = level };
        var drawing = SectionProjection.Build(document, marker);
        Assert.Contains(drawing.Pieces, piece => piece.Part == SectionPart.Glazing && piece.Description == "Glass");
    }
}
