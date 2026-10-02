using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Schedules;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>
/// Paint and Split Face: a material on a wall's face, or on a part split off it, without
/// changing the wall's type - shown in 3D, counted in the takeoff, kept through saving, copying
/// and splitting the wall.
/// </summary>
[Collection("Wpf")]
public class WallPaintTests
{
    private static (BimDocument Document, Wall Wall, WallType Type, Guid Terracotta, Guid Tile) OneWall()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var wall = new Wall { Start = new Point2D(0, 0), End = new Point2D(6000, 0), TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000 };
        document.Add(wall);
        return (document, wall, type,
            document.Materials.First(m => m.Name == "Paint, Terracotta").Id,
            document.Materials.First(m => m.Name == "Ceramic Tile, White Gloss").Id);
    }

    [Fact]
    public void PaintingAFaceWithNothingSplitPaintsTheWholeOfIt()
    {
        var (_, wall, _, terracotta, _) = OneWall();

        WallPaint.Paint(wall, WallFace.Interior, 2000, 1500, terracotta)!.Redo();

        var region = Assert.Single(wall.FaceRegions);
        Assert.True(region.IsWhole);
        Assert.Equal(terracotta, WallPaint.PaintAt(wall, WallFace.Interior, 100, 2900));
        Assert.Null(WallPaint.PaintAt(wall, WallFace.Exterior, 100, 2900));
    }

    [Fact]
    public void ABandSplitOffEndToEndIsPaintedOnItsOwnAndTheRestApart()
    {
        var (document, wall, _, terracotta, tile) = OneWall();

        // Corners clicked past the ends: the band runs the face's whole length.
        WallPaint.Split(document, wall, WallFace.Interior, new Point2D(-200, 0), new Point2D(6200, 1200))!.Redo();
        var band = Assert.Single(wall.FaceRegions);
        Assert.Null(band.From);
        Assert.Null(band.To);
        Assert.Null(band.Bottom);
        Assert.Equal(1200, band.Top);

        WallPaint.Paint(wall, WallFace.Interior, 3000, 600, tile)!.Redo();
        WallPaint.Paint(wall, WallFace.Interior, 3000, 2000, terracotta)!.Redo();

        Assert.Equal(tile, WallPaint.PaintAt(wall, WallFace.Interior, 3000, 600));
        Assert.Equal(terracotta, WallPaint.PaintAt(wall, WallFace.Interior, 3000, 2000));

        // The whole face went in under the band, so the band still shows over it.
        Assert.True(wall.FaceRegions[0].IsWhole);
    }

    [Fact]
    public void WhatShowsLeavesOutTheDoorAndTheRegionsOverIt()
    {
        var (document, wall, type, terracotta, tile) = OneWall();
        var door = new Door { TypeId = document.TypesOf<DoorType>().First().Id, LevelId = wall.LevelId, HostWallId = wall.Id, DistanceAlongWall = 3000 };
        document.Add(door);
        var doorType = document.FindType<DoorType>(door.TypeId)!;

        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, terracotta)!.Redo();
        WallPaint.Split(document, wall, WallFace.Exterior, new Point2D(500, 500), new Point2D(1500, 1500))!.Redo();
        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, tile)!.Redo();

        var (from, to) = WallPaint.FaceSpan(document, wall, type, WallFace.Exterior);
        var faceArea = (to - from) * 3000;
        var doorArea = doorType.Width * doorType.Height;

        var areas = WallPaint.PaintedAreas(document, wall).ToDictionary(entry => entry.MaterialId, entry => entry.Area);
        Assert.Equal(1000 * 1000, areas[tile], 1);
        Assert.Equal(faceArea - doorArea - 1000 * 1000, areas[terracotta], 1);

        // Counted in the material takeoff as paint.
        var lines = MaterialTakeoff.Lines(document).Where(line => line.Category == "Paint").ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(1000 * 1000, lines.Single(line => line.Material == "Ceramic Tile, White Gloss").Area, 1);
    }

    [Fact]
    public void In3DThePaintIsJustProudOfTheFaceInItsMaterial()
    {
        var (document, wall, type, terracotta, _) = OneWall();
        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, terracotta)!.Redo();

        var paint = ModelMeshBuilder.Build(document).Single(mesh => mesh.Description == "Paint, Terracotta");
        Assert.Equal(wall.Id, paint.ElementId);
        var across = type.Width / 2 + WallPaint.ShownProud;
        Assert.All(paint.Positions, point => Assert.Equal(across, point.Y, 3));
        Assert.Equal(3000, paint.Positions.Max(point => point.Z), 3);
        Assert.NotEmpty(paint.Edges);
    }

    [Fact]
    public void RemovingPaintLeavesASplitPartSplitAndTakesAWholeFaceAway()
    {
        var (document, wall, _, terracotta, _) = OneWall();
        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, terracotta)!.Redo();
        WallPaint.Split(document, wall, WallFace.Exterior, new Point2D(500, 500), new Point2D(1500, 1500))!.Redo();
        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, terracotta)!.Redo();

        WallPaint.Paint(wall, WallFace.Exterior, 1000, 1000, null)!.Redo();
        Assert.Equal(2, wall.FaceRegions.Count);
        Assert.Null(wall.FaceRegions[1].MaterialId);

        WallPaint.Paint(wall, WallFace.Exterior, 4000, 2500, null)!.Redo();
        Assert.Single(wall.FaceRegions);
        Assert.Null(WallPaint.Paint(wall, WallFace.Exterior, 4000, 2500, null));
    }

    [Fact]
    public void ItIsSavedCopiedAndDividedWhenTheWallIsSplit()
    {
        var (document, wall, _, terracotta, tile) = OneWall();
        WallPaint.Split(document, wall, WallFace.Interior, new Point2D(-100, 0), new Point2D(6100, 1200))!.Redo();
        WallPaint.Paint(wall, WallFace.Interior, 3000, 600, tile)!.Redo();
        WallPaint.Split(document, wall, WallFace.Interior, new Point2D(4000, 1500), new Point2D(5000, 2500))!.Redo();
        WallPaint.Paint(wall, WallFace.Interior, 4500, 2000, terracotta)!.Redo();

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document)).Walls.Single();
        Assert.Equal(wall.FaceRegions, loaded.FaceRegions);

        var copy = (Wall)ElementCopy.Clone(wall)!;
        Assert.Equal(wall.FaceRegions, copy.FaceRegions);

        var split = new SplitWallCommand(document, wall, new Point2D(3000, 0));
        split.Redo();
        Assert.Equal(tile, WallPaint.PaintAt(wall, WallFace.Interior, 1000, 600));
        Assert.Equal(tile, WallPaint.PaintAt(split.Remainder, WallFace.Interior, 500, 600));
        Assert.Equal(terracotta, WallPaint.PaintAt(split.Remainder, WallFace.Interior, 1500, 2000));
        Assert.Null(WallPaint.PaintAt(wall, WallFace.Interior, 2000, 2000));

        split.Undo();
        Assert.Equal(2, wall.FaceRegions.Count);
    }

    [Fact]
    public void AClickInAnElevationPaintsWhereItLandsAndTwoClicksSplitAPartOff()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, wall, type, terracotta, _) = OneWall();
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
                var face = -type.Width / 2;

                plan.SetTool(PlanTool.SplitFace);
                Assert.True(plan.SplitFaceIn3D(wall.Id, new Point3D(1000, face, 500)));
                Assert.NotNull(plan.SplitFaceCorner);
                Assert.True(plan.SplitFaceIn3D(wall.Id, new Point3D(2000, face, 1500)));
                Assert.Null(plan.SplitFaceCorner);
                var part = Assert.Single(wall.FaceRegions);
                Assert.Equal((WallFace.Interior, 1000.0, 2000.0, 500.0, 1500.0), (part.Face, part.From!.Value, part.To!.Value, part.Bottom!.Value, part.Top!.Value));

                plan.SetTool(PlanTool.Paint);
                plan.PaintMaterialId = terracotta;
                Assert.True(plan.PaintIn3D(wall.Id, new Point3D(1500, face, 1000)));
                Assert.Equal(terracotta, WallPaint.PaintAt(wall, WallFace.Interior, 1500, 1000));
                Assert.Null(WallPaint.PaintAt(wall, WallFace.Interior, 3000, 1000));

                plan.History!.Undo();
                Assert.Null(wall.FaceRegions.Single().MaterialId);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
