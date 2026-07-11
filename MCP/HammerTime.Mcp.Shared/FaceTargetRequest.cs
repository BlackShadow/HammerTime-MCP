using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Shared
{
    public sealed class FaceTargetRequest
    {
        private FaceTargetRequest(IReadOnlyList<FaceTargetRef> faceRefs, IReadOnlyList<long> objectIds)
        {
            FaceRefs = faceRefs;
            ObjectIds = objectIds;
        }

        public IReadOnlyList<FaceTargetRef> FaceRefs { get; }
        public IReadOnlyList<long> ObjectIds { get; }
        public bool HasExplicitFaceTargets => FaceRefs.Count > 0;
        public bool HasExplicitObjectTargets => ObjectIds.Count > 0;
        public bool HasExplicitTargets => HasExplicitFaceTargets || HasExplicitObjectTargets;

        public static FaceTargetRequest Parse(JObject parameters)
        {
            parameters = parameters ?? new JObject();

            var faceRefs = new List<FaceTargetRef>();
            var faceRefsToken = parameters["faceRefs"] ?? parameters["faces"];
            if (faceRefsToken != null && faceRefsToken.Type != JTokenType.Null)
            {
                if (!(faceRefsToken is JArray array))
                {
                    throw new ArgumentException("faceRefs must be an array of { objectId, faceId } objects.");
                }

                if (array.Count == 0)
                {
                    throw new ArgumentException("faceRefs must contain at least one face reference.");
                }

                foreach (var item in array.OfType<JObject>())
                {
                    faceRefs.Add(ParseFaceRef(item));
                }

                if (faceRefs.Count != array.Count)
                {
                    throw new ArgumentException("Each faceRefs entry must be an object with objectId and faceId.");
                }
            }

            var hasObjectId = HasValue(parameters, "objectId");
            var hasFaceId = HasValue(parameters, "faceId");
            var hasFaceIds = HasValue(parameters, "faceIds");
            if (hasObjectId || hasFaceId || hasFaceIds)
            {
                if (!hasObjectId)
                {
                    throw new ArgumentException("objectId is required when faceId or faceIds is supplied.");
                }

                if (!hasFaceId && !hasFaceIds)
                {
                    throw new ArgumentException("faceId or faceIds is required when objectId is supplied for face-level targeting.");
                }

                var objectId = parameters["objectId"].Value<long>();
                var faceIds = new List<long>();
                if (hasFaceId) faceIds.Add(parameters["faceId"].Value<long>());
                if (hasFaceIds) faceIds.AddRange(ReadLongs(parameters["faceIds"], "faceIds"));
                if (faceIds.Count == 0)
                {
                    throw new ArgumentException("faceIds must contain at least one face ID.");
                }

                foreach (var faceId in faceIds.Distinct())
                {
                    faceRefs.Add(new FaceTargetRef(objectId, faceId));
                }
            }

            return new FaceTargetRequest(
                DistinctFaceRefs(faceRefs),
                ReadLongs(parameters["ids"], "ids").Distinct().ToArray());
        }

        private static FaceTargetRef ParseFaceRef(JObject obj)
        {
            if (!HasValue(obj, "objectId"))
            {
                throw new ArgumentException("Face reference is missing objectId.");
            }

            if (!HasValue(obj, "faceId"))
            {
                throw new ArgumentException("Face reference is missing faceId.");
            }

            return new FaceTargetRef(obj["objectId"].Value<long>(), obj["faceId"].Value<long>());
        }

        private static IReadOnlyList<long> ReadLongs(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null) return Array.Empty<long>();
            if (token.Type == JTokenType.Array) return token.Select(x => x.Value<long>()).ToArray();
            if (token.Type == JTokenType.Integer) return new[] { token.Value<long>() };
            throw new ArgumentException($"{name} must be an integer or an array of integers.");
        }

        private static bool HasValue(JObject obj, string name)
        {
            var token = obj?[name];
            return token != null && token.Type != JTokenType.Null;
        }

        private static IReadOnlyList<FaceTargetRef> DistinctFaceRefs(IEnumerable<FaceTargetRef> refs)
        {
            return refs
                .GroupBy(x => new { x.ObjectId, x.FaceId })
                .Select(x => x.First())
                .ToArray();
        }
    }

    public sealed class FaceTargetRef
    {
        public FaceTargetRef(long objectId, long faceId)
        {
            ObjectId = objectId;
            FaceId = faceId;
        }

        public long ObjectId { get; }
        public long FaceId { get; }
    }
}
