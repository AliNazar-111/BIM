using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// One panel of a curtain wall, as something that can be clicked and given properties of its
/// own - what it is made of, which glass, which door - while the wall keeps properties of its
/// own for all of them.
///
/// A panel is not stored: the wall stores its grid and the few cells that differ from the rest,
/// and a panel is worked out from them whenever one is asked for. So a panel cannot go stale,
/// and moving a grid line does not leave orphaned panels behind. Its id is worked out from the
/// wall's id and the cell's place in the grid, which is what lets a click on a pane in the 3D
/// view find its way back to the panel it belongs to.
/// </summary>
public sealed class CurtainPanel : Element
{
    private CurtainPanel(Wall wall, int column, int row)
    {
        Id = IdOf(wall.Id, column, row);
        HostWallId = wall.Id;
        LevelId = wall.LevelId;
        Column = column;
        Row = row;
    }

    public override BuiltInCategory Category => BuiltInCategory.CurtainPanels;

    public Guid HostWallId { get; }

    public int Column { get; }

    public int Row { get; }

    /// <summary>
    /// The id a panel of this wall has. Worked out from the wall and the cell rather than made
    /// up, so the same panel is the same panel on every rebuild of the 3D view.
    /// </summary>
    public static Guid IdOf(Guid wallId, int column, int row)
    {
        var bytes = wallId.ToByteArray();
        var original = BitConverter.ToUInt32(bytes, 12);

        // Folded into the last four bytes, which are the ones a wall id is least likely to
        // vary in a way that would collide with a neighbouring cell. Counted from one, and
        // guarded: a panel that came out with its wall's own id would be picked up as the
        // wall itself, and clicking it would select the whole wall.
        var mixed = original ^ (uint)((column + 1) * 73856093) ^ (uint)((row + 1) * 19349663);
        if (mixed == original) mixed ^= 0x9E3779B9;

        BitConverter.GetBytes(mixed).CopyTo(bytes, 12);
        return new Guid(bytes);
    }

    /// <summary>The panel at a cell of a curtain wall, or null if the wall has no such cell.</summary>
    public static CurtainPanel? At(BimDocument document, Wall wall, int column, int row) =>
        CurtainLayout.Of(document, wall) is { } layout && layout.Cells.Any(c => c.Column == column && c.Row == row)
            ? new CurtainPanel(wall, column, row)
            : null;

    /// <summary>The panel an id stands for, searching the curtain walls for the one it belongs to.</summary>
    public static CurtainPanel? Find(BimDocument document, Guid id)
    {
        foreach (var wall in document.Walls.Where(document.IsCurtainWall))
        {
            if (CurtainLayout.Of(document, wall) is not { } layout) continue;

            foreach (var cell in layout.Cells)
                if (IdOf(wall.Id, cell.Column, cell.Row) == id)
                    return new CurtainPanel(wall, cell.Column, cell.Row);
        }

        return null;
    }

    /// <summary>The cell this panel stands for, as the wall's grid currently sets it out.</summary>
    public CurtainCell? Cell(BimDocument document) =>
        document.Walls.FirstOrDefault(w => w.Id == HostWallId) is { } wall && CurtainLayout.Of(document, wall) is { } layout
            ? layout.Cells.FirstOrDefault(c => c.Column == Column && c.Row == Row)
            : null;

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var wall = document.Walls.FirstOrDefault(w => w.Id == HostWallId);

        // Read afresh every time rather than held: a parameter that answered from a snapshot
        // taken when the palette was built would report the old value after its own edit, and
        // nothing downstream - the views, the undo stack - would hear that anything changed.
        CurtainCell? cell() => Cell(document);

        yield return ParameterValue.ReadOnly(CurtainPanelParameters.Panel, () => $"Column {Column + 1}, row {Row + 1}");

        yield return ParameterValue.ReadOnly(
            CurtainPanelParameters.HostWall,
            () => wall is null ? "<none>" : $"{wall.Category} : {document.GetWallType(wall)?.Name ?? "?"}");

        yield return ParameterValue.BindChoice(
            CurtainPanelParameters.Kind,
            () => EnumText.Humanise(cell()?.Kind ?? CurtainPanelKind.Glazed),
            v => { if (EnumText.TryParse<CurtainPanelKind>(v, out var kind)) Set(document, panel => panel with { Kind = kind }); },
            EnumText.Choices<CurtainPanelKind>());

        // What it is glazed with, which is the panel's own until it is set back to the wall's.
        yield return ParameterValue.BindChoice(
            CurtainPanelParameters.Glass,
            () => EnumText.Humanise(cell()?.Glass ?? CurtainGlass.Clear),
            v => { if (EnumText.TryParse<CurtainGlass>(v, out var glass)) Set(document, panel => panel with { Glass = glass }); },
            EnumText.Choices<CurtainGlass>());

        // Which door fills it. Only a curtain wall door can be a panel, as in Revit; a window
        // is cut into the wall with the window tool instead, and is its own element.
        var doors = CurtainDoors.TypesFor(document);
        yield return ParameterValue.BindChoice(
            CurtainPanelParameters.DoorType,
            () => doors.FirstOrDefault(d => d.Id == cell()?.OpeningTypeId)?.Name ?? "None",
            v =>
            {
                var chosen = doors.FirstOrDefault(d => d.Name == v);
                Set(document, panel => panel with
                {
                    OpeningTypeId = chosen?.Id,
                    Kind = chosen is null ? CurtainPanelKind.Glazed : CurtainPanelKind.Door
                });
            },
            new[] { "None" }.Concat(doors.Select(d => d.Name)).ToArray());

        // Whether a door panel is drawn standing open, as an ordinary door is. A panel that is
        // not a door has nothing to swing, so it stays shut and cannot be set.
        yield return cell()?.Kind == CurtainPanelKind.Door
            ? ParameterValue.Bind(
                CurtainPanelParameters.IsOpen,
                () => cell()?.IsOpen ?? false,
                v => Set(document, panel => panel with { IsOpen = v }))
            : ParameterValue.ReadOnly(CurtainPanelParameters.IsOpen, () => false);

        // Which way a door panel is hung and which way it opens: the mirror of the door, the
        // same two flips an ordinary door has.
        yield return ParameterValue.Bind(
            CurtainPanelParameters.FlipHand,
            () => cell()?.FlipHand ?? false,
            v => Set(document, panel => panel with { FlipHand = v }));

        yield return ParameterValue.Bind(
            CurtainPanelParameters.FlipFacing,
            () => cell()?.FlipFacing ?? false,
            v => Set(document, panel => panel with { FlipFacing = v }));

        // Where the panel is: along the wall to its middle, and the height of its underside.
        // Setting the distance slides the whole bay, lines and all, along the wall.
        yield return ParameterValue.BindValidated(
            CurtainPanelParameters.DistanceAlongWall,
            () => cell() is not { } c ? 0 : (c.ClearFrom + c.ClearTo) / 2,
            v => Slide(document, v));

        yield return ParameterValue.ReadOnly(
            CurtainPanelParameters.HeightAboveFloor,
            () => cell() is not { } c ? 0 : c.ClearBottom);

        // Sizing a panel moves the grid lines round it, because that is what its size is: the
        // bays beside it give up what it takes.
        yield return ParameterValue.BindValidated(
            CurtainPanelParameters.Width,
            () => cell() is not { } c ? 0 : c.ClearTo - c.ClearFrom,
            v => Resize(document, across: true, v));

        yield return ParameterValue.BindValidated(
            CurtainPanelParameters.Height,
            () => cell() is not { } c ? 0 : c.ClearTop - c.ClearBottom,
            v => Resize(document, across: false, v));
        yield return ParameterValue.ReadOnly(
            CurtainPanelParameters.Area,
            () => cell() is not { } c ? 0 : Math.Max(0, c.ClearTo - c.ClearFrom) * Math.Max(0, c.ClearTop - c.ClearBottom));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

    /// <summary>
    /// Makes this panel a given width or height by moving the two grid lines round it, which
    /// is what a panel's size is: the bays beside it give up what it takes, and an edge of the
    /// wall cannot move, so a panel against one grows inward only. Refused when it would leave
    /// a neighbour too narrow to build.
    /// </summary>
    private bool Resize(BimDocument document, bool across, double clear)
    {
        if (clear <= 0) return false;
        if (document.Walls.FirstOrDefault(w => w.Id == HostWallId) is not { } wall) return false;
        if (CurtainLayout.Of(document, wall) is not { } layout || Cell(document) is not { } cell) return false;

        var verticals = layout.Verticals.ToList();
        var horizontals = layout.Horizontals.ToList();
        var lines = across ? verticals : horizontals;
        var index = across ? Column : Row;
        if (index < 0 || index + 1 >= lines.Count) return false;

        var extent = across ? layout.Length : layout.Height;
        var change = clear - (across ? cell.ClearTo - cell.ClearFrom : cell.ClearTop - cell.ClearBottom);
        if (Math.Abs(change) < CurtainDoors.Tolerance / 10) return true;

        // The ends of the wall are not grid lines that can be moved.
        var lowFixed = index == 0;
        var highFixed = index + 1 == lines.Count - 1;
        if (lowFixed && highFixed) return false;

        var low = lines[index] - (highFixed ? change : lowFixed ? 0 : change / 2);
        var high = lines[index + 1] + (lowFixed ? change : highFixed ? 0 : change / 2);

        // Whatever is next door keeps enough to be a panel, and so does this one.
        var floor = index == 0 ? 0 : lines[index - 1] + CurtainDoors.MinimumPanel;
        var ceiling = index + 2 >= lines.Count ? extent : lines[index + 2] - CurtainDoors.MinimumPanel;
        if (low < floor - 1e-6 || high > ceiling + 1e-6 || high - low < CurtainDoors.MinimumPanel) return false;

        lines[index] = low;
        lines[index + 1] = high;

        wall.CurtainGrid = new CurtainGrid(Inner(verticals, layout.Length), Inner(horizontals, layout.Height));
        return true;
    }

    /// <summary>
    /// Slides the panel along the wall to put its middle here, taking both the grid lines round
    /// it with it. Refused when it would push a neighbour out of existence, or when the panel is
    /// against an end of the wall, which is not a line that can move.
    /// </summary>
    private bool Slide(BimDocument document, double middle)
    {
        if (document.Walls.FirstOrDefault(w => w.Id == HostWallId) is not { } wall) return false;
        if (CurtainLayout.Of(document, wall) is not { } layout || Cell(document) is not { } cell) return false;

        var verticals = layout.Verticals.ToList();
        var horizontals = layout.Horizontals.ToList();
        if (Column < 0 || Column + 1 >= verticals.Count) return false;
        if (Column == 0 || Column + 1 == verticals.Count - 1) return false;

        var change = middle - (cell.ClearFrom + cell.ClearTo) / 2;
        if (Math.Abs(change) < CurtainDoors.Tolerance / 10) return true;

        var low = verticals[Column] + change;
        var high = verticals[Column + 1] + change;

        var floor = Column == 0 ? 0 : verticals[Column - 1] + CurtainDoors.MinimumPanel;
        var ceiling = Column + 2 >= verticals.Count ? layout.Length : verticals[Column + 2] - CurtainDoors.MinimumPanel;
        if (low < floor - 1e-6 || high > ceiling + 1e-6) return false;

        verticals[Column] = low;
        verticals[Column + 1] = high;

        wall.CurtainGrid = new CurtainGrid(Inner(verticals, layout.Length), Inner(horizontals, layout.Height));
        return true;
    }

    /// <summary>The wall's own grid lines: the inner ones, without the two ends of the wall.</summary>
    private static List<double> Inner(IEnumerable<double> lines, double extent) =>
        lines.Where(x => x > CurtainDoors.Tolerance && x < extent - CurtainDoors.Tolerance).ToList();

    /// <summary>
    /// Changes this panel on its wall: the cell's entry among the wall's panels, added if the
    /// cell had none, and dropped again when it goes back to being plain glass like the rest.
    /// </summary>
    private void Set(BimDocument document, Func<CurtainPanelOverride, CurtainPanelOverride> change)
    {
        if (document.Walls.FirstOrDefault(w => w.Id == HostWallId) is not { } wall) return;

        var panels = (wall.CurtainPanels ?? Array.Empty<CurtainPanelOverride>()).ToList();
        var index = panels.FindIndex(p => p.Column == Column && p.Row == Row);

        var current = index >= 0
            ? panels[index]
            : new CurtainPanelOverride(Column, Row, CurtainPanelKind.Glazed, null, wall.CurtainGlass);

        var updated = change(current);

        // A door has to stand on the floor, as it does everywhere else. A window does not.
        if (updated.Kind == CurtainPanelKind.Door && Row != 0) return;

        if (index >= 0) panels[index] = updated;
        else panels.Add(updated);

        // Nothing to remember about a panel that is simply the wall's own glass.
        if (updated.Kind == CurtainPanelKind.Glazed && updated.Glass == wall.CurtainGlass)
            panels.RemoveAll(p => p.Column == Column && p.Row == Row);

        wall.CurtainPanels = panels.Count == 0 ? null : panels.OrderBy(p => p.Column).ThenBy(p => p.Row).ToList();
    }
}

public static class CurtainPanelParameters
{
    public static readonly ParameterDefinition Panel =
        new("Grid Position", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HostWall =
        new("Host", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Kind =
        new("Panel", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition Glass =
        new("Glass", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.MaterialsAndFinishes);

    public static readonly ParameterDefinition DoorType =
        new("Door Type", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition IsOpen =
        new("Open", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FlipHand =
        new("Flip Hand", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition DistanceAlongWall =
        new("Distance Along Wall", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition HeightAboveFloor =
        new("Height Above Floor", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
