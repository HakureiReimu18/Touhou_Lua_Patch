-- 爱丽丝人偶指挥（客户端：热键注册 + 指令发送）
-- 触发条件：手持魔法之国的魔导书；指令目标点 = 当前准星世界坐标。
-- 单机直接调 TLE.DollCommand.Execute；联机发网络消息给服务端。

if not CLIENT then return end

TLE = TLE or {}

local BOOK_ID = "Touhou_Alice_Magic_Book"
local MOD_PAGE = "人偶指挥"
local NETMSG = "Touhou.AliceDollCommand"

local function log(msg)
    print("[人偶指挥] " .. msg)
end

local function holding_book(character)
    if character == nil then return false end
    for item in character.HeldItems do
        if tostring(item.Prefab.Identifier) == BOOK_ID then return true end
    end
    return false
end

local function send_command(cmd)
    local character = Character.Controlled
    if character == nil then return end
    if not holding_book(character) then
        log("需要手持魔导书才能指挥人偶")
        return
    end

    local cursor = nil
    local ok, err = pcall(function() return character.CursorPosition end)
    if ok and err ~= nil then
        cursor = err
    else
        log("读不到准星坐标（CursorPosition 不可用）：" .. tostring(err))
        return
    end

    if Game.IsSingleplayer then
        local api = AliceDollCommand
        if api == nil and TLE.DollCommand ~= nil then api = TLE.DollCommand end
        if api ~= nil and api.Execute ~= nil then
            api.Execute(character, cmd, cursor.X, cursor.Y)
        else
            log("服务端模块未加载（AliceDollCommand 为空）")
        end
        return
    end

    if Networking == nil or Networking.Start == nil or Networking.Send == nil then
        log("联机网络接口不可用，指令未发送")
        return
    end
    local msg = Networking.Start(NETMSG)
    if msg == nil then
        log("Networking.Start 返回空消息，指令未发送")
        return
    end
    msg.WriteString(cmd)
    msg.WriteSingle(cursor.X)
    msg.WriteSingle(cursor.Y)
    Networking.Send(msg)
end

-- 三条指令的热键绑定；注册走东方快捷键框架（ TouhouHotkey.RegisterBinding ），
-- 框架还没加载就塞进 TouhouHotkeyPending 兜底（框架加载时会统一排空）
local BINDINGS = {
    { id = "touhou.doll.move",   name = "人偶·前进到准星", mod = MOD_PAGE, default_key = "G",
      on_trigger = function() send_command("move") end },
    { id = "touhou.doll.hold",   name = "人偶·驻守待命",   mod = MOD_PAGE, default_key = "H",
      on_trigger = function() send_command("hold") end },
    { id = "touhou.doll.follow", name = "人偶·跟随",       mod = MOD_PAGE, default_key = "B",
      on_trigger = function() send_command("follow") end },
}

local function register_all()
    if TouhouHotkey ~= nil and TouhouHotkey.RegisterBinding ~= nil then
        for _, def in ipairs(BINDINGS) do
            TouhouHotkey.RegisterBinding(def)
        end
        return
    end
    TouhouHotkeyPending = TouhouHotkeyPending or {}
    for _, def in ipairs(BINDINGS) do
        TouhouHotkeyPending[#TouhouHotkeyPending + 1] = def
    end
    log("快捷键框架未就绪，绑定已暂存（TouhouHotkeyPending）")
end

register_all()
