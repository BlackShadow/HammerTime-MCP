using System;
using Newtonsoft.Json;

namespace HammerTime.Mcp.Shared
{
    public readonly struct Vector3Dto : IEquatable<Vector3Dto>
    {
        [JsonProperty("x")]
        public float X { get; }

        [JsonProperty("y")]
        public float Y { get; }

        [JsonProperty("z")]
        public float Z { get; }

        [JsonConstructor]
        public Vector3Dto(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(Vector3Dto other)
        {
            return X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        }

        public override bool Equals(object obj)
        {
            return obj is Vector3Dto other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = X.GetHashCode();
                hashCode = (hashCode * 397) ^ Y.GetHashCode();
                hashCode = (hashCode * 397) ^ Z.GetHashCode();
                return hashCode;
            }
        }
    }

    public sealed class BoxDto
    {
        [JsonProperty("min")]
        public Vector3Dto Min { get; set; }

        [JsonProperty("max")]
        public Vector3Dto Max { get; set; }

        [JsonProperty("center")]
        public Vector3Dto Center { get; set; }
    }
}
