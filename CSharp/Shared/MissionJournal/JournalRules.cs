using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.MissionJournal
{
    internal sealed class JournalMission
    {
        internal string Event = "", Text = "";
    }

    internal sealed class JournalDocument
    {
        internal string Series = "", SeriesName = "", Title = "";
        internal readonly List<JournalMission> Missions = new();
        internal static JournalDocument Parse(XElement root)
        {
            string Read(XElement e, string key) => ((string)e.Attribute(key) ?? "").Trim();
            var result = new JournalDocument
            {
                Series = Read(root, "series").ToLowerInvariant(),
                SeriesName = Read(root, "seriesname"), Title = Read(root, "title")
            };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in root.Elements("Mission").Take(64))
            {
                string id = Read(node, "event"), text = Read(node, "text");
                if (id.Length == 0 || text.Length == 0 || !seen.Add(id)) continue;
                result.Missions.Add(new JournalMission { Event = id, Text = text });
            }
            return result;
        }
    }

    internal sealed class JournalChoice
    {
        internal readonly string Event, Text;
        internal JournalChoice(string id, string text) { Event = id; Text = text; }
    }

    internal sealed class JournalLedger
    {
        private readonly Dictionary<string, JournalChoice> choices = new(StringComparer.OrdinalIgnoreCase);
        internal string Epoch { get; private set; } = Guid.NewGuid().ToString("N");
        internal JournalChoice Get(string series) => choices.TryGetValue(series, out var choice) ? choice : null;
        internal void BeginRound() { choices.Clear(); Epoch = Guid.NewGuid().ToString("N"); }
        internal bool Accept(string epoch, string series, JournalMission mission, Func<bool> enqueue)
        {
            if (epoch != Epoch || string.IsNullOrWhiteSpace(series) || mission == null || Get(series) != null)
                return false;
            // 先占系列名额，两个玩家同时点也只有一个事件能进队列。
            choices.Add(series, new JournalChoice(mission.Event, mission.Text));
            bool accepted = false;
            try { return accepted = enqueue(); }
            finally { if (!accepted) choices.Remove(series); }
        }
    }
}
