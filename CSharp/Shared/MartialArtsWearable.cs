using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.MartialArts
{
    /// <summary>
    /// 武术武器的"握持穿戴贴图"数据组件：纯数据载体，不做任何行为。
    ///
    /// 为什么不是直接挂原版 Wearable 组件：实测原版 Wearable 挂在武器上会干扰出招
    /// （加入就出不了拳，移除即恢复——槽位合并/输入路由/装备逻辑哪里冲突不重要，
    /// 重要的是不共存）。所以物品 XML 写 &lt;MartialArtsWearable&gt; 而不是 &lt;Wearable&gt;，
    /// 本组件内部构造一个"分离"的 Wearable 实例（不注册进 item.Components），
    /// 只为复用原版构造器对 &lt;sprite&gt; 子元素的解析——贴图格式与原版穿戴完全一致。
    /// </summary>
    public class MartialArtsWearable : ItemComponent
    {
        /// <summary>分离的 Wearable 实例：只解析 sprite 数据，不参与任何游戏系统</summary>
        public readonly Wearable DataHolder;

        public MartialArtsWearable(Item item, ContentXElement element) : base(item, element)
        {
            try
            {
                // Wearable 构造器即原版 sprite 解析入口（wearableSprites 字段在此填充）
                DataHolder = new Wearable(item, element);
            }
            catch (Exception e)
            {
                StrikeManager.Log($"MartialArtsWearable：贴图数据解析失败（{item.Name}）：{e.Message}", true);
            }
        }
    }

    /// <summary>
    /// 握持贴图的挂载/摘除管理器。挂载配方逐条复刻原版 Wearable.Equip/Unequip：
    /// Init(picker) → Picker= → 按 sprite 的 limb（LimbType）解析肢体 → 加入
    /// Limb.WearingItems → 按 Sprite.Depth 排序 → UpdateWearableTypesToHide →
    /// OnWearablesChanged。渲染走原版管线（翻转/水深/遮罩/隐藏规则全自动一致）。
    /// </summary>
    public static class MartialWearableManager
    {
        // Wearable 的私有字段（sprite 数组从分离实例里取）
        static FieldInfo spritesField;   // Wearable.wearableSprites (WearableSprite[])
        static MethodInfo updateWearableHideM;  // Limb.UpdateWearableTypesToHide——仅客户端程序集存在（遮挡规则是渲染层概念），服务端为 null
        static bool reflectionAttempted, reflectionReady;

        /// <summary>已挂载记录：物品 →（挂载者， 碰过的肢体）——摘除/清理用</summary>
        static readonly Dictionary<Item, (Character Wearer, List<Limb> Limbs)> attached = new();

        static bool EnsureReflection()
        {
            if (reflectionReady) return true;
            if (reflectionAttempted) return false;
            reflectionAttempted = true;
            spritesField = typeof(Wearable).GetField("wearableSprites",
                BindingFlags.NonPublic | BindingFlags.Instance);
            updateWearableHideM = typeof(Limb).GetMethod("UpdateWearableTypesToHide",
                BindingFlags.Public | BindingFlags.Instance);
            reflectionReady = spritesField != null;
            if (!reflectionReady)
                StrikeManager.Log("MartialWearable：找不到 Wearable.wearableSprites 字段，握持贴图停用", true);
            return reflectionReady;
        }

        /// <summary>装备到手槽时挂载（重复调用安全：先摘再挂）。仅武术武器。</summary>
        public static void Attach(Item item, Character c)
        {
            if (item == null || c == null || c.Removed || c.AnimController == null) return;
            if (!EnsureReflection()) return;
            var comp = item.GetComponent<MartialArtsWearable>();
            var holder = comp?.DataHolder;
            if (holder == null) return;
            if (spritesField.GetValue(holder) is not Array sprites || sprites.Length == 0) return;

            Detach(item); // 幂等：防重复挂载

            var touched = new List<Limb>();
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites.GetValue(i) is not WearableSprite ws) continue;
                Limb limb = c.AnimController.GetLimb(ws.Limb); // ws.Limb 是 LimbType
                if (limb == null || limb.Removed) continue;
                try
                {
                    ws.Init(c);
                    ws.Picker = c;
                    if (!limb.WearingItems.Contains(ws))
                    {
                        limb.WearingItems.Add(ws);
                        // Equip 同款：按贴图深度排序，保证多层穿戴遮挡正确
                        limb.WearingItems.Sort((a, b) =>
                            (a.Sprite?.Depth ?? 0f).CompareTo(b.Sprite?.Depth ?? 0f));
                    }
                    updateWearableHideM?.Invoke(limb, null);
                    if (!touched.Contains(limb)) touched.Add(limb);
                }
                catch (Exception e) { StrikeManager.Log($"握持贴图挂载单贴图异常：{e.Message}", true); }
            }
            if (touched.Count > 0)
            {
                attached[item] = (c, touched);
                try { c.OnWearablesChanged(); } catch { }
            }
        }

        /// <summary>放下/收起时摘除</summary>
        public static void Detach(Item item)
        {
            if (item == null) return;
            if (!attached.TryGetValue(item, out var entry)) return;
            attached.Remove(item);
            var comp = item.GetComponent<MartialArtsWearable>();
            Array sprites = null;
            if (comp?.DataHolder != null && spritesField != null)
                sprites = spritesField.GetValue(comp.DataHolder) as Array;

            foreach (var limb in entry.Limbs)
            {
                if (limb == null || limb.Removed) continue;
                try
                {
                    if (sprites != null)
                    {
                        for (int i = 0; i < sprites.Length; i++)
                        {
                            if (sprites.GetValue(i) is WearableSprite ws)
                                limb.WearingItems.RemoveAll(s => ReferenceEquals(s, ws));
                        }
                    }
                    updateWearableHideM?.Invoke(limb, null);
                }
                catch { }
            }
            try { entry.Wearer?.OnWearablesChanged(); } catch { }
        }

        /// <summary>巡回结束清理（肢体随角色卸载一起消失，这里只清记录）</summary>
        public static void ClearAll() => attached.Clear();
    }

    /// <summary>装备到手槽 → 挂载握持贴图（仅武术武器）</summary>
    [HarmonyPatch]
    public static class MartialWearableEquipPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(MeleeWeapon), "Equip");

        static void Postfix(MeleeWeapon __instance, Character character)
        {
            try
            {
                if (StrikeManager.ReadWeaponConfig(__instance.item, out _, out _))
                    MartialWearableManager.Attach(__instance.item, character);
            }
            catch (Exception e) { StrikeManager.Log($"握持贴图挂载异常：{e.Message}", true); }
        }
    }

    /// <summary>放下/收起 → 摘除握持贴图</summary>
    [HarmonyPatch]
    public static class MartialWearableDropPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(MeleeWeapon), "Drop");

        static void Postfix(MeleeWeapon __instance, Character dropper)
        {
            try { MartialWearableManager.Detach(__instance.item); }
            catch (Exception e) { StrikeManager.Log($"握持贴图摘除异常：{e.Message}", true); }
        }
    }
}
