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

    else  -- is round02
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
local function get_direction(vector)  -- in radians
	return math.atan2(vector.Y, vector.X)
end
local function get_angle_difference(rad1, rad2)
    if rad2 < rad1 then
        rad1, rad2 = rad2, rad1
    end
    -- now rad2 >= rad1
    return math.min(rad2 - rad1, 2 * math.pi - rad2 + rad1)
end

-- 无定位鱼叉时的目标搜索：在射手鼠标瞄准方向两侧 acquire_cone 锥角内、
-- acquire_range 距离内，找出与瞄准线夹角最小的非本阵营存活角色。
-- 注意必须用 CursorWorldPosition：CursorPosition 在角色位于潜艇内时是潜艇相对坐标，
-- 直接与世界坐标相减会混入潜艇位置的固定偏移，导致瞄准方向恒偏向一侧。
local function find_mouse_target(shooter)
    if shooter == nil or shooter.Removed then return nil end

    local shooter_position = shooter.WorldPosition
    local aim_direction = get_direction(shooter.CursorWorldPosition - shooter_position)
    local best_target = nil
    local best_angle = acquire_cone  -- 超出锥角的候选直接排除

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
            -- 确定追踪目标点：有鱼叉追鱼叉；没有鱼叉则追射手鼠标瞄准方向上的敌人
            local target_position = nil
            if harpoon ~= nil then
                if harpoon.Removed then
                    table.remove(active_rounds, index)  -- 鱼叉中途失效：保持原行为，停止追踪
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

                -- steers towards the target
                local steering_force = get_unit_vector(target_direction) - get_unit_vector(round_direction)
                round.body.ApplyLinearImpulse(steering_force * steering_magnitude)
            end
        end
    end
end)