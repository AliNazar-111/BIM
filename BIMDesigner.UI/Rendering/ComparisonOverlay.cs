using BIMDesigner.Core.Coordination;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// A comparison shown on the views: what was added green, what changed amber, what only moved blue,
/// what was removed red - drawn from the issue before, where it was - and everything as it was faded.
/// For a linked model, its own elements are coloured and this project faded under them.
/// </summary>
public sealed class ComparisonOverlay
{
    public static readonly ColourRgb AddedColour = new(0x2F, 0xA8, 0x4F);
    public static readonly ColourRgb ChangedColour = new(0xE8, 0x9B, 0x1A);
    public static readonly ColourRgb MovedColour = new(0x3B, 0x82, 0xD6);
    public static readonly ColourRgb RemovedColour = new(0xD9, 0x3B, 0x2E);

    private static readonly ElementStyle Faded = new(Halftone: true);
    private readonly Dictionary<double, IReadOnlyList<(Point2D A, Point2D B, Guid Element)>> _removedCuts = new();
    private IReadOnlyList<Mesh3D>? _removedMeshes;

    public ComparisonOverlay(ModelComparison comparison, BimDocument before, ModelLink? link)
    {
        (Comparison, Before, Link) = (comparison, before, link);
        Removed = comparison.Elements.Where(change => change.Kind == ChangeKind.Removed).Select(change => change.Id).ToHashSet();
    }

    public ModelComparison Comparison { get; }

    /// <summary>The issue before, to draw what has gone from.</summary>
    public BimDocument Before { get; }

    /// <summary>The linked model compared; null where it is this project.</summary>
    public ModelLink? Link { get; }

    /// <summary>The elements gone since the issue before.</summary>
    public IReadOnlySet<Guid> Removed { get; }

    /// <summary>The kinds of change coloured: the others are faded as if as they were.</summary>
    public HashSet<ChangeKind> Kinds { get; } = new() { ChangeKind.Added, ChangeKind.Removed, ChangeKind.Moved, ChangeKind.Changed };

    public static ColourRgb ColourOf(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => AddedColour,
        ChangeKind.Moved => MovedColour,
        ChangeKind.Removed => RemovedColour,
        _ => ChangedColour
    };

    /// <summary>How an element of this project is drawn: by how it has changed; faded where it is as it was, or where a linked model is compared.</summary>
    public ElementStyle StyleOf(Element element)
    {
        if (Link is not null) return Faded;
        if (Comparison.Of(element.Id) is { } change && Kinds.Contains(change.Kind)) return new ElementStyle(Colour: ColourOf(change.Kind));
        return Comparison.Compared.Contains(element.Id) ? Faded : default;
    }

    /// <summary>How an element of a linked model is drawn: by how it has changed where it is that link compared; faded otherwise.</summary>
    public ElementStyle StyleOfLinked(Guid linkId, Guid elementId)
    {
        if (Link?.Id != linkId) return Faded;
        return Comparison.Of(elementId) is { } change && Kinds.Contains(change.Kind) ? new ElementStyle(Colour: ColourOf(change.Kind)) : Faded;
    }

    /// <summary>What was removed, in 3D, where it was - in this project, or where the link puts it - red and faint.</summary>
    public IReadOnlyList<Mesh3D> RemovedMeshes(BimDocument host)
    {
        if (!Kinds.Contains(ChangeKind.Removed) || Removed.Count == 0) return Array.Empty<Mesh3D>();
        if (_removedMeshes is null)
        {
            var built = ModelMeshBuilder.Build(Before, element => Removed.Contains(element.Id));
            if (Link is { } link)
            {
                var place = ModelLinks.Placement(host, link, Before);
                var level = host.Levels.OrderBy(each => each.Elevation).FirstOrDefault()?.Id ?? Guid.Empty;
                built = built.Select(mesh => mesh.Mapped(place, mesh.ElementId, mesh.ElementId, level, mesh.ElementId)).ToList();
            }

            _removedMeshes = built;
        }

        // Fresh each time: a view colours and fades the meshes it is given.
        return _removedMeshes.Select(mesh => mesh.Mapped(point => point)).Select(mesh =>
        {
            mesh.Colour = RemovedColour;
            mesh.Opacity = 0.4;
            return mesh;
        }).ToList();
    }

    /// <summary>What was removed from a linked model, cut at a plan's height, as lines.</summary>
    public IReadOnlyList<(Point2D A, Point2D B, Guid Element)> RemovedCut(BimDocument host, double height)
    {
        if (Link is null || !Kinds.Contains(ChangeKind.Removed) || Removed.Count == 0) return Array.Empty<(Point2D, Point2D, Guid)>();
        var key = Math.Round(height, 1);
        if (!_removedCuts.TryGetValue(key, out var lines)) _removedCuts[key] = lines = Sight.SliceAt(RemovedMeshes(host), height);
        return lines;
    }

    /// <summary>The storey of the issue before that stands for one of this project's: the same, or one of its name, or at its height.</summary>
    public Guid? LevelBefore(BimDocument host, Guid levelId)
    {
        if (Before.FindLevel(levelId) is { } same) return same.Id;
        if (host.FindLevel(levelId) is not { } level) return null;
        return Before.Levels.FirstOrDefault(each => each.Name == level.Name)?.Id
               ?? Before.Levels.Where(each => Math.Abs(each.Elevation - level.Elevation) < 1).Select(each => (Guid?)each.Id).FirstOrDefault();
    }
}
