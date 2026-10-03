-- 写完这段就睡觉

TLE = {}
TLE.Name="Touhou_Lua_Expansion"
TLE.Version = "1.0"
TLE.VersionNum = 01000000
TLE.Path = table.pack(...)[1]

--[[dofile(TLE.Path.."/Lua/Scripts/Server/Touhou_Hisoutensoku_Amor.lua")]]

--装束包收录表：客户端选择窗口和服务端校验共用，必须先加载
dofile(TLE.Path .. "/Lua/Scripts/CostumePack_Items.lua")

if Game.IsSingleplayer or SERVER then
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Cook.lua")]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Costume_Lock.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Costume_Pack.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Monarch.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Zero_Moment_Pendant.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Magic_Weapon_Bonus.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Magic_Weapon_Skill_Gain.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Hooked_Jade_Durability.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Touhou_Pricer.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Jyoon_Wealth_Talent.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Server/Sanae_Miracle_Talent.lua")
    --[[红美铃天赋「气与姿态」：须排在 Touhou_Magic_Weapon_Bonus.lua 之后（DamageMultiplier 补丁依赖其加载顺序）]]
    dofile(TLE.Path .. "/Lua/Scripts/Server/Meiling_Qi_Talent.lua")
    --[[ 人偶指挥（隐形信标方案）已停用：信标会被 DoVisibilityCheck 重新显形，
         移动耦合效果也不理想，先整链路下线。要重启用请同步恢复
         CSharp/Shared/AliceDollCommand.cs 里 OnLoadCompleted 的注释。
    dofile(TLE.Path .. "/Lua/Scripts/Server/Alice_Doll_Command.lua")]]
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Pseudologia.lua")]]
end


if CLIENT and not Game.IsSingleplayer then
--[[    dofile(TLE.Path .. "/Lua/Scripts/Server/Pseudologia.lua")]]
end

	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Lock_Client.lua")
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Costume_Pack_Client.lua")
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Mod_Hotkey.lua")
	--[[改名台功能已下线（归档至 _Archive），不再加载 Touhou_Renamer.lua]]
	--[[人偶指挥（隐形信标方案）已停用，与 Server 侧同步下线。
	dofile(TLE.Path.."/Lua/Scripts/Client/Alice_Doll_Command_Client.lua")]]
	dofile(TLE.Path.."/Lua/Scripts/Server/Touhou_Monorail.lua")
	dofile(TLE.Path.."/Lua/Scripts/Server/Iroha_Versatile_Adaptation.lua")


--[[ if CLIENT then
	Timer.Wait(function()
		local runstring = "\n/// Touhou Lua Expansion"..TLE.Version.." ///\n"
		print(runstring)  
	end,1)
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
end ]]

--[[
if not Game.IsSingleplayer then
dofile(TLE.Path.."/Lua/Scripts/Server/Alice_Doll_Control_Change.lua")
end
if Game.IsSingleplayer then
	dofile(TLE.Path.."/Lua/Scripts/Client/Alice_Doll_Control_Change_Client.lua")
end ]]
