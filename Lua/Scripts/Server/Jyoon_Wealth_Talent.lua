-- 天天天天上上上上天天天天下下下下，唯唯唯唯我我我我独独独独尊尊尊尊

-- 依神女苑天赋「最凶最恶的双子之妹」(JyoonTalent)：钱包有钱就免疫中毒，装备越贵档位越高
-- affliction 的具体效果在 XML 那边写，这里只负责按装备价格挂/摘对应标识符
-- 生效要同时有天赋 + 装束给的 Touhou_Jyoon_Character_Effect；查 affliction 而不是查物品本身，免得切换装束类的拓展模组失效

local TALENT_IDENTIFIER = Identifier("JyoonTalent")
-- 光靠 HasTalent 判定不了（GiveTalentInfo 是永久解锁），所以还得看上面的装束 affliction
local OUTFIT_AFFLICTION_IDENTIFIER = "Touhou_Jyoon_Character_Effect"

local AFFLICTION_DURATION = 40
local UPDATE_INTERVAL = 30.0

local AFF = {
    POISON_IMMUNITY = "Jyoon_Wealth_PoisonImmunity",
    BAND_LOW = "Jyoon_Wealth_Tier_BandLow",
    BAND_MID = "Jyoon_Wealth_Tier_BandMid",
    TIER_3000 = "Jyoon_Wealth_Tier_3000",
    TIER_7000 = "Jyoon_Wealth_Tier_7000",
    TIER_10000 = "Jyoon_Wealth_Tier_10000",
    TIER_70000 = "Jyoon_Wealth_Tier_70000",
}

local ALL_AFFLICTIONS = {
    AFF.POISON_IMMUNITY,
    AFF.BAND_LOW,
    AFF.BAND_MID,
    AFF.TIER_3000,
    AFF.TIER_7000,
    AFF.TIER_10000,
    AFF.TIER_70000,
}

local VALUE_SLOT_TYPES = {
    InvSlotType.Head,
    InvSlotType.Headset,
    InvSlotType.InnerClothes,
    InvSlotType.OuterClothes,
    InvSlotType.Bag,
    InvSlotType.HealthInterface,
}

local TIER_BAND_MID_MIN = 1000
local TIER_3000_MIN = 3000
local TIER_7000_MIN = 7000
local TIER_10000_MIN = 10000
local TIER_70000_MIN = 70000

local DEBUG_LOG = false

local elapsed = 0

local function has_talent(character)
    if character == nil or character.IsHuman ~= true then
        return false
    end

    local ok, result = pcall(function()
        return character.HasTalent(TALENT_IDENTIFIER)
    end)

    return ok and result == true
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

local function has_outfit_affliction(character)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local affliction = character.CharacterHealth.GetAffliction(OUTFIT_AFFLICTION_IDENTIFIER)
    return affliction ~= nil and affliction.Strength ~= nil and affliction.Strength > 0
end

local function set_affliction(character, affliction_identifier, enable)
    if character == nil or character.CharacterHealth == nil then
        return
    end

    local health = character.CharacterHealth
    local current = health.GetAffliction(affliction_identifier)

    if current ~= nil then
        if enable then
            current.Strength = 1
            current.Duration = AFFLICTION_DURATION
        elseif (current.Strength or 0) ~= 0 then
            current.Strength = 0
        end
        return
    end

    if not enable then
        return
    end

    local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
    local limb = get_main_limb(character)
    if prefab == nil or limb == nil then
        if DEBUG_LOG then
            print("Jyoon.WealthTalent: 找不到 affliction " .. affliction_identifier .. " 或主体位")
        end
        return
    end

    -- ApplyAffliction 会按 100/MaxVitality 缩放强度，所以施加完要手动写回 1
    health.ApplyAffliction(limb, prefab.Instantiate(1))
    local applied = health.GetAffliction(affliction_identifier)
    if applied ~= nil then
        applied.Strength = 1
        applied.Duration = AFFLICTION_DURATION
    end
end

-- 联机取个人钱包；单人模式访问 character.Wallet 会直接抛异常，只能退回战役银行
local function get_wallet_money(character)
    local ok, balance = pcall(function()
        return character.Wallet.Balance
    end)
    if ok and balance ~= nil then
        return balance
    end

    local ok_bank, bank_balance = pcall(function()
        return Game.GameSession.GameMode.Bank.Balance
    end)
    if ok_bank and bank_balance ~= nil then
        return bank_balance
    end

    return 0
end

-- 估价优先复用 Touhou_Pricer 那套逻辑（玩家看到的估价和天赋档位一致），它没加载才自己读售价
-- 这版本 PriceInfo 价格字段叫 Price，BasePrice 是新版源码的改名，俩都试
local function get_item_value(item)
    if TouhouPricer ~= nil and TouhouPricer.GetPrefabValue ~= nil then
        local ok, value = pcall(function() return TouhouPricer.GetPrefabValue(item.Prefab) end)
        if ok and value ~= nil then
            return value
        end
    end

    local price = 0
    pcall(function()
        local info = item.Prefab.DefaultPrice
        if info == nil then info = item.Prefab.Price end
        if info == nil then return end
        local ok_value, value = pcall(function() return info.Price end)
        if not ok_value or value == nil then
            ok_value, value = pcall(function() return info.BasePrice end)
        end
        if ok_value and value ~= nil then
            price = value
        end
    end)
    return price
end

-- 只算身上穿的那几件，不算容器里和手上的
-- 用 GetItemsInLimbSlot 是图稳：遍历 SlotTypes 数组/索引器那套在有的环境下不可靠
local function get_equipment_value(character)
    if character == nil or character.Inventory == nil then
        return 0
    end

    local inv = character.Inventory
    local total = 0

    for _, slot_type in ipairs(VALUE_SLOT_TYPES) do
        local items = nil
        pcall(function() items = inv.GetItemsInLimbSlot(slot_type) end)
        if items ~= nil then
            for item in items do
                if item ~= nil and item.Prefab ~= nil then
                    local price = get_item_value(item)
                    total = total + price
                    if DEBUG_LOG then
                        print("Jyoon.WealthTalent: 槽位物品 " .. tostring(item.Name) .. " 价格=" .. tostring(price))
                    end
                end
            end
        end
    end

    return total
end

local function update_character(character)
    if character == nil or character.Removed or character.IsDead then
        return
    end

    local talent_ok = has_talent(character)
    local outfit_ok = has_outfit_affliction(character)

    if DEBUG_LOG and character.IsPlayer then
        print("Jyoon.WealthTalent: " .. tostring(character.Name)
            .. " 天赋=" .. tostring(talent_ok)
            .. " 装束=" .. tostring(outfit_ok))
    end

    if not talent_ok or not outfit_ok then
        for _, affliction in ipairs(ALL_AFFLICTIONS) do
            set_affliction(character, affliction, false)
        end
        return
    end

    local money = get_wallet_money(character)
    set_affliction(character, AFF.POISON_IMMUNITY, money > 0)

    local value = get_equipment_value(character)

    if DEBUG_LOG then
        print("Jyoon.WealthTalent: " .. tostring(character.Name) .. " 装备价值=" .. tostring(value) .. " 钱包=" .. tostring(money))
    end

    set_affliction(character, AFF.BAND_LOW, value < TIER_BAND_MID_MIN)
    set_affliction(character, AFF.BAND_MID, value >= TIER_BAND_MID_MIN and value < TIER_3000_MIN)
    set_affliction(character, AFF.TIER_3000, value >= TIER_3000_MIN)
    set_affliction(character, AFF.TIER_7000, value >= TIER_7000_MIN)
    set_affliction(character, AFF.TIER_10000, value >= TIER_10000_MIN)
    set_affliction(character, AFF.TIER_70000, value >= TIER_70000_MIN)
end

print("Jyoon.WealthTalent: 脚本已加载 (DEBUG_LOG=" .. tostring(DEBUG_LOG) .. ")")

Hook.Add("think", "Jyoon.WealthTalent.Update", function(delta_time)
    -- 只跑服务端，联机客户端执行了会跟同步打架
    if CLIENT and not Game.IsSingleplayer then return end

    -- Timing 未注册为 Lua 全局（nil 索引），缺 delta_time 时用 1/60 秒兜底
    elapsed = elapsed + (delta_time or (1.0 / 60.0))
    if elapsed < UPDATE_INTERVAL then
        return
    end
    elapsed = 0

    for character in Character.CharacterList do
        local ok, err = pcall(update_character, character)
        if not ok then
            print("Jyoon.WealthTalent 错误: " .. tostring(err))
        end
    end
end)
