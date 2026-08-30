---
doc_type: learning
track: pitfall
severity: high
tags: [barotrauma, client-server, status-effect, reflection, harmony, itemcomponent]
component: ItemAffixes
related_features: [2026-06-17-affix-effects]
related_issues: [2026-06-17-affix-effect-client-only]
---

# 客户端/服务端分离下反射注入效果仅在单端生效

## 现象

Barotrauma 单人模式中，通过 `enchant` 命令（仅客户端执行）反射修改 `ItemComponent.statusEffectLists` 注入词缀 StatusEffect 后，攻击敌人时客户端短暂显示烧伤效果，随后血量回弹——服务端无任何伤害记录。

## 失败尝试

1. **改用 `Item.statusEffectLists`（私有字段）**：同样仅修改客户端侧字典，服务端看不到
2. **在 `ItemLoadPatch` + `RestoreAffixes` 中也注入**：`RestoreAffixes` 仅在 roundStart 触发（3 秒延迟），无法即时生效
3. **使用 `AccessTools.TypeByName("Barotrauma.ItemComponent")`**：命名空间错误，应为 `Barotrauma.Items.Components.ItemComponent`

## 根因

`enchant` 命令在 `Character.Controlled == null` 时直接 return（`Mod.cs:184`），服务端从未执行。效果只注入了客户端侧的 `ItemComponent.statusEffectLists`。

Barotrauma 客户端+服务端同进程运行，`Item` 实例共享但每个 `ItemComponent` 的 `statusEffectLists` 字典对象是各端独立的——客户端和服务端加载的组件是不同的 C# 对象实例。

服务端处理武器攻击时遍历自己的字典，找不到词缀效果 → 无伤害 → 客户端预测的效果被服务端权威数据覆盖。

## 最终解法

用 **Harmony Postfix 动态注入**替代反射字典修改：

1. Harmony Postfix 打在 `ItemComponent.ApplyStatusEffects` 上（`TargetMethod()` 模式避开 internal 类型）
2. 每次效果触发时从共享的 `item.Tags` 读取 `__affix_*` 标签 → 查 `AffixDefs`（两端独立加载但数据一致）→ 筛选匹配今次 `ActionType` 的效果 → 调用 `item.ApplyStatusEffect()`
3. 删除所有反射注入逻辑（`AddEffectToItem`、`AffixEffectTracker` 等）
4. 仅保留 `RegisterEffectsForDisplay` 写入 `component.statusEffectLists` 供外部模组显示（不影响实际效果触发）

## 更早发现方法

- 在实现初期就 grep `Character.Controlled` 确认命令执行范围
- 设计阶段做一个"客户端/服务端数据流沙盘推演"：列出每步修改的是谁的字典、哪端能看到
- 写完后先在服务端日志中验证效果是否触发（`[SV]` 前缀日志），而非只看客户端表现

## 适用场景

Barotrauma 任何通过反射/直接修改 Item/ItemComponent 内部字典来实现功能的模组，都必须验证修改是否在**两端**生效。共享信号（`Item.Tags`、`Item.Condition` 等）是跨端通信的唯一可靠通道。
