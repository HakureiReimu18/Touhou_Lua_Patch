using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    internal sealed class ItemVisualEffects : IDisposable
    {
        private struct DrawContext
        {
            internal SweepProfile Profile;
            internal SweepProfile[] Profiles;
            internal EffectSurface Surface;
            internal Sprite Sprite;
            internal float Seed;
            internal bool World;
            internal bool StraightAlpha;
        }

        private enum DrawStage { Background, Replacement, Overlay }
        private struct SpriteDrawState
        {
            internal bool Counted, Replaced;
            internal int Submitted, RemainingLayers;
        }

        private static ItemVisualEffects instance;
        [ThreadStatic] private static DrawContext current;
        private readonly Plugin plugin;
        private readonly SweepConfig config;
        private readonly SweepRenderer renderer;
        internal static EffectSurface CurrentSurface => current.Surface;

        internal ItemVisualEffects(Plugin plugin, SweepConfig config)
        {
            this.plugin = plugin;
            this.config = config;
            renderer = new SweepRenderer(config.CacheMegabytes);
            instance = this;
        }

        internal void Apply(Harmony harmony)
        {
            // Install every entry point even when disabled; settings may re-enable it live.
            Patch(typeof(Inventory), "DrawSlot", new[] { typeof(SpriteBatch), typeof(Inventory), typeof(VisualSlot),
                typeof(Item), typeof(int), typeof(bool), typeof(InvSlotType) }, nameof(BeginInventory), nameof(EndScope), nameof(DrawSlotTranspiler));
            Patch(typeof(Inventory), "DrawFront", new[] { typeof(SpriteBatch) }, nameof(BeginDragging), nameof(EndScope));
            Patch(typeof(GUIImage), "Draw", new[] { typeof(SpriteBatch) }, nameof(BeginPreview), nameof(EndScope), nameof(PreviewDrawTranspiler));
            Patch(typeof(Fabricator), "DrawOutputOverLay", new[] { typeof(SpriteBatch), typeof(GUICustomComponent) },
                nameof(BeginFabricatorOutput), nameof(EndScope));
            Patch(typeof(Item), "Draw", new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) },
                nameof(BeginWorld), nameof(EndScope));
            MethodInfo spriteDraw = AccessTools.Method(typeof(Sprite), "Draw", new[] { typeof(ISpriteBatch), typeof(Vector2),
                typeof(Color), typeof(Vector2), typeof(float), typeof(Vector2), typeof(SpriteEffects), typeof(float?) });
            if (spriteDraw == null) { throw new MissingMethodException("Sprite.Draw(ISpriteBatch,...Vector2 scale)"); }
            harmony.Patch(spriteDraw, prefix: new HarmonyMethod(typeof(ItemVisualEffects), nameof(BeforeSpriteDraw)),
                postfix: new HarmonyMethod(typeof(ItemVisualEffects), nameof(AfterSpriteDraw)));
            harmony.Patch(AccessTools.Method(typeof(GameMain), "Draw", new[] { typeof(GameTime) }),
                postfix: new HarmonyMethod(typeof(ItemVisualEffects), nameof(EndFrame)));
            DebugConsole.NewMessage($"[Touhou.WeaponBadges] 扫光已加载：{config.Rules.Count} 条 tag 规则。", Color.LightGreen);

            void Patch(Type type, string name, Type[] parameters, string prefix, string finalizer, string transpiler = null)
            {
                MethodInfo method = AccessTools.Method(type, name, parameters);
                if (method == null) { throw new MissingMethodException(type.FullName, name); }
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ItemVisualEffects), prefix) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(ItemVisualEffects), finalizer) { priority = Priority.Last },
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(ItemVisualEffects), transpiler),
                    ilmanipulator: null);
            }
        }

        internal void ApplySettings(EffectSettings.Values value)
        {
            config.Enabled = value.Enabled;
            config.Inventory = value.Inventory;
            config.Previews = value.Previews;
            config.World = value.World;
            config.WorldIntensity = value.WorldIntensity;
            config.CacheMegabytes = value.CacheMegabytes;
            config.AffixGlow.Enabled = value.AffixGlow;
            renderer.Configure(value.Quality, value.Sparkles, value.MaxPerFrame, value.CacheMegabytes);
        }

        private DrawContext ForItem(Item item, ItemPrefab prefab, Sprite sprite, bool world)
            => ForSurface(item, prefab, sprite, world ? EffectSurface.World : EffectSurface.Inventory);

        private DrawContext ForSurface(Item item, ItemPrefab prefab, Sprite sprite, EffectSurface surface)
        {
            bool world = surface == EffectSurface.World;
            DrawContext empty = new DrawContext { Surface = surface };
            if (!config.Enabled || sprite == null || (world && (!config.World || config.WorldIntensity <= 0))) { return empty; }
            SweepProfile[] profiles = config.MatchForSurface(item, prefab, surface);
            if (profiles.Length == 0) { return empty; }
            int hash = prefab?.Identifier.GetHashCode() ?? 0;
            return new DrawContext { Profile = profiles[0], Profiles = profiles, Surface = surface,
                Sprite = sprite, World = world, StraightAlpha = true,
                Seed = (hash & 1023) / 1024f };
        }

        private static void BeginInventory(Item item, bool drawItem, out DrawContext __state)
        {
            __state = current;
            current = instance != null && instance.config.Enabled && instance.config.Inventory && item != null && drawItem
                ? instance.ForItem(item, item.Prefab, item.OverrideInventorySprite ?? item.Prefab.InventoryIcon ?? item.Sprite, false)
                : default;
        }

        private static void BeginDragging(out DrawContext __state)
        {
            __state = current;
            current = default;
            if (instance == null || !instance.config.Enabled || !instance.config.Inventory) { return; }
            Item item = Inventory.DraggingItems.FirstOrDefault();
            current = item != null
                ? instance.ForItem(item, item.Prefab, item.OverrideInventorySprite ?? item.Prefab.InventoryIcon ?? item.Sprite, false)
                : default;
        }

        private static void BeginWorld(Item __instance, Sprite ___activeSprite, out DrawContext __state)
        {
            __state = current;
            current = instance != null && instance.config.Enabled && instance.config.World && instance.config.WorldIntensity > 0
                ? instance.ForItem(__instance, __instance.Prefab, ___activeSprite, true) : default;
        }

        private static void BeginPreview(GUIImage __instance, out DrawContext __state)
        {
            __state = current;
            current = new DrawContext { Surface = EffectSurface.Preview };
            if (instance == null || !instance.config.Enabled || !instance.config.Previews || !__instance.Visible || __instance.Sprite == null ||
                !instance.plugin.TryResolveItem(__instance, out Item item, out ItemPrefab prefab)) { return; }
            Sprite sprite = __instance.Sprite;
            // Ignore UI decorations (quality/price icons etc.) under an item row.
            if (sprite != prefab.InventoryIcon && sprite != prefab.Sprite && sprite != item?.OverrideInventorySprite) { return; }
            current = instance.ForSurface(item, prefab, sprite, EffectSurface.Preview);
            // Barotrauma's SpriteBatch default is NonPremultiplied, including null.
            BlendState blend = __instance.BlendState;
            current.StraightAlpha = blend == null || blend.ColorSourceBlend != Blend.One ||
                blend.ColorDestinationBlend != Blend.InverseSourceAlpha;
        }

        private static void BeginFabricatorOutput(FabricationRecipe ___fabricatedItem, FabricationRecipe ___selectedItem, out DrawContext __state)
        {
            __state = current;
            current = new DrawContext { Surface = EffectSurface.Preview };
            if (instance == null || !instance.config.Enabled || !instance.config.Previews) { return; }
            ItemPrefab prefab = (___fabricatedItem ?? ___selectedItem)?.TargetItem;
            current = prefab != null
                ? instance.ForSurface(null, prefab, prefab.InventoryIcon ?? prefab.Sprite, EffectSurface.Preview)
                : new DrawContext { Surface = EffectSurface.Preview };
        }

        private static void EndScope(DrawContext __state) { current = __state; }

        private static bool BeforeSpriteDraw(Sprite __instance, ISpriteBatch spriteBatch, Vector2 pos, Color color,
            Vector2 origin, float rotate, Vector2 scale, SpriteEffects spriteEffect, float? depth, out SpriteDrawState __state)
        {
            __state = default;
            if (instance == null || !instance.config.Enabled || current.Profiles == null ||
                !ReferenceEquals(__instance, current.Sprite)) { return true; }
            __state.RemainingLayers = current.Profiles.Length;
            instance.DrawProfiles(spriteBatch, __instance.SourceRect, pos + __instance.offset, origin,
                __instance.rotation + rotate, scale, spriteEffect, depth ?? __instance.Depth, color, DrawStage.Background, ref __state);
            __state.Replaced = instance.DrawProfiles(spriteBatch, __instance.SourceRect, pos + __instance.offset, origin,
                __instance.rotation + rotate, scale, spriteEffect, depth ?? __instance.Depth, color, DrawStage.Replacement, ref __state);
            // A cold/over-budget/unsupported effect keeps the original item visible.
            return !__state.Replaced;
        }

        private static void AfterSpriteDraw(Sprite __instance, ISpriteBatch spriteBatch, Vector2 pos, Color color,
            Vector2 origin, float rotate, Vector2 scale, SpriteEffects spriteEffect, float? depth, SpriteDrawState __state)
        {
            if (instance == null || !instance.config.Enabled || current.Profiles == null || !ReferenceEquals(__instance, current.Sprite)) { return; }
            instance.DrawProfiles(spriteBatch, __instance.SourceRect, pos + __instance.offset, origin,
                __instance.rotation + rotate, scale, spriteEffect, depth ?? __instance.Depth, color, DrawStage.Overlay, ref __state);
        }

        private bool DrawProfiles(ISpriteBatch batch, Rectangle source, Vector2 position, Vector2 origin,
            float rotation, Vector2 scale, SpriteEffects effects, float depth, Color color,
            DrawStage stage, ref SpriteDrawState state)
        {
            bool drawn = false;
            foreach (SweepProfile profile in current.Profiles)
            {
                DrawStage layer = profile.ArtEffect?.Kind == "blocks" ? DrawStage.Replacement
                    : !current.World && profile.ArtEffect?.Presentation == "aura" ? DrawStage.Background : DrawStage.Overlay;
                if (layer != stage) { continue; }
                int remaining = renderer.SubmissionLimit - state.Submitted;
                bool legacySweep = current.Profiles.Length == 1 && profile.ArtEffect == null;
                int allocation = legacySweep ? 0 : remaining / Math.Max(1, state.RemainingLayers);
                state.RemainingLayers--;
                if (!legacySweep && allocation < 1) { continue; }
                bool success = renderer.DrawLayer(batch, current.Sprite, source, position, origin, rotation,
                    scale, effects, depth, color, profile, current.Seed, current.World,
                    config.WorldIntensity, current.StraightAlpha, allocation, state.Counted);
                state.Submitted += renderer.LastSubmissions;
                state.Counted |= success;
                drawn |= success;
            }
            return drawn;
        }

        private static IEnumerable<CodeInstruction> DrawSlotTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var result = instructions.ToList();
            MethodInfo draw = AccessTools.Method(typeof(SpriteBatch), "Draw", new[] { typeof(Texture2D), typeof(Rectangle),
                typeof(Rectangle?), typeof(Color) });
            MethodInfo background = AccessTools.Method(typeof(ItemVisualEffects), nameof(DrawAffixSlotBackground));
            int drawIndex = draw == null ? -1 : result.FindIndex(i => i.Calls(draw));
            if (drawIndex < 0 || background == null)
            {
                DebugConsole.NewMessage("[Touhou.WeaponBadges] 找不到格子底图注入点，已跳过词缀背景光。", Color.Yellow);
                return result;
            }

            // 只插在首个格子底图之后；后面的品质框、Highlight 和物品图标仍盖在它上面。
            result.InsertRange(drawIndex + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_3),
                new CodeInstruction(OpCodes.Ldarg, 5),
                new CodeInstruction(OpCodes.Call, background)
            });
            return result;
        }

        private static void DrawAffixSlotBackground(SpriteBatch spriteBatch, VisualSlot slot, Item item, bool drawItem)
        {
            if (instance == null || item == null || !drawItem) { return; }
            SweepConfig config = instance.config;
            if (!config.Enabled || !config.Inventory || config.AffixGlow.Intensity <= 0f ||
                !config.AffixGlow.TryMatch(item, out Color glow)) { return; }

            Rectangle rect = slot.Rect;
            rect.Location += slot.DrawOffset.ToPoint();
            if (slot.HighlightColor.A > 0)
            {
                float amount = slot.HighlightColor.A / 255f * slot.HighlightScaleUpAmount * .5f;
                rect.Inflate(rect.Width * amount, rect.Height * amount);
            }
            int inset = (int)Math.Round(config.AffixGlow.Inset * GUI.Scale);
            if (rect.Width <= inset * 2 || rect.Height <= inset * 2) { return; }
            rect.Inflate(-inset, -inset);

            // 填充只留一点颜色，主要亮度放在边缘，物品细节不被洗掉。
            GUI.DrawRectangle(spriteBatch, rect, glow * (config.AffixGlow.Intensity * .12f), isFilled: true);
            GUIStyle.UIGlow?.Draw(spriteBatch, rect, glow * config.AffixGlow.Intensity);
        }

        private static IEnumerable<CodeInstruction> PreviewDrawTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo draw = AccessTools.Method(typeof(SpriteBatch), "Draw", new[] { typeof(Texture2D), typeof(Vector2),
                typeof(Rectangle?), typeof(Color), typeof(float), typeof(Vector2), typeof(float), typeof(SpriteEffects), typeof(float) });
            MethodInfo replacement = AccessTools.Method(typeof(ItemVisualEffects), nameof(DrawPreview));
            var result = instructions.ToList();
            int replaced = 0;
            foreach (CodeInstruction instruction in result)
            {
                if (!instruction.Calls(draw)) { continue; }
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
                replaced++;
            }
            if (replaced < 1) { throw new InvalidOperationException("GUIImage sprite draw call not found."); }
            return result;
        }

        private static void DrawPreview(SpriteBatch batch, Texture2D texture, Vector2 position, Rectangle? source,
            Color color, float rotation, Vector2 origin, float scale, SpriteEffects effects, float depth)
        {
            bool active = instance != null && instance.config.Enabled && current.Profiles != null && current.Sprite != null && texture == current.Sprite.Texture;
            if (!active) { batch.Draw(texture, position, source, color, rotation, origin, scale, effects, depth); return; }
            var state = new SpriteDrawState { RemainingLayers = current.Profiles.Length };
            Rectangle rect = source ?? new Rectangle(0, 0, texture.Width, texture.Height);
            instance.DrawProfiles(batch, rect, position, origin, rotation, new Vector2(scale), effects, depth,
                color, DrawStage.Background, ref state);
            bool replaced = instance.DrawProfiles(batch, rect, position, origin, rotation, new Vector2(scale), effects, depth,
                color, DrawStage.Replacement, ref state);
            if (!replaced) { batch.Draw(texture, position, source, color, rotation, origin, scale, effects, depth); }
            instance.DrawProfiles(batch, rect, position, origin, rotation, new Vector2(scale), effects, depth,
                color, DrawStage.Overlay, ref state);
        }

        private static void EndFrame() { instance?.renderer.EndFrame(); }

        public void Dispose()
        {
            renderer.Dispose();
            if (ReferenceEquals(instance, this)) { instance = null; current = default; }
        }
    }
}
