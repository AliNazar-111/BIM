using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// Every handle on every shape of stair, turning either way, follows the mouse: dragged a
/// little way along its line, it ends up where it was dragged to, and what should stay put does.
/// </summary>
public class StairGripSweepTests
{
    public static IEnumerable<object[]> Shapes()
    {
        foreach (var left in new[] { false, true })
        {
            yield return new object[] { StairShape.Straight, StairCorner.Landing, left };
            foreach (var corner in new[] { StairCorner.Landing, StairCorner.Winders, StairCorner.BalancedWinders })
            {
                yield return new object[] { StairShape.QuarterTurn, corner, left };
                yield return new object[] { StairShape.HalfTurn, corner, left };
            }

            yield return new object[] { StairShape.ThreeQuarterTurn, StairCorner.Landing, left };
            yield return new object[] { StairShape.ThreeQuarterTurn, StairCorner.Winders, left };

            yield return new object[] { StairShape.Spiral, StairCorner.Landing, left };
            yield return new object[] { StairShape.Curved, StairCorner.Landing, left };
        }

        yield return new object[] { StairShape.Bifurcated, StairCorner.Landing, false };
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EveryHandleFollowsTheMouse(StairShape shape, StairCorner corner, bool left)
    {
        foreach (var kind in Enum.GetValues<StairGripKind>().Where(kind => kind is not (StairGripKind.Flip or StairGripKind.Top or StairGripKind.Bottom)))
        {
            var (document, _) = House();
            var stair = Put(document, 3000, 1500, angle: 60, shape: shape, corner: corner, left: left, width: shape == StairShape.Spiral ? 800 : 900);
            if (StairGrips.Of(document, stair).FirstOrDefault(grip => grip.Kind == kind) is not { } grip) continue;
            var what = $"{shape} {corner} {(left ? "left" : "right")} {kind}";
            var layout = Stairs.Layout(document, stair);

            switch (kind)
            {
                case StairGripKind.WidthLeft or StairGripKind.WidthRight:
                {
                    var other = StairGrips.Of(document, stair).Single(g => g.Kind == (kind == StairGripKind.WidthLeft ? StairGripKind.WidthRight : StairGripKind.WidthLeft));
                    var target = grip.At + grip.Axis * 150;
                    StairGrips.Drag(document, stair, grip, target, 0);
                    var moved = StairGrips.Of(document, stair).Single(g => g.Kind == kind);
                    var stayed = StairGrips.Of(document, stair).Single(g => g.Kind == other.Kind);
                    Assert.True(Math.Abs((moved.At - target).Dot(grip.Axis)) < 11, $"{what}: the side went to {(moved.At - grip.At).Dot(grip.Axis):0}, not 150");
                    if (shape != StairShape.Bifurcated)
                        Assert.True(Math.Abs((stayed.At - other.At).Dot(grip.Axis)) < 1, $"{what}: the other side moved {(stayed.At - other.At).Dot(grip.Axis):0}");
                    break;
                }

                case StairGripKind.Turn:
                {
                    StairGrips.Drag(document, stair, grip, grip.At + grip.Axis * 225, 0);
                    var moved = StairGrips.Of(document, stair).Single(g => g.Kind == kind);
                    Assert.True(Math.Abs((moved.At - grip.At).Dot(grip.Axis) - 225) < 1, $"{what}: the turn went {(moved.At - grip.At).Dot(grip.Axis):0}, not 225");
                    break;
                }

                case StairGripKind.Well:
                {
                    StairGrips.Drag(document, stair, grip, grip.At + grip.Axis * 150, 0);
                    var moved = StairGrips.Of(document, stair).Single(g => g.Kind == kind);
                    // Across: a balanced half turn's second flight is shorter by what its wider well takes of the line walked.
                    Assert.True(Math.Abs((moved.At - grip.At).Dot(grip.Axis) - 150) < 11, $"{what}: the well's handle went {(moved.At - grip.At).Dot(grip.Axis):0}, not 150");
                    Assert.True(Math.Abs((moved.At - grip.At).Dot(grip.Axis.PerpendicularLeft())) < 80, $"{what}: the well's handle slid {(moved.At - grip.At).Dot(grip.Axis.PerpendicularLeft()):0} along the stair");
                    break;
                }

                case StairGripKind.Inside or StairGripKind.Outside:
                {
                    var centre = layout.Centre!.Value;
                    var target = grip.At + grip.Axis * 100;
                    StairGrips.Drag(document, stair, grip, target, 0);
                    var now = Stairs.Layout(document, stair);
                    var radius = kind == StairGripKind.Inside ? now.InnerRadius : now.OuterRadius;
                    Assert.True(Math.Abs(radius - (target - centre).Length) < 11, $"{what}: its radius is {radius:0}, not {(target - centre).Length:0}");
                    Assert.True((now.Centre!.Value - centre).Length < 1e-6, $"{what}: its centre moved");
                    break;
                }

                case StairGripKind.Rotate:
                {
                    var turned = 80 * Math.PI / 180;
                    StairGrips.Drag(document, stair, grip, stair.Start - new Vector2D(Math.Cos(turned), Math.Sin(turned)) * 400, 0);
                    Assert.True(Math.Abs(stair.Angle - 80) < 1e-6, $"{what}: it went up at {stair.Angle:0}°, not 80°");
                    break;
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ItsTopAndFootGoWhereTheyAreDragged(StairShape shape, StairCorner corner, bool left)
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 1500, angle: 60, shape: shape, corner: corner, left: left, width: shape == StairShape.Spiral ? 800 : 900);

        var top = StairGrips.Of(document, stair).Single(grip => grip.Kind == StairGripKind.Top);
        StairGrips.Drag(document, stair, top, top.At, top.Height + 300);
        Assert.Equal(top.Height + 300, Stairs.Layout(document, stair).Top, 6);
        Assert.Equal(top.Height + 300, StairGrips.Of(document, stair).Single(grip => grip.Kind == StairGripKind.Top).Height, 6);

        var foot = StairGrips.Of(document, stair).Single(grip => grip.Kind == StairGripKind.Bottom);
        StairGrips.Drag(document, stair, foot, foot.At, foot.Height + 150);
        Assert.Equal(foot.Height + 150, Stairs.Layout(document, stair).Bottom, 6);
    }
}
