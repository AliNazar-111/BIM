using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Revision clouds - scallops round what changed, each about 8 mm long on the paper, with a
/// triangle carrying the revision's number - and keynote tags: a key in a box with its leader.
/// </summary>
public sealed partial class PlanRenderer
{
    /// <summary>How long a cloud's scallops are on the paper.</summary>
    public const double CloudArcOnPaper = 8;

    private Pen? _cloudPen;

    private Pen CloudPen => _cloudPen ??= Frozen(new Pen(_tagTextBrush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>A length on the paper as model millimetres, at the scale of the view being drawn.</summary>
    private double OnPaperAsModel(double paperMm) =>
        paperMm * (Document?.ViewSettings.ScaleOf(View) is { } scale and > 0 ? scale : 100);

    private void DrawRevisionCloud(DrawingContext dc, RevisionCloud cloud)
    {
        if (Document is null || cloud.Outline.Count < 2) return;
        var (drawCloud, drawTag) = cloud.Drawn(Document);
        var pen = IsSelected(cloud) ? _selectedPen : CloudPen;

        if (drawCloud)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
                foreach (var arc in cloud.Scallops(OnPaperAsModel(CloudArcOnPaper)))
                {
                    context.BeginFigure(ModelToScreen(arc[0]), false, false);
                    context.PolyLineTo(arc.Skip(1).Select(ModelToScreen).ToList(), true, true);
                }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        if (drawTag && cloud.TagAt is { } at && cloud.Revision(Document) is { } revision)
        {
            var centre = ModelToScreen(at);

            // The leader to the nearest corner of the cloud, from the triangle's edge.
            var nearest = cloud.Outline.Select(ModelToScreen).OrderBy(point => (point - centre).Length).First();
            var side = Paper(9);
            var height = side * Math.Sqrt(3) / 2;
            var toward = nearest - centre;
            if (toward.Length > height * 0.6)
            {
                toward.Normalize();
                dc.DrawLine(pen, centre + toward * (height * 0.45), nearest);
            }

            var triangle = new StreamGeometry();
            using (var context = triangle.Open())
            {
                context.BeginFigure(new Point(centre.X, centre.Y - height * 2 / 3), true, true);
                context.LineTo(new Point(centre.X + side / 2, centre.Y + height / 3), true, false);
                context.LineTo(new Point(centre.X - side / 2, centre.Y + height / 3), true, false);
            }

            triangle.Freeze();
            dc.DrawGeometry(_labelBackdrop, pen, triangle);
            var number = Text(Revisions.NumberOf(Document, revision), Paper(2.6), _tagTextBrush, bold: true);
            dc.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y + height / 3 - number.Height - Paper(0.6)));
        }
    }

    /// <summary>The box a keynote tag's text sits in, on this surface.</summary>
    public Rect KeynoteBox(KeynoteTag keynote)
    {
        if (Document is null) return Rect.Empty;
        var text = Text(keynote.Label(Document), Paper(2.5), _tagTextBrush, bold: keynote.Display == KeynoteDisplay.Key);
        var at = ModelToScreen(keynote.Position);
        var (padX, padY) = (Paper(1.2), Paper(0.8));
        return new Rect(at.X - text.Width / 2 - padX, at.Y - text.Height / 2 - padY, text.Width + padX * 2, text.Height + padY * 2);
    }

    private void DrawKeynoteTag(DrawingContext dc, KeynoteTag keynote)
    {
        if (Document is null) return;
        var label = keynote.Label(Document);
        var text = Text(label, Paper(2.5), label == "?" ? _looseDimensionBrush : _tagTextBrush, bold: keynote.Display == KeynoteDisplay.Key);
        var box = KeynoteBox(keynote);
        var pen = IsSelected(keynote) ? _selectedPen : _leaderPen;

        if (keynote.LeaderEnd is { } end)
        {
            var tip = ModelToScreen(end);

            // From the side of the box facing what it notes.
            var from = new Point(Math.Clamp(tip.X, box.Left, box.Right), Math.Clamp(tip.Y, box.Top, box.Bottom));
            if (tip.X < box.Left || tip.X > box.Right) from = new Point(tip.X < box.Left ? box.Left : box.Right, box.Top + box.Height / 2);
            dc.DrawLine(pen, from, tip);
            dc.DrawEllipse(_tagTextBrush, null, tip, Paper(0.5), Paper(0.5));
        }

        dc.DrawRectangle(_labelBackdrop, pen, box);
        dc.DrawText(text, new Point(box.X + (box.Width - text.Width) / 2, box.Y + (box.Height - text.Height) / 2));
    }

    /// <summary>The outline of a cloud being drawn, as it will be.</summary>
    public void DrawPendingCloud(DrawingContext dc, IReadOnlyList<Point2D> outline)
    {
        if (outline.Count < 2) return;
        var preview = new RevisionCloud();
        preview.Outline.AddRange(outline.Count == 2 ? Rectangle(outline[0], outline[1]) : outline);
        foreach (var arc in preview.Scallops(OnPaperAsModel(CloudArcOnPaper)))
            for (var i = 0; i + 1 < arc.Count; i++) dc.DrawLine(_previewPen, ModelToScreen(arc[i]), ModelToScreen(arc[i + 1]));
    }

    /// <summary>A rectangle by two opposite corners, as a cloud's outline.</summary>
    public static IReadOnlyList<Point2D> Rectangle(Point2D a, Point2D b) => new[]
    {
        a, new Point2D(b.X, a.Y), b, new Point2D(a.X, b.Y)
    };
}
