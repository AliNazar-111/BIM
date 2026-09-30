using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.ProductExtension;

namespace BIMDesigner.Tests;

/// <summary>
/// Shaft openings: a hole cut straight down through the roofs, floors and ceilings between a
/// shaft's base and its top - a chimney through a roof, a stairwell through a floor.
/// </summary>
public class ShaftOpeningTests
{
    /// <summary>The 10 m by 8 m house under a 40° gable roof, its roof on the ground floor, standing on the walls at 3 m.</summary>
    private static (BimDocument Document, Roof Roof) House()
    {
        var (document, roof, _) = DormerToolTests.House(10000, 8000, 40);
        return (document, roof);
    }

    /// <summary>Two storeys of floor, 10 m by 8 m, at the ground and first floor, with nothing over them.</summary>
    private static (BimDocument Document, Floor Ground, Floor First) Floors()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<FloorType>().First();
        var outline = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };

        Floor On(int level)
        {
            var floor = new Floor { LevelId = document.Levels[level].Id, TypeId = type.Id };
            floor.SetBoundary(outline);
            document.Add(floor);
            return floor;
        }

        return (document, On(0), On(1));
    }

    private static ShaftOpening Put(BimDocument document, ShaftOpening shaft)
    {
        document.Add(shaft);
        return shaft;
    }

    [Fact]
    public void PutUnderARoofItGoesRightThroughItAndTheRoofHasTheHole()
    {
        var (document, roof) = House();
        var ground = document.Levels[0].Id;
        var at = new Point2D(5000, 2000);
        var whole = roof.SlopingArea(document);

        var shaft = Put(document, Shafts.Placed(document, ground, at, ShaftShape.Rectangle, 600, 600));

        // Up past the top of the roof over it, its height left free rather than held to a level.
        Assert.Null(shaft.TopLevelId);
        Assert.True(shaft.TopElevation(document) > shaft.Outline.Max(corner => roof.TopAt(document, corner)));
        Assert.True(Shafts.Cuts(document, shaft, roof));

        // The roof has a hole there, and less of it to cover: the hole's plan area, up the slope.
        Assert.Contains(RoofJoin.Openings(document, roof), hole => Polygon2D.Contains(hole, at));
        Assert.DoesNotContain(RoofSolid.Pieces(document, roof), piece => Polygon2D.Contains(piece.Outline, at));
        Assert.Equal(whole - 600 * 600 / Math.Cos(40 * Math.PI / 180), roof.SlopingArea(document), 0);
        Assert.Equal("1 roof", Shafts.CutSummary(document, shaft));
    }

    [Fact]
    public void AStairwellGoesUpThroughTheFloorAboveAndNotTheOneItStandsOn()
    {
        var (document, ground, first) = Floors();
        var at = new Point2D(3000, 3000);

        var shaft = Put(document, Shafts.Placed(document, ground.LevelId, at, ShaftShape.Rectangle, 1200, 2400));

        Assert.Equal(first.LevelId, shaft.TopLevelId);
        Assert.False(Shafts.Cuts(document, shaft, ground));
        Assert.True(Shafts.Cuts(document, shaft, first));

        Assert.Equal(ground.Area, Shafts.NetArea(document, ground), 3);
        Assert.Equal(first.Area - 1200 * 2400, Shafts.NetArea(document, first), 3);
        Assert.Equal((first.Area - 1200 * 2400) * document.FindType<SlabType>(first.TypeId)!.Thickness, first.GetVolume(document), 0);
    }

    [Fact]
    public void TheFloorItPassesThroughIsBuiltWithTheHoleIn3DAndSection()
    {
        var (document, _, first) = Floors();
        var at = new Point2D(3000, 3000);
        Put(document, Shafts.Placed(document, document.Levels[0].Id, at, ShaftShape.Rectangle, 1200, 2400));

        // Nothing of the floor over the shaft's middle, and no face standing along the cuts
        // made to build the floor with no holes.
        var meshes = ModelMeshBuilder.Build(document).Where(mesh => mesh.ElementId == first.Id).ToList();
        Assert.NotEmpty(meshes);
        Assert.DoesNotContain(meshes, mesh => Covers(mesh, at));
        Assert.Contains(meshes, mesh => Covers(mesh, new Point2D(8000, 6000)));
        Assert.DoesNotContain(meshes, mesh => HasWallThrough(mesh, 3000, 5000, 8000));

        // A section through it: floor either side of the shaft, none across it.
        var marker = new SectionMarker { Name = "A", Start = new Point2D(3000, -2000), End = new Point2D(3000, 10000), LevelId = first.LevelId, ViewDepth = 1000 };
        document.Add(marker);
        var cut = SectionProjection.Build(document, marker).Pieces.Where(piece => piece.ElementId == first.Id).ToList();
        var middle = at.Y + 2000;
        Assert.DoesNotContain(cut, piece => piece.Bounds.Left < middle && piece.Bounds.Right > middle);
        Assert.Contains(cut, piece => piece.Bounds.Right <= middle - 1200 + 1);
        Assert.Contains(cut, piece => piece.Bounds.Left >= middle + 1200 - 1);
    }

    /// <summary>Whether any triangle of a mesh lies over a point, seen from above.</summary>
    private static bool Covers(Mesh3D mesh, Point2D point)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var (a, b, c) = (mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]]);
            var triangle = new[] { new Point2D(a.X, a.Y), new Point2D(b.X, b.Y), new Point2D(c.X, c.Y) };
            if (Math.Abs(Polygon2D.SignedArea(triangle)) > 1 && Polygon2D.Contains(triangle, point)) return true;
        }

        return false;
    }

    /// <summary>Whether a mesh has an upright face along x = a value, between two y values: a side where there should be none.</summary>
    private static bool HasWallThrough(Mesh3D mesh, double x, double fromY, double toY)
    {
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            var corners = new[] { mesh.Positions[mesh.Indices[i]], mesh.Positions[mesh.Indices[i + 1]], mesh.Positions[mesh.Indices[i + 2]] };
            if (corners.All(p => Math.Abs(p.X - x) < 0.5) && corners.Min(p => p.Y) < toY && corners.Max(p => p.Y) > fromY &&
                corners.Max(p => p.Z) - corners.Min(p => p.Z) > 1)
                return true;
        }

        return false;
    }

    [Fact]
    public void ClickedOnAFloorIn3DItCutsJustThatFloor()
    {
        var (document, ground, first) = Floors();

        var shaft = Put(document, Shafts.PlacedIn(document, ground, new Point2D(2000, 2000), ShaftShape.Round, 300, 300));

        Assert.True(Shafts.Cuts(document, shaft, ground));
        Assert.False(Shafts.Cuts(document, shaft, first));
        Assert.Equal(ground.Area - Math.PI * 150 * 150, Shafts.NetArea(document, ground), tolerance: 1000);
    }

    [Fact]
    public void ARoundOneIsACircleOfItsDiameter()
    {
        var shaft = new ShaftOpening { Location = new Point2D(1000, 1000), Shape = ShaftShape.Round, Width = 400 };

        Assert.Equal(Shafts.RoundSides, shaft.Outline.Count);
        Assert.All(shaft.Outline, point => Assert.Equal(200, point.DistanceTo(shaft.Location), 6));
    }

    [Fact]
    public void LettingGoOfTheTopLevelKeepsItsHeightAndUndoPutsItBack()
    {
        var (document, ground, first) = Floors();
        var shaft = Put(document, Shafts.Placed(document, ground.LevelId, new Point2D(3000, 3000), ShaftShape.Rectangle, 1000, 1000));
        Assert.Equal(first.LevelId, shaft.TopLevelId);

        var top = shaft.GetInstanceParameters(document).Single(parameter => parameter.Name == ShaftParameters.TopConstraint.Name);
        Assert.True(top.TrySet(ShaftOpening.Unconnected));
        Assert.Null(shaft.TopLevelId);
        Assert.Equal(3000, shaft.TopElevation(document), 6);

        var change = top.TakeAppliedChange()!;
        change.Undo();
        Assert.Equal(first.LevelId, shaft.TopLevelId);

        // Its top may not come down to its bottom.
        var height = shaft.GetInstanceParameters(document).Single(parameter => parameter.Name == ShaftParameters.TopOffset.Name);
        Assert.False(height.TrySet(-3000.0));
    }

    [Fact]
    public void TheHoleGoesWhereTheShaftIsMovedMirroredAndCopied()
    {
        var (document, _, first) = Floors();
        var shaft = Put(document, Shafts.Placed(document, document.Levels[0].Id, new Point2D(3000, 3000), ShaftShape.Rectangle, 1000, 500));
        shaft.Angle = 30;

        var move = new MoveElementsCommand(new[] { shaft }, new Vector2D(4000, 0), document: document);
        move.Redo();
        Assert.Contains(Shafts.Holes(document, first), hole => Polygon2D.Contains(hole, new Point2D(7000, 3000)));
        Assert.DoesNotContain(Shafts.Holes(document, first), hole => Polygon2D.Contains(hole, new Point2D(3000, 3000)));
        move.Undo();

        ElementTransforms.Mirror(shaft, new Line2D(new Point2D(5000, 0), new Vector2D(0, 1)));
        Assert.Equal(7000, shaft.Location.X, 6);
        Assert.Equal(150, shaft.Angle, 6);

        var copy = ElementCopy.Duplicate(document, new[] { shaft }).OfType<ShaftOpening>().Single();
        Assert.NotEqual(shaft.Id, copy.Id);
        Assert.Equal(shaft.Outline, copy.Outline);
        Assert.Equal(shaft.TopLevelId, copy.TopLevelId);
    }

    [Fact]
    public void SavedAndOpenedItStillCutsTheFloor()
    {
        var (document, _, _) = Floors();
        var shaft = Put(document, Shafts.Placed(document, document.Levels[0].Id, new Point2D(3000, 3000), ShaftShape.Round, 900, 900));
        shaft.BaseOffset = -50;

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var again = reloaded.Elements.OfType<ShaftOpening>().Single();

        Assert.Equal(shaft.Id, again.Id);
        Assert.Equal(ShaftShape.Round, again.Shape);
        Assert.Equal(900, again.Width);
        Assert.Equal(-50, again.BaseOffset);
        Assert.Equal(shaft.TopLevelId, again.TopLevelId);
        var first = reloaded.Elements.OfType<Floor>().Single(floor => floor.LevelId == reloaded.Levels[1].Id);
        Assert.True(Shafts.Cuts(reloaded, again, first));
    }

    [Fact]
    public void DeletingTheLevelItReachesKeepsItAsTall()
    {
        var (document, ground, first) = Floors();
        var shaft = Put(document, Shafts.Placed(document, ground.LevelId, new Point2D(3000, 3000), ShaftShape.Rectangle, 1000, 1000));

        var delete = new DeleteLevelCommand(document, document.Levels[1]);
        delete.Redo();
        Assert.Null(shaft.TopLevelId);
        Assert.Equal(3000, shaft.TopElevation(document), 6);

        delete.Undo();
        Assert.Equal(first.LevelId, shaft.TopLevelId);
    }

    [Fact]
    public void ItIsDrawnOnThePlansItPassesUpThrough()
    {
        var (document, ground, first) = Floors();
        var shaft = Put(document, Shafts.Placed(document, ground.LevelId, new Point2D(3000, 3000), ShaftShape.Rectangle, 1000, 1000));

        Assert.True(Shafts.ShownOn(document, shaft, ground.LevelId));
        Assert.False(Shafts.ShownOn(document, shaft, first.LevelId));

        shaft.TopLevelId = null;
        shaft.UnconnectedHeight = 6000;
        Assert.True(Shafts.ShownOn(document, shaft, first.LevelId));
    }

    [Fact]
    public void InIfcTheFloorIsWholeWithAVoidForTheShaft()
    {
        var (document, _, _) = Floors();
        Put(document, Shafts.Placed(document, document.Levels[0].Id, new Point2D(3000, 3000), ShaftShape.Rectangle, 1000, 1000));

        using var model = IfcExport.Build(document);

        Assert.Single(model.Instances.OfType<IfcRelVoidsElement>(), relation => relation.RelatedOpeningElement.Name == "Shaft opening");
    }

    [Fact]
    public void TheToolCutsOneWhereClickedAndUndoTakesItAway()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, roof) = House();
                var plan = new BIMDesigner.UI.Controls.PlanView
                {
                    Document = document, ActiveLevelId = roof.LevelId, History = new UndoStack()
                };
                plan.SetTool(BIMDesigner.UI.Controls.PlanTool.Shaft);
                plan.ActiveShaftWidth = 500;
                plan.ActiveShaftDepth = 700;

                Assert.True(plan.PlaceShaftAt(new Point2D(5000, 2000)));
                var shaft = document.Elements.OfType<ShaftOpening>().Single();
                Assert.Equal(500, shaft.Width);
                Assert.Equal(700, shaft.Depth);
                Assert.True(Shafts.Cuts(document, shaft, roof));

                // On the roof in 3D, a second one where it was clicked.
                Assert.True(plan.PlaceShaftIn3D(roof.Id, new Point3D(2000, 6000, 5000)));
                Assert.Equal(2, document.Elements.OfType<ShaftOpening>().Count());

                plan.History!.Undo();
                plan.History.Undo();
                Assert.Empty(document.Elements.OfType<ShaftOpening>());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
