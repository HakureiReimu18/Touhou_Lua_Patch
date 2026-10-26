// 命中效果与网络事件：把射弹归属回发射武器，执行 AttackAffliction/Stun/Sedation，
// 以及按“实际打掉的血”给武器回能。也放着按钮/射击/命中三处 Harmony 补丁。
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics.Dynamics;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Touhou.WeaponArchive;

namespace Touhou.WeaponEnergy
{
    [HarmonyPatch(typeof(CustomInterface), "ButtonClicked")]
    internal static class WeaponEnergyButtonPatch
    {
        private static bool Prefix(CustomInterface __instance)
        {
            if (!WeaponEnergyRuntime.OwnContext) return true;
            var control = __instance.Item.GetComponent<TouhouWeaponEnergy>();
            if (control == null) return true;
#if SERVER
            if (WeaponEnergyRuntime.ButtonSender?.Character != control.Holder()) return false;
#endif
            // 返回 true 只在“成功开启”时：改装姿态/音效在 XML 按钮链里，关闭时不重放。
            return control.Toggle();
        }
    }

#if SERVER
    [HarmonyPatch(typeof(CustomInterface), nameof(CustomInterface.ServerEventRead))]
    internal static class WeaponEnergyServerButtonContextPatch
    {
        private static void Prefix(Client __1, out Client __state)
        {
            __state = WeaponEnergyRuntime.ButtonSender;
            WeaponEnergyRuntime.ButtonSender = __1;
        }

        private static Exception Finalizer(Client __state, Exception __exception)
        {
            WeaponEnergyRuntime.ButtonSender = __state;
            return __exception;
        }
    }
#endif

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Shoot))]
    internal static class WeaponEnergyShootPatch
    {
        private static void Prefix(Projectile __instance)
        {
            if (!WeaponEnergyRuntime.Authority) return;
            WeaponEnergyRuntime.Forget(__instance);
            var control = __instance.Launcher?.GetComponent<TouhouWeaponEnergy>();
            if (control == null) return;
            // 必须在 Shoot 前扣费，hitscan 会在这个调用里立刻命中。
            WeaponEnergyRuntime.Stamp(__instance, control.PayShot(), control);
        }
    }

    [HarmonyPatch(typeof(Projectile), "HandleProjectileCollision")]
    internal static class WeaponEnergyImpactPatch
    {
        internal sealed class HitState
        {
            internal Character Target;
            internal float DamageMultiplier;
            internal bool WasAlive;
            internal float VitalityBefore;
        }

        private static void Prefix(Projectile __instance, Fixture target, out HitState __state)
        {
            __state = null;
            if (!WeaponEnergyRuntime.Authority || !WeaponEnergyRuntime.HasShot(__instance) ||
                target?.Body?.UserData is not Limb limb || limb.character == null) return;
            __state = new HitState
            {
                Target = limb.character,
                WasAlive = !limb.character.IsDead && !limb.character.Removed,
                DamageMultiplier = __instance.Attack?.DamageMultiplier ?? 1f,
                VitalityBefore = limb.character.Vitality,
            };
            if (__instance.Attack != null)
                __instance.Attack.DamageMultiplier *= ArchiveRuntime.Bonus(__instance.Launcher, limb.character, ArchiveBonusKind.Damage);
            if (__instance.Attack != null && (limb.character.IsUnconscious || limb.character.Stun > 0f))
                __instance.Attack.DamageMultiplier *= WeaponEnergyRuntime.NonLethalMultiplier(__instance);
        }

        private static void Restore(Projectile projectile, HitState state)
        {
            if (state != null && projectile.Attack != null) projectile.Attack.DamageMultiplier = state.DamageMultiplier;
        }

        private static void Postfix(Projectile __instance, bool __result, HitState __state)
        {
            Restore(__instance, __state);
            if (__state?.Target == null) return;
            // 生命值差值＝护甲、抗性、溢出都算完的“实际打掉的血”。
            float damage = Math.Max(0f, __state.VitalityBefore - __state.Target.Vitality);
            WeaponEnergyRuntime.AccountDamage(__instance, __state.Target, damage);
            if (__result) WeaponEnergyRuntime.Hit(__instance, __state.Target, __state.WasAlive);
        }

        private static Exception Finalizer(Projectile __instance, HitState __state, Exception __exception)
        {
            Restore(__instance, __state);
            return __exception;
        }
    }

    // 回能加成的来源扫描：武器/穿戴 tag、身上的 affliction、已有天赋，统一按 id 约定命中。
    internal sealed class ModifierScan
    {
        private readonly HashSet<Identifier> tags = new HashSet<Identifier>();
        private readonly HashSet<Identifier> afflictions = new HashSet<Identifier>();
        private readonly HashSet<Identifier> talents = new HashSet<Identifier>();
        internal readonly HashSet<Identifier> Ids = new HashSet<Identifier>();

        internal ModifierScan(Character holder, Item weapon)
        {
            AddItemTags(weapon);
            if (holder == null) return;
            if (holder.Inventory != null)
            {
                int slotCount = Math.Min(holder.Inventory.Capacity, holder.Inventory.SlotTypes.Length);
                for (int i = 0; i < slotCount; i++)
                {
                    var slotType = holder.Inventory.SlotTypes[i];
                    // 只认穿戴槽；手持的两把武器不叠加
                    if (slotType == InvSlotType.Any || slotType == InvSlotType.None ||
                        slotType.HasFlag(InvSlotType.LeftHand) || slotType.HasFlag(InvSlotType.RightHand)) continue;
                    var item = holder.Inventory.GetItemAt(i);
                    if (item == null || (item.MaxCondition > 0f && item.Condition <= 0f)) continue;
                    AddItemTags(item);
                }
            }
            if (holder.CharacterHealth != null)
            {
                foreach (var affliction in holder.CharacterHealth.GetAllAfflictions())
                {
                    var id = affliction?.Prefab?.Identifier ?? default;
                    if (string.IsNullOrEmpty(id.Value)) continue;
                    afflictions.Add(id);
                    Ids.Add(id);
                }
            }
            if (holder.CharacterTalents != null)
            {
                foreach (var talent in holder.CharacterTalents)
                {
                    var id = talent?.Prefab?.Identifier ?? default;
                    if (string.IsNullOrEmpty(id.Value)) continue;
                    talents.Add(id);
                    Ids.Add(id);
                }
            }
        }

        private void AddItemTags(Item item)
        {
            if (item?.Prefab?.Tags == null) return;
            foreach (var tag in item.Prefab.Tags)
            {
                tags.Add(tag);
                Ids.Add(tag);
            }
        }

        internal bool HasTag(Identifier id) => tags.Contains(id);
        internal bool HasAffliction(Identifier id) => afflictions.Contains(id);
        internal bool HasTalent(Identifier id) => talents.Contains(id);
    }

    internal static class WeaponEnergyRuntime
    {
        private static readonly Identifier SedationId = "Touhou_Silence_Sedation".ToIdentifier();
        private static readonly Identifier RecoveryId = "Touhou_Silence_ControlRecovery".ToIdentifier();
        private static readonly Identifier SleepRecoveryId = "Touhou_Silence_SleepRecovery".ToIdentifier();
        private sealed class Shot
        {
            internal bool Enhanced;
            internal bool Recharged;
            internal TouhouWeaponEnergy Owner;
            internal WeaponEnergySettings Settings;
            internal readonly HashSet<Character> HitTargets = new HashSet<Character>();
        }
        private static readonly WeaponEnergySettings Defaults = new WeaponEnergySettings();
        private static ConditionalWeakTable<Projectile, Shot> shots = new ConditionalWeakTable<Projectile, Shot>();
#if SERVER
        [ThreadStatic] internal static Client ButtonSender;
#endif

        internal static bool OwnContext =>
#if SERVER
            GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
            GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static void Clear() => shots = new ConditionalWeakTable<Projectile, Shot>();

        internal static void Stamp(Projectile projectile, bool enhanced, TouhouWeaponEnergy owner = null)
        {
            shots.Remove(projectile);
            shots.Add(projectile, new Shot { Enhanced = enhanced, Owner = owner, Settings = owner?.Settings ?? Defaults });
        }

        internal static void Forget(Projectile projectile) => shots.Remove(projectile);
        internal static bool HasShot(Projectile projectile) => shots.TryGetValue(projectile, out _);
        internal static float NonLethalMultiplier(Projectile projectile)
            => shots.TryGetValue(projectile, out var shot) ? shot.Settings.NonLethalDamageMultiplier : 1f;

        private static float Strength(Character target, Identifier id)
            => target.CharacterHealth.GetAffliction(id)?.Strength ?? 0f;

        internal static void Apply(Character target, Identifier id, float strength, Character source, bool stack = false)
        {
            var prefab = AfflictionPrefab.Prefabs.Find(candidate => candidate.Identifier == id);
            if (prefab == null) return;
            // 原生入口会乘 100/最大生命；这里只抵消体力缩放，抗性和免疫仍交给原生。
            target.CharacterHealth.ApplyAffliction(null,
                prefab.Instantiate(strength * target.MaxVitality / 100f, source), allowStacking: stack);
        }

        private static void Stun(Character target, float seconds)
        {
            float resistance = target.CharacterHealth.GetResistance(AfflictionPrefab.Stun, LimbType.None);
            if (!float.IsFinite(resistance)) return;
            // SetStun 自己查免疫，但不会替调用者计算状态抗性。
            target.SetStun(seconds * Math.Clamp(1f - resistance, 0f, 1f));
        }

        // 命中回能：生命值差值由命中补丁算好后传进来，这里只负责归属到发射的那把枪。
        internal static void AccountDamage(Projectile projectile, Character target, float damage)
        {
            if (!Authority || damage <= 0f || target == null) return;
            if (!shots.TryGetValue(projectile, out var shot) || shot.Owner == null) return;
            if (target == projectile.User) return;
            if (target.Params.IsMachine && !shot.Settings.DamageRegenOnMachines) return;
            shot.Owner.AccumulateDamage(damage);
        }

        private static bool ModeMatches(EffectMode mode, bool enhanced)
            => mode == EffectMode.Always || (mode == EffectMode.Enhanced) == enhanced;

        internal static void Hit(Projectile projectile, Character target, bool? aliveBeforeHit = null)
        {
            if (!Authority || target == null || target.Removed || target.CharacterHealth == null ||
                !shots.TryGetValue(projectile, out var shot) || !shot.HitTargets.Add(target)) return;
            Character source = projectile.User;
            var settings = shot.Settings;
            // 同弹多肢体只进一次；击杀算命中活物，但打尸体不算。
            if ((!settings.HitRechargeOncePerProjectile || !shot.Recharged) &&
                shot.Owner?.RechargeHit(source, target, aliveBeforeHit ?? !target.IsDead, shot.Enhanced) == true)
                shot.Recharged = true;
            if (target.IsDead) return;

            // 普通 affliction：按 when 区分普通/强化，护甲默认生效。
            foreach (var affliction in settings.AttackAfflictions)
            {
                if (!ModeMatches(affliction.Mode, shot.Enhanced)) continue;
                if (affliction.Probability < 1f && Rand.Value() > affliction.Probability) continue;
                if (affliction.IgnoreArmor)
                    Apply(target, affliction.Affliction, affliction.Strength *
                        ArchiveRuntime.Bonus(projectile.Launcher, target, ArchiveBonusKind.Affliction, affliction.Affliction), source);
                else
                    ApplyThroughArmor(target, affliction.Affliction, affliction.Strength *
                        ArchiveRuntime.Bonus(projectile.Launcher, target, ArchiveBonusKind.Affliction, affliction.Affliction), source);
            }

            bool protect = WeaponEnergyRules.ProtectFromHardControl(target.IsHuman, target.IsHumanoid, target.IsPlayer);
            bool hardControlAllowed = WeaponEnergyRules.AllowHardControl(protect, target.Params.IsMachine,
                target.Params.UseBossHealthBar, target.Params.Health.StunImmunity);
            if (target.IsUnconscious || Strength(target, SleepRecoveryId) > 0f) return;

            foreach (var stun in settings.StunEffects)
            {
                if (!ModeMatches(stun.Mode, shot.Enhanced) || stun.Duration <= 0f) continue;
                if (stun.HardControl && !hardControlAllowed) continue;
                if (RegenModifierDef.IsSet(stun.CooldownAffliction) && Strength(target, stun.CooldownAffliction) > 0f) continue;
                Stun(target, stun.Duration * ArchiveRuntime.Bonus(projectile.Launcher, target, ArchiveBonusKind.StunDuration));
                if (stun.Cooldown > 0f && RegenModifierDef.IsSet(stun.CooldownAffliction))
                    Apply(target, stun.CooldownAffliction, stun.Cooldown, source);
            }

            foreach (var sedation in settings.SedationEffects)
            {
                if (!ModeMatches(sedation.Mode, shot.Enhanced)) continue;
                if (!hardControlAllowed) continue;
                float dose = shot.Enhanced ? sedation.Enhanced : sedation.Normal;
                dose *= ArchiveRuntime.Bonus(projectile.Launcher, target, ArchiveBonusKind.Sedation, sedation.Affliction);
                if (sedation.LargeTargetVitality > 0f && target.MaxVitality >= sedation.LargeTargetVitality)
                    dose *= sedation.LargeTargetMultiplier;
                // 存成百分比，换麻醉阈值时不必同时改状态的 maxstrength。
                if (dose > 0f) Apply(target, sedation.Affliction, dose * 100f / sedation.Threshold, source, true);
                Affliction current = target.CharacterHealth.GetAffliction(sedation.Affliction);
                if (current == null || current.Strength < 99.9f) continue;
                float duration = sedation.StunDuration * ArchiveRuntime.Bonus(projectile.Launcher, target, ArchiveBonusKind.StunDuration);
                Stun(target, duration);
                if (duration > 0f && target.Stun > 0f &&
                    target.CharacterHealth.GetResistance(AfflictionPrefab.Stun, LimbType.None) < 1f)
                    ArchiveRuntime.Record(projectile.Launcher?.GetComponent<TouhouWeaponArchive>(), target, source, true);
                current.Strength = 0f;
                float recovery = duration + sedation.Recovery;
                if (recovery > 0f && RegenModifierDef.IsSet(sedation.RecoveryAffliction))
                    Apply(target, sedation.RecoveryAffliction, recovery, source);
                target.HealthUpdateInterval = 0f;
            }
        }

        // 走引擎伤害入口：护甲、抗性、命中音效、技能经验都照常；
        // 直接 ApplyAffliction 的版本见 ignorearmor。
        private static void ApplyThroughArmor(Character target, Identifier id, float strength, Character source)
        {
            var limb = target.AnimController?.MainLimb;
            var prefab = AfflictionPrefab.Prefabs.Find(candidate => candidate.Identifier == id);
            if (prefab == null) return;
            var affliction = prefab.Instantiate(strength * target.MaxVitality / 100f, source);
            if (limb == null)
            {
                target.CharacterHealth.ApplyAffliction(null, affliction, allowStacking: false);
                return;
            }
            target.DamageLimb(limb.WorldPosition, limb, new List<Affliction> { affliction },
                stun: 0f, playSound: false, attackImpulse: Vector2.Zero, attacker: source, allowStacking: false);
        }
    }
}
