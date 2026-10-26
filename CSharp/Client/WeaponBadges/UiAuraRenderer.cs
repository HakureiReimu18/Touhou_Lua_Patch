using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    // The animated ring material is already unwrapped onto the real alpha edge.
    // UI draws this below base art; world batches place it behind by depth.
    internal static class UiAuraRenderer
    {
        internal static int Draw(ArtDrawContext c)
        {
            if (c.AuraTexture == null || c.AuraTexture.IsDisposed || c.AuraWidth < 1 || c.AuraHeight < 1)
            { return SoftArtRenderer.Draw(c); }
            if (c.Batch == null || c.Style == null || c.Source.Width < 1 || c.Source.Height < 1 ||
                !Finite(c.Position) || !Finite(c.Origin) || !Finite(c.Scale) || c.Scale.X == 0 || c.Scale.Y == 0 ||
                !float.IsFinite(c.Rotation) || !float.IsFinite(c.Depth) || !float.IsFinite(c.Opacity) ||
                !float.IsFinite(c.Style.Opacity) || c.Opacity <= 0 || c.Style.Opacity <= 0) { return 0; }
            float speed = float.IsFinite(c.Style.Speed) ? Math.Clamp(c.Style.Speed, 0, 12) : 0;
            double phase = (double)(float.IsFinite(c.Time) ? c.Time : 0) * speed * .5
                + (float.IsFinite(c.Seed) ? c.Seed : 0);
            float animation = (float)(phase - Math.Floor(phase)) * 16;
            int frame = Math.Min(15, (int)animation);
            float mix = animation - frame;
            float opacity = Math.Clamp(c.Opacity * c.Style.Opacity, 0, 1);
            float motion = float.IsFinite(c.Style.Amplitude) ? Math.Clamp(c.Style.Amplitude / .3f, 0, 1) : 0;
            opacity *= 1 - motion * .08f * (.5f + .5f * (float)Math.Sin(phase * Math.PI * 2));
            int limit = c.DrawBudget > 0 ? Math.Min(3, c.DrawBudget) : 3, count = 0;
            Color color = Color.Lerp(c.PrimaryColor, c.Style.AccentColor,
                .3f + .2f * (float)Math.Sin(phase * Math.PI * 2));
            float depth = c.World ? Math.Min(1, c.Depth + .000001f) : c.Depth;
            if (limit == 1) { DrawFrame(mix < .5f ? frame : (frame + 1) % 16, 1); }
            else { DrawFrame(frame, 1 - mix); DrawFrame((frame + 1) % 16, mix); }
            if (count < limit && c.HaloTexture != null && !c.HaloTexture.IsDisposed)
            {
                c.Batch.Draw(c.HaloTexture, c.Position,
                    new Rectangle(0, 0, c.AuraWidth, c.AuraHeight), Tint(c.Style.CoreColor, opacity * .42f),
                    c.Rotation, c.Origin + new Vector2(c.HaloPadding), c.Scale, c.Effects, depth);
                count++;
            }
            return count;

            void DrawFrame(int index, float alpha)
            {
                if (count >= limit || alpha <= .001f) { return; }
                c.Batch.Draw(c.AuraTexture, c.Position,
                    new Rectangle(index % 4 * c.AuraWidth, index / 4 * c.AuraHeight, c.AuraWidth, c.AuraHeight),
                    Tint(color, alpha * opacity), c.Rotation, c.Origin + new Vector2(c.HaloPadding),
                    c.Scale, c.Effects, depth);
                count++;
            }

            Color Tint(Color rgb, float alpha)
            {
                byte a = (byte)Math.Clamp(alpha * 255 + .5f, 0, 255);
                return c.StraightAlpha ? new Color(rgb.R, rgb.G, rgb.B, a)
                    : new Color((byte)(rgb.R * alpha), (byte)(rgb.G * alpha), (byte)(rgb.B * alpha), a);
            }
        }

        private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    }
}
