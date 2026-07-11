using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
            try
            {
                var request = JsonConvert.DeserializeObject<BridgeRequest>(line, Settings)
                              ?? throw new BridgeProtocolException("Request line did not contain a request object.");

                if (string.IsNullOrWhiteSpace(request.Id))
                {
                    throw new BridgeProtocolException("Request is missing required id.");
                }

                if (string.IsNullOrWhiteSpace(request.Method))
                {
                    throw new BridgeProtocolException("Request is missing required method.");
                }

                if (request.Params == null)
                {
                    request.Params = new JObject();
                }

                return request;
            }
            catch (JsonException ex)
            {
                throw new BridgeProtocolException(ex.Message);
            }
        }

        public static string SerializeResponse(BridgeResponse response)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            return JsonConvert.SerializeObject(response, Formatting.None, Settings);
        }

        public static BridgeResponse DeserializeResponse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new BridgeProtocolException("Response line is empty.");
            try
            {
                var obj = JObject.Parse(line);
                if (obj["ok"] == null || obj["ok"].Type == JTokenType.Null)
                {
                    throw new BridgeProtocolException("Response is missing required ok flag.");
                }

                var response = obj.ToObject<BridgeResponse>(JsonSerializer.Create(Settings))
                               ?? throw new BridgeProtocolException("Response line did not contain a response object.");

                if (response.Ok)
                {
                    if (obj["result"] == null)
                    {
                        throw new BridgeProtocolException("Successful response is missing result.");
                    }

                    if (response.Result == null)
                    {
                        response.Result = JValue.CreateNull();
                    }
                }
                else
                {
                    if (response.Error == null)
                    {
                        throw new BridgeProtocolException("Failed response is missing error.");
                    }

                    if (string.IsNullOrWhiteSpace(response.Error.Code))
                    {
                        throw new BridgeProtocolException("Failed response is missing error code.");
                    }

                    if (string.IsNullOrWhiteSpace(response.Error.Message))
                    {
                        throw new BridgeProtocolException("Failed response is missing error message.");
                    }
                }

                return response;
            }
            catch (JsonException ex)
            {
                throw new BridgeProtocolException(ex.Message);
            }
        }
    }

    public sealed class BridgeProtocolException : Exception
    {
        public BridgeProtocolException(string message) : base(message)
        {
        }
    }
}
