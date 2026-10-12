--[[东方-弹药袋：可选弹种清单（客户端/服务端共用）
    弹药袋即弹药：空袋是菜单/合成入口，拿在手上弹「选择弹药种类」，选定后袋子直接插进武器当弹药，袋内存量（耐久）＝剩余弹丸数。
    本表是唯一配置点：客户端据此生成窗口（图标+名字+上限），服务端据此权威校验。
    新增弹种：① Items/AmmoStorage.xml 加一个 hideinmenus="true" 的袋子（含 ItemContainer 与补满按钮）② 下面 pouches 加一条
    identifier/name_key/ammo/capacity ③ Text/Text.xml 与 Text/Items_TraditionalChinese.xml 补 name_key 文本。
    字段：identifier=袋子 id；name_key=窗口文本键；ammo=弹药 id、capacity=上限，二者仅供窗口提示，真正能装什么/上限多少由物品 XML 的 <Containable>/Health 决定；本表不做 Config/*.xml 扩展。]]

TLE_AMMOPOUCH_PACK_ITEMS = {
    -- 空袋本体：两端都靠它识别"手里/目标是不是空袋"
    blank = "Touhou_Ammo_Pouch_Blank",

    pouches = {
        {
            identifier = "Touhou_Ammo_Pouch_Revolver",
            name_key   = "touhou.ammopouch.name.revolver",
            ammo       = "Touhou_Maple_Leaf_Revolver_Bullet",
            capacity   = 128,
        },
        {
            identifier = "Touhou_Ammo_Pouch_BlueRose",
            name_key   = "touhou.ammopouch.name.bluerose",
            ammo       = "Color_Up_Bullet",
            capacity   = 128,
        },
        {
            identifier = "Touhou_Ammo_Pouch_Anguish",
            name_key   = "touhou.ammopouch.name.anguish",
            ammo       = "Touhou_Anguish_Bullet",
            capacity   = 128,
        },
        {
            identifier = "Touhou_Ammo_Pouch_Blunderbuss",
            name_key   = "touhou.ammopouch.name.blunderbuss",
            ammo       = "Mind_Bullet",
            capacity   = 128,
        },
        {
            identifier = "Touhou_Ammo_Pouch_Winchester",
            name_key   = "touhou.ammopouch.name.winchester",
            ammo       = "Exorcism_Bullet",
            capacity   = 128,
        },
    },
}

-- identifier → 条目反查表（两端建表与校验共用）
local BY_ID = {}
for _, entry in ipairs(TLE_AMMOPOUCH_PACK_ITEMS.pouches) do
    BY_ID[entry.identifier] = entry
end

-- 未收录返回 nil
TLE_AMMOPOUCH_PACK_ITEMS.get = function(identifier)
    if type(identifier) ~= "string" then return nil end
    return BY_ID[identifier]
end
