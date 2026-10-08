--[[东方-弹匣坯子：可选弹匣清单（客户端/服务端共用）
    弹匣坯子系统的"收录表"：坯子能换成哪几种弹匣，全部在这里配置。
    客户端按表生成「选择弹药种类」窗口（图标 + 名字 + 容量），服务端按表做权威校验，两边永远一致。
    —— 新增弹匣时只改这里（不需要动客户端窗口，也不需要动服务端脚本）：
      1. 在 Items/AmmoStorage.xml 里加一个 hideinmenus="true" 的弹匣物品（含对应弹的 ItemContainer）
      2. 在下面 magazines 列表里加一条：identifier / name_key / ammo / capacity
      3. 在 Text/Text.xml 与 Text/Items_TraditionalChinese.xml 里补上 name_key 的文本
    字段说明：
      identifier = 弹匣物品 identifier（服务端按它生成物品）
      name_key   = 窗口显示用的文本键（取不到时依次退回 entityname.<小写 identifier> → 物品名 → identifier）
      ammo       = 该弹匣装的弹药 identifier（仅用于窗口提示"装什么弹"，真正能装什么由物品 XML 的
                   <Containable> 决定）
      capacity   = 容量（仅用于窗口显示；真实容量由物品 XML 的 Health / 容器每格上限决定）
    注意：本表不提供 Config/*.xml 扩展（装束包那套 Extra 扩展这里不需要）。]]

TLE_MAGAZINE_PACK_ITEMS = {
    -- 坯子本体：客户端靠它判定"手里拿的是坯子"，服务端也用它判定"目标物品是不是坯子"
    blank = "Touhou_Magazine_Blank",

    magazines = {
        {
            identifier = "Touhou_Magazine_Revolver",
            name_key   = "touhou.magazine.name.revolver",
            ammo       = "Touhou_Maple_Leaf_Revolver_Bullet",
            capacity   = 18,
        },
        {
            identifier = "Touhou_Magazine_BlueRose",
            name_key   = "touhou.magazine.name.bluerose",
            ammo       = "Color_Up_Bullet",
            capacity   = 12,
        },
        {
            identifier = "Touhou_Magazine_Anguish",
            name_key   = "touhou.magazine.name.anguish",
            ammo       = "Touhou_Anguish_Bullet",
            capacity   = 5,
        },
        {
            identifier = "Touhou_Magazine_Blunderbuss",
            name_key   = "touhou.magazine.name.blunderbuss",
            ammo       = "Mind_Bullet",
            capacity   = 12,
        },
        {
            identifier = "Touhou_Magazine_Winchester",
            name_key   = "touhou.magazine.name.winchester",
            ammo       = "Exorcism_Bullet",
            capacity   = 7,
        },
    },
}

-- identifier -> 条目 反查（服务端权威校验用它，客户端建列表也用它）
local BY_ID = {}
for _, entry in ipairs(TLE_MAGAZINE_PACK_ITEMS.magazines) do
    BY_ID[entry.identifier] = entry
end

-- 请求的 identifier 是否在收录表里；命中返回条目，否则 nil
TLE_MAGAZINE_PACK_ITEMS.get = function(identifier)
    if type(identifier) ~= "string" then return nil end
    return BY_ID[identifier]
end
