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
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.VirtualPantry
{
    // 东方食材柜虚拟库存的入口：挂生命周期补丁、管调度队列。
    // 柜子实际在跑的账本/扫描在 TouhouVirtualPantry.cs，协议在 PantryNet.cs。
    //
    // 从独立包「东方食材柜虚拟存储」并入本补丁（2026-10）。两个包同时启用会让一口柜子
    // 被两套账本各管一遍，所以这里一旦发现独立包在场就直接躺平。
    public sealed class PantryPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        internal static readonly List<Fabricator> Fabricators = new();
        private static readonly Queue<TouhouVirtualPantry> due = new();
        private static readonly HashSet<TouhouVirtualPantry> scheduled = new();
        private static ConditionalWeakTable<Item, TouhouVirtualPantry> pantries = new();
        private static readonly HashSet<TouhouVirtualPantry> active = new();
        private static int nextToken;
        private static bool standaloneSeen;
        internal static bool Authority => GameMain.NetworkMember?.IsClient != true;
        internal static uint AllocateToken() => unchecked((uint)Interlocked.Increment(ref nextToken));
        internal static TouhouVirtualPantry Find(Item owner)
            => owner != null && pantries.TryGetValue(owner, out var pantry) ? pantry : null;

        // 独立包在两端都定义了同名类型。LuaCs 按内容包分别编译，类型不会撞在一起，
        // 但两边都往同一口柜子挂组件 —— 所以补丁这边先检查、先让步。
        private static bool StandalonePresent()
        {
            if (standaloneSeen) { return true; }
            Assembly self = typeof(PantryPlugin).Assembly;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == self) { continue; }
                if (assembly.GetType("Touhou.VirtualPantry.PantryPlugin", throwOnError: false) != null)
                {
                    standaloneSeen = true;
                    return true;
                }
            }
            return false;
        }

        public void Initialize()
        {
            if (StandalonePresent())
            {
                DebugConsole.NewMessage("[东方食材虚拟柜] 检测到独立包仍启用，本补丁不接管；请两个包二选一。", Color.Yellow);
                return;
            }
            harmony = new Harmony("touhou.virtualpantry.v1");
            try
            {
                harmony.Patch(AccessTools.Constructor(typeof(Item), new[] {
                    typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) }),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(ItemCreated)));
                harmony.Patch(AccessTools.Constructor(typeof(Fabricator), new[] { typeof(Item), typeof(ContentXElement) }),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(FabricatorCreated)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Save), new[] { typeof(XElement) }),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(Saved)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Load),
                    new[] { typeof(ContentXElement), typeof(Submarine), typeof(bool), typeof(IdRemap) }),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(Loaded)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Clone), Type.EmptyTypes),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(Cloned)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.OnMapLoaded), Type.EmptyTypes),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(MapLoaded)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.Remove), Type.EmptyTypes),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(Removed)));
                harmony.Patch(AccessTools.Method(typeof(Item), nameof(Item.ShallowRemove), Type.EmptyTypes),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(ShallowRemoved)));
                harmony.Patch(AccessTools.Method(typeof(Inventory), "NotifyItemComponentsOfChange"),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(InventoryChanged)));
                harmony.Patch(AccessTools.Method(typeof(Deconstructor), "ProcessItem"),
                    prefix: Guard(nameof(CanDeconstruct)));
                harmony.Patch(AccessTools.Method(typeof(Deconstructor), "Update"),
                    prefix: Guard(nameof(DeconstructorUpdating)));
                harmony.Patch(AccessTools.Method(typeof(MapEntity), "UpdateAll", new[] { typeof(float), typeof(Camera) }),
                    postfix: new HarmonyMethod(typeof(PantryPlugin), nameof(Flush)) { priority = Priority.Last });
                foreach (Type type in new[] { typeof(Inventory), typeof(ItemInventory) })
                {
                    foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == "TryPutItem"))
                    {
                        harmony.Patch(method, prefix: Guard(nameof(CanNest)));
                    }
                }
                // 补丁可能在柜子已经建好之后才加载（换艇/读档中途重载），补扫一遍现有物品
                foreach (var item in Item.ItemList)
                {
                    ItemCreated(item);
                    foreach (var fabricator in item.GetComponents<Fabricator>()) { FabricatorCreated(fabricator); }
                }
#if CLIENT
                PantryGuiHooks.Register(harmony);
#endif
#if SERVER
                PantrySpawnEvents.Register(harmony);
#endif
                PantryNet.Register();
            }
            catch { Dispose(); throw; }
            DebugConsole.NewMessage("[东方食材虚拟柜] 已加载；仅处理东方食材柜。停用扩展前请先停止收纳并全部还原。", Color.LightGreen);
        }

        private static void ItemCreated(Item __instance)
        {
            if (standaloneSeen) { return; }
#if SERVER
            // 还原的是食材，不是柜子；先登记，别让属性事件抢在生成事件前面。
            PantrySpawnEvents.Track(__instance);
#endif
            if (__instance.Prefab.Identifier != "touhou_ingredient_refrigerator".ToIdentifier() || Find(__instance) != null) { return; }
            var element = new ContentXElement(__instance.Prefab.ConfigElement.ContentPackage,
                new XElement("TouhouVirtualPantry", new XAttribute("canbeselected", true)));
            // 保持原组件数量和顺序不变：原版 Item 事件只用下标编码，对端不传组件类型也不传数量。
            var pantry = new TouhouVirtualPantry(__instance, element);
            pantries.Add(__instance, pantry);
            active.Add(pantry);
        }

        private static void Saved(Item __instance, XElement __result) => Find(__instance)?.Save(__result);

        private static void Loaded(ContentXElement element, Item __result)
        {
            TouhouVirtualPantry pantry = Find(__result);
            XElement saved = element.Element.Elements().FirstOrDefault(e => e.Name.LocalName == "TouhouVirtualPantry");
            if (pantry != null && saved != null)
            {
                pantry.Load(new ContentXElement(element.ContentPackage, saved), false, IdRemap.DiscardId, false);
            }
        }

        private static void Cloned(Item __instance, MapEntity __result)
        {
            if (__result is Item clone && Find(__instance) is { } source) { Find(clone)?.Clone(source); }
        }

        private static void MapLoaded(Item __instance) => Find(__instance)?.OnMapLoaded();
        private static void InventoryChanged(Inventory __instance)
        {
            if (__instance.Owner is Item owner) { Find(owner)?.OnInventoryChanged(); }
        }

        private static void Retire(Item owner, bool forget)
        {
            var pantry = Find(owner);
            if (pantry == null) { return; }
            pantry.Retired = true;
            pantry.RemoveComponentSpecific();
            active.Remove(pantry);
            scheduled.Remove(pantry);
            if (forget) { pantries.Remove(owner); }
        }

        private static void Removed(Item __instance) => Retire(__instance, true);
        private static void ShallowRemoved(Item __instance) => Retire(__instance, false);

        private static void FabricatorCreated(Fabricator __instance)
        {
            if (!Fabricators.Contains(__instance)) { Fabricators.Add(__instance); }
        }

        private static bool CanDeconstruct(Item targetItem)
            => Find(targetItem)?.HasStock != true;

        private static HarmonyMethod Guard(string method)
            => new HarmonyMethod(typeof(PantryPlugin), method)
            {
                priority = Priority.First,
                before = new[] { "com.iostorage.mod" }
            };

        internal static void Schedule(TouhouVirtualPantry pantry)
        {
            if (!pantry.Retired && scheduled.Add(pantry)) { due.Enqueue(pantry); }
        }

        private static void Flush(float deltaTime, Camera cam)
        {
            foreach (TouhouVirtualPantry pantry in active) { pantry.Update(deltaTime, cam); }
            // Item.Update 跑在 foreach (Item.ItemList) 里，在那儿生成/移除 Item 会让枚举器失效。
            // 所有转换都挪到这一轮迭代之后同步做完。
            for (int n = 0; n < 4 && due.Count > 0; n++)
            {
                TouhouVirtualPantry pantry = due.Dequeue();
                scheduled.Remove(pantry);
                if (!pantry.Retired && !pantry.Item.Removed) { pantry.Tick(); }
            }
        }

        private static bool CanNest(Inventory __instance, Item item, ref bool __result)
        {
            // 也挡住 IO 通过箱子/储物柜的递归扁平化。人手是安全的：
            // 原版 Character.SaveInventory 会连柜子组件一起保存。
            if (__instance.Owner is Item && Find(item)?.HasStock == true)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static bool DeconstructorUpdating(Deconstructor __instance)
        {
            if (!Authority || !__instance.IsActive) { return true; }
            if (__instance.InputContainer?.Inventory.AllItems.Any(i => Find(i)?.HasStock == true) == true)
            {
                __instance.SetActive(false);
                return false;
            }
            return true;
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            PantryNet.Dispose();
            foreach (var pantry in active) { pantry.Retired = true; pantry.RemoveComponentSpecific(); }
            active.Clear();
            pantries = new ConditionalWeakTable<Item, TouhouVirtualPantry>();
            Fabricators.Clear();
            due.Clear();
            scheduled.Clear();
        }
    }
}
