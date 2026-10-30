using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A group: elements kept together as one thing - a bathroom's fittings, a kitchen run, a
/// typical flat - picked, moved, turned, mirrored, copied and deleted as one, and placed again
/// as another of the same group. Every group of a name is the same group: one changed in Edit
/// Group, the others are made over to match it, each where it stands. Its frame - where it is,
/// how far it is turned and whether it is mirrored - is what carries a change from one to the
/// rest, so a group mirrored to the flat next door gets the change mirrored.
/// </summary>
public sealed class Group : Element
{
    public override BuiltInCategory Category => BuiltInCategory.ModelGroups;

    /// <summary>The group's name: every group of the name is the same group.</summary>
    public string Name { get; set; } = "Group 1";

    /// <summary>What is in it.</summary>
    public List<Guid> MemberIds { get; } = new();

    /// <summary>Where it is: the middle of what was in it when it was made, carried with it as it moves.</summary>
    public Point2D Origin { get; set; }

    /// <summary>How far it has been turned since it was made, degrees anticlockwise.</summary>
    public double Rotation { get; set; }

    /// <summary>Whether it is the mirror image of how it was made.</summary>
    public bool Mirrored { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        yield return ParameterValue.BindCommand<string>(GroupParameters.Name, () => Name, name => Groups.Rename(document, this, name));
        yield return ParameterValue.ReadOnly(GroupParameters.Members, () => (double)Groups.Members(document, this).Count);
        yield return ParameterValue.ReadOnly(GroupParameters.Placed, () => (double)Groups.Like(document, this).Count());
        yield return ParameterValue.ReadOnly(GroupParameters.Level, () => document.FindLevel(LevelId)?.Name ?? string.Empty);
        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    public override string ToString() => Name;
}

public static class GroupParameters
{
    public static readonly ParameterDefinition Name = new("Group Name", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Members = new("Elements in It", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Placed = new("Placed", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.IdentityData);
    public static readonly ParameterDefinition Level = new("Level", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);
}

public static class Groups
{
    /// <summary>The group an element is in, if it is in one.</summary>
    public static Group? Of(BimDocument document, Element? element) =>
        element is null or Group ? null : document.All<Group>().FirstOrDefault(group => group.MemberIds.Contains(element.Id));

    /// <summary>What is in a group, still in the project.</summary>
    public static IReadOnlyList<Element> Members(BimDocument document, Group group) =>
        group.MemberIds.Select(document.FindElement).OfType<Element>().ToList();

    /// <summary>A group and what is in it: what is picked when any of it is.</summary>
    public static IReadOnlyList<Element> Whole(BimDocument document, Group group) => new Element[] { group }.Concat(Members(document, group)).ToList();

    /// <summary>Every group placed of the same name, this one among them.</summary>
    public static IEnumerable<Group> Like(BimDocument document, Group group) =>
        document.All<Group>().Where(other => string.Equals(other.Name, group.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Group 1", "Group 2" - the next not used.</summary>
    public static string NextName(BimDocument document)
    {
        var used = document.All<Group>().Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        while (used.Contains($"Group {number}")) number++;
        return $"Group {number}";
    }

    /// <summary>
    /// What a group made of these would hold: what was picked that stands on its own, and what
    /// is hosted in it - a wall's doors and windows, a basin on it - whether picked or not.
    /// Doors and windows on their own, annotation and what is in a group already stay out.
    /// </summary>
    public static IReadOnlyList<Element> Gathered(BimDocument document, IEnumerable<Element> picked)
    {
        var members = picked.Where(element => element is not Group && ElementTransforms.CanMove(element) && !IsAnnotation(element) && Of(document, element) is null)
            .Distinct().ToList();
        var ids = members.Select(element => element.Id).ToHashSet();
        for (var added = true; added;)
        {
            added = false;
            foreach (var hosted in document.All<IHostedElement>().OfType<Element>())
                if (!ids.Contains(hosted.Id) && ids.Contains(((IHostedElement)hosted).HostId) && !IsAnnotation(hosted))
                {
                    members.Add(hosted);
                    ids.Add(hosted.Id);
                    added = true;
                }
        }

        return members;
    }

    private static bool IsAnnotation(Element element) => element.Category is BuiltInCategory.Dimensions or BuiltInCategory.Tags or BuiltInCategory.TextNotes
        or BuiltInCategory.Sections or BuiltInCategory.Sheets or BuiltInCategory.KeynoteTags or BuiltInCategory.RevisionClouds or BuiltInCategory.DetailLines or BuiltInCategory.FilledRegions;

    /// <summary>A group made of what is picked, as one step; null - and why - where nothing can go in one.</summary>
    public static Group? Make(BimDocument document, IEnumerable<Element> picked, Guid levelId, out string? why)
    {
        var members = Gathered(document, picked);
        why = null;
        if (members.Count == 0)
        {
            why = "Nothing picked can go in a group: pick walls, furniture, fittings, floors - doors and windows go in with their wall.";
            return null;
        }

        var box = BoundingBox2D.Empty;
        foreach (var member in members)
        foreach (var point in ElementTransforms.PlanPoints(member))
            box = box.Include(point);

        var group = new Group { Name = NextName(document), LevelId = levelId, Origin = box.IsEmpty ? new Point2D(0, 0) : box.Centre };
        group.MemberIds.AddRange(members.Select(member => member.Id));
        return group;
    }

    /// <summary>Every group of a name renamed, as one step; null where the name is empty or the same.</summary>
    public static IUndoableCommand? Rename(BimDocument document, Group group, string name)
    {
        var to = name.Trim();
        if (to.Length == 0 || to == group.Name) return null;

        var renamed = Like(document, group).ToList();
        var from = group.Name;
        return new DelegateCommand("Rename Group",
            () => renamed.ForEach(each => each.Name = to),
            () => renamed.ForEach(each => each.Name = from));
    }

    /// <summary>
    /// An element brought from where one group of a name has it to where another has it: out of
    /// the first's frame - back by where it is, turned back, mirrored back - and into the second's.
    /// </summary>
    public static void Reframe(Element element, Group from, Group to)
    {
        var origin = new Point2D(0, 0);
        var across = new Line2D(origin, new Vector2D(1, 0));
        ElementTransforms.Move(element, origin - from.Origin);
        ElementTransforms.Rotate(element, origin, -from.Rotation);
        if (from.Mirrored != to.Mirrored) ElementTransforms.Mirror(element, across);
        ElementTransforms.Rotate(element, origin, to.Rotation);
        ElementTransforms.Move(element, to.Origin - origin);
    }

    /// <summary>
    /// The other groups of an edited group's name made over to match it, each where it stands:
    /// what was in each taken out and a copy of what is in the edited one put in its place, as
    /// one step. Null where there are no others.
    /// </summary>
    public static IUndoableCommand? Propagate(BimDocument document, Group edited)
    {
        var others = Like(document, edited).Where(other => !ReferenceEquals(other, edited)).ToList();
        var source = Members(document, edited);
        if (others.Count == 0) return null;

        var steps = new List<IUndoableCommand>();
        foreach (var other in others)
        {
            // What it held - those hosted in another of it go with that one.
            var old = Members(document, other);
            var oldIds = old.Select(element => element.Id).ToHashSet();
            foreach (var element in old.Where(element => element is not IHostedElement hosted || !oldIds.Contains(hosted.HostId)))
                steps.Add(new DeleteElementCommand(document, element));

            // A copy of what the edited one holds, brought into this one's frame.
            var copies = ElementCopy.Duplicate(document, source);
            var copyIds = copies.Select(copy => copy.Id).ToHashSet();
            foreach (var copy in copies.Where(copy => !HostedCopies.Alone(copy, copyIds)))
            {
                Reframe(copy, edited, other);
                copy.LevelId = other.LevelId;
            }

            steps.AddRange(copies.Select(copy => (IUndoableCommand)new AddElementCommand(document, copy, "Edit Group")));
            var before = other.MemberIds.ToList();
            var after = copies.Select(copy => copy.Id).ToList();
            steps.Add(new DelegateCommand("Edit Group", () => Put(other, after), () => Put(other, before)));
        }

        return new CompositeCommand($"Edit Group {edited.Name}", steps);
    }

    private static void Put(Group group, List<Guid> members)
    {
        group.MemberIds.Clear();
        group.MemberIds.AddRange(members);
    }

    /// <summary>The group's frame moved, turned or mirrored as what is in it is - kept so a change carries over rightly.</summary>
    public static void Move(Group group, Vector2D delta) => group.Origin += delta;

    public static void Rotate(Group group, Point2D centre, double degrees)
    {
        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        var offset = group.Origin - centre;
        group.Origin = centre + new Vector2D(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
        group.Rotation = Normalised(group.Rotation + degrees);
    }

    /// <summary>
    /// A mirror about a line at an angle θ is a turn through 2θ after a mirror about the x axis:
    /// a group turned r and mirrored or not, mirrored again, is turned 2θ - r and the other way round.
    /// </summary>
    public static void Mirror(Group group, Line2D axis)
    {
        group.Origin = ElementTransforms.ReflectPoint(group.Origin, axis);
        var theta = Math.Atan2(axis.Direction.Y, axis.Direction.X) * 180 / Math.PI;
        group.Rotation = Normalised(2 * theta - group.Rotation);
        group.Mirrored = !group.Mirrored;
    }

    private static double Normalised(double degrees) => ((degrees % 360) + 360) % 360;
}
