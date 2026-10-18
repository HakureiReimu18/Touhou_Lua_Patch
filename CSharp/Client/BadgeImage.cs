using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    internal sealed class BadgeImage : IDisposable
    {
        private readonly List<Texture2D> textures = new List<Texture2D>();
        private readonly List<Rectangle> sources = new List<Rectangle>();
        private readonly List<int> ends = new List<int>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private int duration;

        internal BadgeImage(GraphicsDevice device, BadgeRule rule)
        {
            try
            {
                using (var stream = File.OpenRead(rule.File))
                {
                    if (string.Equals(Path.GetExtension(rule.File), ".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        GifAnimation animation = GifDecoder.Decode(stream);
                        foreach (GifFrame frame in animation.Frames)
                        {
                            var texture = new Texture2D(device, animation.Width, animation.Height);
                            textures.Add(texture);
                            byte[] rgba = frame.Rgba;
                            for (int p = 0; p < rgba.Length; p += 4)
                            {
                                int alpha = rgba[p + 3];
                                rgba[p] = (byte)(rgba[p] * alpha / 255);
                                rgba[p + 1] = (byte)(rgba[p + 1] * alpha / 255);
                                rgba[p + 2] = (byte)(rgba[p + 2] * alpha / 255);
                            }
                            texture.SetData(rgba);
                            sources.Add(new Rectangle(0, 0, animation.Width, animation.Height));
                            duration += Math.Max(20, frame.DelayMilliseconds);
                            ends.Add(duration);
                        }
                    }
                    else
                    {
                        Texture2D texture = Texture2D.FromStream(device, stream);
                        textures.Add(texture);
                        // FromStream supplies straight alpha; the game's UI uses AlphaBlend.
                        var pixels = new Color[texture.Width * texture.Height];
                        texture.GetData(pixels);
                        for (int p = 0; p < pixels.Length; p++)
                        {
                            Color c = pixels[p];
                            pixels[p] = new Color(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255, c.A);
                        }
                        texture.SetData(pixels);
                        int width = rule.FrameWidth > 0 ? rule.FrameWidth : texture.Width;
                        int height = rule.FrameHeight > 0 ? rule.FrameHeight : texture.Height;
                        int columns = texture.Width / width;
                        if (columns < 1 || height > texture.Height || (long)columns * (texture.Height / height) < rule.FrameCount)
                        { throw new InvalidDataException("Sprite sheet dimensions do not contain the configured frames."); }
                        for (int frame = 0; frame < rule.FrameCount; frame++)
                        {
                            sources.Add(new Rectangle(frame % columns * width, frame / columns * height, width, height));
                            duration = (int)Math.Round((frame + 1) * 1000.0 / rule.Fps);
                            ends.Add(duration);
                        }
                    }
                }
                if (sources.Count == 0) { throw new InvalidDataException("Image contains no frames."); }
                duration = Math.Max(duration, 1);
            }
            catch { Dispose(); throw; }
        }

        internal void Draw(SpriteBatch batch, Rectangle bounds)
        {
            int time = (int)(clock.ElapsedMilliseconds % duration);
            int frame = ends.FindIndex(end => time < end);
            if (frame < 0) { frame = 0; }
            Rectangle source = sources[frame];
            float scale = Math.Min((float)bounds.Width / source.Width, (float)bounds.Height / source.Height);
            var destination = new Rectangle(bounds.Center.X - (int)(source.Width * scale) / 2,
                bounds.Center.Y - (int)(source.Height * scale) / 2,
                Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
            batch.Draw(textures.Count == 1 ? textures[0] : textures[frame], destination, source, Color.White);
        }

        public void Dispose()
        {
            foreach (Texture2D texture in textures) { texture.Dispose(); }
            textures.Clear();
            clock.Stop();
        }
    }
}
