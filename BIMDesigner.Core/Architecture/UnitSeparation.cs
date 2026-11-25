using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;

namespace BIMDesigner.Core.Architecture;

/// <summary>What a US home under the IRC is, for how its dwelling units are kept apart.</summary>
public enum UnitKind
{
    /// <summary>One dwelling: nothing between units to ask.</summary>
    None,

    /// <summary>A two-family dwelling - a duplex, side by side or one over the other (R302.3).</summary>
    TwoFamily,

    /// <summary>Townhouses: three or more units in a row, each from the foundation to the roof (R302.2).</summary>
    Townhouse
}

/// <summary>
/// The walls and floors between the dwelling units of a US home under the 2021 IRC (round 140), read from the model code's own text: R302.2
/// townhouses, R302.3 two-family dwellings, R302.4 penetrations. In a US project whose Building Type is a house (one- and two-family dwellings
/// and townhouses, R101.2).
///
/// <b>What it is</b>: a townhouse where the Building Type says so - townhouse, row house, terrace - or where three or more dwellings are named
/// on the rooms, or two lines of party walls part the building; a two-family dwelling where it says duplex, two-family or semi-detached, or two
/// dwellings are named, or one line of party walls or a separating floor parts it. A wall between units: its type's Party Wall ticked, or the
/// rooms either side named into different dwellings. A floor between units: its type's Separating ticked, or rooms over and under it in
/// different dwellings.
///
/// <b>Townhouses</b> (R302.2): each unit separated by two 1-hour walls (R302.2.1) - a double wall, two party walls side by side within 300 of
/// each other - or by a common wall rated from both sides 2 hours, 1 hour where an automatic sprinkler system to P2904 is provided (R302.2.2,
/// items 1 and 2), with no plumbing or mechanical equipment, ducts or vents in its cavity but a sprinkler's water-filled pipe; continuous from the
/// foundation to the underside of the roof sheathing (R302.2.3). A parapet 30 in (762) above the roof surfaces either side, above the lower
/// where they differ by no more than 30 in, none where they differ by more (R302.2.4) - unless the covering is Class C or better (ASTM E108,
/// UL 790) and the deck noncombustible or fire-retardant-treated wood 4 ft (1219) either side or 5/8 in Type X gypsum board under it, and no
/// opening or penetration in the roof within 4 ft of the wall - roof windows, chimneys and pipes up through it. A parapet rated as the wall,
/// faced noncombustible (R302.2.5) - its uppermost 18 in (457) - and where the roof slopes toward it at more than 2 in 12, as high as the roof
/// within 3 ft (914). Each unit structurally independent (R302.2.6): a double wall without sprinklers not crossed by one floor - a common wall
/// and a sprinklered building being exceptions 5 and 6. Units one over another are not townhouses (R202) - an apartment building's, the IBC's.
///
/// <b>Two-family dwellings</b> (R302.3): walls and floors between the units rated 1 hour, 1/2 hour in a building sprinklered throughout to P2904,
/// whether a lot line runs between them or not; the walls from the foundation to the underside of the roof sheathing - or stopped at a ceiling
/// of 5/8 in Type X gypsum board either side under an attic with a draft stop over the wall, a cavity barrier there (exception 2); and what
/// holds up a rated floor - an outside wall or one marked bearing - rated as long (R302.3.1).
///
/// <b>Penetrations</b> (R302.4): a pipe through any of these walls and floors firestopped - Penetrations' check, which now finds them.
///
/// Not done: a common wall tight against the outside walls' sheathing (R302.2.2) and its exception's two 2x studs; the 1/2 in gypsum board on
/// the framing holding up an attic's ceiling (R302.3 exception 2); membrane penetrations by electrical boxes (R302.4.2); townhouse sprinklers
/// themselves (R313.1), which most states strike from the code they adopt.
/// </summary>
public static class UnitSeparation
{
    /// <summary>R302.2.4's parapet, the zone either side of the wall, R302.2.5's reach of a roof sloping to it and its noncombustible face; a double wall's widest gap, mm.</summary>
    public const double Parapet = 762, Zone = 1219, SlopeReach = 914, NoncombustibleFace = 457, DoubleGap = 300;

    /// <summary>5/8 in Type X gypsum board, mm, taken as 15.5 at the least.</summary>
    public const double TypeX = 15.5;

    /// <summary>R302.2.5's steepest slope toward a parapet before it rises with the roof: 2 in 12.</summary>
    public const double SteepSlope = 2.0 / 12;

    /// <summary>Whether the IRC's rules are the ones: a US project for a house - one- or two-family, or townhouses (R101.2).</summary>
    public static bool Applies(BimDocument document) =>
        document.ProjectInformation.ClearanceStandard.IsUs() && FireDesign.UseOf(document) == BuildingUse.House;

    /// <summary>What the building is, for its units' separation: by its Building Type's words, else the dwellings named, else the party walls and separating floors.</summary>
    public static UnitKind Kind(BimDocument document)
    {
        if (!Applies(document)) return UnitKind.None;
        var words = Toilets.WordsOf(document.ProjectInformation.BuildingType ?? string.Empty);
        if (words.Any(word => word.StartsWith("townho", StringComparison.Ordinal) || word is "rowhouse" or "rowhouses" or "rowhome" or "rowhomes" or "terrace" or "terraced")
            || words.Contains("row") && words.Any(word => word.StartsWith("house", StringComparison.Ordinal) || word.StartsWith("home", StringComparison.Ordinal)))
            return UnitKind.Townhouse;
        if (words.Contains("duplex") || words.Contains("two") && words.Contains("family") || words.Contains("semi") || words.Contains("semidetached"))
            return UnitKind.TwoFamily;

        var named = Dwellings.Named(document).Count;
        if (named >= 3) return UnitKind.Townhouse;
        if (named == 2) return UnitKind.TwoFamily;
        var lines = Separations(document);
        if (lines >= 2) return UnitKind.Townhouse;
        return lines == 1 || document.All<Floor>().Any(floor => document.FindType<SlabType>(floor.TypeId) is { Separating: true }) ? UnitKind.TwoFamily : UnitKind.None;
    }

    /// <summary>How many lines of party walls part the building: walls typed Party Wall on one line, or side by side as a double wall, one line.</summary>
    private static int Separations(BimDocument document)
    {
        int Work()
        {
            var groups = new List<List<Wall>>();
            foreach (var wall in document.Walls.Where(wall => document.GetWallType(wall) is { Separating: true }))
            {
                var joined = groups.Where(group => group.Any(other => OnLine(wall, other) || Beside(document, wall, other))).ToList();
                var into = new List<Wall> { wall };
                foreach (var group in joined)
                {
                    into.AddRange(group);
                    groups.Remove(group);
                }

                groups.Add(into);
            }

            return groups.Count;
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("unit separation: lines", document, null, null, Work) : Work();
    }

    /// <summary>Whether a wall separates dwelling units: its type a party wall, or the rooms either side named into different dwellings.</summary>
    public static bool Separates(BimDocument document, Wall wall)
    {
        if (!Applies(document) || document.GetWallType(wall) is not { } type || document.IsCurtainWall(wall)) return false;
        if (type.Separating) return true;
        if (type.Function == WallFunction.Exterior) return false;
        var (one, other) = WallRules.RoomsBeside(document, wall);
        return one is not null && other is not null && Dwellings.Of(one).Length > 0 && Dwellings.Of(other).Length > 0 && !Dwellings.Same(one, other);
    }

    /// <summary>Whether a floor separates dwelling units: its type separating, or rooms over it and under it named into different dwellings.</summary>
    public static bool Separates(BimDocument document, Floor floor)
    {
        if (!Applies(document) || floor.Boundary.Count < 3) return false;
        if (document.FindType<SlabType>(floor.TypeId) is { Separating: true }) return true;
        if (document.FindLevel(floor.LevelId) is not { } level) return false;
        var below = document.Levels.Where(each => each.Elevation < level.Elevation - 1).OrderByDescending(each => each.Elevation).FirstOrDefault();
        if (below is null) return false;
        bool Apart(Room room, Guid other) =>
            Dwellings.Of(room).Length > 0 && floor.Contains(room.Location) && RoomsOf.Containing(document, other, room.Location) is { } facing
            && Dwellings.Of(facing).Length > 0 && !Dwellings.Same(room, facing);
        return document.All<Room>().Any(room => room.LevelId == level.Id && Apart(room, below.Id) || room.LevelId == below.Id && Apart(room, level.Id));
    }

    /// <summary>What a wall between units is asked: minutes, what it separates, said, and the rule.</summary>
    public static (int Minutes, string What, string Rule) Asked(BimDocument document, Wall wall)
    {
        var sprinklered = document.ProjectInformation.Sprinklered;
        if (Kind(document) != UnitKind.Townhouse)
            return (sprinklered ? 30 : 60, "the two dwelling units of a two-family dwelling",
                "The IRC (R302.3) asks the walls and floors between the dwelling units of a two-family dwelling to be rated 1 hour (ASTM E119 or UL 263) - 1/2 hour where the building is sprinklered throughout to P2904 - whether a lot line runs between them or not.");
        if (Partner(document, wall) is not null)
            return (60, "two townhouse units, one of a double wall",
                "The IRC (R302.2.1) asks each townhouse unit to be separated from the next by its own 1-hour fire-resistance-rated wall (ASTM E119 or UL 263) - two walls side by side.");
        return (sprinklered ? 60 : 120, "two townhouse units as their common wall",
            "The IRC (R302.2.2) asks a common wall between townhouse units to be rated 2 hours from both sides - 1 hour where an automatic sprinkler system to P2904 is provided - or each unit to have its own 1-hour wall (R302.2.1).");
    }

    /// <summary>"1 hour", "1/2 hour", "2 hours", "45 minutes".</summary>
    public static string Hours(int minutes) => minutes switch
    {
        30 => "1/2 hour",
        60 => "1 hour",
        _ when minutes % 60 == 0 => $"{minutes / 60} hours",
        _ => $"{minutes} minutes"
    };

    private static string Rated(int minutes) => minutes > 0 ? $"rated {Hours(minutes)}" : "not fire-rated";

    // --- Geometry -------------------------------------------------------------------------------

    private static Vector2D Way(Wall wall) => (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);

    private static double Half(BimDocument document, Wall wall) => (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2;

    /// <summary>Whether two walls stand on one line: parallel, the one's middle on the other's line.</summary>
    private static bool OnLine(Wall wall, Wall other) =>
        Math.Abs(Way(wall).Cross((other.End - other.Start).NormalisedOrDefault(Way(wall)))) < 0.02 && Math.Abs(Way(wall).Cross(other.Start.MidpointTo(other.End) - wall.Start)) < 50;

    /// <summary>How far two walls on one line overlap along it, mm.</summary>
    private static double Overlap(Wall wall, Wall other)
    {
        var way = Way(wall);
        double At(Point2D point) => (point - wall.Start).Dot(way);
        var (from, to) = (Math.Min(At(other.Start), At(other.End)), Math.Max(At(other.Start), At(other.End)));
        return Math.Min(wall.Length, to) - Math.Max(0, from);
    }

    /// <summary>Whether two walls stand side by side as a double wall: parallel, off each other's line, no more than 300 apart between their faces, overlapping, at heights that overlap.</summary>
    private static bool Beside(BimDocument document, Wall wall, Wall other)
    {
        if (other == wall || Math.Abs(Way(wall).Cross((other.End - other.Start).NormalisedOrDefault(Way(wall)))) > 0.02) return false;
        var offset = Math.Abs(Way(wall).Cross(other.Start.MidpointTo(other.End) - wall.Start));
        var faces = Half(document, wall) + Half(document, other);
        return offset >= 50 && offset <= faces + DoubleGap && Overlap(wall, other) > Math.Min(wall.Length, other.Length) / 2
               && Math.Min(wall.GetTopElevation(document), other.GetTopElevation(document)) > Math.Max(wall.GetBaseElevation(document), other.GetBaseElevation(document)) + 100;
    }

    /// <summary>The other wall of a double wall a wall is one of, where it is: a wall between the units beside it.</summary>
    public static Wall? Partner(BimDocument document, Wall wall) =>
        document.Walls.FirstOrDefault(other => Beside(document, wall, other) && (document.GetWallType(other) is { Separating: true } || Separates(document, other)));

    /// <summary>The walls on a wall's line stacked one on another with it - storey over storey, a parapet on the roof - lowest first.</summary>
    private static List<Wall> Column(BimDocument document, Wall wall)
    {
        var line = document.Walls.Where(other => other != wall && OnLine(wall, other) && Overlap(wall, other) > 100).ToList();
        var column = new List<Wall> { wall };
        var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        for (var round = 0; round < 20; round++)
        {
            var more = line.Where(other => !column.Contains(other)
                                           && (other.GetBaseElevation(document) <= top + 600 && other.GetTopElevation(document) > top + 1
                                               || other.GetTopElevation(document) >= bottom - 600 && other.GetBaseElevation(document) < bottom - 1)).ToList();
            if (more.Count == 0) break;
            column.AddRange(more);
            (bottom, top) = (column.Min(each => each.GetBaseElevation(document)), column.Max(each => each.GetTopElevation(document)));
        }

        return column.OrderBy(each => each.GetBaseElevation(document)).ToList();
    }

    /// <summary>The roofs over a point - not an extrusion - with their underside there.</summary>
    private static List<(Roof Roof, double Under)> RoofsOver(BimDocument document, Point2D at) =>
        document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && roof.Contains(at)).Select(roof => (roof, roof.UndersideAt(document, at))).ToList();

    /// <summary>Whether a material is noncombustible as a parapet's face asks: classed A1, or masonry, concrete, stucco, metal or fiber cement by its name.</summary>
    public static bool Noncombustible(Material material)
    {
        if (Euroclass.Of(material) is { } fire && fire.Class.AtLeast(Euroclass.A1)) return true;
        return new[] { "stucco", "masonry", "metal", "aluminum", "fiber cement", "fibre cement", "steel", "copper" }.Any(word => material.Name.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a layer is 5/8 in Type X gypsum board: gypsum named Type X, 15.5 mm thick at the least.</summary>
    public static bool IsTypeX(BimDocument document, MaterialLayer layer) =>
        layer.Thickness >= TypeX - 1e-6 && document.FindMaterial(layer.MaterialId)?.Name is { } name && name.Contains("type x", StringComparison.OrdinalIgnoreCase)
        && (name.Contains("gypsum", StringComparison.OrdinalIgnoreCase) || name.Contains("plasterboard", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a material is fire-retardant-treated wood by its name.</summary>
    private static bool FireRetardant(Material material) =>
        material.Name.Contains("fire-retardant", StringComparison.OrdinalIgnoreCase) || material.Name.Contains("fire retardant", StringComparison.OrdinalIgnoreCase)
        || Toilets.WordsOf(material.Name).Contains("frt");

    // --- Walls ----------------------------------------------------------------------------------

    /// <summary>What a wall between dwelling units falls short of, said, and the rule; and an outside or bearing wall holding up a floor between units.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Wall wall, WallType type)
    {
        if (!Applies(document)) yield break;
        if (!Separates(document, wall))
        {
            if (Supporting(document, wall, type) is { } holding) yield return holding;
            yield break;
        }

        var kind = Kind(document);
        var townhouse = kind == UnitKind.Townhouse;
        var (asked, what, rule) = Asked(document, wall);
        var minutes = Doors.FireMinutes(type.FireRating);
        if (minutes < asked) yield return ($"it separates {what}, {Rated(minutes)}.", rule);

        var partner = Partner(document, wall);
        var common = townhouse && partner is null;
        var (continuity, roofRule) = townhouse
            ? ("R302.2.3", "The IRC (R302.2.3) asks the fire-resistance-rated wall between townhouse units to be continuous from the foundation to the underside of the roof sheathing, deck or slab, its full length - attach its top to the roof.")
            : ("R302.3", "The IRC (R302.3) asks the walls between the dwelling units of a two-family dwelling to run from the foundation to the underside of the roof sheathing - or, under an attic, to stop at a ceiling of 5/8 in Type X gypsum board either side with a draft stop over the wall (exception 2) - attach its top to the roof.");

        // No plumbing or mechanical equipment, ducts or vents in a common wall's cavity, but a sprinkler's water-filled pipe (R302.2.2).
        if (common && InCavity(document, wall, type) is { Count: > 0 } services)
            yield return ($"{string.Join(" and ", services)} {(services.Count == 1 && services[0].StartsWith("a ", StringComparison.Ordinal) ? "runs" : "run")} in its cavity, the common wall between townhouse units.",
                "The IRC (R302.2.2) builds the common wall shared by two townhouse units without plumbing or mechanical equipment, ducts or vents in its cavity - water-filled fire sprinkler pipe aside.");

        // From the foundation: the lowest of its column no higher than the walls it meets at its ends go down (R302.2.3, R302.3).
        var column = Column(document, wall);
        if (column[0] == wall && !OverSeparatingFloor(document, wall))
        {
            var ends = column.SelectMany(each => new[] { each.Start, each.End }).ToList();
            var met = document.Walls.Where(other => !column.Contains(other) && !OnLine(wall, other)
                                                    && ends.Any(end => Line2D.DistanceFromSegment(end, other.Start, other.End) < Half(document, other) + Half(document, wall) + 50)).ToList();
            var bottom = wall.GetBaseElevation(document);
            if (met.Count > 0 && met.Min(other => other.GetBaseElevation(document)) is var lowest && lowest < bottom - 50)
                yield return ($"it stops {Units.FormatLength(bottom - lowest)} above the foundation the walls it meets go down to - nothing under it on the storeys below.",
                    townhouse
                        ? "The IRC (R302.2.3) asks the fire-resistance-rated wall between townhouse units to be continuous from the foundation to the underside of the roof sheathing, deck or slab."
                        : "The IRC (R302.3) asks the walls between the dwelling units of a two-family dwelling to run from the foundation to the underside of the roof sheathing.");
        }

        // Each unit standing on its own: no floor run on across a double wall without sprinklers (R302.2.6) - said by one wall of the pair.
        if (townhouse && partner is not null && !document.ProjectInformation.Sprinklered && wall.Id.CompareTo(partner.Id) < 0 && Crossed(document, wall, partner) is { } floor)
            yield return ($"it is one of a double wall between townhouse units, and the {document.FindType<SlabType>(floor.TypeId)?.Name ?? "floor"} runs on across both into the next unit.",
                "The IRC (R302.2.6) asks each townhouse unit to be structurally independent - foundations, sheathing fastened to the wall's framing, coverings and flashing aside; not asked of units separated by a common wall (R302.2.2) or with sprinklers (exceptions 5 and 6).");

        // To the roof, and where it meets it: the wall of its column under the roof answers for it.
        var middle = wall.Start.MidpointTo(wall.End);
        var roofs = RoofsOver(document, middle);
        if (roofs.Count == 0) yield break;
        var underside = roofs.Max(roof => roof.Under);
        var underRoof = column.Where(each => each.GetBaseElevation(document) < underside - 100).DefaultIfEmpty(wall).Last();
        if (underRoof != wall) yield break;
        var top = column.Max(each => each.GetTopElevation(document));
        var attached = column.Any(each => each.AttachedRoof(document) is not null);
        var samples = Enumerable.Range(1, 9).Select(i => wall.Start + Way(wall) * (wall.Length * i / 10)).ToList();
        var gap = attached ? 0 : samples.Select(at => RoofsOver(document, at) is { Count: > 0 } over ? over.Max(roof => roof.Under) - top : 0).DefaultIfEmpty(0).Max();
        if (gap > 50 && !(kind == UnitKind.TwoFamily && AtticStopped(document, wall, type, top)))
            yield return ($"it separates {(townhouse ? "townhouse units" : "the dwelling units")} and stops {Units.FormatLength(gap)} below the roof sheathing over it.", roofRule);

        if (townhouse && Parapets(document, wall, type, column, asked) is { } parapet) yield return parapet;
    }

    /// <summary>Whether a wall stands on a floor between dwelling units: the units one over the other, the floor separating them, not the wall.</summary>
    private static bool OverSeparatingFloor(BimDocument document, Wall wall)
    {
        var (middle, bottom) = (wall.Start.MidpointTo(wall.End), wall.GetBaseElevation(document));
        return document.All<Floor>().Any(floor => Math.Abs(floor.GetTopElevation(document) - bottom) < 400 && floor.Contains(middle) && Separates(document, floor));
    }

    /// <summary>The pipes and ducts running in a wall's cavity, said - not ones passing through it, nor a sprinkler's.</summary>
    private static List<string> InCavity(BimDocument document, Wall wall, WallType type)
    {
        var half = type.Structure.TotalWidth / 2 - 5;
        var (base_, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        bool Inside(Point3D at)
        {
            var plan = new Point2D(at.X, at.Y);
            var along = (plan - wall.Start).Dot(Way(wall));
            return along >= 0 && along <= wall.Length && Math.Abs(Way(wall).Cross(plan - wall.Start)) <= half && at.Z >= base_ - 1 && at.Z <= top + 1;
        }

        bool Runs(Point3D start, Point3D end) =>
            Enumerable.Range(0, 5).Select(i => i / 4.0)
                .Select(t => new Point3D(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t)).Count(Inside) >= 3;

        var found = new List<string>();
        var pipes = document.All<Pipe>().Where(pipe => pipe.Kind != SystemKind.Sprinkler && pipe.Length >= 1 && pipe.Ends(document) is var (start, end) && Runs(start, end)).ToList();
        var ducts = document.All<Duct>().Where(duct => duct.Length >= 1 && duct.Ends(document) is var (start, end) && Runs(start, end)).ToList();
        if (pipes.Count > 0) found.Add(pipes.Count == 1 ? $"a {pipes[0].Kind.Title().ToLowerInvariant()} pipe" : $"{pipes.Count} pipes");
        if (ducts.Count > 0) found.Add(ducts.Count == 1 ? $"a {ducts[0].Kind.Title().ToLowerInvariant()} duct" : $"{ducts.Count} ducts");
        return found;
    }

    /// <summary>
    /// Whether a two-family dwelling's wall may stop under its attic (R302.3 exception 2): a ceiling or floor of 5/8 in Type X gypsum board under
    /// it on both sides at the wall's top, and a cavity barrier - a draft stop - over the wall along it, up into the attic.
    /// </summary>
    private static bool AtticStopped(BimDocument document, Wall wall, WallType type, double top)
    {
        var across = Way(wall).PerpendicularLeft();
        var middle = wall.Start.MidpointTo(wall.End);
        var reach = type.Structure.TotalWidth / 2 + 300;
        bool Ceiled(Point2D at) =>
            document.All<Slab>().Where(slab => slab is Ceiling or Floor && slab.Boundary.Count >= 3 && slab.Contains(at) && Math.Abs(slab.GetBottomElevation(document) - top) < 600)
                .Any(slab => document.FindType<SlabType>(slab.TypeId) is { } slabType && slabType.Structure.Layers.Any(layer => IsTypeX(document, layer)));
        if (!Ceiled(middle + across * reach) || !Ceiled(middle - across * reach)) return false;
        return document.All<CavityBarrier>().Any(barrier =>
        {
            var way = Way(wall);
            if (barrier.Length < 1 || Math.Abs(way.Cross((barrier.End - barrier.Start).NormalisedOrDefault(way))) > 0.05 || Math.Abs(way.Cross(barrier.Start.MidpointTo(barrier.End) - wall.Start)) > type.Structure.TotalWidth / 2 + 150) return false;
            double At(Point2D point) => (point - wall.Start).Dot(way);
            var covered = Math.Min(wall.Length, Math.Max(At(barrier.Start), At(barrier.End))) - Math.Max(0, Math.Min(At(barrier.Start), At(barrier.End)));
            return covered >= wall.Length * 0.9 && (Cavities.Span(document, barrier) is not var (_, barrierTop) || barrierTop > top + 100);
        });
    }

    /// <summary>A floor running on across both walls of a double wall into the next unit, where one does, at the walls' heights.</summary>
    private static Floor? Crossed(BimDocument document, Wall wall, Wall partner)
    {
        var across = Way(wall).PerpendicularLeft();
        var middle = wall.Start.MidpointTo(wall.End);
        var offset = (partner.Start.MidpointTo(partner.End) - wall.Start).Dot(across);
        var side = Math.Sign(offset);
        var (beyond, behind) = (middle + across * (offset + side * (Half(document, partner) + 300)), middle - across * (side * (Half(document, wall) + 300)));
        // A floor at the wall's own top, or within its height: the storey's floor over it or one it passes.
        var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        return document.All<Floor>().FirstOrDefault(floor => floor.Boundary.Count >= 3 && floor.GetTopElevation(document) > bottom + 100 && floor.GetBottomElevation(document) < top + 400
                                                             && floor.Contains(beyond) && floor.Contains(behind));
    }

    /// <summary>
    /// Where a townhouse's wall between units meets the roof (R302.2.4, R302.2.5): a parapet high enough, rated and faced as asked; or without one
    /// the roof either side covered, decked and clear as the exception asks. Null where nothing is wanting.
    /// </summary>
    private static (string Said, string Rule)? Parapets(BimDocument document, Wall wall, WallType type, List<Wall> column, int asked)
    {
        var half = type.Structure.TotalWidth / 2;
        var across = Way(wall).PerpendicularLeft();
        var floor = document.FindLevel(wall.LevelId)?.Elevation ?? wall.GetBaseElevation(document);
        var top = column.Max(each => each.GetTopElevation(document));

        // The roof over the storey at a point: its top there.
        double? RoofTop(Point2D at) =>
            document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && roof.Contains(at))
                .Select(roof => (Roof: roof, Under: roof.UndersideAt(document, at))).Where(entry => entry.Under > floor + 1 && entry.Under < floor + 8000)
                .OrderBy(entry => entry.Under).Select(entry => (double?)entry.Roof.TopAt(document, at)).FirstOrDefault();

        // Along the wall: the roofs either side at its faces, and within 3 ft where they slope up away from it more steeply than 2 in 12.
        double? needed = null;
        var stepped = false;
        for (var i = 1; i <= 9; i++)
        {
            var at = wall.Start + Way(wall) * (wall.Length * i / 10);
            var sides = new[] { 1, -1 }.Select(sign => (Face: RoofTop(at + across * (sign * (half + 50))), Far: RoofTop(at + across * (sign * (half + SlopeReach))))).ToList();
            var faces = sides.Where(side => side.Face is not null).Select(side => side.Face!.Value).ToList();
            if (faces.Count == 0) continue;
            if (faces.Count == 2 && Math.Abs(faces[0] - faces[1]) > Parapet + 0.5)
            {
                stepped = true;
                continue;
            }

            var here = faces.Min() + Parapet;
            foreach (var (face, far) in sides)
                if (face is { } near && far is { } beyond && (beyond - near) / (SlopeReach - 50) > SteepSlope)
                    here = Math.Max(here, beyond);
            needed = Math.Max(needed ?? double.MinValue, here);
        }

        if (needed is null) return null;
        var parapetRule = "The IRC (R302.2.4) asks a parapet 30 in (762) above the roof surfaces either side of the wall between townhouse units - above the lower where they differ by 30 in or less; none where they differ by more - unless the roof covering is Class C or better (ASTM E108 or UL 790), the deck noncombustible or fire-retardant-treated wood 4 ft (1219) either side of the wall, or 5/8 in Type X gypsum board under it on ledgers, and no opening or penetration in the roof within 4 ft of the wall.";
        if (top >= needed.Value - 0.5)
        {
            // R302.2.5: the parapet - what of the column stands above the roof - rated as the wall, faced noncombustible at its top 18 in.
            var roofTop = needed.Value - Parapet;
            var above = column.Where(each => each.GetTopElevation(document) > roofTop + 1).ToList();
            var wanting = new List<string>();
            foreach (var piece in above)
            {
                if (document.GetWallType(piece) is not { } pieceType) continue;
                var minutes = Doors.FireMinutes(pieceType.FireRating);
                if (piece != wall && minutes < asked) wanting.Add($"the {pieceType.Name} over the roof is {Rated(minutes)}");
                var layers = pieceType.Structure.Layers.Where(layer => layer.Thickness > 0).ToList();
                if (piece.GetTopElevation(document) >= top - 1 && layers.Count > 0)
                    foreach (var face in new[] { layers[0], layers[^1] }.Distinct())
                        if (document.FindMaterial(face.MaterialId) is { } material && !Noncombustible(material))
                            wanting.Add($"its face of {material.Name} is not noncombustible");
            }

            return wanting.Count == 0 ? null
                : ($"it is carried up as a parapet between townhouse units, and {string.Join("; ", wanting.Distinct())}.",
                    "The IRC (R302.2.5) asks a parapet rated as the wall under it, its faces noncombustible for the uppermost 18 in (457) on any side next to a roof, coping and counterflashing included.");
        }

        // No parapet: the exception, roof by roof 4 ft either side.
        var middle = wall.Start.MidpointTo(wall.End);
        var wantingRoof = new List<string>();
        var roofs = new[] { 1, -1 }.Select(sign => middle + across * (sign * (half + Zone / 2)))
            .SelectMany(at => document.All<Roof>().Where(roof => !roof.IsExtrusion && roof.Boundary.Count >= 3 && roof.Contains(at) && roof.UndersideAt(document, at) > floor + 1 && roof.UndersideAt(document, at) < floor + 8000))
            .Distinct().ToList();
        foreach (var roof in roofs)
        {
            if (document.FindType<SlabType>(roof.TypeId) is not { } roofType) continue;
            var layers = roofType.Structure.Layers.Where(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0)
                .Select(layer => (Layer: layer, Material: document.FindMaterial(layer.MaterialId))).Where(entry => entry.Material is not null).ToList();
            if (layers.Count == 0)
            {
                wantingRoof.Add($"the {roofType.Name} has no covering given");
                continue;
            }

            var deck = layers.Count > 1 ? layers[1].Material : null;
            var noncombustibleDeck = deck is not null && Noncombustible(deck);
            if (UsRoofClass.Of(layers[0].Material!, noncombustibleDeck) is null)
                wantingRoof.Add($"its covering, the {layers[0].Material!.Name}, has no class given (ASTM E108 or UL 790)");
            var underDeck = layers.Skip(2).Any(entry => IsTypeX(document, entry.Layer));
            if (deck is not null && !noncombustibleDeck && !FireRetardant(deck) && !underDeck)
                wantingRoof.Add($"its deck, the {deck.Name}, is neither noncombustible nor fire-retardant-treated, with no 5/8 in Type X gypsum board under it");
        }

        // No opening or penetration within 4 ft: roof windows, chimneys, pipes up through the roof.
        double FromWall(Point2D at) => Line2D.DistanceFromSegment(at, wall.Start, wall.End) - half;
        var openings = new List<string>();
        var windows = document.All<RoofWindow>().Where(window => roofs.Any(roof => roof.Id == window.RoofId) && RoofWindows.Frame(document, window) is { } frame
                                                                 && (frame.Corners.Min(FromWall) < Zone - 0.5 || Polygon2D.Contains(frame.Corners, middle))).ToList();
        if (windows.Count > 0) openings.Add(windows.Count == 1 ? "a roof window" : $"{windows.Count} roof windows");
        var chimneys = document.All<Chimney>().Where(chimney => FromWall(chimney.Location) - Math.Max(chimney.Width, chimney.Depth) / 2 < Zone - 0.5
                                                                && roofs.Any(roof => roof.Contains(chimney.Location))).ToList();
        if (chimneys.Count > 0) openings.Add(chimneys.Count == 1 ? "a chimney" : $"{chimneys.Count} chimneys");
        var pipes = document.All<Pipe>().Where(pipe => pipe.Length >= 1 && pipe.Ends(document) is var (start, end) && (start.Z > end.Z ? start : end) is var high
                                                       && new Point2D(high.X, high.Y) is var plan && FromWall(plan) < Zone - 0.5
                                                       && roofs.Any(roof => roof.Contains(plan) && high.Z > roof.UndersideAt(document, plan) + 1)).ToList();
        if (pipes.Count > 0) openings.Add(pipes.Count == 1 ? "a pipe up through it" : $"{pipes.Count} pipes up through it");
        if (openings.Count > 0) wantingRoof.Add($"{string.Join(", ", openings)} within 4 ft (1219) of the wall");
        if (wantingRoof.Count == 0) return null;
        return ($"it separates townhouse units under the roof with no parapet 30 in (762) above it{(stepped ? " where the roofs either side are within 30 in of each other" : string.Empty)}, and {string.Join("; ", wantingRoof.Distinct())}.", parapetRule);
    }

    // --- Floors ---------------------------------------------------------------------------------

    /// <summary>What a floor between dwelling units falls short of, said, and the rule.</summary>
    public static IEnumerable<(string Said, string Rule)> Problems(BimDocument document, Floor floor, SlabType type)
    {
        if (!Separates(document, floor)) yield break;
        if (Kind(document) == UnitKind.Townhouse)
        {
            yield return ("it separates dwelling units one over another, in a building of three or more.",
                "The IRC (R202, R101.2) takes in townhouses whose every unit runs from the foundation to the roof with a yard or public way on two sides at least - three or more units, one over another, are an apartment building, under the IBC.");
            yield break;
        }

        var asked = document.ProjectInformation.Sprinklered ? 30 : 60;
        var minutes = Doors.FireMinutes(type.FireRating);
        if (minutes < asked)
            yield return ($"it separates the two dwelling units, {Rated(minutes)}.",
                "The IRC (R302.3) asks the floor between the dwelling units of a two-family dwelling to be rated 1 hour (ASTM E119 or UL 263) - 1/2 hour where the building is sprinklered throughout to P2904 - tight against the outside walls.");
    }

    /// <summary>
    /// An outside wall, or one marked bearing, holding up a floor between a two-family dwelling's units, rated less than the floor is asked
    /// (R302.3.1): said and the rule; null otherwise.
    /// </summary>
    private static (string Said, string Rule)? Supporting(BimDocument document, Wall wall, WallType type)
    {
        if (type.Function != WallFunction.Exterior && wall.StructuralUsage == StructuralUsage.NonBearing || document.IsCurtainWall(wall)) return null;
        var (middle, half, top, bottom) = (wall.Start.MidpointTo(wall.End), type.Structure.TotalWidth / 2, wall.GetTopElevation(document), wall.GetBaseElevation(document));
        foreach (var floor in document.All<Floor>())
        {
            var under = floor.GetBottomElevation(document);
            if (top < under - 50 || bottom > under - 100 || floor.Boundary.Count < 3) continue;
            var edge = Enumerable.Range(0, floor.Boundary.Count).Min(i => Line2D.DistanceFromSegment(middle, floor.Boundary[i], floor.Boundary[(i + 1) % floor.Boundary.Count]));
            if (!floor.Contains(middle) && edge > half + 100) continue;
            if (!Separates(document, floor) || Kind(document) != UnitKind.TwoFamily) continue;
            var asked = document.ProjectInformation.Sprinklered ? 30 : 60;
            var minutes = Doors.FireMinutes(type.FireRating);
            return minutes >= asked ? null
                : ($"it holds up the floor between the two dwelling units, {Rated(minutes)}.",
                    "The IRC (R302.3.1) asks what holds up a floor required to be fire-resistance rated between the dwelling units of a two-family dwelling to be rated as long, or longer.");
        }

        return null;
    }
}
