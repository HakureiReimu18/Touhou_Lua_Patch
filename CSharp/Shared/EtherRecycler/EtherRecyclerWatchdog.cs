using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

// 以太回收机的"结算兜底"（服务端 / 单机权威侧）
// 背景：结算原本只靠 Lua 侧 Timer.Wait / think 看门狗，实机出现"单件回收卡在回收中、且看门狗无日志"。
// 这里完全绕开 Lua 的计时：每帧检查带本机 tag 的物品，只要灯亮着、且超过"预期时长 + 宽限"就通过
// LuaCs 的 Hook.Call 直接触发 Lua 的 Touhou.Recycler.end（幂等，先清结算表再干活，不会双倍产出）。
//
// 挂点照抄项目里已验证的 HomingTickPatch：服务端挂 Barotrauma.Networking.GameServer.Update，
// 客户端/单机挂 GameMain.Update（带"不是纯客户端"的守卫）。

namespace Touhou.EtherRecycler;

#if SERVER
[HarmonyPatch]
public static class EtherRecyclerWatchdogPatch
{
    static System.Reflection.MethodBase TargetMethod()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType("Barotrauma.Networking.GameServer");
            var m = t?.GetMethod("Update", flags);
            if (m != null) { return m; }
        }
        return null;
    }

    static void Postfix(float deltaTime)
    {
        EtherRecyclerWatchdog.Tick(deltaTime);
    }
}
#else
[HarmonyPatch(typeof(GameMain), "Update")]
public static class EtherRecyclerWatchdogPatch
{
    static void Postfix()
    {
        if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsServer) { return; }
        if (GameMain.Instance != null && GameMain.Instance.Paused) { return; }
        EtherRecyclerWatchdog.Tick(CoroutineManager.DeltaTime);
    }
}
#endif

internal static class EtherRecyclerWatchdog
{
    private const float GraceSeconds = 5f;
    private const float PollInterval = 0.5f;

    // item → 灯亮起的时刻（秒，用累加的仿真时间做基准，不依赖任何外部时钟）
    private static readonly Dictionary<Item, float> StartedAt = new();
    private static readonly Dictionary<Item, bool> LastLightState = new();
    private static float clock;
    private static float pollTimer;
    private static int correctionCounter;
    private const int CorrectionEveryPolls = 4; // 每 4 次轮询（≈2 秒）校正一次

#if SERVER
    private static void SendLightState(Item item, LightComponent light)
    {
        try
        {
            // 泛型由编译器推断（不要从 Lua 侧调泛型方法，LuaCs 会报"generic method without argument"）
            item.CreateServerEvent(light);
        }
        catch (Exception ex)
        {
            try { DebugConsole.NewMessage("[以太回收机] 状态校正发送失败：" + ex.Message, Color.OrangeRed); } catch { /* 控制台不可用就算了 */ }
        }
    }
#endif

    public static void Tick(float deltaTime)
    {
        try
        {
            var step = (deltaTime > 0f && !float.IsNaN(deltaTime)) ? deltaTime : (1f / 60f);
            clock += step;
            pollTimer += step;
            if (pollTimer < PollInterval) { return; }
            pollTimer = 0f;
            correctionCounter++;

            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed || !item.HasTag("Touhou_Ether_Recycler".ToIdentifier())) { continue; }

                var light = item.GetComponent<LightComponent>();
                if (light == null) { continue; }

                // ── 客户端状态校正（本次卡死的根因）──
                // LightComponent 的 IsOn 走"延迟同步"：完成时若旧事件被重发并被已处理的客户端忽略，
                // 服务端会认为"已同步"而不再发新的停止事件 → 客户端一直显示运行中/锁着入料栏。
                // 这里由服务端主动补发：状态翻转时立即发；另外每 2 秒对同一台机器无条件重发一次当前状态。
#if SERVER
                if (LastLightState.TryGetValue(item, out var lastOn) && lastOn != light.IsOn)
                {
                    SendLightState(item, light);
                }
                LastLightState[item] = light.IsOn;
                if (correctionCounter % CorrectionEveryPolls == 0)
                {
                    SendLightState(item, light);
                }
#endif

                if (!light.IsOn)
                {
                    StartedAt.Remove(item);
                    continue;
                }

                var power = item.GetComponent<PowerContainer>();
                float expected = power != null ? Math.Max(power.Charge, 0.5f) : 5f;

                if (!StartedAt.TryGetValue(item, out var startedAt))
                {
                    StartedAt[item] = clock;
                    continue;
                }

                if (clock - startedAt < expected + GraceSeconds) { continue; }

                StartedAt.Remove(item);
                try
                {
                    DebugConsole.NewMessage("[以太回收机] 结算超时，C# 兜底强制结算一次", Color.Orange);
                }
                catch { /* 控制台不可用就算了 */ }

                try
                {
                    LuaCsSetup.Instance?.Hook?.Call<object>("Touhou.Recycler.end",
                        new object[] { null, 0f, item, null, Vector2.Zero, null });
                }
                catch (Exception ex)
                {
                    try { DebugConsole.NewMessage("[以太回收机] 兜底结算失败：" + ex.Message, Color.OrangeRed); } catch { /* 同上 */ }
                }
            }
        }
        catch
        {
            // 兜底本身绝不能被异常打断
        }
    }
}
