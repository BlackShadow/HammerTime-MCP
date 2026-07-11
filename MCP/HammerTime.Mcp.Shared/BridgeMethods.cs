namespace HammerTime.Mcp.Shared
{
    public static class BridgeMethods
    {
        public const string Status = "status";
        public const string Doctor = "doctor";

        public const string DocumentsList = "documents.list";
        public const string DocumentsNew = "documents.new";
        public const string DocumentsOpen = "documents.open";
        public const string DocumentsOpenText = "documents.open_text";
        public const string DocumentsActivate = "documents.activate";
        public const string DocumentsSave = "documents.save";
        public const string DocumentsExport = "documents.export";
        public const string DocumentsClose = "documents.close";

        public const string MapSnapshot = "map.snapshot";
        public const string MapSearch = "map.search";
        public const string SelectionGet = "selection.get";
        public const string SelectionSet = "selection.set";
        public const string SelectionFilter = "selection.filter";
        public const string SelectionGrow = "selection.grow";
        public const string SelectionByBounds = "selection.by_bounds";
        public const string ViewportFocus = "viewport.focus";
        public const string ViewportCapture = "viewport.capture";
        public const string ViewportClearMarks = "viewport.clear_marks";
        public const string ViewportCameraSet = "viewport.camera_set";
        public const string ViewportCameraGet = "viewport.camera_get";

        public const string EditorToolsList = "editor.tools_list";
        public const string EditorToolActivate = "editor.tool_activate";

        public const string EntityCreate = "entity.create";
        public const string EntityUpdate = "entity.update";
        public const string EntityDelete = "entity.delete";
        public const string EntityTieBrushes = "entity.tie_brushes";
        public const string EntityUntieBrushes = "entity.untie_brushes";
        public const string FgdEntitiesList = "fgd.entities_list";
        public const string EntitySchema = "entity.schema";
        public const string EntityCreateFromSchema = "entity.create_from_schema";
        public const string ScriptedSequenceList = "scripted_sequence.list";
        public const string ScriptedSequenceUpsert = "scripted_sequence.upsert";

        public const string BrushTypesList = "brush.types_list";
        public const string BrushCreate = "brush.create";
        public const string BrushCreateBox = "brush.create_box";
        public const string BrushCreateFromPlanes = "brush.create_from_planes";

        public const string VertexSubtoolsList = "vertex.subtools_list";
        public const string VertexSubtoolActivate = "vertex.subtool_activate";
        public const string VertexSnapshot = "vertex.snapshot";
        public const string VertexMove = "vertex.move";
        public const string VertexSplitFace = "vertex.split_face";
        public const string VertexTriangulate = "vertex.triangulate";
        public const string VertexFaceEdit = "vertex.face_edit";

        public const string TexturesList = "textures.list";
        public const string TextureSearch = "texture.search";
        public const string TexturePreviewSheet = "texture.preview_sheet";
        public const string TextureApply = "texture.apply";
        public const string TextureReplace = "texture.replace";
        public const string TextureAlignFace = "texture.align_face";
        public const string TextureCopyFromFace = "texture.copy_from_face";
        public const string TextureProject = "texture.project";
        public const string TextureApplySmart = "texture.apply_smart";
        public const string TextureAudit = "texture.audit";
        public const string MapDesignAudit = "map.design_audit";

        public const string FaceList = "face.list";
        public const string FaceSelect = "face.select";
        public const string FaceTextureSet = "face.texture_set";
        public const string FaceDelete = "face.delete";
        public const string ObjectExportMapText = "object.export_maptext";
        public const string ObjectImportMapText = "object.import_maptext";
        public const string ObjectImportMapTextBatch = "object.import_maptext_batch";

        public const string ClipPreview = "clip.preview";
        public const string ClipApply = "clip.apply";
        public const string ClipSplit = "clip.split";

        public const string PrefabsList = "prefabs.list";
        public const string PrefabCreate = "prefab.create";

        public const string ObjectsDelete = "objects.delete";
        public const string ObjectsTransform = "objects.transform";

        public const string ProblemsCheck = "problems.check";
        public const string ProblemsFix = "problems.fix";
        public const string MapValidate = "map.validate";
        public const string MapFixAllSafe = "map.fix_all_safe";

        public const string LeaksLoadPointfile = "leaks.load_pointfile";
        public const string OverlaySet = "overlay.set";
        public const string OverlayClear = "overlay.clear";

        public const string CompileProfilesList = "compile.profiles_list";
        public const string CompileRun = "compile.run";
        public const string CompileLogTail = "compile.log_tail";

        public const string HistoryList = "history.list";
        public const string Undo = "history.undo";
        public const string Redo = "history.redo";

        public const string CordonGet = "cordon.get";
        public const string CordonSet = "cordon.set";
        public const string CordonEnable = "cordon.enable";

        public const string SkillGet = "skill.get";
    }
}
