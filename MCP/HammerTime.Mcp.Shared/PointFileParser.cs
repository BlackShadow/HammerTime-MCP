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
            if (string.IsNullOrWhiteSpace(text)) return points;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                var parts = line.Replace(',', ' ')
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .ToArray();

                if (parts.Length < 3 ||
                    !TryParse(parts[0], out var x) ||
                    !TryParse(parts[1], out var y) ||
                    !TryParse(parts[2], out var z))
                {
                    throw new PointFileParseException(i + 1, $"Malformed pointfile coordinate line {i + 1}: {line}");
                }

                points.Add(new Vector3Dto(x, y, z));
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
