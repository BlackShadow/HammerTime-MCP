using System.Linq;
using HammerTime.Mcp.Shared;
using Xunit;

namespace HammerTime.Mcp.Tests
{
    public class PointFileParserTests
    {
        [Fact]
        public void Parse_PtsLines_EmitsOnePointPerLine()
        {
            var points = PointFileParser.Parse("1 2 3\n4 5 6\n");

            Assert.Equal(2, points.Count);
            Assert.Equal(new Vector3Dto(1, 2, 3), points[0]);
            Assert.Equal(new Vector3Dto(4, 5, 6), points[1]);
        }

        [Fact]
        public void Parse_LinLines_EmitsTwoPointsPerLine()
        {
            var points = PointFileParser.Parse("0 0 0 10 20 30\n");

            Assert.Equal(2, points.Count);
            Assert.Equal(new Vector3Dto(0, 0, 0), points[0]);
            Assert.Equal(new Vector3Dto(10, 20, 30), points[1]);
        }

        [Fact]
        public void Parse_SkipsHeaderAndGarbageLines()
        {
            var text = "# leak trace\nheader line\n1 2 3\n// comment\ngarbage here now\n4 5 6\n";

            var points = PointFileParser.Parse(text);

            Assert.Equal(2, points.Count);
            Assert.Equal(new Vector3Dto(1, 2, 3), points[0]);
            Assert.Equal(new Vector3Dto(4, 5, 6), points[1]);
        }

        [Fact]
        public void Parse_LineWithTrailingLabel_UsesLeadingFloats()
        {
            var points = PointFileParser.Parse("7 8 9 someLabel\n");

            Assert.Single(points);
            Assert.Equal(new Vector3Dto(7, 8, 9), points[0]);
        }

        [Fact]
        public void Parse_AllGarbage_Throws()
        {
            Assert.Throws<PointFileParseException>(() => PointFileParser.Parse("header\nnot numbers\nx y z\n"));
        }

        [Fact]
        public void Parse_EmptyInput_Throws()
        {
            Assert.Throws<PointFileParseException>(() => PointFileParser.Parse("   \n\n"));
        }

        [Fact]
        public void Parse_CommaSeparators_AreSupported()
        {
            var points = PointFileParser.Parse("1,2,3\n");

            Assert.Single(points);
            Assert.Equal(new Vector3Dto(1, 2, 3), points[0]);
        }

        [Fact]
        public void Parse_UsesInvariantCulture()
        {
            var points = PointFileParser.Parse("1.5 2.25 -3.75\n");

            Assert.Single(points);
            Assert.Equal(1.5f, points[0].X);
            Assert.Equal(2.25f, points[0].Y);
            Assert.Equal(-3.75f, points[0].Z);
        }
    }
}
