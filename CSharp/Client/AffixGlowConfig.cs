using System;
using System.Globalization;
using System.Xml.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using Touhou.Affixes;

namespace Touhou.WeaponBadges
{
    internal sealed class AffixGlowConfig
    {
        internal bool Enabled = true;
        internal float Intensity = .30f;
        internal int Inset = 1;

        internal static AffixGlowConfig Load(XElement root, string path)
        {
            var result = new AffixGlowConfig();
            XElement section = root.Element("AffixGlow");
            // 旧配置没有这一节时保持原画面，更新配置后再启用。
            if (section == null)
            {
                result.Enabled = false;
                return result;
            }

            result.Enabled = Bool(section, "enabled", result.Enabled);
            result.Intensity = Float(section, "intensity", result.Intensity, 0f, 1f);
            result.Inset = Int(section, "inset", result.Inset, 0, 8);
            return result;
        }

        internal bool TryMatch(Item item, out Color color)
        {
            color = Color.White;
            if (!Enabled || item == null || !Helpers.TryGetAffix(item, out AffixDef def) || def == null) { return false; }
            // 生效定义已经处理好奇迹多词缀，颜色也和名称前缀同一来源。
            color = def.DisplayColor;
            return true;
        }

        private static bool Bool(XElement e, string key, bool fallback)
            => bool.TryParse((string)e.Attribute(key), out bool value) ? value : fallback;

        private static float Float(XElement e, string key, float fallback, float min, float max)
            => float.TryParse((string)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

        private static int Int(XElement e, string key, int fallback, int min, int max)
            => int.TryParse((string)e.Attribute(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? Math.Clamp(value, min, max) : fallback;
    }
}
