using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    public sealed class Plugin : IAssemblyPlugin
    {
        private const string HarmonyId = "touhou.weaponbadges";
        private Harmony harmony;
        private static Plugin instance;
        private BadgeConfig config;
        private ItemVisualEffects visualEffects;
        private readonly Dictionary<BadgeRule, BadgeImage> images = new Dictionary<BadgeRule, BadgeImage>();
        private readonly HashSet<BadgeRule> failedImages = new HashSet<BadgeRule>();
        private ConditionalWeakTable<GUIComponent, ItemPrefab> previews = new ConditionalWeakTable<GUIComponent, ItemPrefab>();
        [ThreadStatic] private static BadgeRule context;
        private static bool tooltipDrawn;

        public IPluginManagementService PluginService { get; set; }

        public void Initialize()
        {
            if (PluginService == null || !PluginService.TryGetPackageForPlugin<Plugin>(out var package))
            { throw new InvalidOperationException("Cannot locate the weapon badge content package."); }
            config = BadgeConfig.Load(package.Dir);
            try
            {
                SweepConfig sweepConfig = SweepConfig.Load(package.Dir);
                visualEffects = new ItemVisualEffects(this, sweepConfig);
                EffectSettings.Initialize(sweepConfig, visualEffects);
            }
            catch (Exception e)
            {
                EffectSettings.Dispose(visualEffects);
                visualEffects?.Dispose();
                visualEffects = null;
                DebugConsole.AddWarning($"[Touhou.WeaponBadges] 扫光配置无法加载，等级徽章仍可使用：{e.Message}");
            }
            instance = this;
            harmony = new Harmony(HarmonyId);
            try { InstallPatches(); }
            catch { Dispose(); throw; }
            DebugConsole.NewMessage($"[Touhou.WeaponBadges] 客户端等级徽章已加载，{config.Rules.Count} 条 tag 映射。", Color.LightGreen);
        }

        private void InstallPatches()
        {
            Patch(typeof(GUIComponent), "DrawToolTip", new[] { typeof(SpriteBatch) },
                prefix: nameof(BeginGuiTooltip), finalizer: nameof(EndContext));
            Patch(typeof(Inventory), "DrawToolTip", new[] { typeof(SpriteBatch), typeof(RichString), typeof(Rectangle) },
                prefix: nameof(BeginInventoryTooltip), finalizer: nameof(EndContext));
            Patch(typeof(GUIComponent), "DrawToolTip", new[] { typeof(SpriteBatch), typeof(RichString), typeof(Rectangle), typeof(Anchor), typeof(Pivot) },
                transpiler: nameof(TooltipDrawTranspiler));
            Patch(typeof(GUIComponent), "DrawToolTip", new[] { typeof(SpriteBatch), typeof(RichString), typeof(Vector2), typeof(Color?), typeof(Color?) },
                transpiler: nameof(TooltipDrawTranspiler));
            Patch(typeof(GUI), "Draw", new[] { typeof(Camera), typeof(SpriteBatch) },
                prefix: nameof(BeginGuiDraw), postfix: nameof(EndGuiDraw));
            // The selected recipe panel has no ItemPrefab UserData in vanilla.
            MethodInfo selected = AccessTools.GetDeclaredMethods(typeof(Fabricator)).Single(m => m.Name == "CreateSelectedItemUI");
            harmony.Patch(selected, postfix: new HarmonyMethod(typeof(Plugin), nameof(RecipeSelected)));
            visualEffects?.Apply(harmony);
        }

        private void Patch(Type type, string name, Type[] parameters, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
        {
            MethodInfo method = AccessTools.Method(type, name, parameters);
            if (method == null) { throw new MissingMethodException(type.FullName, name); }
            harmony.Patch(method,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Plugin), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Plugin), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(Plugin), finalizer),
                transpiler: transpiler == null ? null : new HarmonyMethod(typeof(Plugin), transpiler),
                ilmanipulator: null);
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            EffectSettings.Dispose(visualEffects);
            visualEffects?.Dispose();
            visualEffects = null;
            foreach (BadgeImage image in images.Values) { image.Dispose(); }
            images.Clear();
            failedImages.Clear();
            previews = new ConditionalWeakTable<GUIComponent, ItemPrefab>();
            if (ReferenceEquals(instance, this)) { instance = null; context = null; }
        }

        private static void BeginGuiTooltip(GUIComponent __instance, out BadgeRule __state)
        {
            __state = context;
            context = instance?.Resolve(__instance);
        }

        private static void BeginInventoryTooltip(out BadgeRule __state)
        {
            __state = context;
            Item item = Inventory.SelectedSlot?.Item;
            context = instance?.config.Match(item, item?.Prefab);
        }

        private static void EndContext(BadgeRule __state) { context = __state; }

        private BadgeRule Resolve(GUIComponent component)
        {
            return TryResolveItem(component, out Item item, out ItemPrefab prefab) ? config.Match(item, prefab) : null;
        }

        internal bool TryResolveItem(GUIComponent component, out Item item, out ItemPrefab prefab)
        {
            item = null;
            prefab = null;
            for (GUIComponent c = component; c != null; c = c.Parent)
            {
                if (c.UserData is Item liveItem) { item = liveItem; prefab = liveItem.Prefab; return true; }
                if (c.UserData is ItemPrefab itemPrefab) { prefab = itemPrefab; return true; }
                if (c.UserData is FabricationRecipe recipe) { prefab = recipe.TargetItem; return prefab != null; }
                if (c.UserData is PurchasedItem purchased) { prefab = purchased.ItemPrefab; return prefab != null; }
                if (previews.TryGetValue(c, out ItemPrefab preview)) { prefab = preview; return true; }
            }
            return false;
        }

        private static void RecipeSelected(Fabricator __instance, GUIFrame ___selectedItemFrame)
        {
            if (instance == null || ___selectedItemFrame == null) { return; }
            instance.previews.Remove(___selectedItemFrame);
            ItemPrefab prefab = __instance.SelectedItem?.TargetItem;
            if (prefab != null) { instance.previews.Add(___selectedItemFrame, prefab); }
        }

        private static IEnumerable<CodeInstruction> TooltipDrawTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo draw = AccessTools.Method(typeof(GUIComponent), "DrawManually",
                new[] { typeof(SpriteBatch), typeof(bool), typeof(bool) });
            MethodInfo replacement = AccessTools.Method(typeof(Plugin), nameof(DrawTooltipWithBadge));
            var result = instructions.ToList();
            int replaced = 0;
            foreach (CodeInstruction instruction in result)
            {
                if (!instruction.Calls(draw)) { continue; }
                // Keep the vanilla tooltip's construction, rich text and anchoring.
                // Only wrap its final draw to reserve a column beside the text.
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            if (replaced != 1) { throw new InvalidOperationException("Expected one tooltip DrawManually call."); }
            return result;
        }

        private static void DrawTooltipWithBadge(GUIComponent component, SpriteBatch spriteBatch, bool alsoChildren, bool recursive)
        {
            BadgeImage image = instance != null && context != null ? instance.GetImage(context) : null;
            if (image == null || !(component is GUITextBlock block))
            {
                component.DrawManually(spriteBatch, alsoChildren, recursive);
                return;
            }
            int size = GUI.IntScale(instance.config.Size);
            int gap = GUI.IntScale(10);
            Point originalSize = block.RectTransform.NonScaledSize;
            Point originalOffset = block.RectTransform.AbsoluteOffset;
            Vector4 originalPadding = block.Padding;
            Alignment originalAlignment = block.TextAlignment;
            try
            {
                int column = size + gap;
                block.RectTransform.NonScaledSize = new Point(originalSize.X + column,
                    Math.Max(originalSize.Y, (int)Math.Ceiling(originalPadding.Y + size + originalPadding.W)));
                // Width and right padding grow together: wrapping and the text
                // column's width are unchanged, including other mods' descriptions.
                block.Padding = originalPadding + new Vector4(0, 0, column, 0);
                block.TextAlignment = Alignment.TopLeft;
                Rectangle fitted = FitTooltipBounds(block.Rect, GameMain.GraphicsWidth, GameMain.GraphicsHeight);
                block.RectTransform.AbsoluteOffset += fitted.Location - block.Rect.Location;
                block.SetTextPos();
                component.DrawManually(spriteBatch, alsoChildren, recursive);
                Rectangle rect = block.Rect;
                var badge = new Rectangle(rect.Right - (int)originalPadding.Z - gap / 2 - size,
                    rect.Top + (int)originalPadding.Y, size, size);
                image.Draw(spriteBatch, badge);
                tooltipDrawn = true;
            }
            finally
            {
                // The shared vanilla cache must not retain the badge column when
                // the next tooltip belongs to an untagged item or a normal button.
                block.RectTransform.NonScaledSize = originalSize;
                block.Padding = originalPadding;
                block.TextAlignment = originalAlignment;
                block.RectTransform.AbsoluteOffset = originalOffset;
                block.SetTextPos();
            }
        }

        private static Rectangle FitTooltipBounds(Rectangle rect, int screenWidth, int screenHeight)
        {
            const int margin = 10;
            rect.X = Math.Clamp(rect.X, margin, Math.Max(margin, screenWidth - margin - rect.Width));
            rect.Y = Math.Clamp(rect.Y, margin, Math.Max(margin, screenHeight - margin - rect.Height));
            return rect;
        }

        private BadgeImage GetImage(BadgeRule rule)
        {
            if (images.TryGetValue(rule, out BadgeImage image)) { return image; }
            if (failedImages.Contains(rule)) { return null; }
            try
            {
                image = new BadgeImage(GameMain.GraphicsDeviceManager.GraphicsDevice, rule);
                images.Add(rule, image);
                return image;
            }
            catch (Exception e)
            {
                failedImages.Add(rule);
                DebugConsole.AddWarning($"[TouhouWeaponBadges] 图片无法加载 {rule.File}: {e.Message}");
                return null;
            }
        }

        private static void BeginGuiDraw() { tooltipDrawn = false; }

        private static void EndGuiDraw(SpriteBatch spriteBatch)
        {
            if (instance == null || !instance.config.PreviewBadges || tooltipDrawn || GUI.PauseMenuOpen || GUI.SettingsMenuOpen || ObjectiveManager.ContentRunning) { return; }
            BadgeRule rule = instance.Resolve(GUI.MouseOn);
            if (rule == null) { return; }
            BadgeRule previous = context;
            try
            {
                context = rule;
                // Handles recipe/preview widgets with an empty vanilla tooltip too.
                GUIComponent.DrawToolTip(spriteBatch, RichString.Plain("Lv. " + Roman(rule.Level)),
                    PlayerInput.MousePosition + new Vector2(GUI.IntScale(18), GUI.IntScale(18)));
            }
            finally { context = previous; }
        }

        private static string Roman(int level)
        {
            string[] numerals = { "0", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return level >= 0 && level < numerals.Length ? numerals[level] : level.ToString();
        }
    }
}
