using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Cummy.Compositor;

/// One corner of a face triangle: where it sits on the face texture and in the face-paint (decal) layout.
public readonly record struct FaceCorner(float TexU, float TexV, float DecalU, float DecalV)
{
    /// Vertices the face paint never reaches carry (-1, 2).
    public bool HasDecal => !(DecalU == -1f && DecalV == 2f);
}

/// The skin triangles of a face model (`*_fac.mdl`, LOD 0, meshes whose material is a `_fac_` one), enough to map
/// face-paint-layout art onto the face texture. A minimal reader for model versions 5 and 6: the Lumina bundled with
/// Dalamud cannot parse v6 (Dawntrail) models.
public sealed class FaceModel
{
    private const uint Version5 = 0x01000005;
    private const uint Version6 = 0x01000006;
    private const int UsageUv = 4;

    /// Corners, three per triangle.
    public FaceCorner[] Corners { get; }
    public int MeshCount { get; }
    public int VertexCount { get; }
    public int TriangleCount => Corners.Length / 3;

    private FaceModel(FaceCorner[] corners, int meshes, int vertices)
    {
        Corners = corners;
        MeshCount = meshes;
        VertexCount = vertices;
    }

    /// Null (with a reason) when the file is not a model this reader understands or has no face skin mesh.
    public static FaceModel? Read(byte[] b, out string? error)
    {
        try
        {
            return Parse(b, out error);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException or IndexOutOfRangeException)
        {
            error = "the model file is truncated or malformed";
            return null;
        }
    }

    private static FaceModel? Parse(byte[] b, out string? error)
    {
        var version = U32(b, 0);
        if (version is not (Version5 or Version6))
        {
            error = $"unsupported model version 0x{version:x}";
            return null;
        }
        var stackSize = (int)U32(b, 4);
        var declCount = U16(b, 12);
        var vertexOffset0 = (int)U32(b, 0x10);
        var indexOffset0 = (int)U32(b, 0x1C);

        // Vertex declarations: 17 elements of 8 bytes each, ended by stream 255.
        var uvElements = new List<(int Stream, int Offset, int Type, int UsageIndex)>[declCount];
        for (int d = 0; d < declCount; d++)
        {
            uvElements[d] = new();
            for (int e = 0; e < 17; e++)
            {
                int at = 0x44 + d * 136 + e * 8;
                if (b[at] == 255) break;
                if (b[at + 3] == UsageUv) uvElements[d].Add((b[at], b[at + 1], b[at + 2], b[at + 4]));
            }
            uvElements[d].Sort((x, y) => x.UsageIndex.CompareTo(y.UsageIndex));
        }

        int p = 0x44 + stackSize;
        var stringSize = (int)U32(b, p + 4);
        p += 8;
        var stringsAt = p;
        p += stringSize;

        // Model header.
        var meshCount = U16(b, p + 4);
        var attributeCount = U16(b, p + 6);
        var submeshCount = U16(b, p + 8);
        var materialCount = U16(b, p + 10);
        var elementIdCount = U16(b, p + 24);
        var terrainShadowMeshCount = b[p + 26];
        var flags2 = b[p + 27];
        var terrainShadowSubmeshCount = U16(b, p + 38);
        p += 56;

        p += elementIdCount * 32;
        var lod0MeshIndex = U16(b, p);
        var lod0MeshCount = U16(b, p + 2);
        p += 3 * 60;
        if ((flags2 & 0x10) != 0) p += 3 * 40;   // extra LODs

        var meshesAt = p;
        p += meshCount * 36;
        p += attributeCount * 4 + terrainShadowMeshCount * 20 + submeshCount * 16 + terrainShadowSubmeshCount * 10;
        var materials = new string[materialCount];
        for (int m = 0; m < materialCount; m++)
            materials[m] = CString(b, stringsAt + (int)U32(b, p + m * 4));

        var corners = new List<FaceCorner>();
        int meshes = 0, vertices = 0;
        for (int m = lod0MeshIndex; m < lod0MeshIndex + lod0MeshCount; m++)
        {
            int at = meshesAt + m * 36;
            var vertexCount = U16(b, at);
            var indexCount = (int)U32(b, at + 4);
            var materialIndex = U16(b, at + 8);
            var startIndex = (int)U32(b, at + 16);
            if (materialIndex >= materials.Length || !materials[materialIndex].Contains("_fac_", StringComparison.Ordinal))
                continue;
            var uvs = uvElements[m];
            if (uvs.Count == 0) continue;

            // Each vertex's UV values in order: texture u, v, then (if present) face-paint u, v.
            var values = new float[vertexCount * 4];
            for (int v = 0; v < vertexCount; v++)
            {
                int n = 0;
                foreach (var uv in uvs)
                {
                    int bufferOffset = (int)U32(b, at + 20 + uv.Stream * 4);
                    int stride = b[at + 32 + uv.Stream];
                    int src = vertexOffset0 + bufferOffset + v * stride + uv.Offset;
                    int count = uv.Type is 1 or 13 ? 2 : 4;
                    for (int c = 0; c < count && n < 4; c++, n++)
                        values[v * 4 + n] = uv.Type is 13 or 14 ? (float)Half(b, src + c * 2) : F32(b, src + c * 4);
                }
                if (n < 4)
                {
                    values[v * 4 + 2] = -1f;   // no face-paint layout on this mesh
                    values[v * 4 + 3] = 2f;
                }
            }

            for (int i = 0; i + 2 < indexCount; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int index = U16(b, indexOffset0 + (startIndex + i + k) * 2);
                    if (index >= vertexCount) throw new ArgumentOutOfRangeException(nameof(b));
                    corners.Add(new FaceCorner(values[index * 4], values[index * 4 + 1], values[index * 4 + 2], values[index * 4 + 3]));
                }
            }
            meshes++;
            vertices += vertexCount;
        }

        if (meshes == 0)
        {
            error = "no face skin mesh";
            return null;
        }
        error = null;
        return new FaceModel(corners.ToArray(), meshes, vertices);
    }

    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    private static float F32(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at, 4));
    private static Half Half(byte[] b, int at) => BinaryPrimitives.ReadHalfLittleEndian(b.AsSpan(at, 2));

    private static string CString(byte[] b, int at)
    {
        int end = Array.IndexOf(b, (byte)0, at);
        return Encoding.UTF8.GetString(b, at, (end < 0 ? b.Length : end) - at);
    }
}
