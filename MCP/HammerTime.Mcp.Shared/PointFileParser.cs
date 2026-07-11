using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HammerTime.Mcp.Shared
{
    public static class PointFileParser
    {
        public static IReadOnlyList<Vector3Dto> Parse(string text)
        {
            var points = new List<Vector3Dto>();
            if (!string.IsNullOrEmpty(text))
            {
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                    var parts = line.Replace(',', ' ')
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                    // Collect leading numeric tokens. Header/trailer/garbage lines start
                    // with a non-numeric token and yield fewer than 3 floats, so they are skipped.
                    var floats = new List<float>();
                    foreach (var part in parts)
                    {
                        if (!TryParse(part, out var value)) break;
                        floats.Add(value);
                    }

                    if (floats.Count >= 6)
                    {
                        // .lin segment: two endpoints per line.
                        points.Add(new Vector3Dto(floats[0], floats[1], floats[2]));
                        points.Add(new Vector3Dto(floats[3], floats[4], floats[5]));
                    }
                    else if (floats.Count >= 3)
                    {
                        // .pts point: a single coordinate triple.
                        points.Add(new Vector3Dto(floats[0], floats[1], floats[2]));
                    }
                }
            }

            if (points.Count == 0)
            {
                throw new PointFileParseException(0, "No parseable points found in pointfile.");
            }

            return points;
        }

        private static bool TryParse(string value, out float parsed)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
        }
    }

    public sealed class PointFileParseException : Exception
    {
        public int LineNumber { get; }

        public PointFileParseException(int lineNumber, string message) : base(message)
        {
            LineNumber = lineNumber;
        }
    }
}
