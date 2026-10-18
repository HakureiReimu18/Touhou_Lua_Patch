using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.WeaponBadges
{
    internal sealed class BadgeRule
    {
        internal string Tag;
        internal string File;
        internal int Level;
        internal int FrameWidth;
        internal int FrameHeight;
        internal int FrameCount = 1;
        internal double Fps = 10;
    }

    internal sealed class BadgeConfig
    {
        internal int Size = 46;
        internal bool PreviewBadges = true;
        internal readonly List<BadgeRule> Rules = new List<BadgeRule>();

        internal static BadgeConfig Load(string packageDir)
        {
            XElement root = XDocument.Load(Path.Combine(packageDir, "Config", "Badges.xml")).Root;
            var result = new BadgeConfig();
            result.Size = Math.Clamp(Int(root, "size", 46), 24, 128);
            result.PreviewBadges = !string.Equals((string)root.Attribute("previewBadges"), "false", StringComparison.OrdinalIgnoreCase);
            foreach (XElement e in root.Elements("Badge"))
            {
                string tag = (string)e.Attribute("tag");
                string file = (string)e.Attribute("file");
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(file)) { continue; }
                result.Rules.Add(new BadgeRule
                {
                    Tag = tag.Trim(), File = Path.GetFullPath(Path.Combine(packageDir, file)),
                    Level = Int(e, "level", 0), FrameWidth = Int(e, "frameWidth", 0),
                    FrameHeight = Int(e, "frameHeight", 0), FrameCount = Math.Max(1, Int(e, "frames", 1)),
                    Fps = double.TryParse((string)e.Attribute("fps"), NumberStyles.Float, CultureInfo.InvariantCulture, out double fps) && fps > 0 && double.IsFinite(fps) ? fps : 10
                });
            }
            result.Rules.Sort((a, b) => b.Level.CompareTo(a.Level));
            return result;
        }

        private static int Int(XElement e, string key, int fallback)
            => int.TryParse((string)e.Attribute(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        internal BadgeRule Match(Barotrauma.Item item, Barotrauma.ItemPrefab prefab)
        {
            // Item.HasTag includes prefab tags and tags added to the live item by Lua/C#.
            return Rules.FirstOrDefault(r => item != null ? item.HasTag(new Barotrauma.Identifier(r.Tag)) : prefab != null && prefab.Tags.Contains(new Barotrauma.Identifier(r.Tag)));
        }
    }
}
