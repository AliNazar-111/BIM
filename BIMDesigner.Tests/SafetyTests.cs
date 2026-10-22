using System.Reflection;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Diagnostics;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Rendering;

namespace BIMDesigner.Tests;

/// <summary>
/// When something goes wrong: an edit that fails part-way is taken back; an element that cannot be
/// drawn is left out and the rest drawn; unsaved work is kept, and offered back after a crash; and
/// what went wrong is logged and said in words.
/// </summary>
[Collection("Wpf")]
public class SafetyTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    /// <summary>A floor that cannot say where its top is - as a corrupt one might not.</summary>
    private sealed class BrokenFloor : Slab
    {
        public override BuiltInCategory Category => BuiltInCategory.Floors;

        public override double GetTopElevation(BimDocument document) => throw new DeliberateFailure();
    }

    private static void OnUiThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                action();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("Failed on the UI thread.", failure);
    }

    [Fact]
    public void AnEditThatGoesWrongPartWayIsTakenBackAndNotRecorded()
    {
        var document = ConstructionTests.House();
        var wall = document.Walls.First();
        var start = wall.Start;
        var history = new UndoStack();

        var failing = new DelegateCommand("Move Wall", () =>
        {
            wall.Start = new Point2D(-500, -500);
            throw new DeliberateFailure();
        }, () => wall.Start = start);
        var failed = Assert.Throws<EditFailedException>(() => history.Execute(failing));
        Assert.Equal("Move Wall", failed.Edit);
        Assert.IsType<DeliberateFailure>(failed.InnerException);
        Assert.Equal(start, wall.Start);
        Assert.False(history.CanUndo);
        Assert.False(history.IsModified);

        // An undo that goes wrong part-way is put back, still there to undo.
        var moved = new Point2D(-200, 0);
        history.Execute(new DelegateCommand("Move Wall", () => wall.Start = moved, () =>
        {
            wall.Start = start;
            throw new DeliberateFailure();
        }));
        Assert.Throws<EditFailedException>(() => history.Undo());
        Assert.Equal(moved, wall.Start);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void AFloorThatCannotBeBuiltIsLeftOutAndTheRestOfTheBuildingIsBuilt()
    {
        var document = ConstructionTests.House();
        var broken = new BrokenFloor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        broken.SetBoundary(new[] { new Point2D(1000, 1000), new Point2D(3000, 1000), new Point2D(3000, 3000), new Point2D(1000, 3000) });
        document.Add(broken);

        var failed = new List<Guid>();
        void Failed(Element element, Exception _) => failed.Add(element.Id);
        DrawFailures.Failed += Failed;
        try
        {
            var meshes = ModelMeshBuilder.Build(document);
            Assert.Contains(broken.Id, failed);
            Assert.DoesNotContain(meshes, mesh => mesh.ElementId == broken.Id);
            Assert.All(document.Walls, wall => Assert.Contains(meshes, mesh => mesh.ElementId == wall.Id));
            Assert.Contains(meshes, mesh => mesh.ElementId == document.All<Floor>().First().Id);

            // Cut through in section: the same.
            failed.Clear();
            var marker = new SectionMarker { Start = new Point2D(2000, -2000), End = new Point2D(2000, 10000), LevelId = document.Levels[0].Id };
            var section = SectionProjection.Build(document, marker);
            Assert.Contains(broken.Id, failed);
            Assert.Contains(section.Pieces, piece => document.FindElement(piece.ElementId) is Wall);
        }
        finally
        {
            DrawFailures.Failed -= Failed;
        }
    }

    [Fact]
    public void AnElementThatCannotBeDrawnOnThePlanIsLeftOutAndTheNextIsDrawn()
    {
        OnUiThread(() =>
        {
            var document = ConstructionTests.House();
            var renderer = new PlanRenderer { Document = document };
            var styled = typeof(PlanRenderer).GetMethod("Styled", Hidden)!;
            var (one, two) = (document.Walls.First(), document.Walls.Last());
            var failed = new List<Guid>();
            void Failed(Element element, Exception _) => failed.Add(element.Id);
            DrawFailures.Failed += Failed;
            try
            {
                var drawing = new DrawingGroup();
                using (var dc = drawing.Open())
                {
                    // Half drawn, then gone wrong - with a transform pushed and never popped.
                    styled.Invoke(renderer, new object[] { dc, one, new Action<DrawingContext>(inner =>
                    {
                        inner.PushTransform(new TranslateTransform(1000, 0));
                        inner.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 10, 10));
                        throw new DeliberateFailure();
                    }) });
                    styled.Invoke(renderer, new object[] { dc, two, new Action<DrawingContext>(inner => inner.DrawRectangle(Brushes.Blue, null, new Rect(0, 0, 10, 10))) });
                }

                Assert.Equal(new[] { one.Id }, failed);

                // Nothing of the one that went wrong: only the next, drawn where it should be.
                var bounds = drawing.Bounds;
                Assert.Equal(new Rect(0, 0, 10, 10), bounds);
            }
            finally
            {
                DrawFailures.Failed -= Failed;
            }
        });
    }

    /// <summary>A session that stops as a crash stops it: its lock let go by Windows, its copy left behind.</summary>
    private static void Crash(RecoverySession session) =>
        ((FileStream?)typeof(RecoverySession).GetField("_lock", Hidden)!.GetValue(session))?.Dispose();

    [Fact]
    public void UnsavedWorkIsKeptAndOnlyALostSessionsWorkIsOfferedBack()
    {
        foreach (var left in RecoverySession.Left()) RecoverySession.Forget(left);
        var document = ConstructionTests.House();
        document.ProjectInformation.Name = "The House";

        // Kept while the session runs: it is not offered to anyone else.
        var running = new RecoverySession();
        Assert.True(running.Keep(document, @"C:\Projects\house.bimx"));
        Assert.True(running.HasCopy);
        Assert.Empty(RecoverySession.Left());

        // Closed as it should be: the copy goes with it.
        running.Dispose();
        Assert.False(running.HasCopy);
        Assert.Empty(RecoverySession.Left());

        // Stopped by a crash: offered back, with what it had been saved as, and read back whole.
        var lost = new RecoverySession();
        lost.Keep(document, @"C:\Projects\house.bimx");
        Crash(lost);
        var work = Assert.Single(RecoverySession.Left());
        Assert.Equal((@"C:\Projects\house.bimx", "The House"), (work.SavedAs, work.Name));
        Assert.Equal(document.Walls.Count(), ProjectFile.Load(work.File).Walls.Count());

        RecoverySession.Forget(work);
        Assert.Empty(RecoverySession.Left());
    }

    [Fact]
    public void TheMainWindowKeepsUnsavedWorkAndBringsItBackAfterACrash()
    {
        OnUiThread(() =>
        {
            foreach (var left in RecoverySession.Left()) RecoverySession.Forget(left);

            // A session with a wall drawn and not saved, that then stops.
            var before = new MainWindow();
            typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(before, new object?[] { ConstructionTests.House(), null, false });
            var history = (UndoStack)typeof(MainWindow).GetField("_history", Hidden)!.GetValue(before)!;
            var shown = (BimDocument)typeof(MainWindow).GetField("_document", Hidden)!.GetValue(before)!;
            Assert.False(before.KeepWork(now: true));
            var added = new Wall { Start = new Point2D(0, 0), End = new Point2D(0, -4000), TypeId = shown.Walls.First().TypeId, LevelId = shown.Levels[0].Id };
            history.Execute(new AddElementCommand(shown, added, "Draw Wall"));
            Assert.True(before.HasUnsavedWork);
            Assert.True(before.KeepWork(now: true));
            Crash((RecoverySession)typeof(MainWindow).GetField("_recovery", Hidden)!.GetValue(before)!);

            // The next session offers it back: brought back as it was, and in no file till saved.
            var after = new MainWindow();
            typeof(MainWindow).GetMethod("LoadDocument", Hidden)!.Invoke(after, new object?[] { BimDocument.CreateDefault(), null, false });
            RecoveredWork? offered = null;
            after.OfferRecovery(work =>
            {
                offered = work;
                return MessageBoxResult.Yes;
            });
            Assert.NotNull(offered);
            var back = (BimDocument)typeof(MainWindow).GetField("_document", Hidden)!.GetValue(after)!;
            Assert.NotNull(back.FindElement(added.Id));
            Assert.True(after.HasUnsavedWork);
            Assert.Empty(RecoverySession.Left());

            // Saved, the copy is not wanted any more.
            var path = Path.Combine(Path.GetTempPath(), $"recovered-{Guid.NewGuid():N}.bimx");
            try
            {
                Assert.True(after.KeepWork(now: true));
                Assert.True((bool)typeof(MainWindow).GetMethod("WriteTo", Hidden)!.Invoke(after, new object[] { path })!);
                Assert.False(((RecoverySession)typeof(MainWindow).GetField("_recovery", Hidden)!.GetValue(after)!).HasCopy);
                Assert.False(after.HasUnsavedWork);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void WhatWentWrongIsLoggedAndSaidInWords()
    {
        AppLog.Error("Testing the log.", new DeliberateFailure("A thing broke."));
        var logged = File.ReadAllText(AppLog.CurrentFile);
        Assert.Contains("Testing the log.", logged);
        Assert.Contains("A thing broke.", logged);
        Assert.Contains(nameof(DeliberateFailure), logged);

        var report = AppLog.Report(new DeliberateFailure("A thing broke."), "drawing a wall");
        Assert.Contains("BIMDesigner", report);
        Assert.Contains("While: drawing a wall", report);
        Assert.Contains(nameof(DeliberateFailure), report);

        var said = ErrorReporting.Said(new EditFailedException("Move Wall", new DeliberateFailure("A thing broke.")));
        Assert.StartsWith("Move Wall could not be done", said);
        Assert.Contains("taken back", said);
        Assert.Contains("A thing broke.", said);
        Assert.Contains("did not expect", ErrorReporting.Said(new InvalidOperationException("Odd.")));
        Assert.Contains("Nothing had been opened", ErrorReporting.Safe(null, false));

        OnUiThread(() =>
        {
            var window = new ErrorWindow(said, ErrorReporting.Safe(null, false), report);
            Assert.Equal(report, window.Details);
            Assert.False(window.CloseApp);
        });
    }
}
