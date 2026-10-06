-- 恋天赋「地底蔷薇」——每隔随机时间（15~30 秒）给自己一次随机时长（5~15 秒）、
-- 随机等级（1~3 级）的「无意识」效果。
-- 替代 Talents/TalentsKoishi.xml 里那条 interval 15 秒 / 75% 概率的能力组
-- 与 Events/Touhou_Talents_Event.xml 的 KoishiTalent_Unconscious 事件（旧 XML 只注释、未删除，保留备查）。
-- 旧 XML 的行为：固定 15 秒一次、75% 概率；时长只有 15/12/8/6 四档，等级 1~3 各约 1/3。
-- 本脚本按描述改成连续随机：间隔 15~30 秒、时长 5~15 秒、等级 1~3
-- （无意识 affliction 自带 strengthchange="-1"，强度即剩余秒数）。
-- 判定与施加都在服务端；60 秒量级的计时用 0.5 秒节拍轮询，避免每帧扫全场。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT = TT.id("Touhou_Koishi_Character_Effect")
local AFF_FLAG = TT.id("Koishi_Unconscious_Flag")
local FLAG_NAME = "Koishi_Unconscious_Flag"
local LEVEL_NAMES = { "Koishi_Unconscious01", "Koishi_Unconscious02", "Koishi_Unconscious03" }

-- 两次无意识之间的随机间隔（秒）
local MIN_INTERVAL, MAX_INTERVAL = 15.0, 30.0
-- 单次无意识的时长（秒）与等级范围
local MIN_DURATION, MAX_DURATION = 5, 15
-- 轮询节拍：0.5 秒扫一遍（间隔是十几秒量级，秒级精度足够，比每帧扫省得多）
local SCAN_INTERVAL = 0.5
local THINK_STEP_FALLBACK = 1.0 / 60.0

local DEBUG_LOG = false
local scan_elapsed = 0
-- 每个角色一个计时器（弱键表，角色移除后自动回收）；第一次看到时随机初始化
local timers = setmetatable({}, { __mode = "k" })

local function log(message)
    if DEBUG_LOG then
        print("Koishi.Unconscious: " .. message)
    end
end

local function roll_interval()
    return MIN_INTERVAL + math.random() * (MAX_INTERVAL - MIN_INTERVAL)
end

-- 到点：挂上标记与随机等级的无意识（时长 = 施加强度，单位秒）
local function trigger(character)
    -- 还在上一次无意识里就跳过（正常流程不会发生，计时器是触发后才重新起算的，这里只兜底）
    if TT.get_strength(character, AFF_FLAG) > 0 then
        return
    end

    local duration = math.random(MIN_DURATION, MAX_DURATION)
    local level_index = math.random(1, #LEVEL_NAMES)
    TT.apply_affliction(character, FLAG_NAME, duration)
    TT.apply_affliction(character, LEVEL_NAMES[level_index], duration)
    log(string.format("%s 进入无意识：%d 级，%d 秒", tostring(character.Name), level_index, duration))
end

local function update_character(character, delta_time)
    if not character.IsHuman or character.IsDead or character.Removed
            or TT.get_strength(character, AFF_TALENT) <= 0 then
        -- 没穿装束 / 死了：清掉计时，下次穿上重新起算
        timers[character] = nil
        return
    end

    local remaining = timers[character]
    if remaining == nil then
        timers[character] = roll_interval()
        return
    end

    remaining = remaining - delta_time
    if remaining > 0 then
        timers[character] = remaining
        return
    end

    trigger(character)
    timers[character] = roll_interval()
end

Hook.Add("think", "Koishi.Unconscious.Update", function(delta_time)
    if TT.is_game_paused() then
        return
    end

    scan_elapsed = scan_elapsed + (delta_time or THINK_STEP_FALLBACK)
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    local elapsed = scan_elapsed
    scan_elapsed = 0

    for character in Character.CharacterList do
        local ok, err = pcall(update_character, character, elapsed)
        if not ok then
            print("Koishi.Unconscious 错误: " .. tostring(err))
        end
    end
end)
