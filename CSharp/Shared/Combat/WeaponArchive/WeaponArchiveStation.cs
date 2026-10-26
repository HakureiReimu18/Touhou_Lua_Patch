using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using HarmonyLib;

namespace Touhou.WeaponArchive
{
    internal enum ArchiveStationStatus : byte { NeedTwo, DifferentModel, InvalidData, Preview, Synced, SingleA, SingleB }
    internal sealed class ArchiveRow
    {
        internal string Species;
        internal int First, Second, Combined;
    }

    public sealed class TouhouArchiveStation : ItemComponent, IServerSerializable
    {
        internal ArchiveStationStatus Status { get; private set; } = ArchiveStationStatus.NeedTwo;
        internal List<ArchiveRow> Rows { get; private set; } = new List<ArchiveRow>();
        private double nextRequest = double.NegativeInfinity;
        private float timer;
        private Item lastFirst, lastSecond;
        private string lastSignature;

        public TouhouArchiveStation(Item item, ContentXElement element) : base(item, element)
        {
            name = "TouhouArchiveStation";
            IsActive = true;
        }
        public override bool UpdateWhenInactive => true;

        private bool Inputs(out TouhouWeaponArchive a, out TouhouWeaponArchive b)
        {
            var inventory = Item.OwnInventory;
            a = inventory?.GetItemAt(0)?.GetComponent<TouhouWeaponArchive>();
            b = inventory?.GetItemAt(1)?.GetComponent<TouhouWeaponArchive>();
            if (a?.Item.Removed == true) a = null;
            if (b?.Item.Removed == true) b = null;
            return a != null && b != null && a.Item != b.Item;
        }

        private bool Build(out TouhouWeaponArchive a, out TouhouWeaponArchive b,
            out Dictionary<string, Dictionary<Guid, int>> union)
        {
            Rows = new List<ArchiveRow>();
            union = null;
            if (!Inputs(out a, out b))
            {
                var single = a ?? b;
                if (single == null) { Status = ArchiveStationStatus.NeedTwo; return false; }
                if (!single.DataValid) { Status = ArchiveStationStatus.InvalidData; return false; }
                foreach (var pair in single.Ledger.Totals().OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    Rows.Add(new ArchiveRow { Species = pair.Key, First = a != null ? pair.Value : 0,
                        Second = a == null ? pair.Value : 0, Combined = pair.Value });
                Status = a != null ? ArchiveStationStatus.SingleA : ArchiveStationStatus.SingleB;
                // 单件也能查，但不生成合并数据，点同步不会改写枪上的档案。
                return false;
            }
            if (a.Item.Prefab.Identifier != b.Item.Prefab.Identifier)
            { Status = ArchiveStationStatus.DifferentModel; return false; }
            if (!a.DataValid || !b.DataValid || !ArchiveLedger.TryUnion(a.Ledger, b.Ledger, out union))
            { Status = ArchiveStationStatus.InvalidData; return false; }
            var first = a.Ledger.Totals();
            var second = b.Ledger.Totals();
            foreach (var pair in union.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Rows.Add(new ArchiveRow { Species = pair.Key,
                    First = first.TryGetValue(pair.Key, out int x) ? x : 0,
                    Second = second.TryGetValue(pair.Key, out int y) ? y : 0,
                    Combined = (int)Math.Min(int.MaxValue, pair.Value.Values.Sum(value => (long)value)) });
            Status = ArchiveStationStatus.Preview;
            return true;
        }

        internal void Request(Character user, bool sync, Client sender = null)
        {
            if (!ArchiveRuntime.Authority || Item.Removed || Item.Condition <= 0f || user == null ||
                user.IsDead || user.IsUnconscious || user.Stun > 0f || user.SelectedItem != Item ||
                Timing.TotalTime < nextRequest) return;
#if SERVER
            if (sender == null || sender.Character != user || !Item.CanClientAccess(sender)) return;
#endif
            nextRequest = Timing.TotalTime + 0.4;
            if (Build(out var a, out var b, out var union) && sync)
            {
                // 不缓存上次预览：提交前再核对槽位、类型和数据，避免拿走枪后仍被旧请求修改。
                if (Inputs(out var currentA, out var currentB) && currentA == a && currentB == b &&
                    a.Item.Prefab.Identifier == b.Item.Prefab.Identifier)
                {
                    var beforeA = a.Ledger.Save();
                    var beforeB = b.Ledger.Save();
                    try
                    {
                        a.Ledger.Commit(union);
                        b.Ledger.Commit(union);
                    }
                    catch
                    {
                        ArchiveLedger.TryLoad(beforeA, a.Ledger.Model, out var originalA);
                        ArchiveLedger.TryLoad(beforeB, b.Ledger.Model, out var originalB);
                        a.RestoreLedger(originalA);
                        b.RestoreLedger(originalB);
                        throw;
                    }
                    a.Changed();
                    b.Changed();
                    Status = ArchiveStationStatus.Synced;
                }
            }
            lastFirst = Item.OwnInventory?.GetItemAt(0);
            lastSecond = Item.OwnInventory?.GetItemAt(1);
            lastSignature = Signature(lastFirst, lastSecond);
            Send();
        }

        private static string Signature(Item first, Item second)
        {
            var a = first?.GetComponent<TouhouWeaponArchive>();
            var b = second?.GetComponent<TouhouWeaponArchive>();
            return a?.Ledger.Source.ToString("N") + ":" + a?.Ledger.Revision + ":" + a?.DataValid + "|" +
                b?.Ledger.Source.ToString("N") + ":" + b?.Ledger.Revision + ":" + b?.DataValid;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (!ArchiveRuntime.Authority || Item.Removed) return;
            timer += deltaTime;
            if (timer < 0.5f) return;
            timer = 0f;
            var first = Item.OwnInventory?.GetItemAt(0);
            var second = Item.OwnInventory?.GetItemAt(1);
            string signature = Signature(first, second);
            if (first == lastFirst && second == lastSecond && signature == lastSignature) return;
            lastFirst = first;
            lastSecond = second;
            lastSignature = signature;
            Build(out _, out _, out _);
            Send();
        }

        private void Send()
        {
#if SERVER
            Item.CreateServerEvent(this);
#endif
        }

#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            msg.WriteByte((byte)Status);
            msg.WriteUInt16((ushort)Rows.Count);
            foreach (var row in Rows)
            {
                msg.WriteString(row.Species);
                msg.WriteInt32(row.First);
                msg.WriteInt32(row.Second);
                msg.WriteInt32(row.Combined);
            }
        }
#else
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            var status = (ArchiveStationStatus)msg.ReadByte();
            int count = msg.ReadUInt16();
            if (!Enum.IsDefined(typeof(ArchiveStationStatus), status) || count > ArchiveLedger.MaxSpecies)
                throw new InvalidOperationException("Invalid archive station snapshot.");
            var rows = new List<ArchiveRow>();
            for (int i = 0; i < count; i++)
            {
                string species = ArchiveLedger.Key(msg.ReadString());
                int first = msg.ReadInt32(), second = msg.ReadInt32(), combined = msg.ReadInt32();
                if (species == null || first < 0 || second < 0 || combined < Math.Max(first, second))
                    throw new InvalidOperationException("Invalid archive station row.");
                rows.Add(new ArchiveRow { Species = species, First = first, Second = second, Combined = combined });
            }
            Status = status;
            Rows = rows;
        }
#endif
    }

    [HarmonyPatch(typeof(CustomInterface), "ButtonClicked")]
    internal static class ArchiveStationButtonPatch
    {
        private static bool Prefix(CustomInterface __instance, CustomInterface.CustomInterfaceElement __0)
        {
            if (!ArchiveRuntime.OwnContext) return true;
            var station = __instance.Item.GetComponent<TouhouArchiveStation>();
            if (station == null) return true;
            string label = __0?.Label;
            if (label != "touhou.archive.preview" && label != "touhou.archive.sync") return false;
#if SERVER
            station.Request(ArchiveStationRequestPatch.Sender?.Character, label == "touhou.archive.sync",
                ArchiveStationRequestPatch.Sender);
#else
            if (ArchiveRuntime.Authority) station.Request(Character.Controlled, label == "touhou.archive.sync");
#endif
            return false;
        }
    }

#if SERVER
    [HarmonyPatch(typeof(CustomInterface), nameof(CustomInterface.ServerEventRead))]
    internal static class ArchiveStationRequestPatch
    {
        [ThreadStatic] internal static Client Sender;
        private static void Prefix(Client __1, out Client __state) { __state = Sender; Sender = __1; }
        private static Exception Finalizer(Client __state, Exception __exception) { Sender = __state; return __exception; }
    }
#endif
}
