-- 东方潜渊行动组 Lua 补丁 —— 脚本装载入口
-- 目录约定：
--   Lua/Scripts/Shared/           客户端与服务端共用（装束包/弹药袋收录表、通用工具、天赋工具）
--   Lua/Scripts/Client/           客户端表现（镜头、UI、热键、装束选择窗口、弹药袋图标）
--   Lua/Scripts/Server/Talents/   角色天赋（一个天赋一个文件，替代对应 XML 能力组/事件）
--   Lua/Scripts/Server/Systems/   玩法系统（料理、定价、装束、弹药袋、魔法武器、单轨等）
--   */Disabled/                   已停用但保留的脚本，要重启用就恢复下面注释里的 dofile

TLE = {}
TLE.Name="Touhou_Lua_Expansion"
TLE.Version = "1.0"
TLE.VersionNum = 01000000
TLE.Path = table.pack(...)[1]

-- ==================== 共用：客户端和服务端都要，必须先加载 ====================
--装束包收录表：客户端选择窗口和服务端校验共用
dofile(TLE.Path .. "/Lua/Scripts/Shared/CostumePack_Items.lua")
--弹药袋收录表：客户端选择窗口和服务端校验共用（新增弹种改这一张表）
--[[⚠ 封存（2026-10）：弹药袋体系统暂停，共享收录表不再加载；恢复时把这行与
    Server/Systems/Touhou_AmmoPouch_Pack.lua、Client/Touhou_AmmoPouch_Pack_Client.lua 的反注释一起放开]]
-- dofile(TLE.Path .. "/Lua/Scripts/Shared/AmmoPouch_Pack_Items.lua")
--TH.* 通用小工具
dofile(TLE.Path .. "/Lua/Scripts/Shared/helperfunctions.lua")
--TouhouTalents.* 天赋通用工具（affliction 强度读写、敌我判定、武器判定等）
dofile(TLE.Path .. "/Lua/Scripts/Shared/TalentUtils.lua")

-- ==================== 服务端（单机 / 服务器） ====================
if Game.IsSingleplayer or SERVER then
    -- ---- 玩法系统 ----
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Cook.lua")]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Costume_Lock.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Costume_Pack.lua")
    --[[⚠ 封存（2026-10）：弹药袋体系统暂停，服务端转换不再加载；恢复时把这行与
        Shared/AmmoPouch_Pack_Items.lua、Client/Touhou_AmmoPouch_Pack_Client.lua 的反注释一起放开]]
    -- dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_AmmoPouch_Pack.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Zero_Moment_Pendant.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Magic_Weapon_Bonus.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Magic_Weapon_Skill_Gain.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Hooked_Jade_Durability.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Pricer.lua")
    --[[以太回收机：批量回收带等级的武器 → 以太层金属/副产物，见 Items/EtherRecycler.xml]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Systems/Touhou_Ether_Recycler.lua")

    -- ---- 角色天赋 ----
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

    -- ---- 停用（保留源码，与脚本文件一起在 Server/Disabled/） ----
    --[[君王追踪已迁移至 C#（CSharp/Shared/MonarchHoming.cs），避免双重转向。
        脚本文件留在磁盘上作保险，要回退就把下面这行的注释去掉、同时停用 C# 侧的 MonarchShootPatch。
    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Touhou_Monarch.lua")]]
    --[[ 人偶指挥（隐形信标方案）已停用：信标会被 DoVisibilityCheck 重新显形，
         移动耦合效果也不理想，先整链路下线。要重启用请同步恢复
         CSharp/Shared/AliceDollCommand.cs 里 OnLoadCompleted 的注释。
    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Alice_Doll_Command.lua")]]
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Pseudologia.lua")]]
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Disabled/Touhou_Hisoutensoku_Amor.lua")]]
end

-- 下面两个脚本自己处理客户端/服务端分支，保持与原来一致：两端都加载
dofile(TLE.Path.."/Lua/Scripts/Server/Systems/Touhou_Monorail.lua")
dofile(TLE.Path.."/Lua/Scripts/Server/Talents/Iroha_Versatile_Adaptation.lua")

-- ==================== 客户端 ====================
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Lock_Client.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Pack_Client.lua")
--[[⚠ 封存（2026-10）：弹药袋「选择弹药种类」窗口停载；恢复时把这行与
    Shared/AmmoPouch_Pack_Items.lua、Server/Systems/Touhou_AmmoPouch_Pack.lua 的反注释一起放开]]
-- dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_AmmoPouch_Pack_Client.lua")
--[[弹药袋图标补丁：已停用 —— 弹药袋改成每种弹一个袋子（自带图标），而袋内容器又开着
    hideitems 时代的"逐枚画在图标上"会把 5 枚待发弹重复画上去，作者反馈不要这个效果。
    文件留在磁盘作回退保险；要恢复"画 1 枚弹种图标"的话再把它 dofile 回来并改成单枚绘制。]]
-- dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Ammo_Pouch_Icons.lua")
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Mod_Hotkey.lua")
--以太回收机的自定义面板驱动（面板本体是 C#：CSharp/Client/EtherRecyclerPanel.cs）
dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Ether_Recycler_Panel.lua")
--[[改名台功能已下线（归档至 _Archive），不再加载 Touhou_Renamer.lua]]
--[[人偶指挥（隐形信标方案）已停用，与 Server 侧同步下线。
dofile(TLE.Path.."/Lua/Scripts/Client/Disabled/Alice_Doll_Command_Client.lua")]]
--[[
if not Game.IsSingleplayer then
dofile(TLE.Path.."/Lua/Scripts/Server/Disabled/Alice_Doll_Control_Change.lua")
end
if Game.IsSingleplayer then
	dofile(TLE.Path.."/Lua/Scripts/Client/Disabled/Alice_Doll_Control_Change_Client.lua")
end ]]
