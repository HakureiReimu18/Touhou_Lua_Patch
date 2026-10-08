--[[
	以太回收机：自定义面板的驱动（客户端脚本）
	每帧把「本地玩家当前选中的物品」交给 C# 面板（Touhou.EtherRecyclerPanel.Sync）。
	面板本体在 CSharp/Client/EtherRecyclerPanel.cs；C# 没加载 / 面板出错时 Hook.Call 是空转，
	本脚本什么也不会做，设备照常按 XML 的原生窗口排版工作。

	为什么用 Lua 驱动：补丁的 Lua 链路（按钮 → LuaHook → 服务端逻辑）已经实机验证可用，
	而 Harmony 补丁在 LuaCs 里是否命中依赖具体方法签名，出问题时既没有报错也不会有面板。
	Lua 每帧同步这条链路简单、可观测（每帧都在跑），也便于排查。
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
