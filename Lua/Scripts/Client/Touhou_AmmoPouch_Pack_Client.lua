--[[东方-空弹药袋（客户端半区）
    把空弹药袋拿在手里（任一手）→ 自动弹出「选择弹药种类」窗口；点一种袋子 → 确认子窗口
    （大图 + 名字 + 存量上限 + 装什么弹）→ 确认后空袋被换成对应弹种的弹药袋。
    只认「空弹药袋」：已经选定弹种的弹药袋（Touhou_Ammo_Pouch_*）拿在手上、插在武器上、
    放在背包里都不会弹窗——判定用 tag/identifier 精确匹配，不做前缀匹配，不会把弹种袋误判成空袋。
    手持期间 Esc/关闭键关窗后不再自动弹，收起再拿出会重新弹（跟装束包一个规矩）。
    可选清单来自 AmmoPouch_Pack_Items.lua 的收录表（TLE_AMMOPOUCH_PACK_ITEMS），新增弹种改表即可，
    本文件不用动。窗口里所有字符串都走 TextManager.Get（键名 touhou.ammopouch.*），不硬编码中文。
    单机直接调 TLE.AmmoPouchPack.Claim 结算；联机发 TLE_AP_CLAIM 由服务端权威结算后同步背包。
    触发不走 XML LuaHook（那条链路在本补丁没验证过），用 think 轮询手持物，跟装束包同款思路。]]

-- 纯服务端没有客户端 UI
if not CLIENT then
    return
end

local MSG_CLAIM = "TLE_AP_CLAIM"
local BLANK_TAG = "Touhou_Ammo_Pouch_Blank"

-- 空袋 identifier：优先取收录表，表没加载时退回默认值（保证手持判定不失效）
local function blank_identifier()
    if TLE_AMMOPOUCH_PACK_ITEMS ~= nil and type(TLE_AMMOPOUCH_PACK_ITEMS.blank) == "string" then
        return TLE_AMMOPOUCH_PACK_ITEMS.blank
    end
    return "Touhou_Ammo_Pouch_Blank"
end

-- 文本一律走 TextManager（不硬编码中文）：取不到就退回键名本身，界面不会因为缺文本而崩
local function T(key)
    local ok, s = pcall(function() return TextManager.Get(key).Value end)
    if ok and type(s) == "string" and s ~= "" then return s end
    return key
end

-- 物品显示名：entityname.<小写 identifier> → ItemPrefab.Name → identifier
-- （收录表里的 name_key 由 build_list 先试，这条是兜底）
local function prefab_display_name(identifier)
    local ok, name = pcall(function() return TextManager.Get("entityname." .. string.lower(identifier)).Value end)
    if ok and type(name) == "string" and name ~= "" then return name end
    local okP, prefab = pcall(function() return ItemPrefab.Prefabs[identifier] end)
    if okP and prefab ~= nil then
        local okN, prefabName = pcall(function() return prefab.Name.Value end)
        if okN and type(prefabName) == "string" and prefabName ~= "" then return prefabName end
    end
    return tostring(identifier)
end

-- 取物品图标：客户端 ItemPrefab 有专门的 InventoryIcon 属性（对应 <InventoryIcon> 元素，
-- 游戏背包栏显示的就是它），没配置时为 nil，回退世界贴图 Sprite。全程 pcall，取不到返回 nil
local function get_item_icon(identifier)
    local okP, prefab = pcall(function() return ItemPrefab.Prefabs[identifier] end)
    if not okP or prefab == nil then return nil end
    local ok1, icon = pcall(function() return prefab.InventoryIcon end)
    if ok1 and icon ~= nil then return icon end
    local ok2, spr = pcall(function() return prefab.Sprite end)
    if ok2 and spr ~= nil then return spr end
    return nil
end

-- 富文本标签（‖color:r,g,b,a‖）在 GUITextBlock 里不渲染，去掉标签只留纯文本
local function strip_rich(name)
    return (string.gsub(name, "‖[^‖]*‖", ""))
end

-- 按收录表生成清单（名称开窗时才解析，代价可忽略）
local function build_list()
    local list = {}
    local entries = (TLE_AMMOPOUCH_PACK_ITEMS ~= nil) and TLE_AMMOPOUCH_PACK_ITEMS.pouches or nil
    if entries == nil then return list end
    for _, entry in ipairs(entries) do
        local name = T(entry.name_key)
        if name == entry.name_key then
            -- name_key 没文本（新增弹种忘了补文本）：退回物品自己的名字
            name = prefab_display_name(entry.identifier)
        end
        table.insert(list, {
            id       = entry.identifier,
            name     = strip_rich(tostring(name)),
            capacity = tonumber(entry.capacity) or 0,
            ammo     = prefab_display_name(entry.ammo),
        })
    end
    return list
end

----------------------------------------------------------------------
-- 窗口状态
----------------------------------------------------------------------

local menu_frame = nil
local confirm_frame = nil     -- 确认子窗口（选中一种后弹出，显示大图 + 名字 + 存量上限，确认后才结算）
local current_blank_id = nil  -- 空袋实体 ID（发送结算用，也是窗口还该不该开着的凭据）
local current_list = {}
local close_was_down = false

-- 手持检测用的状态/函数提前声明（后面才赋值，供窗口按钮回调捕获）
local last_held_id = nil       -- 上一轮检测到的手持空袋实体 ID（nil = 没拿）
local dismissed_id = nil       -- 手持期间被用户关过窗的空袋 ID：不再自动弹，收起再拿出会重置
local held_blank = nil         -- function(character) -> 手持的空袋 item 或 nil

local function close_window()
    if menu_frame ~= nil then
        GUI.GUI.RemoveFromUpdateList(menu_frame, true)
        menu_frame.RectTransform.Parent = nil
        menu_frame = nil
    end
end

local function close_confirm()
    if confirm_frame ~= nil then
        GUI.GUI.RemoveFromUpdateList(confirm_frame, true)
        confirm_frame.RectTransform.Parent = nil
        confirm_frame = nil
    end
end

-- 用户主动关窗（Esc 键或关闭按钮）：记住还拿着的空袋，别再立刻自动弹
local function dismiss_window(held)
    close_window()
    close_confirm()
    current_blank_id = nil
    if held ~= nil then dismissed_id = held.ID end
end

-- 确认换取：单机本地结算，联机发请求给服务端
local function send_claim(pouch_identifier)
    local blank_id = current_blank_id
    close_window()
    close_confirm()
    current_blank_id = nil
    if blank_id == nil or pouch_identifier == nil then return end

    if Game.IsSingleplayer then
        if TLE.AmmoPouchPack ~= nil then
            TLE.AmmoPouchPack.Claim(Character.Controlled, blank_id, pouch_identifier)
        end
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_CLAIM)
        msg.WriteUInt16(blank_id)
        msg.WriteString(pouch_identifier)
        Networking.Send(msg)
    end)
end

----------------------------------------------------------------------
-- 窗口构件
----------------------------------------------------------------------

local function add_text(parent_rt, rel_size, text, alignment)
    local block = GUI.TextBlock(GUI.RectTransform(rel_size, parent_rt), RichString.Plain(RawLString(text)))
    block.TextAlignment = alignment or GUI.Alignment.Center
    return block
end

-- 画物品图标，双保险：先试 GUI.Image 直接吃 Sprite 的构造重载，失败再白色底 + 赋 Sprite 属性
local function add_icon(parent_rt, rel_size, icon)
    if icon == nil then return false end
    local ok = pcall(function() GUI.Image(GUI.RectTransform(rel_size, parent_rt, GUI.Anchor.Center), icon) end)
    if ok then return true end
    return pcall(function()
        local img = GUI.Image(GUI.RectTransform(rel_size, parent_rt, GUI.Anchor.Center), Color(255, 255, 255, 255))
        img.Sprite = icon
    end)
end

-- 横向行（跟装束包同款：小间距 + 左中对齐，防按钮偏出框）
local function add_row(parent_rt, height)
    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, height), parent_rt), true, GUI.Anchor.CenterLeft)
    row.RelativeSpacing = 0.006
    return row
end

-- 确认子窗口：点选清单里的袋子后弹出，显示大图标 + 名字 + 存量上限 + 装什么弹，确认才结算
local function build_confirm_window(entry)
    close_confirm()
    local frame = GUI.Frame(GUI.RectTransform(Vector2(0.22, 0.5), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")
    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.9, 0.95), frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.015
    layout.Stretch = true

    add_text(layout.RectTransform, Vector2(1, 0.1), entry.name, GUI.Alignment.Center)

    -- 大图标（占窗口中部一大块；取不到图标时留文字占位）
    local icon_ok = add_icon(layout.RectTransform, Vector2(0.8, 0.38), get_item_icon(entry.id))
    if not icon_ok then
        add_text(layout.RectTransform, Vector2(1, 0.38), T("touhou.ammopouch.window.noicon"), GUI.Alignment.Center)
    end

    add_text(layout.RectTransform, Vector2(1, 0.08),
        string.format(T("touhou.ammopouch.info.capacity"), entry.capacity), GUI.Alignment.Center)
    add_text(layout.RectTransform, Vector2(1, 0.08),
        string.format(T("touhou.ammopouch.info.ammo"), entry.ammo), GUI.Alignment.Center)

    local btns = add_row(layout.RectTransform, 0.12)
    local ok = GUI.Button(GUI.RectTransform(Vector2(0.48, 1), btns.RectTransform), RawLString(T("touhou.ammopouch.window.confirm")))
    ok.OnClicked = function()
        send_claim(entry.id)
        return true
    end
    local back = GUI.Button(GUI.RectTransform(Vector2(0.48, 1), btns.RectTransform), RawLString(T("touhou.ammopouch.window.cancel")))
    back.OnClicked = function()
        close_confirm()
        return true
    end

    confirm_frame = frame
end

-- 主窗口：一列 5 行（图标 + 名字按钮 + 存量上限），收录表为空时显示兜底文本
local function build_window()
    local frame = GUI.Frame(GUI.RectTransform(Vector2(0.46, 0.56), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")
    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.92, 0.96), frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.012
    layout.Stretch = true

    add_text(layout.RectTransform, Vector2(1, 0.09), T("touhou.ammopouch.window.title"), GUI.Alignment.Center)

    if #current_list == 0 then
        add_text(layout.RectTransform, Vector2(1, 0.5), T("touhou.ammopouch.window.empty"), GUI.Alignment.Center)
    else
        for _, entry in ipairs(current_list) do
            local row = add_row(layout.RectTransform, 0.11)
            -- 图标（取不到就留白，按钮宽度足够）
            add_icon(row.RectTransform, Vector2(0.16, 1), get_item_icon(entry.id))
            -- 名字按钮：点选后弹确认子窗口（大图 + 名字 + 存量上限），确认才结算
            local btn = GUI.Button(GUI.RectTransform(Vector2(0.55, 1), row.RectTransform), RawLString(entry.name))
            btn.OnClicked = function()
                build_confirm_window(entry)
                return true
            end
            -- 存量上限
            add_text(row.RectTransform, Vector2(0.28, 1),
                string.format(T("touhou.ammopouch.info.capacity.short"), entry.capacity), GUI.Alignment.CenterLeft)
        end
    end

    add_text(layout.RectTransform, Vector2(1, 0.06), T("touhou.ammopouch.window.hint"), GUI.Alignment.CenterLeft)

    -- 关闭：效果和 Esc 一致（记住 dismiss，不立刻重弹）
    local close_row = add_row(layout.RectTransform, 0.1)
    local close_btn = GUI.Button(GUI.RectTransform(Vector2(1, 1), close_row.RectTransform), RawLString(T("touhou.ammopouch.window.close")))
    close_btn.OnClicked = function()
        dismiss_window(held_blank(Character.Controlled))
        return true
    end

    menu_frame = frame
end

local function open_window(blank_id)
    close_window()
    close_confirm()
    current_blank_id = blank_id
    current_list = build_list()
    build_window()
end

----------------------------------------------------------------------
-- 手持检测：拿在手里就弹窗（GetItemInLimbSlot + tag/identifier 精确判定，跟装束包同款思路）
-- （状态变量在前面已提前声明，这里只给 held_blank 赋值）
----------------------------------------------------------------------

-- 手里拿的是不是「空袋」：先看 tag（物品 XML 里的 tags="…,Touhou_Ammo_Pouch_Blank,…"），
-- tag 没打上时再按 identifier 精确比对。两处都是全等比较，弹种袋（Touhou_Ammo_Pouch_Revolver 等）
-- 既没有这个 tag 也不等于这个 identifier，不会被误判成空袋
local function is_blank(item)
    if item == nil or item.Removed then return false end
    local okTag, has = pcall(function() return item.HasTag(BLANK_TAG) end)
    if okTag and has == true then return true end
    local okId, identifier = pcall(function() return item.Prefab.Identifier.Value end)
    return okId and identifier == blank_identifier()
end

local function held_blank_impl(character)
    if character == nil or character.Inventory == nil then return nil end
    for _, slot in ipairs({ InvSlotType.RightHand, InvSlotType.LeftHand }) do
        local item = character.Inventory.GetItemInLimbSlot(slot)
        if is_blank(item) then
            return item
        end
    end
    return nil
end
held_blank = held_blank_impl

-- 一次性探测 Esc 键盘状态读取是否可用（同装束包的探测器写法）：探测通过后直连，
-- 不再每帧套 pcall 闭包；探测失败就一直走 pcall 兜底（每帧还会再探，API 迟到了也能接上）
local esc_api_ok = nil   -- true = 直连可用，nil = 还没探测成功

local function esc_down()
    if esc_api_ok == true then
        local state = PlayerInput.GetKeyboardState
        local key = (Keys ~= nil) and Keys.Escape or nil
        if state == nil or key == nil then return false end
        return state.IsKeyDown(key) == true
    end
    local ok, down = pcall(function()
        return PlayerInput.GetKeyboardState.IsKeyDown(Keys.Escape)
    end)
    if ok then esc_api_ok = true end
    return ok and down == true
end

local tick = 0
Hook.Add("think", "TLE_AmmoPouchPack_client", function()
    local character = Character.Controlled

    -- 窗口每帧重新加入 GUI 更新列表才画得出来（跟暂停菜单一样），必须放在所有 return 分支之前
    if menu_frame ~= nil then
        menu_frame.AddToGUIUpdateList(false, 1)
    end
    if confirm_frame ~= nil then
        confirm_frame.AddToGUIUpdateList(false, 2)  -- 层级比主窗高，盖在上面
    end

    -- Esc 边沿：先关确认子窗口，再关主窗口。没窗口时连键盘都不查，省掉每帧的查询
    if menu_frame ~= nil or confirm_frame ~= nil then
        local down = esc_down()
        if down and not close_was_down then
            if confirm_frame ~= nil then
                close_confirm()
            elseif menu_frame ~= nil then
                dismiss_window(held_blank(character))
            end
        end
        close_was_down = down
    end

    -- 手持检测低频轮询（10 帧一次）：拿/收/换手都算，够快又不费
    tick = tick + 1
    if tick < 10 then return end
    tick = 0

    if character == nil or character.IsDead then
        close_window()
        close_confirm()
        current_blank_id = nil
        dismissed_id = nil
        last_held_id = nil
        return
    end

    local held = held_blank(character)
    local held_id = (held ~= nil) and held.ID or nil

    -- 手里的空袋换了（拿出/收起/换手都算），重置"关过窗"标记
    if held_id ~= last_held_id then
        dismissed_id = nil
        last_held_id = held_id
    end

    if menu_frame ~= nil then
        -- 窗口开着但空袋不在手里了 → 关掉（确认子窗口一并关）
        if held_id == nil or held_id ~= current_blank_id then
            close_window()
            close_confirm()
            current_blank_id = nil
        end
        return
    end
    -- 主窗没了，确认子窗口也不该残留
    if confirm_frame ~= nil then close_confirm() end

    -- 手里拿着空袋、且这轮还没被用户关过窗 → 弹窗
    if held ~= nil and dismissed_id ~= held.ID then
        open_window(held.ID)
    end
end)
