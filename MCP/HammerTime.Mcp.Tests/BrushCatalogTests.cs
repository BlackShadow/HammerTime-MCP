using System.Linq;
using HammerTime.Mcp.Shared;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    // NOTE: The plugin's TextureAlignment world/face math lives in HammerTime.Mcp.Plugin,
    // which references the editor binaries (Sledge/HammerTime), so it is untestable-by-design
    // from this project. The pure, portable slice of that math (perimeter, wrap scale, etc.)
    // was moved to HammerTime.Mcp.Shared.TextureProjectionMath and is covered by SmokeTests.
    public class BrushCatalogTests
    {
        [Theory]
        [InlineData("BOX", "Block")]
        [InlineData("Barrel", "Cylinder")]
        [InlineData("CYLINDER", "Cylinder")]
        [InlineData("tube", "Pipe")]
        [InlineData("doughnut", "Torus")]
        public void ResolveTypeName_IsCaseInsensitive(string input, string expected)
        {
            Assert.Equal(expected, BrushCatalog.ResolveTypeName(input));
        }

        [Theory]
        [InlineData("  box  ", "Block")]
        [InlineData("\tbarrel\t", "Cylinder")]
        public void ResolveTypeName_IgnoresSurroundingWhitespace(string input, string expected)
        {
            Assert.Equal(expected, BrushCatalog.ResolveTypeName(input));
        }

        [Fact]
        public void ResolveTypeName_UnknownInput_ReturnsTrimmedInput()
        {
            Assert.Equal("Squircle", BrushCatalog.ResolveTypeName("Squircle"));
            Assert.Equal("Foo", BrushCatalog.ResolveTypeName("  Foo  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveTypeName_NullOrEmpty_ReturnsNull(string input)
        {
            Assert.Null(BrushCatalog.ResolveTypeName(input));
        }

        [Theory]
        [InlineData("wall_width", "wallWidth")]
        [InlineData("wall width", "wallWidth")]
        [InlineData("number_of_sides", "numberOfSides")]
        [InlineData("type", "type")]
        [InlineData("TYPE", "type")]
        [InlineData("Width", "width")]
        public void NormalizeParameterName_ProducesCamelCase(string input, string expected)
        {
            Assert.Equal(expected, BrushCatalog.NormalizeParameterName(input));
        }

        [Theory]
        [InlineData("path")]
        [InlineData("type")]
        [InlineData("min")]
        [InlineData("max")]
        public void IsReservedParameter_TrueForReservedNames(string name)
        {
            Assert.True(BrushCatalog.IsReservedParameter(name));
        }

        [Theory]
        [InlineData("wallWidth")]
        [InlineData("wall width")]
        [InlineData("numberOfSides")]
        [InlineData("arc")]
        public void IsReservedParameter_FalseForBrushControlNames(string name)
        {
            Assert.False(BrushCatalog.IsReservedParameter(name));
        }

        [Fact]
        public void GetAliasesFor_Cylinder_IncludesBarrelAndTank_ButNotTube()
        {
            var aliases = BrushCatalog.GetAliasesFor("Cylinder");
            Assert.Contains("barrel", aliases);
            Assert.Contains("tank", aliases);
            Assert.DoesNotContain("cylinder", aliases); // resolved name itself is excluded
            Assert.DoesNotContain("tube", aliases);     // tube resolves to Pipe
        }

        [Fact]
        public void GetAliasesFor_Pipe_IncludesTube()
        {
            var aliases = BrushCatalog.GetAliasesFor("Pipe");
            Assert.Contains("tube", aliases);
        }

        [Fact]
        public void DefaultTypes_AreOrdered_WithArchFirst()
        {
            var types = BrushCatalog.DefaultTypes;
            Assert.Equal("Arch", types[0].Name);
            Assert.Equal(11, types.Count);
            var names = types.Select(x => x.Name).ToList();
            Assert.Equal(
                new[] { "Arch", "Block", "Tetrahedron", "Pyramid", "Wedge", "Cylinder", "Cone", "Pipe", "Sphere", "Torus", "Text" },
                names);
        }
    }
}
