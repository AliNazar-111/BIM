using System.Text.Json;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>Thrown when a file is not a project, or is a newer format than this build knows.</summary>
public sealed class ProjectFileException : Exception
{
    public ProjectFileException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Reads and writes BIMDesigner project files (<c>.bimx</c>), which are JSON.
///
/// JSON is the right format for this stage: a project is diffable, greppable and fixable by
/// hand while the schema is still moving. It will not stay the right format - section 12.3
/// asks for large-model performance and section 7 for worksharing - but replacing it later
/// is a change to this one class, because everything else talks to <see cref="BimDocument"/>.
/// </summary>
public static class ProjectFile
{
    /// <summary>Raised by one when the format changes in a way older builds cannot read.</summary>
    public const int CurrentFormatVersion = 3;

    public const string Extension = ".bimx";

    public const string FileFilter = "BIMDesigner project (*.bimx)|*.bimx|All files (*.*)|*.*";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ---- saving ----------------------------------------------------------------

    public static void Save(BimDocument document, string path)
    {
        var json = JsonSerializer.Serialize(ToDto(document), Options);

        // Write to a temporary file first, so a failure part-way through cannot destroy the
        // previous save.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Serialises without touching the disk. Used by the round-trip tests.</summary>
    public static string ToJson(BimDocument document) =>
        JsonSerializer.Serialize(ToDto(document), Options);

    private static ProjectFileDto ToDto(BimDocument document)
    {
        var dto = new ProjectFileDto
        {
            SavedUtc = DateTime.UtcNow,
            Project = new ProjectInformationDto
            {
                Name = document.ProjectInformation.Name,
                Number = document.ProjectInformation.Number,
                Client = document.ProjectInformation.Client,
                Address = document.ProjectInformation.Address,
                BuildingType = document.ProjectInformation.BuildingType
            }
        };

        foreach (var level in document.Levels)
            dto.Levels.Add(new LevelDto { Id = level.Id, Name = level.Name, Elevation = level.Elevation });

        foreach (var material in document.Materials)
            dto.Materials.Add(new MaterialDto
            {
                Id = material.Id,
                Name = material.Name,
                Density = material.Density,
                ThermalConductivity = material.ThermalConductivity,
                SurfaceColour = material.SurfaceColour.ToString(),
                CutColour = material.CutColour.ToString(),
                Manufacturer = material.Manufacturer,
                ClassificationCode = material.ClassificationCode,
                CostPerCubicMetre = material.CostPerCubicMetre
            });

        foreach (var stacked in document.TypesOf<StackedWallType>())
            dto.StackedWallTypes.Add(new StackedWallTypeDto
            {
                Id = stacked.Id,
                Name = stacked.Name,
                TypeMark = stacked.TypeMark,
                Description = stacked.Description,
                Cost = stacked.Cost,
                Tiers = stacked.Tiers.Select(tier => new StackTierDto { WallTypeId = tier.WallTypeId, Height = tier.Height }).ToList()
            });

        foreach (var profile in document.TypesOf<SweepProfileType>())
            dto.SweepProfiles.Add(new SweepProfileTypeDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Description = profile.Description,
                Points = profile.Points.SelectMany(p => new[] { p.X, p.Y }).ToList()
            });

        foreach (var sweepType in document.TypesOf<WallSweepType>())
            dto.WallSweepTypes.Add(new WallSweepTypeDto
            {
                Id = sweepType.Id,
                Name = sweepType.Name,
                Kind = sweepType.Kind.ToString(),
                Profile = sweepType.Profile.ToString(),
                ProfileId = sweepType.ProfileId,
                Depth = sweepType.Depth,
                Height = sweepType.Height,
                MaterialId = sweepType.MaterialId,
                CutsWall = sweepType.CutsWall,
                Cuttable = sweepType.Cuttable,
                Setback = sweepType.Setback,
                Cost = sweepType.Cost,
                Description = sweepType.Description
            });

        foreach (var fascia in document.TypesOf<FasciaType>())
            dto.FasciaTypes.Add(new FasciaTypeDto
            {
                Id = fascia.Id, Name = fascia.Name, TypeMark = fascia.TypeMark, Thickness = fascia.Thickness, Depth = fascia.Depth,
                MaterialId = fascia.MaterialId, Cost = fascia.Cost, Description = fascia.Description
            });

        foreach (var gutter in document.TypesOf<GutterType>())
            dto.GutterTypes.Add(new GutterTypeDto
            {
                Id = gutter.Id, Name = gutter.Name, TypeMark = gutter.TypeMark, Shape = gutter.Shape.ToString(), Width = gutter.Width,
                Depth = gutter.Depth, WallThickness = gutter.WallThickness, MaterialId = gutter.MaterialId, Cost = gutter.Cost,
                Description = gutter.Description
            });

        foreach (var soffit in document.TypesOf<SoffitType>())
            dto.SoffitTypes.Add(new SoffitTypeDto
            {
                Id = soffit.Id, Name = soffit.Name, TypeMark = soffit.TypeMark, Thickness = soffit.Thickness,
                MaterialId = soffit.MaterialId, Cost = soffit.Cost, Description = soffit.Description
            });

        foreach (var sweep in document.Elements.OfType<RoofEdgeSweep>())
            dto.RoofEdgeSweeps.Add(new RoofEdgeSweepDto
            {
                Id = sweep.Id,
                Kind = sweep switch { Gutter => "Gutter", Soffit => "Soffit", _ => "Fascia" },
                TypeId = sweep.TypeId,
                LevelId = sweep.LevelId,
                RoofId = sweep.RoofId,
                EdgeIds = sweep.EdgeIds.ToList(),
                HorizontalOffset = sweep.HorizontalOffset,
                VerticalOffset = sweep.VerticalOffset,
                Mark = sweep.Mark,
                Comments = sweep.Comments
            });

        foreach (var opening in document.Elements.OfType<WallOpening>())
            dto.WallOpenings.Add(new WallOpeningDto
            {
                Id = opening.Id,
                HostWallId = opening.HostWallId,
                LevelId = opening.LevelId,
                DistanceAlongWall = opening.DistanceAlongWall,
                Width = opening.Width,
                Height = opening.Height,
                SillHeight = opening.SillHeight,
                Mark = opening.Mark,
                Comments = opening.Comments
            });

        foreach (var placed in document.Elements.OfType<PlacedSweep>())
            dto.PlacedSweeps.Add(new PlacedSweepDto
            {
                Id = placed.Id,
                TypeId = placed.TypeId,
                LevelId = placed.LevelId,
                Kind = placed.Kind.ToString(),
                HostWallIds = placed.HostWallIds.ToList(),
                Side = placed.Side.ToString(),
                Vertical = placed.Vertical,
                Elevation = placed.Elevation,
                Along = placed.Along,
                Offset = placed.Offset,
                Flip = placed.Flip,
                ReturnAtStart = placed.ReturnAtStart,
                ReturnAtEnd = placed.ReturnAtEnd,
                Mark = placed.Mark,
                Comments = placed.Comments
            });

        foreach (var curtain in document.TypesOf<CurtainWallType>())
            dto.CurtainWallTypes.Add(new CurtainWallTypeDto
            {
                Id = curtain.Id,
                Name = curtain.Name,
                TypeMark = curtain.TypeMark,
                AssemblyCode = curtain.AssemblyCode,
                Description = curtain.Description,
                Cost = curtain.Cost,
                Function = curtain.Function.ToString(),
                VerticalLayout = curtain.VerticalLayout.ToString(),
                VerticalSpacing = curtain.VerticalSpacing,
                VerticalCount = curtain.VerticalCount,
                VerticalJustification = curtain.VerticalJustification.ToString(),
                HorizontalLayout = curtain.HorizontalLayout.ToString(),
                HorizontalSpacing = curtain.HorizontalSpacing,
                HorizontalCount = curtain.HorizontalCount,
                HorizontalJustification = curtain.HorizontalJustification.ToString(),
                PanelThickness = curtain.PanelThickness,
                GlassMaterialId = curtain.GlassMaterialId,
                SolidMaterialId = curtain.SolidMaterialId,
                MullionProfile = curtain.MullionProfile.ToString(),
                MullionWidth = curtain.MullionWidth,
                MullionDepth = curtain.MullionDepth,
                MullionMaterialId = curtain.MullionMaterialId,
                BorderMullions = curtain.BorderMullions,
                AutomaticallyEmbed = curtain.AutomaticallyEmbed
            });

        foreach (var type in document.TypesOf<WallType>())
            dto.WallTypes.Add(new WallTypeDto
            {
                Id = type.Id,
                Name = type.Name,
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost,
                Function = type.Function.ToString(),
                FireRating = type.FireRating,
                AcousticRating = type.AcousticRating,
                ThermalResistance = type.ThermalResistance,
                HeatTransferCoefficient = type.HeatTransferCoefficient,
                WrapAtInserts = type.WrapAtInserts != WallWrapping.None,
                WrapAtEnds = type.WrapAtEnds != WallWrapping.None,
                WrappingAtInserts = type.WrapAtInserts.ToString(),
                WrappingAtEnds = type.WrapAtEnds.ToString(),
                ExteriorTaperAngle = type.ExteriorTaperAngle,
                InteriorTaperAngle = type.InteriorTaperAngle,
                Sweeps = type.Sweeps.Select(sweep => new WallSweepDto
                {
                    Kind = sweep.Kind.ToString(),
                    Profile = sweep.Profile.ToString(),
                    Side = sweep.Side.ToString(),
                    Depth = sweep.Depth,
                    Height = sweep.Height,
                    Elevation = sweep.Elevation,
                    FromTop = sweep.FromTop,
                    MaterialId = sweep.MaterialId,
                    Offset = sweep.Offset,
                    Flip = sweep.Flip,
                    Setback = sweep.Setback,
                    CutsWall = sweep.CutsWall,
                    Cuttable = sweep.Cuttable,
                    ProfileId = sweep.ProfileId,
                    Returns = sweep.Returns
                }).ToList(),
                CoarseScaleFillColour = type.CoarseScaleFillColour.ToString(),
                Layers = type.Structure.Layers.Select(layer => new MaterialLayerDto
                {
                    Function = layer.Function.ToString(),
                    MaterialId = layer.MaterialId,
                    Thickness = layer.Thickness,
                    Wraps = layer.Wraps
                }).ToList()
            });

        foreach (var type in document.TypesOf<DoorType>())
        {
            var doorType = new DoorTypeDto
            {
                Operation = type.Operation.ToString(),
                LeafCount = type.LeafCount,
                PanelMaterial = type.PanelMaterial,
                HardwareSet = type.HardwareSet,
                LeafDesign = type.LeafDesign.ToString(),
                GlazingRows = type.GlazingRows,
                GlazingColumns = type.GlazingColumns,
                Function = type.Function.ToString(),
                CurtainPanel = type.CurtainPanel,
                TrimWidth = type.TrimWidth,
                TrimProjectionExterior = type.TrimProjectionExterior,
                TrimProjectionInterior = type.TrimProjectionInterior
            };

            WriteOpeningType(doorType, type);
            dto.DoorTypes.Add(doorType);
        }

        foreach (var type in document.TypesOf<WindowType>())
        {
            var windowType = new WindowTypeDto
            {
                Operation = type.Operation.ToString(),
                GlazingType = type.GlazingType,
                GlazingRows = type.GlazingRows,
                GlazingColumns = type.GlazingColumns,
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient
            };

            WriteOpeningType(windowType, type);
            dto.WindowTypes.Add(windowType);
        }

        foreach (var door in document.Elements.OfType<Door>())
        {
            var doorDto = new DoorDto { SwingAngle = door.SwingAngle, FrameType = door.FrameType, FrameMaterial = door.FrameMaterial, Finish = door.Finish };
            WriteOpening(doorDto, door);
            dto.Doors.Add(doorDto);
        }

        foreach (var window in document.Elements.OfType<Window>())
        {
            var windowDto = new WindowDto();
            WriteOpening(windowDto, window);
            dto.Windows.Add(windowDto);
        }

        foreach (var dimension in document.Elements.OfType<Dimension>())
            dto.Dimensions.Add(new DimensionDto
            {
                Id = dimension.Id,
                LevelId = dimension.LevelId,
                Start = WriteEnd(dimension.Start),
                End = WriteEnd(dimension.End),
                Offset = dimension.Offset,
                Override = dimension.Override,
                Comments = dimension.Comments,
                Workset = dimension.Workset
            });

        foreach (var tag in document.Elements.OfType<Tag>())
            dto.Tags.Add(new TagDto
            {
                Id = tag.Id,
                LevelId = tag.LevelId,
                TargetId = tag.TargetId,
                Field = tag.Field,
                X = tag.Position.X,
                Y = tag.Position.Y,
                ShowLeader = tag.ShowLeader,
                Comments = tag.Comments,
                Workset = tag.Workset
            });

        foreach (var note in document.Elements.OfType<TextNote>())
            dto.TextNotes.Add(new TextNoteDto
            {
                Id = note.Id,
                LevelId = note.LevelId,
                Text = note.Text,
                X = note.Position.X,
                Y = note.Position.Y,
                LeaderX = note.LeaderEnd?.X,
                LeaderY = note.LeaderEnd?.Y,
                Comments = note.Comments,
                Workset = note.Workset
            });

        foreach (var section in document.Elements.OfType<SectionMarker>())
            dto.Sections.Add(new SectionDto
            {
                Id = section.Id,
                LevelId = section.LevelId,
                Name = section.Name,
                StartX = section.Start.X,
                StartY = section.Start.Y,
                EndX = section.End.X,
                EndY = section.End.Y,
                Flipped = section.Flipped,
                ViewDepth = section.ViewDepth,
                Comments = section.Comments,
                Workset = section.Workset
            });

        foreach (var sheet in document.Elements.OfType<Sheet>())
            dto.Sheets.Add(new SheetDto
            {
                Id = sheet.Id,
                Number = sheet.Number,
                Name = sheet.Name,
                PaperSize = sheet.PaperSize.ToString(),
                Orientation = sheet.Orientation.ToString(),
                DrawnBy = sheet.DrawnBy,
                CheckedBy = sheet.CheckedBy,
                Revision = sheet.Revision,
                IssuedOn = sheet.IssuedOn,
                Comments = sheet.Comments,
                Workset = sheet.Workset,
                Viewports = sheet.Viewports.Select(viewport => new ViewportDto
                {
                    Id = viewport.Id,
                    Kind = viewport.View.Kind.ToString(),
                    TargetId = viewport.View.TargetId,
                    ScheduleName = viewport.View.ScheduleName,
                    X = viewport.Centre.X,
                    Y = viewport.Centre.Y,
                    Scale = viewport.Scale.Denominator,
                    ShowTitle = viewport.ShowTitle,
                    TitleOverride = viewport.TitleOverride
                }).ToList()
            });

        foreach (var (view, hidden) in document.ViewSettings.Filtered)
            dto.ViewSettings.Add(new ViewSettingsDto
            {
                Kind = view.Kind.ToString(),
                TargetId = view.TargetId,
                HiddenWallFunctions = hidden.Select(function => function.ToString()).OrderBy(name => name).ToList(),
                Scale = document.ViewSettings.Scaled.Where(s => s.View == view).Select(s => (double?)s.Scale).FirstOrDefault(),
                JoinDisplay = document.ViewSettings.JoinDisplays.Where(s => s.View == view).Select(s => s.Display.ToString()).FirstOrDefault()
            });

        // Views with a scale or join display of their own but nothing hidden.
        foreach (var view in document.ViewSettings.Scaled.Select(s => s.View).Concat(document.ViewSettings.JoinDisplays.Select(s => s.View)).Distinct()
                     .Where(v => document.ViewSettings.Filtered.All(f => f.View != v)))
            dto.ViewSettings.Add(new ViewSettingsDto
            {
                Kind = view.Kind.ToString(),
                TargetId = view.TargetId,
                Scale = document.ViewSettings.Scaled.Where(s => s.View == view).Select(s => (double?)s.Scale).FirstOrDefault(),
                JoinDisplay = document.ViewSettings.JoinDisplays.Where(s => s.View == view).Select(s => s.Display.ToString()).FirstOrDefault()
            });

        foreach (var grid in document.Elements.OfType<Grid>())
            dto.Grids.Add(new GridDto
            {
                Id = grid.Id,
                Name = grid.Name,
                StartX = grid.Start.X,
                StartY = grid.Start.Y,
                EndX = grid.End.X,
                EndY = grid.End.Y,
                BubbleAtStart = grid.BubbleAtStart,
                BubbleAtEnd = grid.BubbleAtEnd,
                Comments = grid.Comments,
                Workset = grid.Workset
            });

        foreach (var type in document.TypesOf<SlabType>())
            dto.SlabTypes.Add(new SlabTypeDto
            {
                Id = type.Id,
                Kind = SlabKindOf(type.Category),
                Name = type.Name,
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost,
                Function = type.Function.ToString(),
                FireRating = type.FireRating,
                AcousticRating = type.AcousticRating,
                HeatTransferCoefficient = type.HeatTransferCoefficient,
                CoarseScaleFillColour = type.CoarseScaleFillColour.ToString(),
                Layers = type.Structure.Layers.Select(layer => new MaterialLayerDto
                {
                    Function = layer.Function.ToString(),
                    MaterialId = layer.MaterialId,
                    Thickness = layer.Thickness,
                    Wraps = layer.Wraps
                }).ToList()
            });

        foreach (var slab in document.Elements.OfType<Slab>())
            dto.Slabs.Add(new SlabDto
            {
                Id = slab.Id,
                TypeId = slab.TypeId,
                LevelId = slab.LevelId,
                Kind = SlabKindOf(slab.Category),
                HeightOffset = slab.HeightOffset,
                Boundary = slab.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                RoofEdges = slab is Roof roof
                    ? roof.Edges.Select(edge => new RoofEdgeDto
                    {
                        Id = edge.Id,
                        DefinesSlope = edge.DefinesSlope,
                        SlopeDegrees = edge.SlopeDegrees,
                        PlateOffset = edge.PlateOffset,
                        WallId = edge.WallId,
                        OnLeftOfWall = edge.OnLeftOfWall,
                        Overhang = edge.Overhang,
                        ExtendToCore = edge.ExtendToCore,
                        ArcId = edge.ArcId
                    }).ToList()
                    : new List<RoofEdgeDto>(),
                RoofCutoff = slab is Roof cut ? cut.CutoffOffset : 0,
                RoofCutoffLevelId = slab is Roof withLevel ? withLevel.CutoffLevelId : null,
                RoofRafterCut = slab is Roof eaves ? eaves.RafterCut.ToString() : "PlumbCut",
                RoofBearing = slab is Roof bearing ? bearing.Bearing.ToString() : "Truss",
                RoofJoinedTo = (slab as Roof)?.JoinedTo,
                RoofDormerOpenings = slab is Roof { DormerOpenings.Count: > 0 } opened ? opened.DormerOpenings.ToList() : null,
                RoofDormer = slab is Roof { Dormer: { } made } dormer
                    ? new RoofDormerDto
                    {
                        Shape = made.Shape.ToString(), Width = made.Width, Height = made.Height,
                        Slope = made.Slope, Overhang = made.Overhang, Walls = dormer.DormerWalls.ToList()
                    }
                    : null,
                RoofOpenings = slab is Roof { Openings.Count: > 0 } holed
                    ? holed.Openings.Select(opening => new RoofOpeningDto
                    {
                        Points = opening.Points.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                        ArcIds = opening.ArcIds.Any(id => id is not null) ? opening.ArcIds.ToList() : null
                    }).ToList()
                    : null,
                RoofFasciaDepth = slab is Roof fascia ? fascia.FasciaDepth : 150,
                RoofExtrusion = slab is Roof { Extrusion: { } extrusion }
                    ? new RoofExtrusionDto
                    {
                        OriginX = extrusion.Origin.X, OriginY = extrusion.Origin.Y,
                        DirectionX = extrusion.Direction.X, DirectionY = extrusion.Direction.Y,
                        Profile = extrusion.Profile.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                        Sagittas = extrusion.HasArcs ? extrusion.Sagittas.ToList() : null,
                        Start = extrusion.Start, End = extrusion.End
                    }
                    : null,
                RoofArrows = slab is Roof arrowed
                    ? arrowed.SlopeArrows.Select(arrow => new RoofArrowDto
                    {
                        TailX = arrow.Tail.X, TailY = arrow.Tail.Y, HeadX = arrow.Head.X, HeadY = arrow.Head.Y,
                        ByHeights = arrow.ByHeights, SlopeDegrees = arrow.SlopeDegrees,
                        TailOffset = arrow.TailOffset, HeadOffset = arrow.HeadOffset
                    }).ToList()
                    : new List<RoofArrowDto>(),
                Mark = slab.Mark,
                Comments = slab.Comments,
                Workset = slab.Workset,
                PhaseCreated = slab.PhaseCreated.ToString()
            });

        foreach (var type in document.TypesOf<ColumnType>())
            dto.ColumnTypes.Add(new ColumnTypeDto
            {
                Id = type.Id,
                Name = type.Name,
                Shape = type.Shape.ToString(),
                Width = type.Width,
                Depth = type.Depth,
                MaterialId = type.MaterialId,
                CoarseScaleFillColour = type.CoarseScaleFillColour.ToString(),
                OffsetBase = type.OffsetBase,
                OffsetTop = type.OffsetTop,
                TopScale = type.Shaping.TopScale,
                Twist = type.Shaping.Twist,
                SlantAcross = type.Shaping.SlantAcross,
                SlantAlong = type.Shaping.SlantAlong,
                Flutes = type.Shaping.Flutes,
                FluteDepth = type.Shaping.FluteDepth,
                BaseHeight = type.Shaping.BaseHeight,
                BaseSpread = type.Shaping.BaseSpread,
                CapitalHeight = type.Shaping.CapitalHeight,
                CapitalSpread = type.Shaping.CapitalSpread,

                // A drawn section is stored as its loops, each a flat run of x, y pairs.
                ProfileLoops = type.CustomProfile is { IsEmpty: false } profile
                    ? profile.Loops.Select(loop => loop.SelectMany(p => new[] { p.X, p.Y }).ToList()).ToList()
                    : null,
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost
            });

        foreach (var column in document.Elements.OfType<Column>())
            dto.Columns.Add(new ColumnDto
            {
                Id = column.Id,
                TypeId = column.TypeId,
                LevelId = column.LevelId,
                LocationX = column.Location.X,
                LocationY = column.Location.Y,
                Rotation = column.Rotation,
                BaseOffset = column.BaseOffset,
                TopLevelId = column.TopLevelId,
                TopOffset = column.TopOffset,
                UnconnectedHeight = column.UnconnectedHeight,
                TopAttachedTo = column.TopAttachedTo,
                BaseAttachedTo = column.BaseAttachedTo,
                MovesWithGrids = column.MovesWithGrids,
                RoomBounding = column.RoomBounding,
                CutByWalls = column.CutByWalls,
                Placement = column.Placement.ToString(),
                PlacementFace = column.PlacementFace.ToString(),
                TopAttachmentStyle = column.TopAttachmentStyle.ToString(),
                BaseAttachmentStyle = column.BaseAttachmentStyle.ToString(),
                OffsetFromAttachmentAtTop = column.OffsetFromAttachmentAtTop,
                OffsetFromAttachmentAtBase = column.OffsetFromAttachmentAtBase,
                Mark = column.Mark,
                Comments = column.Comments,
                Workset = column.Workset,
                PhaseCreated = column.PhaseCreated.ToString(),
                PhaseDemolished = column.PhaseDemolished?.ToString()
            });

        foreach (var type in document.TypesOf<ComponentType>())
            dto.ComponentTypes.Add(new ComponentTypeDto
            {
                Id = type.Id,
                Name = type.Name,
                Category = type.Kind.ToString(),
                Form = type.Form.ToString(),
                Placement = type.Placement.ToString(),
                Width = type.Width,
                Depth = type.Depth,
                Height = type.Height,
                DefaultElevation = type.DefaultElevation,
                MaterialId = type.MaterialId,
                Colour = type.Colour.ToString(),
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost
            });

        foreach (var component in document.Elements.OfType<Component>())
            dto.Components.Add(new ComponentDto
            {
                Id = component.Id,
                TypeId = component.TypeId,
                LevelId = component.LevelId,
                LocationX = component.Location.X,
                LocationY = component.Location.Y,
                Rotation = component.Rotation,
                Elevation = component.Elevation,
                HostId = component.HostId,
                FlipFacing = component.FlipFacing,
                Mark = component.Mark,
                Comments = component.Comments,
                Workset = component.Workset,
                PhaseCreated = component.PhaseCreated.ToString(),
                PhaseDemolished = component.PhaseDemolished?.ToString()
            });

        foreach (var room in document.Elements.OfType<Room>())
            dto.Rooms.Add(new RoomDto
            {
                Id = room.Id,
                LevelId = room.LevelId,
                LocationX = room.Location.X,
                LocationY = room.Location.Y,
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
                Comments = room.Comments,
                Workset = room.Workset,
                PhaseCreated = room.PhaseCreated.ToString()
            });

        foreach (var wall in document.Walls)
            dto.Walls.Add(new WallDto
            {
                Id = wall.Id,
                TypeId = wall.TypeId,
                LevelId = wall.LevelId,
                StartX = wall.Start.X,
                StartY = wall.Start.Y,
                EndX = wall.End.X,
                EndY = wall.End.Y,
                LocationLine = wall.LocationLine.ToString(),
                Flipped = wall.Flipped,
                BaseOffset = wall.BaseOffset,
                TopLevelId = wall.TopLevelId,
                TopOffset = wall.TopOffset,
                UnconnectedHeight = wall.UnconnectedHeight,
                RoomBounding = wall.RoomBounding,
                StructuralUsage = wall.StructuralUsage.ToString(),
                Bulge = wall.Bulge,
                EllipseRatio = wall.Ellipse?.Ratio,
                EllipseFrom = wall.Ellipse?.From,
                EllipseTo = wall.Ellipse?.To,
                SplinePoints = wall.Spline?.Through.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                SplineFrom = wall.Spline?.From,
                JoinedTo = wall.JoinedTo.Count > 0 ? wall.JoinedTo.ToList() : null,
                LockedToJoined = wall.LockedToJoined,
                StartLocked = wall.StartLocked,
                EndLocked = wall.EndLocked,
                StartCleanup = wall.StartCleanup == WallJoinCleanup.UseViewSetting ? null : wall.StartCleanup.ToString(),
                EndCleanup = wall.EndCleanup == WallJoinCleanup.UseViewSetting ? null : wall.EndCleanup.ToString(),
                SplineTo = wall.Spline?.To,
                Profile = wall.Profile?.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                ProfileLength = wall.ProfileLength,
                CurtainVerticals = wall.CurtainGrid?.Verticals.ToList(),
                CurtainHorizontals = wall.CurtainGrid?.Horizontals.ToList(),
                CurtainGlass = wall.CurtainGlass.ToString(),
                CurtainPanels = wall.CurtainPanels?.Select(p => new CurtainPanelDto { Column = p.Column, Row = p.Row, Kind = p.Kind.ToString(), OpeningTypeId = p.OpeningTypeId, Glass = p.Glass.ToString(), FlipHand = p.FlipHand, FlipFacing = p.FlipFacing, IsOpen = p.IsOpen }).ToList(),
                TopAttachedTo = wall.TopAttachedTo,
                BaseAttachedTo = wall.BaseAttachedTo,
                CrossSection = wall.CrossSection.ToString(),
                SlantAngle = wall.SlantAngle,
                AcrossOffset = wall.AcrossOffset,
                UpperSlantAngle = wall.UpperSlantAngle,
                SlantBreakHeight = wall.SlantBreakHeight,
                OverrideTaper = wall.OverrideTaper,
                ExteriorTaper = wall.ExteriorTaper,
                InteriorTaper = wall.InteriorTaper,
                StartJoin = wall.StartJoin.ToString(),
                EndJoin = wall.EndJoin.ToString(),
                Mark = wall.Mark,
                Comments = wall.Comments,
                Workset = wall.Workset,
                PhaseCreated = wall.PhaseCreated.ToString(),
                PhaseDemolished = wall.PhaseDemolished?.ToString()
            });

        return dto;
    }

    private static DimensionEndDto WriteEnd(DimensionReference reference) => new()
    {
        ElementId = reference.ElementId,
        Anchor = reference.Anchor.ToString(),
        X = reference.FallbackPoint.X,
        Y = reference.FallbackPoint.Y
    };

    private static DimensionReference ReadEnd(DimensionEndDto dto) => new()
    {
        ElementId = dto.ElementId,
        Anchor = ParseEnum(dto.Anchor, DimensionAnchor.Point),
        FallbackPoint = new Point2D(dto.X, dto.Y)
    };

    private static string SlabKindOf(BuiltInCategory category) => category switch
    {
        BuiltInCategory.Ceilings => "Ceiling",
        BuiltInCategory.Roofs => "Roof",
        _ => "Floor"
    };

    private static void WriteOpeningType(OpeningTypeDto dto, OpeningType type)
    {
        dto.Id = type.Id;
        dto.Name = type.Name;
        dto.TypeMark = type.TypeMark;
        dto.AssemblyCode = type.AssemblyCode;
        dto.Keynote = type.Keynote;
        dto.Manufacturer = type.Manufacturer;
        dto.Url = type.Url;
        dto.Description = type.Description;
        dto.Cost = type.Cost;
        dto.Width = type.Width;
        dto.Height = type.Height;
        dto.Thickness = type.Thickness;
        dto.FrameMaterial = type.FrameMaterial;
        dto.FireRating = type.FireRating;
        dto.AcousticRating = type.AcousticRating;
        dto.HeatTransferCoefficient = type.HeatTransferCoefficient;
    }

    private static void WriteOpening(OpeningDto dto, Opening opening)
    {
        dto.Id = opening.Id;
        dto.TypeId = opening.TypeId;
        dto.LevelId = opening.LevelId;
        dto.HostWallId = opening.HostWallId;
        dto.DistanceAlongWall = opening.DistanceAlongWall;
        dto.SillHeight = opening.SillHeight;
        dto.FlipFacing = opening.FlipFacing;
        dto.FlipHand = opening.FlipHand;
        dto.Orientation = opening.Orientation.ToString();
        dto.IsOpen = opening.IsOpen;
        dto.WidthOverride = opening.WidthOverride;
        dto.HeightOverride = opening.HeightOverride;
        dto.Mark = opening.Mark;
        dto.Comments = opening.Comments;
        dto.Workset = opening.Workset;
        dto.PhaseCreated = opening.PhaseCreated.ToString();
        dto.PhaseDemolished = opening.PhaseDemolished?.ToString();
    }

    // ---- loading ---------------------------------------------------------------

    public static BimDocument Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileException($"Could not read '{Path.GetFileName(path)}'.", e);
        }

        return FromJson(json);
    }

    public static BimDocument FromJson(string json)
    {
        ProjectFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProjectFileDto>(json, Options);
        }
        catch (JsonException e)
        {
            throw new ProjectFileException("This file is not a BIMDesigner project.", e);
        }

        if (dto is null) throw new ProjectFileException("This file is empty.");

        if (dto.FormatVersion > CurrentFormatVersion)
            throw new ProjectFileException(
                $"This project was saved by a newer version of BIMDesigner (format {dto.FormatVersion}). " +
                $"This build reads up to format {CurrentFormatVersion}.");

        return FromDto(dto);
    }

    /// <summary>
    /// Which layers turn round an exposed end, as a saved wall type meant it.
    ///
    /// Before format 2 nothing turned round an end: the setting existed but every wall was
    /// saved with it off, so a corner showed a section through the wall - the cavity and the
    /// blockwork on show beside whatever the wall turned into. A wall with a finish outside its
    /// core returns that finish instead, so those older types are brought up to it. From format
    /// 2 on, what the file says is what the wall was asked for.
    /// </summary>
    private static WallWrapping EndWrapping(int version, WallTypeDto type, CompoundStructure structure)
    {
        // Older files still said only yes or no: yes meant the exterior finish at the ends.
        var saved = string.IsNullOrEmpty(type.WrappingAtEnds)
            ? type.WrapAtEnds ? WallWrapping.Exterior : WallWrapping.None
            : ParseEnum(type.WrappingAtEnds, WallWrapping.None);

        if (version >= 2 || saved != WallWrapping.None) return saved;

        if (structure.ExteriorWidth > 0) return WallWrapping.Exterior;
        return structure.InteriorWidth > 0 ? WallWrapping.Interior : WallWrapping.None;
    }

    private static BimDocument FromDto(ProjectFileDto dto)
    {
        var document = new BimDocument();

        document.ProjectInformation.Name = dto.Project.Name;
        document.ProjectInformation.Number = dto.Project.Number;
        document.ProjectInformation.Client = dto.Project.Client;
        document.ProjectInformation.Address = dto.Project.Address;
        document.ProjectInformation.BuildingType = dto.Project.BuildingType;

        foreach (var level in dto.Levels)
            document.AddLevel(new Level { Id = level.Id, Name = level.Name, Elevation = level.Elevation });

        foreach (var material in dto.Materials)
            document.AddMaterial(new Material(material.Name)
            {
                Id = material.Id,
                Density = material.Density,
                ThermalConductivity = material.ThermalConductivity,
                SurfaceColour = ParseColour(material.SurfaceColour, new ColourRgb(0x9A, 0x9A, 0x9A)),
                CutColour = ParseColour(material.CutColour, new ColourRgb(0x7A, 0x7A, 0x7A)),
                Manufacturer = material.Manufacturer,
                ClassificationCode = material.ClassificationCode,
                CostPerCubicMetre = material.CostPerCubicMetre
            });

        foreach (var type in dto.WallTypes)
        {
            var structure = new CompoundStructure();
            foreach (var layer in type.Layers)
            {
                if (ReadLayer(layer) is { } read) structure.Add(read);
            }

            document.AddType(new WallType(type.Name, structure)
            {
                Id = type.Id,
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost,
                Function = ParseEnum(type.Function, WallFunction.Interior),
                FireRating = type.FireRating,
                AcousticRating = type.AcousticRating,
                ThermalResistance = type.ThermalResistance,
                HeatTransferCoefficient = type.HeatTransferCoefficient,
                // Older files said only yes or no: yes meant both sides at openings and the
                // exterior at ends, which is what those walls were understood to do.
                WrapAtInserts = string.IsNullOrEmpty(type.WrappingAtInserts)
                    ? type.WrapAtInserts ? WallWrapping.Both : WallWrapping.None
                    : ParseEnum(type.WrappingAtInserts, WallWrapping.Both),
                WrapAtEnds = EndWrapping(dto.FormatVersion, type, structure),
                ExteriorTaperAngle = Math.Clamp(type.ExteriorTaperAngle, -WallLean.MaxAngle, WallLean.MaxAngle),
                InteriorTaperAngle = Math.Clamp(type.InteriorTaperAngle, -WallLean.MaxAngle, WallLean.MaxAngle),
                CoarseScaleFillColour = ParseColour(type.CoarseScaleFillColour, new ColourRgb(0x8A, 0x93, 0xA1))
            });

            // A sweep with no size is not a sweep.
            var loaded = document.FindType<WallType>(type.Id)!;
            loaded.Sweeps.AddRange(type.Sweeps
                .Where(sweep => sweep.Depth > 0 && sweep.Height > 0)
                .Select(sweep => new WallSweep(
                    ParseEnum(sweep.Kind, SweepKind.Sweep),
                    ParseEnum(sweep.Profile, SweepProfile.Rectangle),
                    ParseEnum(sweep.Side, WallSide.Exterior),
                    sweep.Depth,
                    sweep.Height,
                    Math.Max(0, sweep.Elevation),
                    sweep.FromTop,
                    sweep.MaterialId,
                    double.IsFinite(sweep.Offset) ? sweep.Offset : 0,
                    sweep.Flip,
                    double.IsFinite(sweep.Setback) ? Math.Max(0, sweep.Setback) : 0,
                    sweep.CutsWall,
                    sweep.Cuttable,
                    sweep.ProfileId,
                    sweep.Returns)));
        }

        // After the wall types, which their tiers are made of.
        foreach (var dtoStacked in dto.StackedWallTypes)
        {
            var stacked = new StackedWallType(dtoStacked.Name)
            {
                Id = dtoStacked.Id,
                TypeMark = dtoStacked.TypeMark,
                Description = dtoStacked.Description,
                Cost = dtoStacked.Cost
            };

            stacked.Tiers.AddRange(dtoStacked.Tiers.Select(tier => new StackTier(tier.WallTypeId, Math.Max(0, tier.Height))));
            document.AddType(stacked);
        }

        foreach (var dtoProfile in dto.SweepProfiles)
        {
            var profile = new SweepProfileType(dtoProfile.Name) { Id = dtoProfile.Id, Description = dtoProfile.Description };
            for (var i = 0; i + 1 < dtoProfile.Points.Count; i += 2)
                if (double.IsFinite(dtoProfile.Points[i]) && double.IsFinite(dtoProfile.Points[i + 1]))
                    profile.Points.Add(new Point2D(dtoProfile.Points[i], dtoProfile.Points[i + 1]));
            if (profile.Points.Count >= 3) document.AddType(profile);
        }

        foreach (var dtoSweep in dto.WallSweepTypes)
        {
            document.AddType(new WallSweepType(dtoSweep.Name, ParseEnum(dtoSweep.Kind, SweepKind.Sweep))
            {
                Id = dtoSweep.Id,
                Profile = ParseEnum(dtoSweep.Profile, SweepProfile.Rectangle),
                ProfileId = dtoSweep.ProfileId,
                Depth = PositiveOr(dtoSweep.Depth, 20),
                Height = PositiveOr(dtoSweep.Height, 100),
                MaterialId = dtoSweep.MaterialId,
                CutsWall = dtoSweep.CutsWall,
                Cuttable = dtoSweep.Cuttable,
                Setback = double.IsFinite(dtoSweep.Setback) ? Math.Max(0, dtoSweep.Setback) : 0,
                Cost = dtoSweep.Cost,
                Description = dtoSweep.Description
            });
        }

        foreach (var fascia in dto.FasciaTypes)
            document.AddType(new FasciaType(fascia.Name)
            {
                Id = fascia.Id, TypeMark = fascia.TypeMark, Thickness = PositiveOr(fascia.Thickness, 25),
                Depth = double.IsFinite(fascia.Depth) ? Math.Max(0, fascia.Depth) : 0,
                MaterialId = fascia.MaterialId, Cost = fascia.Cost, Description = fascia.Description
            });

        foreach (var soffit in dto.SoffitTypes)
            document.AddType(new SoffitType(soffit.Name)
            {
                Id = soffit.Id, TypeMark = soffit.TypeMark, Thickness = PositiveOr(soffit.Thickness, 12),
                MaterialId = soffit.MaterialId, Cost = soffit.Cost, Description = soffit.Description
            });

        foreach (var gutter in dto.GutterTypes)
            document.AddType(new GutterType(gutter.Name)
            {
                Id = gutter.Id, TypeMark = gutter.TypeMark, Shape = ParseEnum(gutter.Shape, GutterShape.HalfRound),
                Width = PositiveOr(gutter.Width, 125), Depth = PositiveOr(gutter.Depth, 75), WallThickness = PositiveOr(gutter.WallThickness, 4),
                MaterialId = gutter.MaterialId, Cost = gutter.Cost, Description = gutter.Description
            });

        foreach (var dtoCurtain in dto.CurtainWallTypes)
        {
            var curtain = new CurtainWallType(dtoCurtain.Name)
            {
                Id = dtoCurtain.Id,
                TypeMark = dtoCurtain.TypeMark,
                AssemblyCode = dtoCurtain.AssemblyCode,
                Description = dtoCurtain.Description,
                Cost = dtoCurtain.Cost,
                Function = ParseEnum(dtoCurtain.Function, WallFunction.Exterior),
                VerticalLayout = ParseEnum(dtoCurtain.VerticalLayout, CurtainGridLayout.FixedDistance),
                VerticalSpacing = dtoCurtain.VerticalSpacing,
                VerticalCount = dtoCurtain.VerticalCount,
                VerticalJustification = ParseEnum(dtoCurtain.VerticalJustification, CurtainGridJustification.Centre),
                HorizontalLayout = ParseEnum(dtoCurtain.HorizontalLayout, CurtainGridLayout.FixedDistance),
                HorizontalSpacing = dtoCurtain.HorizontalSpacing,
                HorizontalCount = dtoCurtain.HorizontalCount,
                HorizontalJustification = ParseEnum(dtoCurtain.HorizontalJustification, CurtainGridJustification.Beginning),
                PanelThickness = dtoCurtain.PanelThickness,
                GlassMaterialId = dtoCurtain.GlassMaterialId,
                SolidMaterialId = dtoCurtain.SolidMaterialId,
                MullionProfile = ParseEnum(dtoCurtain.MullionProfile, MullionProfile.Rectangular),
                MullionWidth = dtoCurtain.MullionWidth,
                MullionDepth = dtoCurtain.MullionDepth,
                MullionMaterialId = dtoCurtain.MullionMaterialId,
                BorderMullions = dtoCurtain.BorderMullions,
                AutomaticallyEmbed = dtoCurtain.AutomaticallyEmbed
            };

            // A type that could not be built from is put back to the storefront defaults' sizes.
            if (curtain.Problem() is not null)
            {
                var defaults = new CurtainWallType(curtain.Name);
                curtain.VerticalSpacing = defaults.VerticalSpacing;
                curtain.HorizontalSpacing = defaults.HorizontalSpacing;
                curtain.VerticalCount = defaults.VerticalCount;
                curtain.HorizontalCount = defaults.HorizontalCount;
                curtain.PanelThickness = defaults.PanelThickness;
                curtain.MullionWidth = defaults.MullionWidth;
                curtain.MullionDepth = defaults.MullionDepth;
            }

            document.AddType(curtain);
        }

        foreach (var type in dto.DoorTypes)
        {
            var doorType = new DoorType(type.Name, PositiveOr(type.Width, 900), PositiveOr(type.Height, 2100))
            {
                Id = type.Id,
                Operation = ParseEnum(type.Operation, DoorOperation.Swing),
                LeafCount = type.LeafCount > 0 ? type.LeafCount : 1,
                PanelMaterial = type.PanelMaterial,
                HardwareSet = type.HardwareSet,

                // Saved before leaf designs: glass was said in the panel material, panels otherwise.
                LeafDesign = type.LeafDesign is { } design
                    ? ParseEnum(design, DoorLeafDesign.Panelled)
                    : type.PanelMaterial.Contains("glaz", StringComparison.OrdinalIgnoreCase) ? DoorLeafDesign.Glazed : DoorLeafDesign.Panelled,
                GlazingRows = type.GlazingRows is > 0 and <= 12 ? type.GlazingRows : 4,
                GlazingColumns = type.GlazingColumns is > 0 and <= 6 ? type.GlazingColumns : 1,
                Function = ParseEnum(type.Function, DoorFunction.Interior),
                CurtainPanel = type.CurtainPanel,
                TrimWidth = type.TrimWidth is >= 0 and var trim ? trim : 70,
                TrimProjectionExterior = type.TrimProjectionExterior is >= 0 and var outside ? outside : 20,
                TrimProjectionInterior = type.TrimProjectionInterior is >= 0 and var inside ? inside : 20
            };

            ReadOpeningType(doorType, type);
            document.AddType(doorType);
        }

        foreach (var type in dto.WindowTypes)
        {
            var windowType = new WindowType(type.Name, PositiveOr(type.Width, 1200), PositiveOr(type.Height, 1200))
            {
                Id = type.Id,
                Operation = ParseEnum(type.Operation, WindowOperation.Casement),
                GlazingType = type.GlazingType,

                // Saved before divided lights: one pane each way, which is what they all were.
                GlazingRows = type.GlazingRows is > 0 and <= 12 ? type.GlazingRows : 1,
                GlazingColumns = type.GlazingColumns is > 0 and <= 12 ? type.GlazingColumns : 1,
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient
            };

            ReadOpeningType(windowType, type);
            document.AddType(windowType);
        }

        foreach (var wall in dto.Walls)
            document.Add(new Wall
            {
                Id = wall.Id,
                TypeId = wall.TypeId,
                LevelId = wall.LevelId,
                Start = new Point2D(wall.StartX, wall.StartY),
                End = new Point2D(wall.EndX, wall.EndY),
                LocationLine = ParseEnum(wall.LocationLine, WallLocationLine.WallCentreline),
                Flipped = wall.Flipped,
                BaseOffset = wall.BaseOffset,
                TopLevelId = wall.TopLevelId,
                TopOffset = wall.TopOffset,
                UnconnectedHeight = wall.UnconnectedHeight > 0 ? wall.UnconnectedHeight : 3000,
                RoomBounding = wall.RoomBounding,
                StructuralUsage = ParseEnum(wall.StructuralUsage, StructuralUsage.NonBearing),
                Bulge = double.IsFinite(wall.Bulge) ? wall.Bulge : 0,
                Ellipse = ReadEllipse(wall),
                Spline = ReadSpline(wall),
                LockedToJoined = wall.LockedToJoined,
                StartLocked = wall.StartLocked,
                EndLocked = wall.EndLocked,
                StartCleanup = ParseEnum(wall.StartCleanup, WallJoinCleanup.UseViewSetting),
                EndCleanup = ParseEnum(wall.EndCleanup, WallJoinCleanup.UseViewSetting),
                Profile = ReadProfile(wall),
                ProfileLength = ReadProfile(wall) is null ? 0 : wall.ProfileLength,
                CurtainGrid = wall.CurtainVerticals is { } verticals && wall.CurtainHorizontals is { } horizontals
                    ? new CurtainGrid(verticals.Where(double.IsFinite).ToList(), horizontals.Where(double.IsFinite).ToList())
                    : null,
                CurtainGlass = ParseEnum(wall.CurtainGlass, CurtainGlass.Clear),
                CurtainPanels = wall.CurtainPanels?
                    .Where(p => p.Column >= 0 && p.Row >= 0)
                    .Select(p => new CurtainPanelOverride(
                        // Saved when only doors could be panels, the type was called the door type.
                        p.Column, p.Row, ParseEnum(p.Kind, CurtainPanelKind.Glazed), p.OpeningTypeId ?? p.DoorTypeId, ParseEnum(p.Glass, CurtainGlass.Clear),
                        p.FlipHand, p.FlipFacing, p.IsOpen))
                    .ToList(),
                TopAttachedTo = wall.TopAttachedTo,
                BaseAttachedTo = wall.BaseAttachedTo,
                CrossSection = ParseEnum(wall.CrossSection, WallCrossSection.Vertical),
                SlantAngle = Math.Clamp(wall.SlantAngle, -WallLean.MaxAngle, WallLean.MaxAngle),
                AcrossOffset = double.IsFinite(wall.AcrossOffset) ? wall.AcrossOffset : 0,
                UpperSlantAngle = Math.Clamp(wall.UpperSlantAngle, -WallLean.MaxAngle, WallLean.MaxAngle),
                SlantBreakHeight = double.IsFinite(wall.SlantBreakHeight) ? Math.Max(0, wall.SlantBreakHeight) : 0,
                OverrideTaper = wall.OverrideTaper,
                ExteriorTaper = Math.Clamp(wall.ExteriorTaper, -WallLean.MaxAngle, WallLean.MaxAngle),
                InteriorTaper = Math.Clamp(wall.InteriorTaper, -WallLean.MaxAngle, WallLean.MaxAngle),
                StartJoin = ParseEnum(wall.StartJoin, WallJoinKind.Auto),
                EndJoin = ParseEnum(wall.EndJoin, WallJoinKind.Auto),
                Mark = wall.Mark,
                Comments = wall.Comments,
                Workset = wall.Workset,
                PhaseCreated = ParseEnum(wall.PhaseCreated, DesignPhase.New),
                PhaseDemolished = wall.PhaseDemolished is null
                    ? null
                    : ParseEnum(wall.PhaseDemolished, DesignPhase.New)
            });

        // A window used to be something a curtain wall panel could be filled with, which meant
        // it could not be selected, sized or moved on its own. It is a window cut into the wall
        // now, like a window in any other wall, so the ones saved the old way become that -
        // in the middle of the panel they were filling, which is where they were drawn.
        foreach (var saved in dto.Walls.Where(w => w.CurtainPanels is { Count: > 0 }))
        {
            var wall = document.Walls.FirstOrDefault(w => w.Id == saved.Id);
            if (wall is null || CurtainLayout.Of(document, wall) is not { } layout) continue;

            foreach (var panel in saved.CurtainPanels!.Where(p => string.Equals(p.Kind, "Window", StringComparison.OrdinalIgnoreCase)))
            {
                if ((panel.OpeningTypeId ?? panel.DoorTypeId) is not { } typeId) continue;
                if (document.FindType<WindowType>(typeId) is not { } type) continue;
                if (layout.Cells.FirstOrDefault(c => c.Column == panel.Column && c.Row == panel.Row) is not { } cell) continue;

                document.Add(new Window
                {
                    TypeId = type.Id,
                    LevelId = wall.LevelId,
                    HostWallId = wall.Id,
                    DistanceAlongWall = Math.Max(0, (cell.ClearFrom + cell.ClearTo) / 2 + panel.OffsetAlong),
                    SillHeight = Math.Max(0, (cell.ClearBottom + cell.ClearTop) / 2 + panel.OffsetUp - type.Height / 2)
                });
            }

            // The panel goes back to being a pane of glass; the window is its own thing now. It
            // read as plain glass when the file was loaded, since there is no window panel
            // any more, so there is nothing to take out of the wall.
            var made = saved.CurtainPanels!
                .Where(p => string.Equals(p.Kind, "Window", StringComparison.OrdinalIgnoreCase))
                .Select(p => (p.Column, p.Row))
                .ToHashSet();

            wall.CurtainPanels = wall.CurtainPanels?
                .Where(p => !made.Contains((p.Column, p.Row)))
                .ToList() is { Count: > 0 } left
                ? left
                : null;
        }

        // Walls joined face to face, once they are all there to be joined to.
        foreach (var saved in dto.Walls.Where(w => w.JoinedTo is { Count: > 0 }))
        {
            var wall = document.Walls.First(w => w.Id == saved.Id);
            wall.JoinedTo.AddRange(saved.JoinedTo!.Where(id => id != wall.Id && document.Walls.Any(w => w.Id == id)).Distinct());
        }

        // The id has to be set here rather than in ReadOpening: it is init-only, so it can
        // only be given a value while the object is being constructed. Getting this wrong gave
        // every door and window a new identity each time a project was opened, which quietly
        // broke every tag and dimension that referred to one.
        foreach (var door in dto.Doors)
        {
            var element = new Door
            {
                Id = door.Id,
                SwingAngle = door.SwingAngle is > 0 and <= 180 ? door.SwingAngle : 90,
                FrameType = door.FrameType ?? string.Empty,
                FrameMaterial = door.FrameMaterial ?? string.Empty,
                Finish = door.Finish ?? string.Empty
            };

            ReadOpening(element, door);
            document.Add(element);
        }

        foreach (var window in dto.Windows)
        {
            var element = new Window { Id = window.Id };
            ReadOpening(element, window);
            document.Add(element);
        }

        foreach (var type in dto.SlabTypes)
        {
            var structure = new CompoundStructure();
            foreach (var layer in type.Layers)
                if (ReadLayer(layer) is { } read) structure.Add(read);

            SlabType slabType = type.Kind switch
            {
                "Ceiling" => new CeilingType(type.Name, structure) { Id = type.Id },
                "Roof" => new RoofType(type.Name, structure) { Id = type.Id },
                _ => new FloorType(type.Name, structure) { Id = type.Id }
            };

            slabType.TypeMark = type.TypeMark;
            slabType.AssemblyCode = type.AssemblyCode;
            slabType.Keynote = type.Keynote;
            slabType.Manufacturer = type.Manufacturer;
            slabType.Url = type.Url;
            slabType.Description = type.Description;
            slabType.Cost = type.Cost;
            slabType.Function = ParseEnum(type.Function, SlabFunction.Architectural);
            slabType.FireRating = type.FireRating;
            slabType.AcousticRating = type.AcousticRating;
            slabType.HeatTransferCoefficient = type.HeatTransferCoefficient;
            slabType.CoarseScaleFillColour = ParseColour(type.CoarseScaleFillColour, new ColourRgb(0x7A, 0x82, 0x8E));

            document.AddType(slabType);
        }

        foreach (var slab in dto.Slabs)
        {
            Slab element = slab.Kind switch
            {
                "Ceiling" => new Ceiling { Id = slab.Id },
                "Roof" => new Roof { Id = slab.Id },
                _ => new Floor { Id = slab.Id }
            };

            var points = new List<Point2D>();
            for (var i = 0; i + 1 < slab.Boundary.Count; i += 2)
                points.Add(new Point2D(slab.Boundary[i], slab.Boundary[i + 1]));

            element.SetBoundary(points);

            // The edges come after the outline, since it is the outline that says how many
            // there are. A roof from a file that predates pitched roofs has none, and reads
            // back as the flat roof it was drawn as.
            if (element is Roof pitched)
            {
                pitched.CutoffOffset = slab.RoofCutoff;
                pitched.CutoffLevelId = slab.RoofCutoffLevelId;
                pitched.RafterCut = ParseEnum(slab.RoofRafterCut, RafterCut.PlumbCut);
                pitched.Bearing = ParseEnum(slab.RoofBearing, RoofBearing.Truss);
                pitched.JoinedTo = slab.RoofJoinedTo;
                if (slab.RoofDormerOpenings is { } dormers) pitched.DormerOpenings.AddRange(dormers);
                if (slab.RoofDormer is { } made)
                {
                    pitched.Dormer = new DormerSettings(
                        Enum.TryParse<DormerShape>(made.Shape, out var shape) ? shape : DormerShape.Gable,
                        made.Width, made.Height, made.Slope, made.Overhang);
                    pitched.DormerWalls.AddRange(made.Walls);
                }
                if (slab.RoofOpenings is { } holes)
                {
                    pitched.SetOpenings(holes.Select(hole =>
                    {
                        var points = Enumerable.Range(0, hole.Points.Count / 2)
                            .Select(i => new Point2D(hole.Points[2 * i], hole.Points[2 * i + 1]))
                            .ToList();
                        var arcs = Enumerable.Range(0, points.Count)
                            .Select(i => hole.ArcIds is { } ids && i < ids.Count ? ids[i] : null)
                            .ToList();
                        return new RoofOpening(points, arcs);
                    }));
                }
                pitched.FasciaDepth = slab.RoofFasciaDepth;
                if (slab.RoofExtrusion is { } extruded)
                {
                    var profile = new List<Point2D>();
                    for (var i = 0; i + 1 < extruded.Profile.Count; i += 2)
                        profile.Add(new Point2D(extruded.Profile[i], extruded.Profile[i + 1]));

                    pitched.SetExtrusion(new RoofExtrusion(
                        new Point2D(extruded.OriginX, extruded.OriginY),
                        new Vector2D(extruded.DirectionX, extruded.DirectionY),
                        profile, extruded.Start, extruded.End, extruded.Sagittas));
                }

                pitched.SetSlopeArrows(slab.RoofArrows.Select(arrow => new RoofSlopeArrow
                {
                    Tail = new Point2D(arrow.TailX, arrow.TailY), Head = new Point2D(arrow.HeadX, arrow.HeadY),
                    ByHeights = arrow.ByHeights, SlopeDegrees = arrow.SlopeDegrees,
                    TailOffset = arrow.TailOffset, HeadOffset = arrow.HeadOffset
                }));

                if (slab.RoofEdges.Count > 0)
                {
                    pitched.SetEdges(slab.RoofEdges.Select(edge => new RoofEdge
                    {
                        Id = edge.Id ?? Guid.NewGuid(),
                        DefinesSlope = edge.DefinesSlope,
                        SlopeDegrees = edge.SlopeDegrees,
                        PlateOffset = edge.PlateOffset,
                        WallId = edge.WallId,
                        OnLeftOfWall = edge.OnLeftOfWall,
                        Overhang = edge.Overhang,
                        ExtendToCore = edge.ExtendToCore,
                        ArcId = edge.ArcId
                    }));
                }
            }

            element.TypeId = slab.TypeId;
            element.LevelId = slab.LevelId;
            element.HeightOffset = slab.HeightOffset;

            // Before format 3 a roof hung down from its offset, as a floor does; since then it
            // stands on it, as Revit's do, so that a roof based at the top of its walls sits on
            // them. An older roof is lowered by its own thickness so it stays exactly where it
            // was drawn.
            if (element is Roof && dto.FormatVersion < 3 && document.FindType<SlabType>(slab.TypeId) is { } roofType)
                element.HeightOffset -= roofType.Thickness;
            element.Mark = slab.Mark;
            element.Comments = slab.Comments;
            element.Workset = slab.Workset;
            element.PhaseCreated = ParseEnum(slab.PhaseCreated, DesignPhase.New);

            document.Add(element);
        }

        // After the walls they sit on; one whose walls have all gone is dropped.
        var wallIds = document.Walls.Select(w => w.Id).ToHashSet();
        foreach (var saved in dto.WallOpenings.Where(o => wallIds.Contains(o.HostWallId) && o.Width > 0 && o.Height > 0))
            document.Add(new WallOpening
            {
                Id = saved.Id,
                HostWallId = saved.HostWallId,
                LevelId = saved.LevelId,
                DistanceAlongWall = double.IsFinite(saved.DistanceAlongWall) ? Math.Max(0, saved.DistanceAlongWall) : 0,
                Width = saved.Width,
                Height = saved.Height,
                SillHeight = double.IsFinite(saved.SillHeight) ? saved.SillHeight : 0,
                Mark = saved.Mark ?? string.Empty,
                Comments = saved.Comments ?? string.Empty
            });

        foreach (var dtoPlaced in dto.PlacedSweeps)
        {
            var placed = new PlacedSweep
            {
                Id = dtoPlaced.Id,
                TypeId = dtoPlaced.TypeId,
                LevelId = dtoPlaced.LevelId,
                Kind = ParseEnum(dtoPlaced.Kind, SweepKind.Sweep),
                Side = ParseEnum(dtoPlaced.Side, WallSide.Exterior),
                Vertical = dtoPlaced.Vertical,
                Elevation = double.IsFinite(dtoPlaced.Elevation) ? Math.Max(0, dtoPlaced.Elevation) : 0,
                Along = double.IsFinite(dtoPlaced.Along) ? dtoPlaced.Along : 0,
                Offset = double.IsFinite(dtoPlaced.Offset) ? dtoPlaced.Offset : 0,
                Flip = dtoPlaced.Flip,
                ReturnAtStart = dtoPlaced.ReturnAtStart,
                ReturnAtEnd = dtoPlaced.ReturnAtEnd,
                Mark = dtoPlaced.Mark,
                Comments = dtoPlaced.Comments
            };
            placed.HostWallIds.AddRange(dtoPlaced.HostWallIds.Where(wallIds.Contains).Distinct());
            if (placed.HostWallIds.Count > 0) document.Add(placed);
        }

        // A fascia or gutter whose roof is gone has nothing to run along.
        var roofIds = document.Elements.OfType<Roof>().Select(roof => roof.Id).ToHashSet();
        foreach (var saved in dto.RoofEdgeSweeps.Where(saved => roofIds.Contains(saved.RoofId)))
        {
            RoofEdgeSweep sweep = saved.Kind switch
            {
                "Gutter" => new Gutter { Id = saved.Id },
                "Soffit" => new Soffit { Id = saved.Id },
                _ => new Fascia { Id = saved.Id }
            };
            sweep.TypeId = saved.TypeId;
            sweep.LevelId = saved.LevelId;
            sweep.RoofId = saved.RoofId;
            sweep.EdgeIds.AddRange(saved.EdgeIds.Distinct());
            sweep.HorizontalOffset = double.IsFinite(saved.HorizontalOffset) ? saved.HorizontalOffset : 0;
            sweep.VerticalOffset = double.IsFinite(saved.VerticalOffset) ? saved.VerticalOffset : 0;
            sweep.Mark = saved.Mark ?? string.Empty;
            sweep.Comments = saved.Comments ?? string.Empty;
            document.Add(sweep);
        }

        foreach (var type in dto.ColumnTypes)
            document.AddType(new ColumnType(
                type.Name,
                ParseEnum(type.Shape, ColumnShape.Rectangular),
                Size(type.Width, 300), Size(type.Depth, 300))
            {
                Id = type.Id,
                MaterialId = type.MaterialId,
                CoarseScaleFillColour = ParseColour(type.CoarseScaleFillColour, new ColourRgb(0x8A, 0x8F, 0x96)),
                OffsetBase = double.IsFinite(type.OffsetBase) ? type.OffsetBase : 0,
                OffsetTop = double.IsFinite(type.OffsetTop) ? type.OffsetTop : 0,
                CustomProfile = ReadProfile(type.ProfileLoops),
                Shaping = new ColumnShaping
                {
                    TopScale = type.TopScale is > 0 and <= 4 ? type.TopScale : 1,
                    Twist = double.IsFinite(type.Twist) ? type.Twist : 0,
                    SlantAcross = double.IsFinite(type.SlantAcross) ? type.SlantAcross : 0,
                    SlantAlong = double.IsFinite(type.SlantAlong) ? type.SlantAlong : 0,
                    Flutes = Math.Clamp(type.Flutes, 0, 96),
                    FluteDepth = type.FluteDepth >= 0 ? type.FluteDepth : 25,
                    BaseHeight = Math.Max(0, type.BaseHeight),
                    BaseSpread = type.BaseSpread >= 0 ? type.BaseSpread : 0.18,
                    CapitalHeight = Math.Max(0, type.CapitalHeight),
                    CapitalSpread = type.CapitalSpread >= 0 ? type.CapitalSpread : 0.22
                },
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost
            });

        foreach (var column in dto.Columns)
        {
            if (document.FindType<ColumnType>(column.TypeId) is null) continue;

            // An attachment to something that is no longer there is dropped, and the column
            // falls back on its levels - the same rule a wall's attachment follows.
            var slabs = document.Elements.OfType<Slab>().Select(slab => slab.Id).ToHashSet();

            document.Add(new Column
            {
                Id = column.Id,
                TypeId = column.TypeId,
                LevelId = column.LevelId,
                Location = new Point2D(column.LocationX, column.LocationY),
                Rotation = double.IsFinite(column.Rotation) ? column.Rotation : 0,
                BaseOffset = double.IsFinite(column.BaseOffset) ? column.BaseOffset : 0,
                TopLevelId = column.TopLevelId is { } topId && document.FindLevel(topId) is not null ? topId : null,
                TopOffset = double.IsFinite(column.TopOffset) ? column.TopOffset : 0,
                UnconnectedHeight = Size(column.UnconnectedHeight, 3000),
                TopAttachedTo = column.TopAttachedTo is { } top && slabs.Contains(top) ? top : null,
                BaseAttachedTo = column.BaseAttachedTo is { } bottom && slabs.Contains(bottom) ? bottom : null,
                MovesWithGrids = column.MovesWithGrids,
                RoomBounding = column.RoomBounding,
                CutByWalls = column.CutByWalls,
                Placement = ParseEnum(column.Placement, ColumnPlacement.Freestanding),
                PlacementFace = ParseEnum(column.PlacementFace, ColumnJoins.Face.Interior),
                TopAttachmentStyle = ParseEnum(column.TopAttachmentStyle, ColumnAttachmentStyle.CutColumn),
                BaseAttachmentStyle = ParseEnum(column.BaseAttachmentStyle, ColumnAttachmentStyle.CutColumn),
                OffsetFromAttachmentAtTop = double.IsFinite(column.OffsetFromAttachmentAtTop) ? column.OffsetFromAttachmentAtTop : 0,
                OffsetFromAttachmentAtBase = double.IsFinite(column.OffsetFromAttachmentAtBase) ? column.OffsetFromAttachmentAtBase : 0,
                Mark = column.Mark,
                Comments = column.Comments,
                Workset = column.Workset,
                PhaseCreated = ParseEnum(column.PhaseCreated, DesignPhase.New),
                PhaseDemolished = string.IsNullOrEmpty(column.PhaseDemolished)
                    ? null
                    : ParseEnum(column.PhaseDemolished, DesignPhase.Existing)
            });
        }

        foreach (var type in dto.ComponentTypes)
            document.AddType(new ComponentType(
                type.Name,
                ParseEnum(type.Category, BuiltInCategory.Furniture),
                ParseEnum(type.Form, ComponentForm.Box),
                Size(type.Width, 600), Size(type.Depth, 600), Size(type.Height, 600))
            {
                Id = type.Id,
                Placement = ParseEnum(type.Placement, ComponentPlacement.Freestanding),
                DefaultElevation = Math.Max(0, type.DefaultElevation),
                MaterialId = type.MaterialId,
                Colour = ParseColour(type.Colour, new ColourRgb(0xB4, 0x9A, 0x74)),
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost
            });

        foreach (var component in dto.Components)
        {
            // A component whose family went with the project it came from has nothing to be.
            if (document.FindType<ComponentType>(component.TypeId) is not { } family) continue;

            // Saved when only a wall could carry one, the host was called the host wall.
            var saved = component.HostId != Guid.Empty ? component.HostId : component.HostWallId;

            // One fixed to a face that is no longer there stands on its level instead, rather
            // than being dropped: what was placed in the model stays in the model.
            var host = document.Elements.Any(element => element.Id == saved && element is Wall or Slab)
                ? saved
                : Guid.Empty;

            document.Add(new Component
            {
                Id = component.Id,
                TypeId = component.TypeId,
                TypeKind = family.Kind,
                LevelId = component.LevelId,
                Location = new Point2D(component.LocationX, component.LocationY),
                Rotation = double.IsFinite(component.Rotation) ? component.Rotation : 0,
                Elevation = double.IsFinite(component.Elevation) ? component.Elevation : 0,
                HostId = host,
                FlipFacing = component.FlipFacing,
                Mark = component.Mark,
                Comments = component.Comments,
                Workset = component.Workset,
                PhaseCreated = ParseEnum(component.PhaseCreated, DesignPhase.New),
                PhaseDemolished = string.IsNullOrEmpty(component.PhaseDemolished)
                    ? null
                    : ParseEnum(component.PhaseDemolished, DesignPhase.Existing)
            });
        }

        foreach (var room in dto.Rooms)
            document.Add(new Room
            {
                Id = room.Id,
                LevelId = room.LevelId,
                Location = new Point2D(room.LocationX, room.LocationY),
                Name = room.Name,
                Number = room.Number,
                Department = room.Department,
                Occupancy = room.Occupancy,
                OccupantCount = Math.Max(0, room.OccupantCount),
                BaseOffset = room.BaseOffset,
                UpperLimitOffset = room.UpperLimitOffset > room.BaseOffset
                    ? room.UpperLimitOffset
                    : room.BaseOffset + 3000,
                FloorFinish = room.FloorFinish,
                WallFinish = room.WallFinish,
                CeilingFinish = room.CeilingFinish,
                BaseFinish = room.BaseFinish,
                Comments = room.Comments,
                Workset = room.Workset,
                PhaseCreated = ParseEnum(room.PhaseCreated, DesignPhase.New)
            });

        foreach (var grid in dto.Grids)
            document.Add(new Grid
            {
                Id = grid.Id,
                Name = grid.Name,
                Start = new Point2D(grid.StartX, grid.StartY),
                End = new Point2D(grid.EndX, grid.EndY),
                BubbleAtStart = grid.BubbleAtStart,
                BubbleAtEnd = grid.BubbleAtEnd,
                Comments = grid.Comments,
                Workset = grid.Workset
            });

        foreach (var dimension in dto.Dimensions)
            document.Add(new Dimension
            {
                Id = dimension.Id,
                LevelId = dimension.LevelId,
                Start = ReadEnd(dimension.Start),
                End = ReadEnd(dimension.End),
                Offset = dimension.Offset,
                Override = dimension.Override,
                Comments = dimension.Comments,
                Workset = dimension.Workset
            });

        foreach (var tag in dto.Tags)
            document.Add(new Tag
            {
                Id = tag.Id,
                LevelId = tag.LevelId,
                TargetId = tag.TargetId,
                Field = string.IsNullOrWhiteSpace(tag.Field) ? "Mark" : tag.Field,
                Position = new Point2D(tag.X, tag.Y),
                ShowLeader = tag.ShowLeader,
                Comments = tag.Comments,
                Workset = tag.Workset
            });

        foreach (var note in dto.TextNotes)
            document.Add(new TextNote
            {
                Id = note.Id,
                LevelId = note.LevelId,
                Text = note.Text,
                Position = new Point2D(note.X, note.Y),
                LeaderEnd = note.LeaderX is { } lx && note.LeaderY is { } ly
                    ? new Point2D(lx, ly)
                    : null,
                Comments = note.Comments,
                Workset = note.Workset
            });

        foreach (var section in dto.Sections)
            document.Add(new SectionMarker
            {
                Id = section.Id,
                LevelId = section.LevelId,
                Name = string.IsNullOrWhiteSpace(section.Name) ? "A" : section.Name,
                Start = new Point2D(section.StartX, section.StartY),
                End = new Point2D(section.EndX, section.EndY),
                Flipped = section.Flipped,
                ViewDepth = section.ViewDepth > 0 ? section.ViewDepth : 20000,
                Comments = section.Comments,
                Workset = section.Workset
            });

        foreach (var dtoSheet in dto.Sheets)
        {
            var sheet = new Sheet
            {
                Id = dtoSheet.Id,
                Number = dtoSheet.Number,
                Name = dtoSheet.Name,
                PaperSize = ParseEnum(dtoSheet.PaperSize, PaperSize.A1),
                Orientation = ParseEnum(dtoSheet.Orientation, PaperOrientation.Landscape),
                DrawnBy = dtoSheet.DrawnBy,
                CheckedBy = dtoSheet.CheckedBy,
                Revision = dtoSheet.Revision,
                IssuedOn = dtoSheet.IssuedOn,
                Comments = dtoSheet.Comments,
                Workset = dtoSheet.Workset
            };

            foreach (var dtoViewport in dtoSheet.Viewports)
            {
                var kind = ParseEnum(dtoViewport.Kind, ViewKind.FloorPlan);

                sheet.Add(new Viewport
                {
                    Id = dtoViewport.Id,
                    View = new ViewReference(kind, dtoViewport.TargetId, dtoViewport.ScheduleName),
                    Centre = new Point2D(dtoViewport.X, dtoViewport.Y),
                    Scale = new ViewScale(dtoViewport.Scale > 0 ? dtoViewport.Scale : 100),
                    ShowTitle = dtoViewport.ShowTitle,
                    TitleOverride = dtoViewport.TitleOverride
                });
            }

            document.Add(sheet);
        }

        foreach (var settings in dto.ViewSettings)
        {
            var view = new ViewReference(ParseEnum(settings.Kind, ViewKind.FloorPlan), settings.TargetId, string.Empty);

            foreach (var name in settings.HiddenWallFunctions)
                if (Enum.TryParse<WallFunction>(name, out var function))
                    document.ViewSettings.SetWallFunctionVisible(view, function, visible: false);

            if (settings.Scale is { } scale) document.ViewSettings.SetScale(view, scale);
            if (settings.JoinDisplay is { } display) document.ViewSettings.SetJoinDisplay(view, ParseEnum(display, WallJoinDisplay.CleanAllWallJoins));
        }

        // A project written before a category existed carries no types for it. Fill those
        // gaps so every tool in the build can actually be used on an older file.
        document.EnsureDefaultTypes();

        return document;
    }

    /// <summary>An elliptical wall's shape, or null when there is none or it could not describe a piece of ellipse.</summary>
    private static WallEllipse? ReadEllipse(WallDto wall) =>
        wall is { EllipseRatio: { } ratio, EllipseFrom: { } from, EllipseTo: { } to } &&
        new WallEllipse(ratio, from, to) is { IsValid: true } ellipse
            ? ellipse
            : null;

    /// <summary>A spline wall's shape, or null when there is none or it could not describe a curve.</summary>
    private static WallSpline? ReadSpline(WallDto wall)
    {
        if (wall.SplinePoints is not { Count: >= 2 } values || values.Count % 2 != 0) return null;

        var points = Enumerable.Range(0, values.Count / 2).Select(i => new Point2D(values[2 * i], values[2 * i + 1]));
        var spline = new WallSpline(points, wall.SplineFrom, wall.SplineTo);
        return spline.IsValid ? spline : null;
    }

    /// <summary>A wall's edited profile, or null when there is none or it could not be one.</summary>
    private static IReadOnlyList<Point2D>? ReadProfile(WallDto wall)
    {
        if (wall.Profile is not { Count: >= 6 } values || values.Count % 2 != 0 ||
            values.Any(v => !double.IsFinite(v)) || !(wall.ProfileLength > 0))
            return null;

        var points = new List<Point2D>();
        for (var i = 0; i + 1 < values.Count; i += 2) points.Add(new Point2D(values[i], values[i + 1]));
        return WallProfile.Problem(points, wall.ProfileLength) is null ? points : null;
    }

    /// <summary>
    /// One layer of a build-up, or null if it cannot be one.
    ///
    /// Earlier files named finishes by side ("FinishExterior") and could give a membrane a
    /// thickness. The names are read as the finish they were used for, and a membrane with
    /// substance is kept as the finish it really was, so no wall changes size on opening.
    /// </summary>
    private static MaterialLayer? ReadLayer(MaterialLayerDto dto)
    {
        var function = LayerFunctions.TryParse(dto.Function, out var parsed) ? parsed : LayerFunction.Structure;

        if (function == LayerFunction.Membrane)
        {
            if (dto.Thickness > 0) function = LayerFunction.Finish1;
            else return new MaterialLayer(LayerFunction.Membrane, dto.MaterialId, 0, dto.Wraps);
        }

        // A zero-thickness layer that is not a membrane is not a layer.
        return dto.Thickness > 0
            ? new MaterialLayer(function, dto.MaterialId, dto.Thickness, dto.Wraps)
            : null;
    }

    private static void ReadOpeningType(OpeningType type, OpeningTypeDto dto)
    {
        type.TypeMark = dto.TypeMark;
        type.AssemblyCode = dto.AssemblyCode;
        type.Keynote = dto.Keynote;
        type.Manufacturer = dto.Manufacturer;
        type.Url = dto.Url;
        type.Description = dto.Description;
        type.Cost = dto.Cost;
        type.Thickness = dto.Thickness;
        type.FrameMaterial = dto.FrameMaterial;
        type.FireRating = dto.FireRating;
        type.AcousticRating = dto.AcousticRating;
        type.HeatTransferCoefficient = dto.HeatTransferCoefficient;
    }

    private static void ReadOpening(Opening opening, OpeningDto dto)
    {
        opening.TypeId = dto.TypeId;
        opening.LevelId = dto.LevelId;
        opening.HostWallId = dto.HostWallId;
        opening.DistanceAlongWall = Math.Max(0, dto.DistanceAlongWall);
        opening.SillHeight = dto.SillHeight;
        opening.FlipFacing = dto.FlipFacing;
        opening.FlipHand = dto.FlipHand;
        opening.Orientation = ParseEnum(dto.Orientation, OpeningOrientation.Vertical);
        opening.IsOpen = dto.IsOpen;
        opening.WidthOverride = dto.WidthOverride is > 0 and var width ? width : null;
        opening.HeightOverride = dto.HeightOverride is > 0 and var height ? height : null;
        opening.Mark = dto.Mark;
        opening.Comments = dto.Comments;
        opening.Workset = dto.Workset;
        opening.PhaseCreated = ParseEnum(dto.PhaseCreated, DesignPhase.New);
        opening.PhaseDemolished = dto.PhaseDemolished is null
            ? null
            : ParseEnum(dto.PhaseDemolished, DesignPhase.New);
    }

    /// <summary>A door with no width is not a door; fall back rather than throw on load.</summary>
    private static double PositiveOr(double value, double fallback) => value > 0 ? value : fallback;

    /// <summary>Unknown enum text falls back rather than failing the whole load.</summary>
    private static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) ? value : fallback;

    /// <summary>
    /// A drawn column section read back from its loops: the outline first, then its holes. A
    /// loop with too few points to be a shape is dropped rather than left to break the geometry.
    /// </summary>
    private static ColumnProfile? ReadProfile(List<List<double>>? loops)
    {
        if (loops is null || loops.Count == 0) return null;

        var rings = loops
            .Select(loop => loop
                .Where(double.IsFinite)
                .Chunk(2)
                .Where(pair => pair.Length == 2)
                .Select(pair => new Point2D(pair[0], pair[1]))
                .ToList())
            .Where(ring => ring.Count >= 3)
            .ToList();

        if (rings.Count == 0) return null;
        return new ColumnProfile(rings[0], rings.Skip(1).Cast<IReadOnlyList<Point2D>>().ToList());
    }

    /// <summary>A saved dimension, or a workable one where the file had none or nonsense.</summary>
    private static double Size(double saved, double fallback) =>
        double.IsFinite(saved) && saved > 0 ? saved : fallback;

    private static ColourRgb ParseColour(string? text, ColourRgb fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? fallback : ColourRgb.FromHex(text);
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            return fallback;
        }
    }
}
