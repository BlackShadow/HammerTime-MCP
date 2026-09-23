using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Cli
{
    /// <summary>
    /// Turns a bridge result into an MCP tool result: the JSON as a text block plus one image block per image
    /// found anywhere in it (objects with an <c>image/*</c> mimeType and base64 data). The structured copy
    /// carries every image as a file path instead of the base64 payload so it stays small.
    /// </summary>
    public static class McpContentFormatter
    {
        private const string ImageOutputDirectoryEnvironmentVariable = "HAMMERTIME_MCP_IMAGE_OUTPUT_DIR";
        private const int MaxCaptureFiles = 200;
        private const int PruneEveryWrites = 25;
        private static readonly string[] CaptureImageExtensions = { ".png", ".jpg", ".gif", ".webp", ".bmp" };
        private static readonly object PruneLock = new object();
        private static int _writesSincePrune = PruneEveryWrites; // prune on the first write of the process

        public static JObject CreateToolResult(JToken result)
        {
            var images = new List<(string MimeType, string Data)>();
            var structured = StructuredCopy(result, images);

            var content = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = structured.ToString(Formatting.Indented)
                }
            };
            foreach (var image in images)
            {
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["mimeType"] = image.MimeType,
                    ["data"] = image.Data
                });
            }

            return new JObject
            {
                ["content"] = content,
                ["structuredContent"] = structured
            };
        }

        /// <summary>
        /// A copy of the result that is always an object (MCP requires structuredContent to be one), with
        /// image payloads replaced by file paths. Images found at any depth are collected into <paramref name="images"/>.
        /// </summary>
        private static JObject StructuredCopy(JToken result, List<(string MimeType, string Data)> images)
        {
            var clone = result == null || result.Type == JTokenType.Null ? new JObject() : result.DeepClone();
            var structured = clone as JObject ?? new JObject { ["value"] = clone };
            CollectAndStripImages(structured, images);
            return structured;
        }

        private static void CollectAndStripImages(JToken token, List<(string MimeType, string Data)> images)
        {
            if (token is JObject obj)
            {
                var mimeType = obj.Value<string>("mimeType");
                var data = obj.Value<string>("data");
                if (!string.IsNullOrWhiteSpace(mimeType) &&
                    mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(data))
                {
                    images.Add((mimeType, data));
                    if (obj["filePath"] == null)
                    {
                        var filePath = TryWriteImageData(obj.Value<string>("name"), mimeType, data);
                        if (!string.IsNullOrWhiteSpace(filePath)) obj["filePath"] = filePath;
                    }
                    obj.Remove("data");
                    obj["dataOmitted"] = true;
                }

                foreach (var child in obj.Properties().Select(x => x.Value).ToList())
                {
                    CollectAndStripImages(child, images);
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array.ToList())
                {
                    CollectAndStripImages(child, images);
                }
            }
        }

        private static string TryWriteImageData(string name, string mimeType, string data)
        {
            try
            {
                var directory = Environment.GetEnvironmentVariable(ImageOutputDirectoryEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = Path.Combine(Path.GetTempPath(), "HammerTime.MCP", "captures");
                }

                Directory.CreateDirectory(directory);
                var fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
                               Guid.NewGuid().ToString("N") + "-" +
                               SafeFileName(name) + ExtensionForMimeType(mimeType);
                var filePath = Path.Combine(directory, fileName);
                File.WriteAllBytes(filePath, Convert.FromBase64String(data));
                PruneCaptureDirectory(directory);
                return filePath;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Keep the newest <see cref="MaxCaptureFiles"/> images. Checked on the first write and then every
        /// <see cref="PruneEveryWrites"/> writes, so a long-running server does not grow the directory unbounded
        /// but also does not rescan it on every capture.
        /// </summary>
        private static void PruneCaptureDirectory(string directory)
        {
            lock (PruneLock)
            {
                if (++_writesSincePrune < PruneEveryWrites) return;
                _writesSincePrune = 0;
            }

            try
            {
                var stale = Directory.EnumerateFiles(directory)
                    .Where(path => CaptureImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Skip(MaxCaptureFiles)
                    .ToList();

                foreach (var file in stale)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Swallow per-file IO errors (locked/removed files).
                    }
                }
            }
            catch
            {
                // Directory enumeration failed; nothing to prune.
            }
        }

        private static string SafeFileName(string name)
        {
            var safe = string.IsNullOrWhiteSpace(name) ? "image" : name.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(invalid, '-');
            }

            return safe.Length <= 64 ? safe : safe.Substring(0, 64);
        }

        private static string ExtensionForMimeType(string mimeType)
        {
            if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            if (string.Equals(mimeType, "image/gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            if (string.Equals(mimeType, "image/webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (string.Equals(mimeType, "image/bmp", StringComparison.OrdinalIgnoreCase)) return ".bmp";
            return ".png";
        }
    }
}
