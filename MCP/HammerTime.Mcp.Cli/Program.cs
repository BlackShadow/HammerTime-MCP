using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

[assembly: InternalsVisibleTo("HammerTime.Mcp.Tests")]

namespace HammerTime.Mcp.Cli
{
    internal static class Program
    {
        private const string ServerName = "hammertime";
        private const string ServerVersion = "0.1.0";
        private const string DefaultProtocolVersion = "2025-11-25";

        // Candidate editor process names checked by both the serve watchdog and the installer.
        private static readonly string[] EditorProcessNames = { "Hammertime.Editor", "HammertimeEditor", "Sledge.Editor" };

        // Exposed for schema-quality tests via InternalsVisibleTo. Building the list also
        // validates that every catalog tool resolves to a schema (SchemaForCatalogTool throws otherwise).
        internal static IReadOnlyList<(string Name, JObject Schema)> ToolSchemasForTest()
        {
            return ToolDefinition.CreateAll().Select(t => (t.Name, t.InputSchema)).ToList();
        }

        public static async Task<int> Main(string[] args)
        {
            try
            {
                var command = args.Length == 0 ? "serve" : args[0].ToLowerInvariant();
                var rest = args.Skip(1).ToArray();
                switch (command)
                {
                    case "serve":
                        await new McpStdioServer().Run();
                        return 0;
                    case "install":
                        Installer.Install(rest);
                        return 0;
                    case "config":
                    case "print-config":
                        Installer.PrintConfig(rest);
                        return 0;
                    case "status":
                        await PrintStatus(rest);
                        return 0;
                    case "doctor":
                        await PrintDoctor(rest);
                        return 0;
                    case "call":
                        await CallBridge(rest);
                        return 0;
                    case "list-clients":
                        Installer.ListClients(rest);
                        return 0;
                    case "help":
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                    default:
                        Console.Error.WriteLine($"Unknown command '{command}'.");
                        PrintHelp();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static async Task PrintStatus(string[] args)
        {
            var response = await BridgePipeClient.FromArgs(args).Send(BridgeMethods.Status, new JObject());
            Console.WriteLine(response.Ok
                ? response.Result.ToString(Formatting.Indented)
                : $"{response.Error.Code}: {response.Error.Message}");
        }

        private static async Task PrintDoctor(string[] args)
        {
            var response = await BridgePipeClient.FromArgs(args).Send(BridgeMethods.Doctor, new JObject());
            Console.WriteLine(response.Ok
                ? response.Result.ToString(Formatting.Indented)
                : $"{response.Error.Code}: {response.Error.Message}");
        }

        private static async Task CallBridge(string[] args)
        {
            if (args.Length == 0) throw new InvalidOperationException("call requires a bridge method name.");
            var method = args[0];
            var jsonArgs = new List<string>();
            var optionStart = 1;
            if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
            {
                jsonArgs.Add(args[1]);
                optionStart = 2;
                while (optionStart < args.Length && !args[optionStart].StartsWith("--", StringComparison.Ordinal))
                {
                    jsonArgs.Add(args[optionStart]);
                    optionStart++;
                }
            }
            var json = jsonArgs.Count == 0 ? "{}" : string.Join(" ", jsonArgs);
            var parameters = JObject.Parse(json);
            var response = await BridgePipeClient.FromArgs(args.Skip(optionStart).ToArray()).Send(method, parameters);
            Console.WriteLine(BridgeJson.SerializeResponse(response));
        }

        private static void PrintHelp()
        {
            Console.WriteLine("HammerTime MCP");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  hammertime-mcp serve");
            Console.WriteLine("  hammertime-mcp install --hammertime-dir <dir> [--clients generic,claude,claude-code,cursor,codex,vscode,vscode-insiders,windsurf,kimi-code,opencode,antigravity,antigravity-cli,gemini-cli,all] [--scope project|user] [--project-dir <dir>]");
            Console.WriteLine("  hammertime-mcp config");
            Console.WriteLine("  hammertime-mcp status");
            Console.WriteLine("  hammertime-mcp doctor");
            Console.WriteLine("  hammertime-mcp call <bridge.method> '{\"key\":\"value\"}'");
        }

        private sealed class McpStdioServer
        {
            private const string SkillResourceUri = "hammertime://skill/goldsrc-brushwork";
            private readonly List<ToolDefinition> _tools = ToolDefinition.CreateAll();

            public async Task Run()
            {
                try
                {
                    // MCP stdio framing is UTF-8; the default console code page can corrupt
                    // non-ASCII payloads. Setting the encoding throws when no console is
                    // attached (e.g. stdin/stdout redirected), so it is best-effort.
                    Console.InputEncoding = new UTF8Encoding(false);
                    Console.OutputEncoding = new UTF8Encoding(false);
                }
                catch
                {
                    // No console attached; the redirected streams keep their own encoding.
                }

                var serveStart = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    var absentPolls = 0;
                    while (true)
                    {
                        await Task.Delay(5000);
                        if (EditorProcessNames.Any(name => Process.GetProcessesByName(name).Length > 0))
                        {
                            absentPolls = 0;
                            continue;
                        }

                        // Startup grace: tolerate the editor not being up yet. Only exit
                        // after it has been absent for 3 consecutive polls and at least
                        // 30 seconds have elapsed since serve started.
                        absentPolls++;
                        if (absentPolls >= 3 && (DateTime.UtcNow - serveStart) >= TimeSpan.FromSeconds(30))
                        {
                            Console.Error.WriteLine("[MCP] HammerTime Editor is not running. Exiting MCP server.");
                            Environment.Exit(0);
                        }
                    }
                });

                string line;
                while ((line = await Console.In.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    line = line.TrimStart('\uFEFF');

                    JObject request;
                    try
                    {
                        request = JObject.Parse(line);
                    }
                    catch (Exception ex)
                    {
                        await Write(JsonRpcError(null, -32700, ex.Message));
                        continue;
                    }

                    var id = request["id"];
                    var hasId = id != null && id.Type != JTokenType.Null;
                    var method = request.Value<string>("method");
                    if (string.IsNullOrWhiteSpace(method))
                    {
                        // A request carrying an id but no method is an Invalid Request; a
                        // notification (no id) with no method is simply ignored.
                        if (hasId) await Write(JsonRpcError(id, -32600, "Invalid Request: missing method"));
                        continue;
                    }

                    try
                    {
                        var response = await Handle(id, method, request["params"] as JObject ?? new JObject());
                        // Notifications (absent or null id) never receive a response.
                        if (response != null && hasId) await Write(response);
                    }
                    catch (Exception ex)
                    {
                        if (hasId) await Write(JsonRpcError(id, -32000, ex.Message));
                    }
                }
            }

            private async Task<JObject> Handle(JToken id, string method, JObject parameters)
            {
                switch (method)
                {
                    case "initialize":
                        var requestedVersion = parameters.Value<string>("protocolVersion");
                        var supported = new[] { "2025-11-25", "2025-06-18", "2025-03-26" };
                        var negotiatedVersion = supported.Contains(requestedVersion) ? requestedVersion : DefaultProtocolVersion;
                        return JsonRpcResult(id, new
                        {
                            protocolVersion = negotiatedVersion,
                            capabilities = new { tools = new { listChanged = false }, resources = new { subscribe = false, listChanged = false } },
                            serverInfo = new { name = ServerName, version = ServerVersion }
                        });
                    case "notifications/initialized":
                        return null;
                    case "ping":
                        return JsonRpcResult(id, new { });
                    case "tools/list":
                        return JsonRpcResult(id, new { tools = _tools.Select(x => x.ToMcpTool()).ToList() });
                    case "tools/call":
                        return await ToolsCall(id, parameters);
                    case "resources/list":
                        return ResourcesList(id);
                    case "resources/read":
                        return await ResourcesRead(id, parameters);
                    default:
                        return JsonRpcError(id, -32601, $"Unknown MCP method '{method}'.");
                }
            }

            private async Task<JObject> ToolsCall(JToken id, JObject parameters)
            {
                var name = parameters.Value<string>("name");
                var args = parameters["arguments"] as JObject ?? new JObject();
                var tool = _tools.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
                if (tool == null) return JsonRpcError(id, -32602, $"Unknown tool '{name}'.");

                if (string.Equals(tool.BridgeMethod, BridgeMethods.SkillGet, StringComparison.Ordinal))
                {
                    return JsonRpcResult(id, McpContentFormatter.CreateToolResult(ReadLocalSkill()));
                }

                BridgeResponse response;
                try
                {
                    response = await BridgePipeClient.FromConfig().Send(tool.BridgeMethod, tool.WithDefaults(args));
                }
                catch (Exception ex)
                {
                    return JsonRpcResult(id, ToolError(ErrorCodes.EditorUnavailable, ex.Message));
                }

                if (response.Ok)
                {
                    return JsonRpcResult(id, McpContentFormatter.CreateToolResult(response.Result));
                }

                return JsonRpcResult(id, ToolError(response.Error.Code, response.Error.Message));
            }

            private static JObject ResourcesList(JToken id)
            {
                return JsonRpcResult(id, new
                {
                    resources = new[]
                    {
                        new
                        {
                            uri = SkillResourceUri,
                            name = "HammerTime GoldSrc Brushwork Skill",
                            description = "Installed HammerTime MCP mapping rules and visual-verification workflow.",
                            mimeType = "text/markdown"
                        }
                    }
                });
            }

            private static Task<JObject> ResourcesRead(JToken id, JObject parameters)
            {
                var uri = parameters.Value<string>("uri");
                if (!string.Equals(uri, SkillResourceUri, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(JsonRpcError(id, -32602, $"Unknown resource '{uri}'."));
                }

                var skill = ReadLocalSkill();
                var text = skill.Value<string>("text") ?? "";
                return Task.FromResult(JsonRpcResult(id, new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = SkillResourceUri,
                            mimeType = "text/markdown",
                            text
                        }
                    }
                }));
            }

            private static JObject ReadLocalSkill()
            {
                var config = McpBridgeConfig.LoadOrCreate();
                var path = string.IsNullOrWhiteSpace(config.SkillPath) ? McpBridgeConfig.GetDefaultSkillPath() : config.SkillPath;
                var bundledPath = Path.Combine(AppContext.BaseDirectory, "SKILL.md");
                var siblingPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SKILL.md"));

                if (!File.Exists(path) && File.Exists(McpBridgeConfig.GetDefaultSkillPath()))
                {
                    path = McpBridgeConfig.GetDefaultSkillPath();
                }
                if (!File.Exists(path) && File.Exists(bundledPath))
                {
                    path = bundledPath;
                }
                if (!File.Exists(path) && File.Exists(siblingPath))
                {
                    path = siblingPath;
                }

                var exists = File.Exists(path);
                return new JObject
                {
                    ["installed"] = exists,
                    ["path"] = path,
                    ["hash"] = exists ? ComputeFileSha256(path) : null,
                    ["text"] = exists ? File.ReadAllText(path) : ""
                };
            }

            private static string ComputeFileSha256(string path)
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }

            private static object ToolError(string code, string message)
            {
                return new
                {
                    isError = true,
                    content = new[] { new { type = "text", text = $"{code}: {message}" } },
                    structuredContent = new { ok = false, error = new { code, message } }
                };
            }

            private static Task Write(JObject response)
            {
                Console.Out.WriteLine(response.ToString(Formatting.None));
                return Console.Out.FlushAsync();
            }

            private static JObject JsonRpcResult(JToken id, object result)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = result == null ? JValue.CreateNull() : result is JToken token ? token : JToken.FromObject(result)
                };
            }

            private static JObject JsonRpcError(JToken id, int code, string message)
            {
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["error"] = new JObject
                    {
                        ["code"] = code,
                        ["message"] = message
                    }
                };
            }
        }

        private sealed class BridgePipeClient
        {
            // Connecting to a running editor is fast; a stalled response can take
            // much longer (large captures, slow edits), so the two are timed apart.
            private const int ConnectTimeoutMs = 5000;
            private const int DefaultIoTimeoutMs = 120000;
            private const int ReadBufferSize = 64 * 1024;
            private const long MaxLineBytes = 512L * 1024 * 1024;

            private readonly McpBridgeConfig _config;
            private readonly int _connectTimeoutMs;
            private readonly int _ioTimeoutMs;

            private BridgePipeClient(McpBridgeConfig config, int connectTimeoutMs, int ioTimeoutMs)
            {
                _config = config;
                _connectTimeoutMs = connectTimeoutMs;
                _ioTimeoutMs = ioTimeoutMs;
            }

            public static BridgePipeClient FromConfig()
            {
                var config = McpBridgeConfig.LoadOrCreate();
                return new BridgePipeClient(config, ConnectTimeoutMs, ResolveIoTimeout(config));
            }

            public static BridgePipeClient FromArgs(string[] args)
            {
                var path = Args.Value(args, "--config", null);
                var config = McpBridgeConfig.LoadOrCreate(path);
                var ioTimeout = Args.Value(args, "--timeout-ms", ResolveIoTimeout(config));
                return new BridgePipeClient(config, ConnectTimeoutMs, ioTimeout);
            }

            private static int ResolveIoTimeout(McpBridgeConfig config)
            {
                return config.BridgeTimeoutMs.HasValue && config.BridgeTimeoutMs.Value > 0
                    ? config.BridgeTimeoutMs.Value
                    : DefaultIoTimeoutMs;
            }

            public async Task<BridgeResponse> Send(string method, JObject parameters)
            {
                using (var pipe = new NamedPipeClientStream(".", _config.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(_connectTimeoutMs);
                    var request = new BridgeRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Method = method,
                        Token = _config.Token,
                        Params = parameters ?? new JObject()
                    };

                    await WriteLine(pipe, BridgeJson.SerializeRequest(request));
                    var line = await ReadLine(pipe);
                    if (line == null) throw new IOException("HammerTime MCP bridge closed the pipe without a response.");
                    return BridgeJson.DeserializeResponse(line);
                }
            }

            private async Task WriteLine(Stream stream, string line)
            {
                using (var cancellation = new System.Threading.CancellationTokenSource(_ioTimeoutMs))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
            }

            private async Task<string> ReadLine(Stream stream)
            {
                using (var cancellation = new System.Threading.CancellationTokenSource(_ioTimeoutMs))
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[ReadBufferSize];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellation.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new TimeoutException("Timed out waiting for HammerTime MCP bridge response.");
                        }

                        if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                        for (var i = 0; i < read; i++)
                        {
                            if (chunk[i] == (byte)'\n')
                            {
                                // The pipe carries exactly one response per call, so any bytes
                                // buffered after this newline are not needed and are ignored.
                                return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
                            }

                            buffer.WriteByte(chunk[i]);
                            if (buffer.Length > MaxLineBytes)
                            {
                                throw new IOException($"HammerTime MCP bridge response exceeded {MaxLineBytes} bytes without a newline.");
                            }
                        }
                    }
                }
            }
        }

        private sealed class ToolDefinition
        {
            public string Name { get; set; }
            public string BridgeMethod { get; set; }
            public string Description { get; set; }
            public JObject InputSchema { get; set; }
            public JObject DefaultArguments { get; set; }

            public object ToMcpTool()
            {
                return new { name = Name, description = Description, inputSchema = InputSchema };
            }

            public static List<ToolDefinition> CreateAll()
            {
                var tools = new List<ToolDefinition>
                {
                    Tool("hammertime_status", BridgeMethods.Status, "Get HammerTime MCP bridge status.", Schema()),
                    Tool("hammertime_skill", BridgeMethods.SkillGet, "Return the installed HammerTime GoldSrc mapping skill instructions.", Schema()),
                    Tool("documents_list", BridgeMethods.DocumentsList, "List open HammerTime documents.", Schema()),
                    Tool("documents_new", BridgeMethods.DocumentsNew, "Create a new HammerTime map document/tab.", Schema(("loaderHint", "string", "Optional HammerTime loader type name."))),
                    Tool("documents_open", BridgeMethods.DocumentsOpen, "Open a map document.", Schema(("path", "string", "Map file path."), ("loaderHint", "string", "Optional HammerTime loader type name.")), "path"),
                    Tool("documents_open_text", BridgeMethods.DocumentsOpenText, "Open a full Hammer .map file from a string.", Schema(("text", "string", "Full .map file text."), ("name", "string", "Display name for the new document."), ("loaderHint", "string", "Optional HammerTime loader type name.")), "text"),
                    Tool("object_import_maptext_batch", BridgeMethods.ObjectImportMapTextBatch, "Import multiple Hammer .map brush text blocks in one call.", Schema(("texts", "array", "Array of Hammer .map brush text blocks."), ("text", "string", "A single string containing multiple brush blocks."), ("select", "boolean", "Select imported brushes after creation."))),
                    Tool("documents_activate", BridgeMethods.DocumentsActivate, "Activate an open document by path or documentIndex.", Schema(("path", "string", "Open document path."), ("documentIndex", "integer", "Open document index."))),
                    Tool("documents_save", BridgeMethods.DocumentsSave, "Save the active or specified document.", Schema(("path", "string", "Save-as destination path. When it matches an open document's file name that document is saved in place; otherwise it is the destination and the target is documentIndex or the active document (so untitled documents can be saved). Omit to save in place."), ("documentIndex", "integer", "Target open document index when path is a new destination. Uses the active document when omitted."), ("loaderHint", "string", "Optional loader type name."))),
                    Tool("documents_export", BridgeMethods.DocumentsExport, "Export the active or specified document.", Schema(("path", "string", "Export destination path. When it matches an open document's file name that document is exported; otherwise it is the destination and the target is documentIndex or the active document (so untitled documents can be exported)."), ("documentIndex", "integer", "Target open document index when path is a new destination. Uses the active document when omitted."), ("loaderHint", "string", "Optional loader type name.")), "path"),
                    Tool("map_snapshot", BridgeMethods.MapSnapshot, "Return a bounded summary of map objects.", Schema(("maxObjects", "integer", "Maximum objects to return."))),
                    Tool("map_search", BridgeMethods.MapSearch, "Search map objects by type, classname, key/value, selected state, or text.", Schema(("type", "string", "Object type, such as Entity or Solid."), ("classname", "string", "Entity classname."), ("key", "string", "Entity property key."), ("value", "string", "Entity property value."), ("text", "string", "Text to search in classnames and properties."), ("selectedOnly", "boolean", "Only search selected objects."), ("max", "integer", "Maximum results."))),
                    Tool("selection_get", BridgeMethods.SelectionGet, "Return selected object IDs and bounds.", Schema()),
                    Tool("selection_set", BridgeMethods.SelectionSet, "Replace, add, or remove object selection.", WithEnum(Schema(("ids", "array", "Object IDs."), ("mode", "string", "How ids affect the selection. Defaults to replace.")), "mode", "replace", "add", "remove"), "ids"),
                    Tool("viewport_focus", BridgeMethods.ViewportFocus, "Focus 2D/3D viewports on ids, point, or current selection.", WithEnum(Schema(("ids", "array", "Object IDs to frame. Uses point or current selection when omitted."), ("point", "object", "World point to focus on."), ("views", "string", "Which viewports to focus. Defaults to all.")), "views", "all", "2d", "3d")),
                    Tool("viewport_capture", BridgeMethods.ViewportCapture, "Capture visible HammerTime 3D/2D viewport screenshots for visual inspection.",
                        WithEnum(WithEnum(WithEnum(WithEnum(Schema(
                            ("views", "string", "Which viewports to capture. Defaults to all."),
                            ("method", "string", "Capture method. auto tries GPU readback, then PrintWindow, then screen. Defaults to auto. gpu captures omit ImGui overlay highlights (entity names, gizmos, MCP highlights)."),
                            ("includeOverlays", "boolean", "Prefer a screen capture that includes overlay highlights/gizmos. Defaults to false."),
                            ("format", "string", "Output image format. Defaults to png."),
                            ("jpegQuality", "integer", "JPEG quality 1-100 when format is jpeg. Defaults to 85."),
                            ("maxWidth", "integer", "Maximum output image width. Defaults to 1024; 0 means native full size."),
                            ("maxHeight", "integer", "Maximum output image height. Defaults to 1024; 0 means native full size."),
                            ("waitForFrameMs", "integer", "Milliseconds to wait for a fresh rendered frame before capture. Defaults to 250."),
                            ("renderMode", "string", "Temporarily switch render mode before capture: textured or wireframe. flat is not supported."),
                            ("restoreRenderMode", "boolean", "Restore the previous render mode after capture. Defaults to true."),
                            ("camera", "object", "Optional inline camera pose applied to the selected viewports BEFORE the capture (avoids the freelook race between separate camera_set and capture calls). Same fields as viewport_camera_set: 3D position/lookAt/direction/anglesDegrees/fov (Vectors {x,y,z}, at most one orientation field) apply to perspective viewports; 2D center/zoom apply to orthographic viewports.")),
                            "views", "all", "3d", "2d", "top", "front", "side", "focused"),
                            "method", "auto", "gpu", "printwindow", "screen"),
                            "format", "png", "jpeg"),
                            "renderMode", "textured", "wireframe")),
                    Tool("viewport_camera_get", BridgeMethods.ViewportCameraGet, "Return camera state for HammerTime viewports.", WithEnum(Schema(("views", "string", "Which viewports to report. Defaults to all.")), "views", "all", "3d", "2d", "top", "front", "side", "focused")),
                    Tool("viewport_camera_set", BridgeMethods.ViewportCameraSet, "Set HammerTime 3D camera position/lookAt/angles/FOV or 2D center/zoom.", WithEnum(Schema(
                        ("views", "string", "Which viewports to modify. Inferred from provided parameters when omitted."),
                        ("position", "object", "3D camera position Vector {x,y,z}."),
                        ("lookAt", "object", "3D world point the camera should look at (Vector {x,y,z}). Mutually exclusive with direction and anglesDegrees."),
                        ("direction", "object", "3D camera forward direction Vector {x,y,z}. Mutually exclusive with lookAt and anglesDegrees."),
                        ("anglesDegrees", "object", "3D camera Euler angles in degrees {x,y,z}. Mutually exclusive with lookAt and direction."),
                        ("fov", "number", "3D field of view in degrees (clamped 10-170)."),
                        ("center", "object", "2D camera center Vector {x,y,z}."),
                        ("zoom", "number", "2D camera zoom (clamped 0.001-256).")),
                        "views", "all", "3d", "2d", "top", "front", "side", "focused")),
                    Tool("viewport_clear_marks", BridgeMethods.ViewportClearMarks, "Clear MCP overlay highlights and HammerTime object selection wireframes.", Schema(("clearSelection", "boolean", "Deselect selected map objects."), ("clearOverlay", "boolean", "Clear MCP overlay highlights and leak path."))),
                    Tool("editor_tools_list", BridgeMethods.EditorToolsList, "List HammerTime editor tools, including BrushTool and Vertex Manipulation Tool.", Schema()),
                    Tool("editor_tool_activate", BridgeMethods.EditorToolActivate, "Activate a HammerTime editor tool by name or alias, such as brush, vertex, vm, or select.", Schema(("name", "string", "Tool name or alias.")), "name"),
                    Tool("entity_create", BridgeMethods.EntityCreate, "Create a point entity with properties.", Schema(("classname", "string", "Entity classname."), ("origin", "object", "Vector {x,y,z}."), ("properties", "object", "Entity keyvalues."), ("spawnflags", "integer", "Spawn flags."), ("select", "boolean", "Select after creation."))),
                    Tool("entity_update", BridgeMethods.EntityUpdate, "Update entity classname, flags, origin, or keyvalues.", Schema(("id", "integer", "Entity object ID."), ("classname", "string", "New classname."), ("origin", "object", "Vector {x,y,z}."), ("properties", "object", "Keyvalues, null values remove keys."), ("spawnflags", "integer", "Spawn flags.")), "id"),
                    Tool("entity_tie_brushes", BridgeMethods.EntityTieBrushes, "Tie selected or specified solid brushes to a brush entity.", Schema(("ids", "array", "Solid brush object IDs. Uses selection when omitted."), ("classname", "string", "Brush entity classname, such as trigger_once or func_wall."), ("properties", "object", "Entity keyvalues, such as target or targetname."), ("spawnflags", "integer", "Spawn flags."), ("targetEntityId", "integer", "Existing entity ID to receive the brushes."), ("select", "boolean", "Select the entity after tying."))),
                    Tool("entity_untie_brushes", BridgeMethods.EntityUntieBrushes, "Move solid children out of brush entities back to world.", Schema(("ids", "array", "Brush entity IDs. Uses selection when omitted."), ("deleteEmptyEntity", "boolean", "Delete the entity after moving children to world."), ("select", "boolean", "Select moved brushes after untying."))),
                    Tool("scripted_sequence_list", BridgeMethods.ScriptedSequenceList, "List scripted_sequence entities.", Schema(("target", "string", "Optional related target name."))),
                    Tool("scripted_sequence_upsert", BridgeMethods.ScriptedSequenceUpsert, "Create or update a scripted_sequence by id or targetname.", Schema(("id", "integer", "Existing entity ID."), ("targetname", "string", "Sequence targetname."), ("origin", "object", "Vector {x,y,z}."), ("properties", "object", "Additional keyvalues."), ("m_iszEntity", "string", "Target NPC."), ("m_iszPlay", "string", "Animation to play."), ("m_iszIdle", "string", "Idle animation."), ("m_fMoveTo", "string", "Move-to mode."), ("m_flRadius", "string", "Search radius."))),
                    Tool("brush_types_list", BridgeMethods.BrushTypesList, "List HammerTime Brush Tool types and their type-specific parameters.", Schema()),
                    Tool("brush_create", BridgeMethods.BrushCreate, "Create a HammerTime Brush Tool shape. Valid types: Arch, Block, Tetrahedron, Pyramid, Wedge, Cylinder, Cone, Pipe, Sphere, Torus, Text. Aliases include barrel/barrell/can/tank for Cylinder.", WithEnum(BrushSchema(("type", "string", "Brush type or alias. Defaults to Block when omitted.")), "type", BrushCatalog.DefaultTypes.Select(x => x.Name).ToArray()), "min", "max"),
                    Tool("brush_create_box", BridgeMethods.BrushCreateBox, "Create a Block brush. Compatibility wrapper for old box calls.", BrushSchema(), "min", "max"),
                    BrushPreset("brush_create_arch", "Arch", "Create an Arch brush with parameters like numberOfSides, wallWidth, arc, startAngle, addHeight, curvedRamp, tiltAngle, and tiltInterp."),
                    BrushPreset("brush_create_block", "Block", "Create a Block brush."),
                    BrushPreset("brush_create_tetrahedron", "Tetrahedron", "Create a Tetrahedron brush. Parameter: useCentroid."),
                    BrushPreset("brush_create_pyramid", "Pyramid", "Create a Pyramid brush."),
                    BrushPreset("brush_create_wedge", "Wedge", "Create a Wedge brush/ramp."),
                    BrushPreset("brush_create_cylinder", "Cylinder", "Create a Cylinder brush. Use this for barrels, cans, tanks, and round columns. Parameter: numberOfSides."),
                    BrushPreset("brush_create_barrel", "Cylinder", "Create a barrel-shaped Cylinder brush. Parameter: numberOfSides."),
                    BrushPreset("brush_create_cone", "Cone", "Create a Cone brush. Parameter: numberOfSides."),
                    BrushPreset("brush_create_pipe", "Pipe", "Create a Pipe brush. Parameters: numberOfSides and wallWidth."),
                    BrushPreset("brush_create_sphere", "Sphere", "Create a Sphere brush. Parameter: numberOfSides."),
                    BrushPreset("brush_create_torus", "Torus", "Create a Torus brush. Parameters include crossSides, crossRadius, crossStartAngle, crossMakeHollow, crossArc, crossWallWidth, ringSides, ringArc, ringStartAngle, and rotationHeight."),
                    BrushPreset("brush_create_text", "Text", "Create a Text brush. Parameters include fontChooser, flattenFactor, and text."),
                    Tool("vertex_subtools_list", BridgeMethods.VertexSubtoolsList, "List Vertex Manipulation Tool subtools: Point manipulation, Point scaling, and Face editing.", Schema()),
                    Tool("vertex_subtool_activate", BridgeMethods.VertexSubtoolActivate, "Activate a Vertex Manipulation Tool subtool by name or alias, such as point, scale, or face.", Schema(("name", "string", "Vertex subtool name or alias."), ("activateVertexTool", "boolean", "Activate Vertex Manipulation Tool first.")), "name"),
                    Tool("texture_preview_sheet", BridgeMethods.TexturePreviewSheet, "Render texture candidates into a labeled preview sheet image so the AI can visually inspect options.", Schema(("textures", "array", "Texture names or objects with a name field."), ("query", "string", "Optional texture search text."), ("max", "integer", "Maximum textures per page."), ("tileSize", "integer", "Preview tile size in pixels."), ("columns", "integer", "Preview sheet column count."), ("offset", "integer", "Start index into the candidate list for pagination. Defaults to 0."), ("page", "integer", "Zero-based page (offset = page*max) used when offset is omitted."), ("showDimensions", "boolean", "Draw texture dimensions and semantic-flag glyphs on each tile. Defaults to true."))),
                    Tool("texture_browser_capture", BridgeMethods.TexturePreviewSheet, "Render texture-browser-style candidates into a labeled preview sheet image.", Schema(("textures", "array", "Texture names or objects with a name field."), ("query", "string", "Optional texture search text."), ("max", "integer", "Maximum textures per page."), ("tileSize", "integer", "Preview tile size in pixels."), ("columns", "integer", "Preview sheet column count."), ("offset", "integer", "Start index into the candidate list for pagination. Defaults to 0."), ("page", "integer", "Zero-based page (offset = page*max) used when offset is omitted."), ("showDimensions", "boolean", "Draw texture dimensions and semantic-flag glyphs on each tile. Defaults to true."))),
                    Tool("objects_delete", BridgeMethods.ObjectsDelete, "Delete map objects by ID.", Schema(("ids", "array", "Object IDs.")), "ids"),
                    Tool("objects_transform", BridgeMethods.ObjectsTransform, "Translate, rotate, or scale objects.", Schema(("ids", "array", "Object IDs."), ("translation", "object", "Vector {x,y,z}."), ("rotationDegrees", "object", "Euler degrees {x,y,z}."), ("scale", "object", "Scale vector {x,y,z}."), ("pivot", "object", "Transform pivot.")), "ids"),
                    Tool("problems_check", BridgeMethods.ProblemsCheck, "Run HammerTime map problem checks.", Schema(("selectedOnly", "boolean", "Only check currently selected objects."))),
                    Tool("problems_fix", BridgeMethods.ProblemsFix, "Fix one problem reported by problems_check.", Schema(("checker", "string", "Checker full type/name."), ("index", "integer", "Problem index from checker.")), "checker"),
                    Tool("leaks_load_pointfile", BridgeMethods.LeaksLoadPointfile, "Load a .lin/.pts pointfile, focus the leak path, and report intersecting objects.", Schema(("path", "string", "Pointfile path."), ("text", "string", "Pointfile text."))),
                    Tool("overlay_set", BridgeMethods.OverlaySet, "Highlight object IDs in HammerTime viewports.", Schema(("ids", "array", "Object IDs."), ("label", "string", "Overlay label.")), "ids"),
                    Tool("overlay_clear", BridgeMethods.OverlayClear, "Clear MCP overlay highlights and leak path.", Schema())
                };

                AddMissingCatalogTools(tools);
                return tools;
            }

            private static void AddMissingCatalogTools(List<ToolDefinition> tools)
            {
                var existing = new HashSet<string>(tools.Select(x => x.Name), StringComparer.Ordinal);
                foreach (var entry in McpToolCatalog.CreateAll())
                {
                    if (existing.Contains(entry.Name)) continue;
                    tools.Add(Tool(entry.Name, entry.BridgeMethod, entry.Description, SchemaForCatalogTool(entry.Name)));
                }
            }

            private static JObject SchemaForCatalogTool(string name)
            {
                switch (name)
                {
                    case "texture_project":
                        return WithEnum(WithEnum(Schema(
                            ("ids", "array", "Object IDs. Uses selected faces when omitted."),
                            ("objectId", "integer", "Single object ID when targeting one face."),
                            ("faceId", "integer", "Single face ID when targeting one face."),
                            ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."),
                            ("faceRefs", "array", "Explicit face references with objectId and faceId."),
                            ("mode", "string", "Projection mode. Defaults to planar."),
                            ("texture", "string", "Texture name to apply before projection."),
                            ("scale", "number", "Texture scale. Omit for cylindrical auto-wrap scale."),
                            ("direction", "object", "Planar projection direction vector."),
                            ("align", "string", "Planar alignment. Defaults to natural."),
                            ("axis", "object", "Cylindrical axis vector."),
                            ("origin", "object", "Cylindrical origin vector."),
                            ("labels", "integer", "Number of horizontal texture repeats around the cylinder."),
                            ("centerLabel", "boolean", "Center one repeated label/panel on each cylindrical wrap."),
                            ("sides", "integer", "Optional faceted-cylinder side count for seamless polygon wrapping."),
                            ("numberOfSides", "integer", "Alias for sides.")),
                            "mode", "planar", "cylindrical", "fit", "center"),
                            "align", "natural", "center", "fit", "left", "right", "top", "bottom");
                    case "face_texture_set":
                        return WithEnum(Schema(
                            ("ids", "array", "Object IDs. Uses selected faces when omitted."),
                            ("objectId", "integer", "Single object ID when targeting one face."),
                            ("faceId", "integer", "Single face ID when targeting one face."),
                            ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."),
                            ("faceRefs", "array", "Explicit face references with objectId and faceId."),
                            ("texture", "string", "Texture name."),
                            ("name", "string", "Alias for texture name."),
                            ("xScale", "number", "Texture X scale."),
                            ("yScale", "number", "Texture Y scale."),
                            ("xShift", "number", "Texture X shift."),
                            ("yShift", "number", "Texture Y shift."),
                            ("rotation", "number", "Texture rotation in degrees."),
                            ("rotationMode", "string", "absolute rotates the axes to the given angle (default); store writes the raw rotation field."),
                            ("uAxis", "object", "Texture U axis vector {x,y,z}."),
                            ("vAxis", "object", "Texture V axis vector {x,y,z}.")), "rotationMode", "absolute", "store");
                    case "vertex_move":
                        return Schema(
                            ("ids", "array", "Solid object IDs."),
                            ("vertexKeys", "array", "Vertex snapshot keys returned by vertex_snapshot."),
                            ("vertexRefs", "array", "Explicit vertex references with objectId, faceId, and vertexIndex."),
                            ("delta", "object", "Relative movement vector {x,y,z}."),
                            ("position", "object", "Absolute destination vector {x,y,z}."));
                    case "compile_run":
                        return WithEnum(Schema(
                            ("profile", "string", "Built-in compile profile. Defaults to full."),
                            ("steps", "array", "Optional compile step names to restrict the run (such as CSG, BSP, VIS, RAD)."),
                            ("arguments", "object", "Compile argument overrides keyed by tool name."),
                            ("useCordonBounds", "boolean", "Compile with current cordon bounds."),
                            ("workingDirectory", "string", "Working directory for compile tools."),
                            ("runGame", "boolean", "Launch the game after a successful compile.")),
                            "profile", "fast", "full", "custom");
                    case "cordon_set":
                        return WithRequired(Schema(
                            ("min", "object", "Cordon minimum corner."),
                            ("max", "object", "Cordon maximum corner."),
                            ("enabled", "boolean", "Enable cordon after setting bounds.")),
                            "min", "max");
                    case "cordon_enable":
                        return WithRequired(Schema(("enabled", "boolean", "Enable or disable cordon rendering/export.")), "enabled");
                    case "compile_log_tail":
                        return Schema(("runId", "string", "Compile run ID. Uses the most recent run when omitted."), ("count", "integer", "Maximum log lines to return. Defaults to 100."));
                    case "selection_filter":
                        return Schema(("type", "string", "Object type filter, such as Solid or Entity."), ("classname", "string", "Entity classname filter."), ("texture", "string", "Keep objects that use this texture on any face."), ("min", "object", "Filter box minimum corner. Requires max."), ("max", "object", "Filter box maximum corner. Requires min."));
                    case "selection_grow":
                        return WithEnum(Schema(("mode", "string", "How to grow the selection. Defaults to children.")), "mode", "parents", "children", "siblings");
                    case "selection_by_bounds":
                        return WithRequired(WithEnum(Schema(("min", "object", "Selection box minimum corner."), ("max", "object", "Selection box maximum corner."), ("mode", "string", "intersects selects objects whose bounds overlap the box; inside selects only objects fully contained. Defaults to intersects.")), "mode", "intersects", "inside"), "min", "max");
                    case "texture_apply":
                        return WithRequired(Schema(("ids", "array", "Object IDs. Uses selection when omitted."), ("objectId", "integer", "Single object ID when targeting one or more faces."), ("faceId", "integer", "Single face ID on objectId."), ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit face references with objectId and faceId."), ("texture", "string", "Texture name to apply."), ("textureScale", "number", "Texture scale.")), "texture");
                    case "texture_replace":
                        // find/from and replace/to are accepted aliases (the bridge resolves either).
                        return WithRequired(Schema(("find", "string", "Texture name to replace (alias: from)."), ("from", "string", "Alias for find."), ("replace", "string", "Replacement texture name (alias: to)."), ("to", "string", "Alias for replace."), ("selectedOnly", "boolean", "Limit replacement to the current selection instead of the whole map."), ("ids", "array", "Optional object IDs to limit replacement."), ("align", "boolean", "Realign replaced faces to their normal. Defaults to false so existing alignment is preserved.")), "find", "replace");
                    case "texture_align_face":
                        return WithEnum(WithEnum(Schema(("ids", "array", "Object IDs. Uses selected faces when omitted."), ("objectId", "integer", "Single object ID when targeting one or more faces."), ("faceId", "integer", "Single face ID on objectId."), ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit face references with objectId and faceId."), ("mode", "string", "world fixes axes to the world axes; face (alias normal) aligns to the face plane; reset also zeroes shift and rotation. Defaults to normal."), ("rotation", "number", "Optional absolute texture rotation in degrees applied after alignment."), ("justify", "string", "Optional justify within the face after alignment. Defaults to none.")), "mode", "world", "face", "normal", "reset"), "justify", "left", "right", "top", "bottom", "center", "fit", "none");
                    case "texture_copy_from_face":
                        return Schema(("sourceFace", "object", "Source face reference with objectId and faceId."), ("projected", "boolean", "Project the source alignment across the shared edge (default true); false copies the raw texture axes verbatim."), ("ids", "array", "Target object IDs. Uses selected faces when omitted."), ("objectId", "integer", "Single target object ID when targeting one or more faces."), ("faceId", "integer", "Single target face ID on objectId."), ("faceIds", "array", "Target face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit target face references with objectId and faceId."));
                    case "face_list":
                    case "face_select":
                    case "face_delete":
                        return Schema(("ids", "array", "Object IDs. Uses selection when omitted."), ("objectId", "integer", "Single object ID when targeting one or more faces."), ("faceId", "integer", "Single face ID on objectId."), ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit face references with objectId and faceId."));
                    case "vertex_snapshot":
                    case "vertex_triangulate":
                        return Schema(("ids", "array", "Solid object IDs."), ("objectId", "integer", "Single object ID when targeting one or more faces."), ("faceId", "integer", "Single face ID on objectId."), ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit face references with objectId and faceId."));
                    case "vertex_face_edit":
                        return WithEnum(Schema(("ids", "array", "Solid object IDs."), ("objectId", "integer", "Single object ID when targeting one or more faces."), ("faceId", "integer", "Single face ID on objectId."), ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."), ("faceRefs", "array", "Explicit face references with objectId and faceId."), ("action", "string", "Face edit action. poke fans the face from a center point; triangulate splits it into triangles. Defaults to poke.")), "action", "poke", "triangulate");
                    case "vertex_split_face":
                        return WithRequired(Schema(("objectId", "integer", "Object ID."), ("faceId", "integer", "Face ID."), ("vertexIndexA", "integer", "First vertex index. Must be non-adjacent to vertexIndexB."), ("vertexIndexB", "integer", "Second vertex index. Must be non-adjacent to vertexIndexA.")), "objectId", "faceId", "vertexIndexA", "vertexIndexB");
                    case "clip_preview":
                    case "clip_apply":
                    case "clip_split":
                        return WithEnum(Schema(("ids", "array", "Solid object IDs. Uses selection when omitted."), ("normal", "object", "Clip plane normal vector. Provide with an optional point."), ("point", "object", "Point on the clip plane. Defaults to the origin."), ("point1", "object", "First point of a three-point clip plane."), ("point2", "object", "Second point of a three-point clip plane."), ("point3", "object", "Third point of a three-point clip plane."), ("side", "string", "Which side to keep (clip_apply only; clip_split always keeps both). Defaults to front.")), "side", "front", "back", "both");
                    case "object_export_maptext":
                        return Schema(("id", "integer", "Object ID to export. Falls back to ids[0]."), ("ids", "array", "Object IDs; the first is exported when id is omitted."));
                    case "object_import_maptext":
                        return WithRequired(Schema(("text", "string", "Hammer .map brush text for a single solid."), ("select", "boolean", "Select the imported object. Defaults to true.")), "text");
                    case "prefab_create":
                        return WithRequired(Schema(("library", "string", "Prefab library name or .ol path."), ("index", "integer", "Prefab index within the library. Resolved from name when omitted."), ("name", "string", "Prefab name; used when index is omitted."), ("origin", "object", "Placement origin. Defaults to the world origin.")), "library");
                    case "prefabs_list":
                        return Schema(("directory", "string", "Optional prefab directory. Defaults to the bundled prefabs folder."));
                    case "entity_schema":
                        return WithRequired(Schema(("classname", "string", "Entity classname to look up in the active FGD.")), "classname");
                    case "entity_create_from_schema":
                        return WithRequired(Schema(("classname", "string", "Entity classname to create using FGD defaults."), ("origin", "object", "Placement origin. Defaults to the world origin."), ("properties", "object", "Keyvalues that override FGD defaults."), ("spawnflags", "integer", "Spawn flags."), ("select", "boolean", "Select after creation.")), "classname");
                    case "texture_search":
                        return Schema(("query", "string", "Texture search text."), ("text", "string", "Alias for query."), ("max", "integer", "Maximum results. Defaults to 100."), ("groupFrames", "boolean", "Group animation/frame variants under one logical entry by basename. Defaults to true."), ("includeSpecial", "boolean", "Include tool and sky textures. Defaults to true."));
                    case "textures_list":
                        return Schema(("max", "integer", "Maximum textures to return. Defaults to 500."), ("detailed", "boolean", "Return per-texture metadata (dimensions, wad, flags, family) instead of plain names. Defaults to false."));
                    case "history_list":
                        return Schema(("max", "integer", "Maximum history entries."));
                    case "brush_create_from_planes":
                        return WithRequired(Schema(("planes", "array", "At least four plane definitions, each with points or normal/point plus optional texture."), ("texture", "string", "Default texture name for faces without one."), ("select", "boolean", "Select the created brush. Defaults to true.")), "planes");
                    case "texture_apply_smart":
                        return WithEnum(Schema(
                            ("classify", "string", "nearest always assigns each face its best-matching role (default); strict only assigns faces whose best role dot exceeds 0.9 and reports the rest in skippedFaces."),
                            ("front", "string", "Texture applied to faces whose normal points along frontDirection."),
                            ("back", "string", "Texture applied to faces opposite frontDirection."),
                            ("left", "string", "Texture applied to the left faces relative to frontDirection."),
                            ("right", "string", "Texture applied to the right faces relative to frontDirection."),
                            ("top", "string", "Texture applied to upward-facing (+Z) faces."),
                            ("bottom", "string", "Texture applied to downward-facing (-Z) faces."),
                            ("frontDirection", "object", "Front-facing direction vector. Defaults to -Y (0,-1,0)."),
                            ("scale", "number", "Uniform texture scale applied to every assigned face."),
                            ("fit", "boolean", "Fit each texture once across its face."),
                            ("center", "boolean", "Center each texture on its face."),
                            ("ids", "array", "Object IDs whose faces are textured. Uses all objects or selected faces when omitted."),
                            ("objectId", "integer", "Single object ID when targeting one or more faces."),
                            ("faceId", "integer", "Single face ID on objectId."),
                            ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."),
                            ("faceRefs", "array", "Explicit face references with objectId and faceId.")),
                            "classify", "nearest", "strict");
                    case "hammertime_doctor":
                        return ParameterlessSchema();
                    case "editor_tools_list":
                        return ParameterlessSchema();
                    case "compile_profiles_list":
                        return ParameterlessSchema();
                    case "map_validate":
                        return Schema(("selectedOnly", "boolean", "Only validate the current selection instead of the whole map."));
                    case "map_fix_all_safe":
                        return DocumentTargetSchema();
                    case "undo":
                        return DocumentTargetSchema();
                    case "redo":
                        return DocumentTargetSchema();
                    case "cordon_get":
                        return DocumentTargetSchema();
                    case "fgd_entities_list":
                        return Schema(("type", "string", "Entity class type filter, such as PointClass or SolidClass."), ("query", "string", "Case-insensitive substring to match against entity classnames."));
                    case "documents_close":
                        return Schema(
                            ("path", "string", "Open document path to close. Uses the active document when omitted."),
                            ("documentIndex", "integer", "Open document index to close. Uses the active document when omitted."),
                            ("force", "boolean", "Close without prompting to save unsaved changes. Defaults to false (a save prompt may block)."));
                    case "texture_audit":
                        return WithEnum(Schema(
                            ("ids", "array", "Object IDs to audit. Audits the whole map when omitted."),
                            ("objectId", "integer", "Single object ID when targeting one or more faces."),
                            ("faceId", "integer", "Single face ID on objectId."),
                            ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."),
                            ("faceRefs", "array", "Explicit face references with objectId and faceId."),
                            ("scaleTolerance", "number", "Fractional tolerance for scale-outlier detection around the reference scale. Defaults to 0.25."),
                            ("scaleReference", "string", "Compare scales to the median audited scale or to 1.0. Defaults to median."),
                            ("nonUniformTolerance", "number", "Relative |xScale-yScale| tolerance for non-uniform scale. Defaults to 0.05."),
                            ("rotationTolerance", "number", "Degrees a rotation may deviate from a 90-degree multiple. Defaults to 0.5."),
                            ("shiftTolerance", "number", "Tolerance for fractional-shift detection. Defaults to 0.01."),
                            ("stretchThreshold", "number", "Scale magnitude above which a face counts as stretched. Defaults to 4.0."),
                            ("maxOffenders", "integer", "Maximum offenders returned. Defaults to 50."),
                            ("checkCoplanarMismatch", "boolean", "Flag adjacent coplanar faces with different textures. Defaults to true."),
                            ("checkToolTextures", "boolean", "Flag visible tool textures on world solids. Defaults to true."),
                            ("checkHiddenFaces", "boolean", "Flag hidden coplanar faces that should be NULL. Defaults to false."),
                            ("checkPropTextures", "boolean", "On prop-scale solids, flag random-tiling (-N) textures and framed art cropped at scale ~1 (both informational). Defaults to true."),
                            ("propMaxDimension", "number", "Bounding-box longest edge (units) at or below which a solid is treated as a prop for the prop-texture checks. Defaults to 160.")),
                            "scaleReference", "median", "one");
                    case "map_design_audit":
                    {
                        var designSchema = Schema(
                            ("selectedOnly", "boolean", "Audit only the current selection instead of the whole map. Defaults to false."),
                            ("checks", "array", "Subset of checks to run. Runs all when omitted."),
                            ("monotonyThreshold", "number", "Texture share above which a map is flagged monotonous. Defaults to 0.6."),
                            ("microSize", "number", "Bounding-box smallest dimension below which a solid is a micro-brush. Defaults to 1.0."),
                            ("maxExtent", "number", "World extent limit; objects beyond +/- this are flagged. Defaults to 4096."),
                            ("cellSize", "number", "Spatial cell size for hotspot/lighting bucketing. Defaults to 1024."),
                            ("cellFaceThreshold", "integer", "Face count per cell above which a wpoly hotspot is reported. Defaults to 400."),
                            ("lightRadius", "number", "Light influence radius for possibly-unlit cell detection. Defaults to 768."),
                            ("includeProblemChecks", "boolean", "Embed HammerTime problem-check results. Defaults to false."),
                            ("maxOffenders", "integer", "Maximum offenders per check. Defaults to 50."));
                        if (designSchema["properties"]?["checks"] is JObject checksProp)
                        {
                            checksProp["items"] = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("off_grid", "micro_brush", "texture_monotony", "scale_conventions", "unlit", "missing_player_start", "world_extents", "wpoly_hotspots")
                            };
                        }
                        return designSchema;
                    }
                    default:
                        throw new InvalidOperationException($"No input schema defined for catalog tool '{name}'. Add a case in SchemaForCatalogTool.");
                }
            }

            private static ToolDefinition Tool(string name, string method, string description, JObject schema, params string[] required)
            {
                if (required.Length > 0) schema["required"] = new JArray(required);
                return new ToolDefinition { Name = name, BridgeMethod = method, Description = description, InputSchema = schema };
            }

            private static ToolDefinition BrushPreset(string toolName, string brushType, string description)
            {
                var tool = Tool(toolName, BridgeMethods.BrushCreate, description, BrushSchema(), "min", "max");
                tool.DefaultArguments = new JObject { ["type"] = brushType };
                return tool;
            }

            public JObject WithDefaults(JObject args)
            {
                var merged = DefaultArguments == null ? new JObject() : new JObject(DefaultArguments);
                foreach (var prop in (args ?? new JObject()).Properties())
                {
                    merged[prop.Name] = prop.Value.DeepClone();
                }
                return merged;
            }

            private static JObject BrushSchema(params (string Name, string Type, string Description)[] extraProperties)
            {
                var properties = new List<(string Name, string Type, string Description)>
                {
                    ("min", "object", "Minimum Vector {x,y,z}."),
                    ("max", "object", "Maximum Vector {x,y,z}."),
                    ("texture", "string", "Texture name."),
                    ("textureScale", "number", "Texture scale."),
                    ("select", "boolean", "Select after creation."),
                    ("round", "boolean", "Round created vertices where the brush type supports it."),
                    ("parameters", "object", "Type-specific Brush Tool control values, such as numberOfSides, wallWidth, arc, startAngle, curvedRamp, text, or fontChooser.")
                };
                properties.InsertRange(0, extraProperties);
                return Schema(properties.ToArray());
            }

            private static JObject Schema(params (string Name, string Type, string Description)[] properties)
            {
                var props = new JObject();
                foreach (var prop in properties)
                {
                    JObject property;
                    if (prop.Type == "object" && IsVectorProperty(prop.Name))
                    {
                        // Vector params accept {x,y,z}; emit an explicit sub-schema so an LLM
                        // knows the exact shape instead of a bare object.
                        property = VectorSchema(prop.Description);
                    }
                    else
                    {
                        property = new JObject
                        {
                            ["type"] = prop.Type,
                            ["description"] = prop.Description
                        };
                        if (prop.Type == "array")
                        {
                            property["items"] = ArrayItemsSchema(prop.Name);
                        }
                    }

                    props[prop.Name] = property;
                }

                return new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true,
                    ["properties"] = props
                };
            }

            // Property names that carry an {x,y,z} vector across the plugin handlers. Only
            // upgraded to VectorSchema when declared with type "object" (e.g. "scale" is a
            // vector for objects_transform but a plain number for texture tools).
            private static readonly HashSet<string> VectorPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "min", "max", "origin", "position", "point", "point1", "point2", "point3",
                "direction", "axis", "center", "normal", "offset", "translation", "translate",
                "rotate", "rotationDegrees", "scale", "pivot", "delta", "frontDirection",
                "uAxis", "vAxis", "lookAt", "anglesDegrees"
            };

            private static bool IsVectorProperty(string name)
            {
                return VectorPropertyNames.Contains(name);
            }

            private static JObject VectorSchema(string description)
            {
                return new JObject
                {
                    ["type"] = "object",
                    ["description"] = description,
                    ["properties"] = new JObject
                    {
                        ["x"] = new JObject { ["type"] = "number" },
                        ["y"] = new JObject { ["type"] = "number" },
                        ["z"] = new JObject { ["type"] = "number" }
                    },
                    ["required"] = new JArray("x", "y", "z")
                };
            }

            private static JObject WithEnum(JObject schema, string property, params string[] values)
            {
                if (schema?["properties"] is JObject props && props[property] is JObject prop)
                {
                    prop["enum"] = new JArray(values);
                }
                return schema;
            }

            private static JObject WithRequired(JObject schema, params string[] names)
            {
                if (schema != null && names.Length > 0) schema["required"] = new JArray(names);
                return schema;
            }

            private static JObject ParameterlessSchema()
            {
                var schema = Schema();
                schema["description"] = "No parameters.";
                return schema;
            }

            private static JObject DocumentTargetSchema()
            {
                return Schema(
                    ("path", "string", "Target open document path. Uses the active document when omitted."),
                    ("documentIndex", "integer", "Target open document index. Uses the active document when omitted."));
            }

            private static JObject ArrayItemsSchema(string propertyName)
            {
                if (string.Equals(propertyName, "ids", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "integer" };
                }

                if (string.Equals(propertyName, "faceIds", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "integer" };
                }

                if (string.Equals(propertyName, "textures", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "string" };
                }

                if (string.Equals(propertyName, "texts", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "string" };
                }

                if (string.Equals(propertyName, "steps", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(propertyName, "vertexKeys", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "string" };
                }

                return new JObject { ["type"] = "object" };
            }
        }

        private static class Installer
        {
            public static void Install(string[] args)
            {
                var pluginOnly = Args.Has(args, "--plugin-only");
                var clientsOnly = Args.Has(args, "--clients-only");
                if (pluginOnly && clientsOnly) throw new InvalidOperationException("--plugin-only and --clients-only cannot be used together.");

                var hammerTimeDir = ResolveHammerTimeDirectory(Args.Value(args, "--hammertime-dir", null));
                var cliPath = Path.GetFullPath(Environment.ProcessPath ?? typeof(Program).Assembly.Location);
                var projectDir = ResolveProjectDirectory(Args.Value(args, "--project-dir", null));
                var clients = Args.Csv(args, "--clients", "generic").ToList();
                if (clients.Any(x => string.Equals(x, "all", StringComparison.OrdinalIgnoreCase)))
                {
                    clients = AllClientIds().ToList();
                }
                clients = clients.Select(NormalizeClientId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var config = McpBridgeConfig.LoadOrCreate(null, hammerTimeDir);
                config.HammerTimeDirectory = hammerTimeDir;
                var skillInstall = InstallSkill(cliPath, !pluginOnly && clients.Contains("codex", StringComparer.OrdinalIgnoreCase));
                config.SkillPath = skillInstall.AppDataPath ?? McpBridgeConfig.GetDefaultSkillPath();
                config.SkillHash = skillInstall.Hash;
                McpBridgeConfig.Save(McpBridgeConfig.GetDefaultConfigPath(), config);

                var pluginFiles = new List<string>();
                if (!clientsOnly)
                {
                    VerifyInstallSafe(hammerTimeDir, Args.Has(args, "--allow-running"));
                    pluginFiles = CopyPluginFiles(hammerTimeDir);
                }

                var scope = Args.Value(args, "--scope", "user").ToLowerInvariant();
                var updated = new List<string>();
                if (!pluginOnly)
                {
                    foreach (var client in clients)
                    {
                        updated.AddRange(InstallClientConfig(client, scope, cliPath, projectDir));
                    }
                }

                Console.WriteLine(pluginOnly ? "HammerTime MCP plugin installed." : clientsOnly ? "HammerTime MCP client configs installed." : "HammerTime MCP installed.");
                Console.WriteLine($"HammerTime directory: {hammerTimeDir}");
                Console.WriteLine($"Bridge config: {McpBridgeConfig.GetDefaultConfigPath()}");
                Console.WriteLine($"Pipe: {config.PipeName}");
                Console.WriteLine($"Skill file: {config.SkillPath} {(string.IsNullOrWhiteSpace(config.SkillHash) ? "(source not found)" : config.SkillHash)}");
                if (!string.IsNullOrWhiteSpace(skillInstall.CodexPath)) Console.WriteLine($"Codex skill mirror: {skillInstall.CodexPath}");
                if (scope == "project") Console.WriteLine($"Project config directory: {projectDir}");
                if (!clientsOnly)
                {
                    Console.WriteLine($"Plugin staging directory: {Path.Combine(hammerTimeDir, "plugins", "hammertime-mcp")}");
                    Console.WriteLine($"Plugin load directory: {hammerTimeDir}");
                    Console.WriteLine($"Plugin files copied: {pluginFiles.Count}");
                }
                foreach (var file in updated) Console.WriteLine($"Client config updated: {file}");
            }

            public static void PrintConfig(string[] args)
            {
                var cliPath = Path.GetFullPath(Environment.ProcessPath ?? typeof(Program).Assembly.Location);
                var server = McpServerJson(cliPath);
                Console.WriteLine(server.ToString(Formatting.Indented));
            }

            public static void ListClients(string[] args)
            {
                var scope = Args.Value(args, "--scope", "user").ToLowerInvariant();
                var projectDir = ResolveProjectDirectory(Args.Value(args, "--project-dir", null));
                foreach (var candidate in ClientCandidates(scope, projectDir))
                {
                    Console.WriteLine($"{candidate.Name}: {candidate.Path}");
                }
            }

            private static string ResolveHammerTimeDirectory(string provided)
            {
                if (!string.IsNullOrWhiteSpace(provided))
                {
                    var full = Path.GetFullPath(provided);
                    if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"HammerTime directory not found: {full}");
                    return full;
                }

                var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "Hammertime.Editor.exe");
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var repoRoot = FindRepoRoot();
                var candidates = new[]
                {
                    Path.Combine(programFilesX86, "HammertimeEditor"),
                    Path.Combine(programFiles, "HammertimeEditor"),
                    File.Exists(cwdCandidate) ? Directory.GetCurrentDirectory() : null,
                    Path.Combine(repoRoot, "Sledge.Editor", "bin", "Debug", "net6.0-windows7.0"),
                    Path.Combine(repoRoot, "Sledge.Editor", "bin", "Release", "net6.0-windows7.0"),
                    Path.Combine(programFilesX86, "HammerTime"),
                    Path.Combine(programFiles, "HammerTime")
                };

                foreach (var candidate in candidates)
                {
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        File.Exists(Path.Combine(candidate, "Hammertime.Editor.exe")))
                    {
                        return candidate;
                    }
                }

                throw new DirectoryNotFoundException("Could not find HammerTime output directory. Pass --hammertime-dir.");
            }

            private static string ResolveProjectDirectory(string provided)
            {
                var directory = string.IsNullOrWhiteSpace(provided) ? Directory.GetCurrentDirectory() : provided;
                var full = Path.GetFullPath(directory);
                if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Project directory not found: {full}");
                return full;
            }

            private static void VerifyInstallSafe(string hammerTimeDir, bool allowRunning)
            {
                var running = GetRunningHammerTimeProcesses().ToList();
                if (running.Any() && !allowRunning)
                {
                    var details = string.Join("; ", running.Select(x => $"{x.ProcessName} pid {x.Id} '{SafeWindowTitle(x)}'"));
                    throw new InvalidOperationException(
                        "HammerTime is running, so plugin DLLs may be locked. Save/close HammerTime and rerun install, " +
                        "or pass --allow-running for a best-effort copy. Running processes: " + details);
                }

                var targets = new[]
                {
                    Path.Combine(hammerTimeDir, "HammerTime.Mcp.Plugin.dll"),
                    Path.Combine(hammerTimeDir, "HammerTime.Mcp.Shared.dll"),
                    Path.Combine(hammerTimeDir, "plugins", "hammertime-mcp", "HammerTime.Mcp.Plugin.dll"),
                    Path.Combine(hammerTimeDir, "plugins", "hammertime-mcp", "HammerTime.Mcp.Shared.dll")
                };
                foreach (var target in targets.Where(File.Exists))
                {
                    AssertWritable(target);
                }
            }

            private static IEnumerable<System.Diagnostics.Process> GetRunningHammerTimeProcesses()
            {
                var currentId = System.Diagnostics.Process.GetCurrentProcess().Id;
                return System.Diagnostics.Process.GetProcesses().Where(x =>
                    x.Id != currentId &&
                    EditorProcessNames.Any(name => string.Equals(x.ProcessName, name, StringComparison.OrdinalIgnoreCase)));
            }

            private static string SafeWindowTitle(System.Diagnostics.Process process)
            {
                try { return process.MainWindowTitle; }
                catch { return ""; }
            }

            private static void AssertWritable(string path)
            {
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new IOException($"Install target is not writable by this user: {path}. Run the installer from an elevated terminal or install HammerTime in a user-writable directory.", ex);
                }
                catch (Exception ex)
                {
                    throw new IOException($"Install target is locked or not writable: {path}. Close HammerTime and rerun install; if it still fails, run the installer from an elevated terminal.", ex);
                }
            }

            private static List<string> CopyPluginFiles(string hammerTimeDir)
            {
                var sourceDir = ResolvePluginOutputDirectory();

                var pluginDir = Path.Combine(hammerTimeDir, "plugins", "hammertime-mcp");
                var stagingDir = Path.Combine(hammerTimeDir, "plugins", "hammertime-mcp.staging");
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
                Directory.CreateDirectory(stagingDir);
                Directory.CreateDirectory(pluginDir);

                var requiredNames = new[]
                {
                    "HammerTime.Mcp.Plugin.dll",
                    "HammerTime.Mcp.Plugin.deps.json",
                    "HammerTime.Mcp.Shared.dll"
                };
                foreach (var name in requiredNames)
                {
                    var source = Path.Combine(sourceDir, name);
                    if (!File.Exists(source))
                    {
                        throw new FileNotFoundException($"Required HammerTime MCP plugin file was not found: {source}");
                    }
                }

                var pluginNames = new[]
                {
                    "HammerTime.Mcp.Plugin.dll",
                    "HammerTime.Mcp.Plugin.pdb",
                    "HammerTime.Mcp.Plugin.deps.json",
                    "HammerTime.Mcp.Shared.dll",
                    "HammerTime.Mcp.Shared.pdb"
                };

                var copied = new List<string>();
                foreach (var name in pluginNames)
                {
                    var source = Path.Combine(sourceDir, name);
                    if (!File.Exists(source)) continue;

                    var stagingDestination = Path.Combine(stagingDir, name);
                    File.Copy(source, stagingDestination, true);
                }

                foreach (var name in pluginNames)
                {
                    var stagingSource = Path.Combine(stagingDir, name);
                    if (!File.Exists(stagingSource)) continue;

                    var pluginDestination = Path.Combine(pluginDir, name);
                    File.Copy(stagingSource, pluginDestination, true);
                    copied.Add(pluginDestination);

                    var loadDestination = Path.Combine(hammerTimeDir, name);
                    File.Copy(stagingSource, loadDestination, true);
                    copied.Add(loadDestination);
                }

                Directory.Delete(stagingDir, true);

                // Newtonsoft.Json is a shared dependency. Keep it in the editor
                // root where the CLR resolves it for both the editor and plugin.
                var newtonsoftSource = Path.Combine(sourceDir, "Newtonsoft.Json.dll");
                if (File.Exists(newtonsoftSource))
                {
                    var newtonsoftDest = Path.Combine(hammerTimeDir, "Newtonsoft.Json.dll");
                    File.Copy(newtonsoftSource, newtonsoftDest, true);
                    copied.Add(newtonsoftDest);
                }

                if (!File.Exists(Path.Combine(hammerTimeDir, "HammerTime.Mcp.Plugin.dll")))
                {
                    throw new FileNotFoundException($"HammerTime.Mcp.Plugin.dll was not copied to {hammerTimeDir}");
                }
                return copied;
            }

            private static SkillInstallResult InstallSkill(string cliPath, bool copyCodexMirror)
            {
                var source = ResolveSkillSourcePath(cliPath);
                var result = new SkillInstallResult { SourcePath = source, AppDataPath = McpBridgeConfig.GetDefaultSkillPath() };
                if (!File.Exists(source)) return result;

                CopyFileCreatingDirectory(source, result.AppDataPath);
                result.Hash = ComputeFileSha256(result.AppDataPath);

                if (copyCodexMirror)
                {
                    result.CodexPath = McpBridgeConfig.GetCodexSkillPath();
                    CopyFileCreatingDirectory(source, result.CodexPath);
                }

                return result;
            }

            private static string ResolveSkillSourcePath(string cliPath)
            {
                var cliDirectory = Path.GetDirectoryName(Path.GetFullPath(cliPath)) ?? Directory.GetCurrentDirectory();
                var candidates = new List<string>
                {
                    Path.Combine(cliDirectory, "SKILL.md"),
                    Path.GetFullPath(Path.Combine(cliDirectory, "..", "SKILL.md")),
                    Path.Combine(AppContext.BaseDirectory, "SKILL.md"),
                    Path.GetFullPath(Path.Combine(FindRepoRootSafe(), "MCP-Install", "SKILL.md"))
                };

                return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
            }

            private static string FindRepoRootSafe()
            {
                try
                {
                    return FindRepoRoot();
                }
                catch
                {
                    return Directory.GetCurrentDirectory();
                }
            }

            private static void CopyFileCreatingDirectory(string source, string destination)
            {
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.Copy(source, destination, true);
            }

            private static string ComputeFileSha256(string path)
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }

            private static string ResolvePluginOutputDirectory()
            {
                var baseDir = AppContext.BaseDirectory;
                if (File.Exists(Path.Combine(baseDir, "HammerTime.Mcp.Plugin.dll"))) return baseDir;

                var bundlePluginDir = Path.GetFullPath(Path.Combine(baseDir, "..", "Plugin"));
                if (File.Exists(Path.Combine(bundlePluginDir, "HammerTime.Mcp.Plugin.dll"))) return bundlePluginDir;

                var pluginRoot = Path.Combine(FindRepoRoot(), "MCP", "HammerTime.Mcp.Plugin", "bin");
                if (Directory.Exists(pluginRoot))
                {
                    var dll = Directory.GetFiles(pluginRoot, "HammerTime.Mcp.Plugin.dll", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (dll != null) return Path.GetDirectoryName(dll);
                }

                throw new DirectoryNotFoundException("Could not find built HammerTime.Mcp.Plugin output. Build HammerTime.Mcp.Plugin first.");
            }

            private static IEnumerable<string> InstallClientConfig(string client, string scope, string cliPath, string projectDir)
            {
                if (client == "codex")
                {
                    var path = ClientPath("codex", scope, projectDir);
                    UpsertTomlServer(path, cliPath);
                    return new[] { path };
                }

                var candidate = ClientCandidates(scope, projectDir).FirstOrDefault(x => x.Name == client);
                if (candidate == null) throw new InvalidOperationException($"Unknown MCP client '{client}'.");

                if (client == "opencode")
                {
                    UpsertOpenCodeServer(candidate.Path, cliPath);
                }
                else
                {
                    var vscode = client == "vscode" || client == "vscode-insiders";
                    var rootProperty = vscode ? "servers" : "mcpServers";
                    UpsertJsonServer(candidate.Path, rootProperty, cliPath, vscode);
                }
                return new[] { candidate.Path };
            }

            private static string[] AllClientIds()
            {
                return new[]
                {
                    "generic",
                    "claude",
                    "claude-code",
                    "cursor",
                    "codex",
                    "vscode",
                    "vscode-insiders",
                    "windsurf",
                    "kimi-code",
                    "opencode",
                    "antigravity",
                    "antigravity-cli",
                    "gemini-cli"
                };
            }

            private static string NormalizeClientId(string client)
            {
                switch ((client ?? "").Trim().ToLowerInvariant())
                {
                    case "claudecode":
                    case "claude_code":
                        return "claude-code";
                    case "kimi":
                    case "kimi_code":
                        return "kimi-code";
                    case "open-code":
                    case "open_code":
                    case "open":
                        return "opencode";
                    case "antigravity-editor":
                    case "ag":
                        return "antigravity";
                    case "ag-cli":
                    case "antigravity_cli":
                        return "antigravity-cli";
                    case "gemini":
                    case "gemini_cli":
                        return "gemini-cli";
                    case "code-insiders":
                    case "code_insiders":
                    case "vscode_insiders":
                    case "vs-code-insiders":
                    case "vs_code_insiders":
                        return "vscode-insiders";
                    default:
                        return (client ?? "").Trim().ToLowerInvariant();
                }
            }

            private static List<ClientCandidate> ClientCandidates(string scope, string projectDir)
            {
                return new List<ClientCandidate>
                {
                    new ClientCandidate("generic", ClientPath("generic", scope, projectDir)),
                    new ClientCandidate("claude", ClientPath("claude", scope, projectDir)),
                    new ClientCandidate("claude-code", ClientPath("claude-code", scope, projectDir)),
                    new ClientCandidate("cursor", ClientPath("cursor", scope, projectDir)),
                    new ClientCandidate("codex", ClientPath("codex", scope, projectDir)),
                    new ClientCandidate("vscode", ClientPath("vscode", scope, projectDir)),
                    new ClientCandidate("vscode-insiders", ClientPath("vscode-insiders", scope, projectDir)),
                    new ClientCandidate("windsurf", ClientPath("windsurf", scope, projectDir)),
                    new ClientCandidate("kimi-code", ClientPath("kimi-code", scope, projectDir)),
                    new ClientCandidate("opencode", ClientPath("opencode", scope, projectDir)),
                    new ClientCandidate("antigravity", ClientPath("antigravity", scope, projectDir)),
                    new ClientCandidate("antigravity-cli", ClientPath("antigravity-cli", scope, projectDir)),
                    new ClientCandidate("gemini-cli", ClientPath("gemini-cli", scope, projectDir))
                };
            }

            private static string ClientPath(string client, string scope, string projectDir)
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (scope == "project")
                {
                    switch (client)
                    {
                        case "generic": return Path.Combine(projectDir, ".mcp.json");
                        case "cursor": return Path.Combine(projectDir, ".cursor", "mcp.json");
                        case "codex": return Path.Combine(projectDir, ".codex", "config.toml");
                        case "vscode": return Path.Combine(projectDir, ".vscode", "mcp.json");
                        case "vscode-insiders": return Path.Combine(projectDir, ".vscode", "mcp.json");
                        case "claude": return Path.Combine(projectDir, ".claude", "claude_desktop_config.json");
                        case "windsurf": return Path.Combine(projectDir, ".windsurf", "mcp_config.json");
                        case "claude-code": return Path.Combine(projectDir, ".mcp.json");
                        case "kimi-code": return Path.Combine(projectDir, ".kimi", "mcp.json");
                        case "opencode": return Path.Combine(projectDir, "opencode.json");
                        case "antigravity": return Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
                        case "antigravity-cli": return Path.Combine(projectDir, ".agents", "mcp_config.json");
                        case "gemini-cli": return Path.Combine(projectDir, ".gemini", "settings.json");
                    }
                }

                switch (client)
                {
                    case "generic": return Path.Combine(appData, "HammerTime.MCP", "mcp.json");
                    case "claude": return Path.Combine(appData, "Claude", "claude_desktop_config.json");
                    case "claude-code": return Path.Combine(home, ".mcp.json");
                    case "cursor": return Path.Combine(home, ".cursor", "mcp.json");
                    case "codex": return Path.Combine(home, ".codex", "config.toml");
                    case "vscode": return Path.Combine(appData, "Code", "User", "mcp.json");
                    case "vscode-insiders": return Path.Combine(appData, "Code - Insiders", "User", "mcp.json");
                    case "windsurf": return Path.Combine(home, ".codeium", "windsurf", "mcp_config.json");
                    case "kimi-code": return Path.Combine(home, ".kimi", "mcp.json");
                    case "opencode": return Path.Combine(home, ".config", "opencode", "opencode.json");
                    case "antigravity": return Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
                    case "antigravity-cli": return Path.Combine(home, ".gemini", "antigravity-cli", "mcp_config.json");
                    case "gemini-cli": return Path.Combine(home, ".gemini", "settings.json");
                    default: throw new InvalidOperationException($"Unknown client '{client}'.");
                }
            }

            private static void UpsertJsonServer(string path, string rootProperty, string cliPath, bool vscode)
            {
                var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
                var servers = root[rootProperty] as JObject ?? new JObject();
                servers[ServerName] = vscode
                    ? new JObject
                    {
                        ["type"] = "stdio",
                        ["command"] = cliPath,
                        ["args"] = new JArray("serve")
                    }
                    : McpServerJson(cliPath);
                root[rootProperty] = servers;
                WriteJson(path, root);
            }

            private static void UpsertOpenCodeServer(string path, string cliPath)
            {
                var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
                var servers = root["mcp"] as JObject ?? new JObject();
                servers[ServerName] = new JObject
                {
                    ["type"] = "local",
                    ["command"] = new JArray(cliPath, "serve"),
                    ["enabled"] = true
                };
                root["mcp"] = servers;
                WriteJson(path, root);
            }

            private static void UpsertTomlServer(string path, string cliPath)
            {
                var existing = File.Exists(path) ? File.ReadAllText(path) : "";
                const string begin = "# HammerTime MCP BEGIN";
                const string end = "# HammerTime MCP END";
                var start = existing.IndexOf(begin, StringComparison.Ordinal);
                if (start >= 0)
                {
                    var finish = existing.IndexOf(end, start, StringComparison.Ordinal);
                    if (finish >= 0) existing = existing.Remove(start, finish + end.Length - start).TrimEnd();
                }

                var block = string.Join(Environment.NewLine, new[]
                {
                    begin,
                    "[mcp_servers.hammertime]",
                    $"command = \"{EscapeToml(cliPath)}\"",
                    "args = [\"serve\"]",
                    end
                });

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, (existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block + Environment.NewLine).TrimStart());
            }

            private static JObject McpServerJson(string cliPath)
            {
                return new JObject
                {
                    ["command"] = cliPath,
                    ["args"] = new JArray("serve")
                };
            }

            private static void WriteJson(string path, JObject root)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, root.ToString(Formatting.Indented));
            }

            private static string EscapeToml(string value)
            {
                return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }

            private static string FindRepoRoot()
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "HammerTime.sln"))) return dir.FullName;
                    dir = dir.Parent;
                }
                return Directory.GetCurrentDirectory();
            }

            private sealed class ClientCandidate
            {
                public string Name { get; }
                public string Path { get; }

                public ClientCandidate(string name, string path)
                {
                    Name = name;
                    Path = path;
                }
            }

            private sealed class SkillInstallResult
            {
                public string SourcePath { get; set; }
                public string AppDataPath { get; set; }
                public string CodexPath { get; set; }
                public string Hash { get; set; }
            }
        }

        private static class Args
        {
            public static bool Has(string[] args, string name)
            {
                return args.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            }

            public static string Value(string[] args, string name, string fallback)
            {
                for (var i = 0; i < args.Length - 1; i++)
                {
                    if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
                }
                return fallback;
            }

            public static int Value(string[] args, string name, int fallback)
            {
                var value = Value(args, name, null);
                return int.TryParse(value, out var parsed) ? parsed : fallback;
            }

            public static string[] Csv(string[] args, string name, string fallback)
            {
                return Value(args, name, fallback)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();
            }
        }
    }
}
