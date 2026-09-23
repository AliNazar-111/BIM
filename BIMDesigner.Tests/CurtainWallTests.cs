using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.SharedBldgElements;

namespace BIMDesigner.Tests;

/// <summary>Curtain walls: their grid, panels and mullions, and every view of them.</summary>
public class CurtainWallTests
{
    /// <summary>A 6 m by 3 m storefront along the X axis, exterior toward +Y.</summary>
    private static (BimDocument Document, CurtainWallType Type, Wall Wall) Storefront()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<CurtainWallType>().Single(t => t.Name.Contains("Storefront"));
        var wall = new Wall
        {
            Start = new Point2D(0, 0), End = new Point2D(6000, 0),
            TypeId = type.Id, LevelId = document.Levels[0].Id, UnconnectedHeight = 3000
        };
        document.Add(wall);
        return (document, type, wall);
    }

    [Fact]
    public void AStorefrontIsAGridOfPanelsInMullions()
    {
        var (document, _, wall) = Storefront();
        var layout = CurtainLayout.Of(document, wall)!;

        // Every 1500 mm along, centred - which fits 6 m exactly - and every 1500 mm up.
        Assert.Equal(new[] { 0.0, 1500, 3000, 4500, 6000 }, layout.Verticals);
        Assert.Equal(new[] { 0.0, 1500, 3000 }, layout.Horizontals);
        Assert.Equal(8, layout.Cells.Count);

        // Five full-height mullions, and a transom across each bay at the bottom, middle and top.
        Assert.Equal(5, layout.Mullions.Count(m => m.IsVertical));
        Assert.Equal(12, layout.Mullions.Count(m => !m.IsVertical));

        // The first panel fills the clear space inside its frame: a border mullion 50 wide
        // inside the edge, and half of an interior one.
        var first = layout.Cells.Single(c => c.Column == 0 && c.Row == 0);
        Assert.Equal((50.0, 1475.0, 50.0, 1475.0), (first.ClearFrom, first.ClearTo, first.ClearBottom, first.ClearTop));
    }

    [Theory]
    [InlineData(CurtainGridLayout.FixedNumber, 5, 1000, new[] { 1400.0, 2800, 4200, 5600 })]
    [InlineData(CurtainGridLayout.MaximumSpacing, 0, 2000, new[] { 1750.0, 3500, 5250 })]
    [InlineData(CurtainGridLayout.None, 0, 0, new double[0])]
    public void GridsAreSetOutByTheirRule(CurtainGridLayout rule, int count, double spacing, double[] expected)
    {
        var type = new CurtainWallType("test") { VerticalLayout = rule, VerticalCount = count, VerticalSpacing = spacing };
        Assert.Equal(expected, CurtainLayout.TypeLines(type, vertical: true, extent: 7000));
    }

    [Fact]
    public void FixedDistanceLeavesTheOddPanelWhereItIsJustified()
    {
        var type = new CurtainWallType("test") { VerticalSpacing = 2000 };

        type.VerticalJustification = CurtainGridJustification.Beginning;
        Assert.Equal(new[] { 2000.0, 4000, 6000 }, CurtainLayout.TypeLines(type, true, 7000).Where(x => x > 0 && x < 7000));

        type.VerticalJustification = CurtainGridJustification.End;
        Assert.Equal(new[] { 1000.0, 3000, 5000 }, CurtainLayout.TypeLines(type, true, 7000).Where(x => x > 0 && x < 7000));

        type.VerticalJustification = CurtainGridJustification.Centre;
        Assert.Equal(new[] { 500.0, 2500, 4500, 6500 }, CurtainLayout.TypeLines(type, true, 7000).Where(x => x > 0 && x < 7000));
    }

    [Fact]
    public void ADoorPanelHasNoSillAndPanelsCanBeSolidOrEmpty()
    {
        var (document, _, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[]
        {
            new CurtainPanelOverride(1, 0, CurtainPanelKind.Door),
            new CurtainPanelOverride(2, 1, CurtainPanelKind.Solid),
            new CurtainPanelOverride(3, 1, CurtainPanelKind.Empty)
        }).Redo();

        var layout = CurtainLayout.Of(document, wall)!;
        var door = layout.Cells.Single(c => c.Column == 1 && c.Row == 0);
        Assert.Equal(CurtainPanelKind.Door, door.Kind);
        Assert.Equal(0, door.ClearBottom);

        // No transom along the floor under the door; the other bays keep theirs.
        Assert.DoesNotContain(layout.Mullions, m => !m.IsVertical && m.Bottom == 0 && m.From > 1500 && m.To < 3000);
        Assert.Equal(3, layout.Mullions.Count(m => !m.IsVertical && m.Bottom == 0));
        Assert.Equal(7, layout.PanelCount);
    }

    [Fact]
    public void ItsOwnGridReplacesTheTypesAndIsUndoable()
    {
        var (document, _, wall) = Storefront();
        var edit = new SetCurtainLayoutCommand(wall, new CurtainGrid(new[] { 2000.0, 4000 }, new[] { 900.0, 2100 }), null);

        edit.Redo();
        var layout = CurtainLayout.Of(document, wall)!;
        Assert.Equal(new[] { 0.0, 2000, 4000, 6000 }, layout.Verticals);
        Assert.Equal(new[] { 0.0, 900, 2100, 3000 }, layout.Horizontals);

        edit.Undo();
        Assert.Equal(5, CurtainLayout.Of(document, wall)!.Verticals.Count);
    }

    [Fact]
    public void ForJoinsAndRoomsItIsAThinWallAsDeepAsItsMullions()
    {
        var (document, type, wall) = Storefront();
        var body = document.GetWallType(wall)!;

        Assert.Equal(150, body.Width, precision: 9);
        Assert.Equal(type.Id, body.Id);
        Assert.Same(body, document.GetWallType(wall));
        Assert.True(document.IsCurtainWall(wall));
    }

    [Fact]
    public void In3DItIsGlassAndAFrame()
    {
        var (document, _, wall) = Storefront();
        var meshes = ModelMeshBuilder.BuildWall(document, wall);

        // Each panel is its own mesh now, so the glazing is all of them together.
        var glass = ModelMeshBuilder.Bounds(meshes.Where(m => m.Kind == MeshKind.Glazing))!.Value;
        Assert.Equal(50, glass.Min.X, precision: 6);
        Assert.Equal(5950, glass.Max.X, precision: 6);
        Assert.Equal(2950, glass.Max.Z, precision: 6);
        Assert.Equal(12.5, glass.Max.Y, precision: 6);

        var frame = meshes.Single(m => m.Kind == MeshKind.Mullion).Bounds()!.Value;
        Assert.Equal(-75, frame.Min.Y, precision: 6);
        Assert.Equal(75, frame.Max.Y, precision: 6);
        Assert.Equal(3000, frame.Max.Z, precision: 6);
        Assert.DoesNotContain(meshes, m => m.Kind == MeshKind.Wall);
    }

    [Fact]
    public void RoundMullionsAndCurvedWallsBuild()
    {
        var (document, type, wall) = Storefront();
        type.MullionProfile = MullionProfile.Circular;
        type.MullionWidth = 80;
        wall.Bulge = 0.5;

        var meshes = ModelMeshBuilder.BuildWall(document, wall);
        Assert.NotEmpty(meshes.Single(m => m.Kind == MeshKind.Mullion).Positions);

        // Every pane lies on the curve, within half its thickness of the wall's line.
        var curve = wall.LocationCurve;
        Assert.All(meshes.Where(m => m.Kind == MeshKind.Glazing).SelectMany(m => m.Positions),
            p => Assert.True(curve.DistanceTo(new Point2D(p.X, p.Y)) <= 12.5 + 0.6, $"{p} is off the curve."));
    }

    [Fact]
    public void ASectionCutsPanelsAndTransomsOrAMullion()
    {
        var (document, _, wall) = Storefront();

        var throughBay = new SectionMarker { Name = "A", Start = new Point2D(750, -2000), End = new Point2D(750, 2000), LevelId = wall.LevelId };
        var throughMullion = new SectionMarker { Name = "B", Start = new Point2D(1500, -2000), End = new Point2D(1500, 2000), LevelId = wall.LevelId };
        var facing = new SectionMarker { Name = "C", Start = new Point2D(-500, -3000), End = new Point2D(6500, -3000), LevelId = wall.LevelId };
        foreach (var marker in new[] { throughBay, throughMullion, facing }) document.Add(marker);

        var bay = SectionProjection.Build(document, throughBay).Pieces.Where(p => p.ElementId == wall.Id).ToList();
        Assert.Equal(2, bay.Count(p => p.Part == SectionPart.Glazing));
        Assert.Equal(3, bay.Count(p => p.Part == SectionPart.Frame));
        Assert.All(bay, p => Assert.Equal(SectionDepth.Cut, p.Depth));

        var mullion = Assert.Single(SectionProjection.Build(document, throughMullion).Pieces, p => p.ElementId == wall.Id);
        Assert.Equal(SectionPart.Frame, mullion.Part);
        Assert.Equal(3000, mullion.Bounds.Top - mullion.Bounds.Bottom, precision: 6);

        var seen = SectionProjection.Build(document, facing).Pieces.Where(p => p.ElementId == wall.Id).ToList();
        Assert.Equal(8, seen.Count(p => p.Part == SectionPart.Glazing));
        Assert.Equal(17, seen.Count(p => p.Part == SectionPart.Frame));
    }

    [Fact]
    public void ItsQuantitiesAreItsPanelsAndMullions()
    {
        var (document, type, wall) = Storefront();
        var layout = CurtainLayout.Of(document, wall)!;

        Assert.Equal(8, layout.PanelCount);
        var mullionLength = 5 * 3000 + layout.Mullions.Where(m => !m.IsVertical).Sum(m => m.To - m.From);
        Assert.Equal(mullionLength, layout.MullionLength, precision: 6);

        var panels = layout.Cells.Sum(c => (c.ClearTo - c.ClearFrom) * (c.ClearTop - c.ClearBottom)) * type.PanelThickness;
        Assert.Equal(panels + mullionLength * 50 * 150, wall.GetVolume(document), precision: 1);
    }

    [Fact]
    public void SplittingKeepsEveryLineAndPanelWhereItWas()
    {
        var (document, _, wall) = Storefront();
        new SetCurtainLayoutCommand(wall, null, new[] { new CurtainPanelOverride(3, 0, CurtainPanelKind.Door) }).Redo();

        var split = new SplitWallCommand(document, wall, new Point2D(3000, 0));
        split.Redo();

        Assert.Equal(new[] { 0.0, 1500, 3000 }, CurtainLayout.Of(document, wall)!.Verticals);
        var far = CurtainLayout.Of(document, split.Remainder)!;
        Assert.Equal(new[] { 0.0, 1500, 3000 }, far.Verticals);
        Assert.Equal(CurtainPanelKind.Door, far.Cells.Single(c => c.Column == 1 && c.Row == 0).Kind);

        split.Undo();
        Assert.Null(wall.CurtainGrid);
        Assert.Equal(5, CurtainLayout.Of(document, wall)!.Verticals.Count);
    }

    [Fact]
    public void EditingTheTypeIsOneUndoableStep()
    {
        var (document, type, wall) = Storefront();
        var design = type.Duplicate(type.Name);
        design.VerticalSpacing = 1000;
        design.MullionProfile = MullionProfile.Circular;

        var edit = new EditCurtainWallTypeCommand(type, design);
        edit.Redo();
        Assert.Equal(7, CurtainLayout.Of(document, wall)!.Verticals.Count);
        Assert.Equal(MullionProfile.Circular, type.MullionProfile);

        edit.Undo();
        Assert.Equal(1500, type.VerticalSpacing);
        Assert.Equal(MullionProfile.Rectangular, type.MullionProfile);

        Assert.Null(type.Problem());
        Assert.NotNull(type.Duplicate("x") is var bad && (bad.VerticalSpacing = 10) > 0 ? bad.Problem() : null);
    }

    [Fact]
    public void TypesAndWallsAreSaved()
    {
        var (document, type, wall) = Storefront();
        type.MullionProfile = MullionProfile.Circular;
        type.HorizontalLayout = CurtainGridLayout.FixedNumber;
        type.HorizontalCount = 3;
        new SetCurtainLayoutCommand(wall, new CurtainGrid(new[] { 2500.0 }, new[] { 1000.0 }),
            new[] { new CurtainPanelOverride(0, 0, CurtainPanelKind.Solid) }).Redo();

        var path = Path.Combine(Path.GetTempPath(), $"bimtest-{Guid.NewGuid():N}{ProjectFile.Extension}");
        try
        {
            ProjectFile.Save(document, path);
            var reloaded = ProjectFile.Load(path);

            var again = reloaded.FindType<CurtainWallType>(type.Id)!;
            Assert.Equal(MullionProfile.Circular, again.MullionProfile);
            Assert.Equal(CurtainGridLayout.FixedNumber, again.HorizontalLayout);
            Assert.Equal(3, again.HorizontalCount);

            var againWall = reloaded.Walls.Single(w => w.Id == wall.Id);
            Assert.Equal(new[] { 2500.0 }, againWall.CurtainGrid!.Verticals);
            Assert.Equal(CurtainPanelKind.Solid, Assert.Single(againWall.CurtainPanels!).Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnOlderProjectIsGivenCurtainWallTypes()
    {
        var document = BimDocument.CreateDefault();
        foreach (var curtain in document.TypesOf<CurtainWallType>().ToList()) document.RemoveType(curtain);

        document.EnsureDefaultTypes();

        var added = document.TypesOf<CurtainWallType>().ToList();
        Assert.Equal(2, added.Count);
        Assert.All(added, t => Assert.NotNull(document.FindMaterial(t.GlassMaterialId)));
    }

    [Fact]
    public void ItExportsAsACurtainWallOfPlatesAndMembers()
    {
        var (document, _, _) = Storefront();

        using var model = IfcExport.Build(document);

        Assert.Single(model.Instances.OfType<IfcCurtainWall>());
        Assert.Single(model.Instances.OfType<IfcPlate>());
        Assert.Single(model.Instances.OfType<IfcMember>());
        Assert.Empty(model.Instances.OfType<IfcWall>());
    }
}
