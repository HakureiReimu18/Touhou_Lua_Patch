-- 妹红天赋「不死之烟」：全队共享一次的复活
-- 任意同队船员（含妹红自己）生命值掉到 0 时给他挂「涅槃」，本巡回内该队不再触发。
-- 原 XML 的条件只查妹红自己的生命值，救不了队友，所以触发判定搬到这里。
-- 「本队已用过」用全队 aff No_Effects10 记（就是原来那个空标记），它 resetbetweenrounds="true"，
-- 引擎每巡回自动清；脚本内再用 used_teams 做 O(1) 门控。XML 的两条 4.9 分支也读这个 aff，
-- 所以同队每个穿妹红装束+ 的人都会一起切 +10% / -10% 双伤，而不是只切「恰好被选中的那个」。
-- 判定分两路：命中致死挂 character.applyDamage（伤害落地前触发，能预判这次会不会致死）；
-- 持续伤害致死（流血/烧伤的每秒结算不经过 applyDamage）走 think 的低血阈值兜底。
-- 妹红按队伍找：一个队认一个，几个穿都行，但一巡回只共享触发一次。

local TT = TouhouTalents

-- 只认装束给的效果 aff，不认装束本体：别的变体、别的模组授予同一个 aff 也能生效
local MOKOU_EFFECT = "Touhou_Mokou_Character_Effect"
local USED_MARK = "No_Effects10" -- 全队「本巡回已用过」标记（XML 的分支也读它）
local USED_MARK_AMOUNT = 1
local NIRVANA = "Resurrection"   -- 涅槃
local NIRVANA_AMOUNT = 30
local LOW_VITALITY = 5           -- 持续伤害兜底阈值
local CHECK_INTERVAL = 0.5       -- think 兜底节流
local REFRESH_INTERVAL = 0.5     -- 「哪个队有妹红」的重扫节流（和装束每 0.5s 挂一次效果同节奏）

-- TeamID 在 Lua 侧只保证 == 可比，不保证能当稳定的表键 —— 所以状态都用数组 + == 存
local used_teams = {}  -- 本巡回已触发过的队
local mokou_cache = {} -- { {team=, character=}, ... }，最多每 REFRESH_INTERVAL 重扫一次
local last_check = 0
local last_refresh = -math.huge

local function now_time()
    if Timer ~= nil and Timer.GetTime ~= nil then
        return Timer.GetTime()
    end
    return last_check + 1 / 60
end

local function team_is_used(team)
    for i = 1, #used_teams do
        if used_teams[i] == team then return true end
    end
    return false
end

-- 缓存里本队那个妹红；装束摘了就立刻不认（每帧一次 get_strength 的代价，比全表扫便宜得多）
local function cached_mokou(team)
    for i = 1, #mokou_cache do
        local entry = mokou_cache[i]
        if entry.team == team then
            local character = entry.character
            if character ~= nil and not character.IsDead
                and TT.get_strength(character, MOKOU_EFFECT) > 0 then
                return character
            end
            return nil
        end
    end
    return nil
end

-- 重扫全场：每个队记第一个在场的妹红，没有妹红的队根本不进表
local function rebuild_mokou_cache()
    local list = {}
    for character in Character.CharacterList do
        if character ~= nil and not character.IsDead
            and TT.get_strength(character, MOKOU_EFFECT) > 0 then
            local team = character.TeamID
            local known = false
            for i = 1, #list do
                if list[i].team == team then known = true break end
            end
            if not known then
                list[#list + 1] = { team = team, character = character }
            end
        end
    end
    mokou_cache = list
end

-- 缓存是否已经没法用了：空的，或者里面每个妹红都死了/摘了装束。
-- think 只在"没法用"时才补扫 —— 有妹红在场且缓存有效时，就不用周期性全表扫
local function cache_unusable()
    if #mokou_cache == 0 then return true end
    for i = 1, #mokou_cache do
        local character = mokou_cache[i].character
        if character ~= nil and not character.IsDead
            and TT.get_strength(character, MOKOU_EFFECT) > 0 then
            return false
        end
    end
    return true
end

-- 取本队的妹红。缓存没有就按节流重扫一次 —— 没妹红的队因此最多每 REFRESH_INTERVAL 扫一遍，
-- 而不是每次伤害结算都全表找（原来那样）
local function get_mokou(team)
    local found = cached_mokou(team)
    if found ~= nil then return found end

    local now = now_time()
    if now - last_refresh < REFRESH_INTERVAL then return nil end
    last_refresh = now
    rebuild_mokou_cache()
    return cached_mokou(team)
end

-- 这次命中预估会不会把目标打到 0 以下。
-- 引擎落地时会把强度乘 100/最大生命（AddLimbAffliction 的归一化），所以先还原再问 vitalityDecrease。
local function is_lethal_hit(health, attackResult)
    if health == nil or attackResult == nil or attackResult.Afflictions == nil then return false end

    local max_vitality = health.MaxVitality or 0
    if max_vitality <= 0 then return false end

    local decrease = 0
    for affliction in attackResult.Afflictions do
        if affliction ~= nil and affliction.Prefab ~= nil then
            local scaled = (affliction.Strength or 0) * 100 / max_vitality
            decrease = decrease + (affliction:GetVitalityDecrease(health, scaled) or 0)
        end
    end

    return (health.Vitality or 0) - decrease <= 0
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

-- 挂涅槃 + 记本队已用
local function trigger(mokou, target)
    TT.apply_affliction(target, NIRVANA, NIRVANA_AMOUNT)
    used_teams[#used_teams + 1] = mokou.TeamID
    mark_team_used(mokou.TeamID)
end

Hook.Add("roundStart", "Touhou.Mokou.SharedResurrection.RoundStart", function()
    used_teams = {}
    mokou_cache = {}
    last_refresh = -math.huge
end)

Hook.Add("character.applyDamage", "Touhou.Mokou.SharedResurrection.Hit",
    function(characterHealth, attackResult, hitLimb)
        if characterHealth == nil then return end

        local target = characterHealth.Character
        if target == nil or target.IsDead then return end

        local team = target.TeamID
        if team_is_used(team) then return end   -- 本巡回已用过：纯 Lua 比较，不碰引擎

        local mokou = get_mokou(team)
        if mokou == nil then return end
        if (characterHealth.Vitality or 0) <= 0 then return end   -- 本来就贴地了，不算这次秒杀

        if not is_lethal_hit(characterHealth, attackResult) then return end

        trigger(mokou, target)
    end)

-- 持续伤害（流血/烧伤的每秒结算不经过 applyDamage）的兜底
Hook.Add("think", "Touhou.Mokou.SharedResurrection.Tick", function()
    local now = now_time()
    if now - last_check < CHECK_INTERVAL then return end
    last_check = now

    if cache_unusable() and now - last_refresh >= REFRESH_INTERVAL then
        last_refresh = now
        rebuild_mokou_cache()
    end

    for i = 1, #mokou_cache do
        local entry = mokou_cache[i]
        local team, mokou = entry.team, entry.character
        if not team_is_used(team) and mokou ~= nil and not mokou.IsDead
            and TT.get_strength(mokou, MOKOU_EFFECT) > 0 then
            for character in Character.CharacterList do
                if character ~= nil and not character.IsDead
                    and character.TeamID == team
                    and character.CharacterHealth ~= nil
                    and (character.CharacterHealth.Vitality or 0) <= LOW_VITALITY then
                    trigger(mokou, character)
                    return
                end
            end
        end
    end
end)
