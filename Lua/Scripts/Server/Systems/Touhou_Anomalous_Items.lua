-- 异常物品系统：8 件收容物 + 铅封收容箱（壳在 Items/AnomalousItems.xml）。
-- 设计核心：一件物品一条规则；think 节拍 1 秒一次统一处理；
-- 进了收容箱（祖先容器带 Touhou_Containment tag）效果全停——音效一起掐：
-- 持续音都挂在 LightComponent 的 StatusEffect OnActive 上，Lua 翻 IsOn 就无声。
-- TH-08 走 StatusEffect LuaHook（车喇叭同款 RangedWeapon 先出声）。
-- 预言/广播这类延迟演出用 Timer.Wait（以太回收机同款计时）。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local SCAN_INTERVAL = 1.0
local THINK_STEP_FALLBACK = 1.0 / 60.0

local RETURN_AWAY_SECONDS = 90          -- TH-01：离开玩家潜艇且无人看守多久后"回来"
local RETURN_NEAR_DISTANCE_SQ = 2000 * 2000
local REPULSE_DISTANCE_SQ = 600 * 600   -- TH-03：排斥场
local PLANT_DISTANCE_SQ = 1200 * 1200   -- TH-04：盆栽感应
local RADIO_DISTANCE_SQ = 500 * 500     -- TH-07：信号收听
local TRUMPET_DISTANCE_SQ = 800 * 800   -- TH-08：失笑范围
local NOTICE_DISTANCE_SQ = 800 * 800    -- 通用"附近有人"提示

local DEBUG_LOG = false
local function log(message)
    if DEBUG_LOG then
        print("Anomaly: " .. message)
    end
end

-- 各物品的冷却/阶段状态，键是 item 本体；Removed 就丢
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

local function aboard_main_sub(item)
    local main = Submarine.MainSub
    if main == nil then return false end
    local holder = walk_up(item)
    if holder ~= nil and not holder.Removed then
        return holder.Submarine == main
    end
    return item.Submarine == main
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

local function send_to_crew(submarine, sender, text)
    if submarine == nil then return end
    for character in Character.CharacterList do
        if character.IsHuman and not character.IsDead and not character.Removed
                and character.Submarine == submarine then
            send_to_character(character, sender, text)
        end
    end
end

local function find_item_prefab(identifier)
    local ok, prefab = pcall(function() return ItemPrefab.Prefabs[identifier] end)
    if ok and prefab ~= nil then return prefab end
    ok, prefab = pcall(function() return ItemPrefab.Find("", identifier) end)
    if ok then return prefab end
    return nil
end

-- ============ TH-01 会回来的货箱 ============

local RETURN_STAGE_MESSAGES = {
    "……气闸附近多了一个箱子。你记得它被留在了上个站点。",
    "箱子又在响。这次它离你更近了。",
    "它不再敲箱壁了。它在敲舱壁。",
    "它回来了。没有人讨论这件事。",
}

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

local CABINET_LOOT = {
    "battery", "oxygentank", "bandage", "flashlight",
    "iron", "ethanol", "rum", "weldingfuel",
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

    local swapped = false
    for _, old in ipairs(contents) do
        if math.random() < 0.10 and not old.Removed then
            local prefab = find_item_prefab(CABINET_LOOT[math.random(#CABINET_LOOT)])
            if prefab ~= nil then
                Entity.Spawner.AddItemToSpawnQueue(prefab, inv)
                Entity.Spawner.AddItemToRemoveQueue(old)
                swapped = true
                log("TH-02 换走 " .. old.Prefab.Identifier.Value)
            end
        end
    end
    if swapped then
        local pos = item.WorldPosition
        for character in Character.CharacterList do
            if character.IsHuman and not character.IsDead and not character.Removed
                    and dist_sq(pos, character.WorldPosition) < NOTICE_DISTANCE_SQ then
                send_to_character(character, "???", "柜子里有些东西不太一样了。")
            end
        end
    end
end

-- ============ TH-03 「五十米」样本 ============

local function update_repulse_stone(item, s)
    local _, contained = walk_up(item)
    set_light(item, not contained)
    if contained then return end
    -- 敌我不分：靠近的一切活物都恶心、眩晕、战栗
    local pos = item.WorldPosition
    for character in Character.CharacterList do
        if not character.IsDead and not character.Removed
                and dist_sq(pos, character.WorldPosition) < REPULSE_DISTANCE_SQ then
            TT.apply_affliction(character, "Touhou_Anomaly_Repulse", 30)
        end
    end
end

-- ============ TH-04 基列木盆栽 ============

local PLANT_ALERTS = {
    "盆栽的叶片竖了起来。",
    "盆栽的叶片齐齐转向某个方向。",
}

local function update_gilead_sprout(item, s)
    local _, contained = walk_up(item)
    if contained then
        set_light(item, false)
        return
    end
    -- 感应最近的敌意生物：叶片发亮（活体怪物雷达）
    local pos = item.WorldPosition
    local sensed = false
    for other in Character.CharacterList do
        if not other.IsDead and not other.Removed
                and not other.IsHuman and not other.IsPet
                and dist_sq(pos, other.WorldPosition) < PLANT_DISTANCE_SQ then
            sensed = true
            break
        end
    end
    set_light(item, sensed)

    s.alertCool = (s.alertCool or 0) - SCAN_INTERVAL
    if sensed and s.alertCool <= 0 then
        s.alertCool = 45
        local holder = walk_up(item)
        if holder ~= nil then
            send_to_character(holder, "???", PLANT_ALERTS[math.random(#PLANT_ALERTS)])
        end
    end
end

-- ============ TH-05 假月球碎片 ============

local SHARD_SIGHTINGS = {
    "余光里有个轮廓从你身后走过去了。数量对不上。",
    "光膜的影子比你们多一个。多出来的那个站着没动。",
    "你确定刚才的剪影不属于任何人。",
    "影子在你们停下之后，又走了一步。",
}

local function update_fake_moon_shard(item, s)
    local holder, contained = walk_up(item)
    set_light(item, not contained)
    if contained or holder == nil or holder.IsDead then return end
    -- 照明越强，看得越多
    TT.apply_affliction(holder, "psychosis", 1)
    s.sightCool = (s.sightCool or 0) - SCAN_INTERVAL
    if s.sightCool <= 0 then
        s.sightCool = 240 + math.random(120)
        send_to_character(holder, "???", SHARD_SIGHTINGS[math.random(#SHARD_SIGHTINGS)])
        -- TODO:「举着时怪物侦测距离加倍」要核实 EnemyAIController 仇恨接口，下版本再做
    end
end

-- ============ TH-06 灵异珠残响 ============

local ECHO_WHISPERS = {
    "你听见自己的声音在描述接下来的一分钟。",
    "『水会从你左边进来。』——左边没有水管。",
    "『别回头。』描述到此为止。",
    "它描述了你的死法。用词很礼貌。",
    "『有人数错了人数。』你不确定数错的是哪一边。",
    "这段描述用的是你从没学过的语言，但你听懂了。",
    "『它知道你带着它。』",
}

local PROPHECIES = {
    { text = "『你会在黑暗里待一小会儿。』", kind = "lights" },
    { text = "『有一扇门会替谁打开。』", kind = "door" },
    { text = "『什么都不会发生。』", kind = "none" },
}

local function flicker_lights(submarine, seconds)
    local touched = {}
    for it in Item.ItemList do
        if it.Submarine == submarine then
            pcall(function()
                local light = it.GetComponentString("LightComponent")
                if light ~= nil and light.IsOn then
                    light.IsOn = false
                    table.insert(touched, light)
                end
            end)
        end
    end
    Timer.Wait(function()
        for _, light in ipairs(touched) do
            pcall(function()
                if light ~= nil then light.IsOn = true end
            end)
        end
    end, seconds * 1000)
end

local function open_random_door(submarine)
    local doors = {}
    for it in Item.ItemList do
        if it.Submarine == submarine then
            local okDoor, isDoor = pcall(function() return it.HasTag("door") end)
            if okDoor and isDoor then
                table.insert(doors, it)
            end
        end
    end
    if #doors == 0 then return false end
    local target = doors[math.random(#doors)]
    local ok = pcall(function()
        target.GetComponentString("Door").OpenState = 1
    end)
    return ok
end

local function update_orb_echo(item, s)
    local holder, contained = walk_up(item)
    set_light(item, not contained)
    if contained or holder == nil or holder.IsDead then return end

    TT.apply_affliction(holder, "psychosis", 1)

    s.whisperCool = (s.whisperCool or 0) - SCAN_INTERVAL
    if s.whisperCool <= 0 then
        s.whisperCool = 60 + math.random(60)
        send_to_character(holder, "???", ECHO_WHISPERS[math.random(#ECHO_WHISPERS)])
    end

    s.prophecyCool = (s.prophecyCool or 0) - SCAN_INTERVAL
    if s.prophecyCool <= 0 then
        s.prophecyCool = 360 + math.random(240)
        local prophecy = PROPHECIES[math.random(#PROPHECIES)]
        send_to_character(holder, "???", prophecy.text)
        Timer.Wait(function()
            if item.Removed then return end
            local _, stillContained = walk_up(item)
            if stillContained then
                send_to_character(holder, "???", "预言没有实现。箱子里的它似乎叹了口气。")
                return
            end
            if prophecy.kind == "lights" then
                flicker_lights(holder.Submarine, 8)
                send_to_character(holder, "???", "预言实现了。灯灭的那八秒里，敲击声没有停。")
            elseif prophecy.kind == "door" then
                if open_random_door(holder.Submarine) then
                    send_to_character(holder, "???", "你听见某处的舱门自己开了。")
                end
            end
            -- kind == "none" 就什么都不做。这才是最糟的。
        end, (60 + math.random(60)) * 1000)
    end
end

-- ============ TH-07 深渊信号收音机 ============

local RADIO_BROADCASTS = {
    "【信号·摘录】内容描述了一艘潜艇的沉没。未记录舷号。艇员名单与你们完全一致。",
    "【信号·摘录】本次内容为菜谱。步骤完整。最后一行的配料是你们的名字。",
    "【信号·摘录】它以七种语言重复同一段话。第八种语言没人认得，但船上有人听懂了。",
    "【信号·摘录】『……不要回答这个信号。』本条信号编号与当前信号相同。",
    "【信号·摘录】『……左舷压力壳的裂缝出现在第四次巡逻之后。』批注：谁。",
}

local function update_abyss_radio(item, s)
    local _, contained = walk_up(item)
    set_light(item, not contained)
    if contained then return end
    -- 收听者：小幅提速，缓慢疯
    local pos = item.WorldPosition
    for character in Character.CharacterList do
        if character.IsHuman and not character.IsDead and not character.Removed
                and dist_sq(pos, character.WorldPosition) < RADIO_DISTANCE_SQ then
            TT.apply_affliction(character, "Touhou_Anomaly_Signal_Listener", 10)
            TT.apply_affliction(character, "psychosis", 1)
        end
    end

    s.broadcastCool = (s.broadcastCool or 0) - SCAN_INTERVAL
    if s.broadcastCool <= 0 then
        s.broadcastCool = 120 + math.random(120)
        local holder = walk_up(item)
        local submarine = (holder ~= nil) and holder.Submarine or item.Submarine
        send_to_crew(submarine, "深渊信号", RADIO_BROADCASTS[math.random(#RADIO_BROADCASTS)])
    end
end

-- ============ TH-08 喇叭圣母的呼唤 ============

local function update_trumpet(item, s)
    -- 本体没有持续效果，只在这里给吹奏冷却倒计时
    s.blowCool = math.max(0, (s.blowCool or 0) - SCAN_INTERVAL)
end

Hook.Add("Touhou.AnomalyTrumpet.blow", "Touhou.AnomalyTrumpet.blow",
    function(effect, deltaTime, item, targets, worldPosition)
        local ok, err = pcall(function()
            local user = targets ~= nil and targets[1] or nil
            if user == nil or user.IsDead or user.Removed then return end
            local s = state[item]
            if s ~= nil and (s.blowCool or 0) > 0 then return end
            if s == nil then s = get_state(item) end
            s.blowCool = 3
            -- 失笑：不分敌我（吹奏者自己是圣母点名的人，免疫）
            local pos = user.WorldPosition
            for character in Character.CharacterList do
                if character ~= user and character.IsHuman and not character.IsDead and not character.Removed
                        and dist_sq(pos, character.WorldPosition) < TRUMPET_DISTANCE_SQ then
                    TT.apply_affliction(character, "Touhou_Anomaly_Forced_Laugh", 5)
                end
            end
            log("TH-08 吹奏，失笑半径内生效")
        end)
        if not ok then
            print("AnomalyTrumpet 错误: " .. tostring(err))
        end
    end)

-- ============ 收容箱本体 ============

local function update_containment_crate(item, s)
    -- 紫色密封灯：里面装着收容物就亮
    local hasAnomaly = false
    pcall(function()
        local inv = item.OwnInventory
        if inv ~= nil then
            for containedItem in inv.FindAllItems(nil, true) do
                if containedItem.HasTag("Touhou_Anomalous") then
                    hasAnomaly = true
                    break
                end
            end
        end
    end)
    set_light(item, hasAnomaly)
end

-- ============ 主循环 ============

local UPDATERS = {
    Touhou_Containment_Crate = update_containment_crate,
    Touhou_Anomaly_Returning_Box = update_returning_box,
    Touhou_Anomaly_Bigger_Cabinet = update_bigger_cabinet,
    Touhou_Anomaly_Repulse_Stone = update_repulse_stone,
    Touhou_Anomaly_Gilead_Sprout = update_gilead_sprout,
    Touhou_Anomaly_Fake_Moon_Shard = update_fake_moon_shard,
    Touhou_Anomaly_Orb_Echo = update_orb_echo,
    Touhou_Anomaly_Abyss_Radio = update_abyss_radio,
    Touhou_Anomaly_Laugh_Trumpet = update_trumpet,
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
