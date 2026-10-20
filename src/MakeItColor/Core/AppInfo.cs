using System.Reflection;

namespace MakeItColor.Core;

/// <summary>Names, folders and addresses in one place.</summary>
internal static class AppInfo
{
    public const string Name = "MakeItColor";
    public const string Id = "MakeItColor";

    public const string Website = "https://www.nomanshafiq.com/makeitcolor/";
    public const string UpdateFeedUrl = "https://www.nomanshafiq.com/updates/makeitcolor.json";
    public const string Repository = "https://github.com/MuhammadNoman76/make-it-color";

    /// <summary>Settings and logs: %APPDATA%\MakeItColor.</summary>
    public static string RoamingDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Id);

    /// <summary>Models and the NPU compile cache: %LOCALAPPDATA%\MakeItColor (large, machine-specific).</summary>
    public static string LocalDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);

    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    /// <summary>x64 or arm64 (set by the build).</summary>
    public static string Edition { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "Edition")?.Value
        ?? (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
}
