-- 改名台（Touhou_Renamer）—— 精简版
--
-- 显示与持久化已由 C# 插件接管（CSharp/Shared/TouhouRenamerMod.cs）：
--   - Item.get_Name 原生补丁：缓存命中即纳秒级查表，不再跨 Lua 调用
--   - AddTag/RemoveTag 事件驱动：改名即时生效、外部擦除自动补回、文件落盘随事件触发
--   - 不再有定时全场扫描（旧版每 60 think 帧遍历 Item.ItemList 是周期性卡顿的根源）
--
-- 本文件只保留两个 StatusEffect 钩子（LuaHook 只能从 Lua 注册），职责单一：
-- 把玩家在改名台上的输入转换成物品标签，其余全部交给 C#。
--
-- 标签协议（与 C# 侧约定，存档兼容旧版）：
--   threname:<转义后的名字>  —— 重命名标签（%、逗号、换行需转义）
--   threname_cleared         —— 重置标记（C# 见到后清缓存、删文件记录并自动移除）

local TAG_PREFIX = "threname:"
local CLEARED_TAG = "threname_cleared"

local function encodeName(s)
    return (s:gsub("%%", "%%25"):gsub(",", "%%2C"):gsub("\n", "%%0A"):gsub("\r", ""))
end

-- 清除物品上所有重命名标签
local function clearCustomNameTags(item)
    local toRemove = {}
    for tag in (item.Tags or ""):gmatch("[^,]+") do
        if tag:sub(1, #TAG_PREFIX) == TAG_PREFIX then
            table.insert(toRemove, tag)
        end
    end
    for _, tag in ipairs(toRemove) do
        item.RemoveTag(tag)
    end
end

Hook.Add("Touhou_Renamer_Rename", "Touhou_Renamer_Rename", function(effect, deltaTime, item, targets, worldPosition)
    local containedItem = item.OwnInventory.GetItemAt(0)
    if not containedItem then return end
    local lightColor = item.GetComponentString("LightComponent").LightColor
    local name = item.GetComponentString("CustomInterface").customInterfaceElementList[1].Signal
    name = tostring(name):gsub("[\r\n]", " ")
    local storedName
    if lightColor == Color(255, 255, 255, 255) then
        storedName = name
    else
        storedName = string.format("‖color:%d,%d,%d,%d‖%s‖color:end‖", lightColor.R, lightColor.G, lightColor.B, lightColor.A, name)
    end
    -- 先清旧标签，再写新标签；C# 监听 AddTag 完成缓存更新、残留标签清理与文件落盘
    -- （清旧标签时 C# 可能按“外部擦除”补回一次旧标签，随后会被 AddTag 的残留清理移除，最终只剩新标签）
    clearCustomNameTags(containedItem)
    containedItem.AddTag(TAG_PREFIX .. encodeName(storedName))
end)

Hook.Add("Touhou_Renamer_Resetname", "Touhou_Renamer_Resetname", function(effect, deltaTime, item, targets, worldPosition)
    local containedItem = item.OwnInventory.GetItemAt(0)
    if not containedItem then return end
    -- 顺序不能反：先打重置标记（C# 见到后清缓存、删文件记录），再清重命名标签；
    -- 否则 C# 会把清标签误判为“外部擦除”并按缓存补回
    containedItem.AddTag(CLEARED_TAG)
    clearCustomNameTags(containedItem)
end)
