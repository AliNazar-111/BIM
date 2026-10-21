using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.UI.Controls;

/// <summary>A person picked on the plan shows what they see: the area in sight from their eyes, shaded, out to 30 m.</summary>
public partial class PlanView
{
    private static readonly Brush SightFill = Frozen(new SolidColorBrush(Color.FromArgb(0x38, 0xF2, 0xC2, 0x30)));
    private static readonly Pen SightEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xE0, 0xA0, 0x10))), 1.2));

    private (Guid Person, int Version, Point2D At, IReadOnlyList<Point2D> Area)? _sight;

    /// <summary>The area in sight of the one person picked, worked out once for where they stand and drawn under the rest.</summary>
    private void DrawSight(DrawingContext dc)
    {
        if (Document is not { } document || _selection is not [Component person] || person.LevelId != ActiveLevelId ||
            !Walker.IsPerson(document.FindType<ComponentType>(person.TypeId)))
            return;

        if (_sight is not { } kept || kept.Person != person.Id || kept.Version != _modelVersion || kept.At != person.Location)
        {
            var (area, _, _, _) = Sight.Of(document, person);
            _sight = kept = (person.Id, _modelVersion, person.Location, area);
        }

        if (kept.Area.Count < 3) return;
        var outline = new StreamGeometry();
        using (var context = outline.Open())
        {
            context.BeginFigure(ModelToScreen(kept.Area[0]), true, true);
            foreach (var point in kept.Area.Skip(1)) context.LineTo(ModelToScreen(point), true, false);
        }

        outline.Freeze();
        dc.DrawGeometry(SightFill, SightEdge, outline);
    }

}
