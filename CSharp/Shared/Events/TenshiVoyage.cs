using System;
#if !TENSHI_VOYAGE_TEST || TENSHI_VOYAGE_RUNTIME_TEST
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
#endif
#if !TENSHI_VOYAGE_TEST
using Barotrauma.LuaCs;
using HarmonyLib;
#endif

namespace Touhou.TenshiVoyage
{
    internal static class VoyageRules
    {
        internal static bool CanFinish(bool cleared, bool participated, bool alive, bool aboard, bool atExit, bool transitioning) =>
            cleared && participated && alive && aboard && atExit && transitioning;
        internal static bool RequestCurrent(int capturedEpoch, int epoch, int capturedRequest, int request, bool owned, bool active) =>
            capturedEpoch == epoch && capturedRequest == request && owned && active;
    }

    internal sealed class VoyageCues
    {
        private int played;
        internal bool Take(int bit)
        {
            if ((played & bit) != 0) return false;
            played |= bit;
            return true;
        }
    }

#if !TENSHI_VOYAGE_TEST
    public sealed class TenshiVoyagePlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize() => harmony = new Harmony("touhou.tenshi.voyage." +
#if SERVER
            "server");
#else
            "client");
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(VoyageStartPatch));
            harmony.PatchAll(typeof(VoyageUpdatePatch));
            harmony.PatchAll(typeof(VoyageCompletionPatch));
            harmony.PatchAll(typeof(VoyageEndPatch));
            harmony.PatchAll(typeof(VoyageItemSpawnPatch));
            VoyageRuntime.Enabled = true;
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.TenshiVoyage.Start", OnRoundStart);
            // 热重载也只补当前这一趟，不把访客塞进正式船员名单。
            if (VoyageRuntime.Authority && GameMain.GameSession?.Missions != null)
                foreach (var mission in GameMain.GameSession.Missions.OfType<MonsterMission>()) VoyageRuntime.Start(mission);
        }
        private static object OnRoundStart(object[] args)
        {
            if (VoyageRuntime.Authority) VoyageRuntime.BeginRound();
            return null;
        }
        public void Dispose()
        {
            if (VoyageRuntime.Authority) VoyageRuntime.Clear();
            VoyageRuntime.Enabled = false;
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.TenshiVoyage.Start");
            harmony?.UnpatchSelf();
        }
    }

    [HarmonyPatch(typeof(MonsterMission), "StartMissionSpecific")]
    internal static class VoyageStartPatch
    {
        private static void Postfix(MonsterMission __instance)
        {
            if (!VoyageRuntime.Authority) return;
            try { VoyageRuntime.Start(__instance); }
            catch (Exception ex) { Touhou.Anomalies.AnomalyRuntime.Warn("天子同行初始化失败", ex); }
        }
    }

    [HarmonyPatch(typeof(MonsterMission), "UpdateMissionSpecific")]
    internal static class VoyageUpdatePatch
    {
        private static void Postfix(MonsterMission __instance, float deltaTime)
        {
            if (!VoyageRuntime.Authority || Touhou.Anomalies.AnomalyRuntime.Paused || !float.IsFinite(deltaTime) || deltaTime <= 0f) return;
            try { VoyageRuntime.Update(__instance, deltaTime); }
            catch (Exception ex) { Touhou.Anomalies.AnomalyRuntime.Warn("天子同行更新失败", ex); }
        }
    }

    [HarmonyPatch(typeof(MonsterMission), "DetermineCompleted")]
    internal static class VoyageCompletionPatch
    {
        private static void Postfix(MonsterMission __instance, CampaignMode.TransitionType transitionType, ref bool __result)
        {
            if (!VoyageRuntime.Authority || __instance.Prefab.Identifier != VoyageRuntime.MissionId) return;
            // 击退海兽不等于这一趟结束，存盘退出也不算随船抵达。
            __result = VoyageRuntime.CanFinish(__instance, __result, transitionType);
        }
    }

    [HarmonyPatch(typeof(Mission), nameof(Mission.End))]
    internal static class VoyageEndPatch
    {
        private static void Postfix(Mission __instance, CampaignMode.TransitionType transitionType)
        {
            if (!VoyageRuntime.Authority || !(__instance is MonsterMission mission) ||
                mission.Prefab.Identifier != VoyageRuntime.MissionId) return;
            try { VoyageRuntime.End(mission, transitionType); }
            catch (Exception ex) { Touhou.Anomalies.AnomalyRuntime.Warn("天子同行结算失败", ex); }
        }
    }

    [HarmonyPatch(typeof(EntitySpawner.ItemSpawnInfo), nameof(EntitySpawner.ItemSpawnInfo.OnSpawned))]
    internal static class VoyageItemSpawnPatch
    {
        private static void Postfix(EntitySpawner.ItemSpawnInfo __instance, Entity __0)
        {
            if (!VoyageRuntime.Authority || !(__0 is Item item)) return;
            VoyageRuntime.ContainedItemSpawned(__instance.Inventory, item);
        }
    }

#endif

#if !TENSHI_VOYAGE_TEST || TENSHI_VOYAGE_RUNTIME_TEST
    internal static class VoyageRuntime
    {
        internal static readonly Identifier MissionId = "Touhou_Tenshi_Voyage".ToIdentifier();
        private static readonly Identifier NpcId = "Hinanawi_Tenshi".ToIdentifier();
        private static readonly Identifier WeaponId = "Touhou_Concentrated_Energy_Arc_Emitter".ToIdentifier();
        private static readonly Identifier AmmoId = "Touhou_Concentrated_Energy_Arc_Emitter_Ammo".ToIdentifier();
        private static readonly Identifier BulletId = "Touhou_Concentrated_Energy_Arc_Emitter_Bullet".ToIdentifier();
        private static readonly Identifier HatId = "Tenshi_Hat".ToIdentifier();
        private static readonly Identifier PeachId = "Touhou_Tenshi_Peach".ToIdentifier();
        private sealed class Attempt
        {
            internal readonly MonsterMission Mission;
            internal readonly Level OwnerLevel;
            internal readonly HashSet<Item> Loan = new HashSet<Item>();
            internal readonly VoyageCues Cues = new VoyageCues();
            internal Character Guest;
            internal bool Pending, Participated;
            internal int Request;
            internal double RequestedAt;
            internal float Elapsed;
            internal Attempt(MonsterMission mission) { Mission = mission; OwnerLevel = Level.Loaded; }
        }
        private sealed class LoanBinding
        {
            internal readonly Attempt Owner;
            internal readonly int Epoch, Request;
            internal LoanBinding(Attempt owner, int epoch, int request) { Owner = owner; Epoch = epoch; Request = request; }
        }
        private static readonly ConditionalWeakTable<Item, LoanBinding> generatedItemOwners = new ConditionalWeakTable<Item, LoanBinding>();
        private static readonly Dictionary<MonsterMission, Attempt> attempts = new Dictionary<MonsterMission, Attempt>();
        private static int epoch;
        internal static bool Enabled;
        internal static bool Authority => Enabled && Touhou.Anomalies.AnomalyRuntime.Authority &&
            Touhou.Anomalies.AnomalyRuntime.IsMainThread;
        private static bool Alive(Character c) => c != null && !c.Removed && !c.IsDead;
        private static bool Current(Attempt a, int token, int request) =>
            VoyageRules.RequestCurrent(token, epoch, request, a.Request,
                attempts.TryGetValue(a.Mission, out var active) && active == a,
                a.OwnerLevel == Level.Loaded && GameMain.GameSession?.Missions.Contains(a.Mission) == true);

        internal static void Start(MonsterMission mission)
        {
            if (mission.Prefab.Identifier != MissionId || attempts.ContainsKey(mission) ||
                Level.Loaded?.Type != LevelData.LevelType.LocationConnection || Submarine.MainSub == null) return;
            if (GameMain.GameSession?.Campaign?.CampaignMetadata.GetBoolean("tenshi_join_complete".ToIdentifier()) == true) return;
            if (attempts.Count != 0) return;
            var attempt = new Attempt(mission);
            attempts.Add(mission, attempt);
            Spawn(attempt);
        }

        private static void Spawn(Attempt a)
        {
            var sub = Submarine.MainSub;
            if (sub == null || Entity.Spawner == null) return;
            var point = WayPoint.GetRandom(SpawnType.Human, null, sub);
            Hull hull = point?.CurrentHull ?? sub.GetHulls(alsoFromConnectedSubs: false).FirstOrDefault();
            if (hull == null) return;
            HumanPrefab prefab = NPCSet.Get("touhounpc".ToIdentifier(), NpcId, logError: true);
            if (prefab == null) return;
            var info = prefab.CreateCharacterInfo(Rand.RandSync.Unsynced);
            // Team1 的出生包会被客户端自动加入船员；访客沿用原生护送的友好阵营。
            info.TeamID = CharacterTeamType.FriendlyNPC;
            info.Name = TextManager.Get("eventtext.tenshi.v2.name").Value;
            int token = epoch, request = ++a.Request;
            a.Pending = true;
            a.RequestedAt = Timing.TotalTime;
            Entity.Spawner.AddCharacterToSpawnQueue(info.SpeciesName, point?.WorldPosition ?? hull.WorldPosition, info, guest =>
            {
                if (!Current(a, token, request))
                {
                    Entity.Spawner?.AddEntityToRemoveQueue(guest);
                    return;
                }
                a.Pending = false;
                a.Guest = guest;
                try
                {
                    guest.HumanPrefab = prefab;
                    guest.SetOriginalTeamAndChangeTeam(CharacterTeamType.FriendlyNPC, processImmediately: true);
                    prefab.InitializeCharacter(guest, point ?? (ISpatialEntity)hull);
                    prefab.GiveItems(guest, sub, point, Rand.RandSync.Unsynced, createNetworkEvents: true);
                    guest.IsEscorted = true;
                    if (point != null) guest.GiveIdCardTags(point, createNetworkEvent: true);
                    if (guest.AIController is HumanAIController ai)
                    {
                        var idle = ai.ObjectiveManager.GetObjective<AIObjectiveIdle>();
                        idle.Behavior = AIObjectiveIdle.BehaviorType.StayInHull;
                        idle.TargetHull = hull;
                    }
                    foreach (var item in guest.Inventory.FindAllItems(null, recursive: true)) TrackLoan(a, item);
                    a.Participated = a.Mission.State == 0;
                    // 同行也用入组时的电弧发射器；整套借装随任务收回。
                    if (ItemPrefab.Prefabs.TryGet(WeaponId, out var emitter))
                        Entity.Spawner.AddItemToSpawnQueue(emitter, guest.Inventory, onSpawned: weapon =>
                        {
                            if (weapon == null) return;
                            if (!Current(a, token, request) || !Alive(guest))
                            {
                                Entity.Spawner?.AddItemToRemoveQueue(weapon);
                                return;
                            }
                            TrackLoan(a, weapon);
                            if (weapon.OwnInventory != null && ItemPrefab.Prefabs.TryGet(AmmoId, out var ammo))
                                Entity.Spawner.AddItemToSpawnQueue(ammo, weapon.OwnInventory, onSpawned: capacitor =>
                                {
                                    if (capacitor == null) return;
                                    generatedItemOwners.Add(capacitor, new LoanBinding(a, token, request));
                                    if (!Current(a, token, request) || !Alive(guest) || weapon.Removed)
                                    {
                                        Entity.Spawner?.AddItemToRemoveQueue(capacitor);
                                        return;
                                    }
                                    TrackLoan(a, capacitor);
                                });
                        });
                    Say(a, "departure", 1);
                }
                catch
                {
                    foreach (var item in guest.Inventory.FindAllItems(null, recursive: true)) a.Loan.Add(item);
                    Retire(a);
                    throw;
                }
            });
        }

        internal static void ContainedItemSpawned(Inventory inventory, Item item)
        {
            var id = item.Prefab.Identifier;
            if ((id != BulletId && id != PeachId) || !(inventory?.Owner is Item owner) ||
                !generatedItemOwners.TryGetValue(owner, out var binding)) return;
            // 只认借装自己生成的射弹和桃子，不碰玩家塞进来的同名物品。
            if (!((owner.Prefab.Identifier == AmmoId && id == BulletId) ||
                  (owner.Prefab.Identifier == HatId && id == PeachId))) return;
            var a = binding.Owner;
            if (!owner.Removed && Current(a, binding.Epoch, binding.Request) && Alive(a.Guest))
                TrackLoan(a, item);
            else
                Entity.Spawner?.AddItemToRemoveQueue(item);
        }

        private static void TrackLoan(Attempt a, Item item)
        {
            a.Loan.Add(item);
            // 帽子隔一段时间还会产桃，不能只记登船那一刻的背包。
            if (item.Prefab.Identifier == HatId)
                generatedItemOwners.GetValue(item, _ => new LoanBinding(a, epoch, a.Request));
            if (item.OwnInventory != null)
                foreach (var child in item.OwnInventory.FindAllItems(null, recursive: true)) a.Loan.Add(child);
        }

        internal static void Update(MonsterMission mission, float dt)
        {
            if (mission.Prefab.Identifier != MissionId) return;
            if (!attempts.TryGetValue(mission, out var a)) { Start(mission); return; }
            if (a.Pending)
            {
                if (Timing.TotalTime - a.RequestedAt < 10) return;
                a.Pending = false;
                a.Request++;
            }
            if (a.Guest == null) { Spawn(a); return; }
            if (!Alive(a.Guest)) return;
            a.Elapsed += dt;
            if (a.Elapsed < 0.5f) return;
            a.Elapsed = 0f;
            if (mission.monsters.Any(m => Alive(m) &&
                Vector2.DistanceSquared(m.WorldPosition, a.Guest.WorldPosition) < 2000f * 2000f))
                Say(a, "approach", 2);
            if (Hull.HullList.Any(h => h.Submarine == Submarine.MainSub && h.Volume > 0f && h.WaterVolume > h.Volume * 0.2f))
                Say(a, "leak", 4);
            if (mission.State > 0) Say(a, "victory", 8);
        }

        private static void Say(Attempt a, string key, int bit)
        {
            if (Alive(a.Guest) && a.Cues.Take(bit))
                a.Guest.Speak(TextManager.Get("eventtext.tenshi.v2." + key).Value,
                    identifier: ("tenshi_voyage_" + key).ToIdentifier());
        }

        internal static bool CanFinish(MonsterMission mission, bool cleared, CampaignMode.TransitionType transition)
        {
            if (!attempts.TryGetValue(mission, out var a)) return false;
            if (a.OwnerLevel != Level.Loaded) return false;
            var sub = Submarine.MainSub;
            var guestSub = a.Guest?.CurrentHull?.Submarine;
            bool aboard = sub != null && guestSub != null && (guestSub == sub || sub.DockedTo.Contains(guestSub));
            bool returning = transition == CampaignMode.TransitionType.ReturnToPreviousLocation ||
                transition == CampaignMode.TransitionType.ReturnToPreviousEmptyLocation;
            bool atExit = returning ? sub?.AtStartExit == true : sub?.AtEndExit == true;
            return VoyageRules.CanFinish(cleared, a.Participated, Alive(a.Guest), aboard,
                atExit, transition != CampaignMode.TransitionType.None);
        }

        internal static void End(MonsterMission mission, CampaignMode.TransitionType transition)
        {
            if (!attempts.TryGetValue(mission, out var a)) return;
            try
            {
                if (CanFinish(mission, mission.Completed, transition))
                    GameMain.GameSession?.Campaign?.CampaignMetadata.SetValue("Tenshi_Voyage_Completed".ToIdentifier(), true);
            }
            finally
            {
                attempts.Remove(mission);
                Retire(a);
            }
        }

        private static void Retire(Attempt a)
        {
            a.Request++;
            // 先取出临时背包里混入的玩家物品，再收回临时装备。
            var foreign = new HashSet<Item>();
            if (a.Guest?.Inventory != null)
                foreach (var item in a.Guest.Inventory.FindAllItems(null, recursive: true))
                    if (!a.Loan.Contains(item)) foreign.Add(item);
            foreach (var loan in a.Loan)
                if (!loan.Removed && loan.OwnInventory != null)
                    foreach (var child in loan.OwnInventory.FindAllItems(null, recursive: true))
                        if (!a.Loan.Contains(child)) foreign.Add(child);
            foreach (var item in foreign)
            {
                if (item.Removed) continue;
                bool carriedByForeignBag = false;
                Item current = item;
                for (int depth = 0; depth < 64 && current.ParentInventory?.Owner is Item parent; depth++)
                {
                    if (a.Loan.Contains(parent)) break;
                    if (foreign.Contains(parent)) { carriedByForeignBag = true; break; }
                    current = parent;
                }
                if (!carriedByForeignBag) item.Drop(null);
            }
            var spawner = Entity.Spawner;
            foreach (var item in a.Loan)
                if (!item.Removed)
                {
                    if (spawner != null) spawner.AddItemToRemoveQueue(item);
                    else item.Remove();
                }
            a.Loan.Clear();
            // DespawnNow 会生成人类遗物袋；访客只是离船，不是死亡消失。
            if (a.Guest != null && !a.Guest.Removed)
            {
                if (spawner != null) spawner.AddEntityToRemoveQueue(a.Guest);
                else a.Guest.Remove();
            }
            // 和旧撤场一样在保存前处理完队列，联机移除也走原生事件。
            spawner?.Update(createNetworkEvents: true);
        }

        internal static void BeginRound()
        {
            // StartMission 可能早于 roundStart；保留本关刚排好的访客，不再排第二次。
            foreach (var a in attempts.Values.Where(a => a.OwnerLevel != Level.Loaded ||
                GameMain.GameSession?.Missions.Contains(a.Mission) != true).ToList())
            {
                attempts.Remove(a.Mission);
                Retire(a);
            }
        }

        internal static void Clear()
        {
            epoch++;
            foreach (var a in attempts.Values) Retire(a);
            attempts.Clear();
        }
    }
#endif
}
