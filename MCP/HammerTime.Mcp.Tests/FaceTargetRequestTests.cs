using System;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class FaceTargetRequestTests
    {
        [Fact]
        public void Parse_FaceRefsArray_ProducesFaceTargets()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse(
                "{ \"faceRefs\": [ { \"objectId\": 1, \"faceId\": 2 }, { \"objectId\": 1, \"faceId\": 3 } ] }"));

            Assert.True(request.HasExplicitFaceTargets);
            Assert.Equal(2, request.FaceRefs.Count);
            Assert.Equal(1, request.FaceRefs[0].ObjectId);
            Assert.Equal(2, request.FaceRefs[0].FaceId);
            Assert.Equal(3, request.FaceRefs[1].FaceId);
        }

        [Fact]
        public void Parse_FacesAlias_IsAcceptedAsFaceRefs()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse(
                "{ \"faces\": [ { \"objectId\": 7, \"faceId\": 9 } ] }"));

            Assert.Single(request.FaceRefs);
            Assert.Equal(7, request.FaceRefs[0].ObjectId);
            Assert.Equal(9, request.FaceRefs[0].FaceId);
        }

        [Fact]
        public void Parse_ObjectIdAndFaceId_ProducesSingleFaceTarget()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse("{ \"objectId\": 5, \"faceId\": 8 }"));

            Assert.Single(request.FaceRefs);
            Assert.Equal(5, request.FaceRefs[0].ObjectId);
            Assert.Equal(8, request.FaceRefs[0].FaceId);
        }

        [Fact]
        public void Parse_ObjectIdAndFaceIdsArray_ExpandsPerFace()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse("{ \"objectId\": 5, \"faceIds\": [1, 2, 3] }"));

            Assert.Equal(3, request.FaceRefs.Count);
            Assert.All(request.FaceRefs, r => Assert.Equal(5, r.ObjectId));
        }

        [Fact]
        public void Parse_DuplicateFaceRefs_AreDeduplicated()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse(
                "{ \"faceRefs\": [ { \"objectId\": 1, \"faceId\": 2 }, { \"objectId\": 1, \"faceId\": 2 } ] }"));

            Assert.Single(request.FaceRefs);
        }

        [Fact]
        public void Parse_IdsArray_PopulatesObjectIds()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse("{ \"ids\": [1, 2, 3, 2] }"));

            Assert.True(request.HasExplicitObjectTargets);
            Assert.Equal(3, request.ObjectIds.Count); // duplicate 2 removed
            Assert.Contains(1L, request.ObjectIds);
            Assert.Contains(2L, request.ObjectIds);
            Assert.Contains(3L, request.ObjectIds);
        }

        [Fact]
        public void Parse_IdsSingleInteger_IsAcceptedAsOneObjectId()
        {
            var request = FaceTargetRequest.Parse(JObject.Parse("{ \"ids\": 5 }"));

            Assert.Single(request.ObjectIds);
            Assert.Equal(5, request.ObjectIds[0]);
        }

        [Fact]
        public void Parse_NullOrEmptyParameters_HaveNoTargets()
        {
            var request = FaceTargetRequest.Parse(null);
            Assert.False(request.HasExplicitTargets);
            Assert.Empty(request.FaceRefs);
            Assert.Empty(request.ObjectIds);
        }

        [Fact]
        public void Parse_FaceRefsNotArray_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"faceRefs\": 5 }")));
        }

        [Fact]
        public void Parse_EmptyFaceRefsArray_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"faceRefs\": [] }")));
        }

        [Fact]
        public void Parse_FaceIdWithoutObjectId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"faceId\": 2 }")));
        }

        [Fact]
        public void Parse_ObjectIdWithoutFaceId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"objectId\": 5 }")));
        }

        [Fact]
        public void Parse_FaceRefMissingFaceId_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"faceRefs\": [ { \"objectId\": 1 } ] }")));
        }

        [Fact]
        public void Parse_FaceRefNonObjectEntry_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                FaceTargetRequest.Parse(JObject.Parse("{ \"faceRefs\": [ 5 ] }")));
        }
    }
}
