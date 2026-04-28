-- 定义不同背包物品数量档位对应的Affliction配置
-- threshold表示达到该数量下限时生效，identifier是Affliction名称，strength为施加强度
local AFFLICTION_TIERS = {
    { threshold = 150, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier5", strength = 1 },
    { threshold = 120, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier4", strength = 1 },
    { threshold = 90, identifier = "Touhou_Zero_Moment_Pendant_Hook_tier3", strength = 1 },
    { threshold = 50,  identifier = "Touhou_Zero_Moment_Pendant_Hook_tier2", strength = 1 },
    { threshold = 0,   identifier = "Touhou_Zero_Moment_Pendant_Hook_tier1", strength = 1 },
}

-- 找到角色的主肢体（优先）或躯干，供Affliction应用使用
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

-- 根据物品数量选择应当施加的Affliction配置
local function choose_tier(item_count)
    for _, tier in ipairs(AFFLICTION_TIERS) do
        if item_count >= tier.threshold then
            return tier
        end
    end
    return nil
end

-- 钩子函数：每次触发时重新统计物品数量并施加对应的Affliction
Hook.Add("Touhou_Zero_Moment_Pendant_Hook", "Touhou_Zero_Moment_Pendant_Hook",
    function(effect, deltaTime, item, targets, worldPosition)
        local character = targets[1]
        if character == nil or character.Inventory == nil or character.IsDead or character.Removed then
            return
        end
--[[    print("42行OK")]]
        -- 统计背包中非隐藏物品数量
        local items = character.Inventory.FindAllItems(nil, true)
        local count = 0
        for found_item in items do
            if not found_item.Prefab.HideInMenus then
                count = count + 1
            end
        end
--[[    print("统计数量OK")]]

        -- 根据数量选择对应档位
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
        -- 施加档位对应的Affliction，持续时间可在Aff自身定义
        local affliction = prefab.Instantiate(tier.strength or 1)
        health.ApplyAffliction(limb, affliction)
        -- 检查项链耐久；耐久降到 0 时清空所有档位并停止生效
        if item == nil or item.Condition <= 0 then
            local health = character.CharacterHealth
            local limb = get_application_limb(character)
            if health == nil or limb == nil then
                return
            end
--[[                print("检查耐久OK")]]
            -- 为避免残留其它档位的影响，给其余档位施加0强度的Affliction以立即清除
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
