namespace BIMDesigner.Core.Elements;

/// <summary>
/// Element categories (specification section 2.1). A category decides default behaviour,
/// graphics and which parameters an element offers. Only the categories the application
/// can currently place are listed; the rest arrive with their phase.
/// </summary>
public enum BuiltInCategory
{
    Walls,
    Floors,
    Roofs,
    Ceilings,
    Doors,
    Windows,
    Columns,
    StructuralColumns,
    StructuralFraming,
    StructuralFoundations,
    Stairs,
    Railings,
    Rooms,
    Grids,
    Dimensions,
    Tags,
    TextNotes,
    Sections,
    Sheets,
    Furniture,
    Casework,
    PlumbingFixtures,
    SpecialtyEquipment,
    Planting,
    Entourage,
    GenericModels,
    Ducts,
    Pipes,
    LightingFixtures,
    WallSweeps,
    WallReveals,
    WallOpenings,
    CurtainPanels,
    Profiles
}

/// <summary>Design phase of an element (specification section 7, "Phasing").</summary>
public enum DesignPhase
{
    Existing,
    New,
    Temporary,
    Future
}
