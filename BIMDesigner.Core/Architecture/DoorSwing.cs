using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// Whether a door has room to open to the side it faces: over the ground its leaves sweep,
/// nothing overhead comes lower than the door - the underside of a roof's overhang, a soffit
/// closing it. A tall door opening out under the eaves would swing up into them; opening in,
/// under a roof that only rises into the building, it clears them. A door is let open only to a
/// side it clears, and is hung to open to that side when it would not clear the other.
/// </summary>
public static class DoorSwing
{
    /// <summary>How far something overhead may come down into a door's swing before it is in the way, mm.</summary>
    private const double Tolerance = 5;

    /// <summary>
    /// What a curtain wall's door panel would hit opening with this facing - "the soffit", "the
    /// roof" - or null where it swings clear, or it does not swing.
    /// </summary>
    public static string? Hits(BimDocument document, Wall wall, CurtainCell cell, bool flipFacing)
    {
        if (cell.Kind != CurtainPanelKind.Door) return null;

        var door = cell.OpeningTypeId is { } typeId ? document.FindType<DoorType>(typeId) : null;
        if (door is not null && door.Operation is not (DoorOperation.Swing or DoorOperation.DoubleSwing)) return null;

        var width = cell.ClearTo - cell.ClearFrom;
        var bottom = wall.GetBaseElevation(document);
        return Hits(document, wall, cell.ClearFrom, cell.ClearTo, bottom + cell.ClearBottom, bottom + cell.ClearTop,
            door?.Operation == DoorOperation.DoubleSwing ? width / 2 : width, toExterior: !flipFacing);
    }

    /// <summary>What a door in a wall would hit opening with this facing, or null where it swings clear or does not swing.</summary>
    public static string? Hits(BimDocument document, Opening opening, bool flipFacing)
    {
        if (opening is not Door || document.FindType<DoorType>(opening.TypeId) is not
                { Operation: DoorOperation.Swing or DoorOperation.DoubleSwing } door)
            return null;
        if (document.Walls.FirstOrDefault(wall => wall.Id == opening.HostWallId) is not { } host) return null;

        var (from, to) = opening.GetSpan(door);
        var (sill, height) = opening.Placed(document, door, host);
        var bottom = host.GetBaseElevation(document);
        return Hits(document, host, from, to, bottom + sill, bottom + sill + height,
            door.Operation == DoorOperation.DoubleSwing ? (to - from) / 2 : to - from, toExterior: !flipFacing);
    }

    /// <summary>
    /// What comes lower than a door's head over the ground its leaves sweep - out from the face
    /// of the wall on one side as far as a leaf is wide, all along the doorway - or null where
    /// nothing does. Only what is over the doorway counts: a roof below the door's sill is not
    /// in its way.
    /// </summary>
    public static string? Hits(
        BimDocument document, Wall wall, double from, double to, double sill, double head, double reach, bool toExterior)
    {
        if (document.GetWallType(wall) is not { } type || reach <= 1 || to - from <= 1) return null;

        var structure = type.Structure;
        var face = structure.TotalWidth / 2;
        var side = toExterior ? 1.0 : -1.0;

        var soffits = document.Elements.OfType<Soffit>().SelectMany(soffit => RoofEdgeSweeps.SoffitPieces(document, soffit)).ToList();
        var roofs = document.Elements.OfType<Roof>().Select(roof => (Roof: roof, Surface: roof.Surface(document))).ToList();

        bool InTheWay(double underside) => underside > sill && head > underside + Tolerance;

        const int steps = 8;
        for (var i = 0; i <= steps; i++)
        for (var j = 1; j <= steps; j++)
        {
            var point = wall.PointAt(structure, from + (to - from) * i / steps, side * (face + reach * j / steps));

            if (soffits.Any(piece => InTheWay(piece.Bottom) && Polygon2D.Contains(piece.Outline, point))) return "the soffit";
            if (roofs.Any(entry => entry.Roof.Contains(point) && InTheWay(entry.Surface.HeightAt(point)))) return "the roof";
        }

        return null;
    }

    /// <summary>
    /// The facing a door is to have: as asked, unless it would hit something opening that way
    /// and the other way is clear - then the other. Says what it would have hit, when it turned.
    /// </summary>
    public static bool Settle(Func<bool, string?> hits, bool flipFacing, out string? turnedFrom)
    {
        turnedFrom = hits(flipFacing);
        if (turnedFrom is null || hits(!flipFacing) is not null)
        {
            turnedFrom = null;
            return flipFacing;
        }

        return !flipFacing;
    }

    /// <summary>
    /// A curtain wall's panels with every door among them hung to open to a side it clears, as
    /// laid out on the wall with this grid. The panels as they were where every door clears.
    /// </summary>
    public static IReadOnlyList<CurtainPanelOverride>? Settled(
        BimDocument document, Wall wall, CurtainGrid? grid, IReadOnlyList<CurtainPanelOverride>? panels)
    {
        if (panels is null || panels.All(panel => panel.Kind != CurtainPanelKind.Door)) return panels;
        if (CurtainLayout.Of(document, wall) is not { } current) return panels;

        var layout = CurtainLayout.Build(current.Type, current.Length, current.Height, grid, panels, wall.CurtainGlass, current.TopLine);
        return panels
            .Select(panel =>
            {
                if (panel.Kind != CurtainPanelKind.Door ||
                    layout.Cells.FirstOrDefault(cell => cell.Column == panel.Column && cell.Row == panel.Row) is not { } cell)
                    return panel;

                var facing = Settle(flip => Hits(document, wall, cell, flip), panel.FlipFacing, out _);
                return facing == panel.FlipFacing ? panel : panel with { FlipFacing = facing };
            })
            .ToList();
    }
}
