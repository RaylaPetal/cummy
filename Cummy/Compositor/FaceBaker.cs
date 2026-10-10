using System;
using Cummy.ThirdParty.Proteus;

namespace Cummy.Compositor;

/// Redraws art painted in the face-paint (decal) layout onto one face's own texture layout, through that face's
/// model: each skin triangle is filled on the face texture, sampling the art where the triangle sits in the decal
/// layout. Both halves of a face share one texture (mirrored), so where two triangles cover the same texel the
/// stronger coverage wins - facial layers come out symmetric.
public static class FaceBaker
{
    /// <paramref name="art"/> (decal layout, any size) as an overlay for a <paramref name="w"/>×<paramref name="h"/>
    /// face texture. For a normal map (<paramref name="normal"/>), each triangle's in-plane direction is turned from
    /// the decal layout into the face texture's, so relief keeps pointing the right way on both halves.
    public static RgbaImage Bake(FaceModel model, RgbaImage art, int w, int h, bool normal)
    {
        var dst = new byte[w * h * 4];
        var c = model.Corners;
        for (int t = 0; t + 2 < c.Length; t += 3)
        {
            FaceCorner a = c[t], b = c[t + 1], d = c[t + 2];
            if (!a.HasDecal || !b.HasDecal || !d.HasDecal) continue;

            float ax = a.TexU * w, ay = a.TexV * h, bx = b.TexU * w, by = b.TexV * h, dx = d.TexU * w, dy = d.TexV * h;
            float den = (by - dy) * (ax - dx) + (dx - bx) * (ay - dy);
            if (MathF.Abs(den) < 1e-9f) continue;

            // Decal → texture direction for normals: J maps a step in the decal layout to a step on the texture.
            float m00 = 1, m01 = 0, m10 = 0, m11 = 1;
            if (normal && !Jacobian(a, b, d, out m00, out m01, out m10, out m11)) continue;

            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, dx))));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, dx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, dy))));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, dy))));
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float l1 = ((by - dy) * (px - dx) + (dx - bx) * (py - dy)) / den;
                    float l2 = ((dy - ay) * (px - dx) + (ax - dx) * (py - dy)) / den;
                    float l3 = 1 - l1 - l2;
                    if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;

                    float u = l1 * a.DecalU + l2 * b.DecalU + l3 * d.DecalU;
                    float v = l1 * a.DecalV + l2 * b.DecalV + l3 * d.DecalV;
                    if (u < 0 || u >= 1 || v < 0 || v >= 1) continue;   // outside the face-paint texture: nothing

                    Sample(art, u, v, out float r, out float g, out float bl, out float al);
                    int o = (y * w + x) * 4;
                    if (al < 0.5f || al <= dst[o + 3]) continue;   // mirrored halves: keep the stronger coverage

                    if (normal)
                    {
                        float nx = (r - 127.5f) / 127.5f, ny = (g - 127.5f) / 127.5f;
                        float len = MathF.Sqrt(nx * nx + ny * ny);
                        float tx = m00 * nx + m01 * ny, ty = m10 * nx + m11 * ny;
                        float tl = MathF.Sqrt(tx * tx + ty * ty);
                        if (tl > 1e-6f)
                        {
                            tx *= len / tl;
                            ty *= len / tl;
                        }
                        r = tx * 127.5f + 127.5f;
                        g = ty * 127.5f + 127.5f;
                    }
                    dst[o] = Byte(r);
                    dst[o + 1] = Byte(g);
                    dst[o + 2] = Byte(bl);
                    dst[o + 3] = Byte(al);
                }
            }
        }
        return new RgbaImage(dst, w, h);
    }

    /// The linear map from decal-layout steps to texture steps for one triangle (texture units, normalised so only
    /// direction and handedness matter). False for degenerate triangles.
    private static bool Jacobian(FaceCorner a, FaceCorner b, FaceCorner d,
                                 out float m00, out float m01, out float m10, out float m11)
    {
        // Edges in both layouts: T = J · D, so J = T · D⁻¹.
        float du1 = b.DecalU - a.DecalU, dv1 = b.DecalV - a.DecalV, du2 = d.DecalU - a.DecalU, dv2 = d.DecalV - a.DecalV;
        float tu1 = b.TexU - a.TexU, tv1 = b.TexV - a.TexV, tu2 = d.TexU - a.TexU, tv2 = d.TexV - a.TexV;
        float det = du1 * dv2 - du2 * dv1;
        m00 = m01 = m10 = m11 = 0;
        if (MathF.Abs(det) < 1e-12f) return false;
        float i00 = dv2 / det, i01 = -du2 / det, i10 = -dv1 / det, i11 = du1 / det;
        m00 = tu1 * i00 + tu2 * i10;
        m01 = tu1 * i01 + tu2 * i11;
        m10 = tv1 * i00 + tv2 * i10;
        m11 = tv1 * i01 + tv2 * i11;
        return true;
    }

    /// Bilinear, clamped to the texture's edges.
    private static void Sample(RgbaImage img, float u, float v, out float r, out float g, out float b, out float a)
    {
        float fx = u * img.Width - 0.5f, fy = v * img.Height - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(fx), 0, img.Width - 1), y0 = Math.Clamp((int)MathF.Floor(fy), 0, img.Height - 1);
        int x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        float tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
        var p = img.Rgba;
        int i00 = (y0 * img.Width + x0) * 4, i10 = (y0 * img.Width + x1) * 4, i01 = (y1 * img.Width + x0) * 4, i11 = (y1 * img.Width + x1) * 4;
        float Lerp(int ch) => (p[i00 + ch] * (1 - tx) + p[i10 + ch] * tx) * (1 - ty) + (p[i01 + ch] * (1 - tx) + p[i11 + ch] * tx) * ty;
        r = Lerp(0);
        g = Lerp(1);
        b = Lerp(2);
        a = Lerp(3);
    }

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
}
