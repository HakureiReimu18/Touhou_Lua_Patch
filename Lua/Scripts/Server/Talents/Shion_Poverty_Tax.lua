-- 依神紫苑天赋「让一切徒劳无功的贫穷神」：每个巡回损失 50mk。
-- 原 XML 用的是 <CharacterAbilityGiveMoney amount="-50"/>，但 Character.GiveMoney 里有 `amount > 0` 的闸门
-- （Barotrauma 源码：if (campaignMode != null && amount > 0)），负数会被静默丢弃，所以那条从来没扣过钱。
-- 这里改到服务端直接扣：联机扣个人钱包（Wallet.Deduct），单机退回战役银行。

local TAX = 50

-- 装束给的 affliction：HasTalent 是永久解锁的，判定不了当前是否穿着装束，所以以它为准
local OUTFIT_AFFLICTION = "Touhou_Shion_Character_Effect"

-- 有的版本 HasTalent 只吃 Identifier、有的直接吃字符串，两种都喂
local TALENT_IDENTIFIER = Identifier("ShionTalent")
local function has_shion_talent(character)
    local ok, result = pcall(function() return character.HasTalent(TALENT_IDENTIFIER) end)
    if ok and result then return true end
    local ok2, result2 = pcall(function() return character.HasTalent("ShionTalent") end)
    return ok2 and result2 == true
end

local function wears_shion_outfit(character)
    local health = character.CharacterHealth
    if health == nil then return false end
    return health.GetAffliction(OUTFIT_AFFLICTION) ~= nil
end

-- 扣款：联机走个人钱包；单人模式访问 character.Wallet 会抛异常，退回战役银行（接口名不确定，逐个试）
local function deduct(character, amount)
    local ok = pcall(function() character.Wallet.Deduct(amount) end)
    if ok then return true end

    local bank = nil
    pcall(function() bank = Game.GameSession.GameMode.Bank end)
    if bank == nil then return false end

    if pcall(function() bank.Deduct(amount) end) then return true end
    if pcall(function() bank.TryDeduct(amount) end) then return true end
    if pcall(function() bank.Give(-amount) end) then return true end
    return false
end

Hook.Add("roundEnd", "Touhou.Shion.PovertyTax", function()
    for character in Character.CharacterList do
        if character ~= nil and not character.IsDead
            and has_shion_talent(character) and wears_shion_outfit(character) then
            deduct(character, TAX)
            return
        end
    end
end)
