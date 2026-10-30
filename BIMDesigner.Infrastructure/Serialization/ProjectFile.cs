using System.Text.Json;
using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Datums;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Energy;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Mep;
using BIMDesigner.Core.Rendering;
using BIMDesigner.Core.Schedules;
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
public static partial class ProjectFile
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
        Interoperability.LinkFiles.Relate(document, path);
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
                BuildingType = document.ProjectInformation.BuildingType,
                ClearanceStandard = document.ProjectInformation.ClearanceStandard.ToString(),
                Units = document.ProjectInformation.Units.ToString(),
                Sprinklered = document.ProjectInformation.Sprinklered,
                WaterOptionalRequirement = document.ProjectInformation.WaterOptionalRequirement,
                Ground = document.ProjectInformation.Ground.ToString(),
                ClayShrinkage = document.ProjectInformation.ClayShrinkage.ToString(),
                FrostDepth = document.ProjectInformation.FrostDepth,
                GroundSnowLoad = document.ProjectInformation.GroundSnowLoad,
                WaterPressure = document.ProjectInformation.WaterPressure,
                WindPressure = document.ProjectInformation.WindPressure,
                BasicWindSpeed = document.ProjectInformation.BasicWindSpeed,
                SiteAltitude = document.ProjectInformation.SiteAltitude,
                Terrain = document.ProjectInformation.Terrain.ToString(),
                HeatingLocation = document.ProjectInformation.HeatingLocation.ToString(),
                WinterDesignTemperature = document.ProjectInformation.WinterDesignTemperature,
                AirPermeability = document.ProjectInformation.AirPermeability,
                SummerDesignTemperature = document.ProjectInformation.SummerDesignTemperature,
                SummerWetBulb = document.ProjectInformation.SummerWetBulb,
                DailyRange = document.ProjectInformation.DailyRange,
                Latitude = document.ProjectInformation.Latitude,
                TrueNorth = document.ProjectInformation.TrueNorth,
                PlanningMapNote = document.ProjectInformation.PlanningMapNote.Length == 0 ? null : document.ProjectInformation.PlanningMapNote,
                SurveyOffsetX = document.ProjectInformation.SurveyOffset.X,
                SurveyOffsetY = document.ProjectInformation.SurveyOffset.Y,
                DuctRun = document.ProjectInformation.DuctRun.ToString(),
                DuctInsulation = document.ProjectInformation.DuctInsulation,
                DuctsSealed = document.ProjectInformation.DuctsSealed,
                Energy = EnergyOf(document.ProjectInformation.Energy),
                Render = RenderOf(document.ProjectInformation.Render),
                Daylight = DaylightOf(document.ProjectInformation.Daylight)
            }
        };

        foreach (var level in document.Levels)
            dto.Levels.Add(new LevelDto { Id = level.Id, Name = level.Name, Elevation = level.Elevation, StandsOn = level.StandsOn });

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
                CostPerCubicMetre = material.CostPerCubicMetre,
                Keynote = NullIfEmpty(material.Keynote),
                Pattern = material.Pattern.ToString(),
                UnitLength = material.UnitLength,
                UnitHeight = material.UnitHeight,
                JointWidth = material.JointWidth,
                JointColour = material.JointColour.ToString(),
                Reflectance = material.Reflectance
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

        foreach (var style in document.TypesOf<DimensionType>())
            dto.DimensionTypes.Add(new DimensionTypeDto
            {
                Id = style.Id, Name = style.Name, Tick = style.Tick.ToString(), TextSize = style.TextSize, TextOffset = style.TextOffset,
                TextAlongLine = style.TextAlongLine, WitnessGap = style.WitnessGap, WitnessExtension = style.WitnessExtension,
                LineExtension = style.LineExtension, Units = style.Units.ToString(), Decimals = style.Decimals, ShowUnits = style.ShowUnits,
                AngleDecimals = style.AngleDecimals
            });

        foreach (var type in document.ElementTypes.OrderBy(type => type.Id))
            dto.TypeIdentities.Add(new TypeIdentityDto
            {
                Id = type.Id, TypeMark = NullIfEmpty(type.TypeMark), AssemblyCode = NullIfEmpty(type.AssemblyCode), Keynote = NullIfEmpty(type.Keynote),
                Manufacturer = NullIfEmpty(type.Manufacturer), Url = NullIfEmpty(type.Url), Description = NullIfEmpty(type.Description), Cost = type.Cost,
                ClassificationCode = NullIfEmpty(type.ClassificationCode)
            });

        foreach (var fascia in document.TypesOf<FasciaType>())
            dto.FasciaTypes.Add(new FasciaTypeDto
            {
                Id = fascia.Id, Name = fascia.Name, TypeMark = fascia.TypeMark, Thickness = fascia.Thickness, Depth = fascia.Depth,
                MaterialId = fascia.MaterialId, Cost = fascia.Cost, Description = fascia.Description, Profile = fascia.Profile.ToString()
            });

        foreach (var gutter in document.TypesOf<GutterType>())
            dto.GutterTypes.Add(new GutterTypeDto
            {
                Id = gutter.Id, Name = gutter.Name, TypeMark = gutter.TypeMark, Shape = gutter.Shape.ToString(), Width = gutter.Width,
                Depth = gutter.Depth, WallThickness = gutter.WallThickness, MaterialId = gutter.MaterialId, Cost = gutter.Cost,
                Description = gutter.Description, HangerSpacing = gutter.HangerSpacing, LeafGuard = gutter.LeafGuard,
                DownpipeShape = gutter.DownpipeShape.ToString(), DownpipeWidth = gutter.DownpipeWidth, DownpipeDepth = gutter.DownpipeDepth
            });

        foreach (var edgeType in document.TypesOf<SlabEdgeType>())
            dto.SlabEdgeTypes.Add(new SlabEdgeTypeDto
            {
                Id = edgeType.Id, Name = edgeType.Name, TypeMark = edgeType.TypeMark, Shape = edgeType.Shape.ToString(), Width = edgeType.Width,
                Depth = edgeType.Depth, FootWidth = edgeType.FootWidth, MaterialId = edgeType.MaterialId, Cost = edgeType.Cost, Description = edgeType.Description
            });

        foreach (var slabEdge in document.All<SlabEdge>())
            dto.SlabEdges.Add(new SlabEdgeDto
            {
                Id = slabEdge.Id, TypeId = slabEdge.TypeId, FloorId = slabEdge.FloorId, LevelId = slabEdge.LevelId,
                Edges = slabEdge.Edges.SelectMany(pair => new[] { pair.From.X, pair.From.Y, pair.To.X, pair.To.Y }).ToList(),
                HorizontalOffset = slabEdge.HorizontalOffset, VerticalOffset = slabEdge.VerticalOffset, Mark = slabEdge.Mark, Comments = slabEdge.Comments
            });

        foreach (var drain in document.All<RoofDrain>())
            dto.RoofDrains.Add(new RoofDrainDto
            {
                Id = drain.Id, RoofId = drain.RoofId, LevelId = drain.LevelId, X = drain.Location.X, Y = drain.Location.Y,
                OutletDiameter = drain.OutletDiameter, Discharge = drain.Discharge.ToString(), Mark = drain.Mark, Comments = drain.Comments
            });

        foreach (var shaped in document.All<PolygonWall>())
            dto.PolygonWalls.Add(new PolygonWallDto
            {
                Id = shaped.Id, LevelId = shaped.LevelId, Kind = shaped.Kind.ToString(),
                Outline = shaped.Outline.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                StartX = shaped.Start.X, StartY = shaped.Start.Y, EndX = shaped.End.X, EndY = shaped.End.Y,
                StartThickness = shaped.StartThickness, EndThickness = shaped.EndThickness, StraightSide = shaped.StraightSide.ToString(),
                MaterialId = shaped.MaterialId, BaseOffset = shaped.BaseOffset, TopLevelId = shaped.TopLevelId, TopOffset = shaped.TopOffset,
                UnconnectedHeight = shaped.UnconnectedHeight, BaseAttachedTo = shaped.BaseAttachedTo, Mark = shaped.Mark, Comments = shaped.Comments
            });

        foreach (var framing in document.All<WallFraming>())
            dto.WallFramings.Add(new WallFramingDto
            {
                Id = framing.Id, HostId = framing.HostId, LevelId = framing.LevelId, Material = framing.Material.ToString(),
                Section = framing.Section, Spacing = framing.Spacing, TopPlates = framing.TopPlates, NoggingRows = framing.NoggingRows,
                FromEnd = framing.FromEnd, Mark = framing.Mark, Comments = framing.Comments
            });

        foreach (var part in document.All<Part>())
            dto.Parts.Add(new PartDto
            {
                Id = part.Id, HostId = part.HostId, LevelId = part.LevelId, Layer = part.Layer, From = part.From, To = part.To,
                Bottom = part.Bottom, Top = part.Top, Gap = part.Gap, MaterialId = part.MaterialId, Excluded = part.Excluded,
                Mark = part.Mark, Comments = part.Comments
            });

        foreach (var chimney in document.All<Chimney>())
            dto.Chimneys.Add(new ChimneyDto
            {
                Id = chimney.Id, TypeId = chimney.TypeId, LevelId = chimney.LevelId, X = chimney.Location.X, Y = chimney.Location.Y, Width = chimney.Width,
                Depth = chimney.Depth, Angle = chimney.Angle, BaseOffset = chimney.BaseOffset, Flues = chimney.Flues,
                Rule = chimney.Rule.ToString(), ExtraHeight = chimney.ExtraHeight, Fireplace = chimney.Fireplace.ToString(),
                FireplacesAbove = chimney.FireplacesAbove.Select(pair => new ChimneyFireplaceDto { LevelId = pair.Key, Fireplace = pair.Value.ToString() }).ToList(),
                Mark = chimney.Mark, Comments = chimney.Comments
            });

        foreach (var type in document.TypesOf<ChimneyType>())
            dto.ChimneyTypes.Add(new ChimneyTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Construction = type.Construction.ToString(),
                Width = type.Width, Depth = type.Depth, FlueDiameter = type.FlueDiameter, Flues = type.Flues, MinimumHeight = type.MinimumHeight,
                Fireplace = type.Fireplace.ToString(), TemperatureClass = type.TemperatureClass, PressureClass = type.PressureClass,
                Wet = type.Wet, CorrosionClass = type.CorrosionClass, SootFireResistant = type.SootFireResistant,
                ClearanceToCombustibles = type.ClearanceToCombustibles, FireRating = type.FireRating, Cost = type.Cost, Description = type.Description
            });

        foreach (var type in document.TypesOf<StairType>())
            dto.StairTypes.Add(new StairTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Construction = type.Construction.ToString(),
                MaxRiser = type.MaxRiser, Going = type.Going, Nosing = type.Nosing, TreadThickness = type.TreadThickness,
                OpenRisers = type.OpenRisers, RiserBars = type.RiserBars, Support = type.Support.ToString(), Balustrade = type.Balustrade.ToString(),
                HandrailProfile = type.HandrailProfile.ToString(), HandrailWidth = type.HandrailWidth, HandrailDepth = type.HandrailDepth,
                WallClearance = type.WallClearance, HandrailExtension = type.HandrailExtension, HandrailEnds = type.HandrailEnds.ToString(),
                GuardHeight = type.GuardHeight, LowerRail = type.LowerRail, NosingProfile = type.NosingProfile.ToString(),
                RiserMaterialId = type.RiserMaterialId, StringMaterialId = type.StringMaterialId, RailMaterialId = type.RailMaterialId, BalusterMaterialId = type.BalusterMaterialId,
                HandrailHeight = type.HandrailHeight, BalusterSpacing = type.BalusterSpacing,
                Colour = type.Colour.ToString(), MaterialId = type.MaterialId, Cost = type.Cost, Description = type.Description
            });

        foreach (var stair in document.All<Stair>())
            dto.Stairs.Add(new StairDto
            {
                Id = stair.Id, TypeId = stair.TypeId, LevelId = stair.LevelId, X = stair.Start.X, Y = stair.Start.Y, Angle = stair.Angle,
                Shape = stair.Shape.ToString(), Corner = stair.Corner.ToString(), TurnsLeft = stair.TurnsLeft, Width = stair.Width,
                RisersBeforeTurn = stair.RisersBeforeTurn, Well = stair.Well, InnerRadius = stair.InnerRadius, BaseOffset = stair.BaseOffset, TopLevelId = stair.TopLevelId,
                PathLengths = stair.PathLengths.Count > 0 ? stair.PathLengths.ToList() : null, PathTurns = stair.PathTurns.Count > 0 ? stair.PathTurns.ToList() : null,
                RepeatToLevelId = stair.RepeatToLevelId, ShowRiserNumbers = stair.ShowRiserNumbers, ShowFlightDimensions = stair.ShowFlightDimensions,
                Use = stair.Use.ToString(), LoftConversion = stair.LoftConversion, Outside = stair.Outside, BottomStep = stair.BottomStep.ToString(),
                TopOffset = stair.TopOffset, UnconnectedHeight = stair.UnconnectedHeight, Railings = stair.Railings.ToString(),
                CutsFloorAbove = stair.CutsFloorAbove, CutsRoof = stair.CutsRoof, Mark = stair.Mark, Comments = stair.Comments
            });

        foreach (var type in document.TypesOf<RampType>())
            dto.RampTypes.Add(new RampTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Construction = type.Construction.ToString(), Thickness = type.Thickness,
                KerbHeight = type.KerbHeight, KerbWidth = type.KerbWidth, HandrailHeight = type.HandrailHeight, HandrailDiameter = type.HandrailDiameter,
                HandrailExtension = type.HandrailExtension, LowerRail = type.LowerRail, Guarding = type.Guarding.ToString(), PostSpacing = type.PostSpacing,
                LongestFlight = type.LongestFlight,
                Colour = type.Colour.ToString(), MaterialId = type.MaterialId, RailMaterialId = type.RailMaterialId, Cost = type.Cost, Description = type.Description
            });

        foreach (var ramp in document.All<Ramp>())
            dto.Ramps.Add(new RampDto
            {
                Id = ramp.Id, TypeId = ramp.TypeId, LevelId = ramp.LevelId, X = ramp.Start.X, Y = ramp.Start.Y, Angle = ramp.Angle,
                Anchor = ramp.Anchor.ToString(), Shape = ramp.Shape.ToString(), TurnsLeft = ramp.TurnsLeft, Width = ramp.Width, Gap = ramp.Gap,
                PathLengths = ramp.PathLengths.Count > 0 ? ramp.PathLengths.ToList() : null, PathTurns = ramp.PathTurns.Count > 0 ? ramp.PathTurns.ToList() : null,
                Gradient = ramp.Gradient, BaseOffset = ramp.BaseOffset, TopLevelId = ramp.TopLevelId, TopOffset = ramp.TopOffset,
                UnconnectedHeight = ramp.UnconnectedHeight, TopLanding = ramp.TopLanding, BottomLanding = ramp.BottomLanding,
                Building = ramp.Building.ToString(), Outside = ramp.Outside, Railings = ramp.Railings.ToString(), ClosedUnderneath = ramp.ClosedUnderneath,
                Mark = ramp.Mark, Comments = ramp.Comments
            });

        foreach (var type in document.TypesOf<RailingType>())
            dto.RailingTypes.Add(new RailingTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Height = type.Height, Infill = type.Infill.ToString(),
                TopRailProfile = type.TopRailProfile.ToString(), TopRailWidth = type.TopRailWidth, TopRailDepth = type.TopRailDepth,
                PostSize = type.PostSize, PostSpacing = type.PostSpacing, BalusterSize = type.BalusterSize, BalusterSpacing = type.BalusterSpacing,
                BottomGap = type.BottomGap, GlassThickness = type.GlassThickness, Colour = type.Colour.ToString(),
                MaterialId = type.MaterialId, InfillMaterialId = type.InfillMaterialId, Cost = type.Cost, Description = type.Description,
                Use = type.Use.ToString(), HasTopRail = type.HasTopRail, GlassLaminated = type.GlassLaminated, GlassJoint = type.GlassJoint,
                ChannelHeight = type.ChannelHeight, PanelThickness = type.PanelThickness, IntermediateRails = type.IntermediateRails,
                Handrail = type.Handrail.ToString(), HandrailHeight = type.HandrailHeight, HandrailDiameter = type.HandrailDiameter,
                HandrailClearance = type.HandrailClearance, HandrailExtension = type.HandrailExtension, HandrailEnds = type.HandrailEnds.ToString(),
                LineLoad = type.LineLoad, PointLoad = type.PointLoad
            });

        foreach (var ground in document.All<Toposurface>())
            dto.Toposurfaces.Add(new ToposurfaceDto
            {
                Id = ground.Id, LevelId = ground.LevelId, Points = ground.Points.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToList(),
                ExistingPoints = ground.ExistingPoints.Count > 0 ? ground.ExistingPoints.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToList() : null,
                MaterialId = ground.MaterialId, ContourInterval = ground.ContourInterval, Mark = ground.Mark, Comments = ground.Comments
            });

        foreach (var region in document.All<GroundRegion>())
            dto.GroundRegions.Add(new GroundRegionDto
            {
                Id = region.Id, LevelId = region.LevelId, Boundary = region.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Surface = region.Surface.ToString(), MaterialId = region.MaterialId, Approach = region.Approach, Offset = region.Offset, Mark = region.Mark, Comments = region.Comments,
                PathLine = region.PathLine.Count >= 2 ? region.PathLine.SelectMany(point => new[] { point.X, point.Y }).ToList() : null,
                PathWidth = region.PathWidth
            });

        foreach (var group in document.All<Group>())
            dto.Groups.Add(new GroupDto
            {
                Id = group.Id, LevelId = group.LevelId, Name = group.Name, OriginX = group.Origin.X, OriginY = group.Origin.Y,
                Rotation = group.Rotation, Mirrored = group.Mirrored, MemberIds = group.MemberIds.ToList(), Comments = group.Comments
            });

        foreach (var mass in document.All<Mass>())
            dto.Masses.Add(new MassDto
            {
                Id = mass.Id, LevelId = mass.LevelId, Boundary = mass.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Round = mass.Round, CentreX = mass.Centre.X, CentreY = mass.Centre.Y, Radius = mass.Radius,
                BaseOffset = mass.BaseOffset, Height = mass.Height, Top = mass.Top.ToString(), Pitch = mass.Pitch, TopScale = mass.TopScale,
                FloorLevels = mass.FloorLevels.ToList(),
                Links = mass.Links.Select(link => new MassLinkDto { ElementId = link.ElementId, Kind = link.Kind.ToString(), Face = link.Face, LevelId = link.LevelId }).ToList(),
                Mark = mass.Mark, Comments = mass.Comments
            });

        foreach (var pad in document.All<SitePad>())
            dto.SitePads.Add(new SitePadDto
            {
                Id = pad.Id, LevelId = pad.LevelId, Boundary = pad.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                HeightOffset = pad.HeightOffset, Fall = pad.Fall, FallDirection = pad.FallDirection, Batter = pad.Batter,
                Surface = pad.Surface.ToString(), MaterialId = pad.MaterialId, Mark = pad.Mark, Comments = pad.Comments
            });

        foreach (var line in document.All<PropertyLine>())
            dto.PropertyLines.Add(new PropertyLineDto
            {
                Id = line.Id, LevelId = line.LevelId, Boundary = line.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Setback = line.Setback, Mark = line.Mark, Comments = line.Comments,
                Planning = line.Planning == PlanningEdge.None ? null : line.Planning.ToString()
            });

        foreach (var spot in document.All<SpotElevation>())
            dto.SpotElevations.Add(new SpotElevationDto { Id = spot.Id, LevelId = spot.LevelId, X = spot.Location.X, Y = spot.Location.Y, Mark = spot.Mark, Comments = spot.Comments });

        foreach (var system in document.All<BeamSystem>())
            dto.BeamSystems.Add(new BeamSystemDto
            {
                Id = system.Id, TypeId = system.TypeId, LevelId = system.LevelId, Boundary = system.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Direction = system.Direction, Spacing = system.Spacing, Offset = system.Offset, Mark = system.Mark, Comments = system.Comments
            });

        foreach (var type in document.TypesOf<PipeType>())
            (dto.PipeTypes ??= new List<PipeTypeDto>()).Add(new PipeTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Material = type.Material.ToString(), Cost = type.Cost, Description = type.Description
            });

        foreach (var type in document.TypesOf<DuctType>())
            (dto.DuctTypes ??= new List<DuctTypeDto>()).Add(new DuctTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Material = type.Material.ToString(), Shape = type.Shape.ToString(), Inches = type.Inches, Cost = type.Cost, Description = type.Description
            });

        foreach (var duct in document.All<Duct>())
            (dto.Ducts ??= new List<DuctDto>()).Add(new DuctDto
            {
                Id = duct.Id, TypeId = duct.TypeId, LevelId = duct.LevelId, StartX = duct.Start.X, StartY = duct.Start.Y, EndX = duct.End.X, EndY = duct.End.Y,
                StartOffset = duct.StartOffset, EndOffset = duct.EndOffset, Width = duct.Width, Height = duct.Height, Kind = duct.Kind.ToString(),
                SystemId = document.Find<MepSystem>(duct.SystemId)?.Id, FireProtection = duct.FireProtection == FireProtection.None ? null : duct.FireProtection.ToString(),
                DamperRating = duct.DamperRating == 60 ? null : duct.DamperRating, Insulation = duct.Insulation > 0 ? duct.Insulation : null,
                Mark = duct.Mark, Comments = duct.Comments
            });

        foreach (var pipe in document.All<Pipe>())
            (dto.Pipes ??= new List<PipeDto>()).Add(new PipeDto
            {
                Id = pipe.Id, TypeId = pipe.TypeId, LevelId = pipe.LevelId, StartX = pipe.Start.X, StartY = pipe.Start.Y, EndX = pipe.End.X, EndY = pipe.End.Y,
                StartOffset = pipe.StartOffset, EndOffset = pipe.EndOffset, Diameter = pipe.Diameter, Kind = pipe.Kind.ToString(),
                SystemId = document.Find<MepSystem>(pipe.SystemId)?.Id, Return = pipe.Kind == SystemKind.Heating && pipe.Return ? true : null,
                Insulation = pipe.Insulation > 0 ? pipe.Insulation : null, Seal = pipe.Seal != PipeSeal.None ? pipe.Seal.ToString() : null,
                Mark = pipe.Mark, Comments = pipe.Comments
            });

        foreach (var type in document.TypesOf<BeamType>())
            dto.BeamTypes.Add(new BeamTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Section = type.Section.ToString(), Material = type.Material.ToString(),
                Depth = type.Depth, Width = type.Width, WebThickness = type.WebThickness, FlangeThickness = type.FlangeThickness,
                MaterialId = type.MaterialId, Cost = type.Cost, Description = type.Description
            });

        foreach (var beam in document.All<Beam>())
            dto.Beams.Add(new BeamDto
            {
                Id = beam.Id, TypeId = beam.TypeId, LevelId = beam.LevelId, StartX = beam.Start.X, StartY = beam.Start.Y, EndX = beam.End.X, EndY = beam.End.Y,
                StartOffset = beam.StartOffset, EndOffset = beam.EndOffset, Justification = beam.Justification.ToString(), Usage = beam.Usage.ToString(),
                Mark = beam.Mark, Comments = beam.Comments
            });

        foreach (var underlay in document.All<CadUnderlay>())
            (dto.CadUnderlays ??= new List<CadUnderlayDto>()).Add(UnderlayOf(underlay));

        foreach (var link in document.All<BIMDesigner.Core.Coordination.ModelLink>())
            (dto.ModelLinks ??= new List<ModelLinkDto>()).Add(new ModelLinkDto
            {
                Id = link.Id, Path = link.Path, RelativePath = link.RelativePath, Positioning = link.Positioning.ToString(), X = link.Offset.X, Y = link.Offset.Y,
                Elevation = link.Elevation, Rotation = link.Rotation, Shown = link.Shown, Halftone = link.Halftone, Mark = link.Mark, Comments = link.Comments
            });

        foreach (var separator in document.All<RoomSeparator>())
            dto.RoomSeparators.Add(new RoomSeparatorDto
            {
                Id = separator.Id, LevelId = separator.LevelId, StartX = separator.Start.X, StartY = separator.Start.Y, EndX = separator.End.X, EndY = separator.End.Y,
                PhaseCreated = separator.PhaseCreated.ToString(), Mark = separator.Mark, Comments = separator.Comments
            });

        foreach (var railing in document.All<Railing>())
            dto.Railings.Add(new RailingDto
            {
                Id = railing.Id, TypeId = railing.TypeId, LevelId = railing.LevelId,
                Path = railing.Path.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Bulges = railing.Bulges.Any(bulge => bulge != 0) ? railing.Bulges.ToList() : null, HostOpeningId = railing.HostOpeningId,
                Closed = railing.Closed, BaseOffset = railing.BaseOffset, Building = railing.Building.ToString(), Outside = railing.Outside,
                Mark = railing.Mark, Comments = railing.Comments
            });

        foreach (var pipe in document.All<Downpipe>())
            dto.Downpipes.Add(new DownpipeDto
            {
                Id = pipe.Id, GutterId = pipe.GutterId, LevelId = pipe.LevelId, X = pipe.Location.X, Y = pipe.Location.Y,
                Distributor = pipe.Distributor, Mark = pipe.Mark, Comments = pipe.Comments
            });

        foreach (var soffit in document.TypesOf<SoffitType>())
            dto.SoffitTypes.Add(new SoffitTypeDto
            {
                Id = soffit.Id, Name = soffit.Name, TypeMark = soffit.TypeMark, Thickness = soffit.Thickness,
                MaterialId = soffit.MaterialId, Cost = soffit.Cost, Description = soffit.Description,
                Board = soffit.Board.ToString(), FreeAirArea = soffit.FreeAirArea
            });

        foreach (var eave in document.TypesOf<EaveType>())
            dto.EaveTypes.Add(new EaveTypeDto
            {
                Id = eave.Id, Name = eave.Name, TypeMark = eave.TypeMark, Kind = eave.Kind.ToString(),
                RafterWidth = eave.RafterWidth, RafterDepth = eave.RafterDepth, RafterSpacing = eave.RafterSpacing, Tail = eave.Tail.ToString(),
                DeckThickness = eave.DeckThickness, SoffitThickness = eave.SoffitThickness, Board = eave.Board.ToString(), FreeAirArea = eave.FreeAirArea,
                FriezeDepth = eave.FriezeDepth, FriezeThickness = eave.FriezeThickness, Moulding = eave.Moulding.ToString(), MouldingSize = eave.MouldingSize,
                Bracket = eave.Bracket.ToString(), BracketSpacing = eave.BracketSpacing, BracketHeight = eave.BracketHeight,
                BracketDepth = eave.BracketDepth, BracketWidth = eave.BracketWidth, Underside = eave.Underside.ToString(),
                MaterialId = eave.MaterialId, SoffitMaterialId = eave.SoffitMaterialId, Cost = eave.Cost, Description = eave.Description
            });

        foreach (var mullion in document.TypesOf<MullionType>())
            dto.MullionTypes.Add(new MullionTypeDto
            {
                Id = mullion.Id, Name = mullion.Name, TypeMark = mullion.TypeMark, Shape = mullion.Shape.ToString(),
                Width1 = mullion.Width1, Width2 = mullion.Width2, Thickness = mullion.Thickness, Offset = mullion.Offset,
                Leg1 = mullion.Leg1, Leg2 = mullion.Leg2, CapDepth = mullion.CapDepth, WallThickness = mullion.WallThickness,
                Material = mullion.Material.ToString(), MaterialId = mullion.MaterialId, Cost = mullion.Cost, Description = mullion.Description
            });

        foreach (var type in document.TypesOf<RoofWindowType>())
            dto.RoofWindowTypes.Add(new RoofWindowTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Width = type.Width, Height = type.Height,
                FrameWidth = type.FrameWidth, Upstand = type.Upstand, Operation = type.Operation.ToString(),
                FrameMaterialId = type.FrameMaterialId, GlassMaterialId = type.GlassMaterialId, Cost = type.Cost, Description = type.Description,
                HeatTransferCoefficient = type.HeatTransferCoefficient, SolarHeatGainCoefficient = type.SolarHeatGainCoefficient
            });

        foreach (var window in document.All<RoofWindow>())
            dto.RoofWindows.Add(new RoofWindowDto
            {
                Id = window.Id, TypeId = window.TypeId, LevelId = window.LevelId, RoofId = window.RoofId,
                X = window.Location.X, Y = window.Location.Y, Mark = window.Mark, Comments = window.Comments,
                Width = window.Width, Height = window.Height, FrameWidth = window.FrameWidth, Upstand = window.Upstand,
                FrameMaterialId = window.FrameMaterialId, GlassMaterialId = window.GlassMaterialId
            });

        foreach (var shaft in document.All<ShaftOpening>())
            dto.ShaftOpenings.Add(new ShaftOpeningDto
            {
                Id = shaft.Id, LevelId = shaft.LevelId, X = shaft.Location.X, Y = shaft.Location.Y,
                Shape = shaft.Shape.ToString(), Width = shaft.Width, Depth = shaft.Depth, Angle = shaft.Angle,
                BaseOffset = shaft.BaseOffset, TopLevelId = shaft.TopLevelId, TopOffset = shaft.TopOffset,
                UnconnectedHeight = shaft.UnconnectedHeight, Mark = shaft.Mark, Comments = shaft.Comments, ForServices = shaft.ForServices ? true : null
            });

        foreach (var sweep in document.All<RoofEdgeSweep>())
            dto.RoofEdgeSweeps.Add(new RoofEdgeSweepDto
            {
                Id = sweep.Id,
                Kind = sweep switch { Gutter => "Gutter", Soffit => "Soffit", Eave => "Eave", _ => "Fascia" },
                TypeId = sweep.TypeId,
                LevelId = sweep.LevelId,
                RoofId = sweep.RoofId,
                EdgeIds = sweep.EdgeIds.ToList(),
                HorizontalOffset = sweep.HorizontalOffset,
                VerticalOffset = sweep.VerticalOffset,
                Mark = sweep.Mark,
                Comments = sweep.Comments,
                FireStopped = (sweep as Eave)?.FireStopped,
                FireBlocked = (sweep as Eave)?.FireBlocked
            });

        foreach (var opening in document.All<WallOpening>())
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
                Comments = opening.Comments,
                ForServices = opening.ForServices ? true : null
            });

        foreach (var placed in document.All<PlacedSweep>())
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
                AutomaticallyEmbed = curtain.AutomaticallyEmbed,
                HeatTransferCoefficient = curtain.HeatTransferCoefficient,
                SafetyGlazing = curtain.SafetyGlazing,
                InteriorVertical = curtain.InteriorVertical,
                BorderVerticalStart = curtain.BorderVerticalStart,
                BorderVerticalEnd = curtain.BorderVerticalEnd,
                InteriorHorizontal = curtain.InteriorHorizontal,
                BorderHorizontalBottom = curtain.BorderHorizontalBottom,
                BorderHorizontalTop = curtain.BorderHorizontalTop,
                JoinCondition = curtain.JoinCondition.ToString(),
                MullionAnchors = curtain.MullionAnchors,
                Splices = curtain.Splices.ToString(),
                SpliceAbove = curtain.SpliceAbove,
                StackJoint = curtain.StackJoint,
                AnchorCapacity = curtain.AnchorCapacity
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
                Separating = type.Separating,
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
                LogShape = type.Log?.Shape.ToString(),
                LogCourse = type.Log?.CourseHeight,
                LogOverhang = type.Log?.Overhang,
                Bands = type.Bands.Count == 0 ? null : type.Bands.Select(band => new WallBandDto
                {
                    Layer = band.Layer, Bottom = band.Bottom, Top = band.Top, MaterialId = band.MaterialId
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
                TrimProjectionInterior = type.TrimProjectionInterior,
                SelfClosing = type.SelfClosing
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
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient,
                LightTransmittance = type.LightTransmittance,
                TrickleVent = type.TrickleVent > 0 ? type.TrickleVent : null,
                Lights = type.Lights,
                LightPattern = type.LightPattern,
                TransomHeight = type.TransomHeight,
                Fanlight = type.Fanlight.ToString(),
                MullionWidth = type.MullionWidth
            };

            WriteOpeningType(windowType, type);
            dto.WindowTypes.Add(windowType);
        }

        foreach (var door in document.All<Door>())
        {
            var doorDto = new DoorDto { SwingAngle = door.SwingAngle, FrameType = door.FrameType, FrameMaterial = door.FrameMaterial, Finish = door.Finish };
            WriteOpening(doorDto, door);
            dto.Doors.Add(doorDto);
        }

        foreach (var window in document.All<Window>())
        {
            var windowDto = new WindowDto();
            WriteOpening(windowDto, window);
            dto.Windows.Add(windowDto);
        }

        foreach (var dimension in document.All<Dimension>())
            dto.Dimensions.Add(new DimensionDto
            {
                Id = dimension.Id,
                LevelId = dimension.LevelId,
                TypeId = dimension.TypeId,
                Start = WriteEnd(dimension.Start),
                End = WriteEnd(dimension.End),
                Between = dimension.Between.Count > 0 ? dimension.Between.Select(WriteEnd).ToList() : null,
                Offset = dimension.Offset,
                Override = dimension.Override,
                Comments = dimension.Comments,
                Workset = dimension.Workset
            });

        foreach (var angular in document.All<AngularDimension>())
            dto.AngularDimensions.Add(new AngularDimensionDto
            {
                Id = angular.Id, LevelId = angular.LevelId, TypeId = angular.TypeId, First = WriteEnd(angular.First), Second = WriteEnd(angular.Second),
                X = angular.Through.X, Y = angular.Through.Y, Override = angular.Override, Comments = angular.Comments
            });

        foreach (var radial in document.All<RadialDimension>())
            dto.RadialDimensions.Add(new RadialDimensionDto
            {
                Id = radial.Id, LevelId = radial.LevelId, TypeId = radial.TypeId, Kind = radial.Kind.ToString(), Target = WriteEnd(radial.Target),
                X = radial.Through.X, Y = radial.Through.Y, Override = radial.Override, Comments = radial.Comments
            });

        foreach (var tag in document.All<Tag>())
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

        foreach (var note in document.All<TextNote>())
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

        foreach (var section in document.All<SectionMarker>())
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

        // The {3D} view is the project's own: no id of its own to keep.
        dto.Default3D = View3DToDto(document.Default3D);
        dto.Default3D.Id = Guid.Empty;
        foreach (var view in document.All<View3D>()) dto.Views3D.Add(View3DToDto(view));
        foreach (var callout in document.All<Callout>())
            dto.Callouts.Add(new CalloutDto
            {
                Id = callout.Id, LevelId = callout.LevelId, Name = callout.Name,
                ParentKind = callout.Parent.Kind.ToString(), ParentId = callout.Parent.TargetId, ParentIndex = callout.Parent.Index,
                MinX = callout.Region.MinX, MinY = callout.Region.MinY, MaxX = callout.Region.MaxX, MaxY = callout.Region.MaxY,
                Planning = callout.Planning == PlanningDrawing.None ? null : callout.Planning.ToString(),
                Comments = callout.Comments
            });

        foreach (var marker in document.All<InteriorElevationMarker>())
            dto.InteriorElevations.Add(new InteriorElevationDto
            {
                Id = marker.Id, LevelId = marker.LevelId, Name = marker.Name, X = marker.Location.X, Y = marker.Location.Y, Rotation = marker.Rotation,
                Up = marker.LooksUp, Right = marker.LooksRight, Down = marker.LooksDown, Left = marker.LooksLeft, Comments = marker.Comments
            });

        foreach (var walk in document.All<Walkthrough>())
            dto.Walkthroughs.Add(new WalkthroughDto
            {
                Id = walk.Id, LevelId = walk.LevelId, Name = walk.Name, Path = walk.Path.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                EyeHeight = walk.EyeHeight, Frames = walk.Frames, FramesPerSecond = walk.FramesPerSecond, Comments = walk.Comments
            });

        dto.Revisions = document.Revisions.Select(revision => new RevisionDto
        {
            Id = revision.Id, Numbering = revision.Numbering.ToString(), Date = revision.Date, Description = revision.Description,
            IssuedTo = revision.IssuedTo, IssuedBy = revision.IssuedBy, Issued = revision.Issued, Visibility = revision.Visibility.ToString()
        }).ToList();

        foreach (var cloud in document.All<RevisionCloud>())
            dto.RevisionClouds.Add(new RevisionCloudDto
            {
                Id = cloud.Id, LevelId = cloud.LevelId, RevisionId = cloud.RevisionId,
                Outline = cloud.Outline.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                TagX = cloud.TagAt?.X, TagY = cloud.TagAt?.Y, Mark = cloud.Mark, Comments = cloud.Comments, Workset = cloud.Workset
            });

        dto.Keynotes = new KeynoteTableDto
        {
            Source = document.Keynotes.Source,
            Entries = document.Keynotes.Entries.Select(entry => new KeynoteEntryDto { Key = entry.Key, Text = entry.Text, Parent = NullIfEmpty(entry.Parent) }).ToList()
        };

        foreach (var keynote in document.All<KeynoteTag>())
            dto.KeynoteTags.Add(new KeynoteTagDto
            {
                Id = keynote.Id, LevelId = keynote.LevelId, Source = keynote.Source.ToString(), TargetId = keynote.TargetId, MaterialId = keynote.MaterialId,
                Key = keynote.Key, X = keynote.Position.X, Y = keynote.Position.Y, LeaderX = keynote.LeaderEnd?.X, LeaderY = keynote.LeaderEnd?.Y,
                Display = keynote.Display.ToString(), Mark = keynote.Mark, Comments = keynote.Comments, Workset = keynote.Workset
            });

        dto.Schedules = document.Schedules.Select(schedule => new ScheduleDto
        {
            Id = schedule.Id, Name = schedule.Name, Category = schedule.Category.ToString(), Fields = schedule.Fields.ToList(),
            Filters = schedule.Filters.Select(filter => new ScheduleFilterDto { Field = filter.Field, Comparison = filter.Comparison.ToString(), Value = filter.Value }).ToList(),
            SortBy = schedule.SortBy, SortDescending = schedule.SortDescending, GroupBy = schedule.GroupBy, ShowTotals = schedule.ShowTotals,
            Itemize = schedule.Itemize, NotTotalled = schedule.NotTotalled.OrderBy(field => field, StringComparer.Ordinal).ToList(),
            Calculated = schedule.Calculated.Select(calculated => new CalculatedFieldDto
            {
                Name = calculated.Name, Kind = calculated.Kind.ToString(), Formula = calculated.Formula, PercentageOf = calculated.PercentageOf, ByGroup = calculated.ByGroup
            }).ToList(),
            Formats = schedule.Formats.Select(format => new ConditionalFormatDto
            {
                Field = format.Field, Test = format.Test.ToString(), Value = format.Value, Value2 = format.Value2, Background = format.Background.ToString()
            }).ToList(),
            ShowFormatsOnSheets = schedule.ShowFormatsOnSheets
        }).ToList();

        foreach (var sheet in document.All<Sheet>())
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
                RevisionIds = sheet.RevisionIds.ToList(),
                Comments = sheet.Comments,
                Workset = sheet.Workset,
                Viewports = sheet.Viewports.Select(viewport => new ViewportDto
                {
                    Id = viewport.Id,
                    Kind = viewport.View.Kind.ToString(),
                    TargetId = viewport.View.TargetId,
                    ScheduleName = viewport.View.ScheduleName,
                    Index = viewport.View.Index,
                    X = viewport.Centre.X,
                    Y = viewport.Centre.Y,
                    Scale = viewport.Scale.Denominator,
                    ShowTitle = viewport.ShowTitle,
                    TitleOverride = viewport.TitleOverride,
                    DetailNumber = viewport.DetailNumber,
                    ShowNorth = viewport.ShowNorth,
                    ShowScaleBar = viewport.ShowScaleBar,
                    Note = viewport.Note.Length == 0 ? null : viewport.Note
                }).ToList(),
                Images = sheet.Images.Count == 0 ? null : sheet.Images.Select(image => new SheetImageDto
                {
                    Id = image.Id, Name = image.Name, Data = image.Data, X = image.Centre.X, Y = image.Centre.Y,
                    Width = image.Width, ShowTitle = image.ShowTitle, Title = image.Title.Length == 0 ? null : image.Title
                }).ToList()
            });

        foreach (var (view, hidden) in document.ViewSettings.Filtered)
            dto.ViewSettings.Add(new ViewSettingsDto
            {
                Kind = view.Kind.ToString(),
                TargetId = view.TargetId,
                Index = view.Index,
                HiddenWallFunctions = hidden.Select(function => function.ToString()).OrderBy(name => name).ToList(),
                Scale = document.ViewSettings.Scaled.Where(s => s.View == view).Select(s => (double?)s.Scale).FirstOrDefault(),
                JoinDisplay = document.ViewSettings.JoinDisplays.Where(s => s.View == view).Select(s => s.Display.ToString()).FirstOrDefault(),
                DetailLevel = document.ViewSettings.DetailLevelOf(view)?.ToString(),
                Template = document.ViewSettings.TemplateOf(view)
            });

        // Views with a scale, join display, detail level or template of their own but nothing hidden.
        foreach (var view in document.ViewSettings.Scaled.Select(s => s.View).Concat(document.ViewSettings.JoinDisplays.Select(s => s.View))
                     .Concat(document.ViewSettings.DetailLevels.Select(s => s.View)).Concat(document.ViewSettings.Templated.Select(s => s.View)).Distinct()
                     .Where(v => document.ViewSettings.Filtered.All(f => f.View != v)))
            dto.ViewSettings.Add(new ViewSettingsDto
            {
                Kind = view.Kind.ToString(),
                TargetId = view.TargetId,
                Index = view.Index,
                Scale = document.ViewSettings.Scaled.Where(s => s.View == view).Select(s => (double?)s.Scale).FirstOrDefault(),
                JoinDisplay = document.ViewSettings.JoinDisplays.Where(s => s.View == view).Select(s => s.Display.ToString()).FirstOrDefault(),
                DetailLevel = document.ViewSettings.DetailLevelOf(view)?.ToString(),
                Template = document.ViewSettings.TemplateOf(view)
            });

        foreach (var template in document.All<ViewTemplate>())
            dto.ViewTemplates.Add(new ViewTemplateDto
            {
                Id = template.Id, Name = template.Name, For = template.For.ToString(),
                IncludeScale = template.IncludeScale, Scale = template.Scale,
                IncludeDetailLevel = template.IncludeDetailLevel, DetailLevel = template.DetailLevel.ToString(),
                IncludeWallFunctions = template.IncludeWallFunctions, HiddenWallFunctions = template.HiddenWallFunctions.Select(f => f.ToString()).OrderBy(n => n).ToList(),
                IncludeJoinDisplay = template.IncludeJoinDisplay, JoinDisplay = template.JoinDisplay.ToString(),
                IncludeVisualStyle = template.IncludeVisualStyle, VisualStyle = template.VisualStyle.ToString(),
                IncludeHiddenKinds = template.IncludeHiddenKinds, HiddenKinds = template.HiddenKinds.Select(k => k.ToString()).OrderBy(n => n).ToList(),
                IncludeFilters = template.IncludeFilters, Filters = template.Filters.Count == 0 ? null : template.Filters.Select(FilterUseDto).ToList(),
                IncludePhaseFilter = template.IncludePhaseFilter, PhaseFilter = template.PhaseFilter.ToString(),
                Comments = template.Comments
            });

        foreach (var grid in document.All<Grid>())
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
                Separating = type.Separating ? true : null,
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

        foreach (var slab in document.All<Slab>())
            dto.Slabs.Add(new SlabDto
            {
                Id = slab.Id,
                TypeId = slab.TypeId,
                LevelId = slab.LevelId,
                Kind = SlabKindOf(slab.Category),
                HeightOffset = slab.HeightOffset,
                Boundary = slab.Boundary.SelectMany(point => new[] { point.X, point.Y }).ToList(),
                Holes = slab.Holes.Count > 0 ? slab.Holes.Select(hole => hole.SelectMany(point => new[] { point.X, point.Y }).ToList()).ToList() : null,
                SpanDirection = (slab as Floor)?.SpanDirection,
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
                        ArcIds = opening.ArcIds.Any(id => id is not null) ? opening.ArcIds.ToList() : null,
                        Square = opening.Square
                    }).ToList()
                    : null,
                RoofFasciaDepth = slab is Roof fascia ? fascia.FasciaDepth : 150,
                RoofHighLevelVents = slab is Roof { HighLevelVents: true },
                RoofSolarPeakPower = slab is Roof { SolarPeakPower: > 0 } solar ? solar.SolarPeakPower : null,
                RoofSolarOvershading = slab is Roof { SolarPeakPower: > 0, SolarOvershading: not PvOvershading.NoneOrVeryLittle } shaded ? shaded.SolarOvershading.ToString() : null,
                RoofDrainageFall = slab is Roof drained ? drained.DrainageFall : RoofDrainage.DesignFall,
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
                Structural = type.Structural,
                StructuralMaterial = type.StructuralMaterial.ToString(),
                FlangeThickness = type.FlangeThickness,
                WebThickness = type.WebThickness,
                WallThickness = type.WallThickness,
                FireRating = type.FireRating,
                PreservativeTreated = type.PreservativeTreated,
                RustProtected = type.RustProtected,

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

        foreach (var column in document.All<Column>())
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
                Structural = column.Structural,
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

        foreach (var type in document.TypesOf<FootingType>())
            dto.FootingTypes.Add(new FootingTypeDto
            {
                Id = type.Id, Name = type.Name, TypeMark = type.TypeMark, Kind = type.Kind.ToString(), Width = type.Width, Length = type.Length,
                Thickness = type.Thickness, MaterialId = type.MaterialId, StemMaterialId = type.StemMaterialId, AssemblyCode = type.AssemblyCode,
                Cost = type.Cost, Description = type.Description
            });

        foreach (var footing in document.All<WallFooting>())
            dto.WallFootings.Add(new WallFootingDto
            {
                Id = footing.Id, TypeId = footing.TypeId, LevelId = footing.LevelId, WallId = footing.WallId, Depth = footing.Depth,
                Eccentricity = footing.Eccentricity, Mark = footing.Mark, Comments = footing.Comments
            });

        foreach (var pad in document.All<PadFooting>())
            dto.PadFootings.Add(new PadFootingDto
            {
                Id = pad.Id, TypeId = pad.TypeId, LevelId = pad.LevelId, ColumnId = pad.ColumnId, X = pad.Location.X, Y = pad.Location.Y,
                Rotation = pad.Rotation, Depth = pad.Depth, Mark = pad.Mark, Comments = pad.Comments
            });

        foreach (var type in document.TypesOf<ComponentType>())
            dto.ComponentTypes.Add(new ComponentTypeDto
            {
                Id = type.Id,
                Name = type.Name,
                Category = type.Kind.ToString(),
                Form = type.Form.ToString(),
                Placement = type.Placement.ToString(),
                AgainstWall = type.AgainstWall,
                Enclosure = type.Form == ComponentForm.Shower ? type.Enclosure.ToString() : null,
                ExtractRate = type.ExtractRate > 0 ? type.ExtractRate : null,
                Efficacy = type.Efficacy > 0 ? type.Efficacy : null,
                WaterDemand = type.WaterDemand == TreeWaterDemand.Unknown ? null : type.WaterDemand.ToString(),
                StemDiameter = type.StemDiameter > 0 ? type.StemDiameter : null,
                HotWater = type.Form == ComponentForm.Boiler && type.HotWater != BoilerHotWater.Combi ? type.HotWater.ToString() : null,
                FlowRate = type.FlowRate > 0 ? type.FlowRate : null, FlushVolume = type.FlushVolume > 0 ? type.FlushVolume : null,
                PartFlushVolume = type.PartFlushVolume > 0 ? type.PartFlushVolume : null, Capacity = type.Capacity > 0 ? type.Capacity : null,
                WaterPerUse = type.WaterPerUse > 0 ? type.WaterPerUse : null,
                Access = type.Form == ComponentForm.DrainAccess ? type.Access.ToString() : null,
                HeatOutput = HeatsOrHeated(type.Form) ? type.HeatOutput : null,
                CoolingOutput = type.CoolingOutput > 0 ? type.CoolingOutput : null,
                GasInput = type.GasInput > 0 ? type.GasInput : null,
                Coverage = type.Coverage > 0 ? type.Coverage : null, KFactor = type.KFactor > 0 ? type.KFactor : null,
                Temperature = type.Temperature > 0 ? type.Temperature : null, SupplyPressure = type.SupplyPressure > 0 ? type.SupplyPressure : null,
                HeatExponent = type.Form is ComponentForm.Radiator or ComponentForm.TowelRail ? type.HeatExponent : null,
                FloorOutput = type.Form == ComponentForm.UnderfloorHeating ? type.FloorOutput : null,
                FloorOutputLimit = type.Form == ComponentForm.UnderfloorHeating ? type.FloorOutputLimit : null,
                PipeSpacing = type.Form == ComponentForm.UnderfloorHeating ? type.PipeSpacing : null,
                Airflow = type.Form is ComponentForm.HeatRecoveryUnit or ComponentForm.ExtractUnit ? type.Airflow : null,
                AvailablePressure = type.Form is ComponentForm.HeatRecoveryUnit or ComponentForm.ExtractUnit ? type.AvailablePressure : null,
                HeatRecovery = type.Form == ComponentForm.HeatRecoveryUnit ? type.HeatRecovery : null,
                SpecificFanPower = type.SpecificFanPower > 0 ? type.SpecificFanPower : null,
                SpaceEfficiency = type.SpaceEfficiency > 0 ? type.SpaceEfficiency : null,
                WaterEfficiency = type.WaterEfficiency > 0 ? type.WaterEfficiency : null,
                StandingLoss = type.StandingLoss > 0 ? type.StandingLoss : null,
                PressureLoss = type.PressureLoss > 0 ? type.PressureLoss : null,
                RatedAirflow = type.RatedAirflow > 0 ? type.RatedAirflow : null,
                ContinuousRate = type.ContinuousRate > 0 ? type.ContinuousRate : null,
                VentArea = type.VentArea > 0 ? type.VentArea : null,
                Load = type.Load > 0 ? type.Load : null,
                Lumens = type.Lumens > 0 ? type.Lumens : null,
                Distribution = type.Kind == BuiltInCategory.LightingFixtures ? type.Distribution.ToString() : null,
                Photometry = type.Photometry is { } photometry
                    ? new PhotometryDto
                    {
                        Source = photometry.Source, Lumens = photometry.Lumens, Watts = photometry.Watts, Planes = photometry.Planes.ToList(),
                        Angles = photometry.Angles.ToList(), Candela = photometry.Candela.Select(plane => plane.ToList()).ToList()
                    }
                    : null,
                Ways = type.Form == ComponentForm.ConsumerUnit && type.Ways > 0 ? type.Ways : null,
                SupplyRating = type.Form == ComponentForm.ConsumerUnit && type.SupplyRating > 0 ? type.SupplyRating : null,
                ExternalImpedance = type.Form == ComponentForm.ConsumerUnit && type.ExternalImpedance > 0 ? type.ExternalImpedance : null,
                Solids = type.Solids.Count == 0 ? null : type.Solids.Select(solid => new ComponentSolidDto
                {
                    Name = solid.Name, Shape = solid.Shape.ToString(), Finish = solid.Finish.ToString(),
                    From = solid.From, To = solid.To, Back = solid.Back, Front = solid.Front, Bottom = solid.Bottom, Top = solid.Top
                }).ToList(),
                Mesh = type.Mesh is not { } model ? null : new ComponentMeshDto
                {
                    Source = model.Source,
                    Positions = Packed(model.Positions.SelectMany(p => new[] { (float)p.X, (float)p.Y, (float)p.Z }).ToArray()),
                    Triangles = Packed(model.Triangles.ToArray()),
                    Edges = Packed(model.Edges.ToArray())
                },
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

        foreach (var component in document.All<Component>())
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
                StandsOn = component.StandsOn,
                FlipFacing = component.FlipFacing,
                FlipHand = component.FlipHand,
                Mark = component.Mark,
                Comments = component.Comments,
                Workset = component.Workset,
                PhaseCreated = component.PhaseCreated.ToString(),
                PhaseDemolished = component.PhaseDemolished?.ToString()
            });

        // The systems, with only the fittings still in the model on them.
        var systems = document.All<MepSystem>().Select(system => new MepSystemDto
        {
            Id = system.Id, Kind = system.Kind.ToString(), Name = system.Name,
            SourceId = document.Find<Component>(system.SourceId)?.Id,
            Members = system.Members.Where(id => document.Find<Component>(id) is not null).Distinct().ToList(),
            Use = system.Kind == SystemKind.Power ? system.Use.ToString() : null,
            FlowTemperature = system.Kind == SystemKind.Heating && system.FlowTemperature > 0 ? system.FlowTemperature : null,
            ReturnTemperature = system.Kind == SystemKind.Heating && system.ReturnTemperature > 0 ? system.ReturnTemperature : null,
            Breaker = system.Kind == SystemKind.Power && system.Breaker > 0 ? system.Breaker : null,
            Curve = system.Kind == SystemKind.Power ? system.Curve.ToString() : null,
            Rcd = system.Kind == SystemKind.Power ? system.Rcd : null,
            ArcFault = system.Kind == SystemKind.Power ? system.ArcFault : null,
            Cable = system.Kind == SystemKind.Power && system.Cable.Length > 0 ? system.Cable : null,
            Method = system.Kind == SystemKind.Power ? system.Method.ToString() : null,
            Grouped = system.Kind == SystemKind.Power && system.Grouped > 1 ? system.Grouped : null,
            CableLength = system.Kind == SystemKind.Power && system.CableLength > 0 ? system.CableLength : null,
            Mark = system.Mark, Comments = system.Comments
        }).ToList();
        dto.Systems = systems.Count > 0 ? systems : null;

        foreach (var room in document.All<Room>())
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
                ClassificationCode = NullIfEmpty(room.ClassificationCode),
                DesignTemperature = room.DesignTemperature,
                Unheated = room.Unheated ? true : null,
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
                FaceRegions = wall.FaceRegions.Count == 0 ? null : wall.FaceRegions.Select(region => new WallFaceRegionDto
                {
                    Face = region.Face.ToString(), From = region.From, To = region.To, Bottom = region.Bottom, Top = region.Top,
                    MaterialId = region.MaterialId
                }).ToList(),
                CurtainVerticals = wall.CurtainGrid?.Verticals.ToList(),
                CurtainHorizontals = wall.CurtainGrid?.Horizontals.ToList(),
                CurtainRemoved = wall.CurtainGrid?.Removed?
                    .Select(segment => new CurtainSegmentDto { Vertical = segment.Vertical, Line = segment.Line, From = segment.From, To = segment.To })
                    .ToList(),
                CurtainGlass = wall.CurtainGlass.ToString(),
                CurtainPanels = wall.CurtainPanels?.Select(p => new CurtainPanelDto { Column = p.Column, Row = p.Row, Kind = p.Kind.ToString(), OpeningTypeId = p.OpeningTypeId, Glass = p.Glass.ToString(), FlipHand = p.FlipHand, FlipFacing = p.FlipFacing, IsOpen = p.IsOpen }).ToList(),
                CurtainMullions = wall.CurtainMullions?.Select(m => new CurtainMullionDto { Vertical = m.Vertical, Line = m.Line, At = m.At, TypeId = m.TypeId }).ToList(),
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

        // What is pinned, by its id: one list for every kind of element.
        dto.PinnedIds.AddRange(document.Elements.Where(element => element.Pinned).Select(element => element.Id));
        dto.PurgedLibrary = document.PurgedLibrary.Count == 0 ? null : document.PurgedLibrary.Order(StringComparer.OrdinalIgnoreCase).ToList();
        WriteRound6(document, dto);
        WriteCoordination(document, dto);

        return dto;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrEmpty(text) ? null : text;

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
        dto.SafetyGlazing = type.SafetyGlazing;
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

    /// <summary>A project read from its file - its links found again and its linked drawings read again from theirs, unless it is itself being read as a link.</summary>
    public static BimDocument Load(string path, bool refreshLinks = true)
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

        var document = FromJson(json);
        if (refreshLinks) Interoperability.LinkFiles.Refresh(document, path);
        return document;
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

    /// <summary>
    /// An imported drawing as saved: where it is and how it is shown, and the drawing itself - its
    /// points packed as 64-bit numbers in base 64, so a surveyed site's coordinates keep their millimetres.
    /// </summary>
    private static CadUnderlayDto UnderlayOf(CadUnderlay underlay)
    {
        var drawing = underlay.Drawing;
        var points = new List<double>();
        foreach (var path in drawing.Paths)
            foreach (var point in path.Points)
            {
                points.Add(point.X);
                points.Add(point.Y);
            }

        return new CadUnderlayDto
        {
            Id = underlay.Id, LevelId = underlay.LevelId, Source = underlay.Source, Linked = underlay.Linked ? true : null, X = underlay.Location.X, Y = underlay.Location.Y,
            Rotation = underlay.Rotation, Scale = underlay.Scale, Halftone = underlay.Halftone, HiddenLayers = underlay.HiddenLayers.ToList(),
            Mark = underlay.Mark, Comments = underlay.Comments,
            Layers = drawing.Layers.Select(layer => new CadLayerDto { Name = layer.Name, Colour = layer.Colour.ToString() }).ToList(),
            PathLayers = drawing.Paths.Select(path => path.Layer).ToList(),
            PathCounts = drawing.Paths.Select(path => path.Points.Count * (path.Closed ? -1 : 1)).ToList(),
            PathColours = drawing.Paths.Any(path => path.Colour is not null) ? drawing.Paths.Select(path => path.Colour?.ToString() ?? string.Empty).ToList() : null,
            Points = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(points.ToArray().AsSpan())),
            Labels = drawing.Labels.Select(label => new CadLabelDto
            {
                Layer = label.Layer, X = label.At.X, Y = label.At.Y, Height = label.Height, Rotation = label.Rotation, Text = label.Text, Colour = label.Colour?.ToString()
            }).ToList()
        };
    }

    /// <summary>An imported drawing read back; null where its points do not add up.</summary>
    private static CadUnderlay? ReadUnderlay(CadUnderlayDto saved)
    {
        double[] numbers;
        try
        {
            numbers = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(Convert.FromBase64String(saved.Points ?? string.Empty)).ToArray();
        }
        catch (FormatException)
        {
            return null;
        }

        ColourRgb? Colour(string? text) => ColourRgb.TryParse(text, out var colour) ? colour : null;
        var layers = (saved.Layers ?? new List<CadLayerDto>()).Select(layer => new CadDrawingLayer(layer.Name ?? "0", Colour(layer.Colour) ?? new ColourRgb(0, 0, 0))).ToList();
        int Layer(int index) => index >= 0 && index < layers.Count ? index : 0;
        var paths = new List<CadPath>();
        var counts = saved.PathCounts ?? new List<int>();
        var at = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            var count = Math.Abs(counts[i]);
            if (at + 2 * count > numbers.Length) return null;
            var points = Enumerable.Range(0, count).Select(j => new Point2D(numbers[at + 2 * j], numbers[at + 2 * j + 1])).ToList();
            at += 2 * count;
            if (points.Count < 2 || !points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y))) continue;
            var layer = saved.PathLayers is { } pathLayers && i < pathLayers.Count ? Layer(pathLayers[i]) : 0;
            var colour = saved.PathColours is { } colours && i < colours.Count ? Colour(colours[i]) : null;
            paths.Add(new CadPath(layer, points, counts[i] < 0, colour));
        }

        var labels = (saved.Labels ?? new List<CadLabelDto>())
            .Where(label => double.IsFinite(label.X) && double.IsFinite(label.Y) && double.IsFinite(label.Height) && double.IsFinite(label.Rotation) && !string.IsNullOrEmpty(label.Text))
            .Select(label => new CadLabel(Layer(label.Layer), new Point2D(label.X, label.Y), label.Height, label.Rotation, label.Text!, Colour(label.Colour)))
            .ToList();
        var underlay = new CadUnderlay
        {
            Id = saved.Id, LevelId = saved.LevelId, Source = saved.Source ?? string.Empty, Linked = saved.Linked == true,
            Location = double.IsFinite(saved.X) && double.IsFinite(saved.Y) ? new Point2D(saved.X, saved.Y) : default,
            Rotation = double.IsFinite(saved.Rotation) ? saved.Rotation : 0,
            Scale = double.IsFinite(saved.Scale) && saved.Scale > 0 ? saved.Scale : 1,
            Halftone = saved.Halftone, Drawing = new CadDrawing(layers, paths, labels),
            Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
        };
        underlay.HiddenLayers.UnionWith(saved.HiddenLayers ?? new List<string>());
        return underlay;
    }

    /// <summary>A project's energy settings as saved.</summary>
    private static RenderDto RenderOf(RenderSettings settings) => new()
    {
        Date = settings.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Time = settings.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        Latitude = settings.Latitude, Longitude = settings.Longitude, UtcOffset = settings.UtcOffset, Summer = settings.Summer?.ToString(),
        Sky = settings.Sky.ToString(), Quality = settings.Quality.ToString(), Width = settings.Width, Height = settings.Height, Exposure = settings.Exposure
    };

    private static void ReadRender(RenderDto saved, RenderSettings settings)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        if (DateOnly.TryParseExact(saved.Date, "yyyy-MM-dd", invariant, System.Globalization.DateTimeStyles.None, out var date)) settings.Date = date;
        if (TimeOnly.TryParseExact(saved.Time, "HH:mm", invariant, System.Globalization.DateTimeStyles.None, out var time)) settings.Time = time;
        settings.Latitude = saved.Latitude is { } latitude && double.IsFinite(latitude) ? Math.Clamp(latitude, -90, 90) : null;
        settings.Longitude = saved.Longitude is { } longitude && double.IsFinite(longitude) ? Math.Clamp(longitude, -180, 180) : null;
        settings.UtcOffset = saved.UtcOffset is { } offset && double.IsFinite(offset) ? Math.Clamp(offset, -12, 14) : null;
        settings.Summer = saved.Summer is null ? null : ParseEnum(saved.Summer, SummerTime.None);
        settings.Sky = ParseEnum(saved.Sky, RenderSky.Clear);
        settings.Quality = ParseEnum(saved.Quality, RenderQuality.Medium);
        settings.Width = Math.Clamp(saved.Width, 64, 8192);
        settings.Height = Math.Clamp(saved.Height, 64, 8192);
        settings.Exposure = double.IsFinite(saved.Exposure) ? Math.Clamp(saved.Exposure, -5, 5) : 0;
    }

    private static DaylightDto DaylightOf(Core.Energy.DaylightSettings settings) => new()
    {
        SunlightDate = settings.SunlightDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        OutsideLight = settings.OutsideLight, Quality = settings.Quality.ToString()
    };

    private static void ReadDaylight(DaylightDto saved, Core.Energy.DaylightSettings settings)
    {
        if (DateOnly.TryParseExact(saved.SunlightDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            settings.SunlightDate = date;
        settings.OutsideLight = double.IsFinite(saved.OutsideLight) ? Math.Clamp(saved.OutsideLight, 0, 200_000) : 0;
        settings.Quality = ParseEnum(saved.Quality, Core.Energy.DaylightQuality.Standard);
    }

    private static double Finite(double value, double least, double most) => double.IsFinite(value) ? Math.Clamp(value, least, most) : least;

    private static EnergyDto EnergyOf(EnergySettings settings) => new()
    {
        Overshading = settings.Overshading.ToString(), ShelteredSides = settings.ShelteredSides, Bridging = settings.Bridging.ToString(),
        YValue = settings.YValue, Psi = settings.Psi.Count > 0 ? new Dictionary<string, double>(settings.Psi) : null,
        WasteWaterRecovery = settings.WasteWaterRecovery, WasteWaterUtilisation = settings.WasteWaterUtilisation, Battery = settings.Battery, Flat = settings.Flat,
        OverheatingLocation = settings.OverheatingLocation == OverheatingLocation.ByPostcode ? null : settings.OverheatingLocation.ToString(),
        Postcode = string.IsNullOrWhiteSpace(settings.Postcode) ? null : settings.Postcode, Shutters = settings.Shutters, NoisyAtNight = settings.NoisyAtNight
    };

    /// <summary>A project's energy settings read back, each within reason.</summary>
    private static void ReadEnergy(EnergyDto saved, EnergySettings settings)
    {
        settings.Overshading = ParseEnum(saved.Overshading, SapOvershading.AverageOrUnknown);
        settings.ShelteredSides = Math.Clamp(saved.ShelteredSides, 0, 4);
        settings.Bridging = ParseEnum(saved.Bridging, ThermalBridging.ReferenceDetails);
        settings.YValue = double.IsFinite(saved.YValue) && saved.YValue is >= 0 and <= 1 ? saved.YValue : 0.15;
        foreach (var (code, psi) in saved.Psi ?? new Dictionary<string, double>())
            if (double.IsFinite(psi) && psi is > -1 and < 5) settings.Psi[code] = psi;
        settings.WasteWaterRecovery = double.IsFinite(saved.WasteWaterRecovery) && saved.WasteWaterRecovery is >= 0 and <= 1 ? saved.WasteWaterRecovery : 0;
        settings.WasteWaterUtilisation = double.IsFinite(saved.WasteWaterUtilisation) && saved.WasteWaterUtilisation is > 0 and <= 1 ? saved.WasteWaterUtilisation : 0.98;
        settings.Battery = double.IsFinite(saved.Battery) && saved.Battery is >= 0 and <= 100 ? saved.Battery : 0;
        settings.Flat = saved.Flat;
        settings.OverheatingLocation = ParseEnum(saved.OverheatingLocation, OverheatingLocation.ByPostcode);
        settings.Postcode = (saved.Postcode ?? string.Empty).Trim();
        settings.Shutters = saved.Shutters;
        settings.NoisyAtNight = saved.NoisyAtNight;
    }

    private static BimDocument FromDto(ProjectFileDto dto)
    {
        var document = new BimDocument();

        document.ProjectInformation.Name = dto.Project.Name;
        document.ProjectInformation.Number = dto.Project.Number;
        document.ProjectInformation.Client = dto.Project.Client;
        document.ProjectInformation.Address = dto.Project.Address;
        document.ProjectInformation.BuildingType = dto.Project.BuildingType;
        document.ProjectInformation.ClearanceStandard = ParseEnum(dto.Project.ClearanceStandard, ClearanceStandard.ApprovedDocumentM);

        // Written before a project had units of its own: a US project's are feet and inches.
        document.ProjectInformation.Units = ParseEnum(dto.Project.Units, document.ProjectInformation.ClearanceStandard.IsUs() ? UnitSystem.Imperial : UnitSystem.Metric);
        document.ProjectInformation.Sprinklered = dto.Project.Sprinklered;
        document.ProjectInformation.WaterOptionalRequirement = dto.Project.WaterOptionalRequirement;
        document.ProjectInformation.Ground = ParseEnum(dto.Project.Ground, GroundType.FirmClay);
        document.ProjectInformation.ClayShrinkage = ParseEnum(dto.Project.ClayShrinkage, ClayShrinkage.None);
        document.ProjectInformation.FrostDepth = double.IsFinite(dto.Project.FrostDepth) && dto.Project.FrostDepth > 0 ? dto.Project.FrostDepth : 0;
        document.ProjectInformation.GroundSnowLoad = double.IsFinite(dto.Project.GroundSnowLoad) && dto.Project.GroundSnowLoad >= 0 ? dto.Project.GroundSnowLoad : 20;
        document.ProjectInformation.WaterPressure = double.IsFinite(dto.Project.WaterPressure) && dto.Project.WaterPressure is > 0 and <= 200 ? dto.Project.WaterPressure : 40;
        document.ProjectInformation.WindPressure = double.IsFinite(dto.Project.WindPressure) && dto.Project.WindPressure > 0 && dto.Project.WindPressure <= 10 ? dto.Project.WindPressure : 1.0;
        document.ProjectInformation.BasicWindSpeed = double.IsFinite(dto.Project.BasicWindSpeed) && dto.Project.BasicWindSpeed is > 0 and <= 300 ? dto.Project.BasicWindSpeed : 0;
        document.ProjectInformation.SiteAltitude = double.IsFinite(dto.Project.SiteAltitude) && dto.Project.SiteAltitude is >= -500 and <= 5000 ? dto.Project.SiteAltitude : 0;
        document.ProjectInformation.Terrain = ParseEnum(dto.Project.Terrain, WindTerrain.OpenCountry);
        document.ProjectInformation.HeatingLocation = ParseEnum(dto.Project.HeatingLocation, HeatingLocation.London);
        document.ProjectInformation.WinterDesignTemperature = dto.Project.WinterDesignTemperature is { } winter && double.IsFinite(winter) && winter is > -80 and < 80 ? winter : null;
        document.ProjectInformation.AirPermeability = dto.Project.AirPermeability is { } permeability && double.IsFinite(permeability) && permeability is > 0 and <= 100
            ? permeability : HeatLoss.LimitingAirPermeability;
        document.ProjectInformation.SummerDesignTemperature = dto.Project.SummerDesignTemperature is { } summer && double.IsFinite(summer) && summer is > 40 and < 140 ? summer : null;
        document.ProjectInformation.SummerWetBulb = dto.Project.SummerWetBulb is { } wet && double.IsFinite(wet) && wet is > 20 and < 100 ? wet : null;
        document.ProjectInformation.DailyRange = dto.Project.DailyRange is { } range && double.IsFinite(range) && range is >= 0 and <= 60 ? range : 20;
        document.ProjectInformation.Latitude = dto.Project.Latitude is { } latitude && double.IsFinite(latitude) && latitude is >= 0 and <= 70 ? latitude : 40;
        document.ProjectInformation.TrueNorth = double.IsFinite(dto.Project.TrueNorth) ? (dto.Project.TrueNorth % 360 + 360) % 360 : 0;
        document.ProjectInformation.PlanningMapNote = dto.Project.PlanningMapNote ?? string.Empty;
        if (double.IsFinite(dto.Project.SurveyOffsetX) && double.IsFinite(dto.Project.SurveyOffsetY))
            document.ProjectInformation.SurveyOffset = new Vector2D(dto.Project.SurveyOffsetX, dto.Project.SurveyOffsetY);
        document.ProjectInformation.DuctRun = ParseEnum(dto.Project.DuctRun, DuctRun.ConditionedSpace);
        document.ProjectInformation.DuctInsulation = dto.Project.DuctInsulation is { } r && double.IsFinite(r) && r is >= 0 and <= 30 ? r : 8;
        document.ProjectInformation.DuctsSealed = dto.Project.DuctsSealed ?? true;
        if (dto.Project.Energy is { } energy) ReadEnergy(energy, document.ProjectInformation.Energy);
        if (dto.Project.Render is { } render) ReadRender(render, document.ProjectInformation.Render);
        if (dto.Project.Daylight is { } daylight) ReadDaylight(daylight, document.ProjectInformation.Daylight);

        // A level standing on a floor is at its top; the height saved with it is where it was then.
        foreach (var level in dto.Levels)
            document.AddLevel(new Level { Id = level.Id, Name = level.Name, Elevation = level.Elevation, StandsOn = level.StandsOn });

        foreach (var material in dto.Materials)
        {
            var read = new Material(material.Name)
            {
                Id = material.Id,
                Density = material.Density,
                ThermalConductivity = material.ThermalConductivity,
                SurfaceColour = ParseColour(material.SurfaceColour, new ColourRgb(0x9A, 0x9A, 0x9A)),
                CutColour = ParseColour(material.CutColour, new ColourRgb(0x7A, 0x7A, 0x7A)),
                Manufacturer = material.Manufacturer,
                ClassificationCode = material.ClassificationCode,
                CostPerCubicMetre = material.CostPerCubicMetre,
                Keynote = material.Keynote ?? string.Empty
            };

            // Its face as saved; a material saved before patterns laid as its name says.
            if (material.Pattern is null) SurfacePatterns.Settle(read);
            else
            {
                read.Pattern = ParseEnum(material.Pattern, SurfacePattern.None);
                read.UnitLength = Finite(material.UnitLength, 0, 100000);
                read.UnitHeight = Finite(material.UnitHeight, 0, 100000);
                read.JointWidth = Finite(material.JointWidth, 0, 1000);
                read.JointColour = ParseColour(material.JointColour, new ColourRgb(0xC8, 0xC2, 0xB4));
                read.Reflectance = Finite(material.Reflectance, 0, 1);
            }

            document.AddMaterial(read);
        }

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
                Separating = type.Separating,
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
                CoarseScaleFillColour = ParseColour(type.CoarseScaleFillColour, new ColourRgb(0x8A, 0x93, 0xA1)),

                // Built of logs, where it was; a course or overhang that is not a size is the usual one.
                Log = type.LogShape is null ? null : new LogWall(ParseEnum(type.LogShape, LogShape.Round),
                    type.LogCourse is > 0 and var course && double.IsFinite(course) ? course : LogWall.Default.CourseHeight,
                    type.LogOverhang is >= 0 and var overhang && double.IsFinite(overhang) ? overhang : LogWall.Default.Overhang)
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

            // Bands of other materials in its layers; one on a layer it has not got, or upside down, is dropped.
            loaded.Bands.AddRange((type.Bands ?? new List<WallBandDto>())
                .Select(band => new WallBand(band.Layer, band.Bottom, Finite(band.Top), band.MaterialId))
                .Where(band => WallBands.Problem(new[] { band }, loaded.Structure.Layers.Count) is null));
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

        foreach (var style in dto.DimensionTypes)
            document.AddType(new DimensionType(string.IsNullOrWhiteSpace(style.Name) ? "Linear" : style.Name)
            {
                Id = style.Id, Tick = ParseEnum(style.Tick, DimensionTick.Tick), TextSize = Within(style.TextSize, 0.5, 20, 2.5),
                TextOffset = Within(style.TextOffset, 0, 10, 1), TextAlongLine = style.TextAlongLine, WitnessGap = Within(style.WitnessGap, 0, 20, 2),
                WitnessExtension = Within(style.WitnessExtension, 0, 20, 2), LineExtension = Within(style.LineExtension, 0, 20, 2),
                Units = ParseEnum(style.Units, DimensionUnits.Millimetres), Decimals = Math.Clamp(style.Decimals, 0, 4), ShowUnits = style.ShowUnits,
                AngleDecimals = Math.Clamp(style.AngleDecimals, 0, 4)
            });

        foreach (var fascia in dto.FasciaTypes)
            document.AddType(new FasciaType(fascia.Name)
            {
                Id = fascia.Id, TypeMark = fascia.TypeMark, Thickness = PositiveOr(fascia.Thickness, 25),
                Depth = double.IsFinite(fascia.Depth) ? Math.Max(0, fascia.Depth) : 0,
                MaterialId = fascia.MaterialId, Cost = fascia.Cost, Description = fascia.Description,

                // A board saved before fascias had profiles was the moulded one.
                Profile = ParseEnum(fascia.Profile, FasciaProfile.Moulded)
            });

        foreach (var type in dto.RoofWindowTypes)
            document.AddType(new RoofWindowType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Width = PositiveOr(type.Width, 780), Height = PositiveOr(type.Height, 980),
                FrameWidth = PositiveOr(type.FrameWidth, 70), Upstand = double.IsFinite(type.Upstand) && type.Upstand >= 0 ? type.Upstand : 90,
                Operation = ParseEnum(type.Operation, RoofWindowOperation.CentrePivot),
                FrameMaterialId = type.FrameMaterialId, GlassMaterialId = type.GlassMaterialId, Cost = type.Cost, Description = type.Description,
                HeatTransferCoefficient = type.HeatTransferCoefficient is > 0 and < 20 ? type.HeatTransferCoefficient.Value : 1.4,
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient is >= 0 and <= 1 ? type.SolarHeatGainCoefficient.Value : 0.5
            });

        foreach (var type in dto.StairTypes)
            document.AddType(new StairType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Construction = ParseEnum(type.Construction, StairConstruction.Timber),
                MaxRiser = double.IsFinite(type.MaxRiser) && type.MaxRiser >= 100 ? type.MaxRiser : 200,
                Going = double.IsFinite(type.Going) && type.Going >= 150 ? type.Going : 225,
                Nosing = double.IsFinite(type.Nosing) && type.Nosing >= 0 ? type.Nosing : 20,
                TreadThickness = double.IsFinite(type.TreadThickness) && type.TreadThickness >= 15 ? type.TreadThickness : 30,
                OpenRisers = type.OpenRisers, RiserBars = type.RiserBars,
                Support = ParseEnum(type.Support, StairSupport.ClosedStrings), Balustrade = ParseEnum(type.Balustrade, StairBalustrade.Balusters),
                HandrailProfile = ParseEnum(type.HandrailProfile, HandrailProfile.Round),
                HandrailWidth = type.HandrailWidth is >= 20 and <= 150 ? type.HandrailWidth.Value : 44,
                HandrailDepth = type.HandrailDepth is >= 20 and <= 150 ? type.HandrailDepth.Value : 39,
                WallClearance = type.WallClearance is >= 20 and <= 150 ? type.WallClearance.Value : 60,
                HandrailExtension = double.IsFinite(type.HandrailExtension) ? Math.Clamp(type.HandrailExtension, 0, 1000) : 0,
                HandrailEnds = ParseEnum(type.HandrailEnds, HandrailEnds.ReturnToWall),
                GuardHeight = type.GuardHeight is >= 700 and <= 1500 ? type.GuardHeight.Value : 920,
                LowerRail = type.LowerRail, NosingProfile = ParseEnum(type.NosingProfile, NosingProfile.Square),
                RiserMaterialId = type.RiserMaterialId, StringMaterialId = type.StringMaterialId, RailMaterialId = type.RailMaterialId, BalusterMaterialId = type.BalusterMaterialId,
                HandrailHeight = double.IsFinite(type.HandrailHeight) && type.HandrailHeight >= 700 ? type.HandrailHeight : 900,
                BalusterSpacing = double.IsFinite(type.BalusterSpacing) && type.BalusterSpacing >= 60 ? type.BalusterSpacing : 120,
                Colour = ParseColour(type.Colour, new ColourRgb(0xC8, 0xA8, 0x7A)), MaterialId = type.MaterialId,
                Cost = type.Cost, Description = type.Description ?? string.Empty
            });

        // A project saved before it had ramps is given the usual ones.
        if (dto.RampTypes.Count == 0)
            foreach (var type in RampType.Library()) document.AddType(type);
        foreach (var type in dto.RampTypes)
            document.AddType(new RampType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Construction = ParseEnum(type.Construction, RampConstruction.Solid),
                Thickness = Within(type.Thickness, 50, 1000, 150), KerbHeight = Within(type.KerbHeight, 0, 300, 100), KerbWidth = Within(type.KerbWidth, 50, 500, 100),
                HandrailHeight = Within(type.HandrailHeight, 600, 1200, 900), HandrailDiameter = Within(type.HandrailDiameter, 20, 80, 42),
                HandrailExtension = Within(type.HandrailExtension, 0, 1000, 300), LowerRail = type.LowerRail, Guarding = ParseEnum(type.Guarding, RampGuarding.Balusters),
                PostSpacing = Within(type.PostSpacing, 300, 3000, 1500), LongestFlight = Within(type.LongestFlight, 500, 50000, 0),
                Colour = ParseColour(type.Colour, new ColourRgb(0xB5, 0xB3, 0xAE)),
                MaterialId = type.MaterialId, RailMaterialId = type.RailMaterialId, Cost = type.Cost, Description = type.Description ?? string.Empty
            });

        // A project saved before it had ducts is given the usual duct types.
        if (dto.DuctTypes is null or { Count: 0 })
            foreach (var type in DuctType.Library()) document.AddType(type);
        foreach (var type in dto.DuctTypes ?? new List<DuctTypeDto>())
            document.AddType(new DuctType(type.Name, ParseEnum(type.Material, DuctMaterial.GalvanisedSteel), ParseEnum(type.Shape, DuctShape.Round))
            {
                Id = type.Id, TypeMark = type.TypeMark ?? string.Empty, Inches = type.Inches, Cost = type.Cost, Description = type.Description ?? string.Empty
            });

        // A project saved before it had pipes is given the usual pipe types.
        if (dto.PipeTypes is null or { Count: 0 })
            foreach (var type in PipeType.Library()) document.AddType(type);
        foreach (var type in dto.PipeTypes ?? new List<PipeTypeDto>())
            document.AddType(new PipeType(type.Name, ParseEnum(type.Material, PipeMaterial.Copper))
            {
                Id = type.Id, TypeMark = type.TypeMark ?? string.Empty, Cost = type.Cost, Description = type.Description ?? string.Empty
            });

        // A project saved before it had beams is given the usual ones.
        if (dto.BeamTypes.Count == 0)
            foreach (var type in BeamType.Library()) document.AddType(type);
        foreach (var type in dto.BeamTypes)
        {
            double Size(double value, double least, double most, double usual) => double.IsFinite(value) && value >= least && value <= most ? value : usual;
            document.AddType(new BeamType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Section = ParseEnum(type.Section, BeamSection.Rectangular), Material = ParseEnum(type.Material, BeamMaterial.Timber),
                Depth = Size(type.Depth, 50, 2000, 225), Width = Size(type.Width, 20, 1000, 75),
                WebThickness = Size(type.WebThickness, 0, 100, 0), FlangeThickness = Size(type.FlangeThickness, 0, 100, 0),
                MaterialId = type.MaterialId, Cost = type.Cost, Description = type.Description ?? string.Empty
            });
        }

        // A project saved before it had railings is given the usual ones.
        if (dto.RailingTypes.Count == 0)
            foreach (var type in RailingType.Library()) document.AddType(type);
        foreach (var type in dto.RailingTypes)
        {
            double Size(double value, double least, double most, double usual) => double.IsFinite(value) && value >= least && value <= most ? value : usual;
            document.AddType(new RailingType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Height = Size(type.Height, 300, 2500, 1100), Infill = ParseEnum(type.Infill, RailingInfill.Balusters),
                TopRailProfile = ParseEnum(type.TopRailProfile, HandrailProfile.Rectangular), TopRailWidth = Size(type.TopRailWidth, 20, 200, 60),
                TopRailDepth = Size(type.TopRailDepth, 15, 200, 40), PostSize = Size(type.PostSize, 20, 200, 50), PostSpacing = Size(type.PostSpacing, 300, 3000, 1500),
                BalusterSize = Size(type.BalusterSize, 8, 100, 16), BalusterSpacing = Size(type.BalusterSpacing, 40, 400, 110),
                BottomGap = Size(type.BottomGap, 0, 300, 50), GlassThickness = Size(type.GlassThickness, 6, 60, 17.5),
                Colour = ParseColour(type.Colour, new ColourRgb(0x3C, 0x3F, 0x44)), MaterialId = type.MaterialId, InfillMaterialId = type.InfillMaterialId,
                Cost = type.Cost, Description = type.Description ?? string.Empty,
                Use = ParseEnum(type.Use, RailingUse.Guarding), HasTopRail = type.HasTopRail ?? true, GlassLaminated = type.GlassLaminated ?? true,
                GlassJoint = Size(type.GlassJoint ?? 10, 0, 200, 10), ChannelHeight = Size(type.ChannelHeight ?? 100, 0, 300, 100),
                PanelThickness = Size(type.PanelThickness ?? 20, 2, 100, 20), IntermediateRails = Math.Clamp(type.IntermediateRails ?? 0, 0, 10),
                Handrail = ParseEnum(type.Handrail, RailingHandrail.None), HandrailHeight = Size(type.HandrailHeight ?? 900, 300, 1500, 900),
                HandrailDiameter = Size(type.HandrailDiameter ?? 42, 20, 100, 42), HandrailClearance = Size(type.HandrailClearance ?? 50, 0, 200, 50),
                HandrailExtension = Size(type.HandrailExtension ?? 300, 0, 1000, 300), HandrailEnds = ParseEnum(type.HandrailEnds, HandrailEnds.Post),
                // Saved before types said their loads: as the library's type of the name says, or a balcony's.
                LineLoad = Size(type.LineLoad ?? Usual(type.Name)?.LineLoad ?? 0.74, 0, 10, 0.74),
                PointLoad = Size(type.PointLoad ?? Usual(type.Name)?.PointLoad ?? 0.5, 0, 10, 0.5)
            });
        }

        // A project saved before the library had them is given the railings added since - once:
        // one saved with any of them has had them, and those taken out stay out.
        if (dto.RailingTypes.Count > 0 && !dto.RailingTypes.Any(type => RailingType.Added.Contains(type.Name)))
        {
            var names = document.TypesOf<RailingType>().Select(type => type.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var type in RailingType.Library().Where(type => RailingType.Added.Contains(type.Name) && !names.Contains(type.Name))) document.AddType(type);
        }

        foreach (var type in dto.ChimneyTypes)
            document.AddType(new ChimneyType(type.Name)
            {
                Id = type.Id, TypeMark = type.TypeMark, Construction = ParseEnum(type.Construction, ChimneyConstruction.Masonry),
                Width = PositiveOr(type.Width, 450), Depth = PositiveOr(type.Depth, 450),
                FlueDiameter = double.IsFinite(type.FlueDiameter) && type.FlueDiameter >= 0 ? type.FlueDiameter : 185,
                Flues = type.Flues is >= 1 and <= 8 ? type.Flues : 1,
                MinimumHeight = double.IsFinite(type.MinimumHeight) && type.MinimumHeight >= 0 ? type.MinimumHeight : 0,
                Fireplace = ParseEnum(type.Fireplace, ChimneyFireplace.None),
                TemperatureClass = type.TemperatureClass is >= 80 and <= 1000 ? type.TemperatureClass : 600,
                PressureClass = string.IsNullOrWhiteSpace(type.PressureClass) ? "N1" : type.PressureClass,
                Wet = type.Wet, CorrosionClass = type.CorrosionClass is >= 1 and <= 3 ? type.CorrosionClass : 3,
                SootFireResistant = type.SootFireResistant,
                ClearanceToCombustibles = double.IsFinite(type.ClearanceToCombustibles) && type.ClearanceToCombustibles >= 0 ? type.ClearanceToCombustibles : 40,
                FireRating = type.FireRating ?? string.Empty, Cost = type.Cost, Description = type.Description
            });

        foreach (var soffit in dto.SoffitTypes)
            document.AddType(new SoffitType(soffit.Name)
            {
                Id = soffit.Id, TypeMark = soffit.TypeMark, Thickness = PositiveOr(soffit.Thickness, 12),
                MaterialId = soffit.MaterialId, Cost = soffit.Cost, Description = soffit.Description,

                // A board saved before soffits had kinds was a plain one.
                Board = ParseEnum(soffit.Board, SoffitBoard.Solid),
                FreeAirArea = double.IsFinite(soffit.FreeAirArea) && soffit.FreeAirArea > 0 ? soffit.FreeAirArea : 0
            });

        static double Within(double value, double low, double high, double otherwise) => double.IsFinite(value) && value >= low && value <= high ? value : otherwise;
        foreach (var eave in dto.EaveTypes ?? new List<EaveTypeDto>())
            document.AddType(new EaveType(eave.Name)
            {
                Id = eave.Id, TypeMark = eave.TypeMark ?? string.Empty, Kind = ParseEnum(eave.Kind, EaveKind.Closed),
                RafterWidth = Within(eave.RafterWidth, 20, 300, 47), RafterDepth = Within(eave.RafterDepth, 0, 600, 0),
                RafterSpacing = Within(eave.RafterSpacing, 70, 1200, 400), Tail = ParseEnum(eave.Tail, RafterTail.Plumb),
                DeckThickness = Within(eave.DeckThickness, 0, 50, 18), SoffitThickness = Within(eave.SoffitThickness, 0.1, 50, 12),
                Board = ParseEnum(eave.Board, SoffitBoard.Solid), FreeAirArea = Within(eave.FreeAirArea, 0, 1e6, 0),
                FriezeDepth = Within(eave.FriezeDepth, 0, 1000, 0), FriezeThickness = Within(eave.FriezeThickness, 5, 100, 22),
                Moulding = ParseEnum(eave.Moulding, EaveMoulding.None), MouldingSize = Within(eave.MouldingSize, 10, 200, 45),
                Bracket = ParseEnum(eave.Bracket, EaveBracket.None), BracketSpacing = Within(eave.BracketSpacing, 300, 6000, 1200),
                BracketHeight = Within(eave.BracketHeight, 50, 1500, 300), BracketDepth = Within(eave.BracketDepth, 0, 1500, 0),
                BracketWidth = Within(eave.BracketWidth, 20, 300, 75), Underside = ParseEnum(eave.Underside, EaveUnderside.Unprotected),
                MaterialId = eave.MaterialId, SoffitMaterialId = eave.SoffitMaterialId, Cost = eave.Cost, Description = eave.Description ?? string.Empty
            });

        foreach (var edgeType in dto.SlabEdgeTypes)
            document.AddType(new SlabEdgeType(edgeType.Name)
            {
                Id = edgeType.Id, TypeMark = edgeType.TypeMark ?? string.Empty, Shape = ParseEnum(edgeType.Shape, SlabEdgeShape.Thickened),
                Width = PositiveOr(edgeType.Width, 450), Depth = PositiveOr(edgeType.Depth, 450), FootWidth = PositiveOr(edgeType.FootWidth, 250),
                MaterialId = edgeType.MaterialId, Cost = edgeType.Cost, Description = edgeType.Description ?? string.Empty
            });

        foreach (var gutter in dto.GutterTypes)
            document.AddType(new GutterType(gutter.Name)
            {
                Id = gutter.Id, TypeMark = gutter.TypeMark, Shape = ParseEnum(gutter.Shape, GutterShape.HalfRound),
                Width = PositiveOr(gutter.Width, 125), Depth = PositiveOr(gutter.Depth, 75), WallThickness = PositiveOr(gutter.WallThickness, 4),
                MaterialId = gutter.MaterialId, Cost = gutter.Cost, Description = gutter.Description,
                HangerSpacing = double.IsFinite(gutter.HangerSpacing) && gutter.HangerSpacing >= 0 ? gutter.HangerSpacing : 600,
                LeafGuard = gutter.LeafGuard,
                DownpipeShape = ParseEnum(gutter.DownpipeShape, DownpipeShape.Round),
                DownpipeWidth = PositiveOr(gutter.DownpipeWidth, 68), DownpipeDepth = PositiveOr(gutter.DownpipeDepth, 68)
            });

        // Mullion types before the curtain wall types that put them on their lines.
        foreach (var mullion in dto.MullionTypes ?? new List<MullionTypeDto>())
            document.AddType(new MullionType(mullion.Name)
            {
                Id = mullion.Id, TypeMark = mullion.TypeMark ?? string.Empty, Shape = ParseEnum(mullion.Shape, MullionShape.Rectangular),
                Width1 = Within(mullion.Width1, 1, 500, 25), Width2 = Within(mullion.Width2, 1, 500, 25),
                Thickness = Within(mullion.Thickness, 5, 1000, 150), Offset = Within(mullion.Offset, -1000, 1000, 0),
                Leg1 = Within(mullion.Leg1, 10, 1000, 100), Leg2 = Within(mullion.Leg2, 10, 1000, 100),
                CapDepth = Within(mullion.CapDepth, 0, 200, 0), WallThickness = Within(mullion.WallThickness, 0, 50, 3),
                Material = ParseEnum(mullion.Material, MullionMaterial.Aluminium), MaterialId = mullion.MaterialId,
                Cost = mullion.Cost, Description = mullion.Description ?? string.Empty
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
                AutomaticallyEmbed = dtoCurtain.AutomaticallyEmbed,
                HeatTransferCoefficient = dtoCurtain.HeatTransferCoefficient is >= 0 and < 20 ? dtoCurtain.HeatTransferCoefficient.Value : 1.4,
                SafetyGlazing = dtoCurtain.SafetyGlazing ?? true,
                InteriorVertical = dtoCurtain.InteriorVertical,
                BorderVerticalStart = dtoCurtain.BorderVerticalStart,
                BorderVerticalEnd = dtoCurtain.BorderVerticalEnd,
                InteriorHorizontal = dtoCurtain.InteriorHorizontal,
                BorderHorizontalBottom = dtoCurtain.BorderHorizontalBottom,
                BorderHorizontalTop = dtoCurtain.BorderHorizontalTop,
                JoinCondition = ParseEnum(dtoCurtain.JoinCondition, CurtainJoin.VerticalContinuous),
                MullionAnchors = dtoCurtain.MullionAnchors,
                Splices = ParseEnum(dtoCurtain.Splices, MullionSplices.None),
                SpliceAbove = dtoCurtain.SpliceAbove is >= 0 and <= 2000 ? dtoCurtain.SpliceAbove.Value : 300,
                StackJoint = dtoCurtain.StackJoint is >= 1 and <= 100 ? dtoCurtain.StackJoint.Value : 20,
                AnchorCapacity = double.IsFinite(dtoCurtain.AnchorCapacity) && dtoCurtain.AnchorCapacity is >= 0 and <= 500 ? dtoCurtain.AnchorCapacity : 0
            };
            curtain.TakeDepth(document);

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
                TrimProjectionInterior = type.TrimProjectionInterior is >= 0 and var inside ? inside : 20,
                SelfClosing = type.SelfClosing ?? false
            };

            ReadOpeningType(doorType, type);

            // Saved before safety glazing was said: a glazed door's glass taken as the safety glass it is sold with.
            if (type.SafetyGlazing is null && Doors.Glazed(doorType)) doorType.SafetyGlazing = true;
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
                SolarHeatGainCoefficient = type.SolarHeatGainCoefficient,
                LightTransmittance = type.LightTransmittance is >= 0 and <= 1 ? type.LightTransmittance.Value : 0.7,
                TrickleVent = type.TrickleVent is > 0 and < 1e6 ? type.TrickleVent.Value : 0,

                // Saved before divided windows: one light, no transom.
                Lights = type.Lights is > 0 and <= 8 ? type.Lights.Value : 1,
                LightPattern = type.LightPattern ?? string.Empty,
                TransomHeight = double.IsFinite(type.TransomHeight) && type.TransomHeight > 0 ? type.TransomHeight : 0,
                Fanlight = ParseEnum(type.Fanlight, FanlightOperation.Fixed),
                MullionWidth = type.MullionWidth is >= 20 and <= 250 ? type.MullionWidth.Value : 70
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

                // Paint and split faces; a bound that is not a number is left open.
                FaceRegions = (wall.FaceRegions ?? new List<WallFaceRegionDto>())
                    .Select(region => new WallFaceRegion(ParseEnum(region.Face, WallFace.Exterior), Finite(region.From), Finite(region.To),
                        Finite(region.Bottom), Finite(region.Top), region.MaterialId))
                    .ToList(),
                CurtainGrid = wall.CurtainVerticals is { } verticals && wall.CurtainHorizontals is { } horizontals
                    ? new CurtainGrid(verticals.Where(double.IsFinite).ToList(), horizontals.Where(double.IsFinite).ToList(),
                        wall.CurtainRemoved?
                            .Where(segment => double.IsFinite(segment.Line) && double.IsFinite(segment.From) && double.IsFinite(segment.To))
                            .Select(segment => new CurtainSegment(segment.Vertical, segment.Line, segment.From, segment.To))
                            .ToList() is { Count: > 0 } removed ? removed : null)
                    : null,
                CurtainGlass = ParseEnum(wall.CurtainGlass, CurtainGlass.Clear),
                CurtainPanels = wall.CurtainPanels?
                    .Where(p => p.Column >= 0 && p.Row >= 0)
                    .Select(p => new CurtainPanelOverride(
                        // Saved when only doors could be panels, the type was called the door type.
                        p.Column, p.Row, ParseEnum(p.Kind, CurtainPanelKind.Glazed), p.OpeningTypeId ?? p.DoorTypeId, ParseEnum(p.Glass, CurtainGlass.Clear),
                        p.FlipHand, p.FlipFacing, p.IsOpen))
                    .ToList(),
                CurtainMullions = wall.CurtainMullions?
                    .Where(m => double.IsFinite(m.Line) && double.IsFinite(m.At))
                    .Select(m => new CurtainMullionOverride(m.Vertical, m.Line, m.At, m.TypeId))
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
            var wall = document.Find<Wall>(saved.Id);
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
            slabType.Separating = type.Separating == true;
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
            if (slab.Holes is { } sketchedHoles)
                element.SetHoles(sketchedHoles.Select(hole => Enumerable.Range(0, hole.Count / 2).Select(i => new Point2D(hole[2 * i], hole[2 * i + 1]))));
            if (element is Floor spanned && slab.SpanDirection is { } span && double.IsFinite(span)) spanned.SpanDirection = Floors.Normalised(span);

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
                        return new RoofOpening(points, arcs) { Square = hole.Square };
                    }));
                }
                pitched.FasciaDepth = slab.RoofFasciaDepth;
                pitched.HighLevelVents = slab.RoofHighLevelVents;
                pitched.SolarPeakPower = slab.RoofSolarPeakPower is > 0 and <= 1000 ? slab.RoofSolarPeakPower.Value : 0;
                pitched.SolarOvershading = ParseEnum(slab.RoofSolarOvershading, PvOvershading.NoneOrVeryLittle);
                pitched.DrainageFall = double.IsFinite(slab.RoofDrainageFall) && slab.RoofDrainageFall >= 10 ? slab.RoofDrainageFall : RoofDrainage.DesignFall;
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
                Comments = saved.Comments ?? string.Empty,
                ForServices = saved.ForServices == true
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
        var roofIds = document.All<Roof>().Select(roof => roof.Id).ToHashSet();
        foreach (var saved in dto.RoofEdgeSweeps.Where(saved => roofIds.Contains(saved.RoofId)))
        {
            RoofEdgeSweep sweep = saved.Kind switch
            {
                "Gutter" => new Gutter { Id = saved.Id },
                "Soffit" => new Soffit { Id = saved.Id },
                "Eave" => new Eave { Id = saved.Id, FireStopped = saved.FireStopped ?? true, FireBlocked = saved.FireBlocked ?? false },
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

        // A roof window whose roof is gone has nothing to sit in.
        foreach (var saved in dto.RoofWindows.Where(saved => roofIds.Contains(saved.RoofId) && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new RoofWindow
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, RoofId = saved.RoofId,
                Location = new Point2D(saved.X, saved.Y), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty,

                // Its own size and make-up, where it had them; anything unreadable takes its type's.
                Width = Own(saved.Width), Height = Own(saved.Height), FrameWidth = Own(saved.FrameWidth), Upstand = Own(saved.Upstand),
                FrameMaterialId = saved.FrameMaterialId is { } frame && document.FindMaterial(frame) is not null ? frame : null,
                GlassMaterialId = saved.GlassMaterialId is { } glass && document.FindMaterial(glass) is not null ? glass : null
            });

        static double? Own(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;

        // A slab edge is on its floor; one whose floor is gone runs along nothing.
        var edgeFloors = document.All<Floor>().Select(floor => floor.Id).ToHashSet();
        foreach (var saved in dto.SlabEdges.Where(saved => edgeFloors.Contains(saved.FloorId)))
        {
            var slabEdge = new SlabEdge
            {
                Id = saved.Id, TypeId = saved.TypeId, FloorId = saved.FloorId, LevelId = saved.LevelId,
                HorizontalOffset = double.IsFinite(saved.HorizontalOffset) ? saved.HorizontalOffset : 0,
                VerticalOffset = double.IsFinite(saved.VerticalOffset) ? saved.VerticalOffset : 0,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            for (var i = 0; i + 3 < saved.Edges.Count; i += 4)
                slabEdge.Edges.Add((new Point2D(saved.Edges[i], saved.Edges[i + 1]), new Point2D(saved.Edges[i + 2], saved.Edges[i + 3])));
            document.Add(slabEdge);
        }

        // A roof drain is in its roof; one whose roof is gone has nothing to drain.
        var drainRoofs = document.All<Roof>().Select(roof => roof.Id).ToHashSet();
        foreach (var saved in dto.RoofDrains.Where(saved => drainRoofs.Contains(saved.RoofId) && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new RoofDrain
            {
                Id = saved.Id, RoofId = saved.RoofId, LevelId = saved.LevelId, Location = new Point2D(saved.X, saved.Y),
                OutletDiameter = saved.OutletDiameter is > 0 and var size && double.IsFinite(size) ? size : 100,
                Discharge = ParseEnum(saved.Discharge, DrainDischarge.Internal),
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        foreach (var saved in dto.Chimneys.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
        {
            var chimney = new Chimney
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, Location = new Point2D(saved.X, saved.Y),
                Width = double.IsFinite(saved.Width) && saved.Width >= 60 ? saved.Width : 450,
                Depth = double.IsFinite(saved.Depth) && saved.Depth >= 60 ? saved.Depth : 450,
                Angle = double.IsFinite(saved.Angle) ? saved.Angle : 0,
                BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0,
                Flues = saved.Flues is >= 1 and <= 8 ? saved.Flues : 1,
                Rule = ParseEnum(saved.Rule, ChimneyRule.ApprovedDocumentJ),
                ExtraHeight = double.IsFinite(saved.ExtraHeight) && saved.ExtraHeight >= 0 ? saved.ExtraHeight : 0,

                // A chimney saved before there were fireplaces had none.
                Fireplace = ParseEnum(saved.Fireplace, ChimneyFireplace.None),
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };

            // Its fireplaces on the floors above - one on a floor since deleted is gone with it.
            foreach (var above in saved.FireplacesAbove.Where(above => document.FindLevel(above.LevelId) is not null))
                if (ParseEnum(above.Fireplace, ChimneyFireplace.None) is var kind and not ChimneyFireplace.None)
                    chimney.FireplacesAbove[above.LevelId] = kind;

            document.Add(chimney);
        }

        // A stair, from its foot and the way it goes up; one whose top level has gone stands to its own height.
        foreach (var saved in dto.Stairs.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new Stair
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, Start = new Point2D(saved.X, saved.Y),
                Angle = double.IsFinite(saved.Angle) ? saved.Angle : 90,
                Shape = ParseEnum(saved.Shape, StairShape.Straight), Corner = ParseEnum(saved.Corner, StairCorner.Landing), TurnsLeft = saved.TurnsLeft,
                Width = double.IsFinite(saved.Width) && saved.Width >= 500 ? saved.Width : 900,
                RisersBeforeTurn = saved.RisersBeforeTurn is > 0 ? saved.RisersBeforeTurn : null,
                Well = double.IsFinite(saved.Well) && saved.Well >= 0 ? saved.Well : 100,
                InnerRadius = double.IsFinite(saved.InnerRadius) && saved.InnerRadius >= 0 ? saved.InnerRadius : 0,
                PathLengths = saved.PathLengths?.Where(double.IsFinite).Select(length => Math.Max(0, length)).ToList() ?? new List<double>(),
                PathTurns = saved.PathTurns?.Select(turn => double.IsFinite(turn) ? Math.Clamp(turn, -90, 90) : 0).ToList() ?? new List<double>(),
                RepeatToLevelId = saved.RepeatToLevelId is { } repeat && document.FindLevel(repeat) is not null ? repeat : null,
                ShowRiserNumbers = saved.ShowRiserNumbers, ShowFlightDimensions = saved.ShowFlightDimensions,
                Use = ParseEnum(saved.Use, StairUse.Private), LoftConversion = saved.LoftConversion, Outside = saved.Outside, BottomStep = ParseEnum(saved.BottomStep, BottomStep.Plain),
                BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0,
                TopLevelId = saved.TopLevelId is { } top && document.FindLevel(top) is not null ? top : null,
                TopOffset = double.IsFinite(saved.TopOffset) ? saved.TopOffset : 0,
                UnconnectedHeight = double.IsFinite(saved.UnconnectedHeight) && saved.UnconnectedHeight >= 300 ? saved.UnconnectedHeight : 2600,
                Railings = ParseEnum(saved.Railings, StairRailings.Auto), CutsFloorAbove = saved.CutsFloorAbove, CutsRoof = saved.CutsRoof,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // A ramp, from its anchored end and the way it goes up there; one whose top level has gone rises its own height.
        foreach (var saved in dto.Ramps.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new Ramp
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, Start = new Point2D(saved.X, saved.Y),
                Angle = double.IsFinite(saved.Angle) ? saved.Angle : 90, Anchor = ParseEnum(saved.Anchor, RampAnchor.Foot),
                Shape = ParseEnum(saved.Shape, RampShape.Straight), TurnsLeft = saved.TurnsLeft,
                Width = Within(saved.Width, 600, 6000, 1200), Gap = Within(saved.Gap, 0, 5000, 300),
                PathLengths = saved.PathLengths?.Where(double.IsFinite).Select(length => Math.Max(0, length)).ToList() ?? new List<double>(),
                PathTurns = saved.PathTurns?.Select(turn => double.IsFinite(turn) ? Math.Clamp(turn, -90, 90) : 0).ToList() ?? new List<double>(),
                Gradient = Within(saved.Gradient, 4, 100, 15), BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0,
                TopLevelId = saved.TopLevelId is { } top && document.FindLevel(top) is not null ? top : null,
                TopOffset = double.IsFinite(saved.TopOffset) ? saved.TopOffset : 0,
                UnconnectedHeight = Within(saved.UnconnectedHeight, Ramps.MinimumRise, 10000, 450),
                TopLanding = Within(saved.TopLanding, 0, 10000, 1200), BottomLanding = Within(saved.BottomLanding, 0, 10000, 0),
                Building = ParseEnum(saved.Building, RailingBuilding.Dwelling), Outside = saved.Outside, Railings = ParseEnum(saved.Railings, StairRailings.Auto),
                ClosedUnderneath = saved.ClosedUnderneath,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // The ground through its spot levels.
        foreach (var saved in dto.Toposurfaces.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            static List<Point3D> Read(List<double>? saved)
            {
                var numbers = saved ?? new List<double>();
                return Enumerable.Range(0, numbers.Count / 3).Select(i => new Point3D(numbers[3 * i], numbers[3 * i + 1], numbers[3 * i + 2]))
                    .Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z)).ToList();
            }

            var points = Read(saved.Points);
            var ground = new Toposurface
            {
                Id = saved.Id, LevelId = saved.LevelId, MaterialId = saved.MaterialId,
                ContourInterval = double.IsFinite(saved.ContourInterval) && saved.ContourInterval >= 50 ? saved.ContourInterval : 250,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            ground.SetPoints(points);
            ground.SetExistingPoints(Read(saved.ExistingPoints));
            document.Add(ground);
        }

        // Regions of the ground, and joist layouts, by their outlines; one with fewer than three corners is nothing.
        static List<Point2D> Outline(List<double>? saved)
        {
            var numbers = (saved ?? new List<double>()).Where(double.IsFinite).ToList();
            return Enumerable.Range(0, numbers.Count / 2).Select(i => new Point2D(numbers[2 * i], numbers[2 * i + 1])).ToList();
        }

        foreach (var saved in dto.GroundRegions.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var outline = Outline(saved.Boundary);
            if (outline.Count < 3) continue;
            var region = new GroundRegion
            {
                Id = saved.Id, LevelId = saved.LevelId, Surface = ParseEnum(saved.Surface, GroundSurface.Lawn), MaterialId = saved.MaterialId,
                Approach = saved.Approach, Offset = Within(saved.Offset, -3000, 3000, 0), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            if (saved.PathLine is { Count: >= 4 } path && double.IsFinite(saved.PathWidth) && saved.PathWidth >= 300)
                region.SetPath(Enumerable.Range(0, path.Count / 2).Select(i => new Point2D(path[2 * i], path[2 * i + 1])), saved.PathWidth);
            region.SetBoundary(outline);
            document.Add(region);
        }

        foreach (var saved in dto.Masses.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var mass = new Mass
            {
                Id = saved.Id, LevelId = saved.LevelId, BaseOffset = Within(saved.BaseOffset, -100000, 100000, 0),
                Height = double.IsFinite(saved.Height) && saved.Height >= 300 ? saved.Height : 9000,
                Top = ParseEnum(saved.Top, MassTop.Flat), Pitch = Within(saved.Pitch, 1, 75, 35), TopScale = Within(saved.TopScale, 0, 0.999, 0.5),
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            if (saved.Round && double.IsFinite(saved.Radius) && saved.Radius > 0) mass.SetCircle(new Point2D(saved.CentreX, saved.CentreY), saved.Radius);
            else
            {
                var outline = Outline(saved.Boundary);
                if (outline.Count < 3) continue;
                mass.SetBoundary(outline);
            }

            if (saved.FloorLevels is { } levels) mass.FloorLevels.AddRange(levels.Where(id => document.FindLevel(id) is not null));
            if (saved.Links is { } links)
                mass.Links.AddRange(links.Select(link => new MassLink(link.ElementId, ParseEnum(link.Kind, MassLinkKind.Wall), link.Face, link.LevelId)));
            document.Add(mass);
        }

        foreach (var saved in dto.SitePads.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var outline = Outline(saved.Boundary);
            if (outline.Count < 3) continue;
            var pad = new SitePad
            {
                Id = saved.Id, LevelId = saved.LevelId, HeightOffset = Within(saved.HeightOffset, -20000, 20000, -150),
                Fall = saved.Fall >= 1 && saved.Fall <= 1000 ? saved.Fall : 0, FallDirection = double.IsFinite(saved.FallDirection) ? saved.FallDirection : 270,
                Batter = Within(saved.Batter, 0, 100, 2), Surface = ParseEnum(saved.Surface, GroundSurface.Paving), MaterialId = saved.MaterialId,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            pad.SetBoundary(outline);
            document.Add(pad);
        }

        foreach (var saved in dto.PropertyLines.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var outline = Outline(saved.Boundary);
            if (outline.Count < 3) continue;
            var line = new PropertyLine
            {
                Id = saved.Id, LevelId = saved.LevelId, Setback = Within(saved.Setback, 0, 100000, 0), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty,
                Planning = ParseEnum(saved.Planning, PlanningEdge.None)
            };
            line.SetBoundary(outline);
            document.Add(line);
        }

        foreach (var saved in dto.SpotElevations.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new SpotElevation
            {
                Id = saved.Id, LevelId = saved.LevelId, Location = new Point2D(saved.X, saved.Y), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        foreach (var saved in dto.BeamSystems.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var outline = Outline(saved.Boundary);
            if (outline.Count < 3) continue;
            var system = new BeamSystem
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId,
                Direction = saved.Direction is { } way && double.IsFinite(way) ? way : null,
                Spacing = double.IsFinite(saved.Spacing) && saved.Spacing >= 100 ? saved.Spacing : 400,
                Offset = double.IsFinite(saved.Offset) ? saved.Offset : 0, Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            };
            system.SetBoundary(outline);
            document.Add(system);
        }

        // A beam between its ends; one with no length is nothing.
        foreach (var saved in dto.Beams.Where(saved => document.FindLevel(saved.LevelId) is not null
                     && new[] { saved.StartX, saved.StartY, saved.EndX, saved.EndY }.All(double.IsFinite)))
        {
            var (start, end) = (new Point2D(saved.StartX, saved.StartY), new Point2D(saved.EndX, saved.EndY));
            if (start.DistanceTo(end) < 1) continue;
            document.Add(new Beam
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, Start = start, End = end,
                StartOffset = double.IsFinite(saved.StartOffset) ? saved.StartOffset : 0, EndOffset = double.IsFinite(saved.EndOffset) ? saved.EndOffset : 0,
                Justification = ParseEnum(saved.Justification, BeamJustification.Top), Usage = ParseEnum(saved.Usage, BeamUsage.Beam),
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });
        }

        // An imported drawing, placed and shown as it was.
        foreach (var saved in (dto.CadUnderlays ?? new List<CadUnderlayDto>()).Where(saved => document.FindLevel(saved.LevelId) is not null))
            if (ReadUnderlay(saved) is { } underlay) document.Add(underlay);

        // A linked model: where its file was, and where it goes.
        foreach (var saved in dto.ModelLinks ?? new List<ModelLinkDto>())
            document.Add(new BIMDesigner.Core.Coordination.ModelLink
            {
                Id = saved.Id, Path = saved.Path ?? string.Empty, RelativePath = saved.RelativePath ?? string.Empty,
                Positioning = ParseEnum(saved.Positioning, BIMDesigner.Core.Coordination.LinkPositioning.OriginToOrigin),
                Offset = new Point2D(double.IsFinite(saved.X) ? saved.X : 0, double.IsFinite(saved.Y) ? saved.Y : 0) - new Point2D(0, 0),
                Elevation = double.IsFinite(saved.Elevation) ? saved.Elevation : 0, Rotation = double.IsFinite(saved.Rotation) ? saved.Rotation : 0,
                Shown = saved.Shown, Halftone = saved.Halftone, Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // A room separator between its ends; one with no length is nothing.
        foreach (var saved in (dto.RoomSeparators ?? new List<RoomSeparatorDto>()).Where(saved => document.FindLevel(saved.LevelId) is not null
                     && new[] { saved.StartX, saved.StartY, saved.EndX, saved.EndY }.All(double.IsFinite)))
        {
            var (start, end) = (new Point2D(saved.StartX, saved.StartY), new Point2D(saved.EndX, saved.EndY));
            if (start.DistanceTo(end) < 1) continue;
            document.Add(new RoomSeparator
            {
                Id = saved.Id, LevelId = saved.LevelId, Start = start, End = end, PhaseCreated = ParseEnum(saved.PhaseCreated, DesignPhase.New),
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });
        }

        // A railing along its path; one with fewer than two points left is nothing.
        foreach (var saved in dto.Railings.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var numbers = (saved.Path ?? new List<double>()).Where(double.IsFinite).ToList();
            var path = Enumerable.Range(0, numbers.Count / 2).Select(i => new Point2D(numbers[2 * i], numbers[2 * i + 1])).ToList();
            if (path.Count < 2) continue;
            var railing = new Railing
            {
                Id = saved.Id, TypeId = saved.TypeId, LevelId = saved.LevelId, Closed = saved.Closed && path.Count >= 3,
                BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0, Building = ParseEnum(saved.Building, RailingBuilding.Dwelling),
                Outside = saved.Outside, Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty, HostOpeningId = saved.HostOpeningId
            };
            railing.SetPath(path, saved.Bulges ?? new List<double>());
            document.Add(railing);
        }

        // A wall shaped in plan, from its outline; one with no outline left is nothing.
        foreach (var saved in dto.PolygonWalls.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var points = (saved.Outline ?? new List<double>()).Where(double.IsFinite).ToList();
            var outline = Enumerable.Range(0, points.Count / 2).Select(i => new Point2D(points[2 * i], points[2 * i + 1])).ToList();
            if (outline.Count < 3) continue;

            document.Add(new PolygonWall
            {
                Id = saved.Id, LevelId = saved.LevelId, Kind = ParseEnum(saved.Kind, PolygonWallKind.Polygon), Outline = outline,
                Start = new Point2D(saved.StartX, saved.StartY), End = new Point2D(saved.EndX, saved.EndY),
                StartThickness = saved.StartThickness > 0 ? saved.StartThickness : 300, EndThickness = saved.EndThickness > 0 ? saved.EndThickness : 150,
                StraightSide = ParseEnum(saved.StraightSide, TrapezoidSide.Centre), MaterialId = saved.MaterialId,
                BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0,
                TopLevelId = saved.TopLevelId is { } top && document.FindLevel(top) is not null ? top : null,
                TopOffset = double.IsFinite(saved.TopOffset) ? saved.TopOffset : 0,
                UnconnectedHeight = double.IsFinite(saved.UnconnectedHeight) && saved.UnconnectedHeight > 0 ? saved.UnconnectedHeight : 3000,
                BaseAttachedTo = saved.BaseAttachedTo,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });
        }

        // A wall's frame; one whose wall is gone is nothing.
        var framedWalls = document.Walls.Select(wall => wall.Id).ToHashSet();
        foreach (var saved in dto.WallFramings.Where(saved => framedWalls.Contains(saved.HostId)))
            document.Add(new WallFraming
            {
                Id = saved.Id, HostId = saved.HostId, LevelId = saved.LevelId, Material = ParseEnum(saved.Material, FramingMaterial.Timber),
                Section = FramingSection.Named(saved.Section).Name,
                Spacing = double.IsFinite(saved.Spacing) && saved.Spacing >= 200 ? saved.Spacing : 600,
                TopPlates = saved.TopPlates is 1 or 2 ? saved.TopPlates : 2, NoggingRows = saved.NoggingRows is >= 0 and <= 4 ? saved.NoggingRows : 1,
                FromEnd = saved.FromEnd, Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // A part is a layer of its wall; one whose wall is gone is nothing.
        var partHosts = document.Walls.Select(wall => wall.Id).ToHashSet();
        foreach (var saved in dto.Parts.Where(saved => partHosts.Contains(saved.HostId) && saved.Layer >= 0))
            document.Add(new Part
            {
                Id = saved.Id, HostId = saved.HostId, LevelId = saved.LevelId, Layer = saved.Layer,
                From = Finite(saved.From), To = Finite(saved.To), Bottom = Finite(saved.Bottom), Top = Finite(saved.Top),
                Gap = double.IsFinite(saved.Gap) ? Math.Max(0, saved.Gap) : 0, MaterialId = saved.MaterialId, Excluded = saved.Excluded,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // A downpipe hangs off its gutter; one whose gutter is gone has nothing to drain.
        var gutterIds = document.All<Gutter>().Select(gutter => gutter.Id).ToHashSet();
        foreach (var saved in dto.Downpipes.Where(saved => gutterIds.Contains(saved.GutterId) && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new Downpipe
            {
                Id = saved.Id, GutterId = saved.GutterId, LevelId = saved.LevelId, Location = new Point2D(saved.X, saved.Y),
                Distributor = saved.Distributor, Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty
            });

        // A shaft stands on a level; one whose level is gone has nowhere to start from.
        foreach (var saved in dto.ShaftOpenings.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new ShaftOpening
            {
                Id = saved.Id, LevelId = saved.LevelId, Location = new Point2D(saved.X, saved.Y),
                Shape = ParseEnum(saved.Shape, ShaftShape.Rectangle),
                Width = double.IsFinite(saved.Width) && saved.Width >= Shafts.SmallestSize ? saved.Width : 600,
                Depth = double.IsFinite(saved.Depth) && saved.Depth >= Shafts.SmallestSize ? saved.Depth : 600,
                Angle = double.IsFinite(saved.Angle) ? saved.Angle : 0,
                BaseOffset = double.IsFinite(saved.BaseOffset) ? saved.BaseOffset : 0,
                TopLevelId = saved.TopLevelId is { } top && document.FindLevel(top) is not null ? top : null,
                TopOffset = double.IsFinite(saved.TopOffset) ? saved.TopOffset : 0,
                UnconnectedHeight = double.IsFinite(saved.UnconnectedHeight) && saved.UnconnectedHeight > 0 ? saved.UnconnectedHeight : Shafts.DefaultHeight,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty, ForServices = saved.ForServices == true
            });

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
                Structural = type.Structural,
                StructuralMaterial = ParseEnum(type.StructuralMaterial, BeamMaterial.Steel),
                FlangeThickness = double.IsFinite(type.FlangeThickness) && type.FlangeThickness > 0 ? type.FlangeThickness : 0,
                WebThickness = double.IsFinite(type.WebThickness) && type.WebThickness > 0 ? type.WebThickness : 0,
                WallThickness = double.IsFinite(type.WallThickness) && type.WallThickness > 0 ? type.WallThickness : 0,
                FireRating = type.FireRating ?? string.Empty,
                PreservativeTreated = type.PreservativeTreated,
                RustProtected = type.RustProtected,
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
            var slabs = document.All<Slab>().Select(slab => slab.Id).ToHashSet();

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
                Structural = column.Structural,
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

        // Footings: their types, then those under the walls and columns still there.
        foreach (var type in dto.FootingTypes ?? new List<FootingTypeDto>())
            document.AddType(new FootingType(type.Name, ParseEnum(type.Kind, FootingKind.Strip))
            {
                Id = type.Id, TypeMark = type.TypeMark ?? string.Empty, Width = Size(type.Width, 600), Length = Size(type.Length, 600),
                Thickness = Size(type.Thickness, 225), MaterialId = type.MaterialId, StemMaterialId = type.StemMaterialId,
                AssemblyCode = type.AssemblyCode ?? string.Empty, Cost = type.Cost, Description = type.Description ?? string.Empty
            });
        foreach (var footing in (dto.WallFootings ?? new List<WallFootingDto>()).Where(footing => document.Walls.Any(wall => wall.Id == footing.WallId)))
            document.Add(new WallFooting
            {
                Id = footing.Id, TypeId = footing.TypeId, LevelId = footing.LevelId, WallId = footing.WallId,
                Depth = double.IsFinite(footing.Depth) && footing.Depth > 0 ? footing.Depth : 1000,
                Eccentricity = double.IsFinite(footing.Eccentricity) ? footing.Eccentricity : 0,
                Mark = footing.Mark ?? string.Empty, Comments = footing.Comments ?? string.Empty
            });
        foreach (var pad in dto.PadFootings ?? new List<PadFootingDto>())
        {
            // A pad whose column is gone is left where it was, carrying nothing.
            var carried = pad.ColumnId is { } id && document.FindElement(id) is Column ? id : (Guid?)null;
            document.Add(new PadFooting
            {
                Id = pad.Id, TypeId = pad.TypeId, LevelId = pad.LevelId, ColumnId = carried,
                Location = new Point2D(double.IsFinite(pad.X) ? pad.X : 0, double.IsFinite(pad.Y) ? pad.Y : 0),
                Rotation = double.IsFinite(pad.Rotation) ? pad.Rotation : 0, Depth = double.IsFinite(pad.Depth) && pad.Depth > 0 ? pad.Depth : 1000,
                Mark = pad.Mark ?? string.Empty, Comments = pad.Comments ?? string.Empty
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
                AgainstWall = type.AgainstWall ?? ComponentType.BacksOntoWallByDefault(ParseEnum(type.Form, ComponentForm.Box)),
                Enclosure = ParseEnum(type.Enclosure, ShowerEnclosure.None),
                ExtractRate = type.ExtractRate is >= 0 and < 10000 ? type.ExtractRate.Value : 0,
                Efficacy = type.Efficacy is >= 0 and < 1000 ? type.Efficacy.Value : 0,
                WaterDemand = ParseEnum(type.WaterDemand, TreeWaterDemand.Unknown),
                StemDiameter = type.StemDiameter is >= 0 and < 10000 ? type.StemDiameter.Value : 0,
                HotWater = ParseEnum(type.HotWater, BoilerHotWater.Combi),
                Access = ParseEnum(type.Access, DrainAccessKind.InspectionChamber),
                HeatOutput = type.HeatOutput is >= 0 and < 1e7 ? type.HeatOutput.Value : TemplateHeat(type.Name, type.Width)?.HeatOutput ?? 0,
                CoolingOutput = type.CoolingOutput is >= 0 and < 1e7 ? type.CoolingOutput.Value : 0,
                GasInput = type.GasInput is >= 0 and < 1e7 ? type.GasInput.Value : TemplateElectric(type.Name, type.Form)?.GasInput ?? 0,
                Coverage = type.Coverage is >= 0 and < 1e5 ? type.Coverage.Value : TemplateElectric(type.Name, type.Form)?.Coverage ?? 0,
                KFactor = type.KFactor is >= 0 and < 1e4 ? type.KFactor.Value : TemplateElectric(type.Name, type.Form)?.KFactor ?? 0,
                Temperature = type.Temperature is >= 0 and < 1000 ? type.Temperature.Value : TemplateElectric(type.Name, type.Form)?.Temperature ?? 0,
                SupplyPressure = type.SupplyPressure is >= 0 and < 1000 ? type.SupplyPressure.Value : 0,
                HeatExponent = type.HeatExponent is >= 1 and <= 2 ? type.HeatExponent.Value : TemplateHeat(type.Name, type.Width)?.HeatExponent ?? 1.3,
                FloorOutput = type.FloorOutput is >= 0 and < 1000 ? type.FloorOutput.Value : 0,
                FloorOutputLimit = type.FloorOutputLimit is >= 0 and < 10000 ? type.FloorOutputLimit.Value : 0,
                PipeSpacing = type.PipeSpacing is >= 0 and < 10000 ? type.PipeSpacing.Value : 0,
                Airflow = type.Airflow is >= 0 and < 100000 ? type.Airflow.Value : 0,
                AvailablePressure = type.AvailablePressure is >= 0 and < 100000 ? type.AvailablePressure.Value : 0,
                HeatRecovery = type.HeatRecovery is >= 0 and <= 1 ? type.HeatRecovery.Value : 0,
                SpecificFanPower = type.SpecificFanPower is >= 0 and <= 10 ? type.SpecificFanPower.Value : TemplateElectric(type.Name, type.Form)?.SpecificFanPower ?? 0,
                SpaceEfficiency = type.SpaceEfficiency is >= 0 and <= 1000 ? type.SpaceEfficiency.Value : TemplateElectric(type.Name, type.Form)?.SpaceEfficiency ?? 0,
                WaterEfficiency = type.WaterEfficiency is >= 0 and <= 1000 ? type.WaterEfficiency.Value : TemplateElectric(type.Name, type.Form)?.WaterEfficiency ?? 0,
                StandingLoss = type.StandingLoss is >= 0 and < 100 ? type.StandingLoss.Value : TemplateElectric(type.Name, type.Form)?.StandingLoss ?? 0,
                PressureLoss = type.PressureLoss is >= 0 and < 100000 ? type.PressureLoss.Value : 0,
                RatedAirflow = type.RatedAirflow is >= 0 and < 100000 ? type.RatedAirflow.Value : 0,
                ContinuousRate = type.ContinuousRate is >= 0 and < 100000 ? type.ContinuousRate.Value : 0,
                VentArea = type.VentArea is >= 0 and < 1e7 ? type.VentArea.Value : 0,
                Load = type.Load is >= 0 and < 1e7 ? type.Load.Value : TemplateElectric(type.Name, type.Form)?.Load ?? 0,
                Lumens = type.Lumens is >= 0 and < 1e7 ? type.Lumens.Value : TemplateElectric(type.Name, type.Form)?.Lumens ?? 0,
                Distribution = type.Distribution is not null ? ParseEnum(type.Distribution, LightDistribution.Diffuse) : TemplateElectric(type.Name, type.Form)?.Distribution ?? LightDistribution.Diffuse,
                Photometry = PhotometryOf(type.Photometry),
                Ways = type.Ways is > 0 and < 1000 ? type.Ways.Value : TemplateElectric(type.Name, type.Form)?.Ways ?? 0,
                SupplyRating = type.SupplyRating is >= 0 and < 1e5 ? type.SupplyRating.Value : TemplateElectric(type.Name, type.Form)?.SupplyRating ?? 0,
                ExternalImpedance = type.ExternalImpedance is >= 0 and < 1000 ? type.ExternalImpedance.Value : TemplateElectric(type.Name, type.Form)?.ExternalImpedance ?? 0,
                FlowRate = Water(type.FlowRate, ParseEnum(type.Form, ComponentForm.Box) switch { ComponentForm.Basin => 6, ComponentForm.Sink => 8, ComponentForm.Shower => 10, _ => 0 }),
                FlushVolume = Water(type.FlushVolume, ParseEnum(type.Form, ComponentForm.Box) is ComponentForm.Toilet or ComponentForm.WallHungToilet ? 6 : 0),
                PartFlushVolume = type.PartFlushVolume is >= 0 and < 100 ? type.PartFlushVolume.Value
                    : type.FlushVolume is null && ParseEnum(type.Form, ComponentForm.Box) is ComponentForm.Toilet or ComponentForm.WallHungToilet ? 4 : 0,
                Capacity = Water(type.Capacity, ParseEnum(type.Form, ComponentForm.Box) switch
                {
                    ComponentForm.Bath => 185, ComponentForm.HotWaterCylinder => TemplateElectric(type.Name, type.Form)?.Capacity ?? 0, _ => 0
                }),
                WaterPerUse = Water(type.WaterPerUse, ParseEnum(type.Form, ComponentForm.Box) switch { ComponentForm.Dishwasher => 1.25, ComponentForm.WashingMachine => 8.17, _ => 0 }),
                Mesh = MeshFrom(type.Mesh),
                MaterialId = type.MaterialId,
                Colour = ParseColour(type.Colour, new ColourRgb(0xB4, 0x9A, 0x74)),
                TypeMark = type.TypeMark,
                AssemblyCode = type.AssemblyCode,
                Keynote = type.Keynote,
                Manufacturer = type.Manufacturer,
                Url = type.Url,
                Description = type.Description,
                Cost = type.Cost
            }.WithSolids((type.Solids ?? new List<ComponentSolidDto>()).Select(solid => new ComponentSolid
            {
                Name = solid.Name,
                Shape = ParseEnum(solid.Shape, SolidShape.Box),
                Finish = ParseEnum(solid.Finish, Core.Views.ComponentModel.PartFinish.Body),
                From = solid.From, To = solid.To, Back = solid.Back, Front = solid.Front, Bottom = solid.Bottom, Top = solid.Top
            })));

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
                StandsOn = component.StandsOn,
                FlipFacing = component.FlipFacing,
                FlipHand = component.FlipHand,
                Mark = component.Mark,
                Comments = component.Comments,
                Workset = component.Workset,
                PhaseCreated = ParseEnum(component.PhaseCreated, DesignPhase.New),
                PhaseDemolished = string.IsNullOrEmpty(component.PhaseDemolished)
                    ? null
                    : ParseEnum(component.PhaseDemolished, DesignPhase.Existing)
            });
        }

        // The services' systems, on the fittings that came with them - a fitting on one system of a kind at most.
        foreach (var saved in dto.Systems ?? new List<MepSystemDto>())
        {
            if (!Enum.TryParse<SystemKind>(saved.Kind, out var kind)) continue;
            var system = new MepSystem
            {
                Id = saved.Id, Kind = kind, Name = string.IsNullOrWhiteSpace(saved.Name) ? MepSystems.NextName(document, kind) : saved.Name,
                Use = ParseEnum(saved.Use, CircuitUse.Other), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty,
                FlowTemperature = saved.FlowTemperature is > 0 and < 100 ? saved.FlowTemperature.Value : 0,
                ReturnTemperature = saved.ReturnTemperature is > 0 and < 100 ? saved.ReturnTemperature.Value : 0,
                Breaker = saved.Breaker is > 0 and < 10000 ? saved.Breaker.Value : 0,
                Curve = ParseEnum(saved.Curve, BreakerCurve.B),
                Rcd = saved.Rcd ?? true,
                ArcFault = saved.ArcFault ?? false,
                Cable = saved.Cable ?? string.Empty,
                Method = ParseEnum(saved.Method, CableMethod.ClippedDirect),
                Grouped = saved.Grouped is >= 1 and <= 100 ? saved.Grouped.Value : 1,
                CableLength = saved.CableLength is > 0 and < 100000 ? saved.CableLength.Value : 0
            };
            foreach (var id in (saved.Members ?? new List<Guid>()).Distinct())
                if (document.Find<Component>(id) is { } member && MepSystems.WhyNotMember(document, member, system) is null) system.Members.Add(id);
            if (document.Find<Component>(saved.SourceId) is { } source && MepSystems.WhyNotSource(document, source, system) is null) system.SourceId = source.Id;
            document.Add(system);
        }

        // The pipes, on their systems; one with no length is nothing.
        foreach (var saved in (dto.Pipes ?? new List<PipeDto>()).Where(saved => document.FindLevel(saved.LevelId) is not null
                     && new[] { saved.StartX, saved.StartY, saved.EndX, saved.EndY, saved.StartOffset, saved.EndOffset, saved.Diameter }.All(double.IsFinite)))
        {
            var pipe = new Pipe
            {
                Id = saved.Id, LevelId = saved.LevelId, Start = new Point2D(saved.StartX, saved.StartY), End = new Point2D(saved.EndX, saved.EndY),
                StartOffset = saved.StartOffset, EndOffset = saved.EndOffset, Diameter = saved.Diameter > 0 ? saved.Diameter : 15,
                Kind = ParseEnum(saved.Kind, SystemKind.ColdWater), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty, Return = saved.Return == true,
                Insulation = saved.Insulation is > 0 and <= 200 ? saved.Insulation.Value : 0, Seal = ParseEnum(saved.Seal, PipeSeal.None),
                TypeId = document.FindType<PipeType>(saved.TypeId)?.Id ?? document.TypesOf<PipeType>().FirstOrDefault()?.Id ?? Guid.Empty
            };
            if (pipe.Length < 1) continue;
            pipe.SystemId = document.Find<MepSystem>(saved.SystemId) is { } on && on.Kind == pipe.Kind ? on.Id : null;
            document.Add(pipe);
        }

        // The ducts, on their systems; one with no length is nothing.
        foreach (var saved in (dto.Ducts ?? new List<DuctDto>()).Where(saved => document.FindLevel(saved.LevelId) is not null
                     && new[] { saved.StartX, saved.StartY, saved.EndX, saved.EndY, saved.StartOffset, saved.EndOffset, saved.Width, saved.Height }.All(double.IsFinite)))
        {
            var duct = new Duct
            {
                Id = saved.Id, LevelId = saved.LevelId, Start = new Point2D(saved.StartX, saved.StartY), End = new Point2D(saved.EndX, saved.EndY),
                StartOffset = saved.StartOffset, EndOffset = saved.EndOffset, Width = saved.Width > 0 ? saved.Width : 125, Height = saved.Height > 0 ? saved.Height : 125,
                Kind = ParseEnum(saved.Kind, SystemKind.SupplyAir), FireProtection = ParseEnum(saved.FireProtection, FireProtection.None),
                DamperRating = saved.DamperRating is >= 0 and <= 240 ? saved.DamperRating.Value : 60,
                Insulation = saved.Insulation is >= 0 and <= 300 ? saved.Insulation.Value : 0,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty,
                TypeId = document.FindType<DuctType>(saved.TypeId)?.Id ?? document.TypesOf<DuctType>().FirstOrDefault()?.Id ?? Guid.Empty
            };
            if (duct.Length < 1) continue;
            duct.SystemId = document.Find<MepSystem>(saved.SystemId) is { } on && on.Kind == duct.Kind ? on.Id : null;
            document.Add(duct);
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
                ClassificationCode = room.ClassificationCode ?? string.Empty,
                DesignTemperature = room.DesignTemperature is { } designed && double.IsFinite(designed) && designed is > -20 and < 40 ? designed : null,
                Unheated = room.Unheated == true,
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
        {
            var made = new Dimension
            {
                Id = dimension.Id,
                LevelId = dimension.LevelId,
                TypeId = dimension.TypeId,
                Start = ReadEnd(dimension.Start),
                End = ReadEnd(dimension.End),
                Offset = dimension.Offset,
                Override = dimension.Override,
                Comments = dimension.Comments,
                Workset = dimension.Workset
            };
            made.Between.AddRange((dimension.Between ?? new List<DimensionEndDto>()).Select(ReadEnd));
            document.Add(made);
        }

        foreach (var angular in dto.AngularDimensions.Where(saved => double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new AngularDimension
            {
                Id = angular.Id, LevelId = angular.LevelId, TypeId = angular.TypeId, First = ReadEnd(angular.First), Second = ReadEnd(angular.Second),
                Through = new Point2D(angular.X, angular.Y), Override = angular.Override ?? string.Empty, Comments = angular.Comments ?? string.Empty
            });

        foreach (var radial in dto.RadialDimensions.Where(saved => double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new RadialDimension
            {
                Id = radial.Id, LevelId = radial.LevelId, TypeId = radial.TypeId, Kind = ParseEnum(radial.Kind, RadialKind.Radius), Target = ReadEnd(radial.Target),
                Through = new Point2D(radial.X, radial.Y), Override = radial.Override ?? string.Empty, Comments = radial.Comments ?? string.Empty
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

        if (dto.Default3D is { } saved3D) View3DFromDto(saved3D, document.Default3D, document);
        foreach (var saved in dto.Views3D)
        {
            var view = new View3D { Id = saved.Id };
            View3DFromDto(saved, view, document);
            document.Add(view);
        }

        foreach (var saved in dto.InteriorElevations.Where(saved => document.FindLevel(saved.LevelId) is not null && double.IsFinite(saved.X) && double.IsFinite(saved.Y)))
            document.Add(new InteriorElevationMarker
            {
                Id = saved.Id, LevelId = saved.LevelId, Name = string.IsNullOrWhiteSpace(saved.Name) ? "1" : saved.Name,
                Location = new Point2D(saved.X, saved.Y), Rotation = double.IsFinite(saved.Rotation) ? saved.Rotation : 0,
                LooksUp = saved.Up, LooksRight = saved.Right, LooksDown = saved.Down, LooksLeft = saved.Left, Comments = saved.Comments ?? string.Empty
            });

        foreach (var saved in dto.ViewTemplates)
        {
            var template = new ViewTemplate
            {
                Id = saved.Id, Name = string.IsNullOrWhiteSpace(saved.Name) ? "Template" : saved.Name, For = ParseEnum(saved.For, ViewTemplateKind.Plan),
                IncludeScale = saved.IncludeScale, Scale = Within(saved.Scale, 1, 10000, 100),
                IncludeDetailLevel = saved.IncludeDetailLevel, DetailLevel = ParseEnum(saved.DetailLevel, DetailLevel.Medium),
                IncludeWallFunctions = saved.IncludeWallFunctions, IncludeJoinDisplay = saved.IncludeJoinDisplay,
                JoinDisplay = ParseEnum(saved.JoinDisplay, WallJoinDisplay.CleanAllWallJoins),
                IncludeVisualStyle = saved.IncludeVisualStyle, VisualStyle = ParseEnum(saved.VisualStyle, VisualStyle.HiddenLine),
                IncludeHiddenKinds = saved.IncludeHiddenKinds, Comments = saved.Comments ?? string.Empty,
                IncludeFilters = saved.IncludeFilters, IncludePhaseFilter = saved.IncludePhaseFilter,
                PhaseFilter = ParseEnum(saved.PhaseFilter, PhaseFilter.ShowAll)
            };
            template.Filters.AddRange((saved.Filters ?? new List<FilterOverrideDto>()).Select(FilterUse));
            foreach (var name in saved.HiddenWallFunctions ?? new List<string>())
                if (Enum.TryParse<WallFunction>(name, out var function)) template.HiddenWallFunctions.Add(function);
            foreach (var name in saved.HiddenKinds ?? new List<string>())
                if (Enum.TryParse<MeshKind>(name, out var kind)) template.HiddenKinds.Add(kind);
            document.Add(template);
        }

        foreach (var saved in dto.Callouts.Where(saved => new[] { saved.MinX, saved.MinY, saved.MaxX, saved.MaxY }.All(double.IsFinite) && saved.MaxX > saved.MinX && saved.MaxY > saved.MinY))
            document.Add(new Callout
            {
                Id = saved.Id, LevelId = saved.LevelId, Name = string.IsNullOrWhiteSpace(saved.Name) ? "Callout" : saved.Name,
                Parent = new ViewReference(ParseEnum(saved.ParentKind, ViewKind.FloorPlan), saved.ParentId, string.Empty) { Index = saved.ParentIndex },
                Region = new BoundingBox2D(saved.MinX, saved.MinY, saved.MaxX, saved.MaxY), Comments = saved.Comments ?? string.Empty,
                Planning = ParseEnum(saved.Planning, PlanningDrawing.None)
            });

        foreach (var saved in dto.Walkthroughs.Where(saved => document.FindLevel(saved.LevelId) is not null))
        {
            var walk = new Walkthrough
            {
                Id = saved.Id, LevelId = saved.LevelId, Name = string.IsNullOrWhiteSpace(saved.Name) ? "Walkthrough" : saved.Name,
                EyeHeight = Within(saved.EyeHeight, 100, 100000, 1750), Frames = saved.Frames is >= 2 and <= 100000 ? saved.Frames : 300,
                FramesPerSecond = Within(saved.FramesPerSecond, 1, 120, 15), Comments = saved.Comments ?? string.Empty
            };
            var path = saved.Path ?? new List<double>();
            walk.SetPath(Enumerable.Range(0, path.Count / 2).Select(i => new Point2D(path[2 * i], path[2 * i + 1])).Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)));
            if (walk.Path.Count >= 2) document.Add(walk);
        }

        foreach (var saved in dto.Revisions ?? new List<RevisionDto>())
            document.Revisions.Add(new Revision
            {
                Id = saved.Id == Guid.Empty ? Guid.NewGuid() : saved.Id, Numbering = ParseEnum(saved.Numbering, RevisionNumbering.Preliminary), Date = saved.Date,
                Description = saved.Description ?? string.Empty, IssuedTo = saved.IssuedTo ?? string.Empty, IssuedBy = saved.IssuedBy ?? string.Empty,
                Issued = saved.Issued, Visibility = ParseEnum(saved.Visibility, RevisionVisibility.CloudsAndTags)
            });

        foreach (var saved in dto.RevisionClouds ?? new List<RevisionCloudDto>())
        {
            var cloud = new RevisionCloud
            {
                Id = saved.Id, LevelId = saved.LevelId, RevisionId = saved.RevisionId,
                TagAt = saved.TagX is { } tx && saved.TagY is { } ty ? new Point2D(tx, ty) : null,
                Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty, Workset = saved.Workset ?? "Workset1"
            };
            var outline = saved.Outline ?? new List<double>();
            cloud.Outline.AddRange(Enumerable.Range(0, outline.Count / 2).Select(i => new Point2D(outline[2 * i], outline[2 * i + 1])).Where(point => double.IsFinite(point.X) && double.IsFinite(point.Y)));
            if (cloud.Outline.Count >= 2) document.Add(cloud);
        }

        // A file from before keynotes were kept opens with the starter set.
        if (dto.Keynotes is null) document.Keynotes.CopyFrom(KeynoteTable.Starter());
        else
        {
            document.Keynotes.Source = dto.Keynotes.Source ?? string.Empty;
            document.Keynotes.Entries.AddRange((dto.Keynotes.Entries ?? new List<KeynoteEntryDto>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .Select(entry => new KeynoteEntry(entry.Key, entry.Text ?? string.Empty, entry.Parent ?? string.Empty)));
        }

        foreach (var saved in dto.KeynoteTags ?? new List<KeynoteTagDto>())
            document.Add(new KeynoteTag
            {
                Id = saved.Id, LevelId = saved.LevelId, Source = ParseEnum(saved.Source, KeynoteSource.Element), TargetId = saved.TargetId,
                MaterialId = saved.MaterialId, Key = saved.Key ?? string.Empty, Position = new Point2D(saved.X, saved.Y),
                LeaderEnd = saved.LeaderX is { } lx && saved.LeaderY is { } ly ? new Point2D(lx, ly) : null,
                Display = ParseEnum(saved.Display, KeynoteDisplay.Key), Mark = saved.Mark ?? string.Empty, Comments = saved.Comments ?? string.Empty,
                Workset = saved.Workset ?? "Workset1"
            });

        // A file from before schedules were kept opens with those a project starts with.
        if (dto.Schedules is null) document.Schedules.AddRange(ScheduleDefinition.Defaults());
        else
            foreach (var saved in dto.Schedules)
            {
                var schedule = new ScheduleDefinition(saved.Name ?? "Schedule", ParseEnum(saved.Category, BuiltInCategory.Walls), (saved.Fields ?? new List<string>()).ToArray())
                {
                    Id = saved.Id == Guid.Empty ? Guid.NewGuid() : saved.Id,
                    SortBy = saved.SortBy, SortDescending = saved.SortDescending, GroupBy = saved.GroupBy, ShowTotals = saved.ShowTotals,
                    Itemize = saved.Itemize, ShowFormatsOnSheets = saved.ShowFormatsOnSheets
                };
                schedule.Filters.AddRange((saved.Filters ?? new List<ScheduleFilterDto>()).Select(filter =>
                    new ScheduleFilter(filter.Field ?? string.Empty, ParseEnum(filter.Comparison, ScheduleOperator.Equals), filter.Value ?? string.Empty)));
                foreach (var field in saved.NotTotalled ?? new List<string>()) schedule.NotTotalled.Add(field);
                schedule.Calculated.AddRange((saved.Calculated ?? new List<CalculatedFieldDto>()).Select(calculated => new CalculatedField
                {
                    Name = calculated.Name ?? "Calculated", Kind = ParseEnum(calculated.Kind, CalculatedKind.Number), Formula = calculated.Formula ?? string.Empty,
                    PercentageOf = calculated.PercentageOf ?? string.Empty, ByGroup = calculated.ByGroup
                }));
                schedule.Formats.AddRange((saved.Formats ?? new List<ConditionalFormatDto>()).Select(format => new ConditionalFormat
                {
                    Field = format.Field ?? string.Empty, Test = ParseEnum(format.Test, FormatTest.LessThan), Value = format.Value ?? string.Empty,
                    Value2 = format.Value2 ?? string.Empty, Background = ParseColour(format.Background, new ColourRgb(0xF8, 0xC8, 0xC8))
                }));
                document.Schedules.Add(schedule);
            }

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
            sheet.RevisionIds.AddRange(dtoSheet.RevisionIds ?? new List<Guid>());

            foreach (var dtoViewport in dtoSheet.Viewports)
            {
                var kind = ParseEnum(dtoViewport.Kind, ViewKind.FloorPlan);

                sheet.Add(new Viewport
                {
                    Id = dtoViewport.Id,
                    View = new ViewReference(kind, dtoViewport.TargetId, dtoViewport.ScheduleName) { Index = dtoViewport.Index },
                    Centre = new Point2D(dtoViewport.X, dtoViewport.Y),
                    Scale = new ViewScale(dtoViewport.Scale > 0 ? dtoViewport.Scale : 100),
                    ShowTitle = dtoViewport.ShowTitle,
                    TitleOverride = dtoViewport.TitleOverride,
                    DetailNumber = dtoViewport.DetailNumber ?? string.Empty,
                    ShowNorth = dtoViewport.ShowNorth,
                    ShowScaleBar = dtoViewport.ShowScaleBar,
                    Note = dtoViewport.Note ?? string.Empty
                });
            }

            // Its pictures, sized from their own bytes: one that is not a PNG or a JPEG any more is left out.
            foreach (var saved in dtoSheet.Images ?? new List<SheetImageDto>())
                if (saved.Data is { Length: > 0 } data && SheetImage.From(saved.Name, data) is { } image)
                {
                    sheet.AddImage(new SheetImage
                    {
                        Id = saved.Id, Name = image.Name, Data = data, PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight,
                        Centre = new Point2D(saved.X, saved.Y), Width = double.IsFinite(saved.Width) && saved.Width > 1 ? saved.Width : 150,
                        ShowTitle = saved.ShowTitle, Title = saved.Title ?? string.Empty
                    });
                }

            document.Add(sheet);
        }

        foreach (var settings in dto.ViewSettings)
        {
            var view = new ViewReference(ParseEnum(settings.Kind, ViewKind.FloorPlan), settings.TargetId, string.Empty) { Index = settings.Index };

            foreach (var name in settings.HiddenWallFunctions)
                if (Enum.TryParse<WallFunction>(name, out var function))
                    document.ViewSettings.SetWallFunctionVisible(view, function, visible: false);

            if (settings.Scale is { } scale) document.ViewSettings.SetScale(view, scale);
            if (settings.JoinDisplay is { } display) document.ViewSettings.SetJoinDisplay(view, ParseEnum(display, WallJoinDisplay.CleanAllWallJoins));
            if (settings.DetailLevel is { } detail && Enum.TryParse<DetailLevel>(detail, out var level)) document.ViewSettings.SetDetailLevel(view, level);
            if (settings.Template is { } governing) document.ViewSettings.SetTemplate(view, governing);
        }

        // A project written before a category existed carries no types for it. Fill those
        // gaps so every tool in the build can actually be used on an older file.
        // Every type's identity, whatever kind it is - its keynote above all, which most types' own records leave out.
        foreach (var identity in dto.TypeIdentities)
        {
            if (document.ElementTypes.FirstOrDefault(type => type.Id == identity.Id) is not { } type) continue;
            if (identity.TypeMark is { } mark) type.TypeMark = mark;
            if (identity.AssemblyCode is { } code) type.AssemblyCode = code;
            if (identity.Keynote is { } keynote) type.Keynote = keynote;
            if (identity.Manufacturer is { } maker) type.Manufacturer = maker;
            if (identity.Url is { } url) type.Url = url;
            if (identity.Description is { } description) type.Description = description;
            type.Cost = identity.Cost;
            if (identity.ClassificationCode is { } classification) type.ClassificationCode = classification;
        }

        ReadRound6(dto, document);
        ReadCoordination(dto, document);

        // Groups last, once what is in them has been read: holding only what is still there.
        foreach (var saved in dto.Groups ?? new List<GroupDto>())
        {
            var members = (saved.MemberIds ?? new List<Guid>()).Where(id => document.FindElement(id) is not null and not Group).Distinct().ToList();
            if (members.Count == 0 || document.FindLevel(saved.LevelId) is null) continue;
            var group = new Group
            {
                Id = saved.Id, LevelId = saved.LevelId, Name = string.IsNullOrWhiteSpace(saved.Name) ? Groups.NextName(document) : saved.Name!,
                Origin = new Point2D(Within(saved.OriginX, -1e9, 1e9, 0), Within(saved.OriginY, -1e9, 1e9, 0)), Rotation = Within(saved.Rotation, -1e6, 1e6, 0), Mirrored = saved.Mirrored,
                Comments = saved.Comments ?? string.Empty
            };
            group.MemberIds.AddRange(members);
            document.Add(group);
        }

        // What was purged stays out: the library brings back only what the project has never had.
        foreach (var purged in dto.PurgedLibrary ?? new List<string>())
            if (!string.IsNullOrWhiteSpace(purged)) document.PurgedLibrary.Add(purged);
        document.EnsureDefaultTypes();

        var pinned = dto.PinnedIds.ToHashSet();
        foreach (var element in document.Elements.Where(element => pinned.Contains(element.Id))) element.Pinned = true;

        return document;
    }

    /// <summary>An elliptical wall's shape, or null when there is none or it could not describe a piece of ellipse.</summary>
    private static WallEllipse? ReadEllipse(WallDto wall) =>
        wall is { EllipseRatio: { } ratio, EllipseFrom: { } from, EllipseTo: { } to } &&
        new WallEllipse(ratio, from, to) is { IsValid: true } ellipse
            ? ellipse
            : null;

    private static readonly Lazy<IReadOnlyDictionary<string, ComponentType>> TemplateFamilies = new(() =>
        BimDocument.CreateDefault().TypesOf<ComponentType>().GroupBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What a radiator, towel rail or boiler saved before their outputs were kept gives: the
    /// library's own of that name and size, or nothing known.
    /// </summary>
    /// <summary>A standard family of the same name and form, for the electrical figures a file from before has none of.</summary>
    private static ComponentType? TemplateElectric(string name, string? form) =>
        TemplateFamilies.Value.TryGetValue(name, out var family) && family.Form == ParseEnum(form, ComponentForm.Box) ? family : null;

    /// <summary>A light's photometric data as saved - or none, where it is missing or does not hold together.</summary>
    private static Photometry? PhotometryOf(PhotometryDto? saved)
    {
        if (saved is null || saved.Planes.Count == 0 || saved.Angles.Count == 0 || saved.Candela.Count != saved.Planes.Count
            || saved.Candela.Any(plane => plane.Count != saved.Angles.Count)) return null;
        return new Photometry
        {
            Source = saved.Source, Lumens = saved.Lumens, Watts = saved.Watts, Planes = saved.Planes, Angles = saved.Angles,
            Candela = saved.Candela.Select(plane => (IReadOnlyList<double>)plane).ToList()
        };
    }

    private static ComponentType? TemplateHeat(string name, double width) =>
        TemplateFamilies.Value.TryGetValue(name, out var family) && Math.Abs(family.Width - width) < 0.5 && HeatsOrHeated(family.Form) ? family : null;

    /// <summary>Whether a form has a heat output kept: what heats a room or a building, or a cylinder's coil.</summary>
    private static bool HeatsOrHeated(ComponentForm form) =>
        form is ComponentForm.Radiator or ComponentForm.TowelRail or ComponentForm.Boiler or ComponentForm.HeatPump or ComponentForm.HotWaterCylinder;

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
        type.SafetyGlazing = dto.SafetyGlazing ?? false;
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
    /// <summary>A saved number, or nothing where it was not one.</summary>
    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    /// <summary>Numbers packed as their bytes, in base 64: a model's corners would be megabytes written out one by one.</summary>
    private static string Packed<T>(T[] values) where T : struct
    {
        var bytes = new byte[Buffer.ByteLength(values)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static T[] Unpacked<T>(string text, int size) where T : struct
    {
        var bytes = Convert.FromBase64String(text);
        var values = new T[bytes.Length / size];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * size);
        return values;
    }

    /// <summary>A family's 3D model as it was saved, or none where it cannot be read back.</summary>
    private static ComponentMesh? MeshFrom(ComponentMeshDto? dto)
    {
        if (dto is null) return null;
        try
        {
            var corners = Unpacked<float>(dto.Positions, sizeof(float));
            var positions = Enumerable.Range(0, corners.Length / 3).Select(i => new Point3D(corners[3 * i], corners[3 * i + 1], corners[3 * i + 2])).ToList();
            var triangles = Unpacked<int>(dto.Triangles, sizeof(int));
            var edges = Unpacked<int>(dto.Edges, sizeof(int));
            if (triangles.Any(index => index < 0 || index >= positions.Count) || edges.Any(index => index < 0 || index >= positions.Count)) return null;
            return new ComponentMesh(positions, triangles, edges, dto.Source);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The railing type of a name in the library, for what an older project did not save of it.</summary>
    private static RailingType? Usual(string name) => RailingType.Library().FirstOrDefault(type => type.Name == name);

    private static View3DDto View3DToDto(View3D view) => new()
    {
        Id = view.Id, Name = view.Name,
        Camera = view.Camera is { } camera
            ? new List<double> { camera.Eye.X, camera.Eye.Y, camera.Eye.Z, camera.Target.X, camera.Target.Y, camera.Target.Z, camera.FieldOfView }
            : null,
        Perspective = view.Perspective, Style = view.Style.ToString(),
        SectionBox = view.SectionBox is { } box ? new List<double> { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z } : null,
        SectionBoxOn = view.SectionBoxOn,
        HiddenLevels = view.HiddenLevels.Count > 0 ? view.HiddenLevels.ToList() : null,
        HiddenKinds = view.HiddenKinds.Count > 0 ? view.HiddenKinds.Select(kind => kind.ToString()).OrderBy(name => name).ToList() : null,
        ShowGround = view.ShowGround, ShowEdges = view.ShowEdges, Comments = view.Comments
    };

    private static void View3DFromDto(View3DDto saved, View3D view, BimDocument document)
    {
        view.Name = string.IsNullOrWhiteSpace(saved.Name) ? view.Name : saved.Name;
        if (saved.Camera is { Count: 7 } c && c.All(double.IsFinite))
            view.Camera = new Camera3D(new Point3D(c[0], c[1], c[2]), new Point3D(c[3], c[4], c[5])) { FieldOfView = Math.Clamp(c[6], 10, 120) };
        view.Perspective = saved.Perspective;
        view.Style = ParseEnum(saved.Style, VisualStyle.Shaded);
        if (saved.SectionBox is { Count: 6 } b && b.All(double.IsFinite))
            view.SectionBox = Box3D.Around(new Point3D(b[0], b[1], b[2]), new Point3D(b[3], b[4], b[5]));
        view.SectionBoxOn = saved.SectionBoxOn && view.SectionBox is not null;
        if (saved.HiddenLevels is { } levels) view.HiddenLevels.UnionWith(levels.Where(id => document.FindLevel(id) is not null));
        if (saved.HiddenKinds is { } kinds)
            foreach (var name in kinds)
                if (Enum.TryParse<MeshKind>(name, out var kind)) view.HiddenKinds.Add(kind);
        (view.ShowGround, view.ShowEdges, view.Comments) = (saved.ShowGround, saved.ShowEdges, saved.Comments ?? string.Empty);
    }

    /// <summary>A water figure from the file, or what is assumed of a fitting of its kind where the file has none.</summary>
    private static double Water(double? value, double assumed) => value is >= 0 and < 10000 ? value.Value : assumed;

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

    /// <summary>A saved size as it was, if it is one it could be; otherwise the usual.</summary>
    private static double Within(double saved, double least, double most, double usual) =>
        double.IsFinite(saved) && saved >= least && saved <= most ? saved : usual;

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
