using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace MakeItColor.Core.Ai;

/// <summary>A chip the model can run on.</summary>
internal sealed class Device
{
    public Device(string kind, string name, string provider, OrtEpDevice? epDevice)
    {
        Kind = kind;
        Name = name;
        Provider = provider;
        EpDevice = epDevice;
    }

    /// <summary>NPU, GPU or CPU.</summary>
    public string Kind { get; }
    /// <summary>The hardware's name, e.g. "Intel(R) AI Boost".</summary>
    public string Name { get; }
    /// <summary>The ONNX Runtime execution provider, e.g. "OpenVINOExecutionProvider".</summary>
    public string Provider { get; }
    /// <summary>Null for ONNX Runtime's own CPU code.</summary>
    public OrtEpDevice? EpDevice { get; }

    public bool IsCpu => EpDevice == null;

    /// <summary>Identifies the device for "already prepared" markers.</summary>
    public string Key => $"{Kind}-{Provider}-{Name}";

    public override string ToString() => $"{Kind} ({Name})";
}

/// <summary>
/// Picks the chips for the model. Windows ML first fetches the execution providers Windows has certified for this PC
/// (Intel OpenVINO, Qualcomm QNN, AMD Vitis AI, NVIDIA TensorRT ...), which is what makes NPUs usable; DirectML (any
/// DirectX 12 GPU) and the CPU are always there. Order for Auto: NPU, then the best GPU, then the CPU.
/// </summary>
internal static class Hardware
{
    private static readonly SemaphoreSlim ProvidersGate = new(1, 1);
    private static bool s_providersReady;

    /// <summary>Downloads (first time) and registers Windows ML's execution providers. Never throws (except on cancel).</summary>
    public static async Task EnsureProvidersAsync(IProgress<string>? status, CancellationToken ct)
    {
        await ProvidersGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (s_providersReady) return;
            s_providersReady = true; // one attempt per run; CPU and DirectML work without it
            var catalog = ExecutionProviderCatalog.GetDefault();
            foreach (var provider in catalog.FindAllProviders())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var state = provider.ReadyState;
                    if (state == ExecutionProviderReadyState.NotPresent)
                        status?.Report($"Getting the {FriendlyProvider(provider.Name)} driver from Windows (first time only)…");
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var result = await provider.EnsureReadyAsync();
                    var registered = result.Status == ExecutionProviderReadyResultState.Success && provider.TryRegister();
                    Log.Info($"Windows ML provider {provider.Name}: was {state}, {result.Status}, registered {registered} ({clock.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Windows ML provider " + provider.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Windows ML missing or blocked (policy): DirectML and the CPU still work.
            Log.Error(ex, "Windows ML execution providers");
        }
        finally
        {
            ProvidersGate.Release();
        }
    }

    /// <summary>The chips to try, best first, for the user's choice. The CPU is always last, as the fallback.</summary>
    public static List<Device> Candidates(Processor preference)
    {
        var npus = new List<Device>();
        var gpus = new List<(Device Device, int Rank)>();
        var cpuName = "CPU";
        try
        {
            foreach (var d in OrtEnv.Instance().GetEpDevices())
            {
                var name = Describe(d);
                switch (d.HardwareDevice.Type)
                {
                    case OrtHardwareDeviceType.NPU:
                        // "OpenVINOExecutionProvider.AUTO" lets OpenVINO pick: prefer the plain NPU entry.
                        if (!d.EpName.Contains('.')) npus.Insert(0, new Device("NPU", name, d.EpName, d));
                        break;
                    case OrtHardwareDeviceType.GPU:
                        if (d.EpName.Contains('.')) break;
                        gpus.Add((new Device("GPU", name, d.EpName, d), GpuRank(d)));
                        break;
                    case OrtHardwareDeviceType.CPU:
                        if (d.EpName == "CPUExecutionProvider") cpuName = name;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "List devices");
        }

        var gpuList = gpus.OrderBy(g => g.Rank).Select(g => g.Device).ToList();
        var cpu = new Device("CPU", cpuName, "CPUExecutionProvider", null);
        var list = preference switch
        {
            Processor.Npu => npus.Concat(gpuList),
            Processor.Gpu => gpuList,
            Processor.Cpu => Enumerable.Empty<Device>(),
            _ => npus.Concat(gpuList),
        };
        return list.Append(cpu).ToList();
    }

    /// <summary>
    /// Lower is better: discrete GPUs first, then the GPU maker's own provider (NVIDIA TensorRT, AMD MIGraphX, Intel
    /// OpenVINO: faster than DirectML on their own chips; on Intel graphics 572 vs 754 ms a picture), then DirectML.
    /// </summary>
    private static int GpuRank(OrtEpDevice d)
    {
        var discrete = Meta(d.HardwareDevice.Metadata, "Discrete") == "1"
                       || string.Equals(Meta(d.EpMetadata, "DEVICE_TYPE"), "discrete", StringComparison.OrdinalIgnoreCase);
        var provider = d.EpName == "DmlExecutionProvider" ? 1 : 0;
        return (discrete ? 0 : 10) + provider;
    }

    private static string Describe(OrtEpDevice d)
    {
        var name = Meta(d.HardwareDevice.Metadata, "Description") ?? Meta(d.EpMetadata, "FULL_DEVICE_NAME") ?? d.HardwareDevice.Vendor;
        return string.IsNullOrWhiteSpace(name) ? d.HardwareDevice.Type.ToString() : name!.Trim();
    }

    private static string? Meta(OrtKeyValuePairs pairs, string key)
    {
        try { return pairs.Entries.TryGetValue(key, out var value) ? value : null; }
        catch { return null; }
    }

    public static string FriendlyProvider(string name) => name switch
    {
        "OpenVINOExecutionProvider" => "Intel OpenVINO",
        "QNNExecutionProvider" => "Qualcomm QNN",
        "VitisAIExecutionProvider" => "AMD Vitis AI",
        "MIGraphXExecutionProvider" => "AMD MIGraphX",
        "DmlExecutionProvider" => "DirectML",
        _ when name.StartsWith("NvTensorRT", StringComparison.OrdinalIgnoreCase) => "NVIDIA TensorRT",
        _ => name.Replace("ExecutionProvider", ""),
    };

    /// <summary>Session settings for a device. The input size is fixed (512x512), which NPUs need.</summary>
    public static SessionOptions Options(Device device)
    {
        var options = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (device.EpDevice == null)
        {
            // Leave a few cores for decoding video and for the colour maths around the model.
            options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 2);
            return options;
        }
        var providerOptions = new Dictionary<string, string>();
        if (device.Provider == "DmlExecutionProvider")
        {
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            // ONNX Runtime's MatMul+Add -> Gemm fusion gives wrong results for this model's 4D MatMuls on DirectML.
            options.AddSessionConfigEntry("optimization.disable_specified_optimizers", "MatMulAddFusion");
        }
        else if (device.Provider == "OpenVINOExecutionProvider")
        {
            // Keep compiled models between runs: compiling the model for an Intel NPU takes minutes the first time.
            try
            {
                var cache = Path.Combine(AppInfo.LocalDirectory, "openvino-cache");
                Directory.CreateDirectory(cache);
                var ovDevice = device.Kind == "NPU" ? "NPU" : device.Kind == "GPU" ? "GPU" : "CPU";
                providerOptions["load_config"] = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    [ovDevice] = new Dictionary<string, string> { ["CACHE_DIR"] = cache },
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "OpenVINO cache folder");
            }
        }
        options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device.EpDevice }, providerOptions);
        return options;
    }
}
