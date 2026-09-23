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

        // Candidate editor process names checked by the installer.
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
                        return Installer.Install(rest);
                    case "uninstall":
                        return Installer.Uninstall(rest);
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
            await PrintStatusOrDoctor(args, BridgeMethods.Status);
        }

        private static async Task PrintDoctor(string[] args)
        {
            await PrintStatusOrDoctor(args, BridgeMethods.Doctor);
        }

        // status/doctor answer locally (install state, pipe, skill) when the editor cannot be reached.
        private static async Task PrintStatusOrDoctor(string[] args, string method)
        {
            var configPath = Args.Value(args, "--config", null) ?? McpBridgeConfig.GetDefaultConfigPath();
            var config = TryLoadBridgeConfig(configPath);
            if (config == null)
            {
                Console.WriteLine(McpStdioServer.LocalStatus(null, configPath, NoConfigReason(configPath)).ToString(Formatting.Indented));
                return;
            }

            BridgeResponse response;
            try
            {
                response = await BridgePipeClient.FromConfig(config, Args.Value(args, "--timeout-ms", 0)).Send(method, new JObject());
            }
            catch (BridgeUnavailableException ex)
            {
                Console.WriteLine(McpStdioServer.LocalStatus(config, configPath, ex.Message).ToString(Formatting.Indented));
                return;
            }

            Console.WriteLine(response.Ok
                ? response.Result.ToString(Formatting.Indented)
                : $"{response.Error.Code}: {response.Error.Message}");
        }

        /// <summary>
        /// Read the bridge config without ever creating or rewriting it (the editor plugin and the installer
        /// own the file). Returns null when it is missing, unreadable, or lacks a token.
        /// </summary>
        private static McpBridgeConfig TryLoadBridgeConfig(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? null : McpBridgeConfig.TryLoad(path);
        }

        private static string NoConfigReason(string configPath)
        {
            return $"No usable HammerTime MCP bridge config at {configPath}. Start HammerTime with the MCP plugin installed (it writes the file) or run 'hammertime-mcp install', then retry.";
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
            Console.WriteLine("                         [--plugin-only | --clients-only] [--allow-running]");
            Console.WriteLine("  hammertime-mcp uninstall [--clients ...] [--scope project|user] [--project-dir <dir>] [--remove-skill]");
            Console.WriteLine("      Removes only the hammertime entry from each client config; --remove-skill also deletes the installed skill file and its Codex mirror.");
            Console.WriteLine("  hammertime-mcp list-clients [--scope project|user] [--project-dir <dir>]");
            Console.WriteLine("  hammertime-mcp config");
            Console.WriteLine("  hammertime-mcp status");
            Console.WriteLine("  hammertime-mcp doctor");
            Console.WriteLine("  hammertime-mcp call <bridge.method> '{\"key\":\"value\"}'");
        }

        /// <summary>
        /// JSON-RPC 2.0 over newline-delimited stdio. Tool calls are forwarded to the editor plugin over the
        /// named pipe and run concurrently, so a slow capture or compile never blocks ping or another call.
        /// While the editor is not running, map tools fail with editor_unavailable and the status, doctor and
        /// skill tools answer locally; the server keeps running until the client closes stdin.
        /// </summary>
        private sealed class McpStdioServer
        {
            private const string SkillResourceUri = "hammertime://skill/goldsrc-brushwork";
            private const string EmbeddedSkillResourceName = "HammerTime.Mcp.Cli.SKILL.md";
            private static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
            private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

            private const string Instructions =
                "HammerTime (GoldSrc map editor) tools. Call hammertime_skill first for the mapping workflow and rules. " +
                "Map tools need HammerTime running with the MCP plugin loaded; until it is, they return editor_unavailable " +
                "and hammertime_status/hammertime_doctor report the local install state instead. Document-scoped tools act on " +
                "the active document unless documentId, path or documentIndex selects another open one (documents_list shows them). " +
                "viewport_capture reads the editor's on-screen viewports, which show the active document; pass camera inline " +
                "to aim them in the same call.";

            private readonly TextReader _input;
            private readonly TextWriter _output;
            private readonly List<ToolDefinition> _tools = ToolDefinition.CreateAll();
            private readonly BridgeConfigCache _configCache = new BridgeConfigCache();
            private readonly System.Threading.SemaphoreSlim _writeLock = new System.Threading.SemaphoreSlim(1, 1);
            private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _inFlight = new System.Collections.Concurrent.ConcurrentDictionary<Task, byte>();

            /// <summary>Serves the process' stdin/stdout.</summary>
            public McpStdioServer()
            {
                // MCP stdio framing is UTF-8 whatever the console code page is, and a BOM would corrupt the
                // first message; wrapping the raw streams (instead of setting Console encodings) also works
                // when no console is attached.
                var utf8 = new UTF8Encoding(false);
                _input = new StreamReader(Console.OpenStandardInput(), utf8, false);
                _output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            }

            /// <summary>Serve until stdin closes, then let in-flight calls finish writing.</summary>
            public async Task Run()
            {
                string line;
                while ((line = await _input.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    line = line.TrimStart('\uFEFF');

                    JToken message;
                    try
                    {
                        message = JToken.Parse(line);
                    }
                    catch (Exception ex)
                    {
                        await Write(JsonRpcError(null, -32700, ex.Message)).ConfigureAwait(false);
                        continue;
                    }

                    if (message is JArray batch)
                    {
                        if (batch.Count == 0)
                        {
                            await Write(JsonRpcError(null, -32600, "Invalid Request: empty batch")).ConfigureAwait(false);
                            continue;
                        }
                        // A batch is answered with one array once every request in it is done, so it runs
                        // off the read loop like a tool call.
                        Track(Task.Run(() => HandleBatch(batch)));
                        continue;
                    }

                    if (!(message is JObject request))
                    {
                        await Write(JsonRpcError(null, -32600, "Invalid Request: expected a JSON-RPC object or batch array")).ConfigureAwait(false);
                        continue;
                    }

                    if (IsToolCallRequest(request))
                    {
                        // Tool calls may take a long time (captures, compiles): run them on the pool so ping,
                        // tools/list and other calls are answered meanwhile. Responses are written as they finish.
                        Track(Task.Run(async () =>
                        {
                            var response = await Process(request).ConfigureAwait(false);
                            if (response != null) await Write(response).ConfigureAwait(false);
                        }));
                        continue;
                    }

                    var reply = await Process(request).ConfigureAwait(false);
                    if (reply != null) await Write(reply).ConfigureAwait(false);
                }

                // stdin closed: give the in-flight calls a moment to finish writing, then exit.
                var pending = _inFlight.Keys.ToArray();
                if (pending.Length > 0)
                {
                    try
                    {
                        await Task.WhenAll(pending).WaitAsync(ShutdownGrace).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // A late call failed or did not finish in time; the client is gone anyway.
                    }
                }
            }

            private void Track(Task task)
            {
                _inFlight[task] = 0;
                _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }

            private static bool IsToolCallRequest(JObject request)
            {
                var id = request["id"];
                return id != null && id.Type != JTokenType.Null &&
                       string.Equals(request.Value<string>("method"), "tools/call", StringComparison.Ordinal);
            }

            private async Task HandleBatch(JArray batch)
            {
                var replies = await Task.WhenAll(batch.Select(item => item is JObject request
                    ? Process(request)
                    : Task.FromResult(JsonRpcError(null, -32600, "Invalid Request: batch entries must be objects")))).ConfigureAwait(false);
                // Notifications produce no entry; a batch of only notifications gets no response at all.
                var responses = new JArray(replies.Where(x => x != null));
                if (responses.Count > 0) await Write(responses).ConfigureAwait(false);
            }

            /// <summary>Handle one JSON-RPC message; returns the response, or null when none is due (notifications).</summary>
            private async Task<JObject> Process(JObject request)
            {
                var id = request["id"];
                var hasId = id != null && id.Type != JTokenType.Null;
                // Messages without an id are notifications: they never get a response, and nothing but the
                // notifications/* methods is acted on (a tools/call sent as a notification is not run).
                if (!hasId) return null;

                var method = request.Value<string>("method");
                if (string.IsNullOrWhiteSpace(method)) return JsonRpcError(id, -32600, "Invalid Request: missing method");

                var rawParams = request["params"];
                if (rawParams != null && rawParams.Type != JTokenType.Null && !(rawParams is JObject))
                {
                    return JsonRpcError(id, -32602, "Invalid params: MCP requests carry params as an object.");
                }

                try
                {
                    var response = await Handle(id, method, rawParams as JObject ?? new JObject()).ConfigureAwait(false);
                    // A notification method sent with an id (a client mistake) gets an empty result so that
                    // client does not wait forever.
                    return response ?? JsonRpcResult(id, new { });
                }
                catch (Exception ex)
                {
                    return JsonRpcError(id, -32000, ex.Message);
                }
            }

            private async Task<JObject> Handle(JToken id, string method, JObject parameters)
            {
                switch (method)
                {
                    case "initialize":
                        var requestedVersion = parameters.Value<string>("protocolVersion");
                        var negotiatedVersion = SupportedProtocolVersions.Contains(requestedVersion) ? requestedVersion : DefaultProtocolVersion;
                        return JsonRpcResult(id, new
                        {
                            protocolVersion = negotiatedVersion,
                            capabilities = new { tools = new { listChanged = false }, resources = new { subscribe = false, listChanged = false } },
                            serverInfo = new { name = ServerName, version = ServerVersion },
                            instructions = Instructions
                        });
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return null;
                    case "ping":
                        return JsonRpcResult(id, new { });
                    case "tools/list":
                        return JsonRpcResult(id, new { tools = _tools.Select(x => x.ToMcpTool()).ToList() });
                    case "tools/call":
                        return await ToolsCall(id, parameters).ConfigureAwait(false);
                    case "resources/list":
                        return ResourcesList(id);
                    case "resources/read":
                        return ResourcesRead(id, parameters);
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

                var configPath = McpBridgeConfig.GetDefaultConfigPath();
                var config = _configCache.Load(configPath);
                if (string.Equals(tool.BridgeMethod, BridgeMethods.SkillGet, StringComparison.Ordinal))
                {
                    return JsonRpcResult(id, SkillResult(ReadLocalSkill(config)));
                }

                if (config == null)
                {
                    var reason = NoConfigReason(configPath);
                    return IsLocalStatusTool(tool)
                        ? JsonRpcResult(id, McpContentFormatter.CreateToolResult(LocalStatus(null, configPath, reason)))
                        : JsonRpcResult(id, ToolError(ErrorCodes.EditorUnavailable, reason));
                }

                BridgeResponse response;
                try
                {
                    response = await BridgePipeClient.FromConfig(config).Send(tool.BridgeMethod, tool.WithDefaults(args)).ConfigureAwait(false);
                }
                catch (BridgeUnavailableException ex)
                {
                    return IsLocalStatusTool(tool)
                        ? JsonRpcResult(id, McpContentFormatter.CreateToolResult(LocalStatus(config, configPath, ex.Message)))
                        : JsonRpcResult(id, ToolError(ErrorCodes.EditorUnavailable, ex.Message));
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

            private static bool IsLocalStatusTool(ToolDefinition tool)
            {
                return string.Equals(tool.BridgeMethod, BridgeMethods.Status, StringComparison.Ordinal) ||
                       string.Equals(tool.BridgeMethod, BridgeMethods.Doctor, StringComparison.Ordinal);
            }

            /// <summary>The skill as the agent should read it: the markdown itself, with the metadata alongside.</summary>
            private static JObject SkillResult(JObject skill)
            {
                return new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = skill.Value<string>("text") ?? "" }),
                    ["structuredContent"] = new JObject
                    {
                        ["installed"] = skill["installed"],
                        ["path"] = skill["path"],
                        ["hash"] = skill["hash"]
                    }
                };
            }

            /// <summary>What status/doctor report when the editor cannot be reached (<paramref name="config"/> may be null).</summary>
            public static JObject LocalStatus(McpBridgeConfig config, string configPath, string reason)
            {
                var skill = ReadLocalSkill(config);
                var pipeName = config?.PipeName;
                return new JObject
                {
                    ["ok"] = false,
                    ["serverReachable"] = false,
                    ["reason"] = reason,
                    ["pipeName"] = pipeName,
                    ["pipeExists"] = string.IsNullOrWhiteSpace(pipeName) ? (bool?)null : BridgePipeClient.PipeListed(pipeName),
                    ["configPath"] = configPath,
                    ["configExists"] = File.Exists(configPath),
                    ["configUsable"] = config != null,
                    ["hammerTimeDirectory"] = config?.HammerTimeDirectory,
                    ["serverExecutable"] = Environment.ProcessPath,
                    ["serverVersion"] = ServerVersion,
                    ["skillPath"] = skill["path"],
                    ["skillInstalled"] = skill["installed"],
                    ["hint"] = "Start HammerTime with the MCP plugin installed; the bridge pipe exists only while the editor runs."
                };
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
                            description = "HammerTime MCP mapping rules and visual-verification workflow.",
                            mimeType = "text/markdown"
                        }
                    }
                });
            }

            private JObject ResourcesRead(JToken id, JObject parameters)
            {
                var uri = parameters.Value<string>("uri");
                if (!string.Equals(uri, SkillResourceUri, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonRpcError(id, -32602, $"Unknown resource '{uri}'.");
                }

                var skill = ReadLocalSkill(_configCache.Load(McpBridgeConfig.GetDefaultConfigPath()));
                return JsonRpcResult(id, new
                {
                    contents = new[]
                    {
                        new
                        {
                            uri = SkillResourceUri,
                            mimeType = "text/markdown",
                            text = skill.Value<string>("text") ?? ""
                        }
                    }
                });
            }

            /// <summary>
            /// The skill to serve: the installed file (config skill path, then the default install path, then a
            /// SKILL.md next to or above the executable), else the copy embedded in this build. An installed
            /// file is always preferred, so local edits to it are respected.
            /// </summary>
            private static JObject ReadLocalSkill(McpBridgeConfig config)
            {
                var path = string.IsNullOrWhiteSpace(config?.SkillPath) ? McpBridgeConfig.GetDefaultSkillPath() : config.SkillPath;
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

                if (File.Exists(path))
                {
                    try
                    {
                        return new JObject
                        {
                            ["installed"] = true,
                            ["path"] = path,
                            ["hash"] = ComputeFileSha256(path),
                            ["text"] = File.ReadAllText(path)
                        };
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // Unreadable (locked or no access): fall back to the embedded copy below.
                    }
                }

                var embedded = ReadEmbeddedSkill();
                return new JObject
                {
                    ["installed"] = false,
                    ["path"] = embedded == null ? path : "embedded:" + EmbeddedSkillResourceName,
                    ["hash"] = embedded == null ? null : ComputeSha256(Encoding.UTF8.GetBytes(embedded)),
                    ["text"] = embedded ?? ""
                };
            }

            private static string ReadEmbeddedSkill()
            {
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream(EmbeddedSkillResourceName))
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }

            private static string ComputeFileSha256(string path)
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }
            }

            private static string ComputeSha256(byte[] bytes)
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
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

            private async Task Write(JToken response)
            {
                // Responses of concurrent calls are written whole, one per line.
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await _output.WriteLineAsync(response.ToString(Formatting.None)).ConfigureAwait(false);
                    await _output.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                {
                    // The client went away (closed pipe); nothing left to tell.
                }
                finally
                {
                    _writeLock.Release();
                }
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

            /// <summary>Reads the bridge config once and again whenever the file changes; never creates or writes it.</summary>
            private sealed class BridgeConfigCache
            {
                private readonly object _sync = new object();
                private McpBridgeConfig _config;
                private DateTime _lastWriteUtc;
                private long _length;

                public McpBridgeConfig Load(string path)
                {
                    lock (_sync)
                    {
                        var info = new FileInfo(path);
                        if (!info.Exists)
                        {
                            _config = null;
                            return null;
                        }
                        if (_config == null || info.LastWriteTimeUtc != _lastWriteUtc || info.Length != _length)
                        {
                            _config = TryLoadBridgeConfig(path);
                            _lastWriteUtc = info.LastWriteTimeUtc;
                            _length = info.Length;
                        }
                        return _config;
                    }
                }
            }
        }

        /// <summary>The editor is not reachable: no bridge pipe, connection refused, or connect timeout.</summary>
        private sealed class BridgeUnavailableException : Exception
        {
            public BridgeUnavailableException(string message, Exception inner = null) : base(message, inner)
            {
            }
        }

        private sealed class BridgePipeClient
        {
            // Connecting to a running editor is fast; a stalled response can take
            // much longer (large captures, slow edits), so the two are timed apart.
            private const int ConnectTimeoutMs = 5000;
            // When the pipe is not listed at all the editor is almost certainly not running; the short wait
            // only covers the instant between two server instances of a running plugin.
            private const int MissingPipeConnectTimeoutMs = 500;
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

            /// <param name="ioTimeoutMs">Response timeout; 0 uses the config's bridgeTimeoutMs or the default.</param>
            public static BridgePipeClient FromConfig(McpBridgeConfig config, int ioTimeoutMs = 0)
            {
                return new BridgePipeClient(config, ConnectTimeoutMs, ioTimeoutMs > 0 ? ioTimeoutMs : ResolveIoTimeout(config));
            }

            public static BridgePipeClient FromArgs(string[] args)
            {
                var path = Args.Value(args, "--config", null) ?? McpBridgeConfig.GetDefaultConfigPath();
                var config = TryLoadBridgeConfig(path) ?? throw new InvalidOperationException(NoConfigReason(path));
                return FromConfig(config, Args.Value(args, "--timeout-ms", 0));
            }

            private static int ResolveIoTimeout(McpBridgeConfig config)
            {
                return config.BridgeTimeoutMs.HasValue && config.BridgeTimeoutMs.Value > 0
                    ? config.BridgeTimeoutMs.Value
                    : DefaultIoTimeoutMs;
            }

            /// <summary>
            /// Whether the pipe is currently listed under \\.\pipe\ (null when that cannot be told). Listing the
            /// pipe namespace does not open the pipe, so unlike File.Exists it never consumes a server instance.
            /// </summary>
            public static bool? PipeListed(string pipeName)
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFiles(@"\\.\pipe\"))
                    {
                        if (string.Equals(Path.GetFileName(entry), pipeName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    return false;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            public async Task<BridgeResponse> Send(string method, JObject parameters)
            {
                var listed = PipeListed(_config.PipeName);
                var connectTimeoutMs = listed == false ? Math.Min(_connectTimeoutMs, MissingPipeConnectTimeoutMs) : _connectTimeoutMs;

                // CurrentUserOnly: only talk to a pipe server owned by this user, never to an impostor pipe.
                using (var pipe = new NamedPipeClientStream(".", _config.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                {
                    try
                    {
                        await pipe.ConnectAsync(connectTimeoutMs).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException || ex is IOException || ex is UnauthorizedAccessException)
                    {
                        if (listed == false)
                        {
                            throw new BridgeUnavailableException($"HammerTime is not running or the MCP plugin is not loaded (no bridge pipe '{_config.PipeName}'). Start HammerTime and retry.", ex);
                        }
                        if (ex is UnauthorizedAccessException)
                        {
                            throw new BridgeUnavailableException($"The HammerTime MCP bridge pipe '{_config.PipeName}' is not owned by the current user; refusing to connect.", ex);
                        }
                        throw new BridgeUnavailableException($"Could not connect to the HammerTime MCP bridge pipe '{_config.PipeName}' within {connectTimeoutMs} ms: {ex.Message} Is HammerTime running?", ex);
                    }

                    var request = new BridgeRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Method = method,
                        Token = _config.Token,
                        Params = parameters ?? new JObject()
                    };

                    using (var cancellation = new System.Threading.CancellationTokenSource(_ioTimeoutMs))
                    {
                        try
                        {
                            await WriteLine(pipe, BridgeJson.SerializeRequest(request), cancellation.Token).ConfigureAwait(false);
                            var line = await ReadLine(pipe, cancellation.Token).ConfigureAwait(false);
                            if (line == null) throw new IOException($"HammerTime MCP bridge closed the pipe without a response to '{method}'.");
                            return BridgeJson.DeserializeResponse(line);
                        }
                        catch (OperationCanceledException)
                        {
                            throw new TimeoutException($"Timed out after {_ioTimeoutMs} ms waiting for the HammerTime MCP bridge response to '{method}'. Raise bridgeTimeoutMs in the bridge config for long operations.");
                        }
                    }
                }
            }

            private static async Task WriteLine(Stream stream, string line, System.Threading.CancellationToken cancellationToken)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            private static async Task<string> ReadLine(Stream stream, System.Threading.CancellationToken cancellationToken)
            {
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[ReadBufferSize];
                    while (true)
                    {
                        var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
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

        /// <summary>
        /// One MCP tool as served by tools/list: the catalog entry (<see cref="McpToolCatalog"/>, which owns the
        /// tool list and descriptions) plus its input schema from <see cref="SchemaForCatalogTool"/>. Every schema
        /// declares exactly the parameters the plugin bridge reads; a catalog tool without one throws at startup.
        /// </summary>
        private sealed class ToolDefinition
        {
            public string Name { get; set; }
            public string BridgeMethod { get; set; }
            public string Description { get; set; }
            public JObject InputSchema { get; set; }
            public JObject DefaultArguments { get; set; }

            // Brush presets: the tool name fixes the brush type; explicit arguments still win.
            private static readonly Dictionary<string, string> PresetTypes = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["brush_create_arch"] = "Arch",
                ["brush_create_block"] = "Block",
                ["brush_create_tetrahedron"] = "Tetrahedron",
                ["brush_create_pyramid"] = "Pyramid",
                ["brush_create_wedge"] = "Wedge",
                ["brush_create_cylinder"] = "Cylinder",
                ["brush_create_barrel"] = "Cylinder",
                ["brush_create_cone"] = "Cone",
                ["brush_create_pipe"] = "Pipe",
                ["brush_create_sphere"] = "Sphere",
                ["brush_create_torus"] = "Torus",
                ["brush_create_text"] = "Text"
            };

            private static readonly string[] ViewFilters = { "all", "3d", "2d", "top", "front", "side", "focused" };

            public object ToMcpTool()
            {
                return new { name = Name, description = Description, inputSchema = InputSchema };
            }

            public static List<ToolDefinition> CreateAll()
            {
                return McpToolCatalog.CreateAll()
                    .Select(entry => new ToolDefinition
                    {
                        Name = entry.Name,
                        BridgeMethod = entry.BridgeMethod,
                        Description = entry.Description,
                        InputSchema = SchemaForCatalogTool(entry.Name),
                        DefaultArguments = PresetTypes.TryGetValue(entry.Name, out var type) ? new JObject { ["type"] = type } : null
                    })
                    .ToList();
            }

            private static JObject SchemaForCatalogTool(string name)
            {
                switch (name)
                {
                    // Status and skill
                    case "hammertime_status":
                    case "hammertime_doctor":
                    case "hammertime_skill":
                        return ParameterlessSchema();

                    // Documents
                    case "documents_list":
                        return ParameterlessSchema();
                    case "documents_new":
                        return Schema(("loaderHint", "string", "Optional HammerTime loader type name."));
                    case "documents_open":
                        return WithRequired(Schema(
                            ("path", "string", "Map file path to open."),
                            ("loaderHint", "string", "Optional HammerTime loader type name.")), "path");
                    case "documents_open_text":
                        return WithRequired(Schema(
                            ("text", "string", "Full .map file text."),
                            ("name", "string", "Display name for the new document."),
                            ("loaderHint", "string", "Optional HammerTime loader type name.")), "text");
                    case "documents_activate":
                        return Schema(
                            ("documentId", "string", "Stable document id from documents_list."),
                            ("path", "string", "Open document path or name."),
                            ("documentIndex", "integer", "Open document index."));
                    case "documents_save":
                        return Schema(
                            ("path", "string", "Save-as destination path. When it matches an open document's file name that document is saved in place; otherwise it is the destination and the target is documentId/documentIndex or the active document (so untitled documents can be saved). Omit to save in place."),
                            ("documentId", "string", "Target open document id when path is a new destination. Uses the active document when omitted."),
                            ("documentIndex", "integer", "Target open document index when path is a new destination. Uses the active document when omitted."),
                            ("loaderHint", "string", "Optional loader type name."));
                    case "documents_export":
                        return WithRequired(Schema(
                            ("path", "string", "Export destination path. When it matches an open document's file name that document is exported; otherwise it is the destination and the target is documentId/documentIndex or the active document (so untitled documents can be exported)."),
                            ("documentId", "string", "Target open document id when path is a new destination. Uses the active document when omitted."),
                            ("documentIndex", "integer", "Target open document index when path is a new destination. Uses the active document when omitted."),
                            ("loaderHint", "string", "Optional loader type name.")), "path");
                    case "documents_close":
                        return Schema(
                            ("documentId", "string", "Open document id to close. Uses the active document when omitted."),
                            ("path", "string", "Open document path or name to close. Uses the active document when omitted."),
                            ("documentIndex", "integer", "Open document index to close. Uses the active document when omitted."),
                            ("force", "boolean", "Close even when the document has unsaved changes, discarding them. Defaults to false."));

                    // Map queries and validation
                    case "map_snapshot":
                        return DocumentTarget(Schema(("maxObjects", "integer", "Maximum objects to return.")));
                    case "map_search":
                        return DocumentTarget(Schema(
                            ("type", "string", "Object type, such as Entity or Solid."),
                            ("classname", "string", "Entity classname."),
                            ("key", "string", "Entity property key."),
                            ("value", "string", "Entity property value."),
                            ("text", "string", "Text to search in classnames and properties."),
                            ("selectedOnly", "boolean", "Only search selected objects. Defaults to false."),
                            ("max", "integer", "Maximum results. Defaults to 100.")));
                    case "map_validate":
                        return DocumentTarget(Schema(("selectedOnly", "boolean", "Only validate the current selection instead of the whole map. Defaults to false.")));
                    case "map_fix_all_safe":
                        return DocumentTarget(Schema());
                    case "problems_check":
                        return DocumentTarget(Schema(("selectedOnly", "boolean", "Only check currently selected objects. Defaults to false.")));
                    case "problems_fix":
                        return DocumentTarget(WithRequired(Schema(
                            ("checker", "string", "Checker full type/name."),
                            ("index", "integer", "Problem index as reported by problems_check (run with the same selectedOnly). Defaults to 0."),
                            ("selectedOnly", "boolean", "Number the problems within the current selection, as problems_check {selectedOnly:true} does. Defaults to false.")), "checker"));
                    case "map_design_audit":
                        return DocumentTarget(DesignAuditSchema());
                    case "texture_audit":
                        return DocumentTarget(WithEnum(FaceTargetSchema("Object IDs to audit. When omitted, the selected faces are audited if any are selected, else the whole map.",
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
                            "scaleReference", "median", "one"));

                    // Selection
                    case "selection_get":
                        return DocumentTarget(Schema());
                    case "selection_set":
                        return DocumentTarget(WithRequired(WithEnum(Schema(
                            ("ids", "array", "Object IDs."),
                            ("mode", "string", "How ids affect the selection. Defaults to replace.")), "mode", "replace", "add", "remove"), "ids"));
                    case "selection_filter":
                        return DocumentTarget(Schema(
                            ("type", "string", "Object type filter, such as Solid or Entity."),
                            ("classname", "string", "Entity classname filter."),
                            ("texture", "string", "Keep objects that use this texture on any face."),
                            ("min", "object", "Filter box minimum corner. Requires max."),
                            ("max", "object", "Filter box maximum corner. Requires min.")));
                    case "selection_grow":
                        return DocumentTarget(WithEnum(Schema(
                            ("mode", "string", "How to grow the selection. Defaults to children.")), "mode", "parents", "children", "siblings"));
                    case "selection_by_bounds":
                        return DocumentTarget(WithRequired(WithEnum(Schema(
                            ("min", "object", "Selection box minimum corner."),
                            ("max", "object", "Selection box maximum corner."),
                            ("mode", "string", "intersects selects objects whose bounds overlap the box; inside selects only objects fully contained. Defaults to intersects.")),
                            "mode", "intersects", "inside"), "min", "max"));

                    // Viewports and captures. They act on the editor's on-screen viewports, which always show
                    // the active document, so they take no document selector.
                    case "viewport_capture":
                        return ViewportCaptureSchema();
                    case "viewport_focus":
                        return WithEnum(Schema(
                            ("ids", "array", "Object IDs to frame. Uses point or current selection when omitted."),
                            ("point", "object", "World point to focus on."),
                            ("views", "string", "Which viewports to focus. Defaults to all.")), "views", "all", "2d", "3d");
                    case "viewport_camera_get":
                        return WithEnum(Schema(("views", "string", "Which viewports to report. Defaults to all.")), "views", ViewFilters);
                    case "viewport_camera_set":
                        return WithEnum(Schema(
                            ("views", "string", "Which viewports to modify. Inferred from the provided parameters when omitted."),
                            ("position", "object", "3D camera position Vector {x,y,z}."),
                            ("lookAt", "object", "3D world point the camera should look at (Vector {x,y,z}). Mutually exclusive with direction and anglesDegrees."),
                            ("direction", "object", "3D camera forward direction Vector {x,y,z}. Mutually exclusive with lookAt and anglesDegrees."),
                            ("anglesDegrees", "object", "3D camera Euler angles in degrees {x,y,z}. Mutually exclusive with lookAt and direction."),
                            ("fov", "number", "3D field of view in degrees (clamped 10-170)."),
                            ("center", "object", "2D camera center Vector {x,y,z}."),
                            ("zoom", "number", "2D camera zoom (clamped 0.001-256).")), "views", ViewFilters);
                    case "viewport_clear_marks":
                        return DocumentTarget(Schema(
                            ("clearSelection", "boolean", "Deselect selected map objects. Defaults to true."),
                            ("clearOverlay", "boolean", "Clear MCP overlay highlights and leak path. Defaults to true.")));
                    case "overlay_set":
                        return DocumentTarget(WithRequired(Schema(
                            ("ids", "array", "Object IDs."),
                            ("label", "string", "Overlay label.")), "ids"));
                    case "overlay_clear":
                        return DocumentTarget(Schema());
                    case "leaks_load_pointfile":
                        // path is the pointfile here, so only documentId/documentIndex select the document.
                        return Schema(
                            ("path", "string", "Pointfile path (.lin or .pts). Provide path or text."),
                            ("text", "string", "Pointfile text. Provide path or text."),
                            ("documentId", "string", "Target open document id. Uses the active document when omitted."),
                            ("documentIndex", "integer", "Target open document index. Uses the active document when omitted."));

                    // Editor tools
                    case "editor_tools_list":
                    case "vertex_subtools_list":
                        return ParameterlessSchema();
                    case "editor_tool_activate":
                        return WithRequired(Schema(("name", "string", "Tool name or alias.")), "name");
                    case "vertex_subtool_activate":
                        return WithRequired(Schema(
                            ("name", "string", "Vertex subtool name or alias."),
                            ("activateVertexTool", "boolean", "Activate Vertex Manipulation Tool first. Defaults to true.")), "name");

                    // Entities
                    case "entity_create":
                        return DocumentTarget(Schema(
                            ("classname", "string", "Entity classname."),
                            ("origin", "object", "Vector {x,y,z}. Defaults to the world origin."),
                            ("properties", "object", "Entity keyvalues."),
                            ("spawnflags", "integer", "Spawn flags."),
                            ("select", "boolean", "Select after creation. Defaults to false.")));
                    case "entity_update":
                        return DocumentTarget(WithRequired(Schema(
                            ("id", "integer", "Entity object ID."),
                            ("classname", "string", "New classname."),
                            ("origin", "object", "Vector {x,y,z}."),
                            ("properties", "object", "Keyvalues, null values remove keys."),
                            ("spawnflags", "integer", "Spawn flags.")), "id"));
                    case "entity_tie_brushes":
                        return DocumentTarget(Schema(
                            ("ids", "array", "Solid brush object IDs. Uses selection when omitted."),
                            ("classname", "string", "Brush entity classname, such as trigger_once or func_wall. Required unless targetEntityId is given."),
                            ("properties", "object", "Entity keyvalues, such as target or targetname."),
                            ("spawnflags", "integer", "Spawn flags."),
                            ("origin", "object", "Origin for the new brush entity (Vector {x,y,z}). Optional."),
                            ("targetEntityId", "integer", "Existing entity ID to receive the brushes."),
                            ("select", "boolean", "Select the entity after tying. Defaults to true.")));
                    case "entity_untie_brushes":
                        return DocumentTarget(Schema(
                            ("ids", "array", "Brush entity IDs. Uses selection when omitted."),
                            ("deleteEmptyEntity", "boolean", "Delete the entity after moving children to world. Defaults to true."),
                            ("select", "boolean", "Select moved brushes after untying. Defaults to true.")));
                    case "scripted_sequence_list":
                        return DocumentTarget(Schema(("target", "string", "Optional related target name.")));
                    case "scripted_sequence_upsert":
                        return DocumentTarget(Schema(
                            ("id", "integer", "Existing entity ID."),
                            ("targetname", "string", "Sequence targetname."),
                            ("origin", "object", "Vector {x,y,z}."),
                            ("properties", "object", "Additional keyvalues."),
                            ("m_iszEntity", "string", "Target NPC."),
                            ("m_iszPlay", "string", "Animation to play."),
                            ("m_iszIdle", "string", "Idle animation."),
                            ("m_fMoveTo", "string", "Move-to mode."),
                            ("m_flRadius", "string", "Search radius."),
                            ("target", "string", "Entity to trigger when the sequence completes."),
                            ("killtarget", "string", "Entity to remove when the sequence completes."),
                            ("spawnflags", "integer", "Spawn flags.")));
                    case "fgd_entities_list":
                        return DocumentTarget(Schema(
                            ("type", "string", "Entity class type filter, such as PointClass or SolidClass."),
                            ("query", "string", "Case-insensitive substring to match against entity classnames.")));
                    case "entity_schema":
                        return DocumentTarget(WithRequired(Schema(("classname", "string", "Entity classname to look up in the active FGD.")), "classname"));
                    case "entity_create_from_schema":
                        return DocumentTarget(WithRequired(Schema(
                            ("classname", "string", "Entity classname to create using FGD defaults."),
                            ("origin", "object", "Placement origin. Defaults to the world origin."),
                            ("properties", "object", "Keyvalues that override FGD defaults."),
                            ("spawnflags", "integer", "Spawn flags."),
                            ("select", "boolean", "Select after creation. Defaults to false.")), "classname"));

                    // Brushes
                    case "brush_types_list":
                        return ParameterlessSchema();
                    case "brush_create":
                        // No enum on type: the bridge also accepts aliases (BrushCatalog), which an enum would reject.
                        return DocumentTarget(WithRequired(BrushSchema(
                            ("type", "string", "Brush type or alias: Arch, Block, Tetrahedron, Pyramid, Wedge, Cylinder, Cone, Pipe, Sphere, Torus, Text (aliases such as box, cube, barrel, can, tube, ramp, ball, donut are accepted). Defaults to Block.")), "min", "max"));
                    case "brush_create_box":
                    case "brush_create_arch":
                    case "brush_create_block":
                    case "brush_create_tetrahedron":
                    case "brush_create_pyramid":
                    case "brush_create_wedge":
                    case "brush_create_cylinder":
                    case "brush_create_barrel":
                    case "brush_create_cone":
                    case "brush_create_pipe":
                    case "brush_create_sphere":
                    case "brush_create_torus":
                    case "brush_create_text":
                        return DocumentTarget(WithRequired(BrushSchema(), "min", "max"));
                    case "brush_create_from_planes":
                        return DocumentTarget(WithRequired(Schema(
                            ("planes", "array", "At least four plane definitions, each with points or normal/point plus optional texture."),
                            ("texture", "string", "Default texture name for faces without one."),
                            ("select", "boolean", "Select the created brush. Defaults to true.")), "planes"));

                    // Vertex editing
                    case "vertex_snapshot":
                        return DocumentTarget(FaceTargetSchema("Solid object IDs. Uses the selection, else every solid, when omitted."));
                    case "vertex_move":
                        return DocumentTarget(Schema(
                            ("ids", "array", "Solid object IDs to search for the vertices."),
                            ("vertexKeys", "array", "Vertex snapshot keys returned by vertex_snapshot."),
                            ("vertexRefs", "array", "Explicit vertex references with objectId, faceId, and vertexIndex."),
                            ("delta", "object", "Relative movement vector {x,y,z}."),
                            ("position", "object", "Absolute destination vector {x,y,z}.")));
                    case "vertex_split_face":
                        return DocumentTarget(WithRequired(Schema(
                            ("objectId", "integer", "Object ID. With faceId selects the face; uses faceRefs or the first selected face when omitted."),
                            ("faceId", "integer", "Face ID on objectId."),
                            ("faceRefs", "array", "Explicit face reference with objectId and faceId; the first one is split."),
                            ("vertexIndexA", "integer", "First vertex index. Must be non-adjacent to vertexIndexB."),
                            ("vertexIndexB", "integer", "Second vertex index. Must be non-adjacent to vertexIndexA.")), "vertexIndexA", "vertexIndexB"));
                    case "vertex_triangulate":
                        return DocumentTarget(FaceTargetSchema("Solid object IDs. Uses the selected faces when omitted. The triangles are coplanar, so the brush is invalid until their vertices are moved out of the plane with vertex_move."));
                    case "vertex_face_edit":
                        return DocumentTarget(WithEnum(FaceTargetSchema("Solid object IDs. Uses the selected faces when omitted.",
                            ("action", "string", "Face edit action. poke fans the face into triangles from its center pushed out along the normal; triangulate splits it into coplanar triangles (the brush is invalid until the new vertices are moved). Defaults to poke."),
                            ("distance", "number", "poke only: how far the center vertex is pushed out along the face normal. Defaults to 8.")),
                            "action", "poke", "triangulate"));

                    // Textures
                    case "textures_list":
                        return DocumentTarget(Schema(
                            ("max", "integer", "Maximum textures to return. Defaults to 500."),
                            ("detailed", "boolean", "Return per-texture metadata (dimensions, wad, flags, family) instead of plain names. Defaults to false.")));
                    case "texture_search":
                        return DocumentTarget(Schema(
                            ("query", "string", "Texture search text."),
                            ("text", "string", "Alias for query."),
                            ("max", "integer", "Maximum results. Defaults to 100."),
                            ("groupFrames", "boolean", "Group animation/frame variants under one logical entry by basename. Defaults to true."),
                            ("includeSpecial", "boolean", "Include tool and sky textures. Defaults to true.")));
                    case "texture_preview_sheet":
                    case "texture_browser_capture":
                        return DocumentTarget(PreviewSheetSchema());
                    case "texture_apply":
                        return DocumentTarget(WithRequired(FaceTargetSchema("Object IDs. Uses the selection when omitted.",
                            ("texture", "string", "Texture name to apply."),
                            ("textureScale", "number", "Texture scale for both axes. Keeps the faces' scale when omitted."),
                            ("align", "boolean", "Realign the texture axes to each face's normal. Defaults to true; pass false to keep the existing alignment.")), "texture"));
                    case "texture_replace":
                        // find/from and replace/to are aliases, so neither name can be required by the schema.
                        return DocumentTarget(Schema(
                            ("find", "string", "Texture name to replace (alias: from). One of find/from is required."),
                            ("from", "string", "Alias for find."),
                            ("replace", "string", "Replacement texture name (alias: to). One of replace/to is required."),
                            ("to", "string", "Alias for replace."),
                            ("selectedOnly", "boolean", "Limit replacement to the current selection instead of the whole map."),
                            ("ids", "array", "Optional object IDs to limit replacement."),
                            ("align", "boolean", "Realign replaced faces to their normal. Defaults to false so existing alignment is preserved.")));
                    case "texture_align_face":
                        return DocumentTarget(WithEnum(WithEnum(FaceTargetSchema(
                            ("mode", "string", "face (alias normal) aligns to the face plane; world fixes axes to the world axes; reset also zeroes shift and rotation. Defaults to normal."),
                            ("rotation", "number", "Optional absolute texture rotation in degrees applied after alignment."),
                            ("justify", "string", "Optional justify within the face after alignment. Defaults to none.")),
                            "mode", "world", "face", "normal", "reset"), "justify", "left", "right", "top", "bottom", "center", "fit", "none"));
                    case "texture_copy_from_face":
                        return DocumentTarget(FaceTargetSchema("Target object IDs. Uses the selected faces when omitted.",
                            ("sourceFace", "object", "Source face reference with objectId and faceId."),
                            ("projected", "boolean", "Project the source alignment across the shared edge (default true); false copies the raw texture axes verbatim.")));
                    case "texture_project":
                        return DocumentTarget(WithEnum(WithEnum(FaceTargetSchema(
                            ("mode", "string", "Projection mode. Defaults to planar."),
                            ("texture", "string", "Texture name to apply before projection. When omitted each face keeps its texture."),
                            ("scale", "number", "Texture scale. Omit for the cylindrical auto-wrap scale (faceted perimeter / (textureWidth * labels))."),
                            ("direction", "object", "Planar projection direction vector. Defaults to +Z (0,0,1)."),
                            ("align", "string", "Planar alignment. Defaults to natural."),
                            ("axis", "object", "Cylindrical axis vector. Defaults to +Z."),
                            ("origin", "object", "Cylindrical origin vector. Defaults to the solid's center (reported as originUsed)."),
                            ("labels", "integer", "Number of horizontal texture repeats around the cylinder. Defaults to 1."),
                            ("centerLabel", "boolean", "Center one repeated label/panel on each cylindrical wrap. Defaults to true."),
                            ("sides", "integer", "Faceted-cylinder side count for seamless polygon wrapping. Inferred from the targeted side faces when omitted."),
                            ("numberOfSides", "integer", "Alias for sides.")),
                            "mode", "planar", "cylindrical", "fit", "center"), "align", "natural", "center", "fit", "left", "right", "top", "bottom"));
                    case "texture_apply_smart":
                        return DocumentTarget(WithEnum(FaceTargetSchema("Object IDs whose faces are textured. Uses the selected objects or faces when omitted; with neither the call is refused.",
                            ("classify", "string", "nearest always assigns each face its best-matching role (default); strict only assigns faces whose best role dot exceeds 0.9 and reports the rest in skippedFaces."),
                            ("front", "string", "Texture applied to faces whose normal points along frontDirection."),
                            ("back", "string", "Texture applied to faces opposite frontDirection."),
                            ("left", "string", "Texture applied to the left faces relative to frontDirection."),
                            ("right", "string", "Texture applied to the right faces relative to frontDirection."),
                            ("top", "string", "Texture applied to upward-facing (+Z) faces."),
                            ("bottom", "string", "Texture applied to downward-facing (-Z) faces."),
                            ("frontDirection", "object", "Front-facing direction vector. Defaults to -Y (0,-1,0)."),
                            ("scale", "number", "Uniform texture scale applied to every assigned face."),
                            ("fit", "boolean", "Fit each texture once across its face. Defaults to false."),
                            ("center", "boolean", "Center each texture on its face. Defaults to false.")),
                            "classify", "nearest", "strict"));

                    // Faces
                    case "face_list":
                        return DocumentTarget(FaceTargetSchema("Object IDs whose faces are listed (alias: objectIds). Uses the selection, else every solid, when omitted.",
                            ("max", "integer", "Maximum faces to return. Defaults to 500.")));
                    case "face_select":
                        return DocumentTarget(WithEnum(FaceTargetSchema(
                            ("mode", "string", "How the faces affect the face selection. Defaults to replace.")), "mode", "replace", "add", "remove"));
                    case "face_texture_set":
                        return DocumentTarget(WithEnum(FaceTargetSchema(
                            ("texture", "string", "Texture name."),
                            ("name", "string", "Alias for texture name."),
                            ("xScale", "number", "Texture X scale."),
                            ("yScale", "number", "Texture Y scale."),
                            ("xShift", "number", "Texture X shift."),
                            ("yShift", "number", "Texture Y shift."),
                            ("rotation", "number", "Texture rotation in degrees."),
                            ("rotationMode", "string", "absolute rotates the axes to the given angle (default); store writes the raw rotation field."),
                            ("uAxis", "object", "Texture U axis vector {x,y,z}."),
                            ("vAxis", "object", "Texture V axis vector {x,y,z}.")), "rotationMode", "absolute", "store"));
                    case "face_delete":
                        return DocumentTarget(FaceTargetSchema());

                    // Map text
                    case "object_export_maptext":
                        return DocumentTarget(Schema(
                            ("id", "integer", "Object ID to export. Falls back to ids[0]."),
                            ("ids", "array", "Object IDs; the first is exported when id is omitted.")));
                    case "object_import_maptext":
                        return DocumentTarget(WithRequired(Schema(
                            ("text", "string", "Hammer .map brush text for a single solid."),
                            ("select", "boolean", "Select the imported object. Defaults to true.")), "text"));
                    case "object_import_maptext_batch":
                        return DocumentTarget(Schema(
                            ("texts", "array", "Array of Hammer .map brush text blocks. Provide texts or text."),
                            ("text", "string", "A single string containing multiple brush blocks. Provide texts or text."),
                            ("select", "boolean", "Select imported brushes after creation. Defaults to true.")));

                    // Clipping
                    case "clip_preview":
                    case "clip_apply":
                    case "clip_split":
                        return DocumentTarget(ClipSchema());

                    // Objects
                    case "objects_delete":
                        return DocumentTarget(WithRequired(Schema(("ids", "array", "Object IDs.")), "ids"));
                    case "objects_transform":
                        return DocumentTarget(WithRequired(Schema(
                            ("ids", "array", "Object IDs."),
                            ("translation", "object", "Vector {x,y,z}."),
                            ("rotationDegrees", "object", "Euler degrees {x,y,z}."),
                            ("scale", "object", "Scale vector {x,y,z}; every component must be non-zero."),
                            ("pivot", "object", "Point the scale and rotation are applied about. Defaults to the center of the objects' bounds.")), "ids"));

                    // Prefabs
                    case "prefabs_list":
                        return Schema(("directory", "string", "Optional prefab directory. Defaults to the bundled prefabs folder."));
                    case "prefab_create":
                        return DocumentTarget(WithRequired(Schema(
                            ("library", "string", "Prefab library name or .ol path."),
                            ("index", "integer", "Prefab index within the library. Resolved from name when omitted."),
                            ("name", "string", "Prefab name; used when index is omitted."),
                            ("origin", "object", "Placement origin. Defaults to the world origin.")), "library"));

                    // Compile
                    case "compile_profiles_list":
                        return ParameterlessSchema();
                    case "compile_run":
                        return DocumentTarget(WithEnum(Schema(
                            ("profile", "string", "Built-in compile profile. Defaults to full."),
                            ("steps", "array", "Optional compile step names to restrict the run (such as CSG, BSP, VIS, RAD)."),
                            ("arguments", "object", "Compile argument overrides keyed by tool name."),
                            ("useCordonBounds", "boolean", "Compile with current cordon bounds."),
                            ("workingDirectory", "string", "Working directory for compile tools."),
                            ("runGame", "boolean", "Launch the game after a successful compile. Defaults to false."),
                            ("allowUserInterruption", "boolean", "Let the user cancel the compile from the editor. Defaults to false."),
                            ("askRunGame", "boolean", "Ask the user whether to launch the game after a successful compile. Defaults to false.")),
                            "profile", "fast", "full", "custom"));
                    case "compile_log_tail":
                        return Schema(
                            ("runId", "string", "Compile run ID. Uses the most recent run when omitted."),
                            ("count", "integer", "Maximum log lines to return. Defaults to 100."));

                    // History
                    case "undo":
                    case "redo":
                        return DocumentTarget(Schema());
                    case "history_list":
                        return DocumentTarget(Schema(("max", "integer", "Maximum entries per list (the most recent ones). Returns everything when omitted.")));

                    // Cordon
                    case "cordon_get":
                        return DocumentTarget(Schema());
                    case "cordon_set":
                        return DocumentTarget(WithRequired(Schema(
                            ("min", "object", "Cordon minimum corner."),
                            ("max", "object", "Cordon maximum corner."),
                            ("enabled", "boolean", "Enable cordon after setting bounds. Keeps the current state when omitted.")), "min", "max"));
                    case "cordon_enable":
                        return DocumentTarget(WithRequired(Schema(
                            ("enabled", "boolean", "Enable or disable cordon rendering/export."),
                            ("min", "object", "Optional cordon minimum corner to set at the same time."),
                            ("max", "object", "Optional cordon maximum corner to set at the same time.")), "enabled"));

                    default:
                        throw new InvalidOperationException($"No input schema defined for catalog tool '{name}'. Add a case in SchemaForCatalogTool.");
                }
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

            private static JObject ViewportCaptureSchema()
            {
                var schema = Schema(
                    ("views", "string", "Which viewports to capture. Defaults to all."),
                    ("method", "string", "Capture method. auto tries GPU readback, then PrintWindow, then screen. Defaults to auto. gpu captures omit ImGui overlay highlights (entity names, gizmos, MCP highlights)."),
                    ("includeOverlays", "boolean", "Prefer a screen capture that includes overlay highlights/gizmos. Defaults to false."),
                    ("format", "string", "Output image format. Defaults to png."),
                    ("jpegQuality", "integer", "JPEG quality 1-100 when format is jpeg. Defaults to 85."),
                    ("maxWidth", "integer", "Maximum output image width. Defaults to 1024; 0 means native full size."),
                    ("maxHeight", "integer", "Maximum output image height. Defaults to 1024; 0 means native full size."),
                    ("waitForFrameMs", "integer", "Milliseconds to wait for a fresh rendered frame before capture. Defaults to 250."),
                    ("renderMode", "string", "Temporarily switch render mode before capture: textured or wireframe. flat is not supported."),
                    ("restoreRenderMode", "boolean", "Restore the previous render mode after capture. Defaults to true."));
                ((JObject)schema["properties"])["camera"] = CameraSchema(
                    "Optional inline camera pose applied to the selected viewports BEFORE the capture (avoids the freelook race between separate camera_set and capture calls). " +
                    "Same fields as viewport_camera_set: 3D position/lookAt/direction/anglesDegrees/fov (at most one orientation field) apply to perspective viewports; 2D center/zoom apply to orthographic viewports.");
                WithEnum(schema, "views", ViewFilters);
                WithEnum(schema, "method", "auto", "gpu", "printwindow", "screen");
                WithEnum(schema, "format", "png", "jpeg");
                WithEnum(schema, "renderMode", "textured", "wireframe");
                return schema;
            }

            private static JObject CameraSchema(string description)
            {
                return new JObject
                {
                    ["type"] = "object",
                    ["description"] = description,
                    ["properties"] = new JObject
                    {
                        ["position"] = VectorSchema("3D camera position."),
                        ["lookAt"] = VectorSchema("3D world point to look at. Mutually exclusive with direction and anglesDegrees."),
                        ["direction"] = VectorSchema("3D forward direction. Mutually exclusive with lookAt and anglesDegrees."),
                        ["anglesDegrees"] = VectorSchema("3D Euler angles in degrees. Mutually exclusive with lookAt and direction."),
                        ["fov"] = new JObject { ["type"] = "number", ["description"] = "3D field of view in degrees (clamped 10-170)." },
                        ["center"] = VectorSchema("2D camera center."),
                        ["zoom"] = new JObject { ["type"] = "number", ["description"] = "2D camera zoom (clamped 0.001-256)." }
                    }
                };
            }

            private static JObject DesignAuditSchema()
            {
                var schema = Schema(
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
                schema["properties"]["checks"]["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray("off_grid", "micro_brush", "texture_monotony", "scale_conventions", "unlit", "missing_player_start", "world_extents", "wpoly_hotspots")
                };
                return schema;
            }

            private static JObject PreviewSheetSchema()
            {
                return Schema(
                    ("textures", "array", "Texture names (or objects with a name field) to render. Searches the environment when omitted."),
                    ("query", "string", "Optional texture search text used when textures is omitted."),
                    ("text", "string", "Alias for query."),
                    ("max", "integer", "Maximum textures per page (1-128). Defaults to 32."),
                    ("tileSize", "integer", "Preview tile size in pixels (32-512). Defaults to 128; use 192 or more to judge art."),
                    ("columns", "integer", "Preview sheet column count (1-12). Defaults to 4."),
                    ("offset", "integer", "Start index into the candidate list for pagination. Defaults to 0."),
                    ("page", "integer", "Zero-based page (offset = page*max) used when offset is omitted."),
                    ("showDimensions", "boolean", "Draw texture dimensions and naming-convention glyphs on each tile ({ masked, ~ liquid, * light-emitting, + animated, - random-tiling, > scrolling, ^ sky, # tool texture). Defaults to true."));
            }

            private static JObject ClipSchema()
            {
                return WithEnum(Schema(
                    ("ids", "array", "Solid object IDs. Uses the selection when omitted."),
                    ("normal", "object", "Clip plane normal vector. Provide with an optional point."),
                    ("point", "object", "Point on the clip plane. Defaults to the origin."),
                    ("point1", "object", "First point of a three-point clip plane."),
                    ("point2", "object", "Second point of a three-point clip plane."),
                    ("point3", "object", "Third point of a three-point clip plane."),
                    ("side", "string", "Which side to keep (clip_apply only; clip_split always keeps both). Defaults to front.")),
                    "side", "front", "back", "both");
            }

            private static JObject BrushSchema(params (string Name, string Type, string Description)[] extraProperties)
            {
                var properties = new List<(string Name, string Type, string Description)>
                {
                    ("min", "object", "Minimum Vector {x,y,z}."),
                    ("max", "object", "Maximum Vector {x,y,z}."),
                    ("texture", "string", "Texture name. Defaults to the document's active texture."),
                    ("textureScale", "number", "Texture scale. Defaults to the environment's default scale."),
                    ("select", "boolean", "Select after creation. Defaults to false."),
                    ("round", "boolean", "Round created vertices where the brush type supports it. Defaults to true."),
                    ("parameters", "object", "Type-specific Brush Tool control values, such as numberOfSides, wallWidth, arc, startAngle, curvedRamp, text, or fontChooser.")
                };
                properties.InsertRange(0, extraProperties);
                return Schema(properties.ToArray());
            }

            private static JObject FaceTargetSchema(params (string Name, string Type, string Description)[] extraProperties)
            {
                return FaceTargetSchema("Object IDs whose faces are targeted. Uses the selected faces when omitted.", extraProperties);
            }

            /// <summary>The face-targeting parameters every face tool shares (ids, objectId+faceId/faceIds, faceRefs).</summary>
            private static JObject FaceTargetSchema(string idsDescription, params (string Name, string Type, string Description)[] extraProperties)
            {
                var properties = new List<(string Name, string Type, string Description)>
                {
                    ("ids", "array", idsDescription),
                    ("objectId", "integer", "Single object ID when targeting one or more faces."),
                    ("faceId", "integer", "Single face ID on objectId."),
                    ("faceIds", "array", "Face IDs on objectId. These are internal faceId values returned by face_list, not list indexes."),
                    ("faceRefs", "array", "Explicit face references with objectId and faceId.")
                };
                properties.AddRange(extraProperties);
                return Schema(properties.ToArray());
            }

            /// <summary>Document-scoped tools accept these selectors; they default to the active document.</summary>
            private static JObject DocumentTarget(JObject schema)
            {
                var properties = (JObject)schema["properties"];
                if (properties["documentId"] == null) properties["documentId"] = new JObject { ["type"] = "string", ["description"] = "Target open document id from documents_list. Uses the active document when omitted." };
                if (properties["path"] == null) properties["path"] = new JObject { ["type"] = "string", ["description"] = "Target open document path or name. Uses the active document when omitted." };
                if (properties["documentIndex"] == null) properties["documentIndex"] = new JObject { ["type"] = "integer", ["description"] = "Target open document index. Uses the active document when omitted." };
                return schema;
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

            private static JObject ArrayItemsSchema(string propertyName)
            {
                if (string.Equals(propertyName, "ids", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(propertyName, "faceIds", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "integer" };
                }

                if (string.Equals(propertyName, "textures", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(propertyName, "texts", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(propertyName, "steps", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(propertyName, "vertexKeys", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "string" };
                }

                return new JObject { ["type"] = "object" };
            }
        }

        private static class Installer
        {
            private const string TomlBegin = "# HammerTime MCP BEGIN";
            private const string TomlEnd = "# HammerTime MCP END";
            private const string TomlTable = "mcp_servers." + ServerName;

            /// <summary>Returns the exit code: non-zero when any client config could not be written (the others still are).</summary>
            public static int Install(string[] args)
            {
                var pluginOnly = Args.Has(args, "--plugin-only");
                var clientsOnly = Args.Has(args, "--clients-only");
                if (pluginOnly && clientsOnly) throw new InvalidOperationException("--plugin-only and --clients-only cannot be used together.");

                var scope = ResolveScope(args);
                var clients = ResolveClients(args);
                var hammerTimeDir = ResolveHammerTimeDirectory(Args.Value(args, "--hammertime-dir", null));
                var server = ResolveServerCommand();
                var projectDir = ResolveProjectDirectory(Args.Value(args, "--project-dir", null));

                var config = McpBridgeConfig.LoadOrCreate(null, hammerTimeDir);
                config.HammerTimeDirectory = hammerTimeDir;
                var skillInstall = InstallSkill(server.ProgramPath, !pluginOnly && clients.Contains("codex", StringComparer.OrdinalIgnoreCase));
                config.SkillPath = skillInstall.AppDataPath ?? McpBridgeConfig.GetDefaultSkillPath();
                config.SkillHash = skillInstall.Hash;
                McpBridgeConfig.Save(McpBridgeConfig.GetDefaultConfigPath(), config);

                var pluginFiles = new List<string>();
                if (!clientsOnly)
                {
                    VerifyInstallSafe(hammerTimeDir, Args.Has(args, "--allow-running"));
                    pluginFiles = CopyPluginFiles(hammerTimeDir);
                }

                // One client failing (a hand-edited file, a locked file) must not stop the others.
                var updated = new List<string>();
                var failures = new List<(string Client, string Error)>();
                if (!pluginOnly)
                {
                    foreach (var client in clients)
                    {
                        try
                        {
                            var file = InstallClientConfig(client, scope, server, projectDir);
                            // vscode and vscode-insiders (and generic and claude-code) share project files: report each once
                            if (!updated.Contains(file, StringComparer.OrdinalIgnoreCase)) updated.Add(file);
                        }
                        catch (Exception ex)
                        {
                            failures.Add((client, ex.Message));
                        }
                    }
                }

                var status = pluginOnly ? "HammerTime MCP plugin installed" : clientsOnly ? "HammerTime MCP client configs installed" : "HammerTime MCP installed";
                Console.WriteLine(failures.Count == 0 ? status + "." : status + " with errors.");
                Console.WriteLine($"HammerTime directory: {hammerTimeDir}");
                Console.WriteLine($"Bridge config: {McpBridgeConfig.GetDefaultConfigPath()}");
                Console.WriteLine($"Pipe: {config.PipeName}");
                Console.WriteLine($"Server command: {server.Command} {string.Join(" ", server.Args)}");
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
                foreach (var failure in failures) Console.Error.WriteLine($"Client {failure.Client} not updated: {failure.Error}");
                if (failures.Count > 0)
                {
                    Console.Error.WriteLine($"{failures.Count} of {clients.Count} client(s) failed. `hammertime-mcp config` prints the entry to add by hand.");
                }
                return failures.Count == 0 ? 0 : 1;
            }

            /// <summary>Remove only the hammertime entry from each client's config; everything else in the files is left alone.</summary>
            public static int Uninstall(string[] args)
            {
                var scope = ResolveScope(args);
                var clients = ResolveClients(args);
                var projectDir = ResolveProjectDirectory(Args.Value(args, "--project-dir", null));
                var failed = 0;
                foreach (var client in clients)
                {
                    try
                    {
                        var path = ClientPath(client, scope, projectDir);
                        var removed = UninstallClientConfig(client, path);
                        Console.WriteLine(removed ? $"Client config updated: {path}" : $"Nothing to remove for {client} ({path}).");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.Error.WriteLine($"Client {client} not updated: {ex.Message}");
                    }
                }

                if (Args.Has(args, "--remove-skill"))
                {
                    var removedSkills = 0;
                    foreach (var path in new[] { McpBridgeConfig.GetDefaultSkillPath(), McpBridgeConfig.GetCodexSkillPath() })
                    {
                        if (!File.Exists(path)) continue;
                        File.Delete(path);
                        removedSkills++;
                        Console.WriteLine($"Skill file removed: {path}");
                    }
                    if (removedSkills == 0) Console.WriteLine("No skill file to remove.");
                }

                Console.WriteLine("The plugin, bridge config and token stay in place; rerun install to register the clients again.");
                return failed == 0 ? 0 : 1;
            }

            public static void PrintConfig(string[] args)
            {
                var server = ResolveServerCommand();
                var root = new JObject
                {
                    ["mcpServers"] = new JObject { [ServerName] = McpServerJson(server) }
                };
                Console.WriteLine(root.ToString(Formatting.Indented));
            }

            public static void ListClients(string[] args)
            {
                var scope = ResolveScope(args);
                var projectDir = ResolveProjectDirectory(Args.Value(args, "--project-dir", null));
                foreach (var candidate in ClientCandidates(scope, projectDir))
                {
                    Console.WriteLine($"{candidate.Name}: {candidate.Path}");
                }
            }

            private static string ResolveScope(string[] args)
            {
                var scope = Args.Value(args, "--scope", "user").Trim().ToLowerInvariant();
                if (scope != "user" && scope != "project") throw new InvalidOperationException($"--scope must be user or project (got '{scope}').");
                return scope;
            }

            private static List<string> ResolveClients(string[] args)
            {
                var clients = Args.Csv(args, "--clients", "generic").ToList();
                if (clients.Any(x => string.Equals(x, "all", StringComparison.OrdinalIgnoreCase)))
                {
                    clients = AllClientIds().ToList();
                }
                clients = clients.Select(NormalizeClientId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // Reject typos before anything is written.
                var unknown = clients.Where(x => !AllClientIds().Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidOperationException($"Unknown MCP client(s): {string.Join(", ", unknown)}. Known clients: {string.Join(", ", AllClientIds())}, all.");
                }
                return clients;
            }

            /// <summary>
            /// The command clients must run: this executable with "serve". When started through the dotnet muxer
            /// (<c>dotnet hammertime-mcp.dll install</c>) the process path is dotnet itself, which needs the entry
            /// assembly as its first argument (registering <c>dotnet serve</c> would start nothing).
            /// </summary>
            private static ServerCommand ResolveServerCommand()
            {
                var assemblyPath = typeof(Program).Assembly.Location;
                var processPath = Environment.ProcessPath;
                var command = string.IsNullOrWhiteSpace(processPath) ? assemblyPath : processPath;
                if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Cannot determine the hammertime-mcp executable to register.");
                command = Path.GetFullPath(command);

                if (string.Equals(Path.GetFileNameWithoutExtension(command), "dotnet", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(assemblyPath))
                {
                    var assembly = Path.GetFullPath(assemblyPath);
                    return new ServerCommand(command, new[] { assembly, "serve" }, assembly);
                }
                return new ServerCommand(command, new[] { "serve" }, command);
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

            /// <summary>Upsert the server entry for one client and return the file written.</summary>
            private static string InstallClientConfig(string client, string scope, ServerCommand server, string projectDir)
            {
                var path = ClientPath(client, scope, projectDir);
                switch (client)
                {
                    case "codex":
                        UpsertTomlServer(path, server);
                        break;
                    case "opencode":
                        UpsertOpenCodeServer(path, server);
                        break;
                    case "vscode":
                    case "vscode-insiders":
                        UpsertJsonServer(path, "servers", server, stdioType: true);
                        break;
                    case "claude-code":
                        UpsertJsonServer(path, "mcpServers", server, stdioType: true);
                        break;
                    default:
                        UpsertJsonServer(path, "mcpServers", server, stdioType: false);
                        break;
                }
                return path;
            }

            /// <summary>Remove the server entry from one client's config. Returns whether anything was removed.</summary>
            private static bool UninstallClientConfig(string client, string path)
            {
                if (!File.Exists(path)) return false;
                switch (client)
                {
                    case "codex":
                        return RemoveTomlServer(path);
                    case "opencode":
                        return RemoveJsonServer(path, "mcp");
                    case "vscode":
                    case "vscode-insiders":
                        return RemoveJsonServer(path, "servers");
                    default:
                        return RemoveJsonServer(path, "mcpServers");
                }
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
                    // Claude Code keeps user-scope servers in ~/.claude.json ("claude mcp add --scope user"); .mcp.json is project scope only
                    case "claude-code": return Path.Combine(home, ".claude.json");
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

            private static void UpsertJsonServer(string path, string rootProperty, ServerCommand server, bool stdioType)
            {
                UpdateJsonFile(path, root =>
                {
                    var servers = root[rootProperty] as JObject ?? new JObject();
                    var entry = McpServerJson(server);
                    if (stdioType) entry.AddFirst(new JProperty("type", "stdio"));
                    servers[ServerName] = entry;
                    root[rootProperty] = servers;
                    return true;
                });
            }

            private static void UpsertOpenCodeServer(string path, ServerCommand server)
            {
                UpdateJsonFile(path, root =>
                {
                    var servers = root["mcp"] as JObject ?? new JObject();
                    var command = new JArray(server.Command);
                    foreach (var arg in server.Args) command.Add(arg);
                    servers[ServerName] = new JObject
                    {
                        ["type"] = "local",
                        ["command"] = command,
                        ["enabled"] = true
                    };
                    root["mcp"] = servers;
                    return true;
                });
            }

            private static bool RemoveJsonServer(string path, string rootProperty)
            {
                return UpdateJsonFile(path, root =>
                {
                    if (!(root[rootProperty] is JObject servers) || servers[ServerName] == null) return false;
                    servers.Remove(ServerName);
                    return true;
                });
            }

            private static JObject McpServerJson(ServerCommand server)
            {
                return new JObject
                {
                    ["command"] = server.Command,
                    ["args"] = new JArray(server.Args.Select(x => (JToken)x))
                };
            }

            /// <summary>
            /// Read-modify-write a client's JSON config. Client files are other programs' state (~/.claude.json is
            /// rewritten by Claude Code all the time), so the file is re-read right before it is replaced and the
            /// change is redone when it moved underneath us. Returns false when <paramref name="mutate"/> changed nothing.
            /// </summary>
            private static bool UpdateJsonFile(string path, Func<JObject, bool> mutate)
            {
                for (var attempt = 0; ; attempt++)
                {
                    var original = File.Exists(path) ? File.ReadAllText(path) : null;
                    var root = ParseClientJson(path, original);
                    if (!mutate(root)) return false;
                    var text = root.ToString(Formatting.Indented) + Environment.NewLine;

                    var current = File.Exists(path) ? File.ReadAllText(path) : null;
                    if (!string.Equals(original, current, StringComparison.Ordinal) && attempt < 3) continue;
                    WriteTextAtomic(path, text);
                    return true;
                }
            }

            /// <summary>
            /// Parse a client's config as-is: no date or float reinterpretation (Json.NET would otherwise rewrite ISO
            /// timestamps and decimals in the user's file). A file with comments (JSONC, common for VS Code) is refused:
            /// rewriting it would silently drop them.
            /// </summary>
            private static JObject ParseClientJson(string path, string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return new JObject();
                if (ContainsJsonComments(text))
                {
                    throw new InvalidOperationException($"{path} contains comments, which rewriting it would drop; it was left unchanged. Add the hammertime entry by hand (`hammertime-mcp config` prints it).");
                }

                try
                {
                    try
                    {
                        return ParseJson(text, FloatParseHandling.Decimal);
                    }
                    catch (JsonReaderException)
                    {
                        // a number outside the decimal range: read it as double rather than fail the install,
                        // unless even a double cannot hold it (it would be written back as "Infinity")
                        var root = ParseJson(text, FloatParseHandling.Double);
                        if (root.Descendants().OfType<JValue>().Any(v => v.Value is double d && (double.IsInfinity(d) || double.IsNaN(d))))
                        {
                            throw new InvalidOperationException($"{path} contains a number too large to rewrite faithfully; it was left unchanged. Add the hammertime entry by hand (`hammertime-mcp config` prints it).");
                        }
                        return root;
                    }
                }
                catch (JsonReaderException ex)
                {
                    throw new InvalidOperationException($"{path} is not a valid JSON object ({ex.Message}); it was left unchanged.", ex);
                }
            }

            /// <summary>True when the text carries // or /* */ comment tokens (a string containing slashes is not one).</summary>
            private static bool ContainsJsonComments(string text)
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double })
                {
                    try
                    {
                        while (reader.Read())
                        {
                            if (reader.TokenType == JsonToken.Comment) return true;
                        }
                    }
                    catch (JsonReaderException)
                    {
                        // malformed content: the real parse reports it
                    }
                }
                return false;
            }

            private static JObject ParseJson(string text, FloatParseHandling floats)
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = floats })
                {
                    var root = JObject.Load(reader);
                    // JObject.Load stops after the root object; trailing content would be lost on rewrite
                    if (reader.Read()) throw new JsonReaderException("Additional text found after the root object.");
                    return root;
                }
            }

            /// <summary>Replace the file atomically: write a temp file next to it, then swap it in.</summary>
            private static void WriteTextAtomic(string path, string text)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                try
                {
                    File.WriteAllText(temp, text, new UTF8Encoding(false));
                    McpBridgeConfig.ReplaceFile(temp, path);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
                }
            }

            private static void UpsertTomlServer(string path, ServerCommand server)
            {
                var existing = File.Exists(path) ? File.ReadAllText(path) : "";
                var newline = existing.Contains("\r\n") || existing.Length == 0 && Environment.NewLine == "\r\n" ? "\r\n" : "\n";
                var lines = SplitLines(existing);
                var regions = FindTomlServerRegions(lines, path);

                var args = string.Join(", ", server.Args.Select(x => "\"" + EscapeToml(x) + "\""));
                var block = new List<string>
                {
                    TomlBegin,
                    "[" + TomlTable + "]",
                    $"command = \"{EscapeToml(server.Command)}\"",
                    $"args = [{args}]"
                };

                if (regions.Count == 0)
                {
                    block.Add(TomlEnd);
                    var text = existing.TrimEnd();
                    WriteTextAtomic(path, (text.Length == 0 ? "" : text + newline + newline) + string.Join(newline, block) + newline);
                    return;
                }

                // An existing table (our marker block, a block whose END marker was lost, or one added by hand)
                // is replaced in place; keys and sub-tables other than command/args are kept.
                var kept = new List<string>();
                var keptSubTables = new List<string>();
                foreach (var region in regions)
                {
                    SplitTomlRegion(lines, region.Start, region.End, kept, keptSubTables);
                }
                block.AddRange(TrimBlankLines(kept));
                var sub = TrimBlankLines(keptSubTables);
                if (sub.Count > 0)
                {
                    block.Add("");
                    block.AddRange(sub);
                }
                block.Add(TomlEnd);

                var output = new List<string>();
                var index = 0;
                for (var r = 0; r < regions.Count; r++)
                {
                    output.AddRange(lines.Skip(index).Take(regions[r].Start - index));
                    if (r == 0) output.AddRange(block);
                    index = regions[r].End;
                }
                output.AddRange(lines.Skip(index));
                WriteTextAtomic(path, string.Join(newline, output).TrimEnd() + newline);
            }

            private static bool RemoveTomlServer(string path)
            {
                var existing = File.ReadAllText(path);
                var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
                var lines = SplitLines(existing);
                var regions = FindTomlServerRegions(lines, path);
                if (regions.Count == 0) return false;

                var output = new List<string>();
                var index = 0;
                foreach (var region in regions)
                {
                    output.AddRange(lines.Skip(index).Take(region.Start - index));
                    index = region.End;
                    // don't leave a double blank line where the table was
                    if (output.Count > 0 && output[output.Count - 1].Trim().Length == 0 &&
                        index < lines.Count && lines[index].Trim().Length == 0)
                    {
                        index++;
                    }
                }
                output.AddRange(lines.Skip(index));
                var text = string.Join(newline, output).Trim();
                WriteTextAtomic(path, text.Length == 0 ? "" : text + newline);
                return true;
            }

            /// <summary>
            /// Line ranges [Start, End) that define the hammertime server: complete marker blocks, a BEGIN marker
            /// without END (with the table that follows it), and [mcp_servers.hammertime] tables (plus their
            /// sub-tables) added by hand.
            /// </summary>
            private static List<(int Start, int End)> FindTomlServerRegions(List<string> lines, string path)
            {
                var headers = TomlHeaderNames(lines);
                var regions = new List<(int Start, int End)>();
                string currentTable = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    var trimmed = lines[i].Trim();
                    if (headers[i] != null)
                    {
                        currentTable = headers[i];
                    }
                    else if (trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal) &&
                             IsDottedHammerTimeKey(trimmed, currentTable))
                    {
                        throw new InvalidOperationException($"{path} defines the {ServerName} server as a dotted key or inline table, which the installer cannot update safely; it was left unchanged. Replace it with a [{TomlTable}] table or edit it by hand.");
                    }

                    if (trimmed == TomlBegin)
                    {
                        var end = -1;
                        for (var j = i + 1; j < lines.Count; j++)
                        {
                            var t = lines[j].Trim();
                            if (t == TomlEnd) { end = j; break; }
                            if (t == TomlBegin) break;
                        }

                        if (end >= 0)
                        {
                            regions.Add((i, end + 1));
                            for (var j = i; j <= end; j++) if (headers[j] != null) currentTable = headers[j];
                            i = end;
                            continue;
                        }

                        // BEGIN without END: take the marker and, when it is next, the hammertime table below it
                        var next = i + 1;
                        while (next < lines.Count && (lines[next].Trim().Length == 0 || lines[next].Trim().StartsWith("#", StringComparison.Ordinal))) next++;
                        if (next < lines.Count && IsHammerTimeTable(headers[next]))
                        {
                            var tableEnd = TomlTableEnd(lines, headers, next);
                            regions.Add((i, tableEnd));
                            currentTable = headers[next];
                            i = tableEnd - 1;
                        }
                        else
                        {
                            regions.Add((i, i + 1));
                        }
                        continue;
                    }

                    if (IsHammerTimeTable(headers[i]))
                    {
                        var tableEnd = TomlTableEnd(lines, headers, i);
                        regions.Add((i, tableEnd));
                        i = tableEnd - 1;
                    }
                }
                return regions;
            }

            /// <summary>Split a region into the lines to keep: other keys of the main table, and its sub-tables.</summary>
            private static void SplitTomlRegion(List<string> lines, int start, int end, List<string> kept, List<string> keptSubTables)
            {
                var headers = TomlHeaderNames(lines);
                var inSubTable = false;
                var arrayDepth = 0;
                for (var i = start; i < end; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();
                    if (arrayDepth > 0)
                    {
                        // continuation of a dropped multi-line command/args value
                        arrayDepth += TomlBracketDelta(line);
                        continue;
                    }
                    if (trimmed == TomlBegin || trimmed == TomlEnd) continue;
                    if (headers[i] != null)
                    {
                        if (string.Equals(headers[i], TomlTable, StringComparison.Ordinal)) { inSubTable = false; continue; }
                        inSubTable = true;
                    }
                    if (inSubTable)
                    {
                        keptSubTables.Add(line);
                        continue;
                    }
                    if (TomlKeyIs(trimmed, "command") || TomlKeyIs(trimmed, "args"))
                    {
                        arrayDepth = Math.Max(0, TomlBracketDelta(line));
                        continue;
                    }
                    kept.Add(line);
                }
            }

            /// <summary>First line after the table starting at <paramref name="header"/> and its own sub-tables (trailing blank/comment lines excluded).</summary>
            private static int TomlTableEnd(List<string> lines, string[] headers, int header)
            {
                var end = header + 1;
                while (end < lines.Count && (headers[end] == null || IsHammerTimeTable(headers[end]))) end++;
                // comments and blank lines just above the next table belong to it
                while (end > header + 1 && (lines[end - 1].Trim().Length == 0 || lines[end - 1].Trim().StartsWith("#", StringComparison.Ordinal)) &&
                       lines[end - 1].Trim() != TomlEnd)
                {
                    end--;
                }
                return end;
            }

            /// <summary>Normalised table name for each header line ([a.b] or [[a.b]]), null for every other line. Multi-line strings are skipped.</summary>
            private static string[] TomlHeaderNames(List<string> lines)
            {
                var names = new string[lines.Count];
                string openQuote = null;
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (openQuote != null)
                    {
                        if (CountOccurrences(line, openQuote) % 2 == 1) openQuote = null;
                        continue;
                    }

                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        var body = trimmed.TrimStart('[');
                        var close = body.IndexOf(']');
                        if (close > 0)
                        {
                            names[i] = new string(body.Substring(0, close).Where(ch => !char.IsWhiteSpace(ch) && ch != '"' && ch != '\'').ToArray());
                        }
                        continue;
                    }

                    foreach (var quote in new[] { "\"\"\"", "'''" })
                    {
                        if (CountOccurrences(line, quote) % 2 == 1) { openQuote = quote; break; }
                    }
                }
                return names;
            }

            private static bool IsHammerTimeTable(string name)
            {
                return name != null && (name == TomlTable || name.StartsWith(TomlTable + ".", StringComparison.Ordinal));
            }

            /// <summary>hammertime defined as a dotted key or inline table (under [mcp_servers] or at top level).</summary>
            private static bool IsDottedHammerTimeKey(string trimmed, string currentTable)
            {
                var key = trimmed.Split('=')[0];
                if (key.Length == trimmed.Length) return false;
                key = new string(key.Where(ch => !char.IsWhiteSpace(ch) && ch != '"' && ch != '\'').ToArray());
                if (currentTable == "mcp_servers") return key == ServerName || key.StartsWith(ServerName + ".", StringComparison.Ordinal);
                if (currentTable == null) return key == TomlTable || key.StartsWith(TomlTable + ".", StringComparison.Ordinal) ||
                                                  key == "mcp_servers" && trimmed.Contains(ServerName);
                return false;
            }

            private static bool TomlKeyIs(string trimmed, string key)
            {
                var equals = trimmed.IndexOf('=');
                if (equals <= 0) return false;
                var name = trimmed.Substring(0, equals).Trim().Trim('"', '\'');
                return string.Equals(name, key, StringComparison.Ordinal);
            }

            /// <summary>Net count of [ minus ] outside strings and comments.</summary>
            private static int TomlBracketDelta(string line)
            {
                var depth = 0;
                char quote = '\0';
                for (var i = 0; i < line.Length; i++)
                {
                    var ch = line[i];
                    if (quote != '\0')
                    {
                        if (ch == '\\' && quote == '"') i++;
                        else if (ch == quote) quote = '\0';
                        continue;
                    }
                    if (ch == '#') break;
                    if (ch == '"' || ch == '\'') quote = ch;
                    else if (ch == '[') depth++;
                    else if (ch == ']') depth--;
                }
                return depth;
            }

            private static int CountOccurrences(string text, string value)
            {
                var count = 0;
                for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
                return count;
            }

            private static List<string> SplitLines(string text)
            {
                if (text.Length == 0) return new List<string>();
                return text.Replace("\r\n", "\n").Split('\n').ToList();
            }

            private static List<string> TrimBlankLines(List<string> lines)
            {
                var start = 0;
                var end = lines.Count;
                while (start < end && lines[start].Trim().Length == 0) start++;
                while (end > start && lines[end - 1].Trim().Length == 0) end--;
                return lines.Skip(start).Take(end - start).ToList();
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

            private sealed class ServerCommand
            {
                public ServerCommand(string command, string[] args, string programPath)
                {
                    Command = command;
                    Args = args;
                    ProgramPath = programPath;
                }

                /// <summary>Executable the clients run (hammertime-mcp.exe, or the dotnet muxer).</summary>
                public string Command { get; }
                /// <summary>Arguments after the command ("serve", preceded by the entry assembly for the muxer).</summary>
                public string[] Args { get; }
                /// <summary>The hammertime-mcp program itself (the skill file is looked up next to it).</summary>
                public string ProgramPath { get; }
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
