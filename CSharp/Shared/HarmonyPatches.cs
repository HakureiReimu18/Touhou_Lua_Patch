using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Touhou.Affixes
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
            // GetTags() 零分配直扫（item.Tags 的 getter 每次都在拼字符串，显示热路径上禁用）
            List<AffixDef> defs = null;
            foreach (var tag in item.GetTags())
            {
                if (tag.Value.StartsWith(Mod.AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                {
                    var affixId = tag.Value.Substring(Mod.AFFIX_TAG_PREFIX.Length);
                    if (Mod.AffixDefs.TryGetValue(affixId, out var d))
                        (defs ??= new List<AffixDef>(2)).Add(d);
                }
            }
            if (defs == null) return false;
            // 多词缀现场拼一个；反正只在内存表恢复前那阵子走这路，不用缓存
            def = defs.Count == 1 ? defs[0] : Mod.ComposeAffixDef(defs, item);
            return true;
        }

        /// 词缀读取统一入口：内存表优先（校验 prefab 身份，防 ID 复用后漂到别的物件上），
        /// 标签只是兜底——会被 CustomInterface 的 setvalue tags 整串盖掉，运行时以内存表为准
        public static bool TryGetAffix(Item item, out AffixDef def)
        {
            def = null;
            if (Mod.TryGetAffixData(item, out var data))
            {
                def = Mod.GetEffectiveDef(data);
                if (def != null) return true;
            }
            return TryReadAffixFromTags(item, out def);
        }
        
        public static string BracketedRichPrefix(string prefix)
        {
            const string colorEnd = "‖color:end‖";
            if (!string.IsNullOrEmpty(prefix) && prefix.StartsWith("‖color:"))
            {
                int openEnd = prefix.IndexOf('‖', 1);
                int closeStart = prefix.IndexOf(colorEnd, StringComparison.OrdinalIgnoreCase);
                if (openEnd > 0 && closeStart > openEnd)
                {
                    return prefix.Substring(0, openEnd + 1)
                        + "[" + prefix.Substring(openEnd + 1, closeStart - openEnd - 1) + "]"
                        + prefix.Substring(closeStart);
                }
            }
            return "[" + prefix + "]";
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

        /// 余烬：耐久越低伤害越高——100% 无加成，25% 及以下吃满。返回伤害倍率
        public static float LowConditionMult(Item weapon, float maxMult)
        {
            if (weapon == null || maxMult <= 1f) return 1f;
            float max = weapon.MaxCondition;
            if (max <= 0f) return 1f;
            float frac = Math.Min(1f, Math.Max(0f, weapon.Condition / max));
            const float fullAt = 0.25f;
            float t = Math.Min(1f, Math.Max(0f, (1f - frac) / (1f - fullAt)));
            return 1f + (maxMult - 1f) * t;
        }
    }

    /// 反射成员缓存：这些成员都定在固定类型上，解析一次就够，热路径不走字符串查找
    public static class ReflectionCache
    {
        public static readonly Type ItemComponentType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.ItemComponent");
        public static readonly PropertyInfo ComponentItemProp =
            ItemComponentType?.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
        /// 编译版 getter：ApplyStatusEffects 注入是每帧×每组件的热路径，每回都要拿组件所属物品，
        /// 反射 GetValue 太贵，表达式树编译一次后接近直接调
        public static readonly Func<object, Item> ComponentItemGetter = BuildComponentItemGetter();

        static Func<object, Item> BuildComponentItemGetter()
        {
            if (ComponentItemProp == null) return null;
            try
            {
                var p = System.Linq.Expressions.Expression.Parameter(typeof(object), "c");
                return System.Linq.Expressions.Expression.Lambda<Func<object, Item>>(
                    System.Linq.Expressions.Expression.Convert(
                        System.Linq.Expressions.Expression.Property(
                            System.Linq.Expressions.Expression.Convert(p, ItemComponentType),
                            ComponentItemProp),
                        typeof(Item)),
                    p).Compile();
            }
            catch { return null; } // 编译失败就退回反射，功能一样
        }
        public static readonly FieldInfo StatusEffectListsField =
            ItemComponentType?.GetField("statusEffectLists", BindingFlags.Public | BindingFlags.Instance);

        // Item 自己的效果列表是构造时从各组件合并来的，ApplyStatusEffects 只读它，运行时注册的得同步过来
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

        // 射速/散布那批属性词条要用的，解析一次缓存住
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
        public static readonly PropertyInfo RangedWeaponMaxChargeTimeProp =
            RangedWeaponType?.GetProperty("MaxChargeTime", BindingFlags.Public | BindingFlags.Instance);

        public static readonly Type AttackType = typeof(Item).Assembly.GetType("Barotrauma.Attack");
        public static readonly PropertyInfo AttackSourceItemProp =
            AttackType?.GetProperty("SourceItem", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo AttackDamageMultProp =
            AttackType?.GetProperty("DamageMultiplier", BindingFlags.Public | BindingFlags.Instance);
        /// 直写 _damageMultiplier 绕开 setter 的坑：那玩意在字段为 null 时首次写入会连
        /// initialDamageMultiplier 基线一起改掉（见 MeleeDamagePatch.WriteDamageMult）
        public static readonly FieldInfo AttackDamageMultBackingField =
            AttackType?.GetField("_damageMultiplier", BindingFlags.NonPublic | BindingFlags.Instance);
        /// <summary>备用入口，目前没用到，先留着</summary>
        public static readonly MethodInfo AttackSetInitialDamageMultMethod =
            AttackType?.GetMethod("SetInitialDamageMultiplier", BindingFlags.Public | BindingFlags.Instance);

        // 巧匠两条路都要用：船体修复（实例乘算）、设备修理（GetStatValue(RepairSpeed) 加算，见 DeviceRepairPatch）
        public static readonly Type RepairToolType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.RepairTool");
        public static readonly PropertyInfo RepairToolStructureFixProp =
            RepairToolType?.GetProperty("StructureFixAmount", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo RepairToolLevelWallFixProp =
            RepairToolType?.GetProperty("LevelWallFixAmount", BindingFlags.Public | BindingFlags.Instance);

        /// <summary>RepairSpeed 枚举值缓存，双端都是装箱比较省得每次解析</summary>
        public static readonly Type StatTypesType = typeof(Item).Assembly.GetType("Barotrauma.StatTypes");
        public static readonly object RepairSpeedStat =
            StatTypesType == null ? null : Enum.Parse(StatTypesType, "RepairSpeed");

        // 王冠：LastProjectile → Projectile.Attack，命中才读倍率，弹道途中改来得及
        public static readonly PropertyInfo RangedWeaponLastProjectileProp =
            RangedWeaponType?.GetProperty("LastProjectile", BindingFlags.Public | BindingFlags.Instance);
        public static readonly PropertyInfo ProjectileAttackProp =
            ProjectileType?.GetProperty("Attack", BindingFlags.Public | BindingFlags.Instance);
        // 专一：Holdable 的 AllowedSlots 是 get-only，列表只能原地改
        public static readonly Type HoldableType =
            typeof(Item).Assembly.GetType("Barotrauma.Items.Components.Holdable");
        public static readonly PropertyInfo HoldableAllowedSlotsProp =
            HoldableType?.GetProperty("AllowedSlots", BindingFlags.Public | BindingFlags.Instance);

        public static readonly FieldInfo IntervalTimersField =
            typeof(StatusEffect).GetField("intervalTimers", BindingFlags.NonPublic | BindingFlags.Instance);

        // 冷静降噪：AITarget.SoundRange 的 setter 每帧×每个 AI 都跑，Entity 读取走编译版 getter
        public static readonly Type AITargetType =
            typeof(Character).Assembly.GetType("Barotrauma.AITarget");
        public static readonly PropertyInfo AITargetEntityProp =
            AITargetType?.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
        public static readonly Func<object, Entity> AITargetEntityGetter = BuildAITargetEntityGetter();

        static Func<object, Entity> BuildAITargetEntityGetter()
        {
            if (AITargetEntityProp == null) return null;
            try
            {
                var p = System.Linq.Expressions.Expression.Parameter(typeof(object), "t");
                return System.Linq.Expressions.Expression.Lambda<Func<object, Entity>>(
                    System.Linq.Expressions.Expression.Convert(
                        System.Linq.Expressions.Expression.Property(
                            System.Linq.Expressions.Expression.Convert(p, AITargetType),
                            AITargetEntityProp),
                        typeof(Entity)),
                    p).Compile();
            }
            catch { return null; } // 编译失败就退回反射，功能一样
        }

        /// <summary>拿 AITarget 的 Entity：优先编译版 getter，反射兜底</summary>
        public static Entity GetAITargetEntity(object aiTarget)
            => AITargetEntityGetter != null
                ? AITargetEntityGetter(aiTarget)
                : AITargetEntityProp?.GetValue(aiTarget) as Entity;

        /// <summary>拿组件所属物品：优先编译版 getter，反射兜底</summary>
        public static Item GetItem(object component)
            => ComponentItemGetter != null
                ? ComponentItemGetter(component)
                : ComponentItemProp?.GetValue(component) as Item;
    }

#if CLIENT
    [HarmonyPatch(typeof(Item), "get_Name")]
    public static class ItemNamePatch
    {
        // 游戏量拖拽标签背景宽度时连颜色标记一起算，带标记的名字会撑出长空白，拖拽中就返回纯文本名
        static readonly FieldInfo DraggingItemsField = typeof(Inventory).GetField("DraggingItems",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        // get_Name 每帧都在调，顺手编译个 getter
        static readonly Func<List<Item>> DraggingItemsGetter = BuildDraggingItemsGetter();

        static Func<List<Item>> BuildDraggingItemsGetter()
        {
            if (DraggingItemsField == null) return null;
            try
            {
                return System.Linq.Expressions.Expression.Lambda<Func<List<Item>>>(
                    System.Linq.Expressions.Expression.Convert(
                        System.Linq.Expressions.Expression.Field(null, DraggingItemsField),
                        typeof(List<Item>))).Compile();
            }
            catch { return null; } // 编译失败就退回反射，功能一样
        }

        static bool IsDragging(Item item)
        {
            try
            {
                var l = DraggingItemsGetter != null ? DraggingItemsGetter() : DraggingItemsField?.GetValue(null) as List<Item>;
                return l != null && l.Contains(item);
            }
            catch { return false; }
        }

        static void Postfix(Item __instance, ref string __result)
        {
            // 奇迹的整组前缀 AffixData 里早拼好了，单词缀才现拼
            string richPrefix, plainPrefix;
            if (Mod.TryGetAffixData(__instance, out var data) && data.PlainPrefixDisplay != null)
            {
                richPrefix = data.RichPrefixDisplay;
                plainPrefix = data.PlainPrefixDisplay;
            }
            else
            {
                string prefix;
                if (data != null)
                    prefix = Mod.AffixDefs.TryGetValue(data.AffixId, out var d) ? d.DisplayPrefix : data.NamePrefix;
                else if (Helpers.TryReadAffixFromTags(__instance, out var affixDef))
                    prefix = affixDef.DisplayPrefix;
                else
                    return;
                plainPrefix = $"[{Helpers.StripRichText(prefix)}]";
                richPrefix = Helpers.BracketedRichPrefix(prefix);
            }

            bool already = Helpers.StripRichText(__result).StartsWith(plainPrefix);

            if (IsDragging(__instance))
            {
                string plainName = Helpers.StripRichText(__result);
                __result = already ? plainName : $"{plainPrefix} {plainName}";
                return;
            }

            // 防重复靠剥掉颜色后的纯文本比对
            if (!already)
                __result = $"{richPrefix} {__result}";
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.GetHUDTexts))]
    public static class ItemHUDTextsPatch
    {
        static void Postfix(Item __instance, Character character, ref List<ColoredText> __result)
        {
            if (__result.Count == 0) return;

            if (Mod.TryGetAffixData(__instance, out var data))
            {
                // 多词缀（奇迹）时用预拼的整组括号前缀，颜色取全组最高档
                string prefix = data.PlainPrefixDisplay
                    ?? $"[{Helpers.StripRichText(Mod.AffixDefs.TryGetValue(data.AffixId, out var d) ? d.DisplayPrefix : data.NamePrefix)}]";
                if (!__result[0].Text.StartsWith(prefix))
                    __result[0] = new ColoredText($"{prefix} {__result[0].Text}", data.DisplayColor, false, false);
                return;
            }

            if (Helpers.TryReadAffixFromTags(__instance, out var affixDef))
            {
                string prefix = $"[{Helpers.StripRichText(affixDef.DisplayPrefix)}]";
                if (!__result[0].Text.StartsWith(prefix))
                    __result[0] = new ColoredText($"{prefix} {__result[0].Text}", affixDef.DisplayColor, false, false);
            }
        }
    }

    /// 物品栏 tooltip 追加词缀说明，奇迹按组成词缀逐条列。
    /// 别用 ToString() 拼——颜色标记全被剥掉，得从 NestedStr 拿原文用 RichString.Rich 重建
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

            // 标签兜底拿到的只有合成词缀、desc 是空的，自己会被跳过
            List<AffixDef> defs = null;
            if (Mod.TryGetAffixData(item, out var data))
            {
                foreach (var id in Mod.GetAllAffixIds(data))
                {
                    if (Mod.AffixDefs.TryGetValue(id, out var d))
                        (defs ??= new List<AffixDef>(3)).Add(d);
                }
            }
            else if (Helpers.TryGetAffix(item, out var single))
            {
                defs = new List<AffixDef>(1) { single };
            }
            if (defs == null || defs.Count == 0) return;

            string raw = __result.NestedStr?.ToString();
            if (string.IsNullOrEmpty(raw)) return;

            var sb = new System.Text.StringBuilder(raw);
            bool appended = false;
            foreach (var def in defs)
            {
                string desc = Mod.GetDescriptionFor(def, item);
                if (string.IsNullOrEmpty(desc)) continue;
                string line = $"◆ {Helpers.BracketedRichPrefix(def.DisplayPrefix)}：{desc}";
                // tooltip 会重复构建，查重只能搜 NestedStr 原文：RichString.Contains 不是子串语义
                if (raw.Contains(line)) continue;
                sb.Append('\n').Append(line);
                appended = true;
            }

            // 耐久 ≤20% 词缀休眠是刻意设计，红字说出来免得玩家以为词缀坏了
            if (Mod.IsMedicalItem(item) && item.MaxCondition > 0f &&
                item.Condition / item.MaxCondition <= Mod.MedicalMinConditionPercent)
            {
                string dormant = TextManager.Get("affix.dormantline")
                    .Fallback("（耐久不足，词缀效果休眠）", true).ToString();
                if (!raw.Contains(dormant))
                {
                    sb.Append('\n').Append("‖color:255,80,80,255‖").Append(dormant).Append("‖color:end‖");
                    appended = true;
                }
            }

            if (appended) __result = RichString.Rich(sb.ToString(), null);
        }
    }
#endif

    [HarmonyPatch]
    public static class ItemSavePatch
    {
        /// <summary>诊断计数：Save 时实际写出 affixid 的次数（affixdiag 展示）</summary>
        public static int SavedAffixCount;

        static MethodBase TargetMethod()
        {
            // 不用 GetMethod("Save")：某些版本存在同名重载会抛 AmbiguousMatchException，
            // 被 per-type try/catch 吞掉后补丁静默失效——存档写不出 affixid 的直接原因。
            // 改为枚举所有同名方法、精确匹配单参 XElement 的那个
            MethodBase m = null;
            foreach (var candidate in typeof(Item).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.Name != "Save") continue;
                var p = candidate.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(XElement)) { m = candidate; break; }
            }
            if (m != null)
            {
                Mod.LogOnce($"ItemSavePatch: patched {m}");
                return m;
            }
            // 找不到也绝不能返回 null（Harmony 会抛异常中断该类注册）：补哑方法，功能退化但其余补丁不受影响
            Mod.Warning("ItemSavePatch: Item.Save(XElement) NOT FOUND, using dummy target");
            return typeof(ItemSavePatch).GetMethod(nameof(DummyTarget),
                BindingFlags.NonPublic | BindingFlags.Static);
        }

        static void DummyTarget() { }

        static void Postfix(Item __instance, XElement __result)
        {
            if (__result == null) return;

            if (Mod.TryGetAffixData(__instance, out var data))
            {
                __result.SetAttributeValue("affixid", Mod.JoinAffixIds(data));
                // affixuid 跟存档走，跨会话凭它区分同 prefab 的另一件
                if (data.Uid != null) __result.SetAttributeValue("affixuid", data.Uid);
                SavedAffixCount++; // 计数供 affixdiag 查询；不再逐件刷日志（存档时每件一条太吵）
                return;
            }

            // 会话表兜底（prefab 双因子校验）：巡回边界内存表已清空、tags 又被 tickbox 擦除时，
            // affixid 仍写得出来——这是"摆脱 tag 依赖"的关键一层
            if (Mod.TryGetRememberedAffix(__instance, out var remembered))
            {
                __result.SetAttributeValue("affixid", remembered.AffixId);
                if (remembered.Uid != null) __result.SetAttributeValue("affixuid", remembered.Uid);
                SavedAffixCount++;
                return;
            }

            if (!string.IsNullOrEmpty(__instance.Tags))
            {
                List<string> ids = null;
                string uid = null;
                foreach (var tag in __instance.Tags.Split(','))
                {
                    if (tag.StartsWith(Mod.AFFIX_TAG_PREFIX))
                        (ids ??= new List<string>(2)).Add(tag.Substring(Mod.AFFIX_TAG_PREFIX.Length));
                    else if (tag.StartsWith(Mod.AFFIX_UID_TAG_PREFIX)) uid = tag.Substring(Mod.AFFIX_UID_TAG_PREFIX.Length);
                }
                if (ids != null)
                {
                    __result.SetAttributeValue("affixid", string.Join(",", ids));
                    if (uid != null) __result.SetAttributeValue("affixuid", uid);
                    SavedAffixCount++;
                }
            }
            // 别图省事用桥接文件按裸 ID 兜底：物品 ID 跨会话会漂，之前把词缀写到肉桂皮存档上过
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
            string uid = element.GetAttribute("affixuid")?.Value;
            Mod.AddPendingAffix(id, new Mod.PendingAffix
            {
                AffixId = affixId,
                // 刚加载 prefab 就是它自己的，记下来给恢复路径做双因子校验
                PrefabId = __result.Prefab.Identifier.Value,
                Uid = uid
            });

            if (Mod.TryGetAffixData(__result, out _)) return;
            // 逗号连的是奇迹多词缀，逐个验过再整组上；uid 为空的老存档 SetAffixes 会补，每物一次
            var ids = new List<string>();
            foreach (var raw in affixId.Split(','))
            {
                string affixIdPart = raw.Trim();
                if (affixIdPart.Length > 0 && Mod.AffixDefs.ContainsKey(affixIdPart)) ids.Add(affixIdPart);
            }
            if (ids.Count > 0) Mod.SetAffixes(__result, ids, uid);
        }
    }

    /// <summary>
    /// 词缀标签防擦除（根治）：内容模组常用 setvalue tags 整串重写物品标签（开关/状态机，
    /// 且可能 ContinuousSignal 每帧触发），会把 __affix_/__affixuid_ 一起抹掉——
    /// 自愈只能事后补救，存档写入窗口仍可能丢词缀。直接在 Item.Tags 的 setter 上拦截：
    /// 新标签串缺本物品的词缀标签时自动补回，"标签被擦"这一整类问题从源头消失。
    /// 性能：无词缀快速退出；已附魔物品走 AffixData 上的缓存标签串/数组，
    /// steady state 只有几次短字符串 Contains，零分配（LastKnown/Pending 兜底路径才现拼）。
    /// 注意：移除词缀走 Item.RemoveTag（直接操作内部集合、不经过 setter），不会被本补丁挡住；
    /// 物品加载时应用存档标签同样经过 setter，但那时内存/会话表还没有记录，原样放行。
    /// </summary>
    [HarmonyPatch(typeof(Item), "set_Tags")]
    public static class AffixTagPreservePatch
    {
        /// <summary>诊断计数：Prefix 实际执行次数 / 真实补写标签次数（affixdiag 命令展示）</summary>
        public static int FiredCount;
        public static int PreservedCount;
        public static string LastDetail;

        static void Prefix(Item __instance, ref string value)
        {
            // 无词缀快速退出（含待恢复索引：恢复完成前的窗口也要保）
            if (Mod.ItemAffixes.Count == 0 && Mod.LastKnownAffixes.Count == 0 && Mod.PendingByUid.Count == 0) return;
            FiredCount++;

            string joined;
            string[] parts;
            if (Mod.TryGetAffixData(__instance, out var data))
            {
                joined = data.AffixTagsJoined;
                parts = data.AffixTagParts;
                if (parts == null) // 旧数据/初次写入的兜底：补建缓存后再走快路径
                {
                    joined ??= Mod.BuildAffixTagsString(Mod.JoinAffixIds(data), data.Uid);
                    parts = joined.Split(',');
                    data.AffixTagsJoined = joined;
                    data.AffixTagParts = parts;
                }
            }
            else if (Mod.TryGetRememberedAffix(__instance, out var remembered))
            {
                joined = Mod.BuildAffixTagsString(remembered.AffixId, remembered.Uid);
                parts = joined.Split(',');
            }
            else if (Mod.PendingAffixes.TryGetValue(__instance.ID, out var pending)
                && pending.AffixId != null
                && (pending.PrefabId == null || pending.PrefabId == __instance.Prefab.Identifier.Value))
            {
                // 恢复完成前的短暂窗口（Item.Load 已登记待恢复条目）也保住标签
                joined = Mod.BuildAffixTagsString(pending.AffixId, pending.Uid);
                parts = joined.Split(',');
            }
            else if (Mod.ReadUidTag(__instance) is { } tagUid
                && Mod.PendingByUid.TryGetValue(tagUid, out var byUid)
                && (byUid.PrefabId == null || byUid.PrefabId == __instance.Prefab.Identifier.Value))
            {
                // 客户端竞态兜底（跨巡回 ID 漂移）：内存/会话表都没有，但物品身上还带着 uid 标签，
                // 用它去待恢复索引里取词缀清单——否则 TickBox 类组件会在恢复扫描前把标签擦光
                joined = Mod.BuildAffixTagsString(byUid.AffixId, byUid.Uid);
                parts = joined.Split(',');
            }
            else if (Mod.TryGetCurrentAffixIds(__instance, out var curIds) && curIds.Count > 0)
            {
                // 最后兜底：按物品当前的 __affix_ 标签"就地保全"（自身就是即将被覆盖的值）
                joined = Mod.BuildAffixTagsString(string.Join(",", curIds), Mod.ReadUidTag(__instance));
                parts = joined.Split(',');
            }
            else return;

            // 逐标签短串 Contains（标签名够独特，不会误匹配）；缺任何一个才重写
            bool allPresent = !string.IsNullOrEmpty(value);
            if (allPresent)
            {
                foreach (var t in parts)
                {
                    if (!value.Contains(t)) { allPresent = false; break; }
                }
            }
            if (allPresent) return;
            value = string.IsNullOrEmpty(value) ? joined : value.TrimEnd(',') + "," + joined;
            PreservedCount++;
            LastDetail = $"{__instance.Name} (ID={__instance.ID}): kept [{joined}]";
        }
    }

    /// 防 ID 回收串味：带词缀的物件删了（消耗/分解/掉图/巡回卸载）就立刻清内存表和冷却表，
    /// ID 会被引擎回收发给新物件，残留的话新东西会整套"继承"词缀
    [HarmonyPatch(typeof(Item), nameof(Item.Remove))]
    public static class ItemRemovePurgePatch
    {
        static void Postfix(Item __instance)
        {
            if (Mod.ItemAffixes.Count == 0 && Mod.LastKnownAffixes.Count == 0) return; // 全局快速退出
            if (Mod.ItemAffixes.Remove(__instance.ID))
                CooldownDamageState.Purge(__instance.ID);
            ProjectileShootPatch.PurgeCrown(__instance.ID);
            RepairRampState.Purge(__instance.ID);
            // 会话表随物品真正删除一起清理（ID 复用防护；逐条移除，其余物品的条目继续保留）
            Mod.LastKnownAffixes.Remove(__instance.ID);
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

        /// <summary>OnUse 前快照内容物耐久，后面按实际消耗结算（锈蚀/节能/虚空/王冠共用）</summary>
        /// 使用前后的快照：内容物耐久（燃料/弹药）+ 物品自身耐久（持恒）
        sealed class UseSnapshot
        {
            public Dictionary<ushort, float> Conditions;
            public float ItemCondition;
        }

        static void Prefix(object __instance, object[] __args, out UseSnapshot __state)
        {
            __state = null;
            if (!Mod.IsGameplayAuthority) return; // 只跑一边：本地双上下文会让返还翻倍
            if (!Mod.AnyFuelMultAffixes && !Mod.AnyAmmoSaveAffixes && !Mod.AnySelfWearSaveAffixes
                && !Mod.AnyLastShotAffixes && !Mod.AnyFirstShotAffixes) return; // 没这类词缀就没事干
            if (__args == null || __args.Length < 2) return;
            if (__args[0] is not ActionType at || at != ActionType.OnUse) return;

            var item = ReflectionCache.GetItem(__instance);
            if (item == null || !Helpers.TryGetAffix(item, out var def)) return;
            if (Math.Abs(def.FuelConsumeMult - 1f) < 0.0001f && def.AmmoSaveChance <= 0f && def.SelfWearSaveChance <= 0f
                && def.LastShotDamageMult <= 1f && def.FirstShotDamageMult <= 1f) return;
            if (item.ContainedItems == null) return;

            var snap = new UseSnapshot { ItemCondition = item.Condition };
            foreach (var c in item.ContainedItems)
            {
                if (c != null) (snap.Conditions ??= new Dictionary<ushort, float>())[c.ID] = c.Condition;
            }
            __state = snap;
        }

        static void Postfix(object __instance, object[] __args, UseSnapshot __state)
        {
            if (!Mod.IsGameplayAuthority) return; // 单上下文执行：本地服务器双上下文补丁会让命中特效/affliction 双倍
            // 这道是每帧热路径：全场没词缀物品时一个 Count 就打发
            if (Mod.ItemAffixes.Count == 0) return;
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
            if (!Helpers.TryGetAffix(item, out var def))
            {
                // 弹丸自己没词缀就回溯发射器，枪才能触发命中特效（OnImpact）
                if (type != ActionType.OnImpact) return;
                var launcher = GetProjectileLauncher(item);
                if (launcher == null || !Helpers.TryGetAffix(launcher, out def)) return;
                affixSource = launcher;
                viaProjectile = true;
            }

            // 药物耐久 ≤20% 词缀不生效（本体也治不了病），必须在耗材结算前拦——
            // 药物词缀不进组件列表，全指着本补丁
            if (!viaProjectile && Mod.IsMedicalItem(item) &&
                item.MaxCondition > 0f && item.Condition / item.MaxCondition <= Mod.MedicalMinConditionPercent)
            {
                // 打行日志，别让人觉得词缀凭空没了
                Mod.LogThrottled($"affix_gate_{item.ID}", 30,
                    $"[{def.Identifier}] dormant: {item.Name} condition ≤20% (affix effects suppressed)");
                return;
            }

            var before = __state?.Conditions;
            // 耗材百分比结算（锈蚀/节能），跟有没有 StatusEffect 无关
            if (!viaProjectile) ApplyFuelConsumeMult(item, def, before);
            // 虚空：这一发掷一次，中了就全额返还本次弹药消耗
            if (!viaProjectile) ApplyAmmoSave(item, def, before);
            // 持恒：本次使用扣掉的自身耐久按概率退回
            if (!viaProjectile && def.SelfWearSaveChance > 0f) ApplySelfWearSave(item, def, __state);
            // 王冠不在这结算：改到 Projectile.Shoot 前缀按弹匣剩余比例算（见 ProjectileShootPatch.CrownRamp）
            // 领先：满弹匣打出的第一发增伤
            if (!viaProjectile) ApplyFirstShotBonus(item, def, before);

            if (def.Effects == null || def.Effects.Count == 0) return;

            for (int i = 0; i < def.Effects.Count; i++)
            {
                var effect = def.Effects[i];
                if (effect.type != type) continue;
                // 已注册进组件列表的效果由引擎正常触发，这里跳过避免双倍生效
                if (!viaProjectile && IsEffectRegistered(__instance, type, effect)) continue;

                // 药物：ApplyTreatment / Item.Use 会对每个组件各调一次本方法，同一帧里同一个效果只结算一次，
                // 否则一次用药的治疗量按组件数翻倍（健康界面还会连 OnSuccess+OnUse 两轮，XML 里只留 OnUse 了）
                if (!viaProjectile && Mod.IsMedicalItem(item) && !ProcOncePerFrame(item.ID, i)) continue;

                // 补丁管的命中特效只认真命中：打墙/落空不触发、不耗冷却。
                // 例外：纯自伤效果（只 target=Character 且带非 buff affliction，如愤怒）在使用时就结算——
                // 枪械开火时 useTarget/limb 都是空的，不放开的话这类效果既进不了引擎列表（带 interval 被
                // Mod.RegisterEffectsForDisplay 跳过）、又被这道闸门挡住，等于永远不生效
                if (useTarget is not Character && limb == null && !IsSelfHarmEffect(effect)) continue;

                // 带 interval 的没注册进组件（见 Mod.RegisterEffectsForDisplay），冷却补丁按真实秒数管；
                // 引擎那套按命中次数扣，高射速几下就烧完
                if (effect.Interval > 0f)
                {
                    var key = (affixSource.ID, i);
                    if (ProcTimers.TryGetValue(key, out double nextAllowed) && Timing.TotalTime < nextAllowed) continue;
                    ProcTimers[key] = Timing.TotalTime + effect.Interval;
                    // 把引擎自己的按次冷却清掉，免得双重门控
                    (ReflectionCache.IntervalTimersField?.GetValue(effect) as Dictionary<Entity, float>)
                        ?.Remove(affixSource);
                }

                if (user != null) effect.SetUser(user);
                // affliction 跟引擎一样吃攻击倍率，用完记得复位
                float attackMultiplier = __args.Length > 7 ? (float)__args[7] : 1.0f;
                // 跟引擎同款的使用者转换：目标是 Character 又不是 UseTarget 时换成使用者
                var c = character;
                if (user != null &&
                    effect.HasTargetType(StatusEffect.TargetType.Character) &&
                    !effect.HasTargetType(StatusEffect.TargetType.UseTarget))
                {
                    c = user;
                }
                // 一律以词缀持有者为上下文：投射物的冷却记在开火的枪上，不是每颗子弹各算各的
                effect.AttackMultiplier = attackMultiplier;
                affixSource.ApplyStatusEffect(effect, type, deltaTime,
                    c, limb, useTarget, isNetworkEvent: false, checkCondition: false);
                effect.AttackMultiplier = 1.0f;
            }
        }

        /// <summary>命中特效的真实时间冷却表</summary>
        static readonly Dictionary<(ushort ItemId, int EffectIndex), double> ProcTimers = new();

        /// <summary>药物词缀的同帧去重表：ApplyTreatment 对每个组件各触发一次，同一帧里同一个效果只认第一次</summary>
        static readonly Dictionary<(ushort ItemId, int EffectIndex), double> MedicalProcFrames = new();

        static bool ProcOncePerFrame(ushort itemId, int effectIndex)
        {
            var key = (itemId, effectIndex);
            double now = Timing.TotalTime;
            if (MedicalProcFrames.TryGetValue(key, out double lastProc) && lastProc == now) return false;
            MedicalProcFrames[key] = now;
            return true;
        }

        /// 待发增伤（王冠/领先）：OnUse 里这发子弹还没造出来，LastProjectile 还是上一发，
        /// 只能先记下倍率等同一帧的 Shoot 来消费；隔帧的陈旧值直接扔
        public static readonly Dictionary<ushort, (float Mult, double At)> PendingShotBoosts = new();

        public static void ClearProcTimers()
        {
            ProcTimers.Clear();
            MedicalProcFrames.Clear();
            PendingShotBoosts.Clear();
            CooldownDamageState.Clear();
            ProjectileShootPatch.ClearCrown();
            RepairRampState.Clear();
        }

        static void ApplyFuelConsumeMult(Item item, AffixDef def, Dictionary<ushort, float> before)
        {
            if (before == null || Math.Abs(def.FuelConsumeMult - 1f) < 0.0001f) return;
            if (item.ContainedItems == null) return;
            foreach (var c in item.ContainedItems)
            {
                if (c == null || !before.TryGetValue(c.ID, out float oldCond)) continue;
                float consumed = oldCond - c.Condition;
                if (consumed <= 0f) continue;
                // 打空的内容物别再返还：节能救回残量后耐久渐近归零永远打不完，最后一发成无限弹药（河童工具枪实测触发过）
                if (c.Condition <= 0.001f) continue;
                // mult=1.2 → 额外扣除消耗量的 20%；mult=0.8 → 返还 20%
                c.Condition = Math.Min(c.MaxCondition, c.Condition - consumed * (def.FuelConsumeMult - 1f));
            }
        }

        /// 虚空：每次 OnUse 掷一次，中了把本次弹药消耗全额还回来。只罩得住弹匣/电池这类耐久弹药，
        /// 左轮那种散装弹药一个实例一发的，管不了
        static void ApplyAmmoSave(Item item, AffixDef def, Dictionary<ushort, float> before)
        {
            if (before == null || def.AmmoSaveChance <= 0f) return;
            if (item.ContainedItems == null) return;
            float roll = Rand.Range(0f, 1f, Rand.RandSync.Unsynced);
            if (roll >= def.AmmoSaveChance) return;
            foreach (var c in item.ContainedItems)
            {
                if (c == null || !before.TryGetValue(c.ID, out float oldCond)) continue;
                float consumed = oldCond - c.Condition;
                if (consumed <= 0f) continue;
                c.Condition = Math.Min(c.MaxCondition, c.Condition + consumed);
            }
        }

        /// 持恒：本次使用（OnUse 这一轮）扣掉的自身耐久和弹匣/内容物耐久按同一个概率一起退回
        static void ApplySelfWearSave(Item item, AffixDef def, UseSnapshot snap)
        {
            if (snap == null) return;
            float selfConsumed = snap.ItemCondition - item.Condition;
            bool anyConsumed = selfConsumed > 0f;
            // 自耗武器往往连弹匣耐久一起扣，只退枪壳的话词缀等于只生效一半
            if (!anyConsumed && snap.Conditions != null && item.ContainedItems != null)
            {
                foreach (var c in item.ContainedItems)
                {
                    if (c != null && snap.Conditions.TryGetValue(c.ID, out float oldCond)
                        && oldCond - c.Condition > 0f)
                    {
                        anyConsumed = true;
                        break;
                    }
                }
            }
            if (!anyConsumed) return;
            if (Rand.Range(0f, 1f, Rand.RandSync.Unsynced) >= def.SelfWearSaveChance) return;

            if (selfConsumed > 0f)
                item.Condition = Math.Min(item.MaxCondition, item.Condition + selfConsumed);
            if (snap.Conditions != null && item.ContainedItems != null)
            {
                foreach (var c in item.ContainedItems)
                {
                    if (c == null || !snap.Conditions.TryGetValue(c.ID, out float oldCond)) continue;
                    float consumed = oldCond - c.Condition;
                    if (consumed <= 0f) continue;
                    c.Condition = Math.Min(c.MaxCondition, c.Condition + consumed);
                }
            }
        }

        /// 领先：射前满耐久、这发又确实扣了 → 满匣首发，增伤。散装弹药没这概念，不支持
        static void ApplyFirstShotBonus(Item item, AffixDef def, Dictionary<ushort, float> before)
        {
            if (before == null || def.FirstShotDamageMult <= 1f) return;
            if (item.ContainedItems == null) return;
            bool firstShot = false;
            foreach (var c in item.ContainedItems)
            {
                if (c == null || !before.TryGetValue(c.ID, out float oldCond)) continue;
                if (oldCond >= c.MaxCondition - 0.01f && c.Condition < oldCond - 0.0001f) { firstShot = true; break; }
            }
            if (!firstShot) return;
            BoostLastProjectile(item, def.FirstShotDamageMult, def.Identifier, "first-shot");
        }

        /// 只登记不直接改：OnUse 时本发子弹还没造出来，直接改 LastProjectile 会改到上一发上；
        /// 同一帧内随后的 Shoot 前缀来消费，齐射同帧全受益
        static void BoostLastProjectile(Item item, float mult, string affixId, string what)
        {
            PendingShotBoosts[item.ID] = (mult, Timing.TotalTime);
            Mod.DebugLog($"[{affixId}] {what} bonus armed on {item.Name}");
        }

        public static bool IsEffectRegistered(object component, ActionType type, StatusEffect effect)
        {
            if (ReflectionCache.StatusEffectListsField?.GetValue(component) is not
                Dictionary<ActionType, List<StatusEffect>> lists) return false;
            return lists.TryGetValue(type, out var list) && list.Contains(effect);
        }

        /// 纯自伤：只作用于使用者自己（target 仅 Character）且带非 buff 的 affliction。
        /// 这类效果不需要命中对象，使用时（含枪械开火）就该结算，见上面那道命中闸门的例外
        static bool IsSelfHarmEffect(StatusEffect effect)
        {
            if (effect == null || !effect.HasTargetType(StatusEffect.TargetType.Character)) return false;
            if (effect.HasTargetType(StatusEffect.TargetType.This)
                || effect.HasTargetType(StatusEffect.TargetType.Parent)
                || effect.HasTargetType(StatusEffect.TargetType.Contained)
                || effect.HasTargetType(StatusEffect.TargetType.NearbyCharacters)
                || effect.HasTargetType(StatusEffect.TargetType.NearbyItems)
                || effect.HasTargetType(StatusEffect.TargetType.UseTarget)
                || effect.HasTargetType(StatusEffect.TargetType.Hull)
                || effect.HasTargetType(StatusEffect.TargetType.Limb)
                || effect.HasTargetType(StatusEffect.TargetType.AllLimbs)
                || effect.HasTargetType(StatusEffect.TargetType.LastLimb)
                || effect.HasTargetType(StatusEffect.TargetType.LinkedEntities))
            {
                return false;
            }
            if (effect.Afflictions == null) return false;
            foreach (Affliction affliction in effect.Afflictions)
            {
                if (affliction?.Prefab != null && !affliction.Prefab.IsBuff) return true;
            }
            return false;
        }

        public static Item GetProjectileLauncher(Item item)
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

    /// 攻击状态表：荆棘反伤的冷却（Timers）+ 金冠的蓄能（CrownCharge）
    public static class CooldownDamageState
    {
        static readonly Dictionary<ushort, (double NextReady, double LastProc)> Timers = new();
        // 金冠：上次命中的时间与那一发吃到的倍率（同帧齐射共享一份）
        static readonly Dictionary<ushort, (double At, float Mult)> CrownCharge = new();

        public static void Clear() { Timers.Clear(); CrownCharge.Clear(); }

        /// <summary>物品删除时随词缀条目一起清理（ID 复用防护，见 ItemRemovePurgePatch）</summary>
        public static void Purge(ushort itemId) { Timers.Remove(itemId); CrownCharge.Remove(itemId); }

        /// 金冠：伤害倍率随"距上次命中的间隔"线性充能，fullAtSeconds 秒吃满 maxMult。
        /// 同一次攻击的多弹丸/多肢体（同一帧）共享同一份倍率；首次命中直接吃满
        public static float CrownMult(Item source, float maxMult, float fullAtSeconds)
        {
            double now = Timing.TotalTime;
            if (CrownCharge.TryGetValue(source.ID, out var prev) && prev.At == now)
                return prev.Mult;
            double dt = prev.At > 0.0 ? now - prev.At : double.MaxValue;
            float t = fullAtSeconds <= 0.01f ? 1f : (float)Math.Min(1.0, dt / fullAtSeconds);
            float mult = 1f + (maxMult - 1f) * t;
            CrownCharge[source.ID] = (now, mult);
            return mult;
        }

        /// <param name="sameFrameRepeats">
        /// true（默认，历史语义）= 同帧重复调用算同一次攻击，全部放行。
        /// false（荆棘反伤用）= 同帧只放行第一次：一次攻击不管命中几发/几根肢体，只反伤一次。
        /// </param>
        public static bool Proc(Item source, float interval, bool sameFrameRepeats = true)
        {
            double now = Timing.TotalTime; // 每帧更新一次：同帧调用 now 完全相同
            if (Timers.TryGetValue(source.ID, out var t))
            {
                if (now == t.LastProc) return sameFrameRepeats; // 同一次攻击的后续命中（多肢体/多弹丸）
                if (now < t.NextReady) return false;
            }
            Timers[source.ID] = (now + interval, now);
            return true;
        }
    }

    /// <summary>枪械伤害词条：发射时按武器词缀乘 damageMultiplier</summary>
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
            if (!Mod.IsGameplayAuthority) return; // 单上下文执行：本地服务器双上下文补丁会把倍率平方
            if (!Mod.AnyDamageMultAffixes && !Mod.AnySlotOneAffixes
                && !Mod.AnyLastShotAffixes && !Mod.AnyFirstShotAffixes) return; // 全局快速退出
            if (Mod.ItemAffixes.Count == 0) return; // 有定义无实例：剩余路径全白跑
            var launcher = ReflectionCache.LauncherField?.GetValue(__instance) as Item;
            if (launcher == null) return;

            // 只消费同帧的待发增伤（OnUse 到本发 Shoot 同帧紧邻）；齐射同帧全受益，隔帧的扔
            if ((Mod.AnyLastShotAffixes || Mod.AnyFirstShotAffixes)
                && AffixEffectInjectionPatch.PendingShotBoosts.TryGetValue(launcher.ID, out var boost))
            {
                if (boost.At == Timing.TotalTime)
                {
                    damageMultiplier *= boost.Mult;
                    Mod.DebugLog($"[shot-boost] consumed x{boost.Mult} on {launcher.Name}");
                }
                else
                    AffixEffectInjectionPatch.PendingShotBoosts.Remove(launcher.ID);
            }

            if (!Helpers.TryGetAffix(launcher, out var def)) return;
            // 奇迹的蓄力耦合在 ComposeAffixDef 里已烘焙好，别二次判定
            if (Math.Abs(def.DamageMult - 1f) > 0.0001f
                && (def.IsComposite || Mod.ChargeGatedDamageApplies(def, launcher)))
                damageMultiplier *= def.DamageMult;
            // 余烬：耐久越低伤害越高（开火时读武器当前耐久）
            if (def.LowConditionDamageMult > 1f)
                damageMultiplier *= Helpers.LowConditionMult(launcher, def.LowConditionDamageMult);
            // 优先：槽位激活标记 TickSlotOneBonuses 管（连"从 1 号槽拿到手上"都记着）
            if (def.SlotOneDamageMult > 1f &&
                Mod.TryGetAffixData(launcher, out var sdata) && sdata.SlotBonusActive)
                damageMultiplier *= def.SlotOneDamageMult;
            // 金冠不在这触发：开火就判会打空也耗冷却，统一挪到命中时（MeleeDamagePatch 回溯发射器）
            // 王冠在这触发：Shoot 之后才 RemoveContained，开火这一刻弹匣还没扣，正好能读"算上这一发"的剩余量
            if (def.LastShotDamageMult > 1f)
            {
                if (!CrownFrameMult.TryGetValue(launcher.ID, out var crown) || crown.At != Timing.TotalTime)
                {
                    crown = (Timing.TotalTime, CrownRamp(launcher, ReflectionCache.GetItem(__instance), def.LastShotDamageMult));
                    CrownFrameMult[launcher.ID] = crown;
                }
                damageMultiplier *= crown.Mult;
            }
        }

        /// <summary>王冠：每把枪见过的满弹数，当弹匣容量的分母</summary>
        static readonly Dictionary<ushort, int> CrownMagMax = new();
        /// <summary>王冠：同帧齐射的多发弹丸共用一份档位倍率（按第一发的弹匣状态算）</summary>
        static readonly Dictionary<ushort, (double At, float Mult)> CrownFrameMult = new();

        public static void PurgeCrown(ushort itemId)
        {
            CrownMagMax.Remove(itemId);
            CrownFrameMult.Remove(itemId);
        }

        public static void ClearCrown()
        {
            CrownMagMax.Clear();
            CrownFrameMult.Clear();
        }

        /// 王冠：弹匣剩得越少伤害越高。按"已消耗比例"分 10 档、每档 3%，最后 10% 吃满。
        /// 散装弹药按剩余发数 / 见过的满弹数（这一发也算已消耗）；电池类耐久弹药按 Condition/MaxCondition
        static float CrownRamp(Item launcher, Item projectile, float maxMult)
        {
            if (launcher.ContainedItems == null || projectile == null) return 1f;
            int count = 0;
            float condition = 0f, maxCondition = 0f;
            foreach (var c in launcher.ContainedItems)
            {
                if (c == null || c.Removed || c.Prefab != projectile.Prefab) continue;
                count++;
                condition += c.Condition;
                maxCondition += c.MaxCondition;
            }
            if (count == 0) return 1f; // 找不到弹匣内容物（特殊发射链）→ 这词缀不参与

            float consumed;
            if (maxCondition > 0.001f && condition < maxCondition - 0.001f)
            {
                consumed = 1f - condition / maxCondition;
            }
            else
            {
                CrownMagMax.TryGetValue(launcher.ID, out int maxSeen);
                if (count > maxSeen) CrownMagMax[launcher.ID] = maxSeen = count;
                // 单发弹匣（maxSeen 1）每一发都是"最后一发" → 吃满档，和旧版行为一致
                consumed = maxSeen <= 1 ? 1f : (maxSeen - count + 1f) / maxSeen;
            }
            int band = (int)Math.Ceiling(consumed * 10f);
            band = Math.Min(10, Math.Max(1, band));
            return 1f + (maxMult - 1f) * band / 10f;
        }
    }

    /// 攻击伤害：攻击方按武器词缀乘，防御方按目标穿戴（非手持）的词缀护甲乘。
    /// 角色伤害全汇进 ApplyAttack，引擎只在那读一次倍率，结构上重复乘不了；墙体/物品走 DoDamage，前缀里跳过角色目标
    [HarmonyPatch]
    public static class MeleeDamagePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var applyAttack = typeof(Character).GetMethod("ApplyAttack", BindingFlags.Public | BindingFlags.Instance);
            if (applyAttack == null) Mod.Warning("MeleeDamagePatch: Character.ApplyAttack NOT FOUND");
            else yield return applyAttack;

            if (ReflectionCache.AttackType == null)
            {
                Mod.Warning("MeleeDamagePatch: Attack type NOT FOUND");
                yield break;
            }
            var doDamage = ReflectionCache.AttackType.GetMethod("DoDamage", BindingFlags.Public | BindingFlags.Instance);
            if (doDamage == null) Mod.Warning("MeleeDamagePatch: DoDamage NOT FOUND on Attack");
            else yield return doDamage;
        }

        // __state = 改写前的原始倍率（NaN = 没动过）。
        // 引擎从不会重置投射物的 DamageMultiplier（音波那种持续命中会反复读），乘完不复原就逐击复利
        // （金冠 ×1.25 曾滚成 4.5 倍）。后缀恢复不一定跑得到，所以靠前缀自愈：
        // 每个 Attack 缓存一份干净基线，发现当前值还是我们上次写的就直接拿基线重算
        sealed class AttackBaseline
        {
            public float Baseline = float.NaN;   // 引擎原始倍率
            public float LastWritten = float.NaN; // 我们上次写入的值
        }
        static readonly ConditionalWeakTable<object, AttackBaseline> AttackBaselines = new();

        static void Prefix(object __instance, object[] __args, out float __state)
        {
            __state = float.NaN;
            // 这里炸了不能拖垮整局：吞掉记日志，顶多这次攻击没词缀
            try { PrefixInner(__instance, __args, ref __state); }
            catch (Exception ex) { Mod.LogThrottled("dmgprefix_err", 10, $"MeleeDamagePatch Prefix suppressed: {ex.Message}"); }
        }

        static void PrefixInner(object __instance, object[] __args, ref float __state)
        {
            if (!Mod.IsGameplayAuthority) return; // 单上下文执行：本地服务器双上下文补丁会把倍率平方
            if (!Mod.AnyDamageMultAffixes && !Mod.AnyDamageTakenAffixes && !Mod.AnyCooldownDamageAffixes
                && !Mod.AnySlotOneAffixes && !Mod.AnyMassBonusAffixes && !Mod.AnyLoneWolfAffixes
                && !Mod.AnyThornsAffixes) return; // 全局快速退出
            if (Mod.ItemAffixes.Count == 0) return; // 有定义无实例：剩余路径全白跑

            // 两个补丁入口的参数布局不同，先统一解析出 attack/victim/attacker
            Character victim;
            Character attacker;
            object attackObj;
            if (__instance is Character c)
            {
                // Character.ApplyAttack(attacker, worldPosition, attack, ...)：全部角色伤害的唯一漏斗
                victim = c;
                attacker = __args != null && __args.Length > 0 ? __args[0] as Character : null;
                attackObj = __args != null && __args.Length > 2 ? __args[2] : null;
            }
            else
            {
                // Attack.DoDamage(attacker, target, ...)：角色目标已由 ApplyAttack 覆盖，这里只处理墙体/物品
                if (__args == null || __args.Length < 2 || __args[1] is Character || __args[1] is Limb) return;
                victim = null;
                attacker = __args[0] as Character;
                attackObj = __instance;
            }
            if (attackObj == null) return;

            // 荆棘：穿戴者受击时反伤攻击者（与伤害倍率无关，按件独立冷却）
            if (Mod.AnyThornsAffixes && victim != null && attacker != null && attacker != victim)
                ApplyThorns(victim, attacker);

            float mult = 1f;

            if (Mod.AnyDamageMultAffixes || Mod.AnyCooldownDamageAffixes || Mod.AnySlotOneAffixes
                || Mod.AnyMassBonusAffixes || Mod.AnyLoneWolfAffixes)
            {
                var sourceItem = ReflectionCache.AttackSourceItemProp?.GetValue(attackObj) as Item;
                AffixDef atkDef = null;
                Item affixSource = null;
                bool viaProjectile = false;
                if (sourceItem != null && Helpers.TryGetAffix(sourceItem, out atkDef))
                {
                    affixSource = sourceItem; // 近战命中：SourceItem 是武器本体
                }
                else if (sourceItem != null)
                {
                    // 弹丸自身不带词缀，回溯发射器；静态倍率和槽位增伤发射时乘过了，这里只判命中才定的
                    var launcher = AffixEffectInjectionPatch.GetProjectileLauncher(sourceItem);
                    if (launcher != null && Helpers.TryGetAffix(launcher, out atkDef))
                    {
                        affixSource = launcher;
                        viaProjectile = true;
                    }
                }
                if (atkDef != null)
                {
                    // 奇迹的蓄力耦合在 ComposeAffixDef 里已烘焙好，别二次判定
                    if (!viaProjectile && Math.Abs(atkDef.DamageMult - 1f) > 0.0001f
                        && (atkDef.IsComposite || Mod.ChargeGatedDamageApplies(atkDef, affixSource)))
                        mult *= atkDef.DamageMult;
                    // 余烬：耐久越低伤害越高（投射物在开火时已经乘过，这里只管近战）
                    if (!viaProjectile && atkDef.LowConditionDamageMult > 1f)
                        mult *= Helpers.LowConditionMult(affixSource, atkDef.LowConditionDamageMult);
                    if (!viaProjectile && atkDef.SlotOneDamageMult > 1f &&
                        Mod.TryGetAffixData(affixSource, out var slotData) && slotData.SlotBonusActive)
                        mult *= atkDef.SlotOneDamageMult;
                    // 金冠：按距上次命中的间隔充能（10 秒蓄满），同一次攻击共享一份
                    if (atkDef.CooldownDamageMult > 1f)
                        mult *= CooldownDamageState.CrownMult(affixSource, atkDef.CooldownDamageMult, atkDef.CooldownDamageInterval);
                    // 巨人杀手：命中时按目标质量判定（发射时还不知道会命中谁）
                    if (atkDef.MassBonusMult > 1f && victim != null && victim.Mass >= atkDef.MassThreshold)
                        mult *= atkDef.MassBonusMult;
                    // 单打独斗：激活标记由 TickLoneWolfBonuses 维护
                    if (atkDef.LoneWolfDamageMult > 1f &&
                        Mod.TryGetAffixData(affixSource, out var loneData) && loneData.LoneWolfActive)
                        mult *= atkDef.LoneWolfDamageMult;
                }
            }

            // 防御端：目标角色穿戴的词缀护甲减伤
            if (Mod.AnyDamageTakenAffixes && victim != null)
                mult *= GetWornDamageTakenMult(victim);

            var prop = ReflectionCache.AttackDamageMultProp;
            if (prop == null) return;
            var bl = AttackBaselines.GetValue(attackObj, _ => new AttackBaseline());
            float current = (float)prop.GetValue(attackObj);
            // 自愈：当前值 = 上次写的 → 拿缓存基线；否则引擎动过，当前值当新基线
            float orig = current == bl.LastWritten ? bl.Baseline : current;
            bl.Baseline = orig;

            if (Math.Abs(mult - 1f) < 0.0001f)
            {
                // 没增益的命中也顺手愈合：还残留着我们的旧写入就还原
                if (current == bl.LastWritten && current != orig) WriteDamageMult(attackObj, prop, orig);
                return;
            }
            float boosted = orig * mult;
            bl.LastWritten = boosted;
            WriteDamageMult(attackObj, prop, boosted);
            __state = orig;
            if (Mod.DamageDebugLog)
            {
                Mod.Log($"[dmgdbg] {attacker?.Name ?? "?"} -> {(victim != null ? victim.Name : "结构/物品")}" +
                    $" 原倍率={orig:0.###} 词缀乘算={mult:0.###}" +
#if SERVER
                    " [SV]");
#else
                    " [CL]");
#endif
            }
        }

        /// <summary>直写后备字段绕开 setter 的坑（字段为 null 时会连基线一起改）；找不到字段才退回 setter</summary>
        static void WriteDamageMult(object attackObj, PropertyInfo prop, float value)
        {
            if (ReflectionCache.AttackDamageMultBackingField != null)
                ReflectionCache.AttackDamageMultBackingField.SetValue(attackObj, value);
            else
                prop.SetValue(attackObj, value);
        }

        /// 顺手把倍率改回去，图个干净；不跑也没关系，下次前缀会自愈。DoDamage 的 __instance 就是 Attack，ApplyAttack 的在 __args[2]
        static void Postfix(object __instance, object[] __args, float __state)
        {
            if (float.IsNaN(__state)) return;
            try
            {
                object attackObj = __instance is Character
                    ? (__args != null && __args.Length > 2 ? __args[2] : null)
                    : __instance;
                if (attackObj == null) return;
                var prop = ReflectionCache.AttackDamageMultProp;
                if (prop != null) WriteDamageMult(attackObj, prop, __state);
            }
            catch (Exception ex) { Mod.LogThrottled("dmgpostfix_err", 10, $"MeleeDamagePatch Postfix suppressed: {ex.Message}"); }
        }

        /// <summary>荆棘：扫穿戴者的装备槽（手/背包不算），冷却好了就给攻击者挂流血/撕裂</summary>
        static void ApplyThorns(Character victim, Character attacker)
        {
            if (victim.Inventory is not CharacterInventory inv) return;
            if (attacker.CharacterHealth == null) return;
            // 同队不反伤；按 TeamID 比而不是比人类——船员未必是人，敌人也可能是人，None 不豁免。
            // TeamType 的位置随版本变，用 var + ToString 躲编译期依赖
            var atkTeam = attacker.TeamID;
            if (atkTeam == victim.TeamID && atkTeam.ToString() != "None") return;
            // 凭依武装这类多槽位穿戴物每个槽位都会出现一次，得自己去重
            List<Item> seen = null;
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                var slotType = inv.SlotTypes[i];
                if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                    slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                {
                    continue;
                }
                var item = inv.GetItemAt(i);
                if (item == null) continue;
                if (seen != null && seen.Contains(item)) continue;
                if (!Helpers.TryGetAffix(item, out var def)) continue;
                if (def.ThornsBleeding <= 0f && def.ThornsLacerations <= 0f && def.ThornsStun <= 0f) continue;
                // 同帧只放行第一次：一次攻击哪怕同帧命中 6 根肢体/6 发弹丸（爆炸、霰弹、hitscan 多发），
                // 也只反伤一次。同帧全放行是给冷却增伤留的语义，反伤复用会把 8 点流血翻成 48
                if (!CooldownDamageState.Proc(item, def.ThornsInterval, sameFrameRepeats: false)) continue;
                (seen ??= new List<Item>(2)).Add(item);
                ApplyThornsAffliction(attacker, "bleeding", def.ThornsBleeding);
                ApplyThornsAffliction(attacker, "lacerations", def.ThornsLacerations);
                // 眩晕反伤：stun 的强度即秒数，打断攻击者动作；按概率触发（荆棘为 50%）
                if (def.ThornsStun > 0f
                    && Rand.Range(0f, 1f, Rand.RandSync.Unsynced) < def.ThornsStunChance)
                {
                    ApplyThornsAffliction(attacker, "stun", def.ThornsStun);
                }
            }
        }

        static void ApplyThornsAffliction(Character target, string afflictionId, float strength)
        {
            if (strength <= 0f) return;
            try
            {
                if (!AfflictionPrefab.Prefabs.TryGet(afflictionId, out AfflictionPrefab prefab)) return;
                var limb = target.AnimController?.MainLimb;
                if (limb == null) return;
                target.CharacterHealth.ApplyAffliction(limb, new Affliction(prefab, strength),
                    allowStacking: true, ignoreUnkillability: false, recalculateVitality: true);
            }
            catch (Exception ex)
            {
                Mod.Warning($"Thorns affix failed: {ex.Message}");
            }
        }

        static float GetWornDamageTakenMult(Character c)
        {
            if (c?.Inventory is not CharacterInventory inv) return 1f;
            float mult = 1f;
            // 多槽位穿戴物每件会出现多次，去重，不然减伤被平方
            List<Item> seen = null;
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
                if (seen != null && seen.Contains(item)) continue;
                if (Helpers.TryGetAffix(item, out var def) &&
                    Math.Abs(def.DamageTakenMult - 1f) > 0.0001f)
                {
                    (seen ??= new List<Item>(2)).Add(item);
                    mult *= def.DamageTakenMult;
                }
            }
            return mult;
        }
    }

    /// 匠人节奏：连续修理时修理速度逐步提升（船体/设备分开的速率，共用上限），停手超过 1 秒清零
    public static class RepairRampState
    {
        // 停手多久清空累积加成（秒）
        const double RampResetSeconds = 10.0;
        // 修理 tick 间隔超过这个值就算"停顿"：保持加成但不再增长（避免暂停期间白涨）
        const double ContinuousRepairGap = 0.25;

        static readonly Dictionary<ushort, (double Last, float Bonus)> State = new();

        public static void Clear() => State.Clear();
        public static void Purge(ushort itemId) => State.Remove(itemId);

        /// 只读当前加成（不推进）：船体路径在 Use 前缀里取，成功修理后再调 Advance
        public static float Peek(Item tool)
        {
            if (tool == null) return 0f;
            if (!State.TryGetValue(tool.ID, out var st)) return 0f;
            return Timing.TotalTime - st.Last > RampResetSeconds ? 0f : st.Bonus;
        }

        /// 修理 tick 后推进并返回当前加成（0 ~ maxBonus）
        /// 修理 tick 后推进并返回当前加成（0 ~ maxBonus）。
        /// 只有连续修理（tick 间隔 ≤0.25 秒）才增长；停手 ≤10 秒保持不清零、也不增长；超过 10 秒清零
        public static float Advance(Item tool, float ratePerSecond, float maxBonus)
        {
            if (tool == null || ratePerSecond <= 0f || maxBonus <= 0f) return 0f;
            double now = Timing.TotalTime;
            if (!State.TryGetValue(tool.ID, out var st)) st = (now, 0f);
            double dt = now - st.Last;
            if (dt > RampResetSeconds) st.Bonus = 0f;              // 停手超过 10 秒：清零
            else if (dt <= ContinuousRepairGap)                    // 连续修理：累积（停顿期不涨）
                st.Bonus = Math.Min(maxBonus, st.Bonus + (float)dt * ratePerSecond);
            st.Last = now;
            State[tool.ID] = st;
            return st.Bonus;
        }
    }

    /// 匠人节奏（船体）：修理结构时按连续修理时长临时放大 StructureFixAmount。
    /// 走 Use 前缀/后缀而不是属性 getter——自动属性 getter 会被 JIT 内联，Harmony 钩不到
    [HarmonyPatch]
    public static class RepairRampStructurePatch
    {
        static MethodBase TargetMethod()
        {
            var t = typeof(Item).Assembly.GetType("Barotrauma.Items.Components.RepairTool");
            var m = t?.GetMethod("Use", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) Mod.Warning("RepairRampStructurePatch: RepairTool.Use NOT FOUND");
            return m;
        }

        static void Prefix(object __instance, out float __state)
        {
            __state = float.NaN;
            if (!Mod.AnyRepairRampAffixes) return;
            if (!Mod.IsGameplayAuthority) return;
            var prop = ReflectionCache.RepairToolStructureFixProp;
            if (prop == null || !prop.CanRead || !prop.CanWrite) return;
            var item = ReflectionCache.GetItem(__instance);
            if (item == null || !Helpers.TryGetAffix(item, out var def) || def.RepairRamp <= 0f) return;
            float bonus = RepairRampState.Peek(item);
            if (bonus <= 0f) return;
            float original = (float)prop.GetValue(__instance);
            if (original <= 0f) return;
            __state = original;
            prop.SetValue(__instance, original * (1f + bonus));
        }

        static void Postfix(object __instance, float __state, bool __result)
        {
            if (!float.IsNaN(__state))
                ReflectionCache.RepairToolStructureFixProp?.SetValue(__instance, __state);
            if (!__result) return; // 没修到东西不计时
            if (!Mod.AnyRepairRampAffixes || !Mod.IsGameplayAuthority) return;
            var item = ReflectionCache.GetItem(__instance);
            if (item == null || !Helpers.TryGetAffix(item, out var def) || def.RepairRamp <= 0f) return;
            RepairRampState.Advance(item, def.RepairRamp, def.RepairRampMax);
        }
    }

    /// 巧匠（设备修理）：给 GetStatValue(RepairSpeed) 加 (倍率-1)。旧版乘的是 RepairDegreeOfSuccess
    /// 的返回值——0~1 的 lerp 因子不是速度倍率，高技能乘完外推超过 1，fixDuration 变 0 直接命中"秒修"
    /// 分支（用户实测远超 +20%）。引擎是 fixDuration /= 1+talent，加算在那严格线性，而且没状态，叠不了
    [HarmonyPatch]
    public static class DeviceRepairPatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(Character).GetMethod("GetStatValue", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) Mod.Warning("DeviceRepairPatch: Character.GetStatValue NOT FOUND");
            return m;
        }

        static void Postfix(Character __instance, object[] __args, ref float __result)
        {
            if (!Mod.AnyDeviceRepairAffixes && !Mod.AnyRepairRampAffixes) return; // 全局快速退出
            if (!Mod.IsGameplayAuthority) return; // 单上下文执行：本地双上下文各加一遍，+20% 会变 +40%
            if (ReflectionCache.RepairSpeedStat == null) return;
            if (__args == null || __args.Length < 1 || !Equals(__args[0], ReflectionCache.RepairSpeedStat)) return;
            if (__instance?.Info == null) return; // 引擎对无 Info 角色返回 0，不为它凭空造属性
            var held = __instance.HeldItems;
            if (held == null) return;
            foreach (var tool in held)
            {
                if (tool == null) continue;
                if (!Helpers.TryGetAffix(tool, out var def)) continue;
                float add = 0f;
                if (def.DeviceRepairMult > 1f) add += def.DeviceRepairMult - 1f;
                // 匠人节奏：设备修理每 0.5 秒 +10%（= 0.2/秒），上限共用
                if (def.RepairRampDevice > 0f) add += RepairRampState.Advance(tool, def.RepairRampDevice, def.RepairRampMax);
                if (add > 0f)
                {
                    __result += add;
                    return;
                }
            }
        }
    }

    /// 回流：药物吃耐久是组件 StatusEffect，哪条用药路径都汇进 ApplyStatusEffect 单数版，在这按概率全额还。
    /// 一次性（用完归零）的不还；同样吃 ≤20% 闸门，多人只服务端还，条件值会同步
    [HarmonyPatch(typeof(Item), nameof(Item.ApplyStatusEffect))]
    public static class TreatmentDurabilityPatch
    {
        static void Prefix(Item __instance, out float __state) => __state = __instance.Condition;

        static void Postfix(Item __instance, float __state)
        {
            float consumed = __state - __instance.Condition;
            if (!Mod.AnyDurabilitySaveAffixes) return; // 全局快速退出
            if (consumed <= 0f) return; // 绝大多数效果不动耐久，直接退出
            if (!Helpers.TryGetAffix(__instance, out var def) || def.DurabilitySaveChance <= 0f) return;
            if (__instance.Removed) return;
            // 只服务端还：本地双上下文各还一遍，同一份 Condition 会被加两次
            if (!Mod.IsGameplayAuthority) return;
            if (!Mod.IsMedicalItem(__instance)) return;
            if (__instance.Condition <= 0f) return;
            if (__instance.MaxCondition > 0f &&
                __instance.Condition / __instance.MaxCondition <= Mod.MedicalMinConditionPercent) return;
            float roll = Rand.Range(0f, 1f, Rand.RandSync.Unsynced);
            if (roll >= def.DurabilitySaveChance) return;
            __instance.Condition = Math.Min(__instance.MaxCondition, __instance.Condition + consumed);
        }
    }

    /// 冷静（降噪）：AI 听觉统一读 AITarget.SoundRange，在 setter 上乘比每帧轮询准。
    /// 反射找目标是因为双端程序集结构不一样，找不到就记警告跳过，别崩
    [HarmonyPatch]
    public static class NoiseReductionPatch
    {
        static MethodBase TargetMethod()
        {
            var t = typeof(Character).Assembly.GetType("Barotrauma.AITarget");
            var m = t?.GetProperty("SoundRange", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod();
            if (m == null) Mod.Warning("NoiseReduction: AITarget.set_SoundRange NOT FOUND");
            return m;
        }

        static void Prefix(object __instance, ref float value)
        {
            if (!Mod.AnyNoiseAffixes || Mod.ItemAffixes.Count == 0) return; // 全局快速退出
            if (ReflectionCache.GetAITargetEntity(__instance) is not Character c) return;
            float mult = Mod.GetNoiseMultFor(c);
            if (mult < 0.9999f) value *= mult;
        }
    }

    /// 主线程延迟任务：每帧看一眼任务队列，空了就是一个 Count 判断。
    /// 不敢用 Task.Delay 那种线程池回调——游戏状态线程不安全，恢复词缀必须在主线程跑
    [HarmonyPatch]
    public static class MainThreadSchedulerPatch
    {
        static MethodBase TargetMethod()
        {
            // 双端入口不一样：客户端是 GameMain.Update(GameTime)，服务端是 GameServer.Update(float)。
            // flags 必须带 NonPublic——Update 在 XNA 里是 protected override，公开化程序集里是 public，
            // 只按 Public 找运行时会拿 null，补丁整个打不上
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

    /// 存档前强制补一遍词缀标签：tickbox 的 setvalue tags 会把 __affix_ 标签整串擦掉，赶上自愈窗口
    /// 内存档又逢内存表不可用 affixid 就丢了。在这补一次，跟 OnRoundEnd 的补戳互为双保险
    [HarmonyPatch]
    public static class PreSaveAffixStampPatch
    {
        static MethodBase TargetMethod()
        {
            MethodBase found = null;
            // SaveUtil 可能在共享程序集而非 GameMain 所在程序集，遍历全部已加载程序集
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Barotrauma.SaveUtil");
                if (t == null) continue;
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(x => x.Name == "SaveGame").ToList();
                found = methods.FirstOrDefault(x => x.GetParameters().Length >= 1
                        && x.GetParameters()[0].ParameterType == typeof(string))
                    ?? methods.FirstOrDefault();
                if (found != null)
                {
                    Mod.LogOnce($"PreSaveAffixStamp: patched {found}");
                    break;
                }
            }
            if (found != null) return found;
            // 找不到也不能返回 null（这版 Harmony 会抛异常断掉整个 PatchAll），打个哑方法退化为只靠 OnRoundEnd 补戳
            Mod.Warning("PreSaveAffixStamp: SaveUtil.SaveGame NOT FOUND, using dummy target (roundEnd stamp still active)");
            return typeof(PreSaveAffixStampPatch).GetMethod(nameof(DummyTarget),
                BindingFlags.NonPublic | BindingFlags.Static);
        }

        static void DummyTarget() { }

        static void Prefix() => Mod.StampAllAffixTags();
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
            Mod.LogOnce($"EnchantingStation: found Deconstructor type: {type.FullName}");

            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
            {
                if (m.Name != "ProcessItem") continue;
                var p = m.GetParameters();
                if (p.Length >= 1 && p[0].ParameterType == typeof(Item))
                {
                    Mod.LogOnce($"EnchantingStation: PATCHING ProcessItem ({p.Length} params)");
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

            // 必须验身份：补丁挂 Deconstructor.ProcessItem 上，对所有解构仪生效，
            // 不拦的话随便一台通电解构仪都把"武器+材料"当附魔（肉桂事件）
            var station = ReflectionCache.GetItem(__instance);
            if (station == null || !station.HasTag("enchantingstation")) return true;

            var inputInventory = targetItem.ParentInventory;
            if (inputInventory == null) return true;

            // 只处理输入栏的：一轮会对同一物品触发多次，完事的已在输出栏，不拦会误判空放、刚附的奇迹被剥掉；
            // 放给原版没事，原版对输出栏物品是空操作
            var inputContainer = Traverse.Create(__instance).Field("inputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
            bool inInput;
            if (inputContainer != null)
            {
                inInput = ReferenceEquals(inputContainer.Inventory, inputInventory);
            }
            else
            {
                // inputContainer 拿不到就反着来：outputContainer 是验过的，排除法
                var oc = Traverse.Create(__instance).Field("outputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
                inInput = oc == null || !ReferenceEquals(oc.Inventory, inputInventory);
            }
            if (!inInput)
            {
                Mod.DebugLog($"EnchantingStation: skipped {targetItem.Name} (already in output container)");
                return true;
            }

            var items = inputInventory.AllItems.ToList();

            // 回收：主词缀为回收、带一个额外词缀 + 空白附魔石 → 把额外词缀取到石头上（石头不消耗，变成载体）
            if (Mod.TryGetSalvageExtraction(items, out var salvageWeapon, out var blankStone, out var salvageExtra))
                return ExtractSalvageAffix(__instance, inputInventory, salvageWeapon, blankStone, salvageExtra);

            var result = Mod.TryGetEnchantingTarget(items, out var weapon, out var material);
            if (result == null)
            {
                // 奇迹物品没材料时单独办：剥掉奇迹以外的词缀退回输出栏，绝不进分解。
                // 判定走三级兜底（内存表→会话表→tags），不依赖会被 tickbox 擦除的标签
                if (Mod.HasAffixId(targetItem, Mod.MIRACLE_AFFIX_ID))
                    return StripMiracleExtras(__instance, inputInventory, targetItem);

                // 回收物品同理：额外词缀要用空白石头取，别让它被当普通物品分解掉
                if (Mod.HasAffixId(targetItem, Mod.SALVAGE_AFFIX_ID))
                    return ReturnFromStation(__instance, inputInventory, targetItem,
                        $"EnchantingStation: {targetItem.Name} has [salvage], returned to output (use a blank stone to extract)");

                // 无组合是常态（哨站里随便拆点东西都算），5 秒节流；"补丁没跑"仍可从完全无输出判断
                Mod.LogThrottled("ench_no_combo", 5.0,
                    $"EnchantingStation: no weapon+material combo in {station.Name} (items: {string.Join(", ", items.Select(i => i?.Name ?? "null"))}), normal deconstruct");
                return true;
            }

            var affix = Mod.PickAffixByWeight(result.Value.weights, weapon, weapon.ID ^ Environment.TickCount);
            if (affix == null)
            {
                if (result.Value.tierKey == Mod.STONE_TAG)
                {
                    // 石头词缀不适用就整单取消，两边都不消耗（return true 会走正常分解把东西拆了，绝对不行）
                    Mod.LogThrottled("stone_inapplicable_" + weapon.ID, 5.0,
                        $"EnchantingStation: stone affix not applicable to {weapon.Name}, enchantment cancelled (nothing consumed)");
                    return false;
                }
                Mod.DebugLog($"EnchantingStation: no applicable affix for {weapon.Name}");
                return true;
            }

            // 石头转移按目标分几种走法，其余照旧整组替换。
            // 当前词缀组走三级兜底读取：内存恢复前的窗口里若只读内存表，会把"给额外槽物品加词缀"
            // 误执行成"整组替换"（实测：转移优先石头时把宿主连额外一起顶掉）
            if (result.Value.tierKey == Mod.STONE_TAG && !weapon.HasTag(Mod.STONE_TAG))
            {
                bool hasHost = Mod.TryGetCurrentAffixIds(weapon, out var currentIds)
                    && currentIds.Count > 0 && Mod.IsExtraSlotHost(currentIds[0]);
                if (affix.Identifier == Mod.MIRACLE_AFFIX_ID)
                {
                    // 奇迹石头：覆盖物品原有词缀，物品回到"仅奇迹"状态
                    Mod.SetAffixes(weapon, new List<string> { Mod.MIRACLE_AFFIX_ID });
                }
                else if (affix.Identifier == Mod.SALVAGE_AFFIX_ID)
                {
                    // 回收石头：物品原有的单词缀挪进额外槽；已有别的宿主的整单取消（两边都不消耗）
                    if (hasHost)
                    {
                        Mod.LogThrottled("salvage_host_" + weapon.ID, 5.0,
                            $"EnchantingStation: {weapon.Name} already has an extra-slot affix, salvage transfer cancelled (stone not consumed)");
                        return false;
                    }
                    var ids = new List<string> { Mod.SALVAGE_AFFIX_ID };
                    if (currentIds != null) ids.AddRange(currentIds);
                    Mod.SetAffixes(weapon, ids);
                }
                else if (hasHost)
                {
                    // 额外槽（奇迹两格/回收一格），槽位不合的在 PickAffixByWeight 就返回 null 取消了
                    currentIds.Add(affix.Identifier);
                    Mod.SetAffixes(weapon, currentIds);
                }
                else
                {
                    Mod.ApplyAffix(weapon, affix);
                }
            }
            else
            {
                // 材料随机附魔或目标是石头；奇迹物品在此整体重掷，连额外词缀一起被顶替。
                // 回收物品：额外槽只认附魔石转移（描述里也这么写），材料随机到别的词缀整单取消；
                // 随机到回收本身则按主词缀处理，和石头路径一样把原有词缀挪进额外槽
                List<string> salvageIds = null;
                bool salvageHost = !weapon.HasTag(Mod.STONE_TAG) && result.Value.tierKey != Mod.STONE_TAG
                    && Mod.TryGetCurrentAffixIds(weapon, out salvageIds)
                    && salvageIds.Count > 0 && salvageIds[0] == Mod.SALVAGE_AFFIX_ID;
                if (salvageHost && affix.Identifier != Mod.SALVAGE_AFFIX_ID)
                {
                    Mod.LogThrottled("salvage_stoneonly_" + weapon.ID, 5.0,
                        $"EnchantingStation: extra affix on {weapon.Name} requires a stone transfer, material roll cancelled (nothing consumed)");
                    return false;
                }
                if (salvageHost)
                {
                    Mod.SetAffixes(weapon, new List<string> { Mod.SALVAGE_AFFIX_ID });
                }
                else if (!weapon.HasTag(Mod.STONE_TAG) && result.Value.tierKey != Mod.STONE_TAG
                    && affix.Identifier == Mod.SALVAGE_AFFIX_ID)
                {
                    // 材料随机到回收：像回收石一样把原有词缀挪进额外槽
                    Mod.TryGetCurrentAffixIds(weapon, out var oldIds);
                    var ids = new List<string> { Mod.SALVAGE_AFFIX_ID };
                    if (oldIds != null) ids.AddRange(oldIds);
                    Mod.SetAffixes(weapon, ids);
                }
                else
                {
                    Mod.ApplyAffix(weapon, affix);
                }
            }
            Mod.BroadcastAffixApplied(weapon, affix);
            // 马上写桥接文件：附魔台流程本来就不写档，崩了的话词缀只能靠标签兜底
            Mod.SaveAffixData();

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

        /// <summary>奇迹剥离：奇迹以外的词缀全去掉（直接消失），退回输出栏。
        /// 不管有没有可剥的、输出满不满都返回 false——奇迹物品绝不进分解</summary>
        static bool StripMiracleExtras(object __instance, Inventory inputInventory, Item targetItem)
        {
            bool stripped;
            if (Mod.TryGetAffixData(targetItem, out var data) && data.AffixId == Mod.MIRACLE_AFFIX_ID)
            {
                stripped = data.HasExtras;
                if (stripped)
                {
                    // 效果注销/属性恢复/标签重写都在 SetAffixes 内完成
                    Mod.SetAffixes(targetItem, new List<string> { Mod.MIRACLE_AFFIX_ID });
                    if (Mod.AffixDefs.TryGetValue(Mod.MIRACLE_AFFIX_ID, out var miracleDef))
                        Mod.BroadcastAffixApplied(targetItem, miracleDef);
                    Mod.SaveAffixData();
                }
            }
            else
            {
                // 内存表没中（标签兜底）：只清多余词缀标签，奇迹和 UID 的留着
                var extraTags = targetItem.GetTags()
                    .Where(t => t.Value.StartsWith(Mod.AFFIX_TAG_PREFIX, StringComparison.OrdinalIgnoreCase)
                        && !t.Value.Equals(Mod.AFFIX_TAG_PREFIX + Mod.MIRACLE_AFFIX_ID, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                stripped = extraTags.Count > 0;
                foreach (var t in extraTags) targetItem.RemoveTag(t);
            }

            inputInventory.RemoveItem(targetItem);
            var outputContainer = Traverse.Create(__instance).Field("outputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
            if (outputContainer != null && outputContainer.Inventory.TryPutItem(targetItem, null))
            {
                Mod.Log(stripped
                    ? $"EnchantingStation: stripped extra affixes from {targetItem.Name}, only [miracle] remains"
                    : $"EnchantingStation: {targetItem.Name} has no extra affixes to strip");
            }
            else
            {
                inputInventory.TryPutItem(targetItem, null);
                Mod.Log($"EnchantingStation: output full, strip cancelled for {targetItem.Name}");
            }
            return false;
        }

        /// 回收取词缀：把武器额外槽里的词缀转到空白附魔石上，武器只留回收；两件都退回输出栏。
        /// 输出空间不足整单取消，零消耗
        static bool ExtractSalvageAffix(object __instance, Inventory inputInventory, Item weapon, Item stone, string extraId)
        {
            if (!Mod.AffixDefs.ContainsKey(extraId))
            {
                Mod.Log($"EnchantingStation: salvage extraction cancelled, unknown affix {extraId}");
                return false;
            }
            var outputContainer = Traverse.Create(__instance).Field("outputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
            if (outputContainer == null) return false;
            var output = outputContainer.Inventory;

            // 先搬石头再搬武器；第二步失败就把石头放回去，保证不出现半截状态
            inputInventory.RemoveItem(stone);
            if (!output.TryPutItem(stone, null))
            {
                inputInventory.TryPutItem(stone, null);
                Mod.Log($"EnchantingStation: output full, salvage extraction cancelled for {weapon.Name}");
                return false;
            }
            inputInventory.RemoveItem(weapon);
            if (!output.TryPutItem(weapon, null))
            {
                output.RemoveItem(stone);
                inputInventory.TryPutItem(stone, null);
                inputInventory.TryPutItem(weapon, null);
                Mod.Log($"EnchantingStation: output full, salvage extraction cancelled for {weapon.Name}");
                return false;
            }

            Mod.SetAffixes(stone, new List<string> { extraId });
            Mod.SetAffixes(weapon, new List<string> { Mod.SALVAGE_AFFIX_ID });
            if (Mod.AffixDefs.TryGetValue(extraId, out var extraDef)) Mod.BroadcastAffixApplied(stone, extraDef);
            if (Mod.AffixDefs.TryGetValue(Mod.SALVAGE_AFFIX_ID, out var salvageDef)) Mod.BroadcastAffixApplied(weapon, salvageDef);
            Mod.SaveAffixData();
            Mod.Log($"EnchantingStation: extracted [{extraId}] from {weapon.Name} onto {stone.Name}");
            return false;
        }

        /// 回收物品单独放进附魔台：原样退回输出栏，不分解也不剥词缀
        static bool ReturnFromStation(object __instance, Inventory inputInventory, Item targetItem, string logMessage)
        {
            inputInventory.RemoveItem(targetItem);
            var outputContainer = Traverse.Create(__instance).Field("outputContainer").GetValue<Barotrauma.Items.Components.ItemContainer>();
            if (outputContainer != null && outputContainer.Inventory.TryPutItem(targetItem, null))
            {
                Mod.Log(logMessage);
            }
            else
            {
                inputInventory.TryPutItem(targetItem, null);
                Mod.Log($"EnchantingStation: output full, return cancelled for {targetItem.Name}");
            }
            return false;
        }
    }

#if CLIENT
    /// <summary>
    /// 附魔台信息区的上下文提示。客户端 Deconstructor 的信息区每帧从 InfoText 属性刷新
    /// （infoArea.Text = TextManager.Get(InfoText).Fallback(InfoText)，解析不到的键原样显示），
    /// 在 getter 上打后补丁即可按输入栏内容动态给文案——不改任何状态、无每帧写入：
    ///   ① 输入栏有非外套槽的穿戴物（头饰/耳机等）：提示防具类附魔不适用、直接启动会被分解；
    ///   ② 其余情况（含空输入）：默认显示三种附魔材料说明。
    /// 注：存档时序列化会读到动态值（仅文案、无害），脚本不做额外处理。
    /// </summary>
    [HarmonyPatch(typeof(Barotrauma.Items.Components.Deconstructor), "get_InfoText")]
    public static class EnchantingStationHintPatch
    {
        static void Postfix(Barotrauma.Items.Components.Deconstructor __instance, ref string __result)
        {
            var stationItem = ReflectionCache.GetItem(__instance);
            if (stationItem == null || !stationItem.HasTag("enchantingstation")) return;

            var input = __instance.InputContainer?.Inventory;
            if (input != null)
            {
                foreach (var it in input.AllItems)
                {
                    if (it == null || it.Removed || it.HasTag(Mod.STONE_TAG)) continue;
                    // 穿戴物但不在外套槽（头饰/耳机/背包等）：防具类附魔不适用；
                    // 与材料同放启动还会被分解，必须明确警告
                    if ((Mod.IsArmor(it) || Mod.IsPlainWearable(it)) && !Mod.IsOuterClothes(it))
                    {
                        __result = "enchantingstation.hint.nonarmorslot";
                        return;
                    }
                }
            }
            __result = "enchantingstation.hint.materials";
        }
    }
#endif

    /// <summary>穿戴词条公共判定：扫装备槽（手/背包不算），找第一个满足的</summary>
    public static class WornAffixHelper
    {
        public static bool HasWornAffix(Character c, Func<AffixDef, bool> predicate)
        {
            if (c?.Inventory is not CharacterInventory inv) return false;
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                var slotType = inv.SlotTypes[i];
                if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                    slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                {
                    continue;
                }
                var item = inv.GetItemAt(i);
                if (item == null) continue;
                if (Helpers.TryGetAffix(item, out var def) && predicate(def)) return true;
            }
            return false;
        }
    }

    /// 稳定：手部受伤的瞄准抖动（GetAimWobble），手持 noaimwobble 词缀直接归 0。
    /// 参考 tsm 模组的 Lua 写法
    [HarmonyPatch]
    public static class AimWobblePatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(AnimController).GetMethod("GetAimWobble",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Mod.Warning("AimWobblePatch: AnimController.GetAimWobble NOT FOUND");
            return m;
        }

        static bool Prefix(object[] __args, ref float __result)
        {
            if (!Mod.AnyAimWobbleAffixes) return true; // 全局快速退出
            var held = __args != null && __args.Length > 2 ? __args[2] as Item : null;
            if (held == null) return true;
            if (Helpers.TryGetAffix(held, out var def) && def.NoAimWobble)
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }

    /// 踏步：腿部移速惩罚双保险——①CalculateMovementPenalty 前缀归 0（跟 tsm 的 Lua 一致）；
    /// ②GetLegPenalty 后缀兜底，那是读腿部惩罚的唯一漏斗，以后公式绕开①也照样归 0
    [HarmonyPatch]
    public static class MovementPenaltyPatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(Character).GetMethod("CalculateMovementPenalty",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Mod.Warning("MovementPenaltyPatch: Character.CalculateMovementPenalty NOT FOUND");
            else Mod.LogOnce("MovementPenaltyPatch: patched Character.CalculateMovementPenalty");
            return m;
        }

        static bool Prefix(Character __instance, ref float __result)
        {
            if (!Mod.AnyMovePenaltyAffixes) return true; // 全局快速退出
            if (WornAffixHelper.HasWornAffix(__instance, def => def.NoMovePenalty))
            {
                __result = 0f;
                return false;
            }
            return true;
        }
    }

    /// <summary>踏步兜底：GetLegPenalty 是读腿部惩罚的漏斗，穿了就归 0</summary>
    [HarmonyPatch]
    public static class LegPenaltyPatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(Character).GetMethod("GetLegPenalty",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (m == null) Mod.Warning("LegPenaltyPatch: Character.GetLegPenalty NOT FOUND");
            else Mod.LogOnce("LegPenaltyPatch: patched Character.GetLegPenalty");
            return m;
        }

        static void Postfix(Character __instance, ref float __result)
        {
            if (!Mod.AnyMovePenaltyAffixes) return; // 全局快速退出
            if (__result <= 0f) return;
            if (WornAffixHelper.HasWornAffix(__instance, def => def.NoMovePenalty))
                __result = 0f;
        }
    }

    /// 紧张/等候：蓄力倍率打在 MaxChargeTime 的 getter 上实时乘，这值会被别的系统改写，写实例只生效一次。
    /// 但装弹等状态会写 999 再用 eq 999 找恢复点，乘了就永远失配卡装弹——原始值超 5 秒的一律不碰
    [HarmonyPatch]
    public static class ChargeTimePatch
    {
        /// <summary>超过这秒数就当是装弹标记（999 那种），不乘</summary>
        const float MaxNormalChargeSeconds = 5f;

        static MethodBase TargetMethod()
        {
            var m = ReflectionCache.RangedWeaponMaxChargeTimeProp?.GetGetMethod();
            if (m == null) Mod.Warning("ChargeTimePatch: RangedWeapon.MaxChargeTime getter NOT FOUND");
            return m;
        }

        static void Postfix(object __instance, ref float __result)
        {
            if (!Mod.AnyChargeTimeAffixes) return; // 全局快速退出
            if (!Mod.IsGameplayAuthority) return; // 单上下文执行：双上下文会让倍率平方
            if (__result > MaxNormalChargeSeconds) return; // 装弹标记等大值不乘算（否则 eq 999 条件失配卡死）
            var item = ReflectionCache.GetItem(__instance);
            if (item == null || !Helpers.TryGetAffix(item, out var def)) return;
            if (Math.Abs(def.ChargeTimeMult - 1f) > 0.0001f)
                __result *= def.ChargeTimeMult;
        }
    }
}
