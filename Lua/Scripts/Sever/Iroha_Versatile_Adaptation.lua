-- 彩叶装束+：多面适配天赋模式切换
-- 条件：
-- 1) 角色拥有 Iroha_Versatile_Adaptation affliction
-- 2) 穿着指定服装（默认 Iroha_Plus）
-- 3) 根据六项技能中的最高值，激活对应模式 affliction；并列最高可同时激活

local REQUIRED_GATE_AFFLICTION = "Iroha_Versatile_Adaptation"
local REQUIRED_OUTFIT_IDENTIFIER = "Iroha_Plus"
local OVERRIDE_GLASSES_IDENTIFIER = "Touhou_Tsukuyomi_Stealth_VR_Glasses"


-- 为了降低性能开销：
-- - 每秒检查一次
-- - 仅在状态变化时才写入 affliction
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

local character_state_cache = setmetatable({}, { __mode = "k" })
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
    local current = health.GetAffliction(affliction_identifier)
    local target_strength = strength or 0

    if current ~= nil then
        -- 对激活中的模式定期续写，避免 duration 倒计时到 0 造成短暂中断。
        if target_strength > 0 then
            current.Strength = target_strength
        elseif math.abs((current.Strength or 0) - target_strength) > 0.0001 then
            current.Strength = target_strength
        end
        return
    end

    if target_strength <= 0 then
        return
    end

    local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
    local limb = get_main_limb(character)
    if prefab == nil or limb == nil then
        return
    end

    health.ApplyAffliction(limb, prefab.Instantiate(target_strength))
end

local function has_equipped_item(character, target_identifier)
    if character == nil or character.Inventory == nil or target_identifier == nil then
        return false
    end

    local inv = character.Inventory
    local slot_types = { InvSlotType.Headset, InvSlotType.Head, InvSlotType.InnerClothes, InvSlotType.OuterClothes }

    for _, slot_type in ipairs(slot_types) do
        local item = inv.GetItemInLimbSlot(slot_type)
        if item ~= nil and item.Prefab ~= nil and item.Prefab.Identifier ~= nil then
            if tostring(item.Prefab.Identifier) == target_identifier then
                return true
            end
        end
    end

    return false
end

local function is_wearing_required_outfit(character)
    if character == nil or character.Inventory == nil then
        return false
    end

    local inv = character.Inventory
    local slot_types = { InvSlotType.InnerClothes, InvSlotType.OuterClothes }

    for _, slot_type in ipairs(slot_types) do
        local item = inv.GetItemInLimbSlot(slot_type)
        if item ~= nil and item.Prefab ~= nil and item.Prefab.Identifier ~= nil then
            local identifier = tostring(item.Prefab.Identifier)
            if identifier == REQUIRED_OUTFIT_IDENTIFIER then
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

    local skill_identifier = mode.customskill or mode.skill
    if skill_identifier == nil then
        return 0
    end

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
    local has_override_glasses = has_equipped_item(character, OVERRIDE_GLASSES_IDENTIFIER)
    local should_run_modes = has_override_glasses or (has_affliction(character, REQUIRED_GATE_AFFLICTION) and is_wearing_required_outfit(character))

    if has_override_glasses then
        for _, mode in ipairs(MODE_DEFINITIONS) do
            active_flags[mode.affliction] = true
        end
    elseif should_run_modes then
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

    local cache = character_state_cache[character] or {}

    for _, mode in ipairs(MODE_DEFINITIONS) do
        local aff = mode.affliction
        local should_enable = active_flags[aff] == true
        cache[aff] = should_enable

        -- 即使状态未变化，也要定期兜底：
        -- 若增益因 duration 到期被系统移除，这里会在下一轮自动补回。
        set_affliction_strength(character, aff, should_enable and 1 or 0)
    end

    character_state_cache[character] = cache
end

Hook.Add("think", "Iroha.VersatileAdaptation.Update", function(delta_time)
    elapsed = elapsed + (delta_time or 0)
    if elapsed < UPDATE_INTERVAL then
        return
    end
    elapsed = 0

    for character in Character.CharacterList do
        update_character_modes(character)
    end
end)