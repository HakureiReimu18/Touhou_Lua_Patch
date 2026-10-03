--[[东方-装束包（客户端半区）
    把装束包拿在手里（任一手）→ 自动弹出选装束窗口；点一件换取，包随之消失。
    手持期间 Esc/关闭键关窗后不再自动弹，收起再拿出会重新弹。
    可选清单来自 CostumePack_Items.lua 的收录表（TLE_COSTUME_PACK_ITEMS），新增装束改表即可。
    单机直接调 TLE.CostumePack.Claim 结算；联机发 TLE_CP_CLAIM 由服务端权威结算后同步背包。
    触发不走 XML LuaHook（那条链路在本补丁没验证过），用 think 轮询手持物，跟快捷键脚本同款思路。]]

-- 纯服务端没有客户端 UI
if not CLIENT then
    return
end

local PACK_BASIC   = "Touhou_Costume_Pack"
local PACK_PLUS    = "Touhou_Costume_Pack_Plus"
local PACK_HEAD    = "Touhou_Costume_Pack_Headwear"
local PACK_TAG     = "touhou_costumepack"
local MSG_CLAIM    = "TLE_CP_CLAIM"

-- 包 identifier → 清单分类
local PACK_CATEGORY = {
    [PACK_BASIC] = "basic",
    [PACK_PLUS]  = "plus",
    [PACK_HEAD]  = "headwear",
}

-- 语言跟游戏设置走，LANGUAGE_OVERRIDE 可强制。加语言就复制一个块改值，键名别动
local LANGUAGE_OVERRIDE = nil

local L = {
    ["Simplified Chinese"] = {
        title_basic = "选择装束",
        title_plus  = "选择装束+",
        title_head  = "选择头饰",
        empty = "（收录表是空的，请检查 CostumePack_Items.lua）",
        hint = "点击一件换取，包随之消失",
        prev_page = "上一页",
        next_page = "下一页",
        page_format = "第 %d / %d 页",
        close = "关闭 (Esc)",
        confirm = "确认换取",
        cancel = "返回",
        no_icon = "（无图标）",
    },
    ["English"] = {
        title_basic = "Select Outfit",
        title_plus  = "Select Outfit+",
        title_head  = "Select Headwear",
        empty = "(Item list is empty, check CostumePack_Items.lua)",
        hint = "Click an item to claim it. The pack is consumed.",
        prev_page = "Prev",
        next_page = "Next",
        page_format = "Page %d / %d",
        close = "Close (Esc)",
        confirm = "Claim",
        cancel = "Back",
        no_icon = "(no icon)",
    },
}

local LANGUAGE = LANGUAGE_OVERRIDE
if LANGUAGE == nil or L[LANGUAGE] == nil then
    local ok, lang = pcall(function() return tostring(GameSettings.CurrentConfig.Language) end)
    if ok and lang ~= nil and L[lang] ~= nil then
        LANGUAGE = lang
    else
        LANGUAGE = "Simplified Chinese"
    end
end

local function T(key)
    local pack = L[LANGUAGE]
    if pack ~= nil and pack[key] ~= nil then return pack[key] end
    return L["Simplified Chinese"][key] or key
end

-- 手动解析 ‖color:r,g,b,a‖文本‖end‖：GUITextBlock 不渲染富文本标签，只能取出颜色自己设 TextColor
local function parse_rich(name)
    local r, g, b, a = string.match(name, "^‖color:(%d+),(%d+),(%d+),(%d+)‖")
    if r ~= nil then
        return Color(tonumber(r), tonumber(g), tonumber(b), tonumber(a))
    end
    return Color(255, 255, 255, 255)
end

local function display_name(identifier)
    local name = TextManager.Get("entityname." .. string.lower(identifier)).Value
    if name == "" then
        local ok, prefabName = pcall(function() return ItemPrefab.Prefabs[identifier].Name.Value end)
        if ok and prefabName ~= nil and prefabName ~= "" then
            name = prefabName
        else
            name = identifier
        end
    end
    -- 返回纯文本 + 解析出的颜色（GUITextBlock 不渲染富文本，颜色由 parse_rich 提取后手动设）
    return string.gsub(name, "‖[^‖]*‖", ""), parse_rich(name)
end

-- 可读性方案：不搞叠层描边（会有字间距问题），直接把按钮底色压成深灰，
-- 文字用物品自带的解析色。任何颜色的名字在深灰底上都清晰
local BTN_TINT = Color(110, 110, 110, 255)
local function style_colored_button(btn, name, color)
    pcall(function()
        btn.TextBlock.Text = RichString.Plain(RawLString(name))
        btn.TextBlock.TextColor = color
    end)
    btn.Color = BTN_TINT
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

-- 按收录表生成分类清单（名称开窗时才解析，代价可忽略）
local function build_list(category)
    local list = {}
    local ids = (TLE_COSTUME_PACK_ITEMS ~= nil) and TLE_COSTUME_PACK_ITEMS[category] or nil
    if ids == nil then return list end
    for _, identifier in ipairs(ids) do
        local name, color = display_name(identifier)
        table.insert(list, { id = identifier, name = name, color = color })
    end
    table.sort(list, function(a, b) return a.name < b.name end)
    return list
end

----------------------------------------------------------------------
-- 窗口
----------------------------------------------------------------------

local menu_frame = nil
local confirm_frame = nil   -- 确认子窗口（选中某件后弹出，显示大图 + 名字，确认后才结算）
local current_pack_id = nil   -- 装束包实体 ID（发送结算用，也是窗口还该不该开着的凭据）
local current_list = {}
local current_page = 1
local COLS = 2        -- 每行两列，翻页次数减半
local ROWS = 6
local PAGE_SIZE = COLS * ROWS
local esc_was_down = false

-- 手持检测用的状态/函数提前声明（后面才赋值，供窗口按钮回调捕获）
local last_held_pack_id = nil  -- 上一轮检测到的手持包实体 ID（nil = 没拿）
local dismissed_id = nil       -- 手持期间被用户关过窗的包 ID：不再自动弹，收起再拿出会重置
local held_pack = nil          -- function(character) -> 手持的包 item 或 nil

local function close_window()
    if menu_frame ~= nil then
        GUI.GUI.RemoveFromUpdateList(menu_frame, true)
        menu_frame.RectTransform.Parent = nil
        menu_frame = nil
    end
end

-- 用户主动关窗（Esc 键或关闭按钮）：记住还拿着的包，别再立刻自动弹
local function dismiss_window(held)
    close_window()
    current_pack_id = nil
    if held ~= nil then dismissed_id = held.ID end
end

local function send_claim(outfit_id)
    local pack_id = current_pack_id
    close_window()
    current_pack_id = nil
    if Game.IsSingleplayer then
        if TLE.CostumePack ~= nil then
            TLE.CostumePack.Claim(Character.Controlled, pack_id, outfit_id)
        end
        return
    end
    pcall(function()
        local msg = Networking.Start(MSG_CLAIM)
        msg.WriteUInt16(pack_id)
        msg.WriteString(outfit_id)
        Networking.Send(msg)
    end)
end

local function add_text(parent_rt, rel_size, text, alignment)
    local block = GUI.TextBlock(GUI.RectTransform(rel_size, parent_rt), RichString.Plain(RawLString(text)))
    block.TextAlignment = alignment or GUI.Alignment.Center
    return block
end

-- 带颜色的文字（确认窗口标题用）
local function add_colored_text(parent_rt, rel_size, text, color, alignment)
    local block = add_text(parent_rt, rel_size, text, alignment)
    block.TextColor = color
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

-- 横向行（跟快捷键脚本的 add_row 同款：小间距 + 左中对齐，防按钮偏出框）
local function add_row(parent_rt, height)
    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, height), parent_rt), true, GUI.Anchor.CenterLeft)
    row.RelativeSpacing = 0.006
    return row
end

-- 确认子窗口：点选列表里的装束后弹出，显示大图标 + 彩色名字，确认才结算
local function close_confirm()
    if confirm_frame ~= nil then
        GUI.GUI.RemoveFromUpdateList(confirm_frame, true)
        confirm_frame.RectTransform.Parent = nil
        confirm_frame = nil
    end
end

local function build_confirm_window(entry)
    close_confirm()
    local frame = GUI.Frame(GUI.RectTransform(Vector2(0.2, 0.42), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")
    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.9, 0.95), frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.02
    layout.Stretch = true

    -- 彩色名字
    add_colored_text(layout.RectTransform, Vector2(1, 0.12), entry.name, entry.color, GUI.Alignment.Center)

    -- 大图标（占窗口中部一大块；取不到图标时留文字占位）
    local icon_ok = add_icon(layout.RectTransform, Vector2(0.85, 0.55), get_item_icon(entry.id))
    if not icon_ok then
        add_text(layout.RectTransform, Vector2(1, 0.55), T("no_icon"), GUI.Alignment.Center)
    end

    -- 确认 / 返回
    local btns = add_row(layout.RectTransform, 0.12)
    local ok = GUI.Button(GUI.RectTransform(Vector2(0.48, 1), btns.RectTransform), RawLString(T("confirm")))
    ok.OnClicked = function()
        local id = entry.id
        close_confirm()
        send_claim(id)
        return true
    end
    local back = GUI.Button(GUI.RectTransform(Vector2(0.48, 1), btns.RectTransform), RawLString(T("cancel")))
    back.OnClicked = function()
        close_confirm()
        return true
    end

    confirm_frame = frame
end

local function build_window(category)
    local title_key = ({ basic = "title_basic", plus = "title_plus", headwear = "title_head" })[category] or "title_basic"
    local frame = GUI.Frame(GUI.RectTransform(Vector2(0.5, 0.62), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")
    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.92, 0.96), frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.008
    layout.Stretch = true

    add_text(layout.RectTransform, Vector2(1, 0.07), T(title_key), GUI.Alignment.Center)

    local page_count = math.max(1, math.ceil(#current_list / PAGE_SIZE))
    if current_page > page_count then current_page = page_count end

    if #current_list == 0 then
        add_text(layout.RectTransform, Vector2(1, 0.3), T("empty"), GUI.Alignment.Center)
    else
        local first = (current_page - 1) * PAGE_SIZE + 1
        local last = math.min(first + PAGE_SIZE - 1, #current_list)
        -- 两列网格：每行一个横向 LayoutGroup，塞最多两个按钮；末行不满则留空
        for r = 1, ROWS do
            local row = add_row(layout.RectTransform, 0.1)
            for c = 1, COLS do
                local idx = first + (r - 1) * COLS + (c - 1)
                if idx > last then break end
                local entry = current_list[idx]
                local btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(""))
                style_colored_button(btn, entry.name, entry.color)
                -- 点选后弹确认子窗口（大图 + 名字），确认才结算
                btn.OnClicked = function()
                    build_confirm_window(entry)
                    return true
                end
            end
        end
    end

    -- 底部：提示
    add_text(layout.RectTransform, Vector2(1, 0.05), T("hint"), GUI.Alignment.CenterLeft)

    -- 翻页栏：0.2 / 0.6 / 0.2 比例，跟快捷键脚本同款，不会出框
    local page_row = add_row(layout.RectTransform, 0.06)
    local prev = GUI.Button(GUI.RectTransform(Vector2(0.2, 1), page_row.RectTransform), RawLString(T("prev_page")))
    prev.OnClicked = function()
        current_page = current_page - 1
        if current_page < 1 then current_page = page_count end
        local pack_id = current_pack_id
        close_window()
        current_pack_id = pack_id
        build_window(category)
        return true
    end
    add_text(page_row.RectTransform, Vector2(0.6, 1), string.format(T("page_format"), current_page, page_count), GUI.Alignment.Center)
    local nextb = GUI.Button(GUI.RectTransform(Vector2(0.2, 1), page_row.RectTransform), RawLString(T("next_page")))
    nextb.OnClicked = function()
        current_page = current_page + 1
        if current_page > page_count then current_page = 1 end
        local pack_id = current_pack_id
        close_window()
        current_pack_id = pack_id
        build_window(category)
        return true
    end

    -- 关闭：效果和 Esc 一致（记住 dismiss，不立刻重弹）
    local close_row = add_row(layout.RectTransform, 0.06)
    local close_btn = GUI.Button(GUI.RectTransform(Vector2(1, 1), close_row.RectTransform), RawLString(T("close")))
    close_btn.OnClicked = function()
        dismiss_window(held_pack(Character.Controlled))
        return true
    end

    menu_frame = frame
end

local function open_window(pack_id, category)
    close_window()
    current_pack_id = pack_id
    current_list = build_list(category)
    current_page = 1
    build_window(category)
end

----------------------------------------------------------------------
-- 手持检测：拿在手里就弹窗（GetItemInLimbSlot + HasTag，跟玉佩耐久脚本同款）
-- （状态变量在前面已提前声明，这里只给 held_pack 赋值）
----------------------------------------------------------------------

local function held_pack_impl(character)
    if character == nil or character.Inventory == nil then return nil end
    for _, slot in ipairs({ InvSlotType.RightHand, InvSlotType.LeftHand }) do
        local item = character.Inventory.GetItemInLimbSlot(slot)
        if item ~= nil and not item.Removed and item.HasTag(PACK_TAG) then
            return item
        end
    end
    return nil
end
held_pack = held_pack_impl

local function esc_down()
    local ok, down = pcall(function()
        return PlayerInput.GetKeyboardState.IsKeyDown(Keys.Escape)
    end)
    return ok and down == true
end

local tick = 0
Hook.Add("think", "TLE_CostumePack_client", function()
    local character = Character.Controlled

    -- 窗口每帧重新加入 GUI 更新列表才画得出来（跟暂停菜单一样，快捷键脚本同款），
    -- 必须放在所有 return 分支之前
    if menu_frame ~= nil then
        pcall(function() menu_frame.AddToGUIUpdateList(false, 1) end)
    end
    if confirm_frame ~= nil then
        pcall(function() confirm_frame.AddToGUIUpdateList(false, 2) end)  -- 层级比主窗高，盖在上面
    end

    -- Esc 边沿：先关确认子窗口，再关主窗口
    local down = esc_down()
    if down and not esc_was_down then
        if confirm_frame ~= nil then
            close_confirm()
        elseif menu_frame ~= nil then
            dismiss_window(held_pack(character))
        end
    end
    esc_was_down = down

    tick = tick + 1
    if tick < 10 then return end
    tick = 0

    if character == nil or character.IsDead then
        if menu_frame ~= nil then close_window() end
        current_pack_id = nil
        dismissed_id = nil
        last_held_pack_id = nil
        return
    end

    local held = held_pack(character)
    local held_id = (held ~= nil) and held.ID or nil

    -- 手里的包换了（拿出/收起/换手都算），重置"关过窗"标记
    if held_id ~= last_held_pack_id then
        dismissed_id = nil
        last_held_pack_id = held_id
    end

    if menu_frame ~= nil then
        -- 窗口开着但包不在手里了 → 关掉（确认子窗口一并关）
        if held_id == nil or held_id ~= current_pack_id then
            close_window()
            close_confirm()
            current_pack_id = nil
        end
        return
    end
    -- 主窗没了，确认子窗口也不该残留
    if confirm_frame ~= nil then close_confirm() end

    -- 手里拿着包、且这轮还没被用户关过窗 → 弹窗
    if held ~= nil and dismissed_id ~= held.ID then
        local okId, pack_identifier = pcall(function() return tostring(held.Prefab.Identifier) end)
        local category = (okId and PACK_CATEGORY[pack_identifier]) or "basic"
        open_window(held.ID, category)
    end
end)
