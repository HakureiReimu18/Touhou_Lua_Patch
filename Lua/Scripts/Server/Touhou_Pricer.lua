-- 都是假的

--[[
	估价器：物品上点按钮，把背包（含容器套娃）或身上穿戴的装备估价后私聊播报

	估价优先级：自身售价 → 合成配方材料价/产出数 → 分解产物 → 默认 500mk
	按钮在 Items/Tools.xml 里，接 Touhou.Pricer.scanAll / scanWorn 两个 hook
]]

if CLIENT and not Game.IsSingleplayer then return end -- 服务端权威

local DEFAULT_VALUE = 500
local MAX_LINES = 8
local MAX_DETAIL_LEN = 160 -- 消息按字节算长度，别超
local SCAN_COOLDOWN = 0.5  -- 同一估价器同一模式的触发冷却（秒），防重复播报

-- 逐个槽位 pcall；槽位名以当前版本的 InvSlotType 枚举为准（旧版的 Face/IDCard/Toolbelt 已经没有了）
local WORN_SLOT_TYPES = {
	InvSlotType.Head,
	InvSlotType.InnerClothes,
	InvSlotType.OuterClothes,
	InvSlotType.Headset,
	InvSlotType.Card,
	InvSlotType.Bag,
}

-- prefab → 单价缓存，XML 静态数据整场不变
local valueCache = {}
-- "物品ID:模式" → 上次触发时刻，防抖用
local lastScanTime = {}

local function AsList(enumerable)
	local list = {}
	if enumerable == nil then return list end
	for x in enumerable do table.insert(list, x) end
	return list
end

-- FabricationRecipes 是个字典，拿它的 Values 就行
local function GetFabricationRecipes(prefab)
	local ok, dict = pcall(function() return prefab.FabricationRecipes end)
	if not ok or dict == nil then return {} end
	local okValues, values = pcall(function() return dict.Values end)
	if okValues and values ~= nil then
		return AsList(values)
	end
	-- 兜底：直接迭代字典，兼容迭代产出 KeyValuePair 的情况
	local list = {}
	for pair in dict do
		local recipe = pair
		local okValue, inner = pcall(function() return pair.Value end)
		if okValue and inner ~= nil then recipe = inner end
		table.insert(list, recipe)
	end
	return list
end

-- Find 的签名对不上就换着法儿多试几路
local function FindPrefab(identifierObj, identifierStr)
	local ok, prefab = pcall(function() return ItemPrefab.Find("", identifierObj) end)
	if ok and prefab ~= nil then return prefab end
	ok, prefab = pcall(function() return ItemPrefab.Find("", identifierStr) end)
	if ok and prefab ~= nil then return prefab end
	ok, prefab = pcall(function() return ItemPrefab.GetItemPrefab(identifierStr) end) -- 旧版 API
	if ok and prefab ~= nil then return prefab end
	return nil
end

-- 配方材料和分解条目的字段不统一（ItemPrefab / FirstMatchingPrefab / ItemPrefabIdentifier / ItemIdentifier），挨个试
local function ResolveEntryPrefab(entry)
	local prefab = nil
	pcall(function() prefab = entry.ItemPrefab end)
	if prefab ~= nil then return prefab end
	pcall(function() prefab = entry.FirstMatchingPrefab end)
	if prefab ~= nil then return prefab end
	local identifierObj, identifierStr = nil, nil
	pcall(function() identifierObj = entry.ItemPrefabIdentifier end)
	if identifierObj == nil then
		pcall(function() identifierObj = entry.ItemIdentifier end)
	end
	if identifierObj == nil then return nil end
	pcall(function() identifierStr = identifierObj.Value end)
	return FindPrefab(identifierObj, identifierStr)
end

-- 这版本价格字段叫 Price，BasePrice 是新版源码的名字，兜底用
local function GetOwnPrice(prefab)
	local price = nil
	pcall(function()
		local info = prefab.DefaultPrice
		if info == nil then info = prefab.Price end
		if info == nil then return end
		local okValue, value = pcall(function() return info.Price end)
		if not okValue or value == nil then
			okValue, value = pcall(function() return info.BasePrice end)
		end
		if okValue and value ~= nil and value > 0 then
			price = value
		end
	end)
	return price
end

local GetPrefabValue -- 前向声明（递归用）

local function RecipeUnitCost(recipe, visiting)
	local ok, requiredItems = pcall(function() return recipe.RequiredItems end)
	if not ok or requiredItems == nil then return nil end
	local cost = 0
	for req in requiredItems do
		local reqPrefab = ResolveEntryPrefab(req)
		if reqPrefab == nil then return nil end -- 有材料解析不出，放弃这条配方
		local amount = 1
		pcall(function() if req.Amount ~= nil and req.Amount > 0 then amount = req.Amount end end)
		cost = cost + GetPrefabValue(reqPrefab, visiting) * amount
	end
	if cost <= 0 then return nil end
	local output = 1
	pcall(function() if recipe.Amount ~= nil and recipe.Amount > 0 then output = recipe.Amount end end)
	return cost / output
end

GetPrefabValue = function(prefab, visiting)
	if prefab == nil then return 0 end
	if valueCache[prefab] ~= nil then return valueCache[prefab] end

	-- 1. 自身售价
	local ownPrice = GetOwnPrice(prefab)
	if ownPrice ~= nil then
		valueCache[prefab] = ownPrice
		return ownPrice
	end

	-- 防循环：A 由 B 合成、B 分解出 A 之类的环
	if visiting[prefab] then return DEFAULT_VALUE end
	visiting[prefab] = true

	-- 2. 合成配方（多配方取最低单价）
	local best = nil
	for _, recipe in pairs(GetFabricationRecipes(prefab)) do
		local unit = RecipeUnitCost(recipe, visiting)
		if unit ~= nil and (best == nil or unit < best) then
			best = unit
		end
	end
	if best ~= nil then
		visiting[prefab] = nil
		valueCache[prefab] = best
		return best
	end

	-- 3. 分解产物总价值
	local sum = 0
	local ok, decItems = pcall(function() return prefab.DeconstructItems end)
	if ok and decItems ~= nil then
		for dec in decItems do
			local decPrefab = ResolveEntryPrefab(dec)
			if decPrefab ~= nil then
				local amount = 1
				pcall(function() if dec.Amount ~= nil and dec.Amount > 0 then amount = dec.Amount end end)
				sum = sum + GetPrefabValue(decPrefab, visiting) * amount
			end
		end
	end
	visiting[prefab] = nil
	if sum > 0 then
		valueCache[prefab] = sum
		return sum
	end

	-- 4. 默认价值
	valueCache[prefab] = DEFAULT_VALUE
	return DEFAULT_VALUE
end

local function CollectInventoryItems(inventory, out, depth)
	if inventory == nil or depth > 10 then return end
	local ok, items = pcall(function() return inventory.AllItems end)
	if not ok or items == nil then return end
	for invItem in items do
		if invItem ~= nil then
			table.insert(out, invItem)
			pcall(function() CollectInventoryItems(invItem.OwnInventory, out, depth + 1) end)
		end
	end
end

local function CollectWornItems(character, out)
	for _, slotType in ipairs(WORN_SLOT_TYPES) do
		pcall(function()
			local worn = character.Inventory.GetItemInLimbSlot(slotType)
			if worn ~= nil then table.insert(out, worn) end
		end)
	end
end

-- 按钮触发时 targets 里没有使用者，只能沿容器链往上摸持有者
local function GetHolderCharacter(item)
	local entity = item
	for _ = 1, 12 do
		local inv = nil
		pcall(function() inv = entity.ParentInventory end)
		if inv == nil then return nil end
		local owner = nil
		pcall(function() owner = inv.Owner end)
		if owner == nil then return nil end
		for character in Character.CharacterList do
			if owner == character then return character end
		end
		entity = owner -- owner 是外层容器物品，继续向上
	end
	return nil
end

local function SendPricerMessage(character, message)
	local senderName = TextManager.Get("touhou.pricer.sendername").Value
	if Game.IsSingleplayer then
		if Game.GameSession and Game.GameSession.CrewManager then
			Game.GameSession.CrewManager.AddSinglePlayerChatMessage(senderName, message, ChatMessageType.Radio, nil)
		end
	elseif SERVER then
		for _, client in pairs(Client.ClientList) do
			if client.Character == character then
				local chatMessage = ChatMessage.Create(senderName, message, ChatMessageType.Radio, nil)
				Game.Server.SendDirectChatMessage(chatMessage, client)
				return
			end
		end
	end
end

-- 超长了就从末尾一行行砍，砍过就补省略号
local function FitDetailLines(lines, totalKinds)
	local shown = #lines
	while #lines > 0 do
		local text = table.concat(lines, "，")
		if string.len(text) <= MAX_DETAIL_LEN then
			if shown < totalKinds then
				text = text .. TextManager.Get("touhou.pricer.more").Value
			end
			return text
		end
		table.remove(lines)
	end
	return ""
end

local function ScanAndReport(item, targets, wornOnly)
	local itemId = nil
	pcall(function() itemId = item.ID end)
	if itemId ~= nil then
		local key = tostring(itemId) .. ":" .. tostring(wornOnly)
		local now = os.clock()
		if lastScanTime[key] ~= nil and now - lastScanTime[key] < SCAN_COOLDOWN then return end
		lastScanTime[key] = now
	end

	local character = GetHolderCharacter(item)
	if character == nil and targets ~= nil then
		local candidate = targets[1]
		if candidate ~= nil then
			for c in Character.CharacterList do
				if c == candidate then character = c break end
			end
		end
	end
	if character == nil or character.Inventory == nil then return end

	local rawItems = {}
	if wornOnly then
		CollectWornItems(character, rawItems)
	else
		CollectInventoryItems(character.Inventory, rawItems, 0)
	end

	-- 按 prefab 归类，顺手跳过估价器自己
	local groups = {}
	local order = {}
	local visiting = {}
	for _, invItem in pairs(rawItems) do
		if invItem ~= item and invItem.Prefab ~= nil then
			local prefab = invItem.Prefab
			if groups[prefab] == nil then
				groups[prefab] = { name = invItem.Name, count = 0, unit = GetPrefabValue(prefab, visiting) }
				table.insert(order, prefab)
			end
			local stack = 1
			pcall(function() if invItem.StackSize ~= nil and invItem.StackSize > 1 then stack = invItem.StackSize end end)
			groups[prefab].count = groups[prefab].count + stack
		end
	end

	if #order == 0 then
		SendPricerMessage(character, TextManager.Get("touhou.pricer.empty").Value)
		return
	end

	table.sort(order, function(a, b)
		return groups[a].unit * groups[a].count > groups[b].unit * groups[b].count
	end)

	local total = 0
	local totalCount = 0
	local lines = {}
	local lineFmt = TextManager.Get("touhou.pricer.line").Value
	for i, prefab in ipairs(order) do
		local g = groups[prefab]
		local subtotal = g.unit * g.count
		total = total + subtotal
		totalCount = totalCount + g.count
		if i <= MAX_LINES then
			table.insert(lines, string.format(lineFmt, g.name, g.count, math.floor(subtotal + 0.5)))
		end
	end

	local summaryKey = wornOnly and "touhou.pricer.summary.worn" or "touhou.pricer.summary"
	local summaryFmt = TextManager.Get(summaryKey).Value
	SendPricerMessage(character, string.format(summaryFmt, math.floor(total + 0.5), #order, totalCount))

	local detail = FitDetailLines(lines, #order)
	if detail ~= "" then
		SendPricerMessage(character, detail)
	end
end

Hook.Add("Touhou.Pricer.scanAll", "Touhou.Pricer.scanAll", function(effect, deltaTime, item, targets, worldPosition)
	ScanAndReport(item, targets, false)
end)

Hook.Add("Touhou.Pricer.scanWorn", "Touhou.Pricer.scanWorn", function(effect, deltaTime, item, targets, worldPosition)
	ScanAndReport(item, targets, true)
end)

-- 防抖表按"物品ID:模式"累积，一轮下来能把见过的物品全记一遍；它只用来挡 SCAN_COOLDOWN 秒内的重复触发，
-- 跨巡回的旧条目早就失效了（os.clock 只增不减），所以轮前轮后清掉，行为和不清完全一致
local function ClearScanCooldown()
	lastScanTime = {}
end
Hook.Add("roundStart", "Touhou.Pricer.roundStart", ClearScanCooldown)
Hook.Add("roundEnd", "Touhou.Pricer.roundEnd", ClearScanCooldown)

-- 导出给女苑天赋（Jyoon_Wealth_Talent.lua）复用同一套估价；init.lua 里本脚本先加载，直接调就行
TouhouPricer = TouhouPricer or {}
TouhouPricer.GetPrefabValue = function(prefab) return GetPrefabValue(prefab, {}) end
