-- 噩梦缠绕

-- 彩叶装束+：身上有 Touhou_Iroha_Character_Effect（装束给的常驻aff，只认aff不认装束本身，方便其他模组兼容）时，
-- 六项技能里哪个最高就激活哪个模式aff，并列最高就一起激活

local REQUIRED_GATE_AFFLICTION = "Touhou_Iroha_Character_Effect"
local OVERRIDE_GLASSES_IDENTIFIER = "Touhou_Tsukuyomi_Stealth_VR_Glasses"

-- 一秒扫一次；激活中的aff要重新施加续上duration，不然到期会闪断
local UPDATE_INTERVAL = 1.0

local MAGIC_SKILL_IDENTIFIER = "Touhou_Magic"

local MODE_DEFINITIONS = {
    { skill = "electrical", affliction = "Iroha_Mode_Engineer" },
    { skill = "mechanical", affliction = "Iroha_Mode_Mechanic" },
    { skill = "weapons",    affliction = "Iroha_Mode_SafetyOfficer" },
    { skill = "medical",    affliction = "Iroha_Mode_Doctor" },
    { skill = "helm",       affliction = "Iroha_Mode_Captain" },
    { skill = "weapons",    affliction = "Iroha_Mode_Creator", customskill = MAGIC_SKILL_IDENTIFIER }
}

-- 技能 Identifier 惰性缓存：Identifier 每次新建会堆分配，mode 里的技能名是常量，只用建一次
local identifier_cache = {}
local function cached_identifier(name)
    local id = identifier_cache[name]
    if id == nil then
        id = Identifier(name)
        identifier_cache[name] = id
    end
    return id
end

-- 常驻复用的槽位表，别每次判定都新建一张
local EQUIPPED_SLOT_TYPES = { InvSlotType.Headset, InvSlotType.Head, InvSlotType.InnerClothes, InvSlotType.OuterClothes }

-- think不一定传deltaTime，优先用Timer.GetTime，拿不到就按1/60秒累加兜底
local next_update = 0
local elapsed = 0

local function has_affliction(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    return affliction ~= nil and affliction.Strength ~= nil and affliction.Strength > 0
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

local function set_affliction_strength(character, affliction_identifier, strength)
    if character == nil or character.CharacterHealth == nil then
        return
    end

    local health = character.CharacterHealth
    local target_strength = strength or 0

    if target_strength > 0 then
        -- 已存在也重新施加一遍：引擎只在施加时续duration，光改强度会到期闪断
        local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
        local limb = get_main_limb(character)
        if prefab == nil or limb == nil then
            return
        end
        health.ApplyAffliction(limb, prefab.Instantiate(target_strength))
        -- ApplyAffliction会按100/MaxVitality缩放强度，写回目标值
        local current = health.GetAffliction(affliction_identifier)
        if current ~= nil then
            current.Strength = target_strength
        end
        return
    end

    local current = health.GetAffliction(affliction_identifier)
    if current ~= nil and math.abs((current.Strength or 0)) > 0.0001 then
        current.Strength = 0
    end
end

local function has_equipped_item(character, target_identifier)
    if character == nil or character.Inventory == nil or target_identifier == nil then
        return false
    end

    local inv = character.Inventory

    for _, slot_type in ipairs(EQUIPPED_SLOT_TYPES) do
        local item = inv.GetItemInLimbSlot(slot_type)
        if item ~= nil and item.Prefab ~= nil and item.Prefab.Identifier ~= nil then
            if tostring(item.Prefab.Identifier) == target_identifier then
                return true
            end
        end
    end

    return false
end

local function get_skill_level(character, mode)
    if character == nil then
        return 0
    end

    local skill_name = mode.customskill or mode.skill
    if skill_name == nil then
        return 0
    end

    local skill_identifier = cached_identifier(skill_name)

    local ok, value = pcall(function()
        return character.GetSkillLevel(skill_identifier)
    end)

    if ok and value ~= nil then
        return tonumber(value) or 0
    end

    return 0
end

local function update_character_modes(character)
    if character == nil or character.Removed or character.IsDead then
        return
    end

    local active_flags = {}
    local has_gate = has_affliction(character, REQUIRED_GATE_AFFLICTION)
    local has_override_glasses = has_gate and has_equipped_item(character, OVERRIDE_GLASSES_IDENTIFIER)

    if has_override_glasses then
        for _, mode in ipairs(MODE_DEFINITIONS) do
            active_flags[mode.affliction] = true
        end
    elseif has_gate then
        local max_skill = -math.huge
        local levels = {}

        for _, mode in ipairs(MODE_DEFINITIONS) do
            local level = get_skill_level(character, mode)
            levels[mode.affliction] = level
            if level > max_skill then
                max_skill = level
            end
        end

        if max_skill > -math.huge then
            for _, mode in ipairs(MODE_DEFINITIONS) do
                if levels[mode.affliction] == max_skill then
                    active_flags[mode.affliction] = true
                end
            end
        end
    end

    for _, mode in ipairs(MODE_DEFINITIONS) do
        local aff = mode.affliction
        local should_enable = active_flags[aff] == true

        set_affliction_strength(character, aff, should_enable and 1 or 0)
    end
end

Hook.Add("think", "Iroha.VersatileAdaptation.Update", function(delta_time)
    -- 联机时客户端别跑：当主机这脚本会在两个Lua环境各加载一遍，
    -- 客户端重复施aff纯浪费性能，还会跟服务端同步打架
    if CLIENT and not Game.IsSingleplayer then return end

    if Timer ~= nil and Timer.GetTime ~= nil then
        local now = Timer.GetTime()
        if now < next_update then
            return
        end
        next_update = now + UPDATE_INTERVAL
    else
        elapsed = elapsed + (delta_time or (1.0 / 60.0))
        if elapsed < UPDATE_INTERVAL then
            return
        end
        elapsed = 0
    end

    for character in Character.CharacterList do
        update_character_modes(character)
    end
end)