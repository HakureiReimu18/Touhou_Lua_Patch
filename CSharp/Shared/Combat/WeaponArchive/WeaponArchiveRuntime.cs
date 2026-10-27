using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Touhou.WeaponEnergy;

namespace Touhou.WeaponArchive
{
    public sealed class WeaponArchivePlugin : IAssemblyPlugin
    {
        private readonly Harmony harmony = new Harmony(
#if SERVER
            "touhou.weaponarchive.server");
#else
            "touhou.weaponarchive.client");
#endif
        private bool installed;
        public void Initialize() { }
        public void PreInitPatching() => Install();
        public void OnLoadCompleted() => Install();
        private void Install()
        {
            if (installed) return;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ArchiveFactoryPatch));
            harmony.PatchAll(typeof(ArchiveShootPatch));
            harmony.PatchAll(typeof(ArchiveImpactPatch));
            harmony.PatchAll(typeof(ArchiveKillPatch));
            harmony.PatchAll(typeof(ArchiveVictimSavePatch));
            harmony.PatchAll(typeof(ArchiveVictimLoadPatch));
            harmony.PatchAll(typeof(ArchiveStationButtonPatch));
#if SERVER
            harmony.PatchAll(typeof(ArchiveStationRequestPatch));
#endif
            installed = true;
        }
        public void Dispose()
        {
            harmony.UnpatchSelf();
            ArchiveRuntime.Clear();
            installed = false;
        }
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class ArchiveFactoryPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
            if (!ArchiveRuntime.OwnContext) return true;
            if (element.Name.ToString().Equals("TouhouWeaponArchive", StringComparison.OrdinalIgnoreCase))
                __result = new TouhouWeaponArchive(item, element);
            else if (element.Name.ToString().Equals("TouhouArchiveStation", StringComparison.OrdinalIgnoreCase))
                __result = new TouhouArchiveStation(item, element);
            else return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Shoot))]
    internal static class ArchiveShootPatch
    {
        private static void Prefix(Projectile __instance)
        {
            if (!ArchiveRuntime.Authority) return;
            ArchiveRuntime.Forget(__instance);
            var archive = __instance.Launcher?.GetComponent<TouhouWeaponArchive>();
            if (archive != null) ArchiveRuntime.Stamp(__instance, archive);
        }
    }

    [HarmonyPatch(typeof(Projectile), "HandleProjectileCollision")]
    internal static class ArchiveImpactPatch
    {
        internal sealed class State
        {
            internal ArchiveRuntime.AttackContext Previous, Current;
            internal bool ChangedDamage;
            internal bool WasAlive;
            internal float Damage;
        }
        private static void Prefix(Projectile __instance, Fixture target, out State __state)
        {
            __state = null;
            if (!ArchiveRuntime.Authority || !ArchiveRuntime.TryShot(__instance, out var archive)) return;
            var limb = target?.Body?.UserData as Limb;
            var character = limb?.character;
            bool handleDamage = __instance.Attack != null && (character == null || !WeaponEnergyRuntime.HasShot(__instance));
            float multiplier = handleDamage
                ? archive.Multiplier(character == null ? null : character.SpeciesName.Value, ArchiveBonusKind.Damage) : 1f;
            if (character == null && (!handleDamage || multiplier == 1f)) return;
            var context = new ArchiveRuntime.AttackContext { Archive = archive, Target = character, Attacker = __instance.User };
            __state = new State { Previous = ArchiveRuntime.Context, Current = context, WasAlive = character != null && !character.IsDead };
            ArchiveRuntime.Context = context;
            if (character != null) ArchiveRuntime.PrepareHit(__instance, archive, character, __instance.User, limb);
            // 能量武器在原有伤害补丁中统一保存/恢复倍率，避免两套补丁互相覆盖恢复值。
            if (handleDamage)
            {
                __state.ChangedDamage = true;
                __state.Damage = __instance.Attack.DamageMultiplier;
                __instance.Attack.DamageMultiplier *= multiplier;
            }
        }
        private static void Restore(Projectile projectile, State state)
        {
            if (state == null) return;
            if (state.ChangedDamage && projectile.Attack != null) projectile.Attack.DamageMultiplier = state.Damage;
            ArchiveRuntime.Context = state.Previous;
        }
        private static void Postfix(Projectile __instance, bool __result, State __state)
        {
            if (!__result && __state != null) ArchiveRuntime.DiscardHit(__instance, __state.Current.Target);
            if (__result && __state != null && !WeaponEnergyRuntime.HasShot(__instance))
                ArchiveRuntime.ApplyHitEffects(__instance, __state.Current.Target, __state.WasAlive);
            if (__result && __state?.WasAlive == true && __state.Current.Target.IsDead)
                ArchiveRuntime.Record(__state.Current.Archive, __state.Current.Target, __state.Current.Attacker, false);
            Restore(__instance, __state);
        }
        private static Exception Finalizer(Projectile __instance, State __state, Exception __exception)
        {
            if (__exception != null && __state != null) ArchiveRuntime.DiscardHit(__instance, __state.Current.Target);
            Restore(__instance, __state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Kill))]
    internal static class ArchiveKillPatch
    {
        internal sealed class State
        {
            internal bool Alive;
            internal TouhouWeaponArchive Archive;
            internal Character Attacker;
        }
        private static void Prefix(Character __instance, CauseOfDeathType __0, Affliction __1, out State __state)
        {
            __state = null;
            if (!ArchiveRuntime.Authority || __instance.IsDead) return;
            var context = ArchiveRuntime.Context;
            if (context != null && context.Target == __instance)
                __state = new State { Alive = true, Archive = context.Archive, Attacker = context.Attacker };
            else if (__0 == CauseOfDeathType.Affliction && __1?.Source != null && __instance.LastDamageSource is Item source &&
                source.GetComponent<Projectile>() is Projectile projectile &&
                __1.Source == projectile.User && ArchiveRuntime.TryShot(projectile, out var archive))
                __state = new State { Alive = true, Archive = archive, Attacker = projectile.User };
        }
        private static void Postfix(Character __instance, State __state)
        {
            if (__state?.Alive == true && __instance.IsDead)
                ArchiveRuntime.Record(__state.Archive, __instance, __state.Attacker, false);
        }
    }

    [HarmonyPatch(typeof(CharacterHealth), nameof(CharacterHealth.Save))]
    internal static class ArchiveVictimSavePatch
    {
        private static void Postfix(CharacterHealth __instance, XElement __0)
        {
            if (ArchiveRuntime.Authority) ArchiveRuntime.SaveVictim(__instance.Character, __0);
        }
    }

    [HarmonyPatch(typeof(CharacterHealth), nameof(CharacterHealth.Load))]
    internal static class ArchiveVictimLoadPatch
    {
        private static void Postfix(CharacterHealth __instance, XElement __0)
        {
            if (ArchiveRuntime.Authority) ArchiveRuntime.LoadVictim(__instance.Character, __0);
        }
    }

    internal static class ArchiveRuntime
    {
        internal sealed class AttackContext
        {
            internal TouhouWeaponArchive Archive;
            internal Character Target, Attacker;
        }
        private sealed class Shot
        {
            internal TouhouWeaponArchive Archive;
            internal Dictionary<Character, HitPlan> Pending;
            internal HashSet<Character> Applied;
        }
        private sealed class HitPlan
        {
            internal TouhouWeaponArchive Archive;
            internal Character Target, Attacker;
            internal Limb Limb;
            internal List<ResolvedAffliction> Effects;
        }
        private readonly struct ResolvedAffliction
        {
            internal readonly ArchiveAttackAffliction Rule;
            internal readonly float Strength;
            internal ResolvedAffliction(ArchiveAttackAffliction rule, float strength) { Rule = rule; Strength = strength; }
        }
        [ThreadStatic] internal static AttackContext Context;
        private static readonly Identifier sleepRecovery = "Touhou_Silence_SleepRecovery".ToIdentifier();
        private static ConditionalWeakTable<Projectile, Shot> shots = new();
        private static ConditionalWeakTable<Character, HashSet<string>> victims = new();
        internal static bool OwnContext =>
#if SERVER
            GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static void Clear()
        {
            shots = new();
            victims = new();
            Context = null;
        }
        internal static void Forget(Projectile projectile) => shots.Remove(projectile);
        internal static void Stamp(Projectile projectile, TouhouWeaponArchive archive)
        {
            shots.Remove(projectile);
            shots.Add(projectile, new Shot { Archive = archive });
        }
        internal static bool TryShot(Projectile projectile, out TouhouWeaponArchive archive)
        {
            bool found = shots.TryGetValue(projectile, out var shot);
            archive = shot?.Archive;
            return found;
        }

        internal static void PrepareHit(Projectile projectile, TouhouWeaponArchive archive, Character target,
            Character attacker, Limb limb)
        {
            if (!Authority || !archive.DataValid || archive.AttackAfflictions.Count == 0 ||
                target.IsDead || target.Removed || attacker == null || attacker == target ||
                !shots.TryGetValue(projectile, out var shot) || shot.Applied?.Contains(target) == true) return;
            // 门槛和强度在命中前拍下，不能让这一击刚解锁的档案反过来加强自己。
            List<ResolvedAffliction> effects = null;
            string species = ArchiveLedger.Key(target.SpeciesName.Value);
            foreach (var rule in archive.AttackAfflictions)
            {
                if (rule.Strength <= 0f || rule.Probability <= 0f ||
                    !rule.Condition.Matches(archive.Ledger, species)) continue;
                float strength = rule.Strength * archive.Multiplier(species, ArchiveBonusKind.Affliction, rule.Identifier);
                if (!float.IsFinite(strength) || strength <= 0f) continue;
                (effects ??= new List<ResolvedAffliction>()).Add(new ResolvedAffliction(rule, strength));
            }
            if (effects == null)
            {
                shot.Pending?.Remove(target);
                return;
            }
            shot.Pending ??= new Dictionary<Character, HitPlan>();
            shot.Pending[target] = new HitPlan { Archive = archive, Target = target, Attacker = attacker, Limb = limb, Effects = effects };
        }

        internal static void DiscardHit(Projectile projectile, Character target)
        {
            if (target != null && shots.TryGetValue(projectile, out var shot)) shot.Pending?.Remove(target);
        }

        internal static void ApplyHitEffects(Projectile projectile, Character target, bool wasAlive)
        {
            if (!Authority || target == null || !shots.TryGetValue(projectile, out var shot) ||
                shot.Pending == null || !shot.Pending.Remove(target, out var plan)) return;
            if (!wasAlive || target.IsDead || target.Removed || target.CharacterHealth == null ||
                !plan.Archive.DataValid || plan.Archive.Item.Removed) return;
            shot.Applied ??= new HashSet<Character>();
            if (!shot.Applied.Add(target)) return;
            var previous = Context;
            Context = new AttackContext { Archive = plan.Archive, Target = target, Attacker = plan.Attacker };
            try
            {
                foreach (var effect in plan.Effects)
                {
                    if (target.IsDead || target.Removed) break;
                    var rule = effect.Rule;
                    if (rule.HostileOnly && target.IsFriendly(plan.Attacker)) continue;
                    if (rule.Probability < 1f && Rand.Value() >= rule.Probability) continue;
                    var identifier = rule.Identifier.ToIdentifier();
                    var prefab = AfflictionPrefab.Prefabs.Find(candidate => candidate.Identifier == identifier);
                    if (prefab == null) continue;
                    bool hardControl = rule.HardControl || prefab.Identifier == AfflictionPrefab.StunType ||
                        prefab.Identifier == AfflictionPrefab.ParalysisType || prefab.AfflictionType == AfflictionPrefab.StunType ||
                        prefab.AfflictionType == AfflictionPrefab.ParalysisType;
                    if (hardControl)
                    {
                        bool protect = WeaponEnergyRules.ProtectFromHardControl(target.IsHuman, target.IsHumanoid, target.IsPlayer);
                        if (!WeaponEnergyRules.AllowHardControl(protect, target.Params.IsMachine,
                            target.Params.UseBossHealthBar, target.Params.Health.StunImmunity) ||
                            target.IsUnconscious || target.CharacterHealth.GetAffliction(sleepRecovery)?.Strength > 0f)
                            continue;
                    }
                    if (!float.IsFinite(effect.Strength) || !float.IsFinite(target.MaxVitality) || target.MaxVitality <= 0f) continue;
                    var affliction = prefab.Instantiate(effect.Strength * target.MaxVitality / 100f, plan.Attacker);
                    var limb = plan.Limb;
                    if (limb == null || limb.character != target || limb.IsSevered) limb = target.AnimController?.MainLimb;
                    if (rule.IgnoreArmor || limb == null)
                        target.CharacterHealth.ApplyAffliction(limb, affliction, allowStacking: rule.Stack);
                    else
                        target.DamageLimb(limb.WorldPosition, limb, new List<Affliction> { affliction }, stun: 0f,
                            playSound: false, attackImpulse: Vector2.Zero, attacker: plan.Attacker, allowStacking: rule.Stack);
                    target.HealthUpdateInterval = 0f;
                }
                if (target.IsDead) Record(plan.Archive, target, plan.Attacker, false);
            }
            finally { Context = previous; }
        }

        internal static float Bonus(Item weapon, Character target, ArchiveBonusKind kind, Identifier identifier = default)
            => weapon?.GetComponent<TouhouWeaponArchive>()?.Multiplier(target.SpeciesName.Value, kind, identifier.Value) ?? 1f;

        internal static bool Record(TouhouWeaponArchive archive, Character target, Character attacker, bool sedation)
        {
            if (!Authority || archive == null || !archive.DataValid || archive.Item.Removed ||
                target == null || attacker == null || target == attacker || target.IsPlayer ||
                (archive.HostileOnly && target.IsFriendly(attacker)) ||
                (sedation ? !archive.RecordSedation || target.IsDead : !archive.RecordKills || !target.IsDead)) return false;
            var seen = victims.GetOrCreateValue(target);
            string model = archive.Ledger.Model;
            lock (seen)
            {
                if (seen.Contains(model) || seen.Count >= 256 || !archive.Ledger.Record(target.SpeciesName.Value)) return false;
                seen.Add(model);
            }
            archive.Changed();
            return true;
        }

        internal static void SaveVictim(Character target, XElement parent)
        {
            if (!victims.TryGetValue(target, out var seen)) return;
            string[] models;
            lock (seen) models = seen.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (models.Length == 0) return;
            parent.Elements("TouhouArchiveVictim").Remove();
            parent.Add(new XElement("TouhouArchiveVictim",
                models.Select(model => new XElement("Model", new XAttribute("id", model)))));
        }
        internal static void LoadVictim(Character target, XElement parent)
        {
            var node = parent?.Element("TouhouArchiveVictim");
            if (node == null) return;
            var seen = victims.GetOrCreateValue(target);
            lock (seen)
            {
                foreach (var child in node.Elements("Model").Take(256))
                {
                    string key = ArchiveLedger.Key((string)child.Attribute("id"));
                    if (key != null) seen.Add(key);
                }
            }
        }
    }
}
