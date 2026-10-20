using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Touhou.WeaponBadges
{
    internal static class SoftHalo
    {
        internal static int Padding(int width, int height) => Math.Clamp(Math.Min(width, height) / 3, 4, 48);

        // Three separable box passes approximate a Gaussian. Computed only when a sprite enters the cache.
        internal static byte[] BuildAlpha(byte[] alpha, int width, int height, int padding)
        {
            int w = checked(width + padding * 2), h = checked(height + padding * 2);
            var values = new float[checked(w * h)];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) { values[(y + padding) * w + x + padding] = alpha[y * width + x]; }
            var scratch = new float[values.Length];
            // Thin or diagonal weapons can occupy a large atlas rectangle.
            // Estimate local thickness from occupied area / boundary length so
            // their glow hugs the blade instead of following its bounding box.
            int area = 0, perimeter = 0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (alpha[y * width + x] < 80) { continue; }
                area++;
                if (x == 0 || alpha[y * width + x - 1] < 80) { perimeter++; }
                if (x == width - 1 || alpha[y * width + x + 1] < 80) { perimeter++; }
                if (y == 0 || alpha[(y - 1) * width + x] < 80) { perimeter++; }
                if (y == height - 1 || alpha[(y + 1) * width + x] < 80) { perimeter++; }
            }
            float thickness = perimeter > 0 ? 2f * area / perimeter : 1f;
            int radius = Math.Clamp((int)Math.Round(thickness * .16f), 1, 3);
            for (int pass = 0; pass < 3; pass++)
            {
                Blur(values, scratch, w, h, radius, horizontal: true);
                Blur(scratch, values, w, h, radius, horizontal: false);
            }
            var result = new byte[values.Length];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float inside = x >= padding && x < padding + width && y >= padding && y < padding + height
                    ? alpha[(y - padding) * width + x - padding] / 255f : 0;
                result[y * w + x] = (byte)Math.Clamp(values[y * w + x] * 1.65f * (1 - inside * .92f), 0, 255);
            }
            return result;
        }

        private static void Blur(float[] source, float[] target, int width, int height, int radius, bool horizontal)
        {
            int lines = horizontal ? height : width, length = horizontal ? width : height;
            int step = horizontal ? 1 : width;
            int window = radius * 2 + 1;
            for (int line = 0; line < lines; line++)
            {
                int start = horizontal ? line * width : line;
                float sum = 0;
                for (int i = 0; i <= radius && i < length; i++) { sum += source[start + i * step]; }
                for (int i = 0; i < length; i++)
                {
                    target[start + i * step] = sum / window;
                    int remove = i - radius, add = i + radius + 1;
                    if (remove >= 0) { sum -= source[start + remove * step]; }
                    if (add < length) { sum += source[start + add * step]; }
                }
            }
        }

        internal static void Contour(byte[] alpha, int width, int height, out Vector2[] anchors, out Vector2[] normals)
        {
            var points = new List<Vector2>(64);
            var directions = new List<Vector2>(64);
            int columns = Math.Min(16, width), rows = Math.Min(16, height);
            for (int sample = 0; sample < columns; sample++)
            {
                int x = Math.Min(width - 1, (int)((sample + .5f) * width / columns));
                for (int y = 0; y < height; y++)
                { if (alpha[y * width + x] >= 80) { points.Add(new Vector2(x, y)); directions.Add(new Vector2(0, -1)); break; } }
                for (int y = height - 1; y >= 0; y--)
                { if (alpha[y * width + x] >= 80) { points.Add(new Vector2(x, y)); directions.Add(new Vector2(0, 1)); break; } }
            }
            for (int sample = 0; sample < rows; sample++)
            {
                int y = Math.Min(height - 1, (int)((sample + .5f) * height / rows));
                for (int x = 0; x < width; x++)
                { if (alpha[y * width + x] >= 80) { points.Add(new Vector2(x, y)); directions.Add(new Vector2(-1, 0)); break; } }
                for (int x = width - 1; x >= 0; x--)
                { if (alpha[y * width + x] >= 80) { points.Add(new Vector2(x, y)); directions.Add(new Vector2(1, 0)); break; } }
            }
            // Sort by angle so evenly spaced samples cover the entire silhouette.
            var order = new List<int>(points.Count);
            for (int i = 0; i < points.Count; i++) { order.Add(i); }
            order.Sort((a, b) => Math.Atan2(points[a].Y - height * .5, points[a].X - width * .5)
                .CompareTo(Math.Atan2(points[b].Y - height * .5, points[b].X - width * .5)));
            anchors = new Vector2[points.Count]; normals = new Vector2[points.Count];
            for (int i = 0; i < order.Count; i++) { anchors[i] = points[order[i]]; normals[i] = directions[order[i]]; }
        }
    }
}
