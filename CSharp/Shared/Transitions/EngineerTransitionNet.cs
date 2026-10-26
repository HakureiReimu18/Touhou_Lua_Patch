using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.LuaCs.Compatibility;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace Touhou.EngineerTransitions
{
    internal static class EngineerTransitionNet
    {
        private const string Request = "touhou_jt_request_v2";
        private const string State = "touhou_jt_state_v2";
        private const string Message = "touhou_jt_message_v2";
        private static readonly Dictionary<Client, string> nonces = new();
        private static readonly Dictionary<Client, double> requestTimes = new();
        private static string clientNonce = Guid.NewGuid().ToString("N");
        private static long sendSequence;
#if CLIENT
        private static double nextRequest;
        private sealed class Snapshot
        {
            public ushort EntityId;
            public ushort InfoId;
            public long Sequence;
            public string Key;
            public string Target;
            public string Applied;
            public int Extra;
            public int Bonus;
            public int Policy;
            public int Experience;
            public int ExperienceBefore;
            public int ExperienceAfter;
            public Identifier[] Removed;
            public HashSet<Identifier> Talents;
            public Dictionary<Identifier, float> Skills;
            public Dictionary<Identifier, float> Prices;
            public double Expires;
        }
        private static readonly Dictionary<ushort, Snapshot> pending = new();
        private static readonly Dictionary<ushort, long> receivedSequences = new();
#endif

        internal static bool Register()
        {
            try
            {
                if (LuaCsSetup.Instance.Networking is not ILuaCsNetworking compat)
                    throw new InvalidOperationException("LuaCs networking compatibility interface unavailable.");
#if SERVER
                if (typeof(Barotrauma.LuaCs.NetworkingService).GetMethods().All(method =>
                    method.Name != "Send" || method.GetParameters().Length != 3))
                    throw new MissingMethodException("NetworkingService.Send(message, connection, delivery)");
#endif
#if SERVER
                {
                    compat.Receive(Request, args =>
                    {
                        if (!EngineerTransitions.Authority || !EngineerTransitions.Active) return;
                        var (read, sender) = ReadArgs(args);
                        if (read == null || sender == null) return;
                        try
                        {
                            if (requestTimes.TryGetValue(sender, out double next) && Timing.TotalTime < next) return;
                            requestTimes[sender] = Timing.TotalTime + 1;
                            string nonce = read.ReadString();
                            if (!Guid.TryParseExact(nonce, "N", out _)) return;
                            nonces[sender] = nonce;
                            // 请求只能取快照，没有职业、玩家 ID 或加点参数。
                            foreach (var character in Character.CharacterList)
                                if (!character.Removed && EngineerTransitions.IsTransitioned(character))
                                    SendSnapshot(sender, character);
                        }
                        catch (Exception exception) { EngineerTransitions.Log("快照请求未处理：" + exception.Message); }
                    });
                }
#endif
#if CLIENT
                compat.Receive(State, args =>
                {
                    if (!EngineerTransitions.OwnContext || EngineerTransitions.Authority || !EngineerTransitions.Active) return;
                    var (read, _) = ReadArgs(args);
                    if (read == null) return;
                    try
                    {
                        if (read.ReadString() != clientNonce) return;
                        var snapshot = new Snapshot
                        {
                            EntityId = read.ReadUInt16(), InfoId = read.ReadUInt16(),
                            Sequence = read.ReadInt64(),
                            Key = read.ReadString(), Target = read.ReadString(), Applied = read.ReadString(),
                            Extra = read.ReadInt32(), Bonus = read.ReadInt32(),
                            Policy = read.ReadInt32(), Experience = read.ReadInt32(),
                            ExperienceBefore = read.ReadInt32(), ExperienceAfter = read.ReadInt32(),
                            Skills = new Dictionary<Identifier, float>(), Prices = new Dictionary<Identifier, float>(),
                            Expires = Timing.TotalTime + 10
                        };
                        if (!Guid.TryParseExact(snapshot.Key, "N", out _) ||
                            !EngineerTransitionRules.IsTarget(snapshot.Target) ||
                            snapshot.Extra < 0 || snapshot.Extra > 100 || snapshot.Bonus < -1 || snapshot.Bonus > 100 ||
                            snapshot.Experience < 0 || (snapshot.Policy != 0 && snapshot.Policy != EngineerTransitionRules.RespecPolicy) ||
                            (snapshot.Policy == EngineerTransitionRules.RespecPolicy && (snapshot.ExperienceBefore < 0 ||
                                snapshot.ExperienceAfter != EngineerTransitionRules.ExperienceAfterLoss(snapshot.ExperienceBefore))))
                            return;
                        if (receivedSequences.TryGetValue(snapshot.EntityId, out long last) && snapshot.Sequence <= last) return;
                        int removedCount = read.ReadUInt16();
                        if (removedCount > 2048) return;
                        snapshot.Removed = new Identifier[removedCount];
                        for (int i = 0; i < removedCount; i++) snapshot.Removed[i] = read.ReadIdentifier();
                        int talentCount = read.ReadUInt16();
                        if (talentCount > 2048) return;
                        snapshot.Talents = new HashSet<Identifier>();
                        for (int i = 0; i < talentCount; i++) snapshot.Talents.Add(read.ReadIdentifier());
                        int count = read.ReadUInt16();
                        if (count > 128) return;
                        for (int i = 0; i < count; i++)
                        {
                            Identifier id = read.ReadIdentifier();
                            float value = read.ReadSingle(), price = read.ReadSingle();
                            if (!float.IsFinite(value) || value < 0 || !float.IsFinite(price)) return;
                            snapshot.Skills.Add(id, value);
                            snapshot.Prices.Add(id, price);
                        }
                        pending[snapshot.EntityId] = snapshot;
                        receivedSequences[snapshot.EntityId] = snapshot.Sequence;
                        ApplySnapshots();
                    }
                    catch (Exception exception) { EngineerTransitions.Log("职业快照未应用：" + exception.Message); }
                });
                compat.Receive(Message, args =>
                {
                    if (!EngineerTransitions.OwnContext || EngineerTransitions.Authority || !EngineerTransitions.Active) return;
                    var (read, _) = ReadArgs(args);
                    if (read == null) return;
                    try
                    {
                        if (read.ReadString() == clientNonce) ShowNotice(read.ReadString());
                    }
                    catch { }
                });
#endif
                return true;
            }
            catch (Exception exception)
            {
                EngineerTransitions.Log("网络接口未就绪：" + exception.Message);
                return false;
            }
        }

        private static (IReadMessage, Client) ReadArgs(object[] args)
            => (args?.OfType<IReadMessage>().FirstOrDefault(), args?.OfType<Client>().FirstOrDefault());

        internal static void NewRound()
        {
            nonces.Clear();
            requestTimes.Clear();
            clientNonce = Guid.NewGuid().ToString("N");
            sendSequence = 0;
#if CLIENT
            nextRequest = 0;
            pending.Clear();
            receivedSequences.Clear();
#endif
        }

        internal static void Tick()
        {
            if (EngineerTransitions.Authority)
            {
                var clients = GameMain.NetworkMember?.ConnectedClients;
                foreach (var client in nonces.Keys.Where(client => clients == null || !clients.Contains(client)).ToArray())
                {
                    nonces.Remove(client);
                    requestTimes.Remove(client);
                }
                return;
            }
#if CLIENT
            ApplySnapshots();
            if (Timing.TotalTime < nextRequest) return;
            nextRequest = Timing.TotalTime + 5;
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                var message = net.Start(Request);
                message.WriteString(clientNonce);
                ((ILuaCsNetworking)net).Send(message, DeliveryMethod.Reliable);
            }
            catch (Exception exception) { EngineerTransitions.Log("请求职业快照失败：" + exception.Message); }
#endif
        }

        internal static void Broadcast(Character character)
        {
            if (!EngineerTransitions.Authority || GameMain.NetworkMember == null) return;
            foreach (var client in GameMain.NetworkMember.ConnectedClients) SendSnapshot(client, character);
        }

        private static void SendSnapshot(Client client, Character character)
        {
            if (!nonces.TryGetValue(client, out string nonce)) return;
            var receipt = EngineerTransitions.Receipt(character.Info);
            var skills = character.Info.Job.GetSkills().ToArray();
            var talents = character.Info.UnlockedTalents.ToArray();
            if (skills.Length > 128 || talents.Length > 2048 || receipt.RemovedTalents.Length > 2048) return;
            Send(client, State, message =>
            {
                message.WriteString(nonce);
                message.WriteUInt16(character.ID);
                message.WriteUInt16(character.Info.ID);
                message.WriteInt64(++sendSequence);
                message.WriteString(receipt.Key);
                message.WriteString(character.Info.Job.Prefab.Identifier.Value);
                message.WriteString(receipt.Applied);
                message.WriteInt32(character.Info.AdditionalTalentPoints);
                message.WriteInt32(receipt.BonusFloor);
                message.WriteInt32(receipt.Policy);
                message.WriteInt32(character.Info.ExperiencePoints);
                message.WriteInt32(receipt.ExperienceBefore);
                message.WriteInt32(receipt.ExperienceAfter);
                message.WriteUInt16((ushort)receipt.RemovedTalents.Length);
                foreach (string id in receipt.RemovedTalents) message.WriteIdentifier(id.ToIdentifier());
                message.WriteUInt16((ushort)talents.Length);
                foreach (Identifier id in talents) message.WriteIdentifier(id);
                message.WriteUInt16((ushort)skills.Length);
                foreach (var skill in skills)
                {
                    message.WriteIdentifier(skill.Identifier);
                    message.WriteSingle(skill.Level);
                    message.WriteSingle(skill.PriceMultiplier);
                }
            });
        }

        internal static void Notice(Character character, string text)
        {
            if (GameMain.NetworkMember == null)
            {
#if CLIENT
                ShowNotice(text);
#else
                EngineerTransitions.Log(text);
#endif
                return;
            }
            var client = GameMain.NetworkMember.ConnectedClients.FirstOrDefault(candidate => candidate.Character == character);
            if (client != null && nonces.TryGetValue(client, out string nonce))
                Send(client, Message, message => { message.WriteString(nonce); message.WriteString(text); });
            EngineerTransitions.Log(character.Name + "：" + text);
        }

#if CLIENT
        private static void ShowNotice(string text)
        {
            EngineerTransitions.Log(text);
            GUI.AddMessage(text, Color.LightSkyBlue, 8f);
        }

        private static void ApplySnapshots()
        {
            bool refreshed = false;
            foreach (var pair in pending.ToArray())
            {
                var snapshot = pair.Value;
                if (Timing.TotalTime > snapshot.Expires) { pending.Remove(pair.Key); continue; }
                var character = Entity.FindEntityByID(snapshot.EntityId) as Character;
                if (character?.Info?.Job == null || character.Info.ID != snapshot.InfoId) continue;
                if (!JobPrefab.Prefabs.TryGet(snapshot.Target.ToIdentifier(), out var prefab)) continue;
                var receipt = EngineerTransitions.Receipt(character.Info);
                bool newJob = character.Info.Job.Prefab != prefab;
                bool firstReceipt = receipt.Applied != snapshot.Applied || receipt.Policy != snapshot.Policy;
                Job job = null;
                if (newJob)
                {
                    job = new Job(prefab, false);
                    job.Variant = character.Info.Job.Variant;
                    job.OverrideSkills(snapshot.Skills);
                    foreach (var skill in job.GetSkills()) EngineerTransitions.SetSkillPrice(skill, snapshot.Prices[skill.Identifier]);
                }
                EngineerTalentReset reset = null;
                bool syncCleanup = snapshot.Policy == EngineerTransitionRules.RespecPolicy || snapshot.Removed.Length > 0;
                if (syncCleanup)
                    reset = new EngineerTalentReset(character, snapshot.Removed.Where(id => !snapshot.Talents.Contains(id)));
                reset?.Commit();
                if (newJob)
                {
                    character.Info.Job = job;
                    character.Info.AdditionalTalentPoints = snapshot.Extra;
                    if (snapshot.Policy == EngineerTransitionRules.RespecPolicy && firstReceipt &&
                        character.Info.ExperiencePoints >= snapshot.ExperienceBefore)
                        character.Info.ExperiencePoints = Math.Max(snapshot.Experience,
                            character.Info.ExperiencePoints - (snapshot.ExperienceBefore - snapshot.ExperienceAfter));
                    refreshed = true;
                }
                if (syncCleanup)
                {
                    foreach (var id in snapshot.Talents)
                        if (!character.Info.UnlockedTalents.Contains(id)) character.GiveTalent(id, false);
                    refreshed |= reset.HasChanges;
                }
                // 后续技能、经验走原版增量消息；旧快照不能把刚获得的技能等级倒扣回去。
                receipt.Key = snapshot.Key;
                receipt.Applied = snapshot.Applied;
                receipt.Target = snapshot.Target;
                receipt.BonusFloor = snapshot.Bonus;
                receipt.Policy = snapshot.Policy;
                receipt.ExperienceBefore = snapshot.ExperienceBefore;
                receipt.ExperienceAfter = snapshot.ExperienceAfter;
                receipt.RemovedTalents = snapshot.Removed.Select(id => id.Value).ToArray();
                refreshed |= EngineerTransitions.UpdateCards(character);
                pending.Remove(pair.Key);
            }
            if (refreshed) GameMain.GameSession?.RefreshAnyOpenPlayerInfo();
        }
#endif

        private static void Send(Client client, string name, Action<IWriteMessage> write)
        {
            try
            {
                var net = LuaCsSetup.Instance.Networking;
                var method = net.GetType().GetMethods().FirstOrDefault(candidate =>
                    candidate.Name == "Send" && candidate.GetParameters().Length == 3 &&
                    candidate.GetParameters()[1].ParameterType.Name == "NetworkConnection");
                var connection = client.GetType().GetProperty("Connection")?.GetValue(client);
                if (method == null || connection == null) return;
                var message = net.Start(name);
                write(message);
                object reliable = Enum.Parse(method.GetParameters()[2].ParameterType, "Reliable");
                method.Invoke(net, new[] { message, connection, reliable });
            }
            catch (Exception exception) { EngineerTransitions.Log("发送职业数据失败：" + exception.Message); }
        }

        internal static void Clear() => NewRound();
    }
}
