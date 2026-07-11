using System.Collections.Generic;

namespace HammerTime.Mcp.Shared
{
    public static class McpToolCatalog
    {
        public static IReadOnlyList<McpToolInfo> CreateAll()
        {
            return new List<McpToolInfo>
            {
                Tool("hammertime_status", BridgeMethods.Status, "Get HammerTime MCP bridge status."),
                Tool("hammertime_doctor", BridgeMethods.Doctor, "Diagnose HammerTime MCP installation, bridge, version, and runtime state."),
                Tool("hammertime_skill", BridgeMethods.SkillGet, "Return the installed HammerTime GoldSrc mapping skill instructions."),
                Tool("documents_list", BridgeMethods.DocumentsList, "List open HammerTime documents."),
                Tool("documents_new", BridgeMethods.DocumentsNew, "Create a new HammerTime map document/tab."),
                Tool("documents_open", BridgeMethods.DocumentsOpen, "Open a map document."),
                Tool("documents_open_text", BridgeMethods.DocumentsOpenText, "Open a full .map file provided as a string as a new HammerTime document."),
                Tool("documents_activate", BridgeMethods.DocumentsActivate, "Activate an open document by path or documentIndex."),
                Tool("documents_save", BridgeMethods.DocumentsSave, "Save the active or specified document."),
                Tool("documents_export", BridgeMethods.DocumentsExport, "Export the active or specified document."),
                Tool("documents_close", BridgeMethods.DocumentsClose, "Close an open document."),
                Tool("map_snapshot", BridgeMethods.MapSnapshot, "Return a bounded summary of map objects."),
                Tool("map_search", BridgeMethods.MapSearch, "Search map objects by type, classname, key/value, selected state, or text."),
                Tool("map_validate", BridgeMethods.MapValidate, "Run safe map validation checks."),
                Tool("map_fix_all_safe", BridgeMethods.MapFixAllSafe, "Run all safe automatic map fixes."),
                Tool("selection_get", BridgeMethods.SelectionGet, "Return selected object IDs and bounds."),
                Tool("selection_set", BridgeMethods.SelectionSet, "Replace, add, or remove object selection."),
                Tool("selection_filter", BridgeMethods.SelectionFilter, "Filter the current object selection by type, classname, texture, or bounds."),
                Tool("selection_grow", BridgeMethods.SelectionGrow, "Grow object selection to parents, children, siblings, or touching bounds."),
                Tool("selection_by_bounds", BridgeMethods.SelectionByBounds, "Select objects whose bounds intersect or fit inside a box."),
                Tool("viewport_focus", BridgeMethods.ViewportFocus, "Focus 2D/3D viewports on ids, point, or current selection."),
                Tool("viewport_capture", BridgeMethods.ViewportCapture, "Capture visible HammerTime 3D/2D viewport screenshots with selectable method (auto/gpu/printwindow/screen), format (png/jpeg), and optional renderMode (textured/wireframe)."),
                Tool("viewport_camera_set", BridgeMethods.ViewportCameraSet, "Set HammerTime 3D camera position/lookAt/angles/FOV or 2D center/zoom."),
                Tool("viewport_camera_get", BridgeMethods.ViewportCameraGet, "Return camera state for HammerTime viewports."),
                Tool("viewport_clear_marks", BridgeMethods.ViewportClearMarks, "Clear MCP overlay highlights and HammerTime object selection wireframes."),
                Tool("editor_tools_list", BridgeMethods.EditorToolsList, "List HammerTime editor tools."),
                Tool("editor_tool_activate", BridgeMethods.EditorToolActivate, "Activate a HammerTime editor tool by name or alias."),
                Tool("entity_create", BridgeMethods.EntityCreate, "Create a point entity with properties."),
                Tool("entity_update", BridgeMethods.EntityUpdate, "Update entity classname, flags, origin, or keyvalues."),
                Tool("entity_tie_brushes", BridgeMethods.EntityTieBrushes, "Tie selected or specified solid brushes to a brush entity."),
                Tool("entity_untie_brushes", BridgeMethods.EntityUntieBrushes, "Move solid children out of brush entities back to world."),
                Tool("scripted_sequence_list", BridgeMethods.ScriptedSequenceList, "List scripted_sequence entities."),
                Tool("scripted_sequence_upsert", BridgeMethods.ScriptedSequenceUpsert, "Create or update a scripted_sequence."),
                Tool("fgd_entities_list", BridgeMethods.FgdEntitiesList, "List entity classes from the active FGD game data."),
                Tool("entity_schema", BridgeMethods.EntitySchema, "Return FGD schema details for one entity classname."),
                Tool("entity_create_from_schema", BridgeMethods.EntityCreateFromSchema, "Create an entity using FGD defaults plus supplied properties."),
                Tool("brush_types_list", BridgeMethods.BrushTypesList, "List HammerTime Brush Tool types and parameters."),
                Tool("brush_create", BridgeMethods.BrushCreate, "Create a HammerTime Brush Tool shape."),
                Tool("brush_create_box", BridgeMethods.BrushCreateBox, "Create a Block brush."),
                Tool("brush_create_from_planes", BridgeMethods.BrushCreateFromPlanes, "Create a solid brush directly from plane/texture definitions."),
                Tool("brush_create_arch", BridgeMethods.BrushCreate, "Create an Arch brush."),
                Tool("brush_create_block", BridgeMethods.BrushCreate, "Create a Block brush."),
                Tool("brush_create_tetrahedron", BridgeMethods.BrushCreate, "Create a Tetrahedron brush."),
                Tool("brush_create_pyramid", BridgeMethods.BrushCreate, "Create a Pyramid brush."),
                Tool("brush_create_wedge", BridgeMethods.BrushCreate, "Create a Wedge brush."),
                Tool("brush_create_cylinder", BridgeMethods.BrushCreate, "Create a Cylinder brush."),
                Tool("brush_create_barrel", BridgeMethods.BrushCreate, "Create a barrel-shaped Cylinder brush."),
                Tool("brush_create_cone", BridgeMethods.BrushCreate, "Create a Cone brush."),
                Tool("brush_create_pipe", BridgeMethods.BrushCreate, "Create a Pipe brush."),
                Tool("brush_create_sphere", BridgeMethods.BrushCreate, "Create a Sphere brush."),
                Tool("brush_create_torus", BridgeMethods.BrushCreate, "Create a Torus brush."),
                Tool("brush_create_text", BridgeMethods.BrushCreate, "Create a Text brush."),
                Tool("vertex_subtools_list", BridgeMethods.VertexSubtoolsList, "List Vertex Manipulation Tool subtools."),
                Tool("vertex_subtool_activate", BridgeMethods.VertexSubtoolActivate, "Activate a Vertex Manipulation Tool subtool."),
                Tool("vertex_snapshot", BridgeMethods.VertexSnapshot, "Return solid, face, and vertex references for vertex editing."),
                Tool("vertex_move", BridgeMethods.VertexMove, "Move vertices by snapshot keys or explicit face vertex references."),
                Tool("vertex_split_face", BridgeMethods.VertexSplitFace, "Split a face between two non-adjacent vertices."),
                Tool("vertex_triangulate", BridgeMethods.VertexTriangulate, "Triangulate polygon faces."),
                Tool("vertex_face_edit", BridgeMethods.VertexFaceEdit, "Run simple face edit operations such as poke or triangulate."),
                Tool("textures_list", BridgeMethods.TexturesList, "List textures from the active environment."),
                Tool("texture_search", BridgeMethods.TextureSearch, "Search available textures."),
                Tool("texture_preview_sheet", BridgeMethods.TexturePreviewSheet, "Render texture candidates into a labeled preview sheet image."),
                Tool("texture_browser_capture", BridgeMethods.TexturePreviewSheet, "Render texture-browser-style candidates into a labeled preview sheet image."),
                Tool("texture_apply", BridgeMethods.TextureApply, "Apply a texture to objects or faces."),
                Tool("texture_replace", BridgeMethods.TextureReplace, "Replace one texture with another on map faces."),
                Tool("texture_align_face", BridgeMethods.TextureAlignFace, "Align texture axes on faces."),
                Tool("texture_copy_from_face", BridgeMethods.TextureCopyFromFace, "Copy exact texture alignment from one face to other faces."),
                Tool("texture_project", BridgeMethods.TextureProject, "Project texture onto faces using planar, cylindrical, fit, or center modes."),
                Tool("texture_apply_smart", BridgeMethods.TextureApplySmart, "Apply different textures per face role (front, back, left, right, top, bottom) on box props with optional fit/center alignment."),
                Tool("texture_audit", BridgeMethods.TextureAudit, "Audit face texture scale, rotation, shift, alignment, and tool-texture issues; returns offenders with faceRefs."),
                Tool("map_design_audit", BridgeMethods.MapDesignAudit, "Run GoldSrc design-sanity checks: grid, micro-brushes, texture monotony, scale conventions, lighting, player start, extents, face-density hotspots."),
                Tool("face_list", BridgeMethods.FaceList, "List solid faces and texture data."),
                Tool("face_select", BridgeMethods.FaceSelect, "Select faces in HammerTime face selection."),
                Tool("face_texture_set", BridgeMethods.FaceTextureSet, "Set face texture data."),
                Tool("face_delete", BridgeMethods.FaceDelete, "Delete selected solid faces when geometry remains valid."),
                Tool("object_export_maptext", BridgeMethods.ObjectExportMapText, "Export one object/brush as Hammer .map text."),
                Tool("object_import_maptext", BridgeMethods.ObjectImportMapText, "Import Hammer .map brush text as a new object."),
                Tool("object_import_maptext_batch", BridgeMethods.ObjectImportMapTextBatch, "Import multiple Hammer .map brush text blocks in a single transaction."),
                Tool("clip_preview", BridgeMethods.ClipPreview, "Preview solid classification for a clip plane."),
                Tool("clip_apply", BridgeMethods.ClipApply, "Apply a clip plane to selected or specified solids."),
                Tool("clip_split", BridgeMethods.ClipSplit, "Split solids and keep both sides of the clip plane."),
                Tool("prefabs_list", BridgeMethods.PrefabsList, "List Worldcraft prefab libraries and prefabs."),
                Tool("prefab_create", BridgeMethods.PrefabCreate, "Create a prefab instance at an origin."),
                Tool("compile_profiles_list", BridgeMethods.CompileProfilesList, "List built-in MCP compile profiles."),
                Tool("compile_run", BridgeMethods.CompileRun, "Run a compile profile and capture logs."),
                Tool("compile_log_tail", BridgeMethods.CompileLogTail, "Return recent compile log lines."),
                Tool("undo", BridgeMethods.Undo, "Undo the last MCP-recorded map operation."),
                Tool("redo", BridgeMethods.Redo, "Redo the last MCP-recorded map operation."),
                Tool("history_list", BridgeMethods.HistoryList, "List MCP-recorded operation history."),
                Tool("objects_delete", BridgeMethods.ObjectsDelete, "Delete map objects by ID."),
                Tool("objects_transform", BridgeMethods.ObjectsTransform, "Translate, rotate, or scale objects."),
                Tool("problems_check", BridgeMethods.ProblemsCheck, "Run HammerTime map problem checks."),
                Tool("problems_fix", BridgeMethods.ProblemsFix, "Fix one problem reported by problems_check."),
                Tool("leaks_load_pointfile", BridgeMethods.LeaksLoadPointfile, "Load a .lin/.pts pointfile."),
                Tool("overlay_set", BridgeMethods.OverlaySet, "Highlight object IDs in HammerTime viewports."),
                Tool("overlay_clear", BridgeMethods.OverlayClear, "Clear MCP overlay highlights and leak path."),
                Tool("cordon_get", BridgeMethods.CordonGet, "Return cordon bounds and enabled state."),
                Tool("cordon_set", BridgeMethods.CordonSet, "Set cordon bounds."),
                Tool("cordon_enable", BridgeMethods.CordonEnable, "Enable or disable cordon rendering/export.")
            };
        }

        private static McpToolInfo Tool(string name, string bridgeMethod, string description)
        {
            return new McpToolInfo(name, bridgeMethod, description);
        }
    }

    public sealed class McpToolInfo
    {
        public McpToolInfo(string name, string bridgeMethod, string description)
        {
            Name = name;
            BridgeMethod = bridgeMethod;
            Description = description;
        }

        public string Name { get; }
        public string BridgeMethod { get; }
        public string Description { get; }
    }
}
