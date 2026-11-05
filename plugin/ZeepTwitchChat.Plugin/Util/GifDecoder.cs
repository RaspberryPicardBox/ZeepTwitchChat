using System;
using System.Collections.Generic;

namespace ZeepTwitchChat.Util
{
    /// <summary>
    /// Minimal GIF87a/89a decoder, enough for chat emotes: global and local colour tables,
    /// transparency, frame delays and disposal, LZW image data.
    ///
    /// Written from the specification because Unity's <c>Texture2D.LoadImage</c> decodes
    /// PNG/JPG only, and shipping a third-party decoder DLL for one feature is not worth it.
    ///
    /// Dependency free (returns raw RGBA32 bytes), so it is unit tested outside the game.
    /// </summary>
    public static class GifDecoder
    {
        private const int MaxPixels = 512 * 512;

        /// <summary>Ceiling on frames decoded from one file, whatever the caller asks for.</summary>
        private const int MaxFrames = 512;

        /// <summary>
        /// Ceiling on the pixels held for one file (frames * width * height * 4 bytes). A small
        /// file can describe a lot of frames, so the frame count alone is not enough.
        /// </summary>
        private const long MaxDecodedBytes = 32L * 1024 * 1024;

        /// <summary>One decoded frame, already composited onto the full canvas.</summary>
        public sealed class GifFrame
        {
            public int Width;
            public int Height;
            public int DelayMilliseconds;

            /// <summary>RGBA32 pixels, row order bottom-up (Unity texture order).</summary>
            public byte[] Rgba;
        }

        /// <summary>True when the data starts with the GIF signature.</summary>
        public static bool LooksLikeGif(byte[] data)
        {
            return data != null && data.Length > 6 &&
                   data[0] == 'G' && data[1] == 'I' && data[2] == 'F';
        }

        /// <summary>
        /// Decodes up to <paramref name="maxFrames"/> frames. Returns false (with an empty
        /// list) for anything malformed, so a bad emote can never break the chat.
        /// </summary>
        public static bool TryDecode(byte[] data, int maxFrames, out List<GifFrame> frames)
        {
            frames = new List<GifFrame>();

            if (!LooksLikeGif(data) || data.Length < 13)
            {
                return false;
            }

            try
            {
                int limit = maxFrames <= 0 ? 1 : Math.Min(maxFrames, MaxFrames);
                return Decode(data, limit, frames);
            }
            catch (Exception)
            {
                frames.Clear();
                return false;
            }
        }

        private static bool Decode(byte[] data, int maxFrames, List<GifFrame> frames)
        {
            int index = 6;
            int width = ReadUInt16(data, ref index);
            int height = ReadUInt16(data, ref index);

            if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            {
                return false;
            }

            byte packed = data[index++];
            index++; // background colour index
            index++; // pixel aspect ratio

            int[] globalTable = null;

            if ((packed & 0x80) != 0)
            {
                globalTable = ReadColourTable(data, ref index, 2 << (packed & 0x07));
            }

            // Canvas the frames are composited onto, RGBA32, initialised to transparent.
            byte[] canvas = new byte[width * height * 4];

            int delayMs = 0;
            int transparentIndex = -1;
            int disposal = 0;
            byte[] previousCanvas = null;

            while (index < data.Length && frames.Count < maxFrames)
            {
                byte block = data[index++];

                if (block == 0x21) // extension
                {
                    if (data[index++] == 0xF9) // graphic control
                    {
                        int blockSize = data[index++]; // always 4
                        byte flags = data[index++];
                        delayMs = ReadUInt16(data, ref index) * 10;
                        transparentIndex = (flags & 0x01) != 0 ? data[index] : -1;
                        disposal = (flags >> 2) & 0x07;
                        index += Math.Max(0, blockSize - 3);
                    }

                    SkipSubBlocks(data, ref index);
                    continue;
                }

                if (block != 0x2C)
                {
                    // Trailer (0x3B) or something unknown: stop rather than guess.
                    break;
                }

                int left = ReadUInt16(data, ref index);
                int top = ReadUInt16(data, ref index);
                int frameWidth = ReadUInt16(data, ref index);
                int frameHeight = ReadUInt16(data, ref index);
                byte imagePacked = data[index++];

                int[] colourTable = globalTable;

                if ((imagePacked & 0x80) != 0)
                {
                    colourTable = ReadColourTable(data, ref index, 2 << (imagePacked & 0x07));
                }

                if (colourTable == null || frameWidth <= 0 || frameHeight <= 0)
                {
                    return false;
                }

                if (disposal == 3)
                {
                    previousCanvas = (byte[])canvas.Clone();
                }

                int minCodeSize = data[index++];
                byte[] indices = ReadImageData(data, ref index, minCodeSize, frameWidth * frameHeight);
                if (indices == null)
                {
                    return false;
                }

                Composite(canvas, width, height, indices, frameWidth, frameHeight,
                    left, top, colourTable, transparentIndex);

                if ((long)frames.Count * width * height * 4 > MaxDecodedBytes)
                {
                    return false;
                }

                frames.Add(new GifFrame
                {
                    Width = width,
                    Height = height,
                    DelayMilliseconds = delayMs <= 0 ? 100 : delayMs,
                    Rgba = (byte[])canvas.Clone()
                });

                if (disposal == 2)
                {
                    ClearRect(canvas, width, height, left, top, frameWidth, frameHeight);
                }
                else if (disposal == 3 && previousCanvas != null)
                {
                    canvas = previousCanvas;
                    previousCanvas = null;
                }

                delayMs = 0;
                transparentIndex = -1;
                disposal = 0;
            }

            return frames.Count > 0;
        }


        private static int[] ReadColourTable(byte[] data, ref int index, int size)
        {
            if (index + size * 3 > data.Length)
            {
                return null;
            }

            int[] table = new int[size];

            for (int i = 0; i < size; i++)
            {
                int r = data[index++];
                int g = data[index++];
                int b = data[index++];
                table[i] = (r << 16) | (g << 8) | b;
            }

            return table;
        }

        private static void SkipSubBlocks(byte[] data, ref int index)
        {
            while (index < data.Length)
            {
                int size = data[index++];
                if (size == 0)
                {
                    return;
                }

                index += size;
            }
        }

        private static byte[] ReadImageData(byte[] data, ref int index, int minCodeSize, int pixelCount)
        {
            if (minCodeSize < 1 || minCodeSize > 11)
            {
                return null;
            }

            List<byte> compressed = new List<byte>(pixelCount);

            while (index < data.Length)
            {
                int size = data[index++];
                if (size == 0)
                {
                    break;
                }

                if (index + size > data.Length)
                {
                    return null;
                }

                for (int i = 0; i < size; i++)
                {
                    compressed.Add(data[index + i]);
                }

                index += size;
            }

            return LzwDecode(compressed.ToArray(), minCodeSize, pixelCount);
        }

        /// <summary>GIF's variable-width LZW, decoded into palette indices.</summary>
        private static byte[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
        {
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;

            int[] prefix = new int[4096];
            byte[] suffix = new byte[4096];
            byte[] first = new byte[4096];

            for (int i = 0; i < clearCode && i < 4096; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                first[i] = (byte)i;
            }

            byte[] output = new byte[pixelCount];
            byte[] stack = new byte[4096];
            int outPos = 0;
            int bitBuffer = 0;
            int bitCount = 0;
            int pos = 0;
            int previousCode = -1;

            while (outPos < pixelCount)
            {
                while (bitCount < codeSize)
                {
                    if (pos >= data.Length)
                    {
                        return outPos > 0 ? output : null;
                    }

                    bitBuffer |= data[pos++] << bitCount;
                    bitCount += 8;
                }

                int code = bitBuffer & ((1 << codeSize) - 1);
                bitBuffer >>= codeSize;
                bitCount -= codeSize;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    previousCode = -1;
                    continue;
                }

                if (code == endCode)
                {
                    break;
                }

                if (previousCode < 0)
                {
                    if (code >= clearCode || code >= 4096)
                    {
                        return null;
                    }

                    output[outPos++] = suffix[code];
                    previousCode = code;
                    continue;
                }

                int sp = 0;
                int currentCode = code;

                if (code >= nextCode)
                {
                    if (code != nextCode)
                    {
                        return null;
                    }

                    stack[sp++] = first[previousCode];
                    currentCode = previousCode;
                }

                while (currentCode >= clearCode)
                {
                    if (sp >= stack.Length || currentCode >= 4096 || prefix[currentCode] < 0)
                    {
                        return null;
                    }

                    stack[sp++] = suffix[currentCode];
                    currentCode = prefix[currentCode];
                }

                byte lead = suffix[currentCode];
                stack[sp++] = lead;

                if (nextCode < 4096)
                {
                    prefix[nextCode] = previousCode;
                    suffix[nextCode] = lead;
                    first[nextCode] = first[previousCode];
                    nextCode++;

                    if (nextCode == (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                }

                while (sp > 0 && outPos < pixelCount)
                {
                    output[outPos++] = stack[--sp];
                }

                previousCode = code;
            }

            return output;
        }

        private static void Composite(byte[] canvas, int canvasWidth, int canvasHeight, byte[] indices,
            int frameWidth, int frameHeight, int left, int top, int[] table, int transparentIndex)
        {
            for (int y = 0; y < frameHeight; y++)
            {
                // GIF rows run top-down, Unity textures bottom-up.
                int destY = canvasHeight - 1 - (top + y);
                if (destY < 0 || destY >= canvasHeight)
                {
                    continue;
                }

                int srcRow = y * frameWidth;
                int destRow = destY * canvasWidth * 4;

                for (int x = 0; x < frameWidth; x++)
                {
                    int destX = left + x;
                    if (destX < 0 || destX >= canvasWidth)
                    {
                        continue;
                    }

                    int colourIndex = indices[srcRow + x];
                    if (colourIndex == transparentIndex || colourIndex >= table.Length)
                    {
                        continue;
                    }

                    int colour = table[colourIndex];
                    int offset = destRow + destX * 4;
                    canvas[offset] = (byte)((colour >> 16) & 0xFF);
                    canvas[offset + 1] = (byte)((colour >> 8) & 0xFF);
                    canvas[offset + 2] = (byte)(colour & 0xFF);
                    canvas[offset + 3] = 0xFF;
                }
            }
        }

        private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight,
            int left, int top, int frameWidth, int frameHeight)
        {
            for (int y = 0; y < frameHeight; y++)
            {
                int destY = canvasHeight - 1 - (top + y);
                if (destY < 0 || destY >= canvasHeight)
                {
                    continue;
                }

                for (int x = 0; x < frameWidth; x++)
                {
                    int destX = left + x;
                    if (destX < 0 || destX >= canvasWidth)
                    {
                        continue;
                    }

                    int offset = (destY * canvasWidth + destX) * 4;
                    canvas[offset] = 0;
                    canvas[offset + 1] = 0;
                    canvas[offset + 2] = 0;
                    canvas[offset + 3] = 0;
                }
            }
        }

        private static int ReadUInt16(byte[] data, ref int index)
        {
            if (index + 2 > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            int value = data[index] | (data[index + 1] << 8);
            index += 2;
            return value;
        }
    }
}

