using System;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.LuaCs;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.InnerClothesPortrait;

public sealed class InnerClothesPortraitPlugin : IAssemblyPlugin
{
    private Harmony harmony;
    private bool patched;

    public void Initialize()
    {
        harmony = new Harmony("touhou.innerclothesportrait");
        PatchIfNeeded();
    }

    public void OnLoadCompleted()
    {
        PatchIfNeeded();
    }

    public void PreInitPatching()
    {
    }

    public void Dispose()
    {
        harmony?.UnpatchSelf();
        patched = false;
    }

    private void PatchIfNeeded()
    {
        if (patched)
        {
            return;
        }

        harmony?.PatchAll();
        patched = true;
    }
}

[HarmonyPatch(typeof(CharacterInfo), nameof(CharacterInfo.DrawIcon))]
internal static class InnerClothesPortraitHudPatch
{
    private static bool Prefix(CharacterInfo __instance, SpriteBatch spriteBatch, Vector2 screenPos, Vector2 targetAreaSize, bool flip)
    {
        if (spriteBatch == null || __instance?.Character == null)
        {
            return true;
        }

        if (GUI.DisableHUD)
        {
            return true;
        }

        Character character = __instance.Character;
        if (character != Character.Controlled || character.IsDead || character.Inventory == null)
        {
            return true;
        }

        if (!InnerClothesPortraitHelpers.TryGetTaggedClothing(character, out Item equippedClothing))
        {
            return true;
        }

        if (!InnerClothesPortraitHelpers.DrawClothingIcon(spriteBatch, equippedClothing, screenPos, targetAreaSize, flip))
        {
            return true;
        }

        return false;
    }
}

[HarmonyPatch(typeof(CharacterHUD), nameof(CharacterHUD.Draw))]
internal static class InnerClothesPortraitHudDrawPatch
{
    private static void Postfix(SpriteBatch spriteBatch, Character character, Camera cam)
    {
        if (spriteBatch == null || character == null)
        {
            return;
        }

        if (GUI.DisableHUD)
        {
            return;
        }

        if (CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null)
        {
            return;
        }

        if (character.IsRagdolled || character.IsIncapacitated || character.IsKnockedDown)
        {
            return;
        }

        if (Screen.Selected == GameMain.SubEditorScreen && GameMain.SubEditorScreen.WiringMode)
        {
            return;
        }

        if (character.ShouldLockHud())
        {
            return;
        }

        if (!InnerClothesPortraitHelpers.TryGetTaggedClothing(character, out Item equippedClothing))
        {
            return;
        }

        Vector2 screenPos = new Vector2(HUDLayoutSettings.PortraitArea.Center.X - 12 * GUI.Scale, HUDLayoutSettings.PortraitArea.Center.Y);
        Vector2 targetAreaSize = HUDLayoutSettings.PortraitArea.Size.ToVector2();
        InnerClothesPortraitHelpers.DrawClothingIcon(spriteBatch, equippedClothing, screenPos, targetAreaSize, flip: true);
    }
}

internal static class InnerClothesPortraitHelpers
{
    internal static bool TryGetTaggedClothing(Character character, out Item clothing)
    {
        clothing = null;
        if (character == null || character.Inventory == null)
        {
            return false;
        }

        clothing = GetTaggedClothing(character.Inventory);
        return clothing != null;
    }

    internal static bool DrawClothingIcon(SpriteBatch spriteBatch, Item equippedClothing, Vector2 screenPos, Vector2 targetAreaSize, bool flip)
    {
        Sprite iconSprite = GetClothingSprite(equippedClothing);
        if (iconSprite == null)
        {
            return false;
        }

        iconSprite.EnsureLazyLoaded();
        if (iconSprite.SourceRect.Width <= 0 || iconSprite.SourceRect.Height <= 0)
        {
            return false;
        }

        float scaleX = targetAreaSize.X / iconSprite.SourceRect.Width;
        float scaleY = targetAreaSize.Y / iconSprite.SourceRect.Height;
        float scale = MathF.Min(scaleX, scaleY);
        if (scale <= 0f)
        {
            return false;
        }

        var origin = new Vector2(iconSprite.SourceRect.Width / 2f, iconSprite.SourceRect.Height / 2f);
        if (flip)
        {
            origin.X = iconSprite.SourceRect.Width - origin.X;
        }

        Color color = iconSprite == equippedClothing.Sprite ? equippedClothing.GetSpriteColor() : equippedClothing.GetInventoryIconColor();
        var spriteEffects = flip ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        iconSprite.Draw(spriteBatch, screenPos, origin: origin, scale: scale, color: color, spriteEffect: spriteEffects);
        return true;
    }

    private static Sprite GetClothingSprite(Item equippedClothing)
    {
        if (equippedClothing == null)
        {
            return null;
        }

        ItemPrefab prefab = equippedClothing.Prefab;
        return prefab?.InventoryIcon ?? equippedClothing.Sprite ?? prefab?.Sprite;
    }

    private static Item GetTaggedClothing(CharacterInventory inventory)
    {
        if (inventory == null)
        {
            return null;
        }

        var tag = "Touhou_Clothes".ToIdentifier();
        Item outer = inventory.GetItemInLimbSlot(InvSlotType.OuterClothes);
        if (HasTouhouTag(outer, tag))
        {
            return outer;
        }

        Item inner = inventory.GetItemInLimbSlot(InvSlotType.InnerClothes);
        if (HasTouhouTag(inner, tag))
        {
            return inner;
        }

        for (int i = 0; i < inventory.Capacity; i++)
        {
            InvSlotType slotType = inventory.SlotTypes[i];
            if (slotType != InvSlotType.InnerClothes && slotType != InvSlotType.OuterClothes)
            {
                continue;
            }

            Item item = inventory.GetItemAt(i);
            if (HasTouhouTag(item, tag))
            {
                return item;
            }
        }

        return null;
    }

    private static bool HasTouhouTag(Item item, Identifier tag)
    {
        return item != null && item.HasTag(tag);
    }
}
