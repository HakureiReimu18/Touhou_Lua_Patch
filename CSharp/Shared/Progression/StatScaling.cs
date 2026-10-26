using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.StatScaling
{
    // 多个源属性取平均，再换算目标加成；各属性都用引擎的 0.1 = 10% 口径。
    internal sealed class CharacterAbilityModifyStatToStat : CharacterAbility
    {
        private readonly StatTypes[] sourceStats;
        private readonly float multiplier;
        private readonly float maximum;
        private readonly bool valid;
        internal StatTypes StatType { get; }
        internal float LastValue { get; private set; }
        public override bool AllowClientSimulation => true;

        public CharacterAbilityModifyStatToStat(CharacterAbilityGroup group, ContentXElement element) : base(group, element)
        {
            bool parsed = Enum.TryParse(element.GetAttributeString("stattype", ""), true, out StatTypes destination) &&
                destination != StatTypes.None && Enum.IsDefined(typeof(StatTypes), destination);
            StatType = destination;
            var sources = new List<StatTypes>();
            foreach (string name in element.GetAttributeString("sourcestats", "").Split(','))
            {
                if (!Enum.TryParse(name.Trim(), true, out StatTypes source) || source == StatTypes.None ||
                    !Enum.IsDefined(typeof(StatTypes), source) || source == destination || sources.Contains(source))
                    parsed = false;
                else sources.Add(source);
            }
            sourceStats = sources.ToArray();
            multiplier = element.GetAttributeFloat("multiplier", 1f);
            maximum = element.GetAttributeFloat("maxvalue", 0.15f);
            valid = parsed && sourceStats.Length > 0 && float.IsFinite(multiplier) && multiplier >= 0f &&
                float.IsFinite(maximum) && maximum >= 0f;
            if (!valid) DebugConsole.ThrowError("[StatScaling] 属性换算配置无效：需要有效且不重复的源属性，不能直接读取目标自身。");
        }

        public override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            if (!valid || !StatScalingHooks.OwnContext || Character == null || Character.Removed) return;
            float next = 0f;
            if (valid && conditionsMatched)
            {
                double sum = 0;
                bool finite = true;
                foreach (StatTypes source in sourceStats)
                {
                    float value = Character.GetStatValue(source);
                    if (!float.IsFinite(value)) { finite = false; break; }
                    sum += value;
                }
                if (finite) next = (float)Math.Clamp(sum / sourceStats.Length * multiplier, 0d, maximum);
            }
            // 只换掉自己的上一笔贡献，不能每个周期再加一遍，也不能覆盖装备/其他天赋。
            Character.ChangeStat(StatType, next - LastValue);
            LastValue = next;
        }
    }

    // 生命越低，属性越接近 maxvalue；负数可用于低血量时降低属性。
    internal sealed class CharacterAbilityModifyStatToVitality : CharacterAbility
    {
        private readonly float maxValue;
        private readonly bool valid;
        internal StatTypes StatType { get; }
        internal float LastValue { get; private set; }
        public override bool AllowClientSimulation => true;

        public CharacterAbilityModifyStatToVitality(CharacterAbilityGroup group, ContentXElement element) : base(group, element)
        {
            bool parsed = Enum.TryParse(element.GetAttributeString("stattype", ""), true, out StatTypes stat) &&
                stat != StatTypes.None && Enum.IsDefined(typeof(StatTypes), stat);
            StatType = stat;
            maxValue = element.GetAttributeFloat("maxvalue", 0f);
            valid = parsed && float.IsFinite(maxValue);
            if (!valid) DebugConsole.ThrowError("[StatScaling] 生命属性换算配置无效：需要有效的 stattype 和有限 maxvalue。");
        }

        public override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            if (!valid || !StatScalingHooks.OwnContext || Character == null || Character.Removed) return;
            float next = 0f;
            if (conditionsMatched && !Character.IsDead)
            {
                float current = Character.Vitality, maximum = Character.MaxVitality;
                if (float.IsFinite(current) && float.IsFinite(maximum) && maximum > 0f)
                {
                    double missing = 1d - Math.Clamp((double)current / maximum, 0d, 1d);
                    next = (float)(missing * maxValue);
                }
            }
            // 只替换本能力的贡献，满血、条件失效时不碰别人的同类属性。
            Character.ChangeStat(StatType, next - LastValue);
            LastValue = next;
        }
    }

    internal static class StatScalingHooks
    {
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
            if (!OwnContext) return true;
            switch (abilityElement.Name.LocalName.ToLowerInvariant())
            {
                case "characterabilitymodifystattostat":
                    __result = new CharacterAbilityModifyStatToStat(characterAbilityGroup, abilityElement);
                    return false;
                case "characterabilitymodifystattovitality":
                    __result = new CharacterAbilityModifyStatToVitality(characterAbilityGroup, abilityElement);
                    return false;
                default: return true;
            }
        }
    }

    public sealed class StatScalingPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool initialized;
        public void Initialize() => harmony = new Harmony("touhou.statscaling." + StatScalingHooks.Side);
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            if (initialized) return;
            harmony.UnpatchSelf();
            // 不打特性标签，免得词缀插件扫程序集时又挂一遍。
            var load = AccessTools.Method(typeof(CharacterAbility), "Load");
            if (load == null) { DebugConsole.ThrowError("[StatScaling] 找不到天赋加载接口。"); return; }
            try
            {
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(StatScalingHooks), nameof(StatScalingHooks.Load)));
                initialized = true;
            }
            catch (Exception exception) { DebugConsole.ThrowError("[StatScaling] 加载接口未挂上：" + exception.Message); }
        }
        public void Dispose() { harmony?.UnpatchSelf(); initialized = false; }
    }
}
