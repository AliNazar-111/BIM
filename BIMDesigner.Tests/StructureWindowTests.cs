using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;

namespace BIMDesigner.Tests;

/// <summary>The structural Column tool, the Wall and Isolated foundation tools, and the Footings button.</summary>
[Collection("Wpf")]
public class StructureWindowTests
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
    public void ColumnsAndFootingsArePlacedOnThePlan()
    {
        OnUiThread(() =>
        {
            var (document, _) = EaveTests.House(hip: false);
            var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, Width = 1000, Height = 800 };

            // A structural column, from the structural types.
            plan.ActiveStructuralColumnTypeId = document.TypesOf<ColumnType>().Single(type => type.Name == "UC 152 x 152 x 30").Id;
            plan.SetTool(PlanTool.StructuralColumn);
            typeof(PlanView).GetMethod("PlaceColumn", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(plan, new object[] { new Point2D(5000, 4000) });
            var column = Assert.Single(document.Elements.OfType<Column>());
            Assert.True(column.Structural);

            // A footing under the wall clicked, sized by the rules; not a second one.
            plan.SetTool(PlanTool.WallFooting);
            Assert.True(plan.PlaceWallFootingAt(new Point2D(5000, 0)));
            var footing = Assert.Single(document.Elements.OfType<WallFooting>());
            Assert.Equal(document.Walls.First(wall => wall.Start.Y == 0 && wall.End.Y == 0).Id, footing.WallId);
            Assert.False(plan.PlaceWallFootingAt(new Point2D(5000, 0)));

            // A pad under the column clicked.
            plan.SetTool(PlanTool.PadFooting);
            Assert.True(plan.PlacePadFootingAt(new Point2D(5000, 4000)));
            Assert.Equal(column.Id, Assert.Single(document.Elements.OfType<PadFooting>()).ColumnId);

            // Footings under every other wall at once.
            Assert.Equal(3, plan.AddFootings(document.Walls.ToList()));
            Assert.Equal(4, document.Elements.OfType<WallFooting>().Count());
        });
    }

    [Fact]
    public void TheGroundIsSetInThePlansProperties()
    {
        OnUiThread(() =>
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new MainWindow();
            var document = BimDocument.CreateDefault();
            typeof(MainWindow).GetMethod("LoadDocument", hidden)!.Invoke(window, new object?[] { document, null, false });
            var shown = (BimDocument)((PlanView)typeof(MainWindow).GetField("Plan", hidden)!.GetValue(window)!).Document!;
            var parameters = (IEnumerable<BIMDesigner.Core.Parameters.ParameterValue>)typeof(MainWindow).GetMethods(hidden)
                .First(method => method.Name == "PlanViewParameters").Invoke(window, new object[] { "Ground Floor" })!;
            var list = parameters.ToList();
            Assert.True(list.Single(p => p.Name == "Ground").TrySetFromText("Soft clay or silt (type VI, soils investigation)"));
            Assert.Equal(GroundType.SoftClay, shown.ProjectInformation.Ground);
            Assert.True(list.Single(p => p.Name == "Clay Shrinkage").TrySetFromText("High"));
            Assert.Equal(ClayShrinkage.High, shown.ProjectInformation.ClayShrinkage);
        });
    }
}
