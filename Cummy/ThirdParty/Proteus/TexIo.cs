// Ported from Proteus's TextureLoader (https://github.com/solona-m/proteus, AGPL-3.0, by Solona):
// .mtrl sampler parsing, .tex sanitising/decoding through Lumina, and the .tex header writer, with the managed
// BCnEncoder path only. Cummy writes BC3 with a mip chain (managed BC7 is ~150x slower).

using System;
using System.IO;
using System.Reflection;
using System.Text;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using CommunityToolkit.HighPerformance;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Structs;
using Cummy.Compositor;

namespace Cummy.ThirdParty.Proteus;

public sealed record MtrlTexturePaths(string? Diffuse, string? Normal, string? Mask);

public sealed record RgbaImage(byte[] Rgba, int Width, int Height);

public static class TexIo
{
    private const uint SamplerIdDiffuse = 0x1E6FEF9Cu;
    private const uint SamplerIdColorMap0 = 0x115306BEu; // Bibo+ / custom body shaders
    private const uint SamplerIdNormal = 0x0C5EC1F1u;
    private const uint SamplerIdMask = 0x8A4E82B6u;
    private const int SamplerStride = 12;

    private const uint FormatB8G8R8A8 = 0x1450u;
    private const uint FormatBc3 = 0x3431u;
    private const int HeaderSize = 80;
    private const int MaxMips = 13;

    /// A file from disk when modded, else from game data.
    public static byte[]? LoadRawFile(IDataManager data, string? diskPath, string gamePath)
    {
        if (diskPath != null && File.Exists(diskPath))
        {
            try { return File.ReadAllBytes(diskPath); }
            catch (Exception ex) { Plugin.Log.Error(ex, $"Failed to read {diskPath}"); }
        }
        try { return data.GetFile(gamePath)?.Data; }
        catch (Exception ex) { Plugin.Log.Error(ex, $"Failed to load {gamePath} from game data"); return null; }
    }

    /// Walk a .mtrl's header → sampler table by hand (v1.3 layout); Lumina reports zero samplers for real
    /// Dawntrail materials. Every step is bounds-checked and bails to "nothing found" rather than throwing.
    public static MtrlTexturePaths ParseMtrlTextures(byte[] b)
    {
        var none = new MtrlTexturePaths(null, null, null);
        const int MtrlHeaderSize = 0x10;
        if (b.Length < MtrlHeaderSize) return none;

        ushort dataSetSize = BitConverter.ToUInt16(b, 0x06);
        ushort stringTableSize = BitConverter.ToUInt16(b, 0x08);
        byte textureCount = b[0x0C];
        byte uvSetCount = b[0x0D];
        byte colorSetCount = b[0x0E];
        byte additionalSize = b[0x0F];

        int o = MtrlHeaderSize;
        int texTableEnd = o + textureCount * 4;
        if (texTableEnd > b.Length) return none;
        var texOffsets = new ushort[textureCount];
        for (int i = 0; i < textureCount; i++) texOffsets[i] = BitConverter.ToUInt16(b, o + i * 4);

        o = texTableEnd + uvSetCount * 4 + colorSetCount * 4;
        int stringsAt = o;
        if (stringsAt + stringTableSize > b.Length) return none;

        o = stringsAt + stringTableSize + additionalSize + dataSetSize;
        if (o + 12 > b.Length) return none;
        ushort shaderKeyCount = BitConverter.ToUInt16(b, o + 2);
        ushort constantCount = BitConverter.ToUInt16(b, o + 4);
        ushort samplerCount = BitConverter.ToUInt16(b, o + 6);

        o += 12 + shaderKeyCount * 8 + constantCount * 8;
        if (o + samplerCount * SamplerStride > b.Length) return none;

        string? diffuse = null, normal = null, mask = null;
        for (int i = 0; i < samplerCount; i++)
        {
            int s = o + i * SamplerStride;
            uint samplerId = BitConverter.ToUInt32(b, s);
            byte texIndex = b[s + 8];
            if (texIndex >= texOffsets.Length) continue;

            int at = stringsAt + texOffsets[texIndex];
            if (at >= stringsAt + stringTableSize) continue;
            int end = at;
            while (end < stringsAt + stringTableSize && b[end] != 0) end++;
            var path = Encoding.UTF8.GetString(b, at, end - at);
            if (path.Length == 0) continue;
            if (path.StartsWith("--", StringComparison.Ordinal)) path = path[2..];

            if (samplerId is SamplerIdDiffuse or SamplerIdColorMap0) diffuse = path;
            else if (samplerId == SamplerIdNormal) normal = path;
            else if (samplerId == SamplerIdMask) mask = path;
        }
        return new MtrlTexturePaths(diffuse, normal, mask);
    }

    /// Decode a .tex (disk or game data) to RGBA8, mip 0. Null on failure.
    public static RgbaImage? LoadTex(IDataManager data, string? diskPath, string gamePath)
    {
        try
        {
            if (diskPath != null && File.Exists(diskPath))
            {
                var sanitized = SanitizeTexBytes(File.ReadAllBytes(diskPath));
                return ConvertTex(LoadLuminaFileFromBytes<TexFile>(sanitized));
            }
            var tex = data.GetFile<TexFile>(gamePath);
            return tex == null ? null : ConvertTex(tex);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to decode texture {diskPath ?? gamePath}");
            return null;
        }
    }

    private static RgbaImage ConvertTex(TexFile tex)
    {
        int w = tex.Header.Width;
        int h = tex.Header.Height;
        var bgra = tex.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8).RawData;
        var rgba = new byte[bgra.Length];
        OverlayBlend.ParallelPixels(0, bgra.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = bgra[i + 3];
            }
        });
        return new RgbaImage(rgba, w, h);
    }

    // Replicates GameData.GetFileFromDisk<T>() without a GameData instance; Data and Reader have internal setters.
    private static readonly PropertyInfo PropData = typeof(FileResource).GetProperty("Data", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly PropertyInfo PropReader = typeof(FileResource).GetProperty("Reader", BindingFlags.Public | BindingFlags.Instance)!;

    private static T LoadLuminaFileFromBytes<T>(byte[] bytes) where T : FileResource
    {
        var file = Activator.CreateInstance<T>();
        PropData.SetValue(file, bytes);
        PropReader.SetValue(file, new LuminaBinaryReader(bytes, PlatformId.Win32));
        file.LoadFile();
        return file;
    }

    // Some mod tools write MipCount > 1 with zeroed OffsetToSurface slots, which makes Lumina throw. Pre-patch
    // MipCount to the offsets actually populated.
    private static byte[] SanitizeTexBytes(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) return bytes;
        int mipCount = bytes[14] & 0x7F;
        if (mipCount <= 1) return bytes;

        uint prev = BitConverter.ToUInt32(bytes, 28);
        if (prev == 0)
        {
            var p = (byte[])bytes.Clone();
            p[14] = (byte)((p[14] & 0x80) | 1);
            return p;
        }

        uint fmt = BitConverter.ToUInt32(bytes, 4);
        int w = BitConverter.ToUInt16(bytes, 8);
        int h = BitConverter.ToUInt16(bytes, 10);
        long mip0 = Mip0ByteSize(fmt, w, h);
        uint surf1 = BitConverter.ToUInt32(bytes, 32);
        if (mip0 > 0 && surf1 != 0 && surf1 < prev + mip0)
        {
            var p = (byte[])bytes.Clone();
            p[14] = (byte)((p[14] & 0x80) | 1);
            return p;
        }

        int validMips = 1;
        for (int i = 1; i < Math.Min(mipCount, MaxMips); i++)
        {
            uint cur = BitConverter.ToUInt32(bytes, 28 + i * 4);
            if (cur == 0 || cur <= prev || cur >= (uint)bytes.Length) break;
            prev = cur;
            validMips = i + 1;
        }
        if (validMips == mipCount) return bytes;

        var patched = (byte[])bytes.Clone();
        patched[14] = (byte)((patched[14] & 0x80) | (validMips & 0x7F));
        return patched;
    }

    private static long Mip0ByteSize(uint format, int w, int h)
    {
        if (w <= 0 || h <= 0) return 0;
        long px = (long)w * h;
        long blocks = (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4);
        return format switch
        {
            0x1130 or 0x1131 => px,
            0x1440 or 0x1441 => px * 2,
            0x1450 or 0x1451 => px * 4,
            0x2140 => px * 2,
            0x2150 => px * 4,
            0x2460 => px * 8,
            0x2470 => px * 16,
            0x3420 => blocks * 8,
            0x3430 or 0x3431 or 0x6230 or 0x6432 => blocks * 16,
            _ => 0,
        };
    }

    /// Whether <see cref="WriteTex"/> stores this size block-compressed (4-aligned) - and therefore whether the
    /// region-encoding path in the compositor can be used for it.
    public static bool IsBlockCompressible(int width, int height) => width % 4 == 0 && height % 4 == 0;

    /// The mip chain <see cref="WriteTex"/> writes: box-filtered halvings down to 4×4, at most 13 levels, stopping
    /// early when a level is not evenly halvable. Level 0 is <paramref name="image"/> itself.
    public static RgbaImage[] MipChain(RgbaImage image)
    {
        var levels = new System.Collections.Generic.List<RgbaImage> { image };
        var level = image;
        while (levels.Count < MaxMips && level.Width / 2 >= 4 && level.Height / 2 >= 4
               && level.Width % 8 == 0 && level.Height % 8 == 0)
        {
            level = Resample.HalfBox(level);
            levels.Add(level);
        }
        return levels.ToArray();
    }

    /// Write RGBA8 as a BC3 .tex with its <see cref="MipChain"/> (uncompressed B8G8R8A8 single surface when the size
    /// is not 4-aligned). Atomic: written to a temp file, then moved into place.
    public static void WriteTex(RgbaImage image, string outputPath)
    {
        if (IsBlockCompressible(image.Width, image.Height))
        {
            var chain = MipChain(image);
            var surfaces = new byte[chain.Length][];
            for (int i = 0; i < chain.Length; i++) surfaces[i] = EncodeBc3(chain[i]);
            WriteBc3(image.Width, image.Height, surfaces, outputPath);
            return;
        }

        var src = image.Rgba;
        var bgra = new byte[src.Length];
        OverlayBlend.ParallelPixels(0, src.Length, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                bgra[i] = src[i + 2];
                bgra[i + 1] = src[i + 1];
                bgra[i + 2] = src[i];
                bgra[i + 3] = src[i + 3];
            }
        });
        WriteSurfaces(image.Width, image.Height, FormatB8G8R8A8, [bgra], outputPath);
    }

    /// Write an already BC3-encoded mip chain (level 0 first) as a .tex.
    public static void WriteBc3(int width, int height, byte[][] surfaces, string outputPath)
        => WriteSurfaces(width, height, FormatBc3, surfaces, outputPath);

    private static void WriteSurfaces(int width, int height, uint format, byte[][] surfaces, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // 80-byte TexHeader: flags, format, width, height, depth, mip count, then the surface offset table at 28.
        var header = new byte[HeaderSize];
        BitConverter.TryWriteBytes(header.AsSpan(0), 0x00800000u);
        BitConverter.TryWriteBytes(header.AsSpan(4), format);
        BitConverter.TryWriteBytes(header.AsSpan(8), (ushort)width);
        BitConverter.TryWriteBytes(header.AsSpan(10), (ushort)height);
        BitConverter.TryWriteBytes(header.AsSpan(12), (ushort)1);
        header[14] = (byte)surfaces.Length;
        uint offset = HeaderSize;
        for (int i = 0; i < surfaces.Length; i++)
        {
            BitConverter.TryWriteBytes(header.AsSpan(28 + i * 4), offset);
            offset += (uint)surfaces[i].Length;
        }

        var tmp = outputPath + "." + Path.GetRandomFileName() + ".tmp";
        try
        {
            using (var stream = File.Create(tmp))
            {
                stream.Write(header, 0, header.Length);
                foreach (var surface in surfaces)
                    stream.Write(surface, 0, surface.Length);
            }
            File.Move(tmp, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// BC3 (DXT5): full alpha, the same size as BC7, and fast enough to run on every layer change with the
    /// managed encoder. Each 4×4 block is encoded from its own pixels only, so encoding a block-aligned region gives
    /// exactly the blocks a whole-image encode would.
    public static byte[] EncodeBc3(RgbaImage image) => EncodeBc3Region(image, 0, 0, image.Width, image.Height);

    /// BC3 blocks (row-major) for the 4-aligned region (x, y, w, h) of <paramref name="image"/>.
    public static byte[] EncodeBc3Region(RgbaImage image, int x, int y, int w, int h)
    {
        var pixels = new ColorRgba32[w * h];
        var rgba = image.Rgba;
        for (int row = 0; row < h; row++)
        {
            int src = ((y + row) * image.Width + x) * 4, dst = row * w;
            for (int col = 0; col < w; col++, src += 4)
                pixels[dst + col] = new ColorRgba32(rgba[src], rgba[src + 1], rgba[src + 2], rgba[src + 3]);
        }

        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = CompressionQuality.Balanced;
        encoder.OutputOptions.Format = CompressionFormat.Bc3;
        encoder.Options.IsParallel = w * h >= 256 * 256;

        var mem = new ReadOnlyMemory2D<ColorRgba32>(pixels, h, w);
        return encoder.EncodeToRawBytes(mem, 0, out _, out _);
    }
}
