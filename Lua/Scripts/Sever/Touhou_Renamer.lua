local renamedItems = {}

Hook.Patch("Barotrauma.Item", "get_Name", function(instance, ptable)
    if renamedItems[instance] then
        ptable.PreventExecution = true
        return renamedItems[instance]
    end
end)

Hook.Add("Touhou_Renamer_Rename", "Touhou_Renamer_Rename", function(effect, deltaTime, item, targets, worldPosition)
    local containedItem = item.OwnInventory.GetItemAt(0)
    if not containedItem then return end
    local lightColor = item.GetComponentString("LightComponent").LightColor
    local name = item.GetComponentString("CustomInterface").customInterfaceElementList[1].Signal
    if lightColor == Color(255, 255, 255, 255) then
        renamedItems[containedItem] = name
    else
        renamedItems[containedItem] = string.format("‖color:%d,%d,%d,%d‖%s‖color:end‖", lightColor.R, lightColor.G, lightColor.B, lightColor.A, name)
    end
end)

Hook.Add("Touhou_Renamer_Resetname", "Touhou_Renamer_Resetname", function(effect, deltaTime, item, targets, worldPosition)
    local containedItem = item.OwnInventory.GetItemAt(0)
    if not containedItem then return end
    renamedItems[containedItem] = nil
end)
