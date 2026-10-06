-- 紫天赋「境界的妖怪」——一巡回一次（**全服共享一次**）：把全队清醒的人立刻传送到主潜艇的刷新点。
-- 替代 Events/Touhou_Talents_Event.xml 的 YukariTalent_Effect_Self 事件
-- 与 Items/Clothes/Yukari_gear.xml 里按钮的 TriggerEvent（旧 XML 只注释、未删除，保留备查）。
-- 触发链：装束+ 的按钮（XML）→ 给自己挂上 YukariTalent_Teleport_Request 标记（0.5 秒后自然过期）
--   → 本脚本每 0.2 秒轮询一次，看到标记就执行传送。
-- 「一巡回一次」是全服共享的：任何一个人用掉之后，本巡回其他人（以及重生/新加入的持有者）
-- 都不能再用——用掉时给所有穿装束+的人挂上 YukariTalent_Effect_Self_Effect（resetbetweenrounds），
-- 按钮上的条件认这个标记，所以按钮会一起失效；Lua 侧另有 used_this_round 兜底，
-- 迟到的人即使按钮没来得及补标记也放不出第二发。
-- 传送目标：主潜艇（Submarine.MainSub）的 SpawnType.Human 刷新点；取不到刷新点就整体失败、
-- 标记清掉但不算用过，按钮可以再按（与旧 XML 事件的行为一致：没刷新点就什么都不做）。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT = TT.id("Touhou_Yukari_Character_Effect")
local AFF_REQUEST = TT.id("YukariTalent_Teleport_Request")
local USED_NAME = "YukariTalent_Effect_Self_Effect"
local AFF_USED = TT.id(USED_NAME)

local SCAN_INTERVAL = 0.2
-- 用掉之后：每隔这么久给新出现/重生的装束持有者补一次「已用」标记（只为按钮显示，低频就够）
local CATCHUP_INTERVAL = 2.0
local THINK_STEP_FALLBACK = 1.0 / 60.0

local DEBUG_LOG = false
local scan_elapsed = 0
local catchup_elapsed = 0
-- 本巡回是否已经被任何人用掉（roundStart 复位）
local used_this_round = false

local function log(message)
    if DEBUG_LOG then
        print("Yukari.Teleport: " .. message)
    end
end

-- 把全队清醒的人（含自己）传送到主潜艇刷新点；没有主潜艇/刷新点则返回 false
local function teleport_team(character)
    local main_sub = Submarine.MainSub
    if main_sub == nil then
        return false
    end

    local waypoint = WayPoint.GetRandom(SpawnType.Human, nil, main_sub, false, nil)
    if waypoint == nil then
        return false
    end

    local position = waypoint.WorldPosition
    local team = character.TeamID
    local count = 0
    for other in Character.CharacterList do
        if other ~= nil and other.IsHuman and not other.Removed and not other.IsDead
                and not other.IsUnconscious and other.TeamID == team then
            other.TeleportTo(position)
            count = count + 1
        end
    end

    log(string.format("%s 把 %d 名清醒队员传送到了主潜艇", tostring(character.Name), count))
    return true
end

-- 给所有穿装束+（有天赋效果）的人补上「本巡回已用」标记：按钮上的条件认它，按了也不会生效。
-- 只在用掉之后低频跑（每 CATCHUP_INTERVAL 一次），负责重生/新加入的人。
local function mark_used_for_wearers()
    for character in Character.CharacterList do
        if character ~= nil and not character.Removed and not character.IsDead
                and TT.get_strength(character, AFF_TALENT) > 0
                and TT.get_strength(character, AFF_USED) <= 0 then
            TT.apply_affliction(character, USED_NAME, 100)
        end
    end
end

local function handle_request(character)
    -- 请求标记只表示「按了一次按钮」，无论成败都先清掉
    TT.clear_affliction(character, AFF_REQUEST)

    if TT.get_strength(character, AFF_TALENT) <= 0 then
        return
    end
    if used_this_round then
        -- 全服名额已经被别人用掉：只把按钮标记补上，方便玩家看到失效
        TT.apply_affliction(character, USED_NAME, 100)
        return
    end

    if teleport_team(character) then
        used_this_round = true
        mark_used_for_wearers()
        TT.apply_affliction(character, USED_NAME, 100)
    end
end

Hook.Add("roundStart", "Yukari.Teleport.RoundStart", function()
    used_this_round = false
    catchup_elapsed = 0
end)

Hook.Add("think", "Yukari.Teleport.Update", function(delta_time)
    if TT.is_game_paused() then
        return
    end

    scan_elapsed = scan_elapsed + (delta_time or THINK_STEP_FALLBACK)
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    scan_elapsed = 0

    if used_this_round then
        -- 名额已经用完：不用再找请求标记，只需要低频补齐按钮状态
        catchup_elapsed = catchup_elapsed + SCAN_INTERVAL
        if catchup_elapsed >= CATCHUP_INTERVAL then
            catchup_elapsed = 0
            local ok, err = pcall(mark_used_for_wearers)
            if not ok then
                print("Yukari.Teleport 错误: " .. tostring(err))
            end
        end
        return
    end

    catchup_elapsed = 0
    for character in Character.CharacterList do
        if character ~= nil and not character.Removed
                and TT.get_strength(character, AFF_REQUEST) > 0 then
            local ok, err = pcall(handle_request, character)
            if not ok then
                print("Yukari.Teleport 错误: " .. tostring(err))
            end
        end
    end
end)
