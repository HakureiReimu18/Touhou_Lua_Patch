--[[东方-装束锁定（客户端半区）
    缓存服务端广播的锁定状态给设置 GUI 用。联机时在本地把锁着的物品也设上
    NonPlayerTeamInteractable，让物品栏 UI 立刻拖不动（权威判定还是在服务端，本地只是反馈）。
    单机不走网络，直接读写 TLE.CostumeLock。]]

-- 魔了
if not CLIENT then
    return
end

local MSG_STATE  = "TLE_CL_STATE"
local MSG_STATEP = "TLE_CL_STATEP"
local MSG_CFGGET = "TLE_CL_CFGGET"
local MSG_CFGSET = "TLE_CL_CFGSET"
local MSG_UNLOCK = "TLE_CL_UNLOCK"

local CL = {
    has_state = false,   -- 是否收到过服务端状态（未收到时 GUI 显示"加载中"）
    enabled = true,
    lock_time = 120,
    lock_bots = false,   -- 服务端是否把 AI 船员也纳入锁定
    can_edit = false,
    locked = {},         -- { {char_id, char_name, item_name, item_id, wear}, ... }
    OnState = nil,       -- GUI 挂的回调：配置/权限变了就刷页面
}
TLE.CostumeLockClient = CL

-- 本地已断言锁定的物品：item_id -> true（用于状态变化时还原不再锁定的物品）
local locally_locked = {}

local function find_item(item_id)
    local entity = Entity.FindEntityByID(item_id)
    if entity ~= nil and not entity.Removed and LuaUserData.IsTargetType(entity, "Barotrauma.Item") then
        return entity
    end
    return nil
end

-- 按缓存列表在本地加锁，顺带还原已经不在列表里的
local function apply_local_locks()
    if Game.IsSingleplayer then return end  -- 单机由服务端模块同进程直接设置，无需重复
    local new_set = {}
    for _, entry in ipairs(CL.locked) do
        new_set[entry.item_id] = true
        if not locally_locked[entry.item_id] then
            local item = find_item(entry.item_id)
            if item ~= nil then
                pcall(function() item.NonPlayerTeamInteractable = true end)
            end
        end
    end
    for item_id, _ in pairs(locally_locked) do
        if not new_set[item_id] then
            local item = find_item(item_id)
            if item ~= nil then
                pcall(function() item.NonPlayerTeamInteractable = false end)
            end
        end
    end
    locally_locked = new_set
end

local function read_state(message)
    local enabled = message.ReadBoolean()
    local lock_time = message.ReadUInt16()
    local lock_bots = message.ReadBoolean()
    local n = message.ReadUInt16()
    local list = {}
    for _ = 1, n do
        local char_id = message.ReadUInt16()
        local char_name = message.ReadString()
        local item_name = message.ReadString()
        local item_id = message.ReadUInt16()
        local wear = message.ReadUInt16()
        table.insert(list, {
            char_id = char_id, char_name = char_name,
            item_name = item_name, item_id = item_id, wear = wear,
        })
    end
    CL.enabled = enabled
    CL.lock_time = lock_time
    CL.lock_bots = lock_bots
    CL.locked = list
    CL.has_state = true
    apply_local_locks()
end

-- 只有"配置/权限/锁定列表"真的变了才回调 GUI 刷新。
-- 定期主动拉取状态也会走到这里，不比对的话设置页会被反复重建
local last_signature = nil
local function notify_gui_if_changed()
    local parts = { tostring(CL.enabled), tostring(CL.lock_time), tostring(CL.lock_bots),
        tostring(CL.can_edit), tostring(#CL.locked) }
    for _, e in ipairs(CL.locked) do
        parts[#parts + 1] = tostring(e.char_id) .. ":" .. tostring(e.item_id)
    end
    local sig = table.concat(parts, "|")
    if sig == last_signature then return end
    last_signature = sig
    if CL.OnState ~= nil then pcall(CL.OnState) end
end

if not Game.IsSingleplayer then
    Networking.Receive(MSG_STATE, function(message)
        read_state(message)
        notify_gui_if_changed()
    end)

    -- 定向状态（CFGGET 的回包）：末尾附带本机权限位
    Networking.Receive(MSG_STATEP, function(message)
        read_state(message)
        CL.can_edit = message.ReadBoolean()
        notify_gui_if_changed()
    end)
end

-- 物品可能晚于状态包生成（换装/加入同步），周期性对已锁物品重新断言
local assert_counter = 0
Hook.Add("think", "TLE_CostumeLock_client", function()
    if Game.IsSingleplayer then return end
    assert_counter = assert_counter + 1
    if assert_counter < 30 then return end
    assert_counter = 0
    for item_id, _ in pairs(locally_locked) do
        local item = find_item(item_id)
        if item == nil then
            locally_locked[item_id] = nil
        elseif not item.NonPlayerTeamInteractable then
            pcall(function() item.NonPlayerTeamInteractable = true end)
        end
    end
end)

-- 中途加入/重连的客户端此前收不到锁的广播（服务端不走 IsInteractable 校验，
-- 挡脱下全靠各客户端本地的 NonPlayerTeamInteractable），所以主动定期拉取一次状态，
-- 并在角色创建（中途加入会创建角色）、巡回开始时立即拉一次
local refresh_counter = 0
Hook.Add("think", "TLE_CostumeLock_client_refresh", function()
    if Game.IsSingleplayer then return end
    refresh_counter = refresh_counter + 1
    if refresh_counter < 300 then return end  -- 5 秒
    refresh_counter = 0
    CL.RequestState()
end)

Hook.Add("characterCreated", "TLE_CostumeLock_client_join", function(character)
    if Game.IsSingleplayer then return end
    refresh_counter = 0
    CL.RequestState()
end)

Hook.Add("roundStart", "TLE_CostumeLock_client_round", function()
    if Game.IsSingleplayer then return end
    refresh_counter = 0
    CL.RequestState()
end)

-- 打开设置页时拉一次最新状态；单机直接取本地并立即回调
function CL.RequestState()
    if Game.IsSingleplayer then
        if TLE.CostumeLock ~= nil then
            local s = TLE.CostumeLock.GetState()
            CL.enabled = s.enabled
            CL.lock_time = s.lock_time
            CL.lock_bots = s.lock_bots
            CL.can_edit = s.can_edit
            CL.locked = s.locked
            CL.has_state = true
        end
        if CL.OnState ~= nil then pcall(CL.OnState) end
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_CFGGET)
        Networking.Send(msg)
    end)
end

function CL.SendConfig(enabled, lock_time, lock_bots)
    if Game.IsSingleplayer then
        if TLE.CostumeLock ~= nil then
            TLE.CostumeLock.SetConfig(enabled, lock_time, lock_bots)
            CL.RequestState()
        end
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_CFGSET)
        msg.WriteBoolean(enabled)
        msg.WriteUInt16(math.floor(tonumber(lock_time) or 120))
        msg.WriteBoolean(lock_bots == true)
        Networking.Send(msg)
    end)
end

-- 强制解锁：char_id = 0 表示全部
function CL.SendUnlock(char_id)
    if Game.IsSingleplayer then
        if TLE.CostumeLock ~= nil then
            TLE.CostumeLock.Unlock(char_id)
            CL.RequestState()
        end
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_UNLOCK)
        msg.WriteUInt16(char_id)
        Networking.Send(msg)
    end)
    -- 服务端处理后会广播新状态；这里主动再请求一次尽快刷新列表
    CL.RequestState()
end
