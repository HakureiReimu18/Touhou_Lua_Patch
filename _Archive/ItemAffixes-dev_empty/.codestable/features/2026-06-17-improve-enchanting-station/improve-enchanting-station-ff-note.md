---
doc_type: feature-ff-note
feature: 2026-06-17-improve-enchanting-station
date: 2026-06-17
tags: [xml, enchanting-station, item-definition]
---

## 做了什么
参照 TouhouMachine 炼金桌 XML 完善了附魔台的物品定义，使其具备完整的游戏内交互功能：可购买、可制造、可搬动、可分解物品、有粒子特效和音效。

## 改了哪些
- `Items/EnchantingStation.xml` — 全面重写：新增 Body/Holdable/Deconstructor/Fabricate/Deconstruct/Price，改进 ItemContainer(uilabel+Containable)、Repairable(粒子特效)、ConnectionPanel(toggle输入)、LightComponent(条件亮度)、Fabricator(OnContained/OnNotContained)
- `Texts/English.xml:15-17` — 新增 `enchantingstation.input`、`enchantingstation.output`、`enchantingstation.deconstruct.infotext` 本地化键

## 怎么验证的
进游戏后附魔台应显示紫色光晕、可交互选择制造/分解界面、可被搬动、可在商店购买、修理时有粒子特效

## 顺手发现
无
