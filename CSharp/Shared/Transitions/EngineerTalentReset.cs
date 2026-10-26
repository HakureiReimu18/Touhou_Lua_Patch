using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Barotrauma;

namespace Touhou.EngineerTransitions
{
    // 只撤旧职业的贡献；不重建保留天赋，所以装束的冷却、计数和实例也不会重置。
    internal sealed class EngineerTalentReset
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Character character;
        private readonly List<CharacterTalent> live;
        private readonly Dictionary<StatTypes, float> stats;
        private readonly Dictionary<TalentResistanceIdentifier, float> resistances;
        private readonly Dictionary<StatTypes, float> subtract = new();
        private readonly List<(StatTypes Type, SavedStatValue Value)> permanent = new();
        private readonly List<(Item Item, Dictionary<TalentStatIdentifier, float> Stats, TalentStatIdentifier Key)> itemStats = new();
        internal readonly HashSet<Identifier> Removed;
        internal bool HasChanges { get; }

        private static readonly HashSet<string> passiveOrEventAbilities = new(StringComparer.Ordinal)
        {
            "CharacterAbilityGiveResistance", "CharacterAbilityModifyResistance",
            "CharacterAbilityGivePermanentStat", "CharacterAbilityResetPermanentStat",
            "CharacterAbilityGiveItemStat", "CharacterAbilityGiveItemStatToTags",
            "CharacterAbilityModifyValue", "CharacterAbilityModifyAttackData", "CharacterAbilityModifyAffliction",
            "CharacterAbilityApplyStatusEffects", "CharacterAbilityApplyStatusEffectsToAllies",
            "CharacterAbilityApplyStatusEffectsToAttacker", "CharacterAbilityGiveExperience",
            "CharacterAbilityGiveMoney", "CharacterAbilityGiveItem", "CharacterAbilityUpgradeSubmarine",
            // 舰长这几类只停掉旧天赋，不追缴已发奖励或还原已转换的状态；重选后允许再次触发。
            "CharacterAbilityGiveTalentPointsToAllies", "CharacterAbilityGainSimultaneousSkill",
            "CharacterAbilityReplaceAffliction", "CharacterAbilityPutItem",
            // 安全官只停掉处理器，不追缴战利品、奖励或已经治疗的伤势。
            "CharacterAbilityByTheBook", "CharacterAbilityReduceAffliction",
            "CharacterAbilityRegenerateLoot", "CharacterAbilityTandemFire", "CharacterAbilityWarStories",
            // 已经完成的制作、战利品和战役升级不倒退；只停机修工旧天赋的处理器。
            "CharacterAbilitySetMetadataInt", "CharacterAbilitySpawnItemsToContainer", "CharacterAbilityRemoveRandomIngredient"
        };

        // 旧树是唯一边界，不能因隐藏标记或装备授予同 ID 而把职业天赋当成额外天赋。
        internal static Identifier[] ProfessionTalents(TalentTree source) => source.AllTalentIdentifiers.ToArray();

        internal EngineerTalentReset(Character character, IEnumerable<Identifier> requested)
        {
            this.character = character;
            live = Read<List<CharacterTalent>>(character, "characterTalents");
            Removed = requested.ToHashSet();
            stats = Read<Dictionary<StatTypes, float>>(character, "statValues");
            resistances = Read<Dictionary<TalentResistanceIdentifier, float>>(character, "abilityResistances");
            foreach (var talent in live.Where(talent => Removed.Contains(talent.Prefab.Identifier)))
                InspectAbilities(talent);
            InspectPermanentStats();
            foreach (var item in Item.ItemList)
            {
                if (item.Removed) continue;
                var values = Read<Dictionary<TalentStatIdentifier, float>>(item.StatManager, "talentStats");
                foreach (var key in values.Keys)
                {
                    if (key.Save || !Removed.Contains(key.TalentIdentifier)) continue;
                    bool mine = key.UniqueCharacterId.TryUnwrap(out uint id)
                        ? id == character.ID
                        : !Character.CharacterList.Any(other => other != character && !other.Removed && !other.IsDead &&
                            other.CharacterTalents.Any(talent => talent.Prefab.Identifier == key.TalentIdentifier));
                    if (mine) itemStats.Add((item, values, key));
                }
            }
            HasChanges = character.Info.UnlockedTalents.Overlaps(Removed) ||
                character.Info.ResettableExtraTalents.Overlaps(Removed) ||
                live.Any(talent => Removed.Contains(talent.Prefab.Identifier)) ||
                resistances.Keys.Any(key => Removed.Contains(key.TalentIdentifier)) ||
                permanent.Count > 0 || itemStats.Count > 0 || subtract.Count > 0;
        }

        private void InspectAbilities(CharacterTalent talent)
        {
            var groups = new List<(object Group, bool Activated)>();
            foreach (DictionaryEntry entry in Read<IDictionary>(talent, "characterAbilityGroupEffectDictionary"))
                groups.AddRange(((IEnumerable)entry.Value).Cast<object>().Select(group => (group, true)));
            groups.AddRange(Read<IEnumerable>(talent, "characterAbilityGroupIntervals").Cast<object>().Select(group => (group, false)));
            foreach (var entry in groups)
            {
                var group = entry.Group;
                foreach (object ability in Read<IEnumerable>(group, "characterAbilities").Cast<object>()
                    .Concat(Read<IEnumerable>(group, "fallbackAbilities").Cast<object>()))
                {
                    string type = ability.GetType().Name;
                    if (type == "CharacterAbilityGiveStat")
                    {
                        // 原版只初始化 effect 组，interval 里的 GiveStat 不会贡献初始属性。
                        if (!entry.Activated) continue;
                        if (Read<AbilityEffectType>(group, "AbilityEffectType") == AbilityEffectType.None &&
                            Read<IEnumerable>(group, "abilityConditions").Cast<object>().Any())
                            throw new InvalidOperationException("无法确认条件式 GiveStat 是否已初始化：" + talent.Prefab.Identifier);
                        Subtract(Read<StatTypes>(ability, "statType"), Read<float>(ability, "value"));
                    }
                    else if (type == "CharacterAbilityModifyStat")
                    {
                        if (Read<bool>(ability, "lastState"))
                            Subtract(Read<StatTypes>(ability, "statType"), Read<float>(ability, "value"));
                    }
                    else if (type == "CharacterAbilityModifyStatToSkill")
                        Subtract(Read<StatTypes>(ability, "statType"), Read<float>(ability, "lastValue"));
                    else if (ability is Touhou.StatScaling.CharacterAbilityModifyStatToStat scaling)
                        Subtract(scaling.StatType, scaling.LastValue);
                    else if (ability is Touhou.StatScaling.CharacterAbilityModifyStatToVitality vitalityScaling)
                        Subtract(vitalityScaling.StatType, vitalityScaling.LastValue);
                    else if (ability.GetType().Assembly != typeof(Character).Assembly || !passiveOrEventAbilities.Contains(type))
                        throw new InvalidOperationException("暂不支持定向撤销该职业能力，未洗点：" + type);
                }
            }
        }

        private void Subtract(StatTypes type, float value)
        {
            if (!float.IsFinite(value)) throw new InvalidOperationException("Non-finite talent contribution.");
            subtract[type] = subtract.TryGetValue(type, out float previous) ? previous + value : value;
        }

        private static HashSet<(StatTypes, Identifier)> OwnedStats(IEnumerable<Identifier> ids)
        {
            var keys = new HashSet<(StatTypes, Identifier)>();
            foreach (Identifier id in ids)
            {
                if (!TalentPrefab.TalentPrefabs.TryGet(id, out var prefab)) continue;
                foreach (var effect in prefab.ConfigElement.Element.Descendants().Where(e =>
                    e.Name.LocalName.Equals("characterabilitygivepermanentstat", StringComparison.OrdinalIgnoreCase)))
                {
                    string key = Attribute(effect, "statidentifier");
                    if (string.IsNullOrEmpty(key)) continue;
                    if (key.IndexOfAny(new[] { '[', '*', ']' }) >= 0 ||
                        (!string.IsNullOrEmpty(Attribute(effect, "placeholder")) &&
                         !Attribute(effect, "placeholder").Equals("none", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("永久记录含动态标识，无法安全区分归属：" + id);
                    string type = Attribute(effect, "stattype");
                    if (string.IsNullOrEmpty(type)) type = "None";
                    if (!Enum.TryParse(type, true, out StatTypes stat)) throw new InvalidOperationException("Unknown stat: " + type);
                    keys.Add((stat, key.ToIdentifier()));
                }
            }
            return keys;
        }

        private void InspectPermanentStats()
        {
            var owned = OwnedStats(Removed);
            // 树外计数不做格式验证、不重建；只在确实共用待清理的键时拒绝，免得误删。
            foreach (var id in character.Info.UnlockedTalents.Where(id => !Removed.Contains(id)))
            {
                if (!TalentPrefab.TalentPrefabs.TryGet(id, out var prefab)) continue;
                foreach (var effect in prefab.ConfigElement.Element.Descendants().Where(e =>
                    e.Name.LocalName.Equals("characterabilitygivepermanentstat", StringComparison.OrdinalIgnoreCase)))
                {
                    string key = Attribute(effect, "statidentifier").ToLowerInvariant();
                    if (string.IsNullOrEmpty(key)) continue;
                    string pattern = "^" + Regex.Escape(key).Replace(Regex.Escape("[placeholder]"), ".*")
                        .Replace("\\*", ".*") + "$";
                    string type = Attribute(effect, "stattype");
                    if (string.IsNullOrEmpty(type)) type = "None";
                    bool known = Enum.TryParse(type, true, out StatTypes stat);
                    if (owned.Any(pair => (!known || pair.Item1 == stat) &&
                        Regex.IsMatch(pair.Item2.Value, pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)))
                        throw new InvalidOperationException("旧职业和保留天赋共用永久记录，无法安全拆分，未洗点。");
                }
            }
            foreach (var pair in character.Info.SavedStatValues)
                foreach (var value in pair.Value)
                    if (owned.Contains((pair.Key, value.StatIdentifier))) permanent.Add((pair.Key, value));
        }

        internal void Commit()
        {
            // 预检只读。提交阶段只改已确认归属的条目，不调用 RefundTalents / ResetTalents。
            foreach (var pair in subtract)
                stats[pair.Key] = stats.TryGetValue(pair.Key, out float current) ? current - pair.Value : -pair.Value;
            foreach (var key in resistances.Keys.Where(key => Removed.Contains(key.TalentIdentifier)).ToArray())
                resistances.Remove(key);
            foreach (var row in permanent) character.Info.SavedStatValues[row.Type].Remove(row.Value);
            foreach (var row in itemStats) row.Stats.Remove(row.Key);
            live.RemoveAll(talent => Removed.Contains(talent.Prefab.Identifier));
            character.Info.UnlockedTalents.ExceptWith(Removed);
            character.Info.ResettableExtraTalents.ExceptWith(Removed);
        }

        internal void PublishItemStats()
        {
#if SERVER
            // 与原版永久属性事件保持同一条有序流，防止旧消息把已撤销的职业加成补回来。
            foreach (var type in permanent.Select(row => row.Type).Distinct())
            {
                try { GameMain.NetworkMember?.CreateEntityEvent(character, new Character.UpdatePermanentStatsEventData(type)); }
                catch (Exception exception) { EngineerTransitions.Log("永久属性同步待下次完整同步：" + exception.Message); }
            }
            foreach (var item in itemStats.Select(row => row.Item).Distinct())
            {
                try
                {
                    var values = Read<Dictionary<TalentStatIdentifier, float>>(item.StatManager, "talentStats");
                    GameMain.NetworkMember?.CreateEntityEvent(item, new Item.SetItemStatEventData(values));
                }
                catch (Exception exception) { EngineerTransitions.Log("物品属性同步待下次完整同步：" + exception.Message); }
            }
#endif
        }

        private static string Attribute(XElement element, string name)
            => element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        private static T Read<T>(object instance, string name)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, Fields | BindingFlags.DeclaredOnly);
                if (field != null) return (T)field.GetValue(instance);
            }
            throw new MissingFieldException(instance.GetType().FullName, name);
        }
    }
}
