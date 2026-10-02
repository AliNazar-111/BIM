using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.UI.Rendering;

/// <summary>
/// Draws the contents of a floor plan into any <see cref="DrawingContext"/>, under any
/// transform.
///
/// This exists so that the plan on screen and the plan inside a viewport on a sheet are the
/// same drawing, produced by the same code, from the same model. A sheet that rendered its
/// own simplified version of a plan would eventually disagree with the plan - and a drawing
/// set that disagrees with itself is worse than no drawing set. (Â§6.1, Â§6.5)
///
/// It draws content only. Grips, snap rings and half-finished walls are editing affordances
/// belonging to the editor, not to the drawing, and must never appear on a sheet.
/// </summary>
public sealed class PlanRenderer
{
    /// <summary>
    /// Below this, the line between two layers is dropped and only the fills are drawn. This
    /// affects the separator strokes only, never which layers are drawn.
    /// </summary>
    private const double MinStrokedLayerPixels = 2.0;

    /// <summary>
    /// Screen pixels to one millimetre of paper at 96 dpi.
    ///
    /// Annotation is sized on the page, not in the building: a grid bubble is about 6 mm
    /// across whether the drawing is 1:20 or 1:200. The editor treats its own surface as a
    /// page at 1:1, which is what makes a bubble on screen the size it will print.
    /// </summary>
    public const double ScreenPixelsPerPaperMm = 96.0 / 25.4;

    private readonly Pen _wallOutlinePen;
    private readonly Pen _layerPen;
    private readonly Pen _membranePen;
    private readonly Pen _componentPen;
    private readonly Pen _selectedPen;
    private readonly Pen _overheadPen;
    private readonly Pen _previewPen;
    private readonly Pen _locationLinePen;
    private readonly Pen _openingPen;
    private readonly Pen _swingPen;
    private readonly Pen _openPanelPen;
    private readonly Pen _glassPen;
    private readonly Pen _roomPen;
    private readonly Pen _slabPen;
    private readonly Pen _roofLinePen;
    private readonly Pen _roofSlopePen;
    private readonly Pen _gridLinePen;
    private readonly Pen _sectionPen;
    private readonly Pen _sectionHeadPen;
    private readonly Pen _dimensionPen;
    private readonly Pen _looseDimensionPen;
    private readonly Pen _leaderPen;

    private readonly Brush _gridBubbleBrush;
    private readonly Brush _gridTextBrush;
    private readonly Brush _sectionTextBrush;
    private readonly Brush _sectionArrowBrush;
    private readonly Brush _selectedArrowBrush;
    private readonly Brush _dimensionTextBrush;
    private readonly Brush _looseDimensionBrush;
    private readonly Brush _tagBrush;
    private readonly Brush _tagTextBrush;
    private readonly Brush _labelBackdrop;
    private readonly Brush _componentBrush;
    private readonly Brush _roomBrush;
    private readonly Brush _roomSelectedBrush;
    private readonly Brush _roomTagBrush;
    private readonly Brush _unenclosedBrush;
    private readonly Brush _wallLabelBrush;

    private readonly Dictionary<Guid, Brush> _materialBrushes = new();
    private readonly Dictionary<Guid, Brush> _coarseBrushes = new();
    private readonly Dictionary<Guid, Brush> _slabBrushes = new();

    private Func<Point2D, Point> _toScreen = point => new Point(point.X, point.Y);

    public PlanRenderer(DrawingPalette? palette = null)
    {
        var ink = palette ?? AppTheme.Drawing;

        _wallOutlinePen = RenderPens.Solid(ink.WallOutline, 1.3);
        _layerPen = RenderPens.Solid(ink.LayerSeparator, 0.7);
        _membranePen = RenderPens.Dashed(ink.WallOutline, 1.0, 5, 3);
        _overheadPen = RenderPens.Dashed(ink.WallOutline, 0.9, 7, 4);
        _componentPen = RenderPens.Solid(ink.Component, 1.1);
        _selectedPen = RenderPens.Solid(ink.Selected, 2.2);
        _previewPen = RenderPens.Dashed(ink.Preview, 1.4, 4, 3);
        _locationLinePen = RenderPens.Dashed(ink.LocationLine, 1.2, 6, 4);
        _openingPen = RenderPens.Solid(ink.Opening, 1.2);
        _swingPen = RenderPens.Solid(ink.Swing, 1.0);
        _openPanelPen = RenderPens.Dashed(ink.Swing, 1.4, 5, 3);
        _glassPen = RenderPens.Solid(ink.Glass, 1.4);

        _roomPen = RenderPens.Dashed(ink.RoomOutline, 1.0, 3, 3);
        _slabPen = RenderPens.Dashed(ink.SlabOutline, 1.0, 8, 4);

        // Ridges, hips and valleys are built lines, not the edge of a sketch, so they are
        // drawn solid and a little heavier than the roof outline they sit inside.
        _roofLinePen = RenderPens.Solid(ink.WallOutline, 1.2);
        _roofSlopePen = RenderPens.Solid(ink.Selected, 1.2);

        // Long dash, short dash: the chain line drawings use for setting-out.
        _gridLinePen = RenderPens.Chain(ink.GridLine, 1.0, 14, 4, 2, 4);
        _sectionPen = RenderPens.Chain(ink.SectionLine, 1.6, 12, 3, 2, 3);

        // The head is solid: a dashed bubble and a dashed arrow would read as a broken symbol
        // rather than as the chain line the cut itself is drawn with.
        _sectionHeadPen = RenderPens.Solid(ink.SectionLine, 1.6);

        // A dimension that follows the model, and one that is only measuring between points.
        // The reader needs to be able to tell which numbers will keep up with the building.
        _dimensionPen = RenderPens.Solid(ink.Dimension, 1.0);
        _looseDimensionPen = RenderPens.Dashed(ink.LooseDimension, 1.0, 5, 3);
        _leaderPen = RenderPens.Solid(ink.Leader, 1.0);

        _gridBubbleBrush = RenderPens.Fill(ink.BubbleFill);
        _gridTextBrush = RenderPens.Fill(ink.GridText);
        _sectionTextBrush = RenderPens.Fill(ink.SectionText);
        _sectionArrowBrush = RenderPens.Fill(ink.SectionLine);
        _selectedArrowBrush = RenderPens.Fill(ink.Selected);
        _dimensionTextBrush = RenderPens.Fill(ink.DimensionText);
        _looseDimensionBrush = RenderPens.Fill(ink.LooseDimensionText);
        _tagBrush = RenderPens.Fill(ink.TagFill);
        _tagTextBrush = RenderPens.Fill(ink.TagText);
        _labelBackdrop = RenderPens.Fill(ink.LabelBackdrop);
        _wallLabelBrush = RenderPens.Fill(ink.WallLabel);
        _unenclosedBrush = RenderPens.Fill(ink.Unenclosed);

        // Faint enough to read the plan through, which is the point of a colour fill: it says
        // which space is which, it does not replace the drawing.
        _componentBrush = RenderPens.Fill(ink.ComponentFill);
        _roomBrush = RenderPens.Fill(ink.RoomFill);
        _roomSelectedBrush = RenderPens.Fill(ink.RoomFillSelected);
        _roomTagBrush = RenderPens.Fill(ink.RoomTagText);
    }

    public BimDocument? Document { get; set; }

    public DetailLevel DetailLevel { get; set; } = DetailLevel.Fine;

    /// <summary>The view being drawn, for the settings that belong to it: how its wall joins are cleaned.</summary>
    public BIMDesigner.Core.Sheets.ViewReference View { get; set; }

    private HashSet<Guid> _selected = new();

    /// <summary>
    /// What is highlighted while editing. Always empty on a sheet: paper has no selection.
    ///
    /// Held as a set of ids rather than a list of elements because it is asked about once per
    /// element per frame, and a select-all on a large storey would otherwise be quadratic.
    /// </summary>
    public void SetSelection(IEnumerable<Element>? elements) =>
        _selected = elements is null
            ? new HashSet<Guid>()
            : elements.Select(element => element.Id).ToHashSet();

    private bool IsSelected(Element element) => _selected.Count > 0 && _selected.Contains(element.Id);

    /// <summary>Which storey is drawn. A plan is a cut through one level.</summary>
    public Guid ActiveLevelId { get; set; }

    /// <summary>Screen pixels to one model millimetre, under the current transform.</summary>
    public double PixelsPerMm { get; private set; } = 1;

    /// <summary>Screen pixels to one millimetre of paper. Sets how big annotation comes out.</summary>
    public double PixelsPerPaperMm { get; set; } = ScreenPixelsPerPaperMm;

    public double PixelsPerDip { get; set; } = 1;

    /// <summary>
    /// Points the renderer at a surface: how model coordinates reach it, and how magnified
    /// they are when they get there.
    /// </summary>
    public void SetTransform(Func<Point2D, Point> toScreen, double pixelsPerMm)
    {
        _toScreen = toScreen;
        PixelsPerMm = pixelsPerMm > 0 ? pixelsPerMm : 1;
    }

    /// <summary>Clears the cached brushes when the project, and so its materials, changes.</summary>
    public void InvalidateBrushes()
    {
        _materialBrushes.Clear();
        _coarseBrushes.Clear();
        _slabBrushes.Clear();
    }

    private Point ModelToScreen(Point2D point) => _toScreen(point);

    /// <summary>
    /// A size given in paper millimetres, expressed as screen pixels at 1:1, converted to
    /// this surface's page scale. On screen the factor is 1; on a sheet it follows the zoom.
    /// </summary>
    private double Page(double pixelsAtScreenScale) =>
        pixelsAtScreenScale * (PixelsPerPaperMm / ScreenPixelsPerPaperMm);

    /// <summary>
    /// Whether an element belongs on this plan. Grids are datums of the whole building, so
    /// they appear on every level - that is what lets storeys be lined up with each other.
    /// </summary>
    private IEnumerable<T> OnActiveLevel<T>() where T : Element =>
        Document is null
            ? Enumerable.Empty<T>()
            : Document.Elements.OfType<T>().Where(element => element is Grid || (element.LevelId == ActiveLevelId && Filter(element)));

    /// <summary>The view's filters: what it leaves out. Everything is shown unless set.</summary>
    public Func<Element, bool> Filter { get; set; } = _ => true;

    // ---- the drawing -----------------------------------------------------------

    /// <summary>
    /// Everything a floor plan contains, in the order it reads: construction under
    /// annotation, and annotation on top of whatever it describes.
    ///
    /// As an <paramref name="underlay"/> it draws construction only. A storey shown under the
    /// one being worked on is there to set out against; repeating its grid bubbles, room tags
    /// and dimensions would double every label on the drawing.
    /// </summary>
    /// <summary>
    /// Whether each wall's length is written along it. A help while drawing, not part of the
    /// drawing: a sheet, and so a print or a PDF, leaves it off, as Revit's temporary
    /// dimensions never print.
    /// </summary>
    public bool ShowWallLabels { get; set; } = true;

    public void DrawContent(DrawingContext dc, bool underlay = false)
    {
        if (Document is null) return;

        // Grids come through on every level: they are datums of the whole building, and
        // lining storeys up against them is the point of having them. An underlay leaves them
        // to the storey above, which is drawing the same ones.
        if (!underlay)
            foreach (var grid in Document.Elements.OfType<Grid>()) DrawGrid(dc, grid);

        // Slabs are construction under everything else; rooms are the space above them.
        foreach (var slab in OnActiveLevel<Slab>()) DrawSlab(dc, slab);

        // Fascias and gutters round the roofs: the strip each covers, seen from above.
        foreach (var sweep in OnActiveLevel<RoofEdgeSweep>()) DrawRoofEdgeSweep(dc, sweep);
        foreach (var roofWindow in OnActiveLevel<RoofWindow>()) DrawRoofWindow(dc, roofWindow);
        foreach (var shaft in ShaftsOnPlan()) DrawShaft(dc, shaft);
        foreach (var pipe in DownpipesOnPlan()) DrawDownpipe(dc, pipe);

        if (!underlay)
            foreach (var room in OnActiveLevel<Room>()) DrawRoomFill(dc, room);

        foreach (var wall in OnActiveLevel<Wall>()) DrawWall(dc, wall);
        foreach (var opening in OnActiveLevel<Opening>()) DrawOpening(dc, opening);

        // A column is construction like a wall, and is drawn over it where it is built into one.
        foreach (var column in OnActiveLevel<Column>()) DrawColumn(dc, column);

        // Components sit in the rooms and against the walls, so they are drawn over both.
        foreach (var component in OnActiveLevel<Component>()) DrawComponent(dc, component);

        if (underlay) return;

        if (ShowWallLabels)
            foreach (var wall in OnActiveLevel<Wall>()) DrawWallLabel(dc, wall);
        foreach (var room in OnActiveLevel<Room>()) DrawRoomTag(dc, room);

        foreach (var dimension in OnActiveLevel<Dimension>()) DrawDimension(dc, dimension);
        foreach (var section in OnActiveLevel<SectionMarker>()) DrawSectionMarker(dc, section);
        foreach (var tag in OnActiveLevel<Tag>()) DrawTag(dc, tag);
        foreach (var note in OnActiveLevel<TextNote>()) DrawTextNote(dc, note);
    }

    // ---- walls -----------------------------------------------------------------

    /// <summary>
    /// Draws one wall as its assembly - each layer in its own material's cut colour, with the
    /// outline over the top - mitred where it joins another wall, and interrupted wherever a
    /// door or window is hosted in it.
    /// </summary>
    /// <summary>Where the wall being drawn is at the plan's cut height; unchanged unless it leans.</summary>
    private Func<Point2D, Point2D> _lean = point => point;

    private IReadOnlyList<Point2D> Cut(IReadOnlyList<Point2D> points) => points.Select(_lean).ToList();

    /// <summary>The height the plan cuts the wall being drawn at, and the reveals it might cut through.</summary>
    private double _cutElevation;
    private IReadOnlyList<(double Bottom, double Top, WallSide Side, double Depth)> _reveals = Array.Empty<(double, double, WallSide, double)>();

    /// <summary>
    /// The sweeps the plan cuts through: each drawn as the band it makes beside the wall face, as
    /// far out as its profile reaches at the cut height - mitred into its neighbour's at a corner,
    /// turned round an exposed end when it returns. An upright sweep runs through every cut, so
    /// it is always drawn, as the shape it has in plan.
    /// </summary>
    private void DrawSweeps(
        DrawingContext dc, Wall wall, WallType type, IEnumerable<(WallSweep Sweep, Guid ElementId)> sweeps,
        double wallBottom, double wallTop)
    {
        if (Document is null) return;

        var structure = type.Structure;
        var curve = wall.LocationCurve;

        foreach (var (sweep, elementId) in sweeps)
        {
            var placed = elementId != wall.Id;
            if (sweep.Kind == SweepKind.Reveal && !placed) continue;

            var fill = MaterialBrush(sweep.MaterialId);
            var selected = placed && _selected.Contains(elementId);
            var pen = selected ? _selectedPen : _wallOutlinePen;
            var shape = sweep.Shape(Document);

            // A placed one that the cut misses is still shown, faintly, so it can be seen and picked:
            // a sweep as the outline of its full depth, a reveal as a dashed line at the back of its groove.
            if (placed && !sweep.Vertical)
            {
                var (low, high) = sweep.Span(wallBottom, wallTop);
                if (sweep.Kind == SweepKind.Reveal || _cutElevation <= low || _cutElevation >= high)
                {
                    var o = sweep.Kind == SweepKind.Reveal ? -sweep.Depth : sweep.OutAt(1);
                    var edge = new List<double> { 0 };
                    edge.AddRange(curve.Between(0, wall.Length, wall.LeftOf(structure, WallSweeps.Across(type, sweep, o))));
                    edge.Add(wall.Length);
                    DrawPolyline(dc, selected ? _selectedPen : sweep.Kind == SweepKind.Reveal ? _membranePen : _layerPen,
                        edge.Select(a => Cut(new[] { wall.PointAt(structure, a, WallSweeps.Across(type, sweep, o)) })[0]).ToList());
                    continue;
                }
            }

            if (sweep.Kind == SweepKind.Reveal) continue;

            if (sweep.Vertical)
            {
                var outline = shape
                    .Select(p => wall.PointAt(structure,
                        Math.Clamp(sweep.Along + (p.Up - 0.5) * sweep.Height, 0, wall.Length),
                        WallSweeps.Across(type, sweep, sweep.OutAt(p.Out))))
                    .ToList();
                dc.DrawGeometry(fill, pen, BuildOutline(Cut(outline)));
                continue;
            }

            var (bottom, top) = sweep.Span(wallBottom, wallTop);
            if (_cutElevation <= bottom || _cutElevation >= top) continue;

            var reach = ProfileReach(shape, (_cutElevation - bottom) / sweep.Height);
            if (reach <= 0) continue;

            var inner = sweep.Offset;
            var outer = sweep.OutAt(reach);
            var sign = sweep.Side == WallSide.Exterior ? 1.0 : -1.0;

            foreach (var run in WallSweeps.Runs(Document, wall, type, sweep, bottom, top))
            {
                var stations = new List<double> { run.From };
                stations.AddRange(curve.Between(run.From, run.To, wall.LeftOf(structure, sign * type.Width / 2)));
                stations.Add(run.To);

                double At(int s, double o) =>
                    s == 0 ? WallSweeps.EndAlong(wall, type, sweep, run.Start, stations[0], atStart: true, o)
                    : s == stations.Count - 1 ? WallSweeps.EndAlong(wall, type, sweep, run.End, stations[^1], atStart: false, o)
                    : stations[s];

                var band = stations.Select((_, s) => wall.PointAt(structure, At(s, inner), WallSweeps.Across(type, sweep, inner)))
                    .Concat(stations.Select((_, s) => wall.PointAt(structure, At(s, outer), WallSweeps.Across(type, sweep, outer))).Reverse())
                    .ToList();
                dc.DrawGeometry(fill, pen, BuildOutline(Cut(band)));

                // A return: the band carried round the end, across to the wall's far face.
                foreach (var (end, along, atStart) in new[] { (run.Start, run.From, true), (run.End, run.To, false) })
                {
                    if (end.Kind != SweepEndKind.Free || !sweep.Returns) continue;

                    var direction = atStart ? -1.0 : 1.0;
                    var far = -WallSweeps.Across(type, sweep, 0);
                    var corner = new[]
                    {
                        wall.PointAt(structure, along + direction * inner, WallSweeps.Across(type, sweep, inner)),
                        wall.PointAt(structure, along + direction * outer, WallSweeps.Across(type, sweep, outer)),
                        wall.PointAt(structure, along + direction * outer, far),
                        wall.PointAt(structure, along + direction * inner, far)
                    };
                    dc.DrawGeometry(fill, pen, BuildOutline(Cut(corner)));
                }
            }
        }
    }

    /// <summary>How far out a profile reaches at a height through it, both as fractions of its size.</summary>
    private static double ProfileReach(IReadOnlyList<(double Out, double Up)> shape, double up)
    {
        var reach = 0.0;
        for (var i = 0; i < shape.Count; i++)
        {
            var a = shape[i];
            var b = shape[(i + 1) % shape.Count];
            if ((a.Up - up) * (b.Up - up) > 0 || Math.Abs(b.Up - a.Up) < 1e-12) continue;

            reach = Math.Max(reach, a.Out + (b.Out - a.Out) * (up - a.Up) / (b.Up - a.Up));
        }

        return reach;
    }

    private void DrawWall(DrawingContext dc, Wall wall)
    {
        if (Document is null) return;

        var type = Document.GetWallType(wall);
        if (type is null) return;

        var isSelected = IsSelected(wall);

        // A wall standing above the cut - a dormer's, on the roof - is not cut by this plan:
        // it is overhead, and drawn as its outline dashed, as things above the cut are.
        if (IsOverhead(wall))
        {
            var half = type.Width / 2;
            dc.DrawGeometry(null, isSelected ? _selectedPen : _overheadPen,
                BuildOutline(WallJoins.GetBandOutline(Document, wall, type, half, -half)));
            return;
        }

        if (CurtainLayout.Of(Document, wall) is { } curtain)
        {
            DrawCurtainWall(dc, wall, curtain, isSelected);
            return;
        }

        // A slanted or tapered wall is drawn where the plan cuts it, which is not where it
        // stands: every point of it moves across by how far it leans at the cut height.
        var cutHeight = Math.Min(StackedWallType.PlanCutHeight, wall.GetHeight(Document));
        _lean = WallLean.Leans(wall, type)
            ? point => WallLean.Move(wall, type, point, cutHeight)
            : point => point;

        // Reveals and sweeps show in plan only where the plan cuts them.
        var wallBottom = wall.GetBaseElevation(Document);
        _cutElevation = wallBottom + cutHeight;
        _reveals = WallSweeps.Reveals(Document, wall, type, wallBottom, wallBottom + wall.GetHeight(Document));

        // The wall is drawn as the stretches that remain solid. An opening is not painted
        // over the wall afterwards - the wall genuinely is not there (section 2.5). Where
        // those stretches start and stop is worked out in Core, so the plan and the 3D model
        // cannot disagree about the shape of a wall.
        var gaps = WallJoins.FaceGaps(Document, wall, type);

        // A wall with an edited profile is cut only where it reaches the cut height; below it,
        // the rest is seen from above, as its outline.
        if (WallProfile.Of(Document, wall) is not null)
        {
            var half = type.Width / 2;
            foreach (var slice in WallSlices.Solid(Document, wall, type))
                dc.DrawGeometry(null, _layerPen, BuildOutline(WallJoins.GetBandOutline(wall, type, half, -half, slice.CutFrom, slice.CutTo)));
        }

        // An upright reveal is a groove down the face: the wall is drawn thinner along it.
        var verticals = WallSweeps.VerticalReveals(Document, wall);
        var horizontal = _reveals;
        foreach (var slice in WallSlices.InPlan(Document, wall, type, cutHeight))
        foreach (var (part, bands) in WallSweeps.SplitAtVerticalReveals(wall, type, slice, verticals))
        {
            _reveals = bands.Count == 0 ? horizontal : horizontal.Concat(bands).ToList();
            DrawWallRun(dc, wall, type, part, gaps, isSelected);
        }

        _reveals = horizontal;

        var wallTop = wallBottom + wall.GetHeight(Document);
        DrawSweeps(dc, wall, type, type.Sweeps.Select(s => (s, wall.Id)), wallBottom, wallTop);
        DrawSweeps(dc, wall, type, WallSweeps.Placed(Document, wall), wallBottom, wallTop);

        // Show where the user actually drew, so the effect of the location line is visible.
        if (isSelected && wall.LocationLine != WallLocationLine.WallCentreline)
            DrawPolyline(dc, _locationLinePen, wall.LocationCurve.Points());
    }

    /// <summary>
    /// A curtain wall where the plan cuts it: in each bay the panel at cut height - a line of
    /// glass, a solid panel, nothing, or a door with its swing - and the mullions cut through,
    /// square or round.
    /// </summary>
    private void DrawCurtainWall(DrawingContext dc, Wall wall, CurtainLayout layout, bool isSelected)
    {
        var type = layout.Type;
        var body = type.Body;
        var structure = body.Structure;
        var half = type.PanelThickness / 2;
        var cut = Math.Min(StackedWallType.PlanCutHeight, layout.Height / 2);

        foreach (var cell in layout.Cells.Where(c => c.Bottom <= cut && c.Top > cut))
        {
            if (cell.ClearTo - cell.ClearFrom <= 1e-6) continue;

            switch (cell.Kind)
            {
                case CurtainPanelKind.Glazed:
                {
                    // Clear glass is two lines and nothing between; anything that cannot be
                    // seen through is filled, so a spandrel or frosted panel reads on the plan.
                    var pane = CurtainGlassLook.ThicknessOf(cell.Glass, half);
                    var outline = BuildOutline(CurtainGeometry.Band(wall, body, cell.ClearFrom, cell.ClearTo, pane, -pane));
                    dc.DrawGeometry(cell.Glass == CurtainGlass.Clear ? null : GlassBrush(cell.Glass), _glassPen, outline);
                    break;
                }

                case CurtainPanelKind.Solid:
                    dc.DrawGeometry(MaterialBrush(type.SolidMaterialId), _wallOutlinePen,
                        BuildOutline(CurtainGeometry.Band(wall, body, cell.ClearFrom, cell.ClearTo, half, -half)));
                    break;

                case CurtainPanelKind.Door:
                {
                    var jambFrom = wall.PointAt(structure, cell.ClearFrom, 0);
                    var jambTo = wall.PointAt(structure, cell.ClearTo, 0);
                    var width = jambFrom.DistanceTo(jambTo);
                    if (width <= 1e-6) break;

                    var along = (jambTo - jambFrom) / width;
                    var across = wall.ExteriorNormalAt(cell.ClearFrom);
                    var openingType = cell.OpeningTypeId is { } id ? Document?.FindType<OpeningType>(id) : null;

                    var doorType = openingType as DoorType;

                    // Hinged at the start of the bay, opening to the inside.
                    if (doorType is null)
                    {
                        DrawLeafAndArc(dc, _openingPen, jambFrom, along, -across, width, 90);
                        break;
                    }

                    // A panel of a door type is drawn by that type's own symbol, so a sliding
                    // or folding curtain door reads in plan as it does anywhere else. It slides
                    // along the curtain wall rather than over a stretch of solid wall.
                    var panel = new Door
                    {
                        TypeId = doorType.Id, LevelId = wall.LevelId, HostWallId = wall.Id,
                        DistanceAlongWall = (cell.ClearFrom + cell.ClearTo) / 2,
                        FlipHand = cell.FlipHand,
                        FlipFacing = !cell.FlipFacing
                    };

                    DrawDoorSymbol(dc, panel, doorType, d => jambFrom + along * (d - cell.ClearFrom),
                        cell.ClearFrom, cell.ClearTo, cell.ClearFrom, Math.Max(0, layout.Length - cell.ClearTo),
                        along, across, body.Width / 2, _openingPen);
                    break;
                }
            }
        }

        foreach (var mullion in layout.Mullions.Where(m => m.IsVertical))
        {
            var outline = type.MullionProfile == MullionProfile.Circular
                ? CurtainGeometry.Circle(wall, body, (mullion.From + mullion.To) / 2, type.MullionWidth / 2)
                : CurtainGeometry.Band(wall, body, mullion.From, mullion.To, type.MullionDepth / 2, -type.MullionDepth / 2);
            dc.DrawGeometry(MaterialBrush(type.MullionMaterialId), _wallOutlinePen, BuildOutline(outline));
        }

        if (isSelected)
        {
            var width = body.Width / 2;
            dc.DrawGeometry(null, _selectedPen, BuildOutline(CurtainGeometry.Band(wall, body, 0, wall.Length, width, -width)));
        }
    }

    /// <summary>An open line through model points - a curve drawn as its short straight pieces.</summary>
    public void DrawPolyline(DrawingContext dc, Pen pen, IReadOnlyList<Point2D> points)
    {
        if (points.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ModelToScreen(points[0]), false, false);
            ctx.PolyLineTo(points.Skip(1).Select(ModelToScreen).ToArray(), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawWallRun(
        DrawingContext dc, Wall wall, WallType type, WallSlice slice,
        IReadOnlyList<(bool Exterior, double From, double To)> gaps, bool isSelected)
    {
        var structure = type.Structure;
        var half = structure.TotalWidth / 2;
        var (cutFrom, cutTo) = (slice.CutFrom, slice.CutTo);

        // What is drawn is decided by the view's detail level alone (specification section
        // 6.2). Zoom is navigation: magnifying a plan must not change its content.
        if (DetailLevel == DetailLevel.Coarse)
        {
            dc.DrawGeometry(CoarseBrush(type), null,
                BuildOutline(Cut(WallJoins.GetBandOutline(wall, type, half, -half, cutFrom, cutTo))));
        }
        else
        {
            var thinnestLayer = structure.Layers.Count == 0
                ? structure.TotalWidth
                : structure.Layers.Min(layer => layer.Thickness);

            // Only the hairline between layers depends on magnification, never which layers
            // are drawn: a 0.7 px line between two sub-pixel bands covers more of the wall
            // than the bands do, so keeping it at every scale turns the wall into a smear.
            var separator = DetailLevel == DetailLevel.Fine
                            && thinnestLayer * PixelsPerMm >= MinStrokedLayerPixels
                ? _layerPen
                : null;

            foreach (var (layer, start, end) in structure.GetLayerOffsets())
            {
                // A membrane has no thickness: it is drawn as the dashed line it is on a detail.
                if (layer.Thickness <= 0)
                {
                    var line = Cut(WallJoins.GetBandOutline(wall, type, half - start, half - end, cutFrom, cutTo));
                    dc.DrawLine(_membranePen, ModelToScreen(line[0]), ModelToScreen(line[1]));
                    continue;
                }

                if (WallSweeps.Recess(half - start, half - end, half, _reveals, _cutElevation) is not var (outer, inner)) continue;
                var band = Cut(WallJoins.GetBandOutline(wall, type, outer, inner, cutFrom, cutTo));
                dc.DrawGeometry(MaterialBrush(layer.MaterialId), separator, BuildOutline(band));
            }
        }

        // A selected wall is outlined all round, so it is clear exactly what is selected.
        if (isSelected)
        {
            dc.DrawGeometry(null, _selectedPen,
                BuildOutline(Cut(WallJoins.GetBandOutline(wall, type, half, -half, cutFrom, cutTo))));
            return;
        }

        dc.DrawGeometry(null, _wallOutlinePen, BuildCleanOutline(wall, type, slice, gaps));
    }

    /// <summary>
    /// The lines a finished plan draws round a wall: its two faces, less the stretches where
    /// another wall carries on from them, and its ends only where they are exposed. Where
    /// walls join, nothing is drawn across the join, so a building reads as one mass rather
    /// than as boxes pushed together.
    /// </summary>
    private StreamGeometry BuildCleanOutline(
        Wall wall, WallType type, WallSlice slice, IReadOnlyList<(bool Exterior, double From, double To)> gaps)
    {
        var half = type.Width / 2;
        var start = WallJoins.EndPoints(wall, type, half, -half, slice.CutFrom, atStart: true);
        var end = WallJoins.EndPoints(wall, type, half, -half, slice.CutTo, atStart: false);
        var structure = type.Structure;
        var curve = wall.LocationCurve;

        double Along(Point2D point) => wall.Locate(structure, point).Along;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            void Face(Point2D from, Point2D to, bool exterior)
            {
                var a = Along(from);
                var b = Along(to);
                if (b - a <= 1e-9) return;

                // Walk the face, skipping every stretch another wall stops against.
                var cursor = a;
                foreach (var (_, gapFrom, gapTo) in gaps
                             .Where(gap => gap.Exterior == exterior && gap.To > a && gap.From < b)
                             .Where(gap => Clean(wall, curve.PointAt(Math.Clamp((gap.From + gap.To) / 2, 0, curve.Length)), type.Width))
                             .OrderBy(gap => gap.From))
                {
                    if (gapFrom > cursor) Line(cursor, gapFrom);
                    cursor = Math.Max(cursor, gapTo);
                }

                if (cursor < b) Line(cursor, b);

                // A stretch of the face, following it round if the wall is curved.
                void Line(double u0, double u1)
                {
                    var across = exterior ? half : -half;
                    var points = curve.Between(u0, u1, wall.LeftOf(structure, across))
                        .Append(u1)
                        .Select(u => ModelToScreen(_lean(wall.PointAt(structure, u, across))))
                        .ToArray();

                    ctx.BeginFigure(ModelToScreen(_lean(wall.PointAt(structure, u0, across))), false, false);
                    ctx.PolyLineTo(points, true, false);
                }
            }

            void End(IReadOnlyList<Point2D> points)
            {
                ctx.BeginFigure(ModelToScreen(_lean(points[0])), false, false);
                ctx.PolyLineTo(points.Skip(1).Select(p => ModelToScreen(_lean(p))).ToArray(), true, false);
            }

            Face(start[0], end[0], exterior: true);
            Face(start[^1], end[^1], exterior: false);

            // A join left uncleaned shows where each wall stops, as walls butting one another.
            if (!slice.CutFrom.IsJoined || !Clean(wall, wall.Start)) End(start);
            if (!slice.CutTo.IsJoined || !Clean(wall, wall.End)) End(end);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Whether the join at a point on a wall is drawn cleaned up: as its ends ask, or as this view says.</summary>
    private bool Clean(Wall wall, Point2D joint, double reach = WallJoins.JoinTolerance) =>
        Document is null || WallJunctions.IsClean(Document, View, wall, joint, reach);

    /// <summary>
    /// Whether a wall stands wholly above this plan's cut, which is the cut height above its
    /// storey - as a dormer's walls stand on the roof - so is seen overhead rather than cut.
    /// </summary>
    private bool IsOverhead(Wall wall) =>
        Document is not null &&
        wall.GetBaseElevation(Document) >
        (Document.FindLevel(wall.LevelId)?.Elevation ?? 0) + StackedWallType.PlanCutHeight + WallJoins.JoinTolerance;

    private void DrawWallLabel(DrawingContext dc, Wall wall)
    {
        var isSelected = IsSelected(wall);
        if (IsOverhead(wall) && !isSelected) return;

        // Below about 16 mm of paper the number is longer than the wall it labels.
        if (!isSelected && wall.Length * PixelsPerMm <= Page(60)) return;

        var text = Text(Units.FormatLength(wall.Length), Page(11), _wallLabelBrush);

        var midpoint = ModelToScreen(wall.LocationCurve.PointAt(wall.Length / 2));
        var origin = new Point(midpoint.X - text.Width / 2, midpoint.Y - text.Height / 2);
        var backdrop = new Rect(
            origin.X - Page(4), origin.Y - Page(2),
            text.Width + Page(8), text.Height + Page(4));

        dc.DrawRoundedRectangle(_labelBackdrop, null, backdrop, Page(3), Page(3));
        dc.DrawText(text, origin);
    }

    // ---- openings --------------------------------------------------------------

    /// <summary>
    /// Draws a door or window in the gap its host wall leaves for it: the jambs, the frame,
    /// and the symbol that says which it is - a swing arc for a door, glazing for a window.
    /// </summary>
    /// <summary>A door or window not yet in the model, drawn where it would go: the preview as one is placed.</summary>
    public void DrawOpeningPreview(DrawingContext dc, Opening opening) => DrawOpening(dc, opening);

    /// <summary>The curtain wall panel a click would turn into a door, outlined where it is, with its swing.</summary>
    public void DrawCurtainDoorPreview(DrawingContext dc, Wall wall, CurtainWallType type, CurtainCell cell)
    {
        var body = type.Body;
        var half = Math.Max(type.PanelThickness, type.MullionThickness) / 2;
        dc.DrawGeometry(null, _previewPen,
            BuildOutline(CurtainGeometry.Band(wall, body, cell.ClearFrom, cell.ClearTo, half, -half)));

        var jambFrom = wall.PointAt(body.Structure, cell.ClearFrom, 0);
        var jambTo = wall.PointAt(body.Structure, cell.ClearTo, 0);
        var width = jambFrom.DistanceTo(jambTo);
        if (width <= 1e-6) return;

        var along = (jambTo - jambFrom) / width;
        DrawLeafAndArc(dc, _previewPen, jambFrom, along, -wall.ExteriorNormalAt(cell.ClearFrom), width, 90);
    }

    private void DrawOpening(DrawingContext dc, Opening opening)
    {
        if (Document is null) return;

        var wall = Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId);
        if (wall is null) return;

        // Up in a wall above the cut, a door or window is not seen in this plan.
        if (IsOverhead(wall)) return;

        var wallType = Document.GetWallType(wall);
        var type = Document.FindType<OpeningType>(opening.TypeId);
        if (wallType is null || type is null) return;

        var structure = wallType.Structure;
        var half = wallType.Width / 2;

        var (from, to) = opening.GetSpan(type);

        // The door or window itself is straight, set on the chord between its jambs - which on
        // a straight wall is simply the wall.
        var jambFrom = wall.PointAt(structure, from, 0);
        var jambTo = wall.PointAt(structure, to, 0);
        var along = (jambTo - jambFrom).NormalisedOrDefault(wall.TangentAt(from));
        var across = along.PerpendicularLeft();
        if (across.Dot(wall.ExteriorNormalAt((from + to) / 2)) < 0) across = -across;

        // Distance along the wall to a point on its body centreline. Working in distances
        // rather than points is what lets a symbol ask "is there wall left over here?".
        Point2D At(double distance) => jambFrom + along * (distance - from);

        var isSelected = IsSelected(opening);
        var pen = isSelected ? _selectedPen : _openingPen;

        // The reveals at each end of the hole - across the wall where it is, so radial in a
        // curved one.
        dc.DrawLine(pen, ModelToScreen(wall.PointAt(structure, from, half)), ModelToScreen(wall.PointAt(structure, from, -half)));
        dc.DrawLine(pen, ModelToScreen(wall.PointAt(structure, to, half)), ModelToScreen(wall.PointAt(structure, to, -half)));

        // The symbol says which kind of opening it is and, for a door, how the leaf moves. On
        // a plan that is the whole point of the drawing: a sliding door and a swing door
        // occupy the same hole but need completely different space around them.
        if (opening is Door door && type is DoorType doorType)
        {
            // How much unbroken wall sits either side, which is what a sliding panel needs.
            var clearBefore = WallOpenings.ClearRunBeside(Document, wall, from, to, towardEnd: false);
            var clearAfter = WallOpenings.ClearRunBeside(Document, wall, from, to, towardEnd: true);

            DrawDoorSymbol(dc, door, doorType, At, from, to, clearBefore, clearAfter, along, across, half, pen);
        }
        else if (type is WindowType windowType)
            DrawWindowSymbol(dc, opening, windowType, jambFrom, jambTo, along, across, half, pen);
    }

    private void DrawDoorSymbol(
        DrawingContext dc, Door door, DoorType type, Func<double, Point2D> at,
        double from, double to, double clearBefore, double clearAfter,
        Vector2D along, Vector2D across, double wallHalf, Pen pen)
    {
        // The hole it is drawn in, which for a curtain wall's door panel is the panel itself.
        var width = to - from;
        var facing = door.FlipFacing ? -across : across;
        var hingeAtStart = !door.FlipHand;

        var jambFrom = at(from);
        var jambTo = at(to);
        var centre = jambFrom.MidpointTo(jambTo);

        var hinge = hingeAtStart ? jambFrom : jambTo;
        var toOther = hingeAtStart ? along : -along;

        switch (type.Operation)
        {
            case DoorOperation.Swing:
                DrawLeafAndArc(dc, pen, hinge, toOther, facing, width, door.SwingAngle);
                break;

            case DoorOperation.DoubleSwing:
                // A leaf from each jamb, each half the opening, both opening the same way.
                DrawLeafAndArc(dc, pen, jambFrom, along, facing, width / 2, door.SwingAngle);
                DrawLeafAndArc(dc, pen, jambTo, -along, facing, width / 2, door.SwingAngle);
                break;

            case DoorOperation.Sliding when type.LeafCount >= 2:
            {
                // A twin slider: one leaf is fixed and the other runs across it, both inside
                // the one opening. It needs no wall beside it at all, because it parks over
                // its own partner - which is exactly why it gets used where there is no wall
                // to spare.
                var leaf = width / 2;
                var track = wallHalf * 0.35;

                var fixedFirst = !door.FlipHand;
                var fixedFrom = fixedFirst ? from : from + leaf;
                var slidingFrom = fixedFirst ? from + leaf : from;

                // The fixed leaf, on the outer track.
                dc.DrawLine(pen,
                    ModelToScreen(at(fixedFrom) + facing * track),
                    ModelToScreen(at(fixedFrom + leaf) + facing * track));

                // The sliding leaf where it sits when closed, on the inner track.
                dc.DrawLine(pen,
                    ModelToScreen(at(slidingFrom) - facing * track),
                    ModelToScreen(at(slidingFrom + leaf) - facing * track));

                // And where it goes: over the fixed leaf, not onto the wall.
                dc.DrawLine(_openPanelPen,
                    ModelToScreen(at(fixedFrom) - facing * track),
                    ModelToScreen(at(fixedFrom + leaf) - facing * track));

                DrawArrow(dc, _swingPen,
                    at(fixedFirst ? from + leaf * 0.4 : to - leaf * 0.4) - facing * track,
                    fixedFirst ? -along : along, leaf * 0.35);
                break;
            }

            case DoorOperation.Sliding:
            {
                // A single panel has to go somewhere when it is open, and that somewhere is
                // the unbroken wall beside the opening - not across a window, and not past the
                // end of the wall. Flip Hand chooses the side; the wall overrules it only when
                // there is nothing that way to slide onto.
                var parkAfter = !door.FlipHand;
                if ((parkAfter ? clearAfter : clearBefore) <= 0) parkAfter = !parkAfter;

                var available = parkAfter ? clearAfter : clearBefore;
                var travel = Math.Min(width, Math.Max(0, available));

                var parkFrom = parkAfter ? to : from;
                var parkTo = parkAfter ? to + travel : from - travel;
                var direction = parkAfter ? along : -along;

                // Within the wall's own thickness: the panel runs along it, not past a face.
                var onFace = facing * (wallHalf * 0.35);

                // Where it sits when closed - it really is there, so solid.
                dc.DrawLine(pen, ModelToScreen(jambFrom + onFace), ModelToScreen(jambTo + onFace));

                // And where it goes when open. Dashed, because it is not there now; this is
                // the stretch of wall that cannot carry a radiator, a cupboard or a socket.
                if (travel > 0)
                {
                    dc.DrawLine(_openPanelPen,
                        ModelToScreen(at(parkFrom) + onFace),
                        ModelToScreen(at(parkTo) + onFace));
                }

                DrawArrow(dc, _swingPen,
                    at((from + to) / 2 + (parkAfter ? 1 : -1) * width * 0.3) + onFace,
                    direction, width * 0.25);
                break;
            }

            case DoorOperation.Folding:
            {
                // Leaves concertina outward. Two chevrons read as a bi-fold in plan.
                var peak = width / 4;
                var m1 = jambFrom + along * peak + facing * peak;
                var m2 = jambTo - along * peak + facing * peak;

                dc.DrawLine(pen, ModelToScreen(jambFrom), ModelToScreen(m1));
                dc.DrawLine(pen, ModelToScreen(m1), ModelToScreen(centre));
                dc.DrawLine(pen, ModelToScreen(centre), ModelToScreen(m2));
                dc.DrawLine(pen, ModelToScreen(m2), ModelToScreen(jambTo));
                break;
            }

            case DoorOperation.Revolving:
            {
                // A drum the width of the opening, with the leaves inside it.
                var radius = width / 2;
                dc.DrawEllipse(null, _swingPen, ModelToScreen(centre),
                    radius * PixelsPerMm, radius * PixelsPerMm);

                for (var quarter = 0; quarter < 4; quarter++)
                {
                    var radians = quarter * Math.PI / 2 + Math.PI / 4;
                    var spoke = along * Math.Cos(radians) + facing * Math.Sin(radians);
                    dc.DrawLine(pen, ModelToScreen(centre), ModelToScreen(centre + spoke * radius));
                }

                break;
            }

            case DoorOperation.Overhead:
            {
                // Up-and-over: the panel lifts out of plan. It genuinely does project beyond
                // the wall while opening, so it is shown dashed where it ends up.
                var clear = facing * (wallHalf + width * 0.1);
                dc.DrawLine(_previewPen, ModelToScreen(jambFrom + clear), ModelToScreen(jambTo + clear));
                dc.DrawLine(pen, ModelToScreen(jambFrom), ModelToScreen(jambFrom + clear));
                dc.DrawLine(pen, ModelToScreen(jambTo), ModelToScreen(jambTo + clear));
                break;
            }
        }
    }

    private void DrawWindowSymbol(
        DrawingContext dc, Opening opening, WindowType type,
        Point2D jambFrom, Point2D jambTo, Vector2D along, Vector2D across, double wallHalf, Pen pen)
    {
        var facing = opening.FlipFacing ? -across : across;

        // Frame faces flush with the wall on both sides.
        dc.DrawLine(pen, ModelToScreen(jambFrom + across * wallHalf), ModelToScreen(jambTo + across * wallHalf));
        dc.DrawLine(pen, ModelToScreen(jambFrom - across * wallHalf), ModelToScreen(jambTo - across * wallHalf));

        switch (type.Operation)
        {
            case WindowOperation.Sliding:
                // Two sashes passing each other, so two panes offset either side of centre.
                dc.DrawLine(_glassPen,
                    ModelToScreen(jambFrom + facing * (wallHalf * 0.3)),
                    ModelToScreen(jambFrom.MidpointTo(jambTo) + facing * (wallHalf * 0.3)));
                dc.DrawLine(_glassPen,
                    ModelToScreen(jambFrom.MidpointTo(jambTo) - facing * (wallHalf * 0.3)),
                    ModelToScreen(jambTo - facing * (wallHalf * 0.3)));
                break;

            case WindowOperation.Bay:
                // The glass steps out of the wall rather than sitting in its plane.
                var out1 = jambFrom + along * (type.Width * 0.25) + facing * (wallHalf * 1.6);
                var out2 = jambTo - along * (type.Width * 0.25) + facing * (wallHalf * 1.6);
                dc.DrawLine(_glassPen, ModelToScreen(jambFrom), ModelToScreen(out1));
                dc.DrawLine(_glassPen, ModelToScreen(out1), ModelToScreen(out2));
                dc.DrawLine(_glassPen, ModelToScreen(out2), ModelToScreen(jambTo));
                break;

            default:
                dc.DrawLine(_glassPen, ModelToScreen(jambFrom), ModelToScreen(jambTo));

                // An opening sash swings, so it gets an arc like a door; a fixed light does
                // not, and the difference is what the plan is being read for.
                if (type.Operation is WindowOperation.Casement or WindowOperation.TiltAndTurn)
                    DrawLeafAndArc(dc, _swingPen, jambFrom, along, facing, type.Width, 60);
                else if (type.Operation == WindowOperation.Awning)
                    dc.DrawLine(_previewPen,
                        ModelToScreen(jambFrom + facing * (wallHalf * 1.4)),
                        ModelToScreen(jambTo + facing * (wallHalf * 1.4)));

                break;
        }
    }

    /// <summary>
    /// A hinged leaf and the arc it sweeps - the symbol that makes a swing direction readable
    /// on a plan, and the thing a designer checks when placing furniture.
    /// </summary>
    private void DrawLeafAndArc(
        DrawingContext dc, Pen leafPen, Point2D hinge, Vector2D toOther, Vector2D swingSide,
        double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var closed = hinge + toOther * radius;
        var open = hinge + (toOther * Math.Cos(radians) + swingSide * Math.Sin(radians)) * radius;

        dc.DrawLine(leafPen, ModelToScreen(hinge), ModelToScreen(open));

        // Model Y points up and screen Y points down, so the sweep is mirrored on screen.
        var sweep = toOther.Cross(swingSide) > 0
            ? SweepDirection.Clockwise
            : SweepDirection.Counterclockwise;

        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(ModelToScreen(closed), isFilled: false, isClosed: false);
            ctx.ArcTo(ModelToScreen(open),
                new Size(radius * PixelsPerMm, radius * PixelsPerMm),
                rotationAngle: 0,
                isLargeArc: degrees > 180,
                sweepDirection: sweep,
                isStroked: true,
                isSmoothJoin: false);
        }

        arc.Freeze();
        dc.DrawGeometry(null, _swingPen, arc);
    }

    /// <summary>A small arrowhead, used to show which way a sliding panel travels.</summary>
    private void DrawArrow(DrawingContext dc, Pen pen, Point2D tip, Vector2D direction, double size)
    {
        var back = -direction * size;
        var side = direction.PerpendicularLeft() * (size * 0.35);

        dc.DrawLine(pen, ModelToScreen(tip), ModelToScreen(tip + back + side));
        dc.DrawLine(pen, ModelToScreen(tip), ModelToScreen(tip + back - side));
        dc.DrawLine(pen, ModelToScreen(tip), ModelToScreen(tip + back * 1.8));
    }

    // ---- rooms and slabs -------------------------------------------------------

    /// <summary>A roof window seen from above: its frame, the glass inside it, and a line across the glass.</summary>
    private void DrawRoofWindow(DrawingContext dc, RoofWindow window)
    {
        if (Document is null || RoofWindows.Frame(Document, window) is not { } frame) return;

        var pen = IsSelected(window) ? _selectedPen : _roofLinePen;
        dc.DrawGeometry(null, pen, BuildOutline(frame.Corners));
        dc.DrawGeometry(null, pen, BuildOutline(frame.Glass));

        var glass = frame.Glass;
        dc.DrawLine(pen, ModelToScreen(glass[0]), ModelToScreen(glass[2]));
    }

    /// <summary>
    /// A shaft opening as a plan draws one: its outline with a cross through it - a hole all
    /// the way down, not a thing standing there.
    /// </summary>
    private void DrawShaft(DrawingContext dc, ShaftOpening shaft)
    {
        var outline = shaft.Outline;
        if (outline.Count < 3) return;

        var pen = IsSelected(shaft) ? _selectedPen : _roofLinePen;
        dc.DrawGeometry(null, pen, BuildOutline(outline));

        foreach (var (from, to) in shaft.Cross) dc.DrawLine(pen, ModelToScreen(from), ModelToScreen(to));
    }

    /// <summary>
    /// A downpipe seen from above: its section where it runs down the wall, and the swan neck from
    /// the gutter's outlet back to it.
    /// </summary>
    private void DrawDownpipe(DrawingContext dc, Downpipe pipe)
    {
        if (Document is null || Downpipes.Path(Document, pipe) is not { } path) return;

        var pen = IsSelected(pipe) ? _selectedPen : _roofLinePen;
        dc.DrawGeometry(null, pen, BuildOutline(path.Footprint));
        if (path.Points[0].Plan.DistanceTo(path.Foot) > 1) dc.DrawLine(pen, ModelToScreen(path.Points[0].Plan), ModelToScreen(path.Foot));
    }

    /// <summary>The downpipes drawn on this plan: on their gutter's storey, and on every one they pass down through.</summary>
    private IEnumerable<Downpipe> DownpipesOnPlan() =>
        Document is null
            ? Enumerable.Empty<Downpipe>()
            : Document.Elements.OfType<Downpipe>().Where(pipe => Filter(pipe) &&
                (pipe.LevelId == ActiveLevelId ||
                 (Document.FindLevel(ActiveLevelId) is { } level && Downpipes.Path(Document, pipe) is { } path &&
                  level.Elevation >= path.Bottom - 1 && level.Elevation < path.Points[0].Z)));

    /// <summary>The shafts drawn on this plan: those on its level, and those passing up through it.</summary>
    private IEnumerable<ShaftOpening> ShaftsOnPlan() =>
        Document is null
            ? Enumerable.Empty<ShaftOpening>()
            : Document.Elements.OfType<ShaftOpening>().Where(shaft => Shafts.ShownOn(Document, shaft, ActiveLevelId) && Filter(shaft));

    private void DrawRoofEdgeSweep(DrawingContext dc, RoofEdgeSweep sweep)
    {
        if (Document is null) return;

        var pen = IsSelected(sweep) ? _selectedPen : _roofLinePen;
        foreach (var footprint in RoofEdgeSweeps.Footprints(Document, sweep))
            if (footprint.Count >= 3) dc.DrawGeometry(null, pen, BuildOutline(footprint));
    }

    /// <summary>
    /// Draws a floor, ceiling or roof as its outline over a faint fill.
    ///
    /// A floor plan is a horizontal cut through the building, so the slab below the cut is not
    /// what the drawing is about - it is shown quietly, enough to say it is there and to be
    /// picked, without competing with the walls.
    /// </summary>
    private void DrawSlab(DrawingContext dc, Slab slab)
    {
        if (Document is null || slab.Boundary.Count < 3) return;

        var type = Document.FindType<SlabType>(slab.TypeId);
        if (type is null) return;

        var isSelected = IsSelected(slab);

        // A floor or ceiling with shafts through it, or cut back from a curtain wall: drawn as built.
        if (slab is not Roof && SlabEdges.IsCut(Document, slab))
        {
            foreach (var region in SlabEdges.Regions(Document, slab))
                dc.DrawGeometry(SlabBrush(type), isSelected ? _selectedPen : _slabPen, BuildLoops(Shafts.Loops(region)));
            return;
        }

        // A roof joined to another shows only where it is above it: its edge there is the
        // valley where the two meet, not the footprint carried back under the other roof.
        var outlines = slab is Roof { JoinedTo: not null } joined
            ? RoofJoin.Visible(Document, joined)
            : new[] { slab.Boundary };

        foreach (var outline in outlines)
            dc.DrawGeometry(SlabBrush(type), isSelected ? _selectedPen : _slabPen, BuildOutline(outline));

        if (slab is Roof roof)
        {
            DrawRoofLines(dc, roof, isSelected, outlines);

            // Where the roof is cut away for a dormer, the hole's outline.
            foreach (var opening in RoofJoin.Openings(Document, roof))
                dc.DrawGeometry(null, isSelected ? _selectedPen : _slabPen, BuildOutline(opening));
        }
    }

    /// <summary>
    /// What a pitched roof looks like in plan: the ridges, hips and valleys where its faces
    /// meet, and - while it is selected - an arrow up each sloping edge.
    ///
    /// The break lines are the drawing. Without them a hip roof and a flat roof are the same
    /// rectangle, and the plan says nothing about the shape of the building. The arrows are
    /// only shown on selection because they are an editing aid, not part of the drawing: they
    /// say which edges were given a slope, which is otherwise invisible once the roof is made.
    /// </summary>
    private void DrawRoofLines(DrawingContext dc, Roof roof, bool isSelected, IReadOnlyList<IReadOnlyList<Point2D>> shown)
    {
        if (Document is null || roof.Form == RoofForm.Flat) return;

        var surface = roof.Surface(Document);
        var trimmed = roof.JoinedTo is not null;

        foreach (var (from, to) in surface.BreakLines)
        {
            // A joined roof's ridges stop where it runs under the other roof.
            if (trimmed && !shown.Any(outline => Polygon2D.Contains(outline, from.MidpointTo(to)))) continue;
            dc.DrawLine(_roofLinePen, ModelToScreen(from), ModelToScreen(to));
        }

        if (!isSelected) return;

        // An arc is built from many short edges but is one line: its arrow goes once, at its
        // middle piece, not on every one.
        var middles = roof.Edges
            .Select((edge, index) => (edge.ArcId, Index: index))
            .Where(entry => entry.ArcId is not null)
            .GroupBy(entry => entry.ArcId)
            .Select(run => run.Select(entry => entry.Index).OrderBy(index => index).ToList())
            .Select(indices => indices[indices.Count / 2])
            .ToHashSet();

        for (var i = 0; i < roof.Edges.Count && i < roof.Boundary.Count; i++)
        {
            if (!roof.Edges[i].DefinesSlope) continue;
            if (roof.Edges[i].ArcId is not null && !middles.Contains(i)) continue;

            var from = roof.Boundary[i];
            var to = roof.Boundary[(i + 1) % roof.Boundary.Count];
            var along = (to - from).NormalisedOrDefault(default);
            if (along.Length <= 0) continue;

            // Uphill is square to the eave, into the building - the direction water runs down.
            var uphill = Polygon2D.SignedArea(roof.Boundary) >= 0
                ? along.PerpendicularLeft()
                : -along.PerpendicularLeft();

            DrawSlopeArrow(dc, from.MidpointTo(to), uphill);
        }
    }

    /// <summary>An arrow pointing up the slope, drawn at a fixed size on the paper.</summary>
    private void DrawSlopeArrow(DrawingContext dc, Point2D at, Vector2D uphill)
    {
        var start = ModelToScreen(at);
        var direction = new Vector(
            ModelToScreen(at + uphill).X - start.X,
            ModelToScreen(at + uphill).Y - start.Y);

        var length = direction.Length;
        if (length <= 0) return;

        direction /= length;
        var side = new Vector(-direction.Y, direction.X);
        var tip = start + direction * 26;

        dc.DrawLine(_roofSlopePen, start, tip);
        dc.DrawLine(_roofSlopePen, tip, tip - direction * 7 + side * 4);
        dc.DrawLine(_roofSlopePen, tip, tip - direction * 7 - side * 4);
    }

    /// <summary>
    /// Fills the floor area a room occupies. The outline is re-traced every frame, so the fill
    /// follows the walls as they are dragged rather than lagging behind them.
    /// </summary>
    private void DrawRoomFill(DrawingContext dc, Room room)
    {
        if (Document is null) return;

        var boundary = room.GetBoundary(Document);
        if (!boundary.IsEnclosed) return;

        var isSelected = IsSelected(room);

        dc.DrawGeometry(
            isSelected ? _roomSelectedBrush : _roomBrush,
            isSelected ? _selectedPen : null,
            BuildOutline(boundary.Polygon));
    }

    /// <summary>
    /// An architectural column in plan: its section, filled the way it is built.
    ///
    /// At coarse detail a column joined to a wall takes that wall's fill pattern, so the two
    /// read as one piece of construction rather than an object standing in front of a wall -
    /// which is what a column built into a wall actually is. Drawn finer, it is the material it
    /// is made of, the same way the wall beside it is drawn as its layers rather than its fill.
    /// </summary>
    private void DrawColumn(DrawingContext dc, Column column)
    {
        if (Document?.FindType<ColumnType>(column.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0) return;

        var fill = DetailLevel == DetailLevel.Coarse
            ? RenderPens.Fill(RenderPens.ToMediaColor(ColumnJoins.CoarseFill(Document, column, type)))
            : MaterialBrush(ColumnJoins.CutMaterial(Document, column, type));

        // Less whatever wall it is buried in, at the height the plan cuts it.
        var section = ColumnJoins.CutByWalls(
            Document, column, type, column.SectionAt(type), column.GetBaseElevation(Document) + 1000);

        if (section.IsEmpty) return;

        var pen = IsSelected(column) ? _selectedPen : _wallOutlinePen;

        dc.DrawGeometry(fill, pen, BuildOutline(section.Outer));
        foreach (var hole in section.Holes) dc.DrawGeometry(null, pen, BuildOutline(hole));
    }

    /// <summary>
    /// A component in plan: its footprint, and enough of what is inside it to tell one from
    /// another at a glance - the back of a chair, the bowl of a basin, the spread of a tree.
    ///
    /// It is drawn as the family says it is shaped, so a plan cannot show a table the schedule
    /// does not, and a family widened in its type is wider here without anyone redrawing it.
    /// </summary>
    private void DrawComponent(DrawingContext dc, Component component)
    {
        if (Document?.FindType<ComponentType>(component.TypeId) is not { } type) return;
        if (type.Width <= 0 || type.Depth <= 0) return;

        var frame = ComponentModel.FrameOf(component, 0);
        var outlines = ComponentModel.PlanOutlines(type, frame);
        var pen = IsSelected(component) ? _selectedPen : _componentPen;

        // The footprint is filled; what is inside it is drawn in line only, over the top.
        dc.DrawGeometry(_componentBrush, pen, BuildOutline(outlines[0]));
        foreach (var outline in outlines.Skip(1)) dc.DrawGeometry(null, pen, BuildOutline(outline));
    }

    /// <summary>
    /// The room's tag: its name, number and area, which is what a floor plan is read for. An
    /// unenclosed room says so instead, because a silent zero is how bad area schedules reach
    /// a client.
    /// </summary>
    private void DrawRoomTag(DrawingContext dc, Room room)
    {
        if (Document is null) return;

        var boundary = room.GetBoundary(Document);
        var anchor = boundary.IsEnclosed ? boundary.Centroid : room.Location;

        var heading = Text(
            string.IsNullOrWhiteSpace(room.Number) ? room.Name : $"{room.Name}  {room.Number}",
            Page(12), _roomTagBrush, bold: true);

        var detail = Text(
            boundary.IsEnclosed ? Units.FormatArea(boundary.Area) : "Not enclosed",
            Page(11), boundary.IsEnclosed ? _roomTagBrush : _unenclosedBrush);

        var centre = ModelToScreen(anchor);
        var width = Math.Max(heading.Width, detail.Width);
        var height = heading.Height + detail.Height + Page(2);

        var backdrop = new Rect(
            centre.X - width / 2 - Page(6), centre.Y - height / 2 - Page(4),
            width + Page(12), height + Page(8));

        dc.DrawRoundedRectangle(_labelBackdrop, null, backdrop, Page(3), Page(3));
        dc.DrawText(heading, new Point(centre.X - heading.Width / 2, centre.Y - height / 2));
        dc.DrawText(detail,
            new Point(centre.X - detail.Width / 2, centre.Y - height / 2 + heading.Height + Page(2)));

        // A cross marks the point the room is anchored to, so an unenclosed one can be found.
        if (boundary.IsEnclosed) return;

        var marker = ModelToScreen(room.Location);
        var arm = Page(7);
        dc.DrawLine(_roomPen, new Point(marker.X - arm, marker.Y), new Point(marker.X + arm, marker.Y));
        dc.DrawLine(_roomPen, new Point(marker.X, marker.Y - arm), new Point(marker.X, marker.Y + arm));
    }

    // ---- datums ----------------------------------------------------------------

    /// <summary>
    /// Draws a gridline and its bubbles.
    ///
    /// The bubble is the whole point of a grid: it is the label everything else on the sheet
    /// is dimensioned from, so it is sized on the page rather than in the building - a head
    /// you cannot read is a head that is not doing its job.
    /// </summary>
    private void DrawGrid(DrawingContext dc, Grid grid)
    {
        var isSelected = IsSelected(grid);
        var pen = isSelected ? _selectedPen : _gridLinePen;

        var start = ModelToScreen(grid.Start);
        var end = ModelToScreen(grid.End);
        dc.DrawLine(pen, start, end);

        if (grid.BubbleAtStart) DrawGridBubble(dc, grid, start, isSelected);
        if (grid.BubbleAtEnd) DrawGridBubble(dc, grid, end, isSelected);
    }

    private void DrawGridBubble(DrawingContext dc, Grid grid, Point centre, bool isSelected)
    {
        var radius = Page(11);

        dc.DrawEllipse(_gridBubbleBrush, isSelected ? _selectedPen : _gridLinePen, centre, radius, radius);

        var text = Text(grid.Name, Page(12), _gridTextBrush, bold: true);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

    /// <summary>
    /// Draws a section marker: the cut line, with a head at each end carrying the section's
    /// letter and an arrow showing which way the view looks.
    /// </summary>
    private void DrawSectionMarker(DrawingContext dc, SectionMarker section)
    {
        if (section.Length <= 0) return;

        var isSelected = IsSelected(section);

        var start = ModelToScreen(section.Start);
        var end = ModelToScreen(section.End);
        dc.DrawLine(isSelected ? _selectedPen : _sectionPen, start, end);

        // Model Y points up and screen Y points down, so the look direction flips on the way
        // to the screen.
        var look = new Vector(section.LookDirection.X, -section.LookDirection.Y);
        var headPen = isSelected ? _selectedPen : _sectionHeadPen;

        DrawSectionHead(dc, section, start, look, headPen, isSelected);
        DrawSectionHead(dc, section, end, look, headPen, isSelected);
    }

    private void DrawSectionHead(
        DrawingContext dc, SectionMarker section, Point centre, Vector look, Pen pen, bool isSelected)
    {
        var radius = Page(11);
        var arrowLength = Page(15);
        var arrowHalfWidth = Page(5);
        var barb = Page(7);

        dc.DrawEllipse(_gridBubbleBrush, pen, centre, radius, radius);

        var text = Text(section.Name, Page(12), _sectionTextBrush, bold: true);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));

        // The arrow is what makes a section readable: it says which way you are facing, and
        // therefore which half of the building the drawing shows.
        var tail = centre + look * radius;
        var tip = centre + look * (radius + arrowLength);
        var across = new Vector(-look.Y, look.X);

        dc.DrawLine(pen, tail, tip);

        var arrowhead = new StreamGeometry();
        using (var geometry = arrowhead.Open())
        {
            geometry.BeginFigure(tip, isFilled: true, isClosed: true);
            geometry.LineTo(tip - look * barb + across * arrowHalfWidth, true, false);
            geometry.LineTo(tip - look * barb - across * arrowHalfWidth, true, false);
        }

        arrowhead.Freeze();
        dc.DrawGeometry(isSelected ? _selectedArrowBrush : _sectionArrowBrush, null, arrowhead);
    }

    // ---- annotation ------------------------------------------------------------

    /// <summary>
    /// Draws a dimension: witness lines out to the dimension line, ticks at each end, and the
    /// measurement itself.
    ///
    /// The measurement is read from the model each time, so it cannot disagree with what it
    /// dimensions. A dimension whose ends are only points, not references, is drawn in a
    /// different colour - the reader should be able to see which numbers will keep up with the
    /// building and which will not.
    /// </summary>
    private void DrawDimension(DrawingContext dc, Dimension dimension)
    {
        if (Document is null) return;

        var from = dimension.StartPoint(Document);
        var to = dimension.EndPoint(Document);
        if (from.DistanceTo(to) < 1) return;

        var (lineFrom, lineTo) = dimension.GetDimensionLine(Document);
        var isSelected = IsSelected(dimension);
        var attached = dimension.IsAssociative(Document);

        DrawDimensionGeometry(dc, from, to, lineFrom, lineTo,
            isSelected ? _selectedPen : attached ? _dimensionPen : _looseDimensionPen,
            attached ? _dimensionTextBrush : _looseDimensionBrush,
            dimension.DisplayText(Document));
    }

    /// <summary>
    /// The dimension being drawn, following the cursor with its measurement live.
    ///
    /// Seeing the number before committing is most of the value: a dimension is placed to find
    /// out what something measures as often as to report it.
    /// </summary>
    public void DrawPendingDimension(DrawingContext dc, Point2D from, Point2D to)
    {
        if (from.DistanceTo(to) < 1) return;

        // The same offset a placed dimension gets, so the preview shows where it will land.
        var normal = (to - from).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft();
        const double offset = 600;

        DrawDimensionGeometry(dc, from, to, from + normal * offset, to + normal * offset,
            _previewPen, _dimensionTextBrush, Units.FormatLength(from.DistanceTo(to)));
    }

    /// <summary>
    /// The parts of a dimension, shared by a placed one and the one being drawn.
    ///
    /// Witness lines and ticks are sized on the page and stay the same however far you zoom -
    /// exactly as they print at a fixed size on a sheet whatever the drawing's scale.
    /// Construction scales with the model; annotation does not.
    /// </summary>
    private void DrawDimensionGeometry(
        DrawingContext dc, Point2D from, Point2D to, Point2D lineFrom, Point2D lineTo,
        Pen pen, Brush textBrush, string label)
    {
        var witnessOvershoot = Page(7);
        var witnessGap = Page(3);
        var tickHalfLength = Page(5);

        DrawWitnessLine(dc, pen, from, lineFrom, witnessGap, witnessOvershoot);
        DrawWitnessLine(dc, pen, to, lineTo, witnessGap, witnessOvershoot);

        dc.DrawLine(pen, ModelToScreen(lineFrom), ModelToScreen(lineTo));

        // Ticks: the 45-degree slash architectural dimensions use instead of arrows.
        var along = (lineTo - lineFrom).NormalisedOrDefault(Vector2D.UnitX);
        var slash = along + along.PerpendicularLeft();
        var tick = slash.NormalisedOrDefault(Vector2D.UnitX) * (tickHalfLength / PixelsPerMm);

        dc.DrawLine(pen, ModelToScreen(lineFrom - tick), ModelToScreen(lineFrom + tick));
        dc.DrawLine(pen, ModelToScreen(lineTo - tick), ModelToScreen(lineTo + tick));

        var text = Text(label, Page(11.5), textBrush, bold: true);

        var centre = ModelToScreen(lineFrom.MidpointTo(lineTo));
        var origin = new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2);

        dc.DrawRoundedRectangle(_labelBackdrop, null,
            new Rect(origin.X - Page(4), origin.Y - Page(1), text.Width + Page(8), text.Height + Page(2)),
            Page(2), Page(2));

        dc.DrawText(text, origin);
    }

    /// <summary>
    /// A witness line: from just clear of the thing measured, out to just past the dimension
    /// line. The small gap at the element is conventional - it keeps the annotation visibly
    /// separate from the construction it describes.
    /// </summary>
    private void DrawWitnessLine(
        DrawingContext dc, Pen pen, Point2D measured, Point2D onDimensionLine, double gap, double overshoot)
    {
        var start = ModelToScreen(measured);
        var end = ModelToScreen(onDimensionLine);

        var span = end - start;
        var length = Math.Sqrt(span.X * span.X + span.Y * span.Y);
        if (length < 1) return;

        var direction = new Vector(span.X / length, span.Y / length);

        dc.DrawLine(pen, start + direction * Math.Min(gap, length), end + direction * overshoot);
    }

    /// <summary>
    /// Draws a tag: the value it reads off its element, with a leader back to it. The text is
    /// read now, not stored, so it can never say something the model does not.
    /// </summary>
    private void DrawTag(DrawingContext dc, Tag tag)
    {
        if (Document is null) return;

        var isSelected = IsSelected(tag);
        var text = Text(tag.Read(Document), Page(12), _tagTextBrush, bold: true);

        var anchor = ModelToScreen(tag.Position);
        var box = new Rect(
            anchor.X - text.Width / 2 - Page(7), anchor.Y - text.Height / 2 - Page(4),
            text.Width + Page(14), text.Height + Page(8));

        if (tag.ShowLeader && AnchorOf(tag.Target(Document)) is { } target)
            dc.DrawLine(_leaderPen, ModelToScreen(target), new Point(box.Left, box.Bottom));

        dc.DrawRoundedRectangle(_tagBrush, isSelected ? _selectedPen : _leaderPen, box, Page(3), Page(3));
        dc.DrawText(text, new Point(anchor.X - text.Width / 2, anchor.Y - text.Height / 2));
    }

    private void DrawTextNote(DrawingContext dc, TextNote note)
    {
        var isSelected = IsSelected(note);
        var text = Text(note.Text, Page(12), _tagTextBrush);

        var anchor = ModelToScreen(note.Position);
        var box = new Rect(
            anchor.X, anchor.Y - text.Height / 2,
            text.Width + Page(10), text.Height + Page(6));

        if (note.LeaderEnd is { } leader)
            dc.DrawLine(_leaderPen, ModelToScreen(leader), new Point(box.Left, box.Bottom));

        dc.DrawRoundedRectangle(_labelBackdrop, isSelected ? _selectedPen : null, box, Page(2), Page(2));
        dc.DrawText(text, new Point(anchor.X + Page(5), anchor.Y - text.Height / 2 + Page(3)));
    }

    /// <summary>A point on an element for a leader to aim at.</summary>
    private Point2D? AnchorOf(Element? element)
    {
        if (Document is null || element is null) return null;

        return element switch
        {
            Wall wall => wall.Start.MidpointTo(wall.End),
            Opening opening when Document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId) is { } host
                => opening.GetCentre(host),
            Room room => room.GetBoundary(Document) is { IsEnclosed: true } boundary
                ? boundary.Centroid
                : room.Location,
            Slab slab => slab.Centroid,
            Grid grid => grid.Start.MidpointTo(grid.End),
            SectionMarker section => section.Start.MidpointTo(section.End),
            _ => null
        };
    }

    // ---- shared plumbing -------------------------------------------------------

    /// <summary>A closed polygon in model coordinates, converted to a screen geometry.</summary>
    /// <summary>A shape of several loops - an outline and the holes in it - filled between them.</summary>
    private StreamGeometry BuildLoops(IReadOnlyList<IReadOnlyList<Point2D>> loops)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };

        using (var ctx = geometry.Open())
        {
            foreach (var loop in loops.Where(loop => loop.Count >= 3))
            {
                ctx.BeginFigure(ModelToScreen(loop[0]), isFilled: true, isClosed: true);
                ctx.PolyLineTo(loop.Skip(1).Select(ModelToScreen).ToArray(), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    public StreamGeometry BuildOutline(IReadOnlyList<Point2D> corners)
    {
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(ModelToScreen(corners[0]), isFilled: true, isClosed: true);
            ctx.PolyLineTo(corners.Skip(1).Select(ModelToScreen).ToArray(), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private FormattedText Text(string value, double size, Brush brush, bool bold = false) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        bold
            ? new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal)
            : new Typeface("Segoe UI"),
        Math.Max(1, size),
        brush,
        PixelsPerDip);

    /// <summary>The fill for a pane of glass that is not clear, in the colour that glass reads as.</summary>
    private static Brush GlassBrush(CurtainGlass glass) =>
        RenderPens.Fill(RenderPens.ToMediaColor(CurtainGlassLook.ColourOf(glass, new BIMDesigner.Core.Materials.ColourRgb(0x8C, 0xC4, 0xE0))));

    private Brush MaterialBrush(Guid materialId)
    {
        if (_materialBrushes.TryGetValue(materialId, out var cached)) return cached;

        var material = Document?.FindMaterial(materialId);
        var brush = RenderPens.Fill(material is null
            ? Color.FromRgb(0x6A, 0x6A, 0x6A)
            : RenderPens.ToMediaColor(material.CutColour));

        _materialBrushes[materialId] = brush;
        return brush;
    }

    /// <summary>A slab's fill, taken from its type's colour but kept faint.</summary>
    private Brush SlabBrush(SlabType type)
    {
        if (_slabBrushes.TryGetValue(type.Id, out var cached)) return cached;

        var colour = type.CoarseScaleFillColour;
        var brush = RenderPens.Fill(Color.FromArgb(0x28, colour.R, colour.G, colour.B));

        _slabBrushes[type.Id] = brush;
        return brush;
    }

    private Brush CoarseBrush(WallType type)
    {
        if (_coarseBrushes.TryGetValue(type.Id, out var cached)) return cached;

        var brush = RenderPens.Fill(RenderPens.ToMediaColor(type.CoarseScaleFillColour));
        _coarseBrushes[type.Id] = brush;
        return brush;
    }
}
