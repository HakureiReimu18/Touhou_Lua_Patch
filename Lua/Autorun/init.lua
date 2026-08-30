TLE = {}
TLE.Name="Touhou_Lua_Expansion"
TLE.Version = "1.0"
TLE.VersionNum = 01000000
TLE.Path = table.pack(...)[1]

--[[dofile(TLE.Path.."/Lua/Scripts/Sever/Touhou_Hisoutensoku_Amor.lua")]]

if Game.IsSingleplayer or SERVER then
--[[    dofile(TLE.Path .. "/Lua/Scripts/Sever/Cook.lua")]]
    dofile(TLE.Path .. "/Lua/Scripts/Sever/Touhou_Monarch.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Sever/Touhou_Zero_Moment_Pendant.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Sever/Touhou_Magic_Weapon_Bonus.lua")
    dofile(TLE.Path .. "/Lua/Scripts/Sever/Touhou_Magic_Weapon_Skill_Gain.lua")
--[[    dofile(TLE.Path .. "/Lua/Scripts/Sever/Pseudologia.lua")]]
end

if CLIENT and not Game.IsSingleplayer then
--[[    dofile(TLE.Path .. "/Lua/Scripts/Sever/Pseudologia.lua")]]
end

	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Mod_Hotkey.lua")
	--[[改名台功能已下线（归档至 _Archive），不再加载 Touhou_Renamer.lua]]
	dofile(TLE.Path.."/Lua/Scripts/Sever/Touhou_Monorail.lua")
	dofile(TLE.Path.."/Lua/Scripts/Sever/Iroha_Versatile_Adaptation.lua")


--[[ if CLIENT then
	Timer.Wait(function()
		local runstring = "\n/// Touhou Lua Expansion"..TLE.Version.." ///\n"
		print(runstring)  
	end,1)
	dofile(TLE.Path.."/Lua/Scripts/Client/Touhou_Cam_Offset.lua")
end ]]

--[[
if not Game.IsSingleplayer then
dofile(TLE.Path.."/Lua/Scripts/Sever/Alice_Doll_Control_Change.lua")
end
if Game.IsSingleplayer then
	dofile(TLE.Path.."/Lua/Scripts/Client/Alice_Doll_Control_Change_Client.lua")
end ]]
