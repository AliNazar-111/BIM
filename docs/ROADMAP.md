# BIMDesigner Roadmap

Tracks [BIM-Feature-Specification.md](BIM-Feature-Specification.md) against what is built.
Spec section references are in brackets.

Status: ✅ done · 🔨 in progress · ⬜ not started

*Brought up to date on 22 October 2026. What comes next is set out in rounds at the end of
the phases; the building services in their own rounds, M1 to M8, after them.*

---

## Phase 1 — Core & Architecture MVP

Outcome per spec §15: *a usable architectural modeller that produces drawing sets.*

Phase 1 is large, so it is split into steps. Each step ends with something runnable to
test before the next begins.

| Step | Scope | Spec | Status |
| --- | --- | --- | --- |
| **1.1** | **Parametric foundation** — Element/ElementType/Category, type-vs-instance parameters, parameter data types, Levels, Materials, layered wall assemblies, location line, BimDocument | §2.1 §2.3 §3.1 §13.1 | ✅ |
| **1.2** | **Save / open / new project** (`.bimx` JSON format), undo–redo stack, unsaved-changes prompts | §12.3 | ✅ |
| **1.3** | **Wall editing** — wall-to-wall mitre joins, move, endpoint grips, endpoint snapping, split, trim/extend | §2.4 §3.1 | ✅ |
| **1.3b** | **Editing** — multi-select (click, Ctrl+click, band), move any selection, copy/paste and paste in place across levels, offset, mirror, linear array | §2.4 | ✅ |
| 1.3c | Radial array, rotate, scale, align, and lock constraints - **Rotate**, **Scale** and **Pin** done; radial array, **Align** and locked dimensions to come | §2.4 | 🔨 |
| **1.3d** | **Wall completion (W1)** — editable top constraint and top offset, location line changes that keep the wall in place, flip control and Space to flip, offset while drawing, wall functions hidden per view | §3.1 §6.2 | ✅ |
| **1.3e** | **Wall joins and wrapping (W2)** — shared junctions for three or more walls, heaviest run carries through a cross, per-end join control (mitre, butt, run through, square off, disallow), clean outlines with no lines across joins, layers wrapping at exposed ends and openings | §2.4 §3.1 | ✅ |
| **1.3f** | **Wall type editor (W3)** — duplicate, rename and delete wall types; edit layers (function, material, thickness, wrapping) with a live preview and core boundaries; applied as one undoable step; change the type of a whole selection | §3.1 §13.1 | ✅ |
| **1.3g** | **Curved walls (W4, arcs)** — arc walls drawn start–end–point or bent from any wall with its middle grip; joins, layers, wrapping, doors and windows, rooms, sections, 3D and IFC all follow the curve; split, offset, mirror and location line work on arcs | §3.1 | ✅ |
| **1.3i** | **Wall shapes** — rectangle and square, regular polygons (inscribed or circumscribed, 3–32 sides), circle, rounded oval; each placed with two clicks as joined walls facing out, with offset and flip | §3.1 | ✅ |
| **1.3h** | **Curve leftovers** — curved walls crossing straight or curved walls midway, trim/extend to a curve, walls placed on picked lines | §3.1 | ✅ |
| **1.3j** | **Wall heights** — depth walls going down from their level, tops and bases attached to floors and roofs so they follow the slab, detach | §3.1 | ✅ |
| **1.3k** | **Stacked walls** — a wall type built from tiers of other wall types, one tier taking up the height, plan cut through the tier at cut height; 3D, sections, quantities and IFC per tier | §3.1 | ✅ |
| **1.3l** | **Slanted and tapered walls** — a wall leaning from vertical, or tapering on either face by type or per wall; plan cut at cut height, sections and 3D follow | §3.1 | ✅ |
| **1.3m** | **Sweeps and reveals** — skirtings, cornices, beads and plain mouldings run along a face, broken by doors and windows; grooves set back into the face; edited with the wall type; section, plan, 3D and IFC | §3.1 | ✅ |
| **1.3n** | **Elliptical walls** — whole ellipses placed in a box and half ellipses through three clicks; true elliptical curves in plan, joins, rooms, sections, 3D and IFC; split, mirror, offset and location line keep the shape | §3.1 | ✅ |
| **1.3o** | **Edit profile** — a straight wall's elevation outline edited by dragging corners or typing them, with rectangle, gable and stepped starts; doors and windows cut through it; plan cut at cut height, sections, 3D, quantities, sweeps, split and IFC follow the outline | §3.1 | ✅ |
| **1.3p** | **Curtain walls** — types that set out vertical and horizontal grids by fixed distance, fixed number or maximum spacing; glazed, solid, empty and door panels; rectangular or round mullions with optional borders; per-wall grid and panel editing; straight or curved; plan, section, 3D, quantities, split and IFC (`IfcCurtainWall` of plates and members) | §3.1 | ✅ |
| **1.3q** | **Embedded walls** — a curtain wall drawn inside another wall cuts its own opening there, its length and height, following it when moved; host plan, 3D, sections, sweeps and IFC | §3.1 | ✅ |
| **1.3r** | **Placed sweeps and contextual tabs** — sweep and reveal types with offset, flip, setback, cutting and returns; profiles drawn by hand; sweeps and reveals placed horizontally or vertically across several walls, with Add/Remove Walls and Modify Returns; green "Modify \| …" tabs for the selection | §3.1 | ✅ |
| **1.3s** | **Spline, freeform and double-slanted walls** — walls along a smooth curve clicked point by point or drawn freehand, open or closed into a loop, reshaped by their points; walls bent partway up, leaning one way below a break and another above it; plan, joins, section, 3D, split, mirror, offset and saving follow | §3.1 | ✅ |
| **1.4** | **Hosting** — doors and windows in walls, automatic openings, host delete cascades, plan symbols | §2.5 §3.5 §13.2 §13.3 | ✅ |
| **1.5** | **Rooms** — auto-detect bounding elements, area/perimeter/volume traced from the walls, room tags, unenclosed detection, headroom under roofs | §3.7 §13.4 | ✅ |
| 1.5b | Room separation lines for open plans, colour-fill legends by department or occupancy - **room separators** done (Round 12); colour fills to come | §3.7 | 🔨 |
| **1.6** | **Floors, ceilings and flat roofs** — layered build-ups sharing the wall layer system, outline picked from an enclosed space, quantities and persistence | §3.2 §3.3 §3.4 | ✅ |
| 1.6b | Free-sketched slab outlines, slab openings and shafts, slab edges, span direction - **shafts** cutting floors, ceilings and roofs, holes of any shape in roofs, floors dividing the walls through them and stopping at the walls they meet, floors and ceilings sketched with holes of their own (Edit Boundary), span direction, and slab edges (thickened edges, edge beams, kerbs) run along a floor's edges done | §3.2 | ✅ |
| **1.6c** | **Pitched roofs** — slope per edge, pitch and eave height per edge, cutoff, hip/gable/shed/dutch/cross-gable shapes, ridge/hip/valley generation, plan/section/3D, takeoff by sloping area, `IfcRoof`. Built in steps R1–R10 of [ROOFS.md](ROOFS.md): done so far are sketch mode with Pick Walls and overhangs, arcs and cones, walls attaching to the slope, eave cuts, slope arrows, roof by extrusion (gambrel, barrel vault), Join Roof, openings and dormer openings, the one-click Dormer tool, fascias, gutters, soffits and downpipes, roof windows, chimneys, shafts, and flat roofs falling to their drains; still to come are By Face openings, joining an extruded roof to another, flat-roof shape editing, and the last sketch tools listed in ROOFS.md (tangent arcs, locked lines) | §3.3 | 🔨 |
| **1.7** | **Levels and grids** — plan shows one storey, level switching, gridlines with bubbles, snapping to grid crossings | §2.3 | ✅ |
| **1.7b** | **Levels** — add, rename, move and delete storeys (with cascade and one-step undo), underlay of the storey below | §2.3 §6.1 | ✅ |
| **1.8** | **Schedules and quantity takeoff** — tabular views, fields, filters, sorting, grouping with subtotals, bidirectional editing, material takeoff, CSV export | §6.4 | ✅ |
| 1.8b | User-defined schedules, calculated fields, conditional formatting, key schedules - all but key schedules done (Round 4) | §6.4 | 🔨 |
| **1.9** | **Annotation** — associative dimensions, tags that read a parameter, text notes with leaders | §6.3 | ✅ |
| **1.9b** | **Dimension styles, angular/radial dimensions, multi-segment strings, keynotes and a keynote database, revision clouds** (Round 4) | §6.3 | ✅ |
| **1.10a** | **Sections** — section markers on plan, live cut through walls, slabs and openings, levels as datums, what lies beyond the cut shown in elevation, selection shared with the plan | §6.1 §6.2 | ✅ |
| **1.10b** | **3D viewport** — walls as their real layers with openings cut through, slabs at their elevations, glazing and door leaves, orbit/pan/zoom, shaded / flat / X-ray styles, per-storey visibility, click to select | §6.1 §6.2 | ✅ |
| **1.10d** | **3D views** — named 3D views and cameras keeping their camera, style, section box and what they show; section box with handles, the cut solids closed; shaded, flat, X-ray, wireframe and hidden-line styles; perspective or orthographic; walking through the model at eye height; walkthroughs played frame by frame and exported as video; 3D views on sheets as hidden-line or painted vector drawings | §6.1 §6.2 | ✅ |
| **1.10c** | **Elevations, callouts, templates** — the four exterior elevations and interior elevations cropped to their rooms, both on sheets; callouts of plans and sections cropped to their boxes at their own scale and detail, their tags quoting detail and sheet numbers; detail level kept by each view; view templates applied once or governing their views | §6.1 §6.2 | ✅ |
| **1.11a** | **Sheets** — paper sizes, title block read from the project, viewports at true drawing scales, drag to lay out, one renderer shared with the editors | §6.5 | ✅ |
| **1.11b** | **Publishing** — vector PDF export of a sheet or a whole set, printing at true paper size | §6.5 | ✅ |
| **1.11c** | **Sheet lists, revision schedules and clouds** — a sheet list schedule; revisions numbered P01/C01, 1, 2 or A, B, issued and locked; clouds with their tags; the revision schedule and current revision in each title block | §6.5 §7 | ✅ |
| **1.12a** | **IFC4 export** — project/site/building/storeys, walls, slabs, ceilings, doors, windows with real openings, spaces, material layer sets, standard and custom property sets, stable identifiers | §8 | ✅ |
| **1.12b** | **IFC import, DXF export, IFC2x3, COBie** — IFC2x3 and IFC4 read into a project (levels, walls with their layers, doors and windows, floors, roofs, ceilings, rooms, grids, everything else as its shape); plans and sheets as DXF; IFC2x3 Coordination View 2.0 export; COBie 2.4 spreadsheets | §8 | ✅ |
| 1.13 | **Release packaging** — an MSI installer for every user, a Start menu shortcut and icon, .bimx files opening in BIMDesigner, a release script signing the program and installer with signtool. Done but for the certificate itself, which has to be bought or leased: until then releases build unsigned | §12 | 🔨 |
| **1.14** | **Columns** — architectural columns of any section, drawn or from a library (classical orders among them), tapering, twisting and leaning, bases, capitals and fluting; placed at grid intersections, on wall faces or built into walls, rising to what is over them | §3.1 | ✅ |
| **1.15** | **Components and families** — furniture, fittings and equipment placed freestanding or on a host, moved between hosts and levels; types of your own; a family editor to draw new ones from solids, and 3D models loaded as components | §2.2 §3.9 | ✅ |
| **1.16** | **Parts and painting** — wall layers made parts of their own, given their own materials, excluded or divided; faces painted, or split and painted in part | §3.1 | ✅ |

## Phase 2 — Documentation depth & data 🔨

| Step | Scope | Spec | Status |
| --- | --- | --- | --- |
| **2.1** | **Stairs** — straight, turning, winders, spiral, curved, split, along a line, alternating treads and ladders; risers worked out from the storey; openings and headroom; handrails, guarding and stairwell balustrades; checked against Approved Document K, BS 5395-2, the IRC and the ADA, made to pass in one step; plan, 3D, section, schedule, IFC | §3.6 | ✅ |
| **2.2** | **Railings and guarding** — balusters, glass, panels, mesh and cables, curved, with handrails; Guard Edges along every open edge that drops too far; Juliet balconies and glass guarding at doors and windows; checked for height, gaps and loads | §3.6 | ✅ |
| **2.3** | **Ramps** — straight, turning, switchback or along a line, laid out to their gradient with landings, kerbs and handrails; checked against Approved Documents K and M, the IRC and the ADA, made to pass in one step | §3.6 | ✅ |
| **2.4** | **Site** — ground from spot levels or a points file, contours, regions (lawn, paths, paving, gravel, planting), pads graded into the ground, cut and fill, retaining walls, the property line with its setback, ground cut away under the building | §3.8 | ✅ |
| **2.5** | **Fit-out** — kitchens, bathrooms and WCs, beds and living-room furniture, lights, sockets, switches, radiators, fans and alarms, cars, people, trees and parking, each at the sizes it comes in and checked for the room it needs | §3.9 | ✅ |
| **2.6** | **View templates, filters, keynotes, revisions** — templates (Round 3); keynotes and revisions (Round 4); view filters by parameter rules, hiding, colouring or fading what they pick out (Round 6) | §6.2 §7 | ✅ |
| **2.7** | **Design options and phasing** — phases created and demolished on every element; each view's phase and Revit's phase filters, existing faded and demolished red and dashed; design option sets with their primaries, worked on, shown by view and accepted | §7 | ✅ |
| **2.8** | **Classification systems, shared parameters** — the project's own parameters, instance or type, on chosen categories; shared parameter files read and written with their GUIDs; types and rooms coded to Uniclass 2015 or any table loaded, carried to IFC and COBie | §2.1 §7 | ✅ |
| 2.9 | Material library and rendering basics - materials with surface colours, paints and finishes; each material's face laid in its pattern at its true size, edited in a Materials window and drawn in the 3D view's Realistic style; and renderings, path traced in the sun at the site's place, date and time | §13.1 | ✅ |
| 2.10 | IFC import, DWG - **IFC import** done (Round 5); **DWG and DXF** brought onto a storey to work over (kept in the project rather than linked to the file), snapped to and traced, and plans and sheets written out as DWG or DXF on the National CAD Standard's layers | §8 | ✅ |

## Phase 3 — Collaboration 🔨
Worksharing, model linking, copy/monitor, cloud CDE, web/mobile viewer, issues/BCF,
version compare, permissions. §7 §12.1

Issues and BCF are done: issues raised on the 3D view or from clashes, kept in the project, and
exchanged as BCF 2.1 and 3.0 files with the other coordination tools; a BCF server (the BCF API) is
not. The rest of the phase is to come.

## Phase 4 — Structure & MEP 🔨
Beams of steel, timber, glulam and concrete and joist layouts are built, measured against
stair headroom and doorways and checked for their span. Structural columns - steel, timber,
glulam, concrete - and footings - strips under walls, pads under columns - are built, their
loads taken down from the model and checked against Approved Documents A and B and the IRC
(Round 8). The services are under way (Rounds M1 to M6 and M8): every fitting's connectors, systems made of
fittings and fed by equipment, services plans; water pipes sized and the water a home uses;
drainage checked and drawn in long section; heat loss room by room, radiators, floors and
heat pumps against it and heating pipes sized; ventilation - natural, by continuous extract or
with heat recovery - and its ducts sized; circuits designed and their cables sized, the board's
demand, each room's light, lights switched; the services coordinated with the structure - clash
tested, notched and drilled within the rules, openings cut and fire-stopped, supported - and
handed over in IFC4 and COBie, and the building sent out as gbXML for energy analysis.
Still to come: rafts and piles, rebar, analytical model; sprinklers and gas pipe sizing - in
round M7 below. §4 §5

## Phase 5 — Coordination & nD 🔨
**Rule checking** is built for homes: every wall, floor, roof, door, window, room, stair, ramp,
railing, gutter, soffit, chimney, fitting and the dwelling as a whole is checked against the
project's standard - Approved Documents B, E, F, H, J, K, L and M (M4(2) and M4(3)), the
Nationally Described Space Standard and the British Standards they point to, or the IRC and
the ADA - shown in each element's **Regulations**, room by room, and drawn red in plan.
Thermal U-values are worked from each build-up's layers (BS EN ISO 6946, ISO 13370). Clash
detection, the cost plan (5D) and the construction programme (4D) are built (Round 10). Still to
come: energy/daylight analysis, API/plugins, visual scripting. §9

## Phase 6 — Handover & FM 🔨
COBie 2.4 is written (Round 6). Still to come: asset register, O&M documents, maintenance,
digital twin/IoT, dashboards. §11

---

## Next: the rounds

Work goes on in rounds. Each takes one area and finishes it: how the real thing works is
looked up first, then it is built, tested and written up in the README.

| Round | What | Status |
| --- | --- | --- |
| **1** | **Finish the half-built tools** — floors and ceilings sketched with holes and Edit Boundary, span direction, slab edges; sketch corners dragged, Move, Mirror, Align and Fillet Arc in every sketch; soffits sloping under verges, and soffits of any shape sketched as soffit-board ceilings; downpipes cut in section (already were). The rules left from the audit: space separation to the property line (Approved Document B 11.8, 11.17-11.18); party walls - 60 minutes, up to the roof (B 5.8, 5.11); a step-free approach where outside steps reach every door, approach paths' width, cross-fall and passing places, and a level, covered landing at the entrance (M4(2) 2.6-2.10, 2.20; M4(3) 3.9, 3.22); 42 in stair guards outside homes under the ADA (IBC 1015.3); air at the ridge of a steep or wide cold roof (BS 5250; IRC R806.2); distributors on downpipes onto a lower roof (Approved Document H 1.9); gas flue terminals (Approved Document J Diagram 34; IFGC 503.6.5). Already met by how they are built: no gap under a stair's bottom rail, an ADA switchback's handrail continuous round its landing | ✅ |
| **2** | **Massing, then building by face** — masses as a box, cylinder or any outline, flat, tapered or to a point, or topped with a gable, hip or shed; mass floors, gross floor area, volume and surface area; Wall by Face (leaning on a tapered mass, shaped to a gable, halved round a cylinder), Curtain System by Face on upright sides, Floor by Face at the mass floors, Roof by Face (flat, pitched, pyramid or cone, gable, hip, shed); Update to Face bringing them back to a changed mass in one step; roof openings by face, cut square to the slope through the build-up. Not done: sloped glazing - a curtain system on a sloping face | ✅ |
| **3** | **Views** — 3D views kept by name, with cameras; a section box that closes what it cuts; wireframe and hidden-line styles, perspective or orthographic; walking through the model and walkthroughs; interior elevations cropped to their rooms; callouts of plans and sections; detail level by view; view templates applied once or governing views; 3D views and elevations on sheets as vector drawings, each view numbered on its sheet. Walkthroughs exported as video; things picked in interior elevations; section callouts cropped by their corners; templates governing sections and elevations; shaded views on sheets painted exactly as far as each face shows | ✅ |
| **4** | **Drawings and documents** — dimension styles (ticks, arrows or dots; text, witness lines, units); dimension strings through many walls; angular, radial, diameter and arc length dimensions; keynotes off types, materials or a key of their own, a keynote table from an office's file or an NRM1-headed starter set, keynote legends by sheet or project; revisions numbered P01/C01, issued and locked, clouds with their tags, revisions on each sheet, the revision schedule and current revision in the title block; schedules kept in the project, made, edited, duplicated and deleted; calculated fields - formulas and percentages; conditional formatting; schedules and legends on sheets as tables; a sheet list | ✅ |
| **5** | **Files and release** — plans and sheets as DXF at true size, by layer; IFC2x3 and IFC4 read into a project, walls with their layers and their doors and windows, floors, roofs, ceilings, rooms and grids as themselves, everything else as its shape, a far-off model brought to the origin; IFC2x3 Coordination View 2.0 export; walls' axes, gridlines and components' own placements in IFC4; an MSI installer with a Start menu shortcut, an icon and .bimx files opening in BIMDesigner, and a release script that signs it. Not done: the signing certificate itself, which has to be bought or leased - until then a release builds unsigned | ✅ |
| **6** | **Finish Phase 2** — phasing (each view's phase and phase filter, the overrides drawn in plan, on sheets and in 3D, joins kept to the walls a view shows); view filters by parameter rules; design options; project and shared parameters; Uniclass classification carried to IFC and back; COBie 2.4 export | ✅ |
| **7** | **Eaves** — an eave type for each of the five kinds: closed (a soffit sloping under the rafters), open (rafters and boarding on show, a fascia over their ends), exposed rafter (tails cut plumb, square, slant, cove, bullnose or scroll, no fascia), box (a level soffit, frieze board and bed or crown moulding) and abbreviated (close to the wall); brackets, corbels or knee braces at a spacing; the roof's build-up cut back to its rafters over the eave, the rafters set out one by one with hips and jacks at the corners, blocking between them at the wall; in 3D, elevations and section, saved and exported to IFC. The rules: air into a cold roof (BS 5250; IRC R806.2), fire-stopping carried into the eaves over a party wall (Approved Document B 5.11b, Diagram 5.2), and no eave within 2 ft of a lot line, its underside protected within 5 ft unless fireblocked at the wall (IRC R302.1, Table R302.1(1) and its notes) | ✅ |
| **8** | **Structure** — structural columns: steel universal columns and a W shape, square and round hollow sections, timber posts, glulam and concrete, drawn as their sections, their loads taken down from the floors and roof on them; footings: strips under walls with the blockwork on them, pads under columns with their piers, sized for the load and the ground and taken down out of the frost; the ground, clay shrinkage, frost line and snow load set for the project. Checked against Approved Document A (Section 2E, Table 10) and B (5.1, Table B2), and the IRC (R403.1, Tables R403.1(1) to (3), R401.4.1, R407, R317.1); IFC columns and footings; schedules. Also: sprinklers throughout as a project setting (Approved Document B 11.21; IRC Table R302.1(2)) and the IRC's walls and openings near a lot line (R302.1). Not done: rafts, piles, stepped footings on sloping ground, rebar | ✅ |

| **9** | **Mullions** — curtain wall mullion types: rectangular with an offset and a cover cap, circular, and L, V, trapezoid and quad corner sections, in aluminium, steel or timber, hollow or solid; a curtain wall type's interior and border mullions each way, none, or its own as before, and which run through where they meet; one stretch of a line given another type or none in the grid editor, kept through moves, splits and joins; a corner mullion where two curtain walls meet, built by one; each mullion and transom checked as a beam under the wind (CWCT; IBC 2403.3; AAMA TIR-A11) and each transom for the sag its glass gives, against a wind pressure set for the project. Window mullions and transoms: lights side by side, which open and which are fixed, a transom with fixed or top-hung fanlights; escape through one light, air through every opener, each pane a pane for safety glass, guarding by the lowest opener (Approved Documents B 2.10, F Table 1.4, K; IRC R310.2, R308.4.3, R312.2); IFC4 partitioning. Then the gaps: the wind worked out from the site - BS EN 1991-1-4's peak velocity pressure from the UK map speed, altitude and terrain, Table 7.1's wall zones for each member's loaded area; ASCE 7-16's qh and wall components and cladding zones, 0.6 of it for deflection (IBC 2403.3); mullions anchored at each floor and spliced above them, each anchor's wind and weight, stack joints for thermal and floor movement, lengths no longer than stock; each mullion an IfcMember and each anchor an IfcDiscreteAccessory. Not done: the UK National Annex's own exposure charts (the Eurocode's expressions are used), orography, ASCE 7's 10% wall reduction for low-slope roofs (left out, on the safe side), anchor design beyond a capacity given | ✅ |
| **10** | **Coordination: clashes, 4D and 5D** — clash tests between category sets: hard (penetration deeper than the tolerance, so touching faces and joined walls are no clash), clearance, duplicates, and sequence clashes against the programme; what is hosted or joined left out; each clash kept from run to run as an issue - new, active, reviewed, approved, resolved - with its storey, grid location, assignee, priority, due date and comments; shown in 3D; BCF 2.1, HTML and Excel reports. The construction programme: tasks made from the model storey by storey and timed by trade outputs (BCIS bricklaying), or by hand, linked to elements by rule or selection; working-day calendar; critical path by forward and backward pass; baseline, actual dates, percent complete and actual cost; Microsoft Project XML in and out (read by Primavera P6 and Asta); IFC4 IfcWorkSchedule, IfcTask, IfcRelSequence and IfcRelAssignsToProcess; the 3D view on any day - building green, demolishing red, temporary yellow, look-ahead and late orange - played and saved as video. The cost plan: quantities by item, metre, square metre, walls net of openings; NRM1 in England, UniFormat II in the US; preliminaries, overheads and profit, fees, other costs, risk, inflation and tax; cost per m²; cash flow over the programme; earned value (PV, EV, AC, SPI, CPI, EAC); Excel export. Also: floors, roofs and ceilings now follow 3D view filters and phases. Not done: BCF import, clash grouping, resource levelling, Primavera XER, a cost database of rates beyond each type's own | ✅ |
| **11** | **People who walk and see** — walking as an adult, a child or a wheelchair user, each with their own eye height and stopped where they would be: a wheelchair user by steps over 15 mm (ADA 303: 1/2 in), slopes steeper than 1:12 and doorways narrower than 700; walking as a person placed in the model, through their eyes, who is left where the walk ends; the area a person sees, shaded on the plan (an isovist on the plan cut at their eye height, glass and open doors seen through); lines of sight; the doors, windows and rooms a person can see; the view out graded by BS EN 17037 (sight angle, outside distance, sky, landscape and ground layers; seated 1.2 m, standing 1.7 m) for each room and each person; a living room's principal window glazed from 850 at most (Approved Document M, M4(2) 2.24). Not done: daylight factor and sunlight exposure (the rest of BS EN 17037), glare, people walking routes by themselves (crowds, evacuation) | ✅ |
| **12** | **Hardening: large models first** — measured on a block of 225 rooms over three storeys (1,440 walls and 1,440 doors and windows, 2,883 elements in all) and profiled. In a release build, the 3D model now builds in 0.9 s (it took 8 s) and the 3D view refreshes in about 1 s; the plan redraws after an edit in 0.6 s (it took 9-10 s; in a debug build 15-22 s); all six default clash tests run in 0.7 s together (one took 8 s). What made it slow: every wall asking every other wall who it meets, at both ends, again for each neighbour; every door's rules looking at every other door to find the entrance storey; each room traced again for every door beside it; every clash test trying every pair in the building; and the 3D view's edges handed to WPF one point at a time. Then crash protection: whatever goes wrong is logged (a file a day, kept a fortnight) and said in words, with the details to copy, and BIMDesigner carries on rather than closing; an edit that goes wrong part-way is taken back and left out of the undo list, as is an undo or redo that does; an element that cannot be drawn is left out of the plan, section or 3D view and the rest drawn; unsaved work is kept as a recovery copy every five minutes and whenever something goes wrong, thrown away on saving or closing, and offered back at the next start after a crash, power cut or restart. Then IFC files from other programs: 13 real buildings (ArchiCAD's FZK-Haus and institute, Revit's duplex, office, dental clinic and DigitalHub, Tekla, BlenderBIM and others, IFC2x3 and IFC4) and 253 files people sent the IfcOpenShell project as bugs, each opened, built in 3D, drawn on every plan and saved and opened again. What was wrong: a wall's layers were put on the wrong side of its axis when they run the negative way from an offset - how Revit writes every wall - so walls did not meet and rooms did not close, and the export wrote flipped walls the same wrong way; walls clipped to a roof, walls, floors and rooms given as solids of faces, windows that do not say which wall they are in, and Revit's advanced solids were not read; walls stopping at the face of the wall they meet left gaps; open-plan rooms had nothing to part them. Now walls clipped to a roof come in shaped to it, solids come in as walls, floors and rooms where they are, ends are joined to the middle of the wall they meet, windows go in the wall they stand in, clipped shapes are cut, and the new room separators part rooms the file draws with no wall between. Every room of the ArchiCAD buildings is enclosed, with its area; in Revit's office 81 of 99, in the clinic 202 of 269. Not done: curtain walls as curtain walls (they come in as their panels and mullions), stairs and pitched roofs as themselves (as shapes), BCF and IFC4.3. Last, a bug hunt: a house drawn from nothing by clicking and typing as a person does - walls, a partition, doors, windows, rooms, a floor and a roof, copied to the floor above, scheduled, put on sheets, undone and redone twenty times, saved, opened again, printed to PDF, sent out as IFC and read back, and all 75 tools tried - and what got in the way fixed: a room drawn round came apart when a wall of it was moved (the chain's corners are now locked) and, drawn anticlockwise, had its walls' outsides facing in (now turned out as the loop closes); taking a wall by its middle bent it rather than moving it (the bend grip is now beside the wall); the 3D view in a wide window cut off the top and bottom of the building; the first view put on a sheet went in a corner; opening a project kept the last one's section and sheet open; an elevation offered to walk through it, and the 3D view after it looked square-on; the option bar's boxes cut their text short; the title gave no version; changing a type said nothing; and the Roof tool drew a pitched roof with the flat roof's build-up | ✅ |
| **13** | **Starting work** — a splash while BIMDesigner gets ready, then a home screen: the projects opened or saved before, newest first and the pinned above them, each with a picture of its plan, searchable, and New project, Open a project, Open an IFC file, the example house, or recovering unsaved work. New Project in five steps - the template (England, England M4(3), the US), the project's particulars, the site and ground, the storeys and where it is kept - each checked before the next, the levels made for them. File > Close Project back to the home screen, File > Open Recent | ✅ |

Left until those are done: Phase 3's collaboration, the rest of Phase 4's structure and MEP,
and Phase 6's handover beyond COBie. A DWG brought in is kept in the project; linking it so that
it follows changes to the file is not done. A rendering is saved as a picture; putting it on a
sheet, and lights in the model for night scenes, are not done.

## The services: rounds M1 to M8

Mechanical, electrical and plumbing - heating, ventilation, water, drainage, power, lighting
and fire protection - in rounds of their own, homes first, where the rules are modelled
already, then larger buildings. Each is designed and checked to the UK's rules and the US's.

| Round | What | Checked to | Status |
| --- | --- | --- | --- |
| **M1** | **Connections and systems** — every fitting's connectors where pipes, ducts and cables join it, sized as a home's are (a basin's 15 mm hot and cold and 32 mm waste, a WC's 100 mm soil, a radiator's flow and return, a combi's heating, hot water, cold main, gas, condensate and spur), each pointing the way it leaves; a hot water cylinder, the stop valve on the incoming main and the gas meter to run systems from, and system boilers beside combis; systems of cold and hot water, heating zones, foul water, gas, extract air, circuits and the smoke alarms' interlink, made of the fittings selected, a fitting on one system of a kind, fed by the equipment picked or the only one there is - a consumer unit any number of circuits, a boiler any number of zones; Edit System to click fittings on and off, Pick Source; the System Browser by discipline with what is not connected and what is wanting; mechanical, electrical and plumbing plans of each storey, the building faint and the discipline's fittings in its colour, anything not connected red, on sheets like any plan; heating zones as Approved Document L counts them, the rooms each system serves; systems and ports in IFC4. Not done: room design temperatures and air changes for heat loss (M4), switches linked to the lights they work (M6) | IFC4 IfcDistributionSystem, IfcDistributionCircuit and IfcDistributionPort; Approved Documents G (G1, G3), H (H1), L (2021, zones) and B (1.1) with BS 5839-6; IRC R314.4 and R314.6 | ✅ |
| **M2** | **Pipes and water** — pipe types in copper (BS EN 1057), PE-X, stainless and galvanised steel, in the sizes BS EN 806-3 tabulates; pipes drawn from a fitting's connector, a pipe's end or a point along one, level at their run height and rising or dropping upright to what they end at, teeing in where they meet a pipe, refusing to join one carrying something else, taking the system of what they join and putting the fitting they reach on theirs; drawn in plan in their system's colour, round in 3D, cut in sections; each system's pipes traced from its source - what each section carries, how far each fitting is along them, the pipes nothing reaches, the fittings no pipe reaches - and Size Pipes setting each cold and hot water pipe as the standard's simplified method asks, a water heater's cold feed carrying the hot it serves; the dwelling's water use by Approved Document G's calculator from each fitting's flush, flow, capacity and appliance figures, against 125 or the optional 110 litres a person a day, with the fittings approach; in the US, fixture units and the 100 ft hot water run; a Pipe Schedule, pipes costed by the metre under their NRM1 and UniFormat elements, and pipes as IFC4 pipe segments joined port to port. Not done: multilayer and polybutylene (BS EN 806-3 Tables 3.5 to 3.8, not seen), valves and pumps on pipes, pipes following a fitting that moves, sizing to the IRC's Appendix AP, a UK dead-leg limit (BS 8558's table, not seen) - each run's length to its fitting is given instead | BS EN 806-3 Tables 2 and 3; Approved Document G (G2, Appendix A Table A1, Tables 2.1 and 2.2); IRC Table P2903.6 and P2905.3 | ✅ |
| **M3** | **Drainage** — foul and rainwater systems traced back from their outfalls - the foul sewer, the surface water sewer, a soakaway - every pipe knowing the fittings and WCs it carries and which way it falls; PVC-U soil, waste and drain pipe; runs that fall at their gradient as they are drawn, tee into a stack at the height they reach it and will not run uphill; rodding eyes, access fittings, inspection chambers and manholes with their cover level, invert and depth, in a Manhole Schedule; air admittance valves; every downpipe to the ground taken by a rainwater drain; long sections from a drain's head to its outfall; checked by Approved Document H - trap sizes, branch lengths and falls (Table 1, Table 2, Diagram 3), stack sizes, the 450 mm above a stack's foot and its ventilation with the 900 mm over openings within 3 m, a ground floor WC straight to a drain within 1.3 m, drain sizes and gradients (Tables 5 and 6, 3.14, 3.15), access at heads, bends and changes and Table 13's spacing, soakaways 5 m from the building - and by the IRC in the US (fixture units, trap arms, falls); drainage costed under NRM1 8.6 and in IFC4 as distribution chambers. Not done: BS EN 12056-2's discharge units and pipe capacities (not seen; Approved Document H's tables used), ventilating stacks and branch vents drawn as such, gullies and back-drop manholes, sizing drains by flow, pumped drainage, the IRC's stack and branch sizing tables (not seen) | Approved Document H (H1 Tables 1, 2, 3, 5, 6, 11, 12, 13, Diagrams 2 and 3; H3 3.14, 3.15, 3.25); IRC Tables P3004.1, P3105.1, P3005.3 (as reported) | ✅ |
| **M4** | **Heating** — heat loss room by room from the model as it stands: each side of each room the wall along it less its windows and doors, the floor under it and the roof or storey over it, at their U-values with an allowance for thermal bridges, to outside at the design temperature, to the ground at the annual mean under a solid floor and outside under a suspended one, to neighbours and unheated spaces at the annual mean, to rooms at other temperatures at theirs; rooms at their design temperatures by use (21 °C, a bathroom 22, a garage or loft unheated; 68 °F in the US) or as set; the air by BS EN 12831-1's own method from the project's air permeability; the UK design temperature from the nearest of CIBSE Guide A's 14 stations and the site's altitude, the US one from the jurisdiction's table; a Heat Loss window of every room's surfaces and air, the building's load, each heat source against its load, each zone's index circuit and the home's hot water; radiators and towel rails at their BS EN 442 outputs and exponents, underfloor heating at its maker's output per kelvin up to BS EN 1264's warmest floor, each room's emitters against its loss, and Size Radiators choosing from a maker's tested range; heat pumps beside boilers, each zone's flow and return temperatures; heating pipes carrying the heat beyond them, sized for 1.0 m/s and 300 Pa a metre by Darcy-Weisbach, flow and return kept apart as they are drawn and the return dashed; heat loss and design temperature on each IFC space, radiators' output, the heat pump as unitary equipment. Not done: mechanical ventilation and heat recovery in the heat loss (M5 - natural ventilation with intermittent extract is taken); roof windows' own U-values; BS EN 1264-2's own output calculation, manifolds and loops drawn as pipes; heat pump efficiency at its flow temperature, hybrids (MIS 3005-D 5.5.2) and cylinder sizing (MGD 007, not seen); a pump's emitters, valves and fittings beyond the pipes' loss; ACCA Manual J and S (not seen - the same method at the IRC's 68 °F and Table R301.2's temperature); CIBSE Guide C's tables (not seen - worked out from first principles); thermal bridges by psi-value | BS EN 12831-1:2017 (6.3.3, as MCS's heat load calculator and open implementations give it); MCS MIS 3005-D Issue 3.0 (5.5.1, Tables 1 and 2, 5.5.5, 5.6.2); CIBSE Guide A Table 2.5 and Domestic Heating Design Guide 2026 Tables 2-2, 2-9 and 2-18 (as MCS's calculator gives them); RdSAP 10 Table 21; Approved Document L 2021 (Tables 1.1 and 4.1, 5.8, 5.10, 5.12); BS EN 442 (Stelrad's CETIAT-tested outputs); BS EN 1264 (limits and basic characteristic, as Danfoss and Grant give them); IRC R303.10, Table R301.2 | ✅ |
| **M5** | **Ventilation and ducts** — a heat recovery unit, supply and extract valves and outside terminals, and supply, extract and outside air systems fed by the unit; each room's air as Approved Document F sets it - wet rooms extracted at Table 1.2's high rates, habitable rooms supplied by volume, the whole at the whole dwelling rate (0.3 l/s a m² or Table 1.3's), balanced - and in the US the IRC's whole-house and local exhaust rates; ducts drawn with a Duct tool as pipes are, in galvanised spiral, PVC round, PVC flat channel and flexible duct, in plan, 3D and sections; each duct's airflow, speed and friction by Darcy-Weisbach, each system's designed static pressure to its furthest valve against Approved Document F's Table 1.8 (2026 edition) and its unit's available pressure, and Size Ducts by equal friction to what the bends, tees and valves leave of it - ASHRAE's fitting loss coefficients at each bend and tee, each valve's maker's loss at its rated airflow; flexible duct kept to final connections of 1.5 m; natural ventilation with trickle vents and background ventilators to Table 1.7, five at least and 500 mm from a fan; continuous extract by dMEV fans or a central unit at the whole dwelling rate, with 4000 mm² in each habitable room and none in the wet rooms; intake and exhaust ducts insulated 50 mm in the heated home and 25 outside it; ducts through compartment walls and floors protected by fire dampers of 60 minutes or the wall's or floor's rating, fire and smoke dampers where a system serves homes on both sides, a duct smoke detector to close them, or fire-resisting duct, never a damper on a kitchen's extract, and rigid steel through a protected stair's enclosure, by Approved Document B; heat recovery and unbalanced extract in the heat loss; in the US ACCA Manual D's velocities and ASHRAE 62.2's rate beside the IRC's; a Duct Schedule, ducts costed under NRM1 5.7, and IFC4 duct segments joined port to port, air terminals, background ventilators, the heat recovery and extract units, duct smoke detectors and fire dampers in walls and floors. Not done: oval duct; fans' and units' curves (a unit's available pressure is one figure) and noise; BS EN 16798 and CIBSE Guide B (not seen); ASHRAE 62.2 and IECC R403.3.1 are as reported, and Manual D's Table 3-1 from ACCA's checklist as AHRI publishes it, not the manual itself; the 2.5 m/s quiet speed is installers' practice | Approved Document F 2021 (1.17, 1.24, 1.52 to 1.57, 1.62 to 1.64, 1.67 to 1.72, 1.77, Tables 1.1 to 1.3 and 1.7) and 2026 edition (1.83, Table 1.8); Approved Document B Volume 1 (2.9, 9.8, 9.12, 9.14, 9.15, 9.20 to 9.22); BS EN 12831-1 6.3.3.3 and 6.3.3.7; ASHRAE fitting loss coefficients 3-1, 3-5, 5-3, 5-10 and 5-23 (Principles of HVAC, after Idelchik); Trox LVS and Z-LVS data; ACCA Manual D Table 3-1 (AHRI's copy of ACCA's checklist); IRC M1505.4.3 (Equation 15-1) and Table M1505.4.4, ASHRAE 62.2 and IECC R403.3.1 (as reported) | ✅ |
| **M6** | **Electrical and lighting** — each circuit run from its board through the void over the ceiling to its fittings, a ring back; each fitting's rated load; its breaker, curve, RCD and AFDD, its cable, how it is installed and grouped, its length; its design current, what its cable carries as installed, its voltage drop fitting by fitting and its earth fault loop impedance against Table 41.3 where no RCD protects it; the On-Site Guide's standard ring and radial socket circuits, their floor areas and lengths; RCDs on sockets, lighting and bathrooms; the board's maximum demand after the On-Site Guide's diversity against its supply, and its ways; Size Circuits choosing breakers and cables; an Electrical window and a Board Schedule; electric showers; in the US NM-B at 60 °C, 240.4(D), receptacle circuits at 80%, the required small-appliance, laundry, bathroom and garage circuits, GFCI and AFCI where the NEC asks, the dwelling's load by Article 220's standard and optional methods against its service; each room's light by the lumen method against BRE's targets for SAP 10 or the IES's, makers' IES and EULUMDAT files read and their direct light point by point, efficacy to the IECC in the US; lights switched, two-way, and a US stair switched at each floor; smoke alarms where Approved Document B and the IRC put them, CO alarms in the US; wiring drawn on the electrical plan; circuits costed by the metre and their breakers, and in IFC4 with their protective devices. Not done: conduit and cable tray fill, data and TV outlets, three-phase supplies, SPDs and the cpc's adiabatic check, the OSG's lengths for TN-S and type C beyond the ring, inter-reflected light point by point (direct only), utilisation factors beyond two published fittings, outside lighting's controls; BS 7671 and the On-Site Guide themselves, SLL LG9, IES RP-11 and BS 5839-6 not seen (the IET's notes, makers' copies and BRE's paper used), and the 2023 NEC from NFPA's committee documents rather than the printed code | BS 7671:2018+A4:2026 (411.3.3, 411.3.4, 433.1.1, 433.1.204, 701.411.3.3, Tables 4D5, 4C1 and 41.3, Appendix 4) and the IET On-Site Guide (Table A2, Appendix H, Table 7.1), as the IET and makers reproduce them; BS EN 60228; Approved Document B Volume 1 (1.1), Approved Document L Volume 1 (6.58); BRE S10TP-06; Signify DN027B and Cooper TM5 tables; IES LM-63 and EULUMDAT; NEC 2023 (210.8, 210.11(C), 210.12, 210.19, 210.23, 210.52(B), 210.70, 220.41 to 220.82, 230.79, 240.4(D), Table 310.16, 334.80) from NFPA's committee documents; IRC R314.3 and R315.3; IECC R404.1 and the IES Lighting Handbook's Table 33.2 (as reported) | ✅ |
| M7 | **Fire protection and gas** — sprinkler heads, their coverage and pipework; gas pipes sized from each appliance's demand back to the meter | BS 9251, BS EN 12845, BS 6891, Approved Document J; NFPA 13D/13R | ⬜ |
| **M8** | **Coordination and handover** — the services in clash tests against the structure and each other with their insulation allowed for, joined pipes and ducts left out; services through timber joists notched and drilled within the NHBC's and the IRC's rules; openings cut where pipes and ducts pass walls and floors, and pipes through compartment walls and floors fire-stopped, sealed or sleeved as Approved Document B's Table 9.1 has it; supports at their spacings, drawn and costed; gas kept from the electrics and the US panel's space kept clear; pipes and ducts from other programs' IFC read as pipes and ducts on their systems; COBie's System sheet a row for each component on each system, classified by Uniclass EF, and its Connection sheet each source to its fittings; the programme's services first fix before the ceilings and second fix after; heat source and builder's work in the cost plan (NRM1 5.5, 5.14); gbXML 7.03 for energy analysis, spaces at the walls' centre lines closing round each room, constructions and window types as OpenStudio needs them. Not done: clearances for access and maintenance beyond the panel's, hangers in IFC, fittings' insulation, studs and engineered joists (cut only as their makers say), gbXML for pitched roofs (taken flat at each room's height), shading and the site's latitude; BS 8558's support table and BS 5422's insulation tables not seen, and BS 1192-4 read only in an unofficial copy | IFC4 (Reference View 1.2), COBie 2.4 (NBIMS-US V3 4.2, Annex A), Uniclass 2015 EF; NHBC Standards 2025 (6.4.18, Table 6); IRC R502.8.1, R302.4.1.2, E3405.3 and Table P2605.1; Approved Document B Volume 1 (section 9, Table 9.1); BS 6700:1997 Table 14 (withdrawn), BS 6891 (Table 4, 8.4.2), DW/144 (Tables 16, 17), SMACNA, the ADC's flexible duct standards and makers' figures; NRM1; gbXML 7.03 schema | ✅ |

Rounds M1 to M8 were then gone over again for bugs. The services' problem list, gbXML, COBie, IFC,
the cost plan and schedules now work out each room's outline, heat loss, circuit and duct run once
for the whole of a run - a services check of a 108-room model 38 s to under 2 s, its gbXML 145 s to
under 1 s. Fixed along the way: pipes and ducts drawn before their system was made left off it;
a circuit's Properties showing its old breaker after a change; NM-B bunched without its 90 °C
derating; IES and EULUMDAT files symmetric about the 90-270 plane read the wrong way round; an
extract tee's straight run losing by its own share of the air; flexible connections judged piece by
piece; a ceiling under an upper floor taken as the roof, and overhanging floors losing to the
ground; Size Radiators leaving the pipes at the old tappings; pipes of one system never clashing,
and pipes on none clashing at their elbows; openings cut square to a wall whatever the angle;
gbXML's ground taken from a foundation level; IFC ports joined to more than one other, tees now
junction fittings; worksets lost on saving pipes, ducts and systems; and a room measured to the
centre line wherever a wall tees into its side from beyond. Then: a system's Wanting runs only its
own checks (2 s to 20 ms on that model, the same answer as the whole model's), a room's view out
builds only what could be in its way (2.2 s to 0.2 s), and pipes through curved walls get their
openings.

After M8: plant for larger buildings (air handling units, VAV boxes, chillers), pipes and ducts
routed by themselves, fabrication detail (spools, shop drawings) and medical gases.

---

## Design decisions

**One model, many views.** A `Wall` is stored once. The plan view, the future section,
elevation, 3D view and schedules all read that same instance. There is never a separate
"2D wall" and "3D wall". (§1)

**Millimetres everywhere.** Every length in the model is a `double` in millimetres. Areas
are mm², volumes mm³. Conversion happens only when formatting for display, in
`BIMDesigner.Core/Units.cs`. (§2.3)

**Type vs. instance is not optional.** Layers, fire rating and cost live on `WallType` and
are shared by every wall of that type. Height, offsets and mark live on the `Wall`. This
split is the difference between BIM and CAD and is expensive to retrofit, so it is in from
the start. (§2.1, §13.1)

**Parameters are a view over real properties.** Geometry code reads typed C# properties
(`wall.Length`) for speed; the property panel, and later schedules and IFC export, read the
same values through `GetInstanceParameters()`. One source of truth, one uniform data view.
(§2.1, §6.4)

**Core stays UI-agnostic.** `BIMDesigner.Core` references no WPF. That is what will let the
same model back a web viewer or a headless IFC exporter later. (§12.1)

**Every edit is a command.** Nothing mutates the model directly; changes go through
`UndoStack` as `IUndoableCommand`s that store only what it takes to reverse themselves. A
snapshot-based undo would be simpler today and unusable at the model sizes §12.3 asks for.

**Joins are derived, not stored.** Two walls join because their endpoints coincide, and the
mitre is recomputed from the current geometry whenever the plan draws. Storing join records
would mean maintaining them on every move, and they would go stale the first time something
changed them without going through the right code path. (§2.4)

**Worked out once per drawing, never kept past it.** Joins, room outlines and every rule that
looks round the building are derived each time something is drawn, and on a large model that
meant each wall asking every other wall who it meets, at both ends, again for each of its
neighbours. So while one plan, section or 3D view is drawn, `ModelCache` keeps what is worked
out - each wall's end cuts and slices, the walls near a point found from a grid over the plan,
each room's outline, facts about the whole building - and throws it all away when the drawing
ends or anything is added or removed. Joins are still derived, never stored; they are just not
derived twice in one drawing. Beside it the document keeps every element of a kind (`All<T>`)
until the elements change, and finds one by its id (`Find<T>`) rather than looking through them.

**A mitre that runs away is refused.** Near-parallel walls have near-parallel faces, so the
mitre corner tends to infinity and the wall draws as a spike. A mitre may reach no further
than twice the wall's width, nor further than half the shorter wall's length; past either
the end is butted square — the same mitre limit vector graphics applies to strokes.
Likewise, three or more walls at one point are butted, because no single mitre is right
against all of them.

The limit has to be strict to be worth anything: a generous one still admits a taper half
the wall's length, which looks exactly as broken as no limit at all. `WallSpikeRegressionTests`
sweeps 359 angles at four wall lengths and asserts no corner ever escapes. (§2.4)

**A section is a question, not a drawing.** A section marker stores only the cut line, which
way it looks and how far it sees. The drawing is produced by cutting the model every time
the view repaints, so a section can never show the building as it used to be and there is no
"regenerate" step anyone can forget. It is the same reasoning as rooms and schedules, applied
to a view. (§6.1)

The section was also the first view to draw the model's *vertical* data — level elevations,
base offsets, top constraints, slab thicknesses, sill and head heights. All of it had been
stored and scheduled for several steps without ever being looked at.

**One drawing, drawn once.** The plan inside a viewport on a sheet is produced by the same
`PlanRenderer` the plan editor uses, and the section by the same `SectionRenderer`. Only the
transform and the palette differ. A sheet that rendered its own simplified version of a plan
would eventually disagree with the plan, and a drawing set that disagrees with itself is
worse than none. What the renderers deliberately do *not* draw is grips, snap rings and
half-finished walls: those belong to the editor, not to the drawing, and must never reach
paper. (§6.1, §6.5)

**The PDF is written, not printed.** `PdfWriter` emits vector PDF directly by walking the same
WPF drawing the sheet view produces. Printing to a PDF driver would have been less code, but a
drawing set has to be produceable without a printer installed, on a build server as easily as
on a laptop, and has to come out byte-identical each time. Every geometry is flattened to
polylines first — curves, arcs and glyph outlines all arrive as the same thing — which is what
keeps the writer to one file. Text is written as outlines, so no font has to travel with the
drawing and it cannot be substituted at the other end. (§6.5)

**Scale belongs to the viewport, not to the view.** The same plan is legitimately 1:100 on a
general arrangement and 1:20 on a detail sheet, so the scale is a property of how a drawing is
presented rather than of what it contains. Fitting a view picks from the scales that appear on
a scale rule, never an arbitrary ratio — a drawing at 1:87 cannot be measured. (§6.5)

**Annotation is sized on the page; construction is sized in the building.** A grid bubble is
about 6 mm across whether the drawing is 1:20 or 1:200. The editor treats its own surface as a
page at 1:1, which is what makes a bubble on screen the size it will print, and what let the
screen sizes already in the code carry over to sheets unchanged. (§6.2)

**Three audits, asking one blunt question each.** Beside the tests that check a feature does
what its author intended, three ask the same question of everything at once:

- **Round trip** — save a project with one of everything and every field set, reopen it, and
  compare *every parameter every element reports*. A field left out of the file format shows up
  without anyone having to think of it. It found that doors and windows were getting a new
  identity on every open, because `Element.Id` is init-only and the shared opening loader runs
  after construction, so it silently could not set it.
- **Undo** — do a thing, undo it, and insist the saved project is byte-for-byte what it was;
  then redo and insist it matches the first result. A command that undoes *almost* completely
  is the worst kind, because the user sees the shape go back and believes the model did too.
- **Robustness** — degenerate and broken models (no levels, zero-length walls, orphaned doors,
  annotation pointing at deleted elements, sheets whose views have gone) run through every
  consumer: file format, schedules, section, 3D and IFC.

**IFC is written with a library; everything else is written by hand.** `Xbim.Essentials`
(CDDL) handles the IFC4 schema, in `BIMDesigner.Infrastructure` only — Core has no idea IFC
exists. This is the opposite of the call made for PDF, and deliberately so: our PDF writer
emits about eight operators, while IFC4 is an EXPRESS-derived schema of hundreds of entity
types with strict attribute ordering and inverse attributes. The value of the library is not
saved typing, it is schema correctness that other people's software agrees with — a file that
looks right to us and that Revit quietly refuses is the expensive failure here. FreeCAD makes
the same trade, delegating to IfcOpenShell.

Geometry is exported as `IfcExtrudedAreaSolid` — real profiles swept to real heights, using
the same mitred wall outlines the plan draws — rather than as triangles, because every element
in this model genuinely is an extruded outline. Tessellating would throw away the fact that a
wall is a wall. Openings are exported as actual voids with the door or window filling them, so
the receiving application can move the door or take it out and leave the hole. (§8)

**Identifiers are derived, not generated.** An element's IFC GlobalId comes from its own id, so
exporting the same project twice produces the same identifiers and the receiving end can tell
an edited wall from a new one. Freshly generated ones would make every re-issue look like a
new building.

**3D geometry is built in Core, not in the viewer.** `ModelMeshBuilder` produces plain
triangle meshes from the same walls, openings, levels and slabs every other view reads, with no
reference to any 3D library. The WPF viewport only draws them. That keeps the geometry testable
— the tests measure where walls start and stop and whether a door really leaves a hole — and it
means IFC export or a web viewer can reuse it instead of working the building out again. Walls
are extruded layer by layer, so a cavity wall is brick outside and plaster inside rather than a
grey block. (§6.1)

**Deleting a level deletes what stands on it.** A wall whose base level has gone has no
height and no place in the building, so the deletion cascades — and because it is one
command, one undo brings the whole storey back. Walls that only *reach up* to the deleted
level are different: they stand on a level that still exists, so they keep their height as an
unconnected one rather than being deleted. (§2.3)

**A copy is three operations, not one.** The copy needs a new id, or every schedule counts one
element twice. A copied wall has to bring its doors, rehosted onto the copy. And references
*within* the copied set have to follow the copies while references *out of* it stay put — a
tag copied with its wall labels the new wall, a tag copied alone still labels the old one.
`ElementCopy` does all three, is written out per type rather than by reflection so a new
property forces a decision about whether copies carry it, and deliberately does not copy
**marks**: two doors both marked D-04 reach the site through the door schedule. (§2.4)

**Paste in place is how a storey is built.** Copy the ground floor, open the first floor,
Ctrl+Shift+V. Pasting onto another level carries a wall's top constraint up with it, so a
wall that reached the first floor now reaches the second rather than pointing at the level it
stands on and having no height at all.

**Hosted elements are never moved directly.** A door is positioned along its wall, so it
travels with the wall. Moving a selection that contained both would otherwise slide every
door along its wall by the distance the wall moved.

**Drags are one undo step.** A drag writes to the model continuously so the plan updates
live, then records a single command on release. Recording each mouse move would bury real
edits under hundreds of one-pixel nudges. (§12.3)

**The file format has its own classes.** `ProjectFile` translates through DTOs rather than
serialising the domain objects, so refactoring `Wall` cannot silently break every saved
project. Enums are stored as text and colours as hex; unknown values fall back instead of
failing the load. (§12.3)
