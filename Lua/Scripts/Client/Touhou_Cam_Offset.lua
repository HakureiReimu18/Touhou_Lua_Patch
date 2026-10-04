-- 嘻哈世界，安静睡觉

--[[你说你不想在这里，我也不想在这里]]
if not CLIENT then
    return
end

local function Lerp(DefaultScreen, Num , OffsetNum)  --线性插值函数(改了个符号方便认)
    return DefaultScreen * (1+ OffsetNum) + Num * OffsetNum
end

local MAGIC_SKILL_ID = Identifier("Touhou_Magic")

local function GetSkillLevel(character)
    return character.GetSkillLevel(MAGIC_SKILL_ID)
end



-- 四档镜头偏移：右/左手判定（2=右手，4=左手）与数值照旧，按顺序逐档结算，后档覆盖前档
local CAM_OFFSET_TIERS = {
    { identifier = "Touhou_Cam_Offset_Low",    offset = 0.2,  cap = 300 },
    { identifier = "Touhou_Cam_Offset_Normal", offset = 0.35, cap = 380 },
    { identifier = "Touhou_Cam_Offset_High",   offset = 0.48, cap = 440 },
    { identifier = "Touhou_Cam_Offset_Sniper", offset = 0.64, cap = 520 },
}

Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance)
    local character = instance

    if not character then return end
    if GUI.GUI.PauseMenuOpen then return end
    if GUI.KeyboardDispatcher.Subscriber then return end
    if Screen.Selected == nil then return end
    --[[slottype中2代表右手，4代表左手]]
    local rmb_held = PlayerInput.SecondaryMouseButtonHeld()
    local selected_item = character.SelectedItem
    if not rmb_held or selected_item then return end

    local cam = Screen.Selected.Cam
    local skill_bonus = nil -- 一帧只算一次，第一次命中档位时才取
    for _, tier in ipairs(CAM_OFFSET_TIERS) do
        if character.HasEquippedItem(tier.identifier, true, 2) or character.HasEquippedItem(tier.identifier, true, 4) then
            if skill_bonus == nil then
                skill_bonus = 1 + math.min(GetSkillLevel(character) * 0.001, 0.15)
            end
            cam.OffsetAmount = math.min(Lerp(cam.OffsetAmount, 0, tier.offset) * skill_bonus, tier.cap)
        end
    end
end, Hook.HookMethodType.After)
