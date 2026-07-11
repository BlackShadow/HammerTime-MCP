using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using Newtonsoft.Json.Linq;
using Sledge.BspEditor.Primitives;
using Sledge.BspEditor.Primitives.MapData;
using Sledge.BspEditor.Primitives.MapObjectData;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.DataStructures.Geometric;

namespace HammerTime.Mcp.Plugin
{
    public sealed partial class HammerTimeMcpPlugin
    {
        // ======================================================================
        // W4 - Read-only audits (no Perform; whole-map scope allowed by default).
        // ======================================================================

        private async Task<JToken> TextureAudit(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var scaleTolerance = (float)parameters.Optional("scaleTolerance", 0.25);
            var scaleReference = parameters.Optional("scaleReference", "median").ToLowerInvariant();
            var nonUniformTolerance = (float)parameters.Optional("nonUniformTolerance", 0.05);
            var rotationTolerance = (float)parameters.Optional("rotationTolerance", 0.5);
            var shiftTolerance = (float)parameters.Optional("shiftTolerance", 0.01);
            var stretchThreshold = (float)parameters.Optional("stretchThreshold", 4.0);
            var maxOffenders = parameters.Optional("maxOffenders", 50);
            var checkCoplanarMismatch = parameters.Optional("checkCoplanarMismatch", true);
            var checkToolTextures = parameters.Optional("checkToolTextures", true);
            var checkHiddenFaces = parameters.Optional("checkHiddenFaces", false);
            var checkPropTextures = parameters.Optional("checkPropTextures", true);
            var propMaxDimension = (float)parameters.Optional("propMaxDimension", 160.0);

            var warnings = new List<string>();
            var faces = ResolveFaceRefsOrObjects(doc, parameters, allowAllObjectsWhenNoTarget: true).ToList();

            var collection = await doc.Environment.GetTextureCollection().ConfigureAwait(true);
            var allTextureNames = new HashSet<string>(collection.GetAllTextures(), StringComparer.InvariantCultureIgnoreCase);

            // Gather texture dimensions once per distinct name.
            var distinctTextures = faces.Select(f => f.Face.Texture.Name ?? "").Distinct(StringComparer.InvariantCultureIgnoreCase).ToList();
            await collection.Precache(distinctTextures).ConfigureAwait(true);
            var dims = new Dictionary<string, (int w, int h, bool known)>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var name in distinctTextures)
            {
                var known = false;
                var w = 64;
                var h = 64;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        var item = await collection.GetTextureItem(name).ConfigureAwait(true);
                        if (item != null && item.Width > 0 && item.Height > 0) { w = item.Width; h = item.Height; known = true; }
                    }
                    catch (Exception ex)
                    {
                        Log($"texture.audit: dims lookup failed for '{name}': {ex.Message}");
                    }
                }
                dims[name] = (w, h, known);
            }

            var gridSpacing = doc.Map.Data.GetOne<GridData>()?.Grid?.Spacing;

            // Median scale over audited non-tool faces.
            var nonToolXs = new List<float>();
            var nonToolYs = new List<float>();
            foreach (var fr in faces)
            {
                var info = TextureSemantics.Parse(fr.Face.Texture.Name);
                if (info.Tool == null)
                {
                    nonToolXs.Add(Math.Abs(fr.Face.Texture.XScale));
                    nonToolYs.Add(Math.Abs(fr.Face.Texture.YScale));
                }
            }
            var medianX = Median(nonToolXs);
            var medianY = Median(nonToolYs);
            var refX = scaleReference == "one" ? 1f : (medianX > 0 ? medianX : 1f);
            var refY = scaleReference == "one" ? 1f : (medianY > 0 ? medianY : 1f);

            // Prop-scale solids: bounding-box longest edge <= propMaxDimension. Computed
            // once per parent solid (faces are audited per solid). Only these solids get
            // the two prop-texture checks below.
            var propSolids = new HashSet<long>();
            if (checkPropTextures)
            {
                foreach (var obj in faces.Select(f => f.Object).Distinct())
                {
                    var bb = obj.BoundingBox;
                    if (bb != null && !bb.IsEmpty() && bb.LargestDimension <= propMaxDimension)
                    {
                        propSolids.Add(obj.ID);
                    }
                }
            }

            // Per-face issue accumulation.
            var issuesByFace = new Dictionary<long, List<string>>();
            var offenders = new List<OffenderRow>();
            var summary = new Dictionary<string, int>();
            var toolTextureSummary = new Dictionary<string, int>();
            var truncated = false;

            void AddIssue(OffenderRow row, string code)
            {
                if (!row.Issues.Contains(code)) row.Issues.Add(code);
            }

            var rows = new List<OffenderRow>(faces.Count);
            var rowByFace = new Dictionary<long, OffenderRow>();
            foreach (var fr in faces)
            {
                var tex = fr.Face.Texture;
                var info = TextureSemantics.Parse(tex.Name);
                var d = dims.TryGetValue(tex.Name ?? "", out var dv) ? dv : (64, 64, false);
                var normal = fr.Face.Plane.Normal;

                var row = new OffenderRow
                {
                    ObjectId = fr.Object.ID,
                    FaceId = fr.Face.ID,
                    Texture = tex.Name,
                    Normal = normal,
                    XScale = tex.XScale,
                    YScale = tex.YScale,
                    Rotation = tex.Rotation,
                    XShift = tex.XShift,
                    YShift = tex.YShift,
                    TexWidth = d.Item1,
                    TexHeight = d.Item2
                };
                rows.Add(row);
                rowByFace[fr.Face.ID] = row;

                // Tool textures.
                if (info.Tool != null)
                {
                    var key = info.Tool;
                    toolTextureSummary[key] = toolTextureSummary.TryGetValue(key, out var c) ? c + 1 : 1;
                    if (checkToolTextures && IsWorldSolid(fr.Object)
                        && (key == "aaatrigger" || key == "origin" || key == "trigger"))
                    {
                        AddIssue(row, "tool_texture_visible");
                    }
                }

                // Missing / unknown dims.
                if (!string.IsNullOrWhiteSpace(tex.Name) && !allTextureNames.Contains(tex.Name))
                {
                    AddIssue(row, "missing_texture");
                }
                if (!d.Item3)
                {
                    AddIssue(row, "unknown_dimensions");
                }

                var absX = Math.Abs(tex.XScale);
                var absY = Math.Abs(tex.YScale);

                // scale_outlier (only meaningful for non-tool faces).
                if (info.Tool == null && absX > 1e-4f && absY > 1e-4f)
                {
                    if (Math.Abs(absX - refX) > scaleTolerance * Math.Max(refX, 1e-3f)
                        || Math.Abs(absY - refY) > scaleTolerance * Math.Max(refY, 1e-3f))
                    {
                        AddIssue(row, "scale_outlier");
                    }
                }

                // non_uniform_scale.
                var maxScale = Math.Max(absX, absY);
                if (maxScale > 1e-4f && Math.Abs(absX - absY) / maxScale > nonUniformTolerance)
                {
                    AddIssue(row, "non_uniform_scale");
                }

                // rotation_off_axis (informational).
                var r = ((tex.Rotation % 90f) + 90f) % 90f;
                var rOff = Math.Min(r, 90f - r);
                if (rOff > rotationTolerance)
                {
                    AddIssue(row, "rotation_off_axis");
                }

                // rotation_axis_mismatch (informational).
                var expected = new Texture();
                expected.AlignToNormal(normal);
                expected.SetRotation(tex.Rotation);
                if (AngleDegrees(expected.UAxis, tex.UAxis) > 1f || AngleDegrees(expected.VAxis, tex.VAxis) > 1f)
                {
                    AddIssue(row, "rotation_axis_mismatch");
                }

                // fractional_shift (informational).
                if (Math.Abs(tex.XShift - (float)Math.Round(tex.XShift)) > shiftTolerance
                    || Math.Abs(tex.YShift - (float)Math.Round(tex.YShift)) > shiftTolerance)
                {
                    AddIssue(row, "fractional_shift");
                    if (Math.Abs(tex.XShift) >= d.Item1 || Math.Abs(tex.YShift) >= d.Item2)
                    {
                        var tmp = new Texture { XShift = tex.XShift, YShift = tex.YShift };
                        tmp.MinimiseTextureShiftValues(d.Item1, d.Item2);
                        row.NormalizedShiftX = tmp.XShift;
                        row.NormalizedShiftY = tmp.YShift;
                    }
                }

                // stretched.
                if (absX > stretchThreshold || (absX > 1e-4f && absX < 1f / stretchThreshold)
                    || absY > stretchThreshold || (absY > 1e-4f && absY < 1f / stretchThreshold))
                {
                    AddIssue(row, "stretched");
                    row.TexelDensityX = absX > 1e-4f ? 1f / absX : (float?)null;
                    row.TexelDensityY = absY > 1e-4f ? 1f / absY : (float?)null;
                }

                // perpendicular_axis.
                var cp = tex.UAxis.Cross(tex.VAxis);
                if (Math.Abs(Vector3.Dot(normal, cp)) <= 1e-4f)
                {
                    AddIssue(row, "perpendicular_axis");
                }

                // Prop-texture checks (informational). Only for non-tool faces on
                // prop-scale solids (bbox longest edge <= propMaxDimension).
                if (checkPropTextures && info.Tool == null && propSolids.Contains(fr.Object.ID))
                {
                    // random_tiling_on_prop: -N random-tiling textures let the engine
                    // randomize the frame per face, so a prop textured with a -0 frame
                    // renders with mismatched faces (this shipped white-marble furniture
                    // that was meant to be dark wood).
                    if (info.RandomTiling)
                    {
                        AddIssue(row, "random_tiling_on_prop");
                    }

                    // prop_texture_crop: framed object art (crates, panels, appliance
                    // fronts) cropped at scale ~1 loses its border — it should be fitted
                    // (justify:fit) instead (this shipped a crate with its frame band cut
                    // off). Only meaningful when texture dims are known and scale is ~1.
                    if (d.Item3 && d.Item1 > 0 && d.Item2 > 0
                        && absX >= 0.9f && absX <= 1.1f && absY >= 0.9f && absY <= 1.1f)
                    {
                        // Texture-space extent mirrors TextureAlignment.UvBounds:
                        // project each vertex onto the texture axes, divided by |scale|.
                        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                        foreach (var vtx in fr.Face.Vertices)
                        {
                            var pu = Vector3.Dot(vtx, tex.UAxis) / absX;
                            var pv = Vector3.Dot(vtx, tex.VAxis) / absY;
                            if (pu < minU) minU = pu;
                            if (pu > maxU) maxU = pu;
                            if (pv < minV) minV = pv;
                            if (pv > maxV) maxV = pv;
                        }
                        var ratioU = (maxU - minU) / d.Item1;
                        var ratioV = (maxV - minV) / d.Item2;
                        if ((ratioU >= 0.40f && ratioU <= 0.95f && ratioV <= 1.05f)
                            || (ratioV >= 0.40f && ratioV <= 0.95f && ratioU <= 1.05f))
                        {
                            AddIssue(row, "prop_texture_crop");
                            row.CropRatioU = ratioU;
                            row.CropRatioV = ratioV;
                        }
                    }
                }
            }

            // coplanar_texture_mismatch and hidden_face_not_null (bucketed by quantized plane).
            if (checkCoplanarMismatch || checkHiddenFaces)
            {
                var buckets = new Dictionary<string, List<FaceRef>>();
                foreach (var fr in faces)
                {
                    var key = QuantizedPlaneKey(fr.Face.Plane.Normal, fr.Face.Plane.DistanceFromOrigin, false);
                    if (!buckets.TryGetValue(key, out var list)) { list = new List<FaceRef>(); buckets[key] = list; }
                    list.Add(fr);
                }

                if (checkCoplanarMismatch)
                {
                    foreach (var bucket in buckets.Values)
                    {
                        var capped = bucket;
                        if (bucket.Count > 64) { capped = bucket.Take(64).ToList(); truncated = true; }
                        var basisNormal = capped[0].Face.Plane.Normal;
                        var basis = new Texture();
                        basis.AlignToNormal(basisNormal);
                        var boxes = capped.Select(f => ProjectBox(f.Face, basis.UAxis, basis.VAxis)).ToList();
                        for (var a = 0; a < capped.Count; a++)
                        {
                            var ta = TextureSemantics.Parse(capped[a].Face.Texture.Name);
                            for (var b = a + 1; b < capped.Count; b++)
                            {
                                var tb = TextureSemantics.Parse(capped[b].Face.Texture.Name);
                                if (ta.Tool != null || tb.Tool != null) continue;
                                if (string.Equals(capped[a].Face.Texture.Name, capped[b].Face.Texture.Name, StringComparison.InvariantCultureIgnoreCase)) continue;
                                if (BoxesTouch(boxes[a], boxes[b], 1f))
                                {
                                    if (rowByFace.TryGetValue(capped[a].Face.ID, out var ra)) AddIssue(ra, "coplanar_texture_mismatch");
                                    if (rowByFace.TryGetValue(capped[b].Face.ID, out var rb)) AddIssue(rb, "coplanar_texture_mismatch");
                                }
                            }
                        }
                    }
                }

                if (checkHiddenFaces)
                {
                    // Bucket by unsigned plane so opposing faces (flipped normal) collide.
                    var unsigned = new Dictionary<string, List<FaceRef>>();
                    foreach (var fr in faces)
                    {
                        var key = QuantizedPlaneKey(fr.Face.Plane.Normal, fr.Face.Plane.DistanceFromOrigin, true);
                        if (!unsigned.TryGetValue(key, out var list)) { list = new List<FaceRef>(); unsigned[key] = list; }
                        list.Add(fr);
                    }
                    foreach (var bucket in unsigned.Values)
                    {
                        var capped = bucket.Count > 64 ? bucket.Take(64).ToList() : bucket;
                        if (bucket.Count > 64) truncated = true;
                        var basis = new Texture();
                        basis.AlignToNormal(capped[0].Face.Plane.Normal);
                        var boxes = capped.Select(f => ProjectBox(f.Face, basis.UAxis, basis.VAxis)).ToList();
                        for (var a = 0; a < capped.Count; a++)
                        {
                            var infoA = TextureSemantics.Parse(capped[a].Face.Texture.Name);
                            if (string.Equals(infoA.Basename, "null", StringComparison.InvariantCultureIgnoreCase)) continue;
                            for (var b = 0; b < capped.Count; b++)
                            {
                                if (a == b) continue;
                                var opposed = Vector3.Dot(capped[a].Face.Plane.Normal, capped[b].Face.Plane.Normal) < -0.9f;
                                if (!opposed) continue;
                                if (BoxContains(boxes[b], boxes[a], 1f))
                                {
                                    if (rowByFace.TryGetValue(capped[a].Face.ID, out var ra)) AddIssue(ra, "hidden_face_not_null");
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Collect offenders (faces with any issue).
            foreach (var row in rows)
            {
                if (row.Issues.Count == 0) continue;
                offenders.Add(row);
                foreach (var code in row.Issues)
                {
                    summary[code] = summary.TryGetValue(code, out var c) ? c + 1 : 1;
                }
            }

            var sortedOffenders = offenders
                .OrderByDescending(o => o.Issues.Count)
                .ThenBy(o => o.ObjectId)
                .ThenBy(o => o.FaceId)
                .ToList();
            if (sortedOffenders.Count > maxOffenders)
            {
                truncated = true;
                sortedOffenders = sortedOffenders.Take(maxOffenders).ToList();
            }

            return ToToken(new
            {
                faceCount = faces.Count,
                medianScale = new { x = medianX, y = medianY },
                gridSpacing,
                summary,
                offenders = sortedOffenders.Select(o => o.ToJson()).ToList(),
                toolTextureSummary,
                warnings,
                truncated
            });
        }

        private sealed class OffenderRow
        {
            public long ObjectId;
            public long FaceId;
            public string Texture;
            public Vector3 Normal;
            public float XScale;
            public float YScale;
            public float Rotation;
            public float XShift;
            public float YShift;
            public int TexWidth;
            public int TexHeight;
            public float? NormalizedShiftX;
            public float? NormalizedShiftY;
            public float? TexelDensityX;
            public float? TexelDensityY;
            public float? CropRatioU;
            public float? CropRatioV;
            public List<string> Issues = new List<string>();

            public JObject ToJson()
            {
                var metrics = new JObject
                {
                    ["xScale"] = XScale,
                    ["yScale"] = YScale,
                    ["rotation"] = Rotation,
                    ["xShift"] = XShift,
                    ["yShift"] = YShift,
                    ["texWidth"] = TexWidth,
                    ["texHeight"] = TexHeight
                };
                if (NormalizedShiftX.HasValue) metrics["normalizedShiftX"] = NormalizedShiftX.Value;
                if (NormalizedShiftY.HasValue) metrics["normalizedShiftY"] = NormalizedShiftY.Value;
                if (TexelDensityX.HasValue) metrics["texelDensityX"] = TexelDensityX.Value;
                if (TexelDensityY.HasValue) metrics["texelDensityY"] = TexelDensityY.Value;
                if (CropRatioU.HasValue) metrics["cropRatioU"] = CropRatioU.Value;
                if (CropRatioV.HasValue) metrics["cropRatioV"] = CropRatioV.Value;
                return new JObject
                {
                    ["objectId"] = ObjectId,
                    ["faceId"] = FaceId,
                    ["texture"] = Texture,
                    ["issues"] = new JArray(Issues),
                    ["metrics"] = metrics,
                    ["normal"] = new JObject { ["x"] = Normal.X, ["y"] = Normal.Y, ["z"] = Normal.Z }
                };
            }
        }

        // ----------------------------------------------------------------------

        private async Task<JToken> MapDesignAudit(JObject parameters)
        {
            var doc = ResolveDocument(parameters, false);
            var selectedOnly = parameters.Optional("selectedOnly", false);
            var monotonyThreshold = (float)parameters.Optional("monotonyThreshold", 0.6);
            var microSize = (float)parameters.Optional("microSize", 1.0);
            var maxExtent = (float)parameters.Optional("maxExtent", 4096);
            var cellSize = Math.Max(1f, (float)parameters.Optional("cellSize", 1024));
            var cellFaceThreshold = parameters.Optional("cellFaceThreshold", 400);
            var lightRadius = (float)parameters.Optional("lightRadius", 768);
            var includeProblemChecks = parameters.Optional("includeProblemChecks", false);
            var maxOffenders = parameters.Optional("maxOffenders", 50);

            var allChecks = new[] { "off_grid", "micro_brush", "texture_monotony", "scale_conventions", "unlit", "missing_player_start", "world_extents", "wpoly_hotspots" };
            var requested = (parameters["checks"] as JArray)?.Select(x => x.Value<string>().ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Enabled(string name) => requested == null || requested.Count == 0 || requested.Contains(name);

            var warnings = new List<string>();
            var heuristics = new List<string>();
            var truncated = false;

            IEnumerable<IMapObject> scopeObjects = selectedOnly
                ? doc.Selection.SelectMany(x => x.FindAll()).Where(x => x.Hierarchy.Parent != null)
                : doc.Map.Root.FindAll().Where(x => x.Hierarchy.Parent != null);
            var objects = scopeObjects.Distinct().ToList();
            var solids = objects.OfType<Solid>().ToList();
            var entities = objects.OfType<Entity>().ToList();
            var faces = solids.SelectMany(s => s.Faces.Select(f => new FaceRef(s, f))).ToList();

            var gridSpacing = doc.Map.Data.GetOne<GridData>()?.Grid?.Spacing;
            var findings = new JObject();

            // off_grid.
            if (Enabled("off_grid"))
            {
                var histogram = new Dictionary<string, int>();
                var offenders = new List<JObject>();
                foreach (var s in solids)
                {
                    var g = LargestGridGranularity(s);
                    var key = g < 1 ? "fractional" : g.ToString(CultureInfo.InvariantCulture);
                    histogram[key] = histogram.TryGetValue(key, out var c) ? c + 1 : 1;
                    if (g < 1)
                    {
                        offenders.Add(new JObject { ["objectId"] = s.ID, ["granularity"] = "fractional" });
                    }
                }
                if (offenders.Count > maxOffenders) { truncated = true; offenders = offenders.Take(maxOffenders).ToList(); }
                findings["off_grid"] = new JObject
                {
                    ["gridSpacing"] = gridSpacing,
                    ["granularityHistogram"] = JObject.FromObject(histogram),
                    ["fractionalSolids"] = offenders.Count,
                    ["offenders"] = new JArray(offenders)
                };
            }

            // micro_brush + degenerate_face.
            if (Enabled("micro_brush"))
            {
                var micro = new List<JObject>();
                var degenerate = new List<JObject>();
                foreach (var s in solids)
                {
                    if (s.BoundingBox != null && !s.BoundingBox.IsEmpty() && s.BoundingBox.SmallestDimension < microSize)
                    {
                        micro.Add(new JObject { ["objectId"] = s.ID, ["smallestDimension"] = s.BoundingBox.SmallestDimension });
                    }
                    foreach (var f in s.Faces)
                    {
                        if (f.Vertices.Count < 3 || PolygonArea(f) < 0.1f)
                        {
                            degenerate.Add(new JObject { ["objectId"] = s.ID, ["faceId"] = f.ID, ["vertexCount"] = f.Vertices.Count, ["area"] = PolygonArea(f) });
                        }
                    }
                }
                if (micro.Count > maxOffenders) { truncated = true; micro = micro.Take(maxOffenders).ToList(); }
                if (degenerate.Count > maxOffenders) { truncated = true; degenerate = degenerate.Take(maxOffenders).ToList(); }
                findings["micro_brush"] = new JObject
                {
                    ["microBrushCount"] = micro.Count,
                    ["degenerateFaceCount"] = degenerate.Count,
                    ["offenders"] = new JArray(micro),
                    ["degenerateFaces"] = new JArray(degenerate)
                };
            }

            // texture_monotony.
            if (Enabled("texture_monotony"))
            {
                var usage = new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);
                var total = 0;
                foreach (var fr in faces)
                {
                    var info = TextureSemantics.Parse(fr.Face.Texture.Name);
                    if (info.Tool != null || info.Sky) continue;
                    var name = fr.Face.Texture.Name ?? "";
                    usage[name] = usage.TryGetValue(name, out var c) ? c + 1 : 1;
                    total++;
                }
                var top = usage.OrderByDescending(x => x.Value).FirstOrDefault();
                var share = total > 0 && top.Key != null ? top.Value / (float)total : 0f;
                var monotonous = total >= 50 && share > monotonyThreshold;
                findings["texture_monotony"] = new JObject
                {
                    ["faceCount"] = total,
                    ["topTexture"] = top.Key,
                    ["topShare"] = (float)Math.Round(share, 3),
                    ["threshold"] = monotonyThreshold,
                    ["monotonous"] = monotonous
                };
            }

            // scale_conventions (heuristic).
            if (Enabled("scale_conventions"))
            {
                var doorOffenders = new List<JObject>();
                foreach (var e in entities)
                {
                    var cn = e.Data.GetOne<EntityData>()?.Name;
                    if (!string.Equals(cn, "func_door", StringComparison.OrdinalIgnoreCase) && !string.Equals(cn, "func_door_rotating", StringComparison.OrdinalIgnoreCase)) continue;
                    var bb = e.BoundingBox;
                    if (bb == null || bb.IsEmpty()) continue;
                    var width = Math.Max(bb.Width, bb.Length);
                    var height = bb.Height;
                    if (width < 48 || width > 64 || height < 96 || height > 128)
                    {
                        doorOffenders.Add(new JObject { ["objectId"] = e.ID, ["classname"] = cn, ["width"] = width, ["height"] = height });
                    }
                }

                // floor/ceiling gap distribution (heuristic).
                var gapOffenders = new List<JObject>();
                var upFaces = new Dictionary<(int, int), List<FaceRef>>();
                var downFaces = new Dictionary<(int, int), List<FaceRef>>();
                foreach (var fr in faces)
                {
                    var n = fr.Face.Plane.Normal;
                    var c = fr.Face.Origin;
                    var cell = ((int)Math.Floor(c.X / cellSize), (int)Math.Floor(c.Y / cellSize));
                    if (n.Z > 0.9f) { if (!upFaces.TryGetValue(cell, out var l)) upFaces[cell] = l = new List<FaceRef>(); l.Add(fr); }
                    else if (n.Z < -0.9f) { if (!downFaces.TryGetValue(cell, out var l)) downFaces[cell] = l = new List<FaceRef>(); l.Add(fr); }
                }
                foreach (var kv in upFaces)
                {
                    if (!downFaces.TryGetValue(kv.Key, out var downs)) continue;
                    foreach (var up in kv.Value)
                    {
                        var ub = XyBox(up.Face);
                        foreach (var dn in downs)
                        {
                            if (dn.Face.Origin.Z <= up.Face.Origin.Z) continue; // ceiling above floor
                            var db = XyBox(dn.Face);
                            if (!BoxesTouch(ub, db, 0f)) continue;
                            var gap = dn.Face.Origin.Z - up.Face.Origin.Z;
                            if (gap > 0 && gap < 108)
                            {
                                gapOffenders.Add(new JObject { ["floorFace"] = up.Face.ID, ["ceilingFace"] = dn.Face.ID, ["gap"] = gap });
                            }
                        }
                    }
                }
                if (doorOffenders.Count > maxOffenders) { truncated = true; doorOffenders = doorOffenders.Take(maxOffenders).ToList(); }
                if (gapOffenders.Count > maxOffenders) { truncated = true; gapOffenders = gapOffenders.Take(maxOffenders).ToList(); }
                heuristics.Add("scale_conventions");
                findings["scale_conventions"] = new JObject
                {
                    ["heuristic"] = true,
                    ["doorOffenders"] = new JArray(doorOffenders),
                    ["lowGapOffenders"] = new JArray(gapOffenders)
                };
            }

            // unlit (heuristic).
            if (Enabled("unlit"))
            {
                var lightClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "light", "light_spot", "light_environment" };
                var lightOrigins = new List<Vector3>();
                foreach (var e in entities)
                {
                    var cn = e.Data.GetOne<EntityData>()?.Name;
                    if (cn != null && lightClasses.Contains(cn))
                    {
                        lightOrigins.Add(e.Data.GetOne<Origin>()?.Location ?? e.BoundingBox?.Center ?? Vector3.Zero);
                    }
                }
                var hasLightTexture = faces.Any(f => TextureSemantics.Parse(f.Face.Texture.Name).LightEmitting);
                var unlitMap = lightOrigins.Count == 0 && !hasLightTexture;

                var geometryCells = new HashSet<(int, int, int)>();
                foreach (var fr in faces)
                {
                    var c = fr.Face.Origin;
                    geometryCells.Add(((int)Math.Floor(c.X / cellSize), (int)Math.Floor(c.Y / cellSize), (int)Math.Floor(c.Z / cellSize)));
                }
                var possiblyUnlit = 0;
                foreach (var cell in geometryCells)
                {
                    var center = new Vector3((cell.Item1 + 0.5f) * cellSize, (cell.Item2 + 0.5f) * cellSize, (cell.Item3 + 0.5f) * cellSize);
                    if (!lightOrigins.Any(o => Vector3.Distance(o, center) <= lightRadius)) possiblyUnlit++;
                }
                heuristics.Add("unlit");
                findings["unlit"] = new JObject
                {
                    ["heuristic"] = true,
                    ["lightEntityCount"] = lightOrigins.Count,
                    ["hasLightEmittingTexture"] = hasLightTexture,
                    ["unlitMap"] = unlitMap,
                    ["possiblyUnlitCells"] = possiblyUnlit
                };
            }

            // missing_player_start.
            if (Enabled("missing_player_start"))
            {
                bool hasStart;
                var check = _problemChecks.Select(x => x.Value).FirstOrDefault(x => x.GetType().Name == "NoPlayerStart");
                if (check != null)
                {
                    var problems = await check.Check(doc, _ => true).ConfigureAwait(true);
                    hasStart = problems.Count == 0;
                }
                else
                {
                    hasStart = doc.Map.Root.Find(x => string.Equals(x.Data.GetOne<EntityData>()?.Name, "info_player_start", StringComparison.OrdinalIgnoreCase)).Any();
                }
                findings["missing_player_start"] = new JObject { ["hasPlayerStart"] = hasStart, ["missing"] = !hasStart };
            }

            // world_extents.
            if (Enabled("world_extents"))
            {
                var offenders = new List<JObject>();
                foreach (var o in objects)
                {
                    var bb = o.BoundingBox;
                    if (bb == null || bb.IsEmpty()) continue;
                    if (OutsideExtent(bb.Start, maxExtent) || OutsideExtent(bb.End, maxExtent))
                    {
                        offenders.Add(new JObject { ["objectId"] = o.ID, ["min"] = VecJson(bb.Start), ["max"] = VecJson(bb.End) });
                    }
                }
                if (offenders.Count > maxOffenders) { truncated = true; offenders = offenders.Take(maxOffenders).ToList(); }
                findings["world_extents"] = new JObject
                {
                    ["maxExtent"] = maxExtent,
                    ["outOfBoundsCount"] = offenders.Count,
                    ["offenders"] = new JArray(offenders)
                };
            }

            // wpoly_hotspots.
            if (Enabled("wpoly_hotspots"))
            {
                var cells = new Dictionary<(int, int, int), int>();
                foreach (var fr in faces)
                {
                    var c = fr.Face.Origin;
                    var cell = ((int)Math.Floor(c.X / cellSize), (int)Math.Floor(c.Y / cellSize), (int)Math.Floor(c.Z / cellSize));
                    cells[cell] = cells.TryGetValue(cell, out var v) ? v + 1 : 1;
                }
                var hot = cells.Where(x => x.Value > cellFaceThreshold)
                    .OrderByDescending(x => x.Value)
                    .Take(10)
                    .Select(x => new JObject
                    {
                        ["cell"] = new JObject { ["x"] = x.Key.Item1, ["y"] = x.Key.Item2, ["z"] = x.Key.Item3 },
                        ["center"] = VecJson(new Vector3((x.Key.Item1 + 0.5f) * cellSize, (x.Key.Item2 + 0.5f) * cellSize, (x.Key.Item3 + 0.5f) * cellSize)),
                        ["faceCount"] = x.Value,
                        ["label"] = "proxy"
                    })
                    .ToList();
                findings["wpoly_hotspots"] = new JObject
                {
                    ["cellSize"] = cellSize,
                    ["threshold"] = cellFaceThreshold,
                    ["hotspots"] = new JArray(hot)
                };
            }

            // includeProblemChecks.
            if (includeProblemChecks)
            {
                var problemResults = new JArray();
                foreach (var lazy in _problemChecks)
                {
                    var checker = lazy.Value;
                    var problems = await checker.Check(doc, _ => true).ConfigureAwait(true);
                    for (var i = 0; i < problems.Count; i++)
                    {
                        problemResults.Add(new JObject
                        {
                            ["checker"] = checker.GetType().FullName,
                            ["name"] = checker.Name,
                            ["index"] = i,
                            ["text"] = problems[i].Text,
                            ["objectIds"] = new JArray(problems[i].Objects.Select(x => x.ID))
                        });
                    }
                }
                findings["problem_checks"] = problemResults;
            }

            var checksRun = allChecks.Where(Enabled).ToList();
            return ToToken(new
            {
                checksRun,
                findings,
                referenceConventions = new
                {
                    doorWidth = "48-64",
                    minCeiling = 108,
                    maxStep = 16,
                    maxExtent = "±4096"
                },
                heuristics,
                warnings,
                truncated
            });
        }

        // ================= shared audit helpers =================

        private static bool IsWorldSolid(IMapObject obj)
        {
            var p = obj?.Hierarchy?.Parent;
            while (p != null)
            {
                if (p is Entity) return false;
                p = p.Hierarchy?.Parent;
            }
            return true;
        }

        private static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            var sorted = values.OrderBy(x => x).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
        }

        private static float AngleDegrees(Vector3 a, Vector3 b)
        {
            var na = a.Normalise();
            var nb = b.Normalise();
            var dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(na, nb)));
            return (float)(Math.Acos(dot) * 180.0 / Math.PI);
        }

        private static string QuantizedPlaneKey(Vector3 normal, float dist, bool unsigned)
        {
            var n = normal;
            if (unsigned)
            {
                // Fold to a canonical hemisphere so flipped normals share a key.
                if (n.X < 0 || (Math.Abs(n.X) < 1e-4f && n.Y < 0) || (Math.Abs(n.X) < 1e-4f && Math.Abs(n.Y) < 1e-4f && n.Z < 0))
                {
                    n = -n;
                    dist = -dist;
                }
            }
            var nx = Math.Round(n.X, 3);
            var ny = Math.Round(n.Y, 3);
            var nz = Math.Round(n.Z, 3);
            var dq = Math.Round(dist / 0.5) * 0.5;
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", nx, ny, nz, unsigned ? Math.Abs(dq) : dq);
        }

        private static float[] ProjectBox(Face face, Vector3 u, Vector3 v)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var vtx in face.Vertices)
            {
                var px = Vector3.Dot(vtx, u);
                var py = Vector3.Dot(vtx, v);
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
            }
            return new[] { minX, maxX, minY, maxY };
        }

        private static bool BoxesTouch(float[] a, float[] b, float tol)
        {
            return a[0] <= b[1] + tol && b[0] <= a[1] + tol && a[2] <= b[3] + tol && b[2] <= a[3] + tol;
        }

        private static bool BoxContains(float[] outer, float[] inner, float tol)
        {
            return outer[0] - tol <= inner[0] && outer[1] + tol >= inner[1]
                && outer[2] - tol <= inner[2] && outer[3] + tol >= inner[3];
        }

        private static float[] XyBox(Face face)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var vtx in face.Vertices)
            {
                if (vtx.X < minX) minX = vtx.X;
                if (vtx.X > maxX) maxX = vtx.X;
                if (vtx.Y < minY) minY = vtx.Y;
                if (vtx.Y > maxY) maxY = vtx.Y;
            }
            return new[] { minX, maxX, minY, maxY };
        }

        private static float PolygonArea(Face face)
        {
            var verts = face.Vertices.ToList();
            if (verts.Count < 3) return 0f;
            var sum = Vector3.Zero;
            for (var i = 0; i < verts.Count; i++)
            {
                var a = verts[i];
                var b = verts[(i + 1) % verts.Count];
                sum += Vector3.Cross(a, b);
            }
            return sum.Length() / 2f;
        }

        private static int LargestGridGranularity(Solid solid)
        {
            var coords = solid.Faces.SelectMany(f => f.Vertices).SelectMany(v => new[] { v.X, v.Y, v.Z }).ToList();
            if (coords.Count == 0) return 0;
            foreach (var g in new[] { 64, 32, 16, 8, 4, 2, 1 })
            {
                if (coords.All(c => Math.Abs(c - (float)Math.Round(c / g) * g) < 0.01f)) return g;
            }
            return 0; // fractional (g < 1)
        }

        private static bool OutsideExtent(Vector3 v, float maxExtent)
        {
            return Math.Abs(v.X) > maxExtent || Math.Abs(v.Y) > maxExtent || Math.Abs(v.Z) > maxExtent;
        }

        private static JObject VecJson(Vector3 v)
        {
            return new JObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };
        }
    }
}
