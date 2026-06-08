using System;
using System.IO;
using Newtonsoft.Json;

namespace HammerTime.Mcp.Shared
{
    public sealed class McpBridgeConfig
    {
        [JsonProperty("pipeName")]
        public string PipeName { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("hammerTimeDirectory")]
        public string HammerTimeDirectory { get; set; }

        public static string GetDefaultConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HammerTime.MCP",
                "config.json");
        }

        public static McpBridgeConfig CreateDefault(string hammerTimeDirectory = null)
        {
            return new McpBridgeConfig
            {
                PipeName = "hammertime-mcp-" + Environment.UserName.Replace('\\', '_'),
                Token = Guid.NewGuid().ToString("N"),
                HammerTimeDirectory = hammerTimeDirectory
            };
        }

        public static McpBridgeConfig LoadOrCreate(string path = null, string hammerTimeDirectory = null)
        {
            path = path ?? GetDefaultConfigPath();
            if (File.Exists(path))
            {
                var existing = JsonConvert.DeserializeObject<McpBridgeConfig>(File.ReadAllText(path));
                if (existing != null && !string.IsNullOrWhiteSpace(existing.PipeName) && !string.IsNullOrWhiteSpace(existing.Token))
                {
                    if (!string.IsNullOrWhiteSpace(hammerTimeDirectory)) existing.HammerTimeDirectory = hammerTimeDirectory;
                    return existing;
                }
            }

            var created = CreateDefault(hammerTimeDirectory);
            Save(path, created);
            return created;
        }

        public static void Save(string path, McpBridgeConfig config)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
        }
    }
}
