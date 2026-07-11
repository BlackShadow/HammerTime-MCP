# HammerTime MCP

An [MCP](https://modelcontextprotocol.io) server that lets AI assistants drive the
**HammerTime** GoldSrc map editor (the successor to Sledge) directly: create brushes,
apply and align textures, edit geometry, place entities, run compiles, capture the
viewport, and audit map quality — all through natural-language tool calls.

## Architecture

```
+-------------+   stdio JSON-RPC   +---------------------+   named pipe   +--------------------------+   in-proc API   +----------------------+
| MCP client  | <----------------> |  hammertime-mcp.exe | <------------> |  HammerTime.Mcp.Plugin   | <-------------> |  HammerTime / Sledge |
| (Claude,    |   (MCP protocol)   |  (Cli / MCP server) |  (token-auth,  |  (MEF plugin, in editor) |   (BspEditor,   |  editor APIs         |
|  Cursor,    |                    |                     |   JSON frames) |                          |    Rendering,   |                      |
|  Codex, ...)|                    |                     |                |                          |    Providers)   |                      |
+-------------+                    +---------------------+                +--------------------------+                 +----------------------+
```

The MCP client speaks the MCP protocol over stdio to `hammertime-mcp.exe`. That process
translates each tool call into a JSON request, sends it over a per-user **named pipe**
(guarded by a random token) to the plugin running *inside* the editor, and streams the
response back. The plugin executes the operation against the live document using the
editor's own APIs, so everything an AI does is a real, undoable editor action.

## Projects

| Project | Target | Role |
|---------|--------|------|
| `HammerTime.Mcp.Shared` | `netstandard2.0` | DTOs, brush/tool catalogs, texture semantics, pointfile parser, config, bridge contracts. Shared by the plugin (net6) and the CLI (net8). |
| `HammerTime.Mcp.Plugin` | `net6.0-windows` | MEF plugin loaded by the editor. Hosts the named-pipe server and implements every bridge method against the live document. References the editor binaries. |
| `HammerTime.Mcp.Cli` | `net8.0` | The MCP server (`hammertime-mcp.exe`), plus the installer, `doctor`, `status`, and `call` diagnostics commands. |
| `HammerTime.Mcp.Tests` | `net8.0` | xUnit tests for the portable slices (catalogs, parsers, config, tool schemas). References Shared + Cli only. |

## Installing

Run `MCP-Install\install.bat` (produced by a build; see *Building*). The installer:

1. **Stops** any running `hammertime-mcp.exe`.
2. **Selects clients** — an interactive keyboard picker (Up/Down move, Space toggle,
   Enter install, Esc cancel) listing VS Code, VS Code Insiders, Cursor, Claude Desktop,
   Claude Code, Codex CLI, Kimi Code, OpenCode, Antigravity, Gemini CLI, Windsurf, and a
   generic `.mcp.json`. Detected installs are marked; nothing is ticked by default. You can
   also pass clients directly: `install.bat "claude-code,cursor" user "C:\Program Files (x86)\HammertimeEditor"`.
3. **Elevates** for the plugin copy — the plugin DLLs land in the editor's Program Files
   directory, so the installer requests admin rights just for that step (`--plugin-only`),
   then installs the client configs back as the normal user (`--clients-only`).
4. **Scope** — `user` (default) or `project` MCP registration.

What ends up where:

- **Plugin** → the HammerTime editor directory (auto-detected at
  `%ProgramFiles(x86)%\HammertimeEditor`, or pass the folder explicitly): the plugin DLL,
  `HammerTime.Mcp.Shared.dll`, and `Newtonsoft.Json.dll`.
- **Config + skill** → `%APPDATA%\HammerTime.MCP\` (`config.json` and the
  `hammertime-goldsrc-brushwork` skill).
- **Client registration** → each selected client's MCP config, pointing at
  `hammertime-mcp.exe serve`.

## Configuration

`%APPDATA%\HammerTime.MCP\config.json`:

| Field | Meaning |
|-------|---------|
| `pipeName` | Named-pipe name. Defaults to `hammertime-mcp-<sanitized-username>`. |
| `token` | 32-char random token authenticating the CLI to the plugin. |
| `hammerTimeDirectory` | Editor install directory (for `doctor`/launch detection). |
| `skillPath` | Path to the installed `SKILL.md`. |
| `skillHash` | Hash used to detect a stale/edited skill file. |
| `bridgeTimeoutMs` | Read/write IO timeout in milliseconds for the pipe bridge. Optional; when unset the server uses its default of **120000** (120 s). Raise it for very large captures or slow edits. |

### Environment variables

- `HAMMERTIME_MCP_IMAGE_OUTPUT_DIR` — where viewport/preview captures are written.
  Defaults to `%TEMP%\HammerTime.MCP\captures`. The directory is auto-pruned to the
  **newest 200** capture files (once per server run).

## Tools

The server exposes **99 tools**. Names below are grouped by area; each row is a group,
not a per-tool spec (call `brush_types_list`, `editor_tools_list`, etc., or read the tool
descriptions for parameters).

| Area | Tools |
|------|-------|
| Diagnostics | `hammertime_status`, `hammertime_doctor`, `hammertime_skill` — bridge status, install diagnosis, and the mapping skill text. |
| Documents | `documents_list/new/open/open_text/activate/save/export/close` — manage map tabs; open `.map` text directly. |
| Map | `map_snapshot`, `map_search`, `map_validate`, `map_fix_all_safe` — bounded summaries, search, and safe auto-fixes. |
| Selection | `selection_get/set/filter/grow/by_bounds` — read and shape the object selection. |
| Viewport & camera | `viewport_focus`, `viewport_capture`, `viewport_camera_get/set`, `viewport_clear_marks` — frame, screenshot, and drive the 2D/3D cameras. |
| Editor tools | `editor_tools_list`, `editor_tool_activate` — enumerate and switch active editor tools. |
| Entities | `entity_create/update/tie_brushes/untie_brushes`, `entity_schema`, `entity_create_from_schema`, `fgd_entities_list`, `scripted_sequence_list/upsert` — point/brush entities and FGD-driven creation. |
| Brushes | `brush_types_list`, `brush_create`, `brush_create_box/from_planes`, and per-shape helpers (`brush_create_arch/block/tetrahedron/pyramid/wedge/cylinder/barrel/cone/pipe/sphere/torus/text`). |
| Vertex editing | `vertex_subtools_list/subtool_activate`, `vertex_snapshot`, `vertex_move`, `vertex_split_face`, `vertex_triangulate`, `vertex_face_edit`. |
| Textures | `textures_list`, `texture_search`, `texture_preview_sheet`, `texture_browser_capture`, `texture_apply`, `texture_replace`, `texture_align_face`, `texture_copy_from_face`, `texture_project`, `texture_apply_smart`. |
| Auditing | `texture_audit`, `map_design_audit` — flag scale/rotation/alignment/tool-texture offenders and run GoldSrc design-sanity checks. |
| Faces | `face_list`, `face_select`, `face_texture_set`, `face_delete`. |
| Object map-text | `object_export_maptext`, `object_import_maptext`, `object_import_maptext_batch`. |
| Clipping | `clip_preview`, `clip_apply`, `clip_split`. |
| Prefabs | `prefabs_list`, `prefab_create`. |
| Compile | `compile_profiles_list`, `compile_run`, `compile_log_tail`. |
| Objects | `objects_delete`, `objects_transform` (translate/rotate/scale). |
| History | `undo`, `redo`, `history_list` — over MCP-recorded operations. |
| Problems | `problems_check`, `problems_fix`. |
| Leaks & overlay | `leaks_load_pointfile`, `overlay_set`, `overlay_clear`. |
| Cordon | `cordon_get`, `cordon_set`, `cordon_enable`. |

## Recent behavior changes (v0.2)

These are deliberate default changes — the older behavior is still reachable via an
explicit mode where noted:

1. **`texture_replace` preserves alignment by default** — swapping a texture keeps the
   existing face UVs/scale/shift.
2. **Target-requiring operations no longer act on "everything"** — `texture_apply_smart`,
   `face_delete`, `vertex_triangulate`, and `vertex_face_edit` now require explicit
   targets (objects or faceRefs) instead of falling back to the whole selection/map.
3. **`face_texture_set` rotation actually rotates** the texture axes. The legacy
   store-only behavior is available with `rotationMode: "store"`.
4. **`texture_apply_smart` classifies faces by nearest role** (front/back/left/right/
   top/bottom by normal). The legacy exact-axis behavior is `classify: "strict"`.
5. **`texture_copy_from_face` projects by default**, reprojecting the source alignment
   onto the target planes rather than copying raw UV numbers.
6. **`viewport_capture` defaults to `maxWidth`/`maxHeight` of 1024.** Pass `0` for either
   to capture at native resolution.

## New capabilities

- **Camera control** — `viewport_camera_get` / `viewport_camera_set` (3D position/lookAt/
  angles/FOV, 2D center/zoom).
- **Capture tiers** — `viewport_capture` `method: auto` walks a GPU-readback →
  PrintWindow → screen fallback chain, with `renderMode: wireframe` and png/jpeg output
  (`jpegQuality` for jpeg). GPU captures omit the ImGui overlay highlights.
- **Auditing** — `texture_audit` and `map_design_audit` surface offenders with `faceRefs`
  you can feed straight back into face tools.
- **Texture discovery** — metadata- and family-aware `texture_search`, plus paginated
  `texture_preview_sheet` / `texture_browser_capture` contact sheets.
- **Skill** — the installed `SKILL.md` (returned by `hammertime_skill`) documents GoldSrc
  brushwork conventions in structured sections.

## Building

```
dotnet build HammerTime.MCP.sln -c Debug
dotnet test
```

Building the plugin needs the **editor binaries**. `Directory.Build.props` sets
`UseHammerTimeBinaryReferences` for this solution and resolves them from, in order:
`%ProgramFiles(x86)%\HammertimeEditor\` (if `Hammertime.Editor.exe` is present), otherwise
a sibling Sledge source build at `..\Sledge.Editor\bin\<Configuration>\net6.0-windows7.0\`.

`Directory.Build.targets` assembles the installer bundle: when
`McpBuildInstallBundle` is true (the default inside Visual Studio, or set it explicitly),
building `HammerTime.Mcp.Cli` copies its output into `MCP-Install\Server\` (plus `SKILL.md`)
and building `HammerTime.Mcp.Plugin` copies the plugin DLLs into `MCP-Install\Plugin\`.
That populated `MCP-Install\` folder is what `install.bat` deploys.

## Manual verification

See **[VERIFY.md](VERIFY.md)** for the end-to-end manual checklist (install, connect a
client, drive the editor, and confirm captures/edits round-trip).
