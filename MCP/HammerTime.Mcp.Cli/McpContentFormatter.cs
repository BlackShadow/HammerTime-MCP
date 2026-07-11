using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HammerTime.Mcp.Cli
{
    public static class McpContentFormatter
    {
        private const string ImageOutputDirectoryEnvironmentVariable = "HAMMERTIME_MCP_IMAGE_OUTPUT_DIR";
        private const int MaxCaptureFiles = 200;
        private static readonly string[] CaptureImageExtensions = { ".png", ".jpg", ".gif", ".webp", ".bmp" };
        private static readonly HashSet<string> PrunedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object PruneLock = new object();

        public static JObject CreateToolResult(JToken result)
        {
            var structuredSource = AddImageFilePaths(result);
            var structured = StripImageData(structuredSource) ?? JValue.CreateNull();
            var content = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = structured.ToString(Formatting.Indented)
                }
            };

            foreach (var image in FindImages(result))
            {
                var data = image.Value<string>("data");
                var mimeType = image.Value<string>("mimeType") ?? "image/png";
                if (string.IsNullOrWhiteSpace(data)) continue;
                if (!mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;

                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["mimeType"] = mimeType,
                    ["data"] = data
                });
            }

            return new JObject
            {
                ["content"] = content,
                ["structuredContent"] = structured
            };
        }

        private static JToken AddImageFilePaths(JToken token)
        {
            if (token == null) return null;
            var clone = token.DeepClone();
            AddImageFilePathsInPlace(clone);
            return clone;
        }

        public static JToken StripImageData(JToken token)
        {
            if (token == null) return null;
            var clone = token.DeepClone();
            StripImageDataInPlace(clone);
            return clone;
        }

        private static IEnumerable<JObject> FindImages(JToken token)
        {
            if (!(token is JObject obj)) yield break;

            if (obj["image"] is JObject image)
            {
                yield return image;
            }

            if (obj["images"] is JArray images)
            {
                foreach (var entry in images.OfType<JObject>())
                {
                    yield return entry;
                }
            }
        }

        private static void AddImageFilePathsInPlace(JToken token)
        {
            if (token is JObject obj)
            {
                var mimeType = obj.Value<string>("mimeType");
                var data = obj.Value<string>("data");
                if (!string.IsNullOrWhiteSpace(mimeType) &&
                    mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(data) &&
                    obj["filePath"] == null)
                {
                    var filePath = TryWriteImageData(obj.Value<string>("name"), mimeType, data);
                    if (!string.IsNullOrWhiteSpace(filePath))
                    {
                        obj["filePath"] = filePath;
                    }
                }

                foreach (var child in obj.Properties().Select(x => x.Value).ToList())
                {
                    AddImageFilePathsInPlace(child);
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array.ToList())
                {
                    AddImageFilePathsInPlace(child);
                }
            }
        }

        private static void StripImageDataInPlace(JToken token)
        {
            if (token is JObject obj)
            {
                var mimeType = obj.Value<string>("mimeType");
                if (!string.IsNullOrWhiteSpace(mimeType) &&
                    mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    obj["data"] != null)
                {
                    obj.Remove("data");
                    obj["dataOmitted"] = true;
                }

                foreach (var child in obj.Properties().Select(x => x.Value).ToList())
                {
                    StripImageDataInPlace(child);
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array.ToList())
                {
                    StripImageDataInPlace(child);
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

        private static void PruneCaptureDirectory(string directory)
        {
            // Prune each directory at most once per process run to avoid rescanning on
            // every capture. Register before pruning so a failure does not force retries.
            lock (PruneLock)
            {
                if (!PrunedDirectories.Add(directory)) return;
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
