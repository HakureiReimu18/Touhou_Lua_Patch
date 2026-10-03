-- 嘻嘻嘻哈哈哈

local AFFLICTION_TIERS = {
    { threshold = 150, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier5", strength = 1 },
    { threshold = 120, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier4", strength = 1 },
    { threshold = 90, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier3", strength = 1 },
    { threshold = 50,  identifier = "Touhou_Zero_Moment_Pendant_Hook_tier2", strength = 1 },
    { threshold = 0,   identifier = "Touhou_Zero_Moment_Pendant_Hook_tier1", strength = 1 },
}

local function get_application_limb(character)
    if character == nil or character.AnimController == nil then
        return nil
    end

    local limb = character.AnimController.MainLimb
    if limb ~= nil then
        return limb
    end

    return character.AnimController.GetLimb(LimbType.Torso)
end

local function choose_tier(item_count)
    for _, tier in ipairs(AFFLICTION_TIERS) do
        if item_count >= tier.threshold then
            return tier
        end
    end
    return nil
end

Hook.Add("Touhou_Zero_Moment_Pendant_Hook", "Touhou_Zero_Moment_Pendant_Hook",
    function(effect, deltaTime, item, targets, worldPosition)
        local character = targets[1]
        if character == nil or character.Inventory == nil or character.IsDead or character.Removed then
            return
        end
--[[    print("42行OK")]]
        local items = character.Inventory.FindAllItems(nil, true)
        local count = 0
        for found_item in items do
            if not found_item.Prefab.HideInMenus then
                count = count + 1
            end
        end
--[[    print("统计数量OK")]]

        local tier = choose_tier(count)
        if tier == nil then
            return
        end

        local prefab = AfflictionPrefab.Prefabs[tier.identifier]
        local health = character.CharacterHealth
        local limb = get_application_limb(character)
        if prefab == nil or health == nil or limb == nil then
            return
        end
--[[    print("选择档位OK")]]
        -- 持续时间在Aff自己身上定义
        local affliction = prefab.Instantiate(tier.strength or 1)
        health.ApplyAffliction(limb, affliction)
        -- 耐久掉光就清掉所有档位
        if item == nil or item.Condition <= 0 then
            local health = character.CharacterHealth
            local limb = get_application_limb(character)
            if health == nil or limb == nil then
                return
            end
--[[                print("检查耐久OK")]]
            -- 0强度直接盖上去清掉，不然旧档位的加成会残留
            for _, tier in ipairs(AFFLICTION_TIERS) do
                local prefab = AfflictionPrefab.Prefabs[tier.identifier]
                if prefab ~= nil then
                    local removal = prefab.Instantiate(0)
                    removal.Strength = 0
                    health.ApplyAffliction(limb, removal)
                end
            end
            return
--[[                print("清除AffOK")]]
        end
    end)
