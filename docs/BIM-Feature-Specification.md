# BIM System — Feature Specification & Scope

> Source: `BIM_System_Feature_Specification.pdf`, Version 1.0, September 2026.
> Transcribed into the repository so the build can be checked against it.
> **This document is the authoritative scope for BIMDesigner.** Where it conflicts with
> earlier notes, this wins.

A market-aligned, stack-agnostic breakdown of what a BIM platform should include:
modeling elements, disciplines, documentation, data, interoperability, coordination,
analysis, and delivery phases.

---

## 1. Introduction & Scope

BIM is the process of creating and managing a digital representation of a building's
physical and functional characteristics. Unlike CAD, where a wall is just lines, in BIM a
wall is an **object** that knows its thickness, layers, material, height, fire rating, cost
and its relationships to doors, floors and roofs. Every drawing, schedule and quantity is
derived from one coordinated model.

Benchmarked against Autodesk Revit, Graphisoft ArchiCAD, Trimble Tekla Structures,
Nemetschek Allplan and Vectorworks, Bentley OpenBuildings.

### The BIM dimensions

| Dimension | Meaning | What the system must support |
| --- | --- | --- |
| 3D | Geometry | Parametric 3D model of all building elements |
| 4D | Time | Link elements to a construction schedule; sequence simulation |
| 5D | Cost | Quantity takeoff, cost databases, estimates that update with the model |
| 6D | Sustainability | Energy, daylight, carbon and lifecycle analysis |
| 7D | Facility management | Asset data, maintenance, warranties, operations after handover |

---

## 2. Core Modeling Engine

Everything else depends on this layer. It defines how objects are described, constrained
and located.

### 2.1 Parametric object model
- **Element**: base entity with unique ID, category, type, instance parameters, geometry,
  level, phase, workset, and owner.
- **Category** (walls, doors, ducts…) determines default behaviour, graphics and available
  parameters.
- **Type vs. Instance**: type parameters are shared by all instances of a type (e.g. wall
  assembly layers); instance parameters belong to a single placed element (e.g. this
  wall's height, this door's swing).
- **Parameter data types**: length, area, volume, angle, number, integer, text, yes/no,
  material, currency, URL, image, multiline text; units and rounding per project.
- **Formulas** inside families/types (e.g. `Width = Height * 0.5`) and conditional logic.
- **Materials**: physical (density, thermal conductivity), graphical (surface pattern, cut
  pattern, colour), rendering (texture, reflectivity), identity (manufacturer, cost,
  classification code).

### 2.2 Family / object library system
- **System families**: built into the engine (walls, floors, roofs, ceilings, ducts, pipes,
  stairs, railings).
- **Loadable families**: user-created or vendor content (furniture, doors, windows,
  equipment) stored as files and loaded into projects.
- **In-place components**: one-off geometry modelled directly in the project.
- **Family editor**: reference planes, parametric dimensions, nested families, visibility
  per view type and detail level, type catalogs, MEP connectors, symbolic 2D
  representation for plans.
- **Content library / marketplace**: search, preview, categories, versioning, manufacturer
  objects (BIMobject-style).

### 2.3 Datums and positioning
- **Levels**: horizontal datums; elements hosted on a base level with offsets, may attach
  to a top level.
- **Grids**: linear and arc gridlines with bubbles, for column layout and dimensioning.
- **Reference planes and lines** for constraining geometry.
- **Project base point, survey point, true north vs. project north, shared coordinates**
  for linking models and site data.
- **Units**: metric/imperial, per-discipline unit formats, dual-unit display.

### 2.4 Geometry kernel capabilities
- Solids: extrusion, blend, revolve, sweep, swept blend; void cuts; boolean operations.
- Free-form / conceptual massing with surface subdivision and pattern-based curtain systems.
- Element joins (wall-to-wall mitre/butt, wall-to-floor), attach top/base, cut/join geometry.
- Snapping, alignment, lock constraints, equal-spacing, temporary dimensions, offset,
  mirror, array (linear/radial), copy, rotate, trim/extend, split.
- Selection filters, pick by face/edge, section box, isolate/hide, element pinning.

### 2.5 Relationships and hosting
- Host-based elements (doors and windows require a wall; lights a ceiling; sprinklers a pipe).
- Automatic opening creation in host; host deletion deletes hosted elements.
- Room bounding flag on walls, floors, ceilings, columns.
- Dependency tracking so moving a level or grid updates all dependent elements.

---

## 3. Architectural Elements

Each element is parametric and generates its own plan, section and 3D representation
automatically.

### 3.1 Walls
- **Basic walls** with multi-layer assemblies: structure, substrate, thermal/air layer,
  membrane, finish (interior/exterior). Each layer has material, thickness, function
  priority and wrapping behaviour at ends/inserts.
- **Location line** (wall centreline, core centreline, finish face exterior/interior) and
  flip control.
- **Height**: unconnected height or constrained to a top level with offset; base offset;
  attach to roof/floor/ceiling.
- **Profile editing**: sketch custom elevation profiles, openings, arched tops.
- **Curtain walls**: curtain grids (fixed distance, fixed number, maximum spacing),
  mullions (rectangular, circular, corner, custom profiles), panels (glazed, spandrel,
  solid, empty, doors), joins and embedding into basic walls.
- **Stacked walls**: different wall types vertically stacked in one element.
- **Wall sweeps and reveals** (cornices, skirtings, string courses).
- **Properties**: structural usage (bearing/non-bearing/shear), fire rating, acoustic
  rating (STC/Rw), thermal resistance (U/R value), function (exterior, interior,
  retaining, foundation, soffit, core-shaft).
- **Compound wall editing**: modify vertical structure, merge regions, split regions,
  assign layers to regions.

### 3.2 Floors and slabs
- Sketch-based footprint, layered assemblies, thickness, variable thickness with shape
  editing (points/edges for drainage slopes).
- Slab edges, openings (shafts that cut multiple levels), floor-to-wall joins.
- Structural vs. architectural floors; span direction; level offset.

### 3.3 Roofs
- Roof by footprint (slopes per edge, overhangs), by extrusion, by face on massing.
- Gable, hip, mansard, flat, curved; ridge/valley auto-generation.
- Gutters, fascias, soffits, roof openings, dormers, sloped glazing (curtain roof).

### 3.4 Ceilings, columns, beams
- Ceilings: automatic (room-boundary detection) or sketch; grid patterns for tiles; hosts
  for lights/diffusers.
- Architectural columns (decorative, join with walls) distinct from structural columns.

### 3.5 Doors and windows
- Hosted in walls; automatic opening; flip hand/facing; swing direction; sill/head height;
  frame, panel, glazing and hardware sub-components.
- Parameters: width, height, thickness, material, fire rating, acoustic rating, U-value,
  hardware set, mark/number, finish, manufacturer, cost.
- Types: single/double leaf, sliding, folding, revolving, overhead, curtain wall doors;
  casement, awning, sliding, fixed, bay windows, skylights.
- Plan representation (swing arc), elevation representation, schedule-ready data.

### 3.6 Vertical circulation
- **Stairs**: by component (runs, landings, supports/stringers) or by sketch; riser/tread
  rules and code checks (max riser, min tread, headroom); straight, L, U, spiral, winder;
  multistorey stairs.
- **Ramps**: slope and max length rules, landings.
- **Railings**: rail structure (top rail, handrails, intermediate rails), baluster/post
  patterns, hosted on stairs/ramps/floors or free-standing; extensions and terminations.
- **Elevators, escalators, moving walkways** as equipment with shaft openings.

### 3.7 Rooms, spaces, areas and zones
- Rooms auto-detect bounding elements; room separation lines for open plans.
- Room data: name, number, department, occupancy, occupant count, area, perimeter,
  volume, ceiling/floor/wall finishes, base finish, comments.
- Area plans with area schemes (gross, rentable, BOMA, usable).
- MEP Spaces and Zones (heating/cooling analysis, occupancy schedules).
- Colour-fill legends by department, occupancy or any parameter.

### 3.8 Site and landscape
- Toposurface / topo solids from points, contour import, or point cloud; sub-regions.
- Building pads, graded regions with cut/fill volumes, retaining walls.
- Property lines (bearings and distances), setbacks, site components.
- Geolocation, sun path, north arrow.

### 3.9 Furniture, fixtures and equipment (FF&E)
- Furniture, casework, specialty equipment, plumbing fixtures, appliances, signage;
  room-hosted or level-hosted objects with manufacturer data for schedules.

### 3.10 Conceptual massing
- Mass families for early design; mass floors for gross area/volume per level; convert mass
  faces into walls, floors, roofs and curtain systems as design develops.

---

## 4. Structural Discipline

### 4.1 Physical elements
- Structural columns (steel, concrete, timber; slanted), beams, beam systems, braces,
  trusses, girders.
- Structural walls (shear/bearing), structural floors/decks (metal deck, composite),
  foundations: isolated footings, strip/wall footings, rafts/mats, pile caps, piles.
- Openings, slab edges, notches; cantilevers; eccentricity and offsets.
- Steel: end-plate/base-plate connections, bolts, welds, anchors, stiffeners, plates.
- Concrete: rebar (shape-driven, free-form), rebar sets, fabric sheets, couplers; cover
  settings; bar bending schedules.
- Precast: element splitting, lifting anchors, shop drawings.
- Timber/CLT elements and connectors.

### 4.2 Analytical model
- Analytical nodes, members and panels derived from the physical model, manually adjustable.
- Boundary conditions (pinned, fixed, roller, user-defined), releases, load cases and
  combinations (dead, live, wind, seismic), load types (point, line, area).
- Export/round-trip to ETABS, SAP2000, Robot, RISA, SCIA and result import.

### 4.3 Structural documentation
- Framing plans, foundation plans, column/beam/rebar schedules, section marks, connection
  details, steel fabrication data (CIS/2, IFC, NC/DSTV).

---

## 5. MEP Discipline

### 5.1 Mechanical (HVAC)
- Ducts (rectangular, round, oval), fittings, flex ducts, accessories, air terminals,
  mechanical equipment (AHU, VAV, fans, chillers, boilers).
- Systems: supply, return, exhaust; system browser; flow propagation; duct sizing
  (velocity, friction, equal friction, static regain); pressure-loss reports.
- Insulation and lining; automatic routing suggestions; slope for condensate.

### 5.2 Plumbing and piping
- Pipes, fittings, valves, strainers, pumps, tanks, water heaters; plumbing fixtures with
  fixture units.
- Systems: domestic cold/hot water, sanitary, vent, storm, hydronic supply/return, fire
  protection, gas, medical gas.
- Slope-based routing, invert levels, pipe sizing, fixture unit calculations.

### 5.3 Electrical
- Panels/distribution boards, transformers, switchgear, generators, UPS; circuits with
  load, voltage, phases, breaker ratings.
- Lighting fixtures with photometric (IES) data, switches, receptacles, junction boxes,
  data/telecom, fire alarm and security devices.
- Conduits, cable trays, busways with fittings and fill calculations.
- Panel/circuit schedules, load calculations, demand factors, voltage-drop checks, lux
  analysis.

### 5.4 Common MEP capabilities
- Connectors on families (duct, pipe, electrical, cable tray, conduit) with flow direction
  and system classification.
- Spaces and zones for heating/cooling loads; energy settings; gbXML export.
- Fabrication-level detailing (spools, hangers, supports, sleeves).
- System inspection, disconnect warnings, interference checks with structure.

---

## 6. Views, Annotation & Documentation

### 6.1 View types

| View | Purpose / features |
| --- | --- |
| Floor plan / ceiling plan | Cut plane, view range (top, cut, bottom, view depth), underlay of other levels, plan regions |
| Section | Building, wall, detail sections; segmented/jogged; markers auto-placed |
| Elevation | Exterior, interior (room-based), framing elevations; depth cueing |
| 3D / axonometric | Orbit, walk, section box, perspective cameras, saved orientations |
| Callout & detail view | Enlarged views; drafting views for pure 2D details; detail library |
| Schedule / quantity | Tabular views generated from element data |
| Legend | Symbol keys, material legends, colour-fill legends |
| Sheet | Paper-space layout hosting views with title block |
| Area / colour plans | Area schemes, department colour fills |

### 6.2 View control
- Scale, **detail level** (coarse/medium/fine), visual style (wireframe, hidden line,
  shaded, realistic, consistent colours), discipline, phase filter.
- Visibility/Graphics overrides by category, filter (rule-based, e.g. walls with fire
  rating = 2 hr in red), workset, phase, link.
- View templates that lock and propagate settings; view naming standards; browser
  organisation.
- Crop regions, scope boxes, annotation crop, dependent views for matchlines.
- Line styles, line weights per scale, line patterns, fill patterns (drafting vs. model),
  halftone, transparency.

### 6.3 Annotation
- Dimensions: aligned, linear, angular, radial, diameter, arc length, spot elevation, spot
  slope, spot coordinate; dimension styles; equality constraints; witness line control.
- Tags by category with leaders, auto-numbering, tag-all-not-tagged.
- Text, text styles, leaders; keynotes tied to a keynote database; symbols; revision clouds
  and tags; grid/level bubble control.
- Detail components, repeating details, insulation lines, filled/masking regions, detail
  lines, break lines.
- Matchlines, view references, section/elevation/callout markers, north arrows, scales.

### 6.4 Schedules and quantity takeoff
- Schedule any category: fields, filters, sorting/grouping with headers and footers,
  formatting, totals, calculated fields, conditional formatting, images.
- Material takeoffs (area/volume per material), multi-category schedules, key schedules,
  note blocks, sheet lists, view lists, revision schedules.
- **Bidirectional editing**: changing a schedule cell updates the model.
- Export to CSV/XLSX; embed on sheets with split across sheets.

### 6.5 Sheets and publishing
- Title block families with project parameters.
- Sheet numbering conventions, guide grids, viewport titles, sheet sets/subsets.
- Revisions: cloud-based or per-sheet numbering, revision schedules, issue tracking.
- Batch print/export to PDF, DWG, DWF/DWFx, IFC; print setups; transmittal packages.

---

## 7. Data & Information Management

- **Parameter framework**: built-in, project, shared (GUID-based for cross-project
  consistency), family, global parameters (drive multiple elements).
- **Classification**: Uniclass 2015, OmniClass, MasterFormat, UniFormat, NRM, CoClass, IFC
  entity/type mapping; multiple systems per element.
- **Phasing**: existing, demolished, new, temporary, future; phase filters and graphic
  overrides per view.
- **Design options**: option sets with alternatives; elements assigned to options.
- **Groups and assemblies**: model groups, detail groups, attached detail groups;
  assemblies with their own views and schedules (prefab).
- **Model linking**: link architectural, structural, MEP, site and consultant models;
  copy/monitor of levels, grids, columns, walls, openings; coordination review.
- **Worksharing**: central model with local copies, worksets, element borrowing,
  synchronise, relinquish, editing requests.
- **Versioning and audit**: model history, element change log, who-changed-what, restore
  previous versions, compare versions.
- **Data validation**: required parameters, naming rules, allowed values, model health
  checks (warnings count, unplaced rooms, duplicate marks).
- **Project information**: client, address, building type, energy settings, coordinates.
- **Search and query**: find elements by parameter, select all instances, property filters,
  ID lookup.

---

## 8. Interoperability & File Formats

openBIM support (buildingSMART standards) is expected.

| Format / Standard | Direction | Purpose |
| --- | --- | --- |
| IFC 2x3, IFC4, IFC4.3 | Import / Export | Open BIM exchange; MVDs; property set mapping; classification export |
| BCF 2.1 / 3.0 | Import / Export | Issue exchange: viewpoints, comments, screenshots, element refs |
| COBie | Export | Handover spreadsheets for FM (spaces, assets, systems) |
| gbXML | Export | Energy and thermal analysis tools |
| DWG / DXF / DGN | Import / Export / Link | 2D CAD underlays and deliverables; layer mapping |
| PDF, images | Import / Link / Export | Underlays, scanned drawings, publishing |
| SKP, OBJ, FBX, 3DS, glTF, USD | Import / Export | Visualisation and conceptual models |
| Point clouds (RCP, RCS, E57, LAS) | Link | Scan-to-BIM, existing conditions |
| CIS/2, NC/DSTV, SDNF | Export | Steel fabrication |
| CSV, XLSX, ODBC/SQL | Import / Export | Schedules, bulk parameter editing, database sync |
| Open API / SDK / scripting | — | Plugins, automation, visual programming (Dynamo/Grasshopper-style) |

- Export setup mapping: category to IFC class, layer mapping for DWG, property set
  definitions, unit conversion.
- Model checking of imported IFC and an IFC viewer inside the platform.

---

## 9. Coordination, Analysis & nD BIM

### 9.1 Clash detection and model coordination
- Hard clashes (geometry intersects), soft/clearance clashes, workflow/4D clashes.
- Clash tests between disciplines or selection sets; tolerance; grouping; status (new,
  active, reviewed, approved, resolved); assignment; reports (HTML/XLSX/BCF).
- Federated model viewer; saved viewpoints; markup and redlining.
- Issue tracker with due dates, priorities, comments, linked elements.

### 9.2 Model checking and code compliance
- Rule-based checking (Solibri-style): egress distances, door clearances, accessibility,
  stair geometry, space requirements, naming and data completeness; per-element results.

### 9.3 Quantity takeoff and cost (5D)
- Automatic quantities by element/material/assembly; mapping to cost codes and unit rates;
  cost database; estimate reports that update with the model; earned-value comparison.

### 9.4 Scheduling (4D)
- Link elements/selection sets to tasks from MS Project, Primavera P6, Asta; task types
  (construct, demolish, temporary); simulation playback; look-ahead views; progress
  tracking against planned.

### 9.5 Building performance (6D)
- Energy model from spaces/zones: loads, HVAC systems, glazing, orientation; annual
  simulation.
- Daylighting and solar studies, lighting levels, thermal comfort, embodied and operational
  carbon (LCA), acoustic estimates, CFD hand-off.

### 9.6 Structural analysis integration
- Round-trip of analytical model and results; member design checks against codes
  (Eurocode, ACI/AISC, IS, BS).

---

## 10. Visualization

- Material appearance library (textures, bump, reflectivity), decals, environment/HDRI.
- Lighting: sun settings by location/date/time, artificial lights with photometrics,
  exposure control.
- Rendering: local and cloud, quality presets, panoramas, stereo panoramas.
- Real-time viewer, walkthrough paths and animation, camera keyframes, exploded views,
  section box in 3D.
- VR/AR export (glTF/USDZ), links to Enscape/Twinmotion/Lumion-class engines.
- Presentation graphics: sketchy lines, silhouette edges, shadows, ambient occlusion.

---

## 11. Facility Management & Handover (7D)

- Asset register generated from model elements: equipment type, serial number,
  manufacturer, model, install date, warranty start/end, expected life, replacement cost.
- Maintenance schedules and work orders linked to assets; spare parts; service history.
- O&M manuals, datasheets, certificates attached to elements.
- Space management: occupancy, moves, departments, lease areas.
- COBie export at each stage; as-built model acceptance workflow; LOD 500 verification.
- Digital twin hooks: sensor/IoT data mapped to spaces and equipment; dashboards.

---

## 12. Platform, Collaboration & Standards

### 12.1 Common Data Environment (CDE)
- Cloud project hub: model hosting, ISO 19650 folder structure (WIP, Shared, Published,
  Archive), status codes and suitability, transmittals.
- Roles and permissions (view, markup, edit, publish, admin) per project/folder/model;
  audit logs; SSO.
- Web and mobile viewer: 2D sheets and 3D models, measure, section, properties, offline.
- Design review: markups, comments, issues, approvals, version comparison, RFIs, submittals.
- Notifications, activity feed, integrations with PM and document-control systems.

### 12.2 Standards and templates
- Project templates: units, view templates, line styles, fill patterns, families, sheets,
  schedules, browser organisation.
- Naming conventions (ISO 19650-2, BS 1192 style) for files, views, sheets, families,
  worksets.
- BIM Execution Plan (BEP) fields: LOD per stage, responsibilities, deliverables, EIR.
- Company content library governance: approval, versioning, deprecation.

### 12.3 System capabilities
- Performance for large models (millions of elements): level-of-detail streaming, graphics
  culling, background loading, model compression.
- Undo/redo stack, auto-save, crash recovery, model audit/purge, file size control.
- Localisation, accessibility, keyboard shortcuts, customisable UI.
- Licensing/user management, usage analytics, in-product help.
- Security: encryption at rest/in transit, data residency, backup and retention.

---

## 13. Element Parameter Reference

### 13.1 Wall

| Instance parameters | Type parameters |
| --- | --- |
| Base constraint (level), base offset, top constraint, top offset, unconnected height, location line, room bounding, structural usage, phase created/demolished, mark, comments, length, area, volume, workset, design option | Wall type name, structure (layers: function, material, thickness, wraps), wrapping at inserts/ends, total width, function (exterior/interior/etc.), coarse-scale fill pattern/colour, fire rating, acoustic rating, thermal resistance (R), heat transfer coefficient (U), assembly code, keynote, cost, manufacturer, URL, type mark |

### 13.2 Door

| Instance parameters | Type parameters |
| --- | --- |
| Level, sill height, head height, host wall, hand/facing flip, swing angle, mark (door number), from room / to room, frame finish, hardware group, phase, comments | Width, height, thickness, panel material, frame material, frame type, glazing, fire rating, acoustic rating, U-value, door operation (swing, sliding…), leaf count, manufacturer, model, cost, type mark, assembly code, keynote |

### 13.3 Window

| Instance parameters | Type parameters |
| --- | --- |
| Level, sill height, head height, host wall, orientation flip, mark, phase, comments | Width, height, rough width/height, frame material, glazing type, glass thickness, U-value, SHGC, visible transmittance, operation type, fire rating, manufacturer, cost, type mark |

### 13.4 Room / Space

| Parameter | Description |
| --- | --- |
| Name, number, department, occupancy, occupant count | Identity data used in plans, schedules and colour fills |
| Level, base offset, limit offset, upper limit | Vertical extent used to compute volume |
| Area, perimeter, volume, unbounded height | Computed from bounding elements |
| Floor finish, wall finish, ceiling finish, base finish | Finish schedule data |
| Space type, zone, condition type, design heating/cooling loads, airflow | MEP analysis data (Spaces) |

---

## 14. Level of Development (LOD)

Per BIMForum / AIA definitions. The system should let projects declare and check LOD per stage.

| LOD | Description | Example: wall |
| --- | --- | --- |
| 100 | Conceptual; symbolic or massing representation | Mass block or generic thickness line |
| 200 | Approximate geometry: size, shape, location, orientation | Generic wall of approximate thickness |
| 300 | Precise geometry with specific type, quantity and dimensions | Specific wall type with layers and exact height |
| 350 | LOD 300 plus interfaces with other systems | Wall with connections to slab, openings, embedded items |
| 400 | Fabrication/assembly detail | Stud layout, fixings, shop-drawing level |
| 500 | Field-verified as-built | Surveyed and confirmed geometry with asset data |

---

## 15. Phased Roadmap & MVP

| Phase | Scope | Outcome |
| --- | --- | --- |
| **1 — Core & Architecture MVP** | Parametric element model, levels/grids, walls (layered), floors, roofs, ceilings, doors, windows, rooms, basic families, plan/section/elevation/3D views, dimensions/tags/text, schedules, sheets, PDF/DWG export, IFC export, undo/redo, save/load | A usable architectural modeller that produces drawing sets |
| **2 — Documentation depth & data** | Stairs/railings, curtain walls, site, view templates, filters, keynotes, revisions, design options, phasing, classification systems, shared parameters, material library, rendering basics, IFC import, DWG link | Production-ready documentation and open exchange |
| **3 — Collaboration** | Worksharing, model linking, copy/monitor, cloud CDE, web/mobile viewer, issues/BCF, version compare, permissions | Multi-user, multi-discipline projects |
| **4 — Structure & MEP** | Structural elements, rebar, analytical model; ducts, pipes, electrical, systems, sizing, connectors, spaces/zones, gbXML | Full multi-discipline authoring |
| **5 — Coordination & nD** | Clash detection, rule checking, quantity takeoff/5D, 4D scheduling, energy/daylight analysis, API/plugins, visual scripting | Coordination and analysis platform |
| **6 — Handover & FM** | COBie, asset register, O&M documents, maintenance, digital twin/IoT, dashboards | Lifecycle/7D coverage |

---

## 16. Glossary

| Term | Meaning |
| --- | --- |
| BIM | Building Information Modeling: object-based digital model with geometry and data |
| Family / Object / Type | Reusable parametric definition of an element; a type is one configuration of a family |
| Host | Element that carries another (wall hosts a door) |
| Datum | Reference element: level, grid, reference plane |
| IFC | Industry Foundation Classes; open, vendor-neutral BIM data format from buildingSMART |
| BCF | BIM Collaboration Format for exchanging issues between tools |
| COBie | Construction Operations Building information exchange; structured handover data for FM |
| CDE | Common Data Environment; the single source of information for a project |
| ISO 19650 | International standard for information management using BIM |
| LOD | Level of Development (geometric and information reliability of an element) |
| Federated model | Combined model made of separate discipline models linked together |
| Clash detection | Automated search for conflicting elements between models |
| Workset / Worksharing | Mechanism that lets several users edit one central model concurrently |
| 4D / 5D / 6D / 7D | Model linked to time / cost / sustainability / facility management |
| MEP | Mechanical, Electrical and Plumbing disciplines |
| FF&E | Furniture, Fixtures and Equipment |
