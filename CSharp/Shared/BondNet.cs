using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using Client = Barotrauma.Networking.Client;
using IReadMessage = Barotrauma.Networking.IReadMessage;
using IWriteMessage = Barotrauma.Networking.IWriteMessage;
using ILuaCsNetworking = Barotrauma.LuaCs.Compatibility.ILuaCsNetworking;

namespace Touhou.Bond
{
    // 没救了
    // 客户端缓存的配对镜像，GUI/HUD 从这里取数
    public static class BondClientState
    {
        public sealed class PairInfo { public string IdA, NameA, IdB, NameB; }
        public sealed class CandInfo { public string Id, Name; }
        public static readonly List<PairInfo> Pairs = new();
        public static readonly List<CandInfo> Candidates = new();
        // 服务器下发的配置，只读；要改走 NET_CFGSET
        public static readonly Dictionary<string, string> CfgValues = new();
        public static string MyPartnerName = "";
        public static string LastDenied = "";
        public static double LastDeniedAt;
        // 镜像一变就 +1，GUI 靠它判断要不要刷新
        public static int Version;
        public static void Bump() => Version++;
    }

    // 双端程序集签名不一样，收发照主插件那套全程反射
    public static class BondNet
    {
        public const string NET_REQ = "bond_req";       // C→S {targetId}，selfId 服务端从发送者解析，防代签
        public const string NET_BREAK = "bond_break";   // C→S {}（同上）
        public const string NET_DENIED = "bond_denied";
        public const string NET_PAIRS = "bond_pairs";   // S→全 {count, (idA,nameA,idB,nameB)×n}
        public const string NET_YOU = "bond_you";
        public const string NET_CAND = "bond_cand";     // C→S {}，打开面板时发
        public const string NET_CANDLIST = "bond_candlist"; // S→C {count, (id,name)×n}
        public const string NET_CFGGET = "bond_cfgget";   // C→S {}
        public const string NET_CFGSET = "bond_cfgset";   // C→S {count,(key,value)×n}，需管理员权限
        public const string NET_CFGSTATE = "bond_cfgstate"; // S→全 {count,(key,value)×n}

        // 白名单：设置窗口能改哪些键
        static readonly string[] CfgKeys = { "settleinterval", "resistancescale", "maxlinkdistance", "countedtypes", "condlossmult" };

        // 自己的 id 怎么定：优先从发送者解析，单人取本机角色
        static string SelfIdOf(Client sender) =>
            sender != null ? BondMatch.IdOf(sender)
            : GameMain.NetworkMember == null ? BondMatch.IdOfCharacter(Character.Controlled)
            : null;

        public static void Register()
        {
            if (IsAuthorityContext())
            {
                ReceiveCompat(NET_REQ, args =>
                {
                    var (msg, sender) = ParseArgs(args);
                    string targetId = msg != null ? SafeRead(msg.ReadString) : null;
                    string selfId = SelfIdOf(sender);
                    if (selfId == null) return;
                    // 直接传发送者的角色，不靠 id 回环匹配
                    string err = BondMatch.RequestPair(selfId, targetId, sender?.Character);
                    if (err != null)
                    {
                        BondLog.Debug($"配对请求被拒：{selfId} → {targetId}（{err}）");
                        SendTo(sender, NET_DENIED, w => w.WriteString(err));
                    }
                });
                ReceiveCompat(NET_BREAK, args =>
                {
                    var (_, sender) = ParseArgs(args);
                    string selfId = SelfIdOf(sender);
                    if (selfId == null) return;
                    BondMatch.BreakPair(selfId, forced: false);
                });
                ReceiveCompat(NET_CAND, args =>
                {
                    var (_, sender) = ParseArgs(args);
                    SendTo(sender, NET_CANDLIST, w =>
                    {
                        var list = BondMatch.BuildCandidateList();
                        w.WriteUInt16((ushort)list.Count);
                        foreach (var c in list)
                        {
                            w.WriteString(c.Id);
                            w.WriteString(c.Name);
                        }
                    });
                });
                ReceiveCompat(NET_CFGGET, args =>
                {
                    var (_, sender) = ParseArgs(args);
                    SendTo(sender, NET_CFGSTATE, WriteCfgState);
                });
                ReceiveCompat(NET_CFGSET, args =>
                {
                    var (msg, sender) = ParseArgs(args);
                    if (msg == null) return;
                    bool permitted = DebugConsole.CheatsEnabled &&
                        (sender == null ||
                         sender.HasPermission(Barotrauma.Networking.ClientPermissions.ConsoleCommands));
                    if (!permitted)
                    {
                        SendTo(sender, NET_DENIED,
                            w => w.WriteString("修改设置需要作弊权限（服务器 enablecheats + ConsoleCommands 权限）"));
                        return;
                    }
                    int n = SafeRead(msg.ReadUInt16);
                    for (int i = 0; i < n; i++)
                    {
                        string k = SafeRead(msg.ReadString);
                        string v = SafeRead(msg.ReadString);
                        if (k == null || v == null || !CfgKeys.Contains(k)) continue;
                        BondConfig.Set(k, v);
                    }
                    BondLog.Log($"{sender?.Name ?? "?"} 通过设置窗口修改了绑定配置");
                    BroadcastCfgState();
                });
            }

            ReceiveCompat(NET_PAIRS, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                BondClientState.Pairs.Clear();
                int n = SafeRead(msg.ReadUInt16);
                for (int i = 0; i < n; i++)
                {
                    BondClientState.Pairs.Add(new BondClientState.PairInfo
                    {
                        IdA = SafeRead(msg.ReadString), NameA = SafeRead(msg.ReadString),
                        IdB = SafeRead(msg.ReadString), NameB = SafeRead(msg.ReadString)
                    });
                }
                BondClientState.Bump();
                BondLog.Debug($"配对镜像更新：{BondClientState.Pairs.Count} 对");
            });
            ReceiveCompat(NET_YOU, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                BondClientState.MyPartnerName = SafeRead(msg.ReadString) ?? "";
                BondClientState.Bump();
                if (!string.IsNullOrEmpty(BondClientState.MyPartnerName))
                    BondLog.Debug($"你的绑定对象：{BondClientState.MyPartnerName}");
            });
            ReceiveCompat(NET_CANDLIST, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                BondClientState.Candidates.Clear();
                int n = SafeRead(msg.ReadUInt16);
                for (int i = 0; i < n; i++)
                {
                    BondClientState.Candidates.Add(new BondClientState.CandInfo
                    {
                        Id = SafeRead(msg.ReadString),
                        Name = SafeRead(msg.ReadString)
                    });
                }
                BondClientState.Bump();
                BondLog.Debug($"候选人镜像更新：{BondClientState.Candidates.Count} 人");
            });
            ReceiveCompat(NET_DENIED, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                BondClientState.LastDenied = SafeRead(msg.ReadString) ?? "";
                BondClientState.LastDeniedAt = Timing.TotalTime;
                BondClientState.Bump();
                BondLog.Debug($"配对被拒：{BondClientState.LastDenied}");
            });
            ReceiveCompat(NET_CFGSTATE, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                BondClientState.CfgValues.Clear();
                int n = SafeRead(msg.ReadUInt16);
                for (int i = 0; i < n; i++)
                {
                    string k = SafeRead(msg.ReadString);
                    string v = SafeRead(msg.ReadString);
                    if (k != null && v != null) BondClientState.CfgValues[k] = v;
                }
                BondClientState.Bump();
            });
        }

        static Dictionary<string, string> CurrentCfgValues() => new Dictionary<string, string>
        {
            ["settleinterval"] = BondConfig.SettleInterval.ToString("0.##"),
            ["resistancescale"] = BondConfig.ResistanceScale.ToString("0.##"),
            ["maxlinkdistance"] = BondConfig.MaxLinkDistance.ToString("0.##"),
            ["countedtypes"] = BondConfig.CountedTypes,
            ["condlossmult"] = BondConfig.CondLossMult.ToString("0.##")
        };

        static void WriteCfgState(IWriteMessage w)
        {
            var values = CurrentCfgValues();
            w.WriteUInt16((ushort)values.Count);
            foreach (var kv in values)
            {
                w.WriteString(kv.Key);
                w.WriteString(kv.Value);
            }
        }

        static void BroadcastCfgState()
        {
            if (GameMain.NetworkMember == null) { SyncCfgLocal(); return; }
            Broadcast(NET_CFGSTATE, WriteCfgState);
        }

        static void SyncCfgLocal()
        {
            BondClientState.CfgValues.Clear();
            foreach (var kv in CurrentCfgValues()) BondClientState.CfgValues[kv.Key] = kv.Value;
            BondClientState.Bump();
        }

        // 单人没网，一律本地直接搞
        public static void RequestCfgState()
        {
            if (GameMain.NetworkMember == null) { SyncCfgLocal(); return; }
            ClientSend(NET_CFGGET, w => { });
        }

        // 单人直接生效，多人送服务器验权限
        public static void SendCfgSet(Dictionary<string, string> values)
        {
            if (GameMain.NetworkMember == null)
            {
                foreach (var kv in values) BondConfig.Set(kv.Key, kv.Value);
                SyncCfgLocal();
                return;
            }
            ClientSend(NET_CFGSET, w =>
            {
                w.WriteUInt16((ushort)values.Count);
                foreach (var kv in values)
                {
                    w.WriteString(kv.Key);
                    w.WriteString(kv.Value);
                }
            });
        }

        static bool IsAuthorityContext() =>
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

        static (IReadMessage msg, Client sender) ParseArgs(object[] args)
        {
            IReadMessage msg = null; Client sender = null;
            if (args == null) return (msg, sender);
            foreach (var a in args)
            {
                if (a is IReadMessage m) msg = m;
                else if (a is Client c) sender = c;
            }
            return (msg, sender);
        }

        static T SafeRead<T>(Func<T> read)
        {
            try { return read(); } catch { return default; }
        }

        public static void RequestPair(string targetId)
        {
            if (GameMain.NetworkMember == null)
            {
                string selfId = BondMatch.IdOfCharacter(Character.Controlled);
                string err = selfId == null ? "无法确定你的身份" : BondMatch.RequestPair(selfId, targetId);
                if (err != null)
                {
                    BondClientState.LastDenied = err;
                    BondClientState.LastDeniedAt = Timing.TotalTime;
                    BondClientState.Bump();
                    BondLog.Warn($"配对失败：{err}");
                }
                return;
            }
            ClientSend(NET_REQ, w => w.WriteString(targetId ?? ""));
        }

        public static void RequestBreak()
        {
            if (GameMain.NetworkMember == null)
            {
                string selfId = BondMatch.IdOfCharacter(Character.Controlled);
                if (selfId != null) BondMatch.BreakPair(selfId, forced: false);
                return;
            }
            ClientSend(NET_BREAK, w => { });
        }

        public static void RequestCandidates()
        {
            if (GameMain.NetworkMember == null)
            {
                BondClientState.Candidates.Clear();
                foreach (var c in BondMatch.BuildCandidateList())
                    BondClientState.Candidates.Add(new BondClientState.CandInfo { Id = c.Id, Name = c.Name });
                BondClientState.Bump();
                return;
            }
            ClientSend(NET_CAND, w => { });
        }

        // 单人没网就写本地镜像
        public static void BroadcastPairs()
        {
            var pairs = BondMatch.DistinctPairs().ToList();
            if (GameMain.NetworkMember == null)
            {
                SyncLocalMirror(pairs);
                return;
            }
            Broadcast(NET_PAIRS, w =>
            {
                w.WriteUInt16((ushort)pairs.Count);
                foreach (var p in pairs)
                {
                    w.WriteString(p.IdA); w.WriteString(p.NameA);
                    w.WriteString(p.IdB); w.WriteString(p.NameB);
                }
            });
            foreach (var p in pairs)
            {
                var clientA = BondMatch.FindClientById(p.IdA);
                var clientB = BondMatch.FindClientById(p.IdB);
                if (clientA != null) SendTo(clientA, NET_YOU, w => w.WriteString(p.NameB));
                if (clientB != null) SendTo(clientB, NET_YOU, w => w.WriteString(p.NameA));
            }
        }

        // 单人模式权威和客户端同一进程，直接写镜像；MyPartnerName 按本机角色算
        static void SyncLocalMirror(List<PairEntry> pairs)
        {
            BondClientState.Pairs.Clear();
            foreach (var p in pairs)
                BondClientState.Pairs.Add(new BondClientState.PairInfo
                { IdA = p.IdA, NameA = p.NameA, IdB = p.IdB, NameB = p.NameB });
            var me = BondMatch.IdOfCharacter(Character.Controlled);
            BondClientState.MyPartnerName = me != null && BondState.Pairs.TryGetValue(me, out var entry)
                ? entry.OtherName(me) : "";
        }

        static void ReceiveCompat(string name, Action<object[]> handler)
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                if (net is ILuaCsNetworking compat)
                {
                    compat.Receive(name, (LuaCsAction)(args => handler(args)));
                    return;
                }
                var recv = net.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "Receive" && m.GetParameters() is { Length: 2 } ps &&
                    ps[0].ParameterType == typeof(string) && ps[1].ParameterType.IsSubclassOf(typeof(Delegate)));
                if (recv == null) { BondLog.Warn($"Networking.Receive 未找到（{name}）"); return; }
                var delType = recv.GetParameters()[1].ParameterType;
                var invoke = delType.GetMethod("Invoke");
                var ps2 = invoke.GetParameters().Select(p =>
                    System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name ?? "p")).ToArray();
                var arr = System.Linq.Expressions.Expression.NewArrayInit(typeof(object),
                    ps2.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));
                var call = System.Linq.Expressions.Expression.Call(
                    typeof(BondNet).GetMethod(nameof(RunHandler),
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static),
                    System.Linq.Expressions.Expression.Constant(handler), arr);
                recv.Invoke(net, new object[] { name, System.Linq.Expressions.Expression.Lambda(delType, call, ps2).Compile() });
            }
            catch (Exception ex) { BondLog.Warn($"注册接收器 {name} 失败：{ex.Message}"); }
        }

        static void RunHandler(Action<object[]> handler, object[] args) => handler(args);

        sealed class SendMethodCache { public MethodInfo Method; public object Reliable; }

        static readonly Lazy<SendMethodCache> clientSendCache = new Lazy<SendMethodCache>(ResolveClientSend);
        static readonly Lazy<SendMethodCache> connSendCache = new Lazy<SendMethodCache>(ResolveConnSend);

        static SendMethodCache ResolveClientSend()
        {
            var net = LuaCsSetup.Instance.Networking;
            if (net == null) return null;
            var m = net.GetType().GetMethods().FirstOrDefault(x =>
                x.Name == "Send" && x.GetParameters() is { Length: 2 } ps &&
                ps[1].ParameterType.IsEnum && ps[1].ParameterType.Name == "DeliveryMethod");
            return m == null ? null : new SendMethodCache
            {
                Method = m,
                Reliable = Enum.Parse(m.GetParameters()[1].ParameterType, "Reliable")
            };
        }

        static SendMethodCache ResolveConnSend()
        {
            var net = LuaCsSetup.Instance.Networking;
            if (net == null) return null;
            var m = net.GetType().GetMethods().FirstOrDefault(x =>
                x.Name == "Send" && x.GetParameters() is { Length: 3 } ps &&
                ps[1].ParameterType.Name == "NetworkConnection" && ps[2].ParameterType.IsEnum);
            return m == null ? null : new SendMethodCache
            {
                Method = m,
                Reliable = Enum.Parse(m.GetParameters()[2].ParameterType, "Reliable")
            };
        }

        // C→S；本地服务器/单人由 receive 路径兜住
        static void ClientSend(string name, Action<IWriteMessage> write)
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                var msg = net.Start(name);
                write(msg);
                var cache = clientSendCache.Value;
                if (cache == null) { BondLog.Warn("客户端 Send 未找到"); return; }
                cache.Method.Invoke(net, new object[] { msg, cache.Reliable });
            }
            catch (Exception ex) { BondLog.Warn($"发送 {name} 失败：{ex.Message}"); }
        }

        static void Broadcast(string name, Action<IWriteMessage> write)
        {
            if (GameMain.NetworkMember == null) return;
            foreach (var client in GameMain.NetworkMember.ConnectedClients)
                SendTo(client, name, write);
        }

        static void SendTo(Client client, string name, Action<IWriteMessage> write)
        {
            if (client == null) return;
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                var conn = client.GetType().GetProperty("Connection")?.GetValue(client);
                if (conn == null) return;
                var cache = connSendCache.Value;
                if (cache == null) return;
                var msg = net.Start(name);
                write(msg);
                cache.Method.Invoke(net, new[] { msg, conn, cache.Reliable });
            }
            catch (Exception ex) { BondLog.Warn($"发送 {name} 到 {client?.Name} 失败：{ex.Message}"); }
        }
    }
}
