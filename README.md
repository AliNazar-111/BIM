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
IFC4: project, site, building and a storey per level; walls, floors, roofs and ceilings as
extruded solids; doors and windows as real voids in their walls, with the door filling the
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

Floors, ceilings and flat roofs share one `SlabType` built on the same `CompoundStructure`
as walls, so material takeoff, cost and U-values work identically for horizontal and
vertical construction with no second implementation to keep in step.

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
| Copy / paste | `Ctrl+C`, then `Ctrl+V` to paste at the cursor |
| Paste onto another storey | `Ctrl+C`, switch level, `Ctrl+Shift+V` to paste in the same place |
| Offset a wall | `Offset` tool (`O`), set the distance, click the wall on the side to copy to |
| Mirror | Select, `Mirror` tool (`I`), click two points on the mirror line |
| Array | Select, `Array` tool (`Y`), set the count, click two points for the spacing |
| 3D view | `Ctrl+3`, View → 3D View, or "3D View" in the project browser |
| Orbit / pan / zoom in 3D | Drag / right-drag / scroll. `F` fits, `Home` resets |
| See inside in 3D | Untick a storey in the 3D bar, or switch Style to X-ray |
| Manage levels | View → Levels... |
| Storey below as underlay | View → Show Storey Below |
| Reshape a wall | Select it, then drag a square end grip |
| Flip a wall inside-out | Select it and press `Space`, or click the blue arrows on its exterior side |
| Flip the wall being drawn | `Space` while drawing |
| Draw a wall off the clicked line | Type an **Offset** on the option bar before drawing |
| Attach a wall's top to a level | Properties → **Top Constraint** → Up to level |
| Hide walls by function | View → Wall Functions → pick the view, untick the function |
| Change how a corner joins | Select a wall → Properties → **Start Join** / **End Join** (Mitre, Butt, Run Through, Square Off, Disallow) |
| Draw a curved wall | Wall tool, **Shape: Arc** on the option bar: click the start, the end, then a point the arc passes through |
| Curve or straighten a wall | Select it and drag the diamond halfway along it; bring it back to the straight line to straighten |
| Draw a whole shape of walls | Wall tool, **Shape**: Rectangle (Shift for a square), Polygon (set Sides; 3 is a triangle), Circle, Oval (Shift for round). Two clicks place every wall, joined |
| Make or edit a wall type | Architecture → Wall Types..., or **Edit Type...** in Properties with a wall selected |
| Change the type of several walls | Select them, then pick a type at the top of Properties |
| Wrap finishes into openings and round ends | Wall type → **Wrapping at Inserts** / **Wrapping at Ends** |
| Draw a wall going down (foundation, retaining) | Wall tool, set **Depth** instead of Height on the option bar |
| Place walls on existing lines | Wall tool, **Shape: Pick lines**, click grid lines |
| Make a wall follow the floor or roof above | Select walls, Architecture → **Attach Wall Tops** (or **Bases**), then click the slab; **Detach** undoes it |
| Build a wall from tiers of other types | Wall Types → **New Stacked**, add tiers top first, make one tier variable |
| Lean or taper a wall | Properties → **Cross-Section** → Slanted (set **Angle from Vertical**) or Tapered |
| Add a skirting, cornice or groove | Wall Types → **Add Sweep** / **Add Reveal** under Sweeps and Reveals |
| Place a door | `Door` tool (`D`), click a wall |
| Place a window | `Window` tool (`N`), click a wall |
| Place a room | `Room` tool (`R`), click inside an enclosed space |
| Draw a gridline | `Grid` tool (`G`), click start, click end |
| Cut a section | `Section` tool (`C`), click start, click end — the view opens below the plan |
| Flip a section | Select the marker, or use **Flip** in the section panel |
| Open the sheet view | `Ctrl+H`, or pick a sheet in the project browser |
| New sheet | Sheets → New Sheet |
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
| Add a flat roof | `Roof` tool (`F4`), click inside an enclosed space |
| Split a wall | `Split` tool (`X`), click where it should divide |
| Trim / extend | `Trim` tool (`T`), click the wall, then the wall to meet |
| Delete | `Del` |
| Pan | Right-drag or middle-drag |
| Zoom | Scroll wheel |
| Zoom to fit | `F` |
| Switch tool | `S` = select, `W` = wall |
| Detail level | View → Detail Level — see below |
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
