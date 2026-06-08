using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Cli
{
    internal static class Program
    {
        private const string ServerName = "hammertime";
        private const string ServerVersion = "0.1.0";
        private const string DefaultProtocolVersion = "2025-11-25";

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
            private readonly List<ToolDefinition> _tools = ToolDefinition.CreateAll();

            public async Task Run()
            {
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
                    var method = request.Value<string>("method");
                    if (string.IsNullOrWhiteSpace(method)) continue;

                    try
                    {
                        var response = await Handle(id, method, request["params"] as JObject ?? new JObject());
                        if (response != null) await Write(response);
                    }
                    catch (Exception ex)
                    {
                        if (id != null) await Write(JsonRpcError(id, -32000, ex.Message));
                    }
                }
            }

            private async Task<JObject> Handle(JToken id, string method, JObject parameters)
            {
                switch (method)
                {
                    case "initialize":
                        return JsonRpcResult(id, new
                        {
                            protocolVersion = parameters.Value<string>("protocolVersion") ?? DefaultProtocolVersion,
                            capabilities = new { tools = new { listChanged = false } },
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
                    return JsonRpcResult(id, new
                    {
                        content = new[] { new { type = "text", text = response.Result?.ToString(Formatting.Indented) ?? "null" } },
                        structuredContent = response.Result
                    });
                }

                return JsonRpcResult(id, ToolError(response.Error.Code, response.Error.Message));
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
                    ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result)
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
            private readonly McpBridgeConfig _config;
            private readonly int _timeoutMs;

            private BridgePipeClient(McpBridgeConfig config, int timeoutMs)
            {
                _config = config;
                _timeoutMs = timeoutMs;
            }

            public static BridgePipeClient FromConfig(int timeoutMs = 5000)
            {
                return new BridgePipeClient(McpBridgeConfig.LoadOrCreate(), timeoutMs);
            }

            public static BridgePipeClient FromArgs(string[] args)
            {
                var path = Args.Value(args, "--config", null);
                var timeout = Args.Value(args, "--timeout-ms", 5000);
                return new BridgePipeClient(McpBridgeConfig.LoadOrCreate(path), timeout);
            }

            public async Task<BridgeResponse> Send(string method, JObject parameters)
            {
                using (var pipe = new NamedPipeClientStream(".", _config.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    await pipe.ConnectAsync(_timeoutMs);
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
                using (var cancellation = new System.Threading.CancellationTokenSource(_timeoutMs))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
            }

            private async Task<string> ReadLine(Stream stream)
            {
                using (var cancellation = new System.Threading.CancellationTokenSource(_timeoutMs))
                using (var buffer = new MemoryStream())
                {
                    var single = new byte[1];
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(single, 0, 1, cancellation.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new TimeoutException("Timed out waiting for HammerTime MCP bridge response.");
                        }

                        if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
                        if (single[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
                        buffer.WriteByte(single[0]);
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
                    Tool("documents_list", BridgeMethods.DocumentsList, "List open HammerTime documents.", Schema()),
                    Tool("documents_open", BridgeMethods.DocumentsOpen, "Open a map document.", Schema(("path", "string", "Map file path."), ("loaderHint", "string", "Optional HammerTime loader type name.")), "path"),
                    Tool("documents_activate", BridgeMethods.DocumentsActivate, "Activate an open document by path or documentIndex.", Schema(("path", "string", "Open document path."), ("documentIndex", "integer", "Open document index."))),
                    Tool("documents_save", BridgeMethods.DocumentsSave, "Save the active or specified document.", Schema(("path", "string", "Optional output path."), ("loaderHint", "string", "Optional loader type name."))),
                    Tool("documents_export", BridgeMethods.DocumentsExport, "Export the active or specified document.", Schema(("path", "string", "Export path."), ("loaderHint", "string", "Optional loader type name.")), "path"),
                    Tool("map_snapshot", BridgeMethods.MapSnapshot, "Return a bounded summary of map objects.", Schema(("maxObjects", "integer", "Maximum objects to return."))),
                    Tool("map_search", BridgeMethods.MapSearch, "Search map objects by type, classname, key/value, selected state, or text.", Schema(("type", "string", "Object type, such as Entity or Solid."), ("classname", "string", "Entity classname."), ("key", "string", "Entity property key."), ("value", "string", "Entity property value."), ("text", "string", "Text to search in classnames and properties."), ("selectedOnly", "boolean", "Only search selected objects."), ("max", "integer", "Maximum results."))),
                    Tool("selection_get", BridgeMethods.SelectionGet, "Return selected object IDs and bounds.", Schema()),
                    Tool("selection_set", BridgeMethods.SelectionSet, "Replace, add, or remove object selection.", Schema(("ids", "array", "Object IDs."), ("mode", "string", "replace, add, or remove.")), "ids"),
                    Tool("viewport_focus", BridgeMethods.ViewportFocus, "Focus 2D/3D viewports on ids, point, or current selection.", Schema(("ids", "array", "Object IDs."), ("point", "object", "Vector {x,y,z}."), ("views", "string", "all, 2d, or 3d."))),
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
                    Tool("brush_create", BridgeMethods.BrushCreate, "Create a HammerTime Brush Tool shape. Valid types: Arch, Block, Tetrahedron, Pyramid, Wedge, Cylinder, Cone, Pipe, Sphere, Torus, Text. Aliases include barrel/barrell/can/tank for Cylinder.", BrushSchema(("type", "string", "Brush type or alias.")), "type", "min", "max"),
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
                    Tool("objects_delete", BridgeMethods.ObjectsDelete, "Delete map objects by ID.", Schema(("ids", "array", "Object IDs.")), "ids"),
                    Tool("objects_transform", BridgeMethods.ObjectsTransform, "Translate, rotate, or scale objects.", Schema(("ids", "array", "Object IDs."), ("translation", "object", "Vector {x,y,z}."), ("rotationDegrees", "object", "Euler degrees {x,y,z}."), ("scale", "object", "Scale vector {x,y,z}."), ("pivot", "object", "Transform pivot.")), "ids"),
                    Tool("problems_check", BridgeMethods.ProblemsCheck, "Run HammerTime map problem checks.", Schema()),
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
                    tools.Add(Tool(entry.Name, entry.BridgeMethod, entry.Description, Schema()));
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
                    var property = new JObject
                    {
                        ["type"] = prop.Type,
                        ["description"] = prop.Description
                    };
                    if (prop.Type == "array")
                    {
                        property["items"] = ArrayItemsSchema(prop.Name);
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

            private static JObject ArrayItemsSchema(string propertyName)
            {
                if (string.Equals(propertyName, "ids", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "integer" };
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
                var config = McpBridgeConfig.LoadOrCreate(null, hammerTimeDir);
                config.HammerTimeDirectory = hammerTimeDir;
                McpBridgeConfig.Save(McpBridgeConfig.GetDefaultConfigPath(), config);

                var pluginFiles = new List<string>();
                if (!clientsOnly)
                {
                    VerifyInstallSafe(hammerTimeDir, Args.Has(args, "--allow-running"));
                    pluginFiles = CopyPluginFiles(hammerTimeDir);
                }

                var clients = Args.Csv(args, "--clients", "generic");
                if (clients.Any(x => string.Equals(x, "all", StringComparison.OrdinalIgnoreCase)))
                {
                    clients = AllClientIds();
                }

                var scope = Args.Value(args, "--scope", "user").ToLowerInvariant();
                var updated = new List<string>();
                if (!pluginOnly)
                {
                    foreach (var client in clients.Select(NormalizeClientId).Distinct())
                    {
                        updated.AddRange(InstallClientConfig(client, scope, cliPath, projectDir));
                    }
                }

                Console.WriteLine(pluginOnly ? "HammerTime MCP plugin installed." : clientsOnly ? "HammerTime MCP client configs installed." : "HammerTime MCP installed.");
                Console.WriteLine($"HammerTime directory: {hammerTimeDir}");
                Console.WriteLine($"Bridge config: {McpBridgeConfig.GetDefaultConfigPath()}");
                Console.WriteLine($"Pipe: {config.PipeName}");
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
                    (string.Equals(x.ProcessName, "Hammertime.Editor", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.ProcessName, "HammertimeEditor", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.ProcessName, "Sledge.Editor", StringComparison.OrdinalIgnoreCase)));
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
