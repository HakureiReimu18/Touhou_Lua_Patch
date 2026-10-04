-- 莲子天赋「少女秘封俱乐部」——《红斑博物志》获取与阅读效果的 Lua 实现
-- 替代以下 XML（只注释、未删除）：
--   1) Talents/TalentsRenko.xml 的 OnAnyMissionCompleted 能力与三段阅读效果 AbilityGroupInterval
--   2) Events/Touhou_Talents_Event.xml 的 RenkoTalent_MissionCompleted_Check / RenkoTalent_MissionCompleted
--   3) Items/Clothes/Renko_gear.xml 里那条触发发书事件的 OnWearing TriggerEvent
--
-- 增益本体（Touhou_Red_Spot_Freak_Report_Effect01~03）仍由 XML 定义，这里只负责判定与发放。


-- 预先转成 Identifier：这三个只在 GetAffliction 里当 key 用，
-- 0.5 秒一次的全场扫描不必每回都重新转字符串（CharacterHealth 两个重载都在，行为一致）
local RENKO_EFFECT = Identifier("Touhou_Renko_Character_Effect")
local MERRY_EFFECT = Identifier("Touhou_Merry_Character_Effect")
local READ_CHECK_AFFLICTION = Identifier("Touhou_Red_Spot_Freak_Report_Effect_Check")
local BOOK_IDENTIFIER = "Touhou_Red_Spot_Freak_Report"

local BUFF_ALL_SKILLS = "Touhou_Red_Spot_Freak_Report_Effect01"  -- 全员：技能获取速度 +50%
local BUFF_RANDOM_EXP = "Touhou_Red_Spot_Freak_Report_Effect02"  -- 随机一人：技能水平 +10%
local BUFF_SELF = "Touhou_Red_Spot_Freak_Report_Effect03"        -- 自己：技能水平 +30%、最大生命 +30%

local BUFF_STRENGTH = 10

local SKILL_POINT_AMOUNT = 1
local EXPERIENCE_AMOUNT = 500
local MIN_LEVEL = 5

-- 三段效果各自独立掷骰（沿用原 XML 的概率）
local CHANCE_ALL_SKILLS = 0.3
local CHANCE_RANDOM_EXP = 0.4
local CHANCE_SELF = 0.5

-- 巡回开始后这段时间内检测到梅莉，就算「一起开始巡回」（装束 aff 有 0.5 秒左右的施加延迟）
local START_DETECT_WINDOW = 10.0
local SCAN_INTERVAL = 0.5
local THINK_STEP_FALLBACK = 1.0 / 60.0

local DEBUG_LOG = false

local SKILL_IDENTIFIERS = {
    Identifier("electrical"),
    Identifier("helm"),
    Identifier("mechanical"),
    Identifier("medical"),
    Identifier("weapons"),
    Identifier("Touhou_Magic"),
}

math.randomseed(os.time())

local current_round = false
-- 发书判定用的巡回标志：roundEnd 钩子跑在 EndMissions 之前（见 roundEnd 处说明），
-- 所以不能跟 current_round 一起复位，只由 roundStart 置位
local book_check_enabled = false
local round_elapsed = 0
local merry_at_round_start = false
-- 弱键表：角色中途死亡/离队被 GC 后条目自动消失，不用非等到 roundEnd。
-- 扫描循环本来就跳过 Removed/IsDead 的角色，弱化不会让效果在尸体上重触发。
local function new_reading_characters()
    return setmetatable({}, { __mode = "k" })
end
local reading_characters = new_reading_characters()
local scan_elapsed = 0

local function log(message)
    if DEBUG_LOG then
        print("Renko.RedSpotReport: " .. message)
    end
end

local function get_affliction_strength(character, affliction_identifier)
    local health = character.CharacterHealth
    if health == nil then
        return 0
    end

    local affliction = health.GetAffliction(affliction_identifier)
    if affliction == nil then
        return 0
    end

    return affliction.Strength or 0
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

local function apply_affliction(character, affliction_identifier, strength)
    local health = character.CharacterHealth
    local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
    local limb = get_main_limb(character)
    if health == nil or prefab == nil or limb == nil then
        log("找不到 affliction " .. tostring(affliction_identifier) .. " 或主体位")
        return false
    end

    -- ApplyAffliction 会按 100/MaxVitality 缩放强度，施加后手动写回目标强度
    health.ApplyAffliction(limb, prefab.Instantiate(strength))
    local applied = health.GetAffliction(affliction_identifier)
    if applied ~= nil then
        applied.Strength = strength
    end

    return true
end

local function get_character_level(character)
    local ok, level = pcall(function()
        return character.Info.GetCurrentLevel()
    end)
    if ok and level ~= nil then
        return level
    end

    return 0
end

local function can_gain_skill_past_max(character)
    local ok, result = pcall(function()
        return character.HasAbilityFlag(AbilityFlags.GainSkillPastMaximum)
    end)
    return ok and result == true
end

-- 按固定数值加技能点：直接写 Job，不吃 SkillGainSpeed / 单技能 GainSpeed
local function grant_flat_skill_points(character, amount)
    local info = character.Info
    if info == nil or info.Job == nil then
        return
    end

    local job = info.Job
    local increase_past_max = can_gain_skill_past_max(character)
    local reported = false
    for _, skill_identifier in ipairs(SKILL_IDENTIFIERS) do
        local ok, err = pcall(function()
            job.IncreaseSkillLevel(skill_identifier, amount, increase_past_max)
        end)
        if not ok and not reported then
            reported = true
            print("Renko.RedSpotReport 加技能点失败: " .. tostring(err))
        end
    end
end

local function grant_experience(character, amount)
    if character.Info == nil then
        return
    end

    local ok, err = pcall(function()
        character.Info.GiveExperience(amount)
    end)
    if not ok then
        log("给经验失败：" .. tostring(err))
    end
end

-- 同队、存活的人类船员（含自己）
local function get_living_crew(reader)
    local crew = {}
    local team = reader.TeamID
    for character in Character.CharacterList do
        if character ~= nil
            and character.IsHuman == true
            and character.Removed ~= true
            and character.IsDead ~= true
            and character.IsOnPlayerTeam == true
            and character.TeamID == team then
            table.insert(crew, character)
        end
    end

    return crew
end

local function run_read_effect(reader)
    -- 两份效果用的是同一份「同队存活人类船员」名单，算一次复用（两次判定之间名单不会变）
    local crew = get_living_crew(reader)

    -- 1. 所有船员各技能 +1 点（固定值），并临时获得技能获取速度 +50%
    if math.random() < CHANCE_ALL_SKILLS then
        for _, crew_member in ipairs(crew) do
            grant_flat_skill_points(crew_member, SKILL_POINT_AMOUNT)
            apply_affliction(crew_member, BUFF_ALL_SKILLS, BUFF_STRENGTH)
        end
        log("触发效果 1：全员技能点与技能获取速度")
    end

    -- 2. 随机一名船员 +500 经验值，并临时获得技能水平 +10%
    if math.random() < CHANCE_RANDOM_EXP then
        if #crew > 0 then
            local target = crew[math.random(#crew)]
            apply_affliction(target, BUFF_RANDOM_EXP, BUFF_STRENGTH)
            grant_experience(target, EXPERIENCE_AMOUNT)
            log("触发效果 2：" .. tostring(target.Name))
        end
    end

    -- 3. 自己临时获得技能水平 +30% 与最大生命值 +30%
    if math.random() < CHANCE_SELF then
        apply_affliction(reader, BUFF_SELF, BUFF_STRENGTH)
        log("触发效果 3：自己")
    end
end

-- 巡回结束时，若全员任务完成且本巡回与梅莉一起开始，给等级>5 的莲子发书
local function on_missions_ended(session)
    if session == nil or not book_check_enabled then
        return
    end

    local missions = session.Missions
    if missions == nil then
        return
    end

    local any_mission = false
    local all_completed = true
    for mission in missions do
        any_mission = true
        if mission == nil or mission.Completed ~= true then
            all_completed = false
            break
        end
    end
    if not (any_mission and all_completed) then
        return
    end

    if not merry_at_round_start then
        log("本巡回没有和梅莉一起开始，不发《红斑博物志》")
        return
    end

    local prefab = ItemPrefab.Prefabs[BOOK_IDENTIFIER]
    if prefab == nil or Entity == nil or Entity.Spawner == nil then
        return
    end

    for character in Character.CharacterList do
        if character ~= nil
            and character.IsHuman == true
            and character.Removed ~= true
            and character.IsOnPlayerTeam == true
            and character.Inventory ~= nil
            and get_affliction_strength(character, RENKO_EFFECT) > 0
            and get_character_level(character) > MIN_LEVEL then
            Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory)
            log("给 " .. tostring(character.Name) .. " 发放《红斑博物志》")
        end
    end
end

Hook.Add("roundStart", "Renko.RedSpotReport.RoundStart", function()
    current_round = true
    book_check_enabled = true
    round_elapsed = 0
    merry_at_round_start = false
    reading_characters = new_reading_characters()
    scan_elapsed = 0
end)

-- 注意：本环境里 roundEnd 是 GameSession.EndRound 的 Harmony Prefix，会在 EndRound 内部的
-- GameSession.EndMissions 之前执行，而发书补丁挂在 EndMissions 之后。所以这里只复位巡回计时，
-- 发书判定用的是 book_check_enabled（由 roundStart 置位），不会被这次复位影响。
Hook.Add("roundEnd", "Renko.RedSpotReport.RoundEnd", function()
    current_round = false
    round_elapsed = 0
end)

Hook.Add("think", "Renko.RedSpotReport.Update", function(delta_time)
    -- 服务端权威：联机客户端不执行，避免与服务端同步冲突
    if CLIENT and not Game.IsSingleplayer then return end

    local dt = delta_time or THINK_STEP_FALLBACK
    if current_round then
        round_elapsed = round_elapsed + dt
    end

    scan_elapsed = scan_elapsed + dt
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    scan_elapsed = 0

    for character in Character.CharacterList do
        if character ~= nil
            and character.IsHuman == true
            and character.Removed ~= true
            and character.IsDead ~= true
            and character.IsOnPlayerTeam == true then
            -- 1) 巡回开始窗口内记录「同船梅莉」
            if current_round
                and not merry_at_round_start
                and round_elapsed <= START_DETECT_WINDOW
                and get_affliction_strength(character, MERRY_EFFECT) > 0 then
                merry_at_round_start = true
                log("检测到同船梅莉，本巡回满足发书前置")
            end

            -- 2) 阅读《红斑博物志》后会挂 5 秒检测 aff，用来触发一次随机效果
            local reading = get_affliction_strength(character, READ_CHECK_AFFLICTION) > 0
            if reading and not reading_characters[character] then
                reading_characters[character] = true
                if get_affliction_strength(character, RENKO_EFFECT) > 0 then
                    local ok, err = pcall(run_read_effect, character)
                    if not ok then
                        print("Renko.RedSpotReport 阅读效果错误: " .. tostring(err))
                    end
                end
            elseif not reading then
                reading_characters[character] = nil
            end
        end
    end
end)

-- GameSession.EndMissions 里已经算好 missions.All(Completed)，在这里读最准
local patch_ok, patch_err = pcall(function()
    Hook.Patch("Barotrauma.GameSession", "EndMissions", function(instance, ptable)
        if CLIENT and not Game.IsSingleplayer then return end

        local ok, err = pcall(on_missions_ended, instance)
        if not ok then
            print("Renko.RedSpotReport 发书错误: " .. tostring(err))
        end
    end, Hook.HookMethodType.After)
end)
if not patch_ok then
    print("Renko.RedSpotReport: 注册 EndMissions 补丁失败: " .. tostring(patch_err))
end
