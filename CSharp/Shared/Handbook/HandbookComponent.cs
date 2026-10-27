using System;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;

namespace Touhou.Handbook
{
    public sealed partial class TouhouHandbook : ItemComponent
    {
        internal readonly HandbookDocument Document;
        internal readonly HandbookNavigation Navigation;
        public TouhouHandbook(Item item, ContentXElement element) : base(item, element)
        {
            name = "TouhouHandbook";
            CanBeSelected = true;
            XElement xml = element;
            Document = HandbookDocument.Parse(xml);
            Navigation = new HandbookNavigation(Document);
            foreach (var warning in Document.Warnings)
                DebugConsole.AddWarning("[帕秋莉手册] " + item.Prefab.Identifier + "：" + warning);
        }
    }

    public sealed partial class HandbookPlugin : IAssemblyPlugin
    {
        private readonly Harmony harmony = new Harmony(
#if SERVER
            "touhou.handbook.server");
#else
            "touhou.handbook.client");
#endif
        private bool installed;
        public void Initialize() { }
        public void PreInitPatching() => Install();
        public void OnLoadCompleted() => Install();
        private void Install()
        {
            if (installed) return;
            harmony.PatchAll(typeof(HandbookFactoryPatch));
            installed = true;
        }
        public void Dispose()
        {
            harmony.UnpatchSelf();
            RemoveClientViews();
            installed = false;
        }
        partial void RemoveClientViews();
    }

    [HarmonyPatch(typeof(ItemComponent), nameof(ItemComponent.Load),
        new[] { typeof(ContentXElement), typeof(Item), typeof(bool) })]
    internal static class HandbookFactoryPatch
    {
        private static bool Prefix(ContentXElement element, Item item, ref ItemComponent __result)
        {
#if SERVER
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsServer) return true;
#else
            if (GameMain.NetworkMember != null && !GameMain.NetworkMember.IsClient) return true;
#endif
            if (!element.Name.ToString().Equals("TouhouHandbook", StringComparison.OrdinalIgnoreCase)) return true;
            __result = new TouhouHandbook(item, element);
            return false;
        }
    }
}
