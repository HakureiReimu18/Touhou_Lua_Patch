--[[
	以太回收机（批量回收系统）
	把入料栏里带 Touhou_Weapon_Level01..05 tag 的物品，按仪式台（Magic_Altar）的回收配方批量还原：
	Level01 → 炼金术副产物×2；Level02/03/04/05 → 以太层金属×1/2/4/16。
	产出表启动时从模组 XML 读取（以后改配方不用动这里），XML 读不到的等级用内置兜底表补齐；
	催化消耗与处理时长见 Config。

	组件契约（见 Items/EtherRecycler.xml，改动需同步）：
		第 1 个 ItemContainer = 武器入料栏（回收中由 Lua 锁定）
		第 2 个 ItemContainer = 催化槽（元素瓶，回收中锁定）
		第 3 个 ItemContainer = 输出栏（产出生成到这里，不锁定）
		LightComponent.IsOn    = 回收状态标志（Lua 置位，XML 归零逻辑复位）
		MemoryComponent.Value  = 本批结算表（"touhou_weapon_level03:2;…"，取消时清空）
		PowerContainer.Charge  = 处理倒计时（XML 每 0.25 秒扣 0.25）
		第 1 个 CustomInterface = 回收/取消按钮（Labels 由 Lua 切换）

	元素瓶耐久扣到 0 时会触发它自己的 OnBroken（元素瓶 XML 里是 Remove），自行销毁，这里不用管。
]]

if CLIENT and not Game.IsSingleplayer then return end -- 服务端权威

-- 调平衡就改这里
local Config = {
	-- 提示日志：默认关闭；桩验证里由 __TLE_EtherRecyclerVerbose = true 打开
	VerboseLogs = (__TLE_EtherRecyclerVerbose == true),
	CatalystCostPerItem = 1.0,              -- 每件武器消耗催化耐久（元素瓶满耐久 100 → 100 件/瓶）
	SecondsPerItem = 0.5,                   -- 每件处理时间（秒）
	MinSeconds = 0.5,                       -- 处理时间下限
	MaxSeconds = 60,                        -- 处理时间上限
	InputTagPrefix = "touhou_weapon_level", -- 等级 tag 前缀（Touhou_Weapon_Level0X）
}

-- 回收表：等级 tag → 产出（固定值，改平衡就改这里；不再从 XML 读配方）
local FixedRecipes = {
	{ tag = "Touhou_Weapon_Level01", output = "Byproduct_Alchemical", amount = 2 },
	{ tag = "Touhou_Weapon_Level02", output = "Touhou_Base_Metal", amount = 1 },
	{ tag = "Touhou_Weapon_Level03", output = "Touhou_Base_Metal", amount = 2 },
	{ tag = "Touhou_Weapon_Level04", output = "Touhou_Base_Metal", amount = 4 },
	{ tag = "Touhou_Weapon_Level05", output = "Touhou_Base_Metal", amount = 16 },
}

local recipes = {}     -- 等级 tag（小写）→ { tag = 原始大小写, output = 产出 identifier, amount = 每件产出数 }
local levelTagOrder = {} -- 按等级从低到高排序的 tag 列表（取件时从高往低查）
local pendingTimers = {} -- 机器 → 计时令牌（取消/重开时自增，作废旧的 Timer 回调）

local function FindLastUser(machine)
	local minDistance
	local distance
	local closestCharacter

	for character in Character.CharacterList do
		if character.SelectedItem == machine and character.IsPlayer then
			distance = Vector2.Distance(character.WorldPosition, machine.WorldPosition)

			if minDistance == nil then
				minDistance = distance
				closestCharacter = character
			elseif distance < minDistance then
				closestCharacter = character
			end
		end
	end

	return closestCharacter
end

-- 只打控制台，不弹窗（用户要求：宁可控制台信息/面板文字，也不要 MessageBox）
-- 提示日志默认关闭（Config.VerboseLogs = true 可打开）；错误类日志始终打
local function Notify(sendername, text, character)
	if Config.VerboseLogs then
		print("[" .. tostring(sendername) .. "] " .. tostring(text):gsub("\n", " | "))
	end
end

-- 消息抬头用设备名（纯字符串；item.Name 是 LocalizedString，跨 CLR 转换没必要冒这个险）
local function GetMachineName()
	local name = TextManager.Get("entityname.touhou_ether_recycler").Value
	if name == "" then
		name = "Touhou_Ether_Recycler"
	end
	return name
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

-- 按 XML 顺序取机器上的三个容器（入料 / 催化 / 输出）。
-- 注意：不要写 item.GetComponents(ItemContainer)——那是 C# 泛型方法，LuaCs 会报
-- 「Tried to call a generic method without a generic argument」；改为遍历 item.Components，
-- 用 Name 筛 ItemContainer（属性探测兜底），先例见 Touhou_Mod_Hotkey.lua。
local function GetMachineInventories(item)
	local containers = {}

	local ok = pcall(function()
		for component in item.Components do
			local isContainer = false

			local okName, componentName = pcall(function() return component.Name end)
			if okName and (componentName == "ItemContainer" or componentName == "itemcontainer") then
				isContainer = true
			else
				local okInv = pcall(function() return component.Inventory end)
				local okLabel = pcall(function() return component.UILabel end)
				isContainer = okInv and okLabel
			end

			if isContainer then
				local okInv2, inventory = pcall(function() return component.Inventory end)
				if okInv2 and inventory ~= nil then
					table.insert(containers, inventory)
				end
			end
		end
	end)

	if not ok or #containers < 3 then
		print("[Touhou.Recycler] 错误：容器组件数量不足（期望 3 个 ItemContainer，实际 " .. #containers
			.. " 个），请检查 Items/EtherRecycler.xml 的组件契约")
		return nil
	end

	return containers[1], containers[2], containers[3]
end

-- 从 tag 名解析等级号（Touhou_Weapon_Level0X → X）
local function WeaponLevelSuffix(tagName)
	return tonumber(string.sub(string.lower(tagName), #Config.InputTagPrefix + 1))
end

-- 登记一个等级 tag（同名的先到先得）；成功返回 true
local function RegisterLevelTag(tagName, output, amount)
	local key = string.lower(tagName)
	if recipes[key] ~= nil then
		return false
	end

	recipes[key] = { tag = tagName, output = output, amount = amount }
	table.insert(levelTagOrder, tagName)
	table.sort(levelTagOrder, function(a, b) return (WeaponLevelSuffix(a) or 0) < (WeaponLevelSuffix(b) or 0) end)
	return true
end

-- 取物品的等级 tag（小写）；同一件带多个等级 tag 时取最高级（实测无此情况，纯防御）
local function GetWeaponLevelTag(item)
	for i = #levelTagOrder, 1, -1 do
		local tagName = levelTagOrder[i]
		local ok, has = pcall(function() return item.HasTag(tagName) end)
		if ok and has then
			return string.lower(tagName)
		end
	end
	return nil
end

local function GetRecipe(levelTag)
	return recipes[levelTag]
end

-- 扫描入料栏：counts[等级tag] = 件数；只认有回收配方的等级，其余留在栏里
local function BuildBatch(inputInventory)
	local counts, total = {}, 0

	for contained in inputInventory.AllItemsMod do
		local levelTag = GetWeaponLevelTag(contained)
		if levelTag ~= nil and GetRecipe(levelTag) ~= nil then
			counts[levelTag] = (counts[levelTag] or 0) + 1
			total = total + 1
		end
	end

	return counts, total
end

-- counts → 产出表（产出 identifier → 数量）
local function ResolveYields(counts)
	local yields = {}

	for levelTag, count in pairs(counts) do
		local data = GetRecipe(levelTag)
		if data ~= nil then
			yields[data.output] = (yields[data.output] or 0) + (data.amount or 1) * count
		end
	end

	return yields
end

local function SerializeBatch(counts)
	local parts = {}
	for levelTag, count in pairs(counts) do
		table.insert(parts, levelTag .. ":" .. count)
	end
	return table.concat(parts, ";")
end

local function ParseBatch(text)
	local counts = {}
	for levelTag, count in string.gmatch(text, "([%w_]+):(%d+)") do
		counts[levelTag] = (counts[levelTag] or 0) + tonumber(count)
	end
	return counts
end

local function CountTotal(counts)
	local total = 0
	for _, count in pairs(counts) do
		total = total + count
	end
	return total
end

-- 产出物 + 预计数量文本，如「以太层金属×12、炼金术副产物×2」
local function BuildYieldText(yields)
	local parts = {}
	for identifier, amount in pairs(yields) do
		table.insert(parts, GetItemDisplayName(identifier) .. "×" .. amount)
	end
	return table.concat(parts, "、")
end

-- 输出栏需要的空位数（保守估算：按最大堆叠整堆算，不利用现存零头）
local function GetNeededSlots(yields)
	local neededSlots = 0

	for identifier, amount in pairs(yields) do
		local prefab = ItemPrefab.Prefabs[identifier]
		if prefab == nil then
			return nil, identifier
		end

		local maxStack = 32
		pcall(function() maxStack = tonumber(prefab.MaxStackSize) or maxStack end)
		if maxStack == nil or maxStack < 1 then
			maxStack = 32
		end

		neededSlots = neededSlots + math.ceil(amount / maxStack)
	end

	return neededSlots, nil
end

-- 输出栏空位数（按格数）。
-- 注意：1.12 里 Inventory.EmptySlotCount 的实际语义是「已占用格数」（源码 slots.Count(i => !i.Empty())，
-- 游戏内部正是用 EmptySlotCount / Capacity 当「装满比例」），直接拿来判空会永远得到 0 → 误报空间不足。
-- 所以这里按格逐个数（GetItemAt 为 0 基索引，空格返回 nil）。
local function CountFreeSlots(inventory)
	local capacity = 0
	pcall(function() capacity = tonumber(inventory.Capacity) or 0 end)

	if capacity > 0 then
		local free = 0
		local ok = pcall(function()
			for i = 0, capacity - 1 do
				if inventory.GetItemAt(i) == nil then
					free = free + 1
				end
			end
		end)
		if ok then
			return free
		end
	end

	-- 退化：Capacity − 已占用格数
	local occupied = 0
	pcall(function() occupied = tonumber(inventory.EmptySlotCount) or 0 end)
	return math.max(capacity - occupied, 0)
end

-- 催化槽状态：是否放了东西 + 可用耐久合计
local function GetCatalystStatus(catalystInventory)
	local found, available = false, 0

	-- 注意：不要把 pairs 用在 .NET 集合上（MoonSharp 会报 bad argument #1 to 'next'），用单变量 for
	for contained in catalystInventory.AllItemsMod do
		found = true
		local condition = tonumber(contained.Condition) or 0
		if condition > 0 then
			available = available + condition
		end
	end

	return found, available
end

-- 逐瓶扣耐久；扣到 0 的瓶子由它自己的 OnBroken（Remove）销毁
local function ConsumeCatalyst(catalystInventory, cost)
	local remaining = cost

	for contained in catalystInventory.AllItemsMod do
		if remaining <= 0 then
			break
		end

		local condition = tonumber(contained.Condition) or 0
		if condition > 0 then
			local used = math.min(condition, remaining)
			contained.Condition = condition - used
			remaining = remaining - used
		end
	end

	return remaining <= 0
end

local function GetProcessingTime(total)
	local seconds = Config.SecondsPerItem * total
	return math.max(Config.MinSeconds, math.min(Config.MaxSeconds, seconds))
end

-- 从单个 prefab 读回收配方；返回本次命中并入库的条数
local function ReadRecipesFromPrefab(prefab, stats)
	local okId, identifier = pcall(function() return prefab.Identifier.Value end)
	if not okId or identifier == nil then
		return 0
	end

	local okF, fabrications = pcall(function() return prefab.FabricationRecipes end)
	if not okF or fabrications == nil then
		return 0
	end

	stats.withRecipes = stats.withRecipes + 1
	local hit = 0

	for entry in fabrications do
		stats.recipes = stats.recipes + 1

		local recipe = entry
		-- 枚举 Dictionary 可能是 KeyValuePair（取 .Value），也可能只给 key（按索引取回）
		local okRv, rv = pcall(function() return entry.Value end)
		if okRv and rv ~= nil then
			recipe = rv
		elseif entry ~= nil and type(entry) ~= "table" then
			local okR2, r2 = pcall(function() return fabrications[entry] end)
			if okR2 then
				recipe = r2
			end
		end

		if recipe ~= nil then

		-- 只认仪式台配方
		local isAltar = false
		local okS, suitable = pcall(function() return recipe.SuitableFabricators end)
		if okS and suitable ~= nil then
			for fabricator in suitable do
				if string.lower(tostring(fabricator)) == "magic_altar" then
					isAltar = true
					break
				end
			end
		end
		if isAltar then
			stats.altar = stats.altar + 1
		end

		if isAltar then
			local okI, requiredItems = pcall(function() return recipe.RequiredItems end)
			if okI and requiredItems ~= nil then
				for req in requiredItems do
					local tag
					pcall(function() tag = tostring(req.Tag) end)

					if tag ~= nil and tag ~= "" then
						local tagName = string.lower(tag)
						if string.sub(tagName, 1, #Config.InputTagPrefix) == Config.InputTagPrefix
							and tonumber(string.sub(tagName, #Config.InputTagPrefix + 1)) ~= nil then
							local amount = 1
							pcall(function() amount = tonumber(recipe.Amount) or 1 end)
							if RegisterLevelTag(tag, tostring(identifier), amount) then
								stats.hits = stats.hits + 1
								hit = hit + 1
							else
								print("[Touhou.Recycler] 警告：等级 " .. tagName .. " 有多条回收配方，沿用第一条")
							end
						end
					end
				end
			end
		end
	end
	end

	return hit
end

-- 启动时跑一次：从模组 XML 里读仪式台（Magic_Altar）的回收配方
-- 形如 <Fabricate suitablefabricators="Magic_Altar" amount="2"><RequiredItem tag="Touhou_Weapon_Level03"/>…</Fabricate>
-- 所有 API 访问都包 pcall；读不到就退化为 FallbackRecipes（下面会打诊断，便于定位是哪一步读不到）
local function ReadRecycleRecipes()
	local stats = { prefabs = 0, withRecipes = 0, recipes = 0, altar = 0, hits = 0 }

	-- 第一轮：常规扫描（遍历整个 Prefabs 字典）
	-- 注意：不同 LuaCs 版本下，字典遍历可能给 KeyValuePair、也可能只给 key，两种都要认
	pcall(function()
		for entry in ItemPrefab.Prefabs do
			stats.prefabs = stats.prefabs + 1
			local prefab = entry

			local okV, v = pcall(function() return entry.Value end)
			if okV and v ~= nil then
				prefab = v -- KeyValuePair
			elseif type(entry) ~= "table" then
				-- 遍历出来的是 key（Identifier/字符串）：按索引取回来（索引查找已实机验证可用）
				local okP, p = pcall(function() return ItemPrefab.Prefabs[entry] end)
				if okP then
					prefab = p
				end
			end

			if prefab ~= nil then
				ReadRecipesFromPrefab(prefab, stats)
			end
		end
	end)

	-- 第二轮：兜底——直接按 identifier 取「产出物」预制体（索引查找已实机验证可用）
	if stats.hits == 0 then
		for _, candidate in ipairs({ "Touhou_Base_Metal", "Byproduct_Alchemical" }) do
			pcall(function()
				local prefab = ItemPrefab.Prefabs[candidate]
				if prefab ~= nil then
					stats.prefabs = stats.prefabs + 1
					ReadRecipesFromPrefab(prefab, stats)
				end
			end)
		end
	end

	print(string.format("[Touhou.Recycler] 读取诊断：prefab %d，含配方 %d，配方 %d，仪式台 %d，命中等级 tag %d",
		stats.prefabs, stats.withRecipes, stats.recipes, stats.altar, stats.hits))
	print("[Touhou.Recycler] 回收配方读取完成：入库 " .. stats.hits .. " 条")
	if stats.hits == 0 then
		print("[Touhou.Recycler] 警告：未读到任何仪式台回收配方，已退化为内置兜底表")
	end
end

-- 读不到的等级用兜底表补齐（XML 有的以 XML 为准）
local function FillFallbackRecipes()
	local added = 0
	for _, data in ipairs(FallbackRecipes) do
		if RegisterLevelTag(data.tag, data.output, data.amount) then
			added = added + 1
		end
	end
	if added > 0 then
		print("[Touhou.Recycler] 兜底表补齐 " .. added .. " 条等级配方")
	end
end

Hook.Add("Touhou.Recycler.start", function(effect, deltaTime, item, targets, worldPosition)
	local user = FindLastUser(item)

	-- 运行中再按按钮 = 取消（料理台同款闭环：清空结算表与 Charge，归零后 end hook 直接返回，不产生消耗）
	if item.GetComponentString("LightComponent").IsOn then
		Hook.Call("Touhou.Recycler.cancel", {item, user})
		return
	end

	local inputInventory, catalystInventory, outputInventory = GetMachineInventories(item)
	if inputInventory == nil then
		return
	end

	local errors = {}

	local counts, total = BuildBatch(inputInventory)
	if total == 0 then
		table.insert(errors, TextManager.Get("touhou.recycler.noinput").Value)
	end

	local yields = ResolveYields(counts)
	local catalystCost = Config.CatalystCostPerItem * total
	local hasCatalyst, catalystAvailable = GetCatalystStatus(catalystInventory)
	if not hasCatalyst then
		table.insert(errors, TextManager.Get("touhou.recycler.nocatalyst").Value)
	elseif catalystAvailable < catalystCost then
		table.insert(errors, string.format(TextManager.Get("touhou.recycler.nocatalystcondition").Value, catalystCost - catalystAvailable))
	end

	local neededSlots, missingOutput = GetNeededSlots(yields)
	if neededSlots == nil then
		table.insert(errors, string.format(TextManager.Get("touhou.recycler.missingoutput").Value, tostring(missingOutput)))
	else
		local freeSlots = CountFreeSlots(outputInventory)
		if freeSlots < neededSlots then
			table.insert(errors, string.format(TextManager.Get("touhou.recycler.nooutputspace").Value, neededSlots, freeSlots))
		end
	end

	if #errors > 0 then
		local errorMessage = ""
		for _, v in pairs(errors) do
			errorMessage = errorMessage .. v .. "\n"
		end

		Notify(TextManager.Get("error").Value, errorMessage, user)
		return
	end

	-- 启动回收：计时改用 Lua 的 Timer（XML 那套 PowerContainer charge 倒计时实测不生效，会卡在"回收中"）
	local seconds = GetProcessingTime(total)
	item.GetComponentString("MemoryComponent").Value = SerializeBatch(counts)
	item.GetComponentString("PowerContainer").Charge = seconds
	item.GetComponentString("LightComponent").IsOn = true

	inputInventory.Locked = true
	catalystInventory.Locked = true
	item.GetComponentString("CustomInterface").Labels = TextManager.Get("fabricatorcancel").Value

	local token = (pendingTimers[item] or 0) + 1
	pendingTimers[item] = token
	Timer.Wait(function()
		if pendingTimers[item] ~= token then
			return -- 已取消或被新批次顶掉
		end
		pendingTimers[item] = nil
		Hook.Call("Touhou.Recycler.end", nil, 0, item, nil, nil)
	end, math.max(1, math.floor(seconds * 1000)))

	Notify(GetMachineName(), string.format(TextManager.Get("touhou.recycler.started").Value, BuildYieldText(yields), seconds), user)
end)

Hook.Add("Touhou.Recycler.cancel", function(parameters)
	local machine = parameters[1]
	if machine == nil then
		return
	end

	-- 作废本次计时，之后把一切恢复原状（不产生消耗）
	pendingTimers[machine] = (pendingTimers[machine] or 0) + 1

	machine.GetComponentString("MemoryComponent").Value = ""
	machine.GetComponentString("PowerContainer").Charge = 0
	machine.GetComponentString("LightComponent").IsOn = false

	local inputInventory, catalystInventory = GetMachineInventories(machine)
	if inputInventory ~= nil then
		inputInventory.Locked = false
	end
	if catalystInventory ~= nil then
		catalystInventory.Locked = false
	end

	Notify(GetMachineName(), TextManager.Get("fabricatorcancel").Value)
end)

Hook.Add("Touhou.Recycler.end", function(effect, deltaTime, item, targets, worldPosition)
	-- 先把状态复位（灯灭=不再是"运行中"，面板据此把按钮文字切回"开始回收"）
	item.GetComponentString("LightComponent").IsOn = false
	pendingTimers[item] = (pendingTimers[item] or 0) + 1
	item.GetComponentString("CustomInterface").Labels = TextManager.Get("touhou.recycler.start").Value

	local inputInventory, catalystInventory, outputInventory = GetMachineInventories(item)
	if inputInventory == nil then
		return
	end

	local memory = item.GetComponentString("MemoryComponent")
	local batch = memory.Value
	memory.Value = ""

	if inputInventory ~= nil then
		inputInventory.Locked = false
	end
	if catalystInventory ~= nil then
		catalystInventory.Locked = false
	end

	-- 取消路径：结算表为空，什么都不动
	if batch == nil or batch == "" then
		return
	end
	if inputInventory == nil or catalystInventory == nil or outputInventory == nil then
		return
	end

	local counts = ParseBatch(batch)
	local total = CountTotal(counts)
	if total <= 0 then
		return
	end

	-- 扣催化（start 时已校验；这里防御性再查一遍）
	if not ConsumeCatalyst(catalystInventory, Config.CatalystCostPerItem * total) then
		print("[Touhou.Recycler] 警告：催化耐久不足，本次结算跳过")
		return
	end

	-- 按结算表移除入料
	local remaining = {}
	for levelTag, count in pairs(counts) do
		remaining[levelTag] = count
	end

	for contained in inputInventory.AllItemsMod do
		local levelTag = GetWeaponLevelTag(contained)
		if levelTag ~= nil and (remaining[levelTag] or 0) > 0 then
			remaining[levelTag] = remaining[levelTag] - 1
			Entity.Spawner.AddItemToRemoveQueue(contained)
		end
	end

	-- 生成产出
	local yields = ResolveYields(counts)
	for identifier, amount in pairs(yields) do
		local prefab = ItemPrefab.Prefabs[identifier]
		if prefab == nil then
			print("[Touhou.Recycler] 错误：产出物品不存在：" .. tostring(identifier))
		else
			for i = 1, amount, 1 do
				Entity.Spawner.AddItemToSpawnQueue(prefab, outputInventory)
			end
		end
	end

	if Config.VerboseLogs then
		print("[Touhou.Recycler] 回收完成：" .. batch)
	end
end)

Hook.Add("Touhou.Recycler.analyze", function(effect, deltaTime, item, targets, worldPosition)
	local user = FindLastUser(item)

	if item.GetComponentString("LightComponent").IsOn then
		Notify(TextManager.Get("error").Value, TextManager.Get("touhou.recycler.isactive").Value, user)
		return
	end

	local inputInventory, catalystInventory, outputInventory = GetMachineInventories(item)
	if inputInventory == nil then
		return
	end

	local counts, total = BuildBatch(inputInventory)
	if total == 0 then
		Notify(TextManager.Get("error").Value, TextManager.Get("touhou.recycler.noinput").Value, user)
		return
	end

	local yields = ResolveYields(counts)
	local catalystCost = Config.CatalystCostPerItem * total

	local lines = {
		string.format(TextManager.Get("touhou.recycler.analyze.tooltip").Value, total) .. BuildYieldText(yields),
		string.format(TextManager.Get("touhou.recycler.analyze.cost").Value, catalystCost),
		string.format(TextManager.Get("touhou.recycler.analyze.time").Value, GetProcessingTime(total)),
	}

	local neededSlots, missingOutput = GetNeededSlots(yields)
	if neededSlots == nil then
		table.insert(lines, string.format(TextManager.Get("touhou.recycler.missingoutput").Value, tostring(missingOutput)))
	else
		local freeSlots = CountFreeSlots(outputInventory)
		if freeSlots < neededSlots then
			table.insert(lines, string.format(TextManager.Get("touhou.recycler.nooutputspace").Value, neededSlots, freeSlots))
		end
	end

	local hasCatalyst, catalystAvailable = GetCatalystStatus(catalystInventory)
	if not hasCatalyst then
		table.insert(lines, TextManager.Get("touhou.recycler.nocatalyst").Value)
	elseif catalystAvailable < catalystCost then
		table.insert(lines, string.format(TextManager.Get("touhou.recycler.nocatalystcondition").Value, catalystCost - catalystAvailable))
	end

	Notify(GetMachineName(), table.concat(lines, "\n"), user)
end)

-- 启动建表：**固定回收表**（不再从 XML 读配方——实测 LuaCs 下读不到，而数量本来就是固定的；
-- 上面那几个 ReadXXX 函数保留作参考但已不再调用）
local initOk, initErr = pcall(function()
	for _, data in ipairs(FixedRecipes) do
		RegisterLevelTag(data.tag, data.output, data.amount)
	end
	if Config.VerboseLogs then print("[Touhou.Recycler] 回收表已装载 " .. #levelTagOrder .. " 条") end
end)
if not initOk then
	print("[Touhou.Recycler] 错误：回收表装载失败：" .. tostring(initErr))
end
