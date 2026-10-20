using System.Text.Json;
using System.Text.Json.Serialization;

namespace MakeItColor.Core;

public enum AppTheme { System, Light, Dark }

/// <summary>Which chip runs the model. Auto: the NPU, else the best graphics card, else the CPU.</summary>
public enum Processor { Auto, Npu, Gpu, Cpu }

/// <summary>Photo output format.</summary>
public enum PhotoFormat { Png, Jpeg }

/// <summary>Video output quality (H.264 bit rate relative to the picture size).</summary>
public enum VideoQuality { Standard, High, Best }

/// <summary>Size of enhanced photos. Auto: photos under 2000 pixels are made 2x or 4x larger, others keep their size.</summary>
public enum PhotoSize { Auto, Original, Double, Quadruple }

/// <summary>
/// Video clean-up. Full: the enhancer (AI) on every frame; Light: less grain and gentle sharpening, fast on any CPU;
/// Auto: Full when the enhancer runs on an NPU or a graphics card, Light on the CPU.
/// </summary>
public enum VideoEnhance { Auto, Full, Light, Off }

/// <summary>Settings in %APPDATA%\MakeItColor\settings.json.</summary>
public sealed class Settings
{
    [JsonConverter(typeof(JsonStringEnumConverter))] public AppTheme Theme { get; set; } = AppTheme.System;
    [JsonConverter(typeof(JsonStringEnumConverter))] public Processor Processor { get; set; } = Processor.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter))] public PhotoFormat PhotoFormat { get; set; } = PhotoFormat.Png;
    [JsonConverter(typeof(JsonStringEnumConverter))] public VideoQuality VideoQuality { get; set; } = VideoQuality.High;
    /// <summary>Video: keep colours steady from frame to frame where the picture doesn't change.</summary>
    public bool SteadyColors { get; set; } = true;
    /// <summary>Automatic brightness and contrast (photos and videos; in videos also takes out flicker).</summary>
    public bool FixTone { get; set; } = true;
    /// <summary>Photos: the enhancer takes out blur, noise and JPEG damage.</summary>
    public bool EnhancePhotos { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))] public PhotoSize PhotoSize { get; set; } = PhotoSize.Auto;
    [JsonConverter(typeof(JsonStringEnumConverter))] public VideoEnhance VideoEnhance { get; set; } = VideoEnhance.Auto;
    /// <summary>Colour strength, 0.5 to 1.5 (1 = as the model predicts).</summary>
    public double Vividness { get; set; } = 1.0;
    public string? LastFolder { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppInfo.RoamingDirectory, "settings.json");

    public static Settings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath)) Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Read settings");
            Current = new Settings();
        }
        Current.Vividness = Math.Clamp(Current.Vividness, 0.5, 1.5);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.RoamingDirectory);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Json));
            File.Move(temp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Save settings");
        }
    }
}
