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
