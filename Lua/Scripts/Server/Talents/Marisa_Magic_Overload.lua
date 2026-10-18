-- 魔理沙天赋「普通的魔法使」：每次攻击缓慢增长自身魔力过载。
-- 替代 Talents/TalentsMarisa.xml 的两条 OnAttack 概率组（旧 XML 只注释未删，保留备查）。
-- 坑：引擎对爆炸类伤害会按每个受波及肢体各建一次 AbilityAttackData，同一发爆炸会触发多次，
-- 故这里按「攻击对象 + 当前帧」去重，一次攻击只掷一次骰；数值与概率沿用原 XML
-- （技能 <70：30% +0.7；>=70：20% +0.8；操作舰炮时单次增长再减半）。
-- 触发点选 AbilityAttackData 构造函数，覆盖近战/弹幕命中与爆炸等所有带攻击者的攻击事件。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT = Identifier("Touhou_Marisa_Character_Effect")
-- 魔力过载；Prefabs 字典按项目惯例用字符串索引
local OVERLOAD_NAME = "Touhou_Magic_Overload"
local MAGIC_SKILL = Identifier("Touhou_Magic")

-- 技能阈值与概率/数值，与 TalentsMarisa.xml 一致
local SKILL_THRESHOLD = 70
local LOW_SKILL_CHANCE, LOW_SKILL_AMOUNT = 0.3, 0.7
local HIGH_SKILL_CHANCE, HIGH_SKILL_AMOUNT = 0.2, 0.8

-- 操作舰炮时增长减半：舰炮破片类弹药一次命中次数极多
local TURRET_AMOUNT_MULTIPLIER = 0.5
-- 炮塔操作台（潜望镜）标签，游戏的「操作炮塔」AI 指令也用它找操作台
local PERISCOPE_TAG = "periscope"

local DEBUG_LOG = false

local function log(message)
    if DEBUG_LOG then
        print("Marisa.MagicOverload: " .. message)
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

-- 对应旧 XML 的 AbilityConditionAttackData weapontype="Turret" invert="true"：
-- 取不到来源物品时按「条件成立」放行（invert 语义），炮塔射击才拦截
local function is_turret_attack(attack)
    local source_item = attack.SourceItem
    if source_item == nil or source_item.Removed then
        return false
    end

    local projectile = source_item.GetComponentString("Projectile")
    if projectile ~= nil and projectile.Launcher ~= nil then
        return projectile.Launcher.GetComponentString("Turret") ~= nil
    end

    return source_item.GetComponentString("Turret") ~= nil
end

-- 是否在操作舰炮：引擎里 Controller 未被选中时会把 User 置空，故用 periscope 标签
-- 或「选中的 Controller 组件 User 是自己」判定；舵轮/声呐也走 Controller 但无法开火，误判面极小。
local function is_operating_turret(character)
    local selected = character.SelectedItem
    if selected == nil then
        selected = character.SelectedSecondaryItem
    end
    if selected == nil or selected.Removed then
        return false
    end
    if selected.HasTag(PERISCOPE_TAG) then
        return true
    end

    local controller = selected.GetComponentString("Controller")
    return controller ~= nil and controller.User == character
end

-- 施加魔力过载：强度缩放（100/最大生命、抗性、叠加上限）交给引擎，和旧 XML 的施加路径一致
local function add_overload(character, amount)
    local prefab = AfflictionPrefab.Prefabs[OVERLOAD_NAME]
    local health = character.CharacterHealth
    if prefab == nil or health == nil then
        log("找不到魔力过载 affliction")
        return
    end

    health.ApplyAffliction(nil, prefab.Instantiate(amount))
end

-- 同一帧内同一次攻击只判定一次（爆炸按肢体、多段伤害按命中各建一次 AbilityAttackData 时只算一发）；
-- 去重器由 Shared/TalentUtils.lua 提供，咲夜的血之时计用的是同一个
local roll_once_per_attack = TT.new_frame_dedupe("Marisa.MagicOverload.Tick")

Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    local attacker = ptable["attacker"]
    if attacker == nil or attacker.Removed or attacker.IsDead then
        return
    end

    local attack = ptable["sourceAttack"]
    if attack == nil then
        return
    end

    if roll_once_per_attack(attack) then
        return
    end

    if get_strength(attacker, AFF_TALENT) <= 0 then
        return
    end
    if is_turret_attack(attack) then
        return
    end

    local skill = attacker.GetSkillLevel(MAGIC_SKILL) or 0
    local chance, amount
    if skill < SKILL_THRESHOLD then
        chance, amount = LOW_SKILL_CHANCE, LOW_SKILL_AMOUNT
    else
        chance, amount = HIGH_SKILL_CHANCE, HIGH_SKILL_AMOUNT
    end

    if math.random() >= chance then
        return
    end

    -- 操作舰炮时增长减半：放在掷骰之后算，判定概率不变、只有单次数值减半
    if is_operating_turret(attacker) then
        amount = amount * TURRET_AMOUNT_MULTIPLIER
    end

    add_overload(attacker, amount)
    if DEBUG_LOG then
        log(string.format("%s 攻击 +%.3g 过载（技能 %.0f）", tostring(attacker.Name), amount, skill))
    end
end, Hook.HookMethodType.Before)
