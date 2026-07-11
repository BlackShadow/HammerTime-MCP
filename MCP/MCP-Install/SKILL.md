---
name: hammertime-goldsrc-brushwork
description: Use when AI needs to create, edit, or review GoldSrc brushwork in HammerTime, especially map geometry, vertex manipulation, arches/cylinders, texture discipline and semantics, theme palettes, func_detail/detail entities, camera-based design review, and compile-safe GoldSrc level design.
---

# HammerTime GoldSrc Brushwork

## Overview

Build like a skilled GoldSrc mapper: read the existing map first, design a clear visual idea, make convex brushwork that compiles, texture faces deliberately, and verify the result in HammerTime before calling it finished.

## First Pass

Before creating or changing geometry:

1. Confirm HammerTime state with `hammertime_status` and `documents_list`.
2. Read these instructions with `hammertime_skill` when available so the active MCP install and the agent agree on the current map rules.
3. If the user named an available map, activate it with `documents_activate`. If they asked for a new map, create it with `documents_new`. If they did not, use the active document.
4. Read the map shape with `map_snapshot`, then sample relevant objects with `map_search`.
5. Inspect tool capability with `brush_types_list`, `editor_tools_list`, and `vertex_subtools_list` before promising a shaping method. Note the review and inspection tools too — `map_design_audit`, `texture_audit`, `texture_search` (returns width/height/aspect/flags/family), `viewport_camera_set`/`viewport_camera_get`, and `viewport_capture` (with `method`, `renderMode`, `format`) — so you plan verification, not just shaping.
6. Learn the local visual grammar: worldspawn properties, WAD list, object count, grouped brush modules, common brush entities, trim sizes, light colors, entity keyvalues, and repeated motifs.
7. Preserve the active document unless the task requires switching. If you inspect a second open map, restore the user's active map afterward.

Do not assume any study file is open. The following findings were captured from temporary HammerTime study samples while this skill was created; use them as craft guidance, not required inputs.

| Study sample | Captured craft hint |
| --- | --- |
| Dense grouped detail sample | Observed as a 6792-object, heavily grouped detail environment with Half-Life/Xen/ZHLT-style WAD use and many `func_detail` entities using `zhlt_detaillevel`, `zhlt_clipnodedetaillevel`, and occasional `zhlt_noclip`. Treat it as a lesson in dense detail being organized into parent groups and ZHLT detail entities instead of dumped into sealing world geometry. |
| Official lab-style sample | Observed as a 2248-object official-style environment with constrained draw distance, modular lab curves, brush doors, lights, sounds, triggers, `multi_manager` timing, and many `scripted_sequence` chains. Treat it as a lesson in readable scale, lab material logic, and entity choreography; the study sample does not need to be open. |
| Official large outdoor sample | Observed as a 1092-object exterior environment with a very large playable envelope, long draw distance, sky/fog setup, staged water volumes, artillery/explosion scripting, ladders, monster clips, illusionary detail, and many worldspawn terrain solids. Treat it as a lesson in outdoor scale, terraced rock silhouettes, dam/platform massing, and gameplay-volume control. |

### Captured Map Study Findings

From the dense grouped detail study pass:

- High density works because it is grouped. Parent groups hold small clusters such as trims, rails, stairs, panels, and machinery rather than scattering loose brushes everywhere.
- `func_detail` is used as a detail budget tool, not just a label. Many detail entities carry `zhlt_detaillevel` 1 or 2 plus `zhlt_clipnodedetaillevel` 1; tiny visual-only pieces may also use `zhlt_noclip`.
- Complex detail is assembled from many simple convex pieces. A representative cluster used thin 4-unit metal strips, 16/32-unit modules, sloped facets, and mirrored repeats instead of one risky custom solid.
- Face discipline matters: exposed metal faces used a consistent metal texture at 1:1 scale, hidden or nonvisible faces used `NULL`, and selected visible floor/top facets used tighter 0.5-scale material. The lesson is not "texture every face"; it is "spend texture attention only where the player can read it."
- Detail density is layered by silhouette first: long spans, repeated uprights, shallow caps, sloped insets, then small accents. The big form remains readable even when brush count is high.

From the official lab-style study pass:

- Official curves are modular and conservative. A sampled curved lab wall section was six convex solids per arc band, repeated across height bands, using a single lab wall material with aligned 1:1 texture scale and carefully phased shifts.
- The map favors readable architecture over dense decoration. Curves, corridors, lab panels, doors, and transition spaces are simple enough to navigate quickly, then animated entities carry the scene life.
- Lighting uses families: cool blue-purple lab fill, neutral white/cyan utility light, occasional red warning accents, and named groups such as office or red-light setups. Lights support zones and story beats rather than randomly brightening every corner.
- Doors are assemblies. Sliding panels, bars, rotating wheels, sounds, `lip`, speed, wait values, and `multi_manager` timing combine so a door reads as machinery rather than a single moving slab.
- Choreography is part of level design. `scripted_sequence`, `scripted_sentence`, triggers, ambient sounds, and NPC placement are arranged around readable brush spaces, making the map feel authored even when the geometry is restrained.

From the official large outdoor study pass:

- Outdoor scale is built from a few huge readable masses first: canyon envelope, dam-like concrete forms, water basins, long traversal lanes, and horizon blockers. Small rock facets support the silhouette; they do not define it alone.
- Rock formations are mostly stacked convex slabs, wedges, and clipped triangles rather than sculpted organic blobs. A representative exterior cluster used 31 world solids across roughly a 760 x 520 x 368 unit volume, with 16/32/64-unit height steps, angled side planes, triangular top faces, and repeated cap slabs to fake irregular geology while staying compile-safe.
- Terrain reads through terraces: horizontal shelves, vertical cuts, angled transition faces, and staggered depth. Keep strata mostly horizontal, then offset chunks forward/backward so cliffs do not read as a simple staircase.
- Texture use separates near walkable surfaces from cliff mass. Lower planes used 1:1 paving-style material; taller rock caps used broad 2:1 rock scale with `NULL` on hidden top/back faces. The lesson is to spend detail where the player reads contact, then use bigger rock scale for distant mass.
- Outdoor brushwork is paired with control entities. The study sample used large water brushes, ladders, broad clip/monsterclip volumes, transparent/illusionary detail, sounds, sprites, and explosions around the terrain so the huge space stayed readable and playable.
- Outdoor lighting was simple and directional: several `light_environment` entities shared a consistent sun angle/pitch with low-intensity warm daylight, while local lights were reserved for warnings, machinery, and readable gameplay cues.
- Fog, sky, and draw-distance choices are part of brush taste. Distant cliffs and dam silhouettes should simplify with range; foreground rock gets stronger silhouette, cleaner collision, and clearer material transitions.

## Design Loop

Use this loop before and during brush creation:

1. State the scene job in one sentence: what the player should read, where they should move, and what mood the space should carry.
2. Block broad masses first. Use large structural brushes for walls, floors, ceilings, cliffs, and major occluders.
3. Establish silhouette. A good GoldSrc scene reads in black-and-white: roofline, arch, cliff profile, door frame, machinery outline, or corridor bend should be clear without texture.
4. Add rhythm. Repeat trims, beams, panels, columns, lights, or cliff strata at consistent intervals, then break the rhythm at the focal point.
5. Make materials believable. Metal needs seams and supports, concrete needs mass, wood needs planks or beams, lab walls need panels and service logic, cliffs need strata and irregularity.
6. Keep negative space. Do not fill every wall with detail; leave calm areas so the important brushwork feels intentional.
7. Add one hero idea per view. A carved arch, skylight, machine, cliff face, door assembly, or light cone should carry the frame.
8. Stop when the form is readable. More brushes are not automatically more craft.

## Half-Life / GoldSrc Props And Prefabs

Use this section when studying, recreating, or inventing Half-Life / GoldSrc-style props, prefabs, and brush-made objects. Think like a 1998 Half-Life level designer: start with primitive shapes, make the silhouette readable from far away, then let low-resolution textures carry most of the surface detail.

### Prop Study Discipline

When a reference map is loaded, inspect every visible prop individually before extracting rules. Do not skip small, background, repeated, or simple-looking props. For each prop or repeated prop instance, record:

- Basic shape breakdown: the largest masses first, then trims, supports, controls, handles, fins, legs, screens, or caps.
- Main primitive forms: blocks, wedges, clipped boxes, low-sided cylinders, cones, pipes, arches, grates, and thin plates.
- Texture style: material family, color blocks, scale, alignment, screen/button/panel art, grime, labels, warning stripes, and baked shadows.
- Real geometry versus texture-only detail: what changes the silhouette or collision, and what is only painted on.
- Rebuild method: the simplest GoldSrc brush or low-poly sequence that would reproduce the look.
- Modernization risks: smooth bevels, dense cylinders, PBR shine, clean sci-fi surfaces, high-poly curves, and micro-detail.

If a prop repeats, still inspect each instance for orientation, size, texture variant, entity behavior, and placement. Collapse repeats into a reusable rule only after you have checked the visible variations.

### Full Reference-Map Study Workflow

Use this workflow before writing rules from a loaded prefab map:

1. Sweep the whole map visually in 3D view at low zoom. Note every cluster: computer rows, crates, machinery, racks, tanks, weapons, chargers, furniture, signs, lights, and tiny surface details.
2. Sweep again from top/side/front 2D views. Repeated props that hide behind larger objects are easier to spot as separated brush clusters in orthographic views.
3. Select by local bounds or by visible cluster, then focus the viewport on each group. Do not rely only on a global screenshot; small wall plates, thin paper props, handles, brackets, and buttons are easy to miss.
4. For each visible prop instance, identify the biggest mass first. Write the prop as a primitive sentence such as "wide block base, sloped wedge control face, three thin screen plates, side posts, rear cable block."
5. Record texture families on the largest faces before deciding geometry. GoldSrc props often look detailed because a single face texture already contains screen art, vents, labels, bolts, and baked shadow.
6. Compare repeated copies. Look for scale changes, rotations, alternate front textures, functional entities, broken variants, damaged variants, and different placement context.
7. Convert repeated observations into a reusable rule only after all visible variants are checked.
8. When the prop is functional, inspect both the brush shape and the entity behavior. A `func_button`, `func_breakable`, `func_tank`, `func_recharge`, `env_spark`, or `light` may be part of the prop's read.
9. End each prop study with a build recipe: primitives, order of construction, face textures, real geometry details, texture-only details, and what to avoid.

Useful HammerTime tools for the study pass:

- Use `viewport_capture` for broad visual sweeps and focused closeups.
- Use `map_snapshot`, `map_search`, and `selection_by_bounds` to find clusters that are hard to see in one view.
- Use `face_list` on selected solids to confirm texture names, face roles, and texture alignment.
- Use `texture_search`, `texture_preview_sheet`, or `texture_browser_capture` to understand related texture families before rebuilding.
- Use `.map` text parsing only as supporting evidence. It can reveal object counts, texture names, and entity classes, but the final rule must still be checked visually.

### Prop Study Card Template

For every visible prop, fill this out mentally or in notes:

| Field | What to capture |
| --- | --- |
| Prop label | Plain role name such as "upright computer tower", "small wall charger", "rocket on cradle", or "wood crate stack." |
| Placement | Floor, wall, ceiling, table, shelf, vehicle bay, lab bench, military area, or industrial platform. |
| Bounds and scale | Approximate width, depth, height, and relation to player size. Use GoldSrc units when available. |
| Silhouette | The black-shape read from far away: block, slab, wedge, cylinder, cone, frame, rack, or stack. |
| Primitive breakdown | Largest mass first, then secondary masses, trims, supports, controls, handles, fins, legs, caps, and small plates. |
| Texture families | Main material, front face, side/back faces, caps, trims, glass/screens, hazard stripes, labels, grates, grime. |
| Geometry details | Anything that changes outline, collision, opening, function, or shadow: fins, legs, rails, thick handles, barrels, frames. |
| Texture-only details | Bolts, small seams, vents, keys, labels, scratches, dirt, shadows, serial numbers, small warning marks. |
| Entity behavior | Static, breakable, pushable, button, door, charger, turret, light, spark, beam, shooter, sound, or trigger support. |
| Rebuild recipe | Ordered brush or model steps using primitive shapes and face-specific textures. |
| Anti-modern warning | The specific thing that would ruin the look: bevels, smooth curves, glossy material, too many tiny modeled details. |

### Geometry Budget And Scale Heuristics

- Start on a coarse grid. Use 16, 32, 48, 64, 96, 128, and 256 unit proportions for main masses whenever possible.
- Tiny readable plates can be 1 to 2 units thick, but only when they sit on an existing surface. Free-standing details should usually be at least 4 units thick.
- Trim strips, lips, rails, handles, and frame pieces usually read well at 4, 8, or 16 units.
- Small tabletop props commonly fit 16 to 64 units across. Crates and boxes often sit at 32, 48, 64, or 96 unit sizes.
- Desks, console banks, cabinets, and lockers usually occupy 64 to 192 units in width or height, with strong rectangular proportions.
- Large machinery, vehicles, train cars, tanks, and rocket assemblies can span hundreds of units, but they still decompose into simple repeated modules.
- Use 6-sided cylinders for background pipes, small rods, and distant barrels.
- Use 8-sided cylinders for most close barrels, rockets, wheels, and pipe props.
- Use 12-sided cylinders only for hero cylinders, large tanks, or pieces the player will inspect closely.
- Avoid 16+ sides unless the original reference clearly needs it and the brush count remains acceptable.
- A detail should become real geometry when it affects silhouette, collision, player interaction, or a major shadow line.
- A detail should stay texture-only when it is flat, smaller than a few units, repeated many times, or only visible as surface information.
- Do not stack dozens of micro-brushes to imitate modern modeling. If the texture can draw it clearly, the texture should carry it.

### Texture Family Reading

Treat texture names as part of the design language. They usually tell the AI what the prop is and which face role the texture expects:

- `CRATE`, `BCRATE`, `XCRATE`, `C1A1_CRATE`, and `C3A1_CRATE` imply boxes with side, lid, label, and hazard variants.
- `FIFTIES_CMP`, `LAB1_COMP`, and `+0~LAB1_CMP` imply computer panels, screens, key grids, and monitor walls.
- `RECHARGE`, `MEDKIT`, and charger-like names imply thin wall-mounted functional slabs with black or metal trim.
- `TANK`, `SILO`, `BARREL`, `FUEL`, and `OUT_TNT` imply cylindrical or boxy industrial storage with printed hazard information.
- `TRK`, `TREAD`, `TIRE`, `RIM`, `CAB`, and `HOOD` imply vehicle pieces. Use broad vehicle masses and let the texture draw mechanical detail.
- `ROCKET`, `MISSILE`, `NOZ`, `NOSE`, `STAGE`, and `PAYLOAD` imply a staged cylinder/cone prop with face-specific body, nose, nozzle, and banding textures.
- `STEEL`, `DRKMTL`, `BLACK`, `METAL`, and border textures imply trims, frames, dark backs, caps, and support pieces.
- `{GRATE`, `{RAIL`, fence, ladder, and grate textures imply masked detail. Use real geometry for outer frames, not for every opening.
- `STRIPES`, hazard, warning, label, and sign textures should be placed where function justifies them: pinch points, blast areas, doors, launch rails, explosive boxes, and moving machinery.
- `GLASS`, `GLASS_DARK`, screen, and emissive textures need flat framed surfaces and often a matching light or render setting.

Texture selection rules:

- Pick the face texture before finalizing the face size when the texture is a front, screen, sign, door, crate side, or vehicle side panel.
- Keep side and back faces believable. A detailed front with random concrete on the sides reads like a pasted texture, not a prop.
- Use cap textures for cylinder ends, crate tops, barrel lids, pipe ends, and rocket nozzles.
- Rotate and align textures per face. GoldSrc authenticity often depends more on face alignment than on geometry count.
- Prefer one strong texture family per prop, with one or two supporting materials. Too many unrelated materials make the object read as generated clutter.

### Overall Prop Style

- Build low-poly, blocky, and brush-friendly. Props should look authored from convex brushes or simple model pieces, not sculpted subdivision meshes.
- Prefer simple silhouettes: one readable body, one or two secondary masses, then a few trims or functional protrusions.
- Use boxes, wedges, clipped boxes, low-sided cylinders, cones, pipes, thin plates, rails, and repeated modules.
- Keep curves faceted. A 6, 8, or 12 sided cylinder usually reads better for GoldSrc than a smooth 32+ sided tube.
- Let textures do most of the detail: vents, screws, labels, grime, panel seams, screen glow, keypad patterns, bolt rows, and scratches.
- Use chunky proportions and hard edges. Thin decorative pieces should be 2, 4, 8, or 16 units when they need to exist at all.
- Fit the mood: industrial, military, underground facility, laboratory, utility storage, worn office, and practical maintenance hardware.
- Keep props late-1990s Half-Life in taste. They can be clever and recognizable, but they should not become modern cinematic assets.

### Brush Construction Principles

- Break every object into primitive masses first. Name the big forms mentally before adding details: body, base, cap, side pods, frame, handle, screen, muzzle, fin, leg.
- Make the silhouette work without texture. If the black shape is unreadable, fix the large brushes before adding trims.
- Use wedges and clipped brushes for fins, ramps, sloped console faces, angled covers, pipe brackets, and chamfer-like panels.
- Use real geometry for silhouette, gameplay contact, open holes, rail openings, thick handles, big buttons, fins, legs, barrels, wheels, and anything that should cast a visible profile.
- Use texture-only detail for screws, small bolts, tiny vents, warning labels, keypad grids, panel lines, seams, scratches, dirt, baked shadow, and small edge wear.
- Avoid dense beveling. One blocky inset, one side trim, or one clipped corner is enough for many props.
- Avoid carving as a modeling habit. Build separate convex pieces instead.
- Tie decorative assemblies to `func_wall`, `func_detail`, `func_breakable`, `func_pushable`, or the appropriate functional brush entity when the prop behavior requires it. Do not let detail brushes become sealing world geometry.
- Use clean collision separately when a detailed silhouette would snag the player.

### Texture Principles

- Use low-resolution GoldSrc material families: metal, concrete, plastic, painted steel, wood, cardboard, hazard stripe, glass, screen, keypad, vent, crate, grime, label, and panel textures.
- Prefer textures with baked-in lighting: dark edges, stains, dirt, rust, scratches, painted seams, printed labels, and simple iconography.
- Align textures by face role. Front screens, crate sides, caps, labels, and doors need face-specific fitting; sides and backs need believable edge or backing materials.
- Choose brush dimensions around important textures when possible. A 64x96 screen or 48x128 computer panel should drive the brush face size.
- Keep colors muted and utilitarian: gray, black, olive, dull green, off-white, tan, brown wood, red warning, yellow-black hazard, blue/cyan monitor glow.
- Avoid modern PBR assumptions: no roughness/metalness storytelling, glossy realism, perfectly clean materials, or tiny normal-map-like detail.

### Do / Don't

| Do | Don't |
| --- | --- |
| Build the object from a few strong primitives first. | Start by adding bolts, bevels, and tiny panels. |
| Use faceted cylinders and cones for tanks, pipes, barrels, rockets, and wheels. | Use smooth high-poly curves or subdivision-like shapes. |
| Let a screen, label, grate, or panel texture fake complexity. | Model every screw, wire, vent slot, and seam as geometry. |
| Use wedges for sloped consoles, fins, ramps, and angled covers. | Use modern sleek sci-fi curves unless requested. |
| Keep props chunky, practical, worn, and functional. | Make props glossy, pristine, cinematic, or luxury-designed. |
| Reuse modules and texture families across related props. | Mix random materials that ignore object function. |
| Verify texture alignment in the viewport. | Trust a texture name or numeric face data without looking. |

### Prop-By-Prop Observations From `Hlprefabs.map`

The loaded reference map is a dense Half-Life prefab library. A parsed pass found thousands of brush solids and hundreds of separated prop components, including computer/console clusters, crates, office furniture, fuel containers, industrial structures, vehicles, rockets, turrets, chargers, glass/light props, and many small utility details. Use the following observations as reusable craft rules:

- Single-brush computer tower modules: tall 32x72x96-ish boxes use `FIFTIES_CMP*` textures on each face. Geometry is just a rectangular prism; screens, vents, seams, and side panels are entirely texture work. Recreate with one block sized to the texture set, not a modeled case.
- Repeated upright computer panels: similar block dimensions appear in rows with rotated texture variants. The lesson is modular reuse: same box, changed face texture, changed orientation.
- Long lab computer bank: a 320-unit-wide assembly combines a base block, thin side posts, top caps, vertical dividers, face-sized `LAB1_COMP*` and `+0~LAB1_CMP*` screens, and spark/beam helper entities. Geometry frames the monitor wall; the monitor grids and green/cyan data are painted.
- Broken/sparking computer banks: use the same blocky console grammar but add `env_spark`, `env_beam`, `info_target`, or named triggers. The prop still reads from the physical frame; effects are extra scene life.
- CRT/screen boxes: small monitor props are squat blocks with a dark or glowing screen texture on the front, black/dark metal sides, and a simple base. Do not model curved glass.
- Keypads and button panels: tiny plates or `func_button` brushes use animated/button textures. Geometry is usually one thin rectangle; individual keys are texture-only unless the button must move.
- Wall chargers and med stations: health and suit chargers are thin wall slabs, often 8 to 32 units deep, with `+0MEDKIT`, `RECHARGE*`, black edges, and functional brush entities. Keep them flat and texture-driven.
- File cabinets: tall rectangular bodies with drawer textures and occasional `func_door` pieces. Handles, labels, and drawer separations are mostly painted; only opening drawers need separate brushes.
- Desks and tables: broad rectangular tops, block legs or side slabs, and simple trim. Wood or office textures establish detail; do not overbuild screws and drawer pulls.
- Bookcases and shelves: box frames hold many thin book blocks and paper slabs. Some books are angled with clipped/rotated small brushes, but the count stays readable because each book is a simple cuboid or wedge.
- Papers, folders, posters, and maps: thin 1 to 2 unit plates with paper textures. Use them as cheap storytelling accents on desks and walls.
- Trash cans and small bins: short faceted or boxy containers using dedicated trash textures. The rim may be geometry; dents and contents are texture-only.
- Sofas and chairs: chunky cushions are block stacks with worn fabric textures. Armrests and backs are simple cuboids; fabric pattern and grime carry comfort/detail.
- Wooden crates: one block per crate when texture art already includes planks and bracing. Use face-specific crate side/top textures; do not model every plank unless the silhouette needs broken boards.
- Black Mesa supply crates: `BCRATE*`, `CRATE*`, and `XCRATE*` families appear in several sizes. Let texture variants define side, lid, hazard, and label roles.
- Cardboard/freezer boxes: simple 32 or 64 unit boxes with cardboard and label textures. Repeated small boxes matter because they teach scale and clutter density.
- Ammo boxes: compact rectangular blocks with `AMMO*` textures. Use one block unless the lid opens or the handle must silhouette.
- Metal crates and lockers: rectangular bodies with darker trim and panel textures. Add only large handles or doors as geometry.
- Barrels: low-sided cylinders with cap and side textures. Use 8 to 12 sides, cylindrical side projection, cap textures on top/bottom, and no smooth bevels.
- TNT, fuel, and explosive containers: boxes or short cylinders with red/yellow warning textures. The warning label is texture work; the silhouette stays simple.
- Large tanks/silos/cylindrical machinery: faceted cylinders, pipe stubs, rectangular supports, and large labels or banding textures. Use side count based on viewing distance.
- Pipes and conduit runs: low-sided cylinders or rectangular pipe proxies, supported by simple brackets. Use texture seams and bands to suggest flanges instead of many ring brushes.
- Grates and cages: thin frames with `{GRATE*`, `{RAIL*`, or fence textures. Use real geometry for outer frame and thick supports; use masked textures for dense mesh.
- Rails, ladders, and beams: repeated bars at even spacing, built from thin cuboids or simple cylinders. Keep spacing readable and collision simple.
- Industrial platforms and racks: block frames, gridded floors, vertical posts, and hazard stripes. The rack is usually a few rails and plates; the texture supplies grime and perforation.
- Hazard-striped mechanisms: yellow-black stripes appear on barriers, clamps, and danger edges. Use them as readable warnings, not decoration everywhere.
- Doors, panels, and shutters: large flat slabs with trim strips and face-sized door textures. Use separate brush entities only for moving parts.
- Vending/soda machine: tall rectangular body, front display/breakable plate, column of small button brushes, and `env_beverage`/`env_shooter` behavior. The brand, slots, lights, and cans are mostly texture/entity work.
- Lab apparatus and glass: simple cylinders or boxes under glass/dark translucent faces. Use `GLASS_DARK` or `GLASS_BRIGHT` with functional render settings; avoid curved transparent shells unless necessary.
- Tesla/laser apparatus: blocky metal base, vertical rods or supports, named `func_tanklaser`, beams, lights, and sounds. Geometry reads as a crude machine; energy is entity-driven.
- Mounted guns and turrets: small `func_tank`, `func_tanklaser`, `func_tankmortar`, or `func_tankrocket` assemblies with box receivers, thin barrels, simple pivots, and black/metal textures. Do not model modern weapon internals.
- M60/M82-style gun prefabs: long thin barrel blocks, simple receiver boxes, small handles, and control brushes. Texture and entity parameters sell the weapon more than geometry.
- Mortar and cannon props: short stout barrels, chunky base plates, simple angled supports, and dark metal textures. Use faceted cylinders and wedges.
- Rocket props: long faceted cylinder or stacked cylinder sections, cone nose, simple nozzle, blocky fins, and `ROCKET*` textures. Details such as separation bands and warning marks are mostly texture.
- Rocket launcher / tankrocket platform: broad blocky platform, rails, grates, chunky supports, and a barrel/launcher body. Use entity behavior for firing; keep the visual launcher primitive.
- Tank and military vehicle: broad hull blocks, tread texture strips, simple turret block, faceted barrel, and texture-painted hatches. Real geometry defines hull, turret, treads, and cannon silhouette; bolts, panels, and camo are texture-only.
- Bradley/truck parts: wheels are low-sided cylinders or texture planes using `TRK_TIRE`/`TRK_TREAD`; cabs are clipped boxes; canvas and doors are rectangular texture panels.
- Subway/train car: very large boxy vehicle shell with long side textures, repeated windows/panels, rectangular undercarriage, and simple roof/floor masses. Do not curve the car body heavily.
- Storage racks and shelves: cuboid posts and horizontal slabs. Items on shelves can be repeated boxes, bottles, or paper plates; vary texture, not geometry.
- Clocks, signs, wall maps, and targets: thin plates with face art. Only frame thickness should be geometry.
- Small lights and emergency fixtures: small boxes or cylinders with emissive textures and a matching light entity. The fixture motivates the light.
- Glass panes and windows: thin `func_wall` or world plates with glass textures and simple frames. Avoid modeling thickness beyond readable edges.
- Miscellaneous machinery: combine one main block, one cylindrical or wedge element, and one panel/screen/vent texture. If it cannot be named functionally, simplify until it reads as maintenance equipment.

### Category Rules

| Category | Construction rule | Texture rule | Avoid |
| --- | --- | --- | --- |
| Computers, monitors, servers, towers | One block or stacked blocks; add frame strips only for silhouette; screens are flat faces. | Use `LAB1_COMP*`, `FIFTIES_CMP*`, black edges, green/cyan screens, vents, and buttons. | Curved CRT glass, high-poly keyboards, tiny modeled ports. |
| Consoles, buttons, screens, keyboards | Sloped wedge console plus thin screen/button plates; make only important buttons real `func_button` brushes. | Key grids, labels, waveform screens, warning lights, and panel seams are painted. | Modeling every key or LED. |
| Tanks, barrels, containers, fuel | Faceted cylinders or boxes with simple caps, bands, and supports. | Side labels, hazard marks, seams, dirt, and cap art do most detail. | Smooth barrels, bevel-heavy caps, glossy fuel tanks. |
| Rockets, missiles, cylindrical machines | Low-sided cylinder body, cone nose, block fins, nozzle block/cylinder, and simple support stand. | Bands, seams, bolts, warning labels, and stage lines are texture work. | Sleek modern sci-fi missiles or high-poly military replicas. |
| Pipes, vents, rails, beams, supports | Repeated cuboids, 6 to 12 sided cylinders, grates as masked textures, brackets at intervals. | Metal panels, grates, rust, grime, and hazard stripes establish detail. | Modeling every grate opening or flange bolt. |
| Crates, cabinets, lockers, boxes | Single block for intact boxes; extra brushes only for open lids, broken boards, handles, or doors. | Face-specific side/lid/front textures; align each face. | Plank-by-plank construction when texture already has planks. |
| Furniture and office clutter | Cuboid tops, side slabs, block legs, cushion blocks, and thin paper plates. | Wood, fabric, paper, file labels, and stains carry detail. | Rounded modern furniture or high-detail upholstery. |
| Military and vehicle objects | Big hull first, then turret/cab/wheels/treads/barrel; keep broad silhouette. | Camo, hatches, panels, tread links, and wheel detail are texture-driven. | Smooth car bodies, modern suspension detail, dense wheel geometry. |
| Lab and industrial machinery | Block base, panel face, cylinder/pipe accent, rails, vents, small screen. | Lab panels, warning labels, dirty metal, screen glow, and baked shadows. | Clean futuristic machinery with complex curves. |

### Detailed Category Recipes

Computers, monitors, servers, and towers:

1. Start with one vertical or horizontal box sized to the main computer texture.
2. Add a dark rear or side material, then put the screen or panel texture only on the visible front.
3. Add a base block, side trim, or top cap only if it changes the silhouette.
4. Use texture-only vents, screws, drive slots, keyboard keys, warning lamps, and cable sockets.
5. For monitor banks, repeat identical modules with slight texture variation rather than making every screen a unique shape.
6. For damaged or active computers, add `env_spark`, `env_beam`, `light`, or sound entities; do not make the computer itself high-poly.

Consoles, buttons, screens, panels, and keyboards:

1. Build a rectangular base block, then clip or wedge the control face to a readable angle.
2. Place screen textures on flat plates or directly on the sloped face.
3. Make only the large player-facing switch, lever, or button into real geometry or `func_button`.
4. Keep small buttons, key grids, indicator lights, and labels in the texture.
5. Add dark trim strips around panels when needed for readability.
6. Avoid sleek curved control surfaces. GoldSrc consoles should feel fabricated from sheet metal boxes.

Tanks, barrels, containers, and fuel objects:

1. Choose box, horizontal cylinder, vertical cylinder, or capsule-like stacked cylinders as the primary form.
2. Use 8 or 12 sides for close barrels and tanks; use 6 or 8 sides for background cylinders.
3. Put real geometry into large caps, supports, handles, feet, and pipe stubs.
4. Put bands, dents, warning text, grime, fluid labels, and small bolts in the texture.
5. If the object is explosive or hazardous, use warning textures sparingly but visibly.
6. Avoid glossy modern fuel tanks and perfectly smooth pressure vessels.

Rockets, missiles, and cylindrical machinery:

1. Build the body from one or more aligned faceted cylinders.
2. Add a simple cone nose and a short nozzle or dark rear cap.
3. Use wedge fins or triangular brush fins, thick enough to survive GoldSrc scale.
4. Add a cradle, rail, platform, or support frame if the rocket is staged in the environment.
5. Let stage lines, serials, warning labels, rivets, and panel seams come from textures.
6. Keep the form military-industrial, not sleek aerospace concept art.

Pipes, vents, rails, beams, supports, and industrial structures:

1. Decide whether the object is a pipe, a duct, a rail, a beam, a bracket, or a platform before building.
2. Use cylinders for round pipes and cuboids for square ducts, rails, I-beam approximations, and brackets.
3. Use repeated supports at readable intervals rather than a dense realistic fastening system.
4. Use masked grate textures for mesh and perforation.
5. Make outer frames, large crossbars, and handrails real geometry.
6. Paint rust, seams, rivets, perforation, and grime through textures.

Crates, cabinets, lockers, boxes, and storage props:

1. Start with one rectangular box. Most intact crates should remain one brush or one simple model piece.
2. Apply face-specific side, top, front, and label textures.
3. Add real geometry only for open lids, broken planks, thick handles, heavy doors, wheels, or stacked pallets.
4. Use repeated sizes to establish world scale: small ammo boxes, medium supply crates, large shipping crates.
5. Vary texture orientation and stacking, not polygon density.
6. Avoid modeling every plank, nail, hinge, or drawer line if the texture already contains it.

Office, lab furniture, shelves, books, and papers:

1. Use slab tops, side panels, block legs, and boxy cushions.
2. Keep desks, shelves, sofas, and chairs chunky, with square cushions and hard-edged arms.
3. Use thin plates for papers, folders, maps, wall signs, and posters.
4. Use small cuboids and wedges for books, but keep each book primitive.
5. Let wood grain, fabric wear, paper print, and labels come from textures.
6. Avoid rounded modern furniture and detailed upholstery.

Military vehicles, tanks, train cars, and truck parts:

1. Block the hull or car body as a large rectangular mass first.
2. Add major silhouette pieces: turret, cannon, treads, wheels, cab, bumper, bed, roof, or undercarriage.
3. Use low-sided cylinders or texture planes for wheels depending on distance.
4. Use tread, tire, hatch, window, door, and camo textures to fake mechanical density.
5. Keep windows and side panels flat with repeated texture modules.
6. Avoid modern vehicle modeling habits like curved body panels, detailed suspension, and high-poly tires.

Turrets, guns, launchers, and defensive machinery:

1. Build a receiver block, barrel cylinder or cuboid, pivot block, and base plate.
2. Use wedges for angled supports and simple shields.
3. Keep barrels simple and low-sided; add muzzle detail only if it changes the silhouette.
4. Use `func_tank`, `func_tanklaser`, `func_tankrocket`, or related behavior for function.
5. Use black metal, dark steel, hazard, and control textures.
6. Avoid modeling real weapon internals or modern tactical accessories.

Small lights, chargers, signs, panels, and wall details:

1. Build as thin plates, small boxes, or short cylinders mounted to a surface.
2. Use face art for icons, labels, screens, charge meters, warning marks, and lens details.
3. Add a matching `light`, `env_sprite`, `func_recharge`, `func_healthcharger`, or button behavior when the detail is functional.
4. Keep frames simple and thick enough to read.
5. Avoid treating every wall decal as a sculpted object.

### GoldSrc Authenticity Checks

Before calling a prop finished, ask:

- Can the object be understood in one second from a medium distance?
- Does the silhouette still read if all textures are mentally replaced with black material?
- Could the visible geometry be built from convex Hammer brushes or a simple low-poly model?
- Are the smallest repeated details painted instead of modeled?
- Are curves faceted enough to feel late-1990s?
- Does every material choice have a practical role?
- Are hazard stripes, labels, screens, and lights used where they communicate function?
- Would the object fit in Black Mesa, an industrial site, a military depot, a train/subway area, or an underground facility?
- Is the brush/entity split sensible for gameplay and compile health?
- Did any part drift into modern PBR, concept-art sci-fi, ultra-clean lab design, or high-poly realism?

### Anti-Modernization Failure Modes

- Too smooth: reduce cylinder sides, remove bevel loops, and return to chunky caps and hard edges.
- Too detailed in geometry: collapse bolts, seams, vents, keys, screws, and labels into texture work.
- Too glossy: switch to flat painted metal, dull plastic, grime, baked shadows, and worn edges.
- Too sci-fi: replace sweeping curves with rectangular housings, wedge panels, exposed supports, and utilitarian labels.
- Too random: choose one main texture family and one trim family, then align every face to its role.
- Too clean: add dirty, worn, stained, scratched, or faded texture variants rather than more polygons.
- Too noisy: remove small brushes that do not change silhouette or function.
- Too fragile for GoldSrc: thicken thin parts, simplify collision, and avoid brushwork that creates slivers or invalid solids.

### Detailed Rocket Construction Example

1. Choose the pose first: vertical display rocket, horizontal missile on cradle, rocket in a launch tube, or stored rocket body on a rack.
2. Block the main body as a simple faceted cylinder. Use 8 sides for most rockets, 12 sides only for a close hero rocket, and 6 sides for small background missiles.
3. Keep the body length exaggerated enough to read from far away, but not so thin that the brushes become awkward. A squat military-industrial rocket is more Half-Life than a sleek modern missile.
4. Add a cone nose with the same side count as the body. Make it a simple angular cap, not a smooth ogive.
5. If the rocket has stages, use two or three cylinder sections with matching side counts. Let the texture draw most separation lines.
6. Add a rear nozzle from a short dark cylinder, cone, or blocky inset. The nozzle opening can be a dark cap texture instead of nested geometry.
7. Add three or four fins from wedge or triangular brush shapes. Make each fin thick, flat-sided, and readable from side view.
8. Put fins at cardinal or evenly spaced directions. Perfect aerospace correctness matters less than a clear GoldSrc silhouette.
9. Add one or two real band brushes only when they change the outline or help divide a large plain body. Otherwise use texture-painted rings.
10. Use `rocketmain`, `rocketnose`, `rocketstage`, `rocketpayload`, `rocketsep`, `rocketbottom`, or similar texture families when available.
11. Assign face roles deliberately: body sides get body/stage textures, nose faces get nose texture, rear cap gets nozzle texture, fins get dull metal or warning trim, cradle gets steel or grate.
12. Paint panel lines, seams, bolts, serial labels, warning labels, stage breaks, red markings, and yellow-black hazard stripes mostly through textures.
13. Use muted military or industrial colors: gray, olive, dark green, off-white, dirty white, red markings, black metal, or yellow-black hazard stripes.
14. Add a simple cradle, rail, or support stand if the rocket is displayed. Build it from block beams, vertical posts, grates, and brackets.
15. If the rocket is functional, separate visual prop logic from firing behavior. Use `func_tankrocket`, triggers, targets, sounds, smoke, beams, sprites, or scripted effects as needed.
16. Keep collision simple. A box or simplified hull around the stand is often better than player collision on every fin and brace.
17. Verify from a distance that body, nose, fins, nozzle, and support read before inspecting texture detail.
18. Avoid sleek sci-fi profiles, high-sided curves, complex bevels, dense rivets, realistic missile internals, glossy materials, tiny stabilizers, and modern aerospace panel density.

### AI Generation Rules

- Think "brush prefab in a 1998 Half-Life map" before thinking "3D asset."
- Build from simple shapes first, then refine only the silhouette.
- Use a study-card mindset: label the prop, identify primitive masses, choose texture families, then decide geometry versus texture.
- Recreate the reference's simplification strategy, not just its subject matter. A GoldSrc rocket, tank, or computer should show how it was reduced for the engine.
- Decide the material family before adding details.
- Fake complexity with texture alignment, baked grime, labels, vents, screens, and hazard stripes.
- Keep props chunky, practical, worn, and functional.
- Match the Half-Life mood: industrial, military, laboratory, underground, utilitarian, slightly dirty, and readable under low-res lighting.
- Never generate polished modern cinematic assets unless the user explicitly asks for that style.
- When asked for high fidelity, increase accuracy through proportions, texture choice, and silhouette, not through modern polygon density.
- If uncertain, remove geometry and improve face textures first. Add geometry back only when the silhouette, gameplay, or function requires it.
- Prefer repeated modules and variants over unique sculpted shapes.

### Prompt Snippets

Use or adapt these snippets when asking an AI to generate or recreate GoldSrc-style props:

- "Create a Half-Life / GoldSrc-style brush-made [prop]. Use simple convex blocks, wedges, and low-sided cylinders. Make the silhouette readable first, with late-1990s low-resolution industrial textures carrying vents, labels, scratches, grime, and panel lines."
- "Recreate this as if it were built in Hammer for GoldSrc: list the major primitive shapes, which details are real geometry, which details are texture-only, and what texture families should be used."
- "Make a worn Black Mesa utility prop. Keep it chunky, low-poly, brush-friendly, and practical. Avoid modern bevels, PBR materials, smooth sci-fi curves, and high-poly surface detail."
- "For every visible prop in the reference, produce a row: name, silhouette, primitives, texture family, geometry details, texture-only details, rebuild steps, and what would make it look too modern."
- "Design a GoldSrc rocket prefab: faceted cylinder body, cone nose, blocky wedge fins, simple nozzle, muted military colors, texture-painted seams, warning labels, bolts, and hazard bands."
- "Inspect this reference-map prop like a Half-Life mapper: describe its bounds, placement, primitive masses, face textures, entity behavior, real geometry, texture-only detail, and the simplest brush rebuild."
- "Turn this modern-looking [prop] into an authentic GoldSrc prefab. Reduce smooth curves, remove bevel density, keep only silhouette geometry, and move bolts, vents, labels, scratches, and panel seams into low-resolution textures."
- "Create a prop study table for a GoldSrc prefab library. Include every visible small prop and repeated instance; do not merge repeats until orientation, scale, texture variant, and behavior are checked."
- "Give me a Hammer brush recipe for [prop]: ordered primitives, approximate unit sizes, cylinder side counts, face texture families, texture alignment notes, functional entities, and anti-modern mistakes to avoid."
- "Critique this prop for Half-Life authenticity: identify any high-poly geometry, modern material assumptions, over-modeled details, weak silhouette, bad texture alignment, or non-utilitarian design choices."

## Brush Workflow

Use grid-first construction:

- Use 64/128 unit thinking for broad architecture, 16/32 for trims and supports, and 1/2/4 only for final alignment or tiny seams.
- Keep structural sealing brushes simple, snapped, and thick enough to see mistakes. Do not make seal hulls from decorative brush entities.
- Prefer native primitives over stepped block approximations: `brush_create_arch` for arches, `brush_create_cylinder` for round columns/turns, `brush_create_pipe` for tubes/rings, `brush_create_torus` only when a true torus is worth the face cost.
- Prefer `clip_preview`, `clip_apply`, and `clip_split` for clean cuts. Use clipping before vertex manipulation when a plane cut can do the job.
- Use vertex manipulation for controlled convex shape changes: slopes, tapered beams, cliff facets, custom trim ends, asymmetric rocks, or one-brush angled corners.
- Split faces only when the added vertices serve a clear shape. After face splitting or vertex moves, validate immediately.
- Build ornate round or arched details from several simple convex segments when one custom brush becomes fragile.
- Tie decorative brushwork to `func_detail` or `func_wall` when it should not cut world polygons or VIS like structural worldspawn. Keep sealing geometry in worldspawn.
- For collision, separate what the player touches from what the eye sees: use clean clip/collision brushes and let visible detail be lighter.
- After creation, use `selection_set` and `viewport_focus` so the user can see the result.

### Prop Construction & Human Scale

Build props to real-world scale (at texture scale 1, 1 unit is roughly 1 inch) and from enough brushes that they read as objects, not boxes. The player hull is 32x32x72 standing.

**Detail by default — do not be lazy.**

- Default to DETAILED props. The construction patterns below are FLOORS, not targets — hitting the minimum brush count and stopping is lazy work and reads as such. Ship the detailed version unless the user explicitly asks for low-poly/blocking.
- Every prop gets at least one signature detail beyond its base shape (armrests, tap, trim, hardware, clutter).
- **Recognizability rule:** a prop must be identifiable at a glance from silhouette plus one signature detail. If you cannot make it recognizable with available textures and brushes, redesign it or omit it — an ambiguous shape is worse than nothing. (Real failure: a water cooler built as white cylinder + dark cylinder read as "unidentifiable object"; it lacked tap, cup dispenser, and a translucent bottle.)

Human-scale dimension table (units):

| Element | Size (u) |
| --- | --- |
| Seat height | 16-20 |
| Seat depth | 20-24 |
| Table / desk top | 28-36 |
| Counter / bench work surface | 36-44 |
| Backrest top | 40-56 |
| Doorway (person) | 64-96 wide x 96-128 tall |
| Corridor width | >=96 |
| Walkable ceiling | >=108 (128 typical) |
| Step rise | <=16 |
| Railing height | 36-40 |
| Desk-scale device (monitor, terminal, small appliance) | 16-24 |
| Appliance / machine >=96 tall | industrial / lab scale only |

Construction patterns (minimum silhouette so props don't read as boxes):

- Office / task chair: seat + back + 2 armrests + 4 legs or pedestal + base plate — 8-10 brushes. Armless single-pedestal chairs read as lazy kids' furniture.
- Conference / dining chair: seat + back + 4 legs minimum, plus armrests where space allows.
- Table: top + 4 legs or 2 pedestals (+ apron).
- Desk: table + side panels / modesty panel + drawer-front strip.
- Counter: body + recessed kick + overhanging worktop lip.
- Shelf unit: 2 uprights + individual shelf boards.
- Couch / bench: seat + back + 2 armrests.

Rules:

- Furniture-sized props need >=6 brushes and at least one overhang or recess. Single-brush props are for crates and boxes only. A 3-brush chair (seat, back, one pedestal) reads as toy furniture.
- Free-standing props are seen from every side; finish every face. Do not leave a visible side on `NULL` or bare default texture.

Device props are built from their device art:

- When the WAD has dedicated art for a device (monitor faces, control panels, appliance fronts), that art IS the device's front face, fitted aspect-true — do not build a generic box with a tiny inset pane. (Failure: monitors built as big plain boxes with a 12x10 screen inset "looked like voxel art, not a computer"; halflife.wad has full 64x48 monitor-face textures with bezels, e.g. the ~lab_crt10 family, that should be the whole front.)
- Shape the shell like the device: monitor = slimmer front bezel + deeper back box (2 boxes min) + neck/stand; keyboard = thin wedge, key-art texture if one exists.
- NEVER use strongly patterned/striped tiles (e.g. door-header tiles like `*_dr1h`) as prop shell material — the banding reads as corrugated siding. Shells use flat/neutral textures; preview shell candidates at >=192px like any prop texture.

### Tool Recipes

| Task | HammerTime tools |
| --- | --- |
| Read map state | `hammertime_status`, `hammertime_skill`, `documents_list`, `documents_new`, `map_snapshot`, `map_search` |
| Confirm geometry options | `brush_types_list`, `editor_tools_list`, `vertex_subtools_list` |
| Create primitives | `brush_create_block`, `brush_create_wedge`, `brush_create_arch`, `brush_create_cylinder`, `brush_create_pipe`, `brush_create_cone`, `brush_create_torus` |
| Shape geometry | `clip_preview`, `clip_apply`, `clip_split`, `vertex_snapshot`, `vertex_move`, `vertex_split_face`, `vertex_triangulate` |
| Make brush entities | `entity_tie_brushes`, `entity_untie_brushes`, `entity_update` |
| Discover textures | `texture_search` (width/height/aspect/flags/family, `groupFrames:true`), `texture_preview_sheet` (paginated `offset`/`page`), `texture_browser_capture` |
| Texture safely | `texture_apply_smart`, `texture_apply`, `texture_project`, `texture_align_face`, `texture_copy_from_face`, `texture_replace`, `face_list`, `face_texture_set` |
| Review design and texture | `map_design_audit`, `texture_audit` |
| Validate and show | `viewport_capture`, `viewport_camera_set`, `viewport_camera_get`, `map_validate`, `problems_check`, `selection_set`, `viewport_focus`, `overlay_set` |

### External Tool Awareness

Use HammerTime as the editor of record. External GoldSrc utilities are supporting evidence or pipeline helpers, not proof that brushwork is good.

- Before relying on an outside utility, confirm it is installed, available, or explicitly provided by the user.
- Treat compile tools as a staged diagnostic pipeline. CSG/BSP/VIS/RAD-style failures point at different problems: bad solids, hull issues, visibility/leaks, or lighting inputs. Read the log stage and exact problem before changing geometry.
- Use BSP viewers, model/material browsers, entity editors, WAD/texture extractors, packagers, and dependency archivers for inspection or release hygiene. Do not trust decompiled brushwork as authored-quality geometry without cleanup; rebuild suspect planes as clean convex solids.
- Use unit converters, FGD helpers, and lightstyle helpers to keep scale and entity metadata consistent, but still read the active map's local scale, FGD schema, and lighting style.
- Terrain generators and surface tools can suggest a silhouette or height pattern. Final GoldSrc terrain still needs convex, grid-aware, texture-disciplined solids that validate in the editor.
- If using a compiled-map viewer for reference, extract lessons about scale, silhouettes, texture rhythm, and entity choreography rather than copying broken decompiler output.

## GoldSrc Rules

Follow these rules unless the user explicitly asks for experimental geometry:

- Every solid must remain convex. Concave brushwork, twisted faces, non-planar faces, and extremely thin custom solids are compile risks.
- Every face must remain planar. If a vertex move creates a twisted quad or a face whose vertices no longer share a plane, split/rebuild the brush instead of forcing it.
- Do not use carving as a default modeling method. Carving can create messy hidden cuts, invalid solids, and excessive world polygon splits.
- High-sided cylinders, pipes, arches, and tiny round primitives spend face budget quickly and can become fragile when scaled too small. Use the lowest side count that reads correctly at player distance.
- Entity brushes do not seal the map. `func_wall`, `func_door`, `func_detail`, water, glass, grates, ladders, and other brush entities must sit inside sealed world geometry.
- Leaks are map-breaking. A leak can stop VIS, ruin bounced lighting, cause fullbright behavior, and make the engine render far too much.
- Point entities must live inside sealed valid space.
- Visible textured world brushes that touch each other can split polygons and raise `wpoly`. Use clean structure, thoughtful `func_detail`/`func_wall`, and texture scale to control this.
- Non-model point entities do not add epolys, but models, monsters, items, sprites, and visible brush entities can affect render cost.
- Do not claim you can run in-game console/debug commands from HammerTime. For performance and leak debugging, use compile logs, pointfiles, object counts, visible brush/entity density, selected bounds, face inspection, HammerTime validation, and any game-side debug output only if the user provides it.
- Dynamic/animated light styles are expensive in view-heavy areas. Use them sparingly and only where the effect matters.
- For large outdoor spaces, plan occlusion, fog/sky mood, clip/monsterclip control, water volume boundaries, and long sightline simplification as part of the brush design rather than as cleanup after detail is built.
- If a brush validates as `InvalidSolid`, do not keep patching blindly. Delete or simplify it, rebuild from safer convex primitives, and validate again.

## Texture Semantics

GoldSrc texture names encode engine behavior. Read the prefix and flags before applying, and pair the texture with the entity setup it requires. Pull `width`/`height`/`aspect`/`flags`/`family` from `texture_search` first — match the texture aspect to the face aspect and never stretch exact-size art across a mismatched face.

Name conventions:

- `{` prefix (e.g. `{GRATE`, `{FENCE`): alpha-masked transparent. The palette-index-255 area is cut out, but it only shows transparency in-game on a `func_wall` or `func_illusionary` with render mode Solid and FX amount 255. On plain world brushes it renders opaque.
- `~` prefix (e.g. `~LIGHT`, `~FIFTIES_LGT`): light-emitting hint. It does not emit by itself — pair it with a texlight entry in the RAD file (or the compile texlight list) so the compiler treats the face as a light source.
- `+0`..`+9` prefix: animated frame chain. Apply the `+0` frame only; the engine cycles the frames automatically. Do not place individual frames on separate faces.
- `+A`..`+J` prefix: toggle alternates of an animated texture, switched by `func_button`/trigger state. Apply the base frame; the alternate set swaps on activation.
- `-0`..`-3` prefix: random tiling. The engine picks a variant per face to break up repetition. Apply the `-0` frame; the others are chosen automatically.
- `!` prefix or a `water`-named texture: liquid volume. Must be a `func_water` or a world water brush, inside a sealed volume, with the proper render setup to behave as water.
- `scroll`-prefixed textures: scrolling surface. Tie the brush to `func_conveyor` (and set speed) so the texture actually moves.
- `sky`: skybox faces. Draws the sky and, with a `light_environment` plus a RAD sky entry, emits directional daylight. Keep sky brushes as clean sealing world geometry.

Tool textures (compile-only, never decorative):

- `NULL`: face is removed at compile. Put it on every hidden/nonvisible face to cut wpoly.
- `CLIP`: blocks the player but is invisible. Use to smooth collision over jagged detail.
- `HINT` / `SKIP`: vis-splitting helpers. HINT forces a visleaf cut along the face; SKIP caps a HINT brush and is otherwise ignored. Use to control the PVS, not as visible material.
- `AAATRIGGER`: reserved for trigger volumes (`trigger_*`). Never a visible wall texture.
- `ORIGIN`: defines the rotation/movement origin of a brush entity (doors, rotating brushes). The ORIGIN brush is not rendered.
- `BEVEL`: like `NULL` but also suppresses clipnode expansion on that face. Use to fix collision snags on angled brushes.

Rule: before applying any named texture, check `width`/`height`/`aspect` and `flags` from `texture_search`, apply the entity/render setup the name implies, match texture aspect to face aspect, and never stretch exact-size art.

## Theme Palettes

Pick a small, coherent family set per theme instead of sampling textures at random. The families below use `halflife.wad`-style names as **examples to verify with `texture_search`** because WAD contents vary between installs. Discover a real coherent set with `texture_search` (`groupFrames:true`), preview it with `texture_preview_sheet`, then record the chosen set and reuse it.

| Theme | Wall family | Trim / accent | Floor | Ceiling | Sky / liquid / light |
| --- | --- | --- | --- | --- | --- |
| Lab / tech | `LAB*`, `CRETE*` concrete | `TNNL*`, `ELEV*`, dark metal trim | tile/`CRETE*`, darker than walls | clean `CRETE*` panel | `~LIGHT`, `~FIFTIES_LGT` fixtures |
| Industrial / warehouse | `CRATE*`, `DUCT*`, `METAL*` | `{GRATE*`, `STEP*`, `METAL*` borders | `GRATE*`/plate, dark | `DUCT*`/panel | `~LIGHT` fixtures, hazard stripes |
| Outdoor / cliff | `ROCK*`, `CLIFF*` | rock caps at broad scale | `SAND*`, `DIRT*`, `GRASS*` ground | open `sky` | `sky` + `light_environment` |
| Sewer / waste | `SEWER*`, `RUST*` | `DRAIN*`, `METAL*` trim | wet `CRETE*`, darker | `SEWER*`/pipe panel | `SLIME*` / `!`-liquids |

Pairing rules:

- One wall family + one trim family + a floor darker than the walls per space. Keep that single wall family across connected interior spaces (see One wall family per interior); do not mix wall families in a room.
- Reuse the same set across connected rooms so the area reads as one place; change the set only when the space changes theme.
- One accent texture per view maximum (a screen, sign, hazard stripe, or hero panel). More than one accent and none of them read.
- Trims (borders, pillars, doorframes) come from the wall's own family or a deliberate trim / accent contrast — never a random third texture, and never a second wall family (see One wall family per interior).
- Floors darker than walls and ceilings equal-or-darker, so the room reads as a grounded box.

Workflow: `texture_search` the family roots (`groupFrames:true`) -> `texture_preview_sheet` to see dimensions and variants -> pick the wall/trim/floor/ceiling set -> record the set in your working notes -> apply it consistently across the connected space.

## Texture And Light

Texture as part of geometry, not afterthought paint:

- Start texture scale at 1:1 for GoldSrc materials unless the map already uses a different local convention.
- Let textures carry cheap visual depth where possible. A well-aligned grate, panel, stripe, crack, or light texture can replace many tiny brushes when the player only needs to read surface intent.
- Apply image/hero textures to the intended face only. Do not fit a door, panel, sign, poster, or screen texture across every side of a brush.
- Use `texture_apply_smart` with `objectHint`, `surfaceRole`, and `frontDirection` for props, doors, posters, screens, signs, and panels. It now **requires explicit targets** (`ids`/`faceRefs`/`selection`) and errors instead of retexturing the whole map; angled faces are classified to the nearest role by default. Pass `classify:"strict"` for the old thresholding, which reports `skippedFaces`.
- Use face alignment for continuity: trims should wrap corners, hallway walls should share a phase, and angled/clipped faces should not look randomly offset.
- Build brushes to fit important textures when possible, especially doors, panels, lab signage, grates, and trims.
- Use side/back fallback textures for thin props so they look like objects, not wallpaper slabs.
- Audit texture mistakes with `texture_audit` after applying object-specific or image-like textures. It returns summary counts plus offenders with `faceRefs` and metrics (scale outliers, non-uniform scale, off-axis and axis-mismatched rotation, fractional shift, stretched, perpendicular axis, coplanar mismatch, visible tool textures, missing textures). See the Design Review Loop.
- Treat texture assignment as unfinished until alignment is intentional. `texture_apply` only sets the material; it does not prove that origin, scale, or rotation are correct.
- **Never trust numeric face data over the viewport image.** `face_list` can report `scale 1.0 / shift 0.0` while the rendered face is clipped, smeared, or misaligned. The `viewport_capture` image is the final authority. If the image shows cutoff, misalignment, or any visual defect, fix it immediately — do not rationalize the numbers.
- For image-like prop faces such as crate sides, barrel caps, lids, signs, posters, labels, doors, panels, and screens, project or set face alignment immediately after applying the texture. Use `texture_project` with `fit` or `center` for flat bounded faces and caps, and `cylindrical` for wrapped cylinder sides.
- For box props with face-sized art, do not rely on default world-aligned 0/0 shifts. Fit each visible face locally unless the design deliberately needs world alignment, and verify that choice with `face_list`.
- For thin sign or label plates, put the display texture only on the outward display face. Put an edge material on the back, sides, top, and bottom; rotate narrow side faces 90 degrees when the grain, bands, or trim direction should run along the plate.

Alignment tool behaviors (use the tool, not hand math, for these):

- `face_texture_set` `rotation` now **actually rotates the texture axes** (real UV rotation), not metadata. `rotationMode:"store"` is the legacy metadata-only escape hatch.
- `texture_align_face` takes `mode`: `world` (world-axis projection), `face` (in-plane axes; alias `normal`), or `reset`; plus `justify` (`left`/`right`/`top`/`bottom`/`center`/`fit`) and a `rotation` param. Use `mode:face` to keep art square to an angled surface instead of skewing to world axes.
- `texture_copy_from_face` **projects alignment across non-parallel faces by default** (`projected:true`), wrapping continuously around edges and corners — this is the tool for carrying a trim, band, or panel around a corner. Set `projected:false` for a verbatim copy onto a parallel face.
- `texture_replace` **preserves alignment by default** (`align:false`); pass `find`/`replace` (aliases `from`/`to`). It swaps the material without disturbing scale, shift, or rotation.
- `texture_project` `mode:cylindrical` no longer needs an explicit `origin` — it defaults to the solid's center and reports `originUsed`. Supply `origin` only to override.

### Reading A Texture Before Committing

A texture's pixels and its family siblings carry meaning the file name and small preview tiles hide. Judge every hero, prop, door, or signage texture before applying it.

- **Preview at real size.** Preview any hero, prop, door, or signage texture as a single tile at `tileSize` >=192 (`texture_preview_sheet` / `texture_browser_capture`) before use. Small tiles hide weathering, art style, and machinery detail — a 96px tile can look like a clean hero table and turn out to be weathered barn planks at full scale.
- **Infer real-world scale from dimensions.** At scale 1, one texture pixel is one unit is roughly one inch, so read each art texture's dims as object size: a 64x80 face is a waist-height cabinet; 96x128 is a person-sized machine (industrial / lab spaces only); a 160x128 door is a freight / vehicle door, never a person entrance. Do not put mainframe-cabinet textures where desk-scale tech belongs.
- **Fit framed art exactly, with the tool.** Textures with borders or frames (crates, panels, doors, appliance fronts) must fit their face exactly. Apply the fit by running `texture_align_face` with `justify:"fit"` on the placed face (`faceRefs` from the import result or `face_list`) — do NOT hand-compute scale/shift in map text; hand arithmetic ships off-by-a-sliver repeats. (Real failure: two framed posters showed a repeated stripe on one edge from hand-computed fits.) Never crop at scale 1 (a 48x48 framed crate on a 32-unit cube loses its border band); avoid stretching beyond 1.25x. After fitting, close-up capture the face; any visible repeat or seam means the fit is wrong.
- **Aspect guard for fitted art.** Fitting must preserve the art's aspect ratio within ~1.25x. A 64x96 door texture fitted onto a 64x32 cabinet face squashes the panels 3:1 and looks wrong (real failure). Door art goes only on door-proportioned faces; for cabinet / cupboard fronts use a panel or grain field texture plus 3D trim strips to imply doors, or find art whose aspect matches the face.
- **Random-tiling `-N` families are surfaces, not props.** The engine randomizes the frame per face, so a prop textured with a `-0` family renders mismatched faces (e.g. a dark cabinet coming out as white marble). Use `-N` families on floors, walls, and terrain only — never on furniture or props. `texture_audit` reports `random_tiling_on_prop` and `prop_texture_crop` (informational) for prop-scale solids; fix by choosing a non-tiling texture or by fitting.
- **Inspect family variants.** Sibling suffixes (a/b/c) encode purpose: door-jamb strips ship in hinge and plain / latch variants (e.g. `*_dr1a` vs `*_dr1b`/`c`), and the hinge texture belongs on the hinge side only — do not apply one jamb variant to both jambs. Preview the siblings when picking from a family.
- **One wall family per interior.** Connected interior spaces keep a single wall family; express room function through floor, ceiling, trim, and fixtures instead. Introduce a second wall family only on explicit request or a hard theme boundary, and mediate that boundary with a doorway or threshold.

### Texture Fitting & Variant Discovery

When a texture does not fit a face naturally, discover the right texture rather than forcing the wrong one:

1. **Preview textures BEFORE applying them.** Use `texture_preview_sheet` or `texture_browser_capture` to inspect candidate textures before committing. Never guess texture content from file names alone. A texture named `crate09b` may be an explosive hazard label while `crate09a` is a plain lid.

2. **Compare aspect ratio before committing.** If a texture is 64x48 and the face is 64x64, stretching or tiling will visibly distort the art. Do not accept a compromised scale until you have searched for a better fit.

3. **Search for texture sets by base name.** Use `texture_browser_capture` or `texture_search` with the texture's root name to discover related variants. GoldSrc WADs commonly ship textures in families: a base name for sides/body and suffixed variants (`b`, `top`, `c`, numeric) for lids, caps, ends, or alternate faces. Treat mismatch as a signal to search, not as a problem to brute-force with scale.

4. **Assign textures by face role.** A box prop typically needs different textures for vertical faces, the top lid, and the bottom base. Do not apply the primary texture to every face by default. After applying, use `face_list` to confirm each face carries an appropriate texture for its orientation.

5. **Inspect visually before finishing.** After texturing, capture the viewport. If a face looks stretched, tiled, upside-down, or smeared, fix it. The `viewport_capture` image is the final authority, not the numeric face data.

### Alignment Candidate Selection

Before accepting any texture projection, choose the alignment candidate that preserves the artwork with the least distortion:

1. Measure the face bounds and read the candidate texture dimensions before applying final alignment.
2. If the texture dimensions match the face dimensions, prefer manual 1:1 alignment: `xScale = 1.0`, `yScale = 1.0`, and shifts derived from the face vertices/bounds. Do not use `fit` as the final answer for exact-size art.
3. If the face aspect ratio matches the texture but dimensions differ, prefer uniform scale plus shifts so the art keeps its proportions.
4. If the face and texture aspect ratios differ, search the texture family for a better role-specific variant before stretching, cropping, or tiling.
5. Use `center` only when a centered partial image is intentional. Use `fit` only when exact full-face stretch is intentional and the resulting distortion is acceptable.
6. Use `cylindrical` for wrapped curved surfaces (origin defaults to the solid center), then verify the seam and label count in the viewport. For a flat but angled face, use `texture_align_face` `mode: face` so the art stays square to the surface instead of skewing to world axes; use `texture_copy_from_face` (`projected:true`) to carry alignment continuously around a corner.
7. Treat the best candidate as provisional until `viewport_capture` shows complete, non-cut-off, non-smeared artwork on the intended face.

### Projection-Based Texturing

Use `texture_project` for reliable first-try alignment instead of guessing shifts. The tool supports `planar`, `cylindrical`, `fit`, and `center` modes.

**General workflow for any object:**
1. Preview candidate textures with `texture_preview_sheet` or `texture_browser_capture` before applying. Never trust filenames alone.
2. Classify faces by role: **vertical sides**, **top caps** (normal roughly +Z), **bottom caps** (normal roughly -Z), **front faces**, or **curved wrapping surfaces**.
3. Assign a texture suited to each role. A side label does not belong on a cap; a hazard symbol does not belong on a plain prop.
4. Select the best alignment candidate from the face bounds and texture dimensions before committing to `fit`, `center`, manual 1:1 shifts, or cylindrical projection.
5. Apply the texture, then use the projection or manual alignment that matches the face geometry. Do not stop after `texture_apply` for image-like prop faces.

| Face role | Recommended `mode` | Why |
| --- | --- | --- |
| Flat top / bottom / front | **manual 1:1 alignment**, `center`, or `fit` | Use manual 1:1 when artwork dimensions match the face; use `center` or `fit` only when their visual result is intentional. |
| Box/crate face with face-sized art | `fit` or **manual 1:1 alignment** | `fit` is a first try, but if the texture dimensions match the face dimensions, use scale `1.0, 1.0` with shifts instead to avoid aspect-ratio distortion. |
| Thin sign or label display face | `fit` or **manual 1:1 alignment** | `fit` can stretch the art; exact-size art should be 1:1 with edge-aligned shifts. |
| Vertical sides of a box | `planar` with `direction: [0,0,1]` or `fit` | World-aligned or stretched to fill. |
| Curved side of cylinder, pipe, arch, round column | `cylindrical` with `axis` (`origin` optional, defaults to solid center) | Computes angular wrap so the texture is continuous around the curve. |
| Any face needing exact stretch | `fit` | Stretches texture to fill the face exactly (may distort aspect ratio). |

**When texture and face dimensions match exactly (e.g. 64x96 texture on a 64x96 face):**
- Do **not** rely on `texture_project` `fit` as the final answer. `fit` may distort the aspect ratio by using different X and Y scales.
- Instead, set `xScale = 1.0`, `yScale = 1.0`, and align the texture origin to the correct face corner with `xShift` / `yShift`.
- Use `face_list` to read the face vertices, then set the shift to the vertex that corresponds to the texture's `(0,0)` corner. Capture the viewport and verify the art is not stretched.
- Example: a front face spanning `x = -32..32` and `z = 0..96` with U axis `-X` and V axis `+Z` uses `xShift = 32`, `yShift = 96`.

**Seamless wrap on faceted curved sides:**
- Use this for any wrapped side surface: barrels, tanks, pipes, columns, rockets, silos, ducts, arches, curved trims, and low-poly machinery. Do not hardcode the rule to one prop type.
- Never `fit` or `center` each polygon side independently when the side texture should wrap continuously. Per-face fitting restarts the texture on every facet and splits labels, bands, vents, stripes, or seams at the vertical edges.
- Treat the full n-sided cross-section as one unwrapped strip. The U axis runs around the perimeter; the V axis runs along the object's height or length.
- Compute the polygon perimeter from actual vertices when possible: `p = edge1 + edge2 + ... + edgeN`.
- For a regular n-sided cylinder with vertex radius `r`, use `p = 2 * n * r * sin(pi / n)`.
- If you know the vertex-to-vertex diameter `d`, use `p = n * d * sin(pi / n)`.
- If you know the apothem `a` instead, use `p = 2 * n * a * tan(pi / n)`.
- For texture width `w` and desired wrap repeats `t`, set horizontal side scale as `uScale = p / (w * t)`. One repeat uses `t = 1`; two repeated labels or bands use `t = 2`.
- The per-face U advance should be continuous. Each side face consumes its own edge length along the same unwrapped strip; it must not restart at U = 0.
- Use `texture_project` with `mode: cylindrical`, the correct long `axis`, and the calculated scale/repeat intent. `origin` defaults to the solid's center (reported as `originUsed`) — supply it only to override. In JACK/Hammer terms, this is the case for seamless wrap across adjacent side faces, not isolated face fitting.
- Keep top and bottom caps separate. Cap textures use planar, center, fit, or manual 1:1 alignment; the cylindrical side texture must not smear onto caps.
- If a large non-seamless logo or warning mark should appear once, do not force it to be the repeated side wrap texture. Use a separate flat label plate/decal face, choose a texture designed for cylindrical wrapping, or accept a deliberate hard seam.
- Low-sided cylinders will still show faceted lighting and geometry. That is GoldSrc-authentic. The failure to reject is texture discontinuity, not visible polygon facets.
- Visual rejection test: capture the viewport and reject the result if side artwork restarts on each facet, a band jumps at an edge, a label is split unintentionally, the seam is in the main viewing direction, the repeat count is wrong, or the cap artwork is smeared by side projection.

**Cylindrical mode parameters:**
- `axis`: the object's long axis (e.g. `[0,0,1]` for a vertical cylinder).
- `origin`: optional; the center point of the cylinder in world space. Defaults to the solid's center (reported as `originUsed`) — supply it only to override the auto-center.
- `scale`: texture scale. For seamless polygon-side wrapping, calculate it from the faceted perimeter, not from the ideal smooth circle. Use `p / (textureWidth * desiredRepeats)`.
- `labels`: how many texture repetitions around the circumference.
- `centerLabel: true` centers the first label in texture space.

**Important:** If a newly shipped tool (`texture_project`, `viewport_camera_set`/`viewport_camera_get`, `texture_audit`, `map_design_audit`, `texture_align_face`, `texture_search`, ...) is missing from the MCP tool list, the plugin catalog has not reloaded. Close HammerTime completely and restart it. Do not attempt manual workarounds (per-face shift math, cropped screenshots) unless the tool is confirmed unavailable.

- Motivate every light. Add a fixture, emissive panel, sprite, window, flame, or machine glow before placing a light entity.
- Use color temperature to reinforce material and mood: warm utility lights, cold lab fill, green toxic pools, blue moon/sky spill, amber flame.
- Layer lighting lightly: key light, soft fill, small accent. Avoid flattening the scene with many equal lights.
- In outdoor spaces, let the sun/sky establish the main read and use small local lights only for gameplay, machinery, warning color, or story beats. Do not pepper an exterior with unmotivated point lights.

## Composition Patterns

Use these patterns to make brushwork look skilled rather than busy:

- **Trim stack**: wall panel -> thin trim -> inset panel -> cap trim. Keep thicknesses consistent.
- **Arch bay**: structural side columns, arch primitive, inset shadow plane, fitted trim texture, and a fixture near the spring line or keystone.
- **Cliff face**: make several convex facets from cylinders/triangles/wedges, stagger depth, keep strata mostly horizontal, vary silhouette, hide seams with shadow or trims.
- **Outdoor canyon/dam wall**: block the horizon and main concrete/rock masses first, then stack convex terraces in 16/32/64-unit steps. Use broad 2:1 rock scale on cliff mass, 1:1 material near walkable contact, and separate clean clip/monsterclip volumes from the visible jagged edge.
- **Industrial lab wall**: large clean wall panels, service conduits, vents, recessed lights, access doors, and one readable focal machine.
- **Door assembly**: frame first, door slightly inset/narrower, sliding pocket or hinge origin clear, sides/top textured as material edges, button or indicator nearby.
- **Round turn**: use cylinder/arch primitives for the main curvature, then add fitted floor/ceiling trims. Do not fake a round turn with obvious block stairs unless the style wants it.
- **Brush prop**: create a readable silhouette with few strong shapes, add one material accent, tie to `func_wall`/`func_detail` if decorative, then validate.
- **Tool-assisted study**: when a BSP viewer, entity editor, or decompiler is used only for reference, write down authored patterns such as scale, material rhythm, clip/collision separation, and entity timing. Rebuild those patterns cleanly in HammerTime rather than importing noisy geometry.

## Validation Loop

Run this loop after risky edits and before final response:

1. `map_snapshot` the changed area or selected objects.
2. `map_validate` after vertex moves, clips, custom imported brush text, arches, torus/pipe work, or dense prop assemblies.
3. `problems_check` when a compile/editor problem is suspected.
4. Run `map_design_audit` (grid, scale conventions, texture monotony, lighting, world extents, wpoly hotspots) and `texture_audit` (per-face texture issues, plus the informational prop checks `random_tiling_on_prop` and `prop_texture_crop`) after each build phase; fix offenders by `objectId`/`faceRef` and re-run until clean or intentionally waived. See the Design Review Loop for the full workflow.
5. For image-like prop textures, inspect `face_list` before capture. Look for accidental defaults: all visible faces still at shift 0/0 on a moved prop, cap/lid textures applied but not centered, sign art on edge/back faces, or side faces that need 90 degree rotation.
6. Select and focus the result with `selection_set` and `viewport_focus`.
7. Capture the result with `viewport_capture` after focusing it. Aim the 3D view first with `viewport_camera_set` (position + lookAt) for player-eye, corner-overview, and doorway-sightline framings, and add a `renderMode:"wireframe"` pass to check brush structure and fragmentation. `method:"gpu"` capture works even when the editor window is covered but omits `overlay_set` highlights (use `includeOverlays:true` or another method when marks matter). Inspect the returned image content, not just the structured face data. **Do not approve textures based on `face_list` numbers alone.** If a texture is visibly smeared, repeated on the wrong sides, upside down, too blurry, clipped, origin-shifted, seam-misaligned, or visually mismatched, fix the texture alignment/scale/face targeting before calling it finished.
8. Use `texture_preview_sheet` or `texture_browser_capture` before choosing unfamiliar textures. Prefer exact texture names supplied by the user over semantic guesses.
9. If validation times out on a very large loaded map, report that honestly and use narrower evidence: selected bounds, object snapshots, face lists, targeted problem checks, and viewport captures.

When debugging leaks:

- Prefer pointfiles with `leaks_load_pointfile` when available.
- Remember brush entities cannot seal the void.
- Look for tiny grid misalignments, open corners, entity origins outside the map, sky/clip/detail brushes used as seals, and moved doors/windows crossing the hull.
- Use a temporary big-block reasoning strategy only to isolate a leak; do not leave a box around the map as a fix.

## Design Review Loop

Run this after each build phase, alongside the Validation Loop. It catches design and texture problems the geometry validators miss.

1. **Design audit.** Run `map_design_audit` and read the offenders. It flags `off_grid` (per-brush grid granularity), `micro_brush`/`degenerate_face`, `texture_monotony`, `scale_conventions` (doors 48-64 wide x 96-128 tall, steps <=16, floor-to-ceiling under 108 flagged as a heuristic), `unlit` (map-level and per-cell heuristic), `missing_player_start`, `world_extents` beyond +/-4096, and `wpoly_hotspots` (face-density proxy). Each offender carries `objectIds`.
2. **Texture audit.** Run `texture_audit` (`ids`/`faceRefs`/`selection`). It returns summary counts plus offenders with `faceRefs` and metrics for `scale_outlier`, `non_uniform_scale`, `rotation_off_axis`, `rotation_axis_mismatch`, `fractional_shift`, `stretched`, `perpendicular_axis`, `coplanar_texture_mismatch`, `tool_texture_visible`, `missing_texture`, and `unknown_dimensions`, plus the informational prop checks `random_tiling_on_prop` (a `-N` random-tiling family on a prop-scale solid) and `prop_texture_crop` (framed art cropped by its face).
3. **Fix by reference.** Address offenders by `faceRef`/`objectId`, then re-run both audits until clean or a finding is intentionally waived. Document every waiver (why the off-grid brush, monotony, or short room is deliberate).
4. **Visual pass with the camera.** Prefer `viewport_capture` with its inline `camera` parameter (same fields as `viewport_camera_set`) — it applies the pose atomically right before the shot. Prefer this over a separate `viewport_camera_set` + `viewport_capture` pair: editor freelook can move the camera between two calls when the mouse is over a viewport. Standard shots:
   - **Player-eye:** position at floor + 64 units, lookAt the far wall center of each corridor or focal wall. This is what the player actually sees.
   - **Corner overview:** position above a room corner at ~1.5x room height, lookAt the room center, to read massing and silhouette.
   - **Doorway sightline (both sides):** for every doorway, shoot through the opening from both sides at eye level (floor + 64), lookAt through the opening. Confirm the transition reads, the room is enterable, and nothing blocks the opening (see Accessibility & Clearance).
   - **Per-prop close-up:** for every built prop, capture from ~64-96 units at eye level and check for cropped framed art, mismatched random-tiling faces, wrong real-world scale, and unfinished free-standing faces (see Reading A Texture Before Committing and Prop Construction & Human Scale).
5. **Wireframe pass.** Recapture key shots with `renderMode:"wireframe"` (it overlays lines on the textured 3D view and restores automatically) to inspect brush structure and fragmentation — overlapping detail, needless splits, hidden micro-brushes.
6. **Loop economy.** Use `format:"jpeg"` with default sizing for fast review loops; switch to `format:"png"` with `maxWidth:0` (native resolution) for fine texture inspection.

Notes:

- `method:"gpu"` reads the render texture and works even when the editor window is covered or behind other windows, but it omits `overlay_set` highlights — pass `includeOverlays:true` (or use another method) when overlay marks matter. Per-image results carry `captureMethod` and `warnings` (including a black-frame warning to watch for).
- The captured image is the final authority over `face_list` numbers, exactly as in the Validation Loop's 'Never trust numeric face data' rule. If the image disagrees with the metrics, believe the image.
- **Density bar.** A furnished room is typically 25-40+ brushes. A room under that count is blocking-only, not furnished — add prop and detail brushwork (see Prop Construction & Human Scale) before calling it furnished. A ~10-brush room reads as lazy.
- **Save early, save often.** `documents_save` / `documents_export` accept a `path` as a save-as / export destination for untitled documents — save named checkpoints as you build, not only at the end.

## Accessibility & Clearance

Every space the player enters must stay enterable and traversable. This is manual discipline — there is no pathfinding tool; enforce it with rules and visual review.

- Keep a 64u-deep clear zone on both sides of every doorway. Never place a counter, reception desk, chair, or prop across a doorway or hard against a door.
- Primary walk routes (entrance -> hub -> each room) stay >=48u wide and continuous. The player hull is 32x32x72 standing; a route that pinches below 48u reads as blocked.
- Anchor furniture to walls and corners. Anything free-standing keeps >=48u clearance on every side the player uses.
- Do not place large props on the main walking axis. A reception counter belongs to one side of the entrance, not across it.
- Props must never interpenetrate other props: check pairwise AABBs when placing (a chair may tuck under a desk TOP's overhang, but must not intersect pedestals, side panels, or bodies). (Real failure: a task chair placed 8u inside a counter return read as "stitched to the desk".)
- When generating furniture coordinates programmatically, list every prop's AABB and verify separations before importing.
- Verify by capture after furnishing: shoot through each doorway from both sides at eye level (floor + 64) and confirm the room is enterable and nothing blocks the opening. Trace each route (entrance -> hub -> each room) mentally on the top 2D view.

## Common Mistakes

| Mistake | Better move |
| --- | --- |
| Boxy detail spam | Decide the silhouette and rhythm first, then add fewer stronger brush details. |
| Using blocks for curves | Use `Arch`, `Cylinder`, or `Pipe` primitives, then trim and validate. |
| Over-carving | Use clip planes, separate convex brushes, or rebuilt primitives. |
| Concave vertex edits | Split the design into multiple convex solids. |
| Twisted/non-planar faces | Split into triangles or rebuild as separate convex wedges. |
| Sealing with entity brushes | Keep simple worldspawn sealing hulls behind detail entities. |
| Texture soup | Use material families, consistent scale, face-specific fitting, and trim continuity. |
| Random outdoor rock spikes | Build terraces, shelves, cap slabs, and broad silhouettes before adding jagged facets. |
| Door texture on every side | Fit only the front face; sides/top get metal/wood edge textures. |
| Crate or box texture world-aligned from 0/0 | Fit each visible face locally; a matching 64x64 texture on a 64-unit brush still needs face-local origin. |
| Cap, lid, or face-sized artwork merely applied | Select the best alignment candidate from face bounds and texture dimensions, prefer manual 1:1 when dimensions match, then verify shifts and viewport. |
| Sign plate edge grain runs the wrong way | Keep sign art only on display faces; use edge material on sides/backs and rotate narrow side faces 90 degrees when needed. |
| Curved sides misaligned | Use `texture_project` in `cylindrical` mode with the correct `axis`, perimeter-based wrap scale, and `origin` (optional; defaults to the solid center). Do not `fit` or `center` each side face independently unless a hard seam is intentional. |
| Faceted cylinder texture restarts on every side | Treat all side faces as one unwrapped strip. Compute polygon perimeter, set `uScale = perimeter / (textureWidth * repeats)`, then verify continuous U phase around the object. |
| Smooth-circle circumference used for an n-sided brush | Use the faceted polygon perimeter from actual vertices, or `2 * n * r * sin(pi / n)` for regular cylinders. The smooth `2 * pi * r` circumference will drift on low-sided GoldSrc cylinders. |
| Mismatched cap/top texture | Preview the texture family first. Hazard labels, explosive symbols, or branded lids do not belong on plain props. |
| Framed crate/panel/door art cropped at scale 1 | Fit the brush to the texture or use `texture_align_face` `justify:"fit"`; never crop a border band, never stretch past 1.25x. |
| Random-tiling `-N` family on a prop | `-N` randomizes the frame per face; use it on floors/walls/terrain only. Pick a non-tiling texture for furniture and props. |
| Freight/vehicle door texture on a person entrance | Read the dims: a 160x128 door is a freight door. Use a 64-96 wide person-door texture and matching frame. |
| Machine-scale art in a desk-scale space | Infer real-world scale from dims; 96x128 art is a person-sized machine, wrong where desk-scale tech (16-24u) belongs. |
| Hinge jamb variant on both jambs | Sibling a/b/c suffixes encode purpose; put the hinge texture on the hinge side only, plain/latch on the other. |
| Toy-looking furniture from too few brushes | Furniture needs >=6 brushes and one overhang/recess; a 3-brush chair reads as a toy. |
| Armless single-pedestal chair | Office/task chairs get seat + back + 2 armrests + 4 legs or pedestal + base — 8-10 brushes; armless kids' chairs read as lazy. |
| Device built as a plain box with a tiny inset screen | Use the WAD's device art as the whole front face, fitted aspect-true; shape the shell like the device (bezel + back box + stand). |
| Striped/patterned tile as a prop shell | Door-header and banded tiles read as corrugated siding on a shell; use flat/neutral textures, previewed at >=192px. |
| Hand-computed texture fit | Fit framed art with `texture_align_face` `justify:"fit"`; hand-math scale/shift ships off-by-a-sliver repeats. |
| Door art squashed onto a cabinet face | Fitting must hold aspect within ~1.25x; door art goes on door-proportioned faces, cabinet fronts use panel/grain + trim strips. |
| Props interpenetrating each other | Check pairwise AABBs before importing; a chair tucks under a desk overhang but must not intersect pedestals, panels, or bodies. |
| Prop reads as an unidentifiable shape | Every prop must be recognizable from silhouette + one signature detail; add the detail, redesign, or omit it. |
| Prop or counter blocking a doorway or walk route | Keep 64u clear on both sides of doorways and >=48u-wide routes; anchor furniture to walls, not the walking axis. |
| Room "furnished" with ~10 brushes | Furnished rooms run 25-40+ brushes; under that is blocking-only. Add prop and detail brushwork. |
| Committing a texture judged from a tiny preview tile | Preview hero/prop/door/signage textures at `tileSize` >=192; small tiles hide weathering, style, and machinery detail. |
| Second wall family inside one connected interior | Keep one wall family per interior; vary floor, ceiling, trim, and fixtures instead. New wall family only at a threshold. |
| Detail brushes cutting VIS | Tie decorative brushes to `func_detail`/`func_wall` when appropriate. |
| Too many equal lights | Add visible emitters and compose key/fill/accent lighting. |
| "Looks complex but reads flat" | Add depth changes, shadow gaps, hierarchy, and one focal idea. |
| Trusting `face_list` numbers over the viewport | Capture the viewport and visually verify the texture is complete and aligned; numbers only confirm the tool state, not the visual result. |
| `texture_apply_smart` retexturing the whole map | It now requires targets — pass `ids`/`faceRefs`/`selection`; use `classify:"strict"` (with `skippedFaces`) only for the old angled-face thresholding. |
| `texture_replace` wrecking alignment | It preserves alignment by default (`align:false`) and only swaps the material; pass `find`/`replace`. |
| Trim or band breaking at a corner | Use `texture_copy_from_face` (`projected:true`) to wrap alignment continuously across the corner, or `texture_align_face` `mode:face` on the angled face. |
| Rotating a texture had no visible effect | `face_texture_set rotation` now rotates the real UV axes; `rotationMode:"store"` is the legacy metadata-only path. |
| Skipping design review | Run `map_design_audit` + `texture_audit` each phase, fix by `objectId`/`faceRef`, then do a camera pass with `viewport_camera_set` + `viewport_capture`. |

## Response Contract

When using this skill for a HammerTime edit:

- Tell the user what map/document you are working in.
- Mention the design intent before building substantial geometry.
- Prefer direct HammerTime operations over abstract advice.
- Do not edit unrelated maps or files.
- After geometry work, report object IDs or selected/focused result when available.
- Do not claim the map is valid, leak-free, or visually fixed without fresh evidence, including `viewport_capture` for visible brush or texture work.
