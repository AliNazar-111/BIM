using System.Reflection;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Coordination;

/// <summary>
/// What a clash test looks for: solids running into each other (hard), coming nearer than they
/// should (clearance), the same thing drawn twice (duplicate), or - against the construction
/// schedule - something built before what holds it up (sequence).
/// </summary>
public enum ClashKind
{
    Hard,
    Clearance,
    Duplicate,
    Sequence
}

/// <summary>
/// Where a clash stands: found this run (new), found again (active), looked at (reviewed),
/// accepted as it is (approved), or gone (resolved).
/// </summary>
public enum ClashStatus
{
    New,
    Active,
    Reviewed,
    Approved,
    Resolved
}

public enum ClashPriority
{
    Low,
    Normal,
    High,
    Critical
}

/// <summary>A note left on a clash: who, when, what.</summary>
public sealed record ClashComment(DateTime Date, string Author, string Text);

/// <summary>
/// One clash: the two elements, where, by how much - a negative distance how far one runs into
/// the other, a positive one how near they come - and the issue it is: its status, who it is
/// assigned to, its priority, when it is due, and what has been said about it.
/// </summary>
public sealed class Clash
{
    public Guid Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public Guid ElementA { get; init; }

    public Guid ElementB { get; init; }

    public Point3D Point { get; set; }

    public double Distance { get; set; }

    public ClashStatus Status { get; set; } = ClashStatus.New;

    public DateTime Found { get; set; }

    public string Description { get; set; } = string.Empty;

    public string GridLocation { get; set; } = string.Empty;

    public string Level { get; set; } = string.Empty;

    public string AssignedTo { get; set; } = string.Empty;

    public ClashPriority Priority { get; set; } = ClashPriority.Normal;

    public DateOnly? DueDate { get; set; }

    public List<ClashComment> Comments { get; } = new();

    /// <summary>Whether it still wants looking at: not approved, not resolved.</summary>
    public bool IsOpen => Status is ClashStatus.New or ClashStatus.Active or ClashStatus.Reviewed;
}

/// <summary>
/// A clash test: what is checked against what - two sets of categories - what for, and how
/// closely; and what it found, kept from one run to the next so each clash keeps its status.
/// </summary>
public sealed class ClashTest
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Clash test";

    public ClashKind Kind { get; set; } = ClashKind.Hard;

    public List<BuiltInCategory> SelectionA { get; } = new();

    public List<BuiltInCategory> SelectionB { get; } = new();

    /// <summary>A hard clash: how far one must run into the other before it counts, mm. A clearance clash: how near they may come, mm.</summary>
    public double Tolerance { get; set; } = 10;

    /// <summary>Whether to leave out what is meant to meet: a door in its wall, a gutter on its roof, walls joined at a corner.</summary>
    public bool IgnoreHosted { get; set; } = true;

    public DateTime? LastRun { get; set; }

    public List<Clash> Results { get; } = new();
}

/// <summary>
/// Runs clash tests against the model.
///
/// Each element is the solid its 3D meshes make. A hard clash is two solids whose surfaces cut
/// through each other, or one inside the other, by more than the tolerance - measured as the
/// least extent of the region they share, so faces that only touch, as joined walls do, are no
/// clash. A clearance clash is two coming nearer than the tolerance. A duplicate is the same type
/// in the same place twice. What is meant to meet - a door and its wall, a fascia and its roof, a
/// footing and what stands on it, walls joined at a corner - is left out, as is anything
/// attached to the other.
///
/// Each run keeps the clashes it found before: one found again is active - or stays reviewed or
/// approved, as it was left - one no longer found is resolved, and one found for the first time,
/// or found again after it was resolved, is new.
/// </summary>
public static class ClashDetective
{
    /// <summary>How far two faces may sit into each other and still only be touching, mm.</summary>
    private const double Touch = 0.5;

    /// <summary>The tests a project starts with: structure through openings, fittings through walls, rainwater pipes across windows, duplicates.</summary>
    public static IReadOnlyList<ClashTest> Defaults()
    {
        ClashTest Test(string name, ClashKind kind, IEnumerable<BuiltInCategory> a, IEnumerable<BuiltInCategory> b, double tolerance = 10)
        {
            var test = new ClashTest { Name = name, Kind = kind, Tolerance = tolerance };
            test.SelectionA.AddRange(a);
            test.SelectionB.AddRange(b);
            return test;
        }

        var structure = new[] { BuiltInCategory.StructuralFraming, BuiltInCategory.StructuralColumns, BuiltInCategory.Columns };
        var openings = new[] { BuiltInCategory.Doors, BuiltInCategory.Windows, BuiltInCategory.RoofWindows };
        var fittings = new[]
        {
            BuiltInCategory.Furniture, BuiltInCategory.Casework, BuiltInCategory.PlumbingFixtures, BuiltInCategory.SpecialtyEquipment,
            BuiltInCategory.MechanicalEquipment, BuiltInCategory.ElectricalEquipment, BuiltInCategory.LightingFixtures, BuiltInCategory.GenericModels
        };
        var fabric = new[] { BuiltInCategory.Walls, BuiltInCategory.Columns, BuiltInCategory.StructuralColumns, BuiltInCategory.Stairs, BuiltInCategory.Ramps, BuiltInCategory.Railings };
        var built = Enum.GetValues<BuiltInCategory>().Where(Solidly).ToList();

        return new[]
        {
            Test("Structure through doors and windows", ClashKind.Hard, structure, openings),
            Test("Fittings and furniture through walls, columns and stairs", ClashKind.Hard, fittings, fabric),
            Test("Fittings and furniture into each other", ClashKind.Hard, fittings, fittings, 20),
            Test("Rainwater pipes across doors and windows", ClashKind.Hard, new[] { BuiltInCategory.Downpipes, BuiltInCategory.Gutters }, openings),
            Test("Structure into stairs", ClashKind.Hard, structure, new[] { BuiltInCategory.Stairs, BuiltInCategory.Ramps }),
            Test("Duplicates", ClashKind.Duplicate, built, built, 1)
        };
    }

    /// <summary>Whether a category is something built, with a solid to clash: not a view, a note or a datum.</summary>
    public static bool Solidly(BuiltInCategory category) => category is not (
        BuiltInCategory.Rooms or BuiltInCategory.Grids or BuiltInCategory.Dimensions or BuiltInCategory.Tags or BuiltInCategory.TextNotes or
        BuiltInCategory.Sections or BuiltInCategory.Sheets or BuiltInCategory.Profiles or BuiltInCategory.Views or BuiltInCategory.Elevations or
        BuiltInCategory.Callouts or BuiltInCategory.ViewTemplates or BuiltInCategory.RevisionClouds or BuiltInCategory.KeynoteTags or
        BuiltInCategory.SpotElevations or BuiltInCategory.PropertyLines or BuiltInCategory.Masses or BuiltInCategory.Topography or
        BuiltInCategory.WallOpenings or BuiltInCategory.ShaftOpenings or BuiltInCategory.Entourage or BuiltInCategory.RoomSeparationLines);

    /// <summary>Each element's solid, from its 3D meshes - a curtain panel's with its wall.</summary>
    // What one build of the model is made into for clash tests, kept with it: the tests run on it in turn find it made.
    private sealed record Prepared(BimDocument Document, Dictionary<Guid, ClashSolid> Solids, Lazy<Func<Guid, Guid, bool>> Related);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Mesh3D>, Prepared> PreparedFor = new();

    private static Prepared Prepare(BimDocument document, IReadOnlyList<Mesh3D>? meshes)
    {
        Prepared Make(IReadOnlyList<Mesh3D> built) => new(document, SolidsOf(document, built), new Lazy<Func<Guid, Guid, bool>>(() => Related(document)));
        if (meshes is null) return Make(ModelMeshBuilder.Build(document));

        var prepared = PreparedFor.GetValue(meshes, Make);
        return ReferenceEquals(prepared.Document, document) ? prepared : Make(meshes);
    }

    internal static Dictionary<Guid, ClashSolid> Solids(BimDocument document, IReadOnlyList<Mesh3D>? meshes = null) => Prepare(document, meshes).Solids;

    private static Dictionary<Guid, ClashSolid> SolidsOf(BimDocument document, IReadOnlyList<Mesh3D> meshes)
    {
        return meshes
            .Where(mesh => !mesh.IsEmpty)
            .GroupBy(mesh => document.FindElement(mesh.ElementId) is not null ? mesh.ElementId : mesh.OwnerId)
            .Where(group => document.FindElement(group.Key) is not null)
            .ToDictionary(group => group.Key, group => new ClashSolid(group));
    }

    // Every element-valued link an element holds - its host, its roof, the walls it is joined to -
    // found once per kind of element.
    private static readonly Dictionary<Type, List<PropertyInfo>> LinksByType = new();

    private static IEnumerable<Guid> Links(Element element)
    {
        List<PropertyInfo> properties;
        lock (LinksByType)
        {
            if (!LinksByType.TryGetValue(element.GetType(), out properties!))
                LinksByType[element.GetType()] = properties = element.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetIndexParameters().Length == 0)
                    .Where(property => property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?) || typeof(IEnumerable<Guid>).IsAssignableFrom(property.PropertyType))
                    .Where(property => property.Name is not ("Id" or "TypeId" or "LevelId" or "DesignOptionId") &&
                                       !property.Name.EndsWith("MaterialId", StringComparison.Ordinal) && !property.Name.EndsWith("LevelId", StringComparison.Ordinal) &&
                                       !property.Name.EndsWith("ProfileId", StringComparison.Ordinal))
                    .ToList();
        }

        foreach (var property in properties)
            switch (property.GetValue(element))
            {
                case Guid id when id != Guid.Empty: yield return id; break;
                case IEnumerable<Guid> ids:
                    foreach (var id in ids) yield return id;
                    break;
            }
    }

    /// <summary>Which elements are meant to meet: each linked to the other either way.</summary>
    internal static Func<Guid, Guid, bool> Related(BimDocument document)
    {
        var pairs = new HashSet<(Guid, Guid)>();
        foreach (var element in document.Elements)
        foreach (var other in Links(element))
        {
            pairs.Add((element.Id, other));
            pairs.Add((other, element.Id));
        }

        return (a, b) => pairs.Contains((a, b));
    }

    /// <summary>Runs a test, carrying the statuses of what it found before; returns the clashes open after it.</summary>
    public static IReadOnlyList<Clash> Run(BimDocument document, ClashTest test, DateTime now, IReadOnlyList<Mesh3D>? meshes = null)
    {
        var found = Find(document, test, meshes).ToList();
        var before = test.Results.ToDictionary(clash => clash.Id);
        var next = new List<Clash>();
        var number = test.Results.Select(clash => int.TryParse(clash.Name.Replace("Clash", string.Empty), out var n) ? n : 0).DefaultIfEmpty(0).Max();

        foreach (var (a, b, point, distance, said) in found)
        {
            var id = IdOf(test.Id, a, b);
            if (before.Remove(id, out var kept))
            {
                kept.Status = kept.Status switch
                {
                    ClashStatus.Resolved => ClashStatus.New,
                    ClashStatus.New => ClashStatus.Active,
                    _ => kept.Status
                };
                if (kept.Status == ClashStatus.New) kept.Found = now;
                (kept.Point, kept.Distance, kept.Description) = (point, distance, said);
                Place(document, kept);
                next.Add(kept);
                continue;
            }

            var clash = new Clash { Id = id, Name = $"Clash{++number}", ElementA = a, ElementB = b, Point = point, Distance = distance, Found = now, Description = said };
            Place(document, clash);
            next.Add(clash);
        }

        // Not found again: resolved - kept, so the record of it stays.
        foreach (var gone in before.Values)
        {
            gone.Status = ClashStatus.Resolved;
            next.Add(gone);
        }

        test.Results.Clear();
        test.Results.AddRange(next.OrderBy(clash => clash.Status == ClashStatus.Resolved).ThenBy(clash => clash.Distance));
        test.LastRun = now;
        return test.Results.Where(clash => clash.IsOpen).ToList();
    }

    /// <summary>A clash's id: the same whichever way round its two elements are found.</summary>
    public static Guid IdOf(Guid test, Guid a, Guid b)
    {
        var (first, second) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        var bytes = test.ToByteArray().Concat(first.ToByteArray()).Concat(second.ToByteArray()).ToArray();
        return new Guid(System.Security.Cryptography.MD5.HashData(bytes));
    }

    /// <summary>Where a clash is: its storey, and the grid lines nearest it.</summary>
    private static void Place(BimDocument document, Clash clash)
    {
        clash.Level = document.Levels.Where(level => level.Elevation <= clash.Point.Z + 1).OrderByDescending(level => level.Elevation).FirstOrDefault()?.Name
                      ?? document.Levels.OrderBy(level => level.Elevation).FirstOrDefault()?.Name ?? string.Empty;
        clash.GridLocation = GridLocation(document, new Point2D(clash.Point.X, clash.Point.Y));
    }

    /// <summary>The grid lines nearest a point, one each way - "C-3" - or none where the project has no grid.</summary>
    public static string GridLocation(BimDocument document, Point2D point)
    {
        var grids = document.All<Grid>().Where(grid => grid.Length > 1).ToList();
        string? Nearest(bool upright) => grids
            .Where(grid => grid.IsVertical == upright)
            .OrderBy(grid => Math.Abs((point - grid.Start).Cross(grid.Direction)))
            .FirstOrDefault()?.Name;
        var (across, up) = (Nearest(true), Nearest(false));
        return (across, up) switch
        {
            ({ } a, { } b) => $"{a}-{b}",
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => string.Empty
        };
    }

    /// <summary>The name an element goes by in a clash report: its type and mark.</summary>
    public static string NameOf(BimDocument document, Guid id)
    {
        if (document.FindElement(id) is not { } element) return "(gone)";
        var type = document.FindType<ElementType>(element.TypeId)?.Name;
        var what = type ?? element.Category.ToString();
        return string.IsNullOrWhiteSpace(element.Mark) ? what : $"{what} ({element.Mark})";
    }

    /// <summary>What a test finds: each pair, where, how far, and said.</summary>
    internal static IEnumerable<(Guid A, Guid B, Point3D Point, double Distance, string Said)> Find(BimDocument document, ClashTest test, IReadOnlyList<Mesh3D>? meshes = null)
    {
        if (test.Kind == ClashKind.Sequence)
        {
            foreach (var found in ConstructionSequence.Clashes(document, test)) yield return found;
            yield break;
        }

        var prepared = Prepare(document, meshes);
        var solids = prepared.Solids;
        var related = test.IgnoreHosted ? prepared.Related.Value : (_, _) => false;
        var setA = test.SelectionA.ToHashSet();
        var setB = test.SelectionB.ToHashSet();
        var elements = solids.Keys.Select(id => document.FindElement(id)!).ToList();
        var left = elements.Where(element => setA.Contains(element.Category)).ToList();
        var right = elements.Where(element => setB.Contains(element.Category)).ToList();
        var seen = new HashSet<(Guid, Guid)>();
        var reach = test.Kind == ClashKind.Clearance ? test.Tolerance : Touch;

        // Each tried only against those near it in plan, in the order they would all have been tried in.
        var near = new ClashGrid(right.Select(element => solids[element.Id]).ToList(), reach);

        foreach (var a in left)
        foreach (var b in near.Near(solids[a.Id]).Select(index => right[index]))
        {
            if (a.Id == b.Id || related(a.Id, b.Id)) continue;
            var key = a.Id.CompareTo(b.Id) < 0 ? (a.Id, b.Id) : (b.Id, a.Id);
            if (!seen.Add(key)) continue;
            var (one, other) = (solids[a.Id], solids[b.Id]);
            if (one.Gap(other) > reach) continue;

            switch (test.Kind)
            {
                case ClashKind.Duplicate:
                {
                    if (a.TypeId != b.TypeId || a.Category != b.Category || one.Count != other.Count) continue;
                    var off = Math.Max((one.Min - other.Min).Length, (one.Max - other.Max).Length);
                    if (off > Math.Max(test.Tolerance, 0.5)) continue;
                    var middle = (one.Min + one.Max) * 0.5;
                    yield return (a.Id, b.Id, middle.ToPoint(), -off, $"{NameOf(document, a.Id)} is drawn twice, {off:0} mm apart.");
                    break;
                }

                case ClashKind.Hard:
                {
                    if (Penetration(one, other) is not var (depth, at) || depth <= test.Tolerance) continue;
                    yield return (a.Id, b.Id, at.ToPoint(), -depth, $"{NameOf(document, a.Id)} runs {depth:0} mm into {NameOf(document, b.Id)}.");
                    break;
                }

                case ClashKind.Clearance:
                {
                    if (Penetration(one, other) is var (depth, at) && depth > Touch)
                    {
                        yield return (a.Id, b.Id, at.ToPoint(), -depth, $"{NameOf(document, a.Id)} runs {depth:0} mm into {NameOf(document, b.Id)}.");
                        continue;
                    }

                    var gap = one.Distance(other, test.Tolerance);
                    if (gap >= test.Tolerance) continue;
                    var middle = (Middle(one, other));
                    yield return (a.Id, b.Id, middle.ToPoint(), gap, $"{NameOf(document, a.Id)} is {gap:0} mm from {NameOf(document, b.Id)}, within the {test.Tolerance:0} mm it is to be kept clear by.");
                    break;
                }
            }
        }
    }

    /// <summary>The middle of where two boxes meet or come nearest.</summary>
    private static V Middle(ClashSolid one, ClashSolid other)
    {
        static double Mid(double lowA, double highA, double lowB, double highB) =>
            (Math.Max(lowA, lowB) + Math.Min(highA, highB)) / 2;
        return new V(Mid(one.Min.X, one.Max.X, other.Min.X, other.Max.X), Mid(one.Min.Y, one.Max.Y, other.Min.Y, other.Max.Y), Mid(one.Min.Z, one.Max.Z, other.Min.Z, other.Max.Z));
    }

    /// <summary>
    /// How far one solid runs into another, and the middle of where it does: the least extent of
    /// the region they share - its surfaces' crossings and each one's corners inside the other -
    /// or nothing where they do not cut into each other.
    /// </summary>
    private static (double Depth, V At)? Penetration(ClashSolid one, ClashSolid other)
    {
        var points = one.Crossings(other, Touch);
        points.AddRange(one.Vertices.Where(point => other.Inside(point) && other.Distance(point) > Touch));
        points.AddRange(other.Vertices.Where(point => one.Inside(point) && one.Distance(point) > Touch));
        if (points.Count < 2) return null;

        var (min, max) = (points[0], points[0]);
        foreach (var point in points)
        {
            min = new V(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y), Math.Min(min.Z, point.Z));
            max = new V(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y), Math.Max(max.Z, point.Z));
        }

        var extent = max - min;
        var depth = Math.Min(extent.X, Math.Min(extent.Y, extent.Z));

        // A region flat in one axis, where only surfaces meet with nothing inside, is no penetration.
        return depth <= Touch ? null : (depth, (min + max) * 0.5);
    }
}
