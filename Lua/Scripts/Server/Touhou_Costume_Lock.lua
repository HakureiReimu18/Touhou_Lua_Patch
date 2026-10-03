--[[东方-装束锁定（服务端权威）
    穿"装束+"（id 带 _Plus）累计够时长就锁上（NonPlayerTeamInteractable=true，玩家自己脱不下来），
    巡回结束/死亡/管理员强制解锁。配置存 Data/TouhouCostumeLockConfig.txt，
    联机走 TLE_CL_STATE 广播，单机由 GUI 直接调 TLE.CostumeLock。
    默认只锁玩家；开了 lock_bots 后 AI 船员一样锁。]]

-- 毁灭吧
if CLIENT and not SERVER and not Game.IsSingleplayer then
    return
end

-- 注册并取静态类，跟 Touhou_Mod_Hotkey.lua 一个写法
local function register_static(type_name)
    local ok, result = pcall(function()
        local t = LuaUserData.CreateStatic(type_name)
        if t == nil then
            pcall(function() LuaUserData.RegisterType(type_name) end)
            t = LuaUserData.CreateStatic(type_name)
        end
        return t
    end)
    if ok then return result end
    return nil
end

local File = register_static("Barotrauma.IO.File")
local Path = register_static("Barotrauma.IO.Path")

local CONFIG_FILE_NAME = "TouhouCostumeLockConfig.txt"
local LOG_PREFIX = "[东方装束锁定] "
local MSG_STATE  = "TLE_CL_STATE"    -- S→全体：配置 + 锁定列表
local MSG_STATEP = "TLE_CL_STATEP"   -- S→单个：同上 + 请求者 can_edit
local MSG_CFGGET = "TLE_CL_CFGGET"   -- C→S：请求状态
local MSG_CFGSET = "TLE_CL_CFGSET"   -- C→S：修改配置（需权限）
local MSG_UNLOCK = "TLE_CL_UNLOCK"   -- C→S：强制解锁（需权限；0 = 全部）
local LOCK_TIME_MIN = 10
local LOCK_TIME_MAX = 600
local TICK_FRAMES = 30               -- 每 30 帧算一次账
local TICK_SECONDS = 0.5

local CFG = {
    enabled = true,
    lock_time = 120,     -- 秒
    lock_bots = false,   -- 是否把 AI 船员也纳入锁定
    extra_ids = {},      -- 额外纳入锁定的物品 identifier
    exclude_ids = {},    -- 排除在外的物品 identifier
}

local function get_config_path()
    if Path ~= nil then
        local ok, p = pcall(function() return Path.Combine("Data", CONFIG_FILE_NAME) end)
        if ok and p ~= nil then return p end
    end
    return "Data/" .. CONFIG_FILE_NAME
end

local function split_ids(text)
    local list = {}
    for id in string.gmatch(text or "", "[^,]+") do
        local trimmed = string.match(id, "^%s*(.-)%s*$")
        if trimmed ~= "" then table.insert(list, trimmed) end
    end
    return list
end

local function save_config()
    if File == nil then
        print(LOG_PREFIX .. "无法保存配置：Barotrauma.IO.File 不可用（LuaCs 未开放文件访问）")
        return
    end
    local lines = {
        "enabled=" .. (CFG.enabled and "1" or "0"),
        "locktime=" .. tostring(CFG.lock_time),
        "lockbots=" .. (CFG.lock_bots and "1" or "0"),
        "extra_ids=" .. table.concat(CFG.extra_ids, ","),
        "exclude_ids=" .. table.concat(CFG.exclude_ids, ","),
    }
    local path = get_config_path()
    local ok, err = pcall(function() File.WriteAllText(path, table.concat(lines, "\n")) end)
    if not ok then
        print(LOG_PREFIX .. "配置保存失败：" .. tostring(err) .. "（路径：" .. tostring(path) .. "）")
    end
end

local function load_config()
    if File == nil then return end
    local path = get_config_path()
    local ok, text = pcall(function()
        if not File.Exists(path) then return nil end
        return File.ReadAllText(path)
    end)
    if not ok or text == nil then return end
    local kv = {}
    for line in string.gmatch(text, "[^\r\n]+") do
        local k, v = string.match(line, "^([%w_%.]+)=(.*)$")
        if k ~= nil then kv[k] = v end
    end
    if kv["enabled"] ~= nil then CFG.enabled = kv["enabled"] == "1" end
    local lt = tonumber(kv["locktime"])
    if lt ~= nil then CFG.lock_time = math.floor(math.min(math.max(lt, LOCK_TIME_MIN), LOCK_TIME_MAX)) end
    if kv["lockbots"] ~= nil then CFG.lock_bots = kv["lockbots"] == "1" end
    if kv["extra_ids"] ~= nil then CFG.extra_ids = split_ids(kv["extra_ids"]) end
    if kv["exclude_ids"] ~= nil then CFG.exclude_ids = split_ids(kv["exclude_ids"]) end
end

local wear_time = {}     -- character -> 本巡回累计穿着秒数（脱下保留，巡回开始清零）
local locked = {}        -- character -> { item=被锁物品, orig=原 NonPlayerTeamInteractable 值 }
local round_active = false
local tick_counter = 0

-- 追踪对象：玩家控制的角色始终算；开了 lock_bots 后 AI 船员也算（怪物不锁）
local function is_player_character(char)
    if char == nil or char.Removed then return false end
    local ok, res = pcall(function()
        return char.IsHuman and (not char.IsBot or CFG.lock_bots)
    end)
    return ok and res == true
end

-- 物品现在挂在哪个玩家角色身上。实体被替换（中途加入/重连）时靠这个把锁跟到新角色
local function find_item_owner_character(item)
    if item == nil or item.Removed then return nil end
    local ok, owner = pcall(function()
        local inv = item.ParentInventory
        if inv == nil then return nil end
        return inv.Owner
    end)
    if ok and is_player_character(owner) then
        return owner
    end
    return nil
end

-- 装束+ 的 id 都带 _Plus；exclude_ids 优先踢掉，extra_ids 兜底纳入
-- （槽位是不是 InnerClothes 由调用方保证）
local function is_lockable_outfit(item)
    if item == nil or item.Removed then return false end
    local ok, id = pcall(function() return tostring(item.Prefab.Identifier) end)
    if not ok or id == nil then return false end
    for _, ex in ipairs(CFG.exclude_ids) do
        if id == ex then return false end
    end
    for _, ex in ipairs(CFG.extra_ids) do
        if id == ex then return true end
    end
    return string.find(id, "_Plus", 1, true) ~= nil
end

-- 暂停（单机 ESC）不计时；编辑器测试模式按正式巡回一样计时（方便排查问题）
local function is_paused()
    local ok, res = pcall(function() return Game.Paused == true end)
    return ok and res == true
end

local function get_worn_outfit(char)
    if char.Inventory == nil then return nil end
    local ok, item = pcall(function()
        return char.Inventory.GetItemInLimbSlot(InvSlotType["InnerClothes"])
    end)
    if ok then return item end
    return nil
end

-- 定向私聊，发不出去就 print（照抄 Pseudologia.lua 的写法）
local function notify_character(message, char)
    local ok = pcall(function()
        if Game ~= nil and Game.SendDirectChatMessage ~= nil
                and Util ~= nil and Util.FindClientCharacter ~= nil
                and ChatMessageType ~= nil and char ~= nil then
            local target_client = Util.FindClientCharacter(char)
            if target_client ~= nil then
                Game.SendDirectChatMessage("装束锁定", message, nil, ChatMessageType.Default, target_client)
                return
            end
        end
        error("direct chat unavailable")
    end)
    if not ok then print(LOG_PREFIX .. message) end
end

local function notify_client(message, client)
    local ok = pcall(function()
        Game.SendDirectChatMessage("装束锁定", message, nil, ChatMessageType.Default, client)
    end)
    if not ok then print(LOG_PREFIX .. message) end
end

-- 有 ConsoleCommands 权限就能改配置/解锁，不要求服务器真开了作弊
local function has_permission(client)
    local ok, res = pcall(function()
        if client == nil then return true end
        return client.HasPermission(ClientPermissions.ConsoleCommands)
    end)
    return ok and res == true
end

local function write_state(msg)
    msg.WriteBoolean(CFG.enabled)
    msg.WriteUInt16(CFG.lock_time)
    msg.WriteBoolean(CFG.lock_bots)
    local n = 0
    for char, entry in pairs(locked) do
        if not char.Removed and not entry.item.Removed then n = n + 1 end
    end
    msg.WriteUInt16(n)
    for char, entry in pairs(locked) do
        if not char.Removed and not entry.item.Removed then
            msg.WriteUInt16(char.ID)
            msg.WriteString(tostring(char.Name))
            msg.WriteString(tostring(entry.item.Name))
            msg.WriteUInt16(entry.item.ID)
            msg.WriteUInt16(math.floor(wear_time[char] or 0))
        end
    end
end

local function broadcast_state()
    if Game.IsSingleplayer then return end
    pcall(function()
        local msg = Networking.Start(MSG_STATE)
        write_state(msg)
        Networking.Send(msg)
    end)
end

local function send_state_to(client)
    if client == nil or Game.IsSingleplayer then return end
    local ok, err = pcall(function()
        local msg = Networking.Start(MSG_STATEP)
        write_state(msg)
        msg.WriteBoolean(has_permission(client))
        Networking.Send(msg, client.Connection, DeliveryMethod.Reliable)
    end)
    if not ok then
        print(LOG_PREFIX .. "定向发送状态失败：" .. tostring(err))
    end
end

local function lock_outfit(char, item)
    locked[char] = { item = item, orig = item.NonPlayerTeamInteractable }
    item.NonPlayerTeamInteractable = true
    notify_character("你的装束已锁定，巡回结束或死亡后解锁：" .. tostring(item.Name), char)
    broadcast_state()
end

local function unlock_character(char, reason_notify)
    local entry = locked[char]
    if entry == nil then return false end
    locked[char] = nil
    wear_time[char] = nil  -- 计时清零给段宽限期，不然刚解锁下一秒又锁上
    if not entry.item.Removed then
        pcall(function() entry.item.NonPlayerTeamInteractable = entry.orig end)
    end
    if reason_notify and not char.Removed then
        notify_character("你的装束已解锁：" .. tostring(entry.item.Name), char)
    end
    return true
end

local function unlock_all(notify)
    for char, _ in pairs(locked) do
        unlock_character(char, notify)
    end
end

-- 兜底：把场上还挂着锁的合规装束复位。防存档把锁序列化进去、
-- 读档后 Lua 状态丢了导致永久锁死
local function sweep_stray_locks()
    for item in Item.ItemList do
        if not item.Removed and item.NonPlayerTeamInteractable and is_lockable_outfit(item) then
            pcall(function() item.NonPlayerTeamInteractable = false end)
        end
    end
end

local function reset_round()
    unlock_all(false)
    wear_time = {}
    sweep_stray_locks()
    broadcast_state()
end

local function apply_config(enabled, lock_time, lock_bots)
    CFG.enabled = enabled == true
    CFG.lock_time = math.floor(math.min(math.max(tonumber(lock_time) or CFG.lock_time, LOCK_TIME_MIN), LOCK_TIME_MAX))
    CFG.lock_bots = lock_bots == true
    if not CFG.enabled then
        unlock_all(true)   -- 关闭功能时解除所有现存锁定，避免残留
    elseif not CFG.lock_bots then
        -- 关掉 AI 锁定时顺手把现有 AI 船员的锁解了（遍历时删当前键是安全的）
        for char, _ in pairs(locked) do
            if not char.Removed and char.IsBot then
                unlock_character(char, true)
            end
        end
    end
    save_config()
    broadcast_state()
end

local function unlock_by_char_id(char_id, notify)
    for char, _ in pairs(locked) do
        if not char.Removed and char.ID == char_id then
            local r = unlock_character(char, notify)
            broadcast_state()
            return r
        end
    end
    return false
end

if SERVER and not Game.IsSingleplayer then
    Networking.Receive(MSG_CFGGET, function(message, client)
        send_state_to(client)
    end)

    Networking.Receive(MSG_CFGSET, function(message, client)
        local enabled = message.ReadBoolean()
        local lt = message.ReadUInt16()
        local lock_bots = message.ReadBoolean()
        if not has_permission(client) then
            notify_client("修改装束锁定设置需要控制台指令权限（ConsoleCommands 权限）", client)
            return
        end
        apply_config(enabled, lt, lock_bots)
        send_state_to(client)
    end)

    Networking.Receive(MSG_UNLOCK, function(message, client)
        local target = message.ReadUInt16()
        if not has_permission(client) then
            notify_client("强制解锁需要控制台指令权限（ConsoleCommands 权限）", client)
            return
        end
        if target == 0 then
            unlock_all(true)
            broadcast_state()
        else
            unlock_by_char_id(target, true)
        end
    end)
end

-- 单机直调，不走网络。和 C# BondNet 一个思路：没 NetworkMember 就直接本地用，不要作弊权限
TLE.CostumeLock = {
    GetState = function()
        local list = {}
        for char, entry in pairs(locked) do
            if not char.Removed and not entry.item.Removed then
                table.insert(list, {
                    char_id = char.ID,
                    char_name = tostring(char.Name),
                    item_name = tostring(entry.item.Name),
                    item_id = entry.item.ID,
                    wear = math.floor(wear_time[char] or 0),
                })
            end
        end
        return { enabled = CFG.enabled, lock_time = CFG.lock_time, lock_bots = CFG.lock_bots, can_edit = true, locked = list }
    end,
    SetConfig = function(enabled, lock_time, lock_bots)
        apply_config(enabled, lock_time, lock_bots)
        return true
    end,
    Unlock = function(char_id)
        if char_id == 0 then
            unlock_all(true)
            return true
        end
        return unlock_by_char_id(char_id, true)
    end,
    IsLockableOutfit = is_lockable_outfit,
}

load_config()

-- 热重载时对齐一下当前巡回状态，正常启动是 false 等 roundStart 置真
pcall(function()
    round_active = Game.GameSession ~= nil and Game.GameSession.IsRunning
end)

Hook.Add("roundStart", "TLE_CostumeLock_round", function()
    round_active = true
    reset_round()
end)

Hook.Add("roundEnd", "TLE_CostumeLock_round", function()
    round_active = false
    reset_round()
end)

-- 穿戴者死亡 → 立即解锁（尸体上的装束可被队友回收），并清掉其计时
Hook.Add("character.death", "TLE_CostumeLock_death", function(char)
    if char == nil then return end
    if unlock_character(char, false) then
        broadcast_state()
    end
    wear_time[char] = nil
end)

Hook.Add("think", "TLE_CostumeLock_tick", function()
    tick_counter = tick_counter + 1
    if tick_counter < TICK_FRAMES then return end
    tick_counter = 0

    -- 先收集再删，遍历时增删键是未定义行为
    local removed_locked_chars = {}
    for char, _ in pairs(locked) do
        if char.Removed then table.insert(removed_locked_chars, char) end
    end
    for _, char in ipairs(removed_locked_chars) do
        local entry = locked[char]
        if entry ~= nil then
            -- 实体被替换（中途加入/重连就会这样）时物品通常已经跟到新角色身上，
            -- 把锁和计时一起挪过去，别直接还原——不然会提示锁了但实际没锁
            local new_owner = find_item_owner_character(entry.item)
            if new_owner ~= nil then
                locked[char] = nil
                locked[new_owner] = entry
                wear_time[new_owner] = math.max(wear_time[new_owner] or 0, CFG.lock_time)
                broadcast_state()
            else
                if not entry.item.Removed then
                    pcall(function() entry.item.NonPlayerTeamInteractable = entry.orig end)
                end
                locked[char] = nil
            end
        end
    end
    for char, _ in pairs(wear_time) do
        if char.Removed then wear_time[char] = nil end
    end

    -- 周期复查锁状态，防别的系统把属性改了；物品没了就除名
    for char, entry in pairs(locked) do
        if entry.item.Removed then
            locked[char] = nil
        elseif not entry.item.NonPlayerTeamInteractable then
            pcall(function() entry.item.NonPlayerTeamInteractable = true end)
        end
    end

    if not CFG.enabled or not round_active then return end
    if is_paused() then return end  -- 暂停不计时

    for char in Character.CharacterList do
        if is_player_character(char) and not char.IsDead then
            local item = get_worn_outfit(char)
            if item ~= nil and is_lockable_outfit(item) then
                wear_time[char] = (wear_time[char] or 0) + TICK_SECONDS
                if wear_time[char] >= CFG.lock_time and locked[char] == nil then
                    lock_outfit(char, item)
                end
            end
        end
    end
end)
