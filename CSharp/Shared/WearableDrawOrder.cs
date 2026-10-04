using Barotrauma;

#if CLIENT
using HarmonyLib;
using Microsoft.Xna.Framework;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Touhou.Affixes;   // ReflectionCache 定义在 Touhou.Affixes（HarmonyPatches.cs）
#endif

namespace Touhou.WearableOrder
{
    // 穿戴贴图绘制顺序修正：让头饰（Head 槽）画在装束（InnerClothes 等）上方。
    //
    // 引擎规则（Barotrauma.Limb.Draw / Barotrauma.Items.Components.Wearable.Equip）：
    //  - Limb.Draw 按 WearingItems 列表顺序逐个绘制，列表越靠后的贴图减掉的 depthStep 越多、画得越靠前；
    //  - Wearable.Equip 插入新贴图后只排序两次：按原始 sprite depth 降序、再按是否外套槽（OuterClothes）排。
    //    两个键都相同的贴图保持插入顺序 —— 即"先穿的在下、后穿的在上"。
    //  - 装束的 Head 部件和帽子通常都不写 depth、同样继承肢体深度（inheritlimbdepth 默认 true），排序键
    //    完全相同，上下关系于是完全由穿脱顺序决定。而存档加载 / 布娃娃重建（RagdollScale 换参数等）会按
    //    背包槽位顺序重新装备，人类槽位里 Head 排在 InnerClothes 之前（Human.xml 的 Slots 顺序），
    //    装束反而排到帽子之后 = 帽子被装束盖住。
    //
    // 修正：引擎每次增删穿戴贴图（Limb.UpdateWearableTypesToHide）后把该肢体的 WearingItems 稳定重排为
    //     [其它] → [Head 槽头饰] → [外套槽]，组内沿用原版第一个比较器（原始 depth 降序）。
    // 头饰排在装束之后 = 绘制在装束上方；外套槽保持原版"最外层"语义不变。稳定排序保证同组内相同键的
    // 贴图仍维持原顺序（含穿脱顺序），两个头饰之间的相对关系不受影响。
    //
    // 整个实现只在 CLIENT 编译：服务端程序集（DedicatedServer.dll）的 Limb 里没有
    // UpdateWearableTypesToHide / DrawWearable 这套客户端专有成员（Wearable.Equip 在服务端也不调它），
    // 绘制顺序对服务端没有任何意义。
    // 补丁由 Mod 显式安装（TryInstall）而不是 [HarmonyPatch] 特性：本程序集里别的插件会用
    // Harmony.PatchAll 全程序集扫补丁，特性类一旦目标解析失败（目标缺失/游戏更新改名）就会在
    // 他们的 PatchAll 里抛异常、连带中断别的插件的补丁注册——本次报错就是踩了这个坑；
    // 显式安装自己判空，失败只记一条日志、不影响任何其它插件。
    public static class WearableDrawOrder
    {
        // 稳定重排一个肢体的穿戴贴图列表（LINQ OrderBy/ThenBy 稳定）；顺序已正确时不做任何事。
        // 服务端为空实现（无绘制，见上方说明）。
        public static void SortLimb(Limb limb)
        {
#if CLIENT
            if (limb == null || !EnsureReflection()) return;
            if (limbWearingItemsField.GetValue(limb) is not IList list || list.Count < 2) return;

            int prevGroup = int.MinValue;
            float prevDepth = float.MaxValue;
            foreach (var ws in list)
            {
                int group = GetDrawGroup(ws);
                float depth = GetSpriteDepth(ws);
                if (group < prevGroup || (group == prevGroup && depth > prevDepth))
                {
                    var ordered = list.Cast<object>()
                        .OrderBy(GetDrawGroup)
                        .ThenByDescending(GetSpriteDepth)
                        .ToArray();
                    list.Clear();
                    foreach (var item in ordered) list.Add(item);
                    return;
                }
                prevGroup = group;
                prevDepth = depth;
            }
#endif
        }

#if CLIENT
        static bool reflectionAttempted;
        static bool reflectionReady;
        static FieldInfo limbWearingItemsField;   // Limb.WearingItems（internal 泛型 List，反射只能当 IList 用）
        static PropertyInfo wsWearableCompProp;   // WearableSprite.WearableComponent
        static PropertyInfo wsSpriteProp;         // WearableSprite.Sprite
        static PropertyInfo spriteDepthProp;      // Sprite.Depth（原版 Equip 排序第一键）

        static bool EnsureReflection()
        {
            if (reflectionReady) return true;
            if (reflectionAttempted) return false;
            reflectionAttempted = true;
            try
            {
                limbWearingItemsField = typeof(Limb).GetField("WearingItems",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var wsType = ReflectionCache.WearableType?.GetNestedType("WearableSprite",
                        BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(Character).Assembly.GetType("Barotrauma.WearableSprite");
                wsWearableCompProp = wsType?.GetProperty("WearableComponent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                wsSpriteProp = wsType?.GetProperty("Sprite");
                spriteDepthProp = wsSpriteProp?.PropertyType.GetProperty("Depth");
                if (limbWearingItemsField == null || wsWearableCompProp == null || spriteDepthProp == null)
                {
                    WOLog.Warn("reflection members missing, wearable draw-order fix disabled");
                    return false;
                }
                reflectionReady = true;
                WOLog.Info("ready: head-slot wearables drawn above other wearables on the same limb");
            }
            catch (Exception ex)
            {
                WOLog.Warn($"reflection init failed, draw-order fix disabled: {ex.Message}");
            }
            return reflectionReady;
        }

        // 绘制分组：0 = 其它（InnerClothes/背包等，靠列表前段 = 画得靠后），1 = Head 槽头饰，2 = 外套槽（最外层）
        static int GetDrawGroup(object wearableSprite)
        {
            if (wsWearableCompProp?.GetValue(wearableSprite) is not Barotrauma.Items.Components.Pickable comp) return 0;
            var slots = comp.AllowedSlots;
            if (slots == null) return 0;
            if (slots.Contains(InvSlotType.OuterClothes)) return 2;
            if (slots.Contains(InvSlotType.Head)) return 1;
            return 0;
        }

        // 组内次序键：贴图原始 depth（原版 Equip 排序第一键，越大画得越靠后）；空贴图按 0
        static float GetSpriteDepth(object wearableSprite)
        {
            try
            {
                object sprite = wsSpriteProp?.GetValue(wearableSprite);
                if (sprite == null || spriteDepthProp == null) return 0f;
                return spriteDepthProp.GetValue(sprite) is float d ? d : 0f;
            }
            catch { return 0f; }
        }
#endif
    }

#if CLIENT
    static class WOLog
    {
        public static void Info(object msg) => LuaCsLogger.LogMessage($"[Touhou.WearableOrder] {msg ?? "null"}", Color.LightGreen);
        public static void Warn(object msg) => LuaCsLogger.LogMessage($"[Touhou.WearableOrder] {msg ?? "null"}", Color.Orange);
    }

    // 挂钩：引擎每次增删穿戴贴图都会调用 Limb.UpdateWearableTypesToHide（Equip 加完、Unequip 移除后各一次），
    // 在这里重排列表即可覆盖所有路径：正常穿脱、存档加载、RagdollScale 的布娃娃重建修复。
    // 不用 [HarmonyPatch] 特性，由 Mod.PatchOwnNamespaces 调 TryInstall 显式安装（原因见文件头注释）。
    public static class WearableDrawOrderPatch
    {
        public static void TryInstall(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                var target = typeof(Limb).GetMethod("UpdateWearableTypesToHide",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (target == null)
                {
                    WOLog.Warn("Limb.UpdateWearableTypesToHide NOT FOUND, draw-order fix disabled");
                    return;
                }
                var postfix = typeof(WearableDrawOrderPatch).GetMethod("Postfix",
                    BindingFlags.NonPublic | BindingFlags.Static);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                WOLog.Info("patch installed");
            }
            catch (Exception ex)
            {
                WOLog.Warn($"patch install failed, draw-order fix disabled: {ex.Message}");
            }
        }

        static void Postfix(Limb __instance) => WearableDrawOrder.SortLimb(__instance);
    }
#endif
}
