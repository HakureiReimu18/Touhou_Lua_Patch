using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Abilities;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.SecurityTalents
{
    public sealed class SecurityTalentsPlugin : IAssemblyPlugin
    {
        private Harmony harmony;
        private static bool initialized;
        public void Initialize() => harmony = new Harmony("touhou.securitytalents." + SecurityRules.Side);
        public void PreInitPatching() { }
        public void OnLoadCompleted()
        {
            if (initialized) return;
            initialized = true;
            harmony.UnpatchSelf();
            // 不加 HarmonyPatch 特性，免得词缀插件扫程序集时又挂一遍。
            Patch(AccessTools.Method(typeof(CharacterAbility), "Load"), nameof(SecurityHooks.Load), true);
            Patch(AccessTools.Method(typeof(Item), "Use"), nameof(SecurityHooks.UseBegin), true,
                nameof(SecurityHooks.UseEnd), nameof(SecurityHooks.UseFinally));
            Patch(AccessTools.Method(typeof(Projectile), "Shoot"), nameof(SecurityHooks.Shoot), true);
        }
        private void Patch(MethodBase target, string name, bool prefix, string postfix = null, string finalizer = null)
        {
            if (target == null)
            {
                DebugConsole.ThrowError("[SecurityTalents] 找不到接口：" + name);
                return;
            }
            try
            {
                harmony.Patch(target,
                    prefix: prefix ? new HarmonyMethod(typeof(SecurityHooks), name) : null,
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(SecurityHooks), postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SecurityHooks), finalizer));
            }
            catch (Exception exception)
            {
                DebugConsole.ThrowError("[SecurityTalents] 接口未挂上：" + name + " / " + exception.Message);
            }
        }
        public void Dispose()
        {
            harmony?.UnpatchSelf();
            SecurityHooks.Clear();
            initialized = false;
        }
    }

    internal static class SecurityRules
    {
#if SERVER
        internal const string Side = "server";
        internal static bool OwnContext => GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer;
#else
        internal const string Side = "client";
        internal static bool OwnContext => GameMain.NetworkMember == null || GameMain.NetworkMember.IsClient;
#endif
        internal static bool Authority => OwnContext && (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer);
        internal static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(value, max));
        internal static float Roll() => Rand.Range(0f, 1f, Rand.RandSync.Unsynced);
        internal static float Chance(ContentXElement element) => Clamp(element.GetAttributeFloat("chance", 0.3f), 0f, 1f);
    }

    internal sealed class CharacterAbilitySecurityMagazineSave : CharacterAbility
    {
        internal readonly float Chance;
        public CharacterAbilitySecurityMagazineSave(CharacterAbilityGroup group, ContentXElement element) : base(group, element)
        {
            Chance = SecurityRules.Chance(element);
        }
        public override void InitializeAbility(bool addingFirstTime)
        {
            if (SecurityRules.Authority) SecurityHooks.Register(this);
        }
    }

    internal static class SecurityHooks
    {
        private static ConditionalWeakTable<Character, List<CharacterAbilitySecurityMagazineSave>> savers = new();
        private static ConditionalWeakTable<ItemPrefab, MagazineFlag> magazineFlags = new();
        private sealed class MagazineFlag { internal bool Value; }
        [ThreadStatic] private static List<Shot> shots;
        internal sealed class Shot
        {
            internal Item Weapon;
            internal float Chance;
            internal readonly Dictionary<Item, float> Before = new();
            internal readonly HashSet<Item> Fired = new();
        }
        internal static void Clear()
        {
            savers = new();
            magazineFlags = new();
            shots?.Clear();
        }
        internal static void Register(CharacterAbilitySecurityMagazineSave ability)
        {
            var list = savers.GetOrCreateValue(ability.Character);
            // 清掉已被洗掉的实例，不碰其他天赋自己的状态。
            list.RemoveAll(a => !ability.Character.CharacterTalents.Contains(a.CharacterTalent));
            if (!list.Contains(ability)) list.Add(ability);
        }
        internal static bool Load(ContentXElement abilityElement, CharacterAbilityGroup characterAbilityGroup,
            ref CharacterAbility __result)
        {
            if (!SecurityRules.OwnContext) return true;
            switch (abilityElement.Name.LocalName.ToLowerInvariant())
            {
                case "characterabilitysecuritymagazinesave":
                    __result = new CharacterAbilitySecurityMagazineSave(characterAbilityGroup, abilityElement); return false;
                default: return true;
            }
        }
        internal static bool IsMagazine(Item item)
        {
            if (item.GetComponent<ItemContainer>() == null) return false;
            return magazineFlags.GetValue(item.Prefab, prefab => new MagazineFlag
            {
                // 认“扣耐久生成射弹”的弹匣，不按 handheldammo 把电池、散装子弹也算进去。
                Value = prefab.ConfigElement.Element.Descendants().Any(e =>
                    e.Name.LocalName.Equals("statuseffect", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Attr(e, "type"), "OnUse", StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(Attr(e, "condition"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float cost) && cost < 0f &&
                    e.Elements().Any(child => child.Name.LocalName.Equals("spawnitem", StringComparison.OrdinalIgnoreCase)))
            }).Value;
        }
        private static string Attr(XElement element, string name) =>
            element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

        internal static void UseBegin(Item __instance, Character user, out Shot __state)
        {
            __state = null;
            if (!SecurityRules.Authority || user == null || user.Removed || user.IsDead ||
                __instance.GetComponent<RangedWeapon>() == null || !savers.TryGetValue(user, out var list)) return;
            float chance = 0f;
            foreach (var saver in list)
                if (user.CharacterTalents.Contains(saver.CharacterTalent)) chance = Math.Max(chance, saver.Chance);
            if (chance <= 0f) return;
            var shot = new Shot { Weapon = __instance, Chance = chance };
            foreach (var item in __instance.ContainedItems)
                if (item != null && !item.Removed && item.Condition > 0f && IsMagazine(item))
                    shot.Before[item] = item.Condition;
            if (shot.Before.Count == 0) return;
            (shots ??= new()).Add(shot);
            __state = shot;
        }
        internal static void Shoot(Projectile __instance)
        {
            if (!SecurityRules.Authority || shots == null || shots.Count == 0) return;
            var shot = shots[shots.Count - 1];
            var magazine = __instance.Item.Container;
            if (__instance.Launcher == shot.Weapon && magazine != null && shot.Before.ContainsKey(magazine))
                shot.Fired.Add(magazine);
        }
        internal static void UseEnd(Shot __state)
        {
            if (__state == null || !SecurityRules.Authority || __state.Fired.Count == 0) return;
            // 连同枪械 OnUse 结算完才返还，避免只捕捉到弹匣生成射弹前半段。
            if (SecurityRules.Roll() >= __state.Chance) return;
            foreach (var magazine in __state.Fired)
            {
                if (magazine.Removed || magazine.Container != __state.Weapon) continue;
                float oldCondition = __state.Before[magazine];
                if (magazine.Condition < oldCondition)
                    magazine.Condition = Math.Min(magazine.MaxCondition, oldCondition);
            }
        }
        internal static void UseFinally(Shot __state)
        {
            if (__state != null) shots?.Remove(__state);
        }
    }
}
