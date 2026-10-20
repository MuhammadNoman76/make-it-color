using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MakeItColor.Core;

/// <summary>One installer in the update feed.</summary>
public sealed class UpdateFile
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// The update feed (https://www.nomanshafiq.com/updates/makeitcolor.json):
/// <c>{ "app": "MakeItColor", "version": "1.0.1", "released": "...", "notes": [...], "page": "...",
///      "editions": { "x64": { "url": "...", "sha256": "...", "size": 123 }, "arm64": ... } }</c>
/// </summary>
public sealed class UpdateManifest
{
    public string App { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Released { get; set; }
    public List<string> Notes { get; set; } = new();
    public string? Page { get; set; }
    public Dictionary<string, UpdateFile> Editions { get; set; } = new();

    /// <summary>The installer for this copy of MakeItColor, if the feed has one.</summary>
    public UpdateFile? ForThisEdition =>
        Editions.FirstOrDefault(e => string.Equals(e.Key, Updater.Edition, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>How this copy of MakeItColor was installed (decides how its update installer is run).</summary>
public enum InstallScope { NotInstalled, CurrentUser, AllUsers }

/// <summary>Thrown when a downloaded installer doesn't match the size or SHA-256 the feed lists.</summary>
public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message) : base(message) { }
}

/// <summary>
/// Automatic updates: fetch a small JSON feed, compare versions, download this edition's installer,
/// check its size and SHA-256, then run it silently so it upgrades the app in place and starts it again.
/// The check only downloads the feed; nothing is sent beyond a normal HTTP request.
/// </summary>
public static class Updater
{
    public const string DefaultFeedUrl = AppInfo.UpdateFeedUrl;
    public const string FeedOverrideVariable = "MAKEITCOLOR_UPDATE_URL";

    /// <summary>The installer's AppId (installer\MakeItColor.iss) - Inno Setup keys the uninstall entry with it.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2359B1AF-53A7-4F1C-8168-65B4042B82F4}_is1";

    private const int MaxFeedBytes = 256 * 1024;
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Where downloaded installers go. Emptied on the next start.</summary>
    public static string DownloadDirectory => Path.Combine(Path.GetTempPath(), "MakeItColor-Update");

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>x64 or arm64: matches the feed's "editions" keys. Set by build.ps1 (AssemblyMetadata "Edition").</summary>
    public static string Edition => AppInfo.Edition;

    /// <summary>The feed, or the address/file in MAKEITCOLOR_UPDATE_URL (for testing).</summary>
    public static string FeedUrl
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(FeedOverrideVariable);
            return string.IsNullOrWhiteSpace(custom) ? DefaultFeedUrl : custom!.Trim();
        }
    }

    private static bool IsFeedOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FeedOverrideVariable));

    private static string UserAgent => AppInfo.Id + "/" + CurrentVersionText;

    // ------------------------------------------------------------------ versions

    /// <summary>1.0.2, 1.0.2.0 and 1.0.2 (from "v1.0.2") all compare equal.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text!.Trim().TrimStart('v', 'V');
        return System.Version.TryParse(trimmed, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>True when the feed offers a version newer than this one.</summary>
    public static bool IsNewer(UpdateManifest manifest) => IsNewer(manifest.Version, CurrentVersion);

    public static bool IsNewer(string? available, Version current) =>
        ParseVersion(available) is { } v && v > Normalize(current);

    // ------------------------------------------------------------------ feed

    /// <summary>Parses and checks the feed. Throws <see cref="FormatException"/> if it isn't a usable MakeItColor feed.</summary>
    public static UpdateManifest ParseManifest(string json)
    {
        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new FormatException("The update feed isn't valid JSON: " + ex.Message, ex);
        }

        if (manifest == null) throw new FormatException("The update feed is empty.");
        if (!string.Equals(manifest.App, AppInfo.Id, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The update feed isn't for MakeItColor.");
        if (ParseVersion(manifest.Version) == null) throw new FormatException("The update feed has no valid version.");

        manifest.Notes = (manifest.Notes ?? new()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        manifest.Editions = new Dictionary<string, UpdateFile>(manifest.Editions ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, file) in manifest.Editions.ToList())
        {
            if (file == null || !Uri.TryCreate(file.Url, UriKind.Absolute, out _) || file.Size <= 0 ||
                file.Sha256 == null || !IsSha256(file.Sha256))
            {
                manifest.Editions.Remove(key); // unusable entry: never offer it
                continue;
            }
            file.Sha256 = file.Sha256.Trim().ToLowerInvariant();
        }
        return manifest;
    }

    private static bool IsSha256(string text) =>
        text.Trim().Length == 64 && text.Trim().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>Downloads and parses the feed (or reads it from a file when MAKEITCOLOR_UPDATE_URL is a path).</summary>
    public static async Task<UpdateManifest> FetchManifestAsync(CancellationToken token = default)
    {
        var source = FeedUrl;
        string json;
        if (TryGetLocalPath(source, out var path))
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        else
        {
            using var client = CreateClient(FeedTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new FormatException("The update feed is too large.");
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            json = await ReadLimitedAsync(stream, MaxFeedBytes, token).ConfigureAwait(false);
        }
        return ParseManifest(json);
    }

    private static async Task<string> ReadLimitedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit) throw new FormatException("The update feed is too large.");
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryGetLocalPath(string source, out string path)
    {
        path = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                path = uri.LocalPath;
                return true;
            }
            return false;
        }
        return true; // a plain path
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
#if NETFRAMEWORK
        // .NET Framework on Windows 7 / 8.1 doesn't offer TLS 1.2 unless asked to; the website requires it.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true, // GitHub's /raw/ links redirect to raw.githubusercontent.com
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // ------------------------------------------------------------------ download

    /// <summary>
    /// Downloads the installer to %TEMP%\MakeItColor-Update and checks its size and SHA-256.
    /// A file that doesn't match is deleted and <see cref="UpdateVerificationException"/> is thrown.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, UpdateFile file, IProgress<double>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)) throw new FormatException("The installer address isn't valid.");
        // Only HTTPS for real updates; a test feed (MAKEITCOLOR_UPDATE_URL) may point at a local server.
        if (uri.Scheme != Uri.UriSchemeHttps && !(IsFeedOverridden && uri.Scheme == Uri.UriSchemeHttp))
            throw new FormatException("The installer must be downloaded over HTTPS.");

        Directory.CreateDirectory(DownloadDirectory);
        var name = SafeFileName(Path.GetFileName(uri.LocalPath));
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = $"MakeItColor-Setup-{manifest.Version}-{Edition}.exe";
        var target = Path.Combine(DownloadDirectory, name);
        var partial = target + ".part";

        // The overall time is unlimited (big file, slow line) but a stalled connection is given up after 30 s.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            using var client = CreateClient(System.Threading.Timeout.InfiniteTimeSpan);
            stall.CancelAfter(StallTimeout);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } announced && announced != file.Size)
                throw new UpdateVerificationException($"The installer on the server has the wrong size ({announced:N0} bytes, expected {file.Size:N0}).");

            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var abort = stall.Token.Register(() => source.Dispose()); // unblocks a read stuck on a dead connection
            using var sha = SHA256.Create();
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                var lastReport = -1.0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;

                    total += read;
                    if (total > file.Size)
                        throw new UpdateVerificationException("The downloaded installer is larger than the update feed says.");
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);

                    var fraction = (double)total / file.Size;
                    if (fraction - lastReport >= 0.005 || total == file.Size)
                    {
                        lastReport = fraction;
                        progress?.Report(fraction);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (total != file.Size)
                    throw new UpdateVerificationException($"The download is incomplete ({total:N0} of {file.Size:N0} bytes).");
                if (!string.Equals(ToHex(sha.Hash!), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateVerificationException("The downloaded installer doesn't match its SHA-256 checksum, so it wasn't run.");
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);

            // Re-check the file that will actually run (guards against anything touching it in between).
            if (!VerifyFile(target, file.Size, file.Sha256))
                throw new UpdateVerificationException("The downloaded installer doesn't match its SHA-256 checksum, so it wasn't run.");
            return target;
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            // Our stall timer fired (the read may surface as a cancel or as a disposed stream).
            if (stall.IsCancellationRequested) throw new TimeoutException("The download stopped responding.", ex);
            throw;
        }
    }

    /// <summary>True if the file has exactly this size and SHA-256. A file that doesn't match is deleted.</summary>
    public static bool VerifyFile(string path, long size, string sha256)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length == size)
            {
                using var stream = File.OpenRead(path);
                using var sha = SHA256.Create();
                if (string.Equals(ToHex(sha.ComputeHash(stream)), sha256.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Verify " + path);
        }
        TryDelete(path);
        return false;
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Error(ex, "Delete " + path); }
    }

    /// <summary>
    /// Removes installers left in %TEMP%\MakeItColor-Update by an earlier update. The installer that just
    /// updated us may still be finishing, so locked files are retried for a couple of minutes.
    /// </summary>
    public static async Task CleanUpDownloadsAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!Directory.Exists(DownloadDirectory)) return;
                var locked = false;
                foreach (var file in Directory.GetFiles(DownloadDirectory))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { locked = true; }
                }
                if (!locked)
                {
                    try { Directory.Delete(DownloadDirectory, false); } catch { /* not empty / in use */ }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Clean up update downloads");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ install

    /// <summary>The folder MakeItColor.exe runs from, without the trailing backslash.</summary>
    public static string AppDirectory => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Whether this copy was installed by the installer, for all users or just this user.</summary>
    public static InstallScope DetectInstallScope()
    {
        var dir = AppDirectory;
        try
        {
            if (InstalledAt(RegistryHive.CurrentUser, RegistryView.Default, dir)) return InstallScope.CurrentUser;
            if (InstalledAt(RegistryHive.LocalMachine, RegistryView.Registry64, dir) ||
                InstalledAt(RegistryHive.LocalMachine, RegistryView.Registry32, dir))
                return InstallScope.AllUsers;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Detect install scope");
        }

        // No (readable) uninstall entry: go by the folder.
        var userPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (IsUnder(dir, userPrograms)) return InstallScope.CurrentUser;
        foreach (var pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            if (!string.IsNullOrEmpty(pf) && IsUnder(dir, pf!)) return InstallScope.AllUsers;
        return InstallScope.NotInstalled;
    }

    private static bool InstalledAt(RegistryHive hive, RegistryView view, string dir)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(UninstallKey);
        var location = key?.GetValue("InstallLocation") as string;
        return !string.IsNullOrWhiteSpace(location) && SamePath(location!, dir);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnder(string dir, string root) =>
        (dir.TrimEnd('\\') + "\\").StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>The installer's command line: silent, close MakeItColor, install over this copy, start it again afterwards.</summary>
    public static string InstallerArguments(InstallScope scope) =>
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 " +
        (scope == InstallScope.CurrentUser ? "/CURRENTUSER" : "/ALLUSERS") +
        $" /DIR=\"{AppDirectory}\"";

    /// <summary>
    /// Starts the verified installer. A per-machine install asks for administrator rights (UAC).
    /// The caller must exit right after, so the installer can replace the files.
    /// </summary>
    public static void LaunchInstaller(string installer, InstallScope scope)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installer,
            Arguments = InstallerArguments(scope),
            WorkingDirectory = Path.GetDirectoryName(installer) ?? DownloadDirectory,
            UseShellExecute = true,
        })?.Dispose();
    }
}
