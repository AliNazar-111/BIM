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

    public List<DoorTypeDto> DoorTypes { get; set; } = new();

    public List<WindowTypeDto> WindowTypes { get; set; } = new();

    public List<WallDto> Walls { get; set; } = new();

    public List<DoorDto> Doors { get; set; } = new();

    public List<WindowDto> Windows { get; set; } = new();

    public List<RoomDto> Rooms { get; set; } = new();

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

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
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

internal sealed class CurtainPanelDto
{
    public int Column { get; set; }
    public int Row { get; set; }
    public string Kind { get; set; } = "Glazed";
}

internal sealed class StackTierDto
{
    public Guid WallTypeId { get; set; }
    public double Height { get; set; }
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
}

internal sealed class WindowTypeDto : OpeningTypeDto
{
    public string Operation { get; set; } = "Casement";
    public string GlazingType { get; set; } = string.Empty;
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

    public string Mark { get; set; } = string.Empty;
    public string Comments { get; set; } = string.Empty;
    public string Workset { get; set; } = "Workset1";
    public string PhaseCreated { get; set; } = "New";
    public string? PhaseDemolished { get; set; }
}

internal sealed class DoorDto : OpeningDto
{
    public double SwingAngle { get; set; } = 90;
}

internal sealed class WindowDto : OpeningDto
{
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

    /// <summary>An edited elevation outline as x, y pairs (along, height above base), and the wall length it was edited at.</summary>
    public List<double>? Profile { get; set; }
    public double ProfileLength { get; set; }

    /// <summary>A curtain wall's own grid lines, when it does not follow its type's.</summary>
    public List<double>? CurtainVerticals { get; set; }
    public List<double>? CurtainHorizontals { get; set; }
    public List<CurtainPanelDto>? CurtainPanels { get; set; }

    /// <summary>The slabs the top and base are attached to, if any.</summary>
    public Guid? TopAttachedTo { get; set; }
    public Guid? BaseAttachedTo { get; set; }

    /// <summary>"Vertical", "Slanted" or "Tapered", and the angles for them, in degrees.</summary>
    public string CrossSection { get; set; } = "Vertical";
    public double SlantAngle { get; set; }
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
