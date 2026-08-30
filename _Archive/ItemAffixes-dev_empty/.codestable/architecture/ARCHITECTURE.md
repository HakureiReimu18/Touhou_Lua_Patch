# ItemAffixes 架构总入口

> 状态：已填充
> 创建日期：2026-06-17
> 来源：从 DEVELOPER.md 迁移

## 1. 项目简介

ItemAffixes — 为潜渊症(Barotrauma)物品添加 Diablo-like 随机词缀系统。词缀赋予物品前缀名和显示颜色，支持 StatusEffect 实际效果。客户端/服务端分离架构，Harmony 补丁实现名称显示与持久化。

### 文件结构

```
ItemAffixes/
├── filelist.xml              # ContentPackage 清单
├── RunConfig.xml             # 运行配置 (Standard/Standard)
├── Items/
│   ├── Affixes.xml           # 词缀定义 (tier/nameprefix/applicable/StatusEffect)
│   └── EnchantingStation.xml # 附魔台 (Fabricator 驱动的交互装置)
├── Texts/English.xml         # 本地化
├── CSharp/Shared/
│   ├── Mod.cs                # 主插件：生命周期/命令/持久化/恢复
│   └── HarmonyPatches.cs     # Harmony 补丁：保存/加载/名称/HUD
└── Data/
    └── affix_save.xml        # (运行时临时文件) 客户端→服务端传递词缀数据
```

## 2. 核心概念 / 术语表

### 字典类型

| 字典 | 键 | 值 | 跨端共享 | 用途 |
|------|-----|-----|------|------|
| `AffixDefs` | `string` (identifier) | `AffixDef` | 是? | 词缀定义 (只读，加载一次) |
| `ItemAffixes` | `ushort` (item.ID) | `AffixData` | **否** | 显示缓存 (名称/颜色) |
| `PendingAffixes` | `ushort` (item.ID) | `string` (affixId) | **否** | 加载时临时映射 |
| `SavedAffixData` | `ushort` (item.ID) | `string` (affixId) | **否** | 从文件加载的缓存 |
| `EnchantableTags` | `string` (tag) | (HashSet) | 是? | 可附魔物品标签集 |

### Item.ID 的性质

- `ushort`，每次加载时由 `IdRemap` 重新分配
- 同一物品在同一回合内 ID 不变
- 跨回合同一物品 ID **必然变化**
- `PendingAffixes` 依赖 `ItemLoadPatch` 使用加载时的**新**运行时 ID

### 词缀等级与颜色

| Tier | 颜色 |
|------|------|
| Broken | (128,128,128) 灰 |
| Normal | (255,255,255) 白 |
| Rare | (74,144,255) 蓝 |
| Epic | (192,64,255) 紫 |
| Legendary | (255,140,0) 橙 |
| Special | (255,64,64) 红 |

### Affixes.xml 中的 NamePrefix 格式

```
‖color:R,G,B,A‖前缀名‖color:end‖‖color:255,255,255,255‖
```

- `‖color:...‖` 标记开始着色
- `‖color:end‖` 恢复默认
- 末尾的 `‖color:255,255,255,255‖` 确保物品名以白色显示

## 3. 子系统 / 模块索引

### 架构核心：客户端/服务端分离

Barotrauma 单机模式中，客户端和服务端运行在同一进程但**插件静态字段不共享**：

- `Mod.ItemAffixes` — 客户端和服务端各有独立实例
- `Item` 实例本身 — **共享** (同一对象)
- `Item.ItemList` — 共享 (Barotrauma 程序集中的静态字段)

### 数据流

#### 附魔 (enchant 命令，仅客户端执行)

```
enchant 命令
  → ApplyAffix(heldItem, chosen)
    → ItemAffixes[item.ID] = AffixData     ← 客户端字典
    → item.Tags += ", __affix_标识符"        ← 共享 Item，服务端可见
  → SaveAffixData()
    → 写入 Data/affix_save.xml            ← 文件桥：客户端→服务端
    → savedDataLoaded = false
```

#### 保存 (服务端触发 ItemSavePatch)

```
Submarine.SaveToXElement
  → 每个 Item.Save()
    → ItemSavePatch.Postfix 三级查找：
      1. ItemAffixes[__instance.ID]    ← 服务端字典通常空，跳过
      2. __instance.Tags 中的 __affix_* ← 客户端改的是共享 Item 的 Tags
      3. SavedAffixData (从文件加载)    ← 同回合 ID 一致，可靠
    → 找到后: __result.SetAttributeValue("affixid", ...)
    → 持久化到潜艇存档 XML
```

#### 加载 (服务端触发 ItemLoadPatch)

```
Submarine 构造
  → MapEntity.LoadAll
    → Item.Load(element, ...)
      → ItemLoadPatch.Postfix
        → 读取 element.GetAttribute("affixid")
        → PendingAffixes[__result.ID] = affixId
        → 直接设置 __result.Tags += "__affix_*"  ← 不等待 RestoreAffixes
        → 直接填充 ItemAffixes[__result.ID]      ← 不等待 RestoreAffixes
```

#### 恢复 (roundStart，两端都执行，3秒延迟)

```
RestoreAffixes()
  → 遍历 Item.ItemList
    → 方法1: PendingAffixes[item.ID]  ← 服务端有，客户端空
    → 方法2: item.Tags 搜索 __affix_* ← 客户端兜底
  → ApplyAffix(item, def)
```

#### 清理 (roundEnd，两端都执行)

```
OnRoundEnd
  → PendingAffixes.Clear()
  → ItemAffixes.Clear()
  → savedDataLoaded = false
```

#### 跨存档隔离 (roundStart)

```
OnRoundStart
  → File.Delete(affix_save.xml)  ← 删除旧存档的文件残留
```

### 显示补丁 (仅客户端 CLIENT)

#### ItemNamePatch (get_Name)
- 用于背包物品栏 Tooltip 渲染
- **保留**原始 `‖color:...‖` 富文本 markup
- 物品名和前缀颜色独立

#### ItemHUDTextsPatch (GetHUDTexts)
- 用于鼠标悬停地上物品的 HUD
- **剥离** markup，使用 `ColoredText(Color)` 参数
- `StartsWith` 守卫防止高刷新率重复叠加

#### StripRichText
- 去除 `‖xxx‖` 标记段
- 定义在 `Helpers` 静态类中

## 4. 关键架构决定

1. **Tags 作为跨端通信信号** — 客户端通过修改共享 Item 的 Tags（`__affix_*` 前缀）来通知服务端词缀变更，避免跨端直接访问字典
2. **文件桥 (affix_save.xml)** — 客户端写入文件作为中间桥接，服务端在同帧内读取以获取词缀数据。仅同回合有效（依赖 Item.ID 一致性）
3. **PendingAffixes 模式** — 加载时立即填充 Tags 和 ItemAffixes 字典，不等待 RestoreAffixes 延迟回调，防止竞态
4. **双端双字典兜底** — RestoreAffixes 在服务端用 PendingAffixes，在客户端用 Tags 搜索，两端各自独立恢复

## 5. 已知约束 / 硬边界

- `ContentXElement` **不**继承 `XElement`，是包装类。用 `element.GetAttribute("name")` 而非 `element.Attribute("name")`
- `ItemComponent` 完整命名空间是 `Barotrauma.Items.Components.ItemComponent`（非 `Barotrauma.ItemComponent`），Harmony 补丁需用 `typeof(Item).Assembly.GetType()` 或 `TargetMethod()` 间接引用
- Harmony static 方法补丁需要用 `TargetMethod()` 指定具体重载
- `Character.Controlled` 在服务端为 null，命令逻辑需要考虑
- 文件 `affix_save.xml` 仅在同回合内有效（ID 一致），跨回合无效
- `ItemAffixes` 字典在两端不共享，不要依赖它做跨端通信

### 待完成：词缀实际效果

`AffixDef.Effects` 是 `List<StatusEffect>`，已从 XML 加载但**从未应用于物品**。

需要实现：
1. 当词缀应用到物品时，将 `StatusEffect` 添加到物品的 `StatusEffectList`
2. 词缀效果应与物品现有效果正确叠加
3. 需要考虑持久化：StatusEffect 是否已通过 Barotrauma 的序列化机制保存？
