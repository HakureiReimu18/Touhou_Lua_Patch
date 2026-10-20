-- 已停载：机制迁往 CSharp/Shared/AnomalousItems.cs；保留原实现作比较和回滚参考。
-- 不能只取消 dofile 注释就回滚，XML 组件、声音与 affliction 衰减也要一起恢复。
-- 异常物品系统（旧版：TH-01 会回来的货箱 + TH-02 比里面大的柜子 + TH-03「五十米」样本
-- + TH-08 点名喇叭 + 铅封收容箱）。
-- 设计核心：一件物品一条规则；think 节拍 1 秒一次统一处理；
-- 装进收容箱（祖先容器带 Touhou_Containment tag）效果全停——持续音挂在
-- LightComponent 的 StatusEffect OnActive 上，Lua 翻 IsOn 就连声音一起掐。
-- TH-08 是纯事件驱动（吹响才跑），不进 think 扫描。
-- 休眠的 4 件在 _Archive/AnomalousItems_v1_全8件.xml，全量旧脚本在
-- _Archive/Touhou_Anomalous_Items_v1_全8件.lua，放出时搬回去。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local SCAN_INTERVAL = 1.0
local THINK_STEP_FALLBACK = 1.0 / 60.0

local REPULSE_DISTANCE_SQ = 600 * 600   -- TH-03：排斥场（约 6 米）
local REPULSE_NEAR_SQ = 300 * 300       -- TH-03：3 米内症状加重
local REPULSE_WHISPER_SQ = 200 * 200    -- TH-03：2 米内低语
local CORPSE_REJECT_DISTANCE_SQ = 600 * 600 -- TH-03：死者驳回半径（与排斥场同圈）
local SWAP_DISTANCE_SQ = 1500 * 1500      -- TH-08：点名半径（约 15 米）
local NOTICE_DISTANCE_SQ = 800 * 800    -- 通用"附近有人"提示
local RETURN_AWAY_SECONDS = 90          -- TH-01：离船且无人看守多久后"回来"
local RETURN_NEAR_DISTANCE_SQ = 2000 * 2000

local DEBUG_LOG = false
local function log(message)
    if DEBUG_LOG then
        print("Anomaly: " .. message)
    end
end

-- 各物品的冷却/状态，键是 item 本体；Removed 就丢
local state = {}

local function get_state(item)
    local s = state[item]
    if s == nil then
        s = {}
        state[item] = s
    end
    return s
end

-- ============ 小工具 ============

-- 沿父容器往上走：装进收容箱 → 返回 nil,true；在角色身上/手里 → 返回该角色,false
local function walk_up(item)
    local current = item
    for _ = 1, 8 do
        local inv = current.ParentInventory
        if inv == nil then return nil, false end
        local owner = inv.Owner
        if owner == nil then return nil, false end
        -- 有 OwnInventory 的持有者是容器物品；角色没有，到此为止
        local ok, ownInv = pcall(function() return owner.OwnInventory end)
        if not ok or ownInv == nil then
            return owner, false
        end
        local okTag, isCrate = pcall(function() return owner.HasTag("Touhou_Containment") end)
        if okTag and isCrate then
            return nil, true
        end
        current = owner
    end
    return nil, false
end

local function set_light(item, on)
    pcall(function()
        local light = item.GetComponentString("LightComponent")
        if light ~= nil then
            light.IsOn = on
        end
    end)
end

local function dist_sq(a, b)
    local dx = a.X - b.X
    local dy = a.Y - b.Y
    return dx * dx + dy * dy
end

local function send_to_character(character, sender, text)
    pcall(function()
        local client = Util.FindClientCharacter(character)
        if client ~= nil then
            Game.SendDirectChatMessage(sender, text, nil, ChatMessageType.MessageBox, client)
        end
    end)
end

local function find_item_prefab(identifier)
    local ok, prefab = pcall(function() return ItemPrefab.Prefabs[identifier] end)
    if ok and prefab ~= nil then return prefab end
    ok, prefab = pcall(function() return ItemPrefab.Find("", identifier) end)
    if ok then return prefab end
    return nil
end

-- 角色→秒 的冷却表：走一秒倒计时，过期/离场/死亡就清
local function tick_cooldowns(tbl)
    for character, remaining in pairs(tbl) do
        if character.Removed or character.IsDead then
            tbl[character] = nil
        else
            local left = remaining - SCAN_INTERVAL
            if left <= 0 then
                tbl[character] = nil
            else
                tbl[character] = left
            end
        end
    end
end

-- ============ TH-01 会回来的货箱 ============

local RETURN_STAGE_MESSAGES = {
    "……气闸附近多了一个箱子。你记得它被留在了上个站点。",
    "箱子又在响。这次它离你更近了。",
    "它不再敲箱壁了。它在敲舱壁。",
    "它回来了。没有人讨论这件事。",
}

local function aboard_main_sub(item)
    local main = Submarine.MainSub
    if main == nil then return false end
    local holder = walk_up(item)
    if holder ~= nil and not holder.Removed then
        return holder.Submarine == main
    end
    return item.Submarine == main
end

local function pick_return_target()
    -- 随机挑玩家潜艇上的一个活人——箱子"回到"他身边
    local main = Submarine.MainSub
    if main == nil then return nil end
    local candidates = {}
    for character in Character.CharacterList do
        if character.IsHuman and not character.IsDead and not character.Removed
                and character.Submarine == main then
            table.insert(candidates, character)
        end
    end
    if #candidates == 0 then return nil end
    return candidates[math.random(#candidates)]
end

local function update_returning_box(item, s)
    local _, contained = walk_up(item)
    set_light(item, not contained) -- 箱内轻响：在箱外（哪怕渊洋底）都响
    if contained then
        s.away = 0
        return
    end
    if aboard_main_sub(item) then
        s.away = 0
        return
    end
    -- 附近有真人玩家看着它，它就按兵不动（站点里的 NPC 不算——它们看了也白看）
    local pos = item.WorldPosition
    for character in Character.CharacterList do
        if character.IsHuman and not character.IsDead and not character.Removed
                and Util.FindClientCharacter(character) ~= nil
                and dist_sq(pos, character.WorldPosition) < RETURN_NEAR_DISTANCE_SQ then
            s.away = 0
            return
        end
    end
    s.away = (s.away or 0) + SCAN_INTERVAL
    if s.away < RETURN_AWAY_SECONDS then return end
    s.away = 0

    local target = pick_return_target()
    if target == nil then return end
    -- 从原处（包括别人的背包）拽出来，塞到目标身边
    pcall(function()
        local inv = item.ParentInventory
        if inv ~= nil then inv.RemoveItem(item) end
    end)
    -- 跨潜艇传送没核实过引擎行为，多试几路，全失败就只发消息
    local okTp = pcall(function()
        item.Submarine = Submarine.MainSub
        local offset = Vector2(math.random(-60, 60), math.random(-60, 60))
        item.body.TeleportTo(target.WorldPosition + offset, 0, true)
    end)
    if not okTp then
        okTp = pcall(function()
            item.body.SetTransform(target.WorldPosition, 0)
        end)
    end
    log("TH-01 传送 " .. (okTp and "成功" or "失败（只发了消息）"))

    local stage = tonumber(item.GetComponentString("MemoryComponent").Value) or 0
    stage = stage + 1
    item.GetComponentString("MemoryComponent").Value = tostring(stage)
    send_to_character(target, "???", RETURN_STAGE_MESSAGES[math.min(stage, #RETURN_STAGE_MESSAGES)])
end

-- ============ TH-02 比里面大的柜子 ============

-- 换进来的东西：日常杂物为主，偶尔值钱
local CABINET_LOOT = {
    "battery", "Byproduct_Alchemical", "flashlight", "Kappa_Miningdrill",
    "ethanol", "rum", "titanium", "Touhou_Magic_Potion_Candy", "Plant_Essential_Oil", "Touhou_Apple",
}

local function update_bigger_cabinet(item, s)
    local _, contained = walk_up(item)
    if contained then return end
    s.cool = (s.cool or 0) - SCAN_INTERVAL
    if s.cool > 0 then return end
    s.cool = 180

    local inv = item.OwnInventory
    if inv == nil then return end
    local contents = {}
    for containedItem in inv.FindAllItems(nil, true) do
        -- 收容物它不碰——它知道那些是什么
        if not containedItem.HasTag("Touhou_Anomalous") then
            table.insert(contents, containedItem)
        end
    end
    if #contents == 0 then return end

    -- 每 3 分钟必换走一件（随机挑），让玩家切实感到"这柜子不对劲"
    local old = contents[math.random(#contents)]
    if old.Removed then return end
    local prefab = find_item_prefab(CABINET_LOOT[math.random(#CABINET_LOOT)])
    if prefab == nil then return end
    Entity.Spawner.AddItemToSpawnQueue(prefab, inv)
    Entity.Spawner.AddItemToRemoveQueue(old)
    log("TH-02 换走 " .. old.Prefab.Identifier.Value)

    local pos = item.WorldPosition
    for character in Character.CharacterList do
        if character.IsHuman and not character.IsDead and not character.Removed
                and dist_sq(pos, character.WorldPosition) < NOTICE_DISTANCE_SQ then
            send_to_character(character, "???", "柜子里有些东西不太一样了。")
        end
    end
end

-- ============ TH-03「五十米」样本：生死禁区 ============
-- 规则：活物越靠近越难受（分层）；死者会被送出圈外——样本不收藏死者。

local REPULSE_FIRST_HIT = "你的胃拧了起来，太阳穴突突地跳。这里有什么东西不希望任何人靠近。"

local CORPSE_REJECT_MESSAGES = {
    "死亡被婉拒了。",
    "尸体不在登记范围内。样本不收。",
    "它把死者送了出去。它说它不是墓地。",
}

local REPULSE_WHISPERS = {
    "你在它的记忆里。它记得每一个走到五十米的人。",
    "再靠近一步。它想再看看你。——别听它的。",
    "五十米不是它撑开的距离。是它剩下的。",
}

local function update_repulse_stone(item, s)
    local _, contained = walk_up(item)
    set_light(item, not contained)
    if contained then return end

    if s.notified == nil then s.notified = {} end
    if s.whispered == nil then s.whispered = {} end

    local pos = item.WorldPosition
    for character in Character.CharacterList do
        if character.Removed then
            s.notified[character] = nil
            s.whispered[character] = nil
        elseif character.IsDead then
            -- 死者驳回：尸体被送回圈外（约 15 米，方向随机；跨墙它也送得到）
            if dist_sq(pos, character.WorldPosition) < CORPSE_REJECT_DISTANCE_SQ then
                local angle = math.random() * math.pi * 2
                local okTp = pcall(function()
                    character.TeleportTo(pos + Vector2(math.cos(angle) * 1500, math.sin(angle) * 1500), true)
                end)
                if okTp then
                    log("TH-03 驳回了一具尸体")
                    if (s.corpseMsgCool or 0) <= 0 then
                        s.corpseMsgCool = 120
                        for viewer in Character.CharacterList do
                            if viewer.IsHuman and not viewer.IsDead and not viewer.Removed
                                    and dist_sq(pos, viewer.WorldPosition) < NOTICE_DISTANCE_SQ then
                                send_to_character(viewer, "???", CORPSE_REJECT_MESSAGES[math.random(#CORPSE_REJECT_MESSAGES)])
                            end
                        end
                    end
                end
            end
        else
            local d2 = dist_sq(pos, character.WorldPosition)
            if d2 < REPULSE_DISTANCE_SQ then
                -- 分层压迫：3 米内强度 80（接近全速衰减+色差拉满），圈边 30
                local amount = 30
                if d2 < REPULSE_NEAR_SQ then
                    amount = 80
                end
                TT.apply_affliction(character, "Touhou_Anomaly_Repulse", amount)
                -- 进场提示每人一条，冷却 90 秒，别刷屏
                if (s.notified[character] or 0) <= 0 then
                    s.notified[character] = 90
                    send_to_character(character, "???", REPULSE_FIRST_HIT)
                end
                -- 贴脸低语（2 米内才听得到）
                if d2 < REPULSE_WHISPER_SQ and (s.whispered[character] or 0) <= 0 then
                    s.whispered[character] = 60
                    send_to_character(character, "???", REPULSE_WHISPERS[math.random(#REPULSE_WHISPERS)])
                end
            end
        end
    end

    s.corpseMsgCool = math.max(0, (s.corpseMsgCool or 0) - SCAN_INTERVAL)
    tick_cooldowns(s.notified)
    tick_cooldowns(s.whispered)
end

-- ============ TH-08 可编辑配置：点名名单（白名单） ============
-- 只有名单内的角色能被点名换位（默认拒绝，防把大体型生物卡进船舱）。
-- 填角色 prefab identifier（不是显示名），想加谁就加一行。
local TH08_SWAP_WHITELIST = {
    "human",      -- 人类：船员、站点 NPC、海盗都算这个 prefab
    "crawler",    -- 藻鬃爬行者
    "mudraptor",  -- 泥沼猛龙（迅猛龙）
    "humanhusk",  -- 画皮感染者
    -- "Touhou_TheConverted",  -- 易者（未实装，实装后取消注释，identifier 以实际为准）
}
local TH08_SWAP_SET = {}
for _, id in ipairs(TH08_SWAP_WHITELIST) do
    TH08_SWAP_SET[id] = true
end

local function is_swappable(character)
    local ok, identifier = pcall(function() return character.Prefab.Identifier.Value end)
    return ok and identifier ~= nil and TH08_SWAP_SET[identifier] == true
end

-- ============ TH-08 喇叭圣母的呼唤：点名换位 ============
-- 规则：吹响时，圣母从 15 米内随机点名一位活物，与吹响者交换位置。
-- 纯事件驱动（只在吹响时跑一趟），think 节拍扫不到它，零持续开销。
-- 曾考虑"感染笑连锁"（全局每秒人×人扫描 + 人声音频适配），性能与管线成本双高，弃。

local SWAP_USER_LINES = {
    "圣母点名了。上台吧。",
    "你换了个位置。这不是你选的。",
    "喇叭声停下的时候，你已经在别处了。",
}

local SWAP_OTHER_LINES = {
    "你和别人换了位置。",
    "你听见喇叭声从你原来的位置传来。",
    "圣母今晚点了你的名。",
}

Hook.Add("Touhou.AnomalyTrumpet.blow", "Touhou.AnomalyTrumpet.blow",
    function(effect, deltaTime, item, targets, worldPosition)
        local ok, err = pcall(function()
            local user = targets ~= nil and targets[1] or nil
            if user == nil or user.IsDead or user.Removed then return end

            -- 半径内先数活物，再按名单过滤（名单外不点名，防大体型卡进船舱）
            local candidates = {}
            local anyLiving = false
            for character in Character.CharacterList do
                if character ~= user and not character.IsDead and not character.Removed
                        and dist_sq(user.WorldPosition, character.WorldPosition) < SWAP_DISTANCE_SQ then
                    anyLiving = true
                    if is_swappable(character) then
                        table.insert(candidates, character)
                    end
                end
            end
            if #candidates == 0 then
                if anyLiving then
                    send_to_character(user, "喇叭", "圣母对那些名字不感兴趣。")
                else
                    send_to_character(user, "喇叭", "圣母今晚没有点名任何人。")
                end
                return
            end
            local chosen = candidates[math.random(#candidates)]

            local userPos = user.WorldPosition
            local chosenPos = chosen.WorldPosition
            -- 互换坐标（引擎传送行为未实测，各自 pcall 兜底；失败一侧原地不动）
            pcall(function() user.TeleportTo(chosenPos, true) end)
            pcall(function() chosen.TeleportTo(userPos, true) end)
            -- 晕场 1.5 秒（vanilla stun），防止落地即战
            TT.apply_affliction(user, "stun", 1.5)
            TT.apply_affliction(chosen, "stun", 1.5)
            send_to_character(user, "喇叭", SWAP_USER_LINES[math.random(#SWAP_USER_LINES)])
            if chosen.IsHuman then
                send_to_character(chosen, "喇叭", SWAP_OTHER_LINES[math.random(#SWAP_OTHER_LINES)])
            end
            log("TH-08 点名交换：" .. tostring(user.Name) .. " <-> " .. tostring(chosen.Name))
        end)
        if not ok then
            print("AnomalyTrumpet 错误: " .. tostring(err))
        end
    end)

-- ============ 收容箱本体 ============

local function update_containment_crate(item, s)
    -- 紫色密封灯：里面装着收容物或异星遗物就亮
    local hasPayload = false
    pcall(function()
        local inv = item.OwnInventory
        if inv ~= nil then
            for containedItem in inv.FindAllItems(nil, true) do
                if containedItem.HasTag("Touhou_Anomalous") or containedItem.HasTag("alienartifact") then
                    hasPayload = true
                    break
                end
            end
        end
    end)
    set_light(item, hasPayload)
end

-- ============ 主循环 ============

local UPDATERS = {
    Touhou_Containment_Crate = update_containment_crate,
    Touhou_Anomaly_Returning_Box = update_returning_box,
    Touhou_Anomaly_Bigger_Cabinet = update_bigger_cabinet,
    Touhou_Anomaly_Repulse_Stone = update_repulse_stone,
}

local scan_elapsed = 0

Hook.Add("think", "Touhou.AnomalousItems.Update", function(delta_time)
    if TT.is_game_paused() then
        return
    end
    scan_elapsed = scan_elapsed + (delta_time or THINK_STEP_FALLBACK)
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    scan_elapsed = 0

    if Item.ItemList == nil then return end

    for item in Item.ItemList do
        if item ~= nil and not item.Removed then
            local okId, identifier = pcall(function() return item.Prefab.Identifier.Value end)
            local updater = okId and UPDATERS[identifier] or nil
            if updater ~= nil then
                local s = get_state(item)
                local ok, err = pcall(updater, item, s)
                if not ok then
                    print("Anomaly[" .. tostring(identifier) .. "] 错误: " .. tostring(err))
                end
            end
        end
    end

    -- 清理已删除物品的残留状态
    for k in pairs(state) do
        if k.Removed then
            state[k] = nil
        end
    end

end)
