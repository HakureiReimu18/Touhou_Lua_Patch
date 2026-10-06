using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
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
            }
            catch (Exception e)
            {
                ChargedLog.Warn("假命中处置补丁未挂上（反向光束可能复发）: " + e.Message);
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
        /// </summary>
        static void Prefix(Turret __instance)
        {
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) { return; }
            if (__instance?.Item == null) { return; }
            if (ChargedSpellCardData.TryGet(__instance)?.Params == null) { return; }
            capturedLaunches[__instance] = (GetMuzzleSimPosition(__instance), 0f - __instance.Rotation);
            if (ActiveLauncher == null) { ActiveLauncher = __instance.Item; }   // 不覆盖外层发射（嵌套时各管各的）
        }

        /// <summary>无论正常结束还是抛异常都要清掉发射标记，另外顺手清掉可能残留的炮口记录。</summary>
        static void Finalizer(Turret __instance, Exception __exception)
        {
            if (__instance?.Item == null) { return; }
            if (ActiveLauncher == __instance.Item) { ActiveLauncher = null; }
            if (__exception != null) { capturedLaunches.Remove(__instance); }
        }

        static void Postfix(Turret __instance, Item __0, Character __1)
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

            for (int i = 0; i < extra; i++)
            {
                float t = (i + 1f) / (extra + 1f) - 0.5f;     // 在 (-0.5, 0.5) 上均匀铺开，对称于基线
                float rotation = baseRotation + fanRad * t;
                ItemPrefab prefab = ChargedSpellCardData.PickBoltPrefab(boltCandidates, fallbackPrefab);
                Entity.Spawner.AddItemToSpawnQueue(prefab, displayPos, turretItem.Submarine, onSpawned: spawned =>
                {
                    try
                    {
                        if (spawned == null || spawned.Removed) { return; }
                        Projectile projectile = spawned.GetComponent<Projectile>();
                        if (projectile == null) { return; }
                        // 发射方式完全复刻引擎的 Turret.Launch：设好位置/朝向/倍率，再调 Projectile.Use。
                        // 不再用 Projectile.Shoot —— 它走的是"武器→弹"那条路（带 PickBody 遮挡收缩），
                        // 而且一旦 body.Dir 不是 1，Use 里的 `if (Dir < 0) num -= PI` 会把整发弹反向 180°
                        // （就是那个"偶发反向射弹"）。这里 Dir 明确写死 1，方向只由下面算出的扇形角决定。
                        projectile.Launcher = turretItem;
                        projectile.Attacker = user;                            // 与原生 Turret.Launch 对齐（击杀归属/仇恨）
                        projectile.IgnoredBodies = ignoredBodies;              // 忽略自家外壳 / 引擎那一发 / 兄弟弹（见上）
                        if (projectile.Attack != null) { projectile.Attack.DamageMultiplier = damageMultiplier; }
                        if (spawned.body != null)
                        {
                            spawned.body.Dir = 1f;                             // 必须为 1，否则 Use 里会反向
                            spawned.body.ResetDynamics();
                            spawned.body.Enabled = true;
                        }
                        spawned.SetTransform(simPos, rotation, findNewHull: false);   // 引擎就是这么把弹放到炮口的
                        // 与引擎对齐的坐标口径：飞行中的射弹一律"世界坐标 + Submarine = null"。
                        // 引擎 Turret.Launch 把弹 SetTransform 到世界坐标后，FindHull 在炮口找不到舱室，
                        // 于是 Submarine 被置空；DoHitscan 的射线、命中点、光束 tracer 全按这个口径解释坐标。
                        // 这里显式写死，避免生成队列/FindHull 的偶然结果让某一发变成"坐标是世界的、Submarine 却挂着船"，
                        // 那样 WorldPosition 会再多叠一个船位——表现就是那一发不在炮口、方向也不对。
                        spawned.Submarine = null;
                        spawned.body.Submarine = null;
                        spawned.UpdateTransform();                             // 引擎在 SetTransform 之后紧接着就调它
                        bool subBeforeUse = spawned.Submarine != null;         // 发射前是否被 FindHull 挂上了船（异常信号）
                        projectile.Use(null, impulseModifier);                 // 引擎开火用的也是这个（内部再叠加弹自身的 spread）
                        projectile.User = user;                                // Use 内部会清空 User，和引擎一样用完再设回
                        projectile.Attacker = user;
                        // 与引擎一致：把潜艇自身的速度叠加到射弹上，否则船在移动时补射会"落在原地"
                        if (turretItem.Submarine != null && spawned.body != null)
                        {
                            Vector2 inheritedVelocity = turretItem.Submarine.PhysicsBody.LinearVelocity + spawned.body.LinearVelocity;
                            if (inheritedVelocity.LengthSquared() < 3686.4f) { spawned.body.LinearVelocity = inheritedVelocity; }
                        }
                        // 让后面几发补射忽略这一发（见上面 ignoredBodies 的说明）
                        if (spawned.body != null && !ignoredBodies.Contains(spawned.body.FarseerBody))
                        {
                            ignoredBodies.Add(spawned.body.FarseerBody);
                        }
                        if (ChargedLog.Verbose)
                        {
                            // sub 分两段记：发射前=我们把它放到炮口时有没有被 FindHull 挂上船（挂上就是坐标口径错，
                            // WorldPosition 会多叠一个船位）；发射后=命中判定里引擎自己挂的（那属于正常流程）。
                            ChargedLog.Log($"补射 {spawned.Prefab.Identifier}: 出膛点 {simPos.X:0.#},{simPos.Y:0.#}，注入朝向 {MathHelper.ToDegrees(rotation):0.#}°"
                                + $"，Dir={(spawned.body != null ? spawned.body.Dir : 0f):0}，bodyRot={(spawned.body != null ? MathHelper.ToDegrees(spawned.body.Rotation) : 0f):0.#}°，"
                                + $"sub 发射前={(subBeforeUse ? "有" : "无")}/发射后={(spawned.Submarine != null ? "有" : "无")}，"
                                + $"世界 {spawned.WorldPosition.X:0.#},{spawned.WorldPosition.Y:0.#}{(spawned.Removed ? "，已移除" : "")}");
                        }
                    }
                    catch (Exception e)
                    {
                        ChargedLog.Warn("补射发射出错: " + e.Message);
                    }
                });
            }

            // 生成队列默认要等下一帧的 EntitySpawner.Update 才会真正建出实体，那会让补射比原生那一发
            // 慢一帧（看上去就是"先飞出去一发，后面几发才冒出来"）。这里把队列立刻抽干，让 5 发同帧出膛。
            Entity.Spawner.Update();

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
        /// </summary>
        static Vector2 GetMuzzleSimPosition(Turret turret)
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
}
