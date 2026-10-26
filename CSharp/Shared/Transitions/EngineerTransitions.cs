using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.EngineerTransitions
{
    public sealed class EngineerTransitionsPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool initialized;

        public void Initialize() => harmony = new Harmony("touhou.engineertransitions." + EngineerTransitions.Side);
        public void PreInitPatching() { }

        public void OnLoadCompleted()
        {
            if (initialized) return;
            initialized = true;
            harmony.UnpatchSelf();
            bool ready = true;
            ready &= Patch(typeof(CharacterInfo).GetMethod("Save"), nameof(EngineerTransitions.SaveCharacter), false);
            ready &= Patch(typeof(CharacterInfo).GetConstructor(new[] { typeof(ContentXElement), typeof(Identifier) }),
                nameof(EngineerTransitions.LoadCharacter), false);
            ready &= Patch(typeof(Character).GetMethods().Single(method => method.Name == "Create" &&
                method.GetParameters().FirstOrDefault()?.ParameterType == typeof(Identifier)),
                nameof(EngineerTransitions.CharacterCreated), false);
            ready &= Patch(typeof(Character).GetMethod("Kill"), nameof(EngineerTransitions.CharacterKilled), false);
            ready &= Patch(typeof(Item).GetMethod("Use"), nameof(EngineerTransitions.UseBook), true);
            ready &= Patch(typeof(Item).GetMethod("SecondaryUse"), nameof(EngineerTransitions.UseBook), true);
            Patch(typeof(Character).GetMethod("HasJob", new[] { typeof(string) }),
                nameof(EngineerTransitions.HasEngineerJob), false);
            Patch(typeof(Character).GetMethod("HasJob", new[] { typeof(Identifier) }),
                nameof(EngineerTransitions.HasEngineerJobIdentifier), false);
            Patch(typeof(PropertyConditional).GetMethod("PropertyMatchesRequirement",
                BindingFlags.Instance | BindingFlags.NonPublic),
                nameof(EngineerTransitions.EngineerCondition), true);
            Patch(typeof(WayPoint).GetMethod("SelectCrewSpawnPoints"),
                nameof(EngineerTransitions.EngineerSpawnPoints), false);
#if SERVER
            Patch(typeof(KarmaManager).GetMethod("AdjustKarma",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                nameof(EngineerTransitions.EngineerKarma), true);
#endif
            EngineerTransitions.Ready = ready && EngineerTransitionNet.Register();
            EngineerTransitions.AdoptRound();
            foreach (var character in Character.CharacterList) EngineerTransitions.Birth(character);
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.EngineerTransitions.Start." + EngineerTransitions.Side, EngineerTransitions.RoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.EngineerTransitions.End." + EngineerTransitions.Side, EngineerTransitions.RoundEnd);
            LuaCsSetup.Instance.Hook.Add("think", "Touhou.EngineerTransitions.Tick." + EngineerTransitions.Side, EngineerTransitions.Tick);
            EngineerTransitions.RegisterCommand();
            EngineerTransitions.Log(EngineerTransitions.Ready ? "转职插件已加载：使用自定义树，总经验损失 40%，保留额外天赋" : "必要接口缺失，异传书已禁用");
        }

        private bool Patch(MethodBase target, string method, bool prefix)
        {
            try
            {
                if (target == null) throw new MissingMethodException(method);
                var patch = new HarmonyMethod(typeof(EngineerTransitions), method);
                harmony.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
                return true;
            }
            catch (Exception exception)
            {
                EngineerTransitions.Log("补丁未安装：" + method + " / " + exception.Message);
                return false;
            }
        }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.EngineerTransitions.Start." + EngineerTransitions.Side);
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.EngineerTransitions.End." + EngineerTransitions.Side);
            LuaCsSetup.Instance.Hook.Remove("think", "Touhou.EngineerTransitions.Tick." + EngineerTransitions.Side);
            harmony?.UnpatchSelf();
            EngineerTransitions.Clear();
            initialized = false;
        }
    }

    internal static class EngineerTransitions
    {
        internal sealed class CharacterReceipt
        {
            public string Key = Guid.NewGuid().ToString("N");
            public string Applied = "";
            public string Target = "";
            public int BonusFloor = -1;
            public bool RadiationSeen;
            public int Policy;
            public int ExperienceBefore = -1;
            public int ExperienceAfter = -1;
            public string[] RemovedTalents = Array.Empty<string>();
        }

        internal sealed class BodyStamp { public long Birth; }

        internal static bool Ready;
        internal static bool Active;
#if SERVER
        internal const string Side = "server";
#else
        internal const string Side = "client";
#endif
        internal static string Round = Guid.NewGuid().ToString("N");
        private const string RoundKey = "touhou.engineertransitions.round";
        private const string CharacterKey = "touhouengineercharacter";
        private static readonly ConditionalWeakTable<CharacterInfo, CharacterReceipt> receipts = new();
        private static readonly ConditionalWeakTable<Character, BodyStamp> bodies = new();
        private static readonly Dictionary<string, double> notices = new();
        private sealed class LedgerState
        {
            public CampaignMetadata Metadata;
            public string Serialized;
            public EngineerTransitionLedger Ledger;
        }
        private static readonly Dictionary<string, LedgerState> ledgerStates = new(StringComparer.Ordinal);
        private static double nextTick;
        private static long sequence;
        private static string lastBadPayload;
        private static readonly FieldInfo skillPrice = typeof(Skill).GetField(nameof(Skill.PriceMultiplier));
        private static DebugConsole.Command registeredCommand;

        internal static bool OwnContext =>
#if SERVER
            GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static bool ProfileReady(TransitionProfile profile) =>
            TalentTree.JobTalentTrees.TryGet(profile.ArcaneJob.ToIdentifier(), out var arcane) && arcane.TalentSubTrees.Length > 0 &&
            TalentTree.JobTalentTrees.TryGet(profile.MysticJob.ToIdentifier(), out var mystic) && mystic.TalentSubTrees.Length > 0;

        internal static bool TreesReady => TransitionProfiles.All.Any(ProfileReady);

        internal static CharacterReceipt Receipt(CharacterInfo info) => receipts.GetOrCreateValue(info);
        internal static long Birth(Character character) => bodies.GetValue(character,
            _ => new BodyStamp { Birth = Interlocked.Increment(ref sequence) }).Birth;
        internal static bool IsTransitioned(Character character)
            => character?.Info?.Job != null &&
               TransitionProfiles.IsTarget(character.Info.Job.Prefab.Identifier.Value);

        internal static TransitionProfile TransitionedProfile(Character character)
            => character?.Info?.Job == null
                ? null
                : TransitionProfiles.ForTarget(character.Info.Job.Prefab.Identifier.Value);

        private static bool IsBaseJob(Character character, TransitionProfile profile)
            => profile != null && character?.Info?.Job?.Prefab.Identifier == profile.BaseJob;

        internal static void Log(string text) => DebugConsole.NewMessage("[Touhou.JobTransition] " + text, Color.LightSkyBlue);

        internal static void AdoptRound()
        {
            Active = Level.Loaded != null;
            if (!Authority) return;
            var data = GameMain.GameSession?.Campaign?.CampaignMetadata;
            string token = data?.GetString(RoundKey.ToIdentifier(), "");
            if (Guid.TryParseExact(token, "N", out _)) Round = token;
            else data?.SetValue(RoundKey.ToIdentifier(), Round);
        }

        private static EngineerTransitionLedger Ledger(TransitionProfile profile)
        {
            var current = GameMain.GameSession?.Campaign?.CampaignMetadata;
            if (current == null) return null;
            string text = current.GetString(profile.SaveKey.ToIdentifier(), "");
            if (ledgerStates.TryGetValue(profile.Key, out var state) &&
                ReferenceEquals(state.Metadata, current) && state.Serialized == text && state.Ledger != null)
                return state.Ledger;
            try
            {
                var loaded = EngineerTransitionLedger.Load(text);
                ledgerStates[profile.Key] = new LedgerState
                {
                    Metadata = current,
                    Serialized = text,
                    Ledger = loaded
                };
                return loaded;
            }
            catch (Exception exception)
            {
                // 坏数据不能拿空表覆盖，否则已完成记录也会被洗掉。
                if (lastBadPayload != profile.Key + ":" + text)
                {
                    lastBadPayload = profile.Key + ":" + text;
                    Log(profile.Name + "转职存档损坏，暂停结算并保留原数据：" + exception.Message);
                }
                return null;
            }
        }

        private static void Persist(TransitionProfile profile)
        {
            if (!ledgerStates.TryGetValue(profile.Key, out var state) || state.Ledger == null) return;
            string text = state.Ledger.Save();
            state.Metadata.SetValue(profile.SaveKey.ToIdentifier(), text);
            state.Serialized = text;
        }

        internal static void SaveCharacter(CharacterInfo __instance, XElement __result)
        {
            if (!Authority || __result == null) return;
            var receipt = Receipt(__instance);
            __result.SetAttributeValue(CharacterKey, receipt.Key);
            __result.SetAttributeValue("touhouengineerapplied", receipt.Applied);
            __result.SetAttributeValue("touhouengineertarget", receipt.Target);
            __result.SetAttributeValue("touhouengineerbonus", receipt.BonusFloor);
            __result.SetAttributeValue("touhouengineerradiation", receipt.RadiationSeen);
            __result.SetAttributeValue("touhouengineerpolicy", receipt.Policy);
            __result.SetAttributeValue("touhouengineerxpbefore", receipt.ExperienceBefore);
            __result.SetAttributeValue("touhouengineerxpafter", receipt.ExperienceAfter);
            __result.SetAttributeValue("touhouengineerremoved", string.Join(",", receipt.RemovedTalents));
        }

        internal static void LoadCharacter(CharacterInfo __instance, ContentXElement __0)
        {
            var receipt = Receipt(__instance);
            string key = __0.GetAttributeString(CharacterKey, "");
            if (Guid.TryParseExact(key, "N", out _)) receipt.Key = key;
            string applied = __0.GetAttributeString("touhouengineerapplied", "");
            string target = __0.GetAttributeString("touhouengineertarget", "");
            int bonus = __0.GetAttributeInt("touhouengineerbonus", -1);
            if (Guid.TryParseExact(applied, "N", out _) && TransitionProfiles.IsTarget(target) &&
                bonus >= 0 && bonus <= 100)
            {
                receipt.Applied = applied;
                receipt.Target = target;
                receipt.BonusFloor = bonus;
                int policy = __0.GetAttributeInt("touhouengineerpolicy", 0);
                int before = __0.GetAttributeInt("touhouengineerxpbefore", -1);
                int after = __0.GetAttributeInt("touhouengineerxpafter", -1);
                if (policy == EngineerTransitionRules.RespecPolicy && before >= 0 &&
                    after == EngineerTransitionRules.ExperienceAfterLoss(before))
                {
                    receipt.Policy = policy;
                    receipt.ExperienceBefore = before;
                    receipt.ExperienceAfter = after;
                    receipt.RemovedTalents = __0.GetAttributeString("touhouengineerremoved", "")
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            receipt.RadiationSeen = __0.GetAttributeBool("touhouengineerradiation", false);
        }

        internal static void CharacterCreated(Character __result)
        {
            if (__result != null) Birth(__result);
        }

        internal static void CharacterKilled(Character __instance)
        {
            if (!Authority || !Active || !__instance.IsDead || __instance.Info == null) return;
            string key = Receipt(__instance.Info).Key;
            foreach (var profile in TransitionProfiles.All)
            {
                var data = Ledger(profile);
                if (data == null) continue;
                bool changed = false;
                foreach (var record in data.Records.Values)
                {
                    if (record.Completed || record.Source != key || record.DeathObserved) continue;
                    record.DeathObserved = true;
                    record.DeathRound = Round;
                    record.DeathSequence = Interlocked.Increment(ref sequence);
                    changed = true;
                }
                if (changed) Persist(profile);
            }
        }

        internal static object RoundStart(object[] args)
        {
            if (!OwnContext) return null;
            Active = true;
            Round = Guid.NewGuid().ToString("N");
            nextTick = 0;
            notices.Clear();
            EngineerTransitionNet.NewRound();
            if (Authority)
            {
                GameMain.GameSession?.Campaign?.CampaignMetadata?.SetValue(RoundKey.ToIdentifier(), Round);
                foreach (var profile in TransitionProfiles.All) Ledger(profile);
                RegisterCommand();
            }
            return null;
        }

        internal static object RoundEnd(object[] args)
        {
            Active = false;
            EngineerTransitionNet.NewRound();
            return null;
        }

        internal static object Tick(object[] args)
        {
            if (!OwnContext || !Active || Timing.TotalTime < nextTick) return null;
            nextTick = Timing.TotalTime + 0.75;
            try
            {
                EngineerTransitionNet.Tick();
                if (!Authority || !Ready || !TreesReady) return null;
                foreach (var character in Character.CharacterList)
                {
                    if (character.Removed || character.Info == null) continue;
                    Birth(character);
                    if (character.CharacterHealth?.GetAffliction("radiationsickness".ToIdentifier())?.Strength > 0)
                        Receipt(character.Info).RadiationSeen = true;
                    if (IsTransitioned(character)) UpdateCards(character);
                }
                foreach (var profile in TransitionProfiles.All)
                {
                    if (!ProfileReady(profile)) continue;
                    var data = Ledger(profile);
                    if (data == null) continue;
                    foreach (var record in data.Records.Values.ToArray())
                    {
                        Character character = FindPlayer(record.Player);
                        if (character == null || character.Removed || character.IsDead || character.Info?.Job == null) continue;
                        var receipt = Receipt(character.Info);
                        if (record.Completed)
                        {
                            // 原版多人角色文件和战役文件分开存；只修复同一角色，绝不把奖励移给替身。
                            if (record.AppliedCharacter == receipt.Key && IsBaseJob(character, profile))
                                Apply(character, record, profile);
                            continue;
                        }
                        if (!IsBaseJob(character, profile) && !(IsTransitioned(character) && receipt.Applied == record.Id)) continue;
                        if (receipt.Applied == record.Id || record.CanApply(receipt.Key, Round, Birth(character)))
                            Apply(character, record, profile);
                    }
                }
            }
            catch (Exception exception) { Log("本次结算已停止，将在下次检查重试：" + exception.Message); }
            return null;
        }

        internal static string PlayerId(Character character)
        {
            if (character == null) return null;
            if (GameMain.NetworkMember == null)
                return character == Character.Controlled ? "singleplayer" : null;
            var client = GameMain.NetworkMember.ConnectedClients.FirstOrDefault(candidate => candidate.Character == character);
            if (client == null) return null;
            // 不用名字兜底永久资格：改名或同名不能冒领。
            return client.AccountId.TryUnwrap(out AccountId account) ? account.ToString() : null;
        }

        private static Character FindPlayer(string id)
        {
            if (GameMain.NetworkMember == null) return id == "singleplayer" ? Character.Controlled : null;
            return GameMain.NetworkMember.ConnectedClients.FirstOrDefault(client =>
                client.AccountId.TryUnwrap(out AccountId account) && account.ToString() == id)?.Character;
        }

        private static bool PrimaryComplete(Character character, TransitionProfile profile)
        {
            if (!TalentTree.JobTalentTrees.TryGet(profile.BaseJob.ToIdentifier(), out var tree)) return false;
            var primary = tree.TalentSubTrees.FirstOrDefault(subtree => subtree.Identifier == profile.PrimarySubtree);
            return primary != null && primary.HasEnoughTalents(character.Info.UnlockedTalents);
        }

        private static string SkillName(string id) =>
            id == "Touhou_Magic" ? "魔法技能" :
            id == "medical" ? "医疗技能" :
            id == "weapons" ? "武器技能" :
            id == "mechanical" ? "机械技能" :
            id == "helm" ? "驾驶技能" : id;

        private static string CheckRequest(Character character, string target, bool debug)
        {
            var profile = TransitionProfiles.ForTarget(target);
            if (profile == null) return "未知的异传目标。";
            if (!Ready) return "异传接口尚未就绪，书没有消耗。";
            if (!Active || GameMain.GameSession?.Campaign == null) return "异传只支持进行中的战役。";
            if (!IsBaseJob(character, profile) || character.IsDead) return "只有活着的原版" + profile.Name + "可以研读。";
            if (!character.IsOnPlayerTeam) return "只有玩家船员可以研读。";
            if (PlayerId(character) == null) return "无法取得认证玩家身份；未认证联机玩家不能登记永久异传。";
            var data = Ledger(profile);
            if (data == null) return "转职存档未就绪，书没有消耗。";
            string player = PlayerId(character);
            if (data.Records.TryGetValue(player, out var previous) && previous.Completed ||
                !string.IsNullOrEmpty(Receipt(character.Info).Applied))
                return "这个玩家或角色已经完成过异传，不能重复领取。";
            if (!TalentTree.JobTalentTrees.TryGet(target.ToIdentifier(), out var targetTree) ||
                !JobPrefab.Prefabs.ContainsKey(target.ToIdentifier()) || !ProfileReady(profile))
                return "新职业或天赋树未就绪，书没有消耗。";
            if (!debug && !PrimaryComplete(character, profile))
                return "先完成" + profile.Name + "基础树的两个阶段，再研读异传。";
            if (!debug)
            {
                if (!string.IsNullOrEmpty(profile.BaseSkill) &&
                    character.Info.Job.GetSkillLevel(profile.BaseSkill.ToIdentifier()) < profile.BaseSkillLevel)
                    return profile.Name + "需要" + SkillName(profile.BaseSkill) + "达到 " + profile.BaseSkillLevel + "。";
                string skill = profile.SkillForTarget(target);
                float level = profile.SkillLevelForTarget(target);
                if (character.Info.Job.GetSkillLevel(skill.ToIdentifier()) < level)
                    return profile.BookNameForTarget(target) + "需要" + SkillName(skill) + "达到 " + level + "。";
            }
            try
            {
                if (!TalentTree.JobTalentTrees.TryGet(character.Info.Job.Prefab.Identifier, out var source))
                    return "旧职业树缺失，无法安全区分职业与额外天赋；书没有消耗。";
                _ = new EngineerTalentReset(character, EngineerTalentReset.ProfessionTalents(source));
            }
            catch (Exception exception) { return "无法安全撤销旧职业天赋，书没有消耗：" + exception.Message; }
            return null;
        }

        internal static bool UseBook(Item __instance, Character __1)
        {
            var profile = TransitionProfiles.ForBook(__instance.Prefab.Identifier.Value);
            if (profile == null) return true;
            string target = profile.TargetOfBook(__instance.Prefab.Identifier.Value);
            if (target == null) return true;
            if (!OwnContext) return true;
            if (!Authority || __1 == null || __instance.Removed || __instance.Condition <= 0) return false;
            // 只认本人手中的实体，不接受代签角色或隔空用别人的书。
            if (__1.Inventory == null || !__1.HeldItems.Contains(__instance) ||
                !__1.Inventory.AllItems.Contains(__instance)) return false;
            string throttle = PlayerId(__1) ?? ("character:" + __1.ID);
            if (notices.TryGetValue(throttle, out double at) && Timing.TotalTime < at) return false;
            notices[throttle] = Timing.TotalTime + 1.5;
            try
            {
                string error = CheckRequest(__1, target, false);
                if (error != null) { EngineerTransitionNet.Notice(__1, error); return false; }
                if (Entity.Spawner == null) return false;
                var data = Ledger(profile);
                if (data == null) return false;
                data.Records.TryGetValue(throttle, out var previous);
                float condition = __instance.Condition;
                try
                {
                    CreateRequest(__1, target, profile);
                    __instance.Condition = 0;
                    Entity.Spawner.AddItemToRemoveQueue(__instance);
                }
                catch
                {
                    if (previous == null) data.Records.Remove(throttle);
                    else data.Records[throttle] = previous;
                    __instance.Condition = condition;
                    Persist(profile);
                    throw;
                }
                EngineerTransitionNet.Notice(__1, "异传已登记：生效时总经验损失 40%，旧职业天赋重选，额外天赋保留；下个巡回或死亡后新" + profile.Name + "身体承接。");
            }
            catch (Exception exception) { Log("研读失败：" + exception.Message); }
            return false;
        }

        private static EngineerTransitionRecord CreateRequest(Character character, string target, TransitionProfile profile)
        {
            var data = Ledger(profile);
            if (data == null) throw new InvalidOperationException("转职存档未就绪。");
            var record = new EngineerTransitionRecord
            {
                Player = PlayerId(character), Name = character.Name, Id = Guid.NewGuid().ToString("N"),
                Source = Receipt(character.Info).Key, Target = target, OriginRound = Round,
                Policy = EngineerTransitionRules.RespecPolicy
            };
            data.Records[record.Player] = record;
            Birth(character);
            Persist(profile);
            return record;
        }

        private static void Apply(Character character, EngineerTransitionRecord record, TransitionProfile profile)
        {
            if (!ProfileReady(profile)) return;
            var info = character.Info;
            var receipt = Receipt(info);
            if (!string.IsNullOrEmpty(receipt.Applied) && receipt.Applied != record.Id) return;
            if (!JobPrefab.Prefabs.TryGet(record.Target.ToIdentifier(), out var prefab) ||
                !TalentTree.JobTalentTrees.TryGet(record.Target.ToIdentifier(), out var targetTree)) return;
            Job oldJob = info.Job;
            Job newJob = oldJob.Prefab == prefab ? oldJob : CopyJob(oldJob, prefab);
            EngineerTalentReset reset = null;
            bool alreadyApplied = receipt.Applied == record.Id;
            bool legacyCompleted = record.Completed && record.Policy == 0;
            if (alreadyApplied)
            {
                // 角色回执比战役文件新时，只修复记录和 Job，不再次扣经验或清掉后来重选的天赋。
                record.Policy = receipt.Policy;
                record.BonusFloor = receipt.BonusFloor;
                record.ExperienceBefore = receipt.ExperienceBefore;
                record.ExperienceAfter = receipt.ExperienceAfter;
                record.RemovedTalents = receipt.RemovedTalents;
                if (receipt.Policy == EngineerTransitionRules.RespecPolicy) record.PlannedCharacter = receipt.Key;
            }
            else if (!legacyCompleted)
            {
                if (record.ExperienceBefore < 0)
                {
                    if (!TalentTree.JobTalentTrees.TryGet(oldJob.Prefab.Identifier, out var source))
                        throw new InvalidOperationException("旧职业树缺失，暂停转职。");
                    reset = new EngineerTalentReset(character, EngineerTalentReset.ProfessionTalents(source));
                    record.Policy = EngineerTransitionRules.RespecPolicy;
                    record.ExperienceBefore = info.ExperiencePoints;
                    record.ExperienceAfter = EngineerTransitionRules.ExperienceAfterLoss(info.ExperiencePoints);
                    record.PlannedCharacter = receipt.Key;
                    record.RemovedTalents = reset.Removed.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    // 兼容旧存档字段，现在只作回执，不再发放任何额外点数。
                    record.BonusFloor = info.AdditionalTalentPoints;
                }
                if (record.PlannedCharacter != receipt.Key)
                    throw new InvalidOperationException("已有另一个身体的结算计划，暂停转职以免重复结算。");
                // 尚未提交的旧计划也补齐整棵来源树，但不重算已写入的经验代价。
                if (TalentTree.JobTalentTrees.TryGet(oldJob.Prefab.Identifier, out var plannedSource))
                    record.RemovedTalents = record.RemovedTalents
                        .Concat(EngineerTalentReset.ProfessionTalents(plannedSource).Select(id => id.Value))
                        .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                reset ??= new EngineerTalentReset(character, record.RemovedTalents.Select(id => id.ToIdentifier()));
                Persist(profile);
                reset.Commit();
                // 恢复预先写入的同一笔损失；计划后获得的经验不会在重试时再打六折。
                int experience = (int)Math.Min(int.MaxValue, (long)record.ExperienceAfter +
                    Math.Max(0L, (long)info.ExperiencePoints - record.ExperienceBefore));
                // 1.13.4 的 setter 只写字段，界面和网络在回执提交后统一更新，避免 UI 回调打断结算。
                info.ExperiencePoints = experience;
            }
            info.Job = newJob;
            receipt.Applied = record.Id;
            receipt.Target = record.Target;
            receipt.BonusFloor = record.BonusFloor;
            receipt.Policy = record.Policy;
            receipt.ExperienceBefore = record.ExperienceBefore;
            receipt.ExperienceAfter = record.ExperienceAfter;
            receipt.RemovedTalents = record.RemovedTalents;
            record.Completed = true;
            record.AppliedCharacter = receipt.Key;
            Persist(profile);
            reset?.PublishItemStats();
            UpdateCards(character);
#if SERVER
            GameMain.NetworkMember?.CreateEntityEvent(character, new Character.UpdateExperienceEventData());
#endif
            EngineerTransitionNet.Broadcast(character);
#if CLIENT
            GameMain.GameSession?.RefreshAnyOpenPlayerInfo();
#endif
            EngineerTransitionNet.Notice(character, "异传生效：" + prefab.Name.Value +
                (record.Policy == EngineerTransitionRules.RespecPolicy
                    ? "。旧职业天赋已清除，可在新树重选；总经验损失 40%，装束等额外天赋、技能和装备保留。"
                    : "。旧版完成记录已恢复，本次不追扣经验、不洗点。"));
            Log(character.Name + ": " + oldJob.Prefab.Identifier + " -> " + prefab.Identifier);
        }

        internal static Job CopyJob(Job oldJob, JobPrefab prefab)
        {
            var oldSkills = oldJob.GetSkills().ToArray();
            var values = oldSkills.ToDictionary(skill => skill.Identifier, skill => skill.Level);
            if (!values.ContainsKey("Touhou_Magic".ToIdentifier())) values["Touhou_Magic".ToIdentifier()] = 20;
            var result = new Job(prefab, false);
            result.Variant = oldJob.Variant;
            result.OverrideSkills(values);
            foreach (var skill in oldSkills) SetSkillPrice(result.GetSkill(skill.Identifier), skill.PriceMultiplier);
            return result;
        }

        internal static void SetSkillPrice(Skill skill, float value)
        {
            // OverrideSkills 重建 Skill 会丢只读的价格系数，保留旧值避免后续重算变价。
            if (skillPrice == null) throw new MissingFieldException(nameof(Skill.PriceMultiplier));
            skillPrice.SetValue(skill, value);
        }

        internal static bool UpdateCards(Character character)
        {
            if (character.Inventory == null) return false;
            bool changed = false;
            foreach (var item in character.Inventory.AllItems)
            {
                var card = item.GetComponent<IdCard>();
                if (card == null || card.OwnerName != character.Info.Name) continue;
                Identifier job = character.Info.Job.Prefab.Identifier;
                var jobTags = item.GetTags().Where(tag => tag.Value.StartsWith("jobid:", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (card.OwnerJobId == job && jobTags.Length == 1 && jobTags[0].Value == "jobid:" + job.Value) continue;
                card.OwnerJobId = job;
                foreach (Identifier tag in jobTags) item.RemoveTag(tag);
                // 只动职业标签，门禁、队伍、外貌和姓名全部保留。
                item.AddTag("jobid:" + job.Value);
                changed = true;
            }
            return changed;
        }

        internal static void HasEngineerJob(Character __instance, string __0, ref bool __result)
        {
            var profile = TransitionedProfile(__instance);
            if (OwnContext && profile != null &&
                string.Equals(__0, profile.BaseJob, StringComparison.OrdinalIgnoreCase)) __result = true;
        }

        internal static void HasEngineerJobIdentifier(Character __instance, Identifier __0, ref bool __result)
        {
            var profile = TransitionedProfile(__instance);
            if (OwnContext && profile != null && __0 == profile.BaseJob) __result = true;
        }

        internal static bool EngineerCondition(PropertyConditional __instance, ISerializableEntity target,
            SerializableProperty property, ref bool __result)
        {
            var profile = target is Character character ? TransitionedProfile(character) : null;
            if (!OwnContext || profile == null ||
                __instance.AttributeName != "jobidentifier" ||
                !string.Equals(__instance.AttributeValue, profile.BaseJob, StringComparison.OrdinalIgnoreCase))
                return true;
            if (__instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.Equals) __result = true;
            else if (__instance.ComparisonOperator == PropertyConditional.ComparisonOperatorType.NotEquals) __result = false;
            else return true;
            return false;
        }

        internal static void EngineerSpawnPoints(List<CharacterInfo> __0, Submarine __1, WayPoint[] __result)
        {
            if (!OwnContext || __result == null) return;
            var candidatesByJob = new Dictionary<string, WayPoint[]>(StringComparer.Ordinal);
            for (int i = 0; i < Math.Min(__0.Count, __result.Length); i++)
            {
                var profile = TransitionProfiles.ForTarget(__0[i].Job?.Prefab?.Identifier.Value);
                if (profile == null) continue;
                if (!candidatesByJob.TryGetValue(profile.BaseJob, out var candidates))
                {
                    candidates = WayPoint.WayPointList.Where(point => point.Submarine == __1 &&
                        point.SpawnType == SpawnType.Human &&
                        point.AssignedJob?.Identifier == profile.BaseJob).ToArray();
                    candidatesByJob[profile.BaseJob] = candidates;
                }
                if (candidates.Length == 0) continue;
                __result[i] = candidates.FirstOrDefault(point => !__result.Contains(point)) ?? candidates[0];
            }
        }

#if SERVER
        internal static void EngineerKarma(Character __0, ref float __1, string __2)
        {
            if (Authority && __2 == "Disconnected excessive number of wires" &&
                TransitionedProfile(__0)?.BaseJob == "engineer") __1 *= 0.5f;
        }
#endif

        internal static void RegisterCommand()
        {
            if (!Authority || registeredCommand != null) return;
            if (DebugConsole.Commands.Any(command => command.Names.Contains("jobtransit".ToIdentifier())))
            {
                Log("同名 jobtransit 命令已存在，未覆盖。");
                return;
            }
            // 这版 LuaGame.AddCommand 丢弃 isCheat 参数，直接用原版构造器才真的受作弊开关约束。
            registeredCommand = new DebugConsole.Command("jobtransit",
                "职业异传：jobtransit status | pending|now <玩家名/self> arcane|mystic | clear|cleanup <玩家名/self>；cleanup 退掉旧树全部天赋，不扣经验",
                Command, null, true);
#if SERVER
            registeredCommand.OnClientRequestExecute = (sender, position, args) =>
                Log("jobtransit 只允许服务器本机控制台执行；玩家请使用异传书。");
#endif
            DebugConsole.Commands.Add(registeredCommand);
        }

        private static void Command(string[] args)
        {
            if (!Authority || !Ready) return;
            if (args.Length == 0 || args[0] == "status")
            {
                bool any = false;
                foreach (var statusProfile in TransitionProfiles.All)
                {
                    var data = Ledger(statusProfile);
                    if (data == null) continue;
                    foreach (var record in data.Records.Values)
                    {
                        Log("[" + statusProfile.Name + "] " + record.Name + ": " + record.Target +
                            (record.Completed ? " [已完成]" : " [待生效]"));
                        any = true;
                    }
                }
                if (!any) Log("没有异传记录。");
                return;
            }
            if (args.Length < 2) { Log("参数不足，见 help jobtransit。"); return; }
            Character character;
            if (args[1] == "self") character = Character.Controlled;
            else character = GameMain.NetworkMember?.ConnectedClients.FirstOrDefault(client =>
                string.Equals(client.Name, args[1], StringComparison.OrdinalIgnoreCase))?.Character;
            if (character == null) { Log("没有找到在线玩家；单人请用 self。"); return; }
            string player = PlayerId(character);
            var profile = TransitionedProfile(character) ??
                TransitionProfiles.ForBaseJob(character.Info?.Job?.Prefab?.Identifier.Value);
            if (profile == null) { Log("该角色不是可异传的原版职业。"); return; }
            var current = Ledger(profile);
            if (current == null || player == null) { Log("无法取得战役或认证玩家身份。"); return; }
            if (args[0] == "cleanup")
            {
                if (args.Length != 2 || !IsTransitioned(character) ||
                    !current.Records.TryGetValue(player, out var completed) || !completed.Completed ||
                    completed.AppliedCharacter != Receipt(character.Info).Key)
                {
                    Log("cleanup 仅用于有完成记录、且身份匹配的已转职角色。");
                    return;
                }
                try
                {
                    if (!TalentTree.JobTalentTrees.TryGet(profile.BaseJob.ToIdentifier(), out var original))
                        throw new InvalidOperationException("原" + profile.Name + "树缺失，未清理。");
                    var scope = completed.RemovedTalents.Select(id => id.ToIdentifier())
                        .Concat(EngineerTalentReset.ProfessionTalents(original)).Distinct().ToArray();
                    var cleanup = new EngineerTalentReset(character, scope);
                    cleanup.Commit();
                    completed.RemovedTalents = scope.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                    Receipt(character.Info).RemovedTalents = completed.RemovedTalents;
                    Persist(profile);
                    cleanup.PublishItemStats();
                    EngineerTransitionNet.Broadcast(character);
#if CLIENT
                    GameMain.GameSession?.RefreshAnyOpenPlayerInfo();
#endif
                    Log("旧" + profile.Name + "树的全部天赋及永久记录已清理，经验和额外天赋未变；同 ID 的后来重选天赋也已退点。");
                }
                catch (Exception exception) { Log("cleanup 未完成：" + exception.Message); }
                return;
            }
            if (args[0] == "clear")
            {
                if (current.Records.TryGetValue(player, out var record) && !record.Completed)
                {
                    current.Records.Remove(player);
                    Persist(profile);
                    Log("已清除待生效异传；不退书、不清完成记录。");
                }
                return;
            }
            if (args.Length != 3 || (args[0] != "pending" && args[0] != "now")) return;
            string target = args[2] == "arcane" ? profile.ArcaneJob :
                args[2] == "mystic" ? profile.MysticJob : null;
            if (target == null) return;
            string error = CheckRequest(character, target, true);
            if (error != null) { Log(error); return; }
            var pending = CreateRequest(character, target, profile);
            if (args[0] == "now") Apply(character, pending, profile);
            else Log(character.Name + " 已登记待生效异传；调试命令未消耗书。");
        }

        internal static void Clear()
        {
            Ready = false;
            Active = false;
            ledgerStates.Clear();
            receipts.Clear();
            bodies.Clear();
            notices.Clear();
            if (registeredCommand != null) DebugConsole.Commands.Remove(registeredCommand);
            registeredCommand = null;
            EngineerTransitionNet.Clear();
        }
    }
}
