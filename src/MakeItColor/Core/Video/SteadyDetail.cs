namespace MakeItColor.Core.Video;

/// <summary>
/// Keeps the enhancer's detail steady in videos. The enhancer works on each frame alone, so where nothing moves it can
/// draw fine detail a little differently from frame to frame (a shimmer). Where the source frame didn't change from
/// the previous one (compared over 3 x 3 pixels, so noise doesn't count as change), the enhanced pixel is blended with
/// the previous result; the decision comes from the source, not the enhancer's output, so moving things aren't
/// smeared. A cut starts fresh.
/// </summary>
internal sealed class SteadyDetail
{
    private const float Keep = 0.7f;   // weight of the previous result where the source is unchanged
    private const float Change = 3f;   // source change (8-bit luma, 3 x 3 mean) that mostly counts as movement

    private float[]? _previousLuma;
    private byte[]? _previousResult;
    private float[] _luma = Array.Empty<float>(), _weight = Array.Empty<float>();

    public void Reset() => _previousLuma = null;

    /// <summary><paramref name="source"/>: the frame the enhancer worked from (original size); <paramref name="result"/>:
    /// its enhanced version, changed in place.</summary>
    public void Apply(Picture source, Picture result)
    {
        int w = source.Width, h = source.Height, n = w * h, stride = source.Stride;
        if (_luma.Length != n)
        {
            _luma = new float[n];
            _weight = new float[n];
            _previousLuma = null;
        }
        var src = source.Pixels;
        var dst = result.Pixels;
        var luma = _luma;
        Parallel.For(0, h, y =>
        {
            for (int x = 0, i = y * w, p = y * stride; x < w; x++, i++, p += 4)
                luma[i] = src[p] * 0.114f + src[p + 1] * 0.587f + src[p + 2] * 0.299f;
        });

        var previousLuma = _previousLuma;
        var previousResult = _previousResult;
        if (previousLuma != null && previousResult != null && previousResult.Length == dst.Length)
        {
            var weight = _weight;
            Parallel.For(0, h, y =>
            {
                int ya = Math.Max(0, y - 1), yb = Math.Min(h - 1, y + 1);
                for (var x = 0; x < w; x++)
                {
                    int xa = Math.Max(0, x - 1), xb = Math.Min(w - 1, x + 1);
                    float sum = 0;
                    var count = 0;
                    for (var yy = ya; yy <= yb; yy++)
                        for (var xx = xa; xx <= xb; xx++)
                        {
                            var j = yy * w + xx;
                            sum += luma[j] - previousLuma[j];
                            count++;
                        }
                    var d = sum / count / Change;
                    weight[y * w + x] = Keep * MathF.Exp(-d * d);
                }
            });
            Parallel.For(0, h, y =>
            {
                for (int x = 0, i = y * w, p = y * stride; x < w; x++, i++, p += 4)
                {
                    var k = weight[i];
                    if (k < 0.02f) continue;
                    dst[p] = (byte)(dst[p] + k * (previousResult[p] - dst[p]) + 0.5f);
                    dst[p + 1] = (byte)(dst[p + 1] + k * (previousResult[p + 1] - dst[p + 1]) + 0.5f);
                    dst[p + 2] = (byte)(dst[p + 2] + k * (previousResult[p + 2] - dst[p + 2]) + 0.5f);
                }
            });
        }
        _previousLuma = (float[])luma.Clone();
        _previousResult = (byte[])dst.Clone();
    }
}
