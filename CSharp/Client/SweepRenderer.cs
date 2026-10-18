using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    // Draws through the existing batch: no End/Begin, render-target switches or
    // replacement shader that could disturb the game's lighting and UI clipping.
    internal sealed class SweepRenderer : IDisposable
    {
        private readonly struct MaskKey : IEquatable<MaskKey>
        {
            internal readonly Texture2D Texture;
            internal readonly Rectangle Source;
            internal MaskKey(Texture2D texture, Rectangle source) { Texture = texture; Source = source; }
            public bool Equals(MaskKey other) => ReferenceEquals(Texture, other.Texture) && Source == other.Source;
            public override bool Equals(object obj) => obj is MaskKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Texture, Source);
        }

        private sealed class Mask : IDisposable
        {
            internal Texture2D Premultiplied;
            internal Texture2D Straight;
            internal Vector2[] Glints;
            internal Rectangle? Bounds;
            internal long LastUse;
            internal long Bytes;
            public void Dispose() { Premultiplied?.Dispose(); Straight?.Dispose(); }
        }

        private readonly Dictionary<MaskKey, Mask> masks = new Dictionary<MaskKey, Mask>();
        private readonly HashSet<MaskKey> failed = new HashSet<MaskKey>();
        private readonly Stopwatch time = Stopwatch.StartNew();
        private long budget;
        private long usedBytes;
        private long requestedSpace;
        private int maxRows = 24, segments = 7, maxBands = 2, maxSparkles = int.MaxValue;
        private bool sparkles = true;
        private int maxPerFrame = 128, drawnThisFrame;

        internal SweepRenderer(int cacheMegabytes) { budget = (long)cacheMegabytes * 1024 * 1024; }

        internal void Configure(string quality, bool enableSparkles, int frameLimit, int cacheMegabytes)
        {
            maxRows = quality == "low" ? 8 : quality == "balanced" ? 12 : 24;
            segments = quality == "low" ? 3 : quality == "balanced" ? 5 : 7;
            maxBands = quality == "low" ? 1 : 2;
            maxSparkles = quality == "low" ? 0 : quality == "balanced" ? 2 : int.MaxValue;
            sparkles = enableSparkles;
            maxPerFrame = frameLimit;
            budget = (long)cacheMegabytes * 1024 * 1024;
            // GPU resources are still released only after queued draws finish.
        }

        internal void Draw(ISpriteBatch batch, Sprite sprite, Rectangle source, Vector2 position,
            Vector2 origin, float rotation, Vector2 scale, SpriteEffects effects, float depth,
            Color originalColor, SweepProfile profile, float seed, bool world, float worldIntensity,
            bool straightAlpha = false)
        {
            if (profile == null || originalColor.A == 0 || originalColor.R + originalColor.G + originalColor.B < 4 ||
                source.Width < 1 || source.Height < 1 || scale.X == 0 || scale.Y == 0 ||
                (world && worldIntensity <= 0) || (profile.Intensity <= 0 && profile.Glow <= 0 && profile.Pulse <= 0)) { return; }
            // Check before reading/creating a mask as well as before overlay draws.
            if (maxPerFrame > 0 && drawnThisFrame >= maxPerFrame) { return; }
            Texture2D sourceTexture = sprite.Texture;
            if (sourceTexture == null || sourceTexture.IsDisposed) { return; }
            var key = new MaskKey(sourceTexture, source);
            if (failed.Contains(key)) { return; }
            try
            {
                Mask mask = GetMask(key, sprite.FilePath.Value);
                if (mask == null) { return; }
                mask.LastUse = time.ElapsedTicks;
                Texture2D texture = world || straightAlpha ? mask.Straight : mask.Premultiplied;
                bool counted = false;
                double seconds = time.Elapsed.TotalSeconds;
                float phase = (float)(seconds / profile.Period + seed);
                phase -= (float)Math.Floor(phase);
                float opacity = originalColor.A / 255f * (world ? worldIntensity : 1f);
                // World batches sort by depth, so the overlay sits just in front of
                // the item itself, without jumping in front of the wearer's limbs.
                float overlayDepth = world ? Math.Max(0, depth - 0.0000005f) : depth;
                Color tint = FlowColor(profile, (float)(seconds * 0.18 + seed));
                float glow = profile.Glow + profile.Pulse * (0.5f + 0.5f * (float)Math.Sin(phase * Math.PI * 2));
                if (glow > 0.001f)
                {
                    DrawOverlay(new Rectangle(0, 0, source.Width, source.Height), Tint(tint, glow * opacity, world || straightAlpha), origin);
                }

                int rows = Math.Min(source.Height, Math.Clamp((int)Math.Ceiling(source.Height * Math.Abs(scale.Y) / 3), 4, maxRows));
                for (int band = 0; band < Math.Min(profile.Bands, maxBands); band++)
                {
                    float bandPhase = phase + band * 0.5f;
                    bandPhase -= (float)Math.Floor(bandPhase);
                    for (int row = 0; row < rows; row++)
                    {
                        Color rowTint = FlowColor(profile, (float)(seconds * 0.18 + seed + row * 0.012));
                        rowTint = Color.Lerp(rowTint, Color.White, 0.32f);
                        for (int segment = 0; segment < segments; segment++)
                        {
                            if (!TryBandSlice(source.Width, source.Height, row, rows, segment, segments,
                                bandPhase, profile.Width, profile.Slant, out Rectangle slice, out float weight)) { continue; }
                            float strength = profile.Intensity * opacity * weight * (band == 0 ? 1f : 0.65f);
                            DrawOverlay(slice, Tint(rowTint, strength, world || straightAlpha),
                                SliceOrigin(origin, source.Width, source.Height, slice, effects));
                        }
                    }
                }

                if (sparkles && maxSparkles > 0 && profile.Sparkles > 0 && mask.Glints.Length > 0)
                {
                    for (int i = 0; i < Math.Min(profile.Sparkles, maxSparkles); i++)
                    {
                        double pulse = Math.Max(0, Math.Sin(seconds * (2.3 + i * 0.19) + seed * 17 + i * 2.7));
                        float strength = (float)Math.Pow(pulse, 12) * profile.Intensity * opacity;
                        if (strength < 0.025f) { continue; }
                        Vector2 point = mask.Glints[(i * 7 + (int)(seed * 31)) % mask.Glints.Length];
                        int thickness = Math.Max(1, Math.Min(source.Width, source.Height) / 48);
                        int radius = Math.Max(2, Math.Min(source.Width, source.Height) / 14);
                        Color sparkleColor = Color.Lerp(tint, Color.White, 0.75f);
                        DrawGlint(new Rectangle((int)point.X - radius, (int)point.Y - thickness / 2,
                            radius * 2 + 1, thickness), strength);
                        DrawGlint(new Rectangle((int)point.X - thickness / 2, (int)point.Y - radius,
                            thickness, radius * 2 + 1), strength * 0.8f);

                        void DrawGlint(Rectangle rect, float intensity)
                        {
                            Rectangle slice = Rectangle.Intersect(rect, new Rectangle(0, 0, source.Width, source.Height));
                            if (slice.Width <= 0 || slice.Height <= 0) { return; }
                            DrawOverlay(slice, Tint(sparkleColor, intensity, world || straightAlpha),
                                SliceOrigin(origin, source.Width, source.Height, slice, effects));
                        }
                    }
                }

                void DrawOverlay(Rectangle slice, Color color, Vector2 sliceOrigin)
                {
                    Rectangle visible = Rectangle.Intersect(slice, mask.Bounds ?? new Rectangle(0, 0, source.Width, source.Height));
                    if (color.A == 0 || visible.Width <= 0 || visible.Height <= 0) { return; }
                    batch.Draw(texture, position, slice, color, rotation, sliceOrigin, scale, effects, overlayDepth);
                    // One item overlay, regardless of its number of bands/glints.
                    // Fully faded/off-sprite batches leave room for later items.
                    if (!counted) { drawnThisFrame++; counted = true; }
                }
            }
            catch (Exception e)
            {
                // An unreadable third-party texture must not interrupt the base draw.
                failed.Add(key);
                DebugConsole.AddWarning($"[Touhou.WeaponBadges] 扫光贴图跳过 {sprite.FilePath}: {e.Message}");
            }
        }

        internal static bool TryBandSlice(int width, int height, int row, int rows, int segment, int segments,
            float phase, float bandWidth, float slant, out Rectangle slice, out float weight)
        {
            int y0 = row * height / rows;
            int y1 = (row + 1) * height / rows;
            float edge = Math.Abs(slant) * 0.5f + bandWidth;
            float center = -edge + phase * (1 + edge * 2) - slant * ((y0 + y1) * 0.5f / height - 0.5f);
            float left = (center - bandWidth * 0.5f) * width;
            float span = bandWidth * width;
            int x0 = Math.Clamp((int)Math.Round(left + span * segment / segments), 0, width);
            int x1 = Math.Clamp((int)Math.Round(left + span * (segment + 1) / segments), 0, width);
            slice = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            float distance = ((segment + 0.5f) / segments - 0.5f) * 2;
            weight = (float)Math.Exp(-distance * distance * 3.5);
            return slice.Width > 0 && slice.Height > 0;
        }

        internal static Vector2 SliceOrigin(Vector2 origin, int width, int height, Rectangle slice, SpriteEffects effects)
        {
            int x = (effects & SpriteEffects.FlipHorizontally) != 0 ? width - slice.Right : slice.X;
            int y = (effects & SpriteEffects.FlipVertically) != 0 ? height - slice.Bottom : slice.Y;
            return origin - new Vector2(x, y);
        }

        private static Color Tint(Color color, float alpha, bool straight)
        {
            float a = Math.Clamp(alpha, 0, 1);
            return straight ? new Color(color.R, color.G, color.B, (byte)(a * 255)) : color * a;
        }

        private static Color FlowColor(SweepProfile profile, float hue)
        {
            if (profile.Rainbow <= 0) { return profile.Color; }
            hue -= (float)Math.Floor(hue);
            float h = hue * 6;
            float f = h - (float)Math.Floor(h);
            Color rainbow;
            switch ((int)h)
            {
                case 0: rainbow = new Color(1f, f, 0.12f); break;
                case 1: rainbow = new Color(1 - f, 1f, 0.12f); break;
                case 2: rainbow = new Color(0.12f, 1f, f); break;
                case 3: rainbow = new Color(0.12f, 1 - f, 1f); break;
                case 4: rainbow = new Color(f, 0.12f, 1f); break;
                default: rainbow = new Color(1f, 0.12f, 1 - f); break;
            }
            return Color.Lerp(profile.Color, rainbow, profile.Rainbow);
        }

        private Mask GetMask(MaskKey key, string file)
        {
            if (masks.TryGetValue(key, out Mask cached)) { return cached; }
            long bytes = (long)key.Source.Width * key.Source.Height * 8;
            // Oversize is temporary: increasing the live budget must allow a retry.
            if (bytes > budget) { return null; }
            if (usedBytes + bytes > budget)
            {
                // Texture draws may still be queued in a deferred SpriteBatch.
                // Evict after GameMain.Draw completes, then retry next frame.
                requestedSpace = Math.Max(requestedSpace, bytes);
                return null;
            }
            byte[] alpha = SpriteAlphaReader.Read(key.Texture, key.Source, file);
            var premultiplied = new Color[alpha.Length];
            var straight = new Color[alpha.Length];
            var glints = new List<Vector2>();
            int seen = 0;
            int left = key.Source.Width, top = key.Source.Height, right = 0, bottom = 0;
            for (int i = 0; i < alpha.Length; i++)
            {
                byte a = alpha[i];
                premultiplied[i] = new Color(a, a, a, a);
                straight[i] = new Color((byte)255, (byte)255, (byte)255, a);
                if (a > 0)
                {
                    int x = i % key.Source.Width, y = i / key.Source.Width;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1);
                }
                if (a < 160) { continue; }
                seen++;
                var point = new Vector2(i % key.Source.Width, i / key.Source.Width);
                if (glints.Count < 32) { glints.Add(point); }
                else
                {
                    uint hash = unchecked((uint)i * 2654435761u);
                    int replace = (int)(hash % (uint)seen);
                    if (replace < glints.Count) { glints[replace] = point; }
                }
            }
            if (right <= left || bottom <= top) { failed.Add(key); return null; }
            var result = new Mask { Glints = glints.ToArray(), LastUse = time.ElapsedTicks, Bytes = bytes,
                Bounds = new Rectangle(left, top, right - left, bottom - top) };
            try
            {
                result.Premultiplied = new Texture2D(key.Texture.GraphicsDevice, key.Source.Width, key.Source.Height);
                result.Premultiplied.SetData(premultiplied);
                result.Straight = new Texture2D(key.Texture.GraphicsDevice, key.Source.Width, key.Source.Height);
                result.Straight.SetData(straight);
            }
            catch { result.Dispose(); throw; }
            masks.Add(key, result);
            usedBytes += bytes;
            return result;
        }

        internal void EndFrame()
        {
            drawnThisFrame = 0;
            if (requestedSpace > budget) { requestedSpace = 0; }
            while (usedBytes + requestedSpace > budget && masks.Count > 0)
            {
                var oldest = masks.OrderBy(pair => pair.Value.LastUse).First();
                oldest.Value.Dispose();
                usedBytes -= oldest.Value.Bytes;
                masks.Remove(oldest.Key);
            }
            requestedSpace = 0;
        }

        public void Dispose()
        {
            foreach (Mask mask in masks.Values) { mask.Dispose(); }
            masks.Clear();
            failed.Clear();
            usedBytes = 0;
            requestedSpace = 0;
            drawnThisFrame = 0;
            time.Stop();
        }
    }
}
