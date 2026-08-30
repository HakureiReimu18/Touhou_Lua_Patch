using System;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace ExploredMap
{
    /// <summary>
    /// 模组入口。LuaCs 在程序集中扫描 IAssemblyPlugin 并依次回调：
    /// Initialize → PreInitPatching → OnLoadCompleted（内容加载完毕）→ Dispose（卸载）。
    /// 追踪节拍由 Level.Update 的 Harmony 后置补丁驱动（补丁点经社区模组实机验证），
    /// 不依赖 GameMain.LuaCs.Hook（该 API 在新版本已移除）。
    /// </summary>
    public sealed class ExploredMapPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private bool initialized;

        public void Initialize()
        {
        }

        public void PreInitPatching()
        {
        }

        public void OnLoadCompleted()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;

            harmony = new Harmony("touhou.exploredmap");
            harmony.PatchAll();
            ExploredMapTracker.Enabled = true;
            LuaCsLogger.Log("[探索海图仪] 模组已加载");
        }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            ExploredMapTracker.Enabled = false;
            ExploredMapTracker.ResetState();
            initialized = false;
        }
    }

    /// <summary>Level.Update 后置补丁：每帧驱动探索追踪（只在关卡激活时由游戏调用）。</summary>
    [HarmonyPatch(typeof(Level), nameof(Level.Update))]
    internal static class LevelUpdatePatch
    {
        private static void Postfix(float deltaTime)
        {
            ExploredMapTracker.Tick(deltaTime);
        }
    }
}
