---
doc_type: learning
track: knowledge
tags: [barotrauma, harmony, client-server, status-effect, pattern]
component: ItemAffixes
related_features: [2026-06-17-affix-effects]
---

# 标签作为跨端信号 + Harmony Postfix 动态注入 — Barotrauma 模组可复用模式

## 模式

当需要为 Barotrauma 物品添加运行时效果时，**不**修改 `ItemComponent.statusEffectLists`（各端独立），而是：

1. **在共享 `Item` 属性上写入标记**（如 `item.Tags`、`item.Condition` 等——客户端+服务端共享的属性）
2. **Harmony Postfix 打在效果触发入口**（`ItemComponent.ApplyStatusEffects`）上，每次触发时读取标记 → 查定义字典 → 动态注入匹配效果
3. **两端各维护独立的定义字典**（从同一 XML 文件加载，数据一致）

```
enchant(客户端) → item.Tags += 标记(共享)
                       ↓
服务端处理攻击 → ItemComponent.ApplyStatusEffects → Postfix 读标记 → 查字典 → 注入效果
```

## 为什么不用反射修改字典

- `ItemComponent.statusEffectLists` 是 `public readonly`，但各端有独立对象实例——修改一端不影响另一端
- `Item.statusEffectLists` 是 `private readonly`，反射+`readonly` 绕过带来额外复杂度
- 效果触发有时序要求：enchant 后需即时生效，不能等 roundStart

## 关键实现细节

- **避开 internal 类型**：`ItemComponent` 的完整命名空间是 `Barotrauma.Items.Components.ItemComponent`。Harmony 补丁使用 `[HarmonyPatch]` + `TargetMethod()` + `typeof(Item).Assembly.GetType()` 模式间接引用，Postfix 参数用 `object[] __args`
- **效果去重**：`item.ApplyStatusEffect()` 内部的 `ShouldWaitForInterval` 检查提供去重保护
- **显示兼容**：外部模组通过读取 `component.statusEffectLists` 显示物品属性，需额外轻量注入（仅写公有字段，不依赖效果触发）

## 不适用场景

- 需要与另一个不读 `Tags` 的模组通过其他机制交互 → 需找其他共享属性或使用网络消息
- 效果定义需要动态生成且无法从两端共通加载 → 需走 Barotrauma 网络消息通道

## 相关坑点

见 `2026-06-17-learning-client-only-effect-injection.md` — 同一问题从坑点角度记录。
