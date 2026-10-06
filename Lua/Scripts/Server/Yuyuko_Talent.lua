-- 幽幽子天赋「华胥的亡灵」——「一分钟一次，专属武器立即处决失去意识的敌对生物」的服务端实现。
-- 替代 Talents/TalentsYuyuko.xml 里那条 OnAttack + CharacterAbilityModifyAttackData implode 的能力组
-- （旧 XML 只注释、未删除，保留备查）。旧实现的问题：
--   1) 冷却 affliction 的属性写成了 applytpself（正确写法 applytoself），属性名对不上，
--      冷却被施加到了被打的目标身上，攻击者自身根本没有冷却，「一分钟一次」不生效；
--   2) Character 的 isunconscious 对尸体同样为真，打尸体也会触发能力组、白转一次冷却；
--   3) 处决时间点挂在引擎的 implode 标记上，命中与效果时序不好控制。
-- 本脚本的判定（服务端权威，单机同样生效）：
--   命中结算前目标已昏迷且未死 → 目标敌对 → 攻击者有幽幽子装束效果 + 手持专属武器 → 冷却就绪
--   → 处决（复刻引擎 Implode：压伤拉满 → Kill(Pressure) → 肢解/内爆特效），
--   并把冷却施加到攻击者自己身上（沿用原 affliction，图标与持续时间仍在 XML 里）。
-- 触发点：AbilityAttackData 构造钩子（命中结算前判定，引擎触发 OnAttack 天赋的位置）
--   + think 钩子（本帧结束后处决）：判定 → 本次伤害结算 → 目标没被这一击打死才处决并上冷却。
--   这样既保留旧版「先结算伤害、再处决」的时序（这一击直接打死目标时不算处决、不消耗冷却），
--   又只需要一个补丁，不用在全游戏最热的 Character.ApplyAttack 上挂 Before/After 两个钩子。
--   处决发生在下一帧的 think（延迟一帧，视觉上无感知）；无敌/上帝模式挡下击杀时同样不消耗冷却。

-- 服务端权威：联机客户端不执行；单机和开房主机（SERVER 为真）照常执行
if not SERVER and not Game.IsSingleplayer then return end

-- 装束+ 的天赋标记（能力条件里就是它）
local AFF_TALENT = Identifier("Touhou_Yuyuko_Character_Effect")
-- 手持专属武器时由武器 OnActive 授予（西行妖枝 / 彼岸花裁决者 / 幽雅 三件）
local AFF_WEAPON = Identifier("Yuyuko_Weapons")
-- 处决冷却，60 秒（duration 在 affliction 定义里）；Prefabs 字典按项目惯例用字符串索引
local AFF_COOLDOWN_NAME = "Touhou_Yuyuko_Attack_Implode_CD"
local AFF_COOLDOWN = Identifier(AFF_COOLDOWN_NAME)

local DEBUG_LOG = false

local function log(message)
    if DEBUG_LOG then
        print("Yuyuko.Talent: " .. message)
    end
end

local function get_strength(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return 0
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    if affliction == nil then
        return 0
    end

    return affliction.Strength or 0
end

local function get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

-- 攻击来源武器：子弹（带 Projectile）取发射器，近战就是物品本身
local function resolve_weapon(attack)
    if attack == nil then
        return nil
    end

    local source_item = attack.SourceItem
    if source_item == nil or source_item.Removed then
        return nil
    end

    local projectile = source_item.GetComponentString("Projectile")
    if projectile ~= nil and projectile.Launcher ~= nil then
        return projectile.Launcher
    end

    return source_item
end

-- 对应旧 XML 的 AbilityConditionAttackData（手持远程 / 近战，炮塔不算）
local function is_exclusive_weapon_attack(attack)
    local weapon = resolve_weapon(attack)
    if weapon == nil or weapon.Removed then
        return false
    end
    if weapon.GetComponentString("Turret") ~= nil then
        return false
    end
    if weapon.GetComponentString("MeleeWeapon") == nil and weapon.GetComponentString("RangedWeapon") == nil then
        return false
    end

    return true
end

-- 对应旧 XML 的 targettype="Enemy"：HumanAIController.IsFriendly 取反，失败时退回阵营判断。
-- 具名函数 + pcall 参数形式：省掉每次调用现场新建闭包；pcall 保留，异常时按阵营兜底不中断处决逻辑
local function friendly_check(attacker, victim)
    return HumanAIController.IsFriendly(attacker, victim)
end

local function is_enemy_of(attacker, victim)
    local ok, friendly = pcall(friendly_check, attacker, victim)
    if ok then
        return friendly ~= true
    end

    return attacker.TeamID ~= victim.TeamID
end

-- 复刻 Character.Implode()：压伤拉满 → Kill(Pressure) → 肢解（内爆特效在 BreakJoints 里）
-- 返回是否真的杀死了目标（无敌/上帝模式会被引擎挡住，此时不吃冷却）
local function execute(victim, attacker)
    local health = victim.CharacterHealth
    local pressure = AfflictionPrefab.Pressure
    local affliction = nil
    if pressure ~= nil then
        affliction = pressure.Instantiate(pressure.MaxStrength, attacker)
        if health ~= nil then
            health.ApplyAffliction(nil, affliction)
        end
    end

    victim.Kill(CauseOfDeathType.Pressure, affliction, false)
    if not victim.IsDead then
        return false
    end

    pcall(function()
        victim.BreakJoints()
    end)
    log("处决 " .. tostring(victim.Name) .. "（由 " .. tostring(attacker.Name) .. "）")
    return true
end

-- 冷却挂在攻击者自己身上：先施加再写回强度（ApplyAffliction 会按 100/MaxVitality 缩放）
local function apply_cooldown(character)
    local prefab = AfflictionPrefab.Prefabs[AFF_COOLDOWN_NAME]
    local health = character.CharacterHealth
    local limb = get_main_limb(character)
    if prefab == nil or health == nil or limb == nil then
        log("找不到冷却 affliction 或主体位")
        return
    end

    health.ApplyAffliction(limb, prefab.Instantiate(prefab.MaxStrength))
    local applied = health.GetAffliction(AFF_COOLDOWN)
    if applied ~= nil then
        applied.Strength = prefab.MaxStrength
    end
end

-- 命中结算前（AbilityAttackData 构造时）判定并记下待处决目标，处决本身放到下一帧 think：
-- 这样「先结算伤害、目标没被这一击打死才处决」的时序不变，而补丁只挂在构造函数上。
-- 键是受击者（同一次命中会按肢体多次构造，重复只覆盖同一个键）；弱键表，角色移除后自动回收。
local pending_execute = setmetatable({}, { __mode = "k" })

Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    local victim = ptable["target"]
    if victim == nil or victim.Removed or victim.IsDead then
        return
    end

    -- 昏迷且未死：对应旧 XML 的 Conditional isunconscious="true"（尸体不算）
    if not victim.IsUnconscious then
        return
    end

    local attacker = ptable["attacker"]
    if attacker == nil or attacker.Removed or attacker.IsDead or attacker == victim then
        return
    end

    if get_strength(attacker, AFF_TALENT) <= 0 or get_strength(attacker, AFF_WEAPON) <= 0 then
        return
    end

    if get_strength(attacker, AFF_COOLDOWN) > 0 then
        return
    end

    if not is_exclusive_weapon_attack(ptable["sourceAttack"]) then
        return
    end

    if not is_enemy_of(attacker, victim) then
        return
    end

    pending_execute[victim] = attacker
end, Hook.HookMethodType.Before)

-- 本帧结束后统一结算待处决：此时伤害已经打完，能判断「目标是不是被这一击自己打死的」。
-- 没有待处理目标时每帧只花一次 next()（绝大多数帧都是这样）。
Hook.Add("think", "Yuyuko.Talent.ExecutePending", function()
    -- 逐个取第一个并立刻摘掉（不用 pairs 边遍历边删，避免依赖遍历期改表的行为）
    while true do
        local victim, attacker = next(pending_execute)
        if victim == nil then
            break
        end
        pending_execute[victim] = nil

        -- 这一击直接把目标打死了：算普通击杀，不处决、不转冷却
        -- （冷却也在这里复查：同帧里先前那次处决可能已经把冷却用掉了）
        if not victim.IsDead and not victim.Removed
                and not attacker.IsDead and not attacker.Removed
                and get_strength(attacker, AFF_COOLDOWN) <= 0 then
            local ok, killed = pcall(execute, victim, attacker)
            if not ok then
                print("Yuyuko.Talent 处决失败: " .. tostring(killed))
            elseif killed then
                local cooldown_ok, cooldown_err = pcall(apply_cooldown, attacker)
                if not cooldown_ok then
                    print("Yuyuko.Talent 施加冷却失败: " .. tostring(cooldown_err))
                end
            end
            -- killed == false：没真死（无敌 / 上帝模式等），不消耗冷却
        end
    end
end)
