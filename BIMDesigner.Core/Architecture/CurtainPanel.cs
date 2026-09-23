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

        // Which door or window fills it. Only a curtain wall door can be a panel, as in Revit;
        // any window can, because a window in a curtain wall is just a panel that opens.
        var openings = CurtainDoors.TypesFor(document)
            .Cast<OpeningType>()
            .Concat(document.TypesOf<WindowType>().OrderBy(type => type.Width))
            .ToList();

        yield return ParameterValue.BindChoice(
            CurtainPanelParameters.DoorType,
            () => openings.FirstOrDefault(o => o.Id == cell()?.OpeningTypeId)?.Name ?? "None",
            v =>
            {
                var chosen = openings.FirstOrDefault(o => o.Name == v);
                Set(document, panel => panel with
                {
                    OpeningTypeId = chosen?.Id,
                    Kind = chosen switch
                    {
                        WindowType => CurtainPanelKind.Window,
                        null => CurtainPanelKind.Glazed,
                        _ => CurtainPanelKind.Door
                    }
                });
            },
            new[] { "None" }.Concat(openings.Select(o => o.Name)).ToArray());

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

        yield return ParameterValue.ReadOnly(CurtainPanelParameters.Width, () => cell() is not { } c ? 0 : c.ClearTo - c.ClearFrom);
        yield return ParameterValue.ReadOnly(CurtainPanelParameters.Height, () => cell() is not { } c ? 0 : c.ClearTop - c.ClearBottom);
        yield return ParameterValue.ReadOnly(
            CurtainPanelParameters.Area,
            () => cell() is not { } c ? 0 : Math.Max(0, c.ClearTo - c.ClearFrom) * Math.Max(0, c.ClearTop - c.ClearBottom));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }

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
        new("Door or Window", ParameterDataType.Text, ParameterBinding.Instance, ParameterGroup.Construction);

    public static readonly ParameterDefinition FlipHand =
        new("Flip Hand", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition FlipFacing =
        new("Flip Facing", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Constraints);

    public static readonly ParameterDefinition Width =
        new("Width", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Height =
        new("Height", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);

    public static readonly ParameterDefinition Area =
        new("Area", ParameterDataType.Area, ParameterBinding.Instance, ParameterGroup.Dimensions);
}
