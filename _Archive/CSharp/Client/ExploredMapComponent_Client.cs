using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ExploredMap.Items
{
#if CLIENT
    /// <summary>
    /// 客户端分部：实现 IDrawableComponent，把探索海图纹理绘制到设备贴图表面。
    /// 绘制数学与社区已验证的 VideoScreenComponent 一致（item.DrawPosition + -item.Rotation）。
    /// </summary>
    partial class ExploredMapComponent : IDrawableComponent
    {
        /// <summary>屏幕贴图占设备贴图的比例（XML 可调）。</summary>
        [Editable, Serialize(0.9f, IsPropertySaveable.Yes)]
        public float ScreenScale { get; set; }

        /// <summary>手持扫描半径（世界单位，100 = 1 米）（XML 可调）。</summary>
        [Editable, Serialize(6000f, IsPropertySaveable.Yes)]
        public float ScanRange { get; set; }

        public Vector2 DrawSize => Vector2.Zero;

        public void Draw(SpriteBatch spriteBatch, bool editing, float itemDepth = -1, Color? overrideColor = null)
        {
            Texture2D mapTexture = ExploredMapTracker.MapTexture;
            if (mapTexture == null || item.Sprite == null)
            {
                return;
            }

            float scaleX = (item.Sprite.size.X / mapTexture.Width) * item.Scale * ScreenScale;
            float scaleY = (item.Sprite.size.Y / mapTexture.Height) * item.Scale * ScreenScale;

            Vector2 drawPos = new Vector2(item.DrawPosition.X, -item.DrawPosition.Y);
            spriteBatch.Draw(
                mapTexture,
                drawPos,
                null,
                Color.White,
                -item.Rotation,
                new Vector2(mapTexture.Width / 2f, mapTexture.Height / 2f),
                new Vector2(scaleX, scaleY),
                SpriteEffects.None,
                itemDepth + 0.001f
            );
        }
    }
#endif
}
