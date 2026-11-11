using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cummy.Compositor;

/// One attach point (bind point) of a skeleton's .eid: a bone name, an id VFX refer to, and an offset from that bone.
public sealed record EidEntry(string Name, uint Id, float X, float Y, float Z, float RotX, float RotY, float RotZ);

/// A race's attach-point file (`chara/human/cXXXX/skeleton/base/b0001/eid_cXXXXb0001.eid`): `"die\0"`, version
/// `"2101"`, an entry count and 4 padding bytes, then 64-byte entries (32-byte name, id, position, rotation, 4 padding
/// bytes). Cummy adds the creampie drip's attach points to the wearer's own race's file instead of replacing it, so
/// other mods' attach points keep working.
public static class EidFile
{
    private const int HeaderSize = 16;
    private const int EntrySize = 64;
    private const int NameSize = 32;
    private static readonly byte[] Magic = "die\0"u8.ToArray();

    /// The attach points the creampie drip needs (from Dripping Pussy VFX by Tirda Frye): IVCS's pussy and anus bones,
    /// and the right forearm.
    /// Values are the original file's exact floats, in its order.
    public static readonly IReadOnlyList<EidEntry> DripPoints =
    [
        new("iv_omanko", 84, 0, 0, 0, Bits(0xbf02d0e5), Bits(0xbfc91687), Bits(0x40488312)),
        new("j_ude_b_r", 83, 0, 0, 0, 0, 0, 0),
        new("iv_koumon", 85, 0, 0, 0, Bits(0xbf02d0bb), Bits(0xbfc916b8), Bits(0x4048832a)),
    ];

    private static float Bits(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    /// The header's version field, kept as read (null = not an .eid).
    public static (byte[] Version, List<EidEntry> Entries)? Parse(byte[] data)
    {
        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual(Magic)) return null;
        var count = BitConverter.ToUInt32(data, 8);
        if (data.Length < HeaderSize + count * EntrySize) return null;
        var entries = new List<EidEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var o = HeaderSize + i * EntrySize;
            var nameBytes = data.AsSpan(o, NameSize);
            var end = nameBytes.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]);
            float F(int k) => BitConverter.ToSingle(data, o + NameSize + 4 + k * 4);
            entries.Add(new EidEntry(name, BitConverter.ToUInt32(data, o + NameSize), F(0), F(1), F(2), F(3), F(4), F(5)));
        }
        return (data[4..8], entries);
    }

    public static byte[] Write(byte[] version, IReadOnlyList<EidEntry> entries)
    {
        using var ms = new MemoryStream(HeaderSize + entries.Count * EntrySize);
        using var w = new BinaryWriter(ms);
        w.Write(Magic);
        w.Write(version);
        w.Write((uint)entries.Count);
        w.Write(0u);
        foreach (var e in entries)
        {
            var name = new byte[NameSize];
            Encoding.ASCII.GetBytes(e.Name, name);
            w.Write(name);
            w.Write(e.Id);
            foreach (var f in new[] { e.X, e.Y, e.Z, e.RotX, e.RotY, e.RotZ }) w.Write(f);
            w.Write(0u);
        }
        return ms.ToArray();
    }

    /// <paramref name="data"/> with every point of <paramref name="add"/> whose id it lacks appended; null when it isn't
    /// an .eid. Points it already has (by id) are left as they are.
    public static byte[]? AddPoints(byte[] data, IReadOnlyList<EidEntry> add)
    {
        if (Parse(data) is not var (version, entries)) return null;
        var missing = add.Where(p => entries.All(e => e.Id != p.Id)).ToList();
        if (missing.Count == 0) return data;
        entries.AddRange(missing);
        return Write(version, entries);
    }
}
