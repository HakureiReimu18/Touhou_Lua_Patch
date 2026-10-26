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
            if (!ArchiveRuntime.Authority || target?.Body?.UserData is not Limb limb || limb.character == null ||
                !ArchiveRuntime.TryShot(__instance, out var archive)) return;
            var context = new ArchiveRuntime.AttackContext { Archive = archive, Target = limb.character, Attacker = __instance.User };
            __state = new State { Previous = ArchiveRuntime.Context, Current = context, WasAlive = !limb.character.IsDead };
            ArchiveRuntime.Context = context;
            // 能量武器在原有伤害补丁中统一保存/恢复倍率，避免两套补丁互相覆盖恢复值。
            if (!WeaponEnergyRuntime.HasShot(__instance) && __instance.Attack != null)
            {
                __state.ChangedDamage = true;
                __state.Damage = __instance.Attack.DamageMultiplier;
                __instance.Attack.DamageMultiplier *= archive.Multiplier(limb.character.SpeciesName.Value, ArchiveBonusKind.Damage);
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
            if (__result && __state?.WasAlive == true && __state.Current.Target.IsDead)
                ArchiveRuntime.Record(__state.Current.Archive, __state.Current.Target, __state.Current.Attacker, false);
            Restore(__instance, __state);
        }
        private static Exception Finalizer(Projectile __instance, State __state, Exception __exception)
        {
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
        [ThreadStatic] internal static AttackContext Context;
        private static ConditionalWeakTable<Projectile, TouhouWeaponArchive> shots = new();
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
            shots.Add(projectile, archive);
        }
        internal static bool TryShot(Projectile projectile, out TouhouWeaponArchive archive)
            => shots.TryGetValue(projectile, out archive);

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
