using System;

using Cummy.ThirdParty.Proteus;

namespace Cummy.Compositor;

/// RGBA8 resizing for overlays whose size differs from the skin texture they go onto, and for mip generation.
public static class Resample
{
    public static RgbaImage To(RgbaImage src, int dw, int dh)
    {
        if (src.Width == dw && src.Height == dh) return src;
        return dw < src.Width || dh < src.Height ? Box(src, dw, dh) : Bilinear(src, dw, dh);
    }

    /// Exact 2×2 average; both dimensions must be even.
    public static RgbaImage HalfBox(RgbaImage src)
    {
        int dw = src.Width / 2, dh = src.Height / 2, sw = src.Width;
        var s = src.Rgba;
        var dst = new byte[dw * dh * 4];
        OverlayBlend.ParallelPixels(0, dw * dh, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                int x = p % dw, y = p / dw;
                int a = ((y * 2) * sw + x * 2) * 4, b = a + 4, c = a + sw * 4, d = c + 4;
                int o = p * 4;
                for (int ch = 0; ch < 4; ch++)
                    dst[o + ch] = (byte)((s[a + ch] + s[b + ch] + s[c + ch] + s[d + ch] + 2) / 4);
            }
        });
        return new RgbaImage(dst, dw, dh);
    }

    /// Area average for shrinking (any ratio).
    private static RgbaImage Box(RgbaImage src, int dw, int dh)
    {
        int sw = src.Width, sh = src.Height;
        var s = src.Rgba;
        var dst = new byte[dw * dh * 4];
        double fx = (double)sw / dw, fy = (double)sh / dh;
        OverlayBlend.ParallelPixels(0, dw * dh, 1, (from, to) =>
        {
            Span<double> acc = stackalloc double[4];
            for (int p = from; p < to; p++)
            {
                int x = p % dw, y = p / dw;
                int x0 = (int)(x * fx), x1 = Math.Max(x0 + 1, Math.Min(sw, (int)Math.Ceiling((x + 1) * fx)));
                int y0 = (int)(y * fy), y1 = Math.Max(y0 + 1, Math.Min(sh, (int)Math.Ceiling((y + 1) * fy)));
                acc.Clear();
                for (int yy = y0; yy < y1; yy++)
                    for (int xx = x0; xx < x1; xx++)
                    {
                        int i = (yy * sw + xx) * 4;
                        acc[0] += s[i]; acc[1] += s[i + 1]; acc[2] += s[i + 2]; acc[3] += s[i + 3];
                    }
                double n = (x1 - x0) * (y1 - y0);
                int o = p * 4;
                for (int ch = 0; ch < 4; ch++) dst[o + ch] = (byte)Math.Round(acc[ch] / n);
            }
        });
        return new RgbaImage(dst, dw, dh);
    }

    private static RgbaImage Bilinear(RgbaImage src, int dw, int dh)
    {
        int sw = src.Width, sh = src.Height;
        var s = src.Rgba;
        var dst = new byte[dw * dh * 4];
        float fx = (float)sw / dw, fy = (float)sh / dh;
        OverlayBlend.ParallelPixels(0, dw * dh, 1, (from, to) =>
        {
            for (int p = from; p < to; p++)
            {
                int x = p % dw, y = p / dw;
                float sx = Math.Clamp((x + 0.5f) * fx - 0.5f, 0, sw - 1);
                float sy = Math.Clamp((y + 0.5f) * fy - 0.5f, 0, sh - 1);
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = Math.Min(x0 + 1, sw - 1), y1 = Math.Min(y0 + 1, sh - 1);
                float tx = sx - x0, ty = sy - y0;
                int a = (y0 * sw + x0) * 4, b = (y0 * sw + x1) * 4, c = (y1 * sw + x0) * 4, d = (y1 * sw + x1) * 4;
                int o = p * 4;
                for (int ch = 0; ch < 4; ch++)
                {
                    float top = s[a + ch] + (s[b + ch] - s[a + ch]) * tx;
                    float bottom = s[c + ch] + (s[d + ch] - s[c + ch]) * tx;
                    dst[o + ch] = (byte)Math.Round(top + (bottom - top) * ty);
                }
            }
        });
        return new RgbaImage(dst, dw, dh);
    }
}
