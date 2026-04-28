local recipes = {
	--[[
		test 函数，返回为true时代表满足制作配方。
		priority 优先级，默认为0。多个配方满足制作条件时生成优先级最高的食物，优先级相同则随机生成。
		requiredtime 制作时间，默认为1。该配方制作需要的时间，单位为秒。
		amount 生成数量，默认为1。该配方一次生成多少数量的食物。
	--]]

	--[[
	proteinbar = {
		test = function(cooker, identifiers, tags)
			return true
		end,
		priority = -2,
		requiredtime = 1,
		amount = 1,
	},
	--]]
	Touhou_Seafood_Miso_Soup = {
		test = function(cooker, identifiers, tags)
			return identifiers.touhou_seaweed and tags.filling
		end,
		priority = 1,
		requiredtime = 10,
		amount = 2,
	},
	Touhou_Rice_Ball = {
		test = function(cooker, identifiers, tags)
			return identifiers.touhou_seaweed and tags.filling
		end,
		priority = 1,
		requiredtime = 1,
		amount = 2,
	},
	Touhou_Pork_Stir_Fry = {
		test = function(cooker, identifiers, tags)
			return identifiers.touhou_pork and tags.homecooking
		end,
		priority = 1,
		requiredtime = 1,
		amount = 2,
	},
	Touhou_Potato_Croquettes = {
		test = function(cooker, identifiers, tags)
			return identifiers.touhou_potato and tags.salty
		end,
		priority = 1,
		requiredtime = 1,
		amount = 2,
	},
}

local function FindLastUser(cookpot)
	local minDistance
    local distance
    local closestCharacter

	for _, character in pairs(Character.CharacterList) do
		if character.SelectedItem == cookpot and character.IsPlayer then
            distance = Vector2.Distance(character.WorldPosition, cookpot.WorldPosition)

			if minDistance == nil then
                minDistance = distance
                closestCharacter = character
            elseif distance < minDistance then
                minDistance = distance
                closestCharacter = character
            end
		end
	end

    return closestCharacter
end

local function ParseIdentifiers(cookpot)
	local identifiers = {}

	for _, item in pairs(cookpot.OwnInventory.AllItemsMod) do
		local identifier = string.lower(item.Prefab.Identifier.Value)

		if identifiers[identifier] == nil then
			identifiers[identifier] = 1
		else
			identifiers[identifier] = identifiers[identifier] + 1
		end
	end

	return identifiers
end

local function ParseTags(cookpot)
	local tags = {}

	for _, item in pairs(cookpot.OwnInventory.AllItemsMod) do
		for tag in item.GetTags() do
			local tagName = tag.Value
			local tagValue = 1

			local splitPos = string.find(tag.Value, ":")
			if splitPos then
				tagName = string.sub(tag.Value, 1, splitPos-1)
				tagValue = tonumber(string.sub(tag.Value, splitPos + 1)) or 1
				if item.HasTag(tagName) then
					tagValue = tagValue - 1
				end
			end
			tagName = string.lower(tagName)

			if tags[tagName] == nil then
				tags[tagName] = tagValue
			else
				tags[tagName] = tags[tagName] + tagValue
			end
		end
	end

	return tags
end

local function GetValidRecipes(cookpot, cooker)
	local validRecipes = {}
	local identifiers = ParseIdentifiers(cookpot)
	local tags = ParseTags(cookpot)

	for identifier, recipe in pairs(recipes) do
		if recipe.test(cooker, identifiers, tags) then
			validRecipes[identifier] = recipe.priority or 0
		end
	end

	return validRecipes
end

local function GetTopPriorityRecipe(validRecipes)
	local candidateRecipes = {}
	local topPriority

	for identifier, priority in pairs(validRecipes) do
		if topPriority == nil then
			topPriority = priority
			candidateRecipes = {identifier}
		elseif priority > topPriority then
			topPriority = priority
			candidateRecipes = {identifier}
		elseif priority == topPriority then
			table.insert(candidateRecipes, identifier)
		end
	end

	return candidateRecipes[math.random(#candidateRecipes)]
end

local function SendMessageBox(sendername, text, character)
	if SERVER then
		Game.SendDirectChatMessage(sendername, text, nil, ChatMessageType.MessageBox, Util.FindClientCharacter(character))
	else
		GUI.MessageBox(sendername, text)
	end
end

Hook.Add("Touhou.Cooktop.start", function(effect, deltaTime, item, targets, worldPosition)
	local cooker = FindLastUser(item)

	if item.GetComponentString("LightComponent").IsOn then
		Hook.Call("Touhou.Cooktop.cancel", {item, cooker})
		return
	end

	local recipe = GetTopPriorityRecipe(GetValidRecipes(item, cooker))

	local errors = {}

	if item.OwnInventory.EmptySlotCount <= 0 then
		table.insert(errors, TextManager.Get("touhou.cooktop.noavailableingredients").Value)
	elseif recipe == nil then
		table.insert(errors, TextManager.Get("touhou.cooktop.noavailablerecipes").Value)
	end

	if #errors > 0 then
		local errorMessage = ""
		for _, v in pairs(errors) do
			errorMessage = errorMessage .. v .. "\n"
		end

		SendMessageBox(TextManager.Get("error").Value, errorMessage, cooker)
		return
	end

	item.GetComponentString("MemoryComponent").Value = recipe
	item.GetComponentString("PowerContainer").Charge = recipes[recipe].requiredtime or 1
	item.GetComponentString("LightComponent").IsOn = true

	item.OwnInventory.Locked = true
	item.GetComponentString("CustomInterface").Labels = TextManager.Get("fabricatorcancel").Value
end)

Hook.Add("Touhou.Cooktop.cancel", function(parameters)
	local cookpot = parameters[1]
	local cooker = parameters[2]

	cookpot.GetComponentString("MemoryComponent").Value = ""
	cookpot.GetComponentString("PowerContainer").Charge = 0
end)

Hook.Add("Touhou.Cooktop.end", function(effect, deltaTime, item, targets, worldPosition)
	item.GetComponentString("CustomInterface").Labels = TextManager.Get("touhou.cooktop.cook").Value
	item.OwnInventory.Locked = false

	local recipe = item.GetComponentString("MemoryComponent").Value
	item.GetComponentString("MemoryComponent").Value = ""
	if recipe == "" then return end

	for _, containedItem in pairs(item.OwnInventory.AllItemsMod) do
		Entity.Spawner.AddItemToRemoveQueue(containedItem)
	end
	for i = 1, recipes[recipe].amount or 1, 1 do
		Entity.Spawner.AddItemToSpawnQueue(ItemPrefab.Prefabs[recipe], item.OwnInventory)
	end
end)

Hook.Add("Touhou.Cooktop.analyze", function(effect, deltaTime, item, targets, worldPosition)
	local user = FindLastUser(item)
	local errors = {}

	if item.OwnInventory.EmptySlotCount <= 0 then
		table.insert(errors, TextManager.Get("touhou.cooktop.noavailableingredients").Value)
	end
	if item.GetComponentString("LightComponent").IsOn then
		table.insert(errors, TextManager.Get("touhou.cooktop.isactive").Value)
	end

	if #errors > 0 then
		local errorMessage = ""
		for _, v in pairs(errors) do
			errorMessage = errorMessage .. v .. "\n"
		end

		SendMessageBox(TextManager.Get("error").Value, errorMessage, user)
		return
	end

	local tagInfo = ""
	local tags = ParseTags(item)
	for tag, value in pairs(tags) do
		if TextManager.Get("touhou.foodtag." .. tag).Value ~= "" then
			tagInfo = tagInfo .. "- " .. TextManager.Get("touhou.foodtag." .. tag).Value .. " x" .. value .. "\n"
		end
	end
	if tagInfo == "" then
		tagInfo = "- " .. TextManager.Get("none").Value
	end
	tagInfo = TextManager.Get("touhou.cooktop.analyze.tooltip").Value .. "\n" .. tagInfo

	SendMessageBox(item.Name, tagInfo, user)
end)