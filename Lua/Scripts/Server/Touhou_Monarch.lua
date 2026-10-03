-- 叫~

-- magic numbers
local minimum_speed = 7
local acceleration_magnitude = 0.25
local steering_magnitude = 5
local acquire_cone = math.rad(30)  -- 无鱼叉时，鼠标瞄准方向两侧的敌人捕获半锥角
local acquire_range = 2000  -- 无鱼叉时的敌人捕获距离上限（模拟单位，约20米）

local last_harpoon = {}  -- map from Item (launcher) to Item (round01)
local active_rounds = {}  -- list of {round02, harpoon|nil, shooter}；harpoon 为 nil 时改用鼠标引导

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

    local shooter_position = shooter.WorldPosition
    local aim_direction = get_direction(shooter.CursorWorldPosition - shooter_position)
    local best_target = nil
    local best_angle = acquire_cone  

    for character in Character.CharacterList do
        if not character.Removed
            and not character.IsDead
            and character ~= shooter
            and character.TeamID ~= shooter.TeamID then
            local offset = character.WorldPosition - shooter_position
            if offset.Length() <= acquire_range then
                local angle = get_angle_difference(get_direction(offset), aim_direction)
                if angle < best_angle then
                    best_angle = angle
                    best_target = character
                end
            end
        end
    end
    return best_target
end

Hook.Add("think", "touhou_monarch_round02_guide", function()
	if CLIENT and Game.Paused then return end
	if Game.GameSession == nil then return end

    for index = #active_rounds, 1, -1 do
        local value = active_rounds[index]
        local round = value[1]
        local harpoon = value[2]
        local shooter = value[3]
        if round.Removed then
            table.remove(active_rounds, index)
        else
            local target_position = nil
            if harpoon ~= nil then
                if harpoon.Removed then
                    table.remove(active_rounds, index)  -- 鱼叉半路没了就不追了（保持原行为）
                else
                    target_position = harpoon.WorldPosition
                end
            else
                local target = find_mouse_target(shooter)
                if target ~= nil then
                    target_position = target.WorldPosition
                end
            end

            if target_position ~= nil then
                local round_position = round.WorldPosition
                local round_direction = get_direction(round.body.LinearVelocity)
                local target_direction = get_direction(target_position - round_position)

                local round_speed = round.body.LinearVelocity.Length()
                if round_speed < minimum_speed then
                    round.body.ApplyLinearImpulse(get_unit_vector(round_direction) * acceleration_magnitude * (minimum_speed - round_speed))
                end

                local steering_force = get_unit_vector(target_direction) - get_unit_vector(round_direction)
                round.body.ApplyLinearImpulse(steering_force * steering_magnitude)
            end
        end
    end
end)