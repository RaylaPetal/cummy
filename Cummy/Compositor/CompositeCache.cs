using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cummy.ThirdParty.Proteus;

namespace Cummy.Compositor;

/// A part's overlay cropped to the (4-aligned) rectangle its alpha covers, at one texture size.
/// <see cref="Blocks"/> marks, row-major over the crop's 4×4 blocks, which blocks contain any art (alpha &gt; 0):
/// the only blocks this overlay can change.
public sealed record OverlayCrop(byte[] Rgba, int X, int Y, int W, int H, bool[] Blocks);

/// The face a facial overlay is redrawn onto, and its model's stamp (part of the overlay's cache key).
public sealed record FaceMapping(FaceModel Model, string Stamp);

/// What stays expensive between composites, kept until the wearer's skin changes:
/// <list type="bullet">
/// <item>each skin texture decoded, plus its BC3 mip chain encoded once, keyed by the upstream file's stamp;</item>
/// <item>each part's overlay decoded, resized to the skin's size, cut to its stage and cropped to its coverage, keyed
/// by its source's stamp and stage (so changed art is never served from the cache; only the current stage is kept);</item>
/// <item>the wearer's custom images decoded at each skin size, and the label maps (the region map that splits a
/// full-body image, the stage maps that cut staged parts) at each size.</item>
/// </list>
/// A composite then starts from a copy of the cached skin, paints only the parts' rectangles, and re-encodes only
/// the 4×4 blocks that contain art; every other block is copied from the cached skin encoding. That is exact:
/// BC3 encodes each block from its own pixels, the blend kernels leave alpha-0 pixels untouched, and each mip pixel
/// depends only on the 2×2 pixels above it.
/// Not thread-safe: callers hold <see cref="Lock"/>.
public sealed class CompositeCache
{
    private sealed record BaseEntry(string Stamp, RgbaImage Image, byte[][]? Encoded);

    private readonly Dictionary<string, BaseEntry> bases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(BodyPart Part, string File, string Stamp, int W, int H, string Face, int Stage), OverlayCrop?> overlays = new();
    private readonly Dictionary<(string Path, string Stamp, int W, int H), RgbaImage?> images = new();
    private readonly Dictionary<(string Map, int W, int H), byte[]?> regions = new();
    private readonly Dictionary<string, (string Stamp, FaceModel? Model)> faces = new(StringComparer.OrdinalIgnoreCase);
    private readonly string regionMapPath;
    private readonly string faceRegionMapPath;

    public object Lock { get; } = new();

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public bool IsEmpty => bases.Count == 0 && overlays.Count == 0 && images.Count == 0 && regions.Count == 0 && faces.Count == 0;

    public CompositeCache(string regionMapPath, string faceRegionMapPath)
    {
        this.regionMapPath = regionMapPath;
        this.faceRegionMapPath = faceRegionMapPath;
    }

    public void Clear()
    {
        bases.Clear();
        overlays.Clear();
        images.Clear();
        regions.Clear();
        faces.Clear();
    }

    /// The decoded skin for <paramref name="gamePath"/> whose upstream is described by <paramref name="stamp"/>,
    /// with its encoded mip chain when it is block-compressible. Decodes (and encodes) only when the stamp changed.
    public (RgbaImage Image, byte[][]? Encoded)? Base(string gamePath, string stamp, Func<RgbaImage?> decode)
    {
        LastUsedUtc = DateTime.UtcNow;
        if (bases.TryGetValue(gamePath, out var hit) && hit.Stamp == stamp)
            return (hit.Image, hit.Encoded);

        bases.Remove(gamePath);
        var image = decode();
        if (image == null) return null;

        byte[][]? encoded = null;
        if (TexIo.IsBlockCompressible(image.Width, image.Height))
        {
            var chain = TexIo.MipChain(image);
            encoded = new byte[chain.Length][];
            for (int i = 0; i < chain.Length; i++) encoded[i] = TexIo.EncodeBc3(chain[i]);
        }
        bases[gamePath] = new BaseEntry(stamp, image, encoded);
        return (image, encoded);
    }

    /// The face model at <paramref name="gamePath"/> whose file is described by <paramref name="stamp"/>; read only
    /// when the stamp changed. Null when it can't be read (then facial layers are skipped).
    public FaceModel? Face(string gamePath, string stamp, Func<FaceModel?> read)
    {
        LastUsedUtc = DateTime.UtcNow;
        if (faces.TryGetValue(gamePath, out var hit) && hit.Stamp == stamp) return hit.Model;
        var model = read();
        faces[gamePath] = (stamp, model);
        return model;
    }

    /// Frees cached art that a source change made stale: one part's, or for a full image every part split from it.
    /// Memory only - entries are keyed by source stamp, so stale ones are never used.
    public void Forget(OverlayTarget target)
    {
        foreach (var key in overlays.Keys.Where(k => target.Part is { } p ? k.Part == p : k.Part.IsFacial() == target.Face).ToList())
            overlays.Remove(key);
        images.Clear();
    }

    /// <paramref name="part"/>'s <paramref name="file"/> overlay (<see cref="OverlaySources.DiffuseFile"/> or
    /// <see cref="OverlaySources.NormalFile"/>) from <paramref name="source"/> at <paramref name="w"/>×<paramref name="h"/>,
    /// cropped to its coverage; null when it has none or cannot be loaded.
    /// For a facial part, <paramref name="face"/> is the face it is redrawn onto.
    public OverlayCrop? Overlay(BodyPart part, OverlaySource source, string file, int w, int h, FaceMapping? face = null)
    {
        LastUsedUtc = DateTime.UtcNow;
        var stage = source.IsStageCut ? source.Stage : 0;
        var key = (part, file, source.Stamp, w, h, face?.Stamp ?? "", stage);
        if (overlays.TryGetValue(key, out var hit)) return hit;

        // A part only ever shows one stage: drop its other stages' crops of this file and size.
        foreach (var old in overlays.Keys.Where(k => k.Part == part && k.File == file && k.W == w && k.H == h && k.Stage != stage).ToList())
            overlays.Remove(old);
        var crop = LoadCrop(part, source, file, w, h, face);
        overlays[key] = crop;
        return crop;
    }

    private OverlayCrop? LoadCrop(BodyPart part, OverlaySource source, string file, int w, int h, FaceMapping? face)
    {
        if (LoadArt(part, source, file, face == null ? w : 0, face == null ? h : 0) is not { } art) return null;
        // Facial art is in the face-paint layout: redraw it onto this face's own texture layout.
        if (face != null) art = FaceBaker.Bake(face.Model, art, w, h, file == OverlaySources.NormalFile);
        return Crop(art.Rgba, w, h);
    }

    /// The part's art from its source at its own size, split from a full image when that is the source (for
    /// handing to another compositor, e.g. a Proteus overlay pack). Callers hold <see cref="Lock"/>.
    public RgbaImage? Art(BodyPart part, OverlaySource source, string file)
    {
        LastUsedUtc = DateTime.UtcNow;
        return LoadArt(part, source, file, 0, 0);
    }

    /// The part's art from its source at <paramref name="w"/>×<paramref name="h"/> (0×0 = the art's own size),
    /// split from a full image when that is the source, and cut to the source's stage.
    private RgbaImage? LoadArt(BodyPart part, OverlaySource source, string file, int w, int h)
    {
        if (LoadUncut(part, source, file, w, h) is not { } art) return null;
        if (!source.IsStageCut) return art;

        // Stage N: only pixels the shipped art first covers at stages 1..N (0 = the shipped art has no stroke there).
        if (Labels(source.StageMap!, art.Width, art.Height) is not { } stages) return null;
        var rgba = art.Rgba;   // a fresh buffer: shipped art is decoded per call, custom art copied out of the cache
        for (int i = 0; i < stages.Length; i++)
            if (stages[i] == 0 || stages[i] > source.Stage) rgba[i * 4 + 3] = 0;
        return art;
    }

    private RgbaImage? LoadUncut(BodyPart part, OverlaySource source, string file, int w, int h)
    {
        var normal = file == OverlaySources.NormalFile;
        if ((normal ? source.Normal : source.Diffuse) is not { } path) return null;

        if (source.Origin == OverlayOrigin.Shipped)
            return Decode(path, w, h);

        // Custom art: decoded once per size and shared, since a full image feeds several parts.
        if (Image(path, source.Stamp, w, h) is not { } img) return null;
        var rgba = (byte[])img.Rgba.Clone();
        if (normal)
        {
            // A normal map's coverage is its colour image's, as build-assets.py does for the shipped art.
            if (Image(source.Diffuse, source.Stamp, w, h) is not { } colour) return null;
            if (colour.Width != img.Width || colour.Height != img.Height) colour = Resample.To(colour, img.Width, img.Height);
            var c = colour.Rgba;
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = c[i];
        }

        // A part's share of a full image: nothing outside its region (Full Body uses the whole full-body image).
        var map = source.Origin switch
        {
            OverlayOrigin.FullBody when part != BodyPart.FullBody => regionMapPath,
            OverlayOrigin.FullFace => faceRegionMapPath,
            _ => null,
        };
        if (map != null)
        {
            if (Labels(map, img.Width, img.Height) is not { } labels) return null;
            var own = (byte)part;
            for (int i = 0; i < labels.Length; i++)
                if (labels[i] != own) rgba[i * 4 + 3] = 0;
        }
        return new RgbaImage(rgba, img.Width, img.Height);
    }

    private RgbaImage? Image(string path, string stamp, int w, int h)
    {
        var key = (path, stamp, w, h);
        if (!images.TryGetValue(key, out var img))
            images[key] = img = Decode(path, w, h);
        return img;
    }

    /// <paramref name="w"/>×<paramref name="h"/> of 0×0 keeps the image's own size.
    private static RgbaImage? Decode(string path, int w, int h)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var img = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            var full = new RgbaImage(img.Data, img.Width, img.Height);
            return w == 0 ? full : Resample.To(full, w, h);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to load overlay {path}");
            return null;
        }
    }

    /// The label map <paramref name="mapPath"/> at <paramref name="w"/>×<paramref name="h"/> (nearest neighbour:
    /// labels must not blend): one value per pixel, a BodyPart for region maps, a stage for stage maps.
    private byte[]? Labels(string mapPath, int w, int h)
    {
        if (regions.TryGetValue((mapPath, w, h), out var hit)) return hit;
        byte[]? labels = null;
        try
        {
            using var stream = File.OpenRead(mapPath);
            var map = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.Grey);
            labels = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int sy = (int)((long)y * map.Height / h);
                for (int x = 0; x < w; x++)
                    labels[y * w + x] = map.Data[sy * map.Width + (int)((long)x * map.Width / w)];
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to load the label map {mapPath}; parts that need it are skipped.");
        }
        regions[(mapPath, w, h)] = labels;
        return labels;
    }

    /// <paramref name="rgba"/> (<paramref name="w"/>×<paramref name="h"/>) cropped to its non-zero alpha.
    private static OverlayCrop? Crop(byte[] rgba, int w, int h)
    {
        // Bounding box of non-zero alpha, grown to the 4×4 block grid.
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                if (rgba[row + x * 4 + 3] == 0) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }
        if (x1 < 0) return null;

        x0 &= ~3;
        y0 &= ~3;
        x1 = Math.Min(w, (x1 + 4) & ~3);
        y1 = Math.Min(h, (y1 + 4) & ~3);
        int cw = x1 - x0, ch = y1 - y0;
        var crop = new byte[cw * ch * 4];
        for (int row = 0; row < ch; row++)
            Buffer.BlockCopy(rgba, ((y0 + row) * w + x0) * 4, crop, row * cw * 4, cw * 4);

        int bw = cw / 4, bh = ch / 4;
        var blocks = new bool[bw * bh];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
                if (crop[(y * cw + x) * 4 + 3] != 0)
                    blocks[(y / 4) * bw + x / 4] = true;
        return new OverlayCrop(crop, x0, y0, cw, ch, blocks);
    }

    /// Encode <paramref name="image"/> (the cached skin with <paramref name="crops"/> painted in) by copying
    /// <paramref name="baseEncoded"/> and re-encoding only the blocks the crops' art touches, at every mip level.
    public static byte[][] EncodeChanged(RgbaImage image, byte[][] baseEncoded, IReadOnlyList<OverlayCrop> crops)
    {
        var chain = TexIo.MipChain(image);
        var surfaces = new byte[chain.Length][];

        // Level 0: the union of every crop's art blocks.
        int bpr = image.Width / 4, rows = image.Height / 4;
        var dirty = new bool[bpr * rows];
        foreach (var crop in crops)
        {
            int cbw = crop.W / 4, cbh = crop.H / 4, bx0 = crop.X / 4, by0 = crop.Y / 4;
            for (int by = 0; by < cbh; by++)
                for (int bx = 0; bx < cbw; bx++)
                    if (crop.Blocks[by * cbw + bx])
                        dirty[(by0 + by) * bpr + bx0 + bx] = true;
        }

        for (int level = 0; level < chain.Length; level++)
        {
            var img = chain[level];
            int lbpr = img.Width / 4, lrows = img.Height / 4;
            if (level > 0)
            {
                // A block here covers 2×2 blocks of the level above; it changed only if one of them did.
                var next = new bool[lbpr * lrows];
                for (int by = 0; by < lrows; by++)
                    for (int bx = 0; bx < lbpr; bx++)
                    {
                        int px = bx * 2, py = by * 2;
                        bool any = false;
                        for (int dy = 0; dy < 2 && !any; dy++)
                            for (int dx = 0; dx < 2 && !any; dx++)
                            {
                                int x = px + dx, y = py + dy;
                                if (x < bpr && y < rows && dirty[y * bpr + x]) any = true;
                            }
                        next[by * lbpr + bx] = any;
                    }
                dirty = next;
                bpr = lbpr;
                rows = lrows;
            }
            surfaces[level] = (byte[])baseEncoded[level].Clone();
            EncodeDirtyBlocks(img, dirty, surfaces[level]);
        }
        return surfaces;
    }

    /// Re-encode the marked blocks of <paramref name="img"/> into <paramref name="surface"/>. The blocks are packed
    /// side by side into one small image so the encoder runs once; each block encodes from its own pixels, so the
    /// result equals encoding them in place.
    private static void EncodeDirtyBlocks(RgbaImage img, bool[] dirty, byte[] surface)
    {
        int bpr = img.Width / 4;
        var indices = new List<int>();
        for (int i = 0; i < dirty.Length; i++)
            if (dirty[i]) indices.Add(i);
        if (indices.Count == 0) return;

        if (indices.Count * 10 >= dirty.Length * 9)
        {
            // Nearly everything changed: one straight encode is cheaper than packing.
            var all = TexIo.EncodeBc3(img);
            Buffer.BlockCopy(all, 0, surface, 0, all.Length);
            return;
        }

        const int PackedBlocksPerRow = 256;
        int packedCols = Math.Min(indices.Count, PackedBlocksPerRow);
        int packedRows = (indices.Count + PackedBlocksPerRow - 1) / PackedBlocksPerRow;
        int pw = packedCols * 4, ph = packedRows * 4;
        var packed = new byte[pw * ph * 4];
        var src = img.Rgba;
        for (int n = 0; n < indices.Count; n++)
        {
            int bx = indices[n] % bpr, by = indices[n] / bpr;
            int px = (n % PackedBlocksPerRow) * 4, py = (n / PackedBlocksPerRow) * 4;
            for (int row = 0; row < 4; row++)
                Buffer.BlockCopy(src, ((by * 4 + row) * img.Width + bx * 4) * 4, packed, ((py + row) * pw + px) * 4, 16);
        }

        var encoded = TexIo.EncodeBc3(new RgbaImage(packed, pw, ph));
        for (int n = 0; n < indices.Count; n++)
            Buffer.BlockCopy(encoded, n * 16, surface, indices[n] * 16, 16);
    }
}
