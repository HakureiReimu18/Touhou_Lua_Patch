--[[

AI写注释比我厉害
那我有什么用


天赋脚本（仅包含天赋1与天赋3）：
1) 天赋1：死亡回溯
   - 每巡回一次
   - 当角色生命值 <= 1% 时触发
   - 触发后清空角色当前所有 affliction
   - 施加“死亡回溯”增益，并追加 20 精神病
2) 天赋3：伪证专家
   - 玩家按 Alt + X 主动触发
   - 5 分钟冷却

]]

-- 天赋标识符（需与人才树里定义的 identifier 完全一致）
local TALENT_DEATH_REWIND = "HiroTalent"
local TALENT_FALSE_EVIDENCE = "HiroTalent"

-- 相关 affliction 标识符（需与 Afflictions.xml 完全一致）
local FALSE_EVIDENCE_BUFF = "Hiro_Pseudo_Buff"
local REQUIRED_GATE_AFFLICTION = "Hiro_Executor_Of_Justice"
-- 网络消息标识符（客户端请求服务端触发“伪证专家”）
local FALSE_EVIDENCE_NETMSG = "Touhou.FalseEvidence.Request"

-- 伪证专家冷却：300 秒 = 5 分钟
local FALSE_EVIDENCE_COOLDOWN = 300

-- 状态表：
-- 1) death_rewind_used_this_round: 本巡回是否已经有人触发过天赋1（全局锁）
-- 2) false_evidence_cooldown: 记录角色下次可用“伪证专家”的时间戳
local death_rewind_used_this_round = false
local false_evidence_cooldown = setmetatable({}, { __mode = "k" })
local false_evidence_key_was_down = false


-- 客户端提示
local function notify_local_player(message)
    if not CLIENT then
        return
    end

    -- 优先尝试 GUI 提示
    local ok, shown = pcall(function()
        if GUI ~= nil and GUI.AddMessage ~= nil and Color ~= nil then
            GUI.AddMessage(message, Color(180, 255, 180, 255))
            return true
        end
        return false
    end)

    -- 兜底输出到控制台
    if (not ok) or (not shown) then
        print(message)
    end
end

-- 将剩余秒数格式化为“Xm Ys”便于提示
local function format_cooldown_time(remaining_seconds)
    local total = math.max(0, math.ceil(remaining_seconds or 0))
    local minutes = math.floor(total / 60)
    local seconds = total % 60

    if minutes > 0 then
        return tostring(minutes) .. "分" .. tostring(seconds) .. "秒"
    end

    return tostring(seconds) .. "秒"
end

-- 判定角色是否拥有某个天赋。
-- 兼容两种调用方式：Identifier(...) 与 直接字符串。
local function has_talent(character, talent_identifier)
    if character == nil or character.Info == nil then
        return false
    end

    local ok, result = pcall(function()
        return character.HasTalent(Identifier(talent_identifier))
    end)

    if ok and result then
        return true
    end

    ok, result = pcall(function()
        return character.HasTalent(talent_identifier)
    end)

    return ok and result
end

-- 判定角色是否拥有指定 affliction（strength > 0）。
local function has_affliction(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    return affliction ~= nil and affliction.Strength ~= nil and affliction.Strength > 0
end

-- 获取 affliction 施加用肢体：
-- 优先主肢体，若不可用则回退躯干。
local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

-- 对角色施加指定 affliction。
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

-- 清空角色当前所有 affliction。
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

-- 天赋1：死亡回溯主逻辑
-- 触发条件：
-- - 拥有天赋1
-- - 拥有额外门槛 aff：Hiro_Executor_Of_Justice
-- - 当前生命 <= 最大生命的 1%
-- - 本巡回全局尚未触发过（无论谁触发）
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

    -- 关键步骤1：清空该玩家身上所有 affliction
    remove_all_afflictions(character)
end

-- 天赋3：伪证专家主动施放
local function try_activate_false_evidence(character)
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
        if CLIENT and character == Character.Controlled then
            local remain = next_time - now
            notify_local_player("天赋冷却中，剩余：" .. format_cooldown_time(remain))
        end
        return
    end

    -- 进入冷却
    false_evidence_cooldown[character] = now + FALSE_EVIDENCE_COOLDOWN
    -- 施加“伪证”效果（持续时间由 aff 自身 duration 控制）
    apply_affliction(character, FALSE_EVIDENCE_BUFF, 1)

    if CLIENT and character == Character.Controlled then
        notify_local_player("天赋【伪证专家】已激活")
    end
end

-- 多人模式：客户端只发请求，服务端执行实际施放
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

-- 客户端按键监听：
-- 按下 Alt + X 且是“按下瞬间”时触发伪证专家。
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

        -- 某些环境里 Microsoft 命名空间不可用，这里做兼容保护，避免 nil 索引报错
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
        local x_down = PlayerInput.KeyDown(keys.X)
        local combo_down = alt_down and x_down

        if combo_down and not false_evidence_key_was_down then
            if Game.IsSingleplayer then
                try_activate_false_evidence(Character.Controlled)
            else
                if Networking ~= nil and Networking.Start ~= nil and Networking.Send ~= nil then
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

    -- 每巡回开始重置状态：
    -- - 重置“死亡回溯全局已触发”标记
    -- - 清空伪证冷却表
    -- - 重置按键边沿状态
    Hook.Add("roundStart", "Touhou.Talents.RoundReset", function()
        death_rewind_used_this_round = false
        false_evidence_cooldown = setmetatable({}, { __mode = "k" })
        false_evidence_key_was_down = false
    end)

    -- 统一心跳：遍历角色并检查天赋1触发条件
    Hook.Add("think", "Touhou.DeathRewind.Tick", function()
        for character in Character.CharacterList do
            if character ~= nil and not character.Removed and not character.IsDead then
                handle_death_rewind(character)
            end
        end
    end)