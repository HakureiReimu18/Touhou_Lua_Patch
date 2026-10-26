using System;
using System.Collections.Generic;
using System.IO;

namespace Touhou.WeaponBadges
{
    internal sealed class GifFrame
    {
        public byte[] Rgba;
        public int DelayMilliseconds;
    }

    internal sealed class GifAnimation
    {
        public int Width, Height;
        public List<GifFrame> Frames;
    }

    internal static class GifDecoder
    {
        // Retained frame pixels are capped at 64 MiB. Working memory additionally
        // uses one canvas, at most one restore canvas, one index image and LZW tables.
        private const int MaxDecodedBytes = 64 * 1024 * 1024;
        private const int MaxCanvasPixels = 4 * 1024 * 1024;
        private const int MaxFrames = 4096;
        private const int MaxInputBytes = 64 * 1024 * 1024;

        // Reads forward only and leaves the caller's stream open. Delay values are
        // the GIF centisecond values converted to milliseconds, including zero.
        public static GifAnimation Decode(Stream input)
        {
            if (input == null) { throw new ArgumentNullException(nameof(input)); }

            var reader = new GifReader(input);
            byte[] header = reader.ReadBytes(6);
            if (header[0] != 'G' || header[1] != 'I' || header[2] != 'F' ||
                header[3] != '8' || (header[4] != '7' && header[4] != '9') || header[5] != 'a')
            {
                throw new InvalidDataException("Invalid GIF87a/GIF89a header.");
            }

            int width = reader.ReadUInt16();
            int height = reader.ReadUInt16();
            if (width == 0 || height == 0 || (long)width * height > MaxCanvasPixels)
            {
                throw new InvalidDataException("GIF logical canvas exceeds the pixel limit or is empty.");
            }
            int canvasBytes = width * height * 4;
            int screenFlags = reader.ReadByte();
            int backgroundIndex = reader.ReadByte();
            reader.ReadByte(); // Pixel aspect ratio does not change the stored pixel grid.
            byte[] globalPalette = (screenFlags & 0x80) != 0
                ? reader.ReadBytes(3 * (1 << ((screenFlags & 7) + 1))) : null;
            if (globalPalette != null && backgroundIndex >= globalPalette.Length / 3)
            {
                throw new InvalidDataException("GIF background index is outside the global palette.");
            }

            var animation = new GifAnimation
            {
                Width = width,
                Height = height,
                Frames = new List<GifFrame>()
            };
            byte[] canvas = null;
            byte[] restore = null;
            int disposal = 0;
            int delay = 0;
            int transparentIndex = -1;

            while (true)
            {
                int marker = reader.ReadByte();
                if (marker == 0x3b)
                {
                    if (animation.Frames.Count == 0) { throw new InvalidDataException("GIF contains no images."); }
                    return animation;
                }
                if (marker == 0x21)
                {
                    int label = reader.ReadByte();
                    if (label == 0xf9)
                    {
                        if (reader.ReadByte() != 4) { throw new InvalidDataException("Invalid GIF graphic control size."); }
                        int flags = reader.ReadByte();
                        disposal = (flags >> 2) & 7;
                        if (disposal > 3) { throw new InvalidDataException("Unsupported GIF disposal method."); }
                        delay = reader.ReadUInt16() * 10;
                        int index = reader.ReadByte();
                        transparentIndex = (flags & 1) != 0 ? index : -1;
                        if (reader.ReadByte() != 0) { throw new InvalidDataException("Invalid GIF graphic control terminator."); }
                    }
                    else
                    {
                        // Comments, application/loop metadata and unknown extensions
                        // carry sub-blocks. Plain text is not rasterized; it consumes
                        // its graphic control so that it cannot leak into an image.
                        reader.SkipSubBlocks();
                        if (label == 0x01)
                        {
                            disposal = 0;
                            delay = 0;
                            transparentIndex = -1;
                        }
                    }
                    continue;
                }
                if (marker != 0x2c) { throw new InvalidDataException("Invalid GIF block marker."); }

                if (animation.Frames.Count >= MaxFrames ||
                    (long)(animation.Frames.Count + 1) * canvasBytes > MaxDecodedBytes)
                {
                    throw new InvalidDataException("GIF exceeds the frame count or 64 MiB decoded RGBA limit.");
                }
                int left = reader.ReadUInt16();
                int top = reader.ReadUInt16();
                int frameWidth = reader.ReadUInt16();
                int frameHeight = reader.ReadUInt16();
                if (frameWidth == 0 || frameHeight == 0 ||
                    left + frameWidth > width || top + frameHeight > height)
                {
                    throw new InvalidDataException("GIF image rectangle is empty or outside the logical canvas.");
                }
                int imageFlags = reader.ReadByte();
                byte[] palette = (imageFlags & 0x80) != 0
                    ? reader.ReadBytes(3 * (1 << ((imageFlags & 7) + 1))) : globalPalette;
                if (palette == null) { throw new InvalidDataException("GIF image has no color palette."); }
                if (transparentIndex >= palette.Length / 3)
                {
                    throw new InvalidDataException("GIF transparency index is outside the image palette.");
                }

                byte[] indices = DecodeIndices(reader, frameWidth * frameHeight);
                if (canvas == null)
                {
                    canvas = new byte[canvasBytes];
                    FillBackground(canvas, width, 0, 0, width, height,
                        globalPalette, backgroundIndex, palette, transparentIndex);
                }
                if (disposal == 3)
                {
                    if (restore == null) { restore = new byte[canvasBytes]; }
                    Buffer.BlockCopy(canvas, 0, restore, 0, canvasBytes);
                }

                int source = 0;
                if ((imageFlags & 0x40) != 0)
                {
                    // GIF interlace passes: rows 0/8, 4/8, 2/4, 1/2.
                    for (int pass = 0; pass < 4; pass++)
                    {
                        int start = pass == 0 ? 0 : (1 << (3 - pass));
                        int step = pass < 2 ? 8 : (1 << (4 - pass));
                        for (int y = start; y < frameHeight; y += step)
                        {
                            DrawRow(canvas, ((top + y) * width + left) * 4,
                                indices, source, frameWidth, palette, transparentIndex);
                            source += frameWidth;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < frameHeight; y++)
                    {
                        DrawRow(canvas, ((top + y) * width + left) * 4,
                            indices, source, frameWidth, palette, transparentIndex);
                        source += frameWidth;
                    }
                }

                animation.Frames.Add(new GifFrame
                {
                    Rgba = (byte[])canvas.Clone(),
                    DelayMilliseconds = delay
                });
                // Apply disposal only after taking the displayed frame snapshot.
                if (disposal == 2)
                {
                    FillBackground(canvas, width, left, top, frameWidth, frameHeight,
                        globalPalette, backgroundIndex, palette, transparentIndex);
                }
                else if (disposal == 3)
                {
                    Buffer.BlockCopy(restore, 0, canvas, 0, canvasBytes);
                }
                // A graphic control extension applies to exactly one graphic.
                disposal = 0;
                delay = 0;
                transparentIndex = -1;
            }
        }

        private static void FillBackground(byte[] canvas, int canvasWidth,
            int left, int top, int width, int height, byte[] globalPalette,
            int backgroundIndex, byte[] palette, int transparentIndex)
        {
            // Transparent animations conventionally restore to transparency. Keep
            // that palette entry's RGB too, so fully transparent RGBA is preserved.
            byte[] colors = transparentIndex >= 0 ? palette : globalPalette;
            int color = (transparentIndex >= 0 ? transparentIndex : backgroundIndex) * 3;
            byte r = colors == null ? (byte)0 : colors[color];
            byte g = colors == null ? (byte)0 : colors[color + 1];
            byte b = colors == null ? (byte)0 : colors[color + 2];
            byte a = transparentIndex >= 0 || colors == null ? (byte)0 : (byte)255;
            for (int y = top; y < top + height; y++)
            {
                int end = ((y * canvasWidth) + left + width) * 4;
                for (int p = (y * canvasWidth + left) * 4; p < end; p += 4)
                {
                    canvas[p] = r;
                    canvas[p + 1] = g;
                    canvas[p + 2] = b;
                    canvas[p + 3] = a;
                }
            }
        }

        private static void DrawRow(byte[] canvas, int destination, byte[] indices,
            int source, int count, byte[] palette, int transparentIndex)
        {
            for (int x = 0; x < count; x++, destination += 4)
            {
                int index = indices[source + x];
                if (index >= palette.Length / 3) { throw new InvalidDataException("GIF pixel index is outside the image palette."); }
                if (index == transparentIndex) { continue; }
                int color = index * 3;
                canvas[destination] = palette[color];
                canvas[destination + 1] = palette[color + 1];
                canvas[destination + 2] = palette[color + 2];
                canvas[destination + 3] = 255;
            }
        }

        private static byte[] DecodeIndices(GifReader reader, int pixelCount)
        {
            int minimumSize = reader.ReadByte();
            if (minimumSize < 2 || minimumSize > 8) { throw new InvalidDataException("Invalid GIF LZW minimum code size."); }
            var data = new ImageDataReader(reader);
            var output = new byte[pixelCount];
            var prefix = new short[4096];
            var suffix = new byte[4096];
            var stack = new byte[4096];
            int clearCode = 1 << minimumSize;
            int endCode = clearCode + 1;
            int nextCode = endCode + 1;
            int codeSize = minimumSize + 1;
            int previous = -1;
            byte first = 0;
            int written = 0;

            while (true)
            {
                int code = data.ReadCode(codeSize);
                if (code == clearCode)
                {
                    nextCode = endCode + 1;
                    codeSize = minimumSize + 1;
                    previous = -1;
                    continue;
                }
                if (code == endCode)
                {
                    if (written != pixelCount) { throw new InvalidDataException("GIF LZW image has too few pixels."); }
                    data.Finish();
                    return output;
                }
                if (previous < 0)
                {
                    if (code >= clearCode) { throw new InvalidDataException("Invalid first GIF LZW code."); }
                    if (written == pixelCount) { throw new InvalidDataException("GIF LZW image has too many pixels."); }
                    first = (byte)code;
                    output[written++] = first;
                    previous = code;
                    continue;
                }

                int incoming = code;
                int count = 0;
                if (code == nextCode)
                {
                    // KwKwK: the encoder has just defined previous + its first byte.
                    stack[count++] = first;
                    code = previous;
                }
                else if (code > nextCode)
                {
                    throw new InvalidDataException("Invalid GIF LZW dictionary reference.");
                }
                while (code >= clearCode)
                {
                    if (code >= nextCode || count >= stack.Length - 1)
                    {
                        throw new InvalidDataException("Invalid GIF LZW prefix chain.");
                    }
                    stack[count++] = suffix[code];
                    code = prefix[code];
                }
                first = (byte)code;
                stack[count++] = first;
                if (count > pixelCount - written) { throw new InvalidDataException("GIF LZW image has too many pixels."); }
                while (count > 0) { output[written++] = stack[--count]; }

                // A full dictionary remains frozen at 12 bits until a clear code.
                if (nextCode < 4096)
                {
                    prefix[nextCode] = (short)previous;
                    suffix[nextCode] = first;
                    nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < 12) { codeSize++; }
                }
                previous = incoming;
            }
        }

        private sealed class GifReader
        {
            private readonly Stream input;
            private int bytesRead;
            private readonly byte[] skipBuffer = new byte[255];

            public GifReader(Stream input) { this.input = input; }

            public int ReadByte()
            {
                CheckBudget(1);
                int value = input.ReadByte();
                if (value < 0) { throw new InvalidDataException("Unexpected end of GIF stream."); }
                bytesRead++;
                return value;
            }

            public int ReadUInt16() { return ReadByte() | (ReadByte() << 8); }

            public byte[] ReadBytes(int count)
            {
                var bytes = new byte[count];
                ReadExactly(bytes, count);
                return bytes;
            }

            public void ReadExactly(byte[] buffer, int count)
            {
                CheckBudget(count);
                int offset = 0;
                while (offset < count)
                {
                    int read = input.Read(buffer, offset, count - offset);
                    if (read <= 0) { throw new InvalidDataException("Unexpected end of GIF data block."); }
                    offset += read;
                    bytesRead += read;
                }
            }

            public void SkipSubBlocks()
            {
                int count;
                while ((count = ReadByte()) != 0) { ReadExactly(skipBuffer, count); }
            }

            private void CheckBudget(int count)
            {
                if (count > MaxInputBytes - bytesRead) { throw new InvalidDataException("GIF exceeds the 64 MiB input limit."); }
            }
        }

        private sealed class ImageDataReader
        {
            private readonly GifReader reader;
            private readonly byte[] block = new byte[255];
            private int offset;
            private int length;
            private int bits;
            private int bitCount;

            public ImageDataReader(GifReader reader) { this.reader = reader; }

            public int ReadCode(int size)
            {
                while (bitCount < size)
                {
                    if (offset == length)
                    {
                        length = reader.ReadByte();
                        if (length == 0) { throw new InvalidDataException("GIF LZW data has no end code."); }
                        reader.ReadExactly(block, length);
                        offset = 0;
                    }
                    bits |= block[offset++] << bitCount;
                    bitCount += 8;
                }
                int code = bits & ((1 << size) - 1);
                bits >>= size;
                bitCount -= size;
                return code;
            }

            public void Finish()
            {
                // The current block is already buffered; consume subsequent blocks
                // through the terminator, even if the end code precedes padding.
                reader.SkipSubBlocks();
            }
        }
    }
}
