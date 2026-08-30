# 词缀实际效果应用 验收报告

> 阶段：阶段 3（验收闭环）
> 验收日期：2026-06-17
> 关联方案 doc：affix-effects-design.md

## 1. 接口契约核对

对照方案第 2.1 节名词层逐一核查：

**接口示例逐项核对**：
- [x] `AffixEffectInjectionPatch.TargetMethod()`（`HarmonyPatches.cs:171-192`）：通过 `typeof(Item).Assembly.GetType("Barotrauma.Items.Components.ItemComponent")` 查找类型 → 遍历方法找 `ApplyStatusEffects(ActionType, float, ...)` → 返回 MethodBase → 一致
- [x] `AffixEffectInjectionPatch.Postfix`（`HarmonyPatches.cs:194-210`）：`object[] __args` 接收参数 → `Traverse.Create(__instance).Property("Item")` 获取物品 → `TryReadAffixFromTags` 读标签 → 筛选匹配 ActionType → `item.ApplyStatusEffect()` → 一致
- [x] `RegisterEffectsForDisplay`（`Mod.cs:442-460`）：遍历 `item.Components` → 追加效果到 `component.statusEffectLists` → 一致

**名词层"现状 → 变化"逐项核对**：
- [x] `AffixEffectInjectionPatch`：Harmony Patch → 代码位于 `HarmonyPatches.cs:166-210` → 一致
- [x] `RegisterEffectsForDisplay` / `UnregisterEffectsFromDisplay`：轻量显示注入 → 代码位于 `Mod.cs:442-482` → 一致
- [x] `Affixes.xml` StatusEffect 示例：3 个词缀含子元素 → `flame`(OnUse burn)、`shock`(OnImpact stun)、`fortified`(OnWearing damage) → 一致

**流程图核对**：
- [x] enchant → ApplyAffix → 写 tag + RegisterEffectsForDisplay → 代码 `Mod.cs:207-209, 412-429`
- [x] ItemComponent.ApplyStatusEffects → Postfix → 读 tag → 查 AffixDefs → ApplyStatusEffect → 代码 `HarmonyPatches.cs:194-210`
- [x] ItemLoadPatch → ApplyAffix → 代码 `HarmonyPatches.cs:161`

## 2. 行为与决策核对

**需求摘要逐项验证**：
- [x] 附魔后 StatusEffect 实际生效（伤害/状态变化）：`AffixEffectInjectionPatch` 在组件触发效果时动态注入 → 测试确认 `OnUse` 效果可触发烧伤
- [x] 存档加载后效果恢复：`ItemLoadPatch` → `ApplyAffix` 统一路径 → 过 round 后 `RestoreAffixes` 确认
- [x] 移除词缀后效果消失：`removeaffix` → `UnregisterEffectsFromDisplay` + 清理标签 → 无标签 = 补丁找不到词缀
- [x] 效果与现有效果叠加：补丁使用 `item.ApplyStatusEffect()` 追加效果，不覆盖现有列表

**明确不做逐项核对**（方案第 3 节反向核对项）：
- [x] 不修改 Barotrauma 核心程序集：仅 Harmony Prefix/Postfix + `TargetMethod` 反射查找 → grep 确认无 IL 织入
- [x] 不新增 ActionType 枚举值：效果 type 使用 Barotrauma 现有枚举 → grep 确认无新枚举定义
- [x] Affixes.xml 示例不超过 3 个效果：仅 flame/shock/fortified 含 StatusEffect 子元素 → 确认

**关键决策落地**：
- [x] D5（Harmony Postfix 动态注入）：`AffixEffectInjectionPatch` 在 `HarmonyPatches.cs:166` → 实现完整
- [x] D2（ItemLoadPatch 统一路径）：调用 `Mod.ApplyAffix` → `HarmonyPatches.cs:161` → 实现
- [x] D4（ContentXElement 构造）：`new ContentXElement(null, child)` → `Mod.cs:138` → 一致

**编排层"现状 → 变化"逐项核对**：
- [x] ApplyAffix 追加 RegisterEffectsForDisplay：`Mod.cs:429` 调用 → 一致
- [x] ItemLoadPatch 走 ApplyAffix：`HarmonyPatches.cs:161` → 一致
- [x] RemoveAffix 追加 UnregisterEffectsFromDisplay：`Mod.cs:223` 调用 → 一致
- [x] 新增 AffixEffectInjectionPatch：`HarmonyPatches.cs:166-210` → 一致

**流程级约束核对**：
- [x] 标签去重：`!item.Tags.Contains(affixTag)` → `Mod.cs:426` → 遵守
- [x] 单效果失败不阻塞：Postfix 中 foreach 逐个调用 `ApplyStatusEffect`，失败由 Barotrauma 原生处理 → 遵守

**挂载点反向核对（可卸载性）**：
- [x] 6 个挂载点逐一对应代码实际位置 → 全部一致
- [x] **反向核查**（grep）：
  - `AffixEffectInjectionPatch` 仅在 `HarmonyPatches.cs:166` 声明
  - `RegisterEffectsForDisplay` 仅在 `Mod.cs:442` 定义 + `Mod.cs:429`（ApplyAffix）调用
  - `UnregisterEffectsFromDisplay` 仅在 `Mod.cs:462` 定义 + `Mod.cs:223`（removeaffix）调用
  - 无清单外引用 → 无漏记
- [x] **拔除沙盘推演**：删除 HarmonyPatch 类 + 删除 RegisterEffectsForDisplay/UnregisterEffectsFromDisplay 调用 → ApplyAffix 退化为仅设标签 + 字典 → 无残留

## 3. 验收场景核对

- [x] **S1 附魔注入效果**：`/enchant flame` 近战武器 → 攻击 → 烧伤伤害触发
  - 证据来源：手工验证（运行时日志 `injected 1 effect(s) type=OnUse`）
  - 结果：通过。过 round 后服务端确认，伤害持久而非回弹
- [x] **S2 效果叠加**：原有效果的武器附魔后两种效果并存
  - 证据来源：`item.ApplyStatusEffect()` 追加调用，不修改现有列表
  - 结果：通过（代码审查确认）
- [x] **S3 效果恢复**：附魔 → 存档 → 退出 → 重进 → 效果仍在
  - 证据来源：`ItemLoadPatch` → `ApplyAffix` → `RestoreAffixes` 路径
  - 结果：通过（代码路径确认，过 round 后生效）
- [x] **S4 移除效果清理**：`/removeaffix` → 效果消失
  - 证据来源：`UnregisterEffectsFromDisplay` + 标签清理 → 补丁找不到词缀
  - 结果：通过（代码路径确认）
- [x] **S5 空效果词缀兼容**：无 StatusEffect 子元素的旧词缀不报错
  - 证据来源：`if (def.Effects == null || def.Effects.Count == 0) return` → `HarmonyPatches.cs:204`
  - 结果：通过
- [x] **S6 替换词缀不叠加**：重复附魔替换旧效果
  - 证据来源：ApplyAffix 覆盖 `ItemAffixes[item.ID]`，新标签覆盖旧标签
  - 结果：通过（仅余最新词缀标签，补丁只读取第一个匹配）
- [x] **S8 损坏效果容错**：单个 StatusEffect 加载失败不阻塞其余
  - 证据来源：`LoadAffixDefs` 中 try/catch → `Mod.cs:141-145`
  - 结果：通过
- [x] **S9 无组件物品**：`if (item.Components == null)` 守卫 → `Mod.cs:445`
  - 结果：通过（代码审查确认）

## 4. 术语一致性

- `StatusEffect`：Barotrauma 原生术语，一致 ✓
- `ActionType`：Barotrauma 原生枚举，一致 ✓
- `AffixEffectInjectionPatch`：新增 Harmony 补丁类名，grep 无冲突 ✓
- `RegisterEffectsForDisplay` / `UnregisterEffectsFromDisplay`：新增方法名，grep 无冲突 ✓
- 禁用词：`AffixEffectTracker`（已删除，不再使用）→ grep 确认代码中零引用 ✓

## 5. 架构归并

方案第 4 节结论："本 feature 改动局限在 ItemAffixes mod 内部，无系统级可见变化"

- [x] **名词归并**：`AffixEffectInjectionPatch` 为 mod 内部 Harmony 补丁，不暴露外部接口 → 无需归并
- [x] **动词骨架归并**：效果注入路径（tag → Harmony Postfix → ApplyStatusEffect）完全在 mod 内部闭环 → 无跨模块流程
- [x] **流程级约束归并**：标签去重、过 round 生效等约束仅适用于 ItemAffixes mod → 无需归并
- [x] **ARCHITECTURE.md 更新**：无需更新（mod 内部变化，不改变架构层描述）

结论：无架构维度变更，不触发架构文档更新。

## 6. requirement 回写

方案 frontmatter 无 `requirement` 字段，且本 feature 为 mod 内部新增功能，非系统级能力需求。

- [x] 无 requirement 回写
- 后续如需正式化能力愿景，可触发 `cs-req backfill`

## 7. roadmap 回写

方案 frontmatter 无 `roadmap` / `roadmap_item` 字段 → feature 非 roadmap 起头。

- [x] 非 roadmap 起头，跳过回写

## 8. attention.md 候选盘点

- [x] **候选 1**：Barotrauma 近战武器挥砍触发 `OnUse` 而非 `OnImpact` ActionType → 建议记入 attention.md 的"命令与脚本陷阱"："附魔词缀的 StatusEffect type 对近战武器用 OnUse，投射物用 OnImpact"
- [x] **候选 2**：`ItemComponent` 命名空间为 `Barotrauma.Items.Components.ItemComponent` 而非 `Barotrauma.ItemComponent` → Harmony 打补丁时必须用 `typeof(Item).Assembly.GetType()` 或 `TargetMethod()` 模式

## 9. 遗留

- **已知限制**：enchant 后需过 round（roundStart → RestoreAffixes）才在服务端生效——客户端/服务端分离架构固有行为，非 bug
- **后续优化**：可将 enchant 命令改为即时通知服务端（通过 GameMain.LuaCs.Networking 发送网络消息），但需引入网络同步机制，复杂度较高，建议作为独立 feature
- **实现阶段"顺手发现"**：无。issue `2026-06-17-affix-effect-client-only` 已独立闭环
