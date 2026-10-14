using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Infrastructure.Serialization;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// What a stair is for, and where: Approved Document K's private, utility and general access
/// stairs, each with its own limits; a loft conversion's lower headroom; alternating treads and
/// a fixed ladder, allowed only there; a bullnose bottom step, shaped nosings and each part in
/// its own material; and a stair made to pass its rules in one go.
/// </summary>
public class StairUseTests
{
    [Fact]
    public void AStairIsCheckedForWhatItIsFor()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        Assert.Empty(Stairs.Problems(document, stair));

        // A utility stair: 190 rises at most, 250 goings at least; closed risers, handrails both
        // sides running on 300, 1000 between them, guarding 1100 where level.
        stair.Use = StairUse.Utility;
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("allows 190 at most on a utility stair"));
        Assert.Contains(problems, problem => problem.Contains("asks for 250 at least on a utility stair"));
        Assert.Contains(problems, problem => problem.Contains("run on 300"));
        Assert.Contains(problems, problem => problem.Contains("between handrails"));
        Assert.Contains(problems, problem => problem.Contains("Approved Document K asks for 1100"));

        // A general access stair: 170 at most, 12 risers to a flight.
        stair.Use = StairUse.GeneralAccess;
        problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("allows 170 at most on a general access stair"));
        Assert.Contains(problems, problem => problem.Contains("climbs 15 risers in a flight: Approved Document K allows 12"));

        // Open risers, and landings shorter than 1200, are not for a stair other than a house's.
        stair.Shape = StairShape.QuarterTurn;
        stair.TypeId = document.TypesOf<StairType>().First(type => type.OpenRisers).Id;
        problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("risers are open"));
        Assert.Contains(problems, problem => problem.Contains("landings are 900 long"));
    }

    [Fact]
    public void ALoftConversionMayHaveLessHeadroomOverTheMiddleOfItsStair()
    {
        var (document, floor) = House();
        var stair = Put(document, 3000, 600);
        var full = Polygon2D.Area(Assert.Single(Stairs.Holes(document, floor)));

        // Cut for 1900 over its middle, not 2000: a smaller hole.
        stair.LoftConversion = true;
        Assert.True(Polygon2D.Area(Assert.Single(Stairs.Holes(document, floor))) < full);
        Assert.Empty(Stairs.Problems(document, stair));

        // Not cut at all: too low over its middle, and at its sides.
        stair.CutsFloorAbove = false;
        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("in a loft conversion Approved Document K accepts 1900"));
        Assert.Contains(problems, problem => problem.Contains("accepts 1800 at the side"));
    }

    [Fact]
    public void AlternatingTreadsAndALadderAreForALoftConversionOnly()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600, width: 600);
        stair.Shape = StairShape.AlternatingTread;
        var layout = Stairs.Layout(document, stair);

        // Paddles half the stair wide, left and right in turn, one every half going.
        Assert.Equal(112.5, layout.Going, 6);
        Assert.Equal(300 * (225 + 20), Polygon2D.Area(layout.Steps[1].Outline), 0);
        var middles = layout.Steps.Take(2).Select(step => (Polygon2D.Centroid(step.Outline, stair.Start) - stair.Start).Dot(stair.Up.PerpendicularLeft())).ToList();
        Assert.True(middles[0] > 0 && middles[1] < 0);

        var problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("only in a loft conversion"));
        Assert.Contains(problems, problem => problem.Contains("handrail on both sides of alternating treads"));
        (stair.LoftConversion, stair.Railings) = (true, StairRailings.BothSides);
        Assert.Empty(Stairs.Problems(document, stair));

        // The IRC's alternating tread device: 5 in treads; handrails 30 to 34 in.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        problems = Stairs.Problems(document, stair);
        Assert.Contains(problems, problem => problem.Contains("127 (5 in)"));
        Assert.Contains(problems, problem => problem.Contains("762 to 864"));

        // A ship's ladder: 127 at least to a tread, 90 of nosing, so 8½ in with it.
        stair.Shape = StairShape.Ladder;
        layout = Stairs.Layout(document, stair);
        Assert.Equal((StairShape.Ladder, 130.0), (layout.Shape, layout.Going));
        Assert.DoesNotContain(Stairs.Problems(document, stair), problem => problem.Contains("127 (5 in)") || problem.Contains("216"));
    }

    [Fact]
    public void AlternatingTreadsAreMadeToPassTheIrcsOwnRulesForThem()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600, width: 600);
        (stair.Shape, stair.LoftConversion, stair.Railings) = (StairShape.AlternatingTread, true, StairRailings.BothSides);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;

        // Treads 5 in deep, 8½ in with their nosing, handrails 30 to 34 in - not an ordinary stair's nosing or handrail.
        var command = StairFixes.MakeItPass(document, stair, out var message);
        Assert.True(command is not null, message);
        command!.Redo();
        Assert.Empty(Stairs.Problems(document, stair));
        var type = Stairs.TypeOf(document, stair);
        Assert.Equal((840.0, 255.0, 90.0), (type.HandrailHeight, type.Going, type.Nosing));
    }

    [Fact]
    public void ItsBottomStepNosingsAndPartsAreShapedAndMadeAsTold()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        var plain = Polygon2D.Area(Stairs.Layout(document, stair).Steps[0].Outline);

        // A bullnose at both ends: a half round as deep as the tread past each side.
        stair.BottomStep = BottomStep.BullnoseBoth;
        var first = Stairs.Layout(document, stair).Steps[0];
        Assert.InRange(Polygon2D.Area(first.Outline), plain + Math.PI * 122.5 * 122.5 * 0.98, plain + Math.PI * 122.5 * 122.5);
        Assert.InRange(first.Outline.Min(point => point.X), 3000 - 450 - 123, 3000 - 450 - 121);

        // Rounded nosings: more to each tread than a square one.
        var type = Stairs.TypeOf(document, stair).Duplicate("Mine");
        document.AddType(type);
        stair.TypeId = type.Id;
        var square = StairModel.Meshes(document, stair).Sum(mesh => mesh.TriangleCount);
        type.NosingProfile = NosingProfile.Rounded;
        Assert.True(StairModel.Meshes(document, stair).Sum(mesh => mesh.TriangleCount) > square);
        type.NosingProfile = NosingProfile.Chamfered;
        Assert.True(StairModel.Meshes(document, stair).Sum(mesh => mesh.TriangleCount) > square);

        // Its risers in a material of their own.
        var oak = new Material("Oak, Dark") { SurfaceColour = new ColourRgb(0x5A, 0x3C, 0x22) };
        document.AddMaterial(oak);
        type.RiserMaterialId = oak.Id;
        Assert.Contains(StairModel.Meshes(document, stair), mesh => mesh.Description == "Oak, Dark");

        var back = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var saved = back.Elements.OfType<Stair>().Single();
        Assert.Equal((StairUse.Private, false, BottomStep.BullnoseBoth), (saved.Use, saved.LoftConversion, saved.BottomStep));
        var savedType = back.TypesOf<StairType>().Single(candidate => candidate.Name == "Mine");
        Assert.Equal((NosingProfile.Chamfered, oak.Id), (savedType.NosingProfile, savedType.RiserMaterialId));
    }

    [Fact]
    public void ItIsMadeToPassItsRulesInOneStepOnACopyOfItsType()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600, shape: StairShape.QuarterTurn, corner: StairCorner.Winders, width: 800);
        var other = Put(document, 1000, 600);
        var type = Stairs.TypeOf(document, stair);
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        Assert.Equal(5, Stairs.Problems(document, stair).Count);

        var history = new Core.Documents.Commands.UndoStack();
        var make = stair.GetInstanceParameters(document).Single(parameter => parameter.Definition == StairParameters.MakeItPass);
        var command = StairFixes.MakeItPass(document, stair, out var message);
        Assert.NotNull(command);
        Assert.StartsWith("It passes now:", message);
        history.Execute(command!);

        // Its type a copy given to it alone; the other stair on the original, as it was.
        Assert.Empty(Stairs.Problems(document, stair));
        Assert.NotEqual(type.Id, stair.TypeId);
        Assert.Equal(type.Id, other.TypeId);
        Assert.Equal((200.0, 225.0), (type.MaxRiser, type.Going));
        Assert.Equal((920.0, StairCorner.Landing), (stair.Width, stair.Corner));

        history.Undo();
        Assert.Equal((type.Id, 800.0, StairCorner.Winders), (stair.TypeId, stair.Width, stair.Corner));
        Assert.DoesNotContain(document.TypesOf<StairType>(), candidate => candidate.Name.EndsWith("(to pass)"));

        // Already passing, or nothing to be done: no change, and why.
        document.ProjectInformation.ClearanceStandard = ClearanceStandard.ApprovedDocumentM;
        Assert.Null(StairFixes.MakeItPass(document, other, out message));
        Assert.Contains("already", message);
        Assert.True(make.TrySet(true));
    }
}
