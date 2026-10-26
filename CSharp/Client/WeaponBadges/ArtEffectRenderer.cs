using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    internal struct ArtDrawContext
    {
        internal ISpriteBatch Batch;
        internal Texture2D OriginalTexture;
        internal Texture2D MaskTexture;
        internal Texture2D PixelTexture;
        internal Rectangle Source;
        internal Rectangle Bounds;
        internal Vector2 Position;
        internal Vector2 Origin;
        internal Vector2 Scale;
        internal float Rotation;
        internal float Depth;
        internal SpriteEffects Effects;
        internal Color OriginalColor;
        internal Color PrimaryColor;
        internal float Opacity;
        internal float Seed;
        internal float Time;
        internal bool StraightAlpha;
        internal bool World;
        internal int DrawBudget;
        internal int Quality;
        internal bool Sparkles;
        internal ArtEffectProfile Style;
        internal Vector2[] EdgeAnchors;
        internal Texture2D SoftTexture;
        internal Texture2D HaloTexture;
        internal Texture2D AuraTexture;
        internal int AuraWidth, AuraHeight;
        internal int HaloPadding;
        internal Vector2[] ContourAnchors;
        internal Vector2[] ContourNormals;
    }

    // All effects are submitted through the batch that drew the item. The local
    // coordinate system is the sprite's source rectangle, after Bounds cropping.
    internal static class ArtEffectRenderer
    {
        private const float Tau = 6.28318530718f;

        internal static int Draw(ArtDrawContext c)
        {
            if (c.Style?.Presentation == "aura") { return UiAuraRenderer.Draw(c); }
            if (c.Style != null && SoftEffectAtlas.AppliesTo(c.Style.Kind)) { return SoftArtRenderer.Draw(c); }
            if (!CanDraw(c)) { return 0; }
            int quality = Math.Clamp(c.Quality, 0, 2);
            int limit = quality == 0 ? 16 : quality == 1 ? 40 : 80;
            if (c.DrawBudget > 0) { limit = Math.Min(limit, c.DrawBudget); }
            float opacity = Math.Clamp(c.Opacity * Clamp(Finite(c.Style.Opacity, .8f), 0f, 1f), 0f, 1f);
            if (opacity <= .0005f) { return 0; }

            var p = new Painter(c, quality, limit, opacity);
            switch (Kind(c.Style.Kind))
            {
                case 3: DrawEcho(ref p); break;
                case 4: DrawPhantom(ref p); break;
                case 5: DrawCyber(ref p); break;
                case 6: DrawBlocks(ref p); break;
            }
            return Math.Min(p.count, limit);
        }

        private static bool CanDraw(ArtDrawContext c)
        {
            if (c.Batch == null || c.Style == null || string.IsNullOrEmpty(c.Style.Kind) ||
                c.Source.Width < 1 || c.Source.Height < 1 || c.Scale.X == 0 || c.Scale.Y == 0 ||
                !float.IsFinite(c.Scale.X) || !float.IsFinite(c.Scale.Y) ||
                !float.IsFinite(c.Position.X) || !float.IsFinite(c.Position.Y) ||
                !float.IsFinite(c.Rotation) || !float.IsFinite(c.Depth) ||
                !float.IsFinite(c.Opacity) || !float.IsFinite(c.Style.Opacity)) { return false; }
            Rectangle visible = Rectangle.Intersect(c.Bounds,
                new Rectangle(0, 0, c.Source.Width, c.Source.Height));
            return visible.Width > 0 && visible.Height > 0;
        }

        private static int Kind(string kind)
        {
            if (string.Equals(kind, "flame", StringComparison.OrdinalIgnoreCase)) { return 1; }
            if (string.Equals(kind, "solar", StringComparison.OrdinalIgnoreCase)) { return 2; }
            if (string.Equals(kind, "echo", StringComparison.OrdinalIgnoreCase)) { return 3; }
            if (string.Equals(kind, "phantom", StringComparison.OrdinalIgnoreCase)) { return 4; }
            if (string.Equals(kind, "cyber", StringComparison.OrdinalIgnoreCase)) { return 5; }
            if (string.Equals(kind, "blocks", StringComparison.OrdinalIgnoreCase)) { return 6; }
            if (string.Equals(kind, "frost", StringComparison.OrdinalIgnoreCase)) { return 7; }
            if (string.Equals(kind, "lightning", StringComparison.OrdinalIgnoreCase)) { return 8; }
            if (string.Equals(kind, "rune", StringComparison.OrdinalIgnoreCase)) { return 9; }
            return 0;
        }

        private static void DrawEcho(ref Painter p)
        {
            int maximum = p.quality == 0 ? 2 : p.quality == 1 ? 3 : 4;
            int count = Math.Min(maximum, Math.Max(2, p.density));
            for (int i = 0; i < count && p.Remaining > 0; i++)
            {
                float pulse = .5f + .5f * (float)Math.Sin(Tau *
                    (p.phase * .72f + i * .19f));
                float angle = Tau * (i / (float)count + p.phase * .42f);
                float radius = p.amplitude * (.4f + .42f * (i + 1f) / count) * (.78f + .22f * pulse)
                    + p.unit * .018f * p.size;
                var offset = new Vector2((float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius);
                Color color = i % 4 == 0 ? Color.Lerp(p.primary, p.accent, .55f)
                    : i % 4 == 1 ? Color.Lerp(p.core, Color.White, .12f)
                    : i % 4 == 2 ? p.accent : Color.Lerp(p.primary, p.core, .55f);
                float alpha = Math.Max(.22f, .42f + .09f * pulse - i * .045f);
                if (!p.Original(p.bounds, color, alpha, offset)) { break; }
            }

            if (p.Remaining > 0)
            {
                float pulse = .5f + .5f * (float)Math.Sin(Tau * (p.phase * .9f + .31f));
                p.Mask(p.bounds, Color.Lerp(p.accent, p.core, .35f), .055f + .04f * pulse,
                    new Vector2(-p.amplitude * .1f, p.amplitude * .06f));
            }
        }

        private static void DrawPhantom(ref Painter p)
        {
            int maximum = p.quality == 0 ? 2 : p.quality == 1 ? 3 : 4;
            int count = Math.Min(maximum, Math.Max(2, p.density));
            for (int i = 0; i < count && p.Remaining > 0; i++)
            {
                float angle = Tau * (i / (float)count + p.phase * .12f);
                float drift = .5f + .5f * (float)Math.Sin(Tau * (p.phase * .21f + i * .29f));
                float radius = p.unit * .016f * p.size
                    + p.amplitude * (.18f + .22f * drift);
                var offset = new Vector2((float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius);
                Color pale = Color.Lerp(p.core, p.accent, i / (float)Math.Max(1, count) * .35f);
                if (!p.Mask(p.bounds, pale, .1f + .05f * drift, offset)) { break; }
            }
        }

        private static void DrawCyber(ref Painter p)
        {
            float split = Math.Max(1f, p.amplitude * 1.15f) + p.unit * .007f;
            int frame = (int)Math.Floor(p.phase * 10f);
            float flicker = .68f + .32f * (float)Math.Sin(Tau * (p.phase * 2.3f + frame * .17f));
            var red = new Color(255, Math.Min((int)p.accent.G, 62), Math.Min((int)p.accent.B, 56));
            var cyan = new Color(Math.Min((int)p.core.R, 48), Math.Min((int)p.core.G, 248), p.core.B);
            p.Original(p.bounds, red, .24f + .08f * flicker, new Vector2(-split, 0f));
            p.Original(p.bounds, cyan, .22f + .08f * flicker, new Vector2(split, 0f));
            p.Original(p.bounds, Color.Lerp(p.primary, p.core, .55f), .1f,
                new Vector2(0f, -split * .34f));

            int maximum = p.quality == 0 ? 4 : p.quality == 1 ? 7 : 10;
            int slices = Math.Min(maximum, Math.Max(1, p.density));
            for (int i = 0; i < slices && p.Remaining > 0; i++)
            {
                float yr = Hash01(i + 17, frame, p.c.Seed);
                float xr = Hash01(i + 23, frame + 3, p.c.Seed);
                float wr = Hash01(i + 37, frame + 7, p.c.Seed);
                float gate = Hash01(i + 43, frame, p.c.Seed);
                int y = p.bounds.Top + Math.Min(p.bounds.Height - 1, (int)(yr * p.bounds.Height));
                int height = Math.Max(1, Math.Min(p.bounds.Bottom - y,
                    1 + p.bounds.Height / Math.Max(10, 22 - p.quality * 5)));
                int x = p.bounds.Left + (int)(xr * p.bounds.Width * .32f);
                int width = Math.Max(1, Math.Min(p.bounds.Right - x,
                    (int)(p.bounds.Width * (.48f + .48f * wr))));
                var slice = new Rectangle(x, y, width, height);
                float shift = split * (Hash01(i + 71, frame, p.c.Seed) * 2f - 1f) * 1.65f;
                Color color = i % 3 == 0 ? cyan : i % 3 == 1 ? red : p.core;
                if (!p.Original(slice, color, .46f + gate * .28f, new Vector2(shift, 0f)))
                { break; }
            }

            int glitches = p.quality == 0 ? 2 : p.quality == 1 ? 4 : 6;
            for (int i = 0; i < glitches && p.Remaining > 0; i++)
            {
                float xr = Hash01(i + 83, frame + 5, p.c.Seed);
                float yr = Hash01(i + 97, frame + 11, p.c.Seed);
                int w = Math.Max(1, (int)(p.bounds.Width * (.035f + .07f * xr)));
                int h = Math.Max(1, (int)(p.bounds.Height * (.02f + .06f * yr)));
                int x = p.bounds.Left + Math.Min(p.bounds.Width - 1, (int)(xr * p.bounds.Width));
                int y = p.bounds.Top + Math.Min(p.bounds.Height - 1, (int)(yr * p.bounds.Height));
                var block = new Rectangle(x, y, Math.Min(w, p.bounds.Right - x),
                    Math.Min(h, p.bounds.Bottom - y));
                float shift = split * (Hash01(i + 103, frame, p.c.Seed) * 2f - 1f) * 1.8f;
                Color color = (i & 1) == 0 ? p.accent : p.core;
                if (!p.Original(block, color, .62f, new Vector2(shift, 0f))) { break; }
            }

            int sparks = p.c.Sparkles ? p.quality == 0 ? 0 : p.quality == 1 ? 1 : 2 : 0;
            for (int i = 0; i < sparks && p.Remaining >= 2; i++)
            {
                float x = p.bounds.Left + Hash01(i + 113, frame, p.c.Seed) * p.bounds.Width;
                float y = p.bounds.Top + Hash01(i + 127, frame, p.c.Seed) * p.bounds.Height;
                p.Sparkle(new Vector2(x, y), Math.Max(.55f, p.unit * .018f),
                    Math.Max(.35f, p.unit * .006f), p.core, .48f);
            }
        }

        private static void DrawBlocks(ref Painter p)
        {
            // True base replacement path: submit the complete grid or nothing.
            // Never spend this budget on decoration before the final cell is sent.
            if (p.c.OriginalTexture == null || p.c.OriginalTexture.IsDisposed) { return; }
            int maximum = p.quality == 0 ? 16 : p.quality == 1 ? 40 : 72;
            int target = Math.Min(maximum, Math.Max(1, p.density));
            target = Math.Min(target, p.bounds.Width * p.bounds.Height);
            Grid(target, p.bounds.Width, p.bounds.Height, p.Remaining, out int cols, out int rows);
            int required = cols * rows;
            if (required <= 0 || required > p.Remaining) { return; }

            float cycle = Fract(p.phase);
            float scatter = p.amplitude > .00001f
                ? Smooth(.03f, .28f, cycle) * (1f - Smooth(.66f, .9f, cycle))
                : 0f;
            float reach = p.amplitude * scatter;
            int drawn = 0;
            for (int row = 0; row < rows; row++)
            {
                int y0 = p.bounds.Top + row * p.bounds.Height / rows;
                int y1 = p.bounds.Top + (row + 1) * p.bounds.Height / rows;
                for (int col = 0; col < cols; col++)
                {
                    int x0 = p.bounds.Left + col * p.bounds.Width / cols;
                    int x1 = p.bounds.Left + (col + 1) * p.bounds.Width / cols;
                    var cell = new Rectangle(x0, y0, x1 - x0, y1 - y0);
                    if (cell.Width <= 0 || cell.Height <= 0) { return; }

                    int vc = p.flipH ? cols - 1 - col : col;
                    int vr = p.flipV ? rows - 1 - row : row;
                    float ah = Hash01(vc + 13, vr + 31, p.c.Seed);
                    float rh = Hash01(vc + 59, vr + 7, p.c.Seed);
                    float angle = Tau * ah;
                    float radius = reach * (.32f + .68f * rh);
                    float wave = (float)Math.Sin(Tau * (p.phase * .7f + vc * .045f + vr * .06f))
                        * reach * .22f;
                    var offset = new Vector2((float)Math.Cos(angle) * radius + wave,
                        (float)Math.Sin(angle) * radius * .72f);
                    // The grid is the replacement base, not a style overlay. Keep
                    // OriginalColor RGB/alpha byte-for-byte; style opacity and the
                    // parent intensity are reserved for decoration after the grid.
                    if (!p.OriginalBase(cell, offset)) { return; }
                    drawn++;
                }
            }
            if (drawn != required) { return; }

            // Optional edge glow is deliberately after the complete grid, so it
            // cannot displace a cell when the quality budget is tight.
            if (p.quality > 0 && p.Remaining >= 4)
            {
                float inset = Math.Max(.5f, p.unit * .012f);
                float arm = Math.Max(1f, p.unit * (.045f + .015f * p.quality));
                float thickness = Math.Max(.4f, p.unit * .007f);
                Color neon = Color.Lerp(p.accent, p.core, .55f);
                float left = p.visualLeft - inset;
                float top = p.visualTop - inset;
                float right = p.visualRight + inset;
                float bottom = p.visualBottom + inset;
                p.PixelLine(new Vector2(left, top), new Vector2(left + arm, top), thickness, neon, .34f);
                p.PixelLine(new Vector2(right - arm, top), new Vector2(right, top), thickness, neon, .34f);
                p.PixelLine(new Vector2(left, bottom), new Vector2(left + arm, bottom), thickness, neon, .34f);
                p.PixelLine(new Vector2(right - arm, bottom), new Vector2(right, bottom), thickness, neon, .34f);
            }
        }

        private struct Painter
        {
            internal readonly ArtDrawContext c;
            internal readonly ArtEffectProfile style;
            internal readonly int quality;
            internal readonly int limit;
            internal readonly int width;
            internal readonly int height;
            internal readonly Rectangle bounds;
            internal readonly int visualLeft;
            internal readonly int visualTop;
            internal readonly int visualRight;
            internal readonly int visualBottom;
            internal readonly bool flipH;
            internal readonly bool flipV;
            internal readonly float unit;
            internal readonly float amplitude;
            internal readonly float size;
            internal readonly float phase;
            internal readonly float opacity;
            internal readonly int density;
            internal readonly Color primary;
            internal readonly Color accent;
            internal readonly Color core;
            internal int count;

            internal Painter(ArtDrawContext context, int quality, int limit, float opacity)
            {
                c = context;
                style = context.Style;
                this.quality = quality;
                this.limit = limit;
                this.opacity = opacity;
                width = context.Source.Width;
                height = context.Source.Height;
                bounds = Rectangle.Intersect(context.Bounds, new Rectangle(0, 0, width, height));
                flipH = (context.Effects & SpriteEffects.FlipHorizontally) != 0;
                flipV = (context.Effects & SpriteEffects.FlipVertically) != 0;
                visualLeft = flipH ? width - bounds.Right : bounds.Left;
                visualRight = flipH ? width - bounds.Left : bounds.Right;
                visualTop = flipV ? height - bounds.Bottom : bounds.Top;
                visualBottom = flipV ? height - bounds.Top : bounds.Bottom;
                unit = Math.Min(width, height);
                size = Math.Clamp(Finite(style.Size, 1f), .15f, 4f);
                density = Math.Clamp(style.Density, 1, 96);
                float amount = Math.Clamp(Finite(style.Amplitude, 0f), 0f, .75f);
                amplitude = amount * unit;
                float speed = Math.Clamp(Finite(style.Speed, 0f), 0f, 12f);
                float seed = Finite(context.Seed, 0f);
                phase = seed + (amplitude > 0f && speed > 0f ? Finite(context.Time, 0f) * speed : 0f);
                count = 0;

                primary = new Color(context.PrimaryColor.R, context.PrimaryColor.G,
                    context.PrimaryColor.B, (byte)255);
                accent = Color.Lerp(primary, new Color(style.AccentColor.R,
                    style.AccentColor.G, style.AccentColor.B, (byte)255), .72f);
                core = Color.Lerp(primary, new Color(style.CoreColor.R,
                    style.CoreColor.G, style.CoreColor.B, (byte)255), .78f);
            }

            internal int Remaining => limit - count;
            internal float visualWidth => bounds.Width;
            internal float visualHeight => bounds.Height;
            internal Vector2 visualCenter => new Vector2(
                (visualLeft + visualRight) * .5f, (visualTop + visualBottom) * .5f);

            internal bool Mask(Rectangle local, Color rgb, float alpha, Vector2 offset)
            {
                if (c.MaskTexture == null) { return false; }
                local = Rectangle.Intersect(local, bounds);
                if (local.Width <= 0 || local.Height <= 0 || count >= limit) { return false; }
                Color color = Tint(rgb, alpha);
                if (color.A == 0) { return false; }
                Vector2 origin = SliceOrigin(c.Origin, width, height, local, c.Effects);
                c.Batch.Draw(c.MaskTexture, c.Position + ScreenOffset(offset), local, color,
                    c.Rotation, origin, c.Scale, c.Effects, c.Depth);
                count++;
                return true;
            }

            internal bool Original(Rectangle local, Color rgb, float alpha, Vector2 offset)
            {
                if (c.OriginalTexture == null) { return false; }
                local = Rectangle.Intersect(local, bounds);
                if (local.Width <= 0 || local.Height <= 0 || count >= limit) { return false; }
                Color color = Tint(rgb, alpha);
                if (color.A == 0) { return false; }
                var source = new Rectangle(c.Source.X + local.X, c.Source.Y + local.Y,
                    local.Width, local.Height);
                Vector2 origin = SliceOrigin(c.Origin, width, height, local, c.Effects);
                c.Batch.Draw(c.OriginalTexture, c.Position + ScreenOffset(offset), source, color,
                    c.Rotation, origin, c.Scale, c.Effects, c.Depth);
                count++;
                return true;
            }

            internal bool OriginalBase(Rectangle local, Vector2 offset)
            {
                if (c.OriginalTexture == null) { return false; }
                local = Rectangle.Intersect(local, bounds);
                if (local.Width <= 0 || local.Height <= 0 || count >= limit) { return false; }
                var source = new Rectangle(c.Source.X + local.X, c.Source.Y + local.Y,
                    local.Width, local.Height);
                Vector2 origin = SliceOrigin(c.Origin, width, height, local, c.Effects);
                c.Batch.Draw(c.OriginalTexture, c.Position + ScreenOffset(offset), source,
                    c.OriginalColor, c.Rotation, origin, c.Scale, c.Effects, c.Depth);
                count++;
                return true;
            }

            internal bool PixelLine(Vector2 start, Vector2 end, float thickness, Color rgb, float alpha)
            {
                if (c.PixelTexture == null || count >= limit) { return false; }
                Vector2 screenStart = MapLocal(start);
                Vector2 delta = MapLocal(end) - screenStart;
                float length = delta.Length();
                if (length < .001f) { return false; }
                Color color = Tint(rgb, alpha);
                if (color.A == 0) { return false; }
                float screenThickness = Math.Max(.3f, thickness
                    * (float)Math.Sqrt(Math.Abs(c.Scale.X * c.Scale.Y)));
                c.Batch.Draw(c.PixelTexture, screenStart, new Rectangle(0, 0, 1, 1), color,
                    (float)Math.Atan2(delta.Y, delta.X), new Vector2(0f, .5f),
                    new Vector2(length, screenThickness), SpriteEffects.None, c.Depth);
                count++;
                return true;
            }

            internal void Sparkle(Vector2 point, float arm, float thickness, Color rgb, float alpha)
            {
                if (Remaining < 2) { return; }
                if (!PixelLine(point - new Vector2(arm, 0f), point + new Vector2(arm, 0f),
                    thickness, rgb, alpha)) { return; }
                PixelLine(point - new Vector2(0f, arm), point + new Vector2(0f, arm),
                    thickness, rgb, alpha * .78f);
            }

            private Color Tint(Color rgb, float alpha)
            {
                float a = Math.Clamp(alpha * opacity, 0f, 1f);
                if (a <= .0005f) { return new Color(0, 0, 0, 0); }
                if (c.StraightAlpha)
                {
                    return new Color(rgb.R, rgb.G, rgb.B, (byte)(a * 255f + .5f));
                }
                // XNATypes.Color * float scales alpha only, unlike stock XNA.
                return new Color((byte)(rgb.R * a + .5f), (byte)(rgb.G * a + .5f),
                    (byte)(rgb.B * a + .5f), (byte)(a * 255f + .5f));
            }

            private Vector2 MapLocal(Vector2 local)
            {
                float x = (local.X - c.Origin.X) * c.Scale.X;
                float y = (local.Y - c.Origin.Y) * c.Scale.Y;
                float cs = (float)Math.Cos(c.Rotation);
                float sn = (float)Math.Sin(c.Rotation);
                return c.Position + new Vector2(x * cs - y * sn, x * sn + y * cs);
            }

            private Vector2 ScreenOffset(Vector2 local)
            {
                float x = local.X * c.Scale.X;
                float y = local.Y * c.Scale.Y;
                float cs = (float)Math.Cos(c.Rotation);
                float sn = (float)Math.Sin(c.Rotation);
                return new Vector2(x * cs - y * sn, x * sn + y * cs);
            }
        }

        private static void Grid(int target, int width, int height, int limit,
            out int columns, out int rows)
        {
            target = Math.Clamp(target, 1, Math.Min(limit, width * height));
            float aspect = Math.Max(.05f, width / (float)height);
            int guess = (int)Math.Round(Math.Sqrt(target * aspect));
            columns = Math.Clamp(guess, 1, width);
            rows = Math.Clamp(target / columns, 1, height);
            int bestTotal = columns * rows;
            int bestDifference = Math.Abs(columns - rows);
            int from = Math.Max(1, columns - 3);
            int to = Math.Min(width, target);
            for (int candidate = from; candidate <= to; candidate++)
            {
                int candidateRows = Math.Min(height, target / candidate);
                if (candidateRows < 1) { continue; }
                int total = candidate * candidateRows;
                if (total > limit) { continue; }
                int difference = Math.Abs(candidate - candidateRows);
                if (total > bestTotal || (total == bestTotal && difference < bestDifference))
                {
                    columns = candidate;
                    rows = candidateRows;
                    bestTotal = total;
                    bestDifference = difference;
                }
            }
        }

        private static Vector2 SliceOrigin(Vector2 origin, int width, int height,
            Rectangle slice, SpriteEffects effects)
        {
            int x = (effects & SpriteEffects.FlipHorizontally) != 0 ? width - slice.Right : slice.X;
            int y = (effects & SpriteEffects.FlipVertically) != 0 ? height - slice.Bottom : slice.Y;
            return origin - new Vector2(x, y);
        }

        private static float Smooth(float edge0, float edge1, float value)
        {
            float t = Math.Clamp((value - edge0) / Math.Max(.00001f, edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static float Fract(float value) => value - (float)Math.Floor(value);

        private static float Hash01(int a, int b, float seed)
        {
            unchecked
            {
                uint hash = (uint)a * 2654435761u;
                hash ^= (uint)b * 2246822519u;
                hash ^= (uint)(seed * 16777216f);
                hash ^= hash >> 16;
                hash *= 2146121005u;
                hash ^= hash >> 15;
                hash *= 32768u;
                hash ^= hash >> 16;
                return (hash & 0x00FFFFFFu) / 16777216f;
            }
        }

        private static float Finite(float value, float fallback)
            => float.IsFinite(value) ? value : fallback;

        private static float Clamp(float value, float min, float max)
            => Math.Clamp(value, min, max);
    }
}
