using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Shared
{
    public sealed class BridgeResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Result { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public BridgeError Error { get; set; }

        public static BridgeResponse Success(string id, object result)
        {
            return new BridgeResponse
            {
                Id = id,
                Ok = true,
                Result = result == null ? null : JToken.FromObject(result)
            };
        }

        public static BridgeResponse Fail(string id, string code, string message)
        {
            return new BridgeResponse
            {
                Id = id,
                Ok = false,
                Error = new BridgeError { Code = code, Message = message }
            };
        }
    }

    public sealed class BridgeError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
