-- 妹红天赋「不死之烟」：只有妹红本人能涅槃，同队每巡回共用一次机会。
-- 负血只是昏迷，不是死亡；等实际生命值 < 0 再触发，不预估伤害，也不救普通队友。
-- No_Effects10 仍给全队挂，沿用 XML 的双伤分支；换装不能绕过本队次数。

local TT = TouhouTalents

-- 只认装束给的效果 aff，不认装束本体：别的变体、别的模组授予同一个 aff 也能生效
local MOKOU_EFFECT = "Touhou_Mokou_Character_Effect"
local USED_MARK = "No_Effects10" -- XML 的分支也读它，沿用每巡回重置的设置
local USED_MARK_AMOUNT = 1
local NIRVANA = "Resurrection"   -- 涅槃
local NIRVANA_AMOUNT = 30
local CHECK_INTERVAL = 0.5

-- TeamID 在 Lua 侧只保证 == 可比，不保证能当稳定的表键 —— 所以状态都用数组 + == 存
local used_teams = {}  -- 本巡回已触发过的队
local last_check = 0
local fallback_time = 0

local function now_time()
    if Timer ~= nil and Timer.GetTime ~= nil then
        return Timer.GetTime()
    end
    fallback_time = fallback_time + 1 / 60
    return fallback_time
end

local function team_is_used(team)
    for i = 1, #used_teams do
        if used_teams[i] == team then return true end
    end
    return false
end

-- 给本队全体活着的人挂「已用过」标记。
-- 施加后把强度写回 1：ApplyAffliction 会按 100/最大生命缩放，缩到 ActivationThreshold 之下
-- XML 那边的 AbilityConditionHasAffliction 就读不到了
local function mark_team_used(team)
    for character in Character.CharacterList do
        if character ~= nil and not character.IsDead and character.TeamID == team then
            if TT.apply_affliction(character, USED_MARK, USED_MARK_AMOUNT) then
                local affliction = character.CharacterHealth.GetAffliction(USED_MARK)
                if affliction ~= nil then
                    affliction.Strength = USED_MARK_AMOUNT
                end
            end
        end
    end
end

local function trigger(character)
    -- 缺预制时别白白扣掉全队机会。
    if not TT.apply_affliction(character, NIRVANA, NIRVANA_AMOUNT) then return end
    used_teams[#used_teams + 1] = character.TeamID
    mark_team_used(character.TeamID)
end

Hook.Add("roundStart", "Touhou.Mokou.SharedResurrection.RoundStart", function()
    used_teams = {}
    last_check = 0
    fallback_time = 0
end)

-- 同队可以有多个妹红，不能只缓存其中一个；每半秒扫一次，先筛负血再查身份 aff。
Hook.Add("think", "Touhou.Mokou.SharedResurrection.Tick", function()
    local now = now_time()
    if now - last_check < CHECK_INTERVAL then return end
    last_check = now

    if TT.is_game_paused() then return end

    for character in Character.CharacterList do
        if character ~= nil and not character.Removed and not character.IsDead
            and not team_is_used(character.TeamID)
            and character.CharacterHealth ~= nil
            and (character.CharacterHealth.Vitality or 0) < 0
            and TT.get_strength(character, MOKOU_EFFECT) > 0 then
            trigger(character)
        end
    end
end)
