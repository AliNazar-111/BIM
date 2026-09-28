# BIMDesigner

A Windows BIM authoring application. C# / .NET 10 / WPF, built and run from VS Code.

Scope is defined by [docs/BIM-Feature-Specification.md](docs/BIM-Feature-Specification.md).
Progress against it is tracked in [docs/ROADMAP.md](docs/ROADMAP.md).

## Running it

WPF has no visual designer in VS Code, so the loop is **edit → run → look**:

```powershell
dotnet run --project BIMDesigner.UI          # build and open the window
dotnet watch --project BIMDesigner.UI run    # same, but reloads when you save
dotnet test                                  # run the test suite
```

Or press **F5** in VS Code (uses `.vscode/launch.json`, with breakpoints).
Requires the C# Dev Kit extension for IntelliSense and debugging.

### Windows 11 and Smart App Control

If **Smart App Control** is on, Windows may refuse to load a freshly built
`BIMDesigner.Core.dll` or another of the project's own libraries. It judges programs by
reputation, and every build is new, unsigned code, so a build it let through yesterday can be
blocked today. The symptoms:

- the tests all fail with *"An Application Control policy has blocked this file"*
  (`0x800711C7`);
- the app shows **BIMDesigner could not start** and names the blocked file, rather than
  crashing.

To confirm it is this, look in Event Viewer under *Applications and Services Logs →
Microsoft → Windows → CodeIntegrity → Operational* for a Smart App Control block naming the
file.

On a machine used to build software, turn it off: **Windows Security → App & browser control
→ Smart App Control settings → Off**. On many Windows 11 versions it cannot be turned back on
without resetting Windows, so treat that as a decision for the machine, not for the build. On
a managed machine the block may come from your organisation's own policy instead; that has to
be allowed by whoever manages it.

People installing a released copy are not expected to do this. Released builds are to be
signed with a trusted code-signing certificate, which is what Smart App Control accepts
(see the roadmap).

## Projects

| Project | Purpose |
| --- | --- |
| `BIMDesigner.Core` | The BIM model: elements, types, parameters, geometry. No UI, no database, no file formats. |
| `BIMDesigner.Infrastructure` | File formats and interoperability: `.bimx` serialization, IFC4 export via Xbim.Essentials. |
| `BIMDesigner.UI` | WPF shell and the 2D plan view. Later: section, elevation and 3D views. |
| `BIMDesigner.Tests` | xUnit tests over Core. |

`Core` references no WPF, which is what will let the same model back a web viewer or a
headless IFC exporter later.

## How the model is put together

```
BimDocument                    one project
├── Levels                     horizontal datums (§2.3)
├── Materials                  physical / graphical / identity data (§2.1)
├── ElementTypes               shared by every instance of a type
│   └── WallType               layers, fire rating, U-value, cost (§13.1 type params)
│       └── CompoundStructure  ordered exterior face → interior face
└── Elements                   everything placed in the project
    └── Wall                   where, how tall, which level (§13.1 instance params)
```

**Type vs. instance** is the difference between BIM and CAD. Layers, fire rating and cost
live on `WallType` and are shared: edit one and every wall of that type changes. Height,
offsets and mark live on the `Wall` and are its own.

**Quantities are never stored.** `Length`, `Area` and `Volume` are computed from the wall
and its type on every read, so they cannot drift out of date when either changes.

**Millimetres everywhere.** Lengths are mm, areas mm², volumes mm³, all as `double`.
Conversion happens only when formatting for display, in `BIMDesigner.Core/Units.cs`.

**Walls join by touching.** Two walls mitre because their endpoints coincide — there is no
stored join record to maintain or let go stale. `WallJoins` pairs the two walls' faces by
which side of the corner they fall on, so the mitre is the same whichever way either wall
was drawn. Snapping to existing endpoints is what makes the corners land exactly.

**A wall that runs into the side of another stops at its face.** It is cut back so it butts
against it, rather than carrying on to the other wall's centreline and pushing half a wall's
width inside it. The wall being run into is never altered — it simply passes through.

A run that has been **split** counts as one wall for this, which is what makes splitting a
wall leave a junction looking exactly as it did. And a wall whose neighbour carries straight on
is cut square *before* anything else is considered, even when other walls meet at the same
point: without that rule the two halves of a split wall each mitre with the stem between them
and meet as a notch.

An end is **butted square** rather than mitred in these cases, because in each of them a
mitre is either meaningless or ambiguous:

- the end is free, or a neighbour carries straight on;
- **three or more walls meet with no run through them** — no single mitre is right against
  two different neighbours, so picking one arbitrarily would just be wrong in a different way;
- the mitre would **reach too far** past the joint. As two walls approach parallel, their
  faces do too, and the mitre corner runs away — drawing the wall as a long tapering spike.
  This is the same mitre limit a vector-graphics renderer applies to a stroked corner. Two
  bounds apply, whichever is tighter:
  - **twice the wall's width**, which starts butting at about 29° between walls (the same
    cut-off as SVG's default mitre limit);
  - **half the shorter wall's length**, because a corner longer than the wall it belongs to
    is never right however wide the wall is.

  Showing a 25° corner butted is a small cosmetic compromise; letting a shallow corner
  through draws the wall as a spike half its own length, which is a broken drawing.

**Openings are positioned along their host, not in the project.** A door stores how far
along its wall it sits, never an X/Y point. Move the wall, drag its end, change its location
line, and every door in it follows without anything having to notice and update them. A
stored coordinate would fall out of the wall the first time the wall moved. (§2.5)

**Annotation reads the model; it does not repeat it.** A dimension holds references, not
coordinates, and measures them when it is drawn — so moving a wall moves the number. A tag
holds a field name, not text, and reads it when it is drawn — so renaming a door type
relabels every door on every sheet. Typed-in numbers and labels are how drawing sets come to
contradict the building they describe, and that contradiction is the most expensive kind of
error a set can carry onto a site. (§6.3)

A dimension whose ends only reference *points* is drawn in a different colour from one
attached to elements, because the reader should be able to tell which numbers will keep up
with the building and which will not.

**A section is a question too.** A section marker stores the cut line, the direction it looks
and how far it sees — and nothing else. The drawing is produced by cutting the model on every
repaint, so moving a wall moves it in the section immediately, and there is no regenerate step
to forget. Walls become vertical bands of their real layers, floors and roofs sit at their
true elevations, doors and windows leave gaps between their sill and head heights, and what
lies beyond the cut is drawn faded behind it. (§6.1)

Cutting lengthways along a wall would report metres of masonry as its thickness, so a wall
running within about 11° of the cut line is shown in elevation instead of being sliced.

**A sheet holds references, not pictures.** A viewport stores which view it shows, where it
sits on the paper and at what scale — never the drawing. The sheet asks the model for each
view on every repaint, through the same renderers the editors use, so a sheet is exactly as
current as the building is. Its size is derived too: extend a wall past the edge of the
building and the viewport grows to include it, rather than silently cropping it off the
drawing. (§6.5)

Millimetres on a sheet are millimetres of *paper* — the only place in the application where
they are not millimetres of building. The viewport is where the two meet, and the scale is the
conversion. Scale lives on the viewport rather than on the view, because the same plan is
legitimately 1:100 on a general arrangement and 1:20 on a detail. Fitting a view picks from
the scales that appear on a scale rule: a drawing at 1:87 cannot be measured.

The title block reads the project, so a project renamed once is renamed on every sheet, and
the scale it prints is the scale the viewports are actually at rather than one typed in and
forgotten.

**IFC is the handover format, and the one place we use a library.** File → Export IFC writes
IFC4: project, site, building and a storey per level; walls, floors and ceilings as
extruded solids; roofs as `IfcRoof`, a pitched one aggregating one slab per face; doors and
windows as real voids in their walls, with the door filling the
hole; rooms as spaces; and compound structures as material layer sets, so a brick-on-block
wall arrives as brick on block rather than an anonymous lump. Property sets carry both the
standard IFC ones and every parameter our own elements report. (§8)

The schema is handled by **Xbim.Essentials** (CDDL), referenced from `BIMDesigner.Infrastructure`
only — Core does not know IFC exists. That is the opposite of the decision made for PDF, on
purpose: a PDF of our drawings is a handful of operators, while IFC4 is hundreds of entity
types with strict attribute ordering, and the failure mode is a file that looks right to us and
that Revit refuses. Element ids become IFC GlobalIds, so exporting twice says the same thing
and the receiving end can tell an edited wall from a new one.

**PDFs are written, not printed.** `BIMDesigner.UI/Export/PdfWriter.cs` emits vector PDF by
walking the same WPF drawing the sheet view produces — no dependency, no printer driver, and
the same file every time. Printing to a PDF driver would have been less code but would have
made the export depend on what happens to be installed on the machine.

Every geometry is flattened to polylines before it is written, so curves, arcs and text
outlines all become the one thing the writer has to understand, and the whole thing fits in a
single file. Text goes out as outlines rather than embedded fonts: nothing has to travel with
the drawing and nothing can be substituted at the other end.

The viewport frames and the paper's drop shadow are **not** exported. Both are editor
furniture, and a dashed box printed around every drawing would be read as part of it.

**A schedule is a saved question, not an answer.** Nothing about the building is stored in
one: the rows are produced from the model whenever it is opened. A stored table would be a
snapshot of the building as it was when someone last remembered to refresh it, and a
schedule nobody trusts is worse than no schedule. (§6.4)

Its cells are the elements' own live parameters, which is what gives the bidirectional
editing §6.4 asks for: typing a fire rating into the table writes it to the wall, because
the cell *is* the wall's parameter. There is no second write path that could drift.

A numeric column is only totalled if the sum is a quantity. Adding up five wall heights
gives 15 m of nothing, and a schedule that prints a meaningless total teaches its reader to
distrust the ones that mean something.

**A slab stores its outline; a room does not.** This is the sharpest distinction in the
model so far, and it is deliberate. A room is a *space that exists because walls enclose
it*, so its shape is traced live and follows them. A floor is a *thing someone decided to
build*, so its outline is its own — it does not change shape or vanish because a wall moved,
any more than a poured slab would. Picking an enclosed space is offered as a quick way to
sketch that outline, not as a link to it. (§3.2)

Floors, ceilings and roofs share one `SlabType` built on the same `CompoundStructure`
as walls, so material takeoff, cost and U-values work identically for horizontal and
vertical construction with no second implementation to keep in step.

**A roof is its edges.** A roof by footprint stores one thing per edge of its outline:
whether the roof slopes up from it. That single setting is the whole shape — all four edges
of a rectangle sloping is a hip roof, two opposite ones a gable, one a shed, none a flat
roof — and everything else is worked out from it: the faces, the ridges, hips and valleys,
the height over any point, and the area of the sloping surface a roofer would quote for.
None of it is stored, so a roof cannot disagree with itself.

**A roof is drawn, not guessed.** The Roof tool opens a sketch, as Revit's does: the outline
is picked off walls or drawn, checked when it is finished, and kept with the roof so Edit
Footprint can open it again. An edge picked from a wall is not a line but a position on that
wall — its face, or its core, and the overhang — so when the wall moves the edge goes with
it, whatever moved it, undo included. And a roof **stands on its base** rather than hanging
from it: its underside is at its level plus its base offset where it bears on the wall face,
its build-up rises from there, and an overhanging eave carries on down past the wall to its
edge, lower than the plate by the overhang times the pitch, as a built eave is. A new roof's
base is the top of the walls it was picked from, so it sits on them. (Roofs saved before this
hung down from their offset; they are lowered by their thickness on opening, so they stay
exactly where they were.)

The surface is the *lowest* of the planes its eaves define, each restricted to its own
inward side and to the wedge its two corners leave it. Four planes rising inward cut each
other into a hip roof without hips ever being mentioned. The wedge is what makes an L or a
T come out right: extended, a wing's eave plane passes under the main range lower than the
range's own roof, and without it would take a slice out of a roof it has nothing to do
with. At an outside corner each plane keeps the side where it is lower, which meets its
neighbour in a hip; at an inside corner it keeps the side where it is higher, which meets it
in a valley. It agrees with the straight skeleton that IfcOpenShell's roof tool builds, and
keeps working when the pitches differ. The build-up is measured square to the slope, the
way a roof is specified and built, so faces at different pitches still meet flush.

The common shapes are each tested as someone would build them — the list from Balkan
Architect's *10 Common Roof Shapes*: shed, gable, gable with catslide (one eave dropped
lower), clerestory (two sheds at different heights), hip, half-hip (end eaves raised so
they only clip the top), dutch gable (a hip cut off with **Cutoff Height** and a gablet
built on the deck), cross-gabled (a T footprint, valleys and all) and butterfly (two sheds
sloping from a central gutter). The gambrel is the one that cannot be built from a
footprint — each side has two pitches — and needs a roof by extrusion.

**A roof by extrusion is a section pushed back through the building.** Three clicks in plan
say where the section is drawn and how far the roof runs back from it; the section itself is
drawn square-on in a window of its own, starting from a gable, gambrel, barrel vault, shed or
butterfly and moved point by point from there. The line drawn is the roof's underside, and
its build-up stands on it, mitred at each change of pitch so the top runs through unbroken.
A section that stands a line upright, or folds back under itself, cannot roof anything and is
refused with the reason. The vault is an arch of short straight pieces rather than a true
curve.

Roofs export as `IfcRoof` with the shape recorded on them — `HIP_ROOF`, `GABLE_ROOF`,
`SHED_ROOF`, `GAMBREL_ROOF`, `BARREL_ROOF`, `FLAT_ROOF` — a pitched one as an assembly of one `IfcSlab` per face, each
lying in its own plane, which is what IFC asks for. A roof is deliberately not exported as
a slab: a pitched roof is not a slab, and every quantity downstream would inherit the lie.

**A wall attached to a pitched roof rises to meet it.** Its top follows the roof's underside
along its whole length, so a wall under a gable end fills the triangle up to the ridge and
one under an eave stays at the plate. The outline is worked out from the roof whenever it is
needed, never stored, so a new pitch or footprint reaches the walls at once — and since it is
an outline, the wall's solid, plan cut, section, doors and windows and quantities all follow
it, as they do an edited profile. The underside is taken over the wall's inner face, so no
wedge of air shows along the inside of an eave wall; the extra is hidden in the roof's own
build-up. A stretch of wall running out past the roof steps back down to its own height. An
outline drawn by hand still wins, and curved, leaning and stacked walls keep a level top.
Finishing a roof picked from walls asks whether to attach them, as Revit does.

**Slope arrows are planes too.** An arrow in the sketch is a plane of its own — level across
the arrow, rising along it — and it takes its place among the eave planes by the same rule.
At each corner a face keeps the side of the line between it and its neighbour that its own
edge is on, which is what lets the two arrows of a dormer each take their half of the stretch
of eave between them, and the eave either side keep the rest. An arrow starting at a corner
beside a sloping eave starts at that eave's height, so a dormer continues the eave it is cut
into even where the eave overhangs; elsewhere its height is measured from the roof's base.
Faces that come out in the same plane — the two halves of a split eave — are joined into one.

Not yet: sloped glazing, dormers with walls and a dormer opening, joining one roof to
another, openings in roofs, fascias and soffits. (§3.3)

**A room stores where it is, not what shape it is.** Its outline, area, perimeter and volume
are traced from the walls around it each time they are asked for, so moving a wall changes
the room's area without anyone editing the room — the difference between a room and a
rectangle labelled "room". When the walls no longer enclose the point it is reported as
**not enclosed** rather than quietly reading zero, because a silent zero is how a wrong area
schedule reaches a client. (§3.7)

An unenclosed room reports **no area at all** — not zero — everywhere: in the property panel,
in a schedule and in a CSV export it shows a dash, and it is left out of IFC entirely. Zero
square metres is a claim about a room; a dash is not, and an area schedule has to be able to
tell the two apart.

Area is measured **to the wall faces**, not the centrelines. Measuring to centrelines would
count half of every wall as floor area, which is not what anyone builds, sells or heats.

**A wall's holes are derived too.** `WallOpenings` asks which openings are hosted in a wall
and returns the stretches that remain solid; the wall is drawn as those stretches. An
opening is not painted over the wall afterwards — the wall genuinely is not there. Deleting
a wall deletes its doors and windows, because a door with no wall would quietly corrupt
every schedule that counts them. (§2.5)

**A mitre cuts every layer on one line**, so a corner turns as a single piece of
construction.

**Three or more walls meeting with no straight run through share the junction.** Going round
the joint, each pair of neighbouring walls meets at a corner where their facing sides cross;
each wall is cut from the corner on one side of it, through the middle of the junction, to
the corner on the other. Every piece of the junction belongs to exactly one wall, so they fill
it once with no overlap and no gap. Where several straight runs cross, the heaviest carries
through and the rest stop against it. Two walls that cross part-way along both, with no end
there at all, are joined the same way: the heavier carries on and the lighter is drawn as two
pieces stopping against its faces. Anything that cannot be joined cleanly falls back to a
square end that overlaps.

**Each end can be told how to join**: Mitre, Butt, Run Through, Square Off or Disallow. A
corner has one join and two walls, so setting one end sets the other to match.

**Joined ends are not drawn.** A finished plan does not rule a line across every corner and
tee: the outline follows the faces, leaves a gap where another wall carries on, and closes
only exposed ends and the sides of openings.

**Finishes wrap** round exposed ends and into openings, as the wall type's *Wrapping at Ends*
and *Wrapping at Inserts* say: the wrapping layers outside the core turn the corner, and
everything they wrap round is set back behind them. In 3D the wrap into an opening is left
out, since there it would run the full height of the wall rather than stopping at the head.

**Levels are datums, and deleting one deletes what stands on it.** A wall constrained to a
level works its height out from it every time it is asked, so raising a storey raises every
wall that reaches it and every slab that sits on it, and the section redraws, with nothing in
between to update. Deleting a level cascades to everything hosted on it, as one undoable step;
walls that only reach *up* to it keep their height instead. (§2.3)

**Copying is three operations.** A copy gets a new id; a copied wall brings its doors,
rehosted onto the copy; and references within the copied set follow the copies while
references out of it stay put — a tag copied with its wall labels the new wall, a tag copied
alone still labels the old one. Marks are deliberately not copied, because two doors both
marked D-04 is a defect that reaches the site through the door schedule. (§2.4)

**Paste in place is how storeys are built.** Copy the ground floor, open the first floor,
`Ctrl+Shift+V`. A pasted wall's top constraint moves up with it, so a wall that reached the
first floor now reaches the second.

**Parameters are a view over real properties.** Geometry code reads `wall.Length` directly.
The property panel reads the same value through `GetInstanceParameters()`, which returns
`ParameterValue` objects bound to those properties. One source of truth, one uniform data
view — and the panel generates itself, so new categories need no new property-panel XAML.

## Project files

Projects are saved as `.bimx`, which is JSON — diffable, greppable and fixable by hand
while the schema is still moving. `ProjectFile` translates through a DTO layer rather than
serialising the domain objects directly, so refactoring `Wall` cannot silently break every
saved project. `formatVersion` guards against a newer build's files being misread.

Every edit goes through `UndoStack` as an `IUndoableCommand` storing only what it takes to
reverse itself. The stack also answers "are there unsaved changes?" by remembering how deep
it was at the last save, so undoing back to that point makes the project clean again.

## Using the app

| Action | Input |
| --- | --- |
| Draw a wall | `Wall` tool (`W`), click start, click end (keeps chaining) |
| Stop drawing | `Esc` or right-click — a second `Esc` clears the selection |
| Select | `Select` tool (`S`), click an element |
| Select several | `Ctrl`+click to add or remove, or drag a box on empty space |
| Select everything on this level | `Ctrl+A` |
| Move | Drag anything that is selected — the whole selection moves |
| Nudge the selection | Arrow keys move it 100 mm a press — the snapping step, so it is the same distance at any zoom rather than a screen amount that changes as you zoom. `Shift`+arrow moves a metre. A held key is one move to undo |
| Copy / paste | `Ctrl+C`, then `Ctrl+V` to paste at the cursor |
| Paste onto another storey | `Ctrl+C`, switch level, `Ctrl+Shift+V` to paste in the same place |
| Offset a wall | `Offset` tool (`O`), set the distance, click the wall on the side to copy to |
| Mirror | Select, `Mirror` tool (`I`), click two points on the mirror line |
| Array | Select, `Array` tool (`Y`), set the count, click two points for the spacing |
| 3D view | `Ctrl+3`, View tab → **3D View**, the cube on the quick access bar, or "3D View" in the project browser |
| Plan and 3D side by side | View tab → **Tile Views**, or the tile button on the quick access bar |
| Light or dark theme | Manage tab → **Dark Theme**; takes effect the next time the app starts |
| Orbit / pan / zoom in 3D | Drag / right-drag / scroll. `F` fits, `Home` resets |
| See inside in 3D | Untick a storey in the 3D bar, or switch Style to X-ray |
| Manage levels | Architecture tab → **Level** (or Manage tab → Levels) |
| Storey below as underlay | View tab → **Storey Below** |
| Reshape a wall | Select it, then drag a square end grip |
| Flip a wall inside-out | Select it and press `Space`, or click the blue arrows on its exterior side |
| Flip the wall being drawn | `Space` while drawing |
| Draw a wall off the clicked line | Type an **Offset** on the option bar before drawing |
| Attach a wall's top to a level | Properties → **Top Constraint** → Up to level |
| Hide walls by function | View tab → **Wall Functions** → pick the view, untick the function |
| Change how a corner joins | Modify tab → **Wall Joins**, click the square at the join (Ctrl+click for more): **Butt**, **Miter** or **Square Off**; **Previous** / **Next** change which wall carries on; **Display** cleans the join or shows the walls butting; **Disallow Join** leaves a gap. Or per wall end: Properties → **Start Join** / **End Join** |
| Clean joins only between walls of one type | With nothing selected, Properties → **Wall Join Display** → Clean same type wall joins |
| What shows at the outside of a corner | The wall that carries on past the other leaves an end that is a piece of the elevation - the return of the corner - so its layers turn round it as its type's **Wrapping at Ends** says, rather than leaving the cavity and the blockwork on show. The template's brick wall returns in brick; set it to **None** on the type to have the layers cut straight through. A stretch too short to turn them in, or a corner that is not a right angle, is cut straight anyway. Projects saved before this said None on every wall type, because nothing turned round an end then, and they are brought up to it when they open |
| Join two parallel walls near each other | Modify tab → **Join Geometry**, click one wall then the other (up to 150 mm apart): doors and windows in either cut through both. Click the pair again to unjoin |
| Stand a wall on the wall below, or take it up to the wall above | Select it → **Attach Base** / **Attach Top**: it attaches to whichever is nearer, a slab or a wall in line with it |
| Draw a curved wall | Wall tool, **Shape: Arc** on the option bar: click the start, the end, then a point the arc passes through |
| Curve or straighten a wall | Select it and drag the diamond halfway along it; bring it back to the straight line to straighten |
| Draw a whole shape of walls | Wall tool, **Shape**: Rectangle (Shift for a square), Polygon (set Sides; 3 is a triangle), Circle, Oval or Ellipse (Shift for round). Two clicks place every wall, joined |
| Draw half an ellipse | Wall tool, **Shape: Partial ellipse**: click both ends of an axis, then a point the ellipse passes through |
| Draw a spline wall | Wall tool, **Shape: Spline**: click the start, then points the wall curves through; `Enter` or a double click finishes, clicking the first point closes a smooth loop |
| Draw a freeform wall | Wall tool, **Shape: Freehand**: hold the mouse button and draw; let go to build it, end where you began to close a loop |
| Put a corner point in a wall | Select it and double-click on it (or **Modify \| Walls → Add Point**): the wall splits there, and dragging the round grip pulls both parts straight to wherever you put it. Double-click the point to make them one wall again. On a spline wall the point is smooth instead |
| Reshape a spline wall | Select it and drag the round grips on its points |
| Make or edit a wall type | Architecture tab → **Wall Types**, or **Edit Type...** in Properties with a wall selected |
| Change the type of several walls | Select them, then pick a type at the top of Properties |
| Wrap finishes into openings and round ends | Wall type → **Wrapping at Inserts** / **Wrapping at Ends** |
| Draw a wall going down (foundation, retaining) | Wall tool, set **Depth** instead of Height on the option bar |
| Place walls on existing lines | Wall tool, **Shape: Pick lines**, click grid lines |
| Pick how walls are drawn | Choose the Wall tool: the green **Modify \| Place Wall** tab holds every shape (line, arc, rectangle, polygon, circle, oval, ellipse, spline, freehand, pick lines), Place by Segment and by Room, and Auto Join and Lock |
| Line one face of a wall (an accent or finish wall) | Wall tool, **Place by Segment**, click beside the wall on the side the new wall goes. It runs that face's length between the walls it meets, and its location line is the face against the wall |
| Line every face of a room | Wall tool, **Place by Room**, click inside the room |
| Join a new wall to the wall it lies against | Turn on **Auto Join** on the Place Wall tab before placing: doors and windows in either then cut through both. Turn on **Lock** to make them move together (it turns Auto Join on with it) |
| Keep walls joined at a corner when moving them | Select a wall: a padlock shows just inside each end that meets other walls. Click it to lock that corner (it goes gold): move any of the walls and the others stretch to stay joined there. **Modify \| Walls → Lock Ends** locks every corner of the selection at once; click again to unlock |
| Lock two walls lying against each other | Select one: a padlock shows on the face they share. Click it to lock them so they move as one (it goes gold); click again to unlock. Works on walls already drawn |
| Make a wall follow the floor or roof above | Select walls, Architecture tab → **Attach Top** (or **Attach Base**), then click the slab; **Detach** undoes it. Attached to a pitched roof, the wall's top follows the slope — a gable end fills up to the ridge. Finishing a roof picked from walls offers to attach them for you |
| Build a wall from tiers of other types | Wall Types → **New Stacked**, add tiers top first, make one tier variable |
| Lean or taper a wall | Properties → **Cross-Section** → Slanted (set **Angle from Vertical**) or Tapered |
| Bend a wall partway up | Properties → **Cross-Section** → Double Slanted: set the **Lower** and **Upper Angle from Vertical** and the **Slant Break Height** |
| Give a wall a gable, steps or a notch | Select one straight wall, Architecture tab → **Edit Profile**: drag corners, double-click an edge to add one, Delete removes one; pick a corner and **Make Arc** curves the edge after it by the rise typed beside it (negative bows in) |
| Cut a hole through a wall | Architecture tab → **Wall Opening**, set width, height and sill on the option bar, click the wall - curved walls too. Change it afterwards in Properties |
| Draw a curtain wall | Wall tool, pick a **Curtain Wall** type, draw as any wall (straight, arc or a shape) |
| Where a curtain wall meets another wall | It stops against the face of what it meets, like any other wall, and the frame comes with it: the mullion at that end stands at the shortened end and the glass stops short of it, rather than the glazing running on into the masonry and losing its edge |
| Change a curtain wall's grid or panels | Select it, Architecture tab → **Curtain Grid**: click a panel to make it glass - clear, tinted, frosted, laminated or an opaque spandrel panel - or solid, empty, or a door of a chosen door type; drag a line; double-click to add one (Shift for horizontal); Delete removes one |
| Select one panel of a curtain wall | Click the pane in the 3D view: Properties shows that panel alone - which bay it is, what fills it, its glass, its door type, its size and area. Clicking a mullion selects the whole wall |
| Mirror a curtain wall door | Select the panel and press `Space` to flip which way it opens, or **Modify | Curtain Panels → Mirror** to hang it on the other side; or Properties → **Flip Hand** / **Flip Facing** |
| Open a curtain wall door | Right-click the panel → **Open** (**Close** again), or Properties → **Open**. It swings out of its bay like any other door; a panel that is not a door has nothing to open |
| Reglaze a whole curtain wall | Select the wall, Properties → **Glass**: every panel follows it except the ones given glass of their own |
| Put a door in a curtain wall | **Door** tool, click low on the wall: a bay is cut for the door, with glass beside it and a transom light over it, and the mullion under it goes. A panel that is already a doorway is taken as it is. Only a **Curtain Wall Door** type can be a panel, so a glass one of the nearest size is used; set **Curtain Wall Door** on any door type to offer it. Making the panel glass again takes it away |
| Make or edit a curtain wall type | Architecture tab → Wall Types → **New Curtain**, or pick a curtain type: grid spacing, panels, mullions |
| Set a shopfront into a wall | Draw a curtain wall whose type has **Automatically embed** along inside the wall; it cuts its own opening |
| Add a skirting, cornice or groove | Wall Types → **Add Sweep** / **Add Reveal** under Sweeps and Reveals |
| Draw your own sweep profile | Wall Types → **Profiles...**: draw the outline, or start from a preset |
| Put a sweep or reveal on some walls | Architecture tab → **Sweep** / **Reveal**, pick a type and Horizontal or Vertical on the option bar, click a wall face |
| Carry a sweep onto more walls, or turn its ends | Select the sweep → **Modify \| Wall Sweeps** tab → **Add/Remove Walls**, or **Modify Returns** with Straight Cut / Return on the option bar |
| Find what can be done to a selection | Select it: the green **Modify \| …** tab holds its tools |
| Edit several elements at once | Select them: Properties shows **Walls (2)** and edits all of them; a blank value means they differ. Pick another category from that list when the selection is mixed |
| Pick something under something else | Hover over it and press `Tab` until it is outlined, then click. The status bar names what is outlined |
| Set a plan's scale or detail | The bar under the plan: scale, detail level, storey below. With nothing selected, Properties shows the same |
| Turn the 3D view to a face, edge or corner | Click the ViewCube in the corner; drag it to orbit; the house resets. Zoom buttons are under it |
| Place a door | `Door` tool (`D`): a preview follows the cursor along the wall; point at the side it should swing toward, `Space` swaps the hinge side, click to place. New doors are marked 1, 2, 3…; tick **Tag on Placement** to tag each one |
| Pick a door's design | Door types: **Leaf Design** - Flush, Panelled, Glazed, French Glazed (set **Glazing Rows** / **Columns**), Half Glazed, Louvred, Arched Top Light, Full Glass; with **Trim Width** and projections for the architrave. The template has French double, single glazed, louvred bi-fold, arched entrance and curtain wall glass doors |
| Set one door's own size | Select it, Properties → **Width** / **Height**: that door alone changes, and the hole in the wall with it. Set it back to the type's size and it follows the type again. **Area** follows |
| Line a wall up with a thicker one | Drawing a wall into the end of a thicker one lines it up with that wall's inner face automatically. For walls already drawn: select one → **Modify \| Walls → Align Faces**, again for the other face; Properties → **Offset Across** undoes it |
| Open or shut a door or window | Right-click it → **Open** or **Close**. The leaf swings on its hinges in 3D, a pair swings apart, a slider runs along its track and a casement sash swings out. Properties → **Open** does the same |
| What a right-click offers | In the plan **or the 3D view**. On a door or window: Open / Close, Flip Facing, Flip Hand, Pick New Host, Delete. On a curtain wall panel: which panel it is, and Open / Close, Mirror, Flip Facing for a door panel. On a wall: Flip, Delete. Right-dragging still pans or orbits |
| Flip a door | Select it: click the ↕ arrows to flip which way it swings, the ↔ arrows to flip its hinge side; or press `Space` |
| What flipping shows on a shut door | A door is hung in its rebate with the leaf flush on the side it opens to and its hinges proud of that face, so **Flip Facing** moves the leaf across the reveal and takes the hinges with it, and **Flip Hand** moves them to the other jamb - both visible in 3D without opening the door. Casement and tilt-and-turn sashes are hung the same way |
| Move a door to another wall | Select it → **Modify \| Doors → Pick New Host**, click the wall where it should go |
| Set a door's own properties | Properties: **Sill Height**, **Head Height** (moves it, not its size), **Orientation** in a slanted wall (Vertical or Slanted), **Frame Type**, **Frame Material** (blank follows the type), **Finish**, **Mark**, **Comments**, **Phase Created** and **Phase Demolished** |
| Stand a door upright in a slanted wall | A door placed in a slanted wall leans with it; one already in the wall when it was slanted stays upright. Properties → **Orientation** changes it |
| What **Open** does to a window | Each kind opens the way it works: a casement or tilt-and-turn sash swings out about its hinge, a slider runs its sash across the fixed one to leave half the window clear, and a double-hung pushes its lower sash up behind the upper. A pair of casements is hung one sash on each jamb, so they swing apart. Handles, catches and glazing bars all go with the sash they belong to. A fixed light has nothing to open |
| Pick a window's kind | Window types: **Operation** - Fixed, Casement, Awning, Hopper, Sliding, Tilt and Turn, Double Hung, Louvred, Bay - and **Glazing Rows** / **Columns** for divided lights. The template has twelve, one of each |
| How many panes the bars make | **Glazing Rows** and **Columns** are what one sash is divided into, as joinery counts it: six over six is six panes in each sash of a double hung, not six in the opening. A pair of casements gets the same division in each leaf |
| Put a window in a curtain wall from the grid editor | **Curtain Grid** → **Window (cut into the wall)**, pick the type, set how far along and up from the middle of the panel, then click a panel. It is added to the wall as a window of its own |
| Put a window in a curtain wall | **Window** tool, click the wall where it should go: the window is cut into it like a window in any wall - its own element, selected on its own, with its own type, size, **Sill Height** and **Distance Along Wall**. The glass and the mullions give way to it, and it shows in the **Edit Curtain Grid** elevation where it sits |
| Move a curtain wall panel | Select it, Properties → **Distance Along Wall**: the bay slides along the wall, lines and all, keeping its width. **Height Above Floor** says where its underside is |
| Resize a curtain wall panel | Select it, Properties → **Width** / **Height**: the grid lines round it move and the bays beside it give up what it takes |
| Place a window | `Window` tool (`N`), click a wall. In the 3D view, click the wall where the window should go: it lands there, centred on the height clicked |
| Place a room | `Room` tool (`R`), click inside an enclosed space |
| Stand an architectural column | Architecture tab → **Column**, pick a type, click where it should stand. `Space` turns it a quarter turn. Square, oblong, round, oval, triangular, hexagonal, octagonal and a corner one that tucks into a room |
| Draw a column's own section | Select a column, **Modify → Column → Edit Section**. The editor opens on the section with the column itself in 3D beside it, rebuilt on every edit. Drag to draw a **rectangle**, **circle / ellipse**, **polygon** of any number of sides, or click corner by corner for a **freeform** shape - then **Cut** it out of the section, **Add** it to the section, or keep only where the two **Intersect**. Saving gives the type that section and its shaping, so every column of that type takes it at once |
| Start from a ready-made section | **Start From** in the editor: twenty sections ready to use - **Plain** (square, round, hexagonal, octagonal, triangular), **Corners** (chamfered, rounded, stadium, quirked), **Reveals** (one or two grooves each face, grooved and fluted round), **Hollow** (box, tube, box round a core) and **Built up** (cruciform, T, L corner, square on a round, wall pier). Each is built from the width and depth the section already has, and can be cut and added to afterwards like anything else |
| Draw to a dimension rather than by eye | Type **W**, **D**, **X** and **Y** on the toolbar and press **Apply**: the shape goes in at exactly that size in exactly that place. Dragging shows the size beside the cursor as it goes, and **Snap** sets what the cursor lands on - off, or 1 to 50 mm |
| Get around the board | The wheel zooms about the cursor, middle or right dragging pans, and **Fit** puts the whole section back on screen. The view stays where you put it: editing the section never moves or rescales the board under you |
| Change what the column does while drawing it | The right-hand side carries **Height**, **Top size**, **Twist**, **Slant**, **Flutes**, **Base** and **Capital**, with the column in 3D under them. Change any of them and the column is rebuilt there and then, so a tapered fluted shaft is drawn and checked in one place |
| Take the corners off a section | In the editor: a corner **size**, then **Round corners** or **Chamfer corners**. A corner too shallow to be one is left alone, and one whose edges are too short takes back only as far as they allow, so the shape can never turn inside out |
| Make a column taper, twist or lean | Column types: **Top Size** (below 100% tapers, above it flares), **Twist** in degrees over the height, and **Slant Across** / **Slant Along** for how far the top stands off the base. The solid is regenerated from those numbers, so a column changes shape when its height changes |
| Give a column a base and a capital | Column types: **Base Height** and **Capital Height**, with their spread. The shaft is what is left between them and keeps its own size, so making the column taller grows the shaft and leaves the base and capital as they were |
| Flute a shaft | Column types: **Flutes** and **Flute Depth**. The hollows are cut round the face by the same boolean a hand-drawn cut uses, so fluting is not a special kind of column - it is a section with grooves in it |
| Classical columns | **Tuscan**, **Doric** and **Ionic** are in the template as parameters, not as fixed shapes: a tapered shaft, fluting on two of them, a plinth and a capital. Change any number and the column regenerates |
| Say how a column sits against a wall | Properties → **Wall Placement**: **Freestanding**, **Against Wall** (wholly outside it, its back on the face), **Embedded In Wall** (inside it, flush with one face), **Intersecting Wall** (straddling the line), or **Wall Corner** (tucked into the corner two walls make). **Placement Face** picks which face it is measured from, and setting either moves the column there |
| What a cut in the middle leaves | A hole - a real void through the column, in the plan, in section and in 3D alike, not a shape drawn on top. A cut that reaches the edge takes a notch out instead, and one that goes right across would cut the column in two, so the larger piece is kept |
| Set how high a column is | It is what it stands between, not a number it carries: **Base Level** and **Base Offset**, **Top Level** and **Top Offset** - or **Height** while its top is unconnected. The same constraints a wall is held by |
| Say how high it goes as you place it | The options bar: **Height** takes the column up from the storey being drawn on to the level chosen, **Depth** takes it down from it, and **Unconnected** leaves it standing its own height. Set once, every column placed after it is constrained the same way |
| Stand columns at grid intersections | Select two or more gridlines that cross, then **At Grids** on the options bar: a column goes at every intersection between them. Intersections that already have one are left alone, so running it again adds only what is missing |
| Change what a column type is | Column types: **Shape**, **Width** / **Depth**, **Material** chosen from the project's materials, **Coarse Scale Fill Colour**, and **Offset Base** / **Offset Top** - offsets that belong to the family, so a column that always stands on a plinth says so once and every one placed from it does it |
| Hold a column clear of what it meets | Attached, Properties offers **Offset From Attachment At Top** / **At Base**: the column stops that far short of the face, which is how one is held clear of a slab it should not be carrying. **Top is Attached** and **Base is Attached** say what is holding it |
| A column in a room | Properties → **Room Bounding**, on by default: the column's area comes out of the room it stands in and its sides go on the perimeter, so an area schedule does not count floor that is not there. One built into a wall is not counted - the wall has already shaped the room |
| Columns move with the grid | A column standing on a gridline comes along when that grid is moved, as one step on the undo stack - the grid is the setting out and the columns are what it sets out. Properties → **Moves With Grids** lets one off, for a column moved off the grid on purpose |
| Take a column up to what is over it | Select it, Architecture tab → **Attach Top** (or **Attach Base**), then **click the floor, ceiling or roof** to attach it to - the target is picked, not guessed, so a column under two slabs goes to the one you mean. It follows that slab when it moves. **Detach** gives it its own constraints back |
| Say how the column meets what it is attached to | **Attachment Style** on the options bar while Attach waits: **Cut Column** stops it at the face it meets, **Do Not Cut** carries it on through to the far side. **Offset** holds it that far short of the face. Both are on the column afterwards in Properties |
| Put a column on a wall face | Click on a wall and the column sits flush with the face you clicked on, stepping out of that one only, rather than straddling the wall's line and standing out of both. Select it and **Modify → Column → Align Faces** puts it against the other face; click again to send it back |
| A column built into a wall | Stand it where a wall runs and it is drawn as part of that wall: at **Coarse** detail it takes the wall's fill pattern, and drawn finer it is the wall's structural material. In plan and in section alike, so the two read as one piece of construction rather than a column standing in front of a wall |
| What happens where a column and a wall overlap | Whichever goes right through the other wins. A column reaching **through** the wall is a pier, and the wall gives way to it, its layers wrapping at the sides. A column standing only **part way** into the wall has that buried part cut off it, because a column and a wall in the same place is two solids in one place - wrong in 3D, wrong in section, and counted twice in every quantity. Properties → **Cut By Walls** turns it off for one column |
| What the wall does at a column | A column that goes right through the wall interrupts it, and the wall's compound layers **wrap** at each side of the gap the way they wrap at a doorway - the brick returns into the reveal instead of leaving the cavity and the blockwork on show against the column. A pilaster shallower than the wall stands proud of a wall that carries on behind it, so there is nothing to interrupt |
| Place a component | Architecture tab → **Component**, pick a family on the options bar, click where it goes. `Space` turns it a quarter turn before you click. The template has furniture, casework, sanitaryware, a wall light and a tree |
| Place a component on a wall face | Pick a family whose **Placement** is Face Based or Work Plane Based - a wall light, a wall-hung basin, a wall unit - and click on a wall. It stands against the face clicked, at the fixing height its family says, and is carried by that wall from then on |
| Move a component to a different host | Select it, **Modify → Work Plane → Pick New** (or right-click → Pick New Host), then move over the new host and click. The component is drawn where it would land and the host under it is outlined, so you see the move before you make it |
| Choose what Pick New will take | **Placement** on the options bar while the move is being made: **Vertical Face** takes only a wall; **Face** takes a wall, floor, ceiling or roof; **Work Plane** takes the level instead of a face and stands the component on it. A face-based family cannot go on a work plane, and is refused there |
| What a component on a slab does | It stands on a floor or a roof, and hangs from the soffit of a ceiling, which is where a light goes. Where a ceiling and a floor are both under the cursor the face overhead is taken - a floor is what things stand on, and placing one on the level already does that |
| Move a component to a different level | Select it, Properties → **Level**. **Elevation** lifts it off that level without changing storey |
| Turn a component already placed | Select it, right-click → **Turn 90°**, or Properties → **Rotation** for any angle |
| Change a component family | Component types: **Category**, **Form**, **Width** / **Depth** / **Height**, **Placement** and **Default Elevation**. Every instance of it follows, in plan and in 3D |
| Draw a gridline | `Grid` tool (`G`), click start, click end |
| Cut a section | `Section` tool (`C`), click start, click end — the view opens below the plan |
| Flip a section | Select the marker, or use **Flip** in the section panel |
| Open the sheet view | `Ctrl+H`, or pick a sheet in the project browser |
| New sheet | View tab → **New Sheet** |
| Place a view on a sheet | Pick it in **Place**, press **Add to Sheet** |
| Move a view on a sheet | Drag it |
| Change a view's scale | Select it, then use the **Scale** box |
| Export the model as IFC | File → Export IFC... |
| Export a sheet as PDF | **Export PDF** on the sheet bar, or File → Export This Sheet to PDF |
| Export the whole set | File → Export Drawing Set to PDF — one file, one page per sheet |
| Print | `Ctrl+P` — asks the printer for paper the size of the sheet |
| Dimension | `Dimension` tool (`M`), click what to measure from, then to |
| Tag an element | `Tag` tool (`A`), click it |
| Write a note | `Text` tool (`E`), click where it goes |
| Lay a floor | `Floor` tool (`F2`), click inside an enclosed space |
| Add a ceiling | `Ceiling` tool (`F3`), click inside an enclosed space |
| Add a roof | `Roof` tool (`F4`) opens a **roof sketch** (the green **Modify \| Create Roof Footprint** tab). **Pick Walls**: hover just outside a wall and click — the edge goes along that face, out by the **Overhang** on the options bar, and follows the wall if it moves; **TAB** picks the whole chain of joined walls at once. Or draw with **Line**, **Rectangle**, **Polygon**. Corners between picked walls close by themselves. **Finish** ✓ makes the roof, sitting on the tops of the walls it was picked from; **Cancel** ✗ throws the sketch away |
| Change a line in a roof sketch | Click its **△** to turn its slope on (filled, with its pitch) or off (a gable end). **Select Lines**, then **Defines slope**, **Slope**, **Overhang** and **Extend to wall core** on the options bar change the selected lines; Del deletes them. Ctrl+Z undoes the last sketch change; Esc stops the line being drawn |
| Reopen a roof's sketch | Double-click the roof, or select it → **Edit Footprint** |
| Why a roof won't finish | The lines at fault turn red and the status bar says why: the outline is open, lines cross, there is more than one loop (openings in roofs come later), or a slope arrow no longer starts on a line |
| Slope a roof a way no edge says | In the roof sketch, **Slope Arrow**: click a line of the outline for the tail, then where the slope rises toward. On the options bar, **Specify** is **Slope** (a pitch from the tail) or **Height at Tail** (heights at both ends, the pitch worked out between them). One arrow on a flat roof makes it fall one way — diagonally from corner to corner if drawn that way; an arrow up from a raised gable end makes a half-hip |
| Add a gambrel, a barrel vault or any roof the same all along | **Roof by Extrusion** (next to Roof): click one end of the line the roof's section is drawn on, then the other end (the width it spans), then how far back the roof runs. Draw the section in the window that opens: pick **Gable**, **Gambrel**, **Barrel Vault**, **Shed**, **Butterfly** or **Flat** with a **Rise** and **Overhang**, then drag points, double-click a line to add one, right-click a point to take it out. **Base** is how high above the level it sits — it starts at the tops of the walls under it. **Finish** makes the roof |
| Change an extruded roof's section | Double-click it, or select it → **Edit Profile**. Its depth is **Extrusion Start** and **Extrusion End** in Properties |
| Put a dormer in an eave | In the roof sketch, **Split** the eave twice, select the middle stretch and clear **Defines slope**, then draw two **Slope Arrows** from its ends to its middle. The middle of the eave lifts into a little gable with its own ridge and valleys |
| Change a roof edge | Select the roof and click one of its edges: it becomes an eave the roof slopes up from, or a gable it is cut off at. Arrows show which edges slope while the roof is selected |
| Reshape a whole roof | Select it, Properties → **Roof Shape**: Hip, Gable, Shed or Flat. **Slope** sets the pitch of every sloping edge at once |
| Change one eave's pitch or height | Select the roof: Properties lists **Slope at Edge N** and **Eave Offset at Edge N** for each sloping edge. Drop one eave for a catslide; raise the end eaves of a hip for a half-hip |
| Cut a roof off flat | Properties → **Cutoff Level** (None, Roof Base, or a level) and **Cutoff Offset**: the roof stops there with a flat deck on top — the base of a dutch gable, with a small gable roof placed on the deck |
| Change how the eaves are cut | Properties → **Rafter Cut**: **Plumb Cut** (straight down through the roof), **Two Cut Plumb** (straight down for the **Fascia Depth**, then level back to the underside, where a soffit goes) or **Two Cut Square** (square to the slope for the fascia depth, then level). Fascia Depth can be anything up to the roof's thickness |
| Split a wall | `Split` tool (`X`), click where it should divide |
| Trim / extend | `Trim` tool (`T`), click the wall, then the wall to meet |
| Delete | `Del` |
| Pan | Right-drag or middle-drag |
| Zoom | Scroll wheel |
| Zoom to fit | `F` |
| Switch tool | `S` = select, `W` = wall |
| Detail level | View tab → Coarse, Medium or Fine — see below |
| New / Open / Save | `Ctrl+N` / `Ctrl+O` / `Ctrl+S`, Save As `Ctrl+Shift+S` |
| Undo / Redo | `Ctrl+Z` / `Ctrl+Y` |

The option bar above the canvas sets what new walls are made of: **wall type**, **level**
and **location line**, and with the wall tool an **offset** from the clicked line. Clicks snap to a 100 mm grid, and to nearby wall ends in preference
to the grid so corners meet exactly and mitre.

Changing an existing wall's location line does not move the wall: the drawn line moves to
the chosen face, and walls joined to it follow so their joins hold.

### Detail level

Detail level is a property of the **view**, not of the zoom (spec §6.2). Zooming magnifies
the drawing; it never changes what the drawing contains, any more than leaning closer to a
printed sheet would.

| Level | Walls are drawn as |
| --- | --- |
| Coarse | One body in the wall type's coarse-scale fill colour |
| Medium | Every layer in its material's cut colour, no separating lines |
| Fine | Every layer, with a line between each |

At Fine, the separating lines are dropped once the layers fall below about two screen
pixels apart. That is a stroke-weight decision, not a content one: a 0.7 px line between two
sub-pixel bands covers more of the wall than the bands do, so the wall would read as a grey
smear. The layers themselves are always drawn.
