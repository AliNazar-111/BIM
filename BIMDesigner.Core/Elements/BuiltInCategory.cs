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
    Profiles,
    Fascias,
    Gutters,
    RoofSoffits,
    RoofWindows,
    ShaftOpenings,
    Downpipes,
    RoofDrains,
    Chimneys,
    Parts,

    /// <summary>Radiators and the like: the heating.</summary>
    MechanicalEquipment,

    /// <summary>Sockets.</summary>
    ElectricalFixtures,

    /// <summary>Light switches.</summary>
    LightingDevices,

    /// <summary>The ground: a toposurface.</summary>
    Topography,

    /// <summary>Ramps: their flights and landings, kerbs and handrails.</summary>
    Ramps,

    /// <summary>Pads graded into the ground: patios, drives, terraces, level ground for the building.</summary>
    Pads,

    /// <summary>The lines round a property.</summary>
    PropertyLines,

    /// <summary>Spot elevations: heights read off the model in plan.</summary>
    SpotElevations,

    /// <summary>Smoke, heat and carbon monoxide alarms.</summary>
    FireAlarmDevices,

    /// <summary>The consumer unit, a distribution board.</summary>
    ElectricalEquipment,

    /// <summary>Parking bays marked out on a drive or a car park.</summary>
    Parking,

    /// <summary>Slab edges: thickened edges, edge beams and kerbs run along floors.</summary>
    SlabEdges,

    /// <summary>Masses: the shape of a building before it is one, its faces made into walls, floors and roofs.</summary>
    Masses,

    /// <summary>Views of the model kept by name: 3D views and cameras, walkthroughs.</summary>
    Views,

    /// <summary>Interior elevation markers: each arrow a view of the wall it points at.</summary>
    Elevations,

    /// <summary>Callouts: a part of a plan or section drawn larger as a view of its own.</summary>
    Callouts,

    /// <summary>View templates: named sets of view settings, applied to views or governing them.</summary>
    ViewTemplates,

    /// <summary>Revision clouds: round what a revision of the drawings changed.</summary>
    RevisionClouds,

    /// <summary>Keynote tags: an element's, a material's or a note's keynote, keyed to a legend.</summary>
    KeynoteTags,

    /// <summary>Eaves: what is built under a roof's overhang - rafter tails, soffits, friezes, mouldings and brackets.</summary>
    Eaves,

    /// <summary>Curtain wall mullions: the types of the bars on a curtain wall's grid lines.</summary>
    CurtainWallMullions,

    /// <summary>Room separation lines: lines that bound rooms where no wall does.</summary>
    RoomSeparationLines,

    /// <summary>Piping systems: cold and hot water, heating, foul water and gas, each a network of fittings.</summary>
    PipingSystems,

    /// <summary>Duct systems: the air drawn out by fans and cooker hoods.</summary>
    DuctSystems,

    /// <summary>Electrical circuits from a consumer unit, and the link between the smoke alarms.</summary>
    ElectricalCircuits
}

/// <summary>Design phase of an element (specification section 7, "Phasing").</summary>
public enum DesignPhase
{
    Existing,
    New,
    Temporary,
    Future
}
