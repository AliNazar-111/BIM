using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Tests;

/// <summary>
/// A door opens only to a side it has room to: a tall one in a storefront under the eaves would
/// swing out up into the soffit and the roof's overhang, so it opens in - where the roof only
/// rises - and is not flipped to open out.
/// </summary>
public class DoorSwingTests
{
    /// <summary>
    /// An 8 m storefront under the eave of a roof overhanging it by 450 mm with a soffit, its
    /// third bay a full-height glass door; the storefront's outside faces the overhang.
    /// </summary>
    private static (BimDocument Document, Wall Wall, CurtainPanel Door, Roof Roof) Storefront(bool flipFacing = false)
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;

        var roof = new Roof { TypeId = document.TypesOf<RoofType>().First().Id, LevelId = level, HeightOffset = 2800 };
        roof.SetBoundary(new[] { new Point2D(-450, -450), new Point2D(8450, -450), new Point2D(8450, 6450), new Point2D(-450, 6450) });
        roof.SetEdges(new[]
        {
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30, Overhang = 450 }, new RoofEdge(),
            new RoofEdge { DefinesSlope = true, SlopeDegrees = 30, Overhang = 450 }, new RoofEdge()
        });
        document.Add(roof);

        var soffit = new Soffit { RoofId = roof.Id, LevelId = level, TypeId = document.TypesOf<SoffitType>().First().Id };
        soffit.EdgeIds.AddRange(RoofEdgeSweeps.SoffitEdges(document, roof));
        document.Add(soffit);

        // Drawn east to west, so its outside - its left - faces south, out under the eave.
        var wall = new Wall
        {
            Start = new Point2D(8000, 0), End = new Point2D(0, 0), LevelId = level, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<CurtainWallType>().First(t => t.Name.Contains("Storefront")).Id
        };
        document.Add(wall);

        var layout = CurtainLayout.Of(document, wall)!;
        var (from, to) = (layout.Verticals[2], layout.Verticals[3]);
        var door = document.TypesOf<DoorType>().First(t => t.Name.StartsWith("Curtain Wall Sgl"));
        wall.CurtainGrid = new CurtainGrid(layout.Verticals.Skip(1).SkipLast(1).ToList(), layout.Horizontals.Skip(1).SkipLast(1).ToList(),
            new[] { new CurtainSegment(false, layout.Horizontals[1], from, to) });
        wall.CurtainPanels = new[] { new CurtainPanelOverride(2, 0, CurtainPanelKind.Door, door.Id, FlipFacing: flipFacing) };

        return (document, wall, CurtainPanel.At(document, wall, 2, 0)!, roof);
    }

    [Fact]
    public void ATallDoorOpeningOutUnderTheEavesHitsTheSoffitAndOpeningInClearsIt()
    {
        var (document, wall, door, _) = Storefront();
        var cell = door.Cell(document)!;
        Assert.True(cell.ClearTop > 2800);

        Assert.Equal("the soffit", DoorSwing.Hits(document, wall, cell, flipFacing: false));
        Assert.Null(DoorSwing.Hits(document, wall, cell, flipFacing: true));
    }

    [Fact]
    public void OpenedItOpensInAndSaysWhy()
    {
        var (document, wall, door, _) = Storefront();
        var open = door.GetInstanceParameters(document).Single(p => p.Name == "Open");

        Assert.True(open.TrySet(true));
        Assert.Contains("soffit", open.TakeMessage());

        var cell = door.Cell(document)!;
        Assert.True(cell.IsOpen);
        Assert.True(cell.FlipFacing);
    }

    [Fact]
    public void ItIsNotFlippedToOpenOutIntoTheSoffit()
    {
        var (document, wall, door, _) = Storefront(flipFacing: true);
        var flip = door.GetInstanceParameters(document).Single(p => p.Name == "Flip Facing");

        Assert.False(flip.TrySet(false));
        Assert.Contains("soffit", flip.TakeMessage());
        Assert.True(door.Cell(document)!.FlipFacing);
    }

    [Fact]
    public void RefusedInPropertiesItIsAnErrorForTheWindowToShow()
    {
        var (document, _, door, _) = Storefront(flipFacing: true);
        var row = new BIMDesigner.UI.ViewModels.ParameterRow(door.GetInstanceParameters(document).Single(p => p.Name == "Flip Facing"));
        BIMDesigner.UI.ViewModels.ParameterExplainedEventArgs? said = null;
        row.Explained += (_, e) => said = e;

        row.Flag = false;

        Assert.NotNull(said);
        Assert.True(said!.Refused);
        Assert.Equal("Flip Facing", said.Parameter);
        Assert.Contains("soffit", said.Message);
    }

    [Fact]
    public void RefusedInThePlanItIsAnErrorForTheWindowToShow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (document, _, door, _) = Storefront(flipFacing: true);
                var plan = new BIMDesigner.UI.Controls.PlanView { Document = document, ActiveLevelId = document.Levels[0].Id };
                string? refused = null;
                plan.DoorRefused += (_, why) => refused = why;

                plan.FlipCurtainPanel(door, hand: false);

                Assert.Contains("soffit", refused);
                Assert.True(door.Cell(document)!.FlipFacing);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Exception("The plan failed on the UI thread.", failure);
    }

    [Fact]
    public void ADoorOfOrdinaryHeightClearsTheEavesEitherWay()
    {
        var (document, _, _, roof) = Storefront();
        var brick = new Wall
        {
            Start = new Point2D(8000, 6000), End = new Point2D(0, 6000), LevelId = document.Levels[0].Id, UnconnectedHeight = 3000,
            TypeId = document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior")).Id
        };
        document.Add(brick);

        var type = document.TypesOf<DoorType>().First(t => t.Height <= 2200 && t.Operation == DoorOperation.Swing);
        var door = new Door { HostWallId = brick.Id, LevelId = brick.LevelId, TypeId = type.Id, DistanceAlongWall = 4000 };
        document.Add(door);

        Assert.Null(DoorSwing.Hits(document, door, flipFacing: false));
        Assert.Null(DoorSwing.Hits(document, door, flipFacing: true));
    }
}
