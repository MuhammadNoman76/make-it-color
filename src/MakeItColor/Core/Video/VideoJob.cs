using System.Collections.Concurrent;
using System.Diagnostics;
using MakeItColor.Core.Ai;

namespace MakeItColor.Core.Video;

/// <summary>Progress of a video, for the window.</summary>
internal sealed record VideoProgress(long Done, long Total, double FramesPerSecond, TimeSpan Elapsed, TimeSpan? Remaining, string Device,
    Picture? Before = null, Picture? After = null);

/// <summary>
/// Colours a whole video: FFmpeg decodes frames, each frame's 512x512 input is prepared, the model runs (one frame at a
/// time, on the NPU / GPU / CPU), colours are kept steady between frames, the full-size colour frame is finished, and
/// FFmpeg encodes it with the original sound. Each step runs on its own thread so the model never waits for the CPU.
/// </summary>
internal sealed class VideoJob
{
    /// <summary>Full video clean-up: the enhancer works on frames at most this large (longest side), which suits old
    /// films (their real detail is far below HD) and keeps it to 8 tiles a frame; the result is the original size.</summary>
    public const int EnhanceWorkSize = 800;

    private sealed class Frame
    {
        public long Index;
        public Picture Picture = null!;
        public Picture? Original;
        public bool FirstOfShot;
        public float[] Input = null!;
        public float[] SmallL = null!;
        public float[]? Ab;
    }

    public VideoJob(string input, string output)
    {
        Input = input;
        Output = output;
    }

    public string Input { get; }
    public string Output { get; }
    public VideoInfo? Info { get; private set; }
    public string Encoder { get; private set; } = "";
    public int Cuts { get; private set; }
    /// <summary>The clean-up used: Full, Light or Off.</summary>
    public VideoEnhance Enhance { get; private set; }
    public bool FixTone { get; private set; }

    public async Task RunAsync(IProgress<VideoProgress>? progress, IProgress<string>? status, CancellationToken ct)
    {
        if (!FFmpeg.Available) throw new InvalidOperationException("The video tools (FFmpeg) are missing. Reinstall MakeItColor.");
        var info = await Task.Run(() => FFmpeg.Probe(Input), ct).ConfigureAwait(false);
        Info = info;
        await Engine.PrepareAsync(status, ct).ConfigureAwait(false);

        // Clean-up: Auto is Full when the enhancer runs on an NPU or a graphics card, Light on the CPU.
        var mode = Settings.Current.VideoEnhance;
        EnhanceModel? enhancer = null;
        if (mode is VideoEnhance.Auto or VideoEnhance.Full)
        {
            try
            {
                enhancer = await EnhanceEngine.PrepareAsync(status, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Enhancer for video");
            }
            if (mode == VideoEnhance.Auto) mode = enhancer is { Device.IsCpu: false } ? VideoEnhance.Full : VideoEnhance.Light;
            else if (enhancer == null) mode = VideoEnhance.Light;
        }
        Enhance = mode;
        FixTone = Settings.Current.FixTone;
        status?.Report("Colouring the video…");

        Exception? lastError = null;
        foreach (var encoder in FFmpeg.Encoders)
        {
            try
            {
                Encoder = encoder;
                await Task.Run(() => Run(info, encoder, mode == VideoEnhance.Full ? enhancer : null, mode == VideoEnhance.Light, progress, ct), ct).ConfigureAwait(false);
                return;
            }
            catch (EncoderException ex)
            {
                // That encoder isn't available on this PC: try the next one from the start.
                Log.Error(ex, $"Video encoder {encoder}");
                lastError = ex;
                TryDelete(Output);
            }
        }
        throw new InvalidOperationException("The colour video couldn't be written: " + lastError?.Message);
    }

    private sealed class EncoderException : Exception
    {
        public EncoderException(string message) : base(message) { }
    }

    private void Run(VideoInfo info, string encoder, EnhanceModel? enhancer, bool light, IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        var w = info.Width;
        var h = info.Height;
        var frameBytes = w * h * 4;
        var settings = Settings.Current;
        var temp = Output + ".part.mp4";
        TryDelete(temp);

        using var decoder = FFmpeg.StartDecoder(Input, info);
        var decodeErrors = new System.Text.StringBuilder();
        decoder.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (decodeErrors) decodeErrors.AppendLine(e.Data); };
        decoder.BeginErrorReadLine();

        using var encoderProcess = FFmpeg.StartEncoder(Input, temp, info, encoder, FFmpeg.Bitrate(info, settings.VideoQuality));
        var encodeErrors = new System.Text.StringBuilder();
        encoderProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (encodeErrors) encodeErrors.AppendLine(e.Data); };
        encoderProcess.BeginErrorReadLine();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var decoded = new BlockingCollection<Frame>(3);
        var toned = new BlockingCollection<Frame>(2);
        var prepared = new BlockingCollection<Frame>(2);
        var keepOriginal = settings.FixTone || light || enhancer != null;
        var tone = new VideoTone<Frame>(f => f.Picture, settings.FixTone);
        var colored = new BlockingCollection<Frame>(2);
        var clock = Stopwatch.StartNew();
        long done = 0;
        var lastReport = TimeSpan.Zero;
        var device = "";
        var steady = settings.SteadyColors ? new SteadyColors() : null;
        Exception? failure = null;

        void Fail(Exception ex)
        {
            failure ??= ex;
            try { stop.Cancel(); } catch { /* already */ }
        }

        var read = Task.Run(() =>
        {
            try
            {
                var stream = decoder.StandardOutput.BaseStream;
                for (long index = 0; ; index++)
                {
                    var picture = new Picture(w, h);
                    if (!ReadExactly(stream, picture.Pixels, token)) break;
                    decoded.Add(new Frame { Index = index, Picture = picture }, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
            finally { decoded.CompleteAdding(); }
        });

        // Brightness, contrast and flicker (frames come out 3 frames late), the colour model's input (from the frame with
        // only that fixed, like photos), then the light clean-up.
        var toneStage = Task.Run(() =>
        {
            var cleanup = light ? new LightCleanup() : null;
            void Pass(List<Frame> ready)
            {
                foreach (var f in ready)
                {
                    var first = f.FirstOfShot = tone.IsFirstOfShot(f);
                    f.Input = new float[3 * ColorModel.Plane];
                    f.SmallL = new float[ColorModel.Plane];
                    ColorMath.ModelInput(f.Picture, f.Input, f.SmallL);
                    if (cleanup != null)
                    {
                        if (first) cleanup.Reset();
                        cleanup.Apply(f.Picture);
                    }
                    toned.Add(f, token);
                }
            }
            try
            {
                foreach (var f in decoded.GetConsumingEnumerable(token))
                {
                    if (keepOriginal) f.Original = f.Picture.Clone();
                    Pass(tone.Push(f));
                }
                Pass(tone.Flush());
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
            finally { toned.CompleteAdding(); }
        });

        // Full clean-up: the enhancer on a frame of at most 800 pixels, back to the original size.
        var enhanceStage = Task.Run(() =>
        {
            var tiles = enhancer != null ? new Enhancer(enhancer) : null;
            var steadyDetail = new SteadyDetail();
            var k = Math.Min(1.0, (double)EnhanceWorkSize / Math.Max(w, h));
            int ww = Math.Max(1, (int)Math.Round(w * k)), wh = Math.Max(1, (int)Math.Round(h * k));
            try
            {
                foreach (var f in toned.GetConsumingEnumerable(token))
                {
                    if (tiles != null)
                    {
                        var work = ww == w && wh == h ? f.Picture : ColorMath.Resize(f.Picture, ww, wh);
                        var result = tiles.Run(work, w, h, null, token);
                        if (f.FirstOfShot) steadyDetail.Reset();
                        steadyDetail.Apply(f.Picture, result);
                        f.Picture = result;
                    }
                    prepared.Add(f, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
            finally { prepared.CompleteAdding(); }
        });

        var infer = Task.Run(() =>
        {
            try
            {
                foreach (var f in prepared.GetConsumingEnumerable(token))
                {
                    var (ab, used) = Engine.Run(f.Input);
                    device = used.ToString();
                    steady?.Apply(ab, f.SmallL);
                    f.Ab = ab;
                    f.Input = null!;
                    colored.Add(f, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
            finally { colored.CompleteAdding(); }
        });

        var write = Task.Run(() =>
        {
            var colorizer = new Colorizer();
            try
            {
                var stream = encoderProcess.StandardInput.BaseStream;
                foreach (var f in colored.GetConsumingEnumerable(token))
                {
                    var color = colorizer.Finish(f.Picture, f.Ab!, settings.Vividness);
                    try
                    {
                        stream.Write(color.Pixels, 0, frameBytes);
                    }
                    catch (IOException ex)
                    {
                        lock (encodeErrors) throw new EncoderException(FFmpeg.FirstLine(encodeErrors.ToString() + " " + ex.Message));
                    }
                    var n = Interlocked.Increment(ref done);
                    if (progress != null && (clock.Elapsed - lastReport > TimeSpan.FromSeconds(0.5) || n == 1))
                    {
                        lastReport = clock.Elapsed;
                        var fps = n / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                        TimeSpan? remaining = info.Frames > n && fps > 0 ? TimeSpan.FromSeconds((info.Frames - n) / fps) : null;
                        progress.Report(new VideoProgress(n, info.Frames, fps, clock.Elapsed, remaining, device, f.Original ?? f.Picture, color));
                    }
                }
                stream.Flush();
                stream.Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Fail(ex); }
        });

        try
        {
            Task.WaitAll(read, toneStage, enhanceStage, infer, write);
        }
        catch (AggregateException)
        {
            // the stage recorded its failure
        }

        if (ct.IsCancellationRequested || failure != null)
        {
            TryKill(decoder);
            TryKill(encoderProcess);
            TryDelete(temp);
            ct.ThrowIfCancellationRequested();
            if (failure is EncoderException) throw failure;
            throw new InvalidOperationException(failure!.Message, failure);
        }

        decoder.WaitForExit();
        encoderProcess.WaitForExit();
        Cuts = Math.Max(steady?.Cuts ?? 0, tone.Cuts);
        if (encoderProcess.ExitCode != 0)
        {
            TryDelete(temp);
            lock (encodeErrors) throw new EncoderException(FFmpeg.FirstLine(encodeErrors.ToString()));
        }
        if (decoder.ExitCode != 0 && done == 0)
        {
            TryDelete(temp);
            lock (decodeErrors) throw new InvalidDataException("This video can't be read: " + FFmpeg.FirstLine(decodeErrors.ToString()));
        }
        File.Move(temp, Output, true);
        progress?.Report(new VideoProgress(done, done, done / Math.Max(0.001, clock.Elapsed.TotalSeconds), clock.Elapsed, TimeSpan.Zero, device));
        Log.Info($"Video {Input} -> {Output}: {done} frames in {clock.Elapsed.TotalSeconds:0} s on {device}, {encoder}, {Cuts} cuts, clean-up {Enhance}{(enhancer != null ? " on " + enhancer.Device : "")}, tone {FixTone}");
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(true); } catch { /* gone */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
