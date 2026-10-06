-- 天赋脚本通用工具（服务端权威）：affliction 读写、敌我判定、攻击来源武器判定。
-- 由 init.lua 在服务端脚本之前加载，各天赋脚本统一用全局 TouhouTalents（TT）。
-- 说明：早期几个天赋脚本（Meiling/Renko/Sanae/Jyoon/Yuyuko/Marisa）自带同名局部实现，
-- 行为一致，后续可以逐个迁移过来。

TouhouTalents = TouhouTalents or {}
local TT = TouhouTalents

-- Identifier 字符串预转换缓存
local identifier_cache = {}
function TT.id(name)
    local cached = identifier_cache[name]
    if cached == nil then
        cached = Identifier(name)
        identifier_cache[name] = cached
    end
    return cached
end

function TT.get_strength(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return 0
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    if affliction == nil then
        return 0
    end

    return affliction.Strength or 0
end

function TT.get_main_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    return character.AnimController.MainLimb or character.AnimController.GetLimb(LimbType.Torso)
end

-- 施加 affliction：强度缩放（100/最大生命）、抗性与叠加上限都交给引擎，
-- 与 XML 里 StatusEffect 施加 affliction 的路径完全一致
function TT.apply_affliction(character, affliction_name, amount)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local prefab = AfflictionPrefab.Prefabs[affliction_name]
    if prefab == nil then
        return false
    end

    character.CharacterHealth.ApplyAffliction(nil, prefab.Instantiate(amount))
    return true
end

-- 清掉某个 affliction：强度归零，引擎下一帧 Update 会移除（等价于 XML 的 ReduceAffliction 打满）
function TT.clear_affliction(character, affliction_identifier)
    if character == nil or character.CharacterHealth == nil then
        return false
    end

    local affliction = character.CharacterHealth.GetAffliction(affliction_identifier)
    if affliction == nil then
        return false
    end

    affliction.Strength = 0
    return true
end

-- 敌我判定：对应 XML 的 targettype="Enemy"（HumanAIController.IsFriendly 取反），
-- 异常时退回阵营判断；具名函数 + pcall 参数形式，省掉每次调用现场新建闭包
local function friendly_check(attacker, victim)
    return HumanAIController.IsFriendly(attacker, victim)
end

function TT.is_enemy_of(attacker, victim)
    if attacker == nil or victim == nil or attacker == victim then
        return false
    end

    local ok, friendly = pcall(friendly_check, attacker, victim)
    if ok then
        return friendly ~= true
    end

    return attacker.TeamID ~= victim.TeamID
end

-- 攻击来源武器：子弹（带 Projectile）取发射器，近战武器就是物品本身
function TT.resolve_weapon(attack)
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

-- 是否炮塔攻击（对应 XML 的 AbilityConditionAttackData weapontype="Turret"）
function TT.is_turret_attack(attack)
    local weapon = TT.resolve_weapon(attack)
    if weapon == nil then
        return false
    end

    return weapon.GetComponentString("Turret") ~= nil
end

-- 暂停检测：think 钩子在暂停时仍会触发，周期性天赋必须跳过（读不到状态时按未暂停处理）
local function read_game_paused()
    if GameMain ~= nil and GameMain.Instance ~= nil then
        return GameMain.Instance.Paused
    end
    return false
end

function TT.is_game_paused()
    local ok, paused = pcall(read_game_paused)
    return ok and paused == true
end
