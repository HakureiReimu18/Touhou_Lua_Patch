using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Abilities;
using HarmonyLib;

namespace Touhou.SkillCap
{
    /*
      天赋「技能上限」：把技能水平上限从 100 提到本角色配置的值（默认 150）。

      取代原版 AbilityFlags.GainSkillPastMaximum 的"开/关"做法：原版开关一开就放到全局上限
      （SkillSettings.Current.MaximumSkillWithTalents，默认 200），既不能配数值，靠它突破上限后
      换掉天赋、超出的技能还会留在身上。

      本能力的做法：
        · 能力一构造就把上限登记进 SkillCaps（不等 VerifyState 第一次触发，免得漏登记）；
        · 只在 CharacterInfo.IncreaseSkillLevel 这一次调用期间**临时**打开原版开关，调用结束后
          按进来时的状态还回去 —— 所以别家（或原版天赋）挂的同一个 flag 永远不会被我们摘掉，
          也不会被我们替他们打开；
        · 调用返回后把结果夹到该角色的上限；失效时夹回 100 并注销。

      **不要去补丁 Character.HasAbilityFlag** —— 它只是 `(flags & flag) != 0`，调用点会被 JIT
      内联，Harmony 拦不到，表现为"补丁打了却完全不生效"。

      <CharacterAbilitySkillCap maxskill="150"/>
        maxskill   技能上限，默认 150（低于 100 会被抬到 100）
    */

    internal sealed class CharacterAbilitySkillCap : CharacterAbility
    {
        private readonly float maxSkill;

        public CharacterAbilitySkillCap(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement)
            : base(characterAbilityGroup, abilityElement)
        {
            maxSkill = Math.Max(100f, abilityElement.GetAttributeFloat(150f, "maxskill"));
            SkillCaps.Set(Character, maxSkill);
        }

        public override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            Character character = Character;
            if (character == null) return;

            if (conditionsMatched)
            {
                SkillCaps.Set(character, maxSkill);
                SkillCaps.Clamp(character, maxSkill);
            }
            else
            {
                // 先夹回 100 再注销，否则注销之后就没有上限可用了
                SkillCaps.Clamp(character, 100f);
                SkillCaps.Remove(character);
            }
        }
    }

    internal static class SkillCaps
    {
        // 角色 → 本模组给该角色设的技能上限。没登记的角色一律不干预
        private static readonly Dictionary<Character, float> caps = new Dictionary<Character, float>();

        public static void Set(Character character, float cap)
        {
            if (character != null) caps[character] = cap;
        }

        public static void Remove(Character character)
        {
            if (character != null) caps.Remove(character);
        }

        public static bool TryGet(Character character, out float cap)
        {
            cap = 0f;
            return character != null && caps.TryGetValue(character, out cap);
        }

        public static void Clamp(Character character, float cap)
        {
            if (character == null || character.Info == null || character.Info.Job == null) return;

            foreach (Skill skill in character.Info.Job.GetSkills())
            {
                if (skill != null && skill.Level > cap) skill.Level = cap;
            }
        }

        // 只管登记过的角色；别的模组给自己角色开的 200 上限不归我们管
        public static void ClampIfRegistered(Character character)
        {
            if (TryGet(character, out float cap))
            {
                Clamp(character, cap);
            }
        }

        public static bool WantsPastMaximum(Character character)
        {
            return TryGet(character, out float cap) && cap > 100f;
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名类型，这里截住本能力的元素名，换成自己的实现。
    [HarmonyPatch(typeof(CharacterAbility), nameof(CharacterAbility.Load))]
    internal static class SkillCapLoadPatch
    {
        private const string ElementName = "characterabilityskillcap";

        private static bool Prefix(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
                                   ref CharacterAbility __result)
        {
            if (!string.Equals(abilityElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new CharacterAbilitySkillCap(characterAbilityGroup, abilityElement);
            return false;
        }
    }

    // 涨技能这一次调用期间临时打开原版上限开关，结束后按进来时的状态还回去，再把结果夹到上限。
    // 为什么不常驻挂 flag：abilities 的 flag 是 bitfield，我们摘掉就等于把别家挂的一起摘了。
    // __state 记录"进来时本来有没有这个 flag"，为 true 说明是别人的，我们绝不动它。
    [HarmonyPatch(typeof(CharacterInfo), nameof(CharacterInfo.IncreaseSkillLevel))]
    internal static class SkillCapClampPatch
    {
        private static void Prefix(CharacterInfo __instance, ref bool __state)
        {
            __state = false;

            Character character = __instance?.Character;
            if (!SkillCaps.WantsPastMaximum(character)) return;

            __state = character.HasAbilityFlag(AbilityFlags.GainSkillPastMaximum);
            if (!__state) character.AddAbilityFlag(AbilityFlags.GainSkillPastMaximum);
        }

        private static void Postfix(CharacterInfo __instance, bool __state)
        {
            Character character = __instance?.Character;
            if (character == null) return;

            if (!__state && SkillCaps.WantsPastMaximum(character))
            {
                character.RemoveAbilityFlag(AbilityFlags.GainSkillPastMaximum);
            }

            SkillCaps.ClampIfRegistered(character);
        }
    }

    // 补丁要插件自己注册（本模组惯例），光写 [HarmonyPatch] 特性不会生效
    public sealed class SkillCapPlugin : Barotrauma.LuaCs.IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.skillcap");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(SkillCapLoadPatch));
            harmony.PatchAll(typeof(SkillCapClampPatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
        }
    }
}
