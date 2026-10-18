using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Touhou.WeaponBadges
{
    internal static class SpriteAlphaReader
    {
        internal static byte[] Read(Texture2D texture, Rectangle source, string sourceFile = null)
        {
            if (texture == null) { throw new ArgumentNullException(nameof(texture)); }
            ValidateSource(source, texture.Width, texture.Height);

            try
            {
                if (texture.Format == SurfaceFormat.Color) { return ReadColor(texture, source); }
                if (IsDxt(texture.Format)) { return ReadDxt(texture, source); }
                throw new NotSupportedException("Unsupported texture format: " + texture.Format);
            }
            catch (Exception) when (!string.IsNullOrEmpty(sourceFile))
            {
                using (var stream = File.OpenRead(sourceFile))
                using (Texture2D decoded = Texture2D.FromStream(texture.GraphicsDevice, stream))
                {
                    ValidateSource(source, decoded.Width, decoded.Height);
                    return ReadColor(decoded, source);
                }
            }
        }

        private static void ValidateSource(Rectangle source, int width, int height)
        {
            // Subtraction avoids overflow from Rectangle.Right/Bottom on invalid input.
            if (source.X < 0 || source.Y < 0 || source.Width <= 0 || source.Height <= 0 ||
                source.X > width - source.Width || source.Y > height - source.Height)
            { throw new ArgumentOutOfRangeException(nameof(source), "Source must be inside the texture bounds."); }
        }

        private static byte[] ReadColor(Texture2D texture, Rectangle source)
        {
            var pixels = new Color[checked(source.Width * source.Height)];
            texture.GetData<Color>(0, source, pixels, 0, pixels.Length);
            var alpha = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) { alpha[i] = pixels[i].A; }
            return alpha;
        }

        private static bool IsDxt(SurfaceFormat format)
        {
            return format == SurfaceFormat.Dxt1 || format == SurfaceFormat.Dxt3 || format == SurfaceFormat.Dxt5;
        }

        private static int BlockCount(int size) { return (size - 1) / 4 + 1; }

        private static byte[] ReadDxt(Texture2D texture, Rectangle source)
        {
            int x = source.X & ~3;
            int y = source.Y & ~3;
            int right = (int)Math.Min(((long)source.X + source.Width + 3) & ~3L, texture.Width);
            int bottom = (int)Math.Min(((long)source.Y + source.Height + 3) & ~3L, texture.Height);
            var aligned = new Rectangle(x, y, right - x, bottom - y);
            int blockSize = texture.Format == SurfaceFormat.Dxt1 ? 8 : 16;
            int rowBytes = checked(BlockCount(aligned.Width) * blockSize);
            var blocks = new byte[checked(rowBytes * BlockCount(aligned.Height))];
            try
            {
                texture.GetData<byte>(0, aligned, blocks, 0, blocks.Length);
            }
            catch (Exception)
            {
                // Some backends cannot read compressed subrectangles. Keep only the
                // required block rows after reading level zero, avoiding a full alpha image.
                int fullRowBytes = checked(BlockCount(texture.Width) * blockSize);
                var full = new byte[checked(fullRowBytes * BlockCount(texture.Height))];
                texture.GetData<byte>(full);
                for (int row = 0; row < BlockCount(aligned.Height); row++)
                {
                    Array.Copy(full, checked((y / 4 + row) * fullRowBytes + x / 4 * blockSize),
                        blocks, row * rowBytes, rowBytes);
                }
            }

            byte[] decoded = DecodeDxtAlpha(blocks, aligned.Width, aligned.Height, texture.Format);
            var alpha = new byte[checked(source.Width * source.Height)];
            for (int row = 0; row < source.Height; row++)
            {
                Array.Copy(decoded, (source.Y - y + row) * aligned.Width + source.X - x,
                    alpha, row * source.Width, source.Width);
            }
            return alpha;
        }

        // Blocks are in row-major order, with a complete block for partial edge tiles.
        internal static byte[] DecodeDxtAlpha(byte[] blocks, int width, int height, SurfaceFormat format)
        {
            if (blocks == null) { throw new ArgumentNullException(nameof(blocks)); }
            if (width <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
            if (height <= 0) { throw new ArgumentOutOfRangeException(nameof(height)); }
            if (!IsDxt(format)) { throw new NotSupportedException("Unsupported DXT format: " + format); }
            int blockSize = format == SurfaceFormat.Dxt1 ? 8 : 16;
            int columns = BlockCount(width);
            int rows = BlockCount(height);
            int required = checked(checked(columns * rows) * blockSize);
            if (blocks.Length < required) { throw new ArgumentException("Incomplete DXT block data.", nameof(blocks)); }

            var alpha = new byte[checked(width * height)];
            var table = new byte[8];
            int offset = 0;
            for (int blockY = 0; blockY < rows; blockY++)
            {
                for (int blockX = 0; blockX < columns; blockX++, offset += blockSize)
                {
                    bool transparentDxt1 = false;
                    uint colorIndices = 0;
                    ulong alphaIndices = 0;
                    if (format == SurfaceFormat.Dxt1)
                    {
                        int color0 = blocks[offset] | blocks[offset + 1] << 8;
                        int color1 = blocks[offset + 2] | blocks[offset + 3] << 8;
                        transparentDxt1 = color0 <= color1;
                        colorIndices = (uint)blocks[offset + 4] | (uint)blocks[offset + 5] << 8 |
                            (uint)blocks[offset + 6] << 16 | (uint)blocks[offset + 7] << 24;
                    }
                    else if (format == SurfaceFormat.Dxt5)
                    {
                        int a0 = table[0] = blocks[offset];
                        int a1 = table[1] = blocks[offset + 1];
                        if (a0 > a1)
                        {
                            for (int i = 2; i < 8; i++)
                            { table[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7); }
                        }
                        else
                        {
                            for (int i = 2; i < 6; i++)
                            { table[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5); }
                            table[6] = 0;
                            table[7] = 255;
                        }
                        for (int i = 0; i < 6; i++) { alphaIndices |= (ulong)blocks[offset + 2 + i] << (8 * i); }
                    }

                    for (int py = 0; py < 4 && blockY * 4 + py < height; py++)
                    {
                        for (int px = 0; px < 4 && blockX * 4 + px < width; px++)
                        {
                            int pixel = py * 4 + px;
                            byte value;
                            if (format == SurfaceFormat.Dxt1)
                            {
                                value = transparentDxt1 && ((colorIndices >> (2 * pixel)) & 3) == 3
                                    ? (byte)0 : (byte)255;
                            }
                            else if (format == SurfaceFormat.Dxt3)
                            {
                                value = (byte)(((blocks[offset + pixel / 2] >> (4 * (pixel % 2))) & 15) * 17);
                            }
                            else { value = table[(int)((alphaIndices >> (3 * pixel)) & 7)]; }
                            alpha[(blockY * 4 + py) * width + blockX * 4 + px] = value;
                        }
                    }
                }
            }
            return alpha;
        }
    }
}
