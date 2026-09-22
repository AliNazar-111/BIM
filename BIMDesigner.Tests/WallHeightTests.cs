using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>Walls that hang below their level, and walls attached to the slabs over and under them.</summary>
public class WallHeightTests
{
    private static (BimDocument Document, Wall Wall) Project()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 2400
        };
        document.Add(wall);
        return (document, wall);
    }

    private static T AddSlab<T>(BimDocument document, double heightOffset, int levelIndex = 0) where T : Slab, new()
    {
        var typeId = document.ElementTypes.OfType<SlabType>().First(type => type.Category == new T().Category).Id;
        var slab = new T { TypeId = typeId, LevelId = document.Levels[levelIndex].Id, HeightOffset = heightOffset };
        slab.SetBoundary(new[] { new Point2D(-500, -3000), new Point2D(6500, -3000), new Point2D(6500, 3000), new Point2D(-500, 3000) });
        document.Add(slab);
        return slab;
    }

    // ---- hanging down -------------------------------------------------------------

    [Fact]
    public void AWallCanHangDownFromItsLevel()
    {
        var (document, wall) = Project();

        // Up to its own level, from 1200 below it: a foundation wall.
        wall.TopLevelId = wall.LevelId;
        wall.BaseOffset = -1200;

        Assert.Equal(-1200, wall.GetBaseElevation(document), precision: 6);
        Assert.Equal(0, wall.GetTopElevation(document), precision: 6);
        Assert.Equal(1200, wall.GetHeight(document), precision: 6);

        var lowest = ModelMeshBuilder.Build(document)
            .Where(mesh => mesh.ElementId == wall.Id)
            .Min(mesh => mesh.Bounds()!.Value.Min.Z);
        Assert.Equal(-1200, lowest, precision: 6);
    }

    [Fact]
    public void ItsOwnLevelIsOfferedAsATop()
    {
        var (document, wall) = Project();
        var top = wall.GetInstanceParameters(document).Single(p => p.Name == "Top Constraint");

        Assert.Contains("Up to level: Ground Floor", top.AllowedValues!);

        // But only works with the base below it; level with it, the wall would have no height.
        Assert.False(top.TrySet("Up to level: Ground Floor"));
        wall.BaseOffset = -900;
        Assert.True(top.TrySet("Up to level: Ground Floor"));
        Assert.Equal(900, wall.GetHeight(document), precision: 6);
    }

    // ---- attaching ------------------------------------------------------------------

    [Fact]
    public void AnAttachedTopMeetsTheUndersideOfTheSlabAboveAndFollowsIt()
    {
        var (document, wall) = Project();
        var ceiling = AddSlab<Ceiling>(document, 2700);
        AddSlab<Roof>(document, 3200, levelIndex: 1);

        // The ceiling is lower than the roof, so it is the one over the wall.
        Assert.Same(ceiling, WallAttachments.SlabAbove(document, wall));

        var attach = new AttachWallsCommand(new[] { (wall, true, (Guid?)ceiling.Id) }, "Attach");
        attach.Redo();

        var underside = ceiling.GetBottomElevation(document);
        Assert.Equal(underside, wall.GetTopElevation(document), precision: 6);

        ceiling.HeightOffset = 2900;
        Assert.Equal(underside + 200, wall.GetTopElevation(document), precision: 6);

        attach.Undo();
        Assert.Equal(2400, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void AnAttachedBaseStandsOnTheFloorBelow()
    {
        var (document, wall) = Project();
        var floor = AddSlab<Floor>(document, 150);

        Assert.Same(floor, WallAttachments.FloorBelow(document, wall));

        new AttachWallsCommand(new[] { (wall, false, (Guid?)floor.Id) }, "Attach").Redo();

        Assert.Equal(150, wall.GetBaseElevation(document), precision: 6);
        Assert.Equal(2400, wall.GetHeight(document), precision: 6);
    }

    [Fact]
    public void AWallAlongTheEdgeOfASlabStillFindsIt()
    {
        var (document, wall) = Project();

        // The slab stops at the wall's centreline, as it does when a wall carries its edge.
        var roof = AddSlab<Roof>(document, 3000);
        roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(6000, 0), new Point2D(6000, 5000), new Point2D(0, 5000) });

        Assert.Same(roof, WallAttachments.SlabAbove(document, wall));
    }

    [Fact]
    public void AWallWithNothingOverItHasNothingToAttachTo()
    {
        var (document, wall) = Project();
        var roof = AddSlab<Roof>(document, 3000);
        roof.SetBoundary(new[] { new Point2D(20000, 0), new Point2D(25000, 0), new Point2D(25000, 5000), new Point2D(20000, 5000) });

        Assert.Null(WallAttachments.SlabAbove(document, wall));
    }

    [Fact]
    public void DeletingTheSlabLeavesTheWallItsOwnHeight()
    {
        var (document, wall) = Project();
        var ceiling = AddSlab<Ceiling>(document, 2700);
        wall.TopAttachedTo = ceiling.Id;

        document.Remove(ceiling);

        Assert.Equal(2400, wall.GetHeight(document), precision: 6);
        Assert.Equal("None (deleted)", wall.GetInstanceParameters(document).Single(p => p.Name == "Top Attached To").Value);
    }

    [Fact]
    public void AttachmentsAreSavedAndSurviveASplit()
    {
        var (document, wall) = Project();
        var ceiling = AddSlab<Ceiling>(document, 2700);
        var floor = AddSlab<Floor>(document, 100);
        wall.TopAttachedTo = ceiling.Id;
        wall.BaseAttachedTo = floor.Id;

        var split = new SplitWallCommand(document, wall, new Point2D(3000, 0));
        split.Redo();
        Assert.Equal(ceiling.Id, split.Remainder.TopAttachedTo);
        Assert.Equal(floor.Id, split.Remainder.BaseAttachedTo);

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);
            var copy = reloaded.Walls.Single(w => w.Id == wall.Id);

            Assert.Equal(ceiling.Id, copy.TopAttachedTo);
            Assert.Equal(floor.Id, copy.BaseAttachedTo);
            Assert.Equal(wall.GetHeight(document), copy.GetHeight(reloaded), precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
