using System;
using System.IO;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>
    /// The bridge's own log file (<c>bridge.log</c> next to the config): one line per request and per error.
    /// Appends are serialised (pipe handlers log concurrently) and the file rolls over to <c>bridge.log.1</c>
    /// at the size cap, so a long-lived install never grows it without bound.
    /// </summary>
    internal static class BridgeLog
    {
        public const long DefaultMaxBytes = 1024 * 1024;
        private static readonly object Sync = new object();

        public static void Append(string path, string message, long maxBytes = DefaultMaxBytes)
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var info = new FileInfo(path);
                if (info.Exists && info.Length >= maxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            }
        }
    }
}
