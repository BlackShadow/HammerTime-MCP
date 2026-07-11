using System.Collections.Generic;

namespace HammerTime.Mcp.Shared
{
    /// <summary>
    /// GoldSrc/Half-Life texture-naming conventions parsed purely from the name.
    /// No editor dependency, so the test project can reference it. Case-insensitive.
    /// </summary>
    public sealed class TextureSemanticInfo
    {
        public string Name { get; set; }
        public string Basename { get; set; }
        public string Family { get; set; }

        public bool Transparent { get; set; }
        public bool Liquid { get; set; }
        public bool LightEmitting { get; set; }
        public bool Animated { get; set; }
        public bool ToggleFrame { get; set; }
        public bool RandomTiling { get; set; }
        public bool Scrolling { get; set; }
        public bool Sky { get; set; }

        public int? Frame { get; set; }
        public string Tool { get; set; }
        public bool Special { get; set; }

        /// <summary>Human-readable flag strings for the parsed conventions.</summary>
        public IReadOnlyList<string> Flags
        {
            get
            {
                var list = new List<string>();
                if (Transparent) list.Add("transparent");
                if (Liquid) list.Add("liquid");
                if (LightEmitting) list.Add("lightEmitting");
                if (Animated) list.Add("animated");
                if (ToggleFrame) list.Add("toggle");
                if (RandomTiling) list.Add("randomTiling");
                if (Scrolling) list.Add("scrolling");
                if (Sky) list.Add("sky");
                if (Tool != null) list.Add("tool:" + Tool);
                return list;
            }
        }
    }

    public static class TextureSemantics
    {
        public static TextureSemanticInfo Parse(string name)
        {
            var info = new TextureSemanticInfo { Name = name ?? string.Empty };
            var work = info.Name;
            if (string.IsNullOrEmpty(work))
            {
                info.Basename = string.Empty;
                info.Family = string.Empty;
                return info;
            }

            // Prefix conventions (stripped to form the basename).
            var first = work[0];
            if (first == '{')
            {
                info.Transparent = true;
                work = work.Substring(1);
            }
            else if (first == '!')
            {
                info.Liquid = true;
                work = work.Substring(1);
            }
            else if (first == '~')
            {
                info.LightEmitting = true;
                work = work.Substring(1);
            }
            else if (first == '+' && work.Length >= 2)
            {
                var c = work[1];
                if (c >= '0' && c <= '9')
                {
                    info.Animated = true;
                    info.Frame = c - '0';
                    work = work.Substring(2);
                }
                else if ((c >= 'A' && c <= 'J') || (c >= 'a' && c <= 'j'))
                {
                    info.Animated = true;
                    info.ToggleFrame = true;
                    info.Frame = char.ToUpperInvariant(c) - 'A';
                    work = work.Substring(2);
                }
            }
            else if (first == '-' && work.Length >= 2)
            {
                var c = work[1];
                if (c >= '0' && c <= '9')
                {
                    info.RandomTiling = true;
                    info.Frame = c - '0';
                    work = work.Substring(2);
                }
            }

            info.Basename = work;

            var lowerBase = work.ToLowerInvariant();
            var lowerFull = info.Name.ToLowerInvariant();

            if (lowerBase.Contains("water") || lowerFull.Contains("water")) info.Liquid = true;
            if (lowerBase.StartsWith("sky")) info.Sky = true;
            if (lowerBase.StartsWith("scroll")) info.Scrolling = true;
            if (lowerBase.StartsWith("clip")) info.Tool = "clip";

            switch (lowerBase)
            {
                case "aaatrigger":
                case "origin":
                case "hint":
                case "skip":
                case "null":
                case "bevel":
                case "trigger":
                    info.Tool = lowerBase;
                    break;
            }

            info.Special = info.Sky || info.Tool != null;
            info.Family = StripTrailingDigits(work);
            return info;
        }

        // family = basename with the trailing digit run removed; keep basename if that empties it.
        private static string StripTrailingDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var end = value.Length;
            while (end > 0 && value[end - 1] >= '0' && value[end - 1] <= '9') end--;
            return end == 0 ? value : value.Substring(0, end);
        }
    }
}
