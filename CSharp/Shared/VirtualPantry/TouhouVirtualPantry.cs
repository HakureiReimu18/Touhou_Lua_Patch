using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace Touhou.VirtualPantry
{
    internal sealed class PantryView
    {
        internal uint Key, Prefab;
        internal int Count;
        internal float Condition;
        internal byte Quality;
    }

    // 借 ItemComponent 的 GUI 设施，但故意不塞进 Item.Components —— 原生网络事件靠组件下标定位，
    // 多一个组件就会把取用/附着/库存的帧全错位。
    public sealed partial class TouhouVirtualPantry : ItemComponent
    {
        private PantryLedger ledger = new();
        private XElement unreadState;
        private bool dirty = true, networkDirty = true;
        private float timer, rescanTimer, cookingGrace;
        private int scanSlot;
        private readonly List<PantryView> mirror = new();
        private readonly Dictionary<Fabricator, Dictionary<Identifier, int>> recipeReserves = new();
        private readonly Dictionary<Fabricator, object> recipeVersions = new();
        private readonly Queue<PendingRequest> pendingRequests = new();
        private sealed record PendingRequest(byte Action, uint Key, uint Revision, Character Actor, Client Sender,
            uint ClientToken, uint RequestId);
        internal readonly uint LocalToken = PantryPlugin.AllocateToken();
        internal uint RemoteToken;
        internal bool Retired;
        internal bool Suspended, Draining, Faulted, ForeignManager;
        internal uint Revision = 1;
        internal bool HasStock => unreadState != null || ledger.Total > 0 || !PantryPlugin.Authority && mirror.Any(r => r.Count > 0);
        private ItemContainer Container => item.GetComponent<ItemContainer>();
        private Inventory Inventory => Container?.Inventory;

        public TouhouVirtualPantry(Item item, ContentXElement element) : base(item, element)
        {
            IsActive = true;
            UpdateWhenBroken = true;
            CanBeSelected = true;
        }

        partial void InitGui();
        partial void RefreshGui();
        partial void RemoveGui();

        public override void OnMapLoaded()
        {
            base.OnMapLoaded();
            dirty = networkDirty = true;
        }

        public override void OnInventoryChanged() { dirty = true; }

        public override void Clone(ItemComponent original)
        {
            if (original is not TouhouVirtualPantry pantry) { return; }
            var parent = new XElement("Clone");
            Load(new ContentXElement(item.Prefab.ConfigElement.ContentPackage, pantry.Save(parent)),
                usePrefabValues: false, idRemap: IdRemap.DiscardId, isItemSwap: false);
        }

        public override XElement Save(XElement parentElement)
        {
            // unreadState 是读档失败时原样扣下的旧数据，写回时照抄，不能当空库处理
            XElement state = unreadState != null ? new XElement(unreadState) : ledger.Save(Suspended, Draining);
            parentElement.Add(state);
            return state;
        }

        public override void Load(ContentXElement componentElement, bool usePrefabValues, IdRemap idRemap, bool isItemSwap)
        {
            // 自家格式不依赖 prefab 变化，也不看格子数
            XElement state = componentElement.Element;
            try
            {
                bool suspended = (bool?)state.Attribute("suspended") ?? false;
                bool draining = (bool?)state.Attribute("draining") ?? false;
                PantryLedger replacement = PantryLedger.Load(state);
                ledger = replacement;
                Suspended = suspended;
                Draining = draining;
                unreadState = null;
                Faulted = false;
                ForeignManager = false;
            }
            catch (Exception e)
            {
                unreadState = new XElement(state);
                Faulted = Suspended = true;
                DebugConsole.AddWarning("[东方食材虚拟柜] 存档无法读取，已保留原数据并停止转换：" + e.Message);
            }
            dirty = networkDirty = true;
        }

        public override void Update(float deltaTime, Camera cam)
        {
            if (Retired) { return; }
            timer -= deltaTime;
            if (timer > 0) { return; }
            timer = 0.25f;
            if (PantryPlugin.Authority) { PantryPlugin.Schedule(this); }
            RefreshGui();
        }

        internal void Tick()
        {
            if (Retired) { return; }
            if (PantryPlugin.Authority && GameMain.GameSession?.IsRunning == true && Inventory != null && !item.Removed)
            {
                while (pendingRequests.Count > 0)
                {
                    PendingRequest request = pendingRequests.Dequeue();
                    // 旧摘要照收：桶键不复用，数量和空间都是现在核对。
                    // 反过来按 revision 一律拒掉，会让两个人同时操作时互相饿死。
                    if (request.Revision > Revision || request.Sender != null &&
                        (!item.CanClientAccess(request.Sender) || !PantryNet.IsCurrent(this, request.Sender, request.ClientToken, request.RequestId)))
                    { continue; }
                    try { ApplyRequest(request.Action, request.Key, request.Sender?.Character ?? request.Actor); }
                    catch (Exception e)
                    {
                        Faulted = Suspended = true;
                        Touch();
                        DebugConsole.AddWarning("[东方食材虚拟柜] 请求未完成，库存已暂停：" + e.Message);
                    }
                }
                // 别的模组（IO Storage 一族）给同一口柜子挂了管理组件时让位，免得两套账本互相踩
                bool conflict = item.Components.Any(c => c != this &&
                    (c.GetType().Name == "SlotStackManager" || c.GetType().Name == "StorageBoxConverterComponent" ||
                     c.GetType().Name == "StorageTerminalComponent" || c.GetType().Name == "DataCardComponent"));
                if (conflict && !ForeignManager)
                {
                    ForeignManager = true;
                    Faulted = Suspended = true;
                    Draining = false;
                    Touch();
                    DebugConsole.AddWarning("[东方食材虚拟柜] 柜子含 IO Storage 组件，已停止本扩展收纳；请还原库存后只保留一个虚拟化管理器。");
                }
                rescanTimer -= 0.25f;
                if (rescanTimer <= 0) { dirty = true; rescanTimer = 2; }
                if (!ForeignManager && unreadState == null && (!Faulted || Draining))
                {
                    Dictionary<Identifier, int> demand = CookingDemand();
                    if (dirty || Draining || demand.Count > 0 || cookingGrace > 0)
                    {
                        try { Reconcile(demand); }
                        catch (Exception e)
                        {
                            Faulted = Suspended = true;
                            Touch();
                            DebugConsole.AddWarning("[东方食材虚拟柜] 转换已暂停：" + e.Message);
                        }
                    }
                }
                if (networkDirty)
                {
                    networkDirty = false;
                    PantryNet.Publish(this);
                }
            }
            RefreshGui();
        }

        // 加工台要什么就先给什么：按配方最大需求量留实物，其余照常虚拟化。
        private Dictionary<Identifier, int> CookingDemand()
        {
            var result = new Dictionary<Identifier, int>();
            PantryPlugin.Fabricators.RemoveAll(f => f.Item.Removed);
            foreach (Fabricator f in PantryPlugin.Fabricators)
            {
                if (f.Item.linkedTo == null || !f.Item.linkedTo.Contains(item)) { continue; }
                Character user = Character.CharacterList.FirstOrDefault(c => !c.IsDead &&
                    (ShowsDevice(c.SelectedItem, f.Item) || ShowsDevice(c.SelectedSecondaryItem, f.Item)));
                if (!f.IsActive && user == null) { continue; }
                if (user != null && !Container.HasRequiredItems(user, addMessage: false)) { continue; }
                if (f.fabricationRecipes == null) { continue; }
                // 配方表换过引用就重算；每台机器只留一份缓存
                if (!recipeReserves.TryGetValue(f, out var reserve) ||
                    !recipeVersions.TryGetValue(f, out object version) || !ReferenceEquals(version, f.fabricationRecipes))
                {
                    reserve = new Dictionary<Identifier, int>();
                    foreach (var recipe in f.fabricationRecipes.Values)
                    {
                        var perRecipe = new Dictionary<Identifier, int>();
                        foreach (var required in recipe.RequiredItems)
                        {
                            foreach (ItemPrefab prefab in required.ItemPrefabs)
                            {
                                perRecipe.TryGetValue(prefab.Identifier, out int amount);
                                perRecipe[prefab.Identifier] = amount + required.Amount;
                            }
                        }
                        foreach (var pair in perRecipe)
                        {
                            reserve.TryGetValue(pair.Key, out int amount);
                            reserve[pair.Key] = Math.Max(amount, pair.Value);
                        }
                    }
                    recipeReserves[f] = reserve;
                    recipeVersions[f] = f.fabricationRecipes;
                }
                foreach (var pair in reserve)
                {
                    result.TryGetValue(pair.Key, out int amount);
                    result[pair.Key] = Math.Max(amount, pair.Value);
                }
            }
            // 做菜时配方表会短暂闪空，留 2 秒宽限别把刚补的实物又收回去
            if (result.Count > 0) { cookingGrace = 2; }
            else { cookingGrace = Math.Max(0, cookingGrace - 0.25f); }
            return result;
        }

        private static bool ShowsDevice(Item selected, Item device)
            => selected == device || selected != null && device.DisplaySideBySideWhenLinked &&
                selected.linkedTo.Contains(device) && selected.Prefab.IsLinkAllowed(device.Prefab);

        // 只有"原版东方食材/料理/饮料、纯 Pickable+Holdable+Quality"才敢虚拟化。
        // 任务物品、据点货、动态标签、子容器、连线、额外组件都可能被别的系统按实体追踪，一律留实物。
        private bool Eligible(Item food)
        {
            if (food == null || food.Removed || food.IsInRemoveQueue || !food.ShouldBeSaved || food.Condition <= 0 ||
                food.NonInteractable || food.NonPlayerTeamInteractable ||
                !float.IsFinite(food.ConditionPercentage) ||
                food.IsSalvageMissionItem || food.SpawnedInCurrentOutpost || food.StolenDuringRound ||
                food.OwnInventory != null || food.linkedTo?.Count > 0 || food.ParentInventory != Inventory ||
                !food.Prefab.Identifier.Value.StartsWith("touhou_", StringComparison.OrdinalIgnoreCase) ||
                !(food.HasTag("touhou_food_ingredients") || food.HasTag("touhou_cuisine") || food.HasTag("touhou_drinks")))
            { return false; }
            if (!food.Prefab.Tags.SetEquals(food.GetTags())) { return false; }
            return food.Components.All(c => c.GetType() == typeof(Pickable) || c.GetType() == typeof(Holdable) || c.GetType() == typeof(Quality));
        }

        private static XElement Snapshot(Item food)
        {
            var parent = new XElement("Snapshot");
            XElement saved = food.Save(parent);
            if ((bool?)saved.Attribute("markedfordeconstruction") == true) { return null; }
            return PantryLedger.Normalize(saved);
        }

        private static int TemplateQuality(XElement template)
            => (int?)template.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Quality", StringComparison.OrdinalIgnoreCase))
                ?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("QualityLevel", StringComparison.OrdinalIgnoreCase)) ?? 0;

        private static (Identifier, int, int) BufferKey(Item food)
            => (food.Prefab.Identifier, food.Quality, (int)MathF.Round(food.ConditionPercentage * 1000));

        private static (Identifier, int, int) BufferKey(PantryLedger.Bucket bucket)
            => (bucket.Identifier.ToIdentifier(), TemplateQuality(bucket.Template),
                (int)MathF.Round(((float?)bucket.Template.Attribute("conditionpercentage") ?? 100) * 1000));

        // 一趟扫描同时干两件事：把多余的实物收进账本，把缺的实物还原出来。
        // 每 0.25 秒最多转换 16 件，避免一次搬进几百件食材时集中生成/删除实体。
        private void Reconcile(Dictionary<Identifier, int> demand)
        {
            // 空柜子可能已经被塞进 IO 的输入端或箱子。别在那层转换器接手之后
            // 再给它建一套藏在里面的账本。
            if (Inventory.Locked || item.ParentInventory?.Owner is Item) { return; }
            int budget = 16;
            var physical = new Dictionary<uint, int>();
            var physicalKeys = new HashSet<uint>();
            var buffers = new Dictionary<(Identifier, int, int), int>();
            foreach (Item food in Inventory.AllItems)
            {
                var key = BufferKey(food);
                buffers.TryGetValue(key, out int count);
                buffers[key] = count + 1;
            }
            // 一趟有上限的扫描找锚点；压实之后通常只剩几堆。
            // 每格都轮到，免得第一大堆把后面的食材种类饿死。
            bool incomplete = false;
            int inspected = 0;
            for (int n = 0; n < Inventory.Capacity; n++)
            {
                int slot = (scanSlot + n) % Inventory.Capacity;
                foreach (Item food in Inventory.GetItemsAt(slot).ToArray())
                {
                    if (!Eligible(food)) { continue; }
                    if (++inspected > 192) { scanSlot = slot; incomplete = true; break; }
                    XElement snapshot = Snapshot(food);
                    if (snapshot == null) { continue; }
                    PantryLedger.Bucket bucket = ledger.FindOrAdd(snapshot);
                    if (bucket == null) { continue; }
                    physical.TryGetValue(bucket.Key, out int count);
                    physicalKeys.Add(bucket.Key);
                    int reserve = 1;
                    if (demand.TryGetValue(food.Prefab.Identifier, out int needed)) { reserve = Math.Max(1, needed); }
                    // 加工台正在开/收的一批不能抽走缓冲；暂停、出错、还原中也一律留实物
                    bool keep = Suspended || Faulted || Draining || cookingGrace > 0 && demand.Count == 0 ||
                                count < reserve || bucket.Count >= PantryLedger.MaxStock || ledger.Total >= PantryLedger.MaxStock;
                    if (keep || budget <= 0) { physical[bucket.Key] = count + 1; continue; }
                    // 删除和记账之间不能有 yield 或异步回调：先入队删除再记账，
                    // 并发存档时会出现同一件东西双份库存。
                    try { RemovePhysical(food); }
                    finally
                    {
                        if (food.Removed)
                        {
                            bucket.Count++;
                            var key = BufferKey(bucket);
                            if (buffers.TryGetValue(key, out int remaining)) { buffers[key] = remaining - 1; }
                            Touch();
                        }
                    }
                    if (!food.Removed) { throw new InvalidOperationException("实体移除未完成，未增加虚拟数量。"); }
                    budget--;
                }
                if (incomplete) { break; }
            }
            if (!incomplete)
            {
                scanSlot = (scanSlot + 1) % Math.Max(1, Inventory.Capacity);
                ledger.Prune(physicalKeys);
                foreach (PantryLedger.Bucket bucket in ledger.Buckets.ToArray())
                {
                    if (bucket.Count <= 0 || budget <= 0) { continue; }
                    ItemPrefab prefab = ItemPrefab.Find(null, bucket.Identifier.ToIdentifier());
                    if (prefab == null) { continue; } // 缺 mod 的库存原样留着，等 mod 回来再取
                    // 缓冲按原版的匹配字段算，不按整份 XML：mod 更新后还原物的新默认值
                    // 可能和老模板不一样，但也必须能看见并停止自动补货。
                    var bufferKey = BufferKey(bucket);
                    buffers.TryGetValue(bufferKey, out int count);
                    int target = Draining ? int.MaxValue : 1;
                    if (!Draining && demand.TryGetValue(prefab.Identifier, out int needed)) { target = Math.Max(1, needed); }
                    while (count < target && bucket.Count > 0 && budget-- > 0)
                    {
                        if (!Materialize(bucket, Inventory)) { break; }
                        count++;
                        buffers[bufferKey] = count;
                    }
                }
            }
            dirty = incomplete || budget <= 0;
            if (Draining && ledger.Total == 0) { Draining = false; Suspended = true; Touch(); }
        }

        private static void RemovePhysical(Item food)
        {
#if SERVER
            food.SendPendingNetworkUpdates();
            Entity.Spawner.CreateNetworkEvent(new EntitySpawner.RemoveEntity(food));
#endif
            food.Remove();
        }

        private bool Materialize(PantryLedger.Bucket bucket, Inventory destination)
        {
            if (bucket.Count <= 0 || destination == null || destination.Locked || destination.Owner.Removed) { return false; }
            ItemPrefab prefab = ItemPrefab.Find(null, bucket.Identifier.ToIdentifier());
            if (prefab == null || !CanRestoreInto(bucket, prefab, destination)) { return false; }
            var template = new XElement(bucket.Template);
            template.SetAttributeValue("ID", 0);
            Item spawned = null;
            bool announced = false;
#if SERVER
            using var events = new PantrySpawnEvents(prefab);
#endif
            try
            {
                // 先恢复并放进目标格，再发 SpawnEntity：它的构造函数当场记录库存和格号，
                // 早发就会把"还在空中的物品"写进网络事件。
                spawned = Item.Load(new ContentXElement(prefab.ConfigElement.ContentPackage, template),
                    destination.Owner.Submarine, createNetworkEvent: false, idRemap: IdRemap.DiscardId);
                if (spawned == null) { return false; }
                spawned.SetTransform(destination.Owner.SimPosition, 0, findNewHull: false);
                spawned.CurrentHull = destination.Owner is Item owner ? owner.CurrentHull : (destination.Owner as Character)?.CurrentHull;
                if (!PlaceRestored(spawned, destination))
                {
                    spawned.Remove(); // 还没公布过，不能补发移除事件
                    return false;
                }
#if SERVER
                if (GameMain.Server != null)
                {
                    announced = true;
                    events.Publish(spawned);
                    destination.CreateNetworkEvent();
                }
#endif
                bucket.Count--; // 放稳了才扣库存
                Touch();
                return true;
            }
            catch
            {
                if (spawned != null && !spawned.Removed)
                {
                    if (announced) { RemovePhysical(spawned); }
                    else { spawned.Remove(); }
                }
                throw;
            }
        }

        private static bool CanRestoreInto(PantryLedger.Bucket bucket, ItemPrefab prefab, Inventory destination)
        {
            int quality = TemplateQuality(bucket.Template);
            float condition = (float?)bucket.Template.Attribute("conditionpercentage") ?? 100;
            for (int i = 0; i < destination.Capacity; i++)
            {
                Item first = destination.GetItemsAt(i).FirstOrDefault();
                if (first == null)
                {
                    if (destination.CanBePutInSlot(prefab, i, quality: quality)) { return true; }
                }
                else if (first.Prefab == prefab && first.Quality == quality &&
                    Math.Abs(first.ConditionPercentage - condition) < 0.0001f &&
                    destination.CanBePutInSlot(first, i, ignoreCondition: true))
                { return true; }
            }
            return false;
        }

        private static bool PlaceRestored(Item food, Inventory destination)
        {
            // 原版库存通常只允许满耐久/坏掉的物品叠放。这里只让"同 prefab、同品质、
            // 同耐久和耐久上限"的实物共格，不调食物 Combine、不改耐久、不绕容器上限。
            for (int i = 0; i < destination.Capacity; i++)
            {
                var stack = destination.GetItemsAt(i).ToArray();
                if (stack.Length == 0 || stack.Any(other => other.Prefab != food.Prefab || other.Quality != food.Quality ||
                    Math.Abs(other.Condition - food.Condition) >= 0.0001f ||
                    Math.Abs(other.MaxCondition - food.MaxCondition) >= 0.0001f)) { continue; }
                if (destination.CanBePutInSlot(food, i, ignoreCondition: true) &&
                    destination.TryPutItem(food, i, allowSwapping: false, allowCombine: false, user: null,
                        createNetworkEvent: false, ignoreCondition: true, triggerOnInsertedEffects: false))
                { return true; }
            }
            if (destination is CharacterInventory characterInventory)
            {
                // Inventory.FindAllowedSlot 不认人物装备格类型，会返回空的头部/衣服格；
                // 交回原版人物摆放流程，让它优先普通 Any 格并遵守 AllowedSlots。
                return characterInventory.TryPutItem(food, null, food.AllowedSlots,
                    createNetworkEvent: false, triggerOnInsertedEffects: false);
            }
            int slot = destination.FindAllowedSlot(food);
            return slot >= 0 && destination.TryPutItem(food, slot, allowSwapping: false, allowCombine: false, user: null,
                createNetworkEvent: false, triggerOnInsertedEffects: false);
        }

        private void Touch()
        {
            Revision++;
            networkDirty = true;
        }

        internal List<PantryView> View()
        {
            if (!PantryPlugin.Authority) { return mirror; }
            return ledger.Buckets.Where(b => b.Count > 0).Select(b =>
            {
                ItemPrefab prefab = ItemPrefab.Find(null, b.Identifier.ToIdentifier());
                float condition = (float?)b.Template.Attribute("conditionpercentage") ?? 100;
                return new PantryView { Key = b.Key, Prefab = prefab?.UintIdentifier ?? 0, Count = b.Count,
                    Condition = Math.Clamp(condition, 0, 100),
                    Quality = (byte)Math.Clamp(TemplateQuality(b.Template), 0, 255) };
            }).ToList();
        }

        internal void Request(byte action, uint key = 0)
        {
            if (PantryPlugin.Authority)
            {
#if CLIENT
                // 单机/主机端：直接排队给 Tick 处理，上限 8 条防连点刷屏
                if (pendingRequests.Count < 8)
                {
                    pendingRequests.Enqueue(new PendingRequest(action, key, Revision, Character.Controlled, null, 0, 0));
                    PantryPlugin.Schedule(this);
                }
#endif
                return;
            }
            PantryNet.SendRequest(this, action, key);
        }

        internal void QueueRemoteRequest(byte action, uint key, uint revision, Client sender, uint clientToken, uint requestId)
        {
            if (Retired || pendingRequests.Count >= 8) { return; }
            pendingRequests.Enqueue(new PendingRequest(action, key, revision, null, sender, clientToken, requestId));
            networkDirty = true;
            PantryPlugin.Schedule(this);
        }

        private void ApplyRequest(byte action, uint key, Character actor)
        {
            // 服务端重新核对发起人、柜子访问权限和当前空间；客户端说啥都不全信
            if (actor == null || actor.IsDead || actor.IsUnconscious ||
                (actor.SelectedItem != item && actor.SelectedSecondaryItem != item) ||
                !actor.CanAccessInventory(Inventory) || !Container.HasRequiredItems(actor, addMessage: false) || unreadState != null)
            { return; }
            if (action == 3)
            {
                if (ForeignManager) { return; }
                Suspended = Draining = true;
                dirty = true;
                Touch();
            }
            else if (action == 4 && !Faulted)
            {
                Suspended = Draining = false;
                dirty = true;
                Touch();
            }
            else if ((action == 1 || action == 2) && (!Faulted || ForeignManager))
            {
                PantryLedger.Bucket bucket = ledger.Buckets.FirstOrDefault(b => b.Key == key);
                if (bucket == null) { return; }
                int amount = action == 1 ? 1 : 32;
                for (int n = 0; n < amount && bucket.Count > 0; n++)
                {
                    if (!Materialize(bucket, actor.Inventory)) { break; }
                }
            }
        }

        internal void WriteSnapshot(IWriteMessage msg)
        {
            List<PantryView> rows = View();
            msg.WriteUInt32(Revision);
            msg.WriteByte((byte)((Suspended ? 1 : 0) | (Draining ? 2 : 0) | (Faulted ? 4 : 0) | (ForeignManager ? 8 : 0)));
            msg.WriteByte((byte)rows.Count);
            foreach (PantryView row in rows)
            {
                msg.WriteUInt32(row.Key);
                msg.WriteUInt32(row.Prefab);
                msg.WriteInt32(row.Count);
                msg.WriteSingle(row.Condition);
                msg.WriteByte(row.Quality);
            }
        }

        internal void ReadSnapshot(IReadMessage msg)
        {
            uint revision = msg.ReadUInt32();
            byte flags = msg.ReadByte();
            int count = msg.ReadByte();
            var rows = new List<PantryView>();
            for (int i = 0; i < count; i++)
            {
                rows.Add(new PantryView { Key = msg.ReadUInt32(), Prefab = msg.ReadUInt32(), Count = msg.ReadInt32(),
                    Condition = msg.ReadSingle(), Quality = msg.ReadByte() });
            }
            // 客户端只存镜像，账本永远在权威端
            if (PantryPlugin.Authority || count > PantryLedger.MaxBuckets) { return; }
            Revision = revision;
            Suspended = (flags & 1) != 0;
            Draining = (flags & 2) != 0;
            Faulted = (flags & 4) != 0;
            ForeignManager = (flags & 8) != 0;
            mirror.Clear();
            mirror.AddRange(rows);
        }

        public override void RemoveComponentSpecific()
        {
            PantryNet.Forget(this);
            pendingRequests.Clear();
            recipeReserves.Clear();
            recipeVersions.Clear();
            mirror.Clear();
            RemoveGui();
            base.RemoveComponentSpecific();
        }
    }
}
