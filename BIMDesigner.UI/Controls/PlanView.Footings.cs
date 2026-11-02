using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.UI.Controls;

/// <summary>
/// Structural columns and footings on the plan: the Column tool on the Structure tab, which places
/// from the structural column types; the Wall foundation tool, a strip footing under each wall
/// clicked; the Isolated foundation tool, a pad under each column clicked, or on its own where
/// there is none - each sized and taken as deep as the rules ask, unless a type is picked.
/// </summary>
public partial class PlanView
{
    /// <summary>The type the structural Column tool places from.</summary>
    public Guid ActiveStructuralColumnTypeId { get; set; }

    /// <summary>The strip footing type the Wall foundation tool puts under a wall; none, for the narrowest the rules are met by.</summary>
    public Guid ActiveWallFootingTypeId { get; set; }

    /// <summary>The pad type the Isolated foundation tool puts under a column; none, for the smallest the rules are met by.</summary>
    public Guid ActivePadFootingTypeId { get; set; }

    /// <summary>A click with the Wall foundation tool: a strip footing under the wall clicked. Public so it can be driven from tests.</summary>
    public bool PlaceWallFootingAt(Point2D raw)
    {
        if (Document is null) return false;
        if (HitTestWall(raw) is not { } wall)
        {
            HintChanged?.Invoke(this, "Click a wall: a strip footing goes under it, the masonry on it up to the wall.");
            return false;
        }

        if (wall.IsCurved)
        {
            HintChanged?.Invoke(this, "A strip footing goes under a straight wall; a curved one is not footed here yet.");
            return false;
        }

        if (Footings.Under(Document, wall) is not null)
        {
            HintChanged?.Invoke(this, "That wall has a footing under it already.");
            return false;
        }

        var footing = Footings.Fit(Document, wall, Document.FindType<FootingType>(ActiveWallFootingTypeId));
        Apply(new AddElementCommand(Document, footing, "Wall Foundation"));
        Select(footing);
        HintChanged?.Invoke(this, $"{Document.FindType<FootingType>(footing.TypeId)?.Name ?? "A footing"} under the wall, {Core.Units.FormatMm(Footings.DepthBelowGround(Document, footing) ?? footing.Depth)} below the ground. {StructureRules.Verdict(Document, footing).TrimEnd('.')}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A click with the Isolated foundation tool: a pad under the structural column clicked, or on its own where there is none.</summary>
    public bool PlacePadFootingAt(Point2D raw)
    {
        if (Document is null) return false;
        var chosen = Document.FindType<FootingType>(ActivePadFootingTypeId);

        PadFooting pad;
        if (ColumnAt(raw) is { Structural: true } column)
        {
            if (Footings.Under(Document, column) is not null)
            {
                HintChanged?.Invoke(this, "That column has a pad under it already.");
                return false;
            }

            pad = Footings.Fit(Document, column, chosen);
        }
        else
        {
            var type = chosen ?? Document.TypesOf<FootingType>().FirstOrDefault(candidate => candidate.Kind == FootingKind.Pad);
            if (type is null)
            {
                HintChanged?.Invoke(this, "The project has no pad footing types.");
                return false;
            }

            pad = new PadFooting { TypeId = type.Id, LevelId = ActiveLevelId, Location = SnapPoint(raw, null, out _) };
            pad.Depth = Footings.DepthFor(Document, pad, type);
        }

        Apply(new AddElementCommand(Document, pad, "Isolated Foundation"));
        Select(pad);
        HintChanged?.Invoke(this, $"{Document.FindType<FootingType>(pad.TypeId)?.Name ?? "A pad"}, {Core.Units.FormatMm(Footings.DepthBelowGround(Document, pad) ?? pad.Depth)} below the ground. {StructureRules.Verdict(Document, pad).TrimEnd('.')}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A click with the Raft tool: a raft under the building clicked inside, to the outside of its walls. Public so it can be driven from tests.</summary>
    public bool PlaceRaftAt(Point2D raw)
    {
        if (Document is null) return false;
        if (Document.All<RaftFoundation>().Any(raft => Footings.LevelOf(Document, raft) == ActiveLevelId && Polygon2D.Contains(raft.Outline, raw)))
        {
            HintChanged?.Invoke(this, "There is a raft under that already.");
            return false;
        }

        if (Rafts.Under(Document, ActiveLevelId, raw) is not { } raft)
        {
            HintChanged?.Invoke(this, "Click inside the building: its outside walls are what the raft is drawn round.");
            return false;
        }

        Apply(new AddElementCommand(Document, raft, "Raft Foundation"));
        Select(raft);
        HintChanged?.Invoke(this, $"A raft of {Core.Units.OfSquareMetres(Polygon2D.Area(raft.Outline) / 1e6, "0.0")}, {Core.Units.FormatMm(raft.Thickness)} thick, its edge {Core.Units.FormatMm(Rafts.EdgeBelowGround(Document, raft))} below the ground. {StructureRules.Verdict(Document, raft).TrimEnd('.')}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>A click with the Piles tool: a ground beam on piles under the wall clicked, or a pile on its own where there is none. Public so it can be driven from tests.</summary>
    public bool PlacePilesAt(Point2D raw)
    {
        if (Document is null) return false;
        var (diameter, least) = (Piles.UsualDiameter(Document), Piles.LeastLength(Document));
        if (HitTestWall(raw) is not { } wall)
        {
            var single = Piles.Single(Document, ActiveLevelId, SnapPoint(raw, null, out _), diameter, least);
            Apply(new AddElementCommand(Document, single, "Pile"));
            Select(single);
            HintChanged?.Invoke(this, $"A pile on its own, {Core.Units.FormatMm(single.Diameter)} across and {Core.Units.FormatMm(single.Length)} long. {Piles.CapacitySaid(Document, single).TrimEnd('.')}.");
            ModelChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return true;
        }

        if (wall.IsCurved)
        {
            HintChanged?.Invoke(this, "A ground beam goes under a straight wall; a curved one is not piled here yet.");
            return false;
        }

        if (Piles.Under(Document, wall) is not null || Footings.Under(Document, wall) is not null)
        {
            HintChanged?.Invoke(this, "That wall has a foundation under it already.");
            return false;
        }

        var (beam, piles) = Piles.Fit(Document, wall, diameter, least);
        Apply(new AddElementsCommand(Document, piles.Cast<Element>().Prepend(beam).ToList(), "Ground Beam and Piles"));
        Select(beam);
        HintChanged?.Invoke(this, $"A ground beam under the wall on {piles.Count} new pile{(piles.Count == 1 ? "" : "s")}, the longest {Core.Units.FormatMm(piles.Count == 0 ? 0 : piles.Max(pile => pile.Length))}. {StructureRules.Verdict(Document, beam).TrimEnd('.')}.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Footings under each of the walls and structural columns given that has none - each sized
    /// and taken down as the rules ask - in one step. How many were put in.
    /// </summary>
    public int AddFootings(IEnumerable<Element> targets)
    {
        if (Document is null) return 0;
        var added = new List<Element>();
        foreach (var target in targets.Distinct())
        {
            switch (target)
            {
                case Wall { IsCurved: false } wall when Footings.Under(Document, wall) is null:
                    added.Add(Footings.Fit(Document, wall, Document.FindType<FootingType>(ActiveWallFootingTypeId)));
                    break;
                case Column { Structural: true } column when Footings.Under(Document, column) is null:
                    added.Add(Footings.Fit(Document, column, Document.FindType<FootingType>(ActivePadFootingTypeId)));
                    break;
            }
        }

        if (added.Count == 0) return 0;
        Apply(new AddElementsCommand(Document, added, "Footings"));
        ModelChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return added.Count;
    }

    /// <summary>The footings under a point on this storey: picked last, being under the floor.</summary>
    private IEnumerable<Element> FootingsAt(Point2D model)
    {
        if (Document is null) yield break;
        foreach (var footing in Document.Elements.Where(Footings.IsFoundation))
            if (Footings.LevelOf(Document, footing) == ActiveLevelId && Footings.Footprint(Document, footing) is { Count: >= 3 } outline && Polygon2D.Contains(outline, model))
                yield return footing;
    }
}
