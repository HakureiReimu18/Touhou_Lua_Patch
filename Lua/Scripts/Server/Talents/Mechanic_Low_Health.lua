-- 这里只保留献祭；狂怒改由 CharacterAbilityModifyStatToVitality 处理。
if CLIENT and not Game.IsSingleplayer then return end

local TT = TouhouTalents
local SACRIFICE_TALENT = TT.id("Touhou_FracturedMechanic_Talent03")
local MAX_REDUCTION = 0.2

-- 热重载时也移除旧版狂怒轮询，不留两套维护逻辑。
Hook.Remove("think", "Mechanic.LowHealth.Update")
Hook.Remove("roundStart", "Mechanic.LowHealth.Start")
Hook.Remove("roundEnd", "Mechanic.LowHealth.End")

local function living(character)
    return character ~= nil and not character.Removed and not character.IsDead
        and character.CharacterHealth ~= nil and character.MaxVitality > 0
end

local function missing_health(character)
    return 1 - math.max(0, math.min(1, character.Vitality / character.MaxVitality))
end

Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    local victim = ptable["target"]
    if instance == nil or not living(victim) or not victim.HasTalent(SACRIFICE_TALENT) then return end
    -- 不改共享 Attack，避免同一发爆炸把其他受害者一起减伤。
    instance.DamageMultiplier = instance.DamageMultiplier * (1 - missing_health(victim) * MAX_REDUCTION)
end, Hook.HookMethodType.After)
