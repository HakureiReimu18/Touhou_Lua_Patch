using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;

namespace Touhou.WeaponBadges
{
    // Public surface intentionally uses strings only, for LuaUserData.CreateStatic.
    public static class EffectSettings
    {
        internal sealed class Values
        {
            internal bool Enabled, Inventory, Previews, World, Sparkles = true;
            internal bool AffixGlow = true;
            internal string Quality = "high";
            internal int MaxPerFrame = 128, CacheMegabytes;
            internal float WorldIntensity;

            internal Values Copy() => (Values)MemberwiseClone();
        }

        private static readonly object sync = new object();
        private static readonly string[] keys = { "enabled", "inventory", "previews", "world", "quality",
            "sparkles", "maxPerFrame", "cacheMegabytes", "worldIntensity", "affixGlow" };
        private static Values current;
        private static ItemVisualEffects effects;
        private static string path;
        private static bool registered;

        internal static void Initialize(SweepConfig config, ItemVisualEffects owner)
        {
            lock (sync)
            {
                effects = owner;
                current = new Values { Enabled = config.Enabled, Inventory = config.Inventory,
                    Previews = config.Previews, World = config.World, CacheMegabytes = config.CacheMegabytes,
                    WorldIntensity = config.WorldIntensity, AffixGlow = config.AffixGlow.Enabled };
                path = null;
                try
                {
                    path = Path.Combine(SaveUtil.DefaultSaveFolder, "TouhouWeaponEffects.xml");
                    if (File.Exists(path))
                    {
                        XElement root = XDocument.Load(path).Root;
                        if (root == null || root.Name != "TouhouWeaponEffects" || root.HasElements ||
                            !string.IsNullOrWhiteSpace(root.Value))
                        { throw new FormatException("根元素必须是仅含设置属性的 <TouhouWeaponEffects>。"); }
                        current = Parse(string.Join("\n", root.Attributes().Select(a => a.Name + "=" + a.Value)),
                            current, requireAll: true);
                    }
                }
                catch (Exception e)
                {
                    DebugConsole.AddWarning($"[Touhou.WeaponBadges] 玩家扫光设置无法加载，使用包默认：{e.Message}");
                }
                effects.ApplySettings(current);
                // SafeLuaUserDataService rejects plugin namespaces in Lua RegisterType.
                // C# registration lets Lua directly CreateStatic this string-only API.
                MoonSharp.Interpreter.UserData.RegisterType(typeof(EffectSettings));
                registered = true;
            }
        }

        public static string Read()
        {
            lock (sync) { return current == null ? string.Empty : Snapshot(current); }
        }

        // Partial updates are allowed; omitted keys retain their current values.
        // No live state changes until the complete candidate has been validated and saved.
        public static string Save(string text)
        {
            lock (sync)
            {
                if (current == null || effects == null) { return "扫光模块尚未初始化。"; }
                Values candidate;
                try { candidate = Parse(text, current, requireAll: false); }
                catch (Exception e) { return "扫光设置无效：" + e.Message; }

                string temporary = null;
                try
                {
                    if (path == null) { throw new IOException("无法确定玩家存档目录。"); }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    var root = new XElement("TouhouWeaponEffects");
                    foreach (string line in Snapshot(candidate).Split('\n'))
                    {
                        int equals = line.IndexOf('=');
                        root.SetAttributeValue(line.Substring(0, equals), line.Substring(equals + 1));
                    }
                    new XDocument(root).Save(temporary);
                    // Same-directory replacement leaves the old file intact on failure.
                    if (File.Exists(path)) { File.Replace(temporary, path, null); }
                    else { File.Move(temporary, path); }
                }
                catch (Exception e) { return "扫光设置保存失败：" + e.Message; }
                finally
                {
                    if (temporary != null)
                    {
                        try { if (File.Exists(temporary)) { File.Delete(temporary); } }
                        catch { /* A cleanup error must not hide the original save result. */ }
                    }
                }
                effects.ApplySettings(candidate);
                current = candidate;
                return string.Empty;
            }
        }

        internal static void Dispose(ItemVisualEffects owner)
        {
            lock (sync)
            {
                if (!ReferenceEquals(effects, owner)) { return; }
                current = null;
                effects = null;
                path = null;
                if (registered)
                {
                    // The history also holds descriptors/types from this assembly.
                    MoonSharp.Interpreter.UserData.UnregisterType(typeof(EffectSettings), deleteHistory: true);
                    registered = false;
                }
            }
        }

        private static string Snapshot(Values value)
        {
            return "enabled=" + Boolean(value.Enabled) + "\ninventory=" + Boolean(value.Inventory) +
                "\npreviews=" + Boolean(value.Previews) + "\nworld=" + Boolean(value.World) +
                "\nquality=" + value.Quality + "\nsparkles=" + Boolean(value.Sparkles) +
                "\nmaxPerFrame=" + value.MaxPerFrame.ToString(CultureInfo.InvariantCulture) +
                "\ncacheMegabytes=" + value.CacheMegabytes.ToString(CultureInfo.InvariantCulture) +
                "\nworldIntensity=" + value.WorldIntensity.ToString("R", CultureInfo.InvariantCulture) +
                "\naffixGlow=" + Boolean(value.AffixGlow);
        }

        private static string Boolean(bool value) => value ? "true" : "false";

        private static Values Parse(string text, Values previous, bool requireAll)
        {
            if (string.IsNullOrWhiteSpace(text)) { throw new FormatException("设置内容不能为空。"); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Values value = previous.Copy();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { continue; }
                int equals = line.IndexOf('=');
                if (equals < 1) { throw new FormatException("每行必须使用 key=value 格式。"); }
                string key = line.Substring(0, equals).Trim();
                string entry = line.Substring(equals + 1).Trim();
                if (!keys.Contains(key)) { throw new FormatException("未知设置：" + key); }
                if (!seen.Add(key)) { throw new FormatException("重复设置：" + key); }
                switch (key)
                {
                    case "enabled": value.Enabled = ReadBool(key, entry); break;
                    case "inventory": value.Inventory = ReadBool(key, entry); break;
                    case "previews": value.Previews = ReadBool(key, entry); break;
                    case "world": value.World = ReadBool(key, entry); break;
                    case "sparkles": value.Sparkles = ReadBool(key, entry); break;
                    case "affixGlow": value.AffixGlow = ReadBool(key, entry); break;
                    case "quality":
                        if (entry != "low" && entry != "balanced" && entry != "high")
                        { throw new FormatException("quality 必须是 low、balanced 或 high。"); }
                        value.Quality = entry;
                        break;
                    case "maxPerFrame": value.MaxPerFrame = ReadInt(key, entry, 0, 512); break;
                    case "cacheMegabytes": value.CacheMegabytes = ReadInt(key, entry, 1, 256); break;
                    case "worldIntensity":
                        if (!float.TryParse(entry, NumberStyles.Float, CultureInfo.InvariantCulture, out float intensity) ||
                            !float.IsFinite(intensity) || intensity < 0 || intensity > 1)
                        { throw new FormatException("worldIntensity 必须是 0..1 的有限数值。"); }
                        value.WorldIntensity = intensity;
                        break;
                }
            }
            // 旧九键文件没有 affixGlow，保留包默认值；其他字段仍然必须齐全。
            if (requireAll && seen.Count != keys.Length &&
                (seen.Count != keys.Length - 1 || seen.Contains("affixGlow")))
            { throw new FormatException("玩家设置文件缺少字段。"); }
            return value;
        }

        private static bool ReadBool(string key, string text)
        {
            if (!bool.TryParse(text, out bool value)) { throw new FormatException(key + " 必须是 true 或 false。"); }
            return value;
        }

        private static int ReadInt(string key, string text, int min, int max)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            { throw new FormatException($"{key} 必须是 {min}..{max} 的整数。"); }
            return value;
        }
    }
}
