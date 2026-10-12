-- 妹红天赋「不死之烟」：全队共享一次的复活
-- 任意同队船员（含妹红自己）生命值掉到 0 时给他挂「涅槃」，本巡回内不再触发。
-- 原 XML 的条件只查妹红自己的生命值，救不了队友，所以触发判定搬到这里。
-- 「已用过」用 Mokou_Resurrection 永久统计记（XML 的两条 4.9 分支读它切换 +10%/-10% 双伤，
-- 开新巡回由 XML 的 OnRoundEnd 清零），不再用 No_Effects10 那个空标记。
-- 判定分两路：命中致死挂 character.applyDamage（伤害落地前触发，能预判这次会不会致死）；
-- 持续伤害致死（流血/烧伤的每秒结算不经过 applyDamage）走 think 的低血阈值兜底。

local TT = TouhouTalents

-- 只认装束给的效果 aff，不认装束本体：别的变体、别的模组授予同一个 aff 也能生效
local MOKOU_EFFECT = "Touhou_Mokou_Character_Effect"
local STATE_STAT = "Mokou_Resurrection"
local NIRVANA = "Resurrection"   -- 涅槃
local NIRVANA_AMOUNT = 30
local LOW_VITALITY = 5           -- 持续伤害兜底阈值
local CHECK_INTERVAL = 0.5

local used_this_round = false
local last_check = 0

-- StatTypes 枚举在 Lua 侧取不到时退化成序号 0（None）
local STAT_TYPE_NONE = 0
do
    local ok, value = pcall(function() return StatTypes.None end)
    if ok and value ~= nil then
        STAT_TYPE_NONE = value
    end
end

-- 在场的妹红：活着 + 有装束效果
local function find_mokou()
    for character in Character.CharacterList do
        if character ~= nil and not character.IsDead
            and TT.get_strength(character, MOKOU_EFFECT) > 0 then
            return character
        end
    end
    return nil
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

-- 记下「本巡回已用」：写永久统计，XML 的两条分支靠它切换状态
local function mark_used(mokou)
    if mokou == nil or mokou.Info == nil then return end
    local ok, err = pcall(function()
        mokou.Info.ChangeSavedStatValue(STAT_TYPE_NONE, 1, Identifier(STATE_STAT), false, 1, true)
    end)
    if not ok then
        print("Mokou.SharedResurrection 写永久统计失败: " .. tostring(err))
    end
end

-- 挂涅槃 + 记已用
local function trigger(mokou, target)
    TT.apply_affliction(target, NIRVANA, NIRVANA_AMOUNT)
    used_this_round = true
    mark_used(mokou)
end

Hook.Add("roundStart", "Touhou.Mokou.SharedResurrection.RoundStart", function()
    used_this_round = false
end)

Hook.Add("character.applyDamage", "Touhou.Mokou.SharedResurrection.Hit",
    function(characterHealth, attackResult, hitLimb)
        if characterHealth == nil then return end

        local target = characterHealth.Character
        if target == nil or target.IsDead then return end

        local mokou = find_mokou()
        if mokou == nil or mokou.TeamID ~= target.TeamID then return end
        if used_this_round then return end
        if (characterHealth.Vitality or 0) <= 0 then return end   -- 本来就贴地了，不算这次秒杀

        if not is_lethal_hit(characterHealth, attackResult) then return end

        trigger(mokou, target)
    end)

-- 持续伤害（流血/烧伤的每秒结算不经过 applyDamage）的兜底
Hook.Add("think", "Touhou.Mokou.SharedResurrection.Tick", function()
    local now = last_check
    if Timer ~= nil and Timer.GetTime ~= nil then
        now = Timer.GetTime()
    else
        now = last_check + 1 / 60
    end
    if now - last_check < CHECK_INTERVAL then return end
    last_check = now

    local mokou = find_mokou()
    if mokou == nil or used_this_round then return end

    for character in Character.CharacterList do
        if character ~= nil and not character.IsDead
            and character.TeamID == mokou.TeamID
            and character.CharacterHealth ~= nil
            and (character.CharacterHealth.Vitality or 0) <= LOW_VITALITY then
            trigger(mokou, character)
            return
        end
    end
end)
