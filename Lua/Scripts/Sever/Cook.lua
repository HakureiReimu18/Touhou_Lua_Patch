--[[
	自由搭配料理系统
	设计规格书：Cook_自由搭配料理_设计规格书.md（codestable 工作流）
	设备契约：Items/Cooktop.xml（自由料理台 Touhou_Cooktop03）

	原理：料理的风味 tag 与 Cooktop02 配方在模组 XML 中已存在。
	本脚本启动时自动为每道料理构建"风味画像"，烹饪时对锅内食材聚合打分，
	最高分达阈值产出该料理，否则产出炼金术副产物兜底。
	新增料理只需编写 XML，本文件零改动。
]]

if CLIENT and not Game.IsSingleplayer then return end -- 服务端权威

-- ========== 配置参数（平衡性调优入口，规格书 §6.4，初值经评审确认） ==========
local Config = {
	Threshold = 3.0,               -- 产出阈值：最高分低于此值产出兜底物品
	W_flavor = 1.0,                -- 风味命中权重
	W_core = 3.0,                  -- 核心食材命中权重
	B_full = 2.0,                  -- 画像风味全覆盖奖励
	P_overflow = 0.5,              -- 风味溢出惩罚
	P_foreign = 0.25,              -- 画像外风味惩罚
	FallbackItem = "Byproduct_Alchemical", -- 兜底产出（D8：炼金术副产物）
	FallbackAmount = 1,
	FallbackRequiredTime = 10,     -- 兜底产出烹饪时间（秒）
	DefaultRequiredTime = 10,      -- 配方缺少 requiredtime 数据时的默认值
	SourceFabricator = "touhou_cooktop02", -- 画像硬条件的数据来源配方
}

-- ========== 风味 tag 白名单种子（D5；全量统计自模组 XML，2026-07-29） ==========
-- 启动时还会自动纳入所有带 touhou.foodtag.* 本地化的 tag（自维护，见 IsFlavorTag）
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

-- 约定：带 touhou.foodtag.* 本地化的 tag 即视为风味 tag（本地化与画像共用一份真相）
local function IsFlavorTag(tagName)
	if FlavorTags[tagName] then return true end
	if TextManager.Get("touhou.foodtag." .. tagName).Value ~= "" then
		FlavorTags[tagName] = true
		return true
	end
	return false
end

-- ========== 画像表 ==========
-- profiles[identifier小写] = { flavor, core_items, core_tags, required_skills, requiredtime, amount, original_identifier }
local profiles = {}

-- ========== 基础工具 ==========

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

-- 解析 "tag:value" 形式，返回小写 tag 名与数值
local function ParseTagValue(tagStr)
	local splitPos = string.find(tagStr, ":")
	if splitPos then
		return string.sub(tagStr, 1, splitPos - 1), tonumber(string.sub(tagStr, splitPos + 1)) or 1
	end
	return tagStr, 1
end

-- 收集 prefab 的全部 tag（小写），API 不可用时返回空表（启动日志会体现）
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

-- ========== R1 验证点：Cooktop02 配方数据读取（首选数据源，D3） ==========
-- 对 FabricationRecipes 的 API 形态做深度防御；全部读取失败时启动日志会告警，
-- 此时硬条件仅由 core:/coretag: 前缀 tag 提供（D3 降级方案）
local function ReadCooktop02Recipe(prefab)
	local result = nil

	pcall(function()
		local recipes = prefab.FabricationRecipes
		if recipes == nil then return end

		for _, entry in pairs(recipes) do
			local recipe = entry
			-- Dictionary 的 KeyValuePair 兼容
			local okV, v = pcall(function() return entry.Value end)
			if okV and v ~= nil then recipe = v end

			-- SuitableFabricators 匹配 Cooktop02（属性缺失时视为匹配，宽容处理）
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

-- ========== 画像构建（启动时执行一次，§6.2） ==========
local function BuildProfiles()
	local scanned, built, recipeApiOk = 0, 0, 0

	for _, entry in pairs(ItemPrefab.Prefabs) do
		local prefab = entry
		local okV, v = pcall(function() return entry.Value end) -- KeyValuePair 兼容
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

				-- 风味画像（白名单过滤，结构性 tag 自动排除）；core:/coretag: 前缀为降级硬条件
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

				-- Cooktop02 配方数据（首选硬条件来源）
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

				-- 无风味且无硬条件的料理不进入画像表（§6.2-4）
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

-- ========== 匹配与打分（§6.4） ==========

local function GetSkillLevel(character, skillIdentifier)
	if character == nil then return nil end
	local ok, level = pcall(function() return character.GetSkillLevel(skillIdentifier) end)
	if ok then return tonumber(level) end
	return nil
end

-- 硬条件：核心食材（identifier 与 tag 需求）
local function CheckItemConditions(profile, identifiers, tags)
	for id, needed in pairs(profile.core_items) do
		if (identifiers[id] or 0) < needed then return false end
	end
	for tag, needed in pairs(profile.core_tags) do
		if (tags[tag] or 0) < needed then return false end
	end
	return true
end

-- 硬条件：技能门槛（D7，继承 Cooktop02 配方 RequiredSkill）
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

	-- 风味命中（双向截断）与溢出惩罚
	for t, want in pairs(profile.flavor) do
		local have = potFlavor[t] or 0
		score = score + math.min(have, want) * Config.W_flavor
		if have > want then
			score = score - (have - want) * Config.P_overflow
		elseif have < want then
			fullCoverage = false
		end
	end

	-- 画像外风味惩罚
	for t, have in pairs(potFlavor) do
		if profile.flavor[t] == nil then
			score = score - have * Config.P_foreign
		end
	end

	-- 核心食材加成
	for id, needed in pairs(profile.core_items) do
		if (identifiers[id] or 0) >= needed then
			coreHits = coreHits + 1
		end
	end
	score = score + coreHits * Config.W_core

	-- 画像风味全覆盖奖励
	if fullCoverage and next(profile.flavor) ~= nil then
		score = score + Config.B_full
	end

	return score
end

-- 返回：产出 identifier（nil=无达阈值候选）、最高分、仅因技能不足被过滤的最高分候选
local function SelectRecipe(cookpot, cooker)
	local identifiers = ParseIdentifiers(cookpot)
	local tags = ParseTags(cookpot)

	-- 锅内风味向量（仅白名单 tag 参与打分，结构性 tag 不干扰）
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

-- 输出槽：取设备第 2 个 ItemContainer（契约见 Items/Cooktop.xml），取不到则退回输入栏
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

-- ========== Hooks ==========

Hook.Add("Touhou.Cooktop.start", function(effect, deltaTime, item, targets, worldPosition)
	local cooker = FindLastUser(item)

	if item.GetComponentString("LightComponent").IsOn then
		Hook.Call("Touhou.Cooktop.cancel", {item, cooker})
		return
	end

	local errors = {}

	-- 修复：锅空时报错（原实现 EmptySlotCount <= 0 判断方向相反，锅满时误报）
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

	-- 无候选不报错：兜底产出炼金术副产物（D4/D8）；最佳候选仅因技能不足被过滤时提示玩家（D7）
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

	-- 风味统计（保留原有展示）
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

	-- 产出预测（§6.6）：只读打分，不影响状态
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

-- ========== 启动：构建画像表（异常不阻断 hook 注册，控制台输出诊断） ==========
local buildOk, buildErr = pcall(BuildProfiles)
if not buildOk then
	print("[Touhou.Cook] 错误：画像构建失败：" .. tostring(buildErr))
end
