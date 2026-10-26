using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static Touhou.WeaponBadges.SoftEffectAtlas;

namespace Touhou.WeaponBadges
{
    // Item enchantments sample the caller's narrow, alpha-derived halo. No
    // bounding-box aura, silhouette expansion, full rings or displaced base art.
    internal static class SoftArtRenderer
    {
        private const float Tau = (float)(Math.PI * 2);
        private static readonly Vector2 CenterPivot = new Vector2(64, 64);
        private static readonly Vector2 BoltPivot = new Vector2(12, 64);

        internal static int Draw(ArtDrawContext c)
        {
            if (c.Batch == null || c.Style == null || c.Source.Width < 1 || c.Source.Height < 1 ||
                !Finite(c.Position) || !Finite(c.Origin) || !Finite(c.Scale) ||
                c.Scale.X == 0 || c.Scale.Y == 0 || !float.IsFinite(c.Rotation) ||
                !float.IsFinite(c.Depth) || !float.IsFinite(c.Opacity) || c.Opacity <= 0 ||
                !float.IsFinite(c.Style.Opacity) || c.Style.Opacity <= 0) { return 0; }
            Rectangle bounds = Rectangle.Intersect(c.Bounds, new Rectangle(0, 0, c.Source.Width, c.Source.Height));
            if (bounds.Width < 1 || bounds.Height < 1) { return 0; }
            var p = new Painter(c, bounds);
            if (c.Style.Presentation == "contour" || c.Style.Presentation == "aura") { p.ContourField(); }
            switch (c.Style.Kind)
            {
                case "flame":
                    p.Halo(p.Accent, .2f);
                    p.Trace(.19f, .85f, p.Core, .46f, 0);
                    // A cooler/lower-temperature secondary edge moves against the
                    // bright trace. Both are cropped from the real halo alpha.
                    if (p.Quality > 0) { p.Trace(.14f, -.48f, p.Primary, .22f, .41f); }
                    if (p.Sparkles && p.Quality > 0) { p.Glint(.67f, p.Accent, .26f, false); }
                    break;
                case "solar":
                    p.Halo(p.Primary, .16f);
                    p.Trace(.24f, .29f, Color.Lerp(p.Core, p.Primary, .25f), .5f, .13f);
                    if (p.Sparkles) { p.Glint(.29f, p.Core, .33f, true); }
                    break;
                case "frost":
                    p.Halo(p.Accent, .17f);
                    p.Trace(.22f, -.19f, p.Core, .25f, .24f);
                    Frost(ref p);
                    break;
                case "lightning":
                    p.Halo(p.Accent, .12f);
                    p.Trace(.13f, .73f, p.Primary, .24f, .61f);
                    Lightning(ref p);
                    break;
                case "rune":
                    p.Halo(p.Primary, .13f);
                    p.Trace(.17f, .24f, p.Accent, .3f, .08f);
                    Rune(ref p);
                    break;
            }
            return p.Count;
        }

        private static void Frost(ref Painter p)
        {
            if (!p.ShowDetails) { return; }
            int count = p.Items(1, 2, 3);
            for (int i = 0; i < count && p.Remaining >= 2; i++)
            {
                if (!p.ContourPos((i + .37f) / count, out Vector2 root, out Vector2 normal)) { continue; }
                float angle = (float)Math.Atan2(normal.Y, normal.X) + (float)Math.PI / 2;
                float pixels = p.DetailPixels * (.66f + .12f * Noise(i, 17, p.Seed));
                p.Decal(Crystal, root, new Vector2(.7f, 1f), pixels,
                    angle, CenterPivot, p.Accent, .28f);
                p.Decal(Crystal, root, new Vector2(.4f, .8f), pixels,
                    angle, CenterPivot, p.Core, .4f);
            }
        }

        private static void Lightning(ref Painter p)
        {
            if (!p.ShowDetails) { return; }
            int count = p.Items(1, 2, 3);
            for (int i = 0; i < count && p.Remaining >= 4; i++)
            {
                float h = Noise(i, 41, p.Seed);
                float u = (i + .21f) / count + p.Phase * .08f;
                if (!p.ContourPos(u, out Vector2 a, out Vector2 na) ||
                    !p.ContourPos(u + p.NeighborStep, out Vector2 b, out Vector2 nb)) { continue; }
                // Adjacent cached contour points only: never bridge opposite
                // sides of a narrow blade or float around its bounding box.
                Vector2 delta = b - a;
                float pixels = p.ScreenLength(delta);
                if (pixels < .6f || Vector2.Dot(na, nb) < -.15f) { continue; }
                if (pixels > p.DetailPixels) { b = a + delta * (p.DetailPixels / pixels); }
                a += p.PixelOffset(na, .2f);
                b += p.PixelOffset(nb, .2f);
                delta = b - a;
                float angle = (float)Math.Atan2(delta.Y, delta.X);
                float height = p.LocalPixels(p.World ? 2.2f : 1.25f);
                Vector2 size = new Vector2(delta.Length() * 128f / 104f, height);
                float flash = ArcFlash(p.Phase, h);
                p.Animated(Bolt0, BoltFrames, p.Phase * .8f + h, a, size, angle,
                    BoltPivot, p.Accent, .18f + .23f * flash);
                p.Animated(Bolt0, BoltFrames, p.Phase * .8f + h, a,
                    size * new Vector2(1f, .55f), angle, BoltPivot,
                    p.Core, .22f + .4f * flash);
            }
        }

        private static void Rune(ref Painter p)
        {
            if (!p.ShowDetails) { return; }
            int count = p.Items(1, 2, 3);
            for (int i = 0; i < count && p.Remaining > 0; i++)
            {
                float u = (i + .31f) / count;
                if (!p.ContourPos(u, out Vector2 root, out Vector2 normal)) { continue; }
                float pulse = .75f + .25f * p.Wave(.32f, i * .23f);
                float angle = (float)Math.Atan2(normal.Y, normal.X);
                // Glyph is a single vertical engraving (36x70 within the tile).
                p.Decal(Glyph, root, new Vector2(128f / 70f),
                    p.DetailPixels * .85f, angle, CenterPivot, p.Core, .38f * pulse);
            }
            // This travelling point is part of the engraving's reading rhythm.
            p.Glint(.17f, p.Accent, .3f, false);
        }

        private struct Painter
        {
            private readonly ArtDrawContext c;
            private readonly Rectangle bounds, haloRect;
            private readonly float opacity, cos, sin, screenScale, detailFade;
            private readonly bool flipH, flipV;
            private readonly int limit;
            internal readonly int Quality;
            internal readonly float Phase, Seed, Size, Motion, DetailPixels;
            internal int Count;
            internal int Remaining => limit - Count;
            internal bool World => c.World;
            internal bool Sparkles => c.Sparkles;
            internal bool ShowDetails => detailFade > .08f;
            internal readonly Color Primary, Accent, Core;

            internal Painter(ArtDrawContext c, Rectangle bounds)
            {
                this.c = c;
                this.bounds = bounds;
                Quality = Math.Clamp(c.Quality, 0, 2);
                limit = Quality == 0 ? 16 : Quality == 1 ? 40 : 80;
                if (c.DrawBudget > 0) { limit = Math.Min(limit, c.DrawBudget); }
                opacity = Math.Clamp(c.Opacity * Math.Clamp(c.Style.Opacity, 0, 1), 0, 1);
                screenScale = Math.Max(Math.Abs(c.Scale.X), Math.Abs(c.Scale.Y));
                float itemMin = Math.Min(bounds.Width * Math.Abs(c.Scale.X), bounds.Height * Math.Abs(c.Scale.Y));
                float itemMax = Math.Max(bounds.Width * Math.Abs(c.Scale.X), bounds.Height * Math.Abs(c.Scale.Y));
                detailFade = Math.Clamp(itemMin / 4f, 0, 1) * Math.Clamp(itemMax / 12f, 0, 1);
                DetailPixels = Math.Min(c.World ? 6f : 3f, Math.Max(.5f, itemMin * .55f));
                Size = Math.Clamp(Number(c.Style.Size, 1), .8f, 1.2f);
                Motion = Math.Clamp(Number(c.Style.Amplitude, 0) / .3f, 0, 1);
                Seed = Number(c.Seed, 0);
                float speed = Math.Clamp(Number(c.Style.Speed, 0), 0, 12);
                Phase = (float)((double)Number(c.Time, 0) * speed % 4096) + Seed;
                flipH = (c.Effects & SpriteEffects.FlipHorizontally) != 0;
                flipV = (c.Effects & SpriteEffects.FlipVertically) != 0;
                cos = (float)Math.Cos(c.Rotation);
                sin = (float)Math.Sin(c.Rotation);
                int padding = Math.Max(0, c.HaloPadding);
                haloRect = new Rectangle(0, 0, c.Source.Width + padding * 2, c.Source.Height + padding * 2);
                Primary = Rgb(c.PrimaryColor);
                Accent = Color.Lerp(Primary, Rgb(c.Style.AccentColor), .78f);
                Core = Color.Lerp(Primary, Rgb(c.Style.CoreColor), .86f);
                Count = 0;
            }

            internal int Items(int low, int mid, int high)
                => Math.Min(Quality == 0 ? low : Quality == 1 ? mid : high,
                    Math.Clamp(c.Style.Density, 1, 96));
            internal float Wave(float frequency, float offset)
                => (float)Math.Sin(Tau * (Phase * frequency + offset));
            internal float LocalPixels(float pixels) => pixels / screenScale;
            internal float ScreenLength(Vector2 local) => (local * c.Scale).Length();
            internal Vector2 PixelOffset(Vector2 direction, float pixels)
            {
                float length = ScreenLength(direction);
                return length > .000001f ? direction * (pixels / length) : Vector2.Zero;
            }

            internal bool Halo(Color color, float alpha) => HaloStrip(haloRect, color, alpha);

            internal void ContourField()
            {
                // Shift copies of the actual alpha halo by a few display pixels.
                // This broadens the weapon's own silhouette, including concavities;
                // it never constructs a ring from the sprite's bounding rectangle.
                float phase = Phase * .37f;
                float radius = LocalPixels(c.World ? 1.65f : c.Style.Presentation == "aura" ? 1.75f : .9f)
                    * Size * (1f + .12f * Motion * Wave(.31f, 0));
                Color shade = Color.Lerp(Primary, Accent, .5f + .2f * Wave(.23f, .1f));
                int lobes = Quality == 0 ? 3 : 4;
                for (int i = 0; i < lobes && Remaining > 1; i++)
                {
                    float angle = phase + Tau * i / lobes;
                    Vector2 local = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * radius;
                    HaloOffset(shade, .24f, local);
                }
                Halo(Accent, .58f + .08f * Wave(.35f, 0));
                Halo(Core, .2f);
            }

            private void HaloOffset(Color color, float alpha, Vector2 offset)
            {
                if (!Usable(c.HaloTexture) || Remaining <= 0) { return; }
                Color tint = Tint(color, alpha);
                if (tint.A == 0) { return; }
                Vector2 scaled = offset * c.Scale;
                Vector2 position = c.Position + new Vector2(scaled.X * cos - scaled.Y * sin,
                    scaled.X * sin + scaled.Y * cos);
                c.Batch.Draw(c.HaloTexture, position, haloRect, tint, c.Rotation,
                    c.Origin + new Vector2(Math.Max(0, c.HaloPadding)), c.Scale, c.Effects, c.Depth);
                Count++;
            }

            internal bool HaloStrip(Rectangle slice, Color color, float alpha)
            {
                if (!Usable(c.HaloTexture) || Remaining <= 0) { return false; }
                slice = Rectangle.Intersect(slice, haloRect);
                if (slice.Width < 1 || slice.Height < 1) { return false; }
                Color tint = Tint(color, alpha);
                if (tint.A == 0) { return false; }
                // Source coordinates are in the padded halo, not the item atlas.
                // Mirror the crop's position in the FULL halo, then flip its UVs.
                int x = flipH ? haloRect.Width - slice.Right : slice.X;
                int y = flipV ? haloRect.Height - slice.Bottom : slice.Y;
                Vector2 origin = c.Origin + new Vector2(Math.Max(0, c.HaloPadding))
                    - new Vector2(x, y);
                c.Batch.Draw(c.HaloTexture, c.Position, slice, tint, c.Rotation,
                    origin, c.Scale, c.Effects, c.Depth);
                Count++;
                return true;
            }

            internal void Trace(float width, float speed, Color color, float alpha, float offset)
            {
                bool horizontal = bounds.Width >= bounds.Height;
                int begin = (horizontal ? bounds.Left : bounds.Top) + Math.Max(0, c.HaloPadding);
                int length = horizontal ? bounds.Width : bounds.Height;
                int segments = Quality == 0 ? 5 : 7;
                float bandWidth = Math.Max(1f, length * Math.Clamp(width * Size, .12f, .24f));
                // Smooth out-and-back traversal has no teleport at the endpoints.
                float travel = .5f + .5f * Wave(Math.Abs(speed), offset);
                if (speed < 0) { travel = 1f - travel; }
                float center = begin - bandWidth * .5f + (length + bandWidth) * travel;
                int start = (int)Math.Floor(center - bandWidth * .5f);
                int span = Math.Max(segments, (int)Math.Ceiling(bandWidth));
                float pulse = 1f + Motion * .08f * Wave(.6f, offset);
                for (int i = 0; i < segments && Remaining > 0; i++)
                {
                    int a = start + i * span / segments;
                    int b = start + (i + 1) * span / segments;
                    float t = (i + .5f) / segments;
                    float weight = (float)Math.Sin(Math.PI * t);
                    weight *= weight;
                    Rectangle slice = horizontal
                        ? new Rectangle(a, 0, b - a, haloRect.Height)
                        : new Rectangle(0, a, haloRect.Width, b - a);
                    HaloStrip(slice, color, alpha * weight * pulse);
                }
            }

            internal float NeighborStep => c.ContourAnchors == null || c.ContourAnchors.Length < 2
                ? 0f : Math.Max(1, c.ContourAnchors.Length / 16) / (float)c.ContourAnchors.Length;

            internal bool ContourPos(float u, out Vector2 root, out Vector2 normal)
            {
                root = normal = Vector2.Zero;
                int count = c.ContourAnchors == null ? 0 : c.ContourAnchors.Length;
                if (count < 1) { return false; }
                float index = (u - (float)Math.Floor(u)) * count;
                int a = Math.Min(count - 1, (int)index), b = (a + 1) % count;
                Vector2 source = c.ContourAnchors[a], next = c.ContourAnchors[b];
                if (!Finite(source) || !Finite(next)) { return false; }
                float mix = index - a;
                // Do not interpolate across missing/disconnected contour spans.
                float gap = ScreenLength(next - source);
                if (gap <= Math.Max(DetailPixels * 2f, 2f)) { source = Vector2.Lerp(source, next, mix); }
                Vector2 n = c.ContourNormals != null && a < c.ContourNormals.Length
                    ? c.ContourNormals[a] : Vector2.Zero;
                if (!Finite(n)) { n = Vector2.Zero; }
                float nlen = n.Length();
                normal = nlen > .00001f ? n / nlen : Vector2.Zero;
                root = new Vector2(flipH ? c.Source.Width - source.X : source.X,
                    flipV ? c.Source.Height - source.Y : source.Y);
                normal = new Vector2(flipH ? -normal.X : normal.X, flipV ? -normal.Y : normal.Y);
                return true;
            }

            internal void Glint(float offset, Color color, float alpha, bool star)
            {
                if (!ShowDetails || !ContourPos(Phase * .045f + offset,
                    out Vector2 root, out Vector2 normal)) { return; }
                float fade = .65f + .35f * Wave(.35f, offset);
                Decal(star ? Star : Glow, root, Vector2.One,
                    DetailPixels * .7f, 0, CenterPivot, color, alpha * fade);
            }

            // Decor dimensions are screen-pixel targets, independent of source
            // size or atlas transparent padding. Blit applies a final hard cap.
            internal void Decal(int tile, Vector2 anchor, Vector2 ratio, float pixels,
                float angle, Vector2 pivot, Color color, float alpha)
                => Blit(tile, anchor, ratio * LocalPixels(pixels), angle, pivot, color, alpha);

            internal bool Blit(int tile, Vector2 anchor, Vector2 desiredSize, float angle,
                Vector2 pivot, Color color, float alpha)
            {
                if (Remaining <= 0 || !Usable(c.SoftTexture) || !Finite(anchor) ||
                    !Finite(desiredSize) || desiredSize.X <= 0 || desiredSize.Y <= 0) { return false; }
                Color tint = Tint(color, alpha * detailFade);
                if (tint.A == 0) { return false; }
                Rectangle source = SoftEffectAtlas.Rect(tile);
                Vector2 axis = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
                float xLength = (axis * c.Scale).Length();
                if (xLength < .000001f) { return false; }
                float yLength = c.Scale.X * c.Scale.Y / xLength;
                Vector2 screenSize = new Vector2(desiredSize.X * xLength, desiredSize.Y * Math.Abs(yLength));
                // Includes the full tile, not just its opaque center.
                float cap = c.World ? 7f : 3f;
                float shrink = Math.Min(1f, cap / Math.Max(screenSize.X, screenSize.Y));
                Vector2 scale = new Vector2(desiredSize.X / source.Width * xLength,
                    desiredSize.Y / source.Height * yLength) * shrink;
                float rotation = c.Rotation + (float)Math.Atan2(axis.Y * c.Scale.Y, axis.X * c.Scale.X);
                c.Batch.Draw(c.SoftTexture, Map(anchor), source, tint, rotation,
                    pivot, scale, SpriteEffects.None, c.Depth);
                Count++;
                return true;
            }

            internal void Animated(int firstTile, int frameCount, float cycles, Vector2 anchor,
                Vector2 size, float angle, Vector2 pivot, Color color, float alpha)
            {
                if (Remaining < 2 || frameCount < 1 || !float.IsFinite(cycles)) { return; }
                float animation = (cycles - (float)Math.Floor(cycles)) * frameCount;
                int frame = Math.Min(frameCount - 1, (int)animation);
                float mix = animation - frame;
                Blit(firstTile + frame, anchor, size, angle, pivot, color, alpha * (1f - mix));
                Blit(firstTile + (frame + 1) % frameCount, anchor, size, angle, pivot, color, alpha * mix);
            }

            private Vector2 Map(Vector2 local)
            {
                local = (local - c.Origin) * c.Scale;
                return c.Position + new Vector2(local.X * cos - local.Y * sin, local.X * sin + local.Y * cos);
            }

            private Color Tint(Color rgb, float alpha)
            {
                float a = Math.Clamp(alpha * opacity, 0, 1);
                byte byteAlpha = (byte)(a * 255f + .5f);
                if (c.StraightAlpha) { return new Color(rgb.R, rgb.G, rgb.B, byteAlpha); }
                return new Color((byte)(rgb.R * a + .5f), (byte)(rgb.G * a + .5f),
                    (byte)(rgb.B * a + .5f), byteAlpha);
            }
        }

        private static float ArcFlash(float phase, float seed)
        {
            float wave = .5f + .5f * (float)Math.Sin(Tau * (phase * 1.45f + seed * 3f));
            float t = Math.Clamp((wave - .66f) / .34f, 0f, 1f);
            return t * t * (3f - 2f * t) * (.8f + .2f *
                (float)Math.Sin(Tau * (phase * .37f + seed * 5f)));
        }

        private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
        private static float Number(float value, float fallback) => float.IsFinite(value) ? value : fallback;
        private static bool Usable(Texture2D texture) => texture != null && !texture.IsDisposed;
        private static Color Rgb(Color color) => new Color(color.R, color.G, color.B, (byte)255);
        private static float Noise(int i, int salt, float seed)
        {
            unchecked
            {
                uint h = (uint)i * 2654435761u ^ (uint)salt * 2246822519u ^ (uint)(seed * 65536);
                h ^= h >> 16;
                h *= 2146121005u;
                h ^= h >> 15;
                return (h & 0xffffu) / 65535f;
            }
        }
    }
}
