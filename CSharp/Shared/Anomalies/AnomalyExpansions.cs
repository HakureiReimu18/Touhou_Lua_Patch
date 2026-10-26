using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.Anomalies
{
    public sealed class AnomalyExpansionPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize() => harmony = new Harmony("touhou.anomaly.expansion." +
#if SERVER
            "server");
#else
            "client");
#endif
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(AnomalyInstrumentLoadPatch));
            harmony.PatchAll(typeof(AnomalyInstrumentTickPatch));
            harmony.PatchAll(typeof(EchoAttackPatch));
            harmony.PatchAll(typeof(EchoExplosionPatch));
            harmony.PatchAll(typeof(EchoWoundPatch));
            harmony.PatchAll(typeof(EchoSavePatch));
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.AnomalyExpansion.End", Clear);
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.AnomalyExpansion.Start", Clear);
        }
        private static object Clear(object[] args) { AnomalyInstruments.Clear(); return null; }
        public void Dispose()
        {
            AnomalyInstruments.Clear();
            harmony?.UnpatchSelf();
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.AnomalyExpansion.End");
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.AnomalyExpansion.Start");
        }
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class AnomalyInstrumentLoadPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
            if (!AnomalyRuntime.OwnContext || element.Name.ToString() != "TouhouAnomalyInstrument") return true;
            try { __result = new TouhouAnomalyInstrument(item, element); }
            catch (Exception ex) { AnomalyRuntime.Warn("异常仪器加载失败", ex); __result = null; }
            return false;
        }
    }

    [HarmonyPatch(typeof(MapEntity), nameof(MapEntity.UpdateAll))]
    internal static class AnomalyInstrumentTickPatch
    {
        private static void Postfix(float deltaTime)
        {
            if (!AnomalyRuntime.Authority || !AnomalyRuntime.IsMainThread || AnomalyRuntime.Paused) return;
            try { AnomalyInstruments.Tick(deltaTime); }
            catch (Exception ex) { AnomalyRuntime.Warn("异常仪器更新失败", ex); }
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyAttack))]
    internal static class EchoAttackPatch
    {
        private static void Prefix(Character __instance, out Character __state)
        {
            __state = AnomalyInstruments.AttackTarget;
            if (AnomalyRuntime.Authority) AnomalyInstruments.AttackTarget = __instance;
        }
        private static Exception Finalizer(Character __state, Exception __exception)
        {
            AnomalyInstruments.AttackTarget = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterHealth), "AddLimbAffliction",
        new[] { typeof(CharacterHealth.LimbHealth), typeof(Limb), typeof(Affliction), typeof(bool), typeof(bool) })]
    internal static class EchoWoundPatch
    {
        internal sealed class Pending
        {
            internal Affliction Original;
            internal CharacterHealth.LimbHealth Slot;
            internal Limb Limb;
            internal float Before, Ratio;
        }
        private static void Prefix(CharacterHealth __instance, CharacterHealth.LimbHealth limbHealth, Limb limb,
            ref Affliction newAffliction, bool allowStacking, out Pending __state)
        {
            __state = null;
            try { __state = AnomalyInstruments.Prepare(__instance, limbHealth, limb, ref newAffliction, allowStacking); }
            catch (Exception ex) { AnomalyRuntime.Warn("残响受伤预处理失败，保留原伤", ex); }
        }
        private static void Postfix(CharacterHealth __instance, Pending __state)
        {
            if (__state == null) return;
            try { AnomalyInstruments.Commit(__instance, __state); }
            catch (Exception ex) { AnomalyRuntime.Warn("残响欠账登记失败", ex); }
        }
    }

    [HarmonyPatch(typeof(Explosion), nameof(Explosion.DamageCharacters))]
    internal static class EchoExplosionPatch
    {
        private static void Prefix(out int __state)
        {
            __state = AnomalyInstruments.ExplosionDepth;
            AnomalyInstruments.ExplosionDepth++;
        }
        private static Exception Finalizer(int __state, Exception __exception)
        {
            AnomalyInstruments.ExplosionDepth = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(CharacterHealth), nameof(CharacterHealth.Save))]
    internal static class EchoSavePatch
    {
        private static void Prefix(CharacterHealth __instance)
        {
            // 存档前兑现，不能靠保存再读档把尚未受的伤洗掉。
            if (AnomalyRuntime.Authority) AnomalyInstruments.Settle(__instance.Character);
        }
    }

    public sealed class TouhouAnomalyInstrument : ItemComponent
    {
        internal enum Instrument { Tide, Echo, Radio }
        internal readonly Instrument Mode;
        internal volatile bool Retired;
        internal bool Running;
        internal Character User;
        internal Hull From, To;
        private float age, moved, budget, cooldown;
        private int broadcastStage;
        private Submarine destination;

        public TouhouAnomalyInstrument(Item item, ContentXElement element) : base(item, element)
        {
            Mode = item.Prefab.Identifier == "Touhou_Anomaly_Fake_Moon_Shard".ToIdentifier() ? Instrument.Tide
                : item.Prefab.Identifier == "Touhou_Anomaly_Orb_Echo".ToIdentifier() ? Instrument.Echo : Instrument.Radio;
            IsActive = true;
        }
        public override bool UpdateWhenInactive => true;
        public override void OnItemLoaded()
        {
            if (AnomalyRuntime.IsMainThread) AnomalyInstruments.Register(this);
        }
        public override void Update(float deltaTime, Camera cam)
        {
            if (Retired || !AnomalyRuntime.IsMainThread) return;
            AnomalyInstruments.Register(this);
            var holder = AnomalyRuntime.RootOwner(item) as Character;
            bool audible = Available && (Mode != Instrument.Radio ||
                holder?.CharacterHealth?.GetAffliction(AnomalyInstruments.RadioMarker)?.Strength > 0f);
            IsActive = audible;
            var light = item.GetComponent<LightComponent>();
            if (light != null && light.IsOn != audible) light.IsOn = audible;
        }
        internal bool Available => !Retired && !item.Removed && !item.IsInRemoveQueue && !AnomalyRuntime.IsContained(item);
        internal bool HeldBy(Character c) => c != null && !c.Removed && !c.IsDead &&
            c.HeldItems.Contains(item) && AnomalyRuntime.RootOwner(item) == c;

        public override bool Use(float deltaTime, Character character = null)
        {
            if (!AnomalyRuntime.Authority || !Available || !HeldBy(character) ||
                Mode == Instrument.Echo || Running || cooldown > 0f) return false;
            if (Mode == Instrument.Tide)
            {
                From = character.CurrentHull;
                if (From == null || From.Removed || From.WaterVolume <= 0f) return false;
                Gap nearest = null;
                float best = 200f * 200f;
                foreach (var gap in From.ConnectedGaps)
                {
                    if (gap.Removed || gap.ConnectedDoor == null || gap.linkedTo.Count != 2) continue;
                    var other = gap.linkedTo.OfType<Hull>().FirstOrDefault(h => h != From);
                    float distance = Vector2.DistanceSquared(character.WorldPosition, gap.WorldPosition);
                    if (other == null || other.Removed || other.Submarine != From.Submarine ||
                        other.Volume <= other.WaterVolume || distance >= best) continue;
                    best = distance; nearest = gap; To = other;
                }
                if (nearest == null) { AnomalyRuntime.Notify(character, "两米内没有能接走潮水的舱门。"); return false; }
                budget = Math.Min(From.Volume, To.Volume) * 0.25f;
                AnomalyRuntime.Notify(character, "月斑指向「" + To.RoomName + "」。五秒后，这里的水将出现在那里。");
            }
            else
            {
                destination = Submarine.Loaded.Where(s => !s.Removed && s.Info != null &&
                    (s.Info.Type == SubmarineType.Wreck || s.Info.Type == SubmarineType.Ruin))
                    .OrderBy(s => Vector2.DistanceSquared(s.WorldPosition, item.WorldPosition)).FirstOrDefault();
                AnomalyRuntime.Notify(character, "接收机已开启。持续监听会暴露本机位置；松手放下或收容即可中止。");
            }
            User = character; Running = true; age = moved = 0f; broadcastStage = 0;
            return true;
        }
        public override bool SecondaryUse(float deltaTime, Character character = null)
        {
            if (!AnomalyRuntime.Authority || !Running || !HeldBy(character)) return false;
            Stop();
            return true;
        }
        internal void Stop()
        {
            if (Running) cooldown = Math.Max(cooldown, 30f);
            if (Mode == Instrument.Radio && User != null && AnomalyRuntime.Authority)
                AnomalyInstruments.Mark(User, AnomalyInstruments.RadioMarker, 0f);
            Running = false; User = null; From = To = null; destination = null;
            if (Mode == Instrument.Radio && item.AiTarget != null) item.AiTarget.SoundRange = 0f;
        }
        internal void Step(float dt)
        {
            cooldown = Math.Max(0f, cooldown - dt);
            var light = item.GetComponent<LightComponent>();
            bool on = Available && (Mode != Instrument.Radio || Running);
            if (light != null && light.IsOn != on) light.IsOn = on;
            if (!Running) return;
            if (!Available || !HeldBy(User)) { Stop(); return; }
            age += dt;
            if (Mode == Instrument.Tide)
            {
                if (From == null || To == null || From.Removed || To.Removed || User.CurrentHull != From)
                { Stop(); return; }
                if (age < 5f) return;
                float amount = AnomalyInstrumentRules.TransferAmount(From.WaterVolume, To.WaterVolume, To.Volume,
                    Math.Min(From.Volume, To.Volume) * 0.025f * dt, budget - moved);
                if (amount <= 0f) { Stop(); return; }
                float sourceBefore = From.WaterVolume, targetBefore = To.WaterVolume;
                try
                {
                    From.WaterVolume -= amount;
                    float removed = sourceBefore - From.WaterVolume;
                    To.WaterVolume += removed;
                    // 遇到别的模组钳制水位也不吞水，多退少补到来源舱。
                    float added = To.WaterVolume - targetBefore;
                    From.WaterVolume = sourceBefore - added;
                    moved += added;
                }
                catch
                {
                    try { To.WaterVolume = targetBefore; }
                    finally { From.WaterVolume = sourceBefore; Stop(); }
                    throw;
                }
                if (moved >= budget - 0.01f) Stop();
            }
            else if (Mode == Instrument.Radio)
            {
                AnomalyInstruments.Mark(User, AnomalyInstruments.RadioMarker, 1f);
                if (item.AiTarget != null) item.AiTarget.SoundRange = AnomalyInstrumentRules.RadioRange(age);
                int stage = age >= 30f ? 3 : age >= 18f ? 2 : age >= 6f ? 1 : 0;
                if (stage <= broadcastStage) return;
                broadcastStage = stage;
                if (stage == 1)
                    AnomalyRuntime.Notify(User, destination == null || destination.Removed
                        ? "【深渊信号】本航段没有可辨认的求救坐标。接收机仍在向外发声。"
                        : "【深渊信号】这里是██号。我们已经看见" +
                            (destination.Info.Type == SubmarineType.Wreck ? "残骸。" : "遗迹。"));
                else if (stage == 2)
                    AnomalyRuntime.Notify(User, Bearing(100f) + " 信号正在重复你们的呼号。");
                else
                {
                    AnomalyRuntime.Notify(User, Bearing(25f) + " 下一段播音将公布本机坐标。");
                    Vector2 p = item.WorldPosition / 100f;
                    AnomalyRuntime.Notify(User, $"【深渊信号】接收者坐标：{p.X:F0}，{p.Y:F0}。请不要关闭接收机。");
                }
            }
        }
        private string Bearing(float precision)
        {
            if (destination == null || destination.Removed) return "【深渊信号】求救坐标已无法确认。";
            Vector2 delta = destination.WorldPosition - item.WorldPosition;
            float distance = (float)Math.Sqrt(delta.LengthSquared()) / 100f;
            string horizontal = delta.X >= 0 ? "右" : "左";
            string vertical = delta.Y >= 0 ? "上" : "下";
            return $"【深渊信号】目标在{horizontal}{vertical}方，距离约 {Math.Max(precision, MathF.Round(distance / precision) * precision):F0} 米。";
        }
        public override void RemoveComponentSpecific() { Retired = true; }
    }

    internal static class AnomalyInstrumentRules
    {
        internal const float DebtDelay = 10f, DebtCooldown = 120f, DebtLimit = 30f;
        internal static float TransferAmount(float source, float target, float capacity, float rate, float budget)
        {
            if (!float.IsFinite(source + target + capacity + rate + budget)) return 0f;
            return Math.Max(0f, Math.Min(Math.Min(source, capacity - target), Math.Min(rate, budget)));
        }
        internal static float RadioRange(float age) => Math.Clamp(1500f + age * 275f, 1500f, 18000f);
    }

    internal static class AnomalyInstruments
    {
        private sealed class Debt
        {
            internal Character Victim, Source;
            internal CharacterHealth.LimbHealth Slot;
            internal Limb Limb;
            internal AfflictionPrefab Prefab;
            internal float Amount, Remaining = AnomalyInstrumentRules.DebtDelay;
        }
        private static readonly HashSet<TouhouAnomalyInstrument> Instruments = new();
        private static readonly Dictionary<Character, Debt> Debts = new();
        private static readonly Dictionary<Character, float> Cooldowns = new();
        private static readonly HashSet<string> Wounds = new(StringComparer.OrdinalIgnoreCase)
        { "internaldamage", "blunttrauma", "lacerations", "gunshotwound", "bitewounds", "burn", "bleeding" };
        [ThreadStatic] internal static Character AttackTarget;
        [ThreadStatic] internal static int ExplosionDepth;
        private static readonly Identifier Marker = "Touhou_Anomaly_Deferred_Wound".ToIdentifier();
        internal static readonly Identifier RadioMarker = "Touhou_Anomaly_Radio_Listener".ToIdentifier();
        private static bool settling;
        private static float elapsed;

        internal static void Register(TouhouAnomalyInstrument instrument) => Instruments.Add(instrument);
        internal static void Tick(float dt)
        {
            if (!float.IsFinite(dt) || dt <= 0f || (Instruments.Count == 0 && Debts.Count == 0 && Cooldowns.Count == 0)) return;
            elapsed += dt;
            if (elapsed < 0.25f) return;
            dt = Math.Min(elapsed, 1f); elapsed = 0f;
            foreach (var instrument in Instruments.ToArray())
            {
                if (instrument.Retired || instrument.Item.Removed)
                { instrument.Stop(); Instruments.Remove(instrument); }
                else instrument.Step(dt);
            }
            foreach (var pair in Cooldowns.ToArray())
            {
                if (pair.Key.Removed || pair.Key.IsDead || pair.Value <= dt) Cooldowns.Remove(pair.Key);
                else Cooldowns[pair.Key] = pair.Value - dt;
            }
            foreach (var debt in Debts.Values.ToArray())
            {
                debt.Remaining -= dt;
                if (debt.Remaining <= 0f || debt.Victim.Removed || debt.Victim.IsDead) Settle(debt.Victim);
                else SetMarker(debt.Victim, debt.Remaining);
            }
        }
        private static TouhouAnomalyInstrument Orb(Character c) => Instruments.FirstOrDefault(i =>
            i.Mode == TouhouAnomalyInstrument.Instrument.Echo && i.Available && AnomalyRuntime.RootOwner(i.Item) == c);
        internal static float Stock(CharacterHealth health, CharacterHealth.LimbHealth slot, AfflictionPrefab prefab) =>
            health.afflictions.Where(p => p.Value == slot && p.Key.Prefab == prefab).Sum(p => p.Key.Strength);

        internal static EchoWoundPatch.Pending Prepare(CharacterHealth health, CharacterHealth.LimbHealth slot,
            Limb limb, ref Affliction affliction, bool stacking)
        {
            var c = health.Character;
            if (!AnomalyRuntime.Authority || !AnomalyRuntime.IsMainThread || settling || ExplosionDepth > 0 ||
                c != AttackTarget || c == null || c.Removed || c.IsDead ||
                c.GodMode || health.Unkillable || !c.IsHuman || !stacking || affliction == null ||
                !Wounds.Contains(affliction.Prefab.Identifier.Value) || affliction.Strength <= 0f ||
                Debts.ContainsKey(c) || Cooldowns.ContainsKey(c) || Orb(c) == null) return null;
            float expected = affliction.Strength * (100f / health.MaxVitality) *
                (1f - health.GetResistance(affliction.Prefab, limb?.type ?? LimbType.None));
            if (!float.IsFinite(expected) || expected < 4f) return null;
            float fraction = Math.Min(0.5f, AnomalyInstrumentRules.DebtLimit / expected);
            var state = new EchoWoundPatch.Pending { Original = affliction, Slot = slot, Limb = limb,
                Before = Stock(health, slot, affliction.Prefab), Ratio = fraction / (1f - fraction) };
            // 只替换本次调用的参数，不能改武器共享的 affliction 原件。
            affliction = affliction.Prefab.Instantiate(affliction.Strength * (1f - fraction), affliction.Source);
            return state;
        }
        internal static void Commit(CharacterHealth health, EchoWoundPatch.Pending state)
        {
            float actual = Stock(health, state.Slot, state.Original.Prefab) - state.Before;
            if (actual <= 0f || Debts.ContainsKey(health.Character)) return;
            var c = health.Character;
            var debt = new Debt { Victim = c, Source = state.Original.Source, Slot = state.Slot, Limb = state.Limb,
                Prefab = state.Original.Prefab, Amount = Math.Min(AnomalyInstrumentRules.DebtLimit, actual * state.Ratio) };
            Debts.Add(c, debt);
            Cooldowns[c] = AnomalyInstrumentRules.DebtCooldown;
            SetMarker(c, debt.Remaining);
            string part = state.Limb?.type.ToString() switch
            {
                "Head" => "你的头部", "Torso" => "你的躯干",
                "LeftArm" or "LeftHand" => "你的左臂", "RightArm" or "RightHand" => "你的右臂",
                "LeftLeg" or "LeftFoot" => "你的左腿", "RightLeg" or "RightFoot" => "你的右腿",
                _ => "你的身体"
            };
            AnomalyRuntime.Notify(c, $"十秒后，{part}将兑现一笔伤势。丢掉残响不能取消。");
        }
        private static void SetMarker(Character c, float seconds)
            => Mark(c, Marker, MathF.Ceiling(Math.Max(0f, seconds)));
        internal static void Mark(Character c, Identifier id, float seconds)
        {
            var prefab = AfflictionPrefab.Prefabs.Find(p => p.Identifier == id);
            if (prefab == null || c.Removed || c.IsDead) return;
            var aff = c.CharacterHealth.GetAffliction(id);
            if (aff == null && seconds > 0f) { c.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(seconds)); aff = c.CharacterHealth.GetAffliction(id); }
            if (aff == null || aff.Strength == seconds) return;
            aff.Strength = seconds;
            c.HealthUpdateInterval = 0f;
        }
        internal static void Settle(Character c)
        {
            if (!Debts.TryGetValue(c, out var debt)) return;
            var health = c.CharacterHealth;
            try
            {
                if (!c.Removed && !c.IsDead && !c.GodMode && !health.Unkillable)
                {
                    // 记录的是已通过原生抗性与血量缩放的量，兑现时不能再缩一次。
                    settling = true;
                    var existing = health.afflictions.FirstOrDefault(p => p.Value == debt.Slot && p.Key.Prefab == debt.Prefab).Key;
                    if (existing == null)
                        health.afflictions.Add(debt.Prefab.Instantiate(Math.Min(debt.Prefab.MaxStrength, debt.Amount), debt.Source), debt.Slot);
                    else
                    {
                        existing.Strength = Math.Min(existing.Prefab.MaxStrength, existing.Strength + debt.Amount);
                        if (debt.Source != null) existing.Source = debt.Source;
                        existing.Duration = existing.Prefab.Duration;
                    }
                    health.RecalculateVitality();
                    c.HealthUpdateInterval = 0f;
                }
                Debts.Remove(c);
                SetMarker(c, 0f);
            }
            finally { settling = false; }
        }
        internal static void Clear()
        {
            if (AnomalyRuntime.Authority && AnomalyRuntime.IsMainThread)
                foreach (var c in Debts.Keys.ToArray()) Settle(c);
            foreach (var i in Instruments) i.Stop();
            Instruments.Clear(); Debts.Clear(); Cooldowns.Clear();
            AttackTarget = null; ExplosionDepth = 0; elapsed = 0f;
        }
    }
}
