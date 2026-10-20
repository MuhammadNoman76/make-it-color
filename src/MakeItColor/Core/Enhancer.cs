using MakeItColor.Core.Ai;

namespace MakeItColor.Core;

/// <summary>
/// Runs the enhancer over a whole picture, tile by tile, as Tiles() in tools\model\enhance.py: 256 x 256 tiles that
/// overlap by at least 24 pixels on each side (the overlaps are split in the middle) and edge tiles moved inside the
/// picture, which matches running the whole picture at once to 87 dB. Each tile's 4x result is resampled straight into
/// the output size (antialiased bicubic), so the 4x picture is never held in memory.
/// </summary>
internal sealed class Enhancer
{
    public const int Pad = 24;

    private readonly EnhanceModel _model;
    private readonly float[] _input = new float[3 * EnhanceModel.InPlane];
    private readonly float[] _output = new float[3 * EnhanceModel.OutPlane];

    public Enhancer(EnhanceModel model) => _model = model;

    /// <summary>Tile origins along one axis (enhance.py tile_starts).</summary>
    public static int[] TileStarts(int n)
    {
        const int tile = EnhanceModel.Tile;
        if (n <= tile) return new[] { 0 };
        var starts = new List<int>();
        for (var s = 0; s + tile < n; s += tile - 2 * Pad) starts.Add(s);
        starts.Add(n - tile);
        return starts.ToArray();
    }

    /// <summary>The pixels [lo, hi) each tile is used for (enhance.py kept).</summary>
    public static (int Lo, int Hi)[] Kept(int[] starts, int n)
    {
        var bounds = new int[starts.Length + 1];
        bounds[^1] = n;
        for (var i = 1; i < starts.Length; i++) bounds[i] = (starts[i] + starts[i - 1] + EnhanceModel.Tile) / 2;
        return Enumerable.Range(0, starts.Length).Select(i => (bounds[i], bounds[i + 1])).ToArray();
    }

    /// <summary>Number of tiles for a picture of this size.</summary>
    public static int TileCount(int w, int h) => TileStarts(Math.Max(w, EnhanceModel.Tile)).Length * TileStarts(Math.Max(h, EnhanceModel.Tile)).Length;

    /// <summary>
    /// The enhanced picture at <paramref name="outWidth"/> x <paramref name="outHeight"/> (at most 4x the picture).
    /// The picture should already be at the size the enhancer works on (see <see cref="WorkSize"/>).
    /// </summary>
    public Picture Run(Picture work, int outWidth, int outHeight, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int w = work.Width, h = work.Height;
        const int tile = EnhanceModel.Tile, scale = EnhanceModel.Scale, outTile = EnhanceModel.OutTile;
        int pw = Math.Max(w, tile), ph = Math.Max(h, tile);
        var xs = TileStarts(pw);
        var ys = TileStarts(ph);
        var kx = Kept(xs, pw);
        var ky = Kept(ys, ph);

        // Output pixel -> its taps in the 4x picture, and which tile it comes from (the tile whose kept part holds
        // the centre of the pixel).
        var ax = ColorMath.ResizeAxis.Build(w * scale, outWidth, cubic: true);
        var ay = ColorMath.ResizeAxis.Build(h * scale, outHeight, cubic: true);
        var ownerX = Owners(outWidth, w, kx);
        var ownerY = Owners(outHeight, h, ky);

        var output = new Picture(outWidth, outHeight) { HasAlpha = work.HasAlpha };
        var dst = output.Pixels;
        var done = 0;
        var total = xs.Length * ys.Length;
        for (var ty = 0; ty < ys.Length; ty++)
        {
            var (oy0, oy1) = Range(ownerY, ty);
            for (var tx = 0; tx < xs.Length; tx++)
            {
                ct.ThrowIfCancellationRequested();
                var (ox0, ox1) = Range(ownerX, tx);
                if (oy1 > oy0 && ox1 > ox0)
                {
                    LoadTile(work, xs[tx], ys[ty]);
                    _model.Run(_input, _output);
                    Resample(dst, outWidth, ax, ay, xs[tx] * scale, ys[ty] * scale, ox0, ox1, oy0, oy1, outTile);
                }
                progress?.Report(++done / (double)total);
            }
        }

        if (work.HasAlpha)
        {
            var alpha = ColorMath.Resize(work, outWidth, outHeight);
            for (var i = 3; i < dst.Length; i += 4) dst[i] = alpha.Pixels[i];
        }
        else
        {
            for (var i = 3; i < dst.Length; i += 4) dst[i] = 255;
        }
        return output;
    }

    /// <summary>For each output pixel, the tile whose kept part holds its centre.</summary>
    private static int[] Owners(int outSize, int workSize, (int Lo, int Hi)[] kept)
    {
        var owners = new int[outSize];
        var t = 0;
        for (var i = 0; i < outSize; i++)
        {
            var centre = (i + 0.5) * workSize / outSize; // in pixels of the work picture
            while (t < kept.Length - 1 && centre >= kept[t].Hi) t++;
            owners[i] = t;
        }
        return owners;
    }

    private static (int Lo, int Hi) Range(int[] owners, int tile)
    {
        var lo = Array.IndexOf(owners, tile);
        if (lo < 0) return (0, 0);
        var hi = lo;
        while (hi < owners.Length && owners[hi] == tile) hi++;
        return (lo, hi);
    }

    /// <summary>The 256 x 256 tile at (x0, y0) as RGB planes 0..1; outside the picture the edge pixels are repeated.</summary>
    private void LoadTile(Picture work, int x0, int y0)
    {
        const int tile = EnhanceModel.Tile, plane = EnhanceModel.InPlane;
        var px = work.Pixels;
        var stride = work.Stride;
        int w = work.Width, h = work.Height;
        var input = _input;
        Parallel.For(0, tile, y =>
        {
            var row = Math.Min(y0 + y, h - 1) * stride;
            for (var x = 0; x < tile; x++)
            {
                var p = row + Math.Min(x0 + x, w - 1) * 4;
                var i = y * tile + x;
                input[i] = px[p + 2] / 255f;
                input[plane + i] = px[p + 1] / 255f;
                input[2 * plane + i] = px[p] / 255f;
            }
        });
    }

    /// <summary>Output pixels [ox0, ox1) x [oy0, oy1) from the tile's 4x result (its top-left at (hx, hy) in the 4x picture).</summary>
    private void Resample(byte[] dst, int outWidth, ColorMath.ResizeAxis ax, ColorMath.ResizeAxis ay, int hx, int hy,
                          int ox0, int ox1, int oy0, int oy1, int outTile)
    {
        var src = _output;
        const int plane = EnhanceModel.OutPlane;
        // Columns of the 4x tile the output columns need.
        var c0 = ax.Start[ox0];
        var c1 = ax.Start[ox1 - 1] + ax.Count[ox1 - 1];
        var cols = c1 - c0;
        if (c0 < hx || c1 > hx + outTile || ay.Start[oy0] < hy || ay.Start[oy1 - 1] + ay.Count[oy1 - 1] > hy + outTile)
            throw new InvalidOperationException("Enhancer tile overlap too small for this size");
        Parallel.For(oy0, oy1, oy =>
        {
            var wy = ay.Weights[oy];
            var r0 = ay.Start[oy] - hy;
            var rows = new float[3 * cols];
            for (var c = 0; c < 3; c++)
            {
                var basePlane = c * plane;
                for (var k = 0; k < wy.Length; k++)
                {
                    var r = Math.Clamp(r0 + k, 0, outTile - 1);
                    var wk = (float)wy[k];
                    var o = basePlane + r * outTile + (c0 - hx);
                    for (var j = 0; j < cols; j++) rows[c * cols + j] += wk * Math.Clamp(src[o + j], 0f, 1f);
                }
            }
            var line = oy * outWidth * 4;
            for (var ox = ox0; ox < ox1; ox++)
            {
                var wx = ax.Weights[ox];
                var s0 = ax.Start[ox] - c0;
                double r = 0, g = 0, b = 0;
                for (var k = 0; k < wx.Length; k++)
                {
                    var wk = wx[k];
                    r += wk * rows[s0 + k];
                    g += wk * rows[cols + s0 + k];
                    b += wk * rows[2 * cols + s0 + k];
                }
                var p = line + ox * 4;
                dst[p] = ColorMath.Byte(b * 255);
                dst[p + 1] = ColorMath.Byte(g * 255);
                dst[p + 2] = ColorMath.Byte(r * 255);
            }
        });
    }
}
