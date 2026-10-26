using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;

namespace Touhou.WeaponArchive
{
    public sealed class TouhouWeaponArchive : ItemComponent, IServerSerializable
    {
        internal ArchiveLedger Ledger { get; private set; }
        internal readonly List<ArchiveBonus> Bonuses = new List<ArchiveBonus>();
        internal readonly bool RecordKills, RecordSedation, HostileOnly;
        internal bool DataValid { get; private set; } = true;
        private XElement quarantined;
        private float elapsed;
        private bool dirty = true;
        internal Dictionary<string, int> VisibleTotals = new Dictionary<string, int>();
        private ArchiveLedger snapshotLedger;
        private int snapshotVersion;
        private KeyValuePair<string, int>[] orderedTotals = Array.Empty<KeyValuePair<string, int>>();

        public TouhouWeaponArchive(Item item, ContentXElement element) : base(item, element)
        {
            name = "TouhouWeaponArchive";
            Ledger = new ArchiveLedger(item.Prefab.Identifier.Value);
            RecordKills = element.GetAttributeBool("recordkills", true);
            RecordSedation = element.GetAttributeBool("recordsedation", true);
            HostileOnly = element.GetAttributeBool("hostileonly", true);
            foreach (var child in element.Elements())
            {
                if (!string.Equals(child.Name.ToString(), "Bonus", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Enum.TryParse(child.GetAttributeString("type", ""), true, out ArchiveBonusKind kind)) continue;
                float per = child.GetAttributeFloat("perdefeat", 0f), maximum = child.GetAttributeFloat("maxbonus", 0f);
                if (!float.IsFinite(per) || !float.IsFinite(maximum) || per < 0f || maximum < 0f) continue;
                Bonuses.Add(new ArchiveBonus { Kind = kind, PerDefeat = Math.Min(per, 10f), Maximum = Math.Min(maximum, 10f),
                    Identifier = ArchiveLedger.Key(child.GetAttributeString("identifier", "")),
                    Species = ArchiveLedger.Key(child.GetAttributeString("species", "")) });
            }
            IsActive = true;
        }
        public override bool UpdateWhenInactive => true;

        public override XElement Save(XElement parentElement)
        {
            var result = base.Save(parentElement);
            result.Add(quarantined != null ? new XElement(quarantined) : Ledger.Save());
            return result;
        }

        public override void Load(ContentXElement element, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            base.Load(element, usePrefabValues, idRemap, isItemSwap);
            var data = element.GetChildElement("ArchiveData");
            if (data == null) return; // 老存档没有档案，从空白开始，不补算历史击败。
            XElement xml = data;
            if (ArchiveLedger.TryLoad(xml, Item.Prefab.Identifier.Value, out var ledger))
            {
                Ledger = ledger;
                DataValid = true;
                quarantined = null;
            }
            else
            {
                DataValid = false;
                quarantined = new XElement(xml); // 异常数据原样留存，不能静默清空再覆盖存档。
            }
            Changed();
        }

        internal void Changed()
        {
            dirty = true;
            if (ArchiveRuntime.Authority) RefreshSnapshot();
        }

        private void RefreshSnapshot()
        {
            var ledger = Ledger;
            if (snapshotLedger == ledger && snapshotVersion == ledger.TotalsVersion) return;
            VisibleTotals = ledger.Totals(out int version);
            orderedTotals = VisibleTotals.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            snapshotLedger = ledger;
            snapshotVersion = version;
        }

        internal void RestoreLedger(ArchiveLedger ledger)
        {
            Ledger = ledger ?? throw new InvalidOperationException("Archive rollback data unavailable.");
            Changed();
        }

        internal float Multiplier(string species, ArchiveBonusKind kind, string identifier = null)
        {
            if (!DataValid || Bonuses.Count == 0) return 1f;
            string speciesKey = ArchiveLedger.Key(species), effectKey = ArchiveLedger.Key(identifier);
            int count = -1;
            float result = 1f;
            foreach (var bonus in Bonuses)
                if (bonus.Kind == kind && (bonus.Species == null || bonus.Species == speciesKey) &&
                    (bonus.Identifier == null || bonus.Identifier == effectKey))
                {
                    if (count < 0) count = Ledger.Count(speciesKey);
                    result *= bonus.Multiplier(count);
                }
            return Math.Min(result, 11f);
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (!ArchiveRuntime.Authority || Item.Removed) return;
            elapsed += deltaTime;
            bool held = Item.ParentInventory?.Owner is Character owner && owner.HeldItems.Contains(Item);
            if ((dirty && elapsed >= 0.5f) || (held && elapsed >= 3f))
            {
                RefreshSnapshot();
#if SERVER
                Item.CreateServerEvent(this);
#endif
                dirty = false;
                elapsed = 0f;
            }
        }
        public override void UpdateBroken(float deltaTime, Camera cam) => Update(deltaTime, cam);

#if SERVER
        public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
        {
            msg.WriteBoolean(DataValid);
            RefreshSnapshot();
            msg.WriteUInt16((ushort)orderedTotals.Length);
            foreach (var pair in orderedTotals)
            {
                msg.WriteString(pair.Key);
                msg.WriteInt32(pair.Value);
            }
        }
#else
        public void ClientEventRead(IReadMessage msg, float sendingTime)
        {
            bool valid = msg.ReadBoolean();
            int count = msg.ReadUInt16();
            if (count > ArchiveLedger.MaxSpecies) throw new InvalidOperationException("Archive snapshot exceeds species limit.");
            var totals = new Dictionary<string, int>();
            for (int i = 0; i < count; i++)
            {
                string key = ArchiveLedger.Key(msg.ReadString());
                int value = msg.ReadInt32();
                if (key == null || value < 0 || !totals.TryAdd(key, value))
                    throw new InvalidOperationException("Invalid archive snapshot.");
            }
            DataValid = valid;
            VisibleTotals = totals;
        }
#endif
    }
}
