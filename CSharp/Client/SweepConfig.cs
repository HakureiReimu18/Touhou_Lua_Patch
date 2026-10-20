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
    internal enum EffectSurface { Inventory, Preview, World }

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
        internal ArtEffectProfile ArtEffect;
    }

    internal sealed class SweepRule
    {
        internal string Tag = string.Empty;
        internal Identifier ItemIdentifier;
        internal bool HasItemIdentifier;
        internal int Priority;
        internal SweepProfile Profile;
        internal SweepProfile[] Profiles;
        internal SweepProfile[] InventoryProfiles, PreviewProfiles, WorldProfiles;
    }

    internal sealed class SweepConfig
    {
        internal bool Enabled = true, Inventory = true, Previews = true, World = true;
        internal float WorldIntensity = .65f;
        internal int CacheMegabytes = 32;
        internal AffixGlowConfig AffixGlow = new AffixGlowConfig();
        internal readonly Dictionary<string, SweepProfile> Profiles = new Dictionary<string, SweepProfile>(StringComparer.Ordinal);
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

            var profiles = result.Profiles;
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
                profile.ArtEffect = ArtEffectProfile.Load(e, path);
                profiles.Add(profile.Id, profile);
            }

            foreach (XElement e in root.Element("Rules")?.Elements("Rule") ?? Enumerable.Empty<XElement>())
            {
                string tag = ((string)e.Attribute("tag"))?.Trim();
                string itemIdentifier = ((string)e.Attribute("identifier"))?.Trim();
                if (string.IsNullOrEmpty(tag) == string.IsNullOrEmpty(itemIdentifier))
                { throw new FormatException($"{path}: 每个 Rule 必须且只能指定 tag 或 identifier。"); }
                if (e.Attribute("profile") != null && e.Attribute("profiles") != null)
                { throw new FormatException($"{path}: Rule 不能同时设置 profile 和 profiles。"); }
                SweepProfile[] common = ReadProfiles(e, e.Attribute("profile") != null ? "profile" : "profiles");
                SweepProfile[] inventory = ReadProfiles(e, "inventoryProfiles");
                SweepProfile[] preview = ReadProfiles(e, "previewProfiles");
                SweepProfile[] world = ReadProfiles(e, "worldProfiles");
                if (common == null && inventory == null && preview == null && world == null)
                { throw new FormatException($"{path}: Rule 必须指定 profile(s) 或分场景特效列表。"); }
                result.Rules.Add(new SweepRule
                {
                    Tag = tag ?? string.Empty, ItemIdentifier = new Identifier(itemIdentifier ?? string.Empty),
                    HasItemIdentifier = !string.IsNullOrEmpty(itemIdentifier),
                    Profile = common?.FirstOrDefault(), Profiles = common,
                    InventoryProfiles = inventory, PreviewProfiles = preview, WorldProfiles = world,
                    Priority = Int(e, "priority", 0, int.MinValue, int.MaxValue)
                });

                SweepProfile[] ReadProfiles(XElement rule, string attribute)
                {
                    XAttribute value = rule.Attribute(attribute);
                    if (value == null) { return null; }
                    var list = new List<SweepProfile>();
                    foreach (string id in value.Value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!profiles.TryGetValue(id, out SweepProfile selected))
                        { throw new FormatException($"{path}: Rule '{itemIdentifier ?? tag}' 的 {attribute} 引用了不存在的 Profile '{id}'。"); }
                        if (list.Contains(selected)) { continue; }
                        list.Add(selected);
                    }
                    if (list.Count(p => p.ArtEffect?.Kind == "blocks") > 1)
                    { throw new FormatException($"{path}: {attribute} 每组最多使用一个 blocks 底图替换。"); }
                    if (attribute == "profile" && list.Count != 1)
                    { throw new FormatException($"{path}: profile 必须引用一个非空 Profile；多个特效使用 profiles。"); }
                    return list.ToArray();
                }
            }
            // OrderByDescending is stable: equal priorities retain XML order.
            SweepRule[] sorted = result.Rules.OrderByDescending(r => r.Priority).ToArray();
            result.Rules.Clear();
            result.Rules.AddRange(sorted);
            return result;
        }

        internal SweepProfile Match(Item item, ItemPrefab prefab)
            => MatchForSurface(item, prefab, EffectSurface.Inventory).FirstOrDefault();

        internal SweepProfile[] MatchForSurface(Item item, ItemPrefab prefab, EffectSurface surface)
        {
            if (!Enabled) { return Array.Empty<SweepProfile>(); }
            foreach (SweepRule rule in Rules)
            {
                SweepProfile[] selected = surface == EffectSurface.World ? rule.WorldProfiles
                    : surface == EffectSurface.Preview ? rule.PreviewProfiles : rule.InventoryProfiles;
                selected ??= rule.Profiles;
                if (selected == null) { continue; }
                if (rule.HasItemIdentifier)
                {
                    if ((item?.Prefab ?? prefab)?.Identifier == rule.ItemIdentifier) { return selected; }
                    continue;
                }
                var tag = new Identifier(rule.Tag);
                // Read live tags on every match, including tags added/removed by Lua/C#.
                // Only use prefab tags when no live item exists (e.g. recipe previews).
                if (item != null ? item.HasTag(tag) : prefab != null && prefab.Tags.Contains(tag))
                { return selected; }
            }
            return Array.Empty<SweepProfile>();
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
