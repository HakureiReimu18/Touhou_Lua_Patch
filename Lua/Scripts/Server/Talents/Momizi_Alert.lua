-- 椛天赋「山中的千里眼」——附近有敌人（生物、存活）时获得「警戒」：
-- 行走速度 +15%、游泳速度 +10%、近战攻速 +15%（数值都在警戒 affliction 里）。
-- 替代 Talents/TalentsMomizi.xml 里那条 1.5 秒 interval 能力组（旧 XML 只注释、未删除，保留备查）。
-- 判定与旧 XML 完全一致：同一艘潜艇内、距离 1500 以内、存活且非人类非宠物（Monster）至少一个。
-- 「警戒」affliction duration=1.5 秒，引擎里没有强度衰减，到期是整条删掉，所以这里 1 秒重施续上 duration。
-- 客户端也必须跑这一份：服务端同步角色状态时只带强度、不带 duration（服务端 CharacterHealth.ServerWrite
-- 只写 strength；客户端 ClientRead 对已存在的 aff 只调 Affliction.SetStrength，不碰 duration），
-- 客户端那条 aff 的 duration 只在首次创建时设过一次，客户端不自己重施就会 1.5 秒到期消失、
-- 要等下一次同步才重建——表现就是「警戒一秒有一秒没有」。旧 XML 能力组本来就是两端都跑
-- （CharacterAbilityApplyStatusEffects.AllowClientSimulation = true），搬进 Lua 时漏了客户端这一半。

-- 两端都跑：服务端那份是权威（结算），客户端那份只是为了续上自己这条 aff 的 duration
local TT = TouhouTalents

local AFF_TALENT = TT.id("Touhou_Momizi_Character_Effect")
local SENOR_NAME = "Touhou_Momizi_Character_Effect_Senor"

-- 轮询节拍与警戒距离（与旧 XML 一致：1500 显示单位）
local SCAN_INTERVAL = 1.0
local DETECT_DISTANCE_SQ = 1500 * 1500
-- 施加强度（警戒 affliction maxstrength=10，引擎会夹住）
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
