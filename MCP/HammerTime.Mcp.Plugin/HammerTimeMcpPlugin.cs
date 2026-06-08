using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using Sledge.BspEditor.Compile;
using LogicAndTrick.Oy;
using Newtonsoft.Json.Linq;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Editing.Commands.Pointfile;
using Sledge.BspEditor.Editing.Problems;
using Sledge.BspEditor.Modification;
using Sledge.BspEditor.Modification.Operations;
using Sledge.BspEditor.Modification.Operations.Data;
using Sledge.BspEditor.Modification.Operations.Selection;
using Sledge.BspEditor.Modification.Operations.Tree;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapData;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Tools.Brush;
using Sledge.BspEditor.Tools.Brush.Brushes.Controls;
using Sledge.BspEditor.Tools.Vertex.Tools;
using Sledge.Common;
using Sledge.Common.Shell.Components;
using Sledge.Common.Shell.Context;
using Sledge.Common.Shell.Documents;
using Sledge.Common.Shell.Hooks;
using Sledge.DataStructures.GameData;
using Sledge.DataStructures.Geometric;
using Sledge.Formats.Map.Formats;
using Sledge.Shell.Registers;
using IOPath = System.IO.Path;
using MapGroup = Sledge.BspEditor.Primitives.MapObjects.Group;
using Plane = Sledge.DataStructures.Geometric.Plane;
using TransformOperation = Sledge.BspEditor.Modification.Operations.Mutation.Transform;

namespace HammerTime.Mcp.Plugin
{
    [Export(typeof(IStartupHook))]
    [Export(typeof(IShutdownHook))]
    public sealed class HammerTimeMcpPlugin : IStartupHook, IShutdownHook
    {
        [Import] private Lazy<DocumentRegister> _documents;
        [Import] private Lazy<IContext> _context;
        [Import] private Lazy<McpOverlay> _overlay;
        [ImportMany] private IEnumerable<Lazy<IProblemCheck>> _problemChecks;
        [ImportMany] private IEnumerable<Lazy<IBrush>> _brushes;
        [ImportMany] private IEnumerable<Lazy<ITool>> _tools;
        [ImportMany] private IEnumerable<Lazy<VertexSubtool>> _vertexSubtools;

        private McpBridgeConfig _config;
        private McpNamedPipeServer _server;
        private readonly Dictionary<string, OperationHistory> _histories = new Dictionary<string, OperationHistory>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CompileRunLog> _compileRuns = new Dictionary<string, CompileRunLog>(StringComparer.OrdinalIgnoreCase);
        private readonly object _compileLock = new object();
        private string _activeCompileRunId;

        public Task OnStartup()
        {
            _config = McpBridgeConfig.LoadOrCreate(null, AppDomain.CurrentDomain.BaseDirectory);
            Log("Starting bridge on pipe " + _config.PipeName);
            Oy.Subscribe<string>("Compile:Output", line => AddCompileLog("output", line));
            Oy.Subscribe<string>("Compile:Error", line => AddCompileLog("error", line));
            Oy.Subscribe<string>("Compile:Information", line => AddCompileLog("info", line));
            _server = new McpNamedPipeServer(_config.PipeName, HandleRequest);
            _server.Start();
            return Task.CompletedTask;
        }

        public async Task OnShutdown()
        {
            if (_server != null)
            {
                await _server.Stop().ConfigureAwait(false);
                _server.Dispose();
                _server = null;
            }
        }

        private async Task<BridgeResponse> HandleRequest(BridgeRequest request)
        {
            if (request == null) return BridgeResponse.Fail(null, ErrorCodes.InvalidRequest, "Request is null.");
            Log("Request " + request.Method + " " + request.Id);
            if (!string.Equals(request.Token, _config.Token, StringComparison.Ordinal))
            {
                Log("Unauthorized request " + request.Method + " " + request.Id);
                return BridgeResponse.Fail(request.Id, ErrorCodes.Unauthorized, "Invalid MCP bridge token.");
            }

            try
            {
                var result = await Dispatch(request.Method, request.Params ?? new JObject()).ConfigureAwait(false);
                return BridgeResponse.Success(request.Id, result);
            }
            catch (BridgeCommandException ex)
            {
                Log("Bridge error " + ex.Code + ": " + ex.Message);
                return BridgeResponse.Fail(request.Id, ex.Code, ex.Message);
            }
            catch (PointFileParseException ex)
            {
                Log("Parse error: " + ex.Message);
                return BridgeResponse.Fail(request.Id, ErrorCodes.ParseError, ex.Message);
            }
            catch (Exception ex)
            {
                Log("Unhandled bridge error: " + ex);
                return BridgeResponse.Fail(request.Id, ErrorCodes.EditorUnavailable, ex.Message);
            }
        }

        private async Task<JToken> Dispatch(string method, JObject parameters)
        {
            switch (method)
            {
                case BridgeMethods.Status: return ToToken(Status());
                case BridgeMethods.Doctor: return ToToken(Doctor());
                case BridgeMethods.DocumentsList: return ToToken(DocumentsList());
                case BridgeMethods.DocumentsOpen: return await DocumentsOpen(parameters);
                case BridgeMethods.DocumentsActivate: return await DocumentsActivate(parameters);
                case BridgeMethods.DocumentsSave: return await DocumentsSave(parameters);
                case BridgeMethods.DocumentsExport: return await DocumentsExport(parameters);
                case BridgeMethods.DocumentsClose: return await DocumentsClose(parameters);
                case BridgeMethods.MapSnapshot: return ToToken(MapSnapshot(parameters));
                case BridgeMethods.MapSearch: return ToToken(MapSearch(parameters));
                case BridgeMethods.SelectionGet: return ToToken(SelectionGet(parameters));
                case BridgeMethods.SelectionSet: return await SelectionSet(parameters);
                case BridgeMethods.SelectionFilter: return await SelectionFilter(parameters);
                case BridgeMethods.SelectionGrow: return await SelectionGrow(parameters);
                case BridgeMethods.SelectionByBounds: return await SelectionByBounds(parameters);
                case BridgeMethods.ViewportFocus: return await ViewportFocus(parameters);
                case BridgeMethods.ViewportClearMarks: return await ViewportClearMarks(parameters);
                case BridgeMethods.EditorToolsList: return ToToken(EditorToolsList());
                case BridgeMethods.EditorToolActivate: return await EditorToolActivate(parameters);
                case BridgeMethods.EntityCreate: return await EntityCreate(parameters);
                case BridgeMethods.EntityUpdate: return await EntityUpdate(parameters);
                case BridgeMethods.EntityDelete: return await ObjectsDelete(parameters);
                case BridgeMethods.EntityTieBrushes: return await EntityTieBrushes(parameters);
                case BridgeMethods.EntityUntieBrushes: return await EntityUntieBrushes(parameters);
                case BridgeMethods.FgdEntitiesList: return await FgdEntitiesList(parameters);
                case BridgeMethods.EntitySchema: return await EntitySchema(parameters);
                case BridgeMethods.EntityCreateFromSchema: return await EntityCreateFromSchema(parameters);
                case BridgeMethods.ScriptedSequenceList: return ToToken(ScriptedSequenceList(parameters));
                case BridgeMethods.ScriptedSequenceUpsert: return await ScriptedSequenceUpsert(parameters);
                case BridgeMethods.BrushTypesList: return ToToken(BrushTypesList());
                case BridgeMethods.BrushCreate: return await BrushCreate(parameters);
                case BridgeMethods.BrushCreateBox: return await BrushCreateBox(parameters);
                case BridgeMethods.BrushCreateFromPlanes: return await BrushCreateFromPlanes(parameters);
                case BridgeMethods.VertexSubtoolsList: return ToToken(VertexSubtoolsList());
                case BridgeMethods.VertexSubtoolActivate: return await VertexSubtoolActivate(parameters);
                case BridgeMethods.VertexSnapshot: return ToToken(VertexSnapshot(parameters));
                case BridgeMethods.VertexMove: return await VertexMove(parameters);
                case BridgeMethods.VertexSplitFace: return await VertexSplitFace(parameters);
                case BridgeMethods.VertexTriangulate: return await VertexTriangulate(parameters);
                case BridgeMethods.VertexFaceEdit: return await VertexFaceEdit(parameters);
                case BridgeMethods.TexturesList: return await TexturesList(parameters);
                case BridgeMethods.TextureSearch: return await TextureSearch(parameters);
                case BridgeMethods.TextureApply: return await TextureApply(parameters);
                case BridgeMethods.TextureReplace: return await TextureReplace(parameters);
                case BridgeMethods.TextureAlignFace: return await TextureAlignFace(parameters);
                case BridgeMethods.TextureCopyFromFace: return await TextureCopyFromFace(parameters);
                case BridgeMethods.FaceList: return ToToken(FaceList(parameters));
                case BridgeMethods.FaceSelect: return await FaceSelect(parameters);
                case BridgeMethods.FaceTextureSet: return await FaceTextureSet(parameters);
                case BridgeMethods.FaceDelete: return await FaceDelete(parameters);
                case BridgeMethods.ObjectExportMapText: return ToToken(ObjectExportMapText(parameters));
                case BridgeMethods.ObjectImportMapText: return await ObjectImportMapText(parameters);
                case BridgeMethods.ClipPreview: return ToToken(ClipPreview(parameters));
                case BridgeMethods.ClipApply: return await ClipApply(parameters);
                case BridgeMethods.ClipSplit: return await ClipSplit(parameters);
                case BridgeMethods.PrefabsList: return ToToken(PrefabsList(parameters));
                case BridgeMethods.PrefabCreate: return await PrefabCreate(parameters);
                case BridgeMethods.ObjectsDelete: return await ObjectsDelete(parameters);
                case BridgeMethods.ObjectsTransform: return await ObjectsTransform(parameters);
                case BridgeMethods.ProblemsCheck: return await ProblemsCheck(parameters);
                case BridgeMethods.ProblemsFix: return await ProblemsFix(parameters);
                case BridgeMethods.MapValidate: return await MapValidate(parameters);
                case BridgeMethods.MapFixAllSafe: return await MapFixAllSafe(parameters);
                case BridgeMethods.LeaksLoadPointfile: return await LeaksLoadPointfile(parameters);
                case BridgeMethods.OverlaySet: return ToToken(OverlaySet(parameters));
                case BridgeMethods.OverlayClear: return ToToken(OverlayClear());
                case BridgeMethods.CompileProfilesList: return ToToken(CompileProfilesList());
                case BridgeMethods.CompileRun: return await CompileRun(parameters);
                case BridgeMethods.CompileLogTail: return ToToken(CompileLogTail(parameters));
                case BridgeMethods.HistoryList: return ToToken(HistoryList(parameters));
                case BridgeMethods.Undo: return await Undo(parameters);
                case BridgeMethods.Redo: return await Redo(parameters);
                case BridgeMethods.CordonGet: return ToToken(CordonGet(parameters));
                case BridgeMethods.CordonSet: return await CordonSet(parameters);
                case BridgeMethods.CordonEnable: return await CordonEnable(parameters);
                default:
                    throw new BridgeCommandException(ErrorCodes.UnknownMethod, $"Unknown bridge method '{method}'.");
            }
        }

        private object Status()
        {
            var active = ActiveDocumentOrNull();
            return new
            {
                app = "HammerTime",
                bridge = "HammerTime.Mcp.Plugin",
                pluginVersion = typeof(HammerTimeMcpPlugin).Assembly.GetName().Version?.ToString(),
                sharedVersion = typeof(BridgeMethods).Assembly.GetName().Version?.ToString(),
                pipeName = _config.PipeName,
                configPath = McpBridgeConfig.GetDefaultConfigPath(),
                pluginAssembly = typeof(HammerTimeMcpPlugin).Assembly.Location,
                baseDirectory = AppDomain.CurrentDomain.BaseDirectory,
                activeDocument = active == null ? null : DocumentInfo(active),
                openDocumentCount = _documents.Value.OpenDocuments.Count
            };
        }

        private object Doctor()
        {
            var configPath = McpBridgeConfig.GetDefaultConfigPath();
            var pluginDll = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "HammerTime.Mcp.Plugin.dll");
            var hammerTimeProcesses = System.Diagnostics.Process.GetProcesses()
                .Where(x =>
                    string.Equals(x.ProcessName, "Hammertime.Editor", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.ProcessName, "HammertimeEditor", StringComparison.OrdinalIgnoreCase) ||
                    x.ProcessName.IndexOf("HammerTime", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(x => new { id = x.Id, name = x.ProcessName, title = SafeWindowTitle(x) })
                .ToList();

            return new
            {
                ok = true,
                serverReachable = true,
                configPath,
                configExists = File.Exists(configPath),
                pipeName = _config.PipeName,
                hammerTimeDirectory = _config.HammerTimeDirectory,
                pluginAssembly = typeof(HammerTimeMcpPlugin).Assembly.Location,
                pluginDllExists = File.Exists(pluginDll),
                pluginVersion = typeof(HammerTimeMcpPlugin).Assembly.GetName().Version?.ToString(),
                sharedVersion = typeof(BridgeMethods).Assembly.GetName().Version?.ToString(),
                runningHammerTimeProcesses = hammerTimeProcesses,
                openDocumentCount = _documents.Value.OpenDocuments.Count,
                activeDocument = ActiveDocumentOrNull() == null ? null : DocumentInfo(ActiveDocumentOrNull())
            };
        }

        private object DocumentsList()
        {
            return new
            {
                active = ActiveDocumentOrNull()?.FileName,
                documents = _documents.Value.OpenDocuments.Select(DocumentInfo).ToList()
            };
        }

        private async Task<JToken> DocumentsOpen(JObject parameters)
        {
            var path = parameters.Required<string>("path");
            var loaderHint = parameters.Optional("loaderHint", "");
            if (!File.Exists(path)) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"File not found: {path}");

            var doc = await _documents.Value.OpenDocument(path, loaderHint).ConfigureAwait(true);
            return ToToken(new
            {
                opened = doc != null,
                activeDocument = DocumentInfo(ActiveDocument())
            });
        }

        private async Task<JToken> DocumentsActivate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, true);
            await _documents.Value.ActivateDocument(doc).ConfigureAwait(true);
            return ToToken(new { activeDocument = DocumentInfo(doc) });
        }

        private async Task<JToken> DocumentsSave(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var path = parameters.Optional<string>("path", null) ?? doc.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Save requires a path when the document has no file name.");
            }

            var saved = await _documents.Value.SaveDocument(doc, path, parameters.Optional("loaderHint", "")).ConfigureAwait(true);
            return ToToken(new { saved, document = DocumentInfo(doc) });
        }

        private async Task<JToken> DocumentsExport(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var path = parameters.Required<string>("path");
            var saved = await _documents.Value.ExportDocument(doc, path, parameters.Optional("loaderHint", "")).ConfigureAwait(true);
            return ToToken(new { exported = saved, path });
        }

        private async Task<JToken> DocumentsClose(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var force = parameters.Optional("force", false);
            if (force)
            {
                await _documents.Value.ForceCloseDocument(doc).ConfigureAwait(true);
                return ToToken(new { closed = true, force = true });
            }

            var closed = await _documents.Value.RequestCloseDocument(doc).ConfigureAwait(true);
            return ToToken(new { closed, force = false });
        }

        private object MapSnapshot(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("maxObjects", 2000);
            var objects = doc.Map.Root.FindAll();
            return new
            {
                document = DocumentInfo(doc),
                objectCount = objects.Count,
                returned = Math.Min(max, objects.Count),
                bounds = doc.Map.Root.BoundingBox.ToDto(),
                objects = objects.Take(max).Select(ObjectInfo).ToList()
            };
        }

        private object MapSearch(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var classname = parameters.Optional<string>("classname", null);
            var type = parameters.Optional<string>("type", null);
            var key = parameters.Optional<string>("key", null);
            var value = parameters.Optional<string>("value", null);
            var text = parameters.Optional<string>("text", null);
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var max = parameters.Optional("max", 100);

            var matches = doc.Map.Root.FindAll().Where(x => x.Hierarchy.Parent != null);
            if (selectedOnly) matches = matches.Where(x => x.IsSelected);
            if (!string.IsNullOrWhiteSpace(type)) matches = matches.Where(x => string.Equals(x.GetType().Name, type, StringComparison.InvariantCultureIgnoreCase));
            if (!string.IsNullOrWhiteSpace(classname)) matches = matches.Where(x => string.Equals(x.Data.GetOne<EntityData>()?.Name, classname, StringComparison.InvariantCultureIgnoreCase));
            if (!string.IsNullOrWhiteSpace(key))
            {
                matches = matches.Where(x =>
                {
                    var data = x.Data.GetOne<EntityData>();
                    if (data == null) return false;
                    if (!data.Properties.TryGetValue(key, out var actual)) return false;
                    return value == null || string.Equals(actual, value, StringComparison.InvariantCultureIgnoreCase);
                });
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                matches = matches.Where(x => ObjectMatchesText(x, text));
            }

            var list = matches.Take(max).Select(ObjectInfo).ToList();
            return new { returned = list.Count, objects = list };
        }

        private object SelectionGet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            return new
            {
                count = doc.Selection.Count,
                ids = doc.Selection.Select(x => x.ID).ToList(),
                bounds = doc.Selection.IsEmpty ? null : doc.Selection.GetSelectionBoundingBox().ToDto()
            };
        }

        private async Task<JToken> SelectionSet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var mode = parameters.Optional("mode", "replace").ToLowerInvariant();
            var objects = ResolveObjects(doc, parameters.Ids()).ToList();
            var operations = new List<IOperation>();

            if (mode == "replace")
            {
                operations.Add(new Deselect(doc.Selection.ToList()));
                operations.Add(new Select(objects));
            }
            else if (mode == "add") operations.Add(new Select(objects));
            else if (mode == "remove") operations.Add(new Deselect(objects));
            else throw new BridgeCommandException(ErrorCodes.InvalidRequest, "selection mode must be replace, add, or remove.");

            await Perform(doc, operations).ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private async Task<JToken> ViewportFocus(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            Box box = null;
            var ids = parameters.Ids();
            if (ids.Length > 0)
            {
                var boxes = ResolveObjects(doc, ids).Select(x => x.BoundingBox).Where(x => x != null && !x.IsEmpty()).ToList();
                if (boxes.Any()) box = new Box(boxes);
            }
            else if (parameters.OptionalVector("point").HasValue)
            {
                var point = parameters.OptionalVector("point").Value;
                box = new Box(point - Vector3.One * 64, point + Vector3.One * 64);
            }
            else if (!doc.Selection.IsEmpty) box = doc.Selection.GetSelectionBoundingBox();

            if (box == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "viewport.focus needs ids, point, or a non-empty selection.");

            var views = parameters.Optional("views", "all").ToLowerInvariant();
            if (views == "all" || views == "2d") await Oy.Publish("MapDocument:Viewport:Focus2D", box).ConfigureAwait(true);
            if (views == "all" || views == "3d") await Oy.Publish("MapDocument:Viewport:Focus3D", box).ConfigureAwait(true);
            return ToToken(new { focused = true, views, bounds = box.ToDto() });
        }

        private async Task<JToken> ViewportClearMarks(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var clearSelection = parameters.Optional("clearSelection", true);
            var clearOverlay = parameters.Optional("clearOverlay", true);
            var selectedCount = doc.Selection.Count;

            if (clearOverlay) _overlay.Value.Clear();
            if (clearSelection && !doc.Selection.IsEmpty)
            {
                await Perform(doc, new IOperation[] { new Deselect(doc.Selection.ToList()) }, "viewport.clear_marks", false).ConfigureAwait(true);
            }

            return ToToken(new
            {
                clearedSelection = clearSelection ? selectedCount : 0,
                clearedOverlay = clearOverlay
            });
        }

        private object EditorToolsList()
        {
            var active = _context.Value.Get<ITool>("ActiveTool");
            var tools = GetTools()
                .OrderBy(x => OrderHintAttribute.GetOrderHint(x.GetType()))
                .ThenBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase)
                .Select(tool => new
                {
                    name = tool.Name,
                    type = tool.GetType().FullName,
                    active = ReferenceEquals(tool, active)
                })
                .ToList();

            return new
            {
                activeTool = active == null ? null : new { name = active.Name, type = active.GetType().FullName },
                count = tools.Count,
                tools
            };
        }

        private async Task<JToken> EditorToolActivate(JObject parameters)
        {
            var name = parameters.Required<string>("name");
            var tool = ResolveTool(name);
            await Oy.Publish("ActivateTool", tool).ConfigureAwait(true);
            return ToToken(new
            {
                activated = true,
                tool = new { name = tool.Name, type = tool.GetType().FullName }
            });
        }

        private async Task<JToken> EntityCreate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var classname = parameters.Optional<string>("classname", null) ?? doc.Environment?.DefaultPointEntity ?? "info_player_start";
            var origin = parameters.OptionalVector("origin") ?? Vector3.Zero;
            var properties = parameters.StringDictionary("properties");
            properties.Remove("classname");
            properties.Remove("spawnflags");
            properties.Remove("origin");

            var entityData = new EntityData { Name = classname };
            foreach (var kv in properties) entityData.Properties[kv.Key] = kv.Value;
            if (parameters["spawnflags"] != null) entityData.Flags = parameters.Optional("spawnflags", 0);

            var entity = new Entity(doc.Map.NumberGenerator.Next("MapObject"))
            {
                Data =
                {
                    entityData,
                    new ObjectColor(Colour.GetDefaultEntityColour()),
                    new Origin(origin)
                },
                IsSelected = parameters.Optional("select", false)
            };

            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, entity) };
            if (entity.IsSelected)
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(entity));
            }

            await Perform(doc, ops).ConfigureAwait(true);
            return ToToken(new { created = ObjectInfo(entity) });
        }

        private async Task<JToken> EntityUpdate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var id = parameters.Required<long>("id");
            var obj = ResolveObject(doc, id);
            var data = obj.Data.GetOne<EntityData>();
            if (data == null) throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Object {id} is not an entity.");

            var ops = new List<IOperation>();
            var classname = parameters.Optional<string>("classname", null);
            if (!string.IsNullOrWhiteSpace(classname)) ops.Add(new EditEntityDataName(id, classname));

            if (parameters["spawnflags"] != null) ops.Add(new EditEntityDataFlags(id, parameters.Optional("spawnflags", data.Flags)));

            var properties = parameters.StringDictionary("properties");
            properties.Remove("classname");
            properties.Remove("spawnflags");
            properties.Remove("origin");
            if (properties.Count > 0) ops.Add(new EditEntityDataProperties(id, properties));

            var origin = parameters.OptionalVector("origin");
            if (origin.HasValue)
            {
                var current = obj.Data.GetOne<Origin>()?.Location ?? Vector3.Zero;
                ops.Add(new TransformOperation(Matrix4x4.CreateTranslation(origin.Value - current), obj));
            }

            await Perform(doc, ops).ConfigureAwait(true);
            return ToToken(new { updated = ObjectInfo(ResolveObject(doc, id)) });
        }

        private async Task<JToken> EntityTieBrushes(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var solids = RequestedOrSelectedObjects(doc, parameters)
                .SelectMany(x => x.FindAll())
                .OfType<Solid>()
                .Where(x => x.Hierarchy.Parent != null)
                .GroupBy(x => x.ID)
                .Select(x => x.First())
                .ToList();
            if (!solids.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "entity.tie_brushes requires selected or specified solid brushes.");

            var ops = new List<IOperation>();
            Entity entity;
            if (parameters["targetEntityId"] != null)
            {
                entity = ResolveObject(doc, parameters.Required<long>("targetEntityId")) as Entity;
                if (entity == null) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "targetEntityId must refer to an entity.");
                AddEntityDataUpdates(entity, parameters, ops);
            }
            else
            {
                var classname = parameters.Optional<string>("classname", null);
                if (string.IsNullOrWhiteSpace(classname)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "entity.tie_brushes requires classname when targetEntityId is not supplied.");

                var entityData = new EntityData { Name = classname };
                foreach (var kv in parameters.StringDictionary("properties"))
                {
                    if (!IsReservedEntityProperty(kv.Key) && kv.Value != null) entityData.Properties[kv.Key] = kv.Value;
                }
                if (parameters["spawnflags"] != null) entityData.Flags = parameters.Optional("spawnflags", 0);

                entity = new Entity(doc.Map.NumberGenerator.Next("MapObject"))
                {
                    Data =
                    {
                        entityData,
                        new ObjectColor(Colour.GetDefaultEntityColour())
                    }
                };
                var origin = parameters.OptionalVector("origin");
                if (origin.HasValue) entity.Data.Add(new Origin(origin.Value));
                ops.Add(new Attach(doc.Map.Root.ID, entity));
            }

            foreach (var group in solids.Where(x => x.Hierarchy.Parent.ID != entity.ID).GroupBy(x => x.Hierarchy.Parent.ID))
            {
                ops.Add(new Detatch(group.Key, group.ToList()));
                ops.Add(new Attach(entity.ID, group.ToList()));
            }

            if (parameters.Optional("select", true))
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(entity));
            }

            await Perform(doc, ops, "entity.tie_brushes").ConfigureAwait(true);
            var tiedEntity = ResolveObject(doc, entity.ID);
            return ToToken(new
            {
                entity = ObjectInfo(tiedEntity),
                brushIds = solids.Select(x => x.ID).ToList()
            });
        }

        private async Task<JToken> EntityUntieBrushes(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var entities = RequestedOrSelectedObjects(doc, parameters)
                .Select(InferBrushEntity)
                .Where(x => x != null)
                .GroupBy(x => x.ID)
                .Select(x => x.First())
                .ToList();
            if (!entities.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "entity.untie_brushes requires selected or specified brush entities.");

            var moved = new List<Solid>();
            var ops = new List<IOperation>();
            var deletedEntities = new List<long>();
            var deleteEmptyEntity = parameters.Optional("deleteEmptyEntity", true);
            foreach (var entity in entities)
            {
                var children = entity.FindAll()
                    .Where(x => x.ID != entity.ID)
                    .OfType<Solid>()
                    .Where(x => x.Hierarchy.Parent?.ID == entity.ID)
                    .ToList();
                if (!children.Any()) continue;
                moved.AddRange(children);
                ops.Add(new Detatch(entity.ID, children));
                ops.Add(new Attach(doc.Map.Root.ID, children));
                var movedChildIds = new HashSet<long>(children.Select(x => x.ID));
                var hasOtherChildren = entity.Hierarchy.Any(x => !movedChildIds.Contains(x.ID));
                if (deleteEmptyEntity && !hasOtherChildren && entity.Hierarchy.Parent != null)
                {
                    ops.Add(new Detatch(entity.Hierarchy.Parent.ID, entity));
                    deletedEntities.Add(entity.ID);
                }
            }
            if (!moved.Any()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "No solid brush children were found to untie.");

            if (parameters.Optional("select", true))
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(moved));
            }

            await Perform(doc, ops, "entity.untie_brushes").ConfigureAwait(true);
            return ToToken(new
            {
                movedBrushIds = moved.Select(x => x.ID).ToList(),
                entityIds = entities.Select(x => x.ID).ToList(),
                deletedEmptyEntities = deletedEntities
            });
        }

        private object ScriptedSequenceList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var target = parameters.Optional<string>("target", null);
            var query = doc.Map.Root.Find(x => x.Data.GetOne<EntityData>()?.Name == "scripted_sequence").OfType<Entity>();
            if (!string.IsNullOrWhiteSpace(target))
            {
                query = query.Where(x => x.EntityData.Properties.Values.Any(v => string.Equals(v, target, StringComparison.InvariantCultureIgnoreCase)));
            }
            return new { sequences = query.Select(ObjectInfo).ToList() };
        }

        private async Task<JToken> ScriptedSequenceUpsert(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var id = parameters.Optional<long?>("id", null);
            Entity existing = null;
            if (id.HasValue) existing = ResolveObject(doc, id.Value) as Entity;

            var targetname = parameters.Optional<string>("targetname", null);
            if (existing == null && !string.IsNullOrWhiteSpace(targetname))
            {
                existing = doc.Map.Root.Find(x =>
                    x is Entity &&
                    x.Data.GetOne<EntityData>()?.Name == "scripted_sequence" &&
                    string.Equals(x.Data.GetOne<EntityData>()?.Get<string>("targetname"), targetname, StringComparison.InvariantCultureIgnoreCase))
                    .OfType<Entity>()
                    .FirstOrDefault();
            }

            var properties = parameters.StringDictionary("properties");
            if (!string.IsNullOrWhiteSpace(targetname)) properties["targetname"] = targetname;
            foreach (var key in new[] { "m_iszEntity", "m_iszPlay", "m_iszIdle", "m_fMoveTo", "m_flRadius", "target", "killtarget" })
            {
                var value = parameters.Optional<string>(key, null);
                if (value != null) properties[key] = value;
            }

            if (existing == null)
            {
                var createParams = new JObject
                {
                    ["classname"] = "scripted_sequence",
                    ["origin"] = parameters["origin"] ?? JToken.FromObject(new Vector3Dto(0, 0, 0)),
                    ["properties"] = JObject.FromObject(properties),
                    ["spawnflags"] = parameters["spawnflags"]
                };
                return await EntityCreate(createParams).ConfigureAwait(true);
            }

            var updateParams = new JObject
            {
                ["id"] = existing.ID,
                ["classname"] = "scripted_sequence",
                ["properties"] = JObject.FromObject(properties)
            };
            if (parameters["origin"] != null) updateParams["origin"] = parameters["origin"];
            if (parameters["spawnflags"] != null) updateParams["spawnflags"] = parameters["spawnflags"];
            return await EntityUpdate(updateParams).ConfigureAwait(true);
        }

        private object BrushTypesList()
        {
            var brushes = GetBrushes()
                .OrderBy(x => BrushSortIndex(x.Name))
                .ThenBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase)
                .Select(brush => new
                {
                    name = brush.Name,
                    aliases = BrushCatalog.GetAliasesFor(brush.Name),
                    canRound = brush.CanRound,
                    parameters = ReadBrushControls(brush).Select(x => x.ToDto()).ToList()
                })
                .ToList();

            return new
            {
                count = brushes.Count,
                types = brushes
            };
        }

        private async Task<JToken> BrushCreateBox(JObject parameters)
        {
            var forwarded = new JObject(parameters)
            {
                ["type"] = "Block"
            };
            return await BrushCreate(forwarded).ConfigureAwait(true);
        }

        private async Task<JToken> BrushCreate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var type = parameters.Optional<string>("type", null) ?? parameters.Optional<string>("brushType", "Block");
            var brush = ResolveBrush(type);
            var min = parameters.RequiredVector("min");
            var max = parameters.RequiredVector("max");
            var texture = parameters.Optional<string>("texture", null)
                          ?? doc.Map.Data.GetOne<ActiveTexture>()?.Name
                          ?? "aaatrigger";
            ApplyBrushParameters(brush, parameters["parameters"] as JObject ?? parameters);

            var bounds = new Box(min, max);
            if (bounds.IsEmpty()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Brush bounds cannot be empty.");

            var round = parameters.Optional("round", true);
            var rounding = brush.CanRound && round ? 0 : 2;
            if (bounds.SmallestDimension < 10) rounding = 2;

            var created = brush.Create(doc.Map.NumberGenerator, bounds, texture, rounding).ToList();
            if (!created.Any())
            {
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Brush type '{brush.Name}' did not produce geometry. Check bounds and brush parameters.");
            }

            var scale = (float)(parameters.Optional<decimal?>("textureScale", null) ?? doc.Environment?.DefaultTextureScale ?? 1m);
            foreach (var face in created.SelectMany(x => x.Data.OfType<Face>()))
            {
                face.Texture.Name = texture;
                face.Texture.XScale = scale;
                face.Texture.YScale = scale;
                face.Texture.AlignToNormal(face.Plane.Normal);
            }

            IMapObject createdObject;
            if (created.Count > 1)
            {
                var group = new MapGroup(doc.Map.NumberGenerator.Next("MapObject"));
                created.ForEach(x => x.Hierarchy.Parent = group);
                group.DescendantsChanged();
                createdObject = group;
            }
            else
            {
                createdObject = created[0];
            }

            var select = parameters.Optional("select", false);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, createdObject) };
            if (select)
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(createdObject.FindAll()));
            }

            await Perform(doc, ops).ConfigureAwait(true);
            return ToToken(new
            {
                type = brush.Name,
                requestedType = type,
                objectCount = created.Count,
                created = ObjectInfo(createdObject),
                children = createdObject.FindAll().Where(x => x.ID != createdObject.ID).Select(ObjectInfo).ToList()
            });
        }

        private async Task<JToken> BrushCreateFromPlanes(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var planeDefs = parameters["planes"] as JArray;
            if (planeDefs == null || planeDefs.Count < 4)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "brush_create_from_planes requires at least 4 planes.");
            }

            var definitions = planeDefs.OfType<JObject>().Select(ReadPlaneDefinition).ToList();
            var polyhedron = new Polyhedron(definitions.Select(x => x.Plane));
            if (!polyhedron.IsValid()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "Plane set did not produce a valid convex brush.");

            var solid = new Solid(doc.Map.NumberGenerator.Next("MapObject"));
            foreach (var polygon in polyhedron.Polygons)
            {
                var definition = definitions
                    .OrderByDescending(x => Vector3.Dot(x.Plane.Normal, polygon.Plane.Normal))
                    .First();
                var face = new Face(doc.Map.NumberGenerator.Next("Face"))
                {
                    Texture = definition.Texture.Clone()
                };
                face.Vertices.AddRange(polygon.Vertices);
                solid.Data.Add(face);
            }
            solid.DescendantsChanged();

            var select = parameters.Optional("select", true);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, solid) };
            if (select)
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(solid));
            }

            await Perform(doc, ops, "brush.create_from_planes").ConfigureAwait(true);
            return ToToken(new { created = ObjectInfo(solid), faces = solid.Faces.Select(f => FaceInfo(solid, f)).ToList() });
        }

        private object VertexSubtoolsList()
        {
            var activeTool = _context.Value.Get<ITool>("ActiveTool");
            var subtools = GetVertexSubtools()
                .OrderBy(x => x.OrderHint)
                .ThenBy(x => x.Title, StringComparer.InvariantCultureIgnoreCase)
                .Select(tool => new
                {
                    name = tool.Title,
                    type = tool.GetType().FullName,
                    orderHint = tool.OrderHint,
                    active = tool.Active
                })
                .ToList();

            return new
            {
                vertexToolActive = activeTool != null && string.Equals(activeTool.Name, "Vertex Manipulation Tool", StringComparison.InvariantCultureIgnoreCase),
                count = subtools.Count,
                subtools
            };
        }

        private async Task<JToken> VertexSubtoolActivate(JObject parameters)
        {
            var name = parameters.Required<string>("name");
            var activateVertexTool = parameters.Optional("activateVertexTool", true);
            var subtool = ResolveVertexSubtool(name);
            if (activateVertexTool)
            {
                await Oy.Publish("ActivateTool", ResolveTool("Vertex Manipulation Tool")).ConfigureAwait(true);
            }

            await Oy.Publish("VertexTool:SetSubTool", subtool.GetType()).ConfigureAwait(true);
            return ToToken(new
            {
                activated = true,
                subtool = new { name = subtool.Title, type = subtool.GetType().FullName }
            });
        }

        private async Task<JToken> ObjectsDelete(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var objects = ResolveObjects(doc, parameters.Ids()).Where(x => x.Hierarchy.Parent != null).ToList();
            if (!objects.Any()) return ToToken(new { deleted = 0 });

            var ops = objects
                .GroupBy(x => x.Hierarchy.Parent.ID)
                .Select(g => (IOperation)new Detatch(g.Key, g.ToList()))
                .ToList();
            await Perform(doc, ops).ConfigureAwait(true);
            return ToToken(new { deleted = objects.Count, ids = objects.Select(x => x.ID).ToList() });
        }

        private async Task<JToken> ObjectsTransform(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var objects = ResolveObjects(doc, parameters.Ids()).ToList();
            if (!objects.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "objects.transform requires ids.");

            var pivot = parameters.OptionalVector("pivot") ?? Vector3.Zero;
            var translation = parameters.OptionalVector("translation") ?? Vector3.Zero;
            var scale = parameters.OptionalVector("scale") ?? Vector3.One;
            var rotation = parameters.OptionalVector("rotationDegrees") ?? Vector3.Zero;

            var matrix =
                Matrix4x4.CreateTranslation(-pivot) *
                Matrix4x4.CreateScale(scale) *
                Matrix4x4.CreateRotationX(Degrees(rotation.X)) *
                Matrix4x4.CreateRotationY(Degrees(rotation.Y)) *
                Matrix4x4.CreateRotationZ(Degrees(rotation.Z)) *
                Matrix4x4.CreateTranslation(pivot + translation);

            await Perform(doc, new[] { new TransformOperation(matrix, objects) }).ConfigureAwait(true);
            return ToToken(new { transformed = objects.Select(x => ObjectInfo(ResolveObject(doc, x.ID))).ToList() });
        }

        private async Task<JToken> ProblemsCheck(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var selectedIds = new HashSet<long>(doc.Selection.SelectMany(x => x.FindAll()).Select(x => x.ID));
            Predicate<IMapObject> filter = selectedOnly
                ? obj => obj != null && selectedIds.Contains(obj.ID)
                : _ => true;
            var results = new List<object>();
            foreach (var lazy in _problemChecks)
            {
                var checker = lazy.Value;
                var problems = await checker.Check(doc, filter).ConfigureAwait(true);
                for (var i = 0; i < problems.Count; i++)
                {
                    var problem = problems[i];
                    results.Add(new
                    {
                        checker = checker.GetType().FullName,
                        name = checker.Name,
                        details = checker.Details,
                        canFix = checker.CanFix,
                        index = i,
                        text = problem.Text,
                        objectIds = problem.Objects.Select(x => x.ID).ToList()
                    });
                }
            }
            return ToToken(new { count = results.Count, problems = results });
        }

        private async Task<JToken> ProblemsFix(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var checkerName = parameters.Required<string>("checker");
            var index = parameters.Optional("index", 0);
            var checker = _problemChecks.Select(x => x.Value).FirstOrDefault(x =>
                string.Equals(x.GetType().FullName, checkerName, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.Name, checkerName, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().Name, checkerName, StringComparison.InvariantCultureIgnoreCase));

            if (checker == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Problem checker not found: {checkerName}");
            if (!checker.CanFix) throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Problem checker cannot fix: {checkerName}");

            var problems = await checker.Check(doc, _ => true).ConfigureAwait(true);
            if (index < 0 || index >= problems.Count) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Problem index {index} is out of range.");
            await checker.Fix(doc, problems[index]).ConfigureAwait(true);
            return ToToken(new { fixedProblem = index, checker = checker.GetType().FullName });
        }

        private async Task<JToken> LeaksLoadPointfile(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var path = parameters.Optional<string>("path", null);
            var text = parameters.Optional<string>("text", null);
            if (text == null)
            {
                if (string.IsNullOrWhiteSpace(path)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "leaks.load_pointfile requires path or text.");
                if (!File.Exists(path)) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Pointfile not found: {path}");
                text = File.ReadAllText(path);
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var pointFile = Pointfile.Parse(lines);
            await MapDocumentOperation.Perform(doc, new TrivialOperation(
                d => d.Map.Data.Replace(pointFile),
                c => c.Update(c.Document.Map.Root))).ConfigureAwait(true);

            var points = LinesToPoints(pointFile.Lines).Select(x => x.ToDto()).ToList();
            _overlay.Value.SetLeakPath(points, path == null ? "MCP pointfile" : System.IO.Path.GetFileName(path));

            if (pointFile.Lines.Any())
            {
                var start = pointFile.Lines[0].Start;
                await Oy.Publish("MapDocument:Viewport:Focus2D", start).ConfigureAwait(true);
                await Oy.Publish("MapDocument:Viewport:Focus3D", start).ConfigureAwait(true);
            }

            var hits = pointFile.Lines
                .SelectMany(line => doc.Map.Root.GetIntersectionsForVisibleObjects(line, MapObjectExtensions.IgnoreOptions.IgnoreClip | MapObjectExtensions.IgnoreOptions.IgnoreNull).Take(1))
                .GroupBy(x => x.Object.ID)
                .Select(g => new { objectId = g.Key, count = g.Count(), objectInfo = ObjectInfo(g.First().Object) })
                .ToList();

            return ToToken(new
            {
                loaded = true,
                path,
                lineCount = pointFile.Lines.Count,
                pointCount = points.Count,
                points,
                intersections = hits
            });
        }

        private object OverlaySet(JObject parameters)
        {
            var ids = parameters.Ids();
            var label = parameters.Optional<string>("label", "MCP overlay");
            _overlay.Value.SetHighlights(ids, label);
            return new { overlay = true, highlightedIds = ids };
        }

        private object OverlayClear()
        {
            _overlay.Value.Clear();
            return new { overlay = false };
        }

        private async Task<JToken> TexturesList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("max", 500);
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var names = collection.GetAllTextures().OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).Take(max).ToList();
            return ToToken(new
            {
                count = collection.GetAllTextures().Count(),
                returned = names.Count,
                textures = names,
                packages = collection.Packages.Select(x => new { x.Location, count = x.Textures.Count() }).ToList()
            });
        }

        private async Task<JToken> TextureSearch(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var query = parameters.Optional<string>("query", parameters.Optional<string>("text", ""));
            var max = parameters.Optional("max", 100);
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var names = collection.GetAllTextures()
                .Where(x => string.IsNullOrWhiteSpace(query) || x.IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0)
                .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
                .Take(max)
                .ToList();
            await collection.Precache(names).ConfigureAwait(true);
            var items = await collection.GetTextureItems(names).ConfigureAwait(true);
            return ToToken(new
            {
                query,
                returned = names.Count,
                textures = names.Select(name =>
                {
                    var item = items.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
                    return new { name, width = item?.Width, height = item?.Height, wad = item?.WadName };
                }).ToList()
            });
        }

        private async Task<JToken> TextureApply(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var texture = parameters.Required<string>("texture");
            var faces = ResolveFaceRefsOrObjects(doc, parameters).ToList();
            if (!faces.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture.apply requires ids, faceRefs, or a non-empty selection.");

            var align = parameters.Optional("align", true);
            var scale = parameters.Optional<decimal?>("textureScale", null);
            var ops = new List<IOperation>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                clone.Texture.Name = texture;
                if (scale.HasValue)
                {
                    clone.Texture.XScale = (float)scale.Value;
                    clone.Texture.YScale = (float)scale.Value;
                }
                if (align) clone.Texture.AlignToNormal(clone.Plane.Normal);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }

            await Perform(doc, ops, "texture.apply").ConfigureAwait(true);
            return ToToken(new { texture, changedFaces = faces.Count, faces = faces.Select(x => FaceInfo(x.Object, ResolveFace(doc, x.Object.ID, x.Face.ID))).ToList() });
        }

        private async Task<JToken> TextureReplace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var find = parameters.Required<string>("find");
            var replace = parameters.Required<string>("replace");
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var solids = CandidateObjects(doc, selectedOnly ? doc.Selection.Select(x => x.ID).ToArray() : parameters.Ids()).OfType<Solid>().ToList();
            var matches = solids.SelectMany(s => s.Faces.Where(f => string.Equals(f.Texture.Name, find, StringComparison.InvariantCultureIgnoreCase)).Select(f => new FaceRef(s, f))).ToList();
            var forwarded = new JObject(parameters)
            {
                ["texture"] = replace,
                ["faceRefs"] = new JArray(matches.Select(x => new JObject { ["objectId"] = x.Object.ID, ["faceId"] = x.Face.ID }))
            };
            var result = await TextureApply(forwarded).ConfigureAwait(true);
            return ToToken(new { find, replace, changedFaces = matches.Count, result });
        }

        private async Task<JToken> TextureAlignFace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faces = ResolveFaceRefsOrObjects(doc, parameters).ToList();
            if (!faces.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture.align_face requires ids, faceRefs, or selected faces.");
            var mode = parameters.Optional("mode", "normal").ToLowerInvariant();
            var ops = new List<IOperation>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                clone.Texture.AlignToNormal(clone.Plane.Normal);
                if (mode == "reset")
                {
                    clone.Texture.XShift = 0;
                    clone.Texture.YShift = 0;
                    clone.Texture.Rotation = 0;
                }
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }
            await Perform(doc, ops, "texture.align_face").ConfigureAwait(true);
            return ToToken(new { alignedFaces = faces.Count, mode });
        }

        private object FaceList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("max", 500);
            var faces = CandidateObjects(doc, parameters.Ids())
                .OfType<Solid>()
                .SelectMany(x => x.Faces.Select(f => FaceInfo(x, f)))
                .Take(max)
                .ToList();
            return new { returned = faces.Count, faces };
        }

        private async Task<JToken> FaceSelect(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var mode = parameters.Optional("mode", "replace").ToLowerInvariant();
            var refs = ResolveFaceRefs(doc, parameters["faceRefs"] as JArray).ToList();
            await MapDocumentOperation.Perform(doc, new TrivialOperation(
                d =>
                {
                    var selection = d.Map.Data.GetOne<FaceSelection>();
                    if (selection == null)
                    {
                        selection = new FaceSelection();
                        d.Map.Data.Add(selection);
                    }
                    if (mode == "replace") selection.Clear();
                    foreach (var entry in refs)
                    {
                        if (mode == "remove") selection.Remove(entry.Object, entry.Face);
                        else selection.Add(entry.Object, entry.Face);
                    }
                },
                c =>
                {
                    var selection = c.Document.Map.Data.GetOne<FaceSelection>();
                    if (selection != null) c.Update(selection);
                })).ConfigureAwait(true);
            return ToToken(new { selectedFaces = doc.Map.Data.GetOne<FaceSelection>()?.Count ?? 0, mode });
        }

        private async Task<JToken> FaceTextureSet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faces = ResolveFaceRefsOrObjects(doc, parameters).ToList();
            if (!faces.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "face_texture_set requires ids, faceRefs, or selected faces.");

            var ops = new List<IOperation>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                ApplyTextureFields(clone.Texture, parameters);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }

            await Perform(doc, ops, "face.texture_set").ConfigureAwait(true);
            return ToToken(new { changedFaces = faces.Count, faces = faces.Select(x => FaceInfo(x.Object, ResolveFace(doc, x.Object.ID, x.Face.ID))).ToList() });
        }

        private async Task<JToken> TextureCopyFromFace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var sourceToken = parameters["sourceFace"] as JObject;
            if (sourceToken == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture_copy_from_face requires sourceFace { objectId, faceId }.");
            var source = ResolveFaceRefs(doc, new JArray(sourceToken)).First();
            var targets = ResolveFaceRefsOrObjects(doc, parameters).Where(x => x.Face.ID != source.Face.ID || x.Object.ID != source.Object.ID).ToList();
            if (!targets.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture_copy_from_face requires target faceRefs, ids, or selected faces.");

            var ops = new List<IOperation>();
            foreach (var target in targets)
            {
                var clone = (Face)target.Face.Clone();
                clone.Texture.Unclone(source.Face.Texture);
                ops.Add(new RemoveMapObjectData(target.Object.ID, target.Face));
                ops.Add(new AddMapObjectData(target.Object.ID, clone));
            }

            await Perform(doc, ops, "texture.copy_from_face").ConfigureAwait(true);
            return ToToken(new { source = FaceInfo(source.Object, source.Face), changedFaces = targets.Count });
        }

        private async Task<JToken> FaceDelete(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var refs = ResolveFaceRefsOrObjects(doc, parameters).ToList();
            var grouped = refs.GroupBy(x => x.Object).ToList();
            var ops = new List<IOperation>();
            foreach (var group in grouped)
            {
                var solid = group.Key as Solid;
                if (solid == null) continue;
                var remaining = solid.Faces.Count() - group.Count();
                if (remaining < 4) throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Deleting faces from solid {solid.ID} would leave invalid geometry.");
                foreach (var entry in group) ops.Add(new RemoveMapObjectData(solid.ID, entry.Face));
            }
            await Perform(doc, ops, "face.delete").ConfigureAwait(true);
            return ToToken(new { deletedFaces = refs.Count });
        }

        private object VertexSnapshot(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var solids = CandidateObjects(doc, parameters.Ids()).OfType<Solid>().ToList();
            var vertices = solids.SelectMany(s => s.Faces.SelectMany(f => f.Vertices.Select((v, i) => new
                {
                    objectId = s.ID,
                    faceId = f.ID,
                    vertexIndex = i,
                    vertexKey = VertexKey(v),
                    position = v.ToDto()
                }))).ToList();
            return new
            {
                solidCount = solids.Count,
                vertexRefCount = vertices.Count,
                vertexKeyCount = vertices.Select(x => x.vertexKey).Distinct(StringComparer.Ordinal).Count(),
                vertices,
                solids = solids.Select(s => new
                {
                    objectId = s.ID,
                    faceCount = s.Faces.Count(),
                    bounds = s.BoundingBox.ToDto()
                }).ToList()
            };
        }

        private async Task<JToken> VertexMove(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var delta = parameters.OptionalVector("delta");
            var position = parameters.OptionalVector("position");
            if (!delta.HasValue && !position.HasValue) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "vertex.move requires delta or position.");

            var keys = (parameters["vertexKeys"] as JArray)?.Select(x => x.Value<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
            var refs = ResolveVertexRefs(doc, parameters["vertexRefs"] as JArray).ToList();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var ops = new List<IOperation>();

            foreach (var solid in CandidateObjects(doc, parameters.Ids()).OfType<Solid>())
            {
                foreach (var face in solid.Faces)
                {
                    var clone = (Face)face.Clone();
                    var touched = false;
                    for (var i = 0; i < clone.Vertices.Count; i++)
                    {
                        var keyMatch = keys.Contains(VertexKey(clone.Vertices[i]), StringComparer.Ordinal);
                        var refMatch = refs.Any(r => r.Object.ID == solid.ID && r.Face.ID == face.ID && r.VertexIndex == i);
                        if (!keyMatch && !refMatch) continue;
                        clone.Vertices[i] = position ?? clone.Vertices[i] + delta.Value;
                        touched = true;
                    }
                    if (!touched) continue;
                    ops.Add(new RemoveMapObjectData(solid.ID, face));
                    ops.Add(new AddMapObjectData(solid.ID, clone));
                    changed.Add($"{solid.ID}:{face.ID}");
                }
            }

            if (!ops.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "No vertices matched vertexKeys or vertexRefs.");
            await Perform(doc, ops, "vertex.move").ConfigureAwait(true);
            return ToToken(new { changedFaces = changed.Count, vertexKeys = keys });
        }

        private async Task<JToken> VertexSplitFace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faceRef = ResolveSingleFaceRef(doc, parameters);
            var a = parameters.Optional("vertexIndexA", -1);
            var b = parameters.Optional("vertexIndexB", -1);
            var ops = SplitFaceOperation(doc, faceRef, a, b);
            await Perform(doc, ops, "vertex.split_face").ConfigureAwait(true);
            return ToToken(new { split = true, objectId = faceRef.Object.ID, faceId = faceRef.Face.ID });
        }

        private async Task<JToken> VertexTriangulate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var refs = ResolveFaceRefsOrObjects(doc, parameters).Where(x => x.Face.Vertices.Count > 3).ToList();
            var ops = new List<IOperation>();
            foreach (var entry in refs) ops.AddRange(TriangulateFaceOperation(doc, entry, false));
            await Perform(doc, ops, "vertex.triangulate").ConfigureAwait(true);
            return ToToken(new { triangulatedFaces = refs.Count });
        }

        private async Task<JToken> VertexFaceEdit(JObject parameters)
        {
            var action = parameters.Optional("action", "poke").ToLowerInvariant();
            if (action == "triangulate") return await VertexTriangulate(parameters).ConfigureAwait(true);

            var doc = ResolveDocument(parameters, false);
            var refs = ResolveFaceRefsOrObjects(doc, parameters).Where(x => x.Face.Vertices.Count > 3).ToList();
            var ops = new List<IOperation>();
            foreach (var entry in refs) ops.AddRange(TriangulateFaceOperation(doc, entry, action == "poke"));
            await Perform(doc, ops, "vertex.face_edit").ConfigureAwait(true);
            return ToToken(new { action, editedFaces = refs.Count });
        }

        private object ClipPreview(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var plane = ResolvePlane(parameters);
            var solids = CandidateObjects(doc, parameters.Ids()).OfType<Solid>().ToList();
            var preview = solids.Select(s =>
            {
                var classes = s.Faces.SelectMany(f => f.Vertices).Select(v => plane.OnPlane(v)).Distinct().ToList();
                var spanning = classes.Contains(-1) && classes.Contains(1);
                return new { objectId = s.ID, spanning, front = classes.Contains(1), back = classes.Contains(-1), onPlane = classes.All(x => x == 0) };
            }).ToList();
            return new { solidCount = solids.Count, spanning = preview.Count(x => x.spanning), preview };
        }

        private async Task<JToken> ClipApply(JObject parameters)
        {
            return await ApplyClip(parameters, parameters.Optional("side", "front")).ConfigureAwait(true);
        }

        private async Task<JToken> ClipSplit(JObject parameters)
        {
            return await ApplyClip(parameters, "both").ConfigureAwait(true);
        }

        private async Task<JToken> ApplyClip(JObject parameters, string side)
        {
            var doc = ResolveDocument(parameters, false);
            var plane = ResolvePlane(parameters);
            side = (side ?? "front").ToLowerInvariant();
            var solids = CandidateObjects(doc, parameters.Ids()).OfType<Solid>().ToList();
            var ops = new List<IOperation>();
            var changed = 0;
            foreach (var solid in solids)
            {
                if (!solid.Split(doc.Map.NumberGenerator, plane, out var back, out var front)) continue;
                var created = new List<IMapObject>();
                if (side == "front" || side == "both") created.Add(front);
                if (side == "back" || side == "both") created.Add(back);
                if (!created.Any()) continue;
                ops.Add(new Detatch(solid.Hierarchy.Parent.ID, solid));
                ops.Add(new Attach(solid.Hierarchy.Parent.ID, created));
                changed++;
            }
            await Perform(doc, ops, "clip.apply").ConfigureAwait(true);
            return ToToken(new { clippedSolids = changed, side });
        }

        private object PrefabsList(JObject parameters)
        {
            var dir = IOPath.GetFullPath(parameters.Optional<string>("directory", IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "prefabs")));
            if (!Directory.Exists(dir)) return new { directory = dir, libraries = new object[0] };
            var libraries = new List<object>();
            foreach (var path in Directory.GetFiles(dir, "*.ol"))
            {
                try
                {
                    var lib = WorldcraftPrefabLibrary.FromFile(path);
                    libraries.Add(new
                    {
                        path,
                        name = IOPath.GetFileNameWithoutExtension(path),
                        description = lib.Description,
                        prefabs = lib.Prefabs.Select((p, i) => new { index = i, p.Name, p.Description }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    libraries.Add(new { path, name = IOPath.GetFileNameWithoutExtension(path), description = ex.Message, prefabs = new List<object>() });
                }
            }
            return new { directory = dir, libraries };
        }

        private async Task<JToken> PrefabCreate(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var library = parameters.Required<string>("library");
            var index = parameters.Optional("index", -1);
            var name = parameters.Optional<string>("name", null);
            var origin = parameters.OptionalVector("origin") ?? Vector3.Zero;
            var path = File.Exists(library) ? library : IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "prefabs", library.EndsWith(".ol", StringComparison.OrdinalIgnoreCase) ? library : library + ".ol");
            if (!File.Exists(path)) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Prefab library not found: {path}");
            var lib = WorldcraftPrefabLibrary.FromFile(path);
            if (index < 0 && !string.IsNullOrWhiteSpace(name)) index = lib.Prefabs.FindIndex(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
            if (index < 0 || index >= lib.Prefabs.Count) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Prefab index/name not found.");
            var contents = HammerTime.Formats.Map.Prefab.GetPrefab(lib.Prefabs[index].Map, doc.Map.NumberGenerator, doc.Map).ToList();
            if (!contents.Any()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "Prefab did not contain map objects.");
            var center = new Box(contents.Select(x => x.BoundingBox)).Center;
            var translation = Matrix4x4.CreateTranslation(origin - center);
            var ops = new List<IOperation>
            {
                new Attach(doc.Map.Root.ID, contents),
                new TransformOperation(translation, contents),
                new Deselect(doc.Selection.ToList()),
                new Select(contents)
            };
            await Perform(doc, ops, "prefab.create").ConfigureAwait(true);
            return ToToken(new { library = path, index, created = contents.Select(ObjectInfo).ToList() });
        }

        private object ObjectExportMapText(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var id = parameters.Optional<long?>("id", null) ?? parameters.Ids().FirstOrDefault();
            if (id == 0) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "object_export_maptext requires id or ids[0].");
            var obj = ResolveObject(doc, id);
            if (!(obj is Solid solid)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "object_export_maptext currently exports Solid brush objects.");
            return new { objectId = solid.ID, mapText = SolidToMapText(solid) };
        }

        private async Task<JToken> ObjectImportMapText(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var text = parameters.Required<string>("text");
            var solid = ParseSolidMapText(text, doc.Map.NumberGenerator);
            solid.DescendantsChanged();
            var select = parameters.Optional("select", true);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, solid) };
            if (select)
            {
                ops.Add(new Deselect(doc.Selection.ToList()));
                ops.Add(new Select(solid));
            }
            await Perform(doc, ops, "object.import_maptext").ConfigureAwait(true);
            return ToToken(new { created = ObjectInfo(solid), mapText = SolidToMapText(solid) });
        }

        private async Task<JToken> FgdEntitiesList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var data = await doc.Environment.GetGameData().ConfigureAwait(true);
            var type = parameters.Optional<string>("type", null);
            var query = parameters.Optional<string>("query", null);
            var classes = data.Classes
                .Where(x => x.ClassType != ClassType.Base)
                .Where(x => string.IsNullOrWhiteSpace(type) || string.Equals(x.ClassType.ToString(), type, StringComparison.InvariantCultureIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(query) || x.Name.IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0)
                .OrderBy(x => x.Name, StringComparer.InvariantCultureIgnoreCase)
                .Select(x => new { x.Name, type = x.ClassType.ToString(), x.Description, propertyCount = x.Properties.Count })
                .ToList();
            return ToToken(new { count = classes.Count, entities = classes });
        }

        private async Task<JToken> EntitySchema(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var classname = parameters.Required<string>("classname");
            var data = await doc.Environment.GetGameData().ConfigureAwait(true);
            var schema = data.GetClass(classname);
            if (schema == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"FGD entity not found: {classname}");
            return ToToken(EntitySchemaInfo(schema));
        }

        private async Task<JToken> EntityCreateFromSchema(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var classname = parameters.Required<string>("classname");
            var data = await doc.Environment.GetGameData().ConfigureAwait(true);
            var schema = data.GetClass(classname);
            if (schema == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"FGD entity not found: {classname}");
            var props = new JObject();
            foreach (var prop in schema.Properties.Where(x => !string.IsNullOrWhiteSpace(x.DefaultValue)))
            {
                props[prop.Name] = prop.DefaultValue;
            }
            foreach (var prop in (parameters["properties"] as JObject ?? new JObject()).Properties()) props[prop.Name] = prop.Value;
            var forwarded = new JObject(parameters)
            {
                ["properties"] = props
            };
            return await EntityCreate(forwarded).ConfigureAwait(true);
        }

        private object CompileProfilesList()
        {
            return new
            {
                profiles = new[]
                {
                    new { name = "fast", steps = new[] { "CSG", "BSP" }, runGame = false },
                    new { name = "full", steps = new[] { "CSG", "BSP", "VIS", "RAD" }, runGame = false },
                    new { name = "custom", steps = new string[0], runGame = false }
                }
            };
        }

        private async Task<JToken> CompileRun(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var profile = parameters.Optional("profile", "full").ToLowerInvariant();
            var runId = Guid.NewGuid().ToString("N");
            var log = new CompileRunLog { Id = runId, Profile = profile, StartedUtc = DateTime.UtcNow };
            lock (_compileLock)
            {
                _compileRuns[runId] = log;
                _activeCompileRunId = runId;
            }

            var args = new List<BatchArgument>();
            foreach (var prop in (parameters["arguments"] as JObject ?? new JObject()).Properties())
            {
                args.Add(new BatchArgument { Name = prop.Name, Arguments = prop.Value.Type == JTokenType.Null ? "" : prop.Value.ToString() });
            }

            var steps = CompileStepNames(parameters["steps"] as JArray, profile);
            var options = new BatchOptions
            {
                AllowUserInterruption = parameters.Optional("allowUserInterruption", false),
                RunGame = parameters.Optional("runGame", false),
                AskRunGame = parameters.Optional("askRunGame", false),
                UseCordonBounds = parameters.Optional<bool?>("useCordonBounds", null),
                WorkingDirectory = parameters.Optional<string>("workingDirectory", null),
                BatchSteps = new List<BatchStepType>
                {
                    BatchStepType.CreateWorkingDirectory,
                    BatchStepType.ExportDocument,
                    BatchStepType.RunBuildExecutable,
                    BatchStepType.CheckIfSuccessful,
                    BatchStepType.ProcessBuildResults,
                    BatchStepType.DeleteWorkingDirectory
                }
            };
            if (parameters.Optional("runGame", false)) options.BatchSteps.Add(BatchStepType.RunGame);

            var batch = await doc.Environment.CreateBatch(args, options).ConfigureAwait(true);
            if (steps.Any())
            {
                batch.Steps = batch.Steps.Where(x => steps.Any(step => x.GetType().Name.IndexOf(step, StringComparison.InvariantCultureIgnoreCase) >= 0)).ToList();
            }
            await batch.Run(doc).ConfigureAwait(true);
            log.FinishedUtc = DateTime.UtcNow;
            log.Successful = batch.Successful;
            lock (_compileLock)
            {
                if (_activeCompileRunId == runId) _activeCompileRunId = null;
            }
            return ToToken(new { runId, profile, successful = batch.Successful, logLines = log.Lines.Count });
        }

        private object CompileLogTail(JObject parameters)
        {
            var runId = parameters.Optional<string>("runId", null);
            var count = parameters.Optional("count", 100);
            CompileRunLog log = null;
            lock (_compileLock)
            {
                if (runId == null) log = _compileRuns.Values.OrderByDescending(x => x.StartedUtc).FirstOrDefault();
                else _compileRuns.TryGetValue(runId, out log);
            }
            if (log == null) return new { runId, lines = new string[0] };
            return new { runId = log.Id, successful = log.Successful, lines = log.Lines.Skip(Math.Max(0, log.Lines.Count - count)).ToList() };
        }

        private async Task<JToken> MapValidate(JObject parameters)
        {
            return await ProblemsCheck(parameters).ConfigureAwait(true);
        }

        private async Task<JToken> MapFixAllSafe(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var fixedProblems = new List<object>();
            foreach (var checker in _problemChecks.Select(x => x.Value).Where(x => x.CanFix))
            {
                var problems = await checker.Check(doc, _ => true).ConfigureAwait(true);
                foreach (var problem in problems)
                {
                    await checker.Fix(doc, problem).ConfigureAwait(true);
                    fixedProblems.Add(new { checker = checker.GetType().FullName, problem.Text });
                }
            }
            return ToToken(new { fixedCount = fixedProblems.Count, fixedProblems });
        }

        private async Task<JToken> SelectionFilter(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var filtered = FilterObjects(doc, doc.Selection.ToList(), parameters).ToList();
            await Perform(doc, new IOperation[] { new Deselect(doc.Selection.ToList()), new Select(filtered) }, "selection.filter", false).ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private async Task<JToken> SelectionGrow(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var mode = parameters.Optional("mode", "children").ToLowerInvariant();
            var set = new HashSet<IMapObject>(doc.Selection);
            foreach (var obj in doc.Selection.ToList())
            {
                if (mode == "parents" && obj.Hierarchy.Parent != null) set.Add(obj.Hierarchy.Parent);
                else if (mode == "siblings" && obj.Hierarchy.Parent != null) foreach (var s in obj.Hierarchy.Parent.Hierarchy) set.Add(s);
                else foreach (var child in obj.FindAll()) set.Add(child);
            }
            await Perform(doc, new IOperation[] { new Deselect(doc.Selection.ToList()), new Select(set) }, "selection.grow", false).ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private async Task<JToken> SelectionByBounds(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var bounds = RequiredBox(parameters);
            var mode = parameters.Optional("mode", "intersects").ToLowerInvariant();
            var objects = doc.Map.Root.FindAll()
                .Where(x => x.Hierarchy.Parent != null && x.BoundingBox != null && !x.BoundingBox.IsEmpty())
                .Where(x => mode == "inside" ? x.BoundingBox.ContainedWithin(bounds) : x.BoundingBox.IntersectsWith(bounds))
                .ToList();
            await Perform(doc, new IOperation[] { new Deselect(doc.Selection.ToList()), new Select(objects) }, "selection.by_bounds", false).ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private object HistoryList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var history = HistoryFor(doc);
            return new
            {
                scope = "mcp",
                undoCount = history.Undo.Count,
                redoCount = history.Redo.Count,
                undo = history.Undo.Select(x => new { x.Description, x.CreatedUtc }).ToList(),
                redo = history.Redo.Select(x => new { x.Description, x.CreatedUtc }).ToList()
            };
        }

        private async Task<JToken> Undo(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var history = HistoryFor(doc);
            if (!history.Undo.Any()) return ToToken(new { undone = false, reason = "No MCP history entries." });
            var entry = history.Undo.Last();
            history.Undo.RemoveAt(history.Undo.Count - 1);
            await MapDocumentOperation.Reverse(doc, entry.Operation).ConfigureAwait(true);
            history.Redo.Add(entry);
            return ToToken(new { undone = true, entry.Description });
        }

        private async Task<JToken> Redo(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var history = HistoryFor(doc);
            if (!history.Redo.Any()) return ToToken(new { redone = false, reason = "No MCP redo entries." });
            var entry = history.Redo.Last();
            history.Redo.RemoveAt(history.Redo.Count - 1);
            await MapDocumentOperation.Perform(doc, entry.Operation).ConfigureAwait(true);
            history.Undo.Add(entry);
            return ToToken(new { redone = true, entry.Description });
        }

        private object CordonGet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var cordon = doc.Map.Data.GetOne<CordonBounds>() ?? new CordonBounds();
            return new { enabled = cordon.Enabled, bounds = cordon.Box.ToDto() };
        }

        private async Task<JToken> CordonSet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var bounds = RequiredBox(parameters);
            var enabled = parameters.Optional<bool?>("enabled", null);
            await MapDocumentOperation.Perform(doc, new TrivialOperation(
                d =>
                {
                    var existing = d.Map.Data.GetOne<CordonBounds>();
                    if (existing != null) d.Map.Data.Remove(existing);
                    d.Map.Data.Add(new CordonBounds { Box = bounds, Enabled = enabled ?? existing?.Enabled ?? false });
                },
                c =>
                {
                    var cordon = c.Document.Map.Data.GetOne<CordonBounds>();
                    if (cordon != null) c.Update(cordon);
                })).ConfigureAwait(true);
            return ToToken(CordonGet(parameters));
        }

        private async Task<JToken> CordonEnable(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var enabled = parameters.Required<bool>("enabled");
            var cordon = doc.Map.Data.GetOne<CordonBounds>() ?? new CordonBounds();
            var box = cordon.Box;
            var forwarded = new JObject(parameters)
            {
                ["min"] = JToken.FromObject(box.Start.ToDto()),
                ["max"] = JToken.FromObject(box.End.ToDto()),
                ["enabled"] = enabled
            };
            return await CordonSet(forwarded).ConfigureAwait(true);
        }

        private MapDocument ActiveDocument()
        {
            var doc = ActiveDocumentOrNull();
            if (doc == null) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, "There is no active map document.");
            return doc;
        }

        private MapDocument ActiveDocumentOrNull()
        {
            return _context.Value.Get<MapDocument>("ActiveDocument");
        }

        private MapDocument ResolveDocument(JObject parameters, bool requireExplicit)
        {
            if (parameters["path"] != null)
            {
                var path = parameters.Optional<string>("path", null);
                var byPath = _documents.Value.OpenDocuments.OfType<MapDocument>().FirstOrDefault(x => string.Equals(x.FileName, path, StringComparison.InvariantCultureIgnoreCase));
                if (byPath != null) return byPath;
                throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Open document not found: {path}");
            }

            if (parameters["documentIndex"] != null)
            {
                var index = parameters.Optional("documentIndex", -1);
                var list = _documents.Value.OpenDocuments.OfType<MapDocument>().ToList();
                if (index >= 0 && index < list.Count) return list[index];
                throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Open document index not found: {index}");
            }

            if (requireExplicit) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Document path or documentIndex is required.");
            return ActiveDocument();
        }

        private IMapObject ResolveObject(MapDocument document, long id)
        {
            var obj = document.Map.Root.FindByID(id) ?? document.Map.Root.FindAll().FirstOrDefault(x => x.ID == id);
            if (obj == null) throw new BridgeCommandException(ErrorCodes.ObjectNotFound, $"Map object not found: {id}");
            return obj;
        }

        private IEnumerable<IMapObject> RequestedOrSelectedObjects(MapDocument document, JObject parameters)
        {
            var ids = parameters.Ids();
            if (ids.Any()) return ResolveObjects(document, ids);
            if (!document.Selection.IsEmpty) return document.Selection.ToList();
            throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Object ids or a current selection are required.");
        }

        private static Entity InferBrushEntity(IMapObject obj)
        {
            if (obj is Entity entity) return entity;
            if (obj is Solid && obj.Hierarchy.Parent is Entity parentEntity) return parentEntity;
            return null;
        }

        private static bool IsReservedEntityProperty(string name)
        {
            return string.Equals(name, "classname", StringComparison.InvariantCultureIgnoreCase) ||
                   string.Equals(name, "spawnflags", StringComparison.InvariantCultureIgnoreCase) ||
                   string.Equals(name, "origin", StringComparison.InvariantCultureIgnoreCase);
        }

        private static void AddEntityDataUpdates(IMapObject entity, JObject parameters, ICollection<IOperation> ops)
        {
            var data = entity.Data.GetOne<EntityData>();
            if (data == null) throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Object {entity.ID} is not an entity.");

            var classname = parameters.Optional<string>("classname", null);
            if (!string.IsNullOrWhiteSpace(classname)) ops.Add(new EditEntityDataName(entity.ID, classname));
            if (parameters["spawnflags"] != null) ops.Add(new EditEntityDataFlags(entity.ID, parameters.Optional("spawnflags", data.Flags)));

            var properties = parameters.StringDictionary("properties");
            foreach (var reserved in properties.Keys.Where(IsReservedEntityProperty).ToList()) properties.Remove(reserved);
            if (properties.Count > 0) ops.Add(new EditEntityDataProperties(entity.ID, properties));
        }

        private IEnumerable<IMapObject> ResolveObjects(MapDocument document, IEnumerable<long> ids)
        {
            foreach (var id in ids)
            {
                yield return ResolveObject(document, id);
            }
        }

        private IEnumerable<IMapObject> CandidateObjects(MapDocument document, IEnumerable<long> ids)
        {
            var requested = (ids ?? Array.Empty<long>()).ToArray();
            if (requested.Any()) return ResolveObjects(document, requested);
            if (!document.Selection.IsEmpty) return document.Selection.ToList();
            return document.Map.Root.FindAll().Where(x => x.Hierarchy.Parent != null);
        }

        private IEnumerable<IMapObject> FilterObjects(MapDocument document, IEnumerable<IMapObject> source, JObject parameters)
        {
            var type = parameters.Optional<string>("type", null);
            var classname = parameters.Optional<string>("classname", null);
            var texture = parameters.Optional<string>("texture", null);
            var bounds = parameters["min"] != null || parameters["max"] != null ? RequiredBox(parameters) : null;

            var query = source;
            if (!string.IsNullOrWhiteSpace(type))
            {
                query = query.Where(x => string.Equals(x.GetType().Name, type, StringComparison.InvariantCultureIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(classname))
            {
                query = query.Where(x => string.Equals(x.Data.GetOne<EntityData>()?.Name, classname, StringComparison.InvariantCultureIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(texture))
            {
                query = query.Where(x => x.Data.OfType<Face>().Any(f => string.Equals(f.Texture.Name, texture, StringComparison.InvariantCultureIgnoreCase)));
            }
            if (bounds != null)
            {
                query = query.Where(x => x.BoundingBox != null && x.BoundingBox.IntersectsWith(bounds));
            }
            return query;
        }

        private IEnumerable<FaceRef> ResolveFaceRefsOrObjects(MapDocument document, JObject parameters)
        {
            var refs = ResolveFaceRefs(document, parameters["faceRefs"] as JArray).ToList();
            if (refs.Any()) return refs;

            var selection = document.Map.Data.GetOne<FaceSelection>();
            var selectedFaces = selection?.GetSelectedFaces().Select(x => new FaceRef(x.Key, x.Value)).ToList() ?? new List<FaceRef>();
            if (selectedFaces.Any() && !parameters.Ids().Any()) return selectedFaces;

            return CandidateObjects(document, parameters.Ids()).OfType<Solid>().SelectMany(s => s.Faces.Select(f => new FaceRef(s, f)));
        }

        private IEnumerable<FaceRef> ResolveFaceRefs(MapDocument document, JArray array)
        {
            if (array == null) yield break;
            foreach (var token in array.OfType<JObject>())
            {
                var objectId = token.Required<long>("objectId");
                var faceId = token.Required<long>("faceId");
                var obj = ResolveObject(document, objectId);
                var face = obj.Data.OfType<Face>().FirstOrDefault(x => x.ID == faceId);
                if (face == null) throw new BridgeCommandException(ErrorCodes.ObjectNotFound, $"Face {faceId} was not found on object {objectId}.");
                yield return new FaceRef(obj, face);
            }
        }

        private FaceRef ResolveSingleFaceRef(MapDocument document, JObject parameters)
        {
            if (parameters["objectId"] != null && parameters["faceId"] != null)
            {
                return ResolveFaceRefs(document, new JArray(new JObject
                {
                    ["objectId"] = parameters["objectId"],
                    ["faceId"] = parameters["faceId"]
                })).First();
            }
            return ResolveFaceRefsOrObjects(document, parameters).FirstOrDefault()
                   ?? throw new BridgeCommandException(ErrorCodes.InvalidRequest, "A face reference is required.");
        }

        private Face ResolveFace(MapDocument document, long objectId, long faceId)
        {
            return ResolveObject(document, objectId).Data.OfType<Face>().FirstOrDefault(x => x.ID == faceId);
        }

        private IEnumerable<VertexRef> ResolveVertexRefs(MapDocument document, JArray array)
        {
            if (array == null) yield break;
            foreach (var token in array.OfType<JObject>())
            {
                var faceRef = ResolveFaceRefs(document, new JArray(new JObject
                {
                    ["objectId"] = token.Required<long>("objectId"),
                    ["faceId"] = token.Required<long>("faceId")
                })).First();
                var index = token.Required<int>("vertexIndex");
                if (index < 0 || index >= faceRef.Face.Vertices.Count)
                {
                    throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Vertex index {index} is out of range for face {faceRef.Face.ID}.");
                }
                yield return new VertexRef(faceRef.Object, faceRef.Face, index);
            }
        }

        private List<IOperation> SplitFaceOperation(MapDocument document, FaceRef entry, int a, int b)
        {
            var count = entry.Face.Vertices.Count;
            if (count < 4) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "Only faces with at least four vertices can be split.");
            if (a < 0 || b < 0 || a >= count || b >= count || a == b) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "vertexIndexA and vertexIndexB must reference distinct vertices.");
            if (Math.Abs(a - b) == 1 || Math.Abs(a - b) == count - 1) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Split vertices must not be adjacent.");

            var first = WalkFace(entry.Face, a, b).ToList();
            var second = WalkFace(entry.Face, b, a).ToList();
            var faceA = MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), first);
            var faceB = MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), second);
            return new List<IOperation>
            {
                new RemoveMapObjectData(entry.Object.ID, entry.Face),
                new AddMapObjectData(entry.Object.ID, faceA, faceB)
            };
        }

        private IEnumerable<IOperation> TriangulateFaceOperation(MapDocument document, FaceRef entry, bool poke)
        {
            var vertices = entry.Face.Vertices.ToList();
            if (vertices.Count <= 3) yield break;
            var faces = new List<Face>();
            if (poke)
            {
                var center = vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / vertices.Count;
                for (var i = 0; i < vertices.Count; i++)
                {
                    faces.Add(MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), new[] { vertices[i], vertices[(i + 1) % vertices.Count], center }));
                }
            }
            else
            {
                for (var i = 1; i < vertices.Count - 1; i++)
                {
                    faces.Add(MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), new[] { vertices[0], vertices[i], vertices[i + 1] }));
                }
            }

            yield return new RemoveMapObjectData(entry.Object.ID, entry.Face);
            yield return new AddMapObjectData(entry.Object.ID, faces);
        }

        private static IEnumerable<Vector3> WalkFace(Face face, int start, int end)
        {
            var i = start;
            while (true)
            {
                yield return face.Vertices[i];
                if (i == end) break;
                i = (i + 1) % face.Vertices.Count;
            }
        }

        private static Face MakeFaceFrom(Face source, long id, IEnumerable<Vector3> vertices)
        {
            var face = new Face(id) { Texture = source.Texture.Clone() };
            face.Vertices.AddRange(vertices);
            return face;
        }

        private static object FaceInfo(IMapObject parent, Face face)
        {
            return new
            {
                objectId = parent.ID,
                faceId = face.ID,
                texture = face.Texture.Name,
                rotation = face.Texture.Rotation,
                xScale = face.Texture.XScale,
                yScale = face.Texture.YScale,
                xShift = face.Texture.XShift,
                yShift = face.Texture.YShift,
                normal = face.Plane.Normal.ToDto(),
                vertexCount = face.Vertices.Count,
                vertices = face.Vertices.Select(x => x.ToDto()).ToList()
            };
        }

        private static void ApplyTextureFields(Texture texture, JObject parameters)
        {
            if (parameters["texture"] != null) texture.Name = parameters.Optional<string>("texture", texture.Name);
            if (parameters["name"] != null) texture.Name = parameters.Optional<string>("name", texture.Name);
            if (parameters["rotation"] != null) texture.Rotation = parameters.Optional("rotation", texture.Rotation);
            if (parameters["xShift"] != null) texture.XShift = parameters.Optional("xShift", texture.XShift);
            if (parameters["yShift"] != null) texture.YShift = parameters.Optional("yShift", texture.YShift);
            if (parameters["xScale"] != null) texture.XScale = parameters.Optional("xScale", texture.XScale);
            if (parameters["yScale"] != null) texture.YScale = parameters.Optional("yScale", texture.YScale);
            if (parameters["uAxis"] != null) texture.UAxis = parameters.RequiredVector("uAxis");
            if (parameters["vAxis"] != null) texture.VAxis = parameters.RequiredVector("vAxis");
        }

        private static PlaneDefinition ReadPlaneDefinition(JObject obj)
        {
            Plane plane;
            if (obj["points"] is JArray points && points.Count >= 3)
            {
                plane = new Plane(BridgeParsing.ToVector(points[0]), BridgeParsing.ToVector(points[1]), BridgeParsing.ToVector(points[2]));
            }
            else if (obj["point1"] != null && obj["point2"] != null && obj["point3"] != null)
            {
                plane = new Plane(obj.RequiredVector("point1"), obj.RequiredVector("point2"), obj.RequiredVector("point3"));
            }
            else if (obj["normal"] != null)
            {
                var normal = obj.RequiredVector("normal");
                if (obj["distance"] != null) plane = new Plane(normal, obj.Optional("distance", 0f));
                else plane = new Plane(normal, obj.OptionalVector("point") ?? Vector3.Zero);
            }
            else
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Plane requires points, point1/point2/point3, or normal.");
            }

            var texture = new Texture();
            ApplyTextureFields(texture, obj);
            if (string.IsNullOrWhiteSpace(texture.Name)) texture.Name = "aaatrigger";
            return new PlaneDefinition(plane, texture);
        }

        private static string SolidToMapText(Solid solid)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            foreach (var face in solid.Faces)
            {
                var points = face.Vertices.Take(3).ToList();
                if (points.Count < 3) continue;
                builder.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    "( {0} {1} {2} ) ( {3} {4} {5} ) ( {6} {7} {8} ) {9} [ {10} {11} {12} {13} ] [ {14} {15} {16} {17} ] {18} {19} {20}\r\n",
                    points[0].X, points[0].Y, points[0].Z,
                    points[1].X, points[1].Y, points[1].Z,
                    points[2].X, points[2].Y, points[2].Z,
                    face.Texture.Name,
                    face.Texture.UAxis.X, face.Texture.UAxis.Y, face.Texture.UAxis.Z, face.Texture.XShift,
                    face.Texture.VAxis.X, face.Texture.VAxis.Y, face.Texture.VAxis.Z, face.Texture.YShift,
                    face.Texture.Rotation, face.Texture.XScale, face.Texture.YScale);
            }
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static Solid ParseSolidMapText(string text, UniqueNumberGenerator generator)
        {
            var definitions = new List<PlaneDefinition>();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "{" || trimmed == "}") continue;
                definitions.Add(ParseMapFaceLine(trimmed));
            }
            if (definitions.Count < 4) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Map text must contain at least 4 brush side lines.");

            var polyhedron = new Polyhedron(definitions.Select(x => x.Plane));
            if (!polyhedron.IsValid()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "Map text did not produce a valid convex brush.");
            var solid = new Solid(generator.Next("MapObject"));
            foreach (var polygon in polyhedron.Polygons)
            {
                var definition = definitions.OrderByDescending(x => Vector3.Dot(x.Plane.Normal, polygon.Plane.Normal)).First();
                var face = new Face(generator.Next("Face")) { Texture = definition.Texture.Clone() };
                face.Vertices.AddRange(polygon.Vertices);
                solid.Data.Add(face);
            }
            return solid;
        }

        private static PlaneDefinition ParseMapFaceLine(string line)
        {
            var pattern = @"^\(\s*(?<p1>[^)]*?)\s*\)\s*\(\s*(?<p2>[^)]*?)\s*\)\s*\(\s*(?<p3>[^)]*?)\s*\)\s*(?<tex>\S+)\s*\[\s*(?<u>[^\]]+)\]\s*\[\s*(?<v>[^\]]+)\]\s*(?<rot>-?\d+(?:\.\d+)?)\s*(?<xs>-?\d+(?:\.\d+)?)\s*(?<ys>-?\d+(?:\.\d+)?)";
            var match = Regex.Match(line, pattern);
            if (!match.Success) throw new BridgeCommandException(ErrorCodes.ParseError, "Could not parse map brush side: " + line);
            var u = ParseFloatList(match.Groups["u"].Value, 4);
            var v = ParseFloatList(match.Groups["v"].Value, 4);
            var texture = new Texture
            {
                Name = match.Groups["tex"].Value,
                UAxis = new Vector3(u[0], u[1], u[2]),
                XShift = u[3],
                VAxis = new Vector3(v[0], v[1], v[2]),
                YShift = v[3],
                Rotation = ParseFloat(match.Groups["rot"].Value),
                XScale = ParseFloat(match.Groups["xs"].Value),
                YScale = ParseFloat(match.Groups["ys"].Value)
            };
            return new PlaneDefinition(
                new Plane(ParseMapVector(match.Groups["p1"].Value), ParseMapVector(match.Groups["p2"].Value), ParseMapVector(match.Groups["p3"].Value)),
                texture);
        }

        private static Vector3 ParseMapVector(string text)
        {
            var values = ParseFloatList(text, 3);
            return new Vector3(values[0], values[1], values[2]);
        }

        private static float[] ParseFloatList(string text, int min)
        {
            var values = Regex.Matches(text, @"-?\d+(?:\.\d+)?")
                .Cast<Match>()
                .Select(x => ParseFloat(x.Value))
                .ToArray();
            if (values.Length < min) throw new BridgeCommandException(ErrorCodes.ParseError, "Expected at least " + min + " numeric values in: " + text);
            return values;
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string VertexKey(Vector3 point)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "v:{0:0.###}:{1:0.###}:{2:0.###}", point.X, point.Y, point.Z);
        }

        private static Plane ResolvePlane(JObject parameters)
        {
            if (parameters["point1"] != null && parameters["point2"] != null && parameters["point3"] != null)
            {
                return new Plane(parameters.RequiredVector("point1"), parameters.RequiredVector("point2"), parameters.RequiredVector("point3"));
            }
            if (parameters["normal"] != null)
            {
                return new Plane(parameters.RequiredVector("normal"), parameters.OptionalVector("point") ?? Vector3.Zero);
            }
            throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Clip tools require point1/point2/point3 or normal plus optional point.");
        }

        private static Box RequiredBox(JObject parameters)
        {
            return new Box(parameters.RequiredVector("min"), parameters.RequiredVector("max"));
        }

        private static object EntitySchemaInfo(GameDataObject schema)
        {
            return new
            {
                classname = schema.Name,
                type = schema.ClassType.ToString(),
                schema.Description,
                schema.AdditionalInformation,
                properties = schema.Properties.Select(p => new
                {
                    p.Name,
                    type = p.VariableType.ToString(),
                    p.ShortDescription,
                    p.Description,
                    p.DefaultValue,
                    p.ReadOnly,
                    options = p.Options.Select(o => new { o.Key, o.Description, o.LongDescription, o.On }).ToList()
                }).ToList(),
                behaviours = schema.Behaviours.Select(b => new { b.Name, values = b.Values }).ToList()
            };
        }

        private static List<string> CompileStepNames(JArray explicitSteps, string profile)
        {
            if (explicitSteps != null) return explicitSteps.Select(x => x.Value<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (profile == "fast") return new List<string> { "CSG", "BSP" };
            if (profile == "full") return new List<string> { "CSG", "BSP", "VIS", "RAD" };
            return new List<string>();
        }

        private void AddCompileLog(string kind, string line)
        {
            lock (_compileLock)
            {
                if (string.IsNullOrWhiteSpace(_activeCompileRunId)) return;
                if (!_compileRuns.TryGetValue(_activeCompileRunId, out var log)) return;
                log.Lines.Add($"[{kind}] {line}".TrimEnd());
                if (log.Lines.Count > 4000) log.Lines.RemoveRange(0, log.Lines.Count - 4000);
            }
        }

        private OperationHistory HistoryFor(MapDocument document)
        {
            var key = string.IsNullOrWhiteSpace(document.FileName) ? document.GetHashCode().ToString() : document.FileName;
            if (!_histories.TryGetValue(key, out var history))
            {
                history = new OperationHistory();
                _histories[key] = history;
            }
            return history;
        }

        private static string SafeWindowTitle(System.Diagnostics.Process process)
        {
            try { return process.MainWindowTitle; }
            catch { return ""; }
        }

        private async Task Perform(MapDocument document, IEnumerable<IOperation> operations)
        {
            await Perform(document, operations, "mcp operation").ConfigureAwait(true);
        }

        private async Task Perform(MapDocument document, IEnumerable<IOperation> operations, string description, bool recordHistory = true)
        {
            var list = operations.Where(x => x != null).ToList();
            if (!list.Any()) return;
            var transaction = new Transaction(list);
            await MapDocumentOperation.Perform(document, transaction).ConfigureAwait(true);
            if (recordHistory && !transaction.Trivial)
            {
                var history = HistoryFor(document);
                history.Undo.Add(new HistoryEntry { Operation = transaction, Description = description, CreatedUtc = DateTime.UtcNow });
                history.Redo.Clear();
                if (history.Undo.Count > 100) history.Undo.RemoveAt(0);
            }
        }

        private IEnumerable<ITool> GetTools()
        {
            return (_tools ?? Enumerable.Empty<Lazy<ITool>>())
                .Select(x => x.Value)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name));
        }

        private ITool ResolveTool(string name)
        {
            var resolved = ResolveToolAlias(name);
            var tool = GetTools().FirstOrDefault(x =>
                string.Equals(x.Name, resolved, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().Name, resolved, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().FullName, resolved, StringComparison.InvariantCultureIgnoreCase));
            if (tool != null) return tool;

            var known = GetTools().Select(x => x.Name).OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).ToList();
            throw new BridgeCommandException(
                ErrorCodes.InvalidRequest,
                $"Unknown editor tool '{name}'. Valid tools: {string.Join(", ", known)}.");
        }

        private static string ResolveToolAlias(string name)
        {
            var normalized = BrushCatalog.NormalizeParameterName(name);
            switch (normalized)
            {
                case "brush":
                case "brushTool":
                    return "BrushTool";
                case "vertex":
                case "vm":
                case "vertexTool":
                case "vertexManipulation":
                case "vertexManipulationTool":
                    return "Vertex Manipulation Tool";
                case "select":
                case "selection":
                case "selectTool":
                    return "SelectTool";
                default:
                    return name;
            }
        }

        private IEnumerable<VertexSubtool> GetVertexSubtools()
        {
            return (_vertexSubtools ?? Enumerable.Empty<Lazy<VertexSubtool>>())
                .Select(x => x.Value)
                .Where(x => x != null);
        }

        private VertexSubtool ResolveVertexSubtool(string name)
        {
            var resolved = ResolveVertexSubtoolAlias(name);
            var subtool = GetVertexSubtools().FirstOrDefault(x =>
                string.Equals(x.Title, resolved, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetName(), resolved, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().Name, resolved, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().FullName, resolved, StringComparison.InvariantCultureIgnoreCase));
            if (subtool != null) return subtool;

            var known = GetVertexSubtools().Select(x => x.Title).OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).ToList();
            throw new BridgeCommandException(
                ErrorCodes.InvalidRequest,
                $"Unknown vertex subtool '{name}'. Valid vertex subtools: {string.Join(", ", known)}.");
        }

        private static string ResolveVertexSubtoolAlias(string name)
        {
            var normalized = BrushCatalog.NormalizeParameterName(name);
            switch (normalized)
            {
                case "point":
                case "points":
                case "manipulation":
                case "pointManipulation":
                    return "Point manipulation";
                case "scale":
                case "scaling":
                case "pointScaling":
                    return "Point scaling";
                case "face":
                case "faces":
                case "faceEdit":
                case "faceEditing":
                    return "Face editing";
                default:
                    return name;
            }
        }

        private IEnumerable<IBrush> GetBrushes()
        {
            return (_brushes ?? Enumerable.Empty<Lazy<IBrush>>())
                .Select(x => x.Value)
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name));
        }

        private IBrush ResolveBrush(string type)
        {
            var resolved = BrushCatalog.ResolveTypeName(type);
            var brushes = GetBrushes().ToList();
            var brush = brushes.FirstOrDefault(x => string.Equals(x.Name, resolved, StringComparison.InvariantCultureIgnoreCase)) ??
                        brushes.FirstOrDefault(x => string.Equals(x.GetType().Name, resolved, StringComparison.InvariantCultureIgnoreCase));
            if (brush != null) return brush;

            var known = brushes.Select(x => x.Name).OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).ToList();
            throw new BridgeCommandException(
                ErrorCodes.InvalidRequest,
                $"Unknown brush type '{type}'. Valid brush types: {string.Join(", ", known)}.");
        }

        private static int BrushSortIndex(string name)
        {
            var index = BrushCatalog.DefaultTypes.ToList().FindIndex(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
            return index < 0 ? int.MaxValue : index;
        }

        private static IReadOnlyList<BrushControlBinding> ReadBrushControls(IBrush brush)
        {
            var fields = brush.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(x => typeof(BrushControl).IsAssignableFrom(x.FieldType))
                .Select(x => new { Field = x, Control = x.GetValue(brush) as BrushControl })
                .Where(x => x.Control != null)
                .ToList();

            var controls = SafeControls(brush).Where(x => x != null).ToList();
            return controls.Select((control, index) =>
                {
                    var field = fields.FirstOrDefault(x => ReferenceEquals(x.Control, control))?.Field;
                    var rawName = field == null ? ControlLabel(control) : field.Name.TrimStart('_');
                    return new BrushControlBinding(
                        BrushCatalog.NormalizeParameterName(rawName),
                        ControlKind(control),
                        ControlLabel(control),
                        control,
                        index);
                })
                .ToList();
        }

        private static IReadOnlyList<BrushControl> SafeControls(IBrush brush)
        {
            try
            {
                return brush.GetControls()?.ToList() ?? new List<BrushControl>();
            }
            catch
            {
                return new List<BrushControl>();
            }
        }

        private static string ControlKind(BrushControl control)
        {
            if (control is NumericControl) return "number";
            if (control is BooleanControl) return "boolean";
            if (control is TextControl) return "string";
            if (control is FontChooserControl) return "string";
            return control.GetType().Name;
        }

        private static string ControlLabel(BrushControl control)
        {
            if (control is NumericControl n) return n.LabelText;
            if (control is BooleanControl b) return b.LabelText;
            if (control is TextControl t) return t.LabelText;
            if (control is FontChooserControl f) return f.LabelText;
            return control.GetType().Name;
        }

        private static void ApplyBrushParameters(IBrush brush, JObject parameters)
        {
            if (parameters == null) return;
            var bindings = ReadBrushControls(brush);
            if (!bindings.Any()) return;

            foreach (var property in parameters.Properties())
            {
                if (IsReservedBrushParameter(property.Name)) continue;
                var normalized = BrushCatalog.NormalizeParameterName(property.Name);
                var binding = bindings.FirstOrDefault(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase) ||
                                                           string.Equals(BrushCatalog.NormalizeParameterName(x.Label), normalized, StringComparison.OrdinalIgnoreCase));
                if (binding == null) continue;
                binding.SetValue(property.Value);
            }
        }

        private static bool IsReservedBrushParameter(string name)
        {
            return BrushCatalog.IsReservedParameter(name);
        }

        private static object DocumentInfo(IDocument document)
        {
            if (document == null) return null;
            return new
            {
                name = document.Name,
                path = document.FileName,
                type = document.GetType().FullName,
                hasUnsavedChanges = document.HasUnsavedChanges
            };
        }

        private static object ObjectInfo(IMapObject obj)
        {
            var data = obj.Data.GetOne<EntityData>();
            var origin = obj.Data.GetOne<Origin>();
            return new
            {
                id = obj.ID,
                type = obj.GetType().Name,
                parentId = obj.Hierarchy.Parent?.ID,
                selected = obj.IsSelected,
                bounds = obj.BoundingBox.ToDto(),
                classname = data?.Name,
                spawnflags = data?.Flags,
                origin = origin?.Location.ToDto(),
                properties = data?.Properties,
                children = obj.Hierarchy.Select(x => x.ID).ToList()
            };
        }

        private static bool ObjectMatchesText(IMapObject obj, string text)
        {
            var data = obj.Data.GetOne<EntityData>();
            if (data == null) return false;
            if ((data.Name ?? "").IndexOf(text, StringComparison.InvariantCultureIgnoreCase) >= 0) return true;
            return data.Properties.Any(x =>
                (x.Key ?? "").IndexOf(text, StringComparison.InvariantCultureIgnoreCase) >= 0 ||
                (x.Value ?? "").IndexOf(text, StringComparison.InvariantCultureIgnoreCase) >= 0);
        }

        private static IEnumerable<Vector3> LinesToPoints(IReadOnlyList<Line> lines)
        {
            if (!lines.Any()) yield break;
            yield return lines[0].Start;
            foreach (var line in lines) yield return line.End;
        }

        private static float Degrees(float degrees)
        {
            return (float)(Math.PI / 180.0 * degrees);
        }

        private static JToken ToToken(object value)
        {
            return value == null ? JValue.CreateNull() : JToken.FromObject(value);
        }

        private static void Log(string message)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(McpBridgeConfig.GetDefaultConfigPath());
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                File.AppendAllText(System.IO.Path.Combine(directory, "bridge.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private sealed class FaceRef
        {
            public FaceRef(IMapObject obj, Face face)
            {
                Object = obj;
                Face = face;
            }

            public IMapObject Object { get; }
            public Face Face { get; }
        }

        private sealed class VertexRef
        {
            public VertexRef(IMapObject obj, Face face, int vertexIndex)
            {
                Object = obj;
                Face = face;
                VertexIndex = vertexIndex;
            }

            public IMapObject Object { get; }
            public Face Face { get; }
            public int VertexIndex { get; }
        }

        private sealed class PlaneDefinition
        {
            public PlaneDefinition(Plane plane, Texture texture)
            {
                Plane = plane;
                Texture = texture;
            }

            public Plane Plane { get; }
            public Texture Texture { get; }
        }

        private sealed class OperationHistory
        {
            public List<HistoryEntry> Undo { get; } = new List<HistoryEntry>();
            public List<HistoryEntry> Redo { get; } = new List<HistoryEntry>();
        }

        private sealed class HistoryEntry
        {
            public IOperation Operation { get; set; }
            public string Description { get; set; }
            public DateTime CreatedUtc { get; set; }
        }

        private sealed class CompileRunLog
        {
            public string Id { get; set; }
            public string Profile { get; set; }
            public DateTime StartedUtc { get; set; }
            public DateTime? FinishedUtc { get; set; }
            public bool Successful { get; set; }
            public List<string> Lines { get; } = new List<string>();
        }

        private sealed class BrushControlBinding
        {
            public BrushControlBinding(string name, string kind, string label, BrushControl control, int index)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "parameter" + index : name;
                Kind = kind;
                Label = label;
                Control = control;
                Index = index;
            }

            public string Name { get; }
            public string Kind { get; }
            public string Label { get; }
            public BrushControl Control { get; }
            public int Index { get; }

            public object ToDto()
            {
                if (Control is NumericControl n)
                {
                    return new
                    {
                        name = Name,
                        label = Label,
                        type = Kind,
                        value = n.Value,
                        minimum = n.Minimum,
                        maximum = n.Maximum,
                        precision = n.Precision,
                        increment = n.Increment,
                        enabled = n.ControlEnabled
                    };
                }

                if (Control is BooleanControl b)
                {
                    return new
                    {
                        name = Name,
                        label = Label,
                        type = Kind,
                        value = b.Checked,
                        enabled = b.ControlEnabled
                    };
                }

                if (Control is TextControl t)
                {
                    return new
                    {
                        name = Name,
                        label = Label,
                        type = Kind,
                        value = t.EnteredText
                    };
                }

                if (Control is FontChooserControl f)
                {
                    return new
                    {
                        name = Name,
                        label = Label,
                        type = Kind,
                        value = f.FontName
                    };
                }

                return new
                {
                    name = Name,
                    label = Label,
                    type = Kind
                };
            }

            public void SetValue(JToken value)
            {
                if (value == null || value.Type == JTokenType.Null) return;
                if (Control is NumericControl n)
                {
                    var number = value.Value<decimal>();
                    if (number < n.Minimum || number > n.Maximum)
                    {
                        throw new BridgeCommandException(
                            ErrorCodes.InvalidRequest,
                            $"Brush parameter '{Name}' must be between {n.Minimum} and {n.Maximum}.");
                    }
                    n.Value = number;
                }
                else if (Control is BooleanControl b)
                {
                    b.Checked = value.Value<bool>();
                }
                else if (Control is TextControl t)
                {
                    t.EnteredText = value.Value<string>();
                }
                else if (Control is FontChooserControl f)
                {
                    f.FontName = value.Value<string>();
                }
            }
        }
    }
}
