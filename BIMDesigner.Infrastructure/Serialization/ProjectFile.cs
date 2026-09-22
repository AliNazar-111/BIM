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
    public const int CurrentFormatVersion = 1;

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
                HardwareSet = type.HardwareSet
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
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient
            };

            WriteOpeningType(windowType, type);
            dto.WindowTypes.Add(windowType);
        }

        foreach (var door in document.Elements.OfType<Door>())
        {
            var doorDto = new DoorDto { SwingAngle = door.SwingAngle };
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
                HiddenWallFunctions = hidden.Select(function => function.ToString()).OrderBy(name => name).ToList()
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
                Mark = slab.Mark,
                Comments = slab.Comments,
                Workset = slab.Workset,
                PhaseCreated = slab.PhaseCreated.ToString()
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
                SplineTo = wall.Spline?.To,
                Profile = wall.Profile?.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                ProfileLength = wall.ProfileLength,
                CurtainVerticals = wall.CurtainGrid?.Verticals.ToList(),
                CurtainHorizontals = wall.CurtainGrid?.Horizontals.ToList(),
                CurtainPanels = wall.CurtainPanels?.Select(p => new CurtainPanelDto { Column = p.Column, Row = p.Row, Kind = p.Kind.ToString() }).ToList(),
                TopAttachedTo = wall.TopAttachedTo,
                BaseAttachedTo = wall.BaseAttachedTo,
                CrossSection = wall.CrossSection.ToString(),
                SlantAngle = wall.SlantAngle,
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
                WrapAtEnds = string.IsNullOrEmpty(type.WrappingAtEnds)
                    ? type.WrapAtEnds ? WallWrapping.Exterior : WallWrapping.None
                    : ParseEnum(type.WrappingAtEnds, WallWrapping.None),
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
                HardwareSet = type.HardwareSet
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
                Profile = ReadProfile(wall),
                ProfileLength = ReadProfile(wall) is null ? 0 : wall.ProfileLength,
                CurtainGrid = wall.CurtainVerticals is { } verticals && wall.CurtainHorizontals is { } horizontals
                    ? new CurtainGrid(verticals.Where(double.IsFinite).ToList(), horizontals.Where(double.IsFinite).ToList())
                    : null,
                CurtainPanels = wall.CurtainPanels?
                    .Where(p => p.Column >= 0 && p.Row >= 0)
                    .Select(p => new CurtainPanelOverride(p.Column, p.Row, ParseEnum(p.Kind, CurtainPanelKind.Glazed)))
                    .ToList(),
                TopAttachedTo = wall.TopAttachedTo,
                BaseAttachedTo = wall.BaseAttachedTo,
                CrossSection = ParseEnum(wall.CrossSection, WallCrossSection.Vertical),
                SlantAngle = Math.Clamp(wall.SlantAngle, -WallLean.MaxAngle, WallLean.MaxAngle),
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

        // The id has to be set here rather than in ReadOpening: it is init-only, so it can
        // only be given a value while the object is being constructed. Getting this wrong gave
        // every door and window a new identity each time a project was opened, which quietly
        // broke every tag and dimension that referred to one.
        foreach (var door in dto.Doors)
        {
            var element = new Door
            {
                Id = door.Id,
                SwingAngle = door.SwingAngle is > 0 and <= 180 ? door.SwingAngle : 90
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
            element.TypeId = slab.TypeId;
            element.LevelId = slab.LevelId;
            element.HeightOffset = slab.HeightOffset;
            element.Mark = slab.Mark;
            element.Comments = slab.Comments;
            element.Workset = slab.Workset;
            element.PhaseCreated = ParseEnum(slab.PhaseCreated, DesignPhase.New);

            document.Add(element);
        }

        // After the walls they sit on; one whose walls have all gone is dropped.
        var wallIds = document.Walls.Select(w => w.Id).ToHashSet();
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
