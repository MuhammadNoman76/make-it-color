namespace MakeItColor.Core;

/// <summary>
/// "Fix brightness and contrast": automatic levels for faded or dull pictures. The tones between the darkest and the
/// brightest 0.5 % are stretched toward nearly full black and white (at most 1.8x more contrast, never less). A picture
/// that already uses the whole range is left as it is (a dark or bright mood is kept). Works on the 8-bit values with
/// one lookup table, so it costs next to nothing.
/// </summary>
internal static class Tone
{
    public const double Black = 4, White = 250, MaxGain = 1.8;

    public readonly record struct Levels(double Low, double High, double Mid, double Mean);

    /// <summary>Histogram of the luma (Rec. 601 weights on the 8-bit values), 256 bins.</summary>
    public static long[] Histogram(Picture p)
    {
        var hist = new long[256];
        var px = p.Pixels;
        int w = p.Width, h = p.Height, stride = p.Stride;
        var gate = new object();
        Parallel.For(0, h, () => new long[256], (y, _, local) =>
        {
            for (int x = 0, i = y * stride; x < w; x++, i += 4) local[(px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77 + 128) >> 8]++;
            return local;
        }, local =>
        {
            lock (gate)
                for (var i = 0; i < 256; i++) hist[i] += local[i];
        });
        return hist;
    }

    public static Levels Measure(long[] hist)
    {
        long total = 0;
        double sum = 0;
        for (var i = 0; i < 256; i++)
        {
            total += hist[i];
            sum += (double)i * hist[i];
        }
        if (total == 0) return new Levels(0, 255, 128, 128);
        return new Levels(Percentile(hist, total, 0.005), Percentile(hist, total, 0.995), Percentile(hist, total, 0.5), sum / total);
    }

    // Interpolated within the bin, so the levels move smoothly from frame to frame.
    private static double Percentile(long[] hist, long total, double q)
    {
        var target = q * total;
        long seen = 0;
        for (var i = 0; i < 256; i++)
        {
            if (seen + hist[i] >= target && hist[i] > 0) return i + (target - seen) / hist[i] - 0.5;
            seen += hist[i];
        }
        return 255;
    }

    /// <summary>Histogram distance (0 = same, 2 = nothing in common) on 32 bins: large at a scene cut.</summary>
    public static double Distance(long[] a, long[] b)
    {
        double ta = a.Sum(), tb = b.Sum(), d = 0;
        if (ta == 0 || tb == 0) return 0;
        for (var bin = 0; bin < 256; bin += 8)
        {
            double sa = 0, sb = 0;
            for (var i = bin; i < bin + 8; i++)
            {
                sa += a[i];
                sb += b[i];
            }
            d += Math.Abs(sa / ta - sb / tb);
        }
        return d;
    }

    /// <summary>The lookup table for pictures with <paramref name="levels"/>; <paramref name="gain"/> first scales the
    /// values (the video's flicker correction).</summary>
    public static byte[] Curve(Levels levels, double gain = 1)
    {
        var range = Math.Max(1, levels.High - levels.Low);
        var g = (White - Black) / range;
        var lut = new byte[256];
        if (g <= 1)
        {
            // Already uses the whole range: only the flicker correction.
            for (var i = 0; i < 256; i++) lut[i] = ColorMath.Byte(i * gain);
            return lut;
        }
        // Stretch about the middle of the used range, so Low -> Black and High -> White (less when capped at 1.8x).
        g = Math.Min(g, MaxGain);
        var centre = (levels.Low + levels.High) / 2;
        for (var i = 0; i < 256; i++) lut[i] = ColorMath.Byte((White + Black) / 2 + (i * gain - centre) * g);
        return lut;
    }

    public static void Apply(Picture p, byte[] lut)
    {
        var px = p.Pixels;
        int w = p.Width, stride = p.Stride;
        Parallel.For(0, p.Height, y =>
        {
            for (int x = 0, i = y * stride; x < w; x++, i += 4)
            {
                px[i] = lut[px[i]];
                px[i + 1] = lut[px[i + 1]];
                px[i + 2] = lut[px[i + 2]];
            }
        });
    }

    /// <summary>Photos: measures the picture and corrects it in place.</summary>
    public static void Normalize(Picture p) => Apply(p, Curve(Measure(Histogram(p))));
}

/// <summary>
/// The brightness and contrast fix for videos, steady over time. The levels the tone curve is built from follow each
/// shot slowly (about a second), so the picture doesn't pump; each frame's brightness is also matched to the average of
/// the 3 frames before and after it in the same shot, which takes out the flicker of old film without dimming fades.
/// Frames therefore come out 3 frames late: <see cref="Push"/> returns the frames that are ready, <see cref="Flush"/>
/// the rest at the end.
/// </summary>
internal sealed class VideoTone<T> where T : class
{
    private const int Radius = 3;
    private const double Follow = 0.04;   // share of a new frame's levels taken into the shot's levels
    private const double CutDistance = 0.6;

    private sealed class Entry
    {
        public T Item = default!;
        public Picture Picture = null!;
        public double Mean;
        public int Shot;
        public Tone.Levels Target;
    }

    private readonly Func<T, Picture> _picture;
    private readonly List<Entry> _window = new(); // the last frames, oldest first; the first ones may be done already
    private int _pending;                         // frames at the end of the window not given out yet
    private Tone.Levels? _shot;
    private long[]? _previous;
    private int _shotNumber;

    /// <param name="correct">False: only find the cuts (and keep the frame order and delay), don't change the frames.</param>
    public VideoTone(Func<T, Picture> picture, bool correct = true)
    {
        _picture = picture;
        _correct = correct;
    }

    private readonly bool _correct;

    /// <summary>Number of scene cuts seen.</summary>
    public int Cuts { get; private set; }

    /// <summary>True for the next frame that comes out after a cut (for filters that keep state between frames).</summary>
    public bool IsFirstOfShot(T item) => _firsts.Remove(item);

    private readonly HashSet<T> _firsts = new(ReferenceEqualityComparer.Instance);

    public List<T> Push(T item)
    {
        var picture = _picture(item);
        var hist = Tone.Histogram(picture);
        var levels = Tone.Measure(hist);
        var cut = _previous == null || Tone.Distance(hist, _previous) > CutDistance;
        _previous = hist;
        if (cut)
        {
            if (_shot != null) Cuts++;
            _shotNumber++;
            _shot = levels;
            _firsts.Add(item);
        }
        else
        {
            var s = _shot!.Value;
            _shot = new Tone.Levels(s.Low + Follow * (levels.Low - s.Low), s.High + Follow * (levels.High - s.High),
                                    s.Mid + Follow * (levels.Mid - s.Mid), s.Mean + Follow * (levels.Mean - s.Mean));
        }
        _window.Add(new Entry { Item = item, Picture = picture, Mean = levels.Mean, Shot = _shotNumber, Target = _shot.Value });
        _pending++;
        var ready = new List<T>();
        while (_pending > Radius) ready.Add(Release());
        return ready;
    }

    public List<T> Flush()
    {
        var ready = new List<T>();
        while (_pending > 0) ready.Add(Release());
        return ready;
    }

    private T Release()
    {
        var index = _window.Count - _pending;
        var e = _window[index];
        double sum = 0;
        var n = 0;
        for (var i = Math.Max(0, index - Radius); i <= Math.Min(_window.Count - 1, index + Radius); i++)
        {
            if (_window[i].Shot != e.Shot) continue;
            sum += _window[i].Mean;
            n++;
        }
        var gain = e.Mean >= 8 ? Math.Clamp(sum / n / e.Mean, 0.85, 1.18) : 1;
        if (_correct) Tone.Apply(e.Picture, Tone.Curve(e.Target, gain));
        _pending--;
        while (_window.Count - _pending > Radius) _window.RemoveAt(0); // keep the 3 frames before the next one
        return e.Item;
    }
}

/// <summary>
/// The light clean-up for videos, fast on any CPU: less grain (where a pixel barely changes from the previous frame it
/// is averaged with it; moving things are left alone) and gentle sharpening that never goes past the brightest or
/// darkest pixel around it, so it adds no halos. Works on the luma and moves R, G and B together.
/// </summary>
internal sealed class LightCleanup
{
    private const float Keep = 0.5f;     // weight of the previous frame where nothing moves
    private const float Sigma = 8f;      // a change this large (8-bit units) mostly counts as movement
    private const float Sharpen = 0.5f;

    private float[]? _previous;
    private float[] _y = Array.Empty<float>(), _clean = Array.Empty<float>();

    public void Reset() => _previous = null;

    public void Apply(Picture p)
    {
        int w = p.Width, h = p.Height, n = w * h, stride = p.Stride;
        if (_y.Length != n)
        {
            _y = new float[n];
            _clean = new float[n];
            _previous = null;
        }
        var px = p.Pixels;
        var y0 = _y;
        var clean = _clean;
        var previous = _previous;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, q = y * stride; x < w; x++, i++, q += 4)
            {
                var v = (px[q] * 0.114f + px[q + 1] * 0.587f + px[q + 2] * 0.299f);
                y0[i] = v;
                if (previous == null)
                {
                    clean[i] = v;
                    continue;
                }
                var d = (previous[i] - v) / Sigma;
                clean[i] = v + Keep * MathF.Exp(-d * d) * (previous[i] - v);
            }
        });
        var sharp = new float[n];
        Parallel.For(0, h, y =>
        {
            var ya = Math.Max(0, y - 1);
            var yb = Math.Min(h - 1, y + 1);
            for (var x = 0; x < w; x++)
            {
                var xa = Math.Max(0, x - 1);
                var xb = Math.Min(w - 1, x + 1);
                float lo = float.MaxValue, hi = float.MinValue, blur = 0;
                for (var yy = ya; yy <= yb; yy++)
                    for (var xx = xa; xx <= xb; xx++)
                    {
                        var v = clean[yy * w + xx];
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
                // Binomial 3x3 blur (edges repeated).
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var wt = (dy == 0 ? 2 : 1) * (dx == 0 ? 2 : 1);
                        blur += wt * clean[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                    }
                var c = clean[y * w + x];
                sharp[y * w + x] = Math.Clamp(c + Sharpen * (c - blur / 16), lo, hi);
            }
        });
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, q = y * stride; x < w; x++, i++, q += 4)
            {
                var delta = sharp[i] - y0[i];
                px[q] = ColorMath.Byte(px[q] + delta);
                px[q + 1] = ColorMath.Byte(px[q + 1] + delta);
                px[q + 2] = ColorMath.Byte(px[q + 2] + delta);
            }
        });
        _previous = clean;
        _clean = previous ?? new float[n];
    }
}
