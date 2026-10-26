// 通用武器能量：插件入口。只负责加载配置与安装补丁，具体逻辑在 Component / Runtime 里。
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.WeaponEnergy
{
    public sealed class WeaponEnergyPlugin : IAssemblyPlugin
    {
        // PreInit 先于 Initialize，补丁所有者必须在构造时就准备好。
        private readonly Harmony harmony = new Harmony(
#if SERVER
            "touhou.weaponenergy.server");
#else
            "touhou.weaponenergy.client");
#endif
        private bool patched;

        public void Initialize() { }
        public void PreInitPatching() => Install();
        public void OnLoadCompleted() => Install();

        private void Install()
        {
            if (patched) return;
            harmony.UnpatchSelf();
            harmony.PatchAll(typeof(WeaponEnergyComponentLoadPatch));
            harmony.PatchAll(typeof(WeaponEnergyLegacySavePatch));
            harmony.PatchAll(typeof(WeaponEnergyButtonPatch));
            harmony.PatchAll(typeof(WeaponEnergyShootPatch));
            harmony.PatchAll(typeof(WeaponEnergyImpactPatch));
#if SERVER
            harmony.PatchAll(typeof(WeaponEnergyServerButtonContextPatch));
#endif
            patched = true;
        }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            WeaponEnergyRuntime.Clear();
            patched = false;
        }
    }
}
