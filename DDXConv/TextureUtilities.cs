namespace DDXConv;

/// <summary>
///     Shared utilities for Xbox 360 texture processing.
///     Contains format detection, size calculations, and untiling algorithms.
/// </summary>
public static class TextureUtilities
{
    #region Format Detection

    /// <summary>
    ///     Get the DDS FourCC code for a given Xbox 360 GPU texture format.
    /// </summary>
    public static uint GetDxgiFormat(uint gpuFormat)
    {
        return gpuFormat switch
        {
            0x52 => 0x31545844, // DXT1
            0x53 => 0x33545844, // DXT3  
            0x54 => 0x35545844, // DXT5
            0x71 => 0x32495441, // ATI2 (BC5) - Xbox 360 normal map format
            0x7B => 0x31495441, // ATI1 (BC4) - Single channel format (specular maps)
            0x82 => 0x31545844, // DXT1 (default when DWORD[4] is 0)
            0x86 => 0x31545844, // DXT1 variant
            0x88 => 0x35545844, // DXT5 variant
            0x12 => 0x31545844, // GPUTEXTUREFORMAT_DXT1
            0x13 => 0x33545844, // GPUTEXTUREFORMAT_DXT2/3
            0x14 => 0x35545844, // GPUTEXTUREFORMAT_DXT4/5
            0x06 => 0x18280046, // GPUTEXTUREFORMAT_8_8_8_8 -> A8R8G8B8
            0x04 => 0x28280044, // GPUTEXTUREFORMAT_5_6_5 -> R5G6B5
            _ => 0x31545844 // Default to DXT1
        };
    }

    /// <summary>
    ///     Get the DXT block size in bytes for a given format.
    /// </summary>
    public static int GetBlockSize(uint format)
    {
        return format switch
        {
            // DXT1
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => 8,
            // DXT3
            _ => 16
        };
    }

    /// <summary>
    ///     Check if format is DXT1-like (8 bytes per block).
    /// </summary>
    public static bool IsDxt1Format(uint format)
    {
        return format switch
        {
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => true,
            _ => false
        };
    }

    #endregion

    #region Size Calculations

    /// <summary>
    ///     Calculate the number of mip levels for given dimensions.
    /// </summary>
    public static uint CalculateMipLevels(uint width, uint height)
    {
        uint levels = 1;
        var w = width;
        var h = height;

        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            levels++;
        }

        return levels;
    }

    /// <summary>
    ///     Calculate byte size for a single mip level.
    /// </summary>
    public static uint CalculateMipSize(uint width, uint height, uint format)
    {
        return format switch
        {
            // DXT1
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
            // DXT3
            0x53 or 0x54 or 0x71 or 0x88 or 0x13 or 0x14 => Math.Max(1, (width + 3) / 4) *
                                                            Math.Max(1, (height + 3) / 4) * 16,
            // A8R8G8B8 - 32 bits per pixel
            0x06 => width * height * 4,
            // R5G6B5 - 16 bits per pixel
            0x04 => width * height * 2,
            _ => width * height * 4 // Default to 32bpp
        };
    }

    /// <summary>
    ///     Calculate byte size for a single mip level (int overload).
    /// </summary>
    public static int CalculateMipSize(int width, int height, uint format)
    {
        return (int)CalculateMipSize((uint)width, (uint)height, format);
    }

    /// <summary>
    ///     Calculate total size of a mip chain from given dimensions down to smallest mip.
    /// </summary>
    public static uint CalculateMainDataSize(uint width, uint height, uint format, uint mipLevels)
    {
        uint totalSize = 0;
        var w = width;
        var h = height;

        for (var i = 0; i < mipLevels; i++)
        {
            var mipSize = CalculateMipSize(w, h, format);
            totalSize += mipSize;

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return totalSize;
    }

    /// <summary>
    ///     Calculate total size of a full mip chain from the given dimensions down to 4x4.
    /// </summary>
    public static uint CalculateMipChainSize(int width, int height, uint format)
    {
        uint totalSize = 0;
        var w = width;
        var h = height;
        while (w >= 4 && h >= 4)
        {
            totalSize += (uint)CalculateMipSize(w, h, format);
            w /= 2;
            h /= 2;
        }

        return totalSize;
    }

    /// <summary>
    ///     Calculate DDS row pitch for given width and format.
    /// </summary>
    public static uint CalculatePitch(uint width, uint format)
    {
        // For DXT formats, pitch is the row of 4x4 blocks
        return Math.Max(1, (width + 3) / 4) * (uint)GetBlockSize(format);
    }

    #endregion

    #region Xbox 360 Tiling/Unswizzle

    /// <summary>
    ///     Unswizzle/untile Xbox 360 DXT texture data.
    ///     Xbox 360 uses Morton order (Z-order curve) tiling for textures.
    /// </summary>
    public static byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
    {
        ArgumentNullException.ThrowIfNull(src);

        var blockSize = GetBlockSize(format);
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;

        // Calculate expected size
        var expectedSize = blocksWide * blocksHigh * blockSize;
        if (src.Length < expectedSize)
        {
            // Pad if source is smaller
            var padded = new byte[expectedSize];
            Array.Copy(src, padded, src.Length);
            src = padded;
        }

        var dst = new byte[expectedSize];

        // Calculate log2 of bytes per pixel for tiling
        // DXT1: 8 bytes/block, 4x4 = 16 pixels, so 0.5 bytes/pixel -> log2 = -1, but we use block-based calculation
        // We actually use log2 of (blockSize / 4) since we're processing 4-pixel wide blocks
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (var y = 0; y < blocksHigh; y++)
        {
            var inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (var x = 0; x < blocksWide; x++)
            {
                var inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                var dstOffset = (y * blocksWide + x) * blockSize;
                var srcOffset = (int)inputOffset * blockSize;

                if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                    Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
            }
        }

        return dst;
    }

    /// <summary>
    ///     Calculate tiled row offset for Xbox 360 texture.
    /// </summary>
    public static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        var macro = ((y >> 5) * ((width >> 5) << (int)log2Bpp)) << 11;
        var micro = ((y & 6) >> 1) << (int)log2Bpp << 6;
        return macro + micro + ((y & 8) << (3 + (int)log2Bpp)) + ((y & 1) << 4);
    }

    /// <summary>
    ///     Calculate tiled column offset for Xbox 360 texture.
    /// </summary>
    public static uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
    {
        var macro = (x >> 5) << (int)log2Bpp << 11;
        var micro = (x & 7) << (int)log2Bpp << 6;
        var offset = baseOffset + macro + micro + ((x & 8) << (3 + (int)log2Bpp)) + ((x & 16) << 2) +
                     ((x & ~31u) << (int)log2Bpp);

        return ((offset >> 6) << 12) + ((y & 16) << 7) +
               ((offset & 0x3f) << 6) + (((x & 16) >> 2) | ((~(y & 16) >> 2) & 4)) +
               ((((y >> 3) ^ x) & 2) | (((y >> 2) ^ x) & 1));
    }

    #endregion

    #region Region Extraction

    /// <summary>
    ///     Extract a rectangular region from atlas data.
    ///     Handles DXT block alignment.
    /// </summary>
    public static byte[]? ExtractAtlasRegion(byte[] atlasData, int atlasWidth, int atlasHeight,
        int regionX, int regionY, int regionWidth, int regionHeight, uint format)
    {
        ArgumentNullException.ThrowIfNull(atlasData);

        var blockSize = GetBlockSize(format);
        var blockWidth = 4; // DXT block size in pixels
        var blockHeight = 4;

        // Calculate block counts
        var atlasBlocksX = (atlasWidth + blockWidth - 1) / blockWidth;
        var atlasBlocksY = (atlasHeight + blockHeight - 1) / blockHeight;
        var regionBlocksX = (regionWidth + blockWidth - 1) / blockWidth;
        var regionBlocksY = (regionHeight + blockHeight - 1) / blockHeight;
        var startBlockX = regionX / blockWidth;
        var startBlockY = regionY / blockHeight;

        var outputSize = regionBlocksX * regionBlocksY * blockSize;
        var output = new byte[outputSize];

        var destOffset = 0;
        for (var by = 0; by < regionBlocksY; by++)
        {
            var srcBlockY = startBlockY + by;
            if (srcBlockY >= atlasBlocksY) break;

            for (var bx = 0; bx < regionBlocksX; bx++)
            {
                var srcBlockX = startBlockX + bx;
                if (srcBlockX >= atlasBlocksX) continue;

                var srcOffset = (srcBlockY * atlasBlocksX + srcBlockX) * blockSize;

                if (srcOffset + blockSize <= atlasData.Length && destOffset + blockSize <= output.Length)
                    Array.Copy(atlasData, srcOffset, output, destOffset, blockSize);

                destOffset += blockSize;
            }
        }

        return output;
    }

    /// <summary>
    ///     Interleave two horizontal chunks into a single texture.
    /// </summary>
    public static byte[] InterleaveHorizontalChunks(byte[] leftChunk, byte[] rightChunk,
        int leftWidth, int rightWidth, int height, uint format)
    {
        ArgumentNullException.ThrowIfNull(leftChunk);
        ArgumentNullException.ThrowIfNull(rightChunk);

        var blockSize = GetBlockSize(format);
        var totalWidth = leftWidth + rightWidth;
        var totalBlocksWide = (totalWidth + 3) / 4;
        var leftBlocksWide = (leftWidth + 3) / 4;
        var rightBlocksWide = (rightWidth + 3) / 4;
        var blocksHigh = (height + 3) / 4;

        var result = new byte[totalBlocksWide * blocksHigh * blockSize];

        for (var y = 0; y < blocksHigh; y++)
        {
            // Copy left chunk blocks
            for (var x = 0; x < leftBlocksWide; x++)
            {
                var srcOffset = (y * leftBlocksWide + x) * blockSize;
                var dstOffset = (y * totalBlocksWide + x) * blockSize;
                if (srcOffset + blockSize <= leftChunk.Length && dstOffset + blockSize <= result.Length)
                    Array.Copy(leftChunk, srcOffset, result, dstOffset, blockSize);
            }

            // Copy right chunk blocks
            for (var x = 0; x < rightBlocksWide; x++)
            {
                var srcOffset = (y * rightBlocksWide + x) * blockSize;
                var dstOffset = (y * totalBlocksWide + leftBlocksWide + x) * blockSize;
                if (srcOffset + blockSize <= rightChunk.Length && dstOffset + blockSize <= result.Length)
                    Array.Copy(rightChunk, srcOffset, result, dstOffset, blockSize);
            }
        }

        return result;
    }

    #endregion
}
