---
doc_type: issue-analysis
issue: 2026-06-17-affix-effect-client-only
status: confirmed
root_cause_type: logic
related: [affix-effect-client-only-report.md]
tags: [networking, client-server, status-effect, harmony]
---

# 词缀效果服务端不生效 根因分析

## 1. 问题定位

| 关键位置 | 说明 |
|---|---|
| `Mod.cs:184` | enchant 命令入口 — `if (Character.Controlled == null) return` 导致命令仅在客户端执行，服务端直接返回 |
| `Mod.cs:210` | `ApplyAffix(heldItem, chosen)` — 仅客户端调用，效果注入只在客户端发生 |
| `Mod.cs:438` | `ApplyAffixEffects(item, affix)` — 遍历 `affix.Effects` 调用 `Helpers.AddEffectToItem`，仅客户端执行 |
| `HarmonyPatches.cs:58-79` | `AddEffectToItem` — 将 StatusEffect 实例注入到 `ItemComponent.statusEffectLists` 和 `Item.statusEffectLists`，仅修改客户端侧的字典对象 |
| `Mod.cs:442-464` | `ApplyAffixEffects` — 将注入的效果引用记录到 `AffixEffectTracker[item.ID]`，同样仅客户端 |

**核心矛盾**：Barotrauma 客户端+服务端同进程，`Item` 实例共享但 `ItemComponent.statusEffectLists` 字典对象不共享——客户端通过 `AddEffectToItem` 注入的效果只存在于客户端侧的组件字典中。服务端处理武器攻击时，自己的 `ItemComponent.ApplyStatusEffects()` 迭代的是服务端侧的字典，找不到词缀效果。

## 2. 失败路径还原

**正常（期望）路径**：
```
enchant 命令 → ApplyAffix(item, def)
  → ItemAffixes[id] = data  (客户端字典，不影响服务端)
  → item.Tags += "__affix_flame"  (共享 Item 属性，两端可见)
  → Helpers.AddEffectToItem → 服务端 ItemComponent.statusEffectLists 中也应有该效果
  → 服务端处理攻击 → ItemComponent.ApplyStatusEffects(OnImpact) → 找到烧伤效果 → 生效
```

**失败路径**：
```
enchant 命令 [客户端 only: Character.Controlled != null]
  → ApplyAffix(item, def)
    → ItemAffixes[id] = data  [仅客户端字典]
    → item.Tags += "__affix_flame"  [共享，两端可见 ✓]
    → Helpers.AddEffectToItem(item, effect)  [仅客户端 Component 字典]
      → 客户端 ItemComponent.statusEffectLists[OnImpact].Add(burnEffect) ✓
      → 服务端 ItemComponent.statusEffectLists[OnImpact] — 无此条目 ✗
  → 服务端处理攻击:
    → ItemComponent.ApplyStatusEffects(OnImpact, 1.0f, ...)
      → 遍历 statusEffectLists[OnImpact] — 找不到烧伤效果
      → 无效果触发 → 无烧伤伤害 → 服务端日志无记录
  → 客户端本地处理攻击:
    → ItemComponent.ApplyStatusEffects(OnImpact, 1.0f, ...)
      → 遍历 statusEffectLists[OnImpact] — 找到烧伤效果 ✓
      → 客户端本地短暂显示烧伤 → 服务端权威同步覆盖 → 血量回弹
```

**分叉点**：`Mod.cs:184` — `Character.Controlled == null` 在服务端永远为 true，`enchant` 命令在服务端直接 return。后续所有效果注入逻辑（`ApplyAffix → ApplyAffixEffects → AddEffectToItem`）从未在服务端执行。

## 3. 根因

**根因类型**：logic（逻辑错误）

**根因描述**：实现方案假设通过修改 `ItemComponent.statusEffectLists` 字典来注入词缀效果，但这个假设只在单侧上下文成立。Barotrauma 客户端+服务端在同一进程中运行时，`Item` 实例共享但每个 `ItemComponent` 的 `statusEffectLists` 字典对象是各端独立的——客户端加载的组件和服务端加载的组件是不同的 C# 对象实例，有各自独立的字典引用。`AddEffectToItem` 通过反射/直接访问修改了客户端侧的字典，服务端侧的字典从未被修改。服务端处理武器攻击时遍历自己的字典，自然找不到词缀效果。

**是否有多个根因**：否。单一根因——效果注入只在客户端执行。

## 4. 影响面

- **影响范围**：影响所有通过 `enchant` 命令应用的含 `<StatusEffect>` 的词缀，包括：
  - `flame`（OnImpact 烧伤）— 完全不生效
  - `shock`（OnImpact 眩晕）— 完全不生效
  - `fortified`（OnWearing 减伤）— 完全不生效
  - 未来任意含 StatusEffect 的词缀（OnUse、OnActive 等全部类型）— 均不生效
- **潜在受害模块**：无。Bug 局限在 ItemAffixes 模组内部的 StatusEffect 注入管线，不影响 Barotrauma 核心及其他模组。纯显示功能（名称前缀/颜色）不受影响——它们通过 Harmony 补丁动态读取 `Mod.ItemAffixes` 字典，补丁在两端都运行。
- **数据完整性风险**：无。不涉及存档损坏或状态污染。存档中的词缀 ID 已通过 `ItemSavePatch` 正确保存，加载时 `ItemLoadPatch` 调用 `ApplyAffix`——但 `ApplyAffix` 同样跑在加载时的执行上下文中（可能是服务端），而加载后 `RestoreAffixes` 在 roundStart (3s 延迟) 时调用 `ApplyAffix`，此 hook 两端都运行——但效果的 `AddEffectToItem` 仍然只在 `ApplyAffix` 执行的单侧生效。
- **严重程度复核**：**维持 P1**。核心行为完全失效，无规避方法。

## 5. 修复方案

### 方案 A：Harmony Prefix 动态注入（推荐）

- **做什么**：在 `ItemComponent.ApplyStatusEffects` 上添加 Harmony Prefix 补丁。每次触发效果时，从 `item.Tags` 中提取词缀 ID → 查 `AffixDefs` 获取 `Effects` 列表 → 筛选匹配当前 `ActionType` 的效果 → 调用 `item.ApplyStatusEffect()` 逐条应用。
  - 移除 `Helpers.AddEffectToItem` / `RemoveEffectFromItem` 的调用，不再修改 `ItemComponent.statusEffectLists`
  - 移除 `AffixEffectTracker` 字典及 `ApplyAffixEffects` / `RemoveAffixEffects` 方法
  - 简化 `ApplyAffix` 为仅设置字典 + 标签
- **优点**：
  - Harmony 补丁在客户端和服务端均运行，天然解决单侧注入问题
  - 利用已有的共享信号：`Item.Tags`（共享） + `AffixDefs`（两端独立加载，数据一致）
  - 无需修改 readonly private 字段，无反射风险
  - 移除/替换词缀只需改标签，无需额外清理逻辑
  - 改动量小（约 30 行新增补丁 + 约 50 行删除旧逻辑）
- **缺点 / 风险**：
  - 效果每次通过 `item.ApplyStatusEffect()` 一次性触发，而非永久在列表中。对于 `OnActive`（每帧触发）类型效果，需确认 `StatusEffect.ShouldWaitForInterval` 提供了足够的去重保护
  - 需精确匹配 `ApplyStatusEffects` 的方法签名（有多个重载，用 `TargetMethod` 指定）
- **影响面**：`HarmonyPatches.cs`（+1 补丁类）、`Mod.cs`（删除 `AffixEffectTracker`、`ApplyAffixEffects`、`RemoveAffixEffects`，简化 `ApplyAffix`、`RemoveAffix`、`OnRoundEnd`、`Dispose`）

### 方案 B：服务端同步注入 + 文件桥

- **做什么**：保持当前的反射注入方案，但通过以下机制让服务端也注入效果：
  - enchant 命令保存 `affix_save.xml` 后，服务端通过轮询或 hook 检测文件变化
  - 检测到后服务端调用 `ApplyAffix(item, def)` 补注效果到服务端侧的 `statusEffectLists`
  - 或利用现有的 `RestoreAffixes`（roundStart 3s 延迟）改为即时触发
- **优点**：效果永久存在列表中，`OnActive`/`OnWearing` 等持续型效果管理更自然
- **缺点 / 风险**：
  - 需要客户端→服务端通知机制（文件轮询有延迟、网络消息需要实现 IServerNetObject 接口）
  - enchant 后到服务端注入之间存在时间窗口，期间效果不生效
  - 实现复杂度远高于方案 A（网络通信或文件监控）
  - 仍需反射处理 readonly 字段
- **影响面**：`Mod.cs`（新增网络消息或文件监控）、`HarmonyPatches.cs`（不变）

### 方案 C：Harmony Prefix on Item.ApplyStatusEffects（Item 级别）

- **做什么**：类似方案 A，但补丁打在 `Item.ApplyStatusEffects` 而非 `ItemComponent.ApplyStatusEffects`。这覆盖所有直接调用 `Item.ApplyStatusEffects()` 的场景。
- **优点**：覆盖范围更广——不仅涵盖组件触发的效果，也涵盖直接对 Item 调用 `ApplyStatusEffects()` 的场景（如 Growable.cs 的 `OnProduceSpawned`）
- **缺点 / 风险**：
  - `Item.ApplyStatusEffects` 被 `ItemComponent.ApplyStatusEffects` 内部调用（组件遍历自己的列表后调用 `item.ApplyStatusEffect` 逐条应用），打在 Item 级别不会截获组件列表的遍历
  - 需要同时打两个补丁（ItemComponent + Item）才能完整覆盖，增加复杂度
  - 两个补丁间可能存在双重触发需要 guard
- **影响面**：`HarmonyPatches.cs`（+2 补丁类，需协调）

### 推荐方案

**推荐方案 A**，理由：
1. **根因最直接**：从根本上消除"单侧注入"问题——不依赖任何单侧的字典修改，而是通过共享信号（Item.Tags）让两端各自独立触发效果
2. **改动范围最小**：仅新增一个 Harmony 补丁，同时删除不再需要的反射逻辑和 tracker，净代码量减少
3. **副作用最少**：利用 Barotrauma 原生 `item.ApplyStatusEffect()` 的效果管理（interval 检查、stackable 控制、网络同步），不引入新机制
4. **架构一致**：与现有 `ItemNamePatch`、`ItemSavePatch`、`ItemLoadPatch` 的模式一致——读取共享 Item 属性 → 查 Mod 字典 → 执行动作

方案 B 的复杂度（网络通信/文件监控）远超 bug 本身需要的修复量；方案 C 需要协调两个补丁，增加了出错空间。
