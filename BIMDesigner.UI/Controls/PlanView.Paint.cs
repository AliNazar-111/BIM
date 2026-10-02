using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Paint and Split Face. Paint puts a material on the face of a wall where it is clicked - in
/// plan at the plan's cut height, or anywhere on it in an elevation or the 3D view - on the part
/// split off there, or on the whole face. Split Face takes a part off a face, the rectangle
/// between two corners clicked on it in an elevation or the 3D view, so it can be painted on
/// its own: a tiled band, a feature panel.
/// </summary>
public partial class PlanView
{
    /// <summary>The material Paint puts on.</summary>
    public Guid? PaintMaterialId { get; set; }

    /// <summary>Whether Paint takes paint off rather than putting it on.</summary>
    public bool RemovingPaint { get; set; }

    /// <summary>The first corner of a part being split off a face: the wall, the face, and where on it.</summary>
    private (Wall Wall, WallFace Face, Point2D At, Point3D Model)? _splitFaceStart;

    /// <summary>Where the first corner of a part being split off is, in the model - for a view to draw the part from.</summary>
    public Point3D? SplitFaceCorner => _splitFaceStart?.Model;

    /// <summary>Raised when the first corner of a split is taken or let go.</summary>
    public event EventHandler? SplitFaceChanged;

    /// <summary>A click in plan with Paint: the face of the wall on the side clicked, at the plan's cut height.</summary>
    private void PaintInPlan(Point2D raw)
    {
        if (Document is null || HitTestWall(raw) is not { } wall || Document.GetWallType(wall) is not { } type)
        {
            HintChanged?.Invoke(this, "Click a wall's face to paint it - in plan on the side to paint, or anywhere on it in an elevation or the 3D view.");
            return;
        }

        var (along, across) = wall.Locate(type.Structure, raw);
        PaintFace(wall, across >= 0 ? WallFace.Exterior : WallFace.Interior, along, Core.Architecture.StackedWallType.PlanCutHeight);
    }

    /// <summary>A click in an elevation or the 3D view with Paint: the face it lands on, where it lands. Public so the window can pass clicks on.</summary>
    public bool PaintIn3D(Guid elementId, Point3D at)
    {
        if (Document?.Walls.FirstOrDefault(wall => wall.Id == elementId) is not { } wall) return false;
        if (WallPaint.Locate(Document, wall, at) is not var (face, along, height))
        {
            HintChanged?.Invoke(this, "Click on a wall's face - its inside or outside, not its top or end.");
            return true;
        }

        PaintFace(wall, face, along, height);
        return true;
    }

    /// <summary>Paints one face of a wall at a point on it with the material chosen, or takes the paint off.</summary>
    public bool PaintFace(Wall wall, WallFace face, double along, double height)
    {
        if (Document is null) return false;
        if (CurtainLayout.Of(Document, wall) is not null)
        {
            HintChanged?.Invoke(this, "A curtain wall is glass and panels, not a face to paint: change its panels instead.");
            return false;
        }

        var materialId = RemovingPaint ? null : PaintMaterialId;
        if (!RemovingPaint && materialId is null)
        {
            HintChanged?.Invoke(this, "Pick the material to paint with on the options bar first.");
            return false;
        }

        if (WallPaint.Paint(wall, face, along, height, materialId) is not { } command)
        {
            HintChanged?.Invoke(this, RemovingPaint ? "There is no paint there to take off." : "That is painted with it already.");
            return false;
        }

        Apply(command);
        var name = materialId is { } id ? Document.FindMaterial(id)?.Name : null;
        HintChanged?.Invoke(this, name is null
            ? $"Paint taken off the {face.ToString().ToLowerInvariant()} face."
            : $"{face} face painted {name}. Split Face first to paint only part of it.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// A click in an elevation or the 3D view with Split Face: the first corner of the part, then
    /// the opposite one on the same face. Public so the window can pass clicks on.
    /// </summary>
    public bool SplitFaceIn3D(Guid elementId, Point3D at)
    {
        if (Document?.Walls.FirstOrDefault(wall => wall.Id == elementId) is not { } wall) return false;
        if (CurtainLayout.Of(Document, wall) is not null)
        {
            HintChanged?.Invoke(this, "A curtain wall is glass and panels, not a face to split: change its grid instead.");
            return true;
        }

        if (WallPaint.Locate(Document, wall, at) is not var (face, along, height))
        {
            HintChanged?.Invoke(this, "Click on a wall's face - its inside or outside, not its top or end.");
            return true;
        }

        var here = new Point2D(along, height);
        if (_splitFaceStart is not { } start || !ReferenceEquals(start.Wall, wall) || start.Face != face)
        {
            _splitFaceStart = (wall, face, here, at);
            SplitFaceChanged?.Invoke(this, EventArgs.Empty);
            HintChanged?.Invoke(this, "Now click the opposite corner of the part on the same face. Take it to the face's edge for a band right across. Esc cancels.");
            return true;
        }

        _splitFaceStart = null;
        SplitFaceChanged?.Invoke(this, EventArgs.Empty);
        if (WallPaint.Split(Document, wall, face, start.At, here) is not { } command)
        {
            HintChanged?.Invoke(this, "That part is too small, or is the whole face. Click two corners further apart.");
            return true;
        }

        Apply(command);
        HintChanged?.Invoke(this,
            $"Part split off the {face.ToString().ToLowerInvariant()} face, {Units.FormatLength(Math.Abs(here.X - start.At.X))} by {Units.FormatLength(Math.Abs(here.Y - start.At.Y))}. Paint it with Paint.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Lets go of a split half made.</summary>
    private bool CancelSplitFace()
    {
        if (_splitFaceStart is null) return false;

        _splitFaceStart = null;
        SplitFaceChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
