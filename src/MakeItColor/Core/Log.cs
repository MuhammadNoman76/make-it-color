namespace MakeItColor.Core;

/// <summary>Writes to %APPDATA%\MakeItColor\info.log and error.log (never throws).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("info.log", message);

    public static void Error(Exception? ex, string? context = null)
    {
        if (ex == null) return;
        Write("error.log", $"{context}{Environment.NewLine}{ex}");
    }

    private static void Write(string name, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.RoamingDirectory);
                var file = Path.Combine(AppInfo.RoamingDirectory, name);
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
