using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Outside the building: trees and shrubs near it, and parking bays.
///
/// A tree draws water from round it, and a clay soil it dries shrinks under a foundation: NHBC
/// Standards 4.2 put a tree's zone of influence at 1.25 times its mature height for a high water
/// demand species, 0.75 for a moderate one and 0.5 for a low one (Table 3) - foundations within it
/// go deeper (Tables 4 and 5) - and shrubs over 1.8 m at 0.75 times theirs (Table 6). A tree kept
/// on the site has its roots kept clear of the building: BS 5837's root protection area, 12 times
/// its stem's diameter round it, 15 m at most - in the US, the critical root zone, 1 ft for each
/// inch of trunk.
///
/// A parking bay: Approved Document M's 2.4 x 4.8, level or gently sloping - 1 in 20 at most -
/// capable of widening to 3.3 m (M4(2) 2.12); an accessible one 1.2 m wider and longer, to one
/// side and behind, and level - 1 in 60 (M4(3) 3.12); in the US an accessible space 96 in with a
/// 60 in aisle, 1 in 48 at most (ADA 502).
/// </summary>
public static class Outside
{
    /// <summary>How many times its mature height round a tree a clay soil is dried, by how much water it draws (NHBC Table 3).</summary>
    public static double ZoneFactor(TreeWaterDemand demand) => demand switch
    {
        TreeWaterDemand.Low => 0.5,
        TreeWaterDemand.Moderate => 0.75,
        _ => 1.25
    };

    /// <summary>A tree's root protection area's radius: 12 times its stem's diameter, 15 m at most; none where its stem is not known.</summary>
    public static double RootProtection(ComponentType type) => Math.Min(12 * type.StemDiameter, 15000);

    /// <summary>How far a point is from the nearest wall on a storey, its face; null where there is none.</summary>
    public static double? FromBuilding(BimDocument document, Guid levelId, Point2D at) =>
        document.Walls
            .Where(wall => wall.LevelId == levelId && document.GetWallType(wall) is { } type && type.Structure.TotalWidth > 0 && wall.Length > 1)
            .Select(wall => (double?)Math.Max(0, Line2D.DistanceFromSegment(at, wall.Start, wall.End) - document.GetWallType(wall)!.Structure.TotalWidth / 2))
            .Min();

    /// <summary>A circle round a point, as an outline in plan.</summary>
    private static IReadOnlyList<Point2D> Circle(Point2D centre, double radius) =>
        new ComponentModel.Frame(centre, Vector2D.UnitX, 0).Ellipse(-radius, radius, -radius, radius, 48);

    /// <summary>What is wrong with where a tree, a shrub or a parking bay is, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Component piece, ClearanceStandard standard)
    {
        if (document.FindType<ComponentType>(piece.TypeId) is not { } type) return Array.Empty<PlacementProblem>();

        var us = standard.IsUs();
        var problems = new List<PlacementProblem>();
        switch (type.Form)
        {
            case ComponentForm.Tree or ComponentForm.Conifer or ComponentForm.Shrub when FromBuilding(document, piece.LevelId, piece.Location) is { } apart:
            {
                var shrub = type.Form == ComponentForm.Shrub;
                if (!us && (!shrub || type.Height > 1800 + 0.5))
                {
                    var zone = (shrub ? 0.75 : ZoneFactor(type.WaterDemand)) * type.Height;
                    var demand = type.WaterDemand == TreeWaterDemand.Unknown ? "high (as one of no named species is taken)" : EnumText.Humanise(type.WaterDemand).ToLowerInvariant();
                    if (apart < zone - 0.5)
                        problems.Add(new PlacementProblem(piece, $"it is {apart / 1000:0.0} m from the building, inside the {zone / 1000:0.0} m round it its roots may dry a clay soil.",
                            shrub
                                ? "NHBC Standards 4.2 (Table 6) keep a shrub over 1.8 m tall 0.75 times its mature height from foundations not taken deeper."
                                : $"NHBC Standards 4.2 put a {demand} water demand tree's zone of influence at {ZoneFactor(type.WaterDemand)} times its mature height (Table 3): foundations inside it on a shrinkable clay go deeper (Tables 4 and 5), an engineer's design past 2.5 m.",
                            Circle(piece.Location, zone)));
                }

                // A tree kept on the site: its roots kept clear of the building.
                if (!shrub && piece.PhaseCreated == DesignPhase.Existing && type.StemDiameter > 0 && RootProtection(type) is var root && apart < root - 0.5)
                    problems.Add(new PlacementProblem(piece, $"it is {apart / 1000:0.0} m from the building, inside its {root / 1000:0.0} m root protection area.",
                        us
                            ? "Keep building out of a kept tree's critical root zone: 1 ft for each inch of its trunk's diameter - 12 times it."
                            : "BS 5837 keeps building out of a kept tree's root protection area: a circle 12 times its stem's diameter, 15 m at most.",
                        Circle(piece.Location, root)));
                break;
            }

            case ComponentForm.ParkingBay or ComponentForm.AccessibleParkingBay:
            {
                var accessible = type.Form == ComponentForm.AccessibleParkingBay;
                var footprint = ComponentModel.Footprint(type, ComponentModel.FrameOf(piece, 0));

                // Its size.
                var (wide, deep, sizeRule) = (accessible, us) switch
                {
                    (true, true) => (3962.0, 0.0, "ADA 502.2 asks for an accessible space 96 in (2438) wide with a 60 in (1524) aisle beside it."),
                    (true, false) => (3600.0, 6000.0, "Approved Document M (M4(3) 3.12) asks for a 2.4 x 4.8 m bay with 1.2 m to one side and behind it - 3.6 x 6.0 m."),
                    (false, false) => (2400.0, 4800.0, "Approved Document M's standard parking bay is 2.4 x 4.8 m."),
                    _ => (0.0, 0.0, string.Empty)
                };
                if (type.Width < wide - 0.5 || type.Depth < deep - 0.5)
                    problems.Add(new PlacementProblem(piece, $"at {type.Width / 1000:0.00} x {type.Depth / 1000:0.00} m it is too small.", sizeRule, footprint));

                // How steep the ground under it is, along it and across it.
                var frame = ComponentModel.FrameOf(piece, 0);
                double? Ground(double across, double forward) => Terrains.GroundAt(document, frame.Plan(across, forward));
                var (halfW, halfD) = (type.Width / 2, type.Depth / 2);
                if (Ground(0, -halfD) is { } back && Ground(0, halfD) is { } front && Ground(-halfW, 0) is { } left && Ground(halfW, 0) is { } right)
                {
                    var steepest = Math.Max(Math.Abs(front - back) / type.Depth, Math.Abs(right - left) / type.Width);
                    var (most, slopeRule) = (accessible, us) switch
                    {
                        (true, true) => (1 / 48.0, "ADA 502.4 asks for an accessible space no steeper than 1 in 48 any way."),
                        (true, false) => (1 / 60.0, "Approved Document M (M4(3) 3.12) asks for an accessible bay to be level - 1 in 60 at most."),
                        (false, false) => (1 / 20.0, "Approved Document M (M4(2) 2.12d) asks for a bay level or gently sloping - 1 in 20 at most."),
                        _ => (double.MaxValue, string.Empty)
                    };
                    if (steepest > most + 1e-6)
                        problems.Add(new PlacementProblem(piece, $"the ground under it falls 1 in {1 / steepest:0}.", slopeRule, footprint));
                }

                // An ordinary bay in the UK, capable of widening to 3.3 m: a 900 strip beside it free of walls.
                if (!accessible && !us && type.Width < 3300 - 0.5)
                {
                    var walls = document.Walls
                        .Where(wall => wall.LevelId == piece.LevelId && document.GetWallType(wall) is { } t && t.Structure.TotalWidth > 0)
                        .Select(wall => CurtainGeometry.Band(wall, document.GetWallType(wall)!, 0, wall.Length, document.GetWallType(wall)!.Structure.TotalWidth / 2, -document.GetWallType(wall)!.Structure.TotalWidth / 2))
                        .ToList();
                    var widen = 3300 - type.Width;
                    bool Free(IReadOnlyList<Point2D> strip) => walls.All(wall => PolygonBoolean.Combine(strip, wall, BooleanOperation.Intersection).Sum(region => region.Area) <= 100 * 100);
                    if (!Free(frame.Outline(-halfW - widen, -halfW, -halfD, halfD)) && !Free(frame.Outline(halfW, halfW + widen, -halfD, halfD)))
                        problems.Add(new PlacementProblem(piece, "a wall either side of it stops it being widened to 3.3 m.",
                            "Approved Document M (M4(2) 2.12a) asks for a bay capable of widening to 3.3 m.", footprint));
                }

                break;
            }
        }

        return problems;
    }

    /// <summary>Every tree's, shrub's and bay's problems on a storey, as warnings among the clearances.</summary>
    public static IReadOnlyList<BlockedClearance> Blocked(BimDocument document, Guid levelId, ClearanceStandard standard) =>
        document.All<Component>()
            .Where(piece => piece.LevelId == levelId)
            .SelectMany(piece => Problems(document, piece, standard)
                .Select(problem => new BlockedClearance(new ClearanceZone(piece, "its place", problem.Outline, problem.Rule), string.Empty,
                    document.FindType<ComponentType>(piece.TypeId)?.Name ?? "A piece", problem.Said)))
            .ToList();
}
