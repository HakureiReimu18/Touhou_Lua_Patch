using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace ItemAffixes
{
    public static class Helpers
    {
        public static string ToColorString(this Color c)
        {
            return $"{(int)c.R},{(int)c.G},{(int)c.B},{(int)c.A}";
        }

        public static bool TryReadAffixFromTags(Item item, out AffixDef def)
        {
            def = null;
            // 快速拒绝：一个词缀标记都不带的物品直接返回，不遍历 tag 集合
            if (string.IsNullOrEmpty(item.Tags) || !item.Tags.Contains(Mod.AFFIX_TAG_PREFIX)) return false;
            foreach (var tag in item.GetTags())
            {
                if (tag.Value.StartsWith(Mod.AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    var affixId = tag.Value.Substring(Mod.AFFIX_TAG_PREFIX.Length);
                    return Mod.AffixDefs.TryGetValue(affixId, out def);
                }
            }
            return false;
        }

        public static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int idx;
            while ((idx = s.IndexOf("‖")) >= 0)
            {
                int end = s.IndexOf("‖", idx + 1);
                if (end < 0) break;
                s = s.Remove(idx, end - idx + 1);
            }
            return s.Trim();
        }
    }

    /// <summary>
    /// 反射成员缓存：热路径（每帧/每击）不再走 Traverse 的字符串查找。
    /// Item/Launcher/statusEffectLists 等都定义在固定的基类或类型上，只需解析一次。
    /// </summary>
    public static class ReflectionCache
    {
        public static readonly Type ItemComponentType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.ItemComponent");
        public static readonly PropertyInfo ComponentItemProp =
            ItemComponentType?.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        public static readonly FieldInfo StatusEffectListsField =
            ItemComponentType?.GetField("statusEffectLists", BindingFlags.Public | BindingFlags.Instance);

        // Item 自身的效果列表：构造时由各组件列表合并而来，Item.ApplyStatusEffects（OnWearing 等
        // 物品级触发）只读这个列表——运行时往组件里注册的效果必须同步到这里才会被物品级路径执行
        public static readonly FieldInfo ItemStatusEffectListsField =
            typeof(Item).GetField("statusEffectLists", BindingFlags.NonPublic | BindingFlags.Instance);
        public static readonly FieldInfo ItemHasStatusEffectsField =
            typeof(Item).GetField("hasStatusEffectsOfType", BindingFlags.NonPublic | BindingFlags.Instance);

        public static readonly Type WearableType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.Wearable");
        public static readonly PropertyInfo WearableAllowedSlotsProp =
            WearableType?.GetProperty("AllowedSlots", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo WearableDamageModifiersProp =
            WearableType?.GetProperty("DamageModifiers", BindingFlags.Public | BindingFlags.Instance);

        public static readonly Type ProjectileType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.Projectile");
        public static readonly FieldInfo LauncherField =
            ProjectileType?.GetField("Launcher", BindingFlags.Public | BindingFlags.Instance);

        // 属性词条（射速/散布）用的组件属性：解析一次缓存，附魔/恢复时不再按名字反射
        public static readonly Type MeleeWeaponType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.MeleeWeapon");
        public static readonly Type RangedWeaponType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.RangedWeapon");
        public static readonly PropertyInfo MeleeWeaponReloadProp =
            MeleeWeaponType?.GetProperty("Reload", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo RangedWeaponReloadProp =
            RangedWeaponType?.GetProperty("Reload", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo RangedWeaponSpreadProp =
            RangedWeaponType?.GetProperty("Spread", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo RangedWeaponUnskilledSpreadProp =
            RangedWeaponType?.GetProperty("UnskilledSpread", BindingFlags.Public | BindingFlags.Instance);

        public static readonly Type AttackType = typeof(Item).Assembly.GetType("Barotrauma.Attack");
        public static readonly PropertyInfo AttackSourceItemProp =
            AttackType?.GetProperty("SourceItem", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo AttackDamageMultProp =
            AttackType?.GetProperty("DamageMultiplier", BindingFlags.Public | BindingFlags.Instance);

        public static readonly FieldInfo IntervalTimersField =
            typeof(StatusEffect).GetField("intervalTimers", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>取组件所属物品（基类属性对所有组件子类实例有效）</summary>
        public static Item GetItem(object component) => ComponentItemProp?.GetValue(component) as Item;
    }

#if CLIENT
    [HarmonyPatch(typeof(Item), "get_Name")]
    public static class ItemNamePatch
    {
        static void Postfix(Item __instance, ref string __result)
        {
            if (Mod.ItemAffixes.TryGetValue(__instance.ID, out var data))
            {
                if (!__result.StartsWith("["))
                {
                    string prefix = Mod.AffixDefs.TryGetValue(data.AffixId, out var d)
                        ? d.DisplayPrefix : data.NamePrefix;
                    __result = $"[{prefix}] {__result}";
                }
                return;
            }

            if (Helpers.TryReadAffixFromTags(__instance, out var affixDef))
            {
                if (!__result.StartsWith("["))
                    __result = $"[{affixDef.DisplayPrefix}] {__result}";
            }
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.GetHUDTexts))]
    public static class ItemHUDTextsPatch
    {
        static void Postfix(Item __instance, Character character, ref List<ColoredText> __result)
        {
            if (__result.Count == 0) return;

            if (Mod.ItemAffixes.TryGetValue(__instance.ID, out var data))
            {
                string prefix = Helpers.StripRichText(
                    Mod.AffixDefs.TryGetValue(data.AffixId, out var d) ? d.DisplayPrefix : data.NamePrefix);
                if (!__result[0].Text.StartsWith("["))
                    __result[0] = new ColoredText($"[{prefix}] {__result[0].Text}", data.DisplayColor, false, false);
                return;
            }

            if (Helpers.TryReadAffixFromTags(__instance, out var affixDef))
            {
                string prefix = Helpers.StripRichText(affixDef.DisplayPrefix);
                if (!__result[0].Text.StartsWith("["))
                    __result[0] = new ColoredText($"[{prefix}] {__result[0].Text}", affixDef.DisplayColor, false, false);
            }
        }
    }

    /// <summary>
    /// 物品栏 tooltip 追加词缀效果说明。
    /// 挂点：Inventory.SlotReference.GetTooltip（物品实例级静态方法，名称/描述都在此组装）。
    /// 注意：不能用 ToString() 拼接（会剥掉全部富文本颜色），必须用 NestedStr 取原始标记文本、
    /// 再用 RichString.Rich 重新构造让 ‖color‖ 标记正常解析。
    /// </summary>
    [HarmonyPatch]
    public static class ItemTooltipAffixPatch
    {
        static MethodBase TargetMethod()
        {
            var sr = typeof(Inventory).GetNestedType("SlotReference",
                BindingFlags.Public | BindingFlags.NonPublic);
            var m = sr?.GetMethod("GetTooltip", BindingFlags.NonPublic | BindingFlags.Static);
            if (m == null) Mod.Warning("SlotReference.GetTooltip NOT FOUND");
            return m;
        }

        static void Postfix(Item item, ref RichString __result)
        {
            if (item == null) return;

            AffixDef def = null;
            if (Mod.ItemAffixes.TryGetValue(item.ID, out var data))
                Mod.AffixDefs.TryGetValue(data.AffixId, out def);
            else if (!Helpers.TryReadAffixFromTags(item, out def))
                return;

            if (def == null) return;
            string desc = Mod.GetDescriptionFor(def, item);
            if (string.IsNullOrEmpty(desc)) return;

            string line = $"◆ [{def.DisplayPrefix}]：{desc}";

            string raw = __result.NestedStr?.ToString();
            if (string.IsNullOrEmpty(raw)) return;
            // 去重：Tooltip 会被多次构建且结果会被累积复用。
            // 必须直接查 NestedStr 原文——RichString.Contains 不是子串语义，去重会失效。
            if (raw.Contains(line)) return;
            __result = RichString.Rich(raw + "\n" + line, null);
        }
    }
#endif

    [HarmonyPatch]
    public static class ItemSavePatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(Item).GetMethod("Save", BindingFlags.Public | BindingFlags.Instance);
            if (m != null) Mod.Log($"Found Save method: {m}");
            else Mod.Warning("Save method NOT FOUND on Item");
            return m;
        }

        static void Postfix(Item __instance, XElement __result)
        {
            if (__result == null) return;

            if (Mod.ItemAffixes.TryGetValue(__instance.ID, out var data))
            {
                __result.SetAttributeValue("affixid", data.AffixId);
                return;
            }

            if (!string.IsNullOrEmpty(__instance.Tags))
            {
                foreach (var tag in __instance.Tags.Split(','))
                {
                    if (tag.StartsWith(Mod.AFFIX_TAG_PREFIX))
                    {
                        __result.SetAttributeValue("affixid", tag.Substring(Mod.AFFIX_TAG_PREFIX.Length));
                        return;
                    }
                }
            }
            // 注意：这里绝不能用桥接文件（affix_save.xml）按裸 ID 兜底——
            // 物品 ID 跨会话会漂移，曾因此把词缀写到无辜物品（肉桂皮/肉桂种子）的存档上。
        }
    }

    [HarmonyPatch]
    public static class ItemLoadPatch
    {
        static MethodBase TargetMethod()
        {
            foreach (var m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Load") continue;
                var p = m.GetParameters();
                if (p.Length == 4 && p[0].ParameterType == typeof(ContentXElement))
                    return m;
            }
            Mod.Warning("Item.Load method NOT FOUND");
            return null;
        }

        static void Postfix(Item __result, ContentXElement element)
        {
            if (__result == null) return;
            var affixAttr = element.GetAttribute("affixid");
            if (affixAttr == null || string.IsNullOrEmpty(affixAttr.Value)) return;

            ushort id = __result.ID;
            string affixId = affixAttr.Value;
            Mod.PendingAffixes[id] = affixId;

            if (Mod.AffixDefs.TryGetValue(affixId, out var def) && !Mod.ItemAffixes.ContainsKey(id))
            {
                Mod.ApplyAffix(__result, def);
            }
        }
    }

    [HarmonyPatch]
    public static class AffixEffectInjectionPatch
    {
        static MethodBase TargetMethod()
        {
            var type = typeof(Item).Assembly.GetType("Barotrauma.Items.Components.ItemComponent");
            if (type == null)
            {
                Mod.Warning("AffixEffectInjectionPatch: ItemComponent type NOT FOUND");
                return null;
            }

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (m.Name != "ApplyStatusEffects") continue;
                var p = m.GetParameters();
                if (p.Length >= 2 && p[0].ParameterType == typeof(ActionType) && p[1].ParameterType == typeof(float))
                    return m;
            }

            Mod.Warning("AffixEffectInjectionPatch: ApplyStatusEffects NOT FOUND on ItemComponent");
            return null;
        }

        /// <summary>锈蚀/节能：OnUse 前快照内容物耐久，Postfix 里按实际消耗量百分比增减</summary>
        static void Prefix(object __instance, object[] __args, out Dictionary<ushort, float> __state)
        {
            __state = null;
            if (!Mod.AnyFuelMultAffixes) return; // 全局快速退出
            if (__args == null || __args.Length < 2) return;
            if (__args[0] is not ActionType at || at != ActionType.OnUse) return;

            var item = ReflectionCache.GetItem(__instance);
            if (item == null || !Helpers.TryReadAffixFromTags(item, out var def)) return;
            if (Math.Abs(def.FuelConsumeMult - 1f) < 0.0001f) return;
            if (item.ContainedItems == null) return;

            __state = new Dictionary<ushort, float>();
            foreach (var c in item.ContainedItems)
            {
                if (c != null) __state[c.ID] = c.Condition;
            }
        }

        static void Postfix(object __instance, object[] __args, Dictionary<ushort, float> __state)
        {
            if (__args == null || __args.Length < 2) return;
            var type = (ActionType)__args[0];
            float deltaTime = (float)__args[1];
            Character character = __args.Length > 2 ? __args[2] as Character : null;
            Limb limb = __args.Length > 3 ? __args[3] as Limb : null;
            Entity useTarget = __args.Length > 4 ? __args[4] as Entity : null;
            Character user = __args.Length > 5 ? __args[5] as Character : null;

            var item = ReflectionCache.GetItem(__instance);
            if (item == null) return;

            Item affixSource = item;
            bool viaProjectile = false;
            if (!Helpers.TryReadAffixFromTags(item, out var def))
            {
                // 投射物自身没有词缀：从发射武器读取，让枪械也能触发命中特效（OnImpact）
                if (type != ActionType.OnImpact) return;
                var launcher = GetProjectileLauncher(item);
                if (launcher == null || !Helpers.TryReadAffixFromTags(launcher, out def)) return;
                affixSource = launcher;
                viaProjectile = true;
            }

            // 耗材百分比调整（锈蚀/节能），词缀无 StatusEffect 也要执行
            if (!viaProjectile) ApplyFuelConsumeMult(item, def, __state);

            if (def.Effects == null || def.Effects.Count == 0) return;

            for (int i = 0; i < def.Effects.Count; i++)
            {
                var effect = def.Effects[i];
                if (effect.type != type) continue;
                // 已注册进组件列表的效果由引擎正常触发，这里跳过避免双倍生效
                if (!viaProjectile && IsEffectRegistered(__instance, type, effect)) continue;

                // 补丁触发的命中特效要求实际命中了角色：
                // 开枪未命中/打墙（useTarget 为空或非角色）不触发、不消耗冷却、不触发吸血
                if (useTarget is not Character && limb == null) continue;

                // 带 interval 的命中特效未注册进组件（见 Mod.RegisterEffectsForDisplay），
                // 由补丁按真实秒数管理冷却——引擎的 interval 按命中次数递减，高射速武器会更快烧完
                if (effect.Interval > 0f)
                {
                    var key = (affixSource.ID, i);
                    if (ProcTimers.TryGetValue(key, out double nextAllowed) && Timing.TotalTime < nextAllowed) continue;
                    ProcTimers[key] = Timing.TotalTime + effect.Interval;
                    // 中和引擎自己的按次冷却，避免双重门控
                    (ReflectionCache.IntervalTimersField?.GetValue(effect) as Dictionary<Entity, float>)
                        ?.Remove(affixSource);
                }

                if (user != null) effect.SetUser(user);
                // 与引擎一致：affliction 随攻击倍率缩放（含我们的伤害词缀），用后复位
                float attackMultiplier = __args.Length > 7 ? (float)__args[7] : 1.0f;
                // 与引擎一致的使用者转换：目标为 Character（不含 UseTarget）时解析为使用者/穿戴者
                var c = character;
                if (user != null &&
                    effect.HasTargetType(StatusEffect.TargetType.Character) &&
                    !effect.HasTargetType(StatusEffect.TargetType.UseTarget))
                {
                    c = user;
                }
                // 始终以词缀持有者为上下文调用：投射物路径的冷却记在开火的枪上，而不是每颗子弹各算各的
                effect.AttackMultiplier = attackMultiplier;
                affixSource.ApplyStatusEffect(effect, type, deltaTime,
                    c, limb, useTarget, isNetworkEvent: false, checkCondition: false);
                effect.AttackMultiplier = 1.0f;
            }
        }

        /// <summary>命中特效的真实时间冷却：键 = (词缀物品ID, 效果序号)，值 = 下次可触发时刻</summary>
        static readonly Dictionary<(ushort ItemId, int EffectIndex), double> ProcTimers = new();

        public static void ClearProcTimers() => ProcTimers.Clear();

        static void ApplyFuelConsumeMult(Item item, AffixDef def, Dictionary<ushort, float> before)
        {
            if (before == null || Math.Abs(def.FuelConsumeMult - 1f) < 0.0001f) return;
            if (item.ContainedItems == null) return;
            foreach (var c in item.ContainedItems)
            {
                if (c == null || !before.TryGetValue(c.ID, out float oldCond)) continue;
                float consumed = oldCond - c.Condition;
                if (consumed <= 0f) continue;
                // mult=1.2 → 额外扣除消耗量的 20%；mult=0.8 → 返还 20%
                c.Condition = Math.Min(c.MaxCondition, c.Condition - consumed * (def.FuelConsumeMult - 1f));
            }
        }

        public static bool IsEffectRegistered(object component, ActionType type, StatusEffect effect)
        {
            if (ReflectionCache.StatusEffectListsField?.GetValue(component) is not
                Dictionary<ActionType, List<StatusEffect>> lists) return false;
            return lists.TryGetValue(type, out var list) && list.Contains(effect);
        }

        static Item GetProjectileLauncher(Item item)
        {
            if (item.Components == null) return null;
            foreach (var c in item.Components)
            {
                if (c == null || c.GetType() != ReflectionCache.ProjectileType) continue;
                return ReflectionCache.LauncherField?.GetValue(c) as Item;
            }
            return null;
        }
    }

    /// <summary>
    /// 枪械伤害词条：在投射物发射时按发射武器的词缀乘算 damageMultiplier。
    /// </summary>
    [HarmonyPatch]
    public static class ProjectileShootPatch
    {
        static MethodBase TargetMethod()
        {
            var m = ReflectionCache.ProjectileType?.GetMethod("Shoot", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) Mod.Warning("ProjectileShootPatch: Projectile.Shoot NOT FOUND");
            return m;
        }

        static void Prefix(object __instance, ref float damageMultiplier)
        {
            if (!Mod.AnyDamageMultAffixes) return; // 全局快速退出
            var launcher = ReflectionCache.LauncherField?.GetValue(__instance) as Item;
            if (launcher == null) return;
            if (!Helpers.TryReadAffixFromTags(launcher, out var def)) return;
            if (Math.Abs(def.DamageMult - 1f) < 0.0001f) return;
            damageMultiplier *= def.DamageMult;
        }
    }

    /// <summary>
    /// 攻击伤害词条：攻击方按 SourceItem 词缀乘算，防御方按目标角色穿戴（非手持）的词缀护甲乘算。
    /// Attack.DamageMultiplier 每击由 MeleeWeapon 重设，Prefix 里乘算不会累积。
    /// DoDamage 与 DoDamageToLimb 互不嵌套，分别补丁不会双倍生效；子弹命中也走这两个入口。
    /// </summary>
    [HarmonyPatch]
    public static class MeleeDamagePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            if (ReflectionCache.AttackType == null)
            {
                Mod.Warning("MeleeDamagePatch: Attack type NOT FOUND");
                yield break;
            }
            foreach (var name in new[] { "DoDamage", "DoDamageToLimb" })
            {
                var m = ReflectionCache.AttackType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                if (m == null) Mod.Warning($"MeleeDamagePatch: {name} NOT FOUND on Attack");
                else yield return m;
            }
        }

        static void Prefix(object __instance, object[] __args)
        {
            if (!Mod.AnyDamageMultAffixes && !Mod.AnyDamageTakenAffixes) return; // 全局快速退出
            float mult = 1f;

            // 攻击端：武器词缀伤害倍率
            if (Mod.AnyDamageMultAffixes)
            {
                var sourceItem = ReflectionCache.AttackSourceItemProp?.GetValue(__instance) as Item;
                if (sourceItem != null &&
                    Helpers.TryReadAffixFromTags(sourceItem, out var atkDef) &&
                    Math.Abs(atkDef.DamageMult - 1f) > 0.0001f)
                {
                    mult *= atkDef.DamageMult;
                }
            }

            // 防御端：目标角色穿戴的词缀护甲（DoDamage 的 target / DoDamageToLimb 的 targetLimb 都是 args[1]）
            if (Mod.AnyDamageTakenAffixes && __args != null && __args.Length > 1)
            {
                Character victim = __args[1] as Character ?? (__args[1] as Limb)?.character;
                if (victim != null) mult *= GetWornDamageTakenMult(victim);
            }

            if (Math.Abs(mult - 1f) < 0.0001f) return;
            var prop = ReflectionCache.AttackDamageMultProp;
            if (prop == null) return;
            prop.SetValue(__instance, (float)prop.GetValue(__instance) * mult);
        }

        static float GetWornDamageTakenMult(Character c)
        {
            if (c?.Inventory is not CharacterInventory inv) return 1f;
            float mult = 1f;
            // 直接按槽位迭代：O(槽位数) 且无嵌套扫描、无集合分配
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                var slotType = inv.SlotTypes[i];
                // 只算穿在装备槽里的，手持/背包格不算
                if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                    slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                {
                    continue;
                }
                var item = inv.GetItemAt(i);
                if (item == null) continue;
                if (Helpers.TryReadAffixFromTags(item, out var def) &&
                    Math.Abs(def.DamageTakenMult - 1f) > 0.0001f)
                {
                    mult *= def.DamageTakenMult;
                }
            }
            return mult;
        }
    }

    /// <summary>
    /// 巧匠词条：RepairTool 修理时逐帧触发私有的 ApplyStatusEffectsOnTarget（OnSuccess，deltaTime 缩放），
    /// 在这里按目标最大耐久追加恢复，实现真百分比（与设备耐久上限无关）。
    /// </summary>
    [HarmonyPatch]
    public static class RepairBonusPatch
    {
        static MethodBase TargetMethod()
        {
            var type = typeof(Item).Assembly.GetType("Barotrauma.Items.Components.RepairTool");
            if (type == null)
            {
                Mod.Warning("RepairBonusPatch: RepairTool type NOT FOUND");
                return null;
            }
            var m = type.GetMethod("ApplyStatusEffectsOnTarget", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) Mod.Warning("RepairBonusPatch: ApplyStatusEffectsOnTarget NOT FOUND on RepairTool");
            return m;
        }

        static void Postfix(object __instance, object[] __args)
        {
            if (!Mod.AnyRepairBonusAffixes) return; // 全局快速退出
            if (__args == null || __args.Length < 4) return;
            if (__args[2] is not ActionType at || at != ActionType.OnSuccess) return;
            if (__args[3] is not Item targetItem) return;

            var tool = ReflectionCache.GetItem(__instance);
            if (tool == null || !Helpers.TryReadAffixFromTags(tool, out var def)) return;
            if (def.RepairBonusPercent <= 0f) return;

            float deltaTime = (float)__args[1];
            targetItem.Condition = Math.Min(targetItem.MaxCondition,
                targetItem.Condition + targetItem.MaxCondition * def.RepairBonusPercent * deltaTime);
        }
    }

    /// <summary>
    /// 主线程延迟任务调度：每帧检查一次 Mod 的任务队列（空队列时只有一次 Count 判断的开销）。
    /// 替代 Task.Delay 的线程池回调——游戏状态不是线程安全的，恢复词缀必须在主线程跑。
    /// </summary>
    [HarmonyPatch]
    public static class MainThreadSchedulerPatch
    {
        static MethodBase TargetMethod()
        {
            // 双端程序集的 GameMain 不同：客户端有 Update(GameTime)，服务端只有 Run() 主循环，
            // 服务端上下文的每帧入口是 GameServer.Update(float)。
            // 注意必须带 NonPublic：Update 在 XNA 里是 protected override，公开化程序集里是 public，
            // 离线编译能找到、运行时按 Public 找会返回 null 导致 Harmony 打补丁失败
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var m = typeof(GameMain).GetMethod("Update", flags);
            if (m != null) return m;
            var serverType = typeof(GameMain).Assembly.GetType("Barotrauma.Networking.GameServer");
            m = serverType?.GetMethod("Update", flags);
            if (m == null) Mod.Warning("MainThreadSchedulerPatch: no per-frame Update method found");
            return m;
        }

        static void Postfix() => Mod.RunMainThreadScheduled();
    }

    [HarmonyPatch]
    public static class EnchantingStationPatch
    {
        static MethodBase TargetMethod()
        {
            var type = typeof(Item).Assembly.GetType("Barotrauma.Items.Components.Deconstructor");
            if (type == null)
            {
                Mod.Warning("EnchantingStation: Deconstructor type NOT FOUND in assembly");
                return null;
            }
            Mod.Log($"EnchantingStation: found Deconstructor type: {type.FullName}");

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (m.Name != "ProcessItem") continue;
                var p = m.GetParameters();
                if (p.Length >= 1 && p[0].ParameterType == typeof(Item))
                {
                    Mod.Log($"EnchantingStation: PATCHING ProcessItem ({p.Length} params)");
                    return m;
                }
            }

            Mod.Warning("EnchantingStation: ProcessItem NOT FOUND on Deconstructor");
            return null;
        }

        static bool Prefix(object __instance, Item targetItem)
        {
            if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient) return true;
            if (targetItem == null) return true;

            // 关键身份校验：这个补丁挂在 Deconstructor.ProcessItem 上，对所有解构仪生效。
            // 没有这道校验时，任何通电的解构仪（包括编辑器测试模式）都会把
            // "可附魔物品 + 词缀材料"的存放组合误认为附魔操作（肉桂事件）。
            var station = ReflectionCache.GetItem(__instance);
            if (station == null || !station.HasTag("enchantingstation")) return true;

            var inputInventory = targetItem.ParentInventory;
            if (inputInventory == null) return true;

            var items = inputInventory.AllItems.ToList();
            var result = Mod.TryGetEnchantingTarget(items, out var weapon, out var material);
            if (result == null)
            {
                // 服务器上这条日志用于区分"补丁没跑"（完全无输出）和"判定没通过"（有输出但正常分解）
                Mod.Log($"EnchantingStation: no weapon+material combo in {station.Name} (items: {string.Join(", ", items.Select(i => i?.Name ?? "null"))}), normal deconstruct");
                return true;
            }

            var affix = Mod.PickAffixByWeight(result.Value.weights, weapon, weapon.ID ^ Environment.TickCount);
            if (affix == null)
            {
                Mod.Log($"EnchantingStation: no applicable affix for {weapon.Name}");
                return true;
            }

            Mod.ApplyAffix(weapon, affix);
            Mod.BroadcastAffixApplied(weapon, affix);

            inputInventory.RemoveItem(weapon);

            var outputContainer = Traverse.Create(__instance).Field("outputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
            if (outputContainer != null && outputContainer.Inventory.TryPutItem(weapon, null))
            {
                Mod.Log($"EnchantingStation: output {weapon.Name} with [{affix.Identifier}]");
            }
            else
            {
                inputInventory.TryPutItem(weapon, null);
                Mod.Log($"EnchantingStation: output full, enchantment cancelled for {weapon.Name}");
                return false;
            }

            inputInventory.RemoveItem(material);
            Entity.Spawner?.AddItemToRemoveQueue(material);

            return false;
        }
    }
}
