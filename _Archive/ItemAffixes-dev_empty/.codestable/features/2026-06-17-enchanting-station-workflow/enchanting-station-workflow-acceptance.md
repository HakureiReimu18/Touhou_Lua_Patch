# 附魔台工作流 验收报告

> 阶段：阶段 3（验收闭环）
> 验收日期：2026-06-17
> 关联方案 doc：enchanting-station-workflow-design.md

## 1. 接口契约核对

对照方案第 2.1 节名词层：

**接口示例逐项核对**：
- [x] `TierWeights` 结构体（`Mod.cs:609`）：Broken/Normal/Rare/Epic/Legendary + FixedAffix → 与设计一致
- [x] `MaterialTiers` 字典（`Mod.cs:24-29`）：affixes_material_1/2/3 → 权重值与设计一致
- [x] `EnchantingStationPatch.Prefix`（`HarmonyPatches.cs:247`）：TartMethod → Deconstructor.ProcessItem，Prefix 含 IsClient 守卫 + return false → 一致

**名词层"现状 → 变化"逐项核对**：
- [x] `TryGetEnchantingTarget`（`Mod.cs:500`）：扫描输入物品识别武器+材料 → 一致
- [x] `PickAffixByWeight`（`Mod.cs:541`）：加权随机 + FixedAffix 扩展 → 一致

**流程图核对**（第 2.2 节 mermaid 图）：
- [x] IsClient 分支 → `HarmonyPatches.cs:249` `if (IsClient) return true;`
- [x] TryGetEnchantingTarget → `HarmonyPatches.cs:255`
- [x] 权重随机 → `HarmonyPatches.cs:258` PickAffixByWeight
- [x] ApplyAffix → `HarmonyPatches.cs:261`
- [x] 输出转移 → `HarmonyPatches.cs:265-266` TryPutItem
- [x] 材料消耗 → `HarmonyPatches.cs:278-279` RemoveItem + AddItemToRemoveQueue
- [x] return false → `HarmonyPatches.cs:281`

## 2. 行为与决策核对

**需求摘要逐项验证**：
- [x] 武器+材料 → 附魔 → 词缀：服务端 Prefix 正确执行，`ApplyAffix` 设置 Tags + ItemAffixes + RegisterEffectsForDisplay
- [x] 纯武器正常分解：`TryGetEnchantingTarget` 返回 null → `return true` → 原生分解
- [x] 不同等级材料产出不同权重：`MaterialTiers` + `PickAffixByWeight` 实现
- [x] FixedAffix 扩展点：`TierWeights.FixedAffix` 字段 + `PickAffixByWeight` 检查

**明确不做逐项核对**：
- [x] 未修改 Barotrauma 核心程序集（仅 Harmony Prefix）
- [x] 未改变 Deconstructor UI/音效/粒子
- [x] 未新增命令
- [x] 未为材料添加纹理

**关键决策落地**：
- [x] D1（Harmony Prefix 拦截 ProcessItem）：`EnchantingStationPatch` 实现
- [x] D2（权重配置化）：`MaterialTiers` 字典实现
- [x] D3（不破坏原生行为）：`TryGetEnchantingTarget` 返回 null 时 `return true`
- [x] D4（IsClient 守卫）：`HarmonyPatches.cs:249` 首行守卫 + 服务端 `return false`

**流程级约束核对**：
- [x] 幂等性：已附魔武器再次附魔 → `ApplyAffix` 覆盖
- [x] 材料消耗：每次附魔消耗1个材料
- [x] 非武器不触发：标签不匹配 `EnchantableTags` → `return true`
- [x] 空材料不触发：`TryGetEnchantingTarget` 返回 null → `return true`
- [x] 输出满处理：TryPutItem 失败 → 武器放回输入，材料不消耗
- [x] 客户端空走：`IsClient → return true`

**挂载点反向核对**：
- [x] `EnchantingStationPatch.Prefix`（`HarmonyPatches.cs:215-282`）：唯一挂载点，与清单一致
- [x] `MaterialTiers` 字典（`Mod.cs:24`）：配置项挂载点
- [x] `TryGetEnchantingTarget`（`Mod.cs:500`）：内部方法，辅助挂载点
- [x] **反向 grep**：无清单外挂载点
- [x] **拔除沙盘推演**：删除 `EnchantingStationPatch` 类 + 移除 `MaterialTiers` + `TryGetEnchantingTarget` → 附魔台退化为普通分解台，无残留

## 3. 验收场景核对

- [x] **S1 T1附魔**：affixes_1+武器 → 输出附魔武器 → 服务端日志确认（手工验证）
- [x] **S2 T2附魔**：affixes_2+武器 → Rare概率30%（权重表实现，统计分布验证）
- [x] **S3 T3附魔**：affixes_3+武器 → Rare45%+Epic25%+Legendary15%
- [x] **S4 正常分解**：纯武器→正常分解（TryGetEnchantingTarget null → return true）
- [x] **S5 效果生效**：附魔后 StatusEffect 触发（AffixEffectInjectionPatch，已独立验收）
- [ ] **S6 单机即时可见**：**已知限制** — 客户端 `ItemAffixes` 字典不更新，前缀需等到下巡回 `RestoreAffixes` 才显示（详见"遗留"）
- [x] **S7 客户端无物品操作**：Prefix 首行 `IsClient → return true`，无任何物品 API 调用
- [x] **S8 多种材料仅消耗最高级**：`TryGetEnchantingTarget` 内 `CompareMaterialTier` 逻辑
- [x] **S9 输出满**：TryPutItem 失败 → 武器放回输入，材料不消耗
- [x] **S10 不可附魔物品+材料**：正常分解，材料不消耗

## 4. 术语一致性

- `Deconstructor` / `ProcessItem` / `EnchantingMaterial` → 全代码一致
- `MaterialTiers` / `TierWeights` / `TryGetEnchantingTarget` / `PickAffixByWeight` → 全代码一致
- 禁词：`AllowDeconstruct` 反射 hack → grep 确认已移除 ✓

## 5. 架构归并

方案第 4 节结论："本 feature 改动局限在 ItemAffixes mod 内部，无系统级可见变化"

- [x] 无架构维度变更，不触发 ARCHITECTURE.md 更新
- [x] attention.md 已补入"Barotrauma 物品操作服务端权威模式"条目

## 6. requirement 回写

方案 frontmatter 无 `requirement` 字段。本 feature 为 mod 内部新增功能。

- [x] 无 requirement 回写

## 7. roadmap 回写

方案 frontmatter 无 `roadmap` / `roadmap_item` 字段。

- [x] 非 roadmap 起头，跳过回写

## 8. attention.md 候选盘点

- [x] 本次发现：静态字段在 Barotrauma 单机中按端隔离，`Mod.ItemAffixes` 服务端/客户端各有独立实例，服务端代码无法触及客户端字典。已在 attention.md "命令与脚本陷阱"中体现。
- [x] StatusEffect 残留 bug：同巡回重复附魔时旧效果未清理。**已修复**（`ApplyAffix` 中 `UnregisterEffects` 前置于 `RemoveAffixTag`）。

## 9. 遗留

### 已知限制
1. **客户端即时显示**：附魔台产出物品后，客户端前缀需等到下巡回 `RestoreAffixes` 才更新。根因：Barotrauma 单机中插件静态字段（`Mod.ItemAffixes`）按端隔离，服务端 Prefix 无法更新客户端字典。巡回时 `OnRoundStart` 在两端独立触发 `RestoreAffixes` → 两端各自 `ApplyAffix` → 两端字典同步更新。
2. **save 文件桥未在附魔台流程中使用**：当前 `TriggerAffixSync` 已移除，服务端不写文件桥，客户端无法通过文件同步。

### 后续优化点
1. 客户端即时显示：需引入跨端通信机制（如 LuaCs Hook 或网络包），或重构为双端一致的数据源
2. 多人游戏适配：当前 IsClient 守卫仅保证不崩溃，但多人同步未验证

### 已知 BUG（记录，不在本 feature 内修复）
- 同巡回重复附魔时 StatusEffect 残留：**已修复**（`UnregisterEffects` 前置调用）
