using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MakeItColor.Core;

namespace MakeItColor.Views;

public enum ItemState { Waiting, Working, Done, Failed, Cancelled }

/// <summary>A photo or video in the list: its state, progress and the before / after previews.</summary>
public abstract class WorkItem : INotifyPropertyChanged
{
    private ItemState _state;
    private string _status = "Waiting";
    private double _progress;
    private BitmapSource? _thumbnail, _before, _after;

    protected WorkItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }
    public abstract bool IsVideo { get; }

    public ItemState State { get => _state; set { Set(ref _state, value); OnPropertyChanged(nameof(IsWorking)); OnPropertyChanged(nameof(IsDone)); } }
    public bool IsWorking => _state == ItemState.Working;
    public bool IsDone => _state == ItemState.Done;
    public string Status { get => _status; set => Set(ref _status, value); }
    /// <summary>0 to 1.</summary>
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public BitmapSource? Before { get => _before; set => Set(ref _before, value); }
    public BitmapSource? After { get => _after; set => Set(ref _after, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }
}

/// <summary>A photo: keeps the model's a/b output, so the colour strength can change without running the model again.</summary>
public sealed class PhotoItem : WorkItem
{
    public PhotoItem(string path) : base(path) { }

    public override bool IsVideo => false;

    /// <summary>The model's a/b output (2 x 512 x 512).</summary>
    internal float[]? Ab { get; set; }
    /// <summary>The original at screen size, for redoing the preview at another colour strength.</summary>
    internal Picture? PreviewSource { get; set; }
    public double Vividness { get; set; } = Settings.Current.Vividness;
    /// <summary>Enhance this photo (the Enhance switch; starts as the setting).</summary>
    public bool Enhance { get; set; } = Settings.Current.EnhancePhotos;
    /// <summary>The prepared full-size picture (brightness fixed, enhanced), kept on disk until it is saved.</summary>
    internal string? PreparedFile { get; set; }
    public string? SavedAs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A video: coloured straight to "name (color).mp4" next to the original.</summary>
public sealed class VideoItem : WorkItem
{
    public VideoItem(string path) : base(path) { }

    public override bool IsVideo => true;

    internal CancellationTokenSource Cancel { get; set; } = new();
    public string? OutputPath { get; set; }
}
