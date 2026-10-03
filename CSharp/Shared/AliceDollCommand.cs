// 爱丽丝人偶指挥桥。
// 为什么走文件：LuaCs 的 RegisterType 只搜默认程序集，Lua 够不到本模组编译出的 C# 类型，
// 所以走文件桥接（SaveUtil.DefaultSaveFolder/AliceDollCommandBridge.txt，与 TalentDescToggle 同款思路）。
//
// 方案：给每只被指挥的人偶配一个隐形"信标"（无 AI、无敌、不可见的 human），
// 人偶的原生敌对 AI 感知到信标（配置里 human→Follow）后用自带的 Follow 状态机跟住它，
// 移动和寻路全部走原版系统。信标挂在 人偶→目标 连线的中点上，人偶走多少它走多少：
//   goto : 信标吊在人偶前方朝目标带路，人偶到位后指令完成
//   hold : 信标固定在驻点，人偶聚在附近不 wandering
//   follow: 信标锚在主人身上，人偶跟着信标=跟着主人
// 行格式：cmd|dollId|x|y|ownerId，cmd ∈ goto(move)/hold/follow。
//
// 注意：Character.DoVisibilityCheck 每帧会重算 IsVisible（黑暗/潜行），直接赋值会被盖掉，
// 所以用 Harmony 后缀把信标的 IsVisible 每帧压回 false。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.AliceDollCmd
{
    public static class Bridge
    {
        const string FILE_NAME = "AliceDollCommandBridge.txt";
        const double CommandInterval = 0.5;     // 指令处理节拍
        const double DiagInterval = 2.0;        // 诊断日志节拍
        const double RespawnCooldown = 1.0;     // 信标丢失后重建的最小间隔
        const float GotoCompleteDist = 150f;    // goto 到位判定
        const float LeadMax = 250f;             // 信标最多吊在人偶前方此距离
        const float LeadMin = 60f;              // 信标至少离人偶此距离（贴脸会失去兴趣）
        const float SnapEpsilon = 45f;          // 信标与期望位置差超过此值才瞬移

        sealed class Cmd
        {
            public Character Beacon;
            public double NextSpawn;
            public Vector2 Target;
            public bool Hold;
            public Vector2 HoldPos;
            public Character Owner;
            public bool HasOwner;
            public bool Follow;   // follow=持续跟主人；goto/hold 为 false
        }

        static string bridgePath;
        static DateTime lastMtime;
        static long lastLength = -1;
        static double nextCommand;
        static double nextDiag;
        static readonly Dictionary<Character, Cmd> commands = new();
        static readonly HashSet<Character> beaconSet = new();

        public static string BridgePath
        {
            get
            {
                if (bridgePath != null) return bridgePath;
                string baseDir = null;
                try { baseDir = SaveUtil.DefaultSaveFolder; } catch { }
                bridgePath = !string.IsNullOrEmpty(baseDir)
                    ? System.IO.Path.Combine(baseDir, FILE_NAME)
                    : System.IO.Path.Combine("Data", "Saves", FILE_NAME);
                return bridgePath;
            }
        }

        public static bool IsBeacon(Character c) => beaconSet.Contains(c);

        // 每帧入口（挂 GameMain/GameServer.Update），内部有节流
        public static void Tick()
        {
            PollFile();
            double now = Timing.TotalTime;
            if (now < nextCommand) return;
            nextCommand = now + CommandInterval;
            ProcessAll(now);
        }

        static void PollFile()
        {
            string file = BridgePath;
            if (!File.Exists(file)) return;
            FileInfo fi;
            try { fi = new FileInfo(file); } catch { return; }
            if (fi.LastWriteTimeUtc == lastMtime && fi.Length == lastLength) return;
            lastMtime = fi.LastWriteTimeUtc;
            lastLength = fi.Length;

            string[] lines;
            try { lines = File.ReadAllLines(file); } catch { return; }
            try { File.WriteAllText(file, string.Empty); } catch { }

            foreach (var raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try { Execute(raw); }
                catch (Exception ex) { Warn("指令执行失败: " + ex.Message); }
            }
        }

        static void Execute(string raw)
        {
            var parts = raw.Split('|');
            if (parts.Length < 2) return;
            var cmd = parts[0].Trim();
            if (!ushort.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort id)) return;
            var entity = Entity.FindEntityByID(id);
            if (entity is not Character doll || doll.Removed || doll.IsDead) return;

            if (!commands.TryGetValue(doll, out var c))
            {
                c = new Cmd();
                commands[doll] = c;
            }

            float x = 0, y = 0;
            if (parts.Length >= 4)
            {
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
            }
            if (parts.Length >= 5 && ushort.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort ownerId))
            {
                c.Owner = Entity.FindEntityByID(ownerId) as Character;
                c.HasOwner = c.Owner != null;
            }

            switch (cmd)
            {
                case "goto":
                case "move":
                    c.Target = new Vector2(x, y);
                    c.Hold = false;
                    Log($"goto 指令: {doll.SpeciesName} -> ({x:0},{y:0})");
                    break;
                case "hold":
                    c.HoldPos = doll.WorldPosition;
                    c.Hold = true;
                    Log($"hold 指令: {doll.SpeciesName} 驻守在 ({c.HoldPos.X:0},{c.HoldPos.Y:0})");
                    break;
                case "follow":
                    c.Hold = false;
                    c.Target = c.HasOwner ? c.Owner.WorldPosition : doll.WorldPosition;
                    Log($"follow 指令: {doll.SpeciesName}");
                    break;
            }
            c.Follow = cmd == "follow";
        }

        static void ProcessAll(double now)
        {
            if (commands.Count == 0) return;
            var snapshot = new List<KeyValuePair<Character, Cmd>>(commands);
            foreach (var kv in snapshot)
            {
                var doll = kv.Key;
                var c = kv.Value;
                if (doll.Removed || doll.IsDead)
                {
                    CleanupBeacon(c.Beacon);
                    commands.Remove(doll);
                    continue;
                }
                Process(doll, c, now);
            }

            if (now >= nextDiag)
            {
                nextDiag = now + DiagInterval;
                int i = 0;
                foreach (var kv in commands)
                {
                    if (++i > 4) break;
                    var doll = kv.Key;
                    string state = doll.AIController is EnemyAIController e ? e.State.ToString() : doll.AIController?.GetType().Name ?? "noai";
                    float dBeacon = kv.Value.Beacon == null ? -1 : Vector2.Distance(doll.WorldPosition, kv.Value.Beacon.WorldPosition);
                    Log($"diag {doll.SpeciesName}: state={state} distBeacon={dBeacon:0} cmd={(kv.Value.Follow ? "follow" : kv.Value.Hold ? "hold" : "goto")}");
                }
            }
        }

        static void Process(Character doll, Cmd c, double now)
        {
            // 信标存活确认（丢失后限频重建，防止刷人类）
            if (c.Beacon == null || c.Beacon.Removed || c.Beacon.IsDead)
            {
                if (c.Beacon != null)
                {
                    Warn($"{doll.SpeciesName} 的信标丢失，限频重建");
                    UnregisterBeacon(c.Beacon);
                    c.Beacon = null;
                }
                if (now >= c.NextSpawn)
                {
                    c.NextSpawn = now + RespawnCooldown;
                    c.Beacon = SpawnBeacon(doll.WorldPosition + new Vector2(120, 0));
                }
                if (c.Beacon == null) return;
            }

            // 锚点：hold=驻点，follow=主人位置（每拍刷新），goto=目标点
            Vector2 anchor = c.Hold ? c.HoldPos : (c.Follow && c.HasOwner && !c.Owner.Removed ? c.Owner.WorldPosition : c.Target);

            if (c.Hold)
            {
                TeleportBeacon(c, c.HoldPos);
                return;
            }

            Vector2 toAnchor = anchor - doll.WorldPosition;
            float dFinal = toAnchor.Length();
            if (!c.Follow && dFinal <= GotoCompleteDist)
            {
                Log($"{doll.SpeciesName} 已到达目标，指令完成");
                commands.Remove(doll);
                CleanupBeacon(c.Beacon);
                return;
            }

            // 信标吊在 人偶→锚点 的中点（带上下限）：人偶走多少信标走多少，
            // 不会抢先跑远（旧版 700 步进+400 回拉会造成 300↔1000 来回瞬移）。
            float lead = MathHelper.Clamp(dFinal * 0.5f, LeadMin, LeadMax);
            Vector2 dir = dFinal > 1f ? toAnchor / dFinal : Vector2.Zero;
            Vector2 desired = doll.WorldPosition + dir * lead;
            if (Vector2.Distance(c.Beacon.WorldPosition, desired) > SnapEpsilon)
                TeleportBeacon(c, desired);
        }

        static Character SpawnBeacon(Vector2 worldPos)
        {
            try
            {
                var beacon = Character.Create("human", worldPos, "alicebeacon", null,
                    Entity.NullEntityID, isRemotePlayer: false, hasAi: false, createNetworkEvent: false,
                    ragdoll: null, throwErrorIfNotFound: true, spawnInitialItems: false);
                if (beacon != null)
                {
#if CLIENT
                    beacon.IsVisible = false;
#endif
                    beacon.GodMode = true;
                    beaconSet.Add(beacon);
                }
                return beacon;
            }
            catch (Exception ex)
            {
                Warn("信标生成失败: " + ex.Message);
                return null;
            }
        }

        static void TeleportBeacon(Cmd c, Vector2 worldPos)
        {
            var b = c.Beacon;
            if (b == null || b.Removed) return;
            try { b.TeleportTo(worldPos); } catch (Exception ex) { Warn("信标传送失败: " + ex.Message); }
        }

        static void UnregisterBeacon(Character beacon)
        {
            if (beacon != null) beaconSet.Remove(beacon);
        }

        static void CleanupBeacon(Character beacon)
        {
            UnregisterBeacon(beacon);
            if (beacon == null || beacon.Removed) return;
            try { beacon.DespawnNow(false); } catch (Exception ex) { Warn("信标清理失败: " + ex.Message); }
        }

        public static void ClearState()
        {
            foreach (var kv in commands) CleanupBeacon(kv.Value.Beacon);
            commands.Clear();
            beaconSet.Clear();
        }

        static void Log(string msg) => LuaCsLogger.LogMessage($"[人偶指挥/C#] {msg}", Color.LightGreen);
        static void Warn(string msg) => LuaCsLogger.LogMessage($"[人偶指挥/C#] {msg}", Color.Orange);
    }

    // 插件入口。LuaCs 会自动实例化程序集里所有 IAssemblyPlugin（参照 BondPlugin）
    public sealed class Plugin : IAssemblyPlugin
    {
        Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("touhou.alicedollcmd");
        }

        public void OnLoadCompleted()
        {
            // 【已停用】人偶指挥（隐形信标方案）整条链路暂停：
            // 信标会被 Character.DoVisibilityCheck 每帧重算显形，移动耦合效果也不理想。
            // 重启用步骤：取消下面注释 + 恢复 Lua/Autorun/init.lua 里两个
            // Alice_Doll_Command 脚本的 dofile + 清空残留桥接文件。
            /*
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(FileTickPatch));
            harmony.PatchAll(typeof(BeaconHidePatch));
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.AliceDollCmd.RoundStart", OnRoundStart);
            LuaCsLogger.LogMessage("[人偶指挥/C#] 插件已加载", Color.LightGreen);
            */
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.AliceDollCmd.RoundStart");
            harmony?.UnpatchSelf();
        }

        object OnRoundStart(object[] args)
        {
            Bridge.ClearState();
            return null;
        }
    }

    // 每帧读桥接文件。双端入口：客户端 GameMain.Update(GameTime)，服务端 GameServer.Update(float)
    [HarmonyPatch]
    public static class FileTickPatch
    {
        static MethodBase TargetMethod()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var m = typeof(GameMain).GetMethod("Update", flags);
            if (m != null) return m;
            var serverType = typeof(GameMain).Assembly.GetType("Barotrauma.Networking.GameServer");
            m = serverType?.GetMethod("Update", flags);
            if (m == null)
            {
                LuaCsLogger.LogMessage("[人偶指挥/C#] 未找到每帧 Update，用哑方法兜底防 PatchAll 中断", Color.Orange);
                return typeof(FileTickPatch).GetMethod(nameof(DummyTarget), BindingFlags.NonPublic | BindingFlags.Static);
            }
            return m;
        }

        static void DummyTarget() { }

        static void Postfix() => Bridge.Tick();
    }

    // 隐身补丁：DoVisibilityCheck 每帧重算 IsVisible，把信标强制压回不可见，
    // 否则黑暗重算后信标会重新显形（IsVisible 赋值只生效一帧）。
    // 可见性是纯客户端概念（DedicatedServer 程序集没有 IsVisible/DoVisibilityCheck），整类仅客户端编译。
#if CLIENT
    [HarmonyPatch(typeof(Character), "DoVisibilityCheck")]
    static class BeaconHidePatch
    {
        static void Postfix(Character __instance)
        {
            if (__instance != null && Bridge.IsBeacon(__instance))
                __instance.IsVisible = false;
        }
    }
#endif
}
