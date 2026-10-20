namespace MakeItColor.Core.Video;

/// <summary>
/// Keeps colours from flickering between video frames. The model colours every frame on its own, so a wall can drift
/// from one shade to another from frame to frame. Where the picture hasn't changed (same brightness at that spot,
/// compared on the 512x512 L channel, slightly blurred so film grain doesn't count, and after removing any change of
/// overall brightness, the flicker of old film), the new a/b values are blended with the previous frame's; where
/// something moved, the new frame's colours are used as they are. A cut to another shot (a large change everywhere)
/// starts afresh.
/// </summary>
internal sealed class SteadyColors
{
    private const int S = ColorMath.Size;
    private const int Plane = S * S;
    /// <summary>How much of the previous frame's colour is kept where nothing changed.</summary>
    private const float Keep = 0.6f;
    /// <summary>Brightness difference (L units) at which a spot counts as changed.</summary>
    private const float Change = 3f;
    /// <summary>Average brightness difference (overall shift removed) that means a cut to another shot.</summary>
    private const float Cut = 10f;

    private float[]? _prevAb;
    private float[]? _prevL;
    private readonly float[] _blur = new float[Plane];
    private readonly float[] _temp = new float[Plane];

    public int Cuts { get; private set; }

    /// <summary>Smooths <paramref name="ab"/> in place, given this frame's 512x512 L.</summary>
    public void Apply(float[] ab, float[] smallL)
    {
        ColorMath.BoxMean(smallL, _blur, _temp, S, S, 2);
        if (_prevAb == null || _prevL == null)
        {
            _prevAb = (float[])ab.Clone();
            _prevL = (float[])_blur.Clone();
            return;
        }

        double shiftSum = 0;
        for (var i = 0; i < Plane; i++) shiftSum += _blur[i] - _prevL[i];
        var shift = (float)(shiftSum / Plane);
        double total = 0;
        for (var i = 0; i < Plane; i++) total += Math.Abs(_blur[i] - _prevL[i] - shift);
        if (total / Plane > Cut)
        {
            Cuts++;
        }
        else
        {
            var prev = _prevAb;
            var prevL = _prevL;
            var blur = _blur;
            Parallel.For(0, S, y =>
            {
                for (int i = y * S, e = i + S; i < e; i++)
                {
                    var d = (blur[i] - prevL[i] - shift) / Change;
                    var w = Keep * MathF.Exp(-d * d);
                    ab[i] = ab[i] * (1 - w) + prev[i] * w;
                    ab[Plane + i] = ab[Plane + i] * (1 - w) + prev[Plane + i] * w;
                }
            });
        }
        Array.Copy(ab, _prevAb, ab.Length);
        Array.Copy(_blur, _prevL, Plane);
    }
}
