using System;
using System.Collections.Generic;
using System.Numerics;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.DataStructures.Geometric;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>
    /// Correct texture-alignment math for the MCP bridge. The editor's built-in
    /// helpers (TextureExtensions) only offer face alignment and point-cloud fits
    /// that use the 6 world-axis extreme points; the routines here operate over ALL
    /// face vertices and add a true Hammer "world" alignment plus NaN/Inf recovery.
    /// </summary>
    internal static class TextureAlignment
    {
        private const float ScaleEpsilon = 1e-4f;
        private const float DegenerateExtent = 1e-3f;

        /// <summary>True Hammer World alignment: texture axes fixed to the world axes.</summary>
        public static void AlignWorld(Texture tex, Vector3 normal)
        {
            var axis = normal.ClosestAxis();
            var u = axis == Vector3.UnitX ? Vector3.UnitY : Vector3.UnitX;
            var v = axis == Vector3.UnitZ ? -Vector3.UnitY : -Vector3.UnitZ;
            tex.UAxis = u;
            tex.VAxis = v;
            tex.Rotation = 0;
        }

        /// <summary>Face alignment: axes lie in the face plane (delegates to the editor).</summary>
        public static void AlignFace(Texture tex, Vector3 normal)
        {
            tex.AlignToNormal(normal);
        }

        /// <summary>
        /// Scaled UV bounds over every face vertex (u = v.UAxis / XScale, v = v.VAxis / YScale).
        /// A near-zero scale is treated as 1 with a warning.
        /// </summary>
        public static void UvBounds(Face face, Texture tex, out float minU, out float maxU, out float minV, out float maxV, List<string> warnings)
        {
            var xs = tex.XScale;
            var ys = tex.YScale;
            if (Math.Abs(xs) < ScaleEpsilon) { xs = xs < 0 ? -1f : 1f; warnings?.Add($"face {face.ID}: XScale near zero; treated as 1 for UV bounds."); }
            if (Math.Abs(ys) < ScaleEpsilon) { ys = ys < 0 ? -1f : 1f; warnings?.Add($"face {face.ID}: YScale near zero; treated as 1 for UV bounds."); }

            minU = float.MaxValue; maxU = float.MinValue;
            minV = float.MaxValue; maxV = float.MinValue;
            foreach (var vtx in face.Vertices)
            {
                var u = Vector3.Dot(vtx, tex.UAxis) / xs;
                var v = Vector3.Dot(vtx, tex.VAxis) / ys;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
        }

        /// <summary>
        /// Justify the texture within the face. Modes: left/right/top/bottom/center use scaled
        /// UV bounds; fit re-derives scale from unscaled projections. Degenerate axes are skipped.
        /// </summary>
        public static void Justify(Face face, Texture tex, string justify, int texW, int texH, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(justify)) return;
            justify = justify.ToLowerInvariant();
            if (justify == "none") return;

            if (justify == "fit")
            {
                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                foreach (var vtx in face.Vertices)
                {
                    var u = Vector3.Dot(vtx, tex.UAxis);
                    var v = Vector3.Dot(vtx, tex.VAxis);
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

                var extentU = maxU - minU;
                var extentV = maxV - minV;
                if (Math.Abs(extentU) < DegenerateExtent || texW <= 0)
                {
                    warnings?.Add($"face {face.ID}: fit skipped U axis (degenerate extent).");
                }
                else
                {
                    var sx = tex.XScale < 0 ? -1f : 1f;
                    tex.XScale = extentU / texW * sx;
                    tex.XShift = -minU / tex.XScale;
                }

                if (Math.Abs(extentV) < DegenerateExtent || texH <= 0)
                {
                    warnings?.Add($"face {face.ID}: fit skipped V axis (degenerate extent).");
                }
                else
                {
                    var sy = tex.YScale < 0 ? -1f : 1f;
                    tex.YScale = extentV / texH * sy;
                    tex.YShift = -minV / tex.YScale;
                }
                return;
            }

            UvBounds(face, tex, out var mnU, out var mxU, out var mnV, out var mxV, warnings);
            switch (justify)
            {
                case "left": tex.XShift = -mnU; break;
                case "right": tex.XShift = -mxU + texW; break;
                case "top": tex.YShift = -mnV; break;
                case "bottom": tex.YShift = -mxV + texH; break;
                case "center":
                    tex.XShift = -(mnU + mxU) / 2f + texW / 2f;
                    tex.YShift = -(mnV + mxV) / 2f + texH / 2f;
                    break;
                default:
                    warnings?.Add($"face {face.ID}: unknown justify '{justify}'; ignored.");
                    break;
            }
        }

        /// <summary>Set absolute texture rotation using the editor's own rotation math.</summary>
        public static void SetRotationSafe(Texture tex, float degrees)
        {
            tex.SetRotation(degrees);
        }

        /// <summary>
        /// Repair NaN/Inf or near-zero values on the texture. Scales are restored to +/-1
        /// (sign preserved); shifts/rotation to 0; non-finite axes are re-derived via face alignment.
        /// </summary>
        public static void Sanitize(Texture tex, Vector3 normal, long faceId, List<string> warnings)
        {
            var sanitized = false;

            if (!IsFinite(tex.XScale) || Math.Abs(tex.XScale) < ScaleEpsilon)
            {
                tex.XScale = tex.XScale < 0 ? -1f : 1f;
                sanitized = true;
            }
            if (!IsFinite(tex.YScale) || Math.Abs(tex.YScale) < ScaleEpsilon)
            {
                tex.YScale = tex.YScale < 0 ? -1f : 1f;
                sanitized = true;
            }
            if (!IsFinite(tex.XShift)) { tex.XShift = 0; sanitized = true; }
            if (!IsFinite(tex.YShift)) { tex.YShift = 0; sanitized = true; }
            if (!IsFinite(tex.Rotation)) { tex.Rotation = 0; sanitized = true; }

            if (!IsFinite(tex.UAxis) || !IsFinite(tex.VAxis)
                || tex.UAxis.LengthSquared() < ScaleEpsilon || tex.VAxis.LengthSquared() < ScaleEpsilon)
            {
                tex.AlignToNormal(normal);
                sanitized = true;
            }

            if (sanitized) warnings?.Add($"face {faceId}: sanitized invalid texture values.");
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    }
}
