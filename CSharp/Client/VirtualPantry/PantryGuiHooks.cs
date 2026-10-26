using Barotrauma;
using HarmonyLib;

namespace Touhou.VirtualPantry
{
    // 柜子组件不在 item.Components 里，原生 Item.UpdateHUD/AddToGUIUpdateList 不会轮到它，
    // 所以在这两处补一脚（只对真的挂着虚拟库存的柜子生效）。
    internal static class PantryGuiHooks
    {
        internal static void Register(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Item), "UpdateHUD", new[] { typeof(Camera), typeof(Character), typeof(float) }),
                postfix: new HarmonyMethod(typeof(PantryGuiHooks), nameof(UpdateHud)));
            harmony.Patch(AccessTools.Method(typeof(Item), "AddToGUIUpdateList", new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(PantryGuiHooks), nameof(AddGui)));
        }

        private static void UpdateHud(Item __instance, Camera cam, Character character, float deltaTime)
        {
            var pantry = PantryPlugin.Find(__instance);
            if (pantry == null || pantry.Retired || !pantry.ShouldDrawHUDComponentSpecific(character)) { return; }
            pantry.UpdateHUD(character, deltaTime, cam);
            pantry.PlaceGui();
        }

        private static void AddGui(Item __instance, int order)
        {
            var pantry = PantryPlugin.Find(__instance);
            if (pantry != null && !pantry.Retired) { pantry.AddToGUIUpdateList(order); }
        }
    }
}
