# HammerTime MCP — Manual verification (Batches E + F)

This file merges the Batch E camera/capture sequences with the Batch F texture-correctness,
metadata, preview-sheet, and audit sequences. Batch F begins at the "Batch F" heading below.

# Batch E — Manual verification (Camera control + reliable viewport capture)

Runtime verification needs the HammerTime editor GUI running with the freshly-built
`HammerTime.Mcp.Plugin.dll` installed, a map document open, and the MCP bridge connected.
Everything below is driven through the MCP tools (`viewport_camera_get`, `viewport_camera_set`,
`viewport_capture`). Run them in order.

## 0. Preconditions
- Build succeeded (`dotnet build HammerTime.MCP.sln -c Debug` → 0 errors) and the plugin DLL is
  deployed into the editor plugin folder.
- Open (or create) a map with some visible geometry, e.g. a few brushes near the origin.
- The default 4-viewport layout should be visible (3D + Top/Front/Side 2D views).

## 1. camera_get — baseline
Call `viewport_camera_get` with no args (or `{ "views": "all" }`).

Expect a `viewports` array with one entry per visible viewport:
- 3D entry: `type:"3d"`, `position`, `angles`, `anglesDegrees` (= angles×180/π),
  `direction`, `lookAt` (= position + normalized direction × 64), `fov`, `clipDistance`,
  `width`, `height` (viewport pixel size, non-zero).
- 2D entries: `type:"2d"`, `view` (top/front/side), `position`, `zoom`, `width`, `height`.

Record the 3D `position`/`lookAt` so you can confirm the next step changed them.

## 2. camera_set — 3D position + lookAt
Call `viewport_camera_set`:
```json
{ "views": "3d", "position": { "x": 256, "y": -256, "z": 192 }, "lookAt": { "x": 0, "y": 0, "z": 0 } }
```
Expect `{ "updated": 1, "viewports": [ { "view":"3d", "camera": { ... } } ] }`.
- Confirm the returned camera `position` ≈ (256,-256,192).
- Confirm `direction` points roughly toward the origin (negative x, positive y, negative z).
- Visually: the 3D viewport should now be looking at the origin from the new spot.

### 2b. camera_set — views inference (omit `views`)
```json
{ "position": { "x": 0, "y": -512, "z": 128 }, "anglesDegrees": { "x": 0, "y": 90, "z": 0 } }
```
`views` is inferred as `3d` because 3D params are present. Confirm `updated >= 1`.

### 2c. camera_set — mutual-exclusion error
```json
{ "views":"3d", "lookAt": {"x":0,"y":0,"z":0}, "direction": {"x":1,"y":0,"z":0} }
```
Expect an error: "Specify at most one of lookAt, direction, or anglesDegrees."

### 2d. camera_set — FOV clamp
```json
{ "views":"3d", "fov": 5 }
```
Expect the returned camera `fov` = 10 (clamped to the 10–170 range). Try `fov: 200` → 170.

### 2e. camera_set — 2D center + zoom
```json
{ "views":"top", "center": {"x":128,"y":128,"z":0}, "zoom": 2 }
```
Expect the Top viewport recentred; returned `zoom` = 2. Try `zoom: 1000` → clamped to 256.

### 2f. camera_set — neither 2D nor 3D params, no views
```json
{}
```
Expect an error requesting 3D or 2D parameters (or an explicit `views`).

## 3. viewport_capture — GPU tier (default auto)
Call `viewport_capture` with `{ "views": "3d" }`.
- Expect one image with `captureMethod:"gpu"` (auto picks GPU first), `mimeType:"image/png"`,
  `width`/`height` ≤ 1024 (default cap), a `warnings` array, and a base64 `data` payload.
- The image shows the textured scene from the current 3D camera **without** ImGui overlay
  highlights (entity names, gizmos, MCP overlay). This is expected for GPU capture.
- `warnings` should NOT contain `imageMostlyBlack` for a viewport with geometry in frame.

### 3b. Native size
`{ "views":"3d", "maxWidth": 0, "maxHeight": 0 }` → image at full viewport resolution.

### 3c. Explicit gpu method
`{ "views":"3d", "method": "gpu" }` → `captureMethod:"gpu"`; only the GPU tier is attempted.

## 4. viewport_capture — occluded / unfocused viewport
Cover part of the editor window with another window (or leave the 3D viewport unfocused), then:
`{ "views":"3d", "method":"auto" }`
- GPU readback reads the offscreen render texture, so the capture should still be correct even
  though the window is occluded (unlike a screen grab). `captureMethod` = `gpu`.
- `waitForFrameMs` (default 250) raises the inactive FPS so the unfocused viewport re-renders a
  fresh frame first. If you see stale content, bump `waitForFrameMs` to e.g. 600.

### 4b. includeOverlays (screen tier)
`{ "views":"3d", "includeOverlays": true }`
- Tier order becomes screen → gpu → printwindow. If the viewport is fully visible on screen,
  `captureMethod` = `screen` and the image INCLUDES overlay highlights/gizmos.
- If a non-screen tier served it (e.g. window occluded so screen skipped), the image `warnings`
  array contains `overlaysNotIncluded`.

## 5. viewport_capture — wireframe render mode
`{ "views":"3d", "renderMode": "wireframe" }`
- The plugin flips the document `DisplayFlags.Wireframe`, republishes `SettingsChanged`, waits
  ~400ms for the async scene rebuild, captures, then restores the previous mode (default
  `restoreRenderMode:true`) and waits briefly.
- Expect a wireframe-over-textured 3D image. Call `viewport_capture` again with no renderMode and
  confirm the view is back to normal textured (mode restored).

### 5b. renderMode textured (no-op path)
`{ "views":"3d", "renderMode": "textured" }` when already textured → no rebuild, normal capture.

### 5c. flat render mode → error
`{ "views":"3d", "renderMode": "flat" }`
- Expect error: "flat render mode is not supported by the HammerTime engine (textured and
  wireframe only)."

## 6. viewport_capture — JPEG output
`{ "views":"3d", "format": "jpeg", "jpegQuality": 60 }`
- Expect `mimeType:"image/jpeg"` and a smaller base64 payload than PNG. Try `jpegQuality: 100`
  and `jpegQuality: 1` to confirm the quality knob changes payload size. Out-of-range values are
  clamped to 1–100.

## 7. viewport_capture — all views + printwindow fallback
`{ "views":"all" }` → one image per viewport (3D + 3× 2D). Each has its own `captureMethod`.

`{ "views":"3d", "method": "printwindow" }` → forces the PrintWindow tier only; `captureMethod` =
`printwindow`. (PrintWindow's success bool is now checked; a failed grab advances the chain in
auto mode instead of returning a black frame.)

## Notes / known limitations
- GPU capture intentionally omits the ImGui overlay layer (entity labels, tool gizmos, MCP
  highlight overlays). Use `includeOverlays:true` (screen tier) when you specifically need those.
- There is no flat-shaded pipeline in the engine; only textured and wireframe exist.
- The scene rebuild after a renderMode switch has no completion event, so a fixed ~400ms settle is
  used. On a very large map, increase confidence by capturing twice.

---

# Batch F — Manual verification (Texture correctness, metadata, preview sheet, audits)

Preconditions: editor running with the freshly-built plugin, a map open with a few textured
brushes, at least one WAD loaded. All sequences are driven through the MCP tools. Every texture
tool result now carries a `warnings` array (empty when nothing to report) and `face` entries now
echo `uAxis`/`vAxis` alongside the scale/shift/rotation fields.

## F1. Cylindrical projection with no origin (B2 fallback)
1. Create a cylinder brush (e.g. `brush_create_cylinder`) near, but not at, the world origin.
2. `face_list` it and pick the curved side faces (`faceRefs`).
3. Call `texture_project` with `{ "mode":"cylindrical", "texture":"<wall tex>", "faceRefs":[...] }`
   and NO `origin`.
- Expect the wrap to still center on the brush: the result includes `originUsed` ≈ the solid's
  bounding-box center (not (0,0,0) and not silently skipped).
- Any face whose radius collapses below 0.01 is skipped with a `warnings` entry naming the face,
  and is excluded from `projectedFaces` rather than returning a silent no-op.

## F2. Rotation 45 round-trip (B1 absolute rotation)
1. Pick one face. `face_texture_set` `{ "faceRefs":[one], "rotation": 45 }` (rotationMode defaults
   to `absolute`).
2. Read it back with `face_list`. Confirm `rotation` ≈ 45 and `uAxis`/`vAxis` rotated 45° in-plane.
3. Set `{ "rotation": 0 }` again → axes return to the face-aligned baseline (round-trip).
4. Compare `{ "rotation": 45, "rotationMode": "store" }` on a fresh face: it writes the raw field
   only (legacy behavior; axes NOT rotated). Use this to confirm the two modes differ.
- `texture_align_face` `{ "mode":"face", "rotation": 45, "faceRefs":[...] }` should produce the same
  absolute-45 result via the align→rotation→justify→sanitize order.

## F3. texture_replace preserves alignment (B3)
1. Note a face's `xScale/yScale/xShift/yShift/rotation/uAxis/vAxis` via `face_list`.
2. `texture_replace` `{ "find":"<current>", "replace":"<other>" }` (also try the `from`/`to`
   aliases — both are accepted).
3. Read the face back.
- Only `texture` changed; scale/shift/rotation/axes are identical. Result has
  `alignPreserved: true`. (Contrast `texture_apply`, which realigns by default.)
- Passing `{ "align": true }` to `texture_replace` flips `alignPreserved` to false and realigns.

## F4. apply_smart no-target error + classify (B4)
1. With NOTHING selected and no ids/faceRefs, call `texture_apply_smart` `{ "front":"x" }`.
   - Expect the error: `texture.apply_smart requires ids, faceRefs, or a non-empty selection.`
2. Select a box brush. `texture_apply_smart` `{ "ids":[<box>], "top":"A", "bottom":"B",
   "front":"C", "back":"D", "left":"E", "right":"F" }` with default `classify:"nearest"`.
   - Every axis-aligned face is assigned its nearest role; `changedFaces` = 6, `unassignedFaces`=0.
3. Repeat with a rotated/prism brush and `{ "classify":"strict" }`.
   - Faces whose best role dot ≤ 0.9 appear under `skippedFaces:{count, faces:[{objectId,faceId,
     bestRole,bestDot}]}` instead of being textured.
4. Supply only some roles (e.g. just `top`) in nearest mode: faces resolving to a role with no
   texture increment `unassignedFaces`.

## F5. align_face justify fit (world + justify)
1. Pick a large wall face. `texture_align_face` `{ "mode":"world", "faceRefs":[one] }`.
   - Axes snap to world axes (Rotation 0); result echoes new `uAxis/vAxis`.
2. `texture_align_face` `{ "mode":"face", "justify":"fit", "faceRefs":[one] }`.
   - The texture is scaled so exactly one tile spans the face; `xScale/yScale` derived from the
     face's own vertex extents (all vertices, not the 6-point cloud). Degenerate axes are skipped
     with a warning.
3. Try `justify:"center"`, `"left"`, `"top"` and confirm the shift lands the texture accordingly.
   - If texture dimensions are unknown, a `warnings` entry `texture '<x>' dimensions unknown;
     assumed 64x64` appears and 64×64 is used.

## F6. copy_from_face projected (default true)
1. Texture-align a source face nicely. Pick an adjacent, non-coplanar target face.
2. `texture_copy_from_face` `{ "sourceFace":{objectId,faceId}, "faceRefs":[target] }`.
   - Default `projected:true`: the target's `faces[]` entry reports `projected:true` and
     `coplanar:false`, and the texture visually continues across the shared edge.
3. Repeat with `{ "projected": false }` → raw axis copy (legacy `Unclone`); `projected:false`.

## F7. texture_search groupFrames + metadata (W2)
1. `texture_search` `{ "query":"+0", "groupFrames": true }` (default).
   - Animation frames collapse into one entry per basename with `frames:[{name,frame,toggle}]`,
     plus `width/height/aspect/wad/flags/family/special`.
2. `{ "groupFrames": false }` returns one flat entry per texture with the same metadata.
3. `{ "includeSpecial": false }` drops tool/sky textures from the results.
4. `textures_list` `{ "detailed": true }` returns rich per-texture objects; `{ "detailed": false }`
   (default) returns plain name strings (back-compat).

## F8. Preview sheet pagination + labels (W3)
1. `texture_preview_sheet` `{ "query":"", "max": 16, "columns": 4 }`.
   - Result carries `total`, `offset:0`, `returned`, `hasMore`, `nextOffset`. Each tile has a
     two-line label: full name (line 1, ellipsised if long) and `WxH` + semantic glyphs (line 2).
2. `{ "query":"", "max": 16, "page": 1 }` → `offset` = 16; the next page of textures.
   `{ "offset": 40 }` overrides `page`. Confirm `hasMore/nextOffset` walk the full list.
3. `{ "showDimensions": false }` drops line 2. `texture_browser_capture` accepts the same params.
- Tiles with a missing/NULL image task render the missing-texture placeholder without crashing
  (the NULL-Task path is handled and all returned bitmaps are disposed).

## F9. Audits on a deliberately-bad map (W4)
Build a small map with intentional problems: one brush scaled 8×, one with a fractional-scale
face, an `aaatrigger` face on a WORLD solid, two coplanar adjacent faces with different textures,
a 0.5-unit micro sliver, an off-grid brush (vertices at x.3), no `info_player_start`, and no light
entities.

1. `texture_audit` `{}` (whole map).
   - `faceCount`, `medianScale{x,y}`, `gridSpacing`, `summary{code:count}` populated. `offenders`
     sorted by issue count desc, capped at `maxOffenders`, each with `issues[]`, `metrics`,
     `normal`. Expect codes: `stretched`, `scale_outlier`, `fractional_shift`,
     `tool_texture_visible`, `coplanar_texture_mismatch`, plus `non_uniform_scale`/
     `rotation_*`/`perpendicular_axis` where applicable. `toolTextureSummary` counts tool textures
     (clip/hint/skip/null/bevel only counted, never flagged). `truncated` true if capped.
   - Try `{ "scaleReference":"one" }`, `{ "checkHiddenFaces": true }`, and a `faceRefs`-scoped call.
2. `map_design_audit` `{}`.
   - `findings` has `off_grid` (granularity histogram + fractional offenders + `gridSpacing`),
     `micro_brush` (+ `degenerateFaces`), `texture_monotony`, `scale_conventions` (heuristic:true),
     `unlit` (`unlitMap:true` here, heuristic:true), `missing_player_start` (`missing:true`),
     `world_extents`, `wpoly_hotspots`. `checksRun` lists the checks, `referenceConventions`,
     `heuristics`, `warnings`, `truncated` present.
   - `{ "checks":["off_grid","unlit"] }` runs only that subset. `{ "includeProblemChecks": true }`
     embeds `findings.problem_checks`. `{ "selectedOnly": true }` scopes to the selection.

