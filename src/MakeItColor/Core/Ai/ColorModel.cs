using System.Diagnostics;
using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MakeItColor.Core.Ai;

/// <summary>The colorization model, loaded on one device. Input "gray" [1, 3, 512, 512]; output "ab" [1, 2, 512, 512].</summary>
internal sealed class ColorModel : IDisposable
{
    public const int Plane = ColorMath.Size * ColorMath.Size;

    private readonly InferenceSession _session;
    private readonly object _gate = new();

    private ColorModel(InferenceSession session, Device device)
    {
        _session = session;
        Device = device;
    }

    public Device Device { get; }

    /// <summary>Time for one picture, measured when the model was checked.</summary>
    public double MillisecondsPerPicture { get; private set; }

    /// <summary>Runs the model: <paramref name="input"/> is 3 x 512 x 512; returns a and b, 2 x 512 x 512.</summary>
    public float[] Run(float[] input)
    {
        var tensor = new DenseTensor<float>(input, new[] { 1, 3, ColorMath.Size, ColorMath.Size });
        lock (_gate)
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor("gray", tensor) });
            var output = results[0].AsTensor<float>();
            var ab = new float[2 * Plane];
            if (output is DenseTensor<float> dense) dense.Buffer.Span.CopyTo(ab);
            else
            {
                var i = 0;
                foreach (var v in output) ab[i++] = v;
            }
            return ab;
        }
    }

    /// <summary>Loads the model on <paramref name="device"/> and checks it gives the right colours there.</summary>
    public static ColorModel Open(Device device, string path)
    {
        var clock = Stopwatch.StartNew();
        InferenceSession? session = null;
        try
        {
            using (var options = Hardware.Options(device)) session = new InferenceSession(path, options);
            var loadMs = clock.ElapsedMilliseconds;
            var model = new ColorModel(session, device);
            var (rms, ms) = model.Check();
            model.MillisecondsPerPicture = ms;
            Log.Info($"Model {Path.GetFileName(path)} on {device} [{device.Provider}]: loaded in {loadMs} ms, {ms:0} ms a picture, check difference {rms:0.000}");
            if (double.IsNaN(rms) || rms > 1.5)
                throw new InvalidDataException($"the {device.Kind} gives wrong colours with this model (check difference {rms:0.0})");
            return model;
        }
        catch
        {
            session?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs a known picture (Assets\check-input.bin) and compares the result, averaged over 8 x 8 blocks, with the
    /// 32-bit CPU result (Assets\check-expected.bin). A device or driver that computes something differently
    /// (as DirectML did with one of ONNX Runtime's fusions) is caught here and skipped. Returns the RMS difference
    /// and the time per picture.
    /// </summary>
    private (double Rms, double Ms) Check()
    {
        var gray = ReadResource("check-input.bin");
        var expectedBytes = ReadResource("check-expected.bin");
        var expected = new float[expectedBytes.Length / 4];
        Buffer.BlockCopy(expectedBytes, 0, expected, 0, expectedBytes.Length);

        var input = new float[3 * Plane];
        for (var i = 0; i < Plane; i++) input[i] = input[Plane + i] = input[2 * Plane + i] = gray[i] / 255f;

        var ab = Run(input);
        var clock = Stopwatch.StartNew();
        Run(input);
        var ms = clock.Elapsed.TotalMilliseconds;

        double sum = 0;
        for (var c = 0; c < 2; c++)
            for (var by = 0; by < 64; by++)
                for (var bx = 0; bx < 64; bx++)
                {
                    double mean = 0;
                    for (var y = 0; y < 8; y++)
                        for (var x = 0; x < 8; x++) mean += ab[c * Plane + (by * 8 + y) * ColorMath.Size + bx * 8 + x];
                    var d = mean / 64 - expected[(c * 64 + by) * 64 + bx];
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
