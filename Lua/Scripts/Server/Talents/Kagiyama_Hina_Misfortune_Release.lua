-- 键山雏天赋「秘神流雏」：主动释放厄运缠身。
-- 替代 Talents/TalentsKagiyama_Hina.xml 里那条 0.9 秒 interval 能力组（旧 XML 已注释保留备查）。
-- 旧版对 2500 内所有人施加固定数值的 debuff、不含长眠不醒的噩梦，而且装束按钮是先把厄运清零再结算，
-- 脚本读不到释放时的强度；现在按钮只挂释放标记，清厄运与结算都放在这里。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT  = TT.id("Touhou_Kagiyama_Hina_Character_Effect")
local AFF_CURSE   = TT.id("Touhou_Misfortune_Curse")
local AFF_RELEASE = TT.id("Touhou_Misfortune_Curse_Release")

local RELEASE_DEBUFF = "Touhou_Misfortune_Curse_Release_Debuff"
local NIGHTMARE = "Sleepless_Nightmare"

-- 释放标记只活 1 秒，这个节拍够用
local SCAN_INTERVAL = 0.1
-- 与旧 XML 的 2500 显示单位一致
local RELEASE_RANGE_SQ = 2500 * 2500
-- affliction 自身 maxstrength=1，引擎会夹住
local RELEASE_DEBUFF_STRENGTH = 10
-- 噩梦强度 = 厄运强度 × 此系数；厄运满 100 时给 30 点，约持续 20 秒
local NIGHTMARE_PER_CURSE = 0.3
local THINK_STEP_FALLBACK = 1.0 / 60.0

local DEBUG_LOG = false
local scan_elapsed = 0
-- 去重：同一个人的一次释放只结算一次
local fired = {}

local function log(message)
    if DEBUG_LOG then
        print("Kagiyama_Hina.MisfortuneRelease: " .. message)
    end
end

local function release_for(caster)
    -- 条件不成立时解锁去重标记，下一次释放才能再触发
    if not caster.IsHuman or caster.IsDead or caster.Removed
            or TT.get_strength(caster, AFF_TALENT) <= 0 then
        fired[caster] = nil
        return
    end

    if TT.get_strength(caster, AFF_RELEASE) <= 0 then
        fired[caster] = nil
        return
    end

    if fired[caster] then
        return
    end
    fired[caster] = true

    local released = TT.get_strength(caster, AFF_CURSE)
    if released <= 0 then
        log(tostring(caster.Name) .. " 释放标记存在但厄运为 0，跳过")
        return
    end

    local position = caster.WorldPosition
    local hit_count = 0

    for other in Character.CharacterList do
        if other ~= caster and not other.IsDead and not other.Removed then
            local dx = position.X - other.WorldPosition.X
            local dy = position.Y - other.WorldPosition.Y
            if dx * dx + dy * dy <= RELEASE_RANGE_SQ and TT.is_enemy_of(caster, other) then
                TT.apply_affliction(other, RELEASE_DEBUFF, RELEASE_DEBUFF_STRENGTH)
                TT.apply_affliction(other, NIGHTMARE, released * NIGHTMARE_PER_CURSE)
                hit_count = hit_count + 1
            end
        end
    end

    TT.clear_affliction(caster, AFF_CURSE)

    log(string.format("%s 释放厄运（强度 %.1f），命中 %d 个敌人", tostring(caster.Name), released, hit_count))
end

Hook.Add("think", "Kagiyama_Hina.MisfortuneRelease", function(delta_time)
    if TT.is_game_paused() then
        return
    end

    scan_elapsed = scan_elapsed + (delta_time or THINK_STEP_FALLBACK)
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    scan_elapsed = 0

    for character in Character.CharacterList do
        local ok, err = pcall(release_for, character)
        if not ok then
            print("Kagiyama_Hina.MisfortuneRelease 错误: " .. tostring(err))
        end
    end
end)
