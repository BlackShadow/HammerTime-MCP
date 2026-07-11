using System;
using System.IO;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class McpBridgeConfigTests
    {
        [Fact]
        public void CreateDefault_PipeName_ContainsSanitizedUserName()
        {
            var config = McpBridgeConfig.CreateDefault();

            Assert.False(string.IsNullOrWhiteSpace(config.PipeName));
            var sanitizedUser = Environment.UserName.Replace('\\', '_');
            Assert.Contains(sanitizedUser, config.PipeName);
            Assert.DoesNotContain("\\", config.PipeName);
        }

        [Fact]
        public void CreateDefault_Token_Is32HexChars()
        {
            var config = McpBridgeConfig.CreateDefault();

            Assert.NotNull(config.Token);
            Assert.Equal(32, config.Token.Length);
        }

        [Fact]
        public void CreateDefault_BridgeTimeoutMs_IsNull()
        {
            var config = McpBridgeConfig.CreateDefault();
            Assert.Null(config.BridgeTimeoutMs);
        }

        [Fact]
        public void CreateDefault_PropagatesHammerTimeDirectory()
        {
            var config = McpBridgeConfig.CreateDefault(@"C:\Games\HammertimeEditor");
            Assert.Equal(@"C:\Games\HammertimeEditor", config.HammerTimeDirectory);
        }

        [Fact]
        public void RoundTrip_PreservesBridgeTimeoutMs()
        {
            var config = McpBridgeConfig.CreateDefault();
            config.BridgeTimeoutMs = 60000;

            var json = JsonConvert.SerializeObject(config);
            var restored = JsonConvert.DeserializeObject<McpBridgeConfig>(json);

            Assert.Equal(60000, restored.BridgeTimeoutMs);
            Assert.Equal(config.PipeName, restored.PipeName);
            Assert.Equal(config.Token, restored.Token);
        }

        [Fact]
        public void Serialization_UsesBridgeTimeoutMsPropertyName()
        {
            var config = McpBridgeConfig.CreateDefault();
            config.BridgeTimeoutMs = 5000;

            var token = JObject.Parse(JsonConvert.SerializeObject(config));
            Assert.Equal(5000, token.Value<int>("bridgeTimeoutMs"));
        }

        [Fact]
        public void LoadOrCreate_WithExplicitPath_CreatesThenReloadsSameIdentity()
        {
            // Use an isolated temp path so the real %APPDATA% config is never touched.
            var path = Path.Combine(Path.GetTempPath(), "HammerTime.MCP.Tests", Guid.NewGuid().ToString("N"), "config.json");
            try
            {
                var created = McpBridgeConfig.LoadOrCreate(path);
                Assert.True(File.Exists(path));
                Assert.False(string.IsNullOrWhiteSpace(created.PipeName));
                Assert.False(string.IsNullOrWhiteSpace(created.Token));

                var reloaded = McpBridgeConfig.LoadOrCreate(path);
                Assert.Equal(created.PipeName, reloaded.PipeName);
                Assert.Equal(created.Token, reloaded.Token);
            }
            finally
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }
}
