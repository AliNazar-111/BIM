using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>
/// Slab edges: a thickened edge, an edge beam or a kerb run along a floor's edges, mitred round
/// its corners - the floor's own, going where it goes.
/// </summary>
[Collection("Wpf")]
public class SlabEdgeTests
{
    private static readonly Point2D[] Outline = { new(0, 0), new(10000, 0), new(10000, 6000), new(0, 6000) };

    private static (BimDocument Document, Floor Floor, SlabEdge Edge) Edged(params int[] sides)
    {
        var document = BimDocument.CreateDefault();
        var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
        floor.SetBoundary(Outline);
        document.Add(floor);
        var type = document.TypesOf<SlabEdgeType>().Single(candidate => candidate.Name == "Slab Edge - Edge Beam 300 x 600");
        var edge = new SlabEdge { FloorId = floor.Id, LevelId = floor.LevelId, TypeId = type.Id };
        foreach (var side in sides) edge.Edges.Add((Outline[side], Outline[(side + 1) % 4]));
        document.Add(edge);
        return (document, floor, edge);
    }

    [Fact]
    public void ItRunsAlongTheEdgesPickedMitredRoundTheirCorners()
    {
        // Two sides meeting at a corner: one run of 16 m, the beam 300 in under the floor and 600 down.
        var (document, floor, edge) = Edged(0, 1);
        var run = Assert.Single(SlabEdgeRuns.Runs(document, edge));
        Assert.False(run.Closed);
        Assert.Equal(3, run.Points.Count);
        Assert.Equal(16000, SlabEdgeRuns.Length(document, edge), 6);
        Assert.Equal(300 * 600 * 16000.0, SlabEdgeRuns.Volume(document, edge), 0);

        // Its inner line mitred at the corner, 300 in from both sides.
        var inner = SlabEdgeRuns.Inset(run.Points, false, 300);
        Assert.Equal(new Point2D(9700, 300), inner[1]);

        // As a solid: as deep as the type, from the floor's top down.
        var mesh = SlabEdgeRuns.Mesh(document, edge)!;
        var top = floor.GetTopElevation(document);
        Assert.Equal(top, mesh.Positions.Max(point => point.Z), 6);
        Assert.Equal(top - 600, mesh.Positions.Min(point => point.Z), 6);
        Assert.Contains(ModelMeshBuilder.Build(document), built => built.ElementId == edge.Id);

        // All four sides: one closed run round the floor; an upstand stands up out of it instead.
        foreach (var side in new[] { 2, 3 }) edge.Edges.Add((Outline[side], Outline[(side + 1) % 4]));
        Assert.True(Assert.Single(SlabEdgeRuns.Runs(document, edge)).Closed);
        edge.TypeId = document.TypesOf<SlabEdgeType>().Single(type => type.Shape == SlabEdgeShape.Upstand).Id;
        Assert.Equal(top + 150, SlabEdgeRuns.Mesh(document, edge)!.Positions.Max(point => point.Z), 6);
    }

    [Fact]
    public void ItGoesWithItsFloorAndIsSavedAndExported()
    {
        var (document, floor, edge) = Edged(0, 1);

        // Moved with its floor, it is still on its edges; an edge the floor no longer has is let go.
        new MoveElementsCommand(new Element[] { floor }, new Vector2D(1000, 500), document: document).Redo();
        Assert.Equal(16000, SlabEdgeRuns.Length(document, edge), 6);
        new RotateElementsCommand(new Element[] { floor }, new Point2D(0, 0), 90, document).Redo();
        Assert.Equal(16000, SlabEdgeRuns.Length(document, edge), 6);

        // Copied with it, the copy on the copied floor.
        var copies = ElementCopy.Duplicate(document, new Element[] { floor });
        var copied = copies.OfType<SlabEdge>().Single();
        Assert.Equal(copies.OfType<Floor>().Single().Id, copied.FloorId);

        // Deleted with it.
        var delete = new DeleteElementsCommand(document, new Element[] { floor });
        delete.Redo();
        Assert.DoesNotContain(edge, document.Elements);
        delete.Undo();
        Assert.Contains(edge, document.Elements);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var saved = reopened.Elements.OfType<SlabEdge>().Single(candidate => candidate.Id == edge.Id);
        Assert.Equal(edge.Edges, saved.Edges);
        Assert.Equal(16000, SlabEdgeRuns.Length(reopened, saved), 6);

        using var model = IfcExport.Build(document);
        Assert.Contains(model.Instances.OfType<IfcBuildingElementProxy>(), proxy => proxy.Name == "Slab Edge - Edge Beam 300 x 600");
    }

    [Fact]
    public void TheSlabEdgeToolPicksAFloorsEdgesOneAfterAnother()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var floor = new Floor { LevelId = document.Levels[0].Id, TypeId = document.TypesOf<FloorType>().First().Id };
                floor.SetBoundary(Outline);
                document.Add(floor);
                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id, History = new UndoStack() };
                plan.SetTool(PlanTool.SlabEdge);

                Assert.True(plan.PickSlabEdgeAt(new Point2D(5000, 20)));
                Assert.True(plan.PickSlabEdgeAt(new Point2D(9980, 3000)));
                var edge = Assert.Single(document.Elements.OfType<SlabEdge>());
                Assert.Equal(2, edge.Edges.Count);

                // A side picked again comes off; Esc and a click start another.
                Assert.True(plan.PickSlabEdgeAt(new Point2D(9980, 3000)));
                Assert.Single(edge.Edges);
                plan.SetTool(PlanTool.Select);
                plan.SetTool(PlanTool.SlabEdge);
                Assert.True(plan.PickSlabEdgeAt(new Point2D(5000, 5980)));
                Assert.Equal(2, document.Elements.OfType<SlabEdge>().Count());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }
}
