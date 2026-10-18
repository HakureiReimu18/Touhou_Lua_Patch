using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace Touhou.WeaponBadges
{
    internal sealed class SweepProfile
    {
        internal string Id = string.Empty;
        internal Color Color = Microsoft.Xna.Framework.Color.White;
        internal float Intensity = .22f;
        internal float Period = 5f;
        internal float Width = .14f;
        internal float Slant = .35f;
        internal float Glow = .03f;
        internal float Pulse = .08f;
        internal float Rainbow;
        internal int Bands = 1;
        internal int Sparkles;
    }

    internal sealed class SweepRule
    {
        internal string Tag = string.Empty;
        internal int Priority;
        internal SweepProfile Profile;
    }

    internal sealed class SweepConfig
    {
        internal bool Enabled = true, Inventory = true, Previews = true, World = true;
        internal float WorldIntensity = .65f;
        internal int CacheMegabytes = 32;
        internal AffixGlowConfig AffixGlow = new AffixGlowConfig();
        internal readonly List<SweepRule> Rules = new List<SweepRule>();

        internal static SweepConfig Load(string packageDir)
        {
            string path = Path.Combine(packageDir, "Config", "ItemVisualEffects.xml");
            var result = new SweepConfig();
            if (!File.Exists(path))
            {
                result.Enabled = false;
                DebugConsole.NewMessage($"[Touhou.WeaponBadges] 找不到扫光配置 {path}，已禁用扫光效果；把 ItemVisualEffects.xml 放回 Config/ 后重启生效。", Color.Yellow);
                return result;
            }

            XElement root = XDocument.Load(path).Root;
            if (root == null || root.Name != "ItemVisualEffects")
            { throw new FormatException($"{path}: 根元素必须是 <ItemVisualEffects>。"); }

            result.Enabled = Bool(root, "enabled", result.Enabled);
            result.Inventory = Bool(root, "inventory", result.Inventory);
            result.Previews = Bool(root, "previews", result.Previews);
            result.World = Bool(root, "world", result.World);
            result.WorldIntensity = Float(root, "worldIntensity", result.WorldIntensity, 0f, 1f);
            result.CacheMegabytes = Int(root, "cacheMegabytes", result.CacheMegabytes, 1, 256);
            result.AffixGlow = AffixGlowConfig.Load(root, path);

            var profiles = new Dictionary<string, SweepProfile>(StringComparer.Ordinal);
            foreach (XElement e in root.Element("Profiles")?.Elements("Profile") ?? Enumerable.Empty<XElement>())
            {
                var profile = new SweepProfile { Id = Required(e, "id", path) };
                if (profiles.ContainsKey(profile.Id))
                { throw new FormatException($"{path}: 重复的 Profile id='{profile.Id}'。"); }
                profile.Color = ReadColor(e, profile.Color, path);
                // Normalized amounts keep the sprite readable; period is in seconds,
                // width is a fraction of the sprite and slant is horizontal/vertical slope.
                // Glow is a persistent inner-contour tint; Pulse modulates its strength,
                // Rainbow blends its color. These remain independent of Bands/Sparkles.
                profile.Intensity = Float(e, "intensity", profile.Intensity, 0f, 1f);
                profile.Period = Float(e, "period", profile.Period, .5f, 60f);
                profile.Width = Float(e, "width", profile.Width, .02f, .5f);
                profile.Slant = Float(e, "slant", profile.Slant, -2f, 2f);
                profile.Glow = Float(e, "glow", profile.Glow, 0f, .25f);
                profile.Pulse = Float(e, "pulse", profile.Pulse, 0f, 1f);
                profile.Rainbow = Float(e, "rainbow", profile.Rainbow, 0f, 1f);
                profile.Bands = Int(e, "bands", profile.Bands, 1, 2);
                profile.Sparkles = Int(e, "sparkles", profile.Sparkles, 0, 8);
                profiles.Add(profile.Id, profile);
            }

            foreach (XElement e in root.Element("Rules")?.Elements("Rule") ?? Enumerable.Empty<XElement>())
            {
                string tag = Required(e, "tag", path);
                string id = Required(e, "profile", path);
                if (!profiles.TryGetValue(id, out SweepProfile profile))
                { throw new FormatException($"{path}: Rule tag='{tag}' 引用了不存在的 Profile '{id}'。"); }
                result.Rules.Add(new SweepRule
                {
                    Tag = tag, Profile = profile,
                    Priority = Int(e, "priority", 0, int.MinValue, int.MaxValue)
                });
            }
            // OrderByDescending is stable: equal priorities retain XML order.
            SweepRule[] sorted = result.Rules.OrderByDescending(r => r.Priority).ToArray();
            result.Rules.Clear();
            result.Rules.AddRange(sorted);
            return result;
        }

        internal SweepProfile Match(Item item, ItemPrefab prefab)
        {
            if (!Enabled) { return null; }
            foreach (SweepRule rule in Rules)
            {
                var tag = new Identifier(rule.Tag);
                // Read live tags on every match, including tags added/removed by Lua/C#.
                // Only use prefab tags when no live item exists (e.g. recipe previews).
                if (item != null ? item.HasTag(tag) : prefab != null && prefab.Tags.Contains(tag))
                { return rule.Profile; }
            }
            return null;
        }

        private static string Required(XElement e, string key, string path)
        {
            string value = ((string)e.Attribute(key))?.Trim();
            if (string.IsNullOrEmpty(value))
            { throw new FormatException($"{path}: <{e.Name}> 缺少非空属性 '{key}'。"); }
            return value;
        }

        private static bool Bool(XElement e, string key, bool fallback)
            => bool.TryParse((string)e.Attribute(key), out bool value) ? value : fallback;

        // Invalid/non-finite numbers use field defaults; finite numbers are clamped.
        // Extend profiles here and document the same defaults/ranges in the XML.
        private static float Float(XElement e, string key, float fallback, float min, float max)
            => float.TryParse((string)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        private static int Int(XElement e, string key, int fallback, int min, int max)
            => int.TryParse((string)e.Attribute(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? Math.Clamp(value, min, max) : fallback;

        private static Color ReadColor(XElement e, Color fallback, string path)
        {
            string value = ((string)e.Attribute("color"))?.Trim();
            if (value == null) { return fallback; }
            if (value.Length != 7 || value[0] != '#' ||
                !uint.TryParse(value.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            { throw new FormatException($"{path}: Profile '{(string)e.Attribute("id")}' 的 color 必须为 #rrggbb，实际为 '{value}'。"); }
            return new Color((int)((rgb >> 16) & 255), (int)((rgb >> 8) & 255), (int)(rgb & 255));
        }
    }
}
