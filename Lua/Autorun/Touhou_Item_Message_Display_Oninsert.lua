-- 消息映射表
local ITEM_MESSAGES = {
    ["Hompson_Contender"] = "Hompson_Contender.Message",
    ["Touhou_Winchester"] = "Touhou_Winchester.Message",
    ["Fire_Element_Book"] = "Fire_Element_Book.Message",
    ["Water_Element_Book"] = "Water_Element_Book.Message",
    ["Iron_Element_Book"] = "Iron_Element_Book.Message",
    ["Earth_Element_Book"] = "Earth_Element_Book.Message",
}

Hook.Add("Touhou_Item_Message_Display_Oninsert", "Touhou_Item_Message_Display_Oninsert", function(effect, deltaTime, item, targets, worldPosition)
    local containedItem = item.OwnInventory.GetItemAt(0)
    if not containedItem then return end

    local itemId = containedItem.Prefab.Identifier.Value

    -- 从表中获取对应的消息
    local messageKey = ITEM_MESSAGES[itemId] or "Touhou_Item_Message_Display.Message"

    local senderName = TextManager.Get("Touhou_Item_Message_Display.Sendername")
    local message = TextManager.Get(messageKey)

    if string.find(message, "%%s") then
        message = string.format(message, containedItem.Name)
    end

    -- 发送消息
    if not Game.IsMultiplayer then
        if Game.GameSession and Game.GameSession.CrewManager then
            Game.GameSession.CrewManager.AddSinglePlayerChatMessage(senderName, message, ChatMessageType.Radio, nil)
        end
    elseif SERVER then
        if Game.Server then
            local chatMessage = ChatMessage.Create(senderName, message, ChatMessageType.Radio, nil)
            for client in Client.ClientList do
                Game.Server.SendDirectChatMessage(chatMessage, client)
            end
        end
    end
end)