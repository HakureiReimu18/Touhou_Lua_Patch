using System;
using Barotrauma;
using Barotrauma.Abilities;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.ProximityAura
{
    /*
      天赋「范围光环」：给范围内的友方船员维持一个 affliction，人走了就摘掉。

      和原版 CharacterAbilityApplyStatusEffectsToAllies 的差别只在于「什么时候动手」：
      原版每个间隔周期都会给范围内每个人重新施加一次，重复施加会一直刷新 duration、
      并持续产生联机同步事件；本能力每拍只算一遍「谁该有 / 谁已经有 / 谁不该有」，
      只有真的进出范围时才施加或清除，稳态下不碰任何 affliction。

      <CharacterAbilityProximityAura affliction="Aunn_Koma_Inu_Others" strength="10"
                                    maxdistance="800" gateaffliction="Touhou_Aunn_Character_Effect"
                                    requireaffliction="Aunn_Koma_Inu" allowself="false"/>

        affliction / afflictionid     要维持的 affliction
        strength / amount / Strength  强度，默认 0（施加后写回，规避落地的 100/最大生命 缩放）
        maxdistance / range           范围，默认无上限（跟原版 ApplyStatusEffectsToAllies 的默认一致）
        allowself                     默认 true，跟原版一致
        gateaffliction                持有者先要有这个 affliction，光环才成立
        requireaffliction             持有者还要有这个 affliction（例如「狛犬形态」）

      注意：光环成立条件（gateaffliction / requireaffliction）必须写在能力里而不是写在
      能力组的 <Conditions> 里 —— 条件不匹配时引擎根本不会调 ApplyEffect，那样就没人负责
      把已经发出去的 buff 收回来。
    */

    internal sealed class CharacterAbilityProximityAura : CharacterAbility
    {
        private const string DebugName = "CharacterAbilityProximityAura";

        private readonly Identifier afflictionId;
        private readonly float strength;
        private readonly float maxDistance;
        private readonly bool allowSelf;
        private readonly Identifier gateAffliction;
        private readonly Identifier requireAffliction;

        // 原版每次触发都要在 AfflictionPrefab.Prefabs 里线性扫一遍，首次解析成功后就缓存住
        private AfflictionPrefab cachedPrefab;
        private bool prefabErrorLogged;

        public override bool AppliesEffectOnIntervalUpdate => true;

        public CharacterAbilityProximityAura(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement)
            : base(characterAbilityGroup, abilityElement)
        {
            afflictionId = abilityElement.GetAttributeIdentifier("afflictionid",
                abilityElement.GetAttributeIdentifier("affliction", Identifier.Empty));
            strength = abilityElement.GetAttributeFloat(0f, "strength", "amount", "Strength");
            maxDistance = abilityElement.GetAttributeFloat(float.MaxValue, "maxdistance", "range");
            allowSelf = abilityElement.GetAttributeBool("allowself", true);
            gateAffliction = abilityElement.GetAttributeIdentifier("gateaffliction", Identifier.Empty);
            requireAffliction = abilityElement.GetAttributeIdentifier("requireaffliction", Identifier.Empty);

            if (afflictionId.IsEmpty)
            {
                DebugConsole.ThrowError($"Error in talent {CharacterTalent.DebugIdentifier} - {DebugName} 缺少 affliction 属性。",
                    null, abilityElement.ContentPackage);
            }
        }

        // 注意：publicized 程序集里这些成员是 public，重写必须写 public override
        public override void ApplyEffect() => UpdateAura();

        public override void ApplyEffect(AbilityObject abilityObject) => UpdateAura();

        private void UpdateAura()
        {
            Character self = Character;
            if (self == null || afflictionId.IsEmpty) return;

            // 光环成立的前提：自己活着，且 gate / require 两个 affliction 都在
            bool sourceActive = !self.IsDead && HasRequiredAfflictions(self);

            foreach (Character other in Character.GetFriendlyCrew(self))
            {
                if (other == null || other.Removed) continue;
                if (!allowSelf && other == self) continue;

                bool inRange = sourceActive && !other.IsDead && other.CharacterHealth != null
                    && Vector2.DistanceSquared(other.WorldPosition, self.WorldPosition) <= maxDistance * maxDistance;

                bool has = other.CharacterHealth != null
                    && other.CharacterHealth.GetAffliction(afflictionId) != null;

                if (inRange)
                {
                    if (!has) ApplyTo(other);
                }
                else if (has && !IsCoveredByOtherSource(other))
                {
                    RemoveFrom(other);
                }
            }
        }

        // 队伍里可能有第二个光环持有者，摘人之前先问一句还有没有别人罩着他。
        // 只有真要摘人时才跑这条，平时不占开销。
        private bool IsCoveredByOtherSource(Character target)
        {
            foreach (Character source in Character.CharacterList)
            {
                if (source == null || source == Character || source.Removed || source.IsDead) continue;
                if (source.TeamID != target.TeamID) continue;
                if (!allowSelf && source == target) continue;
                if (!HasRequiredAfflictions(source)) continue;
                if (Vector2.DistanceSquared(source.WorldPosition, target.WorldPosition) <= maxDistance * maxDistance)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasRequiredAfflictions(Character character)
        {
            if (character == null || character.CharacterHealth == null) return false;
            if (!gateAffliction.IsEmpty && character.CharacterHealth.GetAffliction(gateAffliction) == null) return false;
            if (!requireAffliction.IsEmpty && character.CharacterHealth.GetAffliction(requireAffliction) == null) return false;
            return true;
        }

        private void ApplyTo(Character target)
        {
            AfflictionPrefab prefab = GetAfflictionPrefab();
            if (prefab == null || target.CharacterHealth == null) return;

            target.CharacterHealth.ApplyAffliction(null, prefab.Instantiate(strength));

            // 落地时会乘 100/最大生命，这里写回目标强度（和 Lua 的 TT.apply_affliction 一个做法）；
            // 写回必须自己夹到 maxstrength —— 引擎施加时会夹，直接写属性不会
            Affliction applied = target.CharacterHealth.GetAffliction(afflictionId);
            if (applied != null) applied.Strength = Math.Min(strength, prefab.MaxStrength);
        }

        private void RemoveFrom(Character target)
        {
            if (target == null || target.CharacterHealth == null) return;

            // 强度归零，引擎下一拍自己移除（和 Lua 的 TT.clear_affliction 一个做法）
            Affliction current = target.CharacterHealth.GetAffliction(afflictionId);
            if (current != null) current.Strength = 0f;
        }

        private AfflictionPrefab GetAfflictionPrefab()
        {
            if (cachedPrefab != null) return cachedPrefab;

            cachedPrefab = AfflictionPrefab.Prefabs.Find(a => a.Identifier == afflictionId);
            if (cachedPrefab == null && !prefabErrorLogged)
            {
                prefabErrorLogged = true;
                DebugConsole.ThrowError($"Error in talent {CharacterTalent.DebugIdentifier} - {DebugName} 找不到 affliction {afflictionId}。");
            }

            return cachedPrefab;
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名类型，这里截住本能力的元素名，换成自己的实现。
    // 类名必须全局唯一：LuaCs 按类名登记补丁，重名的只会有一个生效（AbilityLoadPatch 就撞过这个坑）。
    [HarmonyPatch(typeof(CharacterAbility), nameof(CharacterAbility.Load))]
    internal static class ProximityAuraLoadPatch
    {
        private const string ElementName = "characterabilityproximityaura";

        private static bool Prefix(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
                                   ref CharacterAbility __result)
        {
            if (!string.Equals(abilityElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new CharacterAbilityProximityAura(characterAbilityGroup, abilityElement);
            return false;
        }
    }

    // 补丁要插件自己注册：本模组的惯例是每个插件在 OnLoadCompleted 里手动 PatchAll，
    // 光写 [HarmonyPatch] 特性不会被自动打上（本文件一开始就是漏了这段，能力一直"找不到"）。
    public sealed class ProximityAuraPlugin : Barotrauma.LuaCs.IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.proximityaura");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ProximityAuraLoadPatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
        }
    }
}
