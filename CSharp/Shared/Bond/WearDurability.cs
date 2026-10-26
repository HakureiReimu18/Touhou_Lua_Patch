using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Bond
{
    /// <summary>
    /// 带 Touhou_Condition_Loss_Rate_X tag 的穿戴物，主人挨揍按 X 倍掉耐久。
    /// 掉多少用 ApplyAttack 前后的活力差量，曲线 = 10 × (伤害/48)² × tag × 全局倍率。
    /// </summary>

    public static class WearDurabilityPatch
    {
        const string TAG_PREFIX = "Touhou_Condition_Loss_Rate_";

        const float CURVE_REF_DAMAGE = 48f;
        const float CURVE_LOSS_AT_REF = 10f;

        // 前后缀配对，重复注册也只有一份后缀消费
        sealed class VitalitySlot { public float Value; }
        static readonly ConditionalWeakTable<Character, VitalitySlot> pendingVitality = new();

        sealed class RateCache { public float Rate; }
        static readonly ConditionalWeakTable<Item, RateCache> rateCache = new();

        static MethodBase TargetMethod()
        {
            var m = typeof(Character).GetMethod("ApplyAttack", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) BondLog.Warn("WearDurabilityPatch: Character.ApplyAttack 未找到");
            return m;
        }

        static void Prefix(Character __instance, Character attacker, Attack attack)
        {
            try
            {
                if (!Touhou.Affixes.Mod.IsGameplayAuthority) return; // 只跑单上下文
                if (BondConfig.CondLossMult <= 0f) return;
                if (__instance == null || __instance.IsDead || __instance.Removed) return;
                if (attacker == null || attacker == __instance) return; // 装备/药物 DoT、自伤不计
                if (__instance.CharacterHealth == null) return;
                pendingVitality.GetValue(__instance, k => new VitalitySlot()).Value = __instance.CharacterHealth.Vitality;
            }
            catch { pendingVitality.Remove(__instance); }
        }

        static void Postfix(Character __instance)
        {
            if (!pendingVitality.TryGetValue(__instance, out var slot)) return;
            float before = slot.Value;
            pendingVitality.Remove(__instance); // 立即消费：堆叠注册的后续后缀读不到直接跳过
            if (before < 0f) return;
            try
            {
                float damage = before - __instance.CharacterHealth.Vitality;
                if (!(damage > 0.001f)) return; // 活力异常时是 NaN，只有反过来写才挡得住
                Drain(__instance, damage);
            }
            catch (Exception ex)
            {
                BondLog.Warn($"耐久损耗抑制了异常：{ex.Message}");
            }
        }

        static void Drain(Character victim, float damage)
        {
            if (victim.Inventory is not CharacterInventory inv) return;
            float curve = damage / CURVE_REF_DAMAGE;
            float baseLoss = CURVE_LOSS_AT_REF * curve * curve;
            if (baseLoss < 0.0005f) return;

            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                var slotType = inv.SlotTypes[i];
                if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                    slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand))
                    continue;
                var item = inv.GetItemAt(i);
                if (item == null || item.Removed || item.MaxCondition <= 0f) continue;

                float rate = GetRate(item);
                if (rate <= 0f) continue;

                float loss = baseLoss * rate * BondConfig.CondLossMult;
                if (loss <= 0f) continue;
                float oldCond = item.Condition;
                item.Condition = Math.Max(0f, item.Condition - loss);
                if (BondConfig.Debug)
                    BondLog.Debug($"[耐久] {victim.Name} 的 {item.Name}：-{loss:0.###}（{oldCond:0.#}→{item.Condition:0.#}，伤害 {damage:0.#} × 倍率 {rate:0.##}）");
            }
        }

        /// 解析并缓存 tag 倍率。GetTags 会带上 prefab 标签，前缀对上数字就行
        static float GetRate(Item item)
        {
            if (rateCache.TryGetValue(item, out var rc)) return rc.Rate;
            float rate = 0f;
            foreach (var tag in item.GetTags())
            {
                string t = tag.Value;
                if (!t.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase)) continue;
                if (float.TryParse(t.Substring(TAG_PREFIX.Length), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float v) && v > 0f)
                    rate = v;
            }
            rateCache.Add(item, new RateCache { Rate = rate });
            return rate;
        }
    }
}
