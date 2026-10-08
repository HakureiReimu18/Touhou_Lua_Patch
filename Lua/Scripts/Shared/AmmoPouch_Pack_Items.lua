--[[东方-弹药袋：可选弹种清单（客户端/服务端共用）
    v5：弹药袋即弹药。空弹药袋是菜单/合成里的入口，拿在手上弹出「选择弹药种类」窗口，
    选定后变成对应弹种的弹药袋——袋子直接插进武器当弹药用，袋内存量（耐久）就是剩余弹丸数。
    这张"收录表"是唯一的配置点：客户端按它生成窗口（图标 + 名字 + 存量上限），
    服务端按它做权威校验，两边永远一致。
    —— 新增弹种时只改这里（不用动客户端窗口，也不用动服务端脚本）：
      1. 在 Items/AmmoStorage.xml 里加一个 hideinmenus="true" 的弹药袋（含 ItemContainer 与补满按钮）
      2. 在下面 pouches 列表里加一条：identifier / name_key / ammo / capacity
      3. 在 Text/Text.xml 与 Text/Items_TraditionalChinese.xml 里补上 name_key 的文本
    字段说明：
      identifier = 袋子物品 identifier（服务端按它生成物品）
      name_key   = 窗口显示用的文本键（取不到时依次退回 entityname.<小写 identifier> → 物品名 → identifier）
      ammo       = 该袋装的弹药 identifier（窗口提示用；真正能装什么由物品 XML 的 <Containable> 决定）
      capacity   = 存量上限（窗口显示用；真实上限由物品 XML 的 Health / 容器每格上限决定）
    注意：本表不提供 Config/*.xml 扩展（装束包那套 Extra 扩展这里不需要）。]]

TLE_AMMOPOUCH_PACK_ITEMS = {
    -- 空袋本体：客户端靠它判定"手里拿的是不是空袋"，服务端也用它判定"目标物品是不是空袋"
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

-- identifier -> 条目 反查（服务端权威校验用它，客户端建列表也用它）
local BY_ID = {}
for _, entry in ipairs(TLE_AMMOPOUCH_PACK_ITEMS.pouches) do
    BY_ID[entry.identifier] = entry
end

-- 请求的 identifier 是否在收录表里；命中返回条目，否则 nil
TLE_AMMOPOUCH_PACK_ITEMS.get = function(identifier)
    if type(identifier) ~= "string" then return nil end
    return BY_ID[identifier]
end
