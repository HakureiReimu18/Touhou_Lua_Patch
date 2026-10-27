using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.WeaponArchive
{
    // 来源计数只增不减，同步取各来源的较大值，不能把两个总数直接相加。
    internal sealed class ArchiveLedger
    {
        internal const int MaxSpecies = 128, MaxEntries = 8192;
        internal string Model { get; }
        internal Guid Source { get; }
        internal int Revision { get; private set; }
        internal int TotalsVersion { get; private set; }
        private readonly Dictionary<string, Dictionary<Guid, int>> records;
        private Dictionary<string, int> cachedTotals;
        private int cachedUniqueSpecies;
        private readonly object gate = new object();

        internal ArchiveLedger(string model, Guid? source = null)
        {
            Model = Key(model);
            if (Model == null) throw new ArgumentException("Invalid archive model.");
            Source = source ?? Guid.NewGuid();
            records = new Dictionary<string, Dictionary<Guid, int>>(StringComparer.Ordinal);
        }

        internal static string Key(string value)
        {
            value = value?.Trim().ToLowerInvariant();
            return string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(char.IsControl) ? null : value;
        }

        internal bool Record(string species)
        {
            species = Key(species);
            if (species == null) return false;
            lock (gate)
            {
                if (!records.TryGetValue(species, out var sources))
                {
                    if (records.Count >= MaxSpecies) return false;
                    records[species] = sources = new Dictionary<Guid, int>();
                    if (cachedTotals != null) cachedTotals[species] = 0;
                    TotalsVersion++;
                }
                if (!sources.ContainsKey(Source) && records.Sum(pair => pair.Value.Count) >= MaxEntries) return false;
                sources.TryGetValue(Source, out int count);
                if (count == int.MaxValue) return false;
                sources[Source] = count + 1;
                if (cachedTotals != null)
                {
                    if (cachedTotals[species] == 0) cachedUniqueSpecies++;
                    cachedTotals[species] = (int)Math.Min(int.MaxValue, (long)cachedTotals[species] + 1);
                }
                TotalsVersion++;
                Revision++;
                return true;
            }
        }

        internal Dictionary<string, int> Totals()
            => Totals(out _);

        internal Dictionary<string, int> Totals(out int version)
        {
            lock (gate)
            {
                EnsureTotals();
                version = TotalsVersion;
                // 给调用方独立副本，不能让显示层改到计数缓存。
                return new Dictionary<string, int>(cachedTotals, StringComparer.Ordinal);
            }
        }

        private void EnsureTotals()
        {
            if (cachedTotals == null)
            {
                cachedTotals = records.ToDictionary(pair => pair.Key,
                    pair => (int)Math.Min(int.MaxValue, pair.Value.Values.Sum(value => (long)value)), StringComparer.Ordinal);
                cachedUniqueSpecies = cachedTotals.Values.Count(value => value > 0);
            }
        }

        internal int UniqueSpeciesCount
        {
            get { lock (gate) { EnsureTotals(); return cachedUniqueSpecies; } }
        }

        internal int Count(string species)
        {
            species = Key(species);
            if (species == null) return 0;
            lock (gate)
            {
                EnsureTotals();
                return cachedTotals.TryGetValue(species, out int count) ? count : 0;
            }
        }

        private Dictionary<string, Dictionary<Guid, int>> Snapshot()
        {
            lock (gate) return records.ToDictionary(pair => pair.Key,
                pair => new Dictionary<Guid, int>(pair.Value), StringComparer.Ordinal);
        }

        internal static bool TryUnion(ArchiveLedger a, ArchiveLedger b,
            out Dictionary<string, Dictionary<Guid, int>> merged)
        {
            merged = null;
            if (a == null || b == null || a.Model != b.Model || a.Source == b.Source) return false;
            var result = a.Snapshot();
            foreach (var species in b.Snapshot())
            {
                if (!result.TryGetValue(species.Key, out var sources))
                    result[species.Key] = sources = new Dictionary<Guid, int>();
                foreach (var entry in species.Value)
                    sources[entry.Key] = sources.TryGetValue(entry.Key, out int old) ? Math.Max(old, entry.Value) : entry.Value;
            }
            if (result.Count > MaxSpecies || result.Sum(pair => pair.Value.Count) > MaxEntries) return false;
            merged = result;
            return true;
        }

        internal void Commit(Dictionary<string, Dictionary<Guid, int>> merged)
        {
            // 先把分配做完，提交时不改枪自己的来源编号。
            var copy = merged.ToDictionary(pair => pair.Key,
                pair => new Dictionary<Guid, int>(pair.Value), StringComparer.Ordinal);
            lock (gate)
            {
                records.Clear();
                foreach (var pair in copy) records.Add(pair.Key, pair.Value);
                cachedTotals = null;
                TotalsVersion++;
                Revision++;
            }
        }

        internal XElement Save()
        {
            var root = new XElement("ArchiveData", new XAttribute("version", 1),
                new XAttribute("model", Model), new XAttribute("source", Source.ToString("N")));
            foreach (var species in Snapshot().OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var row = new XElement("Species", new XAttribute("id", species.Key));
                foreach (var entry in species.Value.OrderBy(pair => pair.Key))
                    row.Add(new XElement("Source", new XAttribute("id", entry.Key.ToString("N")),
                        new XAttribute("count", entry.Value)));
                root.Add(row);
            }
            return root;
        }

        internal static bool TryLoad(XElement xml, string model, out ArchiveLedger ledger)
        {
            ledger = null;
            if (xml == null || (string)xml.Attribute("version") != "1" ||
                Key((string)xml.Attribute("model")) != Key(model) ||
                !Guid.TryParseExact((string)xml.Attribute("source"), "N", out Guid source) || source == Guid.Empty) return false;
            var candidate = new ArchiveLedger(model, source);
            int entries = 0;
            foreach (var row in xml.Elements())
            {
                string species = Key((string)row.Attribute("id"));
                if (row.Name != "Species" || species == null || candidate.records.ContainsKey(species) ||
                    candidate.records.Count >= MaxSpecies) return false;
                var sources = new Dictionary<Guid, int>();
                foreach (var entry in row.Elements())
                {
                    if (entry.Name != "Source" || ++entries > MaxEntries ||
                        !Guid.TryParseExact((string)entry.Attribute("id"), "N", out Guid id) || id == Guid.Empty ||
                        !int.TryParse((string)entry.Attribute("count"), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                        count <= 0 || !sources.TryAdd(id, count)) return false;
                }
                candidate.records.Add(species, sources);
            }
            ledger = candidate;
            return true;
        }
    }

    internal enum ArchiveBonusKind { Damage, Affliction, Sedation, StunDuration }

    internal sealed class ArchiveCondition
    {
        internal string Species;
        internal int MinSpeciesDefeats, MinUniqueSpecies;
        internal bool Matches(ArchiveLedger ledger, string targetSpecies)
            => (Species == null || Species == targetSpecies) &&
                (MinSpeciesDefeats == 0 || ledger.Count(targetSpecies) >= MinSpeciesDefeats) &&
                (MinUniqueSpecies == 0 || ledger.UniqueSpeciesCount >= MinUniqueSpecies);
    }

    internal sealed class ArchiveBonus
    {
        internal ArchiveBonusKind Kind;
        internal string Identifier;
        internal ArchiveCondition Condition;
        internal float FixedMultiplier = 1f;
        internal float PerDefeat, Maximum;
        internal float Multiplier(int count) => FixedMultiplier * (1f + Math.Min(Maximum, count * PerDefeat));
    }

    internal sealed class ArchiveAttackAffliction
    {
        internal string Identifier;
        internal ArchiveCondition Condition;
        internal float Strength, Probability;
        internal bool IgnoreArmor, Stack, HardControl, HostileOnly;
    }
}
