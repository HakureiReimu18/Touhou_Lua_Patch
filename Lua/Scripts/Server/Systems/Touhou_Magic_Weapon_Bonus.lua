-- 就这就这就这？

local MAGIC_SKILL = "Touhou_Magic"
-- 魔法技能封顶，再高不涨
local MAX_MAGIC_SKILL = 300
-- 抖动幅度，越大两头越翘、中间越平
local CURVE_WOBBLE = 0.15
local DEBUG_LOG = false

local identifier_cache = {}
local function cached_identifier(name)
    local id = identifier_cache[name]
    if id == nil then
        id = Identifier(name)
        identifier_cache[name] = id
    end
    return id
end

local WEAPON_LEVELS = {
    {
        tag = "Touhou_Weapon_Level05",
        max_bonus = 0.15,
    },
    {
        tag = "Touhou_Weapon_Level04",
        max_bonus = 0.1,
    },
    {
        tag = "Touhou_Weapon_Level03",
        max_bonus = 0.15,
    },
    {
        tag = "Touhou_Weapon_Level02",
        max_bonus = 0.1,
    },
    {
        tag = "Touhou_Weapon_Level01",
        max_bonus = 0.15,
    },
}

local function clamp(value, min_value, max_value)
    if value < min_value then
        return min_value
    end
    if value > max_value then
        return max_value
    end
    return value
end

local function get_skill_curve_factor(skill)
    -- 前期涨得快、中期放缓、后期又提速；t 和输出都归一化到 0~1
    local t = clamp(skill / MAX_MAGIC_SKILL, 0, 1)
    local curved = t + CURVE_WOBBLE * math.sin(2 * math.pi * t)
    return clamp(curved, 0, 1)
end

local function get_hand_items(character)
    if character == nil or character.Inventory == nil then
        return nil, nil
    end

    local right_hand = character.Inventory.GetItemAt(InvSlotType.RightHand)
    local left_hand = character.Inventory.GetItemAt(InvSlotType.LeftHand)
    return right_hand, left_hand
end

local function get_weapon_level_config(item)
    if item == nil then
        return nil
    end

    for _, config in ipairs(WEAPON_LEVELS) do
        if item.HasTag(config.tag) then
            return config
        end
    end

    return nil
end

local function resolve_magic_weapon_config(character, attack)
    if attack ~= nil then
        local source_item = attack.SourceItem
        if source_item ~= nil then
            local source_config = get_weapon_level_config(source_item)
            if source_config ~= nil then
                return source_config
            end

            local projectile_component = source_item.GetComponentString("Projectile")
            if projectile_component ~= nil and projectile_component.Launcher ~= nil then
                local launcher_config = get_weapon_level_config(projectile_component.Launcher)
                if launcher_config ~= nil then
                    return launcher_config
                end
            end
        end
    end

    local right_hand, left_hand = get_hand_items(character)
    local right_config = get_weapon_level_config(right_hand)
    local left_config = get_weapon_level_config(left_hand)

    if (right_hand ~= nil and right_config == nil)
            or (left_hand ~= nil and left_config == nil) then
        return nil
    end

    if right_config == nil then return left_config end
    if left_config == nil then return right_config end

    return (right_config.max_bonus <= left_config.max_bonus) and right_config or left_config
end

local function get_magic_weapon_bonus(attacker_character, attack)
    if attacker_character == nil or attacker_character.IsDead or attacker_character.Removed then
        return 0
    end

    local skill = attacker_character.GetSkillLevel(cached_identifier(MAGIC_SKILL)) or 0
    if skill <= 0 then
        return 0
    end

    skill = math.min(skill, MAX_MAGIC_SKILL)

    local config = resolve_magic_weapon_config(attacker_character, attack)
    if config == nil then
        return 0
    end

    local curve_factor = get_skill_curve_factor(skill)
    return config.max_bonus * curve_factor
end

local attack_damage_multiplier_overrides = setmetatable({}, { __mode = "k" })

Hook.Patch("Barotrauma.Character", "ApplyAttack", function(instance, ptable)
    local attacker = ptable["attacker"]
    if attacker == nil then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponBonus: attacker is nil, skip bonus.")
        end
        return
    end

    local attack = ptable["attack"]
    local bonus = get_magic_weapon_bonus(attacker, attack)
    if bonus <= 0 then
        return
    end

    if attack == nil then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponBonus: attack is nil, skip bonus.")
        end
        return
    end

    -- 引擎不会自己还原倍率，先记下原值打完再恢复，不然每次都叠乘
    if attack_damage_multiplier_overrides[attack] == nil then
        attack_damage_multiplier_overrides[attack] = attack.DamageMultiplier
    end
    attack.DamageMultiplier = attack_damage_multiplier_overrides[attack] * (1 + bonus)
end, Hook.HookMethodType.Before)

Hook.Patch("Barotrauma.Character", "ApplyAttack", function(instance, ptable)
    local attack = ptable["attack"]
    if attack == nil then
        return
    end

    local original_multiplier = attack_damage_multiplier_overrides[attack]
    if original_multiplier ~= nil then
        attack.DamageMultiplier = original_multiplier
        attack_damage_multiplier_overrides[attack] = nil
    end
end, Hook.HookMethodType.After)
