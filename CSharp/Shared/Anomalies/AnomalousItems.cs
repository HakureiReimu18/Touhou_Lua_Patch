using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using FarseerPhysics;
using Microsoft.Xna.Framework;

namespace Touhou.Anomalies
{
    public sealed class AnomalousItemsPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private bool loaded;
        public void Initialize() => harmony = new Harmony(
#if SERVER
            "touhou.anomalies.server");
#else
            "touhou.anomalies.client");
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            if (loaded) return;
            loaded = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(AnomalyComponentLoadPatch));
            harmony.PatchAll(typeof(AnomalyParentChangedPatch));
            harmony.PatchAll(typeof(AnomalyTrumpetUsePatch));
            harmony.PatchAll(typeof(AnomalyDeferredCleanupPatch));
#if CLIENT
            harmony.PatchAll(typeof(AnomalySubtitlePatch));
#endif
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.Anomalies.End", OnRoundEnd);
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.Anomalies.Start", OnRoundStart);
        }
        private static object OnRoundEnd(object[] args)
        {
            AnomalyRuntime.Clear();
            return null;
        }
        private static object OnRoundStart(object[] args)
        {
            AnomalyRuntime.StartRound();
            return null;
        }
        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.Anomalies.End");
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.Anomalies.Start");
            harmony?.UnpatchSelf();
            AnomalyRuntime.Clear();
            loaded = false;
        }
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class AnomalyComponentLoadPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
            // 本地开服会装两份程序集，只让当前上下文创建组件。
            if (!AnomalyRuntime.OwnContext ||
                !string.Equals(element.Name.ToString(), "TouhouAnomaly", StringComparison.OrdinalIgnoreCase)) return true;
            try { __result = new TouhouAnomaly(item, element); }
            catch (Exception ex)
            {
                AnomalyRuntime.Warn("异常组件加载失败", ex);
                __result = null;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.ParentInventory), MethodType.Setter)]
    internal static class AnomalyParentChangedPatch
    {
        private static void Postfix(Item __instance)
        {
            try { AnomalyRuntime.ParentChanged(__instance); }
            catch (Exception ex) { AnomalyRuntime.Warn("收容状态更新失败", ex); }
        }
    }

    [HarmonyPatch(typeof(RangedWeapon), nameof(RangedWeapon.Use))]
    internal static class AnomalyTrumpetUsePatch
    {
        private static void Postfix(RangedWeapon __instance, Character character, bool __result)
        {
            if (!__result || !AnomalyRuntime.Authority || __instance.Item.Prefab.Identifier != AnomalyRuntime.TrumpetId) return;
            try { AnomalyRuntime.Blow(__instance.Item, character); }
            catch (Exception ex) { AnomalyRuntime.Warn("喇叭换位失败", ex); }
        }
    }

#if SERVER
    [HarmonyPatch(typeof(GameServer), "Update", new[] { typeof(float) })]
#else
    [HarmonyPatch(typeof(GameMain), "Update")]
#endif
    internal static class AnomalyDeferredCleanupPatch
    {
        private static void Postfix()
        {
            if (!AnomalyRuntime.OwnContext || !AnomalyRuntime.IsMainThread) return;
            // 最后一件样本也可能在加载线程删除，不能指望还有物品来驱动撤场。
            AnomalyRuntime.DrainRemoved();
        }
    }

#if CLIENT
    [HarmonyPatch(typeof(GameClient), nameof(GameClient.AddChatMessage), new[] { typeof(ChatMessage) })]
    internal static class AnomalySubtitlePatch
    {
        private static bool Prefix(ChatMessage message)
        {
            if (!AnomalyRuntime.OwnContext || message == null || message.Type != ChatMessageType.Server ||
                message.Sender != null || message.SenderName == null ||
                !message.SenderName.StartsWith(AnomalyRuntime.SubtitleSenderPrefix, StringComparison.Ordinal)) return true;
            try
            {
                AnomalyRuntime.ShowSubtitle(message.Text);
                return false;
            }
            catch (Exception ex)
            {
                // 字幕没画出来就留在聊天栏，也别退回要点 OK 的窗口。
                AnomalyRuntime.Warn("异常字幕显示失败，保留聊天消息", ex);
                return true;
            }
        }
    }
#endif

    public sealed class TouhouAnomaly : ItemComponent
    {
        internal enum Kind { Crate, ReturningBox, Cabinet, Stone }
        internal readonly Kind Mode;
        internal bool Contained;
        internal volatile bool Disposed;
        private LightComponent light;
        private float elapsed, away, cabinetRemaining = 180f, pendingAge;
        private bool cabinetHasContent, exchanging;
        private bool initialized;
        private volatile bool inventoryDirty;
        private int exchangeToken;
        private readonly List<Item> cabinetCandidates = new List<Item>();
        internal readonly Dictionary<Character, NoticeTimes> Notices = new Dictionary<Character, NoticeTimes>();
        internal double CorpseNoticeAt;

        internal sealed class NoticeTimes { internal double EntryAt, WhisperAt; }

        // 已收容的组件仍得检查祖先容器，声音则只跟 IsActive 走。
        public override bool UpdateWhenInactive => Mode != Kind.Crate;

        public TouhouAnomaly(Item item, ContentXElement element) : base(item, element)
        {
            string id = item.Prefab.Identifier.Value;
            Mode = string.Equals(id, "Touhou_Containment_Crate", StringComparison.OrdinalIgnoreCase) ? Kind.Crate
                : string.Equals(id, "Touhou_Anomaly_Returning_Box", StringComparison.OrdinalIgnoreCase) ? Kind.ReturningBox
                : string.Equals(id, "Touhou_Anomaly_Bigger_Cabinet", StringComparison.OrdinalIgnoreCase) ? Kind.Cabinet : Kind.Stone;
            // 读档可能在加载线程，先留一次主线程更新来登记和初始化。
            IsActive = true;
        }

        public override void OnItemLoaded()
        {
            if (!AnomalyRuntime.IsMainThread) return;
            EnsureInitialized();
        }

        private void EnsureInitialized()
        {
            if (initialized || Disposed) return;
            initialized = true;
            try
            {
                AnomalyRuntime.Register(this);
                light = item.GetComponent<LightComponent>();
                RefreshContainment();
                OnInventoryChanged();
            }
            catch (Exception ex) { AnomalyRuntime.Warn("异常物品初始化失败", ex); }
        }

        public override void OnInventoryChanged()
        {
            if (Disposed) return;
            if (!AnomalyRuntime.IsMainThread)
            {
                inventoryDirty = true;
                IsActive = true;
                return;
            }
            if (!initialized) { EnsureInitialized(); return; }
            inventoryDirty = false;
            try
            {
                if (Mode == Kind.Crate)
                {
                    SetLight(AnomalyRuntime.HasProtectedContents(item.OwnInventory));
                    IsActive = false;
                }
                else if (Mode == Kind.Cabinet && !exchanging)
                    RefreshCabinetContent();
            }
            catch (Exception ex) { AnomalyRuntime.Warn("异常容器内容更新失败", ex); }
        }

        internal void RefreshContainment()
        {
            if (Disposed || item.Removed) return;
            bool contained = AnomalyRuntime.IsContained(item);
            bool changed = contained != Contained;
            Contained = contained;
            IsActive = Mode != Kind.Crate && !contained;
            if (Mode == Kind.ReturningBox || Mode == Kind.Stone) SetLight(!contained);
            if (contained) away = 0f;
            if (changed && Mode == Kind.Stone && AnomalyRuntime.Authority)
                AnomalyRuntime.RebuildFields(false);
        }

        private void SetLight(bool on)
        {
            light ??= item.GetComponent<LightComponent>();
            if (light != null && light.IsOn != on) light.IsOn = on;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (Disposed || item.Removed || item.IsInRemoveQueue || !AnomalyRuntime.IsMainThread || AnomalyRuntime.Paused ||
                !float.IsFinite(deltaTime) || deltaTime <= 0f) return;
            try
            {
                EnsureInitialized();
                if (inventoryDirty) { RefreshContainment(); OnInventoryChanged(); }
                if (Mode == Kind.Crate) return;
                if (Mode == Kind.Stone && AnomalyRuntime.Authority)
                    AnomalyRuntime.TickFields(deltaTime);
                elapsed += deltaTime;
                if (elapsed < 1f) return;
                float step = elapsed;
                elapsed = 0f;
                RefreshContainment();
                if (!AnomalyRuntime.Authority) return;
                if (Mode == Kind.ReturningBox) UpdateReturn(step);
                else if (Mode == Kind.Cabinet) UpdateCabinet(step);
            }
            catch (Exception ex) { AnomalyRuntime.Warn("异常物品更新失败：" + item.Prefab.Identifier, ex); }
        }

        private void UpdateReturn(float step)
        {
            var root = AnomalyRuntime.RootOwner(item);
            var main = Submarine.MainSub;
            if (Contained || main == null || root.Submarine == main ||
                AnomalyRuntime.HasWatcher(root.WorldPosition))
            {
                away = 0f;
                return;
            }
            away += step;
            if (away < 90f) return;
            if (AnomalyRuntime.TryReturn(item, main)) away = 0f;
        }

        private void RefreshCabinetContent()
        {
            CollectCabinetCandidates();
            bool has = cabinetCandidates.Count > 0;
            if (!has || !cabinetHasContent) cabinetRemaining = 180f;
            cabinetHasContent = has;
        }

        private void CollectCabinetCandidates()
        {
            cabinetCandidates.Clear();
            if (item.OwnInventory == null) return;
            foreach (var candidate in item.OwnInventory.AllItems)
                if (AnomalyRuntime.Exchangeable(candidate) && !cabinetCandidates.Contains(candidate))
                    cabinetCandidates.Add(candidate);
        }

        private void UpdateCabinet(float step)
        {
            if (exchanging)
            {
                pendingAge += step;
                if (pendingAge < 10f) return;
                // 生成队列若迟迟不回调，旧令牌作废；迟到的新物品会被移除。
                exchangeToken++;
                exchanging = false;
                RefreshCabinetContent();
                return;
            }
            if (Contained || !cabinetHasContent || Entity.Spawner == null) return;
            cabinetRemaining -= step;
            if (cabinetRemaining > 0f) return;
            cabinetRemaining = 180f;
            CollectCabinetCandidates();
            if (cabinetCandidates.Count == 0) { RefreshCabinetContent(); return; }
            var old = cabinetCandidates[AnomalyRuntime.Random.Next(cabinetCandidates.Count)];
            var rewardId = AnomalyRuntime.Loot[AnomalyRuntime.Random.Next(AnomalyRuntime.Loot.Length)];
            var prefab = ItemPrefab.Prefabs.Find(p => p.Identifier == rewardId);
            if (prefab == null) return;
            exchanging = true;
            pendingAge = 0f;
            int token = ++exchangeToken, epoch = AnomalyRuntime.Epoch;
            try
            {
                Entity.Spawner.AddItemToSpawnQueue(prefab, item.WorldPosition,
                    onSpawned: reward => FinishExchange(old, reward, token, epoch));
            }
            catch { exchanging = false; throw; }
        }

        private void FinishExchange(Item old, Item reward, int token, int epoch)
        {
            bool committed = false;
            var inventory = item.OwnInventory;
            int slot = -1;
            bool detached = false;
            try
            {
                if (Disposed || item.Removed || item.IsInRemoveQueue || epoch != AnomalyRuntime.Epoch ||
                    token != exchangeToken || !AnomalyRuntime.Authority || AnomalyRuntime.IsContained(item) ||
                    inventory == null || old.ParentInventory != inventory || !AnomalyRuntime.Exchangeable(old)) return;
                slot = inventory.FindIndex(old);
                if (slot < 0) return;
                inventory.RemoveItem(old);
                detached = true;
                if (!inventory.TryPutItem(reward, slot, false, false, null,
                    createNetworkEvent: false, triggerOnInsertedEffects: false) || reward.ParentInventory != inventory ||
                    Disposed || item.Removed || item.IsInRemoveQueue || AnomalyRuntime.IsContained(item) ||
                    !AnomalyRuntime.Exchangeable(old)) return;
                Entity.Spawner.AddItemToRemoveQueue(old);
                committed = old.IsInRemoveQueue;
                if (committed)
                    AnomalyRuntime.NotifyNearby(item.WorldPosition, "柜子里有些东西不太一样了。", 800f);
            }
            catch (Exception ex) { AnomalyRuntime.Warn("柜子置换失败，保留旧物", ex); }
            finally
            {
                if (!committed)
                {
                    if (reward != null && !reward.Removed)
                    {
                        try
                        {
                            reward.ParentInventory?.RemoveItem(reward);
                            Entity.Spawner?.AddItemToRemoveQueue(reward);
                        }
                        catch (Exception ex) { AnomalyRuntime.Warn("置换奖励清理失败", ex); }
                    }
                    if (detached && !old.Removed && !old.IsInRemoveQueue && old.ParentInventory == null)
                    {
                        bool restored = false;
                        try
                        {
                            restored = !inventory.Owner.Removed && inventory.TryPutItem(old, slot, false, false, null,
                                createNetworkEvent: false, triggerOnInsertedEffects: false);
                        }
                        catch (Exception ex) { AnomalyRuntime.Warn("旧物回装失败", ex); }
                        if (!restored)
                        {
                            try
                            {
                                old.Drop(null, setTransform: false);
                                AnomalyRuntime.MoveItem(old, item.WorldPosition, item.Submarine);
                            }
                            catch (Exception ex) { AnomalyRuntime.Warn("旧物落地失败，未删除", ex); }
                        }
                    }
                }
                if (detached && !item.Removed)
                {
                    try { inventory.CreateNetworkEvent(); }
                    catch (Exception ex) { AnomalyRuntime.Warn("柜子库存同步失败", ex); }
                }
                if (token == exchangeToken)
                {
                    exchanging = false;
                    OnInventoryChanged();
                }
            }
        }

        internal void Forget()
        {
            Disposed = true;
            IsActive = false;
            exchangeToken++;
            Notices.Clear();
            cabinetCandidates.Clear();
        }

        public override void RemoveComponentSpecific()
        {
            try { AnomalyRuntime.Unregister(this); }
            catch (Exception ex) { AnomalyRuntime.Warn("异常物品清理失败", ex); }
            finally { base.RemoveComponentSpecific(); }
        }
    }

    internal static class AnomalyRuntime
    {
        internal const string SubtitleSenderPrefix = "Touhou.Anomalies.Subtitle:";
        internal static readonly Random Random = new Random();
        internal static readonly Identifier TrumpetId = "Touhou_Anomaly_Laugh_Trumpet".ToIdentifier();
        private static readonly Identifier ContainmentTag = "Touhou_Containment".ToIdentifier();
        private static readonly Identifier AnomalyTag = "Touhou_Anomalous".ToIdentifier();
        private static readonly Identifier ArtifactTag = "alienartifact".ToIdentifier();
        private static readonly Identifier RepulseId = "Touhou_Anomaly_Repulse".ToIdentifier();
        internal static readonly Identifier[] Loot = {
            "battery".ToIdentifier(), "Byproduct_Alchemical".ToIdentifier(), "flashlight".ToIdentifier(),
            "Kappa_Miningdrill".ToIdentifier(), "ethanol".ToIdentifier(), "rum".ToIdentifier(),
            "titanium".ToIdentifier(), "Touhou_Magic_Potion_Candy".ToIdentifier(),
            "Plant_Essential_Oil".ToIdentifier(), "Touhou_Apple".ToIdentifier()
        };
        private static readonly HashSet<Identifier> Swappable = new HashSet<Identifier> {
            "human".ToIdentifier(), "crawler".ToIdentifier(), "mudraptor".ToIdentifier(), "humanhusk".ToIdentifier()
        };
        private static readonly string[] ReturnMessages = {
            "……气闸附近多了一个箱子。你记得它被留在了上个站点。",
            "箱子又在响。这次它离你更近了。",
            "它不再敲箱壁了。它在敲舱壁。",
            "它回来了。没有人讨论这件事。"
        };
        private static readonly string[] Whispers = {
            "你在它的记忆里。它记得每一个走到五十米的人。",
            "再靠近一步。它想再看看你。——别听它的。",
            "五十米不是它撑开的距离。是它剩下的。"
        };
        private static readonly string[] CorpseMessages = {
            "死亡被婉拒了。", "尸体不在登记范围内。样本不收。", "它把死者送了出去。它说它不是墓地。"
        };
        private static readonly string[] SwapUser = {
            "圣母点名了。上台吧。", "你换了个位置。这不是你选的。", "喇叭声停下的时候，你已经在别处了。"
        };
        private static readonly string[] SwapOther = {
            "你和别人换了位置。", "你听见喇叭声从你原来的位置传来。", "圣母今晚点了你的名。"
        };
        private static readonly Dictionary<Item, TouhouAnomaly> Components = new Dictionary<Item, TouhouAnomaly>();
        private static readonly ConcurrentQueue<TouhouAnomaly> RemovedComponents = new ConcurrentQueue<TouhouAnomaly>();
        private static readonly List<TouhouAnomaly> Stones = new List<TouhouAnomaly>();
        private static readonly List<(TouhouAnomaly Source, Vector2 Position)> ActiveStones =
            new List<(TouhouAnomaly, Vector2)>();
        private static readonly Dictionary<Character, float> FieldTargets = new Dictionary<Character, float>();
        private static readonly Dictionary<Character, float> Desired = new Dictionary<Character, float>();
        private static readonly Dictionary<Character, float> ClosestStone = new Dictionary<Character, float>();
        private static readonly List<Character> ToRemove = new List<Character>();
        private sealed class BlowStamp { internal double Time = double.NaN; }
        private static ConditionalWeakTable<Item, BlowStamp> lastBlow = new ConditionalWeakTable<Item, BlowStamp>();
        private static float fieldElapsed;
        private static double lastFieldFrame = double.NaN, fieldClock, nextWarning;
        private static bool rebuilding;
        internal static int Epoch { get; private set; }
        internal static bool IsMainThread => GameMain.MainThread == null || Thread.CurrentThread == GameMain.MainThread;
        internal static bool OwnContext =>
#if SERVER
            GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static bool Paused =>
#if SERVER
            false;
#else
            GameMain.Instance != null && GameMain.Instance.Paused;
#endif

        internal static void Warn(string text, Exception ex)
        {
            if (Timing.TotalTime < nextWarning) return;
            nextWarning = Timing.TotalTime + 5;
            try { DebugConsole.NewMessage("[异常物品] " + text + "：" + ex.Message, Color.Orange); }
            catch { }
        }
        internal static void Register(TouhouAnomaly component)
        {
            DrainRemoved();
            Components[component.Item] = component;
            if (component.Mode == TouhouAnomaly.Kind.Stone) Stones.Add(component);
        }
        internal static void Unregister(TouhouAnomaly component)
        {
            if (!IsMainThread)
            {
                component.Disposed = true;
                RemovedComponents.Enqueue(component);
                return;
            }
            component.Forget();
            Components.Remove(component.Item);
            Stones.Remove(component);
            if (Authority && component.Mode == TouhouAnomaly.Kind.Stone) RebuildFields(false);
        }
        internal static void DrainRemoved()
        {
            while (RemovedComponents.TryDequeue(out var component))
            {
                try { Unregister(component); }
                catch (Exception ex) { Warn("延迟撤场失败", ex); }
            }
        }
        internal static void StartRound()
        {
            if (!Authority) return;
            // 老 Lua 留在存档里的不适现在不再衰减，开局先清一次，避免永久残留。
            foreach (var c in Character.CharacterList) SetField(c, 0f);
            RebuildFields(false);
        }
        internal static void Clear()
        {
            Epoch++;
            if (Authority)
                foreach (var target in FieldTargets.Keys)
                {
                    try { SetField(target, 0f); }
                    catch (Exception ex) { Warn("领域残留清理失败", ex); }
                }
            foreach (var component in Components.Values) component.Forget();
            while (RemovedComponents.TryDequeue(out var removed)) removed.Forget();
            Components.Clear();
            Stones.Clear();
            ActiveStones.Clear();
            FieldTargets.Clear();
            Desired.Clear();
            ClosestStone.Clear();
            ToRemove.Clear();
            lastBlow = new ConditionalWeakTable<Item, BlowStamp>();
            fieldElapsed = 0f;
            fieldClock = 0;
            lastFieldFrame = double.NaN;
            nextWarning = 0;
        }

        internal static Entity RootOwner(Item item)
        {
            Entity root = item;
            for (int depth = 0; depth < 64 && root is Item current && current.ParentInventory?.Owner != null; depth++)
                root = current.ParentInventory.Owner;
            return root;
        }
        internal static bool IsContained(Item item)
        {
            for (int depth = 0; depth < 64 && item.ParentInventory?.Owner is Item parent; depth++)
            {
                if (parent.HasTag(ContainmentTag)) return true;
                if (parent == item) return true;
                item = parent;
            }
            // 过深或成环时先按收容处理，别让未知父链放出效果。
            return item.ParentInventory?.Owner is Item;
        }
        internal static void ParentChanged(Item moved)
        {
            if (!IsMainThread) return;
            DrainRemoved();
            if (Components.Count == 0) return;
            if (Components.TryGetValue(moved, out var own)) own.RefreshContainment();
            if (moved.OwnInventory == null) return;
            foreach (var component in Components.Values)
            {
                Item child = component.Item;
                for (int depth = 0; depth < 64 && child.ParentInventory?.Owner is Item parent; depth++)
                {
                    if (parent == moved) { component.RefreshContainment(); break; }
                    child = parent;
                }
            }
        }
        internal static bool HasProtectedContents(Inventory inventory, int depth = 0)
        {
            if (inventory == null) return false;
            if (depth >= 64) return true;
            foreach (var child in inventory.AllItems)
                if (child != null && !child.Removed &&
                    (child.HasTag(AnomalyTag) || child.HasTag(ArtifactTag) ||
                     HasProtectedContents(child.OwnInventory, depth + 1))) return true;
            return false;
        }
        internal static bool Exchangeable(Item item) => item != null && !item.Removed && !item.IsInRemoveQueue &&
            !item.HasTag(AnomalyTag) && !item.HasTag(ArtifactTag) && !item.HasTag(ContainmentTag) &&
            !HasProtectedContents(item.OwnInventory);

        internal static bool HasWatcher(Vector2 position)
        {
            if (GameMain.NetworkMember != null)
            {
                foreach (var client in GameMain.NetworkMember.ConnectedClients)
                    if (Living(client.Character) && Vector2.DistanceSquared(position, client.Character.WorldPosition) < 2000f * 2000f)
                        return true;
            }
#if CLIENT
            if (Living(Character.Controlled) &&
                Vector2.DistanceSquared(position, Character.Controlled.WorldPosition) < 2000f * 2000f) return true;
#endif
            return false;
        }
        private static bool Living(Character c) => c != null && !c.Removed && !c.IsDead;

        internal static void Notify(Character target, string text, string sender = "???")
        {
            if (!Authority || target == null || target.Removed || string.IsNullOrWhiteSpace(text)) return;
            // 沿用原生定向消息的收件人和去重，客户端只把本系统的提示改成字幕。
#if SERVER
            foreach (var client in GameMain.Server.ConnectedClients)
                if (client.Character == target)
                    GameMain.Server.SendDirectChatMessage(
                        ChatMessage.Create(SubtitleSenderPrefix + sender, text, ChatMessageType.Server, null), client);
#else
            if (target == Character.Controlled)
                ShowSubtitle(text);
#endif
        }
#if CLIENT
        internal static void ShowSubtitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // 和异传书走同一个顶部滚动队列，低语不用再叠一声 UI 提示音。
            GUI.AddMessage(text, new Color(135, 206, 250), 8f, playSound: false);
        }
#endif
        internal static void NotifyNearby(Vector2 position, string text, float radius)
        {
#if SERVER
            foreach (var client in GameMain.Server.ConnectedClients)
                if (Living(client.Character) && Vector2.DistanceSquared(position, client.Character.WorldPosition) < radius * radius)
                    Notify(client.Character, text);
#else
            if (Living(Character.Controlled) && Vector2.DistanceSquared(position, Character.Controlled.WorldPosition) < radius * radius)
                Notify(Character.Controlled, text);
#endif
        }
        internal static void MoveItem(Item item, Vector2 worldPosition, Submarine sub)
        {
            item.Submarine = sub;
            if (item.body != null) item.body.Submarine = sub;
            item.SetTransform(ConvertUnits.ToSimUnits(worldPosition - (sub?.Position ?? Vector2.Zero)),
                0f, findNewHull: false);
            item.FindHull();
            item.SetContainedItemPositions();
            item.body?.ResetDynamics();
            item.PositionUpdateInterval = 0f;
        }
        internal static bool TryReturn(Item item, Submarine main)
        {
            if (item.body == null || item.IsInRemoveQueue || IsContained(item)) return false;
            Character target = null;
            int count = 0;
            foreach (var c in Character.CharacterList)
                if (Living(c) && c.IsHuman && c.IsOnPlayerTeam && c.Submarine == main && c.CurrentHull != null &&
                    Random.Next(++count) == 0) target = c;
            if (target == null) return false;
            Vector2 destination = target.WorldPosition;
            bool found = false;
            float halfWidth = Math.Max(30f, item.Rect.Width * 0.5f), halfHeight = Math.Max(25f, item.Rect.Height * 0.5f);
            for (int i = 0; i < 24; i++)
            {
                var point = i == 23 ? target.WorldPosition : target.WorldPosition +
                    new Vector2(Random.Next(-600, 601), Random.Next(-600, 601));
                var hull = Hull.FindHull(point, target.CurrentHull);
                if (hull == null || hull.Submarine != main) continue;
                var rect = hull.WorldRect;
                if (point.X < rect.X + halfWidth || point.X > rect.Right - halfWidth ||
                    point.Y > rect.Y - halfHeight || point.Y < rect.Y - rect.Height + halfHeight) continue;
                destination = point;
                found = true;
                break;
            }
            if (!found) return false;
            var oldInventory = item.ParentInventory;
            var oldPosition = item.WorldPosition;
            var oldSub = item.Submarine;
            try
            {
                item.Drop(null, setTransform: false);
                MoveItem(item, destination, main);
                if (item.ParentInventory != null || item.Submarine != main ||
                    Vector2.DistanceSquared(item.WorldPosition, destination) > 4f) throw new InvalidOperationException("回归落点未确认");
            }
            catch (Exception ex)
            {
                MoveItem(item, oldPosition, oldSub);
                if (oldInventory != null && !oldInventory.Owner.Removed) oldInventory.TryPutItem(item, null);
                Warn("货箱回归失败，已尝试还原", ex);
                return false;
            }
            var memory = item.GetComponent<MemoryComponent>();
            int stage = 0;
            if (memory != null)
            {
                int.TryParse(memory.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out stage);
                stage = Math.Min(Math.Max(0, stage), int.MaxValue - 1) + 1;
                memory.Value = stage.ToString(CultureInfo.InvariantCulture);
#if SERVER
                item.CreateServerEvent(memory);
#endif
            }
            Notify(target, ReturnMessages[Math.Clamp(stage - 1, 0, ReturnMessages.Length - 1)]);
            return true;
        }

        internal static void TickFields(float deltaTime)
        {
            DrainRemoved();
            if (lastFieldFrame == Timing.TotalTime) return;
            lastFieldFrame = Timing.TotalTime;
            fieldClock += deltaTime;
            fieldElapsed += deltaTime;
            if (fieldElapsed < 1f) return;
            float step = Math.Min(fieldElapsed, 2f);
            fieldElapsed = 0f;
            RebuildFields(true, step);
        }
        private static bool ActiveStone(TouhouAnomaly stone) => !stone.Disposed && !stone.Item.Removed &&
            !stone.Item.IsInRemoveQueue && !IsContained(stone.Item);

        internal static void RebuildFields(bool messages, float exposureSeconds = 0f)
        {
            if (!Authority || !IsMainThread || rebuilding || (Stones.Count == 0 && FieldTargets.Count == 0)) return;
            rebuilding = true;
            try
            {
                Desired.Clear();
                ClosestStone.Clear();
                ActiveStones.Clear();
                foreach (var stone in Stones)
                    if (ActiveStone(stone)) ActiveStones.Add((stone, RootOwner(stone.Item).WorldPosition));
                // 全部收容时只撤掉旧效果，不再查场上的角色。
                if (ActiveStones.Count > 0)
                    CollectFieldTargets(messages);
                foreach (var pair in Desired)
                {
                    SetField(pair.Key, pair.Value);
                    if (exposureSeconds > 0f && pair.Key.IsHuman)
                    {
                        float distance = ClosestStone[pair.Key];
                        // 多份样本只取最近一份；装箱事件只撤场，不能顺手再伤一次。
                        float madness = distance < 200f * 200f ? 1f : distance < 300f * 300f ? 0.5f : 0.25f;
                        AddExposure(pair.Key, "psychosis", madness * exposureSeconds);
                        if (distance < 300f * 300f)
                            AddExposure(pair.Key, "organdamage", (distance < 200f * 200f ? 0.75f : 0.15f) * exposureSeconds);
                    }
                }
                foreach (var pair in FieldTargets)
                    if (!Desired.ContainsKey(pair.Key)) SetField(pair.Key, 0f);
                FieldTargets.Clear();
                foreach (var pair in Desired) FieldTargets.Add(pair.Key, pair.Value);
                foreach (var stone in Stones)
                {
                    ToRemove.Clear();
                    foreach (var pair in stone.Notices)
                        if (!Living(pair.Key) || (pair.Value.EntryAt <= fieldClock && pair.Value.WhisperAt <= fieldClock))
                            ToRemove.Add(pair.Key);
                    foreach (var c in ToRemove) stone.Notices.Remove(c);
                }
            }
            finally { rebuilding = false; }
        }
        private static void CollectFieldTargets(bool messages)
        {
            foreach (var c in Character.CharacterList)
            {
                if (c == null || c.Removed) continue;
                var position = c.WorldPosition;
                foreach (var active in ActiveStones)
                {
                    float distance = Vector2.DistanceSquared(active.Position, position);
                    if (distance >= 600f * 600f) continue;
                    if (c.IsDead)
                    {
                        if (messages) RejectCorpse(active.Source, c);
                        // 每具尸体每拍最多送一次，下一拍再复核。
                        break;
                    }
                    float strength = distance < 300f * 300f ? 80f : 30f;
                    if (!Desired.TryGetValue(c, out float current) || strength > current) Desired[c] = strength;
                    if (!ClosestStone.TryGetValue(c, out float closest) || distance < closest) ClosestStone[c] = distance;
                    if (messages) NotifyField(active.Source, c, distance);
                }
            }
        }
        private static void AddExposure(Character c, string id, float amount)
        {
            var prefab = AfflictionPrefab.Prefabs.Find(p => p.Identifier == id.ToIdentifier());
            if (prefab == null || c.CharacterHealth == null || amount <= 0f) return;
            c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(amount));
            c.HealthUpdateInterval = 0f;
        }
        private static void SetField(Character c, float strength)
        {
            if (c == null || c.Removed || c.CharacterHealth == null) return;
            var health = c.CharacterHealth;
            var affliction = health.GetAffliction(RepulseId);
            if (affliction == null && strength > 0f)
            {
                var prefab = AfflictionPrefab.Prefabs.Find(p => p.Identifier == RepulseId);
                if (prefab == null) return;
                health.ApplyAffliction(null, prefab.Instantiate(strength));
                affliction = health.GetAffliction(RepulseId);
            }
            if (affliction == null || affliction.Strength == strength) return;
            // 这是领域档位，不是每秒伤害；最终强度不随最大生命缩放。
            affliction.Strength = Math.Min(strength, affliction.Prefab.MaxStrength);
            c.HealthUpdateInterval = 0f;
        }
        private static void NotifyField(TouhouAnomaly stone, Character c, float distance)
        {
            if (!stone.Notices.TryGetValue(c, out var notice))
                stone.Notices[c] = notice = new TouhouAnomaly.NoticeTimes();
            if (fieldClock >= notice.EntryAt)
            {
                notice.EntryAt = fieldClock + 90;
                Notify(c, "你的胃拧了起来，太阳穴突突地跳。这里有什么东西不希望任何人靠近。");
            }
            if (distance < 200f * 200f && fieldClock >= notice.WhisperAt)
            {
                notice.WhisperAt = fieldClock + 60;
                Notify(c, Whispers[Random.Next(Whispers.Length)]);
            }
        }
        private static bool TryTeleport(Character c, Vector2 destination)
        {
            c.TeleportTo(destination);
            bool success = Vector2.DistanceSquared(c.WorldPosition, destination) <= 4f;
#if SERVER
            if (success)
                foreach (var client in GameMain.Server.ConnectedClients)
                    if (client.InGame) client.PendingPositionUpdates.Enqueue(c);
#endif
            return success;
        }
        private static void RejectCorpse(TouhouAnomaly stone, Character corpse)
        {
            var center = RootOwner(stone.Item).WorldPosition;
            for (int i = 0; i < 16; i++)
            {
                double angle = Random.NextDouble() * Math.PI * 2;
                var destination = center + new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * 1500f;
                bool covered = false;
                foreach (var other in ActiveStones)
                    if (Vector2.DistanceSquared(other.Position, destination) < 600f * 600f)
                    { covered = true; break; }
                if (covered) continue;
                var previous = corpse.WorldPosition;
                try
                {
                    if (!TryTeleport(corpse, destination)) { TryTeleport(corpse, previous); return; }
                }
                catch (Exception ex)
                {
                    try { TryTeleport(corpse, previous); }
                    catch (Exception rollback) { Warn("尸体还原失败", rollback); }
                    Warn("尸体驳回失败，已尝试还原", ex);
                    return;
                }
                if (fieldClock >= stone.CorpseNoticeAt)
                {
                    stone.CorpseNoticeAt = fieldClock + 120;
                    NotifyNearby(center, CorpseMessages[Random.Next(CorpseMessages.Length)], 800f);
                }
                return;
            }
        }
        internal static void Blow(Item trumpet, Character user)
        {
            if (!Authority || trumpet.Removed || trumpet.IsInRemoveQueue || !Living(user) ||
                IsContained(trumpet) || user.Inventory == null ||
                !IsHeld(user, trumpet)) return;
            var stamp = lastBlow.GetValue(trumpet, _ => new BlowStamp());
            if (stamp.Time == Timing.TotalTime) return;
            stamp.Time = Timing.TotalTime;
            Character chosen = null;
            int count = 0;
            bool any = false;
            foreach (var c in Character.CharacterList)
            {
                if (!Living(c) || c == user || Vector2.DistanceSquared(user.WorldPosition, c.WorldPosition) >= 1500f * 1500f) continue;
                any = true;
                if (Swappable.Contains(c.Prefab.Identifier) && Random.Next(++count) == 0) chosen = c;
            }
            if (chosen == null)
            {
                Notify(user, any ? "圣母对那些名字不感兴趣。" : "圣母今晚没有点名任何人。", "喇叭");
                return;
            }
            var userPosition = user.WorldPosition;
            var otherPosition = chosen.WorldPosition;
            try
            {
                if (!TryTeleport(user, otherPosition) || !TryTeleport(chosen, userPosition))
                    throw new InvalidOperationException("换位落点未确认");
            }
            catch (Exception ex)
            {
                try { TryTeleport(user, userPosition); } catch (Exception rollback) { Warn("吹奏者还原失败", rollback); }
                try { TryTeleport(chosen, otherPosition); } catch (Exception rollback) { Warn("点名对象还原失败", rollback); }
                Warn("换位失败，未施加眩晕", ex);
                return;
            }
            user.SetStun(Math.Max(user.Stun, 1.5f));
            chosen.SetStun(Math.Max(chosen.Stun, 1.5f));
            Notify(user, SwapUser[Random.Next(SwapUser.Length)], "喇叭");
            Notify(chosen, SwapOther[Random.Next(SwapOther.Length)], "喇叭");
        }
        private static bool IsHeld(Character character, Item item)
        {
            foreach (var held in character.HeldItems)
                if (held == item) return true;
            return false;
        }
    }
}
