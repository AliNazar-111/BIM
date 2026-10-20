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
        // picked: a wall without its doors is not a copy of that wall. And what is hosted in
        // that - a gutter's downpipes, with the roof the gutter is on.
        for (var added = true; added;)
        {
            added = false;
            foreach (var hosted in document.Elements.OfType<IHostedElement>())
            {
                if (!seen.Contains(hosted.HostId)) continue;

                var element = (Element)hosted;
                if (!seen.Add(element.Id)) continue;
                originals.Add(element);
                added = true;
            }
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
            FaceRegions = wall.FaceRegions.ToList(),
            CurtainGrid = wall.CurtainGrid,
            CurtainPanels = wall.CurtainPanels,
            CurtainMullions = wall.CurtainMullions,
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
            EndJoin = wall.EndJoin,
            AcrossOffset = wall.AcrossOffset,
            CurtainGlass = wall.CurtainGlass,
            TopAttachedTo = wall.TopAttachedTo,
            BaseAttachedTo = wall.BaseAttachedTo
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
            FrameMaterial = door.FrameMaterial,
            Finish = door.Finish,
            Orientation = door.Orientation,
            IsOpen = door.IsOpen,
            WidthOverride = door.WidthOverride,
            HeightOverride = door.HeightOverride
        }, door),

        Window window => CarryCommon(new Window
        {
            HostWallId = window.HostWallId,
            DistanceAlongWall = window.DistanceAlongWall,
            SillHeight = window.SillHeight,
            FlipFacing = window.FlipFacing,
            FlipHand = window.FlipHand,
            Orientation = window.Orientation,
            IsOpen = window.IsOpen,
            WidthOverride = window.WidthOverride,
            HeightOverride = window.HeightOverride
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
            MovesWithGrids = column.MovesWithGrids,
            Structural = column.Structural
        }, column),

        WallFooting strip => CarryCommon(new WallFooting { WallId = strip.WallId, Depth = strip.Depth, Eccentricity = strip.Eccentricity }, strip),

        PadFooting pad => CarryCommon(new PadFooting { ColumnId = pad.ColumnId, Location = pad.Location, Rotation = pad.Rotation, Depth = pad.Depth }, pad),

        Component component => CarryCommon(new Component
        {
            TypeKind = component.TypeKind,
            Location = component.Location,
            Rotation = component.Rotation,
            Elevation = component.Elevation,
            HostId = component.HostId,
            FlipFacing = component.FlipFacing,
            FlipHand = component.FlipHand,
            StandsOn = component.StandsOn
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
            BaseFinish = room.BaseFinish,
            ClassificationCode = room.ClassificationCode
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

        Dimension dimension => CarryCommon(CloneDimension(dimension), dimension),

        AngularDimension angular => CarryCommon(new AngularDimension
        {
            First = CloneReference(angular.First), Second = CloneReference(angular.Second), Through = angular.Through, Override = angular.Override
        }, angular),

        RadialDimension radial => CarryCommon(new RadialDimension
        {
            Kind = radial.Kind, Target = CloneReference(radial.Target), Through = radial.Through, Override = radial.Override
        }, radial),

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

        RevisionCloud cloud => CarryCommon(CloneCloud(cloud), cloud),

        KeynoteTag keynote => CarryCommon(new KeynoteTag
        {
            Source = keynote.Source, TargetId = keynote.TargetId, MaterialId = keynote.MaterialId, Key = keynote.Key,
            Position = keynote.Position, LeaderEnd = keynote.LeaderEnd, Display = keynote.Display
        }, keynote),

        PlacedSweep placed => CarryPlacedSweep(placed),
        Fascia fascia => CarryRoofEdgeSweep(new Fascia(), fascia),
        Gutter gutter => CarryRoofEdgeSweep(new Gutter(), gutter),
        Soffit soffit => CarryRoofEdgeSweep(new Soffit(), soffit),
        Eave eave => CarryRoofEdgeSweep(new Eave { FireStopped = eave.FireStopped, FireBlocked = eave.FireBlocked }, eave),
        RoofWindow roofWindow => CarryCommon(new RoofWindow { RoofId = roofWindow.RoofId, Location = roofWindow.Location, Own = roofWindow.Own }, roofWindow),
        Downpipe pipe => CarryCommon(new Downpipe { GutterId = pipe.GutterId, Location = pipe.Location, Distributor = pipe.Distributor }, pipe),
        SlabEdge slabEdge => CloneSlabEdge(slabEdge),
        RoofDrain drain => CarryCommon(new RoofDrain { RoofId = drain.RoofId, Location = drain.Location, OutletDiameter = drain.OutletDiameter, Discharge = drain.Discharge }, drain),
        PolygonWall shaped => CarryCommon(new PolygonWall
        {
            Kind = shaped.Kind, Outline = shaped.Outline.ToList(), Start = shaped.Start, End = shaped.End,
            StartThickness = shaped.StartThickness, EndThickness = shaped.EndThickness, StraightSide = shaped.StraightSide,
            MaterialId = shaped.MaterialId, BaseOffset = shaped.BaseOffset, TopLevelId = shaped.TopLevelId, TopOffset = shaped.TopOffset,
            UnconnectedHeight = shaped.UnconnectedHeight, BaseAttachedTo = shaped.BaseAttachedTo
        }, shaped),
        WallFraming framing => CarryCommon(new WallFraming
        {
            HostId = framing.HostId, Material = framing.Material, Section = framing.Section, Spacing = framing.Spacing,
            TopPlates = framing.TopPlates, NoggingRows = framing.NoggingRows, FromEnd = framing.FromEnd
        }, framing),
        Part part => CarryCommon(new Part
        {
            HostId = part.HostId, Layer = part.Layer, From = part.From, To = part.To, Bottom = part.Bottom, Top = part.Top,
            Gap = part.Gap, MaterialId = part.MaterialId, Excluded = part.Excluded
        }, part),
        Chimney chimney => CarryCommon(WithFireplacesAbove(new Chimney
        {
            Location = chimney.Location, Width = chimney.Width, Depth = chimney.Depth, Angle = chimney.Angle, BaseOffset = chimney.BaseOffset,
            Flues = chimney.Flues, Rule = chimney.Rule, ExtraHeight = chimney.ExtraHeight, Fireplace = chimney.Fireplace
        }, chimney), chimney),
        Stair stair => CarryCommon(new Stair
        {
            Start = stair.Start, Angle = stair.Angle, Shape = stair.Shape, Corner = stair.Corner, TurnsLeft = stair.TurnsLeft,
            Width = stair.Width, RisersBeforeTurn = stair.RisersBeforeTurn, Well = stair.Well, InnerRadius = stair.InnerRadius, BaseOffset = stair.BaseOffset,
            PathLengths = stair.PathLengths.ToList(), PathTurns = stair.PathTurns.ToList(), RepeatToLevelId = stair.RepeatToLevelId,
            ShowRiserNumbers = stair.ShowRiserNumbers, ShowFlightDimensions = stair.ShowFlightDimensions,
            Use = stair.Use, LoftConversion = stair.LoftConversion, Outside = stair.Outside, BottomStep = stair.BottomStep,
            TopLevelId = stair.TopLevelId, TopOffset = stair.TopOffset, UnconnectedHeight = stair.UnconnectedHeight,
            Railings = stair.Railings, CutsFloorAbove = stair.CutsFloorAbove, CutsRoof = stair.CutsRoof
        }, stair),
        Ramp ramp => CarryCommon(new Ramp
        {
            Start = ramp.Start, Angle = ramp.Angle, Anchor = ramp.Anchor, Shape = ramp.Shape, TurnsLeft = ramp.TurnsLeft, Width = ramp.Width,
            Gap = ramp.Gap, PathLengths = ramp.PathLengths.ToList(), PathTurns = ramp.PathTurns.ToList(), Gradient = ramp.Gradient,
            BaseOffset = ramp.BaseOffset, TopLevelId = ramp.TopLevelId, TopOffset = ramp.TopOffset, UnconnectedHeight = ramp.UnconnectedHeight,
            TopLanding = ramp.TopLanding, BottomLanding = ramp.BottomLanding, Building = ramp.Building, Outside = ramp.Outside, Railings = ramp.Railings,
            ClosedUnderneath = ramp.ClosedUnderneath
        }, ramp),
        Toposurface ground => CarryCommon(Points(new Toposurface { MaterialId = ground.MaterialId, ContourInterval = ground.ContourInterval }, ground.Points, ground.ExistingPoints), ground),
        GroundRegion region => CarryCommon(CopyPath(Outline(new GroundRegion { Surface = region.Surface, MaterialId = region.MaterialId, Approach = region.Approach, Offset = region.Offset }, region.Boundary), region), region),
        SitePad pad => CarryCommon(Outline(new SitePad
        {
            HeightOffset = pad.HeightOffset, Fall = pad.Fall, FallDirection = pad.FallDirection, Batter = pad.Batter, Surface = pad.Surface, MaterialId = pad.MaterialId
        }, pad.Boundary), pad),
        PropertyLine line => CarryCommon(Outline(new PropertyLine { Setback = line.Setback }, line.Boundary), line),
        Mass mass => CarryCommon(CloneMass(mass), mass),
        View3D view => CarryCommon(CloneView(view), view),
        Walkthrough walk => CarryCommon(CloneWalkthrough(walk), walk),
        Callout callout => CarryCommon(new Callout { Name = callout.Name, Parent = callout.Parent, Region = callout.Region }, callout),
        ViewTemplate template => CarryCommon(CloneTemplate(template), template),
        InteriorElevationMarker marker => CarryCommon(new InteriorElevationMarker
        {
            Name = marker.Name, Location = marker.Location, Rotation = marker.Rotation,
            LooksUp = marker.LooksUp, LooksRight = marker.LooksRight, LooksDown = marker.LooksDown, LooksLeft = marker.LooksLeft
        }, marker),
        SpotElevation spot => CarryCommon(new SpotElevation { Location = spot.Location }, spot),
        BeamSystem system => CarryCommon(Outline(new BeamSystem { Direction = system.Direction, Spacing = system.Spacing, Offset = system.Offset }, system.Boundary), system),
        Beam beam => CarryCommon(new Beam
        {
            Start = beam.Start, End = beam.End, StartOffset = beam.StartOffset, EndOffset = beam.EndOffset,
            Justification = beam.Justification, Usage = beam.Usage
        }, beam),
        Railing railing => CarryCommon(Path(new Railing
        {
            Closed = railing.Closed, BaseOffset = railing.BaseOffset, Building = railing.Building, Outside = railing.Outside,
            HostOpeningId = railing.HostOpeningId
        }, railing.Path, railing.Bulges), railing),
        ShaftOpening shaft => CarryCommon(new ShaftOpening
        {
            Location = shaft.Location, Width = shaft.Width, Depth = shaft.Depth, Shape = shaft.Shape, Angle = shaft.Angle,
            BaseOffset = shaft.BaseOffset, TopLevelId = shaft.TopLevelId, TopOffset = shaft.TopOffset,
            UnconnectedHeight = shaft.UnconnectedHeight
        }, shaft),

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

    /// <summary>A copied mass is the same shape; what was made from the original's faces stays the original's.</summary>
    private static Mass CloneMass(Mass original)
    {
        var copy = new Mass { BaseOffset = original.BaseOffset, Height = original.Height, Top = original.Top, Pitch = original.Pitch, TopScale = original.TopScale };
        if (original.Round) copy.SetCircle(original.Centre, original.Radius);
        else copy.SetBoundary(original.Boundary);
        copy.FloorLevels.AddRange(original.FloorLevels);
        return copy;
    }

    /// <summary>A copied path keeps its middle and width, and with them its outline.</summary>
    private static GroundRegion CopyPath(GroundRegion copy, GroundRegion original)
    {
        if (original.PathLine.Count >= 2)
        {
            copy.SetPath(original.PathLine, original.PathWidth);
            copy.SetBoundary(original.Boundary);
        }

        return copy;
    }

    private static Element CloneSlabEdge(SlabEdge original)
    {
        var copy = new SlabEdge { FloorId = original.FloorId, HorizontalOffset = original.HorizontalOffset, VerticalOffset = original.VerticalOffset };
        copy.Edges.AddRange(original.Edges);
        return CarryCommon(copy, original);
    }

    private static Slab CloneSlab(Slab copy, Slab original)
    {
        copy.HeightOffset = original.HeightOffset;
        copy.SetBoundary(original.Boundary);
        copy.SetHoles(original.Holes);
        if (copy is Floor copiedFloor && original is Floor floor) copiedFloor.SpanDirection = floor.SpanDirection;

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
            copied.HighLevelVents = roof.HighLevelVents;
            copied.DrainageFall = roof.DrainageFall;
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
    private static Element CarryRoofEdgeSweep(RoofEdgeSweep copy, RoofEdgeSweep original)
    {
        copy.RoofId = original.RoofId;
        copy.EdgeIds.AddRange(original.EdgeIds);
        copy.HorizontalOffset = original.HorizontalOffset;
        copy.VerticalOffset = original.VerticalOffset;
        return CarryCommon(copy, original);
    }

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

    /// <summary>A copied chimney given the original's fireplaces on the floors above its foot.</summary>
    private static Chimney WithFireplacesAbove(Chimney copy, Chimney original)
    {
        foreach (var (level, fireplace) in original.FireplacesAbove) copy.FireplacesAbove[level] = fireplace;
        return copy;
    }

    /// <summary>A copied ground given the original's spot levels, and the existing ground it recorded.</summary>
    private static Toposurface Points(Toposurface copy, IReadOnlyList<Geometry.Point3D> points, IReadOnlyList<Geometry.Point3D> existing)
    {
        copy.SetPoints(points);
        copy.SetExistingPoints(existing);
        return copy;
    }

    /// <summary>A copied region or joist layout given the original's outline.</summary>
    private static T Outline<T>(T copy, IReadOnlyList<Geometry.Point2D> outline) where T : Element
    {
        switch (copy)
        {
            case GroundRegion region: region.SetBoundary(outline); break;
            case SitePad pad: pad.SetBoundary(outline); break;
            case PropertyLine line: line.SetBoundary(outline); break;
            case BeamSystem system: system.SetBoundary(outline); break;
        }

        return copy;
    }

    /// <summary>A copied railing given the original's path.</summary>
    private static Railing Path(Railing copy, IReadOnlyList<Geometry.Point2D> path, IReadOnlyList<double> bulges)
    {
        copy.SetPath(path, bulges);
        return copy;
    }

    /// <summary>A copied 3D view looks from the same place, drawn the same way, showing the same.</summary>
    private static View3D CloneView(View3D original)
    {
        var copy = new View3D
        {
            Name = original.Name, Camera = original.Camera, Perspective = original.Perspective, Style = original.Style,
            SectionBox = original.SectionBox, SectionBoxOn = original.SectionBoxOn, ShowGround = original.ShowGround, ShowEdges = original.ShowEdges
        };
        copy.HiddenLevels.UnionWith(original.HiddenLevels);
        copy.HiddenKinds.UnionWith(original.HiddenKinds);
        return copy;
    }

    /// <summary>A copied view template sets the same settings, the same ones included.</summary>
    private static ViewTemplate CloneTemplate(ViewTemplate original)
    {
        var copy = new ViewTemplate
        {
            Name = original.Name, For = original.For,
            IncludeScale = original.IncludeScale, Scale = original.Scale,
            IncludeDetailLevel = original.IncludeDetailLevel, DetailLevel = original.DetailLevel,
            IncludeWallFunctions = original.IncludeWallFunctions, IncludeJoinDisplay = original.IncludeJoinDisplay, JoinDisplay = original.JoinDisplay,
            IncludeVisualStyle = original.IncludeVisualStyle, VisualStyle = original.VisualStyle, IncludeHiddenKinds = original.IncludeHiddenKinds,
            IncludeFilters = original.IncludeFilters, IncludePhaseFilter = original.IncludePhaseFilter, PhaseFilter = original.PhaseFilter
        };
        copy.HiddenWallFunctions.UnionWith(original.HiddenWallFunctions);
        copy.HiddenKinds.UnionWith(original.HiddenKinds);
        copy.Filters.AddRange(original.Filters);
        return copy;
    }

    /// <summary>A copied walkthrough follows the same path, at the same height and pace.</summary>
    private static Walkthrough CloneWalkthrough(Walkthrough original)
    {
        var copy = new Walkthrough { Name = original.Name, EyeHeight = original.EyeHeight, Frames = original.Frames, FramesPerSecond = original.FramesPerSecond };
        copy.SetPath(original.Path);
        return copy;
    }

    /// <summary>A copied dimension measures to the same things - a string's between them too - offset and worded the same.</summary>
    private static Dimension CloneDimension(Dimension original)
    {
        var copy = new Dimension
        {
            Start = CloneReference(original.Start), End = CloneReference(original.End), Offset = original.Offset, Override = original.Override
        };
        copy.Between.AddRange(original.Between.Select(CloneReference));
        return copy;
    }

    private static RevisionCloud CloneCloud(RevisionCloud cloud)
    {
        var copy = new RevisionCloud { RevisionId = cloud.RevisionId, TagAt = cloud.TagAt };
        copy.Outline.AddRange(cloud.Outline);
        return copy;
    }

    private static Element CarryCommon(Element copy, Element original)
    {
        copy.TypeId = original.TypeId;
        copy.LevelId = original.LevelId;
        copy.Comments = original.Comments;
        copy.Workset = original.Workset;
        copy.PhaseCreated = original.PhaseCreated;
        copy.PhaseDemolished = original.PhaseDemolished;
        copy.DesignOptionId = original.DesignOptionId;
        foreach (var (id, value) in original.ParameterValues) copy.ParameterValues[id] = value;

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

        // A Juliet balcony copied with its door guards the copy; copied without it, it is a railing.
        if (copy is Railing { HostOpeningId: { } guarded } juliet)
            juliet.HostOpeningId = replacements.TryGetValue(guarded, out var opened) ? opened : null;

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

            case RoofEdgeSweep edge when replacements.TryGetValue(edge.RoofId, out var edgeRoof):
                edge.RoofId = edgeRoof;
                break;

            case RoofWindow roofWindow when replacements.TryGetValue(roofWindow.RoofId, out var windowRoof):
                roofWindow.RoofId = windowRoof;
                break;

            case WallFooting strip when replacements.TryGetValue(strip.WallId, out var footingWall):
                strip.WallId = footingWall;
                break;

            case PadFooting { ColumnId: { } carried } pad when replacements.TryGetValue(carried, out var footingColumn):
                pad.ColumnId = footingColumn;
                break;

            case KeynoteTag keynote when replacements.TryGetValue(keynote.TargetId, out var noted):
                keynote.TargetId = noted;
                break;

            case Downpipe pipe when replacements.TryGetValue(pipe.GutterId, out var pipeGutter):
                pipe.GutterId = pipeGutter;
                break;

            case SlabEdge slabEdge when replacements.TryGetValue(slabEdge.FloorId, out var edgeFloor):
                slabEdge.FloorId = edgeFloor;
                break;

            case RoofDrain drain when replacements.TryGetValue(drain.RoofId, out var drainRoof):
                drain.RoofId = drainRoof;
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

                foreach (var between in dimension.Between)
                    if (replacements.TryGetValue(between.ElementId, out var to))
                        between.ElementId = to;

                break;

            case AngularDimension angular:
                if (replacements.TryGetValue(angular.First.ElementId, out var first)) angular.First.ElementId = first;
                if (replacements.TryGetValue(angular.Second.ElementId, out var second)) angular.Second.ElementId = second;
                break;

            case RadialDimension radial when replacements.TryGetValue(radial.Target.ElementId, out var wall):
                radial.Target.ElementId = wall;
                break;
        }
    }
}
