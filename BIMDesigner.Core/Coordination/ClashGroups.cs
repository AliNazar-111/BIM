using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Coordination;

/// <summary>
/// What a test's clashes are put together by, so the many that one change puts right are one thing to
/// look at: the element they share - a duct through seven beams, one duct to move - the storey, the
/// storey and the grid lines nearest, the two types, or nearness to each other.
/// </summary>
public enum ClashGrouping
{
    None,
    Element,
    Level,
    GridLocation,
    Types,
    Proximity
}

/// <summary>
/// Clashes put together as one issue: named, with what is said about them as a whole. Its status, who
/// has it, its priority and when it is due are its clashes' - set on it, set on each of them. Made by
/// a rule it keeps what it was made by, so a clash found later by the same joins it.
/// </summary>
public sealed class ClashGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "Group";

    /// <summary>What the rule put its clashes together by - "element:", "level:", "grid:", "types:", "near" - none for a group made by hand.</summary>
    public string? Key { get; set; }

    public List<ClashComment> Comments { get; } = new();
}

/// <summary>
/// Groups of clashes: made by a rule from those open and in no group yet - and by it again each time
/// the test runs - or by hand from those picked; a group's status the least advanced of its clashes',
/// so a group stays open while any of them is; its priority the highest, its due date the soonest.
/// </summary>
public static class ClashGroups
{
    /// <summary>A group's clashes, in the order the test keeps them.</summary>
    public static IReadOnlyList<Clash> Members(ClashTest test, ClashGroup group) => test.Results.Where(clash => clash.GroupId == group.Id).ToList();

    /// <summary>Where a group stands: its least advanced clash's status - new before active, reviewed, approved, resolved.</summary>
    public static ClashStatus Status(ClashTest test, ClashGroup group) =>
        Members(test, group) is { Count: > 0 } members ? members.Min(clash => clash.Status) : ClashStatus.Resolved;

    /// <summary>Whether a group still wants looking at: any of its clashes does.</summary>
    public static bool IsOpen(ClashTest test, ClashGroup group) => Members(test, group).Any(clash => clash.IsOpen);

    /// <summary>Its highest priority.</summary>
    public static ClashPriority Priority(ClashTest test, ClashGroup group) =>
        Members(test, group) is { Count: > 0 } members ? members.Max(clash => clash.Priority) : ClashPriority.Normal;

    /// <summary>Its soonest due date.</summary>
    public static DateOnly? Due(ClashTest test, ClashGroup group) => Members(test, group).Select(clash => clash.DueDate).Where(due => due is not null).Min();

    /// <summary>Who has it: whom its clashes are assigned to, if one; the most of them have, if several.</summary>
    public static string AssignedTo(ClashTest test, ClashGroup group) => Members(test, group)
        .Select(clash => clash.AssignedTo).Where(who => who.Length > 0)
        .GroupBy(who => who).OrderByDescending(who => who.Count()).ThenBy(who => who.Key, StringComparer.Ordinal).FirstOrDefault()?.Key ?? string.Empty;

    /// <summary>The middle of its clashes and the box round them.</summary>
    public static (Point3D Middle, Point3D Min, Point3D Max) Extent(IReadOnlyList<Clash> clashes)
    {
        if (clashes.Count == 0) return (new Point3D(0, 0, 0), new Point3D(0, 0, 0), new Point3D(0, 0, 0));
        var min = new Point3D(clashes.Min(clash => clash.Point.X), clashes.Min(clash => clash.Point.Y), clashes.Min(clash => clash.Point.Z));
        var max = new Point3D(clashes.Max(clash => clash.Point.X), clashes.Max(clash => clash.Point.Y), clashes.Max(clash => clash.Point.Z));
        return (new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2), min, max);
    }

    /// <summary>
    /// Puts a test's open clashes that are in no group yet together by a rule: each joining a group the
    /// rule made before whose key it shares, the rest grouped where two or more share one - a clash alone
    /// is left as it is. Returns how many clashes were put in a group.
    /// </summary>
    public static int Group(BimDocument document, ClashTest test, ClashGrouping by, double distance)
    {
        if (by == ClashGrouping.None) return 0;
        var loose = test.Results.Where(clash => clash.IsOpen && clash.GroupId is null).ToList();
        if (loose.Count == 0) return 0;
        var grouped = 0;

        void Join(Clash clash, ClashGroup group)
        {
            clash.GroupId = group.Id;
            loose.Remove(clash);
            grouped++;
        }

        ClashGroup Make(string name, string key)
        {
            var group = new ClashGroup { Name = name, Key = key };
            test.Groups.Add(group);
            return group;
        }

        switch (by)
        {
            case ClashGrouping.Element:
            {
                // Into a group of an element of its own first; then, again and again, the element most of the rest share.
                foreach (var clash in loose.ToList())
                    if (test.Groups.FirstOrDefault(group => group.Key == ElementKey(clash.ElementA) || group.Key == ElementKey(clash.ElementB)) is { } group)
                        Join(clash, group);

                while (loose.SelectMany(clash => new[] { clash.ElementA, clash.ElementB })
                           .GroupBy(id => id).Where(shared => shared.Count() >= 2)
                           .OrderByDescending(shared => shared.Count()).ThenBy(shared => ClashDetective.NameOf(document, shared.Key), StringComparer.Ordinal).ThenBy(shared => shared.Key)
                           .FirstOrDefault() is { } most)
                {
                    var group = Make(ClashDetective.NameOf(document, most.Key), ElementKey(most.Key));
                    foreach (var clash in loose.Where(clash => clash.ElementA == most.Key || clash.ElementB == most.Key).ToList()) Join(clash, group);
                }

                break;
            }

            case ClashGrouping.Proximity:
            {
                // Into a group of clashes near it first; the rest put together where each is within the distance of another.
                foreach (var clash in loose.ToList())
                    if (test.Groups.Where(group => group.Key == "near").FirstOrDefault(group => Members(test, group).Any(member => Apart(member.Point, clash.Point) <= distance)) is { } group)
                        Join(clash, group);

                while (loose.Count > 0)
                {
                    var cluster = new List<Clash> { loose[0] };
                    for (var i = 0; i < cluster.Count; i++)
                        cluster.AddRange(loose.Where(clash => !cluster.Contains(clash) && Apart(clash.Point, cluster[i].Point) <= distance).ToList());
                    if (cluster.Count < 2)
                    {
                        loose.Remove(cluster[0]);
                        continue;
                    }

                    var first = cluster.OrderBy(clash => clash.Name.Length).ThenBy(clash => clash.Name, StringComparer.Ordinal).First();
                    var group = Make(first.GridLocation.Length > 0 ? $"{first.Level} near {first.GridLocation}" : $"{first.Level} near {first.Name}", "near");
                    foreach (var clash in cluster) Join(clash, group);
                }

                break;
            }

            default:
            {
                foreach (var shared in loose.GroupBy(clash => KeyOf(document, test, clash, by)).ToList())
                {
                    var group = test.Groups.FirstOrDefault(group => group.Key == shared.Key.Key);
                    if (group is null && shared.Count() < 2) continue;
                    group ??= Make(shared.Key.Name, shared.Key.Key);
                    foreach (var clash in shared) Join(clash, group);
                }

                break;
            }
        }

        return grouped;
    }

    /// <summary>The clashes picked put together by hand as a group of their own, out of any they were in; none where fewer than two are picked.</summary>
    public static ClashGroup? GroupByHand(ClashTest test, IEnumerable<Guid> clashes, string? name = null)
    {
        var picked = test.Results.Where(clash => clashes.Contains(clash.Id)).ToList();
        if (picked.Count < 2) return null;
        var group = new ClashGroup { Name = string.IsNullOrWhiteSpace(name) ? $"Group {test.Groups.Count + 1}" : name.Trim() };
        test.Groups.Add(group);
        foreach (var clash in picked) clash.GroupId = group.Id;
        Tidy(test);
        return group;
    }

    /// <summary>Clashes taken out of their groups - a group left with none is taken away.</summary>
    public static void TakeOut(ClashTest test, IEnumerable<Guid> clashes)
    {
        var ids = clashes.ToHashSet();
        foreach (var clash in test.Results.Where(clash => ids.Contains(clash.Id))) clash.GroupId = null;
        Tidy(test);
    }

    /// <summary>A group taken apart: its clashes back as they were on their own.</summary>
    public static void Ungroup(ClashTest test, ClashGroup group)
    {
        foreach (var clash in test.Results.Where(clash => clash.GroupId == group.Id)) clash.GroupId = null;
        test.Groups.RemoveAll(each => each.Id == group.Id);
    }

    /// <summary>
    /// A status set on a group, set on each of its clashes - but a clash the test no longer finds stays
    /// resolved unless the group is approved, or put back to resolved.
    /// </summary>
    public static void SetStatus(ClashTest test, ClashGroup group, ClashStatus status)
    {
        foreach (var clash in Members(test, group))
            if (clash.Status != ClashStatus.Resolved || status is ClashStatus.Resolved or ClashStatus.Approved) clash.Status = status;
    }

    /// <summary>Groups whose clashes have all gone taken away, and clashes in a group there is no longer let go.</summary>
    public static void Tidy(ClashTest test)
    {
        var ids = test.Groups.Select(group => group.Id).ToHashSet();
        foreach (var clash in test.Results.Where(clash => clash.GroupId is { } id && !ids.Contains(id))) clash.GroupId = null;
        var used = test.Results.Select(clash => clash.GroupId).OfType<Guid>().ToHashSet();
        test.Groups.RemoveAll(group => !used.Contains(group.Id));
    }

    private static string ElementKey(Guid id) => $"element:{id}";

    private static double Apart(Point3D a, Point3D b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    /// <summary>What a rule groups a clash by, and the name a group of it goes by.</summary>
    private static (string Key, string Name) KeyOf(BimDocument document, ClashTest test, Clash clash, ClashGrouping by)
    {
        var level = clash.Level.Length > 0 ? clash.Level : "No level";
        return by switch
        {
            ClashGrouping.Level => ($"level:{clash.Level}", level),
            ClashGrouping.GridLocation => ($"grid:{clash.Level}|{clash.GridLocation}", clash.GridLocation.Length > 0 ? $"{level} at {clash.GridLocation}" : level),
            ClashGrouping.Types => TypesOf(document, test, clash),
            _ => (string.Empty, string.Empty)
        };
    }

    /// <summary>
    /// The two types a clash is between, each the element's type - or its category, without one - the first
    /// set's first; where either could be in either set, the same whichever way round they were found.
    /// </summary>
    private static (string Key, string Name) TypesOf(BimDocument document, ClashTest test, Clash clash)
    {
        (string Key, string Name, BuiltInCategory? Category) Type(Guid id)
        {
            var (model, element) = document.FindElement(id) is { } own ? (document, own) : ModelLinks.Find(document, id) is var (_, linked, found) ? (linked, found) : ((BimDocument?)null, (Element?)null);
            if (element is null) return ("gone", "(gone)", null);
            return model!.FindType<ElementType>(element.TypeId) is { } type
                ? ($"type:{type.Name}|{element.Category}", type.Name, element.Category)
                : ($"category:{element.Category}", EnumText.Humanise(element.Category), element.Category);
        }

        var (a, b) = (Type(clash.ElementA), Type(clash.ElementB));
        bool Either(BuiltInCategory? category) => category is { } each && test.SelectionA.Contains(each) && test.SelectionB.Contains(each);
        if ((Either(a.Category) || Either(b.Category) || a.Category == b.Category) && string.CompareOrdinal(a.Key, b.Key) > 0) (a, b) = (b, a);
        return ($"types:{a.Key}|{b.Key}", $"{a.Name} against {b.Name}");
    }
}
