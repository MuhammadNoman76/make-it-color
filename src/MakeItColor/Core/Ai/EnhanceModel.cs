using System.Diagnostics;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MakeItColor.Core.Ai;

/// <summary>
/// The enhancer: Real-ESRGAN's compact general model (realesr-general-x4v3 blended 50/50 with its weak-denoise twin, as
/// Real-ESRGAN does by default; BSD-3-Clause), made by tools\model\enhance.py. It takes out blur, noise and JPEG or
/// video compression damage and makes the picture 4x larger. Fixed tiles (NPUs need a fixed size): input "input"
/// [1, 3, 256, 256] RGB 0..1, output "output" [1, 3, 1024, 1024]. It ships with the app in the "models" folder:
/// float16 (2.4 MB) for NPUs and GPUs, float32 (4.9 MB) for the CPU.
/// </summary>
internal sealed class EnhanceModel : IDisposable
{
    public const int Tile = 256;
    public const int Scale = 4;
    public const int OutTile = Tile * Scale;
    public const int InPlane = Tile * Tile;
    public const int OutPlane = OutTile * OutTile;

    private readonly InferenceSession _session;
    private readonly object _gate = new();

    private EnhanceModel(InferenceSession session, Device device)
    {
        _session = session;
        Device = device;
    }

    public Device Device { get; }

    /// <summary>Time for one tile, measured when the model was checked.</summary>
    public double MillisecondsPerTile { get; private set; }

    public static string FileFor(Device device) => device.IsCpu ? "makeitcolor-enhance-fp32.onnx" : "makeitcolor-enhance-fp16.onnx";

    /// <summary>The model file: the app's "models" folder, or MAKEITCOLOR_MODELS when it holds one.</summary>
    public static string PathFor(Device device)
    {
        var name = FileFor(device);
        var custom = Environment.GetEnvironmentVariable(ModelStore.DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(Path.Combine(custom, name))) return Path.Combine(custom, name);
        return Path.Combine(AppContext.BaseDirectory, "models", name);
    }

    /// <summary>Runs one tile: <paramref name="input"/> is 3 x 256 x 256 RGB, <paramref name="output"/> 3 x 1024 x 1024.</summary>
    public void Run(float[] input, float[] output)
    {
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, Tile, Tile });
        lock (_gate)
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("input", tensor) });
            var result = results[0].AsTensor<float>();
            if (result is DenseTensor<float> dense) dense.Buffer.Span.CopyTo(output);
            else
            {
                var i = 0;
                foreach (var v in result) output[i++] = v;
            }
        }
    }

    /// <summary>Loads the enhancer on <paramref name="device"/> and checks it gives the right result there.</summary>
    public static EnhanceModel Open(Device device)
    {
        var path = PathFor(device);
        var clock = Stopwatch.StartNew();
        InferenceSession? session = null;
        try
        {
            using (var options = Hardware.Options(device)) session = new InferenceSession(path, options);
            var loadMs = clock.ElapsedMilliseconds;
            var model = new EnhanceModel(session, device);
            var (rms, ms) = model.Check();
            model.MillisecondsPerTile = ms;
            Log.Info($"Enhancer {Path.GetFileName(path)} on {device} [{device.Provider}]: loaded in {loadMs} ms, {ms:0} ms a tile, check difference {rms:0.0000}");
            if (double.IsNaN(rms) || rms > 0.01)
                throw new InvalidDataException($"the {device.Kind} gives wrong results with the enhancer (check difference {rms:0.000})");
            return model;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a known gray tile (Assets\enhance-check-input.bin) and compares the result, averaged over 8 x 8 blocks, with
    /// PyTorch's (Assets\enhance-check-expected.bin, 3 x 128 x 128). Returns the RMS difference (0..1 units) and the
    /// time per tile.
    /// </summary>
    private (double Rms, double Ms) Check()
    {
        var gray = ReadResource("enhance-check-input.bin");
        var expectedBytes = ReadResource("enhance-check-expected.bin");
        var expected = new float[expectedBytes.Length / 4];
        Buffer.BlockCopy(expectedBytes, 0, expected, 0, expectedBytes.Length);

        var input = new float[3 * InPlane];
        for (var i = 0; i < InPlane; i++) input[i] = input[InPlane + i] = input[2 * InPlane + i] = gray[i] / 255f;
        var output = new float[3 * OutPlane];
        Run(input, output);
        var clock = Stopwatch.StartNew();
        Run(input, output);
        var ms = clock.Elapsed.TotalMilliseconds;

        const int blocks = OutTile / 8;
        double sum = 0;
        for (var c = 0; c < 3; c++)
            for (var by = 0; by < blocks; by++)
                for (var bx = 0; bx < blocks; bx++)
                {
                    double mean = 0;
                    for (var y = 0; y < 8; y++)
                        for (var x = 0; x < 8; x++) mean += Math.Clamp(output[c * OutPlane + (by * 8 + y) * OutTile + bx * 8 + x], 0f, 1f);
                    var d = mean / 64 - expected[(c * blocks + by) * blocks + bx];
                    sum += d * d;
                }
        return (Math.Sqrt(sum / expected.Length), ms);
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is missing");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        lock (_gate) _session.Dispose();
    }
}

/// <summary>
/// Owns the loaded enhancer. Auto: the best graphics card, then the NPU, then the CPU. The colour model prefers the
/// NPU, so in a video the two models run side by side on different chips (on an Intel Core Ultra 5 135U the
/// enhancer takes 75 ms a tile on the graphics and 131 ms on the NPU while the NPU also colours). Every device is
/// checked first and one that gives wrong results is skipped.
/// </summary>
internal static class EnhanceEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static EnhanceModel? s_model;

    public static Device? Current => s_model?.Device;

    /// <summary>True when the enhancer runs on an NPU or a GPU (fast enough for every frame of a video).</summary>
    public static bool IsAccelerated => s_model is { Device.IsCpu: false };

    public static async Task<EnhanceModel> PrepareAsync(IProgress<string>? status, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_model != null) return s_model;
            await Hardware.EnsureProvidersAsync(status, ct).ConfigureAwait(false);
            Exception? last = null;
            var preference = Settings.Current.Processor;
            var candidates = Hardware.Candidates(preference);
            if (preference == Processor.Auto) candidates = candidates.OrderBy(d => d.Kind switch { "GPU" => 0, "NPU" => 1, _ => 2 }).ToList();
            foreach (var device in candidates)
            {
                ct.ThrowIfCancellationRequested();
                status?.Report($"Loading the enhancer on the {device.Kind} ({device.Name})…");
                try
                {
                    s_model = await Task.Run(() => EnhanceModel.Open(device), ct).ConfigureAwait(false);
                    return s_model;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "Enhancer on " + device);
                    last = ex;
                }
            }
            throw new InvalidOperationException("The enhancer can't run on this PC" + (last != null ? ": " + last.Message : ""));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Reset()
    {
        Gate.Wait();
        try
        {
            s_model?.Dispose();
            s_model = null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
