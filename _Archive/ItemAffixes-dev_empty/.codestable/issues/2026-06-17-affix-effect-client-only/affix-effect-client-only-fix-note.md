---
doc_type: issue-fix
issue: 2026-06-17-affix-effect-client-only
path: standard
fix_date: 2026-06-17
related: [affix-effect-client-only-analysis.md]
tags: [networking, client-server, harmony, status-effect]
---

# 词缀效果服务端不生效 修复记录

## 1. 实际采用方案

方案 A — Harmony Postfix 动态注入替代反射字典修改。

**改动要点**：
- **新增** `AffixEffectInjectionPatch`：Harmony Postfix 打在 `ItemComponent.ApplyStatusEffects` 上，每次触发效果时从共享的 `item.Tags` 读取词缀 ID → 查 `AffixDefs` → 筛选匹配 `ActionType` 的效果 → 调用 `item.ApplyStatusEffect()` 逐条应用
- **删除** `Helpers` 中的反射基础设施：`GetStatusEffectLists`、`AddEffectToItem`、`RemoveEffectFromItem`、`AddToStatusEffectLists`、`InitHasStatusEffectsOfTypeField` 及 FieldInfo 缓存字段 — 共 6 个方法/字段
- **删除** `AffixEffectTracker` 字典、`ApplyAffixEffects`、`RemoveAffixEffects` 方法 — 效果不再需要预先注入列表，由 Harmony 补丁动态触发
- **简化** `ApplyAffix` 为仅设置字典 + 标签；`RemoveAffix` 仅清理字典 + 标签

## 2. 改动文件清单

| 文件 | 改动类型 | 说明 |
|------|---------|------|
| `HarmonyPatches.cs:166-189` | 新增 | `AffixEffectInjectionPatch` — Postfix 在 `ItemComponent.ApplyStatusEffects` 上动态注入词缀效果 |
| `HarmonyPatches.cs:11-44` | 修改 | `Helpers` 类 — 删除 6 个反射方法/字段，保留原有 `TryReadAffixFromTags` 等方法 |
| `Mod.cs:27` | 删除 | `AffixEffectTracker` 静态字段 |
| `Mod.cs:412-429` | 修改 | `ApplyAffix` — 删除旧效果移除和 `ApplyAffixEffects` 调用 |
| `Mod.cs:442-485` | 删除 | `ApplyAffixEffects` + `RemoveAffixEffects` 方法 |
| `Mod.cs:221` | 修改 | removeaffix 命令 — 删除 `RemoveAffixEffects(heldItem)` 调用 |
| `Mod.cs:84,385` | 修改 | `Dispose` + `OnRoundEnd` — 删除 `AffixEffectTracker.Clear()` |

净行数：删除 ~80 行旧逻辑，新增 ~23 行补丁。

## 3. 验证结果

- **复现步骤**：`/enchant flame` 近战武器 → 攻击敌人 → 期望烧伤伤害在服务端生效
  - 修复前：客户端短暂显示烧伤后血量回弹，服务端无烧伤记录
  - 修复后：等待用户游戏内验证 — Harmony 补丁在客户端+服务端均运行，`item.Tags` 共享信号两端一致，`AffixDefs` 两端独立加载数据一致
- **代码层面**：`AffixEffectInjectionPatch` 在 `ItemComponent.ApplyStatusEffects` 每次调用时动态注入匹配的 StatusEffect。服务端处理武器攻击时同样走此路径，标签已在共享 Item 上，`AffixDefs` 的服务端实例含相同词缀定义
- **影响面回归**：
  - 纯显示效果（名称前缀/颜色）：不受影响 — `ItemNamePatch`/`ItemHUDTextsPatch` 仍通过 `Mod.ItemAffixes` + `item.Tags` 兜底
  - 存档持久化：不受影响 — `ItemSavePatch` 仍写入 `affixid`
  - 加载恢复：不受影响 — `ItemLoadPatch` 仍调用 `ApplyAffix` 设置标签，补丁动态触发效果

## 4. 遗留事项

无。本次修复已完全替换旧的反射注入方案为 Harmony 动态注入方案，不遗留未清理代码。

> 顺手发现：`Helpers.TryReadAffixFromTags` 同时被 `ItemNamePatch`、`ItemHUDTextsPatch`、`AffixEffectInjectionPatch` 三处使用。当前各补丁独立遍历 Tags 每次解析，若词缀数量增长可考虑缓存 — 但不在本次修复范围。
