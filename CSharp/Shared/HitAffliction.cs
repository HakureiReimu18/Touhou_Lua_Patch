using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace Touhou.HitAffliction
{
    /*
      天赋「命中附加」：把天赋附加的 affliction 打到真正命中的那根肢体上，并归到攻击者名下。

      原版两条写法（CharacterAbilityGiveAffliction，以及 StatusEffect 里 target="Character"）都有
      同一个毛病：LimbSpecific 的 affliction 会给每根肢体各加一份，血量按份数扣；而且都不写
      lastAttackers，NPC 不会把玩家当威胁。本能力在 ApplyAttack 期间记下当次命中的肢体，改走
      DamageLimb，抗性、音效、断肢判定、LastDamage、归属就都跟武器命中一致了。

      简化写法：
          <CharacterAbilityHitAffliction affliction="Touhou_Freeze" strength="1.5"/>
          <CharacterAbilityHitAffliction target="attacker" affliction="slow" strength="10"/>

          affliction / afflictionid     要附加的 affliction
          strength / amount / Strength  强度，三个名字都认，默认 0
          multiplystrengthbyskill       强度再乘以该技能等级
          setvalue                      true 时不许叠加
          target                        hitlimb（默认）/ attacker
          multiplier                    none（默认）/ stat / inherit
          armor                         apply（默认，吃护甲）/ ignore
          creditattacker                附加物不产生伤害时是否补记归属，默认 true
          fallback                      拿不到命中肢体时：vanilla（默认）/ none / nearest
          cooldown                      两次触发之间的最短间隔（秒），默认 0 表示不限速

      复合写法按 StatusEffect 自己声明的 target 分派：Limb 打命中肢体（推荐）、AllLimbs 打目标全部
      健全肢体、This 打自己、Character / UseTarget 打被攻击者、NearbyCharacters 按 range 打附近角色。
      target=Character 时只有 limbspecific="true" 的 affliction 会逐肢体各施加一份，limbspecific="false"
      的只施加一次（原版辐射病给 burn 写 dividebylimbcount、给 organdamage 不写，就是这个区分）。

      另外：写在 AbilityGroupInterval 里的 StatusEffect，其施加量会被乘上"距上次触发的秒数"
      （CharacterAbility.EffectDeltaTime），所以那种位置的 amount 实际是"每秒的量"；要改成
      "每次触发固定量"必须写 disabledeltatime="true"。

      倍率：Limb.AddDamage 只在 AffectedByAttackMultipliers（默认 !IsBuff）为真时才用 multiplier，
      所以写了 isbuff="true" 的附加物天然不吃倍率。none 恒为 1；stat 是角色面板倍率；inherit 再乘上
      Attack.DamageMultiplier × AbilityAttackData.DamageMultiplier，即本次武器命中的完整口径。

      armor="ignore" 只对简化写法有效，直接走 ApplyAffliction，代价是少掉命中音效、LastDamage、
      OnDamaged 与武器技能经验，但 OnAddDamageAffliction 那类增伤天赋仍然生效。
    */

    /// <summary>ApplyAttack 期间的命中上下文，栈式，支持爆炸等嵌套结算。</summary>
    internal static class HitContext
    {
        public readonly struct Frame
        {
            public readonly Character Attacker;
            public readonly Character Victim;
            public readonly Limb HitLimb;
            public readonly Attack Attack;
            public readonly Vector2 WorldPosition;

            public Frame(Character attacker, Character victim, Limb hitLimb, Attack attack, Vector2 worldPosition)
            {
                Attacker = attacker;
                Victim = victim;
                HitLimb = hitLimb;
                Attack = attack;
                WorldPosition = worldPosition;
            }
        }

        [ThreadStatic] private static List<Frame> stack;

        // 场上没有本能力的实例时，整套补丁一个操作都不做。
        private static int liveInstanceCount;

        public static bool Enabled => liveInstanceCount > 0;

        public static void RegisterInstance() => liveInstanceCount++;

        public static void ResetInstanceCount() => liveInstanceCount = 0;

        private static List<Frame> Stack => stack ??= new List<Frame>(4);

        public static Frame? Current => Stack.Count > 0 ? Stack[Stack.Count - 1] : (Frame?)null;

        public static int Push(Character attacker, Character victim, Limb hitLimb, Attack attack, Vector2 worldPosition)
        {
            Stack.Add(new Frame(attacker, victim, hitLimb, attack, worldPosition));
            return Stack.Count;
        }

        public static void Truncate(int depth)
        {
            if (depth < 0) depth = 0;
            int count = Stack.Count;
            if (count <= depth) return;
            if (count == depth + 1) Stack.RemoveAt(count - 1);
            else Stack.RemoveRange(depth, count - depth);
        }

        public static void Clear() => Stack.Clear();

        public static bool TryGetFor(Character victim, out Frame frame)
        {
            frame = default;
            if (victim == null) return false;
            Frame? current = Current;
            if (current == null) return false;
            Frame f = current.Value;
            if (f.Victim != victim) return false;
            frame = f;
            return true;
        }
    }

    public sealed class HitAfflictionPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.hitaffliction");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(AbilityLoadPatch));
            harmony.PatchAll(typeof(ApplyAttackPatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            HitContext.Clear();
            HitContext.ResetInstanceCount();
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名类型，这里截住本能力的元素名，换成自己的实现。
    [HarmonyPatch(typeof(CharacterAbility), nameof(CharacterAbility.Load))]
    internal static class AbilityLoadPatch
    {
        private const string ElementName = "characterabilityhitaffliction";

        private static bool Prefix(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
                                   ref CharacterAbility __result)
        {
            if (!string.Equals(abilityElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new CharacterAbilityHitAffliction(characterAbilityGroup, abilityElement);
            return false;
        }
    }

    // OnAttack / OnAttacked 天赋是在 Character.ApplyAttack 内部（AbilityAttackData 构造时）触发的，
    // 所以进栈之后天赋就能读到当次命中的肢体。
    [HarmonyPatch(typeof(Character), nameof(Character.ApplyAttack))]
    internal static class ApplyAttackPatch
    {
        private static void Prefix(Character __instance, Character attacker, Vector2 worldPosition,
                                   Attack attack, Limb targetLimb, ref int __state)
        {
            if (!HitContext.Enabled) { __state = -1; return; }
            __state = HitContext.Push(attacker, __instance, targetLimb, attack, worldPosition);
        }

        private static void Finalizer(int __state)
        {
            if (__state > 0) HitContext.Truncate(__state - 1);
        }
    }

    internal sealed class CharacterAbilityHitAffliction : CharacterAbility
    {
        private enum TargetKind { HitLimb, Attacker }

        private enum MultiplierMode { None, Stat, Inherit }

        private enum FallbackMode { Vanilla, None, Nearest }

        private enum ArmorMode { Apply, Ignore }

        private const string DebugName = "CharacterAbilityHitAffliction";

        private readonly Identifier afflictionId;
        private readonly float strength;
        private readonly Identifier multiplyStrengthBySkill;
        private readonly bool setValue;
        private readonly TargetKind targetKind;
        private readonly MultiplierMode multiplierMode;
        private readonly FallbackMode fallbackMode;
        private readonly bool creditAttacker;
        private readonly ArmorMode armorMode;
        private readonly float cooldown;

        private readonly List<StatusEffect> statusEffects;

        private double nextAllowedTime;

        // 原版每次触发都要在 AfflictionPrefab.Prefabs 里线性扫一遍，这里首次解析成功后就缓存住。
        private AfflictionPrefab cachedAfflictionPrefab;
        private bool afflictionPrefabErrorLogged;

        public CharacterAbilityHitAffliction(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement)
            : base(characterAbilityGroup, abilityElement)
        {
            afflictionId = abilityElement.GetAttributeIdentifier("afflictionid",
                abilityElement.GetAttributeIdentifier("affliction", Identifier.Empty));
            strength = abilityElement.GetAttributeFloat(0f, "strength", "amount", "Strength");
            multiplyStrengthBySkill = abilityElement.GetAttributeIdentifier("multiplystrengthbyskill", Identifier.Empty);
            setValue = abilityElement.GetAttributeBool("setvalue", false);
            creditAttacker = abilityElement.GetAttributeBool("creditattacker", true);
            cooldown = Math.Max(0f, abilityElement.GetAttributeFloat(0f, "cooldown"));

            targetKind = ParseTargetKind(abilityElement);
            armorMode = ParseArmorMode(abilityElement);
            multiplierMode = ParseMultiplierMode(abilityElement);
            fallbackMode = ParseFallbackMode(abilityElement);

            ContentXElement statusEffectElement = abilityElement.GetChildElement("statuseffects");
            if (statusEffectElement != null)
            {
                statusEffects = CharacterAbilityGroup.ParseStatusEffects(CharacterTalent, statusEffectElement);
            }
            else if (afflictionId.IsEmpty)
            {
                DebugConsole.ThrowError($"Error in talent {CharacterTalent.DebugIdentifier} - {DebugName} needs " +
                                        "either an affliction attribute or a <StatusEffects> block.",
                                        null, abilityElement.ContentPackage);
            }

            HitContext.RegisterInstance();
        }

        private AfflictionPrefab GetAfflictionPrefab()
        {
            if (cachedAfflictionPrefab != null) return cachedAfflictionPrefab;

            cachedAfflictionPrefab = AfflictionPrefab.Prefabs.Find(a => a.Identifier == afflictionId);
            if (cachedAfflictionPrefab == null && !afflictionPrefabErrorLogged)
            {
                afflictionPrefabErrorLogged = true;
                DebugConsole.ThrowError($"Error in {DebugName} - could not find an affliction with the identifier " +
                                        $"\"{afflictionId}\".", null, CharacterTalent.Prefab.ContentPackage);
            }
            return cachedAfflictionPrefab;
        }

        private TargetKind ParseTargetKind(ContentXElement element)
        {
            string raw = element.GetAttributeString("target", "hitlimb");
            if (raw.Equals("hitlimb", StringComparison.OrdinalIgnoreCase)) return TargetKind.HitLimb;
            if (raw.Equals("attacker", StringComparison.OrdinalIgnoreCase)) return TargetKind.Attacker;
            DebugConsole.ThrowError($"Error in {DebugName} - unknown target \"{raw}\" in talent " +
                                    $"{CharacterTalent.DebugIdentifier}; expected \"hitlimb\" or \"attacker\". " +
                                    "Falling back to \"hitlimb\".", null, element.ContentPackage);
            return TargetKind.HitLimb;
        }

        private MultiplierMode ParseMultiplierMode(ContentXElement element)
        {
            string raw = element.GetAttributeString("multiplier", "none");
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return MultiplierMode.None;
            if (raw.Equals("stat", StringComparison.OrdinalIgnoreCase)) return MultiplierMode.Stat;
            if (raw.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return MultiplierMode.Inherit;
            DebugConsole.ThrowError($"Error in {DebugName} - unknown multiplier \"{raw}\" in talent " +
                                    $"{CharacterTalent.DebugIdentifier}; expected \"none\", \"stat\" or \"inherit\". " +
                                    "Falling back to \"none\".", null, element.ContentPackage);
            return MultiplierMode.None;
        }

        private ArmorMode ParseArmorMode(ContentXElement element)
        {
            string raw = element.GetAttributeString("armor", "apply");
            if (raw.Equals("apply", StringComparison.OrdinalIgnoreCase)) return ArmorMode.Apply;
            if (raw.Equals("ignore", StringComparison.OrdinalIgnoreCase))
            {
                if (element.GetChildElement("statuseffects") != null)
                {
                    DebugConsole.ThrowError($"Error in {DebugName} - armor=\"ignore\" 只对简化写法（affliction=…）生效，" +
                                            $"{CharacterTalent.DebugIdentifier} 的 <StatusEffects> 复合写法会忽略它。",
                                            null, element.ContentPackage);
                }
                return ArmorMode.Ignore;
            }
            DebugConsole.ThrowError($"Error in {DebugName} - unknown armor \"{raw}\" in talent " +
                                    $"{CharacterTalent.DebugIdentifier}; expected \"apply\" or \"ignore\". " +
                                    "Falling back to \"apply\".", null, element.ContentPackage);
            return ArmorMode.Apply;
        }

        private FallbackMode ParseFallbackMode(ContentXElement element)
        {
            string raw = element.GetAttributeString("fallback", "vanilla");
            if (raw.Equals("vanilla", StringComparison.OrdinalIgnoreCase)) return FallbackMode.Vanilla;
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) return FallbackMode.None;
            if (raw.Equals("nearest", StringComparison.OrdinalIgnoreCase)) return FallbackMode.Nearest;
            DebugConsole.ThrowError($"Error in {DebugName} - unknown fallback \"{raw}\" in talent " +
                                    $"{CharacterTalent.DebugIdentifier}; expected \"vanilla\", \"none\" or \"nearest\". " +
                                    "Falling back to \"vanilla\".", null, element.ContentPackage);
            return FallbackMode.Vanilla;
        }

        public override void ApplyEffect()
        {
            Apply(null, null, null);
        }

        public override void ApplyEffect(AbilityObject abilityObject)
        {
            Character victim = (abilityObject as IAbilityCharacter)?.Character;
            AbilityAttackData attackData = abilityObject as AbilityAttackData;
            Character attacker = attackData?.Attacker;
            Apply(victim, attacker, attackData);
        }

        private void Apply(Character victimFromObject, Character attackerFromObject, AbilityAttackData attackData)
        {
            Character victim = victimFromObject ?? Character;
            Character attacker = attackerFromObject;

            HitContext.Frame frame = default;
            bool hasFrame = HitContext.TryGetFor(targetKind == TargetKind.Attacker ? attacker : victim, out frame)
                            || HitContext.TryGetFor(victim, out frame);

            Limb hitLimb = hasFrame ? frame.HitLimb : null;
            if (hasFrame && attacker == null) attacker = frame.Attacker;

            Character targetCharacter = targetKind == TargetKind.Attacker
                ? (attacker ?? Character)
                : victim;

            if (targetKind == TargetKind.HitLimb)
            {
                switch (fallbackMode)
                {
                    case FallbackMode.None:
                        if (hitLimb == null) return;
                        break;
                    case FallbackMode.Nearest:
                        if (hitLimb == null)
                        {
                            hitLimb = FindNearestLimb(victim, hasFrame ? frame.WorldPosition : victim.WorldPosition);
                        }
                        break;
                }
            }

            if (cooldown > 0f)
            {
                double now = Timing.TotalTime;
                if (now < nextAllowedTime) return;
                nextAllowedTime = now + cooldown;
            }

            if (statusEffects != null)
            {
                ApplyStatusEffects(hitLimb, victim, targetCharacter, attacker, hasFrame ? frame.WorldPosition : (Vector2?)null);
                return;
            }

            AfflictionPrefab prefab = GetAfflictionPrefab();
            if (prefab == null) return;

            float amount = strength;
            if (!multiplyStrengthBySkill.IsEmpty)
            {
                amount *= Character.GetSkillLevel(multiplyStrengthBySkill);
            }

            ApplyOne(prefab.Instantiate(amount), hitLimb, targetCharacter, attacker, attackData);
        }

        private void ApplyStatusEffects(Limb hitLimb, Character victim, Character targetCharacter,
                                        Character attacker, Vector2? worldPosition)
        {
            Character effectUser = attacker ?? Character;

            foreach (StatusEffect statusEffect in statusEffects)
            {
                if (statusEffect == null) continue;
                statusEffect.SetUser(effectUser);

                if (statusEffect.HasTargetType(StatusEffect.TargetType.Limb) ||
                    statusEffect.HasTargetType(StatusEffect.TargetType.LastLimb))
                {
                    if (hitLimb != null) statusEffect.Apply(ActionType.OnAbility, 1f, effectUser, hitLimb);
                    continue;
                }

                if (statusEffect.HasTargetType(StatusEffect.TargetType.AllLimbs))
                {
                    List<ISerializableEntity> limbs = CollectLimbs(targetCharacter);
                    if (limbs.Count > 0) statusEffect.Apply(ActionType.OnAbility, 1f, effectUser, limbs);
                    continue;
                }

                if (statusEffect.HasTargetType(StatusEffect.TargetType.NearbyCharacters))
                {
                    List<ISerializableEntity> nearby = new List<ISerializableEntity>();
                    statusEffect.AddNearbyTargets(targetCharacter.WorldPosition, nearby);
                    statusEffect.Apply(ActionType.OnAbility, 1f, effectUser, nearby);
                    continue;
                }

                if (statusEffect.HasTargetType(StatusEffect.TargetType.This))
                {
                    statusEffect.Apply(ActionType.OnAbility, 1f, effectUser, Character);
                    continue;
                }

                statusEffect.Apply(ActionType.OnAbility, 1f, effectUser, targetCharacter);
            }
        }

        private static List<ISerializableEntity> CollectLimbs(Character character)
        {
            List<ISerializableEntity> result = new List<ISerializableEntity>();
            if (character?.AnimController == null) return result;
            foreach (Limb limb in character.AnimController.Limbs)
            {
                if (limb != null && !limb.Removed && !limb.IsSevered) result.Add(limb);
            }
            return result;
        }

        private static Limb FindNearestLimb(Character character, Vector2 worldPosition)
        {
            if (character?.AnimController == null) return null;
            Limb best = null;
            float bestDistanceSq = float.MaxValue;
            foreach (Limb limb in character.AnimController.Limbs)
            {
                if (limb == null || limb.Removed || limb.IsSevered) continue;
                float distanceSq = Vector2.DistanceSquared(limb.WorldPosition, worldPosition);
                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    best = limb;
                }
            }
            return best;
        }

        // 复用同一个 List，省掉每次命中的分配；嵌套调用时退化为新分配。
        [ThreadStatic] private static List<Affliction> singleAfflictionBuffer;
        [ThreadStatic] private static bool singleAfflictionInUse;

        private void ApplyOne(Affliction affliction, Limb hitLimb, Character targetCharacter,
                              Character attacker, AbilityAttackData attackData)
        {
            if (targetCharacter == null || targetCharacter.Removed) return;

            bool allowStacking = !setValue;
            float damageMultiplier = ResolveDamageMultiplier(attacker, targetCharacter, attackData);

            if (hitLimb != null && !hitLimb.IsSevered)
            {
                if (armorMode == ArmorMode.Ignore)
                {
                    ApplyIgnoringArmor(affliction, hitLimb, targetCharacter, attacker, allowStacking);
                    return;
                }

                bool borrowed = !singleAfflictionInUse;
                List<Affliction> payload;
                if (borrowed)
                {
                    singleAfflictionInUse = true;
                    payload = singleAfflictionBuffer ??= new List<Affliction>(1);
                    payload.Clear();
                    payload.Add(affliction);
                }
                else
                {
                    payload = new List<Affliction>(1) { affliction };
                }

                try
                {
                    AttackResult result = targetCharacter.DamageLimb(
                        hitLimb.WorldPosition, hitLimb, payload,
                        stun: 0f, playSound: false, attackImpulse: Vector2.Zero,
                        attacker: attacker, damageMultiplier: damageMultiplier,
                        allowStacking: allowStacking);

                    // 不产生伤害的附加物（冻结、眩晕那类）不会自己进 lastAttackers，这里补一次，
                    // 否则 NPC 不会把玩家当成威胁。
                    if (creditAttacker && attacker != null && attacker != targetCharacter && result.Damage <= 0f)
                    {
                        targetCharacter.AddAttacker(attacker, Math.Max(affliction.Strength, 0.01f));
                    }
                }
                finally
                {
                    if (borrowed) singleAfflictionInUse = false;
                }
                return;
            }

            if (fallbackMode == FallbackMode.None && targetKind == TargetKind.HitLimb) return;

            Limb applyLimb = targetKind == TargetKind.Attacker
                ? targetCharacter.AnimController?.MainLimb
                : null;
            targetCharacter.CharacterHealth.ApplyAffliction(applyLimb, affliction, allowStacking);
        }

        // 绕开护甲的版本：直接 ApplyAffliction。仍然复刻了 Limb.AddDamage 里那一步
        // OnAddDamageAffliction，所以增伤类天赋照常生效。
        private void ApplyIgnoringArmor(Affliction affliction, Limb hitLimb, Character targetCharacter,
                                        Character attacker, bool allowStacking)
        {
            Affliction applied = affliction;
            if (attacker != null)
            {
                AbilityAfflictionCharacter abilityAffliction = new AbilityAfflictionCharacter(applied, targetCharacter);
                attacker.CheckTalents(AbilityEffectType.OnAddDamageAffliction, abilityAffliction);
                applied = abilityAffliction.Affliction;
            }

            targetCharacter.CharacterHealth.ApplyAffliction(hitLimb, applied, allowStacking);

            if (creditAttacker && attacker != null && attacker != targetCharacter)
            {
                targetCharacter.AddAttacker(attacker, Math.Max(applied.Strength, 0.01f));
            }
        }

        // 武器命中的总倍率是 Attack.DamageMultiplier × AbilityAttackData.DamageMultiplier，
        // 而 AbilityAttackData 要到天赋跑完才补上 (1 + AttackMultiplier) 和同队的
        // (1 + TeamAttackMultiplier)，所以 stat / inherit 这两档得自己把面板那部分乘上。
        private float ResolveDamageMultiplier(Character attacker, Character target, AbilityAttackData attackData)
        {
            if (multiplierMode == MultiplierMode.None || attacker == null) return 1f;

            float value = 1f + attacker.GetStatValue(StatTypes.AttackMultiplier);
            if (attacker.TeamID == target.TeamID)
            {
                value *= 1f + attacker.GetStatValue(StatTypes.TeamAttackMultiplier);
            }

            if (multiplierMode == MultiplierMode.Inherit && attackData != null)
            {
                value *= attackData.DamageMultiplier;
                Attack sourceAttack = attackData.SourceAttack;
                if (sourceAttack != null) value *= sourceAttack.DamageMultiplier;
            }

            return value;
        }
    }
}
