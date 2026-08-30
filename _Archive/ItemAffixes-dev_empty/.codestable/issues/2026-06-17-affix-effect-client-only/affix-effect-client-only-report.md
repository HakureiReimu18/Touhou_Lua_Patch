---
doc_type: issue-report
issue: 2026-06-17-affix-effect-client-only
status: confirmed
severity: P1
summary: 词缀 StatusEffect 在客户端生效但服务端不触发——武器攻击后烧伤仅本地短暂显示随即被服务端覆盖
tags: [networking, client-server, status-effect, statuseffectlists]
---

# 词缀效果服务端不生效 Issue Report

## 1. 问题现象

手持近战武器执行 `/enchant flame` 附魔"火焰"词缀后，攻击敌人：
- **客户端**：敌人短暂显示受到大量烧伤（burn affliction），但血条随即弹回原值
- **服务端**：日志中无烧伤伤害记录，敌人实际未受到任何烧伤伤害
- 其他可读取物品属性的模组可以检测到武器已获得烧伤能力——说明客户端侧的 ItemComponent.statusEffectLists 已正确注入了 StatusEffect，但服务端处理武器攻击时无法识别该效果

## 2. 复现步骤

1. 进入游戏（单人模式即可，客户端+服务端同进程）
2. 手持任意近战武器（如潜水刀），执行控制台命令 `/enchant flame`
3. 确认物品名显示"火焰"前缀
4. 攻击附近任意敌对生物
5. 观察到：敌人短暂闪烁烧伤提示 → 血量回弹 → 服务端日志无烧伤伤害

复现频率：**稳定**（每次附魔后攻击均复现）

## 3. 期望 vs 实际

**期望行为**：手持附魔"火焰"词缀的武器攻击敌人后，敌人应遭受持续烧伤伤害，服务端日志应有对应伤害记录

**实际行为**：客户端短暂预测显示烧伤，服务端权威数据同步后覆盖——敌人实际未受到任何烧伤伤害

## 4. 环境信息

- 涉及模块 / 功能：ItemAffixes mod — StatusEffect 注入管线（`Mod.ApplyAffix` → `Helpers.AddEffectToItem`）
- 相关文件 / 函数：
  - `CSharp/Shared/Mod.cs:417` `ApplyAffix` — 效果注入入口
  - `CSharp/Shared/Mod.cs:442` `ApplyAffixEffects` — 遍历效果调用 AddEffectToItem
  - `CSharp/Shared/HarmonyPatches.cs:58` `Helpers.AddEffectToItem` — 反射注入 statusEffectLists
  - `CSharp/Shared/Mod.cs:182` enchant 命令 — 仅客户端执行（`Character.Controlled` 服务端为 null）
- 运行环境：潜渊症单人模式（dev/LocalMods）
- 其他上下文：Barotrauma 客户端+服务端运行在同一进程，但插件静态字段 `Mod.ItemAffixes` 两端各有独立实例，`Item` 实例本身共享。`enchant` 命令仅客户端执行，`ApplyAffix` 及其调用的 `AddEffectToItem` 只在客户端侧修改了 `ItemComponent.statusEffectLists`

## 5. 严重程度

**P1** — 词缀 StatusEffect 注入是本次 feature 的核心行为，当前完全无法在服务端生效，阻断了 feature 验收（所有 OnImpact/OnUse/OnWearing 类词缀效果均无法实际造成伤害或修改状态）

## 备注

- 此 bug 在 feature `2026-06-17-affix-effects` 实现阶段（impl 完成、accept 尚未执行）发现
- 词缀的纯显示功能（名称前缀、颜色）不受影响——仅 StatusEffect 注入功能受影响
- 问题本质是客户端/服务端分离架构下，实现方案中"修改 statusEffectLists"的做法只考虑了客户端侧，未覆盖服务端侧
