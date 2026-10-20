using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace MakeItColor.Core;

/// <summary>
/// Automatic updates (same scheme as VibePlayer and PawSip): a quiet check about 10 s after start-up and
/// then at most once a day, an offer in the main window when a newer version is out, and "Update now" =
/// download, verify size and SHA-256, run the installer silently, restart. Failed checks stay silent.
/// </summary>
internal static class UpdateService
{
    public const string DownloadPageUrl = AppInfo.Website;
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private static DispatcherTimer? s_timer;
    private static bool s_checking;
    private static CancellationTokenSource? s_download;

    /// <summary>A newer version on offer (null if none, or not checked yet).</summary>
    public static UpdateManifest? Available { get; private set; }

    /// <summary>The user asked for it (Check now): show it even if that version was skipped.</summary>
    public static bool AskedFor { get; private set; }

    public static bool IsDownloading => s_download != null;

    /// <summary>Raised on the UI thread when <see cref="Available"/> changes.</summary>
    public static event Action? Changed;

    public static void Start()
    {
        if (s_timer != null) return;
        s_timer = new DispatcherTimer { Interval = FirstCheckDelay };
        s_timer.Tick += (_, _) =>
        {
            s_timer.Interval = TimeSpan.FromHours(1);
            if (Settings.Current.CheckForUpdates) _ = CheckAsync(manual: false);
        };
        s_timer.Start();
        _ = Task.Run(Updater.CleanUpDownloadsAsync);
    }

    /// <summary>Checks the feed. Returns a sentence for the Settings page (automatic checks only speak up when there's news).</summary>
    public static async Task<string> CheckAsync(bool manual)
    {
        if (s_checking) return "Already checking…";
        if (!manual)
        {
            var last = Settings.Current.LastUpdateCheck;
            var now = DateTime.UtcNow;
            if (last is { } at && at <= now && now - at < CheckInterval) return "";
        }

        s_checking = true;
        try
        {
            UpdateManifest manifest;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                manifest = await Updater.FetchManifestAsync(cts.Token);
            Settings.Current.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save();

            if (!Updater.IsNewer(manifest))
            {
                if (Available != null)
                {
                    Available = null;
                    Changed?.Invoke();
                }
                return $"You're up to date (MakeItColor {Updater.CurrentVersionText}).";
            }

            var skipped = Updater.ParseVersion(Settings.Current.SkippedVersion) is { } s && s == Updater.ParseVersion(manifest.Version);
            if (skipped && !manual) return "";
            Available = manifest;
            AskedFor = manual;
            Changed?.Invoke();
            return $"MakeItColor {manifest.Version} is available.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check (" + Updater.FeedUrl + ")");
            return manual ? "Couldn't check for updates. Check your internet connection and try again." : "";
        }
        finally
        {
            s_checking = false;
        }
    }

    public static void Skip()
    {
        if (Available == null) return;
        var version = Available.Version;
        Settings.Current.SkippedVersion = version;
        Settings.Save();
        Available = null;
        Changed?.Invoke();
    }

    public static void Later()
    {
        Available = null;
        Changed?.Invoke();
    }

    /// <summary>The installer can only update a copy that it installed (and only with an installer for this edition).</summary>
    public static bool CanInstall(UpdateManifest manifest) =>
        manifest.ForThisEdition != null && Updater.DetectInstallScope() != InstallScope.NotInstalled;

    public static void CancelDownload() => s_download?.Cancel();

    /// <summary>Downloads, verifies and starts the installer, then closes MakeItColor. Throws on failure.</summary>
    public static async Task UpdateNowAsync(IProgress<double> progress)
    {
        var manifest = Available ?? throw new InvalidOperationException("No update on offer.");
        var file = manifest.ForThisEdition;
        var scope = Updater.DetectInstallScope();
        if (file == null || scope == InstallScope.NotInstalled)
        {
            OpenInBrowser(string.IsNullOrWhiteSpace(manifest.Page) ? DownloadPageUrl : manifest.Page!);
            return;
        }

        s_download = new CancellationTokenSource();
        try
        {
            var installer = await Updater.DownloadAsync(manifest, file, progress, s_download.Token);
            Updater.LaunchInstaller(installer, scope);
            // Close so the installer can replace our files (it starts MakeItColor again when it's done).
            await Task.Delay(400);
            Application.Current.Shutdown();
        }
        finally
        {
            s_download.Dispose();
            s_download = null;
        }
    }

    public static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Open " + url);
        }
    }

    public static string EditionName(string edition) => edition switch
    {
        "x64" => "Windows 10/11, 64-bit",
        "arm64" => "Windows 10/11 on ARM",
        "x86" => "Windows 10, 32-bit",
        "win7-8" => "Windows 7/8.1",
        _ => edition,
    };
}
