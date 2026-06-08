using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json.Linq;
using Sledge.DataStructures.Geometric;

namespace HammerTime.Mcp.Plugin
{
    internal static class BridgeParsing
    {
        public static T Optional<T>(this JObject obj, string name, T fallback = default)
        {
            if (obj == null) return fallback;
            var token = obj[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToObject<T>();
        }

        public static T Required<T>(this JObject obj, string name)
        {
            if (obj == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Missing params object.");
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Missing required parameter '{name}'.");
            }
            return token.ToObject<T>();
        }

        public static long[] Ids(this JObject obj, string name = "ids")
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<long>();
            if (token.Type == JTokenType.Array) return token.Select(x => x.Value<long>()).ToArray();
            return new[] { token.Value<long>() };
        }

        public static Dictionary<string, string> StringDictionary(this JObject obj, string name)
        {
            var token = obj?[name] as JObject;
            if (token == null) return new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            var dict = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var prop in token.Properties())
            {
                dict[prop.Name] = prop.Value.Type == JTokenType.Null ? null : Convert.ToString(prop.Value, CultureInfo.InvariantCulture);
            }
            return dict;
        }

        public static Vector3? OptionalVector(this JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return ToVector(token);
        }

        public static Vector3 RequiredVector(this JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Missing required vector parameter '{name}'.");
            }
            return ToVector(token);
        }

        public static Vector3 ToVector(JToken token)
        {
            if (token.Type == JTokenType.Array)
            {
                var values = token.Select(x => x.Value<float>()).ToArray();
                if (values.Length < 3) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Vector array must have 3 values.");
                return new Vector3(values[0], values[1], values[2]);
            }

            var dto = token.ToObject<Vector3Dto>();
            return new Vector3(dto.X, dto.Y, dto.Z);
        }

        public static Vector3Dto ToDto(this Vector3 vector)
        {
            return new Vector3Dto(vector.X, vector.Y, vector.Z);
        }

        public static BoxDto ToDto(this Box box)
        {
            if (box == null) return null;
            return new BoxDto
            {
                Min = box.Start.ToDto(),
                Max = box.End.ToDto(),
                Center = box.Center.ToDto()
            };
        }
    }
}
