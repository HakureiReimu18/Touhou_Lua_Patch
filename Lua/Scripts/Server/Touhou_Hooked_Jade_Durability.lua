-- 你说你不想在这里

--[[
    受击扣勾玉耐久，LuaCS版。代替原XML那套（OnDamaged → 过渡affliction → condition阶梯扣Condition），
    现在直接在 character.applyDamage 钩子里算完就扣。
    只跑服务端：耐久是服务端说了算，客户端别加载这个脚本。
]]

local JadeDurability = {}
TLE.JadeDurability = JadeDurability

-- 潜水服都穿在 OuterClothes
local SUIT_SLOT = InvSlotType.OuterClothes

-- 带这tag的就是模组潜水服
local SUIT_TAG = "Touhou_Divinggear"

local JADE_IDENTIFIER_SUFFIX = "_Hooked_Jade"

-- 各装备的勾玉槽索引（按 XML 里 SubContainer 声明顺序数）。只扣这些槽，塞别的储物槽里的勾玉不管。
-- 新装备有勾玉槽必须来这儿登记，不然不扣（调试日志里会有提示）。
local JADE_SLOTS = {
    gensokyo_divinggear                = {2, 3, 4, 5},
    Touhou_Boudary_Divinggear          = {1, 2, 3},
    Ether_Divinggear                   = {1, 2},
    Magic_Veil                         = {0, 1, 2, 3, 4, 5},
    Touhou_Dichromatic_Lotus_Butterfly = {1, 2, 3, 4, 5},
    Touhou_Hyouibana_Armor_Basic       = {2},
    Touhou_Hyouibana_Armor_Alchemical  = {2, 3},
    Touhou_Hyouibana_Armor01           = {2, 3, 4, 5},
    Touhou_Astral_Veil                 = {1, 2, 3, 4, 5},
    Touhou_Bear_Doll_Bag               = {0},
}

-- 伤害→耐久阶梯，取命中的最高档不叠加。原XML四档各触各的，48伤害实际会扣 3+10+15+35=63；
-- 想复刻那种叠加就把 TIER_MODE 改成 "stack"。
JadeDurability.TIER_MODE = "highest" -- "highest" | "stack"
JadeDurability.TIERS = {
    { minDamage = 3,  loss = 0.03  },
    { minDamage = 6,  loss = 0.1 },
    { minDamage = 18, loss = 0.15 },
    { minDamage = 48, loss = 0.35 },
}

-- 全局磨损系数，调平衡用的
JadeDurability.LOSS_MULTIPLIER = 1.0

-- 调试日志，验证完记得关
JadeDurability.DEBUG = false

local function DebugPrint(msg)
    if JadeDurability.DEBUG then
        print("[勾玉耐久] " .. msg)
    end
end

-- 算进耐久的伤害类型（跟原XML的RequiredAffliction对齐）
local COUNTED_AFFLICTION_TYPES = {
    damage = true,
    burn = true,
    bleeding = true,
}

-- 这次攻击该扣多少
local function CalcDurabilityLoss(totalDamage)
    local loss = 0
    if JadeDurability.TIER_MODE == "stack" then
        for _, tier in ipairs(JadeDurability.TIERS) do
            if totalDamage >= tier.minDamage then
                loss = loss + tier.loss
            end
        end
    else
        for _, tier in ipairs(JadeDurability.TIERS) do
            if totalDamage >= tier.minDamage and tier.loss > loss then
                loss = tier.loss
            end
        end
    end
    return loss * JadeDurability.LOSS_MULTIPLIER
end

-- 把登记槽里的勾玉挨个扣一遍
local function DrainJades(suit, loss)
    local container = suit.OwnInventory
    if container == nil then
        DebugPrint("警告：护甲 " .. tostring(suit.Prefab.Identifier) .. " 没有 OwnInventory，无法扣耐久")
        return
    end

    local suitId = tostring(suit.Prefab.Identifier)
    local jadeSlots = JADE_SLOTS[suitId]
    if jadeSlots == nil then
        DebugPrint("警告：护甲 " .. suitId .. " 未在 JADE_SLOTS 登记勾玉槽位，本次不扣耐久（请在脚本中补充）")
        return
    end

    local drained = 0
    for _, slot in ipairs(jadeSlots) do
        if slot < container.Capacity then
            local jade = container.GetItemAt(slot)
            if jade ~= nil then
                -- Identifier 有的版本是string有的是结构体，统一tostring
                local id = tostring(jade.Prefab.Identifier)
                if string.sub(id, -#JADE_IDENTIFIER_SUFFIX) == JADE_IDENTIFIER_SUFFIX then
                    local before = jade.Condition
                    jade.Condition = math.max(jade.Condition - loss, 0)
                    drained = drained + 1
                    DebugPrint(string.format("槽位 %d 勾玉 %s：耐久 %.1f → %.1f", slot, id, before, jade.Condition))
                    -- 归零后的损坏交给游戏自己处理，跟原XML一样
                else
                    DebugPrint(string.format("勾玉槽位 %d 里的物品 %s 不是勾玉，跳过", slot, id))
                end
            end
        else
            DebugPrint(string.format("警告：登记的勾玉槽位 %d 超出护甲 %s 的容器容量 %d", slot, suitId, container.Capacity))
        end
    end
    if drained == 0 then
        DebugPrint("勾玉槽内没有勾玉，本次不扣耐久")
    end
end

Hook.Add("character.applyDamage", "Touhou_JadeDurability.OnDamage", function(characterHealth, attackResult, hitLimb)
    if characterHealth == nil or attackResult == nil or attackResult.Afflictions == nil then return end

    local character = characterHealth.Character
    if character == nil or character.IsDead or character.Inventory == nil then return end

    local suit = character.Inventory.GetItemInLimbSlot(SUIT_SLOT)
    if suit == nil or not suit.HasTag(SUIT_TAG) then return end

    local totalDamage = 0
    local detail = nil
    if JadeDurability.DEBUG then
        detail = {}
    end
    for affliction in attackResult.Afflictions do
        local affType = tostring(affliction.Prefab.AfflictionType)
        if COUNTED_AFFLICTION_TYPES[affType] then
            totalDamage = totalDamage + affliction.Strength
            if detail ~= nil then
                table.insert(detail, affType .. "=" .. string.format("%.1f", affliction.Strength))
            end
        else
            DebugPrint(string.format("%s 的 affliction 类型 %s 不计入（强度 %.1f）",
                character.Name, affType, affliction.Strength))
        end
    end

    local loss = CalcDurabilityLoss(totalDamage)
    if loss <= 0 then
        if detail ~= nil then
            DebugPrint(string.format("%s 受击，计入伤害 %.1f（%s），低于阶梯阈值，不扣耐久",
                character.Name, totalDamage, table.concat(detail, ", ")))
        end
        return
    end

    if detail ~= nil then
        DebugPrint(string.format("%s 受击，计入伤害 %.1f（%s）→ 扣耐久 %.1f（模式 %s）",
            character.Name, totalDamage, table.concat(detail, ", "), loss, JadeDurability.TIER_MODE))
    end
    DrainJades(suit, loss)
end)

DebugPrint("脚本已加载")
