using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A railing in 3D: its posts at its ends, corners and between; its top rail on them, round or
/// a flat capping; and under it its balusters on a bottom rail, its glass, or its rails along -
/// each bay standing on what is under it, so it follows a terrace's falls.
/// </summary>
public static class RailingModel
{
    private static readonly ColourRgb GlassTint = new(0xBF, 0xDC, 0xE6);

    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Railing railing)
    {
        var type = Railings.TypeOf(document, railing);
        if (Railings.Length(railing) < 1) return Array.Empty<Mesh3D>();

        var made = document.FindMaterial(type.MaterialId);
        var filled = document.FindMaterial(type.InfillMaterialId);
        var frame = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, made?.SurfaceColour ?? type.Colour, made?.Name ?? type.Name);
        var infill = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, filled?.SurfaceColour ?? made?.SurfaceColour ?? type.Colour, filled?.Name ?? made?.Name ?? type.Name);
        var glass = new Mesh3D(railing.Id, railing.LevelId, MeshKind.Railing, GlassTint, "Glass, Toughened Laminated") { Opacity = 0.35 };

        var height = type.Height;
        var depth = Railings.RailDepth(type);
        var halfPost = type.PostSize / 2;

        foreach (var post in Railings.Posts(railing, type))
        {
            var foot = Railings.Base(document, railing, post);
            frame.AddExtrusion(Square(post, halfPost), foot, foot + height - depth);
        }

        foreach (var (from, to) in Railings.Bays(railing, type))
        {
            var (atFrom, atTo) = (Railings.Base(document, railing, from), Railings.Base(document, railing, to));
            var along = (to - from).NormalisedOrDefault(Vector2D.UnitX);
            var across = along.PerpendicularLeft();
            var span = (to - from).Length;
            double Foot(Point2D point) => atFrom + (atTo - atFrom) * Math.Clamp((point - from).Dot(along) / span, 0, 1);

            // Its top rail, post to post.
            if (type.TopRailProfile == HandrailProfile.Round)
            {
                var radius = type.TopRailWidth / 2;
                frame.AddTube(new[] { At(from, atFrom + height - radius), At(to, atTo + height - radius) }, radius);
            }
            else frame.AddExtrusion(Strip(from, to, across, type.TopRailWidth / 2), point => Foot(point) + height - depth, point => Foot(point) + height);

            // Between its posts' faces.
            var (start, end) = (from + along * halfPost, to - along * halfPost);
            var clear = (end - start).Length;
            if (clear < 1) continue;

            switch (type.Infill)
            {
                case RailingInfill.Glass:
                    glass.AddExtrusion(Strip(start + along * 10, end - along * 10, across, type.GlassThickness / 2),
                        point => Foot(point) + type.BottomGap, point => Foot(point) + height - depth);
                    break;

                case RailingInfill.Balusters:
                {
                    var rail = type.BottomGap + Railings.BottomRail;
                    infill.AddExtrusion(Strip(start, end, across, Math.Max(type.BalusterSize, 20) / 2), point => Foot(point) + type.BottomGap, point => Foot(point) + rail);
                    var count = Math.Max(0, (int)Math.Ceiling(clear / Math.Max(20, type.BalusterSpacing) - 1e-9) - 1);
                    for (var k = 1; k <= count; k++)
                    {
                        var at = start + along * (clear * k / (count + 1));
                        infill.AddExtrusion(Square(at, type.BalusterSize / 2), Foot(at) + rail, Foot(at) + height - depth);
                    }

                    break;
                }

                case RailingInfill.HorizontalBars:
                {
                    var rail = type.BottomGap + Railings.BottomRail;
                    infill.AddExtrusion(Strip(start, end, across, Railings.BarSize / 2), point => Foot(point) + type.BottomGap, point => Foot(point) + rail);
                    var gap = Railings.BarGap(type);
                    for (var k = 1; k <= Railings.Bars(type); k++)
                    {
                        var low = rail + k * gap + (k - 1) * Railings.BarSize;
                        infill.AddExtrusion(Strip(start, end, across, Railings.BarSize / 2), point => Foot(point) + low, point => Foot(point) + low + Railings.BarSize);
                    }

                    break;
                }
            }
        }

        return new[] { frame, infill, glass }.Where(mesh => !mesh.IsEmpty).ToList();
    }

    private static Point3D At(Point2D point, double z) => new(point.X, point.Y, z);

    private static IReadOnlyList<Point2D> Square(Point2D centre, double half) => new[]
    {
        new Point2D(centre.X - half, centre.Y - half), new Point2D(centre.X + half, centre.Y - half),
        new Point2D(centre.X + half, centre.Y + half), new Point2D(centre.X - half, centre.Y + half)
    };

    private static IReadOnlyList<Point2D> Strip(Point2D from, Point2D to, Vector2D across, double half) => new[]
    {
        from - across * half, to - across * half, to + across * half, from + across * half
    };
}
