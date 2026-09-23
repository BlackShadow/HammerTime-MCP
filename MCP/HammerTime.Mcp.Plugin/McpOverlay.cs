using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using HammerTime.Mcp.Shared;
using LogicAndTrick.Oy;
using Sledge.BspEditor.Documents;
using Sledge.BspEditor.Primitives.MapObjects;
using Sledge.BspEditor.Rendering.Overlay;
using Sledge.Common.Shell.Documents;
using Sledge.DataStructures.Geometric;
using Sledge.Rendering.Cameras;
using Sledge.Rendering.Overlay;
using Sledge.Rendering.Viewports;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>
    /// Highlight boxes and the leak path the MCP tools draw into viewports. The overlay is engine-global (it is
    /// asked to render into every viewport), so its state is kept per document and only the entry of the
    /// editor's active document is drawn. Switching tabs shows each document's own marks; closing a document
    /// drops its entry.
    /// </summary>
    [Export]
    [Export(typeof(IMapDocumentOverlayRenderable))]
    public sealed class McpOverlay : IMapDocumentOverlayRenderable
    {
        private sealed class Entry
        {
            public List<Vector3> Path = new List<Vector3>();
            public string PathLabel;
            public HashSet<long> HighlightIds = new HashSet<long>();
            public string HighlightLabel;
            /// <summary>Which label was set last; the newest one is drawn.</summary>
            public bool PathLabelIsNewest;
            // Highlight box cache: rebuilt when the highlight set changes instead of resolving
            // FindByID/BoundingBox per highlight per frame per viewport.
            public int Version;
            public int CachedVersion = -1;
            public List<Box> CachedBoxes = new List<Box>();
        }

        private readonly object _sync = new object();
        private readonly Dictionary<MapDocument, Entry> _entries = new Dictionary<MapDocument, Entry>();
        private MapDocument _activeDocument;

        public McpOverlay()
        {
            Oy.Subscribe<IDocument>("Document:Activated", DocumentActivated);
            Oy.Subscribe<IDocument>("Document:Closed", DocumentClosed);
        }

        /// <summary>
        /// Seed the active document. Activation is tracked from "Document:Activated", but a document that
        /// was activated before this overlay was created would otherwise be missed.
        /// </summary>
        public void SetActiveDocument(MapDocument doc)
        {
            lock (_sync) _activeDocument = doc;
        }

        public void SetLeakPath(MapDocument document, IEnumerable<Vector3Dto> points, string label)
        {
            if (document == null) return;
            lock (_sync)
            {
                var entry = EntryFor(document);
                entry.Path = points?.Select(x => new Vector3(x.X, x.Y, x.Z)).ToList() ?? new List<Vector3>();
                entry.PathLabel = label;
                entry.PathLabelIsNewest = true;
            }
        }

        /// <summary>Set the leak path of the active document.</summary>
        public void SetLeakPath(IEnumerable<Vector3Dto> points, string label)
        {
            MapDocument document;
            lock (_sync) document = _activeDocument;
            SetLeakPath(document, points, label);
        }

        public void SetHighlights(MapDocument document, IEnumerable<long> ids, string label)
        {
            if (document == null) return;
            lock (_sync)
            {
                var entry = EntryFor(document);
                entry.HighlightIds = new HashSet<long>(ids ?? Enumerable.Empty<long>());
                entry.HighlightLabel = label;
                entry.PathLabelIsNewest = false;
                entry.Version++;
            }
        }

        /// <summary>Clear the highlights and leak path of <paramref name="document"/>, or of every document when null.</summary>
        public void Clear(MapDocument document)
        {
            lock (_sync)
            {
                if (document == null) _entries.Clear();
                else _entries.Remove(document);
            }
        }

        /// <summary>Clear the highlights and leak paths of every document.</summary>
        public void Clear()
        {
            Clear(null);
        }

        private Task DocumentActivated(IDocument document)
        {
            // A non-map document (or none) is published as the active document too; draw nothing then.
            lock (_sync) _activeDocument = document as MapDocument;
            return Task.CompletedTask;
        }

        private Task DocumentClosed(IDocument document)
        {
            if (document is MapDocument closed)
            {
                lock (_sync)
                {
                    _entries.Remove(closed);
                    if (ReferenceEquals(_activeDocument, closed)) _activeDocument = null;
                }
            }
            return Task.CompletedTask;
        }

        private Entry EntryFor(MapDocument document)
        {
            if (!_entries.TryGetValue(document, out var entry))
            {
                entry = new Entry();
                _entries[document] = entry;
            }
            return entry;
        }

        public void Render(IViewport viewport, OrthographicCamera camera, Vector3 worldMin, Vector3 worldMax, I2DRenderer im)
        {
            if (!TrySnapshot(out var path, out var boxes, out var label)) return;

            RenderPath(path, p => camera.WorldToScreen(p), _ => true, im);
            RenderBoxes(boxes, p => camera.WorldToScreen(p), _ => true, im);
            RenderLabel(label, LabelAnchor(path, boxes), p => camera.WorldToScreen(p), _ => true, im);
        }

        public void Render(IViewport viewport, PerspectiveCamera camera, I2DRenderer im)
        {
            if (!TrySnapshot(out var path, out var boxes, out var label)) return;

            // Points behind the camera project to finite-but-mirrored screen coords, drawing
            // spurious streaks. Reject any world point that is not in front of the camera.
            var position = camera.Position;
            var direction = camera.Direction;
            Func<Vector3, bool> isInFront = w => Vector3.Dot(w - position, direction) > 0;

            RenderPath(path, p => camera.WorldToScreen(p), isInFront, im);
            RenderBoxes(boxes, p => camera.WorldToScreen(p), isInFront, im);
            RenderLabel(label, LabelAnchor(path, boxes), p => camera.WorldToScreen(p), isInFront, im);
        }

        /// <summary>Where the label sits: the start of the leak path, else the first highlight box.</summary>
        private static Vector3? LabelAnchor(IReadOnlyList<Vector3> path, IReadOnlyList<Box> boxes)
        {
            if (path.Count > 0) return path[0];
            if (boxes.Count > 0) return boxes[0].Center;
            return null;
        }

        private static void RenderLabel(string label, Vector3? anchor, Func<Vector3, Vector3> project, Func<Vector3, bool> isWorldVisible, I2DRenderer im)
        {
            if (string.IsNullOrWhiteSpace(label) || anchor == null || !isWorldVisible(anchor.Value)) return;
            var screen = project(anchor.Value);
            if (IsScreenPointUsable(screen))
            {
                im.AddText(new Vector2(screen.X + 6, screen.Y + 6), Color.FromArgb(230, 255, 230, 150), FontType.Normal, label);
            }
        }

        /// <summary>The state to draw for the active document; false when there is nothing.</summary>
        private bool TrySnapshot(out List<Vector3> path, out List<Box> boxes, out string label)
        {
            path = null;
            boxes = null;
            label = null;
            lock (_sync)
            {
                var document = _activeDocument;
                if (document == null || !_entries.TryGetValue(document, out var entry)) return false;
                if (entry.CachedVersion != entry.Version)
                {
                    // NOTE: moving a highlighted object will NOT refresh its box until highlights are
                    // re-set via SetHighlights — an accepted tradeoff to avoid per-frame resolution.
                    entry.CachedVersion = entry.Version;
                    entry.CachedBoxes = entry.HighlightIds
                        .Select(id => document.Map.Root.FindByID(id)?.BoundingBox)
                        .Where(x => x != null && !x.IsEmpty())
                        .ToList();
                }
                path = entry.Path.ToList();
                boxes = entry.CachedBoxes;
                label = entry.PathLabelIsNewest ? entry.PathLabel ?? entry.HighlightLabel : entry.HighlightLabel ?? entry.PathLabel;
                return path.Count > 0 || boxes.Count > 0;
            }
        }

        private static void RenderPath(IReadOnlyList<Vector3> path, Func<Vector3, Vector3> project, Func<Vector3, bool> isWorldVisible, I2DRenderer im)
        {
            if (path.Count < 2) return;
            var color = Color.FromArgb(230, 255, 80, 80);
            for (var i = 1; i < path.Count; i++)
            {
                if (!isWorldVisible(path[i - 1]) || !isWorldVisible(path[i])) continue;
                var a = project(path[i - 1]);
                var b = project(path[i]);
                if (!IsScreenPointUsable(a) || !IsScreenPointUsable(b)) continue;
                im.AddLine(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), color, 2.0f);
            }
        }

        private static void RenderBoxes(IEnumerable<Box> boxes, Func<Vector3, Vector3> project, Func<Vector3, bool> isWorldVisible, I2DRenderer im)
        {
            var color = Color.FromArgb(230, 255, 210, 0);
            foreach (var box in boxes)
            {
                foreach (var line in box.GetBoxLines())
                {
                    if (!isWorldVisible(line.Start) || !isWorldVisible(line.End)) continue;
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
