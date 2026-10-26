using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Abilities;
using HarmonyLib;

namespace Touhou.ConvertAffliction
{
    /*
      天赋「受损类型转化」：把身上某种受损类 affliction **逐肢体、1:1** 转成另一种，
      用来取代原版 CharacterAbilityReplaceAffliction。

      原版那只（1.13.4 反汇编核实）有三个毛病：
        · GetAffliction(id) 全身只返回第一条，拿这一条的强度对全身 ReduceAfflictionOnAllLimbs，
          再把新 aff 全塞回那一条所在的肢体 —— 全身几处伤会并到一根肢体上；
        · 新 aff 走 ApplyAffliction，会乘 100/MaxVitality 与（1 - 抗性），强度白掉一截；
        · 源是全身性 aff（drunk 那类）时，目标若按肢体又会"每根肢体各加一份"，按份数扣血。

      本能力的做法：
        · 逐健康槽（HealthIndex）转换，同槽的多根肢体只算一次 —— 哪条肢体受的伤转哪条，不跨肢体合并；
        · 目标不存在时先 ApplyAffliction、再写回精确强度，绕开缩放与抗性；
        · 目标装不下（到 MaxStrength）时只搬装得下的量，剩下的留在源上，不凭空吞伤害；
        · 源是全身性 aff 时整条转一次：目标也全身就全身对全身，目标按肢体就落到主肢体一份；
        · 抗性可选：resistance="apply" 时落地量再乘（1 - 该肢体的抗性）—— 被削掉的那截就是转化损耗；默认 ignore 不缩水。

      写法（属性名与原版一致，改个元素名就能换过来）：
          <CharacterAbilityConvertAffliction affliction="blunttrauma" newaffliction="lacerations"/>
          <CharacterAbilityConvertAffliction affliction="gunshotwound" newaffliction="bitewounds" strengthmultiplier="1.2"/>
          <CharacterAbilityConvertAffliction affliction="burn" newaffliction="bitewounds" resistance="apply"/>

          affliction / afflictionid       要转走的 affliction
          newaffliction / newafflictionid 转成的 affliction
          strengthmultiplier              强度倍率，默认 1
          resistance                      转化后的 aff 吃不吃抗性：ignore（默认，1:1）/ apply（按落地肢体的抗性削一道）

      注意：挂在 AbilityGroupInterval 上时**不乘间隔秒数**（跟 StatusEffect 的 amount 不一样），
      每次触发把当时存在的量全转掉；转过的强度不会被转第二次（源已清零）。
      间隔组走 VerifyState，事件组（AbilityGroupEffect）走 ApplyEffect，两种挂法都支持。
    */

    internal sealed class CharacterAbilityConvertAffliction : CharacterAbility
    {
        private const string DebugName = "CharacterAbilityConvertAffliction";

        private enum ResistanceMode { Ignore, Apply }

        private readonly Identifier afflictionId;
        private readonly Identifier newAfflictionId;
        private readonly float strengthMultiplier;
        private readonly ResistanceMode resistanceMode;

        // 场上有本能力的实例时才需要遍历，实例里复用一张表省掉每次触发的新分配
        private readonly HashSet<int> visitedLimbSlots = new HashSet<int>();

        // 目标 prefab 只在首次触发时查一次，之后缓存
        private AfflictionPrefab cachedTargetPrefab;
        private bool targetPrefabErrorLogged;

        public CharacterAbilityConvertAffliction(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement)
            : base(characterAbilityGroup, abilityElement)
        {
            afflictionId = abilityElement.GetAttributeIdentifier("afflictionid",
                abilityElement.GetAttributeIdentifier("affliction", Identifier.Empty));
            newAfflictionId = abilityElement.GetAttributeIdentifier("newafflictionid",
                abilityElement.GetAttributeIdentifier("newaffliction", Identifier.Empty));
            strengthMultiplier = Math.Max(0f, abilityElement.GetAttributeFloat("strengthmultiplier", 1f));
            resistanceMode = ParseResistanceMode(abilityElement);

            if (afflictionId.IsEmpty || newAfflictionId.IsEmpty)
            {
                DebugConsole.ThrowError($"Error in talent {CharacterTalent.DebugIdentifier} - {DebugName} needs both " +
                                        "an affliction and a newaffliction attribute.", null, abilityElement.ContentPackage);
            }
            else if (afflictionId == newAfflictionId)
            {
                DebugConsole.ThrowError($"Error in talent {CharacterTalent.DebugIdentifier} - {DebugName} has the same " +
                                        $"identifier \"{afflictionId}\" for affliction and newaffliction.", null, abilityElement.ContentPackage);
            }
        }

        private ResistanceMode ParseResistanceMode(ContentXElement element)
        {
            string raw = element.GetAttributeString("resistance", "ignore");
            if (raw.Equals("ignore", StringComparison.OrdinalIgnoreCase)) return ResistanceMode.Ignore;
            if (raw.Equals("apply", StringComparison.OrdinalIgnoreCase)) return ResistanceMode.Apply;

            DebugConsole.ThrowError($"Error in {DebugName} - unknown resistance \"{raw}\" in talent " +
                                    $"{CharacterTalent.DebugIdentifier}; expected \"apply\" or \"ignore\". " +
                                    "Falling back to \"ignore\".", null, element.ContentPackage);
            return ResistanceMode.Ignore;
        }

        // 挂在 AbilityGroupInterval 上：条件匹配时每个间隔结一次账（状态型能力，不是事件型）
        public override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            if (conditionsMatched)
            {
                Convert();
            }
        }

        // 挂在事件组（AbilityGroupEffect）上也能用：按事件触发时同样逐肢体转一次
        public override void ApplyEffect()
        {
            Convert();
        }

        public override void ApplyEffect(AbilityObject abilityObject)
        {
            Convert();
        }

        private void Convert()
        {
            Character character = Character;
            CharacterHealth health = character?.CharacterHealth;
            if (health == null || character.Removed) return;
            if (afflictionId.IsEmpty || newAfflictionId.IsEmpty || afflictionId == newAfflictionId) return;

            AfflictionPrefab targetPrefab = GetTargetPrefab();
            if (targetPrefab == null) return;

            bool changed = false;

            // 1) 挂在肢体上的源：逐健康槽转，同槽的多根肢体（大腿/小腿/脚）只处理一次
            Limb[] limbs = character.AnimController?.Limbs;
            if (limbs != null)
            {
                visitedLimbSlots.Clear();
                foreach (Limb limb in limbs)
                {
                    if (limb == null || limb.Removed) continue;
                    if (!visitedLimbSlots.Add(limb.HealthIndex)) continue;

                    Affliction source = health.GetAffliction(afflictionId, limb);
                    if (source == null) continue;
                    if (MoveStrength(health, limb, source, targetPrefab)) changed = true;
                }
            }

            // 2) 全身性的源：整条转一次；目标按肢体时落到主肢体一份（不往每根肢体复制）
            Affliction globalSource = health.GetAffliction(afflictionId, allowLimbAfflictions: false);
            if (globalSource != null)
            {
                Limb targetLimb = targetPrefab.LimbSpecific ? character.AnimController?.MainLimb : null;
                if (!targetPrefab.LimbSpecific || targetLimb != null)
                {
                    if (MoveStrength(health, targetLimb, globalSource, targetPrefab)) changed = true;
                }
            }

            if (changed)
            {
                health.RecalculateVitality();
                // 跟引擎施加 affliction 时一样：催一次同步，别等下一个状态更新周期
                character.HealthUpdateInterval = 0f;
            }
        }

        // 把 source 的强度按倍率搬到目标 affliction 上。limb 为 null 表示全身性的目标。
        private bool MoveStrength(CharacterHealth health, Limb limb, Affliction source, AfflictionPrefab targetPrefab)
        {
            float sourceStrength = source.Strength;
            if (sourceStrength <= 0f) return false;

            float amount = sourceStrength * strengthMultiplier;
            if (amount <= 0f) return false;

            Affliction target = limb != null
                ? health.GetAffliction(newAfflictionId, limb)
                : health.GetAffliction(newAfflictionId, allowLimbAfflictions: false);

            float current = target?.Strength ?? 0f;
            float capacity = targetPrefab.MaxStrength - current;
            if (capacity <= 0f) return false;   // 目标满了：源留着不动，等腾出空间再搬

            float moved = Math.Min(amount, capacity);
            float consumed = strengthMultiplier > 0f ? moved / strengthMultiplier : 0f;
            if (consumed > sourceStrength) consumed = sourceStrength;

            // 落地量：默认 1:1；resistance="apply" 时按目标肢体的抗性削一道（跟引擎施加同口径）
            float landing = moved;
            if (resistanceMode == ResistanceMode.Apply)
            {
                float resistance = health.GetResistance(targetPrefab, limb?.type ?? LimbType.None);
                landing = moved * (1f - resistance);
            }

            if (target != null)
            {
                target.Strength = current + landing;
            }
            else
            {
                // ApplyAffliction 会按 100/MaxVitality 缩放、再乘（1-抗性），施加后写回精确强度
                health.ApplyAffliction(limb, targetPrefab.Instantiate(moved));
                Affliction applied = limb != null
                    ? health.GetAffliction(newAfflictionId, limb)
                    : health.GetAffliction(newAfflictionId, allowLimbAfflictions: false);
                if (applied == null)
                {
                    // 引擎挡下了这次施加（GodMode / Unkillable / 免疫等）：源一点都不动，别白吞伤害
                    return false;
                }
                applied.Strength = landing;
            }

            source.Strength = sourceStrength - consumed;
            return true;
        }

        private AfflictionPrefab GetTargetPrefab()
        {
            if (cachedTargetPrefab != null) return cachedTargetPrefab;

            cachedTargetPrefab = AfflictionPrefab.Prefabs.Find(a => a.Identifier == newAfflictionId);
            if (cachedTargetPrefab == null && !targetPrefabErrorLogged)
            {
                targetPrefabErrorLogged = true;
                DebugConsole.ThrowError($"Error in {DebugName} - could not find an affliction with the identifier " +
                                        $"\"{newAfflictionId}\".", null, CharacterTalent.Prefab.ContentPackage);
            }
            return cachedTargetPrefab;
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名类型，这里截住本能力的元素名，换成自己的实现。
    // 类名必须全局唯一：LuaCs 按类名登记补丁，重名的只会有一个生效。
    [HarmonyPatch(typeof(CharacterAbility), nameof(CharacterAbility.Load))]
    internal static class ConvertAfflictionLoadPatch
    {
        private const string ElementName = "characterabilityconvertaffliction";

        private static bool Prefix(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
                                   ref CharacterAbility __result)
        {
            if (!string.Equals(abilityElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new CharacterAbilityConvertAffliction(characterAbilityGroup, abilityElement);
            return false;
        }
    }

    // 补丁要插件自己注册（本模组惯例），光写 [HarmonyPatch] 特性不会生效
    public sealed class ConvertAfflictionPlugin : Barotrauma.LuaCs.IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.convertaffliction");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ConvertAfflictionLoadPatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
        }
    }
}
