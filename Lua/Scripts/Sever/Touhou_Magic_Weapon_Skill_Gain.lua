local MAGIC_SKILL = "Touhou_Magic"
local SKILL_GAIN_PER_DAMAGE = 0.1
local MAX_SKILL_GAIN_PER_HIT = 1.0
local DEBUG_LOG = false

local WEAPON_LEVEL_TAGS = {
    "Touhou_Weapon_Level05",
    "Touhou_Weapon_Level04",
    "Touhou_Weapon_Level03",
    "Touhou_Weapon_Level02",
    "Touhou_Weapon_Level01",
}

local function try_get_param(ptable, key)
    if ptable == nil then
        return nil
    end

    local ok, value = pcall(function()
        return ptable[key]
    end)

    if ok then
        return value
    end

    return nil
end

local function get_weapon_level_tag(item)
    if item == nil then
        return nil
    end

    for _, tag in ipairs(WEAPON_LEVEL_TAGS) do
        if item.HasTag(tag) then
            return tag
        end
    end

    return nil
end

local function resolve_magic_weapon_tag(attacker, attack)
    if attack ~= nil then
        local source_item = attack.SourceItem
        if source_item ~= nil then
            local source_tag = get_weapon_level_tag(source_item)
            if source_tag ~= nil then
                return source_tag
            end

            local projectile_component = source_item.GetComponentString("Projectile")
            if projectile_component ~= nil and projectile_component.Launcher ~= nil then
                local launcher_tag = get_weapon_level_tag(projectile_component.Launcher)
                if launcher_tag ~= nil then
                    return launcher_tag
                end
            end
        end
    end

    if attacker == nil or attacker.Inventory == nil then
        return nil
    end

    local right_hand = attacker.Inventory.GetItemAt(InvSlotType.RightHand)
    local left_hand = attacker.Inventory.GetItemAt(InvSlotType.LeftHand)
    return get_weapon_level_tag(right_hand) or get_weapon_level_tag(left_hand)
end

local function is_valid_enemy(attacker, target)
    if attacker == nil or target == nil then
        return false, "attacker_or_target_nil"
    end

    if not attacker.IsOnPlayerTeam then
        return false, "attacker_not_player_team"
    end

    if target.TeamID ~= attacker.TeamID then
        return true, "enemy_team_mismatch"
    end

    local ok, target_type = pcall(function()
        if target.AIController == nil or target.AIController.GetType == nil then
            return nil
        end
        return target.AIController:GetType().Name
    end)

    if ok and target_type == "EnemyAIController" then
        return true, "enemy_ai_controller"
    end

    return false, "not_enemy"
end

local function get_last_damage(target)
    if target == nil or target.LastDamage == nil then
        return nil
    end

    local last_damage = target.LastDamage
    if last_damage.Damage ~= nil then
        return last_damage.Damage
    end

    local afflictions = last_damage.Afflictions
    if afflictions == nil then
        return nil
    end

    local total_damage = 0
    for _, affliction in ipairs(afflictions) do
        if affliction ~= nil and affliction.GetVitalityDecrease ~= nil then
            local ok, amount = pcall(function()
                return affliction.GetVitalityDecrease(nil)
            end)
            if ok and amount ~= nil then
                total_damage = total_damage + amount
            end
        end
    end

    return total_damage
end

local function get_skill_gain_multiplier(skill_level)
    if skill_level == nil then
        return 1
    end

    local clamped_skill = math.max(0, math.min(skill_level, 100))
    if clamped_skill >= 99 then
        return 0.2
    end

    local curve = 1 - (clamped_skill / 99) * 0.8
    return math.max(curve, 0.2)
end

Hook.Patch("Barotrauma.Character", "ApplyAttack", function(instance, ptable)
    local attacker = try_get_param(ptable, "attacker")
    if attacker == nil or attacker.Info == nil then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponSkillGain: attacker missing.")
        end
        return
    end

    local is_enemy, enemy_reason = is_valid_enemy(attacker, instance)
    if not is_enemy then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponSkillGain: skip non-enemy (" .. tostring(enemy_reason) .. ").")
        end
        return
    end

    local attack = try_get_param(ptable, "attack")
    if resolve_magic_weapon_tag(attacker, attack) == nil then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponSkillGain: no magic weapon tag.")
        end
        return
    end

    local damage = get_last_damage(instance)
    if damage == nil or damage <= 0 then
        if DEBUG_LOG then
            print("Touhou.MagicWeaponSkillGain: damage missing or zero.")
        end
        return
    end

    local base_gain = damage * SKILL_GAIN_PER_DAMAGE
    local current_skill = attacker.GetSkillLevel(Identifier(MAGIC_SKILL)) or 0
    local gain_multiplier = get_skill_gain_multiplier(current_skill)
    local scaled_gain = base_gain * gain_multiplier
    attacker.Info.ApplySkillGain(Identifier(MAGIC_SKILL), scaled_gain, false, MAX_SKILL_GAIN_PER_HIT)
    if DEBUG_LOG then
        print("Touhou.MagicWeaponSkillGain: applied gain " .. tostring(scaled_gain) .. " from damage " .. tostring(damage) .. " with multiplier " .. tostring(gain_multiplier) .. ".")
    end
end, Hook.HookMethodType.After)
