--[[
	以太回收机自定义面板的驱动（客户端）：每帧把本地玩家当前选中的物品交给 C# 面板
	（Touhou.EtherRecyclerPanel.Sync）。面板本体在 CSharp/Client/EtherRecyclerPanel.cs；
	C# 没加载或出错时 Hook.Call 空转，本脚本不做事，设备照常走 XML 原生窗口。
	之所以用 Lua 驱动：Lua 链路已实机验证且每帧可观测，而 Harmony 补丁能否命中取决于
	具体方法签名，失配时既不报错也不出面板。
]]

if SERVER and not Game.IsSingleplayer then return end -- 专用服务器上不需要面板

local SYNC_HOOK = "Touhou.EtherRecyclerPanel.Sync"

Hook.Add("think", "TLE_EtherRecyclerPanel", function()
	local ok, err = pcall(function()
		local character = Character.Controlled
		Hook.Call(SYNC_HOOK, character and character.SelectedItem)
	end)

	if not ok and not TLE_EtherRecyclerPanelWarned then
		TLE_EtherRecyclerPanelWarned = true
		print("[Touhou.Recycler] 面板驱动异常（可忽略，设备功能不受影响）：" .. tostring(err))
	end
end)
