using System.Net.Http;
using System.Security.Cryptography;

namespace MakeItColor.Core.Ai;

/// <summary>A model file the app downloads on first use (never shipped in the installer).</summary>
internal sealed record ModelFile(string Name, string FileName, string Url, long Size, string Sha256);

/// <summary>
/// The colorization model (DDColor, ConvNeXt-L, 512x512) in two precisions, converted by tools\model\convert.py from
/// https://huggingface.co/muhammadnoman76/makeitcolor and published with the app's GitHub release. Downloaded to
/// %LOCALAPPDATA%\MakeItColor\models (or found next to the app in "models\") and checked by size and SHA-256.
/// </summary>
internal static class ModelStore
{
    /// <summary>For testing: a different models folder.</summary>
    public const string DirectoryOverrideVariable = "MAKEITCOLOR_MODELS";

    private const string Release = "https://github.com/MuhammadNoman76/make-it-color/releases/download/models-1/";

    /// <summary>16-bit floats: for NPUs and graphics cards.</summary>
    public static readonly ModelFile Half = new("colour model", "makeitcolor-fp16.onnx", Release + "makeitcolor-fp16.onnx",
        456_392_880, "5b6f2f0a572754d157baee1a856de071eda913bb6bb56d8bb7e4018ef1530b2a");

    /// <summary>32-bit floats: for the CPU (exact, and faster there than 16-bit).</summary>
    public static readonly ModelFile Full = new("colour model for the CPU", "makeitcolor-fp32.onnx", Release + "makeitcolor-fp32.onnx",
        912_062_353, "db1bec244096683c4b1896f504d56b6dc8775e0891d9d084416d5e7d70522ac1");

    public static ModelFile For(Device device) => device.IsCpu ? Full : Half;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    public static string Directory =>
        Environment.GetEnvironmentVariable(DirectoryOverrideVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppInfo.LocalDirectory, "models");

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Id}/{AppInfo.VersionText}");
        return http;
    }

    /// <summary>True when the file is already here (no download needed).</summary>
    public static bool IsAvailable(ModelFile model) =>
        IsValid(Path.Combine(AppContext.BaseDirectory, "models", model.FileName), model) || IsValid(Path.Combine(Directory, model.FileName), model);

    /// <summary>The model's path, downloading and verifying it first if needed.</summary>
    public static async Task<string> EnsureAsync(ModelFile model, IProgress<string>? status, CancellationToken ct)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "models", model.FileName);
        if (IsValid(bundled, model)) return bundled;

        var path = Path.Combine(Directory, model.FileName);
        if (IsValid(path, model)) return path;

        await DownloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsValid(path, model)) return path;
            System.IO.Directory.CreateDirectory(Directory);
            var download = path + ".download";
            try
            {
                await DownloadAsync(model, download, status, ct).ConfigureAwait(false);
                File.Move(download, path, true);
                MarkVerified(path, model);
            }
            finally
            {
                TryDelete(download);
            }
            return path;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private static async Task DownloadAsync(ModelFile model, string target, IProgress<string>? status, CancellationToken ct)
    {
        var sizeMb = model.Size / 1_000_000.0;
        status?.Report($"Downloading the {model.Name}: 0% of {sizeMb:0} MB (first time only)");

        HttpResponseMessage response;
        try
        {
            response = await Http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"The {model.Name} couldn't be downloaded: no connection to github.com ({ex.Message}). Check the internet connection and press Try again.", ex);
        }
        using var _ = response;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The {model.Name} couldn't be downloaded (github.com answered {(int)response.StatusCode} {response.ReasonPhrase}). Press Try again in a while; if it keeps failing, get the latest MakeItColor from nomanshafiq.com/makeitcolor.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long total = 0;
            var lastPercent = -1;
            while (true)
            {
                // A stalled connection gives up after a minute instead of hanging forever.
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                total += read;
                if (total > model.Size) throw new InvalidDataException($"The {model.Name} download is larger than expected.");
                var percent = (int)(total * 100 / model.Size);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    status?.Report($"Downloading the {model.Name}: {percent}% of {sizeMb:0} MB (first time only)");
                }
            }
            if (total != model.Size) throw new InvalidDataException($"The {model.Name} download is incomplete. Check the internet connection and try again.");
        }
        if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {model.Name} download didn't pass the SHA-256 check.");
    }

    /// <summary>Right size, and verified once (a small marker remembers it, so 456 MB isn't hashed on every start).</summary>
    private static bool IsValid(string path, ModelFile model)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != model.Size) return false;
            var marker = path + ".verified";
            var expected = $"{model.Sha256}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (File.Exists(marker) && File.ReadAllText(marker) == expected) return true;
            if (!HashMatches(path, model.Sha256)) return false;
            MarkVerified(path, model);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void MarkVerified(string path, ModelFile model)
    {
        try
        {
            var info = new FileInfo(path);
            File.WriteAllText(path + ".verified", $"{model.Sha256}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        }
        catch
        {
            // a read-only folder is simply checked again next time
        }
    }

    private static bool HashMatches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return string.Equals(Convert.ToHexString(sha.ComputeHash(stream)), sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
