using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.SkillBonus
{
    // 原版没有魔法技能的平点 StatType；走查询加成，不改基础技能，也就不能洗点刷技能。
    internal sealed class CharacterAbilityGiveSkillBonus : CharacterAbility
    {
        internal readonly Identifier Skill;
        internal readonly float Value;
        public override bool AllowClientSimulation => true;

        public CharacterAbilityGiveSkillBonus(CharacterAbilityGroup group, ContentXElement element) : base(group, element)
        {
            Skill = element.GetAttributeIdentifier("skillidentifier", Identifier.Empty);
            Value = element.GetAttributeFloat("value", 0f);
        }

        public override void InitializeAbility(bool addingFirstTime)
        {
            if (Character == null || Skill.IsEmpty || !float.IsFinite(Value)) return;
            var list = SkillBonusHooks.Bonuses.GetOrCreateValue(Character);
            if (!list.Contains(this)) list.Add(this);
        }
    }

    internal static class SkillBonusHooks
    {
        internal static ConditionalWeakTable<Character, List<CharacterAbilityGiveSkillBonus>> Bonuses = new();
#if SERVER
        internal const string Side = "server";
        internal static bool OwnContext => GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
        internal const string Side = "client";
        internal static bool OwnContext => GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Load(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
            ref CharacterAbility __result)
        {
            if (!OwnContext || !abilityElement.Name.LocalName.Equals("CharacterAbilityGiveSkillBonus", StringComparison.OrdinalIgnoreCase))
                return true;
            __result = new CharacterAbilityGiveSkillBonus(characterAbilityGroup, abilityElement);
            return false;
        }

        internal static void SkillLevel(Character __instance, Identifier skillIdentifier, ref float __result)
        {
            if (!OwnContext || __instance == null || __instance.Removed || !Bonuses.TryGetValue(__instance, out var list)) return;
            // 转职/洗点摘掉实例后，本能力的贡献自动消失；不碰其他技能修正。
            list.RemoveAll(ability => !__instance.CharacterTalents.Contains(ability.CharacterTalent));
            float bonus = list.Where(ability => ability.Skill == skillIdentifier).Sum(ability => ability.Value);
            if (float.IsFinite(__result) && float.IsFinite(bonus))
                __result = Math.Max(0f, __result + bonus);
        }
    }

    public sealed class SkillBonusPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        public void Initialize() => harmony = new Harmony("touhou.skillbonus." + SkillBonusHooks.Side);
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            harmony.UnpatchSelf();
            var load = AccessTools.Method(typeof(CharacterAbility), "Load");
            var skill = AccessTools.Method(typeof(Character), "GetSkillLevel", new[] { typeof(Identifier) });
            if (load == null || skill == null)
            {
                DebugConsole.ThrowError("[SkillBonus] 技能加成接口缺失，未挂补丁。");
                return;
            }
            try
            {
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(SkillBonusHooks), nameof(SkillBonusHooks.Load)));
                harmony.Patch(skill, postfix: new HarmonyMethod(typeof(SkillBonusHooks), nameof(SkillBonusHooks.SkillLevel)));
            }
            catch (Exception exception)
            {
                harmony.UnpatchSelf();
                DebugConsole.ThrowError("[SkillBonus] 技能加成未挂上：" + exception.Message);
            }
        }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            SkillBonusHooks.Bonuses = new();
        }
    }
}
