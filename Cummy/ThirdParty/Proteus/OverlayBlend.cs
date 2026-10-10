// Ported from Proteus's OverlayBlend (https://github.com/solona-m/proteus, AGPL-3.0, by Solona): ParallelPixels,
// AlphaComposite, CompoundNormal, SuppressSkinColorInfluence and ScaleOverlayAlpha. Cummy passes coverage as a
// one-byte-per-pixel plane instead of an RGBA buffer, and adds the mask gloss lerp.

using System;
using System.Threading.Tasks;

namespace Cummy.ThirdParty.Proteus;

public static class OverlayBlend
{
    /// Split a per-pixel loop across cores. <paramref name="body"/> is handed a [from, to) sub-range and iterates
    /// it with the same <paramref name="step"/> the serial loop used. Kernels must only touch the pixel they are
    /// given, so the output is identical to the serial loop.
    public static void ParallelPixels(int start, int end, int step, Action<int, int> body)
    {
        const int MinParallelPixels = 256 * 256;
        int span = end - start;
        if (span <= 0) return;
        if (span / step < MinParallelPixels || Environment.ProcessorCount < 2)
        {
            body(start, end);
            return;
        }

        int workers = Math.Min(Environment.ProcessorCount, 16);
        int chunk = ((span + workers - 1) / workers + step - 1) / step * step;
        Parallel.For(0, workers, k =>
        {
            int from = start + k * chunk;
            if (from >= end) return;
            body(from, Math.Min(from + chunk, end));
        });
    }

    /// Standard alpha-over: dst = src * src.a + dst * (1 - src.a); dst alpha unchanged.
    public static void AlphaComposite(byte[] dst, byte[] src, int w, int h)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (a <= 0f) continue;
                float ia = 1f - a;
                dst[i] = (byte)(src[i] * a + dst[i] * ia);
                dst[i + 1] = (byte)(src[i + 1] * a + dst[i + 1] * ia);
                dst[i + 2] = (byte)(src[i + 2] * a + dst[i + 2] * ia);
            }
        });
    }

    /// Adds the overlay's relief (red/green) to the relief beneath, weighted by the overlay alpha. Blue and alpha of
    /// the base are left alone.
    public static void CompoundNormal(byte[] dst, byte[] src, int w, int h)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = src[i + 3] / 255f;
                if (a <= 0f) continue;

                float bx = dst[i] / 127.5f - 1f;
                float by = dst[i + 1] / 127.5f - 1f;
                float ox = src[i] / 127.5f - 1f;
                float oy = src[i + 1] / 127.5f - 1f;

                dst[i] = (byte)Math.Clamp((bx + ox * a + 1f) * 127.5f, 0, 255);
                dst[i + 1] = (byte)Math.Clamp((by + oy * a + 1f) * 127.5f, 0, 255);
            }
        });
    }

    /// Fade the normal map's BLUE channel (skin.shpk "skin colour influence") toward black under the coverage,
    /// weighted by the composited diffuse's luminance, so bright layers keep their colour on any skin tone.
    public static void SuppressSkinColorInfluence(byte[] baseN, byte[] coverage, byte[] diffuse, int w, int h, float strength = 1f)
    {
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                float a = coverage[p] / 255f * strength;
                if (a <= 0f) continue;
                int i = p * 4;
                float lum = (0.299f * diffuse[i] + 0.587f * diffuse[i + 1] + 0.114f * diffuse[i + 2]) / 255f;
                a *= lum;
                if (a <= 0f) continue;
                baseN[i + 2] = (byte)(baseN[i + 2] * (1f - a));
            }
        });
    }

    /// Lerp the skin mask's red (specular) and green (roughness) toward the gloss targets by the overlay alpha.
    /// Blue (subsurface) and alpha stay as the base has them.
    public static void Gloss(byte[] mask, byte[] overlay, int w, int h, byte specular, byte roughness)
    {
        ParallelPixels(0, w * h * 4, 4, (from, to) =>
        {
            for (int i = from; i < to; i += 4)
            {
                float a = overlay[i + 3] / 255f;
                if (a <= 0f) continue;
                float ia = 1f - a;
                mask[i] = (byte)(specular * a + mask[i] * ia);
                mask[i + 1] = (byte)(roughness * a + mask[i + 1] * ia);
            }
        });
    }

    /// Proteus' colour-row opacity (−100…100) on one alpha: negative fades toward transparent, positive pushes a
    /// partly covered pixel toward opaque. Never adds coverage where there is none.
    public static byte ScaleAlpha(byte a, int opacity)
    {
        if (opacity < 0) return (byte)(a * (100 + opacity) / 100);
        if (a > 0) return (byte)Math.Min(255, a + (255 - a) * opacity / 100);
        return a;
    }

    /// coverage = 1 - (1 - coverage) * (1 - alpha): the union of everything layered so far.
    public static void AccumulateCoverage(byte[] coverage, byte[] overlay, int w, int h)
    {
        ParallelPixels(0, w * h, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                int a = overlay[p * 4 + 3];
                if (a == 0) continue;
                coverage[p] = (byte)(255 - (255 - coverage[p]) * (255 - a) / 255);
            }
        });
    }

    // ── Region variants (Cummy) ─────────────────────────────────────────────
    // The same kernels applied to a cropped overlay: src is rw×rh and lands at (rx, ry) in the dw-wide dst. Used so a
    // part only touches the rectangle its art covers.

    /// Run <paramref name="body"/>(dstIndex, srcIndex) for every pixel of the region, rows in parallel.
    private static void ForRegion(int dw, int rx, int ry, int rw, int rh, Action<int, int> body)
    {
        Parallel.For(0, rh, row =>
        {
            int d = ((ry + row) * dw + rx) * 4, sIdx = row * rw * 4;
            for (int col = 0; col < rw; col++, d += 4, sIdx += 4)
                body(d, sIdx);
        });
    }

    public static void AlphaCompositeRegion(byte[] dst, int dw, byte[] src, int rx, int ry, int rw, int rh)
        => ForRegion(dw, rx, ry, rw, rh, (i, s) =>
        {
            float a = src[s + 3] / 255f;
            if (a <= 0f) return;
            float ia = 1f - a;
            dst[i] = (byte)(src[s] * a + dst[i] * ia);
            dst[i + 1] = (byte)(src[s + 1] * a + dst[i + 1] * ia);
            dst[i + 2] = (byte)(src[s + 2] * a + dst[i + 2] * ia);
        });

    public static void CompoundNormalRegion(byte[] dst, int dw, byte[] src, int rx, int ry, int rw, int rh)
        => ForRegion(dw, rx, ry, rw, rh, (i, s) =>
        {
            float a = src[s + 3] / 255f;
            if (a <= 0f) return;
            float bx = dst[i] / 127.5f - 1f;
            float by = dst[i + 1] / 127.5f - 1f;
            float ox = src[s] / 127.5f - 1f;
            float oy = src[s + 1] / 127.5f - 1f;
            dst[i] = (byte)Math.Clamp((bx + ox * a + 1f) * 127.5f, 0, 255);
            dst[i + 1] = (byte)Math.Clamp((by + oy * a + 1f) * 127.5f, 0, 255);
        });

    public static void GlossRegion(byte[] mask, int dw, byte[] src, int rx, int ry, int rw, int rh, byte specular, byte roughness)
        => ForRegion(dw, rx, ry, rw, rh, (i, s) =>
        {
            float a = src[s + 3] / 255f;
            if (a <= 0f) return;
            float ia = 1f - a;
            mask[i] = (byte)(specular * a + mask[i] * ia);
            mask[i + 1] = (byte)(roughness * a + mask[i + 1] * ia);
        });

    /// Union the overlay's alpha into a one-byte-per-pixel coverage plane (dw wide).
    public static void AccumulateCoverageRegion(byte[] coverage, int dw, byte[] src, int rx, int ry, int rw, int rh)
        => ForRegion(dw, rx, ry, rw, rh, (i, s) =>
        {
            int a = src[s + 3];
            if (a == 0) return;
            int p = i / 4;
            coverage[p] = (byte)(255 - (255 - coverage[p]) * (255 - a) / 255);
        });

    /// <see cref="SuppressSkinColorInfluence"/> limited to a region (diffuse and normal the same size).
    public static void SuppressSkinColorInfluenceRegion(byte[] baseN, byte[] coverage, byte[] diffuse, int dw,
                                                        int rx, int ry, int rw, int rh, float strength = 1f)
        => ForRegion(dw, rx, ry, rw, rh, (i, _) =>
        {
            float a = coverage[i / 4] / 255f * strength;
            if (a <= 0f) return;
            float lum = (0.299f * diffuse[i] + 0.587f * diffuse[i + 1] + 0.114f * diffuse[i + 2]) / 255f;
            a *= lum;
            if (a <= 0f) return;
            baseN[i + 2] = (byte)(baseN[i + 2] * (1f - a));
        });
}
