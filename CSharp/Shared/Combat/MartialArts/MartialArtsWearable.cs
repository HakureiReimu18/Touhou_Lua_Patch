using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
#if CLIENT
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
#endif

namespace Touhou.MartialArts
{
    /// <summary>
    /// 武术武器的握持贴图；不占衣服槽，也不关闭武器刚体。
    ///
    /// 为什么不是直接挂原版 Wearable 组件：实测原版 Wearable 挂在武器上会干扰出招
    /// （加入就出不了拳，移除即恢复——槽位合并/输入路由/装备逻辑哪里冲突不重要，
    /// 重要的是不共存）。所以物品 XML 写 &lt;MartialArtsWearable&gt; 而不是 &lt;Wearable&gt;，
    /// 本组件内部构造一个"分离"的 Wearable 实例（不注册进 item.Components），
    /// 只借用原版的贴图/护甲解析，不调用它会关闭刚体的 Equip。
    /// </summary>
    public class MartialArtsWearable : ItemComponent
    {
        /// <summary>不进装备组件清单；贴图和护甲数据仍交给原版肢体管线读。</summary>
        public readonly Wearable DataHolder;
        private readonly List<DamageModifier> liveProtection;
        private readonly DamageModifier[] configuredProtection = Array.Empty<DamageModifier>();
        private bool protectionActive;
        private float refreshTimer;
        private bool warned;

        [Serialize(true, IsPropertySaveable.No)]
        public bool HideHeldSprite { get; set; }

        // 单只拳套按右侧画，换到左手时把整套手臂部件一起换过去。
        [Serialize(false, IsPropertySaveable.No)]
        public bool MatchHoldingHand { get; set; }

        [Serialize(true, IsPropertySaveable.No)]
        public bool DrawAboveClothes { get; set; }

        [Serialize(false, IsPropertySaveable.No)]
        public bool DrawAboveArm { get; set; }

        [Serialize(true, IsPropertySaveable.No)]
        public bool ProtectWhenHeld { get; set; }

        public MartialArtsWearable(Item item, ContentXElement element) : base(item, element)
        {
            try
            {
                // Wearable 构造器即原版 sprite 解析入口（wearableSprites 字段在此填充）
                DataHolder = new Wearable(item, element);
                liveProtection = DataHolder.DamageModifiers as List<DamageModifier>;
                if (liveProtection != null)
                {
                    configuredProtection = liveProtection.ToArray();
                    // 背包里的物品先不带生效护甲，真正握持时再还原。
                    liveProtection.Clear();
                }
                else
                {
                    DataHolder.Remove();
                    DataHolder = null;
                    WarnOnce($"握持护具停用（{item.Name}）：原版护甲容器类型已变化");
                }
            }
            catch (Exception e)
            {
                StrikeManager.Log($"MartialArtsWearable：贴图数据解析失败（{item.Name}）：{e.Message}", true);
            }
        }

        internal void WarnOnce(string message)
        {
            if (warned) return;
            warned = true;
            StrikeManager.Log(message, true);
        }

        public override void Equip(Character character)
        {
            IsActive = true;
            refreshTimer = 0f;
            TryAttach(character);
        }

        public override void Unequip(Character character)
        {
            SetProtection(false);
            MartialWearableManager.Detach(item);
            IsActive = false;
        }

        public override void Drop(Character dropper, bool setTransform = true) => Unequip(dropper);

        public override void Update(float deltaTime, Camera cam)
        {
            RefreshProtection();
            refreshTimer -= deltaTime;
            if (refreshTimer > 0f) return;
            refreshTimer = 0.2f;
            var wearer = item.ParentInventory?.Owner as Character;
            if (wearer == null || !MartialWearableManager.IsHeld(item, wearer))
            {
                Unequip(wearer);
                return;
            }
            // 换手、远端装备和体型重建都在这里对账，没变化时不重新挂。
            TryAttach(wearer);
        }

        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

        void TryAttach(Character wearer)
        {
            try { MartialWearableManager.Attach(item, wearer); }
            catch (Exception e)
            {
                MartialWearableManager.Detach(item);
                WarnOnce($"握持贴图停用（{item.Name}）：{e.Message}");
            }
        }

        public override void RemoveComponentSpecific()
        {
            SetProtection(false);
            MartialWearableManager.Detach(item);
            DataHolder?.Remove();
            base.RemoveComponentSpecific();
        }

        internal void RefreshProtection(Character expectedWearer = null)
        {
            var wearer = item.ParentInventory?.Owner as Character;
            SetProtection(MartialWearableManager.Enabled && ProtectWhenHeld && item.Condition > 0f
                && (expectedWearer == null || wearer == expectedWearer)
                && MartialWearableManager.IsHeld(item, wearer));
        }

        internal void SetProtection(bool enabled)
        {
            if (liveProtection == null || protectionActive == enabled) return;
            liveProtection.Clear();
            if (enabled) liveProtection.AddRange(configuredProtection);
            protectionActive = enabled;
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
        sealed class Attachment
        {
            public Character Wearer;
            public MartialArtsWearable Component;
            public bool LeftHand;
            public readonly List<(Limb Limb, WearableSprite Sprite)> Bindings = new();
        }

        static readonly Dictionary<Item, Attachment> attached = new();
        internal static bool Enabled = true;

        static bool EnsureReflection()
        {
            if (reflectionReady) return true;
            if (reflectionAttempted) return false;
            reflectionAttempted = true;
            spritesField = typeof(Wearable).GetField("wearableSprites",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            updateWearableHideM = typeof(Limb).GetMethod("UpdateWearableTypesToHide",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            reflectionReady = spritesField != null;
            if (!reflectionReady)
                StrikeManager.Log("MartialWearable：找不到 Wearable.wearableSprites 字段，握持贴图停用", true);
            return reflectionReady;
        }

        /// <summary>装备到手槽时挂载（重复调用安全：先摘再挂）。仅武术武器。</summary>
        public static void Attach(Item item, Character c)
        {
            if (item == null) return;
            if (!Enabled || !IsHeld(item, c))
            {
                Detach(item);
                return;
            }
            if (!EnsureReflection()) return;
            var comp = item.GetComponent<MartialArtsWearable>();
            var holder = comp?.DataHolder;
            if (holder == null) return;
            comp.RefreshProtection(c);
            if (spritesField.GetValue(holder) is not Array sprites || sprites.Length == 0) return;

            bool left = !c.Inventory.IsInLimbSlot(item, InvSlotType.RightHand)
                && c.Inventory.IsInLimbSlot(item, InvSlotType.LeftHand);
            if (attached.TryGetValue(item, out var current)
                && current.Wearer == c && current.LeftHand == left
                && current.Bindings.All(b => b.Limb != null && !b.Limb.Removed
                    && c.AnimController.GetLimb(TargetLimb(b.Sprite.Limb, comp.MatchHoldingHand && left)) == b.Limb
                    && b.Limb.WearingItems.Contains(b.Sprite)))
                return;

            Detach(item);

            var entry = new Attachment { Wearer = c, Component = comp, LeftHand = left };
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites.GetValue(i) is not WearableSprite ws) continue;
                try
                {
                    ws.Picker = c;
                    ws.Init(c);
                    // Limb 和隐藏列表都在 Init 里才有值，不能提前找肢体。
                    if (!ws.IsInitialized || ws.HideWearablesOfType == null || ws.Sprite == null) continue;
#if CLIENT
                    // 素材没加载成功时保留普通手持图，别把物品画成空气。
                    if (ws.Sprite.Texture == null) continue;
#endif
                    Limb limb = c.AnimController.GetLimb(TargetLimb(ws.Limb, comp.MatchHoldingHand && left));
                    if (limb == null || limb.Removed) continue;
                    if (!limb.WearingItems.Contains(ws))
                    {
                        limb.WearingItems.Add(ws);
                        // Equip 同款：按贴图深度排序，保证多层穿戴遮挡正确
                        limb.WearingItems.Sort((a, b) =>
                            (b.Sprite?.Depth ?? 0f).CompareTo(a.Sprite?.Depth ?? 0f));
                    }
                    // 先记账，后面的遮挡刷新即使失败，也能把已加入的贴图摘掉。
                    entry.Bindings.Add((limb, ws));
                    updateWearableHideM?.Invoke(limb, null);
                    Touhou.WearableOrder.WearableDrawOrder.SortLimb(limb);
                }
                catch (Exception e) { comp.WarnOnce($"握持贴图挂载异常（{item.Name}）：{e.Message}"); }
            }
            if (entry.Bindings.Count > 0)
            {
                attached[item] = entry;
                comp.RefreshProtection(c);
                try { c.OnWearablesChanged(); } catch { }
            }
            else comp.SetProtection(false);
        }

        internal static bool IsHeld(Item item, Character c) =>
            item != null && !item.Removed && c != null && !c.Removed
            && c.Inventory != null && c.AnimController != null && c.HeldItems.Contains(item);

        internal static LimbType TargetLimb(LimbType limb, bool left) => !left ? limb : limb switch
        {
            LimbType.RightHand => LimbType.LeftHand,
            LimbType.RightForearm => LimbType.LeftForearm,
            LimbType.RightArm => LimbType.LeftArm,
            _ => limb,
        };

        internal static bool HideWorldSprite(Item item)
        {
            if (!Enabled || item?.GetComponent<MartialArtsWearable>()?.HideHeldSprite != true) return false;
            return attached.TryGetValue(item, out var entry) && IsHeld(item, entry.Wearer)
                && entry.Bindings.Any(b => b.Limb != null && !b.Limb.Removed
                    && b.Limb.WearingItems.Contains(b.Sprite));
        }

        /// <summary>放下/收起时摘除</summary>
        public static void Detach(Item item)
        {
            if (item == null) return;
            if (!attached.TryGetValue(item, out var entry)) return;
            attached.Remove(item);
            entry.Component.SetProtection(false);
            foreach (var binding in entry.Bindings)
            {
                var limb = binding.Limb;
                if (limb == null || limb.Removed) continue;
                try
                {
                    limb.WearingItems.RemoveAll(s => ReferenceEquals(s, binding.Sprite));
                    updateWearableHideM?.Invoke(limb, null);
                }
                catch { }
            }
            try { entry.Wearer?.OnWearablesChanged(); } catch { }
        }

        public static void ClearAll()
        {
            foreach (var item in attached.Keys.ToArray()) Detach(item);
        }
    }

    // 只同步护甲开关，不接管伤害计算；漏掉收起回调时也不能白吃半拍护甲。
    [HarmonyPatch]
    public static class MartialWearableProtectionPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Limb), "AddDamage",
            new[] { typeof(Microsoft.Xna.Framework.Vector2), typeof(IEnumerable<Affliction>),
                typeof(bool), typeof(float), typeof(float), typeof(Character) });

        static void Prefix(Limb __instance)
        {
            foreach (var sprite in __instance.WearingItems)
            {
                var holder = sprite?.WearableComponent;
                var component = holder?.Item?.GetComponent<MartialArtsWearable>();
                if (component == null || !ReferenceEquals(component.DataHolder, holder)) continue;
                try { component.RefreshProtection(__instance.character); }
                catch (Exception e)
                {
                    component.SetProtection(false);
                    component.WarnOnce($"握持护甲已回退（{component.item.Name}）：{e.Message}");
                }
            }
        }
    }

#if CLIENT
    // 大拳套仍挂在手上，只把画面深度提到同侧手臂之前，不添护甲部位。
    public static class MartialWearableArmDepthPatch
    {
        const float LayerStep = 0.000001f;
        [ThreadStatic] static bool resolvingDepth;

        static MethodBase TargetMethod() => AccessTools.Method(typeof(Limb), "CalculateDrawParameters",
            new[] { typeof(WearableSprite), typeof(float), typeof(Color), typeof(float) });

        public static void TryInstall(Harmony harmony)
        {
            try
            {
                var target = TargetMethod();
                if (target == null) { StrikeManager.Log("找不到穿戴深度入口，拳套跨肢体修正停用", true); return; }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(MartialWearableArmDepthPatch), nameof(Postfix)));
            }
            catch (Exception e) { StrikeManager.Log($"拳套跨肢体修正未挂载：{e.Message}", true); }
        }

        static void Postfix(Limb __instance, WearableSprite wearable, Color color, float alpha,
            ref (Color FinalColor, Vector2 Origin, float Rotation, float Scale, float Depth) __result)
        {
            if (resolvingDepth || !MartialWearableManager.Enabled || !float.IsFinite(__result.Depth)) return;
            bool left = __instance.type == LimbType.LeftHand;
            if (!left && __instance.type != LimbType.RightHand) return;
            var holder = wearable?.WearableComponent;
            var component = holder?.Item?.GetComponent<MartialArtsWearable>();
            if (component?.DrawAboveArm != true || !component.DrawAboveClothes
                || !ReferenceEquals(component.DataHolder, holder)
                || !MartialWearableManager.IsHeld(component.item, __instance.character)) return;
            try
            {
                resolvingDepth = true;
                float front = __result.Depth;
                var ac = __instance.character.AnimController;
                IncludeLimb(__instance, holder, color, alpha, ref front);
                IncludeLimb(ac.GetLimb(left ? LimbType.LeftForearm : LimbType.RightForearm), holder, color, alpha, ref front);
                IncludeLimb(ac.GetLimb(left ? LimbType.LeftArm : LimbType.RightArm), holder, color, alpha, ref front);
                __result.Depth = Math.Max(0f, front - LayerStep);
            }
            catch (Exception e) { component.WarnOnce($"拳套跨肢体深度已回退：{e.Message}"); }
            finally { resolvingDepth = false; }
        }

        static void IncludeLimb(Limb limb, Wearable ownHolder, Color color, float alpha, ref float front)
        {
            if (limb == null || limb.Removed || limb.ActiveSprite == null) return;
            // 原版条件肢体层在基础深度前移 50 个步长；其它穿戴按片数递增。
            Include(limb.ActiveSprite.Depth - LayerStep * 50f, ref front);
            float step = LayerStep * (limb.WearingItems.Count + limb.OtherWearables.Count + 3);
            foreach (var sprite in limb.WearingItems)
                IncludeWearable(limb, sprite, ownHolder, step, color, alpha, ref front);
            foreach (var sprite in limb.OtherWearables)
                IncludeWearable(limb, sprite, ownHolder, step, color, alpha, ref front);
        }

        static void IncludeWearable(Limb limb, WearableSprite sprite, Wearable ownHolder,
            float step, Color color, float alpha, ref float front)
        {
            if (sprite?.Sprite == null || ReferenceEquals(sprite.WearableComponent, ownHolder)) return;
            // 复用原版的 depthlimb / 外套 / 背包偏移；递归守卫避免再次调整参照层。
            Include(limb.CalculateDrawParameters(sprite, step, color, alpha).Depth, ref front);
        }

        static void Include(float depth, ref float front)
        {
            if (float.IsFinite(depth)) front = Math.Min(front, depth);
        }
    }

    // 只拦场景绘制，不改 HiddenInGame、刚体或背包图标。
    [HarmonyPatch]
    public static class MartialWearableDrawPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(Item), "Draw",
            new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) });

        static bool Prefix(Item __instance, bool editing)
        {
            try { return editing || !MartialWearableManager.HideWorldSprite(__instance); }
            catch { return true; }
        }
    }
#endif
}
