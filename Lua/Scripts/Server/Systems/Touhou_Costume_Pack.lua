--[[东方-装束包（服务端权威）
    校验装束包确实在请求者背包里、目标 identifier 属于对应包的可选清单，
    然后往背包里生成装束、移除装束包。单机由客户端脚本直调 TLE.CostumePack.Claim。
    可选清单来自 CostumePack_Items.lua 的收录表（TLE_COSTUME_PACK_ITEMS），与客户端共用一份。
    已排除（表里没收）：Pollution_*（被玷污的法袍系列）、Touhou_Prometheus_Clothes（普罗米修斯）、
    Touhou_Blood_Crown（独一王冠）等敌人装束。]]

-- 纯联机客户端不做权威判定（本地仿冒没意义，结算以服务端为准）
if CLIENT and not SERVER and not Game.IsSingleplayer then
    return
end

local PACK_CATEGORY = {
    ["Touhou_Costume_Pack"]           = "basic",
    ["Touhou_Costume_Pack_Plus"]      = "plus",
    ["Touhou_Costume_Pack_Headwear"]  = "headwear",
}
local MSG_CLAIM  = "TLE_CP_CLAIM"
local LOG_PREFIX = "[东方装束包] "

-- identifier 是否属于对应包的可选清单（服务端权威判定，不信客户端传来的名单）
local function is_allowed_outfit(identifier, category)
    if TLE_COSTUME_PACK_ITEMS == nil then return false end
    return TLE_COSTUME_PACK_ITEMS.category_of(identifier) == category
end

local function find_item(item_id)
    local entity = Entity.FindEntityByID(item_id)
    if entity ~= nil and not entity.Removed and LuaUserData.IsTargetType(entity, "Barotrauma.Item") then
        return entity
    end
    return nil
end

-- 从物品一路向上找持有者角色（包可能揣在背包里，背包在角色身上）
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
local function claim_impl(character, pack_id, outfit_identifier)
    if character == nil or character.Removed then return nil, "角色不存在" end
    if type(pack_id) ~= "number" or type(outfit_identifier) ~= "string" or outfit_identifier == "" then
        return nil, "请求参数无效"
    end

    local pack = find_item(pack_id)
    if pack == nil then return nil, "装束包不存在" end

    local okId, pack_identifier = pcall(function() return tostring(pack.Prefab.Identifier) end)
    local category = okId and PACK_CATEGORY[pack_identifier] or nil
    if category == nil then
        return nil, "目标物品不是装束包"
    end

    if find_root_character(pack) ~= character then
        return nil, "装束包不在你的背包里"
    end

    if not is_allowed_outfit(outfit_identifier, category) then
        return nil, "该物品不属于此装束包的可选清单：" .. outfit_identifier
    end

    local prefab = ItemPrefab.Prefabs[outfit_identifier]
    if prefab == nil then return nil, "目标物品不存在：" .. outfit_identifier end

    Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory)
    Entity.Spawner.AddItemToRemoveQueue(pack)
    print(LOG_PREFIX .. tostring(character.Name) .. " 用 " .. pack_identifier .. " 换取了 " .. outfit_identifier)
    return true
end

-- 单机直调，不走网络（与 TLE.CostumeLock 一个思路）
TLE.CostumePack = {
    Claim = function(character, pack_id, outfit_identifier)
        local ok, result_or_err, detail = pcall(claim_impl, character, pack_id, outfit_identifier)
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
        local pack_id = message.ReadUInt16()
        local outfit_identifier = message.ReadString()

        local character = client.Character
        if character == nil or character.Removed then return end

        local ok, result_or_err, detail = pcall(claim_impl, character, pack_id, outfit_identifier)
        if not ok then
            print(LOG_PREFIX .. "结算异常：" .. tostring(result_or_err))
            notify(client, "换取失败：服务器内部错误")
            return
        end
        if result_or_err ~= true then
            notify(client, tostring(detail))
        end
    end)
end
