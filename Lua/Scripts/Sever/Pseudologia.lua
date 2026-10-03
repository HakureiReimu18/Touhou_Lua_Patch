-- 你怎么说话的，话

--[[
AI写注释比我厉害
那我有什么用

天赋1 死亡回溯：每巡回一次，血线 <= 1% 触发，清掉全身 affliction（设计里还带增益+20精神病）
天赋3 伪证专家：Alt+X 主动触发，冷却 5 分钟
]]

-- 要和人才树里定义的 identifier 一字不差
local TALENT_DEATH_REWIND = "HiroTalent"
local TALENT_FALSE_EVIDENCE = "HiroTalent"

-- 要和 Afflictions.xml 里的一致
local FALSE_EVIDENCE_BUFF = "Hiro_Pseudo_Buff"
local REQUIRED_GATE_AFFLICTION = "Hiro_Executor_Of_Justice"
-- 联机时客户端发请求用的消息名
local FALSE_EVIDENCE_NETMSG = "Touhou.FalseEvidence.Request"

local FALSE_EVIDENCE_COOLDOWN = 300

-- death_rewind_used_this_round 是全局锁，一巡回里不管谁触发过一次就完事
local death_rewind_used_this_round = false
local false_evidence_cooldown = setmetatable({}, { __mode = "k" })
local false_evidence_key_was_down = false

local function notify_local_player(message, character)
    if SERVER then
        local ok = pcall(function()
            if Game ~= nil and Game.SendDirectChatMessage ~= nil
                    and Util ~= nil and Util.FindClientCharacter ~= nil
                    and ChatMessageType ~= nil and character ~= nil then
                local target_client = Util.FindClientCharacter(character)
                if target_client ~= nil then
                    Game.SendDirectChatMessage("天赋提示", message, nil, ChatMessageType.Default, target_client)
                    return
                end
            end
            error("server ui notify unavailable")
        end)
        if not ok then
            print(message)
        end
        return
    end

    if not CLIENT then
        print(message)
        return
    end

    local ok, shown = pcall(function()
        if GUI ~= nil and GUI.AddMessage ~= nil and Color ~= nil then
            GUI.AddMessage(message, Color(180, 255, 180, 255))
            return true
        end
        if GUI ~= nil and GUI.GUI ~= nil and GUI.GUI.AddMessage ~= nil and Color ~= nil then
            GUI.GUI.AddMessage(message, Color(180, 255, 180, 255))
            return true
        end
        return false
    end)

    if (not ok) or (not shown) then
        print(message)
    end
end

local function format_cooldown_time(remaining_seconds)
    local total = math.max(0, math.ceil(remaining_seconds or 0))
    local minutes = math.floor(total / 60)
    local seconds = total % 60

    if minutes > 0 then
        return tostring(minutes) .. "分" .. tostring(seconds) .. "秒"
    end

    return tostring(seconds) .. "秒"
end

-- 心跳每帧每个角色都要查天赋，Identifier 每次新建会不断堆分配、搞出 GC 尖峰，缓存起来
-- HasTalent 有的版本只吃 Identifier，有的直接吃字符串，两种都喂
local identifier_cache = {}
local function cached_identifier(name)
    local id = identifier_cache[name]
    if id == nil then
        id = Identifier(name)
        identifier_cache[name] = id
    end
    return id
end

local function has_talent(character, talent_identifier)
    if character == nil or character.Info == nil then
        return false
    end

    local ok, result = pcall(function()
        return character.HasTalent(cached_identifier(talent_identifier))
    end)

    if ok and result then
        return true
    end

    ok, result = pcall(function()
        return character.HasTalent(talent_identifier)
    end)

    return ok and result
end

local function has_affliction(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    return affliction ~= nil and affliction.Strength ~= nil and affliction.Strength > 0
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

local function apply_affliction(character, affliction_identifier, strength)
    if character == nil or character.CharacterHealth == nil then
        return
    end

    local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
    local limb = get_main_limb(character)
    if prefab == nil or limb == nil then
        return
    end

    character.CharacterHealth.ApplyAffliction(limb, prefab.Instantiate(strength or 1))
end

local function remove_all_afflictions(character)
    local health = character.CharacterHealth
    if health == nil then
        return
    end

    local ok, afflictions = pcall(function()
        return health.GetAllAfflictions()
    end)
    if not ok or afflictions == nil then
        return
    end

    for affliction in afflictions do
        if affliction ~= nil and affliction.Prefab ~= nil then
            affliction.Strength = 0
        end
    end
end

-- 天赋1：血线压到 1% 以下就清全身 affliction，一个巡回全局只有一次
local function handle_death_rewind(character)
    if not has_talent(character, TALENT_DEATH_REWIND) then
        return
    end

    if not has_affliction(character, REQUIRED_GATE_AFFLICTION) then
        return
    end

    local vitality = character.Vitality or 0
    local max_vitality = character.MaxVitality or 1
    if max_vitality <= 0 then
        return
    end

    local threshold = max_vitality * 0.01
    if vitality > threshold then
        return
    end

    if death_rewind_used_this_round then
        return
    end

    death_rewind_used_this_round = true

    remove_all_afflictions(character)
end

-- prediction_only=true 只走冷却和提示，不放真的（联机客户端本地预演用）
local function try_activate_false_evidence(character, prediction_only)
    if character == nil or character.IsDead or character.Removed then
        return
    end

    if not has_talent(character, TALENT_FALSE_EVIDENCE) then
        return
    end

    if not has_affliction(character, REQUIRED_GATE_AFFLICTION) then
        return
    end

    local now = Timer.GetTime()
    local next_time = false_evidence_cooldown[character] or 0
    if now < next_time then
        local remain = next_time - now
        notify_local_player("天赋冷却中，剩余：" .. format_cooldown_time(remain), character)
        return
    end

    false_evidence_cooldown[character] = now + FALSE_EVIDENCE_COOLDOWN
    if not prediction_only then
        -- 实际持续多久归 aff 自己的 duration 管
        apply_affliction(character, FALSE_EVIDENCE_BUFF, 1)
    end

    notify_local_player("天赋【伪证专家】已激活", character)

    return true
end

-- 联机：客户端只发请求，真放由服务端来
if SERVER then
    if Networking ~= nil and Networking.Receive ~= nil then
        Networking.Receive(FALSE_EVIDENCE_NETMSG, function(message, client)
            if client == nil or client.Character == nil then
                return
            end
            try_activate_false_evidence(client.Character)
        end)
    else
        Hook.Add("netMessageReceived", "Touhou.FalseEvidence.NetRequest", function(message, client, id)
            if id ~= FALSE_EVIDENCE_NETMSG or client == nil or client.Character == nil then
                return
            end
            try_activate_false_evidence(client.Character)
        end)
    end
end

-- Alt+X 按下沿触发
if CLIENT then
    Hook.Add("think", "Touhou.FalseEvidence.Hotkey", function()
        if Character.Controlled == nil or GUI == nil or GUI.GUI == nil then
            return
        end

        if GUI.GUI.PauseMenuOpen then
            return
        end

        if PlayerInput == nil or PlayerInput.KeyDown == nil then
            return
        end

        -- 有的环境没有 Microsoft 命名空间，兜一下防止 nil 索引
        local keys = Keys
        if keys == nil and Microsoft ~= nil
                and Microsoft.Xna ~= nil
                and Microsoft.Xna.Framework ~= nil
                and Microsoft.Xna.Framework.Input ~= nil then
            keys = Microsoft.Xna.Framework.Input.Keys
        end
        if keys == nil then
            return
        end

        local alt_down = PlayerInput.KeyDown(keys.LeftAlt)
                or PlayerInput.KeyDown(keys.RightAlt)
        local x_down = PlayerInput.KeyDown(keys.X)
        local combo_down = alt_down and x_down

        if combo_down and not false_evidence_key_was_down then
            if Game.IsSingleplayer then
                try_activate_false_evidence(Character.Controlled)
            else
                local predicted_ok = try_activate_false_evidence(Character.Controlled, true)
                if predicted_ok and Networking ~= nil and Networking.Start ~= nil and Networking.Send ~= nil then
                    local msg = Networking.Start(FALSE_EVIDENCE_NETMSG)
                    if msg ~= nil then
                        Networking.Send(msg)
                    end
                end
            end
        end

        false_evidence_key_was_down = combo_down
    end)
end

Hook.Add("roundStart", "Touhou.Talents.RoundReset", function()
    death_rewind_used_this_round = false
    false_evidence_cooldown = setmetatable({}, { __mode = "k" })
    false_evidence_key_was_down = false
end)

-- init.lua 会把这个脚本同时塞进服务端和客户端两个环境，联机时客户端别跟着跑，
-- 重复清 affliction 既浪费又和服务端状态冲突，交给服务端就行
Hook.Add("think", "Touhou.DeathRewind.Tick", function()
    if CLIENT and not Game.IsSingleplayer then return end
    for character in Character.CharacterList do
        if character ~= nil and not character.Removed and not character.IsDead then
            handle_death_rewind(character)
        end
    end
end)