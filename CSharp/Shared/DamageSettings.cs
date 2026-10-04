using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Damage
{
    /// <summary>
    /// 武器伤害与防具抗性设置插件。
    /// 设计见 Docs/武器伤害与防具抗性设置-设计稿.md。
    /// P0：采集器 + damage_dump 审计；P1：应用器（Item 构造补丁 + 幂等归一）+ 数值持久化 + damage_set/reset/apply。
    /// 文件桥 / UI / 联动接口按设计稿 P2+ 推进。
    /// </summary>
    public sealed class DamagePlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public static readonly string[] CommandNames =
            { "damage_reload", "damage_list", "damage_dump", "damage_set", "damage_reset", "damage_apply", "damage_probe", "damage_lv" };

        static string packageDir;
        /// <summary>模组目录：优先用主插件已解析的路径，插件初始化顺序不保证它先跑。</summary>
        public static string PackageDir
        {
            get
            {
                if (packageDir != null) return packageDir;
                packageDir = Touhou.Affixes.Mod.Package?.Dir;
                if (string.IsNullOrEmpty(packageDir) &&
                    LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<DamagePlugin>(out var pkg))
                    packageDir = pkg.Dir;
                return packageDir ?? ".";
            }
        }

        public void Initialize()
        {
            harmony = new Harmony("touhou.damage");
        }

        public void PreInitPatching() { }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone)
            {
                DamageLog.Debug("OnLoadCompleted：内容重载，命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防 reloadlua 残留堆叠
            harmony.UnpatchSelf();

            DamageConfig.Load();
            DamageValues.Load();
            DamagePatcher.Register(harmony);

            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.Damage.RoundStart", OnRoundStart);
            LuaCsSetup.Instance.Hook.Add("think", "Touhou.Damage.Think", OnThink);
            RegisterCommands();

            // 新实例上线：对已存在物品做一次归一 + 应用（首次启动时通常无物品，是空操作）
            DamagePatcher.ApplyAll();

            DamageLog.Log("伤害设置插件已加载（应用器生效）");
        }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.Damage.RoundStart");
            LuaCsSetup.Instance.Hook.Remove("think", "Touhou.Damage.Think");
            foreach (var cmd in CommandNames)
                LuaCsSetup.Instance.Game.RemoveCommand(cmd);
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            DamageLog.Log("伤害设置插件已卸载");
        }

        static object OnRoundStart(object[] args)
        {
            // XML 配置与玩家值都可能被外部编辑过；重载后全量重应用（幂等）
            DamageConfig.Load();
            DamageValues.Load();
            DamagePatcher.ApplyAll();
            return null;
        }

        static int thinkCounter;
        static object OnThink(object[] args)
        {
            // 玩家值文件轮询（命令 / UI / 手改都会落盘）：约 1 秒检查一次 mtime
            if (++thinkCounter < 60) return null;
            thinkCounter = 0;
            try
            {
                if (!DamagePatcher.IsAuthority) return null;
                if (!File.Exists(DamageValues.ConfigPath)) return null;
                var mtime = File.GetLastWriteTime(DamageValues.ConfigPath);
                if (mtime == DamageValues.LastConfigMtime) return null;
                DamageValues.Load();
                DamagePatcher.ApplyAll();
            }
            catch (Exception ex) { DamageLog.Warn($"数值轮询出错：{ex.Message}"); }
            return null;
        }

        static void RegisterCommands()
        {
            var game = LuaCsSetup.Instance.Game;
            game.AddCommand("damage_reload", "重载伤害设置配置（Config/damage_settings.xml + 玩家值文件）",
                args => { DamageConfig.Load(); DamageValues.Load(); DamagePatcher.ApplyAll(); }, null, false);
            game.AddCommand("damage_list", "列出伤害设置分组/规则/当前数值/场上实例统计",
                args => DamageDump.PrintGroups(), null, false);
            game.AddCommand("damage_dump", "审计：damage_dump [all|分组id|物品identifier]，输出到存档目录 TouhouDamageDump.txt",
                args => DamageDump.Run(CommandArgs(args).FirstOrDefault() ?? "all"), null, false);
            game.AddCommand("damage_set", "改数值：damage_set <组> <damage|pen|def> <值>（pen 支持 add:0.05 / multiply:1.25）",
                args =>
                {
                    var a = CommandArgs(args);
                    if (a.Length < 3) { DamageLog.Warn("用法：damage_set <组> <damage|pen|def> <值>"); return; }
                    if (!DamagePatcher.IsAuthority) { DamageLog.Warn("仅主机/单机可改数值"); return; }
                    if (!DamageValues.SetValue(a[0], a[1], a[2], out var err))
                    {
                        DamageLog.Warn($"damage_set 失败：{err}");
                        return;
                    }
                    DamagePatcher.ApplyAll();
                    string label = DamageConfig.Groups.TryGetValue(a[0], out var grp) ? $"（{grp.Label}，{grp.Items.Count} 件）" : "";
                    DamageLog.Log($"已设置：{a[0]}{label} {a[1]} = {a[2]}");
                }, null, false);
            game.AddCommand("damage_reset", "重置数值：damage_reset [组|all]（回 XML 配置默认值）",
                args =>
                {
                    if (!DamagePatcher.IsAuthority) { DamageLog.Warn("仅主机/单机可改数值"); return; }
                    var a = CommandArgs(args);
                    DamageValues.ResetToDefaults(a.Length > 0 ? a[0] : "all");
                    DamagePatcher.ApplyAll();
                    DamageLog.Log($"已重置为默认值：{(a.Length > 0 ? a[0] : "all")}");
                }, null, false);
            game.AddCommand("damage_apply", "强制全量重应用（调试用）",
                args => DamagePatcher.ApplyAll(), null, false);
            game.AddCommand("damage_lv", "档位快选：damage_lv <1|2|3>（LV1 略弱于补强 / LV2 等于补强 / LV3 补强×2）",
                args =>
                {
                    if (!DamagePatcher.IsAuthority) { DamageLog.Warn("仅主机/单机可改数值"); return; }
                    var a = CommandArgs(args);
                    if (a.Length < 1 || !int.TryParse(a[0], out int lv) || lv < 1 || lv > 3)
                    {
                        DamageLog.Warn("用法：damage_lv <1|2|3>");
                        return;
                    }
                    if (!DamageValues.ApplyTier(lv, out var err)) { DamageLog.Warn($"档位应用失败：{err}"); return; }
                    DamagePatcher.ApplyAll();
                    DamageLog.Log($"已应用档位 LV{lv}（各分组按档位写入，可用 damage_list 查看）");
                }, null, false);
            game.AddCommand("damage_probe", "对象级探测：damage_probe <物品identifier>，打印该物品各数据点的底值/当前值/跟踪状态",
                args => DamageDump.Probe(CommandArgs(args).FirstOrDefault()), null, false);
        }

        /// <summary>命令参数形状兼容：LuaCs 可能给 object[]{"a"}、object[]{ string[]{"a","b"} } 或混合。</summary>
        static string[] CommandArgs(object[] args)
        {
            if (args == null || args.Length == 0) return Array.Empty<string>();
            if (args.Length == 1)
            {
                if (args[0] is string[] arr) return arr;
                if (args[0] is string s) return new[] { s };
                return new[] { args[0]?.ToString() ?? "" };
            }
            if (args.All(a => a is string)) return args.Cast<string>().ToArray();
            foreach (var a in args)
                if (a is string[] arr2) return arr2;
            return new[] { args[0]?.ToString() ?? "" };
        }
    }

    public static class DamageLog
    {
        public static bool DebugMode = false; // 调参时改 true

        public static void Log(string msg) => LuaCsLogger.LogMessage($"[伤害] {msg}", Color.LightGreen);
        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[伤害] {msg}", Color.Orange);
        public static void Debug(string msg)
        {
            if (DebugMode) LuaCsLogger.LogMessage($"[伤害][dbg] {msg}", Color.LightGray);
        }
    }

    /// <summary>一个可调分组（武器组或防具组）。</summary>
    public sealed class DamageGroup
    {
        public string Id = "";
        public string Label = "";
        public bool IsArmor;
        /// <summary>武器组：伤害倍率。</summary>
        public float DefaultDamage = 1f;
        /// <summary>武器组：穿甲模式 add / multiply，二选一生效。</summary>
        public string PenetrationMode = "add";
        public float PenetrationValue = 0f;
        /// <summary>防具组：防御倍率 M。</summary>
        public float DefaultDefense = 1f;

        /// <summary>档位预设（LV1/LV2/LV3），由配置 XML 的 &lt;Tier&gt; 定义。</summary>
        public readonly Dictionary<int, DamageTier> Tiers = new Dictionary<int, DamageTier>();

        public readonly HashSet<string> Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>一个档位预设（LV1 略弱于补强 / LV2 等于补强 / LV3 补强×2）。</summary>
    public sealed class DamageTier
    {
        public float Damage = 1f;
        public string PenMode = "add";
        public float PenValue = 0f;
        public float Defense = 1f;
    }

    /// <summary>
    /// 配置（Config/damage_settings.xml，扫描所有启用内容包按加载顺序合并：同 id 分组合并、后到覆盖属性）。
    /// </summary>
    public static class DamageConfig
    {
        public const string FileName = "damage_settings.xml";
        public static string ConfigPath => Path.Combine(DamagePlugin.PackageDir, "Config", FileName);

        // 规则（可被配置文件覆盖；默认值 = 设计稿 §3）
        public static readonly HashSet<string> DamageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "damage", "burn", "bleeding", "bloodloss" };
        public static StatusEffect.TargetType EffectTargets =
            StatusEffect.TargetType.Limb | StatusEffect.TargetType.UseTarget |
            StatusEffect.TargetType.AllLimbs | StatusEffect.TargetType.LastLimb;
        public static float DefenseReductionCap = 1f;

        public static readonly Dictionary<string, DamageGroup> Groups = new Dictionary<string, DamageGroup>(StringComparer.OrdinalIgnoreCase);
        public static readonly Dictionary<string, DamageGroup> ByItem = new Dictionary<string, DamageGroup>(StringComparer.OrdinalIgnoreCase);
        public static int FileCount;
        public static string LastError = "";

        public static void Load()
        {
            Groups.Clear();
            ByItem.Clear();
            FileCount = 0;
            LastError = "";
            DamageTypes.Clear();
            DamageTypes.Add("damage");
            DamageTypes.Add("burn");
            DamageTypes.Add("bleeding");
            DamageTypes.Add("bloodloss");
            EffectTargets =
                StatusEffect.TargetType.Limb | StatusEffect.TargetType.UseTarget |
                StatusEffect.TargetType.AllLimbs | StatusEffect.TargetType.LastLimb;
            DefenseReductionCap = 1f;

            try
            {
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    string path;
                    try { path = Path.Combine(pkg.Dir, "Config", FileName); }
                    catch { continue; }
                    if (!File.Exists(path)) continue;
                    MergeFile(path, pkg.Name);
                    FileCount++;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                DamageLog.Warn($"配置扫描失败：{ex.Message}");
            }

            RebuildIndex();
            ValidateAgainstContent();

            if (FileCount == 0)
                DamageLog.Warn($"未找到任何 {FileName}（预期位置：{ConfigPath}）");
            else
                DamageLog.Log($"配置已加载：文件 {FileCount}，分组 {Groups.Count}，物品条目 {ByItem.Count}");
        }

        static void MergeFile(string path, string sourceName)
        {
            XElement root;
            try { root = XDocument.Load(path).Root; }
            catch (Exception ex) { DamageLog.Warn($"配置解析失败（{sourceName}）：{ex.Message}"); return; }
            if (root == null) return;

            var rules = root.Element("Rules");
            if (rules != null)
            {
                var dtEl = rules.Element("DamageTypes");
                if (dtEl != null)
                {
                    DamageTypes.Clear();
                    foreach (var tok in dtEl.Value.Split(','))
                        if (!string.IsNullOrWhiteSpace(tok)) DamageTypes.Add(tok.Trim());
                }
                var etEl = rules.Element("EffectTargets");
                if (etEl != null)
                {
                    var mask = (StatusEffect.TargetType)0;
                    foreach (var tok in etEl.Value.Split(','))
                    {
                        var t = tok.Trim();
                        if (t.Length == 0) continue;
                        if (Enum.TryParse(t, true, out StatusEffect.TargetType parsed)) mask |= parsed;
                        else DamageLog.Warn($"EffectTargets 无法识别：{t}（{sourceName}）");
                    }
                    if (mask != 0) EffectTargets = mask;
                }
                var capEl = rules.Element("DefenseReductionCap");
                if (capEl != null)
                    DefenseReductionCap = ParseFloat((string)capEl.Attribute("value"), "DefenseReductionCap", DefenseReductionCap, sourceName);
                DefenseReductionCap = Math.Clamp(DefenseReductionCap, 0f, 1f);
            }

            foreach (var el in root.Elements("Group"))
            {
                string id = (string)el.Attribute("id");
                if (string.IsNullOrEmpty(id)) { DamageLog.Warn($"分组缺 id（{sourceName}），已跳过"); continue; }

                if (!Groups.TryGetValue(id, out var g))
                {
                    g = new DamageGroup { Id = id, Label = id };
                    Groups[id] = g;
                }
                string label = (string)el.Attribute("label");
                if (!string.IsNullOrEmpty(label)) g.Label = label;
                string kind = (string)el.Attribute("kind");
                if (!string.IsNullOrEmpty(kind)) g.IsArmor = kind.Equals("armor", StringComparison.OrdinalIgnoreCase);
                g.DefaultDamage = ParseFloat((string)el.Attribute("defaultDamage"), "defaultDamage", g.DefaultDamage, sourceName);
                string pm = (string)el.Attribute("penetrationMode");
                if (!string.IsNullOrEmpty(pm))
                {
                    g.PenetrationMode = pm.Trim().ToLowerInvariant();
                    if (g.PenetrationMode != "add" && g.PenetrationMode != "multiply")
                    {
                        DamageLog.Warn($"penetrationMode 只支持 add/multiply（{sourceName} / {id}），已回退 add");
                        g.PenetrationMode = "add";
                    }
                }
                g.PenetrationValue = ParseFloat((string)el.Attribute("penetrationValue"), "penetrationValue", g.PenetrationValue, sourceName);
                g.DefaultDefense = ParseFloat((string)el.Attribute("defaultDefense"), "defaultDefense", g.DefaultDefense, sourceName);

                foreach (var tierEl in el.Elements("Tier"))
                {
                    string lvRaw = (string)tierEl.Attribute("level");
                    if (!int.TryParse(lvRaw, out int lv) || lv < 1)
                    {
                        DamageLog.Warn($"<Tier> level 无效：{lvRaw}（{sourceName} / {id}），已跳过");
                        continue;
                    }
                    string tpm = (string)tierEl.Attribute("penMode");
                    string tpmNorm = string.IsNullOrEmpty(tpm) ? "add" : tpm.Trim().ToLowerInvariant();
                    if (tpmNorm != "add" && tpmNorm != "multiply")
                    {
                        DamageLog.Warn($"Tier penMode 只支持 add/multiply（{sourceName} / {id}），已回退 add");
                        tpmNorm = "add";
                    }
                    g.Tiers[lv] = new DamageTier
                    {
                        Damage = ParseFloat((string)tierEl.Attribute("damage"), "damage", 1f, sourceName),
                        PenMode = tpmNorm,
                        PenValue = ParseFloat((string)tierEl.Attribute("penValue"), "penValue", 0f, sourceName),
                        Defense = ParseFloat((string)tierEl.Attribute("defense"), "defense", 1f, sourceName),
                    };
                }

                foreach (var itemEl in el.Elements("Item"))
                {
                    string iid = (string)itemEl.Attribute("id");
                    if (!string.IsNullOrEmpty(iid)) g.Items.Add(iid.Trim());
                }
            }
        }

        static void RebuildIndex()
        {
            foreach (var g in Groups.Values)
            {
                foreach (var iid in g.Items)
                {
                    if (ByItem.TryGetValue(iid, out var prev) && prev != g)
                        DamageLog.Warn($"物品 {iid} 同时出现在分组 {prev.Id} 与 {g.Id}，以后者为准");
                    ByItem[iid] = g;
                }
            }
        }

        static void ValidateAgainstContent()
        {
            // 内容预加载未就绪（或插件加载早于内容）时跳过，避免整页"未找到"误报；roundStart 会重载复查
            if (!ItemPrefab.Prefabs.Any())
            {
                DamageLog.Debug("物品校验跳过：ItemPrefab.Prefabs 尚未就绪");
                return;
            }

            int found = 0, missing = 0;
            var sample = new List<string>();
            foreach (var iid in ByItem.Keys)
            {
                if (ItemPrefab.Prefabs.ContainsKey(iid)) { found++; continue; }
                missing++;
                if (sample.Count < 20) sample.Add(iid);
            }
            if (missing > 0)
                DamageLog.Warn($"配置引用的物品有 {missing} 个未在内容中找到（如：{string.Join(", ", sample)}{(missing > sample.Count ? " …" : "")}）");
            DamageLog.Debug($"物品校验：找到 {found}，未找到 {missing}");
        }

        public static DamageGroup FindGroup(Item item)
        {
            if (item?.Prefab == null) return null;
            return ByItem.TryGetValue(item.Prefab.Identifier.Value, out var g) ? g : null;
        }

        static float ParseFloat(string raw, string what, float fallback, string source)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            DamageLog.Warn($"数值解析失败：{what}=「{raw}」（{source}），沿用 {fallback}");
            return fallback;
        }
    }

    /// <summary>一条候选数据点（审计用）。OldValue=底值（已跟踪）或当前值，NewValue=按当前倍率的预计生效值。</summary>
    public sealed class DamageScanEntry
    {
        public string Context = "";   // 组件/效果路径，如 Projectile.Attack / Projectile.StatusEffect(OnImpact) target=Limb
        public string Kind = "";      // affliction / penetration / defense
        public string Label = "";     // affliction identifier / 防具条目
        public string TypeText = "";  // affliction 类型 / 防御
        public object Target;         // 运行期对象（Affliction / Attack / DamageModifier），damage_probe 用
        public float OldValue;
        public float NewValue;
        public bool WouldScale;
        public string Reason = "";    // 跳过原因
        public string Note = "";      // 附加说明（封顶 / 爆炸穿甲等）
    }

    /// <summary>
    /// 只读采集器：递归遍历"从物品可达的所有伤害数据点"（设计稿 §4）。
    /// 物品 → 组件（Projectile/MeleeWeapon/…全类型）→ Attack / 组件 StatusEffect
    ///      → afflictions、Explosions → Attack…（含 Attack 内嵌 StatusEffect）；
    /// 防具组另走 Wearable.DamageModifiers。规则只决定乘不乘。
    /// </summary>
    public static class DamageScanner
    {
        public static List<DamageScanEntry> Collect(Item item, DamageGroup group)
        {
            var list = new List<DamageScanEntry>();
            if (item == null || group == null) return list;
            try
            {
                if (group.IsArmor) CollectArmor(item, group, list);
                else CollectWeapon(item, group, list);
            }
            catch (Exception ex)
            {
                DamageLog.Warn($"采集出错（{item.Prefab?.Identifier.Value}）：{ex.Message}");
            }
            return list;
        }

        static void CollectWeapon(Item item, DamageGroup g, List<DamageScanEntry> outp)
        {
            foreach (var ic in item.Components)
            {
                Attack attack = null;
                if (ic is Projectile proj) attack = proj.Attack;
                else if (ic is MeleeWeapon melee) attack = melee.Attack;
                if (attack != null) CollectAttack(attack, $"{ic.GetType().Name}.Attack", g, outp, isExplosion: false);

                CollectComponentEffects(ic, g, outp);
            }
        }

        static void CollectComponentEffects(ItemComponent ic, DamageGroup g, List<DamageScanEntry> outp)
        {
            var lists = ic.statusEffectLists;
            if (lists == null) return;
            foreach (var kv in lists)
                foreach (var se in kv.Value)
                    CollectStatusEffect(se, $"{ic.GetType().Name}.StatusEffect({se.type})", g, outp);
        }

        static void CollectAttack(Attack attack, string context, DamageGroup g, List<DamageScanEntry> outp, bool isExplosion)
        {
            // 穿甲（加/乘二选一，封顶 0~1；爆炸穿甲按设计稿默认跟随，当前游戏内无实际效果）
            // 数值口径：OldValue=底值（已跟踪）或当前值；NewValue=按当前倍率预计生效值
            DamageValues.GetPen(g.Id, out var penMode, out var penValue);
            float basePen = DamagePatcher.BaseOrLive(attack, attack.Penetration);
            float rawPen = penMode == "multiply"
                ? basePen * penValue
                : basePen + penValue;
            float newPen = Math.Clamp(rawPen, 0f, 1f);
            bool penScales = penMode == "multiply"
                ? Math.Abs(penValue - 1f) > 0.0001f
                : Math.Abs(penValue) > 0.0001f;

            var penEntry = new DamageScanEntry
            {
                Context = context,
                Kind = "penetration",
                Label = "穿甲",
                Target = attack,
                OldValue = basePen,
                NewValue = newPen,
                WouldScale = penScales,
                Reason = penScales ? "" : "数值为 0/1，无变化",
            };
            var notes = new List<string>();
            if (isExplosion) notes.Add("爆炸穿甲：当前游戏内无实际效果（伤害链不传 penetration），按设计稿默认跟随");
            if (Math.Abs(rawPen - newPen) > 0.0001f) notes.Add("触发 0~1 封顶");
            penEntry.Note = string.Join("；", notes);
            outp.Add(penEntry);

            foreach (var aff in attack.Afflictions.Keys)
                AddAfflictionEntry(aff, context, g, outp, null);

            foreach (var se in attack.StatusEffects)
                CollectStatusEffect(se, context + " > StatusEffect(" + se.type + ")", g, outp);
        }

        static void CollectStatusEffect(StatusEffect se, string context, DamageGroup g, List<DamageScanEntry> outp)
        {
            string ctx = context + " target=" + se.targetTypes;
            foreach (var aff in se.Afflictions)
                AddAfflictionEntry(aff, ctx, g, outp, se.targetTypes);
            foreach (var exp in se.Explosions)
                CollectAttack(exp.Attack, ctx + " > Explosion.Attack", g, outp, isExplosion: true);
        }

        static void AddAfflictionEntry(Affliction aff, string context, DamageGroup g, List<DamageScanEntry> outp,
            StatusEffect.TargetType? effectTargets)
        {
            var prefab = aff.Prefab;
            string type = prefab?.AfflictionType.Value ?? "";
            string id = prefab?.Identifier.Value ?? "?";
            bool typeOk = DamageConfig.DamageTypes.Contains(type);
            bool targetOk = effectTargets == null || (effectTargets.Value & DamageConfig.EffectTargets) != 0;
            bool scale = typeOk && targetOk;

            string reason = "";
            if (!typeOk) reason = $"类型 {type} 不在白名单";
            else if (!targetOk) reason = $"目标 {effectTargets.Value} 不含配置的 Limb/UseTarget 族";

            float baseVal = DamagePatcher.BaseOrLive(aff, aff.Strength);
            outp.Add(new DamageScanEntry
            {
                Context = context,
                Kind = "affliction",
                Label = id,
                TypeText = type,
                Target = aff,
                OldValue = baseVal,
                NewValue = scale ? baseVal * DamageValues.GetDamage(g.Id) : baseVal,
                WouldScale = scale,
                Reason = reason,
            });
        }

        static void CollectArmor(Item item, DamageGroup g, List<DamageScanEntry> outp)
        {
            float m = DamageValues.GetDefense(g.Id);
            foreach (var ic in item.Components)
            {
                if (!(ic is Wearable wearable)) continue;
                foreach (var dm in wearable.DamageModifiers)
                {
                    float v = DamagePatcher.BaseOrLive(dm, dm.DamageMultiplier);
                    float baseReduction = 1f - v;
                    float reduction = baseReduction * m;
                    bool clamped = false;
                    if (m > 1f)
                    {
                        // 减伤上限（默认 1.0 不设限）；只截断增强方向，且不低于原有的减伤
                        float cap = Math.Max(baseReduction, DamageConfig.DefenseReductionCap);
                        if (reduction > cap) { reduction = cap; clamped = true; }
                    }
                    float nv = Math.Clamp(1f - reduction, 0f, 1f);
                    bool scale = Math.Abs(nv - v) > 0.0001f;

                    string labelText = !string.IsNullOrEmpty(dm.AfflictionTypes) ? dm.AfflictionTypes : dm.AfflictionIdentifiers;
                    if (string.IsNullOrEmpty(labelText)) labelText = "（全部）";

                    outp.Add(new DamageScanEntry
                    {
                        Context = "Wearable.DamageModifier",
                        Kind = "defense",
                        Label = labelText,
                        TypeText = "防御",
                        Target = dm,
                        OldValue = v,
                        NewValue = nv,
                        WouldScale = scale,
                        Reason = scale ? "" : (Math.Abs(m - 1f) < 0.0001f ? "M=1，无变化" : "封顶后仍无变化（原值已是 0/1）"),
                        Note = clamped ? "触发减伤上限" : "",
                    });
                }
            }
        }
    }

    /// <summary>审计报告与命令输出。</summary>
    public static class DamageDump
    {
        public const string DumpFileName = "TouhouDamageDump.txt";

        public static void PrintGroups()
        {
            try
            {
                LuaCsLogger.LogMessage($"[伤害] 配置：文件 {DamageConfig.FileCount} · 分组 {DamageConfig.Groups.Count} · 物品条目 {DamageConfig.ByItem.Count}", Color.LightGreen);
                LuaCsLogger.LogMessage($"[伤害] 规则：DamageTypes=[{string.Join(",", DamageConfig.DamageTypes)}] · EffectTargets=[{DamageConfig.EffectTargets}] · 减伤上限={Fmt(DamageConfig.DefenseReductionCap)}", Color.LightGreen);
                if (DamageConfig.Groups.Count == 0)
                {
                    LuaCsLogger.LogMessage("[伤害] （没有分组；检查 Config/damage_settings.xml 是否存在）", Color.Orange);
                    return;
                }

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Item.ItemList.ToList())
                {
                    if (item == null || item.Removed) continue;
                    var g = DamageConfig.FindGroup(item);
                    if (g == null) continue;
                    counts[g.Id] = counts.TryGetValue(g.Id, out var c) ? c + 1 : 1;
                }

                foreach (var g in DamageConfig.Groups.Values)
                {
                    counts.TryGetValue(g.Id, out var inst);
                    string axis = g.IsArmor
                        ? $"防御 M={Fmt(DamageValues.GetDefense(g.Id))}"
                        : $"伤害 ×{Fmt(DamageValues.GetDamage(g.Id))} · 穿甲 {PenText(g.Id)}";
                    LuaCsLogger.LogMessage($"[伤害]   · {g.Id}（{g.Label}）{g.Items.Count} 件 · 场上实例 {inst} · {axis}", Color.LightGreen);
                }
            }
            catch (Exception ex)
            {
                DamageLog.Warn($"damage_list 出错：{ex.Message}");
            }
        }

        static string PenText(string gid)
        {
            DamageValues.GetPen(gid, out var mode, out var v);
            return (mode == "multiply" ? "×" : "+") + Fmt(v);
        }

        public static void Run(string selector)
        {
            try { RunInternal(selector); }
            catch (Exception ex) { DamageLog.Warn($"dump 出错：{ex.Message}"); }
        }

        static void RunInternal(string selector)
        {
            selector = string.IsNullOrWhiteSpace(selector) ? "all" : selector.Trim();
            bool all = selector.Equals("all", StringComparison.OrdinalIgnoreCase);
            DamageGroup onlyGroup = null;
            string onlyItemId = null;
            if (!all && !DamageConfig.Groups.TryGetValue(selector, out onlyGroup))
                onlyItemId = selector;

            var items = Item.ItemList.ToList();
            var selected = new List<(Item item, DamageGroup group)>();
            var instanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null || item.Removed || item.Prefab == null) continue;
                string pid = item.Prefab.Identifier.Value;
                if (onlyItemId != null && !pid.Equals(onlyItemId, StringComparison.OrdinalIgnoreCase)) continue;
                var g = DamageConfig.FindGroup(item);
                if (onlyGroup != null && g != onlyGroup) continue;
                if (all && g == null) continue;
                instanceCounts[pid] = instanceCounts.TryGetValue(pid, out var c) ? c + 1 : 1;
                selected.Add((item, g));
            }

            if (selected.Count == 0)
            {
                DamageLog.Warn(onlyItemId != null
                    ? $"场上没有物品「{onlyItemId}」的实例（或它不在任何分组里）"
                    : "没有匹配的物品");
                return;
            }

            var report = new List<string>();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groupStats = new Dictionary<string, (int items, int entries, int scale)>(StringComparer.OrdinalIgnoreCase);
            int prefabCount = 0, entryCount = 0, scaleCount = 0;

            foreach (var (item, group) in selected)
            {
                string pid = item.Prefab.Identifier.Value;
                if (!processed.Add(pid)) continue;
                prefabCount++;
                int inst = instanceCounts.TryGetValue(pid, out var ic) ? ic : 1;

                if (group == null)
                {
                    report.Add($"### {pid}   实例×{inst}");
                    report.Add("    ⚠ 不在任何分组中（未扫描；如需纳入请修改 Config/damage_settings.xml）");
                    report.Add("");
                    continue;
                }

                var entries = DamageScanner.Collect(item, group);
                int localScale = entries.Count(e => e.WouldScale);
                entryCount += entries.Count;
                scaleCount += localScale;

                report.Add($"### [{group.Id}] {pid}   实例×{inst} · 条目 {entries.Count}（会乘 {localScale} / 跳过 {entries.Count - localScale}）");
                if (entries.Count == 0)
                {
                    report.Add("    （无伤害数据点）");
                }
                else
                {
                    string lastCtx = null;
                    foreach (var e in entries)
                    {
                        if (e.Context != lastCtx)
                        {
                            report.Add($"  {e.Context}");
                            lastCtx = e.Context;
                        }
                        string kindTag = e.Kind == "penetration" ? "穿甲" : e.Kind == "defense" ? "防御" : "伤害";
                        string typeSuffix = string.IsNullOrEmpty(e.TypeText) ? "" : $"（{e.TypeText}）";
                        string valLine = e.WouldScale
                            ? $"{Fmt(e.OldValue)} → {Fmt(e.NewValue)}"
                            : $"{Fmt(e.OldValue)} → 跳过（{e.Reason}）";
                        string note = string.IsNullOrEmpty(e.Note) ? "" : $"   // {e.Note}";
                        report.Add($"      [{kindTag}] {e.Label}{typeSuffix}  {valLine}{note}");
                    }
                }
                report.Add("");

                if (!groupStats.TryGetValue(group.Id, out var st)) st = (0, 0, 0);
                groupStats[group.Id] = (st.items + 1, st.entries + entries.Count, st.scale + localScale);
            }

            var sb = new StringBuilder();
            sb.AppendLine("# 东方-武器伤害与防具抗性设置 · damage_dump 审计报告（只读；不改数值）");
            sb.AppendLine($"# 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# 规则：伤害类型白名单=[{string.Join(",", DamageConfig.DamageTypes)}] · 效果目标=[{DamageConfig.EffectTargets}] · 防具减伤上限={Fmt(DamageConfig.DefenseReductionCap)}");
            sb.AppendLine("# 数值口径：每条为 底值 → 按当前倍率的预计生效值（已应用的物品运行时即为后者；跳过项列出底值）。");
            sb.AppendLine("# 穿甲按各分组模式（加/乘）预估并封顶 0~1；爆炸穿甲当前游戏内无实际效果（预留）；防具条目为承伤系数。");
            sb.AppendLine($"# 配置来源：{DamageConfig.FileCount} 个文件；物品条目 {DamageConfig.ByItem.Count} 个。");
            sb.AppendLine();
            foreach (var line in report) sb.AppendLine(line);
            sb.AppendLine("==== 汇总 ====");
            foreach (var g in DamageConfig.Groups.Values)
            {
                groupStats.TryGetValue(g.Id, out var st);
                sb.AppendLine($"  [{g.Id}] {g.Label}：扫描物品 {st.items} · 条目 {st.entries}（会乘 {st.scale}）");
            }
            sb.AppendLine($"  合计：物品 {prefabCount}（实例 {selected.Count}）· 条目 {entryCount}（会乘 {scaleCount} / 跳过 {entryCount - scaleCount}）");

            string path = Path.Combine(DamageValues.SaveDir(), DumpFileName);
            File.WriteAllText(path, sb.ToString());

            LuaCsLogger.LogMessage($"[伤害] dump 完成：物品 {prefabCount}（实例 {selected.Count}）· 条目 {entryCount}（会乘 {scaleCount} / 跳过 {entryCount - scaleCount}）", Color.LightGreen);
            LuaCsLogger.LogMessage($"[伤害] 报告：{path}", Color.LightGreen);
        }

        /// <summary>对象级探针：打印某物品各数据点的底值 / 当前运行值 / 跟踪状态，用于核对应用器是否真的改了数值。</summary>
        public static void Probe(string identifier)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    DamageLog.Warn("用法：damage_probe <物品identifier>");
                    return;
                }

                Item found = null;
                foreach (var item in Item.ItemList.ToList())
                {
                    if (item == null || item.Removed || item.Prefab == null) continue;
                    if (item.Prefab.Identifier.Value.Equals(identifier, StringComparison.OrdinalIgnoreCase)) { found = item; break; }
                }
                if (found == null) { DamageLog.Warn($"场上没有「{identifier}」的实例"); return; }

                var g = DamageConfig.FindGroup(found);
                if (g == null) { DamageLog.Warn($"「{identifier}」不在任何分组中"); return; }

                var entries = DamageScanner.Collect(found, g);
                string axis = g.IsArmor
                    ? $"防御 M={Fmt(DamageValues.GetDefense(g.Id))}"
                    : $"伤害 ×{Fmt(DamageValues.GetDamage(g.Id))}";
                LuaCsLogger.LogMessage($"[伤害] 探测 [{g.Id}] {identifier}：{entries.Count} 个数据点 · {axis} · 权威端={DamagePatcher.IsAuthority}", Color.LightGreen);
                LuaCsLogger.LogMessage($"[伤害]   诊断：构造补丁已挂载={DamagePatcher.HookRegistered} · 钩子累计应用 {DamagePatcher.HookAppliedObjects} 个对象 · 上次全量归一转除 {DamagePatcher.LastNormalizedObjects} 个", Color.LightGray);

                int shown = 0;
                foreach (var e in entries)
                {
                    if (shown++ >= 40)
                    {
                        LuaCsLogger.LogMessage("[伤害]   …（超过 40 条已截断）", Color.LightGray);
                        break;
                    }
                    string kind = e.Kind == "penetration" ? "穿甲" : e.Kind == "defense" ? "防御" : "伤害";
                    string typeSuffix = string.IsNullOrEmpty(e.TypeText) ? "" : $"（{e.TypeText}）";
                    bool tracked = DamagePatcher.TryGetBase(e.Target, out var baseVal);
                    string valueText = !e.WouldScale
                        ? $"当前 {Fmt(e.OldValue)} → 跳过（{e.Reason}）"
                        : tracked
                            ? $"底值 {Fmt(baseVal)} → 已应用 {Fmt(e.NewValue)}"
                            : $"当前 {Fmt(e.OldValue)} → 预计应用 {Fmt(e.NewValue)}";
                    // 回归哨兵：伤害路径读 NonClampedStrength，若与 Strength 不一致说明补丁写法回退到了属性 setter
                    if (e.Target is Affliction affTarget &&
                        Math.Abs(affTarget.NonClampedStrength - affTarget.Strength) > 0.0001f)
                        valueText += $"  ⚠NonClamped={Fmt(affTarget.NonClampedStrength)}（伤害路径读它）";
                    LuaCsLogger.LogMessage($"[伤害]   [{kind}] {e.Label}{typeSuffix} {valueText}", Color.LightGreen);
                }
            }
            catch (Exception ex)
            {
                DamageLog.Warn($"damage_probe 出错：{ex.Message}");
            }
        }

        static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
