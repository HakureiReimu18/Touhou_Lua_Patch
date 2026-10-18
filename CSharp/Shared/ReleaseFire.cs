using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.ReleaseFire
{
    /// <summary>
    /// 通用「松手开火」插件：任何 &lt;RangedWeapon&gt; 里加一段 &lt;ReleaseFire .../&gt; 子元素就变成蓄力炮。
    /// （原版 RangedWeapon 的构造函数只认 crosshair / crosshairpointer / particleemitter / particleemittercharge /
    /// chargesound 这几个子元素名，未知的直接忽略，所以可以这么挂。）参数与调参说明写在 Items/Item.xml（单轨一节）。
    ///
    /// 行为：按住开火键蓄力（蓄力音/粒子仍由原生 maxchargetime 驱动），松手（仍在瞄准）时按蓄力比例打出去：
    /// 伤害倍率/穿甲随蓄力从 min 涨到 max；可配硬顶（按住到点强制满蓄力发射）与过蓄自爆
    /// （Condition 清零触发 XML 的 OnBroken，条件变化会自动同步给客户端）。
    ///
    /// 为什么这样切（对旧 Lua 实现的关键改动）：
    ///   · 原生只会"蓄满自动打"，而"松手才打、蓄过头有风险"要求把开火延后到松手那一刻 —— 所以
    ///     RangedWeapon.Use 前缀永远挡掉原生开火，只把 tryingToCharge 置上，让原生继续把蓄力进程画出来；
    ///   · 真正开火时走完整 item.Use（音效/枪口闪光/弹药消耗/装填/联机事件全归原生），只在我们调用前后
    ///     临时改写 WeaponDamageModifier / Penetration / MaxChargeTime；
    ///   · 状态机双端都跑（与原版武器同款的客户端预测 + 服务端权威）：音效、枪口闪光、曳光、弹体初速
    ///     本地就能对上，不需要自定义网络消息；只有过蓄自爆的掷骰留在权威端，靠 Condition 同步出画面。
    ///
    /// 只认"真正拿在手上"（HeldItems 同款判定）：放物品栏里怎么按都不会蓄力（旧单轨的坑，见故障案例库 §2）。
    /// 改装（运行期挂 tag 改参数）走 &lt;ReleaseFireModifier&gt;；参数按帧按当前 tag 重算，挂/摘即时生效。
    /// 排查日志开关见 ReleaseFireLog.Verbose（默认关，热路径上的日志调用点都用 if 包住）。
    /// </summary>
    public sealed class ReleaseFirePlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.releasefire");
        }

        public void PreInitPatching() { }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone)
            {
                ReleaseFireLog.Log("OnLoadCompleted: 内容重载，补丁已注册，跳过");
                return;
            }
            oneTimeInitDone = true;

            // 先清掉本插件名下的历史补丁再重打，防止 reloadlua 残留堆叠
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ReleaseFireUsePatch));
            harmony.PatchAll(typeof(ReleaseFireUpdatePatch));

            ReleaseFireLog.Log("松手开火插件已加载");
        }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            ReleaseFireData.Reset();
            oneTimeInitDone = false;
        }
    }

    internal static class ReleaseFireLog
    {
        /// <summary>低频信息日志开关（参数播报、每次开火详情）。默认关；排查时改成 true。</summary>
        public static bool Verbose = false;

        public static void Log(string msg)
        {
            if (Verbose) LuaCsLogger.LogMessage($"[松手开火] {msg}", Color.LightGreen);
        }

        public static void Warn(string msg) => LuaCsLogger.LogMessage($"[松手开火] {msg}", Color.Orange);
    }

    /// <summary>
    /// 按 tag 生效的参数覆盖（改装件 / 按钮挂的 tag）。解析时把"XML 里写了这个属性"钉成一段赋值闭包，
    /// 运行期只做 tag 判定，不再碰 XML。
    /// </summary>
    internal sealed class ReleaseFireOverride
    {
        public readonly Identifier Tag;
        readonly Action<ReleaseFireParams> apply;

        public ReleaseFireOverride(Identifier tag, Action<ReleaseFireParams> apply)
        {
            Tag = tag;
            this.apply = apply;
        }

        public void Apply(ReleaseFireParams p) => apply(p);
    }

    /// <summary>一把武器的松手开火参数（来自 &lt;RangedWeapon&gt; 下的 &lt;ReleaseFire&gt; 子元素）。</summary>
    internal sealed class ReleaseFireParams
    {
        /// <summary>蓄到满的秒数：到这一刻伤害/穿甲拉到上限，之后继续蓄不再涨。</summary>
        public float ChargeTime = 3f;

        /// <summary>按住不放的硬上限（秒）：到点强制按满蓄力打出去；0 = 不限制。</summary>
        public float HardCap;

        public float MinDamageMultiplier = 1f;
        public float MaxDamageMultiplier = 1f;

        /// <summary>武器穿甲（命中时和弹药穿甲相加，封顶 1）随蓄力从 Min 涨到 Max。</summary>
        public float MinPenetration;
        public float MaxPenetration;

        /// <summary>过蓄临界（秒）：超过之后每帧掷自爆；0 = 安全武器，不掷。</summary>
        public float CritTime;

        public float ExplodeChance = 0.10f;
        public float ExplodeGrowth = 0.30f;
        public float ExplodeChanceMax = 0.95f;

        /// <summary>自爆概率总倍率（改装用；1 = 无修正）。</summary>
        public float ExplodeChanceMultiplier = 1f;

        /// <summary>没找到弹药就不开火（不放空炮、不白进装填）。</summary>
        public bool RequireAmmo = true;

        public readonly List<ReleaseFireOverride> Overrides = new List<ReleaseFireOverride>();

        public void CopyFrom(ReleaseFireParams other)
        {
            ChargeTime = other.ChargeTime;
            HardCap = other.HardCap;
            MinDamageMultiplier = other.MinDamageMultiplier;
            MaxDamageMultiplier = other.MaxDamageMultiplier;
            MinPenetration = other.MinPenetration;
            MaxPenetration = other.MaxPenetration;
            CritTime = other.CritTime;
            ExplodeChance = other.ExplodeChance;
            ExplodeGrowth = other.ExplodeGrowth;
            ExplodeChanceMax = other.ExplodeChanceMax;
            ExplodeChanceMultiplier = other.ExplodeChanceMultiplier;
            RequireAmmo = other.RequireAmmo;
        }

        /// <summary>解析 &lt;ReleaseFire&gt;；没有这个子元素就返回 null（负缓存）。</summary>
        public static ReleaseFireParams Parse(Item item)
        {
            try
            {
                if (item?.Prefab?.ConfigElement is not ContentXElement root) { return null; }

                ContentXElement weaponElement = null;
                foreach (var element in root.Elements())
                {
                    if (element is ContentXElement candidate &&
                        string.Equals(candidate.Name.LocalName, "RangedWeapon", StringComparison.OrdinalIgnoreCase))
                    {
                        weaponElement = candidate;
                        break;
                    }
                }
                if (weaponElement == null) { return null; }
                ContentXElement el = weaponElement.GetChildElement("ReleaseFire");
                if (el == null) { return null; }

                var p = new ReleaseFireParams();
                ReadInto(el, p, p.Overrides, Identifier.Empty);
                foreach (var child in el.Elements())
                {
                    if (child is not ContentXElement mod ||
                        !string.Equals(mod.Name.LocalName, "ReleaseFireModifier", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    Identifier tag = mod.GetAttributeIdentifier("tag", Identifier.Empty);
                    if (tag.IsEmpty) { continue; }
                    ReadInto(mod, p, p.Overrides, tag);
                }
                return p;
            }
            catch (Exception e)
            {
                ReleaseFireLog.Warn("解析 <ReleaseFire> 出错: " + e.Message);
                return null;
            }
        }

        /// <summary>把 XML 上出现的每个属性钉成一段赋值；没写的属性保持原值。overrideTag 非空时只登记覆盖。</summary>
        static void ReadInto(ContentXElement el, ReleaseFireParams p, List<ReleaseFireOverride> overrides, Identifier overrideTag)
        {
            void Num(string name, Action<ReleaseFireParams, float> setter)
            {
                var attr = el.GetAttribute(name);
                if (attr == null) { return; }
                float value = attr.GetAttributeFloat(0f);
                if (overrideTag.IsEmpty) { setter(p, value); }
                else { overrides.Add(new ReleaseFireOverride(overrideTag, q => setter(q, value))); }
            }

            void Range(string name, Action<ReleaseFireParams, float, float> setter)
            {
                var attr = el.GetAttribute(name);
                if (attr == null) { return; }
                ParseRange(attr.Value, out float min, out float max);
                if (overrideTag.IsEmpty) { setter(p, min, max); }
                else { overrides.Add(new ReleaseFireOverride(overrideTag, q => setter(q, min, max))); }
            }

            Num("chargetime", (q, v) => { if (v > 0f) { q.ChargeTime = v; } });
            Num("hardcap", (q, v) => q.HardCap = Math.Max(0f, v));
            Num("crittime", (q, v) => q.CritTime = Math.Max(0f, v));
            Num("explodechance", (q, v) => q.ExplodeChance = Math.Max(0f, v));
            Num("explodegrowth", (q, v) => q.ExplodeGrowth = Math.Max(0f, v));
            Num("explodechancemax", (q, v) => q.ExplodeChanceMax = Math.Max(0f, v));
            Num("explodechancemultiplier", (q, v) => q.ExplodeChanceMultiplier = Math.Max(0f, v));
            Range("damagemultiplier", (q, min, max) => { q.MinDamageMultiplier = min; q.MaxDamageMultiplier = max; });
            Range("penetration", (q, min, max) => { q.MinPenetration = min; q.MaxPenetration = max; });

            var ammoAttr = el.GetAttribute("requireammo");
            if (ammoAttr != null)
            {
                bool value = ammoAttr.GetAttributeBool(true);
                if (overrideTag.IsEmpty) { p.RequireAmmo = value; }
                else { overrides.Add(new ReleaseFireOverride(overrideTag, q => q.RequireAmmo = value)); }
            }
        }

        /// <summary>"min,max"（单值 = 固定值）。</summary>
        static void ParseRange(string text, out float min, out float max)
        {
            string[] parts = (text ?? string.Empty).Split(',');
            TryParse(parts[0], out min);
            if (parts.Length > 1) { TryParse(parts[1], out max); }
            else { max = min; }
        }

        static void TryParse(string text, out float value)
            => float.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>单把武器的运行期状态。</summary>
    internal sealed class ReleaseFireState
    {
        /// <summary>null = 不是松手开火武器（负缓存，避免每次开火重解析 XML）。</summary>
        public ReleaseFireParams Params;

        /// <summary>每帧按当前 tag 重算的有效参数（改装挂/摘即时生效，不缓存）。</summary>
        public readonly ReleaseFireParams Effective = new ReleaseFireParams();

        public bool Charging;
        public float ChargeTime;

        /// <summary>我们自己那一枪的窗口：Use 前缀看到它就放行原生。</summary>
        public bool InternalFire;

        // 开火时临时改写、随后要还原的"静止值"（首次见到这把武器时抄下来）
        public float RestDamageMultiplier = 1f;
        public float RestPenetration;
        public float RestMaxChargeTime;

        public bool ParamsLogged;
    }

    internal static class ReleaseFireData
    {
        private static readonly ConditionalWeakTable<RangedWeapon, ReleaseFireState> cache = new();

        /// <summary>只查不建：给每帧都会过的 Update 用，别给没碰过的武器建状态。</summary>
        public static ReleaseFireState TryGet(RangedWeapon weapon)
            => weapon != null && cache.TryGetValue(weapon, out ReleaseFireState state) ? state : null;

        public static ReleaseFireState GetOrCreate(RangedWeapon weapon)
        {
            if (cache.TryGetValue(weapon, out ReleaseFireState state)) { return state; }

            state = new ReleaseFireState { Params = ReleaseFireParams.Parse(weapon?.Item) };
            // 静止值要在我们动手改之前抄：之后每发打完都还原到它（XML/别的插件定的原始值）
            state.RestDamageMultiplier = weapon.WeaponDamageModifier;
            state.RestPenetration = weapon.Penetration;
            state.RestMaxChargeTime = weapon.MaxChargeTime;
            if (state.Params != null) { state.Effective.CopyFrom(state.Params); }
            cache.Add(weapon, state);

            if (state.Params != null)
            {
                if (weapon.MaxChargeTime <= 0f)
                {
                    // 原生蓄力是"0 秒"时，引擎会在首次装填结束后把组件停更（IsActive=false），状态机跟着停摆
                    ReleaseFireLog.Warn($"{weapon.Item.Prefab.Identifier}: <ReleaseFire> 需要 maxchargetime > 0"
                        + "（原生蓄力同时驱动蓄力音/粒子；为 0 会让组件在首次装填后停更，蓄力不会开始）");
                }
                else if (ReleaseFireLog.Verbose)
                {
                    ReleaseFireLog.Log($"{weapon.Item.Prefab.Identifier}: 松手开火 蓄力 {state.Params.ChargeTime:0.##} 秒 / 硬顶 {state.Params.HardCap:0.##} 秒"
                        + $" / 伤害 ×{state.Params.MinDamageMultiplier:0.##}→×{state.Params.MaxDamageMultiplier:0.##}"
                        + $" / 穿甲 {state.Params.MinPenetration:0.##}→{state.Params.MaxPenetration:0.##}"
                        + $" / 过蓄 {state.Params.CritTime:0.##} 秒起自爆（{state.Params.Overrides.Count} 条 tag 改装）");
                }
            }
            return state;
        }

        public static void Reset() => cache.Clear();
    }

    /// <summary>
    /// RangedWeapon.Use 前缀：松手开火武器永远不在这里开火 —— 只把 tryingToCharge 置上，让原生 Update
    /// 继续攒蓄力（蓄力音/粒子/音调上扬照旧）。开火时机由 ReleaseFireUpdatePatch 的状态机决定，
    /// 真正开枪时我们反过来调 item.Use，靠 InternalFire 标记放行。
    /// </summary>
    [HarmonyPatch(typeof(RangedWeapon), "Use", new Type[] { typeof(float), typeof(Character) })]
    internal static class ReleaseFireUsePatch
    {
        static bool Prefix(RangedWeapon __instance, Character __1)
        {
            if (__instance?.Item == null) { return true; }

            ReleaseFireState state;
            try { state = ReleaseFireData.GetOrCreate(__instance); }
            catch (Exception e)
            {
                ReleaseFireLog.Warn("开火判定出错，交还原生: " + e.Message);
                return true;
            }

            if (state.Params == null) { return true; }   // 不是松手开火武器
            if (state.InternalFire) { return true; }     // 我们自己发的那一枪

            // 引擎的输入分发已经保证"手持 + 按着开火键"；这里再按同一套规则要求瞄准
            if (__1 == null || __1.Removed || __1.IsDead) { return false; }
            if (__instance.Item.RequireAimToUse && !__1.IsKeyDown(InputType.Aim)) { return false; }

            ReleaseFireReflection.SetTryingToCharge(__instance);
            return false;
        }
    }

    /// <summary>
    /// RangedWeapon.Update 前缀里的状态机：
    ///   按着开火键（且瞄准、装填完毕、拿在手上）→ 累计蓄力；过临界开始按帧掷自爆（权威端）；
    ///   到硬顶 → 按满蓄力强制发射；松手 → 按蓄力比例发射；松瞄准/脱手/装填中 → 作废这次蓄力。
    /// 不改原生 Update 本身：蓄力音、粒子、音调、装填计时还是原生那套。
    /// </summary>
    [HarmonyPatch(typeof(RangedWeapon), "Update", new Type[] { typeof(float), typeof(Camera) })]
    internal static class ReleaseFireUpdatePatch
    {
        static void Prefix(RangedWeapon __instance, float __0)
        {
            ReleaseFireState state = ReleaseFireData.TryGet(__instance);   // 只查不建：没开过火的武器不必建状态
            if (state?.Params == null) { return; }

            try { Tick(state, __instance, __0); }
            catch (Exception e)
            {
                ReleaseFireLog.Warn("状态机出错（本次蓄力作废）: " + e.Message);
                state.Charging = false;
                state.ChargeTime = 0f;
            }
        }

        static void Tick(ReleaseFireState state, RangedWeapon weapon, float deltaTime)
        {
            Item item = weapon.Item;

            // 参数每帧重算：改装 tag 是运行期挂/摘的（XML 效果改 Item.tags，跨模组也认）
            ReleaseFireParams p = state.Effective;
            state.Effective.CopyFrom(state.Params);
            foreach (ReleaseFireOverride mod in state.Params.Overrides)
            {
                if (item.HasTag(mod.Tag)) { mod.Apply(state.Effective); }
            }

            Character holder = GetHolder(item);
            bool alive = holder != null && !holder.Removed && !holder.IsDead;
            bool aim = alive && (!item.RequireAimToUse || holder.IsKeyDown(InputType.Aim));
            bool shoot = alive && holder.IsKeyDown(InputType.Shoot);
            bool canCharge = alive && InHands(holder, item) && weapon.ReloadTimer <= 0f;

            if (state.Charging)
            {
                if (canCharge && aim && shoot)
                {
                    state.ChargeTime += deltaTime;

                    if (p.HardCap > 0f && state.ChargeTime >= p.HardCap)
                    {
                        // 硬顶：抢在玩家松手（或蓄到离谱）之前按满蓄力打出去
                        state.Charging = false;
                        state.ChargeTime = 0f;
                        Fire(state, weapon, item, holder, 1f, deltaTime);
                        return;
                    }
                    RollOvercharge(state, p, item, deltaTime);
                    return;
                }

                // 松手/松瞄准/装填中/脱手：该结算了
                bool release = canCharge && aim && !shoot;
                float ratio = p.ChargeTime > 0f ? Math.Min(state.ChargeTime / p.ChargeTime, 1f) : 1f;
                state.Charging = false;
                state.ChargeTime = 0f;
                if (release) { Fire(state, weapon, item, holder, ratio, deltaTime); }
                return;
            }

            if (canCharge && aim && shoot)
            {
                state.Charging = true;
                state.ChargeTime = 0f;
                if (ReleaseFireLog.Verbose && !state.ParamsLogged)
                {
                    state.ParamsLogged = true;
                    ReleaseFireLog.Log($"{item.Prefab.Identifier}: 开始蓄力（满蓄 {p.ChargeTime:0.##} 秒 / 硬顶 {p.HardCap:0.##} 秒）");
                }
            }
        }

        /// <summary>开火：临时改写伤害/穿甲/蓄力门槛，然后走完整的 item.Use（音效闪光弹药装填联机全归原生）。</summary>
        static void Fire(ReleaseFireState state, RangedWeapon weapon, Item item, Character user, float ratio, float deltaTime)
        {
            if (state.Effective.RequireAmmo && weapon.FindProjectile() == null) { return; }   // 没弹：取消，不放空炮

            state.InternalFire = true;
            try
            {
                float damage = Lerp(state.Effective.MinDamageMultiplier, state.Effective.MaxDamageMultiplier, ratio);
                float penetration = Lerp(state.Effective.MinPenetration, state.Effective.MaxPenetration, ratio);
                ReleaseFireReflection.SetDamageMultiplier(weapon, damage);
                ReleaseFireReflection.SetPenetration(weapon, penetration);
                ReleaseFireReflection.SetMaxChargeTime(weapon, 0f);   // 拆掉原生蓄力门槛，让 Use 立刻打
                item.Use(deltaTime, user);
                if (ReleaseFireLog.Verbose)
                {
                    ReleaseFireLog.Log($"{item.Prefab.Identifier}: 松手开火（蓄力 {ratio * 100f:0}%，伤害 ×{damage:0.##}，穿甲 {penetration:0.##}）");
                }
            }
            catch (Exception e)
            {
                ReleaseFireLog.Warn("开火出错: " + e.Message);
            }
            finally
            {
                ReleaseFireReflection.SetDamageMultiplier(weapon, state.RestDamageMultiplier);
                ReleaseFireReflection.SetPenetration(weapon, state.RestPenetration);
                ReleaseFireReflection.SetMaxChargeTime(weapon, state.RestMaxChargeTime);
                state.InternalFire = false;
            }
        }

        /// <summary>过蓄：超过临界后按帧掷骰，拖得越久越容易炸。掷骰只在权威端（客户端跟着 Condition 同步走）。</summary>
        static void RollOvercharge(ReleaseFireState state, ReleaseFireParams p, Item item, float deltaTime)
        {
            if (p.CritTime <= 0f || state.ChargeTime <= p.CritTime) { return; }
            if (!IsAuthority) { return; }

            float chancePerSecond = Clamp(
                p.ExplodeChance + p.ExplodeGrowth * (state.ChargeTime - p.CritTime),
                0f, p.ExplodeChanceMax) * p.ExplodeChanceMultiplier;
            if (Rand.Value() >= chancePerSecond * deltaTime) { return; }

            state.Charging = false;
            state.ChargeTime = 0f;
            Explode(item);
        }

        /// <summary>
        /// 过蓄自爆：Condition 清零触发 XML 的 OnBroken（1 秒后修回原耐久）。条件变化会自动同步给客户端
        /// （Item.SetCondition → 客户端执行 OnBroken），联机能看到爆炸画面和伤害。
        /// </summary>
        static void Explode(Item item)
        {
            if (item == null || item.Removed) { return; }
            float prevCondition = item.Condition;
            item.Condition = 0f;
            CoroutineManager.Invoke(() =>
            {
                if (item != null && !item.Removed) { item.Condition = prevCondition; }
            }, 1f);
            if (ReleaseFireLog.Verbose)
            {
                ReleaseFireLog.Log($"{item.Prefab.Identifier}: 过蓄自爆（1 秒后恢复耐久 {prevCondition:0.#}）");
            }
        }

        static bool IsAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

        static Character GetHolder(Item item)
        {
            Inventory inv = item?.ParentInventory;
            if (inv?.Owner is Character character) { return character; }
            return null;
        }

        /// <summary>引擎的 HeldItems 同款判定：右手/左手拿着才算（放物品栏其他栏位不算）。</summary>
        static bool InHands(Character character, Item item)
        {
            CharacterInventory inv = character?.Inventory;
            if (inv == null) { return false; }
            Item right = inv.GetItemInLimbSlot(InvSlotType.RightHand);
            if (right != null && right.ID == item.ID) { return true; }
            Item left = inv.GetItemInLimbSlot(InvSlotType.LeftHand);
            return left != null && left.ID == item.ID;
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static float Clamp(float x, float lo, float hi)
        {
            if (x < lo) { return lo; }
            if (x > hi) { return hi; }
            return x;
        }
    }

    /// <summary>
    /// 引擎里 private 的成员，编译期写不了，只能反射（既有先例：AmmoStorage.cs 写 RangedWeapon.ReloadTimer、
    /// ChargedSpellCard.cs 写 Turret.currentChargeTime）。拿不到就退化成原行为，只警告一次。
    /// </summary>
    internal static class ReleaseFireReflection
    {
        static readonly FieldInfo tryingToChargeField =
            typeof(RangedWeapon).GetField("tryingToCharge", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo setDamageMultiplier =
            typeof(RangedWeapon).GetProperty("WeaponDamageModifier")?.GetSetMethod(nonPublic: true);
        static readonly MethodInfo setPenetration =
            typeof(RangedWeapon).GetProperty("Penetration")?.GetSetMethod(nonPublic: true);
        static readonly MethodInfo setMaxChargeTime =
            typeof(RangedWeapon).GetProperty("MaxChargeTime")?.GetSetMethod(nonPublic: true);
        static bool warned;

        /// <summary>让原生 Update 把这一帧算成"正在蓄力"（蓄力音/粒子/音调上扬都靠它）。</summary>
        public static void SetTryingToCharge(RangedWeapon weapon)
        {
            if (tryingToChargeField == null)
            {
                WarnOnce("找不到 RangedWeapon.tryingToCharge（引擎改版？）：蓄力音/粒子不再随蓄力走，开火本身不受影响");
                return;
            }
            try { tryingToChargeField.SetValue(weapon, true); }
            catch (Exception e) { WarnOnce("写 tryingToCharge 失败: " + e.Message); }
        }

        public static void SetDamageMultiplier(RangedWeapon weapon, float value) => Set(weapon, setDamageMultiplier, value, "WeaponDamageModifier");
        public static void SetPenetration(RangedWeapon weapon, float value) => Set(weapon, setPenetration, value, "Penetration");
        public static void SetMaxChargeTime(RangedWeapon weapon, float value) => Set(weapon, setMaxChargeTime, value, "MaxChargeTime");

        static void Set(RangedWeapon weapon, MethodInfo setter, float value, string name)
        {
            if (setter == null)
            {
                WarnOnce($"找不到 RangedWeapon.{name} 的 setter（引擎改版？）：蓄力档位不会生效");
                return;
            }
            try { setter.Invoke(weapon, new object[] { value }); }
            catch (Exception e) { WarnOnce($"写 {name} 失败: " + e.Message); }
        }

        static void WarnOnce(string msg)
        {
            if (warned) { return; }
            warned = true;
            ReleaseFireLog.Warn(msg);
        }
    }
}
