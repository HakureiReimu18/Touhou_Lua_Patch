using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using Barotrauma.Networking;

namespace Touhou.VirtualPantry
{
    // 走独立的 LuaCs 消息：不混进 Item.ClientEventRead 的组件索引流，免得改动原生取用/附着/库存事件。
    internal static class PantryNet
    {
        private const string RequestId = "thvp.request.v3", SnapshotId = "thvp.snapshot.v3";
        private const byte Protocol = 3;
        private static INetworkingService net;
        private static bool active;
        private static double lastWarning = double.NegativeInfinity;
        private sealed class Subscription
        {
            internal uint ClientToken, HighestRequest;
            internal double LastSeen, LastRequestAt = double.NegativeInfinity;
        }
        private static readonly Dictionary<TouhouVirtualPantry, Dictionary<Client, Subscription>> subscriptions = new();
        private static readonly Dictionary<TouhouVirtualPantry, uint> sequences = new();

        internal static void Register()
        {
            net = LuaCsSetup.Instance?.Networking;
            if (net == null) { return; } // 离线桩环境没有 LuaCs 服务宿主，静默跳过
            active = true;
            var receiver = (ILuaCsNetworking)net;
            receiver.Receive(RequestId, new LuaCsAction(args =>
            {
                if (!active || !PantryPlugin.Authority) { return; }
                try { ReceiveRequest(args[0] as IReadMessage, args.Length > 1 ? args[1] as Client : null); }
                catch (Exception e) { Warn(e); }
            }));
            receiver.Receive(SnapshotId, new LuaCsAction(args =>
            {
                if (!active || PantryPlugin.Authority) { return; }
                try { ReceiveSnapshot(args[0] as IReadMessage); }
                catch (Exception e) { Warn(e); }
            }));
        }

        internal static void SendRequest(TouhouVirtualPantry pantry, byte action, uint key)
        {
#if CLIENT
            if (!active || net == null || pantry.Retired || pantry.Item.Removed || PantryPlugin.Authority) { return; }
            // 还没拿到服务端实例标识时只能先握手（action 0），避免把操作打到复用了同一实体 ID 的新柜子上
            if (pantry.RemoteToken == 0) { action = 0; key = 0; }
            sequences.TryGetValue(pantry, out uint sequence);
            if (sequence == uint.MaxValue) { return; }
            sequences[pantry] = ++sequence;
            try
            {
                IWriteMessage msg = net.Start(RequestId);
                msg.WriteByte(Protocol);
                msg.WriteUInt16(pantry.Item.ID);
                msg.WriteUInt32(pantry.LocalToken);
                msg.WriteUInt32(pantry.RemoteToken);
                msg.WriteUInt32(sequence);
                msg.WriteUInt32(pantry.Revision);
                msg.WriteByte(action);
                msg.WriteUInt32(key);
                net.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e) { Warn(e); }
#endif
        }

        private static bool CanView(TouhouVirtualPantry pantry, Client sender)
        {
            if (pantry == null || pantry.Retired || pantry.Item.Removed || sender?.Character == null ||
                sender.Character.IsDead || sender.Character.IsUnconscious || !pantry.Item.CanClientAccess(sender)) { return false; }
            var container = pantry.Item.GetComponent<Barotrauma.Items.Components.ItemContainer>();
            return container != null && container.HasRequiredItems(sender.Character, addMessage: false);
        }

        private static void ReceiveRequest(IReadMessage msg, Client sender)
        {
            if (msg == null || sender == null || msg.ReadByte() != Protocol) { return; }
            ushort ownerId = msg.ReadUInt16();
            uint clientToken = msg.ReadUInt32(), serverToken = msg.ReadUInt32(), requestId = msg.ReadUInt32();
            uint revision = msg.ReadUInt32();
            byte action = msg.ReadByte();
            uint key = msg.ReadUInt32();
            var pantry = PantryPlugin.Find(Entity.FindEntityByID(ownerId) as Item);
            if (clientToken == 0 || requestId == 0 || action > 4 || !CanView(pantry, sender)) { return; }
            if (!subscriptions.TryGetValue(pantry, out var viewers))
            {
                viewers = new Dictionary<Client, Subscription>();
                subscriptions[pantry] = viewers;
            }
            if (!viewers.TryGetValue(sender, out var state))
            {
                // 第一次见面只接受握手，别的操作等摘要到了再放行
                if (action != 0) { return; }
                state = new Subscription { ClientToken = clientToken };
                viewers[sender] = state;
            }
            double now = Timing.TotalTime;
            if (now - state.LastRequestAt < 0.15) { return; }
            state.LastRequestAt = now;
            if (state.ClientToken != clientToken)
            {
                if (action != 0 || serverToken != 0) { return; }
                state.ClientToken = clientToken;
                state.HighestRequest = 0;
            }
            state.LastSeen = now;
            // 重发的旧请求直接回摘要，不重复执行
            if (requestId <= state.HighestRequest) { SendSnapshot(pantry, sender, state); return; }
            state.HighestRequest = requestId;
            if (action == 0 || serverToken != pantry.LocalToken)
            {
                SendSnapshot(pantry, sender, state);
                return;
            }
            pantry.QueueRemoteRequest(action, key, revision, sender, clientToken, requestId);
        }

        internal static bool IsCurrent(TouhouVirtualPantry pantry, Client sender, uint clientToken, uint requestId)
            => subscriptions.TryGetValue(pantry, out var viewers) && viewers.TryGetValue(sender, out var state) &&
                state.ClientToken == clientToken && requestId <= state.HighestRequest && Timing.TotalTime - state.LastSeen <= 3;

        internal static void Publish(TouhouVirtualPantry pantry)
        {
            if (!active || !PantryPlugin.Authority || !subscriptions.TryGetValue(pantry, out var viewers)) { return; }
            foreach (var pair in viewers.ToArray())
            {
                if (Timing.TotalTime - pair.Value.LastSeen > 3 || !CanView(pantry, pair.Key))
                { viewers.Remove(pair.Key); continue; }
                SendSnapshot(pantry, pair.Key, pair.Value);
            }
        }

        private static void SendSnapshot(TouhouVirtualPantry pantry, Client target, Subscription state)
        {
#if SERVER
            if (!active || net == null) { return; }
            try
            {
                IWriteMessage msg = net.Start(SnapshotId);
                msg.WriteByte(Protocol);
                msg.WriteUInt16(pantry.Item.ID);
                msg.WriteUInt32(state.ClientToken);
                msg.WriteUInt32(pantry.LocalToken);
                msg.WriteUInt32(state.HighestRequest);
                pantry.WriteSnapshot(msg);
                net.Send(msg, target.Connection, DeliveryMethod.Reliable);
            }
            catch (Exception e) { Warn(e); }
#endif
        }

        private static void ReceiveSnapshot(IReadMessage msg)
        {
            if (msg == null || msg.ReadByte() != Protocol) { return; }
            ushort ownerId = msg.ReadUInt16();
            uint clientToken = msg.ReadUInt32(), serverToken = msg.ReadUInt32();
            msg.ReadUInt32(); // 请求回执；库存仍以服务端为准，客户端不做预扣
            var pantry = PantryPlugin.Find(Entity.FindEntityByID(ownerId) as Item);
            // 两端各自的对象标识，挡掉旧包写到复用同一实体 ID 的新柜子上
            if (pantry == null || pantry.Retired || pantry.LocalToken != clientToken || serverToken == 0 ||
                pantry.RemoteToken != 0 && pantry.RemoteToken != serverToken) { return; }
            pantry.ReadSnapshot(msg);
            pantry.RemoteToken = serverToken;
        }

        private static void Warn(Exception e)
        {
            if (Timing.TotalTime - lastWarning < 5) { return; }
            lastWarning = Timing.TotalTime;
            DebugConsole.AddWarning("[东方食材虚拟柜] 独立同步消息未完成：" + e.Message);
        }

        internal static void Forget(TouhouVirtualPantry pantry)
        {
            subscriptions.Remove(pantry);
            sequences.Remove(pantry);
        }

        internal static void Dispose()
        {
            active = false;
            subscriptions.Clear();
            sequences.Clear();
            if (net != null)
            {
                // LuaCs 没有注销接口，只能把本包占用的两个回调换成空实现
                try
                {
                    var receiver = (ILuaCsNetworking)net;
                    receiver.Receive(RequestId, new LuaCsAction(_ => { }));
                    receiver.Receive(SnapshotId, new LuaCsAction(_ => { }));
                }
                catch { } // 关服阶段 LuaCs 服务可能已经先没了
            }
            net = null;
        }
    }
}
