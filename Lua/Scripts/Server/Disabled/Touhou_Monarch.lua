-- 叫~

-- magic numbers
local minimum_speed = 7
local acceleration_magnitude = 0.25
local steering_magnitude = 5
local acquire_cone = math.rad(30)  -- 无鱼叉时，鼠标瞄准方向两侧的敌人捕获半锥角
local acquire_range = 2000  -- 无鱼叉时的敌人捕获距离上限（模拟单位，约20米）
local acquire_cone_cos_sq = math.cos(acquire_cone) ^ 2  -- 半锥角的 cos² 预存：锥角判定改走点积比较，不用 atan2
local acquire_range_sq = acquire_range * acquire_range  -- 距离上限的平方预存：比平方距离，不用开方

local last_harpoon = {}  -- map from Item (launcher) to Item (round01)
local active_rounds = {}  -- list of {round02, harpoon|nil, shooter}；harpoon 为 nil 时改用鼠标引导
local target_cache = {}  -- shooter -> 目标角色；false 表示本帧已解算过且无目标（每帧开头清空后复用同一张表）

Hook.Add("roundStart", "touhou_monarch_roundstart", function()
    last_harpoon = {}
    active_rounds = {}
end)

Hook.Patch("Barotrauma.Items.Components.Projectile", "Shoot", function(instance, ptable)
    local item = instance.Item
    if item.Prefab.Identifier ~= "Touhou_Monarch_Round01" and item.Prefab.Identifier ~= "Touhou_Monarch_Round02" then return end

    local user = ptable["user"]
    local weapon = instance  -- temporary initial value
    for value in user.HeldItems do
        weapon = value
    end
    if weapon.Prefab.Identifier ~= "Touhou_Monarch" then return end  -- actually should not happen

    if item.Prefab.Identifier == "Touhou_Monarch_Round01" then
        last_harpoon[weapon] = item

    else  
        local round02 = instance.Item
        local round01 = last_harpoon[weapon]
        if round01 ~= nil and round01.Removed then round01 = nil end

        Timer.Wait(function()
            if round02.Removed then return end
            if round01 ~= nil and round01.Removed then return end  -- 有鱼叉但发射后即刻失效：保持原行为，不追踪
            table.insert(active_rounds, {round02, round01, user})
        end, 100)
    end
end)

local function get_unit_vector(rad)
	return Vector2(math.cos(rad), math.sin(rad))
end
local function get_direction(vector)  
	return math.atan2(vector.Y, vector.X)
end
local function get_angle_difference(rad1, rad2)
    if rad2 < rad1 then
        rad1, rad2 = rad2, rad1
    end
    return math.min(rad2 - rad1, 2 * math.pi - rad2 + rad1)
end

-- 没有鱼叉就靠鼠标引导：取准星方向锥角里最近的非本阵营角色。
-- 这里必须用 CursorWorldPosition——潜艇里 CursorPosition 是艇内相对坐标，跟世界坐标一减，瞄准方向会一直偏。
local function find_mouse_target(shooter)
    if shooter == nil or shooter.Removed then return nil end

    -- 同一帧同一射手只解算一次（think 每帧开头清空本表）
    local cached = target_cache[shooter]
    if cached ~= nil then
        if cached == false then return nil end
        return cached
    end

    -- 优化：Entity 没有 X/Y 属性（对着本机 Barotrauma.dll 核对过），所以每个实体只取一次 WorldPosition，
    -- 之后一律拿它的 X/Y 在 Lua 里做双精度标量运算，不再生成 Vector2 用户对象、不再跨边界调 Length。
    local shooter_position = shooter.WorldPosition
    local shooter_x, shooter_y = shooter_position.X, shooter_position.Y
    local cursor_position = shooter.CursorWorldPosition
    local aim_x, aim_y = cursor_position.X - shooter_x, cursor_position.Y - shooter_y
    local aim_length_sq = aim_x * aim_x + aim_y * aim_y
    if aim_length_sq == 0 then
        -- 准星和射手位置完全重合：原实现 atan2(0, 0) = 0，等价于基准方向朝正东
        aim_x, aim_y, aim_length_sq = 1, 0, 1
    end

    local best_target = nil
    local best_cos_sq = acquire_cone_cos_sq

    for character in Character.CharacterList do
        if not character.Removed
            and not character.IsDead
            and character ~= shooter
            and character.TeamID ~= shooter.TeamID then
            local position = character.WorldPosition
            local dx, dy = position.X - shooter_x, position.Y - shooter_y
            local distance_sq = dx * dx + dy * dy
            if distance_sq <= acquire_range_sq then
                if distance_sq == 0 then
                    -- 和射手位置完全重合：原实现 get_direction((0, 0)) = 0，等价于朝向正东
                    dx, dy, distance_sq = 1, 0, 1
                end
                local dot = dx * aim_x + dy * aim_y
                -- dot > 0 把夹角锁在 90° 以内，cos² 在该区间单调递减，
                -- 于是 cos²(夹角) > cos²(best_angle) 与原来的 angle < best_angle 同序同号
                if dot > 0 and dot * dot > best_cos_sq * distance_sq * aim_length_sq then
                    best_cos_sq = dot * dot / (distance_sq * aim_length_sq)
                    best_target = character
                end
            end
        end
    end

    if best_target ~= nil then
        target_cache[shooter] = best_target
    else
        target_cache[shooter] = false
    end
    return best_target
end

-- 弹和目标是否在不同物理坐标系（一个在艇内一个在艇外、或分属两条艇）——这种情况下物理上永远碰不到
local function in_different_space(round_item, target)
    if target == nil then return false end
    local round_sub = round_item.Submarine
    local target_sub = target.Submarine
    if (round_sub == nil) ~= (target_sub == nil) then return true end
    return round_sub ~= nil and target_sub ~= nil and round_sub.ID ~= target_sub.ID
end

-- 坐标系失配导致引擎永远打不到时，按引擎自己的伤害入口补一次命中
local function apply_delayed_hit(round_item, shooter, target, hit_position)
    if target == nil or target.AnimController == nil then return end
    local proj = round_item.GetComponentString("Projectile")
    if proj == nil or proj.Attack == nil then return end

    local limb = target.AnimController.GetLimb(LimbType.Torso)
    if limb == nil or limb.IsSevered then
        limb = nil
        for candidate in target.AnimController.Limbs do
            if candidate ~= nil and not candidate.IsSevered then
                limb = candidate
                break
            end
        end
    end
    if limb == nil then return end

    local attacker = shooter
    if attacker ~= nil and attacker.Removed then attacker = nil end
    if not pcall(function() proj.Attack.DoDamageToLimb(attacker, limb, hit_position, 1.0, false) end) then
        pcall(function() proj.Attack.DoDamageToLimb(attacker, limb, hit_position, 1.0) end)
    end
end

local function remove_item(item)
    if Entity.Spawner ~= nil then
        Entity.Spawner.AddItemToRemoveQueue(item)
    end
end

Hook.Add("think", "touhou_monarch_round02_guide", function()
	if CLIENT and Game.Paused then return end
	if Game.GameSession == nil then return end

    -- 每帧开头清空目标解算缓存：同一帧内多个射手/多枚弹复用结果，跨帧必须重算（目标会动、会死）
    for key in pairs(target_cache) do
        target_cache[key] = nil
    end

    for index = #active_rounds, 1, -1 do
        local value = active_rounds[index]
        local round = value[1]
        local harpoon = value[2]
        local shooter = value[3]
        if round.Removed then
            table.remove(active_rounds, index)
        elseif round.body == nil then
            -- 没刚体就没法转向（刚出膛刚体未建好 / 正在解体）：按同样方式移出，否则下面取成员会每帧报错
            table.remove(active_rounds, index)
        else
            local target_position = nil
            local target_entity = nil
            if harpoon ~= nil then
                if harpoon.Removed then
                    table.remove(active_rounds, index)  -- 鱼叉半路没了就不追了（保持原行为）
                else
                    target_position = harpoon.WorldPosition
                    target_entity = harpoon
                end
            else
                local target = find_mouse_target(shooter)
                if target ~= nil then
                    target_position = target.WorldPosition
                    target_entity = target
                end
            end

            if target_position ~= nil then
                local round_position = round.WorldPosition
                local body = round.body  -- 上面已判过非 nil，取一次省下几次跨边界读
                local velocity = body.LinearVelocity  -- 帧内没人改它，读一次够用（原本方向和速度各读一遍）
                local round_direction = get_direction(velocity)
                local target_direction = get_direction(target_position - round_position)
                local round_speed = velocity.Length()

                -- 坐标系失配：穿 hull 之后引擎可能没把弹转换回目标所在的物理空间（弹和目标物理上错开、
                -- 看着重合但永远碰不到，只会挂着发光）。持续半秒确认后放弃这枚弹。
                local desynced = false
                if in_different_space(round, target_entity) then
                    local frames = (value[4] or 0) + 1
                    value[4] = frames
                    desynced = frames >= 30
                else
                    value[4] = nil
                end

                if desynced then
                    table.remove(active_rounds, index)
                    if (target_position - round_position).Length() < 500 then
                        -- 已经贴到目标附近：按引擎的伤害入口补一次命中再移除，表现得就像命中消失
                        pcall(apply_delayed_hit, round, shooter, target_entity, round_position)
                        pcall(remove_item, round)
                    end
                else
                    if round_speed < minimum_speed then
                        body.ApplyLinearImpulse(get_unit_vector(round_direction) * acceleration_magnitude * (minimum_speed - round_speed))
                    end

                    local steering_force = get_unit_vector(target_direction) - get_unit_vector(round_direction)
                    body.ApplyLinearImpulse(steering_force * steering_magnitude)
                end
            end
        end
    end
end)