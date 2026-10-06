--[[东方-快捷键设置：武器/装束/外套/背包槽位的按钮触发 + 悬浮界面开关 + 统一设置窗口（K 开关）。
    触发全走原版委托，单机直接执行，联机走 CreateClientEvent 由服务器执行。
    配置存 TouhouModHotkeyConfig.txt，旧版自动迁移；文本走 L 表多语言，跟游戏语言走。]]

-- 哦哦哦！
if not CLIENT then
    return
end

-- SaveUtil 在新版 LuaCs 里被禁注册，拿不到就退回下面的兜底路径
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

-- ？？？？？？？？！！！！！！！

local BUTTON_TEXT_TAG = "Text.Touhou_Open_Mod"  -- 武器改装按钮的文本标签
local CONFIG_FILE_NAME = "TouhouModHotkeyConfig.txt"
local DEBUG_LOG = false  -- 排障时改 true

-- 触发枚举的槽位组（分开编号、互不影响）。
-- 模组潜水服也是 subcategory="Touhou"，所以装束不能只靠类别认，必须按槽位认
local SLOT_GROUPS = {
    wearable = { slots = { "InnerClothes", "Head" }, touhou_only = true },   -- 装束（含帽子）
    outer    = { slots = { "OuterClothes" },         touhou_only = false },  -- 外套/潜水服
    bag      = { slots = { "Bag" },                  touhou_only = false },  -- 背包
}

-- 悬浮界面开关：target -> 控制的槽位 + 配置字段 + 显示名
local HUD_GROUPS = {
    toggle_hud       = { slots = { "InnerClothes", "Head" }, flag = "hud_hidden",       display_key = "hud_outfit" },
    toggle_hud_outer = { slots = { "OuterClothes" },         flag = "hud_hidden_outer", display_key = "hud_outer" },
    toggle_hud_bag   = { slots = { "Bag" },                  flag = "hud_hidden_bag",   display_key = "hud_bag" },
}

-- 只认 subcategory 为 "Touhou" 的装束，免得把潜水服这类同样带按钮的服装也扫进来
local OUTFIT_SUBCATEGORY = "Touhou"

-- 可选：额外的装束 identifier / tag 白名单（subcategory 不是 Touhou 的例外装束加在这里）
local OUTFIT_IDENTIFIERS = {}  -- 例如 { "Kokoro_Mask01" }
local OUTFIT_TAGS = {}         -- 例如 { "Touhou_Clothes" }

-- 可选：限制哪些武器允许用快捷键触发改装（填 identifier 或 tag；两者都留空 = 任何带“改装”按钮的武器）
local ALLOWED_IDENTIFIERS = {}  -- 例如 { "Touhou_Monarch", "Touhou_Deceit" }
local ALLOWED_TAGS = {}         -- 例如 { "Touhou_Mod_Weapon" }（需要 XML 里给武器加对应 tag）

-- 可绑定的按键白名单（XNA Keys 名）。Esc 留给取消/关窗，Win 系键会被系统吃掉，
-- 媒体键太冷门，都不要；其余全放开
local KEY_LIST = {}
for i = 65, 90 do KEY_LIST[#KEY_LIST + 1] = string.char(i) end       -- A-Z
for i = 0, 9 do KEY_LIST[#KEY_LIST + 1] = "D" .. i end               -- 数字键 0-9（主键盘）
for i = 1, 24 do KEY_LIST[#KEY_LIST + 1] = "F" .. i end              -- F1-F24
for i = 0, 9 do KEY_LIST[#KEY_LIST + 1] = "NumPad" .. i end          -- 小键盘数字 0-9
for _, n in ipairs({ "Add", "Subtract", "Multiply", "Divide", "Decimal" }) do
    KEY_LIST[#KEY_LIST + 1] = n                                       -- 小键盘运算符 + - * / .
end
for _, n in ipairs({ "Up", "Down", "Left", "Right", "Insert", "Delete", "Home", "End", "PageUp", "PageDown" }) do
    KEY_LIST[#KEY_LIST + 1] = n                                       -- 方向键与编辑键
end
for _, n in ipairs({
    "Space", "Tab", "Enter",                                          -- 空格 / Tab / 回车
    "CapsLock", "NumLock", "PrintScreen", "Scroll", "Pause",          -- 锁定与系统键
    "OemComma", "OemPeriod", "OemQuestion", "OemSemicolon", "OemColon",
    "OemPlus", "OemMinus", "OemOpenBrackets", "OemCloseBrackets",
    "OemPipe", "OemQuotes", "OemTilde", "OemBackslash",               -- 标点符号键（, . / ; : = - [ ] \ ' `）
}) do
    KEY_LIST[#KEY_LIST + 1] = n
end

-- 鼠标键（自定义名字，不在 XNA Keys 里，走 raw_mouse_down 检测）。
-- 左右键是攻击/瞄准，绑了容易误触；滚轮跟背包滚轮冲突，都不给绑
local MOUSE_BUTTONS = { "MouseMiddle", "MouseSide1", "MouseSide2" }

local MODIFIER_NAMES = { "LeftShift", "LeftControl", "LeftAlt", "RightShift", "RightControl", "RightAlt" }
local MODIFIER_DISPLAY = { LeftShift = "Shift", LeftControl = "Ctrl", LeftAlt = "Alt",
                           RightShift = "R-Shift", RightControl = "R-Ctrl", RightAlt = "R-Alt" }

-- 语言跟游戏设置走，LANGUAGE_OVERRIDE 可强制。加语言就复制一个块改值，键名别动
local LANGUAGE_OVERRIDE = nil

local L = {
    ["Simplified Chinese"] = {
        log_prefix = "[东方快捷键] ",
        window_title = "东方模组设置",
        menukey_name = "界面开关键",
        menukey_hint = "用于开关本窗口",
        capturing = "按任意键或鼠标键…(Esc取消)",
        unbound = "未绑定",
        not_detected = "（未检测到）",
        key_mouse_middle = "鼠标中键",
        key_mouse_side1 = "侧键1",
        key_mouse_side2 = "侧键2",
        mouse_unavailable = "鼠标键 %s 无法检测（LuaCs 未开放对应输入接口），该绑定不会生效",
        hint_line = "点击按键后按新键或鼠标键绑定（Esc取消）；多条绑定可共用同一按键，按下时同时触发。",
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
        bd_weapon_btn3 = "武器按钮3",
        bd_toggle_outfit = "装束界面显示",
        bd_toggle_outer = "外套界面显示",
        bd_toggle_bag = "背包界面显示",
        bd_outer1 = "外套按钮1",
        bd_outer2 = "外套按钮2",
        bd_bag1 = "背包按钮1",
        bd_bag2 = "背包按钮2",
        bd_bond_panel = "绑定面板",
        bd_desc_toggle = "描述详略切换",
        desc_detailed_on = "描述显示：详细版",
        desc_detailed_off = "描述显示：简短版",
        bond_settings = "绑定系统设置",
        condloss_settings = "耐久损耗设置",
        hudbar_settings = "进度条设置",
        hb_scale = "整体缩放",
        hb_height = "条长度",
        hb_offset_y = "垂直偏移",
        hb_side_right = "显示在：右侧",
        hb_side_left = "显示在：左侧",
        hb_margin = "边距 ",
        hb_loading = "（未检测到进度条模块，请确认 Lua 补丁的 C# 部分已加载）",
        hb_empty = "（暂无进度条配置）",
        hb_select_hint = "（点击上方列表选择一条）",
        hb_enable = "显示该条",
        hb_reset = "重置为默认值",
        hb_r = "红",
        hb_g = "绿",
        hb_b = "蓝",
        bs_interval = "结算间隔（秒）",
        bs_resist = "转移抗性折算系数（0-1）",
        bs_distance = "链接距离上限（0 = 不限）",
        bs_types = "计入的 affliction 类型（逗号分隔）",
        bs_hint = "保存后即时生效；多人模式需要管理员权限（ConsoleCommands）",
        bs_module_missing = "（未检测到绑定系统模块）",
        bs_denied_prefix = "⚠ ",
        cd_mult = "受击耐久损耗全局倍率（0 = 关闭）",
        cd_hint = "曲线：损耗 = 10 × (伤害/48)² × tag倍率 × 全局倍率（默认 1）；配置方式：给任意可穿戴物品加 tag Touhou_Condition_Loss_Rate_X",
        damage_settings = "武器伤害与防具抗性设置",
        dmg_hint = "选择左侧分组调整倍率；保存后约 1 秒内生效。联机时修改需要管理员权限，改动会广播到所有人的聊天栏",
        dmg_damage = "伤害倍率",
        dmg_pen_mode_add = "穿甲：加算",
        dmg_pen_mode_mul = "穿甲：乘算",
        dmg_pen_value = "穿甲数值",
        dmg_defense = "防御倍率",
        dmg_save = "保存设置",
        dmg_reset = "重置默认",
        dmg_saved = "已保存，约 1 秒内生效",
        dmg_reset_done = "已重置为默认值并保存",
        dmg_loading = "（未检测到伤害设置模块，请确认 Lua 补丁的 C# 部分已加载）",
        dmg_groups_hint = "分组列表",
        dmg_items_fmt = "%d 件",
        dmg_status_fmt = "已应用：物品 %s · 数值对象 %s · %s",
        dmg_lv1 = "LV1（略弱于补强）",
        dmg_lv2 = "LV2（等于补强）",
        dmg_lv3 = "LV3（补强×2）",
        dmg_lv_missing = "（配置里没有档位数据，请更新 Config/damage_settings.xml）",
        dmg_mp_loading = "（正在从主机获取伤害设置…）",
        dmg_readonly_short = "只读：需要管理员权限",
        dmg_submitted = "已提交，约 1 秒内生效",
        dmg_submitted_short = "已提交，等待生效…",
        dmg_lv_submitted = "已提交档位：%s（约 1 秒内生效）",
        dmg_noedit_toast = "提交失败：无法写入请求文件（目录被占用？）",
        dmg_denied_generic = "服务器拒绝了本次修改",
        dmg_denied_prefix = "⚠ ",
        menu_hotkey = "快捷键设置",
        cl_settings = "装束锁定设置",
        cl_enable = "启用装束锁定",
        cl_lock_bots = "锁定AI船员",
        cl_lock_time = "锁定时长",
        cl_save = "保存设置",
        cl_reset = "重置默认",
        cl_reset_done = "装束锁定已重置为默认值：开启，120 秒",
        cl_no_permission = "修改需要控制台指令权限（ConsoleCommands）",
        cl_locked_list = "已锁定玩家",
        cl_total_suffix = "（共 %d 人，滚轮翻看）",
        cl_refresh = "刷新",
        cl_none = "（当前没有被锁定的玩家）",
        cl_unlock = "解锁",
        cl_unlock_all = "全部解锁",
        cl_unlock_all_confirm = "确认全部解锁？",
        cl_loading = "正在从服务器获取状态…",
        cl_module_missing = "装束锁定客户端模块未加载",
        cl_invalid_time = "锁定时长输入无效：请输入 10~600 的数字",
        back = "返回",
        double_press = "双击",
        reset_default = "重置为默认值",
        reset_done = "已重置为默认值：界面开关键 K、绑定面板 L，其余绑定未绑定（修饰键/双击已清空）",
        pg_weapons = "武器",
        pg_wearable = "装束",
        pg_outer = "界面与外套",
        pg_bag = "背包",
        pg_desc = "描述详略",
        page_external = "拓展模组",
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
        window_title = "Touhou Mod Settings",
        menukey_name = "Menu toggle key",
        menukey_hint = "Opens/closes this window",
        capturing = "Press any key or mouse button… (Esc cancels)",
        unbound = "Unbound",
        not_detected = " (not detected)",
        key_mouse_middle = "Mouse middle",
        key_mouse_side1 = "Mouse side 1",
        key_mouse_side2 = "Mouse side 2",
        mouse_unavailable = "Mouse button %s cannot be detected (LuaCs input API unavailable); binding will not fire",
        hint_line = "Click a key button, then press a new key or mouse button (Esc cancels). Multiple bindings may share one key and trigger together.",
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
        bd_weapon_btn3 = "Weapon button 3",
        bd_toggle_outfit = "Outfit UI visibility",
        bd_toggle_outer = "Outerwear UI visibility",
        bd_toggle_bag = "Backpack UI visibility",
        bd_outer1 = "Outerwear button 1",
        bd_outer2 = "Outerwear button 2",
        bd_bag1 = "Backpack button 1",
        bd_bag2 = "Backpack button 2",
        bd_bond_panel = "Bond panel",
        bd_desc_toggle = "Description toggle",
        desc_detailed_on = "Descriptions: detailed",
        desc_detailed_off = "Descriptions: short",
        bond_settings = "Bond system settings",
        condloss_settings = "Durability loss settings",
        hudbar_settings = "Progress bar settings",
        hb_scale = "Global scale",
        hb_height = "Bar length",
        hb_offset_y = "Vertical offset",
        hb_side_right = "Side: Right",
        hb_side_left = "Side: Left",
        hb_margin = "Margin ",
        hb_loading = "(Progress bar module not detected - is the Lua patch's C# part loaded?)",
        hb_empty = "(No progress bars configured)",
        hb_select_hint = "(Select a bar above)",
        hb_enable = "Show this bar",
        hb_reset = "Reset to defaults",
        hb_r = "R",
        hb_g = "G",
        hb_b = "B",
        bs_interval = "Settle interval (seconds)",
        bs_resist = "Resistance scale on transfer (0-1)",
        bs_distance = "Max link distance (0 = unlimited)",
        bs_types = "Counted affliction types (comma separated)",
        bs_hint = "Applies immediately on save; multiplayer requires admin permissions (ConsoleCommands)",
        bs_module_missing = "(Bond system module not detected)",
        bs_denied_prefix = "⚠ ",
        cd_mult = "Global durability loss multiplier on hit (0 = off)",
        cd_hint = "Curve: loss = 10 × (damage/48)² × tag multiplier × global multiplier (default 1); configure by adding tag Touhou_Condition_Loss_Rate_X to any wearable item",
        damage_settings = "Weapon damage & armor settings",
        dmg_hint = "Pick a group on the left to adjust multipliers; applies ~1s after saving. In multiplayer, edits require admin permission and are announced to everyone's chat",
        dmg_damage = "Damage multiplier",
        dmg_pen_mode_add = "Penetration: additive",
        dmg_pen_mode_mul = "Penetration: multiplier",
        dmg_pen_value = "Penetration value",
        dmg_defense = "Defense multiplier",
        dmg_save = "Save settings",
        dmg_reset = "Reset to defaults",
        dmg_saved = "Saved, applies within ~1s",
        dmg_reset_done = "Reset to defaults and saved",
        dmg_loading = "(Damage settings module not detected - is the Lua patch's C# part loaded?)",
        dmg_groups_hint = "Groups",
        dmg_items_fmt = "%d items",
        dmg_status_fmt = "Applied: %s items · %s values · %s",
        dmg_lv1 = "LV1 (below buff mod)",
        dmg_lv2 = "LV2 (= buff mod)",
        dmg_lv3 = "LV3 (buff mod ×2)",
        dmg_lv_missing = "(No tier data in config - update Config/damage_settings.xml)",
        dmg_mp_loading = "(Fetching damage settings from the host...)",
        dmg_readonly_short = "Read-only: admin permission required",
        dmg_submitted = "Submitted, takes effect within ~1s",
        dmg_submitted_short = "Submitted - applying...",
        dmg_lv_submitted = "Tier submitted: %s (within ~1s)",
        dmg_noedit_toast = "Submit failed: could not write the request file",
        dmg_denied_generic = "The server rejected the change",
        dmg_denied_prefix = "⚠ ",
        menu_hotkey = "Hotkey settings",
        cl_settings = "Costume Lock Settings",
        cl_enable = "Enable costume lock",
        cl_lock_bots = "Lock AI crew",
        cl_lock_time = "Lock time",
        cl_save = "Save settings",
        cl_reset = "Reset to default",
        cl_reset_done = "Costume lock reset to default: enabled, 120s",
        cl_no_permission = "Requires ConsoleCommands permission",
        cl_locked_list = "Locked players",
        cl_total_suffix = " (%d total, scroll to view)",
        cl_refresh = "Refresh",
        cl_none = "(no locked players)",
        cl_unlock = "Unlock",
        cl_unlock_all = "Unlock all",
        cl_unlock_all_confirm = "Confirm unlock all?",
        cl_loading = "Fetching state from server…",
        cl_module_missing = "Costume lock client module not loaded",
        cl_invalid_time = "Invalid lock time: enter a number between 10 and 600",
        back = "Back",
        double_press = "Dbl",
        reset_default = "Reset to defaults",
        reset_done = "Reset to defaults: menu key K, bond panel L, others unbound (modifiers/double cleared)",
        pg_weapons = "Weapons",
        pg_wearable = "Outfit",
        pg_outer = "UI & Outerwear",
        pg_bag = "Backpack",
        pg_desc = "Descriptions",
        page_external = "External mods",
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

local function dbg(msg)
    if DEBUG_LOG then print(T("log_prefix") .. msg) end
end

local function is_mouse_button(name)
    for _, n in ipairs(MOUSE_BUTTONS) do
        if n == name then return true end
    end
    return false
end

local function valid_key(name)
    if name == nil then return false end
    if is_mouse_button(name) then return true end
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

-- 绑定模型：{ name=显示名, key=触发键(""=未绑定), modifiers={}, target=目标 }
-- target: weapon:N = 手持武器第 N 个按钮；wearable/outer/bag:N = 对应槽位组第 N 个控件；toggle_hud* = 界面开关
-- 默认全空，交给玩家自己绑。新增只能往末尾追加——配置文件按 binding.N 存的，动顺序会炸老档；
-- v1 老档靠 load_config 里的 cfgver 迁移重排
local function default_bindings()
    return {
        { name = "bd_weapon_btn1", key = "", modifiers = {}, target = "weapon:1" },
        { name = "bd_weapon_btn2", key = "", modifiers = {}, target = "weapon:2" },
        { name = "bd_weapon_btn3", key = "", modifiers = {}, target = "weapon:3" },
        { name = "bd_wearable1", key = "", modifiers = {}, target = "wearable:1" },
        { name = "bd_wearable2", key = "", modifiers = {}, target = "wearable:2" },
        { name = "bd_wearable3", key = "", modifiers = {}, target = "wearable:3" },
        { name = "bd_wearable4", key = "", modifiers = {}, target = "wearable:4" },
        { name = "bd_toggle_outfit", key = "", modifiers = {}, target = "toggle_hud" },
        { name = "bd_toggle_outer", key = "", modifiers = {}, target = "toggle_hud_outer" },
        { name = "bd_toggle_bag", key = "", modifiers = {}, target = "toggle_hud_bag" },
        { name = "bd_outer1", key = "", modifiers = {}, target = "outer:1" },
        { name = "bd_outer2", key = "", modifiers = {}, target = "outer:2" },
        { name = "bd_bag1", key = "", modifiers = {}, target = "bag:1" },
        { name = "bd_bag2", key = "", modifiers = {}, target = "bag:2" },
        -- 绑定面板：Lua 调不动 C# 程序集，键位只存这，触发由 BondGui.cs 读 bondpanel.key 自己轮询
        { name = "bd_bond_panel", key = "L", modifiers = {}, target = "bondpanel" },
        -- 描述详略：按住≥0.3s 临时切简短版，轻点切锁定模式（C# 轮询 desc.detailed 行）
        { name = "bd_desc_toggle", key = "", modifiers = {}, target = "desc_toggle" },
    }
end

local config = {
    menukey = "K",
    bindings = default_bindings(),
    hud_hidden = false,        -- 装束槽位悬浮界面是否隐藏（随配置持久化）
    hud_hidden_outer = false,  -- 外套/潜水服槽位
    hud_hidden_bag = false,    -- 背包槽位
    desc_detailed = true,      -- 描述详略：true = 详细版（默认），false = 简短版（锁定模式）
}

-- 外部模组注册层（v3：id 制，纯增量，内置行为不变）。
-- 别的模组调 TouhouHotkey.RegisterBinding 注册，改键/持久化/双击/冲突检测都白拿
local extra_bindings = {}   -- id → 条目
local extra_order = {}      -- 注册顺序的 id 列表（分页与保存用）
local combined_cache = nil  -- 内置+外部合并视图缓存（注册时失效重建）

-- 合并视图：内置在前（索引与老语义一致），外部按注册顺序追加
local function get_combined()
    if combined_cache == nil then
        combined_cache = {}
        for _, b in ipairs(config.bindings) do combined_cache[#combined_cache + 1] = b end
        for _, id in ipairs(extra_order) do combined_cache[#combined_cache + 1] = extra_bindings[id] end
    end
    return combined_cache
end

-- 描述详略的按住/轻点状态（think 里维护）
local DESC_HOLD_SECONDS = 0.3  -- 按住超过此时长 = 临时简短版（松开恢复）；更短 = 轻点，切换锁定模式
local desc_press_time = nil    -- 描述切换键按下的时刻（os.clock），nil = 未按住
local desc_tapped = false      -- 本帧发生了一次“轻点”（守卫通过后消费）
local desc_last_eff = nil      -- 上次写入配置的实际模式（详细版=true），用于检测变化

local function desc_file_value()
    if desc_press_time ~= nil and os.clock() - desc_press_time >= DESC_HOLD_SECONDS then
        return "0"
    end
    return config.desc_detailed and "1" or "0"
end

-- 绑定匹配顺序缓存（修饰键多的优先）。不缓存的话每帧都得新建表再 sort，白扔一堆分配
local sorted_binding_order = nil
-- 双击绑定键集合缓存（纯配置派生，跟 sorted_binding_order 同一时刻失效）
local double_bound_keys_cache = nil

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
    double_bound_keys_cache = nil  -- 同点失效（双击绑定键集合也是纯配置派生）
    local lines = { "cfgver=2", "menukey=" .. config.menukey }
    lines[#lines + 1] = "hud_hidden=" .. (config.hud_hidden and "1" or "0")
    lines[#lines + 1] = "hud_hidden_outer=" .. (config.hud_hidden_outer and "1" or "0")
    lines[#lines + 1] = "hud_hidden_bag=" .. (config.hud_hidden_bag and "1" or "0")
    lines[#lines + 1] = "desc.detailed=" .. desc_file_value()  -- C# 描述切换（TalentDescToggle.cs）读取此行；按住期间写 0
    for i, b in ipairs(config.bindings) do
        lines[#lines + 1] = "binding." .. i .. ".key=" .. b.key
        lines[#lines + 1] = "binding." .. i .. ".modifiers=" .. table.concat(b.modifiers, ",")
        lines[#lines + 1] = "binding." .. i .. ".double=" .. (b.double and "1" or "0")
        if b.target == "bondpanel" then
            lines[#lines + 1] = "bondpanel.key=" .. b.key  -- C# 绑定面板（BondGui.cs）读取此行
        end
    end
    -- 外部注册绑定（id 制，增删/重排永不错位）
    for _, id in ipairs(extra_order) do
        local b = extra_bindings[id]
        lines[#lines + 1] = "bind." .. id .. ".key=" .. (b.key or "")
        lines[#lines + 1] = "bind." .. id .. ".modifiers=" .. table.concat(b.modifiers, ",")
        lines[#lines + 1] = "bind." .. id .. ".double=" .. (b.double and "1" or "0")
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
    -- 缺行时保持默认详细版（旧配置无此行）
    config.desc_detailed = kv["desc.detailed"] ~= "0"

    -- v1 → v2：老档把"武器改装"单列在最前，v2 并进武器按钮 1-3。remap 把老索引挪到新位置：
    -- 旧 1改装→新 1，旧 6/7 武器按钮→新 2/3，装束/界面等其余顺移
    if kv["cfgver"] ~= "2" then
        local remap = { [1] = 1, [2] = 6, [3] = 7, [4] = 2, [5] = 3, [6] = 4, [7] = 5 }
        local migrated = {}
        for new_i = 1, 15 do
            local old_i = remap[new_i] or new_i
            local k = kv["binding." .. old_i .. ".key"]
            local m = kv["binding." .. old_i .. ".modifiers"]
            if k ~= nil then migrated["binding." .. new_i .. ".key"] = k end
            if m ~= nil then migrated["binding." .. new_i .. ".modifiers"] = m end
        end
        for k, v in pairs(migrated) do kv[k] = v end
    end

    -- 旧版配置迁移：hotkey/modifiers → 绑定1（武器按钮1，原武器改装）
    if valid_key(kv["hotkey"]) then
        config.bindings[1].key = kv["hotkey"]
        config.bindings[1].modifiers = parse_modifiers(kv["modifiers"])
    end

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
        local dbl = kv["binding." .. i .. ".double"]  -- 旧文件无此行，默认单击（false）
        if dbl ~= nil then b.double = dbl == "1" end
    end

    for _, id in ipairs(extra_order) do
        local b = extra_bindings[id]
        local k = kv["bind." .. id .. ".key"]
        if k ~= nil and (k == "" or valid_key(k)) then b.key = k end
        local m = kv["bind." .. id .. ".modifiers"]
        if m ~= nil then b.modifiers = parse_modifiers(m) end
        local d = kv["bind." .. id .. ".double"]
        if d ~= nil then b.double = d == "1" end
    end
end

local function has_modifier(modifiers, name)
    for _, n in ipairs(modifiers) do
        if n == name then return true end
    end
    return false
end

-- 鼠标键不在 XNA Keys 里，得单独搞。LuaCs 每个版本开放的输入接口不一样，
-- 所以每个键备了几个候选检测器，挨个试、缓存第一个能用的，全废就报一次错不刷屏
local XnaMouse = register_static("Microsoft.Xna.Framework.Input.Mouse")

-- 由 XNA Mouse.GetState() 的按钮属性构造检测器（属性值与 ButtonState.Pressed 比较）
local function make_state_detector(prop)
    return function()
        if XnaMouse == nil then error("XNA Mouse unavailable") end
        return tostring(XnaMouse.GetState()[prop]) == "Pressed"
    end
end

local MOUSE_DETECTORS = {
    MouseMiddle = {
        function() return PlayerInput.MiddleMouseButtonHeld() end,
        make_state_detector("MiddleButton"),
    },
    MouseSide1  = { make_state_detector("XButton1") },
    MouseSide2  = { make_state_detector("XButton2") },
}

local mouse_detector_cache = {}   -- 按钮名 -> 可用的检测器函数
local mouse_detector_failed = {}  -- 按钮名 -> 全部检测器均不可用

local function resolve_mouse_detector(name)
    if mouse_detector_cache[name] ~= nil then return mouse_detector_cache[name] end
    if mouse_detector_failed[name] then return nil end
    for _, det in ipairs(MOUSE_DETECTORS[name]) do
        local ok, result = pcall(det)
        if ok and type(result) == "boolean" then
            mouse_detector_cache[name] = det
            return det
        end
    end
    mouse_detector_failed[name] = true
    print(T("log_prefix") .. string.format(T("mouse_unavailable"), name))
    return nil
end

local function raw_mouse_down(name)
    local det = resolve_mouse_detector(name)
    if det == nil then return false end
    local ok, result = pcall(det)
    return ok and result == true
end

-- 不用 PlayerInput.KeyHit/KeyDown：那套被 AllowInput 门控，按住移动键时事件可能被吞。
-- 直接读原始键盘状态、自己记上一帧做边沿检测，边跑边按也能稳定触发
local key_was_down = {}  -- 按键名 -> 上一帧是否按下
local double_last_press = {}  -- 按键名 -> 上一次按下的时刻（双击检测用）
local key_hits = {}      -- 按键名 -> 本帧是否新按下（think 每帧开头清空复用，不再每帧新建）
local double_hits = {}   -- 按键名 -> 本帧是否双击（同上）

-- 键盘状态读取链路：加载时探测一次（照 pause_toggle_probe 的写法），结果存模块级标志。
-- 通过 -> 热路径直连静态调用，不再每帧 pcall、不再建闭包；
-- 没通过 -> 退回下面原来的 pcall 实现，语义跟改动前一模一样，且不把结果固化，
-- 免得加载那一瞬间的一次抖动把热键系统永久废掉
local key_api_direct = pcall(function()
    return PlayerInput.GetKeyboardState.IsKeyDown(Keys["Escape"])
end)

-- 按键名 -> XNA Keys 枚举值缓存（普通表）。名字来自 KEY_LIST 白名单，加载后查一次就够，
-- 省掉每帧的静态成员查找；查不到的按“读不到就是没按下”处理（跟原来 pcall 兜住异常的结果一致）
local key_code_cache = {}
local function key_code(name)
    local code = key_code_cache[name]
    if code == nil then
        local ok, v = pcall(function() return Keys[name] end)
        code = (ok and v ~= nil) and v or false
        key_code_cache[name] = code
    end
    return code
end

local function raw_key_down(name)
    if is_mouse_button(name) then
        return raw_mouse_down(name)
    end
    if key_api_direct then
        local code = key_code(name)
        if code == false then return false end
        return PlayerInput.GetKeyboardState.IsKeyDown(code) == true
    end
    local ok, down = pcall(function()
        return PlayerInput.GetKeyboardState.IsKeyDown(Keys[name])
    end)
    return ok and down == true
end

-- 每帧调一次：本帧按下、上帧没按 = 触发
local function poll_key_hit(name)
    local down = raw_key_down(name)
    local hit = down and not key_was_down[name]
    key_was_down[name] = down
    return hit
end

-- 把“上一帧”记录同步为当前真实状态（退出按键捕获后调用，防止松手前被误判为新按下）
local function sync_key_states()
    key_was_down[config.menukey] = raw_key_down(config.menukey)
    for _, b in ipairs(get_combined()) do
        if b.key ~= "" then
            key_was_down[b.key] = raw_key_down(b.key)
        end
    end
end

-- 要求的修饰键都按住就行，多按的不碍事（按住 Shift 奔跑照触发）。
-- 分发时修饰键多的先匹配，所以 J 和 Shift+J 并存时按 Shift+J 命中带修饰那条
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

-- 冲突规则：普通绑定互相撞键无所谓（一起触发），只有界面开关键必须唯一，双向都不许撞
local function conflicts_with_menukey(key, modifiers)
    if key == "" then return false end
    return binding_signature(key, modifiers) == binding_signature(config.menukey, {})
end

local function menukey_conflicts_with_bindings(key)
    local sig = binding_signature(key, {})
    for _, b in ipairs(get_combined()) do
        if b.key ~= "" and binding_signature(b.key, b.modifiers) == sig then
            return true, b.name
        end
    end
    return false
end

local MOUSE_DISPLAY_KEYS = {
    MouseMiddle = "key_mouse_middle",
    MouseSide1 = "key_mouse_side1", MouseSide2 = "key_mouse_side2",
}

local function key_display_name(name)
    local dk = MOUSE_DISPLAY_KEYS[name]
    if dk ~= nil then return T(dk) end
    if is_modifier_name(name) then return MODIFIER_DISPLAY[name] end
    return name
end

local function binding_display(b)
    if b.key == "" then return T("unbound") end
    local parts = {}
    for _, name in ipairs(MODIFIER_NAMES) do
        if has_modifier(b.modifiers, name) then table.insert(parts, MODIFIER_DISPLAY[name]) end
    end
    table.insert(parts, key_display_name(b.key))
    local s = table.concat(parts, " + ")
    if b.double then s = "2× " .. s end  -- 双击绑定的显示前缀
    return s
end

-- 脏配置自愈：只清跟界面开关键撞车的绑定（绑定之间共用按键是特性，不管）
local function sanitize_config()
    for _, b in ipairs(get_combined()) do
        if conflicts_with_menukey(b.key, b.modifiers) then
            b.key = ""
            b.modifiers = {}
        end
    end
end

-- 不能反射，靠 pcall 探测属性认控件类型
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

-- 枚举物品 CustomInterface 上的可点控件（按钮+复选框），顺序跟 XML 一致。三个坑：
--  · UserData.StatusEffects 读不出（internal 类），真按钮会被误判成"无效果"，不能拿它过滤；
--  · 官方重建 UI 不销毁旧容器，GuiFrame 里会留失效控件，按文本去重、后到的覆盖先到的；
--  · 没文本的（残留/装饰）直接跳过
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

-- 跟鼠标点一下完全等价：按钮直接 Invoke 原版委托（C# 委托必须 Invoke），
-- 复选框翻 Selected，setter 自己会触发原版 OnSelected（联机同步也走里面）
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

local function is_touhou_outfit(item)
    -- Subcategory 是普通属性，直接读；原来那层 pcall 包的是个不会有副作用、也不会抛的属性读
    local sub = nil
    if item ~= nil and item.Prefab ~= nil then sub = tostring(item.Prefab.Subcategory) end
    if sub ~= nil and string.lower(sub) == string.lower(OUTFIT_SUBCATEGORY) then
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

-- 枚举指定槽位组当前装备上的可点控件（组内槽位顺序 + XML 定义顺序）
-- group_name: wearable = 装束（仅东方装备）；outer = 外套/潜水服；bag = 背包
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

local function get_wearable_buttons(character)
    return get_group_buttons(character, "wearable")
end

-- 触发手持武器上的第 N 个按钮（按 XML 定义顺序枚举全部按钮，含"改装"——v2 合并后改装不再单列）
local function try_trigger_weapon_button(character, n)
    for item in character.HeldItems do
        if is_mod_weapon(item) then
            local buttons = find_buttons(item)
            if n <= #buttons then
                click_button(buttons[n])
                dbg("已触发武器按钮「" .. buttons[n].label .. "」（来自「" .. tostring(item.Name) .. "」）")
                return true
            end
        end
    end
    return false
end

-- DrawHudWhenEquipped 是 protected set，改不了；但 CharacterHUD 画悬浮界面前会查
-- GuiFrame.Visible（false 就跳过），而 Visible 是公开可写的，所以靠切 Visible 达到同样效果。
-- 坑：UI 重建（分辨率/缩放变化）会把 Visible 重置回 true，所以隐藏状态得周期性重新压回去（见 think）
local function set_slots_hud_visible(slots, visible)
    local character = Character.Controlled
    if character == nil or character.Inventory == nil then return end
    for _, slot in ipairs(slots) do
        local item = character.Inventory.GetItemInLimbSlot(InvSlotType[slot])
        if item ~= nil then
            for component in item.Components do
                local ok, frame = pcall(function() return component.GuiFrame end)
                if ok and frame ~= nil then
                    -- 值一样就不写：每 30 帧无条件赋同值会让 UI 白白重建/重排（读和写同一个 pcall 里，异常照旧吞掉）
                    pcall(function()
                        if frame.Visible ~= visible then
                            frame.Visible = visible
                        end
                    end)
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
    -- 绑定面板只存键位，触发在 C# 端（BondGui.cs 轮询 bondpanel.key）
    if b.target == "bondpanel" then return true end

    -- desc_toggle 不走这边，由 think 每帧跟按住/轻点（见 DESC_HOLD_SECONDS）

    -- 悬浮界面开关（装束/外套/背包，不依赖当前装备，随时可切）
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

local menu_frame = nil
local menu_page = "hub"      -- 多级菜单当前页："hub" = 主菜单入口页，"bindings" = 快捷键设置页
local capturing = nil        -- 绑定索引（数字）或 "menukey" 或 nil
local capture_ignored = {}   -- 进入捕获模式时已按住的键（防止被立即误捕获）
local menukey_button = nil
local binding_buttons = {}   -- 每条绑定的按键按钮
local detect_labels = {}     -- 装束技能行的“检测到的技能名”标签
local frame_counter = 0
local hud_enforce_counter = 0  -- 装束悬浮界面隐藏状态的强制刷新计数
local pause_toggle_probe = nil  -- GUI.PreventPauseMenuToggle 属性存在性探测结果（nil = 未探测）
local input_blocking_probe = nil  -- GUI.InputBlockingMenuOpen 属性存在性探测结果（nil = 未探测）
-- 内置分页（按功能分组；索引跟 default_bindings 顺序一致）
local BUILTIN_PAGES = {
    { first = 1,  last = 3,  label_key = "pg_weapons"  },   -- 武器按钮 1-3
    { first = 4,  last = 7,  label_key = "pg_wearable" },   -- 装束技能 1-4
    { first = 8,  last = 12, label_key = "pg_outer"    },   -- 界面显示 3 + 外套按钮 2
    { first = 13, last = 15, label_key = "pg_bag"      },   -- 背包按钮 2 + 绑定面板
    { first = 16, last = 16, label_key = "pg_desc"     },   -- 描述详略切换
}

local function get_pages()
    local pages = {}
    for _, p in ipairs(BUILTIN_PAGES) do
        local items = {}
        for i = p.first, math.min(p.last, #config.bindings) do items[#items + 1] = i end
        if #items > 0 then
            pages[#pages + 1] = { label = T(p.label_key), items = items }
        end
    end
    local by_mod = {}
    local base = #config.bindings
    for pos, id in ipairs(extra_order) do
        local b = extra_bindings[id]
        local m = b.mod or T("page_external")
        if by_mod[m] == nil then
            by_mod[m] = { label = m, items = {} }
            pages[#pages + 1] = by_mod[m]
        end
        by_mod[m].items[#by_mod[m].items + 1] = base + pos
    end
    return pages
end

local current_page = 1
local flag_poll_counter = 0  -- C# 设置窗口「返回」标记的轮询节流计数
-- 装束锁定设置页状态（数据来自 Touhou_Costume_Lock_Client.lua 的 TLE.CostumeLockClient 缓存）
local cl_pending_enabled = nil  -- 编辑中的启用开关（nil = 未改动，跟随服务器值）
local cl_pending_time = nil     -- 编辑中的锁定时长（秒）
local cl_pending_bots = nil     -- 编辑中的 AI 船员锁定开关
local cl_dirty = false          -- 有未保存的编辑（状态刷新重建页面时保留暂存值）
local cl_skip_request = false   -- OnState 重建页面时置真，避免"请求→重建→再请求"循环
local cl_unlockall_confirm = nil  -- 全部解锁的二击确认时刻（os.clock）
local cl_time_box = nil      -- 锁定时长输入框（GUITextBox；构造失败时为 nil，退回纯步进按钮）
local cl_list_box = nil      -- 锁定列表 ListBox（跨重建保存滚动位置用）
local cl_list_scroll = 0     -- 锁定列表滚动位置（0~1，重建页面后恢复）

local function refresh_binding_texts()
    if menukey_button ~= nil then
        if capturing == "menukey" then
            menukey_button.Text = RawLString(T("capturing"))
        else
            menukey_button.Text = RawLString(key_display_name(config.menukey))
        end
    end
    for i, btn in pairs(binding_buttons) do
        if capturing == i then
            btn.Text = RawLString(T("capturing"))
        else
            btn.Text = RawLString(binding_display(get_combined()[i]))
        end
    end
end

-- 刷新槽位组绑定行显示的“当前检测到的控件名称”（装束技能/外套按钮/背包按钮）
local function refresh_detected_labels()
    if menu_frame == nil then return end
    local group_lists = {}  -- 组名 -> 控件列表（每组只枚举一次）
    for i, label in pairs(detect_labels) do
        local b = get_combined()[i]
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
    -- 鼠标键同理：点击进入捕获时的那一下点击不能算数，等松开
    for _, name in ipairs(MOUSE_BUTTONS) do
        if raw_mouse_down(name) then
            capture_ignored[name] = true
        end
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
    -- 别在这重置 menu_page：open_menu 开头会调本函数，一重置子页就永远进不去了
    capturing = nil
    capture_ignored = {}
    sync_key_states()
    if menu_frame ~= nil then
        -- 关窗/重建前记下锁定列表的滚动位置，重建后恢复（避免刷新后跳回顶部）
        if cl_list_box ~= nil then
            pcall(function() cl_list_scroll = cl_list_box.ScrollBar.BarScroll end)
        end
        GUI.GUI.RemoveFromUpdateList(menu_frame, true)
        menu_frame.RectTransform.Parent = nil
        menu_frame = nil
        menukey_button = nil
        binding_buttons = {}
        detect_labels = {}
        cl_time_box = nil
        cl_list_box = nil
    end
end

local function add_row(layout, height)
    -- 间距收紧：6 个修饰键复选框 + 双击框的行不再超出窗口
    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, height), layout.RectTransform), true, GUI.Anchor.CenterLeft)
    row.RelativeSpacing = 0.006
    return row
end

-- ==================== 进度条设置页（hudbars） ====================
-- C# 侧（HudAfflictionBars.cs）把合并后的配置写到 TouhouHudBarsState.txt（与热键配置同目录，
-- 只读快照，含本地化后的名称与默认值）；本页读取展示，玩家改动写 TouhouHudBarsConfig.txt，
-- C# 每帧查 mtime 即时重载并回写状态。两边内容相同时互不触发，无回环。
local HB_H = 0.058
local hb_state = nil      -- 解析后的状态表
local hb_state_raw = nil  -- 原始文本（变化检测）
local hb_selected = nil   -- 选中的条索引
local hb_poll_counter = 0
local hb_pending_rebuild = false  -- 拖动滑条期间挂起的页面重建

-- 鼠标左键是否按住（拖动滑条判定；pcall 兜底上下文差异）
local function hb_mouse_held()
    local ok, held = pcall(function() return PlayerInput.PrimaryMouseButtonHeld() end)
    return ok and held == true
end

local function hb_dir_file(name)
    local p = get_config_path()
    return (string.gsub(p, "[^/\\]+$", name))
end

local function hb_parse_state(text)
    local st = { bars = {}, order = {} }
    for line in string.gmatch(text or "", "[^\r\n]+") do
        local k, v = string.match(line, "^([^=]+)=(.*)$")
        if k ~= nil then
            if k == "scale" or k == "offsetx" or k == "offsety" or k == "barheight" then
                st[k] = tonumber(v)
            elseif k == "side" then
                st.side = v
            else
                local idx, prop = string.match(k, "^bar%.(%d+)%.(.+)$")
                if idx ~= nil then
                    idx = tonumber(idx)
                    if st.bars[idx] == nil then
                        st.bars[idx] = {}
                        st.order[#st.order + 1] = idx
                    end
                    st.bars[idx][prop] = v
                end
            end
        end
    end
    return st
end

local function hb_load_state()
    if File == nil then return nil end
    local ok, text = pcall(function() return File.ReadAllText(hb_dir_file("TouhouHudBarsState.txt")) end)
    if not ok or text == nil then return nil end
    hb_state_raw = text
    return hb_parse_state(text)
end

local function hb_write_config()
    if File == nil or hb_state == nil then return end
    local lines = {}
    lines[#lines + 1] = "scale=" .. string.format("%.2f", hb_state.scale or 1)
    lines[#lines + 1] = "side=" .. (hb_state.side or "right")
    lines[#lines + 1] = "offsetx=" .. string.format("%.0f", hb_state.offsetx or 70)
    lines[#lines + 1] = "offsety=" .. string.format("%.0f", hb_state.offsety or 0)
    lines[#lines + 1] = "barheight=" .. string.format("%.0f", hb_state.barheight or 180)
    for _, idx in ipairs(hb_state.order) do
        local b = hb_state.bars[idx]
        lines[#lines + 1] = "bar." .. tostring(b.id) .. ".enabled=" .. (b.enabled == "1" and "1" or "0")
        lines[#lines + 1] = "bar." .. tostring(b.id) .. ".color=" .. tostring(b.color or "255,255,255")
    end
    pcall(function()
        File.WriteAllText(hb_dir_file("TouhouHudBarsConfig.txt"), table.concat(lines, "\n"))
    end)
end

local function hb_parse_color(text)
    local r, g, b = string.match(tostring(text or ""), "(%d+),%s*(%d+),%s*(%d+)")
    return tonumber(r) or 255, tonumber(g) or 255, tonumber(b) or 255
end

-- 与 open_menu 内的 add_text 等价（模块级复刻，hb_slider_row 在 open_menu 外定义，够不到那个 local）
local function hb_add_text(rel_size, parent_rt, text, alignment)
    local block = GUI.TextBlock(GUI.RectTransform(rel_size, parent_rt), RichString.Plain(RawLString(text)))
    block.TextAlignment = alignment
    return block
end

-- 通用滑条行：标签 + 滑条 + 值文本；宽度预算 0.21+0.57+0.20+2×0.006 ≈ 1.0
local function hb_slider_row(layout, height, label, min, max, fmt, get, set)
    local row = add_row(layout, height)
    hb_add_text(Vector2(0.21, 1), row.RectTransform, label, GUI.Alignment.CenterLeft)
    local scroll = GUI.ScrollBar(GUI.RectTransform(Vector2(0.57, 1), row.RectTransform), 0.1)
    scroll.Range = Vector2(min, max)
    local value_text = hb_add_text(Vector2(0.20, 1), row.RectTransform, string.format(fmt, get()), GUI.Alignment.CenterLeft)
    scroll.BarScroll = math.min(math.max((get() - min) / (max - min), 0), 1)
    scroll.OnMoved = function(bar, s)
        set(min + (max - min) * s)
        value_text.Text = RichString.Plain(RawLString(string.format(fmt, get())))
        hb_write_config()
        return true
    end
end

-- ==================== 武器伤害与防具抗性设置页（damage） ====================
-- 数据流（联机全由 C# 负责传输，本页不碰网络）：
--   本页「保存/档位/重置」→ 写 TouhouDamageRequest.txt；
--   C# 桥接（DamageBridge）：权威端本机应用 / 客户端发服务器；
--   服务器应用后广播状态 → 客户端 C# 把状态写进本机 TouhouDamageState.txt（本页只读它）。
local dmg_state = nil       -- 解析后的状态（含 stamp/patched 与分组、canedit/denied）
local dmg_state_raw = nil   -- 原始文本（变化检测）
local dmg_groups = {}       -- 分组数组（显示顺序）
local dmg_selected = nil    -- 选中分组 id
local dmg_pending = {}      -- 编辑暂存：gid -> { damage / penmode / penvalue / defense }
local dmg_dirty = false     -- 有未保存编辑
local dmg_poll_counter = 0
local dmg_last_submit_at = -1  -- 上次提交时刻（os.clock），用于状态行短暂提示
local dmg_last_denied_shown = nil -- 已弹窗过的拒绝原因（避免重复弹）

local function dmg_num(v)
    return tonumber(v) or 0
end

local function dmg_parse_state(text)
    local st = { groups = {}, order = {}, patched = {} }
    for line in string.gmatch(text or "", "[^\r\n]+") do
        local k, v = string.match(line, "^([^=]+)=(.*)$")
        if k ~= nil then
            if k == "stamp" then
                st.stamp = v
            elseif k == "patched.items" or k == "patched.objects" then
                st.patched[k] = v
            elseif k == "canedit" then
                st.canedit = (v == "1")
            elseif k == "denied" then
                st.denied = v
            else
                local gid, prop = string.match(k, "^group%.([^.]+)%.(.+)$")
                if gid ~= nil then
                    local g = st.groups[gid]
                    if g == nil then
                        g = { id = gid, label = gid, kind = "weapon", count = 0 }
                        st.groups[gid] = g
                        st.order[#st.order + 1] = gid
                    end
                    if prop == "count" then g.count = dmg_num(v)
                    elseif prop == "label" then g.label = v
                    elseif prop == "kind" then g.kind = v
                    else
                        local lv, sub = string.match(prop, "^lv(%d)%.(.+)$")
                        if lv ~= nil then
                            g.levels = g.levels or {}
                            local lvn = tonumber(lv)
                            g.levels[lvn] = g.levels[lvn] or {}
                            g.levels[lvn][sub] = v
                        else
                            g[prop] = v
                        end
                    end
                end
            end
        end
    end
    return st
end

local function dmg_set_state_from_text(text)
    dmg_state_raw = text
    local st = dmg_parse_state(text)
    dmg_state = st
    dmg_groups = {}
    for _, gid in ipairs(st.order) do
        dmg_groups[#dmg_groups + 1] = st.groups[gid]
    end
    return st
end

local function dmg_load_state()
    if File == nil then return nil end
    local path = hb_dir_file("TouhouDamageState.txt")
    local ok, text = pcall(function()
        if not File.Exists(path) then return nil end
        return File.ReadAllText(path)
    end)
    if not ok or text == nil then return nil end
    return dmg_set_state_from_text(text)
end

-- 暂存跟随最新状态（未脏时）；进入页面与状态刷新时调用
local function dmg_sync_pending()
    dmg_pending = {}
    for _, g in ipairs(dmg_groups) do
        dmg_pending[g.id] = {
            damage = dmg_num(g.damage),
            penmode = g.penmode or "add",
            penvalue = dmg_num(g.penvalue),
            defense = dmg_num(g.defense),
        }
    end
end

local function dmg_pending_of(gid)
    local p = dmg_pending[gid]
    if p == nil then
        dmg_sync_pending()
        p = dmg_pending[gid]
    end
    return p
end

local function dmg_fmt_num(v)
    return string.format("%.4g", v)
end

-- 玩家值全文（写进请求文件；C# 桥接解析同一格式）
local function dmg_build_config_text()
    local lines = {
        "# 东方-武器伤害与防具抗性设置 · 玩家值（设置页写入请求文件，C# 桥接处理）",
        "# 键：damage.<组>=倍率 · pen.<组>=add:<加值> 或 multiply:<乘数> · def.<组>=防御倍率",
        "ver=1",
    }
    for _, g in ipairs(dmg_groups) do
        local p = dmg_pending[g.id]
        if p ~= nil then
            if g.kind == "armor" then
                lines[#lines + 1] = string.format("def.%s=%s", g.id, dmg_fmt_num(p.defense))
            else
                lines[#lines + 1] = string.format("damage.%s=%s", g.id, dmg_fmt_num(p.damage))
                lines[#lines + 1] = string.format("pen.%s=%s:%s", g.id, p.penmode or "add", dmg_fmt_num(p.penvalue))
            end
        end
    end
    return table.concat(lines, "\n")
end

-- 当前是否可编辑：联机时看服务器下发的 canedit（本地无状态时先按可编辑显示，服务端仍是最终门槛）
local function dmg_can_edit()
    if Game.IsSingleplayer then return true end
    if dmg_state ~= nil and dmg_state.canedit == false then return false end
    return true
end

-- 提交玩家值：只写请求文件（TouhouDamageRequest.txt），传输/应用/权限全由 C# 桥接负责。
-- 返回 true 表示请求已落盘。
local function dmg_write_config()
    if File == nil then return false end
    local text = dmg_build_config_text()
    local ok = pcall(function()
        File.WriteAllText(hb_dir_file("TouhouDamageRequest.txt"), text)
    end)
    if ok then
        dmg_last_submit_at = os.clock and os.clock() or -1
    end
    return ok
end

-- 档位快选：把全部组按档位值（配置 XML 的 Tier）写入暂存并提交（一键生效）
local function dmg_apply_tier(level)
    local applied = false
    for _, g in ipairs(dmg_groups) do
        local lv = g.levels and g.levels[level]
        local p = dmg_pending_of(g.id)
        if lv ~= nil and p ~= nil then
            if g.kind == "armor" then
                p.defense = dmg_num(lv.defense)
            else
                p.damage = dmg_num(lv.damage)
                p.penmode = lv.penmode or "add"
                p.penvalue = dmg_num(lv.penvalue)
            end
            applied = true
        end
    end
    if not applied then
        pcall(function() GUI.AddMessage(T("dmg_lv_missing"), Color(255, 160, 120, 255)) end)
        return
    end
    dmg_dirty = false
    local label = "LV" .. tostring(level)
    if dmg_write_config() then
        pcall(function() GUI.AddMessage(string.format(T("dmg_lv_submitted"), label), Color(150, 255, 150, 255)) end)
        print(T("log_prefix") .. string.format(T("dmg_lv_submitted"), label))
    else
        pcall(function() GUI.AddMessage(T("dmg_noedit_toast"), Color(255, 140, 120, 255)) end)
    end
end

-- 滑条行（与 hb_slider_row 同款；拖动只改暂存 + 刷新数值文本，量化到 0.01）
local function dmg_slider_row(layout, height, label, min, max, fmt, get, set)
    local row = add_row(layout, height)
    hb_add_text(Vector2(0.30, 1), row.RectTransform, label, GUI.Alignment.CenterLeft)
    local scroll = GUI.ScrollBar(GUI.RectTransform(Vector2(0.46, 1), row.RectTransform), 0.1)
    scroll.Range = Vector2(min, max)
    local value_text = hb_add_text(Vector2(0.21, 1), row.RectTransform, string.format(fmt, get()), GUI.Alignment.CenterLeft)
    scroll.BarScroll = math.min(math.max((get() - min) / (max - min), 0), 1)
    scroll.OnMoved = function(bar, s)
        set(math.floor((min + (max - min) * s) * 100 + 0.5) / 100)
        dmg_dirty = true
        value_text.Text = RichString.Plain(RawLString(string.format(fmt, get())))
        return true
    end
end

-- ==================== 绑定/耐久设置页（bondsettings/condloss） ====================
-- C# 侧 BondSettingsBridge（BondGui.cs）：状态快照 TouhouBondState.txt（含 denied 反馈），
-- 本页「保存」写 TouhouBondRequest.txt，C# 每帧查 mtime 后走 BondNet 原有的网络/权限路径。
local bond_state = nil
local bond_state_raw = nil
local bond_poll_counter = 0
local bs_boxes = {}

local function bond_parse_state(text)
    local st = {}
    for line in string.gmatch(text or "", "[^\r\n]+") do
        local k, v = string.match(line, "^([^=]+)=(.*)$")
        if k ~= nil then st[k] = v end
    end
    return st
end

local function bond_load_state()
    if File == nil then return nil end
    local path = hb_dir_file("TouhouBondState.txt")
    local ok, text = pcall(function()
        if not File.Exists(path) then return nil end
        return File.ReadAllText(path)
    end)
    if not ok or text == nil then return nil end
    bond_state_raw = text
    return bond_parse_state(text)
end

local function bond_write_request(entries)
    if File == nil or #entries == 0 then return end
    pcall(function()
        File.WriteAllText(hb_dir_file("TouhouBondRequest.txt"), table.concat(entries, "\n"))
    end)
end

-- 文本框字段：标签单独一行 + 文本框单独一行。
-- 对照实验证明：标签(TextBlock)和文本框(GUITextBox)在同一水平布局行里会导致行尺寸坍缩成 0
-- （文本块照常显示、文本框不可见），两者分开成两行各自渲染正常。
local function bs_textbox_row(layout, label, key, pending)
    hb_add_text(Vector2(1, 0.042), layout.RectTransform, label, GUI.Alignment.CenterLeft)
    local row = add_row(layout, 0.058)
    local box = nil
    local ok = pcall(function()
        box = GUI.TextBox(GUI.RectTransform(Vector2(1, 1), row.RectTransform), RawLString(tostring(pending[key] or "")))
    end)
    if not ok or box == nil then
        ok = pcall(function()
            box = GUI.CreateTextBoxWithPlaceholder(GUI.RectTransform(Vector2(1, 1), row.RectTransform), tostring(pending[key] or ""), RawLString(""))
        end)
    end
    if box ~= nil then bs_boxes[key] = box end
end

local function open_menu()
    close_menu()

    -- 加宽窗口（0.40→0.56）：容纳左右两侧修饰键复选框
    menu_frame = GUI.Frame(GUI.RectTransform(Vector2(0.56, 0.70), GUI.GUI.Canvas, GUI.Anchor.Center), "ItemUI")

    local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.92, 0.96), menu_frame.RectTransform, GUI.Anchor.Center))
    layout.RelativeSpacing = 0.012
    layout.Stretch = true

    -- GUITextBlock 只吃 RichString（按钮/复选框才吃 LocalizedString），LuaCs 不做隐式转换，
    -- 必须 RichString.Plain 显式包一层；对齐靠属性设
    local function add_text(rel_size, parent_rt, text, alignment)
        local block = GUI.TextBlock(GUI.RectTransform(rel_size, parent_rt), RichString.Plain(RawLString(text)))
        block.TextAlignment = alignment
        return block
    end

    -- 固定 10 行布局（标题 + 界面开关键 + 每页5条绑定 + 提示 + 翻页栏 + 关闭），行高不用压缩
    local ROW_H = 0.085

    if menu_page == "hub" then
        add_text(Vector2(1, ROW_H), layout.RectTransform, T("window_title"), GUI.Alignment.Center)

        local btn_hotkey = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("menu_hotkey")))
        btn_hotkey.OnClicked = function()
            menu_page = "bindings"
            pcall(open_menu)
            return true
        end

        local btn_bond = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("bond_settings")))
        btn_bond.OnClicked = function()
            -- 绑定设置是本菜单的子页面，直接切页（数据经 TouhouBondState.txt 快照读取）
            bond_state = nil  -- 强制重读状态快照
            menu_page = "bondsettings"
            pcall(open_menu)
            return true
        end

        local btn_condloss = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("condloss_settings")))
        btn_condloss.OnClicked = function()
            bond_state = nil
            menu_page = "condloss"
            pcall(open_menu)
            return true
        end

        local btn_hudbar = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("hudbar_settings")))
        btn_hudbar.OnClicked = function()
            -- 进度条设置是本菜单的子页面，直接切页（同尺寸窗口原位替换，零延迟）
            hb_selected = nil
            hb_state = nil  -- 强制重读状态快照
            menu_page = "hudbars"
            pcall(open_menu)
            return true
        end

        local btn_damage = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("damage_settings")))
        btn_damage.OnClicked = function()
            -- 武器伤害与防具抗性设置是本菜单的子页面（读 TouhouDamageState.txt / 写 TouhouDamageRequest.txt，传输由 C# 桥接）
            dmg_state = nil
            dmg_dirty = false
            dmg_selected = nil
            menu_page = "damage"
            pcall(open_menu)
            return true
        end

        local btn_costumelock = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("cl_settings")))
        btn_costumelock.OnClicked = function()
            -- 进入页面时重置编辑暂存（跟随服务器最新值）
            cl_pending_enabled = nil
            cl_pending_time = nil
            cl_pending_bots = nil
            cl_dirty = false
            cl_list_scroll = 0
            cl_unlockall_confirm = nil
            menu_page = "costumelock"
            pcall(open_menu)
            return true
        end

        local btn_close = GUI.Button(GUI.RectTransform(Vector2(1, ROW_H), layout.RectTransform), RawLString(T("close")))
        btn_close.OnClicked = function()
            close_menu()
            return true
        end
        return
    end

    if menu_page == "costumelock" then
        local CL = TLE.CostumeLockClient
        add_text(Vector2(1, ROW_H), layout.RectTransform, T("window_title") .. " - " .. T("cl_settings"), GUI.Alignment.Center)

        if CL == nil then
            add_text(Vector2(1, ROW_H), layout.RectTransform, T("cl_module_missing"), GUI.Alignment.Center)
        else
            -- 进入页面时向服务器请求最新状态；OnState 回调重建页面时跳过，避免"请求→重建→再请求"循环
            if not cl_skip_request then CL.RequestState() end
            cl_skip_request = false

            if not cl_dirty then
                cl_pending_enabled = CL.enabled
                cl_pending_time = CL.lock_time
                cl_pending_bots = CL.lock_bots
            end
            local can_edit = CL.can_edit == true

            -- 启用开关 + AI 船员锁定开关（同一行并排，行数不增）
            do
                local row = add_row(layout, ROW_H)
                local tick = GUI.TickBox(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("cl_enable")))
                tick.Selected = cl_pending_enabled == true
                tick.Enabled = can_edit
                tick.OnSelected = function(tb)
                    cl_pending_enabled = tb.Selected
                    cl_dirty = true
                    return true
                end
                local tick_bots = GUI.TickBox(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("cl_lock_bots")))
                tick_bots.Selected = cl_pending_bots == true
                tick_bots.Enabled = can_edit
                tick_bots.OnSelected = function(tb)
                    cl_pending_bots = tb.Selected
                    cl_dirty = true
                    return true
                end
            end

            do
                local row = add_row(layout, ROW_H)
                add_text(Vector2(0.18, 1), row.RectTransform, T("cl_lock_time"), GUI.Alignment.CenterLeft)

                -- 读取文本框当前输入；返回 nil 表示无输入框或内容不是数字
                local function read_box_value()
                    if cl_time_box == nil then return nil end
                    local ok, text = pcall(function() return tostring(cl_time_box.Text) end)
                    if not ok or text == nil then return nil end
                    local n = tonumber(text)
                    if n == nil then return nil end
                    return math.floor(n)
                end

                -- 步进按钮：以文本框当前内容（非法时回退暂存值）为基数调节，随后重建页面刷新显示
                local function step_btn(label, delta)
                    local b = GUI.Button(GUI.RectTransform(Vector2(0.08, 1), row.RectTransform), RawLString(label))
                    b.Enabled = can_edit
                    b.OnClicked = function()
                        local base = read_box_value() or cl_pending_time or CL.lock_time
                        cl_pending_time = math.floor(math.min(math.max(base + delta, 10), 600))
                        cl_dirty = true
                        cl_skip_request = true
                        pcall(open_menu)
                        return true
                    end
                end
                step_btn("-60", -60)
                step_btn("-10", -10)

                -- 文本输入框：直接输入秒数（构造失败时退回仅显示数值的纯步进模式）
                cl_time_box = nil
                local box = nil
                local ok_box = pcall(function()
                    box = GUI.TextBox(GUI.RectTransform(Vector2(0.13, 1), row.RectTransform), RawLString(tostring(cl_pending_time)))
                end)
                if not ok_box or box == nil then
                    ok_box = pcall(function()
                        box = GUI.CreateTextBoxWithPlaceholder(GUI.RectTransform(Vector2(0.13, 1), row.RectTransform), tostring(cl_pending_time), RawLString(""))
                    end)
                end
                if ok_box and box ~= nil then
                    cl_time_box = box
                    box.Enabled = can_edit
                else
                    add_text(Vector2(0.13, 1), row.RectTransform, tostring(cl_pending_time) .. "s", GUI.Alignment.Center)
                end

                step_btn("+10", 10)
                step_btn("+60", 60)

                local save_btn = GUI.Button(GUI.RectTransform(Vector2(0.14, 1), row.RectTransform), RawLString(T("cl_save")))
                save_btn.Enabled = can_edit
                save_btn.OnClicked = function()
                    local value = read_box_value()
                    if value == nil and cl_time_box ~= nil then
                        -- 输入框存在但内容不是数字：提示并放弃本次保存
                        pcall(function() GUI.AddMessage(T("cl_invalid_time"), Color(255, 120, 120, 255)) end)
                        print(T("log_prefix") .. T("cl_invalid_time"))
                        return true
                    end
                    cl_pending_time = math.floor(math.min(math.max(value or cl_pending_time or CL.lock_time, 10), 600))
                    CL.SendConfig(cl_pending_enabled == true, cl_pending_time, cl_pending_bots == true)
                    cl_dirty = false
                    return true
                end

                -- 重置默认：开启 + 120 秒 + 不锁 AI（立即生效并重建页面）
                local reset_btn = GUI.Button(GUI.RectTransform(Vector2(0.14, 1), row.RectTransform), RawLString(T("cl_reset")))
                reset_btn.Enabled = can_edit
                reset_btn.OnClicked = function()
                    cl_pending_enabled = true
                    cl_pending_time = 120
                    cl_pending_bots = false
                    CL.SendConfig(true, 120, false)
                    cl_dirty = false
                    cl_skip_request = true
                    pcall(open_menu)
                    print(T("log_prefix") .. T("cl_reset_done"))
                    return true
                end
            end

            if not can_edit then
                add_text(Vector2(1, ROW_H), layout.RectTransform, T("cl_no_permission"), GUI.Alignment.Center)
            end

            -- 已锁定玩家列表：ListBox 装全部条目，滚轮/拖动滚动条翻看（可视高度仍是 3 行，不影响布局）
            do
                local row = add_row(layout, ROW_H)
                local header = T("cl_locked_list")
                if #CL.locked > 0 then
                    header = header .. string.format(T("cl_total_suffix"), #CL.locked)
                end
                add_text(Vector2(0.76, 1), row.RectTransform, header, GUI.Alignment.CenterLeft)
                local refresh_btn = GUI.Button(GUI.RectTransform(Vector2(0.2, 1), row.RectTransform), RawLString(T("cl_refresh")))
                refresh_btn.OnClicked = function()
                    CL.RequestState()  -- 状态回包/单机同步回调会经 OnState 重建页面
                    return true
                end
            end

            if not CL.has_state then
                add_text(Vector2(1, ROW_H), layout.RectTransform, T("cl_loading"), GUI.Alignment.Center)
            elseif #CL.locked == 0 then
                add_text(Vector2(1, ROW_H), layout.RectTransform, T("cl_none"), GUI.Alignment.Center)
            else
                local list = GUI.ListBox(GUI.RectTransform(Vector2(1, ROW_H * 3), layout.RectTransform))
                cl_list_box = list
                for _, entry in ipairs(CL.locked) do
                    -- 每条占可视高度的 1/3（ListBox 里子项的相对高度以列表框高度为基准）
                    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 1 / 3), list.Content.RectTransform), true, GUI.Anchor.CenterLeft)
                    row.RelativeSpacing = 0.006
                    add_text(Vector2(0.74, 1), row.RectTransform,
                        tostring(entry.char_name) .. "（" .. tostring(entry.item_name) .. "）", GUI.Alignment.CenterLeft)
                    local un_btn = GUI.Button(GUI.RectTransform(Vector2(0.22, 1), row.RectTransform), RawLString(T("cl_unlock")))
                    un_btn.Enabled = can_edit
                    un_btn.OnClicked = function()
                        CL.SendUnlock(entry.char_id)
                        return true
                    end
                end
                -- 恢复重建前的滚动位置（条目变少时会被 ListBox 自动夹到合法范围）
                pcall(function() list.ScrollBar.BarScroll = cl_list_scroll end)
            end

            -- 全部解锁（二击确认，3 秒内再点一次生效）
            do
                local row = add_row(layout, ROW_H)
                local confirming = cl_unlockall_confirm ~= nil and os.clock() - cl_unlockall_confirm < 3
                local all_btn = GUI.Button(GUI.RectTransform(Vector2(1, 1), row.RectTransform),
                    RawLString(confirming and T("cl_unlock_all_confirm") or T("cl_unlock_all")))
                all_btn.Enabled = can_edit
                all_btn.OnClicked = function()
                    if confirming then
                        cl_unlockall_confirm = nil
                        CL.SendUnlock(0)
                    else
                        cl_unlockall_confirm = os.clock()
                        cl_skip_request = true
                        pcall(open_menu)
                    end
                    return true
                end
            end
        end

        do
            local bottom_row = add_row(layout, ROW_H)
            local back_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("back")))
            back_btn.OnClicked = function()
                menu_page = "hub"
                pcall(open_menu)
                return true
            end
            local close_button = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("close")))
            close_button.OnClicked = function()
                close_menu()
                return true
            end
        end
        return
    end

    if menu_page == "hudbars" then
        if hb_state == nil then hb_state = hb_load_state() end
        add_text(Vector2(1, HB_H), layout.RectTransform, T("window_title") .. " - " .. T("hudbar_settings"), GUI.Alignment.Center)

        -- 页面主体用 xpcall 包住：构建出错直接显示在页面上并写 page_error.txt
        local build_ok, build_err = xpcall(function()
        if hb_state == nil then
            add_text(Vector2(1, HB_H), layout.RectTransform, T("hb_loading"), GUI.Alignment.Center)
        else
            -- 左右排版：左列选择要调整的条，右列是全局设置 + 选中条的详细设置
            local columns = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.72), layout.RectTransform), true, GUI.Anchor.CenterLeft)
            columns.RelativeSpacing = 0.01

            -- 左列：条列表
            local left_col = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.32, 1), columns.RectTransform))
            left_col.Stretch = true
            local list = GUI.ListBox(GUI.RectTransform(Vector2(1, 1), left_col.RectTransform))
            if #hb_state.order == 0 then
                hb_add_text(Vector2(1, 0.1), list.Content.RectTransform, T("hb_empty"), GUI.Alignment.Center)
            end
            for _, idx in ipairs(hb_state.order) do
                local b = hb_state.bars[idx]
                local label = (idx == hb_selected and "» " or "") .. tostring(b.name)
                    .. (b.enabled == "1" and "" or "（隐藏）") .. (idx == hb_selected and " «" or "")
                local btn = GUI.Button(GUI.RectTransform(Vector2(1, 0.1), list.Content.RectTransform), RawLString(label))
                btn.OnClicked = function()
                    hb_selected = idx
                    pcall(open_menu)
                    return true
                end
            end

            -- 右列：全局设置 + 选中条详细设置（9 行 × 0.095 + 间距 ≈ 1.0）
            local right_col = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.67, 1), columns.RectTransform))
            right_col.Stretch = true
            right_col.RelativeSpacing = 0.015
            local RC_H = 0.095

            hb_slider_row(right_col, RC_H, T("hb_scale"), 0.5, 2, "%.2f",
                function() return hb_state.scale or 1 end,
                function(v) hb_state.scale = v end)
            hb_slider_row(right_col, RC_H, T("hb_height"), 90, 270, "%.0f",
                function() return hb_state.barheight or 180 end,
                function(v) hb_state.barheight = v end)
            hb_slider_row(right_col, RC_H, T("hb_offset_y"), -400, 400, "%.0f",
                function() return hb_state.offsety or 0 end,
                function(v) hb_state.offsety = v end)

            -- 显示位置（左/右）+ 边距，同一行
            do
                local row = add_row(right_col, RC_H)
                local side_btn = GUI.Button(GUI.RectTransform(Vector2(0.36, 1), row.RectTransform),
                    RawLString(hb_state.side == "left" and T("hb_side_left") or T("hb_side_right")))
                side_btn.OnClicked = function()
                    hb_state.side = (hb_state.side == "left") and "right" or "left"
                    hb_write_config()
                    pcall(open_menu)
                    return true
                end
                local scroll = GUI.ScrollBar(GUI.RectTransform(Vector2(0.4, 1), row.RectTransform), 0.1)
                scroll.Range = Vector2(0, 400)
                local margin_text = hb_add_text(Vector2(0.2, 1), row.RectTransform,
                    T("hb_margin") .. string.format("%.0f", hb_state.offsetx or 70), GUI.Alignment.CenterLeft)
                scroll.BarScroll = math.min(math.max((hb_state.offsetx or 70) / 400, 0), 1)
                scroll.OnMoved = function(bar, s)
                    hb_state.offsetx = 400 * s
                    margin_text.Text = RichString.Plain(RawLString(T("hb_margin") .. string.format("%.0f", hb_state.offsetx)))
                    hb_write_config()
                    return true
                end
            end

            -- 选中条：名称 + 启用开关 + 颜色预览（同一行）
            local sel = hb_selected ~= nil and hb_state.bars[hb_selected] or nil
            do
                local row = add_row(right_col, RC_H)
                hb_add_text(Vector2(0.4, 1), row.RectTransform,
                    sel ~= nil and tostring(sel.name) or T("hb_select_hint"), GUI.Alignment.CenterLeft)
                local tick = GUI.TickBox(GUI.RectTransform(Vector2(0.32, 1), row.RectTransform), RawLString(T("hb_enable")))
                tick.Selected = sel ~= nil and sel.enabled == "1"
                tick.Enabled = sel ~= nil
                tick.OnSelected = function(tb)
                    if sel == nil then return true end
                    sel.enabled = tb.Selected and "1" or "0"
                    hb_write_config()
                    pcall(open_menu)
                    return true
                end
                local preview = GUI.Frame(GUI.RectTransform(Vector2(0.24, 1), row.RectTransform), nil)
                if sel ~= nil then
                    local pr, pg, pb = hb_parse_color(sel.color)
                    preview.Color = Color(pr, pg, pb, 255)
                end
            end

            -- RGB 滑条
            local channels = { { T("hb_r"), 1 }, { T("hb_g"), 2 }, { T("hb_b"), 3 } }
            for _, ch in ipairs(channels) do
                hb_slider_row(right_col, RC_H, ch[1], 0, 255, "%.0f",
                    function()
                        if sel == nil then return 0 end
                        return select(ch[2], hb_parse_color(sel.color))
                    end,
                    function(v)
                        if sel == nil then return end
                        local r, g, b = hb_parse_color(sel.color)
                        local nv = math.floor(math.min(math.max(v, 0), 255) + 0.5)
                        if ch[2] == 1 then r = nv elseif ch[2] == 2 then g = nv else b = nv end
                        sel.color = r .. "," .. g .. "," .. b
                    end)
            end

            -- 重置（选中条 + 全局布局一起）
            do
                local row = add_row(right_col, RC_H)
                local reset_btn = GUI.Button(GUI.RectTransform(Vector2(1, 1), row.RectTransform), RawLString(T("hb_reset")))
                reset_btn.OnClicked = function()
                    hb_state.scale = 1
                    hb_state.side = "right"
                    hb_state.offsetx = 70
                    hb_state.offsety = 0
                    hb_state.barheight = 180
                    if sel ~= nil then
                        sel.color = sel.defaultcolor or sel.color
                        sel.enabled = sel.defaultenabled or "1"
                    end
                    hb_write_config()
                    pcall(open_menu)
                    return true
                end
            end
        end
        end, function(e) return tostring(e) end)
        if not build_ok then
            add_text(Vector2(1, HB_H), layout.RectTransform, "页面构建出错: " .. tostring(build_err), GUI.Alignment.Center)
            pcall(function() File.WriteAllText(hb_dir_file("page_error.txt"), tostring(build_err)) end)
        end
        -- 返回主菜单 / 关闭（与其他页同款）
        do
            local bottom_row = add_row(layout, HB_H)
            local back_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("back")))
            back_btn.OnClicked = function()
                menu_page = "hub"
                pcall(open_menu)
                return true
            end
            local close_button = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("close")))
            close_button.OnClicked = function()
                close_menu()
                return true
            end
        end
        return
    end

    if menu_page == "damage" then
        if dmg_state == nil then dmg_state = dmg_load_state() end
        add_text(Vector2(1, HB_H), layout.RectTransform, T("window_title") .. " - " .. T("damage_settings"), GUI.Alignment.Center)

        -- 页面主体用 xpcall 包住：构建出错直接显示在页面上并写 page_error.txt
        local build_ok, build_err = xpcall(function()
        if dmg_state == nil or #dmg_groups == 0 then
            add_text(Vector2(1, HB_H), layout.RectTransform,
                Game.IsMultiplayer and T("dmg_mp_loading") or T("dmg_loading"), GUI.Alignment.Center)
        else
            if not dmg_dirty then dmg_sync_pending() end
            if dmg_selected == nil or dmg_pending[dmg_selected] == nil then
                dmg_selected = dmg_groups[1].id
            end

            -- 状态行：应用计数/时间 + 联机提示（只读 / 拒绝原因 / 刚提交）
            do
                local status_text = string.format(T("dmg_status_fmt"),
                    tostring(dmg_state.patched["patched.items"] or "?"),
                    tostring(dmg_state.patched["patched.objects"] or "?"),
                    tostring(dmg_state.stamp or ""))
                if Game.IsMultiplayer then
                    if not dmg_can_edit() then
                        status_text = status_text .. " · " .. T("dmg_readonly_short")
                    elseif dmg_state.denied ~= nil and dmg_state.denied ~= "" then
                        status_text = status_text .. " · " .. T("dmg_denied_prefix") .. dmg_state.denied
                    elseif dmg_last_submit_at >= 0 and (os.clock and (os.clock() - dmg_last_submit_at) < 8) then
                        status_text = status_text .. " · " .. T("dmg_submitted_short")
                    end
                end
                add_text(Vector2(1, HB_H), layout.RectTransform, status_text, GUI.Alignment.Center)
            end

            -- 档位快选：一键写入全部组（LV1 略弱 / LV2 等补强 / LV3 补强×2），随后自动提交
            do
                local tier_row = add_row(layout, HB_H)
                local btn_lv1 = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), tier_row.RectTransform), RawLString(T("dmg_lv1")))
                btn_lv1.OnClicked = function()
                    dmg_apply_tier(1)
                    return true
                end
                local btn_lv2 = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), tier_row.RectTransform), RawLString(T("dmg_lv2")))
                btn_lv2.OnClicked = function()
                    dmg_apply_tier(2)
                    return true
                end
                local btn_lv3 = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), tier_row.RectTransform), RawLString(T("dmg_lv3")))
                btn_lv3.OnClicked = function()
                    dmg_apply_tier(3)
                    return true
                end
                -- 联机无权限时只读展示（真正的门槛在服务端，这里只是 UI 反馈）
                if not dmg_can_edit() then
                    btn_lv1.Enabled = false
                    btn_lv2.Enabled = false
                    btn_lv3.Enabled = false
                end
            end

            -- 左右排版：左列选分组，右列调选中分组的参数
            local columns = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.66), layout.RectTransform), true, GUI.Anchor.CenterLeft)
            columns.RelativeSpacing = 0.01

            local left_col = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.34, 1), columns.RectTransform))
            left_col.Stretch = true
            hb_add_text(Vector2(1, 0.1), left_col.RectTransform, T("dmg_groups_hint"), GUI.Alignment.CenterLeft)
            local list = GUI.ListBox(GUI.RectTransform(Vector2(1, 0.9), left_col.RectTransform))
            for _, g in ipairs(dmg_groups) do
                local label = (g.id == dmg_selected and "» " or "") .. tostring(g.label)
                    .. " · " .. string.format(T("dmg_items_fmt"), g.count)
                    .. (g.id == dmg_selected and " «" or "")
                local btn = GUI.Button(GUI.RectTransform(Vector2(1, 0.12), list.Content.RectTransform), RawLString(label))
                btn.OnClicked = function()
                    dmg_selected = g.id
                    pcall(open_menu)
                    return true
                end
            end

            local right_col = GUI.LayoutGroup(GUI.RectTransform(Vector2(0.64, 1), columns.RectTransform))
            right_col.Stretch = true
            right_col.RelativeSpacing = 0.02
            local RC_H = 0.13

            local sel = nil
            for _, g in ipairs(dmg_groups) do
                if g.id == dmg_selected then
                    sel = g
                    break
                end
            end
            if sel ~= nil then
                local p = dmg_pending_of(sel.id)
                if p == nil then
                    add_text(Vector2(1, RC_H), right_col.RectTransform, T("dmg_loading"), GUI.Alignment.Center)
                elseif sel.kind == "armor" then
                    dmg_slider_row(right_col, RC_H, T("dmg_defense"), 0, 3, "%.2f",
                        function() return p.defense or 1 end,
                        function(v) p.defense = v end)
                else
                    dmg_slider_row(right_col, RC_H, T("dmg_damage"), 0.1, 5, "%.2f",
                        function() return p.damage or 1 end,
                        function(v) p.damage = v end)

                    -- 穿甲模式（加算/乘算 二选一生效）；切换时落到该模式的中性值（0 / ×1），不偷偷改数值
                    do
                        local row = add_row(right_col, RC_H)
                        local mode_btn = GUI.Button(GUI.RectTransform(Vector2(1, 1), row.RectTransform),
                            RawLString(p.penmode == "multiply" and T("dmg_pen_mode_mul") or T("dmg_pen_mode_add")))
                        mode_btn.OnClicked = function()
                            if p.penmode == "multiply" then
                                p.penmode = "add"
                                p.penvalue = 0
                            else
                                p.penmode = "multiply"
                                p.penvalue = 1
                            end
                            dmg_dirty = true
                            pcall(open_menu)
                            return true
                        end
                        if not dmg_can_edit() then mode_btn.Enabled = false end
                    end
                    local pen_mult = (p.penmode == "multiply")
                    dmg_slider_row(right_col, RC_H, T("dmg_pen_value"),
                        pen_mult and 0.1 or -0.5, pen_mult and 3.0 or 0.5,
                        pen_mult and "%.2f" or "%.3f",
                        function() return p.penvalue or 0 end,
                        function(v) p.penvalue = v end)
                end

                -- 保存 / 重置（都只是把玩家值写进请求文件，应用/同步由 C# 桥接完成）
                do
                    local row = add_row(right_col, RC_H)
                    local save_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("dmg_save")))
                    save_btn.OnClicked = function()
                        if dmg_write_config() then
                            dmg_dirty = false
                            local msg = Game.IsSingleplayer and T("dmg_saved") or T("dmg_submitted")
                            pcall(function() GUI.AddMessage(msg, Color(150, 255, 150, 255)) end)
                            print(T("log_prefix") .. msg)
                        else
                            pcall(function() GUI.AddMessage(T("dmg_noedit_toast"), Color(255, 140, 120, 255)) end)
                        end
                        return true
                    end
                    local reset_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("dmg_reset")))
                    reset_btn.OnClicked = function()
                        for _, g in ipairs(dmg_groups) do
                            local pp = dmg_pending_of(g.id)
                            if pp ~= nil then
                                pp.damage = dmg_num(g.defaultdamage)
                                pp.penmode = g.defaultpenmode or "add"
                                pp.penvalue = dmg_num(g.defaultpenvalue)
                                pp.defense = dmg_num(g.defaultdefense)
                            end
                        end
                        dmg_dirty = false
                        if dmg_write_config() then
                            local msg = Game.IsSingleplayer and T("dmg_reset_done") or T("dmg_submitted")
                            pcall(function() GUI.AddMessage(msg, Color(150, 255, 150, 255)) end)
                            print(T("log_prefix") .. msg)
                        else
                            pcall(function() GUI.AddMessage(T("dmg_noedit_toast"), Color(255, 140, 120, 255)) end)
                        end
                        return true
                    end
                    if not dmg_can_edit() then
                        save_btn.Enabled = false
                        reset_btn.Enabled = false
                    end
                end

                add_text(Vector2(1, HB_H), right_col.RectTransform, T("dmg_hint"), GUI.Alignment.CenterLeft)
            end
        end
        end, function(e) return tostring(e) end)
        if not build_ok then
            add_text(Vector2(1, HB_H), layout.RectTransform, "页面构建出错: " .. tostring(build_err), GUI.Alignment.Center)
            pcall(function() File.WriteAllText(hb_dir_file("page_error.txt"), tostring(build_err)) end)
        end

        -- 返回主菜单 / 关闭（与其他页同款）
        do
            local bottom_row = add_row(layout, HB_H)
            local back_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("back")))
            back_btn.OnClicked = function()
                menu_page = "hub"
                pcall(open_menu)
                return true
            end
            local close_button = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("close")))
            close_button.OnClicked = function()
                close_menu()
                return true
            end
        end
        return
    end

    if menu_page == "bondsettings" or menu_page == "condloss" then
        if bond_state == nil then bond_state = bond_load_state() end
        local is_bond = menu_page == "bondsettings"
        add_text(Vector2(1, ROW_H), layout.RectTransform,
            T("window_title") .. " - " .. T(is_bond and "bond_settings" or "condloss_settings"), GUI.Alignment.Center)

        -- 页面主体用 xpcall 包住：构建出错直接显示在页面上并写 page_error.txt，不再被静默吞掉
        local build_ok, build_err = xpcall(function()
            if bond_state == nil then
                add_text(Vector2(1, ROW_H), layout.RectTransform, T("bs_module_missing"), GUI.Alignment.Center)
            else
                -- 暂存值每次重建页面时从状态快照初始化；输入框聚焦期间不重建（见 think 轮询）
                local pending = {
                    settleinterval = bond_state.settleinterval,
                    resistancescale = bond_state.resistancescale,
                    maxlinkdistance = bond_state.maxlinkdistance,
                    countedtypes = bond_state.countedtypes,
                    condlossmult = bond_state.condlossmult,
                }
                bs_boxes = {}

                if is_bond then
                    bs_textbox_row(layout, T("bs_interval"), "settleinterval", pending)
                    bs_textbox_row(layout, T("bs_resist"), "resistancescale", pending)
                    bs_textbox_row(layout, T("bs_distance"), "maxlinkdistance", pending)
                    bs_textbox_row(layout, T("bs_types"), "countedtypes", pending)
                    add_text(Vector2(1, ROW_H), layout.RectTransform, T("bs_hint"), GUI.Alignment.Center)
                else
                    bs_textbox_row(layout, T("cd_mult"), "condlossmult", pending)
                    add_text(Vector2(1, ROW_H), layout.RectTransform, T("cd_hint"), GUI.Alignment.Center)
                end

                -- 拒绝反馈（C# 侧已做 10 秒新鲜度判断，非空即新鲜）
                local denied = bond_state.denied or ""
                if denied ~= "" then
                    add_text(Vector2(1, ROW_H), layout.RectTransform, T("bs_denied_prefix") .. denied, GUI.Alignment.Center)
                end

                -- 保存 / 重置
                do
                    local row = add_row(layout, ROW_H)
                    local save_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("cl_save")))
                    save_btn.OnClicked = function()
                        local entries = {}
                        local keys = is_bond
                            and { "settleinterval", "resistancescale", "maxlinkdistance", "countedtypes" }
                            or { "condlossmult" }
                        for _, key in ipairs(keys) do
                            local box = bs_boxes[key]
                            if box ~= nil then
                                local ok, text = pcall(function() return tostring(box.Text) end)
                                if ok and text ~= nil and text ~= "" then
                                    entries[#entries + 1] = key .. "=" .. text
                                end
                            end
                        end
                        bond_write_request(entries)
                        return true
                    end
                    local reset_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), row.RectTransform), RawLString(T("cl_reset")))
                    reset_btn.OnClicked = function()
                        local entries = is_bond
                            and { "settleinterval=1", "resistancescale=0.5", "maxlinkdistance=0", "countedtypes=damage,burn,bleeding,debuff,poison" }
                            or { "condlossmult=1" }
                        bond_write_request(entries)
                        return true
                    end
                end
            end
        end, function(e) return tostring(e) end)
        if not build_ok then
            add_text(Vector2(1, ROW_H), layout.RectTransform, "页面构建出错: " .. tostring(build_err), GUI.Alignment.Center)
            pcall(function() File.WriteAllText(hb_dir_file("page_error.txt"), tostring(build_err)) end)
        end

        -- 返回主菜单 / 关闭（与其他页同款）
        do
            local bottom_row = add_row(layout, ROW_H)
            local back_btn = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("back")))
            back_btn.OnClicked = function()
                menu_page = "hub"
                pcall(open_menu)
                return true
            end
            local close_button = GUI.Button(GUI.RectTransform(Vector2(0.5, 1), bottom_row.RectTransform), RawLString(T("close")))
            close_button.OnClicked = function()
                close_menu()
                return true
            end
        end
        return
    end

    add_text(Vector2(1, ROW_H), layout.RectTransform, T("window_title") .. " - " .. T("menu_hotkey"), GUI.Alignment.Center)

    do
        local row = add_row(layout, ROW_H)
        add_text(Vector2(0.22, 1), row.RectTransform, T("menukey_name"), GUI.Alignment.CenterLeft)
        menukey_button = GUI.Button(GUI.RectTransform(Vector2(0.16, 1), row.RectTransform), RawLString(config.menukey))
        menukey_button.OnClicked = function()
            start_capture("menukey")
            return true
        end
        add_text(Vector2(0.60, 1), row.RectTransform, T("menukey_hint"), GUI.Alignment.CenterLeft)
    end

    local pages = get_pages()
    local total_pages = #pages
    if current_page > total_pages then current_page = total_pages end
    if current_page < 1 then current_page = 1 end
    local page = pages[current_page]
    local combined = get_combined()

    add_text(Vector2(1, ROW_H), layout.RectTransform, page.label, GUI.Alignment.Center)

    for _, i in ipairs(page.items) do
        local b = combined[i]
        local row = add_row(layout, ROW_H)

        -- 名称列：槽位组绑定行（装束技能/外套按钮/背包按钮）同时显示当前检测到的控件名称
        local name_label = add_text(Vector2(0.17, 1), row.RectTransform, T(b.name), GUI.Alignment.CenterLeft)
        local group_name = string.match(b.target, "^(%a+):")
        if group_name ~= nil and SLOT_GROUPS[group_name] ~= nil then
            detect_labels[i] = name_label
        end

        binding_buttons[i] = GUI.Button(GUI.RectTransform(Vector2(0.13, 1), row.RectTransform), RawLString(binding_display(b)))
        binding_buttons[i].OnClicked = function()
            start_capture(i)
            return true
        end

        local clear_button = GUI.Button(GUI.RectTransform(Vector2(0.04, 1), row.RectTransform), RawLString("×"))
        clear_button.OnClicked = function()
            b.key = ""
            b.modifiers = {}
            save_config()
            refresh_binding_texts()
            return true
        end

        -- 双击触发勾选（双击该键才触发；可与单击绑定共用一键，双击优先占用）
        local dbl_tick = GUI.TickBox(GUI.RectTransform(Vector2(0.06, 1), row.RectTransform), RawLString(T("double_press")))
        dbl_tick.Selected = b.double == true
        dbl_tick.OnSelected = function(tb)
            b.double = tb.Selected
            save_config()
            return true
        end

        -- 修饰键勾选（每条绑定独立；宽度收紧，6 个复选框 + 双击框不再溢出）
        for _, name in ipairs(MODIFIER_NAMES) do
            local tick = GUI.TickBox(GUI.RectTransform(Vector2(0.085, 1), row.RectTransform), RawLString(MODIFIER_DISPLAY[name]))
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

    do
        local bottom_row = add_row(layout, ROW_H)
        local back_btn = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), bottom_row.RectTransform), RawLString(T("back")))
        back_btn.OnClicked = function()
            menu_page = "hub"
            pcall(open_menu)
            return true
        end
        local reset_btn = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), bottom_row.RectTransform), RawLString(T("reset_default")))
        reset_btn.OnClicked = function()
            -- 内置绑定回出厂值（界面开关键 K、绑定面板 L，其余未绑定）；外部绑定回其注册默认键
            local defs = default_bindings()
            for i, b in ipairs(config.bindings) do
                b.key = defs[i] ~= nil and defs[i].key or ""
                b.modifiers = {}
                b.double = false
            end
            for _, id in ipairs(extra_order) do
                local b = extra_bindings[id]
                b.key = b.reg_default_key or ""
                b.modifiers = {}
                b.double = false
            end
            config.menukey = "K"
            save_config()
            refresh_binding_texts()
            refresh_detected_labels()
            print(T("log_prefix") .. T("reset_done"))
            return true
        end
        local close_button = GUI.Button(GUI.RectTransform(Vector2(0.33, 1), bottom_row.RectTransform), RawLString(T("close")))
        close_button.OnClicked = function()
            close_menu()
            return true
        end
    end

    refresh_binding_texts()
    refresh_detected_labels()
    if DEBUG_LOG then diagnose_outfit_scan(Character.Controlled) end
end

-- 打开设置窗口（出错打控制台）。外部入口打开时总是回主菜单页
local function safe_open_menu()
    menu_page = "hub"
    local ok, err = pcall(open_menu)
    if not ok then
        print(T("log_prefix") .. string.format(T("open_menu_fail"), tostring(err)))
    end
end

-- 装束锁定状态回调：只在设置窗口停在装束锁定页时重建页面；cl_skip_request 防"请求→重建→再请求"循环，
-- cl_dirty 时保留没存完的编辑
if TLE ~= nil and TLE.CostumeLockClient ~= nil then
    TLE.CostumeLockClient.OnState = function()
        if menu_frame == nil or menu_page ~= "costumelock" then return end
        cl_unlockall_confirm = nil
        cl_skip_request = true
        pcall(open_menu)
    end
end

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

-- 注册一个新快捷键绑定（给别的模组用）。def = { id=全局唯一, name=显示名, mod=分页名, default_key=默认键, on_trigger=function(character, binding) }
-- 返回 true/false（失败原因打控制台）
local function register_binding(def)
    if type(def) ~= "table" or type(def.id) ~= "string" or def.id == "" then
        print(T("log_prefix") .. "RegisterBinding: 无效 id（需要非空字符串）")
        return false
    end
    for _, b in ipairs(config.bindings) do
        if b.target == def.id then
            print(T("log_prefix") .. "RegisterBinding: id 与内置绑定冲突：" .. def.id)
            return false
        end
    end
    if extra_bindings[def.id] ~= nil then
        print(T("log_prefix") .. "RegisterBinding: id 重复注册：" .. def.id)
        return false
    end
    if type(def.on_trigger) ~= "function" then
        print(T("log_prefix") .. "RegisterBinding: " .. def.id .. " 缺少 on_trigger 回调")
        return false
    end
    local b = {
        id = def.id,
        name = def.name or def.id,
        mod = def.mod or T("page_external"),
        key = (def.default_key ~= nil and valid_key(def.default_key)) and def.default_key or "",
        reg_default_key = (def.default_key ~= nil and valid_key(def.default_key)) and def.default_key or "",
        modifiers = {},
        double = false,
        on_trigger = def.on_trigger,
        target = "__external__",  -- 标记：派发时不走内置 target 逻辑
    }
    extra_bindings[def.id] = b
    extra_order[#extra_order + 1] = def.id
    combined_cache = nil
    sorted_binding_order = nil
    double_bound_keys_cache = nil  -- 同点失效（双击绑定键集合也是纯配置派生）
    print(T("log_prefix") .. "已注册快捷键绑定：" .. def.id .. "（" .. b.mod .. " / " .. b.name .. "）")
    return true
end

TouhouHotkey = {
    RegisterBinding = function(def)
        if register_binding(def) then
            save_config()  -- 立即持久化默认键
            return true
        end
        return false
    end,
    --- 查询某 id 当前绑定的键位（"" = 未绑定；nil = 未注册）
    GetBoundKey = function(id)
        local b = extra_bindings[id]
        return b ~= nil and b.key or nil
    end,
}

-- 兼容早期注册（加载顺序无关）：别的模组先跑就把注册请求塞进 TouhouHotkeyPending，这里统一排空
if type(TouhouHotkeyPending) == "table" then
    for _, def in ipairs(TouhouHotkeyPending) do register_binding(def) end
    TouhouHotkeyPending = nil
end

load_config()
sanitize_config()
desc_last_eff = config.desc_detailed  -- 与配置文件对齐，避免启动后误判模式变化

local function get_sorted_binding_order()
    if sorted_binding_order == nil then
        local combined = get_combined()
        local order = {}
        for i in ipairs(combined) do
            order[#order + 1] = i
        end
        table.sort(order, function(a, c)
            return #combined[a].modifiers > #combined[c].modifiers
        end)
        sorted_binding_order = order
    end
    return sorted_binding_order
end

-- 有双击绑定的键集合（单击绑定要让位给同键的双击绑定）。
-- 纯配置派生、跟按键状态无关，所以跟 sorted_binding_order 一样缓存、一样失效
local function get_double_bound_keys()
    if double_bound_keys_cache == nil then
        local t = {}
        for _, b in ipairs(get_combined()) do
            if b.double and b.key ~= "" then t[b.key] = true end
        end
        double_bound_keys_cache = t
    end
    return double_bound_keys_cache
end

local function apply_captured_key(name)
    local conflict, who
    if capturing == "menukey" then
        -- 界面开关键必须唯一：不能与任何绑定重复
        conflict, who = menukey_conflicts_with_bindings(name)
        if not conflict then config.menukey = name end
    else
        -- 普通绑定之间允许共用按键，只需避开界面开关键
        local cb = get_combined()[capturing]
        conflict = cb ~= nil and conflicts_with_menukey(name, cb.modifiers)
        who = "menukey_name"
        if not conflict and cb ~= nil then cb.key = name end
    end
    if conflict then
        print(T("log_prefix") .. string.format(T("bind_conflict"), T(tostring(who))))
    else
        save_config()
    end
    stop_capture()
end

Hook.Add("think", "touhou_hotkey_settings", function()
    -- 清掉上一帧未被消费的轻点标记（被输入守卫拦截的轻点直接丢弃，不延迟触发）
    desc_tapped = false
    -- 复用模块级命中表：开头清空即可，跟每帧新建表等价，省掉每帧的分配
    for k in pairs(key_hits) do key_hits[k] = nil end
    for k in pairs(double_hits) do double_hits[k] = nil end

    -- C# 设置窗口「返回」标记轮询：open_mod_settings=1 → 重新打开主菜单（0.5s 节流）
    flag_poll_counter = flag_poll_counter + 1
    if flag_poll_counter >= 30 then
        flag_poll_counter = 0
        pcall(function()
            if File == nil then return end
            local path = get_config_path()
            if not File.Exists(path) then return end
            local text = File.ReadAllText(path)
            if text == nil then return end
            if not string.find(text, "open_mod_settings=1", 1, true) then return end
            local kept = {}
            for line in string.gmatch(text, "[^\r\n]+") do
                if line ~= "open_mod_settings=1" then kept[#kept + 1] = line end
            end
            File.WriteAllText(path, table.concat(kept, "\n"))
            if menu_frame == nil then safe_open_menu() end
        end)
    end

    -- 设置窗口得像暂停菜单一样每帧重新加入 GUI 更新列表才画得出来，所以必须放在
    -- 所有 return 分支之前（包括捕获按键期间）；order=1 盖在暂停菜单上面
    if menu_frame ~= nil then
        menu_frame.AddToGUIUpdateList(false, 1)

        -- 进度条设置页：0.5 秒轮询状态快照，C# 侧重载（其他模组配置热更新/外部改配置）时重建页面；
        -- 本页自己的写入经 C# 回写后内容相同，文本比对一致就不重建，无回环；
        -- 鼠标按住（拖动滑条）期间延迟重建到松开，否则拖到一半滑条被销毁
        if menu_page == "hudbars" then
            hb_poll_counter = hb_poll_counter + 1
            if hb_poll_counter >= 30 then
                hb_poll_counter = 0
                pcall(function()
                    if File == nil then return end
                    local text = File.ReadAllText(hb_dir_file("TouhouHudBarsState.txt"))
                    if text ~= nil and text ~= hb_state_raw then
                        hb_state_raw = text
                        hb_state = hb_parse_state(text)
                        if hb_mouse_held() then
                            hb_pending_rebuild = true
                        else
                            pcall(open_menu)
                        end
                    elseif hb_pending_rebuild and not hb_mouse_held() then
                        hb_pending_rebuild = false
                        pcall(open_menu)
                    end
                end)
            end
        end

        -- 伤害/防具设置页：0.5 秒轮询状态快照（单机/主机由权威端 C# 写；联机由客户端 C# 把服务器
        -- 下发的状态写进本地文件，传输不经过本脚本）。有未保存编辑时不重建（保住暂存值）。
        if menu_page == "damage" then
            dmg_poll_counter = dmg_poll_counter + 1
            if dmg_poll_counter >= 30 then
                dmg_poll_counter = 0
                pcall(function()
                    if File == nil then return end
                    local path = hb_dir_file("TouhouDamageState.txt")
                    if not File.Exists(path) then return end
                    local text = File.ReadAllText(path)
                    if text ~= nil and text ~= dmg_state_raw then
                        dmg_set_state_from_text(text)
                        -- 服务器拒绝：弹窗提示一次（状态行那点小字太容易被忽略）
                        local denied = dmg_state.denied
                        if denied ~= nil and denied ~= "" and denied ~= dmg_last_denied_shown then
                            dmg_last_denied_shown = denied
                            pcall(function() GUI.AddMessage(T("dmg_denied_prefix") .. denied, Color(255, 140, 120, 255)) end)
                            print(T("log_prefix") .. "服务器拒绝了本次修改：" .. denied)
                        elseif denied == nil or denied == "" then
                            dmg_last_denied_shown = nil
                        end
                        if not dmg_dirty and not hb_mouse_held() then
                            pcall(open_menu)
                        end
                    end
                end)
            end
        end

        -- 绑定/耐久设置页：0.5 秒轮询状态快照；输入框聚焦期间不重建（防打断输入）
        if menu_page == "bondsettings" or menu_page == "condloss" then
            bond_poll_counter = bond_poll_counter + 1
            if bond_poll_counter >= 30 then
                bond_poll_counter = 0
                pcall(function()
                    if File == nil then return end
                    local path = hb_dir_file("TouhouBondState.txt")
                    if not File.Exists(path) then return end
                    local text = File.ReadAllText(path)
                    if text ~= nil and text ~= bond_state_raw then
                        bond_state_raw = text
                        bond_state = bond_parse_state(text)
                        local focused = false
                        pcall(function()
                            focused = GUI.KeyboardDispatcher ~= nil and GUI.KeyboardDispatcher.Subscriber ~= nil
                        end)
                        if not focused then pcall(open_menu) end
                    end
                end)
            end
        end

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
    if pause_toggle_probe == nil then
        pause_toggle_probe = pcall(function() GUI.PreventPauseMenuToggle = (menu_frame ~= nil) end)
    elseif pause_toggle_probe then
        GUI.PreventPauseMenuToggle = (menu_frame ~= nil)
    end

    -- 按键捕获模式：优先级最高
    if capturing ~= nil then
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
            elseif (valid_key(name) and not is_modifier_name(name))
                -- 特例：描述详略切换允许直接绑修饰键（玩家想按住 Shift 看短描述）；
                -- 其他绑定仍不允许，避免与修饰键勾选语义混淆
                or (is_modifier_name(name) and capturing ~= "menukey"
                    and get_combined()[capturing].target == "desc_toggle") then
                apply_captured_key(name)
                return
            end
        end
        -- 键盘没有新输入时检查鼠标键（侧键/中键等；快速点按请按住约半秒再松开，避免帧间漏采）
        for _, name in ipairs(MOUSE_BUTTONS) do
            if not capture_ignored[name] and raw_mouse_down(name) then
                apply_captured_key(name)
                return
            end
        end
        return
    end

    -- 每帧轮询原始状态并自己维护“上一帧”记录（必须在所有 return 分支之前）。
    -- 注意是按唯一按键轮询而不是按绑定轮询——共用一键的多条绑定里，
    -- 逐绑定轮询会让第一条把边沿消费掉，后面的永远不响
    local menukey_hit = poll_key_hit(config.menukey)
    local esc_hit = poll_key_hit("Escape")
    for _, b in ipairs(get_combined()) do
        if b.key ~= "" and key_hits[b.key] == nil then
            key_hits[b.key] = poll_key_hit(b.key)
        end
    end

    -- 描述详略切换（不走边沿分发，每帧直接跟踪按住状态）：
    -- 按住 ≥0.3 秒 = 临时简短版（松开恢复锁定模式）；更短的轻点 = 切换锁定模式（守卫通过后消费）
    local desc_held = false
    for _, b in ipairs(config.bindings) do
        if b.target == "desc_toggle" and b.key ~= ""
            and key_was_down[b.key] == true and modifiers_satisfied(b.modifiers) then
            desc_held = true
            break
        end
    end
    if desc_held then
        if desc_press_time == nil then desc_press_time = os.clock() end
    elseif desc_press_time ~= nil then
        if os.clock() - desc_press_time < DESC_HOLD_SECONDS then desc_tapped = true end
        desc_press_time = nil
    end
    -- 实际模式变化时立即写配置文件（C# 端 0.3 秒轮询生效）
    local desc_eff = config.desc_detailed
        and not (desc_press_time ~= nil and os.clock() - desc_press_time >= DESC_HOLD_SECONDS)
    if desc_eff ~= desc_last_eff then
        desc_last_eff = desc_eff
        save_config()
    end

    -- 暂停菜单出现时注入「东方-快捷键设置」按钮（即便暂停菜单打开时也要执行）
    update_pause_menu_button()

    if GUI.KeyboardDispatcher.Subscriber then return end  -- 聊天框/输入框激活时不触发

    -- 无游戏会话（潜艇编辑器）：设置窗口照常可用，仅跳过触发派发
    local no_session = Game.GameSession == nil

    -- ESC 优先处理设置窗口：子页返回主菜单，主菜单才关闭
    if esc_hit and menu_frame ~= nil then
        if menu_page ~= "hub" then
            menu_page = "hub"
            pcall(safe_open_menu)
        else
            close_menu()
        end
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
    if Game.Paused or no_session then return end  -- 暂停/编辑器无会话时不派发

    -- 控制台、Tab 菜单、战役界面、社交覆盖层等“阻挡输入”的界面打开时不触发技能
    -- （调试控制台打开时即视为正在输入文字；该属性也包含暂停菜单，但不会包含本脚本自己的设置窗口）
    -- 属性存在性只探一次（跟上面的 pause_toggle_probe 一个写法）；没有这个属性的环境直接跳过这段判断
    if input_blocking_probe == nil then
        input_blocking_probe = pcall(function() return GUI.InputBlockingMenuOpen end)
    end
    if input_blocking_probe and GUI.InputBlockingMenuOpen then return end

    -- 轻点「描述详略切换」= 切换锁定模式（详细/简短）
    if desc_tapped then
        desc_tapped = false
        config.desc_detailed = not config.desc_detailed
        save_config()
        print(T("log_prefix") .. (config.desc_detailed and T("desc_detailed_on") or T("desc_detailed_off")))
    end

    -- 绑定分发：修饰键多的绑定优先匹配（例如 Shift+J 优先于 J）。
    -- 签名完全相同（按键+修饰键都一样）的绑定允许共存，按下时全部触发（一键多用）；
    -- 不同签名只触发修饰键最多的那一组，避免 Shift+J 把 J 的绑定也带出来
    local character = Character.Controlled
    if character == nil then return end

    local fired_sig = nil
    -- 双击检测：同一键 0.35 秒内两次边沿 = 双击（三连击不重复触发）
    local double_bound_keys = get_double_bound_keys()
    local now = os.clock()
    for key, hit in pairs(key_hits) do
        if hit then
            if double_last_press[key] ~= nil and now - double_last_press[key] <= 0.35 then
                double_hits[key] = true
                double_last_press[key] = nil
            else
                double_last_press[key] = now
            end
        end
    end

    for _, i in ipairs(get_sorted_binding_order()) do
        local b = get_combined()[i]
        -- desc_toggle 由按住/轻点逻辑单独处理，不参与边沿分发
        local hit = b.target ~= "desc_toggle" and (b.double and double_hits[b.key]
            or (not b.double and key_hits[b.key] and not double_bound_keys[b.key]))
        if hit and modifiers_satisfied(b.modifiers) then
            if b.on_trigger ~= nil then
                -- 外部注册回调（pcall 隔离：模组异常不影响游戏与其他绑定）
                local ok, err = pcall(b.on_trigger, character, b)
                if not ok then
                    print(T("log_prefix") .. "on_trigger 出错（" .. tostring(b.id) .. "）：" .. tostring(err))
                end
            else
                local sig = binding_signature(b.key, b.modifiers)
                if fired_sig == nil then fired_sig = sig end
                if sig == fired_sig then
                    execute_binding(b, character)
                end
            end
        end
    end
end)
