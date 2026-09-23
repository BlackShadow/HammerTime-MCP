using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HammerTime.Mcp.Shared;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.DataStructures.Geometric;
using Plane = Sledge.DataStructures.Geometric.Plane;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>A brush side as read from map text or a plane definition: the plane and the texture that goes on it.</summary>
    internal sealed class PlaneDefinition
    {
        public PlaneDefinition(Plane plane, Texture texture)
        {
            Plane = plane;
            Texture = texture;
        }

        public Plane Plane { get; }
        public Texture Texture { get; }
    }

    /// <summary>
    /// Hammer / Valve 220 brush text: what <c>object_export_maptext</c> writes and <c>object_import_maptext</c>
    /// reads. Numbers are written in plain decimal notation and read in any notation (including the
    /// exponent form some tools emit), comment lines are skipped, and a solid is rebuilt from its planes.
    /// </summary>
    internal static class MapText
    {
        private const string Number = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";
        private static readonly Regex FaceLine = new Regex(
            @"^\(\s*(?<p1>[^)]*?)\s*\)\s*\(\s*(?<p2>[^)]*?)\s*\)\s*\(\s*(?<p3>[^)]*?)\s*\)\s*(?<tex>\S+)\s*\[\s*(?<u>[^\]]+)\]\s*\[\s*(?<v>[^\]]+)\]\s*(?<rot>" + Number + @")\s*(?<xs>" + Number + @")\s*(?<ys>" + Number + @")",
            RegexOptions.Compiled);
        private static readonly Regex Numbers = new Regex(Number, RegexOptions.Compiled);

        public const string FormatHint = "( x y z ) ( x y z ) ( x y z ) TEXTURE [ ux uy uz xshift ] [ vx vy vz yshift ] rotation xscale yscale";

        /// <summary>The solid as one Valve 220 brush block.</summary>
        public static string Write(Solid solid)
        {
            var builder = new StringBuilder();
            builder.Append("{\r\n");
            foreach (var face in solid.Faces)
            {
                var points = face.Vertices.Take(3).ToList();
                if (points.Count < 3) continue;
                builder.Append(string.Format(CultureInfo.InvariantCulture,
                    "( {0} {1} {2} ) ( {3} {4} {5} ) ( {6} {7} {8} ) {9} [ {10} {11} {12} {13} ] [ {14} {15} {16} {17} ] {18} {19} {20}\r\n",
                    N(points[0].X), N(points[0].Y), N(points[0].Z),
                    N(points[1].X), N(points[1].Y), N(points[1].Z),
                    N(points[2].X), N(points[2].Y), N(points[2].Z),
                    face.Texture.Name,
                    N(face.Texture.UAxis.X), N(face.Texture.UAxis.Y), N(face.Texture.UAxis.Z), N(face.Texture.XShift),
                    N(face.Texture.VAxis.X), N(face.Texture.VAxis.Y), N(face.Texture.VAxis.Z), N(face.Texture.YShift),
                    N(face.Texture.Rotation), N(face.Texture.XScale), N(face.Texture.YScale)));
            }
            builder.Append("}\r\n");
            return builder.ToString();
        }

        /// <summary>Plain decimal with up to six places: never an exponent, which older readers reject.</summary>
        private static string N(float value)
        {
            var text = value.ToString("0.######", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

        /// <summary>Split text holding several brush blocks into the individual "{ ... }" blocks.</summary>
        public static List<string> SplitBrushBlocks(string text)
        {
            var blocks = new List<string>();
            var builder = new StringBuilder();
            var depth = 0;
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (depth == 0)
                {
                    if (trimmed == "{")
                    {
                        builder.Clear();
                        builder.AppendLine(rawLine);
                        depth = 1;
                    }
                }
                else
                {
                    builder.AppendLine(rawLine);
                    if (trimmed == "{") depth++;
                    else if (trimmed == "}") depth--;
                    if (depth == 0)
                    {
                        blocks.Add(builder.ToString());
                        builder.Clear();
                    }
                }
            }
            return blocks;
        }

        /// <summary>Read one brush block (braces optional, comment lines ignored) into a solid.</summary>
        public static Solid ParseSolid(string text, UniqueNumberGenerator generator)
        {
            var definitions = new List<PlaneDefinition>();
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "{" || trimmed == "}" || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                definitions.Add(ParseFaceLine(trimmed));
            }
            if (definitions.Count < 4) throw new BridgeCommandException(ErrorCodes.InvalidRequest, "Map text must contain at least 4 brush side lines.");
            return SolidFromPlanes(definitions, generator, "Map text did not produce a valid convex brush.");
        }

        public static PlaneDefinition ParseFaceLine(string line)
        {
            var match = FaceLine.Match(line);
            if (!match.Success)
            {
                throw new BridgeCommandException(ErrorCodes.ParseError, "Could not parse brush side (Valve 220 format expected: " + FormatHint + "): " + line);
            }
            var u = ParseFloatList(match.Groups["u"].Value, 4);
            var v = ParseFloatList(match.Groups["v"].Value, 4);
            var texture = new Texture
            {
                Name = match.Groups["tex"].Value,
                UAxis = new Vector3(u[0], u[1], u[2]),
                XShift = u[3],
                VAxis = new Vector3(v[0], v[1], v[2]),
                YShift = v[3],
                Rotation = ParseFloat(match.Groups["rot"].Value),
                XScale = ParseFloat(match.Groups["xs"].Value),
                YScale = ParseFloat(match.Groups["ys"].Value)
            };
            return new PlaneDefinition(
                new Plane(ParseVector(match.Groups["p1"].Value), ParseVector(match.Groups["p2"].Value), ParseVector(match.Groups["p3"].Value)),
                texture);
        }

        /// <summary>
        /// Intersect the planes into a convex solid; each polygon takes the texture of the plane it lies on
        /// (matched by normal, then by distance so a redundant parallel plane cannot steal the texture).
        /// </summary>
        public static Solid SolidFromPlanes(IReadOnlyList<PlaneDefinition> definitions, UniqueNumberGenerator generator, string invalidMessage)
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                for (var j = i + 1; j < definitions.Count; j++)
                {
                    if (SamePlane(definitions[i].Plane, definitions[j].Plane))
                    {
                        throw new BridgeCommandException(ErrorCodes.InvalidRequest, $"Planes {i} and {j} are the same plane.");
                    }
                }
            }
            // The builder keeps a plane's polygon untouched when it lies entirely outside another plane, so a
            // redundant plane beyond the brush would survive as a huge stray face (and disturb its neighbours):
            // find those planes on a first pass and rebuild from the planes that really bound the brush.
            var planes = definitions.Select(x => x.Plane).ToList();
            var first = new Polyhedron(planes);
            var redundant = new HashSet<int>();
            foreach (var polygon in first.Polygons)
            {
                if (planes.Any(plane => !SamePlane(plane, polygon.Plane) && polygon.Vertices.Count > 0 && polygon.Vertices.All(v => plane.OnPlane(v) > 0)))
                {
                    for (var i = 0; i < planes.Count; i++) if (SamePlane(planes[i], polygon.Plane)) redundant.Add(i);
                }
            }
            var bounding = definitions.Where((d, i) => !redundant.Contains(i)).ToList();
            var polyhedron = new Polyhedron(bounding.Select(x => x.Plane));
            if (bounding.Count < 4 || !polyhedron.IsValid()) throw new BridgeCommandException(ErrorCodes.InvalidOperation, invalidMessage);
            var solid = new Solid(generator.Next("MapObject"));
            foreach (var polygon in polyhedron.Polygons)
            {
                var definition = bounding
                    .OrderByDescending(x => Vector3.Dot(x.Plane.Normal, polygon.Plane.Normal))
                    .ThenBy(x => Math.Abs(x.Plane.DistanceFromOrigin - polygon.Plane.DistanceFromOrigin))
                    .First();
                var face = new Face(generator.Next("Face")) { Texture = definition.Texture.Clone() };
                face.Vertices.AddRange(polygon.Vertices);
                solid.Data.Add(face);
            }
            solid.DescendantsChanged();
            return solid;
        }

        private static bool SamePlane(Plane a, Plane b)
        {
            return Vector3.Dot(a.Normal, b.Normal) > 0.9999f && Math.Abs(a.DistanceFromOrigin - b.DistanceFromOrigin) < 0.01f;
        }

        private static Vector3 ParseVector(string text)
        {
            var values = ParseFloatList(text, 3);
            return new Vector3(values[0], values[1], values[2]);
        }

        private static float[] ParseFloatList(string text, int min)
        {
            var values = Numbers.Matches(text).Cast<Match>().Select(x => ParseFloat(x.Value)).ToArray();
            if (values.Length < min) throw new BridgeCommandException(ErrorCodes.ParseError, "Expected at least " + min + " numeric values in: " + text);
            return values;
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
