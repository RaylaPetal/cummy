using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Cummy.ThirdParty.Proteus;

namespace Cummy.Compositor;

/// Minimal RGBA8 PNG encoder (filter 0, zlib), for art handed to other tools such as Proteus overlay packs.
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// Writes atomically (temp file, then move).
    public static void Write(RgbaImage image, string path)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = image.Width * 4;
            for (int y = 0; y < image.Height; y++)
            {
                z.WriteByte(0);   // no filter
                z.Write(image.Rgba, y * row, row);
            }
        }

        var tmp = path + "." + Path.GetRandomFileName() + ".tmp";
        using (var file = File.Create(tmp))
        {
            file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // RGBA
            ihdr[10] = ihdr[11] = ihdr[12] = 0;
            Chunk(file, "IHDR", ihdr);
            Chunk(file, "IDAT", raw.GetBuffer().AsSpan(0, (int)raw.Length));
            Chunk(file, "IEND", ReadOnlySpan<byte>.Empty);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
