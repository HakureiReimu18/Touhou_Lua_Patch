-- 灵梦天赋「童祭」——受到非人类造成的伤害降低 20%。
-- 替代 Talents/TalentsReimu.xml 里那条 OnAttacked 能力组（旧 XML 只注释、未删除，保留备查）。
-- 旧 XML 的判定是「攻击数据为 NoWeapon/Melee」（按武器类型区分），描述里写的是「非人类造成的伤害」；
-- 本脚本按描述实现：攻击者不是人类（怪物近战、远程、爆炸都算）都减伤 20%。
-- 实现方式：引擎构造 AbilityAttackData 之后（Postfix）压低这一次攻击的倍率——
-- 只影响这一击；不动 Attack 本体，否则同一发爆炸打到别的目标也会被一起减伤。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT = TT.id("Touhou_Reimu_Character_Effect")
-- 承伤倍率：0.8 = 降低 20%
local INCOMING_DAMAGE_MULTIPLIER = 0.8

Hook.Patch("Barotrauma.AbilityAttackData", ".ctor", function(instance, ptable)
    if instance == nil then
        return
    end

    local attacker = ptable["attacker"]
    if attacker == nil or attacker.Removed or attacker.IsHuman then
        return
    end

    local victim = ptable["target"]
    if victim == nil or victim.Removed or victim.IsDead then
        return
    end

    if TT.get_strength(victim, AFF_TALENT) <= 0 then
        return
    end

    instance.DamageMultiplier = instance.DamageMultiplier * INCOMING_DAMAGE_MULTIPLIER
end, Hook.HookMethodType.After)
