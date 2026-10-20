namespace MakeItColor.Core.Ai;

/// <summary>
/// Owns the loaded model and decides where it runs. With Processor = Auto it uses the NPU, then the best graphics card,
/// then the CPU. Compiling the model for an NPU takes a few minutes the first time (OpenVINO keeps the result), so
/// then the graphics card (or CPU) starts the work at once while the NPU is prepared in the background; once it is
/// ready, the next pictures run on the NPU.
/// </summary>
internal static class Engine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object SwapGate = new();
    private static ColorModel? s_model;
    private static Task? s_background;
    private static CancellationTokenSource s_backgroundCts = new();
    private static string s_status = "";

    /// <summary>What the engine is doing, for the status bar ("Ready on the NPU (Intel(R) AI Boost)").</summary>
    public static string Status
    {
        get => s_status;
        private set
        {
            s_status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public static event Action<string>? StatusChanged;

    /// <summary>A note about the NPU being prepared in the background (or null).</summary>
    public static string? BackgroundNote { get; private set; }

    public static Device? Current => s_model?.Device;

    public static double MillisecondsPerPicture => s_model?.MillisecondsPerPicture ?? 0;

    private static string MarkerFile => Path.Combine(AppInfo.LocalDirectory, "prepared.txt");

    /// <summary>Makes sure a model is loaded; returns its device. Throws if nothing can run it.</summary>
    public static async Task<Device> PrepareAsync(IProgress<string>? status, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_model != null) return s_model.Device;
            var report = new Progress<string>(s => { Status = s; status?.Report(s); });
            Status = "Getting ready…";
            await Hardware.EnsureProvidersAsync(report, ct).ConfigureAwait(false);
            var preference = Settings.Current.Processor;
            var candidates = Hardware.Candidates(preference);
            var prepared = ReadMarkers();

            // Auto: an NPU that failed before is skipped (choose NPU in Settings to try it again).
            if (preference == Processor.Auto)
                candidates.RemoveAll(d => d.Kind == "NPU" && prepared.TryGetValue(MarkerKey(d), out var state) && state == "failed");

            // Auto with an NPU that hasn't been compiled yet: compile it in the background, start with the next device.
            if (preference == Processor.Auto && candidates.Count > 1 && candidates[0].Kind == "NPU"
                && !prepared.ContainsKey(MarkerKey(candidates[0])))
            {
                var npu = candidates[0];
                candidates.RemoveAt(0);
                var model = await OpenFirstAsync(candidates, report, ct).ConfigureAwait(false);
                Swap(model);
                StartBackground(npu);
                return model.Device;
            }

            var chosen = await OpenFirstAsync(candidates, report, ct).ConfigureAwait(false);
            Swap(chosen);
            return chosen.Device;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Runs the model with whatever device is current (the NPU takes over as soon as it's ready).</summary>
    public static (float[] Ab, Device Device) Run(float[] input)
    {
        ColorModel model;
        lock (SwapGate) model = s_model ?? throw new InvalidOperationException("The model isn't loaded.");
        return (model.Run(input), model.Device);
    }

    /// <summary>The processor setting changed: unload, so the next picture loads the model where it should now run.</summary>
    public static void Reset()
    {
        s_backgroundCts.Cancel();
        s_backgroundCts = new CancellationTokenSource();
        BackgroundNote = null;
        Swap(null);
        Status = "";
    }

    private static async Task<ColorModel> OpenFirstAsync(List<Device> candidates, IProgress<string> status, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var device in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var file = ModelStore.For(device);
            var path = await ModelStore.EnsureAsync(file, status, ct).ConfigureAwait(false);
            status.Report(device.IsCpu
                ? "Loading the colour model on the CPU…"
                : device.Kind == "NPU" && !ReadMarkers().ContainsKey(MarkerKey(device))
                    ? $"Preparing the NPU ({device.Name}): first time only, this can take about 5 minutes…"
                    : $"Loading the colour model on the {device.Kind} ({device.Name})…");
            try
            {
                var model = await Task.Run(() => ColorModel.Open(device, path), ct).ConfigureAwait(false);
                WriteMarker(device, "ok");
                Status = ReadyText(model);
                return model;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Open model on {device} [{device.Provider}]");
                failures.Add($"{device}: {ex.Message.Split('\n')[0]}");
            }
        }
        throw new InvalidOperationException("The colour model couldn't run on this PC (" + string.Join("; ", failures) + ").");
    }

    private static void StartBackground(Device npu)
    {
        var ct = s_backgroundCts.Token;
        BackgroundNote = $"Preparing the NPU in the background (first time only, about 5 minutes). Until then the {s_model?.Device.Kind ?? "GPU"} does the work.";
        Status = ReadyText(s_model!) + " · preparing the NPU…";
        s_background = Task.Run(async () =>
        {
            try
            {
                var path = await ModelStore.EnsureAsync(ModelStore.Half, null, ct).ConfigureAwait(false);
                var model = ColorModel.Open(npu, path);
                if (ct.IsCancellationRequested)
                {
                    model.Dispose();
                    return;
                }
                WriteMarker(npu, "ok");
                Swap(model);
                BackgroundNote = null;
                Status = ReadyText(model);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Prepare NPU {npu}");
                WriteMarker(npu, "failed");
                BackgroundNote = null;
                if (s_model != null) Status = ReadyText(s_model);
            }
        }, ct);
    }

    private static void Swap(ColorModel? model)
    {
        ColorModel? old;
        lock (SwapGate)
        {
            old = s_model;
            s_model = model;
        }
        // A run on the old model may still be in progress: ColorModel.Dispose waits for it.
        if (old != null && !ReferenceEquals(old, model)) Task.Run(old.Dispose);
    }

    private static string ReadyText(ColorModel model) =>
        $"Ready on the {model.Device.Kind} ({model.Device.Name}) · about {model.MillisecondsPerPicture / 1000:0.0} s a picture";

    // ---------------------------------------------------------------------------- "prepared" markers

    private static string MarkerKey(Device device) => $"{device.Key}|{ModelStore.For(device).Sha256}";

    private static Dictionary<string, string> ReadMarkers()
    {
        var markers = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(MarkerFile)) return markers;
            foreach (var line in File.ReadAllLines(MarkerFile))
            {
                var cut = line.LastIndexOf('=');
                if (cut > 0) markers[line.Substring(0, cut)] = line.Substring(cut + 1);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Read prepared markers");
        }
        return markers;
    }

    private static void WriteMarker(Device device, string state)
    {
        if (device.IsCpu) return;
        try
        {
            var markers = ReadMarkers();
            markers[MarkerKey(device)] = state;
            Directory.CreateDirectory(AppInfo.LocalDirectory);
            File.WriteAllLines(MarkerFile, markers.Select(m => $"{m.Key}={m.Value}"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Write prepared marker");
        }
    }
}
