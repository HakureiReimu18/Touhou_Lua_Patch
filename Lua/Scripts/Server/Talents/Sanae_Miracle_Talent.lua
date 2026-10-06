-- 东风谷早苗天赋「奇迹的现人神」(SanaeTalent) Lua 实现
-- 替代原 XML AbilityGroup + 事件实现的两部分：
-- 1) 巡回开始约10秒后，给除自己外随机一名清醒船员降下随机一种奇迹；主祭司等级>=10时，约15秒后再降下一次
-- 2) 佩戴带有 Illness_Recovery_Charm 标签的护符时，给自己降下随机一种奇迹（每巡回一次）
-- 其余效果（拥有奇迹时减伤/加最大生命、低生命值增伤）仍由 XML TalentsSanae.xml 实现。
-- 奇迹本体（SanaeTalent_Effect01~07、SanaeTalent_Effect_Flag）仍由 affliction XML 定义。
--
-- 生效判定：角色带有 Touhou_Sanae_Character_Effect affliction（强度>0）即视为生效，
-- 不直接检查装备槽位，任何能以任意方式给予该 affliction 的模组/装束都可兼容。
--
-- 多人同时生效时：全船范围的降奇迹(1)只由"主祭司"（ID最小的存活生效者）结算一次；
-- 作用于自身的效果(2)为每个生效者独立结算。
--
-- 性能：单次扫描合并完成所有判定，并按阶段自适应降频：
--   巡回开始 FAST_PHASE_DURATION 秒内每 SCAN_FAST_INTERVAL 秒一次（保证10/15/4.9秒时点精度），
--   之后每 SCAN_SLOW_INTERVAL 秒一次（捕获中途新增生效者/新佩戴护符/中途升到10级），
--   全部效果结算完毕后降至 SCAN_IDLE_INTERVAL 秒兜底。
-- 降奇迹本身（随机抽选目标的第二次遍历）只在到点时执行。

local OUTFIT_FLAG_AFFLICTION = "Touhou_Sanae_Character_Effect"
local CHARM_TAG = "Illness_Recovery_Charm"

local MIRACLES = {
    "SanaeTalent_Effect01", -- 星之奇迹
    "SanaeTalent_Effect02", -- 月之奇迹
    "SanaeTalent_Effect03", -- 日之奇迹
    "SanaeTalent_Effect04", -- 山之奇迹
    "SanaeTalent_Effect05", -- 海之奇迹
    "SanaeTalent_Effect06", -- 空之奇迹（额外给予随机技能7点）
    "SanaeTalent_Effect07", -- 风之奇迹
}
local FLAG_AFFLICTION = "SanaeTalent_Effect_Flag"
local SKY_MIRACLE = "SanaeTalent_Effect06"
local SKY_MIRACLE_SKILL_AMOUNT = 7

local FIRST_GRANT_DELAY = 10.0        -- 第一次全船降奇迹的巡回内时点（秒）
local SECOND_GRANT_DELAY = 15.0       -- 第二次全船降奇迹的巡回内时点（秒）
local SECOND_GRANT_MIN_LEVEL = 10     -- 触发第二次所需的主祭司等级
local CHARM_GRANT_DELAY = 4.9         -- 护符自身奇迹的巡回内时点（秒）

local SCAN_FAST_INTERVAL = 0.5
local SCAN_SLOW_INTERVAL = 2.0
local SCAN_IDLE_INTERVAL = 10.0
local FAST_PHASE_DURATION = 20.0
-- 与旧XML一致：AI(bot)生效者不触发全船降奇迹（自身效果不受影响）
local CREW_GRANT_REQUIRES_PLAYER = true

local DEBUG_LOG = false

math.randomseed(os.time())

-- 缓存 Identifier，避免每次扫描重复字符串转换
local OUTFIT_FLAG_ID = Identifier(OUTFIT_FLAG_AFFLICTION)
local FLAG_ID = Identifier(FLAG_AFFLICTION)

local round_start_time = nil   -- 本巡回开始时刻(Timing.TotalTime)，nil=不在巡回中
local crew_grants_done = 0     -- 本巡回全船降奇迹已结算次数
local charm_granted = setmetatable({}, { __mode = "k" }) -- character -> true，本巡回护符奇迹是否已给（弱键，角色回收后条目自动消失）
local all_settled = false      -- 上次扫描后是否所有效果均已结算（用于降频）
local in_fast_phase = true     -- 巡回开始后 FAST_PHASE_DURATION 秒内为 true（由 scan 按 round_time 更新）
local elapsed = 0

-- LuaCs 未把游戏内部的 Timing 类注册为 Lua 全局（Timing.TotalTime / Timing.Step 会 nil 索引），
-- 统一用注册过的 Timer.GetTime() 取游戏时间
local function now_time()
    if Timer ~= nil and Timer.GetTime ~= nil then
        return Timer.GetTime()
    end
    return 0
end
local THINK_STEP_FALLBACK = 1.0 / 60.0   -- think 钩子缺 delta_time 时的兜底步长（原 Timing.Step）

local function log(message)
    if DEBUG_LOG then
        print("Sanae.MiracleTalent: " .. message)
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

    -- ApplyAffliction 会按 100/MaxVitality 缩放强度，施加后立即写回目标强度
    health.ApplyAffliction(limb, prefab.Instantiate(strength))
    local applied = health.GetAffliction(affliction_identifier)
    if applied ~= nil then
        applied.Strength = strength
    end
    return true
end

-- 空之奇迹附带：随机技能+7（gainedFromAbility=true，等价于 GiveSkill triggertalents="false"）
local function give_random_skill(character, amount)
    if character == nil or character.Info == nil or character.Info.Job == nil then
        return
    end

    local skills = {}
    for skill in character.Info.Job.GetSkills() do
        if skill ~= nil and skill.Identifier ~= nil then
            table.insert(skills, skill.Identifier)
        end
    end
    if #skills == 0 then
        return
    end

    local pick = skills[math.random(#skills)]
    character.Info.IncreaseSkillLevel(pick, amount, true, false)
end

-- 给目标降下随机一种奇迹；已有奇迹（标记>0）则跳过
local function grant_miracle(character)
    if character == nil or character.Removed or character.IsDead then
        return false
    end
    if get_affliction_strength(character, FLAG_ID) > 0 then
        return false
    end

    local pick = MIRACLES[math.random(#MIRACLES)]
    if not apply_affliction(character, pick, 1) then
        return false
    end
    apply_affliction(character, FLAG_AFFLICTION, 1)

    if pick == SKY_MIRACLE then
        give_random_skill(character, SKY_MIRACLE_SKILL_AMOUNT)
    end

    log("给予 " .. tostring(character.Name) .. " 奇迹 " .. tostring(pick))
    return true
end

-- 主祭司：ID 最小的生效者（多人同时生效时全船效果只结算一次）
local function get_primary_wearer(wearers)
    local primary = nil
    for _, character in ipairs(wearers) do
        if (not CREW_GRANT_REQUIRES_PLAYER or character.IsBot ~= true)
            and (primary == nil or character.ID < primary.ID) then
            primary = character
        end
    end
    return primary
end

-- 除主祭司外的随机清醒船员：存活、未失能、同队、无奇迹标记、未携带早苗标记
local function pick_crew_target(primary)
    local candidates = {}
    for character in Character.CharacterList do
        if character ~= nil
            and character ~= primary
            and character.IsHuman == true
            and character.Removed ~= true
            and character.IsDead ~= true
            and character.IsIncapacitated ~= true
            and character.IsOnPlayerTeam == true
            and character.TeamID == primary.TeamID
            and get_affliction_strength(character, FLAG_ID) <= 0
            and get_affliction_strength(character, OUTFIT_FLAG_ID) <= 0 then
            table.insert(candidates, character)
        end
    end

    if #candidates == 0 then
        return nil
    end

    return candidates[math.random(#candidates)]
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

local function has_charm(character)
    if character == nil or character.Inventory == nil then
        return false
    end

    local ok, found = pcall(function()
        for item in character.Inventory.AllItems do
            if item ~= nil and item.HasTag(CHARM_TAG) then
                return true
            end
        end
        return false
    end)

    return ok and found == true
end

-- 单次扫描：一次遍历收集生效者，随后完成全部判定；返回是否所有效果均已结算
local function scan(now)
    local round_time = now - round_start_time

    -- fast 阶段判定只用扫描时的 round_time，think 里不再每帧跨语言取时间
    if in_fast_phase and round_time >= FAST_PHASE_DURATION then
        in_fast_phase = false
    end

    -- 一次遍历收集全部存活生效者（带 Touhou_Sanae_Character_Effect 的人类角色）
    local wearers = {}
    for character in Character.CharacterList do
        if character ~= nil
            and character.IsHuman == true
            and character.Removed ~= true
            and character.IsDead ~= true
            and get_affliction_strength(character, OUTFIT_FLAG_ID) > 0 then
            table.insert(wearers, character)
        end
    end

    -- 1) 全船降奇迹：第一次/第二次（主祭司等级>=10），只由主祭司结算
    if crew_grants_done < 2 and round_time >= FIRST_GRANT_DELAY then
        local primary = get_primary_wearer(wearers)
        if primary ~= nil then
            local due_first = crew_grants_done < 1
            local due_second = crew_grants_done < 2 and round_time >= SECOND_GRANT_DELAY
                and get_character_level(primary) >= SECOND_GRANT_MIN_LEVEL

            if due_second or due_first then
                local target = pick_crew_target(primary)
                if target ~= nil and grant_miracle(target) then
                    crew_grants_done = crew_grants_done + 1
                end
                -- 没有合法目标时本次不消耗次数，下个扫描周期再试
            end
        end
    end

    -- 降频判据（不参与上面的发放判定，只决定扫描间隔）：
    --   第二次降临只由主祭司结算，已过点时点且主祭司等级不足（或没有主祭司）时视为不会再发生；
    --   无生效者时全船部分也无从结算。判为完成后仍按兜底间隔继续扫描并尝试发放，不会漏发。
    local settled
    if crew_grants_done >= 2 or #wearers == 0 then
        settled = true
    elseif crew_grants_done >= 1 and round_time >= SECOND_GRANT_DELAY then
        local primary = get_primary_wearer(wearers)
        settled = primary == nil or get_character_level(primary) < SECOND_GRANT_MIN_LEVEL
    else
        settled = false
    end

    -- 2) 护符自身奇迹：每个生效者独立，每巡回一次
    for _, wearer in ipairs(wearers) do
        if not charm_granted[wearer] then
            if round_time >= CHARM_GRANT_DELAY and has_charm(wearer) then
                grant_miracle(wearer)
                charm_granted[wearer] = true
            else
                settled = false
            end
        end
    end

    return settled
end

Hook.Add("roundStart", "Sanae.MiracleTalent.RoundStart", function()
    round_start_time = now_time()
    crew_grants_done = 0
    charm_granted = setmetatable({}, { __mode = "k" })
    all_settled = false
    in_fast_phase = true
    elapsed = 0
end)

Hook.Add("roundEnd", "Sanae.MiracleTalent.RoundEnd", function()
    round_start_time = nil
end)

Hook.Add("think", "Sanae.MiracleTalent.Update", function(delta_time)
    -- 服务端权威：联机客户端不执行，避免与服务端同步冲突
    if CLIENT and not Game.IsSingleplayer then return end
    if round_start_time == nil then return end

    -- 自适应扫描频率：fast 阶段由 scan 内维护的 in_fast_phase 判断，不再每帧取游戏时间
    local interval = SCAN_SLOW_INTERVAL
    if in_fast_phase then
        interval = SCAN_FAST_INTERVAL
    elseif all_settled then
        interval = SCAN_IDLE_INTERVAL
    end

    elapsed = elapsed + (delta_time or THINK_STEP_FALLBACK)
    if elapsed < interval then
        return
    end
    elapsed = 0

    all_settled = scan(now_time())
end)
