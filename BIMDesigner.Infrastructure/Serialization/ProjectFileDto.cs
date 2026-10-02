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
    public List<GutterTypeDto> GutterTypes { get; set; } = new();
    public List<SoffitTypeDto> SoffitTypes { get; set; } = new();
    public List<RoofEdgeSweepDto> RoofEdgeSweeps { get; set; } = new();
    public List<RoofWindowTypeDto> RoofWindowTypes { get; set; } = new();
    public List<ChimneyTypeDto> ChimneyTypes { get; set; } = new();

    /// <summary>The elements pinned where they are.</summary>
    public List<Guid> PinnedIds { get; set; } = new();
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

    /// <summary>Architectural columns and their types. Missing from older files.</summary>
    public List<ColumnTypeDto> ColumnTypes { get; set; } = new();
    public List<ColumnDto> Columns { get; set; } = new();

    public List<SlabTypeDto> SlabTypes { get; set; } = new();

    public List<SlabDto> Slabs { get; set; } = new();

    public List<GridDto> Grids { get; set; } = new();

    public List<DimensionDto> Dimensions { get; set; } = new();

    public List<TagDto> Tags { get; set; } = new();

    public List<TextNoteDto> TextNotes { get; set; } = new();

    public List<SectionDto> Sections { get; set; } = new();

    public List<SheetDto> Sheets { get; set; } = new();

    /// <summary>Views that leave something out. Missing from older files, where every view showed everything.</summary>
    public List<ViewSettingsDto> ViewSettings { get; set; } = new();
}

/// <summary>What one view hides. The view is named the same way a viewport names it.</summary>
internal sealed class ViewSettingsDto
{
    /// <summary>"FloorPlan", "Section" or "Model3D".</summary>
    public string Kind { get; set; } = "FloorPlan";

    /// <summary>The level or section marker. Empty for the 3D view.</summary>
    public Guid TargetId { get; set; }

    public List<string> HiddenWallFunctions { get; set; } = new();

    /// <summary>The view's scale as the denominator of 1:n, when it has its own.</summary>
    public double? Scale { get; set; }

    /// <summary>The view's Wall Join Display, when not the default of cleaning every join.</summary>
    public string? JoinDisplay { get; set; }
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

    /// <summary>Millimetres from the sheet's bottom-left corner.</summary>
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>The scale denominator: 100 means 1:100.</summary>
    public double Scale { get; set; } = 100;

    public bool ShowTitle { get; set; } = true;
    public string TitleOverride { get; set; } = string.Empty;
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

internal sealed class DimensionDto
{
    public Guid Id { get; set; }
    public Guid LevelId { get; set; }

    public DimensionEndDto Start { get; set; } = new();
    public DimensionEndDto End { get; set; } = new();

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

    public double RoofFasciaDepth { get; set; } = 150;
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

    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
}

internal sealed class ProjectInformationDto
{
    public string Name { get; set; } = "Untitled Project";
    public string Number { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string BuildingType { get; set; } = string.Empty;
}

internal sealed class LevelDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Elevation { get; set; }
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
}

internal sealed class CurtainSegmentDto
{
    public bool Vertical { get; set; }
    public double Line { get; set; }
    public double From { get; set; }
    public double To { get; set; }
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
}

internal sealed class WindowTypeDto : OpeningTypeDto
{
    public string Operation { get; set; } = "Casement";
    public string GlazingType { get; set; } = string.Empty;

    /// <summary>Panes up and across each light; absent in files from before divided lights.</summary>
    public int GlazingRows { get; set; }
    public int GlazingColumns { get; set; }
    public double SolarHeatGainCoefficient { get; set; }
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
    public string? Mark { get; set; }
    public string? Comments { get; set; }
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
}
