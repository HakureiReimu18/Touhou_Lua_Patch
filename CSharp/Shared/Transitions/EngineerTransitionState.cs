using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.EngineerTransitions
{
    // 这部分不碰游戏对象，存读档和重复结算可以离线跑真代码。
    internal sealed class EngineerTransitionRecord
    {
        public string Player = "";
        public string Name = "";
        public string Id = "";
        public string Source = "";
        public string Target = "";
        public string OriginRound = "";
        public bool DeathObserved;
        public string DeathRound = "";
        public long DeathSequence;
        public bool Completed;
        public string AppliedCharacter = "";
        public int BonusFloor = -1;
        public int Policy;
        public int ExperienceBefore = -1;
        public int ExperienceAfter = -1;
        public string PlannedCharacter = "";
        public string[] RemovedTalents = Array.Empty<string>();

        public bool CanApply(string characterKey, string round, long birthSequence)
        {
            if (Completed) return false;
            // 同一具身体重连、切换控制都不算下一次生命。
            if (Source == characterKey && OriginRound != round) return true;
            return DeathObserved && (DeathRound != round || birthSequence > DeathSequence);
        }

        public XElement Save() => new XElement("Transition",
            new XAttribute("player", Player), new XAttribute("name", Name),
            new XAttribute("id", Id), new XAttribute("source", Source),
            new XAttribute("target", Target), new XAttribute("round", OriginRound),
            new XAttribute("death", DeathObserved), new XAttribute("deathround", DeathRound),
            new XAttribute("deathsequence", DeathSequence), new XAttribute("completed", Completed),
            new XAttribute("applied", AppliedCharacter), new XAttribute("bonusfloor", BonusFloor),
            new XAttribute("policy", Policy), new XAttribute("experiencebefore", ExperienceBefore),
            new XAttribute("experienceafter", ExperienceAfter), new XAttribute("plannedcharacter", PlannedCharacter),
            new XAttribute("removed", string.Join(",", RemovedTalents)));

        public static EngineerTransitionRecord Load(XElement element)
        {
            string player = (string)element.Attribute("player") ?? "";
            string id = (string)element.Attribute("id") ?? "";
            string source = (string)element.Attribute("source") ?? "";
            string target = (string)element.Attribute("target") ?? "";
            string round = (string)element.Attribute("round") ?? "";
            if (string.IsNullOrWhiteSpace(player) || !Guid.TryParseExact(id, "N", out _) ||
                !Guid.TryParseExact(source, "N", out _) ||
                !EngineerTransitionRules.IsTarget(target) || !Guid.TryParseExact(round, "N", out _))
                throw new FormatException("Invalid engineer transition identity.");

            var record = new EngineerTransitionRecord
            {
                Player = player, Name = (string)element.Attribute("name") ?? "",
                Id = id, Source = source, Target = target, OriginRound = round,
                DeathObserved = (bool?)element.Attribute("death") ?? false,
                DeathRound = (string)element.Attribute("deathround") ?? "",
                DeathSequence = (long?)element.Attribute("deathsequence") ?? 0,
                Completed = (bool?)element.Attribute("completed") ?? false,
                AppliedCharacter = (string)element.Attribute("applied") ?? "",
                BonusFloor = (int?)element.Attribute("bonusfloor") ?? -1,
                Policy = (int?)element.Attribute("policy") ?? 0,
                ExperienceBefore = (int?)element.Attribute("experiencebefore") ?? -1,
                ExperienceAfter = (int?)element.Attribute("experienceafter") ?? -1,
                PlannedCharacter = (string)element.Attribute("plannedcharacter") ?? "",
                RemovedTalents = ((string)element.Attribute("removed") ?? "")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            };
            if (record.BonusFloor < -1 || record.BonusFloor > 100 ||
                (record.Completed && (!Guid.TryParseExact(record.AppliedCharacter, "N", out _) ||
                                      record.BonusFloor < 0)))
                throw new FormatException("Invalid engineer transition receipt.");
            if (record.Policy != 0 && record.Policy != EngineerTransitionRules.RespecPolicy)
                throw new FormatException("Unsupported engineer transition policy.");
            if (record.Policy == EngineerTransitionRules.RespecPolicy &&
                (record.ExperienceBefore < -1 || record.ExperienceAfter < -1 ||
                 (record.ExperienceBefore >= 0 &&
                  (record.ExperienceAfter != EngineerTransitionRules.ExperienceAfterLoss(record.ExperienceBefore) ||
                   !Guid.TryParseExact(record.PlannedCharacter, "N", out _))) ||
                 (record.Completed && record.ExperienceBefore < 0)))
                throw new FormatException("Invalid engineer experience receipt.");
            return record;
        }
    }

    internal sealed class EngineerTransitionLedger
    {
        public readonly Dictionary<string, EngineerTransitionRecord> Records =
            new Dictionary<string, EngineerTransitionRecord>(StringComparer.Ordinal);

        public string Save() => new XElement("EngineerTransitions", new XAttribute("version", 1),
            Records.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value.Save())).ToString(SaveOptions.DisableFormatting);

        public static EngineerTransitionLedger Load(string text)
        {
            var ledger = new EngineerTransitionLedger();
            if (string.IsNullOrEmpty(text)) return ledger;
            XElement root = XElement.Parse(text);
            if (root.Name != "EngineerTransitions" || (int?)root.Attribute("version") != 1)
                throw new FormatException("Unsupported engineer transition save version.");
            foreach (XElement element in root.Elements("Transition"))
            {
                var record = EngineerTransitionRecord.Load(element);
                if (!ledger.Records.TryAdd(record.Player, record))
                    throw new FormatException("Duplicate engineer transition player.");
            }
            return ledger;
        }
    }

    internal static class EngineerTransitionRules
    {
        public const string Arcane = "engineer_arcane";
        public const string Mystic = "engineer_mystic";
        public const string ArcaneBook = "Touhou_Engineer_Arcane_Book";
        public const string MysticBook = "Touhou_Engineer_Mystic_Book";
        public const string SaveKey = "touhou.engineertransitions.v1";
        public const int RespecPolicy = 2;

        public static bool IsTarget(string id) => TransitionProfiles.IsTarget(id);

        public static string TargetOfBook(string id)
        {
            var profile = TransitionProfiles.ForBook(id);
            return profile?.TargetOfBook(id);
        }

        // 直接结算总经验，不走经验获取倍率，也不把旧天赋再算一份奖励。
        public static int ExperienceAfterLoss(int experience)
            => (int)(Math.Max(0, (long)experience) * 3 / 5);
    }
}
