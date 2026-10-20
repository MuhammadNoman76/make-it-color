namespace MakeItColor.Core;

/// <summary>
/// Turns the model's 512x512 a/b prediction into the full-size colour picture: the original L channel (all the detail),
/// the a/b channels upscaled and lined up with it by a guided filter, optionally made more or less vivid.
/// </summary>
internal sealed class Colorizer
{
    private int _w, _h;
    private float[] _L = Array.Empty<float>(), _I = Array.Empty<float>(), _a = Array.Empty<float>(), _b = Array.Empty<float>();
    private float[] _mI = Array.Empty<float>(), _mII = Array.Empty<float>(), _t1 = Array.Empty<float>(), _t2 = Array.Empty<float>();
    private float[] _t3 = Array.Empty<float>(), _A = Array.Empty<float>(), _B = Array.Empty<float>(), _temp = Array.Empty<float>();

    private void Ensure(int w, int h)
    {
        if (w == _w && h == _h) return;
        _w = w;
        _h = h;
        var n = w * h;
        _L = new float[n]; _I = new float[n]; _a = new float[n]; _b = new float[n];
        _mI = new float[n]; _mII = new float[n]; _t1 = new float[n]; _t2 = new float[n];
        _t3 = new float[n]; _A = new float[n]; _B = new float[n]; _temp = new float[n];
    }

    /// <summary>The colour picture for <paramref name="source"/> from the model output <paramref name="ab"/> [2, 512, 512].</summary>
    public Picture Finish(Picture source, float[] ab, double vividness = 1.0)
    {
        int w = source.Width, h = source.Height, n = w * h;
        Ensure(w, h);
        ColorMath.FullL(source, _L);
        ColorMath.Upscale(ab, 0, _a, w, h);
        ColorMath.Upscale(ab, ColorMath.Size * ColorMath.Size, _b, w, h);

        var r = ColorMath.GuideRadius(w, h);
        var eps = ColorMath.GuideEps;
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                var v = _L[i] / 100f;
                _I[i] = v;
                _t1[i] = v * v;
            }
        });
        ColorMath.BoxMean(_I, _mI, _temp, w, h, r);
        ColorMath.BoxMean(_t1, _mII, _temp, w, h, r);
        Guide(_a, r, eps);
        Guide(_b, r, eps);

        var output = new Picture(w, h) { HasAlpha = source.HasAlpha };
        var src = source.Pixels;
        var dst = output.Pixels;
        var s = vividness;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, p = y * w * 4; x < w; x++, i++, p += 4)
            {
                ColorMath.LabToRgb(_L[i], s * _a[i], s * _b[i], out var rr, out var gg, out var bb);
                dst[p] = ToByte(bb);
                dst[p + 1] = ToByte(gg);
                dst[p + 2] = ToByte(rr);
                dst[p + 3] = src[p + 3];
            }
        });
        return output;
    }

    /// <summary>Guided filter of one a/b plane in place (guide: I = L / 100; box means of I and I^2 already computed).</summary>
    private void Guide(float[] p, int r, float eps)
    {
        int w = _w, h = _h;
        ColorMath.BoxMean(p, _t2, _temp, w, h, r); // mean p
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++) _t3[i] = _I[i] * p[i];
        });
        ColorMath.BoxMean(_t3, _t1, _temp, w, h, r); // mean I*p
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++)
            {
                var mI = _mI[i];
                var a = (_t1[i] - mI * _t2[i]) / (_mII[i] - mI * mI + eps);
                _A[i] = a;
                _B[i] = _t2[i] - a * mI;
            }
        });
        ColorMath.BoxMean(_A, _t1, _temp, w, h, r);
        ColorMath.BoxMean(_B, _t2, _temp, w, h, r);
        Parallel.For(0, h, y =>
        {
            for (int i = y * w, e = i + w; i < e; i++) p[i] = _t1[i] * _I[i] + _t2[i];
        });
        // _mI and _mII are left as they are: the second plane uses them too.
    }

    private static byte ToByte(double v)
    {
        var x = Math.Round(v * 255.0);
        return x <= 0 ? (byte)0 : x >= 255 ? (byte)255 : (byte)x;
    }
}
