--[[东方-装束包：可选物品清单（客户端/服务端共用）
    装束包系统的"收录表"：哪个 identifier 属于哪种包，全部在这里配置。
    客户端按表生成选择窗口，服务端按表做权威校验，两边永远一致。
    新增装束时：
      1. 把 identifier 加进对应分类（basic=装束包 / plus=装束包+ / headwear=头饰包）
      2. 不用改任何其他文件
    已排除（故意不收）：Pollution_*（被玷污的法袍系列，敌人装束）、
    Touhou_Prometheus_Clothes（普罗米修斯）、Touhou_Blood_Crown（独一王冠，敌人掉落）、
    Renko_Veil（槽位特殊）。要回收就把它们加回表。]]

TLE_COSTUME_PACK_ITEMS = {
    basic = {
        "Akyuu_gear",
        "Alice_gear",
        "Alice_gear_Wedding",
        "Aunn_gear",
        "ayagear",
        "Cirno_Eclipse_Valentines",
        "Cirno_gear",
        "Flandre_Eclipse_Anniversary",
        "Flandre_gear",
        "Hong_Meirin_gear",
        "Jyoon_gear",
        "Kagiyama_Hina_gear",
        "Kaguya_gear",
        "Koakuma_gear",
        "Koishi_Eclipse_Valentines",
        "Koishi_Sponsor",
        "Koishi_gear",
        "Kokoro_gear",
        "Lilywhite_gear",
        "Lilywhite_gear_Black",
        "Luna_gear",
        "Marisa_Eclipse_Anniversary",
        "marisagear",
        "Merry_gear",
        "Mokou_gear",
        "Momizi_gear",
        "Nitori_gear",
        "Normal_Magician_Clothes01",
        "Normal_Magician_Clothes02",
        "Normal_Magician_Clothes03",
        "Normal_Yokai_Clothes01",
        "Normal_Yokai_Clothes02",
        "Normal_Yokai_Clothes03",
        "Normal_Yousei_Clothes01",
        "Normal_Yousei_Clothes02",
        "Normal_Yousei_Clothes03",
        "Nue_Eclipse_gear",
        "Nue_gear",
        "Patchouli_gear",
        "Piece_gear",
        "Reimu_Eclipse_Anniversary",
        "Reimu_Eclipse_Valentines",
        "Reimu_gear",
        "Reisen_Outfit",
        "Reisen_gear",
        "Remilia_Eclipse_Anniversary",
        "remiliagear",
        "Renko_gear",
        "Sakuya_gear",
        "Sanae_gear",
        "Satori_gear",
        "Shinki_gear",
        "Shion_gear",
        "Star_gear",
        "Sunny_gear",
        "Tenshi_Eclipse_Anniversary",
        "Tenshi_gear",
        "Tewi_gear",
        "youmugear",
        "Youmu_gear_Christmas",
        "Youmu_Skirt",
        "Yuka_gear",
        "Yukari_gear",
        "Yuyuko_gear",
    },

    plus = {
        "Akyuu_Plus",
        "Alice_Plus",
        "Alice_Plus_Wedding",
        "Aunn_Plus",
        "Aya_Plus",
        "Cirno_Eclipse_Valentines_Plus",
        "Cirno_Plus",
        "Cosmic_Princess_Kaguya_Plus",
        "Ema_Plus",
        "Flandre_Eclipse_Anniversary_Plus",
        "Flandre_Plus",
        "Hiro_Plus",
        "Hong_Meirin_Plus",
        "Iroha_Plus",
        "Jyoon_Plus",
        "Kagiyama_Hina_Plus",
        "Kaguya_Plus",
        "Koakuma_Plus",
        "Koishi_Eclipse_Valentines_Plus",
        "Koishi_Plus",
        "Koishi_Sponsor_Plus",
        "Kokoro_Plus",
        "Lilywhite_Plus",
        "Lilywhite_Plus_Black",
        "Luna_Plus",
        "Marisa_Eclipse_Anniversary_Plus",
        "Marisa_Plus",
        "Merry_Plus",
        "Mokou_Plus",
        "Momizi_Plus",
        "Nitori_Plus",
        "Nue_Eclipse_Plus",
        "Nue_Plus",
        "Patchouli_Plus",
        "Piece_Plus",
        "Reimu_Eclipse_Anniversary_Plus",
        "Reimu_Eclipse_Valentines_Plus",
        "Reimu_Plus",
        "Reisen_Outfit_Plus",
        "Reisen_Plus",
        "Remilia_Eclipse_Anniversary_Plus",
        "Remilia_Plus",
        "Renko_Plus",
        "Sakuya_Plus",
        "Sanae_Plus",
        "Satori_Plus",
        "Sherry_Plus",
        "Shinki_Plus",
        "Shion_Plus",
        "Star_Plus",
        "Sunny_Plus",
        "Tenshi_Eclipse_Anniversary_Plus",
        "Tenshi_Plus",
        "Tewi_Plus",
        "Yachiyo_Plus",
        "Youmu_Plus",
        "Youmu_Plus_Christmas",
        "Youmu_Skirt_Plus",
        "Yuka_Plus",
        "Yukari_Plus",
        "Yuyuko_Plus",
    },

    headwear = {
        "Alice_Headpiece",
        "Camellia_Hair_Accessory",
        "Cirno_Eclipse_Valentines_Headpiece",
        "Hong_Meirin_Hat",
        "Jyoon_Hat",
        "Koishi_Eclipse_Valentines_Hat",
        "Koishi_Hat",
        "Koishi_Sponsor_Hat",
        "Kokoro_Mask01",
        "Kokoro_Mask02",
        "Lilywhite_Headband",
        "Marisa_Hat_Eclipse_Anniversary",
        "marisahat",
        "Nitori_Hat",
        "Renko_Hat",
        "Sunnu_Scary_Mask",
        "Tenshi_Eclipse_Anniversary_Headpiece",
        "Tenshi_Hat",
        "Touhou_Hakurei_Fox_Headpiece",
        "Touhou_Incredible_Headpiece",
        "Touhou_Lucky_Four_Leaf_Clover_Headpiece",
        "Touhou_Sanae_Frog_Hairband",
        "Touhou_Sunflower_Headpiece",
        "Touhou_Youmu_Christmas_Bow",
        "Yuyuko_Hat",
    },
}

-- identifier -> 分类名（"basic"/"plus"/"headwear"/nil），服务端校验和客户端建表都用这个反查
local CATEGORY_OF = {}
for category, ids in pairs(TLE_COSTUME_PACK_ITEMS) do
    for _, id in ipairs(ids) do
        CATEGORY_OF[id] = category
    end
end
TLE_COSTUME_PACK_ITEMS.category_of = function(identifier)
    return CATEGORY_OF[identifier]
end

----------------------------------------------------------------------
-- 扩展收录：Config/CostumePack_Extra.xml
-- <Item category="..." identifier="..." />，category：basic=装束包 / plus=装束包+ / headwear=头饰包
-- 重复的 id 自动去重；分类名写错会被跳过并打日志。
-- 收录范围：**所有已启用内容包各自的 Config/CostumePack_Extra.xml**（含本模组），按加载顺序合并。
-- 别的模组把自己那份放在自己模组的 Config 里即可，不用塞进本模组；本模组那份排最前，优先级最高。
-- 与 CSharp/Shared/HomingProjectiles.cs 扫 Config/homing_config.xml 是同一套做法。
-- 用文本扫描而不是 XElement 解析：配置结构很简单，这样跨 LuaCs 版本最稳。
----------------------------------------------------------------------
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

-- 文件接口：默认沿用 Barotrauma.IO.File（不做路径限制）；它只在 LuaCs 开了 C# 脚本
-- （LuaCsSetupConfig.xml 的 EnableCsScripting）时才注册得上，取不到就退回 LuaCs 自带的
-- 全局 File（Barotrauma.LuaCsFile，一定存在；其可读白名单覆盖 LocalMods 与
-- WorkshopMods/Installed，足够读所有模组目录）。
local File = register_static("Barotrauma.IO.File")
local FILE_IS_LUACS = false
if File == nil then
    local ok, f = pcall(function() return _G.File end)
    if ok and f ~= nil then
        File = f
        FILE_IS_LUACS = true
    end
end

local function read_config_text(path)
    if File == nil then return nil end
    local okExists, exists = pcall(function() return File.Exists(path) end)
    if not okExists or exists ~= true then return nil end
    local okRead, text
    if FILE_IS_LUACS then
        okRead, text = pcall(function() return File.Read(path) end)
    else
        okRead, text = pcall(function() return File.ReadAllText(path) end)
    end
    if okRead and type(text) == "string" then return text end
    return nil
end

-- 收集所有已启用内容包的目录：多个来源逐级兜底（哪个能用用哪个）。读不到的路径无所谓，
-- 下面只处理真实存在的文件。
local function collect_package_dirs()
    local getters = {
        function() return ContentPackageManager.EnabledPackages.All end,
        function() return ContentPackageManager.EnabledPackages.Regular end,
        function() return ContentPackageManager.RegularPackages end,
        function() return ContentPackageManager.AllPackages end,
    }
    for _, get in ipairs(getters) do
        local dirs, seen = {}, {}
        local ok = pcall(function()
            for pkg in get() do
                local okDir, dir = pcall(function() return pkg.Dir end)
                if not okDir or type(dir) ~= "string" or dir == "" then
                    local okPath, path = pcall(function() return pkg.Path end)
                    if okPath and type(path) == "string" then
                        dir = string.match(path, "^(.*)[/\\][Ff][Ii][Ll][Ee][Ll][Ii][Ss][Tt]%.xml$")
                    end
                end
                if type(dir) == "string" and dir ~= "" then
                    dir = string.gsub(dir, "\\", "/")
                    dir = string.gsub(dir, "/+$", "")
                    if dir ~= "" and not seen[dir] then
                        seen[dir] = true
                        table.insert(dirs, dir)
                    end
                end
            end
        end)
        if ok and #dirs > 0 then return dirs end
    end
    return {}
end

local EXTRA_FILE_SUFFIX = { "/Config/CostumePack_Extra.xml", "/config/CostumePack_Extra.xml" }

local function merge_extra_items()
    if File == nil then
        print("[装束包] 扩展收录表未载入：LuaCs 文件接口不可用")
        return
    end

    -- 本模组目录排最前（即使内容包枚举失败，原来那份 Config 也照旧生效），随后是其他所有已启用内容包
    local dirs, seen_dir = {}, {}
    local function add_dir(dir)
        if type(dir) ~= "string" or dir == "" then return end
        dir = string.gsub(dir, "\\", "/")
        dir = string.gsub(dir, "/+$", "")
        if dir ~= "" and not seen_dir[dir] then
            seen_dir[dir] = true
            table.insert(dirs, dir)
        end
    end
    pcall(function() add_dir(TLE.Path) end)
    for _, dir in ipairs(collect_package_dirs()) do add_dir(dir) end

    local added, skipped, duplicated, files = 0, 0, 0, 0
    for _, dir in ipairs(dirs) do
        for _, suffix in ipairs(EXTRA_FILE_SUFFIX) do
            local text = read_config_text(dir .. suffix)
            if text ~= nil then
                files = files + 1
                -- 先剥掉注释，避免注释里的示例被当成配置
                text = string.gsub(text, "<!%-%-.-%-%->", "")
                for tag in string.gmatch(text, "<Item%s+[^/>]*") do
                    local category = string.match(tag, 'category%s*=%s*"([^"]+)"')
                    local identifier = string.match(tag, 'identifier%s*=%s*"([^"]+)"')
                    if category ~= nil and identifier ~= nil then
                        category = string.lower(category)
                        if TLE_COSTUME_PACK_ITEMS[category] == nil then
                            skipped = skipped + 1
                            print("[装束包] 扩展收录表跳过未知分类 " .. tostring(category) .. "：" .. identifier)
                        elseif CATEGORY_OF[identifier] ~= nil then
                            -- 已在表里（或别的模组先登记了），跳过
                            duplicated = duplicated + 1
                        else
                            table.insert(TLE_COSTUME_PACK_ITEMS[category], identifier)
                            CATEGORY_OF[identifier] = category
                            added = added + 1
                        end
                    end
                end
                -- 同一模组只认一份（Windows 下 Config / config 是同一个目录，避免重复读）
                break
            end
        end
    end

    if files > 0 then
        print("[装束包] 扩展收录表：扫描 " .. #dirs .. " 个模组目录，读取 " .. files ..
              " 份文件，收录 " .. added .. " 条，重复忽略 " .. duplicated .. " 条，跳过 " .. skipped .. " 条")
    end
end

merge_extra_items()
