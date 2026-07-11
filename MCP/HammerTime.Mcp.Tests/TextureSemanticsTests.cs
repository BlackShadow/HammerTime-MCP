using HammerTime.Mcp.Shared;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class TextureSemanticsTests
    {
        [Fact]
        public void TransparentPrefix_SetsFlagAndStripsBasename()
        {
            var info = TextureSemantics.Parse("{GRATE");
            Assert.True(info.Transparent);
            Assert.Equal("GRATE", info.Basename);
        }

        [Fact]
        public void AnimatedDigitPrefix_ParsesFrame()
        {
            var info = TextureSemantics.Parse("+0BTN");
            Assert.True(info.Animated);
            Assert.False(info.ToggleFrame);
            Assert.Equal(0, info.Frame);
            Assert.Equal("BTN", info.Basename);
        }

        [Fact]
        public void ToggleFramePrefix_ParsesToggle()
        {
            var info = TextureSemantics.Parse("+ABTN");
            Assert.True(info.Animated);
            Assert.True(info.ToggleFrame);
            Assert.Equal(0, info.Frame);
        }

        [Fact]
        public void LightEmittingPrefix_SetsFlag()
        {
            var info = TextureSemantics.Parse("~LIGHT3A");
            Assert.True(info.LightEmitting);
            Assert.Equal("LIGHT3A", info.Basename);
        }

        [Fact]
        public void LiquidPrefixAndName_SetLiquid()
        {
            var info = TextureSemantics.Parse("!WATERBLUE");
            Assert.True(info.Liquid);
        }

        [Fact]
        public void ToolTexture_IsSpecialTool()
        {
            var info = TextureSemantics.Parse("aaatrigger");
            Assert.Equal("aaatrigger", info.Tool);
            Assert.True(info.Special);
        }

        [Fact]
        public void RandomTilingPrefix_SetsFlag()
        {
            var info = TextureSemantics.Parse("-1WALL");
            Assert.True(info.RandomTiling);
            Assert.Equal(1, info.Frame);
            Assert.Equal("WALL", info.Basename);
        }

        [Fact]
        public void Family_StripsTrailingDigits()
        {
            var info = TextureSemantics.Parse("crate01");
            Assert.Equal("crate", info.Family);
        }

        [Fact]
        public void Family_KeepsBasenameWhenAllDigits()
        {
            var info = TextureSemantics.Parse("100");
            Assert.Equal("100", info.Family);
        }

        [Fact]
        public void SkyPrefix_IsSpecial()
        {
            var info = TextureSemantics.Parse("sky_day");
            Assert.True(info.Sky);
            Assert.True(info.Special);
        }
    }
}
