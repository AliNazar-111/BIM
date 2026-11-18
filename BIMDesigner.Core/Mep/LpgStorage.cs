using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// LPG kept outside, as Approved Document J has it - in England, not under the US's codes:
///
/// A tank of 1.1 tonnes or less standing in the open air (5.14 to 5.18, Table 11): from the building, the boundary and a fixed source of
/// ignition - a flue's outlet - 2.5 m for one of 0.25 tonnes or less, 3 m for one of 1.1 tonnes; behind a fire wall 0.3 m and 1.5 m, the
/// wall as high as the tank's relief valve - its top - and the 2.5 m or 3 m kept around its ends; drains and gullies within those distances
/// protected from the gas getting in (5.15). Past 1.1 tonnes, the industry's code with the HSE (5.13).
///
/// Cylinders standing against a wall outside (5.19, 5.20): their valves - their tops - 1 m from an opening in the building or a flue's
/// outlet across, or 300 mm above or below it; 2 m from a gully unless a wall 250 mm high stands between; on concrete or paving.
/// </summary>
public static class LpgStorage
{
    /// <summary>Table 11: a tank holding this much at most, kg, kept so far from things, and so far behind a fire wall, mm.</summary>
    public static readonly IReadOnlyList<(double Kg, double Open, double Shielded)> Table11 = new[] { (250.0, 2500.0, 300.0), (1100.0, 3000.0, 1500.0) };

    /// <summary>5.20: cylinders' valves from an opening or a flue's outlet, across and up or down; from a gully; the wall between that will do, mm.</summary>
    public const double ValveAcross = 1000, ValveUpDown = 300, FromGully = 2000, GullyWall = 250;

    /// <summary>What is wanting in a building's LPG.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        foreach (var piece in document.All<Component>())
        {
            if (document.FindType<ComponentType>(piece.TypeId) is not { } type) continue;
            if (type.Form == ComponentForm.LpgTank)
                foreach (var problem in Tank(document, piece, type)) yield return problem;
            else if (type.Form == ComponentForm.LpgCylinders)
                foreach (var problem in Cylinders(document, piece, type)) yield return problem;
        }
    }

    private static IEnumerable<MepProblem> Tank(BimDocument document, Component tank, ComponentType type)
    {
        if (RoomsOf.Of(document, tank) is { } room)
        {
            yield return new MepProblem(tank, $"it is inside the building, in {RoomsOf.Label(room)} - Approved Document J (5.15) asks an LPG tank outdoors, and not in a pit");
            yield break;
        }

        if (Table11.FirstOrDefault(row => type.Capacity <= row.Kg + 1e-6) is not { Kg: > 0 } band)
        {
            yield return new MepProblem(tank, $"it holds {type.Capacity / 1000:0.00} tonnes - Approved Document J's guidance (5.13, 5.14) goes to 1.1 tonnes; past that, the industry's code agreed with the HSE");
            yield break;
        }

        var footprint = OilHeating.Footprint(tank, type);
        var top = tank.Elevation + type.Height;
        var level = tank.LevelId;
        var said = $"{(band.Kg <= 250 ? "0.25" : "1.1")} tonnes or less";

        // What it is kept from: the building's walls, the boundary, and every flue's outlet.
        var targets = new List<(string What, Point2D At, double Gap)>();
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == level && wall.Length > 1 && Combustibility.BoundsARoom(document, wall)))
        {
            var face = Nearest(footprint, wall.Start, wall.End);
            targets.Add(("the building", face.At, face.Gap - (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2));
        }

        foreach (var line in document.All<PropertyLine>().Where(line => line.Boundary.Count >= 2))
            for (var i = 0; i < line.Boundary.Count; i++)
            {
                var edge = Nearest(footprint, line.Boundary[i], line.Boundary[(i + 1) % line.Boundary.Count]);
                targets.Add(("the boundary", edge.At, edge.Gap));
            }

        foreach (var outlet in FlueOutlets.All(document).Where(outlet => outlet.Wall.LevelId == level))
            targets.Add(("a flue's outlet", outlet.At, Distance(footprint, outlet.At)));

        foreach (var group in targets.Where(target => target.Gap < band.Open - 0.5).GroupBy(target => target.What))
        {
            var (what, at, gap) = group.MinBy(target => target.Gap);
            if (FireWall(document, tank, footprint, at, top) is { } wall)
            {
                var around = new[] { wall.Start, wall.End }.Min(end => Distance(footprint, end) + end.DistanceTo(at));
                if (gap >= band.Shielded - 0.5 && around >= band.Open - 0.5) continue;
                yield return new MepProblem(tank, gap < band.Shielded - 0.5
                    ? $"it stands {Units.Metres(Math.Max(0, gap), "0.0")} from {what}, behind a fire wall - Approved Document J (Table 11) asks {Units.Metres(band.Shielded, "0.0")} behind one for a tank of {said}"
                    : $"it stands {Units.Metres(around, "0.0")} from {what} around the ends of its fire wall - Approved Document J (5.18, Table 11) asks the fire wall to reach so far that {Units.Metres(band.Open, "0.0")} is kept around its ends, for a tank of {said}");
            }
            else
                yield return new MepProblem(tank, $"it stands {Units.Metres(Math.Max(0, gap), "0.0")} from {what} - Approved Document J (Table 11) asks {Units.Metres(band.Open, "0.0")} for a tank of {said}, or {Units.Metres(band.Shielded, "0.0")} behind a fire wall as high as its relief valve");
        }

        // Drains and gullies within those distances: protected from the gas getting in (5.15).
        foreach (var drain in document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form is ComponentForm.Gully or ComponentForm.DrainAccess))
            if (Distance(footprint, drain.Location) is var gap && gap < band.Open - 0.5)
                yield return new MepProblem(tank, $"a {document.FindType<ComponentType>(drain.TypeId)!.Name} is {Units.Metres(gap, "0.0")} from it - Approved Document J (5.15) asks drains, gullies and cellar hatches within {Units.Metres(band.Open, "0.0")} of an LPG tank protected from the gas getting in");
    }

    private static IEnumerable<MepProblem> Cylinders(BimDocument document, Component cylinders, ComponentType type)
    {
        if (RoomsOf.Of(document, cylinders) is { } room)
        {
            yield return new MepProblem(cylinders, $"they are inside the building, in {RoomsOf.Label(room)} - Approved Document J (5.20) asks LPG cylinders outside, against a wall, well aired");
            yield break;
        }

        var footprint = OilHeating.Footprint(cylinders, type);
        var valves = cylinders.Elevation + type.Height;
        var level = document.FindLevel(cylinders.LevelId)?.Elevation ?? 0;

        // Openings in the building and flues' outlets: 1 m across, or 300 mm above or below the valves.
        var near = new List<(string What, double Across, double UpDown)>();
        foreach (var wall in document.Walls.Where(wall => wall.LevelId == cylinders.LevelId && wall.Length > 1 && Combustibility.BoundsARoom(document, wall)))
        {
            var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            var half = (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2;
            foreach (var window in document.All<Window>().Where(window => window.HostWallId == wall.Id))
                if (document.FindType<WindowType>(window.TypeId) is { } windowType && window.GetSpan(windowType) is var (from, to))
                    near.Add(($"the {windowType.Name} window", Distance(footprint, wall.Start + along * from, wall.Start + along * to) - half, Gap(valves, window.SillHeight, window.SillHeight + window.HeightOf(windowType))));
            foreach (var door in document.All<Door>().Where(door => door.HostWallId == wall.Id))
                if (document.FindType<DoorType>(door.TypeId) is { } doorType && door.GetSpan(doorType) is var (from, to))
                    near.Add(($"the {doorType.Name} door", Distance(footprint, wall.Start + along * from, wall.Start + along * to) - half, Gap(valves, 0, doorType.Height)));
        }

        foreach (var outlet in FlueOutlets.All(document).Where(outlet => outlet.Wall.LevelId == cylinders.LevelId))
            near.Add(("a flue's outlet", Distance(footprint, outlet.At), Math.Abs(valves - outlet.Height)));
        foreach (var (what, across, upDown) in near.Where(entry => entry.Across < ValveAcross - 0.5 && entry.UpDown < ValveUpDown - 0.5))
            yield return new MepProblem(cylinders, $"their valves are {Units.Metres(Math.Max(0, across), "0.0")} from {what} and {upDown:0} mm above or below it - Approved Document J (5.20 a) asks them 1 m across from an opening in the building or a flue's outlet, and 300 mm above or below it");

        // Gullies: 2 m, unless a wall 250 mm high stands between.
        foreach (var gully in document.All<Component>().Where(piece => document.FindType<ComponentType>(piece.TypeId)?.Form == ComponentForm.Gully))
        {
            var gap = Distance(footprint, gully.Location);
            if (gap >= FromGully - 0.5) continue;
            var walled = document.Walls.Any(wall => wall.LevelId == cylinders.LevelId && wall.GetHeight(document) >= GullyWall - 0.5 && Crosses(cylinders.Location, gully.Location, wall.Start, wall.End));
            if (!walled)
                yield return new MepProblem(cylinders, $"a gully is {Units.Metres(gap, "0.0")} from them - Approved Document J (5.20 b) asks LPG cylinders 2 m from an unsealed gully or a drain without a trap, unless a wall 250 mm high stands between");
        }

        // A firm level base: concrete or paving under them.
        var paved = document.All<GroundRegion>().Where(region => region.Surface is GroundSurface.Paving or GroundSurface.Path or GroundSurface.Driveway && region.Boundary.Count >= 3)
                        .Any(region => footprint.All(corner => Polygon2D.Contains(region.Boundary, corner)))
                    || document.All<Floor>().Any(floor => floor.LevelId == cylinders.LevelId && floor.Boundary.Count >= 3 && footprint.All(floor.Contains))
                    || document.All<SitePad>().Any(pad => pad.Boundary.Count >= 3 && footprint.All(corner => Polygon2D.Contains(pad.Boundary, corner)));
        if (!paved)
            yield return new MepProblem(cylinders, "no firm level base under them - Approved Document J (5.20) asks concrete 50 mm thick at least, or paving slabs bedded on mortar: draw a region of the ground laid as paving under them");
        _ = level;
    }

    /// <summary>How far a height is from a span of heights: none within it.</summary>
    private static double Gap(double height, double bottom, double top) => height < bottom ? bottom - height : height > top ? height - top : 0;

    /// <summary>A fire wall between a tank and a point: a wall of no room, across the line from the tank to it, as high as the tank's top - its relief valve.</summary>
    private static Wall? FireWall(BimDocument document, Component tank, IReadOnlyList<Point2D> footprint, Point2D toward, double top) =>
        document.Walls.FirstOrDefault(wall => wall.LevelId == tank.LevelId && wall.Length > 1 && !Combustibility.BoundsARoom(document, wall)
                                               && Crosses(tank.Location, toward, wall.Start, wall.End)
                                               && wall.GetTopElevation(document) - (document.FindLevel(tank.LevelId)?.Elevation ?? 0) >= top - 0.5);

    /// <summary>The point of a segment nearest an outline, and how far it is.</summary>
    private static (Point2D At, double Gap) Nearest(IReadOnlyList<Point2D> outline, Point2D a, Point2D b)
    {
        var best = (At: a, Gap: double.PositiveInfinity);
        foreach (var corner in outline)
        {
            var at = Line2D.ClosestPointOnSegment(corner, a, b);
            if (corner.DistanceTo(at) < best.Gap) best = (at, corner.DistanceTo(at));
        }

        for (var i = 0; i < outline.Count; i++)
            foreach (var end in new[] { a, b })
            {
                var gap = Line2D.DistanceFromSegment(end, outline[i], outline[(i + 1) % outline.Count]);
                if (gap < best.Gap) best = (end, gap);
            }

        return best;
    }

    private static double Distance(IReadOnlyList<Point2D> outline, Point2D point) =>
        Polygon2D.Contains(outline, point) ? 0 : outline.Select((corner, i) => Line2D.DistanceFromSegment(point, corner, outline[(i + 1) % outline.Count])).Min();

    private static double Distance(IReadOnlyList<Point2D> outline, Point2D a, Point2D b) => Nearest(outline, a, b).Gap;

    private static bool Crosses(Point2D p, Point2D q, Point2D r, Point2D s)
    {
        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var (d1, d2, d3, d4) = (Cross(r, s, p), Cross(r, s, q), Cross(p, q, r), Cross(p, q, s));
        return (d1 > 1e-6 && d2 < -1e-6 || d1 < -1e-6 && d2 > 1e-6) && (d3 > 1e-6 && d4 < -1e-6 || d3 < -1e-6 && d4 > 1e-6);
    }
}
