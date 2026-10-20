namespace MakeItColor.Core;

/// <summary>
/// The colour maths around the model, ported from tools\model\pipeline.py (the reference this must match):
/// OpenCV's float Lab formulas (sRGB, D65), a PIL-style antialiased resize for the 512x512 model input, bilinear
/// upscaling of the predicted a/b channels and a guided filter that lines them up with the full-size L channel.
/// </summary>
internal static class ColorMath
{
    public const int Size = 512;
    public const float GuideEps = 1e-3f;

    // sRGB 8-bit value -> linear light, exact.
    private static readonly float[] Linear8 = Enumerable.Range(0, 256).Select(i => (float)SrgbToLinear(i / 255.0)).ToArray();

    // XYZ (relative to the D65 white) -> linear sRGB, OpenCV's matrix times the white point.
    private static readonly double[] XyzToRgb = BuildXyzToRgb();

    private static double[] BuildXyzToRgb()
    {
        double[] m = { 3.240479, -1.53715, -0.498535, -0.969256, 1.875991, 0.041556, 0.055648, -0.204043, 1.057311 };
        double[] white = { 0.950456, 1.0, 1.088754 };
        var c = new double[9];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                c[i * 3 + j] = m[i * 3 + j] * white[j];
        return c;
    }

    public static double SrgbToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double LinearToSrgb(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    /// <summary>L (0..100) from linear RGB: L depends only on the luminance Y.</summary>
    public static float LFromLinear(double r, double g, double b)
    {
        var y = 0.212671 * r + 0.715160 * g + 0.072169 * b;
        return (float)(y > 0.008856 ? 116 * Math.Cbrt(y) - 16 : 903.3 * y);
    }

    /// <summary>L of an 8-bit BGR pixel.</summary>
    public static float LFromBgr8(byte b, byte g, byte r) => LFromLinear(Linear8[r], Linear8[g], Linear8[b]);

    /// <summary>L of a non-linear (gamma-encoded) BGR pixel in 0..1.</summary>
    public static float LFromBgr(double b, double g, double r) => LFromLinear(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));

    /// <summary>Lab -> non-linear sRGB in 0..1 (OpenCV Lab2RGB float: clip in linear light, then gamma).</summary>
    public static void LabToRgb(double L, double a, double bb, out double r, out double g, out double b)
    {
        double y, fy;
        if (L <= 7.9996248)
        {
            y = L / 903.3;
            fy = 7.787 * y + 16.0 / 116;
        }
        else
        {
            fy = (L + 16) / 116;
            y = fy * fy * fy;
        }
        var fx = fy + a / 500;
        var fz = fy - bb / 200;
        var x = fx <= 0.20689657 ? (fx - 16.0 / 116) / 7.787 : fx * fx * fx;
        var z = fz <= 0.20689657 ? (fz - 16.0 / 116) / 7.787 : fz * fz * fz;
        var c = XyzToRgb;
        r = LinearToSrgb(Math.Clamp(c[0] * x + c[1] * y + c[2] * z, 0, 1));
        g = LinearToSrgb(Math.Clamp(c[3] * x + c[4] * y + c[5] * z, 0, 1));
        b = LinearToSrgb(Math.Clamp(c[6] * x + c[7] * y + c[8] * z, 0, 1));
    }

    /// <summary>The gray value the model sees for a given L (Lab with a = b = 0, so R = G = B).</summary>
    public static float GrayForL(double L)
    {
        LabToRgb(L, 0, 0, out var r, out _, out _);
        return (float)r;
    }

    // ---------------------------------------------------------------------------- resizing

    /// <summary>PIL-style antialiased filter weights for one axis: triangle (bilinear) or, with cubic, PIL's bicubic.</summary>
    public sealed class ResizeAxis
    {
        public int[] Start = Array.Empty<int>();
        public int[] Count = Array.Empty<int>();
        public double[][] Weights = Array.Empty<double[]>();

        public static ResizeAxis Build(int src, int dst, bool cubic = false)
        {
            var scale = (double)src / dst;
            var filterScale = Math.Max(scale, 1.0);
            var support = (cubic ? 2.0 : 1.0) * filterScale;
            var axis = new ResizeAxis { Start = new int[dst], Count = new int[dst], Weights = new double[dst][] };
            for (var i = 0; i < dst; i++)
            {
                var center = (i + 0.5) * scale;
                var lo = Math.Max((int)Math.Floor(center - support), 0);
                var hi = Math.Min((int)Math.Ceiling(center + support), src);
                var w = new double[hi - lo];
                double sum = 0;
                for (var j = lo; j < hi; j++)
                {
                    var d = Math.Abs((j + 0.5 - center) / filterScale);
                    var t = cubic ? Cubic(d) : Math.Max(0.0, 1.0 - d);
                    w[j - lo] = t;
                    sum += t;
                }
                for (var k = 0; k < w.Length; k++) w[k] /= sum;
                axis.Start[i] = lo;
                axis.Count[i] = w.Length;
                axis.Weights[i] = w;
            }
            return axis;
        }

        // Keys cubic with a = -0.5, as PIL's BICUBIC.
        private static double Cubic(double x)
        {
            const double a = -0.5;
            if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
            if (x < 2) return ((a * x - 5 * a) * x + 8 * a) * x - 4 * a;
            return 0;
        }
    }

    /// <summary>The picture resized to w x h (antialiased, on the gamma-encoded values; alpha too).</summary>
    public static Picture Resize(Picture pic, int w, int h, bool cubic = true)
    {
        if (w == pic.Width && h == pic.Height) return pic.Clone();
        int sw = pic.Width, sh = pic.Height;
        var ax = ResizeAxis.Build(sw, w, cubic);
        var ay = ResizeAxis.Build(sh, h, cubic);
        var rows = new float[sh * w * 4];
        var px = pic.Pixels;
        var stride = pic.Stride;
        Parallel.For(0, sh, y =>
        {
            var row = y * stride;
            var o = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                var wts = ax.Weights[x];
                var p = row + ax.Start[x] * 4;
                for (var k = 0; k < wts.Length; k++, p += 4)
                {
                    var wk = wts[k];
                    b += wk * px[p];
                    g += wk * px[p + 1];
                    r += wk * px[p + 2];
                    a += wk * px[p + 3];
                }
                rows[o + x * 4] = (float)b;
                rows[o + x * 4 + 1] = (float)g;
                rows[o + x * 4 + 2] = (float)r;
                rows[o + x * 4 + 3] = (float)a;
            }
        });
        var output = new Picture(w, h) { HasAlpha = pic.HasAlpha };
        var dst = output.Pixels;
        Parallel.For(0, h, y =>
        {
            var wts = ay.Weights[y];
            var y0 = ay.Start[y];
            for (var x = 0; x < w * 4; x++)
            {
                double v = 0;
                for (var k = 0; k < wts.Length; k++) v += wts[k] * rows[(y0 + k) * w * 4 + x];
                dst[y * w * 4 + x] = Byte(v);
            }
        });
        return output;
    }

    public static byte Byte(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    /// <summary>
    /// The model input: the picture resized to 512x512 (antialiased, on the gamma-encoded values), its L channel turned
    /// back into gray and repeated in 3 planes. Also returns that small L (for the video's frame-to-frame comparisons).
    /// </summary>
    public static void ModelInput(Picture pic, float[] input, float[] smallL)
    {
        int w = pic.Width, h = pic.Height, s = Size;
        var ax = ResizeAxis.Build(w, s);
        var ay = ResizeAxis.Build(h, s);
        var rows = new double[h * s * 3];
        var px = pic.Pixels;
        var stride = pic.Stride;
        Parallel.For(0, h, y =>
        {
            var row = y * stride;
            var o = y * s * 3;
            for (var x = 0; x < s; x++)
            {
                double b = 0, g = 0, r = 0;
                var wts = ax.Weights[x];
                var p = row + ax.Start[x] * 4;
                for (var k = 0; k < wts.Length; k++, p += 4)
                {
                    var wk = wts[k];
                    b += wk * px[p];
                    g += wk * px[p + 1];
                    r += wk * px[p + 2];
                }
                rows[o + x * 3] = b / 255.0;
                rows[o + x * 3 + 1] = g / 255.0;
                rows[o + x * 3 + 2] = r / 255.0;
            }
        });
        var plane = s * s;
        Parallel.For(0, s, y =>
        {
            var wts = ay.Weights[y];
            var y0 = ay.Start[y];
            for (var x = 0; x < s; x++)
            {
                double b = 0, g = 0, r = 0;
                for (var k = 0; k < wts.Length; k++)
                {
                    var q = ((y0 + k) * s + x) * 3;
                    b += wts[k] * rows[q];
                    g += wts[k] * rows[q + 1];
                    r += wts[k] * rows[q + 2];
                }
                var L = LFromBgr(b, g, r);
                var gray = GrayForL(L);
                var i = y * s + x;
                smallL[i] = L;
                input[i] = gray;
                input[plane + i] = gray;
                input[2 * plane + i] = gray;
            }
        });
    }

    /// <summary>Full-size L channel of the picture.</summary>
    public static void FullL(Picture pic, float[] L)
    {
        int w = pic.Width, h = pic.Height;
        var px = pic.Pixels;
        var stride = pic.Stride;
        Parallel.For(0, h, y =>
        {
            var p = y * stride;
            var o = y * w;
            for (var x = 0; x < w; x++, p += 4) L[o + x] = LFromBgr8(px[p], px[p + 1], px[p + 2]);
        });
    }

    /// <summary>Bilinear upscaling of one 512x512 plane (half-pixel centres, edges clamped) to w x h.</summary>
    public static void Upscale(float[] src, int srcOffset, float[] dst, int w, int h)
    {
        int s = Size;
        var x0 = new int[w];
        var x1 = new int[w];
        var fx = new float[w];
        for (var x = 0; x < w; x++)
        {
            var pos = Math.Clamp((x + 0.5) * s / w - 0.5, 0, s - 1);
            x0[x] = (int)Math.Floor(pos);
            x1[x] = Math.Min(x0[x] + 1, s - 1);
            fx[x] = (float)(pos - x0[x]);
        }
        Parallel.For(0, h, y =>
        {
            var pos = Math.Clamp((y + 0.5) * s / h - 0.5, 0, s - 1);
            var y0 = (int)Math.Floor(pos);
            var y1 = Math.Min(y0 + 1, s - 1);
            var fy = (float)(pos - y0);
            var r0 = srcOffset + y0 * s;
            var r1 = srcOffset + y1 * s;
            var o = y * w;
            for (var x = 0; x < w; x++)
            {
                var top = src[r0 + x0[x]] * (1 - fx[x]) + src[r0 + x1[x]] * fx[x];
                var bot = src[r1 + x0[x]] * (1 - fx[x]) + src[r1 + x1[x]] * fx[x];
                dst[o + x] = top * (1 - fy) + bot * fy;
            }
        });
    }

    // ---------------------------------------------------------------------------- guided filter

    public static int GuideRadius(int w, int h) => 2 * Math.Max(1, (int)Math.Round(Math.Max(w, h) / (double)Size, MidpointRounding.ToEven));

    /// <summary>Mean over a (2r+1)^2 window, the window clipped at the edges: down the columns, then along the rows.</summary>
    public static void BoxMean(float[] src, float[] dst, float[] temp, int w, int h, int r)
    {
        Parallel.For(0, (w + 63) / 64, block =>
        {
            var xs = block * 64;
            var xe = Math.Min(w, xs + 64);
            var sum = new double[xe - xs];
            for (var y = 0; y < Math.Min(r, h); y++)
                for (var x = xs; x < xe; x++) sum[x - xs] += src[y * w + x];
            for (var y = 0; y < h; y++)
            {
                var add = y + r;
                var remove = y - r - 1;
                if (add < h)
                    for (var x = xs; x < xe; x++) sum[x - xs] += src[add * w + x];
                if (remove >= 0)
                    for (var x = xs; x < xe; x++) sum[x - xs] -= src[remove * w + x];
                var count = Math.Min(h - 1, y + r) - Math.Max(0, y - r) + 1;
                for (var x = xs; x < xe; x++) temp[y * w + x] = (float)(sum[x - xs] / count);
            }
        });
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            double sum = 0;
            for (var x = 0; x < Math.Min(r, w); x++) sum += temp[o + x];
            for (var x = 0; x < w; x++)
            {
                var add = x + r;
                var remove = x - r - 1;
                if (add < w) sum += temp[o + add];
                if (remove >= 0) sum -= temp[o + remove];
                var count = Math.Min(w - 1, x + r) - Math.Max(0, x - r) + 1;
                dst[o + x] = (float)(sum / count);
            }
        });
    }
}

/// <summary>A picture in memory: 8-bit BGRA rows (alpha is kept as it is).</summary>
internal sealed class Picture
{
    public Picture(int width, int height, byte[]? pixels = null)
    {
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; }
    public bool HasAlpha { get; set; }

    public Picture Clone() => new(Width, Height, (byte[])Pixels.Clone()) { HasAlpha = HasAlpha };
}
