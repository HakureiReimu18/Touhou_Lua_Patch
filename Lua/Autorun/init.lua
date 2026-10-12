-- 东方潜渊行动组 Lua 补丁 —— 脚本装载入口
-- 目录约定：Shared/ 两端共用（收录表、通用工具）；Client/ 客户端表现（镜头 /UI/热键/选择窗口）；
-- Server/Talents/ 天赋（一个文件一个天赋，替代对应 XML 能力组/事件）；Server/Systems/ 玩法系统（料理/定价/装束/弹药袋/魔法武器/单轨）。
-- */Disabled/ 是停用但保留的脚本，重启用就恢复下面注释里的 dofile。

TLE = {}
TLE.Name="Touhou_Lua_Expansion"
TLE.Version = "1.0"
TLE.VersionNum = 01000000
TLE.Path = table.pack(...)[1]

-- 共用：客户端与服务端都要，且必须先加载
--装束包收录表：客户端选择窗口和服务端校验共用
dofile(TLE.Path .. "/Lua/Scripts/Shared/CostumePack_Items.lua")
--[[⚠ 封存（2026-10）：弹药袋体系统暂停，本收录表不再加载；恢复时把下行与
    Server/Systems/Touhou_AmmoPouch_Pack.lua、Client/Touhou_AmmoPouch_Pack_Client.lua 的注释一起放开]]
-- dofile(TLE.Path .. "/Lua/Scripts/Shared/AmmoPouch_Pack_Items.lua")
--TH.* 通用小工具
dofile(TLE.Path .. "/Lua/Scripts/Shared/helperfunctions.lua")
--TouhouTalents.* 天赋通用工具（affliction 强度读写、敌我判定、武器判定等）
dofile(TLE.Path .. "/Lua/Scripts/Shared/TalentUtils.lua")

-- 服务端（单机 / 服务器）
if Game.IsSingleplayer or SERVER then
    -- 玩法系统
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Cook.lua")]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Costume_Lock.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Costume_Pack.lua")
    --[[⚠ 封存（2026-10）：弹药袋体系统暂停，服务端转换不再加载；恢复时把下行与
        Shared/AmmoPouch_Pack_Items.lua、Client/Touhou_AmmoPouch_Pack_Client.lua 的注释一起放开]]
    -- dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_AmmoPouch_Pack.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Zero_Moment_Pendant.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Magic_Weapon_Bonus.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Magic_Weapon_Skill_Gain.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Hooked_Jade_Durability.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Pricer.lua")
    --[[以太回收机：批量回收带等级的武器 → 以太层金属/副产物，见 Items/EtherRecycler.xml]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Ether_Recycler.lua")
    --[[⚠ 封存（2026-10）：异常物品初版整体下线，效果做得简陋，待重做后再放出。
        Items/AnomalousItems.xml 与 Items/Afflictions.xml 末尾三件 affliction 已同步注释；
        恢复时把下行与那两处的注释一起放开]]
    -- dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Anomalous_Items.lua")

    -- 角色天赋
    --[[红美铃天赋「气与姿态」：须排在 Touhou_Magic_Weapon_Bonus.lua 之后（DamageMultiplier 补丁依赖其加载顺序）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Meiling_Qi_Talent.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Jyoon_Wealth_Talent.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Sanae_Miracle_Talent.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Renko_Red_Spot_Freak_Report.lua")
    --[[幽幽子天赋「华胥的亡灵」：专属武器处决失去意识的敌人（原 XML 能力组已注释，见 Talents/TalentsYuyuko.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Yuyuko_Talent.lua")
    --[[魔理沙天赋「普通的魔法使」：攻击积累魔力过载，按攻击事件去重（原 XML 能力组已注释，见 Talents/TalentsMarisa.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Marisa_Magic_Overload.lua")
    --[[恋天赋「地底蔷薇」：随机间隔的随机等级无意识（原 XML 能力组与事件已注释，见 Talents/TalentsKoishi.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Koishi_Unconscious.lua")
    --[[紫天赋「境界的妖怪」：一巡回一次的全体传送（原事件已注释，见 Events/Touhou_Talents_Event.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Yukari_Teleport.lua")
    --[[灵梦天赋「童祭」：受到非人类造成的伤害降低 20%（原 XML 能力组已注释，见 Talents/TalentsReimu.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Reimu_Protection.lua")
    --[[椛天赋「山中的千里眼」：附近有敌人时获得警戒（原 XML 能力组已注释，见 Talents/TalentsMomizi.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Momizi_Alert.lua")
    --[[键山雏天赋「秘神流雏」：主动释放厄运缠身（原 XML 能力组已注释，见 Talents/TalentsKagiyama_Hina.xml；
        装束按钮同步改为只挂释放标记，见 Items/Clothes/Kagiyama_Hina_gear.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Kagiyama_Hina_Misfortune_Release.lua")
    --[[紫苑天赋「让一切徒劳无功的贫穷神」：每个巡回扣 50mk（原 XML 的 <CharacterAbilityGiveMoney amount="-50"/>
        会撞上 Character.GiveMoney 的 amount > 0 闸门被静默丢弃，见 Talents/TalentsShion.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Shion_Poverty_Tax.lua")
    --[[妹红天赋「不死之烟」：全队共享一次的复活（原 XML 只管自己、救不到队友，触发判定搬进 Lua，见 Talents/TalentsMokou.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Mokou_Shared_Resurrection.lua")
    --[[因幡天为天赋的随机效果：攻击/治疗抽奖 + 出血效应（原 XML 触发组与三段事件已注释，见 Talents/TalentsTewi.xml、
        Talents/Talents_MRE.xml、Events/Touhou_Special_Events.xml）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Talents/Tewi_Lucky_Talent.lua")

    -- 停用（源码在 Server/Disabled/）
    --[[君王追踪已迁移至 C#（CSharp/Shared/MonarchHoming.cs），避免双重转向；回退就放开下行注释
        并停用 C# 侧 MonarchShootPatch。
    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Touhou_Monarch.lua")]]
    --[[人偶指挥（隐形信标方案）已停用：信标会被 DoVisibilityCheck 重新显形；重启用需同步恢复
         CSharp/Shared/AliceDollCommand.cs 里 OnLoadCompleted 的注释。
    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Alice_Doll_Command.lua")]]
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Pseudologia.lua")]]
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Touhou_Hisoutensoku_Amor.lua")]]
end

-- 下面两个脚本自己处理客户端/服务端分支，保持与原来一致：两端都加载
dofile(TLE.Path.."/Lua/Scripts/Server/Systems/Touhou_Monorail.lua")
dofile(TLE.Path.."/Lua/Scripts/Server/Talents/Iroha_Versatile_Adaptation.lua")

-- 客户端
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Lock_Client.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Pack_Client.lua")
--[[⚠ 封存（2026-10）：弹药袋「选择弹药种类」窗口停载；恢复时把下行与
    Shared/AmmoPouch_Pack_Items.lua、Server/Systems/Touhou_AmmoPouch_Pack.lua 的注释一起放开]]
-- dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_AmmoPouch_Pack_Client.lua")
--[[弹药袋图标补丁：已停用——新方案每种弹一个袋子（自带图标），而袋内容器仍开着 hideitems，
    逐枚绘制会把待发弹重复画上去。要恢复"画 1 枚弹种图标"需把它 dofile 回来并改成单枚绘制。]]
-- dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Ammo_Pouch_Icons.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Mod_Hotkey.lua")
--以太回收机的自定义面板驱动（面板本体是 C#：CSharp/Client/EtherRecyclerPanel.cs）
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Ether_Recycler_Panel.lua")
--[[改名台功能已下线（归档至 _Archive），不再加载 Touhou_Renamer.lua]]
--[[人偶指挥（隐形信标方案）已停用，与 Server 侧同步下线。
dofile(TLE.Path.."/Lua/Scripts/Client/Disabled/Alice_Doll_Command_Client.lua")]]
--[[人偶指挥控制切换整链下线（未加载，源码在 Disabled/）。要恢复就按原样放开：
    非单机加载 Server/Disabled/Alice_Doll_Control_Change.lua，单机加载 Client/Disabled/Alice_Doll_Control_Change_Client.lua]]
