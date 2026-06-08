using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Linq;
using System.Numerics;
using HammerTime.Mcp.Shared;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Rendering.Overlay;
using Sledge.DataStructures.Geometric;
using Sledge.Rendering.Cameras;
using Sledge.Rendering.Overlay;
using Sledge.Rendering.Viewports;

namespace HammerTime.Mcp.Plugin
{
    [Export]
    [Export(typeof(IMapDocumentOverlayRenderable))]
    public sealed class McpOverlay : IMapDocumentOverlayRenderable
    {
        private readonly object _sync = new object();
        private MapDocument _document;
        private List<Vector3> _path = new List<Vector3>();
        private HashSet<long> _highlightIds = new HashSet<long>();
        private string _label;

        public void SetActiveDocument(MapDocument doc)
        {
            lock (_sync) _document = doc;
        }

        public void SetLeakPath(IEnumerable<Vector3Dto> points, string label)
        {
            lock (_sync)
            {
                _path = points?.Select(x => new Vector3(x.X, x.Y, x.Z)).ToList() ?? new List<Vector3>();
                _label = label;
            }
        }

        public void SetHighlights(IEnumerable<long> ids, string label)
        {
            lock (_sync)
            {
                _highlightIds = new HashSet<long>(ids ?? Enumerable.Empty<long>());
                _label = label;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _path.Clear();
                _highlightIds.Clear();
                _label = null;
            }
        }

        public void Render(IViewport viewport, OrthographicCamera camera, Vector3 worldMin, Vector3 worldMax, I2DRenderer im)
        {
            List<Vector3> path;
            List<Box> boxes;
            string label;
            Snapshot(out path, out boxes, out label);

            RenderPath(path, p => camera.WorldToScreen(p), im);
            RenderBoxes(boxes, p => camera.WorldToScreen(p), im);

            if (!string.IsNullOrWhiteSpace(label) && path.Count > 0)
            {
                var screen = camera.WorldToScreen(path[0]);
                im.AddText(new Vector2(screen.X + 6, screen.Y + 6), Color.FromArgb(230, 255, 230, 150), FontType.Normal, label);
            }
        }

        public void Render(IViewport viewport, PerspectiveCamera camera, I2DRenderer im)
        {
            List<Vector3> path;
            List<Box> boxes;
            string label;
            Snapshot(out path, out boxes, out label);

            RenderPath(path, p => camera.WorldToScreen(p), im);
            RenderBoxes(boxes, p => camera.WorldToScreen(p), im);

            if (!string.IsNullOrWhiteSpace(label) && path.Count > 0)
            {
                var screen = camera.WorldToScreen(path[0]);
                if (IsScreenPointUsable(screen))
                {
                    im.AddText(new Vector2(screen.X + 6, screen.Y + 6), Color.FromArgb(230, 255, 230, 150), FontType.Normal, label);
                }
            }
        }

        private void Snapshot(out List<Vector3> path, out List<Box> boxes, out string label)
        {
            lock (_sync)
            {
                path = _path.ToList();
                label = _label;
                var doc = _document;
                boxes = doc == null
                    ? new List<Box>()
                    : _highlightIds
                        .Select(id => doc.Map.Root.FindByID(id)?.BoundingBox)
                        .Where(x => x != null && !x.IsEmpty())
                        .ToList();
            }
        }

        private static void RenderPath(IReadOnlyList<Vector3> path, Func<Vector3, Vector3> project, I2DRenderer im)
        {
            if (path.Count < 2) return;
            var color = Color.FromArgb(230, 255, 80, 80);
            for (var i = 1; i < path.Count; i++)
            {
                var a = project(path[i - 1]);
                var b = project(path[i]);
                if (!IsScreenPointUsable(a) || !IsScreenPointUsable(b)) continue;
                im.AddLine(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), color, 2.0f);
            }
        }

        private static void RenderBoxes(IEnumerable<Box> boxes, Func<Vector3, Vector3> project, I2DRenderer im)
        {
            var color = Color.FromArgb(230, 255, 210, 0);
            foreach (var box in boxes)
            {
                foreach (var line in box.GetBoxLines())
                {
                    var a = project(line.Start);
                    var b = project(line.End);
                    if (!IsScreenPointUsable(a) || !IsScreenPointUsable(b)) continue;
                    im.AddLine(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), color, 1.5f);
                }
            }
        }

        private static bool IsScreenPointUsable(Vector3 point)
        {
            return !float.IsNaN(point.X) && !float.IsNaN(point.Y) &&
                   !float.IsInfinity(point.X) && !float.IsInfinity(point.Y);
        }
    }
}
