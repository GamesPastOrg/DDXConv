using System;
using System.IO;
using System.Runtime.InteropServices;
using XCompression;

namespace DDXConv
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: DDXConv <input.ddx> [output.dds] or DDXConv <directory> <output_directory>");
                Console.WriteLine("Converts Xbox 360 DDX texture files to DDS format");
                return;
            }

            string inputPath = args[0];
            if (Directory.Exists(inputPath))
            {
                // Batch convert all .ddx files in the directory
                string outputDir = args[1];
                Directory.CreateDirectory(outputDir);

                var ddxFiles = Directory.GetFiles(inputPath, "*.ddx", SearchOption.AllDirectories);
                foreach (var ddxFile in ddxFiles)
                {
                    string relativePath = Path.GetRelativePath(inputPath, ddxFile);
                    string outputBatchPath = Path.Combine(outputDir, Path.ChangeExtension(relativePath, ".dds"));
                    Directory.CreateDirectory(Path.GetDirectoryName(outputBatchPath)!);

                    try
                    {
                        var parser = new DdxParser();
                        parser.ConvertDdxToDds(ddxFile, outputBatchPath);
                        Console.WriteLine($"Converted {ddxFile} to {outputBatchPath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error converting {ddxFile}: {ex.Message}");
                    }
                }

                Console.WriteLine($"Batch conversion completed. Converted {ddxFiles.Length} files.");
                return;
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Input file or directory not found: {inputPath}");
                return;
            }
            
            string outputPath = args.Length > 1 ? args[1] : Path.ChangeExtension(inputPath, ".dds");

            try
            {
                var parser = new DdxParser();
                parser.ConvertDdxToDds(inputPath, outputPath);
                Console.WriteLine($"Successfully converted {inputPath} to {outputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }

    public class DdxParser
    {
        private const uint MAGIC_3XDO = 0x4F445833; // '3XDO'

        public void ConvertDdxToDds(string inputPath, string outputPath)
        {
            using (var reader = new BinaryReader(File.OpenRead(inputPath)))
            {
                uint magic = reader.ReadUInt32();
                
                if (magic != MAGIC_3XDO)
                {
                    throw new InvalidDataException($"Invalid DDX magic: 0x{magic:X8}. Expected 3XDO.");
                }

                ConvertDdxToDds(reader, outputPath);
            }
        }

        private void ConvertDdxToDds(BinaryReader reader, string outputPath)
        {
            // Read priority bytes (used for degradation)
            byte priorityL = reader.ReadByte();
            byte priorityC = reader.ReadByte();
            byte priorityH = reader.ReadByte();
            
            // Read version
            ushort version = reader.ReadUInt16();

            if (version < 3)
            {
                throw new NotSupportedException($"DDX version {version} is not supported. Need version >= 3");
            }

            // After reading version, we're at offset 0x09
            // Height is at offset 0x0E, width is at offset 0x3C
            
            // Skip to 0x0E (5 bytes: 0x09 to 0x0E)
            reader.ReadBytes(5);
            
            // Read height at 0x0E
            ushort height = reader.ReadUInt16();
            
            // Now at 0x10, read 52 bytes of texture header (to 0x44)
            byte[] textureHeader = reader.ReadBytes(52);
            
            // Width is at absolute offset 0x3C = 0x10 + 0x2C (44 bytes into header)
            ushort width = BitConverter.ToUInt16(textureHeader, 0x2C);
            
            var texture = ParseD3DTextureHeader(textureHeader, width, height);

            // For 3XDO files, the texture data starts immediately after the header
            // There are no separate size fields - just read all remaining data
            long currentPos = reader.BaseStream.Position;
            long fileSize = reader.BaseStream.Length;
            uint remainingBytes = (uint)(fileSize - currentPos);
            
            // Read all texture data
            byte[] mainData = reader.ReadBytes((int)remainingBytes);
            
            // Calculate total expected size: atlas (2x resolution) + linear mips
            uint atlasSize = (uint)CalculateMipSize(width, height, texture.DataFormat);
            uint linearDataSize = CalculateMainDataSize(width, height, texture.DataFormat, CalculateMipLevels(width, height));
            
            //// Check if data is XCompress compressed (starts with 0xFF or 0x0F)
            //if (mainData.Length > 0 && (mainData[0] == 0xFF || mainData[0] == 0x0F))
            //{
            //    Console.WriteLine($"Detected XCompress compression, decompressing...");
                
                // Decompress the first chunk (256x256 main texture or atlas)
                byte[] compressedData = mainData;
                byte[] firstChunk = DecompressXMemCompress(compressedData, atlasSize, out int firstChunkCompressedSize);
                Console.WriteLine($"First chunk: consumed {firstChunkCompressedSize} compressed bytes, got {firstChunk.Length} decompressed bytes");
                
                // Check if there's a second compressed chunk
                int offset = firstChunkCompressedSize;
                if (offset < compressedData.Length && (compressedData[offset] == 0xFF || compressedData[offset] == 0x0F))
                {
                    // Decompress second chunk (linear mip data)
                    byte[] remainingCompressed = new byte[compressedData.Length - offset];
                    Array.Copy(compressedData, offset, remainingCompressed, 0, remainingCompressed.Length);
                    
                    byte[] secondChunk = DecompressXMemCompress(remainingCompressed, linearDataSize, out int secondChunkCompressedSize);
                    Console.WriteLine($"Second chunk: consumed {secondChunkCompressedSize} compressed bytes, got {secondChunk.Length} decompressed bytes");
                    
                    // Combine both chunks
                    mainData = new byte[firstChunk.Length + secondChunk.Length];
                    Array.Copy(firstChunk, 0, mainData, 0, firstChunk.Length);
                    Array.Copy(secondChunk, 0, mainData, firstChunk.Length, secondChunk.Length);
                    Console.WriteLine($"Combined {firstChunk.Length} + {secondChunk.Length} = {mainData.Length} bytes total");
                }
                else
                {
                    // Only one chunk
                    mainData = firstChunk;
                }
                
                // Save raw combined data for analysis
                string rawPath = outputPath.Replace(".dds", "_raw.bin");
                File.WriteAllBytes(rawPath, mainData);
                Console.WriteLine($"Saved raw combined data to {rawPath}");
            //}
            //else
            //{
            //    // Not compressed
            //    string rawPath = outputPath.Replace(".dds", "_raw.bin");
            //    File.WriteAllBytes(rawPath, mainData);
            //    Console.WriteLine($"Saved raw data to {rawPath}");
            //}
            
            byte[] chunk1 = new byte[atlasSize];
            byte[] chunk2 = new byte[atlasSize];
            Array.Copy(mainData, 0, chunk1, 0, (int)atlasSize);
            Array.Copy(mainData, (int)atlasSize, chunk2, 0, (int)atlasSize);
            
            Console.WriteLine($"Chunk 1: {chunk1.Length} bytes, Chunk 2: {chunk2.Length} bytes");
            
            // Untile both chunks
            byte[] untiledChunk1 = UnswizzleDXTTexture(chunk1, width, height, texture.DataFormat);
            byte[] untiledChunk2 = UnswizzleDXTTexture(chunk2, width, height, texture.DataFormat);
            
            Console.WriteLine($"Untiled both chunks to {untiledChunk1.Length} and {untiledChunk2.Length} bytes");
            
            // Chunk 2 is the main surface
            // Chunk 1 contains mip atlas
            uint mainSurfaceSize = (uint)CalculateMipSize(width, height, texture.DataFormat);

            // Combine: main surface from chunk2 + mips from unpacked chunk1
            byte[] mips = UnpackMipAtlas(untiledChunk1, width, height, texture.DataFormat);
            Console.WriteLine($"Extracted {mips.Length} bytes of mips from chunk 1");
            
            byte[] linearData = new byte[mainSurfaceSize + mips.Length];
            Array.Copy(untiledChunk2, 0, linearData, 0, (int)mainSurfaceSize);
            Array.Copy(mips, 0, linearData, (int)mainSurfaceSize, mips.Length);
            
            Console.WriteLine($"Combined {mainSurfaceSize} bytes main surface + {mips.Length} bytes mips = {linearData.Length} total");
            
            // No tail data in first tested file
            byte[] tailData = null;

            // Update texture info with 256x256 dimensions
            texture.MipLevels = CalculateMipLevels(width, height);
            
            // Convert to DDS and write with full mip chain
            WriteDdsFile(outputPath, texture, linearData, tailData);
        }
        
        private uint ReadBigEndianUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   (uint)data[offset + 3];
        }
        
        private byte[] DecompressXMemCompress(byte[] compressedData, uint uncompressedSize, out int bytesConsumed)
        {
            // Xbox 360 DDX files may contain more data than the atlas size suggests
            // Allocate extra space for potential linear mip data
            byte[] decompressedData = new byte[uncompressedSize * 2]; // Double the buffer to be safe
            
            using (var context = new DecompressionContext())
            {
                int compressedLen = compressedData.Length;
                int decompressedLen = decompressedData.Length;
                
                ErrorCode result = context.Decompress(
                    compressedData, 0, ref compressedLen,
                    decompressedData, 0, ref decompressedLen);

                if (result != ErrorCode.None)
                {
                    throw new Exception($"XMemCompress decompression failed: {result}");
                }
                
                Console.WriteLine($"Decompressed {compressedLen} -> {decompressedLen} bytes");
                bytesConsumed = compressedLen;
                
                // Trim to actual decompressed size
                if (decompressedLen < decompressedData.Length)
                {
                    Array.Resize(ref decompressedData, decompressedLen);
                }
            }

            return decompressedData;
        }
        
        private void WriteSkyrimDds(string outputPath, uint width, uint height, byte[] textureData, byte[] originalHeader)
        {
            using (var writer = new BinaryWriter(File.Create(outputPath)))
            {
                // Write DDS magic
                writer.Write(0x20534444); // 'DDS '
                
                // Write DDS_HEADER
                writer.Write(124); // dwSize
                
                // dwFlags - required fields
                uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // CAPS, HEIGHT, WIDTH, PIXELFORMAT
                if (textureData.Length > 0)
                    flags |= 0x80000; // LINEARSIZE
                writer.Write(flags);
                
                writer.Write(height); // dwHeight
                writer.Write(width);  // dwWidth
                
                // dwPitchOrLinearSize - for DXT5: max(1, ((width+3)/4)) * block_size * height
                uint linearSize = Math.Max(1, (width + 3) / 4) * 16 * Math.Max(1, (height + 3) / 4);
                writer.Write(linearSize);
                
                writer.Write(0); // dwDepth
                writer.Write(1); // dwMipMapCount
                
                // dwReserved1[11]
                for (int i = 0; i < 11; i++)
                    writer.Write(0);
                
                // DDS_PIXELFORMAT (32 bytes)
                writer.Write(32); // dwSize
                writer.Write(0x4); // dwFlags (FOURCC)
                writer.Write(0x35545844); // dwFourCC ('DXT5')
                writer.Write(0); // dwRGBBitCount
                writer.Write(0); // dwRBitMask
                writer.Write(0); // dwGBitMask
                writer.Write(0); // dwBBitMask
                writer.Write(0); // dwABitMask
                
                // dwCaps
                writer.Write(0x1000); // DDSCAPS_TEXTURE
                
                // dwCaps2, dwCaps3, dwCaps4
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                
                // dwReserved2
                writer.Write(0);
                
                // Write texture data
                writer.Write(textureData);
            }
        }

        private byte[] ReadTextureData(BinaryReader reader, uint compressedSize, uint uncompressedSize, bool isCompressed)
        {
            if (compressedSize == 0)
                return new byte[0];

            byte[] compressedData = reader.ReadBytes((int)compressedSize);

            if (!isCompressed)
            {
                return compressedData;
            }

            // Decompress using XMemDecompress
            byte[] decompressedData = new byte[uncompressedSize];
            
            //using (var context = new DecompressionContext())
            //{
            //    int decompressedBytes = context.Decompress(
            //        compressedData, 0, (int)compressedSize,
            //        decompressedData, 0, (int)uncompressedSize);
//
            //    if (decompressedBytes != uncompressedSize)
            //    {
            //        Console.WriteLine($"Warning: Decompressed {decompressedBytes} bytes, expected {uncompressedSize}");
            //    }
            //}

            return decompressedData;
        }

        private D3DTextureInfo ParseD3DTextureHeader(byte[] header, ushort width, ushort height)
        {
            // Xbox 360 D3D texture header structure
            // Dimensions are passed separately as they're at fixed file offsets
            
            var info = new D3DTextureInfo();

            // Set dimensions from parameters
            info.Width = width;
            info.Height = height;
            
            // Based on Common and MipFlush being first 8 bytes,
            // GPUTEXTURE_FETCH_CONSTANT should be at offset 8
            uint[] formatDwords = new uint[6];
            for (int i = 0; i < 6; i++)
            {
                formatDwords[i] = BitConverter.ToUInt32(header, 8 + i * 4);
            }
            
            uint dword0 = formatDwords[0];
            uint dword3 = formatDwords[3];

            // The format appears to be in DWORD[3] byte 0 (bits 0-7)
            // Based on actual file analysis
            info.DataFormat = dword3 & 0xFF;
            if (info.DataFormat != 0x82)
            {
                throw new NotSupportedException($"Unsupported texture format: 0x{info.DataFormat:X2}");
            }
            info.Endian = (dword0 >> 26) & 0x3;
            info.Tiled = ((dword0 >> 19) & 1) != 0;

            // Determine format
            info.Format = GetDxgiFormat(info.DataFormat);
            
            // Calculate mip levels from dimensions
            info.MipLevels = CalculateMipLevels(info.Width, info.Height);
            
            // Calculate main data size (before mip tail)
            info.MainDataSize = CalculateMainDataSize(info.Width, info.Height, info.DataFormat, info.MipLevels);

            return info;
        }

        private uint GetDxgiFormat(uint gpuFormat)
        {
            // Map Xbox 360 GPU texture formats to D3D formats
            // The format codes found in DDX files appear to be different from standard GPUTEXTUREFORMAT
            // Based on actual file analysis:
            // 0x82 appears in 256x256 DXT1 textures
            // 0x88 appears in 1024x256 textures
            
            return gpuFormat switch
            {
                0x52 => 0x31545844, // DXT1 (standard GPUTEXTUREFORMAT_DXT1 = 0x12, but in DDX it's 0x52?)
                0x53 => 0x33545844, // DXT3  
                0x54 => 0x35545844, // DXT5
                0x82 => 0x31545844, // DXT1 variant seen in files
                0x86 => 0x31545844, // DXT1 variant
                0x88 => 0x35545844, // DXT5 variant seen in files
                0x12 => 0x31545844, // GPUTEXTUREFORMAT_DXT1
                0x13 => 0x33545844, // GPUTEXTUREFORMAT_DXT2/3
                0x14 => 0x35545844, // GPUTEXTUREFORMAT_DXT4/5
                0x06 => 0x18280046, // GPUTEXTUREFORMAT_8_8_8_8 -> A8R8G8B8
                0x04 => 0x28280044, // GPUTEXTUREFORMAT_5_6_5 -> R5G6B5
                _ => 0x31545844 // Default to DXT1
            };
        }

        private uint CalculateMipLevels(uint width, uint height)
        {
            uint levels = 1;
            uint w = width;
            uint h = height;
            
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                levels++;
            }
            
            return levels;
        }

        private uint CalculateMainDataSize(uint width, uint height, uint format, uint mipLevels)
        {
            uint totalSize = 0;
            uint w = width;
            uint h = height;
            
            for (int i = 0; i < mipLevels; i++)
            {
                uint mipSize = CalculateMipSize(w, h, format);
                totalSize += mipSize;
                
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
            
            return totalSize;
        }

        private uint CalculateMipSize(uint width, uint height, uint format)
        {
            // Calculate size based on format
            switch (format)
            {
                case 0x52: // DXT1
                case 0x82: // DXT1 variant
                case 0x86: // DXT1 variant
                case 0x12: // GPUTEXTUREFORMAT_DXT1
                    return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8;
                
                case 0x53: // DXT3
                case 0x54: // DXT5
                case 0x88: // DXT5 variant
                case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                    return Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;
                
                case 0x06: // A8R8G8B8 - 32 bits per pixel
                    return width * height * 4;
                
                case 0x04: // R5G6B5 - 16 bits per pixel
                    return width * height * 2;
                
                default:
                    return width * height * 4; // Default to 32bpp
            }
        }
        
        private int CalculateMipSize(int width, int height, uint format)
        {
            return (int)CalculateMipSize((uint)width, (uint)height, format);
        }

        private void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] mainData, byte[] tailData)
        {
            using (var writer = new BinaryWriter(File.Create(outputPath)))
            {
                // Write DDS header
                WriteDdsHeader(writer, texture);
                
                // Write texture data
                writer.Write(mainData);
                
                if (tailData != null && tailData.Length > 0)
                {
                    writer.Write(tailData);
                }
            }
        }

        private void WriteDdsHeader(BinaryWriter writer, D3DTextureInfo texture)
        {
            // DDS magic
            writer.Write(0x20534444); // "DDS "

            // DDS_HEADER
            writer.Write(124); // dwSize
            
            uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
            if (texture.MipLevels > 1)
                flags |= 0x20000; // DDSD_MIPMAPCOUNT
            
            writer.Write(flags); // dwFlags
            writer.Write(texture.Height); // dwHeight
            writer.Write(texture.Width); // dwWidth
            
            uint pitch = CalculatePitch(texture.Width, texture.DataFormat);
            writer.Write(pitch); // dwPitchOrLinearSize
            
            writer.Write(0); // dwDepth
            writer.Write(texture.MipLevels); // dwMipMapCount
            
            // dwReserved1[11]
            for (int i = 0; i < 11; i++)
                writer.Write(0);

            // DDS_PIXELFORMAT
            WriteDdsPixelFormat(writer, texture.DataFormat);

            // dwCaps
            uint caps = 0x1000; // DDSCAPS_TEXTURE
            if (texture.MipLevels > 1)
                caps |= 0x400000 | 0x8; // DDSCAPS_MIPMAP | DDSCAPS_COMPLEX
            
            writer.Write(caps);
            writer.Write(0); // dwCaps2
            writer.Write(0); // dwCaps3
            writer.Write(0); // dwCaps4
            writer.Write(0); // dwReserved2
        }

        private void WriteDdsPixelFormat(BinaryWriter writer, uint format)
        {
            writer.Write(32); // dwSize
            
            // Determine if this is a compressed format
            bool isCompressed = format == 0x12 || format == 0x13 || format == 0x14 ||
                               format == 0x52 || format == 0x53 || format == 0x54 ||
                               format == 0x82 || format == 0x86 || format == 0x88;
            
            if (isCompressed)
            {
                writer.Write(0x4); // dwFlags = DDPF_FOURCC
                
                // Write FourCC
                uint fourCC = format switch
                {
                    0x12 or 0x52 or 0x82 or 0x86 => 0x31545844, // "DXT1"
                    0x13 or 0x53 => 0x33545844, // "DXT3"
                    0x14 or 0x54 or 0x88 => 0x35545844, // "DXT5"
                    _ => 0
                };
                writer.Write(fourCC);
                
                writer.Write(0); // dwRGBBitCount
                writer.Write(0); // dwRBitMask
                writer.Write(0); // dwGBitMask
                writer.Write(0); // dwBBitMask
                writer.Write(0); // dwABitMask
            }
            else
            {
                // Uncompressed format
                writer.Write(0x41); // dwFlags = DDPF_RGB | DDPF_ALPHAPIXELS
                writer.Write(0); // dwFourCC
                
                if (format == 0x06) // A8R8G8B8
                {
                    writer.Write(32); // dwRGBBitCount
                    writer.Write(0x00FF0000u); // dwRBitMask
                    writer.Write(0x0000FF00u); // dwGBitMask
                    writer.Write(0x000000FFu); // dwBBitMask
                    writer.Write(0xFF000000u); // dwABitMask
                }
                else if (format == 0x04) // R5G6B5
                {
                    writer.Write(16); // dwRGBBitCount
                    writer.Write(0xF800); // dwRBitMask
                    writer.Write(0x07E0); // dwGBitMask
                    writer.Write(0x001F); // dwBBitMask
                    writer.Write(0); // dwABitMask
                }
                else
                {
                    // Default to A8R8G8B8
                    writer.Write(32);
                    writer.Write(0x00FF0000u);
                    writer.Write(0x0000FF00u);
                    writer.Write(0x000000FFu);
                    writer.Write(0xFF000000u);
                }
            }
        }

        private uint CalculatePitch(uint width, uint format)
        {
            switch (format)
            {
                case 0x12: // DXT1
                    return Math.Max(1, (width + 3) / 4) * 8;
                
                case 0x13: // DXT3
                case 0x14: // DXT5
                    return Math.Max(1, (width + 3) / 4) * 16;
                
                case 0x06: // A8R8G8B8
                    return width * 4;
                
                case 0x04: // R5G6B5
                    return width * 2;
                
                default:
                    return width * 4;
            }
        }
        
        private byte[] UnswizzleDXTTexture(byte[] src, int width, int height, uint format)
        {
            // Determine block size based on format
            int blockSize;
            switch (format)
            {
                case 0x52: // DXT1
                case 0x82: // DXT1 variant
                case 0x86: // DXT1 variant
                case 0x12: // GPUTEXTUREFORMAT_DXT1
                    blockSize = 8;
                    break;
                    
                case 0x53: // DXT3
                case 0x54: // DXT5
                case 0x88: // DXT5 variant
                case 0x13: // GPUTEXTUREFORMAT_DXT2/3
                case 0x14: // GPUTEXTUREFORMAT_DXT4/5
                    blockSize = 16;
                    break;
                    
                default:
                    return src; // Unknown format, return as-is
            }
            
            int blocksWide = width / 4;
            int blocksHigh = height / 4;
            byte[] dst = new byte[src.Length];
            
            // Xbox 360 tiling algorithm from Xenia emulator
            // Bytes per pixel (log2) - for DXT blocks
            uint log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));
            
            for (int y = 0; y < blocksHigh; y++)
            {
                uint inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);
                
                for (int x = 0; x < blocksWide; x++)
                {
                    uint inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                    inputOffset >>= (int)log2Bpp;
                    
                    int srcOffset = (int)(inputOffset * blockSize);
                    int dstOffset = (y * blocksWide + x) * blockSize;
                    
                    if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                    {
                        // Copy block and fix endianness for each 16-bit word
                        for (int i = 0; i < blockSize; i += 2)
                        {
                            // Xbox 360 is big-endian, swap bytes
                            dst[dstOffset + i] = src[srcOffset + i + 1];
                            dst[dstOffset + i + 1] = src[srcOffset + i];
                        }
                    }
                }
            }
            
            return dst;
        }
        
        // Xbox 360 tiling functions from Xenia emulator
        // https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
        private uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
        {
            uint macro = (y / 32 * (width / 32)) << (int)(log2Bpp + 7);
            uint micro = (y & 6) << 2 << (int)log2Bpp;
            return macro + ((micro & ~0xFu) << 1) + (micro & 0xF) +
                   ((y & 8) << (int)(3 + log2Bpp)) + ((y & 1) << 4);
        }
        
        private uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
        {
            uint macro = (x / 32) << (int)(log2Bpp + 7);
            uint micro = (x & 7) << (int)log2Bpp;
            uint offset = baseOffset + macro + ((micro & ~0xFu) << 1) + (micro & 0xF);
            return ((offset & ~0x1FFu) << 3) + ((offset & 0x1C0) << 2) + (offset & 0x3F) +
                   ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6);
        }
        
        private byte[] UnpackMipAtlas(byte[] atlasData, int width, int height, uint format)
        {
            int blockSize = CalculateMipSize(4, 4, format);
            int atlasWidthInBlocks = width / 4;
            
            // Actual texture is half the atlas size
            int actualWidth = width / 2;
            int actualHeight = height / 2;
            
            // Calculate total size needed for all mips linearly packed
            uint totalSize = CalculateMainDataSize((uint)actualWidth, (uint)actualHeight, format, CalculateMipLevels((uint)actualWidth, (uint)actualHeight));
            byte[] output = new byte[totalSize];
            int outputOffset = 0;
            
            // Mip positions (based on 256x256 texture
            var mipPositions = new (int x, int y, int w, int h)[]
            {
                (0,                      0,                        width / 2,   height / 2),     // Mip 0: 128x128 at (0,0)
                (width / 2,              0,                        width / 4,   height / 4),     // Mip 1: 64x64 at (128,0)
                (0,                      height / 2,               width / 8,   height / 8),     // Mip 2: 32x32 at (0,128)
                (width / 2 + width / 16, height / 2,               width / 16,  height / 16),    // Mip 3: 16x16 at (144,128)
                (width / 2 + width / 32, height / 2,               width / 32,  height / 32),    // Mip 4: 8x8 at (136,128)
                (width / 2 + width / 64, height / 2,               width / 64,  height / 64),    // Mip 5: 4x4 at (132,128)
                (width / 2,              height / 2 + height / 32, width / 128, height / 128),   // Mip 6: 2x2 at (128,136)
                (width / 2,              height / 2 + height / 64, width / 256, height / 256),   // Mip 7: 1x1 at (128,132)
            };

            for (int mipLevel = 0; mipLevel < mipPositions.Length; mipLevel++)
            {
                var (mipX, mipY, mipWidth, mipHeight) = mipPositions[mipLevel];

                if (mipWidth < 4) mipWidth = 4;
                if (mipHeight < 4) mipHeight = 4;

                Console.WriteLine($"Extracting mip {mipLevel}: {mipWidth}x{mipHeight} from atlas position ({mipX * 4}, {mipY * 4})");
                
                // Extract this mip from the atlas
                for (int by = 0; by < mipHeight / 4; by++)
                {
                    for (int bx = 0; bx < mipWidth / 4; bx++)
                    {
                        int srcBlockX = mipX + bx;
                        int srcBlockY = mipY + by;
                        int srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;
                        
                        if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                        {
                            Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                        }
                        
                        outputOffset += blockSize;
                    }
                }
            }
            
            return output;
        }
    }

    public class D3DTextureInfo
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public uint Format { get; set; }
        public uint DataFormat { get; set; }
        public uint MipLevels { get; set; }
        public uint Pitch { get; set; }
        public bool Tiled { get; set; }
        public uint Endian { get; set; }
        public uint MainDataSize { get; set; }
    }
}
