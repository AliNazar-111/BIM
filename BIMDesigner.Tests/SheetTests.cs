using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Tests over sheets and viewports (specification section 6.5).
///
/// The thing being checked throughout is that a sheet stores references and not drawings: a
/// viewport's size, title and contents are all derived from the model when asked for, so a
/// drawing set cannot drift away from the building it documents.
/// </summary>
public class SheetTests
{
    private const double Tolerance = 1e-6;

    /// <summary>A 6 m x 4 m room of 200 mm walls on the ground floor.</summary>
    private static (BimDocument Document, Guid LevelId) Project()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.StartsWith("Generic"));
        var levelId = document.Levels.First().Id;

        Point2D[] corners =
        {
            new(0, 0), new(6000, 0), new(6000, 4000), new(0, 4000)
        };

        for (var i = 0; i < corners.Length; i++)
        {
            document.Add(new Wall
            {
                Start = corners[i],
                End = corners[(i + 1) % corners.Length],
                TypeId = type.Id,
                LevelId = levelId,
                UnconnectedHeight = 3000
            });
        }

        return (document, levelId);
    }

    private static Sheet AddSheet(BimDocument document)
    {
        var sheet = new Sheet { Number = Sheets.NextNumber(document), Name = "General Arrangement" };
        document.Add(sheet);
        return sheet;
    }

    // ---- paper ------------------------------------------------------------------

    [Fact]
    public void PaperSizesAreTheIsoOnes()
    {
        Assert.Equal(1189, Paper.Width(PaperSize.A0, PaperOrientation.Landscape), Tolerance);
        Assert.Equal(841, Paper.Height(PaperSize.A0, PaperOrientation.Landscape), Tolerance);

        Assert.Equal(297, Paper.Width(PaperSize.A4, PaperOrientation.Landscape), Tolerance);
        Assert.Equal(210, Paper.Height(PaperSize.A4, PaperOrientation.Landscape), Tolerance);
    }

    [Fact]
    public void TurningASheetPortraitSwapsItsEdges()
    {
        var sheet = new Sheet { PaperSize = PaperSize.A3, Orientation = PaperOrientation.Landscape };

        Assert.Equal(420, sheet.Width, Tolerance);
        Assert.Equal(297, sheet.Height, Tolerance);

        sheet.Orientation = PaperOrientation.Portrait;

        Assert.Equal(297, sheet.Width, Tolerance);
        Assert.Equal(420, sheet.Height, Tolerance);
    }

    // ---- scale ------------------------------------------------------------------

    [Fact]
    public void ScaleConvertsBetweenBuildingAndPaper()
    {
        var scale = new ViewScale(50);

        // Six metres of building is 120 mm of paper at 1:50.
        Assert.Equal(120, scale.ToPaper(6000), Tolerance);
        Assert.Equal(6000, scale.ToModel(120), Tolerance);
    }

    [Fact]
    public void FittingAViewChoosesAScaleOffTheRuler()
    {
        // 7.8 m of building into 200 mm of paper needs 1:50 or smaller; 1:50 gives 156 mm.
        Assert.Equal(50, ViewScale.FittingInto(7800, 200).Denominator, Tolerance);

        // The same drawing on a much narrower strip has to go further out.
        Assert.Equal(200, ViewScale.FittingInto(7800, 40).Denominator, Tolerance);

        // Never an arbitrary ratio: every answer is a scale that appears on a scale rule.
        Assert.Contains(ViewScale.FittingInto(12345, 173).Denominator, ViewScale.Common);
    }

    [Fact]
    public void FittingAViewFitsItBothWaysNotJustAcross()
    {
        // A tall, narrow drawing: 4 m wide and 30 m high into a 200 x 250 mm space. Fitting
        // the width alone would pick 1:20 and run 1.5 m of paper off the top of the sheet.
        var scale = ViewScale.FittingInto(4000, 30000, 200, 250);

        Assert.True(scale.ToPaper(4000) <= 200);
        Assert.True(scale.ToPaper(30000) <= 250);
        Assert.Equal(200, scale.Denominator, Tolerance);
    }

    [Fact]
    public void AutoFittedViewsLandInsideTheDrawingArea()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        // The same arithmetic the Add button does.
        var extent = ViewExtent.Of(document, ViewReference.FloorPlan(levelId)).OrAtLeast(1000);
        var availableWidth = sheet.Width - TitleBlock.Width(sheet) - TitleBlock.Margin * 3;
        var availableHeight = sheet.Height - TitleBlock.Margin * 2 - 16;

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Scale = ViewScale.FittingInto(extent.Width, extent.Height, availableWidth, availableHeight)
        };

        var size = viewport.PaperBounds(document);
        viewport.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(viewport);

        var placed = viewport.PaperBounds(document);

        // A viewport that lands off the paper is a sheet that looks empty.
        Assert.True(placed.Width > 0 && placed.Height > 0);
        Assert.True(placed.MinX >= 0 && placed.MaxX <= sheet.Width);
        Assert.True(placed.MinY >= 0 && placed.MaxY <= sheet.Height);
        Assert.True(placed.MaxX <= sheet.Width - TitleBlock.Width(sheet),
            "a drawing must not run under the title block");
    }

    [Fact]
    public void AnInvalidScaleDoesNotDivideByZero()
    {
        var scale = new ViewScale(0);

        Assert.False(scale.IsValid);
        Assert.Equal(6000, scale.ToPaper(6000), Tolerance);
    }

    // ---- viewports --------------------------------------------------------------

    [Fact]
    public void AViewportIsAsBigAsItsContentsAtItsScale()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(200, 200),
            Scale = new ViewScale(100)
        };

        sheet.Add(viewport);

        // Walls 6000 x 4000 between centrelines, measured to their faces (+100 all round),
        // plus the standard margin, all at 1:100.
        var expectedWidth = (6200 + ViewExtent.Margin * 2) / 100;
        var expectedHeight = (4200 + ViewExtent.Margin * 2) / 100;

        var bounds = viewport.PaperBounds(document);

        Assert.Equal(expectedWidth, bounds.Width, precision: 6);
        Assert.Equal(expectedHeight, bounds.Height, precision: 6);
        Assert.Equal(200, bounds.Centre.X, precision: 6);
        Assert.Equal(200, bounds.Centre.Y, precision: 6);
    }

    [Fact]
    public void ChangingTheScaleChangesHowMuchPaperTheViewTakes()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport { View = ViewReference.FloorPlan(levelId), Centre = new Point2D(200, 150) };
        sheet.Add(viewport);

        viewport.Scale = new ViewScale(100);
        var atOneHundred = viewport.PaperBounds(document).Width;

        viewport.Scale = new ViewScale(50);
        var atFifty = viewport.PaperBounds(document).Width;

        // Twice as big a drawing for half the denominator. Nothing stored had to be updated.
        Assert.Equal(atOneHundred * 2, atFifty, precision: 6);
    }

    [Fact]
    public void AViewportGrowsWhenTheBuildingDoes()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport { View = ViewReference.FloorPlan(levelId), Centre = new Point2D(200, 150) };
        sheet.Add(viewport);

        var before = viewport.PaperBounds(document).Width;

        document.Add(new Wall
        {
            Start = new Point2D(6000, 0),
            End = new Point2D(12000, 0),
            TypeId = document.TypesOf<WallType>().First().Id,
            LevelId = levelId
        });

        // A stored viewport size would have cropped the extension off the drawing silently.
        Assert.True(viewport.PaperBounds(document).Width > before,
            "a viewport is sized from its contents, so extending the building must widen it");
    }

    [Fact]
    public void AnEmptyViewStillHasSomeSizeRatherThanNone()
    {
        var document = BimDocument.CreateDefault();
        var sheet = AddSheet(document);

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(document.Levels.First().Id),
            Centre = new Point2D(200, 150)
        };

        sheet.Add(viewport);

        // A plan of nothing has no extent; dividing by that width would give infinity.
        var bounds = viewport.PaperBounds(document);
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
    }

    [Fact]
    public void AViewportKnowsWhichPartOfThePaperItCovers()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(200, 150),
            Scale = new ViewScale(100)
        };

        sheet.Add(viewport);

        Assert.True(viewport.Contains(document, new Point2D(200, 150)));
        Assert.False(viewport.Contains(document, new Point2D(500, 150)));
    }

    // ---- what a viewport shows --------------------------------------------------

    [Fact]
    public void AViewportTitleIsReadFromTheModelNotTyped()
    {
        var (document, levelId) = Project();
        var viewport = new Viewport { View = ViewReference.FloorPlan(levelId) };

        Assert.Equal("Ground Floor Plan", viewport.TitleIn(document));

        // Renaming the level retitles the drawing, with nothing in between to update.
        document.Levels.First().Name = "Lower Ground";
        Assert.Equal("Lower Ground Plan", viewport.TitleIn(document));
    }

    [Fact]
    public void ATypedTitleOverridesTheViewsOwnName()
    {
        var (document, levelId) = Project();

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            TitleOverride = "Proposed Ground Floor"
        };

        Assert.Equal("Proposed Ground Floor", viewport.TitleIn(document));
    }

    [Fact]
    public void AViewportOfASectionIsAsTallAsTheBuildingIsOnThatCut()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(3000, -3000),
            End = new Point2D(3000, 7000),
            LevelId = levelId
        };
        document.Add(marker);

        var viewport = new Viewport
        {
            View = ViewReference.Section(marker.Id),
            Centre = new Point2D(200, 150),
            Scale = new ViewScale(100)
        };

        sheet.Add(viewport);

        Assert.Equal("Section A", viewport.TitleIn(document));

        // The cut is 10 m long; the walls stand 3 m, and the ground and first floor datums
        // reach to 3000, so the drawing is 3 m tall plus its margins.
        var bounds = viewport.PaperBounds(document);
        Assert.Equal((10000 + ViewExtent.Margin * 2) / 100, bounds.Width, precision: 6);
        Assert.Equal((3000 + ViewExtent.Margin * 2) / 100, bounds.Height, precision: 6);
    }

    [Fact]
    public void AViewportWhoseViewHasGoneSaysSoRatherThanCrashing()
    {
        var (document, levelId) = Project();

        var marker = new SectionMarker { Name = "A", Start = new Point2D(0, 0), End = new Point2D(1000, 0) };
        document.Add(marker);

        var viewport = new Viewport { View = ViewReference.Section(marker.Id) };
        Assert.True(viewport.View.ExistsIn(document));

        document.Remove(marker);

        Assert.False(viewport.View.ExistsIn(document));
        Assert.Equal("Section - missing marker", viewport.TitleIn(document));
        Assert.True(viewport.PaperBounds(document).Width > 0);

        Assert.True(new Viewport { View = ViewReference.FloorPlan(levelId) }.View.ExistsIn(document));
    }

    // ---- laying out a sheet -----------------------------------------------------

    [Fact]
    public void NewViewsAreLaidOutBesideEachOtherRatherThanStacked()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var first = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = first.PaperBounds(document);
        first.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(first);

        var second = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        second.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(second);

        var a = first.PaperBounds(document);
        var b = second.PaperBounds(document);

        var overlaps = a.MinX < b.MaxX && a.MaxX > b.MinX && a.MinY < b.MaxY && a.MaxY > b.MinY;
        Assert.False(overlaps, "a second view dropped on top of the first would hide it");
    }

    [Fact]
    public void ViewsAreKeptOffTheTitleBlockAndTheSheetEdge()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = viewport.PaperBounds(document);
        viewport.Centre = sheet.NextFreePosition(document, size.Width, size.Height);

        var placed = viewport.PaperBounds(document);

        Assert.True(placed.MinX >= TitleBlock.Margin - Tolerance);
        Assert.True(placed.MinY >= TitleBlock.Margin - Tolerance);
        Assert.True(placed.MaxY <= sheet.Height - TitleBlock.Margin + Tolerance);
        Assert.True(placed.MaxX <= sheet.Width - TitleBlock.Width(sheet) - TitleBlock.Margin + Tolerance);
    }

    // ---- keeping views where they belong ----------------------------------------

    /// <summary>A sheet with one 1:100 plan placed at the top left of its drawing area.</summary>
    private static (BimDocument Document, Sheet Sheet, Viewport Viewport, Guid LevelId) LaidOut()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        var viewport = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = viewport.PaperBounds(document);
        viewport.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(viewport);

        return (document, sheet, viewport, levelId);
    }

    private static bool Inside(BoundingBox2D inner, BoundingBox2D outer) =>
        inner.MinX >= outer.MinX - Tolerance && inner.MaxX <= outer.MaxX + Tolerance &&
        inner.MinY >= outer.MinY - Tolerance && inner.MaxY <= outer.MaxY + Tolerance;

    private static bool Overlap(BoundingBox2D a, BoundingBox2D b) =>
        a.MinX < b.MaxX - Tolerance && a.MaxX > b.MinX + Tolerance &&
        a.MinY < b.MaxY - Tolerance && a.MaxY > b.MinY + Tolerance;

    [Fact]
    public void AViewCannotBeDraggedOffThePaper()
    {
        var (document, sheet, viewport, _) = LaidOut();

        // Far below and to the right of the sheet.
        viewport.Centre = sheet.ConstrainMove(document, viewport, new Point2D(5000, -5000));

        Assert.True(Inside(Sheet.Footprint(document, viewport), sheet.DrawingArea),
            "a view dragged off the sheet must stop at the edge of the drawing area");
    }

    [Fact]
    public void AViewCannotBeDraggedOverTheTitleBlock()
    {
        var (document, sheet, viewport, _) = LaidOut();

        viewport.Centre = sheet.ConstrainMove(document, viewport, new Point2D(sheet.Width - 20, sheet.Height / 2));

        var footprint = Sheet.Footprint(document, viewport);
        Assert.True(footprint.MaxX <= sheet.Width - TitleBlock.Width(sheet) - TitleBlock.Margin + Tolerance);
    }

    [Fact]
    public void AViewCannotBeDraggedOnTopOfAnother()
    {
        var (document, sheet, first, levelId) = LaidOut();

        var second = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = second.PaperBounds(document);
        second.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(second);

        // Drag the second straight onto the first.
        second.Centre = sheet.ConstrainMove(document, second, first.Centre);

        Assert.False(Overlap(Sheet.Footprint(document, first), Sheet.Footprint(document, second)));
        Assert.True(sheet.IsWellPlaced(document, second));
    }

    [Fact]
    public void AViewBlockedOneWaySlidesTheOtherWay()
    {
        var (document, sheet, first, levelId) = LaidOut();

        var second = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = second.PaperBounds(document);
        second.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(second);

        // The second sits beside the first. Ask it to move left into the first and down at the
        // same time: the left is blocked, but it should still go down.
        var start = second.Centre;
        second.Centre = sheet.ConstrainMove(document, second, new Point2D(first.Centre.X, start.Y - 40));

        Assert.True(second.Centre.Y < start.Y, "a blocked view should slide along what blocks it, not stop dead");
        Assert.False(Overlap(Sheet.Footprint(document, first), Sheet.Footprint(document, second)));
    }

    [Fact]
    public void AViewTitleCountsAsPartOfTheView()
    {
        var (document, sheet, viewport, _) = LaidOut();

        var drawing = viewport.PaperBounds(document);
        var footprint = Sheet.Footprint(document, viewport);

        // Two drawings that clear each other but whose titles collide are still on top of each other.
        Assert.Equal(drawing.MinY - Viewport.TitleBand, footprint.MinY, Tolerance);

        viewport.ShowTitle = false;
        Assert.Equal(drawing.MinY, Sheet.Footprint(document, viewport).MinY, Tolerance);
    }

    [Fact]
    public void AViewAlreadyOverlappingCanStillBeDraggedFree()
    {
        var (document, sheet, first, levelId) = LaidOut();

        // Put a second view straight on top of the first, as shrinking the paper might.
        var second = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Scale = new ViewScale(100),
            Centre = first.Centre
        };
        sheet.Add(second);

        Assert.False(sheet.IsWellPlaced(document, second));

        // It must not be stuck where it is just because every small move still overlaps.
        var free = sheet.NextFreePosition(document, second.PaperBounds(document).Width,
            second.PaperBounds(document).Height, ignore: second);

        second.Centre = sheet.ConstrainMove(document, second, free);

        Assert.True(sheet.IsWellPlaced(document, second));
    }

    [Fact]
    public void ShrinkingThePaperShowsWhichViewsNoLongerFit()
    {
        var (document, sheet, viewport, _) = LaidOut();
        Assert.Empty(sheet.Misplaced(document));

        // Nothing moves when the paper changes, but the view no longer fits where it was.
        sheet.PaperSize = PaperSize.A4;
        viewport.Centre = new Point2D(250, 170);

        Assert.Contains(viewport, sheet.Misplaced(document));
    }

    [Fact]
    public void EnlargingTheScaleMovesTheViewIfItNoLongerFitsAndOneUndoPutsItBack()
    {
        var (document, sheet, first, levelId) = LaidOut();
        var history = new UndoStack();

        var second = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(100) };
        var size = second.PaperBounds(document);
        second.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
        sheet.Add(second);

        var centre = first.Centre;

        // At 1:50 the first view doubles in size and would run into the second.
        history.Execute(SetViewportScaleCommand.On(document, sheet, first, new ViewScale(50)));

        Assert.Equal(50, first.Scale.Denominator, Tolerance);
        Assert.True(sheet.IsWellPlaced(document, first));
        Assert.True(sheet.IsWellPlaced(document, second));

        history.Undo();

        Assert.Equal(100, first.Scale.Denominator, Tolerance);
        Assert.Equal(centre, first.Centre);
    }

    [Fact]
    public void ShrinkingTheScaleLeavesTheViewWhereItIs()
    {
        var (document, sheet, viewport, _) = LaidOut();
        var centre = viewport.Centre;

        var command = SetViewportScaleCommand.On(document, sheet, viewport, new ViewScale(200));
        command.Redo();

        // Smaller always fits where it was; moving it would only be annoying.
        Assert.Equal(centre, viewport.Centre);
    }

    [Fact]
    public void ManyViewsPlacedInTurnNeverOverlap()
    {
        var (document, sheet, _, levelId) = LaidOut();

        for (var i = 0; i < 5; i++)
        {
            var viewport = new Viewport { View = ViewReference.FloorPlan(levelId), Scale = new ViewScale(200) };
            var size = viewport.PaperBounds(document);
            viewport.Centre = sheet.NextFreePosition(document, size.Width, size.Height);
            sheet.Add(viewport);
        }

        Assert.Empty(sheet.Misplaced(document));
    }

    [Fact]
    public void SheetNumbersCarryOnFromTheHighestAlreadyUsed()
    {
        var document = BimDocument.CreateDefault();

        Assert.Equal("A-101", Sheets.NextNumber(document));

        document.Add(new Sheet { Number = "A-101" });
        Assert.Equal("A-102", Sheets.NextNumber(document));

        // Out-of-order numbering must not hand back one that is already taken.
        document.Add(new Sheet { Number = "A-205" });
        Assert.Equal("A-206", Sheets.NextNumber(document));
    }

    // ---- editing ----------------------------------------------------------------

    [Fact]
    public void PlacingMovingAndRemovingAViewAreAllUndoable()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);
        var history = new UndoStack();

        var viewport = new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(150, 150)
        };

        history.Execute(new AddViewportCommand(sheet, viewport, "Place View"));
        Assert.Single(sheet.Viewports);

        history.Execute(new MoveViewportCommand(viewport, viewport.Centre, new Point2D(300, 200)));
        Assert.Equal(300, viewport.Centre.X, Tolerance);

        history.Execute(new SetViewportScaleCommand(viewport, new ViewScale(50)));
        Assert.Equal(50, viewport.Scale.Denominator, Tolerance);

        history.Undo();
        Assert.Equal(100, viewport.Scale.Denominator, Tolerance);

        history.Undo();
        Assert.Equal(150, viewport.Centre.X, Tolerance);

        history.Undo();
        Assert.Empty(sheet.Viewports);
    }

    [Fact]
    public void UndoingARemovalPutsTheViewBackInItsOriginalOrder()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);
        var history = new UndoStack();

        var first = new Viewport { View = ViewReference.FloorPlan(levelId) };
        var second = new Viewport { View = ViewReference.FloorPlan(levelId) };
        var third = new Viewport { View = ViewReference.FloorPlan(levelId) };

        sheet.Add(first);
        sheet.Add(second);
        sheet.Add(third);

        history.Execute(new RemoveViewportCommand(sheet, second));
        history.Undo();

        // Back where it was, not on top: drawing order is part of what the sheet looks like.
        Assert.Equal(1, sheet.IndexOf(second));
    }

    // ---- persistence ------------------------------------------------------------

    [Fact]
    public void SheetsSurviveSavingAndReopening()
    {
        var (document, levelId) = Project();

        var marker = new SectionMarker
        {
            Name = "A",
            Start = new Point2D(3000, -3000),
            End = new Point2D(3000, 7000),
            LevelId = levelId
        };
        document.Add(marker);

        var sheet = AddSheet(document);
        sheet.PaperSize = PaperSize.A2;
        sheet.Orientation = PaperOrientation.Portrait;
        sheet.DrawnBy = "AB";
        sheet.CheckedBy = "CD";
        sheet.Revision = "P3";

        sheet.Add(new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(120, 300),
            Scale = new ViewScale(50)
        });

        sheet.Add(new Viewport
        {
            View = ViewReference.Section(marker.Id),
            Centre = new Point2D(120, 120),
            Scale = new ViewScale(100),
            TitleOverride = "Section Through Living Room",
            ShowTitle = false
        });

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var match = Assert.Single(reloaded.Elements.OfType<Sheet>());

        Assert.Equal(sheet.Id, match.Id);
        Assert.Equal(sheet.Number, match.Number);
        Assert.Equal(PaperSize.A2, match.PaperSize);
        Assert.Equal(PaperOrientation.Portrait, match.Orientation);
        Assert.Equal("AB", match.DrawnBy);
        Assert.Equal("CD", match.CheckedBy);
        Assert.Equal("P3", match.Revision);
        Assert.Equal(2, match.Viewports.Count);

        var plan = match.Viewports[0];
        Assert.Equal(ViewKind.FloorPlan, plan.View.Kind);
        Assert.Equal(levelId, plan.View.TargetId);
        Assert.Equal(50, plan.Scale.Denominator, Tolerance);
        Assert.Equal(120, plan.Centre.X, Tolerance);

        var section = match.Viewports[1];
        Assert.Equal(ViewKind.Section, section.View.Kind);
        Assert.Equal(marker.Id, section.View.TargetId);
        Assert.False(section.ShowTitle);
        Assert.Equal("Section Through Living Room", section.TitleIn(reloaded));
    }

    [Fact]
    public void AReopenedSheetMeasuresTheSame()
    {
        var (document, levelId) = Project();
        var sheet = AddSheet(document);

        sheet.Add(new Viewport
        {
            View = ViewReference.FloorPlan(levelId),
            Centre = new Point2D(200, 150),
            Scale = new ViewScale(100)
        });

        var before = sheet.Viewports[0].PaperBounds(document);

        var reloaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var after = reloaded.Elements.OfType<Sheet>().Single().Viewports[0].PaperBounds(reloaded);

        Assert.Equal(before.Width, after.Width, precision: 6);
        Assert.Equal(before.Height, after.Height, precision: 6);
        Assert.Equal(before.MinX, after.MinX, precision: 6);
        Assert.Equal(before.MinY, after.MinY, precision: 6);
    }

    [Fact]
    public void AProjectSavedBeforeSheetsExistedStillOpens()
    {
        var (document, _) = Project();
        AddSheet(document);

        var json = ProjectFile.ToJson(document).Replace("\"sheets\"", "\"sheetsRemoved\"");
        var reloaded = ProjectFile.FromJson(json);

        Assert.Empty(reloaded.Elements.OfType<Sheet>());
        Assert.Equal(4, reloaded.Walls.Count());
    }

    // ---- extents ----------------------------------------------------------------

    [Fact]
    public void AnExtentGrowsToHoldEveryPointGivenToIt()
    {
        var box = BoundingBox2D.Empty;

        Assert.True(box.IsEmpty);

        box = box.Include(new Point2D(10, 20)).Include(new Point2D(-5, 40));

        Assert.Equal(-5, box.MinX, Tolerance);
        Assert.Equal(20, box.MinY, Tolerance);
        Assert.Equal(10, box.MaxX, Tolerance);
        Assert.Equal(40, box.MaxY, Tolerance);
        Assert.Equal(15, box.Width, Tolerance);
        Assert.Equal(new Point2D(2.5, 30), box.Centre);
    }

    [Fact]
    public void AFlatExtentIsGivenSomeSizeSoNothingDividesByZero()
    {
        var line = BoundingBox2D.Around(new[] { new Point2D(0, 5), new Point2D(1000, 5) });

        Assert.Equal(0, line.Height, Tolerance);

        var usable = line.OrAtLeast(200);

        Assert.Equal(1000, usable.Width, Tolerance);
        Assert.Equal(200, usable.Height, Tolerance);
        Assert.Equal(5, usable.Centre.Y, Tolerance);
    }
}
