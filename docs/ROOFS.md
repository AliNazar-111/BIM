# Roofs — how Revit does it, and where we are

A working analysis of Revit's roof system, taken apart piece by piece, set against what
BIMDesigner has today, and turned into build steps. It is written to build from: every
section ends in what we need to make, and the last part is the plan.

Sources: the Autodesk Revit 2026 help, *Roofs* chapter and the pages it links to (about fifty
of them: roof by footprint, by extrusion, by face, boundary line properties, instance and type
properties, slope arrows, eaves, joins, dormers, openings, fascia, gutters, soffits, shape
editing, sketching, troubleshooting). Everything below is our own summary of how the system
behaves; property and tool names are Revit's, so the two can be compared line for line.

Status marks: ✅ built · 🟡 partly built · ⬜ not built

---

## 1. The one difference that matters most

**In Revit a roof is drawn. In BIMDesigner it is clicked.**

Today our Roof tool asks for a click inside a space closed by walls, traces the room, and puts
a roof over it. That has three consequences, and all three are wrong for a roof:

1. **The roof sits inside the walls.** A room outline is the *inside* face of the walls, so
   the roof covers the room and stops short of the masonry. A real roof sits *on* the walls
   and runs past them.
2. **There is no overhang.** Eaves project beyond the wall face — typically 300–600 mm. We
   have no way to say so.
3. **The outline is not the user's.** A roof is very often not the shape of any one room: it
   covers several rooms, a porch, a carport, or is cut short at a parapet. Only a sketch can
   say that.

Revit never guesses the roof outline. It opens **sketch mode**: a temporary drawing state in
which you build the roof's boundary from lines — by picking walls (which places the line on
the wall's outer face, plus an overhang), by drawing lines, rectangles, polygons and arcs, or
by picking existing lines — then click **Finish** (✓) to turn the sketch into a roof or
**Cancel** (✗) to throw it away. The sketch is kept with the roof, and **Edit Footprint**
reopens it later.

Everything else in Revit's roof system hangs off that sketch. Our geometry engine is already
ahead of what we let the user tell it; the missing piece is the way of telling it.

---

## 2. Revit's roof model, part by part

### 2.1 What a roof is

- A **system family** (built in, not loaded), with **types** that carry the layered build-up
  and **instances** that carry the sketch, level and eave settings.
- Created three ways, plus one type variation:

| Method | What you give it | Where you draw it | Typical use |
| --- | --- | --- | --- |
| **Roof by Footprint** | A closed outline in plan; each line may define a slope | Plan or reflected ceiling plan | Almost every pitched and flat roof |
| **Roof by Extrusion** | An open profile in elevation, pushed along a direction | Elevation, section or 3D, on a work plane | Gambrel, barrel vault, any constant-section roof |
| **Roof by Face** | Non-vertical faces of a mass | Any view showing the mass | Conceptual massing to building |
| **Sloped Glazing** | A roof type whose body is a curtain system | Either method above | Glass roofs, conservatories |

- Openings in a footprint roof are **extra closed loops inside the outline**, drawn in the same
  sketch. An extruded roof gets openings from the separate **Vertical** opening tool.
- A roof **cannot cut doors or windows**.

### 2.2 Sketch mode (the heart of it)

Entering the tool puts Revit into a sketch state with its own contextual tab
("Modify | Create Roof Footprint"). While it is active, only sketch tools work.

**Mode panel** — **Finish** ✓ and **Cancel** ✗. Finish validates the sketch; an invalid one
(an open loop, crossing lines, overlapping loops) is refused with a message saying why, and the
user stays in the sketch to fix it.

**Draw panel** — two kinds of thing can be drawn:

- **Boundary Line**, with the usual shapes: Line, Rectangle, Inscribed/Circumscribed Polygon,
  Circle, Start-End-Radius Arc, Centre-Ends Arc, Tangent Arc, Fillet Arc, **Pick Lines**, and
  — for roofs — **Pick Walls**.
- **Slope Arrow** (see 2.5).

**Options Bar** while drawing:

| Option | Meaning |
| --- | --- |
| **Defines slope** | New lines are slope-defining (on by default for Pick Walls) |
| **Overhang** | With Pick Walls: how far the line sits outside the wall |
| **Extend to wall core** | Measure the overhang from the wall's core face instead of its finish face |
| **Chain** | Each line starts where the last ended |
| **Offset** | Draw the line this far from where the cursor is (or from the picked line) |
| **Radius** | Preset radius for arcs and filleted corners |
| **Lock** | With Pick Lines: keep the sketch line locked to what it was picked from |

**Modify panel inside the sketch** — the ordinary editing tools work on sketch lines: Align,
Offset, Mirror, Move, Copy, Rotate, Trim/Extend (to corner, single, multiple), **Split
Element**, Array, Scale. Trim to corner is how a picked set of wall lines is closed into a loop.

**Tools panel** — **Align Eaves** (2.6).

**Shift** constrains lines to horizontal/vertical and arcs to multiples of 45°/90°.
**Tab** over walls picks a whole chain of connected walls in one click.

**Pick Walls, exactly:**

- The line lands on the **outer face of the wall** (or its core face, with Extend to wall
  core), moved outward by the **Overhang**.
- The line stays **associated with the wall**: move the wall and the roof edge follows.
- Picking from the *inside* of a wall flips which face it goes to; the side of the wall the
  cursor is on decides.
- Lines are slope-defining by default, which is why picking the four walls of a box gives a
  hip roof straight away.

### 2.3 Boundary line properties (one set per sketch line)

Selecting a sketch line shows its own properties:

| Property | Meaning | Notes |
| --- | --- | --- |
| **Defines Roof Slope** | This edge is an eave the roof rises from | A small triangle (△) marks it in the sketch |
| **Slope** | The pitch at this edge | Also editable as a number beside the line in the canvas |
| **Overhang** | Line's distance outside its wall | Pick Walls lines only |
| **Plate Offset From Base** | Height, above the roof's base, where roof meets **wall** | Pick Walls lines; see the note below |
| **Offset From Roof Base** | Height of this edge above the roof base | Drawn (non-wall) slope lines |
| **Extend into wall (to core)** | Overhang measured from the core face | Pick Walls lines |
| **Length** | Read-only | |

**The plate height is at the wall, not at the eave.** This is easy to miss and it matters:
with a 500 mm overhang at 30°, the eave (the roof edge) sits 289 mm *below* the height the
roof meets the wall. Revit fixes the roof by where it bears on the wall — which is how it is
built — and lets the overhang drop below that. **Rafter or Truss** (instance property) decides
which face of the wall the plate height is measured on: the inside face for rafters, the
outside face for trusses.

### 2.4 How the slope settings make shapes

- No sloping line → **flat roof**.
- One sloping line → **shed** (mono-pitch).
- Two opposite sloping lines → **gable**; the other two edges become the gable ends.
- Three or four sloping lines → **hip** (three gives a hip at one end, gable at the other).
- Other outlines and combinations give valleys, crossed gables and so on automatically.
- A slope-defining **arc or circle** gives a **conical** roof; setting the arc's
  **Number of Full Segments** facets it instead.
- **Cutoff Level + Cutoff Offset** stop the roof flat at a height, so another roof can sit on
  top — the dutch gable, the mansard, a gablet.

### 2.5 Slope arrows

A second way to make a surface slope, for when "each edge has a pitch" does not describe it.

- Drawn inside the sketch from a **tail** (on a boundary line that is *not* slope-defining,
  unless at a corner) to a **head**.
- **Specify = Slope**: height at the tail plus a pitch.
- **Specify = Height at Tail**: a level and offset at the tail, and a level and offset at the
  head — the pitch follows.
- Used for: a flat roof falling to a drain; a slope running diagonally across a surface; a
  **four-sided gable** (all lines non-sloping, arrows from the split long sides); a **dormer
  with no side walls** (split one edge into three, arrows from the ends of the middle piece to
  its midpoint).
- The same arrow works on floors, ceilings, soffits and toposolids.

### 2.6 Eaves

- **Overhang** per line (2.3).
- **Align Eaves** (in the sketch): shows eave heights beside each eave; pick a reference eave,
  then the others to match — either by **Adjust Height** (changes plate/offset) or **Adjust
  Overhang** (changes the overhang so the eave lands at the same height). Drawn lines only
  offer Adjust Height.
- **Rafter Cut** (instance): how the roof edge is cut — **Plumb Cut** (vertical), **Two Cut –
  Plumb** (a vertical cut then a horizontal soffit cut), **Two Cut – Square** (square to the
  slope, then horizontal). **Fascia Depth** sets how deep the vertical part is for the
  two-cut forms, between zero and the roof's thickness.

### 2.7 Roof instance properties

| Group | Property | Footprint | Extrusion |
| --- | --- | --- | --- |
| Constraints | **Base Level** | ✓ | ✓ |
| | **Base Offset From Level** | ✓ | |
| | **Room Bounding** | ✓ | ✓ |
| | **Related to Mass** (read-only) | ✓ | ✓ |
| | **Cutoff Level**, **Cutoff Offset** | ✓ | |
| | **Work Plane** | | ✓ |
| | **Extrusion Start**, **Extrusion End** | | ✓ |
| | **Reference Level**, **Level Offset** | | ✓ |
| Construction | **Rafter Cut** | ✓ | ✓ |
| | **Fascia Depth** | ✓ | ✓ |
| | **Rafter or Truss** | ✓ (Pick Walls roofs) | |
| | **Maximum Ridge Height** (read-only) | ✓ | |
| Dimensions | **Slope** — sets every slope-defining line at once; blank if none | ✓ | |
| | **Thickness** (read-only unless shape-edited with a variable layer) | ✓ | ✓ |
| | **Volume**, **Area** (read-only) | ✓ | ✓ |
| Identity / Phasing | Comments, Mark, Phase Created, Phase Demolished | ✓ | ✓ |

### 2.8 Roof type properties

- **Structure** → **Edit Assembly** dialog: the layer build-up (function, material,
  thickness, wraps, *variable* layer for tapered insulation), with a preview.
- **Default Thickness** (read-only, the sum of the layers).
- **Coarse Scale Fill Pattern / Colour**.
- Identity: Keynote, Model, Manufacturer, Type Comments, URL, Description, Assembly
  Code/Description, Type Mark, Cost.
- Analytical: U value, R value, thermal mass, absorptance, roughness.

### 2.9 After the roof exists: the Modify | Roofs tab

Selecting a roof gives:

- **Edit Footprint** (or **Edit Profile** for an extrusion) — back into the sketch.
- **Shape handles** in elevation/3D to drag an edge.
- **Openings panel**: **By Face** (square to a roof face), **Vertical** (plumb, for a
  chimney or shaft), **Dormer** (cut the opening a dormer needs, by picking the dormer's
  walls and roof edges).
- **Shape Editing panel** (flat roofs only, not attached to another roof): **Modify Sub
  Elements**, **Add Point**, **Add Split Line**, **Pick Supports**, **Reset Shape** — for
  drainage falls and tapered insulation.
- **Join/Unjoin Roof** (on the Modify tab, Geometry panel): extends one roof's edge until it
  meets the face of another roof or a wall — how a dormer roof or a lower wing is married to
  the main roof.
- Walls are attached *from the wall side*: select walls → **Attach Top/Base** → pick the roof.
  The wall's top then follows the underside of the roof, **which is what fills a gable end**.
  Detach, or Detach All, undoes it.
- **Align** on ridges in elevation/3D, via a named reference plane set as the work plane.

### 2.10 Roof by extrusion

- Open an **elevation, section or 3D** view; start the tool; choose the **work plane** (a
  named reference plane, or pick a wall face or plane).
- Choose a **Reference Level** (defaults to the highest level) and an **Offset**; a reference
  plane is placed there to sketch against.
- Sketch the profile as an **open** chain of lines and arcs — no vertical lines, and no part
  of the profile may lie under another ("must face upward").
- Finish. The depth comes from **Extrusion Start / End** (by default the length of what it
  covers); drag the ends in plan or set them as properties.
- It can be **rehosted** to another work plane afterwards. Openings use the **Vertical** tool.

### 2.11 Dormers

Two ways:

1. **No side walls**: in the main roof's sketch, split an edge into three, make the middle
   piece non-sloping, draw two slope arrows from its ends to its midpoint.
2. **With walls**: build the dormer's walls and roof, **Join Roof** the dormer roof to the
   main roof, then **Dormer Opening** and pick the dormer walls, the joined roof and the edges
   to cut the hole in the main roof.

### 2.12 Roof edge elements: fascia, gutter, soffit

- **Fascia** and **Gutter** are sweeps: a profile run along picked edges of roofs, soffits,
  fascias or model lines. Consecutive picks are one continuous element that mitres at corners;
  **Restart** begins a separate one. Instance: vertical and horizontal profile offset, angle,
  length. Type: profile, material, identity.
- **Soffit** is a sketched element of its own, like a small floor on the underside of the
  eaves: sketch by **Pick Roof Edges** and **Pick Walls** (outer face), trim to a closed loop,
  finish. It stays associated with roof and walls, and can slope by line properties or a slope
  arrow. Instance: level, height offset, room bounding, slope, perimeter, area, volume.

### 2.13 Plan display

A roof above the plan's cut plane is shown in plan with its outline and its ridge, hip and
valley lines; the part below the cut is drawn cut. Overhangs appear outside the walls. The
sketch's slope symbols only show while editing the sketch.

---

## 3. Where BIMDesigner is

### 3.1 What has been built, in the order it was built

| Step | What | Status |
| --- | --- | --- |
| 1 | Flat roof as a layered slab, outline picked from an enclosed space | ✅ |
| 2 | `Roof` split from slabs; one `RoofEdge` per boundary edge (defines slope, pitch, plate offset) | ✅ |
| 3 | Geometry engine: faces from the lowest of the eave planes, each held to its own inward side and to the wedge between its corners — hips, ridges and valleys fall out of it | ✅ |
| 4 | Build-up measured square to the slope, so faces meet flush at ridges | ✅ |
| 5 | 3D mesh per face and per layer | ✅ |
| 6 | Plan: ridge, hip and valley lines; slope arrows on sloping edges while selected | ✅ |
| 7 | Section: each face cut as a true sloping parallelogram | ✅ |
| 8 | Properties: Roof Shape (Hip/Gable/Shed/Flat), Slope (all edges), per-edge **Slope at Edge N** and **Eave Offset at Edge N**, **Cutoff Level** and **Cutoff Offset**, **Rafter Cut** and **Fascia Depth**, ridge height, sloping area | ✅ |
| 9 | Click an edge of a selected roof to turn its slope on or off | ✅ |
| 10 | Options bar for the Roof tool: Shape and Pitch | ✅ |
| 11 | Material takeoff by the sloping surface, not the footprint | ✅ |
| 12 | IFC: `IfcRoof` with `PredefinedType`; pitched roofs as an assembly of one `IfcSlab` per face | ✅ |
| 13 | Save/load of edges and cutoff; old files read back as flat roofs | ✅ |
| 14 | Tests for the ten common shapes (shed, gable, catslide, clerestory, hip, half-hip, dutch gable, cross-gable, butterfly; gambrel shown to need extrusion) | ✅ |
| 15 | Polygon engine fixes found on the way: coincident edges, partly-overlapping edges, false crossings between near-parallel edges | ✅ |

### 3.2 Feature by feature against Revit

| Revit feature | Status | What we have / what is missing |
| --- | --- | --- |
| **Sketch mode** (enter, draw, Finish ✓ / Cancel ✗, validation) | ✅ | Roof tool opens a sketch with its own tab; Finish checks it (open ends, crossings, extra loops) and marks the lines at fault in red; Cancel leaves nothing; sketch undo; Esc stops a line; switching tools asks first |
| **Pick Walls** (outer face / core, overhang, follows the wall) | ✅ | Side of the wall from the cursor; overhang; Extend to wall core; TAB picks the whole chain; corners close themselves; edges follow their walls after any change, undo included. Straight walls only for now |
| **Draw boundary lines** (line, rectangle, polygon, circle, arcs, fillet, pick lines, offset, chain) | ✅ | Line (chained, Shift for level/plumb, snaps to sketch ends), Rectangle, Polygon, Circle, Arc (start, end, through a point), Pick Lines (floor, ceiling and roof edges, wall faces, grid lines) with Offset. Not yet: fillet and tangent arcs, Lock on picked lines |
| Edit sketch lines (trim/extend, split, move, align, mirror) | 🟡 | Select, delete, Split (arcs split into arcs), Offset (move or copy; neighbours follow; arcs offset round their centre), Trim/Extend to Corner. Not yet: dragging ends, move, mirror, align |
| Inner loops = openings | ✅ | A rectangle, polygon, circle or any closed loop drawn inside the outline is a hole cut straight down through the roof - skylight, chimney, light well; its lines stop sloping, it reopens with Edit Footprint (circles as circles), and 3D, section, plan, sloping area and IFC all take it out. A loop outside the outline is refused |
| **Edit Footprint** to reopen a roof | ✅ | Ribbon button or double-click; the roof is hidden while its sketch is open; Finish is one undoable step |
| Defines Roof Slope per line | ✅ | Edge click + properties |
| Slope per line, editable beside the line | ✅ | △ marker and pitch beside each line; click △ to toggle slope; with Select Lines, click the pitch to type a new one |
| Overhang per line | ✅ | Options bar, for new picks and for selected lines |
| Plate Offset From Base (at the wall) | ✅ | The roof bears on the wall face at its plate height; the overhang drops below it by overhang × tan(pitch); Rafter or Truss chooses the face |
| Extend into wall (to core) | ✅ | |
| Base Level + Base Offset From Level | ✅ | A roof stands on its base, as in Revit; a new roof is based at the top of the walls it was picked from, or on the storey above when drawn. Older files are migrated so nothing moves |
| Cutoff Level + Offset | ✅ | None, Roof Base, or any level, plus an offset |
| Rafter Cut, Fascia Depth | ✅ | Plumb Cut, Two Cut Plumb, Two Cut Square; fascia depth up to the roof's thickness. 3D and section; IFC still exports each face as a plain slab |
| Rafter or Truss | ✅ | Truss (default) bears on the wall's outside face, Rafter on its inside face - on the core's faces with Extend to wall core. Shown only for roofs picked from walls |
| Maximum Ridge Height | ✅ | Shown as ridge height above base |
| Slope (roof-wide) | ✅ | |
| Thickness, Volume, Area | ✅ | Volume and area use the sloping surface |
| Room Bounding | ⬜ | Roofs don't bound rooms' upper limits |
| Type: Structure / Edit Assembly | ✅ | Shared with floors and walls |
| Type: coarse fill, identity, analytical | 🟡 | Most slab type fields exist; roof-specific analytical fields not checked |
| Conical roof from a sloping arc | ✅ | A sloping circle is a cone, a sloping arc part of one: built from pieces of at most 10°, drawn without a line down every join and shaded smooth in 3D. Not yet: Number of Full Segments |
| **Slope arrows** | ✅ | Slope or Height at Tail; tail snaps onto a line, head onto a line or the grid; drawn and selected in the sketch; saved with the roof. Heights are measured from the roof's base (or from the eave an arrow starts beside), not from a chosen level |
| **Align Eaves** | ✅ | Eave heights shown beside each line; click the reference, then the eaves to match, by Adjust Height (plate) or Adjust Overhang (picked lines) |
| **Roof by Extrusion** | ✅ | Placed in plan with three clicks, the profile drawn square-on in its own window from a gable, gambrel, barrel vault, shed, butterfly or flat start; Edit Profile or a double-click reopens it. Clicks lock onto wall corners; arcs are true arcs, smooth in 3D; walls under it are offered attachment; the ends are cut plumb. See R7 |
| Roof by Face | ⬜ | No masses |
| Sloped Glazing | ⬜ | Curtain system exists for walls only |
| **Attach wall tops to a pitched roof** (gable ends) | ✅ | The wall's top follows the roof's underside along its length: gables fill to the ridge, eave walls stay at the plate; solid, plan, section, openings and quantities follow. Finishing a roof from picked walls asks to attach them. Curved, leaning and stacked walls keep a level top |
| Join/Unjoin Roof | ✅ | Click the edge to carry back, then the roof it runs into: carried back until buried in it and trimmed there - whole where clear above, standing on its top where partly in it, gone beneath. Plan shows it only where it is above; click a joined roof's edge to unjoin |
| Openings: Vertical, By Face | 🟡 | Vertical openings are drawn in the roof's own sketch as inner loops; no separate Vertical or By Face (square to the slope) tool yet |
| Dormer (slope-arrow and dormer-opening methods) | ✅ | Slope-arrow dormer: split the eave, clear the middle's slope, two arrows. With walls: walls standing on the roof (Attach Base finds it; their base follows its slope and they end where they are buried), a gable roof on them joined to the main roof, then Dormer Opening cuts the main roof between the walls' inside faces, back to where the dormer's underside meets it |
| **Dormer tool** (one click; not in Revit) | ✅ | Click on a roof's slope: front and side walls standing on the roof, a Gable, Shed or Hip roof picked off them, joined into the main roof, and the main roof opened under it - one undo step. Width, Height, Slope and Overhang on the options bar; the height is lowered to what fits under the ridge, and a roof too low for any dormer says so. Doors and windows in its walls - or in any wall whose outline is lower than they are - are kept inside it, live: brought down or made shorter where they would come out of the top, back to their own size when there is room. A new window is sized to the wall it goes in, keeping its proportions and clear of the corners. The dormer selects as one, like a group: TAB or a second click in 3D reaches a part |
| Fascia, Gutter | ⬜ | Wall sweeps exist; could be reused along roof edges |
| Soffit | ⬜ | |
| Shape editing (flat roofs: points, split lines, supports) | ⬜ | |
| Plan display of ridges, hips, valleys | ✅ | |
| Section through pitched roof | ✅ | |
| IFC `IfcRoof` | ✅ | |
| Schedules / takeoff by sloping area | ✅ | |

---

## 4. The build plan

Ordered so that each step leaves something usable, and so the thing the user noticed — "in
Revit you pick walls and points, and the roof sits on the walls" — comes first.

### R1 — Sketch mode ✅

A general sketch state in the plan view, built once and reused for floors, ceilings, soffits
and openings later.

- Starting the Roof tool enters **Modify | Create Roof Footprint** instead of placing
  anything. A green contextual tab holds: Finish ✓, Cancel ✗, the Draw tools, the edit tools.
- The sketch is a list of **sketch lines** living outside the model until Finish: each a line
  or an arc, with its own properties. They are drawn in the sketch colour (magenta in Revit),
  selectable, draggable by their ends, deletable.
- **Finish** checks the sketch: closed loops only, no lines crossing, no loops overlapping; one
  outer loop and any number of inner loops (openings). If invalid, the offending lines are
  highlighted and the message says what is wrong; the sketch stays open.
- **Cancel** / Esc throws the sketch away.
- Undo inside the sketch undoes sketch edits; Finish is one undoable step in the model.
- **Edit Footprint** on a selected roof reopens its sketch.
- Data: `Roof` stores its sketch — loops of sketch lines — and the footprint the geometry uses
  is derived from it. Today's `Boundary` + `Edges` becomes the single-loop, straight-line case,
  so existing roofs and files convert without loss.

*Done when:* a roof can be drawn with lines in a sketch, finished, reopened, changed and
finished again, and Cancel leaves nothing behind.

### R2 — Pick Walls and the draw tools ✅ (fillet and tangent arcs, and dragging line ends, still to come)

- **Pick Walls**: hover a wall to preview the line on its outer face (or inner face, from the
  cursor's side), click to add. **Tab** takes the whole chain of joined walls. Options bar:
  **Defines slope** (on), **Overhang** (default 0; typical 300–600), **Extend to wall core**.
- The line is **associated** with the wall: it stores the wall and its overhang rather than
  coordinates, and is re-derived when the wall moves or changes type.
- Corners between picked lines are **trimmed/extended automatically** so a box of picked
  walls closes into a loop without extra work.
- **Draw**: Line (chained), Rectangle, Polygon, Circle, arcs, Pick Lines, with **Offset** and
  Shift constraints — reusing the wall drawing code.
- **Trim/Extend** and **Split** on sketch lines.
- **Base level**: the roof sits on the level of the plan it is sketched in, at **Base Offset
  From Level**. Starting it on the lowest level offers to move it to the level above, as
  Revit does, since that is almost always the mistake.

*Done when:* picking the four walls of a box with a 500 mm overhang gives a hip roof sitting
on the walls and projecting 500 mm past them, and moving a wall moves the roof edge.

### R3 — Line properties and canvas controls ✅

- Selecting a sketch line shows its properties: Defines Roof Slope, Slope, Overhang, Plate
  Offset From Base, Offset From Roof Base, Extend into wall (to core), Length.
- **△** beside each slope-defining line; clicking it toggles the slope. The pitch shown as a
  number beside the line, **click to type a new one**.
- **Plate height at the wall**: for a picked line, the roof plane passes through the plate
  height on the wall face and continues down over the overhang. **Rafter or Truss** chooses
  which face. (The engine change: each eave plane is anchored at the wall line, not the edge.)
- **Align Eaves**: heights shown at each eave; pick a reference then others; Adjust Height or
  Adjust Overhang.

*Done when:* a 30° roof with a 500 mm overhang meets the wall at the plate height and its
eave is 289 mm lower, as built.

### R4 — Walls that meet a pitched roof ✅ (straight, upright walls; curved, leaning and stacked walls keep a level top)

- **Attach Top** to a pitched roof makes the wall's top follow the underside of the roof
  along its length — the gable end fills in, and a wall under a hip stops at the plate.
- This needs a wall top that varies along the wall, carried through the wall's 3D solid,
  sections, plan cut, openings and joins. It is the most visible gap after sketch mode.

*Done when:* the four walls of a gabled house, attached to the roof, close the gable ends
with no gap, in 3D and in section.

### R5 — Eave construction ✅

- **Rafter Cut**: Plumb Cut, Two Cut – Plumb, Two Cut – Square; **Fascia Depth**. The roof
  edge solid is cut accordingly in 3D and section.
- **Cutoff Level** as a level plus offset (we have the offset form).

### R6 — Slope arrows ✅ (plus Split for sketch lines)

- Slope Arrow in the sketch Draw panel; tail on a non-sloping line; **Specify** = Slope or
  Height at Tail (with levels and offsets at tail and head).
- Engine: an arrow defines a plane directly (point, direction, pitch), competing with the
  eave planes the same way they compete with each other.
- Gives the four-sided gable, the wall-less dormer, falls on flat roofs.

### R7 — Roof by extrusion ✅

- Drawn in a section or elevation on a work plane (a wall face or reference plane): open
  profile of lines and arcs, reference level and offset, extrusion start and end.
- Validation: no vertical lines, no part of the profile under another.
- Geometry: each profile segment swept along the extrusion direction, build-up square to each
  segment; plan, section, 3D, IFC.
- Gives the **gambrel**, the **barrel vault**, the saltbox, any constant-section roof.

What was built, and where it differs from Revit:

- **Placed from the plan, drawn square-on.** Revit sets a work plane and draws the profile in
  an elevation. Here the **Roof by Extrusion** tool takes three clicks in plan — the two ends
  of the line the profile is drawn on (the width it spans), then how far the roof runs back —
  and opens a profile window over that width. The work plane is the vertical plane through
  the first line; there is no separate reference plane to name or rehost.
- **Clicks lock onto the walls.** Each click snaps to a wall's corner as drawn - the
  **Outside corner** of the building where two walls meet, which is where a roof's edge
  usually goes, or the **Inside corner** - to the end of a wall's line, or to a grid crossing,
  with a ring and the name of what it found. Locked onto a corner, the roof runs exactly to it;
  otherwise its depth rounds to the snap step.
- **Profile window.** Start from Gable, Gambrel, Barrel Vault, Shed, Butterfly or Flat, set
  out by **Rise** and **Overhang**; then drag points (10 mm snap), double-click a line to add
  one, right-click a point to take it out. Each line shows its pitch; the building's width is
  shaded along the base and the project's levels are dashed across it. A line that stands
  upright or runs back under the one before turns red, and Finish is refused with the reason.
- **Arcs.** Dragging the diamond in the middle of any line bends it into an arc (Revit's
  Start-End-Radius arc); dragged back to the line it is straight again. An arc is stored as an
  arc - its two ends and how far it bows (its sagitta) - and the vault preset is a single one.
  The roof is built from it in pieces of at most 3°, but it is one surface: no line is drawn
  along the joins in plan or 3D, and the 3D shades it smooth, creasing only at the profile's
  real corners. Splitting an arc with a new point keeps its curve; taking the point out again
  gives the arc back. An arc reaching a half circle is refused - it would stand upright where
  it springs.
- **Base.** The window's **Base** is the roof's Base Offset From Level. It starts at the tops
  of the walls the rectangle covers (as a roof picked from walls does), else the next level up.
- **The profile is the underside.** As a footprint roof stands on its base, the profile is
  where the roof's underside is, and its build-up stands on it, each layer offset square to
  its line and mitred at every break, so the top does not step where the pitch changes.
- **Walls under it.** Finishing the roof asks whether to attach the walls it covers, as a
  roof picked from walls does; yes closes the gable ends up to the arch or the ridge. A wall
  only partly under the roof - one whose centre line the roof's edge stops on - takes its
  height from the faces the roof is over, so its outer half does not hold it down.
- **Ends are plumb.** The extrusion's two ends are cut straight down; **Extrusion Start** and
  **Extrusion End** are instance properties, validated so the end stays past the start.
- **Edit Profile** on the context tab, or a double-click on the roof, reopens the window over
  the walls the roof covers; the profile and base change as one undo step. Moving, mirroring,
  copying, saving, section, 3D, attached walls and quantities all carry the extrusion; IFC
  exports it as `IfcRoof` with `GAMBREL_ROOF`, `BARREL_ROOF` and so on, one slab per face.
- Not done: openings by the Vertical tool (with R8), joining an extruded roof to another roof
  (R8), arcs drawn by three points or a centre (only bent from a line).

### R8 — Joins, openings, dormers 🟡 (Join/Unjoin Roof, Dormer Opening, openings drawn in the outline and a one-click Dormer tool done; By Face openings to come)

- **Join/Unjoin Roof**: extend a roof's edge to meet another roof's face or a wall.
- **Openings**: Vertical and By Face, as sketches.
- **Dormer Opening**: pick the dormer's walls and joined roof to cut the main roof.

### R9 — Fascia, gutter, soffit ⬜

- Fascia and gutter as profile sweeps along picked roof edges (reuse the wall sweep engine),
  continuous and mitred across picks, Restart to break; offsets and angle.
- Soffit as a sketched element (Pick Roof Edges + Pick Walls), with its own type and
  properties.

### R10 — The rest ⬜

- Shape editing of flat roofs (points, split lines, supports, reset) for drainage falls.
- Conical roofs from slope-defining arcs, and faceting by segment count.
- Sloped glazing (curtain roof).
- Roof by face (needs masses).
- Room bounding by roofs.

---

## 5. Notes for building it

- **Keep the engine.** The face builder (lowest plane, own side, corner wedges) already takes
  any set of planes. Pick Walls, plate height at the wall, slope arrows and extrusion all end
  up as planes or bands of planes; none of them needs a different engine.
- **The sketch is the roof.** Store the sketch, derive the footprint. That is what makes Edit
  Footprint, wall association and inner-loop openings possible, and it is how Revit behaves:
  a roof never forgets how it was drawn.
- **One sketch mode, many elements.** Floors, ceilings, soffits, openings and eventually
  railings and slab edges all use the same draw–finish–cancel cycle. Build R1 generally.
- **Association over coordinates.** A picked-wall line remembers its wall and overhang and
  recomputes; it is the difference between a roof that follows a design change and one that
  has to be redrawn.
- **Plate height at the wall, not at the eave.** Getting this wrong puts every overhanging roof
  at the wrong height by `overhang × tan(pitch)`.
- **Validation messages must point.** Revit highlights the lines that break a sketch. A sketch
  that silently refuses to finish is the most frustrating thing a roof tool can do.
