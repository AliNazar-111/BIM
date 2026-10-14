using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// A stair's handrails and guarding as they are built: round or rectangular, on brackets clear
/// of a wall, running on past its top and bottom, their ends returned to the wall or the floor
/// or stopped on a post, a second rail for children, guarding at its own height where it is
/// level - and checked for a hand's grip, its clearance from the wall, its ends and its height.
/// </summary>
public class StairRailingTests
{
    /// <summary>A stair up the inside of the west wall, a handrail on the wall and a balustrade on its open side.</summary>
    private static (BimDocument Document, Stair Stair, StairType Type) AgainstAWall()
    {
        var (document, _) = House();
        var face = document.GetWallType(document.Walls.First())!.Structure.TotalWidth / 2;
        var stair = Put(document, face + 450, 600);
        stair.Railings = StairRailings.BothSides;
        var type = Stairs.TypeOf(document, stair).Duplicate("Mine");
        document.AddType(type);
        stair.TypeId = type.Id;
        return (document, stair, type);
    }

    private static IEnumerable<Point3D> Points(IEnumerable<Mesh3D> meshes) => meshes.SelectMany(mesh => mesh.Positions);

    [Fact]
    public void ItsHandrailOnTheWallIsClearOfItOnBracketsRunningOnAndReturnedAtItsEnds()
    {
        var (document, stair, type) = AgainstAWall();
        var layout = Stairs.Layout(document, stair);
        var wallFace = stair.Start.X - 450;

        // A round 44 handrail, 60 off the wall: its line 82 out from the wall, on steel brackets.
        var parts = StairModel.Parts(document, stair);
        Assert.NotEmpty(parts.Handrails);
        Assert.NotEmpty(parts.Balustrades);
        Assert.Contains(parts.Handrails, mesh => mesh.Description == "Stainless Steel");
        var rail = Points(parts.Handrails.Where(mesh => mesh.Description != "Stainless Steel")).ToList();
        Assert.Contains(rail, point => Math.Abs(point.X - wallFace) < 1);
        Assert.InRange(rail.Max(point => point.X), wallFace + 60 + 44 - 1, wallFace + 60 + 44 + 1);

        // Run on 300 level past its foot and its head.
        type.HandrailExtension = 300;
        var wallRail = StairModel.Railings(document, stair, layout).Single(railing => railing.OnWall);
        Assert.Equal(600 - 300, wallRail.Points[0].At.Y, 6);
        Assert.Equal(layout.TopEdge.From.Y + 300, wallRail.Points[^1].At.Y, 6);
        Assert.Equal(wallRail.Points[0].Height, wallRail.Points[1].Height, 6);

        // Turned down to the floor at its ends; with a second rail at 600 for children.
        type.HandrailEnds = HandrailEnds.ReturnToFloor;
        type.LowerRail = true;
        rail = Points(StairModel.Parts(document, stair).Handrails.Where(mesh => mesh.Description != "Stainless Steel")).ToList();
        Assert.Equal(layout.Bottom, rail.Min(point => point.Z), 0);
        Assert.Contains(rail, point => Math.Abs(point.Z - (layout.Bottom + 200 + 600 - 22)) < 25 && point.Y < 700);

        // Rectangular, 50 by 39: a strip on edge.
        type.HandrailProfile = HandrailProfile.Rectangular;
        (type.HandrailWidth, type.HandrailDepth) = (50, 39);
        Assert.NotEmpty(StairModel.Parts(document, stair).Handrails);
    }

    [Fact]
    public void ItsGuardingStandsAtItsOwnHeightWhereItIsLevel()
    {
        var (document, floor) = House();
        var stair = Put(document, 3000, 600);
        var layout = Stairs.Layout(document, stair);
        var type = Stairs.TypeOf(document, stair);

        // Round the stairwell on the floor above: 920 over the floor - its handrail 900 over the pitch line.
        var guarding = StairModel.Parts(document, stair).Guarding;
        Assert.Contains(Points(guarding), point => Math.Abs(point.Z - (floor.GetTopElevation(document) + type.GuardHeight)) < 0.5);
        Assert.DoesNotContain(Points(guarding), point => point.Z > floor.GetTopElevation(document) + type.GuardHeight + 0.5 && point.Z < floor.GetTopElevation(document) + type.GuardHeight + 99);
        var first = StairModel.Railings(document, stair, layout).First();
        Assert.All(StairModel.BalustersAlong(first.Points[0], first.Points[1], first.LeftSide, type, layout),
            baluster => Assert.Equal(type.HandrailHeight - type.HandrailWidth, baluster.Top - (layout.Bottom + 200 + (baluster.At - stair.Start).Dot(stair.Up) / 225 * 200), 0));
    }

    [Fact]
    public void ItsHandrailsAreCheckedForAGripTheirClearanceTheirEndsAndTheirHeight()
    {
        var (document, stair, type) = AgainstAWall();
        Assert.Empty(Stairs.Problems(document, stair));

        // Approved Document K: round, 32 to 50 across; or no more than 50 by 39; 50 to 75 off the wall; guarding 900.
        type.HandrailWidth = 60;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("Approved Document K asks for 32 to 50"));
        (type.HandrailProfile, type.HandrailWidth, type.HandrailDepth) = (HandrailProfile.Rectangular, 60, 45);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("no more than 50 wide and 39 deep"));
        (type.HandrailProfile, type.HandrailWidth) = (HandrailProfile.Round, 44);
        type.WallClearance = 40;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("40 off the wall: Approved Document K asks for 50 to 75"));
        type.WallClearance = 60;
        type.GuardHeight = 850;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("guarding is 850 high"));
        type.GuardHeight = 920;

        // The IRC: a rectangular rail 4 to 6¼ in round; its ends returned or on a post; guarding 36 in.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        (type.HandrailProfile, type.HandrailWidth, type.HandrailDepth) = (HandrailProfile.Rectangular, 50, 39);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("178 round"));
        type.HandrailProfile = HandrailProfile.Round;
        type.HandrailEnds = HandrailEnds.Cut;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("ends are cut off"));
        type.HandrailEnds = HandrailEnds.ReturnToWall;
        type.GuardHeight = 900;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("the IRC asks for 36 in (914)"));
    }

    [Fact]
    public void ItsRailingsAreSavedAndExportedAsHandrailsBalustradesAndGuarding()
    {
        var (document, stair, type) = AgainstAWall();
        (type.HandrailProfile, type.HandrailWidth, type.HandrailDepth, type.WallClearance) = (HandrailProfile.Rectangular, 48, 36, 55);
        (type.HandrailExtension, type.HandrailEnds, type.GuardHeight, type.LowerRail) = (300, HandrailEnds.Post, 1000, true);

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).TypesOf<StairType>().Single(candidate => candidate.Name == "Mine");
        Assert.Equal((HandrailProfile.Rectangular, 48.0, 36.0, 55.0), (back.HandrailProfile, back.HandrailWidth, back.HandrailDepth, back.WallClearance));
        Assert.Equal((300.0, HandrailEnds.Post, 1000.0, true), (back.HandrailExtension, back.HandrailEnds, back.GuardHeight, back.LowerRail));
        var copy = type.Duplicate("Copy");
        Assert.Equal((type.HandrailEnds, type.GuardHeight, type.LowerRail), (copy.HandrailEnds, copy.GuardHeight, copy.LowerRail));

        using var model = IfcExport.Build(document);
        var railings = model.Instances.OfType<IfcRailing>().Select(railing => railing.PredefinedType).ToList();
        Assert.Contains(IfcRailingTypeEnum.HANDRAIL, railings);
        Assert.Contains(IfcRailingTypeEnum.BALUSTRADE, railings);
        Assert.Contains(IfcRailingTypeEnum.GUARDRAIL, railings);
    }
}
