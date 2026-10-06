--[[
	自由搭配料理系统
	风味 tag 和 Cooktop02 配方 XML 里本来就有；启动时给每道菜建个"风味画像"，
	烹饪时按锅内食材打分，过阈值出那道菜，不够就出炼金副产物。
	新增料理只用写 XML，这里不用动。
]]

-- 正在享受人生

if CLIENT and not Game.IsSingleplayer then return end -- 服务端权威

--[[你是穿越时间的蠕虫。雷鸣之歌扭曲了你的体态。幸福降临。白若珍珠，但眼里藏着黄红般的混沌。透过镜子，倒影终将被纠正。把你的内在留在门口。手指透过表面沁入湿润。你一直是全新的自己。你希望这是真的。当你做梦时，我们就站在你身边。你能听到我们的谈话，但终将忘记。现在这种情况发生得越来越频繁。你在你的规则中赋予我们权限。我们在污渍中等待。而这种情况的措辞已经经过了修改。重复那个词。那声音的名字。它在你家里引起共振。歌声落，掌声响。我们毫无保留地打造你。真相破壳而出。你回家了。你提醒了我们该回家了。你带着你的老大一起来了。必须吃掉所有头发。在现实背后的意念世界中，你肯定希望这意识流将你卷走。歌声落，掌声响。这个套路就是死亡时间：打破第一面、第二面、第三面、第四面墙，然后是第五面墙，接着是地板；地板没了：你就只能掉下去了！你怎么描述发狂的？苦中作乐。耳虫曲是一种你在梦中也会吟唱的歌曲：“宝贝宝贝宝贝耶”。只是塑料。非常安全，不用担心。哈哈，真有趣。现在最后一个蛋也破了。房间里的洞其实是你内心的通道。既然来了我就会帮你穿过内心的空洞。其实你一直在这里，命定之子。副本的副本。橘子皮。这幅图上是你拿着图片的样子。当你听到这句话时，你就知道你又是全新的自己了。你想聆听。你想做梦。你想微笑。你想受伤。你不想成为这样。]]

-- 调平衡就改这里
local Config = {
	Threshold = 3.0,               -- 最高分低于这个就出兜底货
	W_flavor = 1.0,
	W_core = 3.0,
	B_full = 2.0,
	P_overflow = 0.5,
	P_foreign = 0.25,
	FallbackItem = "Byproduct_Alchemical", -- 炼金术副产物兜底
	FallbackAmount = 1,
	FallbackRequiredTime = 10,
	DefaultRequiredTime = 10,      -- 配方没写烹饪时间就用这个
	SourceFabricator = "touhou_cooktop02", -- 画像硬条件从哪个配方读
}

-- 风味tag白名单，2026-07-29 从模组XML统计的；带 touhou.foodtag.* 本地化的 tag 启动时会自动收进来
local FlavorTags = {
	aquatic = true, auto_bursting = true, chinese = true, cultural_heritage = true,
	divine_punishment = true, dreamy = true, economical = true, expensive = true,
	filling = true, fresh = true, fruity = true, fungus = true,
	good_w_alcohol = true, greasy = true, grilled = true, homecooking = true,
	hot = true, japanese = true, large_portion = true, legendary = true,
	meat = true, mild = true, mountain_delicacy = true, peculiar = true,
	photogenic = true, poison = true, premium = true, raw = true,
	refreshing = true, salty = true, sea_delicacy = true, signature = true,
	small_portion = true, soup = true, sour = true, specialty = true,
	spicy = true, strength_boosting = true, sweet = true, vegetarian = true,
	western = true, wonderful = true,
}

-- 约定：有 touhou.foodtag.* 本地化的就算风味tag
local function IsFlavorTag(tagName)
	if FlavorTags[tagName] then return true end
	if TextManager.Get("touhou.foodtag." .. tagName).Value ~= "" then
		FlavorTags[tagName] = true
		return true
	end
	return false
end

local profiles = {}


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

local function IsPotEmpty(cookpot)
	for _ in pairs(cookpot.OwnInventory.AllItemsMod) do
		return false
	end
	return true
end

local function SendMessageBox(sendername, text, character)
	if SERVER then
		Game.SendDirectChatMessage(sendername, text, nil, ChatMessageType.MessageBox, Util.FindClientCharacter(character))
	else
		GUI.MessageBox(sendername, text)
	end
end

local function GetItemDisplayName(identifier)
	local name = TextManager.Get("entityname." .. string.lower(identifier)).Value
	if name == "" then
		local ok, prefabName = pcall(function() return ItemPrefab.Prefabs[identifier].Name.Value end)
		if ok and prefabName ~= nil and prefabName ~= "" then
			name = prefabName
		else
			name = identifier
		end
	end
	return name
end

local function ParseTagValue(tagStr)
	local splitPos = string.find(tagStr, ":")
	if splitPos then
		return string.sub(tagStr, 1, splitPos - 1), tonumber(string.sub(tagStr, splitPos + 1)) or 1
	end
	return tagStr, 1
end

-- prefab.Tags 这API不一定有，读不到就当没tag
local function CollectPrefabTags(prefab)
	local list = {}
	local ok = pcall(function()
		for tag in prefab.Tags do
			table.insert(list, string.lower(tostring(tag)))
		end
	end)
	if ok then return list end
	return {}
end

-- 从 Cooktop02 配方读硬条件。FabricationRecipes 的 API 形态没准，全用 pcall 包着；
-- 全读挂了的话启动日志会报警告，硬条件只能靠 core:/coretag: 前缀 tag 顶上
local function ReadCooktop02Recipe(prefab)
	local result = nil

	pcall(function()
		local recipes = prefab.FabricationRecipes
		if recipes == nil then return end

		for _, entry in pairs(recipes) do
			local recipe = entry
			-- 枚举 Dictionary 拿到的是 KeyValuePair，要取 .Value
			local okV, v = pcall(function() return entry.Value end)
			if okV and v ~= nil then recipe = v end

			-- SuitableFabricators 里得有 cooktop02；这属性读不到就当匹配
			local suitable = true
			local okS, fabricators = pcall(function() return recipe.SuitableFabricators end)
			if okS and fabricators ~= nil then
				suitable = false
				for fabId in fabricators do
					if string.lower(tostring(fabId)) == Config.SourceFabricator then
						suitable = true
						break
					end
				end
			end
			if not suitable then goto continue end

			local data = { required_items = {}, required_skills = {}, requiredtime = nil, amount = nil }

			pcall(function() data.requiredtime = tonumber(recipe.RequiredTime) end)
			pcall(function() data.amount = tonumber(recipe.Amount) end)

			local okI, requiredItems = pcall(function() return recipe.RequiredItems end)
			if okI and requiredItems ~= nil then
				for _, req in pairs(requiredItems) do
					local entry2 = { amount = 1 }
					pcall(function()
						if req.Amount ~= nil then entry2.amount = tonumber(req.Amount) or 1 end
					end)
					local idStr, tagStr
					pcall(function() idStr = tostring(req.ItemIdentifier) end)
					pcall(function() tagStr = tostring(req.Tag) end)
					if idStr ~= nil and idStr ~= "" then
						entry2.identifier = string.lower(idStr)
					end
					if tagStr ~= nil and tagStr ~= "" then
						entry2.tag = string.lower(tagStr)
					end
					if entry2.identifier ~= nil or entry2.tag ~= nil then
						table.insert(data.required_items, entry2)
					end
				end
			end

			local okK, requiredSkills = pcall(function() return recipe.RequiredSkills end)
			if okK and requiredSkills ~= nil then
				for _, skill in pairs(requiredSkills) do
					local s = {}
					pcall(function() s.identifier = string.lower(tostring(skill.Identifier)) end)
					pcall(function() s.level = tonumber(skill.Level) or 0 end)
					if s.identifier ~= nil then
						table.insert(data.required_skills, s)
					end
				end
			end

			result = data
			break -- 每道菜只取第一个 Cooktop02 配方
			::continue::
		end
	end)

	return result
end

-- 启动时跑一次，把画像表建起来
local function BuildProfiles()
	local scanned, built, recipeApiOk = 0, 0, 0

	for _, entry in pairs(ItemPrefab.Prefabs) do
		local prefab = entry
		local okV, v = pcall(function() return entry.Value end)
		if okV and v ~= nil then prefab = v end

		local okId, identifier = pcall(function() return prefab.Identifier.Value end)
		if okId and identifier ~= nil then
			local prefabTags = CollectPrefabTags(prefab)

			local isCuisine = false
			for _, tagStr in ipairs(prefabTags) do
				if tagStr == "touhou_cuisine" then
					isCuisine = true
					break
				end
			end

			if isCuisine then
				scanned = scanned + 1

				local profile = {
					flavor = {},
					core_items = {},
					core_tags = {},
					required_skills = {},
					requiredtime = nil,
					amount = nil,
					original_identifier = identifier,
				}

				-- 结构性 tag 不进画像；core:/coretag: 前缀的是降级硬条件
				for _, tagStr in ipairs(prefabTags) do
					local name, value = ParseTagValue(tagStr)
					if string.sub(name, 1, 5) == "core:" then
						local item = string.sub(name, 6)
						profile.core_items[item] = (profile.core_items[item] or 0) + value
					elseif string.sub(name, 1, 8) == "coretag:" then
						local tag = string.sub(name, 9)
						profile.core_tags[tag] = (profile.core_tags[tag] or 0) + value
					elseif IsFlavorTag(name) then
						profile.flavor[name] = (profile.flavor[name] or 0) + value
					end
				end

				local recipeData = ReadCooktop02Recipe(prefab)
				if recipeData ~= nil then
					recipeApiOk = recipeApiOk + 1
					for _, req in ipairs(recipeData.required_items) do
						if req.identifier ~= nil then
							profile.core_items[req.identifier] = math.max(profile.core_items[req.identifier] or 0, req.amount)
						end
						if req.tag ~= nil then
							profile.core_tags[req.tag] = math.max(profile.core_tags[req.tag] or 0, req.amount)
						end
					end
					profile.required_skills = recipeData.required_skills
					profile.requiredtime = recipeData.requiredtime
					profile.amount = recipeData.amount
				end

				-- 风味和硬条件全空的菜不进表
				if next(profile.flavor) ~= nil or next(profile.core_items) ~= nil or next(profile.core_tags) ~= nil then
					profiles[string.lower(identifier)] = profile
					built = built + 1
				end
			end
		end
	end

	print("[Touhou.Cook] 画像构建完成：扫描料理 " .. scanned .. " 道，入库 " .. built .. " 道，Cooktop02 配方读取成功 " .. recipeApiOk .. " 道")
	if built > 0 and recipeApiOk == 0 then
		print("[Touhou.Cook] 警告：FabricationRecipes API 读取全部失败（R1），核心食材与技能硬条件未生效；可在料理 XML 上用 core:/coretag: 前缀 tag 补充硬条件（D3 降级方案）")
	end
	if built == 0 then
		print("[Touhou.Cook] 警告：画像表为空，所有烹饪将产出兜底物品；请检查 Touhou_Cuisine 标记 tag 与 prefab.Tags API 是否可用")
	end
end


local function GetSkillLevel(character, skillIdentifier)
	if character == nil then return nil end
	local ok, level = pcall(function() return character.GetSkillLevel(skillIdentifier) end)
	if ok then return tonumber(level) end
	return nil
end

local function CheckItemConditions(profile, identifiers, tags)
	for id, needed in pairs(profile.core_items) do
		if (identifiers[id] or 0) < needed then return false end
	end
	for tag, needed in pairs(profile.core_tags) do
		if (tags[tag] or 0) < needed then return false end
	end
	return true
end

-- 技能门槛照抄 Cooktop02 配方的 RequiredSkill
local function CheckSkillConditions(profile, cooker)
	if #profile.required_skills == 0 then return true end
	if cooker == nil then return false end
	for _, skill in ipairs(profile.required_skills) do
		local level = GetSkillLevel(cooker, skill.identifier)
		if level == nil or level < (skill.level or 0) then return false end
	end
	return true
end

local function ScoreProfile(profile, identifiers, potFlavor)
	local score = 0
	local fullCoverage = true
	local coreHits = 0

	-- 风味分：命中给分，放多了倒扣
	for t, want in pairs(profile.flavor) do
		local have = potFlavor[t] or 0
		score = score + math.min(have, want) * Config.W_flavor
		if have > want then
			score = score - (have - want) * Config.P_overflow
		elseif have < want then
			fullCoverage = false
		end
	end

	for t, have in pairs(potFlavor) do
		if profile.flavor[t] == nil then
			score = score - have * Config.P_foreign
		end
	end

	for id, needed in pairs(profile.core_items) do
		if (identifiers[id] or 0) >= needed then
			coreHits = coreHits + 1
		end
	end
	score = score + coreHits * Config.W_core

	if fullCoverage and next(profile.flavor) ~= nil then
		score = score + Config.B_full
	end

	return score
end

-- 返回：产出identifier(没达阈值就是nil)、最高分、被技能门槛卡掉的最佳候选
local function SelectRecipe(cookpot, cooker)
	local identifiers = ParseIdentifiers(cookpot)
	local tags = ParseTags(cookpot)

	-- 只有白名单风味参与打分
	local potFlavor = {}
	for tag, value in pairs(tags) do
		if FlavorTags[tag] then
			potFlavor[tag] = value
		end
	end

	local bestScore, bestCandidates
	local blockedScore, blockedCandidates

	for identifier, profile in pairs(profiles) do
		if CheckItemConditions(profile, identifiers, tags) then
			local score = ScoreProfile(profile, identifiers, potFlavor)
			if CheckSkillConditions(profile, cooker) then
				if bestScore == nil or score > bestScore then
					bestScore = score
					bestCandidates = { identifier }
				elseif score == bestScore then
					table.insert(bestCandidates, identifier)
				end
			else
				if blockedScore == nil or score > blockedScore then
					blockedScore = score
					blockedCandidates = { identifier }
				elseif score == blockedScore then
					table.insert(blockedCandidates, identifier)
				end
			end
		end
	end

	if bestScore ~= nil and bestScore >= Config.Threshold then
		return bestCandidates[math.random(#bestCandidates)], bestScore, nil
	end
	if blockedScore ~= nil and blockedScore >= Config.Threshold then
		return nil, blockedScore, blockedCandidates[math.random(#blockedCandidates)]
	end
	return nil, nil, nil
end

-- 输出槽定的是第2个 ItemContainer（Cooktop.xml 里写死的），找不到就退回输入栏
local function GetOutputInventory(item)
	local outputInventory = item.OwnInventory
	local containerIndex = 0
	for container in item.GetComponents(ItemContainer) do
		containerIndex = containerIndex + 1
		if containerIndex == 2 then
			outputInventory = container.Inventory
			break
		end
	end
	return outputInventory
end


Hook.Add("Touhou.Cooktop.start", function(effect, deltaTime, item, targets, worldPosition)
	local cooker = FindLastUser(item)

	if item.GetComponentString("LightComponent").IsOn then
		Hook.Call("Touhou.Cooktop.cancel", {item, cooker})
		return
	end

	local errors = {}

	-- 修过：原来拿 EmptySlotCount<=0 判断没食材，结果锅满才报，方向反了
	if IsPotEmpty(item) then
		table.insert(errors, TextManager.Get("touhou.cooktop.noavailableingredients").Value)
	end

	if #errors > 0 then
		local errorMessage = ""
		for _, v in pairs(errors) do
			errorMessage = errorMessage .. v .. "\n"
		end

		SendMessageBox(TextManager.Get("error").Value, errorMessage, cooker)
		return
	end

	local recipe, _, skillBlocked = SelectRecipe(item, cooker)

	-- 没达标的也不报错，出兜底货；最佳候选是技能不够才黄的，提示一下玩家
	if recipe == nil then
		recipe = Config.FallbackItem
		if skillBlocked ~= nil then
			SendMessageBox(item.Name,
				TextManager.Get("touhou.cooktop.skillblocked").Value .. GetItemDisplayName(skillBlocked),
				cooker)
		end
	end

	local requiredtime = Config.FallbackRequiredTime
	if profiles[recipe] ~= nil then
		requiredtime = profiles[recipe].requiredtime or Config.DefaultRequiredTime
	end

	item.GetComponentString("MemoryComponent").Value = recipe
	item.GetComponentString("PowerContainer").Charge = requiredtime
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

	local prefab = ItemPrefab.Prefabs[recipe]
	if prefab == nil then
		print("[Touhou.Cook] 错误：产出物品不存在：" .. tostring(recipe))
		return
	end

	local amount = Config.FallbackAmount
	if profiles[recipe] ~= nil then
		amount = profiles[recipe].amount or 1
	end

	local outputInventory = GetOutputInventory(item)
	for i = 1, amount, 1 do
		Entity.Spawner.AddItemToSpawnQueue(prefab, outputInventory)
	end
end)

Hook.Add("Touhou.Cooktop.analyze", function(effect, deltaTime, item, targets, worldPosition)
	local user = FindLastUser(item)
	local errors = {}

	if IsPotEmpty(item) then
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

	-- 预测会做出什么，只打分不动锅
	local recipe, _, skillBlocked = SelectRecipe(item, user)
	local prediction
	if recipe ~= nil then
		prediction = TextManager.Get("touhou.cooktop.analyze.prediction").Value .. GetItemDisplayName(recipe)
	elseif skillBlocked ~= nil then
		prediction = TextManager.Get("touhou.cooktop.analyze.prediction").Value .. GetItemDisplayName(skillBlocked)
			.. TextManager.Get("touhou.cooktop.analyze.skillshortage").Value
	else
		prediction = TextManager.Get("touhou.cooktop.analyze.prediction.mush").Value
	end
	tagInfo = tagInfo .. "\n" .. prediction

	SendMessageBox(item.Name, tagInfo, user)
end)

-- 启动建画像；炸了也不挡 hook 注册，错误打到控制台
local buildOk, buildErr = pcall(BuildProfiles)
if not buildOk then
	print("[Touhou.Cook] 错误：画像构建失败：" .. tostring(buildErr))
end
