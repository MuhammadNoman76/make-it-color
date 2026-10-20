using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MakeItColor.Core.Video;

/// <summary>What ffprobe says about a video.</summary>
internal sealed class VideoInfo
{
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Frame rate as a fraction (e.g. 30000/1001).</summary>
    public string Rate { get; set; } = "25";
    public double Fps { get; set; } = 25;
    public double Duration { get; set; }
    public long Frames { get; set; }
    public bool HasAudio { get; set; }
    public string? AudioCodec { get; set; }
    public string SampleAspect { get; set; } = "1:1";
    public string Codec { get; set; } = "";
}

/// <summary>
/// Runs FFmpeg (the LGPL build in the app's "ffmpeg" folder) to read videos as raw frames and to write the coloured
/// frames back as H.264 MP4 with the original sound.
/// </summary>
internal static class FFmpeg
{
    public static string? Folder { get; } = FindFolder();

    public static bool Available => Folder != null;

    private static string? FindFolder()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (File.Exists(Path.Combine(bundled, "ffmpeg.exe")) && File.Exists(Path.Combine(bundled, "ffprobe.exe"))) return bundled;
        // Development: FFmpeg on the PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"))) return dir;
            }
            catch
            {
                // bad PATH entry
            }
        }
        return null;
    }

    private static string Exe(string name) => Path.Combine(Folder ?? throw new InvalidOperationException("FFmpeg is missing."), name);

    public static VideoInfo Probe(string path)
    {
        var psi = Start("ffprobe.exe", "-v", "error", "-print_format", "json", "-show_streams", "-show_format", path);
        psi.RedirectStandardOutput = true;
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe didn't start.");
        var json = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException("This video can't be read: " + FirstLine(errors));

        using var doc = JsonDocument.Parse(json);
        var info = new VideoInfo();
        var foundVideo = false;
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = Str(s, "codec_type");
            if (type == "video" && !foundVideo && !(s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1))
            {
                foundVideo = true;
                info.Codec = Str(s, "codec_name") ?? "";
                info.Width = s.GetProperty("width").GetInt32();
                info.Height = s.GetProperty("height").GetInt32();
                var rate = Str(s, "avg_frame_rate");
                if (string.IsNullOrEmpty(rate) || rate == "0/0") rate = Str(s, "r_frame_rate");
                if (!string.IsNullOrEmpty(rate) && rate != "0/0")
                {
                    info.Rate = rate!;
                    info.Fps = Fraction(rate!);
                }
                if (long.TryParse(Str(s, "nb_frames"), out var frames)) info.Frames = frames;
                if (double.TryParse(Str(s, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) info.Duration = d;
                var sar = Str(s, "sample_aspect_ratio");
                if (!string.IsNullOrEmpty(sar) && sar != "0:1" && sar != "N/A") info.SampleAspect = sar!;
                // Phones store portrait video sideways with a rotation; FFmpeg turns the frames upright when decoding.
                if (Math.Abs(Rotation(s)) % 180 == 90) (info.Width, info.Height) = (info.Height, info.Width);
            }
            else if (type == "audio" && !info.HasAudio)
            {
                info.HasAudio = true;
                info.AudioCodec = Str(s, "codec_name");
            }
        }
        if (!foundVideo) throw new InvalidDataException("This file has no video.");
        if (info.Duration <= 0 && doc.RootElement.TryGetProperty("format", out var format)
            && double.TryParse(Str(format, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fd)) info.Duration = fd;
        if (info.Frames <= 0 && info.Duration > 0) info.Frames = (long)Math.Round(info.Duration * info.Fps);
        return info;
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var list))
            foreach (var item in list.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r) && r.TryGetDouble(out var deg)) return (int)Math.Round(deg);
        if (stream.TryGetProperty("tags", out var tags) && int.TryParse(Str(tags, "rotate"), out var rot)) return rot;
        return 0;
    }

    /// <summary>Decoder: raw BGRA frames on stdout at a constant frame rate (so sound stays in sync).</summary>
    public static Process StartDecoder(string path, VideoInfo info)
    {
        var psi = Start("ffmpeg.exe", "-v", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-an", "-sn", "-dn",
            "-fps_mode", "cfr", "-r", info.Rate, "-f", "rawvideo", "-pix_fmt", "bgra", "-");
        psi.RedirectStandardOutput = true;
        return Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
    }

    /// <summary>Video encoders to try, best first: Windows' Media Foundation H.264 (hardware where there is some), then OpenH264.</summary>
    public static readonly string[] Encoders = { "h264_mf", "libopenh264" };

    /// <summary>Encoder: raw BGRA frames on stdin plus the original file's sound, to an H.264 MP4.</summary>
    public static Process StartEncoder(string input, string output, VideoInfo info, string encoder, long bitrate)
    {
        var args = new List<string>
        {
            "-v", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{info.Width}x{info.Height}", "-r", info.Rate, "-i", "-",
        };
        if (info.HasAudio) args.AddRange(new[] { "-i", input });
        args.AddRange(new[] { "-map", "0:v:0" });
        if (info.HasAudio) args.AddRange(new[] { "-map", "1:a:0?" });

        // H.264 in 4:2:0 needs even sizes: drop the last row / column if needed. BT.709, TV range, tagged.
        var sar = info.SampleAspect.Replace(':', '/');
        args.AddRange(new[] { "-vf", $"crop=trunc(iw/2)*2:trunc(ih/2)*2,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setsar={sar}" });
        args.AddRange(new[] { "-c:v", encoder, "-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", (bitrate * 3 / 2).ToString(CultureInfo.InvariantCulture),
            "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture) });
        if (encoder == "h264_mf") args.AddRange(new[] { "-rate_control", "u_vbr", "-scenario", "archive" });
        args.AddRange(new[] { "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv" });

        if (info.HasAudio)
        {
            // MP4 takes these as they are; anything else becomes AAC.
            var copy = info.AudioCodec is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "opus" or "flac";
            if (copy) args.AddRange(new[] { "-c:a", "copy" });
            else args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        }
        args.AddRange(new[] { "-movflags", "+faststart", "-shortest", output });

        var psi = Start("ffmpeg.exe", args.ToArray());
        psi.RedirectStandardInput = true;
        return Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg didn't start.");
    }

    /// <summary>Bit rate for the output: bits per pixel per frame by quality, at least 1.5 Mbit/s.</summary>
    public static long Bitrate(VideoInfo info, VideoQuality quality)
    {
        var bpp = quality switch { VideoQuality.Standard => 0.08, VideoQuality.Best => 0.22, _ => 0.14 };
        return (long)Math.Max(1_500_000, info.Width * (double)info.Height * Math.Max(1, info.Fps) * bpp);
    }

    private static ProcessStartInfo Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(Exe(exe))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "unknown error";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static double Fraction(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0) return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 25;
    }
}
