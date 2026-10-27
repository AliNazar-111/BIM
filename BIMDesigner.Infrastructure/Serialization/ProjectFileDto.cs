namespace BIMDesigner.Infrastructure.Serialization;

/// <summary>
/// The on-disk shape of a project.
///
/// These are deliberately separate from the domain classes in Core. A saved file has to
/// keep opening after the model classes are refactored, so the format is written down here
/// once and translated, rather than following whatever the C# classes happen to look like
/// today. Enums are stored as text and colours as hex for the same reason: the file stays
/// readable and survives members being reordered.
///
/// Each element category gets its own array. Adding doors means adding a "doors" array and
/// deciding explicitly what it holds, which is the intended amount of friction.
/// </summary>
internal sealed class ProjectFileDto
{
    /// <summary>Bumped whenever the format changes incompatibly. See <see cref="ProjectFile"/>.</summary>
    public int FormatVersion { get; set; } = ProjectFile.CurrentFormatVersion;

    public string Application { get; set; } = "BIMDesigner";

    public DateTime SavedUtc { get; set; }

    public ProjectInformationDto Project { get; set; } = new();

    public List<LevelDto> Levels { get; set; } = new();

    public List<MaterialDto> Materials { get; set; } = new();

    public List<WallTypeDto> WallTypes { get; set; } = new();

    /// <summary>Walls built of other wall types one above another. Missing from older files.</summary>
    public List<StackedWallTypeDto> StackedWallTypes { get; set; } = new();
    public List<CurtainWallTypeDto> CurtainWallTypes { get; set; } = new();
    public List<SweepProfileTypeDto> SweepProfiles { get; set; } = new();
    public List<WallSweepTypeDto> WallSweepTypes { get; set; } = new();
    public List<PlacedSweepDto> PlacedSweeps { get; set; } = new();

    /// <summary>Fascia and gutter types, and the fascias and gutters run along roof edges. Missing from older files.</summary>
    public List<FasciaTypeDto> FasciaTypes { get; set; } = new();

    public List<DimensionTypeDto> DimensionTypes { get; set; } = new();

    /// <summary>
    /// Every type's identity - its mark, classification, keynote, maker, link, description and
    /// cost - whatever kind it is, so none is lost by a type whose own record leaves it out.
    /// </summary>
    public List<TypeIdentityDto> TypeIdentities { get; set; } = new();
    public List<GutterTypeDto> GutterTypes { get; set; } = new();
    public List<SlabEdgeTypeDto> SlabEdgeTypes { get; set; } = new();
    public List<SlabEdgeDto> SlabEdges { get; set; } = new();
    public List<SoffitTypeDto> SoffitTypes { get; set; } = new();
    public List<EaveTypeDto> EaveTypes { get; set; } = new();
    public List<MullionTypeDto> MullionTypes { get; set; } = new();
    public List<RoofEdgeSweepDto> RoofEdgeSweeps { get; set; } = new();
    public List<RoofWindowTypeDto> RoofWindowTypes { get; set; } = new();
    public List<ChimneyTypeDto> ChimneyTypes { get; set; } = new();
    public List<StairTypeDto> StairTypes { get; set; } = new();
    public List<StairDto> Stairs { get; set; } = new();
    public List<RampTypeDto> RampTypes { get; set; } = new();
    public List<RampDto> Ramps { get; set; } = new();
    public List<RailingTypeDto> RailingTypes { get; set; } = new();
    public List<RailingDto> Railings { get; set; } = new();
    public List<BeamTypeDto> BeamTypes { get; set; } = new();
    public List<BeamDto> Beams { get; set; } = new();

    /// <summary>The services' pipe types and pipes. Missing from older files.</summary>
    public List<PipeTypeDto>? PipeTypes { get; set; }
    public List<PipeDto>? Pipes { get; set; }

    /// <summary>The ventilation's duct types and ducts. Missing from older files.</summary>
    public List<DuctTypeDto>? DuctTypes { get; set; }
    public List<DuctDto>? Ducts { get; set; }
    public List<RoomSeparatorDto> RoomSeparators { get; set; } = new();
    public List<ToposurfaceDto> Toposurfaces { get; set; } = new();
    public List<GroundRegionDto> GroundRegions { get; set; } = new();
    public List<SitePadDto> SitePads { get; set; } = new();
    public List<MassDto> Masses { get; set; } = new();
    public List<GroupDto> Groups { get; set; } = new();
    public List<PropertyLineDto> PropertyLines { get; set; } = new();
    public List<SpotElevationDto> SpotElevations { get; set; } = new();
    public List<BeamSystemDto> BeamSystems { get; set; } = new();

    /// <summary>The elements pinned where they are.</summary>
    public List<Guid> PinnedIds { get; set; } = new();

    /// <summary>Every element's phases where they are not the default - built new, never demolished - whatever kind it is.</summary>
    public List<ElementPhaseDto> ElementPhases { get; set; } = new();

    /// <summary>Each element's workset where it is not the first - for every kind, its own record carrying one or not; none in a file from before.</summary>
    public List<ElementWorksetDto>? ElementWorksets { get; set; }

    /// <summary>The view filters, the design option sets and which elements are in an option.</summary>
    public List<ParameterFilterDto> ParameterFilters { get; set; } = new();
    public List<DesignOptionSetDto> DesignOptionSets { get; set; } = new();
    public List<OptionMemberDto> DesignOptionMembers { get; set; } = new();

    /// <summary>The project's own parameters, and their values on elements and types.</summary>
    public List<ProjectParameterDto> ProjectParameters { get; set; } = new();
    public List<ParameterValueDto> ParameterValues { get; set; } = new();

    /// <summary>The classification. Missing from older files, which open with the Uniclass starter set.</summary>
    public ClassificationDto? Classification { get; set; }
    public List<RoofWindowDto> RoofWindows { get; set; } = new();
    public List<ShaftOpeningDto> ShaftOpenings { get; set; } = new();
    public List<DownpipeDto> Downpipes { get; set; } = new();
    public List<RoofDrainDto> RoofDrains { get; set; } = new();
    public List<ChimneyDto> Chimneys { get; set; } = new();
    public List<PartDto> Parts { get; set; } = new();
    public List<WallFramingDto> WallFramings { get; set; } = new();
    public List<PolygonWallDto> PolygonWalls { get; set; } = new();
    public List<WallOpeningDto> WallOpenings { get; set; } = new();

    public List<DoorTypeDto> DoorTypes { get; set; } = new();

    public List<WindowTypeDto> WindowTypes { get; set; } = new();

    public List<WallDto> Walls { get; set; } = new();

    public List<DoorDto> Doors { get; set; } = new();

    public List<WindowDto> Windows { get; set; } = new();

    public List<RoomDto> Rooms { get; set; } = new();

    /// <summary>Loadable component families and what has been placed from them. Missing from older files.</summary>
    public List<ComponentTypeDto> ComponentTypes { get; set; } = new();
    public List<ComponentDto> Components { get; set; } = new();

    /// <summary>The services' systems: the fittings on each and what feeds it. Missing from older files.</summary>
    public List<MepSystemDto>? Systems { get; set; }

    /// <summary>Architectural columns and their types. Missing from older files.</summary>
    public List<ColumnTypeDto> ColumnTypes { get; set; } = new();
    public List<ColumnDto> Columns { get; set; } = new();
    public List<FootingTypeDto> FootingTypes { get; set; } = new();
    public List<WallFootingDto> WallFootings { get; set; } = new();
    public List<PadFootingDto> PadFootings { get; set; } = new();

    public List<SlabTypeDto> SlabTypes { get; set; } = new();

    public List<SlabDto> Slabs { get; set; } = new();

    public List<GridDto> Grids { get; set; } = new();

    public List<DimensionDto> Dimensions { get; set; } = new();

    public List<AngularDimensionDto> AngularDimensions { get; set; } = new();

    public List<RadialDimensionDto> RadialDimensions { get; set; } = new();

    public List<TagDto> Tags { get; set; } = new();

    public List<TextNoteDto> TextNotes { get; set; } = new();

    public List<SectionDto> Sections { get; set; } = new();

    public List<SheetDto> Sheets { get; set; } = new();

    /// <summary>The project's schedules. Missing from older files, which open with the schedules a project starts with.</summary>
    public List<ScheduleDto>? Schedules { get; set; }

    /// <summary>The clash tests and their clashes, the construction programme and the cost plan's additions; none in older files.</summary>
    public List<ClashTestDto>? ClashTests { get; set; }
    public ConstructionScheduleDto? Construction { get; set; }
    public CostSettingsDto? CostSettings { get; set; }

    /// <summary>The revisions of the drawings, in order, and the clouds round what they changed.</summary>
    public List<RevisionDto> Revisions { get; set; } = new();
    public List<RevisionCloudDto> RevisionClouds { get; set; } = new();

    /// <summary>The project's keynotes. Missing from older files, which open with the starter set.</summary>
    public KeynoteTableDto? Keynotes { get; set; }
    public List<KeynoteTagDto> KeynoteTags { get; set; } = new();

    /// <summary>Views that leave something out. Missing from older files, where every view showed everything.</summary>
    public List<ViewSettingsDto> ViewSettings { get; set; } = new();

    /// <summary>The {3D} view: its camera, style, section box. Missing from older files, which open fitted round the model.</summary>
    public View3DDto? Default3D { get; set; }

    /// <summary>3D views kept by name: cameras, and copies of {3D}.</summary>
    public List<View3DDto> Views3D { get; set; } = new();

    public List<WalkthroughDto> Walkthroughs { get; set; } = new();

    public List<InteriorElevationDto> InteriorElevations { get; set; } = new();

    public List<CalloutDto> Callouts { get; set; } = new();

    public List<ViewTemplateDto> ViewTemplates { get; set; } = new();
}

/// <summary>A view template: what it is for, and each setting with whether it is included.</summary>
internal sealed class ViewTemplateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Template";
    public string For { get; set; } = "Plan";
    public bool IncludeScale { get; set; } = true;
    public double Scale { get; set; } = 100;
    public bool IncludeDetailLevel { get; set; } = true;
    public string DetailLevel { get; set; } = "Medium";
    public bool IncludeWallFunctions { get; set; } = true;
    public List<string>? HiddenWallFunctions { get; set; }
    public bool IncludeJoinDisplay { get; set; } = true;
    public string JoinDisplay { get; set; } = "CleanAllWallJoins";
    public bool IncludeVisualStyle { get; set; } = true;
    public string VisualStyle { get; set; } = "HiddenLine";
    public bool IncludeHiddenKinds { get; set; } = true;
    public List<string>? HiddenKinds { get; set; }
    public bool IncludeFilters { get; set; } = true;
    public List<FilterOverrideDto>? Filters { get; set; }
    public bool IncludePhaseFilter { get; set; } = true;
    public string? PhaseFilter { get; set; }
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A callout: the view it is drawn on, and its box there.</summary>
internal sealed class CalloutDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string Name { get; set; } = "Callout 1";
    public string ParentKind { get; set; } = "FloorPlan";
    public Guid ParentId { get; set; }
    public int ParentIndex { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public string Comments { get; set; } = string.Empty;
}

/// <summary>An interior elevation marker: where it stands, how it is turned, and which of its arrows are views.</summary>
internal sealed class InteriorElevationDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string Name { get; set; } = "1";
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public bool Up { get; set; } = true;
    public bool Right { get; set; } = true;
    public bool Down { get; set; } = true;
    public bool Left { get; set; } = true;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A 3D view: where it looks from, how it is drawn, its section box and what it leaves out.</summary>
internal sealed class View3DDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "{3D}";

    /// <summary>The eye's x, y, z, the target's x, y, z, and the field of view; null until first looked through.</summary>
    public List<double>? Camera { get; set; }

    public bool Perspective { get; set; } = true;
    public string? Style { get; set; }

    /// <summary>The section box's lowest corner then its highest, x, y, z each.</summary>
    public List<double>? SectionBox { get; set; }
    public bool SectionBoxOn { get; set; }

    public List<Guid>? HiddenLevels { get; set; }
    public List<string>? HiddenKinds { get; set; }
    public bool ShowGround { get; set; } = true;
    public bool ShowEdges { get; set; } = true;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A walkthrough: its path on its storey, the eye's height, and how it plays.</summary>
internal sealed class WalkthroughDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string Name { get; set; } = "Walkthrough 1";
    public List<double>? Path { get; set; }
    public double EyeHeight { get; set; } = 1750;
    public int Frames { get; set; } = 300;
    public double FramesPerSecond { get; set; } = 15;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>What one view hides. The view is named the same way a viewport names it.</summary>
internal sealed class ViewSettingsDto
{
    /// <summary>"FloorPlan", "Section" or "Model3D".</summary>
    public string Kind { get; set; } = "FloorPlan";

    /// <summary>The level or section marker. Empty for the 3D view.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Which of the views of what it shows: the side of an elevation, the arrow of an interior marker.</summary>
    public int Index { get; set; }

    public List<string> HiddenWallFunctions { get; set; } = new();

    /// <summary>The view's scale as the denominator of 1:n, when it has its own.</summary>
    public double? Scale { get; set; }

    /// <summary>The view's Wall Join Display, when not the default of cleaning every join.</summary>
    public string? JoinDisplay { get; set; }

    /// <summary>The view's detail level, where it has one of its own.</summary>
    public string? DetailLevel { get; set; }

    /// <summary>The view template governing the view, where one does.</summary>
    public Guid? Template { get; set; }

    /// <summary>Its phase and phase filter, where not new construction and Show All.</summary>
    public string? Phase { get; set; }
    public string? PhaseFilter { get; set; }

    /// <summary>The view filters added to it, in order.</summary>
    public List<FilterOverrideDto>? Filters { get; set; }

    /// <summary>The design option it shows of a set, where not the primary.</summary>
    public List<OptionChoiceDto>? Options { get; set; }
}

internal sealed class FilterOverrideDto
{
    public Guid FilterId { get; set; }
    public bool Visible { get; set; } = true;
    public string? Colour { get; set; }
    public bool Halftone { get; set; }
}

internal sealed class OptionChoiceDto
{
    public Guid Set { get; set; }
    public Guid Option { get; set; }
}

internal sealed class ElementWorksetDto
{
    public Guid ElementId { get; set; }
    public string Workset { get; set; } = string.Empty;
}

internal sealed class ElementPhaseDto
{
    public Guid ElementId { get; set; }
    public string Created { get; set; } = "New";
    public string? Demolished { get; set; }
}

internal sealed class ParameterFilterDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Filter";
    public List<string> Categories { get; set; } = new();
    public List<ScheduleFilterDto> Rules { get; set; } = new();
    public bool MatchAny { get; set; }
}

internal sealed class DesignOptionSetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Option Set";
    public List<DesignOptionDto> Options { get; set; } = new();
}

internal sealed class DesignOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Option";
    public bool IsPrimary { get; set; }
}

internal sealed class OptionMemberDto
{
    public Guid ElementId { get; set; }
    public Guid OptionId { get; set; }
}

internal sealed class ProjectParameterDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Parameter";
    public string DataType { get; set; } = "Text";
    public string Binding { get; set; } = "Instance";
    public string Group { get; set; } = "IdentityData";
    public List<string> Categories { get; set; } = new();
    public bool Shared { get; set; }
    public string? SharedGroup { get; set; }
    public string? Description { get; set; }
}

/// <summary>A project parameter's value on an element or a type: as text, a number, a whole number, yes or no, or money.</summary>
internal sealed class ParameterValueDto
{
    public Guid OwnerId { get; set; }
    public Guid ParameterId { get; set; }
    public string Kind { get; set; } = "Text";
    public string? Text { get; set; }
    public double? Number { get; set; }
    public bool? Flag { get; set; }
}

internal sealed class ClassificationDto
{
    public string System { get; set; } = "Uniclass 2015";
    public string? Edition { get; set; }
    public List<ClassificationEntryDto> Entries { get; set; } = new();
}

internal sealed class ClassificationEntryDto
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// A drawing sheet and the views placed on it. The views are stored as references, never as
/// drawings, so a reopened sheet is a cut of the building as it is now.
/// </summary>
internal sealed class SheetDto
{
    public Guid Id { get; set; }

    public string Number { get; set; } = "A-101";
    public string Name { get; set; } = "Unnamed";

    public string PaperSize { get; set; } = "A1";
    public string Orientation { get; set; } = "Landscape";

    public string DrawnBy { get; set; } = string.Empty;
    public string CheckedBy { get; set; } = string.Empty;
    public string Revision { get; set; } = "P1";
    public DateTime? IssuedOn { get; set; }

    /// <summary>Revisions put on the sheet by hand.</summary>
    public List<Guid> RevisionIds { get; set; } = new();

    public List<ViewportDto> Viewports { get; set; } = new();

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

internal sealed class ViewportDto
{
    public Guid Id { get; set; }

    /// <summary>"FloorPlan", "Section" or "Schedule".</summary>
    public string Kind { get; set; } = "FloorPlan";

    /// <summary>The level or section marker shown. Empty for a schedule.</summary>
    public Guid TargetId { get; set; }

    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>Which of the views of what it shows: the side of an elevation, the arrow of an interior marker.</summary>
    public int Index { get; set; }

    /// <summary>Millimetres from the sheet's bottom-left corner.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>The scale denominator: 100 means 1:100.</summary>
    public double Scale { get; set; } = 100;

    public bool ShowTitle { get; set; } = true;
    public string TitleOverride { get; set; } = string.Empty;

    /// <summary>Its number on the sheet; missing from older files, where the views are numbered as they come.</summary>
    public string? DetailNumber { get; set; }
}

/// <summary>
/// A section marker. Only the cut line, which way it looks and how far it sees are stored -
/// the drawing itself is produced by cutting the model whenever the section is opened, so
/// there is nothing here that could be saved out of date.
/// </summary>
internal sealed class SectionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }

    public string Name { get; set; } = "A";

    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }

    public bool Flipped { get; set; }
    public double ViewDepth { get; set; } = 20000;

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

/// <summary>
/// One end of a dimension. The reference is what makes it follow the model; the point is
/// kept so a dimension whose reference has gone still knows where it was.
/// </summary>
internal sealed class DimensionEndDto
{
    public Guid ElementId { get; set; }
    public string Anchor { get; set; } = "Point";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A dimension style.</summary>
internal sealed class DimensionTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Linear";
    public string Tick { get; set; } = "Tick";
    public double TextSize { get; set; } = 2.5;
    public double TextOffset { get; set; } = 1;
    public bool TextAlongLine { get; set; } = true;
    public double WitnessGap { get; set; } = 2;
    public double WitnessExtension { get; set; } = 2;
    public double LineExtension { get; set; } = 2;
    public string Units { get; set; } = "Millimetres";
    public int Decimals { get; set; }
    public bool ShowUnits { get; set; }
    public int AngleDecimals { get; set; }
}

internal sealed class RevisionDto
{
    public Guid Id { get; set; }
    public string Numbering { get; set; } = "Preliminary";
    public DateTime? Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public string IssuedTo { get; set; } = string.Empty;
    public string IssuedBy { get; set; } = string.Empty;
    public bool Issued { get; set; }
    public string Visibility { get; set; } = "CloudsAndTags";
}

internal sealed class RevisionCloudDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public Guid RevisionId { get; set; }
    public List<double> Outline { get; set; } = new();
    public double? TagX { get; set; }
    public double? TagY { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

internal sealed class KeynoteTableDto
{
    public string Source { get; set; } = string.Empty;
    public List<KeynoteEntryDto> Entries { get; set; } = new();
}

internal sealed class KeynoteEntryDto
{
    public string Key { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Parent { get; set; }
}

internal sealed class KeynoteTagDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string Source { get; set; } = "Element";
    public Guid TargetId { get; set; }
    public Guid MaterialId { get; set; }
    public string Key { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double? LeaderX { get; set; }
    public double? LeaderY { get; set; }
    public string Display { get; set; } = "Key";
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

/// <summary>A schedule: what it reports, how, and what it works out and colours.</summary>
internal sealed class ScheduleDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Schedule";
    public string Category { get; set; } = "Walls";
    public List<string> Fields { get; set; } = new();
    public List<ScheduleFilterDto> Filters { get; set; } = new();
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public string? GroupBy { get; set; }
    public bool ShowTotals { get; set; } = true;
    public bool Itemize { get; set; } = true;
    public List<string> NotTotalled { get; set; } = new();
    public List<CalculatedFieldDto> Calculated { get; set; } = new();
    public List<ConditionalFormatDto> Formats { get; set; } = new();
    public bool ShowFormatsOnSheets { get; set; }
}

internal sealed class ScheduleFilterDto
{
    public string Field { get; set; } = string.Empty;
    public string Comparison { get; set; } = "Equals";
    public string Value { get; set; } = string.Empty;
}

internal sealed class CalculatedFieldDto
{
    public string Name { get; set; } = "Calculated";
    public string Kind { get; set; } = "Number";
    public string Formula { get; set; } = string.Empty;
    public string PercentageOf { get; set; } = string.Empty;
    public bool ByGroup { get; set; }
}

internal sealed class ConditionalFormatDto
{
    public string Field { get; set; } = string.Empty;
    public string Test { get; set; } = "LessThan";
    public string Value { get; set; } = string.Empty;
    public string Value2 { get; set; } = string.Empty;
    public string Background { get; set; } = "F8C8C8";
}

/// <summary>A type's identity, by its id.</summary>
internal sealed class TypeIdentityDto
{
    public Guid Id { get; set; }
    public string? TypeMark { get; set; }
    public string? AssemblyCode { get; set; }
    public string? Keynote { get; set; }
    public string? Manufacturer { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public decimal Cost { get; set; }
    public string? ClassificationCode { get; set; }
}

/// <summary>An angular dimension: its two lines and a point its arc passes through.</summary>
internal sealed class AngularDimensionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public Guid TypeId { get; set; }
    public DimensionEndDto First { get; set; } = new();
    public DimensionEndDto Second { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public string Override { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A radius, diameter or arc-length dimension: the curved wall's line it measures, and where it was placed.</summary>
internal sealed class RadialDimensionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public Guid TypeId { get; set; }
    public string Kind { get; set; } = "Radius";
    public DimensionEndDto Target { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public string Override { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class DimensionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }

    /// <summary>Its style; missing from older files, where every dimension takes the project's first.</summary>
    public Guid TypeId { get; set; }

    public DimensionEndDto Start { get; set; } = new();
    public DimensionEndDto End { get; set; } = new();

    /// <summary>A string's references between its ends.</summary>
    public List<DimensionEndDto>? Between { get; set; }

    public double Offset { get; set; } = 600;
    public string Override { get; set; } = string.Empty;

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

internal sealed class TagDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public Guid TargetId { get; set; }

    public string Field { get; set; } = "Mark";
    public double X { get; set; }
    public double Y { get; set; }
    public bool ShowLeader { get; set; } = true;

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

internal sealed class TextNoteDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }

    public string Text { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }

    public double? LeaderX { get; set; }
    public double? LeaderY { get; set; }

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

/// <summary>
/// A setting-out gridline. It carries no level, because a grid belongs to the building
/// rather than to a storey - that is what lets storeys be lined up against each other.
/// </summary>
internal sealed class GridDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }

    public bool BubbleAtStart { get; set; } = true;
    public bool BubbleAtEnd { get; set; } = true;

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
}

/// <summary>
/// A floor, ceiling or roof build-up. <see cref="Kind"/> says which, so the three share one
/// array rather than three identical ones.
/// </summary>
internal sealed class SlabTypeDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "Floor";
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Keynote { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }

    public string Function { get; set; } = "Architectural";
    public string FireRating { get; set; } = string.Empty;

    /// <summary>A floor separating dwellings - a compartment floor; none in a file from before.</summary>
    public bool? Separating { get; set; }
    public double AcousticRating { get; set; }
    public double HeatTransferCoefficient { get; set; }
    public string CoarseScaleFillColour { get; set; } = "#7A828E";

    public List<MaterialLayerDto> Layers { get; set; } = new();
}

/// <summary>A placed floor, ceiling or roof, with the outline that was sketched for it.</summary>
internal sealed class SlabDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public string Kind { get; set; } = "Floor";

    public double HeightOffset { get; set; }

    /// <summary>Outline vertices, flattened as x, y, x, y - compact and obvious in the file.</summary>
    public List<double> Boundary { get; set; } = new();

    /// <summary>
    /// For a roof, what each boundary edge does: one entry per edge, in the boundary's order.
    /// Empty on a floor or a ceiling, and on a roof saved before roofs could be pitched -
    /// which reads back as the flat roof it was.
    /// </summary>
    public List<RoofEdgeDto> RoofEdges { get; set; } = new();

    /// <summary>For a roof, how far above its base it is cut off flat. 0 lets it run to its ridge.</summary>
    public double RoofCutoff { get; set; }

    /// <summary>For a roof, the level its cutoff is measured from; null measures it from the roof's base.</summary>
    public Guid? RoofCutoffLevelId { get; set; }

    /// <summary>For a roof, how its eaves are cut. Absent in older files, which were all plumb cut.</summary>
    public string RoofRafterCut { get; set; } = "PlumbCut";

    /// <summary>Where a roof picked from walls bears on them: Truss or Rafter.</summary>
    public string RoofBearing { get; set; } = "Truss";

    /// <summary>The roof this one is joined to - Join Roof - or null.</summary>
    public Guid? RoofJoinedTo { get; set; }

    /// <summary>The dormers this roof is opened for.</summary>
    public List<Guid>? RoofDormerOpenings { get; set; }

    /// <summary>When the roof is a dormer's, made by the Dormer tool: what it was made as, and its walls.</summary>
    public RoofDormerDto? RoofDormer { get; set; }

    /// <summary>Holes drawn in the roof's sketch.</summary>
    public List<RoofOpeningDto>? RoofOpenings { get; set; }

    /// <summary>Holes sketched in a floor's or ceiling's outline, each flattened as x, y, x, y.</summary>
    public List<List<double>>? Holes { get; set; }

    /// <summary>A floor's span direction, degrees; null where none is given.</summary>
    public double? SpanDirection { get; set; }

    public double RoofFasciaDepth { get; set; } = 150;
    public bool RoofHighLevelVents { get; set; }
    public double RoofDrainageFall { get; set; } = 40;

    /// <summary>For a roof, the slope arrows drawn in its sketch.</summary>
    public List<RoofArrowDto> RoofArrows { get; set; } = new();

    /// <summary>For a roof by extrusion, its profile and how far it runs; null for a roof by footprint.</summary>
    public RoofExtrusionDto? RoofExtrusion { get; set; }

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
}

/// <summary>A roof by extrusion: the line its profile is drawn on, the profile, and where it runs across that line.</summary>
internal sealed class RoofExtrusionDto
{
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double DirectionX { get; set; } = 1;
    public double DirectionY { get; set; }

    /// <summary>Profile points as distance along, height above base: along, height, along, height.</summary>
    public List<double> Profile { get; set; } = new();

    /// <summary>How far each line of the profile bows into an arc; zero, or missing, for a straight one.</summary>
    public List<double>? Sagittas { get; set; }

    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary>A slope arrow drawn in a roof's sketch: where it runs, and how its slope is given.</summary>
internal sealed class RoofArrowDto
{
    public double TailX { get; set; }
    public double TailY { get; set; }
    public double HeadX { get; set; }
    public double HeadY { get; set; }
    public bool ByHeights { get; set; }
    public double SlopeDegrees { get; set; } = 30;
    public double TailOffset { get; set; }
    public double HeadOffset { get; set; }
}

/// <summary>What a roof does at one edge of its footprint: the whole shape of a roof is this, per edge.</summary>
internal sealed class RoofEdgeDto
{
    /// <summary>Which edge it is, for a fascia or gutter along it; absent in older files.</summary>
    public Guid? Id { get; set; }

    public bool DefinesSlope { get; set; }
    public double SlopeDegrees { get; set; } = 30;
    public double PlateOffset { get; set; }

    /// <summary>The wall the edge was picked from, which it follows; null for a drawn edge.</summary>
    public Guid? WallId { get; set; }

    public bool OnLeftOfWall { get; set; }
    public double Overhang { get; set; }
    public bool ExtendToCore { get; set; }

    /// <summary>The arc of the outline this edge is one straight piece of; null for a straight edge.</summary>
    public Guid? ArcId { get; set; }
}

/// <summary>One type of architectural column: its shape, size and what it is made of.</summary>
internal sealed class ColumnTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Shape { get; set; } = "Rectangular";

    public double Width { get; set; }
    public double Depth { get; set; }

    public Guid MaterialId { get; set; }
    public string CoarseScaleFillColour { get; set; } = string.Empty;

    /// <summary>A section drawn in the column editor: the outline first, then any holes in it.</summary>
    public List<List<double>>? ProfileLoops { get; set; }

    /// <summary>Type-wide offsets of the base and top. Absent in older files, where they were nothing.</summary>
    public double OffsetBase { get; set; }
    public double OffsetTop { get; set; }

    /// <summary>What the column does on the way up. Absent in older files, where it did nothing.</summary>
    public double TopScale { get; set; } = 1;
    public double Twist { get; set; }
    public double SlantAcross { get; set; }
    public double SlantAlong { get; set; }
    public int Flutes { get; set; }
    public double FluteDepth { get; set; } = 25;
    public double BaseHeight { get; set; }
    public double BaseSpread { get; set; } = 0.18;
    public double CapitalHeight { get; set; }
    public double CapitalSpread { get; set; } = 0.22;

    /// <summary>A structural column type's: its section's flanges, web or wall, what it is made of, its fire rating and protection. Absent in older files, where every column was architectural.</summary>
    public bool Structural { get; set; }
    public string? StructuralMaterial { get; set; }
    public double FlangeThickness { get; set; }
    public double WebThickness { get; set; }
    public double WallThickness { get; set; }
    public string? FireRating { get; set; }
    public bool PreservativeTreated { get; set; }
    public bool RustProtected { get; set; } = true;

    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Keynote { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }
}

/// <summary>
/// An architectural column. Its height is not stored: the constraints and attachments that
/// decide it are, so a column reopened against a roof is as high as that roof is now.
/// </summary>
internal sealed class ColumnDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }

    public double LocationX { get; set; }
    public double LocationY { get; set; }
    public double Rotation { get; set; }

    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; } = 3000;

    /// <summary>Whether it is a structural column. Absent in older files, where none was.</summary>
    public bool Structural { get; set; }

    public Guid? TopAttachedTo { get; set; }
    public Guid? BaseAttachedTo { get; set; }

    /// <summary>Whether it comes along when the grid it stands on moves. Absent in older files, where it did.</summary>
    public bool MovesWithGrids { get; set; } = true;

    /// <summary>Whether it takes its area out of the room it stands in. Absent in older files, where it did.</summary>
    public bool RoomBounding { get; set; } = true;

    /// <summary>Whether walls are taken out of it. Absent in older files, where they were not.</summary>
    public bool CutByWalls { get; set; } = true;

    /// <summary>How it sits against the walls. Absent in older files, where it was freestanding.</summary>
    public string Placement { get; set; } = "Freestanding";
    public string PlacementFace { get; set; } = "Interior";

    public string TopAttachmentStyle { get; set; } = "CutColumn";
    public string BaseAttachmentStyle { get; set; } = "CutColumn";

    public double OffsetFromAttachmentAtTop { get; set; }
    public double OffsetFromAttachmentAtBase { get; set; }

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
    public string? PhaseDemolished { get; set; }
}

/// <summary>One type of a loadable component family: its category, form, size and placement.</summary>
internal sealed class ComponentTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Furniture";
    public string Form { get; set; } = "Box";
    public string Placement { get; set; } = "Freestanding";

    public double Width { get; set; }
    public double Depth { get; set; }
    public double Height { get; set; }
    public double DefaultElevation { get; set; }

    /// <summary>Whether one put down near a wall backs onto it; none in a file from before, to go by its form.</summary>
    public bool? AgainstWall { get; set; }

    /// <summary>A shower's glass: none, alcove, corner or walk-in.</summary>
    public string? Enclosure { get; set; }

    /// <summary>An extract fan's or cooker hood's extract rate, l/s; none in a file from before.</summary>
    public double? ExtractRate { get; set; }

    /// <summary>A light's efficacy, lm/W; none in a file from before.</summary>
    public double? Efficacy { get; set; }

    /// <summary>A tree's water demand, and its stem's diameter; none in a file from before.</summary>
    public string? WaterDemand { get; set; }

    public double? StemDiameter { get; set; }

    /// <summary>A boiler's hot water, where it heats a cylinder's rather than its own as a combi; none for a combi.</summary>
    public string? HotWater { get; set; }

    /// <summary>What Approved Document G's water efficiency calculator counts of it; none in a file from before.</summary>
    public double? FlowRate { get; set; }
    public double? FlushVolume { get; set; }
    public double? PartFlushVolume { get; set; }
    public double? Capacity { get; set; }
    public double? WaterPerUse { get; set; }

    /// <summary>An access point's kind: a rodding eye, an access fitting, an inspection chamber or a manhole.</summary>
    public string? Access { get; set; }

    /// <summary>A heat recovery unit's airflow, available pressure and efficiency; none in a file from before.</summary>
    public double? Airflow { get; set; }
    public double? AvailablePressure { get; set; }
    public double? HeatRecovery { get; set; }

    /// <summary>A valve's pressure loss at its rated airflow, a fan's continuous rate, a background ventilator's equivalent area; none in a file from before.</summary>
    public double? PressureLoss { get; set; }
    public double? RatedAirflow { get; set; }
    public double? ContinuousRate { get; set; }
    public double? VentArea { get; set; }

    /// <summary>What it draws from its circuit, W; a light's lumens, how it spreads them and its photometric data; a consumer unit's ways, supply and Ze - none in a file from before.</summary>
    public double? Load { get; set; }
    public double? Lumens { get; set; }
    public string? Distribution { get; set; }
    public PhotometryDto? Photometry { get; set; }
    public int? Ways { get; set; }
    public double? SupplyRating { get; set; }
    public double? ExternalImpedance { get; set; }

    /// <summary>What it heats with: its heat output, a radiator's exponent, a floor's output and pipe spacing; none in a file from before.</summary>
    public double? HeatOutput { get; set; }
    public double? HeatExponent { get; set; }
    public double? FloorOutput { get; set; }
    public double? FloorOutputLimit { get; set; }
    public double? PipeSpacing { get; set; }

    /// <summary>A family of your own's solids, in its box.</summary>
    public List<ComponentSolidDto>? Solids { get; set; }

    /// <summary>A family of your own's 3D model, in its box.</summary>
    public ComponentMeshDto? Mesh { get; set; }

    public Guid MaterialId { get; set; }
    public string Colour { get; set; } = string.Empty;

    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Keynote { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }
}

/// <summary>A component placed in the model: where it stands, which way it faces, what carries it.</summary>
internal sealed class ComponentDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }

    public double LocationX { get; set; }
    public double LocationY { get; set; }
    public double Rotation { get; set; }
    public double Elevation { get; set; }

    /// <summary>The face carrying it: a wall, or a floor, ceiling or roof.</summary>
    public Guid HostId { get; set; }

    /// <summary>What it was called when only a wall could carry one.</summary>
    public Guid HostWallId { get; set; }

    public bool FlipFacing { get; set; }

    /// <summary>Whether it is the mirror image of its family.</summary>
    public bool FlipHand { get; set; }

    /// <summary>The floor or flat roof it stands on, its elevation from that slab's top.</summary>
    public Guid? StandsOn { get; set; }

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
    public string? PhaseDemolished { get; set; }
}

/// <summary>
/// A room. Only where it is and what it is called are stored: its shape, area and volume are
/// traced from the walls when the project is opened, so they can never be saved stale.
/// </summary>
internal sealed class RoomDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }

    public double LocationX { get; set; }
    public double LocationY { get; set; }

    public string Name { get; set; } = "Room";
    public string Number { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Occupancy { get; set; } = string.Empty;
    public int OccupantCount { get; set; }

    public double BaseOffset { get; set; }
    public double UpperLimitOffset { get; set; } = 3000;

    public string FloorFinish { get; set; } = string.Empty;
    public string WallFinish { get; set; } = string.Empty;
    public string CeilingFinish { get; set; } = string.Empty;
    public string BaseFinish { get; set; } = string.Empty;
    public string? ClassificationCode { get; set; }

    /// <summary>The temperature it is heated to where set, °C, and whether it is not heated; none by its use.</summary>
    public double? DesignTemperature { get; set; }
    public bool? Unheated { get; set; }

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
}

internal sealed class ComponentSolidDto
{
    public string Name { get; set; } = string.Empty;
    public string Shape { get; set; } = "Box";
    public string Finish { get; set; } = "Body";
    public double From { get; set; }
    public double To { get; set; }
    public double Back { get; set; }
    public double Front { get; set; }
    public double Bottom { get; set; }
    public double Top { get; set; }
}

/// <summary>A family's 3D model, packed: its corners as 32-bit floats and its triangles and edges as 32-bit indices, each in base 64.</summary>
internal sealed class ComponentMeshDto
{
    public string Source { get; set; } = string.Empty;
    public string Positions { get; set; } = string.Empty;
    public string Triangles { get; set; } = string.Empty;
    public string Edges { get; set; } = string.Empty;
}

internal sealed class ProjectInformationDto
{
    public string Name { get; set; } = "Untitled Project";
    public string Number { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string BuildingType { get; set; } = string.Empty;

    /// <summary>Whose rules clearances are checked against; none in a file from before, for Approved Document M.</summary>
    public string? ClearanceStandard { get; set; }
    public string? Units { get; set; }

    public bool Sprinklered { get; set; }

    /// <summary>Whether Approved Document G's optional requirement of 110 litres a person a day applies.</summary>
    public bool WaterOptionalRequirement { get; set; }

    /// <summary>The ground the footings are sized for, and how far the frost and snow go. Absent in older files: firm clay, no shrinkage set, no frost line, 20 psf.</summary>
    public string? Ground { get; set; }
    public string? ClayShrinkage { get; set; }
    public double FrostDepth { get; set; }
    public double GroundSnowLoad { get; set; } = 20;

    /// <summary>A US house's water pressure where it comes in, psi; 40 in older files.</summary>
    public double WaterPressure { get; set; } = 40;

    /// <summary>The wind pressure on the cladding, kPa; 1 kPa in older files.</summary>
    public double WindPressure { get; set; } = 1.0;

    /// <summary>The site's basic wind speed, altitude and terrain; none in older files.</summary>
    public double BasicWindSpeed { get; set; }
    public double SiteAltitude { get; set; }
    public string? Terrain { get; set; }

    /// <summary>What its heat loss is worked to: a UK location, a US winter design temperature (°F), its air permeability; none in older files.</summary>
    public string? HeatingLocation { get; set; }
    public double? WinterDesignTemperature { get; set; }
    public double? AirPermeability { get; set; }
}

/// <summary>A footing's type: a strip or a pad, its sizes, and what it and what stands on it are made of.</summary>
internal sealed class FootingTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Footing";
    public string TypeMark { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public double Width { get; set; } = 600;
    public double Length { get; set; } = 600;
    public double Thickness { get; set; } = 225;
    public Guid MaterialId { get; set; }
    public Guid StemMaterialId { get; set; }
    public string? AssemblyCode { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>A strip footing: the wall it is under, how deep, and how far off its middle.</summary>
internal sealed class WallFootingDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public Guid WallId { get; set; }
    public double Depth { get; set; } = 1000;
    public double Eccentricity { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

/// <summary>A pad footing: the column it carries, or where it stands, and how deep.</summary>
internal sealed class PadFootingDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public Guid? ColumnId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Rotation { get; set; }
    public double Depth { get; set; } = 1000;
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class LevelDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Elevation { get; set; }

    /// <summary>The floor whose top the level is at, where its storey is built on the one below.</summary>
    public Guid? StandsOn { get; set; }
}

internal sealed class MaterialDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Density { get; set; }
    public double ThermalConductivity { get; set; }
    public string SurfaceColour { get; set; } = "#9A9A9A";
    public string CutColour { get; set; } = "#7A7A7A";
    public string Manufacturer { get; set; } = string.Empty;
    public string ClassificationCode { get; set; } = string.Empty;
    public decimal CostPerCubicMetre { get; set; }
    public string? Keynote { get; set; }
}

internal sealed class MaterialLayerDto
{
    public string Function { get; set; } = "Structure";
    public Guid MaterialId { get; set; }
    public double Thickness { get; set; }
    public bool Wraps { get; set; } = true;
}

internal sealed class WallSweepDto
{
    public string Kind { get; set; } = "Sweep";
    public string Profile { get; set; } = "Rectangle";
    public string Side { get; set; } = "Exterior";
    public double Depth { get; set; }
    public double Height { get; set; }
    public double Elevation { get; set; }
    public bool FromTop { get; set; }
    public Guid MaterialId { get; set; }
    public double Offset { get; set; }
    public bool Flip { get; set; }
    public double Setback { get; set; }
    public bool CutsWall { get; set; }
    public bool Cuttable { get; set; } = true;
    public Guid? ProfileId { get; set; }
    public bool Returns { get; set; }
}

internal sealed class StackedWallTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Stacked";
    public string TypeMark { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }

    /// <summary>Bottom tier first. A height of zero is the variable tier.</summary>
    public List<StackTierDto> Tiers { get; set; } = new();
}

internal sealed class CurtainWallTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Curtain Wall";
    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public string Function { get; set; } = "Exterior";

    public string VerticalLayout { get; set; } = "FixedDistance";
    public double VerticalSpacing { get; set; } = 1500;
    public int VerticalCount { get; set; } = 4;
    public string VerticalJustification { get; set; } = "Centre";
    public string HorizontalLayout { get; set; } = "FixedDistance";
    public double HorizontalSpacing { get; set; } = 1500;
    public int HorizontalCount { get; set; } = 2;
    public string HorizontalJustification { get; set; } = "Beginning";

    public double PanelThickness { get; set; } = 25;
    public Guid GlassMaterialId { get; set; }
    public Guid SolidMaterialId { get; set; }

    public string MullionProfile { get; set; } = "Rectangular";
    public double MullionWidth { get; set; } = 50;
    public double MullionDepth { get; set; } = 150;
    public Guid MullionMaterialId { get; set; }
    public bool BorderMullions { get; set; } = true;
    public bool AutomaticallyEmbed { get; set; }

    /// <summary>Its U-value and whether its glass is safety glass; none in a file from before.</summary>
    public double? HeatTransferCoefficient { get; set; }
    public bool? SafetyGlazing { get; set; }

    /// <summary>The mullion type on each line: none saved, its own mullion; an empty id, none there. Absent in older files.</summary>
    public Guid? InteriorVertical { get; set; }
    public Guid? BorderVerticalStart { get; set; }
    public Guid? BorderVerticalEnd { get; set; }
    public Guid? InteriorHorizontal { get; set; }
    public Guid? BorderHorizontalBottom { get; set; }
    public Guid? BorderHorizontalTop { get; set; }
    public string? JoinCondition { get; set; }

    /// <summary>Its mullions' anchors and splices; none in older files.</summary>
    public bool MullionAnchors { get; set; }
    public string? Splices { get; set; }
    public double? SpliceAbove { get; set; }
    public double? StackJoint { get; set; }
    public double AnchorCapacity { get; set; }
}

internal sealed class MullionTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Mullion";
    public string TypeMark { get; set; } = string.Empty;
    public string? Shape { get; set; }
    public double Width1 { get; set; } = 25;
    public double Width2 { get; set; } = 25;
    public double Thickness { get; set; } = 150;
    public double Offset { get; set; }
    public double Leg1 { get; set; } = 100;
    public double Leg2 { get; set; } = 100;
    public double CapDepth { get; set; }
    public double WallThickness { get; set; } = 3;
    public string? Material { get; set; }
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class CurtainSegmentDto
{
    public bool Vertical { get; set; }
    public double Line { get; set; }
    public double From { get; set; }
    public double To { get; set; }
}

internal sealed class CurtainMullionDto
{
    public bool Vertical { get; set; }

    /// <summary>The line: along the wall for an upright, up it for a transom.</summary>
    public double Line { get; set; }

    /// <summary>A point in the stretch: up the wall for an upright, along it for a transom.</summary>
    public double At { get; set; }

    public Guid? TypeId { get; set; }
}

internal sealed class CurtainPanelDto
{
    public int Column { get; set; }
    public int Row { get; set; }
    public string Kind { get; set; } = "Glazed";

    /// <summary>For a door panel, which way it is hung and which way it opens.</summary>
    public bool FlipHand { get; set; }
    public bool FlipFacing { get; set; }

    /// <summary>For a door panel, whether it is drawn standing open.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Where a window in the panel sits, from the middle of it: along the wall and up it.</summary>
    public double OffsetAlong { get; set; }
    public double OffsetUp { get; set; }

    /// <summary>For a glazed panel, what the pane is; absent means clear glass.</summary>
    public string? Glass { get; set; }

    /// <summary>The door or window type this panel is; null for plain glass or a panel with no type named.</summary>
    public Guid? OpeningTypeId { get; set; }

    /// <summary>What the type was called before windows could be panels too. Read, never written.</summary>
    public Guid? DoorTypeId { get; set; }
}

internal sealed class StackTierDto
{
    public Guid WallTypeId { get; set; }
    public double Height { get; set; }
}

internal sealed class WallBandDto
{
    public int Layer { get; set; }
    public double Bottom { get; set; }
    public double? Top { get; set; }
    public Guid MaterialId { get; set; }
}

internal sealed class WallTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Keynote { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }

    public string Function { get; set; } = "Interior";

    /// <summary>Whether it is a party wall, separating dwellings.</summary>
    public bool Separating { get; set; }
    public string FireRating { get; set; } = string.Empty;
    public double AcousticRating { get; set; }
    public double ThermalResistance { get; set; }
    public double HeatTransferCoefficient { get; set; }
    /// <summary>Yes/no in files written before the wrapping choices; kept so those still open.</summary>
    public bool WrapAtInserts { get; set; } = true;
    public bool WrapAtEnds { get; set; }

    /// <summary>"None", "Exterior", "Interior" or "Both". Empty in older files.</summary>
    public string WrappingAtInserts { get; set; } = string.Empty;
    public string WrappingAtEnds { get; set; } = string.Empty;

    /// <summary>How far tapered walls of this type lean in on each face, in degrees.</summary>
    public double ExteriorTaperAngle { get; set; }
    public double InteriorTaperAngle { get; set; }

    /// <summary>Profiles run along the faces of walls of this type. Missing from older files.</summary>
    public List<WallSweepDto> Sweeps { get; set; } = new();
    public List<WallBandDto>? Bands { get; set; }
    public string? LogShape { get; set; }
    public double? LogCourse { get; set; }
    public double? LogOverhang { get; set; }
    public string CoarseScaleFillColour { get; set; } = "#8A93A1";

    public List<MaterialLayerDto> Layers { get; set; } = new();
}

/// <summary>Fields shared by door and window types. Written out flat in each array.</summary>
internal abstract class OpeningTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string AssemblyCode { get; set; } = string.Empty;
    public string Keynote { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Cost { get; set; }

    public double Width { get; set; } = 900;
    public double Height { get; set; } = 2100;
    public double Thickness { get; set; } = 100;
    public string FrameMaterial { get; set; } = string.Empty;
    public string FireRating { get; set; } = string.Empty;
    public double AcousticRating { get; set; }
    public double HeatTransferCoefficient { get; set; }

    /// <summary>Whether its glass is safety glass; none in a file from before.</summary>
    public bool? SafetyGlazing { get; set; }
}

internal sealed class DoorTypeDto : OpeningTypeDto
{
    public string Operation { get; set; } = "Swing";
    public int LeafCount { get; set; } = 1;
    public string PanelMaterial { get; set; } = string.Empty;
    public string HardwareSet { get; set; } = string.Empty;

    /// <summary>The leaf design, and the glazing and trim it is drawn with. Absent in projects saved before designs.</summary>
    public string? LeafDesign { get; set; }
    public int GlazingRows { get; set; }
    public int GlazingColumns { get; set; }
    public string? Function { get; set; }
    public double? TrimWidth { get; set; }
    public double? TrimProjectionExterior { get; set; }
    public double? TrimProjectionInterior { get; set; }

    /// <summary>Whether it is a curtain wall door, one that replaces a panel of a curtain wall.</summary>
    public bool CurtainPanel { get; set; }

    /// <summary>Whether it closes itself; none in a file from before.</summary>
    public bool? SelfClosing { get; set; }
}

internal sealed class WindowTypeDto : OpeningTypeDto
{
    public string Operation { get; set; } = "Casement";
    public string GlazingType { get; set; } = string.Empty;

    /// <summary>Panes up and across each light; absent in files from before divided lights.</summary>
    public int GlazingRows { get; set; }
    public int GlazingColumns { get; set; }
    public double SolarHeatGainCoefficient { get; set; }

    /// <summary>Its glazing's light transmittance; none in a file from before.</summary>
    public double? LightTransmittance { get; set; }

    /// <summary>Its trickle vent's equivalent area, mm²; none in a file from before, or without one.</summary>
    public double? TrickleVent { get; set; }

    /// <summary>Its lights side by side, which open, its transom and fanlights, and its mullions' width; absent in files from before divided windows.</summary>
    public int? Lights { get; set; }
    public string? LightPattern { get; set; }
    public double TransomHeight { get; set; }
    public string? Fanlight { get; set; }
    public double? MullionWidth { get; set; }
}

/// <summary>
/// A placed door or window. Its position is a distance along the host wall, not a point, so
/// it survives the wall being moved - the same reason the model stores it that way.
/// </summary>
internal abstract class OpeningDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public Guid HostWallId { get; set; }

    public double DistanceAlongWall { get; set; }
    public double SillHeight { get; set; }
    public bool FlipFacing { get; set; }
    public bool FlipHand { get; set; }

    /// <summary>Whether it is drawn standing open.</summary>
    public bool IsOpen { get; set; }

    /// <summary>"Vertical" or "Slanted" in a slanted wall. Absent in files from before it was a choice.</summary>
    public string? Orientation { get; set; }

    /// <summary>This opening's own size, when it is not the size its type says.</summary>
    public double? WidthOverride { get; set; }
    public double? HeightOverride { get; set; }

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
    public string? PhaseDemolished { get; set; }
}

internal sealed class DoorDto : OpeningDto
{
    public double SwingAngle { get; set; } = 90;
    public string? FrameType { get; set; }
    public string? FrameMaterial { get; set; }
    public string? Finish { get; set; }
}

internal sealed class WindowDto : OpeningDto
{
}

internal sealed class WallFaceRegionDto
{
    public string Face { get; set; } = "Exterior";
    public double? From { get; set; }
    public double? To { get; set; }
    public double? Bottom { get; set; }
    public double? Top { get; set; }
    public Guid? MaterialId { get; set; }
}

internal sealed class WallDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }

    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }

    public string LocationLine { get; set; } = "WallCentreline";
    public bool Flipped { get; set; }
    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; } = 3000;
    public bool RoomBounding { get; set; } = true;
    public string StructuralUsage { get; set; } = "NonBearing";

    /// <summary>How each end joins: "Auto", "Mitre", "Butt", "RunThrough", "SquareOff" or "Disallow".</summary>
    /// <summary>How far the wall bows: 0 for straight. See WallCurve for the convention.</summary>
    public double Bulge { get; set; }

    /// <summary>An elliptical wall's shape: semi-axis ratio and parameter range. Absent for straight and arc walls.</summary>
    public double? EllipseRatio { get; set; }
    public double? EllipseFrom { get; set; }
    public double? EllipseTo { get; set; }

    /// <summary>A spline wall's points between its ends as x, y pairs in its own frame, and the stretch of it this wall is. Absent otherwise.</summary>
    /// <summary>The walls this one is joined to face to face, and whether it is locked to them.</summary>
    public List<Guid>? JoinedTo { get; set; }
    public bool LockedToJoined { get; set; }

    /// <summary>Whether the corners at the wall's start and end are locked.</summary>
    public bool StartLocked { get; set; }
    public bool EndLocked { get; set; }

    /// <summary>How the joins at the wall's start and end are cleaned up in plan: "UseViewSetting", "Clean" or "DontClean".</summary>
    public string? StartCleanup { get; set; }
    public string? EndCleanup { get; set; }

    public List<double>? SplinePoints { get; set; }
    public double? SplineFrom { get; set; }
    public double? SplineTo { get; set; }

    /// <summary>An edited elevation outline as x, y pairs (along, height above base), and the wall length it was edited at.</summary>
    public List<double>? Profile { get; set; }
    public double ProfileLength { get; set; }
    public List<WallFaceRegionDto>? FaceRegions { get; set; }

    /// <summary>A curtain wall's own grid lines, when it does not follow its type's.</summary>
    public List<double>? CurtainVerticals { get; set; }
    public List<double>? CurtainHorizontals { get; set; }

    /// <summary>Stretches taken out of those lines, which make the bays either side one panel.</summary>
    public List<CurtainSegmentDto>? CurtainRemoved { get; set; }

    public List<CurtainPanelDto>? CurtainPanels { get; set; }

    /// <summary>Mullions chosen on their own: a stretch of a line, and the mullion type on it - none when no type is given.</summary>
    public List<CurtainMullionDto>? CurtainMullions { get; set; }

    /// <summary>What the whole curtain wall is glazed with, for panels that do not say otherwise.</summary>
    public string? CurtainGlass { get; set; }

    /// <summary>The slabs the top and base are attached to, if any.</summary>
    public Guid? TopAttachedTo { get; set; }
    public Guid? BaseAttachedTo { get; set; }

    /// <summary>"Vertical", "Slanted", "DoubleSlanted" or "Tapered", and the angles for them, in degrees.</summary>
    public string CrossSection { get; set; } = "Vertical";
    public double SlantAngle { get; set; }

    /// <summary>How far the wall body is shifted across its location line, exterior positive.</summary>
    public double AcrossOffset { get; set; }

    /// <summary>A double-slanted wall's lean above its break, in degrees, and the break's height above the base in millimetres.</summary>
    public double UpperSlantAngle { get; set; }
    public double SlantBreakHeight { get; set; }
    public bool OverrideTaper { get; set; }
    public double ExteriorTaper { get; set; }
    public double InteriorTaper { get; set; }

    public string StartJoin { get; set; } = "Auto";
    public string EndJoin { get; set; } = "Auto";

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
    public string? PhaseDemolished { get; set; }
}

internal sealed class SweepProfileTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Profile";
    public string Description { get; set; } = string.Empty;

    /// <summary>The outline as out, up pairs, millimetres.</summary>
    public List<double> Points { get; set; } = new();
}

internal sealed class WallSweepTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Sweep";
    public string Kind { get; set; } = "Sweep";
    public string Profile { get; set; } = "Rectangle";
    public Guid? ProfileId { get; set; }
    public double Depth { get; set; } = 20;
    public double Height { get; set; } = 100;
    public Guid MaterialId { get; set; }
    public bool CutsWall { get; set; }
    public bool Cuttable { get; set; } = true;
    public double Setback { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>A rectangular opening cut through a wall.</summary>
internal sealed class WallOpeningDto
{
    public Guid Id { get; set; }
    public Guid HostWallId { get; set; }
    public Guid LevelId { get; set; }
    public double DistanceAlongWall { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double SillHeight { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }

    /// <summary>Cut for the services; none in a file from before, or for any other opening.</summary>
    public bool? ForServices { get; set; }
}

internal sealed class FasciaTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Fascia";
    public string TypeMark { get; set; } = string.Empty;
    public double Thickness { get; set; } = 25;
    public double Depth { get; set; }
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Profile { get; set; }
}

internal sealed class SoffitTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Soffit";
    public string TypeMark { get; set; } = string.Empty;
    public double Thickness { get; set; } = 12;
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Board { get; set; }
    public double FreeAirArea { get; set; }
}

/// <summary>An eave's type: its kind and what it is built of.</summary>
internal sealed class EaveTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Eave";
    public string TypeMark { get; set; } = string.Empty;
    public string? Kind { get; set; }
    public double RafterWidth { get; set; } = 47;
    public double RafterDepth { get; set; }
    public double RafterSpacing { get; set; } = 400;
    public string? Tail { get; set; }
    public double DeckThickness { get; set; } = 18;
    public double SoffitThickness { get; set; } = 12;
    public string? Board { get; set; }
    public double FreeAirArea { get; set; }
    public double FriezeDepth { get; set; }
    public double FriezeThickness { get; set; } = 22;
    public string? Moulding { get; set; }
    public double MouldingSize { get; set; } = 45;
    public string? Bracket { get; set; }
    public double BracketSpacing { get; set; } = 1200;
    public double BracketHeight { get; set; } = 300;
    public double BracketDepth { get; set; }
    public double BracketWidth { get; set; } = 75;
    public string? Underside { get; set; }
    public Guid MaterialId { get; set; }
    public Guid SoffitMaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class GutterTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Gutter";
    public string TypeMark { get; set; } = string.Empty;
    public string Shape { get; set; } = "HalfRound";
    public double Width { get; set; } = 125;
    public double Depth { get; set; } = 75;
    public double WallThickness { get; set; } = 4;
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
    public double HangerSpacing { get; set; } = 600;
    public bool LeafGuard { get; set; }
    public string? DownpipeShape { get; set; }
    public double DownpipeWidth { get; set; } = 68;
    public double DownpipeDepth { get; set; } = 68;
}

internal sealed class SlabEdgeTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Slab Edge";
    public string? TypeMark { get; set; }
    public string? Shape { get; set; }
    public double Width { get; set; } = 450;
    public double Depth { get; set; } = 450;
    public double FootWidth { get; set; } = 250;
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string? Description { get; set; }
}

internal sealed class SlabEdgeDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid FloorId { get; set; }
    public Guid LevelId { get; set; }

    /// <summary>Each edge's two ends, flattened as x, y, x, y.</summary>
    public List<double> Edges { get; set; } = new();

    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class RoofDrainDto
{
    public Guid Id { get; set; }
    public Guid RoofId { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double OutletDiameter { get; set; } = 100;
    public string? Discharge { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class PolygonWallDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string? Kind { get; set; }
    public List<double>? Outline { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartThickness { get; set; }
    public double EndThickness { get; set; }
    public string? StraightSide { get; set; }
    public Guid MaterialId { get; set; }
    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; } = 3000;
    public Guid? BaseAttachedTo { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class WallFramingDto
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public Guid LevelId { get; set; }
    public string? Material { get; set; }
    public string? Section { get; set; }
    public double Spacing { get; set; } = 600;
    public int TopPlates { get; set; } = 2;
    public int NoggingRows { get; set; } = 1;
    public bool FromEnd { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class PartDto
{
    public Guid Id { get; set; }
    public Guid HostId { get; set; }
    public Guid LevelId { get; set; }
    public int Layer { get; set; }
    public double? From { get; set; }
    public double? To { get; set; }
    public double? Bottom { get; set; }
    public double? Top { get; set; }
    public double Gap { get; set; }
    public Guid? MaterialId { get; set; }
    public bool Excluded { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

internal sealed class StairTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string Construction { get; set; } = "Timber";
    public double MaxRiser { get; set; }
    public double Going { get; set; }
    public double Nosing { get; set; }
    public double TreadThickness { get; set; }
    public bool OpenRisers { get; set; }
    public bool RiserBars { get; set; }
    public string Support { get; set; } = "ClosedStrings";
    public string Balustrade { get; set; } = "Balusters";
    public string HandrailProfile { get; set; } = "Round";
    public double? HandrailWidth { get; set; }
    public double? HandrailDepth { get; set; }
    public double? WallClearance { get; set; }
    public double HandrailExtension { get; set; }
    public string HandrailEnds { get; set; } = "ReturnToWall";
    public double? GuardHeight { get; set; }
    public bool LowerRail { get; set; }
    public string NosingProfile { get; set; } = "Square";
    public Guid RiserMaterialId { get; set; }
    public Guid StringMaterialId { get; set; }
    public Guid RailMaterialId { get; set; }
    public Guid BalusterMaterialId { get; set; }
    public double HandrailHeight { get; set; }
    public double BalusterSpacing { get; set; }
    public string Colour { get; set; } = string.Empty;
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class StairDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Angle { get; set; }
    public string Shape { get; set; } = "Straight";
    public string Corner { get; set; } = "Landing";
    public bool TurnsLeft { get; set; }
    public double Width { get; set; }
    public int? RisersBeforeTurn { get; set; }
    public double Well { get; set; }
    public double InnerRadius { get; set; }
    public List<double>? PathLengths { get; set; }
    public List<double>? PathTurns { get; set; }
    public Guid? RepeatToLevelId { get; set; }
    public bool ShowRiserNumbers { get; set; }
    public string Use { get; set; } = "Private";
    public bool LoftConversion { get; set; }
    public bool Outside { get; set; }
    public string BottomStep { get; set; } = "Plain";
    public bool ShowFlightDimensions { get; set; }
    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; }
    public string Railings { get; set; } = "Auto";
    public bool CutsFloorAbove { get; set; } = true;
    public bool CutsRoof { get; set; } = true;
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class RampTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string Construction { get; set; } = "Solid";
    public double Thickness { get; set; }
    public double KerbHeight { get; set; }
    public double KerbWidth { get; set; }
    public double HandrailHeight { get; set; }
    public double HandrailDiameter { get; set; }
    public double HandrailExtension { get; set; }
    public bool LowerRail { get; set; }
    public string Guarding { get; set; } = "Balusters";
    public double PostSpacing { get; set; }
    public double LongestFlight { get; set; }
    public string Colour { get; set; } = string.Empty;
    public Guid MaterialId { get; set; }
    public Guid RailMaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class RampDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Angle { get; set; }
    public string Anchor { get; set; } = "Foot";
    public string Shape { get; set; } = "Straight";
    public bool TurnsLeft { get; set; }
    public double Width { get; set; }
    public double Gap { get; set; }
    public List<double>? PathLengths { get; set; }
    public List<double>? PathTurns { get; set; }
    public double Gradient { get; set; }
    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; }
    public double TopLanding { get; set; }
    public double BottomLanding { get; set; }
    public string Building { get; set; } = "Dwelling";
    public bool Outside { get; set; } = true;
    public string Railings { get; set; } = "Auto";
    public bool ClosedUnderneath { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class RailingTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public double Height { get; set; }
    public string Infill { get; set; } = "Balusters";
    public string TopRailProfile { get; set; } = "Rectangular";
    public double TopRailWidth { get; set; }
    public double TopRailDepth { get; set; }
    public double PostSize { get; set; }
    public double PostSpacing { get; set; }
    public double BalusterSize { get; set; }
    public double BalusterSpacing { get; set; }
    public double BottomGap { get; set; }
    public double GlassThickness { get; set; }
    public string Colour { get; set; } = string.Empty;
    public Guid MaterialId { get; set; }
    public Guid InfillMaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Use { get; set; }
    public bool? HasTopRail { get; set; }
    public bool? GlassLaminated { get; set; }
    public double? GlassJoint { get; set; }
    public double? ChannelHeight { get; set; }
    public double? PanelThickness { get; set; }
    public int? IntermediateRails { get; set; }
    public string? Handrail { get; set; }
    public double? HandrailHeight { get; set; }
    public double? HandrailDiameter { get; set; }
    public double? HandrailClearance { get; set; }
    public double? HandrailExtension { get; set; }
    public string? HandrailEnds { get; set; }
    public double? LineLoad { get; set; }
    public double? PointLoad { get; set; }
}

internal sealed class GroundRegionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Boundary { get; set; }
    public string Surface { get; set; } = "Lawn";
    public Guid MaterialId { get; set; }
    public bool Approach { get; set; }
    public double Offset { get; set; }

    /// <summary>A path's middle, x, y, x, y, and its width; none for a region drawn by its outline.</summary>
    public List<double>? PathLine { get; set; }
    public double PathWidth { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class GroupDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public string? Name { get; set; }
    public double OriginX { get; set; }
    public double OriginY { get; set; }
    public double Rotation { get; set; }
    public bool Mirrored { get; set; }
    public List<Guid>? MemberIds { get; set; }
    public string Comments { get; set; } = string.Empty;
}

internal sealed class MassDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Boundary { get; set; }
    public bool Round { get; set; }
    public double CentreX { get; set; }
    public double CentreY { get; set; }
    public double Radius { get; set; }
    public double BaseOffset { get; set; }
    public double Height { get; set; } = 9000;
    public string? Top { get; set; }
    public double Pitch { get; set; } = 35;
    public double TopScale { get; set; } = 0.5;
    public List<Guid>? FloorLevels { get; set; }
    public List<MassLinkDto>? Links { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class MassLinkDto
{
    public Guid ElementId { get; set; }
    public string? Kind { get; set; }
    public int Face { get; set; }
    public Guid? LevelId { get; set; }
}

internal sealed class SitePadDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Boundary { get; set; }
    public double HeightOffset { get; set; }
    public double Fall { get; set; }
    public double FallDirection { get; set; }
    public double Batter { get; set; }
    public string Surface { get; set; } = "Paving";
    public Guid MaterialId { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class PropertyLineDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Boundary { get; set; }
    public double Setback { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class SpotElevationDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class BeamSystemDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Boundary { get; set; }
    public double? Direction { get; set; }
    public double Spacing { get; set; }
    public double Offset { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class ToposurfaceDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Points { get; set; }
    public List<double>? ExistingPoints { get; set; }
    public Guid MaterialId { get; set; }
    public double ContourInterval { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class BeamTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string Section { get; set; } = "Rectangular";
    public string Material { get; set; } = "Timber";
    public double Depth { get; set; }
    public double Width { get; set; }
    public double WebThickness { get; set; }
    public double FlangeThickness { get; set; }
    public Guid MaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class DuctTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string Material { get; set; } = "GalvanisedSteel";
    public string Shape { get; set; } = "Round";
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class DuctDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartOffset { get; set; }
    public double EndOffset { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Kind { get; set; } = "SupplyAir";
    public Guid? SystemId { get; set; }

    /// <summary>What keeps a fire-separating wall it passes; none where unprotected.</summary>
    public string? FireProtection { get; set; }

    /// <summary>Its damper's rating, minutes, and its insulation, mm; none in a file from before.</summary>
    public int? DamperRating { get; set; }
    public double? Insulation { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class PipeTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TypeMark { get; set; } = string.Empty;
    public string Material { get; set; } = "Copper";
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class PipeDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartOffset { get; set; }
    public double EndOffset { get; set; }
    public double Diameter { get; set; }
    public string Kind { get; set; } = "ColdWater";
    public Guid? SystemId { get; set; }

    /// <summary>A heating return; none for a flow, or any other pipe.</summary>
    public bool? Return { get; set; }

    /// <summary>Its insulation, mm, and how it is fire-stopped; none in a file from before.</summary>
    public double? Insulation { get; set; }
    public string? Seal { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A light's photometric data: where it was read from, its lumens and watts, its planes and angles, and its candelas on each plane.</summary>
internal sealed class PhotometryDto
{
    public string Source { get; set; } = string.Empty;
    public double Lumens { get; set; }
    public double Watts { get; set; }
    public List<double> Planes { get; set; } = new();
    public List<double> Angles { get; set; } = new();
    public List<List<double>> Candela { get; set; } = new();
}

internal sealed class MepSystemDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "ColdWater";
    public string Name { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
    public List<Guid> Members { get; set; } = new();

    /// <summary>A circuit's use; none for a system that is not a circuit.</summary>
    public string? Use { get; set; }

    /// <summary>A heating zone's flow and return temperatures where set, °C.</summary>
    public double? FlowTemperature { get; set; }
    public double? ReturnTemperature { get; set; }

    /// <summary>A circuit's breaker, curve, protection, cable, how it is installed, its grouping and length; none for a system that is not a circuit.</summary>
    public double? Breaker { get; set; }
    public string? Curve { get; set; }
    public bool? Rcd { get; set; }
    public bool? ArcFault { get; set; }
    public string? Cable { get; set; }
    public string? Method { get; set; }
    public int? Grouped { get; set; }
    public double? CableLength { get; set; }

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class RoomSeparatorDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public string PhaseCreated { get; set; } = "New";
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class BeamDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double StartX { get; set; }
    public double StartY { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public double StartOffset { get; set; }
    public double EndOffset { get; set; }
    public string Justification { get; set; } = "Top";
    public string Usage { get; set; } = "Beam";
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class RailingDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public List<double>? Path { get; set; }
    public List<double>? Bulges { get; set; }
    public Guid? HostOpeningId { get; set; }
    public bool Closed { get; set; }
    public double BaseOffset { get; set; }
    public string Building { get; set; } = "Dwelling";
    public bool Outside { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

internal sealed class ChimneyDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 450;
    public double Depth { get; set; } = 450;
    public double Angle { get; set; }
    public double BaseOffset { get; set; }
    public int Flues { get; set; } = 1;
    public string? Rule { get; set; }
    public double ExtraHeight { get; set; }
    public string? Fireplace { get; set; }
    public List<ChimneyFireplaceDto> FireplacesAbove { get; set; } = new();
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

/// <summary>A chimney's fireplace on a floor above its foot.</summary>
internal sealed class ChimneyFireplaceDto
{
    public Guid LevelId { get; set; }
    public string? Fireplace { get; set; }
}

internal sealed class ChimneyTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Chimney";
    public string TypeMark { get; set; } = string.Empty;
    public string? Construction { get; set; }
    public double Width { get; set; } = 450;
    public double Depth { get; set; } = 450;
    public double FlueDiameter { get; set; } = 185;
    public int Flues { get; set; } = 1;
    public double MinimumHeight { get; set; }
    public string? Fireplace { get; set; }
    public int TemperatureClass { get; set; } = 600;
    public string? PressureClass { get; set; }
    public bool Wet { get; set; }
    public int CorrosionClass { get; set; } = 3;
    public bool SootFireResistant { get; set; } = true;
    public double ClearanceToCombustibles { get; set; } = 40;
    public string? FireRating { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class DownpipeDto
{
    public Guid Id { get; set; }
    public Guid GutterId { get; set; }
    public bool Distributor { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
}

/// <summary>A fascia or gutter: the roof it runs along, which of its edges, and how far it is moved off them.</summary>
internal sealed class RoofEdgeSweepDto
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "Fascia";
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public Guid RoofId { get; set; }
    public List<Guid> EdgeIds { get; set; } = new();
    public double HorizontalOffset { get; set; }
    public double VerticalOffset { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }

    /// <summary>An eave's: whether it is fire-stopped where party walls meet it, and fireblocked at the wall.</summary>
    public bool? FireStopped { get; set; }
    public bool? FireBlocked { get; set; }
}

internal sealed class RoofWindowTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Roof Window";
    public string TypeMark { get; set; } = string.Empty;
    public double Width { get; set; } = 780;
    public double Height { get; set; } = 980;
    public double FrameWidth { get; set; } = 70;
    public double Upstand { get; set; } = 90;
    public string? Operation { get; set; }
    public Guid FrameMaterialId { get; set; }
    public Guid GlassMaterialId { get; set; }
    public decimal Cost { get; set; }
    public string Description { get; set; } = string.Empty;
}

internal sealed class RoofWindowDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public Guid RoofId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string? Mark { get; set; }
    public string? Comments { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public double? FrameWidth { get; set; }
    public double? Upstand { get; set; }
    public Guid? FrameMaterialId { get; set; }
    public Guid? GlassMaterialId { get; set; }
}

internal sealed class ShaftOpeningDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Shape { get; set; } = "Rectangle";
    public double Width { get; set; } = 600;
    public double Depth { get; set; } = 600;
    public double Angle { get; set; }
    public double BaseOffset { get; set; }
    public Guid? TopLevelId { get; set; }
    public double TopOffset { get; set; }
    public double UnconnectedHeight { get; set; } = 3000;
    public string? Mark { get; set; }
    public string? Comments { get; set; }

    /// <summary>Cut for the services; none in a file from before, or for any other opening.</summary>
    public bool? ForServices { get; set; }
}

internal sealed class PlacedSweepDto
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public Guid LevelId { get; set; }
    public string Kind { get; set; } = "Sweep";
    public List<Guid> HostWallIds { get; set; } = new();
    public string Side { get; set; } = "Exterior";
    public bool Vertical { get; set; }
    public double Elevation { get; set; }
    public double Along { get; set; }
    public double Offset { get; set; }
    public bool Flip { get; set; }
    public bool ReturnAtStart { get; set; }
    public bool ReturnAtEnd { get; set; }
    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
}

/// <summary>A dormer made by the Dormer tool: its shape and size, and the walls that are part of it.</summary>
internal sealed class RoofDormerDto
{
    public string Shape { get; set; } = "Gable";
    public double Width { get; set; }
    public double Height { get; set; }
    public double Slope { get; set; }
    public double Overhang { get; set; }
    public List<Guid> Walls { get; set; } = new();
}

/// <summary>A hole drawn in a roof's sketch: its outline as x, y pairs, and the arc each edge is part of.</summary>
internal sealed class RoofOpeningDto
{
    public List<double> Points { get; set; } = new();
    public List<Guid?>? ArcIds { get; set; }
    public bool Square { get; set; }
}
