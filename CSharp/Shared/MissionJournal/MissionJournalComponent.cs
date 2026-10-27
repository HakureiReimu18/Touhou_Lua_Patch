using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;

namespace Touhou.MissionJournal
{
    public sealed partial class TouhouMissionJournal : ItemComponent, IClientSerializable, IServerSerializable
    {
        internal static readonly JournalLedger Ledger = new();
        internal static readonly HashSet<TouhouMissionJournal> Instances = new();
        internal static bool Authority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
        internal readonly JournalDocument Document;
        internal string RemoteEpoch = "", RemoteEvent = "", RemoteText = "", Failure = "";
        internal bool RemoteKnown;
        private double nextQuery;
        private float lastSnapshot = float.NegativeInfinity;
        internal JournalChoice Choice => Authority ? Ledger.Get(Document.Series) :
            RemoteEvent.Length == 0 ? null : new JournalChoice(RemoteEvent, RemoteText);
        internal bool Known => Authority || RemoteKnown;

        public TouhouMissionJournal(Item item, ContentXElement element) : base(item, element)
        {
            name = "TouhouMissionJournal";
            CanBeSelected = true;
            Document = JournalDocument.Parse(element);
            Instances.Add(this);
        }

        internal bool CanRead(Character reader) =>
            reader != null && !reader.Removed && !reader.IsDead && !reader.IsUnconscious && reader.Stun <= 0 &&
            !item.Removed && reader.CanInteractWith(item) &&
            (reader.SelectedItem == item || reader.SelectedSecondaryItem == item || reader.HeldItems.Contains(item));

        internal bool TryAccept(Character reader, int index, string epoch)
        {
            if (!Authority || !CanRead(reader) || GameMain.GameSession?.IsRunning != true ||
                GameMain.GameSession.Campaign == null || index < 0 || index >= Document.Missions.Count) return false;
            var mission = Document.Missions[index];
            var manager = GameMain.GameSession.EventManager;
            var prefab = EventSet.GetEventPrefab(mission.Event.ToIdentifier());
            if (prefab == null || manager == null) { Failure = "任务事件未加载，未消耗本巡回名额。"; return false; }
            try
            {
                bool result = Ledger.Accept(epoch, Document.Series, mission, () =>
                {
                    var ev = prefab.CreateInstance(manager.RandomSeed);
                    if (ev == null) return false;
                    manager.QueuedEvents.Enqueue(ev);
                    return true;
                });
                Failure = result || Choice != null ? "" : "当前无法创建任务，未消耗本巡回名额。";
                if (result) PublishSeries(Document.Series);
                return result;
            }
            catch (Exception ex)
            {
                Failure = "任务创建失败，未消耗本巡回名额。";
                DebugConsole.AddWarning("[任务日志] " + ex.Message);
                return false;
            }
        }

        private static void PublishSeries(string series)
        {
#if SERVER
            foreach (var journal in Instances)
                if (!journal.item.Removed && journal.item.FullyInitialized && journal.Document.Series == series)
                {
                    try { journal.item.CreateServerEvent(journal); }
                    catch (Exception ex) { DebugConsole.AddWarning("[任务日志] 登记已生效，等待界面重新同步：" + ex.Message); }
                }
#endif
        }

        public override void OnItemLoaded()
        {
            base.OnItemLoaded();
#if SERVER
            CoroutineManager.Invoke(() =>
            {
                if (!item.Removed && item.FullyInitialized) item.CreateServerEvent(this);
            }, 0.1f);
#endif
        }

        internal void RefreshSnapshot()
        {
#if CLIENT
            if (Authority || Timing.TotalTime < nextQuery) return;
            nextQuery = Timing.TotalTime + 1;
            item.CreateClientEvent(this, new Request(255, ""));
#endif
        }

        internal bool Choose(int index)
        {
            if (!Known || Choice != null || !CanRead(Character.Controlled)) return false;
#if CLIENT
            if (!Authority)
            {
                item.CreateClientEvent(this, new Request((byte)index, RemoteEpoch));
                return true;
            }
#endif
            return TryAccept(Character.Controlled, index, Ledger.Epoch);
        }

        private sealed record Request(byte Index, string Epoch) : IEventData;
#if CLIENT
        public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
        {
            var request = extraData is Item.ComponentStateEventData state && state.ComponentData is Request data ?
                data : new Request(255, "");
            msg.WriteByte(request.Index);
            msg.WriteString(request.Epoch);
        }
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            string epoch = msg.ReadString(), selected = msg.ReadString(), text = msg.ReadString(), error = msg.ReadString();
            if (sendingTime < lastSnapshot) return;
            lastSnapshot = sendingTime;
            RemoteEpoch = epoch; RemoteEvent = selected; RemoteText = text; Failure = error; RemoteKnown = true;
        }
#endif
#if SERVER
        public void ServerEventRead(IReadMessage msg, Client sender)
        {
            byte index = msg.ReadByte();
            string epoch = msg.ReadString();
            if (sender?.Character == null || !CanRead(sender.Character) || !item.CanClientAccess(sender)) return;
            if (index != 255) TryAccept(sender.Character, index, epoch);
            item.CreateServerEvent(this);
        }
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            var choice = Ledger.Get(Document.Series);
            msg.WriteString(Ledger.Epoch);
            msg.WriteString(choice?.Event ?? "");
            msg.WriteString(choice?.Text ?? "");
            msg.WriteString(Failure);
        }
#endif
        internal static void BeginRound()
        {
            Ledger.BeginRound();
            foreach (var journal in Instances)
            {
                journal.RemoteKnown = false;
                journal.RemoteEpoch = journal.RemoteEvent = journal.RemoteText = journal.Failure = "";
                journal.nextQuery = 0;
                journal.lastSnapshot = float.NegativeInfinity;
            }
        }
        public override void RemoveComponentSpecific()
        {
            ReleaseView();
            Instances.Remove(this);
            base.RemoveComponentSpecific();
        }
        partial void ReleaseView();
        internal static void ReleaseViews()
        {
            foreach (var journal in Instances.ToArray()) journal.ReleaseView();
        }
    }

    public sealed class MissionJournalPlugin : IAssemblyPlugin
    {
        private readonly Harmony harmony = new("touhou.missionjournal." +
#if SERVER
            "server");
#else
            "client");
#endif
        private bool installed;
        public void Initialize() { }
        public void PreInitPatching() => Install();
        public void OnLoadCompleted() => Install();
        private void Install()
        {
            if (installed) return;
            harmony.PatchAll(typeof(JournalFactoryPatch));
            harmony.PatchAll(typeof(JournalRoundPatch));
            harmony.PatchAll(typeof(JournalRoundEndPatch));
            installed = true;
        }
        public void Dispose() { harmony.UnpatchSelf(); TouhouMissionJournal.ReleaseViews(); installed = false; }
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class JournalFactoryPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
#if SERVER
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) return true;
#else
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsClient) return true;
#endif
            if (!element.Name.ToString().Equals("TouhouMissionJournal", StringComparison.OrdinalIgnoreCase)) return true;
            __result = new TouhouMissionJournal(item, element);
            return false;
        }
    }
    [HarmonyPatch(typeof(EventManager), nameof(EventManager.StartRound))]
    internal static class JournalRoundPatch
    {
        private static void Postfix() => TouhouMissionJournal.BeginRound();
    }
    [HarmonyPatch(typeof(EventManager), nameof(EventManager.EndRound))]
    internal static class JournalRoundEndPatch
    {
        private static void Postfix() => TouhouMissionJournal.ReleaseViews();
    }
}
