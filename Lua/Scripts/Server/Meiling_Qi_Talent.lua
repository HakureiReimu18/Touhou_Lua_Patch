-- 红美铃天赋「气与姿态」服务端逻辑（Lua 补丁侧）
-- 职责（姿态切换本身由装束按钮 XML 完成，见 Hong_Meirin_gear.xml 的 CustomInterface）：
--   1) 穿着 Hong_Meirin_Plus 的角色没有姿态时自动补默认姿态「蓄势」（覆盖巡回开始/重生/中途穿上）
--   2) 气的被动增减：蓄势每秒 +1，全力每秒 -1（触底停 0，不自动切姿态）
--   3) 蓄势姿态下战斗获气：对敌人造成伤害 / 受到敌人攻击，按伤害量折算并设单次上下限
--   4) 全力姿态专属武器：命中时 20% 概率额外 50% 伤害，同一次攻击结算内消耗 2% 气
--   5) 脱下装束/死亡后清除姿态与气
-- 气与姿态的数值效果（近战伤害/攻速/眩晕抗性、护身减伤等）全部在
-- 本体模组 Touhou_Character_Afflictions.xml 的 affliction 定义里，本脚本只驱动强度。

if CLIENT and not Game.IsSingleplayer then return end

local OUTFIT_IDENTIFIER = "Hong_Meirin_Plus"
local EXCLUSIVE_WEAPON_TAG = "Hong_Meirin_Exclusive"

local AFF_QI = "Hong_Meirin_Qi"
local AFF_STANCE_CHARGE = "Hong_Meirin_Stance_Charge"
local AFF_STANCE_GUARD = "Hong_Meirin_Stance_Guard"
local AFF_STANCE_ALLOUT = "Hong_Meirin_Stance_Allout"
local AFF_STANCE_CD = "Hong_Meirin_Stance_CD"
local STANCE_AFFS = { AFF_STANCE_CHARGE, AFF_STANCE_GUARD, AFF_STANCE_ALLOUT }

-- 槽位常量：两处判定都在 ApplyAttack 每次命中的路径上，表提到模块级复用（不再逐次建表）
local OUTFIT_SLOTS = { InvSlotType.InnerClothes, InvSlotType.OuterClothes }
local HAND_SLOTS = { InvSlotType.RightHand, InvSlotType.LeftHand }

local QI_MAX = 100
-- 被动增减（每秒）
local QI_CHARGE_PER_SEC = 1
local QI_ALLOUT_PER_SEC = -1
-- 战斗获气：gain = clamp(伤害 × K, MIN, MAX)，上下限防止高频低伤刷气与低频高伤爆气（数值待平衡）
local DEAL_K, DEAL_MIN, DEAL_MAX = 0.15, 0.2, 4
local TAKE_K, TAKE_MIN, TAKE_MAX = 0.2, 0.2, 4
-- 全力专属武器触发
local ALLOUT_PROC_CHANCE = 0.2
local ALLOUT_PROC_MULTIPLIER = 1.5
local ALLOUT_PROC_QI_COST = 2

local TICK_INTERVAL = 0.5   -- 姿态维护/清理节拍
local QI_INTERVAL = 1.0     -- 气的被动增减节拍
local DEBUG_LOG = false

-- LuaCs 没注册 Timing 全局，think 缺 delta_time 时用固定步长兜底
local THINK_STEP_FALLBACK = 1.0 / 60.0

local tick_elapsed = 0
local qi_elapsed = 0

local function clamp(value, min_value, max_value)
    if value < min_value then return min_value end
    if value > max_value then return max_value end
    return value
end

local function is_wearing_outfit(character)
    if character == nil or character.Inventory == nil then return false end
    for _, slot in ipairs(OUTFIT_SLOTS) do
        local item = character.Inventory.GetItemInLimbSlot(slot)
        if item ~= nil and item.Prefab ~= nil and tostring(item.Prefab.Identifier) == OUTFIT_IDENTIFIER then
            return true
        end
    end
    return false
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then return nil end
    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

local function get_strength(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then return 0 end
    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    if affliction == nil then return 0 end
    return affliction.Strength or 0
end

-- 设置 affliction 强度；没有时先施加再写回（ApplyAffliction 会按 100/MaxVitality 缩放，必须覆盖）
local function set_strength(character, affliction_identifier, value, max_strength)
    if character == nil or character.CharacterHealth == nil then return end
    value = clamp(value, 0, max_strength or QI_MAX)

    local health = character.CharacterHealth
    local affliction = health.GetAffliction(affliction_identifier)
    if affliction ~= nil then
        affliction.Strength = value
        return
    end
    if value <= 0 then return end

    local prefab = AfflictionPrefab.Prefabs[affliction_identifier]
    local limb = get_main_limb(character)
    if prefab == nil or limb == nil then
        if DEBUG_LOG then print("Meiling.QiTalent: 找不到 affliction " .. affliction_identifier .. " 或主体位") end
        return
    end
    health.ApplyAffliction(limb, prefab.Instantiate(1))
    affliction = health.GetAffliction(affliction_identifier)
    if affliction ~= nil then
        affliction.Strength = value
    end
end

local function get_qi(character)
    return get_strength(character, AFF_QI)
end

local function add_qi(character, delta)
    if delta == 0 then return end
    set_strength(character, AFF_QI, get_qi(character) + delta, QI_MAX)
end

-- 当前姿态标识符；无姿态返回 nil（姿态 affliction minstrength=1，强度不足 1 视为无）
local function get_active_stance(character)
    for _, identifier in ipairs(STANCE_AFFS) do
        if get_strength(character, identifier) >= 1 then
            return identifier
        end
    end
    return nil
end

local function is_enemy_of(a, b)
    if a == nil or b == nil or a == b then return false end
    return a.TeamID ~= b.TeamID
end

-- 攻击总伤害：对应 API 未必存在，统一用 pcall 包裹（模块级函数，避免每次命中新建闭包）
local function total_character_damage(attack)
    return attack.GetTotalCharacterDamage()
end

-- 造成伤害/受到伤害共用：按攻击强度折算获气（仅蓄势姿态）
local function grant_combat_qi(character, attack, k, min_gain, max_gain)
    if get_active_stance(character) ~= AFF_STANCE_CHARGE then return end
    if attack == nil then return end

    local damage = 0
    local ok, result = pcall(total_character_damage, attack)
    if ok and result ~= nil then damage = result end
    if damage <= 0 then return end

    add_qi(character, clamp(damage * k, min_gain, max_gain))
end

-- 全力专属武器判定：优先攻击来源物品，退回双手物品（照 Touhou_Magic_Weapon_Bonus 的模式）
local function is_exclusive_weapon_attack(attacker, attack)
    if attack ~= nil and attack.SourceItem ~= nil then
        if attack.SourceItem.HasTag(EXCLUSIVE_WEAPON_TAG) then return true end
    end
    if attacker == nil or attacker.Inventory == nil then return false end
    for _, slot in ipairs(HAND_SLOTS) do
        local item = attacker.Inventory.GetItemAt(slot)
        if item ~= nil and item.HasTag(EXCLUSIVE_WEAPON_TAG) then return true end
    end
    return false
end

-- ==================== 全力触发 ====================
-- 按攻击实例去重：一次挥击会对多个肢体各调一次 ApplyAttack，20% 判定与耗气只做一次。
-- 注意：本补丁直接乘 DamageMultiplier 不还原，依赖 Touhou_Magic_Weapon_Bonus.lua 先加载
-- （其 Before 先备份原始倍率、After 还原；init.lua 中本脚本须排在它之后 dofile）。
local allout_proc_state = setmetatable({}, { __mode = "k" }) -- attack -> { proc = bool }

Hook.Patch("Barotrauma.Character", "ApplyAttack", function(instance, ptable)
    local attacker = ptable["attacker"]
    if attacker == nil or attacker.IsDead or attacker.Removed then return end

    -- 两个判定都是纯读、无副作用：先做过滤性更强的武器判定，少走 GetAffliction
    local attack = ptable["attack"]
    if attack == nil then return end
    if not is_exclusive_weapon_attack(attacker, attack) then return end
    if get_active_stance(attacker) ~= AFF_STANCE_ALLOUT then return end

    local state = allout_proc_state[attack]
    if state == nil then
        local proc = get_qi(attacker) > 0 and math.random() < ALLOUT_PROC_CHANCE
        state = { proc = proc }
        allout_proc_state[attack] = state
        if proc then
            add_qi(attacker, -ALLOUT_PROC_QI_COST)
        end
    end
    if state.proc then
        attack.DamageMultiplier = attack.DamageMultiplier * ALLOUT_PROC_MULTIPLIER
    end
end, Hook.HookMethodType.Before)

-- ==================== 战斗获气 ====================
-- ApplyAttack 的 instance 是受击者，ptable["attacker"] 是攻击者；无攻击者的环境伤害不计。
Hook.Patch("Barotrauma.Character", "ApplyAttack", function(instance, ptable)
    local victim = instance
    local attacker = ptable["attacker"]
    local attack = ptable["attack"]
    if victim == nil or attacker == nil then return end

    -- 美铃对敌人造成伤害
    if is_enemy_of(victim, attacker) and attacker.IsHuman and is_wearing_outfit(attacker) then
        grant_combat_qi(attacker, attack, DEAL_K, DEAL_MIN, DEAL_MAX)
    end

    -- 美铃受到敌人攻击伤害
    if is_enemy_of(attacker, victim) and victim.IsHuman and is_wearing_outfit(victim) then
        grant_combat_qi(victim, attack, TAKE_K, TAKE_MIN, TAKE_MAX)
    end
end, Hook.HookMethodType.After)

-- ==================== 周期维护 ====================
local function maintain_character(character)
    if character == nil or character.Removed or character.IsDead or not character.IsHuman then
        return
    end

    if not is_wearing_outfit(character) then
        -- 脱下装束：清除姿态与气（强度归零，姿态 affliction minstrength=1 归零即失效）
        if get_strength(character, AFF_QI) > 0 or get_active_stance(character) ~= nil then
            set_strength(character, AFF_QI, 0, QI_MAX)
            for _, identifier in ipairs(STANCE_AFFS) do
                set_strength(character, identifier, 0, 100)
            end
        end
        return
    end

    -- 无姿态时补默认姿态「蓄势」（覆盖巡回开始/重生/中途穿上）
    if get_active_stance(character) == nil then
        set_strength(character, AFF_STANCE_CHARGE, 100, 100)
        if DEBUG_LOG then print("Meiling.QiTalent: " .. tostring(character.Name) .. " 默认姿态=蓄势") end
    end
end

local function tick_qi(character)
    if character == nil or character.Removed or character.IsDead or not character.IsHuman then
        return
    end
    if not is_wearing_outfit(character) then return end

    local stance = get_active_stance(character)
    if stance == AFF_STANCE_CHARGE then
        add_qi(character, QI_CHARGE_PER_SEC)
    elseif stance == AFF_STANCE_ALLOUT then
        add_qi(character, QI_ALLOUT_PER_SEC)
    end
    -- 护身：不增不减
end

print("Meiling.QiTalent: 脚本已加载 (DEBUG_LOG=" .. tostring(DEBUG_LOG) .. ")")

-- 巡回开始：穿着装束的角色强制回到默认姿态「蓄势」（姿态 affliction 无 duration 会跨巡回残留；
-- 气不归零，跨巡回保留）。think 里的无姿态兜底覆盖重生/中途穿上等其余情况
Hook.Add("roundStart", "Meiling.QiTalent.RoundStart", function()
    for character in Character.CharacterList do
        local ok, err = pcall(function()
            if character.IsHuman and not character.IsDead and not character.Removed and is_wearing_outfit(character) then
                for _, identifier in ipairs(STANCE_AFFS) do
                    set_strength(character, identifier, 0, 100)
                end
                set_strength(character, AFF_STANCE_CHARGE, 100, 100)
            end
        end)
        if not ok then print("Meiling.QiTalent 错误: " .. tostring(err)) end
    end
end)

-- 暂停检测：think 钩子在暂停时仍会触发，气的增减必须跳过
-- （模块级命名函数 + pcall(命名函数)，避免每帧新建闭包）
local function read_game_paused()
    if GameMain ~= nil and GameMain.Instance ~= nil then
        return GameMain.Instance.Paused
    end
    return false
end

local function is_game_paused()
    local ok, paused = pcall(read_game_paused)
    return ok and paused == true
end

Hook.Add("think", "Meiling.QiTalent.Update", function(delta_time)
    if is_game_paused() then return end
    tick_elapsed = tick_elapsed + (delta_time or THINK_STEP_FALLBACK)
    qi_elapsed = qi_elapsed + (delta_time or THINK_STEP_FALLBACK)

    if tick_elapsed >= TICK_INTERVAL then
        tick_elapsed = 0
        for character in Character.CharacterList do
            local ok, err = pcall(maintain_character, character)
            if not ok then print("Meiling.QiTalent 错误: " .. tostring(err)) end
        end
    end

    if qi_elapsed >= QI_INTERVAL then
        qi_elapsed = 0
        for character in Character.CharacterList do
            local ok, err = pcall(tick_qi, character)
            if not ok then print("Meiling.QiTalent 错误: " .. tostring(err)) end
        end
    end
end)
