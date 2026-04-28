-- magic numbers
local minimum_speed = 7
local acceleration_magnitude = 0.25
local steering_magnitude = 5

local last_harpoon = {}  -- map from Item (launcher) to Item (round01)
local active_rounds = {}  -- list of pairs of Item {round02, round01}

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

    elseif last_harpoon[weapon] ~= nil and not last_harpoon[weapon].Removed then  -- is round02
--[[         table.insert(active_rounds, {instance.Item, last_harpoon[weapon]}) ]]
        local round02 = instance.Item
        local round01 = last_harpoon[weapon]

        Timer.Wait(function()
            if round02.Removed or round01.Removed then return end
            table.insert(active_rounds, {round02, round01})
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

Hook.Add("think", "touhou_monarch_round02_guide", function()
	if CLIENT and Game.Paused then return end
	if Game.GameSession == nil then return end
    
    local toremove = {}
    for index = #active_rounds, 1, -1 do
        local value = active_rounds[index]
        local round = value[1]
        local target = value[2]
        if round.Removed or target.Removed then
            table.remove(active_rounds, index)
        else
            local round_position = round.WorldPosition
            local target_position = target.WorldPosition
            local round_direction = get_direction(round.body.LinearVelocity)
            local target_direction = get_direction(target_position - round_position)
            
            local round_speed = round.body.LinearVelocity.Length()
            if round_speed < minimum_speed then
                round.body.ApplyLinearImpulse(get_unit_vector(round_direction) * acceleration_magnitude * (minimum_speed - round_speed))
            end
            
            -- if get_angle_difference(round_direction, target_direction) > math.pi / 2 then
            if false then
                -- loses target
                table.remove(active_rounds, index)
            else
                -- steers towards the target
                local steering_force = get_unit_vector(target_direction) - get_unit_vector(round_direction)
                round.body.ApplyLinearImpulse(steering_force * steering_magnitude)
            end
        end
    end
end)