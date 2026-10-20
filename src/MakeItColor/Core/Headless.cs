using System.Diagnostics;
using System.Text.Json;
using MakeItColor.Core.Ai;
using MakeItColor.Core.Video;

namespace MakeItColor.Core;

/// <summary>
/// For tests and scripts, without a window:
///   MakeItColor.exe --colorize &lt;input&gt; &lt;output&gt; [--processor auto|npu|gpu|cpu] [--vividness 1.0]
///                   [--no-steady] [--quality standard|high|best] [--report report.json]
///                   [--tone on|off] [--enhance on|off] [--size auto|original|double|quadruple]
///                   [--video-enhance auto|full|light|off] [--no-color]   (--no-color: photo, save the prepared picture)
///   MakeItColor.exe --prepare [--processor ...] [--report report.json]   (download and load the model, nothing else)
/// Settings aren't changed. The report says which device ran the model and how long it took.
/// </summary>
internal static class Headless
{
    public static bool Handles(string[] args) => args.Any(a => a is "--colorize" or "--prepare");

    public static async Task<int> RunAsync(string[] args)
    {
        var report = new Dictionary<string, object?>();
        var reportPath = Value(args, "--report");
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var status = new Progress<string>(s => log.Add($"{clock.Elapsed.TotalSeconds:0.0}s {s}"));
        try
        {
            Settings.Load();
            if (Enum.TryParse<Processor>(Value(args, "--processor") ?? "", true, out var processor)) Settings.Current.Processor = processor;
            if (Enum.TryParse<VideoQuality>(Value(args, "--quality") ?? "", true, out var quality)) Settings.Current.VideoQuality = quality;
            if (double.TryParse(Value(args, "--vividness"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vivid))
                Settings.Current.Vividness = Math.Clamp(vivid, 0.5, 1.5);
            if (args.Contains("--no-steady")) Settings.Current.SteadyColors = false;
            if (Value(args, "--tone") is { } tone) Settings.Current.FixTone = tone == "on";
            if (Value(args, "--enhance") is { } enhance) Settings.Current.EnhancePhotos = enhance == "on";
            if (Enum.TryParse<PhotoSize>(Value(args, "--size") ?? "", true, out var size)) Settings.Current.PhotoSize = size;
            if (Enum.TryParse<VideoEnhance>(Value(args, "--video-enhance") ?? "", true, out var videoEnhance)) Settings.Current.VideoEnhance = videoEnhance;
            report["tone"] = Settings.Current.FixTone;
            report["processor"] = Settings.Current.Processor.ToString();

            var device = await Engine.PrepareAsync(status, CancellationToken.None);
            report["device"] = device.ToString();
            report["provider"] = device.Provider;
            report["msPerPicture"] = Math.Round(Engine.MillisecondsPerPicture, 1);
            report["prepareSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            report["backgroundNote"] = Engine.BackgroundNote;

            var i = Array.IndexOf(args, "--colorize");
            if (i >= 0)
            {
                var input = Path.GetFullPath(args[i + 1]);
                var output = Path.GetFullPath(args[i + 2]);
                var start = clock.Elapsed;
                if (PictureIO.IsVideo(input))
                {
                    var job = new VideoJob(input, output);
                    VideoProgress? last = null;
                    await job.RunAsync(new Progress<VideoProgress>(p => last = p), status, CancellationToken.None);
                    report["kind"] = "video";
                    report["frames"] = last?.Done;
                    report["framesPerSecond"] = last == null ? null : Math.Round(last.FramesPerSecond, 2);
                    report["size"] = $"{job.Info?.Width}x{job.Info?.Height}";
                    report["fps"] = job.Info?.Fps;
                    report["encoder"] = job.Encoder;
                    report["cuts"] = job.Cuts;
                    report["enhance"] = job.Enhance.ToString();
                    report["enhanceDevice"] = EnhanceEngine.Current?.ToString();
                    report["device"] = last?.Device ?? report["device"];
                }
                else
                {
                    var original = PictureIO.Load(input);
                    var timings = new Dictionary<string, double>();
                    var step = Stopwatch.StartNew();
                    var prepared = await PhotoPrep.PrepareAsync(original, true, status, null, CancellationToken.None);
                    var source = prepared.Detail;
                    timings["prepare"] = step.Elapsed.TotalMilliseconds;
                    report["enhance"] = Settings.Current.EnhancePhotos;
                    report["enhanceDevice"] = Settings.Current.EnhancePhotos ? EnhanceEngine.Current?.ToString() : null;
                    report["outputSize"] = $"{source.Width}x{source.Height}";
                    if (args.Contains("--no-color"))
                    {
                        PictureIO.Save(source, output, Path.GetExtension(output).ToLowerInvariant() is ".jpg" or ".jpeg" ? PhotoFormat.Jpeg : PhotoFormat.Png);
                        report["kind"] = "photo";
                        report["size"] = $"{original.Width}x{original.Height}";
                        report["ms"] = timings.ToDictionary(t => t.Key, t => Math.Round(t.Value, 1));
                        report["seconds"] = Math.Round((clock.Elapsed - start).TotalSeconds, 2);
                        report["output"] = output;
                        report["status"] = "ok";
                        return 0;
                    }
                    step.Restart();
                    var modelInput = new float[3 * ColorModel.Plane];
                    var smallL = new float[ColorModel.Plane];
                    ColorMath.ModelInput(prepared.ForModel, modelInput, smallL);
                    timings["input"] = step.Elapsed.TotalMilliseconds;
                    step.Restart();
                    var (ab, used) = Engine.Run(modelInput);
                    timings["model"] = step.Elapsed.TotalMilliseconds;
                    step.Restart();
                    var color = new Colorizer().Finish(source, ab, Settings.Current.Vividness);
                    timings["finish"] = step.Elapsed.TotalMilliseconds;
                    var format = Path.GetExtension(output).ToLowerInvariant() is ".jpg" or ".jpeg" ? PhotoFormat.Jpeg : PhotoFormat.Png;
                    PictureIO.Save(color, output, format);
                    if (Value(args, "--dump-ab") is { } dump)
                    {
                        var bytes = new byte[ab.Length * 4];
                        Buffer.BlockCopy(ab, 0, bytes, 0, bytes.Length);
                        File.WriteAllBytes(dump, bytes);
                    }
                    report["kind"] = "photo";
                    report["size"] = $"{original.Width}x{original.Height}";
                    report["device"] = used.ToString();
                    report["ms"] = timings.ToDictionary(t => t.Key, t => Math.Round(t.Value, 1));
                }
                report["seconds"] = Math.Round((clock.Elapsed - start).TotalSeconds, 2);
                report["output"] = output;
            }
            report["status"] = "ok";
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Headless");
            report["status"] = "failed";
            report["error"] = ex.Message;
            return 1;
        }
        finally
        {
            report["log"] = log;
            report["totalSeconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            if (reportPath != null)
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static string? Value(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
