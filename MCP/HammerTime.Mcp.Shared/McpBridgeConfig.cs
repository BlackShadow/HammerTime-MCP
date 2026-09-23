using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace HammerTime.Mcp.Shared
{
    /// <summary>
    /// Shared between the editor plugin (which listens) and the stdio server (which connects): the pipe name and the
    /// token every request must carry. Only the plugin and the installer create the file (<see cref="LoadOrCreate"/>);
    /// the stdio server only reads it (<see cref="TryLoad"/>), so a missing or torn file can never mint a token the
    /// running editor does not know.
    /// </summary>
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

        public static string GetDefaultPipeName()
        {
            return "hammertime-mcp-" + Environment.UserName.Replace('\\', '_');
        }

        public static McpBridgeConfig CreateDefault(string hammerTimeDirectory = null)
        {
            return new McpBridgeConfig
            {
                PipeName = GetDefaultPipeName(),
                Token = Guid.NewGuid().ToString("N"),
                HammerTimeDirectory = hammerTimeDirectory,
                SkillPath = GetDefaultSkillPath()
            };
        }

        /// <summary>The config at <paramref name="path"/>, or null when the file is missing, broken or unreadable. Never writes.</summary>
        public static McpBridgeConfig TryLoad(string path = null)
        {
            return Load(path ?? GetDefaultConfigPath(), out _);
        }

        private enum LoadFailure { None, Missing, Broken, Unreadable }

        private static McpBridgeConfig Load(string path, out LoadFailure failure)
        {
            failure = LoadFailure.None;
            try
            {
                if (!File.Exists(path))
                {
                    failure = LoadFailure.Missing;
                    return null;
                }

                var existing = JsonConvert.DeserializeObject<McpBridgeConfig>(File.ReadAllText(path));
                if (existing == null || string.IsNullOrWhiteSpace(existing.Token))
                {
                    failure = LoadFailure.Broken;
                    return null;
                }

                if (string.IsNullOrWhiteSpace(existing.PipeName)) existing.PipeName = GetDefaultPipeName();
                if (string.IsNullOrWhiteSpace(existing.SkillPath)) existing.SkillPath = GetDefaultSkillPath();
                return existing;
            }
            catch (JsonException)
            {
                failure = LoadFailure.Broken;
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                failure = LoadFailure.Unreadable;
                return null;
            }
        }

        /// <summary>
        /// Load the config, creating (and saving) a fresh one when the file is missing or broken. An unreadable
        /// file (permissions, IO error) is left alone: replacing it would mint a token the clients cannot see.
        /// </summary>
        public static McpBridgeConfig LoadOrCreate(string path = null, string hammerTimeDirectory = null)
        {
            path = path ?? GetDefaultConfigPath();
            var existing = Load(path, out var failure);
            if (existing == null)
            {
                if (failure == LoadFailure.Unreadable) throw new IOException($"The MCP bridge config at {path} exists but cannot be read.");

                var created = CreateDefault(hammerTimeDirectory);
                try
                {
                    // A broken file is replaced; a missing one is created without overwriting, so two processes
                    // starting at once cannot each mint a different token: whoever loses re-reads the file.
                    Save(path, created, overwrite: failure == LoadFailure.Broken);
                }
                catch (IOException)
                {
                    // lost the race (or the broken file was replaced by someone else): use what is on disk now
                }

                existing = TryLoad(path) ?? created;
            }

            if (!string.IsNullOrWhiteSpace(hammerTimeDirectory)) existing.HammerTimeDirectory = hammerTimeDirectory;
            return existing;
        }

        /// <summary>Write the config, replacing the file atomically so readers never see a torn file.</summary>
        public static void Save(string path, McpBridgeConfig config)
        {
            Save(path, config, overwrite: true);
        }

        private static void Save(string path, McpBridgeConfig config, bool overwrite)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(JsonConvert.SerializeObject(config, Formatting.Indented));
                }

                if (!overwrite)
                {
                    // File.Move never overwrites: it throws when the target appeared in the meantime.
                    File.Move(temp, path);
                    return;
                }

                ReplaceFile(temp, path);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }

        /// <summary>Move <paramref name="source"/> over <paramref name="destination"/>, atomically where the file system allows it.</summary>
        public static void ReplaceFile(string source, string destination)
        {
            if (File.Exists(destination))
            {
                try
                {
                    File.Replace(source, destination, null);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
                {
                    // some file systems (network shares, FAT) cannot replace in place
                    if (!File.Exists(source)) throw;
                }

                File.Delete(destination);
            }

            File.Move(source, destination);
        }
    }
}
