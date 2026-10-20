using MakeItColor.Core.Ai;

namespace MakeItColor.Core;

/// <summary>
/// A photo ready to colour: the colour model sees ForModel (only the brightness fixed: the pictures it was tested
/// with; the enhancer's output can change its guesses), and Detail (brightness fixed and enhanced, maybe larger)
/// gives the colour photo its L channel, so all its detail.
/// </summary>
internal sealed record Prepared(Picture ForModel, Picture Detail);

/// <summary>
/// What happens to a photo before it is coloured: the brightness and contrast fix, then the enhancer (at the photo's
/// own size, or 2x / 4x larger).
/// </summary>
internal static class PhotoPrep
{
    /// <summary>Auto size: photos smaller than this (longest side) are made 2x or 4x larger.</summary>
    public const int AutoTarget = 2000;
    /// <summary>Largest photo made (24 megapixels, about 6000 x 4000): the colour maths needs about 50 bytes a pixel.</summary>
    public const double MaxOutputPixels = 24e6;
    /// <summary>Largest picture the enhancer works on; larger photos are reduced first.</summary>
    public const double MaxWorkPixels = 16e6;

    public static bool Wanted(bool enhance) => enhance && Settings.Current.EnhancePhotos || Settings.Current.FixTone;

    /// <summary>The size of the finished photo.</summary>
    public static (int W, int H) OutputSize(int w, int h, PhotoSize size, bool enhance)
    {
        if (!enhance) return (w, h);
        double s = size switch
        {
            PhotoSize.Original => 1,
            PhotoSize.Double => 2,
            PhotoSize.Quadruple => 4,
            _ => Math.Max(w, h) >= AutoTarget ? 1 : Math.Max(w, h) * 2 >= AutoTarget ? 2 : 4,
        };
        var pixels = (double)w * h;
        if (pixels * s * s > MaxOutputPixels) s = Math.Max(1, Math.Sqrt(MaxOutputPixels / pixels));
        return ((int)Math.Round(w * s), (int)Math.Round(h * s));
    }

    /// <summary>
    /// The pictures for the colour model and for the detail: <paramref name="source"/> itself when nothing is to be
    /// done; otherwise new pictures (brightness and contrast fixed; the detail enhanced when
    /// <paramref name="enhance"/> and the setting are on).
    /// </summary>
    public static async Task<Prepared> PrepareAsync(Picture source, bool enhance, IProgress<string>? status, IProgress<double>? progress, CancellationToken ct)
    {
        var settings = Settings.Current;
        enhance &= settings.EnhancePhotos;
        if (!enhance && !settings.FixTone) return new Prepared(source, source);
        var toned = source;
        if (settings.FixTone)
        {
            toned = source.Clone();
            await Task.Run(() => Tone.Normalize(toned), ct).ConfigureAwait(false);
        }
        if (!enhance) return new Prepared(toned, toned);
        var work = toned;

        var model = await EnhanceEngine.PrepareAsync(status, ct).ConfigureAwait(false);
        var (ow, oh) = OutputSize(source.Width, source.Height, settings.PhotoSize, true);
        status?.Report(ow > source.Width ? $"Enhancing and enlarging to {ow:N0} × {oh:N0}…" : "Enhancing…");
        var detail = await Task.Run(() =>
        {
            var pixels = (double)work.Width * work.Height;
            if (pixels > MaxWorkPixels)
            {
                var k = Math.Sqrt(MaxWorkPixels / pixels);
                work = ColorMath.Resize(work, Math.Max(1, (int)Math.Round(work.Width * k)), Math.Max(1, (int)Math.Round(work.Height * k)));
            }
            ow = Math.Min(ow, work.Width * EnhanceModel.Scale);
            oh = Math.Min(oh, work.Height * EnhanceModel.Scale);
            return new Enhancer(model).Run(work, ow, oh, progress, ct);
        }, ct).ConfigureAwait(false);
        return new Prepared(toned, detail);
    }

    // ---------------------------------------------------------------------------- keeping the prepared photo

    /// <summary>Folder for prepared photos waiting to be saved (raw pixels), one per running copy of the app.</summary>
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "MakeItColor", Environment.ProcessId.ToString());

    public static string Keep(Picture picture)
    {
        Directory.CreateDirectory(TempDirectory);
        var path = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".bgra");
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        writer.Write(picture.Width);
        writer.Write(picture.Height);
        writer.Write(picture.HasAlpha);
        writer.Write(picture.Pixels);
        return path;
    }

    public static Picture Read(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        var w = reader.ReadInt32();
        var h = reader.ReadInt32();
        var alpha = reader.ReadBoolean();
        var pixels = reader.ReadBytes(w * h * 4);
        if (pixels.Length != w * h * 4) throw new EndOfStreamException("The prepared photo is incomplete");
        return new Picture(w, h, pixels) { HasAlpha = alpha };
    }

    /// <summary>Removes prepared photos left by copies of the app that are no longer running (and this one's, at exit).</summary>
    public static void CleanTemp(bool own = false)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "MakeItColor");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                if (pid == Environment.ProcessId ? !own : IsRunning(pid)) continue;
                try { Directory.Delete(dir, true); } catch { /* in use */ }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clean temp");
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
