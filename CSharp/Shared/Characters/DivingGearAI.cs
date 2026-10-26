using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Reflection;

namespace Touhou.DivingGearAI
{
    // 让 AI 认可"靠自身耐久供氧"的潜水服：魔力披纱 / 魔力披纱(粉) / 二色莲花蝶 / 星界披纱。
    //
    // 引擎判断"AI 有没有可用潜水装备"是纯标签模型：穿着的 deepdiving 装备里必须含一件带 oxygensource
    // 标签、Condition 达标的物品（HumanAIController.HasDivingSuit → HasItem(..., containedTag: OxygenSource)）。
    // 这几件装备的氧气来自自身 Condition（XML 里 OxygenAvailable="1000.0" Condition="-0.04"），容器里没有氧气源，
    // 于是 AI 一律判"没装备/没氧气"：反复进"找潜水装备"流程；AIObjectiveFindDivingGear 还会给它配氧气罐
    // ——而这类装备只有勾玉/注射槽，罐子必然放不进去，AI 就在"找装备→搬罐子→失败→再来"里打转。
    // 识别方式是主模组里给这几件装备新加的 Touhou_SelfOxygen 标签（以后新增自供氧装备记得挂上）。
    static class DgLog
    {
        public static void Info(object msg) => LuaCsLogger.LogMessage($"[Touhou.DivingGearAI] {msg ?? "null"}", Color.LightGreen);
        public static void Warn(object msg) => LuaCsLogger.LogMessage($"[Touhou.DivingGearAI] {msg ?? "null"}", Color.Orange);
    }

    public static class SelfOxygen
    {
        public static readonly Identifier Tag = "Touhou_SelfOxygen".ToIdentifier();

        // 只看穿在身上的（InnerClothes/OuterClothes），跟引擎 HasEquippedItem 的口径一致。
        // 直接扫槽位而不是遍历 AllItems：HasDivingSuit 会被 CalculateHullSafety 之类的路径高频调用，
        // 别在热路径上现场拼集合。
        public static Item GetWorn(Character character)
        {
            if (character?.Inventory is not CharacterInventory inv) return null;
            int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
            for (int i = 0; i < slotCount; i++)
            {
                if ((inv.SlotTypes[i] & (InvSlotType.InnerClothes | InvSlotType.OuterClothes)) == 0) continue;
                var item = inv.GetItemAt(i);
                if (item != null && !item.Removed && item.HasTag(Tag)) return item;
            }
            return null;
        }

        // 复刻 AIObjectiveFindDivingGear.IsSuitablePressureProtection：压力防护够不够当前深度。
        // 语义复刻而不是反射调它，是因为 HasDivingSuit 在 AI 热路径上，吃不消每次 Invoke。
        public static bool PressureOk(Item item, Character character)
        {
            var wearable = item.GetComponent<Wearable>();
            if (wearable == null) return false;
            float depth = Level.Loaded?.GetRealWorldDepth(character.WorldPosition.Y) ?? 0f;
            return wearable.PressureProtection >= depth + 500f;
        }
    }

    // 核心：AIObjectiveFindSafety / AIObjectiveGoTo / AIObjectiveRescue 里所有"我有没有潜水装备"的门都走这里
    [HarmonyPatch]
    public static class HasDivingSuitPatch
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(HumanAIController), "HasDivingSuit");
            if (m == null) DgLog.Warn("HumanAIController.HasDivingSuit NOT FOUND");
            return m;
        }

        static bool Prefix(Character character, float conditionPercentage, bool requireOxygenTank,
            bool requireSuitablePressureProtection, ref bool __result)
        {
            // 不要求氧气罐时原逻辑本来就过得去（我们只补"没有内含氧气源"这一条）
            if (!requireOxygenTank) return true;
            var suit = SelfOxygen.GetWorn(character);
            if (suit == null) return true;
            if (suit.ConditionPercentage < conditionPercentage) return true;
            if (requireSuitablePressureProtection && !SelfOxygen.PressureOk(suit, character)) return true;
            __result = true;
            return false;
        }
    }

    // NeedsDivingGear 判定"只要轻装"时（needsSuit == false），AIObjectiveFindSafety 问的是 HasDivingMask。
    // 不补的话，穿着披纱的 AI 仍会被当成没装备、满船找潜水镜。
    [HarmonyPatch]
    public static class HasDivingMaskPatch
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(HumanAIController), "HasDivingMask");
            if (m == null) DgLog.Warn("HumanAIController.HasDivingMask NOT FOUND");
            return m;
        }

        static bool Prefix(Character character, float conditionPercentage, bool requireOxygenTank, ref bool __result)
        {
            if (!requireOxygenTank) return true;
            var suit = SelfOxygen.GetWorn(character);
            if (suit == null) return true;
            if (suit.ConditionPercentage < conditionPercentage) return true;
            __result = true;
            return false;
        }
    }

    // 穿着自供氧装备时，AIObjectiveFindDivingGear.Act 整段唯一会做的事就是"给这件装备配氧气罐"（必然失败）。
    // 直接把 targetItem 指向身上这件、跳过 Act：AIObjective.CheckState() 随即判 CheckObjectiveState()（targetItem 已穿）
    // 为真 -> 目标当帧完成，不再建搬罐子子目标。反射拿不到成员就整类跳过，退回原行为。
    [HarmonyPatch]
    public static class FindDivingGearActPatch
    {
        static MethodBase TargetMethod()
        {
            var gearType = AccessTools.TypeByName("Barotrauma.AIObjectiveFindDivingGear");
            if (gearType == null)
            {
                DgLog.Warn("AIObjectiveFindDivingGear NOT FOUND");
                return null;
            }
            // 注入的 ___targetItem 得先确定能找到，免得 Harmony 在打补丁时才炸
            var targetItemField = AccessTools.Field(gearType, "targetItem");
            var act = AccessTools.Method(gearType, "Act");
            if (targetItemField == null || act == null || act.DeclaringType != gearType)
            {
                DgLog.Warn("AIObjectiveFindDivingGear members incomplete, patch skipped");
                return null;
            }
            return act;
        }

        // __instance 声明成基类 AIObjective（character 是它上面的 public 字段），省掉一次反射取值
        static bool Prefix(AIObjective __instance, ref Item ___targetItem)
        {
            var suit = SelfOxygen.GetWorn(__instance.character);
            if (suit == null) return true;
            ___targetItem = suit;
            return false;
        }
    }

    // 同一套判定的另一半：谓词本身要求"内含物品带 oxygensource 标签"。
    // 自供氧装备里装的是勾玉，但氧气确实由这件装备提供，所以它肚子里任何一件东西都算"氧气源"——
    // 这样 AI 手里拿着没穿的披纱时，Act 里那个 flag 会走"去穿上"的分支，而不是"先给它配氧气罐"。
    [HarmonyPatch]
    public static class ContainedOxygenSourcePatch
    {
        static MethodBase TargetMethod()
        {
            var gearType = AccessTools.TypeByName("Barotrauma.AIObjectiveFindDivingGear");
            var m = gearType == null ? null : AccessTools.Method(gearType, "IsSuitableContainedOxygenSource");
            if (m == null) DgLog.Warn("AIObjectiveFindDivingGear.IsSuitableContainedOxygenSource NOT FOUND");
            return m;
        }

        static void Postfix(Item item, ref bool __result)
        {
            if (__result || item == null) return;
            if (item.ParentInventory?.Owner is Item owner && owner.HasTag(SelfOxygen.Tag)) __result = true;
        }
    }

    // deepdivinglarge 是为了"披纱进不了潜水服柜"（vanilla 柜子 excludeditems=deepdivinglarge）而挂的，
    // 但 AIObjectiveGoTo 每 2 秒按这个标签查一次"身上这件动力服有没有电"：披纱没有 Powered 组件，
    // 于是被当成"动力服没电"——喊缺燃料，还想把燃料棒塞进勾玉槽（交换会回滚，不丢东西，但很傻）。
    // 只对这个查询把自供氧装备遮掉；真动力服不受影响（全程序集里只有这一处按 deepdivinglarge 查）。
    [HarmonyPatch]
    public static class ExosuitFuelPatch
    {
        static MethodBase TargetMethod()
        {
            var m = AccessTools.Method(typeof(Character), "GetEquippedItem");
            if (m == null) DgLog.Warn("Character.GetEquippedItem NOT FOUND");
            return m;
        }

        static void Postfix(Identifier __0, ref Item __result)
        {
            if (__result == null || !__result.HasTag(SelfOxygen.Tag)) return;
            if (__0 != Tags.PoweredDivingSuit) return;
            __result = null;
        }
    }
}
