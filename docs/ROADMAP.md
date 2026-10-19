# BIMDesigner Roadmap

Tracks [BIM-Feature-Specification.md](BIM-Feature-Specification.md) against what is built.
Spec section references are in brackets.

Status: ✅ done · 🔨 in progress · ⬜ not started

*Brought up to date on 19 October 2026. What comes next is set out in rounds at the end of
the phases.*

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
| 1.5b | Room separation lines for open plans, colour-fill legends by department or occupancy | §3.7 | ⬜ |
| **1.6** | **Floors, ceilings and flat roofs** — layered build-ups sharing the wall layer system, outline picked from an enclosed space, quantities and persistence | §3.2 §3.3 §3.4 | ✅ |
| 1.6b | Free-sketched slab outlines, slab openings and shafts, slab edges, span direction - **shafts** cutting floors, ceilings and roofs, holes of any shape in roofs, floors dividing the walls through them and stopping at the walls they meet, floors and ceilings sketched with holes of their own (Edit Boundary), span direction, and slab edges (thickened edges, edge beams, kerbs) run along a floor's edges done | §3.2 | ✅ |
| **1.6c** | **Pitched roofs** — slope per edge, pitch and eave height per edge, cutoff, hip/gable/shed/dutch/cross-gable shapes, ridge/hip/valley generation, plan/section/3D, takeoff by sloping area, `IfcRoof`. Built in steps R1–R10 of [ROOFS.md](ROOFS.md): done so far are sketch mode with Pick Walls and overhangs, arcs and cones, walls attaching to the slope, eave cuts, slope arrows, roof by extrusion (gambrel, barrel vault), Join Roof, openings and dormer openings, the one-click Dormer tool, fascias, gutters, soffits and downpipes, roof windows, chimneys, shafts, and flat roofs falling to their drains; still to come are By Face openings, joining an extruded roof to another, flat-roof shape editing, and the last sketch tools listed in ROOFS.md (tangent arcs, locked lines) | §3.3 | 🔨 |
| **1.7** | **Levels and grids** — plan shows one storey, level switching, gridlines with bubbles, snapping to grid crossings | §2.3 | ✅ |
| **1.7b** | **Levels** — add, rename, move and delete storeys (with cascade and one-step undo), underlay of the storey below | §2.3 §6.1 | ✅ |
| **1.8** | **Schedules and quantity takeoff** — tabular views, fields, filters, sorting, grouping with subtotals, bidirectional editing, material takeoff, CSV export | §6.4 | ✅ |
| 1.8b | User-defined schedules, calculated fields, conditional formatting, key schedules | §6.4 | ⬜ |
| **1.9** | **Annotation** — associative dimensions, tags that read a parameter, text notes with leaders | §6.3 | ✅ |
| 1.9b | Dimension styles, angular/radial dimensions, multi-segment strings, keynotes and a keynote database, revision clouds | §6.3 | ⬜ |
| **1.10a** | **Sections** — section markers on plan, live cut through walls, slabs and openings, levels as datums, what lies beyond the cut shown in elevation, selection shared with the plan | §6.1 §6.2 | ✅ |
| **1.10b** | **3D viewport** — walls as their real layers with openings cut through, slabs at their elevations, glazing and door leaves, orbit/pan/zoom, shaded / flat / X-ray styles, per-storey visibility, click to select | §6.1 §6.2 | ✅ |
| **1.10d** | **3D views** — named 3D views and cameras keeping their camera, style, section box and what they show; section box with handles, the cut solids closed; shaded, flat, X-ray, wireframe and hidden-line styles; perspective or orthographic; walking through the model at eye height; walkthroughs played frame by frame; 3D views on sheets as hidden-line or painted vector drawings | §6.1 §6.2 | ✅ |
| **1.10c** | **Elevations, callouts, templates** — the four exterior elevations and interior elevations cropped to their rooms, both on sheets; callouts of plans and sections cropped to their boxes at their own scale and detail, their tags quoting detail and sheet numbers; detail level kept by each view; view templates applied once or governing their views | §6.1 §6.2 | ✅ |
| **1.11a** | **Sheets** — paper sizes, title block read from the project, viewports at true drawing scales, drag to lay out, one renderer shared with the editors | §6.5 | ✅ |
| **1.11b** | **Publishing** — vector PDF export of a sheet or a whole set, printing at true paper size | §6.5 | ✅ |
| 1.11c | Sheet lists, revision schedules and clouds, issue history | §6.5 §7 | ⬜ |
| **1.12a** | **IFC4 export** — project/site/building/storeys, walls, slabs, ceilings, doors, windows with real openings, spaces, material layer sets, standard and custom property sets, stable identifiers | §8 | ✅ |
| 1.12b | IFC import, DXF export of plans and sheets, IFC2x3 fallback, COBie | §8 | ⬜ |
| 1.13 | **Release packaging** — installer, signed with a trusted code-signing certificate so Smart App Control and SmartScreen accept it; file association for .bimx | §12 | ⬜ |
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
| 2.6 | View templates, filters, keynotes, revisions - **view templates** done (Round 3); filters, keynotes and revisions to come | §6.2 §7 | 🔨 |
| 2.7 | Design options and phasing - **Phase Created** and **Phase Demolished** on every element done; phase views, phase filters and design options to come | §7 | 🔨 |
| 2.8 | Classification systems, shared parameters | §2.1 §7 | ⬜ |
| 2.9 | Material library and rendering basics - materials with surface colours, paints and finishes done; textures and rendering to come | §13.1 | 🔨 |
| 2.10 | IFC import, DWG link | §8 | ⬜ |

## Phase 3 — Collaboration ⬜
Worksharing, model linking, copy/monitor, cloud CDE, web/mobile viewer, issues/BCF,
version compare, permissions. §7 §12.1

## Phase 4 — Structure & MEP 🔨
Beams of steel, timber, glulam and concrete and joist layouts are built, measured against
stair headroom and doorways and checked for their span. Still to come: structural columns,
foundations, rebar, analytical model; ducts, pipes, electrical, systems, sizing, connectors,
spaces/zones, gbXML. §4 §5

## Phase 5 — Coordination & nD 🔨
**Rule checking** is built for homes: every wall, floor, roof, door, window, room, stair, ramp,
railing, gutter, soffit, chimney, fitting and the dwelling as a whole is checked against the
project's standard - Approved Documents B, E, F, H, J, K, L and M (M4(2) and M4(3)), the
Nationally Described Space Standard and the British Standards they point to, or the IRC and
the ADA - shown in each element's **Regulations**, room by room, and drawn red in plan.
Thermal U-values are worked from each build-up's layers (BS EN ISO 6946, ISO 13370). Still to
come: clash detection, quantity takeoff/5D, 4D scheduling, energy/daylight analysis,
API/plugins, visual scripting. §9

## Phase 6 — Handover & FM ⬜
COBie, asset register, O&M documents, maintenance, digital twin/IoT, dashboards. §11

---

## Next: the rounds

Work goes on in rounds. Each takes one area and finishes it: how the real thing works is
looked up first, then it is built, tested and written up in the README.

| Round | What | Status |
| --- | --- | --- |
| **1** | **Finish the half-built tools** — floors and ceilings sketched with holes and Edit Boundary, span direction, slab edges; sketch corners dragged, Move, Mirror, Align and Fillet Arc in every sketch; soffits sloping under verges, and soffits of any shape sketched as soffit-board ceilings; downpipes cut in section (already were). The rules left from the audit: space separation to the property line (Approved Document B 11.8, 11.17-11.18); party walls - 60 minutes, up to the roof (B 5.8, 5.11); a step-free approach where outside steps reach every door, approach paths' width, cross-fall and passing places, and a level, covered landing at the entrance (M4(2) 2.6-2.10, 2.20; M4(3) 3.9, 3.22); 42 in stair guards outside homes under the ADA (IBC 1015.3); air at the ridge of a steep or wide cold roof (BS 5250; IRC R806.2); distributors on downpipes onto a lower roof (Approved Document H 1.9); gas flue terminals (Approved Document J Diagram 34; IFGC 503.6.5). Already met by how they are built: no gap under a stair's bottom rail, an ADA switchback's handrail continuous round its landing | ✅ |
| **2** | **Massing, then building by face** — masses as a box, cylinder or any outline, flat, tapered or to a point, or topped with a gable, hip or shed; mass floors, gross floor area, volume and surface area; Wall by Face (leaning on a tapered mass, shaped to a gable, halved round a cylinder), Curtain System by Face on upright sides, Floor by Face at the mass floors, Roof by Face (flat, pitched, pyramid or cone, gable, hip, shed); Update to Face bringing them back to a changed mass in one step; roof openings by face, cut square to the slope through the build-up. Not done: sloped glazing - a curtain system on a sloping face | ✅ |
| **3** | **Views** — 3D views kept by name, with cameras; a section box that closes what it cuts; wireframe and hidden-line styles, perspective or orthographic; walking through the model and walkthroughs; interior elevations cropped to their rooms; callouts of plans and sections; detail level by view; view templates applied once or governing views; 3D views and elevations on sheets as vector drawings, each view numbered on its sheet. Not done: exporting a walkthrough as a video | ✅ |
| **4** | **Drawings and documents** — dimension styles, angular and radial dimensions, multi-segment strings, keynotes, revision clouds, sheet lists, revision schedules, schedules of your own with calculated fields and conditional formatting | ⬜ |
| **5** | **Files and release** — DXF export, IFC import, IFC2x3, a signed installer opening .bimx files | ⬜ |

Left until those are done: Phase 3's collaboration, the rest of Phase 4's structure and MEP,
clash checking and 4D/5D, and Phase 6's handover.

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
