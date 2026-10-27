using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.DoctorTalents
{
    // 奇迹守护者：每巡回第一次治疗的目标获得 10% 物理抗性，并把所受伤害的一半转给医生。
    // 计量方式照绑定护符：ApplyAttack 前后对同一批 affliction 取存量差，只对真实入账的部分结算。
    public sealed class DoctorTalentsPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool initialized;

        public void Initialize() => harmony = new Harmony("touhou.doctortalents." + DoctorRules.Side);
        public void PreInitPatching() { }

        public void OnLoadCompleted()
        {
            if (initialized) return;
            initialized = true;
            harmony.UnpatchSelf();
            Patch(AccessTools.Method(typeof(CharacterAbility), "Load"), nameof(DoctorHooks.Load), true);
            Patch(typeof(Character).GetMethod("ApplyAttack", BindingFlags.Public | BindingFlags.Instance),
                nameof(DoctorHooks.AttackPrefix), true, nameof(DoctorHooks.AttackPostfix));
            LuaCsSetup.Instance.Hook.Add("roundStart", "Touhou.DoctorTalents.Start." + DoctorRules.Side, DoctorHooks.RoundStart);
            LuaCsSetup.Instance.Hook.Add("roundEnd", "Touhou.DoctorTalents.End." + DoctorRules.Side, DoctorHooks.RoundEnd);
        }

        private void Patch(MethodBase target, string name, bool prefix, string postfix = null)
        {
            if (target == null)
            {
                DebugConsole.ThrowError("[DoctorTalents] 找不到接口：" + name);
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix ? new HarmonyMethod(typeof(DoctorHooks), name) : null,
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(DoctorHooks), postfix));
            }
            catch (Exception exception)
            {
                DebugConsole.ThrowError("[DoctorTalents] 接口未挂上：" + name + " / " + exception.Message);
            }
        }

        public void Dispose()
        {
            LuaCsSetup.Instance.Hook.Remove("roundStart", "Touhou.DoctorTalents.Start." + DoctorRules.Side);
            LuaCsSetup.Instance.Hook.Remove("roundEnd", "Touhou.DoctorTalents.End." + DoctorRules.Side);
            harmony?.UnpatchSelf();
            DoctorHooks.Clear();
            initialized = false;
        }
    }

    internal static class DoctorRules
    {
#if SERVER
        internal const string Side = "server";
        internal static bool OwnContext => GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
        internal const string Side = "client";
        internal static bool OwnContext => GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static void Warn(string text) => DebugConsole.NewMessage("[DoctorTalents] " + text, Color.OrangeRed);
    }

    internal sealed class CharacterAbilityMiracleGuardian : CharacterAbility
    {
        internal readonly float Resistance;
        internal readonly float Share;

        public CharacterAbilityMiracleGuardian(CharacterAbilityGroup group, ContentXElement element) : base(group, element)
        {
            Resistance = MathHelper.Clamp(element.GetAttributeFloat(0.1f, "resistance"), 0f, 1f);
            Share = MathHelper.Clamp(element.GetAttributeFloat(0.5f, "share"), 0f, 1f);
        }

        public override void ApplyEffect(AbilityObject abilityObject)
        {
            try
            {
                if (!DoctorRules.Authority) return;
                var target = (abilityObject as IAbilityCharacter)?.Character;
                if (target != null) DoctorHooks.TryLink(Character, target, Resistance, Share);
            }
            catch (Exception exception)
            {
                DoctorRules.Warn("守护链接失败：" + exception.Message);
            }
        }
    }

    internal static class DoctorHooks
    {
        private static readonly Identifier WardId = "Touhou_Doctor_MiracleGuardian_Ward".ToIdentifier();
        private static readonly Identifier TalentId = "Touhou_Doctor_MiracleGuardian".ToIdentifier();
        // 攻击里哪些 affliction 算“伤害”。比绑定护符默认表多带上撕裂/钝伤/枪伤等物理类型。
        private static readonly HashSet<string> DamageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "damage", "burn", "bleeding", "lacerations", "blunttrauma", "gunshotwound",
            "bitewounds", "explosiondamage", "internaldamage", "acidburn", "radiationsickness",
            "poison", "deeptissueinjury"
        };
        private static readonly Dictionary<Character, GuardianLink> byDoctor = new();
        private static readonly Dictionary<Character, GuardianLink> byTarget = new();
        private static readonly Dictionary<Character, Snapshot> pending = new();

        private sealed class GuardianLink
        {
            internal Character Doctor;
            internal Character Target;
            internal float Share;
            internal float Resistance;
        }

        private sealed class Snapshot
        {
            internal readonly Dictionary<AfflictionPrefab, float> Before = new();
            internal LimbType Limb;
        }

        internal static bool Load(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
            ref CharacterAbility __result)
        {
            if (!DoctorRules.OwnContext) return true;
            if (string.Equals(abilityElement.Name.LocalName, "characterabilitymiracleGuardian",
                    StringComparison.OrdinalIgnoreCase))
            {
                __result = new CharacterAbilityMiracleGuardian(characterAbilityGroup, abilityElement);
                return false;
            }
            return true;
        }

        internal static void TryLink(Character doctor, Character target, float resistance, float share)
        {
            if (!DoctorRules.Authority || doctor == null || target == null || doctor == target) return;
            if (doctor.Removed || doctor.IsDead || target.Removed || target.IsDead) return;
            if (target.TeamID != doctor.TeamID) return;
            if (byDoctor.ContainsKey(doctor) || byTarget.ContainsKey(target)) return;

            var link = new GuardianLink
            {
                Doctor = doctor,
                Target = target,
                Share = MathHelper.Clamp(share, 0f, 1f),
                Resistance = MathHelper.Clamp(resistance, 0f, 1f)
            };
            byDoctor[doctor] = link;
            byTarget[target] = link;
            ApplyWard(target);
        }

        private static void ApplyWard(Character target)
        {
            if (target?.CharacterHealth == null) return;
            if (!AfflictionPrefab.Prefabs.TryGet(WardId, out AfflictionPrefab prefab)) return;
            target.CharacterHealth.ApplyAffliction(target.AnimController?.MainLimb,
                new Affliction(prefab, 1f), true, false, true);
        }

        private static void RemoveWard(Character target)
        {
            if (target?.CharacterHealth == null) return;
            target.CharacterHealth.ReduceAfflictionOnAllLimbs(WardId, 1f);
        }

        private static void RemoveLink(GuardianLink link)
        {
            if (link == null) return;
            byDoctor.Remove(link.Doctor);
            byTarget.Remove(link.Target);
            pending.Remove(link.Target);
            RemoveWard(link.Target);
        }

        internal static object RoundStart(object[] args)
        {
            if (DoctorRules.Authority) Clear();
            return null;
        }

        internal static object RoundEnd(object[] args)
        {
            if (DoctorRules.Authority) Clear();
            return null;
        }

        internal static void Clear()
        {
            foreach (var link in byDoctor.Values.ToList()) RemoveWard(link.Target);
            byDoctor.Clear();
            byTarget.Clear();
            pending.Clear();
        }

        // ApplyAttack 前缀：只对本次攻击里计入伤害的 affliction 做存量快照。
        internal static void AttackPrefix(Character __instance, Character attacker, Attack attack)
        {
            try
            {
                if (!DoctorRules.Authority || __instance == null || attack?.Afflictions == null) return;
                if (attacker == null || attacker == __instance || __instance.IsDead || __instance.Removed) return;
                if (!byTarget.TryGetValue(__instance, out var link) || link.Target != __instance) return;

                var snapshot = new Snapshot();
                foreach (var affliction in attack.Afflictions.Keys)
                {
                    var prefab = affliction?.Prefab;
                    if (prefab == null || affliction.Strength <= 0f) continue;
                    if (!DamageTypes.Contains(prefab.AfflictionType.Value)) continue;
                    if (snapshot.Before.ContainsKey(prefab)) continue;
                    snapshot.Before[prefab] = StockOf(__instance, prefab);
                }
                if (snapshot.Before.Count > 0) pending[__instance] = snapshot;
                else pending.Remove(__instance);
            }
            catch (Exception exception)
            {
                DoctorRules.Warn("伤害计量前缀失败：" + exception.Message);
            }
        }

        // ApplyAttack 后缀：按真实入账的差值把 share 比例转给医生。
        internal static void AttackPostfix(Character __instance, Limb targetLimb)
        {
            try
            {
                if (!DoctorRules.Authority || __instance == null) return;
                if (!pending.TryGetValue(__instance, out var snapshot)) return;
                pending.Remove(__instance);
                if (!byTarget.TryGetValue(__instance, out var link)) return;

                var doctor = link.Doctor;
                if (doctor == null || doctor.Removed || doctor.IsDead ||
                    doctor.Info == null || !doctor.Info.UnlockedTalents.Contains(TalentId))
                {
                    RemoveLink(link);
                    return;
                }

                bool movedAny = false;
                LimbType limbType = targetLimb?.type ?? LimbType.Torso;
                foreach (var pair in snapshot.Before)
                {
                    float delta = StockOf(__instance, pair.Key) - pair.Value;
                    if (delta <= 0.01f) continue;
                    float moved = delta * link.Share;
                    if (moved <= 0.01f) continue;
                    Transfer(link, pair.Key, limbType, moved);
                    movedAny = true;
                }

                if (movedAny)
                {
                    __instance.CharacterHealth.RecalculateVitality();
                    doctor.CharacterHealth.RecalculateVitality();
                }
            }
            catch (Exception exception)
            {
                DoctorRules.Warn("伤害转移失败：" + exception.Message);
            }
        }

        private static void Transfer(GuardianLink link, AfflictionPrefab prefab, LimbType limbType, float moved)
        {
            var doctor = link.Doctor;
            var limb = FindLimb(doctor, limbType) ?? doctor.AnimController?.MainLimb;
            if (limb == null) return;

            float maxVitality = Math.Max(doctor.CharacterHealth.MaxVitality, 1f);
            float resistance = doctor.CharacterHealth.GetResistance(prefab, limbType);
            float effectiveResistance = MathHelper.Clamp(resistance * 0.5f, 0f, 0.95f);
            float applied = Math.Min(moved * (maxVitality / 100f) / Math.Max(1f - effectiveResistance, 0.05f),
                prefab.MaxStrength);

            if (applied > 0.01f)
                doctor.CharacterHealth.ApplyAffliction(limb, new Affliction(prefab, applied), true, false, true);

            link.Target.CharacterHealth.ReduceAfflictionOnAllLimbs(prefab.Identifier, moved);
        }

        private static float StockOf(Character character, AfflictionPrefab prefab)
        {
            float total = 0f;
            foreach (var affliction in character.CharacterHealth.GetAllAfflictions())
                if (affliction.Prefab == prefab) total += affliction.Strength;
            return total;
        }

        private static Limb FindLimb(Character character, LimbType type)
        {
            var limbs = character?.AnimController?.Limbs;
            if (limbs == null) return null;
            foreach (var limb in limbs)
                if (limb != null && limb.type == type && !limb.IsSevered) return limb;
            return null;
        }
    }
}
