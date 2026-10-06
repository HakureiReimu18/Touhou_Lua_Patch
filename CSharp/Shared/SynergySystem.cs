using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

// 角色羁绊：两名（及以上）特定角色同时在场 → 给指定目标挂增益。
// 方案、字段说明、装束身份 aff 全表见 Docs/角色羁绊系统-设计方案.md。
//
// 与 Touhou.Bond（绑定护符：玩家自选配对 + 伤害共担）是两回事，互不干扰，别复用同一批 affliction 名字。
// 判定依据是装束自己刷的身份 aff（每 0.5 秒一次、duration 1 秒），所以：
//   · 脱装/换装/死亡/重生/中途加入都会在 0.5~2 秒内自动跟上，不需要额外事件；
//   · 增益 affliction 必须带 duration（自过期兜底），断链时还会主动移除一次。
//
// 配置：每个内容包各放一份 Config/synergy_config.xml，扫描全部启用包按加载顺序合并；
//      同 id 后者覆盖前者，remove="true" 可删掉别的包加的条目。

namespace Touhou.Synergy
{
    /// <summary>
    /// 插件入口。只 Patch 本命名空间的类，避免与主插件（Touhou.Affixes）/ 追踪 / 绑定护符重复打补丁。
    /// </summary>
    public sealed class SynergyPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public static readonly string[] CommandNames = { "synergy_reload", "synergy_list" };

        static string packageDir;
        /// <summary>模组目录：优先用主插件已解析的路径，插件初始化顺序不保证它先跑。</summary>
        public static string PackageDir
        {
            get
            {
                if (packageDir != null) return packageDir;
                packageDir = Touhou.Affixes.Mod.Package?.Dir;
                if (string.IsNullOrEmpty(packageDir) &&
                    LuaCsSetup.Instance.PluginPackageManager.TryGetPackageForPlugin<SynergyPlugin>(out var pkg))
                    packageDir = pkg.Dir;
                return packageDir ?? ".";
            }
        }

        public void Initialize()
        {
            harmony = new Harmony("touhou.synergy");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone)
            {
                SynergyLog.Debug("OnLoadCompleted: 内容重载，补丁/命令已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(SynergyTickPatch));

            SynergyConfig.Load();
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.Synergy.RoundStart", OnRoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.Synergy.RoundEnd", OnRoundEnd);
            RegisterCommands();

            SynergyLog.Log("角色羁绊插件已加载");
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.Synergy.RoundStart");
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.Synergy.RoundEnd");
            foreach (var cmd in CommandNames)
                LuaCsSetup.Instance.Game.RemoveCommand(cmd);
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            SynergyEngine.ClearRuntime();
            SynergyLog.Log("角色羁绊插件已卸载");
        }

        object OnRoundStart(object[] args)
        {
            SynergyConfig.Load();
            SynergyEngine.OnRoundStart();
            return null;
        }

        object OnRoundEnd(object[] args)
        {
            SynergyEngine.OnRoundEnd();
            return null;
        }

        static void RegisterCommands()
        {
            var game = LuaCsSetup.Instance.Game;
            game.AddCommand("synergy_reload", "重载角色羁绊配置（扫描所有内容包的 Config/synergy_config.xml）", args =>
            {
                SynergyConfig.Load();
                SynergyLog.Log($"重载完成：羁绊 {SynergyConfig.Entries.Count} 条");
            }, null, false);

            game.AddCommand("synergy_list", "列出当前生效的角色羁绊", args =>
            {
                if (SynergyConfig.Entries.Count == 0) SynergyLog.Log("当前没有配置任何羁绊");
                else
                {
                    foreach (var e in SynergyConfig.Entries)
                    {
                        SynergyLog.Log($"[{e.Id}] 成员：{string.Join(" + ", e.Members.ConvertAll(m => m.Display))} " +
                                       $"| 范围：{e.Scope} | 优先级：{e.Priority} | 互斥：{e.Exclusive} " +
                                       $"| 检测：{e.Detect} | 存活判定：{(e.AliveOnly ? "是" : "否")} | 模式：{e.Mode}");
                        foreach (var g in e.Grants)
                            SynergyLog.Log($"    → {g.Aff} 给 {g.To}（强度 {g.Strength}）");
                    }
                }
                SynergyLog.Log(SynergyEngine.PerfSummary);
            }, null, false);
        }
    }

    // ==================== 判定节拍 ====================
    // 挂点与 MainThreadSchedulerPatch / RagdollScaleTickPatch 相同：客户端/单人挂 GameMain.Update，
    // 专用服务器挂 GameServer.Update（在 DedicatedServer.dll，编译期不一定引用得到，用反射解析）。
    // 这里必须编译期分流：服务端程序集里没有 GameMain.Paused 这类客户端专有成员，
    // 不加 #if 会在服务端编译时直接 CS1061（实测踩过，整个程序集编译失败）。
    [HarmonyPatch]
    public static class SynergyTickPatch
    {
        static MethodBase TargetMethod()
        {
            // flags 必须带 NonPublic：Update 在 XNA 里是 protected override（公开化程序集里才是 public），
            // 只按 Public 找运行时会拿到 null，补丁整个打不上
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
#if SERVER
            // 服务端上下文只认 GameServer.Update：GameMain.Update 在这里要么不存在、要么不会被调用
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Barotrauma.Networking.GameServer");
                var m = t?.GetMethod("Update", flags);
                if (m != null) return m;
            }
            SynergyLog.Warn("SynergyTickPatch: 找不到 GameServer.Update，羁绊不会生效");
            return null;
#else
            var m = typeof(GameMain).GetMethod("Update", flags);
            if (m != null) return m;
            // 兜底：客户端上下文找不到 GameMain.Update 时再试 GameServer
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Barotrauma.Networking.GameServer");
                m = t?.GetMethod("Update", flags);
                if (m != null) return m;
            }
            SynergyLog.Warn("SynergyTickPatch: 找不到每帧 Update 挂点，羁绊不会生效");
            return null;
#endif
        }

        static void Postfix()
        {
            // 纯客户端上下文不跑；单人（NetworkMember == null）与服务器（IsServer）跑
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) return;
#if !SERVER
            // 暂停菜单是客户端专有功能：GameMain.Paused 在服务端程序集里不存在，必须编译期分流
            if (GameMain.Instance != null && GameMain.Instance.Paused) return;
#endif
            SynergyEngine.Tick();
        }
    }

    // ==================== 配置 ====================

    public enum SynergyScope { SameSub, SameTeam, Anywhere }
    public enum SynergyMode { WhilePresent, RoundStart }

    /// <summary>互斥范围：多条羁绊同时成立时，谁能和谁并存。</summary>
    public enum ExclusiveKind
    {
        None,       // 默认：可与其他羁绊同时生效（现状行为）
        Members,    // 与共用至少一个成员角色的其他羁绊互斥
        All,        // 与任何同时成立的羁绊互斥
    }

    /// <summary>成员检测范围：谁有资格算"在场成员"。</summary>
    public enum DetectScope
    {
        Crew,       // 默认：玩家 + 玩家队的人类船员（含 AI 船员）
        Players,    // 只认玩家操控的角色（联机里的真人）
        Any,        // 不限（含中立/敌对角色，只要带着对应身份 aff 或穿着对应装束）
    }

    /// <summary>成员标记：aff（装束身份 affliction）与 item（装束物品 identifier）二选一。推荐 aff。</summary>
    public sealed class SynergyMember
    {
        public string Aff;              // 原样文本，日志用
        public Identifier AffIdentifier;
        public string Item;
        public string Display => Aff ?? Item;
    }

    /// <summary>grant 的投放对象。</summary>
    public enum GrantTarget { Members, MemberIndex, Crew }

    public sealed class SynergyGrant
    {
        public string Aff;
        public float Strength = 1f;
        public GrantTarget Target = GrantTarget.Members;
        public int MemberIndex;         // Target == MemberIndex 时的 0-based 下标
        public string To;               // 原样文本，日志用
    }

    public sealed class SynergyEntry
    {
        public string Id;
        public SynergyScope Scope = SynergyScope.SameSub;
        public bool AliveOnly = true;
        public DetectScope Detect = DetectScope.Crew;
        public SynergyMode Mode = SynergyMode.WhilePresent;
        public int Priority;                                  // 越大越优先；缺省 0（同优先级按配置顺序）
        public ExclusiveKind Exclusive = ExclusiveKind.None;  // 互斥范围；缺省 none
        public readonly List<SynergyMember> Members = new List<SynergyMember>(2);
        public readonly List<SynergyGrant> Grants = new List<SynergyGrant>(2);
    }

    public static class SynergyConfig
    {
        public static readonly List<SynergyEntry> Entries = new List<SynergyEntry>();
        public static bool Debug;

        static readonly Dictionary<string, int> positionById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        static string ConfigPath => Path.Combine(SynergyPlugin.PackageDir, "Config", "synergy_config.xml");

        public static void Load()
        {
            Entries.Clear();
            positionById.Clear();
            Debug = false;

            EnsureOwnTemplate();

            int files = 0, packages = 0;
            try
            {
                // 扫描所有启用内容包的 Config/synergy_config.xml，按加载顺序合并（排后面的包覆盖先前的）
                foreach (var pkg in ContentPackageManager.RegularPackages)
                {
                    packages++;
                    string path;
                    try { path = Path.Combine(pkg.Dir, "Config", "synergy_config.xml"); }
                    catch { continue; }
                    if (!File.Exists(path)) continue;
                    MergeFile(path, pkg.Name);
                    files++;
                }
            }
            catch (Exception ex) { SynergyLog.Warn($"配置扫描失败：{ex.Message}"); }

            SynergyEngine.RebuildWatched();
            SynergyEngine.ResetPerf();
            SynergyLog.Log($"配置已加载：文件 {files}（扫描包 {packages}），羁绊 {Entries.Count} 条，" +
                           $"成员标记 {SynergyEngine.WatchedCount} 个");
        }

        static void MergeFile(string path, string sourceName)
        {
            XElement root;
            try { root = XDocument.Load(path).Root; }
            catch (Exception ex) { SynergyLog.Warn($"配置解析失败（{sourceName}）：{ex.Message}"); return; }
            if (root == null) return;

            if (IsTrue(root.Attribute("debug"))) Debug = true;

            // 根节点既可以是 <Synergies><Synergy/></Synergies>，也可以直接是 <Synergy/>
            IEnumerable<XElement> nodes = root.Name.LocalName == "Synergy"
                ? new[] { root }
                : root.Elements("Synergy");

            int added = 0, replaced = 0, removed = 0;
            foreach (var el in nodes)
            {
                string id = ((string)el.Attribute("id"))?.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    SynergyLog.Warn($"{sourceName}: 跳过没有 id 的 <Synergy>");
                    continue;
                }

                if (IsTrue(el.Attribute("remove")))
                {
                    if (positionById.TryGetValue(id, out int at))
                    {
                        Entries.RemoveAt(at);
                        positionById.Remove(id);
                        // 下标变了，重建索引
                        for (int i = at; i < Entries.Count; i++) positionById[Entries[i].Id] = i;
                        removed++;
                    }
                    continue;
                }

                var entry = ParseEntry(el, id, sourceName);
                if (entry == null) continue;

                if (positionById.TryGetValue(id, out int exist))
                {
                    Entries[exist] = entry;
                    replaced++;
                }
                else
                {
                    positionById[id] = Entries.Count;
                    Entries.Add(entry);
                    added++;
                }
            }

            if (added + replaced + removed > 0)
                SynergyLog.Log($"{sourceName}：新增 {added}，覆盖 {replaced}，移除 {removed}");
        }

        static SynergyEntry ParseEntry(XElement el, string id, string sourceName)
        {
            var entry = new SynergyEntry
            {
                Id = id,
                Scope = ParseScope((string)el.Attribute("scope")),
                AliveOnly = ParseBool(el.Attribute("alive_only"), true),
                Detect = ParseDetect((string)el.Attribute("detect")),
                Mode = ParseMode((string)el.Attribute("mode")),
                Priority = ParsePriority(el.Attribute("priority")),
                Exclusive = ParseExclusive((string)el.Attribute("exclusive")),
            };

            foreach (var m in el.Elements("Member"))
            {
                string aff = ((string)m.Attribute("aff"))?.Trim();
                string item = ((string)m.Attribute("item"))?.Trim();

                if (!string.IsNullOrEmpty(aff))
                {
                    if (!AfflictionPrefab.Prefabs.TryGet(aff, out _))
                    {
                        SynergyLog.Warn($"{sourceName} / {id}: 成员 aff 未注册，跳过该成员：{aff}");
                        continue;
                    }
                    entry.Members.Add(new SynergyMember { Aff = aff, AffIdentifier = new Identifier(aff) });
                }
                else if (!string.IsNullOrEmpty(item))
                {
                    if (ItemPrefab.Prefabs[item] == null)
                    {
                        SynergyLog.Warn($"{sourceName} / {id}: 成员装束未找到，跳过该成员：{item}");
                        continue;
                    }
                    entry.Members.Add(new SynergyMember { Item = item });
                }
                else
                {
                    SynergyLog.Warn($"{sourceName} / {id}: <Member> 缺少 aff 或 item，跳过");
                }
            }

            if (entry.Members.Count < 2)
            {
                SynergyLog.Warn($"{sourceName} / {id}: 有效成员不足 2 个，整条跳过");
                return null;
            }

            foreach (var g in el.Elements("Grant"))
            {
                string aff = ((string)g.Attribute("aff"))?.Trim();
                if (string.IsNullOrEmpty(aff))
                {
                    SynergyLog.Warn($"{sourceName} / {id}: <Grant> 缺少 aff，跳过");
                    continue;
                }
                if (!AfflictionPrefab.Prefabs.TryGet(aff, out _))
                {
                    SynergyLog.Warn($"{sourceName} / {id}: 增益 aff 未注册，跳过该 Grant：{aff}");
                    continue;
                }

                string to = (((string)g.Attribute("to")) ?? "members").Trim().ToLowerInvariant();
                var grant = new SynergyGrant
                {
                    Aff = aff,
                    To = to,
                    Strength = ParseFloat(g.Attribute("strength"), 1f),
                };

                if (to == "members") grant.Target = GrantTarget.Members;
                else if (to == "crew") grant.Target = GrantTarget.Crew;
                else if (to.StartsWith("member:"))
                {
                    if (int.TryParse(to.Substring("member:".Length), out int n) && n >= 1 && n <= entry.Members.Count)
                    {
                        grant.Target = GrantTarget.MemberIndex;
                        grant.MemberIndex = n - 1;
                    }
                    else
                    {
                        SynergyLog.Warn($"{sourceName} / {id}: to=\"{to}\" 越界（成员 {entry.Members.Count} 个），跳过该 Grant");
                        continue;
                    }
                }
                else
                {
                    SynergyLog.Warn($"{sourceName} / {id}: 未知的 to=\"{to}\"（可用 member:N / members / crew），跳过该 Grant");
                    continue;
                }

                entry.Grants.Add(grant);
            }

            if (entry.Grants.Count == 0)
            {
                SynergyLog.Warn($"{sourceName} / {id}: 没有任何有效 <Grant>，整条跳过");
                return null;
            }

            return entry;
        }

        static SynergyScope ParseScope(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "same_team": return SynergyScope.SameTeam;
                case "anywhere": return SynergyScope.Anywhere;
                case "": case "same_sub": return SynergyScope.SameSub;
                default:
                    SynergyLog.Warn($"未知的 scope=\"{raw}\"，按 same_sub 处理");
                    return SynergyScope.SameSub;
            }
        }

        static SynergyMode ParseMode(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "round_start": return SynergyMode.RoundStart;
                case "": case "while_present": return SynergyMode.WhilePresent;
                default:
                    SynergyLog.Warn($"未知的 mode=\"{raw}\"，按 while_present 处理");
                    return SynergyMode.WhilePresent;
            }
        }

        static int ParsePriority(XAttribute attr)
        {
            if (attr == null) return 0;
            string raw = attr.Value.Trim();
            if (raw.Length == 0) return 0;
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int v)) return v;
            SynergyLog.Warn($"未知的 priority=\"{raw}\"，按 0 处理");
            return 0;
        }

        static ExclusiveKind ParseExclusive(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "members": case "true": return ExclusiveKind.Members;
                case "all": return ExclusiveKind.All;
                case "": case "none": case "false": return ExclusiveKind.None;
                default:
                    SynergyLog.Warn($"未知的 exclusive=\"{raw}\"，按 none 处理");
                    return ExclusiveKind.None;
            }
        }

        static DetectScope ParseDetect(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "players": case "player": return DetectScope.Players;
                case "any": case "all": return DetectScope.Any;
                case "": case "crew": return DetectScope.Crew;
                default:
                    SynergyLog.Warn($"未知的 detect=\"{raw}\"，按 crew 处理");
                    return DetectScope.Crew;
            }
        }

        static bool ParseBool(XAttribute attr, bool fallback)
        {
            if (attr == null) return fallback;
            string v = attr.Value.Trim();
            if (v.Length == 0) return fallback;
            if (v == "1") return true;
            if (v == "0") return false;
            return bool.TryParse(v, out bool b) ? b : fallback;
        }

        static float ParseFloat(XAttribute attr, float fallback)
        {
            if (attr == null) return fallback;
            return float.TryParse(attr.Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }

        static bool IsTrue(XAttribute attr)
        {
            if (attr == null) return false;
            string v = attr.Value.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        static void EnsureOwnTemplate()
        {
            if (File.Exists(ConfigPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                    "<!-- 角色羁绊配置。一个 <Synergy> = 一组羁绊；其他模组可在自己的包里放同名文件扩展。\n" +
                    "     id          全局唯一；同 id 由加载顺序靠后的包整条覆盖，remove=\"true\" 可删除。\n" +
                    "     scope       same_sub(默认) / same_team / anywhere\n" +
                    "     alive_only  默认 true；false = 尸体也算在场\n" +
                    "     detect      crew(默认，玩家+玩家队 AI 船员) / players(只认玩家) / any(不限)\n" +
                    "     mode        while_present(默认) / round_start(开局 10 秒窗口内成立即锁定到巡回结束)\n" +
                    "     priority    整数，越大越优先（默认 0；同优先级按配置顺序，靠前的赢）\n" +
                    "     exclusive   none(默认，可共存) / members(与共用成员的羁绊互斥) / all(与任何羁绊互斥)\n" +
                    "     <Member>    aff=\"装束身份 affliction\" 或 item=\"装束物品 identifier\"（二选一，至少 2 个）\n" +
                    "     <Grant>     aff=\"要发的增益\" to=\"member:1|member:2|members|crew\" strength=\"1\"\n" +
                    "     装束身份 aff 全表见 Docs/角色羁绊系统-设计方案.md 附录 A -->\n" +
                    "<Synergies>\n" +
                    "  <!-- 示例（把成员 aff 换成真实角色、并先在内容模组里定义好增益 aff 再启用）：\n" +
                    "  <Synergy id=\"reimu_marisa\" scope=\"same_sub\">\n" +
                    "    <Member aff=\"Touhou_Reimu_Character_Effect\"/>\n" +
                    "    <Member aff=\"Touhou_Marisa_Character_Effect\"/>\n" +
                    "    <Grant aff=\"Touhou_Synergy_ReimuMarisa_Reimu\" to=\"member:1\"/>\n" +
                    "    <Grant aff=\"Touhou_Synergy_ReimuMarisa_Marisa\" to=\"member:2\"/>\n" +
                    "  </Synergy>\n" +
                    "  -->\n" +
                    "</Synergies>\n");
            }
            catch (Exception ex) { SynergyLog.Warn($"模板写入失败：{ex.Message}"); }
        }
    }

    // ==================== 判定与发放 ====================

    public static class SynergyEngine
    {
        public const double TickInterval = 0.5;     // 与装束身份 aff 的刷新节奏同拍
        public const double RoundStartWindow = 10.0; // round_start 模式的检测窗口（秒）
        const double MaxRefreshInterval = 2.0;      // 增益刷新间隔上限：被外部手段清掉后最多 2 秒补回

        // 成员标记总数 = aff 槽位数 + item 槽位数（仅供 SynergyConfig.Load 打日志）
        public static int WatchedCount => watchedAffs.Count + itemIndex.Count;

        // 在场索引按"槽位数组"对齐：watchedAffIndex[aff] / itemIndex[item] 给出槽位号，
        // presentByAff[slot] / presentByItem[slot] 即该标记的在场角色表——扫描时一次遍历
        // 角色自身 affliction 列表就能直接按槽位 Add，不必回字典查键。
        static readonly List<Identifier> watchedAffs = new List<Identifier>();
        static readonly Dictionary<Identifier, int> watchedAffIndex = new Dictionary<Identifier, int>();
        static List<Character>[] presentByAff = new List<Character>[0];
        static readonly Dictionary<string, int> itemIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static List<Character>[] presentByItem = new List<Character>[0];

        // 发放 prebuild：增益 aff 的 prefab 与"只删自己那条"的移除谓词各建一次，撤销时复用，
        // 避免每次断链 newobj 闭包 + 委托。
        static readonly Dictionary<string, AfflictionPrefab> grantPrefabs = new Dictionary<string, AfflictionPrefab>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Func<Affliction, bool>> removePredicates = new Dictionary<string, Func<Affliction, bool>>(StringComparer.OrdinalIgnoreCase);

        static readonly List<Character> candidates = new List<Character>(64);
        static readonly List<Character> picked = new List<Character>(4);
        static readonly List<Character> buffer = new List<Character>(16);

        // 角色 → { 羁绊 id → 下次刷新时刻 }（值以 Timing.TotalTime 为基准做节流，
        // 未到期一次 API 都不调；断链时按此只移除自己发的那份）
        static readonly Dictionary<Character, Dictionary<string, double>> granted = new Dictionary<Character, Dictionary<string, double>>();
        static readonly List<Character> pruneBuffer = new List<Character>(8);

        static double nextTick;
        static double roundStartTime = -1.0;

        // round_start 模式：开局窗口内成立 → 把成员与当拍目标快照一起记下来，之后只对这批目标续命。
        // 被互斥压制时不删记录：优先级让开后能自动恢复发放，不必重新满足开局窗口。
        sealed class LockRecord
        {
            public readonly List<Character> Members = new List<Character>();
            public readonly List<Character> Targets = new List<Character>();
        }
        static readonly Dictionary<string, LockRecord> lockedTargets = new Dictionary<string, LockRecord>(StringComparer.OrdinalIgnoreCase);

        // 本拍"成立"的条目：池化复用，避免每拍 new；Members 每拍 Clear 后重填。
        sealed class ActiveEntry
        {
            public SynergyEntry Entry;
            public int Order;                 // 配置顺序（越小越靠前），同优先级时用它打破平手
            public bool Locked;               // round_start：是否已锁定（"成立"不代表已锁定）
            public readonly List<Character> Members = new List<Character>(4);
        }
        static ActiveEntry[] activePool = new ActiveEntry[0];
        static int activeCount;

        // ② 选择的复用缓冲：索引数组 + 选中标记 + 压制者下标（-1 = 未被压制）
        static int[] orderCache = new int[0];
        static bool[] selectedCache = new bool[0];
        static int[] winnerCache = new int[0];
        // 静态比较委托：priority 降序、Order 升序。Comparer.Create 只在静态初始化时建一次，
        // 排序热路径不再 new 任何比较器/闭包。
        static readonly IComparer<int> orderComparer = Comparer<int>.Create(CompareActiveOrder);

        static readonly InvSlotType[] OutfitSlots = { InvSlotType.InnerClothes, InvSlotType.OuterClothes };

        // 判定耗时统计（Debug/诊断用）：synergy_list 打印摘要，synergy_reload 清零
        static readonly Stopwatch perfWatch = new Stopwatch();
        static long perfSamples;
        static double perfTotalUs;
        static double perfMaxUs;
        static int lastCandidates;
        static int lastFormed;

        /// <summary>判定耗时摘要（微秒）。Debug/诊断用。</summary>
        public static string PerfSummary
        {
            get
            {
                double avgUs = perfSamples > 0 ? perfTotalUs / perfSamples : 0.0;
                return $"判定 {perfSamples} 次｜平均 {avgUs:0.0} µs｜最大 {perfMaxUs:0.0} µs｜" +
                       $"上次候选 {lastCandidates} 人｜上次成立 {lastFormed} 条";
            }
        }

        /// <summary>清零判定耗时统计（配置重载时调用）。</summary>
        public static void ResetPerf()
        {
            perfSamples = 0;
            perfTotalUs = 0.0;
            perfMaxUs = 0.0;
            lastCandidates = 0;
            lastFormed = 0;
        }

        public static void RebuildWatched()
        {
            watchedAffs.Clear();
            watchedAffIndex.Clear();
            itemIndex.Clear();
            grantPrefabs.Clear();
            removePredicates.Clear();

            // 先把所有 member 标记收集去重、得到"标记 → 槽位号"映射，同时为 grant 预建 prefab / 移除谓词，
            // 最后按槽位数一次性建对齐的列表数组（槽位号在 presentByAff[slot] 里就是下标）。
            foreach (var entry in SynergyConfig.Entries)
            {
                foreach (var m in entry.Members)
                {
                    if (m.AffIdentifier != null)
                    {
                        if (!watchedAffIndex.ContainsKey(m.AffIdentifier))
                        {
                            watchedAffIndex[m.AffIdentifier] = watchedAffs.Count;
                            watchedAffs.Add(m.AffIdentifier);
                        }
                    }
                    else if (m.Item != null && !itemIndex.ContainsKey(m.Item))
                    {
                        itemIndex[m.Item] = itemIndex.Count;
                    }
                }

                foreach (var g in entry.Grants)
                {
                    if (grantPrefabs.ContainsKey(g.Aff)) continue;
                    if (!AfflictionPrefab.Prefabs.TryGet(g.Aff, out AfflictionPrefab prefab)) continue;
                    grantPrefabs[g.Aff] = prefab;

                    // 每个增益 aff 只建一个委托，撤销时复用；闭包只捕获 aff 字符串，不碰 entry/grant
                    string aff = g.Aff;
                    removePredicates[aff] = a => a != null && a.Identifier == aff;
                }
            }

            presentByAff = new List<Character>[watchedAffs.Count];
            for (int i = 0; i < presentByAff.Length; i++) presentByAff[i] = new List<Character>(4);

            presentByItem = new List<Character>[itemIndex.Count];
            for (int i = 0; i < presentByItem.Length; i++) presentByItem[i] = new List<Character>(4);
        }

        public static void OnRoundStart()
        {
            ClearRuntime();
            roundStartTime = Timing.TotalTime;
            nextTick = 0.0;
        }

        /// <summary>巡回结束：清运行时状态。round_start 的窗口计时只由 OnRoundStart / 这里管理。</summary>
        public static void OnRoundEnd()
        {
            ClearRuntime();
            roundStartTime = -1.0;
        }

        public static void ClearRuntime()
        {
            for (int i = 0; i < presentByAff.Length; i++) presentByAff[i].Clear();
            for (int i = 0; i < presentByItem.Length; i++) presentByItem[i].Clear();
            candidates.Clear();
            granted.Clear();
            lockedTargets.Clear();
            // 池对象留着复用，只把 Members 清空、计数归零
            for (int i = 0; i < activeCount; i++) activePool[i].Members.Clear();
            activeCount = 0;
        }

        public static void Tick()
        {
            if (SynergyConfig.Entries.Count == 0) return;
            if (Timing.TotalTime < nextTick) return;
            nextTick = Timing.TotalTime + TickInterval;

            perfWatch.Restart();
            try { Evaluate(); }
            catch (Exception ex) { SynergyLog.Warn($"判定异常：{ex.Message}"); }
            finally
            {
                // 采样放在 finally：Evaluate 抛异常也要 Stop 并计入，避免漏采
                perfWatch.Stop();
                double us = perfWatch.ElapsedTicks * 1000000.0 / Stopwatch.Frequency;
                perfSamples++;
                perfTotalUs += us;
                if (us > perfMaxUs) perfMaxUs = us;
            }
        }

        static void Evaluate()
        {
            // 1) 候选角色（每拍重建；只滤 Removed，死亡与否按条目的 alive_only 判）
            candidates.Clear();
            PruneGranted();

            foreach (var c in Character.CharacterList)
            {
                if (c == null || c.Removed) continue;
                candidates.Add(c);
            }
            lastCandidates = candidates.Count;
            if (candidates.Count == 0) { lastFormed = 0; activeCount = 0; return; }

            // 2) 在场索引：每角色只遍历一次自己的 affliction 列表
            for (int i = 0; i < presentByAff.Length; i++) presentByAff[i].Clear();
            for (int i = 0; i < presentByItem.Length; i++) presentByItem[i].Clear();

            // 本拍统一时间基准：节流记账与 round_start 窗口共用，避免逐目标重复读 Timing.TotalTime
            double now = Timing.TotalTime;

            foreach (var c in candidates)
            {
                var health = c.CharacterHealth;
                if (health != null && watchedAffIndex.Count > 0)
                {
                    // 直接遍历 afflictions 字典：Dictionary.Enumerator 是结构体，foreach 零分配。
                    // 不用 GetAllAfflictions()——它返回 IReadOnlyCollection 接口，而 .NET 8 里
                    // KeyCollection / KeyCollection.Enumerator 都是类，foreach 每次都得 new 一个枚举器
                    // （每个角色每拍一次堆分配）。字段在公开化程序集里是 public。
                    foreach (var kv in health.afflictions)
                    {
                        var a = kv.Key;
                        if (a == null || a.Strength <= 0f) continue;
                        if (watchedAffIndex.TryGetValue(a.Identifier, out int slot))
                            presentByAff[slot].Add(c);
                    }
                }

                if (presentByItem.Length > 0 && c.Inventory is CharacterInventory inv)
                {
                    foreach (var slot in OutfitSlots)
                    {
                        var item = inv.GetItemInLimbSlot(slot);
                        string id = item?.Prefab?.Identifier.Value;
                        if (id != null && itemIndex.TryGetValue(id, out int itemSlot) && !presentByItem[itemSlot].Contains(c))
                            presentByItem[itemSlot].Add(c);
                    }
                }
            }

            // 3) 三段式：① 收集成立条目 → ② 按优先级做互斥裁决 → ③ 发放/撤销
            double sinceRoundStart = roundStartTime >= 0.0 ? now - roundStartTime : double.MaxValue;

            CollectActive(sinceRoundStart);
            lastFormed = activeCount;   // "成立"数在裁决前统计，被压制的不算沉默，只是没发放
            SelectActive();
            ApplyActive(now);
        }

        // ---------- ① 收集成立条目 ----------

        static ActiveEntry AcquireActive()
        {
            if (activeCount >= activePool.Length)
            {
                int len = Math.Max(8, activePool.Length * 2);
                var bigger = new ActiveEntry[len];
                Array.Copy(activePool, bigger, activePool.Length);
                activePool = bigger;
            }
            var ae = activePool[activeCount];
            if (ae == null) { ae = new ActiveEntry(); activePool[activeCount] = ae; }
            return ae;
        }

        static ActiveEntry PushActive(SynergyEntry entry, bool locked)
        {
            var ae = AcquireActive();
            ae.Entry = entry;
            ae.Order = activeCount;   // 收集顺序 = 配置顺序，同优先级时靠前者先选
            ae.Locked = locked;
            ae.Members.Clear();
            activeCount++;
            return ae;
        }

        static void CollectActive(double sinceRoundStart)
        {
            activeCount = 0;
            foreach (var entry in SynergyConfig.Entries)
            {
                if (entry.Mode == SynergyMode.RoundStart)
                {
                    if (lockedTargets.TryGetValue(entry.Id, out var rec))
                    {
                        // 已锁定：不再重判条件，成员取锁定记录（被压制后优先级让开能自动恢复）
                        PushActive(entry, true).Members.AddRange(rec.Members);
                    }
                    else if (sinceRoundStart <= RoundStartWindow && PickMembers(entry, picked))
                    {
                        // 窗口内首次成立：先不锁定，等②选完再锁——被压制就不该占用锁定资格
                        PushActive(entry, false).Members.AddRange(picked);
                    }
                }
                else if (PickMembers(entry, picked))
                {
                    PushActive(entry, false).Members.AddRange(picked);
                }
            }
        }

        // ---------- ② 互斥裁决 ----------

        static int CompareActiveOrder(int x, int y)
        {
            var a = activePool[x];
            var b = activePool[y];
            int byPriority = b.Entry.Priority.CompareTo(a.Entry.Priority);  // 数字越大越靠前
            if (byPriority != 0) return byPriority;
            return a.Order.CompareTo(b.Order);                              // 同优先级按配置顺序，靠前者先选
        }

        static bool Conflicts(ActiveEntry a, ActiveEntry b)
        {
            var ka = a.Entry.Exclusive;
            var kb = b.Entry.Exclusive;
            if (ka == ExclusiveKind.All || kb == ExclusiveKind.All) return true;
            if (ka == ExclusiveKind.None && kb == ExclusiveKind.None) return false;
            // 至少一条是 members：只有共用成员角色才算冲突
            foreach (var m in a.Members)
                if (m != null && b.Members.Contains(m)) return true;
            return false;
        }

        static void SelectActive()
        {
            if (orderCache.Length < activeCount)
            {
                int len = Math.Max(activeCount, orderCache.Length * 2);
                orderCache = new int[len];
                selectedCache = new bool[len];
                winnerCache = new int[len];
            }

            for (int i = 0; i < activeCount; i++)
            {
                orderCache[i] = i;
                selectedCache[i] = false;
                winnerCache[i] = -1;
            }
            Array.Sort(orderCache, 0, activeCount, orderComparer);

            for (int i = 0; i < activeCount; i++)
            {
                int idx = orderCache[i];
                var ae = activePool[idx];
                for (int j = 0; j < i; j++)
                {
                    int other = orderCache[j];
                    if (!selectedCache[other]) continue;
                    if (!Conflicts(ae, activePool[other])) continue;

                    // 与已选中条目冲突 → 本拍不发放；记下压制者，供③撤销时打日志
                    winnerCache[idx] = other;
                    if (SynergyConfig.Debug)
                        SynergyLog.Debug($"羁绊「{ae.Entry.Id}」被「{activePool[other].Entry.Id}」压制（互斥 {ae.Entry.Exclusive} / {activePool[other].Entry.Exclusive}）");
                    break;
                }
                if (winnerCache[idx] < 0) selectedCache[idx] = true;
            }
        }

        // ---------- ③ 应用 ----------

        static void ApplyActive(double now)
        {
            for (int i = 0; i < activeCount; i++)
            {
                var ae = activePool[i];
                if (!selectedCache[i])
                {
                    int winner = winnerCache[i];
                    RevokeAll(ae.Entry, winner >= 0 ? activePool[winner].Entry.Id : null);
                    continue;
                }

                if (ae.Entry.Mode != SynergyMode.RoundStart) { GrantAll(ae.Entry, ae.Members, now); continue; }

                if (!ae.Locked) LockRoundStart(ae);
                if (lockedTargets.TryGetValue(ae.Entry.Id, out var rec)) RefreshLocked(rec, ae.Entry, now);
            }

            // 池外条目 = 本拍条件不成立（成员散开/死亡等）：同样要撤销已发过的增益。
            // 不能指望 duration 兜底——duration<=0 的常驻增益会永远留在身上。
            foreach (var entry in SynergyConfig.Entries)
            {
                if (IsCollected(entry)) continue;
                RevokeAll(entry, null);
            }
        }

        static bool IsCollected(SynergyEntry entry)
        {
            for (int i = 0; i < activeCount; i++)
                if (activePool[i].Entry == entry) return true;
            return false;
        }

        // 首次锁定：把成员与当拍目标快照写进记录；之后只对快照续命（脱装/换人不撤销）
        static void LockRoundStart(ActiveEntry ae)
        {
            var entry = ae.Entry;
            var rec = new LockRecord();
            rec.Members.AddRange(ae.Members);
            foreach (var g in entry.Grants)
            {
                // CollectCrew 会先 Clear 再填，所以先收进 buffer 再并入快照
                if (g.Target == GrantTarget.Crew)
                {
                    CollectCrew(entry, ae.Members[0], buffer);
                    rec.Targets.AddRange(buffer);
                }
                else if (g.Target == GrantTarget.MemberIndex) rec.Targets.Add(ae.Members[g.MemberIndex]);
                else rec.Targets.AddRange(ae.Members);
            }
            DedupeInPlace(rec.Targets);
            lockedTargets[entry.Id] = rec;
            ae.Locked = true;
            SynergyLog.Log($"羁绊「{entry.Id}」在开局窗口内成立，锁定到巡回结束（目标 {rec.Targets.Count} 个）");
        }

        static void RefreshLocked(LockRecord rec, SynergyEntry entry, double now)
        {
            buffer.Clear();
            foreach (var c in rec.Targets)
            {
                if (c == null || c.Removed) continue;
                if (entry.AliveOnly && c.IsDead) continue;
                buffer.Add(c);
            }
            rec.Targets.Clear();
            rec.Targets.AddRange(buffer);
            foreach (var c in rec.Targets) GrantAllGrants(entry, c, now);
        }

        // 原地去重（保留首次出现）：锁定只发生一次，用 IndexOf 与既有 Contains 同一套相等语义
        static void DedupeInPlace(List<Character> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var c = list[i];
                if (c == null) { list.RemoveAt(i); continue; }
                if (list.IndexOf(c) != i) list.RemoveAt(i);
            }
        }

        // ---------- 成员判定 ----------

        static bool PassesDetect(Character c, DetectScope scope)
        {
            switch (scope)
            {
                case DetectScope.Players: return c.IsPlayer;
                case DetectScope.Crew: return c.IsHuman && c.IsOnPlayerTeam;
                default: return true;
            }
        }

        static bool PickMembers(SynergyEntry entry, List<Character> result)
        {
            result.Clear();
            foreach (var m in entry.Members)
            {
                Character found = null;
                var list = MemberCandidates(m);
                if (list != null)
                {
                    foreach (var c in list)
                    {
                        if (result.Contains(c)) continue;                 // 同一角色不能占两席
                        if (!PassesDetect(c, entry.Detect)) continue;      // 检测范围（玩家 / 船员 / 不限）
                        if (entry.AliveOnly && c.IsDead) continue;
                        found = c;
                        break;
                    }
                }
                if (found == null) return false;
                result.Add(found);
            }

            switch (entry.Scope)
            {
                case SynergyScope.SameSub:
                {
                    var sub = result[0].Submarine;
                    for (int i = 1; i < result.Count; i++)
                        if (result[i].Submarine != sub) return false;
                    break;
                }
                case SynergyScope.SameTeam:
                {
                    var team = result[0].TeamID;
                    for (int i = 1; i < result.Count; i++)
                        if (result[i].TeamID != team) return false;
                    break;
                }
            }
            return true;
        }

        static List<Character> MemberCandidates(SynergyMember m)
        {
            if (m.AffIdentifier != null && watchedAffIndex.TryGetValue(m.AffIdentifier, out int affSlot)) return presentByAff[affSlot];
            if (m.Item != null && itemIndex.TryGetValue(m.Item, out int itemSlot)) return presentByItem[itemSlot];
            return null;
        }

        // ---------- 发放 / 撤销 ----------

        static void GrantAll(SynergyEntry entry, List<Character> members, double now)
        {
            foreach (var g in entry.Grants)
            {
                switch (g.Target)
                {
                    case GrantTarget.MemberIndex:
                        if (g.MemberIndex < members.Count) Apply(members[g.MemberIndex], entry, g, now);
                        break;
                    case GrantTarget.Crew:
                        CollectCrew(entry, members[0], buffer);
                        foreach (var c in buffer) Apply(c, entry, g, now);
                        break;
                    default:
                        foreach (var c in members) Apply(c, entry, g, now);
                        break;
                }
            }
        }

        static void GrantAllGrants(SynergyEntry entry, Character c, double now)
        {
            foreach (var g in entry.Grants) Apply(c, entry, g, now);
        }

        static void CollectCrew(SynergyEntry entry, Character anchor, List<Character> result)
        {
            result.Clear();
            foreach (var c in candidates)
            {
                if (c == anchor) { result.Add(c); continue; }
                if (entry.AliveOnly && c.IsDead) continue;
                if (!c.IsOnPlayerTeam) continue;
                switch (entry.Scope)
                {
                    case SynergyScope.SameSub:
                        if (c.Submarine != anchor.Submarine) continue;
                        break;
                    case SynergyScope.SameTeam:
                        if (c.TeamID != anchor.TeamID) continue;
                        break;
                }
                result.Add(c);
            }
        }

        static void Apply(Character target, SynergyEntry entry, SynergyGrant g, double now)
        {
            if (target == null || target.Removed) return;

            // 已发过且未到刷新时刻 → 直接返回，一次 API 都不调（断链时 RevokeAll 会删记账键，重新缔结立即补发）
            if (granted.TryGetValue(target, out var ids) &&
                ids.TryGetValue(entry.Id, out double nextRefresh) && now < nextRefresh) return;

            if (!grantPrefabs.TryGetValue(g.Aff, out AfflictionPrefab prefab)) return;

            var limb = target.AnimController?.MainLimb;
            if (limb == null) return;

            float strength = g.Strength;
            if (prefab.MaxStrength > 0f) strength = Math.Min(strength, prefab.MaxStrength);
            if (strength <= 0f) return;

            try
            {
                target.CharacterHealth.ApplyAffliction(limb, new Affliction(prefab, strength), true, false, true);
                // ApplyAffliction 会按 100/MaxVitality 缩放，施加后写回目标强度（与美铃/莲子脚本同一套做法）
                var applied = target.CharacterHealth.GetAffliction(prefab.Identifier);
                applied?.SetStrength(strength);

                if (ids == null)
                {
                    ids = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    granted[target] = ids;
                }
                ids[entry.Id] = now + RefreshInterval(prefab);
            }
            catch (Exception ex)
            {
                SynergyLog.Warn($"施加 {g.Aff} 到 {target.Name} 失败：{ex.Message}");
            }
        }

        // suppressedBy != null 表示本次撤销是互斥裁决压下来的（而非条件不成立）。
        // 日志放在 early return 之后：只有确实发过、这次真的撤销了才打，不会每拍刷屏。
        static void RevokeAll(SynergyEntry entry, string suppressedBy = null)
        {
            buffer.Clear();
            foreach (var kv in granted)
                if (kv.Value.ContainsKey(entry.Id)) buffer.Add(kv.Key);
            if (buffer.Count == 0) return;

            if (suppressedBy != null)
                SynergyLog.Log($"羁绊「{entry.Id}」被「{suppressedBy}」压制，撤销增益");

            foreach (var c in buffer)
            {
                if (c == null || c.Removed) continue;
                foreach (var g in entry.Grants) RemoveGrant(c, g.Aff);
                // 必须把记账键删掉：否则重新缔结时会被节流当成"未到刷新时刻"挡下，不再施加
                if (granted.TryGetValue(c, out var ids)) ids.Remove(entry.Id);
                if (SynergyConfig.Debug) SynergyLog.Debug($"羁绊「{entry.Id}」断开，移除 {c.Name} 身上的增益");
            }
        }

        static void RemoveGrant(Character c, string aff)
        {
            var health = c?.CharacterHealth;
            if (health == null) return;
            // 谓词在 RebuildWatched 里按增益 aff 预建好，这里直接复用（不再每次断链 newobj 闭包）
            if (!removePredicates.TryGetValue(aff, out var predicate)) return;
            try
            {
                // 只移除本引擎发的这条 aff；同名 aff 若来自其他系统则不受影响（名字约定为羁绊专用）
                health.RemoveAfflictions(predicate);
            }
            catch (Exception ex) { SynergyLog.Warn($"移除 {aff} 失败：{ex.Message}"); }
        }

        // 增益的刷新间隔：取 duration 的一半，钳在 [TickInterval, 2.0] 秒。
        // duration<=0（常驻增益）用 2 秒做低频校验；2 秒上限保证增益被外部手段清掉后最多 2 秒补回。
        static double RefreshInterval(AfflictionPrefab prefab)
        {
            double d = prefab.Duration;
            if (d <= 0.01) return MaxRefreshInterval;
            return Math.Min(Math.Max(d * 0.5, TickInterval), MaxRefreshInterval);
        }

        static void PruneGranted()
        {
            if (granted.Count == 0) return;
            pruneBuffer.Clear();
            foreach (var kv in granted)
                if (kv.Key == null || kv.Key.Removed) pruneBuffer.Add(kv.Key);
            foreach (var c in pruneBuffer) granted.Remove(c);
        }
    }

    // Debug 输出受 <Synergies debug="true"> 控制
    public static class SynergyLog
    {
        public static void Log(string msg) => LuaCsLogger.LogMessage($"[羁绊] {msg}", Color.LightGreen);
        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[羁绊] {msg}", Color.Orange);
        public static void Debug(string msg)
        {
            if (SynergyConfig.Debug) LuaCsLogger.LogMessage($"[羁绊][dbg] {msg}", Color.LightGray);
        }
    }
}
