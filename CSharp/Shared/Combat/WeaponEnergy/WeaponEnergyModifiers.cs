// 全局回能加成登记表：Config/WeaponEnergyModifiers.xml，给没有 tag 字段的
// 天赋 / affliction 按 id 映射成百分比加成。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;

namespace Touhou.WeaponEnergy
{
    // 全局回能加成登记表：Config/WeaponEnergyModifiers.xml（本包优先，其他内容包的也认）。
    // 天赋和 affliction 没有 tag 字段，靠这张表把任意 id 映射成百分比加成；
    // 物品 tag 既可以用这里的 <Modifier tag=...>，也可以直接用 TouhouModEnergy_* 命名约定。
    internal sealed class WeaponEnergyModifierEntry
    {
        internal Identifier Tag;
        internal Identifier Affliction;
        internal Identifier Talent;
        internal float Natural;   // 百分比，可负
        internal float Damage;

        internal bool Matches(Func<Identifier, bool> hasTag, Func<Identifier, bool> hasAffliction,
            Func<Identifier, bool> hasTalent)
        {
            if (RegenModifierDef.IsSet(Tag) && !hasTag(Tag)) return false;
            if (RegenModifierDef.IsSet(Affliction) && !hasAffliction(Affliction)) return false;
            if (RegenModifierDef.IsSet(Talent) && !hasTalent(Talent)) return false;
            if (!RegenModifierDef.IsSet(Tag) && !RegenModifierDef.IsSet(Affliction) && !RegenModifierDef.IsSet(Talent))
                return false;
            return true;
        }
    }

    internal static class WeaponEnergyModifierRegistry
    {
        internal static readonly List<WeaponEnergyModifierEntry> Entries = new List<WeaponEnergyModifierEntry>();
        static readonly Dictionary<string, DateTime> mtimes = new Dictionary<string, DateTime>();
        static double nextCheck;
        const string RelativePath = "Config/WeaponEnergyModifiers.xml";

        internal static void LoadIfChanged()
        {
            try
            {
                double now = Timing.TotalTime;
                if (now < nextCheck) return;
                nextCheck = now + 2.0;
                var paths = EnumeratePaths();
                bool dirty = paths.Count != mtimes.Count;
                if (!dirty)
                {
                    foreach (var path in paths)
                    {
                        if (!mtimes.TryGetValue(path, out var mtime) || File.GetLastWriteTime(path) != mtime)
                        {
                            dirty = true;
                            break;
                        }
                    }
                }
                if (!dirty) return;
                mtimes.Clear();
                var loaded = new List<WeaponEnergyModifierEntry>();
                foreach (var path in paths)
                {
                    mtimes[path] = File.GetLastWriteTime(path);
                    try
                    {
                        Parse(File.ReadAllText(path), loaded);
                    }
                    catch
                    {
                        // 单个包写错不该让整张表失效
                    }
                }
                Entries.Clear();
                Entries.AddRange(loaded);
            }
            catch
            {
            }
        }

        // 测试与热重载共用：直接吃一段 XML。
        internal static void LoadFromXml(string text)
        {
            var loaded = new List<WeaponEnergyModifierEntry>();
            Parse(text, loaded);
            Entries.Clear();
            Entries.AddRange(loaded);
        }

        private static void Parse(string text, List<WeaponEnergyModifierEntry> into)
        {
            XElement root;
            try { root = XElement.Parse(text); }
            catch { return; }
            foreach (var element in root.Elements("Modifier"))
            {
                var entry = new WeaponEnergyModifierEntry
                {
                    Tag = Id(element, "tag"),
                    Affliction = Id(element, "affliction"),
                    Talent = Id(element, "talent"),
                    Natural = Number(element, "natural"),
                    Damage = Number(element, "damage"),
                };
                if (!RegenModifierDef.IsSet(entry.Tag) && !RegenModifierDef.IsSet(entry.Affliction) &&
                    !RegenModifierDef.IsSet(entry.Talent)) continue;
                into.Add(entry);
            }
        }

        private static Identifier Id(XElement element, string name)
        {
            string value = element.Attribute(name)?.Value?.Trim();
            return string.IsNullOrEmpty(value) ? default : value.ToIdentifier();
        }

        private static float Number(XElement element, string name)
        {
            string text = element.Attribute(name)?.Value;
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                   float.IsFinite(value) ? value : 0f;
        }

        private static List<string> EnumeratePaths()
        {
            var paths = new List<string>();
            string ownDir = null;
            try
            {
                if (LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<WeaponEnergyPlugin>(out var own))
                    ownDir = own.Dir;
            }
            catch { }
            if (ownDir != null)
            {
                string ownPath = Path.Combine(ownDir, RelativePath);
                if (File.Exists(ownPath)) paths.Add(ownPath);
            }
            try
            {
                foreach (var package in ContentPackageManager.EnabledPackages.Regular)
                {
                    if (package == null || package.Dir == null || package.Dir == ownDir) continue;
                    string path = Path.Combine(package.Dir, RelativePath);
                    if (File.Exists(path)) paths.Add(path);
                }
            }
            catch { }
            return paths;
        }
    }
}
