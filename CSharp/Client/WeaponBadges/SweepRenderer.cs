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
            internal Vector2[] EdgeAnchors;
            internal Texture2D HaloPremultiplied;
            internal Texture2D HaloStraight;
            internal int HaloPadding;
            internal Vector2[] ContourAnchors;
            internal Vector2[] ContourNormals;
            internal Rectangle? Bounds;
            internal long LastUse;
            internal long Bytes;
            internal Dictionary<int, SilhouetteAura> Auras = new Dictionary<int, SilhouetteAura>();
            public void Dispose()
            {
                Premultiplied?.Dispose(); Straight?.Dispose();
                HaloPremultiplied?.Dispose(); HaloStraight?.Dispose();
                foreach (SilhouetteAura aura in Auras.Values) { aura.Dispose(); }
            }
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
        private int artQuality = 2;
        internal int LastSubmissions { get; private set; }
        internal int SubmissionLimit => artQuality == 0 ? 16 : artQuality == 1 ? 40 : 80;

        internal SweepRenderer(int cacheMegabytes) { budget = (long)cacheMegabytes * 1024 * 1024; }

        internal void Configure(string quality, bool enableSparkles, int frameLimit, int cacheMegabytes)
        {
            artQuality = quality == "low" ? 0 : quality == "balanced" ? 1 : 2;
            maxRows = quality == "low" ? 8 : quality == "balanced" ? 12 : 24;
            segments = quality == "low" ? 3 : quality == "balanced" ? 5 : 7;
            maxBands = quality == "low" ? 1 : 2;
            maxSparkles = quality == "low" ? 0 : quality == "balanced" ? 2 : int.MaxValue;
            sparkles = enableSparkles;
            maxPerFrame = frameLimit;
            budget = (long)cacheMegabytes * 1024 * 1024;
            // GPU resources are still released only after queued draws finish.
        }

        internal bool Draw(ISpriteBatch batch, Sprite sprite, Rectangle source, Vector2 position,
            Vector2 origin, float rotation, Vector2 scale, SpriteEffects effects, float depth,
            Color originalColor, SweepProfile profile, float seed, bool world, float worldIntensity,
            bool straightAlpha = false)
            => DrawLayer(batch, sprite, source, position, origin, rotation, scale, effects, depth,
                originalColor, profile, seed, world, worldIntensity, straightAlpha, 0, false);

        internal bool DrawLayer(ISpriteBatch batch, Sprite sprite, Rectangle source, Vector2 position,
            Vector2 origin, float rotation, Vector2 scale, SpriteEffects effects, float depth,
            Color originalColor, SweepProfile profile, float seed, bool world, float worldIntensity,
            bool straightAlpha, int submissionLimit, bool itemAlreadyCounted)
        {
            LastSubmissions = 0;
            if (profile == null || originalColor.A == 0 || originalColor.R + originalColor.G + originalColor.B < 4 ||
                source.Width < 1 || source.Height < 1 || scale.X == 0 || scale.Y == 0 ||
                (world && worldIntensity <= 0) || (profile.Intensity <= 0 && profile.Glow <= 0 && profile.Pulse <= 0)) { return false; }
            // Check before reading/creating a mask as well as before overlay draws.
            if (!itemAlreadyCounted && maxPerFrame > 0 && drawnThisFrame >= maxPerFrame) { return false; }
            Texture2D sourceTexture = sprite.Texture;
            if (sourceTexture == null || sourceTexture.IsDisposed) { return false; }
            var key = new MaskKey(sourceTexture, source);
            if (failed.Contains(key)) { return false; }
            try
            {
                bool soft = profile.ArtEffect != null && SoftEffectAtlas.AppliesTo(profile.ArtEffect.Kind);
                Mask mask = GetMask(key, sprite.FilePath.Value, soft);
                if (mask == null) { return false; }
                mask.LastUse = time.ElapsedTicks;
                Texture2D texture = world || straightAlpha ? mask.Straight : mask.Premultiplied;
                bool counted = false;
                int submissions = 0;
                double seconds = time.Elapsed.TotalSeconds;
                float phase = (float)(seconds / profile.Period + seed);
                phase -= (float)Math.Floor(phase);
                float opacity = originalColor.A / 255f * (world ? worldIntensity : 1f);
                // World batches sort by depth, so the overlay sits just in front of
                // the item itself, without jumping in front of the wearer's limbs.
                float overlayDepth = world ? Math.Max(0, depth - 0.0000005f) : depth;
                if (profile.ArtEffect != null)
                {
                    Texture2D softTexture = soft ? SoftEffectAtlas.Get(sourceTexture.GraphicsDevice, world || straightAlpha) : null;
                    SilhouetteAura aura = profile.ArtEffect.Presentation == "aura" && soft
                        ? GetAura(mask, key, sprite.FilePath.Value, profile.ArtEffect.Kind) : null;
                    int submitted = ArtEffectRenderer.Draw(new ArtDrawContext
                    {
                        Batch = batch, OriginalTexture = sourceTexture, MaskTexture = texture,
                        PixelTexture = GUI.WhiteTexture, Source = source,
                        Bounds = mask.Bounds ?? new Rectangle(0, 0, source.Width, source.Height),
                        EdgeAnchors = mask.EdgeAnchors,
                        SoftTexture = softTexture,
                        AuraTexture = aura == null ? null : world || straightAlpha ? aura.Straight : aura.Premultiplied,
                        AuraWidth = aura?.Width ?? 0, AuraHeight = aura?.Height ?? 0,
                        HaloTexture = world || straightAlpha ? mask.HaloStraight : mask.HaloPremultiplied,
                        HaloPadding = mask.HaloPadding, ContourAnchors = mask.ContourAnchors, ContourNormals = mask.ContourNormals,
                        Position = position, Origin = origin, Scale = scale, Rotation = rotation,
                        Depth = overlayDepth, Effects = effects, OriginalColor = originalColor,
                        PrimaryColor = profile.Color, Opacity = opacity * profile.Intensity,
                        Seed = seed, Time = (float)seconds, StraightAlpha = world || straightAlpha, World = world,
                        Quality = artQuality, Sparkles = sparkles, Style = profile.ArtEffect, DrawBudget = submissionLimit
                    });
                    LastSubmissions = submitted;
                    if (submitted > 0 && !itemAlreadyCounted) { drawnThisFrame++; }
                    return submitted > 0;
                }
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

                return counted;

                void DrawOverlay(Rectangle slice, Color color, Vector2 sliceOrigin)
                {
                    if (submissionLimit > 0 && submissions >= submissionLimit) { return; }
                    Rectangle visible = Rectangle.Intersect(slice, mask.Bounds ?? new Rectangle(0, 0, source.Width, source.Height));
                    if (color.A == 0 || visible.Width <= 0 || visible.Height <= 0) { return; }
                    batch.Draw(texture, position, slice, color, rotation, sliceOrigin, scale, effects, overlayDepth);
                    LastSubmissions = ++submissions;
                    // One item overlay, regardless of its number of bands/glints.
                    // Fully faded/off-sprite batches leave room for later items.
                    if (!counted)
                    {
                        if (!itemAlreadyCounted) { drawnThisFrame++; }
                        counted = true;
                    }
                }
            }
            catch (Exception e)
            {
                // An unreadable third-party texture must not interrupt the base draw.
                failed.Add(key);
                DebugConsole.AddWarning($"[Touhou.WeaponBadges] 扫光贴图跳过 {sprite.FilePath}: {e.Message}");
                return false;
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
            // Barotrauma XNATypes.Color * float changes alpha only.
            return straight ? new Color(color.R, color.G, color.B, (byte)(a * 255)) :
                new Color((byte)(color.R * a), (byte)(color.G * a), (byte)(color.B * a), (byte)(a * 255));
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

        private Mask GetMask(MaskKey key, string file, bool needsHalo)
        {
            int padding = needsHalo ? SoftHalo.Padding(key.Source.Width, key.Source.Height) : 0;
            long haloBytes = needsHalo ? (long)(key.Source.Width + padding * 2) * (key.Source.Height + padding * 2) * 8 : 0;
            if (masks.TryGetValue(key, out Mask cached))
            {
                if (needsHalo && cached.HaloStraight == null)
                {
                    if (cached.Bytes + haloBytes > budget) { return null; }
                    if (usedBytes + haloBytes > budget) { requestedSpace = Math.Max(requestedSpace, haloBytes); return null; }
                    CreateHalo(cached, key, SpriteAlphaReader.Read(key.Texture, key.Source, file), padding);
                    cached.Bytes += haloBytes;
                    usedBytes += haloBytes;
                }
                return cached;
            }
            long bytes = (long)key.Source.Width * key.Source.Height * 8;
            bytes += haloBytes;
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
            var edgeAnchors = new List<Vector2>(24);
            int sampleCount = Math.Min(24, key.Source.Width);
            for (int sample = 0; sample < sampleCount; sample++)
            {
                int x = Math.Min(key.Source.Width - 1, (int)((sample + .5f) * key.Source.Width / sampleCount));
                for (int y = 0; y < key.Source.Height; y++)
                {
                    if (alpha[y * key.Source.Width + x] < 80) { continue; }
                    edgeAnchors.Add(new Vector2(x, y));
                    break;
                }
            }
            var result = new Mask { Glints = glints.ToArray(), LastUse = time.ElapsedTicks, Bytes = bytes,
                EdgeAnchors = edgeAnchors.ToArray(),
                Bounds = new Rectangle(left, top, right - left, bottom - top) };
            try
            {
                result.Premultiplied = new Texture2D(key.Texture.GraphicsDevice, key.Source.Width, key.Source.Height);
                result.Premultiplied.SetData(premultiplied);
                result.Straight = new Texture2D(key.Texture.GraphicsDevice, key.Source.Width, key.Source.Height);
                result.Straight.SetData(straight);
                if (needsHalo) { CreateHalo(result, key, alpha, padding); }
            }
            catch { result.Dispose(); throw; }
            masks.Add(key, result);
            usedBytes += bytes;
            return result;
        }

        private SilhouetteAura GetAura(Mask mask, MaskKey key, string file, string kind)
        {
            int material = SilhouetteAura.Material(kind);
            if (mask.Auras.TryGetValue(material, out SilhouetteAura cached)) { return cached; }
            long bytes = SilhouetteAura.Bytes(key.Source.Width, key.Source.Height, mask.HaloPadding);
            if (mask.Bytes + bytes > budget || (key.Source.Width + mask.HaloPadding * 2) * 4 > 4096 ||
                (key.Source.Height + mask.HaloPadding * 2) * 4 > 4096) { return null; }
            if (usedBytes + bytes > budget) { requestedSpace = Math.Max(requestedSpace, bytes); return null; }
            SilhouetteAura aura = SilhouetteAura.Create(key.Texture.GraphicsDevice,
                SpriteAlphaReader.Read(key.Texture, key.Source, file), key.Source.Width, key.Source.Height,
                mask.HaloPadding, SoftEffectAtlas.Alpha, 512, material);
            mask.Auras.Add(material, aura);
            usedBytes += bytes; mask.Bytes += bytes;
            return aura;
        }

        private static void CreateHalo(Mask mask, MaskKey key, byte[] alpha, int padding)
        {
            byte[] halo = SoftHalo.BuildAlpha(alpha, key.Source.Width, key.Source.Height, padding);
            int width = key.Source.Width + padding * 2, height = key.Source.Height + padding * 2;
            var colors = new Color[halo.Length];
            Texture2D straight = null, premultiplied = null;
            try
            {
                for (int i = 0; i < colors.Length; i++) { colors[i] = new Color((byte)255, (byte)255, (byte)255, halo[i]); }
                straight = new Texture2D(key.Texture.GraphicsDevice, width, height);
                straight.SetData(colors);
                for (int i = 0; i < colors.Length; i++) { byte a = halo[i]; colors[i] = new Color(a, a, a, a); }
                premultiplied = new Texture2D(key.Texture.GraphicsDevice, width, height);
                premultiplied.SetData(colors);
                SoftHalo.Contour(alpha, key.Source.Width, key.Source.Height, out Vector2[] points, out Vector2[] normals);
                mask.HaloStraight = straight; mask.HaloPremultiplied = premultiplied;
                mask.HaloPadding = padding; mask.ContourAnchors = points; mask.ContourNormals = normals;
            }
            catch { straight?.Dispose(); premultiplied?.Dispose(); throw; }
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
