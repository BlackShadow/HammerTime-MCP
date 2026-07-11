using System;
using HammerTime.Mcp.Shared;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class SmokeTests
    {
        [Theory]
        [InlineData("box", "Block")]
        [InlineData("barrel", "Cylinder")]
        [InlineData("donut", "Torus")]
        [InlineData("Wedge", "Wedge")]
        public void BrushCatalog_ResolvesAliases(string input, string expected)
        {
            Assert.Equal(expected, BrushCatalog.ResolveTypeName(input));
        }

        [Fact]
        public void RegularPolygonPerimeter_MatchesClosedForm()
        {
            // hexagon of radius 10: perimeter = 6 * 10 = 60 (side == radius for n=6)
            Assert.Equal(60.0, TextureProjectionMath.RegularPolygonPerimeter(6, 10), 6);
        }

        [Fact]
        public void WrapScale_DividesPerimeterByTextureWidthTimesRepeats()
        {
            Assert.Equal(2.0, TextureProjectionMath.WrapScale(256, 64, 2), 6);
        }
    }
}
