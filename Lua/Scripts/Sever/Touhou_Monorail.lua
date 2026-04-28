local MAX_CHARGING_TIME = 5
local MIN_DAMAGE_MULTIPLIER = 1
local MAX_DAMAGE_MULTIPLIER = 10

LuaUserData.MakePropertyAccessible(Descriptors["Barotrauma.Items.Components.RangedWeapon"], "WeaponDamageModifier")

local function isHolding(character, item)
    if character == nil or character.Removed or character.Inventory == nil then return false end
    if item == character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) then return true end
    if item == character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) then return true end
    return false
end

Hook.Add("Touhou_Monorail_aiming", "Touhou_Monorail_aiming", function(effect, deltaTime, item, targets, worldPosition)
    local rangedWeapon = targets[2]
    local user = targets[3]
    if rangedWeapon.ReloadTimer > 0 then return end -- don't charge while reloading
    if user.IsKeyDown(InputType.Shoot) then         -- starts charging
        if item.Condition == 101 then
            item.Condition = 1
        elseif item.Condition < 100 then -- still charging
            item.Condition = math.min(item.Condition + 100 / MAX_CHARGING_TIME * deltaTime, 100)
        end
    elseif item.Condition < 101 then
        if isHolding(user, item) then -- shoot
            rangedWeapon.WeaponDamageModifier = MIN_DAMAGE_MULTIPLIER +
            (MAX_DAMAGE_MULTIPLIER - MIN_DAMAGE_MULTIPLIER) * (item.Condition / 100)
            rangedWeapon.use(deltaTime, user)
        end
        item.Condition = 101
        rangedWeapon.WeaponDamageModifier = 1
    end
end)

Hook.Add("Touhou_Monorail_update", "Touhou_Monorail_update", function(effect, deltaTime, item, targets, worldPosition)
    -- item.Condition <= 100 here
    local rangedWeapon = targets[2]
    local user = targets[3]
    if item.Condition == 0 then return end -- poor bastard exploded
    if user == nil or not isHolding(user, item) or rangedWeapon.ReloadTimer > 0 then
        item.Condition = 101
    end
end)