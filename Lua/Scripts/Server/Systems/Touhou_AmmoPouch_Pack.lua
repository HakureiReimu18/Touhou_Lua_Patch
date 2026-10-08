--[[东方-空弹药袋（服务端权威）
    v5：空弹药袋 → 对应弹种的弹药袋（袋子直接插进武器当弹药用）。
    收到请求后校验：角色存在、目标物品确实是空袋、空袋在这个角色的背包里、
    请求的袋子 identifier 在收录表里，然后往背包里生成对应弹药袋、移除空袋。
    单机由客户端脚本直调 TLE.AmmoPouchPack.Claim（同一套校验，只是不走网络）。
    可选清单来自 AmmoPouch_Pack_Items.lua 的收录表（TLE_AMMOPOUCH_PACK_ITEMS），与客户端共用一份，
    服务端只认这张表，不信客户端传来的任何名单。]]

-- 纯联机客户端不做权威判定（本地仿冒没意义，结算以服务端为准）
if CLIENT and not SERVER and not Game.IsSingleplayer then
    return
end

local MSG_CLAIM = "TLE_AP_CLAIM"
local LOG_PREFIX = "[东方弹药袋] "

-- 文本走 TextManager（键名 touhou.ammopouch.msg.*），取不到退回键名，绝不因为缺文本中断结算
local function T(key)
    local ok, s = pcall(function() return TextManager.Get(key).Value end)
    if ok and type(s) == "string" and s ~= "" then return s end
    return key
end

-- 空袋 identifier：优先取收录表，表没加载时退回默认值
local function blank_identifier()
    if TLE_AMMOPOUCH_PACK_ITEMS ~= nil and type(TLE_AMMOPOUCH_PACK_ITEMS.blank) == "string" then
        return TLE_AMMOPOUCH_PACK_ITEMS.blank
    end
    return "Touhou_Ammo_Pouch_Blank"
end

-- 请求的袋子是否在收录表里（服务端权威判定，客户端传来的 identifier 只当查询键）
local function allowed_pouch(identifier)
    if TLE_AMMOPOUCH_PACK_ITEMS == nil or type(TLE_AMMOPOUCH_PACK_ITEMS.get) ~= "function" then
        return nil
    end
    return TLE_AMMOPOUCH_PACK_ITEMS.get(identifier)
end

local function find_item(item_id)
    local entity = Entity.FindEntityByID(item_id)
    if entity ~= nil and not entity.Removed and LuaUserData.IsTargetType(entity, "Barotrauma.Item") then
        return entity
    end
    return nil
end

-- 从物品一路向上找持有者角色（空袋可能揣在背包里，背包在角色身上）
local function find_root_character(item)
    local current = item
    for _ = 1, 8 do
        local inv = current.ParentInventory
        if inv == nil then return nil end
        local owner = inv.Owner
        if owner == nil then return nil end
        if LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
            return owner
        end
        if LuaUserData.IsTargetType(owner, "Barotrauma.Item") then
            current = owner
        else
            return nil
        end
    end
    return nil
end

-- 失败给玩家发消息：联机走私聊，单机弹窗
local function notify(target, text)
    if SERVER and not Game.IsSingleplayer then
        pcall(function() Game.SendDirectChatMessage("", text, nil, ChatMessageType.MessageBox, target) end)
    elseif CLIENT then
        pcall(function() GUI.MessageBox(LOG_PREFIX, text) end)
    end
end

-- 结算。返回 true 表示成功；失败返回 nil + 原因文本
local function claim_impl(character, blank_id, pouch_identifier)
    if character == nil or character.Removed then return nil, T("touhou.ammopouch.msg.nocharacter") end
    if type(blank_id) ~= "number" or type(pouch_identifier) ~= "string" or pouch_identifier == "" then
        return nil, T("touhou.ammopouch.msg.invalid")
    end

    local blank = find_item(blank_id)
    if blank == nil then return nil, T("touhou.ammopouch.msg.noblank") end

    local okId, blank_prefab_id = pcall(function() return tostring(blank.Prefab.Identifier) end)
    if not okId or blank_prefab_id ~= blank_identifier() then
        return nil, T("touhou.ammopouch.msg.notblank")
    end

    if find_root_character(blank) ~= character then
        return nil, T("touhou.ammopouch.msg.notyours")
    end

    if allowed_pouch(pouch_identifier) == nil then
        return nil, string.format(T("touhou.ammopouch.msg.notallowed"), pouch_identifier)
    end

    local prefab = ItemPrefab.Prefabs[pouch_identifier]
    if prefab == nil then
        return nil, string.format(T("touhou.ammopouch.msg.noprefab"), pouch_identifier)
    end

    -- 顺序照装束包：先生成新物品，再排队移除空袋
    Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory)
    Entity.Spawner.AddItemToRemoveQueue(blank)
    print(LOG_PREFIX .. tostring(character.Name) .. " 用 " .. blank_prefab_id .. " 换取了 " .. pouch_identifier)
    return true
end

-- 单机直调，不走网络（与 TLE.CostumePack 一个思路）
TLE.AmmoPouchPack = {
    Claim = function(character, blank_id, pouch_identifier)
        local ok, result_or_err, detail = pcall(claim_impl, character, blank_id, pouch_identifier)
        if not ok then
            print(LOG_PREFIX .. "结算异常：" .. tostring(result_or_err))
            return false
        end
        if result_or_err ~= true then
            notify(nil, tostring(detail))
            return false
        end
        return true
    end,
}

if SERVER and not Game.IsSingleplayer then
    Networking.Receive(MSG_CLAIM, function(message, client)
        local blank_id = message.ReadUInt16()
        local pouch_identifier = message.ReadString()

        local character = client.Character
        if character == nil or character.Removed then return end

        local ok, result_or_err, detail = pcall(claim_impl, character, blank_id, pouch_identifier)
        if not ok then
            print(LOG_PREFIX .. "结算异常：" .. tostring(result_or_err))
            notify(client, T("touhou.ammopouch.msg.servererror"))
            return
        end
        if result_or_err ~= true then
            notify(client, tostring(detail))
        end
    end)
end
