using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using FarseerPhysics;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.ChargedTurret
{
    /// <summary>
    /// 蓄能符卡发射器（连射炮弹药 · 蓄力齐射）插件。
    ///
    /// 不新增 ItemComponent：组件类型的发现走 ReflectionUtils.GetDerivedNonAbstract&lt;ItemComponent&gt;()，
    /// 结果进 TypeSearchCache 缓存、只扫已注册程序集，插件程序集的注册时机不可控（找不到就是一条红字 +
    /// 物品静默缺组件）。因此沿用本生态的稳定做法——炮塔 XML 仍是原版 &lt;Turret&gt;，齐射参数写在它下面的
    /// 自定义子元素 &lt;ChargedSpellCard .../&gt;（原版 Turret 构造函数只认自己认识的名字，未知子元素直接忽略），
    /// 由本插件运行时从 item.Prefab.ConfigElement 里读出来。
    ///
    /// 行为改造的核心是两个补丁，其余全部吃原版：
    ///   · Turret.TryLaunch 前缀：发射前快照弹药盒 Condition（用于推算"本次发射实际扣了多少耐久"）；
    ///     并负责连打/装弹节奏——装弹窗口内直接拦掉这次发射，连打第 2..N 轮时补满蓄力（跳过重新蓄力）
    ///   · Turret.Launch 后置：原生那一发之后再补射 burstcount-1 发同款弹（炮口扇形铺开）、
    ///     把这一轮齐射的弹药耐久按 ammomultiplier 补扣到位，并按 burstspercharge 数轮数、打满后进装弹
    ///   · Turret.Launch 前缀还会把炮口/基线抄下来（后置里读 __0 的位置不可靠，见下面的说明），
    ///     并标记"本炮正在发射"的窗口（供下面几条判定 O(1) 认门）
    ///
    /// 另外有一条针对引擎 bug 的处置（不是玩法改动，是修画面/修手感）：
    ///   · Projectile.DoHitscan 前缀 + Projectile.HandleProjectileCollision 前缀：
    ///     引擎的 RayCastInOtherSubs 会把射线再投进 Submarine.Loaded 里每艘潜艇的坐标系、命中点按
    ///     +该艇位置 折算回来；对停在场外/隐藏位的潜艇，折出来的"命中点"会离开炮口几千像素
    ///     （实测：射线 +15.3°，光束却被画成 -146°）。处置办法是记下每发的射线，丢掉"不在这条射线上"的
    ///     命中——既不让玩家看到反向光束，也不会让这一发被假命中吃掉。详见 ChargedSpellCardBogusHitPatch。
    ///   · Projectile.LaunchProjSpecific 前缀：兜底把"指向弹体背面"的光束按朝向接回去（正常不该触发，
    ///     触发时会打橙色告警）。
    ///
    /// 节奏用 singlechargedshot="true"：引擎开火后会把蓄力钳下去（WindingDown→Inactive），
    /// 蓄力音因此会在开火瞬间停、装弹期间保持静音，下一组开始蓄力时再响，不需要我们碰音效。
    /// 轮数限制与装弹窗口靠反射读写 Turret.currentChargeTime 实现（原版没有公开接口），拿不到就自动退化。
    /// 蓄力时长/音效/粒子/HUD 仍由原版驱动（maxchargetime + ChargeSound/ChargeSprite/showchargeindicator），
    /// 联机同步（Turret 是 IServerSerializable）、AI 蓄力容差/优先级都不重写。
    /// 插件缺失时炮塔退化为"原生单发蓄力炮"，不会加载失败。
    /// 参考写法：本仓库 HomingProjectiles.cs 的 Turret.Launch postfix（同一挂点）；
    /// 射弹生成照参考模组 3792205908 的 VoidRanged.cs（AddItemToSpawnQueue + Projectile.Shoot）。
    /// 排查日志开关见 ChargedLog.Verbose（默认关，热路径上的日志调用点都用 if 包住）。
    /// </summary>
    public sealed class ChargedSpellCardPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.chargedspellcard");
        }

        public void PreInitPatching() { }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone)
            {
                ChargedLog.Log("OnLoadCompleted: 内容重载，补丁已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ChargedSpellCardAmmoPatch));
            harmony.PatchAll(typeof(ChargedSpellCardLaunchPatch));

            // 探针 + 反向光束处置补丁。
            // 假命中处置属于功能补丁（反向光束/被吃弹的修复）：挂不上就说明引擎改版，必须显式报出来；
            // 探针里只有射线记录是功能性的（假命中判定要用），日志部分在 Verbose 关掉后不产生任何输出。
            try
            {
                harmony.PatchAll(typeof(ChargedSpellCardBogusHitPatch));
                harmony.PatchAll(typeof(ChargedSpellCardSpawnFlushPatch));   // 客户端补射显形（生成消息一到就重演）
                harmony.PatchAll(typeof(ChargedSpellCardHitscanHoldPatch));  // hitscan 弹延后移除（给客户端留重演窗口）
            }
            catch (Exception e)
            {
                ChargedLog.Warn("功能补丁未挂上（反向光束/多人补射同步可能复发）: " + e.Message);
            }
            try
            {
                harmony.PatchAll(typeof(ChargedSpellCardHitscanProbePatch));
                harmony.PatchAll(typeof(ChargedSpellCardTracerProbePatch));
            }
            catch (Exception e)
            {
                ChargedLog.Warn("探针补丁未挂上（不影响炮塔功能）: " + e.Message);
            }

            // 多人：注册"补射发射"同步消息（不处理的一侧也要占位注册，LuaCs 靠它交换 netId）
            ChargedSpellCardNetSync.EnsureRegistered();

            ChargedLog.Log("蓄能符卡发射器插件已加载");
        }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            ChargedSpellCardData.Reset();
            oneTimeInitDone = false;
        }
    }

    internal static class ChargedLog
    {
        /// <summary>低频信息日志开关（齐射详情、探针等）。默认关；排查反向弹/齐射节奏时改成 true，
        /// 并且把调用点用 if (ChargedLog.Verbose) 包住（拼接字符串本身也有开销）。</summary>
        public static bool Verbose = false;

        public static void Log(string msg)
        {
            if (Verbose) LuaCsLogger.LogMessage($"[蓄能炮塔] {msg}", Color.LightGreen);
        }

        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[蓄能炮塔] {msg}", Color.Orange);
    }

    /// <summary>单个炮塔 prefab 的齐射参数（来自 &lt;Turret&gt; 下的 &lt;ChargedSpellCard&gt; 子元素）。
    /// 各属性的含义、取值范围与调参建议，见东方潜渊行动组Lua补丁/Items/Item.xml 中文注释（散射符卡发射器一节）。</summary>
    internal sealed class ChargedSpellCardParams
    {
        /// <summary>一次齐射的总弹数（1 = 关掉齐射，退化成原生单发蓄力炮）。</summary>
        public int BurstCount = 1;

        /// <summary>补射在基线上均匀铺开的总张角（度）。</summary>
        public float FanAngleDeg = 12f;

        /// <summary>每发弹的弹药耐久倍数（2 = 双倍消耗）。</summary>
        public float AmmoMultiplier = 2f;

        /// <summary>&gt; 0 时按这个绝对值当"每发弹基准耐久"，不再按本次观测差值推算。</summary>
        public float AmmoCost;

        /// <summary>一次蓄力最多连打几轮齐射（0 = 不限制，纯按引擎的 singlechargedshot 走）。
        /// 第 2..N 轮由插件补满蓄力跳过重新蓄力；打满 N 轮后进入装弹窗口。
        /// 配合炮塔 singlechargedshot="true" 使用（false 时蓄力一直满格、蓄力音会一直响）。</summary>
        public int BurstsPerCharge;

        /// <summary>装弹时间（秒）：连打打满后额外等待多久才允许开始下一组蓄力。
        /// 0 = 不等待。实际两组之间的间隔 ≈ 装弹时间 + maxchargetime（装弹期间蓄力是停的、音效静音）。</summary>
        public float MagazineReload;
    }

    /// <summary>单个炮塔实例的运行时状态。</summary>
    internal sealed class ChargedSpellCardState
    {
        public ChargedSpellCardParams Params;      // null = 不是蓄能炮塔（负缓存，避免每帧重解析 XML）
        public float SnapshotCondition;            // TryLaunch 前缀快照
        public bool SnapshotValid;
        public double LastBurstTime = double.MinValue;   // 同一帧只齐射一次（Launch 是每发弹调一次）
        public bool MissingAmmoLogged;
        public int BurstsSinceCharge;              // 本段蓄力已经连打了几轮
        public double ReloadUntil;                 // 装弹窗口结束时刻（Timing.TotalTime 基准）
    }

    internal static class ChargedSpellCardData
    {
        private static readonly ConditionalWeakTable<Turret, ChargedSpellCardState> cache = new();

        // 弹药盒 prefab → 它 OnUse 里的射弹候选（含 commonness 权重）。只解析一次；
        // prefab 是常驻对象、条目很少，所以用普通字典即可
        private static readonly Dictionary<ItemPrefab, List<(ItemPrefab Prefab, float Commonness)>> boltCandidateCache = new();

        /// <summary>只查不建：给"不该顺手触发解析"的地方用（例如每次发射前记录炮口）。</summary>
        public static ChargedSpellCardState TryGet(Turret turret)
        {
            return cache.TryGetValue(turret, out ChargedSpellCardState state) ? state : null;
        }

        public static ChargedSpellCardState GetOrCreate(Turret turret)
        {
            if (cache.TryGetValue(turret, out ChargedSpellCardState state)) { return state; }
            state = new ChargedSpellCardState { Params = ParseParams(turret?.Item) };
            cache.Add(turret, state);
            if (state.Params != null)
            {
                ChargedLog.Log($"{turret.Item.Prefab.Identifier}: 齐射 {state.Params.BurstCount} 发 / 扇形 {state.Params.FanAngleDeg:0.#}° / 弹药 ×{state.Params.AmmoMultiplier:0.##}"
                    + (state.Params.AmmoCost > 0f ? $"（每发基准耐久 {state.Params.AmmoCost:0.###}）" : "（每发基准耐久按实扣推算）"));
                if (state.Params.BurstsPerCharge > 0 && !turret.SingleChargedShot)
                {
                    ChargedLog.Warn($"{turret.Item.Prefab.Identifier}: 建议把 singlechargedshot 改成 true"
                        + "（false 时蓄力会一直停在满格：蓄力音会一直响、开火后也不会静音）");
                }
            }
            return state;
        }

        public static void Reset()
        {
            cache.Clear();
            boltCandidateCache.Clear();
        }

        /// <summary>炮塔 linkedTo 里的弹药源（turretammosource）里装着的弹药盒。</summary>
        public static Item FindAmmoBox(Item turretItem)
        {
            List<MapEntity> linked = turretItem?.linkedTo;
            if (linked == null) { return null; }
            foreach (MapEntity entity in linked)
            {
                if (entity is not Item loader || loader.Removed) { continue; }
                if (!loader.HasTag(Tags.TurretAmmoSource)) { continue; }
                foreach (Item contained in loader.ContainedItems)
                {
                    if (contained != null && !contained.Removed) { return contained; }
                }
            }
            return null;
        }

        /// <summary>
        /// 弹药盒自己 &lt;StatusEffect type="OnUse"&gt; 里的 SpawnItem 候选（含 commonness 权重）。
        /// 例如「天上剑『天人之五衰』」会随机生成 4 种弹 + 剑气，靠它让补射也"每发各摇一次"，
        /// 而不是把原生那一发的弹种简单复制 4 份。返回 null = 这种弹药没有候选信息（补射沿用原生弹种）。
        /// </summary>
        public static List<(ItemPrefab Prefab, float Commonness)> GetBoltCandidates(Item box)
        {
            if (box?.Prefab == null) { return null; }
            if (boltCandidateCache.TryGetValue(box.Prefab, out var cached)) { return cached; }
            List<(ItemPrefab, float)> parsed = ParseBoltCandidates(box.Prefab);
            boltCandidateCache[box.Prefab] = parsed;
            return parsed;
        }

        /// <summary>按 commonness 加权随机挑一发弹；没有候选就回落到 fallback（原生那一发的弹种）。</summary>
        public static ItemPrefab PickBoltPrefab(List<(ItemPrefab Prefab, float Commonness)> candidates, ItemPrefab fallback)
        {
            if (candidates == null || candidates.Count == 0) { return fallback; }
            if (candidates.Count == 1) { return candidates[0].Prefab ?? fallback; }
            float total = 0f;
            foreach (var candidate in candidates) { total += Math.Max(0f, candidate.Commonness); }
            if (total <= 0f) { return fallback; }
            float roll = Rand.Range(0f, total);
            foreach (var candidate in candidates)
            {
                roll -= Math.Max(0f, candidate.Commonness);
                if (roll <= 0f) { return candidate.Prefab ?? fallback; }
            }
            return candidates[candidates.Count - 1].Prefab ?? fallback;
        }

        static List<(ItemPrefab, float)> ParseBoltCandidates(ItemPrefab boxPrefab)
        {
            try
            {
                if (boxPrefab.ConfigElement is not ContentXElement root) { return null; }
                List<(ItemPrefab, float)> result = null;
                foreach (var element in root.Elements())
                {
                    if (element is not ContentXElement container ||
                        !string.Equals(container.Name.LocalName, "ItemContainer", StringComparison.OrdinalIgnoreCase)) { continue; }
                    foreach (var effectElement in container.Elements())
                    {
                        if (effectElement is not ContentXElement effect ||
                            !string.Equals(effect.Name.LocalName, "StatusEffect", StringComparison.OrdinalIgnoreCase) ||
                            !effect.GetAttributeString("type", "").Equals("OnUse", StringComparison.OrdinalIgnoreCase)) { continue; }
                        foreach (var spawnElement in effect.Elements())
                        {
                            if (spawnElement is not ContentXElement spawn ||
                                !string.Equals(spawnElement.Name.LocalName, "SpawnItem", StringComparison.OrdinalIgnoreCase)) { continue; }
                            float commonness = Math.Max(0f, spawn.GetAttributeFloat("commonness", 1f));
                            string idList = spawn.GetAttributeString("identifiers", spawn.GetAttributeString("identifier", ""));
                            foreach (string raw in idList.Split(','))
                            {
                                string idText = raw.Trim();
                                if (idText.Length == 0) { continue; }
                                if (MapEntityPrefab.FindByIdentifier(idText) is not ItemPrefab boltPrefab) { continue; }
                                (result ??= new List<(ItemPrefab, float)>()).Add((boltPrefab, commonness));
                            }
                        }
                    }
                }
                return result;
            }
            catch (Exception e)
            {
                ChargedLog.Warn("解析弹药盒的 SpawnItem 候选出错: " + e.Message);
                return null;
            }
        }

        private static ChargedSpellCardParams ParseParams(Item item)
        {
            try
            {
                if (item?.Prefab?.ConfigElement is not ContentXElement root) { return null; }

                ContentXElement turretElement = null;
                foreach (var element in root.Elements())
                {
                    if (element is ContentXElement candidate &&
                        string.Equals(candidate.Name.LocalName, "Turret", StringComparison.OrdinalIgnoreCase))
                    {
                        turretElement = candidate;
                        break;
                    }
                }
                if (turretElement == null) { return null; }

                var sub = turretElement.GetChildElement("ChargedSpellCard");
                if (sub == null) { return null; }

                return new ChargedSpellCardParams
                {
                    BurstCount = Math.Max(1, sub.GetAttributeInt("burstcount", 1)),
                    FanAngleDeg = sub.GetAttributeFloat("fanangle", 12f),
                    AmmoMultiplier = Math.Max(1f, sub.GetAttributeFloat("ammomultiplier", 2f)),
                    AmmoCost = Math.Max(0f, sub.GetAttributeFloat("ammocost", 0f)),
                    BurstsPerCharge = Math.Max(0, sub.GetAttributeInt("burstspercharge", 0)),
                    MagazineReload = Math.Max(0f, sub.GetAttributeFloat("magazinereload", 0f)),
                };
            }
            catch (Exception e)
            {
                ChargedLog.Warn("解析 <ChargedSpellCard> 出错: " + e.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// 读写炮塔蓄力（用于"一次蓄力连打 N 轮"和"装弹窗口"）。蓄力值在引擎里是 private 字段
    /// （Turret.currentChargeTime），原版没有公开接口，只能反射；拿不到就退化成"不做节奏控制"并只警告一次
    /// （反射既有先例：本仓库 HomingProjectiles.cs 反射 Turret.GetFriendlyTeam）。
    /// </summary>
    internal static class ChargedSpellCardChargeControl
    {
        static readonly FieldInfo chargeField =
            typeof(Turret).GetField("currentChargeTime", BindingFlags.NonPublic | BindingFlags.Instance);
        static bool warned;

        /// <summary>补满蓄力：让引擎立刻允许开火（跳过"重新蓄力"），用于连打的第 2..N 轮。</summary>
        public static void Fill(Turret turret) => Set(turret, turret.MaxChargeTime);

        /// <summary>清空蓄力：引擎随后会进入 Inactive/WindingDown，蓄力音自动停。</summary>
        public static void Clear(Turret turret) => Set(turret, 0f);

        static void Set(Turret turret, float value)
        {
            if (chargeField == null)
            {
                if (!warned)
                {
                    warned = true;
                    ChargedLog.Warn("找不到 Turret.currentChargeTime（引擎改版？）：burstspercharge 的连打/装弹节奏失效");
                }
                return;
            }
            try
            {
                chargeField.SetValue(turret, value);
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    ChargedLog.Warn("改写蓄力失败: " + e.Message);
                }
            }
        }
    }

    /// <summary>
    /// Turret.TryLaunch 前缀，管两件事：
    ///   1) 发射前快照弹药盒耐久（原生扣耐久发生在 TryLaunch 内部，Launch 后置里才能算出扣了多少）
    ///   2) 连打/装弹节奏：装弹窗口内直接返回 false 拦掉这次发射（引擎收不到"按住扳机"，蓄力保持为 0、静音）；
    ///      连打的第 2..N 轮把蓄力补满，跳过重新蓄力、只等 reload。
    /// </summary>
    [HarmonyPatch(typeof(Turret), "TryLaunch")]
    internal static class ChargedSpellCardAmmoPatch
    {
        static bool Prefix(Turret __instance)
        {
            // 客户端也会跑到这里（引擎的 TryLaunch 在客户端只是立刻返回），借这个每帧都会过一下的点
            // 兜住"插件加载时 LuaCs 网络服务还没就绪"的情况：同步消息必须在本端注册过才收得到。
            ChargedSpellCardNetSync.EnsureRegistered();
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) { return true; }
            if (__instance?.Item == null) { return true; }

            ChargedSpellCardState state = ChargedSpellCardData.GetOrCreate(__instance);
            ChargedSpellCardParams p = state.Params;
            if (p == null) { return true; }

            state.SnapshotValid = false;
            Item box = ChargedSpellCardData.FindAmmoBox(__instance.Item);
            if (box != null)
            {
                state.SnapshotCondition = box.Condition;
                state.SnapshotValid = true;
            }

            if (p.BurstsPerCharge <= 0) { return true; }

            double now = Timing.TotalTime;
            if (state.ReloadUntil > now)
            {
                state.BurstsSinceCharge = 0;
                return false;   // 装弹中：整段跳过 TryLaunch（蓄力不积累，蓄力音保持静音）
            }

            // 松手判定：距上一次齐射超过 reload + 0.3 秒，就认为射手已经松开扳机收手了——
            // 清掉连打计数，于是下一发不会被"补满蓄力"直接放行，必须老老实实重新蓄满。
            // 组内两轮之间的间隔就是 reload，所以这个阈值不会误伤连打。
            if (state.BurstsSinceCharge > 0 &&
                now - state.LastBurstTime > __instance.Reload + 0.3)
            {
                state.BurstsSinceCharge = 0;
            }

            if (state.BurstsSinceCharge > 0 && state.BurstsSinceCharge < p.BurstsPerCharge)
            {
                ChargedSpellCardChargeControl.Fill(__instance);
            }
            return true;
        }
    }

    /// <summary>
    /// 原生那一发打出去之后的补射 + 弹药耐久补扣。
    /// Launch 是每发弹调一次（Turret.Launch(Item projectile, Character user, float? launchRotation, float tinkeringStrength)），
    /// 参数按位置绑定 __0 = 射弹 Item、__1 = 射手（AI 炮塔可能为 null）。
    /// </summary>
    [HarmonyPatch(typeof(Turret), "Launch")]
    internal static class ChargedSpellCardLaunchPatch
    {
        /// <summary>每次发射的"炮口位置 / 基准朝向"临时记录（键=炮塔，后置用完即取）。</summary>
        static readonly Dictionary<Turret, (Vector2 Muzzle, float Rotation)> capturedLaunches = new();

        /// <summary>正在发射的炮塔物品（只在 Turret.Launch 调用期间有效，终结器里清掉）。
        /// 探针/假命中处置靠它一眼认出"这是我们这门炮打的弹"，不必给全场的 hitscan 武器都做一次
        /// GetComponent&lt;Turret&gt; + 查表——连射炮这种武器一秒能打十几发，省下来的是实打实的。</summary>
        internal static Item ActiveLauncher;

        /// <summary>
        /// 在引擎动这发弹之前，先把炮口位置和基准朝向抄下来。
        /// 后置里再读 __0 的位置/朝向是不可靠的：hitscan 弹会在 Turret.Launch 内部一路 DoHitscan
        /// 把弹 SetTransform 到命中点（findNewHull 还可能顺带把它翻过来），偶尔就读到反向的朝向——
        /// 这正是"偶发反向射弹"的来源（实测也只有 hitscan 弹药会中，物理弹从没出现）。
        /// 炮口用引擎那套算法算（GetRelativeFiringPosition + 忽略自家潜艇的遮挡收缩）；
        /// 基准朝向就是引擎给原生那发用的 0f - Turret.Rotation（本炮 spread=0，没有额外随机量）。
        /// 另外顺手把引擎那一发（hitscan 的）登记进"延后移除"名单——它的移除请求发生在
        /// 引擎 Launch 内部，只有在前缀里登记才拦得住（后置里再登记就晚了）。
        /// </summary>
        static void Prefix(Turret __instance, Item __0)
        {
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) { return; }
            if (__instance?.Item == null) { return; }
            if (ChargedSpellCardData.TryGet(__instance)?.Params == null) { return; }
            capturedLaunches[__instance] = (GetMuzzleSimPosition(__instance), 0f - __instance.Rotation);
            if (ActiveLauncher == null) { ActiveLauncher = __instance.Item; }   // 不覆盖外层发射（嵌套时各管各的）
            if (__0?.GetComponent<Projectile>()?.Hitscan == true) { ChargedSpellCardHitscanHoldPatch.Hold(__0); }
        }

        /// <summary>无论正常结束还是抛异常都要清掉发射标记，另外顺手清掉可能残留的炮口记录。</summary>
        static void Finalizer(Turret __instance, Exception __exception)
        {
            if (__instance?.Item == null) { return; }
            if (ActiveLauncher == __instance.Item) { ActiveLauncher = null; }
            if (__exception != null) { capturedLaunches.Remove(__instance); }
        }

        static void Postfix(Turret __instance, Item __0, Character __1, float __3)
        {
            // 服务端权威：客户端不生成实体（原生发射本身也只在权威端发生，这里再兜一道）
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) { return; }
            if (__instance?.Item == null || __0 == null || __0.Removed || __0.body == null) { return; }

            // 排查用：把"引擎原生那一发"落地的状态也记下来（补射那四发在下面单独记）。
            // 反向弹若出自原生那一发，这里能看到它的朝向/Dir/坐标口径是否正常。
            if (ChargedLog.Verbose)
            {
                Projectile engineBolt = __0.GetComponent<Projectile>();
                ChargedLog.Log($"[探针·原生发] {__0.Prefab.Identifier}: 位置 {__0.SimPosition.X:0.#},{__0.SimPosition.Y:0.#}，"
                    + $"朝向 {MathHelper.ToDegrees(__0.body.Rotation):0.#}°，Dir {__0.body.Dir:0}，sub {(__0.Submarine != null ? "有" : "无")}，"
                    + $"hitscan {(engineBolt != null && engineBolt.Hitscan ? "是" : "否")}，位置坐标口径 {(__0.Submarine != null ? "局部" : "世界")}");
            }

            ChargedSpellCardState state = ChargedSpellCardData.GetOrCreate(__instance);
            ChargedSpellCardParams p = state.Params;
            if (p == null) { return; }

            // 巡回过渡/加载窗口不生成实体：此刻 AddItemToSpawnQueue 出来的射弹可能成为
            // 双方状态不一致的孤儿实体（客户端 MISSING_ENTITY 上报），参考模组 3792205908 的同类守卫
            if (GameMain.NetworkMember != null && Level.Loaded == null) { return; }

            double now = Timing.TotalTime;
            double gap = now - state.LastBurstTime;   // 距上一轮齐射的间隔（0 = 同一帧）
            if (gap == 0) { return; }                 // 同一帧只齐射一次（Launch 是每发弹调一次）
            state.LastBurstTime = now;

            // ---- 连打轮数 / 装弹：一次蓄力最多连打 burstspercharge 轮，打满后进入装弹窗口 ----
            // （与 burstcount 无关，burstcount=1 的单发炮塔也照样生效；
            //   松手重新计数的判定放在 TryLaunch 前缀里，那边才决定要不要补满蓄力）
            if (p.BurstsPerCharge > 0)
            {
                state.BurstsSinceCharge++;
                if (state.BurstsSinceCharge >= p.BurstsPerCharge)
                {
                    state.BurstsSinceCharge = 0;
                    state.ReloadUntil = now + p.MagazineReload;        // 装弹窗口：这期间 TryLaunch 被整个拦掉
                    ChargedSpellCardChargeControl.Clear(__instance);   // 清空蓄力 → 蓄力音立刻停，装弹期静音
                    if (ChargedLog.Verbose) { ChargedLog.Log($"{__instance.Item.Prefab.Identifier}: 连打 {p.BurstsPerCharge} 轮完成，装弹 {p.MagazineReload:0.##} 秒"); }
                }
            }

            if (p.BurstCount <= 1) { return; }

            ItemPrefab fallbackPrefab = __0.Prefab;
            if (fallbackPrefab == null || Entity.Spawner == null) { return; }

            // ---- 弹药耐久：口径 = 每发弹 × ammomultiplier ----
            Item box = ChargedSpellCardData.FindAmmoBox(__instance.Item);
            float baseCost = p.AmmoCost;
            if (baseCost <= 0f && state.SnapshotValid && box != null)
            {
                baseCost = Math.Max(0f, state.SnapshotCondition - box.Condition);
            }

            int extra = p.BurstCount - 1;
            if (box != null && baseCost > 0f)
            {
                float perBolt = baseCost * p.AmmoMultiplier;             // 每发弹应扣的耐久
                float owedForFirst = Math.Max(0f, perBolt - baseCost);   // 原生那发还欠的份额
                float available = box.Condition;
                int affordable = (int)Math.Floor((available - owedForFirst) / perBolt + 0.0001f);
                if (affordable < extra) { extra = Math.Max(0, affordable); }
                float charge = Math.Min(available, owedForFirst + extra * perBolt);
                if (charge > 0f) { box.Condition = Math.Max(0f, available - charge); }
            }
            else if (box == null && !state.MissingAmmoLogged)
            {
                state.MissingAmmoLogged = true;
                ChargedLog.Warn($"{__instance.Item.Prefab.Identifier}: 找不到弹药盒，齐射不额外扣耐久");
            }

            if (extra <= 0) { return; }

            // ---- 补射：炮口扇形铺开 ----
            // 弹种逐发随机：优先按弹药盒自己的候选表（commonness 加权，例如 Celestial_Decay 的 4 种弹 + 剑气），
            // 弹药盒没有候选信息（只写死一种弹）时沿用原生那一发的弹种
            var boltCandidates = ChargedSpellCardData.GetBoltCandidates(box);
            Item turretItem = __instance.Item;

            // 炮口位置：普通弹直接沿用引擎刚放好的位置；hitscan 弹不行——
            // Turret.Launch 内部会一路 DoHitscan 把弹推到命中点（打空气才留在炮口），
            // 后置里再读 SimPosition 拿到的就是"命中位置"，补射会从命中点冒出来
            //（表现就是"打中生物后重新散射四发"）。所以 hitscan 用引擎那套炮口坐标自己算一遍。
            Projectile firedProjectile = __0.GetComponent<Projectile>();
            bool firedHitscan = firedProjectile != null && firedProjectile.Hitscan;
            // 炮口/基线优先用前缀（引擎动手之前）抄下来的那一份：hitscan 弹在后置里读到的位置和朝向
            // 可能已经被 DoHitscan 改过（命中点、翻转）——那就是"偶发反向射弹"的来源。
            bool hasCapture = capturedLaunches.TryGetValue(__instance, out var captured);
            if (hasCapture) { capturedLaunches.Remove(__instance); }
            Vector2 simPos = hasCapture ? captured.Muzzle : __0.SimPosition;
            Vector2 displayPos = ConvertUnits.ToDisplayUnits(simPos);   // 生成队列要显示坐标
            float baseRotation = hasCapture ? captured.Rotation : __0.body.Rotation;   // 缺捕获值时才回退读弹自身
            float fanRad = MathHelper.ToRadians(p.FanAngleDeg);
            float damageMultiplier = __instance.DamageMultiplier;
            float impulseModifier = __instance.LaunchImpulse;
            Character user = __1;

            // 补射的射线/碰撞要忽略三类刚体，否则"一出生就命中"：
            //  1) 自家潜艇的刚体——炮口贴着壳面时补射会蹭到自家外壳，弹药的 OnImpact 会在炮口触发
            //  2) 引擎那一发的刚体——同一帧它就停在炮口同一个点上
            //  3) 先一步生成的兄弟补射的刚体——四发都在同一点生成（下面每生成一发就加进列表）
            // 不忽略的后果：红符在炮口原地生成侧向弹；幻想风靡这种 removeonhit 的弹会被直接移除，
            // 甚至因为 body.Dir 被翻成 -1，在 Projectile.Use 里撞上 `num -= PI` 而反向飞出。
            List<FarseerPhysics.Dynamics.Body> ignoredBodies = new List<FarseerPhysics.Dynamics.Body>();
            if (turretItem.Submarine?.PhysicsBody != null)
            {
                ignoredBodies.Add(turretItem.Submarine.PhysicsBody.FarseerBody);
            }
            if (__0.body != null)
            {
                ignoredBodies.Add(__0.body.FarseerBody);
            }

            // 这一轮真正打出去的补射（连同各自的角度）：稍后广播给客户端做本地重演
            List<(Item Bolt, float Rotation)> launchedBolts = new List<(Item Bolt, float Rotation)>(extra + 1);

            for (int i = 0; i < extra; i++)
            {
                float t = (i + 1f) / (extra + 1f) - 0.5f;     // 在 (-0.5, 0.5) 上均匀铺开，对称于基线
                float rotation = baseRotation + fanRad * t;
                ItemPrefab prefab = ChargedSpellCardData.PickBoltPrefab(boltCandidates, fallbackPrefab);
                // 第三个参数特意传 null 而不是自家潜艇：生成消息会把"潜艇"一起发给客户端，
                // 而我们的弹随后会被摆成"世界坐标 + Submarine = null"（引擎对飞行中射弹的口径）。
                // 如果生成消息里带着潜艇，客户端那份就会变成"世界坐标却挂着船"——两端坐标口径不一致，
                // 位置同步和本地模拟互相打架，表现就是弹体在弹道上来抽搐。传 null 让两端一开始就同口径
                // （引擎构造函数会自己在生成位置找舱室，真在船内的话它会自动认出来）。
                Entity.Spawner.AddItemToSpawnQueue(prefab, displayPos, (Submarine)null, onSpawned: spawned =>
                {
                    if (spawned == null || spawned.Removed) { return; }
                    // hitscan 的弹要提前登记"延后移除"：引擎 DoHitscan 结尾会立刻请求移除，
                    // 只有先登记才拦得住（拦下来的这段窗口是给客户端做本地重演/描弹道用的）。
                    if (spawned.GetComponent<Projectile>()?.Hitscan == true) { ChargedSpellCardHitscanHoldPatch.Hold(spawned); }
                    ChargedSpellCardLauncher.LaunchBolt(turretItem, __instance, spawned, rotation, user, __3, ignoredBodies);
                    launchedBolts.Add((spawned, rotation));
                });
            }

            // 生成队列默认要等下一帧的 EntitySpawner.Update 才会真正建出实体，那会让补射比原生那一发
            // 慢一帧（看上去就是"先飞出去一发，后面几发才冒出来"）。这里把队列立刻抽干，让 5 发同帧出膛。
            Entity.Spawner.Update();

            // 引擎那一发如果是 hitscan，也一并登记进同步消息（客户端只有这样才能把它也画出来）。
            // 它的角度在 DoHitscan 结束时会被复原成发射时的角度，直接读弹体即可。
            if (firedHitscan && __0 != null && !__0.Removed && __0.body != null)
            {
                launchedBolts.Add((__0, __0.body.Rotation));
            }

            // 多人：把这一轮补射的发射参数发给客户端，让它们用同一套动作把本地的弹重演一遍。
            // 不发的话客户端那份弹体收不到速度/朝向，只会被服务端的周期位置同步拖着走——
            // 表现就是"补射在弹道上来回抽搐、方向也是乱的"（实体本身会由生成队列事件正常同步过去）。
            ChargedSpellCardNetSync.SendBolts(turretItem, user, launchedBolts);

            if (ChargedLog.Verbose)
            {
                ChargedLog.Log($"{turretItem.Prefab.Identifier}: 齐射 {1 + extra}/{p.BurstCount} 发（扇形 {p.FanAngleDeg:0.#}°，炮口 {simPos.X:0.#},{simPos.Y:0.#}，基线 {MathHelper.ToDegrees(baseRotation):0.#}°，"
                    + $"弹药耐久 {(box != null ? box.Condition : -1f):0.##}{(firedHitscan ? "，hitscan 用算出来的炮口" : "")}"
                    + $"，船位 {(turretItem.Submarine != null ? $"{turretItem.Submarine.SimPosition.X:0.#},{turretItem.Submarine.SimPosition.Y:0.#}" : "无")}）");
            }
        }

        // 引擎自己算炮口用的是 private 方法 Turret.GetRelativeFiringPosition(bool useOffset = true)，只能反射。
        // 用委托而不是 MethodInfo.Invoke：Invoke 每次都要装箱 object[] 参数、返回值也要装箱，
        // 每轮齐射省一次分配（本炮在场上一秒能打好几轮）。
        //（反射既有先例：本仓库 HomingProjectiles.cs 反射 Turret.GetFriendlyTeam）
        static readonly Func<Turret, bool, Vector2> getRelativeFiringPosition = CreateRelativeFiringPositionDelegate();

        static Func<Turret, bool, Vector2> CreateRelativeFiringPositionDelegate()
        {
            try
            {
                MethodInfo method = typeof(Turret).GetMethod("GetRelativeFiringPosition", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null) { return null; }
                return (Func<Turret, bool, Vector2>)Delegate.CreateDelegate(typeof(Func<Turret, bool, Vector2>), method);
            }
            catch (Exception e)
            {
                ChargedLog.Warn("绑定炮口计算方法失败（改回兜底位置）: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 复刻引擎 Turret.Launch 的炮口计算：先取 GetRelativeFiringPosition，再做一次
        /// "炮塔 → 炮口"的遮挡检查（过滤器忽略自家潜艇），被船壳挡住就退到 LastPickedPosition。
        /// 少了这一步，hitscan 补射会从船壳里起步，射线一出门就打在自己船上——
        /// 弹药的 OnImpact 会在炮口触发（例如红符那两条侧向弹会从炮口冒出来）。
        /// 客户端重演补射时也用同一个方法取炮口（结果与服务端一致到几像素内）。
        /// </summary>
        internal static Vector2 GetMuzzleSimPosition(Turret turret)
        {
            Vector2 muzzle;
            if (getRelativeFiringPosition != null)
            {
                try
                {
                    muzzle = ConvertUnits.ToSimUnits(getRelativeFiringPosition(turret, true));
                }
                catch (Exception e)
                {
                    ChargedLog.Warn("算炮口位置失败，退回炮塔自身位置: " + e.Message);
                    return turret.Item.SimPosition;
                }
            }
            else
            {
                return turret.Item.SimPosition;
            }

            try
            {
                Item turretItem = turret.Item;
                FarseerPhysics.Dynamics.Body blocked = Submarine.PickBody(
                    ConvertUnits.ToSimUnits(turretItem.WorldPosition), muzzle, null,
                    FarseerPhysics.Dynamics.Category.Cat1, ignoreSensors: true,
                    (FarseerPhysics.Dynamics.Fixture f) => !(f.Body.UserData is Submarine sub) || sub != turretItem.Submarine,
                    allowInsideFixture: true);
                if (blocked != null) { muzzle = Submarine.LastPickedPosition; }
            }
            catch (Exception e)
            {
                ChargedLog.Warn("炮口遮挡收缩失败（不影响发射）: " + e.Message);
            }
            return muzzle;
        }
    }

    /// <summary>
    /// 排查用探针：记录每一次 hitscan 射线真正用的方向（DoHitscan 的 dir + 弹体当时朝向/Dir/所属潜艇）。
    /// "偶发反向弹道"只可能出在三个地方：我们补射的那一发、引擎原生那一发、或者弹药 OnImpact 生成的东西。
    /// 这条日志把前两者的实际方向写死；方向对得上就说明问题不在射线本身。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "DoHitscan")]
    internal static class ChargedSpellCardHitscanProbePatch
    {
        /// <summary>本次 DoHitscan 的射线口径（起点=开火瞬间弹体位置，方向=弹体朝向），
        /// 供假命中判定使用（命中点必然落在这条射线上）。</summary>
        internal static readonly Dictionary<Item, (Vector2 Origin, float Rotation)> Rays = new();

        static void Prefix(Projectile __instance, Vector2 dir)
        {
            try
            {
                Item item = __instance?.Item;
                if (item == null || item.Prefab == null) { return; }
                // O(1) 认门：只有本炮的发射窗口（Turret.Launch 调用期间）才做记录，
                // 别家武器的 hitscan 在这里一个静态比较就返回了。
                // 不看 Launcher 是有意为之：窗口内同步发生的一切 hitscan 都属于"这一炮"——
                // 包括弹药自己 OnImpact 生成的子射弹（它们的 Launcher 不是炮塔物品）。
                if (ChargedSpellCardLaunchPatch.ActiveLauncher == null) { return; }

                // 无论日志开不开都要记：后面的假命中判定要用
                if (item.body != null) { Rays[item] = (item.WorldPosition, item.body.Rotation); }
                if (!ChargedLog.Verbose) { return; }

                float dirDeg = MathHelper.ToDegrees((float)Math.Atan2(dir.Y, dir.X));
                float bodyDeg = item.body != null ? MathHelper.ToDegrees(item.body.Rotation) : 0f;
                float bodyDir = item.body != null ? item.body.Dir : 0f;
                ChargedLog.Log($"[探针·射线] {item.Prefab.Identifier}: 射线 {dirDeg:0.#}°，弹体朝向 {bodyDeg:0.#}°，Dir {bodyDir:0}，"
                    + $"sub {(item.Submarine != null ? "有" : "无")}，位置 {item.SimPosition.X:0.#},{item.SimPosition.Y:0.#}"
                    + $"（世界 {item.WorldPosition.X:0.#},{item.WorldPosition.Y:0.#}）");
            }
            catch (Exception e) { ChargedLog.Warn("[探针·射线] 记录出错: " + e.Message); }
        }

        /// <summary>DoHitscan 结束（正常或异常）就清掉记录，避免字典里留死引用。</summary>
        static void Finalizer(Projectile __instance)
        {
            try
            {
                if (__instance?.Item != null) { Rays.Remove(__instance.Item); }
            }
            catch { /* 清理用的，失败不影响任何东西 */ }
        }
    }

    /// <summary>
    /// 反向光束的根因处置：把"不在射线上的命中"丢掉。
    ///
    /// 引擎 Projectile.DoHitscan 除了在本坐标系里打射线，还会调 RayCastInOtherSubs 把同一条射线
    /// 投射到 Submarine.Loaded 里每一艘潜艇自己的坐标系里，命中点再按 +该潜艇位置 折算回来。
    /// 对停在隐藏位（Submarine.HiddenSubStartPosition，约 -50000,10000）的潜艇，折算出来的"命中点"
    /// 会离开炮口几千个显示单位——实测就是"起点 772,1047 → 终点 -2552,-1196，连线 -146°"，
    /// 而同一条弹的射线方向是 +15.3°。这种假命中还会让这一发被判定为已命中而被吃掉。
    ///
    /// 判定规则（只对"本炮打出去的 hitscan 弹"生效）：合法命中点必然在射线上——
    /// 引擎报的就是射线与碰撞体的交点，贴脸那种用的是射线起点；所以垂直偏离射线超过
    /// OffRayTolerance 或者落在射线反方向的，一定是折算错位的产物，按"未命中"返回（引擎也这么用返回值）。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "HandleProjectileCollision")]
    internal static class ChargedSpellCardBogusHitPatch
    {
        /// <summary>允许的垂直偏离（显示单位，100 ≈ 1 米）。船上正常的命中偏离接近 0，
        /// 折算错位的量级是几千，取 200 两头都留足余量。</summary>
        const float OffRayTolerance = 200f;

        static bool Prefix(Projectile __instance, FarseerPhysics.Dynamics.Fixture target, ref bool __result)
        {
            try
            {
                Item item = __instance?.Item;
                if (item == null || item.Prefab == null) { return true; }
                // 有射线记录 = 这一发属于本炮当前这次发射（含弹药生成的子射弹），才做判定
                if (!ChargedSpellCardHitscanProbePatch.Rays.TryGetValue(item, out var ray)) { return true; }

                Vector2 delta = item.WorldPosition - ray.Origin;
                float dirX = (float)Math.Cos(ray.Rotation);
                float dirY = (float)Math.Sin(ray.Rotation);
                float along = delta.X * dirX + delta.Y * dirY;                 // 沿射线方向的分量
                float offRay = Math.Abs(delta.Y * dirX - delta.X * dirY);      // 到射线的垂直距离
                if (offRay <= OffRayTolerance && along >= -OffRayTolerance) { return true; }

                if (ChargedLog.Verbose)
                {
                    ChargedLog.Log($"[丢弃假命中] {item.Prefab.Identifier}: 命中点偏射线 {offRay:0}，沿线 {along:0}；"
                        + $"射线起点 {ray.Origin.X:0},{ray.Origin.Y:0} 方向 {MathHelper.ToDegrees(ray.Rotation):0.#}°，"
                        + $"弹体位置 {item.WorldPosition.X:0},{item.WorldPosition.Y:0}（sub {(item.Submarine != null ? "有" : "无")}），"
                        + $"打到 {DescribeFixtureOwner(target)}");
                }
                __result = false;   // 与引擎"这次碰撞不算命中"的返回值一致
                return false;       // 跳过原方法：不结算伤害、不触发 OnImpact、不占命中数
            }
            catch (Exception e)
            {
                ChargedLog.Warn("[丢弃假命中] 判定出错: " + e.Message);
                return true;
            }
        }

        /// <summary>把命中体的归属写清楚（是船壳、舱室，还是某艘潜艇身上的东西），便于确认假命中来自哪。</summary>
        static string DescribeFixtureOwner(FarseerPhysics.Dynamics.Fixture target)
        {
            object bodyData = target?.Body?.UserData;
            object fixtureData = target?.UserData;
            Submarine owner = (bodyData as Entity)?.Submarine ?? (fixtureData as Entity)?.Submarine;
            if (bodyData is Submarine bodySub) { owner = bodySub; }
            string typeName = bodyData?.GetType().Name ?? fixtureData?.GetType().Name ?? "?";
            return $"{typeName}{(owner != null ? $"（艇 {owner.Info?.Name}）" : "（无艇）")}";
        }
    }

    /// <summary>
    /// 光束探针 + 兜底修正：记录 hitscan 光束真正描出来的两个端点（引擎 LaunchProjSpecific 的 tracer 参数）
    /// 和"起点→终点"的连线方向。顺带做一道保险：hitscan 的光束不可能指向弹体自己的背面，
    /// 万一有别的机制又把终点算歪（比如引擎未来改版），这里按弹体朝向把长度接回去，不让玩家看到反向光束。
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "LaunchProjSpecific", new Type[] { typeof(Vector2), typeof(Vector2) })]
    internal static class ChargedSpellCardTracerProbePatch
    {
        static void Prefix(Projectile __instance, Vector2 startLocation, ref Vector2 endLocation)
        {
            try
            {
                Item item = __instance?.Item;
                if (item == null || item.Prefab == null) { return; }
                // 同上：只要在本炮这次发射的窗口里就管（含弹药生成的子射弹）
                if (ChargedSpellCardLaunchPatch.ActiveLauncher == null) { return; }

                Vector2 delta = endLocation - startLocation;
                float length = delta.Length();
                if (length <= 1f) { return; }   // 贴脸那种零长光束没什么可看的
                float bodyRot = item.body != null ? item.body.Rotation : 0f;
                float dirX = (float)Math.Cos(bodyRot);
                float dirY = (float)Math.Sin(bodyRot);

                // 兜底：连线方向与弹体朝向超过 90° = 光束指向了背面，终点必然是算错的那个
                if (item.body != null && delta.X * dirX + delta.Y * dirY < 0f)
                {
                    endLocation = startLocation + new Vector2(dirX, dirY) * length;
                    ChargedLog.Warn($"[修正反向光束] {item.Prefab.Identifier}: 原终点 {delta.X:0},{delta.Y:0}"
                        + $"按弹体朝向 {MathHelper.ToDegrees(bodyRot):0.#}° 修正，长度保持 {length:0}（若频繁出现请回报）");
                }

                if (!ChargedLog.Verbose) { return; }
                float linkDeg = MathHelper.ToDegrees((float)Math.Atan2(delta.Y, delta.X));
                ChargedLog.Log($"[探针·光束] {item.Prefab.Identifier}: 起点 {startLocation.X:0},{startLocation.Y:0} → 终点 {endLocation.X:0},{endLocation.Y:0}"
                    + $"（连线 {linkDeg:0.#}°，长 {length:0}），弹体朝向 {MathHelper.ToDegrees(bodyRot):0.#}°，"
                    + $"世界位置 {item.WorldPosition.X:0},{item.WorldPosition.Y:0}");
            }
            catch (Exception e) { ChargedLog.Warn("[探针·光束] 记录出错: " + e.Message); }
        }
    }

    /// <summary>
    /// 补射统一的"打出"动作。
    ///
    /// 关键：**直接调用引擎自己的 Turret.Launch**，不自己复刻炮口计算/坐标口径/速度继承。
    /// 之前手写那套（自己 SetTransform + 强制 Submarine=null + Use）虽然大部分时候看着正常，
    /// 但和引擎那一发仍有细微差异，实测会留下"某发弹在炮口贴脸命中、弹道退化成零长度"这类问题。
    /// 走引擎原函数后，补射与引擎那一发是同一条代码路径：同一套炮口收缩、同一套找舱室/换帧、
    /// 同样的速度继承、同样的伤害倍率与音效粒子——两端（服务端/客户端重演）也都用它。
    ///
    /// 两个注意点：
    ///   · 引擎内部的方向是 `0f - launchRotation`，所以要传 −扇形角；
    ///   · 引擎 Launch 会把弹塞进 turret.activeProjectiles（原生 maxactiveprojectiles 只该统计
    ///     "炮塔自己那一发"，所以调用后把它摘出来，保持原有语义）。
    /// </summary>
    internal static class ChargedSpellCardLauncher
    {
        static readonly Action<Turret, Item, Character, float?, float> turretLaunch = CreateTurretLaunchDelegate();
        static readonly FieldInfo activeProjectilesField =
            typeof(Turret).GetField("activeProjectiles", BindingFlags.NonPublic | BindingFlags.Instance);
        static bool launchMissingWarned;

        static Action<Turret, Item, Character, float?, float> CreateTurretLaunchDelegate()
        {
            try
            {
                MethodInfo method = typeof(Turret).GetMethod("Launch", BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null) { return null; }
                return (Action<Turret, Item, Character, float?, float>)Delegate.CreateDelegate(
                    typeof(Action<Turret, Item, Character, float?, float>), method);
            }
            catch (Exception e)
            {
                ChargedLog.Warn("绑定 Turret.Launch 失败（补射将无法发射）: " + e.Message);
                return null;
            }
        }

        /// <summary>把一发补射交给引擎的 Turret.Launch 打出去。rotation 是我们算好的扇形角（弧度）。</summary>
        internal static void LaunchBolt(Item turretItem, Turret turret, Item bolt, float rotation,
                                        Character user, float tinkeringStrength, List<FarseerPhysics.Dynamics.Body> ignoredBodies)
        {
            if (bolt == null || bolt.Removed || bolt.body == null || turret == null) { return; }
            if (turretLaunch == null)
            {
                if (!launchMissingWarned)
                {
                    launchMissingWarned = true;
                    ChargedLog.Warn("找不到 Turret.Launch，补射只能跳过（引擎改版？）");
                }
                return;
            }

            // 顺手把"本炮正在发射"的窗口标起来：假命中处置、光束探针都靠它认门。
            // 服务端本来就是发射窗口内（Launch 前缀设过），客户端重演这条路径需要自己设。
            Item previousLauncher = ChargedSpellCardLaunchPatch.ActiveLauncher;
            ChargedSpellCardLaunchPatch.ActiveLauncher = turretItem;
            try
            {
                Projectile projectile = bolt.GetComponent<Projectile>();
                if (projectile == null) { return; }
                // 引擎的 Launch 会往 IgnoredBodies 里补一个触发器刚体（要求非 null），先给上我们的表：
                // 自家船体 / 引擎那一发 / 已经打出去的兄弟弹（见调用方）
                if (ignoredBodies != null) { projectile.IgnoredBodies = ignoredBodies; }

                turretLaunch(turret, bolt, user, 0f - rotation, tinkeringStrength);

                // 摘出 activeProjectiles：原生限流只该数"炮塔自己那一发"，补射不计（与 XML 注释一致）
                if (activeProjectilesField?.GetValue(turret) is List<Item> active) { active.Remove(bolt); }

                // 让后续的兄弟弹忽略这一发（同一轮里几发都从炮口出发）
                if (ignoredBodies != null && bolt.body != null && !ignoredBodies.Contains(bolt.body.FarseerBody))
                {
                    ignoredBodies.Add(bolt.body.FarseerBody);
                }

                if (ChargedLog.Verbose)
                {
                    ChargedLog.Log($"补射 {bolt.Prefab.Identifier}: 注入朝向 {MathHelper.ToDegrees(rotation):0.#}°"
                        + $"，Dir={(bolt.body != null ? bolt.body.Dir : 0f):0}，bodyRot={(bolt.body != null ? MathHelper.ToDegrees(bolt.body.Rotation) : 0f):0.#}°，"
                        + $"sub={(bolt.Submarine != null ? "有" : "无")}，世界 {bolt.WorldPosition.X:0.#},{bolt.WorldPosition.Y:0.#}"
                        + $"{(bolt.Removed ? "，已移除" : "")}");
                }
            }
            catch (Exception e)
            {
                ChargedLog.Warn("补射发射出错: " + e.Message);
            }
            finally
            {
                ChargedSpellCardLaunchPatch.ActiveLauncher = previousLauncher;
            }
        }

        /// <summary>
        /// 客户端重演用：构建"要忽略的刚体"表。服务端那边表里是自家船体 + 引擎那一发 + 兄弟补射；
        /// 客户端手上只有自己在重演的这一发，所以按"炮口附近带 Projectile 的东西"来兜——
        /// 同一轮里引擎那一发和兄弟弹都落在炮口附近，正好一网打尽，也不会误伤远处的正常目标。
        /// </summary>
        internal static List<FarseerPhysics.Dynamics.Body> BuildClientIgnoredBodies(Item turretItem, Vector2 muzzleSimPos, Item exclude)
        {
            List<FarseerPhysics.Dynamics.Body> list = new List<FarseerPhysics.Dynamics.Body>();
            try
            {
                if (turretItem?.Submarine?.PhysicsBody != null) { list.Add(turretItem.Submarine.PhysicsBody.FarseerBody); }
                const float radius = 3f;                     // sim 单位（3 米）
                float radiusSq = radius * radius;
                foreach (Item other in Item.ItemList)
                {
                    if (other == null || other == exclude || other.Removed || other.body == null) { continue; }
                    if (Vector2.DistanceSquared(other.SimPosition, muzzleSimPos) > radiusSq) { continue; }
                    if (other.GetComponent<Projectile>() == null) { continue; }
                    list.Add(other.body.FarseerBody);
                }
            }
            catch (Exception e) { ChargedLog.Warn("构建忽略表出错: " + e.Message); }
            return list;
        }
    }

    /// <summary>
    /// 多人同步：服务端把每轮补射的发射参数（炮塔 / 弹 / 角度 / 射手）广播给客户端，
    /// 客户端用 ChargedSpellCardLauncher 跑一遍同样的动作，让本地那份弹体自己飞起来。
    ///
    /// 为什么必须自己发：引擎里本来有一条 Turret.ClientEventRead → Launch(...) 的路，
    /// 但正式版里没有任何地方构造它的 EventData（整个程序集搜不到构造点），那条路是死的——
    /// 服务端生成的补射到了客户端只有"实体生成 + 周期位置同步"，速度/朝向没人告诉它。
    ///
    /// 走 LuaCs 自带的网络服务（参考模组 3792205908 用同一套调用验证可用）：
    ///   Start(netId) → 写入 → Send(msg, conn, DeliveryMethod.Reliable)（conn = null 即广播给全体客户端）
    ///   Receive(netId, handler) 注册接收端；不处理的一侧也要占位注册（LuaCs 靠它交换 netId 定义）
    /// </summary>
    internal static class ChargedSpellCardNetSync
    {
        const string NetIdBoltLaunch = "touhou.scatter.bolts";

        static bool registered;
        static bool registerFailed;

        static MethodInfo netSendToConn;
        static MethodInfo netSendAll;
        static bool sendResolved;

        /// <summary>是否有还没落实的补射重演（客户端用；生成消息一到就处理，见 ChargedSpellCardSpawnFlushPatch）。</summary>
        internal static bool HasPending => pending.Count > 0;

        struct PendingBolt
        {
            public ushort TurretId;
            public ushort UserId;
            public ushort BoltId;
            public float Rotation;
            public double ExpireTime;
        }

        static readonly List<PendingBolt> pending = new List<PendingBolt>();
        static bool pumpScheduled;

        /// <summary>注册同步消息。两侧都要注册（本侧不处理也注册，LuaCs 靠它交换 netId）。</summary>
        public static void EnsureRegistered()
        {
            if (registered || registerFailed) { return; }
            if (GameMain.NetworkMember == null) { return; }   // 单机没有网络服务，不用注册（也就不会有告警噪音）
            try
            {
                object netObj = LuaCsSetup.Instance?.Networking;
                if (netObj == null) { return; }
                if (!RegisterReceive(netObj))
                {
                    registerFailed = true;
                    ChargedLog.Warn("注册补射同步失败（没找到 NetworkingService.Receive(string, Delegate)）");
                    return;
                }
                registered = true;
                if (ChargedLog.Verbose) { ChargedLog.Log("补射同步消息已注册"); }
            }
            catch (Exception e)
            {
                registerFailed = true;
                ChargedLog.Warn("注册补射同步失败（多人下客户端补射会不同步）: " + e.Message);
            }
        }

        /// <summary>
        /// 按形状反射注册 Receive：客户端/专用服务器的程序集里第二个参数是**不同的委托类型**
        /// （签名不一致，直接 new 委托会 CS0123），所以用表达式树构造一个与运行时委托匹配的匿名委托，
        /// 把参数包成 object[] 转交给 OnBoltLaunchMessageCompat。
        /// </summary>
        static bool RegisterReceive(object netObj)
        {
            MethodInfo recv = null;
            foreach (MethodInfo m in netObj.GetType().GetMethods())
            {
                if (m.Name != "Receive") { continue; }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsSubclassOf(typeof(Delegate)))
                { recv = m; break; }
            }
            if (recv == null) { return false; }

            Type delType = recv.GetParameters()[1].ParameterType;
            MethodInfo invoke = delType.GetMethod("Invoke");
            ParameterInfo[] dps = invoke.GetParameters();
            var parameters = dps.Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name ?? "p")).ToArray();
            var array = System.Linq.Expressions.Expression.NewArrayInit(typeof(object),
                parameters.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));
            var call = System.Linq.Expressions.Expression.Call(
                typeof(ChargedSpellCardNetSync).GetMethod(nameof(RunHandler), BindingFlags.NonPublic | BindingFlags.Static),
                System.Linq.Expressions.Expression.Constant(new Action<object[]>(OnBoltLaunchMessageCompat)), array);
            Delegate handler = System.Linq.Expressions.Expression.Lambda(delType, call, parameters).Compile();
            recv.Invoke(netObj, new object[] { NetIdBoltLaunch, handler });
            return true;
        }

        static void RunHandler(Action<object[]> handler, object[] args) => handler(args);

        /// <summary>把运行时委托的参数（可能是 1 个或 2 个参数：消息 / 消息+发送者）里的消息取出来。</summary>
        static void OnBoltLaunchMessageCompat(object[] args)
        {
            if (args == null) { return; }
            foreach (object a in args)
            {
                if (a is IReadMessage msg) { OnBoltLaunchMessage(msg); return; }
            }
        }

        /// <summary>服务端：把这一轮补射的发射参数广播出去。</summary>
        public static void SendBolts(Item turretItem, Character user, List<(Item Bolt, float Rotation)> bolts)
        {
            if (turretItem == null || bolts == null || bolts.Count == 0) { return; }
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) { return; }   // 单机、纯客户端都不发
            try
            {
                EnsureRegistered();
                // 注意：LuaCs 的网络服务类型是内部类型，而且客户端/专用服务器两个程序集里的签名未必一致
                //（注册那边就踩过 CS0123），所以这里一律按 object + 反射用，不写死任何 LuaCs 内部类型名。
                object netObj = LuaCsSetup.Instance?.Networking;
                if (netObj == null) { return; }
                IWriteMessage msg = StartMessage(netObj);
                if (msg == null) { return; }
                int count = Math.Min(bolts.Count, 255);
                msg.WriteUInt16(turretItem.ID);
                msg.WriteUInt16(user?.ID ?? 0);
                msg.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    msg.WriteUInt16(bolts[i].Bolt.ID);
                    msg.WriteSingle(bolts[i].Rotation);
                }
                if (!TrySend(netObj, msg, null))
                {
                    ChargedLog.Warn("补射同步发送失败（客户端补射会不同步）");
                }
                else if (ChargedLog.Verbose)
                {
                    ChargedLog.Log($"补射同步已广播：{count} 发（客户端会按同一套动作重演）");
                }
            }
            catch (Exception e) { ChargedLog.Warn("补射同步发送出错: " + e.Message); }
        }

        static MethodInfo startMethod;

        /// <summary>反射调用 LuaCs 网络服务的 Start(string) 取一个待写消息（类型/重载都可能因程序集而变，按形状找）。</summary>
        static IWriteMessage StartMessage(object netObj)
        {
            if (startMethod == null)
            {
                foreach (MethodInfo m in netObj.GetType().GetMethods())
                {
                    ParameterInfo[] ps = m.GetParameters();
                    if (m.Name == "Start" && ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        startMethod = m;
                        break;
                    }
                }
                if (startMethod == null) { ChargedLog.Warn("补射同步：LuaCs 网络服务里没有 Start(string)"); }
            }
            return startMethod?.Invoke(netObj, new object[] { NetIdBoltLaunch }) as IWriteMessage;
        }

        static bool TrySend(object netObj, IWriteMessage msg, NetworkConnection conn)
        {
            try
            {
                if (netObj == null || msg == null) { return false; }
                if (!sendResolved)
                {
                    sendResolved = true;
                    foreach (MethodInfo method in netObj.GetType().GetMethods())
                    {
                        if (method.Name != "Send") { continue; }
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length == 3 && parameters[0].ParameterType == typeof(IWriteMessage)
                            && parameters[1].ParameterType == typeof(NetworkConnection)
                            && parameters[2].ParameterType == typeof(DeliveryMethod))
                        {
                            netSendToConn = method;
                        }
                        else if (parameters.Length == 2 && parameters[0].ParameterType == typeof(IWriteMessage)
                                 && parameters[1].ParameterType == typeof(DeliveryMethod))
                        {
                            netSendAll = method;
                        }
                    }
                }
                if (netSendToConn != null)                          // 三参数版：conn = null 即广播（1.0.109 起只有这个）
                {
                    netSendToConn.Invoke(netObj, new object[] { msg, conn, DeliveryMethod.Reliable });
                    return true;
                }
                if (netSendAll != null)                             // 两参数版：全体广播
                {
                    netSendAll.Invoke(netObj, new object[] { msg, DeliveryMethod.Reliable });
                    return true;
                }
            }
            catch (Exception e) { ChargedLog.Warn("补射同步发送失败: " + e.Message); }
            return false;
        }

        static void OnBoltLaunchMessage(IReadMessage msg)
        {
            try
            {
                ushort turretId = msg.ReadUInt16();
                ushort userId = msg.ReadUInt16();
                int count = msg.ReadByte();
                double expire = Timing.TotalTime + 3.0;             // 等实体生成消息到达的宽限时间
                for (int i = 0; i < count; i++)
                {
                    ushort boltId = msg.ReadUInt16();
                    float rotation = msg.ReadSingle();
                    pending.Add(new PendingBolt
                    {
                        TurretId = turretId,
                        UserId = userId,
                        BoltId = boltId,
                        Rotation = rotation,
                        ExpireTime = expire,
                    });
                }
                PumpPending();
                if (ChargedLog.Verbose) { ChargedLog.Log($"收到补射同步：{count} 发（炮塔 {turretId}）"); }
            }
            catch (Exception e) { ChargedLog.Warn("补射同步消息解析失败: " + e.Message); }
        }

        /// <summary>
        /// 客户端重演：消息可能比"实体生成"先到（两条消息走的通道不同），所以拿不到弹体就先挂着，
        /// 最多等 3 秒；实体一生成（见 ChargedSpellCardSpawnFlushPatch）或每 50ms 都会再试一次，
        /// 到点还没出现就放弃并记一条日志。
        /// </summary>
        internal static void PumpPending()
        {
            pumpScheduled = false;
            if (pending.Count == 0) { return; }
            try
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    PendingBolt entry = pending[i];
                    Item bolt = Entity.FindEntityByID(entry.BoltId) as Item;
                    Item turretItem = Entity.FindEntityByID(entry.TurretId) as Item;
                    if (bolt == null || bolt.Removed || turretItem == null)
                    {
                        if (Timing.TotalTime > entry.ExpireTime)
                        {
                            pending.RemoveAt(i);
                            if (ChargedLog.Verbose) { ChargedLog.Log($"补射同步放弃：弹 {entry.BoltId} 没在时限内出现"); }
                        }
                        continue;
                    }
                    pending.RemoveAt(i);
                    Turret turret = turretItem.GetComponent<Turret>();
                    if (turret == null || bolt.body == null) { continue; }
                    Character user = Entity.FindEntityByID(entry.UserId) as Character;
                    // 扫描中心只是个近似值（引擎的炮口计算在 Launch 内部自己会做），用来找炮口附近的同位弹
                    Vector2 muzzle = ChargedSpellCardLaunchPatch.GetMuzzleSimPosition(turret);
                    var ignored = ChargedSpellCardLauncher.BuildClientIgnoredBodies(turretItem, muzzle, bolt);
                    ChargedSpellCardLauncher.LaunchBolt(turretItem, turret, bolt, entry.Rotation, user, 0f, ignored);
                }
            }
            catch (Exception e) { ChargedLog.Warn("补射重演出错: " + e.Message); }
            if (pending.Count > 0 && !pumpScheduled)
            {
                pumpScheduled = true;
                CoroutineManager.Invoke(PumpPending, 0.05f);
            }
        }
    }

    /// <summary>
    /// 客户端补射显形用的小补丁：客户端的实体生成消息（Item.ReadSpawnData）一落地就立刻处理挂起的补射。
    /// 我们的同步消息和"实体生成"走的是两条通道，谁先到都可能；只靠 50ms 轮询的话，
    /// hitscan 弹（服务端紧接着就把它删了）经常会错过——所以生成一到就马上发射。
    /// </summary>
    [HarmonyPatch(typeof(Item), "ReadSpawnData")]
    internal static class ChargedSpellCardSpawnFlushPatch
    {
        static void Postfix()
        {
            if (ChargedSpellCardNetSync.HasPending) { ChargedSpellCardNetSync.PumpPending(); }
        }
    }

    /// <summary>
    /// hitscan 弹"多活一会儿"的补丁。
    ///
    /// 引擎 Projectile.DoHitscan 结尾会立刻把这发弹放进移除队列，而"实体生成"和"移除"两个网络事件
    /// 是同一次刷新发出去的——客户端那份弹还没来得及用我们的同步消息打出去（描出弹道）就被删了，
    /// 表现就是客户端只看得见引擎那一发。这里把本门炮这一轮打出去的 hitscan 弹的移除拦下来，
    /// 推迟一个很短的时间再真正移除：期间把它藏起来、物理体停掉（不会碰撞、不会再触发 OnImpact），
    /// 客户端就在这段窗口里收到"生成 + 同步消息"，用同一套动作把本地弹打出去、自己描弹道。
    ///
    /// 登记必须发生在引擎请求移除之前：
    ///   · 补射是我们自己生成的 → 在生成回调里登记（发射动作之前）
    ///   · 引擎那一发在 Launch 内部就被请求移除了 → 只能在我们 Launch 前缀里登记
    /// </summary>
    [HarmonyPatch(typeof(EntitySpawner), "AddItemToRemoveQueue")]
    internal static class ChargedSpellCardHitscanHoldPatch
    {
        /// <summary>被拦下来的 hitscan 弹要保留多久（秒）。够"生成 + 同步消息"到达客户端并重演即可。</summary>
        const float HoldTime = 0.25f;

        static readonly HashSet<Item> hold = new HashSet<Item>();

        internal static void Hold(Item bolt)
        {
            if (bolt != null && !bolt.Removed) { hold.Add(bolt); }
        }

        static bool Prefix(Item item)
        {
            if (item == null || !hold.Remove(item)) { return true; }
            try
            {
                if (item.body != null) { item.body.Enabled = false; }   // 停掉物理体：不再碰撞、不再有位置同步
                item.HiddenInGame = true;
            }
            catch (Exception e) { ChargedLog.Warn("藏起 hitscan 弹失败: " + e.Message); }
            Item held = item;
            CoroutineManager.Invoke(() =>
            {
                if (held != null && !held.Removed && Entity.Spawner != null) { Entity.Spawner.AddItemToRemoveQueue(held); }
            }, HoldTime);
            if (ChargedLog.Verbose) { ChargedLog.Log($"hitscan 弹 {item.Prefab.Identifier} 延后 {HoldTime:0.##} 秒移除（留给客户端重演）"); }
            return false;   // 跳过引擎这次的立即移除
        }
    }
}
