using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>The 3D view's meshes as its phasing and filters have them: faded, coloured, or - taken down - red and faint.</summary>
public partial class ModelView
{
    private void Restyle(IReadOnlyList<Mesh3D> meshes, Func<Element, ElementStyle> styleOf)
    {
        if (_document is null) return;
        var styles = new Dictionary<Guid, ElementStyle>();
        foreach (var mesh in meshes)
        {
            if (!styles.TryGetValue(mesh.OwnerId, out var style))
                styles[mesh.OwnerId] = style = _document.FindElement(mesh.OwnerId) is { } element ? styleOf(element) : default;
            if (style.IsPlain) continue;

            if (style.Colour is { } colour) mesh.Colour = colour;
            if (style.Halftone || style.Dashed) mesh.Opacity = Math.Min(mesh.Opacity, style.Dashed ? 0.3 : 0.45);
        }
    }
}

/// <summary>A comparison in 3D: each element coloured by how it has changed, what was removed red and faint where it stood, the rest faded.</summary>
public partial class ModelView
{
    private Rendering.ComparisonOverlay? _comparison;

    /// <summary>The comparison shown, or none.</summary>
    public Rendering.ComparisonOverlay? Comparison
    {
        get => _comparison;
        set
        {
            _comparison = value;
            Rebuild();
        }
    }

    private IReadOnlyList<Mesh3D> Compared(IReadOnlyList<Mesh3D> meshes, Rendering.ComparisonOverlay comparing)
    {
        if (_document is null) return meshes;
        foreach (var mesh in meshes)
        {
            var style = mesh.SourceId is { } source ? comparing.StyleOfLinked(mesh.OwnerId, source)
                : _document.FindElement(mesh.OwnerId) is { } element ? comparing.StyleOf(element) : default;
            if (style.Colour is { } colour) mesh.Colour = colour;
            if (style.Halftone) mesh.Opacity = Math.Min(mesh.Opacity, 0.25);
        }

        var removed = comparing.RemovedMeshes(_document);
        return removed.Count == 0 ? meshes : meshes.Concat(removed).ToList();
    }
}
