using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    // Unwrap the original animated ring material onto distance from real item
    // alpha. Baking happens once per cached sprite/material, never per frame.
    internal sealed class SilhouetteAura : IDisposable
    {
        internal Texture2D Straight, Premultiplied;
        internal int Width, Height;
        internal static int Material(string kind) => kind == "flame" ? 16 : kind == "solar" ? 32 : 48;
        internal static long Bytes(int width, int height, int padding)
            => (long)(width + padding * 2) * (height + padding * 2) * 16 * 8;

        internal static SilhouetteAura Create(GraphicsDevice device, byte[] alpha, int width, int height,
            int padding, byte[] material, int materialWidth, int firstTile)
        {
            var result = new SilhouetteAura { Width = width + padding * 2, Height = height + padding * 2 };
            try
            {
                byte[] values = BuildAlpha(alpha, width, height, padding, material, materialWidth, firstTile);
                var colors = new Color[values.Length];
                for (int i = 0; i < values.Length; i++) { colors[i] = new Color((byte)255, (byte)255, (byte)255, values[i]); }
                result.Straight = new Texture2D(device, result.Width * 4, result.Height * 4);
                result.Straight.SetData(colors);
                for (int i = 0; i < values.Length; i++) { byte a = values[i]; colors[i] = new Color(a, a, a, a); }
                result.Premultiplied = new Texture2D(device, result.Width * 4, result.Height * 4);
                result.Premultiplied.SetData(colors);
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal static byte[] BuildAlpha(byte[] alpha, int width, int height, int padding,
            byte[] material, int materialWidth, int firstTile)
        {
            int w = checked(width + padding * 2), h = checked(height + padding * 2);
            int count = checked(w * h);
            var distance = new float[count];
            var nearest = new int[count];
            for (int i = 0; i < count; i++) { distance[i] = float.PositiveInfinity; nearest[i] = -1; }
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y + padding) * w + x + padding;
                if (alpha[y * width + x] >= 80) { distance[i] = 0; nearest[i] = i; }
            }
            // Two-pass chamfer propagation follows concavities and disconnected
            // parts. No angle around the sprite's bounding box defines coverage.
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (x > 0) { Relax(i, i - 1, 1); }
                if (y > 0)
                {
                    Relax(i, i - w, 1);
                    if (x > 0) { Relax(i, i - w - 1, 1.41421356f); }
                    if (x + 1 < w) { Relax(i, i - w + 1, 1.41421356f); }
                }
            }
            for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x;
                if (x + 1 < w) { Relax(i, i + 1, 1); }
                if (y + 1 < h)
                {
                    Relax(i, i + w, 1);
                    if (x > 0) { Relax(i, i + w - 1, 1.41421356f); }
                    if (x + 1 < w) { Relax(i, i + w + 1, 1.41421356f); }
                }
            }
            float reach = Math.Min(padding - 1, Math.Clamp(Math.Min(width, height) * .18f, 4f, 12f));
            var result = new byte[checked(count * 16)];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, near = nearest[i];
                float d = distance[i];
                if (near < 0 || d >= reach || reach <= 0) { continue; }
                float inside = x >= padding && x < width + padding && y >= padding && y < height + padding
                    ? alpha[(y - padding) * width + x - padding] / 255f : 0;
                if (inside >= .999f) { continue; }
                // Longitudinal UV carries the material's curls along the blade;
                // opposite sides see different portions of the original ring.
                int nx = near % w, ny = near / w;
                double angle = (nx * .037 + ny * .029) * Math.PI * 2
                    + Math.Atan2(y - ny, x - nx) * .24;
                float t = d / reach;
                double radius = 33 + t * 23;
                int sx = Math.Clamp((int)Math.Round(63.5 + Math.Cos(angle) * radius), 0, 127);
                int sy = Math.Clamp((int)Math.Round(63.5 + Math.Sin(angle) * radius), 0, 127);
                float fade = (1 - t * t) * (1 - inside);
                for (int frame = 0; frame < 16; frame++)
                {
                    int tile = firstTile + frame;
                    byte value = material[(tile / 4 * 128 + sy) * materialWidth + tile % 4 * 128 + sx];
                    result[(frame / 4 * h + y) * (w * 4) + frame % 4 * w + x]
                        = (byte)Math.Clamp(value * fade * 1.8f, 0, 255);
                }
            }
            return result;

            void Relax(int i, int from, float step)
            {
                float candidate = distance[from] + step;
                if (candidate < distance[i]) { distance[i] = candidate; nearest[i] = nearest[from]; }
            }
        }

        public void Dispose() { Straight?.Dispose(); Premultiplied?.Dispose(); }
    }
}
