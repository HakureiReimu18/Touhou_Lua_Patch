--[[东方-快捷键设置（武器改装 + 武器按钮 + 槽位技能 + 悬浮界面开关 + 统一设置窗口）
    功能：
      · 武器改装：手持带“改装”按钮（Text.Touhou_Open_Mod）的武器时，按快捷键触发改装；
      · 武器按钮1/2：触发手持武器上除“改装”外的其他按钮（如月能步枪/湛蓝玫瑰的射击模式切换）；
      · 装束技能1~4：触发装束槽（InnerClothes+Head）东方装束的第 1~4 个控件；
      · 外套按钮1/2、背包按钮1/2：触发外套槽（OuterClothes，如潜水服）、背包槽（Bag）的第 1/2 个控件；
        以上均支持按钮和复选框（复选框 = 翻转勾选状态，与鼠标点击完全一致）；
      · 装束/外套/背包界面显示：按槽位独立隐藏/显示该格子装备的悬浮 UI，互不影响；
      · 所有触发都走原版委托——单机直接执行；联机走原版 CreateClientEvent 通道由服务器权威执行。

    设置窗口「东方-快捷键设置」：
      · ESC 暂停菜单底部有入口按钮；也可按“界面开关键”（默认 K）随时开关，ESC 关闭窗口；
      · 绑定较多时分页显示（每页 5 条），底部翻页栏切换；
      · 每条绑定可独立设置：触发键（点击进入捕获模式后按新键，Esc 取消）、
        修饰键（Shift/Ctrl/Alt，可多选；带修饰键的绑定需按住对应修饰键才触发，
        多余按住的修饰键不影响无修饰键绑定——按住 Shift 奔跑也能用快捷键）、清除绑定；
      · 多条绑定允许共用同一按键（按下时同时触发，一键多用）；只有界面开关键必须唯一；
      · 槽位组绑定行会实时显示当前检测到的控件名称，方便确认对应关系；
      · 后续新的配置项统一在这个窗口里扩展。

    槽位组编号规则：装束 = InnerClothes+Head（仅 subcategory="Touhou" 的东方装备），
    外套 = OuterClothes（潜水服等），背包 = Bag；各组内按 XML 定义顺序编号
    （跳过无文本的残留/装饰控件，同名控件以最新重建的为准）。
    换装后编号自动重排，无需重新绑键。

    配置保存在存档目录 TouhouModHotkeyConfig.txt，旧版配置自动迁移，工坊更新模组不会丢配置。

    多语言：界面文本自动跟随游戏语言（GameSettings.CurrentConfig.Language），内置简体中文与 English；
    添加新语言只需复制脚本中 L 表的一个语言块并翻译（键名保持一致），
    也可用 LANGUAGE_OVERRIDE 强制指定语言。
]]
if not CLIENT then
    return
end

-- 注册并获取静态类（部分类型必须先 RegisterType 才能 CreateStatic；
-- Barotrauma.SaveUtil 在新版 LuaCs 中被明确禁止注册，拿不到就用兜底路径）
local function register_static(type_name)
    local ok, result = pcall(function()
        local t = LuaUserData.CreateStatic(type_name)
        if t == nil then
            pcall(function() LuaUserData.RegisterType(type_name) end)
            t = LuaUserData.CreateStatic(type_name)
        end
        return t
    end)
    if ok then return result end
    return nil
end

local File = register_static("Barotrauma.IO.File")
local Path = register_static("Barotrauma.IO.Path")
local SaveUtil = register_static("Barotrauma.SaveUtil")
local XnaPoint = LuaUserData.CreateStatic("Microsoft.Xna.Framework.Point", true)

--================ 常量 ================
local BUTTON_TEXT_TAG = "Text.Touhou_Open_Mod"  -- 武器改装按钮使用的文本标签
local CONFIG_FILE_NAME = "TouhouModHotkeyConfig.txt"
local DEBUG_LOG = false  -- 诊断日志开关（排障时改为 true，控制台会输出 [东方快捷键] 前缀的详细信息）

-- 触发枚举用的槽位组：不同格子的 UI 分开编号、互不影响
-- （模组潜水服也是 subcategory="Touhou"，所以不能靠类别区分，必须按槽位区分）
local SLOT_GROUPS = {
    wearable = { slots = { "InnerClothes", "Head" }, touhou_only = true },   -- 装束（含帽子）
    outer    = { slots = { "OuterClothes" },         touhou_only = false },  -- 外套/潜水服
    bag      = { slots = { "Bag" },                  touhou_only = false },  -- 背包
}

-- 悬浮界面显示开关的槽位组：target -> 控制的槽位 + 配置字段名 + 显示名（多语言键）
local HUD_GROUPS = {
    toggle_hud       = { slots = { "InnerClothes", "Head" }, flag = "hud_hidden",       display_key = "hud_outfit" },
    toggle_hud_outer = { slots = { "OuterClothes" },         flag = "hud_hidden_outer", display_key = "hud_outer" },
    toggle_hud_bag   = { slots = { "Bag" },                  flag = "hud_hidden_bag",   display_key = "hud_bag" },
}

-- 装束精确匹配：只认 subcategory 为 "Touhou" 的装备（东方模组装束的共同特征），
-- 避免误扫潜水服等同样带 CustomInterface 按钮的原版/其他模组服装
local OUTFIT_SUBCATEGORY = "Touhou"

-- 可选：额外的装束 identifier / tag 白名单（subcategory 不是 Touhou 的例外装束加在这里）
local OUTFIT_IDENTIFIERS = {}  -- 例如 { "Kokoro_Mask01" }
local OUTFIT_TAGS = {}         -- 例如 { "Touhou_Clothes" }

-- 可选：限制哪些武器允许用快捷键触发改装（填 identifier 或 tag；两者都留空 = 任何带“改装”按钮的武器）
local ALLOWED_IDENTIFIERS = {}  -- 例如 { "Touhou_Monarch", "Touhou_Deceit" }
local ALLOWED_TAGS = {}         -- 例如 { "Touhou_Mod_Weapon" }（需要 XML 里给武器加对应 tag）

-- 允许绑定的按键白名单（XNA Keys 枚举名称）
local KEY_LIST = {}
for i = 65, 90 do KEY_LIST[#KEY_LIST + 1] = string.char(i) end       -- A-Z
for i = 0, 9 do KEY_LIST[#KEY_LIST + 1] = "D" .. i end               -- 数字键 0-9（主键盘）
for i = 1, 12 do KEY_LIST[#KEY_LIST + 1] = "F" .. i end              -- F1-F12
for i = 0, 9 do KEY_LIST[#KEY_LIST + 1] = "NumPad" .. i end          -- 小键盘数字 0-9
for _, n in ipairs({ "Add", "Subtract", "Multiply", "Divide", "Decimal" }) do
    KEY_LIST[#KEY_LIST + 1] = n                                       -- 小键盘运算符 + - * / .
end
for _, n in ipairs({ "Up", "Down", "Left", "Right", "Insert", "Delete", "Home", "End", "PageUp", "PageDown" }) do
    KEY_LIST[#KEY_LIST + 1] = n                                       -- 方向键与编辑键
end

local MODIFIER_NAMES = { "LeftShift", "LeftControl", "LeftAlt" }
local MODIFIER_DISPLAY = { LeftShift = "Shift", LeftControl = "Ctrl", LeftAlt = "Alt" }

--================ 多语言文本 ================
-- 语言自动跟随游戏设置（GameSettings.CurrentConfig.Language）；
-- 也可用 LANGUAGE_OVERRIDE 强制指定（填语言名如 "English"，nil = 自动跟随）。
-- 添加新语言：复制一个语言块并翻译所有值，键名保持一致即可，无需改动其他代码。
local LANGUAGE_OVERRIDE = nil

local L = {
    ["Simplified Chinese"] = {
        log_prefix = "[东方快捷键] ",
        window_title = "东方-快捷键设置",
        menukey_name = "界面开关键",
        menukey_hint = "用于开关本窗口",
        capturing = "按任意键…(Esc取消)",
        unbound = "未绑定",
        not_detected = "（未检测到）",
        hint_line = "点击按键后按新键绑定（Esc取消）；多条绑定可共用同一按键，按下时同时触发。",
        prev_page = "上一页",
        next_page = "下一页",
        page_format = "第 %d / %d 页",
        close = "关闭 (Esc)",
        bd_weapon_mod = "武器改装",
        bd_wearable1 = "装束技能1",
        bd_wearable2 = "装束技能2",
        bd_wearable3 = "装束技能3",
        bd_wearable4 = "装束技能4",
        bd_weapon_btn1 = "武器按钮1",
        bd_weapon_btn2 = "武器按钮2",
        bd_toggle_outfit = "装束界面显示",
        bd_toggle_outer = "外套界面显示",
        bd_toggle_bag = "背包界面显示",
        bd_outer1 = "外套按钮1",
        bd_outer2 = "外套按钮2",
        bd_bag1 = "背包按钮1",
        bd_bag2 = "背包按钮2",
        hud_outfit = "装束",
        hud_outer = "外套",
        hud_bag = "背包",
        hud_ui_suffix = "悬浮界面：",
        hud_state_hidden = "已隐藏",
        hud_state_shown = "已显示",
        bind_conflict = "绑定失败：该组合已被「%s」占用，请换一个键",
        mod_conflict = "修改失败：该组合已被「%s」占用",
        cfg_save_no_io = "无法保存配置：Barotrauma.IO.File 不可用（LuaCs 未开放文件访问）",
        cfg_read_no_io = "无法读取配置：Barotrauma.IO.File 不可用（LuaCs 未开放文件访问）",
        cfg_save_fail = "配置保存失败：%s（路径：%s）",
        cfg_read_fail = "配置读取失败：%s（路径：%s）",
        open_menu_fail = "打开设置窗口出错: %s",
    },
    ["English"] = {
        log_prefix = "[TouhouHotkey] ",
        window_title = "Touhou - Hotkey Settings",
        menukey_name = "Menu toggle key",
        menukey_hint = "Opens/closes this window",
        capturing = "Press any key… (Esc cancels)",
        unbound = "Unbound",
        not_detected = " (not detected)",
        hint_line = "Click a key button, then press a new key (Esc cancels). Multiple bindings may share one key and trigger together.",
        prev_page = "Prev",
        next_page = "Next",
        page_format = "Page %d / %d",
        close = "Close (Esc)",
        bd_weapon_mod = "Weapon mod",
        bd_wearable1 = "Outfit skill 1",
        bd_wearable2 = "Outfit skill 2",
        bd_wearable3 = "Outfit skill 3",
        bd_wearable4 = "Outfit skill 4",
        bd_weapon_btn1 = "Weapon button 1",
        bd_weapon_btn2 = "Weapon button 2",
        bd_toggle_outfit = "Outfit UI visibility",
        bd_toggle_outer = "Outerwear UI visibility",
        bd_toggle_bag = "Backpack UI visibility",
        bd_outer1 = "Outerwear button 1",
        bd_outer2 = "Outerwear button 2",
        bd_bag1 = "Backpack button 1",
        bd_bag2 = "Backpack button 2",
        hud_outfit = "Outfit",
        hud_outer = "Outerwear",
        hud_bag = "Backpack",
        hud_ui_suffix = " overlay UI: ",
        hud_state_hidden = "hidden",
        hud_state_shown = "shown",
        bind_conflict = "Binding failed: combo already used by \"%s\", pick another key",
        mod_conflict = "Change failed: combo already used by \"%s\"",
        cfg_save_no_io = "Cannot save config: Barotrauma.IO.File unavailable (LuaCs file access blocked)",
        cfg_read_no_io = "Cannot read config: Barotrauma.IO.File unavailable (LuaCs file access blocked)",
        cfg_save_fail = "Failed to save config: %s (path: %s)",
        cfg_read_fail = "Failed to read config: %s (path: %s)",
        open_menu_fail = "Failed to open settings window: %s",
    },
}

-- 确定当前语言：手动强制 > 游戏设置 > 默认简体中文
local LANGUAGE = LANGUAGE_OVERRIDE
if LANGUAGE == nil or L[LANGUAGE] == nil then
    local ok, lang = pcall(function() return tostring(GameSettings.CurrentConfig.Language) end)
    if ok and lang ~= nil and L[lang] ~= nil then
        LANGUAGE = lang
    else
        LANGUAGE = "Simplified Chinese"
    end
end

-- 取文本：当前语言缺失时回退到简体中文，再缺失回退键名本身
local function T(key)
    local pack = L[LANGUAGE]
    if pack ~= nil and pack[key] ~= nil then return pack[key] end
    return L["Simplified Chinese"][key] or key
end

local function dbg(msg)
    if DEBUG_LOG then print(T("log_prefix") .. msg) end
end

local function valid_key(name)
    for _, n in ipairs(KEY_LIST) do
        if n == name then return true end
    end
    return false
end

local function is_modifier_name(name)
    for _, n in ipairs(MODIFIER_NAMES) do
        if n == name then return true end
    end
    return false
end

--================ 配置（绑定列表模型） ================
-- 每条绑定：{ name=显示名, key=触发键(""表示未绑定), modifiers={修饰键}, target=目标 }
-- target: "weapon" = 手持武器改装按钮；"weapon:N" = 手持武器第 N 个其他按钮（排除改装）；
--         "wearable:N" = 装束按钮列表第 N 个；"toggle_hud" = 装束按钮显示开关
-- 默认全部不绑定（留空），由玩家自行设置，避免与默认键冲突导致设置失败
-- 注意：新增绑定只能追加在末尾，顺序改变会破坏已保存配置（配置文件按 binding.N 存取）
local function default_bindings()
    return {
        { name = "bd_weapon_mod",   key = "", modifiers = {}, target = "weapon" },
        { name = "bd_wearable1", key = "", modifiers = {}, target = "wearable:1" },
        { name = "bd_wearable2", key = "", modifiers = {}, target = "wearable:2" },
        { name = "bd_wearable3", key = "", modifiers = {}, target = "wearable:3" },
        { name = "bd_wearable4", key = "", modifiers = {}, target = "wearable:4" },
        { name = "bd_weapon_btn1", key = "", modifiers = {}, target = "weapon:1" },
        { name = "bd_weapon_btn2", key = "", modifiers = {}, target = "weapon:2" },
        { name = "bd_toggle_outfit", key = "", modifiers = {}, target = "toggle_hud" },
        { name = "bd_toggle_outer", key = "", modifiers = {}, target = "toggle_hud_outer" },
        { name = "bd_toggle_bag", key = "", modifiers = {}, target = "toggle_hud_bag" },
        { name = "bd_outer1", key = "", modifiers = {}, target = "outer:1" },
        { name = "bd_outer2", key = "", modifiers = {}, target = "outer:2" },
        { name = "bd_bag1", key = "", modifiers = {}, target = "bag:1" },
        { name = "bd_bag2", key = "", modifiers = {}, target = "bag:2" },
    }
end

local config = {
    menukey = "K",
    bindings = default_bindings(),
    hud_hidden = false,        -- 装束槽位悬浮界面是否隐藏（随配置持久化）
    hud_hidden_outer = false,  -- 外套/潜水服槽位
    hud_hidden_bag = false,    -- 背包槽位
}

-- 绑定匹配顺序缓存（修饰键多的优先），nil 表示需要重建。
-- 原实现每帧都新建 order 表并 table.sort，产生持续的堆分配
local sorted_binding_order = nil

local function get_config_path()
    -- 首选：官方存档目录（需要 SaveUtil；新版 LuaCs 禁止注册它时会拿不到）
    if SaveUtil ~= nil and Path ~= nil then
        local ok, p = pcall(function()
            return Path.Combine(SaveUtil.DefaultSaveFolder, CONFIG_FILE_NAME)
        end)
        if ok and p ~= nil then return p end
    end
    -- 次选：游戏目录下的 Data/Saves（存档文件夹，始终存在且 SafeIO 允许写入 .txt）
    if Path ~= nil then
        local ok, p = pcall(function() return Path.Combine("Data", "Saves", CONFIG_FILE_NAME) end)
        if ok and p ~= nil then return p end
    end
    return "Data/Saves/" .. CONFIG_FILE_NAME
end

local function save_config()
    if File == nil then
        print(T("log_prefix") .. T("cfg_save_no_io"))
        return
    end
    -- 配置已变化，绑定匹配顺序需要重建
    sorted_binding_order = nil
    local lines = { "menukey=" .. config.menukey }
    lines[#lines + 1] = "hud_hidden=" .. (config.hud_hidden and "1" or "0")
    lines[#lines + 1] = "hud_hidden_outer=" .. (config.hud_hidden_outer and "1" or "0")
    lines[#lines + 1] = "hud_hidden_bag=" .. (config.hud_hidden_bag and "1" or "0")
    for i, b in ipairs(config.bindings) do
        lines[#lines + 1] = "binding." .. i .. ".key=" .. b.key
        lines[#lines + 1] = "binding." .. i .. ".modifiers=" .. table.concat(b.modifiers, ",")
    end
    local path = get_config_path()
    local ok, err = pcall(function()
        File.WriteAllText(path, table.concat(lines, "\n"))
    end)
    if ok then
        dbg("配置已保存到 " .. tostring(path))
    else
        print(T("log_prefix") .. string.format(T("cfg_save_fail"), tostring(err), tostring(path)))
    end
end

local function parse_modifiers(text)
    local list = {}
    for name in string.gmatch(text or "", "[^,]+") do
        if is_modifier_name(name) then
            table.insert(list, name)
        end
    end
    return list
end

local function load_config()
    if File == nil then
        print(T("log_prefix") .. T("cfg_read_no_io"))
        return
    end
    local path = get_config_path()
    local ok, text = pcall(function()
        if not File.Exists(path) then return nil end
        return File.ReadAllText(path)
    end)
    if not ok then
        print(T("log_prefix") .. string.format(T("cfg_read_fail"), tostring(text), tostring(path)))
        return
    end
    if text == nil then
        dbg("配置文件不存在，使用默认设置：" .. tostring(path))
        return
    end
    dbg("已读取配置文件：" .. tostring(path))

    local kv = {}
    for line in string.gmatch(text, "[^\r\n]+") do
        local k, v = string.match(line, "^([%w%.]+)=(.*)$")
        if k ~= nil then kv[k] = v end
    end

    if valid_key(kv["menukey"]) then
        config.menukey = kv["menukey"]
    end

    config.hud_hidden = kv["hud_hidden"] == "1"
    config.hud_hidden_outer = kv["hud_hidden_outer"] == "1"
    config.hud_hidden_bag = kv["hud_hidden_bag"] == "1"

    -- 旧版配置迁移：hotkey/modifiers → 绑定1（武器改装）
    if valid_key(kv["hotkey"]) then
        config.bindings[1].key = kv["hotkey"]
        config.bindings[1].modifiers = parse_modifiers(kv["modifiers"])
    end

    -- 新版绑定格式
    for i, b in ipairs(config.bindings) do
        local key = kv["binding." .. i .. ".key"]
        local mods = kv["binding." .. i .. ".modifiers"]
        if key ~= nil then
            if key == "" or valid_key(key) then
                b.key = key
            end
            if mods ~= nil then
                b.modifiers = parse_modifiers(mods)
            end
        end
    end
end

--================ 绑定工具函数 ================
local function has_modifier(modifiers, name)
    for _, n in ipairs(modifiers) do
        if n == name then return true end
    end
    return false
end

--================ 原始按键检测 ================
-- 不用 PlayerInput.KeyHit/KeyDown：它们被游戏内部的 AllowInput 门控，
-- 且“上一帧”状态由游戏自己的更新节奏管理——按住移动键等操作时按键事件可能被吞。
-- 这里直接读原始键盘状态，自己维护“上一帧”记录做边沿检测，
-- 保证无论玩家是否在移动/操作，按下绑定键就能触发。
local key_was_down = {}  -- 按键名 -> 上一帧是否按下

local function raw_key_down(name)
    local ok, down = pcall(function()
        return PlayerInput.GetKeyboardState.IsKeyDown(Keys[name])
    end)
    return ok and down == true
end

-- 每帧调用一次：当前帧按下且上一帧未按下 = 触发（同时刷新上一帧记录）
local function poll_key_hit(name)
    local down = raw_key_down(name)
    local hit = down and not key_was_down[name]
    key_was_down[name] = down
    return hit
end

-- 把“上一帧”记录同步为当前真实状态（退出按键捕获后调用，防止松手前被误判为新按下）
local function sync_key_states()
    key_was_down[config.menukey] = raw_key_down(config.menukey)
    for _, b in ipairs(config.bindings) do
        if b.key ~= "" then
            key_was_down[b.key] = raw_key_down(b.key)
        end
    end
end

-- 触发条件：绑定要求的修饰键全部按住即可。
-- 多余按住的修饰键不阻止触发（例如按住 Shift 奔跑时，无修饰键的绑定照常生效）；
-- 分发顺序是“修饰键多的优先”，所以 J 与 Shift+J 共存时，按 Shift+J 会优先命中带修饰键的绑定
local function modifiers_satisfied(modifiers)
    for _, name in ipairs(modifiers) do
        if not raw_key_down(name) then
            return false
        end
    end
    return true
end

-- 绑定签名（按键 + 排序后的修饰键），用于冲突检测
local function binding_signature(key, modifiers)
    local mods = {}
    for _, n in ipairs(MODIFIER_NAMES) do
        if has_modifier(modifiers, n) then table.insert(mods, n) end
    end
    return key .. "|" .. table.concat(mods, ",")
end

-- 冲突规则：普通绑定之间允许共用按键（按下时同时触发）；
-- 只有“界面开关键”必须唯一——绑定不能占用它，它也不能与任何绑定重复
local function conflicts_with_menukey(key, modifiers)
    if key == "" then return false end
    return binding_signature(key, modifiers) == binding_signature(config.menukey, {})
end

local function menukey_conflicts_with_bindings(key)
    local sig = binding_signature(key, {})
    for _, b in ipairs(config.bindings) do
        if b.key ~= "" and binding_signature(b.key, b.modifiers) == sig then
            return true, b.name
        end
    end
    return false
end

local function binding_display(b)
    if b.key == "" then return T("unbound") end
    local parts = {}
    for _, name in ipairs(MODIFIER_NAMES) do
        if has_modifier(b.modifiers, name) then table.insert(parts, MODIFIER_DISPLAY[name]) end
    end
    table.insert(parts, b.key)
    return table.concat(parts, " + ")
end

-- 脏配置自愈：只处理与界面开关键重复的绑定（清空该绑定，由玩家重新指定）；
-- 绑定之间共用按键是合法特性，不做干预
local function sanitize_config()
    for _, b in ipairs(config.bindings) do
        if conflicts_with_menukey(b.key, b.modifiers) then
            b.key = ""
            b.modifiers = {}
        end
    end
end

--================ GUI 控件识别（LuaCs 禁止反射，用属性探测做 duck-typing） ================
local function is_gui_button(component)
    -- OnClicked 是 GUIButton 特有字段
    return pcall(function() return component.OnClicked end)
end

local function is_gui_tickbox(component)
    -- OnSelected 是 GUITickBox 特有字段（GUIButton 没有）
    return pcall(function() return component.OnSelected end)
end

local function is_layout_group(component)
    -- AbsoluteSpacing 是 GUILayoutGroup 特有属性
    return pcall(function() return component.AbsoluteSpacing end)
end

-- 按钮的 UserData（CustomInterfaceElement）是 internal 嵌套类，LuaCs 读不出其字段，
-- 因此无法可靠判断按钮是否带 StatusEffects——枚举时一律不按效果过滤（见 find_buttons 注释）

--================ 按钮/复选框枚举与触发 ================
-- 枚举物品 CustomInterface 上的可点击控件（按钮 + 复选框）
-- 返回数组 { control=控件, kind="button"|"tick", label=文本, item=物品 }，顺序与 XML 定义顺序一致
-- 注意：
--  · 不能用 UserData.StatusEffects 过滤——CustomInterfaceElement 是 internal 类，
--    LuaCs 读不出它的字段，会把所有真按钮误判为“无效果”；
--  · 官方 CreateGUI 重建 UI 时旧容器不销毁，GuiFrame 里会残留失效控件，
--    因此按文本去重，后出现的（最新重建的）覆盖先前的；
--  · 无文本的控件（残留/装饰控件）直接跳过
local function find_buttons(item)
    local buttons = {}
    local seen = {}  -- label -> buttons 数组下标
    for component in item.Components do
        -- CustomInterface 才有 GuiFrame 属性（LuaCs 禁止反射，用 pcall 属性探测识别组件类型）
        local ok, frame = pcall(function() return component.GuiFrame end)
        if ok and frame ~= nil then
            -- 官方实现中按钮都在 GuiFrame 直接子级的 GUILayoutGroup（uiElementContainer）里；
            -- 找不到容器时兜底扫描整个 Frame
            local roots = {}
            for child in frame.Children do
                if is_layout_group(child) then
                    roots[#roots + 1] = child
                end
            end
            if #roots == 0 then roots[1] = frame end

            for _, root in ipairs(roots) do
                for child in root.GetAllChildren() do
                    local kind = nil
                    if is_gui_button(child) then
                        kind = "button"
                    elseif is_gui_tickbox(child) then
                        kind = "tick"
                    end
                    if kind ~= nil then
                        local label = tostring(child.Text)
                        if not string.match(label, "^%s*$") then
                            local entry = { control = child, kind = kind, label = label, item = item }
                            if seen[label] ~= nil then
                                -- 同名控件：替换为最新出现的（新重建的 UI 控件）
                                buttons[seen[label]] = entry
                            else
                                buttons[#buttons + 1] = entry
                                seen[label] = #buttons
                            end
                        end
                    end
                end
            end
        end
    end
    return buttons
end

-- 触发单个控件，与鼠标点击完全一致：
-- 按钮 = 调用原版点击委托（C# 委托必须用 Invoke）；
-- 复选框 = 翻转 Selected，setter 会自动触发原版 OnSelected 委托（含联机 CreateClientEvent 同步）
local function click_button(entry)
    if entry.kind == "tick" then
        entry.control.Selected = not entry.control.Selected
    else
        entry.control.OnClicked.Invoke(entry.control, entry.control.UserData)
    end
end

local function is_mod_weapon(item)
    if #ALLOWED_IDENTIFIERS == 0 and #ALLOWED_TAGS == 0 then return true end
    for _, id in ipairs(ALLOWED_IDENTIFIERS) do
        if tostring(item.Prefab.Identifier) == id then return true end
    end
    for _, tag in ipairs(ALLOWED_TAGS) do
        if item.HasTag(tag) then return true end
    end
    return false
end

-- 武器改装：优先按“改装”文本匹配按钮，兜底取第一个按钮
local function try_trigger_mod(item)
    if not is_mod_weapon(item) then return false end
    local buttons = find_buttons(item)
    if #buttons == 0 then return false end

    local mod_text = tostring(TextManager.Get(BUTTON_TEXT_TAG))
    for _, entry in ipairs(buttons) do
        if entry.label == mod_text then
            click_button(entry)
            dbg("已点击「" .. tostring(item.Name) .. "」的改装按钮（需耐久充满效果才会生效）")
            return true
        end
    end
    click_button(buttons[1])
    dbg("已点击「" .. tostring(item.Name) .. "」的第一个按钮（未按文本匹配到“改装”）")
    return true
end

-- 判断物品是否为东方装束：subcategory == "Touhou"，或在额外白名单中
local function is_touhou_outfit(item)
    local ok, sub = pcall(function() return tostring(item.Prefab.Subcategory) end)
    if ok and sub ~= nil and string.lower(sub) == string.lower(OUTFIT_SUBCATEGORY) then
        return true
    end
    for _, id in ipairs(OUTFIT_IDENTIFIERS) do
        if tostring(item.Prefab.Identifier) == id then return true end
    end
    for _, tag in ipairs(OUTFIT_TAGS) do
        if item.HasTag(tag) then return true end
    end
    return false
end

-- 枚举指定槽位组当前装备上的全部可点击控件（按组内槽位顺序 + XML 定义顺序）
-- group_name: "wearable"（装束，仅东方装备）/ "outer"（外套/潜水服）/ "bag"（背包）
local function get_group_buttons(character, group_name)
    local list = {}
    local group = SLOT_GROUPS[group_name]
    if group == nil or character == nil or character.Inventory == nil then return list end
    for _, slot in ipairs(group.slots) do
        local item = character.Inventory.GetItemInLimbSlot(InvSlotType[slot])
        if item ~= nil and (not group.touhou_only or is_touhou_outfit(item)) then
            for _, entry in ipairs(find_buttons(item)) do
                table.insert(list, entry)
            end
        end
    end
    return list
end

-- 装束按钮枚举（装束技能1~4 用）
local function get_wearable_buttons(character)
    return get_group_buttons(character, "wearable")
end

-- 触发手持武器上除“改装”外的第 N 个按钮（射击模式切换等）
local function try_trigger_weapon_button(character, n)
    local mod_text = tostring(TextManager.Get(BUTTON_TEXT_TAG))
    for item in character.HeldItems do
        if is_mod_weapon(item) then
            local others = {}
            for _, entry in ipairs(find_buttons(item)) do
                if entry.label ~= mod_text then
                    others[#others + 1] = entry
                end
            end
            if n <= #others then
                click_button(others[n])
                dbg("已触发武器按钮「" .. others[n].label .. "」（来自「" .. tostring(item.Name) .. "」）")
                return true
            end
        end
    end
    return false
end

--================ 悬浮界面显示开关 ================
-- DrawHudWhenEquipped 属性是 protected set，LuaCs 改不了；
-- 但 CharacterHUD 绘制/注册悬浮界面前都会检查 GuiFrame.Visible（false 直接跳过），
-- 且 GUIComponent.Visible 是公开可写的——因此用切换 GuiFrame.Visible 实现同样的效果。
-- 注意：游戏在 UI 重建（分辨率/UI 缩放变化）时会把 Visible 重置回 true，
-- 因此隐藏状态需要周期性重新强制（见 think 钩子）。
-- 按槽位组分别控制：装束（InnerClothes+Head）、外套（OuterClothes）、背包（Bag）互不影响
local function set_slots_hud_visible(slots, visible)
    local character = Character.Controlled
    if character == nil or character.Inventory == nil then return end
    for _, slot in ipairs(slots) do
        local item = character.Inventory.GetItemInLimbSlot(InvSlotType[slot])
        if item ~= nil then
            for component in item.Components do
                local ok, frame = pcall(function() return component.GuiFrame end)
                if ok and frame ~= nil then
                    pcall(function() frame.Visible = visible end)
                end
            end
        end
    end
end

-- 装束扫描诊断：逐槽位、逐组件打印探测结果，用于排查“检测不到按钮”的问题
local function diagnose_outfit_scan(character)
    print("[东方快捷键] ---- 装束扫描诊断 ----")
    if character == nil or character.Inventory == nil then
        print("[东方快捷键]   角色或物品栏为 nil")
        return
    end
    for _, slot in ipairs({ "InnerClothes", "Head", "OuterClothes", "Bag" }) do
        local item = character.Inventory.GetItemInLimbSlot(InvSlotType[slot])
        if item == nil then
            print("[东方快捷键]   槽位 " .. slot .. "：无物品")
        else
            local ok_sub, sub = pcall(function() return tostring(item.Prefab.Subcategory) end)
            print("[东方快捷键]   槽位 " .. slot .. "：「" .. tostring(item.Name)
                .. "」 identifier=" .. tostring(item.Prefab.Identifier)
                .. " subcategory=" .. (ok_sub and tostring(sub) or "(读取失败)")
                .. " 判定为东方装束=" .. tostring(is_touhou_outfit(item)))
            for component in item.Components do
                local ok_f, frame = pcall(function() return component.GuiFrame end)
                if ok_f and frame ~= nil then
                    local total, pass_btn, labeled = 0, 0, 0
                    for child in frame.GetAllChildren() do
                        total = total + 1
                        if is_gui_button(child) or is_gui_tickbox(child) then
                            pass_btn = pass_btn + 1
                            local label = tostring(child.Text)
                            if not string.match(label, "^%s*$") then
                                labeled = labeled + 1
                                print("[东方快捷键]     控件：「" .. label .. "」")
                            end
                        end
                    end
                    if total > 0 then
                        print("[东方快捷键]     组件 " .. tostring(component.Name)
                            .. "：子控件 " .. total .. " 个，可点击控件 " .. pass_btn
                            .. " 个，其中带文本 " .. labeled .. " 个")
                    end
                end
            end
        end
    end
    print("[东方快捷键] ---- 诊断结束 ----")
end

-- 执行一条绑定
local function execute_binding(b, character)
    if b.target == "weapon" then
        for item in character.HeldItems do
            if try_trigger_mod(item) then return true end
        end
        dbg(T(b.name) .. "：双手没有找到可改装的武器")
        return false
    end

    -- 悬浮界面显示开关（按槽位组：装束/外套/背包，不依赖当前装备，随时可切换）
    local hud_group = HUD_GROUPS[b.target]
    if hud_group ~= nil then
        config[hud_group.flag] = not config[hud_group.flag]
        set_slots_hud_visible(hud_group.slots, not config[hud_group.flag])
        save_config()
        print(T("log_prefix") .. T(hud_group.display_key) .. T("hud_ui_suffix")
            .. (config[hud_group.flag] and T("hud_state_hidden") or T("hud_state_shown")))
        return true
    end

    -- 手持武器的第 N 个其他按钮（排除“改装”）
    local wn = tonumber(string.match(b.target, "^weapon:(%d+)$") or "")
    if wn ~= nil then
        if try_trigger_weapon_button(character, wn) then return true end
        dbg(T(b.name) .. "：手持武器上没有对应的其他按钮")
        return false
    end

    -- 槽位组的第 N 个控件（wearable:N 装束技能 / outer:N 外套按钮 / bag:N 背包按钮）
    local group_name, idx_text = string.match(b.target, "^(%a+):(%d+)$")
    if group_name ~= nil and SLOT_GROUPS[group_name] ~= nil then
        local idx = tonumber(idx_text)
        local list = get_group_buttons(character, group_name)
        if idx ~= nil and idx <= #list then
            click_button(list[idx])
            dbg("已触发" .. T(b.name) .. "「" .. list[idx].label .. "」（来自「" .. tostring(list[idx].item.Name) .. "」）")
            return true
        end
        dbg(T(b.name) .. "：未检测到对应的控件（当前共检测到 " .. #list .. " 个）")
        if DEBUG_LOG then diagnose_outfit_scan(character) end
        return false
    end

    return false
end

--================ 设置界面 ================
local menu_frame = nil
local capturing = nil        -- 绑定索引（数字）或 "menukey" 或 nil
local capture_ignored = {}   -- 进入捕获模式时已按住的键（防止被立即误捕获）
local menukey_button = nil
local binding_buttons = {}   -- 每条绑定的按键按钮
local detect_labels = {}     -- 装束技能行的“检测到的技能名”标签
local frame_counter = 0
local hud_enforce_counter = 0  -- 装束悬浮界面隐藏状态的强制刷新计数
local BINDINGS_PER_PAGE = 5  -- 设置窗口每页显示的绑定条数，超出翻页
local current_page = 1

local function refresh_binding_texts()
    if menukey_button ~= nil then
        if capturing == "menukey" then
            menukey_button.Text = RawLString(T("capturing"))
        else
            menukey_button.Text = RawLString(config.menukey)
        end
    end
    for i, btn in pairs(binding_buttons) do
        if capturing == i then
            btn.Text = RawLString(T("capturing"))
        else
            btn.Text = RawLString(binding_display(config.bindings[i]))
        end
    end
end

-- 刷新槽位组绑定行显示的“当前检测到的控件名称”（装束技能/外套按钮/背包按钮）
local function refresh_detected_labels()
    if menu_frame == nil then return end
    local group_lists = {}  -- 组名 -> 控件列表（每组只枚举一次）
    for i, label in pairs(detect_labels) do
        local b = config.bindings[i]
        local group_name, idx_text = string.match(b.target, "^(%a+):(%d+)$")
        local n = tonumber(idx_text)
        local text = T(b.name)
        if group_name ~= nil and SLOT_GROUPS[group_name] ~= nil and n ~= nil then
            if group_lists[group_name] == nil then
                group_lists[group_name] = get_group_buttons(Character.Controlled, group_name)
            end
            local list = group_lists[group_name]
            if n <= #list then
                text = T(b.name) .. "：" .. list[n].label
            else
                text = T(b.name) .. T("not_detected")
            end
        end
        label.Text = RichString.Plain(RawLString(text))
    end
end

-- 进入按键捕获模式：记录当前已按住的键，等它们松开后才接受新输入
local function start_capture(target)
    capturing = target
    capture_ignored = {}
    for key in PlayerInput.GetKeyboardState.GetPressedKeys() do
        capture_ignored[tostring(key)] = true
    end
    refresh_binding_texts()
end

local function stop_capture()
    capturing = nil
    capture_ignored = {}
    sync_key_states()  -- 捕获期间跳过了轮询，把“上一帧”记录同步为当前真实状态
    refresh_binding_texts()
end

local function close_menu()
    capturing = nil
    capture_ignored = {}
    sync_key_states()
    if menu_frame ~= nil then
        GUI.GUI.RemoveFromUpdateList(menu_frame, true)
        menu_frame.RectTransform.Parent = nil
        menu_frame = nil
        menukey_button = nil
        binding_buttons = {}
        detect_labels = {}
    end
end

local function add_row(layout, height)
    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, height), layout.RectTransform), true, GUI.Anchor.CenterLeft)
    row.RelativeSpacing = 0.015
    return row
end

local function open_menu()
    close_menu()

    menu_frame = GUI.Frame(GUI.RectTransform(Vector2(0.40, 0.70), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")

    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.92, 0.96), menu_frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.012
    layout.Stretch = true

    -- 创建文本标签的辅助函数
    -- 注意：GUITextBlock 构造函数需要 RichString（GUIButton/GUITickBox 才是 LocalizedString），
    -- LuaCs 不做隐式类型转换，必须用 RichString.Plain 显式构造；对齐方式通过属性设置
    local function add_text(rel_size, parent_rt, text, alignment)
        local block = GUI.TextBlock(GUI.RectTransform(rel_size, parent_rt), RichString.Plain(RawLString(text)))
        block.TextAlignment = alignment
        return block
    end

    -- 固定 10 行布局（标题 + 界面开关键 + 每页5条绑定 + 提示 + 翻页栏 + 关闭），行高不用压缩
    local ROW_H = 0.085

    -- 标题
    add_text(Vector2(1, ROW_H), layout.RectTransform, T("window_title"), GUI.Alignment.Center)

    -- 界面开关键
    do
        local row = add_row(layout, ROW_H)
        add_text(Vector2(0.3, 1), row.RectTransform, T("menukey_name"), GUI.Alignment.CenterLeft)
        menukey_button = GUI.Button(GUI.RectTransform(Vector2(0.22, 1), row.RectTransform), RawLString(config.menukey))
        menukey_button.OnClicked = function()
            start_capture("menukey")
            return true
        end
        add_text(Vector2(0.48, 1), row.RectTransform, T("menukey_hint"), GUI.Alignment.CenterLeft)
    end

    -- 各条绑定（按页切片显示）
    local total_pages = math.max(1, math.ceil(#config.bindings / BINDINGS_PER_PAGE))
    if current_page > total_pages then current_page = total_pages end
    if current_page < 1 then current_page = 1 end
    local first = (current_page - 1) * BINDINGS_PER_PAGE + 1
    local last = math.min(first + BINDINGS_PER_PAGE - 1, #config.bindings)

    for i = first, last do
        local b = config.bindings[i]
        local row = add_row(layout, ROW_H)

        -- 名称列：槽位组绑定行（装束技能/外套按钮/背包按钮）同时显示当前检测到的控件名称
        local name_label = add_text(Vector2(0.3, 1), row.RectTransform, T(b.name), GUI.Alignment.CenterLeft)
        local group_name = string.match(b.target, "^(%a+):")
        if group_name ~= nil and SLOT_GROUPS[group_name] ~= nil then
            detect_labels[i] = name_label
        end

        -- 按键按钮
        binding_buttons[i] = GUI.Button(GUI.RectTransform(Vector2(0.22, 1), row.RectTransform), RawLString(binding_display(b)))
        binding_buttons[i].OnClicked = function()
            start_capture(i)
            return true
        end

        -- 清除绑定按钮
        local clear_button = GUI.Button(GUI.RectTransform(Vector2(0.06, 1), row.RectTransform), RawLString("×"))
        clear_button.OnClicked = function()
            b.key = ""
            b.modifiers = {}
            save_config()
            refresh_binding_texts()
            return true
        end

        -- 修饰键勾选（每条绑定独立）
        for _, name in ipairs(MODIFIER_NAMES) do
            local tick = GUI.TickBox(GUI.RectTransform(Vector2(0.14, 1), row.RectTransform), RawLString(MODIFIER_DISPLAY[name]))
            tick.Selected = has_modifier(b.modifiers, name)
            tick.OnSelected = function(tb)
                local new_mods = {}
                for _, n in ipairs(b.modifiers) do
                    if n ~= name then table.insert(new_mods, n) end
                end
                if tb.Selected then table.insert(new_mods, name) end

                local conflict = conflicts_with_menukey(b.key, new_mods)
                if conflict then
                    tb.Selected = not tb.Selected  -- 撤销勾选
                    print(T("log_prefix") .. string.format(T("mod_conflict"), T("menukey_name")))
                    return true
                end
                b.modifiers = new_mods
                save_config()
                refresh_binding_texts()
                return true
            end
        end
    end

    -- 提示
    add_text(Vector2(1, ROW_H), layout.RectTransform, T("hint_line"), GUI.Alignment.Center)

    -- 翻页栏（页数大于 1 时才需要，但始终显示以保持布局稳定）
    do
        local page_row = add_row(layout, ROW_H)
        local prev_button = GUI.Button(GUI.RectTransform(Vector2(0.2, 1), page_row.RectTransform), RawLString(T("prev_page")))
        prev_button.OnClicked = function()
            current_page = current_page - 1
            if current_page < 1 then current_page = total_pages end
            pcall(open_menu)
            return true
        end
        add_text(Vector2(0.6, 1), page_row.RectTransform,
            string.format(T("page_format"), current_page, total_pages), GUI.Alignment.Center)
        local next_button = GUI.Button(GUI.RectTransform(Vector2(0.2, 1), page_row.RectTransform), RawLString(T("next_page")))
        next_button.OnClicked = function()
            current_page = current_page + 1
            if current_page > total_pages then current_page = 1 end
            pcall(open_menu)
            return true
        end
    end

    local close_button = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("close")))
    close_button.OnClicked = function()
        close_menu()
        return true
    end

    refresh_binding_texts()
    refresh_detected_labels()
    if DEBUG_LOG then diagnose_outfit_scan(Character.Controlled) end
end

-- 打开设置窗口（带错误提示，出错时打印到控制台方便排查）
local function safe_open_menu()
    local ok, err = pcall(open_menu)
    if not ok then
        print(T("log_prefix") .. string.format(T("open_menu_fail"), tostring(err)))
    end
end

--================ 暂停菜单注入 ================
local PAUSE_BUTTON_FLAG = "touhou_hotkey_settings_button"

-- 在暂停菜单（ESC 菜单）的按钮列表底部追加「东方-快捷键设置」按钮
local function add_pause_menu_button(pause_menu)
    if pause_menu == nil then return end

    -- 找到暂停菜单内层的按钮容器（唯一的 GUILayoutGroup）
    local container = nil
    for child in pause_menu.GetAllChildren() do
        if is_layout_group(child) then
            container = child
            break
        end
    end
    if container == nil then return end

    -- 防止重复添加
    for child in container.Children do
        if child.UserData == PAUSE_BUTTON_FLAG then return end
    end

    local btn = GUI.Button(GUI.RectTransform(Vector2(1, 0.1), container.RectTransform), RawLString(T("window_title")))
    btn.UserData = PAUSE_BUTTON_FLAG
    btn.OnClicked = function()
        safe_open_menu()
        return true
    end

    -- 与原版逻辑一致：按钮变多后重新计算内框最小高度，避免溢出（失败不影响按钮本身）
    pcall(function()
        local inner = container.RectTransform.Parent.GUIComponent
        local total = 0
        for c in container.Children do
            total = total + c.Rect.Height + container.AbsoluteSpacing
        end
        local needed = math.ceil(total / container.RectTransform.RelativeSize.Y)
        local min_size = inner.RectTransform.MinSize
        if needed > min_size.Y then
            inner.RectTransform.MinSize = XnaPoint(min_size.X, needed)
        end
    end)
end

local last_pause_menu = nil

-- 监视暂停菜单实例，新菜单出现时注入按钮（在 think 钩子中调用）
local function update_pause_menu_button()
    local pause_menu = GUI.GUI.PauseMenu
    if pause_menu == nil then
        last_pause_menu = nil
        return
    end
    if pause_menu ~= last_pause_menu then
        last_pause_menu = pause_menu
        add_pause_menu_button(pause_menu)
    end
end

--================ 主逻辑 ================
load_config()
sanitize_config()

-- 获取绑定匹配顺序（修饰键多的优先）；结果缓存，仅在 save_config 后重建
local function get_sorted_binding_order()
    if sorted_binding_order == nil then
        local order = {}
        for i in ipairs(config.bindings) do
            order[#order + 1] = i
        end
        table.sort(order, function(a, c)
            return #config.bindings[a].modifiers > #config.bindings[c].modifiers
        end)
        sorted_binding_order = order
    end
    return sorted_binding_order
end

Hook.Add("think", "touhou_hotkey_settings", function()
    -- 设置窗口需要像暂停菜单一样，每帧重新加入 GUI 更新列表才会被绘制
    -- （Barotrauma 的 GUI 控件必须每帧重新注册，否则会被移出更新列表而不显示；
    --   因此这一步必须在所有 return 分支之前执行，包括按键捕获期间）
    -- order=1：绘制在暂停菜单等普通界面之上，避免被遮挡
    if menu_frame ~= nil then
        menu_frame.AddToGUIUpdateList(false, 1)

        -- 定期刷新装束技能行检测到的按钮名称（换装备后自动更新）
        frame_counter = frame_counter + 1
        if frame_counter >= 30 then
            frame_counter = 0
            refresh_detected_labels()
        end
    end

    -- 悬浮界面隐藏状态需要周期性重新强制（按槽位组分别处理）：
    -- 换装、UI 重建（分辨率/缩放变化）都会把 GuiFrame.Visible 重置回 true
    if config.hud_hidden or config.hud_hidden_outer or config.hud_hidden_bag then
        hud_enforce_counter = hud_enforce_counter + 1
        if hud_enforce_counter >= 30 then
            hud_enforce_counter = 0
            for _, group in pairs(HUD_GROUPS) do
                if config[group.flag] then
                    set_slots_hud_visible(group.slots, false)
                end
            end
        end
    end

    -- 设置窗口打开期间，阻止游戏的 ESC 切换暂停菜单（改由本脚本自己处理 ESC 关窗）
    pcall(function() GUI.PreventPauseMenuToggle = (menu_frame ~= nil) end)

    -- 按键捕获模式：优先级最高
    if capturing ~= nil then
        -- 先清理已经松开的忽略键
        for name in pairs(capture_ignored) do
            if not raw_key_down(name) then
                capture_ignored[name] = nil
            end
        end

        local state = PlayerInput.GetKeyboardState
        for key in state.GetPressedKeys() do
            local name = tostring(key)
            if capture_ignored[name] then
                -- 进入捕获时已按住的键，等松开，不算数
            elseif name == "Escape" then
                key_was_down["Escape"] = true  -- 这帧的 Esc 已被捕获取消消费，避免紧接着触发关窗
                stop_capture()
                return
            elseif valid_key(name) and not is_modifier_name(name) then
                local conflict, who
                if capturing == "menukey" then
                    -- 界面开关键必须唯一：不能与任何绑定重复
                    conflict, who = menukey_conflicts_with_bindings(name)
                    if not conflict then config.menukey = name end
                else
                    -- 普通绑定之间允许共用按键，只需避开界面开关键
                    conflict = conflicts_with_menukey(name, config.bindings[capturing].modifiers)
                    who = "menukey_name"
                    if not conflict then config.bindings[capturing].key = name end
                end
                if conflict then
                    print(T("log_prefix") .. string.format(T("bind_conflict"), T(tostring(who))))
                else
                    save_config()
                end
                stop_capture()
                return
            end
        end
        return
    end

    -- 每帧轮询按键原始状态并自维护“上一帧”记录（必须在所有 return 分支之前，
    -- 保证任何情况下记录都是新鲜的，也不会受游戏输入门控影响）
    -- 注意：按“唯一按键”轮询而不是按绑定轮询——多条绑定共用同一按键时，
    -- 逐绑定轮询会让第一条绑定消费掉边沿事件，导致其后的绑定永远不触发
    local menukey_hit = poll_key_hit(config.menukey)
    local esc_hit = poll_key_hit("Escape")
    local key_hits = {}  -- 按键名 -> 本帧是否新按下
    for _, b in ipairs(config.bindings) do
        if b.key ~= "" and key_hits[b.key] == nil then
            key_hits[b.key] = poll_key_hit(b.key)
        end
    end

    -- 暂停菜单出现时注入「东方-快捷键设置」按钮（即便暂停菜单打开时也要执行）
    update_pause_menu_button()

    if GUI.KeyboardDispatcher.Subscriber then return end  -- 聊天框/输入框激活时不触发

    if Game.GameSession == nil then
        if menu_frame ~= nil then close_menu() end
        return
    end

    -- ESC 优先关闭设置窗口（窗口打开期间游戏的暂停菜单切换已被 PreventPauseMenuToggle 阻止）
    if esc_hit and menu_frame ~= nil then
        close_menu()
        return
    end

    -- 设置界面开关（暂停菜单打开时也允许开关设置窗口）
    if menukey_hit then
        if menu_frame ~= nil then
            close_menu()
        else
            safe_open_menu()
        end
        return
    end

    if GUI.GUI.PauseMenuOpen then return end
    if menu_frame ~= nil then return end  -- 设置界面打开时不触发技能
    if Game.Paused then return end

    -- 控制台、Tab 菜单、战役界面、社交覆盖层等“阻挡输入”的界面打开时不触发技能
    -- （调试控制台打开时即视为正在输入文字；该属性也包含暂停菜单，但不会包含本脚本自己的设置窗口）
    local ok_ib, input_blocking = pcall(function() return GUI.InputBlockingMenuOpen end)
    if ok_ib and input_blocking then return end

    -- 绑定分发：修饰键多的绑定优先匹配（例如 Shift+J 优先于 J）。
    -- 签名完全相同（按键+修饰键都一样）的绑定允许共存，按下时全部触发（一键多用）；
    -- 不同签名只触发修饰键最多的那一组，避免 Shift+J 把 J 的绑定也带出来
    local character = Character.Controlled
    if character == nil then return end

    local fired_sig = nil
    for _, i in ipairs(get_sorted_binding_order()) do
        local b = config.bindings[i]
        if key_hits[b.key] and modifiers_satisfied(b.modifiers) then
            local sig = binding_signature(b.key, b.modifiers)
            if fired_sig == nil then fired_sig = sig end
            if sig == fired_sig then
                execute_binding(b, character)
            end
        end
    end
end)
