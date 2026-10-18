-- 咲夜天赋「完美而潇洒的从者」：每次攻击 35% 概率给自己叠「血之时计」（+0.75）。
-- 替代 Talents/TalentsSakuya.xml 里那条 OnAttack 概率组（旧 XML 只注释未删，保留备查）。
-- 坑：引擎对爆炸类伤害会按每个受波及肢体各建一次 AbilityAttackData，近战/多段伤害武器命中多个肢体时同理，
-- 同一发攻击会把 35% 的概率组触发好几遍——故按「攻击对象 + 当前帧」去重，一次攻击只掷一次骰
-- （与 Marisa_Magic_Overload.lua 同一套写法）。判定条件与旧 XML 一致：装束+ 效果、手持远程/近战（炮塔不算）、
-- 目标是存活敌人。施加仍走 ApplyAffliction，缩放与 100 上限交给引擎；击杀减 10% 那部分仍由 XML 负责。

-- 服务端权威：联机客户端不执行（旧 XML 的 randomchance 本来就是服务端限定）；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

-- 装束+ 的天赋标记
local AFF_TALENT = TT.id("Touhou_Sakuya_Character_Effect")
-- 血之时计；Prefabs 字典按项目惯例用字符串索引
local BLOOD_CLOCK_NAME = "Touhou_Blood_Clock_Time"

-- 与 TalentsSakuya.xml 保持一致
local PROC_CHANCE = 0.35
local PROC_AMOUNT = 0.75

local DEBUG_LOG = false
-- 一次攻击事件只判定一次（爆炸按肢体多次构造 AbilityAttackData 时只算一发）
local roll_once_per_attack = TT.new_frame_dedupe("Sakuya.BloodClock.Tick")

local function log(message)
    if DEBUG_LOG then
        print("Sakuya.BloodClock: " .. message)
    end
end

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

    if TT.get_strength(attacker, AFF_TALENT) <= 0 then
        return
    end
    if not TT.is_handheld_or_melee_attack(attack) then
        return
    end

    -- 目标是存活敌人（对应旧 XML 的 targettype="Enemy" + Conditional IsDead="false"）
    local victim = ptable["target"]
    if victim == nil or victim.Removed or victim.IsDead then
        return
    end
    if not TT.is_enemy_of(attacker, victim) then
        return
    end

    if math.random() >= PROC_CHANCE then
        return
    end

    TT.apply_affliction(attacker, BLOOD_CLOCK_NAME, PROC_AMOUNT)
    log(string.format("%s 攻击 +%.2f 血之时计", tostring(attacker.Name), PROC_AMOUNT))
end, Hook.HookMethodType.Before)
