local MAGIC_SKILL = "Touhou_Magic"
-- 魔法能力的上限：超过该值不会再提高伤害加成
local MAX_MAGIC_SKILL = 300
-- 非线性曲线强度：数值越大，前后段增幅越明显，中段越平缓
local CURVE_WOBBLE = 0.15
-- 调试开关：为 true 时输出无法解析攻击者的原因
local DEBUG_LOG = false

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
    -- 非线性曲线：前期增长较快、中期变缓、后期再次加速
    -- 以 0~1 的标准化技能值 t 为输入，输出同样落在 0~1 的倍率
    local t = clamp(skill / MAX_MAGIC_SKILL, 0, 1)
    local curved = t + CURVE_WOBBLE * math.sin(2 * math.pi * t)
    return clamp(curved, 0, 1)
end

local function get_hand_items(character)
    -- 获取双手物品：用于判断是否满足“双手都为魔法武器”的条件
    if character == nil or character.Inventory == nil then
        return nil, nil
    end

    local right_hand = character.Inventory.GetItemAt(InvSlotType.RightHand)
    local left_hand = character.Inventory.GetItemAt(InvSlotType.LeftHand)
    return right_hand, left_hand
end

local function get_weapon_level_config(item)
    -- 根据武器的等级标签选择对应配置
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

    local skill = attacker_character.GetSkillLevel(Identifier(MAGIC_SKILL)) or 0
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
