using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.AttackerCondition
{
    /*
      天赋条件「检测攻击者」：条件主体是本次攻击的发动者，而不是天赋持有者自己。

      为什么需要它：写
          <AbilityConditionCharacter targettype="Enemy">
              <Conditional psychosis="gt 5"/>
          </AbilityConditionCharacter>
      时，条件主体取自 AbilityAttackData.Character —— 而那是**受害者**（见 AbilityAttackData 的构造函数：
      Character = target，Attacker = attacker，两边检查收到的是同一个对象）。于是这条在 OnAttacked 组里
      永远是"拿自己判自己"，targettype="Enemy" 恒为 false，整组从不触发。
      攻击者只能用 AbilityAttackData.Attacker 取到，而原版没有任何条件读它。

      用法：把原来的 <AbilityConditionCharacter> 换成 <AbilityConditionAttacker>，其余照写。
      targettype / targettypes 与 <Conditional> 的语义和原版完全一致（Conditional 走原版 PropertyConditional，
      即"把 affliction 名当属性名"的那种写法）：

          <AbilityConditionAttacker targettype="Enemy">
              <Conditional Touhou_Misfortune_Curse="gt 0"/>
          </AbilityConditionAttacker>

      只在带攻击数据的效果组里有效（OnAttack / OnAttacked / OnAttackResult / OnAttackedResult /
      OnKillCharacter 等）；写在纯状态组（AbilityGroupInterval / None）里没有攻击者可取，
      会按原版规则报错一次并返回 false。

      invert="true" 由 AbilityConditionData 统一处理，这里只管正向判定。
    */

    internal sealed class AbilityConditionAttacker : AbilityConditionData
    {
        private readonly List<TargetType> targetTypes;
        private readonly List<PropertyConditional> conditionals = new List<PropertyConditional>();

        public AbilityConditionAttacker(CharacterTalent characterTalent, ContentXElement conditionElement)
            : base(characterTalent, conditionElement)
        {
            targetTypes = ParseTargetTypes(conditionElement.GetAttributeStringArray("targettypes",
                conditionElement.GetAttributeStringArray("targettype", Array.Empty<string>())));

            foreach (ContentXElement element in conditionElement.Elements())
            {
                if (string.Equals(element.Name.ToString(), "conditional", StringComparison.OrdinalIgnoreCase))
                {
                    conditionals.AddRange(PropertyConditional.FromXElement(element));
                }
            }
        }

        public override bool MatchesConditionSpecific(AbilityObject abilityObject)
        {
            Character attacker = (abilityObject as AbilityAttackData)?.Attacker;
            if (attacker == null || attacker.Removed)
            {
                return false;
            }

            if (!IsViableTarget(targetTypes, attacker))
            {
                return false;
            }

            foreach (PropertyConditional conditional in conditionals)
            {
                if (!conditional.Matches(attacker))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名条件类型，这里截住本条件的元素名，换成自己的实现。
    public sealed class AttackerConditionPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.attackercondition");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(ConstructConditionPatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
        }
    }

    [HarmonyPatch(typeof(CharacterAbilityGroup), "ConstructCondition")]
    internal static class ConstructConditionPatch
    {
        private const string ElementName = "abilityconditionattacker";

        private static bool Prefix(CharacterTalent characterTalent, ContentXElement conditionElement, bool errorMessages,
                                   ref AbilityCondition __result)
        {
            if (!string.Equals(conditionElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new AbilityConditionAttacker(characterTalent, conditionElement);
            return false;
        }
    }
}
