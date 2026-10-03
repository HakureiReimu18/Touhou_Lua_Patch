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



Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance)
    local character = instance

    if not character then return end
    if GUI.GUI.PauseMenuOpen then return end
    if GUI.KeyboardDispatcher.Subscriber then return end
    --[[slottype中2代表右手，4代表左手]]
    local rmb_held = PlayerInput.SecondaryMouseButtonHeld()
    local selected_item = character.SelectedItem
    if rmb_held
            and (character.HasEquippedItem("Touhou_Cam_Offset_Low",true,2) or character.HasEquippedItem("Touhou_Cam_Offset_Low",true,4))
            and not selected_item then
        Screen.Selected.Cam.OffsetAmount = math.min(Lerp(Screen.Selected.Cam.OffsetAmount, 0, 0.2)
                * (1 + math.min( GetSkillLevel(character) * 0.001, 0.15)), 300)
    end
    if rmb_held
            and (character.HasEquippedItem("Touhou_Cam_Offset_Normal",true,2) or character.HasEquippedItem("Touhou_Cam_Offset_Normal",true,4))
            and not selected_item then
        Screen.Selected.Cam.OffsetAmount = math.min(Lerp(Screen.Selected.Cam.OffsetAmount, 0, 0.35)
                * (1 + math.min( GetSkillLevel(character) * 0.001, 0.15)), 380)
    end
    if rmb_held
            and (character.HasEquippedItem("Touhou_Cam_Offset_High",true,2) or character.HasEquippedItem("Touhou_Cam_Offset_High",true,4))
            and not selected_item then
        Screen.Selected.Cam.OffsetAmount = math.min(Lerp(Screen.Selected.Cam.OffsetAmount, 0, 0.48)
                * (1 + math.min( GetSkillLevel(character) * 0.001, 0.15)), 440)
    end
    if rmb_held
            and (character.HasEquippedItem("Touhou_Cam_Offset_Sniper",true,2) or character.HasEquippedItem("Touhou_Cam_Offset_Sniper",true,4))
            and not selected_item then
        Screen.Selected.Cam.OffsetAmount = math.min(Lerp(Screen.Selected.Cam.OffsetAmount, 0, 0.64)
                * (1 + math.min( GetSkillLevel(character) * 0.001, 0.15)), 520)
    end
end, Hook.HookMethodType.After)
