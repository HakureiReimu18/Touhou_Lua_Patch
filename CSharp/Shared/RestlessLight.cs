using System;
#if !RESTLESS_LIGHT_POLICY_TEST
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;
#if CLIENT
using Barotrauma.Lights;
#endif
#endif

namespace Touhou.Anomalies
{
    internal sealed class RestlessLightClock
    {
        internal const float EmpInterval = 6f, ShockInterval = 4f, EscapeGrace = 18f, EscapeInterval = 8f;
        private float emp = EmpInterval, shock = ShockInterval, exposure, blink;
        internal struct Pulses { internal bool Emp, Shock, Blink; }
        internal void Reset() { emp = EmpInterval; shock = ShockInterval; exposure = blink = 0f; }
        internal Pulses Step(float dt, bool contained, bool humanNearby)
        {
            var result = new Pulses();
            if (!float.IsFinite(dt) || dt <= 0f) return result;
            if (contained)
            {
                Reset();
                return result;
            }
            dt = Math.Min(dt, 0.5f);
            emp -= dt; shock -= dt;
            if (emp <= 0f) { result.Emp = true; emp = EmpInterval; }
            if (shock <= 0f) { result.Shock = true; shock = ShockInterval; }
            if (!humanNearby) { exposure = blink = 0f; return result; }
            exposure += dt;
            if (exposure < EscapeGrace) return result;
            blink -= dt;
            if (blink <= 0f) { result.Blink = true; blink = EscapeInterval; }
            return result;
        }
        internal static bool ShieldTag(string tag) =>
            string.Equals(tag, "artifactcontainer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "Touhou_Containment", StringComparison.OrdinalIgnoreCase);
        internal static bool AncestorShielded<T>(T source, Func<T, T> parentOf, Func<T, bool> shields) where T : class
        {
            T current = source;
            for (int depth = 0; depth < 64; depth++)
            {
                T parent = parentOf(current);
                if (parent == null) return false;
                if (ReferenceEquals(parent, source) || shields(parent)) return true;
                current = parent;
            }
            return true;
        }
        internal static float Falloff(float distance, float radius) =>
            radius > 0f ? Math.Clamp(1f - distance / radius, 0f, 1f) : 0f;
        internal static bool EmpTarget(bool light, bool vulnerable, bool repairable, bool battery) =>
            !light && vulnerable && (repairable || battery);
    }

#if !RESTLESS_LIGHT_POLICY_TEST
    public sealed class RestlessLightPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize() => harmony = new Harmony("touhou.restlesslight." +
#if SERVER
            "server");
#else
            "client");
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(RestlessLightLoadPatch));
            harmony.PatchAll(typeof(RestlessLightMissionPatch));
            harmony.PatchAll(typeof(RestlessLightSpawnPatch));
            harmony.PatchAll(typeof(RestlessLightFailurePatch));
            harmony.PatchAll(typeof(RestlessLightUpdatePatch));
            RestlessLightRuntime.Enabled = true;
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.RestlessLight.End", Clear);
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.RestlessLight.Start", Clear);
        }
        private static object Clear(object[] args) { RestlessLightRuntime.Clear(); return null; }
        public void Dispose()
        {
            RestlessLightRuntime.Enabled = false;
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.RestlessLight.End");
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.RestlessLight.Start");
            harmony?.UnpatchSelf();
            RestlessLightRuntime.Clear();
        }
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class RestlessLightLoadPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
            if (!AnomalyRuntime.OwnContext ||
                !string.Equals(element.Name.ToString(), "TouhouRestlessLight", StringComparison.OrdinalIgnoreCase)) return true;
            try { __result = new TouhouRestlessLight(item, element); }
            catch (Exception ex) { AnomalyRuntime.Warn("异常灯组件加载失败", ex); __result = null; }
            return false;
        }
    }

    [HarmonyPatch(typeof(SalvageMission), "DetermineCompleted")]
    internal static class RestlessLightMissionPatch
    {
        private static void Postfix(SalvageMission __instance, ref bool __result)
        {
            if (__instance.Prefab.Identifier != RestlessLightRuntime.MissionId) return;
            // 结算时查本次任务的实物，不能拿“曾经装进箱子”当完成凭证。
            __result = __result && __instance.targets.Count == 1 &&
                RestlessLightRuntime.Shielded(__instance.targets[0].Item);
        }
    }

    [HarmonyPatch(typeof(SalvageMission), "StartMissionSpecific")]
    internal static class RestlessLightSpawnPatch
    {
        private static void Postfix(SalvageMission __instance)
        {
            if (!AnomalyRuntime.Authority || __instance.Prefab.Identifier != RestlessLightRuntime.MissionId ||
                __instance.targets.Count != 1) return;
            Item lamp = __instance.targets[0].Item;
            if (lamp == null || lamp.Removed || lamp.ParentInventory != null) return;
            // 有的沉船没有 locker 标签，补一个普通箱子，不改潜艇文件。
            var point = WayPoint.WayPointList.FirstOrDefault(w => w.Submarine?.Info.Type == SubmarineType.Wreck && w.CurrentHull != null);
            if (point == null || Entity.Spawner == null ||
                !ItemPrefab.Prefabs.TryGet("metalcrate".ToIdentifier(), out var crate)) return;
            AnomalyRuntime.MoveItem(lamp, point.WorldPosition, point.Submarine);
            int token = RestlessLightRuntime.Epoch;
            Entity.Spawner.AddItemToSpawnQueue(crate, point.WorldPosition, point.Submarine, onSpawned: box =>
            {
                if (box == null || box.Removed) return;
                if (token != RestlessLightRuntime.Epoch || lamp.Removed || lamp.ParentInventory != null)
                {
                    Entity.Spawner?.AddItemToRemoveQueue(box);
                    return;
                }
                box.OwnInventory?.TryPutItem(lamp, null);
            });
        }
    }

    [HarmonyPatch(typeof(SalvageMission), "EndMissionSpecific")]
    internal static class RestlessLightFailurePatch
    {
        private static void Postfix(SalvageMission __instance, bool completed)
        {
            if (!AnomalyRuntime.Authority || completed || __instance.Prefab.Identifier != RestlessLightRuntime.MissionId) return;
            // 失败重接不能越积越多；只清本次目标，不动玩家的箱子。
            foreach (var target in __instance.targets)
                if (target.Item != null && !target.Item.Removed && target.Item.Prefab.Identifier == RestlessLightRuntime.LampId)
                    target.Item.Remove();
        }
    }

#if SERVER
    [HarmonyPatch(typeof(GameServer), "Update", new[] { typeof(float) })]
#else
    [HarmonyPatch(typeof(GameMain), "Update")]
#endif
    internal static class RestlessLightUpdatePatch
    {
        private static void Postfix()
        {
            if (!RestlessLightRuntime.Enabled || !AnomalyRuntime.OwnContext || !AnomalyRuntime.IsMainThread) return;
            try
            {
                RestlessLightRuntime.DrainRemoved();
                if (AnomalyRuntime.Authority) RestlessLightRuntime.TickReport();
            }
            catch (Exception ex) { AnomalyRuntime.Warn("异常灯记录或撤场处理失败", ex); }
        }
    }

    public sealed class TouhouRestlessLight : ItemComponent
    {
        private readonly RestlessLightClock clock = new RestlessLightClock();
        private volatile bool disposed;
        internal bool Gone => disposed || item.Removed || item.IsInRemoveQueue;
#if CLIENT
        private LightSource glow;
        private readonly Dictionary<Item, LightSource> borrowed = new Dictionary<Item, LightSource>();
        private float visualScan;
#endif
        public override bool UpdateWhenInactive => true;
        public TouhouRestlessLight(Item item, ContentXElement element) : base(item, element) { IsActive = true; }
        public override void Update(float deltaTime, Camera cam)
        {
            try { Tick(deltaTime); }
            catch (Exception ex)
            {
                try { ClearVisuals(); } catch { }
                AnomalyRuntime.Warn("异常灯更新失败", ex);
            }
        }
        private void Tick(float deltaTime)
        {
            if (disposed || !RestlessLightRuntime.Enabled || !AnomalyRuntime.OwnContext || !AnomalyRuntime.IsMainThread ||
                AnomalyRuntime.Paused || item.Removed || item.IsInRemoveQueue || !float.IsFinite(deltaTime) || deltaTime <= 0f) return;
            RestlessLightRuntime.Register(this);
            bool contained = RestlessLightRuntime.Shielded(item);
            Vector2 center = AnomalyRuntime.RootOwner(item).WorldPosition;
#if CLIENT
            UpdateVisuals(center, contained, deltaTime);
#endif
            if (!AnomalyRuntime.Authority) return;
            Character observer = RestlessLightRuntime.Observer(center);
            var pulses = clock.Step(deltaTime, contained, observer != null);
            if (pulses.Emp) RestlessLightRuntime.Emp(center, item);
            if (pulses.Shock) RestlessLightRuntime.Shock(center);
            if (pulses.Blink && observer?.Submarine != null) RestlessLightRuntime.Blink(item, observer);
        }
#if CLIENT
        private void UpdateVisuals(Vector2 center, bool contained, float dt)
        {
            if (contained) { ClearVisuals(); return; }
            double time = Timing.TotalTime;
            float phase = (float)(time % 12.0 / 12.0);
            var color = new Color(
                (byte)(155 + 90 * Math.Sin(phase * Math.PI * 2)),
                (byte)(155 + 90 * Math.Sin((phase + 0.3333) * Math.PI * 2)),
                (byte)(155 + 90 * Math.Sin((phase + 0.6667) * Math.PI * 2)), (byte)150);
            // 独立光源不受普通容器的 hideitems 和原灯组件的休眠控制。
            glow ??= new LightSource(center, RestlessLightRuntime.GlowRadius, color, null) { CastShadows = false };
            glow.Position = center;
            glow.Color = color;
            bool pulse = time % RestlessLightClock.EmpInterval < 1.25;
            if (!pulse)
            {
                ClearBorrowed();
                visualScan = 0f;
                return;
            }
            visualScan -= dt;
            if (visualScan <= 0f)
            {
                visualScan = 0.5f;
                foreach (var target in Item.ItemList)
                {
                    if (target == item || target.Removed || target.IsInRemoveQueue ||
                        target.GetComponent<LightComponent>() == null ||
                        Vector2.DistanceSquared(center, target.WorldPosition) > RestlessLightRuntime.EmpRadius * RestlessLightRuntime.EmpRadius) continue;
                    if (!borrowed.ContainsKey(target))
                        borrowed[target] = new LightSource(target.WorldPosition, 280f, Color.White, null) { CastShadows = false };
                }
            }
            foreach (var pair in borrowed)
            {
                bool visible = !pair.Key.Removed && !pair.Key.IsInRemoveQueue &&
                    Vector2.DistanceSquared(center, pair.Key.WorldPosition) <= RestlessLightRuntime.EmpRadius * RestlessLightRuntime.EmpRadius;
                pair.Value.Enabled = visible;
                if (!visible) continue;
                pair.Value.Position = pair.Key.WorldPosition;
                pair.Value.Color = new Color(220, 230, 255, 110);
            }
        }
        private void ClearBorrowed()
        {
            foreach (var light in borrowed.Values) light.Remove();
            borrowed.Clear();
        }
#endif
        internal void ClearVisuals()
        {
#if CLIENT
            glow?.Remove(); glow = null;
            ClearBorrowed();
#endif
        }
        internal void ResetForRound() { clock.Reset(); ClearVisuals(); }
        public override void RemoveComponentSpecific()
        {
            disposed = true;
            if (AnomalyRuntime.IsMainThread) RestlessLightRuntime.Unregister(this);
            base.RemoveComponentSpecific();
        }
    }

    internal static class RestlessLightRuntime
    {
        internal static readonly Identifier MissionId = "Touhou_Patchouli_Lamp_Mission".ToIdentifier();
        internal static readonly Identifier LampId = "Touhou_Anomaly_Restless_Light".ToIdentifier();
        internal static readonly Identifier ReportId = "Touhou_Patchouli_Observation".ToIdentifier();
        internal const float GlowRadius = 1600f, EmpRadius = 800f, ShockRadius = 220f, WatchRadius = 900f;
        internal static bool Enabled;
        private static readonly HashSet<TouhouRestlessLight> components = new HashSet<TouhouRestlessLight>();
        private static readonly List<TouhouRestlessLight> removed = new List<TouhouRestlessLight>();
        private static readonly Random random = new Random();
        private static double nextReport;
        private static bool reportPending;
        private static int epoch;
        internal static int Epoch => epoch;
        internal static void Register(TouhouRestlessLight light) => components.Add(light);
        internal static void Unregister(TouhouRestlessLight light) { light.ClearVisuals(); components.Remove(light); }
        internal static void DrainRemoved()
        {
            removed.Clear();
            foreach (var light in components) if (light.Gone) removed.Add(light);
            foreach (var light in removed) Unregister(light);
            removed.Clear();
        }
        internal static void Clear()
        {
            foreach (var light in components) light.ResetForRound();
            components.Clear();
            removed.Clear();
            epoch++; reportPending = false; nextReport = Timing.TotalTime + 2;
        }
        internal static bool Shielded(Item source)
        {
            if (source == null || source.Removed || source.IsInRemoveQueue) return false;
            return RestlessLightClock.AncestorShielded(source,
                current => current.ParentInventory?.Owner as Item,
                parent => parent.HasTag("artifactcontainer") || parent.HasTag("Touhou_Containment"));
        }
        internal static Character Observer(Vector2 position)
        {
            Character closest = null;
            float best = WatchRadius * WatchRadius;
            foreach (var c in Character.CharacterList)
            {
                if (c.Removed || c.IsDead || !c.IsHuman || c.CurrentHull == null || c.Submarine == null) continue;
                float distance = Vector2.DistanceSquared(position, c.WorldPosition);
                if (distance < best) { closest = c; best = distance; }
            }
            return closest;
        }
        internal static void Emp(Vector2 center, Item source)
        {
            foreach (var target in Item.ItemList)
            {
                if (target == source || target.Removed || target.IsInRemoveQueue) continue;
                float distance = Vector2.Distance(center, target.WorldPosition);
                if (distance >= EmpRadius) continue;
                var powered = target.GetComponent<Powered>();
                var battery = target.GetComponent<PowerContainer>();
                if (!RestlessLightClock.EmpTarget(target.GetComponent<LightComponent>() != null,
                    powered?.VulnerableToEMP == true, target.Repairables.Any(), battery != null)) continue;
                float strength = 0.06f * RestlessLightClock.Falloff(distance, EmpRadius);
                if (target.Repairables.Any()) target.Condition -= target.MaxCondition * strength;
                if (battery != null) battery.Charge = Math.Max(0f, battery.Charge - battery.GetCapacity() * strength);
            }
        }
        internal static void Shock(Vector2 center)
        {
            foreach (var c in Character.CharacterList)
            {
                if (c.Removed || c.IsDead || !c.IsHuman ||
                    Vector2.DistanceSquared(center, c.WorldPosition) > ShockRadius * ShockRadius) continue;
                foreach (var effect in new[] { ("electricshock", 25f), ("burn", 1.5f), ("stun", 0.35f) })
                    if (AfflictionPrefab.Prefabs.TryGet(effect.Item1.ToIdentifier(), out var prefab))
                        c.CharacterHealth.ApplyAffliction(c.AnimController.MainLimb, prefab.Instantiate(effect.Item2));
                c.HealthUpdateInterval = 0f;
            }
        }
        internal static void Blink(Item source, Character observer)
        {
            if (Shielded(source) || source.body == null || observer.Submarine == null) return;
            if (observer.Submarine.Info.Type != SubmarineType.Player && observer.Submarine.Info.Type != SubmarineType.Wreck) return;
            var points = WayPoint.WayPointList.Where(w => w.Submarine == observer.Submarine &&
                w.CurrentHull != null && Vector2.DistanceSquared(w.WorldPosition, observer.WorldPosition) > 250f * 250f &&
                Vector2.DistanceSquared(w.WorldPosition, source.WorldPosition) > 250f * 250f).ToList();
            if (points.Count == 0) return;
            var point = points[random.Next(points.Count)];
            Inventory previousInventory = source.ParentInventory;
            Vector2 previous = source.WorldPosition;
            Submarine previousSub = source.Submarine;
            try
            {
                source.Drop(null, setTransform: true);
                AnomalyRuntime.MoveItem(source, point.WorldPosition, observer.Submarine);
                source.body.Enabled = true;
                if (source.ParentInventory != null || source.CurrentHull?.Submarine != observer.Submarine)
                    throw new InvalidOperationException("瞬移落点不在目标潜艇内");
            }
            catch (Exception ex)
            {
                AnomalyRuntime.MoveItem(source, previous, previousSub);
                previousInventory?.TryPutItem(source, null);
                AnomalyRuntime.Warn("异常灯瞬移失败，已尝试还原", ex);
            }
        }
        internal static void TickReport()
        {
            if (AnomalyRuntime.Paused || Timing.TotalTime < nextReport) return;
            nextReport = Timing.TotalTime + 1;
            // 等抵达站点再排生成队列，免得结算后的旧关卡清理把记录一起带走。
            var campaign = GameMain.GameSession?.Campaign;
            if (campaign == null || Level.Loaded?.Type != LevelData.LevelType.Outpost || reportPending ||
                !campaign.CampaignMetadata.GetBoolean("Patchouli_Lamp_Completed".ToIdentifier()) ||
                campaign.CampaignMetadata.GetBoolean("Patchouli_Lamp_Report_Delivered".ToIdentifier()) ||
                campaign.CampaignMetadata.GetBoolean("Patchouli_Lamp_Report_Issued".ToIdentifier())) return;
            if (Item.ItemList.Any(i => !i.Removed && !i.IsInRemoveQueue && i.Prefab.Identifier == ReportId))
            {
                campaign.CampaignMetadata.SetValue("Patchouli_Lamp_Report_Issued".ToIdentifier(), true);
                return;
            }
            Character target = null;
#if SERVER
            target = GameMain.Server?.ConnectedClients.Select(c => c.Character)
                .FirstOrDefault(c => c != null && !c.Removed && !c.IsDead && c.IsHuman && c.Inventory != null);
#else
            target = Character.Controlled;
#endif
            if (target == null || target.Removed || target.IsDead || !target.IsHuman || target.Inventory == null ||
                Entity.Spawner == null || !ItemPrefab.Prefabs.TryGet(ReportId, out var prefab)) return;
            int token = epoch;
            reportPending = true;
            try
            {
                Entity.Spawner.AddItemToSpawnQueue(prefab, target.Inventory, onSpawned: report =>
                {
                    if (token != epoch) { if (report != null && !report.Removed) Entity.Spawner?.AddItemToRemoveQueue(report); return; }
                    reportPending = false;
                    if (report == null || report.Removed) return;
                    if (GameMain.GameSession?.Campaign != campaign)
                    {
                        Entity.Spawner?.AddItemToRemoveQueue(report);
                        return;
                    }
                    campaign.CampaignMetadata.SetValue("Patchouli_Lamp_Report_Issued".ToIdentifier(), true);
                });
            }
            catch (Exception ex)
            {
                reportPending = false;
                AnomalyRuntime.Warn("观察记录生成失败，稍后重试", ex);
            }
        }
    }
#endif
}
