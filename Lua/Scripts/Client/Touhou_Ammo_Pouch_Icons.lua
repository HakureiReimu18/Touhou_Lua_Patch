--[[东方-弹药袋图标（客户端半区）
    在弹药袋的物品栏图标上，把袋内的「待发实体弹」画出来（照 Workshop「Storage Icons」2920200449 的做法）：
      · 1 枚 → 居中铺满（= 袋子图标上显示的就是它装的是什么弹）
      · 2~5 枚 → 紧凑网格（最多 3 列，见 GRID_MAX_COLS 的说明）
    做法：Hook.Patch("Barotrauma.Inventory", "DrawSlot", ..., After) —— 某个槽位画的是弹药袋时，
    在它上面盖一层袋内子弹图标。图标与颜色只在"袋子内容变了"时算一次并缓存：
      · 内容变化由 inventoryPutItem / Inventory.RemoveItem 两个钩子即时标脏
      · 另有低频兜底扫描（THINK_SWEEP_INTERVAL 帧一次），任何绕过上面两个钩子的改动（含 C# 侧
        直接搬运/补弹）最迟两个心跳也会被发现
      · roundStart / roundEnd 清空缓存
    白名单是 6 个袋子 identifier（5 个弹种袋 + 空袋；空袋没有容器，自然不会画）。
    「补满」按钮产生的 Touhou_Ammo_Fill_Marker 是瞬时标记物，不算子弹，收集内容时直接跳过。
    只做表现，不改任何游戏状态。]]

if not CLIENT then
    return
end

-- 内容图标要读 VisualSlot 的 Rect / DrawOffset，该类型不是 LuaCs 默认注册的类型，得手动注册
-- （参考模组也是这么干的）。已经注册过时 RegisterType 会报错，忽略即可
pcall(function() LuaUserData.RegisterType("Barotrauma.VisualSlot") end)

-- 白名单：只有这 6 个袋子（空袋没有 ItemContainer，取不到内容自然不画）
local WHITELIST = {
    ["Touhou_Ammo_Pouch_Blank"]       = true,
    ["Touhou_Ammo_Pouch_Revolver"]    = true,
    ["Touhou_Ammo_Pouch_BlueRose"]    = true,
    ["Touhou_Ammo_Pouch_Anguish"]     = true,
    ["Touhou_Ammo_Pouch_Blunderbuss"] = true,
    ["Touhou_Ammo_Pouch_Winchester"]  = true,
}
local POUCH_TAG       = "Touhou_Ammo_Pouch"        -- 热路径前置筛选用的 tag（5 个弹种袋共用）
local FILL_MARKER_TAG = "Touhou_Ammo_Fill_Marker"  -- 「补满」按钮的标记物：不是子弹，不画

-- 观感参数（要微调只改这里）
local ITEM_SCALE           = 1.0    -- 单枚图标倍率：1.0 = 按格子铺满（照参考实现），调小则袋子露出更多
local MAX_ICON_SCALE       = 2.0    -- 单个图标的缩放上限（和游戏自己画物品图标的上限一致）
local ICON_MARGIN          = 10     -- 每个格子里留的边距（游戏画物品图标也用 10）
-- 多枚时最多铺这么多个列：物品 XML 的 itemsperrow="5" 说的是"容器 UI 里一排放 5 格"，
-- 而袋子只有一格、5 发是叠在同一格里的——直接按 5 列铺会把图标压到看不见，所以这里限定 3 列
-- （5 发 = 3+2 两排）。单枚时列数=1，就是"居中铺满"。
local GRID_MAX_COLS        = 3
local THINK_SWEEP_INTERVAL = 120    -- think 兜底扫描间隔（帧，约 2 秒）

-- item.ID -> { item=袋子, ids={内容实体ID…}, icons={ {sprite,color}… }, dirty=还要不要重算 }
local cache = {}
local LOG_PREFIX = "[东方弹药袋图标] "
local disabled = false   -- 绘制出错就整体停用（只打一条日志），绝不刷屏/拖垮帧率

----------------------------------------------------------------------
-- 缓存
----------------------------------------------------------------------

-- 袋里的待发实体弹：全都在袋子的 ItemContainer 里（v5 就一格，最多叠 N 发）
-- 返回按 ID 排序的列表（顺序稳定，画出来的位置才不会跳）
local function content_items(pouch)
    local items = {}
    local inv = pouch.OwnInventory
    if inv == nil then return items end
    for _, contained in pairs(inv.AllItemsMod) do
        if contained ~= nil and not contained.Removed then
            local isMarker = false
            local okTag, has = pcall(function() return contained.HasTag(FILL_MARKER_TAG) end)
            if okTag and has == true then isMarker = true end
            if not isMarker then
                items[#items + 1] = contained
            end
        end
    end
    table.sort(items, function(a, b) return a.ID < b.ID end)
    return items
end

-- 内容签名：只看实体 ID（子弹被吸收成耐久、补弹、拖走一发都会换 ID）
local function signature_of(items)
    local ids = {}
    for i = 1, #items do ids[i] = items[i].ID end
    return ids
end

local function same_ids(a, b)
    local n = #a
    if n ~= #b then return false end
    for i = 1, n do
        if a[i] ~= b[i] then return false end
    end
    return true
end

-- 取物品图标与颜色：优先 OverrideInventorySprite（游戏画物品图标时也是这个优先），
-- 其次 InventoryIcon（背包栏用的那个），再其次世界贴图 Sprite
local function item_icon(item)
    local sprite = nil
    pcall(function() sprite = item.OverrideInventorySprite end)
    if sprite == nil then
        local okP, prefab = pcall(function() return item.Prefab end)
        if okP and prefab ~= nil then
            local ok1, icon = pcall(function() return prefab.InventoryIcon end)
            if ok1 and icon ~= nil then sprite = icon end
            if sprite == nil then
                local ok2, spr = pcall(function() return prefab.Sprite end)
                if ok2 then sprite = spr end
            end
        end
    end
    if sprite == nil then return nil, nil end
    local okC, color = pcall(function() return item.GetSpriteColor() end)
    if not okC or color == nil then color = Color(255, 255, 255, 255) end
    return sprite, color
end

local function build_entry(pouch)
    local entry = {
        item = pouch,
        ids = {},
        icons = {},
        dirty = false,
    }
    local ok, err = pcall(function()
        local items = content_items(pouch)
        entry.ids = signature_of(items)
        for i = 1, #items do
            local sprite, color = item_icon(items[i])
            if sprite ~= nil then
                entry.icons[#entry.icons + 1] = { sprite = sprite, color = color }
            end
        end
    end)
    if not ok then
        print(LOG_PREFIX .. "读取袋内子弹失败：" .. tostring(err))
    end
    cache[pouch.ID] = entry
    return entry
end

-- 标脏：只对白名单里的袋子动作（钩子是全游戏范围的，参数可能是角色、别的容器、任何物品）
-- 全部读操作都套在 pcall 里：Owner 是角色（没有 Prefab）时静默跳过，不影响游戏
local function mark_dirty(item)
    if item == nil then return end
    local ok, identifier = pcall(function()
        if item.Removed then return nil end
        return item.Prefab.Identifier.Value
    end)
    if not ok or WHITELIST[identifier] ~= true then return end
    local entry = cache[item.ID]
    if entry ~= nil then entry.dirty = true end
end

----------------------------------------------------------------------
-- 绘制
----------------------------------------------------------------------

-- 格子内铺满缩放：照游戏 DrawSlot 的算法（留边距 + 上限 MAX_ICON_SCALE），再乘倍率
local function fit_scale(cell_w, cell_h, sprite, multiplier)
    local size = sprite.size
    local scale = math.min(MAX_ICON_SCALE,
        (cell_w - ICON_MARGIN) / size.X,
        (cell_h - ICON_MARGIN) / size.Y)
    scale = scale * multiplier
    if scale <= 0.01 then return 0 end   -- 格子小到画不下，宁可不画
    return scale
end

-- 待发弹：1 枚 = 居中铺满；多枚 = 最多 GRID_MAX_COLS 列的网格，整体居中
local function draw_bullets(spriteBatch, rect, drawOffset, cached)
    local n = #cached.icons
    if n == 0 then return end

    local cols = math.min(n, GRID_MAX_COLS)
    local rows = math.ceil(n / cols)
    local cell_w = rect.Width / cols
    local cell_h = rect.Height / rows
    local center = Vector2.Add(rect.Center.ToVector2(), drawOffset)

    for i = 1, n do
        local icon = cached.icons[i]
        local col = (i - 1) % cols
        local row = math.floor((i - 1) / cols)
        local scale = fit_scale(cell_w, cell_h, icon.sprite, ITEM_SCALE)
        if scale > 0 then
            local pos = Vector2.Add(center, Vector2(
                cell_w * (col + 0.5) - rect.Width / 2,
                cell_h * (row + 0.5) - rect.Height / 2))
            icon.sprite.Draw(spriteBatch, pos, icon.color, 0, scale)
        end
    end
end

-- DrawSlot 里的实际工作（只有白名单里的袋子会走到这里，外面套了一次 pcall，见下面的补丁）
local function draw_pouch_slot(ptable)
    local item = ptable["item"]
    -- 白名单就只有那几个袋子：tag 命中之后还要再核一次 identifier，tag 被别的模组复用也不会画错
    if item.Prefab == nil or WHITELIST[item.Prefab.Identifier.Value] ~= true then return end
    if item.OwnInventory == nil then return end

    local slot = ptable["slot"]
    if slot == nil then return end

    local entry = cache[item.ID]
    if entry == nil or entry.dirty then
        entry = build_entry(item)
    end
    if #entry.icons == 0 then return end

    draw_bullets(ptable["spriteBatch"], slot.Rect, slot.DrawOffset, entry)
end

-- public static void DrawSlot(SpriteBatch, Inventory, VisualSlot, Item, int, bool, InvSlotType)
-- 热路径：每帧每个槽位都会进来一次，前置筛选只用普通字段读取（不会抛），
-- 只有确认是弹药袋才进 pcall（里面要读 VisualSlot 的字段，那是唯一可能因类型未注册而失败的地方）
Hook.Patch("Barotrauma.Inventory", "DrawSlot", function(_, ptable)
    if disabled then return end
    local item = ptable["item"]
    if item == nil or item.Removed then return end
    if item.HasTag(POUCH_TAG) ~= true then return end
    -- 游戏没画这个物品的图标（比如容器内容物已经展开）时也不盖
    if ptable["drawItem"] == false then return end

    local ok, err = pcall(draw_pouch_slot, ptable)
    if not ok then
        disabled = true
        print(LOG_PREFIX .. "绘制失败，图标补丁已停用：" .. tostring(err))
    end
end, Hook.HookMethodType.After)

----------------------------------------------------------------------
-- 标脏：内容变化的两个来源
----------------------------------------------------------------------

-- 有东西被放进某个容器（inventory.Owner = 容器物品）
Hook.Add("inventoryPutItem", "TLE_AmmoPouchIcons_put", function(inventory, item)
    if disabled or inventory == nil then return end
    mark_dirty(inventory.Owner)   -- 袋子自己
    mark_dirty(item)              -- 放进去的是另一个袋子时也刷它
end)

-- 有东西离开某个容器：子弹被拖走/打掉/被 C# 吸收或补弹时都会走这里
-- （参考实现同样补这个钩子：有些搬运路径不会触发 inventoryPutItem）
Hook.Patch("Barotrauma.Inventory", "RemoveItem", function(_, ptable)
    if disabled then return end
    local ok = pcall(function()
        local item = ptable["item"]
        if item == nil then return end
        local parent = item.ParentInventory
        if parent == nil then return end
        mark_dirty(parent.Owner)   -- 空袋子的图标要变回袋子本身
    end)
    if not ok then return end
end, Hook.HookMethodType.Before)

----------------------------------------------------------------------
-- 兜底扫描 + 缓存清理
----------------------------------------------------------------------

local tick = 0
Hook.Add("think", "TLE_AmmoPouchIcons_sweep", function()
    if disabled then return end
    tick = tick + 1
    if tick < THINK_SWEEP_INTERVAL then return end
    tick = 0
    for id, entry in pairs(cache) do
        local pouch = entry.item
        if pouch == nil or pouch.Removed then
            cache[id] = nil
        else
            local ok, ids = pcall(function() return signature_of(content_items(pouch)) end)
            if not ok or not same_ids(ids, entry.ids) then
                entry.dirty = true
            end
        end
    end
end)

Hook.Add("roundStart", "TLE_AmmoPouchIcons_clearStart", function() cache = {} end)
Hook.Add("roundEnd", "TLE_AmmoPouchIcons_clearEnd", function() cache = {} end)
