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

        [JsonProperty("skillPath")]
        public string SkillPath { get; set; }

        [JsonProperty("skillHash")]
        public string SkillHash { get; set; }

        [JsonProperty("bridgeTimeoutMs")]
        public int? BridgeTimeoutMs { get; set; }

        public static string GetDefaultConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HammerTime.MCP",
                "config.json");
        }

        public static string GetDefaultSkillPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HammerTime.MCP",
                "skills",
                "hammertime-goldsrc-brushwork",
                "SKILL.md");
        }

        public static string GetCodexSkillPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex",
                "skills",
                "hammertime-goldsrc-brushwork",
                "SKILL.md");
        }

        public static McpBridgeConfig CreateDefault(string hammerTimeDirectory = null)
        {
            return new McpBridgeConfig
            {
                PipeName = "hammertime-mcp-" + Environment.UserName.Replace('\\', '_'),
                Token = Guid.NewGuid().ToString("N"),
                HammerTimeDirectory = hammerTimeDirectory,
                SkillPath = GetDefaultSkillPath()
            };
        }

        public static McpBridgeConfig LoadOrCreate(string path = null, string hammerTimeDirectory = null)
        {
            path = path ?? GetDefaultConfigPath();
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonConvert.DeserializeObject<McpBridgeConfig>(File.ReadAllText(path));
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.PipeName) && !string.IsNullOrWhiteSpace(existing.Token))
                    {
                        if (!string.IsNullOrWhiteSpace(hammerTimeDirectory)) existing.HammerTimeDirectory = hammerTimeDirectory;
                        if (string.IsNullOrWhiteSpace(existing.SkillPath)) existing.SkillPath = GetDefaultSkillPath();
                        return existing;
                    }
                }
                catch (JsonException)
                {
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
