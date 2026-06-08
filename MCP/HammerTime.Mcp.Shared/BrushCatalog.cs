using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HammerTime.Mcp.Shared
{
    public static class BrushCatalog
    {
        private static readonly string[] TypeOrder =
        {
            "Arch",
            "Block",
            "Tetrahedron",
            "Pyramid",
            "Wedge",
            "Cylinder",
            "Cone",
            "Pipe",
            "Sphere",
            "Torus",
            "Text"
        };

        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["box"] = "Block",
                ["cube"] = "Block",
                ["rect"] = "Block",
                ["rectangle"] = "Block",
                ["tetra"] = "Tetrahedron",
                ["tetrahedron"] = "Tetrahedron",
                ["pyramid"] = "Pyramid",
                ["ramp"] = "Wedge",
                ["wedge"] = "Wedge",
                ["barrel"] = "Cylinder",
                ["barrell"] = "Cylinder",
                ["cask"] = "Cylinder",
                ["can"] = "Cylinder",
                ["tank"] = "Cylinder",
                ["cylinder"] = "Cylinder",
                ["cone"] = "Cone",
                ["pipe"] = "Pipe",
                ["tube"] = "Pipe",
                ["arch"] = "Arch",
                ["arc"] = "Arch",
                ["sphere"] = "Sphere",
                ["ball"] = "Sphere",
                ["torus"] = "Torus",
                ["donut"] = "Torus",
                ["doughnut"] = "Torus",
                ["text"] = "Text"
            };

        public static IReadOnlyList<BrushTypeInfo> DefaultTypes { get; } =
            TypeOrder.Select(x => new BrushTypeInfo(x, GetAliasesFor(x).ToArray())).ToList();

        public static string ResolveTypeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var normalized = NormalizeKey(input);
            if (Aliases.TryGetValue(normalized, out var alias)) return alias;
            return TypeOrder.FirstOrDefault(x => string.Equals(NormalizeKey(x), normalized, StringComparison.OrdinalIgnoreCase))
                   ?? input.Trim();
        }

        public static string NormalizeParameterName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;
            var words = SplitWords(input).ToList();
            if (words.Count == 0) return string.Empty;
            var builder = new StringBuilder(words[0].ToLowerInvariant());
            foreach (var word in words.Skip(1))
            {
                builder.Append(char.ToUpperInvariant(word[0]));
                if (word.Length > 1) builder.Append(word.Substring(1).ToLowerInvariant());
            }
            return builder.ToString();
        }

        public static bool IsReservedParameter(string name)
        {
            switch (NormalizeParameterName(name).ToLowerInvariant())
            {
                case "path":
                case "documentindex":
                case "type":
                case "brushtype":
                case "min":
                case "max":
                case "texture":
                case "texturescale":
                case "select":
                case "round":
                case "parameters":
                    return true;
                default:
                    return false;
            }
        }

        public static IReadOnlyList<string> GetAliasesFor(string type)
        {
            var resolved = ResolveTypeName(type) ?? type;
            return Aliases
                .Where(x => string.Equals(x.Value, resolved, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(x.Key, NormalizeKey(resolved), StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Key)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string NormalizeKey(string value)
        {
            return string.Concat(SplitWords(value)).ToLowerInvariant();
        }

        private static IEnumerable<string> SplitWords(string value)
        {
            var current = new StringBuilder();
            foreach (var ch in value.Trim().TrimStart('_'))
            {
                if (char.IsLetterOrDigit(ch))
                {
                    current.Append(ch);
                }
                else if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }

            if (current.Length > 0) yield return current.ToString();
        }
    }

    public sealed class BrushTypeInfo
    {
        public BrushTypeInfo(string name, IReadOnlyList<string> aliases)
        {
            Name = name;
            Aliases = aliases;
        }

        public string Name { get; }
        public IReadOnlyList<string> Aliases { get; }
    }
}
