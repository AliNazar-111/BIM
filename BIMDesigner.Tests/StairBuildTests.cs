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
