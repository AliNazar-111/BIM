using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>The Eave tool on the plan, and the Eaves button on a roof's tab.</summary>
[Collection("Wpf")]
public class EaveWindowTests
{
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
        if (failure is not null) throw new Exception("The window failed on the UI thread.", failure);
    }

    [Fact]
    public void EavesArePickedOneAfterAnotherTakingOverTheSoffitUnderThem()
    {
        OnUiThread(() =>
        {
            var (document, roof) = EaveTests.House(hip: false);
            var soffit = new Soffit { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<SoffitType>().First().Id };
            soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, roof));
            document.Add(soffit);

            var plan = new PlanView { Document = document, ActiveLevelId = roof.LevelId, Width = 1000, Height = 800 };
            plan.SetTool(PlanTool.Eave);
            plan.ActiveEaveTypeId = document.TypesOf<EaveType>().Single(type => type.Name == "Eave - Box Cornice, Frieze and Bed Moulding").Id;
            Point2D Middle(int i) => roof.Boundary[i].MidpointTo(roof.Boundary[(i + 1) % roof.Boundary.Count]);

            // An eave on the first eave picked; the soffit gives it up.
            Assert.True(plan.PickRoofEdgeAt(Middle(0)));
            var eave = Assert.Single(document.Elements.OfType<Eave>());
            Assert.Equal(plan.ActiveEaveTypeId, eave.TypeId);
            Assert.DoesNotContain(roof.Edges[0].Id, soffit.EdgeIds);

            // A verge is not an eave; the far eave joins the same one.
            Assert.False(plan.PickRoofEdgeAt(Middle(1)));
            Assert.True(plan.PickRoofEdgeAt(Middle(2)));
            Assert.Equal(2, eave.EdgeIds.Count);
            Assert.Equal(new[] { roof.Edges[1].Id, roof.Edges[3].Id }.ToHashSet(), soffit.EdgeIds.ToHashSet());
        });
    }

    [Fact]
    public void TheEavesButtonPutsExposedRaftersUnderEveryEaveOfTheRoofOffTheFascia()
    {
        OnUiThread(() =>
        {
            var (document, roof) = EaveTests.House(hip: true);
            var fascia = new Fascia { RoofId = roof.Id, LevelId = roof.LevelId, TypeId = document.TypesOf<FasciaType>().First().Id };
            fascia.EdgeIds.AddRange(RoofEdgeSweeps.FasciaEdges(document, roof));
            document.Add(fascia);

            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new MainWindow();
            typeof(MainWindow).GetMethod("LoadDocument", hidden)!.Invoke(window, new object?[] { document, null, false });
            var plan = (PlanView)typeof(MainWindow).GetField("Plan", hidden)!.GetValue(window)!;
            var shown = (Core.Documents.BimDocument)plan.Document!;
            var shownRoof = shown.Elements.OfType<Roof>().Single();
            plan.ActiveEaveTypeId = shown.TypesOf<EaveType>().Single(type => type.Name == "Eave - Exposed Rafter Tails, Cove Cut").Id;
            plan.Select(shownRoof);

            typeof(MainWindow).GetMethod("AddRoofEdgesAllRound", hidden)!.Invoke(window, new object[] { RoofEdgeKind.Eave });
            var eave = Assert.Single(shown.Elements.OfType<Eave>());
            Assert.Equal(4, eave.EdgeIds.Count);
            Assert.DoesNotContain(shown.Elements.OfType<Fascia>(), candidate => candidate.RoofId == shownRoof.Id);
            var hint = (TextBlock)typeof(MainWindow).GetField("StatusHint", hidden)!.GetValue(window)!;
            Assert.StartsWith("An eave under 4 eaves", hint.Text);

            // The roof's own tab offers it.
            var button = (Button)typeof(MainWindow).GetField("ContextAddEaves", hidden)!.GetValue(window)!;
            Assert.Equal(Visibility.Visible, button.Visibility);
        });
    }
}
