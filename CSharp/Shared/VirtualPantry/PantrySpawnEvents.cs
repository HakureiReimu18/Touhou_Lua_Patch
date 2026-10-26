#if SERVER
using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace Touhou.VirtualPantry
{
    // Item.Load 就算 createNetworkEvent=false 也可能顺手发属性/状态事件。
    // 还原出来的物品先攒着，等它的原生生成事件发出去以后再按原顺序补发。
    internal sealed class PantrySpawnEvents : IDisposable
    {
        [ThreadStatic] private static PantrySpawnEvents current;
        private readonly PantrySpawnEvents previous;
        private readonly ItemPrefab prefab;
        private Item restored;
        private readonly List<(GameServer Server, INetSerializable Entity, NetEntityEvent.IData Data)> pending = new();

        internal PantrySpawnEvents(ItemPrefab prefab)
        {
            this.prefab = prefab;
            previous = current;
            current = this;
        }

        internal static void Register(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.CreateEntityEvent),
                new[] { typeof(INetSerializable), typeof(NetEntityEvent.IData) }),
                prefix: new HarmonyMethod(typeof(PantrySpawnEvents), nameof(Capture)) { priority = Priority.First });
        }

        internal static void Track(Item item)
        {
            if (current != null && current.restored == null && item.Prefab == current.prefab) { current.restored = item; }
        }

        private static bool Capture(GameServer __instance, INetSerializable entity, NetEntityEvent.IData extraData)
        {
            if (current == null || !ReferenceEquals(current.restored, entity)) { return true; }
            current.pending.Add((__instance, entity, extraData));
            return false;
        }

        internal void Publish(Item item)
        {
            current = previous;
            Entity.Spawner.CreateNetworkEvent(new EntitySpawner.SpawnEntity(item));
            foreach (var entry in pending) { entry.Server.CreateEntityEvent(entry.Entity, entry.Data); }
            pending.Clear();
            // 生成数据带品质、不带耐久；loadingRound 让恢复的耐久不会重放损坏/断裂效果
            item.CreateStatusEvent(loadingRound: true);
        }

        public void Dispose()
        {
            if (current == this) { current = previous; }
            pending.Clear();
        }
    }
}
#endif
