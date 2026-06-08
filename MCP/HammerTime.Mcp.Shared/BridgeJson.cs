using System;
using Newtonsoft.Json;

namespace HammerTime.Mcp.Shared
{
    public static class BridgeJson
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static string SerializeRequest(BridgeRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return JsonConvert.SerializeObject(request, Formatting.None, Settings);
        }

        public static BridgeRequest DeserializeRequest(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new BridgeProtocolException("Request line is empty.");
            return JsonConvert.DeserializeObject<BridgeRequest>(line, Settings)
                   ?? throw new BridgeProtocolException("Request line did not contain a request object.");
        }

        public static string SerializeResponse(BridgeResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return JsonConvert.SerializeObject(response, Formatting.None, Settings);
        }

        public static BridgeResponse DeserializeResponse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new BridgeProtocolException("Response line is empty.");
            return JsonConvert.DeserializeObject<BridgeResponse>(line, Settings)
                   ?? throw new BridgeProtocolException("Response line did not contain a response object.");
        }
    }

    public sealed class BridgeProtocolException : Exception
    {
        public BridgeProtocolException(string message) : base(message)
        {
        }
    }
}
