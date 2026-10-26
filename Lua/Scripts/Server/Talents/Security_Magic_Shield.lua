-- 魔力护盾：照美铃“气”的恢复和受击消耗写，减伤仍由天赋 XML 负责。
if CLIENT and not Game.IsSingleplayer then return end

local TT = TouhouTalents
local TALENT = TT.id("Touhou_MagicBulletHunter_Talent04")
local SHIELD_NAME = "Touhou_Security_MagicShield"
local SHIELD = TT.id(SHIELD_NAME)

local SHIELD_MAX = 100
local RECOVERY_PER_SEC = 1
local HEALTH_THRESHOLD = 0.95
local TAKE_K, TAKE_MIN, TAKE_MAX = 0.1, 0.25, 2
local TICK_INTERVAL = 0.5
local THINK_STEP_FALLBACK = 1 / 60
local tick_elapsed, recovery_elapsed = 0, 0
local seen_hits = {}

local function eligible(character)
    return character ~= nil and not character.Removed and not character.IsDead
        and character.CharacterHealth ~= nil and character.HasTalent(TALENT)
end

local function set_shield(character, value)
    value = math.max(0, math.min(SHIELD_MAX, value))
    local affliction = character.CharacterHealth.GetAffliction(SHIELD)
    if affliction == nil and value > 0 then
        TT.apply_affliction(character, SHIELD_NAME, 1)
        affliction = character.CharacterHealth.GetAffliction(SHIELD)
    end
    if affliction ~= nil then
        -- 盾点数不跟最大生命缩放，施加后写回目标值。
        affliction.Strength = value
    end
end

local function update_character(character, seconds)
    if character == nil or character.Removed or character.CharacterHealth == nil then return end
    if not eligible(character) then
        TT.clear_affliction(character, SHIELD)
        return
    end
    if seconds > 0 and character.MaxVitality > 0
        and character.Vitality / character.MaxVitality > HEALTH_THRESHOLD then
        set_shield(character, TT.get_strength(character, SHIELD) + RECOVERY_PER_SEC * seconds)
    end
end

-- XML 先检查余量并减伤，再扣点；构造攻击数据这条路也包含爆炸。
Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    local victim = ptable["target"]
    local attack = ptable["sourceAttack"]
    if ptable["attacker"] == nil or attack == nil or not eligible(victim)
        or TT.get_strength(victim, SHIELD) <= 0 then return end

    -- 爆炸逐肢体触发，同一帧同一目标的同一攻击只扣一次。
    local seen = seen_hits[victim]
    if seen == nil then
        seen = {}
        seen_hits[victim] = seen
    end
    if seen[attack] then return end
    seen[attack] = true

    local damage = attack.GetTotalCharacterDamage()
    if damage <= 0 then return end
    local cost = math.max(TAKE_MIN, math.min(TAKE_MAX, damage * TAKE_K))
    set_shield(victim, TT.get_strength(victim, SHIELD) - cost)
end, Hook.HookMethodType.After)

local function reset_timers()
    tick_elapsed, recovery_elapsed = 0, 0
    seen_hits = {}
end
Hook.Add("roundStart", "Security.MagicShield.Start", reset_timers)
Hook.Add("roundEnd", "Security.MagicShield.End", reset_timers)

Hook.Add("think", "Security.MagicShield.Update", function(delta_time)
    seen_hits = {}
    if TT.is_game_paused() then return end
    local dt = delta_time or THINK_STEP_FALLBACK
    tick_elapsed = tick_elapsed + dt
    recovery_elapsed = recovery_elapsed + dt
    if tick_elapsed < TICK_INTERVAL and recovery_elapsed < 1 then return end
    tick_elapsed = 0
    local seconds = math.floor(recovery_elapsed)
    recovery_elapsed = recovery_elapsed - seconds
    for character in Character.CharacterList do
        local ok, err = pcall(update_character, character, seconds)
        if not ok then print("Security.MagicShield 错误: " .. tostring(err)) end
    end
end)
