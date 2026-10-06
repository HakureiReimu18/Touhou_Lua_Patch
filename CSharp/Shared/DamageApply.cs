using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Damage
{
    /// <summary>
    /// 数值与持久化（设计稿 §5.2 / §6）：
    /// - TouhouDamageConfig.txt（存档目录）：玩家值（命令 / 后续 UI 写，本侧读）；
    /// - TouhouDamageState.txt（存档目录）：已应用值 + 统计（本侧写；脚本热重载时按它做除法归一，防叠加）。
    /// </summary>
    public static class DamageValues
    {
        public const string ConfigFileName = "TouhouDamageConfig.txt";
        public const string StateFileName = "TouhouDamageState.txt";

        sealed class Entry
        {
            public float Damage = 1f;
            public string PenMode = "add";
            public float PenValue = 0f;
            public float Defense = 1f;
        }

        static readonly Dictionary<string, Entry> values = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Entry> applied = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public static DateTime LastConfigMtime = DateTime.MinValue;
        public static int LastPatchedItems;
        public static int LastPatchedObjects;

        public static string SaveDir()
        {
            try
            {
                var d = SaveUtil.DefaultSaveFolder;
                if (!string.IsNullOrEmpty(d)) return d;
            }
            catch { }
            return Path.Combine("Data", "Saves");
        }

        public static string ConfigPath => Path.Combine(SaveDir(), ConfigFileName);
        public static string StatePath => Path.Combine(SaveDir(), StateFileName);

        public static void Load()
        {
            values.Clear();
            applied.Clear();

            // 1) XML 配置默认值铺底
            foreach (var g in DamageConfig.Groups.Values)
                values[g.Id] = new Entry { Damage = g.DefaultDamage, PenMode = g.PenetrationMode, PenValue = g.PenetrationValue, Defense = g.DefaultDefense };

            // 2) 玩家值覆盖：damage.<组> / pen.<组>=add:0.05|multiply:1.25|0.05 / def.<组>
            if (File.Exists(ConfigPath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(ConfigPath))
                    {
                        string key, raw;
                        if (!SplitLine(line, out key, out raw)) continue;
                        var dot = key.IndexOf('.');
                        if (dot <= 0) continue;
                        string axis = key.Substring(0, dot).ToLowerInvariant();
                        string gid = key.Substring(dot + 1);
                        if (!values.TryGetValue(gid, out var e)) continue;
                        if (axis == "damage")
                        {
                            if (TryParseFloat(raw, out var v)) e.Damage = v;
                        }
                        else if (axis == "def")
                        {
                            if (TryParseFloat(raw, out var v)) e.Defense = v;
                        }
                        else if (axis == "pen")
                        {
                            ParsePen(raw, ref e.PenMode, ref e.PenValue);
                        }
                    }
                }
                catch (Exception ex) { DamageLog.Warn($"玩家配置读取失败：{ex.Message}"); }
            }
            else
            {
                SaveConfig(); // 首次生成，便于手改 / UI 读取
                DamageLog.Log($"已生成默认玩家配置：{ConfigPath}");
            }

            // 3) 已应用值（脚本热重载归一变基用；缺失按 1.0 / add:0 处理）
            if (File.Exists(StatePath))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(StatePath))
                    {
                        string key, raw;
                        if (!SplitLine(line, out key, out raw)) continue;
                        if (!key.StartsWith("applied.", StringComparison.OrdinalIgnoreCase)) continue;
                        var rest = key.Substring("applied.".Length);
                        var dot = rest.IndexOf('.');
                        if (dot <= 0) continue;
                        string axis = rest.Substring(0, dot).ToLowerInvariant();
                        string gid = rest.Substring(dot + 1);
                        if (!applied.TryGetValue(gid, out var e)) { e = new Entry(); applied[gid] = e; }
                        if (axis == "damage") { if (TryParseFloat(raw, out var v)) e.Damage = v; }
                        else if (axis == "def") { if (TryParseFloat(raw, out var v)) e.Defense = v; }
                        else if (axis == "pen") { ParsePen(raw, ref e.PenMode, ref e.PenValue); }
                    }
                }
                catch (Exception ex) { DamageLog.Warn($"已应用状态读取失败：{ex.Message}"); }
            }

            LastConfigMtime = File.Exists(ConfigPath) ? File.GetLastWriteTime(ConfigPath) : DateTime.MinValue;
            DamageLog.Debug($"数值已加载：组 {values.Count}，已应用记录 {applied.Count}");
        }

        static bool SplitLine(string line, out string key, out string raw)
        {
            key = raw = null;
            if (string.IsNullOrWhiteSpace(line)) return false;
            line = line.Trim();
            if (line.StartsWith("#")) return false;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            key = line.Substring(0, eq).Trim();
            raw = line.Substring(eq + 1).Trim();
            return key.Length > 0;
        }

        static bool TryParseFloat(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        static bool ParsePen(string raw, ref string mode, ref float value)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var t = raw.Trim();
            int colon = t.IndexOf(':');
            if (colon > 0)
            {
                var m = t.Substring(0, colon).Trim().ToLowerInvariant();
                if (m == "add" || m == "mul" || m == "multiply")
                {
                    if (TryParseFloat(t.Substring(colon + 1).Trim(), out var v))
                    {
                        mode = (m == "add") ? "add" : "multiply";
                        value = v;
                        return true;
                    }
                }
                return false;
            }
            if (TryParseFloat(t, out var v2))
            {
                value = v2; // 裸数字：只改数值、保持当前模式
                return true;
            }
            return false;
        }

        public static float GetDamage(string gid) => values.TryGetValue(gid, out var e) ? e.Damage : 1f;
        public static float GetDefense(string gid) => values.TryGetValue(gid, out var e) ? e.Defense : 1f;
        public static void GetPen(string gid, out string mode, out float value)
        {
            if (values.TryGetValue(gid, out var e)) { mode = e.PenMode; value = e.PenValue; }
            else { mode = "add"; value = 0f; }
        }

        public static float AppliedDamage(string gid) => applied.TryGetValue(gid, out var e) ? e.Damage : 1f;
        public static float AppliedDefense(string gid) => applied.TryGetValue(gid, out var e) ? e.Defense : 1f;
        public static void GetAppliedPen(string gid, out string mode, out float value)
        {
            if (applied.TryGetValue(gid, out var e)) { mode = e.PenMode; value = e.PenValue; }
            else { mode = "add"; value = 0f; }
        }

        /// <summary>命令 / UI 用：改一个轴的值（axis: damage / pen / def）。</summary>
        public static bool SetValue(string gid, string axis, string rawValue, out string error)
        {
            error = "";
            if (!values.TryGetValue(gid, out var e)) { error = $"没有分组「{gid}」"; return false; }
            axis = (axis ?? "").Trim().ToLowerInvariant();
            if (DamageConfig.Groups.TryGetValue(gid, out var grp))
            {
                if (grp.IsArmor && (axis == "damage" || axis == "dmg" || axis == "pen" || axis == "penetration"))
                {
                    error = $"「{gid}」（{grp.Label}）是防具组，只支持 def（防御倍率）";
                    return false;
                }
                if (!grp.IsArmor && (axis == "def" || axis == "defense"))
                {
                    error = $"「{gid}」（{grp.Label}）是武器组，只支持 damage / pen";
                    return false;
                }
            }
            switch (axis)
            {
                case "damage":
                case "dmg":
                    if (!TryParseFloat(rawValue, out var dv)) { error = $"数值无效：{rawValue}"; return false; }
                    if (dv < 0f || dv > 10f) { error = $"伤害倍率超出范围（0~10）：{dv}"; return false; }
                    e.Damage = dv;
                    break;
                case "pen":
                case "penetration":
                    {
                        string m = e.PenMode; float v = e.PenValue;
                        if (!ParsePen(rawValue, ref m, ref v)) { error = $"穿甲值无效：{rawValue}"; return false; }
                        if (m == "add" && (v < -1f || v > 1f)) { error = $"穿甲加值超出范围（-1~1）：{v}"; return false; }
                        if (m == "multiply" && (v < 0f || v > 10f)) { error = $"穿甲乘数超出范围（0~10）：{v}"; return false; }
                        e.PenMode = m; e.PenValue = v;
                        break;
                    }
                case "def":
                case "defense":
                    if (!TryParseFloat(rawValue, out var fv)) { error = $"数值无效：{rawValue}"; return false; }
                    if (fv < 0f || fv > 10f) { error = $"防御倍率超出范围（0~10）：{fv}"; return false; }
                    e.Defense = fv;
                    break;
                default:
                    error = "轴只支持 damage / pen / def";
                    return false;
            }
            SaveConfig();
            return true;
        }

        /// <summary>重置某组（或全部）为 XML 配置默认值。</summary>
        public static void ResetToDefaults(string gidOrAll)
        {
            bool all = string.IsNullOrEmpty(gidOrAll) || gidOrAll.Equals("all", StringComparison.OrdinalIgnoreCase);
            foreach (var g in DamageConfig.Groups.Values)
            {
                if (!all && !g.Id.Equals(gidOrAll, StringComparison.OrdinalIgnoreCase)) continue;
                values[g.Id] = new Entry { Damage = g.DefaultDamage, PenMode = g.PenetrationMode, PenValue = g.PenetrationValue, Defense = g.DefaultDefense };
            }
            SaveConfig();
        }

        /// <summary>档位快选（LV1 略弱于补强 / LV2 等于补强 / LV3 补强×2）：把全部有档位的组写入对应值并保存。</summary>
        public static bool ApplyTier(int level, out string error)
        {
            error = "";
            int applied = 0;
            foreach (var g in DamageConfig.Groups.Values)
            {
                if (!values.TryGetValue(g.Id, out var e)) continue;
                if (!g.Tiers.TryGetValue(level, out var t)) continue;
                if (g.IsArmor)
                {
                    e.Defense = t.Defense;
                }
                else
                {
                    e.Damage = t.Damage;
                    e.PenMode = t.PenMode;
                    e.PenValue = t.PenValue;
                }
                applied++;
            }
            if (applied == 0) { error = $"配置里没有 LV{level} 档位"; return false; }
            SaveConfig();
            return true;
        }

        public static void SaveConfig()
        {
            try
            {
                var lines = new List<string>
                {
                    "# 东方-武器伤害与防具抗性设置 · 玩家值（命令/设置页写入；游戏内 damage_set / 设置页修改）",
                    "# 键：damage.<组>=倍率 · pen.<组>=add:<加值> 或 multiply:<乘数>（也可裸数字只改值） · def.<组>=防御倍率",
                    "ver=1",
                };
                foreach (var g in DamageConfig.Groups.Values)
                {
                    if (!values.TryGetValue(g.Id, out var e)) continue;
                    if (g.IsArmor) lines.Add($"def.{g.Id}={Fmt(e.Defense)}");
                    else
                    {
                        lines.Add($"damage.{g.Id}={Fmt(e.Damage)}");
                        lines.Add($"pen.{g.Id}={e.PenMode}:{Fmt(e.PenValue)}");
                    }
                }
                File.WriteAllText(ConfigPath, string.Join("\n", lines) + "\n");
                LastConfigMtime = File.GetLastWriteTime(ConfigPath);
            }
            catch (Exception ex) { DamageLog.Warn($"玩家配置写入失败：{ex.Message}"); }
        }

        /// <summary>应用完成后调用：记录"已应用值"（归一变基）并写状态文件。</summary>
        public static void MarkApplied(int patchedItems, int patchedObjects)
        {
            applied.Clear();
            foreach (var kv in values)
                applied[kv.Key] = new Entry { Damage = kv.Value.Damage, PenMode = kv.Value.PenMode, PenValue = kv.Value.PenValue, Defense = kv.Value.Defense };
            LastPatchedItems = patchedItems;
            LastPatchedObjects = patchedObjects;
            try
            {
                var lines = new List<string>
                {
                    "# 东方-武器伤害与防具抗性设置 · 状态（C# 写；设置页读取显示，applied.* 用于脚本热重载归一，勿手改）",
                    "ver=1",
                    $"patched.items={patchedItems}",
                    $"patched.objects={patchedObjects}",
                    $"stamp={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                };
                foreach (var g in DamageConfig.Groups.Values)
                {
                    if (!applied.TryGetValue(g.Id, out var e)) continue;
                    if (g.IsArmor) lines.Add($"applied.def.{g.Id}={Fmt(e.Defense)}");
                    else
                    {
                        lines.Add($"applied.damage.{g.Id}={Fmt(e.Damage)}");
                        lines.Add($"applied.pen.{g.Id}={e.PenMode}:{Fmt(e.PenValue)}");
                    }
                }
                // 设置页（Lua）显示用：分组元数据 + 当前值 + 默认值 + 档位预设
                foreach (var g in DamageConfig.Groups.Values)
                {
                    if (!values.TryGetValue(g.Id, out var v)) continue;
                    lines.Add($"group.{g.Id}.label={g.Label}");
                    lines.Add($"group.{g.Id}.kind={(g.IsArmor ? "armor" : "weapon")}");
                    lines.Add($"group.{g.Id}.count={g.Items.Count}");
                    for (int lv = 1; lv <= 9; lv++)
                    {
                        if (!g.Tiers.TryGetValue(lv, out var t)) continue;
                        if (g.IsArmor)
                        {
                            lines.Add($"group.{g.Id}.lv{lv}.defense={Fmt(t.Defense)}");
                        }
                        else
                        {
                            lines.Add($"group.{g.Id}.lv{lv}.damage={Fmt(t.Damage)}");
                            lines.Add($"group.{g.Id}.lv{lv}.penmode={t.PenMode}");
                            lines.Add($"group.{g.Id}.lv{lv}.penvalue={Fmt(t.PenValue)}");
                        }
                    }
                    if (g.IsArmor)
                    {
                        lines.Add($"group.{g.Id}.defense={Fmt(v.Defense)}");
                        lines.Add($"group.{g.Id}.defaultdefense={Fmt(g.DefaultDefense)}");
                    }
                    else
                    {
                        lines.Add($"group.{g.Id}.damage={Fmt(v.Damage)}");
                        lines.Add($"group.{g.Id}.penmode={v.PenMode}");
                        lines.Add($"group.{g.Id}.penvalue={Fmt(v.PenValue)}");
                        lines.Add($"group.{g.Id}.defaultdamage={Fmt(g.DefaultDamage)}");
                        lines.Add($"group.{g.Id}.defaultpenmode={g.PenetrationMode}");
                        lines.Add($"group.{g.Id}.defaultpenvalue={Fmt(g.PenetrationValue)}");
                    }
                }
                File.WriteAllText(StatePath, string.Join("\n", lines) + "\n");
            }
            catch (Exception ex) { DamageLog.Warn($"状态写入失败：{ex.Message}"); }
        }

        static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // ==================== 联机请求（设置页文件桥 / 服务器收提交共用） ====================

        /// <summary>一条待应用的改动（联机请求解析结果）。</summary>
        public sealed class Change
        {
            public string Gid = "";
            public string Axis = "";    // damage / pen / def
            public string Mode = "add"; // 仅 pen
            public float Value;
        }

        /// <summary>解析并校验玩家值文本（damage./pen./def. 三类键；范围与分组规则与命令一致）。</summary>
        public static bool TryParseChanges(IEnumerable<string> lines, out List<Change> changes, out string error)
        {
            changes = new List<Change>();
            error = "";
            int count = 0;
            foreach (var raw in lines)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { error = $"行格式无效：{line}"; return false; }
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                int dot = key.IndexOf('.');
                if (dot <= 0) continue;   // ver=1 之类直接忽略
                string axis = key.Substring(0, dot).ToLowerInvariant();
                string gid = key.Substring(dot + 1);
                if (!DamageConfig.Groups.TryGetValue(gid, out var grp)) { error = $"没有分组「{gid}」"; return false; }
                if (grp.IsArmor && axis != "def") { error = $"「{gid}」（{grp.Label}）是防具组，只支持 def"; return false; }
                if (!grp.IsArmor && axis == "def") { error = $"「{gid}」（{grp.Label}）是武器组，只支持 damage/pen"; return false; }

                var ch = new Change { Gid = gid, Axis = axis };
                if (axis == "damage")
                {
                    if (!TryParseFloat(val, out ch.Value)) { error = $"{gid} 的伤害倍率无效：{val}"; return false; }
                    if (ch.Value < 0f || ch.Value > 10f) { error = $"{gid} 的伤害倍率超范围（0~10）：{val}"; return false; }
                }
                else if (axis == "def")
                {
                    if (!TryParseFloat(val, out ch.Value)) { error = $"{gid} 的防御倍率无效：{val}"; return false; }
                    if (ch.Value < 0f || ch.Value > 10f) { error = $"{gid} 的防御倍率超范围（0~10）：{val}"; return false; }
                }
                else if (axis == "pen")
                {
                    string m = "add"; float v = 0f;
                    if (!ParsePen(val, ref m, ref v)) { error = $"{gid} 的穿甲值无效：{val}"; return false; }
                    if (m == "add" && (v < -1f || v > 1f)) { error = $"{gid} 的穿甲加值超范围（-1~1）：{val}"; return false; }
                    if (m == "multiply" && (v < 0f || v > 10f)) { error = $"{gid} 的穿甲乘数超范围（0~10）：{val}"; return false; }
                    ch.Mode = m; ch.Value = v;
                }
                else { error = $"未知轴「{axis}」（只支持 damage / pen / def）"; return false; }

                if (++count > 512) { error = "条目过多"; return false; }
                changes.Add(ch);
            }
            if (changes.Count == 0) { error = "没有可用的数值条目"; return false; }
            return true;
        }

        /// <summary>写入改动并保存（不负责重应用/广播）。</summary>
        public static void ApplyChanges(List<Change> changes)
        {
            foreach (var ch in changes)
            {
                if (!values.TryGetValue(ch.Gid, out var e)) continue;
                switch (ch.Axis)
                {
                    case "damage": e.Damage = ch.Value; break;
                    case "def": e.Defense = ch.Value; break;
                    case "pen": e.PenMode = ch.Mode; e.PenValue = ch.Value; break;
                }
            }
            SaveConfig();
        }

        /// <summary>改动前快照（命令路径用：命令自己改值，之后用它算差分公告）。</summary>
        public sealed class GroupSnapshot
        {
            public float Damage, PenValue, Defense;
            public string PenMode;
        }

        public static Dictionary<string, GroupSnapshot> Snapshot()
        {
            var snap = new Dictionary<string, GroupSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in DamageConfig.Groups.Values)
            {
                if (!values.TryGetValue(g.Id, out var e)) continue;
                snap[g.Id] = new GroupSnapshot
                { Damage = e.Damage, PenMode = e.PenMode, PenValue = e.PenValue, Defense = e.Defense };
            }
            return snap;
        }

        /// <summary>对比快照与当前值的差分公告文本（命令路径在应用之后调用）。</summary>
        public static List<string> DescribeDiffSince(Dictionary<string, GroupSnapshot> before)
        {
            var outList = new List<string>();
            if (before == null) return outList;
            foreach (var g in DamageConfig.Groups.Values)
            {
                if (!before.TryGetValue(g.Id, out var b) || !values.TryGetValue(g.Id, out var e)) continue;
                string label = string.IsNullOrEmpty(g.Label) ? g.Id : g.Label;
                var parts = new List<string>();
                if (g.IsArmor)
                {
                    if (Math.Abs(b.Defense - e.Defense) > 0.0001f)
                        parts.Add($"防御 {Fmt(b.Defense)}→{Fmt(e.Defense)}");
                }
                else
                {
                    if (Math.Abs(b.Damage - e.Damage) > 0.0001f)
                        parts.Add($"伤害 ×{Fmt(b.Damage)}→×{Fmt(e.Damage)}");
                    if (b.PenMode != e.PenMode || Math.Abs(b.PenValue - e.PenValue) > 0.0001f)
                        parts.Add($"穿甲 {PenText(b.PenMode, b.PenValue)}→{PenText(e.PenMode, e.PenValue)}");
                }
                if (parts.Count > 0) outList.Add($"{label} {string.Join("、", parts)}");
            }
            return outList;
        }

        static string PenText(string mode, float v) => (mode == "multiply" ? "×" : "+") + Fmt(v);
    }

    /// <summary>状态文本工具：给客户端镜像/服务器广播用（剔除 applied.*，那是权威端的归一变基）。</summary>
    public static class DamageDisplay
    {
        public static string StrippedStateText()
        {
            try
            {
                string path = DamageValues.StatePath;
                if (!File.Exists(path)) return null;
                var kept = new List<string>();
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.StartsWith("applied.", StringComparison.OrdinalIgnoreCase)) continue;
                    kept.Add(line);
                }
                return string.Join("\n", kept) + "\n";
            }
            catch (Exception ex) { DamageLog.Warn($"状态读取失败：{ex.Message}"); return null; }
        }
    }

    /// <summary>
    /// 应用器（设计稿 §3 / §6）：
    /// - Harmony postfix 挂在 Item 构造器（Rectangle 汇聚点）→ 新物品创建即打补丁；
    /// - 幂等：逐对象 base 快照（弱表）+ 全局"已应用值"（状态文件）做 reloadlua 归一，绝不叠加；
    /// - 只在权威端（单机 / 主机 / 专用服务器）应用。
    /// 遍历路径与 DamageScanner 保持一致（两边要同步改）。
    /// </summary>
    public static class DamagePatcher
    {
        sealed class Box { public float Base; }

        // key: Affliction / Attack / DamageModifier 对象（弱引用，对象回收自动出表）
        static readonly ConditionalWeakTable<object, Box> snapshots = new ConditionalWeakTable<object, Box>();
        static bool registered;
        static int normalizedThisPass;   // 本次 ApplyAll 因未跟踪而做除法归一的次数
        static int writtenThisPass;      // 本次 ApplyAll 真正写进对象（伤害/穿甲/防御）的次数
        static bool firstApplyDone;

        /// <summary>构造钩子是否已挂载（damage_probe 展示，用于判断新物品是否会被即时打补丁）。</summary>
        public static bool HookRegistered => registered;
        /// <summary>构造钩子累计应用的对象数（会话内）。长时间不增长 = 钩子没生效。</summary>
        public static int HookAppliedObjects;
        /// <summary>上次 ApplyAll 的归一转除计数（首次实例化时的归一属正常，之后应恒为 0）。</summary>
        public static int LastNormalizedObjects;
        /// <summary>上次 ApplyAll 实际写入对象的次数（=0 说明全是"中性跳过"，参数改了也不会生效）。</summary>
        public static int LastWrittenObjects;

        public static bool IsAuthority =>
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

        /// <summary>报告/探测用：对象若已跟踪（本实例打过补丁）返回其底值。</summary>
        public static bool TryGetBase(object obj, out float baseVal)
        {
            baseVal = 0f;
            if (obj == null) return false;
            try
            {
                if (snapshots.TryGetValue(obj, out var box)) { baseVal = box.Base; return true; }
            }
            catch { }
            return false;
        }

        /// <summary>报告/探测用：已跟踪取底值，否则取当前值。</summary>
        public static float BaseOrLive(object obj, float live) => TryGetBase(obj, out var b) ? b : live;

        public static void Register(Harmony harmony)
        {
            if (registered) return;
            try
            {
                var ctor = typeof(Item).GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) }, null);
                if (ctor == null)
                {
                    DamageLog.Warn("Item 构造器未找到，应用器不会生效");
                    return;
                }
                var postfix = typeof(DamagePatcher).GetMethod(nameof(OnItemCreatedPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                harmony.Patch(ctor, postfix: new HarmonyMethod(postfix));
                registered = true;
                DamageLog.Debug("Item 构造补丁已挂载（应用器生效）");
            }
            catch (Exception ex) { DamageLog.Warn($"补丁挂载失败：{ex.Message}"); }
        }

        static void OnItemCreatedPostfix(Item __instance)
        {
            try
            {
                if (!IsAuthority || __instance == null) return;
                var g = DamageConfig.FindGroup(__instance);
                if (g == null) return;
                int n = ApplyItem(__instance, g, freshObject: true);
                if (n > 0) HookAppliedObjects += n;
            }
            catch (Exception ex) { LogCapped($"新物品应用出错：{ex.Message}"); }
        }

        static int errorLogCount;
        static void LogCapped(string msg)
        {
            // 出错路径可能每件物品都触发，只报前几条防刷屏
            if (errorLogCount++ < 5) DamageLog.Warn(msg);
            else if (errorLogCount == 6) DamageLog.Warn("（同类错误过多，后续已静默）");
        }

        /// <summary>全量扫描：新实例上线（reloadlua 归一）与 roundStart / 数值变更后重应用。</summary>
        public static void ApplyAll()
        {
            if (!IsAuthority) return;
            if (DamageConfig.Groups.Count == 0) return;
            int items = 0, objects = 0;
            normalizedThisPass = 0;
            writtenThisPass = 0;
            try
            {
                foreach (var item in Item.ItemList.ToList())
                {
                    if (item == null || item.Removed) continue;
                    var g = DamageConfig.FindGroup(item);
                    if (g == null) continue;
                    items++;
                    objects += ApplyItem(item, g, freshObject: false);
                }
            }
            catch (Exception ex) { LogCapped($"应用扫描出错：{ex.Message}"); }
            LastNormalizedObjects = normalizedThisPass;
            LastWrittenObjects = writtenThisPass;
            // 首次实例化（含 reloadlua）归一属正常；之后仍有归一转除，说明有新物品绕过了构造钩子，需要排查
            if (normalizedThisPass > 0 && firstApplyDone)
                LogCapped($"注意：本次归一了 {normalizedThisPass} 个未跟踪对象（新物品未经构造钩子处理？用 damage_probe 检查钩子状态）");
            firstApplyDone = true;
            DamageValues.MarkApplied(items, objects);
            DamageLog.Log($"应用完成：物品 {items}，数值对象 {objects}（实际写入 {writtenThisPass}，归一 {normalizedThisPass}）");
        }

        static int ApplyItem(Item item, DamageGroup g, bool freshObject)
        {
            int n = 0;
            if (g.IsArmor)
            {
                foreach (var ic in item.Components)
                {
                    if (!(ic is Wearable wearable)) continue;
                    foreach (var dm in wearable.DamageModifiers) n += ApplyDefense(dm, g, freshObject);
                }
                return n;
            }

            foreach (var ic in item.Components)
            {
                Attack attack = null;
                if (ic is Projectile proj) attack = proj.Attack;
                else if (ic is MeleeWeapon melee) attack = melee.Attack;
                if (attack != null) n += ApplyAttack(attack, g, freshObject, isExplosion: false);

                var lists = ic.statusEffectLists;
                if (lists == null) continue;
                foreach (var kv in lists)
                    foreach (var se in kv.Value)
                        n += ApplyStatusEffect(se, g, freshObject);
            }
            return n;
        }

        static int ApplyAttack(Attack attack, DamageGroup g, bool freshObject, bool isExplosion)
        {
            int n = ApplyPenetration(attack, g, freshObject);
            foreach (var aff in attack.Afflictions.Keys)
                n += ApplyAffliction(aff, g, freshObject, null);
            foreach (var se in attack.StatusEffects)
                n += ApplyStatusEffect(se, g, freshObject);
            return n;
        }

        static int ApplyStatusEffect(StatusEffect se, DamageGroup g, bool freshObject)
        {
            int n = 0;
            foreach (var aff in se.Afflictions)
                n += ApplyAffliction(aff, g, freshObject, se.targetTypes);
            foreach (var exp in se.Explosions)
                n += ApplyAttack(exp.Attack, g, freshObject, isExplosion: true);
            return n;
        }

        static int ApplyAffliction(Affliction aff, DamageGroup g, bool freshObject, StatusEffect.TargetType? effectTargets)
        {
            if (aff?.Prefab == null) return 0;
            if (!DamageConfig.DamageTypes.Contains(aff.Prefab.AfflictionType.Value)) return 0;
            if (effectTargets != null && (effectTargets.Value & DamageConfig.EffectTargets) == 0) return 0;

            float factor = DamageValues.GetDamage(g.Id);
            float baseVal;
            if (snapshots.TryGetValue(aff, out var box))
            {
                baseVal = box.Base;
            }
            else
            {
                float cur = aff.Strength;
                baseVal = cur;
                if (!freshObject)
                {
                    // 可能是上一个插件实例按"已应用倍率"烘过的值：除回来再乘当前值
                    float prev = DamageValues.AppliedDamage(g.Id);
                    if (prev > 0.0001f && Math.Abs(prev - 1f) > 0.0001f)
                    {
                        baseVal = cur / prev;
                        normalizedThisPass++;
                    }
                }
                snapshots.Add(aff, new Box { Base = baseVal });
            }
            // 必须用 SetStrength（同时写 _strength 与 _nonClampedStrength）：
            // 伤害路径在攻击倍率≈1 时读 NonClampedStrength，而 Strength 属性的 setter
            // 只在 _nonClampedStrength<0 时同步它、之后不再更新（否则改了值伤害也不变）。
            // 但「本次与上次都是中性」时一个字都不写——中性 = 完全不动（也避免替游戏同步双字段）。
            bool neutral = Math.Abs(factor - 1f) < 0.0001f;
            if (!(neutral && Math.Abs(DamageValues.AppliedDamage(g.Id) - 1f) < 0.0001f))
            {
                aff.SetStrength(baseVal * factor);
                writtenThisPass++;
            }
            return 1;
        }

        static int ApplyPenetration(Attack attack, DamageGroup g, bool freshObject)
        {
            DamageValues.GetPen(g.Id, out var mode, out var factor);
            float basePen;
            if (snapshots.TryGetValue(attack, out var box))
            {
                basePen = box.Base;
            }
            else
            {
                float cur = attack.Penetration;
                basePen = cur;
                if (!freshObject)
                {
                    DamageValues.GetAppliedPen(g.Id, out var amode, out var av);
                    bool penApplied = amode == "multiply" ? Math.Abs(av - 1f) > 0.0001f : Math.Abs(av) > 0.0001f;
                    basePen = InvertPenetration(cur, amode, av);
                    if (penApplied) normalizedThisPass++;
                }
                snapshots.Add(attack, new Box { Base = basePen });
            }
            // 中性 = 完全不动：连 0~1 封顶也不做（内容里存在负穿甲 / 被效果改过的值，替它封顶就是改了数值）。
            // 从非中性回退到中性时把底值写回去；写回/写入前比一下现值，相同就不碰属性。
            DamageValues.GetAppliedPen(g.Id, out var amode2, out var av2);
            bool neutral = mode == "multiply" ? Math.Abs(factor - 1f) < 0.0001f : Math.Abs(factor) < 0.0001f;
            bool appliedNeutral = amode2 == "multiply" ? Math.Abs(av2 - 1f) < 0.0001f : Math.Abs(av2) < 0.0001f;
            if (!(neutral && appliedNeutral))
            {
                float target = neutral ? basePen
                    : Math.Clamp(mode == "multiply" ? basePen * factor : basePen + factor, 0f, 1f);
                if (Math.Abs(attack.Penetration - target) > 0.0001f)
                {
                    attack.Penetration = target;
                    writtenThisPass++;
                }
            }
            return 1;
        }

        static int ApplyDefense(DamageModifier dm, DamageGroup g, bool freshObject)
        {
            float m = DamageValues.GetDefense(g.Id);
            float baseV;
            if (snapshots.TryGetValue(dm, out var box))
            {
                baseV = box.Base;
            }
            else
            {
                float cur = dm.DamageMultiplier;
                baseV = cur;
                if (!freshObject)
                {
                    float prev = DamageValues.AppliedDefense(g.Id);
                    if (prev > 0.0001f && Math.Abs(prev - 1f) > 0.0001f)
                    {
                        baseV = Math.Clamp(1f - (1f - cur) / prev, 0f, 1f);
                        normalizedThisPass++;
                    }
                }
                snapshots.Add(dm, new Box { Base = baseV });
            }
            // 中性 = 完全不动：本次与上次都是 M=1 时不写（避免无谓赋值，也防止把游戏侧改过的值刷回来）
            bool neutral = Math.Abs(m - 1f) < 0.0001f;
            if (!(neutral && Math.Abs(DamageValues.AppliedDefense(g.Id) - 1f) < 0.0001f))
            {
                float target = TransformDefense(baseV, m);
                if (Math.Abs(dm.DamageMultiplier - target) > 0.0001f)
                {
                    dm.DamageMultiplier = target;
                    writtenThisPass++;
                }
            }
            return 1;
        }

        /// <summary>防御换算：承伤 v → 1 − (1−v)×M（M&gt;1 时按减伤上限截断，且不低于原减伤）。</summary>
        static float TransformDefense(float v, float m)
        {
            float reduction = (1f - v) * m;
            if (m > 1f)
            {
                float cap = Math.Max(1f - v, DamageConfig.DefenseReductionCap);
                if (reduction > cap) reduction = cap;
            }
            return Math.Clamp(1f - reduction, 0f, 1f);
        }

        static float InvertPenetration(float cur, string mode, float v)
        {
            float basePen = mode == "multiply"
                ? (Math.Abs(v) > 0.0001f ? cur / v : cur)
                : cur - v;
            return Math.Clamp(basePen, 0f, 1f);
        }
    }
}
