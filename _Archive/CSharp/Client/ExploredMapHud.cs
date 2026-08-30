using System;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ExploredMap
{
    /// <summary>
    /// HUD 大图面板：当本地玩家手持海图仪时，在屏幕右上角绘制放大的海图，
    /// 含航线、玩家/潜艇/前哨站标记与坐标读数。
    /// 补丁方式参照已通过实机验证的 CharacterHUD.Draw 后置补丁（spriteBatch 处于活动状态）。
    /// </summary>
    [HarmonyPatch(typeof(CharacterHUD), nameof(CharacterHUD.Draw))]
    internal static class ExploredMapHudPatch
    {
        private static void Postfix(SpriteBatch spriteBatch, Character character, Camera cam)
        {
            if (spriteBatch == null || GUI.DisableHUD)
            {
                return;
            }
            if (character == null || character != Character.Controlled)
            {
                return;
            }
            // 健康窗口/查看他人时 HUD 隐藏，保持一致
            if (CharacterHealth.OpenHealthWindow != null || character.SelectedCharacter != null)
            {
                return;
            }
            if (!ExploredMapTracker.TryGetEquippedScanRange(character, out _))
            {
                return;
            }
            ExploredMapHud.DrawPanel(spriteBatch, character);
        }
    }

    internal static class ExploredMapHud
    {
        private static Texture2D pixel;

        private static Texture2D Pixel
        {
            get
            {
                GraphicsDevice gd = GameMain.Instance?.GraphicsDevice;
                if (gd != null && (pixel == null || pixel.IsDisposed))
                {
                    pixel = new Texture2D(gd, 1, 1);
                    pixel.SetData(new[] { Color.White });
                }
                return pixel;
            }
        }

        public static void DrawPanel(SpriteBatch spriteBatch, Character player)
        {
            Texture2D mapTexture = ExploredMapTracker.MapTexture;
            if (mapTexture == null || Pixel == null)
            {
                return;
            }

            float uiScale = GUI.Scale;
            float panelH = GameMain.GraphicsHeight * 0.45f;
            float panelW = panelH * 2f;
            int pad = (int)(4 * uiScale);
            var bg = new Rectangle(
                (int)(GameMain.GraphicsWidth - panelW - 16 * uiScale),
                (int)(56 * uiScale),
                (int)panelW,
                (int)panelH);
            var mapRect = new Rectangle(bg.X + pad, bg.Y + pad, bg.Width - pad * 2, bg.Height - pad * 2);

            // 背景与地图
            FillRect(spriteBatch, bg, new Color(3, 12, 16, 220));
            spriteBatch.Draw(mapTexture, mapRect, Color.White);

            // 航线（隔点绘制降低开销）
            var path = ExploredMapTracker.Path;
            Color pathColor = new Color(120, 220, 255) * 0.9f;
            float thickness = MathF.Max(1.5f, 2 * uiScale);
            for (int i = 1; i < path.Count; i += 2)
            {
                DrawLine(spriteBatch, WorldToMap(path[i - 1], mapRect), WorldToMap(path[i], mapRect), pathColor, thickness);
            }

            // 标记：前哨站 / 主潜艇 / 玩家
            Level level = Level.Loaded;
            if (level != null)
            {
                if (level.StartOutpost != null)
                {
                    DrawMarker(spriteBatch, WorldToMap(level.StartOutpost.WorldPosition, mapRect), new Color(255, 170, 60), 8 * uiScale);
                }
                if (level.EndOutpost != null)
                {
                    DrawMarker(spriteBatch, WorldToMap(level.EndOutpost.WorldPosition, mapRect), new Color(80, 160, 255), 8 * uiScale);
                }
            }
            if (Submarine.MainSub != null)
            {
                DrawMarker(spriteBatch, WorldToMap(Submarine.MainSub.WorldPosition, mapRect), Color.White, 8 * uiScale);
            }
            DrawMarker(spriteBatch, WorldToMap(player.WorldPosition, mapRect), new Color(80, 255, 120), 6 * uiScale);

            // 边框
            Color border = new Color(90, 160, 200) * 0.8f;
            float bw = MathF.Max(1f, 1.5f * uiScale);
            FillRect(spriteBatch, new Rectangle(bg.X, bg.Y, bg.Width, (int)bw), border);
            FillRect(spriteBatch, new Rectangle(bg.X, bg.Bottom - (int)bw, bg.Width, (int)bw), border);
            FillRect(spriteBatch, new Rectangle(bg.X, bg.Y, (int)bw, bg.Height), border);
            FillRect(spriteBatch, new Rectangle(bg.Right - (int)bw, bg.Y, (int)bw, bg.Height), border);

            // 标题与坐标读数
            try
            {
                var textColor = new Color(150, 230, 255);
                GUIStyle.SmallFont.DrawString(spriteBatch, "探索海图",
                    new Vector2(bg.X, bg.Y - 20 * uiScale), textColor);
                string coords = $"X {player.WorldPosition.X / 100f:F0} m  深度 {-player.WorldPosition.Y / 100f:F0} m";
                GUIStyle.SmallFont.DrawString(spriteBatch, coords,
                    new Vector2(bg.X, bg.Bottom + 4 * uiScale), textColor);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError($"[探索海图仪] 文本绘制失败: {e.Message}");
            }
        }

        /// <summary>世界坐标 → 面板内屏幕坐标（世界 Y 轴向下为负，翻转映射）。</summary>
        private static Vector2 WorldToMap(Vector2 worldPos, Rectangle mapRect)
        {
            float fx = worldPos.X / (ExploredMapTracker.GridW * ExploredMapTracker.CellSize);
            float fy = -worldPos.Y / (ExploredMapTracker.GridH * ExploredMapTracker.CellSize);
            return new Vector2(
                mapRect.X + MathHelper.Clamp(fx, 0f, 1f) * mapRect.Width,
                mapRect.Y + MathHelper.Clamp(fy, 0f, 1f) * mapRect.Height);
        }

        private static void FillRect(SpriteBatch spriteBatch, Rectangle rect, Color color)
        {
            spriteBatch.Draw(Pixel, rect, color);
        }

        private static void DrawMarker(SpriteBatch spriteBatch, Vector2 center, Color color, float size)
        {
            int s = Math.Max(2, (int)size);
            FillRect(spriteBatch, new Rectangle((int)center.X - s / 2, (int)center.Y - s / 2, s, s), color);
        }

        private static void DrawLine(SpriteBatch spriteBatch, Vector2 a, Vector2 b, Color color, float thickness)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len < 0.01f)
            {
                return;
            }
            spriteBatch.Draw(Pixel, a, null, color, MathF.Atan2(d.Y, d.X), Vector2.Zero,
                new Vector2(len, thickness), SpriteEffects.None, 0f);
        }
    }
}
