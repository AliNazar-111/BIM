using System.Windows;
using System.Windows.Media;
using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.UI.Controls;

/// <summary>How the Mass tool draws a mass's footprint.</summary>
public enum MassFootprint
{
    /// <summary>Two opposite corners.</summary>
    Box,

    /// <summary>Its centre, then a point on it.</summary>
    Circle,

    /// <summary>Its corners clicked round, the first again to close it.</summary>
    Outline
}

/// <summary>
/// Masses on the plan - Revit's In-Place Mass and Model by Face. The Mass tool draws a mass's
/// footprint, so high and so topped; Wall, Curtain System, Floor and Roof by Face make its faces
/// into the building, each remembered so Update to Face brings it back when the mass changes.
/// </summary>
public partial class PlanView
{
    public MassFootprint ActiveMassFootprint { get; set; } = MassFootprint.Box;

    public MassTop ActiveMassTop { get; set; } = MassTop.Flat;

    public double ActiveMassHeight { get; set; } = 9000;

    public double ActiveMassPitch { get; set; } = 35;

    public double ActiveMassTopScale { get; set; } = 0.5;

    private Point2D? _massStart;

    private static readonly Brush MassFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x8E, 0x5C, 0xC8)));
    private static readonly Pen MassPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x5C, 0xC8))), 1.4) { DashStyle = DashStyles.Dash });
    private static readonly Pen MassFloorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x5C, 0xC8))), 2.2));

    /// <summary>A mass of the tool's settings on this storey, round or of an outline.</summary>
    private Mass NewMass() => new()
    {
        LevelId = ActiveLevelId, Height = Math.Max(300, ActiveMassHeight), Top = ActiveMassTop,
        Pitch = Math.Clamp(ActiveMassPitch, 1, 75), TopScale = Math.Clamp(ActiveMassTopScale, 0, 0.999)
    };

    /// <summary>Makes a mass of an outline drawn - the Mass tool's outline closed, or a box. Public so it can be driven from tests.</summary>
    public Mass? MakeMass(IReadOnlyList<Point2D> outline)
    {
        if (Document is null || outline.Count < 3 || Polygon2D.Area(outline) < 1e6) return null;
        var mass = NewMass();
        if (mass.Top is MassTop.Gable or MassTop.Hip or MassTop.Shed && outline.Count < 3) mass.Top = MassTop.Flat;
        mass.SetBoundary(outline);
        Apply(new AddElementCommand(Document, mass, "Mass"));
        AfterMass(mass);
        return mass;
    }

    /// <summary>Makes a round mass - a cylinder, or a cone tapered. Public so it can be driven from tests.</summary>
    public Mass? MakeRoundMass(Point2D centre, double radius)
    {
        if (Document is null || radius < 500) return null;
        var mass = NewMass();
        if (mass.Top is MassTop.Gable or MassTop.Hip or MassTop.Shed) mass.Top = MassTop.Flat;
        mass.SetCircle(centre, radius);
        Apply(new AddElementCommand(Document, mass, "Mass"));
        AfterMass(mass);
        return mass;
    }

    private void AfterMass(Mass mass)
    {
        HintChanged?.Invoke(this, $"Mass: {Polygon2D.Area(mass.Boundary) / 1e6:0.0} m² on the ground, {Units.FormatLength(mass.Height)} high, {Masses.Volume(mass) / 1e9:0} m³. " +
                                  "Draw another, or make its faces into walls, floors and a roof with the By Face tools.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>A click with the Mass tool drawing a box or a circle: its first corner or centre, then the mass.</summary>
    public bool MassClick(Point2D at)
    {
        if (Document is null) return false;
        if (_massStart is not { } start)
        {
            _massStart = at;
            HintChanged?.Invoke(this, ActiveMassFootprint == MassFootprint.Circle ? "Click a point on its circle." : "Click its opposite corner.");
            InvalidateVisual();
            return true;
        }

        _massStart = null;
        var made = ActiveMassFootprint == MassFootprint.Circle
            ? MakeRoundMass(start, start.DistanceTo(at))
            : MakeMass(new[] { start, new Point2D(at.X, start.Y), at, new Point2D(start.X, at.Y) });
        if (made is null) HintChanged?.Invoke(this, "Too small for a mass: draw it bigger.");
        InvalidateVisual();
        return made is not null;
    }

    private bool CancelMass()
    {
        var pending = _massStart is not null;
        _massStart = null;
        return pending;
    }

    /// <summary>The masses this plan's storey cuts: each with where it is cut.</summary>
    private IEnumerable<(Mass Mass, IReadOnlyList<IReadOnlyList<Point2D>> Cut, bool Floor)> MassesOnPlan()
    {
        if (Document?.FindLevel(ActiveLevelId) is not { } level) yield break;
        foreach (var mass in Document.All<Mass>().Where(mass => mass.Boundary.Count >= 3 && _shows(mass)))
        {
            var cut = Masses.Section(Document, mass, level.Elevation + 1);
            if (cut.Count > 0) yield return (mass, cut, mass.FloorLevels.Contains(level.Id));
        }
    }

    /// <summary>The masses under a point of this plan: inside where it is cut, or on its edge.</summary>
    private IEnumerable<Mass> MassesAt(Point2D model)
    {
        var reach = 6 / PixelsPerMm;
        foreach (var (mass, cut, _) in MassesOnPlan())
            if (cut.Any(outline => Polygon2D.Contains(outline, model)
                                   || Enumerable.Range(0, outline.Count).Min(i => Line2D.DistanceFromSegment(model, outline[i], outline[(i + 1) % outline.Count])) <= reach))
                yield return mass;
    }

    /// <summary>The masses: see-through in purple, dashed, where this storey cuts them - a mass floor here drawn bold.</summary>
    private void DrawMasses(DrawingContext dc)
    {
        foreach (var (mass, cut, floor) in MassesOnPlan())
        foreach (var outline in cut)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(ModelToScreen(outline[0]), true, true);
                foreach (var point in outline.Skip(1)) context.LineTo(ModelToScreen(point), true, false);
            }

            geometry.Freeze();
            dc.DrawGeometry(MassFill, _selection.Contains(mass) ? SketchSelectedPen : floor ? MassFloorPen : MassPen, geometry);
        }

        // The box or circle being drawn.
        if (ActiveTool == PlanTool.Mass && _massStart is { } start && _cursorModel is var cursor)
        {
            if (ActiveMassFootprint == MassFootprint.Circle)
            {
                var radius = start.DistanceTo(cursor) * PixelsPerMm;
                dc.DrawEllipse(null, SketchPreviewPen, ModelToScreen(start), radius, radius);
            }
            else
            {
                var (a, b) = (ModelToScreen(start), ModelToScreen(cursor));
                dc.DrawRectangle(null, SketchPreviewPen, new Rect(a, b));
            }
        }
    }

    // ---- by face ----------------------------------------------------------------------

    /// <summary>What a By Face tool makes, in words.</summary>
    private static string ByFaceNoun(PlanTool tool) => tool switch
    {
        PlanTool.CurtainByFace => "curtain system",
        PlanTool.FloorByFace => "floors",
        PlanTool.RoofByFace => "roof",
        _ => "wall"
    };

    /// <summary>A click with a By Face tool in plan: the side of a mass under it for a wall or curtain system; the mass under it for its floors or roof.</summary>
    public bool ByFaceAt(Point2D raw)
    {
        if (Document is null) return false;
        if (ActiveTool is PlanTool.WallByFace or PlanTool.CurtainByFace)
        {
            var reach = 10 / PixelsPerMm;
            foreach (var (mass, cut, _) in MassesOnPlan())
            {
                var outline = cut.FirstOrDefault(candidate => candidate.Count == mass.Boundary.Count) ?? mass.Boundary;
                var side = Masses.SideAt(outline, raw, reach);
                if (side >= 0) return MakeByFace(mass, side);
            }

            HintChanged?.Invoke(this, $"Click the edge of a mass - the {ByFaceNoun(ActiveTool)} goes on that face.");
            return false;
        }

        if (MassesAt(raw).FirstOrDefault() is { } picked) return MakeByFace(picked, -1);
        HintChanged?.Invoke(this, $"Click a mass - its {ByFaceNoun(ActiveTool)} {(ActiveTool == PlanTool.FloorByFace ? "are" : "is")} made from it.");
        return false;
    }

    /// <summary>A click with a By Face tool on a mass in 3D: the face it landed on.</summary>
    public bool ByFaceIn3D(Guid id, CorePoint3D at)
    {
        if (Document?.FindElement(id) is not Mass mass) return false;
        var face = Masses.FaceAt(Document, mass, at);
        return MakeByFace(mass, face is { Kind: MassFaceKind.Side } side ? side.Index : -1);
    }

    /// <summary>
    /// A mass's face made into the building with the active By Face tool: a side into a wall or
    /// curtain wall, the mass into its floors - at its mass floors, every level through it if it
    /// has none yet - or its top into a roof. Remembered by the mass, one step to undo.
    /// </summary>
    public bool MakeByFace(Mass mass, int side)
    {
        if (Document is null) return false;
        var made = new List<(Element, MassLink)>();
        IUndoableCommand? floorsFirst = null;
        string? why = null;

        switch (ActiveTool)
        {
            case PlanTool.WallByFace or PlanTool.CurtainByFace:
            {
                if (side < 0)
                {
                    HintChanged?.Invoke(this, "That is the mass's top or bottom: a wall goes on a side. Roof by Face makes its top a roof.");
                    return false;
                }

                var curtain = ActiveTool == PlanTool.CurtainByFace;
                var typeId = curtain
                    ? Document.FindType<CurtainWallType>(ActiveWallTypeId)?.Id ?? Document.TypesOf<CurtainWallType>().FirstOrDefault(type => !type.AutomaticallyEmbed)?.Id ?? Document.TypesOf<CurtainWallType>().FirstOrDefault()?.Id
                    : Document.FindType<WallType>(ActiveWallTypeId)?.Id ?? Document.TypesOf<WallType>().FirstOrDefault(type => type.Function == WallFunction.Exterior)?.Id;
                if (typeId is not { } chosen) return false;

                // A curtain system has no gable profile, nor leans: it stops at the eaves of a sloping top.
                if (curtain && mass.Top == MassTop.Tapered)
                {
                    HintChanged?.Invoke(this, "A curtain system goes on an upright face: this one leans.");
                    return false;
                }

                var walls = Masses.WallsByFace(Document, mass, side, chosen, out why);
                if (walls is null) break;
                foreach (var wall in walls)
                {
                    if (curtain)
                    {
                        wall.Profile = null;
                        wall.UnconnectedHeight = mass.Height;
                    }

                    made.Add((wall, new MassLink(wall.Id, curtain ? MassLinkKind.CurtainWall : MassLinkKind.Wall, side, null)));
                }

                break;
            }

            case PlanTool.FloorByFace:
            {
                var typeId = Document.FindType<FloorType>(ActiveFloorTypeId)?.Id ?? Document.TypesOf<FloorType>().FirstOrDefault()?.Id;
                if (typeId is not { } chosen) return false;

                // With no mass floors yet, one at every level it rises through - in the same step.
                if (mass.FloorLevels.Count == 0)
                {
                    floorsFirst = new SetMassFloorsCommand(mass, Masses.LevelsWithin(Document, mass));
                    floorsFirst.Redo();
                }

                foreach (var floor in Masses.FloorsByFace(Document, mass, chosen))
                    made.Add((floor, new MassLink(floor.Id, MassLinkKind.Floor, -1, floor.LevelId)));
                if (made.Count == 0) why = "It has no mass floors to make floors from: none of its levels cut it.";
                break;
            }

            case PlanTool.RoofByFace:
            {
                var typeId = Document.FindType<RoofType>(ActiveRoofTypeId)?.Id ?? Document.TypesOf<RoofType>().FirstOrDefault()?.Id;
                if (typeId is not { } chosen) return false;
                if (Masses.RoofByFace(Document, mass, chosen, out why) is { } roof)
                    made.Add((roof, new MassLink(roof.Id, MassLinkKind.Roof, -1, null)));
                break;
            }
        }

        if (made.Count == 0)
        {
            floorsFirst?.Undo();
            HintChanged?.Invoke(this, why ?? $"Nothing to make a {ByFaceNoun(ActiveTool)} from there.");
            return false;
        }

        var noun = ByFaceNoun(ActiveTool);
        var name = $"{char.ToUpperInvariant(noun[0])}{noun[1..]} by Face";
        IUndoableCommand make = new MakeByFaceCommand(Document, mass, made, name);
        Apply(floorsFirst is null ? make : new CompositeCommand(name, new[] { floorsFirst, make }));
        HintChanged?.Invoke(this, $"{made.Count} {(made.Count == 1 && ActiveTool != PlanTool.FloorByFace ? noun : ActiveTool == PlanTool.FloorByFace ? (made.Count == 1 ? "floor" : "floors") : noun + "s")} made from the mass. " +
                                  "Change the mass, then Update to Face to bring them back to it.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>The walls, floors and roof made from the selected mass's faces brought back to it. Public so it can be driven from tests.</summary>
    public bool UpdateMassToFace()
    {
        if (Document is null || _selection is not [Mass mass]) return false;
        if (Masses.UpdateToFace(Document, mass, out var changed) is not { } command)
        {
            HintChanged?.Invoke(this, "Nothing has been made from this mass's faces to bring back to it.");
            return false;
        }

        Apply(command);
        HintChanged?.Invoke(this, $"{changed} element{(changed == 1 ? "" : "s")} brought back to the mass's faces.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>Mass floors on the selected mass at every level it rises through - or off, if it has them all.</summary>
    public bool ToggleMassFloors()
    {
        if (Document is null || _selection is not [Mass mass]) return false;
        var every = Masses.LevelsWithin(Document, mass);
        var all = every.Count > 0 && every.All(mass.FloorLevels.Contains);
        Apply(new SetMassFloorsCommand(mass, all ? Array.Empty<Guid>() : every));
        HintChanged?.Invoke(this, all ? "Its mass floors taken off." : $"Mass floors at {every.Count} level{(every.Count == 1 ? "" : "s")}: {Masses.GrossFloorArea(Document, mass) / 1e6:0.0} m² gross floor area.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }
}

