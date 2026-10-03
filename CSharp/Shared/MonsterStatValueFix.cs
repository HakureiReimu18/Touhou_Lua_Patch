using Barotrauma;
using HarmonyLib;

namespace Touhou.Affixes
{
    /// <summary>
    /// 引擎的 Character.GetStatValue 对 Info 为 null 的角色（=所有怪物）第一行就 return 0，
    /// affliction 的 &lt;StatValue&gt; 对怪物因此全不生效。其实数据一直都在（CharacterHealth
    /// 汇总 aff 时根本不看 Info），只是被提前 return 掉了，这里补回去。
    /// 人类走原逻辑不动；无 Info 时引擎恒 0，加算就等于替换。
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.GetStatValue))]
    public static class MonsterStatValuePatch
    {
        static void Postfix(Character __instance, StatTypes statType, ref float __result)
        {
            if (__instance == null || __instance.Info != null) return; // 人类走引擎原逻辑
            var health = __instance.CharacterHealth;
            if (health == null) return;
            // 纯读取加算、无状态，两端各算各的，不用过 IsGameplayAuthority
            __result += health.GetStatValue(statType);
        }
    }
}
