using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// Charging electric vehicles (round 98), as Approved Document S (2021) sets it out for new buildings in England.
///
/// Associated parking spaces (Appendix A): the parking bays within the site boundary - the property line, or all of them where none is drawn.
/// A covered car park: bays in an enclosed room that is not a dwelling's garage or carport. A new residential building (S1, 44D, 1.1 to 1.4):
/// as many spaces with a charge point as there are dwellings, or as there are spaces where there are fewer, outside a covered car park first;
/// where that is short of them, cable routes in the covered car park's spaces to make it up - all of them past 10 spaces (44D(4)); and past 10
/// spaces with more spaces than dwellings, cable routes to every space without a charge point (S1(3), 1.3). A new building other than a
/// residential one with more than 10 spaces (S4, 3.1 to 3.4): one charge point, outside a covered car park, and cable routes to one in five of
/// the rest. Each charge point 7 kW at least (6.2b), with a universal socket rather than a tethered cable save in exceptional cases (6.2c);
/// one with several outlets serving as many spaces at once (6.6), a future connection location likewise (6.5); where any are given and there
/// are accessible spaces, one of those served (6.4).
///
/// A charge point or future connection location serves a space within 1 m of it - taken so, Diagram 6.2 putting them at a space's corner.
/// The £3600 connection cost cap (44D(2), 1.5 to 1.7) and mixed-use buildings (S6) are not modelled.
/// </summary>
public static class EvCharging
{
    /// <summary>How near a space a charge point or future connection serves it, mm - taken so; its least rated output (6.2b), W; how many spaces before cable routes are asked (S1(3), 3.1).</summary>
    public const double Reach = 1000, LeastOutput = 7000;

    public const int Many = 10;

    /// <summary>What a model's parking falls short of in Approved Document S.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs()) yield break;
        ComponentType? TypeOf(Component piece) => document.FindType<ComponentType>(piece.TypeId);
        var pieces = document.All<Component>().Select(piece => (Piece: piece, Type: TypeOf(piece))).Where(entry => entry.Type is not null).Select(entry => (entry.Piece, Type: entry.Type!)).ToList();

        // Each charge point by itself (6.2).
        foreach (var (point, type) in pieces.Where(entry => entry.Type.Form == ComponentForm.EvChargePoint))
        {
            var output = type.Load / Math.Max(1, type.Outlets);
            if (output < LeastOutput - 0.5)
                yield return new MepProblem(point, $"it charges at {output / 1000:0.0} kW an outlet - Approved Document S (6.2b) asks a charge point of 7 kW nominal rated output at least");
            if (type.Tethered)
                yield return new MepProblem(point, "its cable is tethered - Approved Document S (6.2c) asks a universal socket, a tethered charge point only in exceptional cases such as a self-build home whose vehicle is known");
        }

        // The associated parking spaces: the bays within the site boundary, open or in a covered car park.
        var lines = document.All<PropertyLine>().Where(line => line.Boundary.Count >= 3).ToList();
        var bays = pieces.Where(entry => entry.Type.Form is ComponentForm.ParkingBay or ComponentForm.AccessibleParkingBay
                                         && (lines.Count == 0 || lines.Any(line => Polygon2D.Contains(line.Boundary, entry.Piece.Location))))
            .Select(entry => (entry.Piece, entry.Type, Outline: OilHeating.Footprint(entry.Piece, entry.Type), Covered: Covered(document, entry.Piece))).ToList();
        if (bays.Count == 0) yield break;

        // Which spaces the charge points serve, each as many as its outlets, nearest first; then the future connection locations the rest.
        var charged = Serve(pieces.Where(entry => entry.Type.Form == ComponentForm.EvChargePoint).ToList(), bays.Select(bay => (bay.Piece, bay.Outline)).ToList());
        var routed = Serve(pieces.Where(entry => entry.Type.Form == ComponentForm.EvFutureConnection).ToList(),
            bays.Where(bay => !charged.Contains(bay.Piece)).Select(bay => (bay.Piece, bay.Outline)).ToList());
        var open = bays.Where(bay => !bay.Covered).ToList();
        var covered = bays.Where(bay => bay.Covered).ToList();
        var total = bays.Count;
        var chargedOpen = open.Count(bay => charged.Contains(bay.Piece));
        Component First(IEnumerable<(Component Piece, ComponentType Type, IReadOnlyList<Point2D> Outline, bool Covered)> some, Func<Component, bool> wanting) =>
            some.Select(bay => bay.Piece).FirstOrDefault(wanting) ?? some.First().Piece;

        var use = FireDesign.UseOf(document);
        if (use is BuildingUse.House or BuildingUse.Flats)
        {
            // A new residential building (S1, 44D, Section 1).
            var named = Dwellings.Named(document).Count;
            var dwellings = named > 0 ? named : document.All<Room>().Any() ? 1 : 0;
            if (dwellings == 0) yield break;
            var needed = Math.Min(total, dwellings);
            var fromOpen = Math.Min(needed, open.Count);
            // Dwellings formed by a material change of use: S2 and regulation 44E - a charge point for each, no cable routes (round 145).
            if (Conversion.Applies(document))
            {
                var chargedAll = bays.Count(bay => charged.Contains(bay.Piece));
                if (chargedAll < needed)
                    yield return new MepProblem(First(bays, piece => !charged.Contains(piece)),
                        $"{chargedAll} of the {total} associated parking spaces {(chargedAll == 1 ? "has" : "have")} a charge point - Approved Document S (S2, regulation 44E) asks one for each dwelling formed by a material change of use, {needed}, where the car park or its electrics are worked on and the incoming supply takes them");
                yield break;
            }

            if (chargedOpen < fromOpen)
                yield return new MepProblem(First(open, piece => !charged.Contains(piece)),
                    $"{chargedOpen} of the {open.Count} associated parking spaces outside a covered car park {(chargedOpen == 1 ? "has" : "have")} a charge point - Approved Document S (S1(2), 1.1, 1.4) asks {fromOpen}: as many as there are dwellings, {dwellings}, or as there are spaces where there are fewer, outside a covered car park first");

            // Cable routes: in the covered car park, to make up what the open spaces cannot - all of them past 10 spaces (44D(4)); past 10
            // spaces with more spaces than dwellings, every space without a charge point (S1(3), 1.3).
            var coveredAsked = covered.Count == 0 ? 0 : total > Many ? covered.Count : Math.Max(0, needed - open.Count);
            var coveredRouted = covered.Count(bay => routed.Contains(bay.Piece) || charged.Contains(bay.Piece));
            if (coveredRouted < coveredAsked)
                yield return new MepProblem(First(covered, piece => !routed.Contains(piece) && !charged.Contains(piece)),
                    $"{coveredRouted} of the {covered.Count} spaces in the covered car park {(coveredRouted == 1 ? "has" : "have")} cable routes or a charge point - Approved Document S (44D(4), 1.2) asks {coveredAsked}{(total > Many ? ", all of them past 10 spaces" : ", to make up one a dwelling with the spaces outside it")}");

            if (total > Many && total > dwellings)
            {
                var without = bays.Where(bay => !charged.Contains(bay.Piece)).ToList();
                var bare = without.Count(bay => !routed.Contains(bay.Piece));
                if (bare > 0)
                    yield return new MepProblem(First(without, piece => !routed.Contains(piece)),
                        $"{bare} of the {total} associated parking spaces {(bare == 1 ? "has" : "have")} neither a charge point nor cable routes - Approved Document S (S1(3), 1.3) asks cable routes to every space without a charge point where there are more than 10 spaces and more spaces than dwellings, {dwellings}");
            }
        }
        else if (total > Many)
        {
            // A new building other than a residential one with more than 10 spaces (S4, Section 3): one charge point outside a covered car
            // park - none where all are covered (3.4) - and cable routes to one in five of the rest (3.1b).
            if (open.Count > 0 && chargedOpen == 0)
                yield return new MepProblem(First(open, _ => true), $"none of its {total} parking spaces has a charge point - Approved Document S (S4, 3.1a, 3.3) asks one charge point for a building other than a residential one with more than 10 spaces, outside a covered car park");
            var rest = open.Count > 0 ? total - 1 : total;
            var asked = (int)Math.Ceiling(rest / 5.0);
            var routes = bays.Count(bay => routed.Contains(bay.Piece)) + Math.Max(0, bays.Count(bay => charged.Contains(bay.Piece)) - 1);
            if (routes < asked)
                yield return new MepProblem(First(bays, piece => !routed.Contains(piece) && !charged.Contains(piece)),
                    $"{routes} of its other parking spaces {(routes == 1 ? "has" : "have")} cable routes - Approved Document S (S4, 3.1b, 3.4) asks at least one in five of them, {asked}");
        }

        // Where charge points or future connections are given and there are accessible spaces, one of those served (6.4).
        var accessible = bays.Where(bay => bay.Type.Form == ComponentForm.AccessibleParkingBay).ToList();
        if (accessible.Count > 0 && (charged.Count > 0 || routed.Count > 0) && !accessible.Any(bay => charged.Contains(bay.Piece) || routed.Contains(bay.Piece)))
            yield return new MepProblem(accessible[0].Piece, "none of the accessible parking spaces has a charge point or a future connection location - Approved Document S (6.4, Diagram 6.1) asks at least one of them to have one, where they are given");
    }

    /// <summary>
    /// Whether a parking bay is in a covered car park (Appendix A): in an enclosed room on its storey that is not a garage or a carport - a
    /// dwelling's own, or an open space under a roof, is not one.
    /// </summary>
    public static bool Covered(BimDocument document, Component bay) =>
        RoomsOf.Containing(document, bay.LevelId, bay.Location) is { } room && !Doors.Garage(document, room)
        && !room.Name.Contains("carport", StringComparison.OrdinalIgnoreCase) && !room.Name.Contains("car port", StringComparison.OrdinalIgnoreCase);

    /// <summary>The spaces some charge points or future connections serve: each the nearest spaces within reach of it, as many as its outlets.</summary>
    private static HashSet<Component> Serve(List<(Component Piece, ComponentType Type)> points, List<(Component Piece, IReadOnlyList<Point2D> Outline)> spaces)
    {
        var served = new HashSet<Component>();
        foreach (var (point, type) in points)
        {
            var near = spaces.Where(space => !served.Contains(space.Piece)).Select(space => (space.Piece, Distance: Apart(point.Location, space.Outline)))
                .Where(entry => entry.Distance <= Reach + 0.5).OrderBy(entry => entry.Distance).Take(Math.Max(1, type.Outlets));
            foreach (var (space, _) in near) served.Add(space);
        }

        return served;
    }

    /// <summary>How far a point is from a space's outline, mm: none inside it.</summary>
    private static double Apart(Point2D at, IReadOnlyList<Point2D> outline) =>
        Polygon2D.Contains(outline, at) ? 0 : Enumerable.Range(0, outline.Count).Min(i => Line2D.DistanceFromSegment(at, outline[i], outline[(i + 1) % outline.Count]));
}
