using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Views;
using CoreMaterial = BIMDesigner.Core.Materials.Material;
using MediaPoint3D = System.Windows.Media.Media3D.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// The realistic style: each face in its material's pattern - brick in bond, boards, slates in
/// courses - laid along it and up it at its true size; and what a rendering is made from: what the
/// view shows, whether its ground is shown, and where it looks from.
/// </summary>
public partial class ModelView
{
    /// <summary>Each mesh's faces with their places in its pattern, and the pattern; none where it is a plain colour.</summary>
    private readonly Dictionary<Mesh3D, (MeshGeometry3D Geometry, SurfaceTexture Texture)?> _textured = new();

    private IReadOnlyDictionary<string, CoreMaterial>? _materialsByName;

    private static readonly ConditionalWeakTable<SurfaceTexture, ImageSource> Pictures = new();

    private static readonly Dictionary<(SurfaceTexture, double), Brush> TileBrushes = new();

    /// <summary>Patterns worked out again: the materials or the model changed.</summary>
    private void ForgetTextures()
    {
        _textured.Clear();
        _materialsByName = null;
    }

    /// <summary>The patterns the meshes shown are in, drawn side by side on the processor's cores before they are needed.</summary>
    private void PrepareTextures()
    {
        _materialsByName ??= _document is null ? new Dictionary<string, CoreMaterial>() : SurfacePatterns.ByName(_document);
        var finishes = _meshes.Where(mesh => mesh.Kind != MeshKind.Glazing && !_textured.ContainsKey(mesh))
            .Select(mesh => SurfacePatterns.Of(mesh, _materialsByName))
            .Where(finish => finish.Pattern != Core.Materials.SurfacePattern.None)
            .Distinct()
            .ToList();
        if (finishes.Count > 1) Parallel.ForEach(finishes, finish => SurfaceTextures.Of(finish));
    }

    /// <summary>A mesh's faces with each corner's place in its pattern, in repeats of the pattern's picture; none for a plain colour.</summary>
    private (MeshGeometry3D Geometry, SurfaceTexture Texture)? Textured(Mesh3D mesh)
    {
        if (_textured.TryGetValue(mesh, out var known)) return known;
        _materialsByName ??= _document is null ? new Dictionary<string, CoreMaterial>() : SurfacePatterns.ByName(_document);
        var texture = mesh.Kind == MeshKind.Glazing ? null : SurfaceTextures.Of(SurfacePatterns.Of(mesh, _materialsByName));
        if (texture is null) return _textured[mesh] = null;

        // Each triangle its own corners, so each lies in the pattern by the way it faces.
        var (positions, indices, normals) = (mesh.Positions, mesh.Indices, mesh.Normals);
        var count = indices.Count / 3 * 3;
        var points = new MediaPoint3D[count];
        var places = new System.Windows.Point[count];
        var smooth = normals is { } list && list.Count == positions.Count ? new Vector3D[count] : null;
        for (var i = 0; i < count; i += 3)
        {
            var (a, b, c) = (positions[indices[i]], positions[indices[i + 1]], positions[indices[i + 2]]);
            var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            var (nx, ny, nz) = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
            for (var k = 0; k < 3; k++)
            {
                var p = positions[indices[i + k]];
                points[i + k] = new MediaPoint3D(p.X / MillimetresPerUnit, p.Y / MillimetresPerUnit, p.Z / MillimetresPerUnit);
                var (u, v) = TextureMapping.Of(p.X, p.Y, p.Z, nx, ny, nz);
                places[i + k] = new System.Windows.Point(u / texture.Length, -v / texture.Rise);
                if (smooth is not null)
                {
                    var n = normals![indices[i + k]];
                    smooth[i + k] = new Vector3D(n.X, n.Y, n.Z);
                }
            }
        }

        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(points),
            TriangleIndices = new Int32Collection(Enumerable.Range(0, count)),
            TextureCoordinates = new PointCollection(places)
        };
        if (smooth is not null) geometry.Normals = new Vector3DCollection(smooth);
        geometry.Freeze();
        return _textured[mesh] = (geometry, texture);
    }

    /// <summary>A pattern's picture, as the screen draws it.</summary>
    public static ImageSource PictureOf(SurfaceTexture texture) => Pictures.GetValue(texture, made =>
    {
        var bitmap = BitmapSource.Create(made.Width, made.Height, 96, 96, PixelFormats.Rgb24, null, made.Pixels, made.Width * 3);
        bitmap.Freeze();
        return bitmap;
    });

    /// <summary>A face in its pattern, repeated across it, as faded as the mesh; lit up when selected.</summary>
    private Material TexturedMaterial(Mesh3D mesh, SurfaceTexture texture)
    {
        var opacity = Math.Round(mesh.Opacity, 2);
        if (!TileBrushes.TryGetValue((texture, opacity), out var brush))
        {
            if (TileBrushes.Count > 512) TileBrushes.Clear();
            brush = new ImageBrush(PictureOf(texture))
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                Stretch = Stretch.Fill,
                Opacity = opacity
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            TileBrushes[(texture, opacity)] = brush;
        }

        Material material = new DiffuseMaterial(brush);
        if (_selected.Contains(mesh.ElementId) || _selected.Contains(mesh.OwnerId))
        {
            var highlight = new SolidColorBrush(Color.FromArgb(0x90, 0x2E, 0x7F, 0xE0));
            highlight.Freeze();
            material = new MaterialGroup { Children = { material, new EmissiveMaterial(highlight) } };
        }

        material.Freeze();
        return material;
    }

    // ---- what a rendering is made from ------------------------------------------------------------

    /// <summary>What the view shows as it stands: its filters, hidden storeys and kinds, and its section box's cut.</summary>
    public IReadOnlyList<Mesh3D> ShownMeshes() => _meshes.Where(mesh => mesh.ElementId != _previewId && _view.Shows(mesh)).ToList();

    /// <summary>Whether the view shows the ground under the model.</summary>
    public bool GroundShown => _view.ShowGround && _view.ActiveBox is null && _view.Style != VisualStyle.Wireframe;

    /// <summary>Where the view looks from, as it is on the screen: in perspective by its field of view, or orthographic by its width.</summary>
    public Camera3D CurrentCamera()
    {
        var camera = (ProjectionCamera)_viewport.Camera;
        var look = camera.LookDirection;
        if (look.Length < 1e-12) look = new Vector3D(0, 1, 0);
        look.Normalize();
        var eye = new Core.Geometry.Point3D(camera.Position.X * MillimetresPerUnit, camera.Position.Y * MillimetresPerUnit, camera.Position.Z * MillimetresPerUnit);
        var distance = Math.Max(_distance * MillimetresPerUnit, 1000);
        var target = new Core.Geometry.Point3D(eye.X + look.X * distance, eye.Y + look.Y * distance, eye.Z + look.Z * distance);
        return camera switch
        {
            PerspectiveCamera perspective => new Camera3D(eye, target) { FieldOfView = perspective.FieldOfView },
            OrthographicCamera ortho => new Camera3D(eye, target, false) { FieldOfView = 2 * Math.Atan(ortho.Width * MillimetresPerUnit / 2 / distance) * 180 / Math.PI },
            _ => new Camera3D(eye, target)
        };
    }
}
