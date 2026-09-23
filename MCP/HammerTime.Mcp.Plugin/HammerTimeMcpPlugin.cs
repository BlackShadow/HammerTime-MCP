using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using Sledge.BspEditor.Compile;
using LogicAndTrick.Oy;
using Newtonsoft.Json.Linq;
using Sledge.BspEditor.Components;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Environment;
using Sledge.BspEditor.Editing.Commands.Pointfile;
using Sledge.BspEditor.Editing.History;
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
using Sledge.BspEditor.Rendering.Viewport;
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
using Sledge.Providers.Texture;
using Sledge.Rendering.Cameras;
using Sledge.Rendering.Engine;
using Sledge.Rendering.Viewports;
using Sledge.Shell;
using Sledge.Shell.Registers;
using IOPath = System.IO.Path;
using MapGroup = Sledge.BspEditor.Primitives.MapObjects.Group;
using Plane = Sledge.DataStructures.Geometric.Plane;
using TransformOperation = Sledge.BspEditor.Modification.Operations.Mutation.Transform;
using TransformTexturesScale = Sledge.BspEditor.Modification.Operations.Mutation.TransformTexturesScale;
using TransformTexturesUniform = Sledge.BspEditor.Modification.Operations.Mutation.TransformTexturesUniform;

namespace HammerTime.Mcp.Plugin
{
    [Export(typeof(IStartupHook))]
    [Export(typeof(IShutdownHook))]
    public sealed partial class HammerTimeMcpPlugin : IStartupHook, IShutdownHook
    {
        [Import] private Lazy<DocumentRegister> _documents;
        [Import] private Lazy<IContext> _context;
        [Import] private Lazy<McpOverlay> _overlay;
        [Import(AllowDefault = true)] private Lazy<EngineInterface> _engine;
        [ImportMany] private IEnumerable<Lazy<IProblemCheck>> _problemChecks;
        [ImportMany] private IEnumerable<Lazy<IBrush>> _brushes;
        [ImportMany] private IEnumerable<Lazy<ITool>> _tools;
        [ImportMany] private IEnumerable<Lazy<VertexSubtool>> _vertexSubtools;
        [ImportMany] private IEnumerable<Lazy<IDocumentLoader>> _loaders;

        private McpBridgeConfig _config;
        private McpNamedPipeServer _server;
        // Keyed by MapDocument instance identity so history survives saving an untitled doc
        // (its FileName changes on save). Guarded by _historyLock for concurrent pipe handlers.
        private readonly ConditionalWeakTable<MapDocument, OperationHistory> _histories = new ConditionalWeakTable<MapDocument, OperationHistory>();
        private readonly object _historyLock = new object();
        // Stable per-document ids (12 hex chars) and per-document request gates, keyed by instance identity
        // so a closed document releases both.
        private static readonly ConditionalWeakTable<MapDocument, string> DocumentIds = new ConditionalWeakTable<MapDocument, string>();
        private static readonly ConditionalWeakTable<MapDocument, SemaphoreSlim> DocumentGates = new ConditionalWeakTable<MapDocument, SemaphoreSlim>();
        // How long a request waits for another request on the same document before failing.
        private static readonly TimeSpan DocumentGateTimeout = TimeSpan.FromMinutes(5);
        private const int MaxCompileRuns = 20;
        private readonly Dictionary<string, CompileRunLog> _compileRuns = new Dictionary<string, CompileRunLog>(StringComparer.OrdinalIgnoreCase);
        private readonly object _compileLock = new object();
        private string _activeCompileRunId;

        // Mirror of HammerTime.Mcp.Cli's EditorProcessNames for exact process-name matching.
        private static readonly string[] EditorProcessNames = { "Hammertime.Editor", "HammertimeEditor", "Sledge.Editor" };

        public Task OnStartup()
        {
            _config = McpBridgeConfig.LoadOrCreate(null, AppDomain.CurrentDomain.BaseDirectory);
            Log("Starting bridge on pipe " + _config.PipeName);
            Oy.Subscribe<string>("Compile:Output", line => AddCompileLog("output", line));
            Oy.Subscribe<string>("Compile:Error", line => AddCompileLog("error", line));
            Oy.Subscribe<string>("Compile:Information", line => AddCompileLog("info", line));
            // Creating the overlay here subscribes it to document activate/close before any map opens.
            _overlay.Value.SetActiveDocument(ActiveDocumentOrNull());
            _server = new McpNamedPipeServer(_config.PipeName, HandleRequest, Log);
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
            if (!TokenMatches(request.Token, _config.Token))
            {
                Log("Unauthorized request " + request.Method + " " + request.Id);
                return BridgeResponse.Fail(request.Id, ErrorCodes.Unauthorized, "Invalid MCP bridge token.");
            }

            var parameters = request.Params ?? new JObject();
            SemaphoreSlim gate = null;
            var acquired = false;
            try
            {
                // Requests on the same document run one at a time: pipe connections are handled concurrently,
                // and two edits interleaving on one map would corrupt its (and the MCP) history.
                if (NeedsDocumentGate(request.Method))
                {
                    var target = TryResolveDocumentForLock(request.Method, parameters);
                    if (target != null) gate = GateFor(target);
                }
                if (gate != null)
                {
                    acquired = await gate.WaitAsync(DocumentGateTimeout).ConfigureAwait(false);
                    if (!acquired)
                    {
                        return BridgeResponse.Fail(request.Id, ErrorCodes.InvalidOperation, $"The document is busy with another operation (waited {DocumentGateTimeout.TotalMinutes:0} minutes); retry later.");
                    }
                }

                var result = await Dispatch(request.Method, parameters).ConfigureAwait(false);
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
            catch (Exception ex) when (IsParameterConversionError(ex))
            {
                // A parameter value of the wrong shape is the caller's mistake, not an editor failure.
                Log("Invalid request " + request.Method + ": " + ex);
                return BridgeResponse.Fail(request.Id, ErrorCodes.InvalidRequest, ex.Message);
            }
            catch (Exception ex)
            {
                Log("Unhandled bridge error: " + ex);
                return BridgeResponse.Fail(request.Id, ErrorCodes.EditorUnavailable, ex.Message);
            }
            finally
            {
                if (acquired) gate.Release();
            }
        }

        private static bool TokenMatches(string presented, string expected)
        {
            if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
        }

        // A conversion failure raised while reading a request parameter (JToken conversions, directly or via
        // BridgeParsing). Argument/format errors from anywhere else are internal failures and stay editor errors.
        private static bool IsParameterConversionError(Exception ex)
        {
            if (!(ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is InvalidCastException ||
                  ex is OverflowException || ex is ArgumentException))
            {
                return false;
            }

            var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
            if (frames == null) return false;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                var type = method?.DeclaringType;
                if (type == null) continue;
                if (type == typeof(BridgeParsing)) return true;
                if (type.Namespace == "Newtonsoft.Json.Linq" &&
                    (method.Name == "op_Explicit" || method.Name == "ToObject" || method.Name == "Value" ||
                     method.Name == "Values" || method.Name == "Convert"))
                {
                    return true;
                }
            }
            return false;
        }

        private static SemaphoreSlim GateFor(MapDocument document)
        {
            return DocumentGates.GetValue(document, _ => new SemaphoreSlim(1, 1));
        }

        // Methods that never act on a document skip the gate (a compile holding its document's gate must not
        // block listing documents or tailing the compile log).
        private static bool NeedsDocumentGate(string method)
        {
            switch (method)
            {
                case BridgeMethods.Status:
                case BridgeMethods.Doctor:
                case BridgeMethods.SkillGet:
                case BridgeMethods.DocumentsList:
                case BridgeMethods.DocumentsNew:
                case BridgeMethods.DocumentsOpen:
                case BridgeMethods.DocumentsOpenText:
                case BridgeMethods.DocumentsActivate:
                case BridgeMethods.EditorToolsList:
                case BridgeMethods.EditorToolActivate:
                case BridgeMethods.BrushTypesList:
                case BridgeMethods.VertexSubtoolsList:
                case BridgeMethods.CompileProfilesList:
                case BridgeMethods.CompileLogTail:
                    return false;
                default:
                    return true;
            }
        }

        // Best-effort document the request will operate on (for the gate); null when it cannot be told in advance.
        private MapDocument TryResolveDocumentForLock(string method, JObject parameters)
        {
            try
            {
                switch (method)
                {
                    // The capture reads the on-screen viewports, which show the active document
                    case BridgeMethods.ViewportCapture:
                        return ActiveDocumentOrNull();
                    // path is the save-as destination here, not a document selector
                    case BridgeMethods.DocumentsSave:
                    case BridgeMethods.DocumentsExport:
                        return ResolveSaveTarget(parameters, parameters.Optional<string>("path", null));
                    // path is the pointfile
                    case BridgeMethods.LeaksLoadPointfile:
                        return ResolveDocument(parameters, false, pathSelectsDocument: false);
                }
                return ResolveDocument(parameters, false);
            }
            catch (Exception ex) when (ex is BridgeCommandException || IsParameterConversionError(ex))
            {
                return null;
            }
        }

        private async Task<JToken> Dispatch(string method, JObject parameters)
        {
            switch (method)
            {
                case BridgeMethods.Status: return ToToken(Status());
                case BridgeMethods.Doctor: return ToToken(Doctor());
                case BridgeMethods.SkillGet: return ToToken(SkillGet());
                case BridgeMethods.DocumentsList: return ToToken(DocumentsList());
                case BridgeMethods.DocumentsNew: return await DocumentsNew(parameters);
                case BridgeMethods.DocumentsOpen: return await DocumentsOpen(parameters);
                case BridgeMethods.DocumentsOpenText: return await DocumentsOpenText(parameters);
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
                case BridgeMethods.ViewportCapture: return await ViewportCapture(parameters);
                case BridgeMethods.ViewportCameraGet: return ToToken(ViewportCameraGet(parameters));
                case BridgeMethods.ViewportCameraSet: return ToToken(ViewportCameraSet(parameters));
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
                case BridgeMethods.TexturePreviewSheet: return await TexturePreviewSheet(parameters);
                case BridgeMethods.TextureApply: return await TextureApply(parameters);
                case BridgeMethods.TextureReplace: return await TextureReplace(parameters);
                case BridgeMethods.TextureAlignFace: return await TextureAlignFace(parameters);
                case BridgeMethods.TextureCopyFromFace: return await TextureCopyFromFace(parameters);
                case BridgeMethods.TextureProject: return await TextureProject(parameters);
                case BridgeMethods.TextureApplySmart: return await TextureApplySmart(parameters);
                case BridgeMethods.TextureAudit: return await TextureAudit(parameters);
                case BridgeMethods.MapDesignAudit: return await MapDesignAudit(parameters);
                case BridgeMethods.FaceList: return ToToken(FaceList(parameters));
                case BridgeMethods.FaceSelect: return await FaceSelect(parameters);
                case BridgeMethods.FaceTextureSet: return await FaceTextureSet(parameters);
                case BridgeMethods.FaceDelete: return await FaceDelete(parameters);
                case BridgeMethods.ObjectExportMapText: return ToToken(ObjectExportMapText(parameters));
                case BridgeMethods.ObjectImportMapText: return await ObjectImportMapText(parameters);
                case BridgeMethods.ObjectImportMapTextBatch: return await ObjectImportMapTextBatch(parameters);
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
                case BridgeMethods.OverlayClear: return ToToken(OverlayClear(parameters));
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
            var skill = SkillSummary();
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
                skillPath = skill.path,
                skillExists = skill.exists,
                skillHash = skill.hash,
                activeDocument = active == null ? null : DocumentInfo(active),
                openDocumentCount = _documents.Value.OpenDocuments.Count
            };
        }

        private object Doctor()
        {
            var configPath = McpBridgeConfig.GetDefaultConfigPath();
            var pluginDll = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "HammerTime.Mcp.Plugin.dll");
            var skill = SkillSummary();
            var hammerTimeProcesses = System.Diagnostics.Process.GetProcesses()
                .Where(x => EditorProcessNames.Any(name => string.Equals(x.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
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
                skillPath = skill.path,
                skillExists = skill.exists,
                skillHash = skill.hash,
                pluginVersion = typeof(HammerTimeMcpPlugin).Assembly.GetName().Version?.ToString(),
                sharedVersion = typeof(BridgeMethods).Assembly.GetName().Version?.ToString(),
                runningHammerTimeProcesses = hammerTimeProcesses,
                openDocumentCount = _documents.Value.OpenDocuments.Count,
                activeDocument = ActiveDocumentOrNull() == null ? null : DocumentInfo(ActiveDocumentOrNull())
            };
        }

        private object SkillGet()
        {
            var skill = SkillSummary();
            return new
            {
                installed = skill.exists,
                path = skill.path,
                hash = skill.hash,
                text = skill.exists ? File.ReadAllText(skill.path) : null
            };
        }

        private (string path, bool exists, string hash) SkillSummary()
        {
            var path = string.IsNullOrWhiteSpace(_config?.SkillPath) ? McpBridgeConfig.GetDefaultSkillPath() : _config.SkillPath;
            var exists = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            var hash = exists ? ComputeFileSha256(path) : null;
            return (path, exists, hash);
        }

        private object DocumentsList()
        {
            return new
            {
                active = ActiveDocumentOrNull()?.FileName,
                documents = _documents.Value.OpenDocuments.Select(DocumentInfo).ToList()
            };
        }

        private async Task<JToken> DocumentsNew(JObject parameters)
        {
            var loaderHint = parameters.Optional("loaderHint", "");
            var loaders = _loaders.Select(x => x.Value).Where(x => x.CanLoad(null)).ToList();
            if (!loaders.Any())
            {
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, "No document loader can create a blank map document.");
            }

            var loader = string.IsNullOrWhiteSpace(loaderHint)
                ? null
                : loaders.FirstOrDefault(x => string.Equals(x.GetType().Name, loaderHint, StringComparison.InvariantCultureIgnoreCase));
            if (loader == null) loader = loaders[0];

            var doc = await _documents.Value.NewDocument(loader).ConfigureAwait(true);
            if (doc == null)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Loader '{loader.GetType().Name}' did not create a document.");
            }

            return ToToken(new
            {
                created = true,
                loader = loader.GetType().Name,
                activeDocument = DocumentInfo(doc),
                openDocumentCount = _documents.Value.OpenDocuments.Count
            });
        }

        private async Task<JToken> DocumentsOpen(JObject parameters)
        {
            var path = parameters.Required<string>("path");
            var loaderHint = parameters.Optional("loaderHint", "");
            if (!File.Exists(path)) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"File not found: {path}");

            var doc = await _documents.Value.OpenDocument(path, loaderHint).ConfigureAwait(true);
            // OpenDocument returns null when the file was already open (it just activates it)
            var opened = doc ?? _documents.Value.GetDocumentByFileName(path) ?? FindOpenDocument(path);
            return ToToken(new
            {
                opened = opened != null,
                alreadyOpen = doc == null && opened != null,
                document = DocumentInfo(opened),
                activeDocument = DocumentInfo(ActiveDocumentOrNull())
            });
        }

        private async Task<JToken> DocumentsOpenText(JObject parameters)
        {
            var text = parameters.Required<string>("text");
            var loaderHint = parameters.Optional("loaderHint", "");
            var name = parameters.Optional<string>("name", null) ?? "Untitled";
            var tempPath = IOPath.Combine(IOPath.GetTempPath(), $"hammertime_open_text_{Guid.NewGuid():N}.map");
            File.WriteAllText(tempPath, text, Encoding.ASCII);
            IDocument doc;
            try
            {
                // The loader reads the whole file into memory (result.Map) and disposes its
                // stream before returning, so the temp file is not needed after Load completes.
                // We also clear doc.FileName below, so a later save never targets the temp path.
                doc = await _documents.Value.OpenDocument(tempPath, loaderHint).ConfigureAwait(true);
            }
            finally
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) { Log("DocumentsOpenText: failed to delete temp file " + tempPath + ": " + ex.Message); }
            }
            if (doc != null)
            {
                doc.FileName = "";
                doc.Name = name;
            }
            return ToToken(new
            {
                opened = doc != null,
                name = doc?.Name,
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
            var path = parameters.Optional<string>("path", null);
            var doc = ResolveSaveTarget(parameters, path);

            // path is the save-as DESTINATION; only fall back to the document's own file
            // name when no path was supplied (in-place save).
            var destination = !string.IsNullOrWhiteSpace(path) ? path : doc.FileName;
            if (string.IsNullOrWhiteSpace(destination))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Save requires a path when the document has no file name.");
            }

            // SaveDocument (switchFileName=true) writes to the path and sets FileName, so
            // an untitled document becomes titled at destination after saving.
            var saved = await _documents.Value.SaveDocument(doc, destination, parameters.Optional("loaderHint", "")).ConfigureAwait(true);
            return ToToken(new { saved, savedAs = destination, document = DocumentInfo(doc) });
        }

        private async Task<JToken> DocumentsExport(JObject parameters)
        {
            var path = parameters.Required<string>("path");
            var doc = ResolveSaveTarget(parameters, path);
            var saved = await _documents.Value.ExportDocument(doc, path, parameters.Optional("loaderHint", "")).ConfigureAwait(true);
            return ToToken(new { exported = saved, path, exportedTo = path });
        }

        // Resolve the target document for save/export. Unlike ResolveDocument, `path` is
        // treated as a save/export DESTINATION rather than a hard document selector: this
        // fixes the untitled-document catch-22 where a destination path that matches no
        // open document would otherwise fail with document_not_found. Back-compat: when
        // `path` DOES match an open document's file name (or name), that document is the
        // target (in-place save to that path). Otherwise a documentIndex, if present,
        // selects the target; failing that the active document is used.
        private MapDocument ResolveSaveTarget(JObject parameters, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var byPath = FindOpenDocument(path);
                if (byPath != null) return byPath;
            }

            if (parameters["documentId"] != null)
            {
                var id = parameters.Optional<string>("documentId", null);
                return DocumentById(id) ?? throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Open document id not found: {id}");
            }

            if (parameters["documentIndex"] != null)
            {
                var index = parameters.Optional("documentIndex", -1);
                var list = _documents.Value.OpenDocuments.OfType<MapDocument>().ToList();
                if (index >= 0 && index < list.Count) return list[index];
                throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Open document index not found: {index}");
            }

            return ActiveDocument();
        }

        private async Task<JToken> DocumentsClose(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var force = parameters.Optional("force", false);
            if (!force && doc.HasUnsavedChanges)
            {
                // Never raise the editor's save prompt from a bridge call: the modal would block until someone clicks it.
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Document '{doc.Name}' has unsaved changes: save it first (documents_save) or pass force:true to discard them.");
            }

            var info = DocumentInfo(doc);
            if (force)
            {
                await _documents.Value.ForceCloseDocument(doc).ConfigureAwait(true);
                return ToToken(new { closed = true, force = true, document = info });
            }

            var closed = await _documents.Value.RequestCloseDocument(doc).ConfigureAwait(true);
            return ToToken(new { closed, force = false, document = info });
        }

        private object MapSnapshot(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("maxObjects", 2000);
            // Exclude the root/world object itself, matching MapSearch's Parent != null filter.
            var objects = doc.Map.Root.FindAll().Where(x => x.Hierarchy.Parent != null).ToList();
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

            var all = matches.ToList();
            var list = all.Take(max).Select(ObjectInfo).ToList();
            return new { total = all.Count, returned = list.Count, objects = list };
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

            if (mode == "replace") operations.AddRange(ReplaceSelectionOps(doc, objects));
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

        private sealed class ViewportCaptureTarget
        {
            public ViewportMapDocumentControl Control;
            public IViewport Viewport;
            public string View;
            public bool Focused;
            public object Camera;
            public Rectangle ScreenBounds;
            public bool Visible;
        }

        private async Task<JToken> ViewportCapture(JObject parameters)
        {
            // The on-screen viewports always show the active document: a selector naming another one cannot be
            // honoured, and the render mode must be switched on the document actually being captured.
            var requested = ResolveDocument(parameters, false);
            var doc = ActiveDocument();
            var selectorIgnored = !ReferenceEquals(requested, doc);
            var views = parameters.Optional("views", "all").ToLowerInvariant();
            var method = parameters.Optional("method", "auto").ToLowerInvariant();
            var includeOverlays = parameters.Optional("includeOverlays", false);
            var format = parameters.Optional("format", "png").ToLowerInvariant();
            var jpegQuality = Math.Max(1, Math.Min(100, parameters.Optional("jpegQuality", 85)));
            var maxWidth = parameters.Optional("maxWidth", 1024);
            var maxHeight = parameters.Optional("maxHeight", 1024);
            var waitForFrameMs = Math.Max(0, parameters.Optional("waitForFrameMs", 250));
            var renderMode = parameters.Optional<string>("renderMode", null)?.ToLowerInvariant();
            var restoreRenderMode = parameters.Optional("restoreRenderMode", true);

            // Optional inline camera pose applied to the selected viewports before the
            // freshness wait, so the captured frame reflects the requested pose even if
            // the user's mouse triggers editor freelook between separate MCP calls.
            var cameraApply = parameters["camera"] is JObject cameraObj ? ParseCameraApply(cameraObj) : null;

            if (renderMode == "flat")
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "flat render mode is not supported by the HammerTime engine (textured and wireframe only).");
            }
            if (renderMode != null && renderMode != "textured" && renderMode != "wireframe")
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "renderMode must be textured or wireframe.");
            }
            if (format != "png" && format != "jpeg" && format != "jpg")
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "format must be png or jpeg.");
            }
            if (method != "auto" && method != "gpu" && method != "printwindow" && method != "screen")
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "method must be auto, gpu, printwindow, or screen.");
            }

            var host = MapDocumentControlHost.Instance;
            if (host == null)
            {
                throw new BridgeCommandException(ErrorCodes.EditorUnavailable, "Map document viewport host is not available.");
            }

            var engine = _engine?.Value;
            var topWarnings = new List<string>();
            if (selectorIgnored) topWarnings.Add("documentSelectorIgnored");

            // Step 1: optionally switch render mode (document-global) and let the scene rebuild.
            bool changedRenderMode = false;
            bool originalWireframe = false;
            if (renderMode != null)
            {
                var desiredWireframe = renderMode == "wireframe";
                host.InvokeSync(() =>
                {
                    var flags = doc.Map.Data.GetOne<DisplayFlags>() ?? new DisplayFlags();
                    originalWireframe = flags.Wireframe;
                    if (flags.Wireframe != desiredWireframe)
                    {
                        flags.Wireframe = desiredWireframe;
                        doc.Map.Data.Replace(flags);
                        Oy.Publish("SettingsChanged", new object());
                        changedRenderMode = true;
                    }
                });
                if (changedRenderMode)
                {
                    // No completion event exists for the async SceneManager rebuild; settle briefly.
                    await Task.Delay(400).ConfigureAwait(false);
                }
            }

            try
            {
                // Step 2: gather matching viewport targets on the UI thread (metadata + references).
                var targets = new List<ViewportCaptureTarget>();
                host.InvokeSync(() =>
                {
                    foreach (var control in host.GetControls().OfType<ViewportMapDocumentControl>())
                    {
                        var view = ViewName(control.Camera);
                        var is2d = control.Camera is OrthographicCamera;
                        var is3d = control.Camera is PerspectiveCamera;
                        if (!ViewFilterMatches(views, view, is2d, is3d, control.IsFocused)) continue;

                        // Apply the requested pose before reading CameraInfo so the
                        // returned state (and the frame we wait for) reflect it. 3D fields
                        // land on perspective cams, center/zoom on ortho cams.
                        if (cameraApply != null) ApplyCameraToControl(control, cameraApply);

                        var client = control.Control.ClientRectangle;
                        targets.Add(new ViewportCaptureTarget
                        {
                            Control = control,
                            Viewport = ViewportReadback.TryGetViewport(control),
                            View = view,
                            Focused = control.IsFocused,
                            Camera = CameraInfo(control.Camera),
                            ScreenBounds = control.Control.RectangleToScreen(control.Control.ClientRectangle),
                            Visible = control.Control.Visible && client.Width > 0 && client.Height > 0
                        });
                    }
                });

                var captures = new List<object>();
                var errors = new List<object>();

                foreach (var target in targets)
                {
                    var warnings = new List<string>();
                    try
                    {
                        if (target.Viewport != null && engine != null && waitForFrameMs > 0)
                        {
                            await ViewportReadback.WaitForFreshFrame(target.Viewport, engine, waitForFrameMs).ConfigureAwait(false);
                        }

                        var tiers = BuildCaptureTierOrder(method, includeOverlays);
                        Bitmap bitmap = null;
                        string usedMethod = null;
                        var tierErrors = new List<string>();
                        foreach (var tier in tiers)
                        {
                            try
                            {
                                bitmap = CaptureViewportTier(tier, target, engine, host);
                                if (bitmap != null)
                                {
                                    usedMethod = tier;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                tierErrors.Add(tier + ": " + ex.Message);
                            }
                        }

                        if (bitmap == null)
                        {
                            errors.Add(new { view = target.View, error = string.Join("; ", tierErrors) });
                            continue;
                        }

                        using (bitmap)
                        {
                            if (includeOverlays && usedMethod != "screen") warnings.Add("overlaysNotIncluded");
                            if (ViewportReadback.IsMostlyBlack(bitmap)) warnings.Add("imageMostlyBlack");

                            using (var output = ResizeIfNeeded(bitmap, maxWidth, maxHeight))
                            {
                                var (data, mimeType) = ViewportReadback.Encode(output, format, jpegQuality);
                                captures.Add(new
                                {
                                    name = "viewport-" + target.View,
                                    view = target.View,
                                    captureMethod = usedMethod,
                                    mimeType,
                                    data,
                                    width = output.Width,
                                    height = output.Height,
                                    focused = target.Focused,
                                    camera = target.Camera,
                                    screenBounds = RectInfo(target.ScreenBounds),
                                    warnings
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new { view = target.View, error = ex.Message });
                    }
                }

                if (!captures.Any() && errors.Any())
                {
                    throw new BridgeCommandException(ErrorCodes.EditorUnavailable, "Viewport capture failed: " + string.Join("; ", errors.Select(x => JObject.FromObject(x)["error"]?.ToString())));
                }

                if (changedRenderMode && !restoreRenderMode) topWarnings.Add("renderModeNotRestored");

                return ToToken(new
                {
                    document = DocumentInfo(doc),
                    viewFilter = views,
                    method,
                    format,
                    captured = captures.Count,
                    images = captures,
                    errors,
                    warnings = topWarnings
                });
            }
            finally
            {
                // Step 4: restore render mode if we changed it.
                if (changedRenderMode && restoreRenderMode)
                {
                    host.InvokeSync(() =>
                    {
                        var flags = doc.Map.Data.GetOne<DisplayFlags>() ?? new DisplayFlags();
                        if (flags.Wireframe != originalWireframe)
                        {
                            flags.Wireframe = originalWireframe;
                            doc.Map.Data.Replace(flags);
                            Oy.Publish("SettingsChanged", new object());
                        }
                    });
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        }

        private static string[] BuildCaptureTierOrder(string method, bool includeOverlays)
        {
            if (method != "auto") return new[] { method };
            return includeOverlays
                ? new[] { "screen", "gpu", "printwindow" }
                : new[] { "gpu", "printwindow", "screen" };
        }

        private static Bitmap CaptureViewportTier(string tier, ViewportCaptureTarget target, EngineInterface engine, MapDocumentControlHost host)
        {
            switch (tier)
            {
                case "gpu":
                    if (engine == null || ViewportReadback.TryGetDevice() == null || target.Viewport?.ViewportRenderTexture == null)
                    {
                        throw new InvalidOperationException("GPU readback is not available for this viewport.");
                    }
                    // CaptureGpu pauses the render thread itself; no UI-thread dependency.
                    return ViewportReadback.CaptureGpu(target.Viewport, engine);
                case "printwindow":
                {
                    Bitmap result = null;
                    host.InvokeSync(() => { result = CapturePrintWindow(target.Control.Control); });
                    return result;
                }
                case "screen":
                    return CaptureScreen(target.ScreenBounds, target.Visible);
                default:
                    throw new InvalidOperationException("Unknown capture method '" + tier + "'.");
            }
        }

        private object ViewportCameraGet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var views = parameters.Optional("views", "all").ToLowerInvariant();
            var host = MapDocumentControlHost.Instance;
            if (host == null)
            {
                throw new BridgeCommandException(ErrorCodes.EditorUnavailable, "Map document viewport host is not available.");
            }

            var list = new List<object>();
            host.InvokeSync(() =>
            {
                foreach (var control in host.GetControls().OfType<ViewportMapDocumentControl>())
                {
                    var view = ViewName(control.Camera);
                    var is2d = control.Camera is OrthographicCamera;
                    var is3d = control.Camera is PerspectiveCamera;
                    if (!ViewFilterMatches(views, view, is2d, is3d, control.IsFocused)) continue;
                    list.Add(new { view, focused = control.IsFocused, camera = CameraInfo(control.Camera) });
                }
            });

            return new { document = DocumentInfo(doc), viewFilter = views, viewports = list };
        }

        // Parsed camera fields shared by viewport.camera_set and viewport.capture's
        // inline camera parameter. 3D fields apply to perspective cameras; center/zoom
        // apply to orthographic cameras.
        private sealed class CameraApply
        {
            public Vector3? Position;
            public Vector3? LookAt;
            public Vector3? Direction;
            public Vector3? AnglesDegrees;
            public float? Fov;
            public Vector3? Center;
            public float? Zoom;

            public bool Has3d => Position.HasValue || LookAt.HasValue || Direction.HasValue || AnglesDegrees.HasValue || Fov.HasValue;
            public bool Has2d => Center.HasValue || Zoom.HasValue;
        }

        private static CameraApply ParseCameraApply(JObject source)
        {
            var apply = new CameraApply
            {
                Position = source.OptionalVector("position"),
                LookAt = source.OptionalVector("lookAt"),
                Direction = source.OptionalVector("direction"),
                AnglesDegrees = source.OptionalVector("anglesDegrees"),
                Fov = source.Optional<float?>("fov", null),
                Center = source.OptionalVector("center"),
                Zoom = source.Optional<float?>("zoom", null)
            };

            var orientationCount = (apply.LookAt.HasValue ? 1 : 0) + (apply.Direction.HasValue ? 1 : 0) + (apply.AnglesDegrees.HasValue ? 1 : 0);
            if (orientationCount > 1)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Specify at most one of lookAt, direction, or anglesDegrees.");
            }

            if (apply.Fov.HasValue) apply.Fov = Math.Max(10f, Math.Min(170f, apply.Fov.Value));
            return apply;
        }

        // Apply parsed camera values to one viewport control (must run on the UI thread).
        private static void ApplyCameraToControl(ViewportMapDocumentControl control, CameraApply apply)
        {
            if (control.Camera is PerspectiveCamera cam)
            {
                if (apply.Position.HasValue) cam.Position = apply.Position.Value;
                if (apply.LookAt.HasValue) cam.Direction = apply.LookAt.Value - cam.Position;
                else if (apply.Direction.HasValue) cam.Direction = apply.Direction.Value;
                else if (apply.AnglesDegrees.HasValue) cam.Angles = DegreesToRadians(apply.AnglesDegrees.Value);
                if (apply.Fov.HasValue) cam.FOV = apply.Fov.Value;
            }
            else if (control.Camera is OrthographicCamera ocam)
            {
                if (apply.Center.HasValue) ocam.Position = ocam.Flatten(apply.Center.Value);
                if (apply.Zoom.HasValue) ocam.Zoom = apply.Zoom.Value;
            }
        }

        private object ViewportCameraSet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);

            var apply = ParseCameraApply(parameters);

            var viewsParam = parameters.Optional<string>("views", null);
            string views;
            if (!string.IsNullOrWhiteSpace(viewsParam)) views = viewsParam.ToLowerInvariant();
            else if (apply.Has3d) views = "3d";
            else if (apply.Has2d) views = "2d";
            else throw new BridgeCommandException(ErrorCodes.InvalidRequest, "viewport.camera_set requires 3D (position/lookAt/direction/anglesDegrees/fov) or 2D (center/zoom) parameters, or an explicit views filter.");

            var host = MapDocumentControlHost.Instance;
            if (host == null)
            {
                throw new BridgeCommandException(ErrorCodes.EditorUnavailable, "Map document viewport host is not available.");
            }

            var updated = new List<object>();
            host.InvokeSync(() =>
            {
                foreach (var control in host.GetControls().OfType<ViewportMapDocumentControl>())
                {
                    var view = ViewName(control.Camera);
                    var is2d = control.Camera is OrthographicCamera;
                    var is3d = control.Camera is PerspectiveCamera;
                    if (!ViewFilterMatches(views, view, is2d, is3d, control.IsFocused)) continue;

                    ApplyCameraToControl(control, apply);

                    updated.Add(new { view, focused = control.IsFocused, camera = CameraInfo(control.Camera) });
                }
            });

            return new { updated = updated.Count, document = DocumentInfo(doc), viewFilter = views, viewports = updated };
        }

        private async Task<JToken> ViewportClearMarks(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var clearSelection = parameters.Optional("clearSelection", true);
            var clearOverlay = parameters.Optional("clearOverlay", true);
            var selectedCount = doc.Selection.Count;

            if (clearOverlay) _overlay.Value.Clear(doc);
            if (clearSelection && !doc.Selection.IsEmpty)
            {
                await Perform(doc, new IOperation[] { new Deselect(doc.Selection.ToList()) }, "viewport.clear_marks").ConfigureAwait(true);
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
            await RejectBrushEntityClass(doc, classname).ConfigureAwait(true);
            var origin = parameters.OptionalVector("origin") ?? Vector3.Zero;
            var properties = parameters.StringDictionary("properties");

            var entityData = new EntityData { Name = classname };
            foreach (var kv in properties)
            {
                if (!IsReservedEntityProperty(kv.Key) && kv.Value != null) entityData.Properties[kv.Key] = kv.Value;
            }
            if (parameters["spawnflags"] != null) entityData.Flags = parameters.Optional("spawnflags", 0);

            var entity = new Entity(doc.Map.NumberGenerator.Next("MapObject"))
            {
                Data =
                {
                    entityData,
                    new ObjectColor(Colour.GetDefaultEntityColour()),
                    new Origin(origin)
                }
            };

            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, entity) };
            if (parameters.Optional("select", false)) ops.AddRange(ReplaceSelectionOps(doc, new[] { entity }));

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
            foreach (var reserved in properties.Keys.Where(IsReservedEntityProperty).ToList()) properties.Remove(reserved);
            if (properties.Count > 0) ops.Add(new EditEntityDataProperties(id, properties));

            var origin = parameters.OptionalVector("origin");
            if (origin.HasValue)
            {
                // A brush entity has no Origin data: its position is the centre of its brushes
                var current = obj.Data.GetOne<Origin>()?.Location ?? obj.BoundingBox?.Center ?? Vector3.Zero;
                ops.AddRange(TransformOps(doc, Matrix4x4.CreateTranslation(origin.Value - current), new[] { obj }));
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

            if (parameters.Optional("select", true)) ops.AddRange(ReplaceSelectionOps(doc, new[] { entity }));

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
                // Every solid under the entity, including those inside nested groups
                var children = entity.FindAll().Where(x => x.ID != entity.ID).OfType<Solid>().ToList();
                if (!children.Any()) continue;
                moved.AddRange(children);
                foreach (var byParent in children.GroupBy(x => x.Hierarchy.Parent.ID))
                {
                    ops.Add(new Detatch(byParent.Key, byParent.ToList()));
                }
                ops.Add(new Attach(doc.Map.Root.ID, children));

                // Groups that held nothing but solids (and such groups) are empty now; a group that also holds
                // something else (a point entity, say) keeps it and stays.
                var emptied = TopLevel(entity.FindAll()
                        .OfType<MapGroup>()
                        .Where(g => g.FindAll().All(x => x.ID == g.ID || x is Solid || x is MapGroup)))
                    .ToList();
                foreach (var byParent in emptied.GroupBy(x => x.Hierarchy.Parent.ID))
                {
                    ops.Add(new Detatch(byParent.Key, byParent.ToList()));
                }
                var emptiedIds = new HashSet<long>(emptied.Select(x => x.ID));
                var keepsOtherChildren = entity.Hierarchy.Any(x => !(x is Solid) && !emptiedIds.Contains(x.ID));
                if (deleteEmptyEntity && !keepsOtherChildren && entity.Hierarchy.Parent != null)
                {
                    ops.Add(new Detatch(entity.Hierarchy.Parent.ID, entity));
                    deletedEntities.Add(entity.ID);
                }
            }
            if (!moved.Any()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, "The targeted brush entities contain no solids to untie.");

            if (parameters.Optional("select", true)) ops.AddRange(ReplaceSelectionOps(doc, moved));

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
                CopyDocumentSelector(parameters, createParams);
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
            CopyDocumentSelector(parameters, updateParams);
            return await EntityUpdate(updateParams).ConfigureAwait(true);
        }

        // Forwarded calls must act on the document the caller named, not fall back to the active one.
        private static void CopyDocumentSelector(JObject from, JObject to)
        {
            foreach (var key in new[] { "documentId", "path", "documentIndex" })
            {
                if (from[key] != null) to[key] = from[key].DeepClone();
            }
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

            var bounds = new Box(min, max);
            if (bounds.IsEmpty() || bounds.SmallestDimension <= 0) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Brush bounds must have a positive size on every axis (min and max may not share a coordinate).");

            var round = parameters.Optional("round", true);
            var rounding = brush.CanRound && round ? 0 : 2;
            if (bounds.SmallestDimension < 10) rounding = 2;

            // The brush parameter controls are the editor's own (the Brush tool sidebar shows them): apply the
            // call's overrides only for the duration of the call.
            List<IMapObject> created;
            var bindings = ReadBrushControls(brush);
            var saved = bindings.Select(x => (Binding: x, Value: x.CurrentValue())).ToList();
            try
            {
                ApplyBrushParameters(bindings, parameters["parameters"] as JObject ?? parameters);
                created = brush.Create(doc.Map.NumberGenerator, bounds, texture, rounding).ToList();
            }
            finally
            {
                foreach (var entry in saved) entry.Binding.RestoreValue(entry.Value);
            }
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
            if (select) ops.AddRange(ReplaceSelectionOps(doc, createdObject.FindAll()));

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
            var solid = MapText.SolidFromPlanes(definitions, doc.Map.NumberGenerator, "Plane set did not produce a valid convex brush.");

            var select = parameters.Optional("select", true);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, solid) };
            if (select) ops.AddRange(ReplaceSelectionOps(doc, new[] { solid }));

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
            // A child of a deleted parent goes with it: count and detach only the top-most objects
            var objects = TopLevel(ResolveObjects(doc, parameters.Ids())).Where(x => x.Hierarchy.Parent != null).ToList();
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
            // A transform recurses into children, so an object and its descendants must be applied once
            var objects = TopLevel(ResolveObjects(doc, parameters.Ids())).ToList();
            if (!objects.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "objects.transform requires ids.");

            var translation = parameters.OptionalVector("translation") ?? Vector3.Zero;
            var scale = parameters.OptionalVector("scale") ?? Vector3.One;
            var rotation = parameters.OptionalVector("rotationDegrees") ?? Vector3.Zero;
            if (Math.Abs(scale.X) < 1e-4f || Math.Abs(scale.Y) < 1e-4f || Math.Abs(scale.Z) < 1e-4f)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "scale components must be non-zero (a zero scale flattens the objects and cannot be undone).");
            }
            // Like the editor, scale and rotate about the objects' own centre unless a pivot is given
            var boxes = objects.Select(x => x.BoundingBox).Where(x => x != null && !x.IsEmpty()).ToList();
            var pivot = parameters.OptionalVector("pivot") ?? (boxes.Any() ? new Box(boxes).Center : Vector3.Zero);

            // Scale first, then rotate and move, as two transforms: the editor's texture lock treats a scale
            // (texture scale lock) and a move/rotation (texture lock) differently.
            var scaling = Vector3.Distance(scale, Vector3.One) > 1e-6f;
            var moving = rotation != Vector3.Zero || translation != Vector3.Zero;
            var ops = new List<IOperation>();
            if (scaling)
            {
                var scaleMatrix =
                    Matrix4x4.CreateTranslation(-pivot) *
                    Matrix4x4.CreateScale(scale) *
                    Matrix4x4.CreateTranslation(pivot);
                ops.AddRange(TransformOps(doc, scaleMatrix, objects, isScale: true));
            }
            if (moving || !scaling)
            {
                var moveMatrix =
                    Matrix4x4.CreateTranslation(-pivot) *
                    Matrix4x4.CreateRotationX(Degrees(rotation.X)) *
                    Matrix4x4.CreateRotationY(Degrees(rotation.Y)) *
                    Matrix4x4.CreateRotationZ(Degrees(rotation.Z)) *
                    Matrix4x4.CreateTranslation(pivot + translation);
                ops.AddRange(TransformOps(doc, moveMatrix, objects));
            }

            await Perform(doc, ops, "objects.transform").ConfigureAwait(true);
            return ToToken(new { pivot = pivot.ToDto(), transformed = objects.Select(x => ObjectInfo(ResolveObject(doc, x.ID))).ToList() });
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
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var selectedIds = new HashSet<long>(doc.Selection.SelectMany(x => x.FindAll()).Select(x => x.ID));
            Predicate<IMapObject> filter = selectedOnly ? obj => obj != null && selectedIds.Contains(obj.ID) : _ => true;
            var checker = _problemChecks.Select(x => x.Value).FirstOrDefault(x =>
                string.Equals(x.GetType().FullName, checkerName, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.Name, checkerName, StringComparison.InvariantCultureIgnoreCase) ||
                string.Equals(x.GetType().Name, checkerName, StringComparison.InvariantCultureIgnoreCase));

            if (checker == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Problem checker not found: {checkerName}");
            if (!checker.CanFix) throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Problem checker cannot fix: {checkerName}");

            var problems = await checker.Check(doc, filter).ConfigureAwait(true);
            if (index < 0 || index >= problems.Count) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Problem index {index} is out of range (the checker reports {problems.Count} problem(s) now; re-run problems_check with the same selectedOnly).");
            await checker.Fix(doc, problems[index]).ConfigureAwait(true);
            return ToToken(new { fixedProblem = index, text = problems[index].Text, checker = checker.GetType().FullName });
        }

        private async Task<JToken> LeaksLoadPointfile(JObject parameters)
        {
            // path is the pointfile to read, not a document selector
            var doc = ResolveDocument(parameters, false, pathSelectsDocument: false);
            var path = parameters.Optional<string>("path", null);
            var text = parameters.Optional<string>("text", null);
            if (text == null)
            {
                if (string.IsNullOrWhiteSpace(path)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "leaks.load_pointfile requires path or text.");
                if (!File.Exists(path)) throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Pointfile not found: {path}");
                text = File.ReadAllText(path);
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            Pointfile pointFile;
            try
            {
                pointFile = Pointfile.Parse(lines);
            }
            catch (Exception ex)
            {
                throw new BridgeCommandException(ErrorCodes.ParseError, "Could not parse the pointfile (.lin lines are 'x y z - x y z', .pts lines are 'x y z'): " + ex.Message);
            }
            await MapDocumentOperation.Perform(doc, new TrivialOperation(
                d => d.Map.Data.Replace(pointFile),
                c => c.Update(c.Document.Map.Root))).ConfigureAwait(true);

            var points = LinesToPoints(pointFile.Lines).Select(x => x.ToDto()).ToList();
            _overlay.Value.SetLeakPath(doc, points, path == null ? "MCP pointfile" : System.IO.Path.GetFileName(path));

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
            var doc = ResolveDocument(parameters, false);
            var ids = parameters.Ids();
            var label = parameters.Optional<string>("label", "MCP overlay");
            _overlay.Value.SetHighlights(doc, ids, label);
            return new { overlay = true, highlightedIds = ids, document = DocumentInfo(doc) };
        }

        private object OverlayClear(JObject parameters)
        {
            // An explicit path/documentIndex clears that document only; otherwise every document is cleared.
            if (parameters["path"] == null && parameters["documentIndex"] == null)
            {
                _overlay.Value.Clear(null);
                return new { overlay = false };
            }

            var doc = ResolveDocument(parameters, true);
            _overlay.Value.Clear(doc);
            return new { overlay = false, document = DocumentInfo(doc) };
        }

        private async Task<JToken> TexturesList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("max", 500);
            var detailed = parameters.Optional("detailed", false);
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var allNames = collection.GetAllTextures().OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase).ToList();
            var names = allNames.Take(max).ToList();
            var packages = collection.Packages.Select(x => new { x.Location, count = x.Textures.Count() }).ToList();

            if (!detailed)
            {
                return ToToken(new
                {
                    count = allNames.Count,
                    returned = names.Count,
                    textures = names,
                    packages
                });
            }

            var wadMap = BuildWadMap(collection);
            await collection.Precache(names).ConfigureAwait(true);
            var items = await collection.GetTextureItems(names).ConfigureAwait(true);
            var detailedList = names.Select(name => BuildTextureMeta(name, FindItem(items, name), wadMap)).ToList();
            return ToToken(new
            {
                count = allNames.Count,
                returned = names.Count,
                textures = detailedList,
                packages
            });
        }

        private async Task<JToken> TextureSearch(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var query = parameters.Optional<string>("query", parameters.Optional<string>("text", ""));
            var max = parameters.Optional("max", 100);
            var groupFrames = parameters.Optional("groupFrames", true);
            var includeSpecial = parameters.Optional("includeSpecial", true);
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var wadMap = BuildWadMap(collection);

            var matched = collection.GetAllTextures()
                .Where(x => string.IsNullOrWhiteSpace(query) || x.IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0)
                .Select(name => new { name, info = TextureSemantics.Parse(name) })
                .Where(x => includeSpecial || !x.info.Special)
                .OrderBy(x => x.name, StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            if (groupFrames)
            {
                var groups = matched
                    .GroupBy(x => x.info.Basename, StringComparer.InvariantCultureIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.InvariantCultureIgnoreCase)
                    .Take(max)
                    .Select(g => g.OrderBy(x => x.info.Frame ?? 0).ToList())
                    .ToList();
                var reps = groups.Select(g => g[0].name).ToList();
                await collection.Precache(reps).ConfigureAwait(true);
                var items = await collection.GetTextureItems(reps).ConfigureAwait(true);
                var entries = groups.Select(g =>
                {
                    var rep = g[0];
                    var meta = BuildTextureMeta(rep.name, FindItem(items, rep.name), wadMap, rep.info);
                    meta["frameCount"] = g.Count;
                    meta["frames"] = new JArray(g.Select(x => new JObject
                    {
                        ["name"] = x.name,
                        ["frame"] = x.info.Frame,
                        ["toggle"] = x.info.ToggleFrame
                    }));
                    return meta;
                }).ToList();
                return ToToken(new { query, grouped = true, returned = entries.Count, textures = entries });
            }

            var flat = matched.Take(max).ToList();
            var flatNames = flat.Select(x => x.name).ToList();
            await collection.Precache(flatNames).ConfigureAwait(true);
            var flatItems = await collection.GetTextureItems(flatNames).ConfigureAwait(true);
            var flatEntries = flat.Select(x => BuildTextureMeta(x.name, FindItem(flatItems, x.name), wadMap, x.info)).ToList();
            return ToToken(new { query, grouped = false, returned = flatEntries.Count, textures = flatEntries });
        }

        private static Dictionary<string, string> BuildWadMap(TextureCollection collection)
        {
            var map = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var pkg in collection.Packages)
            {
                var wad = string.IsNullOrWhiteSpace(pkg.Location) ? null : IOPath.GetFileName(pkg.Location);
                foreach (var t in pkg.Textures)
                {
                    if (!map.ContainsKey(t)) map[t] = wad;
                }
            }
            return map;
        }

        private static TextureItem FindItem(IEnumerable<TextureItem> items, string name)
        {
            return items?.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.InvariantCultureIgnoreCase));
        }

        private static JObject BuildTextureMeta(string name, TextureItem item, Dictionary<string, string> wadMap, TextureSemanticInfo info = null)
        {
            info = info ?? TextureSemantics.Parse(name);
            int? w = item?.Width;
            int? h = item?.Height;
            double? aspect = (w.HasValue && h.HasValue && h.Value != 0) ? (double?)Math.Round(w.Value / (double)h.Value, 2) : null;
            var wad = wadMap != null && wadMap.TryGetValue(name, out var wv) ? wv : item?.WadName;
            return new JObject
            {
                ["name"] = name,
                ["width"] = w,
                ["height"] = h,
                ["aspect"] = aspect,
                ["wad"] = wad,
                ["flags"] = new JArray(info.Flags),
                ["frame"] = info.Frame,
                ["basename"] = info.Basename,
                ["family"] = info.Family,
                ["special"] = info.Special
            };
        }

        private const int MaxSheetSide = 4096;

        private async Task<JToken> TexturePreviewSheet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var query = parameters.Optional<string>("query", parameters.Optional<string>("text", ""));
            var max = Math.Max(1, Math.Min(parameters.Optional("max", 32), 128));
            var tileSize = Math.Max(32, Math.Min(parameters.Optional("tileSize", 128), 512));
            var columns = Math.Max(1, Math.Min(parameters.Optional("columns", 4), 12));
            var showDimensions = parameters.Optional("showDimensions", true);
            var labelHeight = 34;
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var allNames = ResolveTexturePreviewNames(parameters, collection, query);
            var total = allNames.Count;

            // Bound the composed image (max*tileSize*columns could otherwise reach 6000+ px a side); the
            // per-page count shrinks to what fits, so page-based offsets use that count and never skip textures
            if (columns * tileSize > MaxSheetSide) columns = Math.Max(1, MaxSheetSide / tileSize);
            var rowsThatFit = Math.Max(1, MaxSheetSide / (tileSize + labelHeight));
            var pageSize = Math.Min(max, rowsThatFit * columns);

            // Pagination: explicit offset wins; otherwise page*pageSize (page default 0).
            var offset = parameters.Optional<int?>("offset", null) ?? parameters.Optional("page", 0) * pageSize;
            if (offset < 0) offset = 0;
            var names = allNames.Skip(offset).Take(pageSize).ToList();

            var rows = Math.Max(1, (int)Math.Ceiling(names.Count / (double)columns));
            var sheetWidth = columns * tileSize;
            var sheetHeight = rows * (tileSize + labelHeight);
            var tiles = new List<object>();

            using (var sheet = new Bitmap(sheetWidth, sheetHeight, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(sheet))
            using (var streamSource = collection.GetStreamSource())
            using (var labelBrush = new SolidBrush(Color.White))
            using (var dimBrush = new SolidBrush(Color.FromArgb(180, 180, 180)))
            using (var missingBrush = new SolidBrush(Color.FromArgb(32, 32, 32)))
            using (var borderPen = new Pen(Color.FromArgb(96, 96, 96)))
            using (var nameFont = new Font(FontFamily.GenericSansSerif, 7f))
            using (var dimFont = new Font(FontFamily.GenericSansSerif, 7f))
            {
                g.Clear(Color.Black);
                for (var i = 0; i < names.Count; i++)
                {
                    var name = names[i];
                    var info = TextureSemantics.Parse(name);
                    var col = i % columns;
                    var row = i / columns;
                    var x = col * tileSize;
                    var y = row * (tileSize + labelHeight);
                    var imageRect = new Rectangle(x, y, tileSize, tileSize);
                    var labelWidth = tileSize - 4;
                    var status = "ok";
                    TextureItem textureItem = null;

                    try
                    {
                        textureItem = await collection.GetTextureItem(name).ConfigureAwait(true);
                        var task = streamSource.GetImage(name, tileSize, tileSize);
                        ICollection<Bitmap> bitmaps = task == null ? null : await task.ConfigureAwait(true);
                        if (bitmaps == null || !bitmaps.Any())
                        {
                            status = "missing";
                            DrawMissingTexture(g, missingBrush, borderPen, imageRect);
                        }
                        else
                        {
                            try
                            {
                                var bitmap = bitmaps.First();
                                if (bitmap == null)
                                {
                                    status = "missing";
                                    DrawMissingTexture(g, missingBrush, borderPen, imageRect);
                                }
                                else
                                {
                                    DrawImageFitted(g, bitmap, imageRect);
                                    g.DrawRectangle(borderPen, imageRect.X, imageRect.Y, imageRect.Width - 1, imageRect.Height - 1);
                                }
                            }
                            finally
                            {
                                foreach (var b in bitmaps) b?.Dispose();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "error: " + ex.Message;
                        DrawMissingTexture(g, missingBrush, borderPen, imageRect);
                    }

                    // Line 1: tile index + name (ellipsised to fit). Line 2: dims + semantic glyphs.
                    var nameText = FitText(g, (offset + i) + " " + name, nameFont, labelWidth);
                    g.DrawString(nameText, nameFont, labelBrush, x + 2, y + tileSize + 1);
                    if (showDimensions)
                    {
                        var dims = textureItem != null ? $"{textureItem.Width}x{textureItem.Height}" : "?x?";
                        var glyphs = SemanticGlyphs(info);
                        var line2 = string.IsNullOrEmpty(glyphs) ? dims : dims + " " + glyphs;
                        g.DrawString(FitText(g, line2, dimFont, labelWidth), dimFont, dimBrush, x + 2, y + tileSize + 16);
                    }

                    tiles.Add(new
                    {
                        index = offset + i,
                        name,
                        x,
                        y,
                        width = tileSize,
                        height = tileSize,
                        textureWidth = textureItem?.Width,
                        textureHeight = textureItem?.Height,
                        wad = textureItem?.WadName,
                        flags = info.Flags,
                        family = info.Family,
                        status
                    });
                }

                var returned = names.Count;
                var hasMore = offset + returned < total;
                return ToToken(new
                {
                    document = DocumentInfo(doc),
                    query,
                    total,
                    offset,
                    returned,
                    hasMore,
                    nextOffset = hasMore ? (int?)(offset + returned) : null,
                    pageSize,
                    tileSize,
                    columns,
                    textures = tiles,
                    images = new[]
                    {
                        new
                        {
                            name = "texture-preview-sheet",
                            view = "texture-browser",
                            mimeType = "image/png",
                            data = BitmapToPngBase64(sheet),
                            width = sheet.Width,
                            height = sheet.Height
                        }
                    }
                });
            }
        }

        private static string SemanticGlyphs(TextureSemanticInfo info)
        {
            var sb = new StringBuilder();
            if (info.Transparent) sb.Append('{');
            if (info.Liquid) sb.Append('~');
            if (info.LightEmitting) sb.Append('*');
            if (info.Animated) sb.Append('+');
            if (info.RandomTiling) sb.Append('-');
            if (info.Scrolling) sb.Append('>');
            if (info.Sky) sb.Append('^');
            if (info.Tool != null) sb.Append('#');
            return sb.ToString();
        }

        private static string FitText(Graphics graphics, string text, Font font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (graphics.MeasureString(text, font).Width <= maxWidth) return text;
            const string ellipsis = "...";
            var trimmed = text;
            while (trimmed.Length > 1 && graphics.MeasureString(trimmed + ellipsis, font).Width > maxWidth)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }
            return trimmed + ellipsis;
        }

        private async Task<JToken> TextureApply(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var texture = parameters.Required<string>("texture");
            var faces = RequireFaces(doc, parameters, "texture.apply", out var scope);

            var align = parameters.Optional("align", true);
            var scale = parameters.Optional<decimal?>("textureScale", null);
            var warnings = new List<string>();
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
                TextureAlignment.Sanitize(clone.Texture, clone.Plane.Normal, entry.Face.ID, warnings);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }

            var changedFaceRefs = FaceTargetRefs(faces);
            await Perform(doc, ops, "texture.apply").ConfigureAwait(true);
            EnsureTextureNames(doc, changedFaceRefs, texture);
            var changedFaces = FaceInfos(doc, changedFaceRefs);
            return ToToken(new { texture, scope = scope.ToString().ToLowerInvariant(), changedFaces = changedFaces.Count, faces = changedFaces, warnings });
        }

        private async Task<JToken> TextureReplace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            // B3: accept find/from and replace/to aliases.
            var find = parameters.Optional<string>("find", null) ?? parameters.Optional<string>("from", null);
            var replace = parameters.Optional<string>("replace", null) ?? parameters.Optional<string>("to", null);
            if (string.IsNullOrWhiteSpace(find)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture.replace requires find (or from).");
            if (string.IsNullOrWhiteSpace(replace)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture.replace requires replace (or to).");
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var solids = selectedOnly
                ? SolidsOf(doc.Selection).ToList() // an empty selection replaces nothing, it never widens to the map
                : SolidsOf(CandidateObjects(doc, parameters.Ids())).ToList();
            var matches = solids.SelectMany(s => s.Faces.Where(f => string.Equals(f.Texture.Name, find, StringComparison.InvariantCultureIgnoreCase)).Select(f => new FaceRef(s, f))).ToList();
            if (matches.Count == 0)
            {
                // Nothing to forward: texture.apply must never see an empty target list (it would reject it or widen it)
                return ToToken(new { find, replace, changedFaces = 0, searchedSolids = solids.Count, alignPreserved = true, warnings = new[] { $"no face in the searched solids uses '{find}'" } });
            }
            var forwarded = new JObject(parameters)
            {
                ["texture"] = replace,
                ["faceRefs"] = new JArray(matches.Select(x => new JObject { ["objectId"] = x.Object.ID, ["faceId"] = x.Face.ID }))
            };
            // B3: replace preserves existing alignment by default (align=false), unlike texture.apply.
            if (forwarded["align"] == null) forwarded["align"] = false;
            var alignApplied = forwarded.Optional("align", false);
            var result = await TextureApply(forwarded).ConfigureAwait(true);
            var innerWarnings = (result as JObject)?["warnings"] as JArray ?? new JArray();
            return ToToken(new { find, replace, changedFaces = matches.Count, alignPreserved = !alignApplied, warnings = innerWarnings, result });
        }

        private async Task<JToken> TextureAlignFace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faces = RequireFaces(doc, parameters, "texture.align_face", out var scope);
            var mode = parameters.Optional("mode", "normal").ToLowerInvariant();
            if (mode != "world" && mode != "face" && mode != "normal" && mode != "reset") throw new BridgeCommandException(ErrorCodes.InvalidRequest, "mode must be world, face (alias normal), or reset.");
            var rotation = parameters.Optional<float?>("rotation", null);
            var justify = parameters.Optional("justify", "none").ToLowerInvariant();
            var warnings = new List<string>();
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);

            var ops = new List<IOperation>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                var normal = clone.Plane.Normal;
                switch (mode)
                {
                    case "world":
                        TextureAlignment.AlignWorld(clone.Texture, normal);
                        break;
                    case "reset":
                        TextureAlignment.AlignFace(clone.Texture, normal);
                        clone.Texture.XShift = 0;
                        clone.Texture.YShift = 0;
                        clone.Texture.Rotation = 0;
                        clone.Texture.XScale = 1;
                        clone.Texture.YScale = 1;
                        break;
                    case "face":
                    case "normal":
                    default:
                        TextureAlignment.AlignFace(clone.Texture, normal);
                        break;
                }
                if (rotation.HasValue) TextureAlignment.SetRotationSafe(clone.Texture, rotation.Value);
                if (justify != "none")
                {
                    var dims = await ResolveTextureDims(collection, clone.Texture.Name, warnings).ConfigureAwait(true);
                    TextureAlignment.Justify(clone, clone.Texture, justify, dims.w, dims.h, warnings);
                }
                TextureAlignment.Sanitize(clone.Texture, normal, entry.Face.ID, warnings);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }
            var changedFaceRefs = FaceTargetRefs(faces);
            await Perform(doc, ops, "texture.align_face").ConfigureAwait(true);
            var changedFaces = FaceInfos(doc, changedFaceRefs);
            return ToToken(new { alignedFaces = changedFaces.Count, mode, scope = scope.ToString().ToLowerInvariant(), faces = changedFaces, warnings });
        }

        private object FaceList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional("max", 500);

            if (HasExplicitFaceReferenceParameters(parameters))
            {
                var refs = ResolveFaceRefs(doc, ParseFaceTargetRequest(parameters).FaceRefs)
                    .Select(x => FaceInfo(x.Object, x.Face))
                    .Take(max)
                    .ToList();
                return new { returned = refs.Count, faces = refs };
            }

            var objectIds = parameters.Ids("objectIds")
                .Concat(parameters.Ids())
                .Concat(parameters["objectId"] != null ? new[] { parameters.Required<long>("objectId") } : Array.Empty<long>())
                .Where(id => id != 0)
                .Distinct()
                .ToArray();
            var faces = SolidsOf(CandidateObjects(doc, objectIds.Any() ? objectIds : null))
                .SelectMany(x => x.Faces.Select(f => FaceInfo(x, f)))
                .Take(max)
                .ToList();
            return new { returned = faces.Count, faces };
        }

        private async Task<JToken> FaceSelect(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var mode = parameters.Optional("mode", "replace").ToLowerInvariant();
            if (mode != "replace" && mode != "add" && mode != "remove")
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "face_select mode must be replace, add, or remove.");
            }

            var refs = RequireFaces(doc, parameters, "face_select", out _);
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
            var selectedFaces = CurrentSelectedFaces(doc).ToList();
            return ToToken(new { selectedFaces = selectedFaces.Count, mode, faces = selectedFaces.Select(x => FaceInfo(x.Object, x.Face)).ToList() });
        }

        private async Task<JToken> FaceTextureSet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faces = RequireFaces(doc, parameters, "face_texture_set", out var scope);

            var warnings = new List<string>();
            var ops = new List<IOperation>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                ApplyTextureFields(clone.Texture, parameters, clone.Plane.Normal, warnings, entry.Face.ID);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }

            var changedFaceRefs = FaceTargetRefs(faces);
            var requestedTexture = parameters.Optional<string>("texture", null) ?? parameters.Optional<string>("name", null);
            await Perform(doc, ops, "face.texture_set").ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(requestedTexture)) EnsureTextureNames(doc, changedFaceRefs, requestedTexture);
            var changedFaces = FaceInfos(doc, changedFaceRefs);
            return ToToken(new { changedFaces = changedFaces.Count, scope = scope.ToString().ToLowerInvariant(), faces = changedFaces, warnings });
        }

        private async Task<JToken> TextureCopyFromFace(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var sourceToken = parameters["sourceFace"] as JObject;
            if (sourceToken == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture_copy_from_face requires sourceFace { objectId, faceId }.");
            var source = ResolveFaceRefs(doc, new JArray(sourceToken)).First();
            var targets = RequireFaces(doc, parameters, "texture_copy_from_face", out var scope).Where(x => x.Face.ID != source.Face.ID || x.Object.ID != source.Object.ID).ToList();
            if (!targets.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "texture_copy_from_face: the only targeted face is the source face.");

            var projected = parameters.Optional("projected", true);
            var warnings = new List<string>();
            var ops = new List<IOperation>();
            var coplanarList = new List<bool>();
            foreach (var target in targets)
            {
                var clone = (Face)target.Face.Clone();
                var coplanar = PlanesParallel(clone.Plane.Normal, source.Face.Plane.Normal, 1e-3f);
                coplanarList.Add(coplanar);
                if (projected)
                {
                    clone.Texture.Name = source.Face.Texture.Name;
                    clone.Texture.AlignWithTexture(clone.Plane, source.Face.Plane, source.Face.Texture);
                }
                else
                {
                    clone.Texture.Unclone(source.Face.Texture);
                }
                TextureAlignment.Sanitize(clone.Texture, clone.Plane.Normal, target.Face.ID, warnings);
                ops.Add(new RemoveMapObjectData(target.Object.ID, target.Face));
                ops.Add(new AddMapObjectData(target.Object.ID, clone));
            }

            var changedFaceRefs = FaceTargetRefs(targets);
            await Perform(doc, ops, "texture.copy_from_face").ConfigureAwait(true);
            var changedFaces = FaceInfos(doc, changedFaceRefs);
            var faceTokens = new List<JObject>();
            for (var i = 0; i < changedFaces.Count; i++)
            {
                var jt = (JObject)JToken.FromObject(changedFaces[i]);
                jt["projected"] = projected;
                if (i < coplanarList.Count) jt["coplanar"] = coplanarList[i];
                faceTokens.Add(jt);
            }
            return ToToken(new { source = FaceInfo(source.Object, source.Face), scope = scope.ToString().ToLowerInvariant(), changedFaces = faceTokens.Count, faces = faceTokens, warnings });
        }

        private async Task<JToken> TextureProject(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var faces = RequireFaces(doc, parameters, "texture.project", out var scope);

            var mode = parameters.Optional("mode", "planar").ToLowerInvariant();
            var texture = parameters.Optional<string>("texture", null);
            var scale = parameters.Optional<decimal?>("scale", null);
            var cylindricalAxis = mode == "cylindrical"
                ? UnitOrDefault(parameters.OptionalVector("axis") ?? Vector3.UnitZ, Vector3.UnitZ)
                : Vector3.UnitZ;
            var explicitCylindricalSides = mode == "cylindrical" ? OptionalCylindricalSideCount(parameters) : null;
            var inferredCylindricalSides = mode == "cylindrical" && !explicitCylindricalSides.HasValue
                ? faces.GroupBy(x => x.Object.ID).ToDictionary(x => x.Key, x => InferCylindricalSideCount(x, cylindricalAxis))
                : new Dictionary<long, int>();

            var warnings = new List<string>();
            var textureCollection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            // Dimensions of the texture each face ends up with: the requested one, else the face's own
            var dimsByTexture = new Dictionary<string, (int w, int h)>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var name in faces.Select(f => texture ?? f.Face.Texture.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.InvariantCultureIgnoreCase))
            {
                dimsByTexture[name] = await ResolveTextureDims(textureCollection, name, warnings).ConfigureAwait(true);
            }

            Vector3? originUsed = null;
            var ops = new List<IOperation>();
            var appliedFaces = new List<FaceRef>();
            foreach (var entry in faces)
            {
                var clone = (Face)entry.Face.Clone();
                if (!string.IsNullOrWhiteSpace(texture)) clone.Texture.Name = texture;
                if (scale.HasValue)
                {
                    clone.Texture.XScale = (float)scale.Value;
                    clone.Texture.YScale = (float)scale.Value;
                }
                var (texWidth, texHeight) = dimsByTexture.TryGetValue(clone.Texture.Name ?? "", out var td) ? td : (64, 64);

                var cloud = new Cloud(entry.Face.Vertices); // the cylindrical wrap still uses the cloud extents

                switch (mode)
                {
                    case "fit":
                        clone.Texture.AlignToNormal(clone.Plane.Normal);
                        TextureAlignment.Justify(clone, clone.Texture, "fit", texWidth, texHeight, warnings);
                        break;
                    case "center":
                        clone.Texture.AlignToNormal(clone.Plane.Normal);
                        TextureAlignment.Justify(clone, clone.Texture, "center", texWidth, texHeight, warnings);
                        break;
                    case "planar":
                        ApplyPlanarProjection(clone, parameters, texWidth, texHeight, warnings);
                        break;
                    case "cylindrical":
                        var sideCount = explicitCylindricalSides;
                        if (!sideCount.HasValue
                            && inferredCylindricalSides.TryGetValue(entry.Object.ID, out var inferredSides)
                            && inferredSides >= 3)
                        {
                            sideCount = inferredSides;
                        }
                        var cyl = ApplyCylindricalProjection(clone, parameters, cloud, texWidth, texHeight, sideCount, !scale.HasValue, entry.Object, warnings);
                        if (!originUsed.HasValue) originUsed = cyl.origin;
                        if (!cyl.applied) continue; // B2: skip face, warning already recorded.
                        break;
                    default:
                        throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Unknown projection mode: {mode}");
                }

                TextureAlignment.Sanitize(clone.Texture, clone.Plane.Normal, entry.Face.ID, warnings);
                appliedFaces.Add(entry);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
            }

            var changedFaceRefs = FaceTargetRefs(appliedFaces);
            await Perform(doc, ops, "texture.project").ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(texture)) EnsureTextureNames(doc, changedFaceRefs, texture);
            var changedFaces = FaceInfos(doc, changedFaceRefs);
            return ToToken(new
            {
                projectedFaces = changedFaces.Count,
                mode,
                scope = scope.ToString().ToLowerInvariant(),
                faces = changedFaces,
                originUsed = originUsed?.ToDto(),
                warnings
            });
        }

        private void ApplyPlanarProjection(Face face, JObject parameters, int texWidth, int texHeight, List<string> warnings)
        {
            var direction = parameters.OptionalVector("direction") ?? Vector3.UnitZ;
            if (direction.LengthSquared() < 1e-6f) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "direction must be a non-zero vector.");
            direction = Vector3.Normalize(direction);
            var align = parameters.Optional("align", "natural").ToLowerInvariant();
            if (!new[] { "natural", "center", "fit", "left", "right", "top", "bottom" }.Contains(align)) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "align must be natural, center, fit, left, right, top or bottom.");

            var axis = direction.ClosestAxis();
            var tempV = axis == Vector3.UnitZ ? -Vector3.UnitY : -Vector3.UnitZ;
            face.Texture.UAxis = direction.Cross(tempV).Normalise();
            face.Texture.VAxis = face.Texture.UAxis.Cross(direction).Normalise();
            face.Texture.Rotation = 0;

            if (align == "natural")
            {
                // texture origin at the face's minimum corner along the projection axes (all vertices considered)
                TextureAlignment.UvBounds(face, face.Texture, out var minU, out _, out var minV, out _, warnings);
                face.Texture.XShift = -minU;
                face.Texture.YShift = -minV;
            }
            else
            {
                TextureAlignment.Justify(face, face.Texture, align, texWidth, texHeight, warnings);
            }
        }

        // B2: origin fallback chain (params origin -> owning solid bbox center -> face origin),
        // no silent return on tiny radius, and Sanitize guards the rawU/XScale division.
        private (bool applied, Vector3 origin) ApplyCylindricalProjection(Face face, JObject parameters, Cloud cloud, int texWidth, int texHeight, int? sideCount, bool autoWrapScale, IMapObject owner, List<string> warnings)
        {
            var axis = UnitOrDefault(parameters.OptionalVector("axis") ?? Vector3.UnitZ, Vector3.UnitZ);
            var explicitOrigin = parameters.OptionalVector("origin");
            Vector3 origin;
            if (explicitOrigin.HasValue) origin = explicitOrigin.Value;
            else if (owner?.BoundingBox != null && !owner.BoundingBox.IsEmpty()) origin = owner.BoundingBox.Center;
            else origin = face.Origin;
            var labels = Math.Max(1, parameters.Optional("labels", 1));
            var centerLabel = parameters.Optional("centerLabel", true);

            face.Texture.AlignToNormal(face.Plane.Normal);

            var faceCenter = face.Origin;
            var rel = faceCenter - origin;
            var perp = rel - rel.Dot(axis) * axis;
            var radius = perp.Length();
            if (radius < 0.01f)
            {
                warnings?.Add($"face {face.ID}: cylindrical projection skipped (radius {radius:0.###} below 0.01 at origin [{origin.X:0.##}, {origin.Y:0.##}, {origin.Z:0.##}]).");
                return (false, origin);
            }

            var perimeter = sideCount.HasValue && sideCount.Value >= 3
                ? (float)TextureProjectionMath.RegularPolygonPerimeter(sideCount.Value, radius)
                : 2 * (float)Math.PI * radius;
            if (autoWrapScale)
            {
                face.Texture.XScale = (float)TextureProjectionMath.WrapScale(perimeter, texWidth, labels);
            }

            var refAxis = Math.Abs(Vector3.Dot(axis, Vector3.UnitZ)) > 0.99f ? Vector3.UnitX : Vector3.UnitZ;
            refAxis = UnitOrDefault(refAxis - Vector3.Dot(refAxis, axis) * axis, Vector3.UnitX);
            var perpAxis = UnitOrDefault(axis.Cross(refAxis), Vector3.UnitY);
            var angle = (float)Math.Atan2(Vector3.Dot(perp, perpAxis), Vector3.Dot(perp, refAxis));
            if (angle < 0) angle += 2 * (float)Math.PI;

            var wrapRadius = perimeter / (2 * (float)Math.PI);
            var expectedU = angle * wrapRadius / face.Texture.XScale;
            // u at the face centre is (centre . U) / XScale + XShift: cancel exactly that, so a cylinder away
            // from the world origin wraps as seamlessly as one at the origin
            var rawU = Vector3.Dot(faceCenter, face.Texture.UAxis) / face.Texture.XScale;

            var offset = centerLabel ? (texWidth / 2f) - (perimeter / labels / 2f / face.Texture.XScale) : 0;
            face.Texture.XShift = expectedU - rawU + offset;

            var yvals = cloud.GetExtents().Select(x => Vector3.Dot(x, face.Texture.VAxis) / face.Texture.YScale).ToList();
            face.Texture.YShift = -yvals.Min();

            TextureAlignment.Sanitize(face.Texture, face.Plane.Normal, face.ID, warnings);
            return (true, origin);
        }

        private static int? OptionalCylindricalSideCount(JObject parameters)
        {
            var sideCount = parameters.Optional<int?>("sides", null) ?? parameters.Optional<int?>("numberOfSides", null);
            if (sideCount.HasValue && sideCount.Value < 3)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Cylindrical side count must be at least 3.");
            }
            return sideCount;
        }

        private static int InferCylindricalSideCount(IEnumerable<FaceRef> faces, Vector3 axis)
        {
            var count = faces.Count(x =>
            {
                var normal = UnitOrDefault(x.Face.Plane.Normal, axis);
                return Math.Abs(Vector3.Dot(normal, axis)) < 0.5f;
            });
            return count >= 3 ? count : 0;
        }

        private static Vector3 UnitOrDefault(Vector3 vector, Vector3 fallback)
        {
            return vector.LengthSquared() < 0.0001f ? fallback : Vector3.Normalize(vector);
        }

        // B6: resolve texture pixel dimensions, warning + assuming 64x64 when unknown.
        private static async Task<(int w, int h)> ResolveTextureDims(TextureCollection collection, string textureName, List<string> warnings)
        {
            if (!string.IsNullOrWhiteSpace(textureName))
            {
                try
                {
                    var item = await collection.GetTextureItem(textureName).ConfigureAwait(true);
                    if (item != null && item.Width > 0 && item.Height > 0) return (item.Width, item.Height);
                }
                catch (Exception ex)
                {
                    Log($"texture dimensions lookup failed for '{textureName}': {ex.Message}");
                }
            }
            warnings?.Add($"texture '{textureName}' dimensions unknown; assumed 64x64");
            return (64, 64);
        }

        private static bool PlanesParallel(Vector3 n1, Vector3 n2, float tolerance)
        {
            return Math.Abs(Math.Abs(Vector3.Dot(n1.Normalise(), n2.Normalise())) - 1f) <= tolerance;
        }

        private async Task<JToken> TextureApplySmart(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            // B4a: require an explicit target instead of silently textureing every object.
            var faces = RequireFaces(doc, parameters, "texture.apply_smart", out var scope);

            var classify = parameters.Optional("classify", "nearest").ToLowerInvariant();

            var frontDir = (parameters.OptionalVector("frontDirection") ?? -Vector3.UnitY).Normalise();

            var frontTex = parameters.Optional<string>("front", null);
            var backTex = parameters.Optional<string>("back", null);
            var leftTex = parameters.Optional<string>("left", null);
            var rightTex = parameters.Optional<string>("right", null);
            var topTex = parameters.Optional<string>("top", null);
            var bottomTex = parameters.Optional<string>("bottom", null);

            var scale = parameters.Optional<decimal?>("scale", null);
            var fit = parameters.Optional("fit", false);
            var center = parameters.Optional("center", false);

            Vector3 rightDir;
            if (Math.Abs(frontDir.Z) > 0.99f)
            {
                rightDir = Vector3.UnitX;
            }
            else
            {
                rightDir = Vector3.Cross(Vector3.UnitZ, frontDir).Normalise();
            }
            var leftDir = -rightDir;
            var backDir = -frontDir;
            var upDir = Vector3.UnitZ;
            var downDir = -Vector3.UnitZ;

            var roleDirs = new (string name, Vector3 dir, string tex)[]
            {
                ("front", frontDir, frontTex),
                ("back", backDir, backTex),
                ("right", rightDir, rightTex),
                ("left", leftDir, leftTex),
                ("top", upDir, topTex),
                ("bottom", downDir, bottomTex)
            };

            var warnings = new List<string>();
            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var textureDimensions = new Dictionary<string, (int w, int h)>(StringComparer.InvariantCultureIgnoreCase);
            var allTextures = new[] { frontTex, backTex, leftTex, rightTex, topTex, bottomTex }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
            foreach (var tex in allTextures)
            {
                textureDimensions[tex] = await ResolveTextureDims(collection, tex, warnings).ConfigureAwait(true);
            }

            var results = new List<object>();
            var skippedFaces = new List<object>();
            var unassignedFaces = 0;
            var ops = new List<IOperation>();

            foreach (var entry in faces)
            {
                var normal = entry.Face.Plane.Normal;

                // B4b: score every role direction and take the best.
                string bestRole = null;
                string bestTex = null;
                var bestDot = float.MinValue;
                foreach (var r in roleDirs)
                {
                    var d = Vector3.Dot(normal, r.dir);
                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestRole = r.name;
                        bestTex = r.tex;
                    }
                }

                if (classify == "strict" && bestDot <= 0.9f)
                {
                    skippedFaces.Add(new { objectId = entry.Object.ID, faceId = entry.Face.ID, bestRole, bestDot });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(bestTex))
                {
                    unassignedFaces++;
                    continue;
                }

                var clone = (Face)entry.Face.Clone();
                clone.Texture.Name = bestTex;
                clone.Texture.AlignToNormal(clone.Plane.Normal);

                if (scale.HasValue)
                {
                    clone.Texture.XScale = (float)scale.Value;
                    clone.Texture.YScale = (float)scale.Value;
                }

                if (fit || center)
                {
                    var dims = textureDimensions.TryGetValue(bestTex, out var d) ? d : (w: 64, h: 64);
                    var cloud = new Cloud(entry.Face.Vertices);
                    if (fit)
                        clone.Texture.FitToPointCloud(dims.w, dims.h, cloud, 1, 1);
                    else if (center)
                        clone.Texture.AlignWithPointCloud(dims.w, dims.h, cloud, BoxAlignMode.Center);
                }

                TextureAlignment.Sanitize(clone.Texture, clone.Plane.Normal, entry.Face.ID, warnings);
                ops.Add(new RemoveMapObjectData(entry.Object.ID, entry.Face));
                ops.Add(new AddMapObjectData(entry.Object.ID, clone));
                results.Add(new { objectId = entry.Object.ID, faceId = entry.Face.ID, role = bestRole, texture = bestTex, dot = bestDot });
            }

            if (ops.Any())
                await Perform(doc, ops, "texture.apply_smart").ConfigureAwait(true);

            return ToToken(new
            {
                changedFaces = results.Count,
                classify,
                scope = scope.ToString().ToLowerInvariant(),
                faces = results,
                skippedFaces = new { count = skippedFaces.Count, faces = skippedFaces },
                unassignedFaces,
                warnings
            });
        }

        private async Task<JToken> FaceDelete(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var refs = RequireFaces(doc, parameters, "face_delete", out var scope);
            var grouped = refs.GroupBy(x => x.Object).ToList();
            var ops = new List<IOperation>();
            var deleted = 0;
            foreach (var group in grouped)
            {
                var solid = group.Key as Solid;
                if (solid == null) continue;
                var removed = new HashSet<long>(group.Select(x => x.Face.ID));
                var remaining = solid.Faces.Where(f => !removed.Contains(f.ID)).ToList();
                // Only a face that contributes no edges (degenerate) can go without opening the solid
                if (remaining.Count < 4 || !IsClosedMesh(remaining))
                {
                    throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Deleting those faces would leave solid {solid.ID} open (every face of a brush is needed unless it is degenerate). Use clip_apply to reshape a brush.");
                }
                foreach (var entry in group) ops.Add(new RemoveMapObjectData(solid.ID, entry.Face));
                deleted += group.Count();
            }
            await Perform(doc, ops, "face.delete").ConfigureAwait(true);
            return ToToken(new { deletedFaces = deleted, scope = scope.ToString().ToLowerInvariant() });
        }

        private object VertexSnapshot(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var solids = SolidsOf(CandidateObjects(doc, parameters.Ids())).ToList();
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

            var keyList = (parameters["vertexKeys"] as JArray)?.Select(x => x.Value<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
            var keys = new HashSet<string>(keyList, StringComparer.Ordinal);
            var refs = ResolveVertexRefs(doc, parameters["vertexRefs"] as JArray).ToList();
            // A vertex reference names a corner of the solid: every face sharing that corner moves with it,
            // otherwise the solid would be torn open along the neighbouring faces
            var refKeys = refs.GroupBy(r => r.Object.ID).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(r => VertexKey(r.Face.Vertices[r.VertexIndex])), StringComparer.Ordinal));
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var ops = new List<IOperation>();

            // Only the solids named (by ids, the selection, or the vertexRefs) are touched, never the whole map
            var named = parameters.Ids().Length > 0 || !doc.Selection.IsEmpty
                ? RequestedOrSelectedObjects(doc, parameters)
                : Enumerable.Empty<IMapObject>();
            var candidates = SolidsOf(named.Concat(refs.Select(r => r.Object))).ToList();
            foreach (var solid in candidates)
            {
                var clones = new List<(Face Original, Face Clone)>();
                var touchedSolid = false;
                foreach (var face in solid.Faces)
                {
                    var clone = (Face)face.Clone();
                    var touched = false;
                    for (var i = 0; i < clone.Vertices.Count; i++)
                    {
                        var key = VertexKey(clone.Vertices[i]);
                        var matched = keys.Contains(key) || (refKeys.TryGetValue(solid.ID, out var solidKeys) && solidKeys.Contains(key));
                        if (!matched) continue;
                        clone.Vertices[i] = position ?? clone.Vertices[i] + delta.Value;
                        touched = true;
                    }
                    clones.Add((face, clone));
                    touchedSolid |= touched;
                    if (touched) changed.Add($"{solid.ID}:{face.ID}");
                }
                if (!touchedSolid) continue;

                // The moved solid must still be a valid brush (planar convex faces, no coplanar sides); a brush
                // that was already invalid may still be edited, typically to repair it
                var preview = new Solid(0);
                foreach (var pair in clones) preview.Data.Add(pair.Clone);
                if (solid.IsValid() && !preview.IsValid())
                {
                    throw new BridgeCommandException(ErrorCodes.InvalidOperation, $"Moving those vertices would make solid {solid.ID} invalid (a face becomes non-planar or the brush non-convex). Move all vertices of the affected faces, split the face first, or use clip_apply.");
                }
                foreach (var pair in clones.Where(x => changed.Contains($"{solid.ID}:{x.Original.ID}")))
                {
                    ops.Add(new RemoveMapObjectData(solid.ID, pair.Original));
                    ops.Add(new AddMapObjectData(solid.ID, pair.Clone));
                }
            }

            if (!ops.Any()) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "No vertices matched vertexKeys or vertexRefs on the targeted solids (pass ids or select the solids when using vertexKeys).");
            await Perform(doc, ops, "vertex.move").ConfigureAwait(true);
            return ToToken(new { changedFaces = changed.Count, vertexKeys = keyList });
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
            var refs = RequireFaces(doc, parameters, "vertex_triangulate", out var scope);
            refs = refs.Where(x => x.Face.Vertices.Count > 3).ToList();
            var ops = new List<IOperation>();
            foreach (var entry in refs) ops.AddRange(TriangulateFaceOperation(doc, entry));
            await Perform(doc, ops, "vertex.triangulate").ConfigureAwait(true);
            return ToToken(new { triangulatedFaces = refs.Count, scope = scope.ToString().ToLowerInvariant(), warnings = new[] { "triangulated sides are coplanar: the brush is invalid until their vertices are moved out of the plane (vertex_move), then validate" } });
        }

        private async Task<JToken> VertexFaceEdit(JObject parameters)
        {
            var action = parameters.Optional("action", "poke").ToLowerInvariant();
            if (action == "triangulate") return await VertexTriangulate(parameters).ConfigureAwait(true);

            if (action != "poke") throw new BridgeCommandException(ErrorCodes.InvalidRequest, "action must be poke or triangulate.");
            var doc = ResolveDocument(parameters, false);
            var distance = parameters.Optional("distance", 8f);
            var refs = RequireFaces(doc, parameters, "vertex_face_edit", out var scope);
            refs = refs.Where(x => x.Face.Vertices.Count >= 3).ToList();
            var ops = new List<IOperation>();
            foreach (var entry in refs) ops.AddRange(PokeFaceOperation(doc, entry, distance));
            await Perform(doc, ops, "vertex.face_edit").ConfigureAwait(true);
            return ToToken(new { action, distance, editedFaces = refs.Count, scope = scope.ToString().ToLowerInvariant(), warnings = distance == 0 ? new[] { "distance 0 leaves coplanar sides: the brush is invalid until the centre vertex is moved" } : Array.Empty<string>() });
        }

        private object ClipPreview(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var plane = ResolvePlane(parameters);
            var solids = SolidsOf(CandidateObjects(doc, parameters.Ids())).ToList();
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
            if (side != "front" && side != "back" && side != "both") throw new BridgeCommandException(ErrorCodes.InvalidRequest, "side must be front, back, or both.");
            // A cut is destructive: it needs explicit ids or a selection, never the whole map by default
            var solids = SolidsOf(RequestedOrSelectedObjects(doc, parameters)).ToList();
            var ops = new List<IOperation>();
            var changed = 0;
            var warnings = new List<object>();
            foreach (var solid in solids)
            {
                var parent = solid.Hierarchy.Parent;
                if (parent == null)
                {
                    warnings.Add(new { objectId = solid.ID, warning = "Solid has no parent; skipped clip." });
                    continue;
                }
                if (!solid.Split(doc.Map.NumberGenerator, plane, out var back, out var front)) continue;
                var created = new List<IMapObject>();
                if (side == "front" || side == "both") created.Add(front);
                if (side == "back" || side == "both") created.Add(back);
                ops.Add(new Detatch(parent.ID, solid));
                ops.Add(new Attach(parent.ID, created));
                changed++;
            }
            await Perform(doc, ops, "clip.apply").ConfigureAwait(true);
            return ToToken(new { clippedSolids = changed, side, warnings });
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
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, contents) };
            ops.AddRange(TransformOps(doc, translation, contents));
            ops.AddRange(ReplaceSelectionOps(doc, contents));
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
            return new { objectId = solid.ID, mapText = MapText.Write(solid) };
        }

        private async Task<JToken> ObjectImportMapText(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var text = parameters.Required<string>("text");
            var solid = MapText.ParseSolid(text, doc.Map.NumberGenerator);
            var select = parameters.Optional("select", true);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, solid) };
            if (select) ops.AddRange(ReplaceSelectionOps(doc, new[] { solid }));
            await Perform(doc, ops, "object.import_maptext").ConfigureAwait(true);
            return ToToken(new { created = ObjectInfo(solid), mapText = MapText.Write(solid) });
        }

        private async Task<JToken> ObjectImportMapTextBatch(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var blocks = new List<string>();
            if (parameters["texts"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    var value = item?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(value)) blocks.Add(value);
                }
            }
            else
            {
                var text = parameters.Required<string>("text");
                blocks = MapText.SplitBrushBlocks(text);
            }
            if (blocks.Count == 0) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "No brush blocks provided.");

            var solids = new List<Solid>();
            foreach (var block in blocks)
            {
                solids.Add(MapText.ParseSolid(block, doc.Map.NumberGenerator));
            }

            var select = parameters.Optional("select", true);
            var ops = new List<IOperation> { new Attach(doc.Map.Root.ID, solids) };
            if (select) ops.AddRange(ReplaceSelectionOps(doc, solids));
            await Perform(doc, ops, "object.import_maptext_batch").ConfigureAwait(true);
            return ToToken(new { count = solids.Count, created = solids.Select(ObjectInfo).ToList() });
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
                // Keep the last runs only (each holds up to 4000 log lines)
                foreach (var stale in _compileRuns.Values.Where(x => x.FinishedUtc != null).OrderByDescending(x => x.StartedUtc).Skip(MaxCompileRuns - 1).ToList())
                {
                    _compileRuns.Remove(stale.Id);
                }
            }

            try
            {
                return await RunCompileBatch(doc, parameters, profile, runId, log).ConfigureAwait(true);
            }
            finally
            {
                // Also when the batch throws: the run must not stay "active" and collect later compiles' output
                if (log.FinishedUtc == null) log.FinishedUtc = DateTime.UtcNow;
                lock (_compileLock)
                {
                    if (_activeCompileRunId == runId) _activeCompileRunId = null;
                }
            }
        }

        private async Task<JToken> RunCompileBatch(MapDocument doc, JObject parameters, string profile, string runId, CompileRunLog log)
        {
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
            int lineCount;
            lock (_compileLock)
            {
                log.Successful = batch.Successful;
                log.FinishedUtc = DateTime.UtcNow;
                lineCount = log.Lines.Count;
            }
            return ToToken(new { runId, profile, successful = batch.Successful, logLines = lineCount });
        }

        private object CompileLogTail(JObject parameters)
        {
            var runId = parameters.Optional<string>("runId", null);
            var count = parameters.Optional("count", 100);
            lock (_compileLock)
            {
                CompileRunLog log = null;
                if (runId == null) log = _compileRuns.Values.OrderByDescending(x => x.StartedUtc).FirstOrDefault();
                else _compileRuns.TryGetValue(runId, out log);
                if (log == null) return new { runId, lines = new string[0] };
                // Copied inside the lock: the compile's output handler appends to Lines concurrently.
                return new { runId = log.Id, successful = log.Successful, finished = log.FinishedUtc != null, lines = log.Lines.Skip(Math.Max(0, log.Lines.Count - count)).ToList() };
            }
        }

        private async Task<JToken> MapValidate(JObject parameters)
        {
            return await ProblemsCheck(parameters).ConfigureAwait(true);
        }

        private async Task<JToken> MapFixAllSafe(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var fixedProblems = new List<object>();
            var remaining = new List<object>();
            foreach (var checker in _problemChecks.Select(x => x.Value).Where(x => x.CanFix))
            {
                // Fixing one problem can change the others (ids get renumbered, objects move), so re-check after
                // each fix instead of walking a stale list. A fix that changes nothing is skipped from then on
                // and the next problem is tried.
                var checkerName = checker.GetType().FullName;
                var stuck = new HashSet<string>(StringComparer.Ordinal);
                string lastKey = null;
                string lastText = null;
                var lastCount = 0;
                var maxRounds = -1;
                for (var round = 0; ; round++)
                {
                    var problems = await checker.Check(doc, _ => true).ConfigureAwait(true);
                    if (maxRounds < 0) maxRounds = problems.Count * 2 + 10;
                    if (lastKey != null)
                    {
                        var stillThere = problems.Any(x => ProblemKey(x) == lastKey);
                        if (problems.Count < lastCount || !stillThere) fixedProblems.Add(new { checker = checkerName, Text = lastText });
                        else stuck.Add(lastKey);
                        lastKey = null;
                    }

                    var next = problems.FirstOrDefault(x => !stuck.Contains(ProblemKey(x)));
                    if (next == null || round >= maxRounds)
                    {
                        if (problems.Count > 0) remaining.Add(new { checker = checkerName, count = problems.Count, first = problems[0].Text });
                        break;
                    }
                    lastKey = ProblemKey(next);
                    lastText = next.Text;
                    lastCount = problems.Count;
                    await checker.Fix(doc, next).ConfigureAwait(true);
                }
            }
            return ToToken(new { fixedCount = fixedProblems.Count, fixedProblems, remaining });
        }

        private static string ProblemKey(Problem problem)
        {
            return problem.Text + "|" + string.Join(",", (problem.Objects ?? new List<IMapObject>()).Select(x => x.ID));
        }

        private async Task<JToken> SelectionFilter(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var filtered = FilterObjects(doc, doc.Selection.ToList(), parameters).ToList();
            await Perform(doc, ReplaceSelectionOps(doc, filtered), "selection.filter").ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private async Task<JToken> SelectionGrow(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var mode = parameters.Optional("mode", "children").ToLowerInvariant();
            if (mode != "parents" && mode != "children" && mode != "siblings") throw new BridgeCommandException(ErrorCodes.InvalidRequest, "selection.grow mode must be parents, children, or siblings.");
            var set = new HashSet<IMapObject>(doc.Selection);
            foreach (var obj in doc.Selection.ToList())
            {
                // The world root is never selectable
                if (mode == "parents") { if (obj.Hierarchy.Parent != null && obj.Hierarchy.Parent.Hierarchy.Parent != null) set.Add(obj.Hierarchy.Parent); }
                else if (mode == "siblings") { if (obj.Hierarchy.Parent != null) foreach (var s in obj.Hierarchy.Parent.Hierarchy) set.Add(s); }
                else foreach (var child in obj.FindAll()) set.Add(child);
            }
            await Perform(doc, ReplaceSelectionOps(doc, set), "selection.grow").ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private async Task<JToken> SelectionByBounds(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var bounds = RequiredBox(parameters);
            var mode = parameters.Optional("mode", "intersects").ToLowerInvariant();
            if (mode != "intersects" && mode != "inside") throw new BridgeCommandException(ErrorCodes.InvalidRequest, "selection.by_bounds mode must be intersects or inside.");
            var objects = doc.Map.Root.FindAll()
                .Where(x => x.Hierarchy.Parent != null && x.BoundingBox != null && !x.BoundingBox.IsEmpty())
                .Where(x => mode == "inside" ? x.BoundingBox.ContainedWithin(bounds) : x.BoundingBox.IntersectsWith(bounds))
                .ToList();
            await Perform(doc, ReplaceSelectionOps(doc, objects), "selection.by_bounds").ConfigureAwait(true);
            return ToToken(SelectionGet(parameters));
        }

        private object HistoryList(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var max = parameters.Optional<int?>("max", null);
            var history = HistoryFor(doc);
            lock (_historyLock)
            {
                // The most recent entries sit at the end of both lists
                var undo = max.HasValue ? history.Undo.TakeLast(Math.Max(0, max.Value)) : history.Undo;
                var redo = max.HasValue ? history.Redo.TakeLast(Math.Max(0, max.Value)) : history.Redo;
                return new
                {
                    scope = "mcp",
                    undoCount = history.Undo.Count,
                    redoCount = history.Redo.Count,
                    undo = undo.Select(x => new { x.Description, x.CreatedUtc }).ToList(),
                    redo = redo.Select(x => new { x.Description, x.CreatedUtc }).ToList()
                };
            }
        }

        private async Task<JToken> Undo(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var history = HistoryFor(doc);
            HistoryEntry entry;
            lock (_historyLock)
            {
                if (!history.Undo.Any()) return ToToken(new { undone = false, reason = "No MCP history entries." });
                entry = history.Undo.Last();
            }
            // Reversing an operation that is no longer the editor's newest one would leave the editor's own undo
            // stack pointing at an already reversed change (Ctrl+Z would then reverse it a second time).
            var stack = doc.Map.Data.GetOne<HistoryStack>();
            if (stack != null && !ReferenceEquals(stack.UndoOperation(), entry.Operation))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, "The newest change to this document was not made through MCP (it was edited in the editor since); use the editor's Undo, or continue editing.");
            }
            await MapDocumentOperation.Reverse(doc, entry.Operation).ConfigureAwait(true);
            lock (_historyLock)
            {
                history.Undo.Remove(entry);
                history.Redo.Add(entry);
            }
            return ToToken(new { undone = true, entry.Description });
        }

        private async Task<JToken> Redo(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var history = HistoryFor(doc);
            HistoryEntry entry;
            lock (_historyLock)
            {
                if (!history.Redo.Any()) return ToToken(new { redone = false, reason = "No MCP redo entries." });
                entry = history.Redo.Last();
            }
            var stack = doc.Map.Data.GetOne<HistoryStack>();
            if (stack != null && !ReferenceEquals(stack.RedoOperation(), entry.Operation))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidOperation, "The document changed since that operation was undone; it can no longer be redone.");
            }
            await MapDocumentOperation.Perform(doc, entry.Operation).ConfigureAwait(true);
            lock (_historyLock)
            {
                history.Redo.Remove(entry);
                history.Undo.Add(entry);
            }
            return ToToken(new { redone = true, entry.Description });
        }

        private object CordonGet(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var existing = doc.Map.Data.GetOne<CordonBounds>();
            var cordon = existing ?? new CordonBounds();
            return new { configured = existing != null, enabled = cordon.Enabled, bounds = cordon.Box.ToDto() };
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
            // Bounds given by the caller win; otherwise the current (or default) cordon box is kept
            var forwarded = new JObject(parameters) { ["enabled"] = enabled };
            if (forwarded["min"] == null || forwarded["min"].Type == JTokenType.Null) forwarded["min"] = JToken.FromObject(box.Start.ToDto());
            if (forwarded["max"] == null || forwarded["max"].Type == JTokenType.Null) forwarded["max"] = JToken.FromObject(box.End.ToDto());
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

        // An open document by file name (or display name); paths are compared in normalised form.
        private MapDocument FindOpenDocument(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var normalised = NormalizePath(path);
            var open = _documents.Value.OpenDocuments.OfType<MapDocument>().ToList();
            return open.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.FileName) && string.Equals(NormalizePath(x.FileName), normalised, StringComparison.InvariantCultureIgnoreCase))
                   ?? open.FirstOrDefault(x => string.Equals(x.Name, path, StringComparison.InvariantCultureIgnoreCase));
        }

        private static string NormalizePath(string path)
        {
            try { return IOPath.GetFullPath(path); }
            catch (Exception) { return path; }
        }

        // A stable id for an open document, valid for as long as it stays open.
        private static string DocumentIdOf(MapDocument document)
        {
            if (document == null) return null;
            return DocumentIds.GetValue(document, _ => Guid.NewGuid().ToString("N").Substring(0, 12));
        }

        private MapDocument DocumentById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return _documents.Value.OpenDocuments.OfType<MapDocument>().FirstOrDefault(x => string.Equals(DocumentIdOf(x), id, StringComparison.OrdinalIgnoreCase));
        }

        // pathSelectsDocument is false for tools whose `path` parameter is a file of their own (pointfiles).
        private MapDocument ResolveDocument(JObject parameters, bool requireExplicit, bool pathSelectsDocument = true)
        {
            if (parameters["documentId"] != null)
            {
                var id = parameters.Optional<string>("documentId", null);
                var byId = DocumentById(id);
                if (byId != null) return byId;
                throw new BridgeCommandException(ErrorCodes.DocumentNotFound, $"Open document id not found: {id}");
            }

            if (pathSelectsDocument && parameters["path"] != null)
            {
                var path = parameters.Optional<string>("path", null);
                var byPath = FindOpenDocument(path);
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

            if (requireExplicit) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "documentId, path or documentIndex is required.");
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

        // Keys that are not plain keyvalues: classname/spawnflags/origin have their own parameters, and "Location"
        // is the editor's own move trigger.
        private static bool IsReservedEntityProperty(string name)
        {
            return string.Equals(name, "classname", StringComparison.InvariantCultureIgnoreCase) ||
                   string.Equals(name, "spawnflags", StringComparison.InvariantCultureIgnoreCase) ||
                   string.Equals(name, "origin", StringComparison.InvariantCultureIgnoreCase) ||
                   string.Equals(name, "Location", StringComparison.InvariantCultureIgnoreCase);
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

        /// <summary>The objects without an ancestor in the same set (a transform or delete recurses into children).</summary>
        private static IEnumerable<IMapObject> TopLevel(IEnumerable<IMapObject> objects)
        {
            var set = new HashSet<IMapObject>(objects);
            return set.Where(x =>
            {
                for (var p = x.Hierarchy.Parent; p != null; p = p.Hierarchy.Parent)
                {
                    if (set.Contains(p)) return false;
                }
                return true;
            });
        }

        /// <summary>
        /// The transform plus the texture transform the editor's select tool would add: with texture lock on, a
        /// move or rotation keeps textures in place (uniform); with texture scale lock on, a scale scales them.
        /// </summary>
        private static IEnumerable<IOperation> TransformOps(MapDocument document, Matrix4x4 matrix, IEnumerable<IMapObject> objects, bool isScale = false)
        {
            var list = objects.ToList();
            var ops = new List<IOperation> { new TransformOperation(matrix, list) };
            var flags = document.Map.Data.GetOne<TransformationFlags>() ?? new TransformationFlags();
            if (!isScale && flags.TextureLock) ops.Add(new TransformTexturesUniform(matrix, list.SelectMany(x => x.FindAll())));
            else if (isScale && flags.TextureScaleLock) ops.Add(new TransformTexturesScale(matrix, list.SelectMany(x => x.FindAll())));
            return ops;
        }

        private IEnumerable<IMapObject> FilterObjects(MapDocument document, IEnumerable<IMapObject> source, JObject parameters)
        {
            var type = parameters.Optional<string>("type", null);
            var classname = parameters.Optional<string>("classname", null);
            var texture = parameters.Optional<string>("texture", null);
            var bounds = parameters["min"] != null || parameters["max"] != null ? RequiredBox(parameters) : null;
            if (!string.IsNullOrWhiteSpace(type) && !new[] { "Solid", "Entity", "Group" }.Contains(type, StringComparer.InvariantCultureIgnoreCase))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, "type must be Solid, Entity, or Group.");
            }

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
                // an entity or group uses the texture when any of its brushes does
                query = query.Where(x => SolidsOf(new[] { x }).Any(s => s.Faces.Any(f => string.Equals(f.Texture.Name, texture, StringComparison.InvariantCultureIgnoreCase))));
            }
            if (bounds != null)
            {
                query = query.Where(x => x.BoundingBox != null && x.BoundingBox.IntersectsWith(bounds));
            }
            return query;
        }

        /// <summary>Where a face tool's targets came from; echoed as <c>scope</c> so a stale selection is visible.</summary>
        private enum FaceScope { None, FaceRefs, FaceSelection, Ids, Selection, Map }

        private IEnumerable<FaceRef> ResolveFaceRefsOrObjects(MapDocument document, JObject parameters, bool allowAllObjectsWhenNoTarget = true)
        {
            return ResolveFaceRefsOrObjects(document, parameters, allowAllObjectsWhenNoTarget, out _);
        }

        private IEnumerable<FaceRef> ResolveFaceRefsOrObjects(MapDocument document, JObject parameters, bool allowAllObjectsWhenNoTarget, out FaceScope scope)
        {
            var request = ParseFaceTargetRequest(parameters);
            var refs = ResolveFaceRefs(document, request.FaceRefs).ToList();
            if (refs.Any())
            {
                scope = FaceScope.FaceRefs;
                return refs;
            }

            var selectedFaces = CurrentSelectedFaces(document).ToList();
            if (selectedFaces.Any() && !request.HasExplicitObjectTargets)
            {
                scope = FaceScope.FaceSelection;
                return selectedFaces;
            }

            if (request.HasExplicitObjectTargets)
            {
                scope = FaceScope.Ids;
                return SolidsOf(ResolveObjects(document, request.ObjectIds)).SelectMany(s => s.Faces.Select(f => new FaceRef(s, f)));
            }
            if (!document.Selection.IsEmpty)
            {
                scope = FaceScope.Selection;
                return SolidsOf(document.Selection).SelectMany(s => s.Faces.Select(f => new FaceRef(s, f)));
            }
            if (!allowAllObjectsWhenNoTarget)
            {
                scope = FaceScope.None;
                return Enumerable.Empty<FaceRef>();
            }
            scope = FaceScope.Map;
            return SolidsOf(document.Map.Root.FindAll()).SelectMany(s => s.Faces.Select(f => new FaceRef(s, f)));
        }

        /// <summary>The faces a mutating face tool works on; fails when nothing is targeted.</summary>
        private List<FaceRef> RequireFaces(MapDocument document, JObject parameters, string tool, out FaceScope scope)
        {
            var faces = ResolveFaceRefsOrObjects(document, parameters, allowAllObjectsWhenNoTarget: false, out scope).ToList();
            if (!faces.Any())
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"{tool} needs targets: ids (objects), objectId with faceId/faceIds, faceRefs, a selection of objects, or faces selected with face_select.");
            }
            return faces;
        }

        /// <summary>Every solid inside <paramref name="objects"/> (groups and brush entities contribute their brushes).</summary>
        private static IEnumerable<Solid> SolidsOf(IEnumerable<IMapObject> objects)
        {
            return objects.SelectMany(x => x.FindAll()).OfType<Solid>().Where(x => x.Hierarchy.Parent != null).Distinct();
        }

        /// <summary>Every solid inside <paramref name="objects"/> (groups and brush entities contribute their brushes).</summary>
        private static IEnumerable<Solid> SolidsOf(IEnumerable<IMapObject> objects)
        {
            return objects.SelectMany(x => x.FindAll()).OfType<Solid>().Where(x => x.Hierarchy.Parent != null).Distinct();
        }

        /// <summary>
        /// Operations that make <paramref name="targets"/> the selection. Targets already selected stay selected:
        /// the editor's Select drops already-selected objects when it is built, so a plain "deselect everything,
        /// select targets" would end with those objects deselected.
        /// </summary>
        private static IEnumerable<IOperation> ReplaceSelectionOps(MapDocument document, IEnumerable<IMapObject> targets)
        {
            var wanted = new HashSet<IMapObject>(targets);
            return new IOperation[]
            {
                new Deselect(document.Selection.Where(x => !wanted.Contains(x)).ToList()),
                new Select(wanted)
            };
        }

        /// <summary>Point-entity creation refuses classes the FGD marks as brush entities (they need entity_tie_brushes).</summary>
        private async Task RejectBrushEntityClass(MapDocument doc, string classname)
        {
            GameData data;
            try { data = await doc.Environment.GetGameData().ConfigureAwait(true); }
            catch (Exception ex)
            {
                Log("RejectBrushEntityClass: game data unavailable: " + ex.Message);
                return;
            }
            var schema = data?.GetClass(classname);
            if (schema != null && schema.ClassType == ClassType.Solid)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"'{classname}' is a brush entity in the FGD: create brushes and tie them with entity_tie_brushes instead of entity_create.");
            }
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

        private IEnumerable<FaceRef> ResolveFaceRefs(MapDocument document, IEnumerable<FaceTargetRef> refs)
        {
            if (refs == null) yield break;
            foreach (var faceRef in refs)
            {
                var obj = ResolveObject(document, faceRef.ObjectId);
                var face = obj.Data.OfType<Face>().FirstOrDefault(x => x.ID == faceRef.FaceId);
                if (face == null) throw new BridgeCommandException(ErrorCodes.ObjectNotFound, $"Face {faceRef.FaceId} was not found on object {faceRef.ObjectId}.");
                yield return new FaceRef(obj, face);
            }
        }

        private FaceTargetRequest ParseFaceTargetRequest(JObject parameters)
        {
            try
            {
                return FaceTargetRequest.Parse(parameters);
            }
            catch (ArgumentException ex)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, ex.Message);
            }
        }

        private static bool HasExplicitFaceReferenceParameters(JObject parameters)
        {
            return parameters["faceRefs"] != null
                   || parameters["faces"] != null
                   || parameters["faceId"] != null
                   || parameters["faceIds"] != null;
        }

        private IEnumerable<FaceRef> CurrentSelectedFaces(MapDocument document)
        {
            return document.Map.Data.GetOne<FaceSelection>()?.GetSelectedFaces().Select(x => new FaceRef(x.Key, x.Value)) ?? Enumerable.Empty<FaceRef>();
        }

        private static List<FaceTargetRef> FaceTargetRefs(IEnumerable<FaceRef> faces)
        {
            return faces.Select(x => new FaceTargetRef(x.Object.ID, x.Face.ID)).ToList();
        }

        private List<object> FaceInfos(MapDocument document, IEnumerable<FaceTargetRef> refs)
        {
            return ResolveFaceRefs(document, refs).Select(x => FaceInfo(x.Object, x.Face)).ToList();
        }

        private void EnsureTextureNames(MapDocument document, IEnumerable<FaceTargetRef> refs, string expectedTexture)
        {
            var mismatches = ResolveFaceRefs(document, refs)
                .Where(x => !string.Equals(x.Face.Texture.Name, expectedTexture, StringComparison.InvariantCultureIgnoreCase))
                .Select(x => $"{x.Object.ID}:{x.Face.ID}={x.Face.Texture.Name}")
                .ToList();
            if (mismatches.Any())
            {
                throw new BridgeCommandException(
                    ErrorCodes.InvalidOperation,
                    $"Texture update did not take effect for face(s): {string.Join(", ", mismatches)}. Expected '{expectedTexture}'.");
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

        /// <summary>Fan the face into triangles from its centre pushed <paramref name="distance"/> units along the normal (the editor's poke).</summary>
        private IEnumerable<IOperation> PokeFaceOperation(MapDocument document, FaceRef entry, float distance)
        {
            var vertices = entry.Face.Vertices.ToList();
            if (vertices.Count < 3) yield break;
            var center = vertices.Aggregate(Vector3.Zero, (a, b) => a + b) / vertices.Count + entry.Face.Plane.Normal * distance;
            var faces = new List<Face>();
            for (var i = 0; i < vertices.Count; i++)
            {
                faces.Add(MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), new[] { vertices[i], vertices[(i + 1) % vertices.Count], center }));
            }
            yield return new RemoveMapObjectData(entry.Object.ID, entry.Face);
            yield return new AddMapObjectData(entry.Object.ID, faces);
        }

        /// <summary>Split the face into a fan of coplanar triangles (the brush is invalid until vertices are moved out of the plane).</summary>
        private IEnumerable<IOperation> TriangulateFaceOperation(MapDocument document, FaceRef entry)
        {
            var vertices = entry.Face.Vertices.ToList();
            if (vertices.Count <= 3) yield break;
            var faces = new List<Face>();
            for (var i = 1; i < vertices.Count - 1; i++)
            {
                faces.Add(MakeFaceFrom(entry.Face, document.Map.NumberGenerator.Next("Face"), new[] { vertices[0], vertices[i], vertices[i + 1] }));
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

        /// <summary>A closed mesh: every edge is shared by exactly two faces.</summary>
        private static bool IsClosedMesh(IReadOnlyList<Face> faces)
        {
            var edges = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var face in faces)
            {
                var count = face.Vertices.Count;
                if (count < 3) return false;
                for (var i = 0; i < count; i++)
                {
                    var a = VertexKey(face.Vertices[i]);
                    var b = VertexKey(face.Vertices[(i + 1) % count]);
                    var key = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                    edges[key] = edges.TryGetValue(key, out var n) ? n + 1 : 1;
                }
            }
            return edges.Values.All(x => x == 2);
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
                uAxis = face.Texture.UAxis.ToDto(),
                vAxis = face.Texture.VAxis.ToDto(),
                normal = face.Plane.Normal.ToDto(),
                vertexCount = face.Vertices.Count,
                vertices = face.Vertices.Select(x => x.ToDto()).ToList()
            };
        }

        // B1: apply name/shift/scale, then U/V axes, then rotation LAST (so the axes it
        // rotates are the final ones), then sanitize. rotationMode "absolute" (default)
        // rotates the axes to an absolute angle via SetRotation; "store" writes the raw field.
        private static void ApplyTextureFields(Texture texture, JObject parameters, Vector3 normal, List<string> warnings, long faceId = 0, string defaultRotationMode = "absolute")
        {
            if (parameters["texture"] != null) texture.Name = parameters.Optional<string>("texture", texture.Name);
            if (parameters["name"] != null) texture.Name = parameters.Optional<string>("name", texture.Name);
            if (parameters["xShift"] != null) texture.XShift = parameters.Optional("xShift", texture.XShift);
            if (parameters["yShift"] != null) texture.YShift = parameters.Optional("yShift", texture.YShift);
            if (parameters["xScale"] != null) texture.XScale = parameters.Optional("xScale", texture.XScale);
            if (parameters["yScale"] != null) texture.YScale = parameters.Optional("yScale", texture.YScale);
            if (parameters["uAxis"] != null) texture.UAxis = parameters.RequiredVector("uAxis");
            if (parameters["vAxis"] != null) texture.VAxis = parameters.RequiredVector("vAxis");
            if (parameters["rotation"] != null)
            {
                var rotationMode = parameters.Optional("rotationMode", defaultRotationMode).ToLowerInvariant();
                var rotation = parameters.Optional("rotation", texture.Rotation);
                if (rotationMode == "store") texture.Rotation = rotation;
                else TextureAlignment.SetRotationSafe(texture, rotation);
            }
            TextureAlignment.Sanitize(texture, normal, faceId, warnings);
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
            ApplyTextureFields(texture, obj, plane.Normal, null, 0, "store");
            if (string.IsNullOrWhiteSpace(texture.Name)) texture.Name = "aaatrigger";
            return new PlaneDefinition(plane, texture);
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
            lock (_historyLock)
            {
                return _histories.GetValue(document, _ => new OperationHistory());
            }
        }

        private static string SafeWindowTitle(System.Diagnostics.Process process)
        {
            try { return process.MainWindowTitle; }
            catch (Exception ex) { Log("SafeWindowTitle: failed to read title for process " + process.Id + ": " + ex.Message); return ""; }
        }

        private async Task Perform(MapDocument document, IEnumerable<IOperation> operations)
        {
            await Perform(document, operations, "mcp operation").ConfigureAwait(true);
        }

        private async Task Perform(MapDocument document, IEnumerable<IOperation> operations, string description)
        {
            var list = operations.Where(x => x != null).ToList();
            if (!list.Any()) return;
            var transaction = new Transaction(list);
            await MapDocumentOperation.Perform(document, transaction).ConfigureAwait(true);
            // Every non-trivial transaction lands on the editor's undo stack, selection changes included, so it
            // is recorded here too: MCP undo only reverses the editor's newest operation, and an unrecorded one
            // would block it.
            if (!transaction.Trivial)
            {
                var history = HistoryFor(document);
                lock (_historyLock)
                {
                    history.Undo.Add(new HistoryEntry { Operation = transaction, Description = description, CreatedUtc = DateTime.UtcNow });
                    history.Redo.Clear();
                    if (history.Undo.Count > 100) history.Undo.RemoveAt(0);
                }
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
            catch (Exception ex)
            {
                Log("ReadBrushControls: failed to read controls for brush '" + brush.Name + "': " + ex.Message);
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

        private static void ApplyBrushParameters(IReadOnlyList<BrushControlBinding> bindings, JObject parameters)
        {
            if (parameters == null || !bindings.Any()) return;

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
                documentId = DocumentIdOf(document as MapDocument),
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

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        private const uint PW_RENDERFULLCONTENT = 0x00000002;

        private static Bitmap CapturePrintWindow(System.Windows.Forms.Control control)
        {
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
            {
                throw new InvalidOperationException("Control is not available for capture.");
            }

            var clientBounds = control.ClientRectangle;
            if (clientBounds.Width <= 0 || clientBounds.Height <= 0)
            {
                throw new InvalidOperationException("Control has no visible size.");
            }

            var bitmap = new Bitmap(clientBounds.Width, clientBounds.Height, PixelFormat.Format32bppArgb);
            bool ok;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    ok = PrintWindow(control.Handle, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            if (!ok)
            {
                bitmap.Dispose();
                throw new InvalidOperationException("PrintWindow failed for viewport control.");
            }

            return bitmap;
        }

        private static Bitmap CaptureScreen(Rectangle screenBounds, bool visible)
        {
            if (!visible || screenBounds.Width <= 0 || screenBounds.Height <= 0)
            {
                throw new InvalidOperationException("Viewport is not visible on screen for a screen capture.");
            }

            var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(screenBounds.Location, System.Drawing.Point.Empty, screenBounds.Size);
                }
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }

            return bitmap;
        }

        private static Bitmap ResizeIfNeeded(Bitmap source, int maxWidth, int maxHeight)
        {
            var widthLimit = maxWidth > 0 ? maxWidth : source.Width;
            var heightLimit = maxHeight > 0 ? maxHeight : source.Height;
            var ratio = Math.Min(widthLimit / (double)source.Width, heightLimit / (double)source.Height);
            if (ratio >= 1) return (Bitmap)source.Clone();

            var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
            var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
            var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            return resized;
        }

        private static string BitmapToPngBase64(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        private static string ViewName(ICamera camera)
        {
            if (camera is PerspectiveCamera) return "3d";
            if (camera is OrthographicCamera orthographic) return orthographic.ViewType.ToString().ToLowerInvariant();
            return camera?.Type.ToString().ToLowerInvariant() ?? "unknown";
        }

        private static bool ViewFilterMatches(string filter, string view, bool is2d, bool is3d, bool focused)
        {
            switch ((filter ?? "all").ToLowerInvariant())
            {
                case "all": return true;
                case "2d": return is2d;
                case "3d": return is3d;
                case "focused": return focused;
                case "top":
                case "front":
                case "side": return string.Equals(view, filter, StringComparison.InvariantCultureIgnoreCase);
                default: return string.Equals(view, filter, StringComparison.InvariantCultureIgnoreCase);
            }
        }

        private static object CameraInfo(ICamera camera)
        {
            if (camera is PerspectiveCamera perspective)
            {
                var direction = perspective.Direction;
                var normalized = direction.LengthSquared() > 0.0000001f ? Vector3.Normalize(direction) : direction;
                var lookAt = perspective.Position + normalized * 64f;
                return new
                {
                    type = "3d",
                    position = perspective.Position.ToDto(),
                    angles = perspective.Angles.ToDto(),
                    anglesDegrees = RadiansToDegrees(perspective.Angles).ToDto(),
                    direction = perspective.Direction.ToDto(),
                    lookAt = lookAt.ToDto(),
                    fov = perspective.FOV,
                    clipDistance = perspective.ClipDistance,
                    width = perspective.Width,
                    height = perspective.Height
                };
            }

            if (camera is OrthographicCamera orthographic)
            {
                return new
                {
                    type = "2d",
                    view = orthographic.ViewType.ToString().ToLowerInvariant(),
                    position = orthographic.Position.ToDto(),
                    zoom = orthographic.Zoom,
                    width = orthographic.Width,
                    height = orthographic.Height
                };
            }

            return new
            {
                type = camera?.Type.ToString().ToLowerInvariant(),
                position = camera == null ? (object)null : camera.Position.ToDto()
            };
        }

        private static Vector3 RadiansToDegrees(Vector3 radians)
        {
            const float factor = 180f / (float)Math.PI;
            return new Vector3(radians.X * factor, radians.Y * factor, radians.Z * factor);
        }

        private static Vector3 DegreesToRadians(Vector3 degrees)
        {
            const float factor = (float)Math.PI / 180f;
            return new Vector3(degrees.X * factor, degrees.Y * factor, degrees.Z * factor);
        }

        private static object RectInfo(Rectangle rectangle)
        {
            return new { x = rectangle.X, y = rectangle.Y, width = rectangle.Width, height = rectangle.Height };
        }

        // Returns the full ordered, de-duplicated candidate list. Pagination (offset/max)
        // is applied by the caller so it can report the true total.
        private static List<string> ResolveTexturePreviewNames(JObject parameters, TextureCollection collection, string query)
        {
            var explicitNames = new List<string>();
            var token = parameters["textures"];
            if (token is JArray array)
            {
                foreach (var item in array)
                {
                    if (item.Type == JTokenType.String) explicitNames.Add(item.Value<string>());
                    else if (item is JObject obj)
                    {
                        var name = obj.Optional<string>("name", null) ?? obj.Optional<string>("texture", null);
                        if (!string.IsNullOrWhiteSpace(name)) explicitNames.Add(name);
                    }
                }
            }
            else if (token != null && token.Type == JTokenType.String)
            {
                explicitNames.AddRange(token.Value<string>().Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
            }

            var source = explicitNames.Any()
                ? explicitNames
                : collection.GetBrowsableTextures()
                    .Where(x => string.IsNullOrWhiteSpace(query) || x.IndexOf(query, StringComparison.InvariantCultureIgnoreCase) >= 0)
                    .OrderBy(x => x, StringComparer.InvariantCultureIgnoreCase)
                    .ToList();

            return source
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }

        private static void DrawImageFitted(Graphics graphics, Image image, Rectangle target)
        {
            var ratio = Math.Min(target.Width / (double)image.Width, target.Height / (double)image.Height);
            var width = Math.Max(1, (int)Math.Round(image.Width * ratio));
            var height = Math.Max(1, (int)Math.Round(image.Height * ratio));
            var x = target.X + (target.Width - width) / 2;
            var y = target.Y + (target.Height - height) / 2;
            graphics.DrawImage(image, new Rectangle(x, y, width, height));
        }

        private static void DrawMissingTexture(Graphics graphics, Brush brush, Pen pen, Rectangle target)
        {
            graphics.FillRectangle(brush, target);
            graphics.DrawLine(pen, target.Left, target.Top, target.Right, target.Bottom);
            graphics.DrawLine(pen, target.Right, target.Top, target.Left, target.Bottom);
            graphics.DrawRectangle(pen, target.X, target.Y, target.Width - 1, target.Height - 1);
        }

        private static string ComputeFileSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
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
                BridgeLog.Append(System.IO.Path.Combine(directory, "bridge.log"), message);
            }
            catch
            {
                // Intentionally silent: Log is the logging sink itself, so a logging failure has nowhere to go.
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

            // The control's current value, for restoring it after a call's temporary override.
            public object CurrentValue()
            {
                if (Control is NumericControl n) return n.Value;
                if (Control is BooleanControl b) return b.Checked;
                if (Control is TextControl t) return t.EnteredText;
                if (Control is FontChooserControl f) return f.FontName;
                return null;
            }

            // Put back a value read by CurrentValue; untouched controls are left alone so the sidebar does not
            // see a spurious change.
            public void RestoreValue(object value)
            {
                if (Control is NumericControl n) { if (value is decimal d && n.Value != d) n.Value = d; }
                else if (Control is BooleanControl b) { if (value is bool v && b.Checked != v) b.Checked = v; }
                else if (Control is TextControl t) { if (!string.Equals(t.EnteredText, value as string, StringComparison.Ordinal)) t.EnteredText = value as string; }
                else if (Control is FontChooserControl f) { if (!string.Equals(f.FontName, value as string, StringComparison.Ordinal)) f.FontName = value as string; }
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
