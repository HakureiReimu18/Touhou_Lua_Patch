--[[东方-武器伤害与防具抗性设置（服务端/权威半区）
    权限门槛 + 全服聊天广播 + 联机状态同步（P4-lite，设计稿 §7）。

    - 联机（含主机）客户端设置页「保存 / 档位 / 重置」→ TLE_DMG_SET 携带完整玩家值文本，
      本脚本校验 ConsoleCommands 权限后规范化写入 TouhouDamageConfig.txt（权威端 C# 每秒热加载应用）；
    - 每约 0.5 秒对比权威端 TouhouDamageState.txt：数值变化 → 全服广播新状态（客户端只在内存显示）
      + 聊天栏公告「谁改了什么」；覆盖设置页 / damage_set / damage_lv / 手改文件等所有来源；
    - TLE_DMG_GET：任意客户端可请求当前状态（打开页面 / 定期拉取），回包附其编辑权限位。

    单机不走网络：设置页直接写文件（本脚本只负责聊天栏公告与广播的联机分支）。]]

if CLIENT and not SERVER and not Game.IsSingleplayer then
    return
end

-- 注册并取静态类，跟 Touhou_Costume_Lock.lua 一个写法
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

local STATE_FILE  = "TouhouDamageState.txt"
local CONFIG_FILE = "TouhouDamageConfig.txt"
local MSG_STATE  = "TLE_DMG_STATE"    -- S→全体：状态快照（已剔除 applied.*）
local MSG_STATEP = "TLE_DMG_STATEP"   -- S→单个：同上 + 请求者 can_edit
local MSG_GET    = "TLE_DMG_GET"      -- C→S：请求状态
local MSG_SET    = "TLE_DMG_SET"      -- C→S：提交玩家值全文（需权限）
local MSG_DENIED = "TLE_DMG_DENIED"   -- S→单个：拒绝原因
local LOG_PREFIX = "[东方伤害设置] "
local CHAT_SENDER = "伤害设置"
local TICK_FRAMES = 30                -- 约 0.5 秒检查一次
local MAX_CFG_BYTES = 65536
local MAX_CHAT_LEN = 260

-- ==================== 文件与解析 ====================

local function save_dir_file(name)
    if SaveUtil ~= nil and Path ~= nil then
        local ok, p = pcall(function() return Path.Combine(SaveUtil.DefaultSaveFolder, name) end)
        if ok and p ~= nil then return p end
    end
    if Path ~= nil then
        local ok, p = pcall(function() return Path.Combine("Data", "Saves", name) end)
        if ok and p ~= nil then return p end
    end
    return "Data/Saves/" .. name
end

local function read_text(path)
    if File == nil then return nil end
    local ok, text = pcall(function()
        if not File.Exists(path) then return nil end
        return File.ReadAllText(path)
    end)
    if ok then return text end
    return nil
end

local function write_text(path, text)
    if File == nil then return false, "文件接口不可用" end
    local ok, err = pcall(function() File.WriteAllText(path, text) end)
    if ok then return true end
    return false, tostring(err)
end

-- 剔除 applied.* 行：客户端只用于显示，绝不能拿主机记录去覆盖本地（防归一化污染）
local function strip_applied(text)
    local kept = {}
    for line in string.gmatch(text or "", "[^\r\n]+") do
        if string.sub(line, 1, 8) ~= "applied." then kept[#kept + 1] = line end
    end
    return table.concat(kept, "\n")
end

-- 取分组元数据与当前值（值统一存字符串，比较零误差）
local function parse_state(text)
    local st = { order = {}, values = {}, labels = {}, kinds = {} }
    for line in string.gmatch(text or "", "[^\r\n]+") do
        local k, v = string.match(line, "^([^=]+)=(.*)$")
        if k ~= nil then
            local gid, prop = string.match(k, "^group%.([^.]+)%.(.+)$")
            if gid ~= nil then
                if st.values[gid] == nil then
                    st.values[gid] = {}
                    st.order[#st.order + 1] = gid
                end
                if prop == "label" then st.labels[gid] = v
                elseif prop == "kind" then st.kinds[gid] = v
                elseif prop == "damage" or prop == "penmode" or prop == "penvalue" or prop == "defense" then
                    st.values[gid][prop] = v
                end
            end
        end
    end
    return st
end

local function fmt_num(v)
    local n = tonumber(v)
    if n == nil then return tostring(v) end
    return string.format("%.4g", n)
end

local function pen_text(mode, value)
    if mode == "multiply" then return "×" .. fmt_num(value) end
    return "+" .. fmt_num(value)
end

-- 规范化：只保留合法键（damage / pen / def + 已知分组），数值逐项校验范围；
-- 返回 新文本, nil 或 nil, 错误原因
local function sanitize_config(text, known_gids)
    if type(text) ~= "string" or text == "" then return nil, "内容为空" end
    if #text > MAX_CFG_BYTES then return nil, "内容过长" end

    local accepted = {}
    local count = 0
    local function known(gid)
        if known_gids == nil then return true end
        return known_gids[gid] == true
    end

    for line in string.gmatch(text, "[^\r\n]+") do
        local t = string.match(line, "^%s*(.-)%s*$")
        if t ~= "" and string.sub(t, 1, 1) ~= "#" then
            local axis, gid, raw = string.match(t, "^(%a+)%.([%w_%-]+)%s*=%s*(.-)$")
            if axis ~= nil then
                axis = string.lower(axis)
                local out = nil
                if axis == "damage" or axis == "def" then
                    local v = tonumber(raw)
                    if v == nil then return nil, gid .. " 的数值无效：" .. raw end
                    if v < 0 or v > 10 then return nil, gid .. " 的数值超出范围（0~10）：" .. raw end
                    out = axis .. "." .. gid .. "=" .. fmt_num(v)
                elseif axis == "pen" then
                    local m, mv = string.match(raw, "^(%a+):(.*)$")
                    local v = tonumber(mv)
                    if m == nil or v == nil then return nil, gid .. " 的穿甲格式无效：" .. raw end
                    m = string.lower(m)
                    if m == "mul" then m = "multiply" end
                    if m ~= "add" and m ~= "multiply" then return nil, gid .. " 的穿甲模式无效：" .. raw end
                    if m == "add" and (v < -1 or v > 1) then return nil, gid .. " 的穿甲加值超出范围（-1~1）：" .. raw end
                    if m == "multiply" and (v < 0 or v > 10) then return nil, gid .. " 的穿甲乘数超出范围（0~10）：" .. raw end
                    out = "pen." .. gid .. "=" .. m .. ":" .. fmt_num(v)
                end
                if out ~= nil then
                    if not known(gid) then return nil, "未知分组：" .. gid end
                    count = count + 1
                    if count > 512 then return nil, "条目过多" end
                    accepted[#accepted + 1] = out
                end
            end
        end
    end
    if count == 0 then return nil, "没有可用的数值条目" end

    local lines = {
        "# 东方-武器伤害与防具抗性设置 · 玩家值（联机请求写入；单机由设置页/命令写入，C# 每秒热加载）",
        "# 键：damage.<组>=倍率 · pen.<组>=add:<加值> 或 multiply:<乘数> · def.<组>=防御倍率",
        "ver=1",
    }
    for _, l in ipairs(accepted) do lines[#lines + 1] = l end
    return table.concat(lines, "\n") .. "\n"
end

-- ==================== 权限 / 网络 ====================

-- 服主恒有全部权限（GameServer.OnOwnerDetermined 里给 All）；其余按 ConsoleCommands
local function has_permission(client)
    if client == nil then return true end
    local ok, res = pcall(function()
        if Game.Server ~= nil and Game.Server.OwnerConnection ~= nil
                and client.Connection ~= nil and client.Connection == Game.Server.OwnerConnection then
            return true
        end
        return client.HasPermission(ClientPermissions.ConsoleCommands)
    end)
    return ok and res == true
end

local function client_name(client)
    if client == nil then return "" end
    local ok, name = pcall(function()
        if client.Character ~= nil then return tostring(client.Character.Name) end
        return tostring(client.Name)
    end)
    if ok and name ~= nil and name ~= "" then return name end
    return "玩家"
end

local function send_denied(client, reason)
    if client == nil then
        print(LOG_PREFIX .. "拒绝（无发送者）：" .. reason)
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_DENIED)
        msg.WriteString(reason)
        Networking.Send(msg, client.Connection, DeliveryMethod.Reliable)
    end)
    pcall(function()
        Game.SendDirectChatMessage(CHAT_SENDER, reason, nil, ChatMessageType.Default, client)
    end)
    print(LOG_PREFIX .. "拒绝 " .. client_name(client) .. "：" .. reason)
end

local function broadcast_state(stripped)
    if Game.IsSingleplayer then return end
    pcall(function()
        local msg = Networking.Start(MSG_STATE)
        msg.WriteString(stripped)
        Networking.Send(msg)
    end)
end

local function send_state_to(client, can_edit)
    if client == nil then return end
    local stripped = strip_applied(read_text(save_dir_file(STATE_FILE)) or "")
    local ok, err = pcall(function()
        local msg = Networking.Start(MSG_STATEP)
        msg.WriteString(stripped)
        msg.WriteBoolean(can_edit == true)
        Networking.Send(msg, client.Connection, DeliveryMethod.Reliable)
    end)
    if not ok then print(LOG_PREFIX .. "定向发送状态失败：" .. tostring(err)) end
end

-- ==================== 聊天公告 ====================

local function announce(text)
    if #text > MAX_CHAT_LEN then text = string.sub(text, 1, MAX_CHAT_LEN) .. "…" end
    local ok = pcall(function()
        if Game.IsSingleplayer then
            if Game.GameSession ~= nil and Game.GameSession.CrewManager ~= nil then
                Game.GameSession.CrewManager.AddSinglePlayerChatMessage(CHAT_SENDER, text, ChatMessageType.Default, nil)
            end
        elseif Game.Server ~= nil then
            local chatMessage = ChatMessage.Create(CHAT_SENDER, text, ChatMessageType.Default, nil)
            for _, c in pairs(Client.ClientList) do
                Game.Server.SendDirectChatMessage(chatMessage, c)
            end
        end
    end)
    -- 独立显式 print：无论聊天通不通，控制台都留痕
    print(LOG_PREFIX .. text)
    if not ok then
        -- 聊天不可用时静默（print 已兜底）
    end
end

-- 主机侧改动（设置页 / 控制台命令 / 改文件）没带请求者名字时的署名
local function default_actor()
    local ok, name = pcall(function()
        if Character ~= nil and Character.Controlled ~= nil then
            return tostring(Character.Controlled.Name)
        end
        if Game.Server ~= nil then return tostring(Game.Server.ServerName or "") end
        return ""
    end)
    if ok and name ~= nil and name ~= "" then return name end
    return "服务器"
end

-- ==================== 状态监视（数值变化 → 广播 + 公告） ====================

local last_state_text = nil
local last_values = nil
local last_labels = nil
local last_kinds = nil
local last_order = nil
local pending_actor = nil       -- 本次数值变化的提交者署名
local pending_requester = nil   -- 提交者客户端：应用完成后定向回一份状态（不等广播，主机自己也稳）

local function build_diff(oldv, newv, labels, kinds, order)
    local out = {}
    for _, gid in ipairs(order) do
        local ov, nv = oldv[gid], newv[gid]
        if ov ~= nil and nv ~= nil then
            local parts = {}
            if kinds[gid] == "armor" then
                if ov.defense ~= nv.defense then
                    parts[#parts + 1] = string.format("防御 %s→%s", fmt_num(ov.defense), fmt_num(nv.defense))
                end
            else
                if ov.damage ~= nv.damage then
                    parts[#parts + 1] = string.format("伤害 ×%s→×%s", fmt_num(ov.damage), fmt_num(nv.damage))
                end
                if ov.penmode ~= nv.penmode or ov.penvalue ~= nv.penvalue then
                    parts[#parts + 1] = string.format("穿甲 %s→%s",
                        pen_text(ov.penmode, ov.penvalue), pen_text(nv.penmode, nv.penvalue))
                end
            end
            if #parts > 0 then
                out[#out + 1] = string.format("%s %s", labels[gid] or gid, table.concat(parts, "、"))
            end
        end
    end
    return out
end

local function tick()
    local text = read_text(save_dir_file(STATE_FILE))
    if text == nil or text == last_state_text then return end
    last_state_text = text

    local st = parse_state(text)
    if last_values ~= nil then
        local diffs = build_diff(last_values, st.values, st.labels, st.kinds, last_order or st.order)
        broadcast_state(strip_applied(text))
        if pending_requester ~= nil then
            -- 提交方定向回一份：应用完成才算数（主机自己不依赖广播也能刷新页面）
            send_state_to(pending_requester, has_permission(pending_requester))
        end
        if #diffs > 0 then
            local actor = pending_actor
            if actor == nil or actor == "" then actor = default_actor() end
            announce(actor .. " 修改了伤害/防御设置：" .. table.concat(diffs, "；"))
        end
    end
    last_values, last_labels, last_kinds, last_order = st.values, st.labels, st.kinds, st.order
    pending_actor = nil
    pending_requester = nil
end

-- ==================== 网络入口 ====================

if SERVER and not Game.IsSingleplayer then
    Networking.Receive(MSG_GET, function(message, client)
        send_state_to(client, has_permission(client))
    end)

    Networking.Receive(MSG_SET, function(message, client)
        if not has_permission(client) then
            send_denied(client, "修改武器伤害/防具抗性设置需要管理员权限（ConsoleCommands）")
            return
        end
        local ok, text = pcall(function() return message.ReadString() end)
        if not ok or type(text) ~= "string" then
            send_denied(client, "设置内容读取失败")
            return
        end
        local cur = parse_state(read_text(save_dir_file(STATE_FILE)) or "")
        local known = nil
        if #cur.order > 0 then
            known = {}
            for _, gid in ipairs(cur.order) do known[gid] = true end
        end
        local sanitized, err = sanitize_config(text, known)
        if sanitized == nil then
            send_denied(client, "设置内容无效：" .. tostring(err))
            return
        end
        local wok, werr = write_text(save_dir_file(CONFIG_FILE), sanitized)
        if not wok then
            send_denied(client, "写入玩家配置失败：" .. tostring(werr))
            return
        end
        pending_actor = client_name(client)
        pending_requester = client
        print(LOG_PREFIX .. "已接受 " .. pending_actor .. " 的设置提交，等待 C# 应用")
        -- 不立即回包：等状态文件真的变了（C# 应用完成）再由 tick 定向回一份，避免提示提前清掉
    end)
end

-- 状态文件检查按帧数节流（约 0.5 秒一次）：权威端 C# 本身每秒轮询应用，这个节奏够用且不浪费
local tick_counter = 0
Hook.Add("think", "TLE_DamageSettings_tick", function()
    if not (SERVER or Game.IsSingleplayer) then return end
    tick_counter = tick_counter + 1
    if tick_counter < TICK_FRAMES then return end
    tick_counter = 0
    tick()
end)

print(LOG_PREFIX .. "服务端模块已加载（权限门槛 + 全服广播" .. (Game.IsSingleplayer and "，单机）" or "）"))
