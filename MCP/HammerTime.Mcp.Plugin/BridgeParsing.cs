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
    /// <summary>
    /// Reads request parameters. A value of the wrong shape is the caller's mistake, so every conversion
    /// failure becomes an <c>invalid_request</c> naming the parameter (never an internal error).
    /// </summary>
    internal static class BridgeParsing
    {
        public static T Optional<T>(this JObject obj, string name, T fallback = default)
        {
            if (obj == null) return fallback;
            var token = obj[name];
            return token == null || token.Type == JTokenType.Null ? fallback : Convert<T>(token, name);
        }

        public static T Required<T>(this JObject obj, string name)
        {
            if (obj == null) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Missing params object.");
            var token = obj[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Missing required parameter '{name}'.");
            }
            return Convert<T>(token, name);
        }

        public static long[] Ids(this JObject obj, string name = "ids")
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<long>();
            try
            {
                if (token.Type == JTokenType.Array) return token.Select(x => x.Value<long>()).ToArray();
                return new[] { token.Value<long>() };
            }
            catch (Exception ex) when (IsConversionError(ex))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Parameter '{name}' must be an integer object id or an array of them.");
            }
        }

        public static Dictionary<string, string> StringDictionary(this JObject obj, string name)
        {
            var token = obj?[name] as JObject;
            if (token == null) return new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            var dict = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var prop in token.Properties())
            {
                // GoldSrc keyvalues are text the engine reads with atoi/atof: booleans become 1/0, never "True"
                dict[prop.Name] = prop.Value.Type == JTokenType.Null ? null
                    : prop.Value.Type == JTokenType.Boolean ? (prop.Value.Value<bool>() ? "1" : "0")
                    : System.Convert.ToString(prop.Value, CultureInfo.InvariantCulture);
            }
            return dict;
        }

        public static Vector3? OptionalVector(this JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return ToVector(token, name);
        }

        public static Vector3 RequiredVector(this JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Missing required vector parameter '{name}'.");
            }
            return ToVector(token, name);
        }

        /// <summary>A vector given as {x,y,z} or as a three-element array.</summary>
        public static Vector3 ToVector(JToken token, string name = "vector")
        {
            try
            {
                if (token.Type == JTokenType.Array)
                {
                    var values = token.Select(x => x.Value<float>()).ToArray();
                    if (values.Length < 3) throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Parameter '{name}' must have 3 values.");
                    return new Vector3(values[0], values[1], values[2]);
                }

                var dto = token.ToObject<Vector3Dto>();
                return new Vector3(dto.X, dto.Y, dto.Z);
            }
            catch (Exception ex) when (IsConversionError(ex))
            {
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Parameter '{name}' must be a vector {{x,y,z}} or [x,y,z] of numbers.");
            }
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

        private static T Convert<T>(JToken token, string name)
        {
            try
            {
                return token.ToObject<T>();
            }
            catch (Exception ex) when (IsConversionError(ex))
            {
                var expected = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Parameter '{name}' must be a {Describe(expected)} (got {token.Type.ToString().ToLowerInvariant()}).");
            }
        }

        private static bool IsConversionError(Exception ex)
        {
            return ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is InvalidCastException ||
                   ex is ArgumentException || ex is OverflowException;
        }

        private static string Describe(Type type)
        {
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(int) || type == typeof(long)) return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
            if (type == typeof(string)) return "string";
            return type.Name;
        }
    }
}
