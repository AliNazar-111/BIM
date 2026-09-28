using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Documents;

/// <summary>
/// Duplicating elements (specification section 2.4).
///
/// Copying is not one operation but three, and getting any of them wrong produces a model
/// that looks right and is not:
///
/// <list type="bullet">
/// <item>the copy must be a new element, with its own id, or the two would be the same thing
/// twice and every schedule would count one of them;</item>
/// <item>a copied wall has to bring its doors and windows, rehosted onto the copy - a door
/// left pointing at the original would appear in the wrong wall;</item>
/// <item>references <em>within</em> the copied set have to be redirected to the copies, while
/// references out of it are left pointing where they were. A tag copied alongside its wall
/// should label the new wall; a tag copied on its own still labels the old one.</item>
/// </list>
/// </summary>
public static class ElementCopy
{
    /// <summary>
    /// Clones a set of elements, including anything hosted by them, with every reference
    /// inside the set redirected to the copies. Nothing is added to the document: the caller
    /// decides where the copies go and puts them in through a command.
    /// </summary>
    public static List<Element> Duplicate(BimDocument document, IEnumerable<Element> elements)
    {
        var originals = new List<Element>();
        var seen = new HashSet<Guid>();

        foreach (var element in elements)
        {
            if (element is Sheet) continue;             // a sheet is not in the building
            if (!seen.Add(element.Id)) continue;

            originals.Add(element);
        }

        // Anything hosted in something being copied comes with it, whether or not it was
        // picked: a wall without its doors is not a copy of that wall.
        foreach (var hosted in document.Elements.OfType<IHostedElement>())
        {
            if (!seen.Contains(hosted.HostId)) continue;

            var element = (Element)hosted;
            if (seen.Add(element.Id)) originals.Add(element);
        }

        var copies = new List<Element>();
        var replacements = new Dictionary<Guid, Guid>();

        foreach (var original in originals)
        {
            if (Clone(original) is not { } copy) continue;

            replacements[original.Id] = copy.Id;
            copies.Add(copy);
        }

        foreach (var copy in copies) Redirect(copy, replacements);

        return copies;
    }

    /// <summary>
    /// A copy of one element, with a new id and everything else carried over.
    ///
    /// Written out per type rather than reflected or serialised, so that adding a property to
    /// an element makes someone decide whether a copy should carry it. A clone that silently
    /// drops a field is the kind of bug that is only found months later.
    /// </summary>
    public static Element? Clone(Element element) => element switch
    {
        Wall wall => CarryJoins(wall, CarryCommon(new Wall
        {
            Start = wall.Start,
            End = wall.End,
            LocationLine = wall.LocationLine,
            Flipped = wall.Flipped,
            BaseOffset = wall.BaseOffset,
            TopLevelId = wall.TopLevelId,
            TopOffset = wall.TopOffset,
            UnconnectedHeight = wall.UnconnectedHeight,
            RoomBounding = wall.RoomBounding,
            StructuralUsage = wall.StructuralUsage,
            Bulge = wall.Bulge,
            Ellipse = wall.Ellipse,
            Spline = wall.Spline,
            Profile = wall.Profile?.ToList(),
            ProfileLength = wall.ProfileLength,
            CurtainGrid = wall.CurtainGrid,
            CurtainPanels = wall.CurtainPanels,
            CrossSection = wall.CrossSection,
            SlantAngle = wall.SlantAngle,
            LockedToJoined = wall.LockedToJoined,
            StartLocked = wall.StartLocked,
            EndLocked = wall.EndLocked,
            StartCleanup = wall.StartCleanup,
            EndCleanup = wall.EndCleanup,
            UpperSlantAngle = wall.UpperSlantAngle,
            SlantBreakHeight = wall.SlantBreakHeight,
            OverrideTaper = wall.OverrideTaper,
            ExteriorTaper = wall.ExteriorTaper,
            InteriorTaper = wall.InteriorTaper,
            StartJoin = wall.StartJoin,
            EndJoin = wall.EndJoin
        }, wall)),

        Door door => CarryCommon(new Door
        {
            HostWallId = door.HostWallId,
            DistanceAlongWall = door.DistanceAlongWall,
            SillHeight = door.SillHeight,
            FlipFacing = door.FlipFacing,
            FlipHand = door.FlipHand,
            SwingAngle = door.SwingAngle,
            FrameType = door.FrameType,
            Finish = door.Finish
        }, door),

        Window window => CarryCommon(new Window
        {
            HostWallId = window.HostWallId,
            DistanceAlongWall = window.DistanceAlongWall,
            SillHeight = window.SillHeight,
            FlipFacing = window.FlipFacing,
            FlipHand = window.FlipHand
        }, window),

        Floor floor => CloneSlab(new Floor(), floor),
        Ceiling ceiling => CloneSlab(new Ceiling(), ceiling),
        Roof roof => CloneSlab(new Roof(), roof),

        Column column => CarryCommon(new Column
        {
            Location = column.Location,
            Rotation = column.Rotation,
            BaseOffset = column.BaseOffset,
            TopLevelId = column.TopLevelId,
            TopOffset = column.TopOffset,
            UnconnectedHeight = column.UnconnectedHeight,
            TopAttachedTo = column.TopAttachedTo,
            BaseAttachedTo = column.BaseAttachedTo,
            TopAttachmentStyle = column.TopAttachmentStyle,
            BaseAttachmentStyle = column.BaseAttachmentStyle,
            Placement = column.Placement,
            PlacementFace = column.PlacementFace,
            OffsetFromAttachmentAtTop = column.OffsetFromAttachmentAtTop,
            OffsetFromAttachmentAtBase = column.OffsetFromAttachmentAtBase,
            RoomBounding = column.RoomBounding,
            CutByWalls = column.CutByWalls,
            MovesWithGrids = column.MovesWithGrids
        }, column),

        Component component => CarryCommon(new Component
        {
            TypeKind = component.TypeKind,
            Location = component.Location,
            Rotation = component.Rotation,
            Elevation = component.Elevation,
            HostId = component.HostId,
            FlipFacing = component.FlipFacing
        }, component),

        Room room => CarryCommon(new Room
        {
            Location = room.Location,
            Name = room.Name,
            Number = room.Number,
            Department = room.Department,
            Occupancy = room.Occupancy,
            OccupantCount = room.OccupantCount,
            BaseOffset = room.BaseOffset,
            UpperLimitOffset = room.UpperLimitOffset,
            FloorFinish = room.FloorFinish,
            WallFinish = room.WallFinish,
            CeilingFinish = room.CeilingFinish,
            BaseFinish = room.BaseFinish
        }, room),

        Grid grid => CarryCommon(new Grid
        {
            Start = grid.Start,
            End = grid.End,
            Name = grid.Name,
            BubbleAtStart = grid.BubbleAtStart,
            BubbleAtEnd = grid.BubbleAtEnd
        }, grid),

        SectionMarker section => CarryCommon(new SectionMarker
        {
            Start = section.Start,
            End = section.End,
            Name = section.Name,
            Flipped = section.Flipped,
            ViewDepth = section.ViewDepth
        }, section),

        Dimension dimension => CarryCommon(new Dimension
        {
            Start = CloneReference(dimension.Start),
            End = CloneReference(dimension.End),
            Offset = dimension.Offset,
            Override = dimension.Override
        }, dimension),

        Tag tag => CarryCommon(new Tag
        {
            TargetId = tag.TargetId,
            Field = tag.Field,
            Position = tag.Position,
            ShowLeader = tag.ShowLeader
        }, tag),

        TextNote note => CarryCommon(new TextNote
        {
            Text = note.Text,
            Position = note.Position,
            LeaderEnd = note.LeaderEnd
        }, note),

        PlacedSweep placed => CarryPlacedSweep(placed),

        WallOpening opening => CarryCommon(new WallOpening
        {
            HostWallId = opening.HostWallId,
            DistanceAlongWall = opening.DistanceAlongWall,
            Width = opening.Width,
            Height = opening.Height,
            SillHeight = opening.SillHeight
        }, opening),

        _ => null
    };

    /// <summary>The walls a copy is joined to face to face: the same ones, until <see cref="Redirect"/> points them at copies.</summary>
    private static Element CarryJoins(Wall original, Element copy)
    {
        ((Wall)copy).JoinedTo.AddRange(original.JoinedTo);
        return copy;
    }

    private static Slab CloneSlab(Slab copy, Slab original)
    {
        copy.HeightOffset = original.HeightOffset;
        copy.SetBoundary(original.Boundary);

        // A copied roof is the same roof: the edges that slope carry over, or the copy would
        // come out flat.
        if (copy is Roof copied && original is Roof roof)
        {
            copied.CutoffOffset = roof.CutoffOffset;
            copied.CutoffLevelId = roof.CutoffLevelId;
            copied.RafterCut = roof.RafterCut;
            copied.Bearing = roof.Bearing;
            copied.JoinedTo = roof.JoinedTo;
            copied.Dormer = roof.Dormer;
            copied.DormerWalls.AddRange(roof.DormerWalls);
            copied.SetOpenings(roof.Openings);
            copied.FasciaDepth = roof.FasciaDepth;
            copied.SetSlopeArrows(roof.SlopeArrows.Select(arrow => arrow.Copy()));
            copied.SetExtrusion(roof.Extrusion);
            copied.SetEdges(roof.Edges.Select(edge => edge.Copy()));
        }

        return (Slab)CarryCommon(copy, original);
    }

    /// <summary>
    /// The bookkeeping every element carries. The mark is deliberately <em>not</em> copied:
    /// a mark identifies one element, and two doors both marked D-04 is a defect that reaches
    /// the site through the door schedule.
    /// </summary>
    private static Element CarryPlacedSweep(PlacedSweep placed)
    {
        var copy = new PlacedSweep
        {
            Kind = placed.Kind,
            Side = placed.Side,
            Vertical = placed.Vertical,
            Elevation = placed.Elevation,
            Along = placed.Along,
            Offset = placed.Offset,
            Flip = placed.Flip,
            ReturnAtStart = placed.ReturnAtStart,
            ReturnAtEnd = placed.ReturnAtEnd,
            Mark = placed.Mark
        };
        copy.HostWallIds.AddRange(placed.HostWallIds);
        return CarryCommon(copy, placed);
    }

    private static Element CarryCommon(Element copy, Element original)
    {
        copy.TypeId = original.TypeId;
        copy.LevelId = original.LevelId;
        copy.Comments = original.Comments;
        copy.Workset = original.Workset;
        copy.PhaseCreated = original.PhaseCreated;
        copy.PhaseDemolished = original.PhaseDemolished;

        return copy;
    }

    private static DimensionReference CloneReference(DimensionReference reference) => new()
    {
        ElementId = reference.ElementId,
        Anchor = reference.Anchor,
        FallbackPoint = reference.FallbackPoint
    };

    /// <summary>
    /// Points a copy's references at the other copies, where those were part of the same
    /// operation. Anything referring outside the set keeps pointing where it did.
    /// </summary>
    private static void Redirect(Element copy, IReadOnlyDictionary<Guid, Guid> replacements)
    {
        // Walls copied with the roof they stand on or carry go with the copy of it.
        if (copy is Wall attached)
        {
            if (attached.TopAttachedTo is { } top && replacements.TryGetValue(top, out var newTop)) attached.TopAttachedTo = newTop;
            if (attached.BaseAttachedTo is { } foot && replacements.TryGetValue(foot, out var newFoot)) attached.BaseAttachedTo = newFoot;
        }

        // A dormer copied with its walls is a dormer of the copies; copied without them, a roof.
        if (copy is Roof { Dormer: not null } dormer)
        {
            for (var i = dormer.DormerWalls.Count - 1; i >= 0; i--)
            {
                if (replacements.TryGetValue(dormer.DormerWalls[i], out var wall)) dormer.DormerWalls[i] = wall;
                else dormer.DormerWalls.RemoveAt(i);
            }

            if (dormer.DormerWalls.Count == 0) dormer.Dormer = null;
        }

        switch (copy)
        {
            case Opening opening when replacements.TryGetValue(opening.HostWallId, out var host):
                opening.HostWallId = host;
                break;

            case WallOpening cut when replacements.TryGetValue(cut.HostWallId, out var cutHost):
                cut.HostWallId = cutHost;
                break;

            case Component component when replacements.TryGetValue(component.HostId, out var componentHost):
                component.HostId = componentHost;
                break;

            case Wall joined when joined.JoinedTo.Count > 0:
                // A lining copied without the wall it lines is no longer against it: the join goes.
                for (var i = joined.JoinedTo.Count - 1; i >= 0; i--)
                {
                    if (replacements.TryGetValue(joined.JoinedTo[i], out var partner)) joined.JoinedTo[i] = partner;
                    else joined.JoinedTo.RemoveAt(i);
                }

                break;

            case PlacedSweep placed:
                for (var i = 0; i < placed.HostWallIds.Count; i++)
                    if (replacements.TryGetValue(placed.HostWallIds[i], out var wall)) placed.HostWallIds[i] = wall;
                break;

            case Tag tag when replacements.TryGetValue(tag.TargetId, out var target):
                tag.TargetId = target;
                break;

            case Dimension dimension:
                if (replacements.TryGetValue(dimension.Start.ElementId, out var start))
                    dimension.Start.ElementId = start;

                if (replacements.TryGetValue(dimension.End.ElementId, out var end))
                    dimension.End.ElementId = end;

                break;
        }
    }
}
