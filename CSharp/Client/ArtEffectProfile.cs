using System;
using System.Globalization;
using System.Xml.Linq;
using Microsoft.Xna.Framework;

namespace Touhou.WeaponBadges
{
    // Art profiles share the existing Sweep Profile element. A missing effect or
    // effect="sweep" keeps the ordinary tier sweep; every other non-empty value
    // must name one of the styles implemented by ArtEffectRenderer.
    internal sealed class ArtEffectProfile
    {
        internal string Kind = string.Empty;
        internal string Presentation = "edge";
        internal Color AccentColor = new Color(255, 104, 56);
        internal Color CoreColor = new Color(255, 224, 148);
        internal float Amplitude = .16f;
        internal float Speed = 1f;
        internal int Density = 12;
        internal float Size = 1f;
        internal float Opacity = .8f;

        private const string SupportedEffects =
            "flame, solar, echo, phantom, cyber, blocks, frost, lightning, rune";

        internal static ArtEffectProfile Load(XElement e, string path)
        {
            if (e == null) { throw new ArgumentNullException(nameof(e)); }
            string requested = ((string)e.Attribute("effect"))?.Trim();
            if (string.IsNullOrEmpty(requested)) { return null; }

            string kind = requested.ToLowerInvariant();
            if (kind == "sweep") { return null; }
            if (!IsSupported(kind))
            {
                string id = ((string)e.Attribute("id"))?.Trim();
                string profile = string.IsNullOrEmpty(id) ? "<Profile>" : $"Profile '{id}'";
                string source = string.IsNullOrEmpty(path) ? "<unknown path>" : path;
                throw new FormatException(
                    $"{source}: {profile} 的 effect='{requested}' 不受支持；可用值：{SupportedEffects}，或省略 effect / 使用 sweep。");
            }

            GetDefaults(kind, out Color accent, out Color core, out float amplitude,
                out float speed, out int density, out float size, out float opacity);
            var result = new ArtEffectProfile
            {
                Kind = kind,
                AccentColor = ReadColor(e, "accentColor", accent, path),
                CoreColor = ReadColor(e, "coreColor", core, path),
                // Amplitude is a fraction of the source short side. All finite
                // values are clamped; invalid numbers keep the style default.
                Amplitude = Float(e, "amplitude", amplitude, 0f, .75f),
                Speed = Float(e, "speed", speed, 0f, 12f),
                Density = Int(e, "density", density, 1, 96),
                Size = Float(e, "size", size, .15f, 4f),
                Opacity = Float(e, "opacity", opacity, 0f, 1f)
            };
            string presentation = ((string)e.Attribute("presentation"))?.Trim().ToLowerInvariant() ?? "edge";
            if (presentation != "edge" && presentation != "aura" && presentation != "contour")
            { throw new FormatException($"{path}: presentation 只能为 edge、aura 或 contour。"); }
            if (presentation != "edge" && kind != "flame" && kind != "solar" && kind != "frost" && kind != "lightning" && kind != "rune")
            { throw new FormatException($"{path}: effect='{kind}' 不支持 {presentation} 展示。"); }
            result.Presentation = presentation;
            return result;
        }

        private static bool IsSupported(string kind)
        {
            switch (kind)
            {
                case "flame":
                case "solar":
                case "echo":
                case "phantom":
                case "cyber":
                case "blocks":
                case "frost":
                case "lightning":
                case "rune":
                    return true;
                default:
                    return false;
            }
        }

        private static void GetDefaults(string kind, out Color accent, out Color core,
            out float amplitude, out float speed, out int density, out float size, out float opacity)
        {
            switch (kind)
            {
                case "solar":
                    accent = new Color(255, 201, 77); core = new Color(255, 240, 168);
                    amplitude = .12f; speed = .65f; density = 18; size = 1.25f; opacity = .72f;
                    return;
                case "echo":
                    accent = new Color(73, 230, 255); core = new Color(255, 113, 216);
                    amplitude = .22f; speed = .55f; density = 5; size = 1f; opacity = .62f;
                    return;
                case "phantom":
                    accent = new Color(154, 216, 255); core = new Color(233, 255, 255);
                    amplitude = .11f; speed = .25f; density = 4; size = 1.05f; opacity = .42f;
                    return;
                case "cyber":
                    accent = new Color(255, 45, 154); core = new Color(54, 246, 255);
                    amplitude = .06f; speed = 5.5f; density = 8; size = 1f; opacity = .8f;
                    return;
                case "blocks":
                    accent = new Color(165, 108, 255); core = new Color(76, 244, 255);
                    amplitude = .22f; speed = .45f; density = 64; size = .95f; opacity = 1f;
                    return;
                case "frost":
                    accent = new Color(123, 215, 255); core = new Color(232, 255, 255);
                    amplitude = .07f; speed = .4f; density = 12; size = 1.15f; opacity = .68f;
                    return;
                case "lightning":
                    accent = new Color(181, 137, 255); core = new Color(255, 241, 166);
                    amplitude = .08f; speed = 4.5f; density = 9; size = 1.15f; opacity = .82f;
                    return;
                case "rune":
                    accent = new Color(66, 240, 198); core = new Color(255, 224, 145);
                    amplitude = .04f; speed = .32f; density = 20; size = 1.25f; opacity = .72f;
                    return;
                default:
                    accent = new Color(255, 104, 56); core = new Color(255, 224, 148);
                    amplitude = .16f; speed = .8f; density = 14; size = .8f; opacity = .78f;
                    return;
            }
        }

        private static float Float(XElement e, string key, float fallback, float min, float max)
            => float.TryParse((string)e.Attribute(key), NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float value) && float.IsFinite(value)
                ? Math.Clamp(value, min, max)
                : fallback;

        private static int Int(XElement e, string key, int fallback, int min, int max)
            => int.TryParse((string)e.Attribute(key), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int value)
                ? Math.Clamp(value, min, max)
                : fallback;

        private static Color ReadColor(XElement e, string key, Color fallback, string path)
        {
            string value = ((string)e.Attribute(key))?.Trim();
            if (value == null) { return fallback; }
            if (value.Length != 7 || value[0] != '#' ||
                !uint.TryParse(value.Substring(1), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out uint rgb))
            {
                string id = ((string)e.Attribute("id"))?.Trim();
                string profile = string.IsNullOrEmpty(id) ? "<Profile>" : $"Profile '{id}'";
                string source = string.IsNullOrEmpty(path) ? "<unknown path>" : path;
                throw new FormatException($"{source}: {profile} 的 {key} 必须为 #rrggbb，实际为 '{value}'。");
            }
            return new Color((int)((rgb >> 16) & 255), (int)((rgb >> 8) & 255), (int)(rgb & 255));
        }
    }
}
