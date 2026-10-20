using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    internal static class SoftEffectAtlas
    {
        internal const int Glow = 0, Ring = 1, Runes = 2, Crystal = 3, Mist = 4, Star = 5, Ribbon = 6, Glyph = 7,
            Bolt0 = 8, FlameAura0 = 16, SolarAura0 = 32, WispAura0 = 48;
        internal const int FlameFrames = 16, SolarFrames = 16, WispFrames = 16, BoltFrames = 8;
        private static string path;
        private static Texture2D straight, premultiplied;
        internal static byte[] Alpha;

        internal static Rectangle Rect(int tile) => new Rectangle(tile % 4 * 128, tile / 4 * 128, 128, 128);

        internal static void Initialize(string directory)
        {
            Dispose();
            path = Path.Combine(directory, "Images", "Effects", "soft_effects_atlas.png");
        }

        internal static Texture2D Get(GraphicsDevice device, bool straightAlpha)
        {
            if (straight == null)
            {
                if (path == null) { throw new InvalidOperationException("Soft effect atlas is not initialized."); }
                Texture2D decoded = null;
                Texture2D newStraight = null, newPremultiplied = null;
                try
                {
                    using (var stream = File.OpenRead(path)) { decoded = Texture2D.FromStream(device, stream); }
                    byte[] alpha = SpriteAlphaReader.Read(decoded, new Rectangle(0, 0, decoded.Width, decoded.Height), path);
                    var colors = new Color[alpha.Length];
                    for (int i = 0; i < colors.Length; i++) { colors[i] = new Color((byte)255, (byte)255, (byte)255, alpha[i]); }
                    newStraight = new Texture2D(device, decoded.Width, decoded.Height);
                    newStraight.SetData(colors);
                    for (int i = 0; i < colors.Length; i++)
                    { byte a = alpha[i]; colors[i] = new Color(a, a, a, a); }
                    newPremultiplied = new Texture2D(device, decoded.Width, decoded.Height);
                    newPremultiplied.SetData(colors);
                    straight = newStraight; premultiplied = newPremultiplied; Alpha = alpha;
                }
                catch
                {
                    newStraight?.Dispose(); newPremultiplied?.Dispose();
                    throw;
                }
                finally { decoded?.Dispose(); }
            }
            return straightAlpha ? straight : premultiplied;
        }

        internal static bool AppliesTo(string kind) => kind == "flame" || kind == "solar" || kind == "frost" || kind == "lightning" || kind == "rune";

        internal static void Dispose()
        {
            straight?.Dispose(); premultiplied?.Dispose();
            straight = null; premultiplied = null; path = null; Alpha = null;
        }
    }
}
