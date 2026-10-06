using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.LuaCs;
using Client = Barotrauma.Networking.Client;
using IReadMessage = Barotrauma.Networking.IReadMessage;
using IWriteMessage = Barotrauma.Networking.IWriteMessage;
using ILuaCsNetworking = Barotrauma.LuaCs.Compatibility.ILuaCsNetworking;

namespace Touhou.Damage
{
    /// <summary>
    /// 客户端镜像（联机时设置页显示用）：由服务器下发的状态文本 + 编辑权限 + 拒绝原因组成。
    /// 与 BondClientState 同套路（版本号驱动写盘）。
    /// </summary>
    public static class DamageClientState
    {
        public static string StateText = null;   // 服务器下发的展示文本（不含 applied.*）
        public static bool CanEdit = true;
        public static string LastDenied = "";
        public static double LastDeniedAt = -9999;
        public static int Version;
        public static void Bump() => Version++;
    }

    /// <summary>
    /// 联机传输（照 BondNet 的成熟方案）：
    /// - 消息全部走 LuaCs 的 Networking（反射兼容层，双端程序集签名不一致）；
    /// - 服务器：收提交 → 权限校验（ConsoleCommands，服主按 OwnerConnection 兜底）→ 校验/应用数值
    ///   → 广播状态 + 定向回包 + 全服聊天公告；
    /// - 客户端：收状态写镜像（本地展示文件）、收拒绝写提示。
    /// 与绑定系统（BondNet）区别：这里额外要一条"谁改了什么"的聊天公告，用反射调 GameServer.SendChatMessage。
    /// </summary>
    public static class DamageNet
    {
        public const string NET_GET = "damage_cfgget";      // C→S：请求状态
        public const string NET_SET = "damage_cfgset";      // C→S：提交玩家值全文
        public const string NET_STATE = "damage_cfgstate";  // S→全体：状态文本
        public const string NET_STATEP = "damage_cfgstatep"; // S→单个：状态文本 + can_edit
        public const string NET_DENIED = "damage_denied";   // S→单个：拒绝原因

        public static void Register()
        {
            if (IsAuthority)
            {
                ReceiveCompat(NET_GET, args =>
                {
                    var (_, sender) = ParseArgs(args);
                    SendStateTo(sender, HasPermission(sender));
                });
                ReceiveCompat(NET_SET, args =>
                {
                    var (msg, sender) = ParseArgs(args);
                    if (msg == null) return;
                    LogLine($"收到 {ActorNameOf(sender)} 的设置提交");
                    if (!HasPermission(sender))
                    {
                        SendDenied(sender, "修改武器伤害/防具抗性设置需要管理员权限（ConsoleCommands）");
                        return;
                    }
                    string text = SafeRead(msg.ReadString);
                    if (string.IsNullOrEmpty(text))
                    {
                        SendDenied(sender, "设置内容读取失败");
                        return;
                    }
                    DamageBridge.ApplyRequestText(text, ActorNameOf(sender), sender);
                });
            }

            ReceiveCompat(NET_STATE, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                ApplyStateText(SafeRead(msg.ReadString), null);
            });
            ReceiveCompat(NET_STATEP, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                string text = SafeRead(msg.ReadString);
                bool canEdit = SafeRead(msg.ReadBoolean);
                ApplyStateText(text, canEdit);
            });
            ReceiveCompat(NET_DENIED, args =>
            {
                var (msg, _) = ParseArgs(args);
                if (msg == null) return;
                string reason = SafeRead(msg.ReadString);
                DamageClientState.LastDenied = string.IsNullOrEmpty(reason) ? "服务器拒绝了本次修改" : reason;
                DamageClientState.LastDeniedAt = Timing.TotalTime;
                DamageClientState.Bump();
                DamageLog.Warn($"提交被拒：{DamageClientState.LastDenied}");
            });
        }

        static void ApplyStateText(string text, bool? canEdit)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (DamageClientState.StateText == null)
                DamageLog.Log("已收到服务器状态快照（联机同步生效，设置页显示主机数值）");
            else if (DamageClientState.StateText != text)
                DamageLog.Debug("服务器状态已更新");
            DamageClientState.StateText = text;
            if (canEdit.HasValue && canEdit.Value != DamageClientState.CanEdit)
                DamageLog.Log(canEdit.Value ? "服务器确认你有设置权限" : "服务器下发的权限：只读（无 ConsoleCommands）");
            if (canEdit.HasValue) DamageClientState.CanEdit = canEdit.Value;
            DamageClientState.Bump();
        }

        public static bool IsAuthority =>
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

        // ---------- 客户端侧 ----------

        public static void RequestState()
        {
            if (IsAuthority) return;
            ClientSend(NET_GET, w => { });
        }

        public static void SendSet(string text)
        {
            if (IsAuthority) return;
            LogLine($"已提交设置（{text?.Length ?? 0} 字符，等待主机回包）");
            ClientSend(NET_SET, w => w.WriteString(text ?? ""));
        }

        // ---------- 服务器侧 ----------

        public static void SendDenied(Client client, string reason)
        {
            if (client == null) return;
            SendTo(client, NET_DENIED, w => w.WriteString(reason));
            LogLine($"拒绝 {client.Name}：{reason}");
        }

        public static void SendStateTo(Client client, bool canEdit)
        {
            if (client == null) return;
            string text = DamageDisplay.StrippedStateText();
            SendTo(client, NET_STATEP, w =>
            {
                w.WriteString(text ?? "");
                w.WriteBoolean(canEdit);
            });
        }

        public static void BroadcastState()
        {
            if (GameMain.NetworkMember == null) return;
            string text = DamageDisplay.StrippedStateText();
            if (string.IsNullOrEmpty(text)) return;
            Broadcast(NET_STATE, w => w.WriteString(text));
        }

        public static bool HasPermission(Client client)
        {
            if (client == null) return true;
            try
            {
                var owner = GameMain.NetworkMember?.GetType().GetProperty("OwnerConnection")?.GetValue(GameMain.NetworkMember);
                var conn = client.GetType().GetProperty("Connection")?.GetValue(client);
                if (owner != null && conn != null && ReferenceEquals(owner, conn)) return true;
            }
            catch { }
            try { return client.HasPermission(Barotrauma.Networking.ClientPermissions.ConsoleCommands); }
            catch { return false; }
        }

        public static string ActorNameOf(Client client)
        {
            if (client == null) return LocalPlayerName();
            try
            {
                var ch = client.GetType().GetProperty("Character")?.GetValue(client);
                if (ch is Character c && c != null) return c.Name ?? client.Name;
            }
            catch { }
            try { return client.Name ?? "玩家"; } catch { return "玩家"; }
        }

        public static string LocalPlayerName()
        {
            try
            {
                if (Character.Controlled != null && !string.IsNullOrEmpty(Character.Controlled.Name))
                    return Character.Controlled.Name;
            }
            catch { }
            try
            {
                var sn = GameMain.NetworkMember?.GetType().GetProperty("ServerName")?.GetValue(GameMain.NetworkMember) as string;
                if (!string.IsNullOrEmpty(sn)) return sn;
            }
            catch { }
            return "服务器";
        }

        static void LogLine(string msg) => DamageLog.Log(msg);

        // ---------- 反射收发（照 BondNet） ----------

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
                if (recv == null) { DamageLog.Warn($"Networking.Receive 未找到（{name}）"); return; }
                var delType = recv.GetParameters()[1].ParameterType;
                var invoke = delType.GetMethod("Invoke");
                var ps2 = invoke.GetParameters().Select(p =>
                    System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name ?? "p")).ToArray();
                var arr = System.Linq.Expressions.Expression.NewArrayInit(typeof(object),
                    ps2.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object))));
                var call = System.Linq.Expressions.Expression.Call(
                    typeof(DamageNet).GetMethod(nameof(RunHandler), BindingFlags.NonPublic | BindingFlags.Static),
                    System.Linq.Expressions.Expression.Constant(handler), arr);
                recv.Invoke(net, new object[] { name, System.Linq.Expressions.Expression.Lambda(delType, call, ps2).Compile() });
            }
            catch (Exception ex) { DamageLog.Warn($"注册接收器 {name} 失败：{ex.Message}"); }
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
                if (cache == null) { DamageLog.Warn("客户端 Send 未找到"); return; }
                cache.Method.Invoke(net, new object[] { msg, cache.Reliable });
            }
            catch (Exception ex) { DamageLog.Warn($"发送 {name} 失败：{ex.Message}"); }
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
                cache.Method.Invoke(net, new object[] { msg, conn, cache.Reliable });
            }
            catch (Exception ex) { DamageLog.Warn($"发送 {name} 到 {client?.Name} 失败：{ex.Message}"); }
        }
    }

    /// <summary>全服聊天公告：服务器用反射调 GameServer.SendChatMessage；单机走 CrewManager。</summary>
    public static class DamageChat
    {
        public const string SenderName = "伤害设置";

        public static void Broadcast(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool sent = false;
            var nm = GameMain.NetworkMember;
            if (nm != null && nm.IsServer)
            {
                try
                {
                    var m = nm.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(x => x.Name == "SendChatMessage"
                            && x.GetParameters() is { Length: 6 } ps && ps[0].ParameterType == typeof(string));
                    if (m != null)
                    {
                        var ps = m.GetParameters();
                        m.Invoke(nm, new object[]
                        {
                            text,
                            EnumArg(ps[1].ParameterType, "Server"),
                            null,   // Client senderClient
                            null,   // Character senderCharacter
                            EnumArg(ps[4].ParameterType, null),
                            EnumArg(ps[5].ParameterType, null),
                        });
                        sent = true;
                    }
                }
                catch (Exception ex) { DamageLog.Warn($"聊天广播失败：{ex.Message}"); }
            }
#if CLIENT
            if (!sent && GameMain.GameSession != null && GameMain.GameSession.CrewManager != null)
            {
                try
                {
                    GameMain.GameSession.CrewManager.AddSinglePlayerChatMessage(
                        SenderName, text, Barotrauma.Networking.ChatMessageType.Default, null);
                    sent = true;
                }
                catch { }
            }
#endif
            // 兜底：无论聊天通不通，控制台都留痕
            DamageLog.Log(text);
        }

        static object EnumArg(Type t, string preferred)
        {
            var u = Nullable.GetUnderlyingType(t) ?? t;
            if (preferred != null)
            {
                try { return Enum.Parse(u, preferred, true); } catch { }
            }
            return Enum.ToObject(u, 0);
        }
    }

    /// <summary>
    /// 文件桥（照 BondSettingsBridge）：
    /// - 设置页「保存/档位/重置」写 TouhouDamageRequest.txt（玩家值全文）；
    /// - 权威端（单机/主机）读请求文件 → 本地应用 + 广播 + 公告；
    /// - 纯客户端读请求文件 → 发服务器；并把服务器下发的状态写进本地 TouhouDamageState.txt
    ///   （剔除 applied.*：那是权威端的归一变基，客户端写了会污染自己以后的单机/开服归一化）。
    /// </summary>
    public static class DamageBridge
    {
        public const string RequestFileName = "TouhouDamageRequest.txt";

        static DateTime requestMtime = DateTime.MinValue;
        static int lastMirrorVersion = -1;
        static string lastMirrorDenied;
        static bool lastMirrorDeniedFresh;
        static double nextCfgRequest;

        static string RequestPath => Path.Combine(DamageValues.SaveDir(), RequestFileName);

        /// <summary>每帧调用（插件 think 钩子）。</summary>
        public static void Tick()
        {
            try
            {
                TickRequest();
                TickMirror();
            }
            catch (Exception ex) { DamageLog.Warn($"桥接出错：{ex.Message}"); }
        }

        static void TickRequest()
        {
            if (!File.Exists(RequestPath)) return;
            DateTime mtime;
            try { mtime = File.GetLastWriteTime(RequestPath); } catch { return; }
            if (mtime == requestMtime) return;
            requestMtime = mtime;

            string text;
            try { text = File.ReadAllText(RequestPath); } catch { return; }
            if (string.IsNullOrWhiteSpace(text)) return;

            if (DamageNet.IsAuthority) ApplyRequestText(text, DamageNet.LocalPlayerName(), null);
            else DamageNet.SendSet(text);
            // 处理完删掉请求文件：避免启动时把上一局的残留请求再提交/应用一遍
            try { File.Delete(RequestPath); } catch { }
        }

        static void TickMirror()
        {
            if (DamageNet.IsAuthority) return;   // 权威端状态文件由 C# 应用流程自己写（含 applied.*）

            // 定期向服务器拉状态（打开页面 / 换局后 5 秒内必然拿到）
            double now = Timing.TotalTime;
            if (now >= nextCfgRequest)
            {
                nextCfgRequest = now + 5.0;
                DamageNet.RequestState();
            }

            bool denyFresh = !string.IsNullOrEmpty(DamageClientState.LastDenied) &&
                             Timing.TotalTime - DamageClientState.LastDeniedAt < 10.0;
            if (DamageClientState.Version == lastMirrorVersion &&
                DamageClientState.LastDenied == lastMirrorDenied &&
                denyFresh == lastMirrorDeniedFresh) return;
            lastMirrorVersion = DamageClientState.Version;
            lastMirrorDenied = DamageClientState.LastDenied;
            lastMirrorDeniedFresh = denyFresh;

            string text = DamageClientState.StateText;
            if (string.IsNullOrEmpty(text) && !denyFresh) return;   // 还没拿到过服务器状态，别覆盖本地快照
            WriteMirror(text, DamageClientState.CanEdit, denyFresh ? DamageClientState.LastDenied : "");
        }

        static void WriteMirror(string stateText, bool canEdit, string denied)
        {
            try
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(stateText)) sb.Append(stateText.TrimEnd('\n', '\r')).Append('\n');
                sb.Append("canedit=").Append(canEdit ? "1" : "0").Append('\n');
                sb.Append("denied=").Append(denied ?? "").Append('\n');
                string content = sb.ToString();
                string path = DamageValues.StatePath;
                if (File.Exists(path))
                {
                    try { if (File.ReadAllText(path) == content) return; } catch { }
                }
                File.WriteAllText(path, content);
            }
            catch (Exception ex) { DamageLog.Warn($"镜像状态写入失败：{ex.Message}"); }
        }

        /// <summary>校验 + 应用 + 广播 + 回包 + 公告。requester 为 null 表示权威端本机改动。</summary>
        public static bool ApplyRequestText(string text, string actor, Client requester)
        {
            if (!DamageNet.IsAuthority) return false;
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToArray();

            if (!DamageValues.TryParseChanges(lines, out var changes, out string error))
            {
                if (requester != null) DamageNet.SendDenied(requester, "设置内容无效：" + error);
                else DamageLog.Warn("本机设置内容无效：" + error);
                return false;
            }

            var before = DamageValues.Snapshot();
            DamageValues.ApplyChanges(changes);
            DamagePatcher.ApplyAll();                        // 应用并回写状态文件（权威端）
            var diff = DamageValues.DescribeDiffSince(before);
            DamageNet.BroadcastState();
            if (requester != null) DamageNet.SendStateTo(requester, DamageNet.HasPermission(requester));
            DamageLog.Log($"已应用 {actor} 的设置（{changes.Count} 项，实际变化 {diff.Count} 项）并广播");
            AnnounceAfterChange(before, actor, diff);
            return true;
        }

        /// <summary>改动后统一收尾：广播状态 + 聊天公告差分（设置页与本机命令共用）。</summary>
        public static void AnnounceAfterChange(Dictionary<string, DamageValues.GroupSnapshot> before, string actor,
            List<string> precomputedDiff = null)
        {
            DamageNet.BroadcastState();
            var diff = precomputedDiff ?? DamageValues.DescribeDiffSince(before);
            if (diff.Count == 0) return;
            string who = string.IsNullOrEmpty(actor) ? DamageNet.LocalPlayerName() : actor;
            DamageChat.Broadcast(who + " 修改了伤害/防御设置：" + string.Join("；", diff));
        }
    }
}
