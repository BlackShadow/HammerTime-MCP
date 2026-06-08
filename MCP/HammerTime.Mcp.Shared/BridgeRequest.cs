using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Shared
{
    public sealed class BridgeRequest
    {
        [JsonProperty("id", Required = Required.Always)]
        public string Id { get; set; }

        [JsonProperty("method", Required = Required.Always)]
        public string Method { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }

        [JsonProperty("params")]
        public JObject Params { get; set; } = new JObject();
    }
}
