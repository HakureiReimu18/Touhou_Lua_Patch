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
        "Reimu_Decalmask",
        "Renko_Hat",
        "Rumia_Headband",
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
-- 扩展收录：Config/CostumePack_Extra.xml（给拓展模组/玩家用，不用改本文件）
-- <Item category="..." identifier="..." />，category：basic=装束包 / plus=装束包+ / headwear=头饰包
-- 重复的 id 自动去重；分类名写错会被跳过并打日志。
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

local File = register_static("Barotrauma.IO.File")

local function merge_extra_items()
    if File == nil then return end
    local okPath, path = pcall(function() return TLE.Path .. "/Config/CostumePack_Extra.xml" end)
    if not okPath or path == nil then return end
    local okExists, exists = pcall(function() return File.Exists(path) end)
    if not okExists or not exists then return end

    local okRead, text = pcall(function() return File.ReadAllText(path) end)
    if not okRead or text == nil then return end

    -- 先剥掉注释，避免注释里的示例被当成配置
    text = string.gsub(text, "<!%-%-.-%-%->", "")

    local added, skipped = 0, 0
    for tag in string.gmatch(text, "<Item%s+[^/>]*") do
        local category = string.match(tag, 'category%s*=%s*"([^"]+)"')
        local identifier = string.match(tag, 'identifier%s*=%s*"([^"]+)"')
        if category ~= nil and identifier ~= nil then
            category = string.lower(category)
            if TLE_COSTUME_PACK_ITEMS[category] == nil then
                skipped = skipped + 1
                print("[装束包] 扩展收录表跳过未知分类 " .. tostring(category) .. "：" .. identifier)
            elseif CATEGORY_OF[identifier] ~= nil then
                -- 已在表里（或重复行），跳过
            else
                table.insert(TLE_COSTUME_PACK_ITEMS[category], identifier)
                CATEGORY_OF[identifier] = category
                added = added + 1
            end
        end
    end
    if added > 0 or skipped > 0 then
        print("[装束包] 扩展收录表载入 " .. added .. " 条，跳过 " .. skipped .. " 条（" .. path .. "）")
    end
end

merge_extra_items()
