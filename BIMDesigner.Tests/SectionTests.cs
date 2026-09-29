using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over the section cut (specification section 6.1).
///
/// The section is the first view to draw the model's vertical data at all, so these check
/// the numbers as much as the geometry: a band in the wrong place here means a level
/// elevation, a base offset or a sill height is being read wrongly everywhere.
/// </summary>
public class SectionTests
{
    private const double Tolerance = 1e-6;

    private static BimDocument Project() => BimDocument.CreateDefault();

    /// <summary>A wall running east-west along y = 0, from x = 0 to x = 6000.</summary>
    private static Wall AddWall(BimDocument document, WallType type, double length = 6000)
    {
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(length, 0),
            TypeId = type.Id,
            LevelId = document.Levels.First().Id,
            UnconnectedHeight = 3000
        };

        document.Add(wall);
        return wall;
    }

    /// <summary>A cut running north-south across that wall at x = 3000.</summary>
    private static SectionMarker AddSection(BimDocument document, double x = 3000, double depth = 20000)
    {
        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(x, -4000),
            End = new Point2D(x, 4000),
            LevelId = document.Levels.First().Id,
            ViewDepth = depth
        };

        document.Add(marker);
        return marker;
    }

    private static IEnumerable<SectionPiece> Cut(SectionDrawing drawing, Guid elementId) =>
        drawing.Pieces.Where(piece => piece.ElementId == elementId && piece.Depth == SectionDepth.Cut);

    // ---- the marker itself ------------------------------------------------------

    [Fact]
    public void TheMarkerMeasuresPositionAlongAndDepthInFront()
    {
        var document = Project();
        var marker = AddSection(document);

        // The cut runs north; 4000 up the line is the origin's latitude.
        Assert.Equal(4000, marker.DistanceAlong(new Point2D(3000, 0)), Tolerance);
        Assert.Equal(8000, marker.Length, Tolerance);

        // Looking left of "north" is west, so a point to the west is in front.
        var inFront = marker.LookDirection;
        Assert.Equal(1000, marker.DepthOf(new Point2D(3000, 0) + inFront * 1000), Tolerance);
        Assert.Equal(-1000, marker.DepthOf(new Point2D(3000, 0) - inFront * 1000), Tolerance);
    }

    [Fact]
    public void FlippingTheMarkerLooksTheOtherWay()
    {
        var document = Project();
        var marker = AddSection(document);

        var before = marker.LookDirection;
        marker.Flipped = true;

        Assert.Equal(-before.X, marker.LookDirection.X, Tolerance);
        Assert.Equal(-before.Y, marker.LookDirection.Y, Tolerance);
    }

    // ---- walls ------------------------------------------------------------------

    [Fact]
    public void ACutAcrossAWallShowsItAsTheWallIsThick()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var drawing = SectionProjection.Build(document, marker);
        var bands = Cut(drawing, wall.Id).ToList();

        var single = Assert.Single(bands);
        Assert.Equal(SectionPart.WallLayer, single.Part);

        // 200 mm of wall, cut square on.
        Assert.Equal(200, single.Bounds.Width, precision: 6);

        // The wall straddles the cut's own origin, 4000 along the line.
        Assert.Equal(3900, single.Bounds.Left, precision: 6);
        Assert.Equal(4100, single.Bounds.Right, precision: 6);
    }

    [Fact]
    public void AWallStandsBetweenItsLevelAndItsHeight()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var ground = document.Levels.First();
        ground.Elevation = 0;
        wall.BaseOffset = 150;
        wall.UnconnectedHeight = 2700;

        var band = Cut(SectionProjection.Build(document, marker), wall.Id).First();

        Assert.Equal(150, band.Bounds.Bottom, precision: 6);
        Assert.Equal(2850, band.Bounds.Top, precision: 6);
    }

    [Fact]
    public void AWallConstrainedToALevelFollowsIt()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var first = document.Levels[1];
        wall.TopLevelId = first.Id;
        wall.TopOffset = 0;

        var band = Cut(SectionProjection.Build(document, marker), wall.Id).First();
        Assert.Equal(first.Elevation, band.Bounds.Top, precision: 6);

        // Raising the level raises the wall, with nothing in between to update.
        first.Elevation = 3600;
        band = Cut(SectionProjection.Build(document, marker), wall.Id).First();
        Assert.Equal(3600, band.Bounds.Top, precision: 6);
    }

    [Fact]
    public void EveryLayerOfAWallIsCutSeparatelyAndTheyMeetExactly()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var bands = Cut(SectionProjection.Build(document, marker), wall.Id)
            .OrderBy(piece => piece.Bounds.Left)
            .ToList();

        Assert.Equal(type.Structure.Layers.Count, bands.Count);

        // The bands must tile the wall: no gaps, no overlaps, no missing thickness.
        Assert.Equal(type.Structure.TotalWidth, bands.Sum(b => b.Bounds.Width), precision: 6);

        for (var i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].Bounds.Right, bands[i].Bounds.Left, precision: 6);

        // Which end of the cut the exterior face lands on depends on which way the section
        // looks, so the layers are read outward from that face rather than left to right.
        var centre = new Point2D(3000, 0);
        var exteriorFace = marker.DistanceAlong(centre + wall.ExteriorNormal * (type.Structure.TotalWidth / 2));
        var reading = exteriorFace > marker.DistanceAlong(centre)
            ? Enumerable.Reverse(bands).ToList()
            : bands;

        // The first layer of the assembly really is the one against the exterior face.
        var outermost = reading.First().Bounds;
        Assert.Equal(exteriorFace, exteriorFace > marker.DistanceAlong(centre) ? outermost.Right : outermost.Left,
            precision: 6);

        Assert.Equal(
            type.Structure.Layers.Select(layer => Math.Round(layer.Thickness, 6)),
            reading.Select(band => Math.Round(band.Bounds.Width, 6)));
    }

    [Fact]
    public void AnObliqueCutShowsMoreWallThanTheWallIsThick()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);

        // A cut at 45 degrees through a 200 mm wall reads 200 * sqrt(2) across.
        var marker = new SectionMarker
        {
            Start = new Point2D(0, -3000),
            End = new Point2D(6000, 3000),
            LevelId = document.Levels.First().Id
        };
        document.Add(marker);

        var band = Cut(SectionProjection.Build(document, marker), wall.Id).First();
        Assert.Equal(200 * Math.Sqrt(2), band.Bounds.Width, precision: 3);
    }

    [Fact]
    public void AWallRunningAlongTheCutIsNotSliced()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();
        var wall = AddWall(document, type);

        // The cut runs the same way as the wall, a metre in front of it.
        var marker = new SectionMarker
        {
            Start = new Point2D(0, -1000),
            End = new Point2D(6000, -1000),
            LevelId = document.Levels.First().Id,
            ViewDepth = 20000
        };
        document.Add(marker);

        var pieces = SectionProjection.Build(document, marker).Pieces
            .Where(piece => piece.ElementId == wall.Id)
            .ToList();

        // Slicing lengthways would report metres of masonry. It is drawn in elevation.
        Assert.All(pieces, piece => Assert.Equal(SectionDepth.Seen, piece.Depth));
    }

    [Fact]
    public void AWallBehindTheViewerIsNotDrawn()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();
        var wall = AddWall(document, type);

        // Looking away from the wall.
        var marker = new SectionMarker
        {
            Start = new Point2D(0, -1000),
            End = new Point2D(6000, -1000),
            LevelId = document.Levels.First().Id,
            Flipped = true
        };
        document.Add(marker);

        Assert.DoesNotContain(
            SectionProjection.Build(document, marker).Pieces, p => p.ElementId == wall.Id);
    }

    [Fact]
    public void AWallBeyondTheViewDepthIsNotDrawn()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();
        var wall = new Wall
        {
            Start = new Point2D(0, 30000),
            End = new Point2D(6000, 30000),
            TypeId = type.Id,
            LevelId = document.Levels.First().Id
        };
        document.Add(wall);

        var marker = new SectionMarker
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            LevelId = document.Levels.First().Id,
            ViewDepth = 20000
        };
        document.Add(marker);

        // The wall is 30 m away; the view sees 20 m. A view depth nobody enforces is not one.
        Assert.DoesNotContain(
            SectionProjection.Build(document, marker).Pieces, p => p.ElementId == wall.Id);

        marker.ViewDepth = 40000;
        Assert.Contains(SectionProjection.Build(document, marker).Pieces, p => p.ElementId == wall.Id);
    }

    // ---- openings ---------------------------------------------------------------

    [Fact]
    public void ACutThroughADoorLeavesOnlyTheWallAboveIt()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var doorType = document.TypesOf<DoorType>().First(t => t.Width == 900);
        document.Add(new Door
        {
            TypeId = doorType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000,
            SillHeight = 0
        });

        var drawing = SectionProjection.Build(document, marker);
        var band = Assert.Single(Cut(drawing, wall.Id));

        // Nothing below the head; the wall above it is what a lintel carries.
        Assert.Equal(doorType.Height, band.Bounds.Bottom, precision: 6);
        Assert.Equal(3000, band.Bounds.Top, precision: 6);
    }

    [Fact]
    public void ACutThroughAWindowLeavesWallUnderTheSillAndOverTheHead()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var windowType = document.TypesOf<WindowType>().First(t => t.Height == 1200);
        document.Add(new Window
        {
            TypeId = windowType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000,
            SillHeight = 900
        });

        var bands = Cut(SectionProjection.Build(document, marker), wall.Id)
            .OrderBy(piece => piece.Bounds.Bottom)
            .ToList();

        Assert.Equal(2, bands.Count);

        Assert.Equal(0, bands[0].Bounds.Bottom, precision: 6);
        Assert.Equal(900, bands[0].Bounds.Top, precision: 6);

        Assert.Equal(2100, bands[1].Bounds.Bottom, precision: 6);
        Assert.Equal(3000, bands[1].Bounds.Top, precision: 6);
    }

    [Fact]
    public void TheGlazingIsDrawnInTheGapItLeaves()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);
        var marker = AddSection(document);

        var windowType = document.TypesOf<WindowType>().First(t => t.Height == 1200);
        var window = new Window
        {
            TypeId = windowType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 3000,
            SillHeight = 900
        };
        document.Add(window);

        var glazing = Assert.Single(
            SectionProjection.Build(document, marker).Pieces, p => p.ElementId == window.Id);

        Assert.Equal(SectionPart.Glazing, glazing.Part);
        Assert.Equal(900, glazing.Bounds.Bottom, precision: 6);
        Assert.Equal(2100, glazing.Bounds.Top, precision: 6);
    }

    [Fact]
    public void ACutBesideADoorStillShowsSolidWall()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var wall = AddWall(document, type);

        var doorType = document.TypesOf<DoorType>().First(t => t.Width == 900);
        document.Add(new Door
        {
            TypeId = doorType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 1000
        });

        // Cutting at x = 3000 misses a door centred on x = 1000.
        var marker = AddSection(document);

        var band = Assert.Single(Cut(SectionProjection.Build(document, marker), wall.Id));
        Assert.Equal(0, band.Bounds.Bottom, precision: 6);
        Assert.Equal(3000, band.Bounds.Top, precision: 6);
    }

    [Fact]
    public void AWindowInAWallSeenBeyondTheCutIsDrawnOnIt()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();

        // A wall parallel to the cut, four metres in front of it.
        var wall = new Wall
        {
            Start = new Point2D(0, 4000),
            End = new Point2D(6000, 4000),
            TypeId = type.Id,
            LevelId = document.Levels.First().Id
        };
        document.Add(wall);

        var windowType = document.TypesOf<WindowType>().First(t => t.Width == 1200);
        var window = new Window
        {
            TypeId = windowType.Id,
            LevelId = wall.LevelId,
            HostWallId = wall.Id,
            DistanceAlongWall = 2000,
            SillHeight = 900
        };
        document.Add(window);

        var marker = new SectionMarker
        {
            Start = new Point2D(0, 0),
            End = new Point2D(6000, 0),
            LevelId = document.Levels.First().Id,
            ViewDepth = 20000
        };
        document.Add(marker);

        var drawing = SectionProjection.Build(document, marker);

        var face = Assert.Single(drawing.Pieces, p => p.ElementId == wall.Id);
        Assert.Equal(SectionDepth.Seen, face.Depth);
        Assert.Equal(SectionPart.WallFace, face.Part);

        var glazing = Assert.Single(drawing.Pieces, p => p.ElementId == window.Id);
        Assert.Equal(SectionDepth.Seen, glazing.Depth);

        // 1200 wide, centred 2000 along a wall that runs the same way as the cut.
        Assert.Equal(1400, glazing.Bounds.Left, precision: 6);
        Assert.Equal(2600, glazing.Bounds.Right, precision: 6);
        Assert.Equal(900, glazing.Bounds.Bottom, precision: 6);
    }

    // ---- slabs ------------------------------------------------------------------

    [Fact]
    public void AFloorIsCutAsLayersHangingBelowItsLevel()
    {
        var document = Project();
        var floorType = document.TypesOf<FloorType>().Single(t => t.Name.Contains("Screed"));

        var floor = new Floor { TypeId = floorType.Id, LevelId = document.Levels[1].Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, -2000), new Point2D(6000, -2000),
            new Point2D(6000, 2000), new Point2D(0, 2000)
        });
        document.Add(floor);

        var marker = AddSection(document);
        var bands = Cut(SectionProjection.Build(document, marker), floor.Id)
            .OrderByDescending(piece => piece.Bounds.Top)
            .ToList();

        Assert.Equal(floorType.Structure.Layers.Count, bands.Count);

        // The level is the finished surface; the build-up hangs below it.
        var level = document.Levels[1].Elevation;
        Assert.Equal(level, bands.First().Bounds.Top, precision: 6);
        Assert.Equal(level - floorType.Thickness, bands.Last().Bounds.Bottom, precision: 6);

        // And they tile, exactly as a wall's do.
        for (var i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].Bounds.Bottom, bands[i].Bounds.Top, precision: 6);

        // The cut crosses the full 4 m depth of the slab.
        Assert.All(bands, band => Assert.Equal(4000, band.Bounds.Width, precision: 6));
    }

    [Fact]
    public void ACeilingSitsAtItsHeightOffset()
    {
        var document = Project();
        var ceilingType = document.TypesOf<CeilingType>().First();

        var ceiling = new Ceiling
        {
            TypeId = ceilingType.Id,
            LevelId = document.Levels.First().Id,
            HeightOffset = 2400
        };
        ceiling.SetBoundary(new[]
        {
            new Point2D(0, -2000), new Point2D(6000, -2000),
            new Point2D(6000, 2000), new Point2D(0, 2000)
        });
        document.Add(ceiling);

        var marker = AddSection(document);
        var band = Cut(SectionProjection.Build(document, marker), ceiling.Id).First();

        Assert.Equal(2400, band.Bounds.Top, precision: 6);
        Assert.Equal(2400 - ceilingType.Thickness, band.Bounds.Bottom, precision: 6);
    }

    [Fact]
    public void AnLShapedSlabCutAcrossTheBiteGivesTwoStretches()
    {
        var document = Project();
        var floorType = document.TypesOf<FloorType>().First();

        // A U on plan: solid at both ends of the cut, open in the middle.
        var floor = new Floor { TypeId = floorType.Id, LevelId = document.Levels.First().Id };
        floor.SetBoundary(new[]
        {
            new Point2D(0, -4000), new Point2D(6000, -4000), new Point2D(6000, 4000),
            new Point2D(4000, 4000), new Point2D(4000, -2000),
            new Point2D(2000, -2000), new Point2D(2000, 4000), new Point2D(0, 4000)
        });
        document.Add(floor);

        // Cut north-south at x = 3000, straight down the open middle of the U.
        var marker = AddSection(document);

        var bands = Cut(SectionProjection.Build(document, marker), floor.Id)
            .Where(piece => piece.Bounds.Top >= 0)
            .GroupBy(piece => Math.Round(piece.Bounds.Left, 3))
            .ToList();

        // One stretch of slab, from the south edge up to where the bite begins.
        var stretch = Assert.Single(bands);
        Assert.Equal(0, stretch.Key, precision: 3);
        Assert.All(stretch, piece => Assert.Equal(2000, piece.Bounds.Right, precision: 3));
    }

    // ---- the drawing as a whole -------------------------------------------------

    [Fact]
    public void LevelsAreCarriedIntoTheSectionInOrder()
    {
        var document = Project();
        var marker = AddSection(document);

        var drawing = SectionProjection.Build(document, marker);

        Assert.Equal(document.Levels.Count, drawing.Levels.Count);
        Assert.Equal(
            document.Levels.Select(level => level.Elevation).OrderBy(e => e),
            drawing.Levels.Select(line => line.Elevation));
    }

    [Fact]
    public void WhatIsSeenIsDrawnBeforeWhatIsCut()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().First();

        AddWall(document, type);                                        // crossed by the cut
        document.Add(new Wall                                           // seen beyond it
        {
            Start = new Point2D(0, 3000),
            End = new Point2D(6000, 3000),
            TypeId = type.Id,
            LevelId = document.Levels.First().Id
        });

        var marker = AddSection(document);
        var depths = SectionProjection.Build(document, marker).Pieces.Select(p => (int)p.Depth).ToList();

        Assert.Equal(depths.OrderBy(d => d), depths);
    }

    [Fact]
    public void ASectionOfAnEmptyProjectIsEmptyRatherThanWrong()
    {
        var document = Project();
        var marker = AddSection(document);

        var drawing = SectionProjection.Build(document, marker);

        Assert.True(drawing.IsEmpty);
        Assert.Equal(8000, drawing.Width, Tolerance);
    }

    [Fact]
    public void AMarkerWithNoLengthCutsNothing()
    {
        var document = Project();
        AddWall(document, document.TypesOf<WallType>().First());

        var marker = new SectionMarker
        {
            Start = new Point2D(3000, 0),
            End = new Point2D(3000, 0),
            LevelId = document.Levels.First().Id
        };
        document.Add(marker);

        // Degenerate input must give an empty drawing, not a divide by zero.
        Assert.True(SectionProjection.Build(document, marker).IsEmpty);
    }

    // ---- persistence ------------------------------------------------------------

    [Fact]
    public void SectionMarkersSurviveSavingAndReopening()
    {
        var document = Project();
        var marker = AddSection(document);
        marker.Name = "B";
        marker.Flipped = true;
        marker.ViewDepth = 12000;

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var match = Assert.Single(reloaded.Elements.OfType<SectionMarker>());

        Assert.Equal(marker.Id, match.Id);
        Assert.Equal("B", match.Name);
        Assert.True(match.Flipped);
        Assert.Equal(12000, match.ViewDepth, precision: 6);
        Assert.Equal(marker.Start.X, match.Start.X, precision: 6);
        Assert.Equal(marker.End.Y, match.End.Y, precision: 6);
    }

    [Fact]
    public void AReopenedSectionCutsTheSameDrawing()
    {
        var document = Project();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Exterior"));
        AddWall(document, type);
        var marker = AddSection(document);

        var before = SectionProjection.Build(document, marker);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var after = SectionProjection.Build(reloaded, reloaded.Elements.OfType<SectionMarker>().Single());

        Assert.Equal(before.Pieces.Count, after.Pieces.Count);

        foreach (var (a, b) in before.Pieces.Zip(after.Pieces))
        {
            Assert.Equal(a.Bounds.Left, b.Bounds.Left, precision: 6);
            Assert.Equal(a.Bounds.Right, b.Bounds.Right, precision: 6);
            Assert.Equal(a.Bounds.Bottom, b.Bounds.Bottom, precision: 6);
            Assert.Equal(a.Bounds.Top, b.Bounds.Top, precision: 6);
        }
    }

    [Fact]
    public void WhatIsBeyondTheCutIsDrawnFarthestFirstSoNearerWallsHideIt()
    {
        // Two walls parallel to the cut, one behind the other, each with a window at the same
        // place: the nearer wall is drawn after the farther one and its window, so it hides them.
        var document = Project();
        var type = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
        var window = document.TypesOf<WindowType>().First();
        var level = document.Levels[0].Id;
        var far = new Wall { Start = new Point2D(0, 6000), End = new Point2D(8000, 6000), TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 };
        var near = new Wall { Start = new Point2D(0, 2000), End = new Point2D(8000, 2000), TypeId = type.Id, LevelId = level, UnconnectedHeight = 3000 };
        document.Add(far);
        document.Add(near);
        var farWindow = new Window { HostWallId = far.Id, LevelId = level, TypeId = window.Id, DistanceAlongWall = 4000, SillHeight = 900 };
        document.Add(farWindow);

        var marker = new SectionMarker { Start = new Point2D(-1000, 0), End = new Point2D(9000, 0), LevelId = level, Name = "A" };
        if (marker.DepthOf(new Point2D(0, 5000)) < 0) marker.Flipped = true;
        document.Add(marker);

        var pieces = SectionProjection.Build(document, marker).Pieces.ToList();
        var lastFar = pieces.FindLastIndex(piece => piece.ElementId == far.Id || piece.ElementId == farWindow.Id);
        var firstNear = pieces.FindIndex(piece => piece.ElementId == near.Id);

        Assert.True(firstNear > lastFar, "The nearer wall is drawn before what is behind it, so what is behind shows over it.");
    }

    [Fact]
    public void ARoofBeyondTheCutIsSeenAndOneSeenEdgeOnIsNot()
    {
        // A hip roof over a 10 m x 7 m box, cut along its length: the far slope and the hipped
        // ends rise over the rooms beyond the cut. Cut across a gable's ridge, its slopes are
        // seen edge on and show nothing beyond the cut itself.
        foreach (var hip in new[] { true, false })
        {
            var document = Project();
            var level = document.Levels[0].Id;
            var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 3000 };
            roof.SetBoundary(new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 7000), new Point2D(0, 7000) });
            roof.SetEdges(Enumerable.Range(0, 4).Select(i => new RoofEdge { DefinesSlope = hip || i % 2 == 0, SlopeDegrees = 35 }));
            document.Add(roof);

            var marker = hip
                ? new SectionMarker { Start = new Point2D(-1000, 3000), End = new Point2D(11000, 3000), LevelId = level, Name = "A" }
                : new SectionMarker { Start = new Point2D(5000, -1000), End = new Point2D(5000, 8000), LevelId = level, Name = "A" };
            document.Add(marker);

            var seen = SectionProjection.Build(document, marker).Pieces
                .Where(piece => piece.ElementId == roof.Id && piece.Depth == SectionDepth.Seen)
                .ToList();

            if (hip)
            {
                Assert.NotEmpty(seen);
                Assert.All(seen, piece => Assert.True(piece.Bounds.Bottom >= 3000 - 1, "The roof is seen below its eaves."));
            }
            else Assert.Empty(seen);
        }
    }
}
