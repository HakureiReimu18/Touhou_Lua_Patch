using Barotrauma;
using HarmonyLib;
using Barotrauma.LuaCs;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Touhou.Affixes;   // ReflectionCache 定义在 Touhou.Affixes（HarmonyPatches.cs）

namespace Touhou.RagdollScale
{
    // 体型缩放：穿带 "charscale_系数" 标签的装备（如 charscale_0.8）时，克隆一份独立的
    // RagdollParams 乘算 LimbScale/JointScale 后 Recreate。只改尺寸不动拓扑，贴图跟 LimbScale 走。
    // 绝不改共享缓存实例（全人类共用的）；还原就是 Recreate 回原始参数，不写存档，新回合对账自动重挂。
    // 本系统的日志前缀（与词缀系统 Touhou.Affixes 区分）
    static class RSLog
    {
        public static void Info(object msg) => LuaCsLogger.LogMessage($"[Touhou.RagdollScale] {msg ?? "null"}", Color.LightGreen);
        public static void Warn(object msg) => LuaCsLogger.LogMessage($"[Touhou.RagdollScale] {msg ?? "null"}", Color.Orange);
    }

    public static class RagdollScaleManager
    {
        public const string ScaleTagPrefix = "charscale_";

        class ScaleState
        {
            public object OriginalParams;   // 首次应用前的 RagdollParams（通常是物种共享默认实例）
            public float BaseLimbScale;
            public float BaseJointScale;
            public float CurrentFactor = 1f;
            public double NextRetryTime;    // 失败退避，免得对账心跳每秒刷错误日志
        }

        static readonly Dictionary<Character, ScaleState> states = new();

        static bool reflectionAttempted;
        static bool reflectionReady;
        static PropertyInfo ragdollParamsProp;      // Ragdoll 上的
        static PropertyInfo limbScaleProp;
        static PropertyInfo jointScaleProp;
        static PropertyInfo speciesNameProp;        // （private set）
        static PropertyInfo pathProp;               // EditableParams.Path
        static PropertyInfo mainElementProp;        // EditableParams.MainElement
        static FieldInfo docField;                  // EditableParams.doc
        static FieldInfo variantAppliedField;       // isVariantScaleApplied（private）
        static MethodInfo deserializeMethod;        // Deserialize(XElement, bool, bool)
        static MethodInfo loadMethod;               // Load(ContentPath, Identifier)
        static PropertyInfo textureScaleProp;
        static PropertyInfo sourceRectScaleProp;
        static PropertyInfo collidersProp;          // 调试用
        static FieldInfo lightParentBodyField;      // LightComponent.ParentBody（字段，不是属性）
        static FieldInfo wearableSpritesField;      // Wearable.wearableSprites
        static PropertyInfo wsLightComponentsProp;
        static PropertyInfo wsLimbProp;
        static FieldInfo wearableLimbTypesField;    // LimbType[]，与 sprites 一一对应
        static FieldInfo wearableLimbsField;        // Equip 时缓存的肢体引用
        static FieldInfo limbWearingItemsField;     // Limb.WearingItems
        static PropertyInfo wsSpriteProp;
        static PropertyInfo spriteDepthProp;        // WearingItems 排序依据
        static PropertyInfo wsWearableCompProp;
        static MethodInfo updateWearableHideM;
        static Type huskAfflictionType;
        static FieldInfo huskAppendageField;
        static MethodInfo createCollidersM, createLimbsM, createJointsM; // protected
        static MethodInfo recreateRespawnMethod;    // AnimController.RecreateAndRespawn：官方重建法，会保留世界坐标
        static readonly Dictionary<Type, MethodInfo> getDefaultMethods = new(); // 各参数类型的 GetDefaultRagdollParams(Character)

        // 沿继承链向上找成员（Ragdoll 可能是 internal，得从 AnimController 基类链上找）
        static Type FindTypeInHierarchy(Type start, string memberName, MemberKinds kind)
        {
            for (var t = start; t != null; t = t.BaseType)
            {
                bool found = kind switch
                {
                    MemberKinds.Property => t.GetProperty(memberName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null,
                    MemberKinds.Method => t.GetMethod(memberName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null,
                    _ => t.GetField(memberName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null,
                };
                if (found) return t;
            }
            return null;
        }
        enum MemberKinds { Property, Method, Field }

        static bool EnsureReflection()
        {
            if (reflectionReady) return true;
            if (reflectionAttempted) return false;
            reflectionAttempted = true;
            try
            {
                // Ragdoll 类型：AnimController 基类链上持有 RagdollParams 属性的那一层
                var ragdollType = FindTypeInHierarchy(typeof(AnimController), "RagdollParams", MemberKinds.Property);
                if (ragdollType == null) throw new Exception("Ragdoll type not found");
                ragdollParamsProp = ragdollType.GetProperty("RagdollParams",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var ragdollParamsType = ragdollParamsProp.PropertyType;

                limbScaleProp = ragdollParamsType.GetProperty("LimbScale");
                jointScaleProp = ragdollParamsType.GetProperty("JointScale");
                speciesNameProp = ragdollParamsType.GetProperty("SpeciesName",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                deserializeMethod = ragdollParamsType.GetMethod("Deserialize",
                    BindingFlags.Public | BindingFlags.Instance);
                textureScaleProp = ragdollParamsType.GetProperty("TextureScale");
                sourceRectScaleProp = ragdollParamsType.GetProperty("SourceRectScale");
                collidersProp = ragdollParamsType.GetProperty("Colliders");
                variantAppliedField = ragdollParamsType.GetField("isVariantScaleApplied",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                // Load(ContentPath, Identifier)：两参数的那个重载
                foreach (var m in ragdollParamsType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name != "Load") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2) { loadMethod = m; break; }
                }
                createCollidersM = ragdollParamsType.GetMethod("CreateColliders",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                createLimbsM = ragdollParamsType.GetMethod("CreateLimbs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                createJointsM = ragdollParamsType.GetMethod("CreateJoints",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                // 基类 EditableParams 上的成员
                var editableType = FindTypeInHierarchy(ragdollParamsType, "MainElement", MemberKinds.Property);
                mainElementProp = editableType?.GetProperty("MainElement",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                pathProp = editableType?.GetProperty("Path",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                docField = editableType?.GetField("doc",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                recreateRespawnMethod = typeof(AnimController).GetMethod("RecreateAndRespawn",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { ragdollParamsType }, null);

                // loadMethod 和 doc 克隆两组至少得成一个：前者从布娃娃文件重新加载（主路径），
                // 后者从内存 XML 克隆（兜底，有些参数实例的 MainElement 是空的）
                bool loadReady = loadMethod != null;
                bool docCloneReady = deserializeMethod != null && createCollidersM != null &&
                    createLimbsM != null && createJointsM != null && mainElementProp != null && docField != null;
                if (limbScaleProp == null || jointScaleProp == null || speciesNameProp == null ||
                    pathProp == null || recreateRespawnMethod == null || (!loadReady && !docCloneReady))
                {
                    throw new Exception("one or more members missing");
                }
                reflectionReady = true;
                RSLog.Info("reflection cache ready");

                // 修复用反射（可选，失败只停用对应修复）
                var asm = typeof(Character).Assembly;
                var lightCompType = asm.GetType("Barotrauma.Items.Components.LightComponent");
                lightParentBodyField = lightCompType?.GetField("ParentBody",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                wearableSpritesField = ReflectionCache.WearableType?.GetField("wearableSprites",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var wsType = ReflectionCache.WearableType?.GetNestedType("WearableSprite",
                    BindingFlags.Public | BindingFlags.NonPublic)
                    ?? asm.GetType("Barotrauma.WearableSprite");
                wsLightComponentsProp = wsType?.GetProperty("LightComponents");
                wsLimbProp = wsType?.GetProperty("Limb");
                wearableLimbTypesField = ReflectionCache.WearableType?.GetField("limbType",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                wearableLimbsField = ReflectionCache.WearableType?.GetField("limb",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                limbWearingItemsField = typeof(Limb).GetField("WearingItems");
                wsSpriteProp = wsType?.GetProperty("Sprite");
                spriteDepthProp = wsSpriteProp?.PropertyType.GetProperty("Depth");
                wsWearableCompProp = wsType?.GetProperty("WearableComponent");
                updateWearableHideM = typeof(Limb).GetMethod("UpdateWearableTypesToHide");
                huskAfflictionType = asm.GetType("Barotrauma.AfflictionHusk");
                huskAppendageField = huskAfflictionType?.GetField("huskAppendage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (lightParentBodyField == null || wearableSpritesField == null || wsLightComponentsProp == null)
                    RSLog.Warn("wearable light rebind members incomplete, light fix disabled");
                if (wearableLimbTypesField == null || limbWearingItemsField == null)
                    RSLog.Warn("wearable re-register members incomplete, visibility fix disabled");
                if (huskAppendageField == null)
                    RSLog.Warn("huskAppendage field not found, appendage fix disabled");
            }
            catch (Exception ex)
            {
                RSLog.Warn($"reflection init failed, scaling disabled: {ex.Message}");
            }
            return reflectionReady;
        }

        // 单件装备的缩放系数（无标签=1）。prefab 标签优先，prefab 没有才看实例标签——
        // 不然存档里残留的旧 charscale 标签会和改完 XML 的新标签一起被叠乘。
        // 系数缓存，物品移动时失效
        static readonly Dictionary<Item, float> itemFactorCache = new();

        // 物品移动（穿脱/换格）时调用，使其系数缓存失效
        public static void InvalidateItemFactor(Item item)
        {
            if (item != null) itemFactorCache.Remove(item);
        }

        static float GetItemScaleFactor(Item item)
        {
            if (itemFactorCache.TryGetValue(item, out float cached)) return cached;
            float f = ComputeItemScaleFactor(item);
            itemFactorCache[item] = f;
            return f;
        }

        static float ComputeItemScaleFactor(Item item)
        {
            // prefab 标签（XML 当前值）优先
            if (item.Prefab?.Tags != null)
            {
                foreach (var tag in item.Prefab.Tags)
                {
                    if (TryParseScaleTag(tag.Value, out float f)) return f;
                }
            }
            // prefab 没有缩放标签时才看实例标签（只取第一个，忽略可能的重复残留）
            foreach (var tag in item.GetTags())
            {
                if (TryParseScaleTag(tag.Value, out float f)) return f;
            }
            return 1f;
        }

        // 解析单个 charscale_ 标签；无效/非缩放标签返回 false
        static bool TryParseScaleTag(string tag, out float factor)
        {
            factor = 1f;
            if (string.IsNullOrEmpty(tag) ||
                !tag.StartsWith(ScaleTagPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (!float.TryParse(tag.Substring(ScaleTagPrefix.Length),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f) || f <= 0f) return false;
            factor = f;
            return true;
        }

        // 当前穿戴的总缩放系数（手持/背包格不算，多件叠乘）
        static float GetWornScaleFactor(Character c)
        {
            if (c?.Inventory is not CharacterInventory inv) return 1f;
            float factor = 1f;
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
                if (item != null) factor *= GetItemScaleFactor(item);
            }
            return factor;
        }

        // 回合切换时引擎在批量卸载刚体，这时重建/删肢体会撞车（"removing a body that is not in the simulation"）。
        // 所以结算和加载期间一律不重建，排队的也直接扔，新回合对账会重新判定。
        static bool RoundActive =>
            GameMain.GameSession is { IsRunning: true, RoundEnding: false };

        // 对账入口：穿脱补丁和低频心跳都调这里，幂等——目标状态和当前一致就直接返回
        public static void Reconcile(Character c)
        {
            if (!RoundActive) return;
            if (c == null || c.Removed || c.AnimController == null) return;
            float factor = GetWornScaleFactor(c);
            bool has = states.TryGetValue(c, out var st);
            bool wantScaled = Math.Abs(factor - 1f) > 0.001f;

            if (!has && !wantScaled) return;                                   // 与缩放系统无关的角色
            if (has && Math.Abs(factor - st.CurrentFactor) < 0.001f)
            {
                // 自愈：外部（引擎或其他模组）可能拿默认参数重建过，内存状态没变但实际缩放被洗了——
                // 回读当前 LimbScale 核对，不符就落到下面重新应用，不用玩家重穿装备
                if (!reflectionReady) return;
                object curParams = ragdollParamsProp.GetValue(c.AnimController);
                if (curParams == null) return;
                float actual = (float)limbScaleProp.GetValue(curParams);
                if (Math.Abs(actual - st.BaseLimbScale * factor) < 0.001f) return;
                RSLog.Info($"{c.Name} scale washed out externally (limb {actual:F3}), re-applying");
            }
            if (has && Timing.TotalTime < st.NextRetryTime) return;            // 上次失败退避中

            // 排队逐个重建而不是同一帧全建：回合开始一堆角色同时重建会有瞬时负载尖峰、加大 desync
            EnqueueRebuild(c, factor, wantScaled);
        }

        class PendingRebuild { public Character C; public float Factor; public bool WantScaled; }
        static readonly List<PendingRebuild> pendingRebuilds = new();
        static double nextRebuildTime;
        const double RebuildInterval = 0.25;    // 两次重建的最小间隔（秒）

        static void EnqueueRebuild(Character c, float factor, bool wantScaled)
        {
            foreach (var op in pendingRebuilds)
            {
                if (op.C == c) { op.Factor = factor; op.WantScaled = wantScaled; return; }
            }
            pendingRebuilds.Add(new PendingRebuild { C = c, Factor = factor, WantScaled = wantScaled });
        }

        // 每帧跑一个排队的重建（内部 0.25s 节流）
        public static void DrainRebuildQueue()
        {
            if (pendingRebuilds.Count == 0) return;
            if (!RoundActive)
            {
                // 回合切换期间排队操作全扔：此刻重建会和 Submarine.Unload 撞车，新回合心跳对账会重判
                pendingRebuilds.Clear();
                return;
            }
            if (Timing.TotalTime < nextRebuildTime) return;
            var op = pendingRebuilds[0];
            pendingRebuilds.RemoveAt(0);
            nextRebuildTime = Timing.TotalTime + RebuildInterval;

            var c = op.C;
            if (c == null || c.Removed || c.AnimController == null) return;
            bool has = states.TryGetValue(c, out var st);
            if (!op.WantScaled)
            {
                if (!has) return;   // 排队期间已被处理
                LogFactorBreakdown(c, op.Factor, "restore");
                Restore(c, st);
                states.Remove(c);
                return;
            }
            if (has && Math.Abs(op.Factor - st.CurrentFactor) < 0.001f)
            {
                // listen server 双上下文共享实体，排队期间另一个上下文可能已经应用过了，
                // 回读实际 LimbScale 复核一下，免得重复重建
                if (!reflectionReady) return;
                object curParams = ragdollParamsProp.GetValue(c.AnimController);
                if (curParams == null) return;
                float actual = (float)limbScaleProp.GetValue(curParams);
                if (Math.Abs(actual - st.BaseLimbScale * op.Factor) < 0.001f) return;
                // 被外部洗掉后的自愈重建：打印贡献列表，便于定位是谁洗掉的
                LogFactorBreakdown(c, op.Factor, "re-apply");
            }
            ApplyScale(c, op.Factor, st);
        }

        // 诊断：只在异常路径（restore / 自愈 re-apply）打印各缩放装备的贡献列表，正常 apply 不打印
        static void LogFactorBreakdown(Character c, float total, string reason)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[{reason}] {c.Name} total={total:F3} |");
                if (c.Inventory is CharacterInventory inv)
                {
                    int slotCount = Math.Min(inv.Capacity, inv.SlotTypes.Length);
                    for (int i = 0; i < slotCount; i++)
                    {
                        var item = inv.GetItemAt(i);
                        if (item == null) continue;
                        float f = GetItemScaleFactor(item);
                        if (Math.Abs(f - 1f) < 0.001f) continue;   // 只列缩放件
                        sb.Append($" {item.Prefab.Identifier}={f:F2};");
                    }
                }
                RSLog.Info(sb.ToString());
            }
            catch { /* 诊断日志失败不影响主流程 */ }
        }

        static void ApplyScale(Character c, float factor, ScaleState st)
        {
            if (!EnsureReflection()) return;
            var ragdoll = c.AnimController;
            try
            {
                object currentParams = ragdollParamsProp.GetValue(ragdoll);
                if (currentParams == null) return;

                if (st == null)
                {
                    // 基线取物种共享默认参数而不是当前参数：listen server 双上下文共享实体，
                    // 快照当前值会把已缩放的结果当基线再乘一次（0.8 → 0.64）
                    object defaultParams = GetDefaultParams(c, currentParams);
                    float baseLimb = (float)limbScaleProp.GetValue(defaultParams ?? currentParams);
                    float baseJoint = (float)jointScaleProp.GetValue(defaultParams ?? currentParams);
                    st = new ScaleState
                    {
                        OriginalParams = defaultParams ?? currentParams,
                        BaseLimbScale = baseLimb,
                        BaseJointScale = baseJoint,
                    };
                    states[c] = st;

                    // 双上下文去重：已是基线×系数又不是默认实例 → 另一个上下文应用过了，
                    // 只登记不再重建，重复重建会导致传送和抖动
                    float curLimb = (float)limbScaleProp.GetValue(currentParams);
                    if (!ReferenceEquals(currentParams, defaultParams) &&
                        Math.Abs(curLimb - baseLimb * factor) < 0.001f)
                    {
                        st.CurrentFactor = factor;
                        RSLog.Info($"{c.Name} already scaled to {factor:F2} by the other context, adopting state");
                        return;
                    }
                }

                // 从原始参数克隆独立副本（而不是在缩放副本上继续叠），系数变化时也能干净重建
                object source = st.OriginalParams;
                object copy = CloneParams(source);
                // 防二次缩放：带 scalemultiplier 变体的角色 Recreate 时会再乘一次，基线已含变体效果
                variantAppliedField?.SetValue(copy, true);
                limbScaleProp.SetValue(copy, st.BaseLimbScale * factor);
                jointScaleProp.SetValue(copy, st.BaseJointScale * factor);
                // RecreateAndRespawn：官方运行中重建法，内部记录世界坐标并在重建后 TeleportTo 还原
                recreateRespawnMethod.Invoke(ragdoll, new[] { copy });
                RepairAfterRecreate(c);
                st.CurrentFactor = factor;
                st.NextRetryTime = 0;
                RSLog.Info($"{c.Name} scaled to {factor:F2} (limb {st.BaseLimbScale * factor:F3}, joint {st.BaseJointScale * factor:F3})");
            }
            catch (Exception ex)
            {
                // 退避 5 秒防心跳刷屏重试（st 没入字典说明克隆前就挂了，下次穿脱还会来）
                if (st != null) st.NextRetryTime = Timing.TotalTime + 5.0;
                RSLog.Warn($"apply failed for {c.Name}: {ex.Message}");
            }
        }

        // 取物种共享默认 RagdollParams（各子类上的静态工厂）
        static object GetDefaultParams(Character c, object currentParams)
        {
            var t = currentParams.GetType();
            if (!getDefaultMethods.TryGetValue(t, out var m))
            {
                m = t.GetMethod("GetDefaultRagdollParams",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Character) }, null);
                getDefaultMethods[t] = m;   // null 也缓存，避免重复查找
            }
            if (m == null) return null;
            try { return m.Invoke(null, new object[] { c }); }
            catch { return null; }
        }

        // 克隆 RagdollParams：优先按源参数的 Path 从布娃娃文件重新 Load（拿到的是模组冲突裁决后
        // 的实际文件，天然兼容覆写布娃娃的模组）；Path 为空时走内存 XML 克隆（复刻官方 Memento 复制逻辑）。
        static object CloneParams(object source)
        {
            object copy = Activator.CreateInstance(source.GetType(), nonPublic: true);

            // 主路径：从布娃娃文件重新加载
            if (loadMethod != null)
            {
                object path = pathProp.GetValue(source);
                try
                {
                    if (loadMethod.Invoke(copy, new[] { path, speciesNameProp.GetValue(source) }) is true)
                    {
                        CopyTextureScales(source, copy);
                        return copy;
                    }
                }
                catch { /* 落到兜底路径 */ }
            }

            // 兜底：从内存 XML 克隆
            pathProp.SetValue(copy, pathProp.GetValue(source));
            speciesNameProp.SetValue(copy, speciesNameProp.GetValue(source));
            // ContentXElement 继承 XElement，直接作为新文档根节点拷贝
            if (mainElementProp.GetValue(source) is not XElement mainEl)
                throw new Exception("source MainElement is null and file reload failed");
            docField.SetValue(copy, new XDocument(mainEl));
            createCollidersM.Invoke(copy, null);
            createLimbsM.Invoke(copy, null);
            createJointsM.Invoke(copy, null);
            deserializeMethod.Invoke(copy, new object[] { null, true, true });
            CopyTextureScales(source, copy);
            return copy;
        }

        // 变体/覆写可能动过贴图缩放（那俩属性不存盘，Load 回来是原始值），从源同步
        static void CopyTextureScales(object source, object copy)
        {
            textureScaleProp?.SetValue(copy, textureScaleProp.GetValue(source));
            sourceRectScaleProp?.SetValue(copy, sourceRectScaleProp.GetValue(source));
        }

        static void Restore(Character c, ScaleState st)
        {
            if (!EnsureReflection() || st?.OriginalParams == null) return;
            try
            {
                recreateRespawnMethod.Invoke(c.AnimController, new[] { st.OriginalParams });
                RepairAfterRecreate(c);
                RSLog.Info($"{c.Name} restored to original scale");
            }
            catch (Exception ex)
            {
                RSLog.Warn($"restore failed for {c.Name}: {ex.Message}");
            }
        }

        // 重建后修引用：Ragdoll.Recreate(新参数) 不会迁移 WearingItems（只有 Recreate(null) 才迁移），
        // 旧引用全指向已销毁的对象——穿戴贴图不显示、灯光不跟身、husk 附肢偶尔消失，
        // 症状跟"重新穿一遍装备就恢复"一模一样。这里复刻 Equip 的注册/绑定动作，全部幂等。
        static void RepairAfterRecreate(Character c)
        {
            // 0) 穿戴贴图重注册：sprites 挂到新肢体的 WearingItems，同步 Wearable.limb[] 缓存
            //    （不然 Unequip 会从旧肢体上移除、留残影），再按 Equip 的规则排序并刷新隐藏逻辑
            if (wearableSpritesField != null && wearableLimbTypesField != null && limbWearingItemsField != null)
            {
                try
                {
                    var touchedLimbs = new HashSet<Limb>();
                    if (c.Inventory is CharacterInventory inv)
                    {
                        for (int i = 0; i < inv.Capacity; i++)
                        {
                            var item = inv.GetItemAt(i);
                            if (item == null) continue;
                            foreach (var comp in item.Components)
                            {
                                if (!ReflectionCache.WearableType.IsInstanceOfType(comp)) continue;
                                if (wearableSpritesField.GetValue(comp) is not Array sprites) continue;
                                var limbTypes = wearableLimbTypesField.GetValue(comp) as LimbType[];
                                var limbs = wearableLimbsField?.GetValue(comp) as Limb[];
                                if (limbTypes == null || limbs == null) continue;
                                for (int k = 0; k < sprites.Length && k < limbTypes.Length && k < limbs.Length; k++)
                                {
                                    // 只处理正穿着的贴图（limbs[k] 为 null 的就是背包里没穿的）。
                                    // 不滤掉会把没初始化的贴图塞进 WearingItems，后面 Draw 直接空引用崩
                                    if (limbs[k] == null) continue;
                                    object ws = sprites.GetValue(k);
                                    if (ws == null) continue;
                                    Limb newLimb = c.AnimController.GetLimb(limbTypes[k]);
                                    if (newLimb == null) continue;
                                    if (limbWearingItemsField.GetValue(newLimb) is System.Collections.IList list &&
                                        !list.Contains(ws))
                                    {
                                        list.Add(ws);
                                    }
                                    limbs[k] = newLimb;
                                    touchedLimbs.Add(newLimb);
                                }
                            }
                        }
                    }
                    foreach (var limb in touchedLimbs)
                    {
                        if (limbWearingItemsField.GetValue(limb) is System.Collections.IList list && list.Count > 1)
                        {
                            var sorted = list.Cast<object>()
                                .OrderBy(GetWearableSpriteDepth)
                                .ThenBy(GetWearableOuterSlotFlag)
                                .ToList();
                            list.Clear();
                            foreach (var o in sorted) list.Add(o);
                        }
                        updateWearableHideM?.Invoke(limb, null);
                    }
                }
                catch (Exception ex)
                {
                    RSLog.Warn($"wearable re-register failed for {c.Name}: {ex.Message}");
                }
            }

            // 1) 灯光重绑：照 Equip 里 light.ParentBody = equipLimb.body 做，但不走 Equip，免得 OnWearing 副作用重放
            if (lightParentBodyField != null && wearableSpritesField != null && wsLightComponentsProp != null)
            {
                try
                {
                    if (c.Inventory is CharacterInventory inv)
                    {
                        for (int i = 0; i < inv.Capacity; i++)
                        {
                            var item = inv.GetItemAt(i);
                            if (item == null) continue;
                            foreach (var comp in item.Components)
                            {
                                if (!ReflectionCache.WearableType.IsInstanceOfType(comp)) continue;
                                if (wearableSpritesField.GetValue(comp) is not System.Collections.IEnumerable sprites) continue;
                                foreach (var ws in sprites)
                                {
                                    if (ws == null) continue;
                                    Limb newLimb = null;
                                    if (wsLimbProp != null && wsLimbProp.GetValue(ws) is LimbType lt)
                                        newLimb = c.AnimController.GetLimb(lt);
                                    if (newLimb?.body == null) continue;
                                    if (wsLightComponentsProp.GetValue(ws) is not System.Collections.IEnumerable lights) continue;
                                    foreach (var light in lights)
                                    {
                                        if (light != null)
                                            lightParentBodyField.SetValue(light, newLimb.body);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    RSLog.Warn($"light rebind failed for {c.Name}: {ex.Message}");
                }
            }

            // 2) 画皮附肢：摘掉还挂在旧布娃娃上的附肢并清空引用，AfflictionHusk.Update 下一帧发现 null 会自动重新附着
            if (huskAfflictionType != null && huskAppendageField != null && c.CharacterHealth != null)
            {
                try
                {
                    foreach (var aff in c.CharacterHealth.GetAllAfflictions())
                    {
                        if (!huskAfflictionType.IsInstanceOfType(aff)) continue;
                        if (huskAppendageField.GetValue(aff) is not System.Collections.IEnumerable limbs) continue;
                        foreach (var limb in limbs)
                        {
                            if (limb is Limb l && !l.Removed)
                                c.AnimController.RemoveLimb(l);
                        }
                        huskAppendageField.SetValue(aff, null);
                    }
                }
                catch (Exception ex)
                {
                    RSLog.Warn($"appendage reset failed for {c.Name}: {ex.Message}");
                }
            }
        }

        // WearingItems 排序键 1：贴图 Depth（原版 Equip 第一个比较器，空贴图按 0）
        static float GetWearableSpriteDepth(object ws)
        {
            try
            {
                object sprite = wsSpriteProp?.GetValue(ws);
                if (sprite == null || spriteDepthProp == null) return 0f;
                return spriteDepthProp.GetValue(sprite) is float d ? d : 0f;
            }
            catch { return 0f; }
        }

        // WearingItems 排序键 2：是否外套槽（原版 Equip 第二个比较器，InvSlotType 值 32）
        static bool GetWearableOuterSlotFlag(object ws)
        {
            try
            {
                if (wsWearableCompProp?.GetValue(ws) is Barotrauma.Items.Components.Pickable comp)
                    return comp.AllowedSlots.Contains((InvSlotType)32);
            }
            catch { /* 按 false 处理 */ }
            return false;
        }

        // 角色当前实际应用的缩放系数（未缩放返回 1），供 SmoothRotate 力矩补偿补丁查询
        public static float GetAppliedFactor(Character c)
        {
            return c != null && states.TryGetValue(c, out var st) ? st.CurrentFactor : 1f;
        }

        // 低频心跳：全角色对账 + 清理已移除角色的状态（防止字典泄漏）
        public static void ReconcileAll()
        {
            // 先快照成数组再枚举：加载期间引擎会从加载线程增删 CharacterList，直接枚举会炸
            Character[] chars;
            try { chars = Character.CharacterList.ToArray(); }
            catch (Exception) { return; } // 快照失败则本轮跳过，下一秒重试
            foreach (var c in chars)
            {
                if (c != null) Reconcile(c);
            }
            if (states.Count > 0)
            {
                foreach (var key in states.Keys.Where(k => k == null || k.Removed).ToList())
                {
                    states.Remove(key);
                }
            }
            if (itemFactorCache.Count > 0)
            {
                foreach (var key in itemFactorCache.Keys.Where(k => k == null || k.Removed).ToList())
                {
                    itemFactorCache.Remove(key);
                }
            }
            if (pendingRebuilds.Count > 0)
            {
                pendingRebuilds.RemoveAll(op => op.C == null || op.C.Removed);
            }
        }
    }

    // 穿脱即时响应：CharacterInventory 覆写了基类的 PutItem/RemoveItem，补丁得补在覆写方法上。
    // Reconcile 本身幂等，PutItem 失败早退多调一次也无害。
    [HarmonyPatch]
    public static class RagdollScaleInventoryPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var put = typeof(CharacterInventory).GetMethod("PutItem",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (put == null) RSLog.Warn("CharacterInventory.PutItem NOT FOUND");
            else yield return put;

            var remove = typeof(CharacterInventory).GetMethod("RemoveItem", new[] { typeof(Item) });
            if (remove == null) RSLog.Warn("CharacterInventory.RemoveItem NOT FOUND");
            else yield return remove;
        }

        static void Postfix(CharacterInventory __instance, Item __0)
        {
            // 物品移动时其 tag 可能已被其他模组改写（如动态附魔），使系数缓存失效
            RagdollScaleManager.InvalidateItemFactor(__0);
            if (__instance.Owner is Character c) RagdollScaleManager.Reconcile(c);
        }
    }

    // 低频心跳（约 1 秒）：兜底巡回加载、复活、AI 换装这些穿脱补丁盖不到的路径。
    // 挂点和 MainThreadSchedulerPatch 相同（客户端 GameMain.Update / 服务端 GameServer.Update）。
    [HarmonyPatch]
    public static class RagdollScaleTickPatch
    {
        static double nextReconcile;

        static MethodBase TargetMethod()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var m = typeof(GameMain).GetMethod("Update", flags);
            if (m != null) return m;
            var serverType = typeof(GameMain).Assembly.GetType("Barotrauma.Networking.GameServer");
            m = serverType?.GetMethod("Update", flags);
            if (m == null) RSLog.Warn("RagdollScaleTickPatch: no per-frame Update method found");
            return m;
        }

        static void Postfix()
        {
            RagdollScaleManager.DrainRebuildQueue();    // 每帧尝试执行一个排队的重建（内部有 0.25s 间隔节流）
            if (Timing.TotalTime < nextReconcile) return;
            nextReconcile = Timing.TotalTime + 1.0;
            RagdollScaleManager.ReconcileAll();
        }
    }

#if CLIENT
    // 旋转变速补偿：SmoothRotate 力矩 ∝ 系数² 而角惯量 ∝ 系数⁴，缩放角色的角加速度被放大
    // 1/系数² 倍（0.8 倍体型 ≈ 1.56 倍），头部这种轻肢体在动画驱动下过冲抖动（爬梯/转向点头）。
    // 把 force 乘 系数² 正好补回来。仅客户端：listen server 双上下文各补一次会叠乘（0.64²=0.41，过头了）。
    [HarmonyPatch]
    public static class SmoothRotateScalePatch
    {
        static MethodBase TargetMethod()
        {
            var m = typeof(PhysicsBody).GetMethod("SmoothRotate", BindingFlags.Public | BindingFlags.Instance);
            if (m == null) RSLog.Warn("SmoothRotateScalePatch: PhysicsBody.SmoothRotate NOT FOUND");
            return m;
        }

        static void Prefix(PhysicsBody __instance, ref float force)
        {
            if (__instance.FarseerBody?.BodyType == FarseerPhysics.BodyType.Kinematic) return;
            Character c = (__instance.UserData as Limb)?.character ?? __instance.UserData as Character;
            if (c == null) return;
            float f = RagdollScaleManager.GetAppliedFactor(c);
            if (Math.Abs(f - 1f) > 0.001f) force *= f * f;
        }
    }
#endif

#if CLIENT
    // 写了固定 scale（inheritscale=false / ignoreragdollscale=true）的贴图不跟随体型缩放，
    // 按引擎公式重算后绝对赋值（幂等）。已继承 LimbScale 的跳过免得双重缩放。
    // 仅客户端：这是绘制路径，服务端程序集没有。
    [HarmonyPatch]
    public static class WearableDrawScalePatch
    {
        // WearableSprite（internal）/LimbParams（internal 嵌套）上的成员，反射解析一次
        static PropertyInfo wsScaleProp, inheritScaleProp, ignoreRagdollScaleProp, ignoreLimbScaleProp, ignoreTextureScaleProp;
        static FieldInfo limbParamsField;        // Limb.Params
        static PropertyInfo limbParamsScaleProp; // LimbParams.Scale

        static MethodBase TargetMethod()
        {
            var m = typeof(Limb).GetMethod("CalculateDrawParameters", BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) RSLog.Warn("WearableDrawScalePatch: Limb.CalculateDrawParameters NOT FOUND");
            return m;
        }

        static void Postfix(Limb __instance, object wearable,
            ref (Color FinalColor, Vector2 Origin, float Rotation, float Scale, float Depth) __result)
        {
            if (wearable == null) return;
            float f = RagdollScaleManager.GetAppliedFactor(__instance.character);
            if (Math.Abs(f - 1f) < 0.001f) return;

            if (wsScaleProp == null)
            {
                var t = wearable.GetType();
                wsScaleProp = t.GetProperty("Scale");
                inheritScaleProp = t.GetProperty("InheritScale");
                ignoreRagdollScaleProp = t.GetProperty("IgnoreRagdollScale");
                ignoreLimbScaleProp = t.GetProperty("IgnoreLimbScale");
                ignoreTextureScaleProp = t.GetProperty("IgnoreTextureScale");
                limbParamsField = typeof(Limb).GetField("Params");
                limbParamsScaleProp = limbParamsField?.FieldType.GetProperty("Scale");
            }
            if (wsScaleProp == null) return;

            bool inherits = inheritScaleProp?.GetValue(wearable) is true;
            bool ignoresRagdoll = ignoreRagdollScaleProp?.GetValue(wearable) is true;
            if (inherits && !ignoresRagdoll) return;   // 引擎已乘过 LimbScale，不重复缩放

            // 按引擎公式从基准重算（绝对赋值，幂等）：引擎 scale = Scale × TextureScale? ×
            // LimbParams.Scale? × LimbScale?，我们补最后一项，前面引擎乘过的跟着乘、没乘的跳过
            float s = wsScaleProp.GetValue(wearable) is float ws ? ws : 1f;
            if (inherits)
            {
                if (ignoreTextureScaleProp?.GetValue(wearable) is not true)
                    s *= __instance.TextureScale;
                if (ignoreLimbScaleProp?.GetValue(wearable) is not true &&
                    limbParamsScaleProp != null &&
                    limbParamsField?.GetValue(__instance) is object lp && lp != null)
                {
                    s *= (float)limbParamsScaleProp.GetValue(lp);
                }
            }
            __result.Scale = s * f;
        }
    }
#endif
}
