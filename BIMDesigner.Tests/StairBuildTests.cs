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
/// How a stair is built and turns: balanced winders sharing a turn among the treads either side
/// of it; treads on housed or cut strings, a steel spine, steel stringers or built into a wall;
/// open risers barred; balusters or glass - each checked as Approved Document K or the IRC asks.
/// </summary>
public class StairBuildTests
{
    private static StairType TypeNamed(BimDocument document, string name) => document.TypesOf<StairType>().First(type => type.Name.Contains(name));

    [Fact]
    public void BalancedWindersShareTheTurnAmongTheTreadsEitherSideOfIt()
    {
        var (document, floor) = House();
        var stair = Put(document, 1500, 600, shape: StairShape.QuarterTurn, corner: StairCorner.BalancedWinders);
        var layout = Stairs.Layout(document, stair);

        // No newel: every tread the flight's going along the middle, round the turn as up the flights.
        Assert.Empty(layout.Newels);
        Assert.Equal(14, layout.Steps.Count);
        var walk = layout.WalkingLine;
        var lengths = walk.Zip(walk.Skip(1), (a, b) => (b - a).Length).ToList();
        Assert.Equal(14 * 225, lengths.Sum(), 6);
        Assert.All(layout.Steps, step =>
        {
            // Where its riser crosses the line walked: a whole number of goings along it.
            var along = 0.0;
            for (var i = 0; i + 1 < walk.Count; i++)
            {
                var (d, e, f) = (walk[i + 1] - walk[i], step.RiserTo - step.RiserFrom, step.RiserFrom - walk[i]);
                var t = f.Cross(e) / d.Cross(e);
                if (Math.Abs(d.Cross(e)) > 1e-9 && t >= -1e-9 && t <= 1 + 1e-9 && Math.Abs(f.Cross(d) / d.Cross(e) - 0.5) <= 0.5 + 1e-6)
                {
                    Assert.Equal((step.Number - 1) * 225.0, along + t * lengths[i], 3);
                    return;
                }

                along += lengths[i];
            }

            Assert.Fail($"Step {step.Number}'s riser does not cross the line walked.");
        });

        // The turn's taper shared among nine treads, none under 75 at its inside end; Approved Document K met.
        Assert.Equal(9, layout.Steps.Count(step => step.Kind == StairStepKind.Winder));
        Assert.InRange(layout.WinderNarrowEnd!.Value, 75, 225);
        Assert.Equal(225, layout.WinderGoing!.Value, 6);
        Assert.Empty(Stairs.Problems(document, stair));
        Assert.Single(Stairs.Holes(document, floor));

        // A half turn round a 100 well shares its two corners among all its treads.
        stair.Shape = StairShape.HalfTurn;
        layout = Stairs.Layout(document, stair);
        Assert.Empty(layout.Newels);
        Assert.InRange(layout.WinderNarrowEnd!.Value, 50, 75);
        Assert.Empty(Stairs.Problems(document, stair));

        // Too few treads either side of the turn to share it: winders round a newel instead.
        stair.TopLevelId = null;
        stair.UnconnectedHeight = 1000;
        stair.Shape = StairShape.QuarterTurn;
        Assert.Single(Stairs.Layout(document, stair).Newels);

        using var model = IfcExport.Build(document);
        Assert.Equal(IfcStairTypeEnum.QUARTER_WINDING_STAIR, Assert.Single(model.Instances.OfType<IfcStair>()).PredefinedType);
    }

    [Fact]
    public void UnderTheIrcItsHandrailsLeaveItClearEnoughAndStandOutLittle()
    {
        var (document, _) = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Irc;
        var half = document.TypesOf<WallType>().First().Structure.TotalWidth / 2;
        var stair = Put(document, half + 457, 600, width: 914);
        stair.Railings = StairRailings.BothSides;
        var layout = Stairs.Layout(document, stair);

        // Rails both sides, one on the wall, 60 off it and 44 round: 104 out, leaving 914 less that and the balustrade's rail.
        Assert.Contains(StairModel.Railings(document, stair, layout), railing => railing.OnWall);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("R311.7.1"));

        // 100 off the wall: 144 out, more than the 4½ in allowed - though it still leaves 748, more than the 27 in asked.
        Stairs.TypeOf(document, stair).WallClearance = 100;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("stand 144 out from the wall"));
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("clear at its handrails"));

        // Narrowed to 800, a handrail on the wall side only, it leaves 696 at it: short of 31½ in.
        (stair.Width, stair.Railings) = (800, StairRailings.LeftSide);
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("clear at its handrails: the IRC asks for 800"));
    }

    [Fact]
    public void UnderTheAdaAStairOutsideAHomeKeepsToItsRisesTreadsAndHandrails()
    {
        var (document, _) = House();
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        var stair = Put(document, 3000, 600);
        stair.Use = StairUse.GeneralAccess;

        // 15 risers of 200, goings of 225, 900 wide: too steep, too shallow and too narrow for the ADA and the IBC.
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("the ADA allows 178 (7 in) at most (504.2)"));
        Assert.Contains(problems, problem => problem.Contains("the ADA asks for 279 (11 in) at least (504.2)"));
        Assert.Contains(problems, problem => problem.Contains("the IBC asks for 1118 (44 in)"));
        Assert.DoesNotContain(problems, problem => problem.Contains("the IRC"));

        // Made to pass: rises of 7 in at most, 11 in treads, 44 in wide, handrails both sides running on past its ends.
        StairFixes.MakeItPass(document, stair, out _)!.Redo();
        var layout = Stairs.Layout(document, stair);
        Assert.True(layout.Rise <= 178 + 0.5 && layout.Going >= 279 - 0.5 && stair.Width >= 1118 - 0.5, $"{layout.Rise:0} by {layout.Going:0}, {stair.Width:0} wide");
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("(504.2)") || problem.Contains("(505.") || problem.Contains("1118"));

        // Its guarding 42 in, as the IBC asks outside a home.
        Assert.True(Stairs.TypeOf(document, stair).GuardHeight >= 1067 - 0.5);

        // A home's stair stays under the IRC.
        stair.Use = StairUse.Private;
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("the ADA"));
    }

    [Fact]
    public void OpenRisersNeedABarAndBalustersCloseEnoughToStopAChildsHead()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        var open = TypeNamed(document, "Timber, Open Risers");
        stair.TypeId = open.Id;

        // Barred halfway, the 170 between its treads is two gaps of 73.
        Assert.Empty(Stairs.Problems(document, stair));
        open.RiserBars = false;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("leave 170 between the treads: Approved Document K asks for less than 100"));
        open.Nosing = 10;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("overlap by 10"));

        // The IRC's sphere is 4 in; its closed-riser stairs want nosings of ¾ to 1¼ in.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("less than 102 (4 in)"));

        // Only where a gap is more than 30 in up: three steps down to a sunken room may be open.
        var low = Put(document, 1000, 600);
        (low.TypeId, low.TopLevelId, low.UnconnectedHeight) = (open.Id, null, 510);
        Assert.Equal(3, Stairs.Layout(document, low).Risers);
        Assert.DoesNotContain(Stairs.Problems(document, low), problem => problem.Contains("between the treads"));
        document.Remove(low);
        stair.TypeId = TypeNamed(document, "Concrete").Id;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("Its nosings are 0"));

        // Balusters 140 apart leave 108 between: too wide for Approved Document K, not for the IRC's 4⅜ in.
        var closed = TypeNamed(document, "Closed Risers");
        closed.BalusterSpacing = 140;
        stair.TypeId = closed.Id;
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("balusters"));
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("Its balusters are 108 apart"));
    }

    [Fact]
    public void ItsTreadsAreCarriedAsItsTypeSays()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        var layout = Stairs.Layout(document, stair);
        var middle = layout.Steps[6];

        (IReadOnlyList<Mesh3D> Body, IReadOnlyList<Mesh3D> Rails) Built(string name)
        {
            stair.TypeId = TypeNamed(document, name).Id;
            return StairModel.Build(document, stair);
        }

        // A cut string: its top level under each tread, the tread sitting on it.
        var cut = Built("Cut String");
        Assert.Contains(cut.Body.SelectMany(mesh => mesh.Positions), point => Math.Abs(point.X - (2550 + 32)) < 0.5 && Math.Abs(point.Z - (middle.Top - 30)) < 0.5);
        var housed = Built("Closed Risers");
        Assert.DoesNotContain(housed.Body.SelectMany(mesh => mesh.Positions), point => Math.Abs(point.X - (2550 + 32)) < 0.5 && Math.Abs(point.Z - (middle.Top - 30)) < 0.5);

        // A steel spine up the middle, under its treads.
        var spine = Built("Steel Spine").Body.Single(mesh => mesh.Description == "Steel, Painted Dark Grey").Bounds()!.Value;
        Assert.InRange(spine.Min.X, 3000 - 81, 3000);
        Assert.InRange(spine.Max.X, 3000, 3000 + 81);
        Assert.True(spine.Max.Z < layout.Top - 225);

        // Steel plates either side.
        var plates = Built("Steel Stringers").Body.Where(mesh => mesh.Description == "Steel, Painted Dark Grey").Select(mesh => mesh.Bounds()!.Value).ToList();
        Assert.Equal((2550.0, 3450.0), (Math.Round(plates.Min(box => box.Min.X)), Math.Round(plates.Max(box => box.Max.X))));

        // Cantilevered treads, in the middle of the room, have no wall to be built into; along one, they do.
        var floating = Built("Floating");
        Assert.Contains(Stairs.Problems(document, stair), problem => problem.Contains("cantilevered"));
        stair.Start = new Point2D(5500, 600);
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("cantilevered"));

        // Its glass balustrade is seen through, with no balusters.
        var glass = floating.Rails.Where(mesh => mesh.Description == "Glass, Toughened").ToList();
        Assert.NotEmpty(glass);
        Assert.All(glass, pane => Assert.True(pane.Opacity < 1));
        Assert.DoesNotContain(floating.Rails, mesh => mesh.Description == "Paint, White");
        Assert.True(floating.Rails.Concat(floating.Body).Min(mesh => mesh.Bounds()!.Value.Min.Z) >= layout.Bottom - 0.5);
    }

    [Fact]
    public void HowItIsBuiltIsSavedAndExported()
    {
        var (document, _) = House();
        var type = TypeNamed(document, "Closed Risers").Duplicate("Stair - Mine");
        (type.Support, type.Balustrade, type.RiserBars) = (StairSupport.CutStrings, StairBalustrade.Glass, true);
        document.AddType(type);
        var stair = Put(document, 3000, 600, shape: StairShape.HalfTurn, corner: StairCorner.BalancedWinders);
        stair.TypeId = type.Id;

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var back = loaded.TypesOf<StairType>().Single(candidate => candidate.Name == "Stair - Mine");
        Assert.Equal((StairSupport.CutStrings, StairBalustrade.Glass, true), (back.Support, back.Balustrade, back.RiserBars));
        Assert.Equal(StairCorner.BalancedWinders, loaded.Elements.OfType<Stair>().Single().Corner);

        using var model = IfcExport.Build(document);
        var exported = Assert.Single(model.Instances.OfType<IfcStair>());
        Assert.Equal(IfcStairTypeEnum.HALF_WINDING_STAIR, exported.PredefinedType);
        var support = model.Instances.OfType<Xbim.Ifc4.Kernel.IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(exported))
            .Select(relation => relation.RelatingPropertyDefinition).OfType<Xbim.Ifc4.Kernel.IfcPropertySet>()
            .Single(set => set.Name == "Stair")
            .HasProperties.OfType<Xbim.Ifc4.PropertyResource.IfcPropertySingleValue>().Single(property => property.Name == "Support");
        Assert.Equal("Cut Strings", support.NominalValue!.ToString());
    }
}
