using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// How far a building's sides are from the boundary, and how much of each may be open to it - Approved Document B's space separation, so
/// a fire cannot spread across the boundary to the building next door: Volume 1 Section 11 for homes, Volume 2 Section 13 for any other
/// building (round 119).
///
/// Each side is measured to its relevant boundary (11.5, 13.5, Diagram 13.2): a boundary in front of it, parallel to it or at 80 degrees
/// to it at most - not one it runs square to. Its unprotected areas are its windows and doors that stand less than the building's Table B2
/// period, all of a curtain wall, and half of a wall whose outside face is worse than class B-s3, d2 and more than 1 mm thick (11.7,
/// 13.7, Diagram 13.4); the windows and doors of a protected stairway left out (11.10, 13.10), and small areas Diagram 13.5 lets be disregarded - 1 m²
/// at most within a metre square, 4 m from the next, or 0.1 m², 1.5 m from the next.
///
/// A wall less than 1 m from the boundary has only such small areas, and stands the building's period from both sides (11.8, 13.8, Table
/// B1 item 5a). Further off, Method 1 (Diagram 11.7, 13.7) for a building of homes - or in Volume 2 a residential (other) one - of three
/// storeys at most and 24 m long: 5.6 m² at 1 m, 12 at 2, 18 at 3, 24 at 4, 30 at 5, any at 6. Method 2 (Table 11.1, 13.1) for any
/// building 10 m high at most: the unprotected area as a share of the rectangle that encloses it all, 8% at 1 m to 100% at 12.5 for
/// residential, office and assembly buildings, 4% at 1 m to 100% at 25 for shops, industry and storage, interpolated - an open-sided car
/// park allowed the first column (note 3). Sprinklered throughout, the more of double the area and the area at twice the distance
/// (11.21, 13.22). A building taller than 10 m that Method 1 does not reach is left to BR 187 (11.16, 13.17), said, not judged.
///
/// Not done: notional boundaries between buildings on one site (13.5c); canopies (13.13, 13.14); roofs steeper than 70 degrees (13.15);
/// portal frames (13.16); a building's compartments taken one at a time (13.20) - a side is taken whole; uncompartmented walls over 30 m
/// (13.12); atria (13.23); a wall 1 m or more off standing the period from inside (13.9).
/// </summary>
public static class SpaceSeparation
{
    /// <summary>Which of the simple methods a building's sides are judged by.</summary>
    public enum Way
    {
        /// <summary>Method 1: a small building of homes, or of residential (other) use - Diagram 11.7, 13.7.</summary>
        One,

        /// <summary>Method 2: any building 10 m high at most - Table 11.1, 13.1.</summary>
        Two,

        /// <summary>Neither: taller than 10 m, and not small - BR 187.</summary>
        Neither
    }

    /// <summary>One unprotected area of a side, as its elevation shows it: across the side and up, mm; the area it counts for, m²; what it is.</summary>
    public sealed record Patch(Wall Wall, double Left, double Right, double Bottom, double Top, double Area, string What, bool Cladding = false)
    {
        /// <summary>How far apart two patches are in the elevation, mm: nothing where they touch or overlap.</summary>
        public double GapTo(Patch other)
        {
            var across = Math.Max(0, Math.Max(other.Left - Right, Left - other.Right));
            var up = Math.Max(0, Math.Max(other.Bottom - Top, Bottom - other.Top));
            return Math.Sqrt(across * across + up * up);
        }
    }

    /// <summary>
    /// One side of the building: the way it faces, its outside walls, how near the relevant boundary those 1 m or more from it come, mm, its
    /// unprotected area counted there, m², the patches counted, the rectangle enclosing them, m², and all its patches, small ones too.
    /// </summary>
    public sealed record Side(string Facing, IReadOnlyList<Wall> Walls, double Distance, double Unprotected, IReadOnlyList<Patch> Counted, double Enclosing, IReadOnlyList<Patch> All)
    {
        /// <summary>The unprotected area as a share of the rectangle enclosing it, %.</summary>
        public double Percent => Enclosing > 1e-9 ? 100 * Unprotected / Enclosing : 0;
    }

    /// <summary>What is worked out once for a model: the method, why, the building's period, Volume 2 or not, its purpose group's column and its sides.</summary>
    private sealed record Plan(Way Way, string Why, int Period, bool Volume2, bool Lower, IReadOnlyList<Side> Sides);

    /// <summary>The most unprotected area Diagram 11.7 allows a side at a distance from the boundary, m²; null for no limit at 6 m or more.</summary>
    public static double? Allowed(double metres) =>
        metres >= 6 ? null : metres < 1 ? 0 : new[] { 5.6, 12, 18, 24, 30 }[(int)Math.Floor(metres) - 1];

    /// <summary>
    /// As <see cref="Allowed(double)"/>, and for a building sprinklered throughout the more of the
    /// two 11.21 allows: double the area, or the area allowed twice as far from the boundary - a
    /// metre from it at the least.
    /// </summary>
    public static double? Allowed(double metres, bool sprinklered)
    {
        if (!sprinklered || metres < 1) return Allowed(metres);
        var (doubled, halved) = (Allowed(metres) * 2, Allowed(metres * 2));
        return doubled is null || halved is null ? null : Math.Max(doubled.Value, halved.Value);
    }

    /// <summary>Table 13.1's columns: residential, office, assembly and recreation (and Table 11.1); shop and commercial, industrial, storage.</summary>
    private static readonly (double Metres, double Percent)[] Higher = { (1, 8), (2.5, 20), (5, 40), (7.5, 60), (10, 80), (12.5, 100) },
        LowerRisk = { (1, 4), (2, 8), (5, 20), (10, 40), (15, 60), (20, 80), (25, 100) };

    /// <summary>
    /// The most unprotected area Table 11.1 or 13.1 allows a side at a distance from the boundary, as a share of the rectangle enclosing
    /// it, %, interpolated (note 1): the first column, or for shops, industry and storage the second - none under 1 m.
    /// </summary>
    public static double Percent(double metres, bool shopIndustryOrStorage)
    {
        var rows = shopIndustryOrStorage ? LowerRisk : Higher;
        if (metres < 1 - 1e-9) return 0;
        if (metres >= rows[^1].Metres) return 100;
        for (var i = 0; i < rows.Length - 1; i++)
            if (metres <= rows[i + 1].Metres)
                return rows[i].Percent + (rows[i + 1].Percent - rows[i].Percent) * (metres - rows[i].Metres) / (rows[i + 1].Metres - rows[i].Metres);
        return 100;
    }

    /// <summary>As <see cref="Percent(double, bool)"/>, sprinklered throughout the more of double the share and the share twice as far off (11.21, 13.22).</summary>
    public static double Percent(double metres, bool shopIndustryOrStorage, bool sprinklered) =>
        !sprinklered || metres < 1 ? Percent(metres, shopIndustryOrStorage) : Math.Max(Math.Min(100, 2 * Percent(metres, shopIndustryOrStorage)), Percent(2 * metres, shopIndustryOrStorage));

    /// <summary>The way a wall faces, as a compass point: the project's north up the page.</summary>
    private static string Facing(Vector2D outward)
    {
        var degrees = (Math.Atan2(outward.Y, outward.X) * 180 / Math.PI + 360) % 360;
        return degrees switch
        {
            >= 45 and < 135 => "north",
            >= 135 and < 225 => "west",
            >= 225 and < 315 => "south",
            _ => "east"
        };
    }

    /// <summary>
    /// How far a wall's outside face is from its relevant boundary, mm (Diagram 11.2, 13.2): out from the face to a boundary in front of it,
    /// parallel to it or at 80 degrees to it at most - not one it runs square to. Null with no property line, or none in front of it.
    /// </summary>
    public static double? DistanceToBoundary(BimDocument document, Wall wall) => FireSeparationDistance(document, wall);

    /// <summary>
    /// The way out of the building from a wall: away from the middle of its outside walls - whichever
    /// way round the wall was drawn.
    /// </summary>
    public static Vector2D Outward(BimDocument document, Wall wall)
    {
        var outside = document.Walls.Where(other => other.LevelId == wall.LevelId && other.Length > 1 && Outside(document, other)).ToList();
        var middle = outside.Count == 0 ? wall.Start.MidpointTo(wall.End)
            : new Point2D(outside.Average(other => (other.Start.X + other.End.X) / 2), outside.Average(other => (other.Start.Y + other.End.Y) / 2));
        var normal = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        return normal.Dot(wall.Start.MidpointTo(wall.End) - middle) >= 0 ? normal : normal * -1;
    }

    /// <summary>The period an unprotected area falls short of: the building's Table B2 period, 30 minutes at the least.</summary>
    private static int PeriodOf(BimDocument document) => Math.Max(30, FireDesign.PeriodOf(document).Minutes);

    /// <summary>A wall's unprotected area, m²: its windows and doors that stand less than the building's period, all of a curtain wall, half of a combustible face (11.7, 13.7).</summary>
    public static double Unprotected(BimDocument document, Wall wall)
    {
        var along = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        return PatchesOf(document, wall, along, wall.Start, PeriodOf(document)).Sum(patch => patch.Area);
    }

    /// <summary>A wall's outside face as 11.7 and 13.7 count it: its material and class where that is worse than B-s3, d2 and more than 1 mm thick; null otherwise.</summary>
    public static string? Combustible(BimDocument document, Wall wall)
    {
        if (document.IsCurtainWall(wall) || document.GetWallType(wall) is not { } type) return null;
        var face = type.Structure.Layers.FirstOrDefault(layer => layer.Function != LayerFunction.Membrane && layer.Thickness > 0);
        if (face is null || face.Thickness <= 1 + 1e-9 || document.FindMaterial(face.MaterialId) is not { } material) return null;
        return Euroclass.Of(material) is { } fire && !fire.Class.AtLeast(Euroclass.Bs3d2) ? $"{material.Name}, class {fire.Class}" : null;
    }

    /// <summary>
    /// Whether a room is a protected stairway, whose outside walls are left out of the unprotected area (11.10, 13.10): a stair's room,
    /// enclosed, whose walls other than outside ones, and the doors in them, stand 30 minutes.
    /// </summary>
    private static bool ProtectedStair(BimDocument document, Room? room)
    {
        if (room is null || !(room.Name.Contains("stair", StringComparison.OrdinalIgnoreCase) || Doors.StairRooms(document).Contains(room))) return false;
        var boundary = room.GetBoundary(document);
        return boundary.IsEnclosed && boundary.BoundingWalls.Where(other => !Outside(document, other)).All(other =>
            Doors.FireMinutes(document.GetWallType(other)?.FireRating ?? string.Empty) >= 30
            && document.All<Door>().Where(door => door.HostWallId == other.Id).All(door => Doors.FireMinutes(document.FindType<DoorType>(door.TypeId)?.FireRating ?? string.Empty) >= 30));
    }

    /// <summary>A wall's unprotected areas, placed across a side - from an origin, along a way - and up from the datum.</summary>
    private static List<Patch> PatchesOf(BimDocument document, Wall wall, Vector2D across, Point2D origin, int period)
    {
        var patches = new List<Patch>();
        double At(Point2D point) => (point - origin).Dot(across);
        var (bottom, top) = (wall.GetBaseElevation(document), wall.GetTopElevation(document));
        var (start, end) = (At(wall.Start), At(wall.End));
        var whole = wall.Length * (top - bottom) / 1e6;
        if (document.IsCurtainWall(wall))
        {
            patches.Add(new Patch(wall, Math.Min(start, end), Math.Max(start, end), bottom, top, whole, "glass"));
            return patches;
        }

        var curve = wall.LocationCurve;
        var (outward, reach) = (Outward(document, wall), (document.GetWallType(wall)?.Structure.TotalWidth ?? 0) / 2 + 150);
        var openings = 0.0;
        foreach (var opening in document.All<Opening>().Where(opening => opening.HostWallId == wall.Id && opening is Window or Door))
        {
            OpeningType? type = opening is Window window ? document.FindType<WindowType>(window.TypeId) : document.FindType<DoorType>(((Door)opening).TypeId);
            var width = opening.WidthOf(type);
            var (sill, height) = opening.Placed(document, type, wall);
            if (width <= 0 || height <= 0) continue;
            openings += width * height / 1e6;
            if (Doors.FireMinutes(type?.FireRating ?? string.Empty) >= period) continue;
            if (ProtectedStair(document, RoomsOf.Containing(document, wall.LevelId, curve.PointAt(opening.DistanceAlongWall) - outward * reach))) continue;
            var (a, b) = (At(curve.PointAt(Math.Max(0, opening.DistanceAlongWall - width / 2))), At(curve.PointAt(Math.Min(curve.Length, opening.DistanceAlongWall + width / 2))));
            patches.Add(new Patch(wall, Math.Min(a, b), Math.Max(a, b), bottom + sill, bottom + sill + height, width * height / 1e6, opening is Window ? "window" : "door"));
        }

        if (Combustible(document, wall) is { } face && whole - openings > 1e-6)
            patches.Add(new Patch(wall, Math.Min(start, end), Math.Max(start, end), bottom, top, (whole - openings) / 2, face, Cladding: true));
        return patches;
    }

    /// <summary>
    /// Whether an unprotected area may be disregarded (Diagram 11.5, 13.5): not more than 1 m² within a metre square, 4 m from every other
    /// on its side, or not more than 0.1 m², 1.5 m from every other. A combustible face never.
    /// </summary>
    public static bool Small(Patch patch, IEnumerable<Patch> side)
    {
        if (patch.Cladding) return false;
        var others = side.Where(other => !ReferenceEquals(other, patch)).ToList();
        if (patch.Area <= 0.1 + 1e-9 && others.All(other => patch.GapTo(other) >= 1500 - 0.5)) return true;
        return patch.Area <= 1 + 1e-9 && patch.Right - patch.Left <= 1000 + 0.5 && patch.Top - patch.Bottom <= 1000 + 0.5 && others.All(other => patch.GapTo(other) >= 4000 - 0.5);
    }

    /// <summary>Whether a wall is an outside wall of the building: an exterior wall's type, or a curtain wall.</summary>
    private static bool Outside(BimDocument document, Wall wall) =>
        document.IsCurtainWall(wall) || document.GetWallType(wall) is { Function: WallFunction.Exterior };

    /// <summary>Whether the building is an open-sided car park, purpose group 7(b): Table 13.1's first column (note 3) and no 10 m limit (13.21).</summary>
    private static bool CarPark(BimDocument document)
    {
        var words = Toilets.WordsOf(document.ProjectInformation.BuildingType);
        return words.Contains("car") && (words.Contains("park") || words.Contains("parking"));
    }

    /// <summary>The building's sides as they face the property line - none where there is no property line.</summary>
    public static IReadOnlyList<Side> Sides(BimDocument document) => PlanOf(document)?.Sides ?? Array.Empty<Side>();

    /// <summary>Which method the building's sides are judged by, and why; null with no property line.</summary>
    public static (Way Way, string Why)? MethodOf(BimDocument document) => PlanOf(document) is { } plan ? (plan.Way, plan.Why) : null;

    private static Plan? PlanOf(BimDocument document)
    {
        Plan? Work()
        {
            if (!document.All<PropertyLine>().Any(line => line.Boundary.Count >= 3)) return null;
            var period = PeriodOf(document);
            var volume2 = HorizontalEscape.Applies(document);
            var group = volume2 ? HorizontalEscape.Purpose(document) : (PurposeGroup?)null;
            var carPark = volume2 && CarPark(document);
            var lower = !carPark && group is PurposeGroup.ShopAndCommercial or PurposeGroup.Industrial or PurposeGroup.Storage;

            var groups = document.Walls.Where(wall => wall.Length > 1 && Outside(document, wall) && !wall.IsCurved)
                .Select(wall => (Wall: wall, Outward: Outward(document, wall))).GroupBy(entry => Facing(entry.Outward)).ToList();
            var sides = new List<Side>();
            var length = 0.0;
            foreach (var bunch in groups)
            {
                var walls = bunch.Select(entry => entry.Wall).ToList();
                var facing = bunch.Aggregate(new Vector2D(0, 0), (sum, entry) => sum + entry.Outward * entry.Wall.Length).NormalisedOrDefault(bunch.First().Outward);
                var across = facing.PerpendicularLeft();
                var origin = walls[0].Start;
                var ends = walls.SelectMany(wall => new[] { (wall.Start - origin).Dot(across), (wall.End - origin).Dot(across) }).ToList();
                length = Math.Max(length, ends.Max() - ends.Min());

                var distances = walls.ToDictionary(wall => wall, wall => DistanceToBoundary(document, wall) ?? double.MaxValue);
                var all = walls.SelectMany(wall => PatchesOf(document, wall, across, origin, period)).ToList();
                var far = walls.Where(wall => distances[wall] >= 1000).ToList();
                var counted = all.Where(patch => distances[patch.Wall] >= 1000 && !Small(patch, all)).ToList();
                var enclosing = counted.Count == 0 ? 0
                    : (counted.Max(patch => patch.Right) - counted.Min(patch => patch.Left)) * (counted.Max(patch => patch.Top) - counted.Min(patch => patch.Bottom)) / 1e6;
                var distance = (far.Count > 0 ? far : walls).Min(wall => distances[wall]);
                sides.Add(new Side(bunch.Key, walls, distance, counted.Sum(patch => patch.Area), counted, enclosing, all));
            }

            var (storeys, _) = FireDesign.HeightOf(document);
            var height = Combustibility.Heights(document).Building;
            var small = storeys <= 3 && length <= 24000 + 0.5;
            var size = $"{storeys} storey{(storeys == 1 ? "" : "s")}, {length / 1000:0.#} m long, {height / 1000:0.#} m high";
            var (book, one, two, table) = volume2 ? ("Approved Document B Volume 2", "13.19, Diagram 13.7", "13.21", "Table 13.1") : ("Approved Document B", "11.18, Diagram 11.7", "11.20", "Table 11.1");
            var column = !volume2 ? string.Empty
                : carPark ? ", its first column as note 3 lets an open-sided car park"
                : lower ? ", column 2 - shop and commercial, industrial, storage" : ", column 1 - residential, office, assembly and recreation";
            var (way, why) = (!volume2 || group == PurposeGroup.ResidentialOther) && small
                ? (Way.One, $"method 1 ({book} {one}): {size}")
                : height <= 10000 + 0.5 || carPark
                    ? (Way.Two, $"method 2 ({book} {two}, {table}{column}): {size}")
                    : (Way.Neither, $"{size} - neither simple method reaches it: BR 187 ({book} {(volume2 ? "13.17" : "11.16")})");
            return new Plan(way, why, period, volume2, lower, sides);
        }

        return ModelCache.Of(document) is { } cache ? cache.Keep("space separation", document, null, null, Work) : Work();
    }

    /// <summary>What a wall falls short of in its side's space separation - or, in the US, its fire separation: said, and the rule.</summary>
    public static IReadOnlyList<(string Said, string Rule)> Problems(BimDocument document, Wall wall, ClearanceStandard standard)
    {
        if (!Outside(document, wall) || wall.IsCurved) return Array.Empty<(string, string)>();
        if (standard.IsUs()) return IrcProblems(document, wall);
        if (PlanOf(document) is not { } plan || plan.Sides.FirstOrDefault(candidate => candidate.Walls.Contains(wall)) is not { } side) return Array.Empty<(string, string)>();

        var problems = new List<(string, string)>();
        var distance = DistanceToBoundary(document, wall) ?? double.MaxValue;
        var book = plan.Volume2 ? "Approved Document B Volume 2" : "Approved Document B";

        if (distance < 1000)
        {
            if (document.GetWallType(wall) is { } type && !document.IsCurtainWall(wall) && Doors.FireMinutes(type.FireRating) < plan.Period)
                problems.Add(($"it is {Units.Metres(distance, "0.00")} from the property line, {(Doors.FireMinutes(type.FireRating) is > 0 and var minutes ? $"rated {minutes} minutes" : "not fire-rated")}.",
                    plan.Volume2
                        ? $"{book} (13.8, Table B1 item 5a) asks a wall less than 1 m from the relevant boundary to resist fire from each side for the building's Table B2 period - {plan.Period} minutes here."
                        : $"{book} (11.8, Table B4) asks a wall less than 1 m from the boundary to resist fire for {plan.Period} minutes from both sides."));
            var open = side.All.Where(patch => patch.Wall == wall && !patch.Cladding && !Small(patch, side.All)).ToList();
            if (open.Count > 0)
                problems.Add(($"it is {Units.Metres(distance, "0.00")} from the property line, with {(document.IsCurtainWall(wall) ? "its glass" : $"{open.Count} opening{(open.Count == 1 ? "" : "s")} too big or too close together to be disregarded")} in it.",
                    $"{book} ({(plan.Volume2 ? "13.8, Diagram 13.5" : "11.8, Diagram 11.5")}) allows a wall less than 1 m from the boundary only small unprotected areas - 1 m² at most within a metre square, 4 m from the next, or 0.1 m², 1.5 m from the next."));
            return problems;
        }

        if (!side.Counted.Any(patch => patch.Wall == wall)) return problems;
        var sprinklered = document.ProjectInformation.Sprinklered;
        var metres = side.Distance / 1000;
        var cladding = side.Counted.Where(patch => patch.Cladding).Select(patch => patch.What).Distinct().ToList();
        var what = cladding.Count == 0 ? "of windows, glazed doors and glass"
            : $"unprotected - its openings and half its {string.Join("; ", cladding)} ({(plan.Volume2 ? "13.7" : "11.7")})";
        var where = $"its side, facing {side.Facing} {Units.Metres(side.Distance, "0.0")} from the property line,";
        switch (plan.Way)
        {
            case Way.One when Allowed(metres, sprinklered) is { } allowed && side.Unprotected > allowed + 0.005:
                var who = plan.Volume2 ? "a small residential (other) building's side" : FireDesign.UseOf(document) == BuildingUse.Flats ? "a small block of flats' side" : "a house's side";
                var (para, more) = plan.Volume2 ? ("13.19, Diagram 13.7", "13.22") : ("11.18, Diagram 11.7", "11.21");
                problems.Add(($"{where} has {Units.OfSquareMetres(side.Unprotected, "0.0")} {what}.",
                    sprinklered
                        ? $"{book} ({para}, and {more} for a building sprinklered throughout) allows {who} {Units.OfSquareMetres(allowed, "0.#")} of unprotected area there - smaller windows, fire-resisting glazing, or the building further from the boundary."
                        : $"{book} ({para}) allows {who} {Units.OfSquareMetres(allowed, "0.#")} of unprotected area there - smaller windows, fire-resisting glazing, sprinklers throughout ({more}), or the building further from the boundary."));
                break;
            case Way.Two:
                var share = Percent(metres, plan.Lower, sprinklered);
                if (side.Percent <= share + 0.05) break;
                var (two, table, sprinkle, fuller) = plan.Volume2 ? ("13.21", "Table 13.1", "13.22", "13.17") : ("11.20", "Table 11.1", "11.21", "11.16");
                var column = plan.Volume2 ? plan.Lower ? " column 2 - shop and commercial, industrial, storage" : " column 1 - residential, office, assembly and recreation" : string.Empty;
                problems.Add(($"{where} has {Units.OfSquareMetres(side.Unprotected, "0.0")} {what} - {side.Percent:0}% of the {Units.OfSquareMetres(side.Enclosing, "0.#")} rectangle enclosing it.",
                    $"{book} ({two}, {table}{column}{(sprinklered ? $", and {sprinkle} for a building sprinklered throughout" : string.Empty)}) allows {share:0.#}% of the rectangle enclosing a side's unprotected areas there - smaller or fewer openings, fire-resisting glazing{(sprinklered ? string.Empty : $", sprinklers throughout ({sprinkle})")}, the building further from the boundary, or BR 187's fuller method ({fuller})."));
                break;
        }

        return problems;
    }

    /// <summary>
    /// How far a wall's outside face is from the lot line as the IRC measures its fire separation
    /// distance, mm: out from the face to the line in front of it - not to one it runs square to,
    /// which R302.1 leaves out (exception 1). Null with no property line, or none in front of it.
    /// </summary>
    public static double? FireSeparationDistance(BimDocument document, Wall wall)
    {
        if (document.All<PropertyLine>().FirstOrDefault(line => line.Boundary.Count >= 3) is not { } property) return null;
        if (document.GetWallType(wall) is not { } type) return null;
        var half = type.Structure.TotalWidth / 2;
        var outward = Outward(document, wall);
        var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
        var boundary = property.Boundary;
        var nearest = double.MaxValue;
        for (var i = 0; i < boundary.Count; i++)
        {
            var (a, b) = (boundary[i], boundary[(i + 1) % boundary.Count]);
            if (Math.Abs(way.Dot((b - a).NormalisedOrDefault(Vector2D.UnitX))) < Math.Cos(80 * Math.PI / 180) - 1e-9) continue;
            for (var s = 0; s <= 8; s++)
            {
                var face = wall.PointAt(type.Structure, wall.Length * s / 8, 0) + outward * half;
                var on = Line2D.ClosestPointOnSegment(face, a, b);
                if ((on - face).Dot(outward) > 0) nearest = Math.Min(nearest, face.DistanceTo(on));
            }
        }

        return nearest < double.MaxValue ? nearest : null;
    }

    /// <summary>The IRC's lines for a wall near the lot line: 5 ft - 3 ft sprinklered - inside which it is rated an hour; 3 ft inside which it has no openings.</summary>
    public const double RatedWithin = 1524, RatedWithinSprinklered = 914, NoOpeningsWithin = 914;

    /// <summary>
    /// A US house's wall near its lot line, as IRC R302.1 has it: within 5 ft, rated 1 hour with
    /// exposure from both sides; no openings within 3 ft, and between 3 and 5 ft no more than 25%
    /// of the wall (Table R302.1(1)). Sprinklered throughout, rated 1 hour from the outside within
    /// 3 ft, no openings there, and any number beyond (Table R302.1(2)).
    /// </summary>
    private static IReadOnlyList<(string Said, string Rule)> IrcProblems(BimDocument document, Wall wall)
    {
        if (FireSeparationDistance(document, wall) is not { } distance) return Array.Empty<(string, string)>();

        var problems = new List<(string, string)>();
        var sprinklered = document.ProjectInformation.Sprinklered;
        var feet = $"{Units.Metres(distance, "0.00")}{(Units.Imperial ? "" : $" ({distance / 304.8:0.0} ft)")}";
        var curtain = document.IsCurtainWall(wall);
        var minutes = curtain ? 0 : Doors.FireMinutes(document.GetWallType(wall)?.FireRating ?? string.Empty);

        if (distance < (sprinklered ? RatedWithinSprinklered : RatedWithin) && minutes < 60)
            problems.Add(($"it is {feet} from the lot line, {(minutes > 0 ? $"rated {minutes} minutes" : "not fire-rated")}.",
                sprinklered
                    ? "The IRC (R302.1, Table R302.1(2)) asks a wall of a house sprinklered throughout less than 3 ft from the lot line to be rated 1 hour, tested with exposure from the outside."
                    : "The IRC (R302.1, Table R302.1(1)) asks a wall less than 5 ft from the lot line to be rated 1 hour, tested with exposure from both sides - or sprinklers throughout, which bring that to 3 ft (Table R302.1(2))."));

        var (count, open) = Openings(document, wall);
        var area = wall.Length * wall.GetHeight(document) / 1e6;
        var what = curtain ? "its glass" : $"{count} window{(count == 1 ? "" : "s")} and door{(count == 1 ? "" : "s")} in it";
        if (distance < NoOpeningsWithin && open > 0)
            problems.Add(($"it is {feet} from the lot line, with {what}.",
                $"The IRC ({(sprinklered ? "Table R302.1(2)" : "Table R302.1(1)")}) allows no openings in a wall less than 3 ft from the lot line."));
        else if (!sprinklered && distance < RatedWithin && area > 0 && open > area / 4 + 0.005)
            problems.Add(($"it is {feet} from the lot line, its openings {Units.OfSquareMetres(open, "0.0")} of its {Units.OfSquareMetres(area, "0.0")} - {open / area:0%}.",
                "The IRC (Table R302.1(1)) allows a wall 3 to 5 ft from the lot line openings of 25% of its area at most - or any, with sprinklers throughout (Table R302.1(2))."));
        return problems;
    }

    /// <summary>A wall's openings as the IRC counts them: every window and door in it, how many and how big, m² - all of a curtain wall.</summary>
    private static (int Count, double Area) Openings(BimDocument document, Wall wall)
    {
        if (document.IsCurtainWall(wall)) return (1, wall.Length * wall.GetHeight(document) / 1e6);
        var (count, area) = (0, 0.0);
        foreach (var opening in document.All<Opening>().Where(opening => opening.HostWallId == wall.Id))
        {
            OpeningType? type = opening switch
            {
                Window window => document.FindType<WindowType>(window.TypeId),
                Door door => document.FindType<DoorType>(door.TypeId),
                _ => null
            };
            if (opening is not (Window or Door)) continue;
            count++;
            area += opening.WidthOf(type) * opening.HeightOf(type) / 1e6;
        }

        return (count, area);
    }

    /// <summary>Each side's space separation - or, in the US, its fire separation distance - for the property line's Properties.</summary>
    public static string Summary(BimDocument document)
    {
        var sides = Sides(document);
        if (sides.Count == 0) return "No outside walls to measure";
        var sprinklered = document.ProjectInformation.Sprinklered;
        if (document.ProjectInformation.ClearanceStandard.IsUs())
            return string.Join("; ", sides.OrderBy(side => side.Facing).Select(side =>
            {
                var distance = side.Walls.Select(wall => FireSeparationDistance(document, wall)).OfType<double>().DefaultIfEmpty(double.MaxValue).Min();
                if (distance == double.MaxValue) return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]}: faces no lot line";
                var limit = distance < NoOpeningsWithin ? "1-hour walls, no openings"
                    : sprinklered ? "no limit"
                    : distance < RatedWithin ? "1-hour walls, openings up to 25%" : "no limit";
                return $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]} {distance / 304.8:0.0} ft: {limit}";
            }));

        var plan = PlanOf(document)!;
        var said = string.Join("; ", sides.OrderBy(side => side.Facing).Select(side =>
        {
            var metres = side.Distance / 1000;
            var name = $"{char.ToUpperInvariant(side.Facing[0])}{side.Facing[1..]}";
            if (side.Distance == double.MaxValue) return $"{name}: faces no boundary";
            if (metres < 1) return $"{name} {metres:0.0} m: only small openings";
            var open = Units.OfSquareMetres(side.Unprotected, "0.0");
            return plan.Way switch
            {
                Way.One => $"{name} {metres:0.0} m: {open} open, {(Allowed(metres, sprinklered) is { } most ? $"{Units.OfSquareMetres(most, "0.#")} allowed" : "no limit")}",
                Way.Two => $"{name} {metres:0.0} m: {open} open, {side.Percent:0}% of its rectangle, {Percent(metres, plan.Lower, sprinklered):0.#}% allowed",
                _ => $"{name} {metres:0.0} m: {open} open, {side.Percent:0}% of its rectangle"
            };
        }));
        return $"{char.ToUpperInvariant(plan.Why[0])}{plan.Why[1..]}. {said}";
    }
}
