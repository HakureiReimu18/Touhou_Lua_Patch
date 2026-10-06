-- 椛天赋「山中的千里眼」——附近有敌人（生物、存活）时获得「警戒」：
-- 行走速度 +15%、游泳速度 +10%、近战攻速 +15%（数值都在警戒 affliction 里）。
-- 替代 Talents/TalentsMomizi.xml 里那条 1.5 秒 interval 能力组（旧 XML 只注释、未删除，保留备查）。
-- 判定与旧 XML 完全一致：同一艘潜艇内、距离 1500 以内、存活且非人类非宠物（Monster）至少一个。
-- 「警戒」affliction 每秒衰减 8 点（满 10 点），所以这里 1 秒续一次，效果不会闪断
-- （旧 XML 是 1.5 秒一次，中间会有一小段断档，这里顺手收紧）。

-- 服务端权威：联机客户端不执行；单机和开房主机照常执行
if not SERVER and not Game.IsSingleplayer then return end

local TT = TouhouTalents

local AFF_TALENT = TT.id("Touhou_Momizi_Character_Effect")
local SENOR_NAME = "Touhou_Momizi_Character_Effect_Senor"

-- 轮询节拍与警戒距离（与旧 XML 一致：1500 显示单位）
local SCAN_INTERVAL = 1.0
local DETECT_DISTANCE_SQ = 1500 * 1500
-- 施加强度（警戒 affliction maxstrength=10，引擎会夹住；衰减 -8/秒）
local SENOR_STRENGTH = 100
local THINK_STEP_FALLBACK = 1.0 / 60.0

local DEBUG_LOG = false
local scan_elapsed = 0

local function log(message)
    if DEBUG_LOG then
        print("Momizi.Alert: " .. message)
    end
end

-- 附近（同潜艇、1500 以内）有没有存活怪物
local function has_nearby_enemy(character)
    local position = character.WorldPosition
    local submarine = character.Submarine

    for other in Character.CharacterList do
        if other ~= character and not other.IsDead and not other.Removed
                and other.Submarine == submarine
                and not other.IsHuman and not other.IsPet then
            local other_position = other.WorldPosition
            local dx = position.X - other_position.X
            local dy = position.Y - other_position.Y
            if dx * dx + dy * dy < DETECT_DISTANCE_SQ then
                return true
            end
        end
    end

    return false
end

local function update_character(character)
    if not character.IsHuman or character.IsDead or character.Removed
            or TT.get_strength(character, AFF_TALENT) <= 0 then
        return
    end

    if has_nearby_enemy(character) then
        TT.apply_affliction(character, SENOR_NAME, SENOR_STRENGTH)
        log(tostring(character.Name) .. " 附近有敌人，警戒续上")
    end
end

Hook.Add("think", "Momizi.Alert.Update", function(delta_time)
    if TT.is_game_paused() then
        return
    end

    scan_elapsed = scan_elapsed + (delta_time or THINK_STEP_FALLBACK)
    if scan_elapsed < SCAN_INTERVAL then
        return
    end
    scan_elapsed = 0

    for character in Character.CharacterList do
        local ok, err = pcall(update_character, character)
        if not ok then
            print("Momizi.Alert 错误: " .. tostring(err))
        end
    end
end)
