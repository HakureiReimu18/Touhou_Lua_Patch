using System;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.InstantDeathGuard
{
    /*
      天赋「免秒杀」：单次命中/爆炸不能把角色从 >0 血直接打死，至少留下 minvitality 点生命。

      做法是「引擎自己当保险 + 事后补血」：
        1. CharacterAbilityPreventInstantDeath 挂在 AbilityGroupInterval 上，条件匹配时把角色登记为「有守卫」；
        2. ApplyDamage 的 prefix —— 守卫就绪时，在这次命中窗口内给角色挂上引擎自带的
           AbilityFlags.CanNotDieToAfflictions。于是 CharacterHealth.KillIfOutOfVitality 里那句
           `!HasAbilityFlag(CanNotDieToAfflictions)` 会挡下致死判定，**当场绝不会误杀**；
        3. ApplyDamage 的 finalizer —— 如果这次命中后生命值已经掉到下限之下（说明引擎确实挡下了一次致死），
           就在 flag 仍然挂着的时候把致死的 affliction 削到留 minvitality 点，然后摘掉 flag 并进入冷却。

      三个关键点：
        - flag 只存在于「命中窗口」内。流血/烧伤这类持续伤害走 CharacterHealth.Update 的每秒结算、
          不经过 ApplyDamage，所以它们永远看不到这个 flag —— 「单次秒杀免疫」和「DoT 仍可致死」自然分开。
        - 补血发生在 flag 还在的时候。削减 affliction 内部会再跑一遍致死判定，
          此时 flag 仍然覆盖，不会出现「救援途中被补刀」的竞态（这是纯自实现版本会踩的坑）。
        - 只有本来没挂这个 flag 时才由我们接管开关，避免清掉别的系统（妹红的复活等）挂的同一个 flag。

      写法：
          <CharacterAbilityPreventInstantDeath cooldown="60" minvitality="1"/>
    */

    internal static class GuardStates
    {
        internal sealed class State
        {
            public bool Active;
            public float MinVitality = 1f;
            public float Cooldown = 60f;
            public double NextAllowedTime;
            public bool FlagAdded;
        }

        // 挂在角色身上，随角色回收，不用手写清理
        private static readonly ConditionalWeakTable<Character, State> states = new ConditionalWeakTable<Character, State>();

        // 场上没有本能力的实例时，补丁一个操作都不做
        private static int liveInstanceCount;

        public static bool Enabled => liveInstanceCount > 0;

        public static void RegisterInstance() => liveInstanceCount++;

        public static void ResetInstanceCount() => liveInstanceCount = 0;

        public static State Get(Character character) => states.GetOrCreateValue(character);

        public static void Clear() => states.Clear();

        // 只在命中窗口内跨 prefix/finalizer 传状态，ApplyDamage 是同步单线程执行的
        [ThreadStatic] private static State activeState;

        public static void BeginHit(CharacterHealth health)
        {
            if (health == null) return;

            Character character = health.Character;
            if (character == null || character.Removed || character.IsDead) return;

            if (!states.TryGetValue(character, out State state) || !state.Active) return;
            if (health.Vitality <= state.MinVitality) return;               // 之前就贴地了 → 不算秒杀
            if (Timing.TotalTime < state.NextAllowedTime) return;           // 冷却中
            if (character.HasAbilityFlag(AbilityFlags.CanNotDieToAfflictions)) return;   // 别的系统已经挂着了，别插手

            character.AddAbilityFlag(AbilityFlags.CanNotDieToAfflictions);
            state.FlagAdded = true;
            activeState = state;
        }

        public static void EndHit(CharacterHealth health)
        {
            State state = activeState;
            activeState = null;
            if (state == null) return;

            state.FlagAdded = false;

            Character character = health?.Character;
            if (character == null) return;

            if (health.Vitality <= state.MinVitality && Rescue(health, state))
            {
                state.NextAllowedTime = Timing.TotalTime + state.Cooldown;
            }

            if (!character.Removed)
            {
                character.RemoveAbilityFlag(AbilityFlags.CanNotDieToAfflictions);
            }
        }

        // 把致死的 affliction 削到留 minvitality 点。削减内部会重跑致死判定，
        // 但此时 flag 还挂着，引擎会替我们挡住，所以这里是安全的。
        private static bool Rescue(CharacterHealth health, State state)
        {
            for (int i = 0; i < 8 && health.Vitality < state.MinVitality; i++)
            {
                Affliction lethal = health.GetCauseOfDeath().affliction;
                if (lethal == null || lethal.Prefab == null) break;
                health.ReduceAfflictionOnAllLimbs(lethal.Prefab.Identifier, state.MinVitality - health.Vitality);
            }

            return health.Vitality >= state.MinVitality;
        }
    }

    internal sealed class CharacterAbilityPreventInstantDeath : CharacterAbility
    {
        private readonly GuardStates.State state;

        public CharacterAbilityPreventInstantDeath(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement)
            : base(characterAbilityGroup, abilityElement)
        {
            state = GuardStates.Get(Character);
            state.Cooldown = Math.Max(0f, abilityElement.GetAttributeFloat(60f, "cooldown"));
            state.MinVitality = Math.Max(1f, abilityElement.GetAttributeFloat(1f, "minvitality"));

            GuardStates.RegisterInstance();
        }

        // 挂在 AbilityGroupInterval 上：每个间隔结算时同步一次「条件是否匹配」
        public override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            state.Active = conditionsMatched;
        }
    }

    public sealed class InstantDeathGuardPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool oneTimeInitDone;

        public void Initialize()
        {
            harmony = new Harmony("touhou.instantdeathguard");
        }

        public void OnLoadCompleted()
        {
            if (oneTimeInitDone) return;
            oneTimeInitDone = true;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(PreventInstantDeathLoadPatch));
            harmony.PatchAll(typeof(ApplyDamagePatch));
        }

        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            oneTimeInitDone = false;
            GuardStates.Clear();
            GuardStates.ResetInstanceCount();
        }
    }

    // 引擎按元素名去 Barotrauma 程序集里找同名类型，这里截住本能力的元素名，换成自己的实现。
    // 类名必须全局唯一：LuaCs 按类名登记补丁，重名的只会有一个生效。
    [HarmonyPatch(typeof(CharacterAbility), nameof(CharacterAbility.Load))]
    internal static class PreventInstantDeathLoadPatch
    {
        private const string ElementName = "characterabilitypreventinstantdeath";

        private static bool Prefix(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
                                   ref CharacterAbility __result)
        {
            if (!string.Equals(abilityElement.Name.ToString(), ElementName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            __result = new CharacterAbilityPreventInstantDeath(characterAbilityGroup, abilityElement);
            return false;
        }
    }

    // 命中/爆炸期间挂上引擎自带的免死 flag，命中结算完再按需要补血并摘掉
    [HarmonyPatch(typeof(CharacterHealth), nameof(CharacterHealth.ApplyDamage))]
    internal static class ApplyDamagePatch
    {
        private static void Prefix(CharacterHealth __instance)
        {
            if (!GuardStates.Enabled) return;
            GuardStates.BeginHit(__instance);
        }

        private static void Finalizer(CharacterHealth __instance)
        {
            if (!GuardStates.Enabled) return;
            GuardStates.EndHit(__instance);
        }
    }
}
