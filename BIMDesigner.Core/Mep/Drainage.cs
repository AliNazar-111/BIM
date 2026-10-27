using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>An access point to a drain as Approved Document H's Table 13 spaces them, with the start of a drain and a junction.</summary>
public enum AccessClass
{
    Start,
    RoddingEye,
    AccessFittingSmall,
    AccessFittingLarge,
    ShallowChamber,
    DeepChamber,
    Junction
}

/// <summary>
/// Foul water and rainwater drainage, as Approved Document H has it - each system traced back
/// from the outfall it drains to, so every pipe knows the fittings and WCs it carries, which way
/// it falls and what it runs on to.
///
/// Above ground (H1, section 1): an appliance's branch at least the size of its trap (1.15,
/// Table 1: a basin 32, a bath, shower, sink or machine 40, a WC 100); unventilated, a single
/// appliance's branch no longer than Diagram 3 allows - a sink's or bath's 3 m at 40 mm or 4 m at
/// 50, a basin's 1.7 m at 32 mm falling no more than 22 mm/m (1.1 m to 44, 0.7 m to 87) or 3 m at
/// 40, a WC's 6 m - and falling 18 to 90 mm/m (Table 2); a stack serving a WC at least 100 (1.28),
/// no branch into it lower than 450 mm above its foot (1.11), and ventilated - open above the roof,
/// finishing 900 mm over any opening into the building within 3 m (1.31), or closed inside with an
/// air admittance valve (1.33); a ground floor WC straight to a drain only where the drain is no
/// more than 1.3 m below the floor (1.9).
///
/// Below ground (section 2): a foul drain 75 at least, 100 where it carries a WC (2.33), laid no
/// flatter than Table 6 - 1:40 for a flow under 1 l/s, 1:80 over it with a WC, 1:150 for a 150
/// with five WCs - a dwelling's flow being 2.5 l/s (Table 5); access at the head of each run, at
/// bends, changes of gradient and of size (2.49), and no further apart than Table 13. Rainwater
/// (H3): every downpipe to the ground taken by a drain to an outfall - a soakaway, a watercourse
/// or a sewer; drains 75 at least (3.14), 75 and 100 no flatter than 1:100, 150 than 1:150 (3.15);
/// a soakaway 5 m at least from a building (3.25).
///
/// In the US: each fixture's drainage fixture units (IRC Table P3004.1), a trap's arm no longer
/// than Table P3105.1 allows - 5 ft for a 1¼ in trap, 6 for 1½, 8 for 2, 12 for 3, 16 for 4 - and
/// pipes 2½ in and less falling ¼ in a foot, 3 to 6 in ⅛ in (P3005.3).
/// </summary>
public static class Drainage
{
    public static bool IsDrainage(SystemKind kind) => kind is SystemKind.Foul or SystemKind.Rainwater;

    /// <summary>The least trap an appliance has, mm (Approved Document H Table 1) - a WC's outlet over 80 mm.</summary>
    public static double TrapSize(ComponentForm form) => form switch
    {
        ComponentForm.Basin => 32,
        ComponentForm.Bath or ComponentForm.Shower or ComponentForm.Sink or ComponentForm.WashingMachine or ComponentForm.Dishwasher => 40,
        ComponentForm.Toilet or ComponentForm.WallHungToilet => 100,
        _ => 0
    };

    /// <summary>A fixture's drainage fixture units in a dwelling (IRC Table P3004.1): a lavatory 1, a bathtub, shower, kitchen sink, dishwasher or clothes washer 2, a 1.6 gpf WC 3.</summary>
    public static double FixtureUnits(ComponentForm form) => form switch
    {
        ComponentForm.Basin => 1,
        ComponentForm.Bath or ComponentForm.Shower or ComponentForm.Sink or ComponentForm.Dishwasher or ComponentForm.WashingMachine => 2,
        ComponentForm.Toilet or ComponentForm.WallHungToilet => 3,
        _ => 0
    };

    /// <summary>A pipe's bore, mm: what Approved Document H means by its size.</summary>
    public static double Bore(BimDocument document, Pipe pipe) =>
        document.FindType<PipeType>(pipe.TypeId) is { } type ? PipeSizes.Nearest(type.Material, pipe.Diameter).Inside : pipe.Diameter;

    /// <summary>How much a pipe falls toward where it drains, mm a metre along it - infinite where it drops upright; less than nothing where it rises.</summary>
    public static double Fall(PipeLoad load)
    {
        var plan = new Point2D(load.Far.X, load.Far.Y).DistanceTo(new Point2D(load.Near.X, load.Near.Y));
        return plan < 1 ? (load.Far.Z >= load.Near.Z ? double.PositiveInfinity : double.NegativeInfinity) : (load.Far.Z - load.Near.Z) / plan * 1000;
    }

    /// <summary>The level the building stands at: its lowest storey's.</summary>
    private static double GroundFloor(BimDocument document) => document.Levels.Count == 0 ? 0 : document.Levels.Min(level => level.Elevation);

    /// <summary>Whether a pipe is below the ground: both its ends more than 150 mm below its lowest storey's floor - the ground being at least that far below a floor's damp-proof course.</summary>
    public static bool BelowGround(BimDocument document, Pipe pipe)
    {
        var (start, end) = pipe.Ends(document);
        return Math.Max(start.Z, end.Z) < GroundFloor(document) - 150;
    }

    /// <summary>How flat a drain may be laid, as a fall in mm a metre: Approved Document H Table 6 for foul water, 3.15 for rainwater.</summary>
    public static (double Fall, string Said) Flattest(SystemKind kind, double bore, int wcs) => kind == SystemKind.Rainwater
        ? bore <= 100 + 1 ? (10, "1:100 (Approved Document H, 3.15)") : bore <= 150 + 1 ? (1000.0 / 150, "1:150 (Approved Document H, 3.15)") : (1000.0 / 225, "1:225 (Approved Document H, 3.15)")
        : wcs == 0 ? (25, "1:40 for a flow under 1 l/s (Approved Document H, Table 6)")
        : bore >= 150 - 1 && wcs >= 5 ? (1000.0 / 150, "1:150 with 5 WCs or more (Approved Document H, Table 6)")
        : (12.5, "1:80 for a dwelling's 2.5 l/s with a WC (Approved Document H, Tables 5 and 6)");

    /// <summary>How far a trap's arm may run to its vent, mm: IRC Table P3105.1 by the trap's size.</summary>
    public static double TrapArm(double trap) => trap switch
    {
        <= 32 => 1524,
        <= 40 => 1829,
        <= 50 => 2438,
        <= 75 => 3658,
        _ => 4877
    };

    /// <summary>The least fall of a drainage pipe in the US, mm a metre: ¼ in a foot for 2½ in and less, ⅛ in for 3 to 6 in (IRC P3005.3).</summary>
    public static double UsFall(double bore) => bore <= 65 ? 1000.0 / 48 : 1000.0 / 96;

    /// <summary>The access point an element of the drainage is, and how deep it is to the drain's invert.</summary>
    public static AccessClass? ClassOf(BimDocument document, Component access, double depth)
    {
        if (document.FindType<ComponentType>(access.TypeId) is not { Form: ComponentForm.DrainAccess } type) return null;
        return type.Access switch
        {
            DrainAccessKind.RoddingEye => AccessClass.RoddingEye,
            DrainAccessKind.AccessFitting => type.Width >= 225 ? AccessClass.AccessFittingLarge : AccessClass.AccessFittingSmall,
            DrainAccessKind.InspectionChamber => depth <= 1200 ? AccessClass.ShallowChamber : AccessClass.DeepChamber,
            _ => AccessClass.DeepChamber
        };
    }

    /// <summary>
    /// Approved Document H Table 13: the most a drain may run between access points, m, from one to
    /// the next - null where the table sets nothing.
    /// </summary>
    public static double? Spacing(AccessClass from, AccessClass to)
    {
        var column = to switch
        {
            AccessClass.AccessFittingSmall => 0,
            AccessClass.AccessFittingLarge => 1,
            AccessClass.Junction => 2,
            AccessClass.ShallowChamber => 3,
            AccessClass.DeepChamber => 4,
            _ => -1
        };
        if (column < 0) return null;
        double?[] Row(params double?[] values) => values;
        var row = from switch
        {
            AccessClass.Start => Row(12, 12, null, 22, 45),
            AccessClass.RoddingEye => Row(22, 22, 22, 45, 45),
            AccessClass.AccessFittingSmall => Row(null, null, 12, 22, 22),
            AccessClass.AccessFittingLarge => Row(null, null, 22, 45, 45),
            AccessClass.ShallowChamber => Row(22, 45, 22, 45, 45),
            AccessClass.DeepChamber => Row(null, null, null, 45, 90),
            _ => Row(null, null, null, null, null)
        };
        return row[column];
    }

    private static string Name(AccessClass access) => access switch
    {
        AccessClass.Start => "the start of the drain",
        AccessClass.RoddingEye => "a rodding eye",
        AccessClass.AccessFittingSmall => "a small access fitting",
        AccessClass.AccessFittingLarge => "a large access fitting",
        AccessClass.ShallowChamber => "an inspection chamber",
        AccessClass.DeepChamber => "a manhole",
        _ => "a junction"
    };

    /// <summary>The drain's invert under an access point - the bottom of the inside of the pipe there - and the access point's cover, its own base.</summary>
    public static (double Cover, double Invert)? Levels(BimDocument document, Component access)
    {
        if (document.FindType<ComponentType>(access.TypeId) is not { } type) return null;
        var drains = document.All<Pipe>().Where(pipe => IsDrainage(pipe.Kind) && BelowGround(document, pipe)).ToList();
        var under = drains.Select(pipe =>
        {
            var (start, end) = pipe.Ends(document);
            var along = Line2D.ClosestPointOnSegment(access.Location, pipe.Start, pipe.End);
            var length = pipe.Start.DistanceTo(pipe.End);
            var t = length < 1 ? 0 : pipe.Start.DistanceTo(along) / length;
            return (Distance: along.DistanceTo(access.Location), Invert: start.Z + (end.Z - start.Z) * t - Bore(document, pipe) / 2);
        }).Where(entry => entry.Distance <= PipeNetworks.Reach).OrderBy(entry => entry.Invert).ToList();
        if (under.Count == 0) return null;
        return (ComponentHosting.BaseElevation(document, access, type), under[0].Invert);
    }

    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var floor = GroundFloor(document);
        var drained = new HashSet<Guid>();
        var accesses = document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.DrainAccess).ToList();
        var valves = document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.AirAdmittanceValve).ToList();

        foreach (var system in document.All<MepSystem>().Where(system => IsDrainage(system.Kind)))
        {
            var pipes = PipeNetworks.PipesOf(document, system);
            if (pipes.Count == 0) continue;
            var network = PipeNetworks.Trace(document, system);
            foreach (var downpipe in network.Downpipes) drained.Add(downpipe.Id);

            // Every pipe: the right way, and - below the ground - big enough and steep enough.
            foreach (var (pipe, load) in network.Loads)
            {
                var fall = Fall(load);
                var bore = Bore(document, pipe);
                if (fall < -0.5)
                {
                    yield return new MepProblem(pipe, $"it runs uphill, {Units.Millimetres(-fall, "0")} a metre, to where it drains");
                    continue;
                }

                if (!BelowGround(document, pipe))
                {
                    if (us && !pipe.Upright && fall < UsFall(bore) - 0.05)
                        yield return new MepProblem(pipe, $"it falls {Units.Millimetres(fall, "0.#")} a metre - IRC P3005.3 lays a {(bore <= 65 ? "2½ in pipe or less at ¼" : "3 to 6 in pipe at ⅛")} in a foot");
                    continue;
                }

                if (system.Kind == SystemKind.Foul && bore < 75 - 1)
                    yield return new MepProblem(pipe, $"a foul drain of {bore:0} - Approved Document H (2.33) asks 75 at least");
                else if (system.Kind == SystemKind.Foul && load.Wcs > 0 && bore < 100 - 1)
                    yield return new MepProblem(pipe, $"it carries {(load.Wcs == 1 ? "a WC" : $"{load.Wcs} WCs")} at {bore:0} - Approved Document H (2.33) asks 100 at least");
                else if (system.Kind == SystemKind.Rainwater && bore < 75 - 1)
                    yield return new MepProblem(pipe, $"a rainwater drain of {bore:0} - Approved Document H (3.14) asks 75 at least");

                var (flattest, said) = us ? (UsFall(bore), $"{(bore <= 65 ? "¼" : "⅛")} in a foot (IRC P3005.3)") : Flattest(system.Kind, bore, load.Wcs);
                if (!pipe.Upright && fall < flattest - 0.05)
                    yield return new MepProblem(pipe, $"it falls 1:{(fall <= 0 ? 0 : 1000 / fall):0} - laid no flatter than {said}");
            }

            // Each appliance's branch, from its trap to where it meets a stack, a drain or another branch.
            foreach (var (fitting, first) in network.FirstPipe)
            {
                if (document.FindType<ComponentType>(fitting.TypeId) is not { } type || TrapSize(type.Form) is var trap && trap == 0) continue;
                if (system.Kind != SystemKind.Foul) continue;
                var name = type.Name;
                if (!us && Bore(document, first) < trap - 1)
                    yield return new MepProblem(fitting, $"its waste pipe is {Bore(document, first):0} - Approved Document H (1.15, Table 1) asks its trap's {trap:0} at least");

                // The branch: on from the trap, down its tail, until it meets a stack or another branch, or reaches the drain.
                var branch = new List<Pipe>();
                for (Pipe? pipe = first; pipe is not null && network.Loads.TryGetValue(pipe, out var load) && load.Fittings == 1 && !BelowGround(document, pipe); pipe = load.Toward)
                {
                    branch.Add(pipe);
                    if (load.NearJoins > 2) break;
                }

                if (branch.Count == 0) continue;
                var length = branch.Where(pipe => !pipe.Upright).Sum(pipe => pipe.Length);
                var falls = branch.Select(pipe => Fall(network.Loads[pipe])).Where(double.IsFinite).ToList();
                var bore = branch.Min(pipe => Bore(document, pipe));
                var (least, most) = (falls.DefaultIfEmpty(0).Min(), falls.DefaultIfEmpty(0).Max());
                var wc = type.Form is ComponentForm.Toilet or ComponentForm.WallHungToilet;

                if (us)
                {
                    if (!wc && length > TrapArm(trap) + 1)
                        yield return new MepProblem(fitting, $"its trap's arm runs {length / 304.8:0.#} ft to the vent - IRC Table P3105.1 keeps a {UsInches(trap)} trap within {TrapArm(trap) / 304.8:0} ft");
                    continue;
                }

                double? longest = type.Form switch
                {
                    ComponentForm.Sink or ComponentForm.Bath => bore >= 50 - 1 ? 4000 : 3000,
                    ComponentForm.Basin => bore >= 50 - 1 ? 4000 : bore >= 40 - 1 ? 3000 : most <= 22 ? 1700 : most <= 44 ? 1100 : most <= 87 ? 700 : 0,
                    ComponentForm.Toilet or ComponentForm.WallHungToilet => 6000,
                    _ => null
                };
                if (longest is { } limit && length > limit + 1)
                    yield return new MepProblem(fitting, $"its branch runs {Units.Metres(length, "0.00")} - unventilated, Approved Document H (Diagram 3, Table 2) keeps it to {Units.Metres(limit, "0.0")} at {bore:0} falling up to {Units.Millimetres(most, "0")} a metre: vent it, or fit an air admittance valve (1.20)");

                var (flattestBranch, steepest) = type.Form == ComponentForm.Basin && bore < 40 - 1 ? (18.0, 87.0) : (18.0, wc ? double.PositiveInfinity : 90.0);
                if (falls.Count > 0 && least < flattestBranch - 0.05)
                    yield return new MepProblem(fitting, $"its branch falls {Units.Millimetres(least, "0.#")} a metre - Approved Document H (Table 2) lays it at {flattestBranch:0} at the least");
                else if (falls.Count > 0 && most > steepest + 0.05)
                    yield return new MepProblem(fitting, $"its branch falls {Units.Millimetres(most, "0")} a metre - Approved Document H (Table 2) lays it no steeper than {steepest:0}");

                // A ground floor WC straight to a drain: only where the drain is within 1.3 m of the floor.
                if (wc && network.Loads[branch[^1]].Toward is { } into && BelowGround(document, into) && fitting.LevelId == document.Levels.OrderBy(level => level.Elevation).First().Id)
                {
                    var depth = floor - (network.Loads[branch[^1]].Near.Z - Bore(document, into) / 2);
                    if (depth > 1300 + 1)
                        yield return new MepProblem(fitting, $"it goes straight to a drain {Units.Metres(depth, "0.00")} below the floor - Approved Document H (1.9) asks 1.3 m at most: take it to a stack");
                }
            }

            if (system.Kind == SystemKind.Foul)
            {
                foreach (var problem in Stacks(document, network, valves, us)) yield return problem;
                if (!us) foreach (var problem in Access(document, network, accesses)) yield return problem;
            }
            else if (!us)
                foreach (var problem in Access(document, network, accesses)) yield return problem;
        }

        // Rainwater from the roofs to an outfall, and soakaways away from the building.
        if (!us)
        {
            foreach (var downpipe in document.All<Downpipe>())
                if (!drained.Contains(downpipe.Id) && Downpipes.Path(document, downpipe) is { OntoRoof: false })
                    yield return new MepProblem(downpipe, "it discharges to the ground with no drain - Approved Document H (H3) carries rainwater to an outfall: a soakaway, a watercourse or a sewer");

            foreach (var soakaway in document.All<Component>().Where(component => document.FindType<ComponentType>(component.TypeId)?.Form == ComponentForm.Soakaway))
            {
                var nearest = document.Walls.Select(wall => Line2D.DistanceFromSegment(soakaway.Location, wall.Start, wall.End)).DefaultIfEmpty(double.MaxValue).Min();
                if (nearest < 5000)
                    yield return new MepProblem(soakaway, $"it is {Units.Metres(nearest, "0.0")} from the building - Approved Document H (3.25) keeps a soakaway 5 m from a building");
            }
        }
    }

    private static string UsInches(double trap) => trap switch { <= 32 => "1¼ in", <= 40 => "1½ in", <= 50 => "2 in", <= 75 => "3 in", _ => "4 in" };

    /// <summary>A stack: the upright pipes standing over one another above the ground, the fittings they carry, and where they stand.</summary>
    private sealed record Stack(Point2D At, IReadOnlyList<Pipe> Pipes, double Foot, double Top, double Bore, int Wcs);

    private static IEnumerable<MepProblem> Stacks(BimDocument document, PipeNetwork network, IReadOnlyList<Component> valves, bool us)
    {
        var upright = network.Loads.Keys.Where(pipe => pipe.Upright && !BelowGround(document, pipe)).ToList();
        var stacks = new List<Stack>();
        foreach (var group in upright.GroupBy(pipe => (Math.Round(pipe.Start.X / 20), Math.Round(pipe.Start.Y / 20))))
        {
            var ends = group.SelectMany(pipe => { var (a, b) = pipe.Ends(document); return new[] { a.Z, b.Z }; }).ToList();
            var (foot, top) = (ends.Min(), ends.Max());
            var carried = group.Max(pipe => network.Loads[pipe].Fittings);
            // A drop from one fitting to a drain is its branch; a stack carries more, or stands a storey high.
            if (carried < 2 && top - foot < 1500) continue;
            stacks.Add(new Stack(group.First().Start, group.ToList(), foot, top, group.Min(pipe => Bore(document, pipe)), group.Max(pipe => network.Loads[pipe].Wcs)));
        }

        foreach (var stack in stacks)
        {
            if (!us && stack.Wcs > 0 && stack.Bore < 100 - 1)
                yield return new MepProblem(stack.Pipes[0], $"a stack of {stack.Bore:0} serving a WC - Approved Document H (1.28) asks 100 at least");

            // No branch into it within 450 of its foot (Approved Document H 1.11).
            if (!us)
                foreach (var (pipe, load) in network.Loads.Where(entry => !entry.Key.Upright && !BelowGround(document, entry.Key)))
                    if (new Point2D(load.Near.X, load.Near.Y).DistanceTo(stack.At) <= PipeNetworks.Touch * 4 && load.Near.Z - stack.Foot is > 1 and < 450)
                        yield return new MepProblem(pipe, $"it joins the stack {Units.Millimetres(load.Near.Z - stack.Foot, "0")} above its foot - Approved Document H (1.11) keeps branches 450 mm above the bend at a stack's foot");

            // Ventilated: open above the roof, or closed with an air admittance valve (Approved Document H 1.29 to 1.33).
            var valve = valves.Any(each => each.Location.DistanceTo(stack.At) <= 200 && Math.Abs(ComponentHosting.BaseElevation(document, each, document.FindType<ComponentType>(each.TypeId)!) - stack.Top) <= 500);
            if (valve) continue;
            var roof = document.All<Roof>().Where(each => Polygon2D.Contains(each.Boundary, stack.At)).Select(each => (double?)each.Surface(document).HeightAt(stack.At)).Max();
            if (roof is { } under && stack.Top < under + 150)
            {
                yield return new MepProblem(stack.Pipes[0], us
                    ? "its vent stops under the roof - carry it up through the roof, or close it with an air admittance valve"
                    : "it is not ventilated: carry it up through the roof, or close it inside with an air admittance valve (Approved Document H, 1.29 and 1.33)");
                continue;
            }

            if (us) continue;

            // Openings into the building: windows and doors in its outside walls - not a door between rooms.
            foreach (var opening in document.Openings)
            {
                if (document.Find<Wall>(opening.HostWallId) is not { } wall || document.FindType<OpeningType>(opening.TypeId) is not { } type) continue;
                if (document.GetWallType(wall)?.Function != WallFunction.Exterior && document.FindType<CurtainWallType>(wall.TypeId) is null) continue;
                var centre = opening.GetCentre(wall);
                var reach = Math.Max(0, centre.DistanceTo(stack.At) - opening.WidthOf(type) / 2);
                var head = (document.FindLevel(opening.LevelId)?.Elevation ?? 0) + wall.BaseOffset + opening.SillHeight + opening.HeightOf(type);
                if (reach < 3000 && stack.Top < head + 900)
                {
                    yield return new MepProblem(stack.Pipes[0],
                        $"its vent finishes {Units.Metres((stack.Top - head), "0.00")} above the head of {(string.IsNullOrEmpty(opening.Mark) ? type.Name : opening.Mark)}, {Units.Metres(reach, "0.0")} away - Approved Document H (1.31) asks 900 mm above any opening within 3 m");
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Access to the drains: along each run from its head to the outfall, an access point at its
    /// head, at each bend, change of gradient or of size (Approved Document H 2.49), and no further
    /// apart than Table 13.
    /// </summary>
    private static IEnumerable<MepProblem> Access(BimDocument document, PipeNetwork network, IReadOnlyList<Component> accesses)
    {
        var drains = network.Loads.Where(entry => BelowGround(document, entry.Key)).ToDictionary(entry => entry.Key, entry => entry.Value);
        if (drains.Count == 0) yield break;

        AccessClass? At(Point3D where)
        {
            var plan = new Point2D(where.X, where.Y);
            var near = accesses.Where(access => access.Location.DistanceTo(plan) <= PipeNetworks.Reach).OrderBy(access => access.Location.DistanceTo(plan)).FirstOrDefault();
            if (near is null) return null;
            var depth = Levels(document, near) is { } levels ? levels.Cover - levels.Invert : 0;
            return ClassOf(document, near, depth);
        }

        static bool Same(Point3D a, Point3D b) => Math.Abs(a.X - b.X) <= PipeNetworks.Touch && Math.Abs(a.Y - b.Y) <= PipeNetworks.Touch && Math.Abs(a.Z - b.Z) <= PipeNetworks.Touch;

        // How many drains come into each node from upstream: two or more, a junction.
        int Into(Point3D node) => drains.Values.Count(load => Same(load.Near, node));

        // The heads: drains nothing below ground comes into. One a stack or branch comes down into is the start of the drain.
        var reported = new HashSet<Pipe>();
        foreach (var (head, load) in drains.Where(entry => Into(entry.Value.Far) == 0))
        {
            var fromAbove = network.Loads.Any(entry => !BelowGround(document, entry.Key) && Same(entry.Value.Near, load.Far));
            var last = At(load.Far) ?? (fromAbove ? AccessClass.Start : (AccessClass?)null);
            if (last is null && reported.Add(head))
                yield return new MepProblem(head, "no access at the head of the drain - Approved Document H (2.49) asks a rodding eye or a chamber there");

            var run = 0.0;
            Pipe? previous = null;
            PipeLoad? final = null;
            for (Pipe? pipe = head; pipe is not null && drains.TryGetValue(pipe, out var here); previous = pipe, pipe = here.Toward)
            {
                final = here;
                // At the pipe's upstream end: a bend, a change of gradient or of size wants access there.
                if (previous is not null)
                {
                    var before = drains[previous];
                    var access = At(here.Far);
                    var turn = Turn(before, here);
                    var change = Math.Abs(Fall(before) - Fall(here));
                    var resized = Math.Abs(Bore(document, previous) - Bore(document, pipe)) > 1;
                    if (access is null && (turn > 10 || (double.IsFinite(change) && change > 2) || resized) && reported.Add(pipe))
                        yield return new MepProblem(pipe, $"{(turn > 10 ? $"it turns {turn:0}°" : resized ? "it changes size" : "its gradient changes")} with no access point there - Approved Document H (2.49, 2.19) asks one at every bend, change of gradient and change of size");

                    var junction = Into(here.Far) >= 2;
                    var point = access ?? (junction ? AccessClass.Junction : (AccessClass?)null);
                    if (point is { } reached)
                    {
                        if (last is { } from && Spacing(from, reached) is { } most && run > most * 1000 + 1 && reported.Add(pipe))
                            yield return new MepProblem(pipe, $"{Units.Metres(run, "0.0")} of drain from {Name(from)} to {Name(reached)} - Approved Document H (Table 13) allows {most:0} m");
                        if (access is { } kept) { last = kept; run = 0; }
                    }
                }

                run += pipe.Length;
            }

            // The last length, to the chamber where the drain leaves - at the boundary, by the sewer.
            if (previous is not null && final is not null && At(final.Near) is { } leaving && last is { } fromLast
                && Spacing(fromLast, leaving) is { } furthest && run > furthest * 1000 + 1 && reported.Add(previous))
                yield return new MepProblem(previous, $"{Units.Metres(run, "0.0")} of drain from {Name(fromLast)} to {Name(leaving)} - Approved Document H (Table 13) allows {furthest:0} m");
        }
    }

    /// <summary>How far a drain turns in plan from one pipe to the next, degrees.</summary>
    private static double Turn(PipeLoad upstream, PipeLoad downstream)
    {
        var a = new Vector2D(upstream.Near.X - upstream.Far.X, upstream.Near.Y - upstream.Far.Y);
        var b = new Vector2D(downstream.Near.X - downstream.Far.X, downstream.Near.Y - downstream.Far.Y);
        if (a.Length < 1 || b.Length < 1) return 0;
        var cos = Math.Clamp((a.X * b.X + a.Y * b.Y) / (a.Length * b.Length), -1, 1);
        return Math.Acos(cos) * 180 / Math.PI;
    }
}
