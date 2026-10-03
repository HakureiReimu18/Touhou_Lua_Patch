-- 爱丽丝人偶指挥（服务端权威）
-- 职责：
--   1. 认领：魔导书召唤按钮触发 LuaHook 立标记，characterCreated 捕获人偶并记录主人
--   2. 指令：move / hold / follow，写入桥接文件由 C# 侧轮询执行
--      （LuaCs 对 AIObjectiveGoTo 重载构造器绑定有缺陷且注册不到本模组程序集类型，
--        目标创建只能在 C# 侧做，见 CSharp/Shared/AliceDollCommand.cs）
--   3. 联机收网络消息；单机等价于客户端直接调 AliceDollCommand.Execute

TLE = TLE or {}

local NETMSG = "Touhou.AliceDollCommand"
local BOOK_ID = "Touhou_Alice_Magic_Book"
local BRIDGE_FILE = "AliceDollCommandBridge.txt"
local CLAIM_WINDOW = 2.0       -- 召唤按钮按下后多少秒内出生的人偶算本次召唤
local CLAIM_RADIUS = 3000      -- 认领时主人必须在人偶出生点多远以内（防串线）

-- owner(Character) -> { [doll] = { mode="move"|"hold"|"follow"|nil } }
local dollsByOwner = setmetatable({}, { __mode = "k" })
-- owner -> 最近一次按召唤按钮的游戏时间
local pendingSummon = setmetatable({}, { __mode = "k" })

local function log(msg)
    print("[人偶指挥] " .. msg)
end

-- ---------- 桥接文件写入（C# 侧每帧轮询） ----------

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
local SaveUtil = register_static("Barotrauma.SaveUtil")

local function get_bridge_path()
    if SaveUtil ~= nil and Path ~= nil then
        local ok, p = pcall(function()
            return Path.Combine(SaveUtil.DefaultSaveFolder, BRIDGE_FILE)
        end)
        if ok and p ~= nil then return p end
    end
    if Path ~= nil then
        local ok, p = pcall(function() return Path.Combine("Data", "Saves", BRIDGE_FILE) end)
        if ok and p ~= nil then return p end
    end
    return "Data/Saves/" .. BRIDGE_FILE
end

local function bridge_append(line)
    if File == nil then
        log("Barotrauma.IO.File 不可用，无法写桥接文件")
        return false
    end
    local path = get_bridge_path()
    -- AppendAllText 不一定在白名单里，先试，失败就退回读-改-写
    local ok = pcall(function() File.AppendAllText(path, line .. "\n") end)
    if ok then return true end
    local content = ""
    pcall(function() if File.Exists(path) then content = File.ReadAllText(path) end end)
    ok = pcall(function() File.WriteAllText(path, content .. line .. "\n") end)
    return ok
end

-- ---------- 认领 ----------

Hook.Add("Touhou_Alice_Magic_Book.Summon", "Touhou.AliceDollCommand.Summon", function(effect, deltaTime, item, targets, worldPosition)
    local owner = targets ~= nil and targets[1] or nil
    if owner == nil or owner.Removed then
        log(string.format("Summon 钩子触发但 targets 无效（targets=%s, [1]=%s）",
            tostring(targets), tostring(owner)))
        return
    end
    pendingSummon[owner] = Timer.GetTime()
    log(string.format("Summon 标记：owner=%s", tostring(owner.Name)))
end)

Hook.Add("characterCreated", "Touhou.AliceDollCommand.Claim", function(character)
    if character == nil then return end
    local species = tostring(character.SpeciesName)
    if not string.match(species, "^Touhou_Alice_Doll") then return end

    local now = Timer.GetTime()
    local best, bestDist = nil, math.huge
    local dbg = {}
    for o, t in pairs(pendingSummon) do
        if o ~= nil and not o.Removed then
            local age = now - t
            local dx = o.WorldPosition.X - character.WorldPosition.X
            local dy = o.WorldPosition.Y - character.WorldPosition.Y
            local dist = math.sqrt(dx * dx + dy * dy)
            dbg[#dbg + 1] = string.format("%s(age=%.2f,dist=%.0f)", tostring(o.Name), age, dist)
            if age <= CLAIM_WINDOW and dist < bestDist then
                best, bestDist = o, dist
            end
        end
    end

    if best ~= nil and bestDist <= CLAIM_RADIUS then
        dollsByOwner[best] = dollsByOwner[best] or {}
        dollsByOwner[best][character] = { mode = "follow" }
        pendingSummon[best] = nil
        log(string.format("认领：%s 的人偶 %s", tostring(best.Name), species))
    else
        local cand = #dbg > 0 and table.concat(dbg, " ") or "（pendingSummon 为空——Summon 钩子没触发或 targets 为空）"
        log(string.format("人偶 %s 无人认领。候选：%s", species, cand))
    end
end)

-- ---------- 指令执行 ----------

local function for_each_doll(owner, fn)
    local dolls = dollsByOwner[owner]
    if dolls == nil then return 0 end
    local n = 0
    for doll, entry in pairs(dolls) do
        if doll == nil or doll.Removed or doll.IsDead then
            dolls[doll] = nil
        else
            fn(doll, entry)
            n = n + 1
        end
    end
    return n
end

local function holds_book(character)
    if character == nil then return false end
    for item in character.HeldItems do
        if tostring(item.Prefab.Identifier) == BOOK_ID then return true end
    end
    return false
end

-- cmd: "move" | "hold" | "follow"；x/y 仅 move 用到
local function execute(owner, cmd, x, y)
    if owner == nil or owner.Removed then
        log("execute：主人无效")
        return
    end
    if not holds_book(owner) then
        log("execute：" .. tostring(owner.Name) .. " 未手持魔导书，忽略")
        return
    end
    local written, total = 0, 0
    for_each_doll(owner, function(doll, entry)
        total = total + 1
        entry.mode = cmd
        local ok, err = pcall(function()
            -- 第 5 段带主人 ID：C# 侧 follow 模式要靠它定位主人
            bridge_append(string.format("%s|%d|%f|%f|%d", cmd, doll.ID, x or 0, y or 0, owner.ID))
        end)
        if ok then
            written = written + 1
        else
            log("桥接写入失败：" .. tostring(err))
        end
    end)
    if total > 0 then
        log(string.format("%s 指令已写入桥接（%d/%d 只人偶）", cmd, written, total))
    else
        log("execute：" .. tostring(owner.Name) .. " 名下没有存活人偶")
    end
end

-- ---------- 联机 ----------

if SERVER and Networking ~= nil and Networking.Receive ~= nil then
    Networking.Receive(NETMSG, function(message, client)
        if client == nil or client.Character == nil then return end
        local ok, err = pcall(function()
            local cmd = message.ReadString()
            local x = message.ReadSingle()
            local y = message.ReadSingle()
            execute(client.Character, cmd, x, y)
        end)
        if not ok then
            log("网络指令处理失败：" .. tostring(err))
        end
    end)
else
    Hook.Add("netMessageReceived", "Touhou.AliceDollCommand.NetFallback", function(message, client, id)
        if id ~= NETMSG or client == nil or client.Character == nil then return end
        local ok, err = pcall(function()
            local cmd = message.ReadString()
            local x = message.ReadSingle()
            local y = message.ReadSingle()
            execute(client.Character, cmd, x, y)
        end)
        if not ok then
            log("网络指令处理失败：" .. tostring(err))
        end
    end)
end

-- ---------- 暴露给客户端（单机直调） ----------
-- 同时挂 TLE.DollCommand 和独立全局 AliceDollCommand：
-- 两个模组都启用时后者谁的 init 后跑都不会被冲掉

AliceDollCommand = {
    NETMSG = NETMSG,
    Execute = execute,
    -- 调试：控制台可查
    DebugState = function()
        for owner, dolls in pairs(dollsByOwner) do
            local names = {}
            for doll, entry in pairs(dolls) do
                names[#names + 1] = string.format("%s(mode=%s)", tostring(doll.SpeciesName), tostring(entry.mode))
            end
            log(string.format("owner=%s：%s", tostring(owner.Name), table.concat(names, ", ")))
        end
    end,
}
TLE.DollCommand = AliceDollCommand
