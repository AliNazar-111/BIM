using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Walls shaped in plan, drawn with the Wall tool as Archicad draws them: a Trapezoid wall from
/// its start to its end, a thickness at each; a Polygon wall round its corners, closed with Enter,
/// a double click or the first corner. Each is one material through - the core of the wall type
/// chosen - and as high as walls are being drawn.
/// </summary>
public partial class PlanView
{
    public double TrapezoidStartThickness { get; set; } = 300;

    public double TrapezoidEndThickness { get; set; } = 150;

    public TrapezoidSide TrapezoidSide { get; set; } = TrapezoidSide.Centre;

    private readonly List<Point2D> _shapedPoints = new();

    private bool CancelShapedWall()
    {
        var any = _shapedPoints.Count > 0;
        _shapedPoints.Clear();
        return any;
    }

    /// <summary>What a shaped wall drawn now is made of: the core of the wall type chosen, or its first layer.</summary>
    private Guid ShapedWallMaterial()
    {
        if (Document?.FindType<WallType>(ActiveWallTypeId) is not { } type || type.Structure.Layers.Count == 0)
            return Document?.Materials.FirstOrDefault()?.Id ?? Guid.Empty;

        var core = type.Structure.CoreStartIndex;
        return type.Structure.Layers[core >= 0 ? core : 0].MaterialId;
    }

    /// <summary>A shaped wall as high as walls are being drawn: up to the level chosen, or its height, or down by its depth.</summary>
    private PolygonWall NewShapedWall(PolygonWallKind kind)
    {
        var wall = new PolygonWall { Kind = kind, LevelId = ActiveLevelId, MaterialId = ShapedWallMaterial() };
        if (NewWallDepth)
        {
            wall.TopLevelId = ActiveLevelId;
            wall.BaseOffset = -NewWallHeight;
        }
        else if (NewWallTopLevelId is { } top && top != ActiveLevelId)
        {
            wall.TopLevelId = top;
        }
        else
        {
            wall.UnconnectedHeight = NewWallHeight;
        }

        return wall;
    }

    /// <summary>A click with the Trapezoid or Polygon Wall shape.</summary>
    private void PlaceShapedWallPoint(Point2D model)
    {
        if (Document is null) return;

        if (_drawShape == WallShape.Trapezoid)
        {
            if (_shapedPoints.Count == 0)
            {
                _shapedPoints.Add(model);
                HintChanged?.Invoke(this, $"Click where it ends: {Units.FormatLength(TrapezoidStartThickness)} thick here, {Units.FormatLength(TrapezoidEndThickness)} there. Esc cancels.");
                return;
            }

            var start = _shapedPoints[0];
            if (start.DistanceTo(model) < SnapStepMm) return;

            var trapezoid = NewShapedWall(PolygonWallKind.Trapezoid);
            (trapezoid.Start, trapezoid.End) = (start, model);
            (trapezoid.StartThickness, trapezoid.EndThickness, trapezoid.StraightSide) = (TrapezoidStartThickness, TrapezoidEndThickness, TrapezoidSide);
            trapezoid.Shape();

            Apply(new AddElementCommand(Document, trapezoid, "Draw Trapezoid Wall"));
            Select(trapezoid);
            _shapedPoints.Clear();
            _shapedPoints.Add(model);
            HintChanged?.Invoke(this, "Trapezoid wall placed. Click where the next one ends, or Esc to stop.");
            return;
        }

        // A polygon: corner after corner; back on the first closes it.
        if (_shapedPoints.Count >= 3 && model.DistanceTo(_shapedPoints[0]) < Math.Max(SnapStepMm, 8 / PixelsPerMm))
        {
            FinishPolygonWall();
            return;
        }

        if (_shapedPoints.Count > 0 && model.DistanceTo(_shapedPoints[^1]) < 1) return;

        _shapedPoints.Add(model);
        HintChanged?.Invoke(this, _shapedPoints.Count < 3
            ? "Click the next corner of the wall's outline. Esc cancels."
            : "Click the next corner - or the first, a double click or Enter to close the outline.");
    }

    /// <summary>Closes the outline of a polygon wall being drawn and puts it up, or says why it cannot be.</summary>
    private bool FinishPolygonWall()
    {
        if (Document is null || _shapedPoints.Count == 0) return false;

        var outline = _shapedPoints.ToList();
        if (PolygonWalls.OutlineProblem(outline) is { } problem)
        {
            HintChanged?.Invoke(this, problem);
            return true;
        }

        var wall = NewShapedWall(PolygonWallKind.Polygon);
        wall.Outline = outline;
        _shapedPoints.Clear();
        Apply(new AddElementCommand(Document, wall, "Draw Polygon Wall"));
        Select(wall);
        HintChanged?.Invoke(this, $"Polygon wall placed, {Units.FormatArea(Polygon2D.Area(wall.Outline))} in plan. Click to start another.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The shaped wall being drawn, following the cursor.</summary>
    private void DrawShapedWallPreview(DrawingContext dc)
    {
        if (ActiveTool != PlanTool.Wall || _shapedPoints.Count == 0) return;

        if (_drawShape == WallShape.Trapezoid)
        {
            if (PolygonWalls.Trapezoid(_shapedPoints[0], _cursorModel, TrapezoidStartThickness, TrapezoidEndThickness, TrapezoidSide) is { } outline)
                dc.DrawGeometry(null, _previewPen, Outline(outline));
            dc.DrawLine(_previewPen, ModelToScreen(_shapedPoints[0]), ModelToScreen(_cursorModel));
            return;
        }

        var points = _shapedPoints.Append(_cursorModel).ToList();
        DrawModelPolyline(dc, _previewPen, points.Append(points[0]).ToList());
        foreach (var corner in _shapedPoints) dc.DrawEllipse(Brushes.Transparent, _selectedPen, ModelToScreen(corner), 4, 4);
    }
}
